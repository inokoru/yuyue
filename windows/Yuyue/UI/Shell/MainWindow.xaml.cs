using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Text;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Shelf;
using Yuyue.UI.Controls;

namespace Yuyue.UI.Shell;

/// <summary>
/// Native Windows 11 single-window shell with a seamless 40px Warm Mica Alt titlebar,
/// floating 6px Fluent 2 tab pills, paper-matched Reader/PDF header integration,
/// pinned Library tab, closeable Settings tab, drag-reorder, tab tear-out, middle-click close,
/// closed-tab history (<c>Ctrl+Shift+T</c>), <c>F11</c> full-screen, mouse side-buttons,
/// and Windows 11 Taskbar Thumbnail Toolbar (<c>ITaskbarList3</c>).
/// </summary>
public sealed partial class MainWindow : Window, IDebugScreenshotCapable
{
    public LibraryService Library { get; }
    public bool IsPrimaryShell { get; }

    private readonly List<ITabContent> _tabs = [];
    private readonly Dictionary<ITabContent, Action> _tabMetaHandlers = new();
    private readonly Stack<SavedTabEntry> _closedTabHistory = new();

    private ITabContent? _activeTab;
    private ITabContent? _draggingTab;
    private bool _isFullScreen;
    private bool _closingInternally;

