using System;
using System.Collections.Generic;
using System.Threading;
using Inklet.Models;

namespace Inklet.Engine;

/// <summary>Direction for caret snapping over atomic char sequences (CRLF, surrogate pairs).</summary>
internal enum SnapDirection
{
    Left,
    Right,
}

/// <summary>Fine-grained change notification for incremental renderer caches.</summary>
internal readonly struct TextChange
{
    public readonly long Offset;
    public readonly long RemovedLength;
    public readonly long AddedLength;
    public readonly long RemovedLineBreaks;  // breaks destroyed by the removal phase
    public readonly long AddedLineBreaks;    // breaks introduced by the insertion phase
    public readonly long FirstAffectedLine;  // 0-based line containing Offset (post-edit)

    /// <summary>Break count after minus before.</summary>
    public long LineDelta => AddedLineBreaks - RemovedLineBreaks;

    public TextChange(long offset, long removed, long added,
        long removedLineBreaks, long addedLineBreaks, long firstAffectedLine)
    {
        Offset = offset; RemovedLength = removed; AddedLength = added;
        RemovedLineBreaks = removedLineBreaks; AddedLineBreaks = addedLineBreaks;
        FirstAffectedLine = firstAffectedLine;
    }
}

/// <summary>
/// One line of the document, terminator excluded. <see cref="TerminatorLength"/>
/// is 0 (last line), 1 (LF or CR) or 2 (CRLF).
/// </summary>
internal readonly struct LineSlice
{
    public readonly ReadOnlyMemory<char> Text;
    public readonly long CharOffset;         // document offset of the first char
    public readonly byte TerminatorLength;

    public LineSlice(ReadOnlyMemory<char> text, long charOffset, byte terminatorLength)
    {
        Text = text; CharOffset = charOffset; TerminatorLength = terminatorLength;
    }
}

/// <summary>
/// The per-tab text engine seam: a piece-tree document over immutable/append-only
/// buffers with native UTF-16 char offsets (CRLF = 2 chars, no normalisation),
/// reference-based bounded undo, and O(log p) edits.
///
/// Thread model: mutations (Insert/Delete/Replace/Undo/Redo/MarkSaved) must stay
/// on one thread (the UI thread in the app); reads may run on any thread - they
/// bind to the volatile-published tree root and are never torn.
///
/// This Phase-1 in-memory form holds the original content as a string; the
/// memory-mapped byte source and background indexer replace
/// <see cref="OriginalCharBuffer"/> behind the same seam in a later phase.
/// </summary>
internal sealed partial class Document
{
    /// <summary>Longest line slice handed out in one call; giant lines are fetched in segments.</summary>
    internal const int MaxLineSliceChars = 64 * 1024;

    private readonly ICharBuffer _originalBuf;
    private readonly AddBuffer _add = new();
    private readonly UndoStack _undo = new();
    private readonly ITimeSource _clock;
    private PieceTreeNode? _root;            // volatile-published; null = empty document

    public event Action<TextChange>? Changed;

    private Document(string originalText, ITimeSource clock)
    {
        _clock = clock;
        var original = new OriginalCharBuffer(originalText);
        _originalBuf = original;
        _root = originalText.Length == 0
            ? null
            : new PieceTreeNode(null, Piece.Create(PieceBufferKind.Original, original, 0, originalText.Length), null);
        var eol = LineEndingDetector.Detect(originalText);
        LineEnding = eol;
        NewLineString = NewLineFor(eol);
    }

    /// <summary>Streaming ctor: the tree starts empty and absorbs indexed segments.</summary>
    private Document(IByteSource source, TextCodec codec, OriginalIndex index, LineEndingStyle eol)
    {
        _clock = SystemTimeSource.Instance;
        _source = source;
        _codec = codec;
        _index = index;
        _originalBuf = new MappedCharBuffer(source, codec, index);
        _root = null;
        LineEnding = eol;
        NewLineString = NewLineFor(eol);
    }

    private static string NewLineFor(LineEndingStyle eol) => eol switch
    {
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.Cr => "\r",
        _ => "\r\n", // CrLf, and Mixed resolves to CRLF (parity with FileService)
    };

    public static Document CreateUntitled(string initialText = "", ITimeSource? clock = null)
        => new(initialText, clock ?? SystemTimeSource.Instance);

    /// <summary>In-memory open used until the mmap byte source lands.</summary>
    public static Document FromText(string text, ITimeSource? clock = null)
        => new(text, clock ?? SystemTimeSource.Instance);

    // ── Geometry ─────────────────────────────────────────────────────────────

    private PieceTreeNode? Root => Volatile.Read(ref _root);

    /// <summary>Total chars; estimated while a streamed open is still indexing.</summary>
    public long Length => PieceTreeOps.CharLen(Root) + PendingTailChars;

