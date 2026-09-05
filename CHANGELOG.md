# Changelog

All notable changes to Inklet are documented in this file.

---

## [2.0.2] - 2026-08-30

### Fixed
- Save As killed the app instead of saving, and so did choosing "Save" when
  closing an unsaved tab: the save picker was configured with a wildcard it
  rejects, and the resulting error escaped before the picker even appeared.
  The picker now opens, writes the file and retitles the tab, and any future
  picker failure shows the "could not save" dialog instead of ending the
  process.
- On the 32-bit build, opening a file larger than 256 MB showed a raw
  "Not enough memory resources" OS error instead of the intended message.
  The size is now checked before the file is mapped, so the refusal reads
  "Files larger than 256 MB need the 64-bit version of Inklet."
- Any assistive technology could crash the app just by inspecting it. Narrator,
  Voice Access, Magnifier's text tracking and the accessibility scans a store
  submission runs all enumerate a window's UI Automation tree, and doing so
  killed Inklet instantly. The app's entry point ran the UI thread in the
  wrong COM apartment, and automation providers are COM objects that must be
  created on the same kind of thread the UI lives on; the mismatch faulted
  inside the framework. The entry point now uses the apartment WinUI itself
  expects, and the automation tree can be read safely.
- Double-clicking a word selected nothing and triple-clicking a line selected
  nothing: the editor had no notion of a click run, so every press simply moved
  the caret. Double-click now selects the word under the pointer (or the run of
  spaces between words), triple-click selects the whole line including its line
  break, and dragging after either grows the selection by whole words or lines.
- Pressing Enter a second time in the Find bar replaced the match it had just
  found with a line break, quietly altering the file. Finding a match moved
  focus into the document, so the next Enter was typed into it. Focus now stays
  in the Find bar while it is open, and Enter walks the matches and wraps at the
  end as expected.
- UTF-16 and UTF-32 files were refused with a "Binary File" warning whose
  default is Cancel. The check treats a zero byte in the first 8 KB as proof of
  binary content, which is true of UTF-8 and legacy encodings but wrong for
  UTF-16 and UTF-32, where ordinary letters contain one. A file carrying a
  Unicode byte-order mark is now taken at its word and opens directly.
- Reopening the app could kill it on every launch, leaving no way back in
  without clearing app data by hand. Restoring a session whose selected tab
  was a large file with the caret far into it (for example after Ctrl+End,
  then closing the window) asked the engine for the caret's line and column
  before background indexing had reached that far, and the resulting error
  escaped through a UI callback and terminated the process. The same cause
  could instead leave the tab blank until the window was clicked, when the
  error surfaced on a background task and was swallowed. Caret positions are
  now bounded by how much of the document is actually addressable rather than
  by its estimated total size, in session restore, caret movement, selection,
  undo/redo, rendering and the text-services read path.
- Scrollbars were never visible. The editor's scrollbars are created directly
  rather than by a scroll viewer, so they needed their indicator mode set
  explicitly and an explicit thickness; without both they drew nothing and
  took up no width, however correct their range and visibility were.
  Vertical and horizontal scrollbars now appear whenever the content
  overflows.
- Clicking tabs stopped working after switching between tabs when a very
  large file was open: the text-services (IME) subsystem re-synchronises
  document content when told a document changed, and declaring a
  multi-million-character document made that synchronous sync hang the UI
  thread mid-switch. Documents beyond 2 million characters are no longer
  synchronised with the IME subsystem (inline East-Asian composition is
  unavailable in such documents; typing is unaffected). The title bar also
  now declares explicit click-through regions for the tabs and buttons
  instead of relying on the framework's automatic regions.
- Copying or cutting text crashed the app instead of completing or refusing
  gracefully: the WinRT clipboard API (`Clipboard.SetContent`) fails with
  0x800401F0 from the packaged window even for small strings, and the
  unhandled exception killed the process. The copy path now writes through
  the Win32 clipboard (with brief retries while another app holds it), a
  selection too large for the clipboard shows a polite dialog instead of
  crashing or silently doing nothing, and Cut only removes the text once it
  has actually reached the clipboard. Copying ~100 million characters from a
  100 MB document now works; the memory peak while building the clipboard
  text was also halved (exact-size single allocation).

