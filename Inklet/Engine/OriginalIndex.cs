using System;
using System.Collections.Generic;
using System.Threading;

namespace Inklet.Engine;

/// <summary>
/// Two-tier line/char index over the original file bytes (preamble excluded).
///
/// Tier 1 (built once, sequentially, by the background indexer): per-segment
/// cumulative UTF-16 unit and line-break counts. ~16 bytes per 1 MB segment, so
/// a 20 GB file costs ~320 KB. The single-writer publishes progress through the
/// volatile <see cref="CompletedSegments"/> frontier: everything below it is
/// immutable and exact, so readers need no locks.
///
/// Tier 2 (on demand, LRU): per-segment break-end unit lists and per-4KB char
/// samples, rebuilt by re-scanning that one segment's bytes at memchr speed.
/// </summary>
internal sealed class OriginalIndex
{
    public const int DefaultSegmentBytes = 1 << 20;

    private readonly IByteSource _source;
    private readonly TextCodec _codec;
    private readonly long _contentOrigin;   // preamble length
    private readonly long _contentLength;
    private readonly long[] _cumUnits;      // [k] = units in segments 0..k
    private readonly long[] _cumBreaks;
    private int _completedSegments;         // volatile frontier

    private readonly object _detailLock = new();
    private readonly Dictionary<int, LinkedListNode<(int Seg, SegmentDetail Detail, long Bytes)>> _detailMap = [];
    private readonly LinkedList<(int Seg, SegmentDetail Detail, long Bytes)> _detailLru = [];
    private long _detailBytes;
    private const long DetailBudgetBytes = 16 * 1024 * 1024;

    public int SegmentBytes { get; }
    public int SegmentCount { get; }
    public long ContentLength => _contentLength;

    // Exact EOL statistics, complete once indexing finishes.
    public long CrLfCount;
    public long LoneLfCount;
    public long LoneCrCount;

    public OriginalIndex(IByteSource source, TextCodec codec, int segmentBytes = DefaultSegmentBytes)
    {
        if (segmentBytes % SegmentDetail.SampleBytes != 0 || segmentBytes % 2 != 0)
            throw new ArgumentException("segment size must be even and a multiple of the sample grid");
        _source = source;
        _codec = codec;
        _contentOrigin = codec.PreambleLength;
        _contentLength = source.Length - _contentOrigin;
        SegmentBytes = segmentBytes;
        SegmentCount = (int)Math.Max(0, (_contentLength + segmentBytes - 1) / segmentBytes);
        _cumUnits = new long[SegmentCount];
        _cumBreaks = new long[SegmentCount];
    }

    public int CompletedSegments => Volatile.Read(ref _completedSegments);
    public bool IsComplete => CompletedSegments >= SegmentCount;

    /// <summary>Exact units below the frontier.</summary>
    public long IndexedUnits => CompletedSegments == 0 ? 0 : _cumUnits[CompletedSegments - 1];

    /// <summary>Exact breaks below the frontier.</summary>
    public long IndexedBreaks => CompletedSegments == 0 ? 0 : _cumBreaks[CompletedSegments - 1];

    /// <summary>Content bytes below the frontier.</summary>
    public long IndexedBytes => Math.Min((long)CompletedSegments * SegmentBytes, _contentLength);

    public long UnitsBeforeSegment(int seg) => seg == 0 ? 0 : _cumUnits[seg - 1];
    public long BreaksBeforeSegment(int seg) => seg == 0 ? 0 : _cumBreaks[seg - 1];
    public long SegmentStartByte(int seg) => _contentOrigin + (long)seg * SegmentBytes;
    public int SegmentLengthBytes(int seg)
        => (int)Math.Min(SegmentBytes, _contentLength - (long)seg * SegmentBytes);

