using System;
using System.Collections.Generic;

namespace Inklet.Engine;

/// <summary>
/// One undoable edit. Units never copy document text: an insert references the
/// add-buffer run it appended; a delete holds the removed pieces themselves
/// (still valid forever because both buffers are append-only/immutable).
/// </summary>
internal abstract class UndoUnit
{
    public long SequenceId;
}

internal sealed class InsertUnit : UndoUnit
{
    public long Offset;
    public long AddStart;
    public long Length;
    public DateTime LastEditUtc;
}

internal sealed class DeleteUnit : UndoUnit
{
    public long Offset;
    public required RemovedRun Run;
}

/// <summary>ReplaceAll and Replace: several primitive units undone/redone as one.</summary>
internal sealed class CompositeUnit : UndoUnit
{
    public required UndoUnit[] Units; // in application order
}

/// <summary>
/// Bounded undo/redo stacks with typing coalescing and a saved-position marker
/// (dirty = current position differs from the position at last save, which
/// restores the classic "undo back to saved leaves the document clean").
/// </summary>
internal sealed class UndoStack
{
    /// <summary>Consecutive inserts within this window coalesce into one unit.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromMilliseconds(500);

    private const int MaxUnits = 4096;

    private readonly List<UndoUnit> _undo = [];
    private readonly List<UndoUnit> _redo = [];
    private long _nextSequenceId = 1;
    // Every document state is identified by the SequenceId of the last unit applied
    // to reach it; the pristine state is 0. _floorId is the state just below the
    // bottom of the (possibly capped) undo list - undoing everything lands there.
    private long _floorId;
    private long _savedId;
    private bool _savedReachable = true;
    private bool _coalesceSealed;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    private long TopId => _undo.Count == 0 ? _floorId : _undo[^1].SequenceId;

    /// <summary>True when the current position differs from the last-saved position.</summary>
    public bool IsDirty => !_savedReachable || TopId != _savedId;

    public void MarkSaved()
    {
        _savedId = TopId;
        _savedReachable = true;
        // New typing after a save must not merge into the pre-save unit, or undoing
        // it would jump past the saved state.
        _coalesceSealed = true;
    }

    /// <summary>Prevents the next insert from coalescing (caret jumps, save, focus loss).</summary>
    public void SealCoalescing() => _coalesceSealed = true;

    /// <summary>
    /// Marks the document dirty with no reachable saved state (session-restored
    /// unsaved edits: content differs from disk but there is no undo history).
    /// </summary>
    public void MarkUnreachableDirty() => _savedReachable = false;

    /// <summary>
    /// Either extends the top insert unit (contiguous in both document and add
    /// buffer, within the window) or pushes a new one. Returns true if coalesced.
    /// </summary>
    public bool PushInsert(long offset, long addStart, long length, ITimeSource clock)
    {
        var now = clock.UtcNow;
        if (!_coalesceSealed
            && _redo.Count == 0
            && _undo.Count > 0
            && _undo[^1] is InsertUnit top
            && top.Offset + top.Length == offset
            && top.AddStart + top.Length == addStart
            && now - top.LastEditUtc <= CoalesceWindow)
        {
            top.Length += length;
            top.LastEditUtc = now;
            return true;
        }
        Push(new InsertUnit { Offset = offset, AddStart = addStart, Length = length, LastEditUtc = now });
        return false;
    }

    public void PushDelete(long offset, RemovedRun run)
        => Push(new DeleteUnit { Offset = offset, Run = run });

    public void PushComposite(UndoUnit[] units)
        => Push(new CompositeUnit { Units = units });

    private void Push(UndoUnit unit)
    {
        // Ids increase strictly with time, so any state ABOVE the current top has a
        // larger id than TopId. Pushing discards the redo branch; a saved state on
        // that branch becomes unreachable.
        if (_savedId > TopId) _savedReachable = false;
        unit.SequenceId = _nextSequenceId++;
        _redo.Clear();
        _undo.Add(unit);
        _coalesceSealed = false;
        if (_undo.Count > MaxUnits)
        {
            // Evicting the bottom unit raises the floor to its post-state; a saved
            // state strictly below the new floor can no longer be reached by undo.
            _floorId = _undo[0].SequenceId;
            _undo.RemoveAt(0);
            if (_savedId < _floorId) _savedReachable = false;
        }
    }

    /// <summary>Pops the unit to undo, moving it to the redo side.</summary>
    public UndoUnit? PopUndo()
    {
        if (_undo.Count == 0) return null;
        var u = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(u);
        _coalesceSealed = true;
        return u;
    }

    /// <summary>Pops the unit to redo, moving it back to the undo side.</summary>
    public UndoUnit? PopRedo()
    {
        if (_redo.Count == 0) return null;
        var u = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(u);
        _coalesceSealed = true;
        return u;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _floorId = 0;
        _savedId = 0;
        _savedReachable = true;
        _coalesceSealed = false;
    }
}
