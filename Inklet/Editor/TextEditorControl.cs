using System;
using Inklet.Engine;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace Inklet.Editor;

/// <summary>
/// Per-tab view state captured on tab switch and restored on return, so swapping
/// the editor between documents moves no text and loses nothing.
/// </summary>
internal sealed record EditorViewState(ViewportAnchor Anchor, double ScrollX, long Caret, long Anchor2)
{
    public static readonly EditorViewState Default = new(ViewportAnchor.Origin, 0, 0, 0);
}

/// <summary>
/// A custom, virtualised plain-text editor drawn with Win2D. It renders through
/// a per-line CanvasTextLayout cache and scrolls by a document anchor (line,
/// sub-row) rather than absolute pixels, so every frame, keystroke, and scroll
/// is O(viewport) - the document behind it (<see cref="Engine.Document"/>, a
/// piece tree over a memory-mapped file) can be arbitrarily large.
///
/// The control holds no text of its own: <see cref="Document"/> is swapped per
/// tab (undo history and view state live with the document/session, not here).
/// Offsets are native UTF-16 units - CRLF is two chars and the engine's
/// SnapCaret keeps the caret off the middle of CRLF and surrogate pairs.
/// </summary>
internal sealed partial class TextEditorControl : UserControl
{
    private readonly CanvasControl _canvas = new() { IsTabStop = false };
    // These ScrollBars are standalone (no ScrollViewer owns them), so nothing
    // drives the "conscious scrollbar" visual states for us: IndicatorMode must be
    // set explicitly or the template paints nothing, and an explicit thickness is
    // needed or the Auto-sized grid track collapses to zero width. Without both,
    // the bars are invisible however correct Maximum/ViewportSize/Visibility are.
    private const double ScrollBarThickness = 12;

    private readonly ScrollBar _vScroll = new()
    {
        Orientation = Orientation.Vertical,
        IsTabStop = false,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
        Width = ScrollBarThickness,
        MinWidth = ScrollBarThickness,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly ScrollBar _hScroll = new()
    {
        Orientation = Orientation.Horizontal,
        IsTabStop = false,
        IndicatorMode = ScrollingIndicatorMode.MouseIndicator,
        Height = ScrollBarThickness,
        MinHeight = ScrollBarThickness,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Bottom,
    };
    private readonly Microsoft.UI.Xaml.Shapes.Rectangle _caretRect = new() { IsHitTestVisible = false, Width = 1.6 };
    private readonly Grid _rootGrid;

    private Document? _doc;
    private CanvasTextFormat? _textFormat;

    private float _fontSize = 14f;
    private string _fontFamily = "Consolas";
    private bool _bold, _italic;
    private float _lineHeight = 19f;
    private bool _metricsMeasured;
    private bool _firstDrawMarked;
    private bool _firstTextDrawMarked;
    private int _pendingKeystrokeId = -1;

    // Vertical position is a document anchor; horizontal stays in pixels.
    private ViewportAnchor _anchorView = ViewportAnchor.Origin;
    private double _scrollX;
    private double _maxLineWidthPx;      // observed monotonic maximum of built layouts

    private bool _wrap;

    private Color _textColor = Colors.Black;
    private Color _bgColor = Colors.White;
    private Color _selColor = Color.FromArgb(0x80, 0x33, 0x99, 0xFF);

    // IME via CoreTextEditContext (see TextEditorControl.Ime.cs).
    private Windows.UI.Text.Core.CoreTextEditContext? _editContext;
    private bool _ecTried;
    private bool _imeComposing;
    private bool _inEcCallback;
    private IntPtr _windowHwnd;

    private long _caret;
    private long _anchor;                // selection anchor (== caret when no selection)
    private bool _hasFocus;
    private bool _pointerDown;
    private float _desiredColumnX = -1;  // sticky X for up/down navigation

    private const float PadLeft = 12f;
    private const float PadTop = 8f;

    /// <summary>Raised after the document text changes (edit, paste, undo, ...).</summary>
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
        Grid.SetRow(_caretRect, 0); Grid.SetColumn(_caretRect, 0);
        _caretRect.HorizontalAlignment = HorizontalAlignment.Left;
        _caretRect.VerticalAlignment = VerticalAlignment.Top;
        _caretRect.Visibility = Visibility.Collapsed;
        grid.Children.Add(_canvas);
        grid.Children.Add(_caretRect);
        grid.Children.Add(_vScroll);
        grid.Children.Add(_hScroll);
        Content = grid;
        _rootGrid = grid;

        IsTabStop = true;
        UseSystemFocusVisuals = false;
        AllowDrop = true; // MainWindow wires DragOver/Drop to open dropped files

        _canvas.Draw += OnDraw;
        _canvas.SizeChanged += (_, _) => OnViewportSizeChanged();
        _vScroll.Scroll += (_, e) => OnVScroll(e.NewValue);
        _hScroll.Scroll += (_, e) => { _scrollX = e.NewValue; InvalidateView(); };

        PointerWheelChanged += OnPointerWheel;
        _canvas.PointerPressed += OnPointerPressed;
        _canvas.PointerMoved += OnPointerMoved;
        _canvas.PointerReleased += OnPointerReleased;
        _canvas.PointerEntered += (_, _) => ProtectedCursor =
            Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.IBeam);

