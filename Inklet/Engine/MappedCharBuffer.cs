using System;
using System.Collections.Generic;

namespace Inklet.Engine;

/// <summary>
/// Presents the original file's INDEXED PREFIX as an <see cref="ICharBuffer"/>
/// in UTF-16 char space, decoding bytes on demand through an LRU chunk cache.
/// All queries must stay below <see cref="Length"/> (the index frontier), which
/// the owning Document guarantees by only absorbing indexed segments into the
/// piece tree; the single exception is a one-unit peek AT the frontier used for
/// split-CRLF break correction, served straight from the raw bytes.
/// </summary>
internal sealed class MappedCharBuffer : ICharBuffer
{
    private const int ChunkBytes = 128 * 1024;

    private readonly IByteSource _source;
    private readonly TextCodec _codec;
    private readonly OriginalIndex _index;
    private readonly long _contentOrigin;

    private sealed class Chunk
    {
        public required char[] Chars;
        public required long UnitStart;   // char-space offset of Chars[0]
    }

    private readonly object _cacheLock = new();
    private readonly Dictionary<long, LinkedListNode<(long Key, Chunk Chunk)>> _chunkMap = [];
    private readonly LinkedList<(long Key, Chunk Chunk)> _chunkLru = [];
    private long _cacheChars;
    private const long CacheBudgetChars = 32 * 1024 * 1024; // 64 MB of decoded chars

    public MappedCharBuffer(IByteSource source, TextCodec codec, OriginalIndex index)
    {
        _source = source;
        _codec = codec;
        _index = index;
        _contentOrigin = codec.PreambleLength;
    }

    /// <summary>Exact chars available so far (grows as the indexer advances).</summary>
    public long Length => _index.IndexedUnits;

    public char this[long unit]
    {
        get
        {
            if (unit == Length) return PeekUnitAtFrontier();
            var (chunk, idx) = ResolveChunk(unit);
            return chunk.Chars[idx];
        }
    }

    public void CopyTo(long offset, int count, Span<char> destination)
    {
        int copied = 0;
        while (copied < count)
        {
            var (chunk, idx) = ResolveChunk(offset + copied);
            int take = Math.Min(count - copied, chunk.Chars.Length - (int)idx);
            chunk.Chars.AsSpan((int)idx, take).CopyTo(destination.Slice(copied));
            copied += take;
        }
    }

    public long CountBreakEndsInRange(long startExclusive, long endInclusive)
        => BreaksUpTo(endInclusive) - BreaksUpTo(startExclusive);

