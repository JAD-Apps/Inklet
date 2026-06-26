using System;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI;
using Windows.UI.Text.Core;

namespace Inklet.Editor;

/// <summary>
/// A custom, virtualised plain-text editor drawn with Win2D. Unlike the WinUI
/// <c>RichEditBox</c> (which stops painting glyphs past ~512 KB) and a native Win32
/// control (which WinUI's composition occludes), this is a real XAML element, so it
/// composes correctly AND renders documents of any length — it only ever draws the
/// lines visible in the viewport.
///
/// <para>
/// Text is held in <see cref="EditorBuffer"/> (PieceTable + LineIndex + undo/redo) with
/// a single LF line-ending convention internally; the caller normalises to the file's
/// detected ending on save.
/// </para>
/// </summary>
internal sealed partial class TextEditorControl : UserControl
{
    private readonly CanvasControl _canvas = new();
    private readonly ScrollBar _vScroll = new() { Orientation = Orientation.Vertical };
    private readonly ScrollBar _hScroll = new() { Orientation = Orientation.Horizontal };
    private readonly DispatcherTimer _caretTimer = new() { Interval = TimeSpan.FromMilliseconds(530) };

    private EditorBuffer _buffer = new();
    private CanvasTextFormat? _textFormat;

    private float _fontSize = 14f;
    private string _fontFamily = "Consolas";
    private bool _bold, _italic;
    private float _lineHeight = 19f;
    private float _charWidth = 8f;
    private bool _metricsMeasured;

    private double _scrollY, _scrollX, _maxLineWidth;

    private Color _textColor = Colors.Black;
    private Color _bgColor = Colors.White;
    private Color _selColor = Color.FromArgb(0x80, 0x33, 0x99, 0xFF);

    private int _caret;          // offset into the buffer
    private int _anchor;         // selection anchor (== caret when no selection)
    private bool _caretVisible = true;
    private bool _hasFocus;
    private bool _pointerDown;
    private float _desiredColumnX = -1; // sticky X for up/down navigation

    private const float PadLeft = 12f;
    private const float PadTop = 8f;

    /// <summary>Raised after the document text changes (edit, paste, undo, …).</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised after the caret/selection moves.</summary>
    public event EventHandler? SelectionChanged;

    public TextEditorControl()
    {
        var grid = new Grid { Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_bgColor) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(_canvas, 0); Grid.SetColumn(_canvas, 0);
        Grid.SetRow(_vScroll, 0); Grid.SetColumn(_vScroll, 1);
        Grid.SetRow(_hScroll, 1); Grid.SetColumn(_hScroll, 0);
        grid.Children.Add(_canvas);
        grid.Children.Add(_vScroll);
        grid.Children.Add(_hScroll);
        Content = grid;
        _rootGrid = grid;

        IsTabStop = true;
        UseSystemFocusVisuals = false;

        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => UpdateScrollRange();
        _vScroll.Scroll += (_, e) => { _scrollY = e.NewValue; _canvas.Invalidate(); };
        _hScroll.Scroll += (_, e) => { _scrollX = e.NewValue; _canvas.Invalidate(); };

        PointerWheelChanged += OnPointerWheel;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerEntered += (_, _) => ProtectedCursor =
            Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.IBeam);

        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => { _hasFocus = true; StartCaretBlink(); _canvas.Invalidate(); };
        LostFocus += (_, _) => { _hasFocus = false; _caretTimer.Stop(); _caretVisible = false; _canvas.Invalidate(); };

        _caretTimer.Tick += (_, _) => { _caretVisible = !_caretVisible; InvalidateCaret(); };

