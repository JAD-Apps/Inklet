using Inklet.Models;
using Inklet.Services;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Inklet;

/// <summary>
/// Main application window implementing classic Notepad functionality with WinUI 3 styling.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settings = new();
    private DocumentState _documentState = new();
    private bool _isModified;
    private bool _suppressTextChanged;
    private int _zoomPercent = 100;
    private double _baseFontSize = 14.0;
    private string _savedContent = string.Empty;

    // Find state
    private string _lastFindText = string.Empty;
    private bool _showingReplace;

    // File opened from command line
    private readonly string? _initialFilePath;

    /// <summary>
    /// Creates a new MainWindow, optionally opening the file at <paramref name="initialFilePath"/>.
    /// </summary>
    public MainWindow(string? initialFilePath = null)
    {
        _initialFilePath = initialFilePath;
        InitializeComponent();

        // Register code pages for international encoding support
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        SetWindowIcon();
        RestoreSettings();

        // Handle window close to prompt for unsaved changes
        AppWindow.Closing += AppWindow_Closing;

        // Load initial file if provided
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            _ = LoadFileAsync(_initialFilePath);
        }
    }

    #region Window Setup

    private void SetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.png");
            if (File.Exists(iconPath))
            {
                AppWindow.SetIcon(iconPath);
            }
        }
        catch
        {
            // Icon is cosmetic — do not fail startup
        }
    }

    private void RestoreSettings()
    {
        // Word wrap
        MenuWordWrap.IsChecked = _settings.WordWrap;
        Editor.TextWrapping = _settings.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;

        // Status bar
        MenuStatusBar.IsChecked = _settings.StatusBarVisible;
        StatusBarBorder.Visibility = _settings.StatusBarVisible ? Visibility.Visible : Visibility.Collapsed;

        // Font
        _baseFontSize = _settings.FontSize;
        Editor.FontFamily = new FontFamily(_settings.FontFamily);
        Editor.FontSize = _baseFontSize;
        Editor.FontWeight = _settings.FontWeight == "Bold" ? FontWeights.Bold : FontWeights.Normal;
        Editor.FontStyle = _settings.FontStyle == "Italic"
            ? Windows.UI.Text.FontStyle.Italic
            : Windows.UI.Text.FontStyle.Normal;

        // Zoom
        _zoomPercent = _settings.ZoomPercent;
        ApplyZoom();

        // Window size
        try
        {
            AppWindow.Resize(new SizeInt32(
                (int)_settings.WindowWidth,
                (int)_settings.WindowHeight));
        }
        catch
        {
            // Fallback if resize fails
        }
    }

    #endregion

    #region Title Bar

    private void UpdateTitle()
    {
        var modified = _isModified ? "*" : "";
        Title = $"{modified}{_documentState.DisplayFileName} - Inklet";
        AppWindow.Title = Title;
    }

    #endregion

    #region File Operations

    private async void MenuNew_Click(object sender, RoutedEventArgs e)
    {
        if (!await PromptSaveIfModifiedAsync()) return;

        _suppressTextChanged = true;
        Editor.Text = string.Empty;
        _suppressTextChanged = false;
        _savedContent = string.Empty;
        _isModified = false;
        _documentState = new DocumentState();
        UpdateTitle();
        UpdateStatusBar();
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (!await PromptSaveIfModifiedAsync()) return;

        var picker = new FileOpenPicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".log");
        picker.FileTypeFilter.Add(".ini");
        picker.FileTypeFilter.Add(".cfg");
        picker.FileTypeFilter.Add(".xml");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".html");
        picker.FileTypeFilter.Add(".htm");
        picker.FileTypeFilter.Add(".css");
        picker.FileTypeFilter.Add(".js");
        picker.FileTypeFilter.Add(".cs");
        picker.FileTypeFilter.Add(".py");
        picker.FileTypeFilter.Add(".java");
        picker.FileTypeFilter.Add(".cpp");
        picker.FileTypeFilter.Add(".h");
        picker.FileTypeFilter.Add(".yaml");
        picker.FileTypeFilter.Add(".yml");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            await LoadFileAsync(file.Path);
        }
    }

    private async void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsync();
    }

    private async void MenuSaveAs_Click(object sender, RoutedEventArgs e)
    {
        await SaveAsAsync();
    }

    private void MenuExit_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task LoadFileAsync(string filePath)
    {
        try
        {
            var fileSize = FileService.GetFileSize(filePath);
            if (fileSize > FileService.LargeFileThreshold)
            {
                // Warn for large files but still load
                var dialog = new ContentDialog
                {
                    Title = "Large File",
                    Content = $"This file is {fileSize / (1024 * 1024):N0} MB. Loading may take a moment.",
                    PrimaryButtonText = "Open",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot
                };

                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return;
            }

            var (content, state) = await FileService.ReadFileAsync(filePath);

            _suppressTextChanged = true;
            Editor.Text = content;
            _suppressTextChanged = false;
            _savedContent = content;
            _isModified = false;
            _documentState = state;
            UpdateTitle();
            UpdateStatusBar();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error Opening File", ex.Message);
        }
    }

    private async Task<bool> SaveAsync()
    {
        if (_documentState.FilePath is null)
        {
            return await SaveAsAsync();
        }

        try
        {
            await FileService.WriteFileAsync(
                _documentState.FilePath,
                Editor.Text,
                _documentState.Encoding,
                _documentState.HasBom,
                _documentState.LineEnding);

            _savedContent = Editor.Text;
            _isModified = false;
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error Saving File", ex.Message);
            return false;
        }
    }

    private async Task<bool> SaveAsAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Text Documents", new[] { ".txt" });
        picker.FileTypeChoices.Add("All Files", new[] { "." });
        picker.SuggestedFileName = _documentState.DisplayFileName;

        var file = await picker.PickSaveFileAsync();
        if (file is null) return false;

        try
        {
            // When saving as a new file, keep current encoding settings
            _documentState = _documentState with { FilePath = file.Path };

            await FileService.WriteFileAsync(
                file.Path,
                Editor.Text,
                _documentState.Encoding,
                _documentState.HasBom,
                _documentState.LineEnding);

            _savedContent = Editor.Text;
            _isModified = false;
            UpdateTitle();
            UpdateStatusBar();
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

    private void MenuUndo_Click(object sender, RoutedEventArgs e)
    {
        // WinUI TextBox has built-in undo support
        Editor.Undo();
    }

    private void MenuCut_Click(object sender, RoutedEventArgs e)
    {
        Editor.CutSelectionToClipboard();
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        Editor.CopySelectionToClipboard();
    }

    private void MenuPaste_Click(object sender, RoutedEventArgs e)
    {
        Editor.PasteFromClipboard();
    }

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

    private void MenuSelectAll_Click(object sender, RoutedEventArgs e)
    {
        Editor.SelectAll();
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

    private void MenuFind_Click(object sender, RoutedEventArgs e)
    {
        ShowFindBar(showReplace: false);
    }

    private void MenuReplace_Click(object sender, RoutedEventArgs e)
    {
        ShowFindBar(showReplace: true);
    }

    private void MenuFindNext_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void MenuFindPrevious_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private async void MenuGoTo_Click(object sender, RoutedEventArgs e)
    {
        var lineCount = CountLines(Editor.Text);
        var input = new TextBox
        {
            PlaceholderText = $"Line number (1-{lineCount})"
        };

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
            int.TryParse(input.Text, out int targetLine) &&
            targetLine >= 1 && targetLine <= lineCount)
        {
            GoToLine(targetLine);
        }
    }

    private void ShowFindBar(bool showReplace)
    {
        _showingReplace = showReplace;
        FindReplaceBar.Visibility = Visibility.Visible;
        ReplacePanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;

        // Pre-fill with selection
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
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            FindNext();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            FindReplaceBar.Visibility = Visibility.Collapsed;
            Editor.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void FindNext_Click(object sender, RoutedEventArgs e)
    {
        FindNext();
    }

    private void FindPrev_Click(object sender, RoutedEventArgs e)
    {
        FindPrevious();
    }

    private void FindNext()
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        _lastFindText = searchText;
        var comparison = FindMatchCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var startPos = Editor.SelectionStart + Editor.SelectionLength;
        var index = Editor.Text.IndexOf(searchText, startPos, comparison);

        // Wrap around to beginning
        if (index < 0)
        {
            index = Editor.Text.IndexOf(searchText, 0, comparison);
        }

        if (index >= 0)
        {
            Editor.SelectionStart = index;
            Editor.SelectionLength = searchText.Length;
            Editor.Focus(FocusState.Programmatic);
        }
    }

    private void FindPrevious()
    {
        var searchText = FindTextBox.Text;
        if (string.IsNullOrEmpty(searchText)) return;

        _lastFindText = searchText;
        var comparison = FindMatchCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var endPos = Editor.SelectionStart;
        if (endPos <= 0) endPos = Editor.Text.Length;

        var index = Editor.Text.LastIndexOf(searchText, endPos - 1, comparison);

        // Wrap around to end
        if (index < 0)
        {
            index = Editor.Text.LastIndexOf(searchText, Editor.Text.Length - 1, comparison);
        }

        if (index >= 0)
        {
            Editor.SelectionStart = index;
            Editor.SelectionLength = searchText.Length;
            Editor.Focus(FocusState.Programmatic);
        }
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return;

        var comparison = FindMatchCase.IsChecked == true
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        // If current selection matches the find text, replace it
        if (Editor.SelectedText.Equals(FindTextBox.Text, comparison))
        {
            var start = Editor.SelectionStart;
            var text = Editor.Text;
            Editor.Text = string.Concat(
                text.AsSpan(0, start),
                ReplaceTextBox.Text,
                text.AsSpan(start + Editor.SelectionLength));
            Editor.SelectionStart = start + ReplaceTextBox.Text.Length;
        }

        // Find next occurrence
        FindNext();
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return;

        var comparison = FindMatchCase.IsChecked == true
            ? StringComparison.CurrentCulture
            : StringComparison.CurrentCultureIgnoreCase;

        var newText = Editor.Text.Replace(FindTextBox.Text, ReplaceTextBox.Text, comparison);
        if (newText != Editor.Text)
        {
            Editor.Text = newText;
        }
    }

    private void GoToLine(int lineNumber)
    {
        var text = Editor.Text;
        int currentLine = 1;
        int position = 0;

        for (int i = 0; i < text.Length && currentLine < lineNumber; i++)
        {
            if (text[i] == '\r')
            {
                currentLine++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++; // skip \n in \r\n
                }
                position = i + 1;
            }
            else if (text[i] == '\n')
            {
                currentLine++;
                position = i + 1;
            }
        }

        if (currentLine == lineNumber || lineNumber == 1)
        {
            Editor.SelectionStart = lineNumber == 1 ? 0 : position;
            Editor.SelectionLength = 0;
            Editor.Focus(FocusState.Programmatic);
        }
    }

    #endregion

    #region Format

    private void MenuWordWrap_Click(object sender, RoutedEventArgs e)
    {
        var wrap = MenuWordWrap.IsChecked;
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _settings.WordWrap = wrap;
    }

    private async void MenuFont_Click(object sender, RoutedEventArgs e)
    {
        await ShowFontDialogAsync();
    }

    private async Task ShowFontDialogAsync()
    {
        var panel = new StackPanel { Spacing = 12 };

        var fontFamilyBox = new TextBox
        {
            Header = "Font",
            Text = Editor.FontFamily.Source,
            PlaceholderText = "Font family name"
        };
        panel.Children.Add(fontFamilyBox);

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
            if (!string.IsNullOrWhiteSpace(fontFamilyBox.Text))
            {
                Editor.FontFamily = new FontFamily(fontFamilyBox.Text);
                _settings.FontFamily = fontFamilyBox.Text;
            }

            _baseFontSize = sizeBox.Value;
            _settings.FontSize = _baseFontSize;
            ApplyZoom();

            var isBold = boldCheck.IsChecked == true;
            Editor.FontWeight = isBold ? FontWeights.Bold : FontWeights.Normal;
            _settings.FontWeight = isBold ? "Bold" : "Normal";

            var isItalic = italicCheck.IsChecked == true;
            Editor.FontStyle = isItalic
                ? Windows.UI.Text.FontStyle.Italic
                : Windows.UI.Text.FontStyle.Normal;
            _settings.FontStyle = isItalic ? "Italic" : "Normal";
        }
    }

    #endregion

    #region View

    private void MenuStatusBar_Click(object sender, RoutedEventArgs e)
    {
        var visible = MenuStatusBar.IsChecked;
        StatusBarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _settings.StatusBarVisible = visible;
    }

    private void MenuZoomIn_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomPercent + 10);
    }

    private void MenuZoomOut_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(_zoomPercent - 10);
    }

    private void MenuZoomReset_Click(object sender, RoutedEventArgs e)
    {
        SetZoom(100);
    }

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
    {
        await ShowErrorAsync("Page Setup", "Page Setup is configured through the system Print dialog.");
    }

    private async void MenuPrint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // Use a temporary file and shell print for simplicity and reliability
            var tempFile = Path.Combine(Path.GetTempPath(), $"Inklet_Print_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, Editor.Text);

            var processInfo = new System.Diagnostics.ProcessStartInfo(tempFile)
            {
                Verb = "print",
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(processInfo);
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
        var runtimeVersion = RuntimeInformation.FrameworkDescription;
        var osVersion = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.ProcessArchitecture;

        var aboutPanel = new StackPanel { Spacing = 12 };

        // App icon and title header
        var headerPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.png");
            if (File.Exists(iconPath))
            {
                var icon = new Microsoft.UI.Xaml.Controls.Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)),
                    Width = 64,
                    Height = 64
                };
                headerPanel.Children.Add(icon);
            }
        }
        catch { }

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleStack.Children.Add(new TextBlock
        {
            Text = "Inklet",
            FontSize = 20,
            FontWeight = FontWeights.Bold
        });
        titleStack.Children.Add(new TextBlock
        {
            Text = $"Version {version.Major}.{version.Minor}.{version.Build}",
            FontSize = 14,
            Foreground = new SolidColorBrush(Colors.Gray)
        });
        headerPanel.Children.Add(titleStack);
        aboutPanel.Children.Add(headerPanel);

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "A lightweight, modern Notepad clone for Windows.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14
        });

        var infoText = $"Build Date: {buildDate:yyyy-MM-dd}\n" +
                       $"Runtime: {runtimeVersion}\n" +
                       $"Architecture: {arch}\n" +
                       $"OS: {osVersion}\n" +
                       $"Windows App SDK: 1.8";

        aboutPanel.Children.Add(new TextBlock
        {
            Text = infoText,
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            Foreground = new SolidColorBrush(Colors.Gray),
            IsTextSelectionEnabled = true
        });

        aboutPanel.Children.Add(new TextBlock
        {
            Text = "\u00a9 2025 JAD Apps. All rights reserved.",
            FontSize = 12,
            Foreground = new SolidColorBrush(Colors.Gray)
        });

        var dialog = new ContentDialog
        {
            Title = "About Inklet",
            Content = aboutPanel,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };

        await dialog.ShowAsync();
    }

    #endregion

    #region Editor Events

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;

        _isModified = Editor.Text != _savedContent;
        UpdateTitle();
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCursorPosition();
    }

    #endregion

    #region Drag and Drop

    private void Editor_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void Editor_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;

        var items = await e.DataView.GetStorageItemsAsync();
        if (items.Count > 0 && items[0] is StorageFile file)
        {
            if (!await PromptSaveIfModifiedAsync()) return;
            await LoadFileAsync(file.Path);
        }
    }

    #endregion

    #region Status Bar

    private void UpdateStatusBar()
    {
        UpdateCursorPosition();
        StatusBarEncoding.Text = _documentState.EncodingDisplayName;
        StatusBarLineEnding.Text = LineEndingDetector.GetDisplayName(_documentState.LineEnding);
        StatusBarZoom.Text = $"{_zoomPercent}%";
    }

    private void UpdateCursorPosition()
    {
        var text = Editor.Text;
        var selectionStart = Editor.SelectionStart;

        int line = 1;
        int col = 1;

        for (int i = 0; i < selectionStart && i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                line++;
                col = 1;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++; // skip \n in \r\n
                }
            }
            else if (text[i] == '\n')
            {
                line++;
                col = 1;
            }
            else
            {
                col++;
            }
        }

        StatusBarPosition.Text = $"Ln {line}, Col {col}";
    }

    #endregion

    #region Window Close

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isModified)
        {
            args.Cancel = true;

            if (await PromptSaveIfModifiedAsync())
            {
                // Save window size before closing
                SaveWindowSize();
                Close();
            }
        }
        else
        {
            SaveWindowSize();
        }
    }

    private void SaveWindowSize()
    {
        try
        {
            _settings.WindowWidth = AppWindow.Size.Width;
            _settings.WindowHeight = AppWindow.Size.Height;
        }
        catch
        {
            // Ignore failures saving window size
        }
    }

    #endregion

    #region Dialogs

    /// <summary>
    /// Prompts user to save if document is modified. Returns true if safe to proceed, false to cancel.
    /// </summary>
    private async Task<bool> PromptSaveIfModifiedAsync()
    {
        if (!_isModified) return true;

        var dialog = new ContentDialog
        {
            Title = "Unsaved Changes",
            Content = $"Do you want to save changes to {_documentState.DisplayFileName}?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        var result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true, // Don't save, proceed
            _ => false // Cancel
        };
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = Content.XamlRoot
        };
        await dialog.ShowAsync();
    }

    #endregion

    #region Helpers

    private void InitializeWithWindow(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow(picker, hwnd);
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void InitializeWithWindow(object obj, IntPtr hwnd);

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 1;

        int lines = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                lines++;
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (text[i] == '\n')
            {
                lines++;
            }
        }
        return lines;
    }

    #endregion
}
