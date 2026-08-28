using System;
using System.Collections.Generic;
using Inklet.Engine;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;

namespace Inklet.Editor;

internal sealed partial class TextEditorControl
{
    // ── Per-line layout cache ────────────────────────────────────────────────
    //
    // One CanvasTextLayout per logical line, LRU-capped at ~4 viewports. The
    // layout is the single source of truth for wrap rows, caret x/y, hit
    // testing and selection rectangles, which makes tabs, CJK, proportional
    // fonts and surrogate pairs correct by construction. Invalidation is
    // incremental: an edit evicts only the affected lines and shifts the keys
    // of later ones; font/width changes bump a stamp so entries lazily rebuild.

    private sealed class LineEntry
    {
        public required CanvasTextLayout Layout;
        public required int RowCount;
        public required int Stamp;          // font/width generation
        public required int TextLength;     // chars in the layout (terminator excluded)
    }

    private readonly Dictionary<long, LinkedListNode<(long Line, LineEntry Entry)>> _layoutMap = [];
    private readonly LinkedList<(long Line, LineEntry Entry)> _layoutLru = [];
    private int _layoutStamp = 1;           // bumped on font or wrap-width change
    private int _lastWrapWidthKey = -1;

    private int LayoutCacheCapacity => Math.Max(128, ViewportRows * 4);

    private void ResetLayoutCache()
    {
        foreach (var node in _layoutLru) node.Entry.Layout.Dispose();
        _layoutLru.Clear();
        _layoutMap.Clear();
        _layoutStamp++;
    }

    /// <summary>Incremental invalidation from a document change.</summary>
    private void ApplyChangeToLayoutCache(TextChange change)
    {
        long first = change.FirstAffectedLine;
        long lastRemoved = first + Math.Max(0, change.RemovedLineBreaks);
        if (change.LineDelta == 0 && change.RemovedLineBreaks == 0 && change.AddedLineBreaks == 0)
        {
            // Single-line edit: evict that line only.
            RemoveLayout(first);
            return;
        }
        // Evict the destroyed range, then shift later keys by the delta.
        var shifted = new List<(long Line, LinkedListNode<(long, LineEntry)> Node)>();
        var toRemove = new List<long>();
        foreach (var kv in _layoutMap)
        {
            if (kv.Key >= first && kv.Key <= lastRemoved) toRemove.Add(kv.Key);
            else if (kv.Key > lastRemoved) shifted.Add((kv.Key, kv.Value));
        }
        foreach (var line in toRemove) RemoveLayout(line);
        foreach (var (line, node) in shifted) _layoutMap.Remove(line);
        foreach (var (line, node) in shifted)
        {
            long newLine = line + change.LineDelta;
            node.ValueRef = (newLine, node.Value.Item2);
            _layoutMap[newLine] = node;
        }
    }

    private void RemoveLayout(long line)
    {
        if (_layoutMap.Remove(line, out var node))
        {
            node.Value.Item2.Layout.Dispose();
            _layoutLru.Remove(node);
        }
    }

    /// <summary>
    /// The layout for a logical line, building on miss. Returns null for lines
    /// not yet materialised (beyond the indexing frontier) or with no document.
    /// </summary>
    private LineEntry? GetLayout(long line)
    {
        var doc = _doc;
        if (doc is null || _textFormat is null) return null;
        if (line < 0 || line > doc.IndexedLineCountFloor) return null;

        if (_layoutMap.TryGetValue(line, out var node))
        {
            if (node.Value.Item2.Stamp == _layoutStamp)
            {
                _layoutLru.Remove(node);
                _layoutLru.AddFirst(node);
                return node.Value.Item2;
            }
            RemoveLayout(line);
        }

        LineSlice slice;
        try { slice = doc.GetLine(line); }
        catch (ArgumentOutOfRangeException) { return null; }
        string text = slice.Text.ToString();

        float width = _wrap ? WrapWidth : 1_000_000f;
        var layout = new CanvasTextLayout(_canvas, text, _textFormat, width, 0)
        {
            WordWrapping = _wrap ? CanvasWordWrapping.Wrap : CanvasWordWrapping.NoWrap,
        };
        int rows = Math.Max(1, layout.LineMetrics.Length);
        var entry = new LineEntry { Layout = layout, RowCount = rows, Stamp = _layoutStamp, TextLength = text.Length };

        double w = layout.LayoutBounds.Width + PadLeft * 2;
        if (w > _maxLineWidthPx) { _maxLineWidthPx = w; UpdateScrollRange(); }

        var added = _layoutLru.AddFirst((line, entry));
        _layoutMap[line] = added;
        while (_layoutLru.Count > LayoutCacheCapacity && _layoutLru.Last is { } last)
        {
            _layoutMap.Remove(last.Value.Line);
            last.Value.Entry.Layout.Dispose();
            _layoutLru.RemoveLast();
        }
        return entry;
    }

    private float WrapWidth => (float)Math.Max(16, _canvas.ActualWidth - PadLeft - 6);

    // ── Wrap oracle (RowWalker's view of the document) ───────────────────────

