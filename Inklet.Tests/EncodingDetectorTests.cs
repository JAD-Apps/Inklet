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
}
