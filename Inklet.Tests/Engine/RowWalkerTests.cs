using Inklet.Editor;

namespace Inklet.Tests.Engine;

/// <summary>Scripted oracle: rows per line supplied directly.</summary>
internal sealed class FakeWrapOracle(int[] rowsPerLine) : ILineWrapOracle
{
    public long LineCount => rowsPerLine.Length;
    public int RowsOfLine(long line) => rowsPerLine[line];
}

[TestClass]
public sealed class RowWalkerTests
{
    // Lines with 1, 3, 1, 2, 5, 1 display rows -> 13 rows total.
    private static readonly FakeWrapOracle Oracle = new([1, 3, 1, 2, 5, 1]);

    [TestMethod]
    public void WhenWalkForwardWithinLineThenSubRowAdvances()
    {
        var a = RowWalker.Walk(new ViewportAnchor(1, 0, 0), 2, Oracle);
        Assert.AreEqual(new ViewportAnchor(1, 2, 0), a);
    }

    [TestMethod]
    public void WhenWalkForwardAcrossLinesThenSpills()
    {
        var a = RowWalker.Walk(ViewportAnchor.Origin, 5, Oracle);
        // rows: L0r0=0, L1r0=1, L1r1=2, L1r2=3, L2r0=4, L3r0=5
        Assert.AreEqual(new ViewportAnchor(3, 0, 0), a);
    }

    [TestMethod]
    public void WhenWalkBackwardAcrossLinesThenSpills()
    {
        var a = RowWalker.Walk(new ViewportAnchor(3, 1, 0), -3, Oracle);
        // back: L3r1 -> L3r0 -> L2r0 -> L1r2
        Assert.AreEqual(new ViewportAnchor(1, 2, 0), a);
    }

    [TestMethod]
    public void WhenWalkPastEdgesThenClamped()
    {
        Assert.AreEqual(new ViewportAnchor(5, 0, 0), RowWalker.Walk(ViewportAnchor.Origin, 999, Oracle));
        Assert.AreEqual(ViewportAnchor.Origin, RowWalker.Walk(new ViewportAnchor(5, 0, 0), -999, Oracle));
    }

    [TestMethod]
    public void WhenRoundTripWalksThenInverse()
    {
        var start = new ViewportAnchor(2, 0, 0);
        for (int delta = -8; delta <= 8; delta++)
        {
            var there = RowWalker.Walk(start, delta, Oracle);
            var dist = RowWalker.TryDistance(start, there, 100, Oracle);
            // Clamping at edges can shorten the walk; the measured distance must
            // match the effective (clamped) delta.
            var back = RowWalker.Walk(there, -dist!.Value, Oracle);
            Assert.AreEqual(start, back, $"delta {delta}");
        }
    }

    [TestMethod]
    public void WhenDistanceExceedsLimitThenNull()
    {
        Assert.IsNull(RowWalker.TryDistance(ViewportAnchor.Origin, new ViewportAnchor(4, 4, 0), 3, Oracle));
        Assert.AreEqual(12, RowWalker.TryDistance(ViewportAnchor.Origin, new ViewportAnchor(5, 0, 0), 50, Oracle));
        Assert.AreEqual(-12, RowWalker.TryDistance(new ViewportAnchor(5, 0, 0), ViewportAnchor.Origin, 50, Oracle));
    }

    [TestMethod]
    public void WhenBottomAnchorThenLastViewportFills()
    {
        // 13 total rows, viewport of 4 -> top of the last full viewport is row 9
        // = L4r2 (rows: L0=1, L1=3, L2=1, L3=2 -> 7 before L4; row 9 = L4 sub 2).
        Assert.AreEqual(new ViewportAnchor(4, 2, 0), RowWalker.BottomAnchor(4, Oracle));
        // A viewport taller than the content clamps to the origin.
        Assert.AreEqual(ViewportAnchor.Origin, RowWalker.BottomAnchor(99, Oracle));
    }

    [TestMethod]
    public void WhenClampToBottomThenOnlyOverscrollMoves()
    {
        var legal = new ViewportAnchor(2, 0, 0);
        Assert.AreEqual(legal, RowWalker.ClampToBottom(legal, 4, Oracle));
        var over = new ViewportAnchor(5, 0, 0);
        Assert.AreEqual(new ViewportAnchor(4, 2, 0), RowWalker.ClampToBottom(over, 4, Oracle));
    }

    [TestMethod]
    public void WhenAnchorSubRowStaleAfterRewrapThenWalkNormalises()
    {
        // Oracle where line 1 now has fewer rows than a stale anchor believes.
        var shrunk = new FakeWrapOracle([1, 2, 1]);
        var a = RowWalker.Walk(new ViewportAnchor(1, 5, 0), 0, shrunk);
        Assert.AreEqual(new ViewportAnchor(1, 1, 0), a);
    }
}
