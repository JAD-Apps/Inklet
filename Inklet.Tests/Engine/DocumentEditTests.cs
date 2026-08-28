using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>Deterministic clock for coalescing-window tests.</summary>
internal sealed class FakeTimeSource : ITimeSource
{
    public DateTime UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public void Advance(TimeSpan by) => UtcNow += by;
}

[TestClass]
public sealed class DocumentEditTests
{
    private static string All(Document d) => d.GetText(0, d.Length);

    [TestMethod]
    public void WhenEmptyDocumentThenLengthZeroAndOneLine()
    {
        var d = Document.CreateUntitled();
        Assert.AreEqual(0, d.Length);
        Assert.AreEqual(1, d.LineCount);
        Assert.IsFalse(d.CanUndo);
        Assert.IsFalse(d.IsDirty);
    }

    [TestMethod]
    public void WhenInsertThenUndoThenContentRestored()
    {
        var d = Document.FromText("hello");
        d.Insert(5, " world");
        Assert.AreEqual("hello world", All(d));

        var caret = d.Undo();
        Assert.AreEqual("hello", All(d));
        Assert.AreEqual(5L, caret);
    }

    [TestMethod]
    public void WhenDeleteThenUndoThenContentRestored()
    {
        var d = Document.FromText("hello world");
        d.Delete(5, 6);
        Assert.AreEqual("hello", All(d));

        var caret = d.Undo();
        Assert.AreEqual("hello world", All(d));
        Assert.AreEqual(11L, caret);
    }

    [TestMethod]
    public void WhenUndoThenRedoThenContentReapplied()
    {
        var d = Document.FromText("a");
        d.Insert(1, "b");
        d.Undo();
        var caret = d.Redo();
        Assert.AreEqual("ab", All(d));
        Assert.AreEqual(2L, caret);
    }

    [TestMethod]
    public void WhenTypingWithinWindowThenCoalescesIntoOneUndoUnit()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("", clock);
        d.Insert(0, "a");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        d.Insert(1, "b");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        d.Insert(2, "c");

