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
        // Check for command-line file argument
        string? filePath = null;
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Length > 1)
        {
            // First arg is the exe path, subsequent args are user arguments
            filePath = cmdArgs.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        }

        _window = new MainWindow(filePath);
        _window.Activate();
    }
}
