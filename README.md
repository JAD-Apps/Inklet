# Inklet

A lightweight, modern Notepad clone for Windows built with WinUI 3 and .NET 8.

Inklet faithfully recreates the classic Windows 10 Notepad experience with modern WinUI 3 styling, Mica backdrop, and system theme support â€” while staying as light and fast as possible.

![Inklet](Inklet.png)

## Features

### File Operations
- **New** â€” Start a fresh document in the current tab
- **New Tab** â€” Open an additional editor tab (Ctrl+T)
- **Close Tab** â€” Close the current tab (Ctrl+W)
- **Open** â€” Open any text file with automatic encoding detection
- **Save / Save As** â€” Save with your choice of encoding (Ctrl+S / Ctrl+Shift+S)
- **Print / Page Setup** â€” Full Windows print support (Ctrl+P)
- **Drag & Drop** â€” Drop files directly onto the editor

### Edit Operations
- **Undo / Redo** â€” Ctrl+Z / Ctrl+Y
- **Cut / Copy / Paste / Delete** â€” Standard clipboard operations
- **Find & Replace** â€” With match-case option (Ctrl+F / Ctrl+H)
- **Find Next / Previous** â€” F3 / Shift+F3
- **Go To Line** â€” Jump to a specific line number (Ctrl+G)
- **Select All** â€” Select entire document (Ctrl+A)
- **Time/Date** â€” Insert current timestamp (F5)

### Tabs & Session
- **Multi-tab editing** â€” Any number of tabs open simultaneously
- **Session persistence** â€” All tab content, cursor position, encoding, and line endings are automatically saved on close and restored on next launch (including unsaved content in untitled tabs)
- **Tab headers** â€” `*` prefix indicates unsaved changes

### Format
- **Word Wrap** â€” Toggle word wrap on/off
- **Font** â€” Choose font family, style, and size

### View
- **Status Bar** â€” Line/column position, encoding, line ending, zoom level
- **Zoom** â€” Ctrl+Scroll, Ctrl+Plus/Minus, or View â€º Zoom menu (25%â€“500%)

### Encoding Support
- UTF-8 (with and without BOM)
- UTF-16 LE / BE
- ANSI (system default)
- Auto-detection of file encoding including international code pages
- Extensive code page support (Shift-JIS, GB2312, ISO-8859-x, and more)

### Line Endings
- Windows (CRLF)
- Unix (LF)
- Classic Mac (CR)
- Automatic detection and display in status bar

### Performance
- Instant startup
- Graceful handling of large files with async loading
- Minimal memory footprint
- Mica backdrop for modern appearance without overhead

### File Associations
- Registers as an "Open With" handler for common text formats: `.txt`, `.log`, `.ini`, `.cfg`, `.md`, `.xml`, `.json`, `.csv`, `.yaml`, `.yml`

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Windows 11 supported

## Building

1. Open `Inklet.slnx` in Visual Studio 2022 17.8+ or Visual Studio 2026
2. Set **Inklet (Package)** as the startup project
3. Build and run (F5)

> Default editor font size is 12 pt (Consolas).

## Testing

```bash
dotnet test Inklet.Tests
```

## License

This project is licensed for personal, non-commercial use only. See [LICENSE](LICENSE) for details.

## Author

John Donnelly â€” [JAD Apps](https://github.com/John-Donnelly)

Â© 2025 JAD Apps. All rights reserved.



