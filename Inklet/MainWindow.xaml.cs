using Inklet.Editor;
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
/// Main application window â€” hosts a multi-tab editor with session persistence.
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
    // unsaved Untitled-tab content. Coalesced â€” skipped if a save is already in flight.
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

        SetupCustomTitleBar();
        RestoreSettings();
        // The window icon costs a disk probe; it only affects the taskbar/alt-tab,
        // so set it after the first frame rather than before Activate.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SetWindowIcon);
        AppWindow.Closing += AppWindow_Closing;

        // Autosave timer is started at the END of InitialLoadAsync â€” starting it here
        // would let an early tick race the tab-population loop and observe a half-built
        // TabStrip.TabItems collection.
        Diagnostics.Perf.Mark("WindowCtorDone");
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
            // Only persist if at least one tab is dirty â€” autosaving an unchanged
            // session every 30 s would needlessly thrash the disk. Snapshot the tab
            // collection before iterating: PersistSessionAsync awaits, and a tab close
            // on the UI thread during that await would invalidate a live enumeration.
            var snapshot = TabStrip.TabItems.OfType<TabViewItem>().ToList();
            bool anyDirty = false;
            foreach (var tvi in snapshot)
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
        ApplyZoom(); // applies the zoom-adjusted font once and updates the status bar
    }

    private async Task InitialLoadAsync()
    {
        Diagnostics.Perf.Mark("InitialLoadStart");
        // Session read + JSON parse run off the UI thread; the window is already
        // visible and interactive while this completes.
        var envelope = await Task.Run(() => _settings.LoadSessionV2());
        Diagnostics.Perf.Mark("SessionReadDone");

        var lostRestores = new List<string>();

        if (envelope.Tabs.Count > 0)
        {
            if (_settings.WindowMaximized && AppWindow.Presenter is OverlappedPresenter overlapped)
                overlapped.Maximize();
            else
                ResizeWindow((int)_settings.WindowWidth, (int)_settings.WindowHeight);

            foreach (var state in envelope.Tabs)
            {
                var session = await RestoreTabAsync(state, lostRestores);
                AttachTab(session);
            }

            var clampedIdx = Math.Clamp(envelope.ActiveTab, 0, TabStrip.TabItems.Count - 1);
            if (TabStrip.SelectedIndex == clampedIdx)
            {
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
            ResizeWindow(800, 550);
            AddNewTab();
        }

        // Command-line file: reuse the active tab if it is a clean untitled one.
        if (!string.IsNullOrWhiteSpace(_initialFilePath))
        {
            // The binary-file prompt needs a live XamlRoot; the window may not have
            // completed its first layout yet (see the 1.0.9 silent-failure fix).
            while (Content.XamlRoot is null)
                await Task.Delay(15);

            TabSession session;
            if (ActiveSession is { FilePath: null, IsModified: false } cur)
                session = cur;
            else
                session = AddNewTab();

            Diagnostics.Perf.Mark("CmdFileLoadStart");
            await LoadFileIntoSessionAsync(session, _initialFilePath);
            Diagnostics.Perf.Mark("CmdFileLoadDone");
        }

        if (lostRestores.Count > 0)
        {
            while (Content.XamlRoot is null) await Task.Delay(15);
            await ShowErrorAsync("Unsaved changes not restored",
                "These files changed on disk since the last session, so their unsaved edits could not be re-applied:\n"
                + string.Join("\n", lostRestores));
        }

        StartAutosaveTimer();
        DispatcherQueue.TryEnqueue(() => Editor.Focus(FocusState.Programmatic));
    }

    /// <summary>
    /// Rebuilds one tab from its captured session state. File-backed tabs open
    /// instantly (memory-mapped view; indexing continues in the background) and
    /// unsaved edit deltas re-apply once the file is verified unchanged.
    /// </summary>
    private async Task<TabSession> RestoreTabAsync(Engine.SessionTabState state, List<string> lostRestores)
    {
        var session = new TabSession();
        if (state.FilePath is not null && File.Exists(state.FilePath))
        {
            try
            {
                var doc = await Engine.Document.OpenAsync(state.FilePath);
                session.Doc = doc;
                session.FilePath = state.FilePath;
                session.Document = DocumentStateFrom(doc);
                if (state.Pieces is not null)
                {
                    if (Engine.Document.FingerprintMatches(state))
                    {
                        if (doc.IsFullyIndexed) doc.ApplySessionState(state);
                        else session.PendingSessionState = state; // applied on IndexCompleted
                    }
                    else
                    {
                        lostRestores.Add(Path.GetFileName(state.FilePath));
                    }
                }
                HookDocumentEvents(session);
                AttachFileWatcher(session);
            }
            catch
            {
                // Unreadable file: fall back to an empty untitled tab.
                session.Doc = Engine.Document.CreateUntitled();
                session.FilePath = null;
            }
        }
        else
        {
            // Untitled (or the file vanished): restore content inline, v1-style.
            session.Doc = Engine.Document.CreateUntitled(state.UntitledContent ?? string.Empty);
            if (state.Dirty || (state.FilePath is not null && state.UntitledContent is not null))
                session.Doc.MarkRestoredDirty();
            session.Document = BuildDocumentState(state);
        }
        session.View = EditorViewState.Default with
        {
            Caret = state.CaretOffset,
            Anchor2 = state.AnchorOffset,
            Anchor = new ViewportAnchor(Math.Max(0, state.ScrollLine), 0, 0),
        };
        session.ShownDirty = session.IsModified;
        return session;
    }

    /// <summary>Status-bar metadata derived from a live engine document.</summary>
    private static DocumentState DocumentStateFrom(Engine.Document doc) => new()
    {
        FilePath = doc.FilePath,
        Encoding = doc.Encoding,
        HasBom = doc.HasBom,
        LineEnding = doc.LineEnding,
    };

    /// <summary>
    /// Marshals a document's background-indexing events onto the UI thread:
    /// absorbed segments feed the editor/scrollbar, the status bar shows
    /// progress, and pending session deltas apply on completion.
    /// </summary>
    private void HookDocumentEvents(TabSession session)
    {
        var doc = session.Doc;
        if (doc is null || doc.IsFullyIndexed) return;
        doc.IndexProgressChanged += _ => DispatcherQueue.TryEnqueue(() => OnDocIndexProgress(session));
        doc.IndexCompleted += () => DispatcherQueue.TryEnqueue(() => OnDocIndexProgress(session));
    }

    private void OnDocIndexProgress(TabSession session)
    {
        var doc = session.Doc;
        if (doc is null) return;
        doc.AbsorbIndexedSegments();
        if (doc.IsFullyIndexed && session.PendingSessionState is { } pending)
        {
            session.PendingSessionState = null;
            try { doc.ApplySessionState(pending); } catch { /* deltas no longer applicable */ }
        }
        if (ReferenceEquals(session, ActiveSession))
        {
            UpdateCursorPosition();
            RefreshDirtyIndicators(session);
        }
    }

    private void ResizeWindow(int width, int height)
    {
        try { AppWindow.Resize(new SizeInt32(width, height)); }
        catch (Exception ex) { Debug.WriteLine($"ResizeWindow({width},{height}) failed: {ex.Message}"); }
    }

    private static DocumentState BuildDocumentState(Engine.SessionTabState data)
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
        var session = new TabSession { FilePath = filePath, Doc = Engine.Document.CreateUntitled() };
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

        // O(1): swap the document reference and restore this tab's view. No text
        // moves and the incoming tab's undo history is intact.
        Editor.Document = session.Doc;
        Editor.RestoreViewState(session.View);
        Editor.WordWrap = _settings.WordWrap;
        UpdateTitle(session);
        UpdateStatusBar(session);
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Captures the active tab's caret/selection/scroll into its session.</summary>
    private void SaveCurrentTabState()
    {
        if (ActiveSession is not { } session) return;
        if (ReferenceEquals(Editor.Document, session.Doc))
            session.View = Editor.CaptureViewState();
    }

    private void PersistSession() => _ = PersistSessionAsync();

    /// <summary>
    /// Persists the session. Snapshot capture is cheap on the UI thread (edit
    /// deltas, never file content); serialisation and I/O run off-thread.
    /// Returns false if the write failed - the close handler uses this to prompt
    /// before tearing down the window with unsaved data.
    /// </summary>
    private async Task<bool> PersistSessionAsync()
    {
        var envelope = BuildSessionSnapshot();
        _settings.LastActiveTabIndex = TabStrip.SelectedIndex;
        return await _settings.SaveSessionV2Async(envelope).ConfigureAwait(false);
    }

    private SettingsService.SessionV2Envelope BuildSessionSnapshot()
    {
        SaveCurrentTabState();

        var envelope = new SettingsService.SessionV2Envelope
        {
            ActiveTab = TabStrip.SelectedIndex,
        };
        foreach (var tvi in TabStrip.TabItems.OfType<TabViewItem>())
        {
            if (tvi.Tag is not TabSession s || s.Doc is null) continue;
            // A dirty streamed doc that is still indexing cannot capture deltas yet;
            // fall back to its pending (restored) state so nothing is dropped.
            var state = s.Doc.CaptureSessionState() ?? s.PendingSessionState;
            if (state is null) continue;
            state.CaretOffset = s.View.Caret;
            state.AnchorOffset = s.View.Anchor2;
            state.ScrollLine = s.View.Anchor.Line;
            envelope.Tabs.Add(state);
        }
        return envelope;
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
                    return; // Save failed or was cancelled â€” abort close
            }
            else if (result == ContentDialogResult.None)
            {
                return; // User chose Cancel â€” abort close
            }
            // ContentDialogResult.Secondary = Don't Save â€” fall through to close
        }

        if (TabStrip.TabItems.Count == 1)
        {
            // Last tab - reset rather than close.
            DetachFileWatcher(session);
            var oldDoc = session.Doc;
            session.Doc = Engine.Document.CreateUntitled();
            session.FilePath = null;
            session.Document = new DocumentState();
            session.View = EditorViewState.Default;
            session.ShownDirty = false;
            Editor.Document = session.Doc;
            oldDoc?.Dispose();
            RefreshTabHeader(session);
            UpdateTitle(session);
            UpdateStatusBar(session);
            PersistSession();
            Editor.Focus(FocusState.Programmatic);
        }
        else
        {
            if (ReferenceEquals(Editor.Document, session.Doc)) Editor.Document = null;
            TabStrip.TabItems.Remove(tab);
            InvalidateTabLayout();
            session.Dispose(); // watcher + document (releases the file mapping)
            // Persist remaining tabs immediately so a mid-session close is not lost
            // if the app terminates unexpectedly before the next graceful shutdown.
            PersistSession();
        }
    }

    private void TabStrip_SelectionChanged(object _, SelectionChangedEventArgs e)
    {
        // Capture the outgoing tab's caret/scroll; its text needs no syncing -
        // the document IS the state.
        foreach (var removed in e.RemovedItems.OfType<TabViewItem>())
        {
            if (removed.Tag is TabSession old && ReferenceEquals(Editor.Document, old.Doc))
                old.View = Editor.CaptureViewState();
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
        var oldDoc = session.Doc;
        session.Doc = Engine.Document.CreateUntitled();
        session.FilePath = null;
        session.Document = new DocumentState();
        session.View = EditorViewState.Default;
        session.ShownDirty = false;
        Editor.Document = session.Doc;
        oldDoc?.Dispose();

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
            // Warn on binary files before opening.
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

            // No "large file" warning any more: the document opens as a memory-mapped
            // view with the first screen available immediately, whatever the size.
            var doc = await Engine.Document.OpenAsync(filePath);
            var oldDoc = session.Doc;
            session.Doc = doc;
            session.FilePath = filePath;
            session.Document = DocumentStateFrom(doc);
            session.View = EditorViewState.Default;
            session.ShownDirty = false;
            HookDocumentEvents(session);
            AttachFileWatcher(session);
            RefreshTabHeader(session);

            if (ReferenceEquals(ActiveSession, session))
            {
                Editor.Document = doc;
                UpdateTitle(session);
                UpdateStatusBar(session);
            }
            oldDoc?.Dispose();
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
        if (session.Doc is null) return false;
        if (session.FilePath is null) return await SaveAsSessionAsync(session);

        try
        {
            // Suppress the watcher's echo of our own write, before AND after - the
            // FileSystemWatcher event can arrive at either moment.
            session.Watcher?.SuppressNextChange();
            await session.Doc.SaveAsync();
            session.Watcher?.SuppressNextChange();

            RefreshDirtyIndicators(session);
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
        if (session.Doc is null) return false;

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
            session.Watcher?.SuppressNextChange();
            await session.Doc.SaveAsync(new Engine.SaveOptions { TargetPath = file.Path });
            session.FilePath = file.Path;
            session.Document = DocumentStateFrom(session.Doc) with { FilePath = file.Path };

            // Save As changes the watched path - re-attach to the new location.
            AttachFileWatcher(session);
            RefreshDirtyIndicators(session);
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

    private void MenuUndo_Click(object _, RoutedEventArgs _e) => Editor.DocumentUndo();

    private void MenuRedo_Click(object _, RoutedEventArgs _e)
    {
        Editor.Focus(FocusState.Programmatic);
        Editor.DocumentRedo();
    }

    private void MenuCut_Click(object _, RoutedEventArgs _e) => Editor.CutPlainSelection();
    private void MenuCopy_Click(object _, RoutedEventArgs _e) => Editor.CopyPlainSelection();
    private async void MenuPaste_Click(object _, RoutedEventArgs _e) => await Editor.PastePlainAsync();
    private void MenuSelectAll_Click(object _, RoutedEventArgs _e) => Editor.DocumentSelectAll();

    private void MenuDelete_Click(object _, RoutedEventArgs _e)
        => Editor.DeleteSelection();

    private void MenuTimeDate_Click(object _, RoutedEventArgs _e)
        => Editor.InsertAtCaret(DateTime.Now.ToString("h:mm tt M/d/yyyy"));

    #endregion

    #region Find & Replace

    private void MenuFind_Click(object _, RoutedEventArgs _e) => ShowFindBar(false);
    private void MenuReplace_Click(object _, RoutedEventArgs _e) => ShowFindBar(true);
    private void MenuFindNext_Click(object _, RoutedEventArgs _e) => FindNext();
    private void MenuFindPrevious_Click(object _, RoutedEventArgs _e) => FindPrevious();

    private async void MenuGoTo_Click(object _, RoutedEventArgs _e)
    {
        var lineCount = Editor.LineCount;
        var approx = Editor.IsLineCountExact ? "" : "~";
        var input = new TextBox { PlaceholderText = $"Line number (1-{approx}{lineCount})" };
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
            long.TryParse(input.Text, out long target) && target >= 1 && target <= lineCount)
        {
            Editor.GoToLine(target);
        }
    }

    private void ShowFindBar(bool showReplace)
    {
        FindReplaceBar.Visibility = Visibility.Visible;
        ReplacePanel.Visibility = showReplace ? Visibility.Visible : Visibility.Collapsed;
        // Prefill from a short single-line selection only (fetch once, bounded).
        if (Editor.SelectionLength is > 0 and <= 1024)
        {
            var selected = Editor.GetSelectedText();
            if (!selected.Contains('\n') && !selected.Contains('\r'))
                FindTextBox.Text = selected;
        }
        FindTextBox.Focus(FocusState.Programmatic);
        FindTextBox.SelectAll();
    }

    private void CloseFindBar_Click(object _, RoutedEventArgs _e)
    {
        _findCts?.Cancel();
        FindReplaceBar.Visibility = Visibility.Collapsed;
        Editor.Focus(FocusState.Programmatic);
    }

    private void FindTextBox_KeyDown(object _, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) { FindNext(); e.Handled = true; }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            _findCts?.Cancel();
            FindReplaceBar.Visibility = Visibility.Collapsed;
            Editor.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void FindNext_Click(object _, RoutedEventArgs _e) => FindNext();
    private void FindPrev_Click(object _, RoutedEventArgs _e) => FindPrevious();

    // One find at a time: a new request cancels the previous scan.
    private System.Threading.CancellationTokenSource? _findCts;

    private async void FindNext() => await RunFindAsync(backward: false);
    private async void FindPrevious() => await RunFindAsync(backward: true);

    /// <summary>
    /// Streams a search over the engine snapshot off the UI thread - the editor
    /// stays interactive however large the document is.
    /// </summary>
    private async Task RunFindAsync(bool backward)
    {
        var doc = ActiveSession?.Doc;
        var needle = FindTextBox.Text;
        if (doc is null || string.IsNullOrEmpty(needle)) return;

        _findCts?.Cancel();
        _findCts = new System.Threading.CancellationTokenSource();
        var ct = _findCts.Token;

        var query = new Engine.FindQuery
        {
            Needle = needle,
            MatchCase = FindMatchCase.IsChecked == true,
            Backward = backward,
            StartOffset = backward
                ? Math.Max(0, Editor.SelectionStart - 1)
                : Editor.SelectionStart + Editor.SelectionLength,
        };
        try
        {
            var hit = await doc.FindNextAsync(query, ct);
            if (ct.IsCancellationRequested) return;
            if (hit is { } m)
            {
                Editor.SetSelection(m.Offset, m.Length);
                Editor.Focus(FocusState.Programmatic);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async void Replace_Click(object _, RoutedEventArgs _e)
    {
        var doc = ActiveSession?.Doc;
        if (doc is null || string.IsNullOrEmpty(FindTextBox.Text)) return;
        var cmp = FindMatchCase.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (Editor.SelectionLength > 0 && Editor.SelectionLength <= 64 * 1024
            && Editor.GetSelectedText().Equals(FindTextBox.Text, cmp))
        {
            long start = Editor.SelectionStart;
            // One engine edit: a single undo unit, history preserved.
            doc.Replace(start, Editor.SelectionLength, ReplaceTextBox.Text);
            Editor.SetSelection(start + ReplaceTextBox.Text.Length, 0);
        }
        await RunFindAsync(backward: false);
    }

    private async void ReplaceAll_Click(object _, RoutedEventArgs _e)
    {
        var doc = ActiveSession?.Doc;
        var needle = FindTextBox.Text;
        if (doc is null || string.IsNullOrEmpty(needle)) return;

        _findCts?.Cancel();
        _findCts = new System.Threading.CancellationTokenSource();
        var ct = _findCts.Token;
        try
        {
            // Collect on a background thread, apply on the UI thread as one undo
            // unit; retry once if an edit slipped in between.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var (offsets, revision) = await doc.CollectMatchesAsync(
                    needle, FindMatchCase.IsChecked == true, ct);
                if (ct.IsCancellationRequested) return;
                if (doc.TryReplaceAll(offsets, revision, needle.Length, ReplaceTextBox.Text))
                    break;
            }
        }
        catch (OperationCanceledException) { }
    }

    #endregion

    #region Format

    private void MenuWordWrap_Click(object _, RoutedEventArgs _e)
    {
        var wrap = MenuWordWrap.IsChecked;
        Editor.WordWrap = wrap;
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
            IsChecked = _settings.FontWeight == "Bold"
        };
        panel.Children.Add(boldCheck);

        var italicCheck = new CheckBox
        {
            Content = "Italic",
            IsChecked = _settings.FontStyle == "Italic"
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
                _settings.FontFamily = chosen;

            _baseFontSize = sizeBox.Value;
            _settings.FontSize = _baseFontSize;
            _settings.FontWeight = boldCheck.IsChecked == true ? "Bold" : "Normal";
            _settings.FontStyle = italicCheck.IsChecked == true ? "Italic" : "Normal";

            // Re-applies family, zoom-adjusted size, bold and italic to the Win2D editor.
            ApplyZoom();
        }
    }

    private void ApplyFontToEditor()
    {
        bool bold = _settings.FontWeight == "Bold";
        bool italic = _settings.FontStyle == "Italic";
        float size = (float)(_baseFontSize * _zoomPercent / 100.0);
        Editor.SetFont(_settings.FontFamily, Math.Max(1f, size), bold, italic);
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
        ApplyFontToEditor();
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
            PlaceholderText = "e.g. &f\t\t&d  â€”  tokens: &f filename, &d date, &t time, &p page, &P total"
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

        var doc = session.Doc;
        if (doc is null) return;
        var fileName = session.FilePath ?? "Untitled";

        // The print service streams logical lines straight from the engine (its
        // reads are snapshot-based and thread-safe), so printing holds O(1) text
        // in memory regardless of document size.
        IEnumerable<string> DocumentLines()
        {
            long count = doc.LineCount;
            for (long line = 0; line < count && line <= doc.IndexedLineCountFloor; line++)
                yield return doc.GetLine(line).Text.ToString();
        }
        var setup = LoadPrintPageSettings();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        try
        {
            // PrintDlgEx is COM-based and shows UI â€” it must run on a dedicated STA thread.
            // Task.Run uses ThreadPool threads which are MTA, causing a COM null-vtable crash.
            var tcs = new TaskCompletionSource<bool>();
            var staThread = new Thread(() =>
            {
                try
                {
                    var svc = new PrintService(
                        DocumentLines,
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

            // 'printed' is false when the user cancelled â€” nothing to report.
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
        // assembly.Location is empty for single-file bundles â€” fall back to "now" so
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

    /// <summary>
    /// Wires the editor's change events, applies the themed colours and font, and tracks
    /// light/dark theme switches.
    /// </summary>
    private void Editor_Loaded(object _, RoutedEventArgs _e)
    {
        Editor.SetWindowHandle(WinRT.Interop.WindowNative.GetWindowHandle(this));
        Editor.TextChanged += Editor_TextChanged;
        Editor.SelectionChanged += Editor_SelectionChanged;
        Editor.ActualThemeChanged += (_, _) => ApplyEditorTheme();
        ApplyEditorTheme();
        ApplyZoom(); // one font application (SetFont resets the layout caches)
    }

    /// <summary>Applies the current light/dark theme colours to the Win2D editor surface.</summary>
    private void ApplyEditorTheme()
    {
        try
        {
            bool dark = Editor.ActualTheme == ElementTheme.Dark;
            var text = dark ? Windows.UI.Color.FromArgb(255, 0xF2, 0xF2, 0xF2)
                            : Windows.UI.Color.FromArgb(255, 0x1A, 0x1A, 0x1A);
            var bg = dark ? Windows.UI.Color.FromArgb(255, 0x1F, 0x1F, 0x1F)
                          : Windows.UI.Color.FromArgb(255, 0xFF, 0xFF, 0xFF);
            var sel = dark ? Windows.UI.Color.FromArgb(0x99, 0x2C, 0x5A, 0x8C)
                           : Windows.UI.Color.FromArgb(0x99, 0xAD, 0xD6, 0xFF);
            Editor.SetColors(text, bg, sel);
        }
        catch (Exception ex) { Debug.WriteLine($"ApplyEditorTheme failed: {ex.Message}"); }
    }

    private void Editor_TextChanged(object? _, EventArgs _e)
    {
        // Per keystroke this is O(1): dirty state comes straight from the engine's
        // undo position; the header/title refresh only on actual transitions
        // (including undo-back-to-saved flipping the tab clean again).
        if (ActiveSession is { } session) RefreshDirtyIndicators(session);
    }

    /// <summary>Refreshes the tab header/title when the dirty state actually changed.</summary>
    private void RefreshDirtyIndicators(TabSession session)
    {
        bool dirty = session.IsModified;
        if (dirty == session.ShownDirty) return;
        session.ShownDirty = dirty;
        RefreshTabHeader(session);
        UpdateTitle(session);
    }

    private void Editor_SelectionChanged(object? _, EventArgs _e) => UpdateCursorPosition();

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
        // (the alternative â€” recursive expansion â€” would be a footgun for large trees).
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
        var doc = ActiveSession?.Doc;
        if (doc is null)
        {
            StatusBarPosition.Text = "Ln 1, Col 1";
            return;
        }

        // O(log n) from the engine's line index - no document materialisation.
        var (line, col) = Editor.CaretLineColumn;
        string position = $"Ln {line + 1}, Col {col + 1}";
        if (!doc.IsFullyIndexed)
            position += $"  \u00b7  Indexing {doc.IndexProgress:P0}";
        StatusBarPosition.Text = position;
    }

    #endregion

    #region Window Close

    // True once the async session save has completed and we're ready to actually close.
    private bool _allowClose;

    private async void AppWindow_Closing(AppWindow _, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;

        // Block the OS close until we've finished writing â€” otherwise large unsaved
        // buffers can be silently dropped if the process exits before the write finishes.
        args.Cancel = true;

        bool savedOk;
        try
        {
            SaveCurrentTabState();
            SaveWindowSize();
            savedOk = await PersistSessionAsync();
        }
        catch
        {
            savedOk = false;
        }

        if (!savedOk && AnyTabIsModified())
        {
            // Save failed AND there's unsaved data â€” give the user a real choice rather
            // than silently destroying their work. The XamlRoot is still alive at this
            // point (we cancelled the close) so the dialog renders correctly.
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Couldn't save your session",
                    Content = "Inklet failed to write your unsaved tabs to disk (the disk may be full or the file is locked). " +
                              "Closing now will lose those changes.",
                    PrimaryButtonText = "Close anyway",
                    CloseButtonText = "Stay open",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = Content.XamlRoot,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                    return; // user chose Stay open â€” abort the close
            }
            catch
            {
                // If the dialog itself can't render (very rare), fall through to close
                // â€” better to close than to deadlock the app.
            }
        }

        _allowClose = true;
        Close();
    }

    private bool AnyTabIsModified()
    {
        foreach (var tvi in TabStrip.TabItems.OfType<TabViewItem>())
        {
            if (tvi.Tag is TabSession s && s.IsModified) return true;
        }
        return false;
    }

    private void SaveWindowSize()
    {
        try
        {
            var isMaximized = AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized };
            _settings.WindowMaximized = isMaximized;
            // Only overwrite the restored size when not maximized â€” the maximized
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

    #endregion
}