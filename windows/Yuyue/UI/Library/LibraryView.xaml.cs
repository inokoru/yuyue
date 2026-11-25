using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Podcast;
using Yuyue.Shelf;
using Yuyue.UI.Shell;

namespace Yuyue.UI.Library;

/// <summary>
/// Brand-new native Windows 11 Fluent 2 Library workspace hosted as the pinned Tab 1 ("书库")
/// inside <see cref="MainWindow"/>. Implements both <see cref="ITabContent"/> and <see cref="ILibraryHost"/>.
/// </summary>
public sealed partial class LibraryView : UserControl, ITabContent, ILibraryHost
{
    private readonly MainWindow _ownerWindow;
    private readonly LibraryService _library;
    private readonly BookRecordPageBuilder _recordBuilder;
    private readonly PodcastWorkspaceBuilder _podcastBuilder;

    private IReadOnlyList<BookSummary> _shelfBooks = [];
    private IReadOnlyList<BookSummary> _keptBooks = [];
    private IReadOnlyList<BookCollection> _collections = [];
    private Dictionary<string, uint> _markCounts = [];
    private IReadOnlyList<VocabWord> _words = [];
    private HashSet<string> _pairedBookIds = [];

    private string _activeSection = "all"; // all | progress | podcasts | audio | notes | words | collections | col:<id>
    private string _statusFilter = "all";  // all | reading | want | finished
    private string _formatFilter = "all";  // all | epub | pdf | txt | audio
    private string _inlineQuery = "";
    private string _sortKey = "recent";    // recent | title | author | progress | added
    private bool _sortAsc;
    private bool _navCompact;
    private bool _suppressZoomEvent;
    private bool _disposed;

    private readonly HashSet<string> _selectedBookIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action> _selectionVisualUpdaters = [];
    private readonly Dictionary<string, (Button RowBtn, Border Indicator, TextBlock Label, TextBlock? Badge, ContentPresenter Icon)> _navControls = new();

    private Border? _podcastCustomDock;
    private double _lastRenderWidth;
    private int _lastCatalogFilteredCount;
    private int _lastCatalogReadingCount;

    // ITabContent implementation
    public string TabId => "pinned-library";
    public string TabTitle => "书库";
    public string? BookId => null;
    public string TabKind => "library";
    public string TabIconName => "shelf";
    public bool IsPinned => true;
    public bool IsAudioPlaying => _library.SystemIntegration.IsAudioPlaying && _podcastBuilder.IsPlaying;
    public PaperChromeTheme? ActivePaperChrome => null;
    public UIElement ViewElement => this;
    public event Action? TabMetaChanged;

    // ILibraryHost implementation
    public Window OwnerWindow => _ownerWindow;
    public XamlRoot? WindowXamlRoot => RootGrid.XamlRoot;
    UIElement? ILibraryHost.Content => RootGrid;
    public double SubPageAvailableWidth => Math.Max(420, (MainScroller.ActualWidth > 0 ? MainScroller.ActualWidth : 920) - 48);

    public LibraryView(MainWindow ownerWindow, LibraryService library)
    {
        _ownerWindow = ownerWindow;
        _library = library;
        InitializeComponent();

        _recordBuilder = new BookRecordPageBuilder(this, library);
        _podcastBuilder = new PodcastWorkspaceBuilder(this, library);

        _navCompact = _library.Prefs.NavPaneCompact;
        _sortKey = string.IsNullOrWhiteSpace(_library.Prefs.BooksSort) ? "recent" : _library.Prefs.BooksSort;
        _sortAsc = string.Equals(_library.Prefs.ShelfSortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        _suppressZoomEvent = true;
        CoverZoomSlider.Value = Math.Clamp(_library.Prefs.CoverWidth, 78, 148);
        _suppressZoomEvent = false;

        BuildImportSplitFlyout();
        BuildSortFlyout();
        BuildBatchStatusFlyout();

        _library.SystemIntegration.PlaybackStateChanged += OnPlaybackStateChanged;
        RootGrid.SizeChanged += (_, args) =>
        {
            double w = args.NewSize.Width;
            InlineFilterBox.Width = w < 1060 ? 138 : 188;
        };

        ApplyNavCompactState();
        ApplyAppearance();
        RefreshBooks();
    }

    public void SwitchTab(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) section = "all";
        section = section.Trim().ToLowerInvariant() switch
        {
            "audiobooks" or "audiobook" => "audio",
            "books" or "shelf" => "all",
            "podcast" => "podcasts",
            _ => section,
        };
        _activeSection = section;
        _selectedBookIds.Clear();
        UpdateNavSelectionVisuals();
        RenderCurrentSection();
    }

    public void OpenBookRecord(string bookId, string? subTab = null)
    {
        _recordBuilder.OpenBookId = bookId;
        if (!string.IsNullOrWhiteSpace(subTab)) _recordBuilder.SetSubTab(subTab);
        SwitchTab("progress");
    }

    public void OnTabActivated()
    {
        RefreshBooks();
    }

    public void OnTabDeactivated() { }

    public void OnAppearanceChanged() => ApplyAppearance();

    public void OnReaderSettingsChanged() { }

    public void OnVoiceChanged() { }

    public void OnForeignPrefsChanged() { }

    public void OnSnapshotRestored()
    {
        ApplyAppearance();
        RefreshBooks();
    }

    public void SetFullScreenChrome(bool isFullScreen) { }

    public SavedTabEntry? SerializeTabState() => null;

    public Task<bool> PrepareCloseAsync() => Task.FromResult(false);

