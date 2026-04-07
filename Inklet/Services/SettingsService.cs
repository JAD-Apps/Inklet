using System;
using System.Collections.Generic;
using System.Text.Json;
using Inklet.Models;
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
        get => GetValue(nameof(FontSize), 12.0);
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
        get => GetValue(nameof(WindowHeight), 550.0);
        set => SetValue(nameof(WindowHeight), value);
    }

    /// <summary>Active tab index from the last session.</summary>
    internal int LastActiveTabIndex
    {
        get => GetValue(nameof(LastActiveTabIndex), 0);
        set => SetValue(nameof(LastActiveTabIndex), value);
    }

    // ---------------------------------------------------------------
    // Page setup
    // ---------------------------------------------------------------

    /// <summary>Top margin in inches.</summary>
    internal double PrintMarginTop
    {
        get => GetValue(nameof(PrintMarginTop), 1.0);
        set => SetValue(nameof(PrintMarginTop), value);
    }

    /// <summary>Bottom margin in inches.</summary>
    internal double PrintMarginBottom
    {
        get => GetValue(nameof(PrintMarginBottom), 1.0);
        set => SetValue(nameof(PrintMarginBottom), value);
    }

    /// <summary>Left margin in inches.</summary>
    internal double PrintMarginLeft
    {
        get => GetValue(nameof(PrintMarginLeft), 1.25);
        set => SetValue(nameof(PrintMarginLeft), value);
    }

    /// <summary>Right margin in inches.</summary>
    internal double PrintMarginRight
    {
        get => GetValue(nameof(PrintMarginRight), 1.25);
        set => SetValue(nameof(PrintMarginRight), value);
    }

    /// <summary>
    /// Header template. Tokens: &amp;f = filename, &amp;d = short date, &amp;t = time, &amp;p = page, &amp;P = total pages.
    /// Empty string disables the header.
    /// </summary>
    internal string PrintHeader
    {
        get => GetValue(nameof(PrintHeader), "&f");
        set => SetValue(nameof(PrintHeader), value);
    }

    /// <summary>
    /// Footer template. Tokens: same as <see cref="PrintHeader"/>.
    /// Empty string disables the footer.
    /// </summary>
    internal string PrintFooter
    {
        get => GetValue(nameof(PrintFooter), "Page &p of &P");
        set => SetValue(nameof(PrintFooter), value);
    }

    /// <summary>
    /// Full per-tab state persisted at session close, including unsaved content.
    /// </summary>
    internal IReadOnlyList<PersistedTabData> SessionTabs
    {
        get
        {
            var json = GetValue<string>(nameof(SessionTabs), null!);
            if (string.IsNullOrEmpty(json)) return [];
            try
            {
                return JsonSerializer.Deserialize<PersistedTabData[]>(json) ?? [];
            }
            catch { return []; }
        }
        set
        {
            try
            {
                SetValue(nameof(SessionTabs), JsonSerializer.Serialize(value));
            }
            catch { }
        }
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
