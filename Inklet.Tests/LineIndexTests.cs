using Inklet.Models;

namespace Inklet.Tests;

[TestClass]
public class LineIndexTests
{
    [TestMethod]
    public void WhenEmptyTextThenSingleLine()
    {
        var index = new LineIndex();
        index.Invalidate(string.Empty);

        Assert.AreEqual(1, index.LineCount);
        Assert.AreEqual((1, 1), index.GetLineColumn(0));
    }

    [TestMethod]
    public void WhenSingleLineThenLineCountIsOne()
    {
        var index = new LineIndex();
        index.Invalidate("hello");

        Assert.AreEqual(1, index.LineCount);
        Assert.AreEqual((1, 6), index.GetLineColumn(5));
    }

    [TestMethod]
    public void WhenMultipleLfLinesThenLineCountMatches()
    {
        var index = new LineIndex();
        index.Invalidate("a\nb\nc");

        Assert.AreEqual(3, index.LineCount);
        Assert.AreEqual((1, 1), index.GetLineColumn(0));
        Assert.AreEqual((2, 1), index.GetLineColumn(2));
        Assert.AreEqual((3, 1), index.GetLineColumn(4));
    }

    [TestMethod]
    public void WhenCrLfLineEndingsThenCountedAsSingleBreak()
    {
        var index = new LineIndex();
        index.Invalidate("a\r\nb\r\nc");

        Assert.AreEqual(3, index.LineCount);
        Assert.AreEqual((2, 1), index.GetLineColumn(3)); // start of "b"
    }

    [TestMethod]
    public void WhenBareCrLineEndingsThenCountedAsLineBreak()
    {
        var index = new LineIndex();
        index.Invalidate("a\rb\rc");

        Assert.AreEqual(3, index.LineCount);
        Assert.AreEqual((2, 1), index.GetLineColumn(2));
    }

    [TestMethod]
    public void WhenTrailingNewlineThenAdditionalEmptyLine()
    {
        var index = new LineIndex();
        index.Invalidate("a\n");

        Assert.AreEqual(2, index.LineCount);
        Assert.AreEqual((2, 1), index.GetLineColumn(2));
    }

    [TestMethod]
    public void WhenOffsetMidLineThenColumnIsOneBased()
    {
        var index = new LineIndex();
        index.Invalidate("hello\nworld");

        Assert.AreEqual((2, 4), index.GetLineColumn(9)); // 'l' in "world"
    }

    [TestMethod]
    public void WhenGetOffsetForLineThenReturnsLineStart()
    {
        var index = new LineIndex();
        index.Invalidate("abc\ndef\nghi");

        Assert.AreEqual(0, index.GetOffset(1));
        Assert.AreEqual(4, index.GetOffset(2));
        Assert.AreEqual(8, index.GetOffset(3));
    }

    [TestMethod]
    public void WhenGetOffsetOutOfRangeThenClamps()
    {
        var index = new LineIndex();
        index.Invalidate("a\nb");

        Assert.AreEqual(0, index.GetOffset(0));
        Assert.AreEqual(0, index.GetOffset(-5));
        Assert.AreEqual(2, index.GetOffset(99));
    }

    [TestMethod]
    public void WhenInvalidateAndReadThenRebuildsLazily()
    {
        var index = new LineIndex();
        index.Invalidate("a\nb");
        Assert.AreEqual(2, index.LineCount);

        index.Invalidate("a\nb\nc\nd");
        Assert.AreEqual(4, index.LineCount);
    }

    [TestMethod]
    public void WhenGetLineColumnOutOfRangeThenClamps()
    {
        var index = new LineIndex();
        index.Invalidate("hello");

        Assert.AreEqual((1, 1), index.GetLineColumn(-10));
        Assert.AreEqual((1, 6), index.GetLineColumn(999));
    }

    [TestMethod]
    public void WhenMixedLineEndingsThenAllRecognised()
    {
        var index = new LineIndex();
        index.Invalidate("a\nb\rc\r\nd");

        Assert.AreEqual(4, index.LineCount);
    }
}
