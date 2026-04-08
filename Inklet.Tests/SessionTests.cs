using Inklet.Models;
using System.Text;

namespace Inklet.Tests;

/// <summary>
/// Tests for <see cref="TabSession"/> and <see cref="PersistedTabData"/> covering the four
/// session states: new empty tab, new tab with typed content, opened file without changes,
/// and opened file with unsaved changes.
/// </summary>
[TestClass]
public class SessionTests
{
    [TestInitialize]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    // -----------------------------------------------------------------------
    // TabSession.IsModified
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenNewEmptyTabThenIsModifiedFalse()
    {
        var session = new TabSession();

        Assert.IsFalse(session.IsModified);
    }

    [TestMethod]
    public void WhenUntitledTabWithTypedContentThenIsModifiedTrue()
    {
        var session = new TabSession
        {
            Content = "hello world",
            SavedContent = string.Empty
        };

        Assert.IsTrue(session.IsModified);
    }

    [TestMethod]
    public void WhenOpenedFileUnchangedThenIsModifiedFalse()
    {
        const string diskContent = "file content";
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = diskContent,
            SavedContent = diskContent
        };

        Assert.IsFalse(session.IsModified);
    }

    [TestMethod]
    public void WhenOpenedFileWithUnsavedChangesThenIsModifiedTrue()
    {
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = "edited content",
            SavedContent = "original content"
        };

        Assert.IsTrue(session.IsModified);
    }

    // -----------------------------------------------------------------------
    // TabSession.TabTitle
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenNewEmptyTabThenTabTitleIsUntitled()
    {
        var session = new TabSession();

        Assert.AreEqual("Untitled", session.TabTitle);
    }

    [TestMethod]
    public void WhenUntitledModifiedThenTabTitleHasAsteriskPrefix()
    {
        var session = new TabSession { Content = "unsaved" };

        Assert.AreEqual("*Untitled", session.TabTitle);
    }

    [TestMethod]
    public void WhenSavedFileUnchangedThenTabTitleIsFileName()
    {
        const string diskContent = "data";
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = diskContent,
            SavedContent = diskContent
        };

        Assert.AreEqual("notes.txt", session.TabTitle);
    }

    [TestMethod]
    public void WhenSavedFileModifiedThenTabTitleHasAsteriskPrefix()
    {
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = "changed",
            SavedContent = "original"
        };

        Assert.AreEqual("*notes.txt", session.TabTitle);
    }

    // -----------------------------------------------------------------------
    // PersistedTabData produced by the four scenarios (mirrors PersistSession)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenNewEmptyTabPersistedThenDataHasNoPathAndNotDirty()
    {
        var session = new TabSession();

        var data = BuildPersistedData(session);

        Assert.IsNull(data.FilePath);
        Assert.AreEqual(string.Empty, data.Content);
        Assert.IsFalse(data.IsModified);
    }

    [TestMethod]
    public void WhenUntitledTabWithContentPersistedThenDataIsDirtyAndContentPreserved()
    {
        var session = new TabSession { Content = "draft text" };

        var data = BuildPersistedData(session);

        Assert.IsNull(data.FilePath);
        Assert.AreEqual("draft text", data.Content);
        Assert.IsTrue(data.IsModified);
    }

    [TestMethod]
    public void WhenOpenedFileUnchangedPersistedThenDataIsNotDirtyAndContentOmitted()
    {
        const string diskContent = "original";
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = diskContent,
            SavedContent = diskContent
        };

        var data = BuildPersistedData(session);

        Assert.AreEqual(@"C:\docs\notes.txt", data.FilePath);
        Assert.IsFalse(data.IsModified);
        // Content is intentionally empty for unmodified file-backed tabs to reduce storage
        Assert.AreEqual(string.Empty, data.Content);
    }

    [TestMethod]
    public void WhenOpenedFileModifiedPersistedThenDataIsDirtyAndUnsavedContentPreserved()
    {
        var session = new TabSession
        {
            FilePath = @"C:\docs\notes.txt",
            Content = "edited",
            SavedContent = "original"
        };

        var data = BuildPersistedData(session);

        Assert.AreEqual(@"C:\docs\notes.txt", data.FilePath);
        Assert.AreEqual("edited", data.Content);
        Assert.IsTrue(data.IsModified);
    }

    // -----------------------------------------------------------------------
    // Session restore — untitled/missing-file branch (else path in InitialLoadAsync)
    // -----------------------------------------------------------------------

    [TestMethod]
    public void WhenRestoringEmptyUntitledTabThenSessionIsNotModified()
    {
        var data = new PersistedTabData
        {
            FilePath = null,
            Content = string.Empty,
            IsModified = false
        };

        var session = RestoreUntitledSession(data);

        Assert.IsFalse(session.IsModified);
        Assert.AreEqual(string.Empty, session.Content);
    }

    [TestMethod]
    public void WhenRestoringUntitledTabWithContentThenSessionIsModified()
    {
        var data = new PersistedTabData
        {
            FilePath = null,
            Content = "unsaved draft",
            IsModified = true
        };

        var session = RestoreUntitledSession(data);

        Assert.IsTrue(session.IsModified);
        Assert.AreEqual("unsaved draft", session.Content);
    }

    [TestMethod]
    public void WhenRestoringMissingFileWithUnsavedChangeThenSessionIsModified()
    {
        // File was on disk but no longer exists — we restore the last-known content.
        var data = new PersistedTabData
        {
            FilePath = @"C:\deleted\file.txt",
            Content = "last known content",
            IsModified = true
        };

        var session = RestoreUntitledSession(data);

        Assert.IsTrue(session.IsModified);
        Assert.AreEqual("last known content", session.Content);
    }

    [TestMethod]
    public void WhenRestoringMissingFileWithNoChangeThenSessionIsNotModified()
    {
        var data = new PersistedTabData
        {
            FilePath = @"C:\deleted\file.txt",
            Content = "file content",
            IsModified = false
        };

        var session = RestoreUntitledSession(data);

        Assert.IsFalse(session.IsModified);
        Assert.AreEqual("file content", session.Content);
    }

    [TestMethod]
    public void WhenRestoringSessionThenCursorPositionIsRestored()
    {
        var data = new PersistedTabData
        {
            Content = "hello world",
            IsModified = true,
            CursorPosition = 5
        };

        var session = RestoreUntitledSession(data);

        Assert.AreEqual(5, session.CursorPosition);
    }

    [TestMethod]
    public void WhenRestoringSessionThenEncodingIsRestored()
    {
        var data = new PersistedTabData
        {
            EncodingCodePage = 1252,
            IsModified = false,
            Content = string.Empty
        };

        var session = RestoreUntitledSession(data);

        Assert.AreEqual(1252, session.Document.Encoding.CodePage);
    }

    // -----------------------------------------------------------------------
    // Helpers — mirror the logic in MainWindow without requiring WinUI
    // -----------------------------------------------------------------------

    /// <summary>
    /// Mirrors the PersistedTabData projection in <c>PersistSession</c>.
    /// Content is intentionally omitted for unmodified file-backed tabs.
    /// </summary>
    private static PersistedTabData BuildPersistedData(TabSession s) => new()
    {
        FilePath = s.FilePath,
        Content = (s.FilePath is not null && !s.IsModified) ? string.Empty : s.Content,
        IsModified = s.IsModified,
        CursorPosition = s.CursorPosition,
        EncodingCodePage = s.Document.Encoding.CodePage,
        HasBom = s.Document.HasBom,
        LineEnding = (int)s.Document.LineEnding,
    };

    /// <summary>
    /// Mirrors the untitled/missing-file restore branch in <c>InitialLoadAsync</c>.
    /// </summary>
    private static TabSession RestoreUntitledSession(PersistedTabData data)
    {
        var session = new TabSession { FilePath = data.FilePath };
        session.Content = data.Content;
        session.SavedContent = data.IsModified ? string.Empty : data.Content;
        session.CursorPosition = data.CursorPosition;

        Encoding enc;
        try { enc = Encoding.GetEncoding(data.EncodingCodePage); }
        catch { enc = Encoding.UTF8; }

        session.Document = new DocumentState
        {
            FilePath = data.FilePath,
            Encoding = enc,
            HasBom = data.HasBom,
            LineEnding = (LineEndingStyle)data.LineEnding,
        };

        return session;
    }
}