        d.Undo();
        Assert.AreEqual("", All(d));
        Assert.IsFalse(d.CanUndo);
    }

    [TestMethod]
    public void WhenTypingBeyondWindowThenSeparateUndoUnits()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("", clock);
        d.Insert(0, "a");
        clock.Advance(TimeSpan.FromSeconds(2));
        d.Insert(1, "b");

        d.Undo();
        Assert.AreEqual("a", All(d));
        Assert.IsTrue(d.CanUndo);
    }

    [TestMethod]
    public void WhenNonContiguousInsertsThenNotCoalesced()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("xy", clock);
        d.Insert(1, "a");   // x a y
        d.Insert(0, "b");   // b x a y - different offset, must not merge

        d.Undo();
        Assert.AreEqual("xay", All(d));
    }

    [TestMethod]
    public void WhenNewEditAfterUndoThenRedoCleared()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("", clock);
        d.Insert(0, "a");
        clock.Advance(TimeSpan.FromSeconds(2));
        d.Insert(1, "b");
        d.Undo();
        Assert.IsTrue(d.CanRedo);
        d.Insert(1, "c");
        Assert.IsFalse(d.CanRedo);
        Assert.AreEqual("ac", All(d));
    }

    [TestMethod]
    public void WhenSaveThenUndoBackToSavedThenClean()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("base", clock);
        d.Insert(4, "1");
        d.MarkSaved();
        Assert.IsFalse(d.IsDirty);

        clock.Advance(TimeSpan.FromSeconds(2));
        d.Insert(5, "2");
        Assert.IsTrue(d.IsDirty);

        d.Undo();
        Assert.IsFalse(d.IsDirty);   // back at the saved state

        d.Redo();
        Assert.IsTrue(d.IsDirty);
    }

    [TestMethod]
    public void WhenEditAfterUndoPastSaveThenPermanentlyDirty()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("", clock);
        d.Insert(0, "a");
        clock.Advance(TimeSpan.FromSeconds(2));
        d.Insert(1, "b");
        d.MarkSaved();

        d.Undo();                    // below saved state
        Assert.IsTrue(d.IsDirty);
        clock.Advance(TimeSpan.FromSeconds(2));
        d.Insert(1, "c");            // discards the redo branch holding the saved state
        Assert.IsTrue(d.IsDirty);

        d.Undo();
        Assert.IsTrue(d.IsDirty);    // saved state is unreachable forever
    }

    [TestMethod]
    public void WhenTypingAfterSaveThenDoesNotCoalesceIntoPreSaveUnit()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("", clock);
        d.Insert(0, "a");
        d.MarkSaved();
        d.Insert(1, "b");            // within window and contiguous, but sealed by save
        Assert.IsTrue(d.IsDirty);

        d.Undo();
        Assert.AreEqual("a", All(d));
        Assert.IsFalse(d.IsDirty);   // undo stops exactly at the saved state
    }

    [TestMethod]
    public void WhenReplaceThenSingleUndoUnitRestoresBoth()
    {
        var d = Document.FromText("hello world");
        d.Replace(0, 5, "goodbye");
        Assert.AreEqual("goodbye world", All(d));

        d.Undo();
        Assert.AreEqual("hello world", All(d));
        Assert.IsFalse(d.CanUndo);

        d.Redo();
        Assert.AreEqual("goodbye world", All(d));
    }

    [TestMethod]
    public void WhenInsertIntoCrLfDocumentThenLfConvertedToCrLf()
    {
        var d = Document.FromText("a\r\nb");
        Assert.AreEqual("\r\n", d.NewLineString);
        d.Insert(1, "\n");
        Assert.AreEqual("a\r\n\r\nb", All(d));
    }

    [TestMethod]
    public void WhenInsertIntoLfDocumentThenCrLfConvertedToLf()
    {
        var d = Document.FromText("a\nb");
        Assert.AreEqual("\n", d.NewLineString);
        d.Insert(3, "x\r\ny");
        Assert.AreEqual("a\nbx\ny", All(d));
    }

    [TestMethod]
    public void WhenInsertRawThenNoEolConversion()
    {
        var d = Document.FromText("a\nb");
        d.InsertRaw(3, "x\r\ny");
        Assert.AreEqual("a\nbx\r\ny", All(d));
    }

    [TestMethod]
    public void WhenDeleteAllThenUndoRestoresEverything()
    {
        const string text = "line1\r\nline2\r\nline3";
        var d = Document.FromText(text);
        d.Delete(0, d.Length);
        Assert.AreEqual(0, d.Length);
        Assert.AreEqual(1, d.LineCount);

        d.Undo();
        Assert.AreEqual(text, All(d));
        Assert.AreEqual(3, d.LineCount);
    }

    [TestMethod]
    public void WhenChangedEventThenReportsOffsetsAndLineDelta()
    {
        var d = Document.FromText("ab");
        TextChange last = default;
        d.Changed += c => last = c;

        d.Insert(1, "x\ny");   // LF converted to CRLF? source has no breaks -> CrLf default
        Assert.AreEqual(1, last.Offset);
        Assert.AreEqual(0, last.RemovedLength);
        Assert.AreEqual(4, last.AddedLength);   // "x\r\ny"
        Assert.AreEqual(1, last.LineDelta);

        d.Delete(1, 4);
        Assert.AreEqual(-1, last.LineDelta);
        Assert.AreEqual(4, last.RemovedLength);
    }

    [TestMethod]
    public void WhenManyInterleavedEditsThenContentConsistent()
    {
        var clock = new FakeTimeSource();
        var d = Document.FromText("0123456789", clock);
        for (int i = 0; i < 500; i++)
        {
            d.Insert(d.Length / 2, i.ToString()[..1]);
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        Assert.AreEqual(510, d.Length);
        for (int i = 0; i < 500; i++) d.Undo();
        Assert.AreEqual("0123456789", All(d));
    }
}
