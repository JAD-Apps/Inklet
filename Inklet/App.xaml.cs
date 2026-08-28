using Microsoft.UI.Xaml;
using System;
using System.Linq;

namespace Inklet;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>
    /// Initializes the singleton application object.
    /// </summary>
    public App()
    {
        Diagnostics.Perf.Mark("AppCtor");
        InitializeComponent();

        // Crash forensics: stowed exceptions kill a WinUI app with 0xC000027B and
        // no managed stack anywhere. Log every route to %TEMP% before dying.
        UnhandledException += (_, e) =>
            CrashLog("XamlUnhandled", e.Exception, e.Message);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLog("AppDomainUnhandled", e.ExceptionObject as Exception, null);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            CrashLog("UnobservedTask", e.Exception, null);
    }

    private static void CrashLog(string source, Exception? ex, string? message)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "inklet-crash.log");
            System.IO.File.AppendAllText(path,
                $"[{DateTime.Now:HH:mm:ss.fff}] {source}: {message}\n{ex}\n---\n");
        }
        catch { }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ResolveCommandLineFile());
        if (Diagnostics.Perf.Enabled)
        {
            void OnFirstActivated(object s, WindowActivatedEventArgs e)
            {
                Diagnostics.Perf.Mark("Activated");
                _window!.Activated -= OnFirstActivated;
            }
            _window.Activated += OnFirstActivated;
        }
        _window.Activate();
    }

    /// <summary>
    /// Returns the canonical absolute path of the first non-flag command-line argument
    /// that points to an existing file, or null if there isn't one. Defensive against
    /// relative paths, missing files, and Path.GetFullPath throwing on malformed input.
    /// </summary>
    private static string? ResolveCommandLineFile()
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length <= 1) return null;

        var raw = cmdArgs.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(raw)) return null;

        try
        {
            var full = System.IO.Path.GetFullPath(raw);
            return System.IO.File.Exists(full) ? full : null;
        }
        catch
        {
            return null;
        }
    }
}
