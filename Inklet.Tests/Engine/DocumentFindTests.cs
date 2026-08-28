using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

[TestClass]
public sealed class DocumentFindTests
{
    private static FindQuery Q(string needle, long from = 0, bool backward = false, bool matchCase = false, bool wrap = true)
        => new() { Needle = needle, StartOffset = from, Backward = backward, MatchCase = matchCase, Wrap = wrap };

    [TestMethod]
    public void WhenForwardFindThenMatchesIndexOfSemantics()
    {
        using var d = Document.FromText("alpha beta gamma beta delta");
        var hit = d.FindNextAsync(Q("beta")).GetAwaiter().GetResult();
        Assert.AreEqual(6, hit!.Value.Offset);

        hit = d.FindNextAsync(Q("beta", from: 7)).GetAwaiter().GetResult();
        Assert.AreEqual(17, hit!.Value.Offset);
    }

    [TestMethod]
    public void WhenNoMatchAheadThenWrapsToStart()
    {
        using var d = Document.FromText("target early, nothing later");
        var hit = d.FindNextAsync(Q("target", from: 10)).GetAwaiter().GetResult();
        Assert.AreEqual(0, hit!.Value.Offset);

        var none = d.FindNextAsync(Q("absent", from: 0)).GetAwaiter().GetResult();
        Assert.IsNull(none);
    }

    [TestMethod]
    public void WhenBackwardFindThenLastIndexBeforeStart()
    {
        using var d = Document.FromText("one two one two one");
        var hit = d.FindNextAsync(Q("one", from: 10, backward: true)).GetAwaiter().GetResult();
        Assert.AreEqual(8, hit!.Value.Offset);

        // Wraps to the end when nothing before.
        hit = d.FindNextAsync(Q("two", from: 3, backward: true)).GetAwaiter().GetResult();
        Assert.AreEqual(12, hit!.Value.Offset);
    }

    [TestMethod]
    public void WhenCaseSensitivityTogglesThenRespected()
    {
        using var d = Document.FromText("Case CASE case");
        Assert.AreEqual(0, d.FindNextAsync(Q("case")).GetAwaiter().GetResult()!.Value.Offset);
        Assert.AreEqual(10, d.FindNextAsync(Q("case", matchCase: true)).GetAwaiter().GetResult()!.Value.Offset);
    }

    [TestMethod]
    public void WhenMatchStraddlesSearchWindowThenFound()
    {
        // Build a document just over one search window with the needle straddling
        // the 256K boundary.
        var sb = new StringBuilder(new string('x', 256 * 1024 - 3));
        sb.Append("NEEDLE");
        sb.Append(new string('y', 500));
        using var d = Document.FromText(sb.ToString());
        var hit = d.FindNextAsync(Q("NEEDLE")).GetAwaiter().GetResult();
        Assert.AreEqual(256 * 1024 - 3, hit!.Value.Offset);
    }

    [TestMethod]
    public void WhenFindOverEditedPiecesThenSeesLiveContent()
    {
        using var d = Document.FromText("start middle end");
        d.Insert(6, "INSERTED ");
        var hit = d.FindNextAsync(Q("INSERTED middle")).GetAwaiter().GetResult();
        Assert.AreEqual(6, hit!.Value.Offset);
    }

    [TestMethod]
    public void WhenReplaceAllThenSingleUndoUnitAndParityWithStringReplace()
    {
        const string original = "aaa bbb aaa ccc aaa\r\nmore aaa here";
        using var d = Document.FromText(original);
        var (offsets, rev) = d.CollectMatchesAsync("aaa", matchCase: false).GetAwaiter().GetResult();
        Assert.AreEqual(4, offsets.Count);
        Assert.IsTrue(d.TryReplaceAll(offsets, rev, 3, "ZZ"));

        Assert.AreEqual(original.Replace("aaa", "ZZ"), d.GetText(0, d.Length));

        d.Undo(); // one Ctrl+Z restores everything
        Assert.AreEqual(original, d.GetText(0, d.Length));
        d.Redo();
        Assert.AreEqual(original.Replace("aaa", "ZZ"), d.GetText(0, d.Length));
    }

    [TestMethod]
    public void WhenReplaceAllAfterConcurrentEditThenRefused()
    {
        using var d = Document.FromText("x y x y x");
        var (offsets, rev) = d.CollectMatchesAsync("x", matchCase: true).GetAwaiter().GetResult();
        d.Insert(0, "shift ");
        Assert.IsFalse(d.TryReplaceAll(offsets, rev, 1, "Q"), "stale offsets must be refused");
        Assert.AreEqual("shift x y x y x", d.GetText(0, d.Length));
    }

    [TestMethod]
    public void WhenReplaceAllWithEolInReplacementThenConvertedToDocumentEnding()
    {
        using var d = Document.FromText("a|b|c");   // no breaks -> CRLF default
        var (offsets, rev) = d.CollectMatchesAsync("|", matchCase: true).GetAwaiter().GetResult();
        Assert.IsTrue(d.TryReplaceAll(offsets, rev, 1, "\n"));
        Assert.AreEqual("a\r\nb\r\nc", d.GetText(0, d.Length));
        Assert.AreEqual(3, d.LineCount);
    }

    [TestMethod]
    public void WhenCancelledThenStopsPromptly()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200_000; i++) sb.Append("filler line without the token\n");
        using var d = Document.FromText(sb.ToString());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsExactly<TaskCanceledException>(() =>
            d.FindNextAsync(Q("absent-needle"), cts.Token).GetAwaiter().GetResult());
    }
}
