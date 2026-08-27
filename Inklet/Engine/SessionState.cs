using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Inklet.Engine;

/// <summary>
/// Serialisable per-tab document state, schema v2: file-backed tabs persist a
/// fingerprint plus edit deltas (original-range references + typed text), never
/// the file's content. A 20 GB file with a handful of edits captures in ~1 KB.
/// </summary>
internal sealed class SessionTabState
{
    [JsonPropertyName("v")] public int SchemaVersion { get; set; } = 2;
    [JsonPropertyName("path")] public string? FilePath { get; set; }
    [JsonPropertyName("size")] public long FileSize { get; set; }
    [JsonPropertyName("mtime")] public long FileMTimeTicks { get; set; }
    [JsonPropertyName("enc")] public int EncodingCodePage { get; set; }
    [JsonPropertyName("bom")] public bool HasBom { get; set; }
    [JsonPropertyName("eol")] public int LineEnding { get; set; }
    [JsonPropertyName("caret")] public long CaretOffset { get; set; }
    [JsonPropertyName("anchor")] public long AnchorOffset { get; set; }
    [JsonPropertyName("scroll")] public long ScrollLine { get; set; }

    /// <summary>Deltas for a dirty file-backed tab; null when clean (open fresh).</summary>
    [JsonPropertyName("pieces")] public List<SessionPiece>? Pieces { get; set; }

    /// <summary>Full content for untitled tabs (v1 parity).</summary>
    [JsonPropertyName("content")] public string? UntitledContent { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this, SessionJson.Options);
    public static SessionTabState? FromJson(string json)
        => JsonSerializer.Deserialize<SessionTabState>(json, SessionJson.Options);
}

internal sealed class SessionPiece
{
    /// <summary>"o" = original-file char range; "a" = typed text carried inline.</summary>
    [JsonPropertyName("k")] public string Kind { get; set; } = "o";
    [JsonPropertyName("s")] public long Start { get; set; }
    [JsonPropertyName("l")] public long Length { get; set; }
    [JsonPropertyName("t")] public string? Text { get; set; }
}

internal static class SessionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

internal sealed partial class Document
{
    /// <summary>
    /// Captures this document's session state. Cheap: walks the piece list and
    /// extracts only typed text. Returns null when the state cannot be captured
    /// yet (a dirty streamed document still indexing) - the caller should keep
    /// its previous snapshot.
    /// </summary>
    public SessionTabState? CaptureSessionState()
    {
        if (FilePath is not null && !IsFullyIndexed && IsDirty) return null;

        var state = new SessionTabState
        {
            FilePath = FilePath,
            EncodingCodePage = Encoding.CodePage,
            HasBom = HasBom,
            LineEnding = (int)LineEnding,
        };

        if (FilePath is null)
        {
            // Untitled: full content, exactly like schema v1.
            state.UntitledContent = GetText(0, Length);
            return state;
        }

        if (FilePath is not null)
        {
            try
            {
                var fi = new System.IO.FileInfo(FilePath);
                state.FileSize = fi.Length;
                state.FileMTimeTicks = fi.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                // Missing file: still capture; restore will surface the mismatch.
            }
        }

        if (!IsDirty) return state; // clean: fingerprint + view state only

        var pieces = new List<Piece>();
        PieceTreeOps.CollectPieces(Root, pieces);
        state.Pieces = new List<SessionPiece>(pieces.Count);
        foreach (var p in pieces)
        {
            if (p.Kind == PieceBufferKind.Original)
            {
                state.Pieces.Add(new SessionPiece { Kind = "o", Start = p.Start, Length = p.Length });
            }
            else
            {
                var text = new char[p.Length];
                _add.CopyTo(p.Start, (int)p.Length, text);
                state.Pieces.Add(new SessionPiece { Kind = "a", Text = new string(text) });
            }
        }
        return state;
    }

    /// <summary>
    /// Rebuilds the piece tree from captured deltas. The document must be a
    /// freshly opened, fully indexed instance of the same file (the caller
    /// validates the fingerprint first). The restored state reads as dirty
    /// until the user saves; undo history does not survive restarts (v1 parity).
    /// </summary>
    public void ApplySessionState(SessionTabState state)
    {
        if (state.Pieces is null) return;
        if (!IsFullyIndexed)
            throw new InvalidOperationException("Apply session deltas only after indexing completes.");

        var rebuilt = new List<Piece>(state.Pieces.Count);
        foreach (var sp in state.Pieces)
        {
            if (sp.Kind == "o")
            {
                if (sp.Length <= 0) continue;
                rebuilt.Add(Piece.Create(PieceBufferKind.Original, _originalBuf, sp.Start, sp.Length));
            }
            else
            {
                string text = sp.Text ?? "";
                if (text.Length == 0) continue;
                long addStart = _add.Append(text);
                rebuilt.Add(Piece.Create(PieceBufferKind.Add, _add, addStart, text.Length));
            }
        }
        long breaksBefore = PieceTreeOps.Breaks(Root);
        long oldLength = Length;
        System.Threading.Volatile.Write(ref _root, PieceTreeOps.Build(rebuilt.ToArray()));
        _undo.Clear();
        _undo.MarkUnreachableDirty();
        RaiseChanged(0, oldLength, Length, breaksBefore);
    }

    /// <summary>True when the on-disk file still matches the captured fingerprint.</summary>
    public static bool FingerprintMatches(SessionTabState state)
    {
        if (state.FilePath is null) return true;
        try
        {
            var fi = new System.IO.FileInfo(state.FilePath);
            return fi.Exists && fi.Length == state.FileSize && fi.LastWriteTimeUtc.Ticks == state.FileMTimeTicks;
        }
        catch
        {
            return false;
        }
    }
}
