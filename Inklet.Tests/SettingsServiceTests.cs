using Inklet.Models;
using Inklet.Services;
using System.Text;
using System.Text.Json;

namespace Inklet.Tests;

/// <summary>
/// Tests for the static, ApplicationData-independent parts of <see cref="SettingsService"/>.
/// The instance methods depend on <c>ApplicationData.Current</c> which is unavailable in
/// unit-test (unpackaged) processes, so we test the file IO helpers directly.
/// </summary>
[TestClass]
public class SettingsServiceTests
{
    private string _testDir = null!;
    private string _path = null!;

    [TestInitialize]
    public void Setup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _testDir = Path.Combine(Path.GetTempPath(), $"InkletSessionTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _path = Path.Combine(_testDir, "session.json");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [TestMethod]
    public void WhenWritingNewSessionFileThenFileExistsWithContent()
    {
        SettingsService.WriteSessionFileAtomic(_path, "[]");

        Assert.IsTrue(File.Exists(_path));
        Assert.AreEqual("[]", File.ReadAllText(_path));
    }

    [TestMethod]
    public void WhenOverwritingExistingFileThenBackupIsCreated()
    {
        File.WriteAllText(_path, "[\"old\"]");

        SettingsService.WriteSessionFileAtomic(_path, "[\"new\"]");

        Assert.AreEqual("[\"new\"]", File.ReadAllText(_path));
        Assert.IsTrue(File.Exists(_path + ".bak"));
        Assert.AreEqual("[\"old\"]", File.ReadAllText(_path + ".bak"));
    }

    [TestMethod]
    public void WhenWriteCompletesThenNoTempFileLeftBehind()
    {
        SettingsService.WriteSessionFileAtomic(_path, "[]");

        Assert.IsFalse(File.Exists(_path + ".tmp"));
    }

    [TestMethod]
    public void WhenReadingMissingFileThenReturnsEmpty()
    {
        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void WhenReadingValidFileThenReturnsTabs()
    {
        var tabs = new[]
        {
            new PersistedTabData { FilePath = "a.txt", Content = "hello", IsModified = true },
            new PersistedTabData { FilePath = null,    Content = "draft", IsModified = true },
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(tabs));

        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(2, result.Count);
        Assert.AreEqual("a.txt", result[0].FilePath);
        Assert.AreEqual("hello", result[0].Content);
        Assert.IsNull(result[1].FilePath);
    }

    [TestMethod]
    public void WhenPrimaryFileCorruptThenFallsBackToBackup()
    {
        var goodTabs = new[] { new PersistedTabData { Content = "from backup", IsModified = true } };
        File.WriteAllText(_path + ".bak", JsonSerializer.Serialize(goodTabs));
        File.WriteAllText(_path, "{ this is not valid json");

        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("from backup", result[0].Content);
    }

    [TestMethod]
    public void WhenPrimaryFileMissingButBackupExistsThenUsesBackup()
    {
        var goodTabs = new[] { new PersistedTabData { Content = "recovered", IsModified = true } };
        File.WriteAllText(_path + ".bak", JsonSerializer.Serialize(goodTabs));

        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(1, result.Count);
        Assert.AreEqual("recovered", result[0].Content);
    }

    [TestMethod]
    public void WhenBothFilesCorruptThenReturnsEmpty()
    {
        File.WriteAllText(_path, "garbage");
        File.WriteAllText(_path + ".bak", "also garbage");

        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void WhenPrimaryEmptyThenReturnsEmpty()
    {
        File.WriteAllText(_path, "");

        var result = SettingsService.ReadSessionFile(_path);

        Assert.AreEqual(0, result.Count);
    }

    [TestMethod]
    public void WhenAtomicWriteCalledRepeatedlyThenFileIsAlwaysReadable()
    {
        // Simulates rapid session-tab churn — every read between writes must succeed.
        for (int i = 0; i < 10; i++)
        {
            SettingsService.WriteSessionFileAtomic(_path, $"[{{\"path\":\"file{i}.txt\"}}]");
            var roundTrip = SettingsService.ReadSessionFile(_path);
            Assert.AreEqual(1, roundTrip.Count);
            Assert.AreEqual($"file{i}.txt", roundTrip[0].FilePath);
        }
    }
}
