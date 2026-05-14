using Inklet.Models;
using Inklet.Services;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Foundation.Collections;

namespace Inklet;

/// <summary>
/// Main application window — hosts a multi-tab editor with session persistence.
/// </summary>
public sealed partial class MainWindow : Window
{
    private static readonly string[] s_textFileTypes = [".txt"];

    // FileSavePicker accepts "*" as the documented "All Files" wildcard. The previous
    // value "." was non-standard and worked only on some Windows builds.
    private static readonly string[] s_allFileTypes = ["*"];

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

    private DispatcherTimer? _tabScrollTimer;
    private int _tabScrollDirection;

    // Cached on first lookup. The TabView's internal ScrollViewer doesn't change for
    // the life of the window, so walking the visual tree on every scroll event was
    // wasted work (especially for TabScrollTimer_RepeatTick at 50 ms cadence).
    private ScrollViewer? _cachedTabsScrollViewer;

    // Autosave: every 30 s, if any tab is dirty, snapshot the session to disk so a
    // power-loss / process-kill in the middle of a long editing session doesn't lose
    // unsaved Untitled-tab content. Coalesced — skipped if a save is already in flight.
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromSeconds(30);
    private DispatcherTimer? _autosaveTimer;
    private int _autosaveInFlight; // 0 = idle, 1 = saving (Interlocked-managed)

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
        SetupCustomTitleBar();
        RestoreSettings();
        AppWindow.Closing += AppWindow_Closing;
        StartAutosaveTimer();

