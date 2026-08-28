using System;
using System.Collections.Generic;

namespace Inklet.Engine;

/// <summary>
/// One contiguous run of characters in either the original or the add buffer.
/// Pieces are never empty. Break metadata is piece-local: <see cref="Breaks"/>
/// counts line breaks within the piece's own text where CRLF is ONE break, and a
/// CR whose matching LF lies outside the piece counts as a lone-CR break (the
/// tree's seam arithmetic re-merges such splits with a neighbouring LF).
/// </summary>
internal readonly struct Piece
{
    public readonly PieceBufferKind Kind;
    public readonly long Start;      // offset in the backing buffer
    public readonly long Length;     // chars, > 0
    public readonly long Breaks;     // piece-local break count (CRLF = 1)
    public readonly bool FirstIsLf;
    public readonly bool LastIsCr;

    private Piece(PieceBufferKind kind, long start, long length, long breaks, bool firstIsLf, bool lastIsCr)
    {
        Kind = kind; Start = start; Length = length; Breaks = breaks;
        FirstIsLf = firstIsLf; LastIsCr = lastIsCr;
    }

    /// <summary>Builds a piece, deriving break metadata from the buffer (O(log breaks)).</summary>
    public static Piece Create(PieceBufferKind kind, ICharBuffer buffer, long start, long length)
    {
        long end = start + length;
        bool firstIsLf = buffer.PeekChar(start) == '\n';
        bool lastIsCr = buffer.PeekChar(end - 1) == '\r';
        long breaks = buffer.CountBreakEndsInRange(start, end);
        // Split-CRLF correction: a trailing CR whose LF lies just beyond the piece is
        // a break piece-locally, but the buffer's break for it ends outside the range.
        if (lastIsCr && end < buffer.Length && buffer.PeekChar(end) == '\n') breaks++;
        return new Piece(kind, start, length, breaks, firstIsLf, lastIsCr);
    }

    /// <summary>Extends an add-buffer piece in place (used for typing coalescing).</summary>
    public static Piece CreateExtended(Piece left, ICharBuffer buffer, long extraLength)
        => Create(left.Kind, buffer, left.Start, left.Length + extraLength);

    /// <summary>Piece-local char offset just after the k-th (0-based, k &lt; Breaks) break.</summary>
    public long BreakEnd(ICharBuffer buffer, long k)
    {
        long end = Start + Length;
        bool corrected = LastIsCr && end < buffer.Length && buffer.PeekChar(end) == '\n';
        long raw = Breaks - (corrected ? 1 : 0);
        if (k < raw) return buffer.GetBreakEndAfter(Start, k) - Start;
        return Length; // the corrected split-CRLF break ends at the piece boundary
    }

    /// <summary>Piece-local breaks with break end &lt;= localPos.</summary>
    public long CountBreakEndsUpTo(ICharBuffer buffer, long localPos)
    {
        long end = Start + Length;
        long c = buffer.CountBreakEndsInRange(Start, Start + localPos);
        bool corrected = LastIsCr && end < buffer.Length && buffer.PeekChar(end) == '\n';
        if (corrected && localPos == Length) c++;
        return c;
    }
}

/// <summary>The pieces removed by a range delete, in document order. Undo re-inserts them verbatim.</summary>
internal sealed class RemovedRun
{
    public readonly Piece[] Pieces;
    public readonly long TotalLength;

    public RemovedRun(Piece[] pieces)
    {
        Pieces = pieces;
        long len = 0;
        foreach (var p in pieces) len += p.Length;
        TotalLength = len;
    }
}

