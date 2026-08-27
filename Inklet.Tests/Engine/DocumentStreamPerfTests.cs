using System.Diagnostics;
using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Large-file streaming guards: instant first screen, flat memory, exact
/// geometry after background indexing. Uses a real 256 MB file on disk.
/// </summary>
[TestClass]
[TestCategory("Perf")]
public sealed class DocumentStreamPerfTests
{
    private static string s_bigFile = "";
    private const long TargetBytes = 256L * 1024 * 1024;
    private const string Line = "2026-08-27T00:00:00.000Z INFO worker-000 request handled path=/api/v1/items/000000 status=200\r\n";

    [ClassInitialize]
    public static void CreateCorpus(TestContext _)
    {
        s_bigFile = Path.Combine(Path.GetTempPath(), "inklet-perf-256mb.log");
        var block = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Line, 1024)));
        long reps = TargetBytes / block.Length;
        if (File.Exists(s_bigFile) && new FileInfo(s_bigFile).Length == reps * block.Length) return;
        using var fs = File.Create(s_bigFile);
        for (long i = 0; i < reps; i++) fs.Write(block);
    }

    [ClassCleanup]
    public static void DeleteCorpus() { try { File.Delete(s_bigFile); } catch { } }

    [TestMethod]
    public void WhenOpen256MbThenFirstScreenUnder200MsAndEditableImmediately()
    {
        var sw = Stopwatch.StartNew();
        using var doc = Document.OpenAsync(s_bigFile).GetAwaiter().GetResult();
        var line0 = doc.GetLine(0);
        sw.Stop();

        Console.WriteLine($"open-to-first-line: {sw.Elapsed.TotalMilliseconds:F1}ms");
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 200, $"open took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.IsTrue(line0.Text.Length > 0);
        Assert.IsTrue(doc.Length > 0, "estimated length available");

        // Immediately editable in the absorbed region, with instant undo.
        doc.Insert(0, "EDIT ");
        Assert.AreEqual("EDIT 2026", doc.GetText(0, 9));
        doc.Undo();
    }

    [TestMethod]
    public void WhenIndexing256MbThenCompletesAndGeometryExact()
    {
        using var doc = Document.OpenAsync(s_bigFile).GetAwaiter().GetResult();
        var done = new ManualResetEventSlim();
        if (doc.IsFullyIndexed) done.Set();
        doc.IndexCompleted += () => done.Set();

        var sw = Stopwatch.StartNew();
        Assert.IsTrue(doc.IsFullyIndexed || done.Wait(TimeSpan.FromSeconds(60)), "index completed");
        sw.Stop();
        doc.AbsorbIndexedSegments();

        long expectedLines = new FileInfo(s_bigFile).Length / Line.Length + 1;
        Console.WriteLine($"index 256MB: {sw.Elapsed.TotalSeconds:F1}s ({TargetBytes / Math.Max(1, sw.Elapsed.TotalSeconds) / 1e9:F2} GB/s)");
        Assert.IsTrue(doc.IsLineCountExact);
        Assert.AreEqual(expectedLines, doc.LineCount);

        // Exact random-access geometry at the far end of the file.
        long lastContentLine = doc.LineCount - 2;
        var slice = doc.GetLine(lastContentLine);
        Assert.AreEqual(Line.TrimEnd('\r', '\n'), slice.Text.ToString());
        Assert.AreEqual(2, slice.TerminatorLength);
    }

    [TestMethod]
    public void WhenFullyIndexed256MbThenWorkingSetStaysFlat()
    {
        long before = Environment.WorkingSet;
        using (var doc = Document.OpenAsync(s_bigFile).GetAwaiter().GetResult())
        {
            var done = new ManualResetEventSlim();
            if (doc.IsFullyIndexed) done.Set();
            doc.IndexCompleted += () => done.Set();
            Assert.IsTrue(doc.IsFullyIndexed || done.Wait(TimeSpan.FromSeconds(60)));
            doc.AbsorbIndexedSegments();

            // Touch a spread of lines to exercise the decode cache.
            var rng = new Random(3);
            for (int i = 0; i < 200; i++) _ = doc.GetLine(rng.NextInt64(doc.LineCount));

            long after = Environment.WorkingSet;
            double deltaMb = (after - before) / 1024.0 / 1024.0;
            Console.WriteLine($"working set delta after open+index+reads: {deltaMb:F0} MB");
            // Budget: index (tiny) + decode cache (<=64 MB) + mapped pages the OS
            // counts against us for touched regions. The old engine needed ~8x the
            // file (2+ GB here); anything close to that is the regression.
            Assert.IsTrue(deltaMb < 700, $"working set grew {deltaMb:F0} MB on a 256 MB file");
        }
    }
}