        RebuildTextFormat();
    }

    private readonly Grid _rootGrid;

    // ── Public API (mirrors what MainWindow needs) ───────────────────────────

    public void SetText(string? text)
    {
        _buffer = new EditorBuffer(NormalizeNewlines(text ?? string.Empty));
        _caret = _anchor = 0;
        _scrollY = _scrollX = 0;
        MeasureWidestLine();
        UpdateScrollRange();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    public string GetText() => _buffer.GetText();

    public int LineCount => _buffer.LineCount;

    public int SelectionStart => Math.Min(_caret, _anchor);
    public int SelectionLength => Math.Abs(_caret - _anchor);

    public (int Line, int Column) CaretLineColumn => _buffer.GetLineColumn(_caret);

    public bool CanUndo => _buffer.CanUndo;
    public bool CanRedo => _buffer.CanRedo;

    public void SetSelection(int start, int length)
    {
        int len = _buffer.Length;
        start = Math.Clamp(start, 0, len);
        int end = Math.Clamp(start + Math.Max(0, length), 0, len);
        _anchor = start;
        _caret = end;
        _desiredColumnX = -1;
        BringCaretIntoView();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    public string GetSelectedText()
        => SelectionLength == 0 ? string.Empty : _buffer.GetText(SelectionStart, SelectionLength);

    public void SetColors(Color text, Color background, Color selection)
    {
        _textColor = text;
        _bgColor = background;
        _selColor = selection;
        _rootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_bgColor);
        _canvas.Invalidate();
    }

    public void SetFont(string family, float sizePt, bool bold, bool italic)
    {
        _fontFamily = family;
        _fontSize = sizePt;
        _bold = bold;
        _italic = italic;
        RebuildTextFormat();
        _metricsMeasured = false;
        MeasureWidestLine();
        UpdateScrollRange();
        _canvas.Invalidate();
    }

    public void ScrollToLine(int line)
    {
        _scrollY = Math.Max(0, (line - 1) * _lineHeight);
        UpdateScrollRange();
        _canvas.Invalidate();
    }

    public new void Focus() => Focus(FocusState.Programmatic);

    // ── Editing ──────────────────────────────────────────────────────────────

    private void InsertText(string text)
    {
        text = NormalizeNewlines(text);
        if (SelectionLength > 0) DeleteSelectionInternal();
        _buffer.Insert(_caret, text);
        _caret += text.Length;
        _anchor = _caret;
        AfterEdit();
    }

    private void DeleteSelectionInternal()
    {
        int s = SelectionStart, l = SelectionLength;
        _buffer.Delete(s, l);
        _caret = _anchor = s;
    }

    private void Backspace()
    {
        if (SelectionLength > 0) { DeleteSelectionInternal(); AfterEdit(); return; }
        if (_caret == 0) return;
        // Treat CRLF as one unit defensively (buffer is LF, so this is just one char).
        int removeStart = _caret - 1;
        _buffer.Delete(removeStart, 1);
        _caret = _anchor = removeStart;
        AfterEdit();
    }

    private void DeleteForward()
    {
        if (SelectionLength > 0) { DeleteSelectionInternal(); AfterEdit(); return; }
        if (_caret >= _buffer.Length) return;
        _buffer.Delete(_caret, 1);
        AfterEdit();
    }

    private void DoUndo()
    {
        var pos = _buffer.Undo();
        if (pos is int p) { _caret = _anchor = Math.Clamp(p, 0, _buffer.Length); AfterEdit(); }
    }

    private void DoRedo()
    {
        var pos = _buffer.Redo();
        if (pos is int p) { _caret = _anchor = Math.Clamp(p, 0, _buffer.Length); AfterEdit(); }
    }

    private void AfterEdit()
    {
        _desiredColumnX = -1;
        MeasureWidestLine();
        UpdateScrollRange();
        BringCaretIntoView();
        _canvas.Invalidate();
        TextChanged?.Invoke(this, EventArgs.Empty);
        RaiseSelectionChanged();
    }

    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        char c = args.Character;
        // Tab is the only control char accepted as text; the rest (backspace, CR/LF,
        // arrows, …) are handled in KeyDown.
        if (c != '\t' && char.IsControl(c)) return;
        InsertText(c.ToString());
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool handled = true;

        switch (e.Key)
        {
            case VirtualKey.Left: MoveCaret(PrevPosition(ctrl), shift); break;
            case VirtualKey.Right: MoveCaret(NextPosition(ctrl), shift); break;
            case VirtualKey.Up: MoveVertical(-1, shift); break;
            case VirtualKey.Down: MoveVertical(1, shift); break;
            case VirtualKey.Home: MoveCaret(ctrl ? 0 : LineStart(_caret), shift); break;
            case VirtualKey.End: MoveCaret(ctrl ? _buffer.Length : LineEnd(_caret), shift); break;
            case VirtualKey.PageUp: MovePage(-1, shift); break;
            case VirtualKey.PageDown: MovePage(1, shift); break;
            case VirtualKey.Back: Backspace(); break;
            case VirtualKey.Delete: DeleteForward(); break;
            case VirtualKey.Enter: InsertText("\n"); break;
            case VirtualKey.Tab: InsertText("\t"); break;
            case VirtualKey.A when ctrl: SetSelection(0, _buffer.Length); break;
            case VirtualKey.Z when ctrl: DoUndo(); break;
            case VirtualKey.Y when ctrl: DoRedo(); break;
            case VirtualKey.C when ctrl: CopySelection(); break;
            case VirtualKey.X when ctrl: CutSelection(); break;
            case VirtualKey.V when ctrl: _ = PasteAsync(); break;
            default: handled = false; break;
        }

        if (handled) e.Handled = true;
    }

    private static bool IsDown(VirtualKey key)
        => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

    private void MoveCaret(int newPos, bool extend)
    {
        _caret = Math.Clamp(newPos, 0, _buffer.Length);
        if (!extend) _anchor = _caret;
        _desiredColumnX = -1;
        BringCaretIntoView();
        ResetCaretBlink();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    private void MoveVertical(int dir, bool extend)
    {
        var (line, _) = _buffer.GetLineColumn(_caret);
        if (_desiredColumnX < 0)
        {
            int ls = LineStart(_caret);
            _desiredColumnX = (_caret - ls) * _charWidth;
        }
        int targetLine = Math.Clamp(line + dir, 1, _buffer.LineCount);
        int targetStart = _buffer.GetOffsetForLine(targetLine);
        int targetLen = LineEnd(targetStart) - targetStart;
        int col = (int)Math.Round(_desiredColumnX / _charWidth);
        col = Math.Clamp(col, 0, targetLen);
        _caret = targetStart + col;
        if (!extend) _anchor = _caret;
        BringCaretIntoView();
        ResetCaretBlink();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    private void MovePage(int dir, bool extend)
    {
        int linesPerPage = Math.Max(1, (int)(_canvas.ActualHeight / _lineHeight) - 1);
        var (line, _) = _buffer.GetLineColumn(_caret);
        int targetLine = Math.Clamp(line + dir * linesPerPage, 1, _buffer.LineCount);
        int targetStart = _buffer.GetOffsetForLine(targetLine);
        int targetLen = LineEnd(targetStart) - targetStart;
        var (_, col) = _buffer.GetLineColumn(_caret);
        _caret = targetStart + Math.Min(col - 1, targetLen);
        if (!extend) _anchor = _caret;
        BringCaretIntoView();
        ResetCaretBlink();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    private int LineStart(int offset)
    {
        var (line, _) = _buffer.GetLineColumn(offset);
        return _buffer.GetOffsetForLine(line);
    }

    private int LineEnd(int offset)
    {
        var (line, _) = _buffer.GetLineColumn(offset);
        int nextStart = line < _buffer.LineCount ? _buffer.GetOffsetForLine(line + 1) : _buffer.Length;
        // Trim the trailing newline so End lands before it.
        int end = nextStart;
        if (end > 0 && end <= _buffer.Length)
        {
            string tail = _buffer.GetText(Math.Max(0, end - 1), Math.Min(1, end));
            if (tail == "\n") end -= 1;
        }
        return Math.Max(LineStartOf(line), end);
    }

    private int LineStartOf(int line) => _buffer.GetOffsetForLine(line);

    private int PrevPosition(bool word)
    {
        if (_caret == 0) return 0;
        if (!word) return _caret - 1;
        int p = _caret - 1;
        while (p > 0 && IsWordSep(CharAt(p - 1))) p--;
        while (p > 0 && !IsWordSep(CharAt(p - 1))) p--;
        return p;
    }

    private int NextPosition(bool word)
    {
        int len = _buffer.Length;
        if (_caret >= len) return len;
        if (!word) return _caret + 1;
        int p = _caret;
        while (p < len && !IsWordSep(CharAt(p))) p++;
        while (p < len && IsWordSep(CharAt(p))) p++;
        return p;
    }

    private char CharAt(int i)
    {
        var s = _buffer.GetText(i, 1);
        return s.Length > 0 ? s[0] : '\0';
    }

    private static bool IsWordSep(char c) => c != '_' && !char.IsLetterOrDigit(c);

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(_canvas).Position;
        int pos = HitTest(pt.X, pt.Y);
        bool shift = IsDown(VirtualKey.Shift);
        _caret = pos;
        if (!shift) _anchor = pos;
        _pointerDown = true;
        _canvas.CapturePointer(e.Pointer);
        _desiredColumnX = -1;
        ResetCaretBlink();
        _canvas.Invalidate();
        RaiseSelectionChanged();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var pt = e.GetCurrentPoint(_canvas).Position;
        _caret = HitTest(pt.X, pt.Y);
        BringCaretIntoView();
        _canvas.Invalidate();
        RaiseSelectionChanged();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    private int HitTest(double px, double py)
    {
        int line = (int)((py + _scrollY - PadTop) / _lineHeight) + 1;
        line = Math.Clamp(line, 1, _buffer.LineCount);
        int start = _buffer.GetOffsetForLine(line);
        int lineLen = LineEnd(start) - start;
        int col = (int)Math.Round((px + _scrollX - PadLeft) / _charWidth);
        col = Math.Clamp(col, 0, lineLen);
        return start + col;
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    private void CopySelection()
    {
        if (SelectionLength == 0) return;
        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        pkg.SetText(GetSelectedText().Replace("\n", "\r\n"));
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
    }

    private void CutSelection()
    {
        if (SelectionLength == 0) return;
        CopySelection();
        DeleteSelectionInternal();
        AfterEdit();
    }

    public async System.Threading.Tasks.Task PasteAsync()
    {
        try
        {
            var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text)) return;
            var text = await content.GetTextAsync();
            if (!string.IsNullOrEmpty(text)) InsertText(text);
        }
        catch { /* best-effort */ }
    }

    public void CopyPublic() => CopySelection();
    public void CutPublic() => CutSelection();
    public void SelectAllPublic() => SetSelection(0, _buffer.Length);
    public void UndoPublic() => DoUndo();
    public void RedoPublic() => DoRedo();

    // ── Scrolling / metrics ──────────────────────────────────────────────────

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsHorizontalMouseWheel)
            _scrollX = Math.Clamp(_scrollX - props.MouseWheelDelta, 0, _hScroll.Maximum);
        else
            _scrollY = Math.Clamp(_scrollY - props.MouseWheelDelta, 0, _vScroll.Maximum);
        _vScroll.Value = _scrollY;
        _hScroll.Value = _scrollX;
        _canvas.Invalidate();
        e.Handled = true;
    }

    private void BringCaretIntoView()
    {
        var (line, _) = _buffer.GetLineColumn(_caret);
        double caretTop = PadTop + (line - 1) * _lineHeight;
        double caretBottom = caretTop + _lineHeight;
        double viewH = _canvas.ActualHeight;
        if (caretTop < _scrollY) _scrollY = caretTop;
        else if (caretBottom > _scrollY + viewH) _scrollY = caretBottom - viewH;

        int lineStart = _buffer.GetOffsetForLine(line);
        double caretX = PadLeft + (_caret - lineStart) * _charWidth;
        double viewW = _canvas.ActualWidth;
        if (caretX - PadLeft < _scrollX) _scrollX = Math.Max(0, caretX - PadLeft);
        else if (caretX + _charWidth > _scrollX + viewW) _scrollX = caretX + _charWidth - viewW;

        UpdateScrollRange();
    }

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
        _charWidth = _fontSize * 0.6f;
    }

    private void MeasureWidestLine()
    {
        int widest = 0, lines = _buffer.LineCount;
        for (int line = 1; line <= lines && line <= 5000; line++)
        {
            int start = _buffer.GetOffsetForLine(line);
            int end = line < lines ? _buffer.GetOffsetForLine(line + 1) : _buffer.Length;
            if (end - start > widest) widest = end - start;
        }
        _maxLineWidth = widest * _charWidth + PadLeft * 2;
    }

    private void UpdateScrollRange()
    {
        double contentH = _buffer.LineCount * _lineHeight + PadTop * 2;
        double viewH = _canvas.ActualHeight;
        double vMax = Math.Max(0, contentH - viewH);
        _vScroll.Minimum = 0; _vScroll.Maximum = vMax;
        _vScroll.ViewportSize = viewH;
        _vScroll.LargeChange = Math.Max(_lineHeight, viewH - _lineHeight);
        _vScroll.SmallChange = _lineHeight * 3;
        _vScroll.Visibility = vMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _scrollY = Math.Clamp(_scrollY, 0, vMax);
        _vScroll.Value = _scrollY;

        double viewW = _canvas.ActualWidth;
        double hMax = Math.Max(0, _maxLineWidth - viewW);
        _hScroll.Minimum = 0; _hScroll.Maximum = hMax;
        _hScroll.ViewportSize = viewW;
        _hScroll.LargeChange = Math.Max(_charWidth, viewW - _charWidth);
        _hScroll.SmallChange = _charWidth * 4;
        _hScroll.Visibility = hMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _scrollX = Math.Clamp(_scrollX, 0, hMax);
        _hScroll.Value = _scrollX;
    }

    // ── Caret blink ──────────────────────────────────────────────────────────

    private void StartCaretBlink() { _caretVisible = true; _caretTimer.Start(); }
    private void ResetCaretBlink() { if (_hasFocus) { _caretTimer.Stop(); _caretVisible = true; _caretTimer.Start(); } }
    private void InvalidateCaret() => _canvas.Invalidate();

    private void RaiseSelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    private static string NormalizeNewlines(string s)
    {
        if (string.IsNullOrEmpty(s) || s.IndexOf('\r') < 0) return s;
        return s.Replace("\r\n", "\n").Replace('\r', '\n');
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(_bgColor);
        if (_textFormat is null) return;

        if (!_metricsMeasured)
        {
            using var probe = new CanvasTextLayout(sender, "0000000000", _textFormat, 0, 0);
            _charWidth = (float)(probe.LayoutBounds.Width / 10.0);
            _lineHeight = (float)Math.Ceiling(probe.LayoutBounds.Height);
            if (_lineHeight < 4) _lineHeight = (float)Math.Ceiling(_fontSize * 1.35f);
            _metricsMeasured = true;
            MeasureWidestLine();
            UpdateScrollRange();
        }

        double viewH = sender.ActualHeight;
        int lineCount = _buffer.LineCount;
        int firstLine = Math.Max(1, (int)((_scrollY - PadTop) / _lineHeight) + 1);
        int linesVisible = (int)(viewH / _lineHeight) + 2;
        int lastLine = Math.Min(lineCount, firstLine + linesVisible);

        int selStart = SelectionStart, selEnd = SelectionStart + SelectionLength;
        float baseX = PadLeft - (float)_scrollX;

        for (int line = firstLine; line <= lastLine; line++)
        {
            int start = _buffer.GetOffsetForLine(line);
            int end = line < lineCount ? _buffer.GetOffsetForLine(line + 1) : _buffer.Length;
            string raw = _buffer.GetText(start, end - start);
            string text = raw.TrimEnd('\n');
            float y = (float)(PadTop + (line - 1) * _lineHeight - _scrollY);

            // Selection highlight for this line.
            if (selEnd > selStart && selEnd > start && selStart < end)
            {
                int s = Math.Max(selStart, start) - start;
                int eSel = Math.Min(selEnd, end) - start;
                bool selectsNewline = selEnd > start + text.Length; // selection spans the line break
                float selX1 = baseX + s * _charWidth;
                float selX2 = baseX + Math.Min(eSel, text.Length) * _charWidth + (selectsNewline ? _charWidth * 0.5f : 0);
                ds.FillRectangle(selX1, y, Math.Max(1, selX2 - selX1), _lineHeight, _selColor);
            }

            if (text.Length > 0)
                ds.DrawText(text, baseX, y, _textColor, _textFormat);
        }

        // Caret.
        if (_hasFocus && _caretVisible && SelectionLength == 0)
        {
            var (cl, _) = _buffer.GetLineColumn(_caret);
            int cls = _buffer.GetOffsetForLine(cl);
            float cx = baseX + (_caret - cls) * _charWidth;
            float cy = (float)(PadTop + (cl - 1) * _lineHeight - _scrollY);
            ds.FillRectangle(cx, cy, 1.6f, _lineHeight, _textColor);
        }
    }
}
