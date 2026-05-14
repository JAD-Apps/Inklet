using System;
using System.Collections.Generic;

namespace Inklet.Models;

/// <summary>
/// A lazily-rebuilt index of line-start offsets in a text buffer. Lookup operations
/// (offset → line/column, line → offset, line count) are O(log N); rebuild is O(N).
///
/// The previous code walked from offset 0 to the cursor on every selection change,
/// which made arrow-key navigation in a multi-megabyte file painfully slow. This
/// index makes arrow keys constant-cost and amortises the build over edit bursts:
/// the index is marked dirty on text change but only rebuilt on the next read.
/// </summary>
public sealed class LineIndex
{
    private string _text = string.Empty;
    private bool _dirty;

    // _lineStarts[i] = offset of the first character of line (i+1) in _text. Always
    // contains 0 as its first entry. A trailing line-ending implies a final empty line
    // whose start is _text.Length.
    private readonly List<int> _lineStarts = [0];

    /// <summary>Total number of lines in the indexed text.</summary>
    public int LineCount
    {
        get { EnsureBuilt(); return _lineStarts.Count; }
    }

    /// <summary>
    /// Notifies the index that the underlying text has changed. The actual rebuild is
    /// deferred to the next read so that bursts of edits without intervening reads pay
    /// only one rebuild cost.
    /// </summary>
    public void Invalidate(string newText)
    {
        _text = newText ?? string.Empty;
        _dirty = true;
    }

    /// <summary>
    /// Returns the 1-based (line, column) for an offset into the indexed text.
    /// Out-of-range offsets are clamped.
    /// </summary>
    public (int Line, int Column) GetLineColumn(int offset)
    {
        EnsureBuilt();
        if (offset < 0) offset = 0;
        if (offset > _text.Length) offset = _text.Length;

        // Binary search for the largest line-start <= offset.
        int lo = 0, hi = _lineStarts.Count - 1;
        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (_lineStarts[mid] <= offset) lo = mid;
            else hi = mid - 1;
        }

        int line = lo + 1;
        int column = offset - _lineStarts[lo] + 1;
        return (line, column);
    }

    /// <summary>
    /// Returns the buffer offset of the start of <paramref name="lineNumber"/> (1-based).
    /// Out-of-range line numbers are clamped to [1, <see cref="LineCount"/>].
    /// </summary>
    public int GetOffset(int lineNumber)
    {
        EnsureBuilt();
        if (lineNumber < 1) lineNumber = 1;
        if (lineNumber > _lineStarts.Count) lineNumber = _lineStarts.Count;
        return _lineStarts[lineNumber - 1];
    }

    private void EnsureBuilt()
    {
        if (!_dirty) return;
        _dirty = false;

        _lineStarts.Clear();
        _lineStarts.Add(0);

        var text = _text;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\r')
            {
                // CR LF counts as one line break. Skip the LF half.
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                _lineStarts.Add(i + 1);
            }
            else if (c == '\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }
}