    /// <summary>Scans one segment (single writer: the indexer thread or the open path).</summary>
    public SegmentScan ScanNextSegment(ref ScanCarry carry)
    {
        int seg = _completedSegments;
        if (seg >= SegmentCount) throw new InvalidOperationException("already complete");
        var bytes = _source.GetSpan(SegmentStartByte(seg), SegmentLengthBytes(seg));
        var scan = _codec.ScanSegment(bytes, ref carry, isFinal: seg == SegmentCount - 1);
        _cumUnits[seg] = UnitsBeforeSegment(seg) + scan.Utf16Units;
        _cumBreaks[seg] = BreaksBeforeSegment(seg) + scan.BreakEnds;
        CrLfCount += scan.CrLf;
        LoneLfCount += scan.LoneLf;
        LoneCrCount += scan.LoneCr;
        Volatile.Write(ref _completedSegments, seg + 1);
        return scan;
    }

    /// <summary>Segment containing the char that starts at (or covers) this unit; unit &lt; IndexedUnits.</summary>
    public int FindSegmentByUnit(long unit)
    {
        int lo = 0, hi = CompletedSegments - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cumUnits[mid] <= unit) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Segment owning the k-th (0-based) break end; k &lt; IndexedBreaks.</summary>
    public int FindSegmentByBreak(long k)
    {
        int lo = 0, hi = CompletedSegments - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cumBreaks[mid] <= k) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Tier-2 detail for a completed segment (LRU-cached rescan).</summary>
    public SegmentDetail GetDetail(int seg)
    {
        lock (_detailLock)
        {
            if (_detailMap.TryGetValue(seg, out var node))
            {
                _detailLru.Remove(node);
                _detailLru.AddFirst(node);
                return node.Value.Detail;
            }
        }

        // Rescan outside the lock (multiple threads may redundantly rescan; harmless).
        var detail = RescanSegment(seg);
        long cost = detail.BreakEndUnits.Length * 8L + detail.SampleUnitCum.Length * 4L + 64;
        lock (_detailLock)
        {
            if (_detailMap.TryGetValue(seg, out var raced)) return raced.Value.Detail;
            var node = _detailLru.AddFirst((seg, detail, cost));
            _detailMap[seg] = node;
            _detailBytes += cost;
            while (_detailBytes > DetailBudgetBytes && _detailLru.Last is { } last)
            {
                _detailLru.RemoveLast();
                _detailMap.Remove(last.Value.Seg);
                _detailBytes -= last.Value.Bytes;
            }
        }
        return detail;
    }

    private SegmentDetail RescanSegment(int seg)
    {
        if (seg >= CompletedSegments) throw new InvalidOperationException("segment not indexed yet");
        var carry = new ScanCarry { PrevEndsWithCr = SegmentEndsWithCr(seg - 1) };
        var breakEnds = new List<long>();
        var samples = new List<int>();
        var bytes = _source.GetSpan(SegmentStartByte(seg), SegmentLengthBytes(seg));
        _codec.ScanSegment(bytes, ref carry, isFinal: seg == SegmentCount - 1, breakEnds, samples);
        return new SegmentDetail
        {
            BreakEndUnits = breakEnds.ToArray(),
            SampleUnitCum = samples.ToArray(),
        };
    }

    /// <summary>Whether a segment's last content unit is a CR (carry-in for rescans).</summary>
    private bool SegmentEndsWithCr(int seg)
    {
        if (seg < 0) return false;
        long endByte = SegmentStartByte(seg) + SegmentLengthBytes(seg);
        switch (_codec.Class)
        {
            case CodecClass.Utf8:
            case CodecClass.SingleByte:
                return _source.GetSpan(endByte - 1, 1)[0] == (byte)'\r';
            case CodecClass.Utf16LE:
            {
                var b = _source.GetSpan(endByte - 2, 2);
                return (ushort)(b[0] | (b[1] << 8)) == '\r';
            }
            case CodecClass.Utf16BE:
            {
                var b = _source.GetSpan(endByte - 2, 2);
                return (ushort)((b[0] << 8) | b[1]) == '\r';
            }
            default:
                return false;
        }
    }
}