    /// <summary>Total lines (empty document = 1); estimated while indexing.</summary>
    public long LineCount => PieceTreeOps.Breaks(Root) + PendingTailBreaks + 1;

    public LineEndingStyle LineEnding { get; }

    /// <summary>The terminator inserted for Enter and normalised into pasted text.</summary>
    public string NewLineString { get; }

    public bool IsDirty => _undo.IsDirty;
    public bool CanUndo => _undo.CanUndo;
    public bool CanRedo => _undo.CanRedo;

    /// <summary>Bumped on every mutation; cache keys and staleness checks hang off it.</summary>
    public int Revision => Volatile.Read(ref _revision);
    private int _revision;

    /// <summary>Marks the current state as the on-disk state (after save).</summary>
    public void MarkSaved() => _undo.MarkSaved();

    /// <summary>Breaks typing coalescing (call on caret moves so undo units match runs).</summary>
    public void SealUndoCoalescing() => _undo.SealCoalescing();

    // ── Reads (any thread) ───────────────────────────────────────────────────

    private ICharBuffer BufferFor(PieceBufferKind kind)
        => kind == PieceBufferKind.Original ? _originalBuf : _add;

    public string GetText(long offset, long length)
    {
        var root = Root;
        long total = PieceTreeOps.CharLen(root);
        if (offset < 0 || length < 0 || offset + length > total)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (length == 0) return string.Empty;
        return string.Create((int)length, (root, offset), (span, state) =>
            PieceTreeOps.CopyTo(state.root, state.offset, span.Length, span, BufferFor));
    }

    public char CharAt(long offset) => PieceTreeOps.CharAt(Root, offset, BufferFor);

    /// <summary>0-based (line, column) of a char offset. Columns count UTF-16 units.</summary>
    public (long Line, long Column) GetLineColumn(long offset)
    {
        var root = Root;
        if (offset < 0 || offset > PieceTreeOps.CharLen(root))
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (root is null) return (0, 0);
        long line = PieceTreeOps.CountBreakEndsUpTo(root, offset, BufferFor);
        long lineStart = line == 0 ? 0 : PieceTreeOps.BreakEnd(root, line - 1, BufferFor);
        return (line, offset - lineStart);
    }

    /// <summary>Document offset of the first char of a 0-based line.</summary>
    public long GetOffsetForLine(long line)
    {
        var root = Root;
        long breaks = PieceTreeOps.Breaks(root);
        if (line < 0 || line > breaks) throw new ArgumentOutOfRangeException(nameof(line));
        return line == 0 ? 0 : PieceTreeOps.BreakEnd(root!, line - 1, BufferFor);
    }

    /// <summary>One line, terminator excluded, capped at <see cref="MaxLineSliceChars"/>.</summary>
    public LineSlice GetLine(long line)
    {
        var root = Root;
        long breaks = PieceTreeOps.Breaks(root);
        if (line < 0 || line > breaks) throw new ArgumentOutOfRangeException(nameof(line));
        long start = line == 0 ? 0 : PieceTreeOps.BreakEnd(root!, line - 1, BufferFor);
        long endWithTerm = line == breaks ? PieceTreeOps.CharLen(root) : PieceTreeOps.BreakEnd(root!, line, BufferFor);

        byte term = 0;
        if (endWithTerm > start)
        {
            char last = PieceTreeOps.CharAt(root, endWithTerm - 1, BufferFor);
            if (last == '\n')
                term = endWithTerm - 2 >= start && PieceTreeOps.CharAt(root, endWithTerm - 2, BufferFor) == '\r'
                    ? (byte)2 : (byte)1;
            else if (last == '\r')
                term = 1;
        }
        long contentLen = Math.Min(endWithTerm - start - term, MaxLineSliceChars);
        var chars = new char[contentLen];
        PieceTreeOps.CopyTo(root, start, (int)contentLen, chars, BufferFor);
        return new LineSlice(chars, start, term);
    }

    /// <summary>
    /// Moves an offset off the middle of an atomic pair (CRLF or surrogate pair).
    /// </summary>
    public long SnapCaret(long offset, SnapDirection direction)
    {
        var root = Root;
        long total = PieceTreeOps.CharLen(root);
        if (offset <= 0 || offset >= total) return Math.Clamp(offset, 0, total);
        char before = PieceTreeOps.CharAt(root, offset - 1, BufferFor);
        char at = PieceTreeOps.CharAt(root, offset, BufferFor);
        bool midCrLf = before == '\r' && at == '\n';
        bool midSurrogate = char.IsHighSurrogate(before) && char.IsLowSurrogate(at);
        if (!midCrLf && !midSurrogate) return offset;
        return direction == SnapDirection.Left ? offset - 1 : offset + 1;
    }

