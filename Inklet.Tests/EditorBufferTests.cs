using Inklet.Editor;

namespace Inklet.Tests;

[TestClass]
public class EditorBufferTests
{
    [TestMethod]
    public void WhenEmptyConstructorThenLengthZeroAndOneLine()
    {
        var b = new EditorBuffer();
        Assert.AreEqual(0, b.Length);
        Assert.AreEqual(1, b.LineCount);
        Assert.AreEqual(string.Empty, b.GetText());
    }

    [TestMethod]
    public void WhenInsertedTextThenLineCountUpdates()
    {
        var b = new EditorBuffer();
        b.Insert(0, "line1\nline2\nline3");
        Assert.AreEqual(3, b.LineCount);
    }

    [TestMethod]
    public void WhenInsertThenUndoThenContentRestored()
    {
        var b = new EditorBuffer("hello");
        b.Insert(5, " world");
        Assert.AreEqual("hello world", b.GetText());

        var caret = b.Undo();
        Assert.AreEqual("hello", b.GetText());
        Assert.AreEqual(5, caret);
    }

    [TestMethod]
    public void WhenDeleteThenUndoThenContentRestored()
    {
        var b = new EditorBuffer("hello world");
        b.Delete(5, 6);
        Assert.AreEqual("hello", b.GetText());

        var caret = b.Undo();
        Assert.AreEqual("hello world", b.GetText());
        Assert.AreEqual(11, caret);
    }

    [TestMethod]
    public void WhenUndoThenRedoThenContentReapplied()
    {
        var b = new EditorBuffer("a");
        b.Insert(1, "b");
        b.Undo();
        var caret = b.Redo();
        Assert.AreEqual("ab", b.GetText());
        Assert.AreEqual(2, caret);
    }

    [TestMethod]
    public void WhenSequentialInsertsWithinWindowThenCoalesced()
    {
        // Typing "abc" character-by-character within the coalesce window should
        // produce a single undo entry. Ctrl+Z should remove all three.
        var b = new EditorBuffer();
        b.Insert(0, "a");
        b.Insert(1, "b");
        b.Insert(2, "c");

        Assert.AreEqual("abc", b.GetText());

        b.Undo();

        Assert.AreEqual(string.Empty, b.GetText());
    }

    [TestMethod]
    public void WhenDeleteBetweenInsertsThenCoalescingBreaks()
    {
        var b = new EditorBuffer();
        b.Insert(0, "a");
        b.Delete(0, 1);
        b.Insert(0, "b");

        // Three undo entries — delete broke the chain, so insert "b" is its own.
        b.Undo();
        Assert.AreEqual(string.Empty, b.GetText());
        b.Undo();
        Assert.AreEqual("a", b.GetText());
        b.Undo();
        Assert.AreEqual(string.Empty, b.GetText());
    }

    [TestMethod]
    public void WhenCanUndoCanRedoAfterEditsThenFlagsCorrect()
    {
        var b = new EditorBuffer();
        Assert.IsFalse(b.CanUndo);
        Assert.IsFalse(b.CanRedo);

        b.Insert(0, "x");
        Assert.IsTrue(b.CanUndo);
        Assert.IsFalse(b.CanRedo);

        b.Undo();
        Assert.IsFalse(b.CanUndo);
        Assert.IsTrue(b.CanRedo);

        b.Insert(0, "y");
        // New edit clears the redo stack.
        Assert.IsTrue(b.CanUndo);
        Assert.IsFalse(b.CanRedo);
    }

    [TestMethod]
    public void WhenGetLineColumnThenOneBased()
    {
        var b = new EditorBuffer("a\nbc\nd");
        Assert.AreEqual((1, 1), b.GetLineColumn(0));
        Assert.AreEqual((2, 1), b.GetLineColumn(2));
        Assert.AreEqual((3, 1), b.GetLineColumn(5));
    }

    [TestMethod]
    public void WhenGetTextSliceThenReturnsRequestedRange()
    {
        var b = new EditorBuffer("the quick brown fox");
        Assert.AreEqual("quick", b.GetText(4, 5));
    }

    [TestMethod]
    public void WhenLargeBurstOfInsertsThenStillReachesContent()
    {
        var b = new EditorBuffer();
        for (int i = 0; i < 1000; i++) b.Insert(b.Length, "x");

        Assert.AreEqual(1000, b.Length);
        Assert.AreEqual(new string('x', 1000), b.GetText());
    }
}
