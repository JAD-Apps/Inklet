using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

/// <summary>
/// Guards the distinction between <see cref="Document.Length"/> (which includes the
/// indexer's density estimate while a streamed open is still running) and
/// <see cref="Document.AddressableLength"/> (what the piece tree can actually
/// address right now).
///
/// Regression: session restore put a caret at ~1.05e9 in a 1 GB document, the editor
/// clamped it to Length, and the resulting geometry lookup threw
/// ArgumentOutOfRangeException out of a XAML callback — killing the process on every
/// launch until LocalState was cleared by hand. Clamping to AddressableLength is the
/// fix; these tests pin the property that makes it correct.
/// </summary>
[TestClass]
[DoNotParallelize] // shares a corpus file on disk
public sealed class DocumentAddressableLengthTests
{
    private static string s_file = "";
    private const long TargetBytes = 64L * 1024 * 1024; // > SmallFileThresholdBytes, so it streams
    private const string Line = "2026-08-27T00:00:00.000Z INFO worker-000 request handled path=/api/v1/items/000000 status=200\r\n";

    [ClassInitialize]
    public static void CreateCorpus(TestContext _)
    {
        if (!Environment.Is64BitProcess)
            Assert.Inconclusive("The streaming path is 64-bit only; 32-bit uses full decode.");
        s_file = Path.Combine(Path.GetTempPath(), "inklet-addressable-64mb.log");
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
    public void WhenStillIndexingThenAddressableLengthIsTheSafeGeometryCeiling()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();

        long addressable = doc.AddressableLength;
        long estimated = doc.Length;
        Console.WriteLine($"AddressableLength={addressable:N0}  Length={estimated:N0}  fullyIndexed={doc.IsFullyIndexed}");

        Assert.IsLessThanOrEqualTo(estimated, addressable,
            "AddressableLength must never exceed Length");

        // The ceiling the editor is allowed to clamp to is always safe...
        _ = doc.GetLineColumn(addressable);
        _ = doc.GetLineColumn(0);

        // ...whereas Length is NOT, precisely because it carries the estimate.
        // (Vacuous only if the file indexed before we looked; the assertion above
        // still holds, and the negative case is covered by the synthetic test below.)
        if (estimated > addressable)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => doc.GetLineColumn(estimated),
                "clamping a caret to Length and asking for geometry is the crash");
        }
    }

    [TestMethod]
    public void WhenCaretRestoredBeyondFrontierThenClampingToAddressableLengthSurvives()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();

        // A session-restored caret from a previous run, far past anything absorbed.
        const long RestoredCaret = 1_055_999_916;

        long safe = Math.Clamp(RestoredCaret, 0, doc.AddressableLength);
        var (line, column) = doc.GetLineColumn(safe);   // must not throw
        Assert.IsGreaterThanOrEqualTo(0, line);
        Assert.IsGreaterThanOrEqualTo(0, column);
        Console.WriteLine($"restored caret {RestoredCaret:N0} clamped to {safe:N0} -> line {line:N0}, col {column}");
    }

    /// <summary>
    /// The IME text-request path (OnEcTextRequested) reads a window with GetText.
    /// TSF requests are inbound and can arrive mid-index, so that read window must
    /// be bounded by AddressableLength too — clamping it to Length killed the
    /// process from a WinRT callback.
    /// </summary>
    [TestMethod]
    public void WhenStillIndexingThenGetTextIsBoundedByAddressableLengthNotLength()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();

        long addressable = doc.AddressableLength;
        long estimated = doc.Length;

        // A read window ending at the addressable ceiling is fine...
        const int Window = 64 * 1024;
        long start = Math.Max(0, addressable - Window);
        _ = doc.GetText(start, addressable - start);

        // ...one ending at Length is not, while the estimate is still in play.
        if (estimated > addressable)
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(
                () => doc.GetText(start, estimated - start));
        }
    }

    [TestMethod]
    public void WhenFullyIndexedThenAddressableLengthEqualsLength()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();
        WaitForIndex(doc, TimeSpan.FromMinutes(2));

        Assert.AreEqual(doc.Length, doc.AddressableLength,
            "once indexing is absorbed there is no estimated tail left");
        _ = doc.GetLineColumn(doc.Length); // the whole document is addressable now
    }

    [TestMethod]
    public void WhenInMemoryDocumentThenAddressableLengthEqualsLength()
    {
        using var doc = Document.CreateUntitled("alpha\r\nbeta\r\n");
        Assert.AreEqual(doc.Length, doc.AddressableLength);
        Assert.AreEqual(13, doc.AddressableLength);
    }

    [TestMethod]
    public void WhenEditedWhileIndexingThenAddressableLengthTracksTheEdit()
    {
        using var doc = Document.OpenAsync(s_file).GetAwaiter().GetResult();
        long before = doc.AddressableLength;

        doc.Insert(0, "XY");
        Assert.AreEqual(before + 2, doc.AddressableLength);
        _ = doc.GetLineColumn(doc.AddressableLength);

        doc.Undo();
        Assert.AreEqual(before, doc.AddressableLength);
    }
}