    private DispatcherTimer? _toastTimer;
    private Action? _toastAction;
    private TaskbarThumbToolbar? _thumbToolbar;
    private IntPtr _hwnd;
    private bool _subclassAttached;
    private uint _wmTaskbarCreated;

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData);

    private const nuint ShellSubclassId = 0x5959_5348;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_APPCOMMAND = 0x0319;
    private const uint WM_NCDESTROY = 0x0082;
    private const int THBN_CLICKED = 0x1800;
    private const int APPCOMMAND_BROWSER_BACKWARD = 1;
    private const int APPCOMMAND_BROWSER_FORWARD = 2;

    private static readonly SubclassProc s_subclassDelegate = ShellWndProc;
    private static readonly Dictionary<IntPtr, MainWindow> s_windowByHwnd = new();

    public IReadOnlyList<ITabContent> Tabs => _tabs;
    public ITabContent? ActiveTab => _activeTab;
    public bool IsSettingsOpen => _activeTab?.TabKind == "settings";
    public XamlRoot? WindowXamlRoot => RootGrid.XamlRoot;

    public Func<string, ITabContent>? SettingsTabFactory { get; set; }
    public Func<IReadOnlyList<string>, Task>? FilesDroppedHandler { get; set; }
    public event Action<MainWindow, ITabContent>? TabTearOutRequested;
    public event Action<MainWindow>? ShellWindowClosingToTray;

    public MainWindow(LibraryService library, bool isPrimaryShell = true)
    {
        Library = library;
        IsPrimaryShell = isPrimaryShell;
        InitializeComponent();

        Title = L10n.T("语阅");
        // IMPORTANT: Use ONLY Window.ExtendsContentIntoTitleBar + Window.SetTitleBar(TabDragRegion).
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TabDragRegion);

        int initW = Math.Max(980, library.Prefs.MainWindowWidth);
        int initH = Math.Max(660, library.Prefs.MainWindowHeight);
        ThemeHelper.ResizeWindow(this, initW, initH, minWidthDip: 900, minHeightDip: 600);
        if (isPrimaryShell && library.Prefs.MainWindowMaximized && AppWindow.Presenter is OverlappedPresenter op)
        {
            try { op.Maximize(); } catch { }
        }

        AttachWin32Hooks();
        ApplyAppearance();

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        Library.SystemIntegration.PlaybackStateChanged += OnPlaybackStateChanged;
    }

    private void AttachWin32Hooks()
    {
        try
        {
            _hwnd = WindowNative.GetWindowHandle(this);
            if (_hwnd == IntPtr.Zero) return;
            _wmTaskbarCreated = RegisterWindowMessageW("TaskbarButtonCreated");
            lock (s_windowByHwnd) s_windowByHwnd[_hwnd] = this;
            _subclassAttached = SetWindowSubclass(_hwnd, s_subclassDelegate, ShellSubclassId, 0);
            _thumbToolbar = new TaskbarThumbToolbar(_hwnd);
            DispatcherQueue.TryEnqueue(UpdateTaskbarThumbToolbar);
        }
        catch { }
    }

    private static IntPtr ShellWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, nuint dwRefData)
    {
        MainWindow? win;
        lock (s_windowByHwnd) s_windowByHwnd.TryGetValue(hWnd, out win);

        if (win is not null)
        {
            if (win._wmTaskbarCreated != 0 && uMsg == win._wmTaskbarCreated)
            {
                win.DispatcherQueue.TryEnqueue(() =>
                {
                    win._thumbToolbar?.Reset();
                    win.UpdateTaskbarThumbToolbar();
                });
            }
            else if (uMsg == WM_COMMAND)
            {
                int hi = (int)(((long)wParam >> 16) & 0xFFFF);
                int lo = (int)((long)wParam & 0xFFFF);
                if (hi == THBN_CLICKED)
                {
                    win.DispatcherQueue.TryEnqueue(() => win.HandleTaskbarThumbCommand((ushort)lo));
                    return IntPtr.Zero;
                }
            }
            else if (uMsg == WM_XBUTTONDOWN)
            {
                int btn = (int)(((long)wParam >> 16) & 0xFFFF);
                bool isBack = btn == 1;
                if (btn is 1 or 2)
                {
                    win.DispatcherQueue.TryEnqueue(() => win.HandleMouseBackForward(isBack));
                    return new IntPtr(1);
                }
            }
            else if (uMsg == WM_APPCOMMAND)
            {
                int cmd = (int)(((long)lParam >> 16) & 0x0FFF);
                if (cmd == APPCOMMAND_BROWSER_BACKWARD || cmd == APPCOMMAND_BROWSER_FORWARD)
                {
                    bool isBack = cmd == APPCOMMAND_BROWSER_BACKWARD;
                    win.DispatcherQueue.TryEnqueue(() => win.HandleMouseBackForward(isBack));
                    return new IntPtr(1);
                }
            }
            else if (uMsg == WM_NCDESTROY)
            {
                lock (s_windowByHwnd) s_windowByHwnd.Remove(hWnd);
                RemoveWindowSubclass(hWnd, s_subclassDelegate, uIdSubclass);
            }
        }
        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }

    private void HandleMouseBackForward(bool isBack)
    {
        _activeTab?.HandleMouseBackForward(isBack);
    }

    private void HandleTaskbarThumbCommand(ushort cmdId)
    {
        var src = App.CurrentApp.ActiveMiniPlayerSource;
        try
        {
            switch (cmdId)
            {
                case TaskbarThumbToolbar.IdPrev:
                    src?.SkipPrevious();
                    break;
                case TaskbarThumbToolbar.IdPlayPause:
                    if (App.CurrentApp.IsPodcastActiveAudioOwner())
                        Library.SystemIntegration.TogglePlaybackCallback?.Invoke();
                    else if (src is not null)
                        src.TogglePlayPause();
                    else
                        Library.SystemIntegration.TogglePlaybackCallback?.Invoke();
                    break;
                case TaskbarThumbToolbar.IdNext:
                    src?.SkipNext();
                    break;
            }
        }
        catch (ObjectDisposedException)
        {
            App.CurrentApp.ClearActiveMiniPlayerSource(src);
        }
    }

    private void OnPlaybackStateChanged()
    {
        DispatcherQueue.TryEnqueue(UpdateTaskbarThumbToolbar);
    }

    public void UpdateTaskbarThumbToolbar()
    {
        bool hasAudio = App.CurrentApp.ActiveMiniPlayerSource is not null
            || Library.SystemIntegration.TogglePlaybackCallback is not null;
        bool isPlaying = Library.SystemIntegration.IsAudioPlaying;
        _thumbToolbar?.EnsureAdded(hasAudio, isPlaying);
    }

    public void AddTab(ITabContent content, bool select = true, int insertIndex = -1)
    {
        if (_tabs.Contains(content))
        {
            if (select) SelectTab(content);
            return;
        }

        Action metaHandler = () => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_tabs.Contains(content)) return;
            RebuildTabStrip();
            if (ReferenceEquals(content, _activeTab))
            {
                ApplyActiveTabChrome();
                UpdateWindowTitle();
            }
        });
        _tabMetaHandlers[content] = metaHandler;
        content.TabMetaChanged += metaHandler;

        if (insertIndex >= 0 && insertIndex <= _tabs.Count)
            _tabs.Insert(insertIndex, content);
        else
            _tabs.Add(content);

        if (select)
            ActivateTab(content);
        else
            RebuildTabStrip();
    }

    public bool SelectTab(ITabContent content)
    {
        if (!_tabs.Contains(content)) return false;
        ActivateTab(content);
        return true;
    }

    public ITabContent? FindTabByBookId(string bookId) =>
        _tabs.FirstOrDefault(t => t.ContainsBookId(bookId));

    public ITabContent? FindPinnedLibraryTab() =>
        _tabs.FirstOrDefault(t => t.IsPinned && t.TabKind == "library");

    public async Task<bool> CloseTabAsync(ITabContent content, bool recordHistory = true)
    {
        if (content.IsPinned) return false;
        if (!_tabs.Contains(content)) return false;

        if (!await content.PrepareCloseAsync()) return false;

        if (recordHistory && content.SerializeTabState() is { } saved)
            _closedTabHistory.Push(saved);

        DetachTabInternal(content, disposeContent: true);

        if (!IsPrimaryShell && _tabs.Count == 0)
        {
            _closingInternally = true;
            Close();
        }
        return true;
    }

    public ITabContent? DetachTabForTransfer(ITabContent content)
    {
        if (content.IsPinned) return null;
        if (!_tabs.Contains(content)) return null;
        DetachTabInternal(content, disposeContent: false);
        return content;
    }

    private void DetachTabInternal(ITabContent content, bool disposeContent)
    {
        int idx = _tabs.IndexOf(content);
        bool wasActive = ReferenceEquals(_activeTab, content);
        if (ReferenceEquals(_draggingTab, content))
            _draggingTab = null;

        if (_tabMetaHandlers.Remove(content, out var metaHandler))
            content.TabMetaChanged -= metaHandler;

        if (wasActive)
        {
            content.OnTabDeactivated();
            ContentStage.Children.Clear();
            _activeTab = null;
        }

        _tabs.Remove(content);

        if (disposeContent)
        {
            if (content is IMiniPlayerSource mps)
                App.CurrentApp.ClearActiveMiniPlayerSource(mps);
            try { content.Dispose(); } catch { }
        }

        if (wasActive && _tabs.Count > 0)
        {
            int nextIdx = Math.Clamp(idx - 1, 0, _tabs.Count - 1);
            ActivateTab(_tabs[nextIdx]);
        }
        else
        {
            RebuildTabStrip();
            if (_tabs.Count == 0) ApplyActiveTabChrome();
        }
    }

    private void ActivateTab(ITabContent content)
    {
        if (ReferenceEquals(_activeTab, content))
        {
            if (ContentStage.Children.Count == 0 || !ReferenceEquals(ContentStage.Children[0], content.ViewElement))
            {
                ContentStage.Children.Clear();
                ContentStage.Children.Add(content.ViewElement);
            }
            L10n.ApplyTree(content.ViewElement);
            ApplyActiveTabChrome();
            UpdateWindowTitle();
            return;
        }

        _activeTab?.OnTabDeactivated();
        _activeTab = content;
        ContentStage.Children.Clear();
        ContentStage.Children.Add(content.ViewElement);
        L10n.ApplyTree(content.ViewElement);
        content.OnTabActivated();
        content.SetFullScreenChrome(_isFullScreen);
        ApplyActiveTabChrome();
        UpdateWindowTitle();
    }

    private void RebuildTabStrip()
    {
        TabStripPanel.Children.Clear();

        bool appDark = ThemeHelper.IsDark(Library.Prefs.ToElementTheme());
        var paper = _activeTab?.ActivePaperChrome;
        bool isDark = paper?.IsDark ?? appDark;
        var p = ThemeHelper.Palette(isDark);
        var tc = ThemeHelper.TintColors(Library.Prefs.Tint, isDark);

        Color activeBg = paper is not null
            ? paper.PaperBg
            : (isDark ? Color.FromArgb(255, 42, 40, 37) : Color.FromArgb(255, 255, 254, 252));
        Color activeBorder = paper is not null
            ? paper.Line
            : (isDark ? Color.FromArgb(34, 255, 255, 255) : Color.FromArgb(26, 42, 43, 46));
        Color hoverBg = paper is not null
            ? (paper.IsDark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, paper.Ink1.R, paper.Ink1.G, paper.Ink1.B))
            : (isDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(13, 42, 43, 46));
        Color textPrimary = paper is not null ? paper.Ink1 : p.T1;
        Color textMuted = paper is not null ? paper.Ink2 : p.T2;
        Color sepColor = paper is not null
            ? Color.FromArgb(36, paper.Ink2.R, paper.Ink2.G, paper.Ink2.B)
            : (isDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(26, 42, 43, 46));

        // Update + button visual to match active strip
        AddTabBtn.Content = YYIcons.Make("plus", 12, ThemeHelper.Brush(textMuted));
        AddTabBtn.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);
        AddTabBtn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(hoverBg);
        AddTabBtn.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(hoverBg);

        for (int i = 0; i < _tabs.Count; i++)
        {
            var tab = _tabs[i];
            bool active = ReferenceEquals(tab, _activeTab);
            bool nextActive = i + 1 < _tabs.Count && ReferenceEquals(_tabs[i + 1], _activeTab);

            var pill = BuildTabPill(tab, active, isDark, activeBg, activeBorder, hoverBg, textPrimary, textMuted, tc.Accent);
            TabStripPanel.Children.Add(pill);

            // Subtle vertical divider between adjacent inactive tabs (Windows 11 Fluent TabStrip rhythm)
            if (i + 1 < _tabs.Count && !active && !nextActive)
            {
                TabStripPanel.Children.Add(new Border
                {
                    Width = 1,
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = ThemeHelper.Brush(sepColor),
                    IsHitTestVisible = false,
                });
            }
        }
    }

    private Border BuildTabPill(
        ITabContent tab,
        bool active,
        bool dark,
        Color activeBg,
        Color activeBorder,
        Color hoverBg,
        Color textPrimary,
        Color textMuted,
        Color accentColor)
    {
        Color iconFg = tab.IsAudioPlaying
            ? accentColor
            : (active ? (tab.TabKind == "settings" ? accentColor : textPrimary) : textMuted);
        Color titleFg = active ? textPrimary : textMuted;

        var contentGrid = new Grid { ColumnSpacing = 7 };
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (!tab.IsPinned)
            contentGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = YYIcons.Make(tab.IsAudioPlaying ? "wave" : tab.TabIconName, 13.5, ThemeHelper.Brush(iconFg));
        icon.VerticalAlignment = VerticalAlignment.Center;
        contentGrid.Children.Add(icon);

        var titleTb = new TextBlock
        {
            Text = (tab.IsPinned || tab.TabKind == "settings") ? L10n.T(tab.TabTitle) : tab.TabTitle,
            FontSize = 12.5,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = ThemeHelper.Brush(titleFg),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (!tab.IsPinned && tab.TabKind != "settings") L10n.Skip(titleTb);
        Grid.SetColumn(titleTb, 1);
        contentGrid.Children.Add(titleTb);

        Button? closeBtn = null;
        if (!tab.IsPinned)
        {
            closeBtn = new Button
            {
                Width = 20,
                Height = 20,
                MinWidth = 20,
                MinHeight = 20,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                VerticalAlignment = VerticalAlignment.Center,
                UseSystemFocusVisuals = false,
                Opacity = active ? 0.72 : 0.0,
                Content = YYIcons.Make("xmark", 9, ThemeHelper.Brush(textMuted)),
            };
            closeBtn.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);
            closeBtn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(hoverBg);
            closeBtn.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(hoverBg);
            closeBtn.Click += (_, _) => _ = CloseTabAsync(tab);
            Grid.SetColumn(closeBtn, 2);
            contentGrid.Children.Add(closeBtn);
        }

        var pill = new Border
        {
            Height = 28,
            MinWidth = tab.IsPinned ? 84 : 112,
            MaxWidth = tab.IsPinned ? 128 : 228,
            Padding = tab.IsPinned ? new Thickness(11, 0, 11, 0) : new Thickness(10, 0, 5, 0),
            CornerRadius = new CornerRadius(6),
            Background = active ? ThemeHelper.Brush(activeBg) : new SolidColorBrush(Colors.Transparent),
            BorderBrush = active ? ThemeHelper.Brush(activeBorder) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = contentGrid,
            CanDrag = !tab.IsPinned,
            AllowDrop = true,
        };

        pill.PointerEntered += (_, _) =>
        {
            if (!active) pill.Background = ThemeHelper.Brush(hoverBg);
            if (closeBtn is not null) closeBtn.Opacity = 0.9;
        };
        pill.PointerExited += (_, _) =>
        {
            if (!active) pill.Background = new SolidColorBrush(Colors.Transparent);
            if (closeBtn is not null) closeBtn.Opacity = active ? 0.72 : 0.0;
        };
        pill.PointerPressed += (_, e) =>
        {
            var props = e.GetCurrentPoint(pill).Properties;
            if (!tab.IsPinned && props.IsMiddleButtonPressed)
            {
                e.Handled = true;
                _ = CloseTabAsync(tab);
                return;
            }
            if (props.IsLeftButtonPressed)
            {
                SelectTab(tab);
            }
        };

        // Drag-to-reorder tabs
        pill.DragStarting += (_, args) =>
        {
            _draggingTab = tab;
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        pill.DropCompleted += (_, _) => _draggingTab = null;
        pill.DragOver += (_, args) =>
        {
            if (_draggingTab is not null && !ReferenceEquals(_draggingTab, tab))
            {
                args.AcceptedOperation = DataPackageOperation.Move;
                args.Handled = true;
            }
        };
        pill.Drop += (_, args) =>
        {
            if (_draggingTab is null || ReferenceEquals(_draggingTab, tab) || _draggingTab.IsPinned) return;
            int fromIdx = _tabs.IndexOf(_draggingTab);
            int toIdx = _tabs.IndexOf(tab);
            if (fromIdx >= 0 && toIdx >= 0)
            {
                int clampedTo = Math.Max(_tabs[0].IsPinned ? 1 : 0, toIdx);
                _tabs.RemoveAt(fromIdx);
                _tabs.Insert(clampedTo, _draggingTab);
                RebuildTabStrip();
            }
            _draggingTab = null;
            args.Handled = true;
        };

        // Native Windows 11 tab context menu
        pill.RightTapped += (_, e) =>
        {
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
            if (!tab.IsPinned && _tabs.Count > 1)
            {
                var tearItem = new MenuFlyoutItem { Text = L10n.T("移至独立窗口") };
                tearItem.Click += (_, _) => TabTearOutRequested?.Invoke(this, tab);
                menu.Items.Add(tearItem);
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            if (!tab.IsPinned)
            {
                var closeItem = new MenuFlyoutItem { Text = L10n.T("关闭标签页"), KeyboardAcceleratorTextOverride = "Ctrl+W" };
                closeItem.Click += (_, _) => _ = CloseTabAsync(tab);
                menu.Items.Add(closeItem);
            }
            if (_tabs.Any(t => !t.IsPinned && !ReferenceEquals(t, tab)))
            {
                var closeOthers = new MenuFlyoutItem { Text = L10n.T("关闭其他标签页") };
                closeOthers.Click += async (_, _) =>
                {
                    foreach (var other in _tabs.Where(t => !t.IsPinned && !ReferenceEquals(t, tab)).ToArray())
                        await CloseTabAsync(other);
                };
                menu.Items.Add(closeOthers);
            }
            if (_closedTabHistory.Count > 0)
            {
                menu.Items.Add(new MenuFlyoutSeparator());
                var reopenItem = new MenuFlyoutItem { Text = L10n.T("重新打开关闭的标签页"), KeyboardAcceleratorTextOverride = "Ctrl+Shift+T" };
                reopenItem.Click += (_, _) => ReopenLastClosedTab();
                menu.Items.Add(reopenItem);
            }
            if (menu.Items.Count > 0)
                menu.ShowAt(pill, e.GetPosition(pill));
        };

        return pill;
    }

    private void UpdateWindowTitle()
    {
        if (_activeTab is null || _activeTab.IsPinned)
        {
            Title = L10n.T("语阅");
        }
        else if (_activeTab.TabKind == "settings")
        {
            Title = L10n.T("设置 · 语阅");
        }
        else
        {
            Title = $"{_activeTab.TabTitle} · {L10n.T("语阅")}";
        }
        FullScreenTitleText.Text = Title;
    }

    public void ApplyAppearance()
    {
        var theme = Library.Prefs.ToElementTheme();
        RootGrid.RequestedTheme = theme;
        bool dark = ThemeHelper.IsDark(theme);

        ThemeHelper.ConfigureWindowBackdrop(this, Library.Prefs.UseWarmMica);
        ThemeHelper.ApplyControlThemeResources(RootGrid.Resources, dark, Library.Prefs.Tint);
        ThemeHelper.ConfigureTitleBar(this, dark);

        DropOverlayIcon.Content = YYIcons.Make("book-open", 30, ThemeHelper.Brush(ThemeHelper.TintColors(Library.Prefs.Tint, dark).Accent));

        foreach (var tab in _tabs)
            tab.OnAppearanceChanged();

        ApplyActiveTabChrome();
        L10n.ApplyTree(RootGrid);
    }

    /// <summary>
    /// Synchronizes the 40px top tabstrip and native Windows 11 caption buttons with either
    /// the active Reader/PDF tab's reading paper color or Warm Mica Alt.
    /// </summary>
    public void ApplyActiveTabChrome()
    {
        bool appDark = ThemeHelper.IsDark(Library.Prefs.ToElementTheme());
        var paper = _activeTab?.ActivePaperChrome;

        if (paper is not null)
        {
            TitleBarChrome.Background = ThemeHelper.Brush(paper.TabStripBg);
            RootGrid.Background = ThemeHelper.Brush(paper.PaperBg);
            ThemeHelper.ConfigureTitleBarForPaper(this, paper.Ink1, paper.Ink2, paper.IsDark);
        }
        else
        {
            TitleBarChrome.Background = new SolidColorBrush(Colors.Transparent);
            Color micaFallback = Library.Prefs.UseWarmMica
                ? (appDark ? Color.FromArgb(220, 22, 21, 20) : Color.FromArgb(216, 242, 239, 234))
                : (appDark ? Color.FromArgb(255, 20, 19, 18) : Color.FromArgb(255, 239, 236, 230));
            RootGrid.Background = ThemeHelper.Brush(micaFallback);
            ThemeHelper.ConfigureTitleBar(this, appDark);
        }

        SyncTitleBarLeftSlot();
        RebuildTabStrip();
    }

    public void SyncTitleBarLeftSlot()
    {
        bool appDark = ThemeHelper.IsDark(Library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(appDark);
        var tc = ThemeHelper.TintColors(Library.Prefs.Tint, appDark);

        SidebarToggleBtn.Content = YYIcons.Make("nav-toggle", 14, ThemeHelper.Brush(p.T2));
        TitleBarBackIcon.Content = YYIcons.Make("arrow-left", 13.5, ThemeHelper.Brush(p.T1));
        BrandDoorPath.Fill = ThemeHelper.Brush(p.T1);
        BrandBarsPath.Stroke = ThemeHelper.Brush(tc.Accent);

        if (_activeTab is Yuyue.UI.Library.LibraryView lv)
        {
            bool compact = lv.IsNavCompact;
            TitleBarLeftSlotCol.Width = new GridLength(compact ? 54 : 214);
            TitleBarLeftSlot.Visibility = Visibility.Visible;
            LibraryHeaderHost.Visibility = Visibility.Visible;
            SettingsHeaderHost.Visibility = Visibility.Collapsed;
            BrandMarkPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            SidebarToggleBtn.HorizontalAlignment = compact ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            SidebarToggleBtn.Margin = compact ? new Thickness(0) : new Thickness(0, 0, 8, 0);
            TabStripScroller.Margin = new Thickness(0);
        }
        else if (_activeTab?.TabKind == "settings")
        {
            TitleBarLeftSlotCol.Width = new GridLength(214);
            TitleBarLeftSlot.Visibility = Visibility.Visible;
            LibraryHeaderHost.Visibility = Visibility.Collapsed;
            SettingsHeaderHost.Visibility = Visibility.Visible;
            TabStripScroller.Margin = new Thickness(0);
        }
        else
        {
            TitleBarLeftSlotCol.Width = new GridLength(0);
            TitleBarLeftSlot.Visibility = Visibility.Collapsed;
            TabStripScroller.Margin = new Thickness(8, 0, 0, 0);
        }
    }

    private void BrandMark_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is Yuyue.UI.Library.LibraryView lv)
            lv.SwitchTab("all");
    }

    private void SidebarToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is Yuyue.UI.Library.LibraryView lv)
            lv.ToggleNavCompactFromShell();
    }

    private void TitleBarBackBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_activeTab is Yuyue.UI.Settings.SettingsView sv)
            _ = CloseTabAsync(sv, recordHistory: false);
        else
            App.CurrentApp.ShowShelf();
    }

    public void ShowSettings(string page = "general")
    {
        var existing = _tabs.FirstOrDefault(t => t.TabKind == "settings");
        if (existing is Yuyue.UI.Settings.SettingsView sv)
        {
            sv.NavigateTo(page);
            SelectTab(sv);
            return;
        }

        if (SettingsTabFactory is not null)
        {
            var newSettings = SettingsTabFactory(page);
            AddTab(newSettings, select: true);
        }
    }

    public void CloseSettings()
    {
        var existing = _tabs.FirstOrDefault(t => t.TabKind == "settings");
        if (existing is not null)
            _ = CloseTabAsync(existing, recordHistory: false);
        else if (FindPinnedLibraryTab() is { } lib)
            SelectTab(lib);
    }

    public void ToggleFullScreen() => SetFullScreen(!_isFullScreen);

    public void SetFullScreen(bool fullScreen)
    {
        if (_isFullScreen == fullScreen) return;
        _isFullScreen = fullScreen;
        try
        {
            AppWindow.SetPresenter(fullScreen
                ? AppWindowPresenterKind.FullScreen
                : AppWindowPresenterKind.Overlapped);
        }
        catch { }

        TitleBarRow.Height = fullScreen ? new GridLength(0) : new GridLength(40);
        TitleBarChrome.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenPeekZone.Visibility = fullScreen ? Visibility.Visible : Visibility.Collapsed;
        if (!fullScreen) FullScreenPeekBar.Visibility = Visibility.Collapsed;

        foreach (var tab in _tabs)
            tab.SetFullScreenChrome(fullScreen);
    }

    public void ShowToast(string message, string? actionLabel = null, Action? action = null, int autoHideSeconds = 4)
    {
        ShellToastText.Text = L10n.T(message);
        _toastAction = action;
        if (!string.IsNullOrWhiteSpace(actionLabel) && action is not null)
        {
            ShellToastActionBtn.Content = L10n.T(actionLabel);
            ShellToastActionBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ShellToastActionBtn.Visibility = Visibility.Collapsed;
        }
        ShellToastBanner.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(Math.Max(2, autoHideSeconds)) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ShellToastBanner.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    public List<SavedTabEntry> CaptureOpenTabsState()
    {
        var result = new List<SavedTabEntry>();
        foreach (var tab in _tabs)
        {
            if (tab.IsPinned) continue;
            if (tab.SerializeTabState() is { } entry)
                result.Add(entry);
        }
        return result;
    }

    public void ReopenLastClosedTab()
    {
        if (_closedTabHistory.Count == 0)
        {
            ShowToast("没有可恢复的最近关闭标签页");
            return;
        }
        var entry = _closedTabHistory.Pop();
        App.CurrentApp.OpenBook(entry.BookId);
    }

    private void AddTabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xr)
            _ = QuickSearchDialog.ShowAsync(xr, Library, _activeTab?.BookId);
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        bool inTextInput = FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox or PasswordBox or RichEditBox;

        if (e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            if (_isFullScreen)
            {
                SetFullScreen(false);
                e.Handled = true;
                return;
            }
            if (IsSettingsOpen && !inTextInput)
            {
                CloseSettings();
                e.Handled = true;
                return;
            }
        }

        if (ctrl && !alt)
        {
            if (e.Key == (VirtualKey)188) // Ctrl+,
            {
                if (IsSettingsOpen) CloseSettings();
                else ShowSettings("general");
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.K)
            {
                if (RootGrid.XamlRoot is { } xr)
                    _ = QuickSearchDialog.ShowAsync(xr, Library, _activeTab?.BookId);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.W && !shift)
            {
                if (_activeTab is not null && !_activeTab.IsPinned)
                {
                    _ = CloseTabAsync(_activeTab);
                    e.Handled = true;
                    return;
                }
            }

            if (e.Key == VirtualKey.T && shift)
            {
                ReopenLastClosedTab();
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Tab && _tabs.Count > 1)
            {
                int cur = _activeTab is not null ? _tabs.IndexOf(_activeTab) : 0;
                int next = shift ? (cur - 1 + _tabs.Count) % _tabs.Count : (cur + 1) % _tabs.Count;
                SelectTab(_tabs[next]);
                e.Handled = true;
                return;
            }

            if (!shift && e.Key >= VirtualKey.Number1 && e.Key <= VirtualKey.Number9)
            {
                int num = (int)e.Key - (int)VirtualKey.Number1;
                if (e.Key == VirtualKey.Number9 && _tabs.Count > 0)
                    SelectTab(_tabs[^1]);
                else if (num < _tabs.Count)
                    SelectTab(_tabs[num]);
                e.Handled = true;
                return;
            }
        }

        if (!inTextInput && _activeTab is not null)
        {
            if (_activeTab.HandleWindowKeyDown(e.Key, ctrl, shift, alt))
                e.Handled = true;
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            DropOverlayText.Text = L10n.T("松开即可导入到语阅书库");
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(it => it.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
            if (paths.Count > 0 && FilesDroppedHandler is not null)
                await FilesDroppedHandler(paths);
        }
        catch { }
        finally
        {
            deferral.Complete();
        }
    }

    private void FullScreenPeek_Entered(object sender, PointerRoutedEventArgs e)
    {
        if (_isFullScreen) FullScreenPeekBar.Visibility = Visibility.Visible;
    }

    private void FullScreenPeek_Exited(object sender, PointerRoutedEventArgs e)
    {
        FullScreenPeekBar.Visibility = Visibility.Collapsed;
    }

    private void ExitFullScreen_Click(object sender, RoutedEventArgs e) => SetFullScreen(false);

    private void ShellToastAction_Click(object sender, RoutedEventArgs e)
    {
        ShellToastBanner.Visibility = Visibility.Collapsed;
        _toastTimer?.Stop();
        _toastAction?.Invoke();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (IsPrimaryShell)
            SaveWindowGeometry();

        if (!IsPrimaryShell || _closingInternally || App.CurrentApp.IsExitingApp)
            return;

        string behavior = Library.Prefs.CloseWindowBehavior;
        bool shouldHideToTray = behavior == "always_tray"
            || (behavior == "tray_when_playing" && Library.SystemIntegration.IsAudioPlaying)
            || (Library.Prefs.AutoLaunchOnLogin && behavior != "exit");

        if (shouldHideToTray)
        {
            args.Cancel = true;
            Library.SystemIntegration.EnsureTrayVisible(forceVisible: true);
            try { AppWindow.Hide(); } catch { }
            ShellWindowClosingToTray?.Invoke(this);
            return;
        }

        if (Library.Prefs.RestoreLastTabs)
        {
            Library.Prefs.LastOpenTabs = CaptureOpenTabsState();
            Library.SavePrefs();
        }
    }

    private void SaveWindowGeometry()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter op)
                Library.Prefs.MainWindowMaximized = op.State == OverlappedPresenterState.Maximized;
            if (ThemeHelper.TryGetRestoredDipSize(this, out int w, out int h))
            {
                Library.Prefs.MainWindowWidth = w;
                Library.Prefs.MainWindowHeight = h;
            }
            Library.SavePrefs();
        }
        catch { }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        Library.SystemIntegration.PlaybackStateChanged -= OnPlaybackStateChanged;
        _toastTimer?.Stop();
        _thumbToolbar?.Dispose();
        if (_subclassAttached && _hwnd != IntPtr.Zero)
        {
            lock (s_windowByHwnd) s_windowByHwnd.Remove(_hwnd);
            try { RemoveWindowSubclass(_hwnd, s_subclassDelegate, ShellSubclassId); } catch { }
        }
        foreach (var tab in _tabs.ToArray())
        {
            if (_tabMetaHandlers.Remove(tab, out var h))
                tab.TabMetaChanged -= h;
            if (tab is IMiniPlayerSource mps)
                App.CurrentApp.ClearActiveMiniPlayerSource(mps);
            try { tab.Dispose(); } catch { }
        }
        _tabMetaHandlers.Clear();
        _tabs.Clear();
    }

    public void ForceCloseWindow()
    {
        _closingInternally = true;
        Close();
    }

    public async Task<bool> SaveCompositeScreenshotAsync(string path)
    {
        try
        {
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(RootGrid);
            var pixelBuffer = await rtb.GetPixelsAsync();
            byte[] dst = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixelBuffer);
            int dstW = rtb.PixelWidth;
            int dstH = rtb.PixelHeight;
            double scaleX = RootGrid.ActualWidth > 0 ? dstW / RootGrid.ActualWidth : 1.0;
            double scaleY = RootGrid.ActualHeight > 0 ? dstH / RootGrid.ActualHeight : 1.0;

            if (_activeTab is Yuyue.UI.Reader.ReaderTabView rtv)
            {
                foreach (var (hostEl, wvHost) in rtv.GetWebViewCaptureTargets())
                {
                    if (hostEl is not FrameworkElement fe || fe.ActualWidth < 20 || fe.ActualHeight < 20) continue;
                    string tmpPng = Path.Combine(Path.GetTempPath(), $"yuyue-wv-{Guid.NewGuid():N}.png");
                    try
                    {
                        await wvHost.CapturePreviewPngAsync(tmpPng);
                        if (!File.Exists(tmpPng)) continue;
                        var pt = fe.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0));
                        int ox = Math.Max(0, (int)Math.Round(pt.X * scaleX));
                        int oy = Math.Max(0, (int)Math.Round(pt.Y * scaleY));
                        int targetW = Math.Min(dstW - ox, (int)Math.Round(fe.ActualWidth * scaleX));
                        int targetH = Math.Min(dstH - oy, (int)Math.Round(fe.ActualHeight * scaleY));
                        if (targetW <= 0 || targetH <= 0) continue;

                        var sf = await Windows.Storage.StorageFile.GetFileFromPathAsync(tmpPng);
                        using var s = await sf.OpenReadAsync();
                        var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(s);
                        var tr = new Windows.Graphics.Imaging.BitmapTransform
                        {
                            ScaledWidth = (uint)targetW,
                            ScaledHeight = (uint)targetH,
                            InterpolationMode = Windows.Graphics.Imaging.BitmapInterpolationMode.Linear,
                        };
                        var pd = await dec.GetPixelDataAsync(
                            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                            tr,
                            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
                        byte[] src = pd.DetachPixelData();
                        for (int y = 0; y < targetH; y++)
                        {
                            int srcRow = y * targetW * 4;
                            int dstRow = ((oy + y) * dstW + ox) * 4;
                            Buffer.BlockCopy(src, srcRow, dst, dstRow, targetW * 4);
                        }
                    }
                    catch { }
                    finally
                    {
                        try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch { }
                    }
                }
            }

            // Composite any open modal ContentDialog (e.g. QuickSearchDialog) from PopupRoot over the window capture
            if (RootGrid.XamlRoot is { } xr)
            {
                foreach (var pop in VisualTreeHelper.GetOpenPopupsForXamlRoot(xr))
                {
                    if (pop.Child is not ContentDialog dlg || dlg.ActualWidth < 20 || dlg.ActualHeight < 20) continue;
                    try
                    {
                        var pRtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                        await pRtb.RenderAsync(dlg);
                        var pBuf = await pRtb.GetPixelsAsync();
                        byte[] src = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pBuf);
                        int pw = pRtb.PixelWidth;
                        int ph = pRtb.PixelHeight;
                        int ox = Math.Max(0, (dstW - pw) / 2);
                        int oy = Math.Max(0, (dstH - ph) / 2);
                        // Subtle Windows 11 modal smoke layer
                        for (int i = 0; i < dst.Length; i += 4)
                        {
                            dst[i] = (byte)(dst[i] * 0.72);
                            dst[i + 1] = (byte)(dst[i + 1] * 0.72);
                            dst[i + 2] = (byte)(dst[i + 2] * 0.72);
                        }
                        int cw = Math.Min(pw, dstW - ox);
                        int ch = Math.Min(ph, dstH - oy);
                        for (int y = 0; y < ch; y++)
                        {
                            for (int x = 0; x < cw; x++)
                            {
                                int si = (y * pw + x) * 4;
                                int di = ((oy + y) * dstW + (ox + x)) * 4;
                                byte sa = src[si + 3];
                                if (sa == 0) continue;
                                int invA = 255 - sa;
                                dst[di] = (byte)Math.Min(255, src[si] + (dst[di] * invA) / 255);
                                dst[di + 1] = (byte)Math.Min(255, src[si + 1] + (dst[di + 1] * invA) / 255);
                                dst[di + 2] = (byte)Math.Min(255, src[si + 2] + (dst[di + 2] * invA) / 255);
                                dst[di + 3] = 255;
                            }
                        }
                    }
                    catch { }
                }
            }

            using var outStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var enc = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, outStream);
            enc.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)dstW, (uint)dstH, 96, 96, dst);
            await enc.FlushAsync();
            outStream.Seek(0);
            byte[] outBytes = new byte[outStream.Size];
            await outStream.ReadAsync(
                System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(outBytes),
                (uint)outBytes.Length,
                Windows.Storage.Streams.InputStreamOptions.None);
            await File.WriteAllBytesAsync(path, outBytes);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }
}
