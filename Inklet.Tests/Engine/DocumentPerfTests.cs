using System.Diagnostics;
using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Performance guards for the engine's core promise: per-edit cost independent of
/// document size. Thresholds are deliberately loose (CI machines vary); the old
/// engine fails them by orders of magnitude, which is the regression being pinned.
/// </summary>
[TestClass]
[DoNotParallelize] // wall-clock assertions need a quiet process
[TestCategory("Perf")]
public sealed class DocumentPerfTests
{
    private static string BuildBigText(int approxChars)
    {
        var sb = new StringBuilder(approxChars + 128);
        int i = 0;
        while (sb.Length < approxChars)
            sb.Append("line ").Append(i++).Append(" with some typical content padding\r\n");
        return sb.ToString();
    }

    [TestMethod]
    public void WhenThousandRandomInsertsInto50MbDocThenP99UnderOneMs()
    {
        var doc = Document.FromText(BuildBigText(50_000_000));
        var rng = new Random(42);
        var latencies = new double[1000];

        // Warm up the JIT on the edit path.
        for (int i = 0; i < 50; i++) doc.Insert(rng.NextInt64(doc.Length + 1), "w");

        var sw = new Stopwatch();
        for (int i = 0; i < latencies.Length; i++)
        {
            long at = rng.NextInt64(doc.Length + 1);
            sw.Restart();
            doc.Insert(at, "x");
            sw.Stop();
            latencies[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(latencies);
        double p50 = latencies[latencies.Length / 2];
        double p99 = latencies[(int)(latencies.Length * 0.99)];
        Console.WriteLine($"insert p50={p50:F4}ms p99={p99:F4}ms max={latencies[^1]:F4}ms");
        Assert.IsTrue(p99 < 1.0, $"p99 insert latency {p99:F3} ms exceeds 1 ms");
    }

    [TestMethod]
    public void WhenViewportReadFromHeavilyEditedDocThenFast()
    {
        var doc = Document.FromText(BuildBigText(20_000_000));
        var rng = new Random(7);
        for (int i = 0; i < 5_000; i++)
            doc.Insert(rng.NextInt64(doc.Length + 1), "edit! ");

        var sw = Stopwatch.StartNew();
        long firstLine = doc.LineCount / 2;
        for (long l = firstLine; l < firstLine + 100; l++)
            _ = doc.GetLine(l);
        sw.Stop();
        Console.WriteLine($"100-line viewport read after 5k edits: {sw.Elapsed.TotalMilliseconds:F2}ms");
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 50, $"viewport read took {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    [TestMethod]
    public void WhenSelectAllDeleteOn50MbDocThenUndoKeepsNoTextCopy()
    {
        var doc = Document.FromText(BuildBigText(50_000_000));
        long before = GC.GetTotalMemory(forceFullCollection: true);

        var sw = Stopwatch.StartNew();
        doc.Delete(0, doc.Length);   // select-all + delete
        sw.Stop();

        long after = GC.GetTotalMemory(forceFullCollection: true);
        Console.WriteLine($"whole-doc delete: {sw.Elapsed.TotalMilliseconds:F2}ms, heap delta {(after - before) / 1024.0:F0} KB");
        // The old engine pushed a 50 MB string onto the undo stack here; the new one
        // stores piece references only.
        Assert.IsTrue(after - before < 5_000_000, $"delete retained {(after - before) / 1e6:F1} MB");
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 50, $"whole-doc delete took {sw.Elapsed.TotalMilliseconds:F1} ms");

        doc.Undo();
        Assert.AreEqual(50, doc.GetText(0, 50).Length);
    }
}