## [2.0.1] - 2026-08-28

### Fixed
- The app crashed when pressing Backspace (or Delete) at the end of the
  document: the engine's change notification fires before the editor
  repositions its caret, and the momentarily out-of-range caret made a
  geometry lookup throw inside a XAML callback, killing the process with
  a stowed exception (0xC000027B). Caret positions are now clamped at the
  change boundary and in every display path.

### Added
- Unhandled exceptions are now logged to %TEMP%\inklet-crash.log before the
  process dies, so future crash reports carry a usable stack.

## [2.0.0] - 2026-08-28

### Changed
- Ground-up text engine rewrite for large files: documents are now
  memory-mapped and indexed in the background, so files of any size (tested
  to 1 GB+, designed for tens of GB) open instantly, edit at sub-millisecond
  latency, and hold flat memory regardless of file size. Typing latency into
  a 100 MB file improved from ~88 ms to ~0.9 ms per keystroke.
- Rendering now uses per-line text layouts: tabs, CJK text, proportional
  fonts and emoji position correctly in hit-testing, selection and the caret
  (previously monospace-approximated).
- Undo is per-tab and survives both tab switches and saves; undoing back to
  the last saved state marks the tab clean again.
- Saving is atomic (temp file + swap) and byte-identical outside your edits -
  mixed line endings in existing files are preserved exactly.
- Find, Replace and Replace All run in the background and no longer freeze
  the window on large documents; Replace All is a single undo step.
- Replacing text no longer clears the undo history.
- Session files now store edit deltas instead of full document text for
  file-backed tabs (a 1 GB file with a few edits persists in ~1 KB); old
  session files migrate automatically.
- Printing streams the document instead of holding several copies in memory.
- The "Large File" warning dialog is gone - opening is instant at any size.
- The caret now blinks at the system rate and idle redraws are eliminated.

### Fixed
- The portable (non-Store) release build crashed on startup with error
  0xC000027B: the publish pipeline silently skipped WinUI resource generation
  and omitted the app's resource index and compiled XAML from the output. The
  release workflow now publishes through msbuild (so the platform applies), the
  project carries its resources into the publish output, and the workflow fails
  fast if they are ever missing again.
- Opening a file larger than 10 MB from the command line or a file association
  silently failed: the "Large File" confirmation dialog was shown before the
  window's content tree was ready (null `XamlRoot`), and the resulting
  `ArgumentException` was swallowed by the fire-and-forget startup task. The
  startup path now waits for the content tree before prompting.

## [1.0.9] - 2026-06-27

### Added
- **IME (East-Asian composition) support** on the custom Win2D editor, implemented with `CoreTextEditContext` (`Editor/TextEditorControl.Ime.cs`). The editor registers with the WinUI 3 desktop text-services stack via `CoreTextServicesManager.GetForCurrentView()` and calls `NotifyFocusEnter`/`NotifyFocusLeave`, so an active IME drives composition and commit through the edit-context events (`TextUpdating`, `TextRequested`, `SelectionRequested`, `SelectionUpdating`, `CompositionStarted`/`Completed`). The candidate window is placed at the caret via `LayoutRequested`. Plain Latin typing continues to arrive through `CharacterReceived` (the IME never composes it) and is suppressed only while a composition is in flight so input is never doubled. The editor keeps the IME in sync by calling `NotifyTextChanged`/`NotifySelectionChanged` on its own edits.

### Changed
- Replaced an earlier raw-TSF (`ITextStoreACP`) input experiment with `CoreTextEditContext`. A raw TSF text store connects and is queried, but a custom WinUI 3 control is not treated by the IME as its composition target (composition fell through to plain Latin input); `CoreTextServicesManager`/`CoreTextEditContext` plugs into WinUI's own input pipeline instead. The TSF interop/bridge/store files were removed. `CoreTextServicesManager.GetForCurrentView()` works in this WinUI 3 desktop build (the historical desktop limitation is gone — verified by probe).

---

## [1.0.8] - 2026-06-26

