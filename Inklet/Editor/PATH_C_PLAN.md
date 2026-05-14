# Path C — Custom Editor Plan

## What's in this PR (the data layer)

Two production-ready building blocks:

- **`PieceTable`** — immutable-original / append-add / piece-list backing store.
  Inserts and deletes are local (no full-buffer copy), and consecutive typing
  coalesces into a single piece. Tested with 19 cases covering construction,
  insert/delete/slice, char access, coalescing, multi-piece deletes, and
  out-of-range arguments.
- **`EditorBuffer`** — wraps `PieceTable` + `LineIndex` + an undo/redo stack
  with edit coalescing (sequential single-char inserts within 500 ms become
  one undo entry, matching Notepad). Tested with 11 cases covering insert/
  delete/undo/redo, coalescing semantics, and burst inserts.

Total impact on the binary today: zero. Nothing in `MainWindow` consumes these
classes yet — they're scaffolding for the next step.

## What's still needed for a fully custom editor

The remaining work is the renderer + input plumbing. It is **multi-week scope**
and was deliberately deferred:

### 1. Rendering (the biggest single piece)
- Add `Microsoft.Graphics.Canvas.WinUI` (Win2D) NuGet reference.
- New `EditorControl : UserControl` hosting a `CanvasControl`.
- For each visible line, build a `CanvasTextLayout` from the buffer slice and
  draw via `CanvasDrawingSession.DrawTextLayout`.
- Cache `CanvasTextLayout` per logical line; invalidate the affected line on
  buffer change events.
- Word wrap implemented via `CanvasTextLayout.RequestedSize`.

### 2. Caret + selection
- Blinking caret via `DispatcherTimer` driven from `Caret.GetCaretBlinkTime()`.
- Selection rectangles computed from `CanvasTextLayout.GetCharacterRegions`.
- Mouse: click → caret position via `HitTest`; drag → extend selection;
  double-click → word at point; triple-click → whole line.
- Keyboard: arrow keys / Home/End / PageUp/PageDown / Shift+arrows / Ctrl+arrows
  for word movement / Tab/Enter / Backspace/Delete.

### 3. Scrolling + virtualisation
- `ScrollViewer` host; `EditorControl` reports its `RequestedHeight` based on
  line count × line height.
- During render, only iterate lines whose Y-range overlaps the viewport.
- `BringIntoView` for the caret on edit.

### 4. IME
- `CoreTextEditContext` for East Asian input methods. This is non-trivial —
  plan for a couple of days even with the WinUI sample as a reference.

### 5. Accessibility (UIA)
- `AutomationPeer` exposing `ITextProvider`, `IInvokeProvider`.
- Without this, screen readers see an opaque control.

### 6. Clipboard + drag-drop
- Cut/Copy/Paste via `Windows.ApplicationModel.DataTransfer.Clipboard`.
- Drop accepts `StorageItems` (text files) and `Text` (paste).

### 7. Wire into `MainWindow`
- Replace `<TextBox x:Name="Editor">` in MainWindow.xaml with the new control.
- Update every `Editor.Text`/`Editor.SelectionStart`/etc. site (~40 references)
  to the new control's API.
- Per-tab buffer ownership (no shared editor) — see `P9` in the audit report,
  which was deferred specifically because Path C makes it free.

## Why we're stopping here

A half-finished custom editor would be visibly worse than the current
TextBox: missing word selection, broken IME for CJK users, no accessibility,
weird scrollbar behaviour, etc. Shipping the data layer with full test
coverage gives the next contributor (or a future iteration) a foundation to
build on without losing the perf characteristics that justified Path C in
the first place.

The performance-critical work that *is* shipped today — atomic session
writes, async close-save, O(1) dirty flag, line-index cache, sample-based
encoding detection, single-pass line-ending normalisation, file watcher,
30 s autosave, and the rest — already takes Inklet from "competitive with
Notepad up to ~1 MB" to "competitive up to ~10 MB or so", which covers the
overwhelming majority of files users open. Path C is what unlocks the
multi-hundred-MB tier.
