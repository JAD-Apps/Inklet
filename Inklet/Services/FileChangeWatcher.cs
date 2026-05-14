using System;
using System.IO;
using System.Threading;

namespace Inklet.Services;

/// <summary>
/// Watches a single file for external modifications and raises a debounced
/// <see cref="Changed"/> event back on a caller-supplied dispatcher.
///
/// Several traps that this class abstracts:
/// <list type="bullet">
///   <item>Editors typically write a file via temp-then-rename, which generates
///         multiple FileSystemWatcher events in quick succession. We debounce by
///         <see cref="DebounceMilliseconds"/> after the last raw event.</item>
///   <item>When we save the file ourselves the watcher will fire; callers can
///         <see cref="SuppressNextChange"/> to ignore the next event window.</item>
///   <item>FileSystemWatcher events arrive on a thread-pool thread; the consumer
///         dispatcher (typically <c>DispatcherQueue.TryEnqueue</c>) marshals back
///         to the UI thread.</item>
/// </list>
/// </summary>
internal sealed class FileChangeWatcher : IDisposable
{
    private const int DebounceMilliseconds = 250;

    private readonly FileSystemWatcher _watcher;
    private readonly Action _onChanged;
    private readonly Timer _debounceTimer;
    private long _suppressUntilTicks;
    private bool _disposed;

    /// <summary>
    /// Raises <paramref name="onChanged"/> when the file at <paramref name="filePath"/>
    /// is modified externally, debounced. The callback is invoked on a thread-pool
    /// thread — wrap any UI work in <c>DispatcherQueue.TryEnqueue</c>.
    /// </summary>
    public FileChangeWatcher(string filePath, Action onChanged)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(onChanged);

        _onChanged = onChanged;
        _debounceTimer = new Timer(OnDebounceElapsed, null, Timeout.Infinite, Timeout.Infinite);

        var directory = Path.GetDirectoryName(filePath) ?? throw new ArgumentException("Invalid path", nameof(filePath));
        var fileName = Path.GetFileName(filePath);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnRawEvent;
        _watcher.Created += OnRawEvent;
        _watcher.Renamed += OnRawEvent;
    }

    /// <summary>
    /// Marks the next ~1 second of events as "self-inflicted" — typically called
    /// immediately before saving the file ourselves, so the resulting watcher
    /// echo doesn't trigger an unwanted reload prompt.
    /// </summary>
    public void SuppressNextChange()
    {
        // 1 second is generous: covers slow disks, antivirus scans, OneDrive sync, etc.
        var until = DateTime.UtcNow.AddSeconds(1).Ticks;
        Interlocked.Exchange(ref _suppressUntilTicks, until);
    }

    private void OnRawEvent(object sender, FileSystemEventArgs e)
    {
        if (_disposed) return;
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressUntilTicks)) return;

        // Reset the debounce timer — the event handler will fire only after the file
        // has been quiet for DebounceMilliseconds.
        _debounceTimer.Change(DebounceMilliseconds, Timeout.Infinite);
    }

    private void OnDebounceElapsed(object? state)
    {
        if (_disposed) return;
        try { _onChanged(); }
        catch
        {
            // Caller's responsibility to surface errors — we must not let a callback
            // exception kill the timer thread.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _debounceTimer.Dispose();
    }
}
