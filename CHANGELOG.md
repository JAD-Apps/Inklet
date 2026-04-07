# Changelog

All notable changes to Inklet are documented in this file.

---

## [1.2.0] — 2026-04-07

### Added
- **Full session persistence** — the entire editor state is saved automatically on close with no save dialog:
  - Untitled tabs with unsaved content are preserved across restarts
  - Saved files with in-progress edits restore both the on-disk version and the unsaved overlay
  - Cursor position, encoding, BOM, and line ending style are all stored per tab
- `PersistedTabData` record for structured JSON serialisation of tab snapshots
- `SessionTabs` setting replaces the previous file-path-only `SessionFilePaths`

### Changed
- Closing the app no longer shows a "Save changes?" dialog — all state is committed silently
- Closing a tab no longer prompts to save
- File › New no longer prompts to save
- `PromptSaveSessionAsync` removed

---

## [1.1.0] — 2026-04-07

### Added
- **Multi-tab editing** via WinUI 3 `TabView`
  - Ctrl+T and the `+` button open new tabs
  - Tabs are closable, reorderable, and draggable
  - Tab header shows `*` prefix when the tab has unsaved changes
  - Closing the last tab resets it rather than exiting (mirrors Windows Notepad)
- File › New Tab menu item (Ctrl+T)
- **Basic session memory** — open file paths and active tab index restored on next launch
- Font dialog: font-family `TextBox` replaced with an editable `ComboBox` pre-populated with common monospaced fonts (Cascadia Code, Consolas, Fira Code, JetBrains Mono, and others); free-text entry still accepted
- Editor padding increased from `8,4` to `12,10` (horizontal, vertical) for improved readability
- File › Open reuses the active tab when it is a clean untitled tab, otherwise opens in a new tab
- Drag-and-drop follows the same reuse-or-new-tab logic
- `TabSession` model to hold per-tab runtime state (file path, content, cursor, document metadata)

---

## [1.0.2] — 2026-04-07

### Fixed
- Removed `WindowsPackageType=None` added in 1.0.1 — the app now runs correctly as a packaged MSIX via the `wapproj`; the unpackaged bootstrap shim conflicted with the package identity set by the deployment pipeline, causing an immediate `0x80070032` crash on launch

---

## [1.0.1] — 2026-04-07

### Fixed
- Renamed package identity from auto-generated GUID to `JADApps.Inklet` v1.0.1.0
- Fixed `MaxVersionTested` in `Package.appxmanifest` to match `TargetPlatformVersion` (`10.0.26100.0`)
- Added `AppxOSMaxVersionTestedReplaceManifestVersion=false` to `wapproj` to prevent MSBuild overwriting the manifest value at build time
- Added `WindowsPackageType=None` as a temporary workaround for `REGDB_E_CLASSNOTREG` on launch caused by a version mismatch between Windows 11 Insider build 26200 and `appxdeploymentserver.dll` v10.0.26100 (reverted in 1.0.2)
- Replaced the umbrella `Microsoft.WindowsAppSDK` 1.8 metapackage with seven individual sub-packages to exclude the `Microsoft.WindowsAppSDK.AI` and `Microsoft.WindowsAppSDK.ML` packages, which were injecting AI DLLs and an `appxfragment` registering AI activatable classes that caused `DEP2500`
- Removed `systemai:Capability` from `Package.appxmanifest` (root cause of initial `DEP2500`)

---

## [1.0.0] — 2026-04-07

### Added
- Initial release of **Inklet** — a lightweight WinUI 3 Notepad clone for Windows

#### Editor
- Full-featured plain-text editor based on WinUI 3 `TextBox` with Mica backdrop
- Word wrap toggle (Format › Word Wrap)
- Font picker: family, size, bold, italic (Format › Font)
- Zoom: In / Out / Reset (Ctrl++, Ctrl+−, Ctrl+0; View › Zoom)

#### File operations
- New, Open, Save, Save As with full file-picker integration
- Large-file warning for files over the size threshold
- Drag-and-drop file loading
- Command-line file argument support (`Inklet.exe path\to\file.txt`)
- Print via shell `print` verb

#### Encoding & line endings
- Automatic encoding detection (UTF-8, UTF-16 LE/BE, system ANSI, and others) via `UTF.Unknown` and `System.Text.Encoding.CodePages`
- BOM detection and round-trip preservation
- CRLF / LF / CR line ending detection, display, and round-trip preservation
- Encoding and line ending shown in the status bar

#### Edit operations
- Undo (Ctrl+Z), Cut, Copy, Paste, Delete, Select All
- Time/Date insert (F5)
- Go To Line (Ctrl+G)
- Find (Ctrl+F) with wrap-around and match-case option
- Find Next (F3) / Find Previous (Shift+F3)
- Replace (Ctrl+H) and Replace All — inline overlay panel

#### UI chrome
- Menu bar: File, Edit, Format, View, Help
- Status bar: line/column position, zoom level, line ending, encoding (toggleable via View › Status Bar)
- Window title reflects current file name and unsaved-changes indicator (`*`)
- Save-on-close prompt for unsaved changes
- Window size persisted and restored across sessions
- About dialog with version, build date, runtime, and OS information

#### Project structure
- `Inklet` — main WinUI 3 application (`.csproj`, net8.0-windows10.0.19041.0)
- `Inklet (Package)` — MSIX packaging project (`.wapproj`)
- `Inklet.Tests` — MSTest 4 unit test project
- `Inklet.slnx` — Visual Studio solution

#### Testing
- 58 unit tests across four suites: `EncodingDetectorTests`, `FileServiceTests`, `LineEndingTests`, `DocumentStateTests`
- Tests cover encoding detection, BOM handling, file read/write round-trips, line ending detection and conversion, and document state display logic

#### Infrastructure
- `.gitignore`, `LICENSE` (MIT), `README.md`
- Publish profiles for win-x64, win-x86, win-arm64
- `AllowUnsafeBlocks` enabled for encoding detection interop
