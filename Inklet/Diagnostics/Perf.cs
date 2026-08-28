using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;

namespace Inklet.Diagnostics;

/// <summary>
/// Zero-overhead-when-disabled performance instrumentation. Enabled by setting
/// INKLET_PERF=1 in the environment; events are emitted both as ETW (provider
/// "Inklet-Perf", for WPA/PerfView traces) and, when INKLET_PERF_LOG is set to a
/// writable path (or by default %TEMP%\inklet-perf.csv), as a CSV the measurement
/// scripts in Scripts/ parse directly.
///
/// CSV columns: name,msSinceMain,utcTicks,id
/// "msSinceMain" is relative to the AppMain mark (first line of Main); the launch
/// script computes process-start → AppMain from utcTicks vs Process.StartTime.
/// </summary>
internal static class Perf
{
    public static readonly bool Enabled;

    private static readonly long s_t0 = Stopwatch.GetTimestamp();
    private static readonly object s_lock = new();
    private static StreamWriter? s_log;
    private static int s_nextKeystrokeId;

    static Perf()
    {
        Enabled = Environment.GetEnvironmentVariable("INKLET_PERF") == "1";
        if (!Enabled) return;

        var path = Environment.GetEnvironmentVariable("INKLET_PERF_LOG");
        if (string.IsNullOrWhiteSpace(path))
            path = Path.Combine(Path.GetTempPath(), "inklet-perf.csv");
        try
        {
            s_log = new StreamWriter(path, append: false) { AutoFlush = true };
            s_log.WriteLine("name,msSinceMain,utcTicks,id");
        }
        catch
        {
            s_log = null; // ETW still works; CSV sink is best-effort.
        }
    }

    private static double MsSinceMain()
        => (Stopwatch.GetTimestamp() - s_t0) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Emit a named one-shot milestone (AppMain, Activated, FirstTextDraw, …).</summary>
    public static void Mark(string name)
    {
        if (!Enabled) return;
        PerfEventSource.Log.Mark(name);
        Write(name, -1);
    }

    /// <summary>Reserve an id for a keystroke entering the pipeline and emit KeystrokeIn.</summary>
    public static int KeystrokeIn()
    {
        if (!Enabled) return -1;
        int id = ++s_nextKeystrokeId;
        PerfEventSource.Log.KeystrokeIn(id);
        Write("KeystrokeIn", id);
        return id;
    }

    /// <summary>Emit KeystrokeDrawn for the paired id from <see cref="KeystrokeIn"/>.</summary>
    public static void KeystrokeDrawn(int id)
    {
        if (!Enabled || id < 0) return;
        PerfEventSource.Log.KeystrokeDrawn(id);
        Write("KeystrokeDrawn", id);
    }

    private static void Write(string name, int id)
    {
        var line = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{name},{MsSinceMain():F3},{DateTime.UtcNow.Ticks},{id}");
        lock (s_lock) s_log?.WriteLine(line);
    }
}

[EventSource(Name = "Inklet-Perf")]
internal sealed class PerfEventSource : EventSource
{
    public static readonly PerfEventSource Log = new();

    private PerfEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Mark(string Name) { if (IsEnabled()) WriteEvent(1, Name); }

    [Event(2, Level = EventLevel.Informational)]
    public void KeystrokeIn(int Id) { if (IsEnabled()) WriteEvent(2, Id); }

    [Event(3, Level = EventLevel.Informational)]
    public void KeystrokeDrawn(int Id) { if (IsEnabled()) WriteEvent(3, Id); }
}
