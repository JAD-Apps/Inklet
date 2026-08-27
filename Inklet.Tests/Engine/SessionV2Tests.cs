using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

[TestClass]
public sealed class SessionV2Tests
{
    private const int TinySegment = 8192;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"inklet-sess-{Guid.NewGuid():N}.txt");

    private static void DriveToCompletion(Document doc)
    {
        while (doc.AdvanceIndexForTest()) { }
        doc.AbsorbIndexedSegments();
    }

    [TestMethod]
    public void WhenUntitledCapturedThenFullContentPersisted()
    {
        using var d = Document.CreateUntitled("untitled\r\ncontent");
        d.Insert(0, "typed ");
        var state = d.CaptureSessionState();
        Assert.IsNotNull(state);
        Assert.AreEqual("typed untitled\r\ncontent", state.UntitledContent);
        Assert.IsNull(state.Pieces);

        // JSON round-trip.
        var back = SessionTabState.FromJson(state.ToJson())!;
        Assert.AreEqual(state.UntitledContent, back.UntitledContent);
    }

    [TestMethod]
    public void WhenCleanFileTabCapturedThenNoContentAndNoPieces()
    {
        string path = TempPath();
        File.WriteAllText(path, "clean file content\r\nline 2");
        try
        {
            using var d = Document.OpenAsync(path).GetAwaiter().GetResult();
            var state = d.CaptureSessionState()!;
            Assert.AreEqual(path, state.FilePath);
            Assert.IsNull(state.Pieces);
            Assert.IsNull(state.UntitledContent);
            Assert.IsTrue(state.FileSize > 0);
            Assert.IsTrue(Document.FingerprintMatches(state));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenDirtyFileTabCapturedThenDeltasOnlyAndTiny()
    {
        var body = string.Concat(Enumerable.Range(0, 50_000).Select(i => $"big row {i:D8}\r\n"));
        string path = TempPath();
        File.WriteAllText(path, body);
        try
        {
            using var d = Document.OpenForTest(path, TinySegment);
            DriveToCompletion(d);
            d.Insert(100, "EDIT-A");
            d.Insert(5000, "EDIT-B");
            d.Delete(20, 7);

            var state = d.CaptureSessionState()!;
            Assert.IsNotNull(state.Pieces);
            string json = state.ToJson();
            Assert.IsTrue(json.Length < 2048, $"delta capture should be tiny, was {json.Length} B for a {body.Length} B document");
            Assert.IsFalse(json.Contains("big row 00025000"), "file content must not be embedded");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenDeltasRestoredThenContentAndDirtinessRecovered()
    {
        var body = string.Concat(Enumerable.Range(0, 20_000).Select(i => $"content row {i:D6}\n"));
        string path = TempPath();
        File.WriteAllText(path, body);
        try
        {
            string expectedText;
            string json;
            using (var d = Document.OpenForTest(path, TinySegment))
            {
                DriveToCompletion(d);
                d.Insert(50, "SESSION-EDIT ");
                d.Delete(200, 25);
                d.Insert(d.AbsorbedLength, "\nTAIL");
                expectedText = d.GetText(0, d.Length);
                json = d.CaptureSessionState()!.ToJson();
            }

            var state = SessionTabState.FromJson(json)!;
            Assert.IsTrue(Document.FingerprintMatches(state));
            using var restored = Document.OpenForTest(path, TinySegment);
            DriveToCompletion(restored);
            restored.ApplySessionState(state);

            Assert.AreEqual(expectedText, restored.GetText(0, restored.Length));
            Assert.IsTrue(restored.IsDirty, "restored unsaved edits read as dirty");
            Assert.IsFalse(restored.CanUndo, "undo history does not survive restarts");

            // Geometry equivalence with a reference build of the same text.
            using var reference = Document.FromText(expectedText);
            Assert.AreEqual(reference.LineCount, restored.LineCount);
            Assert.AreEqual(reference.GetOffsetForLine(reference.LineCount - 1),
                restored.GetOffsetForLine(restored.LineCount - 1));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenFileChangedOnDiskThenFingerprintMismatch()
    {
        string path = TempPath();
        File.WriteAllText(path, "original");
        try
        {
            using var d = Document.OpenAsync(path).GetAwaiter().GetResult();
            var state = d.CaptureSessionState()!;
            File.WriteAllText(path, "changed on disk meanwhile");
            Assert.IsFalse(Document.FingerprintMatches(state));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenDirtyStreamedDocStillIndexingThenCaptureDeclines()
    {
        var body = string.Concat(Enumerable.Range(0, 3000).Select(i => $"gate row {i:D6}\n"));
        string path = TempPath();
        File.WriteAllText(path, body);
        try
        {
            using var d = Document.OpenForTest(path, TinySegment);
            d.Insert(0, "dirty");
            Assert.IsFalse(d.IsFullyIndexed);
            Assert.IsNull(d.CaptureSessionState(), "cannot capture deltas before the file is fully indexed");
        }
        finally { File.Delete(path); }
    }
}
