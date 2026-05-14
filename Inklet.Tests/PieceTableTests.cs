using Inklet.Editor;

namespace Inklet.Tests;

[TestClass]
public class PieceTableTests
{
    // -----------------------------------------------------------------------
    // Construction
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenEmptyConstructorThenLengthZero()
    {
        var pt = new PieceTable();
        Assert.AreEqual(0, pt.Length);
        Assert.AreEqual(string.Empty, pt.GetText());
    }

    [TestMethod]
    public void WhenConstructedWithOriginalThenLengthMatches()
    {
        var pt = new PieceTable("hello world");
        Assert.AreEqual(11, pt.Length);
        Assert.AreEqual("hello world", pt.GetText());
    }

    // -----------------------------------------------------------------------
    // Insert
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenInsertAtStartThenContentPrepended()
    {
        var pt = new PieceTable("world");
        pt.Insert(0, "hello ");
        Assert.AreEqual("hello world", pt.GetText());
    }

    [TestMethod]
    public void WhenInsertAtEndThenContentAppended()
    {
        var pt = new PieceTable("hello");
        pt.Insert(5, " world");
        Assert.AreEqual("hello world", pt.GetText());
    }

    [TestMethod]
    public void WhenInsertInMiddleThenContentSpliced()
    {
        var pt = new PieceTable("hello world");
        pt.Insert(6, "beautiful ");
        Assert.AreEqual("hello beautiful world", pt.GetText());
    }

    [TestMethod]
    public void WhenInsertEmptyStringThenNoChange()
    {
        var pt = new PieceTable("hello");
        pt.Insert(2, "");
        Assert.AreEqual("hello", pt.GetText());
    }

    [TestMethod]
    public void WhenSequentialAppendsThenCoalescedIntoSinglePiece()
    {
        var pt = new PieceTable();
        for (int i = 0; i < 100; i++) pt.Insert(pt.Length, "a");
        Assert.AreEqual(100, pt.Length);
        Assert.AreEqual(new string('a', 100), pt.GetText());
        // Coalescing keeps the piece count low under sustained typing.
        Assert.AreEqual(1, pt.PieceCount);
    }

    [TestMethod]
    public void WhenInsertOutOfRangeThenClampedToBounds()
    {
        var pt = new PieceTable("abc");
        pt.Insert(-5, "<");
        pt.Insert(999, ">");
        Assert.AreEqual("<abc>", pt.GetText());
    }

    // -----------------------------------------------------------------------
    // Delete
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenDeleteFromStartThenPrefixRemoved()
    {
        var pt = new PieceTable("hello world");
        pt.Delete(0, 6);
        Assert.AreEqual("world", pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteFromEndThenSuffixRemoved()
    {
        var pt = new PieceTable("hello world");
        pt.Delete(5, 6);
        Assert.AreEqual("hello", pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteInMiddleThenPiecesSplit()
    {
        var pt = new PieceTable("hello beautiful world");
        pt.Delete(5, 10);
        Assert.AreEqual("hello world", pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteEntireDocumentThenLengthZero()
    {
        var pt = new PieceTable("hello");
        pt.Delete(0, 5);
        Assert.AreEqual(0, pt.Length);
        Assert.AreEqual(string.Empty, pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteSpansMultiplePiecesThenAllAffectedPiecesUpdated()
    {
        var pt = new PieceTable("ABCDE");
        pt.Insert(2, "XY"); // ABXYCDE
        pt.Insert(5, "Z");  // ABXYCZDE
        Assert.AreEqual("ABXYCZDE", pt.GetText());

        pt.Delete(1, 5); // remove "BXYCZ"
        Assert.AreEqual("ADE", pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteOutOfRangeThenClamped()
    {
        var pt = new PieceTable("abc");
        pt.Delete(-1, 2);
        Assert.AreEqual("c", pt.GetText());

        pt.Delete(0, 999);
        Assert.AreEqual(string.Empty, pt.GetText());
    }

    [TestMethod]
    public void WhenDeleteZeroLengthThenNoChange()
    {
        var pt = new PieceTable("abc");
        pt.Delete(1, 0);
        Assert.AreEqual("abc", pt.GetText());
    }

    // -----------------------------------------------------------------------
    // GetText slice
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenGetTextSliceThenReturnsRequestedRange()
    {
        var pt = new PieceTable("hello world");
        Assert.AreEqual("world", pt.GetText(6, 5));
        Assert.AreEqual("hello", pt.GetText(0, 5));
    }

    [TestMethod]
    public void WhenGetTextSliceSpansEditsThenCorrect()
    {
        var pt = new PieceTable("hello world");
        pt.Insert(5, " brave new");
        // Document is now: "hello brave new world"
        Assert.AreEqual("brave new", pt.GetText(6, 9));
    }

    [TestMethod]
    public void WhenGetTextSliceOutOfRangeThenClamped()
    {
        var pt = new PieceTable("abc");
        Assert.AreEqual("abc", pt.GetText(-5, 100));
        Assert.AreEqual(string.Empty, pt.GetText(2, 0));
    }

    // -----------------------------------------------------------------------
    // CharAt
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenCharAtValidOffsetThenReturnsCharacter()
    {
        var pt = new PieceTable("hello");
        Assert.AreEqual('h', pt.CharAt(0));
        Assert.AreEqual('o', pt.CharAt(4));
    }

    [TestMethod]
    public void WhenCharAtSpansPiecesThenReturnsCorrectChar()
    {
        var pt = new PieceTable("hello world");
        pt.Insert(5, "_");
        // Document: "hello_ world"
        Assert.AreEqual('_', pt.CharAt(5));
        Assert.AreEqual(' ', pt.CharAt(6));
    }

    [TestMethod]
    public void WhenCharAtOutOfRangeThenReturnsNul()
    {
        var pt = new PieceTable("abc");
        Assert.AreEqual('\0', pt.CharAt(-1));
        Assert.AreEqual('\0', pt.CharAt(3));
        Assert.AreEqual('\0', pt.CharAt(99));
    }

    // -----------------------------------------------------------------------
    // Stress / round-trip — typing a paragraph then deleting it
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenTypeParagraphCharByCharThenContentMatches()
    {
        var pt = new PieceTable();
        const string para = "The quick brown fox jumps over the lazy dog.";
        for (int i = 0; i < para.Length; i++)
            pt.Insert(i, para[i].ToString());

        Assert.AreEqual(para, pt.GetText());
        Assert.AreEqual(1, pt.PieceCount); // sequential typing coalesces
    }

    [TestMethod]
    public void WhenInterleavedInsertsAndDeletesThenContentConsistent()
    {
        var pt = new PieceTable("0123456789");
        pt.Insert(5, "ABC"); // "01234ABC56789"
        pt.Delete(2, 2);     // remove "23" → "014ABC56789"
        Assert.AreEqual("014ABC56789", pt.GetText());
        pt.Insert(0, ">"); // ">014ABC56789"
        Assert.AreEqual(">014ABC56789", pt.GetText());
        pt.Delete(pt.Length - 1, 1);
        Assert.AreEqual(">014ABC5678", pt.GetText());
    }
}
