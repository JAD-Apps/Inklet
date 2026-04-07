# Inklet

A lightweight, modern Notepad clone for Windows built with WinUI 3 and .NET 8.

Inklet faithfully recreates the classic Windows 10 Notepad experience with modern WinUI 3 styling, Mica backdrop, and system theme support — while staying as light and fast as possible.

![Inklet](Inklet.png)

## Features

### File Operations
- **New** — Start a fresh document
- **Open** — Open any text file with automatic encoding detection
- **Save / Save As** — Save with your choice of encoding
- **Print / Page Setup** — Full Windows print support
- **Drag & Drop** — Drop files directly onto the editor

### Edit Operations
- **Undo** — Single-level undo
- **Cut / Copy / Paste / Delete** — Standard clipboard operations
- **Find & Replace** — With match-case option
- **Go To Line** — Jump to a specific line number
- **Select All** — Select entire document
- **Time/Date** — Insert current timestamp

### Format
- **Word Wrap** — Toggle word wrap on/off
- **Font** — Choose font family, style, and size

### View
- **Status Bar** — Line/column position, encoding, line ending, zoom level
- **Zoom** — Ctrl+Scroll or Ctrl+Plus/Minus (25%–500%)

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

## Requirements

- Windows 10 version 1809 (build 17763) or later
- Windows 11 supported

## Building

1. Open `Inklet.slnx` in Visual Studio 2022 17.8+ or Visual Studio 2026
2. Set **Inklet (Package)** as the startup project
3. Build and run (F5)

## Testing

```bash
dotnet test Inklet.Tests
```

## License

This project is licensed for personal, non-commercial use only. See [LICENSE](LICENSE) for details.

## Author

John Donnelly — [JAD Apps](https://github.com/John-Donnelly)

© 2025 JAD Apps. All rights reserved.
