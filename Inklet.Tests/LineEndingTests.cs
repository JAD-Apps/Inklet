using Inklet.Models;

namespace Inklet.Tests;

[TestClass]
public class LineEndingTests
{
    [TestMethod]
    public void WhenEmptyStringThenDefaultsToCrLf()
    {
        var result = LineEndingDetector.Detect("");

        Assert.AreEqual(LineEndingStyle.CrLf, result);
    }

    [TestMethod]
    public void WhenNullStringThenDefaultsToCrLf()
    {
        var result = LineEndingDetector.Detect(null!);

        Assert.AreEqual(LineEndingStyle.CrLf, result);
    }

    [TestMethod]
    public void WhenOnlyCrLfThenDetectsCrLf()
    {
        var result = LineEndingDetector.Detect("Hello\r\nWorld\r\nTest");

        Assert.AreEqual(LineEndingStyle.CrLf, result);
    }

    [TestMethod]
    public void WhenOnlyLfThenDetectsLf()
    {
        var result = LineEndingDetector.Detect("Hello\nWorld\nTest");

        Assert.AreEqual(LineEndingStyle.Lf, result);
    }

    [TestMethod]
    public void WhenOnlyCrThenDetectsCr()
    {
        var result = LineEndingDetector.Detect("Hello\rWorld\rTest");

        Assert.AreEqual(LineEndingStyle.Cr, result);
    }

    [TestMethod]
    public void WhenMixedLineEndingsThenDetectsMixed()
    {
        var result = LineEndingDetector.Detect("Hello\r\nWorld\nTest");

        Assert.AreEqual(LineEndingStyle.Mixed, result);
    }

    [TestMethod]
    public void WhenNoLineEndingsThenDefaultsToCrLf()
    {
        var result = LineEndingDetector.Detect("Hello World");

        Assert.AreEqual(LineEndingStyle.CrLf, result);
    }

    [TestMethod]
    public void WhenSingleCrLfThenDetectsCrLf()
    {
        var result = LineEndingDetector.Detect("Hello\r\n");

        Assert.AreEqual(LineEndingStyle.CrLf, result);
    }

    [TestMethod]
    public void WhenGetDisplayNameCrLfThenReturnsWindowsCRLF()
    {
        var name = LineEndingDetector.GetDisplayName(LineEndingStyle.CrLf);

        Assert.AreEqual("Windows (CRLF)", name);
    }

    [TestMethod]
    public void WhenGetDisplayNameLfThenReturnsUnixLF()
    {
        var name = LineEndingDetector.GetDisplayName(LineEndingStyle.Lf);

        Assert.AreEqual("Unix (LF)", name);
    }

    [TestMethod]
    public void WhenGetDisplayNameCrThenReturnsMacintoshCR()
    {
        var name = LineEndingDetector.GetDisplayName(LineEndingStyle.Cr);

        Assert.AreEqual("Macintosh (CR)", name);
    }

    [TestMethod]
    public void WhenGetDisplayNameMixedThenReturnsMixed()
    {
        var name = LineEndingDetector.GetDisplayName(LineEndingStyle.Mixed);

        Assert.AreEqual("Mixed", name);
    }

    [TestMethod]
    public void WhenGetLineEndingStringCrLfThenReturnsCrLf()
    {
        var s = LineEndingDetector.GetLineEndingString(LineEndingStyle.CrLf);

        Assert.AreEqual("\r\n", s);
    }

    [TestMethod]
    public void WhenGetLineEndingStringLfThenReturnsLf()
    {
        var s = LineEndingDetector.GetLineEndingString(LineEndingStyle.Lf);

        Assert.AreEqual("\n", s);
    }

    [TestMethod]
    public void WhenGetLineEndingStringCrThenReturnsCr()
    {
        var s = LineEndingDetector.GetLineEndingString(LineEndingStyle.Cr);

        Assert.AreEqual("\r", s);
    }

    [TestMethod]
    public void WhenNormalizeToCrLfThenAllLineEndingsConverted()
    {
        var input = "Hello\nWorld\rTest\r\nEnd";

        var result = LineEndingDetector.Normalize(input, LineEndingStyle.CrLf);

        Assert.AreEqual("Hello\r\nWorld\r\nTest\r\nEnd", result);
    }

    [TestMethod]
    public void WhenNormalizeToLfThenAllLineEndingsConverted()
    {
        var input = "Hello\r\nWorld\rTest\nEnd";

        var result = LineEndingDetector.Normalize(input, LineEndingStyle.Lf);

        Assert.AreEqual("Hello\nWorld\nTest\nEnd", result);
    }

    [TestMethod]
    public void WhenNormalizeToCrThenAllLineEndingsConverted()
    {
        var input = "Hello\r\nWorld\nTest\rEnd";

        var result = LineEndingDetector.Normalize(input, LineEndingStyle.Cr);

        Assert.AreEqual("Hello\rWorld\rTest\rEnd", result);
    }

    [TestMethod]
    public void WhenNormalizeEmptyStringThenReturnsEmpty()
    {
        var result = LineEndingDetector.Normalize("", LineEndingStyle.Lf);

        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void WhenNormalizeNullStringThenReturnsEmpty()
    {
        // The method's signature returns a non-nullable string; null input must not
        // propagate as a null return (would NRE downstream callers like FileService.WriteFileAsync).
        var result = LineEndingDetector.Normalize(null!, LineEndingStyle.Lf);

        Assert.AreEqual(string.Empty, result);
    }

    [TestMethod]
    public void WhenNormalizeNoLineEndingsThenReturnsUnchanged()
    {
        var result = LineEndingDetector.Normalize("Hello", LineEndingStyle.Lf);

        Assert.AreEqual("Hello", result);
    }
}
