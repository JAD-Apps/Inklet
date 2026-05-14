using Inklet.Services;

namespace Inklet.Tests;

[TestClass]
public class PrintServiceTests
{
    // -----------------------------------------------------------------------
    // ExpandTokens — Notepad-compatible header/footer template tokens
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenTemplateIsLiteralStringThenReturnedUnchanged()
    {
        var result = PrintService.ExpandTokens("Just a heading", "doc.txt", 1, 1);

        Assert.AreEqual("Just a heading", result);
    }

    [TestMethod]
    public void WhenTemplateContainsAmpersandFThenFileNameSubstituted()
    {
        var result = PrintService.ExpandTokens("File: &f", "/path/to/Notes.md", 1, 1);

        Assert.AreEqual("File: Notes.md", result);
    }

    [TestMethod]
    public void WhenTemplateContainsAmpersandPThenPageNumberSubstituted()
    {
        var result = PrintService.ExpandTokens("Page &p of &P", "doc.txt", 3, 7);

        Assert.AreEqual("Page 3 of 7", result);
    }

    [TestMethod]
    public void WhenTemplateContainsDoubleAmpersandThenLiteralAmpersandEmitted()
    {
        var result = PrintService.ExpandTokens("Hi && bye", "doc.txt", 1, 1);

        Assert.AreEqual("Hi & bye", result);
    }

    [TestMethod]
    public void WhenTemplateContainsUnknownTokenThenLeftLiteral()
    {
        // &z is not a recognised token — the original implementation leaves '&'
        // and emits the next char literally too. We assert that behaviour.
        var result = PrintService.ExpandTokens("&z", "doc.txt", 1, 1);

        Assert.AreEqual("&z", result);
    }

    [TestMethod]
    public void WhenTemplateEndsWithLoneAmpersandThenEmittedLiterally()
    {
        var result = PrintService.ExpandTokens("Stop&", "doc.txt", 1, 1);

        Assert.AreEqual("Stop&", result);
    }

    [TestMethod]
    public void WhenTemplateContainsCaseInsensitiveFileTokenThenSubstituted()
    {
        // The implementation accepts both &f and &F.
        var result = PrintService.ExpandTokens("&F", "doc.txt", 1, 1);

        Assert.AreEqual("doc.txt", result);
    }

    [TestMethod]
    public void WhenFileNameEmptyThenSubstitutesUntitled()
    {
        var result = PrintService.ExpandTokens("&f", "", 1, 1);

        Assert.AreEqual("Untitled", result);
    }

    [TestMethod]
    public void WhenAllTokensCombinedThenAllSubstituted()
    {
        var result = PrintService.ExpandTokens("&f|&p|&P", "log.txt", 5, 12);

        Assert.AreEqual("log.txt|5|12", result);
    }

    [TestMethod]
    public void WhenTokensInTabbedTemplateThenPositionalLayoutPreserved()
    {
        // Notepad's three-zone layout uses tabs as separators — token expansion
        // must preserve them so DrawHfLine can split on '\t' afterwards.
        var result = PrintService.ExpandTokens("&f\t\t&p", "x.txt", 2, 3);

        Assert.AreEqual("x.txt\t\t2", result);
    }
}
