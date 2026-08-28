using System;
using System.Runtime.InteropServices;
using Inklet.Engine;
using Windows.Foundation;
using Windows.UI.Text.Core;

namespace Inklet.Editor;

// ─────────────────────────────────────────────────────────────────────────────
// IME support via CoreTextEditContext.
//
// CoreTextServicesManager.GetForCurrentView() is the WinUI 3 desktop hook into
// the OS text-services / IME stack. Division of labour:
//   - Plain Latin typing        -> CharacterReceived (TextUpdating never fires).
//   - IME composition/commit    -> the edit-context events below.
//
// TSF speaks int offsets; the engine speaks long. Documents that fit int are
// mapped 1:1; beyond int.MaxValue chars the context is not notified (Latin
// typing still works; IME composition in >2G-char documents is a known gap).
// Undo/redo notify the RANGED change (previously the whole document), and text
// requests are clamped to a sane window so TSF can never ask for gigabytes.
// ─────────────────────────────────────────────────────────────────────────────

internal sealed partial class TextEditorControl
{
    private const int MaxImeRequestChars = 64 * 1024;

    private bool ImeMappable => (_doc?.Length ?? 0) <= int.MaxValue;

    private int ImeClamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    /// <summary>Lazily create and wire the edit context (once). Safe to call repeatedly.</summary>
    private void EnsureEditContext()
    {
        if (_ecTried) return;
        _ecTried = true;
        try
        {
            var mgr = CoreTextServicesManager.GetForCurrentView();
            var ctx = mgr.CreateEditContext();
            ctx.InputPaneDisplayPolicy = CoreTextInputPaneDisplayPolicy.Manual;
            ctx.InputScope = CoreTextInputScope.Text;

            ctx.TextRequested += OnEcTextRequested;
            ctx.SelectionRequested += OnEcSelectionRequested;
            ctx.TextUpdating += OnEcTextUpdating;
            ctx.SelectionUpdating += OnEcSelectionUpdating;
            ctx.FormatUpdating += OnEcFormatUpdating;
            ctx.LayoutRequested += OnEcLayoutRequested;
            ctx.CompositionStarted += OnEcCompositionStarted;
            ctx.CompositionCompleted += OnEcCompositionCompleted;
            ctx.FocusRemoved += OnEcFocusRemoved;

            _editContext = ctx;
            ImeLog("EditContext created");
            if (_hasFocus) ctx.NotifyFocusEnter();
        }
        catch (Exception ex)
        {
            ImeLog("EditContext setup failed: " + ex.GetType().Name + ": " + ex.Message);
            _editContext = null;
        }
    }

    private CoreTextRange CurrentSelectionRange()
        => new() { StartCaretPosition = ImeClamp(SelectionStart), EndCaretPosition = ImeClamp(SelectionStart + SelectionLength) };

    // ── Notifications from the editor to TSF ──────────────────────────────────

    /// <summary>Ranged change notification for app-initiated edits (typing, delete, paste).</summary>
    private void NotifyImeTextChanged(long modStart, long modOldLen, long modNewLen)
    {
        if (_editContext is null || _inEcCallback || !ImeMappable) return;
        var changed = new CoreTextRange
        {
            StartCaretPosition = ImeClamp(modStart),
            EndCaretPosition = ImeClamp(modStart + modOldLen),
        };
        _editContext.NotifyTextChanged(changed, ImeClamp(modNewLen), CurrentSelectionRange());
    }

    /// <summary>
    /// Undo/redo notify the actual changed range (the engine's Changed event has
    /// already fired by the time the caret lands, so use the caret vicinity).
    /// </summary>
    private void NotifyImeUndoRedo()
    {
        if (_editContext is null || _inEcCallback || !ImeMappable) return;
        // The engine applied one unit at a known caret position; TSF only needs a
        // conservative range around it, not the whole document.
        _lastChangeForIme ??= new TextChange(0, 0, 0, 0, 0, 0);
        var c = _lastChangeForIme.Value;
        var changed = new CoreTextRange
        {
            StartCaretPosition = ImeClamp(c.Offset),
            EndCaretPosition = ImeClamp(c.Offset + c.RemovedLength),
        };
        _editContext.NotifyTextChanged(changed, ImeClamp(c.AddedLength), CurrentSelectionRange());
    }

