using System.Text.Json;
using Inklet.Engine;
using Inklet.Models;

namespace Inklet.Tests;

/// <summary>
/// Tests for <see cref="TabSession"/> (dirty state now derives from the engine
/// document's undo position) and the v1 <see cref="PersistedTabData"/> format
/// kept for session-file migration.
/// </summary>
[TestClass]
public sealed class SessionTests
{
    // ---------------------------------------------------------------
    // TabSession.IsModified / TabTitle (engine-document backed)
    // ---------------------------------------------------------------

    [TestMethod]
    public void WhenNewSessionThenCleanAndUntitled()
    {
        using var session = new TabSession { Doc = Document.CreateUntitled() };
        Assert.IsFalse(session.IsModified);
        Assert.AreEqual("Untitled", session.TabTitle);
    }

    [TestMethod]
    public void WhenDocEditedThenModifiedAndStarred()
    {
        using var session = new TabSession { Doc = Document.CreateUntitled() };
        session.Doc!.Insert(0, "typed");
        Assert.IsTrue(session.IsModified);
        Assert.AreEqual("*Untitled", session.TabTitle);
    }

    [TestMethod]
    public void WhenUndoBackToSavedThenCleanAgain()
    {
        using var session = new TabSession { Doc = Document.CreateUntitled() };
        session.Doc!.Insert(0, "typed");
        session.Doc.Undo();
        Assert.IsFalse(session.IsModified, "undo to the saved state flips the tab clean");
    }

    [TestMethod]
    public void WhenSavedThenCleanWithFileName()
    {
        using var session = new TabSession
        {
            Doc = Document.FromText("content"),
            FilePath = @"C:\docs\notes.txt",
        };
        session.Doc!.Insert(0, "x");
        Assert.AreEqual("*notes.txt", session.TabTitle);
        session.Doc.MarkSaved();
        Assert.AreEqual("notes.txt", session.TabTitle);
    }

    [TestMethod]
    public void WhenRestoredDirtyThenModifiedWithoutUndoHistory()
    {
        using var session = new TabSession { Doc = Document.CreateUntitled("restored unsaved") };
        session.Doc!.MarkRestoredDirty();
        Assert.IsTrue(session.IsModified);
        Assert.IsFalse(session.Doc.CanUndo);
    }

    [TestMethod]
    public void WhenDisposedThenDocumentReleased()
    {
        var session = new TabSession { Doc = Document.CreateUntitled("x") };
        session.Dispose();
        Assert.IsNull(session.Doc);
    }

    // ---------------------------------------------------------------
    // v1 PersistedTabData format (migration source - shape must not drift)
    // ---------------------------------------------------------------

    [TestMethod]
    public void WhenV1DataSerializedThenJsonPropertyNamesStable()
    {
        var data = new PersistedTabData
        {
            FilePath = @"C:\f.txt",
            Content = "body",
            IsModified = true,
            CursorPosition = 3,
            EncodingCodePage = 65001,
            HasBom = true,
            LineEnding = 1,
        };
        var json = JsonSerializer.Serialize(data);
        StringAssert.Contains(json, "\"path\"");
        StringAssert.Contains(json, "\"content\"");
        StringAssert.Contains(json, "\"dirty\"");
        StringAssert.Contains(json, "\"cursor\"");
        StringAssert.Contains(json, "\"encoding\"");
        StringAssert.Contains(json, "\"bom\"");
        StringAssert.Contains(json, "\"lineEnding\"");

        var back = JsonSerializer.Deserialize<PersistedTabData>(json)!;
        Assert.AreEqual(data, back);
    }

    [TestMethod]
    public void WhenV1ArrayParsedThenRoundTrips()
    {
        const string v1Json = """
            [{"path":null,"content":"unsaved text","dirty":true,"cursor":5,"encoding":65001,"bom":false,"lineEnding":0},
             {"path":"C:\\a.txt","content":"","dirty":false,"cursor":0,"encoding":1252,"bom":false,"lineEnding":0}]
            """;
        var tabs = JsonSerializer.Deserialize<PersistedTabData[]>(v1Json)!;
        Assert.HasCount(2, tabs);
        Assert.AreEqual("unsaved text", tabs[0].Content);
        Assert.IsTrue(tabs[0].IsModified);
        Assert.AreEqual(@"C:\a.txt", tabs[1].FilePath);
        Assert.AreEqual(1252, tabs[1].EncodingCodePage);
    }

    // ---------------------------------------------------------------
    // v2 envelope shape (what SettingsService writes)
    // ---------------------------------------------------------------

    [TestMethod]
    public void WhenV2EnvelopeSerializedThenVersionTagged()
    {
        var envelope = new Inklet.Services.SettingsService.SessionV2Envelope
        {
            ActiveTab = 1,
            Tabs =
            [
                new SessionTabState { FilePath = @"C:\big.log", CaretOffset = 42 },
                new SessionTabState { UntitledContent = "draft", Dirty = true },
            ],
        };
        var json = JsonSerializer.Serialize(envelope, SessionJson.Options);
        StringAssert.Contains(json, "\"v\":2");
        StringAssert.Contains(json, "\"active\":1");

        var back = JsonSerializer.Deserialize<Inklet.Services.SettingsService.SessionV2Envelope>(json, SessionJson.Options)!;
        Assert.HasCount(2, back.Tabs);
        Assert.AreEqual(42, back.Tabs[0].CaretOffset);
        Assert.AreEqual("draft", back.Tabs[1].UntitledContent);
        Assert.IsTrue(back.Tabs[1].Dirty);
    }
}