/// <summary>
/// Persistent (path-copying) weight-balanced tree of pieces. Every mutation
/// builds new nodes along one root-to-leaf path and returns a new root; readers
/// hold whatever root they observed and are never torn. Subtree aggregates:
/// char length and seam-corrected line-break counts (a CR at the end of one
/// piece followed by an LF at the start of the next forms ONE break).
/// </summary>
internal sealed class PieceTreeNode
{
    public readonly Piece P;
    public readonly PieceTreeNode? L;
    public readonly PieceTreeNode? R;
    public readonly int Count;       // pieces in subtree
    public readonly long CharLen;    // chars in subtree
    public readonly long Breaks;     // seam-corrected breaks in subtree text
    public readonly bool FirstIsLf;  // first char of subtree text
    public readonly bool LastIsCr;   // last char of subtree text

    public PieceTreeNode(PieceTreeNode? l, Piece p, PieceTreeNode? r)
    {
        P = p; L = l; R = r;
        Count = 1 + (l?.Count ?? 0) + (r?.Count ?? 0);
        CharLen = p.Length + (l?.CharLen ?? 0) + (r?.CharLen ?? 0);
        long seamLp = (l is not null && l.LastIsCr && p.FirstIsLf) ? 1 : 0;
        long seamPr = (r is not null && p.LastIsCr && r.FirstIsLf) ? 1 : 0;
        Breaks = (l?.Breaks ?? 0) + p.Breaks + (r?.Breaks ?? 0) - seamLp - seamPr;
        FirstIsLf = l?.FirstIsLf ?? p.FirstIsLf;
        LastIsCr = r?.LastIsCr ?? p.LastIsCr;
    }
}

/// <summary>
/// Operations over immutable piece-tree roots. All methods are static and pure;
/// the owning <see cref="Document"/> holds the current root and publishes it
/// with a volatile write.
/// </summary>
internal static class PieceTreeOps
{
    private const int Ratio = 3; // Adams weight-balance ratio

    private static int Weight(PieceTreeNode? n) => (n?.Count ?? 0) + 1;
    public static long CharLen(PieceTreeNode? n) => n?.CharLen ?? 0;
    public static long Breaks(PieceTreeNode? n) => n?.Breaks ?? 0;

    // ── Construction / balance ───────────────────────────────────────────────

    private static PieceTreeNode Mk(PieceTreeNode? l, Piece p, PieceTreeNode? r) => new(l, p, r);

    private static PieceTreeNode SingleL(PieceTreeNode? l, Piece p, PieceTreeNode r)
        => Mk(Mk(l, p, r.L), r.P, r.R);
    private static PieceTreeNode SingleR(PieceTreeNode l, Piece p, PieceTreeNode? r)
        => Mk(l.L, l.P, Mk(l.R, p, r));
    private static PieceTreeNode DoubleL(PieceTreeNode? l, Piece p, PieceTreeNode r)
        => Mk(Mk(l, p, r.L!.L), r.L.P, Mk(r.L.R, r.P, r.R));
    private static PieceTreeNode DoubleR(PieceTreeNode l, Piece p, PieceTreeNode? r)
        => Mk(Mk(l.L, l.P, l.R!.L), l.R.P, Mk(l.R.R, p, r));

    /// <summary>Balance for at-most-one-level imbalance (Adams' T').</summary>
    private static PieceTreeNode Balance(PieceTreeNode? l, Piece p, PieceTreeNode? r)
    {
        int wl = Weight(l), wr = Weight(r);
        if (wl + wr <= 2) return Mk(l, p, r);
        if (wr > Ratio * wl)
        {
            var rn = r!;
            return Weight(rn.L) < Weight(rn.R) ? SingleL(l, p, rn) : DoubleL(l, p, rn);
        }
        if (wl > Ratio * wr)
        {
            var ln = l!;
            return Weight(ln.R) < Weight(ln.L) ? SingleR(ln, p, r) : DoubleR(ln, p, r);
        }
        return Mk(l, p, r);
    }

    /// <summary>Joins two trees of arbitrary size around pivot piece p (Adams' concat3).</summary>
    public static PieceTreeNode Concat3(PieceTreeNode? l, Piece p, PieceTreeNode? r)
    {
        if (l is null && r is null) return Mk(null, p, null);
        if (l is null) return InsertMin(r!, p);
        if (r is null) return InsertMax(l, p);
        if (Ratio * Weight(l) < Weight(r)) return Balance(Concat3(l, p, r.L), r.P, r.R);
        if (Ratio * Weight(r) < Weight(l)) return Balance(l.L, l.P, Concat3(l.R, p, r));
        return Mk(l, p, r);
    }

