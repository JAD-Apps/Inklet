using System;

namespace Inklet.Models;

/// <summary>
/// Holds the full runtime state for a single editor tab.
/// </summary>
internal sealed class TabSession
{
    internal Guid Id { get; } = Guid.NewGuid();

    /// <summary>File path on disk, or null for untitled tabs.</summary>
    internal string? FilePath { get; set; }

    /// <summary>Current text content of the tab.</summary>
    internal string Content { get; set; } = string.Empty;

    /// <summary>Content as it was when last saved (for dirty-check).</summary>
    internal string SavedContent { get; set; } = string.Empty;

    /// <summary>Cursor position within the content.</summary>
    internal int CursorPosition { get; set; }

    /// <summary>Document metadata (encoding, line ending, etc.).</summary>
    internal DocumentState Document { get; set; } = new();

    /// <summary>Whether the tab has unsaved changes.</summary>
    internal bool IsModified => Content != SavedContent;

    /// <summary>Label shown on the tab strip.</summary>
    internal string TabTitle => (IsModified ? "*" : "") +
                                (FilePath is null ? "Untitled" : System.IO.Path.GetFileName(FilePath));
}
