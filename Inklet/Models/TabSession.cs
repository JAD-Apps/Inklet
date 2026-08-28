using System;
using System.Text.Json.Serialization;
using Inklet.Editor;
using Inklet.Engine;
using Inklet.Services;

namespace Inklet.Models;

/// <summary>
/// Flat, JSON-serializable snapshot of a tab, schema v1. Retained ONLY so old
/// session files can be migrated; new sessions persist
/// <see cref="Inklet.Engine.SessionTabState"/> (schema v2, edit deltas instead
/// of full content for file-backed tabs).
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
/// Runtime state for a single editor tab. The tab OWNS its engine document -
/// switching tabs swaps the document reference into the editor (no text moves,
/// undo history and dirty state live with the document), and the view state
/// (caret/selection/scroll) is captured here across switches.
/// </summary>
public sealed class TabSession : IDisposable
{
    /// <summary>File path on disk, or null for untitled tabs.</summary>
    public string? FilePath { get; set; }

    /// <summary>The engine document behind this tab.</summary>
    internal Document? Doc { get; set; }

    /// <summary>
    /// Session deltas waiting to be applied once the document finishes indexing
    /// (restored dirty tabs over large files), or null.
    /// </summary>
    internal SessionTabState? PendingSessionState { get; set; }

    /// <summary>Caret/selection/scroll captured when the tab was last active.</summary>
    internal EditorViewState View { get; set; } = EditorViewState.Default;

    /// <summary>Document metadata for the status bar (encoding, line ending).</summary>
    public DocumentState Document { get; set; } = new();

    /// <summary>
    /// Per-tab watcher for external file modifications. Null for untitled tabs.
    /// MainWindow attaches/detaches this when FilePath changes.
    /// </summary>
    internal FileChangeWatcher? Watcher { get; set; }

    /// <summary>
    /// The dirty state the tab header currently shows; MainWindow refreshes the
    /// header only when <see cref="IsModified"/> departs from this.
    /// </summary>
    internal bool ShownDirty { get; set; }

    /// <summary>
    /// Whether the tab has unsaved changes. Derived from the document's undo
    /// position vs its saved mark, which restores the classic "undo back to the
    /// saved state leaves the tab clean" behaviour.
    /// </summary>
    public bool IsModified => Doc?.IsDirty ?? false;

    /// <summary>Label shown on the tab strip.</summary>
    public string TabTitle => (IsModified ? "*" : "") +
                               (FilePath is null ? "Untitled" : System.IO.Path.GetFileName(FilePath));

    public void Dispose()
    {
        Watcher?.Dispose();
        Watcher = null;
        Doc?.Dispose();
        Doc = null;
    }
}
