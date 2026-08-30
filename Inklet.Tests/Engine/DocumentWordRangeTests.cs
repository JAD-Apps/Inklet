using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Word and line ranges, the units behind double- and triple-click selection.
/// Before these existed the editor's pointer handler had no click-count logic at
/// all: every press just placed the caret, so double-clicking a word selected
/// nothing.
/// </summary>
[TestClass]
public sealed class DocumentWordRangeTests
{
    // "alpha beta gamma\r\nsecond line\r\n"
    //  0123456789...
    private static Document Sample() => Document.CreateUntitled("alpha beta gamma\r\nsecond line\r\n");

    private static string TextOf(Document d, (long Start, long End) r)
        => d.GetText(r.Start, r.End - r.Start);

    [TestMethod]
    public void WhenOffsetInsideWordThenWholeWordReturned()
    {
        using var doc = Sample();
        Assert.AreEqual("alpha", TextOf(doc, doc.WordRangeAt(0)));
        Assert.AreEqual("alpha", TextOf(doc, doc.WordRangeAt(2)));
        Assert.AreEqual("beta", TextOf(doc, doc.WordRangeAt(7)));
        Assert.AreEqual("gamma", TextOf(doc, doc.WordRangeAt(13)));
    }

    [TestMethod]
    public void WhenOffsetAtWordStartThenThatWordNotThePrevious()
    {
        using var doc = Sample();
        Assert.AreEqual("beta", TextOf(doc, doc.WordRangeAt(6)));   // 'b' of beta
    }

    [TestMethod]
    public void WhenOffsetOnSeparatorThenSeparatorRunReturned()
    {
        using var doc = Sample();
        Assert.AreEqual(" ", TextOf(doc, doc.WordRangeAt(5)));      // the space after alpha
    }

    [TestMethod]
    public void WhenRunOfSeparatorsThenTheWholeRun()
    {
        using var doc = Document.CreateUntitled("a   b");
        Assert.AreEqual("   ", TextOf(doc, doc.WordRangeAt(2)));
    }

    [TestMethod]
    public void WhenUnderscoreOrDigitsThenTreatedAsWordCharacters()
    {
        using var doc = Document.CreateUntitled("foo_bar42 next");
        Assert.AreEqual("foo_bar42", TextOf(doc, doc.WordRangeAt(4)));
    }

    [TestMethod]
    public void WhenOffsetAtLineEndThenLastWordNotTheNextLine()
    {
        using var doc = Sample();
        // offset 16 is the CR terminating line 1; the range must stay on line 1
        var r = doc.WordRangeAt(16);
        Assert.AreEqual("gamma", TextOf(doc, r));
    }

    [TestMethod]
    public void WhenWordRangeThenNeverSpansALineBreak()
    {
        using var doc = Sample();
        for (long i = 0; i <= doc.AddressableLength; i++)
        {
            var (s, e) = doc.WordRangeAt(i);
            string t = doc.GetText(s, e - s);
            Assert.DoesNotContain("\n", t, $"offset {i} crossed a line break");
            Assert.DoesNotContain("\r", t, $"offset {i} crossed a line break");
        }
    }

    [TestMethod]
    public void WhenEmptyLineThenWordRangeIsEmptyAtThatLine()
    {
        using var doc = Document.CreateUntitled("a\r\n\r\nb");
        var (s, e) = doc.WordRangeAt(3);   // the empty second line
        Assert.AreEqual(s, e);
    }

    [TestMethod]
    public void WhenLineRangeThenIncludesTerminator()
    {
        using var doc = Sample();
        Assert.AreEqual("alpha beta gamma\r\n", TextOf(doc, doc.LineRangeAt(4)));
        Assert.AreEqual("second line\r\n", TextOf(doc, doc.LineRangeAt(20)));
    }

    [TestMethod]
    public void WhenLastLineHasNoTerminatorThenRangeStopsAtEnd()
    {
        using var doc = Document.CreateUntitled("one\r\ntwo");
        Assert.AreEqual("two", TextOf(doc, doc.LineRangeAt(6)));
    }

    [TestMethod]
    public void WhenOffsetOutOfRangeThenClampedNotThrown()
    {
        using var doc = Sample();
        _ = doc.WordRangeAt(-5);
        _ = doc.WordRangeAt(long.MaxValue);
        _ = doc.LineRangeAt(-1);
        _ = doc.LineRangeAt(long.MaxValue);
    }

    [TestMethod]
    public void WhenSeparatorClassificationThenUnderscoreIsAWordCharacter()
    {
        Assert.IsFalse(Document.IsWordSeparator('_'));
        Assert.IsFalse(Document.IsWordSeparator('7'));
        Assert.IsFalse(Document.IsWordSeparator('q'));
        Assert.IsTrue(Document.IsWordSeparator(' '));
        Assert.IsTrue(Document.IsWordSeparator('-'));
        Assert.IsTrue(Document.IsWordSeparator('\t'));
    }

    [TestMethod]
    public void WhenCjkTextThenWordRangeStaysWithinTheLine()
    {
        using var doc = Document.CreateUntitled("日本語 テキスト\r\nnext");
        var (s, e) = doc.WordRangeAt(1);
        string t = doc.GetText(s, e - s);
        Assert.DoesNotContain("\r", t);
        Assert.IsGreaterThan(0, t.Length);
    }
}