    private sealed class WrapOracle(TextEditorControl owner) : ILineWrapOracle
    {
        public long LineCount => owner._doc?.LineCount ?? 1;
        public int RowsOfLine(long line)
        {
            if (!owner._wrap) return 1;
            return owner.GetLayout(line)?.RowCount ?? 1;
        }
    }

    private WrapOracle Oracle => _oracle ??= new WrapOracle(this);
    private WrapOracle? _oracle;

    // ── Metrics ──────────────────────────────────────────────────────────────

    private void RebuildTextFormat()
    {
        _textFormat?.Dispose();
        _textFormat = new CanvasTextFormat
        {
            FontFamily = _fontFamily,
            FontSize = _fontSize,
            FontWeight = _bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = _italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            WordWrapping = CanvasWordWrapping.NoWrap,
        };
        _lineHeight = (float)Math.Ceiling(_fontSize * 1.35f);
    }

    private void EnsureMetrics()
    {
        if (_metricsMeasured || _textFormat is null) return;
        using var probe = new CanvasTextLayout(_canvas, "0jgÅ", _textFormat, 0, 0);
        _lineHeight = (float)Math.Ceiling(probe.LayoutBounds.Height);
        if (_lineHeight < 4) _lineHeight = (float)Math.Ceiling(_fontSize * 1.35f);
        // Pin uniform row height so wrap rows, selection boxes and the caret all
        // share one grid even when font fallback kicks in.
        _textFormat.LineSpacing = _lineHeight;
        _textFormat.LineSpacingBaseline = _lineHeight * 0.8f;
        _metricsMeasured = true;
        _layoutStamp++; // probe may change the effective row height
        UpdateScrollRange();
    }

    // ── Draw ─────────────────────────────────────────────────────────────────

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(_bgColor);
        var doc = _doc;
        if (_textFormat is null)
        {
            MarkPerfDraw(doc);
            return;
        }
        EnsureMetrics();

        double viewH = sender.ActualHeight;
        float baseX = PadLeft - (float)_scrollX;
        long selStart = SelectionStart, selEnd = SelectionStart + SelectionLength;

        var anchor = _anchorView;
        long line = Math.Clamp(anchor.Line, 0, Math.Max(0, (doc?.LineCount ?? 1) - 1));
        float y = PadTop - anchor.PixelDelta - anchor.SubRow * _lineHeight;

        while (y < viewH && doc is not null && line < doc.LineCount)
        {
            var entry = GetLayout(line);
            if (entry is null)
            {
                // Beyond the indexing frontier: nothing to draw yet.
                y += _lineHeight;
                line++;
                continue;
            }

            // Selection highlight for the part of this line inside the selection.
            if (selEnd > selStart)
            {
                long lineStart = doc.GetOffsetForLine(line);
                long lineEnd = lineStart + entry.TextLength;
                if (selEnd > lineStart && selStart <= lineEnd)
                {
                    int relStart = (int)Math.Clamp(selStart - lineStart, 0, entry.TextLength);
                    int relEnd = (int)Math.Clamp(selEnd - lineStart, 0, entry.TextLength);
                    if (relEnd > relStart)
                    {
                        foreach (var region in entry.Layout.GetCharacterRegions(relStart, relEnd - relStart))
                        {
                            ds.FillRectangle(
                                baseX + (float)region.LayoutBounds.X,
                                y + (float)region.LayoutBounds.Y,
                                (float)region.LayoutBounds.Width,
                                (float)region.LayoutBounds.Height,
                                _selColor);
                        }
                    }
                    // Half-char stub when the selection spans this line's break.
                    if (selEnd > lineEnd && lineEnd < doc.Length)
                    {
                        var caretPos = CaretXyInLayout(entry, entry.TextLength);
                        ds.FillRectangle(
                            baseX + caretPos.X, y + caretPos.Y,
                            _fontSize * 0.5f, _lineHeight, _selColor);
                    }
                }
            }

            ds.DrawTextLayout(entry.Layout, baseX, y, _textColor);
            y += entry.RowCount * _lineHeight;
            line++;
        }

