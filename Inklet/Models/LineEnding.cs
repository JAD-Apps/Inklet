namespace Inklet.Models;

/// <summary>
/// Represents detected or user-selected line ending style.
/// </summary>
public enum LineEndingStyle
{
    /// <summary>Windows-style CR+LF (\r\n).</summary>
    CrLf,

    /// <summary>Unix-style LF (\n).</summary>
    Lf,

    /// <summary>Classic Mac-style CR (\r).</summary>
    Cr,

    /// <summary>File contains mixed line endings.</summary>
    Mixed
}

/// <summary>
/// Utility methods for detecting and converting line endings.
/// </summary>
public static class LineEndingDetector
{
    /// <summary>
    /// Detects the predominant line ending style in the given text.
    /// </summary>
    public static LineEndingStyle Detect(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LineEndingStyle.CrLf;
        }

        int crLfCount = 0;
        int lfCount = 0;
        int crCount = 0;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crLfCount++;
                    i++; // skip the \n
                }
                else
                {
                    crCount++;
                }
            }
            else if (text[i] == '\n')
            {
                lfCount++;
            }
        }

        int total = crLfCount + lfCount + crCount;
        if (total == 0)
        {
            return LineEndingStyle.CrLf;
        }

        // If only one type is present, return it
        if (crLfCount > 0 && lfCount == 0 && crCount == 0) return LineEndingStyle.CrLf;
        if (lfCount > 0 && crLfCount == 0 && crCount == 0) return LineEndingStyle.Lf;
        if (crCount > 0 && crLfCount == 0 && lfCount == 0) return LineEndingStyle.Cr;

        return LineEndingStyle.Mixed;
    }

    /// <summary>
    /// Returns the string representation of a line ending style.
    /// </summary>
    public static string GetLineEndingString(LineEndingStyle style) => style switch
    {
        LineEndingStyle.CrLf => "\r\n",
        LineEndingStyle.Lf => "\n",
        LineEndingStyle.Cr => "\r",
        _ => "\r\n"
    };

    /// <summary>
    /// Returns a display label for the status bar.
    /// </summary>
    public static string GetDisplayName(LineEndingStyle style) => style switch
    {
        LineEndingStyle.CrLf => "Windows (CRLF)",
        LineEndingStyle.Lf => "Unix (LF)",
        LineEndingStyle.Cr => "Macintosh (CR)",
        LineEndingStyle.Mixed => "Mixed",
        _ => "Windows (CRLF)"
    };

    /// <summary>
    /// Normalizes all line endings in text to the specified style.
    /// Returns <see cref="string.Empty"/> for null or empty input — the previous
    /// behaviour of returning null on null input violated the non-nullable return
    /// contract and caused crashes in callers that didn't expect it.
    /// </summary>
    public static string Normalize(string text, LineEndingStyle targetStyle)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        // First normalize everything to \n, then convert to target
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var target = GetLineEndingString(targetStyle);

        return target == "\n" ? normalized : normalized.Replace("\n", target);
    }
}
