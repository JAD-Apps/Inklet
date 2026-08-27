using System;
using System.Text;

namespace Inklet.Engine;

/// <summary>How a codec's bytes map to UTF-16 units, which decides the engine path.</summary>
internal enum CodecClass
{
    Utf8,        // variable width, self-synchronising - streamed with index samples
    Utf16LE,     // unit = 2 bytes, direct arithmetic mapping
    Utf16BE,
    SingleByte,  // unit = 1 byte, direct arithmetic mapping
    Unstreamable, // DBCS / stateful / UTF-32 - full-decode fallback path
}

/// <summary>Carry state for a sequential multi-segment scan.</summary>
internal struct ScanCarry
{
    public bool PrevEndsWithCr;   // segment ended with a CR: its break lands in the next segment
}

/// <summary>Per-segment scan result (counts are segment-owned; see ScanCarry).</summary>
internal struct SegmentScan
{
    public long Utf16Units;
    public long BreakEnds;
    public long CrLf;             // EOL statistics (exact when the scan completes)
    public long LoneLf;
    public long LoneCr;
}

/// <summary>
/// Optional tier-2 collector: char offsets are segment-local UTF-16 unit offsets.
/// </summary>
internal sealed class SegmentDetail
{
    public required int[] BreakEndUnits;       // segment-local unit offset just after each break
                                               // (int: a segment holds far fewer units than 2^31)
    public required int[] SampleUnitCum;       // cumulative units at every SampleBytes boundary
    public const int SampleBytes = 4096;
}

/// <summary>
/// Byte-level text codec used by the streaming engine: counts UTF-16 units and
/// line breaks per segment, and decodes byte ranges on demand. Only classes
/// where a byte position can be locally mapped to a char position are
/// streamable; everything else takes the full-decode fallback at open.
/// </summary>
internal sealed class TextCodec
{
    public CodecClass Class { get; }
    public Encoding Encoding { get; }
    public int PreambleLength { get; }

    private readonly Decoder _decoder; // used only under lock for fallback paths

    private TextCodec(CodecClass cls, Encoding encoding, int preambleLength)
    {
        Class = cls;
        Encoding = encoding;
        PreambleLength = preambleLength;
        _decoder = encoding.GetDecoder();
    }

    /// <summary>Classifies a detected encoding; Unstreamable means use full decode.</summary>
    public static TextCodec Create(Encoding encoding, bool hasBom)
    {
        int preamble = hasBom ? encoding.GetPreamble().Length : 0;
        CodecClass cls = encoding.CodePage switch
        {
            65001 => CodecClass.Utf8,
            1200 => CodecClass.Utf16LE,
            1201 => CodecClass.Utf16BE,
            _ when encoding.IsSingleByte => CodecClass.SingleByte,
            _ => CodecClass.Unstreamable,
        };
        return new TextCodec(cls, encoding, preamble);
    }

    // ── Scanning ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans one segment of content bytes (preamble already excluded), updating
    /// carry. When detail is wanted, records segment-local break ends and
    /// per-4KB char samples. isFinal adds the trailing-CR break of the file.
    /// </summary>
    public SegmentScan ScanSegment(ReadOnlySpan<byte> bytes, ref ScanCarry carry, bool isFinal,
        System.Collections.Generic.List<long>? breakEnds = null,
        System.Collections.Generic.List<int>? sampleCums = null)
    {
        return Class switch
        {
            CodecClass.Utf8 or CodecClass.SingleByte
                => ScanByteOriented(bytes, ref carry, isFinal, Class == CodecClass.Utf8, breakEnds, sampleCums),
            CodecClass.Utf16LE => ScanUtf16(bytes, ref carry, isFinal, bigEndian: false, breakEnds, sampleCums),
            CodecClass.Utf16BE => ScanUtf16(bytes, ref carry, isFinal, bigEndian: true, breakEnds, sampleCums),
            _ => throw new InvalidOperationException("Unstreamable codec cannot be scanned."),
        };
    }

    private static SegmentScan ScanByteOriented(ReadOnlySpan<byte> bytes, ref ScanCarry carry, bool isFinal,
        bool utf8, System.Collections.Generic.List<long>? breakEnds, System.Collections.Generic.List<int>? sampleCums)
    {
        var r = new SegmentScan();
        long units = 0;
        int nextSample = 0;
        // Pending carry: a CR that ended the previous segment. Its break belongs to
        // THIS segment (end = our unit 0, or unit 1 when we start with the LF half).
        if (carry.PrevEndsWithCr)
        {
            if (bytes.Length > 0 && bytes[0] == (byte)'\n')
            {
                // handled when the LF is consumed below (as a CRLF continuation)
            }
            else
            {
                r.BreakEnds++;
                r.LoneCr++;
                breakEnds?.Add(0);
            }
        }

        for (int i = 0; i < bytes.Length; i++)
        {
            if (sampleCums is not null && i >= nextSample)
            {
                sampleCums.Add((int)units);
                nextSample += SegmentDetail.SampleBytes;
            }
            byte b = bytes[i];
            if (utf8)
            {
                // UTF-16 unit count: every non-continuation byte is one unit; F0-F4
                // lead bytes (4-byte scalars) contribute a second unit.
                if ((b & 0xC0) != 0x80)
                {
                    units++;
                    if (b >= 0xF0) units++;
                }
            }
            else
            {
                units++;
            }

            if (b == (byte)'\n')
            {
                bool afterCr = i > 0 ? bytes[i - 1] == (byte)'\r' : carry.PrevEndsWithCr;
                r.BreakEnds++;
                if (afterCr) r.CrLf++; else r.LoneLf++;
                breakEnds?.Add(units); // unit count already includes this LF
            }
            else if (b == (byte)'\r')
            {
                if (i + 1 < bytes.Length)
                {
                    if (bytes[i + 1] != (byte)'\n')
                    {
                        r.BreakEnds++;
                        r.LoneCr++;
                        breakEnds?.Add(units);
                    }
                    // else: counted at the LF
                }
                // else: trailing CR - carried to the next segment (or finalised below)
            }
        }
        if (bytes.Length > 0) carry.PrevEndsWithCr = bytes[^1] == (byte)'\r';
        if (isFinal && carry.PrevEndsWithCr)
        {
            r.BreakEnds++;
            r.LoneCr++;
            breakEnds?.Add(units);
            carry.PrevEndsWithCr = false;
        }
        r.Utf16Units = units;
        return r;
    }

