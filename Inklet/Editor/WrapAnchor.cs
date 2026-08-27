using System;

namespace Inklet.Editor;

/// <summary>
/// Supplies display-row counts for logical lines. Implemented by the renderer's
/// layout cache (rows come from the line's CanvasTextLayout; 1 when wrap is
/// off). Costs are O(1) for cached lines and one layout build otherwise, so
/// callers walk bounded distances only.
/// </summary>
internal interface ILineWrapOracle
{
    long LineCount { get; }
    int RowsOfLine(long line);
}

/// <summary>
/// Vertical scroll position as a document anchor: the display row
/// (<see cref="Line"/>, <see cref="SubRow"/>) drawn at the top of the viewport,
/// plus a sub-row pixel remainder for smooth wheel scrolling. There is no
/// absolute pixel offset anywhere - the view never needs to know the total
/// content height, which is what makes scrolling O(viewport) at any file size.
/// </summary>
internal readonly record struct ViewportAnchor(long Line, int SubRow, float PixelDelta)
{
    public static readonly ViewportAnchor Origin = new(0, 0, 0);
}

/// <summary>Bounded anchor arithmetic. Every operation touches O(|delta| + viewport) lines.</summary>
internal static class RowWalker
{
    /// <summary>
    /// Advances the anchor by a number of display rows (negative = up), clamping
    /// at the document edges. PixelDelta is preserved by the caller's choice.
    /// </summary>
    public static ViewportAnchor Walk(ViewportAnchor anchor, long deltaRows, ILineWrapOracle oracle)
    {
        long line = Math.Clamp(anchor.Line, 0, Math.Max(0, oracle.LineCount - 1));
        int sub = anchor.SubRow;
        int rowsHere = oracle.RowsOfLine(line);
        if (sub >= rowsHere) sub = rowsHere - 1;

        while (deltaRows > 0)
        {
            int remainingInLine = rowsHere - 1 - sub;
            if (deltaRows <= remainingInLine)
            {
                sub += (int)deltaRows;
                return new ViewportAnchor(line, sub, anchor.PixelDelta);
            }
            if (line >= oracle.LineCount - 1)
                return new ViewportAnchor(line, rowsHere - 1, anchor.PixelDelta); // bottom clamp
            deltaRows -= remainingInLine + 1;
            line++;
            sub = 0;
            rowsHere = oracle.RowsOfLine(line);
        }
        while (deltaRows < 0)
        {
            if (-deltaRows <= sub)
            {
                sub -= (int)(-deltaRows);
                return new ViewportAnchor(line, sub, anchor.PixelDelta);
            }
            if (line == 0)
                return new ViewportAnchor(0, 0, anchor.PixelDelta); // top clamp
            deltaRows += sub + 1;
            line--;
            rowsHere = oracle.RowsOfLine(line);
            sub = rowsHere - 1;
        }
        return new ViewportAnchor(line, sub, anchor.PixelDelta);
    }

    /// <summary>
    /// Display rows from anchor a to anchor b (positive when b is below a), or
    /// null when the distance exceeds <paramref name="limit"/> - callers teleport
    /// instead of walking unbounded distances.
    /// </summary>
    public static long? TryDistance(ViewportAnchor a, ViewportAnchor b, long limit, ILineWrapOracle oracle)
    {
        if (a.Line == b.Line) return b.SubRow - a.SubRow;
        var (top, bottom, sign) = a.Line < b.Line ? (a, b, 1) : (b, a, -1);
        long rows = oracle.RowsOfLine(top.Line) - top.SubRow; // rows from top anchor to end of its line
        for (long line = top.Line + 1; line < bottom.Line; line++)
        {
            rows += oracle.RowsOfLine(line);
            if (rows > limit) return null;
        }
        rows += bottom.SubRow;
        return rows > limit ? null : sign * rows;
    }

    /// <summary>
    /// The highest anchor that still fills a viewport of `viewportRows` (i.e. the
    /// bottom-most legal scroll position). Walks backward from the last line.
    /// </summary>
    public static ViewportAnchor BottomAnchor(long viewportRows, ILineWrapOracle oracle)
    {
        long lastLine = Math.Max(0, oracle.LineCount - 1);
        var bottom = new ViewportAnchor(lastLine, oracle.RowsOfLine(lastLine) - 1, 0);
        return Walk(bottom, -(Math.Max(1, viewportRows) - 1), oracle);
    }

    /// <summary>Clamps an anchor so the viewport never scrolls past the bottom of the content.</summary>
    public static ViewportAnchor ClampToBottom(ViewportAnchor anchor, long viewportRows, ILineWrapOracle oracle)
    {
        var bottom = BottomAnchor(viewportRows, oracle);
        if (anchor.Line > bottom.Line || (anchor.Line == bottom.Line && anchor.SubRow > bottom.SubRow))
            return bottom;
        return anchor;
    }
}