    private static PieceTreeNode InsertMin(PieceTreeNode n, Piece p)
        => n.L is null ? Balance(Mk(null, p, null), n.P, n.R) : Balance(InsertMin(n.L, p), n.P, n.R);

    private static PieceTreeNode InsertMax(PieceTreeNode n, Piece p)
        => n.R is null ? Balance(n.L, n.P, Mk(null, p, null)) : Balance(n.L, n.P, InsertMax(n.R, p));

    /// <summary>Joins two trees (no pivot).</summary>
    public static PieceTreeNode? Concat(PieceTreeNode? l, PieceTreeNode? r)
    {
        if (l is null) return r;
        if (r is null) return l;
        var (rest, max) = RemoveMax(l);
        return Concat3(rest, max, r);
    }

    private static (PieceTreeNode? rest, Piece max) RemoveMax(PieceTreeNode n)
    {
        if (n.R is null) return (n.L, n.P);
        var (rest, max) = RemoveMax(n.R);
        return (Balance(n.L, n.P, rest), max);
    }

    // ── Split ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Splits at char offset k: left gets the first k chars. Splitting inside a
    /// piece re-derives the two halves' break metadata from the buffer.
    /// </summary>
    public static (PieceTreeNode? left, PieceTreeNode? right) Split(
        PieceTreeNode? n, long k, Func<PieceBufferKind, ICharBuffer> buffers)
    {
        if (n is null) return (null, null);
        long ll = CharLen(n.L);
        if (k < ll)
        {
            var (a, b) = Split(n.L, k, buffers);
            return (a, Concat3(b, n.P, n.R));
        }
        long kp = k - ll;
        if (kp == 0) return (n.L, Concat3(null, n.P, n.R));
        if (kp < n.P.Length)
        {
            var buf = buffers(n.P.Kind);
            var pl = Piece.Create(n.P.Kind, buf, n.P.Start, kp);
            var pr = Piece.Create(n.P.Kind, buf, n.P.Start + kp, n.P.Length - kp);
            return (Concat3(n.L, pl, null), Concat3(null, pr, n.R));
        }
        if (kp == n.P.Length) return (Concat3(n.L, n.P, null), n.R);
        var (c, d) = Split(n.R, kp - n.P.Length, buffers);
        return (Concat3(n.L, n.P, c), d);
    }

    // ── Queries ──────────────────────────────────────────────────────────────

    /// <summary>Copies [offset, offset+count) of the subtree text into destination.</summary>
    public static void CopyTo(PieceTreeNode? n, long offset, int count,
        Span<char> destination, Func<PieceBufferKind, ICharBuffer> buffers)
    {
        while (n is not null && count > 0)
        {
            long ll = CharLen(n.L);
            if (offset < ll)
            {
                if (offset + count <= ll) { n = n.L; continue; }
                int leftTake = (int)(ll - offset);
                CopyTo(n.L, offset, leftTake, destination[..leftTake], buffers);
                destination = destination[leftTake..];
                count -= leftTake;
                offset = ll;
            }
            long kp = offset - ll;
            if (kp < n.P.Length)
            {
                int take = (int)Math.Min(count, n.P.Length - kp);
                buffers(n.P.Kind).CopyTo(n.P.Start + kp, take, destination[..take]);
                destination = destination[take..];
                count -= take;
                offset += take;
                kp += take;
            }
            offset -= ll + n.P.Length;
            n = n.R;
        }
    }

