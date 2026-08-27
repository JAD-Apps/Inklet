using System.Diagnostics;
using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Opt-in guards at genuinely huge sizes (8 GB - larger than many machines'
/// RAM). Excluded from default runs; execute with:
///   dotnet test --filter "TestCategory=HugeFiles"
/// Needs ~16 GB free disk in %TEMP% (file + one save) and a few minutes.
/// </summary>
[TestClass]
[DoNotParallelize]
[TestCategory("HugeFiles")]
public sealed class HugeFilesTests
{
    private static string s_file = "";
    private const long TargetBytes = 8L * 1024 * 1024 * 1024;
    private const string Line = "2026-08-27T00:00:00.000Z INFO worker-000 huge-corpus row 0000000000 status=200\r\n";

    [ClassInitialize]
    public static void CreateCorpus(TestContext _)
    {
        // Opt-in twice over: the category filter AND this env var, so a plain
        // `dotnet test` never spends minutes and 16 GB of disk by surprise.
        if (Environment.GetEnvironmentVariable("INKLET_HUGE_TESTS") != "1")
            Assert.Inconclusive("Set INKLET_HUGE_TESTS=1 to run the 8 GB suite.");
        s_file = Path.Combine(Path.GetTempPath(), "inklet-huge-8gb.log");
        var block = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(Line, 1024)));
        long reps = TargetBytes / block.Length;
        if (File.Exists(s_file) && new FileInfo(s_file).Length == reps * block.Length) return;
        using var fs = File.Create(s_file);
        for (long i = 0; i < reps; i++) fs.Write(block);
    }

    [ClassCleanup]
    public static void DeleteCorpus() { try { File.Delete(s_file); } catch { } }

    private static void WaitForIndex(Document doc, TimeSpan timeout)
    {
        var done = new ManualResetEventSlim();
        if (doc.IsFullyIndexed) done.Set();
        doc.IndexCompleted += () => done.Set();
        Assert.IsTrue(doc.IsFullyIndexed || done.Wait(timeout), "indexing finished in time");
        doc.AbsorbIndexedSegments();
    }

    [TestMethod]
    public void WhenOpen8GbThenInstantViewFlatMemoryAndExactGeometry()
    {
        long gcBefore = GC.GetTotalMemory(forceFullCollection: true);

        var sw = Stopwatch.StartNew();
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();
        var line0 = doc.GetLine(0);
        sw.Stop();
        Console.WriteLine($"open-to-first-line: {sw.Elapsed.TotalMilliseconds:F0}ms");
        Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 500, $"open took {sw.Elapsed.TotalMilliseconds:F0} ms");
        Assert.IsTrue(line0.Text.Length > 0);

        sw.Restart();
        WaitForIndex(doc, TimeSpan.FromMinutes(5));
        sw.Stop();
        Console.WriteLine($"index 8 GB: {sw.Elapsed.TotalSeconds:F0}s ({TargetBytes / Math.Max(1, sw.Elapsed.TotalSeconds) / 1e9:F2} GB/s)");

        long expectedLines = new FileInfo(s_file).Length / Line.Length + 1;
        Assert.IsTrue(doc.IsLineCountExact);
        Assert.AreEqual(expectedLines, doc.LineCount);

        // Random access at the far end and a spread of positions.
        var rng = new Random(19);
        for (int i = 0; i < 100; i++)
        {
            long line = rng.NextInt64(doc.LineCount - 1);
            var slice = doc.GetLine(line);
            Assert.AreEqual(Line.TrimEnd('\r', '\n').Length, slice.Text.Length, $"line {line}");
        }

        long gcAfter = GC.GetTotalMemory(forceFullCollection: true);
        double heapMb = (gcAfter - gcBefore) / 1024.0 / 1024.0;
        Console.WriteLine($"managed heap delta: {heapMb:F0} MB");
        Assert.IsTrue(heapMb < 200, $"heap grew {heapMb:F0} MB on an 8 GB file");
    }

    [TestMethod]
    public void WhenEditAndSave8GbThenByteIdenticalOutsideEditAndUndoIntact()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();
        WaitForIndex(doc, TimeSpan.FromMinutes(5));

        long farOffset = doc.GetOffsetForLine(doc.LineCount - 1_000);
        doc.Insert(farOffset, "HUGE-EDIT");
        Assert.IsTrue(doc.IsDirty);

        string target = s_file + ".saved";
        try
        {
            var sw = Stopwatch.StartNew();
            doc.SaveAsync(new SaveOptions { TargetPath = target }).GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"save 8 GB: {sw.Elapsed.TotalSeconds:F1}s");
            Assert.IsFalse(doc.IsDirty);

            var fi = new FileInfo(target);
            Assert.AreEqual(new FileInfo(s_file).Length + 9, fi.Length, "exactly the 9-byte edit added");

            // Spot-check byte identity at head, far tail, and around the edit.
            using var a = File.OpenRead(s_file);
            using var b = File.OpenRead(target);
            var bufA = new byte[64 * 1024];
            var bufB = new byte[64 * 1024];
            void Compare(long offA, long offB, string ctx)
            {
                a.Position = offA; b.Position = offB;
                a.ReadExactly(bufA); b.ReadExactly(bufB);
                CollectionAssert.AreEqual(bufA, bufB, ctx);
            }
            Compare(0, 0, "head");
            Compare(farOffset - 100_000, farOffset - 100_000, "before edit");
            Compare(farOffset, farOffset + 9, "after edit (shifted by insert)");
            Compare(a.Length - bufA.Length, b.Length - bufB.Length, "tail");

            // Undo survives the save.
            Assert.IsTrue(doc.CanUndo);
            doc.Undo();
            Assert.IsTrue(doc.IsDirty);
            Assert.AreEqual(new FileInfo(s_file).Length, doc.Length);
        }
        finally { File.Delete(target); }
    }

    [TestMethod]
    public void WhenSessionCaptured8GbWithEditsThenTinyAndRestores()
    {
        string json;
        long expectedLength;
        using (var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult())
        {
            WaitForIndex(doc, TimeSpan.FromMinutes(5));
            doc.Insert(1000, "SESSION-A");
            doc.Insert(doc.Length / 2, "SESSION-B");
            expectedLength = doc.Length;

            var sw = Stopwatch.StartNew();
            var state = doc.CaptureSessionState()!;
            json = state.ToJson();
            sw.Stop();
            Console.WriteLine($"capture: {sw.Elapsed.TotalMilliseconds:F1}ms, {json.Length} bytes");
            Assert.IsTrue(sw.Elapsed.TotalMilliseconds < 100, "capture must be near-instant");
            Assert.IsTrue(json.Length < 2048, $"delta capture was {json.Length} B");
        }

        var restoredState = SessionTabState.FromJson(json)!;
        Assert.IsTrue(Document.FingerprintMatches(restoredState));
        using var restored = Document.OpenAsync(s_file).GetAwaiter().GetResult();
        WaitForIndex(restored, TimeSpan.FromMinutes(5));
        restored.ApplySessionState(restoredState);
        Assert.AreEqual(expectedLength, restored.Length);
        Assert.AreEqual("SESSION-A", restored.GetText(1000, 9));
        Assert.IsTrue(restored.IsDirty);
    }
}
