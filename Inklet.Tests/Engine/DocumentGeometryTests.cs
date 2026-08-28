using Inklet.Engine;

namespace Inklet.Tests.Engine;

[TestClass]
public sealed class DocumentGeometryTests
{
    [TestMethod]
    public void WhenCrLfContentThenLineCountAndOffsetsNative()
    {
        var d = Document.FromText("ab\r\ncd\r\nef");
        Assert.AreEqual(3, d.LineCount);
        Assert.AreEqual(0, d.GetOffsetForLine(0));
        Assert.AreEqual(4, d.GetOffsetForLine(1));   // after CR LF
        Assert.AreEqual(8, d.GetOffsetForLine(2));
    }

    [TestMethod]
    public void WhenMixedEndingsThenEachBreakCountedOnce()
    {
        var d = Document.FromText("a\r\nb\nc\rd");
        Assert.AreEqual(4, d.LineCount);
        Assert.AreEqual(3, d.GetOffsetForLine(1));
        Assert.AreEqual(5, d.GetOffsetForLine(2));
        Assert.AreEqual(7, d.GetOffsetForLine(3));
    }

    [TestMethod]
    public void WhenGetLineThenTerminatorExcludedAndLengthReported()
    {
        var d = Document.FromText("ab\r\ncd\ne\rlast");
        var l0 = d.GetLine(0);
        Assert.AreEqual("ab", l0.Text.ToString());
        Assert.AreEqual(2, l0.TerminatorLength);
        Assert.AreEqual(0, l0.CharOffset);

        var l1 = d.GetLine(1);
        Assert.AreEqual("cd", l1.Text.ToString());
        Assert.AreEqual(1, l1.TerminatorLength);

        var l2 = d.GetLine(2);
        Assert.AreEqual("e", l2.Text.ToString());
        Assert.AreEqual(1, l2.TerminatorLength);

        var l3 = d.GetLine(3);
        Assert.AreEqual("last", l3.Text.ToString());
        Assert.AreEqual(0, l3.TerminatorLength);
    }

    [TestMethod]
    public void WhenGetLineColumnThenNativeUnits()
    {
        var d = Document.FromText("ab\r\ncd");
        Assert.AreEqual((0L, 0L), d.GetLineColumn(0));
        Assert.AreEqual((0L, 2L), d.GetLineColumn(2));   // on the CR
        Assert.AreEqual((1L, 0L), d.GetLineColumn(4));
        Assert.AreEqual((1L, 2L), d.GetLineColumn(6));
    }

    [TestMethod]
    public void WhenEditSplitsCrLfThenLineCountStillCorrect()
    {
        // Inserting between CR and LF is what a caret can never do, but a Replace
        // range or a raw edit can produce pieces that slice the pair; the tree's
        // seam arithmetic must keep counting one break for an adjacent CR+LF and
        // two breaks when text separates them.
        var d = Document.FromText("a\r\nb");
        Assert.AreEqual(2, d.LineCount);

        d.InsertRaw(2, "X");        // a\r X \nb  -> CR and LF now separate breaks
        Assert.AreEqual("a\rX\nb", d.GetText(0, d.Length));
        Assert.AreEqual(3, d.LineCount);
        Assert.AreEqual(2, d.GetOffsetForLine(1));
        Assert.AreEqual(4, d.GetOffsetForLine(2));

        d.Undo();                    // pieces rejoin; CR+LF must merge back to one break
        Assert.AreEqual("a\r\nb", d.GetText(0, d.Length));
        Assert.AreEqual(2, d.LineCount);
        Assert.AreEqual(3, d.GetOffsetForLine(1));
    }

    [TestMethod]
    public void WhenDeleteCreatesCrLfSeamThenBreaksMerge()
    {
        // "a\rX\nb" - deleting X makes the CR and LF adjacent: 3 lines -> 2.
        var d = Document.FromText("a\rX\nb");
        Assert.AreEqual(3, d.LineCount);
        d.Delete(2, 1);
        Assert.AreEqual("a\r\nb", d.GetText(0, d.Length));
        Assert.AreEqual(2, d.LineCount);
        Assert.AreEqual(3, d.GetOffsetForLine(1));
    }

    [TestMethod]
    public void WhenSnapCaretInsideCrLfThenMoves()
    {
        var d = Document.FromText("a\r\nb");
        Assert.AreEqual(1, d.SnapCaret(2, SnapDirection.Left));
        Assert.AreEqual(3, d.SnapCaret(2, SnapDirection.Right));
        Assert.AreEqual(1, d.SnapCaret(1, SnapDirection.Left));   // not mid-pair
        Assert.AreEqual(3, d.SnapCaret(3, SnapDirection.Right));
    }

    [TestMethod]
    public void WhenSnapCaretInsideSurrogatePairThenMoves()
    {
        var d = Document.FromText("a\U0001F600b"); // emoji = surrogate pair at [1,2]
        Assert.AreEqual(1, d.SnapCaret(2, SnapDirection.Left));
        Assert.AreEqual(3, d.SnapCaret(2, SnapDirection.Right));
    }

    [TestMethod]
    public void WhenTrailingBreakThenLastLineEmpty()
    {
        var d = Document.FromText("ab\n");
        Assert.AreEqual(2, d.LineCount);
        var last = d.GetLine(1);
        Assert.AreEqual("", last.Text.ToString());
        Assert.AreEqual(0, last.TerminatorLength);
        Assert.AreEqual(3, last.CharOffset);
    }

    [TestMethod]
    public void WhenCrOnlyDocumentThenBreaksCounted()
    {
        var d = Document.FromText("a\rb\rc");
        Assert.AreEqual(3, d.LineCount);
        Assert.AreEqual("\r", d.NewLineString);
        Assert.AreEqual(2, d.GetOffsetForLine(1));
        Assert.AreEqual(4, d.GetOffsetForLine(2));
    }

    [TestMethod]
    public void WhenManyEditsThenGeometryMatchesLineIndexSemantics()
    {
        var d = Document.FromText("l1\r\nl2\r\nl3");
        d.Insert(0, "start ");
        d.Insert(d.Length, " end");
        Assert.AreEqual(3, d.LineCount);
        var (line, col) = d.GetLineColumn(d.Length);
        Assert.AreEqual(2, line);
        Assert.AreEqual("l3 end".Length, col);
    }
}
