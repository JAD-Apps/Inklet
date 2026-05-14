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
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(ResolveCommandLineFile());
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
