using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
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

            // Migration: remove the old SessionTabs key stored in ApplicationDataContainer
            // (limited to 8 KB) — session data is now stored in a JSON file.
            _settings.Values.Remove("SessionTabs");
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

    /// <summary>Font size in points. Default 14 matches MainWindow.xaml's TextBox FontSize.</summary>
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
        get => GetValue(nameof(WindowHeight), 550.0);
        set => SetValue(nameof(WindowHeight), value);
    }

    /// <summary>Window was maximized when last closed.</summary>
    internal bool WindowMaximized
    {
        get => GetValue(nameof(WindowMaximized), false);
        set => SetValue(nameof(WindowMaximized), value);
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
    /// Stored as a JSON file in LocalFolder to avoid the 8 KB ApplicationDataContainer limit.
    /// </summary>
    internal IReadOnlyList<PersistedTabData> SessionTabs
    {
        get
        {
            try
            {
                var path = GetSessionFilePath();
                if (path is null) return [];
                return ReadSessionFile(path);
            }
            catch { return []; }
        }
        set
        {
            try
            {
                var path = GetSessionFilePath();
                if (path is null) return;
                var json = JsonSerializer.Serialize(value, s_jsonOptions);
                WriteSessionFileAtomic(path, json);
            }
            catch { }
        }
    }

    /// <summary>
    /// Asynchronously persists session tabs without blocking the UI thread.
    /// Returns false if the write failed (e.g., disk full, permission denied).
    /// Used by the window-close path so the UI thread is not blocked while writing
    /// large unsaved buffers.
    /// </summary>
    internal async Task<bool> SaveSessionTabsAsync(IReadOnlyList<PersistedTabData> tabs)
    {
        try
        {
            var path = GetSessionFilePath();
            if (path is null) return false;
            var json = JsonSerializer.Serialize(tabs, s_jsonOptions);
            await WriteSessionFileAtomicAsync(path, json).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Async counterpart to <see cref="WriteSessionFileAtomic"/>. The rename is still
    /// synchronous (it's already atomic on NTFS); only the bytes-to-disk part is awaited.
    /// </summary>
    internal static async Task WriteSessionFileAtomicAsync(string path, string content)
    {
        var tmp = path + ".tmp";
        var backup = path + ".bak";

        await File.WriteAllTextAsync(tmp, content).ConfigureAwait(false);

        if (File.Exists(path))
            File.Replace(tmp, path, backup, ignoreMetadataErrors: true);
        else
            File.Move(tmp, path);
    }

    /// <summary>
    /// Reads and deserializes the session file. If the primary file is missing or
    /// corrupted, falls back to the .bak copy left by <see cref="WriteSessionFileAtomic"/>.
    /// </summary>
    internal static IReadOnlyList<PersistedTabData> ReadSessionFile(string path)
    {
        if (TryDeserialize(path, out var tabs)) return tabs;

        var backup = path + ".bak";
        if (File.Exists(backup) && TryDeserialize(backup, out var backupTabs)) return backupTabs;

        return [];
    }

    private static bool TryDeserialize(string path, out IReadOnlyList<PersistedTabData> result)
    {
        result = [];
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return false;
            var tabs = JsonSerializer.Deserialize<PersistedTabData[]>(json);
            if (tabs is null) return false;
            result = tabs;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> atomically.
    /// The write goes to a temp sibling and is then renamed into place via
    /// <see cref="File.Replace(string, string, string?, bool)"/>, leaving a .bak copy of the
    /// previous version. A crash mid-write therefore cannot corrupt the live file:
    /// the previous file remains intact on disk.
    /// </summary>
    internal static void WriteSessionFileAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        var backup = path + ".bak";

        // Write the new content to a sibling file.
        File.WriteAllText(tmp, content);

        if (File.Exists(path))
        {
            // File.Replace is atomic on NTFS — the live file is never absent, even
            // if the process is killed mid-call.
            File.Replace(tmp, path, backup, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
    };

    /// <summary>
    /// Returns the full path to the session file in LocalFolder, or null outside a packaged context.
    /// </summary>
    private string? GetSessionFilePath()
    {
        if (_settings is null) return null;
        try
        {
            var folder = ApplicationData.Current.LocalFolder.Path;
            return Path.Combine(folder, "session.json");
        }
        catch { return null; }
    }

    // In-memory read cache for the ApplicationDataContainer values. Reads check the
    // cache first; misses fall back to the underlying container and populate the cache.
    // Writes update both the cache and the container so a process restart sees the
    // latest values immediately. Avoids per-property COM round-trips on RestoreSettings,
    // MenuPageSetup, and the dozen other places that batch property reads.
    private readonly Dictionary<string, object?> _readCache = new(StringComparer.Ordinal);

    private T GetValue<T>(string key, T defaultValue)
    {
        if (_settings is null) return defaultValue;

        if (_readCache.TryGetValue(key, out var cached))
            return cached is T typedCached ? typedCached : defaultValue;

        try
        {
            if (_settings.Values.TryGetValue(key, out var value))
            {
                _readCache[key] = value;
                if (value is T typed) return typed;
            }
            else
            {
                // Cache the absence so we don't re-query a missing key — defaults are
                // returned without a COM round-trip thereafter.
                _readCache[key] = null;
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
            _readCache[key] = value;
        }
        catch
        {
            // Guard against settings write failures
        }
    }
}
