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
    /// <summary>File path on disk, or null for untitled tabs.</summary>
    public string? FilePath { get; set; }

    private string _content = string.Empty;
    private string _savedContent = string.Empty;
    private bool _isDirty;

    /// <summary>
    /// Current text content of the tab. Setting this stamps the dirty flag in O(1)
    /// — the previous implementation did a full O(N) string equality on every
    /// <see cref="IsModified"/> read, called per-keystroke from the tab title refresh.
    /// </summary>
    public string Content
    {
        get => _content;
        set
        {
            // Reference-equal updates are no-ops (e.g. assigning a captured snapshot
            // back to itself). Anything else marks the tab dirty; like Notepad we don't
            // try to detect "user typed and undid back to saved" — that pessimisation
            // would re-introduce the per-keystroke O(N) compare.
            if (ReferenceEquals(_content, value)) return;
            _content = value;
            _isDirty = !ReferenceEquals(_content, _savedContent);
            Lines.Invalidate(_content);
        }
    }

    /// <summary>
    /// Content as it was when last saved. Setting this clears the dirty flag iff the
    /// new value is the same reference as the current Content; otherwise it just
    /// records the baseline. Prefer <see cref="MarkSaved"/> in save paths.
    /// </summary>
    public string SavedContent
    {
        get => _savedContent;
        set
        {
            _savedContent = value;
            _isDirty = !ReferenceEquals(_content, _savedContent);
        }
    }

    /// <summary>Cursor position within the content.</summary>
    public int CursorPosition { get; set; }

    /// <summary>Document metadata (encoding, line ending, etc.).</summary>
    public DocumentState Document { get; set; } = new();

    /// <summary>
    /// Per-tab cached line-start index for the editor's current text. Owned by the
    /// tab so that switching away and back doesn't re-scan the whole document.
    /// </summary>
    internal LineIndex Lines { get; } = new();

    /// <summary>
    /// Whether the tab has unsaved changes. O(1) — see <see cref="Content"/>.
    /// </summary>
    public bool IsModified => _isDirty;

    /// <summary>
    /// Marks the current content as saved. Use this after a successful write rather
    /// than assigning <c>SavedContent = Content</c> — the former is intent-revealing
    /// and the latter requires the caller to know the dirty flag will reset because
    /// the references match.
    /// </summary>
    public void MarkSaved()
    {
        _savedContent = _content;
        _isDirty = false;
    }

    /// <summary>Label shown on the tab strip.</summary>
    public string TabTitle => (_isDirty ? "*" : "") +
                               (FilePath is null ? "Untitled" : System.IO.Path.GetFileName(FilePath));
}
