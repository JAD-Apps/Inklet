using System;
using System.Collections.Generic;
using Inklet.Models;

namespace Inklet.Editor;

/// <summary>
/// A high-level edit buffer that combines a <see cref="PieceTable"/> backing store,
/// a lazily-rebuilt <see cref="LineIndex"/>, and a simple undo/redo stack with edit
/// coalescing. Designed as the data layer for a custom virtualised editor.
///
/// <para>
/// The shape mirrors what a custom editor control needs to feed its renderer:
/// fast O(log lines) line/column lookup, fast O(window) text slicing for the visible
/// viewport, and undo/redo without re-snapshotting the whole document.
/// </para>
///
/// <para>
/// Scope note: this is the backing store. The renderer (Direct2D + DWrite + caret +
/// selection + IME + UIA) is the much larger remaining piece — see PATH_C_PLAN.md.
/// </para>
/// </summary>
public sealed class EditorBuffer
{
    private readonly PieceTable _table;
    private readonly LineIndex _lines = new();
    private readonly Stack<UndoEntry> _undo = new();
    private readonly Stack<UndoEntry> _redo = new();

    // Edit coalescing: consecutive single-character inserts at the same position are
    // merged into a single undo entry — the standard Notepad-equivalent behaviour
    // where Ctrl+Z undoes whole words rather than individual keystrokes.
    private UndoEntry? _coalesceTarget;
    private DateTime _lastEditTime;
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(500);

    public EditorBuffer(string initial = "")
    {
        _table = new PieceTable(initial);
        _lines.Invalidate(initial);
    }

    /// <summary>Total character length.</summary>
    public int Length => _table.Length;

    /// <summary>Line count (1-based).</summary>
    public int LineCount => _lines.LineCount;

    /// <summary>Whether <see cref="Undo"/> would have an effect.</summary>
    public bool CanUndo => _undo.Count > 0;

    /// <summary>Whether <see cref="Redo"/> would have an effect.</summary>
    public bool CanRedo => _redo.Count > 0;

    /// <summary>
    /// Inserts text at <paramref name="offset"/>. Coalesces with the previous insert
    /// if it occurred within the coalesce window at the contiguous position — this
    /// keeps the undo stack at the granularity of a typing burst rather than
    /// individual keypresses.
    /// </summary>
    public void Insert(int offset, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var now = DateTime.UtcNow;
        bool coalesced = false;

        if (_coalesceTarget is { } target
            && target.Kind == EditKind.Insert
            && target.Offset + target.Text.Length == offset
            && (now - _lastEditTime) < CoalesceWindow)
        {
            // Extend the in-flight undo entry rather than pushing a new one.
            _coalesceTarget = target with { Text = target.Text + text };
            // Replace the top of the undo stack with the extended entry.
            _undo.Pop();
            _undo.Push(_coalesceTarget.Value);
            coalesced = true;
        }

        if (!coalesced)
        {
            var entry = new UndoEntry(EditKind.Insert, offset, text);
            _undo.Push(entry);
            _coalesceTarget = entry;
        }

        _redo.Clear();
        _lastEditTime = now;

        _table.Insert(offset, text);
        InvalidateLineIndex();
    }

    /// <summary>
    /// Deletes <paramref name="length"/> characters at <paramref name="offset"/>.
    /// The deleted text is captured into the undo entry so Undo can restore it.
    /// </summary>
    public void Delete(int offset, int length)
    {
        if (length <= 0 || _table.Length == 0) return;

        var deleted = _table.GetText(offset, length);
        if (deleted.Length == 0) return;

        _undo.Push(new UndoEntry(EditKind.Delete, offset, deleted));
        _redo.Clear();
        _coalesceTarget = null; // deletes break coalescing chains

        _table.Delete(offset, deleted.Length);
        InvalidateLineIndex();
    }

    /// <summary>Undoes the most recent edit. Returns the cursor position to land at.</summary>
    public int? Undo()
    {
        if (_undo.Count == 0) return null;
        var entry = _undo.Pop();
        _coalesceTarget = null;

        if (entry.Kind == EditKind.Insert)
        {
            _table.Delete(entry.Offset, entry.Text.Length);
            _redo.Push(entry);
            InvalidateLineIndex();
            return entry.Offset;
        }
        else
        {
            _table.Insert(entry.Offset, entry.Text);
            _redo.Push(entry);
            InvalidateLineIndex();
            return entry.Offset + entry.Text.Length;
        }
    }

    /// <summary>Redoes the most recently undone edit.</summary>
    public int? Redo()
    {
        if (_redo.Count == 0) return null;
        var entry = _redo.Pop();
        _coalesceTarget = null;

        if (entry.Kind == EditKind.Insert)
        {
            _table.Insert(entry.Offset, entry.Text);
            _undo.Push(entry);
            InvalidateLineIndex();
            return entry.Offset + entry.Text.Length;
        }
        else
        {
            _table.Delete(entry.Offset, entry.Text.Length);
            _undo.Push(entry);
            InvalidateLineIndex();
            return entry.Offset;
        }
    }

    /// <summary>Returns the full document text. O(N) — prefer <see cref="GetText(int, int)"/>.</summary>
    public string GetText() => _table.GetText();

    /// <summary>Returns a slice of the document.</summary>
    public string GetText(int offset, int length) => _table.GetText(offset, length);

    /// <summary>Returns the (line, column) — 1-based — for an offset.</summary>
    public (int Line, int Column) GetLineColumn(int offset) => _lines.GetLineColumn(offset);

    /// <summary>Returns the buffer offset for the start of <paramref name="lineNumber"/>.</summary>
    public int GetOffsetForLine(int lineNumber) => _lines.GetOffset(lineNumber);

    private void InvalidateLineIndex()
    {
        // For now we re-materialise to feed the LineIndex — once the renderer is built,
        // we'll switch to incremental line-index updates that consume only the inserted
        // or deleted span, eliminating the materialisation here too.
        _lines.Invalidate(_table.GetText());
    }

    private enum EditKind { Insert, Delete }

    private readonly record struct UndoEntry(EditKind Kind, int Offset, string Text);
}
