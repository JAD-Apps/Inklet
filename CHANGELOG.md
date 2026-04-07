# Changelog

All notable changes to Inklet are documented in this file.

---

## [0.9.5] - 2026-07-04

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

## [0.9.4] - 2026-07-04

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

## [0.9.3] - 2026-07-04

### Documentation
- README - added Redo, Close Tab (Ctrl+W), Ctrl+Scroll zoom to feature list; added Tabs & Session section; added File Associations section; fixed Undo description; precise keyboard notation throughout
- CHANGELOG - added entries for 0.9.1 and 0.9.2; corrected all version references

---

## [0.9.2] - 2026-07-04

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

## [0.9.1] - 2026-07-04

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