        _ = InitialLoadAsync();
    }

    private void StartAutosaveTimer()
    {
        _autosaveTimer = new DispatcherTimer { Interval = AutosaveInterval };
        _autosaveTimer.Tick += AutosaveTick;
        _autosaveTimer.Start();
    }

    private async void AutosaveTick(object? sender, object e)
    {
        // Coalesce: if a save is already running we skip this tick rather than queueing
        // a second concurrent write.
        if (Interlocked.CompareExchange(ref _autosaveInFlight, 1, 0) != 0) return;

        try
        {
            // Only persist if at least one tab is dirty — autosaving an unchanged
            // session every 30 s would needlessly thrash the disk.
            bool anyDirty = false;
            foreach (var tvi in TabStrip.TabItems.OfType<TabViewItem>())
            {
                if (tvi.Tag is TabSession s && s.IsModified) { anyDirty = true; break; }
            }
            if (!anyDirty) return;

            await PersistSessionAsync();
        }
        catch
        {
            // Autosave is best-effort; the next tick or the close handler will retry.
        }
        finally
        {
            Interlocked.Exchange(ref _autosaveInFlight, 0);
        }
    }

    #region Window Setup

    private void SetWindowIcon()
    {
        try
        {
            var icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.ico");
            if (File.Exists(icoPath))
            {
                AppWindow.SetIcon(icoPath);
                return;
            }
            var pngPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Inklet.png");
            if (File.Exists(pngPath))
                AppWindow.SetIcon(pngPath);
        }
        catch (Exception ex) { Debug.WriteLine($"SetWindowIcon failed: {ex.Message}"); }
    }

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarGrid);

        // Make the OS caption buttons blend into the Mica backdrop.
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = Colors.Transparent;
    }

    private void TitleBar_Loaded(object _, RoutedEventArgs _e)
    {
        UpdateCaptionButtonColumn();
        UpdateTabScrollButtons();

        // Wire the tab strip's internal ScrollViewer so the arrows update
        // when the user scrolls the tab strip directly (not just via our buttons).
        var sv = FindTabScrollViewer();
        if (sv is not null)
            sv.ViewChanged += (_, _) => UpdateTabScrollButtons();

        ConfigureTabViewVisualTree();
        WireScrollButtonPointerEvents();
    }

    private void TitleBar_SizeChanged(object _, SizeChangedEventArgs _e)
    {
        UpdateCaptionButtonColumn();
        InvalidateTabLayout();
    }

    /// <summary>
    /// Keeps the caption-button placeholder column the same width as the OS-drawn buttons
    /// so that our interactive controls never overlap them.
    /// </summary>
    private void UpdateCaptionButtonColumn()
    {
        var rightInset = AppWindow.TitleBar.RightInset;
        if (rightInset > 0)
            CaptionButtonColumn.Width = new GridLength(rightInset);
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
            // Restore persisted window state
            if (_settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter overlapped)
                overlapped.Maximize();
            else
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
                if (session.FilePath is not null && File.Exists(session.FilePath))
                    AttachFileWatcher(session);
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

        // Command-line file: reuse the active tab if it is a clean untitled one,
        // otherwise open in a new tab. Without this, the common "double-click .txt to
        // open" flow would leave the user staring at [Untitled] [file.txt] instead of
        // just [file.txt].
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            TabSession session;
            if (ActiveSession is { FilePath: null, IsModified: false } cur)
                session = cur;
            else
                session = AddNewTab();

            await LoadFileIntoSessionAsync(session, _initialFilePath);
        }
    }

    private void ResizeWindow(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch (Exception ex) { Debug.WriteLine($"ResizeWindow({width},{height}) failed: {ex.Message}"); }
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
        ScrollToEndOfTabStrip();
        return session;
    }

    /// <summary>
    /// Scrolls the tab strip all the way to the right so the newest tab is fully visible.
    /// Deferred to run after layout has been updated with the new tab's width.
    /// </summary>
    private void ScrollToEndOfTabStrip()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            TabStrip.UpdateLayout();
            var sv = FindTabScrollViewer();
            if (sv is not null && sv.ScrollableWidth > 0)
            {
                sv.ChangeView(sv.ScrollableWidth, null, null, false);
                UpdateTabScrollButtons();
            }
        });
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
        var tabData = BuildSessionSnapshot();
        _settings.SessionTabs = tabData;
        _settings.LastActiveTabIndex = TabStrip.SelectedIndex;
    }

    /// <summary>
    /// Async counterpart to <see cref="PersistSession"/> used by the window-close path.
    /// The session JSON write happens on a thread-pool thread so the UI thread is not
    /// blocked when persisting many unsaved buffers.
    /// </summary>
    private async Task PersistSessionAsync()
    {
        var tabData = BuildSessionSnapshot();
        _settings.LastActiveTabIndex = TabStrip.SelectedIndex;
        await _settings.SaveSessionTabsAsync(tabData).ConfigureAwait(false);
    }

    private List<PersistedTabData> BuildSessionSnapshot()
    {
        // Always flush the active tab's cursor position before writing — Editor_TextChanged
        // keeps session.Content live, but CursorPosition is only synced on tab-switch.
        SaveCurrentTabState();

        return TabStrip.TabItems
            .OfType<TabViewItem>()
            .Select(tvi => tvi.Tag is TabSession s ? new PersistedTabData
            {
                FilePath = s.FilePath,
                // Only persist content for untitled tabs or tabs with unsaved changes;
                // unmodified file-backed tabs will be reloaded from disk on next launch.
                Content = (s.FilePath is not null && !s.IsModified) ? string.Empty : s.Content,
                IsModified = s.IsModified,
                CursorPosition = s.CursorPosition,
                EncodingCodePage = s.Document.Encoding.CodePage,
                HasBom = s.Document.HasBom,
                LineEnding = (int)s.Document.LineEnding,
            } : null)
            .Where(d => d is not null)
            .Select(d => d!)
            .ToList();
    }

    // XAML event handlers

    private void TabStrip_AddTabButtonClick(TabView _, object _args)
        => AddNewTab();

    private async void TabStrip_TabCloseRequested(TabView _, TabViewTabCloseRequestedEventArgs args)
        => await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem tab)
    {
        if (tab.Tag is not TabSession session) return;

        // Sync the editor text into the session before checking IsModified,
        // so the dirty flag is accurate for the tab being closed.
        if (ReferenceEquals(TabStrip.SelectedItem, tab))
            SaveCurrentTabState();

        if (session.IsModified)
        {
            var result = await ShowSavePromptAsync(session);
            if (result == ContentDialogResult.Primary)
            {
                if (!await SaveSessionAsync(session))
                    return; // Save failed or was cancelled — abort close
            }
            else if (result == ContentDialogResult.None)
            {
                return; // User chose Cancel — abort close
            }
            // ContentDialogResult.Secondary = Don't Save — fall through to close
        }

        if (TabStrip.TabItems.Count == 1)
        {
            // Last tab — reset rather than close
            DetachFileWatcher(session);
            _suppressTextChanged = true;
            Editor.Text = string.Empty;
            _suppressTextChanged = false;
            session.Content = string.Empty;
            session.SavedContent = string.Empty;
            session.FilePath = null;
            session.CursorPosition = 0;
            session.Document = new DocumentState();
            RefreshTabHeader(session);
            UpdateTitle(session);
            UpdateStatusBar(session);
            PersistSession();
            Editor.Focus(FocusState.Programmatic);
        }
        else
        {
            DetachFileWatcher(session);
            TabStrip.TabItems.Remove(tab);
            InvalidateTabLayout();
            // Persist remaining tabs immediately so a mid-session close is not lost
            // if the app terminates unexpectedly before the next graceful shutdown.
            PersistSession();
        }
    }

    private void TabStrip_SelectionChanged(object _, SelectionChangedEventArgs e)
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

        UpdateTabScrollButtons();
    }

    private void TabStrip_TabItemsChanged(TabView _, IVectorChangedEventArgs _args)
    {
        InvalidateTabLayout();
    }

    /// <summary>
    /// Forces the TabView to recalculate tab widths and updates scroll buttons.
    /// Called after tab removal and window resize so equal-width tabs expand
    /// to fill the available space.
    /// </summary>
    private void InvalidateTabLayout()
    {
        UpdateTabScrollButtons();
        DispatcherQueue.TryEnqueue(() =>
        {
            TabStrip.InvalidateMeasure();
            TabStrip.UpdateLayout();
            UpdateTabScrollButtons();
        });
    }

    /// <summary>
    /// Shows/hides the scroll arrows based on whether the tab strip is overflowing.
    /// Both buttons are shown or hidden as a pair to prevent layout flickering.
    /// Individual buttons are enabled/disabled based on the current scroll position.
    /// </summary>
    private void UpdateTabScrollButtons()
    {
        var sv = FindTabScrollViewer();
        if (sv is null)
        {
            ScrollTabsLeftButton.Visibility = Visibility.Collapsed;
            ScrollTabsRightButton.Visibility = Visibility.Collapsed;
            return;
        }

        bool overflows = sv.ScrollableWidth > 0;
        var vis = overflows ? Visibility.Visible : Visibility.Collapsed;
        ScrollTabsLeftButton.Visibility = vis;
        ScrollTabsRightButton.Visibility = vis;

        ScrollTabsLeftButton.IsEnabled = sv.HorizontalOffset > 0;
        ScrollTabsRightButton.IsEnabled = sv.HorizontalOffset < sv.ScrollableWidth - 1;
    }

    private ScrollViewer? FindTabScrollViewer()
    {
        // Cached after the first successful lookup. The TabView template doesn't get
        // re-applied during the window's lifetime, so the ScrollViewer reference is
        // stable. Tab-scroll repeat fires at 50 ms cadence and previously walked the
        // entire visual tree on every tick.
        return _cachedTabsScrollViewer ??= FindDescendant<ScrollViewer>(TabStrip);
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            var result = FindDescendant<T>(child);
            if (result is not null) return result;
        }
        return null;
    }

    private static FrameworkElement? FindDescendantByName(DependencyObject parent, string name)
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            var result = FindDescendantByName(child, name);
            if (result is not null) return result;
        }
        return null;
    }

    /// <summary>
    /// Hides the TabView's built-in scroll buttons, collapses the unused content area,
    /// and adds smooth reposition transitions for tab items.
    /// </summary>
    private void ConfigureTabViewVisualTree()
    {
        // Hide the TabView's built-in scroll buttons (we provide our own).
        var scrollDecrease = FindDescendantByName(TabStrip, "ScrollDecreaseButton");
        var scrollIncrease = FindDescendantByName(TabStrip, "ScrollIncreaseButton");
        if (scrollDecrease is not null) scrollDecrease.Visibility = Visibility.Collapsed;
        if (scrollIncrease is not null) scrollIncrease.Visibility = Visibility.Collapsed;

        // Collapse the content area rows and stretch the tab strip row so it fills
        // the entire TabView height, eliminating any gap below the tabs.
        if (VisualTreeHelper.GetChildrenCount(TabStrip) > 0 &&
            VisualTreeHelper.GetChild(TabStrip, 0) is Grid rootGrid)
        {
            if (rootGrid.RowDefinitions.Count > 0)
                rootGrid.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            for (int i = 1; i < rootGrid.RowDefinitions.Count; i++)
                rootGrid.RowDefinitions[i].Height = new GridLength(0);
        }

        // Hide the bottom separator line.
        var separator = FindDescendantByName(TabStrip, "TabSeparator");
        if (separator is not null) separator.Visibility = Visibility.Collapsed;

        // Add smooth reposition animation so tabs slide when added/removed.
        var itemsPanel = FindDescendant<ItemsStackPanel>(TabStrip);
        if (itemsPanel is not null)
        {
            itemsPanel.ChildrenTransitions ??= new TransitionCollection();
            itemsPanel.ChildrenTransitions.Add(new RepositionThemeTransition());
        }
    }

    /// <summary>
    /// Wires PointerPressed/Released events on the scroll buttons so that a single
    /// click scrolls ~5 tabs and holding the button scrolls continuously.
    /// </summary>
    private void WireScrollButtonPointerEvents()
    {
        ScrollTabsLeftButton.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ScrollTabsLeft_PointerPressed), true);
        ScrollTabsLeftButton.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);
        ScrollTabsLeftButton.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);
        ScrollTabsLeftButton.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);

        ScrollTabsRightButton.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(ScrollTabsRight_PointerPressed), true);
        ScrollTabsRightButton.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);
        ScrollTabsRightButton.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);
        ScrollTabsRightButton.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(ScrollTabs_PointerReleased), true);
    }

    private void ScrollTabsLeft_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ScrollTabsLeftButton.IsEnabled) return;
        ScrollTabStrip(-500);
        StartTabScrollRepeat(-1);
    }

    private void ScrollTabsRight_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ScrollTabsRightButton.IsEnabled) return;
        ScrollTabStrip(500);
        StartTabScrollRepeat(1);
    }

    private void ScrollTabs_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        StopTabScrollRepeat();
    }

    private void StartTabScrollRepeat(int direction)
    {
        StopTabScrollRepeat();
        _tabScrollDirection = direction;

        // Initial delay before continuous scrolling begins.
        _tabScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _tabScrollTimer.Tick += TabScrollTimer_InitialTick;
        _tabScrollTimer.Start();
    }

    private void TabScrollTimer_InitialTick(object? sender, object e)
    {
        if (_tabScrollTimer is null) return;
        _tabScrollTimer.Stop();
        _tabScrollTimer.Tick -= TabScrollTimer_InitialTick;

        // Switch to fast repeat interval for smooth continuous scrolling.
        _tabScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
        _tabScrollTimer.Tick += TabScrollTimer_RepeatTick;
        _tabScrollTimer.Start();
    }

    private void TabScrollTimer_RepeatTick(object? sender, object e)
    {
        ScrollTabStrip(_tabScrollDirection * 80);

        // Stop repeating once we've reached the scroll boundary.
        var sv = FindTabScrollViewer();
        if (sv is null) { StopTabScrollRepeat(); return; }
        bool atEnd = _tabScrollDirection < 0
            ? sv.HorizontalOffset <= 0
            : sv.HorizontalOffset >= sv.ScrollableWidth - 1;
        if (atEnd) StopTabScrollRepeat();
    }

    private void StopTabScrollRepeat()
    {
        if (_tabScrollTimer is not null)
        {
            _tabScrollTimer.Stop();
            _tabScrollTimer = null;
        }
    }

    private void ScrollTabStrip(double offsetDelta)
    {
        var sv = FindTabScrollViewer();
        if (sv is null) return;
        var newOffset = Math.Clamp(sv.HorizontalOffset + offsetDelta, 0, sv.ScrollableWidth);
        sv.ChangeView(newOffset, null, null, false);
        UpdateTabScrollButtons();
    }

    /// <summary>
    /// Prevents double-clicking on title bar buttons from maximizing/restoring the window.
    /// </summary>
    private void TitleBarButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
    }

    private void MenuNewTab_Click(object _, RoutedEventArgs _e)
        => AddNewTab();

    private async void MenuCloseTab_Click(object _, RoutedEventArgs _e)
    {
        if (TabStrip.SelectedItem is TabViewItem tvi)
            await CloseTabAsync(tvi);
    }

    #endregion

    #region Title Bar

    private void UpdateTitle(TabSession? session = null)
    {
        session ??= ActiveSession;
        if (session is null) return;

        // Update taskbar and snap/alt-tab display; the visual title bar shows the tab strip.
        var title = $"{session.TabTitle} - Inklet";
        AppWindow.Title = title;
    }

    #endregion

    #region File Operations

    private void MenuNew_Click(object _, RoutedEventArgs _e)
    {
        if (ActiveSession is not { } session) return;

        DetachFileWatcher(session);
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

    private async void MenuOpen_Click(object _, RoutedEventArgs _e)
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

    private async void MenuSave_Click(object _, RoutedEventArgs _e)
    {
        if (ActiveSession is not null) await SaveSessionAsync(ActiveSession);
    }

    private async void MenuSaveAs_Click(object _, RoutedEventArgs _e)
    {
        if (ActiveSession is not null) await SaveAsSessionAsync(ActiveSession);
    }

    private void MenuExit_Click(object _, RoutedEventArgs _e) => Close();

    private async Task LoadFileIntoSessionAsync(TabSession session, string filePath)
    {
        try
        {
            // Warn on binary files before attempting to load
            if (FileService.IsBinaryFile(filePath))
            {
                var dialog = new ContentDialog
                {
                    Title = "Binary File",
                    Content = $"{Path.GetFileName(filePath)} appears to be a binary file " +
                              "and will not display correctly as text.",
                    PrimaryButtonText = "Open Anyway",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }

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

            AttachFileWatcher(session);
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

    /// <summary>
    /// Attaches a <see cref="FileChangeWatcher"/> to <paramref name="session"/> for its
    /// current FilePath, disposing any previous watcher. Marshals events onto the UI
    /// thread and prompts the user to reload.
    /// </summary>
    private void AttachFileWatcher(TabSession session)
    {
        DetachFileWatcher(session);
        if (session.FilePath is null) return;

        try
        {
            session.Watcher = new FileChangeWatcher(session.FilePath, () =>
            {
                DispatcherQueue.TryEnqueue(async () => await OnExternalFileChangeAsync(session));
            });
        }
        catch
        {
            // Best-effort. A watcher failure (network drive, permissions) shouldn't
            // prevent the tab from opening.
        }
    }

    private static void DetachFileWatcher(TabSession session)
    {
        session.Watcher?.Dispose();
        session.Watcher = null;
    }

    private async Task OnExternalFileChangeAsync(TabSession session)
    {
        // The watcher catches our own writes too (we suppress those, but be defensive).
        if (session.FilePath is null) return;

        var dialog = new ContentDialog
        {
            Title = "File changed",
            Content = $"{System.IO.Path.GetFileName(session.FilePath)} was modified outside Inklet. " +
                      (session.IsModified
                          ? "Reloading will discard your unsaved changes."
                          : "Reload to see the latest version."),
            PrimaryButtonText = "Reload",
            CloseButtonText = "Keep my version",
            DefaultButton = session.IsModified ? ContentDialogButton.Close : ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await LoadFileIntoSessionAsync(session, session.FilePath);
    }

    private async Task<bool> SaveSessionAsync(TabSession session)
    {
        if (session.FilePath is null) return await SaveAsSessionAsync(session);

        try
        {
            // Tell the watcher to ignore our own write — otherwise the user gets
            // a "file changed externally" prompt every time they save.
            session.Watcher?.SuppressNextChange();

            await FileService.WriteFileAsync(
                session.FilePath, session.Content,
                session.Document.Encoding, session.Document.HasBom,
                session.Document.LineEnding);

            session.MarkSaved();
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

            session.MarkSaved();
            // Save As changes the watched path — re-attach to the new location.
            AttachFileWatcher(session);
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

    private void MenuUndo_Click(object _, RoutedEventArgs _e) => Editor.Undo();

    private void MenuRedo_Click(object _, RoutedEventArgs _e)
    {
        // TextBox exposes Undo() but not Redo(). Posting WM_KEYDOWN/KEYUP for Ctrl+Y
        // directly to the focused window keeps the input scoped to our process — the
        // previous keybd_event approach injected synthetic events into the global input
        // queue (caught by other apps, screen readers, IME, AutoHotkey, etc.) and was
        // marked SYSLIB1054 obsolete.
        Editor.Focus(FocusState.Programmatic);

        var hwnd = GetFocus();
        if (hwnd == IntPtr.Zero) return;

        const uint WM_KEYDOWN = 0x0100;
        const uint WM_KEYUP = 0x0101;
        const int VK_CONTROL = 0x11;
        const int VK_Y = 0x59;

        // Synthesise Ctrl+Y. PostMessage queues the message rather than blocking on the
        // window proc, matching how a real keystroke arrives.
        PostMessage(hwnd, WM_KEYDOWN, VK_CONTROL, 0);
        PostMessage(hwnd, WM_KEYDOWN, VK_Y, 0);
        PostMessage(hwnd, WM_KEYUP, VK_Y, 0);
        PostMessage(hwnd, WM_KEYUP, VK_CONTROL, 0);
    }

    private void MenuCut_Click(object _, RoutedEventArgs _e) => Editor.CutSelectionToClipboard();
    private void MenuCopy_Click(object _, RoutedEventArgs _e) => Editor.CopySelectionToClipboard();
    private void MenuPaste_Click(object _, RoutedEventArgs _e) => Editor.PasteFromClipboard();
    private void MenuSelectAll_Click(object _, RoutedEventArgs _e) => Editor.SelectAll();

    private void MenuDelete_Click(object _, RoutedEventArgs _e)
    {
        if (Editor.SelectionLength > 0)
        {
            var start = Editor.SelectionStart;
            var text = Editor.Text;
            Editor.Text = string.Concat(text.AsSpan(0, start), text.AsSpan(start + Editor.SelectionLength));
            Editor.SelectionStart = start;
        }
    }

    private void MenuTimeDate_Click(object _, RoutedEventArgs _e)
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

    private void MenuFind_Click(object _, RoutedEventArgs _e) => ShowFindBar(false);
    private void MenuReplace_Click(object _, RoutedEventArgs _e) => ShowFindBar(true);
    private void MenuFindNext_Click(object _, RoutedEventArgs _e) => FindNext();
    private void MenuFindPrevious_Click(object _, RoutedEventArgs _e) => FindPrevious();

    private async void MenuGoTo_Click(object _, RoutedEventArgs _e)
    {
        var lineCount = ActiveSession?.Lines.LineCount ?? 1;
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

    private void CloseFindBar_Click(object _, RoutedEventArgs _e)
    {
        FindReplaceBar.Visibility = Visibility.Collapsed;
        Editor.Focus(FocusState.Programmatic);
    }

    private void FindTextBox_KeyDown(object _, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { FindNext(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            FindReplaceBar.Visibility = Visibility.Collapsed;
            Editor.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void FindNext_Click(object _, RoutedEventArgs _e) => FindNext();
    private void FindPrev_Click(object _, RoutedEventArgs _e) => FindPrevious();

    /// <summary>
    /// Returns the editor's text via the cached snapshot on the active session.
    /// Editor.Text is a COM property whose getter materialises a fresh string on every
    /// access — we already keep a reference on the active TabSession via Editor_TextChanged,
    /// so reusing it avoids a second materialisation per find op.
    /// </summary>
    private string GetEditorText()
        => ActiveSession?.Content ?? Editor.Text;

    private void FindNext()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var haystack = GetEditorText();
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var start = Editor.SelectionStart + Editor.SelectionLength;

        var idx = haystack.IndexOf(needle, start, cmp);
        if (idx < 0) idx = haystack.IndexOf(needle, 0, cmp);
        if (idx >= 0)
        {
            Editor.SelectionStart = idx;
            Editor.SelectionLength = needle.Length;
            Editor.Focus(FocusState.Programmatic);
        }
    }

    private void FindPrevious()
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        var haystack = GetEditorText();
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var end = Editor.SelectionStart;
        if (end <= 0) end = haystack.Length;

        var idx = haystack.LastIndexOf(needle, end - 1, cmp);
        if (idx < 0 && haystack.Length > 0)
            idx = haystack.LastIndexOf(needle, haystack.Length - 1, cmp);
        if (idx >= 0)
        {
            Editor.SelectionStart = idx;
            Editor.SelectionLength = needle.Length;
            Editor.Focus(FocusState.Programmatic);
        }
    }

    private void Replace_Click(object _, RoutedEventArgs _e)
    {
        if (string.IsNullOrEmpty(FindTextBox.Text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (Editor.SelectedText.Equals(FindTextBox.Text, cmp))
        {
            var start = Editor.SelectionStart;
            var t = GetEditorText();
            Editor.Text = string.Concat(t.AsSpan(0, start), ReplaceTextBox.Text, t.AsSpan(start + Editor.SelectionLength));
            Editor.SelectionStart = start + ReplaceTextBox.Text.Length;
        }
        FindNext();
    }

    private void ReplaceAll_Click(object _, RoutedEventArgs _e)
    {
        var needle = FindTextBox.Text;
        if (string.IsNullOrEmpty(needle)) return;

        // Use Ordinal comparisons to match FindNext/FindPrevious — the previous
        // CurrentCulture choice meant "İ" matched differently in find vs replace-all.
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var haystack = GetEditorText();
        var newText = haystack.Replace(needle, ReplaceTextBox.Text, cmp);
        if (!ReferenceEquals(newText, haystack)) Editor.Text = newText;
    }

    private void GoToLine(int lineNumber)
    {
        if (ActiveSession is not { } session) return;

        Editor.SelectionStart = session.Lines.GetOffset(lineNumber);
        Editor.SelectionLength = 0;
        Editor.Focus(FocusState.Programmatic);
    }

    #endregion

    #region Format

    private void MenuWordWrap_Click(object _, RoutedEventArgs _e)
    {
        var wrap = MenuWordWrap.IsChecked;
        Editor.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        _settings.WordWrap = wrap;
    }

    private async void MenuFont_Click(object _, RoutedEventArgs _e) => await ShowFontDialogAsync();

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

    private void MenuStatusBar_Click(object _, RoutedEventArgs _e)
    {
        var visible = MenuStatusBar.IsChecked;
        StatusBarBorder.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _settings.StatusBarVisible = visible;
    }

    private void MenuZoomIn_Click(object _, RoutedEventArgs _e) => SetZoom(_zoomPercent + 10);
    private void MenuZoomOut_Click(object _, RoutedEventArgs _e) => SetZoom(_zoomPercent - 10);
    private void MenuZoomReset_Click(object _, RoutedEventArgs _e) => SetZoom(100);

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

    private async void MenuPageSetup_Click(object _, RoutedEventArgs _e)
    {
        var setup = LoadPrintPageSettings();

        // ---- Build dialog content ----
        var marginTop = new TextBox { Text = MarginToString(setup.Margins.Top), Header = "Top (inches)" };
        var marginBottom = new TextBox { Text = MarginToString(setup.Margins.Bottom), Header = "Bottom (inches)" };
        var marginLeft = new TextBox { Text = MarginToString(setup.Margins.Left), Header = "Left (inches)" };
        var marginRight = new TextBox { Text = MarginToString(setup.Margins.Right), Header = "Right (inches)" };
        var headerBox = new TextBox
        {
            Text = setup.Header,
            Header = "Header",
            PlaceholderText = "e.g. &f\t\t&d  —  tokens: &f filename, &d date, &t time, &p page, &P total"
        };
        var footerBox = new TextBox
        {
            Text = setup.Footer,
            Header = "Footer",
            PlaceholderText = "e.g. Page &p of &P"
        };

        var marginRow = new Microsoft.UI.Xaml.Controls.StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        marginRow.Children.Add(WrapWithWidth(marginLeft, 130));
        marginRow.Children.Add(WrapWithWidth(marginRight, 130));
        marginRow.Children.Add(WrapWithWidth(marginTop, 130));
        marginRow.Children.Add(WrapWithWidth(marginBottom, 130));

        var panel = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 12, MinWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "Margins", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        panel.Children.Add(marginRow);
        panel.Children.Add(new TextBlock { Text = "Header && Footer", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });
        panel.Children.Add(headerBox);
        panel.Children.Add(footerBox);

        var dialog = new ContentDialog
        {
            Title = "Page Setup",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        // ---- Parse and persist ----
        int top = ParseMargin(marginTop.Text, setup.Margins.Top);
        int bottom = ParseMargin(marginBottom.Text, setup.Margins.Bottom);
        int left = ParseMargin(marginLeft.Text, setup.Margins.Left);
        int right = ParseMargin(marginRight.Text, setup.Margins.Right);

        _settings.PrintMarginTop = top / 100.0;
        _settings.PrintMarginBottom = bottom / 100.0;
        _settings.PrintMarginLeft = left / 100.0;
        _settings.PrintMarginRight = right / 100.0;
        _settings.PrintHeader = headerBox.Text;
        _settings.PrintFooter = footerBox.Text;
    }

    private async void MenuPrint_Click(object _, RoutedEventArgs _e)
    {
        var session = ActiveSession;
        if (session is null) return;

        var text = Editor.Text;
        var fileName = session.FilePath ?? "Untitled";
        var setup = LoadPrintPageSettings();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        try
        {
            // PrintDlgEx is COM-based and shows UI — it must run on a dedicated STA thread.
            // Task.Run uses ThreadPool threads which are MTA, causing a COM null-vtable crash.
            var tcs = new TaskCompletionSource<bool>();
            var staThread = new Thread(() =>
            {
                try
                {
                    var svc = new PrintService(
                        text,
                        fileName,
                        _settings.FontFamily,
                        (float)_settings.FontSize,
                        _settings.FontWeight == "Bold",
                        _settings.FontStyle == "Italic",
                        setup);

                    tcs.SetResult(svc.ShowDialogAndPrint(hwnd));
                }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Name = "Inklet.PrintDialog";
            // IsBackground stays false: if the user closes the window while a print job is
            // mid-spool, we want the spool to finish rather than being torn down with the
            // process. The thread exits on its own once Print() returns.
            staThread.Start();
            bool printed = await tcs.Task;

            // 'printed' is false when the user cancelled — nothing to report.
            _ = printed;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Print Error", ex.Message);
        }
    }

    // ---------------------------------------------------------------
    // Print helpers
    // ---------------------------------------------------------------

    private PrintPageSettings LoadPrintPageSettings() => new()
    {
        Margins = new Margins(
            (int)(_settings.PrintMarginLeft * 100),
            (int)(_settings.PrintMarginRight * 100),
            (int)(_settings.PrintMarginTop * 100),
            (int)(_settings.PrintMarginBottom * 100)),
        Header = _settings.PrintHeader,
        Footer = _settings.PrintFooter
    };

    /// <summary>Converts a GDI+ hundredths-of-an-inch margin value to a display string.</summary>
    private static string MarginToString(int hundredths) => (hundredths / 100.0).ToString("0.##");

    /// <summary>
    /// Parses a user-entered inch value and returns it as hundredths of an inch,
    /// clamped to [25, 500]. Falls back to <paramref name="fallback"/> on invalid input.
    /// </summary>
    private static int ParseMargin(string text, int fallback)
    {
        if (double.TryParse(text, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.CurrentCulture, out double inches))
        {
            return Math.Clamp((int)(inches * 100), 25, 500);
        }
        return fallback;
    }

    /// <summary>Wraps a control in a container of fixed width for the margin row.</summary>
    private static UIElement WrapWithWidth(UIElement control, double width)
    {
        var container = new Microsoft.UI.Xaml.Controls.StackPanel { Width = width };
        container.Children.Add(control);
        return container;
    }

    #endregion

    #region About

    private async void MenuAbout_Click(object _, RoutedEventArgs _e)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version ?? new Version(1, 0, 0);
        // assembly.Location is empty for single-file bundles — fall back to "now" so
        // the About dialog still renders something sensible.
        DateTime buildDate;
        try
        {
            buildDate = !string.IsNullOrEmpty(assembly.Location)
                ? File.GetLastWriteTime(assembly.Location)
                : DateTime.Now;
        }
        catch { buildDate = DateTime.Now; }

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
        catch (Exception ex) { Debug.WriteLine($"About icon load failed: {ex.Message}"); }

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
            Text = $"\u00a9 {DateTime.Now.Year} JAD Apps. All rights reserved.",
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

    private void Editor_TextChanged(object _, TextChangedEventArgs _e)
    {
        if (_suppressTextChanged) return;
        if (ActiveSession is not { } session) return;

        bool wasDirty = session.IsModified;
        session.Content = Editor.Text;

        // Tab header / title bar only need refreshing when the dirty state actually
        // flips. This is the per-keystroke hot path — RefreshTabHeader iterates all
        // tabs, and AppWindow.Title is a COM call into the title bar. Doing them
        // unconditionally on every keypress was visibly laggy on large documents.
        if (session.IsModified != wasDirty)
        {
            RefreshTabHeader(session);
            UpdateTitle(session);
        }
    }

    private void Editor_SelectionChanged(object _, RoutedEventArgs _e)
    {
        UpdateCursorPosition();
    }

    private void Editor_PointerWheelChanged(object _, PointerRoutedEventArgs e)
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

    private void Editor_DragOver(object _, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
        }
    }

    private async void Editor_Drop(object _, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();

        // Open every dropped file. The first file may reuse a clean untitled tab;
        // subsequent files always go into new tabs. Folders are skipped silently
        // (the alternative — recursive expansion — would be a footgun for large trees).
        bool firstFileHandled = false;
        foreach (var item in items)
        {
            if (item is not StorageFile file) continue;

            TabSession session;
            if (!firstFileHandled && ActiveSession is { FilePath: null, IsModified: false } cur)
                session = cur;
            else
                session = AddNewTab();

            await LoadFileIntoSessionAsync(session, file.Path);
            firstFileHandled = true;
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
        if (ActiveSession is not { } session)
        {
            StatusBarPosition.Text = "Ln 1, Col 1";
            return;
        }

        // The line index is rebuilt only when the buffer changes, so this is O(log lines)
        // per cursor movement instead of O(N) over the entire buffer (the previous
        // implementation walked from offset 0 on every selection change).
        var (line, col) = session.Lines.GetLineColumn(Editor.SelectionStart);
        StatusBarPosition.Text = $"Ln {line}, Col {col}";
    }

    #endregion

    #region Window Close

    // True once the async session save has completed and we're ready to actually close.
    private bool _allowClose;

    private async void AppWindow_Closing(AppWindow _, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        // Block the OS close until we've finished writing — otherwise large unsaved
        // buffers can be silently dropped if the process exits before the write finishes.
        args.Cancel = true;

        try
        {
            SaveCurrentTabState();
            SaveWindowSize();
            await PersistSessionAsync();
        }
        catch
        {
            // Even on save failure we must let the window close — a save failure should
            // never trap the user inside the app. The .bak from the previous successful
            // close (see SettingsService.WriteSessionFileAtomicAsync) is still on disk.
        }
        finally
        {
            _allowClose = true;
            Close();
        }
    }

    private void SaveWindowSize()
    {
        try
        {
            var isMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            _settings.WindowMaximized = isMaximized;
            // Only overwrite the restored size when not maximized — the maximized
            // dimensions equal the screen resolution and must not be used as a
            // restored size on next launch.
            if (!isMaximized)
            {
                _settings.WindowWidth = AppWindow.Size.Width;
                _settings.WindowHeight = AppWindow.Size.Height;
            }
        }
        catch (Exception ex) { Debug.WriteLine($"SaveWindowSize failed: {ex.Message}"); }
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

    /// <summary>
    /// Prompts the user to save unsaved changes.
    /// Returns Primary (Save), Secondary (Don't Save), or None (Cancel).
    /// </summary>
    private async Task<ContentDialogResult> ShowSavePromptAsync(TabSession session)
    {
        return await new ContentDialog
        {
            Title = "Inklet",
            Content = $"Do you want to save changes to {session.TabTitle.TrimStart('*')}?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don\u2019t Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        }.ShowAsync();
    }

    #endregion

    #region Helpers

    private void InitializeWithWindow(object picker)
    {
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // PostMessage / GetFocus — used by MenuRedo_Click to send Ctrl+Y to the focused
    // editor window without leaking synthetic input into the global queue.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessage(IntPtr hWnd, uint Msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetFocus();

    #endregion
}