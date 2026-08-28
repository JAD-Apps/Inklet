using System;
using System.Runtime.InteropServices;
using Inklet.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace Inklet.Editor;

internal sealed partial class TextEditorControl
{
    // ── Anchor-based vertical scrolling ──────────────────────────────────────
    //
    // The scrollbar works in LOGICAL LINE units: Value is the anchor line and
    // Maximum is the bottom-most legal anchor line. That is exact when wrap is
    // off (row == line) and line-granular when wrap is on - the thumb maps
    // fractionally onto the document without ever needing a total row count,
    // which is what keeps a 100-million-line file scrollable in O(viewport).

    private int ViewportRows => Math.Max(1, (int)(_canvas.ActualHeight / Math.Max(1f, _lineHeight)));

    private void OnViewportSizeChanged()
    {
        if (_wrap)
        {
            // Wrap width changed: all row counts are stale. Entries rebuild
            // lazily; the anchor keeps its line and clamps its sub-row.
            _layoutStamp++;
            int key = (int)WrapWidth;
            if (key != _lastWrapWidthKey) _lastWrapWidthKey = key;
        }
        UpdateScrollRange();
        InvalidateView();
    }

    private void UpdateScrollRange()
    {
        var doc = _doc;
        long lineCount = doc?.LineCount ?? 1;

        // Bottom clamp: the anchor may not scroll past the last full viewport.
        var bottom = RowWalker.BottomAnchor(ViewportRows, Oracle);
        _anchorView = ClampAnchor(_anchorView, bottom);

        double vMax = Math.Max(0, bottom.Line);
        _vScroll.Minimum = 0;
        _vScroll.Maximum = vMax;
        _vScroll.ViewportSize = Math.Max(1, ViewportRows);
        _vScroll.SmallChange = 3;
        _vScroll.LargeChange = Math.Max(1, ViewportRows - 1);
        _vScroll.Visibility = vMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _vScroll.Value = Math.Min(vMax, _anchorView.Line);

        if (_wrap)
        {
            _hScroll.Maximum = 0;
            _hScroll.Visibility = Visibility.Collapsed;
            _scrollX = 0;
            _hScroll.Value = 0;
            return;
        }

        double viewW = _canvas.ActualWidth;
        double hMax = Math.Max(0, _maxLineWidthPx - viewW);
        _hScroll.Minimum = 0;
        _hScroll.Maximum = hMax;
        _hScroll.ViewportSize = viewW;
        _hScroll.LargeChange = Math.Max(16, viewW - 16);
        _hScroll.SmallChange = _fontSize * 2;
        _hScroll.Visibility = hMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _scrollX = Math.Clamp(_scrollX, 0, hMax);
        _hScroll.Value = _scrollX;
    }

    private static ViewportAnchor ClampAnchor(ViewportAnchor anchor, ViewportAnchor bottom)
    {
        if (anchor.Line < 0) return ViewportAnchor.Origin;
        if (anchor.Line > bottom.Line || (anchor.Line == bottom.Line && anchor.SubRow > bottom.SubRow))
            return bottom;
        return anchor;
    }

    private void OnVScroll(double newValue)
    {
        long line = (long)Math.Round(newValue);
        _anchorView = new ViewportAnchor(Math.Max(0, line), 0, 0);
        UpdateScrollRange();
        InvalidateView();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsHorizontalMouseWheel)
        {
            _scrollX = Math.Clamp(_scrollX - props.MouseWheelDelta, 0, _hScroll.Maximum);
            _hScroll.Value = _scrollX;
        }
        else
        {
            int rows = -Math.Sign(props.MouseWheelDelta) * 3;
            ScrollByRows(rows);
        }
        InvalidateView();
        e.Handled = true;
    }

    private void ScrollByRows(long deltaRows)
    {
        _anchorView = RowWalker.Walk(_anchorView, deltaRows, Oracle);
        UpdateScrollRange();
    }

    /// <summary>
    /// Reveals the caret: nearby targets scroll minimally; far targets teleport
    /// the anchor so the operation stays O(viewport) at any distance.
    /// </summary>
    private void BringCaretIntoView()
    {
        var doc = _doc;
        if (doc is null) return;
        var (line, subRow, x) = CaretDisplayPos(_caret);
        var caretAnchor = new ViewportAnchor(line, subRow, 0);
        int rows = ViewportRows;

        var dist = RowWalker.TryDistance(_anchorView with { PixelDelta = 0 }, caretAnchor, 2L * rows + 4, Oracle);
        if (dist is null)
        {
            // Far away: teleport, caret roughly a third down the view.
            _anchorView = RowWalker.Walk(caretAnchor, -(rows / 3), Oracle);
        }
        else if (dist < 0)
        {
            _anchorView = caretAnchor; // above: caret row to the top
        }
        else if (dist >= rows)
        {
            _anchorView = RowWalker.Walk(caretAnchor, -(rows - 1), Oracle); // below: caret at bottom
        }

        if (_wrap)
        {
            _scrollX = 0;
        }
        else
        {
            double caretX = PadLeft + x;
            double viewW = _canvas.ActualWidth;
            if (viewW > 0)
            {
                if (caretX - PadLeft < _scrollX) _scrollX = Math.Max(0, caretX - PadLeft);
                else if (caretX + 8 > _scrollX + viewW) _scrollX = caretX + 8 - viewW;
            }
        }
        UpdateScrollRange();
    }

    // ── Caret blink (system rate, overlay opacity - zero canvas redraws) ─────

    private DispatcherTimer? _caretTimer;

    [DllImport("user32.dll")]
    private static extern uint GetCaretBlinkTime();

    private void InitCaretBlinkTimer()
    {
        uint ms = GetCaretBlinkTime();
        if (ms == 0 || ms == uint.MaxValue) return; // blinking disabled system-wide
        _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Clamp(ms, 100, 2000)) };
        _caretTimer.Tick += (_, _) => _caretRect.Opacity = _caretRect.Opacity > 0.5 ? 0.0 : 1.0;
    }

    private void StartCaretBlink()
    {
        _caretRect.Opacity = 1.0;
        _caretTimer?.Start();
    }

    private void StopCaretBlink()
    {
        _caretTimer?.Stop();
        _caretRect.Visibility = Visibility.Collapsed;
    }

    private void ResetCaretBlink()
    {
        if (!_hasFocus) return;
        _caretTimer?.Stop();
        _caretRect.Opacity = 1.0;
        _caretTimer?.Start();
    }
}