        MarkPerfDraw(doc);
    }

    private void MarkPerfDraw(Document? doc)
    {
        if (!Diagnostics.Perf.Enabled) return;
        if (!_firstDrawMarked)
        {
            _firstDrawMarked = true;
            Diagnostics.Perf.Mark("FirstCanvasDraw");
        }
        if (!_firstTextDrawMarked && (doc?.Length ?? 0) > 0)
        {
            _firstTextDrawMarked = true;
            Diagnostics.Perf.Mark("FirstTextDraw");
        }
        if (_pendingKeystrokeId >= 0)
        {
            Diagnostics.Perf.KeystrokeDrawn(_pendingKeystrokeId);
            _pendingKeystrokeId = -1;
        }
    }

    // ── Geometry via layouts ─────────────────────────────────────────────────

    /// <summary>Caret (x, y) within a line's layout for a line-relative char index.</summary>
    private System.Numerics.Vector2 CaretXyInLayout(LineEntry entry, int index)
    {
        index = Math.Clamp(index, 0, entry.TextLength);
        if (entry.TextLength == 0) return default;
        return index == 0
            ? entry.Layout.GetCaretPosition(0, false)
            : entry.Layout.GetCaretPosition(index - 1, true);
    }

    /// <summary>Caret position as (line, subRow, x-in-layout).</summary>
    private (long Line, int SubRow, float X) CaretDisplayPos(long offset)
    {
        var doc = _doc;
        if (doc is null) return (0, 0, 0);
        var (line, col) = doc.GetLineColumn(offset);
        var entry = GetLayout(line);
        if (entry is null) return (line, 0, 0);
        var xy = CaretXyInLayout(entry, (int)Math.Min(col, entry.TextLength));
        int subRow = Math.Clamp((int)(xy.Y / Math.Max(1f, _lineHeight)), 0, entry.RowCount - 1);
        return (line, subRow, xy.X);
    }

    /// <summary>Maps canvas-relative pixels to a document offset (layout hit test).</summary>
    private long HitTestOffset(double canvasX, double canvasY)
    {
        var doc = _doc;
        if (doc is null) return 0;

        // Resolve the display row under the pointer by walking from the anchor.
        double yFromTop = canvasY - PadTop + _anchorView.PixelDelta;
        long rowDelta = (long)Math.Floor(yFromTop / Math.Max(1f, _lineHeight));
        var target = RowWalker.Walk(_anchorView with { PixelDelta = 0 }, _anchorView.SubRow + rowDelta, Oracle);
        // Walk starts at (line, subRow=anchor.SubRow); normalise: we want delta from the anchor's own row.
        target = RowWalker.Walk(new ViewportAnchor(_anchorView.Line, _anchorView.SubRow, 0), rowDelta, Oracle);

        var entry = GetLayout(target.Line);
        long lineStart = target.Line <= doc.IndexedLineCountFloor ? doc.GetOffsetForLine(target.Line) : doc.Length;
        if (entry is null) return Math.Min(lineStart, doc.Length);

        float x = (float)(canvasX - PadLeft + _scrollX);
        float y = (target.SubRow + 0.5f) * _lineHeight;
        int col;
        if (entry.Layout.HitTest(x, y, out var region))
        {
            col = region.CharacterIndex;
            // Snap to the trailing side when the pointer is past the char's midpoint.
            if (x > region.LayoutBounds.X + region.LayoutBounds.Width / 2)
                col += Math.Max(1, region.CharacterCount);
        }
        else
        {
            // Past the end of the row: land after the last char of that sub-row.
            col = RowEndColumn(entry, target.SubRow);
        }
        col = Math.Clamp(col, 0, entry.TextLength);
        return doc.SnapCaret(lineStart + col, SnapDirection.Left);
    }

    /// <summary>Line-relative column just after the last char of a sub-row.</summary>
    private int RowEndColumn(LineEntry entry, int subRow)
    {
        var metrics = entry.Layout.LineMetrics;
        int col = 0;
        for (int i = 0; i <= subRow && i < metrics.Length; i++)
        {
            int count = metrics[i].CharacterCount;
            if (i == subRow)
            {
                // Exclude the trailing wrap/whitespace position so the caret sits
                // at the visual end of the row.
                return Math.Min(entry.TextLength, col + count);
            }
            col += count;
        }
        return entry.TextLength;
    }

    /// <summary>Line-relative column of the first char of a sub-row.</summary>
    private int RowStartColumn(LineEntry entry, int subRow)
    {
        var metrics = entry.Layout.LineMetrics;
        int col = 0;
        for (int i = 0; i < subRow && i < metrics.Length; i++) col += metrics[i].CharacterCount;
        return Math.Min(col, entry.TextLength);
    }

    // ── Caret overlay ────────────────────────────────────────────────────────

    private void UpdateCaretOverlay()
    {
        var doc = _doc;
        if (doc is null || !_hasFocus || SelectionLength != 0)
        {
            _caretRect.Visibility = Visibility.Collapsed;
            return;
        }
        var (line, subRow, x) = CaretDisplayPos(_caret);
        var dist = RowWalker.TryDistance(_anchorView with { PixelDelta = 0 },
            new ViewportAnchor(line, subRow, 0), ViewportRows + 2, Oracle);
        if (dist is null || dist < 0 || dist > ViewportRows + 1)
        {
            _caretRect.Visibility = Visibility.Collapsed; // off-screen
            return;
        }
        double cx = PadLeft - _scrollX + x;
        double cy = PadTop - _anchorView.PixelDelta + dist.Value * _lineHeight;
        if (cx < -2 || cx > _canvas.ActualWidth + 2)
        {
            _caretRect.Visibility = Visibility.Collapsed;
            return;
        }
        _caretRect.Margin = new Thickness(cx, cy, 0, 0);
        _caretRect.Height = _lineHeight;
        _caretRect.Fill ??= new Microsoft.UI.Xaml.Media.SolidColorBrush(_textColor);
        _caretRect.Visibility = Visibility.Visible;
    }

    private void InvalidateView()
    {
        _canvas.Invalidate();
        UpdateCaretOverlay();
    }
}
