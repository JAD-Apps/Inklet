using System;
using System.Collections.Generic;
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
    private readonly CanvasControl _canvas = new() { IsTabStop = false };
    private readonly ScrollBar _vScroll = new() { Orientation = Orientation.Vertical, IsTabStop = false };
    private readonly ScrollBar _hScroll = new() { Orientation = Orientation.Horizontal, IsTabStop = false };
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

    // Word wrap: each logical line can occupy several display rows. _rowsBeforeLine is a
    // prefix sum — _rowsBeforeLine[L-1] is the first display row of logical line L, and
    // _rowsBeforeLine[lineCount] is the total display-row count. Rebuilt lazily (it is
    // O(N)) when the text, font, viewport width or wrap setting changes.
    private bool _wrap;
    private readonly List<int> _rowsBeforeLine = new() { 0 };
    private readonly List<int> _wrapScratch = new();
    private bool _wrapMapDirty = true;
    private int _cpr = 1; // chars per display row (cached)

    private Color _textColor = Colors.Black;
    private Color _bgColor = Colors.White;
    private Color _selColor = Color.FromArgb(0x80, 0x33, 0x99, 0xFF);

    // IME via CoreTextEditContext — the WinUI 3 desktop text-services hook (see
    // TextEditorControl.Ime.cs). Created lazily on first focus. Plain Latin typing still
    // arrives via CharacterReceived; the IME drives composition/commit through the
    // edit-context events. _inEcCallback guards against echoing IME edits back as notifications.
    private Windows.UI.Text.Core.CoreTextEditContext? _editContext;
    private bool _ecTried;
    private bool _imeComposing;  // true between CompositionStarted and CompositionCompleted
    private bool _inEcCallback;  // true while applying an IME-initiated edit
    private IntPtr _windowHwnd;  // top-level window (for client<->screen mapping)

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
        AllowDrop = true; // MainWindow wires DragOver/Drop to open dropped files

        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => { UpdateScrollRange(); _canvas.Invalidate(); };
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
        GotFocus += (_, _) => { _hasFocus = true; EnsureEditContext(); _editContext?.NotifyFocusEnter(); StartCaretBlink(); _canvas.Invalidate(); };
        LostFocus += (_, _) => { _hasFocus = false; _editContext?.NotifyFocusLeave(); _imeComposing = false; _caretTimer.Stop(); _caretVisible = false; _canvas.Invalidate(); };

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
        _wrapMapDirty = true;
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
        _wrapMapDirty = true;
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

    public void Focus() => Focus(FocusState.Programmatic);

    /// <summary>Supplies the top-level window handle (used for client&lt;-&gt;screen mapping).</summary>
    public void SetWindowHandle(IntPtr hwnd)
    {
        _windowHwnd = hwnd;
        EnsureEditContext(); // the HWND may arrive after first focus; set up now that we have it
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void InsertText(string text)
    {
        text = NormalizeNewlines(text);
        int s = SelectionStart, oldLen = SelectionLength;
        if (oldLen > 0) _buffer.Delete(s, oldLen);
        _buffer.Insert(s, text);
        _caret = _anchor = s + text.Length;
        CommitAppEdit(s, oldLen, text.Length);
    }

    private void DeleteRange(int start, int length)
    {
        if (length <= 0) return;
        _buffer.Delete(start, length);
        _caret = _anchor = start;
        CommitAppEdit(start, length, 0);
    }

    private void Backspace()
    {
        if (SelectionLength > 0) { DeleteRange(SelectionStart, SelectionLength); return; }
        if (_caret == 0) return;
        DeleteRange(_caret - 1, 1);
    }

    private void DeleteForward()
    {
        if (SelectionLength > 0) { DeleteRange(SelectionStart, SelectionLength); return; }
        if (_caret >= _buffer.Length) return;
        DeleteRange(_caret, 1);
    }

    private void DoUndo()
    {
        int oldLen = _buffer.Length;
        var pos = _buffer.Undo();
        if (pos is int p) { _caret = _anchor = Math.Clamp(p, 0, _buffer.Length); CommitAppEdit(0, oldLen, _buffer.Length); }
    }

    private void DoRedo()
    {
        int oldLen = _buffer.Length;
        var pos = _buffer.Redo();
        if (pos is int p) { _caret = _anchor = Math.Clamp(p, 0, _buffer.Length); CommitAppEdit(0, oldLen, _buffer.Length); }
    }

    /// <summary>App-initiated edit: tell the IME the document changed, then refresh the view.</summary>
    private void CommitAppEdit(int modStart, int modOldLen, int modNewLen)
    {
        if (_editContext is not null && !_inEcCallback)
        {
            var changed = new Windows.UI.Text.Core.CoreTextRange { StartCaretPosition = modStart, EndCaretPosition = modStart + modOldLen };
            _editContext.NotifyTextChanged(changed, modNewLen, CurrentSelectionRange());
        }
        AfterEdit();
    }

    private void AfterEdit()
    {
        _desiredColumnX = -1;
        _wrapMapDirty = true;
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
        // Plain Latin typing arrives here as a normal character; the IME does NOT compose it
        // (TextUpdating never fires for it), so we insert it directly. While an IME composition
        // is in flight, text is delivered through the edit-context events instead — suppress
        // here so we don't double-insert.
        if (_imeComposing) return;

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

    private void MoveVertical(int dir, bool extend) => MoveByRows(dir, extend);

    private void MovePage(int dir, bool extend)
    {
        int rowsPerPage = Math.Max(1, (int)(_canvas.ActualHeight / _lineHeight) - 1);
        MoveByRows(dir * rowsPerPage, extend);
    }

    /// <summary>Moves the caret up/down by display rows, keeping the sticky horizontal column.</summary>
    private void MoveByRows(int deltaRows, bool extend)
    {
        var (row, x) = CaretDisplayPos(_caret);
        if (_desiredColumnX < 0) _desiredColumnX = x;
        int targetRow = Math.Clamp(row + deltaRows, 0, Math.Max(0, TotalRows() - 1));
        _caret = OffsetAtDisplay(targetRow, _desiredColumnX);
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
        int displayRow = Math.Max(0, (int)((py + _scrollY - PadTop) / _lineHeight));
        return OffsetAtDisplay(displayRow, px + _scrollX - PadLeft);
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
        DeleteRange(SelectionStart, SelectionLength);
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

    // ── RichEditBox/RichEditExtensions-compatible API (keeps MainWindow call sites stable) ──
    public string GetPlainText() => GetText();
    public void SetPlainText(string? text) => SetText(text);
    public int GetSelectionStart() => SelectionStart;
    public void SetSelectionStart(int position) => SetSelection(position, SelectionLength);
    public int GetSelectionLength() => SelectionLength;
    public void SetSelectionLength(int length) => SetSelection(SelectionStart, length);
    public void DocumentSelectAll() => SelectAllPublic();
    public void DocumentUndo() => UndoPublic();
    public void DocumentRedo() => RedoPublic();
    public void CutPlainSelection() => CutPublic();
    public void CopyPlainSelection() => CopyPublic();
    public System.Threading.Tasks.Task PastePlainAsync() => PasteAsync();

    /// <summary>Wraps long lines to the viewport width (and hides the horizontal scrollbar).</summary>
    public bool WordWrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value) return;
            _wrap = value;
            _wrapMapDirty = true;
            _scrollX = 0;
            UpdateScrollRange();
            BringCaretIntoView();
            _canvas.Invalidate();
        }
    }

    /// <summary>Inserts text at the caret (replacing any selection), e.g. Time/Date or Replace.</summary>
    public void InsertAtCaret(string text) => InsertText(text);

    /// <summary>Deletes the current selection (menu Edit ▸ Delete with a selection).</summary>
    public void DeleteSelection()
    {
        if (SelectionLength == 0) return;
        DeleteRange(SelectionStart, SelectionLength);
    }

    /// <summary>Moves the caret to the start of <paramref name="line"/> (1-based) and reveals it.</summary>
    public void GoToLine(int line)
    {
        line = Math.Clamp(line, 1, _buffer.LineCount);
        SetSelection(_buffer.GetOffsetForLine(line), 0);
        Focus();
    }

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
        var (row, x) = CaretDisplayPos(_caret);
        double caretTop = PadTop + row * _lineHeight;
        double caretBottom = caretTop + _lineHeight;
        double viewH = _canvas.ActualHeight;
        if (caretTop < _scrollY) _scrollY = caretTop;
        else if (caretBottom > _scrollY + viewH) _scrollY = caretBottom - viewH;

        if (_wrap)
        {
            _scrollX = 0;
        }
        else
        {
            double caretX = PadLeft + x;
            double viewW = _canvas.ActualWidth;
            if (caretX - PadLeft < _scrollX) _scrollX = Math.Max(0, caretX - PadLeft);
            else if (caretX + _charWidth > _scrollX + viewW) _scrollX = caretX + _charWidth - viewW;
        }

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

    // ── Word-wrap mapping ─────────────────────────────────────────────────────

    private string LineText(int line)
    {
        int start = _buffer.GetOffsetForLine(line);
        int end = line < _buffer.LineCount ? _buffer.GetOffsetForLine(line + 1) : _buffer.Length;
        return _buffer.GetText(start, end - start).TrimEnd('\n');
    }

    private int ComputeCharsPerRow()
    {
        double avail = _canvas.ActualWidth - PadLeft - 6;
        return avail < _charWidth ? 1 : Math.Max(1, (int)(avail / Math.Max(1f, _charWidth)));
    }

    /// <summary>
    /// Greedy monospace word wrap: fills <paramref name="rowStarts"/> with the start offset
    /// (relative to the line) of each display row. Breaks after the last space/tab that fits,
    /// or hard-breaks a word longer than the row. Always begins with 0.
    /// </summary>
    private static void WrapLine(string text, int cpr, List<int> rowStarts)
    {
        rowStarts.Clear();
        rowStarts.Add(0);
        int len = text.Length, pos = 0;
        while (pos + cpr < len)
        {
            int limit = pos + cpr;
            int brk = -1;
            for (int j = limit; j > pos; j--)
            {
                char c = text[j - 1];
                if (c == ' ' || c == '\t') { brk = j; break; }
            }
            int next = brk > pos ? brk : limit;
            if (next <= pos) next = pos + 1;
            rowStarts.Add(next);
            pos = next;
        }
    }

    private bool WrapReady => _rowsBeforeLine.Count == _buffer.LineCount + 1;

    private void EnsureWrapMap()
    {
        if (!_wrap || _canvas.ActualWidth <= 1) return;
        int cpr = ComputeCharsPerRow();
        if (!_wrapMapDirty && cpr == _cpr && WrapReady) return;
        _cpr = cpr;
        _wrapMapDirty = false;
        _rowsBeforeLine.Clear();
        int lineCount = _buffer.LineCount, acc = 0;
        for (int line = 1; line <= lineCount; line++)
        {
            _rowsBeforeLine.Add(acc);
            WrapLine(LineText(line), cpr, _wrapScratch);
            acc += _wrapScratch.Count;
        }
        _rowsBeforeLine.Add(acc);
    }

    /// <summary>Total display rows (= logical line count when wrap is off).</summary>
    private int TotalRows()
    {
        if (!_wrap) return _buffer.LineCount;
        EnsureWrapMap();
        return WrapReady ? _rowsBeforeLine[_buffer.LineCount] : _buffer.LineCount;
    }

    /// <summary>Largest logical line whose first display row is &lt;= <paramref name="displayRow"/>.</summary>
    private int LineAtDisplayRow(int displayRow)
    {
        int lc = _buffer.LineCount, lo = 1, hi = lc;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_rowsBeforeLine[mid - 1] <= displayRow) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Maps a caret offset to its display row and x pixel (relative to <c>PadLeft</c>).</summary>
    private (int row, float x) CaretDisplayPos(int offset)
    {
        var (line, _) = _buffer.GetLineColumn(offset);
        int lineStart = _buffer.GetOffsetForLine(line);
        int col = offset - lineStart;
        EnsureWrapMap();
        if (!_wrap || !WrapReady) return (line - 1, col * _charWidth);

        WrapLine(LineText(line), _cpr, _wrapScratch);
        int sub = _wrapScratch.Count - 1;
        for (int i = 1; i < _wrapScratch.Count; i++)
            if (col < _wrapScratch[i]) { sub = i - 1; break; }
        return (_rowsBeforeLine[line - 1] + sub, (col - _wrapScratch[sub]) * _charWidth);
    }

    /// <summary>Maps a display row + x pixel (relative to <c>PadLeft</c>) to a buffer offset.</summary>
    private int OffsetAtDisplay(int displayRow, double xRelToPad)
    {
        int colInRow = Math.Max(0, (int)Math.Round(xRelToPad / Math.Max(1f, _charWidth)));
        EnsureWrapMap();
        if (!_wrap || !WrapReady)
        {
            int line = Math.Clamp(displayRow + 1, 1, _buffer.LineCount);
            int start = _buffer.GetOffsetForLine(line);
            int lineLen = LineEnd(start) - start;
            return start + Math.Clamp(colInRow, 0, lineLen);
        }

        int total = _rowsBeforeLine[_buffer.LineCount];
        displayRow = Math.Clamp(displayRow, 0, Math.Max(0, total - 1));
        int L = LineAtDisplayRow(displayRow);
        int sub = displayRow - _rowsBeforeLine[L - 1];
        string text = LineText(L);
        WrapLine(text, _cpr, _wrapScratch);
        sub = Math.Clamp(sub, 0, _wrapScratch.Count - 1);
        int rowStart = _wrapScratch[sub];
        int rowEnd = sub + 1 < _wrapScratch.Count ? _wrapScratch[sub + 1] : text.Length;
        int col = Math.Clamp(colInRow, 0, rowEnd - rowStart);
        return _buffer.GetOffsetForLine(L) + rowStart + col;
    }

    private void UpdateScrollRange()
    {
        double contentH = TotalRows() * _lineHeight + PadTop * 2;
        double viewH = _canvas.ActualHeight;
        double vMax = Math.Max(0, contentH - viewH);
        _vScroll.Minimum = 0; _vScroll.Maximum = vMax;
        _vScroll.ViewportSize = viewH;
        _vScroll.LargeChange = Math.Max(_lineHeight, viewH - _lineHeight);
        _vScroll.SmallChange = _lineHeight * 3;
        _vScroll.Visibility = vMax > 0 ? Visibility.Visible : Visibility.Collapsed;
        _scrollY = Math.Clamp(_scrollY, 0, vMax);
        _vScroll.Value = _scrollY;

        if (_wrap)
        {
            // Wrapped content never scrolls horizontally.
            _hScroll.Maximum = 0;
            _hScroll.Visibility = Visibility.Collapsed;
            _scrollX = 0; _hScroll.Value = 0;
            return;
        }

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

    private void RaiseSelectionChanged()
    {
        if (_editContext is not null && !_inEcCallback)
            _editContext.NotifySelectionChanged(CurrentSelectionRange());
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

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
            _wrapMapDirty = true; // metrics now accurate -> chars-per-row may change
            MeasureWidestLine();
            UpdateScrollRange();
        }

        double viewH = sender.ActualHeight;
        int lineCount = _buffer.LineCount;
        int selStart = SelectionStart, selEnd = SelectionStart + SelectionLength;
        float baseX = PadLeft - (float)_scrollX;

        int firstRow = Math.Max(0, (int)((_scrollY - PadTop) / _lineHeight));
        int lastRow = firstRow + (int)(viewH / _lineHeight) + 2;

        EnsureWrapMap();
        bool wrap = _wrap && WrapReady;
        int startLine = wrap ? LineAtDisplayRow(Math.Min(firstRow, _rowsBeforeLine[lineCount])) : firstRow + 1;
        startLine = Math.Clamp(startLine, 1, lineCount);
        int displayRow = wrap ? _rowsBeforeLine[startLine - 1] : startLine - 1;

        for (int line = startLine; line <= lineCount; line++)
        {
            int lineStart = _buffer.GetOffsetForLine(line);
            string text = LineText(line);

            if (wrap) WrapLine(text, _cpr, _wrapScratch);
            int rows = wrap ? _wrapScratch.Count : 1;

            for (int sub = 0; sub < rows; sub++, displayRow++)
            {
                if (displayRow < firstRow) continue;
                if (displayRow > lastRow) break;

                int rs = wrap ? _wrapScratch[sub] : 0;
                int re = wrap ? (sub + 1 < rows ? _wrapScratch[sub + 1] : text.Length) : text.Length;
                int rowAbsStart = lineStart + rs, rowAbsEnd = lineStart + re;
                float y = (float)(PadTop + displayRow * _lineHeight - _scrollY);

                // Selection highlight for the part of this row inside the selection. s/e are
                // columns relative to the row start, so each display row begins at baseX.
                if (selEnd > selStart && selEnd > rowAbsStart && selStart <= rowAbsEnd)
                {
                    int s = Math.Max(selStart, rowAbsStart) - rowAbsStart;
                    int e = Math.Min(selEnd, rowAbsEnd) - rowAbsStart;
                    bool selectsBreak = selEnd > rowAbsEnd && re == text.Length; // spans the hard line break
                    float x1 = baseX + s * _charWidth;
                    float x2 = baseX + e * _charWidth + (selectsBreak ? _charWidth * 0.5f : 0);
                    if (x2 > x1) ds.FillRectangle(x1, y, x2 - x1, _lineHeight, _selColor);
                }

                if (re > rs)
                    ds.DrawText(text.Substring(rs, re - rs), baseX, y, _textColor, _textFormat);
            }

            if (displayRow > lastRow) break;
        }

        // Caret.
        if (_hasFocus && _caretVisible && SelectionLength == 0)
        {
            var (crow, cx) = CaretDisplayPos(_caret);
            float caretX = baseX + cx;
            float caretY = (float)(PadTop + crow * _lineHeight - _scrollY);
            ds.FillRectangle(caretX, caretY, 1.6f, _lineHeight, _textColor);
        }
    }
}
