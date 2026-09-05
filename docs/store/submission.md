# Inklet 2.0.2 — Microsoft Store submission pack

Everything to paste into Partner Center for the 2.0.2 update. Field limits are
noted where they matter; all copy below fits them. The upload artifact is
`Inklet (Package)/AppPackages/Inklet (Package)_2.0.2.0_x86_x64_arm64_bundle.msixupload`.

---

## Packages

- Upload: `Inklet (Package)_2.0.2.0_x86_x64_arm64_bundle.msixupload`
- Version: 2.0.2.0 (supersedes 1.0.9.0; 2.0.1 and 2.0.2 fix crashes and
  selection/encoding defects found in QA - see CHANGELOG)
- Architectures: x86, x64, ARM64
  - Note: on 32-bit (x86) devices, files larger than 256 MB are refused with a
    message pointing at the 64-bit build; the large-file engine is 64-bit only.

## Store listing — Description (limit 10,000 chars)

```
Inklet is a fast, clean notepad for Windows — everything you want from a plain-text editor, nothing you don't.

Version 2.0 rebuilds the text engine from the ground up for large files. Log files, database dumps, exports: files that other editors refuse or freeze on simply open.

BUILT FOR BIG FILES
• Gigabyte-sized files open instantly — the first page appears in milliseconds while indexing finishes in the background
• Typing stays under a millisecond whether the file is 1 KB or 10 GB
• Jump to any line — line 11,000,000 is as fast as line 11
• Memory stays low and flat no matter how big the file is
• Find & Replace runs in the background, so the window never freezes

CAREFUL WITH YOUR BYTES
• Saves are atomic and byte-exact: everything you didn't touch is written back identically — mixed line endings included
• Undo history survives tab switches and saves; undo back to your last save and the tab marks itself clean again
• Replace All is a single undo step
• UTF-8, UTF-16 and legacy encodings, with byte-order-mark handling
• Line-ending detection and preservation (Windows CRLF, Unix LF, classic Mac CR)

EVERYTHING A NOTEPAD SHOULD HAVE
• Tabs, with your session — open files, cursor positions and unsaved work — restored across restarts
• Word wrap, zoom, light and dark mode
• Correct text handling for East-Asian text, emoji and any font
• Printing with headers, footers and page setup
• Drag and drop files to open them
• Status bar with line/column, encoding and line-ending at a glance

NO NONSENSE
• No accounts, no telemetry, no ads
• Small, fast and quiet — it starts in under half a second and stays out of your way

Inklet is built by JAD Apps for people who live in text files. If a file is too big for your editor, it isn't too big for Inklet.
```

## Store listing — What's new in this version (limit 1,500 chars)

```
Inklet 2.0 is a ground-up rewrite of the text engine, built for files of any size:

• Gigabyte-sized files now open instantly, with indexing finishing in the background — tested to 10 GB and 113 million lines
• Typing latency is under a millisecond at any file size
• Saves are atomic and byte-exact: untouched content is written back identically, mixed line endings preserved
• Undo history is per-tab and survives both tab switches and saves; undoing back to your last save marks the tab clean
• Find & Replace runs in the background and no longer freezes the window; Replace All is one undo step
• Tabs, cursor position and unsaved work are restored across restarts far more efficiently
• Text rendering is now layout-accurate: East-Asian text, emoji and proportional fonts position correctly in selection and caret placement
• The caret blinks at your system rate and idle redraw is eliminated
• Fixed: opening a file over 10 MB from Explorer could silently fail
• Fixed: the "Large File" warning dialog is gone — no file is too large to open
```

## Store listing — App features (up to 20, 200 chars each)

```
Opens gigabyte-sized files instantly
Sub-millisecond typing at any file size
Byte-exact, atomic saves that preserve mixed line endings
Tabbed editing with full session restore
Per-tab undo history that survives saves
Background Find & Replace that never freezes the window
UTF-8, UTF-16 and legacy encoding support with BOM handling
Windows, Unix and classic Mac line-ending detection
Word wrap, zoom, and light/dark mode
Printing with headers, footers and page setup
No accounts, no telemetry, no ads
```

## Store listing — Search terms (up to 7, 30 chars each, no more than 21 unique words total)

```
notepad
text editor
large files
log viewer
plain text
txt editor
big file editor
```

## Store listing — Screenshots (docs/store/screenshots/, 1906×1025 PNG)

| File | Caption (limit 200 chars) |
|---|---|
| `01-hero-dark.png` | A fast, clean notepad for Windows — tabs, dark mode, and a text engine built for files of any size. |
| `02-gigabyte-file.png` | A 1 GB log file at line 11,000,000 — opened instantly, scrolled and edited without a stutter. |
| `03-find-replace.png` | Find & Replace runs in the background — searching a gigabyte never freezes the window. |
| `04-light-theme.png` | Light and dark themes follow Windows. No accounts, no telemetry, no clutter. |
| `05-menu-open.png` | Everything where you expect it - File, Edit, Format, View and Help behind one clean menu. |

Order in Partner Center: 02 (the differentiator) first or 01 first — recommend
02, 01, 03, 04 so the large-file story leads.

## Properties

- Category: Productivity (unchanged from 1.0.9)
- Pricing: Free (unchanged)
- System requirements — note for the listing: 64-bit (x64/ARM64) recommended
  for files larger than 256 MB.

## Support info (carried over; listed for completeness)

- Website: https://github.com/JAD-Apps/Inklet
- Support contact: https://github.com/JAD-Apps/Inklet/issues
- Privacy policy: https://github.com/JAD-Apps/Inklet/blob/master/PRIVACY_POLICY.md

## Certification notes (the "Notes for certification" box)

```
Update to an existing app. No account is required; no network access is used.
The app edits local plain-text files chosen by the user (file picker, drag &
drop, or .txt/.log/.md/etc. file association). To exercise the headline
feature, open any large text file (e.g. a multi-hundred-MB log): the content
appears immediately, the status bar shows background indexing progress, and
Ctrl+G jumps to any line.
```

## Pre-submission checklist

- [ ] Manual QA pass on the installed 2.0.2.0 (`docs/store/qa-checklist.md`)
- [ ] Master is green (CI) and tagged v2.0.2
- [ ] Upload the .msixupload, paste the fields above, reorder screenshots
- [ ] Submit for certification
