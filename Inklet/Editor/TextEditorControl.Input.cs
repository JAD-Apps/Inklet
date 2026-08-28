using System;
using Inklet.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Inklet.Editor;

internal sealed partial class TextEditorControl
{
    // ── Keyboard ─────────────────────────────────────────────────────────────

    private void OnCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs args)
    {
        // Plain Latin typing arrives here; while an IME composition is in flight
        // text comes through the edit-context events instead (see Ime partial).
        if (_imeComposing) return;

        char c = args.Character;
        if (c != '\t' && char.IsControl(c)) return;
        _pendingKeystrokeId = Diagnostics.Perf.KeystrokeIn();
        InsertText(c.ToString());
        args.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var doc = _doc;
        if (doc is null) return;
        bool ctrl = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        bool handled = true;

        switch (e.Key)
        {
            case VirtualKey.Left: MoveCaret(PrevPosition(ctrl), shift); break;
            case VirtualKey.Right: MoveCaret(NextPosition(ctrl), shift); break;
            case VirtualKey.Up: MoveByRows(-1, shift); break;
            case VirtualKey.Down: MoveByRows(1, shift); break;
            case VirtualKey.Home: MoveCaret(ctrl ? 0 : CaretLineStart(), shift); break;
            case VirtualKey.End: MoveCaret(ctrl ? doc.Length : CaretLineEnd(), shift); break;
            case VirtualKey.PageUp: MoveByRows(-(ViewportRows - 1), shift); break;
            case VirtualKey.PageDown: MoveByRows(ViewportRows - 1, shift); break;
            case VirtualKey.Back: Backspace(); break;
            case VirtualKey.Delete: DeleteForward(); break;
            case VirtualKey.Enter: InsertText(doc.NewLineString); break;
            case VirtualKey.Tab: InsertText("\t"); break;
            case VirtualKey.A when ctrl: SetSelection(0, doc.Length); break;
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

    private void MoveCaret(long newPos, bool extend)
    {
        var doc = _doc;
        if (doc is null) return;
        _caret = Math.Clamp(newPos, 0, doc.Length);
        if (!extend) _anchor = _caret;
        _desiredColumnX = -1;
        doc.SealUndoCoalescing();
        BringCaretIntoView();
        ResetCaretBlink();
        InvalidateView();
        RaiseSelectionChanged();
    }

    /// <summary>Moves the caret by display rows, keeping the sticky horizontal x.</summary>
    private void MoveByRows(long deltaRows, bool extend)
    {
        var doc = _doc;
        if (doc is null) return;
        var (line, subRow, x) = CaretDisplayPos(_caret);
        if (_desiredColumnX < 0) _desiredColumnX = x;

        var target = RowWalker.Walk(new ViewportAnchor(line, subRow, 0), deltaRows, Oracle);
        var entry = GetLayout(target.Line);
        long lineStart = target.Line <= doc.IndexedLineCountFloor ? doc.GetOffsetForLine(target.Line) : doc.Length;
        long newCaret;
        if (entry is null)
        {
            newCaret = Math.Min(lineStart, doc.Length);
        }
        else
        {
            float y = (target.SubRow + 0.5f) * _lineHeight;
            int col = entry.Layout.HitTest(_desiredColumnX, y, out var region)
                ? region.CharacterIndex + (_desiredColumnX > region.LayoutBounds.X + region.LayoutBounds.Width / 2
                    ? Math.Max(1, region.CharacterCount) : 0)
                : RowEndColumn(entry, target.SubRow);
            newCaret = doc.SnapCaret(lineStart + Math.Clamp(col, 0, entry.TextLength), SnapDirection.Left);
        }

        _caret = newCaret;
        if (!extend) _anchor = _caret;
        doc.SealUndoCoalescing();
        BringCaretIntoView();
        ResetCaretBlink();
        InvalidateView();
        RaiseSelectionChanged();
    }

    private long CaretLineStart()
    {
        var doc = _doc!;
        var (line, _) = doc.GetLineColumn(_caret);
        return doc.GetOffsetForLine(line);
    }

    private long CaretLineEnd()
    {
        var doc = _doc!;
        var (line, _) = doc.GetLineColumn(_caret);
        var slice = doc.GetLine(line);
        return slice.CharOffset + slice.Text.Length;
    }

    private long PrevPosition(bool word)
    {
        var doc = _doc!;
        if (_caret == 0) return 0;
        if (!word) return doc.SnapCaret(_caret - 1, SnapDirection.Left);

        // Word-left: operate on the caret line's slice; crossing the line start
        // steps over the terminator first.
        var (line, col) = doc.GetLineColumn(_caret);
        if (col == 0) return doc.SnapCaret(_caret - 1, SnapDirection.Left);
        var text = doc.GetLine(line).Text.Span;
        int p = (int)Math.Min(col, text.Length);
        while (p > 0 && IsWordSep(text[p - 1])) p--;
        while (p > 0 && !IsWordSep(text[p - 1])) p--;
        return doc.GetOffsetForLine(line) + p;
    }

    private long NextPosition(bool word)
    {
        var doc = _doc!;
        long len = doc.Length;
        if (_caret >= len) return len;
        if (!word) return doc.SnapCaret(_caret + 1, SnapDirection.Right);

        var (line, col) = doc.GetLineColumn(_caret);
        var slice = doc.GetLine(line);
        var text = slice.Text.Span;
        int p = (int)Math.Min(col, text.Length);
        if (p >= text.Length)
            return doc.SnapCaret(Math.Min(len, slice.CharOffset + text.Length + slice.TerminatorLength), SnapDirection.Right);
        while (p < text.Length && !IsWordSep(text[p])) p++;
        while (p < text.Length && IsWordSep(text[p])) p++;
        return slice.CharOffset + p;
    }

    private static bool IsWordSep(char c) => c != '_' && !char.IsLetterOrDigit(c);

    // ── Mouse ────────────────────────────────────────────────────────────────

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(_canvas).Position;
        long pos = HitTestOffset(pt.X, pt.Y);
        bool shift = IsDown(VirtualKey.Shift);
        _caret = pos;
        if (!shift) _anchor = pos;
        _pointerDown = true;
        _canvas.CapturePointer(e.Pointer);
        _desiredColumnX = -1;
        _doc?.SealUndoCoalescing();
        ResetCaretBlink();
        InvalidateView();
        RaiseSelectionChanged();
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;
        var pt = e.GetCurrentPoint(_canvas).Position;
        _caret = HitTestOffset(pt.X, pt.Y);
        BringCaretIntoView();
        InvalidateView();
        RaiseSelectionChanged();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _pointerDown = false;
        _canvas.ReleasePointerCapture(e.Pointer);
    }

    // ── Clipboard ────────────────────────────────────────────────────────────

    /// <summary>Copies the selection with CRLF endings, built in a single pass.</summary>
    private void CopySelection()
    {
        var doc = _doc;
        if (doc is null || SelectionLength == 0) return;
        if (SelectionLength > MaxClipboardChars) return; // guarded upstream by the menu too

        long start = SelectionStart, length = SelectionLength;
        var sb = new System.Text.StringBuilder((int)Math.Min(length + length / 16, int.MaxValue - 64));
        const int Chunk = 64 * 1024;
        long done = 0;
        bool pendingCr = false; // a CR seen, undecided whether it heads a CRLF
        while (done < length)
        {
            int take = (int)Math.Min(Chunk, length - done);
            string part = doc.GetText(start + done, take);
            foreach (char c in part)
            {
                if (pendingCr)
                {
                    sb.Append("\r\n");
                    pendingCr = false;
                    if (c == '\n') continue; // the pair's own LF
                }
                if (c == '\r') pendingCr = true;
                else if (c == '\n') sb.Append("\r\n");
                else sb.Append(c);
            }
            done += take;
        }
        if (pendingCr) sb.Append("\r\n");

        var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage
        { RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy };
        pkg.SetText(sb.ToString());
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
}