    private TextChange? _lastChangeForIme;

    private void NotifyImeSelectionChanged()
    {
        if (_editContext is null || _inEcCallback || !ImeMappable) return;
        _editContext.NotifySelectionChanged(CurrentSelectionRange());
    }

    private void NotifyImeDocumentReset()
    {
        _imeComposing = false;
        if (_editContext is null || !ImeMappable) return;
        var all = new CoreTextRange { StartCaretPosition = 0, EndCaretPosition = 0 };
        _editContext.NotifyTextChanged(all, ImeClamp(_doc?.Length ?? 0), CurrentSelectionRange());
    }

    // ── Read requests (TSF asks the editor for its current text/selection) ────

    private void OnEcTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var doc = _doc;
        var req = args.Request;
        if (doc is null) { req.Text = ""; return; }
        long len = Math.Min(doc.Length, int.MaxValue);
        long s = Math.Clamp(req.Range.StartCaretPosition, 0, len);
        long e = Math.Clamp(req.Range.EndCaretPosition, s, len);
        // TSF can request arbitrarily large ranges; clamp to a sane window.
        if (e - s > MaxImeRequestChars) e = s + MaxImeRequestChars;
        req.Text = doc.GetText(s, e - s);
    }

    private void OnEcSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
        => args.Request.Selection = CurrentSelectionRange();

    // ── Write requests (the IME composes/commits text) ────────────────────────

    private void OnEcTextUpdating(CoreTextEditContext sender, CoreTextTextUpdatingEventArgs args)
    {
        try
        {
            var r = args.Range;
            var ns = args.NewSelection;
            ImeLog($"TextUpdating range=[{r.StartCaretPosition},{r.EndCaretPosition}] '{args.Text}' newSel=[{ns.StartCaretPosition},{ns.EndCaretPosition}]");
            ApplyImeEdit(r.StartCaretPosition, r.EndCaretPosition, args.Text ?? string.Empty,
                         ns.StartCaretPosition, ns.EndCaretPosition);
            args.Result = CoreTextTextUpdatingResult.Succeeded;
        }
        catch (Exception ex)
        {
            ImeLog("TextUpdating failed: " + ex.Message);
            args.Result = CoreTextTextUpdatingResult.Failed;
        }
    }

    private void OnEcSelectionUpdating(CoreTextEditContext sender, CoreTextSelectionUpdatingEventArgs args)
    {
        var sel = args.Selection;
        SetSelectionFromIme(sel.StartCaretPosition, sel.EndCaretPosition);
        args.Result = CoreTextSelectionUpdatingResult.Succeeded;
    }

    private void OnEcFormatUpdating(CoreTextEditContext sender, CoreTextFormatUpdatingEventArgs args)
        => args.Result = CoreTextFormatUpdatingResult.Succeeded;

    private void OnEcCompositionStarted(CoreTextEditContext sender, CoreTextCompositionStartedEventArgs args)
    {
        _imeComposing = true;
        ImeLog("CompositionStarted");
    }

    private void OnEcCompositionCompleted(CoreTextEditContext sender, CoreTextCompositionCompletedEventArgs args)
    {
        _imeComposing = false;
        ImeLog("CompositionCompleted");
    }

    private void OnEcFocusRemoved(CoreTextEditContext sender, object args)
        => _imeComposing = false;

    /// <summary>Apply an IME edit: replace [delStart,delEnd) with <paramref name="text"/> and set the selection.</summary>
    private void ApplyImeEdit(int delStart, int delEnd, string text, int selStart, int selEnd)
    {
        var doc = _doc;
        if (doc is null) return;
        _inEcCallback = true;
        try
        {
            long len = doc.Length;
            long ds = Math.Clamp(delStart, 0, len);
            long de = Math.Clamp(delEnd, ds, len);
            if (!doc.IsEditableRange(ds, de - ds)) return;
            if (de > ds || text.Length > 0)
            {
                // Raw insert: the IME's composition text is inserted verbatim
                // (an IME never produces line breaks mid-composition).
                if (de > ds && text.Length > 0) doc.Replace(ds, de - ds, text);
                else if (de > ds) doc.Delete(ds, de - ds);
                else doc.InsertRaw(ds, text);
            }
            long nlen = doc.Length;
            _anchor = Math.Clamp(selStart, 0, nlen);
            _caret = Math.Clamp(selEnd, 0, nlen);
            AfterEdit();
        }
        finally { _inEcCallback = false; }
    }

    private void SetSelectionFromIme(int start, int end)
    {
        var doc = _doc;
        if (doc is null) return;
        _inEcCallback = true;
        try
        {
            _anchor = Math.Clamp(start, 0, doc.Length);
            _caret = Math.Clamp(end, 0, doc.Length);
            _desiredColumnX = -1;
            BringCaretIntoView();
            InvalidateView();
            RaiseSelectionChanged();
        }
        finally { _inEcCallback = false; }
    }

    // ── Layout (where to place the IME candidate window) ──────────────────────

    private void OnEcLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        var req = args.Request;
        if (GetImeTextBounds(req.Range.StartCaretPosition, req.Range.EndCaretPosition, out var textRect))
            req.LayoutBounds.TextBounds = textRect;
        req.LayoutBounds.ControlBounds = ToScreenDips(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
    }

    private bool GetImeTextBounds(long start, long end, out Rect rect)
    {
        rect = default;
        var doc = _doc;
        if (doc is null || _canvas.ActualWidth <= 0 || !_metricsMeasured) return false;
        long len = doc.Length;
        var p1 = ImeCaretCanvasPos(Math.Clamp(start, 0, len));
        var p2 = ImeCaretCanvasPos(Math.Clamp(end, 0, len));
        if (p1 is not { } a || p2 is not { } b) return false;
        double left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        double right = Math.Max(a.X, b.X), bottom = Math.Max(a.Y, b.Y) + _lineHeight;
        if (right - left < 1) right = left + 1;
        rect = ToScreenDips(left, top, right - left, bottom - top);
        return true;
    }

    /// <summary>Canvas-relative caret position of an offset, or null when off-screen.</summary>
    private Point? ImeCaretCanvasPos(long offset)
    {
        var (line, subRow, x) = CaretDisplayPos(offset);
        var dist = RowWalker.TryDistance(_anchorView with { PixelDelta = 0 },
            new ViewportAnchor(line, subRow, 0), ViewportRows + 4, Oracle);
        if (dist is null) return null;
        return new Point(PadLeft - _scrollX + x, PadTop - _anchorView.PixelDelta + dist.Value * _lineHeight);
    }

    /// <summary>Map a canvas-client rectangle (DIPs) to screen coordinates (DIPs) for the IME.</summary>
    private Rect ToScreenDips(double x, double y, double w, double h)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        var origin = _canvas.TransformToVisual(null).TransformPoint(new Point(0, 0));
        var p = new POINT { x = 0, y = 0 };
        if (_windowHwnd != IntPtr.Zero) ClientToScreen(_windowHwnd, ref p);
        double sx = p.x / scale + origin.X + x;
        double sy = p.y / scale + origin.Y + y;
        return new Rect(sx, sy, Math.Max(0, w), Math.Max(0, h));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    // Diagnostic trace for IME bring-up. Inert unless INKLET_TSF_LOG is set.
    private static readonly string? _imeLogPath = ResolveImeLogPath();
    private static string? ResolveImeLogPath()
    {
        var v = Environment.GetEnvironmentVariable("INKLET_TSF_LOG");
        if (string.IsNullOrEmpty(v)) return null;
        return v is "1" or "true" or "on"
            ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inklet-tsf.log")
            : v;
    }
    private static void ImeLog(string m)
    {
        if (_imeLogPath is null) return;
        try { System.IO.File.AppendAllText(_imeLogPath, m + "\r\n"); } catch { }
    }
}