        KeyDown += OnKeyDown;
        CharacterReceived += OnCharacterReceived;
        GotFocus += (_, _) => { _hasFocus = true; EnsureEditContext(); _editContext?.NotifyFocusEnter(); StartCaretBlink(); UpdateCaretOverlay(); };
        LostFocus += (_, _) => { _hasFocus = false; _editContext?.NotifyFocusLeave(); _imeComposing = false; StopCaretBlink(); };

        InitCaretBlinkTimer();
        RebuildTextFormat();
    }

    // ── Document binding ─────────────────────────────────────────────────────

    /// <summary>
    /// The document shown/edited. Swapping is O(1): caches reset, no text moves.
    /// The caller restores per-tab view state separately (RestoreViewState).
    /// </summary>
    public Document? Document
    {
        get => _doc;
        set
        {
            if (ReferenceEquals(_doc, value)) return;
            if (_doc is not null) _doc.Changed -= OnDocChanged;
            _doc = value;
            if (_doc is not null) _doc.Changed += OnDocChanged;
            _caret = _anchor = 0;
            _anchorView = ViewportAnchor.Origin;
            _scrollX = 0;
            _maxLineWidthPx = 0;
            _desiredColumnX = -1;
            ResetLayoutCache();
            UpdateScrollRange();
            NotifyImeDocumentReset();
            InvalidateView();
            RaiseSelectionChanged();
        }
    }

    private void OnDocChanged(TextChange change)
    {
        // The engine raises Changed synchronously from inside the mutation, BEFORE
        // the edit site has repositioned _caret/_anchor - a delete at the end of
        // the document briefly leaves them past the new length. Anything below
        // (and any draw this triggers) must see in-range positions, or geometry
        // lookups throw inside a XAML callback and take the process down with a
        // stowed exception (0xC000027B).
        long len = _doc?.Length ?? 0;
        _caret = Math.Clamp(_caret, 0, len);
        _anchor = Math.Clamp(_anchor, 0, len);

        _lastChangeForIme = change;
        ApplyChangeToLayoutCache(change);
        // Keep the view anchored to the same content when lines shift above it.
        if (change.LineDelta != 0 && change.FirstAffectedLine < _anchorView.Line)
        {
            long newLine = Math.Max(change.FirstAffectedLine, _anchorView.Line + change.LineDelta);
            _anchorView = _anchorView with { Line = newLine };
        }
        UpdateScrollRange();
        InvalidateView();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Captures the current view for the outgoing tab.</summary>
    public EditorViewState CaptureViewState()
        => new(_anchorView, _scrollX, _caret, _anchor);

    /// <summary>Restores a previously captured view for the incoming tab.</summary>
    public void RestoreViewState(EditorViewState state)
    {
        // AddressableLength, not Length: a session-restored caret can sit far beyond
        // the indexer's absorbed frontier, and every geometry call below would throw.
        long len = _doc?.AddressableLength ?? 0;
        _caret = Math.Clamp(state.Caret, 0, len);
        _anchor = Math.Clamp(state.Anchor2, 0, len);
        _anchorView = state.Anchor;
        _scrollX = state.ScrollX;
        _desiredColumnX = -1;
        UpdateScrollRange();
        InvalidateView();
        RaiseSelectionChanged();
    }

    // ── Selection / caret API (long offsets, snap-aware) ─────────────────────

    public long SelectionStart => Math.Min(_caret, _anchor);
    public long SelectionLength => Math.Abs(_caret - _anchor);

    public (long Line, long Column) CaretLineColumn
        => _doc is null ? (0, 0) : _doc.GetLineColumn(Math.Clamp(_caret, 0, _doc.AddressableLength));

    public long LineCount => _doc?.LineCount ?? 1;
    public bool IsLineCountExact => _doc?.IsLineCountExact ?? true;
    public long DocumentLength => _doc?.Length ?? 0;

    public bool CanUndo => _doc?.CanUndo ?? false;
    public bool CanRedo => _doc?.CanRedo ?? false;

    public void SetSelection(long start, long length)
    {
        if (_doc is null) return;
        long len = _doc.AddressableLength;
        start = _doc.SnapCaret(Math.Clamp(start, 0, len), SnapDirection.Left);
        long end = _doc.SnapCaret(Math.Clamp(start + Math.Max(0, length), 0, len), SnapDirection.Right);
        _anchor = start;
        _caret = end;
        _desiredColumnX = -1;
        _doc.SealUndoCoalescing();
        BringCaretIntoView();
        InvalidateView();
        RaiseSelectionChanged();
    }

    /// <summary>Selected text. Refuses selections too large to materialise.</summary>
    public string GetSelectedText()
    {
        if (_doc is null || SelectionLength == 0) return string.Empty;
        if (SelectionLength > MaxClipboardChars)
            throw new InvalidOperationException("Selection too large to copy.");
        return _doc.GetText(SelectionStart, SelectionLength);
    }

    internal const long MaxClipboardChars = 256L * 1024 * 1024;

    public void SetColors(Color text, Color background, Color selection)
    {
        _textColor = text;
        _bgColor = background;
        _selColor = selection;
        _rootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(_bgColor);
        _caretRect.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(_textColor);
        InvalidateView();
    }

    public void SetFont(string family, float sizePt, bool bold, bool italic)
    {
        _fontFamily = family;
        _fontSize = sizePt;
        _bold = bold;
        _italic = italic;
        RebuildTextFormat();
        _metricsMeasured = false;
        _maxLineWidthPx = 0;
        ResetLayoutCache();
        UpdateScrollRange();
        InvalidateView();
    }

    public void Focus() => Focus(FocusState.Programmatic);

    /// <summary>Supplies the top-level window handle (used for client&lt;-&gt;screen mapping).</summary>
    public void SetWindowHandle(IntPtr hwnd)
    {
        _windowHwnd = hwnd;
        EnsureEditContext();
    }

    /// <summary>Wraps long lines to the viewport width (and hides the horizontal scrollbar).</summary>
    public bool WordWrap
    {
        get => _wrap;
        set
        {
            if (_wrap == value) return;
            _wrap = value;
            _scrollX = 0;
            _anchorView = _anchorView with { SubRow = 0, PixelDelta = 0 };
            ResetLayoutCache();
            UpdateScrollRange();
            BringCaretIntoView();
            InvalidateView();
        }
    }

    /// <summary>Moves the caret to the start of <paramref name="line"/> (1-based) and reveals it.</summary>
    public void GoToLine(long line)
    {
        if (_doc is null) return;
        line = Math.Clamp(line, 1, _doc.LineCount);
        long target = Math.Min(line - 1, _doc.IndexedLineCountFloor);
        SetSelection(_doc.GetOffsetForLine(target), 0);
        Focus();
    }

    // ── Editing ──────────────────────────────────────────────────────────────

    private void InsertText(string text)
    {
        if (_doc is null) return;
        long s = SelectionStart, oldLen = SelectionLength;
        if (!_doc.IsEditableRange(s, oldLen)) return; // beyond the indexing frontier
        if (oldLen > 0)
        {
            _doc.Replace(s, oldLen, text);
            text = _doc.ConvertNewLinesPublic(text);
        }
        else
        {
            _doc.Insert(s, text);
            text = _doc.ConvertNewLinesPublic(text);
        }
        _caret = _anchor = s + text.Length;
        NotifyImeTextChanged(s, oldLen, text.Length);
        AfterEdit();
    }

    private void DeleteRange(long start, long length)
    {
        if (_doc is null || length <= 0) return;
        if (!_doc.IsEditableRange(start, length)) return;
        _doc.Delete(start, length);
        _caret = _anchor = start;
        NotifyImeTextChanged(start, length, 0);
        AfterEdit();
    }

    private void Backspace()
    {
        if (_doc is null) return;
        if (SelectionLength > 0) { DeleteRange(SelectionStart, SelectionLength); return; }
        if (_caret == 0) return;
        long from = _doc.SnapCaret(_caret - 1, SnapDirection.Left);
        DeleteRange(from, _caret - from);
    }

    private void DeleteForward()
    {
        if (_doc is null) return;
        if (SelectionLength > 0) { DeleteRange(SelectionStart, SelectionLength); return; }
        if (_caret >= _doc.Length) return;
        long to = _doc.SnapCaret(_caret + 1, SnapDirection.Right);
        DeleteRange(_caret, to - _caret);
    }

    private void DoUndo()
    {
        if (_doc is null) return;
        var pos = _doc.Undo();
        if (pos is long p)
        {
            _caret = _anchor = Math.Clamp(p, 0, _doc.AddressableLength);
            NotifyImeUndoRedo();
            AfterEdit();
        }
    }

    private void DoRedo()
    {
        if (_doc is null) return;
        var pos = _doc.Redo();
        if (pos is long p)
        {
            _caret = _anchor = Math.Clamp(p, 0, _doc.AddressableLength);
            NotifyImeUndoRedo();
            AfterEdit();
        }
    }

    /// <summary>Post-edit view refresh. The document's Changed event already updated the caches.</summary>
    private void AfterEdit()
    {
        _desiredColumnX = -1;
        BringCaretIntoView();
        ResetCaretBlink();
        InvalidateView();
        RaiseSelectionChanged();
    }

    // ── Facade kept for MainWindow ergonomics ────────────────────────────────

    public void CopyPublic() => CopySelection();
    public void CutPublic() => CutSelection();
    public void SelectAllPublic() { if (_doc is not null) SetSelection(0, _doc.Length); }
    public void UndoPublic() => DoUndo();
    public void RedoPublic() => DoRedo();
    public void DocumentSelectAll() => SelectAllPublic();
    public void DocumentUndo() => UndoPublic();
    public void DocumentRedo() => RedoPublic();
    public void CutPlainSelection() => CutPublic();
    public void CopyPlainSelection() => CopyPublic();
    public System.Threading.Tasks.Task PastePlainAsync() => PasteAsync();

    /// <summary>Inserts text at the caret (replacing any selection), e.g. Time/Date.</summary>
    public void InsertAtCaret(string text) => InsertText(text);

    /// <summary>Deletes the current selection (menu Edit &gt; Delete).</summary>
    public void DeleteSelection()
    {
        if (SelectionLength == 0) return;
        DeleteRange(SelectionStart, SelectionLength);
    }

    private void RaiseSelectionChanged()
    {
        NotifyImeSelectionChanged();
        UpdateCaretOverlay();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