    // ── Mutations (single-threaded) ──────────────────────────────────────────

    /// <summary>
    /// Inserts text with line endings converted to <see cref="NewLineString"/>
    /// (typed and pasted content is born with the document's own endings).
    /// </summary>
    public void Insert(long offset, string text)
        => InsertCore(offset, ConvertNewLines(text), pushUndo: true);

    /// <summary>Inserts text exactly as given (IME commits, tests).</summary>
    public void InsertRaw(long offset, string text)
        => InsertCore(offset, text, pushUndo: true);

    public void Delete(long offset, long length)
        => DeleteCore(offset, length, pushUndo: true);

    /// <summary>Delete + insert as a single undo unit.</summary>
    public void Replace(long offset, long length, string text)
    {
        GuardEditable(offset, length);
        text = ConvertNewLines(text);
        long breaksBefore = PieceTreeOps.Breaks(Root);
        long breaksMid = breaksBefore;
        var units = new List<UndoUnit>(2);
        if (length > 0)
        {
            var run = DeleteStructural(offset, length);
            units.Add(new DeleteUnit { Offset = offset, Run = run });
            breaksMid = PieceTreeOps.Breaks(Root);
        }
        if (text.Length > 0)
        {
            long addStart = _add.Append(text);
            InsertStructural(offset, Piece.Create(PieceBufferKind.Add, _add, addStart, text.Length));
            units.Add(new InsertUnit { Offset = offset, AddStart = addStart, Length = text.Length, LastEditUtc = _clock.UtcNow });
        }
        if (units.Count == 1)
        {
            if (units[0] is DeleteUnit d) _undo.PushDelete(d.Offset, d.Run);
            else if (units[0] is InsertUnit i) { _undo.SealCoalescing(); _undo.PushInsert(i.Offset, i.AddStart, i.Length, _clock); }
        }
        else if (units.Count > 1)
        {
            _undo.PushComposite(units.ToArray());
        }
        RaiseChanged(offset, length, text.Length,
            Math.Max(0, breaksBefore - breaksMid),
            Math.Max(0, PieceTreeOps.Breaks(Root) - breaksMid));
    }

    /// <summary>Undoes one unit; returns the caret offset to restore, or null.</summary>
    public long? Undo()
    {
        var unit = _undo.PopUndo();
        if (unit is null) return null;
        return ApplyInverse(unit);
    }

