using System;
using Inklet.Engine;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    /// <summary>
    /// Copies the selection with CRLF endings. Returns true only when the text
    /// actually reached the clipboard - every failure mode (selection too large,
    /// out of memory building the string, the OS clipboard refusing the payload)
    /// surfaces as a polite dialog instead of an unhandled exception, which in a
    /// packaged WinUI app is process death (this is exactly how 2.0.1 died on
    /// Ctrl+X of a huge selection: Clipboard.SetContent threw 0x800401F0).
    /// </summary>
    private bool CopySelection()
    {
        var doc = _doc;
        if (doc is null || SelectionLength == 0) return false;
        if (SelectionLength > MaxClipboardChars)
        {
            ShowClipboardNotice(
                $"This selection is {SelectionLength / (1024 * 1024)} million characters — too large for the Windows clipboard. " +
                "Copy a smaller range, or use Save As to export the document.");
            return false;
        }

        long start = SelectionStart, length = SelectionLength;
        string text;
        try
        {
            text = BuildClipboardText(doc, start, length);
        }
        catch (OutOfMemoryException)
        {
            ShowClipboardNotice("There isn't enough memory to copy a selection this large.");
            return false;
        }

        // Write through the Win32 clipboard, not WinRT Clipboard.SetContent - the
        // WinRT API fails with CO_E_NOTINITIALIZED (0x800401F0) from this packaged
        // WinUI window even for tiny strings, and that exception is what used to
        // kill the app on Ctrl+C/Ctrl+X. The Win32 path is what the rest of the
        // OS uses and behaves.
        if (!Win32Clipboard.TrySetText(_windowHwnd, text))
        {
            ShowClipboardNotice(
                "Windows couldn't accept this text onto the clipboard. " +
                "It may be too large, or another app is holding the clipboard — try again in a moment.");
            return false;
        }
        return true;
    }

    /// <summary>Minimal Win32 clipboard writer (CF_UNICODETEXT) with brief open-retries.</summary>
    private static class Win32Clipboard
    {
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool CloseClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EmptyClipboard();
        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GlobalLock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern IntPtr GlobalFree(IntPtr hMem);

        private const uint CF_UNICODETEXT = 13;
        private const uint GMEM_MOVEABLE = 0x0002;

        public static unsafe bool TrySetText(IntPtr ownerHwnd, string text)
        {
            // The clipboard is a single shared lock; another process can hold it
            // for a moment (clipboard history, RDP, sync tools). Retry briefly.
            bool opened = false;
            for (int attempt = 0; attempt < 10 && !(opened = OpenClipboard(ownerHwnd)); attempt++)
                System.Threading.Thread.Sleep(30);
            if (!opened) return false;

            IntPtr hMem = IntPtr.Zero;
            try
            {
                long bytes = ((long)text.Length + 1) * sizeof(char);
                hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes);
                if (hMem == IntPtr.Zero) return false;
                var dst = (char*)GlobalLock(hMem);
                if (dst is null) return false;
                text.AsSpan().CopyTo(new Span<char>(dst, text.Length));
                dst[text.Length] = '\0';
                GlobalUnlock(hMem);

                if (!EmptyClipboard()) return false;
                if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero) return false;
                hMem = IntPtr.Zero; // ownership transferred to the system
                return true;
            }
            catch (OutOfMemoryException)
            {
                return false;
            }
            finally
            {
                if (hMem != IntPtr.Zero) GlobalFree(hMem);
                CloseClipboard();
            }
        }
    }

    /// <summary>
    /// Materialises the selection with CRLF endings in ONE exactly-sized
    /// allocation: a counting pass sizes the string, a second pass fills it.
    /// (The previous StringBuilder approach held ~2x the text at peak.)
    /// </summary>
    private static string BuildClipboardText(Engine.Document doc, long start, long length)
    {
        const int Chunk = 256 * 1024;
        var buf = new char[(int)Math.Min(Chunk, length)];

        // Pass 1: converted length = length + (number of lone LF or lone CR breaks).
        long extra = 0;
        bool pendingCr = false;
        for (long done = 0; done < length; )
        {
            int take = (int)Math.Min(buf.Length, length - done);
            doc.CopyTo(start + done, take, buf);
            for (int i = 0; i < take; i++)
            {
                char c = buf[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (c == '\n') { done += 0; }   // CRLF pair: no growth
                    else extra++;                    // lone CR grew by one
                }
                else if (c == '\n') extra++;         // lone LF grows by one
                if (c == '\r') pendingCr = true;
            }
            done += take;
        }
        if (pendingCr) extra++;

        // Pass 2: fill the exact-size string.
        return string.Create((int)(length + extra), (doc, start, length), static (span, state) =>
        {
            var (d, s, len) = state;
            var chunk = new char[(int)Math.Min(Chunk, len)];
            int outPos = 0;
            bool pending = false;
            for (long done = 0; done < len; )
            {
                int take = (int)Math.Min(chunk.Length, len - done);
                d.CopyTo(s + done, take, chunk);
                for (int i = 0; i < take; i++)
                {
                    char c = chunk[i];
                    if (pending)
                    {
                        span[outPos++] = '\r';
                        span[outPos++] = '\n';
                        pending = false;
                        if (c == '\n') continue;
                    }
                    if (c == '\r') pending = true;
                    else if (c == '\n') { span[outPos++] = '\r'; span[outPos++] = '\n'; }
                    else span[outPos++] = c;
                }
                done += take;
            }
            if (pending) { span[outPos++] = '\r'; span[outPos++] = '\n'; }
        });
    }

    /// <summary>Fire-and-forget notice dialog; never throws back into the caller.</summary>
    private async void ShowClipboardNotice(string message)
    {
        try
        {
            if (XamlRoot is null) return;
            await new ContentDialog
            {
                Title = "Clipboard",
                Content = message,
                CloseButtonText = "OK",
                XamlRoot = XamlRoot,
            }.ShowAsync();
        }
        catch
        {
            // A dialog already open, or the window tearing down - nothing to do.
        }
    }

    private void CutSelection()
    {
        if (SelectionLength == 0) return;
        // Only remove the text once it has definitely reached the clipboard;
        // a failed copy must never eat the selection.
        if (CopySelection())
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
