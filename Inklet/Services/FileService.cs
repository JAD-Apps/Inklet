using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inklet.Models;

namespace Inklet.Services;

/// <summary>
/// Handles file I/O operations with encoding detection and large file support.
/// </summary>
public static class FileService
{
    /// <summary>
    /// Maximum file size (in bytes) to load entirely into memory. Files larger
    /// than this will still be loaded but trigger a progress indication.
    /// </summary>
    public const long LargeFileThreshold = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Reads a file from disk with automatic encoding detection.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="cancellationToken">Cancellation token for large file operations.</param>
    /// <returns>The file content as a string and the detected document state.</returns>
    public static async Task<(string Content, DocumentState State)> ReadFileAsync(
        string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        var data = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        // Get the BOM preamble length to skip it when decoding
        int preambleLength = hasBom ? encoding.GetPreamble().Length : 0;
        var content = encoding.GetString(data, preambleLength, data.Length - preambleLength);

        var lineEnding = LineEndingDetector.Detect(content);

        var state = new DocumentState
        {
            FilePath = filePath,
            Encoding = encoding,
            HasBom = hasBom,
            LineEnding = lineEnding == LineEndingStyle.Mixed ? LineEndingStyle.CrLf : lineEnding
        };

        return (content, state);
    }

    /// <summary>
    /// Writes text content to a file with the specified encoding.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="content">Text content to write.</param>
    /// <param name="encoding">Encoding to use when writing.</param>
    /// <param name="writeBom">Whether to write a byte order mark.</param>
    /// <param name="lineEnding">Line ending style to normalize to before writing.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task WriteFileAsync(
        string filePath,
        string content,
        Encoding encoding,
        bool writeBom,
        LineEndingStyle lineEnding,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("File path cannot be null or empty.", nameof(filePath));
        }

        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(encoding);

        // Normalize line endings before writing
        var normalizedContent = LineEndingDetector.Normalize(content, lineEnding);

        // Create an encoding instance that respects the BOM preference
        var writeEncoding = CreateEncoding(encoding, writeBom);

        await File.WriteAllTextAsync(filePath, normalizedContent, writeEncoding, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates an encoding instance with the specified BOM setting.
    /// </summary>
    private static Encoding CreateEncoding(Encoding sourceEncoding, bool emitBom)
    {
        return sourceEncoding.CodePage switch
        {
            65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom),
            1200 => new UnicodeEncoding(bigEndian: false, byteOrderMark: emitBom),
            1201 => new UnicodeEncoding(bigEndian: true, byteOrderMark: emitBom),
            12000 => new UTF32Encoding(bigEndian: false, byteOrderMark: emitBom),
            12001 => new UTF32Encoding(bigEndian: true, byteOrderMark: emitBom),
            _ => sourceEncoding
        };
    }

    /// <summary>
    /// Returns the file size in bytes, or -1 if the file does not exist.
    /// </summary>
    public static long GetFileSize(string filePath)
    {
        try
        {
            var info = new FileInfo(filePath);
            return info.Exists ? info.Length : -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// File extensions that are known binary formats and should not be opened as text.
    /// </summary>
    private static readonly HashSet<string> s_binaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Executables / libraries
        ".exe", ".dll", ".sys", ".ocx", ".drv", ".com", ".scr",
        // Installers / packages
        ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle",
        // Archives
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".cab", ".iso", ".dmg",
        // Images
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".tiff", ".tif", ".webp", ".svg",
        // Audio / Video
        ".mp3", ".wav", ".flac", ".aac", ".ogg", ".wma",
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".flv", ".webm",
        // Documents (binary)
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        // Databases
        ".db", ".sqlite", ".mdb", ".accdb",
        // .NET / Java
        ".pdb", ".nupkg", ".jar", ".class",
        // Fonts
        ".ttf", ".otf", ".woff", ".woff2",
        // Other
        ".bin", ".dat", ".o", ".obj", ".lib", ".a", ".so", ".dylib",
    };

    /// <summary>
    /// Determines whether a file appears to be a binary (non-text) file.
    /// Checks the file extension first, then sniffs the first 8 KB for NUL bytes.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <returns><c>true</c> if the file is likely binary; <c>false</c> if it appears to be text.</returns>
    public static bool IsBinaryFile(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        var ext = Path.GetExtension(filePath);
        if (!string.IsNullOrEmpty(ext) && s_binaryExtensions.Contains(ext))
            return true;

        // Sniff the first 8 KB for NUL bytes — a strong indicator of binary content.
        try
        {
            using var stream = File.OpenRead(filePath);
            var buffer = new byte[Math.Min(8192, stream.Length)];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);
            for (int i = 0; i < bytesRead; i++)
            {
                if (buffer[i] == 0x00)
                    return true;
            }
        }
        catch
        {
            // If we can't read the file, let the caller handle the error later.
        }

        return false;
    }
}