    private static SegmentScan ScanUtf16(ReadOnlySpan<byte> bytes, ref ScanCarry carry, bool isFinal,
        bool bigEndian, System.Collections.Generic.List<long>? breakEnds, System.Collections.Generic.List<int>? sampleCums)
    {
        var r = new SegmentScan();
        long units = 0;
        int nextSample = 0;
        int usable = bytes.Length & ~1; // segment sizes are even; the file tail may not be
        if (carry.PrevEndsWithCr)
        {
            ushort first = usable >= 2 ? ReadU16(bytes, 0, bigEndian) : (ushort)0;
            if (first != '\n')
            {
                r.BreakEnds++;
                r.LoneCr++;
                breakEnds?.Add(0);
            }
        }
        for (int i = 0; i < usable; i += 2)
        {
            if (sampleCums is not null && i >= nextSample)
            {
                sampleCums.Add((int)units);
                nextSample += SegmentDetail.SampleBytes;
            }
            ushort u = ReadU16(bytes, i, bigEndian);
            units++;
            if (u == '\n')
            {
                bool afterCr = i >= 2 ? ReadU16(bytes, i - 2, bigEndian) == '\r' : carry.PrevEndsWithCr;
                r.BreakEnds++;
                if (afterCr) r.CrLf++; else r.LoneLf++;
                breakEnds?.Add(units);
            }
            else if (u == '\r')
            {
                if (i + 2 < usable && ReadU16(bytes, i + 2, bigEndian) != '\n')
                {
                    r.BreakEnds++;
                    r.LoneCr++;
                    breakEnds?.Add(units);
                }
                else if (i + 2 >= usable)
                {
                    // trailing CR: carried
                }
            }
        }
        // A lone trailing byte (malformed file) still decodes to one replacement unit.
        if (usable < bytes.Length) units++;
        carry.PrevEndsWithCr = usable >= 2 && ReadU16(bytes, usable - 2, bigEndian) == '\r';
        if (isFinal && carry.PrevEndsWithCr)
        {
            r.BreakEnds++;
            r.LoneCr++;
            breakEnds?.Add(units);
            carry.PrevEndsWithCr = false;
        }
        r.Utf16Units = units;
        return r;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> b, int i, bool bigEndian)
        => bigEndian ? (ushort)((b[i] << 8) | b[i + 1]) : (ushort)(b[i] | (b[i + 1] << 8));

    // ── Decoding ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a byte range that begins on a char boundary into chars. For UTF-8
    /// the range may end mid-sequence; the tail bytes of an incomplete sequence
    /// are NOT decoded and their count is returned so the caller advances.
    /// </summary>
    public int DecodeRange(ReadOnlySpan<byte> bytes, Span<char> chars, out int bytesConsumed)
    {
        switch (Class)
        {
            case CodecClass.SingleByte:
            {
                bytesConsumed = bytes.Length;
                return Encoding.GetChars(bytes, chars);
            }
            case CodecClass.Utf16LE:
            case CodecClass.Utf16BE:
            {
                int usable = bytes.Length & ~1;
                bytesConsumed = usable;
                return Encoding.GetChars(bytes[..usable], chars);
            }
            case CodecClass.Utf8:
            {
                // Trim an incomplete trailing sequence (self-synchronising: back up
                // over continuation bytes to the lead, check its expected length).
                int end = bytes.Length;
                int back = 0;
                while (back < 3 && end - 1 - back >= 0 && (bytes[end - 1 - back] & 0xC0) == 0x80) back++;
                if (end - 1 - back >= 0)
                {
                    byte lead = bytes[end - 1 - back];
                    int expected = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
                    if (expected > back + 1) end -= back + 1; // incomplete - defer
                }
                bytesConsumed = end;
                return Encoding.GetChars(bytes[..end], chars);
            }
            default:
                throw new InvalidOperationException("Unstreamable codec cannot DecodeRange.");
        }
    }

    /// <summary>
    /// For UTF-8: advances from a known char boundary to the byte position of a
    /// char that is `unitsToSkip` UTF-16 units further on. Returns byte advance.
    /// For fixed-width codecs this is pure arithmetic and never called.
    /// </summary>
    public int AdvanceUtf8Units(ReadOnlySpan<byte> bytes, long unitsToSkip, out long unitsSkipped)
    {
        int i = 0;
        long units = 0;
        while (i < bytes.Length && units < unitsToSkip)
        {
            byte b = bytes[i];
            if ((b & 0xC0) != 0x80)
            {
                long add = b >= 0xF0 ? 2 : 1;
                if (units + add > unitsToSkip) break; // stop at the char start; never split a pair
                units += add;
            }
            i++; // continuation bytes add no units and are skipped by the same walk
        }
        unitsSkipped = units;
        return i;
    }
}
