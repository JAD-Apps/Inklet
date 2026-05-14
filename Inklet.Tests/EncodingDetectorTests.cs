using Inklet.Services;
using System.Text;

namespace Inklet.Tests;

[TestClass]
public class EncodingDetectorTests
{
    [TestInitialize]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [TestMethod]
    public void WhenEmptyByteArrayThenReturnsUtf8WithoutBom()
    {
        var (encoding, hasBom) = EncodingDetector.Detect([]);

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsFalse(hasBom);
    }

    [TestMethod]
    public void WhenUtf8BomPresentThenDetectsUtf8WithBom()
    {
        byte[] data = [0xEF, 0xBB, 0xBF, .. "Hello"u8.ToArray()];

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenUtf16LeBomPresentThenDetectsUtf16Le()
    {
        var content = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Hello")).ToArray();

        var (encoding, hasBom) = EncodingDetector.Detect(content);

        Assert.AreEqual(1200, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenUtf16BeBomPresentThenDetectsUtf16Be()
    {
        var content = Encoding.BigEndianUnicode.GetPreamble()
            .Concat(Encoding.BigEndianUnicode.GetBytes("Hello")).ToArray();

        var (encoding, hasBom) = EncodingDetector.Detect(content);

        Assert.AreEqual(1201, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenPureAsciiThenDetectsUtf8WithoutBom()
    {
        byte[] data = "Hello, World!\r\nThis is a test."u8.ToArray();

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsFalse(hasBom);
    }

    [TestMethod]
    public void WhenUtf8WithMultibyteCharsThenDetectsUtf8()
    {
        // "Héllo" in UTF-8 (é = 0xC3 0xA9)
        byte[] data = [0x48, 0xC3, 0xA9, 0x6C, 0x6C, 0x6F];

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsFalse(hasBom);
    }

    [TestMethod]
    public void WhenNullInputThenThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => EncodingDetector.Detect(null!));
    }

    [TestMethod]
    public void WhenUtf32LeBomPresentThenDetectsUtf32Le()
    {
        byte[] bom = [0xFF, 0xFE, 0x00, 0x00];
        var content = new UTF32Encoding(false, true).GetBytes("Hi");
        byte[] data = [.. bom, .. content];

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(12000, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenUtf32BeBomPresentThenDetectsUtf32Be()
    {
        byte[] bom = [0x00, 0x00, 0xFE, 0xFF];
        var content = new UTF32Encoding(true, true).GetBytes("Hi");
        byte[] data = [.. bom, .. content];

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(12001, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenOnlyBomBytesThenDetectsCorrectEncoding()
    {
        // Just the UTF-8 BOM, no content
        byte[] data = [0xEF, 0xBB, 0xBF];

        var (encoding, hasBom) = EncodingDetector.Detect(data);

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsTrue(hasBom);
    }

    [TestMethod]
    public void WhenLargeAsciiBufferThenDetectedAsUtf8WithoutFullScan()
    {
        // 1 MB of ASCII — should be detected as UTF-8 quickly via sample.
        var data = new byte[1024 * 1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)('a' + (i % 26));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (encoding, hasBom) = EncodingDetector.Detect(data);
        sw.Stop();

        Assert.AreEqual(65001, encoding.CodePage);
        Assert.IsFalse(hasBom);
        // Sanity: detection should be fast — sample is 64 KB, full scan would take much longer.
        Assert.IsTrue(sw.ElapsedMilliseconds < 200, $"Detection took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public void WhenIsValidUtf8WithLengthThenScansOnlyRequestedRange()
    {
        // Bytes 0–4 are valid UTF-8, byte 5 is invalid. With length=5 we should see "valid".
        byte[] data = [0x68, 0x65, 0x6C, 0x6C, 0x6F, 0xFF];

        Assert.IsTrue(EncodingDetector.IsValidUtf8(data, 5));
        Assert.IsFalse(EncodingDetector.IsValidUtf8(data, 6));
    }

    [TestMethod]
    public void WhenIsValidUtf8SampleEndsMidSequenceButFullBufferContinuesThenAccepts()
    {
        // 0xC3 starts a 2-byte UTF-8 sequence. Sample length 1 truncates mid-sequence,
        // but the full buffer continues with 0xA9 (a valid continuation byte) — so the
        // truncation is benign and we should report valid.
        byte[] data = [0xC3, 0xA9, 0x21]; // "é!"

        Assert.IsTrue(EncodingDetector.IsValidUtf8(data, 1));
    }

    // -----------------------------------------------------------------------
    // UTF-8 validator edge cases (often missed by hand-rolled validators)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenOverlongTwoByteSequenceThenRejected()
    {
        // 0xC0 0xAF would decode to '/' but is a forbidden overlong encoding.
        // The validator rejects 0xC0/0xC1 as lead bytes (only accepts >= 0xC2).
        byte[] data = [0xC0, 0xAF];

        Assert.IsFalse(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenContinuationByteWithoutLeaderThenRejected()
    {
        // A bare continuation byte (0x80-0xBF) without a preceding lead byte is invalid.
        byte[] data = [0x80];

        Assert.IsFalse(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenLeadByteFollowedByAsciiThenRejected()
    {
        // 0xC3 expects a continuation byte; following with 'A' (0x41) is invalid.
        byte[] data = [0xC3, 0x41];

        Assert.IsFalse(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenTwoByteSequenceAtBufferEndIsTruncatedThenRejected()
    {
        // Lead byte at the very end of the real buffer with no continuation.
        byte[] data = [0x68, 0xC3];

        Assert.IsFalse(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenAsciiOnlyFullBufferThenAccepted()
    {
        byte[] data = "Hello, World!"u8.ToArray();

        Assert.IsTrue(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenValidThreeByteSequenceThenAccepted()
    {
        // U+20AC (€) = 0xE2 0x82 0xAC
        byte[] data = [0xE2, 0x82, 0xAC];

        Assert.IsTrue(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenValidFourByteSequenceThenAccepted()
    {
        // U+1F600 (😀) = 0xF0 0x9F 0x98 0x80
        byte[] data = [0xF0, 0x9F, 0x98, 0x80];

        Assert.IsTrue(EncodingDetector.IsValidUtf8(data, data.Length));
    }

    [TestMethod]
    public void WhenAnsiFallbackThenCodePageIsResolvedNotZero()
    {
        // Bytes that are not valid UTF-8 and not detected with high enough confidence
        // by UTF.Unknown — forces the ANSI fallback. The resolved code page must be a
        // concrete value (e.g., 1252) so the session-persisted encoding survives a
        // locale change between launches.
        byte[] data = [0xFF, 0xFE]; // UTF-16 LE BOM-ish but only 2 bytes — actually triggers UTF-16 LE detection
        // Use a byte that explicitly fails UTF-8 validation:
        data = [0xC0, 0x40, 0xC1, 0x41]; // overlong / invalid UTF-8 leading bytes

        var (encoding, _) = EncodingDetector.Detect(data);

        Assert.AreNotEqual(0, encoding.CodePage,
            "Persisted code page must not be 0 (session restore would re-resolve to a possibly-different ANSI page)");
    }
}
