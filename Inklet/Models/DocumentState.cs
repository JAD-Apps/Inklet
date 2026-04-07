using System.Text;

namespace Inklet.Models;

/// <summary>
/// Immutable snapshot of a loaded document's state.
/// </summary>
public sealed record DocumentState
{
    /// <summary>File path on disk, or null for untitled documents.</summary>
    public string? FilePath { get; init; }

    /// <summary>The encoding used when the file was read (or the user's selection).</summary>
    public Encoding Encoding { get; init; } = Encoding.UTF8;

    /// <summary>Whether the encoding included a BOM when read.</summary>
    public bool HasBom { get; init; }

    /// <summary>Detected or selected line ending style.</summary>
    public LineEndingStyle LineEnding { get; init; } = LineEndingStyle.CrLf;

    /// <summary>Display name for the encoding, shown in the status bar.</summary>
    public string EncodingDisplayName => GetEncodingDisplayName(Encoding, HasBom);

    /// <summary>Display name for the file, shown in the title bar.</summary>
    public string DisplayFileName => FilePath is null
        ? "Untitled"
        : System.IO.Path.GetFileName(FilePath);

    private static string GetEncodingDisplayName(Encoding encoding, bool hasBom)
    {
        if (encoding.CodePage == 65001) // UTF-8
        {
            return hasBom ? "UTF-8 with BOM" : "UTF-8";
        }

        if (encoding.CodePage == 1200) return "UTF-16 LE";
        if (encoding.CodePage == 1201) return "UTF-16 BE";

        return encoding.EncodingName;
    }
}
