using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Inklet.Engine;

internal readonly record struct FindMatch(long Offset, long Length);

internal sealed class FindQuery
{
    public required string Needle { get; init; }
    public bool MatchCase { get; init; }
    public bool Backward { get; init; }
    public long StartOffset { get; init; }
    public bool Wrap { get; init; } = true;
    public IProgress<double>? Progress { get; init; }
}

internal sealed partial class Document
{
    private const int SearchWindowChars = 256 * 1024;

    /// <summary>
    /// Finds the next match from the query's start offset, scanning a sliding
    /// window over the snapshot - no document materialisation, vectorised
    /// comparisons, cancellable between windows. While a streamed open is still
    /// indexing, only the absorbed region is searched. Semantics match the old
    /// UI: Ordinal / OrdinalIgnoreCase, wrap-around to the start (or end).
    /// </summary>
    public async Task<FindMatch?> FindNextAsync(FindQuery query, CancellationToken ct = default)
    {
        var root = Root;
        return await Task.Run(() => FindCore(root, query, ct), ct);
    }

    /// <summary>Synchronous find over the current snapshot (used by tests and ReplaceAll).</summary>
    internal FindMatch? FindCore(PieceTreeNode? root, FindQuery query, CancellationToken ct)
    {
        string needle = query.Needle;
        if (needle.Length == 0) return null;
        long total = PieceTreeOps.CharLen(root);
        if (needle.Length > total) return null;
        var cmp = query.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        if (!query.Backward)
        {
            long from = Math.Clamp(query.StartOffset, 0, total);
            var hit = ScanForward(root, from, total, needle, cmp, query.Progress, ct);
            if (hit is null && query.Wrap && from > 0)
                hit = ScanForward(root, 0, Math.Min(total, from + needle.Length - 1), needle, cmp, query.Progress, ct);
            return hit;
        }
        else
        {
            // string.LastIndexOf(needle, start) semantics: the match may START at
            // or before `from`, even if it extends past it.
            long from = Math.Clamp(query.StartOffset, 0, total);
            var hit = ScanBackward(root, 0, Math.Min(total, from + needle.Length), needle, cmp, query.Progress, ct);
            if (hit is null && query.Wrap && from < total)
                hit = ScanBackward(root, 0, total, needle, cmp, query.Progress, ct);
            return hit;
        }
    }

    private FindMatch? ScanForward(PieceTreeNode? root, long start, long endExcl, string needle,
        StringComparison cmp, IProgress<double>? progress, CancellationToken ct)
    {
        if (endExcl - start < needle.Length) return null;
        int window = (int)Math.Min(SearchWindowChars, endExcl - start);
        var buf = new char[window];
        long pos = start;
        while (pos < endExcl)
        {
            ct.ThrowIfCancellationRequested();
            int take = (int)Math.Min(buf.Length, endExcl - pos);
            if (take < needle.Length) return null;
            PieceTreeOps.CopyTo(root, pos, take, buf.AsSpan(0, take), BufferFor);
            int idx = ((ReadOnlySpan<char>)buf.AsSpan(0, take)).IndexOf(needle, cmp);
            if (idx >= 0) return new FindMatch(pos + idx, needle.Length);
            // Overlap so a match straddling the window boundary is not missed.
            pos += take - (needle.Length - 1);
            progress?.Report((double)(pos - start) / Math.Max(1, endExcl - start));
        }
        return null;
    }

    private FindMatch? ScanBackward(PieceTreeNode? root, long startIncl, long endExcl, string needle,
        StringComparison cmp, IProgress<double>? progress, CancellationToken ct)
    {
        if (endExcl - startIncl < needle.Length) return null;
        int window = (int)Math.Min(SearchWindowChars, endExcl - startIncl);
        var buf = new char[window];
        long pos = endExcl;
        while (pos > startIncl)
        {
            ct.ThrowIfCancellationRequested();
            long from = Math.Max(startIncl, pos - buf.Length);
            int take = (int)(pos - from);
            if (take < needle.Length) return null;
            PieceTreeOps.CopyTo(root, from, take, buf.AsSpan(0, take), BufferFor);
            int idx = ((ReadOnlySpan<char>)buf.AsSpan(0, take)).LastIndexOf(needle, cmp);
            if (idx >= 0) return new FindMatch(from + idx, needle.Length);
            pos = from + (needle.Length - 1);
            progress?.Report((double)(endExcl - pos) / Math.Max(1, endExcl - startIncl));
        }
        return null;
    }

    /// <summary>
    /// Collects every match over the current snapshot on a background thread.
    /// Returns the match offsets and the revision they are valid against.
    /// </summary>
    public async Task<(List<long> Offsets, int Revision)> CollectMatchesAsync(
        string needle, bool matchCase, CancellationToken ct = default, IProgress<double>? progress = null)
    {
        var root = Root;
        int revision = Revision;
        var offsets = await Task.Run(() =>
        {
            var result = new List<long>();
            var cmp = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            long total = PieceTreeOps.CharLen(root);
            if (needle.Length == 0 || needle.Length > total) return result;
            var buf = new char[(int)Math.Min(SearchWindowChars, total)];
            long pos = 0;
            while (pos <= total - needle.Length)
            {
                ct.ThrowIfCancellationRequested();
                int take = (int)Math.Min(buf.Length, total - pos);
                PieceTreeOps.CopyTo(root, pos, take, buf.AsSpan(0, take), BufferFor);
                ReadOnlySpan<char> span = buf.AsSpan(0, take);
                int local = 0;
                while (local <= take - needle.Length)
                {
                    int idx = span[local..].IndexOf(needle, cmp);
                    if (idx < 0) break;
                    result.Add(pos + local + idx);
                    local += idx + needle.Length; // non-overlapping, like string.Replace
                }
                pos += take - (needle.Length - 1);
                progress?.Report((double)pos / Math.Max(1, total));
            }
            return result;
        }, ct);
        return (offsets, revision);
    }

    /// <summary>
    /// Applies collected replacements as ONE undo unit. Must run on the mutation
    /// thread and against the same revision the matches were collected from
    /// (returns false if the document changed in between). Descending order
    /// keeps earlier offsets stable while later ones are rewritten.
    /// </summary>
    public bool TryReplaceAll(List<long> offsets, int revision, int needleLength, string replacement)
    {
        if (Revision != revision) return false;
        if (offsets.Count == 0) return true;
        replacement = ConvertNewLinesPublic(replacement);

        var units = new List<UndoUnit>(offsets.Count * 2);
        long breaksBefore = PieceTreeOps.Breaks(Root);
        for (int i = offsets.Count - 1; i >= 0; i--)
        {
            long at = offsets[i];
            var run = DeleteStructural(at, needleLength);
            units.Add(new DeleteUnit { Offset = at, Run = run });
            if (replacement.Length > 0)
            {
                long addStart = _add.Append(replacement);
                InsertStructural(at, Piece.Create(PieceBufferKind.Add, _add, addStart, replacement.Length));
                units.Add(new InsertUnit { Offset = at, AddStart = addStart, Length = replacement.Length, LastEditUtc = _clock.UtcNow });
            }
        }
        _undo.PushComposite(units.ToArray());
        // Edits are scattered document-wide: report a whole-document eviction
        // (removed breaks = everything from the first match on).
        RaiseChanged(offsets[0], needleLength, replacement.Length,
            Math.Max(0, breaksBefore), Math.Max(0, PieceTreeOps.Breaks(Root)));
        return true;
    }

    internal string ConvertNewLinesPublic(string text) => ConvertNewLines(text);
}
