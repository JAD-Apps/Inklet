using System.Text;
using Inklet.Engine;

namespace Inklet.Tests.Engine;

[TestClass]
public sealed class DocumentSaveTests
{
    private const int TinySegment = 8192;

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"inklet-save-{Guid.NewGuid():N}.txt");

    private static void DriveToCompletion(Document doc)
    {
        while (doc.AdvanceIndexForTest()) { }
        doc.AbsorbIndexedSegments();
    }

    [TestMethod]
    public void WhenUneditedStreamedDocSavedAsThenByteIdentical()
    {
        // Mixed EOLs and an invalid-free UTF-8 body must round-trip exactly.
        var content = new StringBuilder();
        for (int i = 0; i < 500; i++)
            content.Append($"crlf {i}\r\n").Append($"lf {i}\n").Append($"cr {i}\r").Append("日本語😀\n");
        byte[] source = Encoding.UTF8.GetBytes(content.ToString());
        string src = TempPath(), dst = TempPath();
        File.WriteAllBytes(src, source);
        try
        {
            using var doc = Document.OpenForTest(src, TinySegment);
            DriveToCompletion(doc);
            doc.SaveAsync(new SaveOptions { TargetPath = dst }).GetAwaiter().GetResult();
            CollectionAssert.AreEqual(source, File.ReadAllBytes(dst), "byte identity");
        }
        finally { File.Delete(src); File.Delete(dst); }
    }

    [TestMethod]
    public void WhenOneEditThenSaveIsByteIdenticalOutsideTheEdit()
    {
        var content = new StringBuilder();
        for (int i = 0; i < 2000; i++) content.Append($"row {i:D6} mixed\r\nunix line\n");
        byte[] source = Encoding.UTF8.GetBytes(content.ToString());
        string src = TempPath(), dst = TempPath();
        File.WriteAllBytes(src, source);
        try
        {
            using var doc = Document.OpenForTest(src, TinySegment);
            DriveToCompletion(doc);
            doc.Insert(10, "EDIT");
            doc.SaveAsync(new SaveOptions { TargetPath = dst }).GetAwaiter().GetResult();

            var written = File.ReadAllBytes(dst);
            var expected = source.Take(10).Concat(Encoding.UTF8.GetBytes("EDIT")).Concat(source.Skip(10)).ToArray();
            CollectionAssert.AreEqual(expected, written);
        }
        finally { File.Delete(src); File.Delete(dst); }
    }

    [TestMethod]
    public void WhenSaveOverSelfThenAtomicAndUndoSurvives()
    {
        byte[] source = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"line {i}\r\n")));
        string path = TempPath();
        File.WriteAllBytes(path, source);
        try
        {
            using var doc = Document.OpenForTest(path, TinySegment);
            DriveToCompletion(doc);
            doc.Insert(0, "HEAD:");
            Assert.IsTrue(doc.IsDirty);

            doc.SaveAsync().GetAwaiter().GetResult();
            Assert.IsFalse(doc.IsDirty, "clean after save");
            Assert.IsFalse(File.Exists(path + ".tmp"), "temp cleaned up");
            StringAssert.StartsWith(Encoding.UTF8.GetString(File.ReadAllBytes(path)), "HEAD:line 0");

            // Undo history survives the save; undoing makes it dirty again.
            Assert.IsTrue(doc.CanUndo);
            doc.Undo();
            Assert.IsTrue(doc.IsDirty);
            Assert.AreEqual("line 0", doc.GetText(0, 6));

            // And a second save writes the reverted content.
            doc.SaveAsync().GetAwaiter().GetResult();
            CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
            Assert.IsFalse(doc.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenSaveWhileStillIndexingThenTailBytesPreserved()
    {
        byte[] source = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, 3000).Select(i => $"idx row {i:D6}\n")));
        string src = TempPath(), dst = TempPath();
        File.WriteAllBytes(src, source);
        try
        {
            using var doc = Document.OpenForTest(src, TinySegment);
            doc.AdvanceIndexForTest();           // a couple of segments only
            doc.AbsorbIndexedSegments();
            Assert.IsFalse(doc.IsFullyIndexed);
            doc.Insert(0, "X");

            doc.SaveAsync(new SaveOptions { TargetPath = dst }).GetAwaiter().GetResult();
            var expected = new byte[] { (byte)'X' }.Concat(source).ToArray();
            CollectionAssert.AreEqual(expected, File.ReadAllBytes(dst), "un-indexed tail streamed raw");
        }
        finally { File.Delete(src); File.Delete(dst); }
    }

    [TestMethod]
    public void WhenBomDocumentSavedThenBomPreserved()
    {
        string body = "bom content\r\nsecond";
        byte[] source = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(body)).ToArray();
        string path = TempPath();
        File.WriteAllBytes(path, source);
        try
        {
            using var doc = Document.OpenAsync(path).GetAwaiter().GetResult();
            Assert.IsTrue(doc.HasBom);
            doc.Insert(0, "x");
            doc.SaveAsync().GetAwaiter().GetResult();
            var written = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(Encoding.UTF8.GetPreamble(), written.Take(3).ToArray(), "BOM kept");
            Assert.AreEqual("x" + body, Encoding.UTF8.GetString(written.Skip(3).ToArray()));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenEncodingOverrideThenTranscoded()
    {
        string body = "converted content\r\nsecond line";
        string path = TempPath();
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(body));
        try
        {
            using var doc = Document.OpenAsync(path).GetAwaiter().GetResult();
            doc.SaveAsync(new SaveOptions
            {
                EncodingOverride = Encoding.Unicode,
                BomOverride = true,
            }).GetAwaiter().GetResult();

            var written = File.ReadAllBytes(path);
            CollectionAssert.AreEqual(Encoding.Unicode.GetPreamble(), written.Take(2).ToArray());
            Assert.AreEqual(body, Encoding.Unicode.GetString(written.Skip(2).ToArray()));
            Assert.AreEqual(1200, doc.Encoding.CodePage, "document metadata updated");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void WhenUntitledSavedToNewFileThenCreated()
    {
        string path = TempPath();
        try
        {
            using var doc = Document.CreateUntitled("fresh\r\ncontent");
            doc.Insert(0, "very ");
            doc.SaveAsync(new SaveOptions { TargetPath = path }).GetAwaiter().GetResult();
            Assert.AreEqual("very fresh\r\ncontent", File.ReadAllText(path));
            Assert.AreEqual(path, doc.FilePath);
            Assert.IsFalse(doc.IsDirty);
        }
        finally { File.Delete(path); }
    }
}
