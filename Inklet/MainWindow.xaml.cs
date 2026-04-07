using Inklet.Models;
using Inklet.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Inklet;

/// <summary>
/// Main application window — hosts a multi-tab editor with session persistence.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string[] s_textFileTypes = [".txt"];
    private static readonly string[] s_allFileTypes = ["."];

    // Common monospaced fonts shown in the font picker drop-down.
    private static readonly string[] s_monoFonts =
    [
        "Cascadia Code", "Cascadia Mono", "Consolas", "Courier New",
        "Lucida Console", "Lucida Sans Typewriter", "OCR A Extended",
        "Source Code Pro", "Fira Code", "JetBrains Mono",
    ];

    private readonly SettingsService _settings = new();
    private bool _suppressTextChanged;
    private int _zoomPercent = 100;
    private double _baseFontSize = 14.0;

    private readonly string? _initialFilePath;

    // ---------------------------------------------------------------
    // Tab management
    // ---------------------------------------------------------------

    private TabSession? ActiveSession =>
        TabStrip.SelectedItem is TabViewItem tvi &&
        tvi.Tag is TabSession s ? s : null;

    /// <summary>
    /// Creates a new MainWindow, optionally opening the file at <paramref name="initialFilePath"/>.
    /// </summary>
    public MainWindow(string? initialFilePath = null)
    {
        _initialFilePath = initialFilePath;
        InitializeComponent();

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        SetWindowIcon();
        RestoreSettings();
        AppWindow.Closing += AppWindow_Closing;

        _ = InitialLoadAsync();
    }

    #region Window Setup

    private void SetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.png");
            if (File.Exists(iconPath))
                AppWindow.SetIcon(iconPath);
        }
        catch { }
    }

    private void RestoreSettings()
    {
        MenuWordWrap.IsChecked = _settings.WordWrap;
        MenuStatusBar.IsChecked = _settings.StatusBarVisible;
        StatusBarBorder.Visibility = _settings.StatusBarVisible
            ? Visibility.Visible : Visibility.Collapsed;

        _baseFontSize = _settings.FontSize;
        _zoomPercent = _settings.ZoomPercent;
        ApplyFontToEditor();
        ApplyZoom();
    }

    private async Task InitialLoadAsync()
    {
        var tabs = _settings.SessionTabs;
        var activeIdx = _settings.LastActiveTabIndex;

        if (tabs.Count > 0)
        {
            // Restore persisted window size
            ResizeWindow((int)_settings.WindowWidth, (int)_settings.WindowHeight);

            foreach (var data in tabs)
            {
                var session = new TabSession { FilePath = data.FilePath };

                if (data.FilePath is not null && File.Exists(data.FilePath))
                {
                    // Reload file from disk…
                    var (content, state) = await FileService.ReadFileAsync(data.FilePath);
                    session.Document = state;

                    if (data.IsModified)
                    {
                        // …but show the in-progress unsaved edits, not the on-disk version
                        session.Content = data.Content;
                        session.SavedContent = content;
                    }
                    else
                    {
                        session.Content = content;
                        session.SavedContent = content;
                    }
                }
                else
                {
                    // Untitled or missing file — restore content as-is
                    session.Content = data.Content;
                    session.SavedContent = data.IsModified ? string.Empty : data.Content;
                    session.Document = BuildDocumentState(data);
                }

                session.CursorPosition = data.CursorPosition;
                AttachTab(session);
            }

            // Select the previously active tab; SelectionChanged fires SwitchToTab
            // which loads content and restores the cursor position.
            var clampedIdx = Math.Clamp(activeIdx, 0, TabStrip.TabItems.Count - 1);
            if (TabStrip.SelectedIndex == clampedIdx)
            {
                // Already on the right index (e.g. single tab) — force the switch manually
                // because SelectionChanged won't fire if the index didn't change.
                if (TabStrip.TabItems[clampedIdx] is TabViewItem tvi)
                    SwitchToTab(tvi);
            }
            else
            {
                TabStrip.SelectedIndex = clampedIdx;
            }
        }
        else
        {
            // No previous session — open at the default 800x550
            ResizeWindow(800, 550);
            AddNewTab();
        }

        // Command-line file always opens in its own tab
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            var session = AddNewTab();
            await LoadFileIntoSessionAsync(session, _initialFilePath);
        }
    }

    private void ResizeWindow(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch { }
    }

    private static DocumentState BuildDocumentState(PersistedTabData data)
    {
        System.Text.Encoding enc;
        try { enc = System.Text.Encoding.GetEncoding(data.EncodingCodePage); }
        catch { enc = System.Text.Encoding.UTF8; }
        return new DocumentState
        {
            FilePath = data.FilePath,
            Encoding = enc,
            HasBom = data.HasBom,
            LineEnding = (LineEndingStyle)data.LineEnding,
        };
    }

    #endregion

    #region Tab Management

    private TabSession AddNewTab(string? filePath = null)
    {
        var session = CreateTab(filePath);
        TabStrip.SelectedItem = TabStrip.TabItems[^1];
        return session;
    }

    private TabSession CreateTab(string? filePath = null)
    {
        var session = new TabSession { FilePath = filePath };
        AttachTab(session);
        return session;
    }

    private void AttachTab(TabSession session)
    {
        var tvi = new TabViewItem
        {
            Header = session.TabTitle,
            Tag = session,
            IsClosable = true,
        };
        TabStrip.TabItems.Add(tvi);
    }

    private void RefreshTabHeader(TabSession session)
    {
        foreach (var item in TabStrip.TabItems.OfType<TabViewItem>())
        {
            if (item.Tag == session)
            {
                item.Header = session.TabTitle;
                break;
            }
        }
    }

    private void SwitchToTab(TabViewItem tvi)
    {
        if (tvi.Tag is not TabSession session) return;

        _suppressTextChanged = true;
        Editor.Text = session.Content;
        Editor.SelectionStart = Math.Min(session.CursorPosition, session.Content.Length);
        Editor.SelectionLength = 0;
        _suppressTextChanged = false;

        Editor.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        UpdateTitle(session);
        UpdateStatusBar(session);
        Editor.Focus(FocusState.Programmatic);
    }

    private void SaveCurrentTabState()
    {
        if (ActiveSession is not { } session) return;
        session.Content = Editor.Text;
        session.CursorPosition = Editor.SelectionStart;
    }

    private void PersistSession()
    {
        var tabData = TabStrip.TabItems
            .OfType<TabViewItem>()
            .Select(tvi => tvi.Tag is TabSession s ? new PersistedTabData
            {
                FilePath = s.FilePath,
                Content = s.Content,
                IsModified = s.IsModified,
                CursorPosition = s.CursorPosition,
                EncodingCodePage = s.Document.Encoding.CodePage,
                HasBom = s.Document.HasBom,
                LineEnding = (int)s.Document.LineEnding,
            } : null)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();

        _settings.SessionTabs = tabData;
        _settings.LastActiveTabIndex = TabStrip.SelectedIndex;
    }

    // XAML event handlers

    private void TabStrip_AddTabButtonClick(TabView sender, object args)
        => AddNewTab();

    private void TabStrip_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
        => CloseTab(args.Tab);

    private void CloseTab(TabViewItem tab)
    {
        if (tab.Tag is not TabSession session) return;

        if (TabStrip.TabItems.Count == 1)
        {
            // Last tab — reset rather than close
            _suppressTextChanged = true;
            Editor.Text = string.Empty;
            _suppressTextChanged = false;
            session.Content = string.Empty;
            session.SavedContent = string.Empty;
            session.FilePath = null;
            session.Document = new DocumentState();
            RefreshTabHeader(session);
            UpdateTitle(session);
            UpdateStatusBar(session);
            Editor.Focus(FocusState.Programmatic);
        }
        else
        {
            TabStrip.TabItems.Remove(tab);
            // Persist remaining tabs immediately so a mid-session close is not lost
            // if the app terminates unexpectedly before the next graceful shutdown.
            PersistSession();
        }
    }

    private void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Persist state leaving the old tab
        foreach (var removed in e.RemovedItems.OfType<TabViewItem>())
        {
            if (removed.Tag is TabSession old)
            {
                old.Content = Editor.Text;
                old.CursorPosition = Editor.SelectionStart;
            }
        }

        if (TabStrip.SelectedItem is TabViewItem tvi)
            SwitchToTab(tvi);
    }

    private void TabStrip_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        // Nothing extra needed; headers are updated via RefreshTabHeader
    }

    private void MenuNewTab_Click(object sender, RoutedEventArgs e)
        => AddNewTab();

    private void MenuCloseTab_Click(object sender, RoutedEventArgs e)
    {
        if (TabStrip.SelectedItem is TabViewItem tvi)
            CloseTab(tvi);
    }

    #endregion

    #region Title Bar

    private void UpdateTitle(TabSession? session = null)
    {
        session ??= ActiveSession;
        if (session is null) return;

        var title = $"{session.TabTitle} - Inklet";
        Title = title;
        AppWindow.Title = title;
    }

    #endregion

    #region File Operations

    private void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is not { } session) return;

        _suppressTextChanged = true;
        Editor.Text = string.Empty;
        _suppressTextChanged = false;

        session.Content = string.Empty;
        session.SavedContent = string.Empty;
        session.FilePath = null;
        session.Document = new DocumentState();
        session.CursorPosition = 0;

        RefreshTabHeader(session);
        UpdateTitle(session);
        UpdateStatusBar(session);
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        foreach (var ext in new[] { ".txt", ".log", ".ini", ".cfg", ".xml", ".json",
            ".csv", ".md", ".html", ".htm", ".css", ".js", ".cs", ".py",
            ".java", ".cpp", ".h", ".yaml", ".yml", "*" })
        {
            picker.FileTypeFilter.Add(ext);
        }

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        // Open in current tab if it is a clean untitled tab, else new tab
        if (ActiveSession is { } cur && cur.FilePath is null && !cur.IsModified)
        {
            await LoadFileIntoSessionAsync(cur, file.Path);
        }
        else
        {
            var session = AddNewTab();
            await LoadFileIntoSessionAsync(session, file.Path);
        }
    }

    private async void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is not null) await SaveSessionAsync(ActiveSession);
    }

    private async void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveSession is not null) await SaveAsSessionAsync(ActiveSession);
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task LoadFileIntoSessionAsync(TabSession session, string filePath)
    {
        try
        {
            var fileSize = FileService.GetFileSize(filePath);
            if (fileSize > FileService.LargeFileThreshold)
            {
                var dialog = new ContentDialog
                {
                    Title = "Large File",
                    Content = $"This file is {fileSize / (1024 * 1024):N0} MB. Loading may take a moment.",
                    PrimaryButtonText = "Open",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

            var (content, state) = await FileService.ReadFileAsync(filePath);
            session.Content = content;
            session.SavedContent = content;
            session.FilePath = filePath;
            session.Document = state;
            session.CursorPosition = 0;

            RefreshTabHeader(session);

            // Only update the editor if this session is active
            if (ActiveSession == session)
            {
                _suppressTextChanged = true;
                Editor.Text = content;
                _suppressTextChanged = false;
                UpdateTitle(session);
                UpdateStatusBar(session);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error Opening File", ex.Message);
        }
    }

    private async Task<bool> SaveSessionAsync(TabSession session)
    {
        if (session.FilePath is null) return await SaveAsSessionAsync(session);

        try
        {
            await FileService.WriteFileAsync(
                session.FilePath, session.Content,
                session.Document.Encoding, session.Document.HasBom,
                session.Document.LineEnding);

            session.SavedContent = session.Content;
            RefreshTabHeader(session);
            UpdateTitle(session);
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error Saving File", ex.Message);
            return false;
        }
    }

    private async Task<bool> SaveAsSessionAsync(TabSession session)
    {
        var picker = new FileSavePicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text Documents", s_textFileTypes);
        picker.FileTypeChoices.Add("All Files", s_allFileTypes);
        picker.SuggestedFileName = session.Document.DisplayFileName;

        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;

        try
        {
            session.FilePath = file.Path;
            session.Document = session.Document with { FilePath = file.Path };

            await FileService.WriteFileAsync(
                file.Path, session.Content,
                session.Document.Encoding, session.Document.HasBom,
                session.Document.LineEnding);

            session.SavedContent = session.Content;
            RefreshTabHeader(session);
            UpdateTitle(session);
            UpdateStatusBar(session);
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error Saving File", ex.Message);
            return false;
        }
    }

    #endregion

    #region Edit Operations

    private void MenuUndo_Click(object sender, RoutedEventArgs e) => Editor.Undo();

    private void MenuRedo_Click(object sender, RoutedEventArgs e)
    {
        // TextBox exposes Undo() but not Redo(); simulate Ctrl+Y so the
        // control's internal redo stack is consumed.
        Editor.Focus(FocusState.Programmatic);
        const byte VK_CONTROL = 0x11;
        const byte VK_Y = 0x59;
        const uint KEYEVENTF_KEYUP = 0x0002;
        keybd_event(VK_CONTROL, 0, 0, 0);
        keybd_event(VK_Y, 0, 0, 0);
        keybd_event(VK_Y, 0, KEYEVENTF_KEYUP, 0);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0);
    }

    private void MenuCut_Click(object sender, RoutedEventArgs e) => Editor.CutSelectionToClipboard();
    private void MenuCopy_Click(object sender, RoutedEventArgs e) => Editor.CopySelectionToClipboard();
    private void MenuPaste_Click(object sender, RoutedEventArgs e) => Editor.PasteFromClipboard();
    private void MenuSelectAll_Click(object sender, RoutedEventArgs e) => Editor.SelectAll();

    private void MenuDelete_Click(object sender, RoutedEventArgs e)
    {
        if (Editor.SelectionLength > 0)
        {
            var start = Editor.SelectionStart;
            var text = Editor.Text;
            Editor.Text = string.Concat(text.AsSpan(0, start), text.AsSpan(start + Editor.SelectionLength));
            Editor.SelectionStart = start;
        }
    }

    private void MenuTimeDate_Click(object sender, RoutedEventArgs e)
    {
        var timeDate = DateTime.Now.ToString("h:mm tt M/d/yyyy");
        var start = Editor.SelectionStart;
        var text = Editor.Text;
        Editor.Text = string.Concat(
            text.AsSpan(0, start),
            timeDate,
            text.AsSpan(start + Editor.SelectionLength));
        Editor.SelectionStart = start + timeDate.Length;
    }

    #endregion

    #region Find & Replace

    private void MenuFind_Click(object sender, RoutedEventArgs e) => ShowFindBar(false);
    private void MenuReplace_Click(object sender, RoutedEventArgs e) => ShowFindBar(true);
    private void MenuFindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void MenuFindPrevious_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private async void MenuGoTo_Click(object sender, RoutedEventArgs e)
    {
        var lineCount = CountLines(Editor.Text);
        var input = new TextBox { PlaceholderText = $"Line number (1-{lineCount})" };
        var dialog = new ContentDialog
        {
            Title = "Go To Line",
            Content = input,
            PrimaryButtonText = "Go To",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            int.TryParse(input.Text, out int target) && target >= 1 && target <= lineCount)
        {
            GoToLine(target);
        }
    }

    private void ShowFindBar(bool showReplace)
    {
        FindReplaceBar.Visibility = Visibility.Visible;
        ReplacePanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        if (Editor.SelectedText.Length > 0 && !Editor.SelectedText.Contains('\n'))
        {
            FindTextBox.Text = Editor.SelectedText;
        }
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void CloseFindBar_Click(object sender, RoutedEventArgs e)
    {
        FindReplaceBar.Visibility = Visibility.Collapsed;
        Editor.Focus(FocusState.Programmatic);
    }

    private void FindTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { FindNext(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            FindReplaceBar.Visibility = Visibility.Collapsed;
            Editor.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e) => FindNext();
    private void FindPrev_Click(object sender, RoutedEventArgs e) => FindPrevious();

    private void FindNext()
    {
        var text = FindTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = Editor.SelectionStart + Editor.SelectionLength;
        var idx = Editor.Text.IndexOf(text, start, cmp);
        if (idx < 0) idx = Editor.Text.IndexOf(text, 0, cmp);
        if (idx >= 0) { Editor.SelectionStart = idx; Editor.SelectionLength = text.Length; Editor.Focus(FocusState.Programmatic); }
    }

    private void FindPrevious()
    {
        var text = FindTextBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var end = Editor.SelectionStart;
        if (end <= 0) end = Editor.Text.Length;
        var idx = Editor.Text.LastIndexOf(text, end - 1, cmp);
        if (idx < 0 && Editor.Text.Length > 0) idx = Editor.Text.LastIndexOf(text, Editor.Text.Length - 1, cmp);
        if (idx >= 0) { Editor.SelectionStart = idx; Editor.SelectionLength = text.Length; Editor.Focus(FocusState.Programmatic); }
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (Editor.SelectedText.Equals(FindTextBox.Text, cmp))
        {
            var start = Editor.SelectionStart;
            var t = Editor.Text;
            Editor.Text = string.Concat(t.AsSpan(0, start), ReplaceTextBox.Text, t.AsSpan(start + Editor.SelectionLength));
            Editor.SelectionStart = start + ReplaceTextBox.Text.Length;
        }
        FindNext();
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;
        var newText = Editor.Text.Replace(FindTextBox.Text, ReplaceTextBox.Text, cmp);
        if (newText != Editor.Text) Editor.Text = newText;
    }

    private void GoToLine(int lineNumber)
    {
        var text = Editor.Text;
        int line = 1, pos = 0;
        for (int i = 0; i < text.Length && line < lineNumber; i++)
        {
            if (text[i] == '\r')
            {
                line++; if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                pos = i + 1;
            }
            else if (text[i] == '\n') { line++; pos = i + 1; }
        }
        Editor.SelectionStart = lineNumber == 1 ? 0 : pos;
        Editor.SelectionLength = 0;
        Editor.Focus(FocusState.Programmatic);
    }

    #endregion

    #region Format

    private void MenuWordWrap_Click(object sender, RoutedEventArgs e)
    {
        var wrap = MenuWordWrap.IsChecked;
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _settings.WordWrap = wrap;
    }

    private async void MenuFont_Click(object sender, RoutedEventArgs e) => await ShowFontDialogAsync();

    private async Task ShowFontDialogAsync()
    {
        var panel = new StackPanel { Spacing = 12 };

        // Font family drop-down
        var fontCombo = new ComboBox
        {
            Header = "Font",
            Width = 240,
            IsEditable = true,
        };
        foreach (var f in s_monoFonts) fontCombo.Items.Add(f);
        fontCombo.Text = _settings.FontFamily;
        panel.Children.Add(fontCombo);

        var sizeBox = new NumberBox
        {
            Header = "Size",
            Value = _baseFontSize,
            Minimum = 6,
            Maximum = 72,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        panel.Children.Add(sizeBox);

        var boldCheck = new CheckBox
        {
            Content = "Bold",
            IsChecked = Editor.FontWeight.Weight == FontWeights.Bold.Weight
        };
        panel.Children.Add(boldCheck);

        var italicCheck = new CheckBox
        {
            Content = "Italic",
            IsChecked = Editor.FontStyle == Windows.UI.Text.FontStyle.Italic
        };
        panel.Children.Add(italicCheck);

        var dialog = new ContentDialog
        {
            Title = "Font",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var chosen = (fontCombo.SelectedItem as string) ?? fontCombo.Text;
            if (!string.IsNullOrWhiteSpace(chosen))
            {
                Editor.FontFamily = new FontFamily(chosen);
                _settings.FontFamily = chosen;
            }

            _baseFontSize = sizeBox.Value;
            _settings.FontSize = _baseFontSize;
            ApplyZoom();

            var isBold = boldCheck.IsChecked == true;
            Editor.FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal;
            _settings.FontWeight = isBold ? "Bold" : "Normal";

            var isItalic = italicCheck.IsChecked == true;
            Editor.FontStyle = isItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            _settings.FontStyle = isItalic ? "Italic" : "Normal";
        }
    }

    private void ApplyFontToEditor()
    {
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        Editor.FontWeight = _settings.FontWeight == "Bold" ? FontWeights.Bold : FontWeights.Normal;
        Editor.FontStyle = _settings.FontStyle == "Italic"
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;
    }

    #endregion

    #region View

    private void MenuStatusBar_Click(object sender, RoutedEventArgs e)
    {
        var visible = MenuStatusBar.IsChecked;
        StatusBarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _settings.StatusBarVisible = visible;
    }

    private void MenuZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent + 10);
    private void MenuZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoomPercent - 10);
    private void MenuZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(100);

    private void SetZoom(int percent)
    {
        _zoomPercent = Math.Clamp(percent, 25, 500);
        _settings.ZoomPercent = _zoomPercent;
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        Editor.FontSize = _baseFontSize * _zoomPercent / 100.0;
        StatusBarZoom.Text = $"{_zoomPercent}%";
    }

    #endregion

    #region Print

    private async void MenuPageSetup_Click(object sender, RoutedEventArgs e)
        => await ShowErrorAsync("Page Setup", "Page Setup is configured through the system Print dialog.");

    private async void MenuPrint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var tempFile = Path.Combine(Path.GetTempPath(), $"Inklet_Print_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, Editor.Text);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tempFile)
            {
                Verb = "print",
                UseShellExecute = true
            });

            // Best-effort cleanup: the spooler typically holds the file for a few seconds.
            _ = Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(
                _ => { try { File.Delete(tempFile); } catch { } },
                TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Print Error", ex.Message);
        }
    }

    #endregion

    #region About

    private async void MenuAbout_Click(object sender, RoutedEventArgs e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        var buildDate = File.GetLastWriteTime(assembly.Location);

        var panel = new StackPanel { Spacing = 12 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };

        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.png");
            if (File.Exists(iconPath))
            {
                header.Children.Add(new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)),
                    Width = 64, Height = 64
                });
            }
        }
        catch { }

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock { Text = "Inklet", FontSize = 20, FontWeight = FontWeights.Bold });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"Version {version.Major}.{version.Minor}.{version.Build}",
            FontSize = 14,
            Foreground = new SolidColorBrush(Colors.Gray)
        });
        header.Children.Add(titleStack);
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock
        {
            Text = "A lightweight, modern Notepad clone for Windows.",
            TextWrapping = TextWrapping.Wrap, FontSize = 14
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Build Date: {buildDate:yyyy-MM-dd}\n" +
                   $"Runtime: {RuntimeInformation.FrameworkDescription}\n" +
                   $"Architecture: {RuntimeInformation.ProcessArchitecture}\n" +
                   $"OS: {RuntimeInformation.OSDescription}\n" +
                   $"Windows App SDK: 1.8",
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Colors.Gray),
            IsTextSelectionEnabled = true
        });
        panel.Children.Add(new TextBlock
        {
            Text = "\u00a9 2025 JAD Apps. All rights reserved.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        await new ContentDialog
        {
            Title = "About Inklet",
            Content = panel,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    #endregion

    #region Editor Events

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        if (ActiveSession is not { } session) return;

        session.Content = Editor.Text;
        RefreshTabHeader(session);
        UpdateTitle(session);
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCursorPosition();
    }

    private void Editor_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // Ctrl+Scroll adjusts zoom; let the TextBox handle plain scrolling normally.
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control))
        {
            var delta = e.GetCurrentPoint(Editor).Properties.MouseWheelDelta;
            SetZoom(_zoomPercent + (delta > 0 ? 10 : -10));
            e.Handled = true;
        }
    }

    #endregion

    #region Drag and Drop

    private void Editor_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Copy;
    }

    private async void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0 && items[0] is StorageFile file)
        {
            if (ActiveSession is { FilePath: null, IsModified: false } cur)
                await LoadFileIntoSessionAsync(cur, file.Path);
            else
            {
                var session = AddNewTab();
                await LoadFileIntoSessionAsync(session, file.Path);
            }
        }
    }

    #endregion

    #region Status Bar

    private void UpdateStatusBar(TabSession? session = null)
    {
        session ??= ActiveSession;
        if (session is null) return;
        UpdateCursorPosition();
        StatusBarEncoding.Text = session.Document.EncodingDisplayName;
        StatusBarLineEnding.Text = LineEndingDetector.GetDisplayName(session.Document.LineEnding);
        StatusBarZoom.Text = $"{_zoomPercent}%";
    }

    private void UpdateCursorPosition()
    {
        var text = Editor.Text;
        var pos = Editor.SelectionStart;
        int line = 1, col = 1;
        for (int i = 0; i < pos && i < text.Length; i++)
        {
            if (text[i] == '\r') { line++; col = 1; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') { line++; col = 1; }
            else col++;
        }
        StatusBarPosition.Text = $"Ln {line}, Col {col}";
    }

    #endregion

    #region Window Close

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveCurrentTabState();
        SaveWindowSize();
        PersistSession();
    }

    private void SaveWindowSize()
    {
        try
        {
            _settings.WindowWidth = AppWindow.Size.Width;
            _settings.WindowHeight = AppWindow.Size.Height;
        }
        catch { }
    }

    #endregion

    #region Dialogs

    private async Task ShowErrorAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    #endregion

    #region Helpers

    private void InitializeWithWindow(object picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;
        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r') { lines++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (text[i] == '\n') lines++;
        }
        return lines;
    }

    // Simulates a Win32 key event so that the TextBox processes redo internally.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, nint dwExtraInfo);

    #endregion
}