### Added
- **Word wrap** on the custom Win2D editor (Format ▸ Word Wrap). Long lines wrap to the viewport width at word boundaries (hard-breaking words longer than a row), the horizontal scrollbar is hidden while wrapped, and caret movement, click hit-testing, selection and up/down navigation all operate on the wrapped display rows. Verified by running the app: a 586-character line wraps across rows and clicking into a wrapped row places the caret correctly.

---

## [1.0.7] - 2026-06-26

### Fixed
- **Large documents were partly invisible.** Past roughly 512 KB the text rendered in the background colour and could not be seen — the reported bug. The root cause is that the WinUI 3 `RichEditBox` stops painting glyphs beyond a few hundred KB (the text is still loaded and selectable, but not drawn), and no colour/limit workaround overcomes it. A hosted native Win32 edit control renders large files but WinUI's composition occludes child HWNDs (the "airspace" problem). Both were verified by running the app and screenshotting deep lines. The editor surface has therefore been **replaced with a custom Win2D control** (`Editor/TextEditorControl.cs`) that draws only the lines visible in the viewport. It is a real XAML element (composes correctly, no airspace) and virtualises rendering, so it displays and edits documents of **any length** while staying fast. Verified on a 4 MB / 60,000-line file: it renders line 60,000, navigation/Find/Go To reach it, and saving preserves the file's line endings.
- Find, Go To and the Ln/Col status drifted on multi-line documents because the cached text used CRLF line breaks while the control counted caret positions with one unit per break. In-memory text is now held with a single LF convention shared by the editor, Find and Go To; files still save with their detected line ending (CRLF/LF/CR).
- Switching away from, or autosaving, an unmodified tab silently marked it as modified (a stray `*` and an unnecessary save prompt on close). The tab state is now only re-read from the editor when an actual edit is pending.

