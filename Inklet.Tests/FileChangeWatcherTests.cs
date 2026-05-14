using Inklet.Services;

namespace Inklet.Tests;

[TestClass]
public class FileChangeWatcherTests
{
    private string _testDir = null!;
    private string _path = null!;

    [TestInitialize]
    public void Setup()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"InkletWatcherTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _path = Path.Combine(_testDir, "watched.txt");
        File.WriteAllText(_path, "initial");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_testDir, recursive: true); } catch { }
    }

    [TestMethod]
    public void WhenFileChangedExternallyThenCallbackFires()
    {
        var fired = new ManualResetEventSlim(false);
        using var watcher = new FileChangeWatcher(_path, () => fired.Set());

        File.WriteAllText(_path, "modified");

        // 250 ms debounce + watcher dispatch overhead. Cap test duration at 5 s.
        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)),
            "Callback should fire within 5 seconds of an external write");
    }

    [TestMethod]
    public void WhenSuppressNextChangeThenCallbackDoesNotFire()
    {
        var fired = new ManualResetEventSlim(false);
        using var watcher = new FileChangeWatcher(_path, () => fired.Set());

        watcher.SuppressNextChange();
        File.WriteAllText(_path, "self-write");

        // Should NOT fire — the suppression is effective for ~1 s.
        Assert.IsFalse(fired.Wait(TimeSpan.FromMilliseconds(750)),
            "Callback must not fire while suppression window is active");
    }

    [TestMethod]
    public void WhenSuppressionWindowExpiresThenLaterChangeFires()
    {
        var fired = new ManualResetEventSlim(false);
        using var watcher = new FileChangeWatcher(_path, () => fired.Set());

        watcher.SuppressNextChange();
        // Wait for the suppression window (1 s) to lapse, then write.
        Thread.Sleep(1100);
        File.WriteAllText(_path, "after suppression");

        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)),
            "Callback should fire after the suppression window expires");
    }

    [TestMethod]
    public void WhenMultipleRapidChangesThenCallbackFiresOnce()
    {
        int count = 0;
        var fired = new ManualResetEventSlim(false);
        using var watcher = new FileChangeWatcher(_path, () =>
        {
            Interlocked.Increment(ref count);
            fired.Set();
        });

        // Burst of 5 writes within debounce window — should coalesce to a single callback.
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(_path, $"v{i}");
            Thread.Sleep(20);
        }

        // Wait for the debounce + a margin.
        Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(5)));
        // Allow a moment for any stragglers, then confirm we got exactly one callback.
        Thread.Sleep(500);
        Assert.AreEqual(1, count, "Burst of writes should debounce to a single callback");
    }

    [TestMethod]
    public void WhenDisposedThenNoFurtherCallbacks()
    {
        var fired = new ManualResetEventSlim(false);
        var watcher = new FileChangeWatcher(_path, () => fired.Set());

        watcher.Dispose();
        File.WriteAllText(_path, "after dispose");

        Assert.IsFalse(fired.Wait(TimeSpan.FromMilliseconds(750)));
    }
}
