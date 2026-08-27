using System;
using System.Collections.Generic;

namespace Inklet.Engine;

/// <summary>
/// Identifies which backing store a <see cref="Piece"/> references.
/// </summary>
internal enum PieceBufferKind : byte
{
    Original,
    Add,
}

/// <summary>
/// A read-only character store that also knows where its line breaks end.
///
/// Break convention (native offsets, CRLF = 2 chars): a "break end" is the char
/// offset immediately AFTER a line terminator, where CRLF counts as one break
/// ending after the LF, and a lone CR or lone LF each count as one break. The
/// break-end list is strictly ascending; because both implementations are
/// append-only/immutable it can be binary-searched without locking.
/// </summary>
internal interface ICharBuffer
{
    long Length { get; }
    char this[long index] { get; }
    void CopyTo(long offset, int count, Span<char> destination);
    /// <summary>Number of break ends e with startExclusive &lt; e &lt;= endInclusive.</summary>
    long CountBreakEndsInRange(long startExclusive, long endInclusive);
    /// <summary>The k-th (0-based) break end at offset &gt; startExclusive.</summary>
    long GetBreakEndAfter(long startExclusive, long k);
}

/// <summary>
/// Immutable original-content buffer over a single string (the Phase-1 in-memory
/// stand-in for the future memory-mapped byte source). The break-end index is
/// built once at construction with an O(N) scan.
/// </summary>
internal sealed class OriginalCharBuffer : ICharBuffer
{
    private readonly string _text;
    private readonly long[] _breakEnds;

    public OriginalCharBuffer(string text)
    {
        _text = text;
        var breaks = new List<long>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                breaks.Add(i + 1);
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    breaks.Add(i + 2);
                    i++; // consume the LF half of the CRLF
                }
                else
                {
                    breaks.Add(i + 1);
                }
            }
        }
        _breakEnds = breaks.ToArray();
    }

    public long Length => _text.Length;

    public char this[long index] => _text[(int)index];

    public void CopyTo(long offset, int count, Span<char> destination)
        => _text.AsSpan((int)offset, count).CopyTo(destination);

    public long CountBreakEndsInRange(long startExclusive, long endInclusive)
        => BreakSearch.CountInRange(_breakEnds, startExclusive, endInclusive);

    public long GetBreakEndAfter(long startExclusive, long k)
        => BreakSearch.GetAfter(_breakEnds, startExclusive, k);
}

/// <summary>
/// Append-only add buffer: chunked char blocks (no LOH) plus a single ascending
/// break-end list. Nothing is ever removed - deleted/undone text stays reachable,
/// which is what makes reference-based undo sound.
/// </summary>
internal sealed class AddBuffer : ICharBuffer
{
    private const int BlockShift = 16;                 // 64 Ki chars = 128 KB per block
    private const int BlockSize = 1 << BlockShift;
    private const int BlockMask = BlockSize - 1;

    private readonly List<char[]> _blocks = [];
    private readonly List<long> _breakEnds = [];
    private long _length;
    private char _lastChar;                            // for CRLF spotting across appends

    public long Length => _length;

    public char this[long index] => _blocks[(int)(index >> BlockShift)][(int)(index & BlockMask)];

    /// <summary>Appends text and returns the start offset of the appended run.</summary>
    public long Append(ReadOnlySpan<char> text)
    {
        long start = _length;
        // Record break ends. An LF appended directly after a CR (even in a previous
        // Append call) upgrades that CR's break to a CRLF break ending one later.
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            long docPos = start + i;
            if (c == '\n')
            {
                char prev = i > 0 ? text[i - 1] : (docPos > 0 ? _lastChar : '\0');
                if (prev == '\r')
                {
                    // The CR already registered a break ending at docPos; extend it.
                    _breakEnds[^1] = docPos + 1;
                }
                else
                {
                    _breakEnds.Add(docPos + 1);
                }
            }
            else if (c == '\r')
            {
                _breakEnds.Add(docPos + 1);
            }
        }

        // Copy the characters into blocks.
        int copied = 0;
        while (copied < text.Length)
        {
            int block = (int)(_length >> BlockShift);
            int within = (int)(_length & BlockMask);
            if (block == _blocks.Count) _blocks.Add(new char[BlockSize]);
            int take = Math.Min(text.Length - copied, BlockSize - within);
            text.Slice(copied, take).CopyTo(_blocks[block].AsSpan(within));
            copied += take;
            _length += take;
        }
        if (text.Length > 0) _lastChar = text[^1];
        return start;
    }

    public void CopyTo(long offset, int count, Span<char> destination)
    {
        int copied = 0;
        while (copied < count)
        {
            int block = (int)((offset + copied) >> BlockShift);
            int within = (int)((offset + copied) & BlockMask);
            int take = Math.Min(count - copied, BlockSize - within);
            _blocks[block].AsSpan(within, take).CopyTo(destination.Slice(copied));
            copied += take;
        }
    }

    public long CountBreakEndsInRange(long startExclusive, long endInclusive)
        => BreakSearch.CountInRange(_breakEnds, startExclusive, endInclusive);

    public long GetBreakEndAfter(long startExclusive, long k)
        => BreakSearch.GetAfter(_breakEnds, startExclusive, k);
}

/// <summary>Binary-search helpers shared by the break-end lists.</summary>
internal static class BreakSearch
{
    /// <summary>Index of the first element strictly greater than value.</summary>
    private static int UpperBound(List<long> ends, long value)
    {
        int lo = 0, hi = ends.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ends[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static int UpperBound(long[] ends, long value)
    {
        int lo = 0, hi = ends.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (ends[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    public static long CountInRange(List<long> ends, long startExclusive, long endInclusive)
        => UpperBound(ends, endInclusive) - UpperBound(ends, startExclusive);

    public static long GetAfter(List<long> ends, long startExclusive, long k)
        => ends[(int)(UpperBound(ends, startExclusive) + k)];

    public static long CountInRange(long[] ends, long startExclusive, long endInclusive)
        => UpperBound(ends, endInclusive) - UpperBound(ends, startExclusive);

    public static long GetAfter(long[] ends, long startExclusive, long k)
        => ends[UpperBound(ends, startExclusive) + k];
}
