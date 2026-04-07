using System;
using Windows.Storage;

namespace Inklet.Services;

/// <summary>
/// Persists user preferences using local app settings.
/// </summary>
internal sealed class SettingsService
{
    private readonly ApplicationDataContainer _settings;

    internal SettingsService()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            // Running outside of a packaged context (e.g., unit tests)
            _settings = null!;
        }
    }

    /// <summary>Word wrap enabled.</summary>
    internal bool WordWrap
    {
        get => GetValue(nameof(WordWrap), false);
        set => SetValue(nameof(WordWrap), value);
    }

    /// <summary>Status bar visible.</summary>
    internal bool StatusBarVisible
    {
        get => GetValue(nameof(StatusBarVisible), true);
        set => SetValue(nameof(StatusBarVisible), value);
    }

    /// <summary>Font family name.</summary>
    internal string FontFamily
    {
        get => GetValue(nameof(FontFamily), "Consolas");
        set => SetValue(nameof(FontFamily), value);
    }

    /// <summary>Font size in points.</summary>
    internal double FontSize
    {
        get => GetValue(nameof(FontSize), 14.0);
        set => SetValue(nameof(FontSize), value);
    }

    /// <summary>Font weight (Normal, Bold).</summary>
    internal string FontWeight
    {
        get => GetValue(nameof(FontWeight), "Normal");
        set => SetValue(nameof(FontWeight), value);
    }

    /// <summary>Font style (Normal, Italic).</summary>
    internal string FontStyle
    {
        get => GetValue(nameof(FontStyle), "Normal");
        set => SetValue(nameof(FontStyle), value);
    }

    /// <summary>Zoom percentage (25–500).</summary>
    internal int ZoomPercent
    {
        get => GetValue(nameof(ZoomPercent), 100);
        set => SetValue(nameof(ZoomPercent), Math.Clamp(value, 25, 500));
    }

    /// <summary>Last used window width.</summary>
    internal double WindowWidth
    {
        get => GetValue(nameof(WindowWidth), 800.0);
        set => SetValue(nameof(WindowWidth), value);
    }

    /// <summary>Last used window height.</summary>
    internal double WindowHeight
    {
        get => GetValue(nameof(WindowHeight), 600.0);
        set => SetValue(nameof(WindowHeight), value);
    }

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_settings is null) return defaultValue;

        try
        {
            if (_settings.Values.TryGetValue(key, out var value) && value is T typed)
            {
                return typed;
            }
        }
        catch
        {
            // Guard against corrupted settings
        }

        return defaultValue;
    }

    private void SetValue<T>(string key, T value)
    {
        if (_settings is null) return;

        try
        {
            _settings.Values[key] = value;
        }
        catch
        {
            // Guard against settings write failures
        }
    }
}
