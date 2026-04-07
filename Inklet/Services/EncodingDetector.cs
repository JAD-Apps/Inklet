using System;
using System.IO;
using System.Text;
using UtfUnknown;

namespace Inklet.Services;

/// <summary>
/// Detects encoding of text files using BOM analysis and statistical heuristics.
/// </summary>
public static class EncodingDetector
{
    /// <summary>
    /// Detects the encoding of a file by examining its byte order mark and content.
    /// </summary>
    /// <param name="data">Raw file bytes.</param>
    /// <returns>The detected encoding and whether a BOM was present.</returns>
    public static (Encoding Encoding, bool HasBom) Detect(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Length == 0)
        {
            return (Encoding.UTF8, false);
        }

        // Check for BOM first — most reliable indicator
        var bomResult = DetectBom(data);
        if (bomResult.HasValue)
        {
            return bomResult.Value;
        }

        // Check if the content is valid UTF-8 first — prefer UTF-8 over single-byte
        // encodings because UTF-8 is the most common encoding and ASCII is a subset of it.
        if (IsValidUtf8(data))
        {
            return (Encoding.UTF8, false);
        }

        // Use UTF.Unknown for statistical detection of non-UTF-8 encodings
        var detectionResult = CharsetDetector.DetectFromBytes(data);
        if (detectionResult.Detected is { } detected && detected.Confidence > 0.5f)
        {
            try
            {
                var encoding = Encoding.GetEncoding(detected.EncodingName);
                return (encoding, false);
            }
            catch (ArgumentException)
            {
                // Unknown encoding name, fall through
            }
        }

        // Default to system ANSI code page
        return (Encoding.GetEncoding(0), false);
    }

    /// <summary>
    /// Detects encoding from a byte order mark at the start of the data.
    /// </summary>
    private static (Encoding Encoding, bool HasBom)? DetectBom(byte[] data)
    {
        // UTF-32 BE: 00 00 FE FF (check before UTF-16 BE)
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF)
        {
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: true), true);
        }

        // UTF-32 LE: FF FE 00 00 (check before UTF-16 LE)
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00)
        {
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: true), true);
        }

        // UTF-8 BOM: EF BB BF
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), true);
        }

        // UTF-16 BE: FE FF
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true), true);
        }

        // UTF-16 LE: FF FE
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true), true);
        }

        return null;
    }

    /// <summary>
    /// Validates whether the byte array is valid UTF-8.
    /// </summary>
    private static bool IsValidUtf8(byte[] data)
    {
        int i = 0;
        bool hasMultiByte = false;

        while (i < data.Length)
        {
            byte b = data[i];

            int sequenceLength;
            if (b <= 0x7F)
            {
                sequenceLength = 1;
            }
            else if (b >= 0xC2 && b <= 0xDF)
            {
                sequenceLength = 2;
                hasMultiByte = true;
            }
            else if (b >= 0xE0 && b <= 0xEF)
            {
                sequenceLength = 3;
                hasMultiByte = true;
            }
            else if (b >= 0xF0 && b <= 0xF4)
            {
                sequenceLength = 4;
                hasMultiByte = true;
            }
            else
            {
                return false;
            }

            if (i + sequenceLength > data.Length)
            {
                return false;
            }

            for (int j = 1; j < sequenceLength; j++)
            {
                if (data[i + j] < 0x80 || data[i + j] > 0xBF)
                {
                    return false;
                }
            }

            i += sequenceLength;
        }

        // Pure ASCII is also valid UTF-8
        return true;
    }
}
