using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inklet.Models;

namespace Inklet.Services;

/// <summary>
/// Handles file I/O operations with encoding detection and large file support.
/// </summary>
internal static class FileService
{
    /// <summary>
    /// Maximum file size (in bytes) to load entirely into memory. Files larger
    /// than this will still be loaded but trigger a progress indication.
    /// </summary>
    internal const long LargeFileThreshold = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Reads a file from disk with automatic encoding detection.
    /// </summary>
    /// <param name="filePath">Absolute path to the file.</param>
    /// <param name="cancellationToken">Cancellation token for large file operations.</param>
    /// <returns>The file content as a string and the detected document state.</returns>
    internal static async Task<(string Content, DocumentState State)> ReadFileAsync(
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
    internal static async Task WriteFileAsync(
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
    internal static long GetFileSize(string filePath)
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
}