    public async Task<bool> SaveCompositeScreenshotAsync(string path)
    {
        try
        {
            await global::Yuyue.Reader.QuoteShareDialog.SaveElementPngAsync(RootGrid, path);
            return File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public bool HandleMouseBackForward(bool isBack)
    {
        if (!isBack) return false;
        if (_activeSection == "progress" && _recordBuilder.OpenBookId is not null)
        {
            _recordBuilder.OpenBookId = null;
            RenderCurrentSection();
            return true;
        }
        if (_activeSection == "podcasts" && _podcastBuilder.OpenIssueId is not null)
        {
            _podcastBuilder.OpenIssueId = null;
            RenderCurrentSection();
            return true;
        }
        if (_activeSection.StartsWith("col:", StringComparison.Ordinal))
        {
            SwitchTab("collections");
            return true;
        }
        return false;
    }

    public bool HandleWindowKeyDown(VirtualKey key, bool ctrl, bool shift, bool alt)
    {
        if (_activeSection == "podcasts" && !ctrl && !alt && _podcastBuilder.HandleKeyDown(key))
            return true;

        if (ctrl && !alt)
        {
            if (key == VirtualKey.B)
            {
                ToggleNavCompact();
                return true;
            }
            if (key == VirtualKey.E)
            {
                if (IsBooksSection())
                {
                    InlineFilterBox.Focus(FocusState.Programmatic);
                    InlineFilterBox.SelectAll();
                    return true;
                }
            }
            if (key == VirtualKey.O)
            {
                _ = PickAndImportBooksAsync();
                return true;
            }
            if (key == VirtualKey.A && IsBooksSection())
            {
                SelectAllVisibleBooks();
                return true;
            }
        }

        if (IsBooksSection() && _selectedBookIds.Count > 0)
        {
            if (key == VirtualKey.Escape)
            {
                _selectedBookIds.Clear();
                UpdateBatchBarAndSelectionVisuals();
                return true;
            }
            if (key == VirtualKey.F2 && _selectedBookIds.Count == 1)
            {
                var book = _keptBooks.FirstOrDefault(b => _selectedBookIds.Contains(b.Id));
                if (book is not null) _ = ShowEditBookDialogAsync(book);
                return true;
            }
            if (key == VirtualKey.Delete)
            {
                var selected = _keptBooks.Where(b => _selectedBookIds.Contains(b.Id)).ToList();
                if (selected.Count > 0) _ = HandleDeleteBooksAsync(selected);
                return true;
            }
            if (key == VirtualKey.Enter)
            {
                OpenSelectedBooksInTabs();
                return true;
            }
        }
        return false;
    }

    public void ApplyAppearance()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        QuickSearchIcon.Content = YYIcons.Make("search", 13, ThemeHelper.Brush(p.T2));
        SidebarAddCollectionBtn.Content = YYIcons.Make("plus", 11.5, ThemeHelper.Brush(p.T3));
        IconFeedback.Content = YYIcons.Make("feedback", 14, ThemeHelper.Brush(p.T2));
        IconSettings.Content = YYIcons.Make("gear", 14, ThemeHelper.Brush(p.T2));
        ImportBtnIcon.Content = YYIcons.Make("plus", 12.5, ThemeHelper.Brush(Colors.White));
        ImportDropdownIcon.Content = YYIcons.Make("chevron-down", 10, ThemeHelper.Brush(Colors.White));
        ImportSplitBorder.Background = ThemeHelper.Brush(tc.Fill);
        Color accentHover = Color.FromArgb(28, 0, 0, 0);
        Color accentPress = Color.FromArgb(48, 0, 0, 0);
        foreach (var b in new[] { ImportPrimaryBtn, ImportDropdownBtn })
        {
            b.Resources["ButtonBackground"] = new SolidColorBrush(Colors.Transparent);
            b.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(accentHover);
            b.Resources["ButtonBackgroundPressed"] = ThemeHelper.Brush(accentPress);
            b.Resources["ButtonForeground"] = ThemeHelper.Brush(Colors.White);
            b.Resources["ButtonForegroundPointerOver"] = ThemeHelper.Brush(Colors.White);
            b.Resources["ButtonForegroundPressed"] = ThemeHelper.Brush(Colors.White);
        }
        GridViewBtn.Content = YYIcons.Make("grid-view", 13.5, ThemeHelper.Brush(p.T2));
        ListViewBtn.Content = YYIcons.Make("list-view", 13.5, ThemeHelper.Brush(p.T2));
        SortBtnIcon.Content = YYIcons.Make(_sortAsc ? "sort-asc" : "sort-desc", 13, ThemeHelper.Brush(p.T2));

        RebuildNavItems();
        UpdateViewToggleButtons();
        UpdateClickModePill();
        UpdateSyncFooter();
        UpdateGlobalNowPlayingDock();
        RenderCurrentSection();
        L10n.ApplyTree(RootGrid);
    }

    private void RebuildNavItems()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        _navControls.Clear();
        NavLibraryStack.Children.Clear();
        NavNotesStack.Children.Clear();
        NavCollectionsStack.Children.Clear();

        void AddNavItem(Panel host, string key, string icon, string label, int count, bool acceptBookDrop = false)
        {
            var indicator = new Border
            {
                Width = 3,
                Height = 16,
                CornerRadius = new CornerRadius(1.5),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(-6, 0, 0, 0),
                Opacity = 0,
            };

            var iconHost = new ContentPresenter
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = YYIcons.Make(icon, 15, ThemeHelper.Brush(p.T2)),
            };
            var labelTb = new TextBlock
            {
                Text = key.StartsWith("col:", StringComparison.Ordinal) ? label : L10n.T(label),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = ThemeHelper.Brush(p.T1),
                Visibility = _navCompact ? Visibility.Collapsed : Visibility.Visible,
            };
            if (key.StartsWith("col:", StringComparison.Ordinal)) L10n.Skip(labelTb);

            var badgeTb = new TextBlock
            {
                Text = count > 0 ? count.ToString() : "",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = (!_navCompact && count > 0) ? Visibility.Visible : Visibility.Collapsed,
            };

            var grid = new Grid { ColumnSpacing = _navCompact ? 0 : 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var rootGrid = new Grid();
            Grid.SetColumn(iconHost, 0);
            Grid.SetColumn(labelTb, 1);
            Grid.SetColumn(badgeTb, 2);
            grid.Children.Add(iconHost);
            grid.Children.Add(labelTb);
            grid.Children.Add(badgeTb);
            rootGrid.Children.Add(indicator);
            rootGrid.Children.Add(grid);

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYSidebarItemStyle"],
                Content = rootGrid,
                Tag = key,
                Padding = _navCompact ? new Thickness(9, 0, 0, 0) : new Thickness(10, 0, 10, 0),
            };
            ToolTipService.SetToolTip(btn, key.StartsWith("col:", StringComparison.Ordinal) ? label : L10n.T(label));
            btn.Click += (_, _) => SwitchTab(key);

            if (acceptBookDrop && key.StartsWith("col:", StringComparison.Ordinal))
            {
                string colId = key["col:".Length..];
                btn.AllowDrop = true;
                btn.DragOver += (_, e) =>
                {
                    if (e.DataView.Contains(StandardDataFormats.Text))
                        e.AcceptedOperation = DataPackageOperation.Copy;
                };
                btn.Drop += async (_, e) =>
                {
                    if (!e.DataView.Contains(StandardDataFormats.Text)) return;
                    try
                    {
                        string text = await e.DataView.GetTextAsync();
                        if (text.StartsWith("yuyue-book:", StringComparison.Ordinal))
                        {
                            string bid = text["yuyue-book:".Length..];
                            _library.SetInCollection(colId, bid, true);
                            RefreshBooks();
                            ShowToast("已加入合集");
                        }
                    }
                    catch { }
                };
            }

            host.Children.Add(btn);
            _navControls[key] = (btn, indicator, labelTb, badgeTb, iconHost);
        }

        int totalBooks = _shelfBooks.Count;
        int audioCount = _shelfBooks.Count(b => b.Format == BookFormat.Audio);
        int podcastCount = 0;
        try { podcastCount = _library.Core.PodcastIssues(null).Length; } catch { }
        int notesCount = 0;
        foreach (var kv in _markCounts) notesCount += (int)kv.Value;

        AddNavItem(NavLibraryStack, "all", "shelf", "全部书籍", totalBooks);
        AddNavItem(NavLibraryStack, "progress", "book", "阅读进度", _keptBooks.Count(b => b.Status == ReadingStatus.Reading));
        AddNavItem(NavLibraryStack, "podcasts", "radio", "播客", podcastCount);
        AddNavItem(NavLibraryStack, "audio", "headphones", "有声书", audioCount);

        AddNavItem(NavNotesStack, "notes", "square.and.pencil", "笔记", notesCount);
        AddNavItem(NavNotesStack, "words", "a-wen", "生词本", _words.Count);

        AddNavItem(NavCollectionsStack, "collections", "stack", "全部合集", _collections.Count);
        var orderedCols = GetOrderedCollections();
        for (int ci = 0; ci < Math.Min(orderedCols.Count, 12); ci++)
        {
            var col = orderedCols[ci];
            string icon = string.IsNullOrWhiteSpace(col.Icon) ? "folder" : col.Icon;
            string navKey = $"col:{col.Id}";
            AddNavItem(NavCollectionsStack, navKey, icon, col.Name, (int)col.Count, acceptBookDrop: true);
            if (_navControls.TryGetValue(navKey, out var navCtrl))
                navCtrl.RowBtn.ContextFlyout = BuildCollectionContextMenu(col, dark, ci, orderedCols.Count);
        }