    public static char CharAt(PieceTreeNode? n, long offset, Func<PieceBufferKind, ICharBuffer> buffers)
    {
        while (n is not null)
        {
            long ll = CharLen(n.L);
            if (offset < ll) { n = n.L; continue; }
            long kp = offset - ll;
            if (kp < n.P.Length) return buffers(n.P.Kind)[n.P.Start + kp];
            offset = kp - n.P.Length;
            n = n.R;
        }
        throw new ArgumentOutOfRangeException(nameof(offset));
    }

    /// <summary>
    /// Subtree-local char offset just after the k-th (0-based, k &lt; Breaks) break
    /// of the subtree's text, honouring seam-merged CRLF pairs.
    /// </summary>
    public static long BreakEnd(PieceTreeNode n, long k, Func<PieceBufferKind, ICharBuffer> buffers)
    {
        long acc = 0;
        while (true)
        {
            long bL = Breaks(n.L);
            long seamLp = (n.L is not null && n.L.LastIsCr && n.P.FirstIsLf) ? 1 : 0;
            if (k < bL - seamLp)
            {
                n = n.L!;
                continue;
            }
            long kp = k - (bL - seamLp);
            long seamPr = (n.R is not null && n.P.LastIsCr && n.R.FirstIsLf) ? 1 : 0;
            if (kp < n.P.Breaks - seamPr)
                return acc + CharLen(n.L) + n.P.BreakEnd(buffers(n.P.Kind), kp);
            long kr = kp - (n.P.Breaks - seamPr);
            acc += CharLen(n.L) + n.P.Length;
            n = n.R!;
            k = kr;
        }
    }

    /// <summary>Number of subtree breaks whose (merged) end is &lt;= pos.</summary>
    public static long CountBreakEndsUpTo(PieceTreeNode? n, long pos, Func<PieceBufferKind, ICharBuffer> buffers)
    {
        long acc = 0;
        while (n is not null && pos > 0)
        {
            long ll = CharLen(n.L);
            long seamLp = (n.L is not null && n.L.LastIsCr && n.P.FirstIsLf) ? 1 : 0;
            if (pos <= ll)
            {
                // A CR-break at the very end of L that seam-merges into P has not
                // fully ended at pos == ll; the recursion below would count it.
                if (seamLp == 1 && pos == ll) acc -= 1;
                n = n.L;
                continue;
            }
            long pp = pos - ll;
            long seamPr = (n.R is not null && n.P.LastIsCr && n.R.FirstIsLf) ? 1 : 0;
            if (pp <= n.P.Length)
            {
                acc += Breaks(n.L) - seamLp + n.P.CountBreakEndsUpTo(buffers(n.P.Kind), pp);
                if (seamPr == 1 && pp == n.P.Length) acc -= 1;
                return acc;
            }
            acc += Breaks(n.L) - seamLp + n.P.Breaks - seamPr;
            pos = pp - n.P.Length;
            n = n.R;
        }
        return acc;
    }

    /// <summary>In-order pieces of the subtree (used to capture a RemovedRun).</summary>
    public static void CollectPieces(PieceTreeNode? n, List<Piece> into)
    {
        if (n is null) return;
        CollectPieces(n.L, into);
        into.Add(n.P);
        CollectPieces(n.R, into);
    }

    /// <summary>Builds a balanced tree from pieces in document order.</summary>
    public static PieceTreeNode? Build(ReadOnlySpan<Piece> pieces)
    {
        if (pieces.Length == 0) return null;
        int mid = pieces.Length / 2;
        return Mk(Build(pieces[..mid]), pieces[mid], Build(pieces[(mid + 1)..]));
    }

    /// <summary>Rightmost piece of the subtree, or null.</summary>
    public static Piece? MaxPiece(PieceTreeNode? n)
    {
        if (n is null) return null;
        while (n.R is not null) n = n.R;
        return n.P;
    }

    /// <summary>Removes the rightmost piece (caller guarantees n is non-null).</summary>
    public static PieceTreeNode? RemoveMaxPiece(PieceTreeNode n, out Piece removed)
    {
        var (rest, max) = RemoveMax(n);
        removed = max;
        return rest;
    }
}