### Changed
- Typing in large files is no longer laggy: the editor no longer serialises the whole document on every keystroke. It flips an O(1) dirty flag, reads the caret line/column straight from the control, and materialises the full text only on demand (save, find, go to, tab switch).
- The editor area is now an opaque solid surface (like Notepad's edit area) rather than floating over the Mica backdrop. Mica remains behind the title bar and tabs.

### Known limitations
- Word wrap, IME (East-Asian composition) and screen-reader (UIA) accessibility are not yet implemented on the new editor surface and are planned follow-ups.

---

## [1.0.5] - 2026-04-08

### Fixed
- App icon was missing from the taskbar — `AppWindow.SetIcon` was called with a `.png` file which Windows does not use for taskbar rendering; a multi-resolution `.ico` (256/48/32/16 px) is now generated from the source artwork and preferred by `SetWindowIcon()`
- About dialog showed hardcoded year `2025` for copyright — now uses `DateTime.Now.Year` so it always reflects the current year

---

## [1.0.4] - 2026-04-08

### Fixed
- App crashed immediately on launch from the Microsoft Store (`REGDB_E_CLASSNOTREG`) — the WAP packaging project had `WinUISDKReferences=false` and used individual `Microsoft.WindowsAppSDK.*` packages, so the SDK's MSBuild targets never injected a `PackageDependency` for the Windows App SDK runtime into the manifest; the Store installer therefore never co-installed `Microsoft.WindowsAppRuntime.1.8`, leaving WinUI 3 COM classes unregistered; fixed by adding `Microsoft.WindowsAppSDK` with `<IncludeAssets>build</IncludeAssets>` to the WAP project so the dependency is declared correctly
- Window reopened at screen-overflow dimensions when it had been closed while maximised — `SaveWindowSize` was unconditionally writing `AppWindow.Size` (which equals the screen resolution when maximised) as the restored size; the saved size is now only updated when the window is not maximised, and a new `WindowMaximized` setting causes the window to reopen via `OverlappedPresenter.Maximize()` instead of `ResizeWindow()`

---

## [1.0.3] - 2026-04-08

### Fixed
- App still crashed on launch from Store/sideload — `PublishTrimmed` was disabled in the `.csproj` but the three `.pubxml` publish profiles used by the packaging project did not set it, allowing the SDK to re-enable IL trimming for self-contained builds; `PublishTrimmed=False` is now set in all publish profiles

---

## [1.0.2] - 2026-04-08

### Fixed
- Session data lost on close — persisted tab JSON was stored in `ApplicationDataContainer` which has an 8 KB size limit; session data is now written to a file in `LocalFolder` so tabs of any size are preserved reliably
- Session cursor position lost on background-tab close — `PersistSession` now calls `SaveCurrentTabState` first so the active tab's cursor position is always captured before serialisation, not only on app close or tab switch
- Last-tab reset not persisted — closing the final tab now calls `PersistSession` immediately after clearing state, so a crash between reset and app close no longer restores stale content on next launch; cursor position is also explicitly zeroed

### Added
- Binary file warning — opening a known binary format (`.exe`, `.dll`, `.zip`, `.pdf`, images, archives, etc.) or a file containing NUL bytes now shows a confirmation dialog explaining the file will not display correctly

### Changed
- Unmodified file-backed tabs no longer store their full content in the session file; they are reloaded from disk on next launch, significantly reducing session file size

---

## [1.0.1] - 2026-04-07

### Fixed
- App failed to launch from the Microsoft Store — the IL trimmer stripped `ComInterfaceEntry` from `System.Runtime.InteropServices`, which CsWinRT requires at runtime for WinRT vtable registration; `PublishTrimmed` is now disabled

---

## [1.0.0] - 2026-04-07

### Fixed
- Print dialog (`PrintDlgEx`) crash — `AccessViolationException` caused by a null `lpPageRanges` pointer; a one-element `PRINTPAGERANGE` buffer is now allocated before calling the API (as required by the Win32 contract even when `PD_NOPAGENUMS` is set)
- Print dialog (`PrintDlgEx`) crash — `AccessViolationException` caused by invoking the COM-based `PrintDlgEx` on a `Task.Run` ThreadPool thread (MTA); the call now runs on a dedicated STA thread via `TaskCompletionSource<bool>`

### Changed
- Default font size changed from 14 pt to 12 pt

---

## [0.9.5] - 2026-04-07

### Added
Save prompt on tab close: Save / Don't Save / Cancel dialog when closing a modified tab
Tab width auto-expand: tabs fill available space immediately after a tab is closed
Window resize recalculates tab widths so equal-width tabs always fill the tab strip

### Changed
CloseTab refactored to async CloseTabAsync with ContentDialog save prompt
InvalidateTabLayout added: TabStrip.InvalidateMeasure + UpdateLayout deferred
TitleBar_SizeChanged and TabStrip_TabItemsChanged call InvalidateTabLayout
No save prompt on program close - session memory persists content silently (unchanged)
Package manifest version 0.9.4.0 -> 0.9.5.0

---

## [0.9.4] - 2026-04-07

### Added
Custom title bar with app icon, label, gear menu button, scroll-left/right buttons
TitleBarGrid 6 columns height 36px; gear button 36x36 Stretch
TransparentButtonStyle in App.xaml for frameless icon buttons
Single click scrolls ~500px; hold scrolls 80px/50ms after 400ms delay
Scroll buttons shown/hidden as pair; enabled/disabled at endpoints
DoubleTapped handlers on title bar controls prevent accidental window maximize
Tab strip visual tree: built-in scroll buttons hidden, content rows collapsed
Tab strip row set to Star; RepositionThemeTransition for smooth animations
ScrollToEndOfTabStrip: deferred scroll so new tabs are visible
Full multi-scale app icon set (BadgeLogo, LargeTile, SmallTile, SplashScreen, etc.)
wapproj updated to reference all new image assets
Package manifest version 0.9.2.0 -> 0.9.4.0

---

## [0.9.3] - 2026-04-07

### Documentation
- README - added Redo, Close Tab (Ctrl+W), Ctrl+Scroll zoom to feature list; added Tabs & Session section; added File Associations section; fixed Undo description; precise keyboard notation throughout
- CHANGELOG - added entries for 0.9.1 and 0.9.2; corrected all version references

---

## [0.9.2] - 2026-04-07

### Added
- Redo - Edit > Redo (Ctrl+Y); the underlying TextBox redo stack is triggered so multi-step redo works
- Close Tab - File > Close Tab (Ctrl+W) closes the active tab; mirrors standard browser/editor behaviour
- Ctrl+Scroll zoom - Holding Ctrl while scrolling adjusts zoom in 10% steps

### Fixed
- Print temp file leak - temp file for the shell print verb is scheduled for deletion 30 s after spooling
- Session loss on mid-session tab close - PersistSession() called immediately after non-last tab removed

### Changed
- TabStrip_TabCloseRequested refactored into shared CloseTab(TabViewItem) used by both XAML event and Ctrl+W

---

## [0.9.1] - 2026-04-07

### Added
- File type associations - manifest declares windows.fileTypeAssociation for .txt .log .ini .cfg .md .xml .json .csv .yaml .yml
- Assembly version - AssemblyVersion, FileVersion, and NuGet Version added to Inklet.csproj

### Changed
- Package manifest version bumped from 0.7.1.0 to 0.9.1.0

---

## [0.9.0] - 2026-04-07

### Added
- Full session persistence - entire editor state saved automatically on close with no save dialog
  - Untitled tabs with unsaved content preserved across restarts
  - Saved files with in-progress edits restore on-disk version and unsaved overlay
  - Cursor position, encoding, BOM, and line ending stored per tab
- PersistedTabData record for structured JSON serialisation of tab snapshots
- SessionTabs setting replaces previous file-path-only SessionFilePaths
- Launch window size set to 800x550; active tab index and cursor position restored on startup

### Changed
- Closing the app no longer shows a Save changes? dialog - all state committed silently
- Closing a tab no longer prompts to save
- File > New no longer prompts to save
- PromptSaveSessionAsync removed

---

## [0.8.0] - 2026-04-07

### Added
- Multi-tab editing via WinUI 3 TabView (Ctrl+T, + button, closable/reorderable/draggable tabs)
- Tab header shows * prefix when the tab has unsaved changes
- Closing the last tab resets it rather than exiting (mirrors Windows Notepad)
- File > New Tab menu item (Ctrl+T)
- Basic session memory - open file paths and active tab index restored on next launch
- Font dialog: font-family TextBox replaced with editable ComboBox with common monospaced fonts
- Editor padding increased from 8,4 to 12,10 for improved readability
- File > Open reuses active tab when clean untitled, otherwise opens in new tab
- Drag-and-drop follows same reuse-or-new-tab logic
- TabSession model for per-tab runtime state

---

## [0.7.2] - 2026-04-07

### Fixed
- Removed WindowsPackageType=None added in 0.7.1; app now runs correctly as packaged MSIX via wapproj

---

## [0.7.1] - 2026-04-07

### Fixed
- Renamed package identity from auto-generated GUID to JADApps.Inklet v0.7.1.0
- Fixed MaxVersionTested in Package.appxmanifest to match TargetPlatformVersion (10.0.26100.0)
- Added AppxOSMaxVersionTestedReplaceManifestVersion=false to wapproj
- Added WindowsPackageType=None as temporary workaround for REGDB_E_CLASSNOTREG (reverted in 0.7.2)
- Replaced umbrella Microsoft.WindowsAppSDK 1.8 metapackage with seven individual sub-packages to exclude AI/ML
- Removed systemai:Capability from Package.appxmanifest (root cause of DEP2500)

---

## [0.7.0] - 2026-04-07

### Added
- Initial release of Inklet - a lightweight WinUI 3 Notepad clone for Windows
- Full-featured plain-text editor with Mica backdrop, word wrap, font picker, zoom
- New, Open, Save, Save As, Print, drag-and-drop, command-line file argument support
- Automatic encoding detection (UTF-8, UTF-16 LE/BE, ANSI, international code pages)
- BOM and line ending (CRLF/LF/CR) detection, display, and round-trip preservation
- Undo, Cut, Copy, Paste, Delete, Select All, Time/Date, Go To Line, Find, Replace
- Menu bar (File, Edit, Format, View, Help) and status bar
- Window title reflects file name and unsaved-changes indicator (*)
- Window size persisted and restored across sessions
- About dialog with version, build date, runtime, and OS information
- 58 unit tests: EncodingDetectorTests, FileServiceTests, LineEndingTests, DocumentStateTests

