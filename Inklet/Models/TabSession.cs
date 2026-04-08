using System;
using System.Text.Json.Serialization;

namespace Inklet.Models;

/// <summary>
/// Flat, JSON-serializable snapshot of a tab saved at session close.
/// </summary>
public sealed record PersistedTabData
{
    /// <summary>Absolute file path, or null for untitled tabs.</summary>
    [JsonPropertyName("path")]
    public string? FilePath { get; init; }

    /// <summary>Full text content (used for unsaved/untitled tabs).</summary>
    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;

    /// <summary>Whether the tab had unsaved changes at session close.</summary>
    [JsonPropertyName("dirty")]
    public bool IsModified { get; init; }

    /// <summary>Caret position to restore.</summary>
    [JsonPropertyName("cursor")]
    public int CursorPosition { get; init; }

    /// <summary>Encoding code page (e.g. 65001 = UTF-8).</summary>
    [JsonPropertyName("encoding")]
    public int EncodingCodePage { get; init; } = 65001;

    /// <summary>Whether the file had a BOM.</summary>
    [JsonPropertyName("bom")]
    public bool HasBom { get; init; }

    /// <summary>Line ending style (0=CRLF, 1=LF, 2=CR).</summary>
    [JsonPropertyName("lineEnding")]
    public int LineEnding { get; init; }
}

/// <summary>
/// Holds the full runtime state for a single editor tab.
/// </summary>
public sealed class TabSession
{
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>File path on disk, or null for untitled tabs.</summary>
    public string? FilePath { get; set; }

    /// <summary>Current text content of the tab.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Content as it was when last saved (for dirty-check).</summary>
    public string SavedContent { get; set; } = string.Empty;

    /// <summary>Cursor position within the content.</summary>
    public int CursorPosition { get; set; }

    /// <summary>Document metadata (encoding, line ending, etc.).</summary>
    public DocumentState Document { get; set; } = new();

    /// <summary>Whether the tab has unsaved changes.</summary>
    public bool IsModified => Content != SavedContent;

    /// <summary>Label shown on the tab strip.</summary>
    public string TabTitle => (IsModified ? "*" : "") +
                               (FilePath is null ? "Untitled" : System.IO.Path.GetFileName(FilePath));
}
