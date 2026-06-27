using System;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.UI.Text.Core;

namespace Inklet.Editor;

// ─────────────────────────────────────────────────────────────────────────────
// IME support via CoreTextEditContext.
//
// CoreTextServicesManager.GetForCurrentView() is the WinUI 3 desktop hook into the
// OS text-services / IME stack (it works on current Windows 11; the old desktop
// limitation is gone). Unlike a raw TSF ITextStoreACP, this plugs into WinUI's own
// input pipeline, so an East-Asian IME treats this control as the composition target.
//
// Division of labour:
//   • Plain Latin typing  -> CharacterReceived (TextUpdating never fires for it).
//   • IME composition/commit -> the edit-context events below.
// The editor notifies the context of its own edits via NotifyTextChanged /
// NotifySelectionChanged (see CommitAppEdit / RaiseSelectionChanged).
// ─────────────────────────────────────────────────────────────────────────────

internal sealed partial class TextEditorControl
{
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
        => new() { StartCaretPosition = SelectionStart, EndCaretPosition = SelectionStart + SelectionLength };

    // ── Read requests (TSF asks the editor for its current text/selection) ─────

    private void OnEcTextRequested(CoreTextEditContext sender, CoreTextTextRequestedEventArgs args)
    {
        var req = args.Request;
        int len = _buffer.Length;
        int s = Math.Clamp(req.Range.StartCaretPosition, 0, len);
        int e = Math.Clamp(req.Range.EndCaretPosition, s, len);
        req.Text = _buffer.GetText(s, e - s);
    }

    private void OnEcSelectionRequested(CoreTextEditContext sender, CoreTextSelectionRequestedEventArgs args)
        => args.Request.Selection = CurrentSelectionRange();

    // ── Write requests (the IME composes/commits text) ─────────────────────────

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

    // Composition underline/format — the composing text already lives in the buffer and
    // renders as normal text; we don't draw a distinct underline (refinement, not required).
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
        _inEcCallback = true;
        try
        {
            text = NormalizeNewlines(text);
            int len = _buffer.Length;
            delStart = Math.Clamp(delStart, 0, len);
            delEnd = Math.Clamp(delEnd, delStart, len);
            if (delEnd > delStart) _buffer.Delete(delStart, delEnd - delStart);
            if (text.Length > 0) _buffer.Insert(delStart, text);
            int nlen = _buffer.Length;
            _anchor = Math.Clamp(selStart, 0, nlen);
            _caret = Math.Clamp(selEnd, 0, nlen);
            AfterEdit();
        }
        finally { _inEcCallback = false; }
    }

    private void SetSelectionFromIme(int start, int end)
    {
        _inEcCallback = true;
        try
        {
            _anchor = Math.Clamp(start, 0, _buffer.Length);
            _caret = Math.Clamp(end, 0, _buffer.Length);
            _desiredColumnX = -1;
            BringCaretIntoView();
            _canvas.Invalidate();
            RaiseSelectionChanged();
        }
        finally { _inEcCallback = false; }
    }

    // ── Layout (where to place the IME candidate window) ───────────────────────

    private void OnEcLayoutRequested(CoreTextEditContext sender, CoreTextLayoutRequestedEventArgs args)
    {
        var req = args.Request;
        if (GetImeTextBounds(req.Range.StartCaretPosition, req.Range.EndCaretPosition, out var textRect))
            req.LayoutBounds.TextBounds = textRect;
        req.LayoutBounds.ControlBounds = ToScreenDips(0, 0, _canvas.ActualWidth, _canvas.ActualHeight);
    }

    private bool GetImeTextBounds(int start, int end, out Rect rect)
    {
        rect = default;
        if (_canvas.ActualWidth <= 0 || !_metricsMeasured) return false;
        int len = _buffer.Length;
        var (r1, x1) = CaretDisplayPos(Math.Clamp(start, 0, len));
        var (r2, x2) = CaretDisplayPos(Math.Clamp(end, 0, len));
        double cx1 = PadLeft + x1 - _scrollX, cy1 = PadTop + r1 * _lineHeight - _scrollY;
        double cx2 = PadLeft + x2 - _scrollX, cy2 = PadTop + r2 * _lineHeight - _scrollY;
        double left = Math.Min(cx1, cx2), top = Math.Min(cy1, cy2);
        double right = Math.Max(cx1, cx2), bottom = Math.Max(cy1, cy2) + _lineHeight;
        if (right - left < 1) right = left + 1;
        rect = ToScreenDips(left, top, right - left, bottom - top);
        return true;
    }

    /// <summary>Map a canvas-client rectangle (DIPs) to screen coordinates (DIPs) for the IME.</summary>
    private Rect ToScreenDips(double x, double y, double w, double h)
    {
        double scale = XamlRoot?.RasterizationScale ?? 1.0;
        var origin = _canvas.TransformToVisual(null).TransformPoint(new Point(0, 0)); // DIPs within window
        var p = new POINT { x = 0, y = 0 };
        if (_windowHwnd != IntPtr.Zero) ClientToScreen(_windowHwnd, ref p);            // window client origin (px)
        double sx = p.x / scale + origin.X + x;
        double sy = p.y / scale + origin.Y + y;
        return new Rect(sx, sy, Math.Max(0, w), Math.Max(0, h));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);

    // Diagnostic trace for IME bring-up. Inert unless INKLET_TSF_LOG is set (to a path,
    // or "1"/"on" for %TEMP%\inklet-tsf.log) — no I/O in normal runs.
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