        UpdateNavSelectionVisuals();
    }

    private void UpdateNavSelectionVisuals()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        Color selectedBg = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 42, 43, 46);

        foreach (var (key, ctrl) in _navControls)
        {
            bool active = string.Equals(key, _activeSection, StringComparison.OrdinalIgnoreCase);
            ctrl.RowBtn.Background = active ? ThemeHelper.Brush(selectedBg) : new SolidColorBrush(Colors.Transparent);
            ctrl.Indicator.Background = ThemeHelper.Brush(tc.Accent);
            ctrl.Indicator.Opacity = active ? 1.0 : 0.0;
            ctrl.Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            ctrl.Label.Foreground = ThemeHelper.Brush(active ? p.T1 : p.T2);
        }
    }

    public bool IsNavCompact => _navCompact;
    public void ToggleNavCompactFromShell() => ToggleNavCompact();

    private void ToggleNavCompact()
    {
        _navCompact = !_navCompact;
        _library.Prefs.NavPaneCompact = _navCompact;
        _library.SavePrefs();
        ApplyNavCompactState();
        RebuildNavItems();
    }

    private void ApplyNavCompactState()
    {
        NavColumn.Width = new GridLength(_navCompact ? 54 : 214);
        var labelVis = _navCompact ? Visibility.Collapsed : Visibility.Visible;
        QuickSearchLabel.Visibility = labelVis;
        QuickSearchHint.Visibility = labelVis;
        QuickSearchBtn.HorizontalAlignment = _navCompact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        QuickSearchBtn.HorizontalContentAlignment = _navCompact ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        QuickSearchBtn.Width = _navCompact ? 36 : double.NaN;
        QuickSearchBtn.Padding = _navCompact ? new Thickness(0) : new Thickness(10, 0, 10, 0);
        QuickSearchGrid.ColumnSpacing = _navCompact ? 0 : 7;
        GroupLabelLibrary.Visibility = labelVis;
        GroupLabelNotes.Visibility = labelVis;
        GroupCollectionsHeader.Visibility = labelVis;
        SyncFooterLabel.Visibility = labelVis;
        FeedbackFooterLabel.Visibility = labelVis;
        SettingsFooterLabel.Visibility = labelVis;
        SettingsFooterHint.Visibility = labelVis;
        _ownerWindow?.SyncTitleBarLeftSlot();
    }

    public void RefreshBooks()
    {
        try
        {
            var (shelf, kept) = _library.GetShelfAndKeptBooks();
            _shelfBooks = shelf;
            _keptBooks = kept;
            _collections = _library.GetCollections();
            _markCounts = _library.GetMarkCounts();
            _words = _library.GetWords();
            _pairedBookIds = [];
            try
            {
                foreach (var pair in _library.Core.ListeningPairs())
                {
                    _pairedBookIds.Add(pair.Book);
                    _pairedBookIds.Add(pair.Recording);
                }
            }
            catch { }
        }
        catch { }

        // Prune stale selections
        var validIds = _keptBooks.Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _selectedBookIds.RemoveWhere(id => !validIds.Contains(id));

        RebuildNavItems();
        UpdateSyncFooter();
        UpdateGlobalNowPlayingDock();
        RenderCurrentSection();
    }

    public void RefreshCurrentSubPage() => RenderCurrentSection();

    public void UpdateSyncFooter()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        if (!_library.Sync.IsEnabled)
        {
            SyncStatusDot.Fill = ThemeHelper.Brush(p.T3);
            SyncFooterLabel.Text = L10n.T("未开启同步");
            return;
        }
        if (_library.Sync.State == SyncStateKind.Syncing)
        {
            SyncStatusDot.Fill = ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, dark).Accent);
            SyncFooterLabel.Text = L10n.T("正在同步…");
            return;
        }
        if (_library.Sync.State == SyncStateKind.Failed)
        {
            SyncStatusDot.Fill = ThemeHelper.Brush(p.Danger);
            SyncFooterLabel.Text = L10n.T("同步异常 · 点击查看");
            return;
        }
        SyncStatusDot.Fill = ThemeHelper.Brush(p.Ok);
        SyncFooterLabel.Text = _library.Prefs.LastSyncedUnixMs is long ms
            ? FormatRelativeSyncTime(ms)
            : L10n.T("已开启同步");
    }

    private static string FormatRelativeSyncTime(long unixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        var span = DateTime.Now - dt;
        if (span.TotalMinutes < 1) return L10n.T("刚刚同步");
        if (span.TotalMinutes < 60) return L10n.T($"{(int)span.TotalMinutes} 分钟前同步");
        if (dt.Date == DateTime.Today) return L10n.T($"今天 {dt:HH:mm} 同步");
        return L10n.T($"{dt:M月d日} 同步");
    }

    private bool IsBooksSection() =>
        _activeSection is "all" or "audio" || _activeSection.StartsWith("col:", StringComparison.Ordinal);

    private void RenderCurrentSection()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        bool isBooks = IsBooksSection();

        HeaderLeftExtras.Children.Clear();
        HeaderRightExtras.Children.Clear();
        StatusPillsPanel.Children.Clear();
        BooksToolbarControls.Visibility = isBooks ? Visibility.Visible : Visibility.Collapsed;
        CoverZoomHost.Visibility = (isBooks && _library.Prefs.ShelfViewMode != "list") ? Visibility.Visible : Visibility.Collapsed;
        ClickModeToggleBtn.Visibility = isBooks ? Visibility.Visible : Visibility.Collapsed;
        BatchCommandBar.Visibility = (isBooks && _selectedBookIds.Count > 0) ? Visibility.Visible : Visibility.Collapsed;

        if (_activeSection != "podcasts")
        {
            _podcastCustomDock = null;
            UpdateGlobalNowPlayingDock();
        }

        _selectionVisualUpdaters.Clear();
        MainContentStack.Children.Clear();

        switch (_activeSection)
        {
            case "all":
                PageTitleText.Text = L10n.T("全部书籍");
                BuildStatusFilterPills(dark);
                RenderBooksCatalog(_shelfBooks, showContinueReading: true, dark);
                break;

            case "audio":
                PageTitleText.Text = L10n.T("有声书");
                BuildStatusFilterPills(dark);
                RenderBooksCatalog(_shelfBooks.Where(b => b.Format == BookFormat.Audio).ToArray(), showContinueReading: false, dark);
                break;

            case "progress":
                PageTitleText.Text = L10n.T("阅读进度");
                _recordBuilder.RenderInto(MainContentStack, _keptBooks, dark);
                StatusLeftText.Text = L10n.T($"共 {_keptBooks.Count} 本书 · 在读 {_keptBooks.Count(b => b.Status == ReadingStatus.Reading)} 本");
                break;

            case "podcasts":
                PageTitleText.Text = L10n.T("播客");
                _podcastBuilder.PopulateTopHeader(HeaderLeftExtras, HeaderRightExtras, dark);
                _podcastBuilder.RenderInto(MainContentStack, dark);
                StatusLeftText.Text = L10n.T("语阅播客与双语精听");
                break;

            case "notes":
                PageTitleText.Text = L10n.T("笔记与划线");
                RenderNotesSubView(dark);
                break;

            case "words":
                PageTitleText.Text = L10n.T("生词本");
                RenderWordsSubView(dark);
                break;

            case "collections":
                PageTitleText.Text = L10n.T("合集");
                RenderCollectionsSubView(dark);
                break;

            default:
                if (_activeSection.StartsWith("col:", StringComparison.Ordinal))
                {
                    string cid = _activeSection["col:".Length..];
                    var col = _collections.FirstOrDefault(c => c.Id == cid);
                    PageTitleText.Text = col?.Name ?? L10n.T("合集");
                    if (col is not null) L10n.Skip(PageTitleText);
                    var books = _keptBooks.Where(b => b.Collections.Contains(cid, StringComparer.OrdinalIgnoreCase)).ToArray();
                    BuildStatusFilterPills(dark);
                    RenderBooksCatalog(books, showContinueReading: false, dark);
                }
                break;
        }

        UpdateBatchBarAndSelectionVisuals();
    }

    private void BuildStatusFilterPills(bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        (string Id, string Label)[] pills =
        [
            ("all", "全部"),
            ("reading", "在读"),
            ("want", "想读"),
            ("finished", "读完"),
        ];

        var wrap = new Border
        {
            Height = 32,
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (id, label) in pills)
        {
            bool on = _statusFilter == id;
            var btn = new Button
            {
                Content = L10n.T(label),
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(on ? 1 : 0),
                BorderBrush = on ? ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)) : null,
                FontSize = 12,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                Foreground = ThemeHelper.Brush(on ? tc.Accent : p.T2),
                UseSystemFocusVisuals = false,
            };
            string cap = id;
            btn.Click += (_, _) =>
            {
                _statusFilter = cap;
                RenderCurrentSection();
            };
            row.Children.Add(btn);
        }
        wrap.Child = row;
        StatusPillsPanel.Children.Add(wrap);
    }

    private List<BookSummary> FilterAndSortBooks(IReadOnlyList<BookSummary> source)
    {
        IEnumerable<BookSummary> q = source;

        if (_statusFilter != "all")
        {
            q = _statusFilter switch
            {
                "reading" => q.Where(b => b.Status == ReadingStatus.Reading),
                "want" => q.Where(b => b.Status == ReadingStatus.Want),
                "finished" => q.Where(b => b.Status == ReadingStatus.Finished),
                _ => q,
            };
        }

        if (_formatFilter != "all")
        {
            q = _formatFilter switch
            {
                "epub" => q.Where(b => b.Format is BookFormat.Epub or BookFormat.Mobi),
                "pdf" => q.Where(b => b.Format == BookFormat.Pdf),
                "txt" => q.Where(b => b.Format == BookFormat.Txt),
                "audio" => q.Where(b => b.Format == BookFormat.Audio),
                _ => q,
            };
        }

        if (!string.IsNullOrWhiteSpace(_inlineQuery))
        {
            string term = _inlineQuery.Trim();
            q = q.Where(b =>
                NameMatcher.Matches(b.Title, term) ||
                b.Authors.Any(a => NameMatcher.Matches(a, term)));
        }

        IOrderedEnumerable<BookSummary> ordered = _sortKey switch
        {
            "title" => _sortAsc
                ? q.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase)
                : q.OrderByDescending(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
            "author" => _sortAsc
                ? q.OrderBy(b => b.Authors.FirstOrDefault() ?? "", StringComparer.CurrentCultureIgnoreCase)
                : q.OrderByDescending(b => b.Authors.FirstOrDefault() ?? "", StringComparer.CurrentCultureIgnoreCase),
            "progress" => _sortAsc
                ? q.OrderBy(b => b.Progress)
                : q.OrderByDescending(b => b.Progress),
            "added" => _sortAsc
                ? q.OrderBy(b => b.AddedAt)
                : q.OrderByDescending(b => b.AddedAt),
            _ => _sortAsc
                ? q.OrderBy(b => b.OpenedAt ?? b.AddedAt)
                : q.OrderByDescending(b => b.OpenedAt ?? b.AddedAt),
        };
        return ordered.ToList();
    }

    private void RenderBooksCatalog(IReadOnlyList<BookSummary> source, bool showContinueReading, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var filtered = FilterAndSortBooks(source);
        _lastCatalogFilteredCount = filtered.Count;
        _lastCatalogReadingCount = source.Count(b => b.Status == ReadingStatus.Reading);

        StatusLeftText.Text = _selectedBookIds.Count > 0
            ? L10n.T($"已选 {_selectedBookIds.Count} 本 · 共 {_lastCatalogFilteredCount} 本")
            : L10n.T($"共 {_lastCatalogFilteredCount} 本书 · 在读 {_lastCatalogReadingCount} 本");

        if (filtered.Count == 0)
        {
            MainContentStack.Children.Add(BuildEmptyLibraryCard(source.Count == 0, dark));
            return;
        }

        bool isList = string.Equals(_library.Prefs.ShelfViewMode, "list", StringComparison.OrdinalIgnoreCase);

        // Atmospheric cover-tinted Continue Reading strip (only in Grid view on "all" when not filtering)
        if (!isList && showContinueReading && _statusFilter == "all" && string.IsNullOrWhiteSpace(_inlineQuery))
        {
            var recent = source
                .Where(b => b.Status == ReadingStatus.Reading || (b.OpenedAt is not null && b.Progress > 0.005f))
                .OrderByDescending(b => b.OpenedAt ?? b.AddedAt)
                .Take(3)
                .ToList();

            if (recent.Count > 0)
            {
                var crSection = new StackPanel { Spacing = 10 };
                crSection.Children.Add(new TextBlock
                {
                    Text = L10n.T("继续阅读"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T2),
                });

                var crGrid = new Grid { ColumnSpacing = 14 };
                for (int i = 0; i < Math.Max(3, recent.Count); i++)
                    crGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int i = 0; i < recent.Count; i++)
                {
                    var card = BuildContinueReadingCard(recent[i], dark);
                    Grid.SetColumn(card, i);
                    crGrid.Children.Add(card);
                }
                crSection.Children.Add(crGrid);
                MainContentStack.Children.Add(crSection);
            }
        }

        if (isList)
        {
            MainContentStack.Children.Add(BuildDetailsListView(filtered, dark));
        }
        else
        {
            var gridSection = new StackPanel { Spacing = 12 };
            if (showContinueReading && _statusFilter == "all" && string.IsNullOrWhiteSpace(_inlineQuery))
            {
                gridSection.Children.Add(new TextBlock
                {
                    Text = L10n.T($"全部书目 ({filtered.Count})"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T2),
                });
            }
            gridSection.Children.Add(BuildBooksWrapGrid(filtered, dark));
            MainContentStack.Children.Add(gridSection);
        }
    }

    private Border BuildContinueReadingCard(BookSummary book, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        int pct = (int)Math.Round(Math.Clamp(book.Progress, 0f, 1f) * 100);

        // Airy Windows 11 Fluent 2 elevated warm paper card (subtle cover-tinted ambient wash, never heavy dark mud)
        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 12),
            ContextFlyout = BuildBookMenuFlyout(book),
        };
        if (!string.IsNullOrEmpty(book.CoverPath) && File.Exists(book.CoverPath))
        {
            _ = TintContinueCardAsync(card, book.CoverPath, dark, p.Card);
        }

        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var cover = BookVisuals.CreateCoverView(book, 56, 78, dark, _pairedBookIds.Contains(book.Id), IsBookPlaying(book), tc.Accent);
        Grid.SetColumn(cover, 0);
        g.Children.Add(cover);

        var info = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var titleTb = L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var authorTb = L10n.Skip(new TextBlock
        {
            Text = book.Authors.Length > 0 ? string.Join(" · ", book.Authors) : FormatBookTypeBadge(book.Format),
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T2),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        double frac = Math.Clamp(book.Progress, 0.02, 1.0);
        var barHost = new Grid
        {
            Height = 3.5,
            Margin = new Thickness(0, 2, 0, 2),
        };
        barHost.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = ThemeHelper.Brush(p.Inset),
        });
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(frac, GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1.0 - frac), GridUnitType.Star) });
        var barFill = new Border
        {
            CornerRadius = new CornerRadius(2),
            Background = ThemeHelper.Brush(tc.Fill),
        };
        Grid.SetColumn(barFill, 0);
        fillGrid.Children.Add(barFill);
        barHost.Children.Add(fillGrid);

        var actionsRow = new Grid { Margin = new Thickness(0, 3, 0, 0), ColumnSpacing = 8 };
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionsRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var pctBadge = new TextBlock
        {
            Text = pct > 0 ? $"{pct}% · {FormatBookTypeBadge(book.Format)}" : L10n.T("刚开始阅读"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(pctBadge, 0);
        actionsRow.Children.Add(pctBadge);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(btns, 1);
        var listenBtn = new Button
        {
            Content = YYIcons.Make("headphones", 12.5, ThemeHelper.Brush(p.T2)),
            Width = 28,
            Height = 26,
            MinWidth = 28,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(p.Inset),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
        };
        ToolTipService.SetToolTip(listenBtn, L10n.T("直接开始朗读 / 听书"));
        listenBtn.Click += (_, _) => TryOpenBook(book, autoListen: true);

        var readBtn = new Button
        {
            Content = L10n.T("继续阅读"),
            Height = 26,
            MinHeight = 26,
            Padding = new Thickness(10, 0, 10, 0),
            CornerRadius = new CornerRadius(5),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Background = ThemeHelper.Brush(tc.Soft),
            Foreground = ThemeHelper.Brush(tc.Accent),
            BorderBrush = ThemeHelper.Brush(tc.Edge),
            BorderThickness = new Thickness(1),
            UseSystemFocusVisuals = false,
        };
        readBtn.Resources["ButtonBackground"] = ThemeHelper.Brush(tc.Soft);
        readBtn.Resources["ButtonBackgroundPointerOver"] = ThemeHelper.Brush(tc.Fill);
        readBtn.Resources["ButtonForeground"] = ThemeHelper.Brush(tc.Accent);
        readBtn.Resources["ButtonForegroundPointerOver"] = ThemeHelper.Brush(Colors.White);
        readBtn.Click += (_, _) => TryOpenBook(book);

        btns.Children.Add(listenBtn);
        btns.Children.Add(readBtn);
        actionsRow.Children.Add(btns);

        info.Children.Add(titleTb);
        info.Children.Add(authorTb);
        info.Children.Add(barHost);
        info.Children.Add(actionsRow);
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        card.Child = g;
        card.PointerEntered += (_, _) => card.BorderBrush = ThemeHelper.Brush(tc.Edge);
        card.PointerExited += (_, _) => card.BorderBrush = ThemeHelper.Brush(p.Line);
        card.DoubleTapped += (_, _) => TryOpenBook(book);
        return card;
    }

    private static async Task TintContinueCardAsync(Border card, string coverPath, bool dark, Color baseCard)
    {
        try
        {
            var c = await CoverColorHelper.LoadLeadCardColorAsync(coverPath, dark);
            // Soft 10% ambient wash on left edge blending cleanly into warm Fluent card surface
            double w = dark ? 0.22 : 0.09;
            Color wash = Color.FromArgb(255,
                (byte)Math.Clamp(baseCard.R + (c.R - baseCard.R) * w, 0, 255),
                (byte)Math.Clamp(baseCard.G + (c.G - baseCard.G) * w, 0, 255),
                (byte)Math.Clamp(baseCard.B + (c.B - baseCard.B) * w, 0, 255));
            card.Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.2),
                EndPoint = new Windows.Foundation.Point(1, 0.8),
                GradientStops =
                {
                    new GradientStop { Color = wash, Offset = 0.0 },
                    new GradientStop { Color = baseCard, Offset = 0.72 },
                },
            };
        }
        catch { }
    }

    private StackPanel BuildBooksWrapGrid(IReadOnlyList<BookSummary> books, bool dark)
    {
        double availW = Math.Max(460, SubPageAvailableWidth);
        _lastRenderWidth = availW;
        double coverW = Math.Clamp(_library.Prefs.CoverWidth, 78, 148);
        double coverH = Math.Round(coverW * 1.40);
        double cellW = coverW + 20;
        const double colGap = 16;
        int cols = Math.Max(2, (int)Math.Floor((availW + colGap) / (cellW + colGap)));

        var outer = new StackPanel { Spacing = 18 };
        for (int i = 0; i < books.Count; i += cols)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = colGap };
            for (int c = 0; c < cols && (i + c) < books.Count; c++)
            {
                row.Children.Add(BuildBookGridTile(books[i + c], coverW, coverH, dark));
            }
            outer.Children.Add(row);
        }
        return outer;
    }

    private Border BuildBookGridTile(BookSummary book, double coverW, double coverH, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        bool selected = _selectedBookIds.Contains(book.Id);
        int pct = (int)Math.Round(Math.Clamp(book.Progress, 0f, 1f) * 100);

        var tile = new Border
        {
            Width = coverW + 20,
            Padding = new Thickness(10, 10, 10, 8),
            CornerRadius = new CornerRadius(8),
            Background = selected ? ThemeHelper.Brush(tc.Soft) : new SolidColorBrush(Colors.Transparent),
            BorderBrush = selected ? ThemeHelper.Brush(tc.Accent) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(1.5),
            ContextFlyout = BuildBookMenuFlyout(book),
            CanDrag = true,
        };
        tile.DragStarting += (_, e) =>
        {
            e.Data.SetText($"yuyue-book:{book.Id}");
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };

        var stack = new StackPanel { Spacing = 7 };

        // Cover container with hover selection check ring + quick open/listen pill
        var coverHost = new Grid { Width = coverW, Height = coverH, HorizontalAlignment = HorizontalAlignment.Center };
        var coverView = BookVisuals.CreateCoverView(
            book, coverW, coverH, dark,
            hasPairedAudio: _pairedBookIds.Contains(book.Id),
            isPlaying: IsBookPlaying(book),
            playingAccent: tc.Accent,
            shadowReceiver: CoverShadowReceiver,
            eink: _library.Prefs.GlobalEinkMode);
        coverHost.Children.Add(coverView);

        // Top-left checkbox ring (visible on hover or when selected)
        var transparentBrush = new SolidColorBrush(Colors.Transparent);
        var hoverBrush = ThemeHelper.Brush(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 42, 43, 46));
        var selectedBgBrush = ThemeHelper.Brush(tc.Soft);
        var accentBrush = ThemeHelper.Brush(tc.Accent);
        var ringIdleBgBrush = ThemeHelper.Brush(Color.FromArgb(200, p.Card.R, p.Card.G, p.Card.B));
        var ringIdleBorderBrush = ThemeHelper.Brush(p.T3);

        var checkRing = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(5, 5, 0, 0),
            Background = selected ? accentBrush : ringIdleBgBrush,
            BorderBrush = selected ? accentBrush : ringIdleBorderBrush,
            BorderThickness = new Thickness(1),
            Opacity = selected ? 1.0 : 0.0,
            Child = selected ? YYIcons.Make("checkmark", 11, ThemeHelper.Brush(Colors.White)) : null,
        };
        checkRing.Tapped += (_, e) =>
        {
            ToggleBookSelection(book.Id);
            e.Handled = true;
        };
        checkRing.DoubleTapped += (_, e) => e.Handled = true;
        coverHost.Children.Add(checkRing);

        // Bottom hover quick-open / listen strip
        var quickHoverBar = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(4, 3, 4, 3),
            CornerRadius = new CornerRadius(6),
            Background = ThemeHelper.Brush(Color.FromArgb(228, p.Card.R, p.Card.G, p.Card.B)),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Opacity = 0.0,
        };
        quickHoverBar.Tapped += (_, e) => e.Handled = true;
        quickHoverBar.DoubleTapped += (_, e) => e.Handled = true;
        var hoverBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var qOpenBtn = new Button
        {
            Content = L10n.T("打开"),
            Height = 22,
            MinHeight = 22,
            Padding = new Thickness(7, 0, 7, 0),
            CornerRadius = new CornerRadius(4),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Background = ThemeHelper.Brush(tc.Fill),
            Foreground = ThemeHelper.Brush(Colors.White),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
        };
        qOpenBtn.Click += (_, _) => TryOpenBook(book);

        var qListenBtn = new Button
        {
            Content = YYIcons.Make("headphones", 11, ThemeHelper.Brush(p.T1)),
            Width = 22,
            Height = 22,
            MinWidth = 22,
            MinHeight = 22,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(p.Inset),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
        };
        ToolTipService.SetToolTip(qListenBtn, L10n.T("直接听书"));
        qListenBtn.Click += (_, _) => TryOpenBook(book, autoListen: true);

        hoverBtns.Children.Add(qOpenBtn);
        hoverBtns.Children.Add(qListenBtn);
        quickHoverBar.Child = hoverBtns;
        coverHost.Children.Add(quickHoverBar);

        stack.Children.Add(coverHost);

        // Title & Author & Progress
        var titleTb = L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = 17,
        });
        stack.Children.Add(titleTb);

        string authorText = book.Authors.Length > 0 ? book.Authors[0] : FormatBookTypeBadge(book.Format);
        var metaRow = new Grid { ColumnSpacing = 6 };
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        metaRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var subTb = L10n.Skip(new TextBlock
        {
            Text = authorText,
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        metaRow.Children.Add(subTb);

        if (_library.Prefs.ShowProgress && pct > 0)
        {
            var pctTb = new TextBlock
            {
                Text = $"{pct}%",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tc.Accent),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(pctTb, 1);
            metaRow.Children.Add(pctTb);
        }
        stack.Children.Add(metaRow);

        tile.Child = stack;

        bool isPointerOver = false;
        void SyncTileVisual()
        {
            bool sel = _selectedBookIds.Contains(book.Id);
            tile.Background = sel ? selectedBgBrush : (isPointerOver ? hoverBrush : transparentBrush);
            tile.BorderBrush = sel ? accentBrush : transparentBrush;
            checkRing.Background = sel ? accentBrush : ringIdleBgBrush;
            checkRing.BorderBrush = sel ? accentBrush : ringIdleBorderBrush;
            checkRing.Opacity = (sel || isPointerOver) ? 1.0 : 0.0;
            if (sel)
            {
                checkRing.Child ??= YYIcons.Make("checkmark", 11, ThemeHelper.Brush(Colors.White));
            }
            else
            {
                checkRing.Child = null;
            }
        }
        _selectionVisualUpdaters.Add(SyncTileVisual);

        tile.PointerEntered += (_, _) =>
        {
            isPointerOver = true;
            SyncTileVisual();
            quickHoverBar.Opacity = 1.0;
        };
        tile.PointerExited += (_, _) =>
        {
            isPointerOver = false;
            SyncTileVisual();
            quickHoverBar.Opacity = 0.0;
        };
        tile.Tapped += (_, e) =>
        {
            if (e.Handled) return;
            HandleBookTileClick(book);
        };
        tile.DoubleTapped += (_, e) =>
        {
            if (e.Handled) return;
            if (!_library.Prefs.ShelfSingleClickOpen)
                TryOpenBook(book);
            e.Handled = true;
        };

        return tile;
    }

    private Border BuildDetailsListView(IReadOnlyList<BookSummary> books, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        var rows = new StackPanel { Spacing = 0 };

        // Explorer-style crisp header strip (no heavy double card frame)
        var headerGrid = new Grid
        {
            Height = 34,
            Padding = new Thickness(8, 0, 12, 0),
            Background = ThemeHelper.Brush(p.Inset),
            CornerRadius = new CornerRadius(6),
            ColumnSpacing = 12,
        };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        var allCb = new CheckBox
        {
            IsChecked = books.Count > 0 && books.All(b => _selectedBookIds.Contains(b.Id)),
            MinWidth = 0,
            Width = 22,
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        void SyncAllCheckBox()
        {
            bool allSelected = books.Count > 0 && books.All(b => _selectedBookIds.Contains(b.Id));
            if (allCb.IsChecked != allSelected)
                allCb.IsChecked = allSelected;
        }
        _selectionVisualUpdaters.Add(SyncAllCheckBox);
        allCb.Click += (_, _) =>
        {
            if (allCb.IsChecked == true)
            {
                foreach (var b in books) _selectedBookIds.Add(b.Id);
            }
            else
            {
                _selectedBookIds.Clear();
            }
            UpdateBatchBarAndSelectionVisuals();
        };
        Grid.SetColumn(allCb, 0);
        headerGrid.Children.Add(allCb);

        void AddSortHeader(int col, string label, string key)
        {
            bool active = _sortKey == key;
            string arrow = active ? (_sortAsc ? " ↑" : " ↓") : "";
            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYTextActionButtonStyle"],
                Content = L10n.T(label) + arrow,
                FontSize = 11.5,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Medium,
                Foreground = ThemeHelper.Brush(active ? tc.Accent : p.T2),
                Padding = new Thickness(2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            btn.Click += (_, _) => SetSort(key);
            Grid.SetColumn(btn, col);
            headerGrid.Children.Add(btn);
        }

        AddSortHeader(1, "书名", "title");
        AddSortHeader(2, "作者", "author");
        var fmtLbl = new TextBlock { Text = L10n.T("格式"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(fmtLbl, 3);
        headerGrid.Children.Add(fmtLbl);
        AddSortHeader(4, "阅读进度", "progress");
        var stLbl = new TextBlock { Text = L10n.T("状态"), FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(stLbl, 5);
        headerGrid.Children.Add(stLbl);
        AddSortHeader(6, "最近阅读", "recent");

        rows.Children.Add(headerGrid);

        var transparentBrush = new SolidColorBrush(Colors.Transparent);
        var hoverBrush = ThemeHelper.Brush(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 42, 43, 46));
        var selectedBgBrush = ThemeHelper.Brush(tc.Soft);

        foreach (var book in books)
        {
            bool selected = _selectedBookIds.Contains(book.Id);
            int pct = (int)Math.Round(Math.Clamp(book.Progress, 0f, 1f) * 100);

            var rowBorder = new Border
            {
                Height = 48,
                Padding = new Thickness(8, 0, 12, 0),
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 2, 0, 0),
                Background = selected ? selectedBgBrush : transparentBrush,
                BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46)),
                BorderThickness = new Thickness(0, 0, 0, 1),
                ContextFlyout = BuildBookMenuFlyout(book),
                CanDrag = true,
            };
            rowBorder.DragStarting += (_, e) =>
            {
                e.Data.SetText($"yuyue-book:{book.Id}");
                e.Data.RequestedOperation = DataPackageOperation.Copy;
            };
            var rg = new Grid { ColumnSpacing = 12 };
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(128) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(76) });
            rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

            var cb = new CheckBox
            {
                IsChecked = selected,
                MinWidth = 0,
                Width = 22,
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            cb.Click += (_, _) => ToggleBookSelection(book.Id);
            cb.Tapped += (_, e) => e.Handled = true;
            cb.DoubleTapped += (_, e) => e.Handled = true;
            Grid.SetColumn(cb, 0);
            rg.Children.Add(cb);

            // Mini cover + Title
            var titleCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            titleCell.Children.Add(BookVisuals.CreateCoverView(book, 24, 34, dark));
            titleCell.Children.Add(L10n.Skip(new TextBlock
            {
                Text = book.Title,
                FontSize = 13,
                FontWeight = FontWeights.Medium,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            Grid.SetColumn(titleCell, 1);
            rg.Children.Add(titleCell);

            var authorTb = L10n.Skip(new TextBlock
            {
                Text = book.Authors.Length > 0 ? string.Join("、", book.Authors) : "—",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(authorTb, 2);
            rg.Children.Add(authorTb);

            var fmtPill = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(6, 2, 6, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = FormatBookTypeBadge(book.Format),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T2),
                },
            };
            Grid.SetColumn(fmtPill, 3);
            rg.Children.Add(fmtPill);

            // Progress bar + %
            var progCell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var track = new Grid { Width = 66, Height = 4, VerticalAlignment = VerticalAlignment.Center };
            track.Children.Add(new Border { CornerRadius = new CornerRadius(2), Background = ThemeHelper.Brush(p.Inset) });
            track.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Round(66 * Math.Clamp(book.Progress, 0f, 1f)),
                CornerRadius = new CornerRadius(2),
                Background = ThemeHelper.Brush(tc.Accent),
            });
            progCell.Children.Add(track);
            progCell.Children.Add(new TextBlock
            {
                Text = $"{pct}%",
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Grid.SetColumn(progCell, 4);
            rg.Children.Add(progCell);

            var statusTb = new TextBlock
            {
                Text = FormatReadingStatus(book.Status),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(book.Status == ReadingStatus.Reading ? tc.Accent : p.T2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(statusTb, 5);
            rg.Children.Add(statusTb);

            long ts = book.OpenedAt ?? book.AddedAt;
            var dateTb = new TextBlock
            {
                Text = FormatShortDate(ts),
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(dateTb, 6);
            rg.Children.Add(dateTb);

            rowBorder.Child = rg;

            bool isRowPointerOver = false;
            void SyncRowVisual()
            {
                bool sel = _selectedBookIds.Contains(book.Id);
                rowBorder.Background = sel ? selectedBgBrush : (isRowPointerOver ? hoverBrush : transparentBrush);
                if (cb.IsChecked != sel)
                    cb.IsChecked = sel;
            }
            _selectionVisualUpdaters.Add(SyncRowVisual);

            rowBorder.PointerEntered += (_, _) =>
            {
                isRowPointerOver = true;
                SyncRowVisual();
            };
            rowBorder.PointerExited += (_, _) =>
            {
                isRowPointerOver = false;
                SyncRowVisual();
            };
            rowBorder.Tapped += (_, e) =>
            {
                if (e.Handled) return;
                HandleBookTileClick(book);
            };
            rowBorder.DoubleTapped += (_, e) =>
            {
                if (e.Handled) return;
                if (!_library.Prefs.ShelfSingleClickOpen)
                    TryOpenBook(book);
                e.Handled = true;
            };

            rows.Children.Add(rowBorder);
        }

        card.Child = rows;
        return card;
    }

    private void HandleBookTileClick(BookSummary book)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (_library.Prefs.ShelfSingleClickOpen && !ctrl && _selectedBookIds.Count == 0)
        {
            TryOpenBook(book);
            return;
        }

        if (ctrl)
        {
            ToggleBookSelection(book.Id);
        }
        else
        {
            if (_selectedBookIds.Count == 1 && _selectedBookIds.Contains(book.Id))
                _selectedBookIds.Clear();
            else
            {
                _selectedBookIds.Clear();
                _selectedBookIds.Add(book.Id);
            }
            UpdateBatchBarAndSelectionVisuals();
        }
    }

    private void ToggleBookSelection(string bookId)
    {
        if (!_selectedBookIds.Add(bookId))
            _selectedBookIds.Remove(bookId);
        UpdateBatchBarAndSelectionVisuals();
    }

    private void SelectAllVisibleBooks()
    {
        IReadOnlyList<BookSummary> baseList;
        if (_activeSection == "audio")
        {
            baseList = _shelfBooks.Where(b => b.Format == BookFormat.Audio).ToArray();
        }
        else if (_activeSection.StartsWith("col:", StringComparison.Ordinal))
        {
            string cid = _activeSection["col:".Length..];
            baseList = _keptBooks.Where(b => b.Collections.Contains(cid, StringComparer.OrdinalIgnoreCase)).ToArray();
        }
        else
        {
            baseList = _shelfBooks;
        }

        foreach (var b in FilterAndSortBooks(baseList))
            _selectedBookIds.Add(b.Id);
        UpdateBatchBarAndSelectionVisuals();
    }

    private void UpdateBatchBarAndSelectionVisuals()
    {
        if (IsBooksSection())
        {
            foreach (var updater in _selectionVisualUpdaters)
                updater();

            StatusLeftText.Text = _selectedBookIds.Count > 0
                ? L10n.T($"已选 {_selectedBookIds.Count} 本 · 共 {_lastCatalogFilteredCount} 本")
                : L10n.T($"共 {_lastCatalogFilteredCount} 本书 · 在读 {_lastCatalogReadingCount} 本");
        }

        if (!IsBooksSection() || _selectedBookIds.Count == 0)
        {
            BatchCommandBar.Visibility = Visibility.Collapsed;
            return;
        }
        BatchCommandBar.Visibility = Visibility.Visible;
        BatchSelectionCountText.Text = L10n.T($"已选 {_selectedBookIds.Count} 本");
        BatchEditBtn.Visibility = _selectedBookIds.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border BuildEmptyLibraryCard(bool trulyEmpty, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var stack = new StackPanel
        {
            Spacing = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(32, 48, 32, 48),
        };
        bool isAudioEmpty = _activeSection == "audio";
        stack.Children.Add(YYIcons.Make(isAudioEmpty ? "headphones" : "book-open", 36, ThemeHelper.Brush(tc.Accent)));
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(trulyEmpty
                ? (isAudioEmpty ? "还没有有声书" : "书库里还没有书")
                : "当前筛选条件下没有匹配的书籍"),
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = L10n.T(trulyEmpty
                ? (isAudioEmpty
                    ? "直接将 M4B / MP3 音频文件或文件夹拖进窗口，或点击下方按钮导入有声书，支持与电子书图文配对。"
                    : "直接将 EPUB / PDF / TXT / 有声书拖进窗口，或点击下方按钮导入本地文件与 Wi-Fi 传书。")
                : "清空搜索词或切换上方阅读状态筛选即可查看全部书籍。"),
            FontSize = 12.5,
            Foreground = ThemeHelper.Brush(p.T2),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            MaxWidth = 420,
        });

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        if (trulyEmpty)
        {
            if (isAudioEmpty)
            {
                var impAudioBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("导入有声书 / 音频文件夹") };
                impAudioBtn.Click += (_, _) => _ = PickAndImportAudiobookAsync();
                btnRow.Children.Add(impAudioBtn);
            }
            else
            {
                var impBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"], Content = L10n.T("导入本地书籍") };
                impBtn.Click += (_, _) => _ = PickAndImportBooksAsync();
                var wifiBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("局域网 Wi-Fi 传书") };
                wifiBtn.Click += (_, _) => ShowWifiTransfer();
                btnRow.Children.Add(impBtn);
                btnRow.Children.Add(wifiBtn);
            }
        }
        else
        {
            var clearBtn = new Button { Style = (Style)Application.Current.Resources["YYFluentButtonStyle"], Content = L10n.T("重置筛选") };
            clearBtn.Click += (_, _) =>
            {
                _statusFilter = "all";
                _formatFilter = "all";
                _inlineQuery = "";
                InlineFilterBox.Text = "";
                RenderCurrentSection();
            };
            btnRow.Children.Add(clearBtn);
        }
        stack.Children.Add(btnRow);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Child = stack,
        };
    }

    /// <summary>
    /// Native Windows 11 <see cref="MenuFlyout"/> for books with left icons and right keyboard accelerator hints.
    /// </summary>
    public FlyoutBase BuildBookMenuFlyout(BookSummary book)
    {
        var menu = new MenuFlyout();

        MenuFlyoutItem MakeItem(string text, string icon, Action click, string? shortcut = null, bool danger = false)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(text) };
            if (!string.IsNullOrEmpty(shortcut)) mi.KeyboardAcceleratorTextOverride = shortcut;
            if (danger) mi.Foreground = (Brush)Application.Current.Resources["YYDangerBrush"];
            mi.Click += (_, _) => click();
            return mi;
        }

        menu.Items.Add(MakeItem("在新标签页打开", "book-open", () => TryOpenBook(book), "Enter"));
        menu.Items.Add(MakeItem("在独立窗口打开", "detach", () => App.CurrentApp.OpenBookInNewWindow(book.Id)));
        menu.Items.Add(MakeItem("直接开始朗读 / 听书", "headphones", () => TryOpenBook(book, autoListen: true)));
        menu.Items.Add(new MenuFlyoutSeparator());

        var statusSub = new MenuFlyoutSubItem { Text = L10n.T("标记阅读状态") };
        foreach (var (st, lbl) in new[] { (ReadingStatus.Reading, "在读"), (ReadingStatus.Want, "想读"), (ReadingStatus.Finished, "读完") })
        {
            var sCap = st;
            var subItem = new ToggleMenuFlyoutItem { Text = L10n.T(lbl), IsChecked = book.Status == st };
            subItem.Click += (_, _) =>
            {
                if (sCap == ReadingStatus.Finished)
                    _library.SetFinished(book.Id, true);
                else if (book.Status == ReadingStatus.Finished && sCap == ReadingStatus.Reading)
                    _library.SetFinished(book.Id, false);
                else
                    _library.SetStatus(book.Id, sCap);
                RefreshBooks();
            };
            statusSub.Items.Add(subItem);
        }
        menu.Items.Add(statusSub);
        menu.Items.Add(MakeItem(
            book.Status == ReadingStatus.Finished ? "取消读完标记" : "标为读完",
            "check",
            () =>
            {
                _library.SetFinished(book.Id, book.Status != ReadingStatus.Finished);
                RefreshBooks();
            }));
        menu.Items.Add(MakeItem("加入合集…", "stack", () => OpenPickCollectionForBook(book)));
        if (book.Format != BookFormat.Audio && !book.Record)
        {
            menu.Items.Add(MakeItem("配对有声书…", "headphones", () => _ = ShowPairAudiobookDialogAsync(book)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MakeItem("编辑书名与作者…", "pencil", () => _ = ShowEditBookDialogAsync(book), "F2"));
        if (book.Available && !book.Record)
        {
            menu.Items.Add(MakeItem("在文件资源管理器中显示", "folder", () => RevealInExplorer(book)));
            if (book.Format != BookFormat.Audio && book.Format != BookFormat.Pdf)
            {
                menu.Items.Add(MakeItem("导出带笔记的 EPUB…", "share-up", () => _ = ExportAnnotatedEpubAsync(book)));
            }
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        if (!book.Record)
        {
            menu.Items.Add(MakeItem("只删文件，留下记录", "archive", () =>
            {
                try
                {
                    App.CurrentApp.CloseBookWindows(book.Id);
                    _library.KeepRecordOnly(book.Id);
                    RefreshBooks();
                    ShowToast($"已删除《{book.Title}》原文件，保留阅读记录与笔记");
                }
                catch (Exception ex)
                {
                    ShowToast($"操作失败：{ex.Message}");
                }
            }));
        }
        menu.Items.Add(MakeItem("移出书库", "trash", () => OpenDeleteForBook(book), "Del", danger: true));
        return menu;
    }

    private void BuildImportSplitFlyout()
    {
        var flyout = new MenuFlyout();
        void Add(string text, Action act, string? key = null)
        {
            var mi = new MenuFlyoutItem { Text = L10n.T(text) };
            if (key is not null) mi.KeyboardAcceleratorTextOverride = key;
            mi.Click += (_, _) => act();
            flyout.Items.Add(mi);
        }

        Add("导入本地书籍 (EPUB / PDF / TXT / MOBI)…", () => _ = PickAndImportBooksAsync(), "Ctrl+O");
        Add("导入有声书 / 音频文件夹…", () => _ = PickAndImportAudiobookAsync());
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("局域网 Wi-Fi 传书…", ShowWifiTransfer);
        Add("远程书库 (OPDS / WebDAV)…", ShowRemoteLibrary);
        Add("记一本纸质书…", () => _ = PromptAddRecordBookAsync(ReadingStatus.Reading));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("从 .yuyue 备份快照恢复…", () => _ = ImportSnapshotAsync());

        ImportDropdownBtn.Flyout = flyout;
    }

    private void BuildSortFlyout()
    {
        var flyout = new MenuFlyout();
        (string Key, string Label)[] opts =
        [
            ("recent", "最近阅读"),
            ("added", "添加时间"),
            ("title", "书名"),
            ("author", "作者"),
            ("progress", "阅读进度"),
        ];
        foreach (var (key, label) in opts)
        {
            string k = key;
            var mi = new MenuFlyoutItem { Text = L10n.T(label) };
            mi.Click += (_, _) => SetSort(k);
            flyout.Items.Add(mi);
        }
        SortBtn.Flyout = flyout;
        UpdateSortButtonLabel();
    }

    private void BuildBatchStatusFlyout()
    {
        var flyout = new MenuFlyout();
        foreach (var (st, lbl) in new[] { (ReadingStatus.Reading, "标记为在读"), (ReadingStatus.Want, "标记为想读"), (ReadingStatus.Finished, "标记为读完") })
        {
            var sCap = st;
            var mi = new MenuFlyoutItem { Text = L10n.T(lbl) };
            mi.Click += (_, _) =>
            {
                foreach (var id in _selectedBookIds)
                    try { _library.SetStatus(id, sCap); } catch { }
                RefreshBooks();
                ShowToast($"已更新 {_selectedBookIds.Count} 本书的状态");
            };
            flyout.Items.Add(mi);
        }
        BatchStatusBtn.Flyout = flyout;
    }

    private void SetSort(string key)
    {
        if (_sortKey == key) _sortAsc = !_sortAsc;
        else { _sortKey = key; _sortAsc = key is "title" or "author"; }

        _library.Prefs.BooksSort = _sortKey;
        _library.Prefs.ShelfSortDirection = _sortAsc ? "asc" : "desc";
        _library.SavePrefs();

        UpdateSortButtonLabel();
        RenderCurrentSection();
    }

    private void UpdateSortButtonLabel()
    {
        SortBtnLabel.Text = _sortKey switch
        {
            "added" => L10n.T("添加时间"),
            "title" => L10n.T("书名"),
            "author" => L10n.T("作者"),
            "progress" => L10n.T("阅读进度"),
            _ => L10n.T("最近阅读"),
        };
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        SortBtnIcon.Content = YYIcons.Make(_sortAsc ? "sort-asc" : "sort-desc", 13, ThemeHelper.Brush(ThemeHelper.Palette(dark).T2));
    }

    private void UpdateViewToggleButtons()
    {
        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        bool list = string.Equals(_library.Prefs.ShelfViewMode, "list", StringComparison.OrdinalIgnoreCase);
        GridViewBtn.Background = !list ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
        ListViewBtn.Background = list ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent);
    }

    private void UpdateClickModePill()
    {
        ClickModeToggleBtn.Content = _library.Prefs.ShelfSingleClickOpen
            ? L10n.T("点击模式：单击直开")
            : L10n.T("点击模式：双击打开");
    }

    private bool IsBookPlaying(BookSummary book) =>
        _library.SystemIntegration.IsPlayingTitle(book.Title)
        || string.Equals(App.CurrentApp.GetActiveAudioBookId(), book.Id, StringComparison.OrdinalIgnoreCase);

    private void OnPlaybackStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateGlobalNowPlayingDock();
            TabMetaChanged?.Invoke();
        });
    }

    public void SetBottomDock(Border? dock)
    {
        _podcastCustomDock = dock;
        BottomDockHost.Children.Clear();
        if (dock is not null)
        {
            BottomDockHost.Children.Add(dock);
        }
        else
        {
            UpdateGlobalNowPlayingDock();
        }
    }

    private void UpdateGlobalNowPlayingDock()
    {
        if (_podcastCustomDock is not null) return;
        BottomDockHost.Children.Clear();
        if (!_library.SystemIntegration.IsAudioPlaying || string.IsNullOrWhiteSpace(_library.SystemIntegration.ActivePlaybackTitle))
            return;

        bool dark = ThemeHelper.IsDark(_library.Prefs.ToElementTheme());
        var p = ThemeHelper.Palette(dark);
        var tc = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var dock = new Border
        {
            Padding = new Thickness(20, 8, 20, 8),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        var g = new Grid { ColumnSpacing = 12 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var waveIcon = YYIcons.Make("wave", 15, ThemeHelper.Brush(tc.Accent));
        Grid.SetColumn(waveIcon, 0);
        g.Children.Add(waveIcon);

        var titleTb = L10n.Skip(new TextBlock
        {
            Text = $"{L10n.T("正在播放")} · {_library.SystemIntegration.ActivePlaybackTitle}",
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = ThemeHelper.Brush(p.T1),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(titleTb, 1);
        g.Children.Add(titleTb);

        var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var toggleBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Content = L10n.T("暂停"),
        };
        toggleBtn.Click += (_, _) => _library.SystemIntegration.TogglePlaybackCallback?.Invoke();

        var pipBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Content = L10n.T("迷你浮窗"),
        };
        pipBtn.Click += (_, _) => App.CurrentApp.ShowMiniPlayer();

        var jumpBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYFluentAccentButtonStyle"],
            Height = 26,
            MinHeight = 26,
            Content = L10n.T("回到书页"),
        };
        jumpBtn.Click += (_, _) => App.CurrentApp.ActivateActiveAudioSurface();

        rightBtns.Children.Add(toggleBtn);
        rightBtns.Children.Add(pipBtn);
        rightBtns.Children.Add(jumpBtn);
        Grid.SetColumn(rightBtns, 2);
        g.Children.Add(rightBtns);

        dock.Child = g;
        BottomDockHost.Children.Add(dock);
    }

    // ILibraryHost forwarding
    public Border CreateCoverView(BookSummary book, double width, double height, bool elevated) =>
        BookVisuals.CreateCoverView(
            book, width, height,
            ThemeHelper.IsDark(_library.Prefs.ToElementTheme()),
            _pairedBookIds.Contains(book.Id),
            IsBookPlaying(book),
            shadowReceiver: elevated ? CoverShadowReceiver : null,
            eink: _library.Prefs.GlobalEinkMode);

    public void ShowToast(string text, bool canUndo = false, bool isDelete = false, Action? undo = null, string? detail = null, string? actionLabel = null, string? iconKind = null)
    {
        string full = string.IsNullOrWhiteSpace(detail) ? text : $"{text} · {detail}";
        _ownerWindow.ShowToast(full, canUndo ? (actionLabel ?? "撤销") : actionLabel, canUndo ? undo : null);
    }

    public void TryOpenBook(BookSummary book, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        if (book.Record)
        {
            _recordBuilder.OpenBookId = book.Id;
            SwitchTab("progress");
            return;
        }
        App.CurrentApp.OpenBook(book.Id, autoListen, section, sentence, askQuestion, searchQuery);
    }

    public void OpenPickCollectionForBook(BookSummary book) => _ = ShowPickCollectionDialogAsync([book]);
    public void OpenDeleteForBook(BookSummary book) => _ = HandleDeleteBooksAsync([book]);

    public void OpenPodcastInline(string issueId, double? seekSec = null, bool autoPlay = false)
    {
        _activeSection = "podcasts";
        UpdateNavSelectionVisuals();
        _podcastBuilder.OpenIssueAt(issueId, seekSec, autoPlay);
        RenderCurrentSection();
    }

    public Task OpenStudioSheetForBookAsync(string bookId, StudioKind kind = StudioKind.BookGuide)
    {
        _activeSection = "podcasts";
        UpdateNavSelectionVisuals();
        RenderCurrentSection();
        return _podcastBuilder.ShowStudioSheetAsync(kind, bookId);
    }

    public void SyncShelfSortFromPrefs()
    {
        _sortKey = string.IsNullOrWhiteSpace(_library.Prefs.BooksSort) ? "recent" : _library.Prefs.BooksSort;
        UpdateSortButtonLabel();
        RenderCurrentSection();
    }

    // Event handlers
    private void SidebarAddCollection_Click(object sender, RoutedEventArgs e) => _ = CreateNewCollectionDialogAsync();

    private void QuickSearch_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xr)
            _ = QuickSearchDialog.ShowAsync(xr, _library);
    }

    private void SyncFooter_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Sync.IsEnabled && _library.Sync.State != SyncStateKind.Failed)
            _ = _library.Sync.SyncNowAsync();
        else
            _ownerWindow.ShowSettings("sync");
    }

    private void FeedbackFooter_Click(object sender, RoutedEventArgs e)
    {
        if (RootGrid.XamlRoot is { } xr)
            _ = _library.Updates.ShowFeedbackComposerAsync(_ownerWindow, xr);
    }

    private void SettingsFooter_Click(object sender, RoutedEventArgs e) => _ownerWindow.ShowSettings("general");

    private void InlineFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _inlineQuery = InlineFilterBox.Text ?? "";
        RenderCurrentSection();
    }

    private void GridViewBtn_Click(object sender, RoutedEventArgs e)
    {
        _library.Prefs.ShelfViewMode = "grid";
        _library.SavePrefs();
        UpdateViewToggleButtons();
        RenderCurrentSection();
    }

    private void ListViewBtn_Click(object sender, RoutedEventArgs e)
    {
        _library.Prefs.ShelfViewMode = "list";
        _library.SavePrefs();
        UpdateViewToggleButtons();
        RenderCurrentSection();
    }

    private void ImportPrimary_Click(object sender, RoutedEventArgs e) => _ = PickAndImportBooksAsync();

    private void BatchSelectAll_Click(object sender, RoutedEventArgs e) => SelectAllVisibleBooks();

    private void BatchClearSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedBookIds.Clear();
        UpdateBatchBarAndSelectionVisuals();
    }

    private void BatchOpen_Click(object sender, RoutedEventArgs e) => OpenSelectedBooksInTabs();

    private void OpenSelectedBooksInTabs()
    {
        var targets = _keptBooks.Where(b => _selectedBookIds.Contains(b.Id)).ToList();
        foreach (var b in targets) TryOpenBook(b);
        _selectedBookIds.Clear();
        UpdateBatchBarAndSelectionVisuals();
    }

    private void BatchAddToCollection_Click(object sender, RoutedEventArgs e)
    {
        var targets = _keptBooks.Where(b => _selectedBookIds.Contains(b.Id)).ToList();
        if (targets.Count > 0) _ = ShowPickCollectionDialogAsync(targets);
    }

    private void BatchEdit_Click(object sender, RoutedEventArgs e)
    {
        var target = _keptBooks.FirstOrDefault(b => _selectedBookIds.Contains(b.Id));
        if (target is not null) _ = ShowEditBookDialogAsync(target);
    }

    private void BatchDelete_Click(object sender, RoutedEventArgs e)
    {
        var targets = _keptBooks.Where(b => _selectedBookIds.Contains(b.Id)).ToList();
        if (targets.Count > 0) _ = HandleDeleteBooksAsync(targets);
    }

    private void ClickModeToggle_Click(object sender, RoutedEventArgs e)
    {
        _library.Prefs.ShelfSingleClickOpen = !_library.Prefs.ShelfSingleClickOpen;
        _library.SavePrefs();
        UpdateClickModePill();
        ShowToast(_library.Prefs.ShelfSingleClickOpen ? "已切换为单击封面直接打开" : "已切换为双击打开（单击可选中多本）");
    }

    private void CoverZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressZoomEvent) return;
        _library.Prefs.CoverWidth = Math.Round(e.NewValue);
        _library.SavePrefs();
        if (IsBooksSection() && _library.Prefs.ShelfViewMode != "list")
            RenderCurrentSection();
    }

    private void MainScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (IsBooksSection() && _library.Prefs.ShelfViewMode != "list" && Math.Abs(e.NewSize.Width - _lastRenderWidth) > 40)
            RenderCurrentSection();
    }

    private static string FormatBookTypeBadge(BookFormat fmt) => fmt switch
    {
        BookFormat.Pdf => "PDF",
        BookFormat.Txt => "TXT",
        BookFormat.Audio => "有声",
        BookFormat.Mobi => "MOBI",
        _ => "EPUB",
    };

    private static string FormatReadingStatus(ReadingStatus st) => st switch
    {
        ReadingStatus.Reading => L10n.T("在读"),
        ReadingStatus.Finished => L10n.T("读完"),
        ReadingStatus.Paused => L10n.T("搁置"),
        ReadingStatus.Dropped => L10n.T("弃读"),
        _ => L10n.T("想读"),
    };

    private static string FormatShortDate(long unixMs)
    {
        if (unixMs <= 0) return "—";
        try
        {
            var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
            if (dt.Date == DateTime.Today) return dt.ToString("HH:mm");
            if (dt.Year == DateTime.Now.Year) return dt.ToString("M月d日");
            return dt.ToString("yyyy/M/d");
        }
        catch { return "—"; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _library.SystemIntegration.PlaybackStateChanged -= OnPlaybackStateChanged;
        try { _podcastBuilder.Dispose(); } catch { }
    }
}