    /// <summary>Redoes one unit; returns the caret offset to restore, or null.</summary>
    public long? Redo()
    {
        var unit = _undo.PopRedo();
        if (unit is null) return null;
        return ApplyForward(unit);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    private void GuardEditable(long offset, long length)
    {
        if (offset < 0 || length < 0)
            throw new ArgumentOutOfRangeException(nameof(offset));
        long absorbed = AbsorbedLength;
        if (offset + length > absorbed)
        {
            throw !IsFullyIndexed
                ? new InvalidOperationException(
                    $"Edit at [{offset}, {offset + length}) is beyond the indexed frontier ({absorbed}); wait for indexing.")
                : new ArgumentOutOfRangeException(nameof(offset));
        }
    }

    private string ConvertNewLines(string text)
    {
        if (text.Length == 0 || EndingsMatch(text, NewLineString)) return text;

        var sb = new System.Text.StringBuilder(text.Length + 16);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                sb.Append(NewLineString);
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
            }
            else if (c == '\n')
            {
                sb.Append(NewLineString);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>True when every line break in text already uses the target ending.</summary>
    private static bool EndingsMatch(string text, string target)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                bool isCrLf = i + 1 < text.Length && text[i + 1] == '\n';
                if (isCrLf) { if (target != "\r\n") return false; i++; }
                else if (target != "\r") return false;
            }
            else if (c == '\n')
            {
                if (target != "\n") return false;
            }
        }
        return true;
    }

    private void InsertCore(long offset, string text, bool pushUndo)
    {
        if (text.Length == 0) return;
        GuardEditable(offset, 0);

        long breaksBefore = PieceTreeOps.Breaks(Root);
        long addStart = _add.Append(text);
        InsertStructural(offset, Piece.Create(PieceBufferKind.Add, _add, addStart, text.Length));
        if (pushUndo) _undo.PushInsert(offset, addStart, text.Length, _clock);
        RaiseChanged(offset, 0, text.Length, breaksBefore);
    }

    private void DeleteCore(long offset, long length, bool pushUndo)
    {
        if (length == 0) return;
        GuardEditable(offset, length);

        long breaksBefore = PieceTreeOps.Breaks(Root);
        var run = DeleteStructural(offset, length);
        if (pushUndo) _undo.PushDelete(offset, run);
        RaiseChanged(offset, length, 0, breaksBefore);
    }

    /// <summary>Splices a piece in at offset, coalescing with a contiguous add piece to its left.</summary>
    private void InsertStructural(long offset, Piece piece)
    {
        var (l, r) = PieceTreeOps.Split(Root, offset, BufferFor);
        // Typing coalescing at the tree level: if the piece to the left is the add
        // piece we just extended past, merge instead of accumulating 1-char pieces.
        if (piece.Kind == PieceBufferKind.Add && l is not null)
        {
            var max = PieceTreeOps.MaxPiece(l)!.Value;
            if (max.Kind == PieceBufferKind.Add && max.Start + max.Length == piece.Start)
            {
                var rest = PieceTreeOps.RemoveMaxPiece(l, out _);
                var merged = Piece.Create(PieceBufferKind.Add, _add, max.Start, max.Length + piece.Length);
                Volatile.Write(ref _root, PieceTreeOps.Concat3(rest, merged, r));
                return;
            }
        }
        Volatile.Write(ref _root, PieceTreeOps.Concat3(l, piece, r));
    }

    private RemovedRun DeleteStructural(long offset, long length)
    {
        var (l, rest) = PieceTreeOps.Split(Root, offset, BufferFor);
        var (mid, r) = PieceTreeOps.Split(rest, length, BufferFor);
        var pieces = new List<Piece>();
        PieceTreeOps.CollectPieces(mid, pieces);
        Volatile.Write(ref _root, PieceTreeOps.Concat(l, r));
        return new RemovedRun(pieces.ToArray());
    }

    /// <summary>Re-inserts the removed pieces of a delete at their original offset.</summary>
    private void ReinsertStructural(long offset, RemovedRun run)
    {
        var (l, r) = PieceTreeOps.Split(Root, offset, BufferFor);
        var runTree = PieceTreeOps.Build(run.Pieces);
        Volatile.Write(ref _root, PieceTreeOps.Concat(PieceTreeOps.Concat(l, runTree), r));
    }

    private long ApplyInverse(UndoUnit unit)
    {
        switch (unit)
        {
            case InsertUnit ins:
            {
                long breaksBefore = PieceTreeOps.Breaks(Root);
                DeleteStructural(ins.Offset, ins.Length);
                RaiseChanged(ins.Offset, ins.Length, 0, breaksBefore);
                return ins.Offset;
            }
            case DeleteUnit del:
            {
                long breaksBefore = PieceTreeOps.Breaks(Root);
                ReinsertStructural(del.Offset, del.Run);
                RaiseChanged(del.Offset, 0, del.Run.TotalLength, breaksBefore);
                return del.Offset + del.Run.TotalLength;
            }
            case CompositeUnit comp:
                long caret = 0;
                for (int i = comp.Units.Length - 1; i >= 0; i--)
                    caret = ApplyInverse(comp.Units[i]);
                return caret;
            default:
                throw new InvalidOperationException($"Unknown undo unit {unit.GetType().Name}");
        }
    }

    private long ApplyForward(UndoUnit unit)
    {
        switch (unit)
        {
            case InsertUnit ins:
            {
                long breaksBefore = PieceTreeOps.Breaks(Root);
                InsertStructural(ins.Offset, Piece.Create(PieceBufferKind.Add, _add, ins.AddStart, ins.Length));
                RaiseChanged(ins.Offset, 0, ins.Length, breaksBefore);
                return ins.Offset + ins.Length;
            }
            case DeleteUnit del:
            {
                long breaksBefore = PieceTreeOps.Breaks(Root);
                DeleteStructural(del.Offset, del.Run.TotalLength);
                RaiseChanged(del.Offset, del.Run.TotalLength, 0, breaksBefore);
                return del.Offset;
            }
            case CompositeUnit comp:
                long caret = 0;
                foreach (var u in comp.Units)
                    caret = ApplyForward(u);
                return caret;
            default:
                throw new InvalidOperationException($"Unknown undo unit {unit.GetType().Name}");
        }
    }

    private void RaiseChanged(long offset, long removed, long added, long breaksBefore)
    {
        // Single-phase edit: attribute the whole delta to one side.
        long delta = PieceTreeOps.Breaks(Root) - breaksBefore;
        RaiseChanged(offset, removed, added, Math.Max(0, -delta), Math.Max(0, delta));
    }

    private void RaiseChanged(long offset, long removed, long added, long removedBreaks, long addedBreaks)
    {
        Interlocked.Increment(ref _revision);
        var handler = Changed;
        if (handler is null) return;
        var root = Root;
        long line = root is null ? 0 : PieceTreeOps.CountBreakEndsUpTo(root, Math.Min(offset, PieceTreeOps.CharLen(root)), BufferFor);
        handler(new TextChange(offset, removed, added, removedBreaks, addedBreaks, line));
    }
}
