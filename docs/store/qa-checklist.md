# Inklet 2.0.0 — Pre-submission QA checklist

Build under test: **packaged 2.0.0.0** (already installed on this machine as the
dev deploy). Work top to bottom; each line has an expected result. Tick the box,
or note the failure in the margin with what you saw.

Test files: `%TEMP%\inklet-corpus\` (corpus-1mb.log, corpus-100mb.log,
corpus-1gb.log, corpus-cjk.txt, corpus-mixed-eol.txt, corpus-megaline.txt).
If missing, regenerate: `Scripts\New-TestCorpus.ps1 -Large`.

---

## 1. Launch & session

- [ ] Launch from Start menu — window appears in well under a second, editor has focus (first keystroke lands without clicking)
- [ ] Your previous tabs are restored with cursor positions
- [ ] Type in a tab, close the window (X), relaunch — unsaved content and the `*` dirty marker are back
- [ ] Undo after restart is empty (expected: history doesn't survive restarts)
- [ ] Maximize, close, relaunch — window comes back maximized

## 2. Basic editing & rendering

- [ ] Type a paragraph — caret keeps up, no lag, no visual artifacts
- [ ] Caret blinks at your system rate; an idle window shows no flicker/repaint
- [ ] Click to place the caret between characters — lands where you aimed
- [ ] Double-click drag selection follows the pointer precisely
- [ ] Selection highlight aligns exactly with glyphs (no offset creep on long lines)
- [ ] Home / End / Ctrl+Home / Ctrl+End / PageUp / PageDown all behave
- [ ] Ctrl+Left/Right jumps word by word; Shift variants extend the selection
- [ ] Up/Down through lines of different lengths keeps the "sticky column"
- [ ] Undo (Ctrl+Z) / Redo (Ctrl+Y) walk edits correctly; typing bursts undo as one unit
- [ ] Undo back to the last save — the tab's `*` disappears
- [ ] Cut / Copy / Paste round-trip through another app (Notepad) with correct line breaks

## 3. Visuals across settings

- [ ] Zoom: Ctrl+scroll and Ctrl+Numpad+/- from 25% to 500% — caret, selection and hit-testing stay aligned at every step
- [ ] Change font (gear ▸ Format ▸ Font) to a proportional font (e.g. Segoe UI) — clicking between characters still lands correctly
- [ ] Bold and italic render and measure correctly
- [ ] Word wrap ON: long lines wrap at the window edge, no horizontal scrollbar, resize re-wraps live
- [ ] Word wrap ON: click and arrow-key through wrapped rows — caret lands where expected
- [ ] Word wrap OFF: horizontal scrollbar appears for long lines and tracks correctly
- [ ] `corpus-cjk.txt`: CJK text, emoji and mixed-width lines render and hit-test correctly (click between wide chars)
- [ ] Dark ↔ light: switch Windows theme, restart Inklet — fully re-themed
      (KNOWN: switching theme while running re-themes the editor but not the
      title bar until restart — do not file)
- [ ] If you have a second monitor at different scaling: drag the window across — text stays crisp, caret aligned

## 4. Large files

- [ ] Open `corpus-1gb.log` — content visible essentially instantly
- [ ] Status bar shows `· Indexing NN%` and counts up; scrolling/typing work during it
- [ ] While indexing: typing near the top works; the un-indexed far region simply isn't editable yet (expected)
- [ ] After indexing: Ctrl+G to line 11000000 — instant, status bar confirms
- [ ] Ctrl+End then Ctrl+Home — both instant
- [ ] Grab the scrollbar thumb and drag through the whole file — smooth, no freezes
- [ ] Type an edit deep in the file, Ctrl+S — save completes in seconds, `*` clears, app stays responsive during it
- [ ] `corpus-megaline.txt` (one 256 MB line): opens, scrolls horizontally, typing is sluggish-but-usable (~10 ms/key — expected; only the first 64K chars of the line render)
- [ ] Close the 1 GB tab, reopen it, make an edit, close the WINDOW, relaunch — the edit is restored (delta session)

## 5. Find & Replace

- [ ] Ctrl+F: bar opens, prefilled from a short selection if one exists
- [ ] Enter / F3 / arrows find next; wraps around at the end
- [ ] Backward find (▲) works from mid-document
- [ ] Match case toggles behaviour
- [ ] On the 1 GB file: searching stays responsive, never freezes the window; Esc cancels
- [ ] Replace: replaces the selected match then finds the next; Ctrl+Z restores it
- [ ] Replace All on a small file: all instances replaced; ONE Ctrl+Z restores everything
- [ ] Go To (Ctrl+G): out-of-range input is rejected; valid line lands with caret at line start

## 6. Files & encodings

- [ ] File ▸ Open a `.txt` — opens in current tab if it was a clean Untitled, else new tab
- [ ] Double-click a `.txt` in Explorer — opens in Inklet (association)
- [ ] Double-click a **large** (>10 MB) file in Explorer — opens fine (this silently failed in 1.0.9)
- [ ] Drag & drop two files onto the window — both open as tabs
- [ ] Open a binary file (an .exe renamed .txt) — "Binary File" warning appears; Cancel aborts
- [ ] `corpus-mixed-eol.txt`: open, one small edit, save — reopen in a hex/diff tool: only your edit changed, CRLF/LF/CR mix preserved
- [ ] Save As to a new name — subsequent saves go to the new file; watcher follows it
- [ ] Status bar shows correct encoding and line-ending labels for a UTF-8, a UTF-16, and an ANSI file
- [ ] Edit the open file in another editor and save there — Inklet prompts to reload; Reload shows the external change, "Keep my version" doesn't
- [ ] Saving in Inklet does NOT trigger its own reload prompt

## 7. IME  ⚠ thin automated coverage — please be thorough

- [ ] Add Japanese (Microsoft IME) in Windows settings if needed; Win+Space to switch
- [ ] Type romaji — composition text appears inline at the caret
- [ ] Candidate window opens NEXT TO the caret (not at a screen corner)
- [ ] Space cycles candidates; Enter commits; Esc cancels composition
- [ ] Commit, then Ctrl+Z — the committed text undoes cleanly
- [ ] Compose mid-line between existing characters — inserts at the right place
- [ ] Switch back to English — plain typing still works
- [ ] Composition in a large file behaves the same

## 8. Tabs & window

- [ ] Ctrl+T new tab; Ctrl+W close tab; the `+` button and per-tab ✕ work
- [ ] Closing a dirty tab prompts Save / Don't Save / Cancel — all three behave
- [ ] Open ~15 tabs — scroll arrows appear, click-scroll and hold-to-repeat work
- [ ] Tab switching is instant and preserves each tab's caret, scroll position and undo history
- [ ] Closing the last tab resets it to a clean Untitled (window stays open)
- [ ] Window close with a dirty FILE tab: no prompt, edits restored on next launch (session carries them — expected)

## 9. Print

- [ ] Page Setup: change margins and header/footer tokens (&f &d &t &p &P) — persisted
- [ ] Print a small file (or Microsoft Print to PDF): content, wrap, header/footer and page numbers correct
- [ ] Print preview/print a LARGE file: dialog appears promptly, spooling doesn't hang the app, `&P` total pages is right
- [ ] Cancel in the print dialog — no crash, app usable

## 10. Ten minutes of real use

- [ ] Use it on your own files for ten minutes — nothing feels off (typing feel, scrolling feel, focus, dialogs)

---

## Known & accepted for 2.0 — do NOT file these

| Behaviour | Status |
|---|---|
| Theme switch while running: chrome stays until restart | pre-existing, polish later |
| Wrap mode scrollbar moves in whole lines, not pixels | by design (huge-file model) |
| Lines cap at 64K chars per rendered row (megalines) | documented limit |
| Far regions read-only during initial indexing seconds | by design |
| Select-all+delete blocked during indexing | by design |
| Undo history not persisted across restarts | parity with 1.0.9 |
| 32-bit build refuses files > 256 MB | by design, points to 64-bit |
| Saved-over file's old blob holds disk space until tab closes | accepted trade-off |
| IME unavailable in documents beyond 2 billion chars | documented gap |

**Result:** ☐ PASS — submit  ☐ Issues found: ______________________________