    /// <summary>Breaks with end unit &lt;= unit (unit may equal Length).</summary>
    private long BreaksUpTo(long unit)
    {
        if (unit <= 0) return 0;
        if (unit >= Length) return _index.IndexedBreaks;
        int seg = _index.FindSegmentByUnit(unit);
        long segUnitsBefore = _index.UnitsBeforeSegment(seg);
        var detail = _index.GetDetail(seg);
        long local = unit - segUnitsBefore;
        // upper bound over the segment-local break-end list
        int lo = 0, hi = detail.BreakEndUnits.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (detail.BreakEndUnits[mid] <= local) lo = mid + 1; else hi = mid;
        }
        return _index.BreaksBeforeSegment(seg) + lo;
    }

    public long GetBreakEndAfter(long startExclusive, long k)
    {
        long target = BreaksUpTo(startExclusive) + k;   // 0-based global break index
        int seg = _index.FindSegmentByBreak(target);
        var detail = _index.GetDetail(seg);
        long local = target - _index.BreaksBeforeSegment(seg);
        return _index.UnitsBeforeSegment(seg) + detail.BreakEndUnits[local];
    }

    // ── Mapping and decoding ─────────────────────────────────────────────────

    private (Chunk Chunk, long Index) ResolveChunk(long unit)
    {
        if ((ulong)unit >= (ulong)Length)
            throw new ArgumentOutOfRangeException(nameof(unit), $"unit {unit} beyond indexed frontier {Length}");
        long byteAt = UnitToContentByte(unit, out _);
        long key = byteAt / ChunkBytes;
        var chunk = GetOrDecodeChunk(key);
        long idx = unit - chunk.UnitStart;
        if (idx < 0 || idx >= chunk.Chars.Length)
            throw new InvalidOperationException($"chunk mapping inconsistency at unit {unit}");
        return (chunk, idx);
    }

    /// <summary>
    /// Content-byte offset (0-based, preamble excluded) of the char covering
    /// `unit`; for a low surrogate this is its pair's sequence start.
    /// </summary>
    private long UnitToContentByte(long unit, out long unitAtByte)
    {
        switch (_codec.Class)
        {
            case CodecClass.SingleByte:
                unitAtByte = unit;
                return unit;
            case CodecClass.Utf16LE:
            case CodecClass.Utf16BE:
                unitAtByte = unit;
                return unit * 2;
            case CodecClass.Utf8:
            {
                int seg = _index.FindSegmentByUnit(unit);
                long segUnits = _index.UnitsBeforeSegment(seg);
                var detail = _index.GetDetail(seg);
                long local = unit - segUnits;
                // Sample grid: SampleUnitCum[i] = units before byte i*SampleBytes.
                int lo = 0, hi = detail.SampleUnitCum.Length - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (detail.SampleUnitCum[mid] <= local) lo = mid; else hi = mid - 1;
                }
                long sampleByte = (long)lo * SegmentDetail.SampleBytes;
                long segStart = (long)seg * _index.SegmentBytes;
                int segLen = _index.SegmentLengthBytes(seg);
                // The sample byte can sit mid-sequence; skip continuation bytes first.
                var window = _source.GetSpan(_contentOrigin + segStart + sampleByte,
                    (int)Math.Min(segLen - sampleByte + 8, _index.ContentLength - segStart - sampleByte));
                int adj = 0;
                while (adj < window.Length && (window[adj] & 0xC0) == 0x80) adj++;
                long unitsToSkip = local - detail.SampleUnitCum[lo];
                int advance = _codec.AdvanceUtf8Units(window[adj..], unitsToSkip, out long skipped);
                unitAtByte = segUnits + detail.SampleUnitCum[lo] + skipped;
                return segStart + sampleByte + adj + advance;
            }
            default:
                throw new InvalidOperationException("Unstreamable codec has no byte mapping.");
        }
    }

    private Chunk GetOrDecodeChunk(long key)
    {
        lock (_cacheLock)
        {
            if (_chunkMap.TryGetValue(key, out var node))
            {
                _chunkLru.Remove(node);
                _chunkLru.AddFirst(node);
                return node.Value.Chunk;
            }
        }
        var chunk = DecodeChunk(key);
        lock (_cacheLock)
        {
            if (_chunkMap.TryGetValue(key, out var raced)) return raced.Value.Chunk;
            var node = _chunkLru.AddFirst((key, chunk));
            _chunkMap[key] = node;
            _cacheChars += chunk.Chars.Length;
            while (_cacheChars > CacheBudgetChars && _chunkLru.Last is { } last)
            {
                _chunkLru.RemoveLast();
                _chunkMap.Remove(last.Value.Key);
                _cacheChars -= last.Value.Chunk.Chars.Length;
            }
        }
        return chunk;
    }

    /// <summary>
    /// Decodes the chars whose encoded sequences START within content bytes
    /// [key*ChunkBytes, (key+1)*ChunkBytes), reading past the end to complete a
    /// straddling final sequence.
    /// </summary>
    private Chunk DecodeChunk(long key)
    {
        long start = key * ChunkBytes;
        long endExcl = Math.Min(start + ChunkBytes, _index.ContentLength);

        long unitStart;
        long adjStart = start;
        if (_codec.Class == CodecClass.Utf8)
        {
            // Skip continuation bytes at the chunk start (owned by the previous chunk).
            var head = _source.GetSpan(_contentOrigin + start, (int)Math.Min(8, _index.ContentLength - start));
            int adj = 0;
            while (adj < head.Length && (head[adj] & 0xC0) == 0x80) adj++;
            adjStart = start + adj;
            unitStart = UnitsBeforeContentByte(adjStart);
        }
        else
        {
            unitStart = _codec.Class == CodecClass.SingleByte ? start : start / 2;
        }

        // Extend past the chunk end to finish a straddling sequence (UTF-8 only).
        long readEnd = endExcl;
        if (_codec.Class == CodecClass.Utf8 && readEnd < _index.ContentLength)
            readEnd = Math.Min(readEnd + 4, _index.ContentLength);

        var bytes = _source.GetSpan(_contentOrigin + adjStart, (int)(readEnd - adjStart));
        // Trim to sequences starting before endExcl: from endExcl, back up over the
        // continuation bytes into their lead; if that lead starts >= endExcl, drop it.
        if (_codec.Class == CodecClass.Utf8)
        {
            int cut = (int)(endExcl - adjStart);
            if (cut < bytes.Length)
            {
                // Find the last sequence start < endExcl and include its full extent.
                int lead = cut;
                while (lead > 0 && (bytes[lead - 1] & 0xC0) == 0x80) lead--;
                if (lead > 0)
                {
                    byte lb = bytes[lead - 1];
                    int expected = lb >= 0xF0 ? 4 : lb >= 0xE0 ? 3 : lb >= 0xC0 ? 2 : 1;
                    int available = bytes.Length - (lead - 1);
                    int take = Math.Min(expected, available);
                    bytes = bytes[..(lead - 1 + take)];
                }
                else
                {
                    bytes = bytes[..cut];
                }
            }
        }

        bool isUtf16 = _codec.Class is CodecClass.Utf16LE or CodecClass.Utf16BE;
        var chars = new char[isUtf16 ? bytes.Length / 2 + 1 : bytes.Length];
        int written = _codec.DecodeRange(bytes, chars, out int consumed);
        // A malformed odd-length UTF-16 file: the scanner counted its lone trailing
        // byte as one unit; decode it as the replacement char to stay consistent.
        if (isUtf16 && readEnd == _index.ContentLength && consumed < bytes.Length)
            chars[written++] = '�';
        if (written != chars.Length) Array.Resize(ref chars, written);
        return new Chunk { Chars = chars, UnitStart = unitStart };
    }

    /// <summary>Units of all chars starting before this content byte (UTF-8 path).</summary>
    private long UnitsBeforeContentByte(long contentByte)
    {
        int seg = (int)(contentByte / _index.SegmentBytes);
        if (seg >= _index.CompletedSegments) seg = _index.CompletedSegments - 1;
        long segStart = (long)seg * _index.SegmentBytes;
        var detail = _index.GetDetail(seg);
        long localByte = contentByte - segStart;
        int sample = (int)Math.Min(localByte / SegmentDetail.SampleBytes, detail.SampleUnitCum.Length - 1);
        long sampleByte = (long)sample * SegmentDetail.SampleBytes;
        int windowLen = (int)Math.Min(localByte - sampleByte,
            _index.ContentLength - segStart - sampleByte);
        var window = _source.GetSpan(_contentOrigin + segStart + sampleByte, windowLen);
        long units = detail.SampleUnitCum[sample];
        for (int i = 0; i < window.Length; i++)
        {
            byte b = window[i];
            if ((b & 0xC0) != 0x80)
            {
                units++;
                if (b >= 0xF0) units++;
            }
        }
        return _index.UnitsBeforeSegment(seg) + units;
    }

    /// <summary>Reads the single char AT the frontier (split-CRLF correction peek).</summary>
    private char PeekUnitAtFrontier()
    {
        long byteFrontier = _index.IndexedBytes;
        if (byteFrontier >= _index.ContentLength)
            throw new ArgumentOutOfRangeException(nameof(byteFrontier), "peek beyond end of content");
        switch (_codec.Class)
        {
            case CodecClass.Utf8:
            case CodecClass.SingleByte:
            {
                byte b = _source.GetSpan(_contentOrigin + byteFrontier, 1)[0];
                // Only the LF question matters for the peek; any multi-byte char is 'not LF'.
                return b < 0x80 ? (char)b : '�';
            }
            case CodecClass.Utf16LE:
            {
                var s = _source.GetSpan(_contentOrigin + byteFrontier, 2);
                return (char)(s[0] | (s[1] << 8));
            }
            case CodecClass.Utf16BE:
            {
                var s = _source.GetSpan(_contentOrigin + byteFrontier, 2);
                return (char)((s[0] << 8) | s[1]);
            }
            default:
                throw new InvalidOperationException();
        }
    }
}
