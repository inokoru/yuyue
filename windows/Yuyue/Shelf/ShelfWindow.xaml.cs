using System.Diagnostics;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI.Xaml.Documents;
using WinRT.Interop;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Podcast;
using Yuyue.Reader;
using TimeSpan = System.TimeSpan;

namespace Yuyue.Shelf;

public sealed partial class ShelfWindow : Window
{
    private readonly LibraryService _library;
    private readonly BookRecordPageBuilder _recordBuilder;
    private readonly PodcastWorkspaceBuilder _podcastBuilder;
    private IReadOnlyList<BookSummary> _shelfBooks = [];
    private IReadOnlyList<BookSummary> _keptBooks = [];
    private IReadOnlyList<BookCollection> _collections = [];
    private Dictionary<string, uint> _markCounts = [];
    private IReadOnlyList<VocabWord> _words = [];
    private ListenPair[] _listeningPairs = [];
    private HashSet<string> _pairedBookIds = [];

    private string _activeFilter = "all"; // all | progress | podcasts | audio | notes | words | collections | col:<id>
    private string _sortKey = "recent";   // recent | added | title | author
    private string _collectionSort = "recent"; // recent | name | count | own
    private string _collectionScope = "all";   // all | pinned
    private string _collectionQuery = "";
    private string? _highlightedBookId;
    private bool _sidebarVisible = true;
    private bool _narrowAutoCollapsed;
    private bool _readingExpanded;
    private bool _cameFromCollectionsWall;
    private static readonly HashSet<string> EbookImportExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".txt", ".mobi", ".azw3", ".azw", ".prc", ".fb2",
    };

    private bool _closed;
    private string? _hoveredSidebarGroup;
    private bool _notesByBook;
    private string _notesChip = "all"; // all | thoughts | yellow | blue | pink | wave
    private string _notesQuery = "";
    private string? _notesChosenBookId;
    private readonly Dictionary<string, IReadOnlyList<Mark>> _notesMarksCache = new(StringComparer.Ordinal);
    private NoteConflict[]? _cachedNoteConflicts;
    private ReviewDeck? _cachedReviewDeck;
    private bool _wordsByBook;
    private bool _wordsShowKnown;
    private string _wordsQuery = "";
    private string? _speakingVocabWord;
    private int _vocabSpeakSeq;
    private TextBox? _activeNotesFilterBox;
    private TextBox? _activeWordsFilterBox;
    private TextBox? _activeCollectionsFilterBox;
    private Action? _toastUndoAction;
    private DispatcherTimer? _toastTimer;
    private bool _toastHoverPaused;
    private DispatcherTimer? _importCardDismissTimer;
    private bool _importCardHoverPaused;
    private DispatcherTimer? _dropLeaveTimer;
    private int _dragStorageCount;
    private bool _probingDragItems;
    private readonly List<(Button Btn, Action Activate, FrameworkElement? OpenHint)> _quickSearchItems = [];
    private int _quickSearchSelectedIdx = -1;
    private BookSummary? _quickSearchBestCompanionBook;
    private CancellationTokenSource? _quickSearchCts;
    private Windows.Media.Playback.MediaPlayer? _reviewClipPlayer;

    public XamlRoot WindowXamlRoot => RootGrid.XamlRoot;
    internal double SubPageAvailableWidth => Math.Max(320, (PaperSheet.ActualWidth > 0 ? PaperSheet.ActualWidth : 800) - 50);
    internal void OpenPickCollectionForBook(BookSummary book) => _ = ShowPickCollectionDialogAsync(book);
    internal void OpenDeleteForBook(BookSummary book) => _ = HandleDeleteBookAsync(book);

    public ShelfWindow(LibraryService library)
    {
        _library = library;
        _recordBuilder = new BookRecordPageBuilder(this, library);
        _podcastBuilder = new PodcastWorkspaceBuilder(this, library);
        _sidebarVisible = _library.Prefs.ShelfSidebarShown;
        _sortKey = _library.Prefs.BooksSort is "added" or "title" or "author" ? _library.Prefs.BooksSort : "recent";
        _collectionSort = _library.Prefs.CollectionSort is "name" or "count" or "own" ? _library.Prefs.CollectionSort : "recent";
        InitializeComponent();

        YYChrome.StripTextBoxChrome(QuickSearchBox);
        ThemeHelper.ResizeWindow(this, _library.Prefs.ShelfWindowWidth, _library.Prefs.ShelfWindowHeight, minWidthDip: 720, minHeightDip: 500);
        SetTitleBar(TopDragBar);
        ThemeHelper.ApplyElevation(PaperSheet, DeskShadowReceiver, 20f);
        ThemeHelper.ApplyElevation(ToastBanner, PaperInnerGrid, 14f);
        ThemeHelper.ApplyElevation(ImportQueueCard, DeskShadowReceiver, 20f);
        ThemeHelper.ApplyElevation(QuickSearchCard, DeskShadowReceiver, 32f);

        if (!_sidebarVisible)
        {
            ApplySidebarVisibility(false);
        }

        // Restore collapsed sidebar groups from DevicePrefs
        foreach (var grp in _library.Prefs.ClosedSidebarGroups)
        {
            var panel = grp switch { "library" => GroupLibrary, "notes" => GroupNotes, "collections" => GroupCollections, _ => null };
            if (panel is not null) panel.Visibility = Visibility.Collapsed;
        }

        _library.SystemIntegration.PlaybackStateChanged += OnPlaybackStateChanged;

        Closed += (_, _) =>
        {
            _closed = true;
            _toastTimer?.Stop();
            _importCardDismissTimer?.Stop();
            _dropLeaveTimer?.Stop();
            _library.SystemIntegration.PlaybackStateChanged -= OnPlaybackStateChanged;
            if (ThemeHelper.TryGetRestoredDipSize(this, out int sw, out int sh))
            {
                _library.Prefs.ShelfWindowWidth = sw;
                _library.Prefs.ShelfWindowHeight = sh;
                _library.SavePrefs();
            }
            _podcastBuilder.Dispose();
            try { _reviewClipPlayer?.Pause(); _reviewClipPlayer?.Dispose(); _reviewClipPlayer = null; } catch { }
            try { _quickSearchCts?.Cancel(); _quickSearchCts?.Dispose(); } catch { }
        };
        RootGrid.SizeChanged += (_, e) =>
        {
            bool narrow = e.NewSize.Width > 0 && e.NewSize.Width < 760;
            if (narrow && !_narrowAutoCollapsed)
            {
                _narrowAutoCollapsed = true;
                ApplySidebarVisibility(false);
            }
            else if (!narrow && _narrowAutoCollapsed)
            {
                _narrowAutoCollapsed = false;
                ApplySidebarVisibility(_sidebarVisible);
            }
        };
        PaperSheet.SizeChanged += (_, _) =>
        {
            if (_activeFilter is "all" or "audio" || _activeFilter.StartsWith("col:", StringComparison.Ordinal))
                RenderShelfMain();
            else if (_activeFilter == "progress" && _recordBuilder.OpenBookId is null)
                RenderProgressPage();
            else if (_activeFilter == "collections")
                RenderCollectionsWall();
        };

        MountStaticIcons();
        ApplyAppearance();
        RefreshBooks();
        UpdateSyncFooter();
        TrackActiveShelfScreen();
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                TrackActiveShelfScreen();
        };
    }

    private void TrackActiveShelfScreen()
    {
        string screen = _activeFilter switch
        {
            "notes" or "words" => "Notes",
            "podcasts" => "Podcasts",
            "progress" => "Me",
            _ => "Shelf",
        };
        _library.Analytics.TrackScreen(screen);
    }

    public void RefreshCurrentSubPage()
    {
        ConfigureHeaderForCurrentView();
        RenderCurrentView();
    }

    public void OpenPodcastInline(string issueId, double? seekSec = null, bool autoPlay = false)
    {
        _podcastBuilder.OpenIssueAt(issueId, seekSec, autoPlay);
        _activeFilter = "podcasts";
        UpdateNavSelectionVisuals();
        ConfigureHeaderForCurrentView();
        RenderCurrentView();
        TrackActiveShelfScreen();
    }

    public Task OpenStudioSheetForBookAsync(string bookId, StudioKind kind = StudioKind.BookGuide) =>
        _podcastBuilder.ShowStudioSheetAsync(kind, bookId);

    public void RevealAndHighlightBook(string bookId)
    {
        _highlightedBookId = bookId;
        _activeFilter = "all";
        UpdateNavSelectionVisuals();
        RenderCurrentView();
        TrackActiveShelfScreen();
        _ = Task.Run(async () =>
        {
            await Task.Delay(3500);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                if (_highlightedBookId == bookId)
                {
                    _highlightedBookId = null;
                    if (_activeFilter == "all") RenderShelfMain();
                }
            });
        });
    }

    public async Task PromptAddRecordBookAsync(ReadingStatus defaultStatus)
    {
        if (RootGrid.XamlRoot is null) return;
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        ContentDialog? dlgRef = null;
        BookSummary? added = null;

        var titleBox = new TextBox
        {
            PlaceholderText = L10n.T("书名"),
            FontFamily = serif,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
        };
        YYChrome.StripTextBoxChrome(titleBox);

        void DoAdd()
        {
            string trimmed = titleBox.Text.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;
            try
            {
                added = _library.Core.AddRecordBook(trimmed, [], defaultStatus);
                dlgRef?.Hide();
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, canUndo: false, isDelete: true);
            }
        }

        var addBtn = YYChrome.MakeSheetPillButton("加入", DoAdd, dark, _library.Prefs.Tint, "dark", height: 32, horizontalPadding: 16);
        addBtn.IsEnabled = false;
        addBtn.Opacity = 0.42;

        titleBox.TextChanged += (_, _) =>
        {
            bool ok = !string.IsNullOrWhiteSpace(titleBox.Text);
            addBtn.IsEnabled = ok;
            addBtn.Opacity = ok ? 1.0 : 0.42;
        };
        titleBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Enter && !string.IsNullOrWhiteSpace(titleBox.Text))
            {
                ke.Handled = true;
                DoAdd();
            }
        };

        var actionRow = new Grid { ColumnSpacing = 8 };
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actionRow.Children.Add(new TextBlock
        {
            Text = $"放进 {BookRecordPageBuilder.StatusDisplayName(defaultStatus)}",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var btnPair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        btnPair.Children.Add(YYChrome.MakeSheetPillButton("取消", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 32, horizontalPadding: 14));
        btnPair.Children.Add(addBtn);
        Grid.SetColumn(btnPair, 1);
        actionRow.Children.Add(btnPair);

        var body = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                new TextBlock
                {
                    Text = L10n.T("加一本书"),
                    FontSize = 15,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                },
                titleBox,
                actionRow,
                new TextBlock
                {
                    Text = L10n.T("作者、封面以后在书的页面里补，不补也行。"),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                },
            },
        };

        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 380, body, tint: _library.Prefs.Tint);
        dlgRef.Opened += (_, _) => titleBox.Focus(FocusState.Programmatic);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        if (added is not null)
        {
            _recordBuilder.ActiveStatus = defaultStatus;
            RefreshBooks();
        }
    }

    public void UpdateSyncFooter()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        SyncStatusText.Text = L10n.T(_library.Sync.StatusSummary());
        Color dotColor = _library.Sync.State switch
        {
            SyncStateKind.Syncing => tint.Accent,
            SyncStateKind.Failed => p.Danger,
            _ when _library.Sync.IsEnabled => p.Ok,
            _ => p.T3,
        };
        SyncDot.Fill = ThemeHelper.Brush(dotColor);

        int peerCount = 0;
        if (_library.Sync.IsEnabled)
        {
            try { peerCount = _library.GetDevices().Count; } catch { }
        }
        if (_library.Sync.IsEnabled && peerCount > 1)
        {
            SyncDevicesText.Text = L10n.T($"{peerCount} 台设备");
            SyncDevicesText.Visibility = Visibility.Visible;
        }
        else
        {
            SyncDevicesText.Text = "";
            SyncDevicesText.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateSidebarGroupChevrons()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var t3 = ThemeHelper.Brush(ThemeHelper.Palette(dark).T3);
        foreach (var (key, panel, presenter) in new[]
        {
            ("library", GroupLibrary, GroupChevronLibrary),
            ("notes", GroupNotes, GroupChevronNotes),
            ("collections", GroupCollections, GroupChevronCollections),
        })
        {
            bool collapsed = panel.Visibility == Visibility.Collapsed;
            bool hovered = _hoveredSidebarGroup == key;
            presenter.Content = YYIcons.Make(collapsed ? "chevron-right" : "chevron-down", 10, t3);
            presenter.Opacity = collapsed || hovered ? 1.0 : 0.0;
        }
    }

    private void GroupHeader_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key)
        {
            _hoveredSidebarGroup = key;
            UpdateSidebarGroupChevrons();
        }
    }

    private void GroupHeader_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string key && _hoveredSidebarGroup == key)
        {
            _hoveredSidebarGroup = null;
            UpdateSidebarGroupChevrons();
        }
    }

    private void MountStaticIcons()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var t1 = ThemeHelper.Brush(p.T1);
        var t2 = ThemeHelper.Brush(p.T2);
        var t3 = ThemeHelper.Brush(p.T3);

        SearchIconHost.Content = YYIcons.Make("search", 12, t2);
        QuickSearchLeadIcon.Content = YYIcons.Make("search", 15, t2);
        SidebarToggleBtn.Content = YYIcons.Make("sidebar", 14, t2);
        ImportPlusIcon.Content = YYIcons.Make("plus", 11, t1);
        SortChevronIcon.Content = YYIcons.Make("updown", 11, t2);
        SettingsFootBtn.Content = YYIcons.Make("gear", 14, t2);
        FeedbackFootBtn.Content = YYIcons.Make("feedback", 14, t2);
        EmptyIconHost.Content = YYIcons.Make("books.vertical", 28, t3);
        ToastCheckIcon.Content = YYIcons.Make("checkmark", 9.5, new SolidColorBrush(Colors.White));
        ToastCloseButton.Content = YYIcons.Make("xmark", 9.5, t3);
        DropArrowIcon.Content = YYIcons.Make("arrow-down", 24, ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, dark).Accent));

        IconAll.Content = YYIcons.Make("shelf", 13.5, _activeFilter == "all" ? t1 : t2);
        IconProgress.Content = YYIcons.Make("book", 14, _activeFilter == "progress" ? t1 : t2);
        IconPodcasts.Content = YYIcons.Make("radio", 14, _activeFilter == "podcasts" ? t1 : t2);
        IconAudio.Content = YYIcons.Make("wave", 14, _activeFilter == "audio" ? t1 : t2);
        IconNotes.Content = YYIcons.Make("square.and.pencil", 14, _activeFilter == "notes" ? t1 : t2);
        IconWords.Content = YYIcons.Make("a-wen", 14, _activeFilter == "words" ? t1 : t2);
        IconCollections.Content = YYIcons.Make("stack", 14, _activeFilter == "collections" ? t1 : t2);
        UpdateSidebarGroupChevrons();

        AutomationProperties.SetName(QuickSearchTrigger, L10n.T("搜索 · 问"));
        AutomationProperties.SetName(SyncFooterBtn, L10n.T("同步"));
        AutomationProperties.SetName(SettingsFootBtn, L10n.T("设置"));
        AutomationProperties.SetName(FeedbackFootBtn, L10n.T("反馈"));
        AutomationProperties.SetName(SidebarToggleBtn, L10n.T("显示或隐藏侧栏"));
        AutomationProperties.SetName(ToastCloseButton, L10n.T("关闭"));
        AutomationProperties.SetName(ImportBtn, L10n.T("导入"));
        AutomationProperties.SetName(SortButton, L10n.T("排序"));
    }

    public void ApplyAppearance()
    {
        Title = L10n.T("语阅");
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        ThemeHelper.ConfigureTitleBar(this, dark);
        var pal = ThemeHelper.Palette(dark);
        var tnt = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        DropOverlay.Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)230 : (byte)220, pal.Bg.R, pal.Bg.G, pal.Bg.B));
        DropOverlayInner.Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)28 : (byte)14, tnt.Accent.R, tnt.Accent.G, tnt.Accent.B));
        MountStaticIcons();
        SyncSortMenuVisuals();
        UpdateSyncFooter();

        double coverW = Math.Clamp(_library.Prefs.CoverWidth, 76, 148);
        BooksGridLayout.MinItemWidth = coverW;
        BooksGridLayout.MinItemHeight = coverW * 1.5 + (_library.Prefs.ShowProgress ? 48 : 32);

        RenderCurrentView();
    }

    public void RefreshBooks()
    {
        var (shelf, kept) = _library.GetShelfAndKeptBooks();
        _shelfBooks = shelf;
        _keptBooks = kept;
        _collections = _library.GetCollections();
        _markCounts = _library.GetMarkCounts();
        _words = _library.GetWords();
        try { _listeningPairs = _library.Core.ListeningPairs(); } catch { _listeningPairs = []; }
        _pairedBookIds = _listeningPairs.Select(lp => lp.Book).ToHashSet(StringComparer.Ordinal);
        InvalidateNotesCache();

        UpdateSidebarCounts();
        RebuildCollectionsSidebar();
        UpdateNavSelectionVisuals();
        RenderCurrentView();
    }

    private void UpdateSidebarCounts()
    {
        CountAll.Text = CountText(_shelfBooks.Count);
        CountProgress.Text = CountText(_keptBooks.Count(b => b.Status == ReadingStatus.Reading));
        int podcastCount = 0;
        try { podcastCount = _library.Podcasts.GetIssues().Count(i => i.Placement == PodcastPlacement.Desk); } catch { }
        CountPodcasts.Text = CountText(podcastCount);
        CountAudio.Text = CountText(_shelfBooks.Count(b => b.Format == BookFormat.Audio || _pairedBookIds.Contains(b.Id)));

        int totalNotes = 0;
        foreach (var b in _shelfBooks.Where(b => b.Available))
        {
            if (_markCounts.TryGetValue(b.Id, out uint c)) totalNotes += (int)c;
        }
        CountNotes.Text = CountText(totalNotes);
        CountWords.Text = CountText(_words.Count);
        CountCollections.Text = $"{_collections.Count} ›";
    }

    // 对齐 Mac 01-shelf.png：侧栏数字为 0 时同样显示 0
    private static string CountText(int n) => n.ToString();

    private void RebuildCollectionsSidebar()
    {
        CollectionsStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var t2 = ThemeHelper.Brush(p.T2);

        // 1:1 对齐 MacShelfView.swift lines 359-370：按固定顺序展示已固定合集，支持拖拽排序与从资源管理器直接拖文件入合集
        var colMap = _collections.ToDictionary(c => c.Id);
        foreach (var pinnedId in _library.Prefs.PinnedCollectionIds.ToArray())
        {
            if (!colMap.TryGetValue(pinnedId, out var col)) continue;
            var colItem = col;
            var tag = $"col:{colItem.Id}";
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = YYIcons.Make(YYIcons.CollectionIconName(colItem.Name, colItem.Icon), 13, t2);
            var name = L10n.Skip(new TextBlock { Text = colItem.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(name, 1);
            var cnt = new TextBlock
            {
                Text = CountText((int)colItem.Count),
                FontSize = 11,
                Foreground = ThemeHelper.Brush(p.T3),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(cnt, 2);

            grid.Children.Add(icon);
            grid.Children.Add(name);
            grid.Children.Add(cnt);

            var ctx = new MenuFlyout();
            var pinItem = new MenuFlyoutItem { Text = L10n.T("取消固定") };
            pinItem.Click += (_, _) =>
            {
                _library.Prefs.PinnedCollectionIds.Remove(colItem.Id);
                _library.SavePrefs();
                RefreshBooks();
            };
            var renameItem = new MenuFlyoutItem { Text = L10n.T("重命名…") };
            renameItem.Click += async (_, _) => await RenameCollectionAsync(colItem);
            var iconSub = BuildCollectionIconSubMenu(colItem);
            var delItem = new MenuFlyoutItem { Text = L10n.T("删除合集…"), Foreground = ThemeHelper.Brush(p.Danger) };
            delItem.Click += async (_, _) => await ConfirmDeleteCollectionAsync(colItem);
            ctx.Items.Add(pinItem);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(renameItem);
            ctx.Items.Add(iconSub);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delItem);

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYSidebarItemStyle"],
                Tag = tag,
                AllowDrop = true,
                CanDrag = true,
                Content = grid,
                ContextFlyout = ctx,
            };
            string pinCid = colItem.Id;
            string pinCname = colItem.Name;
            AutomationProperties.SetName(btn, pinCname);
            btn.DragStarting += (_, de) =>
            {
                de.Data.Properties["yuyue-pincol"] = pinCid;
                de.Data.SetText("yuyue-pincol:" + pinCid);
                de.Data.RequestedOperation = DataPackageOperation.Move | DataPackageOperation.Copy;
            };
            btn.DragOver += (_, de) =>
            {
                if (de.DataView.Properties.ContainsKey("yuyue-pincol"))
                {
                    de.AcceptedOperation = DataPackageOperation.Move;
                    de.Handled = true;
                    HideDropOverlay();
                }
                else if (de.DataView.Properties.ContainsKey("yuyue-book-id")
                    || de.DataView.Contains(StandardDataFormats.Text)
                    || de.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    de.AcceptedOperation = DataPackageOperation.Copy;
                    de.Handled = true;
                    HideDropOverlay();
                }
            };
            btn.Drop += async (_, de) =>
            {
                de.Handled = true;
                HideDropOverlay();
                var deferral = de.GetDeferral();
                try
                {
                    if (de.DataView.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await de.DataView.GetStorageItemsAsync();
                        var paths = items.Select(it => it.Path).Where(fp => !string.IsNullOrEmpty(fp)).ToArray();
                        if (paths.Length > 0) await ImportPathsAsync(paths, openFirst: false, targetCollectionId: pinCid, source: "share_in");
                        return;
                    }
                    string? dragBookId = de.DataView.Properties.TryGetValue("yuyue-book-id", out var bv) ? bv as string : null;
                    string? dragPinId = de.DataView.Properties.TryGetValue("yuyue-pincol", out var pv) ? pv as string : null;
                    if (dragBookId is null && dragPinId is null && de.DataView.Contains(StandardDataFormats.Text))
                    {
                        string txt = await de.DataView.GetTextAsync();
                        if (txt.StartsWith("yuyue-book:", StringComparison.Ordinal)) dragBookId = txt["yuyue-book:".Length..];
                        else if (txt.StartsWith("yuyue-pincol:", StringComparison.Ordinal)) dragPinId = txt["yuyue-pincol:".Length..];
                    }
                    if (!string.IsNullOrEmpty(dragBookId))
                    {
                        _library.SetInCollection(pinCid, dragBookId, true);
                        RefreshBooks();
                        ShowToast($"已放进合集“{pinCname}”", canUndo: false);
                    }
                    else if (!string.IsNullOrEmpty(dragPinId))
                    {
                        var pins = _library.Prefs.PinnedCollectionIds;
                        int fromIdx = pins.IndexOf(dragPinId);
                        int toIdx = pins.IndexOf(pinCid);
                        if (fromIdx >= 0 && toIdx >= 0 && fromIdx != toIdx)
                        {
                            pins.RemoveAt(fromIdx);
                            pins.Insert(toIdx, dragPinId);
                            _library.SavePrefs();
                            RebuildCollectionsSidebar();
                            UpdateNavSelectionVisuals();
                        }
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };
            btn.Click += NavItem_Click;
            CollectionsStack.Children.Add(btn);
        }
    }

    private void NavCollections_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Properties.ContainsKey("yuyue-pincol")) return;
        if (e.DataView.Properties.ContainsKey("yuyue-book-id") || e.DataView.Contains(StandardDataFormats.Text))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.Handled = true;
            HideDropOverlay();
            bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
            var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
            NavCollections.Background = ThemeHelper.Brush(tint.Soft);
        }
    }

    private void NavCollections_DragLeave(object sender, DragEventArgs e) => UpdateNavSelectionVisuals();

    private async void NavCollections_Drop(object sender, DragEventArgs e)
    {
        UpdateNavSelectionVisuals();
        if (e.DataView.Properties.ContainsKey("yuyue-pincol")) return;
        string? bid = e.DataView.Properties.TryGetValue("yuyue-book-id", out var bv) ? bv as string : null;
        if (bid is null && e.DataView.Contains(StandardDataFormats.Text))
        {
            e.Handled = true;
            var def = e.GetDeferral();
            try
            {
                string txt = await e.DataView.GetTextAsync();
                if (txt.StartsWith("yuyue-book:", StringComparison.Ordinal))
                    bid = txt["yuyue-book:".Length..];
            }
            finally
            {
                def.Complete();
            }
        }
        else if (bid is not null)
        {
            e.Handled = true;
        }

        if (!string.IsNullOrEmpty(bid))
        {
            var book = _keptBooks.FirstOrDefault(b => b.Id == bid)
                ?? _shelfBooks.FirstOrDefault(b => b.Id == bid);
            if (book is not null)
                await ShowPickCollectionDialogAsync(book);
        }
    }

    private async Task RenameCollectionAsync(BookCollection col)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        string? nextName = await YYChrome.ShowPromptAsync(
            RootGrid.XamlRoot,
            dark,
            "重命名合集",
            placeholder: "合集的名字",
            initialText: col.Name,
            confirmText: "保存",
            cancelText: "取消",
            tint: _library.Prefs.Tint);
        if (!string.IsNullOrWhiteSpace(nextName))
        {
            try
            {
                _library.Core.RenameCollection(col.Id, nextName.Trim());
                RefreshBooks();
            }
            catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
        }
    }

    private async Task ConfirmDeleteCollectionAsync(BookCollection col)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        bool confirmed = await YYChrome.ShowConfirmAsync(
            RootGrid.XamlRoot,
            dark,
            $"删除合集“{col.Name}”？",
            "这个合集删掉，里面的书还在书架上。",
            confirmText: "删除",
            cancelText: "取消",
            destructive: true,
            tint: _library.Prefs.Tint);
        if (confirmed)
        {
            string colId = col.Id;
            string colName = col.Name;
            bool wasViewingCol = _activeFilter == $"col:{colId}";
            bool wasPinned = _library.Prefs.PinnedCollectionIds.Contains(colId);

            if (wasViewingCol) _activeFilter = _cameFromCollectionsWall ? "collections" : "all";
            _library.Prefs.PinnedCollectionIds.Remove(colId);
            _library.SavePrefs();
            _library.DeleteCollection(colId);
            RefreshBooks();
            ShowToast($"已删除合集“{colName}”", canUndo: true, isDelete: true, undo: () =>
            {
                try
                {
                    _library.Core.RestoreCollection(colId);
                    if (wasPinned && !_library.Prefs.PinnedCollectionIds.Contains(colId))
                    {
                        _library.Prefs.PinnedCollectionIds.Add(colId);
                        _library.SavePrefs();
                    }
                    if (wasViewingCol) _activeFilter = $"col:{colId}";
                }
                catch { }
                RefreshBooks();
            });
        }
    }

    private void UpdateNavSelectionVisuals()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var selectedBg = ThemeHelper.Brush(dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(16, 42, 43, 46));
        var transparent = new SolidColorBrush(Colors.Transparent);
        bool viewingUnpinnedCol = _activeFilter.StartsWith("col:", StringComparison.Ordinal)
            && !_library.Prefs.PinnedCollectionIds.Contains(_activeFilter[4..]);

        Button[] fixedButtons = [NavAll, NavProgress, NavPodcasts, NavAudio, NavNotes, NavWords, NavCollections];
        foreach (var btn in fixedButtons)
        {
            bool on = Equals(btn.Tag, _activeFilter) || (btn == NavCollections && viewingUnpinnedCol);
            btn.Background = on ? selectedBg : transparent;
            btn.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        foreach (var btn in CollectionsStack.Children.OfType<Button>())
        {
            bool on = Equals(btn.Tag, _activeFilter);
            btn.Background = on ? selectedBg : transparent;
            btn.FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }

        MountStaticIcons();

        ViewTitleText.Text = _activeFilter switch
        {
            "progress" => "进度",
            "podcasts" => "播客",
            "audio" => "有声",
            "notes" => "笔记",
            "words" => "生词本",
            "collections" => "全部合集",
            _ when _activeFilter.StartsWith("col:", StringComparison.Ordinal) =>
                _collections.FirstOrDefault(c => $"col:{c.Id}" == _activeFilter)?.Name ?? "合集",
            _ => "书",
        };
    }

    public void SetBottomDock(Border? dock)
    {
        bool isPodcasts = _activeFilter == "podcasts";
        if (dock is null)
        {
            SubPageBottomDockHost.Content = null;
            SubPageBottomDockHost.Visibility = Visibility.Collapsed;
            SubPageScroll.Padding = new Thickness(22, 4, 28, isPodcasts ? 0 : 40);
            ShelfContentStack.Padding = new Thickness(0, 0, 0, 48);
            return;
        }
        ThemeHelper.ApplyElevation(dock, PaperInnerGrid, 14f);
        SubPageBottomDockHost.Content = dock;
        SubPageBottomDockHost.Visibility = Visibility.Visible;
        SubPageScroll.Padding = new Thickness(22, 4, 28, isPodcasts ? 0 : 86);
        ShelfContentStack.Padding = new Thickness(0, 0, 0, isPodcasts ? 48 : 86);
    }

    private Border BuildGlobalNowPlayingBar(string activeTitle)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        outer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var leftGrid = new Grid { ColumnSpacing = 10 };
        leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        leftGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        leftGrid.Children.Add(new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = ThemeHelper.Brush(tint.Soft),
            VerticalAlignment = VerticalAlignment.Center,
            Child = YYIcons.Make("headphones", 12.5, ThemeHelper.Brush(tint.Accent)),
        });

        var textStack = new StackPanel { Spacing = 0.5, VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(L10n.Skip(new TextBlock
        {
            Text = activeTitle,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));
        textStack.Children.Add(new TextBlock
        {
            Text = L10n.T("正在收听 · 点击回到播放页"),
            FontSize = 10,
            Foreground = ThemeHelper.Brush(palette.T3),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(textStack, 1);
        leftGrid.Children.Add(textStack);

        var surfaceBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Padding = new Thickness(12, 0, 8, 0),
            CornerRadius = new CornerRadius(22, 0, 0, 22),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            UseSystemFocusVisuals = false,
            Content = leftGrid,
        };
        AutomationProperties.SetName(surfaceBtn, $"{activeTitle} · {L10n.T("正在收听 · 点击回到播放页")}");
        surfaceBtn.Click += (_, _) => App.CurrentApp.ActivateActiveAudioSurface();
        outer.Children.Add(surfaceBtn);

        var toggleBtn = new Button
        {
            Width = 30,
            Height = 30,
            MinWidth = 30,
            MinHeight = 30,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 7, 0),
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(tint.Soft),
            BorderBrush = ThemeHelper.Brush(tint.Edge),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = false,
            Content = YYIcons.Make("pause", 11.5, ThemeHelper.Brush(tint.Accent), filled: true),
        };
        ToolTipService.SetToolTip(toggleBtn, L10n.T("暂停"));
        AutomationProperties.SetName(toggleBtn, L10n.T("暂停"));
        toggleBtn.Click += (_, _) =>
        {
            if (_library.SystemIntegration.TogglePlaybackCallback is Action cb)
                cb();
            else
                App.CurrentApp.ActivateActiveAudioSurface();
        };
        Grid.SetColumn(toggleBtn, 1);
        outer.Children.Add(toggleBtn);

        return new Border
        {
            Height = 44,
            MinWidth = 300,
            MaxWidth = 520,
            CornerRadius = new CornerRadius(22),
            Background = ThemeHelper.Brush(palette.Card),
            BorderBrush = ThemeHelper.Brush(palette.Line),
            BorderThickness = new Thickness(1),
            Child = outer,
        };
    }

    private void UpdateGlobalNowPlayingDock()
    {
        if (_activeFilter == "podcasts") return;
        if (_library.SystemIntegration.IsAudioPlaying
            && !string.IsNullOrWhiteSpace(_library.SystemIntegration.ActivePlaybackTitle))
        {
            SetBottomDock(BuildGlobalNowPlayingBar(_library.SystemIntegration.ActivePlaybackTitle));
        }
        else
        {
            SetBottomDock(null);
        }
    }

    private void OnPlaybackStateChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            if (_activeFilter != "podcasts")
            {
                UpdateGlobalNowPlayingDock();
            }
            if (_activeFilter is "all" or "audio" || _activeFilter.StartsWith("col:", StringComparison.Ordinal))
            {
                RenderShelfMain();
            }
        });
    }

    private bool IsBookCurrentlyPlaying(BookSummary book)
    {
        string? activeBookId = App.CurrentApp.GetActiveAudioBookId();
        if (!string.IsNullOrEmpty(activeBookId))
        {
            if (activeBookId == book.Id) return true;
            if (_listeningPairs.Any(lp => (lp.Book == book.Id && lp.Recording == activeBookId) || (lp.Recording == book.Id && lp.Book == activeBookId)))
                return true;
        }
        return _library.SystemIntegration.IsPlayingTitle(book.Title);
    }

    private void RenderCurrentView()
    {
        SetBottomDock(null);
        ConfigureHeaderForCurrentView();
        switch (_activeFilter)
        {
            case "progress":
                ShelfScroll.Visibility = Visibility.Collapsed;
                SubPageScroll.Visibility = Visibility.Visible;
                RenderProgressPage();
                break;
            case "podcasts":
                ShelfScroll.Visibility = Visibility.Collapsed;
                SubPageScroll.Visibility = Visibility.Visible;
                RenderPodcastsPage();
                break;
            case "notes":
                ShelfScroll.Visibility = Visibility.Collapsed;
                SubPageScroll.Visibility = Visibility.Visible;
                RenderNotesPage();
                break;
            case "words":
                ShelfScroll.Visibility = Visibility.Collapsed;
                SubPageScroll.Visibility = Visibility.Visible;
                RenderWordsPage();
                break;
            case "collections":
                ShelfScroll.Visibility = Visibility.Collapsed;
                SubPageScroll.Visibility = Visibility.Visible;
                RenderCollectionsWall();
                break;
            default:
                SubPageScroll.Visibility = Visibility.Collapsed;
                ShelfScroll.Visibility = Visibility.Visible;
                RenderShelfMain();
                break;
        }
        if (_activeFilter != "podcasts")
        {
            UpdateGlobalNowPlayingDock();
        }
        L10n.ApplyTree(RootGrid);
        if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
        {
            ViewTitleText.Text = _collections.FirstOrDefault(c => $"col:{c.Id}" == _activeFilter)?.Name ?? L10n.T("合集");
        }
    }

    private void ConfigureHeaderForCurrentView()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var t1 = ThemeHelper.Brush(p.T1);

        HeaderLeftCustomHost.Children.Clear();
        HeaderRightHost.Children.Clear();
        _activeNotesFilterBox = null;
        _activeWordsFilterBox = null;
        _activeCollectionsFilterBox = null;
        ViewTitleText.Visibility = Visibility.Visible;
        ViewSubtitleText.Text = "";

        Border BuildHeaderSegments(bool secondSelected, string leftLabel, string rightLabel, Action<bool> onSelect)
        {
            var grid = new Grid { ColumnSpacing = 2, Width = 148 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Button Seg(int col, string text, bool on, bool val)
            {
                var b = new Button
                {
                    Content = text,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(6, 0, 6, 0),
                    FontSize = 11.5,
                    FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(on ? p.T1 : p.T2),
                    Background = on ? ThemeHelper.Brush(p.Card) : new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(7),
                    UseSystemFocusVisuals = false,
                };
                b.Click += (_, _) => onSelect(val);
                Grid.SetColumn(b, col);
                return b;
            }

            grid.Children.Add(Seg(0, leftLabel, !secondSelected, false));
            grid.Children.Add(Seg(1, rightLabel, secondSelected, true));
            return new Border
            {
                Background = ThemeHelper.Brush(p.Inset),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(3),
                Child = grid,
            };
        }

        Button PillWithIcon(string icon, string label, RoutedEventHandler onClick)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(YYIcons.Make(icon, 11, t1));
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var btn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = sp };
            btn.Click += onClick;
            return btn;
        }

        switch (_activeFilter)
        {
            case "progress":
                if (_recordBuilder.OpenBookId is not null)
                {
                    ViewTitleText.Visibility = Visibility.Collapsed;
                    var backSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
                    backSp.Children.Add(YYIcons.Make("chevron-left", 11.5, t1));
                    backSp.Children.Add(new TextBlock { Text = "进度", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = t1 });
                    var backBtn = new Button
                    {
                        Height = 30,
                        MinHeight = 30,
                        Padding = new Thickness(10, 0, 12, 0),
                        CornerRadius = new CornerRadius(15),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        UseSystemFocusVisuals = false,
                        Content = backSp,
                    };
                    AutomationProperties.SetName(backBtn, L10n.T("进度"));
                    backBtn.Click += (_, _) =>
                    {
                        _recordBuilder.OpenBookId = null;
                        RefreshCurrentSubPage();
                    };
                    HeaderLeftCustomHost.Children.Add(backBtn);
                }
                else
                {
                    ViewSubtitleText.Text = $"{_keptBooks.Count} 本";
                    HeaderRightHost.Children.Add(PillWithIcon("plus", "加书", async (_, _) => await PromptAddRecordBookAsync(_recordBuilder.ActiveStatus)));
                }
                break;

            case "podcasts":
                ViewTitleText.Visibility = Visibility.Collapsed;
                _podcastBuilder.PopulateTopHeader(HeaderLeftCustomHost, HeaderRightHost, dark);
                break;

            case "notes":
            {
                int totalMarks = 0;
                int booksCnt = 0;
                foreach (var b in _keptBooks)
                {
                    if (_markCounts.TryGetValue(b.Id, out uint c) && c > 0) { totalMarks += (int)c; booksCnt++; }
                }
                if (totalMarks > 0) ViewSubtitleText.Text = $"{totalMarks} 条 · 来自 {booksCnt} 本书";

                if (totalMarks > 0 || !string.IsNullOrEmpty(_notesQuery))
                {
                    var notesFilterWrap = new Border
                    {
                        Height = 28,
                        Width = 164,
                        CornerRadius = new CornerRadius(14),
                        Background = ThemeHelper.Brush(p.Inset),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(9, 0, 6, 0),
                    };
                    var nfg = new Grid { ColumnSpacing = 5 };
                    nfg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    nfg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    nfg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    nfg.Children.Add(YYIcons.Make("search", 11, ThemeHelper.Brush(p.T3)));

                    var notesFilterBox = new TextBox
                    {
                        Text = _notesQuery,
                        PlaceholderText = "找笔记",
                        FontSize = 11.5,
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        Padding = new Thickness(0, 4, 0, 0),
                        MinHeight = 24,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(notesFilterBox);
                    _activeNotesFilterBox = notesFilterBox;
                    notesFilterBox.TextChanged += (_, _) =>
                    {
                        _notesQuery = notesFilterBox.Text ?? "";
                        RenderNotesPage();
                    };
                    Grid.SetColumn(notesFilterBox, 1);
                    nfg.Children.Add(notesFilterBox);

                    if (!string.IsNullOrEmpty(_notesQuery))
                    {
                        var clearNotesBtn = new Button
                        {
                            Width = 18,
                            Height = 18,
                            MinWidth = 18,
                            MinHeight = 18,
                            Padding = new Thickness(0),
                            CornerRadius = new CornerRadius(9),
                            BorderThickness = new Thickness(0),
                            Background = new SolidColorBrush(Colors.Transparent),
                            Content = YYIcons.Make("xmark", 9, ThemeHelper.Brush(p.T3)),
                        };
                        AutomationProperties.SetName(clearNotesBtn, L10n.T("清除搜索"));
                        clearNotesBtn.Click += (_, _) =>
                        {
                            _notesQuery = "";
                            ConfigureHeaderForCurrentView();
                            RenderNotesPage();
                        };
                        Grid.SetColumn(clearNotesBtn, 2);
                        nfg.Children.Add(clearNotesBtn);
                    }
                    notesFilterWrap.Child = nfg;
                    HeaderRightHost.Children.Add(notesFilterWrap);
                }

                HeaderRightHost.Children.Add(BuildHeaderSegments(_notesByBook, "按时间", "按书", byBook =>
                {
                    _notesByBook = byBook;
                    ConfigureHeaderForCurrentView();
                    RenderNotesPage();
                }));

                var expSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                expSp.Children.Add(YYIcons.Make("share-up", 11, t1));
                var expBtnLabel = new TextBlock { Text = "导出", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                expSp.Children.Add(expBtnLabel);
                var noteMenu = new MenuFlyout();
                var exportNotesBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = expSp,
                    Flyout = noteMenu,
                    IsEnabled = totalMarks > 0,
                };
                AutomationProperties.SetName(exportNotesBtn, L10n.T("导出"));

                void SetNotesExportBusy(bool busy)
                {
                    if (_closed) return;
                    exportNotesBtn.IsEnabled = !busy && totalMarks > 0;
                    expBtnLabel.Text = L10n.T(busy ? "导出中…" : "导出");
                }

                // Fix first-visit 按书 scope: fall back to most recently marked book if _notesChosenBookId isn't set yet
                BookSummary? chosenBook = null;
                if (_notesByBook)
                {
                    chosenBook = _keptBooks.FirstOrDefault(b => b.Id == _notesChosenBookId)
                        ?? _keptBooks
                            .Select(b => (Book: b, Marks: GetCachedMarks(b.Id)))
                            .Where(x => x.Marks.Count > 0)
                            .OrderByDescending(x => x.Marks.Max(m => m.At))
                            .FirstOrDefault().Book;
                    if (chosenBook is not null) _notesChosenBookId = chosenBook.Id;
                }

                var mdItem = new MenuFlyoutItem { Text = chosenBook is not null ? "导出这本书的 Markdown…" : "导出为 Markdown…" };
                mdItem.Click += async (_, _) =>
                {
                    string? outPath = await NativePickerHelper.PickSaveFileAsync(
                        this,
                        chosenBook is not null ? $"{chosenBook.Title}-笔记" : $"语阅笔记-{DateTime.Now:yyyyMMdd}",
                        [("Markdown", [".md"])]);
                    if (string.IsNullOrEmpty(outPath)) return;
                    string chipSnap = _notesChip;
                    string querySnap = _notesQuery;
                    var keptSnap = _keptBooks.ToArray();
                    SetNotesExportBusy(true);
                    try
                    {
                        string md = await Task.Run(() => BuildFilteredNotesMarkdownCore(chosenBook, keptSnap, chipSnap, querySnap));
                        await File.WriteAllTextAsync(outPath, md);
                        if (!_closed) ShowToast($"已导出笔记到 {Path.GetFileName(outPath)}", canUndo: false);
                    }
                    catch (Exception ex) { if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true); }
                    finally { SetNotesExportBusy(false); }
                };
                noteMenu.Items.Add(mdItem);
                noteMenu.Items.Add(new MenuFlyoutSeparator());

                bool anyConnected = false;
                if (_library.Prefs.ObsidianExportOn && !string.IsNullOrWhiteSpace(_library.Prefs.ObsidianFolder) && Directory.Exists(_library.Prefs.ObsidianFolder))
                {
                    anyConnected = true;
                    var obsItem = new MenuFlyoutItem { Text = "发到 Obsidian 库" };
                    obsItem.Click += async (_, _) =>
                    {
                        SetNotesExportBusy(true);
                        try
                        {
                            var (booksN, marksN) = await _library.NoteExports.ExportToObsidianAsync(chosenBook);
                            if (!_closed) ShowToast($"已发送 {marksN} 条笔记到 Obsidian（{booksN} 本书）", canUndo: false);
                        }
                        catch (Exception ex) { if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true); }
                        finally { SetNotesExportBusy(false); }
                    };
                    noteMenu.Items.Add(obsItem);
                }

                if (_library.Prefs.NotionExportOn && !string.IsNullOrWhiteSpace(_library.Secrets.Get("export.notion.token")))
                {
                    anyConnected = true;
                    var notItem = new MenuFlyoutItem { Text = "发到 Notion" };
                    notItem.Click += async (_, _) =>
                    {
                        SetNotesExportBusy(true);
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToNotionAsync(chosenBook);
                            if (!_closed) ShowToast(marksN > 0 ? $"已增量同步 {marksN} 条新笔记到 Notion" : "Notion 已是最新，没有新笔记需要发送", canUndo: false);
                        }
                        catch (Exception ex) { if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true); }
                        finally { SetNotesExportBusy(false); }
                    };
                    noteMenu.Items.Add(notItem);
                }

                if (_library.Prefs.FlomoExportOn && NoteExportService.ValidateFlomoWebhook(_library.Secrets.Get("export.flomo.url") is { Length: > 0 } fu ? fu : _library.Prefs.FlomoWebhookUrl))
                {
                    anyConnected = true;
                    var floItem = new MenuFlyoutItem { Text = "发到 flomo" };
                    floItem.Click += async (_, _) =>
                    {
                        SetNotesExportBusy(true);
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToFlomoAsync(chosenBook);
                            if (!_closed) ShowToast(marksN > 0 ? $"已发送 {marksN} 条卡片到 flomo" : "flomo 已是最新，没有未发过的划线", canUndo: false);
                        }
                        catch (Exception ex) { if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true); }
                        finally { SetNotesExportBusy(false); }
                    };
                    noteMenu.Items.Add(floItem);
                }

                if (_library.Prefs.XinzhiExportOn && !string.IsNullOrWhiteSpace(_library.Secrets.Get("export.xinzhi.token")))
                {
                    anyConnected = true;
                    var xzItem = new MenuFlyoutItem { Text = "发到 新枝" };
                    xzItem.Click += async (_, _) =>
                    {
                        SetNotesExportBusy(true);
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToXinzhiAsync(chosenBook);
                            if (!_closed) ShowToast(marksN > 0 ? $"已发送 {marksN} 条笔记到新枝" : "新枝已是最新，没有未发过的划线", canUndo: false);
                        }
                        catch (Exception ex) { if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true); }
                        finally { SetNotesExportBusy(false); }
                    };
                    noteMenu.Items.Add(xzItem);
                }

                if (!anyConnected)
                {
                    var cfgObs = new MenuFlyoutItem { Text = "连接 Obsidian / Notion / flomo / 新枝…" };
                    cfgObs.Click += (_, _) => App.CurrentApp.OpenSettings("export");
                    noteMenu.Items.Add(cfgObs);
                }

                if (chosenBook is not null && chosenBook.Format is not BookFormat.Pdf and not BookFormat.Audio && !chosenBook.Record)
                {
                    noteMenu.Items.Add(new MenuFlyoutSeparator());
                    var epubItem = new MenuFlyoutItem { Text = "导出带笔记的 EPUB (.epub)…" };
                    epubItem.Click += async (_, _) =>
                    {
                        string? outPath = await NativePickerHelper.PickSaveFileAsync(this, $"{chosenBook.Title}（带笔记）", [(L10n.T("EPUB 电子书"), [".epub"])]);
                        if (string.IsNullOrEmpty(outPath)) return;
                        SetNotesExportBusy(true);
                        try
                        {
                            await _library.NoteExports.ExportAnnotatedEpubAsync(chosenBook.Id, outPath);
                            if (!_closed) ShowToast($"已导出 {Path.GetFileName(outPath)}", canUndo: false);
                        }
                        catch (Exception ex) { if (!_closed) ShowToast($"导出失败：{ex.Message}", canUndo: false, isDelete: true); }
                        finally { SetNotesExportBusy(false); }
                    };
                    noteMenu.Items.Add(epubItem);
                }

                HeaderRightHost.Children.Add(exportNotesBtn);
                break;
            }

            case "words":
            {
                int learning = _words.Count(w => !w.Known);
                int known = _words.Count(w => w.Known);
                if (_words.Count > 0) ViewSubtitleText.Text = $"在学 {learning} · 已掌握 {known}";

                if (_words.Count > 0 || !string.IsNullOrEmpty(_wordsQuery))
                {
                    var wordsFilterWrap = new Border
                    {
                        Height = 28,
                        Width = 164,
                        CornerRadius = new CornerRadius(14),
                        Background = ThemeHelper.Brush(p.Inset),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(9, 0, 6, 0),
                    };
                    var wfg = new Grid { ColumnSpacing = 5 };
                    wfg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    wfg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    wfg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    wfg.Children.Add(YYIcons.Make("search", 11, ThemeHelper.Brush(p.T3)));

                    var wordsFilterBox = new TextBox
                    {
                        Text = _wordsQuery,
                        PlaceholderText = "找生词",
                        FontSize = 11.5,
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        Padding = new Thickness(0, 4, 0, 0),
                        MinHeight = 24,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(wordsFilterBox);
                    _activeWordsFilterBox = wordsFilterBox;
                    wordsFilterBox.TextChanged += (_, _) =>
                    {
                        _wordsQuery = wordsFilterBox.Text ?? "";
                        RenderWordsPage();
                    };
                    Grid.SetColumn(wordsFilterBox, 1);
                    wfg.Children.Add(wordsFilterBox);

                    if (!string.IsNullOrEmpty(_wordsQuery))
                    {
                        var clearWordsBtn = new Button
                        {
                            Width = 18,
                            Height = 18,
                            MinWidth = 18,
                            MinHeight = 18,
                            Padding = new Thickness(0),
                            CornerRadius = new CornerRadius(9),
                            BorderThickness = new Thickness(0),
                            Background = new SolidColorBrush(Colors.Transparent),
                            Content = YYIcons.Make("xmark", 9, ThemeHelper.Brush(p.T3)),
                        };
                        AutomationProperties.SetName(clearWordsBtn, L10n.T("清除搜索"));
                        clearWordsBtn.Click += (_, _) =>
                        {
                            _wordsQuery = "";
                            ConfigureHeaderForCurrentView();
                            RenderWordsPage();
                        };
                        Grid.SetColumn(clearWordsBtn, 2);
                        wfg.Children.Add(clearWordsBtn);
                    }
                    wordsFilterWrap.Child = wfg;
                    HeaderRightHost.Children.Add(wordsFilterWrap);
                }

                HeaderRightHost.Children.Add(BuildHeaderSegments(_wordsByBook, "按时间", "按书", byBook =>
                {
                    _wordsByBook = byBook;
                    ConfigureHeaderForCurrentView();
                    RenderWordsPage();
                }));

                var expSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                expSp.Children.Add(YYIcons.Make("share-up", 11, t1));
                expSp.Children.Add(new TextBlock { Text = "导出", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var expMenu = new MenuFlyout();
                var ankiItem = new MenuFlyoutItem { Text = "Anki 卡片 (.txt)…" };
                ankiItem.Click += async (_, _) =>
                {
                    string? outPath = await NativePickerHelper.PickSaveFileAsync(this, "语阅生词 Anki", [(L10n.T("Anki 制表符卡片"), [".txt"])]);
                    if (string.IsNullOrEmpty(outPath)) return;
                    var sb = new System.Text.StringBuilder("#separator:tab\n#html:true\n#tags column:3\n");
                    foreach (var w in GetFilteredWords())
                    {
                        string front = $"<b>{System.Net.WebUtility.HtmlEncode(w.Word)}</b>" + (string.IsNullOrEmpty(w.Phonetic) ? "" : $" <span style=\"color:#666\">/{System.Net.WebUtility.HtmlEncode(w.Phonetic.Trim('/'))}/</span>");
                        string ctxHtml = "";
                        if (!string.IsNullOrEmpty(w.Context))
                        {
                            string encCtx = System.Net.WebUtility.HtmlEncode(w.Context);
                            string encWord = System.Net.WebUtility.HtmlEncode(w.Word);
                            int pos = encCtx.IndexOf(encWord, StringComparison.OrdinalIgnoreCase);
                            if (pos >= 0)
                                encCtx = $"{encCtx[..pos]}<b>{encCtx.Substring(pos, encWord.Length)}</b>{encCtx[(pos + encWord.Length)..]}";
                            ctxHtml = $"<br><i>{encCtx}</i>";
                        }
                        string bookTitle = ResolveVocabSourceTitle(w.Book);
                        string citeHtml = string.IsNullOrEmpty(bookTitle) ? "" : $"<br><small>——《{System.Net.WebUtility.HtmlEncode(bookTitle)}》</small>";
                        string back = System.Net.WebUtility.HtmlEncode(w.Meaning ?? "").Replace("\n", "<br>") + ctxHtml + citeHtml;
                        sb.AppendLine($"{front}\t{back}\t语阅");
                    }
                    await File.WriteAllTextAsync(outPath, sb.ToString());
                    ShowToast($"已导出 Anki 卡片到 {Path.GetFileName(outPath)}", canUndo: false);
                };
                var csvItem = new MenuFlyoutItem { Text = "CSV 表格 (.csv)…" };
                csvItem.Click += async (_, _) =>
                {
                    string? outPath = await NativePickerHelper.PickSaveFileAsync(this, "语阅生词", [("CSV", [".csv"])]);
                    if (string.IsNullOrEmpty(outPath)) return;
                    static string CsvCell(string? s)
                    {
                        string v = (s ?? "").Replace("\r\n", " ").Replace('\n', ' ').Replace("\"", "\"\"");
                        return $"\"{v}\"";
                    }
                    string csvHeader = L10n.T("单词,音标,释义,原句,出处,加入时间,掌握了");
                    if (csvHeader == "单词,音标,释义,原句,出处,加入时间,掌握了" && L10n.T("是") != "是")
                    {
                        csvHeader = string.Join(",",
                            L10n.T("单词"), L10n.T("音标"), L10n.T("释义"), L10n.T("原句"),
                            L10n.T("出处"), L10n.T("加入时间"), L10n.T("已掌握"));
                    }
                    var csvSb = new System.Text.StringBuilder(csvHeader + "\n");
                    var scope = GetFilteredWords();
                    foreach (var w in scope)
                    {
                        string added = DateTimeOffset.FromUnixTimeMilliseconds(w.At).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
                        string bookTitle = ResolveVocabSourceTitle(w.Book);
                        csvSb.AppendLine(string.Join(",",
                            CsvCell(w.Word),
                            CsvCell(w.Phonetic),
                            CsvCell(w.Meaning),
                            CsvCell(w.Context),
                            CsvCell(bookTitle),
                            CsvCell(added),
                            CsvCell(L10n.T(w.Known ? "是" : "否"))));
                    }
                    await File.WriteAllTextAsync(outPath, csvSb.ToString(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    ShowToast($"已导出 CSV 到 {Path.GetFileName(outPath)}", canUndo: false);
                };
                expMenu.Items.Add(ankiItem);
                expMenu.Items.Add(csvItem);
                var expWordBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = expSp,
                    Flyout = expMenu,
                    IsEnabled = _words.Count > 0,
                };
                AutomationProperties.SetName(expWordBtn, L10n.T("导出"));
                HeaderRightHost.Children.Add(expWordBtn);
                break;
            }

            case "collections":
            {
                if (_collections.Count > 0)
                {
                    int totalInCols = _keptBooks.Count(b => b.Collections.Length > 0);
                    ViewSubtitleText.Text = totalInCols > 0
                        ? $"{_collections.Count} 个 · 收纳 {totalInCols} 本"
                        : $"{_collections.Count} 个";
                }
                if (_collections.Count > 0 || !string.IsNullOrEmpty(_collectionQuery))
                {
                    var filterWrap = new Border
                    {
                        Height = 28,
                        Width = 164,
                        CornerRadius = new CornerRadius(14),
                        Background = ThemeHelper.Brush(p.Inset),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(0.5),
                        Padding = new Thickness(9, 0, 6, 0),
                    };
                    var fg = new Grid { ColumnSpacing = 5 };
                    fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    fg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    fg.Children.Add(YYIcons.Make("search", 11, ThemeHelper.Brush(p.T3)));

                    var filterBox = new TextBox
                    {
                        Text = _collectionQuery,
                        PlaceholderText = "找合集",
                        FontSize = 11.5,
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        Padding = new Thickness(0, 4, 0, 0),
                        MinHeight = 24,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    YYChrome.StripTextBoxChrome(filterBox);
                    _activeCollectionsFilterBox = filterBox;
                    filterBox.TextChanged += (_, _) =>
                    {
                        _collectionQuery = filterBox.Text ?? "";
                        RenderCollectionsWall();
                    };
                    Grid.SetColumn(filterBox, 1);
                    fg.Children.Add(filterBox);

                    if (!string.IsNullOrEmpty(_collectionQuery))
                    {
                        var clearBtn = new Button
                        {
                            Width = 18,
                            Height = 18,
                            Padding = new Thickness(0),
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            Content = new TextBlock { Text = "×", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3) },
                            UseSystemFocusVisuals = false,
                        };
                        AutomationProperties.SetName(clearBtn, L10n.T("清除搜索"));
                        clearBtn.Click += (_, _) =>
                        {
                            _collectionQuery = "";
                            ConfigureHeaderForCurrentView();
                            RenderCollectionsWall();
                        };
                        Grid.SetColumn(clearBtn, 2);
                        fg.Children.Add(clearBtn);
                    }
                    filterWrap.Child = fg;
                    HeaderRightHost.Children.Add(filterWrap);
                }
                HeaderRightHost.Children.Add(PillWithIcon("plus", "新建合集", NewCollection_Click));
                break;
            }

            default:
                if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
                {
                    string activeCid = _activeFilter[4..];
                    var activeCol = _collections.FirstOrDefault(c => c.Id == activeCid);
                    bool isPinned = activeCol is not null && _library.Prefs.PinnedCollectionIds.Contains(activeCol.Id);

                    if (_cameFromCollectionsWall || !isPinned)
                    {
                        var backSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                        backSp.Children.Add(YYIcons.Make("chevron-left", 10.5, ThemeHelper.Brush(p.T2)));
                        backSp.Children.Add(new TextBlock { Text = "合集", FontSize = 12, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
                        var backBtn = new Button
                        {
                            Background = new SolidColorBrush(Colors.Transparent),
                            BorderThickness = new Thickness(0),
                            CornerRadius = new CornerRadius(7),
                            Height = 26,
                            MinHeight = 26,
                            Padding = new Thickness(4, 0, 6, 0),
                            UseSystemFocusVisuals = false,
                            Content = backSp,
                        };
                        ToolTipService.SetToolTip(backBtn, "返回全部合集 (Ctrl+[)");
                        AutomationProperties.SetName(backBtn, L10n.T("返回全部合集 (Ctrl+[)"));
                        backBtn.Click += (_, _) =>
                        {
                            _cameFromCollectionsWall = false;
                            _activeFilter = "collections";
                            UpdateNavSelectionVisuals();
                            RenderCurrentView();
                        };
                        HeaderLeftCustomHost.Children.Add(backBtn);
                    }

                    if (activeCol is not null)
                    {
                        var colMenu = new MenuFlyout();
                        var pinItem = new MenuFlyoutItem { Text = L10n.T(isPinned ? "取消固定到侧栏" : "固定到侧栏") };
                        pinItem.Click += (_, _) =>
                        {
                            if (isPinned) _library.Prefs.PinnedCollectionIds.Remove(activeCol.Id);
                            else if (!_library.Prefs.PinnedCollectionIds.Contains(activeCol.Id)) _library.Prefs.PinnedCollectionIds.Add(activeCol.Id);
                            _library.SavePrefs();
                            RefreshBooks();
                        };
                        var renItem = new MenuFlyoutItem { Text = L10n.T("重命名…") };
                        renItem.Click += async (_, _) => await RenameCollectionAsync(activeCol);
                        var delItem = new MenuFlyoutItem { Text = L10n.T("删除合集…"), Foreground = ThemeHelper.Brush(p.Danger) };
                        delItem.Click += async (_, _) => await ConfirmDeleteCollectionAsync(activeCol);
                        colMenu.Items.Add(pinItem);
                        colMenu.Items.Add(new MenuFlyoutSeparator());
                        colMenu.Items.Add(renItem);
                        colMenu.Items.Add(BuildCollectionIconSubMenu(activeCol));
                        colMenu.Items.Add(new MenuFlyoutSeparator());
                        colMenu.Items.Add(delItem);

                        var manageBtn = new Button
                        {
                            Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                            Content = new TextBlock { Text = "···", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
                            Flyout = colMenu,
                        };
                        ToolTipService.SetToolTip(manageBtn, "管理合集");
                        AutomationProperties.SetName(manageBtn, L10n.T("管理合集"));
                        HeaderRightHost.Children.Add(manageBtn);
                    }
                }
                ImportPlusIcon.Content = YYIcons.Make("plus", 11, t1);
                HeaderRightHost.Children.Add(ImportBtn);
                break;
        }
    }

    private void ReadingExpand_Click(object sender, RoutedEventArgs e)
    {
        _readingExpanded = !_readingExpanded;
        RenderShelfMain();
    }

    // ---------- 1. 书架主视图 ----------

    private void RenderShelfMain()
    {
        IEnumerable<BookSummary> source = _shelfBooks;
        if (_activeFilter == "audio") source = source.Where(b => b.Format == BookFormat.Audio || _pairedBookIds.Contains(b.Id));
        else if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
        {
            var colId = _activeFilter[4..];
            source = source.Where(b => b.Collections.Contains(colId));
        }

        var sorted = SortBooks(source).ToArray();
        bool showBand = _activeFilter is "all" or "audio";
        // 1:1 对齐 MacShelfView.swift line 664：在读横带始终按最近阅读顺序，不受下方网格按书名/作者排序影响
        var allReading = showBand
            ? source.Where(b => b.Status == ReadingStatus.Reading)
                .OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt))
                .ToArray()
            : [];

        double sheetW = PaperSheet.ActualWidth > 0 ? PaperSheet.ActualWidth : 800;
        double bandAvailW = Math.Max(280, sheetW - 50);
        int readingCap = Math.Max(1, (int)((bandAvailW + 12) / (266 + 12)));
        if (allReading.Length <= readingCap) _readingExpanded = false;

        var reading = _readingExpanded ? allReading : allReading.Take(readingCap).ToArray();
        ReadingCardsHost.Children.Clear();
        ReadingCardsHost.RowDefinitions.Clear();
        ReadingCardsHost.ColumnDefinitions.Clear();

        if (reading.Length > 0)
        {
            ReadingBand.Visibility = Visibility.Visible;
            if (allReading.Length > readingCap)
            {
                ReadingBandCount.Visibility = Visibility.Collapsed;
                ReadingExpandBtn.Visibility = Visibility.Visible;
                bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
                var expSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
                expSp.Children.Add(new TextBlock { Text = _readingExpanded ? "收起" : $"全部 {allReading.Length} 本", FontSize = 11.5 });
                expSp.Children.Add(YYIcons.Make(_readingExpanded ? "chevron-up" : "chevron-down", 9.5, ThemeHelper.Brush(ThemeHelper.Palette(dark).T2)));
                ReadingExpandBtn.Content = expSp;
            }
            else
            {
                ReadingExpandBtn.Visibility = Visibility.Collapsed;
                ReadingBandCount.Visibility = Visibility.Visible;
                ReadingBandCount.Text = $"{allReading.Length} 本";
            }

            if (reading.Length == 1)
            {
                var single = CreateReadingCard(reading[0], tinted: false);
                if (single is FrameworkElement fe)
                {
                    fe.MaxWidth = 316;
                    fe.HorizontalAlignment = HorizontalAlignment.Left;
                }
                ReadingCardsHost.Children.Add(single);
            }
            else
            {
                int cols = Math.Max(1, Math.Min(readingCap, reading.Length));
                int rows = (reading.Length + cols - 1) / cols;
                ReadingCardsHost.ColumnSpacing = 12;
                ReadingCardsHost.RowSpacing = 12;
                for (int c = 0; c < cols; c++)
                    ReadingCardsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                for (int r = 0; r < rows; r++)
                    ReadingCardsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                for (int i = 0; i < reading.Length; i++)
                {
                    var cardEl = CreateReadingCard(reading[i], tinted: false);
                    Grid.SetRow((FrameworkElement)cardEl, i / cols);
                    Grid.SetColumn((FrameworkElement)cardEl, i % cols);
                    ReadingCardsHost.Children.Add(cardEl);
                }
            }
        }
        else
        {
            ReadingBand.Visibility = Visibility.Collapsed;
        }

        var cardIds = reading.Select(b => b.Id).ToHashSet();
        var rest = sorted.Where(b => !cardIds.Contains(b.Id)).ToArray();

        GridSection.Visibility = (rest.Length > 0 || cardIds.Count == 0) ? Visibility.Visible : Visibility.Collapsed;
        GridCountText.Text = cardIds.Count == 0 ? $"{rest.Length} 本" : $"其余 {rest.Length} 本";

        if (rest.Length == 0 && cardIds.Count == 0)
        {
            bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
            var pal = ThemeHelper.Palette(dark);
            GridHeaderRow.Visibility = Visibility.Collapsed;
            BooksRepeater.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            if (_activeFilter == "audio")
            {
                EmptyIconHost.Content = YYIcons.Make("waveform", 28, ThemeHelper.Brush(pal.T3));
                EmptyTitleText.Text = "还没有有声书";
                EmptyTitleText.FontSize = 13.5;
                EmptyTitleText.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
                EmptyTitleText.Foreground = ThemeHelper.Brush(pal.T1);
                EmptySubtitleText.Text = "把 M4B、MP3、M4A 拖进窗口，或导入音频与书架上的电子书合成听读同步。";
                EmptySubtitleText.Visibility = Visibility.Visible;
                EmptyActionBtn.Content = "＋ 导入有声书";
                EmptyActionBtn.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyIconHost.Content = YYIcons.Make(_activeFilter.StartsWith("col:", StringComparison.Ordinal) ? "stack" : "books.vertical", 28, ThemeHelper.Brush(pal.T3));
                EmptyTitleText.Text = _activeFilter.StartsWith("col:", StringComparison.Ordinal)
                    ? "还没有书。把文件拖进来，或在书的右键菜单里加入这个合集"
                    : "书架是空的，把 EPUB、PDF、MOBI、TXT 拖进窗口就能开始读";
                EmptyTitleText.FontSize = 12.5;
                EmptyTitleText.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
                EmptyTitleText.Foreground = ThemeHelper.Brush(pal.T2);
                EmptySubtitleText.Visibility = Visibility.Collapsed;
                EmptyActionBtn.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            GridHeaderRow.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            BooksRepeater.ItemsSource = rest.Select(CreateBookCell).ToArray();
        }
        L10n.ApplyTree(ShelfContentStack);
    }

    private IEnumerable<BookSummary> SortBooks(IEnumerable<BookSummary> books) => _sortKey switch
    {
        "added" => books.OrderByDescending(b => b.AddedAt),
        "title" => books.OrderBy(b => b.Title, StringComparer.CurrentCultureIgnoreCase),
        "author" => books.OrderBy(b => b.Authors.FirstOrDefault() ?? "", StringComparer.CurrentCultureIgnoreCase),
        _ => books.OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt)),
    };

    private static string? FormatRemainingTail(BookSummary book)
    {
        if (book.Format == BookFormat.Pdf && book.Page > 0) return $"第 {book.Page} 页";
        uint totalMin = book.Format == BookFormat.Audio ? book.ListenMinutes : book.ReadMinutes;
        if (totalMin == 0 || book.Progress >= 0.995) return null;
        int rem = Math.Max(1, (int)Math.Round(totalMin * (1.0 - Math.Clamp(book.Progress, 0, 1))));
        return rem >= 60
            ? (rem % 60 == 0 ? $"还剩 {rem / 60} 小时" : $"还剩 {rem / 60} 小时 {rem % 60} 分")
            : $"还剩 {rem} 分";
    }

    private UIElement CreateReadingCard(BookSummary book, bool tinted)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var bgBrush = tinted ? ThemeHelper.Brush(CoverColorHelper.FallbackLead(dark)) : ThemeHelper.Brush(palette.Card);
        var borderBrush = tinted
            ? ThemeHelper.Brush(Color.FromArgb(36, 255, 255, 255))
            : ThemeHelper.Brush(dark ? Color.FromArgb(16, 255, 255, 255) : Color.FromArgb(16, 42, 43, 46));
        var titleBrush = tinted ? ThemeHelper.Brush(Colors.White) : ThemeHelper.Brush(palette.T1);
        var authorBrush = tinted ? ThemeHelper.Brush(Color.FromArgb(198, 255, 255, 255)) : ThemeHelper.Brush(palette.T2);
        var metaBrush = tinted ? ThemeHelper.Brush(Color.FromArgb(186, 255, 255, 255)) : ThemeHelper.Brush(palette.T3);
        var pctBrush = tinted ? ThemeHelper.Brush(Colors.White) : ThemeHelper.Brush(tint.Accent);
        var iconBrush = tinted ? ThemeHelper.Brush(Color.FromArgb(215, 255, 255, 255)) : ThemeHelper.Brush(palette.T3);

        var card = new Border
        {
            Height = 88,
            CornerRadius = new CornerRadius(14),
            Background = bgBrush,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(0.5),
        };
        ThemeHelper.ApplyElevation(card, PaperInnerGrid, 10f);

        // 首卡异步提取封面主色染色（移植自 Mac CoverHue.load）
        if (tinted && !string.IsNullOrEmpty(book.CoverPath))
        {
            _ = TintLeadCardAsync(card, book.CoverPath, dark);
        }

        var outer = new Grid();
        var row = new Grid { Padding = new Thickness(11, 11, 8, 11), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(CreateCoverView(book, 46, 64, elevated: false));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2.5 };
        info.Children.Add(L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = titleBrush,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        }));

        var author = book.Authors.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(author))
        {
            info.Children.Add(L10n.Skip(new TextBlock { Text = author, FontSize = 11.5, Foreground = authorBrush, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
        }

        var pct = (int)Math.Round(book.Progress * 100);
        bool isPlayingThis = IsBookCurrentlyPlaying(book);
        bool hasPairedOrAudio = book.Format == BookFormat.Audio || _pairedBookIds.Contains(book.Id);
        var metaLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 1, 0, 0) };
        if (isPlayingThis)
        {
            metaLine.Children.Add(new TextBlock { Text = "正在听 ·", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
        }
        else
        {
            metaLine.Children.Add(new TextBlock { Text = hasPairedOrAudio ? "有声 ·" : "图书 ·", FontSize = 11, Foreground = metaBrush });
        }
        metaLine.Children.Add(new TextBlock { Text = pct > 0 ? $"{pct}%" : "刚开始", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = pctBrush });
        if (FormatRemainingTail(book) is string tail)
        {
            metaLine.Children.Add(new TextBlock { Text = $"· {tail}", FontSize = 11, Foreground = metaBrush });
        }
        info.Children.Add(metaLine);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var listenBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Background = isPlayingThis ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
            CornerRadius = new CornerRadius(8),
            Content = YYIcons.Make(
                isPlayingThis ? "waveform" : "headphones",
                13.5,
                isPlayingThis ? ThemeHelper.Brush(tint.Accent) : iconBrush),
        };
        ToolTipService.SetToolTip(listenBtn, isPlayingThis ? "正在听" : "听");
        AutomationProperties.SetName(listenBtn, L10n.T(isPlayingThis ? "正在听" : "听"));
        listenBtn.Click += (_, _) => TryOpenBook(book, autoListen: true);

        var moreBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Foreground = iconBrush,
            Content = new TextBlock { Text = "···", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
            Flyout = BuildBookMenuFlyout(book),
        };
        ToolTipService.SetToolTip(moreBtn, "更多");
        AutomationProperties.SetName(moreBtn, L10n.T("更多"));

        actions.Children.Add(listenBtn);
        actions.Children.Add(moreBtn);
        Grid.SetColumn(actions, 2);
        row.Children.Add(actions);

        var hitBtn = new Button
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            UseSystemFocusVisuals = false,
            ContextFlyout = BuildBookMenuFlyout(book),
        };
        AutomationProperties.SetName(hitBtn, $"{book.Title}{(book.Authors.Length > 0 ? $"，{book.Authors[0]}" : "")}，{(pct > 0 ? $"{pct}%" : L10n.T("刚开始"))}");
        hitBtn.Click += (_, _) => TryOpenBook(book);

        outer.Children.Add(hitBtn);
        if (tinted)
        {
            // 对齐 MacShelfView.swift lines 1601-1604：染色首卡左上角微光渐变
            outer.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                IsHitTestVisible = false,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0, 0),
                    EndPoint = new Windows.Foundation.Point(0.55, 0.75),
                    GradientStops =
                    {
                        new GradientStop { Color = Color.FromArgb(34, 255, 255, 255), Offset = 0 },
                        new GradientStop { Color = Color.FromArgb(0, 255, 255, 255), Offset = 1 },
                    },
                },
            });
        }
        outer.Children.Add(row);

        double clampedProg = Math.Clamp(book.Progress, 0.04, 1.0);
        var trackWrap = new Grid { Height = 2.5, Margin = new Thickness(14, 0, 14, 1), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
        trackWrap.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1.25),
            Background = ThemeHelper.Brush(tinted ? Color.FromArgb(44, 255, 255, 255) : (dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(16, 42, 43, 46))),
        });
        var fillGrid = new Grid();
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clampedProg, GridUnitType.Star) });
        fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1.0 - clampedProg), GridUnitType.Star) });
        fillGrid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(1.25),
            Background = ThemeHelper.Brush(tinted ? Color.FromArgb(210, 255, 255, 255) : tint.Fill),
        });
        trackWrap.Children.Add(fillGrid);
        outer.Children.Add(trackWrap);

        card.Child = outer;
        return card;
    }

    private async Task TintLeadCardAsync(Border card, string coverPath, bool dark)
    {
        var color = await CoverColorHelper.LoadLeadCardColorAsync(coverPath, dark);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_closed) return;
            card.Background = ThemeHelper.Brush(color);
        });
    }

    private UIElement CreateBookCell(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        double w = Math.Clamp(_library.Prefs.CoverWidth, 76, 148);
        double h = Math.Round(w * 1.5);

        var root = new StackPanel { Width = w, Spacing = 0 };

        var cover = CreateCoverView(book, w, h, elevated: true);
        bool isHighlighted = _highlightedBookId == book.Id;
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var coverBtn = new Button
        {
            Padding = new Thickness(isHighlighted ? 2 : 0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = isHighlighted ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(isHighlighted ? 2.5 : 0),
            CornerRadius = new CornerRadius(isHighlighted ? 7 : 4),
            UseSystemFocusVisuals = false,
            CanDrag = true,
            Content = cover,
            ContextFlyout = BuildBookMenuFlyout(book),
        };
        AutomationProperties.SetName(coverBtn, $"{book.Title}{(book.Authors.Length > 0 ? $"，{book.Authors[0]}" : "")}，{L10n.T(FormatShelfNote(book))}");
        if (!_library.Prefs.GlobalEinkMode)
        {
            coverBtn.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
            coverBtn.PointerEntered += (_, _) => coverBtn.Translation = new Vector3(0, -2f, 0);
            coverBtn.PointerExited += (_, _) => coverBtn.Translation = Vector3.Zero;
        }
        if (isHighlighted)
        {
            coverBtn.Loaded += (_, _) => { try { coverBtn.StartBringIntoView(); } catch { } };
        }
        string dragBookId = book.Id;
        coverBtn.DragStarting += (_, e) =>
        {
            e.Data.Properties["yuyue-book-id"] = dragBookId;
            e.Data.SetText("yuyue-book:" + dragBookId);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };
        coverBtn.Click += (_, _) => TryOpenBook(book);
        root.Children.Add(coverBtn);

        root.Children.Add(L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 9, 0, 2),
        }));

        if (_library.Prefs.ShowProgress)
        {
            if (IsBookCurrentlyPlaying(book))
            {
                int pct = (int)Math.Round(book.Progress * 100);
                var listenRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                listenRow.Children.Add(YYIcons.Make("headphones", 10, ThemeHelper.Brush(tint.Accent)));
                listenRow.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"{L10n.T("正在听")} · {pct}%",
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
                root.Children.Add(listenRow);
            }
            else
            {
                root.Children.Add(new TextBlock
                {
                    Text = FormatShelfNote(book),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(palette.T3),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
        }

        return root;
    }

    /// <summary>
    /// 1:1 <c>MacShelfView.swift open(_:_:)</c> (commit <c>e42fcb6</c>):
    /// surfaces a clear cue when a synced book's file lives on another device instead of failing silently,
    /// and routes paper books to their inline record view.
    /// </summary>
    public void TryOpenBook(string bookId, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        var found = _keptBooks.FirstOrDefault(b => b.Id == bookId)
            ?? _shelfBooks.FirstOrDefault(b => b.Id == bookId);
        if (found is not null)
        {
            TryOpenBook(found, autoListen, section, sentence, askQuestion, searchQuery);
            return;
        }
        try
        {
            var det = _library.GetBookDetail(bookId);
            TryOpenBook(det.Summary, autoListen, section, sentence, askQuestion, searchQuery);
        }
        catch
        {
            ShowToast("这本书在当前设备上不可用", canUndo: false, detail: "把同一个文件拖进来，进度和笔记就接上了", iconKind: "cloud");
        }
    }

    public void TryOpenBook(BookSummary book, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        if (book.Record)
        {
            _recordBuilder.OpenBookId = book.Id;
            SwitchTab("progress");
            return;
        }
        if (!book.Available)
        {
            ShowToast($"《{book.Title}》在别的设备上", canUndo: false, detail: "把同一个文件拖进来，进度和笔记就接上了", iconKind: "cloud");
            return;
        }
        App.CurrentApp.OpenBook(book.Id, autoListen: autoListen, section: section, sentence: sentence, askQuestion: askQuestion, searchQuery: searchQuery);
    }

    private static readonly (Color Light, Color Dark)[] PaperBookClothTones =
    [
        (Color.FromArgb(255, 233, 227, 215), Color.FromArgb(255, 50, 45, 39)), // warm linen
        (Color.FromArgb(255, 221, 229, 225), Color.FromArgb(255, 39, 48, 45)), // muted sage
        (Color.FromArgb(255, 222, 227, 236), Color.FromArgb(255, 40, 45, 54)), // slate mist
        (Color.FromArgb(255, 235, 224, 218), Color.FromArgb(255, 54, 43, 39)), // dusty terracotta
        (Color.FromArgb(255, 229, 225, 235), Color.FromArgb(255, 47, 43, 53)), // soft wisteria
        (Color.FromArgb(255, 235, 230, 214), Color.FromArgb(255, 52, 48, 37)), // pale ochre
    ];

    internal static Color PaperClothColor(string title, bool dark)
    {
        ulong hash = 5381;
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(title ?? ""))
            hash = ((hash << 5) + hash) + b;
        var pair = PaperBookClothTones[(int)(hash % (ulong)PaperBookClothTones.Length)];
        return dark ? pair.Dark : pair.Light;
    }

    public Border CreateCoverView(BookSummary book, double width, double height, bool elevated)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        bool isAudio = book.Format == BookFormat.Audio;
        bool isPaper = book.Record;
        double actualH = isAudio ? width : height;

        Color placeholderBg = isAudio
            ? palette.Inset
            : (isPaper
                ? PaperClothColor(book.Title, dark)
                : (dark ? Color.FromArgb(255, 44, 40, 36) : Color.FromArgb(255, 235, 230, 221)));

        var inner = new Border
        {
            Width = width,
            Height = actualH,
            VerticalAlignment = isAudio ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(isAudio ? Math.Max(5, width * 0.08) : 5),
            BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(14, 42, 43, 46)),
            BorderThickness = new Thickness(0.5),
            Background = ThemeHelper.Brush(placeholderBg),
            Opacity = book.Available || isPaper ? 1.0 : 0.55,
        };
        if (elevated && !_library.Prefs.GlobalEinkMode) ThemeHelper.ApplyElevation(inner, PaperInnerGrid, 12f);

        var layer = new Grid();

        if (!string.IsNullOrEmpty(book.CoverPath) && File.Exists(book.CoverPath))
        {
            var bmp = new BitmapImage { DecodePixelWidth = Math.Max(48, (int)Math.Ceiling(width * 2)) };
            bmp.UriSource = new Uri(book.CoverPath);
            layer.Children.Add(new Image { Source = bmp, Stretch = Stretch.UniformToFill });
        }
        else if (isAudio)
        {
            layer.Children.Add(new ContentControl
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Content = YYIcons.Make("headphones", Math.Max(14, width * 0.34), ThemeHelper.Brush(palette.T3)),
            });
        }
        else
        {
            double unit = width / 100.0;
            if (isPaper)
            {
                // Subtle physical book-spine crease along the left edge (1:1 BookCover.swift:89-165)
                layer.Children.Add(new Border
                {
                    Width = Math.Max(3, 8 * unit),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    IsHitTestVisible = false,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0, 0.5),
                        EndPoint = new Windows.Foundation.Point(1, 0.5),
                        GradientStops =
                        {
                            new GradientStop { Color = Color.FromArgb(20, 0, 0, 0), Offset = 0.0 },
                            new GradientStop { Color = Color.FromArgb(56, 255, 255, 255), Offset = 0.35 },
                            new GradientStop { Color = Color.FromArgb(14, 0, 0, 0), Offset = 0.68 },
                            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 1.0 },
                        },
                    },
                });
            }
            var face = L10n.Skip(new Grid { Padding = new Thickness((isPaper ? 14 : 12) * unit, 11 * unit, 10 * unit, 10 * unit) });
            face.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            face.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var topStack = new StackPanel { Spacing = Math.Max(2, 4.5 * unit), VerticalAlignment = VerticalAlignment.Top };
            topStack.Children.Add(new TextBlock
            {
                Text = book.Title,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = Math.Max(8.5, 14 * unit),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = width < 48 ? 2 : 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (book.Authors.Length > 0 && width >= 40)
            {
                topStack.Children.Add(new TextBlock
                {
                    Text = book.Authors[0],
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = Math.Max(7.5, 9.5 * unit),
                    Foreground = ThemeHelper.Brush(palette.T2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            face.Children.Add(topStack);

            if (book.Format == BookFormat.Pdf)
            {
                var pdfPill = new Border
                {
                    CornerRadius = new CornerRadius(Math.Max(2, 3 * unit)),
                    Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)28 : (byte)16, palette.T1.R, palette.T1.G, palette.T1.B)),
                    Padding = new Thickness(Math.Max(3, 4.5 * unit), Math.Max(0.8, 1.4 * unit), Math.Max(3, 4.5 * unit), Math.Max(0.8, 1.4 * unit)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Child = new TextBlock
                    {
                        Text = "PDF",
                        FontSize = Math.Max(6.5, 8 * unit),
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(palette.T2),
                    },
                };
                Grid.SetRow(pdfPill, 1);
                face.Children.Add(pdfPill);
            }
            layer.Children.Add(face);
        }

        // 右下角有声徽标 / 异地云端徽标（对齐 MacShelfView.swift:1040-1051，不可用书籍在任意封面尺寸均显示 icloud.and.arrow.down）
        if (!book.Available && !book.Record)
        {
            double badgeSize = width < 30 ? 12 : (width < 56 ? 15 : 19);
            double iconSz = width < 30 ? 7.5 : (width < 56 ? 8.5 : 10.5);
            double margin = width < 30 ? 2 : (width < 56 ? 3 : 5);
            layer.Children.Add(new Border
            {
                Width = badgeSize,
                Height = badgeSize,
                CornerRadius = new CornerRadius(badgeSize / 2),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, margin, margin),
                Child = YYIcons.Make("icloud.and.arrow.down", iconSz, ThemeHelper.Brush(palette.T2)),
            });
        }
        else if (width >= 60 && (isAudio || _pairedBookIds.Contains(book.Id)))
        {
            layer.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(Color.FromArgb(184, 16, 18, 20)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                Child = YYIcons.Make("headphones", 9, new SolidColorBrush(Colors.White)),
            });
        }

        inner.Child = layer;
        if (!isAudio) return inner;

        return new Border
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Colors.Transparent),
            Child = inner,
        };
    }

    /// <summary>
    /// 1:1 移植自 <c>BookMenu.swift</c>：214px 浮卡，顶部书名/作者/剩余时长细条，5 种阅读状态（右侧图标与对勾），接着读/从头听，编辑信息/加入合集/显示原文件/删除。
    /// </summary>
    public Flyout BuildBookMenuFlyout(BookSummary book)
    {
        var flyout = new Flyout
        {
            FlyoutPresenterStyle = (Style)Application.Current.Resources["YYFloatFlyoutPresenterStyle"],
            ShouldConstrainToRootBounds = true,
        };
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var root = new StackPanel { Width = 214, Spacing = 0, Padding = new Thickness(0, 0, 0, 4) };

        // Header（1:1 BookMenu.swift:147-184：书名与作者同一基线，进度条与剩余时间同一行）
        var head = new StackPanel { Spacing = 6, Padding = new Thickness(14, 11, 14, 10) };
        var titleRow = new Grid { ColumnSpacing = 6 };
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.Children.Add(L10n.Skip(new TextBlock
        {
            Text = book.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = book.Authors.Length > 0 ? 138 : 186,
        }));
        if (book.Authors.Length > 0)
        {
            var authTb = L10n.Skip(new TextBlock
            {
                Text = book.Authors[0],
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom,
            });
            Grid.SetColumn(authTb, 1);
            titleRow.Children.Add(authTb);
        }
        head.Children.Add(titleRow);

        if (book.Record)
        {
            var paperRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            paperRow.Children.Add(BookRecordPageBuilder.MakePaperBookTag(dark));
            string stLbl = book.Status switch
            {
                ReadingStatus.Reading => "在读",
                ReadingStatus.Want => "想读",
                ReadingStatus.Finished => "读过",
                ReadingStatus.Paused => "搁置",
                ReadingStatus.Dropped => "弃读",
                _ => "在读",
            };
            paperRow.Children.Add(new TextBlock { Text = stLbl, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T2), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(paperRow);
        }
        else if (!book.Available)
        {
            head.Children.Add(new TextBlock
            {
                Text = "在别的设备上 · 拖入同文件可接上进度",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else if (book.Progress > 0.002 && book.Status != ReadingStatus.Finished)
        {
            string remLine = FormatRemainingTail(book) ?? $"{Math.Max(1, (int)Math.Round(book.Progress * 100))}%";
            double cl = Math.Clamp(book.Progress, 0.04, 1.0);
            var progRow = new Grid { ColumnSpacing = 8 };
            progRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            progRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var progTrack = new Grid { Height = 2.5, VerticalAlignment = VerticalAlignment.Center };
            progTrack.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(palette.Line) });
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cl, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1.0 - cl), GridUnitType.Star) });
            fg.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Fill) });
            progTrack.Children.Add(fg);
            progRow.Children.Add(progTrack);

            var remTb = new TextBlock { Text = remLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T2), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(remTb, 1);
            progRow.Children.Add(remTb);
            head.Children.Add(progRow);
        }
        else
        {
            head.Children.Add(new TextBlock
            {
                Text = book.Status == ReadingStatus.Finished ? "读完了" : "还没读",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T2),
            });
        }
        root.Children.Add(head);

        Border Hairline() => new() { Height = 0.5, Background = ThemeHelper.Brush(palette.Line), Margin = new Thickness(14, 3, 14, 3) };

        Button MenuRow(string label, string rightIcon, Action onClick, bool accent = false, bool danger = false)
        {
            var fg = danger ? ThemeHelper.Brush(palette.Danger)
                : accent ? ThemeHelper.Brush(tint.Accent)
                : ThemeHelper.Brush(palette.T1);
            var iconBrush = danger ? ThemeHelper.Brush(palette.Danger)
                : accent ? ThemeHelper.Brush(tint.Accent)
                : ThemeHelper.Brush(palette.T1);

            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = accent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var ic = YYIcons.Make(rightIcon, 13, iconBrush);
            Grid.SetColumn(ic, 1);
            g.Children.Add(ic);

            var b = new Button
            {
                Height = 28,
                MinHeight = 28,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(5, 0, 5, 0),
                Padding = new Thickness(9, 0, 9, 0),
                UseSystemFocusVisuals = false,
                Content = g,
            };
            b.PointerEntered += (_, _) => b.Background = ThemeHelper.Brush(palette.Inset);
            b.PointerExited += (_, _) => b.Background = new SolidColorBrush(Colors.Transparent);
            b.Click += (_, _) => { flyout.Hide(); onClick(); };
            return b;
        }

        // Section 1：5 种阅读状态
        root.Children.Add(Hairline());
        var stSection = new StackPanel { Spacing = 1 };
        foreach (var (label, status, icon) in new[]
        {
            ("在读", ReadingStatus.Reading, "book"),
            ("想读", ReadingStatus.Want, "bookmark"),
            ("读过", ReadingStatus.Finished, "check-circle"),
            ("搁置", ReadingStatus.Paused, "pause-circle"),
            ("弃读", ReadingStatus.Dropped, "xmark-circle"),
        })
        {
            var st = status;
            bool active = book.Status == st;
            stSection.Children.Add(MenuRow(label, active ? "checkmark" : icon, () =>
            {
                _library.SetStatus(book.Id, st);
                RefreshBooks();
            }, accent: active));
        }
        root.Children.Add(stSection);

        // Section 2：打开 / 朗读 / 已配对有声书（1:1 BookMenu.swift:200-217）
        if (!book.Record && book.Available)
        {
            root.Children.Add(Hairline());
            var openSec = new StackPanel { Spacing = 1 };
            bool isAudio = book.Format == BookFormat.Audio;
            bool playingNow = IsBookCurrentlyPlaying(book);
            bool hasPairedAudiobook = _listeningPairs.Any(lp => lp.Book == book.Id);
            string openLbl = playingNow
                ? "正在听 · 切换过去"
                : isAudio
                    ? (book.Progress > 0.005 ? "接着听" : "开始听")
                    : (book.Progress > 0.005 ? "接着读" : "开始读");
            openSec.Children.Add(MenuRow(openLbl, playingNow ? "waveform" : (isAudio ? "headphones" : "book"), () => TryOpenBook(book), accent: true));
            openSec.Children.Add(MenuRow("从头听", "headphones", () => TryOpenBook(book, autoListen: true, section: 0, sentence: 0)));
            if (!isAudio && hasPairedAudiobook)
                openSec.Children.Add(MenuRow("有声书…", "waveform", () => _ = ShowPairAudiobookDialogAsync(book)));
            root.Children.Add(openSec);
        }

        // Section 3：编辑信息 / 加入合集 / 显示原文件 / 删除
        root.Children.Add(Hairline());
        var mgmtSec = new StackPanel { Spacing = 1 };
        mgmtSec.Children.Add(MenuRow("编辑信息…", "pencil", () => _ = ShowEditBookDialogAsync(book)));
        mgmtSec.Children.Add(MenuRow("加入合集…", "stack", () => _ = ShowPickCollectionDialogAsync(book)));
        if (!book.Record)
            mgmtSec.Children.Add(MenuRow("在资源管理器中显示", "folder", () => RevealOriginalInExplorer(book)));
        mgmtSec.Children.Add(MenuRow("删除", "trash", () => _ = HandleDeleteBookAsync(book), danger: true));
        root.Children.Add(mgmtSec);

        L10n.ApplyTree(root);
        flyout.Content = root;
        return flyout;
    }

    private async Task ShowPairAudiobookDialogAsync(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var curPair = _listeningPairs.FirstOrDefault(lp => lp.Book == book.Id);
        var audioBooks = _shelfBooks.Where(b => b.Format == BookFormat.Audio).ToArray();
        var listPanel = new StackPanel { Spacing = 8 };
        ContentDialog? dlgRef = null;
        bool wantImportAudio = false;

        if (curPair is not null)
        {
            var rec = _shelfBooks.FirstOrDefault(b => b.Id == curPair.Recording);
            var curCard = new StackPanel { Spacing = 8 };
            curCard.Children.Add(new TextBlock
            {
                Text = $"当前已配对：{rec?.Title ?? "有声书"}",
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tint.Accent),
                TextWrapping = TextWrapping.Wrap,
            });
            var rowActs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (rec is not null)
            {
                rowActs.Children.Add(YYChrome.MakeSheetPillButton("打开有声书", () => { dlgRef?.Hide(); TryOpenBook(rec); }, dark, _library.Prefs.Tint, "warm", height: 30, horizontalPadding: 14));
            }
            rowActs.Children.Add(YYChrome.MakeSheetPillButton("取消配对", () =>
            {
                try { _library.Core.AudiobookPair(curPair.Recording, null); } catch { }
                dlgRef?.Hide();
                RefreshBooks();
                ShowToast("已取消有声书配对", canUndo: false);
            }, dark, _library.Prefs.Tint, "secondary", height: 30, horizontalPadding: 14));
            curCard.Children.Add(rowActs);
            listPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(tint.Soft),
                BorderBrush = ThemeHelper.Brush(tint.Edge),
                BorderThickness = new Thickness(0.5),
                Padding = new Thickness(12, 10, 12, 10),
                Child = curCard,
            });
        }

        if (audioBooks.Length > 0)
        {
            listPanel.Children.Add(new TextBlock { Text = "选择书架上的有声书配对", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(2, 4, 0, 0) });
            var grp = new StackPanel { Spacing = 0 };
            for (int i = 0; i < audioBooks.Length; i++)
            {
                if (i > 0) grp.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(12, 0, 12, 0) });
                var recItem = audioBooks[i];
                bool isCur = curPair?.Recording == recItem.Id;
                var rowGrid = new Grid { ColumnSpacing = 8 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.Children.Add(new TextBlock
                {
                    Text = recItem.Title,
                    FontSize = 12.5,
                    FontWeight = isCur ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = ThemeHelper.Brush(p.T1),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (isCur)
                {
                    var chk = YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent));
                    Grid.SetColumn(chk, 1);
                    rowGrid.Children.Add(chk);
                }
                var rowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(12, 9, 12, 9),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = rowGrid,
                };
                rowBtn.Click += (_, _) =>
                {
                    try { _library.Core.AudiobookPair(recItem.Id, book.Id); } catch { }
                    dlgRef?.Hide();
                    RefreshBooks();
                    ShowToast($"已将《{book.Title}》与《{recItem.Title}》配对", canUndo: false);
                };
                grp.Children.Add(rowBtn);
            }
            listPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = grp,
            });
        }
        else
        {
            listPanel.Children.Add(new TextBlock
            {
                Text = "书架上还没有有声书，可以先导入 .m4b / .mp3 文件或文件夹后再配对。",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        var foot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foot.Children.Add(YYChrome.MakeSheetPillButton("完成", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 34, horizontalPadding: 16));
        foot.Children.Add(YYChrome.MakeSheetPillButton("导入有声书…", () => { wantImportAudio = true; dlgRef?.Hide(); }, dark, _library.Prefs.Tint, "dark", height: 34, horizontalPadding: 16));

        var sheetBody = new StackPanel
        {
            Spacing = 14,
            Padding = new Thickness(22, 18, 22, 18),
            Children =
            {
                YYChrome.MakeSheetHeader("有声书", book.Title, () => dlgRef?.Hide(), dark),
                new ScrollViewer { MaxHeight = 320, Content = listPanel },
                foot,
            },
        };

        if (RootGrid.XamlRoot is null) return;
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 400, sheetBody, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        if (wantImportAudio)
        {
            var audioExts = AudiobookImporter.Extensions.Concat([".zip"]).ToArray();
            var files = await NativePickerHelper.PickMultipleFilesAsync(this, audioExts, PickerLocationId.MusicLibrary);
            if (files.Count > 0)
            {
                try
                {
                    var imported = await AudiobookImporter.ShowImportSheetAsync(this, RootGrid.XamlRoot, _library, files.ToArray());
                    if (imported is not null)
                    {
                        _library.Analytics.TrackBookImported(imported.Format.ToString(), "file_picker", files.FirstOrDefault());
                        try { _library.Core.AudiobookPair(imported.Id, book.Id); } catch { }
                        RefreshBooks();
                        ShowToast($"已导入并配对《{imported.Title}》", canUndo: false);
                    }
                }
                catch (Exception ex) { ShowToast($"导入失败：{ex.Message}", canUndo: false, isDelete: true); }
            }
        }
    }

    private async Task HandleDeleteBookAsync(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        bool hasRecordData = (_markCounts.TryGetValue(book.Id, out var mc) && mc > 0)
            || book.Rating is > 0
            || !string.IsNullOrWhiteSpace(book.Comment)
            || (book.Tags is { Length: > 0 })
            || book.Status is ReadingStatus.Finished or ReadingStatus.Paused or ReadingStatus.Dropped;

        if (book.Record && hasRecordData)
        {
            bool confirmed = await YYChrome.ShowConfirmAsync(
                RootGrid.XamlRoot,
                dark,
                $"删除《{book.Title}》的阅读记录？",
                "删除后评分、短评、标签和划线记录都会被移除，可以在提示条里撤销。",
                confirmText: "删除记录",
                cancelText: "取消",
                destructive: true,
                tint: _library.Prefs.Tint);
            if (!confirmed) return;
        }
        else if (!book.Record && hasRecordData && RootGrid.XamlRoot is not null)
        {
            ContentDialog? delDlg = null;
            int choice = 0; // 0 = cancel, 1 = keep record, 2 = delete all
            var body = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 18) };
            body.Children.Add(YYChrome.MakeSheetHeader($"删除《{book.Title}》？", null, () => delDlg?.Hide(), dark, titleSize: 16));
            body.Children.Add(new TextBlock
            {
                Text = L10n.T("这本书里有你的阅读记录或笔记，可以只删书文件、在「进度」里留下记录。"),
                FontSize = 12.5,
                LineHeight = 19,
                Foreground = ThemeHelper.Brush(p.T2),
                TextWrapping = TextWrapping.Wrap,
            });
            var btnStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
            btnStack.Children.Add(YYChrome.MakeSheetPillButton("只删文件，留下记录", () => { choice = 1; delDlg?.Hide(); }, dark, _library.Prefs.Tint, "dark", height: 38, stretch: true));
            var delAllBtn = YYChrome.MakeSheetPillButton("连记录一起删", () => { choice = 2; delDlg?.Hide(); }, dark, _library.Prefs.Tint, "secondary", height: 36, stretch: true);
            delAllBtn.Foreground = ThemeHelper.Brush(p.Danger);
            btnStack.Children.Add(delAllBtn);
            btnStack.Children.Add(YYChrome.MakeSheetPillButton("取消", () => delDlg?.Hide(), dark, _library.Prefs.Tint, "secondary", height: 36, stretch: true));
            body.Children.Add(btnStack);

            delDlg = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 360, body, tint: _library.Prefs.Tint);
            await delDlg.ShowSafeAsync(_library.Prefs.Tint);
            if (choice == 0) return;
            if (choice == 1)
            {
                App.CurrentApp.CloseBookWindows(book.Id);
                try { _library.Core.KeepRecordOnly(book.Id); } catch { }
                RefreshBooks();
                ShowToast($"已移除《{book.Title}》的文件，保留阅读记录", canUndo: false);
                return;
            }
        }

        string bid = book.Id;
        string bTitle = book.Title;
        App.CurrentApp.CloseBookWindows(bid);
        _library.DeleteBook(bid);
        RefreshBooks();
        ShowToast($"已删除《{bTitle}》", canUndo: true, isDelete: true, undo: () =>
        {
            try { _library.RestoreBook(bid); } catch { }
            RefreshBooks();
        });
    }

    private async Task ShowEditBookDialogAsync(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        ContentDialog? dlgRef = null;

        byte[]? pickedCoverBytes = null;
        var coverPreviewHost = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
            AllowDrop = true,
            Children = { CreateCoverView(book, 76, 110, elevated: true) },
        };
        var pickCoverBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 2, 10, 2),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = "换封面",
        };

        async Task LoadPickedCoverAsync(string path)
        {
            pickedCoverBytes = await File.ReadAllBytesAsync(path);
            coverPreviewHost.Children.Clear();
            var bmp = new BitmapImage { DecodePixelWidth = 152 };
            bmp.UriSource = new Uri(path);
            var b = new Border
            {
                Width = 76,
                Height = 110,
                CornerRadius = new CornerRadius(5),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = new Image { Source = bmp, Stretch = Stretch.UniformToFill },
            };
            coverPreviewHost.Children.Add(b);
            pickCoverBtn.Content = L10n.T("已换新封面");
        }

        coverPreviewHost.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.Copy; };
        coverPreviewHost.Drop += async (_, e) =>
        {
            try
            {
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    var first = items.OfType<Windows.Storage.StorageFile>().FirstOrDefault(f =>
                        f.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        f.Name.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));
                    if (first is not null) await LoadPickedCoverAsync(first.Path);
                }
            }
            catch { }
        };

        pickCoverBtn.Click += async (_, _) =>
        {
            string? path = await NativePickerHelper.PickSingleFileAsync(this, [".jpg", ".jpeg", ".png", ".webp"], viewMode: PickerViewMode.Thumbnail);
            if (!string.IsNullOrEmpty(path)) await LoadPickedCoverAsync(path);
        };

        // 1:1 shelf-edit/2.png: two-row white table card (书名 | ... / 作者 | 几位作者用顿号隔开)
        var titleBox = new TextBox
        {
            PlaceholderText = "留空就用文件里的",
            Text = book.Title,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
        };
        YYChrome.StripTextBoxChrome(titleBox);
        var authorBox = new TextBox
        {
            PlaceholderText = "几位作者用顿号隔开",
            Text = string.Join("、", book.Authors),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            FontSize = 13.5,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 0, 6),
        };
        YYChrome.StripTextBoxChrome(authorBox);

        Grid MakeFieldRow(string label, TextBox box)
        {
            var g = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 5, 12, 5), MinHeight = 42 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.Children.Add(new TextBlock { Text = label, FontSize = 13, Foreground = ThemeHelper.Brush(p.T2), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(box, 1);
            g.Children.Add(box);
            return g;
        }

        var formStack = new StackPanel { Spacing = 0 };
        formStack.Children.Add(MakeFieldRow("书名", titleBox));
        formStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });
        formStack.Children.Add(MakeFieldRow("作者", authorBox));

        var formCard = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.75),
            Margin = new Thickness(0, 6, 0, 0),
            Child = formStack,
        };

        var coverNote = new TextBlock
        {
            Text = "只改书架上显示的名字，书的文件不动。封面也可以直接拖一张图到上面。",
            FontSize = 11.5,
            LineHeight = 18,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(4, 0, 4, 0),
        };

        bool saving = false;
        Button? okBtnRef = null;
        async void SaveAndClose()
        {
            if (saving) return;
            saving = true;
            if (okBtnRef is not null) okBtnRef.IsEnabled = false;
            var newTitle = titleBox.Text.Trim();
            var newAuthors = authorBox.Text
                .Split(['、', ',', '，', ';', '；', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            var coverBytes = pickedCoverBytes;
            string editBookId = book.Id;
            try
            {
                await Task.Run(() =>
                {
                    _library.EditBookInfo(editBookId, newTitle, newAuthors);
                    if (coverBytes is not null)
                        _library.SetCover(editBookId, coverBytes);
                });
            }
            catch (Exception ex)
            {
                saving = false;
                if (okBtnRef is not null) okBtnRef.IsEnabled = true;
                if (!_closed) ShowToast(ex.Message, canUndo: false, isDelete: true);
                return;
            }
            if (_closed) return;
            dlgRef?.Hide();
            RefreshBooks();
            ShowToast("书籍信息已更新", canUndo: false);
        }

        titleBox.KeyDown += (_, ke) => { if (ke.Key == VirtualKey.Enter) { ke.Handled = true; SaveAndClose(); } };
        authorBox.KeyDown += (_, ke) => { if (ke.Key == VirtualKey.Enter) { ke.Handled = true; SaveAndClose(); } };

        var okBtn = YYChrome.MakeSheetPillButton("好", SaveAndClose, dark, _library.Prefs.Tint, "dark", height: 42, stretch: true);
        okBtnRef = okBtn;
        okBtn.Margin = new Thickness(0, 4, 0, 0);

        var body = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(22, 18, 22, 20),
            Children =
            {
                YYChrome.MakeSheetHeader("编辑信息", null, () => dlgRef?.Hide(), dark, titleSize: 18),
                coverPreviewHost,
                pickCoverBtn,
                formCard,
                coverNote,
                okBtn,
            },
        };
        L10n.ApplyTree(body);

        if (RootGrid.XamlRoot is null) return;
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 360, body, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

#if DEBUG
    internal void OpenDebugSheet(string kind)
    {
        if (kind == "edit-book" && _shelfBooks.Count > 0)
            _ = ShowEditBookDialogAsync(_shelfBooks[0]);
        else if (kind == "quote-share" && _shelfBooks.Count > 0)
            _ = QuoteShareDialog.ShowAsync(this, RootGrid.XamlRoot, "赌书消得泼茶香，当时只道是寻常。", "所有的伏笔都在不经意的闲笔里悄然种下。", _shelfBooks[0].Title, _shelfBooks[0].Authors.FirstOrDefault(), _shelfBooks[0].CoverPath, "第一回", _library);
        else if (kind == "feedback")
            _ = _library.Updates.ShowFeedbackComposerAsync(this, RootGrid.XamlRoot);
        else if (kind == "review")
            _ = ShowReviewSheetAsync(null);
    }
#endif

    private void RevealOriginalInExplorer(BookSummary book)
    {
        var orig = _library.TryOriginalFile(book.Id);
        if (string.IsNullOrEmpty(orig) || !File.Exists(orig))
        {
            ShowToast("未找到这本书的本地原文件", canUndo: false);
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{orig}\"") { UseShellExecute = true });
    }

    private async Task ShowPickCollectionDialogAsync(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var inSet = book.Collections.ToHashSet();
        var initialInSet = inSet.ToHashSet();
        // Freeze initial order so toggling a collection doesn't jump rows under the user's cursor (1:1 CollectionPicker.swift)
        var frozenOrder = _library.GetCollections()
            .OrderByDescending(c => initialInSet.Contains(c.Id))
            .ThenByDescending(c => c.UsedAt)
            .Select(c => c.Id)
            .ToList();
        var searchBox = new TextBox
        {
            PlaceholderText = "找合集（支持拼音首字母），或打新名字回车创建…",
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            CornerRadius = new CornerRadius(10),
        };
        var rowsHost = new StackPanel { Spacing = 10 };
        var rowActions = new List<(Button Btn, Action Trigger)>();
        int selIdx = -1;

        void UpdatePickHighlight()
        {
            for (int i = 0; i < rowActions.Count; i++)
            {
                rowActions[i].Btn.Background = ThemeHelper.Brush(i == selIdx ? tint.Soft : Colors.Transparent);
            }
        }

        Border BuildGroupCard(IReadOnlyList<BookCollection> groupCols)
        {
            var stack = new StackPanel { Spacing = 0 };
            for (int idx = 0; idx < groupCols.Count; idx++)
            {
                if (idx > 0)
                    stack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(50, 0, 12, 0) });

                var col = groupCols[idx];
                var cid = col.Id;
                bool inside = inSet.Contains(cid);
                var g = new Grid { ColumnSpacing = 10 };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                g.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(inside ? tint.Soft : p.Inset),
                    Child = YYIcons.Make(YYIcons.CollectionIconName(col.Name, col.Icon), 13, ThemeHelper.Brush(inside ? tint.Accent : p.T1)),
                });
                var m = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                m.Children.Add(L10n.Skip(new TextBlock { Text = col.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) }));
                m.Children.Add(new TextBlock { Text = $"{col.Count} 本", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(m, 1);
                g.Children.Add(m);

                if (inside)
                {
                    var chk = YYIcons.Make("checkmark", 12, ThemeHelper.Brush(tint.Accent));
                    Grid.SetColumn(chk, 2);
                    g.Children.Add(chk);
                }

                var rowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 12, 7),
                    CornerRadius = new CornerRadius(0),
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    UseSystemFocusVisuals = false,
                    Content = g,
                };
                void DoToggle()
                {
                    bool next = !inSet.Contains(cid);
                    if (next) inSet.Add(cid); else inSet.Remove(cid);
                    _library.SetInCollection(cid, book.Id, next);
                    RebuildRows(searchBox.Text);
                    RefreshBooks();
                }
                rowBtn.Click += (_, _) => DoToggle();
                rowActions.Add((rowBtn, DoToggle));
                stack.Children.Add(rowBtn);
            }

            return new Border
            {
                CornerRadius = new CornerRadius(14),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                Child = stack,
            };
        }

        void RebuildRows(string filter)
        {
            rowsHost.Children.Clear();
            rowActions.Clear();
            selIdx = -1;
            string q = filter.Trim();
            var allCols = _library.GetCollections();
            foreach (var c in allCols)
            {
                if (!frozenOrder.Contains(c.Id))
                {
                    frozenOrder.Insert(0, c.Id);
                    if (inSet.Contains(c.Id)) initialInSet.Add(c.Id);
                }
            }

            var cols = allCols
                .Where(c => string.IsNullOrEmpty(q) || NameMatcher.Matches(c.Name, q))
                .OrderBy(c => frozenOrder.IndexOf(c.Id))
                .ToArray();

            if (!string.IsNullOrEmpty(q) && !allCols.Any(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase)))
            {
                var createGrid = new Grid { ColumnSpacing = 10 };
                createGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
                createGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                createGrid.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(9),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Child = YYIcons.Make("plus", 12, ThemeHelper.Brush(tint.Accent)),
                });
                var createMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                createMeta.Children.Add(new TextBlock { Text = $"新建合集“{q}”", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(tint.Accent) });
                createMeta.Children.Add(new TextBlock { Text = "建好就把这本书放进去", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
                Grid.SetColumn(createMeta, 1);
                createGrid.Children.Add(createMeta);

                var createBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(10, 7, 12, 7),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    UseSystemFocusVisuals = false,
                    Content = createGrid,
                };
                void DoCreate()
                {
                    try
                    {
                        var created = _library.CreateCollection(q, book.Id);
                        inSet.Add(created.Id);
                        initialInSet.Add(created.Id);
                        frozenOrder.Insert(0, created.Id);
                        searchBox.Text = "";
                        RebuildRows("");
                        RefreshBooks();
                    }
                    catch { }
                }
                createBtn.Click += (_, _) => DoCreate();
                rowActions.Add((createBtn, DoCreate));
                rowsHost.Children.Add(createBtn);
            }

            var inGroup = cols.Where(c => initialInSet.Contains(c.Id)).ToArray();
            var restGroup = cols.Where(c => !initialInSet.Contains(c.Id)).ToArray();

            if (inGroup.Length > 0)
                rowsHost.Children.Add(BuildGroupCard(inGroup));
            if (restGroup.Length > 0)
                rowsHost.Children.Add(BuildGroupCard(restGroup));
            if (inGroup.Length == 0 && restGroup.Length == 0 && string.IsNullOrEmpty(q))
            {
                rowsHost.Children.Add(new TextBlock
                {
                    Text = "还没有合集，点右上角「新建合集」就能把这本书收进去。",
                    FontSize = 12,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4, 12, 4, 12),
                });
            }

            if (rowActions.Count > 0) { selIdx = 0; UpdatePickHighlight(); }
            L10n.ApplyTree(rowsHost);
        }

        searchBox.TextChanged += (_, _) => RebuildRows(searchBox.Text);
        searchBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == VirtualKey.Down && rowActions.Count > 0)
            {
                selIdx = (selIdx + 1) % rowActions.Count;
                UpdatePickHighlight();
                ke.Handled = true;
                return;
            }
            if (ke.Key == VirtualKey.Up && rowActions.Count > 0)
            {
                selIdx = selIdx <= 0 ? rowActions.Count - 1 : selIdx - 1;
                UpdatePickHighlight();
                ke.Handled = true;
                return;
            }
            if (ke.Key != VirtualKey.Enter) return;
            ke.Handled = true;
            if (selIdx >= 0 && selIdx < rowActions.Count)
            {
                rowActions[selIdx].Trigger();
                return;
            }
            string q = searchBox.Text.Trim();
            if (string.IsNullOrEmpty(q)) return;
            try
            {
                var created = _library.CreateCollection(q, book.Id);
                inSet.Add(created.Id);
                initialInSet.Add(created.Id);
                frozenOrder.Insert(0, created.Id);
                searchBox.Text = "";
                RebuildRows("");
                RefreshBooks();
            }
            catch { }
        };
        RebuildRows("");

        // 1:1 CollectionPicker.swift: circular search button + 加入合集 / book title + white 新建合集 pill + grouped YYGroup sections
        ContentDialog? dlgRef = null;
        searchBox.Visibility = Visibility.Collapsed;
        var searchCircleBtn = new Button
        {
            Width = 32,
            Height = 32,
            MinWidth = 32,
            MinHeight = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = YYIcons.Make("search", 13.5, ThemeHelper.Brush(p.T1)),
        };
        AutomationProperties.SetName(searchCircleBtn, L10n.T("搜索"));
        searchCircleBtn.Click += (_, _) =>
        {
            bool nextVis = searchBox.Visibility != Visibility.Visible;
            searchBox.Visibility = nextVis ? Visibility.Visible : Visibility.Collapsed;
            if (nextVis) searchBox.Focus(FocusState.Programmatic);
        };

        var newColBtn = new Button
        {
            Height = 30,
            MinHeight = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(0.5),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(p.T1),
            UseSystemFocusVisuals = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = L10n.T("新建合集"),
        };
        newColBtn.Click += async (_, _) =>
        {
            string? createdName = await YYChrome.ShowPromptAsync(
                RootGrid.XamlRoot,
                dark,
                "新建合集",
                placeholder: "合集的名字，例如：睡前、科幻",
                confirmText: "好",
                cancelText: "取消",
                subtitle: $"建好就把《{book.Title}》放进去",
                tint: _library.Prefs.Tint);
            if (!string.IsNullOrWhiteSpace(createdName))
            {
                try
                {
                    var created = _library.CreateCollection(createdName.Trim(), book.Id);
                    inSet.Add(created.Id);
                    RefreshBooks();
                    ShowToast($"已新建合集“{created.Name}”并放入《{book.Title}》", canUndo: false);
                }
                catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
                return;
            }
            await ShowPickCollectionDialogAsync(book);
        };

        var popHeader = new Grid { ColumnSpacing = 10 };
        popHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        popHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        popHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        popHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        popHeader.Children.Add(searchCircleBtn);

        var popMeta = new StackPanel { Spacing = 0, VerticalAlignment = VerticalAlignment.Center };
        popMeta.Children.Add(new TextBlock { Text = L10n.T("加入合集"), FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        popMeta.Children.Add(L10n.Skip(new TextBlock { Text = book.Title, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
        Grid.SetColumn(popMeta, 1);
        popHeader.Children.Add(popMeta);

        Grid.SetColumn(newColBtn, 2);
        popHeader.Children.Add(newColBtn);

        var closeCircle = YYChrome.MakeSheetCloseButton(() => dlgRef?.Hide(), dark, 26);
        Grid.SetColumn(closeCircle, 3);
        popHeader.Children.Add(closeCircle);

        var popBody = new StackPanel
        {
            Spacing = 10,
            Padding = new Thickness(18, 16, 18, 16),
            Children =
            {
                popHeader,
                searchBox,
                new ScrollViewer { MaxHeight = 300, Content = rowsHost },
            },
        };

        if (RootGrid.XamlRoot is null) return;
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 360, popBody, tint: _library.Prefs.Tint);
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
    }

    // 1:1 对齐 Shelf.swift lines 54-64 & 260-269
    private string FormatShelfNote(BookSummary book)
    {
        if (!book.Available) return "在别的设备上";
        int pct = (int)Math.Round(book.Progress * 100);
        if (book.Format == BookFormat.Audio || _pairedBookIds.Contains(book.Id))
        {
            if (book.Status == ReadingStatus.Finished) return "有声 · 听完";
            if (book.Progress > 0.002) return $"有声 · {Math.Max(1, pct)}%";
            return book.ListenMinutes >= 60 ? $"有声 · {Math.Max(1, book.ListenMinutes / 60)} 小时" : "有声";
        }
        if (book.Status == ReadingStatus.Finished) return "读完";
        if (book.Progress > 0.002) return $"{Math.Max(1, pct)}%";
        return book.Status switch
        {
            ReadingStatus.Reading => "刚开始",
            ReadingStatus.Paused => "搁置",
            ReadingStatus.Dropped => "弃读",
            _ => "想读",
        };
    }

    // ---------- 2. 子页（进度 / 播客 / 笔记 / 生词本 / 合集） ----------

    private void RenderProgressPage()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        _recordBuilder.RenderInto(SubPageStack, _keptBooks, dark);
    }

    private void RenderPodcastsPage()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        _podcastBuilder.RenderInto(SubPageStack, dark);
    }

    public static UIElement BuildHighlightedQuote(Mark mark, YYPalette palette, bool dark, double fontSize = 13.5)
    {
        var serif = new FontFamily(YYFontFamily.Serif.WindowsFamily());
        double lineHeight = Math.Round(fontSize * 1.65);

        if (mark.Style == MarkStyle.Wave)
        {
            // 1:1 ReaderCards.swift WavyLineRenderer: red quadratic sine wave beneath every rendered line
            var container = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var tb = new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 0, 0, 3),
            };
            var wavePath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = ThemeHelper.Brush(palette.HlR),
                StrokeThickness = 1.15,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                IsHitTestVisible = false,
            };
            tb.SizeChanged += (_, e) =>
            {
                double w = e.NewSize.Width;
                double h = e.NewSize.Height;
                if (w < 4 || h < 4) return;
                int lines = Math.Max(1, (int)Math.Round((h - 3) / lineHeight));
                var geo = new PathGeometry();
                const double step = 3.6;
                const double amp = 2.0;
                for (int li = 0; li < lines; li++)
                {
                    double y = Math.Min(h - 1.5, (li + 1) * lineHeight - 1.5);
                    double lineW = w;
                    var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(0, y), IsClosed = false };
                    double x = 0;
                    bool up = true;
                    while (x < lineW - 0.5)
                    {
                        double next = Math.Min(x + step, lineW);
                        fig.Segments.Add(new QuadraticBezierSegment
                        {
                            Point1 = new Windows.Foundation.Point((x + next) / 2.0, y + (up ? -amp : amp)),
                            Point2 = new Windows.Foundation.Point(next, y),
                        });
                        x = next;
                        up = !up;
                    }
                    geo.Figures.Add(fig);
                }
                wavePath.Data = geo;
            };
            container.Children.Add(L10n.Skip(tb));
            container.Children.Add(wavePath);
            return container;
        }

        if (mark.Style is MarkStyle.Underline or MarkStyle.Dotted)
        {
            var container = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var tb = new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0, 0, 0, 2),
            };
            var linePath = new Microsoft.UI.Xaml.Shapes.Path
            {
                Stroke = ThemeHelper.Brush(palette.T2),
                StrokeThickness = mark.Style == MarkStyle.Dotted ? 1.1 : 1.25,
                Opacity = mark.Style == MarkStyle.Dotted ? 0.65 : 0.85,
                IsHitTestVisible = false,
            };
            if (mark.Style == MarkStyle.Dotted)
                linePath.StrokeDashArray = new DoubleCollection { 1.5, 2.5 };
            tb.SizeChanged += (_, e) =>
            {
                double w = e.NewSize.Width;
                double h = e.NewSize.Height;
                if (w < 4 || h < 4) return;
                int lines = Math.Max(1, (int)Math.Round((h - 2) / lineHeight));
                var geo = new PathGeometry();
                for (int li = 0; li < lines; li++)
                {
                    double y = Math.Min(h - 1.0, (li + 1) * lineHeight - 1.5);
                    var fig = new PathFigure { StartPoint = new Windows.Foundation.Point(0, y), IsClosed = false };
                    fig.Segments.Add(new LineSegment { Point = new Windows.Foundation.Point(w, y) });
                    geo.Figures.Add(fig);
                }
                linePath.Data = geo;
            };
            container.Children.Add(L10n.Skip(tb));
            container.Children.Add(linePath);
            return container;
        }

        Color hlColor = mark.Style switch
        {
            MarkStyle.Blue => palette.MkB,
            MarkStyle.Pink => palette.MkP,
            _ => palette.MkY,
        };
        if (!dark) hlColor = Color.FromArgb(145, hlColor.R, hlColor.G, hlColor.B);

        return new Border
        {
            Background = ThemeHelper.Brush(hlColor),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 1.5, 4, 1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = L10n.Skip(new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = lineHeight,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
            }),
        };
    }

    private static readonly string[] WeekdayNames = ["星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"];

    private static string FormatDayName(DateTime day)
    {
        var today = DateTime.Today;
        if (day.Date == today) return "今天";
        if (day.Date == today.AddDays(-1)) return "昨天";
        return WeekdayNames[(int)day.DayOfWeek];
    }

    private IReadOnlyList<Mark> GetCachedMarks(string bookId)
    {
        if (!_notesMarksCache.TryGetValue(bookId, out var marks))
        {
            try { marks = _library.GetMarks(bookId); }
            catch { marks = []; }
            _notesMarksCache[bookId] = marks;
        }
        return marks;
    }

    private void InvalidateNotesCache()
    {
        _notesMarksCache.Clear();
        _cachedNoteConflicts = null;
        _cachedReviewDeck = null;
    }

    private static bool MatchesNotesChipStatic(Mark m, string chip) => chip switch
    {
        "thoughts" => !string.IsNullOrWhiteSpace(m.Note),
        "yellow" => m.Style == MarkStyle.Yellow,
        "blue" => m.Style == MarkStyle.Blue,
        "pink" => m.Style == MarkStyle.Pink,
        "wave" => m.Style == MarkStyle.Wave,
        _ => true,
    };

    private static bool MatchesNotesFilterStatic(Mark m, string chip, string rawQuery, BookSummary? book = null)
    {
        if (!MatchesNotesChipStatic(m, chip)) return false;
        string q = rawQuery.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        if (m.Text.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.IsNullOrEmpty(m.Note) && m.Note.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        if (book is not null && NameMatcher.Matches(book.Title, q)) return true;
        return false;
    }

    private bool MatchesNotesFilter(Mark m, BookSummary? book = null) =>
        MatchesNotesFilterStatic(m, _notesChip, _notesQuery, book);

    private string BuildFilteredNotesMarkdownCore(
        BookSummary? chosenBook,
        IReadOnlyList<BookSummary> keptSnapshot,
        string chip,
        string query)
    {
        bool hasActiveFilter = chip != "all" || !string.IsNullOrWhiteSpace(query);
        if (!hasActiveFilter)
        {
            return chosenBook is not null
                ? NoteExportService.BuildObsidianBookMarkdown(_library, chosenBook)
                : NoteExporter.BuildLibraryMarkdown(_library);
        }

        string FormatSingleBookFiltered(BookSummary book, IReadOnlyList<Mark> filtered)
        {
            BookDetail? detail = null;
            if (!book.Record)
            {
                try { detail = _library.GetBookDetail(book.Id); } catch { }
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# {book.Title}");
            if (book.Authors.Length > 0) sb.AppendLine($"\n> 作者：{string.Join(" / ", book.Authors)}");
            sb.AppendLine($"> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm} · 共 {filtered.Count} 条筛选划线\n");

            foreach (var group in filtered.GroupBy(m => m.Section).OrderBy(g => g.Key))
            {
                string chapter = book.Format == BookFormat.Pdf
                    ? $"第 {group.Key + 1} 页"
                    : (detail is not null && group.Key < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[group.Key].Title)
                        ? detail.Sections[group.Key].Title!
                        : (detail is null && group.Key == 0 ? "笔记" : $"第 {group.Key + 1} 节"));
                sb.AppendLine($"## {chapter}\n");
                foreach (var m in group)
                {
                    sb.AppendLine($"> {m.Text.Replace("\n", "\n> ")}");
                    if (!string.IsNullOrWhiteSpace(m.Note))
                        sb.AppendLine($"\n{m.Note}");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        if (chosenBook is not null)
        {
            var filtered = _library.GetMarks(chosenBook.Id)
                .Where(m => MatchesNotesFilterStatic(m, chip, query, chosenBook))
                .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
                .ToArray();
            return FormatSingleBookFiltered(chosenBook, filtered);
        }

        var allSb = new System.Text.StringBuilder();
        allSb.AppendLine($"# 语阅笔记筛选导出\n\n> 导出时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n");
        int booksExported = 0;
        foreach (var book in keptSnapshot)
        {
            var filtered = _library.GetMarks(book.Id)
                .Where(m => MatchesNotesFilterStatic(m, chip, query, book))
                .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
                .ToArray();
            if (filtered.Length == 0) continue;
            booksExported++;
            allSb.AppendLine("---\n");
            allSb.AppendLine(FormatSingleBookFiltered(book, filtered));
        }
        if (booksExported == 0)
            allSb.AppendLine("（当前筛选条件下没有匹配的划线或想法）");
        return allSb.ToString();
    }

    private async Task LoadReviewDeckIntoSlotAsync(ContentControl slot, bool dark)
    {
        try
        {
            long dayStartMs = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
            var deck = await Task.Run(() => _library.Core.ReviewToday(null, dayStartMs, 5));
            if (_closed) return;
            _cachedReviewDeck = deck;
            if ((deck.Items.Length > 0 || deck.DoneToday > 0) && slot.XamlRoot is not null)
            {
                slot.Content = BuildReviewDeckCard(deck, dark);
                L10n.ApplyTree(slot);
            }
        }
        catch { }
    }

    private void RenderNotesPage()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var bookNotes = _keptBooks
            .Select(b => (Book: b, Marks: GetCachedMarks(b.Id)))
            .Where(x => x.Marks.Count > 0)
            .OrderByDescending(x => x.Marks.Max(m => m.At))
            .ToArray();

        if (bookNotes.Length == 0)
        {
            var emptyStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 110, 0, 0) };
            emptyStack.Children.Add(YYIcons.Make("highlighter", 28, ThemeHelper.Brush(palette.T3)));
            emptyStack.Children.Add(new TextBlock { Text = "还没有笔记", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(palette.T1), HorizontalAlignment = HorizontalAlignment.Center });
            emptyStack.Children.Add(new TextBlock { Text = "读书时选中文字即可划线；所有书的划线和想法都会在这里", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T2), HorizontalAlignment = HorizontalAlignment.Center });
            SubPageStack.Children.Add(emptyStack);
            L10n.ApplyTree(SubPageStack);
            return;
        }

        if (_notesChosenBookId is null || !bookNotes.Any(b => b.Book.Id == _notesChosenBookId))
            _notesChosenBookId = bookNotes[0].Book.Id;

        // 1:1 ConflictSheet.swift: 存在多设备想法冲突时在笔记页顶部展示解决入口横幅
        try
        {
            _cachedNoteConflicts ??= _library.Core.NoteConflicts();
            var conflicts = _cachedNoteConflicts;
            if (conflicts.Length > 0)
            {
                var cfGrid = new Grid { Padding = new Thickness(14, 10, 14, 10) };
                cfGrid.Children.Add(new TextBlock
                {
                    Text = $"⚠️ 有 {conflicts.Length} 条笔记在两台设备上各有不同改动",
                    FontSize = 12.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(palette.T1),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var resBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                    Content = "查看并解决…",
                    Height = 26,
                    MinHeight = 26,
                    FontSize = 11.5,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                resBtn.Click += async (_, _) => await SnapshotAndConflictDialogs.ShowNoteConflictsSheetAsync(RootGrid.XamlRoot, _library, RefreshBooks);
                cfGrid.Children.Add(resBtn);
                SubPageStack.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(tint.Soft),
                    BorderBrush = ThemeHelper.Brush(tint.Edge),
                    BorderThickness = new Thickness(1),
                    Margin = new Thickness(0, 0, 0, 8),
                    Child = cfGrid,
                });
            }
        }
        catch { }

        bool MatchesChip(Mark m, BookSummary? bk = null) => MatchesNotesFilter(m, bk);

        UIElement MakeNoMatchingNotesEmptyState()
        {
            var emptyFilter = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 64, 0, 0) };
            emptyFilter.Children.Add(YYIcons.Make("search", 24, ThemeHelper.Brush(palette.T3)));
            emptyFilter.Children.Add(new TextBlock
            {
                Text = L10n.T("没有匹配的笔记"),
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            return emptyFilter;
        }

        StackPanel BuildFilterChips(IReadOnlyList<Mark> scopeMarks)
        {
            var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 6) };
            void AddChip(string key, string? text, int count, Color? dot = null, bool wave = false)
            {
                bool on = _notesChip == key;
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                if (wave)
                {
                    sp.Children.Add(new TextBlock { Text = "~", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.HlR), VerticalAlignment = VerticalAlignment.Center });
                }
                else if (dot is Color dc)
                {
                    sp.Children.Add(new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Background = ThemeHelper.Brush(dc), VerticalAlignment = VerticalAlignment.Center });
                }
                if (text is not null) sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5 });
                sp.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 11, Opacity = 0.72 });

                var b = new Button
                {
                    Content = sp,
                    Height = 26,
                    MinHeight = 26,
                    Padding = new Thickness(11, 0, 11, 0),
                    CornerRadius = new CornerRadius(13),
                    BorderThickness = new Thickness(1),
                    Background = ThemeHelper.Brush(on ? tint.Black : palette.Pill),
                    Foreground = ThemeHelper.Brush(on ? tint.OnBlack : palette.T2),
                    BorderBrush = ThemeHelper.Brush(on ? tint.Edge : palette.PillLine),
                    UseSystemFocusVisuals = false,
                };
                b.Click += (_, _) => { _notesChip = on && key != "all" ? "all" : key; RenderNotesPage(); };
                chips.Children.Add(b);
            }

            AddChip("all", "全部", scopeMarks.Count);
            AddChip("thoughts", "有想法", scopeMarks.Count(m => !string.IsNullOrWhiteSpace(m.Note)));
            chips.Children.Add(new Border { Width = 1, Height = 15, Background = ThemeHelper.Brush(palette.Line), Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            foreach (var (key, st, col, isWave) in new[]
            {
                ("yellow", MarkStyle.Yellow, palette.HlY, false),
                ("blue", MarkStyle.Blue, palette.HlB, false),
                ("pink", MarkStyle.Pink, palette.MkP, false),
                ("wave", MarkStyle.Wave, palette.HlR, true),
            })
            {
                int n = scopeMarks.Count(m => m.Style == st);
                if (n > 0) AddChip(key, null, n, col, isWave);
            }
            return chips;
        }

        var chapterTitleCache = new Dictionary<string, Dictionary<uint, string>>();
        string GetChapterTitle(BookSummary book, uint sec)
        {
            if (book.Format == BookFormat.Pdf) return $"第 {sec + 1} 页";
            string bookId = book.Id;
            if (!chapterTitleCache.TryGetValue(bookId, out var map))
            {
                map = [];
                try
                {
                    var d = _library.GetBookDetail(bookId);
                    foreach (var t in d.Toc)
                        if (!map.ContainsKey(t.Section)) map[t.Section] = t.Title;
                }
                catch { }
                chapterTitleCache[bookId] = map;
            }
            return map.TryGetValue(sec, out var title) && !string.IsNullOrWhiteSpace(title) ? title : $"第 {sec + 1} 章";
        }

        UIElement BuildNoteEntry(BookSummary book, Mark mark, bool showBookFooter)
        {
            var entry = new StackPanel { Spacing = 7, Padding = new Thickness(0, 12, 0, 10) };
            entry.Children.Add(L10n.Skip(BuildHighlightedQuote(mark, palette, dark, 14)));

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                var thoughtRow = new Grid { ColumnSpacing = 9, Margin = new Thickness(2, 2, 0, 0) };
                thoughtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.5) });
                thoughtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                thoughtRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(0.75) });
                var tb = L10n.Skip(new TextBlock
                {
                    Text = mark.Note,
                    FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    TextWrapping = TextWrapping.Wrap,
                });
                Grid.SetColumn(tb, 1);
                thoughtRow.Children.Add(tb);
                entry.Children.Add(thoughtRow);
            }

            var dt = DateTimeOffset.FromUnixTimeMilliseconds(mark.At).LocalDateTime;
            var footer = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            if (showBookFooter)
            {
                var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
                left.Children.Add(CreateCoverView(book, 14, 20, elevated: false));
                left.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = $"{book.Title} · {L10n.T(GetChapterTitle(book, mark.Section))}",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(palette.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }));
                footer.Children.Add(left);
            }
            var rightMeta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (mark.Heard)
                rightMeta.Children.Add(YYIcons.Make("headphones", 10.5, ThemeHelper.Brush(tint.Accent)));
            rightMeta.Children.Add(new TextBlock
            {
                Text = showBookFooter ? dt.ToString("HH:mm") : $"{dt.Month} 月 {dt.Day} 日",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            footer.Children.Add(rightMeta);
            entry.Children.Add(footer);

            var ctx = new MenuFlyout();
            var jumpItem = new MenuFlyoutItem { Text = L10n.T("回到书里这一页") };
            jumpItem.Click += (_, _) => TryOpenBook(book, section: mark.Section, sentence: (int)mark.Start.Sentence);

            var editNoteItem = new MenuFlyoutItem { Text = L10n.T(string.IsNullOrWhiteSpace(mark.Note) ? "写想法…" : "改想法…") };
            editNoteItem.Click += async (_, _) =>
            {
                var updated = await BookRecordPageBuilder.ShowThoughtEditorSheetAsync(
                    RootGrid.XamlRoot, dark, _library.Prefs.Tint, mark.Text, mark.Note);
                if (updated.Saved)
                {
                    _library.Core.SetMarkNote(book.Id, mark.Id, updated.Note);
                    RefreshBooks();
                }
            };

            var styleSubItem = new MenuFlyoutSubItem { Text = L10n.T("换个样子") };
            foreach (var (st, stLabel) in new[]
            {
                (MarkStyle.Yellow, "荧光黄"),
                (MarkStyle.Blue, "浅蓝"),
                (MarkStyle.Pink, "樱粉"),
                (MarkStyle.Wave, "波浪线"),
                (MarkStyle.Underline, "实线下划线"),
                (MarkStyle.Dotted, "点线下划线"),
            })
            {
                var targetStyle = st;
                var smi = new ToggleMenuFlyoutItem { Text = L10n.T(stLabel), IsChecked = mark.Style == targetStyle };
                smi.Click += (_, _) =>
                {
                    _library.SetMarkStyle(book.Id, mark.Id, targetStyle);
                    RefreshBooks();
                };
                styleSubItem.Items.Add(smi);
            }

            var copyItem = new MenuFlyoutItem { Text = L10n.T("复制") };
            copyItem.Click += (_, _) =>
            {
                var dp = new DataPackage();
                dp.SetText(string.IsNullOrWhiteSpace(mark.Note) ? mark.Text : $"{mark.Text}\n—— {mark.Note}");
                Clipboard.SetContent(dp);
                ShowToast("已复制划线", canUndo: false);
            };

            string noteChapter = GetChapterTitle(book, mark.Section);
            var shareItem = new MenuFlyoutItem { Text = L10n.T("分享书摘…") };
            shareItem.Click += async (_, _) => await QuoteShareDialog.ShowAsync(
                this, RootGrid.XamlRoot, mark.Text, mark.Note, book.Title, book.Authors.FirstOrDefault(), book.CoverPath, chapter: noteChapter, library: _library);

            var delItem = new MenuFlyoutItem { Text = L10n.T("删除划线"), Foreground = ThemeHelper.Brush(palette.Danger) };
            delItem.Click += (_, _) =>
            {
                string bid = book.Id;
                string mid = mark.Id;
                _library.DeleteMark(bid, mid);
                RefreshBooks();
                ShowToast("已删除划线", canUndo: true, isDelete: true, undo: () =>
                {
                    try { _library.Core.RestoreMark(bid, mid); } catch { }
                    RefreshBooks();
                });
            };

            ctx.Items.Add(jumpItem);
            ctx.Items.Add(editNoteItem);
            ctx.Items.Add(styleSubItem);
            ctx.Items.Add(copyItem);
            ctx.Items.Add(shareItem);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delItem);

            var btn = new Button
            {
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                UseSystemFocusVisuals = false,
                Content = entry,
                ContextFlyout = ctx,
            };
            btn.DoubleTapped += (_, _) => TryOpenBook(book, section: mark.Section, sentence: (int)mark.Start.Sentence);
            return btn;
        }

        Border WrapNoteCard(IReadOnlyList<UIElement> entries)
        {
            var sp = new StackPanel { Spacing = 0 };
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) sp.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(palette.Line) });
                sp.Children.Add(entries[i]);
            }
            return new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 4, 20, 4),
                Child = sp,
            };
        }

        if (!_notesByBook)
        {
            var allMarks = bookNotes.SelectMany(bn => bn.Marks).ToArray();
            SubPageStack.Children.Add(BuildFilterChips(allMarks));

            // 1:1 ReviewDeckCard（今天回顾三层叠卡，MacReviewView.swift:70-156），异步加载不阻塞主线程
            var reviewDeckSlot = new ContentControl();
            SubPageStack.Children.Add(reviewDeckSlot);
            if (_cachedReviewDeck is ReviewDeck cachedDeck)
            {
                if (cachedDeck.Items.Length > 0 || cachedDeck.DoneToday > 0)
                    reviewDeckSlot.Content = BuildReviewDeckCard(cachedDeck, dark);
            }
            else
            {
                _ = LoadReviewDeckIntoSlotAsync(reviewDeckSlot, dark);
            }

            var byDay = bookNotes
                .SelectMany(bn => bn.Marks.Where(m => MatchesChip(m, bn.Book)).Select(m => (bn.Book, Mark: m, Day: DateTimeOffset.FromUnixTimeMilliseconds(m.At).LocalDateTime.Date)))
                .OrderByDescending(x => x.Mark.At)
                .GroupBy(x => x.Day)
                .ToArray();

            if (byDay.Length == 0)
            {
                SubPageStack.Children.Add(MakeNoMatchingNotesEmptyState());
            }
            else
            {
                foreach (var grp in byDay)
                {
                    var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 4, 0, 8) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                    var side = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
                    side.Children.Add(new TextBlock { Text = FormatDayName(grp.Key), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.T1) });
                    side.Children.Add(new TextBlock { Text = $"{grp.Key.Month} 月 {grp.Key.Day} 日", FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
                    side.Children.Add(new TextBlock { Text = $"{grp.Count()} 条", FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
                    row.Children.Add(side);

                    var card = WrapNoteCard(grp.Select(x => BuildNoteEntry(x.Book, x.Mark, showBookFooter: true)).ToArray());
                    Grid.SetColumn(card, 1);
                    row.Children.Add(card);
                    SubPageStack.Children.Add(row);
                }
            }
        }
        else
        {
            bool hasFilter = !string.IsNullOrWhiteSpace(_notesQuery) || _notesChip != "all";
            var filteredBookNotes = hasFilter
                ? bookNotes
                    .Select(bn => (bn.Book, AllMarks: bn.Marks, MatchedMarks: (IReadOnlyList<Mark>)bn.Marks.Where(m => MatchesChip(m, bn.Book)).ToArray()))
                    .Where(x => x.MatchedMarks.Count > 0)
                    .ToArray()
                : bookNotes
                    .Select(bn => (bn.Book, AllMarks: bn.Marks, MatchedMarks: bn.Marks))
                    .ToArray();

            if (filteredBookNotes.Length == 0)
            {
                SubPageStack.Children.Add(BuildFilterChips(bookNotes.SelectMany(bn => bn.Marks).ToArray()));
                SubPageStack.Children.Add(MakeNoMatchingNotesEmptyState());
                L10n.ApplyTree(SubPageStack);
                return;
            }

            if (_notesChosenBookId is null || !filteredBookNotes.Any(b => b.Book.Id == _notesChosenBookId))
                _notesChosenBookId = filteredBookNotes[0].Book.Id;

            bool coversOnly = _library.Prefs.NotesCoversOnly;
            var split = new Grid { ColumnSpacing = 22 };
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(coversOnly ? 52 : 250) });
            split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Border BuildColumnSwitch()
            {
                var swRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
                foreach (var (val, icon, tip) in new[] { (false, "list", "显示书名"), (true, "col-2", "只看封面") })
                {
                    bool active = coversOnly == val;
                    var cb = new Button
                    {
                        Width = 22,
                        Height = 19,
                        MinWidth = 22,
                        MinHeight = 19,
                        Padding = new Thickness(0),
                        CornerRadius = new CornerRadius(6),
                        BorderThickness = new Thickness(0),
                        Background = active ? ThemeHelper.Brush(palette.Card) : new SolidColorBrush(Colors.Transparent),
                        UseSystemFocusVisuals = false,
                        Content = YYIcons.Make(icon, 10.5, ThemeHelper.Brush(active ? palette.T1 : palette.T3)),
                    };
                    ToolTipService.SetToolTip(cb, tip);
                    AutomationProperties.SetName(cb, L10n.T(tip));
                    bool targetVal = val;
                    cb.Click += (_, _) =>
                    {
                        _library.Prefs.NotesCoversOnly = targetVal;
                        _library.SavePrefs();
                        RenderNotesPage();
                    };
                    swRow.Children.Add(cb);
                }
                return new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Background = ThemeHelper.Brush(palette.Inset),
                    Padding = new Thickness(2),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Child = swRow,
                };
            }

            var leftColWrap = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
            if (coversOnly)
            {
                var swTop = BuildColumnSwitch();
                swTop.HorizontalAlignment = HorizontalAlignment.Center;
                leftColWrap.Children.Add(swTop);

                var railStack = new StackPanel { Spacing = 8, Padding = new Thickness(0, 6, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
                foreach (var bn in filteredBookNotes)
                {
                    bool on = bn.Book.Id == _notesChosenBookId;
                    var covWrap = new Border
                    {
                        Padding = new Thickness(3),
                        CornerRadius = new CornerRadius(6),
                        BorderBrush = on ? ThemeHelper.Brush(tint.Accent) : new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(1.5),
                        Opacity = on ? 1.0 : 0.76,
                        Child = CreateCoverView(bn.Book, 30, 44, elevated: false),
                    };
                    var cBtn = new Button
                    {
                        Padding = new Thickness(0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = covWrap,
                    };
                    ToolTipService.SetToolTip(cBtn, hasFilter ? $"{bn.Book.Title} ({bn.MatchedMarks.Count})" : bn.Book.Title);
                    AutomationProperties.SetName(cBtn, bn.Book.Title);
                    string bid = bn.Book.Id;
                    cBtn.Click += (_, _) => { _notesChosenBookId = bid; ConfigureHeaderForCurrentView(); RenderNotesPage(); };
                    railStack.Children.Add(cBtn);
                }
                leftColWrap.Children.Add(new Border
                {
                    Width = 48,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(palette.Card),
                    BorderBrush = ThemeHelper.Brush(palette.Line),
                    BorderThickness = new Thickness(1),
                    Child = railStack,
                });
            }
            else
            {
                var headBar = new Grid { Margin = new Thickness(6, 0, 2, 0) };
                headBar.Children.Add(new TextBlock
                {
                    Text = $"{filteredBookNotes.Length} 本书",
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(palette.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                headBar.Children.Add(BuildColumnSwitch());
                leftColWrap.Children.Add(headBar);

                var leftStack = new StackPanel { Spacing = 2 };
                foreach (var bn in filteredBookNotes)
                {
                    bool on = bn.Book.Id == _notesChosenBookId;
                    int thoughtCnt = bn.AllMarks.Count(m => !string.IsNullOrWhiteSpace(m.Note));
                    long latestAt = bn.AllMarks.Max(m => m.At);
                    var r = new Grid { ColumnSpacing = 10 };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.Children.Add(CreateCoverView(bn.Book, 26, 38, elevated: false));
                    var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                    var topRow = new Grid { ColumnSpacing = 6 };
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    topRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    topRow.Children.Add(L10n.Skip(new TextBlock { Text = bn.Book.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(on ? tint.Accent : palette.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
                    var agoTb = new TextBlock { Text = ShortAgo(latestAt), FontSize = 10, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(agoTb, 1);
                    topRow.Children.Add(agoTb);
                    info.Children.Add(topRow);
                    string countLine = hasFilter
                        ? $"{bn.MatchedMarks.Count} 条"
                        : (thoughtCnt > 0 ? $"划线 {bn.AllMarks.Count} · 想法 {thoughtCnt}" : $"划线 {bn.AllMarks.Count}");
                    info.Children.Add(new TextBlock
                    {
                        Text = countLine,
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(hasFilter ? tint.Accent : palette.T3),
                    });
                    Grid.SetColumn(info, 1);
                    r.Children.Add(info);

                    var btn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(8, 7, 10, 7),
                        CornerRadius = new CornerRadius(10),
                        BorderThickness = new Thickness(0),
                        Background = on ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Colors.Transparent),
                        UseSystemFocusVisuals = false,
                        Content = r,
                    };
                    string bid = bn.Book.Id;
                    btn.Click += (_, _) => { _notesChosenBookId = bid; ConfigureHeaderForCurrentView(); RenderNotesPage(); };
                    leftStack.Children.Add(btn);
                }

                leftColWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(palette.Card),
                    BorderBrush = ThemeHelper.Brush(palette.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(5),
                    Child = leftStack,
                });
            }
            split.Children.Add(leftColWrap);

            var chosen = filteredBookNotes.First(b => b.Book.Id == _notesChosenBookId);
            int chosenThoughts = chosen.AllMarks.Count(m => !string.IsNullOrWhiteSpace(m.Note));
            var rightStack = new StackPanel { Spacing = 14 };
            var head = new Grid { ColumnSpacing = 14 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(CreateCoverView(chosen.Book, 46, 66, elevated: false));
            var hMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            hMeta.Children.Add(L10n.Skip(new TextBlock { Text = chosen.Book.Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.T1) }));
            int chosenPct = (int)Math.Round(chosen.Book.Progress * 100);
            string progPart = chosen.Book.Record
                ? $"{L10n.T("纸书")} · {L10n.T(BookRecordPageBuilder.StatusDisplayName(chosen.Book.Status))}"
                : (chosen.Book.Status == ReadingStatus.Finished ? L10n.T("已读完") : L10n.T($"读到 {chosenPct}%"));
            string line1 = string.Join(" · ", new[] { chosen.Book.Authors.FirstOrDefault(), progPart }.Where(s => !string.IsNullOrWhiteSpace(s)));
            hMeta.Children.Add(L10n.Skip(new TextBlock
            {
                Text = line1,
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(palette.T2),
            }));
            hMeta.Children.Add(new TextBlock
            {
                Text = $"划线 {chosen.AllMarks.Count}{(chosenThoughts > 0 ? $" · 想法 {chosenThoughts}" : "")} · 按书里的顺序",
                FontSize = 11,
                Foreground = ThemeHelper.Brush(palette.T3),
            });
            Grid.SetColumn(hMeta, 1);
            head.Children.Add(hMeta);

            var rightBtns = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var revBookSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            revBookSp.Children.Add(YYIcons.Make("refresh", 11, ThemeHelper.Brush(palette.T1)));
            revBookSp.Children.Add(new TextBlock { Text = "回顾", FontSize = 12 });
            var revBookBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = revBookSp };
            string chosenBookId = chosen.Book.Id;
            var chosenBookObj = chosen.Book;
            revBookBtn.Click += async (_, _) => await ShowReviewSheetAsync(chosenBookId);
            rightBtns.Children.Add(revBookBtn);

            var openBookBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "打开书" };
            openBookBtn.Click += (_, _) => TryOpenBook(chosenBookObj);
            rightBtns.Children.Add(openBookBtn);
            Grid.SetColumn(rightBtns, 2);
            head.Children.Add(rightBtns);

            rightStack.Children.Add(head);
            rightStack.Children.Add(BuildFilterChips(chosen.AllMarks));

            var filteredMarks = chosen.MatchedMarks
                .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
                .ToArray();

            if (filteredMarks.Length == 0)
            {
                rightStack.Children.Add(MakeNoMatchingNotesEmptyState());
            }
            else
            {
                foreach (var secGrp in filteredMarks.GroupBy(m => m.Section))
                {
                    var chHead = new Grid { Margin = new Thickness(4, 4, 4, -6) };
                    chHead.Children.Add(new TextBlock
                    {
                        Text = GetChapterTitle(chosen.Book, secGrp.Key),
                        FontSize = 12,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = ThemeHelper.Brush(palette.T2),
                    });
                    chHead.Children.Add(new TextBlock
                    {
                        Text = $"{secGrp.Count()} 条",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(palette.T3),
                        HorizontalAlignment = HorizontalAlignment.Right,
                    });
                    rightStack.Children.Add(chHead);
                    rightStack.Children.Add(WrapNoteCard(secGrp.Select(m => BuildNoteEntry(chosen.Book, m, showBookFooter: false)).ToArray()));
                }
            }

            Grid.SetColumn(rightStack, 1);
            split.Children.Add(rightStack);
            SubPageStack.Children.Add(split);
        }
        L10n.ApplyTree(SubPageStack);
    }

    private UIElement BuildReviewDeckCard(ReviewDeck deck, bool dark)
    {
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var stackLayer = new Grid { Margin = new Thickness(0, 2, 0, 12) };
        stackLayer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(14, 10, 14, -6),
            Opacity = 0.55,
        });
        stackLayer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(15),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(7, 5, 7, -3),
            Opacity = 0.78,
        });

        var inner = new Grid { ColumnSpacing = 16 };
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new TextBlock { Text = "今天回顾", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(p.T1) });
        left.Children.Add(new TextBlock
        {
            Text = deck.Items.Length > 0 ? $"{deck.Items.Length} 条 · 约 2 分钟" : "看完了",
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
        });
        inner.Children.Add(left);

        var div = new Border { Width = 1, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 4, 0, 4) };
        Grid.SetColumn(div, 1);
        inner.Children.Add(div);

        var mid = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        if (deck.Items.Length > 0)
        {
            var first = deck.Items[0];
            var firstBook = _keptBooks.FirstOrDefault(b => b.Id == first.Book);
            mid.Children.Add(L10n.Skip(new TextBlock
            {
                Text = $"“{first.Mark.Text}”",
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 13.5,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            mid.Children.Add(L10n.Skip(new TextBlock
            {
                Text = firstBook?.Title ?? L10n.T("书摘"),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
            }));
        }
        else
        {
            mid.Children.Add(new TextBlock
            {
                Text = $"今天的 {deck.DoneToday} 条看完了。留下的以后还会再出现。",
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(p.T2),
            });
        }
        Grid.SetColumn(mid, 2);
        inner.Children.Add(mid);

        if (deck.Items.Length > 0)
        {
            var rightCluster = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            var distinctBooks = deck.Items
                .Select(it => _keptBooks.FirstOrDefault(b => b.Id == it.Book))
                .Where(b => b is not null)
                .DistinctBy(b => b!.Id)
                .Take(3)
                .ToArray();
            if (distinctBooks.Length > 0)
            {
                var miniStack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                for (int ci = 0; ci < distinctBooks.Length; ci++)
                {
                    var miniCov = CreateCoverView(distinctBooks[ci]!, 18, 26, elevated: false);
                    miniCov.Margin = new Thickness(ci == 0 ? 0 : -6, 0, 0, 0);
                    Canvas.SetZIndex(miniCov, 3 - ci);
                    miniStack.Children.Add(miniCov);
                }
                rightCluster.Children.Add(miniStack);
            }

            var startBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(14, 0, 14, 0),
                Content = "开始",
                VerticalAlignment = VerticalAlignment.Center,
            };
            startBtn.Click += async (_, _) => await ShowReviewSheetAsync(null);
            rightCluster.Children.Add(startBtn);
            Grid.SetColumn(rightCluster, 3);
            inner.Children.Add(rightCluster);
        }

        stackLayer.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = ThemeHelper.Brush(p.Card),
            BorderBrush = ThemeHelper.Brush(p.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(18, 14, 18, 14),
            Child = inner,
        });
        return stackLayer;
    }

    private async Task ShowReviewSheetAsync(string? onlyBookId)
    {
        // 1:1 MacReviewView.swift:30-31: per-book review uses UtcNow & 5u so it always works even after 8 daily cards
        long dayStartMs = onlyBookId is not null
            ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            : new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        ReviewItem[] items;
        PodcastClip[] allPodClips = [];
        try
        {
            (items, allPodClips) = await Task.Run(() =>
            {
                var deck = _library.Core.ReviewToday(onlyBookId, dayStartMs, 5u);
                PodcastClip[] clips = [];
                try { clips = _library.Core.PodcastClips(null); } catch { }
                return (deck.Items, clips);
            });
        }
        catch { return; }
        if (_closed) return;

        if (items.Length == 0)
        {
            ShowToast("今天没有需要回顾的划线", canUndo: false);
            return;
        }

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        int idx = 0;
        var keptMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? nextOnlyBookId = null;
        var host = new StackPanel { Spacing = 14, Padding = new Thickness(22, 18, 22, 20) };
        ContentDialog? dlgRef = null;
        Button? activePrimaryBtn = null;

        void FocusActiveButton()
        {
            var target = activePrimaryBtn;
            if (target is null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed) return;
                target.Focus(FocusState.Programmatic);
            });
        }

        void RenderCard()
        {
            host.Children.Clear();
            if (idx >= items.Length)
            {
                int keptCnt = items.Count(it => !keptMap.TryGetValue(it.Mark.Id, out bool k) || k);
                int droppedCnt = items.Length - keptCnt;

                var sumCard = new StackPanel { Spacing = 10 };
                sumCard.Children.Add(new TextBlock
                {
                    Text = onlyBookId is null ? $"今天的 {items.Length} 条看完了" : $"这本书的 {items.Length} 条看完了",
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 18.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(p.T1),
                });
                sumCard.Children.Add(new TextBlock
                {
                    Text = $"留下 {keptCnt} 条" + (droppedCnt > 0 ? $" · 不要了 {droppedCnt} 条" : ""),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(p.T2),
                });

                var rowsList = new StackPanel { Spacing = 0 };
                for (int ri = 0; ri < items.Length; ri++)
                {
                    if (ri > 0) rowsList.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
                    var rit = items[ri];
                    bool isKept = !keptMap.TryGetValue(rit.Mark.Id, out bool kv) || kv;
                    var rBook = _keptBooks.FirstOrDefault(b => b.Id == rit.Book);

                    var rg = new Grid { ColumnSpacing = 8, Height = 36 };
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    if (rBook is not null)
                    {
                        var cv = CreateCoverView(rBook, 14, 20, elevated: false);
                        cv.Opacity = isKept ? 1.0 : 0.45;
                        rg.Children.Add(cv);
                    }
                    var rText = L10n.Skip(new TextBlock
                    {
                        Text = rit.Mark.Text,
                        FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(isKept ? p.T1 : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    });
                    Grid.SetColumn(rText, 1);
                    rg.Children.Add(rText);

                    var rState = new TextBlock
                    {
                        Text = isKept ? "✓" : "不要了",
                        FontSize = 11,
                        Foreground = ThemeHelper.Brush(isKept ? p.T2 : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    Grid.SetColumn(rState, 2);
                    rg.Children.Add(rState);

                    var flipBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(4, 0, 4, 0),
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        UseSystemFocusVisuals = false,
                        Content = rg,
                    };
                    ReviewItem capturedItem = rit;
                    flipBtn.Click += (_, _) =>
                    {
                        bool nextKeep = !(keptMap.TryGetValue(capturedItem.Mark.Id, out bool curK) ? curK : true);
                        keptMap[capturedItem.Mark.Id] = nextKeep;
                        try { _library.Core.SetMarkReview(capturedItem.Book, capturedItem.Mark.Id, nextKeep); } catch { }
                        RenderCard();
                    };
                    rowsList.Children.Add(flipBtn);
                }
                sumCard.Children.Add(rowsList);
                sumCard.Children.Add(new TextBlock
                {
                    Text = "留下的以后还会再出现。不要了的只是不再回顾，笔记还在；点一条可以改主意。",
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(p.T3),
                    TextWrapping = TextWrapping.Wrap,
                });

                // 只回顾某一本书行，直接置于总结卡片组内部（1:1 MacReviewView.swift:409-427）
                var booksWithMarks = _keptBooks.Where(b => _markCounts.TryGetValue(b.Id, out var c) && c > 0).ToArray();
                if (booksWithMarks.Length > 0)
                {
                    sumCard.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(0, 2, 0, 0) });
                    var onlyMenu = new MenuFlyout();
                    foreach (var bk in booksWithMarks)
                    {
                        string bid = bk.Id;
                        var mi = new MenuFlyoutItem { Text = bk.Title };
                        mi.Click += (_, _) => { nextOnlyBookId = bid; dlgRef?.Hide(); };
                        onlyMenu.Items.Add(mi);
                    }
                    var onlyRow = new Grid { ColumnSpacing = 8, Height = 32 };
                    onlyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    onlyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    onlyRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    onlyRow.Children.Add(YYIcons.Make("book", 12, ThemeHelper.Brush(p.T2)));
                    var onlyLbl = new TextBlock { Text = "只回顾某一本书", FontSize = 12.5, Foreground = ThemeHelper.Brush(p.T1), VerticalAlignment = VerticalAlignment.Center };
                    Grid.SetColumn(onlyLbl, 1);
                    onlyRow.Children.Add(onlyLbl);
                    var onlyChev = YYIcons.Make("chevron-right", 10, ThemeHelper.Brush(p.T3));
                    Grid.SetColumn(onlyChev, 2);
                    onlyRow.Children.Add(onlyChev);

                    var onlyBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent),
                        BorderThickness = new Thickness(0),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(4, 0, 4, 0),
                        UseSystemFocusVisuals = false,
                        Content = onlyRow,
                        Flyout = onlyMenu,
                    };
                    sumCard.Children.Add(onlyBtn);
                }

                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.75),
                    Padding = new Thickness(20, 18, 20, 14),
                    Child = sumCard,
                });

                var doneBtn = YYChrome.MakeSheetPillButton("完成", () => dlgRef?.Hide(), dark, _library.Prefs.Tint, "dark", height: 38, stretch: true);
                host.Children.Add(doneBtn);
                activePrimaryBtn = doneBtn;
                L10n.ApplyTree(host);
                doneBtn.Loaded += (_, _) => doneBtn.Focus(FocusState.Programmatic);
                FocusActiveButton();
                return;
            }

            var cur = items[idx];
            var book = _keptBooks.FirstOrDefault(b => b.Id == cur.Book);
            string revChap = "";
            try
            {
                var det = _library.GetBookDetail(cur.Book);
                if (cur.Mark.Section < det.Sections.Length) revChap = det.Sections[cur.Mark.Section].Title ?? "";
            }
            catch { }

            // 1:1 notes-review/1.png: top bar (left circle X + centered "今天回顾 · 1 / N" + right circle Share), then segmented progress bar
            var topBar = new Grid();
            var closeRevBtn = new Button
            {
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("xmark", 11.5, ThemeHelper.Brush(p.T1)),
            };
            AutomationProperties.SetName(closeRevBtn, L10n.T("关闭"));
            closeRevBtn.Click += (_, _) => dlgRef?.Hide();
            topBar.Children.Add(closeRevBtn);

            var onlyBook = onlyBookId is not null ? _keptBooks.FirstOrDefault(b => b.Id == onlyBookId) : null;
            string revHeadTitle = onlyBookId is null
                ? L10n.T("今天回顾")
                : (onlyBook is not null ? L10n.T($"回顾《{onlyBook.Title}》") : L10n.T("回顾这本书"));
            topBar.Children.Add(L10n.Skip(new TextBlock
            {
                Text = $"{revHeadTitle} · {idx + 1} / {items.Length}",
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = ThemeHelper.Brush(p.T1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 310,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));

            var shareTopBtn = new Button
            {
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                IsTabStop = false,
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("share", 13, ThemeHelper.Brush(p.T1)),
            };
            AutomationProperties.SetName(shareTopBtn, L10n.T("分享书摘"));
            shareTopBtn.Click += async (_, _) =>
            {
                if (RootGrid.XamlRoot is not { } xr) return;
                await Yuyue.Reader.QuoteShareDialog.ShowAsync(
                    this, xr, cur.Mark.Text, cur.Mark.Note,
                    book?.Title ?? "书摘", book?.Authors.FirstOrDefault(), book?.CoverPath,
                    chapter: revChap, library: _library);
            };
            topBar.Children.Add(shareTopBtn);
            host.Children.Add(topBar);

            var progGrid = new Grid { ColumnSpacing = 5 };
            for (int i = 0; i < items.Length; i++)
            {
                progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var seg = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = ThemeHelper.Brush(i < idx ? tint.Fill : p.Inset),
                };
                Grid.SetColumn(seg, i);
                progGrid.Children.Add(seg);
            }
            host.Children.Add(progGrid);

            var cardGrid = new Grid { MinHeight = 310, RowSpacing = 14 };
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var quoteTop = new StackPanel { Spacing = 12, VerticalAlignment = VerticalAlignment.Top };
            quoteTop.Children.Add(L10n.Skip(new TextBlock { Text = "“", FontFamily = new FontFamily("Georgia"), FontSize = 36, Foreground = ThemeHelper.Brush(tint.Accent), Margin = new Thickness(0, -6, 0, -14) }));
            quoteTop.Children.Add(L10n.Skip(new TextBlock
            {
                Text = cur.Mark.Text,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 18.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                LineHeight = 30,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
            }));

            if (!string.IsNullOrWhiteSpace(cur.Mark.Note))
            {
                var th = new Grid { ColumnSpacing = 8 };
                th.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                th.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                th.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                var nt = L10n.Skip(new TextBlock { Text = cur.Mark.Note, FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()), FontSize = 13.5, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap });
                Grid.SetColumn(nt, 1);
                th.Children.Add(nt);
                quoteTop.Children.Add(th);
            }
            cardGrid.Children.Add(quoteTop);

            var footWrap = new StackPanel { Spacing = 12 };
            footWrap.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line) });
            var foot = new Grid { ColumnSpacing = 10 };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (book is not null) foot.Children.Add(CreateCoverView(book, 28, 40, elevated: false));
            var bm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            bm.Children.Add(L10n.Skip(new TextBlock { Text = book?.Title ?? L10n.T("书摘"), FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
            int daysOld = Math.Max(0, (int)(DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(cur.Mark.At)).TotalDays);
            string ageText = daysOld == 0
                ? "今天"
                : daysOld == 1
                    ? "昨天"
                    : daysOld < 30
                        ? $"{daysOld} 天前"
                        : daysOld < 365
                            ? $"{Math.Max(1, daysOld / 30)} 个月前"
                            : $"{Math.Max(1, daysOld / 365)} 年前";
            string subFoot = string.Join(" · ", new[] { book?.Authors.FirstOrDefault(), revChap, L10n.T(ageText) }.Where(s => !string.IsNullOrWhiteSpace(s)));
            bm.Children.Add(L10n.Skip(new TextBlock { Text = subFoot, FontSize = 11, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));
            Grid.SetColumn(bm, 1);
            foot.Children.Add(bm);

            var rightFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            string needle = cur.Mark.Text.Trim();
            string prefix = needle.Length >= 6 ? needle[..Math.Min(18, needle.Length)] : "";
            var matchingClip = prefix.Length >= 6
                ? allPodClips.FirstOrDefault(cl => cl.QuoteText.Contains(prefix, StringComparison.OrdinalIgnoreCase) || needle.Contains(cl.QuoteText, StringComparison.OrdinalIgnoreCase))
                : null;
            string? resolvedClipPath = null;
            if (matchingClip is not null && !string.IsNullOrEmpty(matchingClip.AudioRelPath))
            {
                string cand = Path.IsPathRooted(matchingClip.AudioRelPath)
                    ? matchingClip.AudioRelPath
                    : Path.Combine(_library.Core.PodcastClipsDir(), Path.GetFileName(matchingClip.AudioRelPath));
                if (File.Exists(cand)) resolvedClipPath = cand;
            }
            if (resolvedClipPath is string clipPath)
            {
                bool clipPlayingNow = false;
                var replayBtn = new Button
                {
                    Height = 24,
                    MinHeight = 24,
                    Padding = new Thickness(9, 0, 9, 0),
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(tint.Soft),
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    BorderThickness = new Thickness(0),
                    FontSize = 11,
                    Content = "▶ 重听原声",
                };
                replayBtn.Click += (_, _) =>
                {
                    try
                    {
                        if (clipPlayingNow)
                        {
                            _reviewClipPlayer?.Pause();
                            clipPlayingNow = false;
                            replayBtn.Content = L10n.T("▶ 重听原声");
                            return;
                        }
                        _reviewClipPlayer?.Pause();
                        _reviewClipPlayer?.Dispose();
                        _reviewClipPlayer = new Windows.Media.Playback.MediaPlayer { AutoPlay = true };
                        _reviewClipPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() =>
                        {
                            if (_closed) return;
                            clipPlayingNow = false;
                            replayBtn.Content = L10n.T("▶ 重听原声");
                        });
                        _reviewClipPlayer.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(clipPath));
                        clipPlayingNow = true;
                        replayBtn.Content = L10n.T("⏹ 停止原声");
                    }
                    catch { }
                };
                rightFoot.Children.Add(replayBtn);
            }

            var jumpBtn = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(6, 4, 6, 4),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(p.T2),
                UseSystemFocusVisuals = false,
                Content = "回原文 ›",
            };
            jumpBtn.Click += (_, _) =>
            {
                dlgRef?.Hide();
                TryOpenBook(cur.Book, section: cur.Mark.Section, sentence: (int)cur.Mark.Start.Sentence);
            };
            rightFoot.Children.Add(jumpBtn);
            Grid.SetColumn(rightFoot, 2);
            foot.Children.Add(rightFoot);
            footWrap.Children.Add(foot);
            Grid.SetRow(footWrap, 1);
            cardGrid.Children.Add(footWrap);

            // Stacked deck peek effect (1:1 notes-review/1.png)
            var deckWrap = new Grid();
            if (idx + 2 < items.Length)
            {
                deckWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Opacity = 0.55,
                    Margin = new Thickness(22, 12, 22, -10),
                });
            }
            if (idx + 1 < items.Length)
            {
                deckWrap.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(0.5),
                    Opacity = 0.78,
                    Margin = new Thickness(11, 6, 11, -5),
                });
            }
            var mainCard = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(0.75),
                Padding = new Thickness(22, 20, 22, 18),
                Child = cardGrid,
            };
            deckWrap.Children.Add(mainCard);
            host.Children.Add(deckWrap);
            YYChrome.AnimateEntrance(mainCard, fromY: 5f, durationMs: 140);

            var actGrid = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 6, 0, 0) };
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dropBtn = YYChrome.MakeSheetPillButton("✕  不要了", DoDrop, dark, _library.Prefs.Tint, "secondary", height: 42, stretch: true);
            var keepBtn = YYChrome.MakeSheetPillButton("✓  留下", DoKeep, dark, _library.Prefs.Tint, "warm", height: 42, stretch: true);
            Grid.SetColumn(keepBtn, 1);
            actGrid.Children.Add(dropBtn);
            actGrid.Children.Add(keepBtn);
            host.Children.Add(actGrid);

            activePrimaryBtn = keepBtn;
            L10n.ApplyTree(host);
            keepBtn.Loaded += (_, _) => keepBtn.Focus(FocusState.Programmatic);
            FocusActiveButton();
        }

        void DoDrop()
        {
            if (idx >= items.Length) return;
            var cur = items[idx];
            keptMap[cur.Mark.Id] = false;
            try { _library.Core.SetMarkReview(cur.Book, cur.Mark.Id, false); } catch { }
            idx++;
            RenderCard();
        }

        void DoKeep()
        {
            if (idx >= items.Length) return;
            var cur = items[idx];
            keptMap[cur.Mark.Id] = true;
            try { _library.Core.SetMarkReview(cur.Book, cur.Mark.Id, true); } catch { }
            idx++;
            RenderCard();
        }

        RenderCard();
        if (RootGrid.XamlRoot is null) return;
        dlgRef = YYChrome.CreateMacSheet(RootGrid.XamlRoot, dark, 444, host, tint: _library.Prefs.Tint);
        dlgRef.Opened += (_, _) => FocusActiveButton();
        dlgRef.PreviewKeyDown += (_, ke) =>
        {
            if (RootGrid.XamlRoot is not null)
            {
                var popups = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot);
                if (popups.Any(pp => pp.Child is MenuFlyoutPresenter or FlyoutPresenter)) return;
            }

            if (idx >= items.Length)
            {
                if (ke.Key is VirtualKey.Enter or VirtualKey.Space)
                {
                    ke.Handled = true;
                    dlgRef?.Hide();
                }
                return;
            }

            if (ke.Key is VirtualKey.Right or VirtualKey.Enter or VirtualKey.Space)
            {
                ke.Handled = true;
                DoKeep();
            }
            else if (ke.Key is VirtualKey.Left or VirtualKey.Delete or VirtualKey.Back)
            {
                ke.Handled = true;
                DoDrop();
            }
        };
        await dlgRef.ShowSafeAsync(_library.Prefs.Tint);
        try { _reviewClipPlayer?.Pause(); _reviewClipPlayer?.Dispose(); _reviewClipPlayer = null; } catch { }
        if (_closed) return;
        _cachedReviewDeck = null;
        RenderNotesPage();
        if (nextOnlyBookId is not null)
            await ShowReviewSheetAsync(nextOnlyBookId);
    }

    private VocabWord[] GetFilteredWords()
    {
        string q = _wordsQuery.Trim();
        return _words
            .Where(w => w.Known == _wordsShowKnown)
            .Where(w => string.IsNullOrEmpty(q)
                || w.Word.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(w.Meaning) && w.Meaning.Contains(q, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrEmpty(w.Context) && w.Context.Contains(q, StringComparison.OrdinalIgnoreCase))
                || NameMatcher.Matches(ResolveVocabSourceTitle(w.Book), q))
            .OrderByDescending(w => w.At)
            .ToArray();
    }

    private void RenderWordsPage()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (_words.Count == 0)
        {
            var emptyStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 110, 0, 0) };
            emptyStack.Children.Add(YYIcons.Make("a-wen", 28, ThemeHelper.Brush(palette.T3)));
            emptyStack.Children.Add(new TextBlock { Text = "还没有生词", FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(palette.T1), HorizontalAlignment = HorizontalAlignment.Center });
            emptyStack.Children.Add(new TextBlock { Text = "读外文书时点一个词，在释义卡片里点「加入生词本」，就记在这里，带着原句", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T2), HorizontalAlignment = HorizontalAlignment.Center });
            SubPageStack.Children.Add(emptyStack);
            L10n.ApplyTree(SubPageStack);
            return;
        }

        int learningCount = _words.Count(w => !w.Known);
        int knownCount = _words.Count(w => w.Known);

        // 1:1 MacWordsView.swift:146-165：仅「在学 N」「已掌握 N」两枚筹码，无闪卡复习按钮
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (showKnown, label) in new[] { (false, $"在学 {learningCount}"), (true, $"已掌握 {knownCount}") })
        {
            bool on = _wordsShowKnown == showKnown;
            var b = new Button
            {
                Content = label,
                Height = 26,
                MinHeight = 26,
                Padding = new Thickness(11, 0, 11, 0),
                CornerRadius = new CornerRadius(13),
                BorderThickness = new Thickness(1),
                FontSize = 11.5,
                FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Background = ThemeHelper.Brush(on ? tint.Black : palette.Card),
                Foreground = ThemeHelper.Brush(on ? tint.OnBlack : palette.T2),
                BorderBrush = ThemeHelper.Brush(on ? tint.Edge : palette.Line),
                UseSystemFocusVisuals = false,
            };
            bool sk = showKnown;
            b.Click += (_, _) => { _wordsShowKnown = sk; RenderWordsPage(); };
            chips.Children.Add(b);
        }
        SubPageStack.Children.Add(chips);

        var shown = GetFilteredWords();
        if (shown.Length == 0)
        {
            SubPageStack.Children.Add(new TextBlock
            {
                Text = !string.IsNullOrWhiteSpace(_wordsQuery)
                    ? L10n.T("没有匹配的生词")
                    : (_wordsShowKnown ? "暂无已掌握的词。将在学的词标为「已掌握」后移至此处" : "已全部掌握。新查的词加入生词本后会显示在这里"),
                FontSize = 12,
                Foreground = ThemeHelper.Brush(palette.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 56, 0, 0),
            });
            L10n.ApplyTree(SubPageStack);
            return;
        }

        var speakerButtons = new List<(string Word, Button Btn)>();
        void SyncSpeakerButtons()
        {
            foreach (var (word, btn) in speakerButtons)
            {
                bool active = string.Equals(_speakingVocabWord, word, StringComparison.Ordinal);
                btn.Background = ThemeHelper.Brush(active ? tint.Soft : palette.Inset);
                btn.BorderBrush = active ? ThemeHelper.Brush(tint.Edge) : null;
                btn.BorderThickness = new Thickness(active ? 0.5 : 0);
                btn.Content = YYIcons.Make(active ? "waveform" : "speaker", 12, ThemeHelper.Brush(active ? tint.Accent : palette.T1));
                string tip = L10n.T(active ? "停止朗读" : "读一遍");
                ToolTipService.SetToolTip(btn, tip);
                AutomationProperties.SetName(btn, tip);
            }
        }

        async Task ToggleSpeakWordAsync(string word, string? lang)
        {
            if (string.Equals(_speakingVocabWord, word, StringComparison.Ordinal))
            {
                _vocabSpeakSeq++;
                _speakingVocabWord = null;
                Yuyue.Speech.AudioPlayer.StopActivePreview();
                SyncSpeakerButtons();
                return;
            }

            int seq = ++_vocabSpeakSeq;
            _speakingVocabWord = word;
            Yuyue.Speech.AudioPlayer.StopActivePreview();
            SyncSpeakerButtons();
            try
            {
                await Yuyue.Speech.AudioPlayer.SpeakForeignWordAsync(_library, word, lang);
            }
            catch { }

            if (!_closed && seq == _vocabSpeakSeq && string.Equals(_speakingVocabWord, word, StringComparison.Ordinal))
            {
                _speakingVocabWord = null;
                SyncSpeakerButtons();
            }
        }

        // 全库词频统计（1:1 Vocab.swift:63-68）
        var freqMap = new Dictionary<string, WordCount>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var wc in _library.Core.WordCounts(shown.Select(w => w.Word).Distinct().ToArray()))
                freqMap[wc.Word] = wc;
        }
        catch { }

        var detailCache = new Dictionary<string, BookDetail?>(StringComparer.Ordinal);
        string ResolveWordChapterTitle(VocabWord w)
        {
            if (string.IsNullOrWhiteSpace(w.Book)) return "";
            if (w.Book.StartsWith("podcast:", StringComparison.Ordinal))
            {
                var pod = TryResolvePodcastIssueForWord(w.Book);
                return pod is not null && !string.IsNullOrWhiteSpace(pod.ShowTitle) && pod.ShowTitle != pod.Title
                    ? pod.ShowTitle
                    : "";
            }
            var bk = _keptBooks.FirstOrDefault(b => b.Id == w.Book);
            if (bk is null || bk.Record) return "";
            if (bk.Format == BookFormat.Pdf) return w.Section > 0 ? $"第 {w.Section + 1} 页" : "";
            if (!detailCache.TryGetValue(w.Book, out var det))
            {
                try { det = _library.GetBookDetail(w.Book); } catch { det = null; }
                detailCache[w.Book] = det;
            }
            if (det is null) return "";
            var tocMatch = det.Toc.FirstOrDefault(t => t.Section == w.Section && !string.IsNullOrWhiteSpace(t.Title));
            if (tocMatch is not null) return tocMatch.Title;
            if (w.Section < det.Sections.Length && !string.IsNullOrWhiteSpace(det.Sections[w.Section].Title))
                return det.Sections[w.Section].Title!;
            return "";
        }

        string BookTitleOf(string id) => ResolveVocabSourceTitle(id, "已删除的书");

        var groups = _wordsByBook
            ? shown.GroupBy(w => w.Book).Select(g => (Title: BookTitleOf(g.Key), Sub: (string?)null, Words: g.ToArray())).ToArray()
            : shown.GroupBy(w => DateTimeOffset.FromUnixTimeMilliseconds(w.At).LocalDateTime.Date)
                .Select(g => (Title: FormatDayName(g.Key), Sub: (string?)$"{g.Key.Month} 月 {g.Key.Day} 日", Words: g.ToArray())).ToArray();

        foreach (var grp in groups)
        {
            var row = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 4, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var side = new StackPanel { Spacing = 2, Margin = new Thickness(0, 14, 0, 0) };
            side.Children.Add(new TextBlock { Text = grp.Title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.T1), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
            if (grp.Sub is not null) side.Children.Add(new TextBlock { Text = grp.Sub, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
            side.Children.Add(new TextBlock { Text = $"{grp.Words.Length} 个", FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
            row.Children.Add(side);

            var cardStack = new StackPanel { Spacing = 0 };
            for (int i = 0; i < grp.Words.Length; i++)
            {
                if (i > 0) cardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(palette.Line) });
                var w = grp.Words[i];
                var item = new StackPanel { Spacing = 5, Padding = new Thickness(0, 14, 0, 14), Background = new SolidColorBrush(Colors.Transparent) };

                var head = new Grid();
                var wordLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
                wordLine.Children.Add(L10n.Skip(new TextBlock
                {
                    Text = w.Word,
                    FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(palette.T1),
                }));
                if (!string.IsNullOrWhiteSpace(w.Phonetic))
                    wordLine.Children.Add(L10n.Skip(new TextBlock { Text = $"/{w.Phonetic.Trim('/', '[', ']')}/", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T2), VerticalAlignment = VerticalAlignment.Center }));
                head.Children.Add(wordLine);

                bool isSpeakingThis = string.Equals(_speakingVocabWord, w.Word, StringComparison.Ordinal);
                var spkBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(isSpeakingThis ? tint.Soft : palette.Inset),
                    BorderBrush = isSpeakingThis ? ThemeHelper.Brush(tint.Edge) : null,
                    BorderThickness = new Thickness(isSpeakingThis ? 0.5 : 0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    UseSystemFocusVisuals = false,
                    Content = YYIcons.Make(isSpeakingThis ? "waveform" : "speaker", 12, ThemeHelper.Brush(isSpeakingThis ? tint.Accent : palette.T1)),
                };
                string spkTip = L10n.T(isSpeakingThis ? "停止朗读" : "读一遍");
                ToolTipService.SetToolTip(spkBtn, spkTip);
                AutomationProperties.SetName(spkBtn, spkTip);
                string speakText = w.Word;
                string? wordLang = ResolveVocabLanguage(w.Book);
                speakerButtons.Add((speakText, spkBtn));
                spkBtn.Click += (_, _) => _ = ToggleSpeakWordAsync(speakText, wordLang);
                head.Children.Add(spkBtn);
                item.Children.Add(head);

                // POS 斜体词性拆分（1:1 MacWordsView.swift:239-243）
                if (!string.IsNullOrEmpty(w.Meaning))
                {
                    var meanTb = L10n.Skip(new TextBlock { FontSize = 13, Foreground = ThemeHelper.Brush(palette.T1), TextWrapping = TextWrapping.Wrap });
                    int dotIdx = w.Meaning.IndexOf(". ", StringComparison.Ordinal);
                    if (dotIdx is > 0 and <= 6)
                    {
                        meanTb.Inlines.Add(new Run
                        {
                            Text = w.Meaning[..(dotIdx + 2)],
                            FontFamily = new FontFamily("Georgia"),
                            FontStyle = Windows.UI.Text.FontStyle.Italic,
                            Foreground = ThemeHelper.Brush(palette.T3),
                        });
                        meanTb.Inlines.Add(new Run { Text = w.Meaning[(dotIdx + 2)..] });
                    }
                    else
                    {
                        meanTb.Text = w.Meaning;
                    }
                    item.Children.Add(meanTb);
                }

                // 原句目标词下划线高亮 + 长句围绕目标词智能截断（1:1 Vocab.swift:29-52）
                if (!string.IsNullOrEmpty(w.Context))
                {
                    var ctxTb = new TextBlock
                    {
                        FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                        FontSize = 13.5,
                        LineHeight = 21,
                        Foreground = ThemeHelper.Brush(palette.T2),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 2, 0, 0),
                    };

                    string rawCtx = w.Context.Replace('\r', ' ').Replace('\n', ' ').Trim();
                    int FindTargetPos(string text)
                    {
                        int p0 = text.IndexOf(w.Word, StringComparison.OrdinalIgnoreCase);
                        if (p0 < 0 && w.Word.Length >= 4)
                        {
                            string stem = w.Word.EndsWith('e') || w.Word.EndsWith('y') ? w.Word[..^1] : w.Word;
                            p0 = text.IndexOf(stem, StringComparison.OrdinalIgnoreCase);
                            if (p0 > 0 && char.IsLetter(text[p0 - 1])) p0 = -1;
                        }
                        return p0;
                    }

                    string ctxText = rawCtx;
                    int rawHit = FindTargetPos(rawCtx);
                    if (rawCtx.Length > 120 && rawHit >= 0)
                    {
                        int from = Math.Max(0, rawHit - 40);
                        if (from > 0)
                        {
                            int sp = rawCtx.IndexOf(' ', from);
                            if (sp >= 0 && sp < rawHit) from = sp + 1;
                        }
                        int to = Math.Min(rawCtx.Length, from + 120);
                        if (to < rawCtx.Length)
                        {
                            int sp = rawCtx.LastIndexOf(' ', to - 1, Math.Max(0, to - Math.Max(from, rawHit + w.Word.Length)));
                            if (sp > rawHit) to = sp;
                        }
                        ctxText = (from > 0 ? "…" : "") + rawCtx[from..to].Trim() + (to < rawCtx.Length ? "…" : "");
                    }

                    int hitPos = FindTargetPos(ctxText);
                    if (hitPos >= 0)
                    {
                        int endPos = hitPos + Math.Min(w.Word.Length, ctxText.Length - hitPos);
                        while (endPos < ctxText.Length && char.IsLetter(ctxText[endPos])) endPos++;
                        if (hitPos > 0) ctxTb.Inlines.Add(new Run { Text = ctxText[..hitPos] });
                        var ul = new Underline();
                        ul.Inlines.Add(new Run { Text = ctxText[hitPos..endPos], Foreground = ThemeHelper.Brush(palette.T1), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        ctxTb.Inlines.Add(ul);
                        if (endPos < ctxText.Length) ctxTb.Inlines.Add(new Run { Text = ctxText[endPos..] });
                    }
                    else
                    {
                        ctxTb.Text = ctxText;
                    }
                    item.Children.Add(L10n.Skip(ctxTb));
                }

                var foot = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                var srcLeft = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                string chapTitle = ResolveWordChapterTitle(w);
                string bookTitle = BookTitleOf(w.Book);
                string srcLine = _wordsByBook
                    ? (chapTitle != bookTitle ? chapTitle : "")
                    : (!string.IsNullOrWhiteSpace(chapTitle) && chapTitle != bookTitle ? $"{bookTitle} · {chapTitle}" : bookTitle);
                if (!string.IsNullOrWhiteSpace(srcLine))
                    srcLeft.Children.Add(L10n.Skip(new TextBlock { Text = srcLine, FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(palette.T2), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis }));

                int wordTokenCount = w.Word.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
                if (wordTokenCount is > 0 and <= 1 && freqMap.TryGetValue(w.Word, out var wc) && wc.Times > 0)
                {
                    string freqLine = wc.Books > 1
                        ? $"在 {wc.Books} 本书里出现 {wc.Times} 次"
                        : (wc.Times == 1 ? "只出现过这一次" : $"在这本书里出现 {wc.Times} 次");
                    srcLeft.Children.Add(new TextBlock { Text = freqLine, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3) });
                }
                foot.Children.Add(srcLeft);

                var knownBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(10, 0, 10, 0),
                    Height = 24,
                    MinHeight = 24,
                    CornerRadius = new CornerRadius(12),
                    Background = ThemeHelper.Brush(palette.Inset),
                    BorderThickness = new Thickness(0),
                    FontSize = 11.5,
                    Foreground = ThemeHelper.Brush(palette.T1),
                    UseSystemFocusVisuals = false,
                    Content = w.Known ? "↩ 再学一遍" : "✓ 已掌握",
                };
                string wordKey = w.Word;
                bool curKnown = w.Known;
                knownBtn.Click += (_, _) => { _library.SetWordKnown(wordKey, !curKnown); RefreshBooks(); };
                foot.Children.Add(knownBtn);
                item.Children.Add(foot);

                // 右键菜单：回到书里/播客这一句 / 读一遍 / 拷贝 / 删除（带撤销）
                var podIssue = TryResolvePodcastIssueForWord(w.Book);
                bool isPodWord = podIssue is not null || w.Book.StartsWith("podcast:", StringComparison.Ordinal);
                Action jumpAction = podIssue is not null
                    ? () => OpenPodcastInline(podIssue.Id, w.Sentence > 0 ? (double?)w.Sentence : null)
                    : (isPodWord
                        ? () => ShowToast("未找到对应的播客单集", canUndo: false)
                        : () => TryOpenBook(w.Book, section: w.Section, sentence: (int)w.Sentence));

                var wCtx = new MenuFlyout();
                var wJump = new MenuFlyoutItem { Text = L10n.T(podIssue is not null ? "回到播客这一句" : "回到书里这一句") };
                wJump.Click += (_, _) => jumpAction();
                var wSpeak = new MenuFlyoutItem { Text = L10n.T("读一遍") };
                wSpeak.Click += (_, _) => _ = ToggleSpeakWordAsync(speakText, wordLang);
                var wCopy = new MenuFlyoutItem { Text = L10n.T("拷贝") };
                wCopy.Click += (_, _) =>
                {
                    var dp = new DataPackage();
                    dp.SetText($"{w.Word}  {w.Meaning}".Trim());
                    Clipboard.SetContent(dp);
                    ShowToast("已拷贝生词", canUndo: false);
                };
                var wDel = new MenuFlyoutItem { Text = L10n.T("删除"), Foreground = ThemeHelper.Brush(palette.Danger) };
                VocabWord savedWord = w;
                wDel.Click += (_, _) =>
                {
                    _library.DeleteWord(savedWord.Word);
                    RefreshBooks();
                    ShowToast($"已删除生词“{savedWord.Word}”", canUndo: true, isDelete: true, undo: () =>
                    {
                        try
                        {
                            _library.Core.RestoreWord(savedWord.Word);
                        }
                        catch { }
                        RefreshBooks();
                    });
                };
                wCtx.Items.Add(wJump);
                wCtx.Items.Add(wSpeak);
                wCtx.Items.Add(wCopy);
                wCtx.Items.Add(new MenuFlyoutSeparator());
                wCtx.Items.Add(wDel);
                item.ContextFlyout = wCtx;

                item.DoubleTapped += (_, _) => jumpAction();
                cardStack.Children.Add(item);
            }

            var card = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 2, 20, 2),
                Child = cardStack,
            };
            Grid.SetColumn(card, 1);
            row.Children.Add(card);
            SubPageStack.Children.Add(row);
        }
        L10n.ApplyTree(SubPageStack);
    }

    internal readonly record struct CollectionClothTone(
        Color WellTop, Color WellBottom, Color WellBorder, Color Ink,
        Color FolioFront, Color FolioBack, Color FolioBorder, Color FolioLine);

    internal static CollectionClothTone ResolveCollectionClothTone(string id, string? iconKey, bool dark, bool eink)
    {
        CollectionClothTone Make(Color lt, Color lb, Color dt, Color db, Color li, Color di)
        {
            var ink = dark ? di : li;
            return new CollectionClothTone(
                dark ? dt : lt,
                dark ? db : lb,
                dark ? Color.FromArgb(15, 255, 255, 255) : Color.FromArgb(26, li.R, li.G, li.B),
                ink,
                dark ? Color.FromArgb(255, 46, 45, 42) : Color.FromArgb(255, 253, 252, 249),
                dark ? Color.FromArgb(10, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255),
                dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(38, li.R, li.G, li.B),
                dark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(26, li.R, li.G, li.B));
        }

        if (eink)
        {
            return Make(
                Color.FromArgb(255, 238, 237, 233), Color.FromArgb(255, 232, 231, 226),
                Color.FromArgb(255, 34, 34, 34), Color.FromArgb(255, 30, 30, 30),
                Color.FromArgb(255, 60, 60, 60), Color.FromArgb(255, 200, 200, 200));
        }

        int kind;
        if (!string.IsNullOrWhiteSpace(iconKey))
        {
            kind = iconKey switch
            {
                "globe" or "eye" => 1,
                "sparkle" or "clock" => 2,
                "moon" or "key" => 3,
                "heart" or "quote.opening" or "sun.max" => 4,
                "headphones" or "cup.and.saucer" => 0,
                _ => 5,
            };
        }
        else
        {
            ulong h = 5381;
            foreach (byte b in System.Text.Encoding.UTF8.GetBytes(id ?? ""))
                h = ((h << 5) + h) + b;
            kind = (int)(h % 6);
        }

        return kind switch
        {
            1 => Make(Color.FromArgb(255, 237, 239, 231), Color.FromArgb(255, 230, 233, 222), Color.FromArgb(255, 32, 36, 32), Color.FromArgb(255, 28, 32, 28), Color.FromArgb(255, 78, 98, 76), Color.FromArgb(255, 162, 184, 158)), // 芦苇青
            2 => Make(Color.FromArgb(255, 237, 239, 241), Color.FromArgb(255, 229, 232, 236), Color.FromArgb(255, 32, 35, 39), Color.FromArgb(255, 28, 31, 35), Color.FromArgb(255, 74, 92, 110), Color.FromArgb(255, 158, 178, 198)), // 苍烟灰
            3 => Make(Color.FromArgb(255, 239, 237, 239), Color.FromArgb(255, 232, 229, 233), Color.FromArgb(255, 35, 34, 39), Color.FromArgb(255, 30, 29, 34), Color.FromArgb(255, 90, 84, 106), Color.FromArgb(255, 174, 168, 192)), // 松烟黛
            4 => Make(Color.FromArgb(255, 243, 237, 234), Color.FromArgb(255, 237, 229, 225), Color.FromArgb(255, 39, 33, 33), Color.FromArgb(255, 34, 29, 29), Color.FromArgb(255, 130, 88, 84), Color.FromArgb(255, 202, 162, 158)), // 赭茶红
            5 => Make(Color.FromArgb(255, 241, 238, 232), Color.FromArgb(255, 234, 230, 222), Color.FromArgb(255, 35, 34, 32), Color.FromArgb(255, 31, 30, 28), Color.FromArgb(255, 96, 90, 80), Color.FromArgb(255, 182, 176, 166)), // 素石白
            _ => Make(Color.FromArgb(255, 243, 239, 231), Color.FromArgb(255, 237, 231, 220), Color.FromArgb(255, 37, 34, 30), Color.FromArgb(255, 32, 29, 26), Color.FromArgb(255, 116, 92, 66), Color.FromArgb(255, 198, 174, 146)), // 暖宣沙
        };
    }

    internal static (bool IsSymbol, string Value, double FontSize) ResolveExLibrisEmblem(string title, string? explicitIcon)
    {
        if (!string.IsNullOrWhiteSpace(explicitIcon)) return (true, explicitIcon, 15.5);
        string trimmed = (title ?? "").Trim();
        if (trimmed.Length == 0) return (true, "books", 15.5);
        if (trimmed.All(c => c <= 127))
        {
            if (trimmed.All(char.IsDigit))
            {
                if (trimmed.Length <= 2) return (false, trimmed, 15);
                if (trimmed.Length <= 4) return (false, trimmed, 12);
                return (true, "books", 15.5);
            }
            if (trimmed.Length <= 3) return (false, trimmed.ToUpperInvariant(), trimmed.Length == 1 ? 16 : 13.5);
            char firstLetter = trimmed.FirstOrDefault(char.IsLetter);
            return firstLetter != '\0' ? (false, char.ToUpperInvariant(firstLetter).ToString(), 16) : (true, "books", 15.5);
        }
        return (false, trimmed[..1], 16.5);
    }

    private UIElement BuildExLibrisWell(string title, string? explicitIcon, CollectionClothTone tone, bool dark, bool eink)
    {
        var g = new Grid();
        Border MakeBackFolio(double tx) => new()
        {
            Width = 48,
            Height = 72,
            CornerRadius = new CornerRadius(4),
            Background = ThemeHelper.Brush(tone.FolioBack),
            BorderBrush = ThemeHelper.Brush(tone.FolioLine),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(tx * 2, 0, 0, -15),
        };
        g.Children.Add(MakeBackFolio(-34));
        g.Children.Add(MakeBackFolio(34));

        var innerFrame = new Grid();
        innerFrame.Children.Add(new Border
        {
            Margin = new Thickness(4.5),
            CornerRadius = new CornerRadius(2.5),
            BorderBrush = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)34 : (byte)24, tone.Ink.R, tone.Ink.G, tone.Ink.B)),
            BorderThickness = new Thickness(0.5),
        });

        var center = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6) };
        var emb = ResolveExLibrisEmblem(title, explicitIcon);
        var inkBrush = ThemeHelper.Brush(Color.FromArgb(200, tone.Ink.R, tone.Ink.G, tone.Ink.B));
        if (emb.IsSymbol)
        {
            center.Children.Add(YYIcons.Make(YYIcons.CollectionIconName(title, emb.Value), 15.5, inkBrush));
        }
        else
        {
            center.Children.Add(L10n.Skip(new TextBlock
            {
                Text = emb.Value,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = emb.FontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = inkBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
            }));
        }
        var rules = new StackPanel { Spacing = 2.5, HorizontalAlignment = HorizontalAlignment.Center };
        rules.Children.Add(new Border { Width = 18, Height = 1, CornerRadius = new CornerRadius(0.5), Background = ThemeHelper.Brush(Color.FromArgb(56, tone.Ink.R, tone.Ink.G, tone.Ink.B)) });
        rules.Children.Add(new Border { Width = 11, Height = 1, CornerRadius = new CornerRadius(0.5), Background = ThemeHelper.Brush(Color.FromArgb(36, tone.Ink.R, tone.Ink.G, tone.Ink.B)) });
        center.Children.Add(rules);
        innerFrame.Children.Add(center);

        var front = new Border
        {
            Width = 58,
            Height = 86,
            CornerRadius = new CornerRadius(4.5),
            Background = ThemeHelper.Brush(tone.FolioFront),
            BorderBrush = ThemeHelper.Brush(tone.FolioBorder),
            BorderThickness = new Thickness(0.5),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, -8),
            Child = innerFrame,
        };
        if (!eink && !dark) ThemeHelper.ApplyElevation(front, PaperInnerGrid, 6f);
        g.Children.Add(front);
        return g;
    }

    private UIElement BuildCollectionCoverStage(IReadOnlyList<BookSummary> books, CollectionClothTone tone, bool eink)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = books.Count >= 3 ? 7 : 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };

        if (books.Count >= 3)
        {
            var left = CreateCoverView(books[1], 52, 78, elevated: false);
            left.Margin = new Thickness(0, 0, 0, -12);
            var mid = CreateCoverView(books[0], 58, 87, elevated: !eink);
            mid.Margin = new Thickness(0, 0, 0, -6);
            var right = CreateCoverView(books[2], 52, 78, elevated: false);
            right.Margin = new Thickness(0, 0, 0, -12);
            row.Children.Add(left);
            row.Children.Add(mid);
            row.Children.Add(right);
        }
        else if (books.Count == 2)
        {
            var first = CreateCoverView(books[0], 56, 84, elevated: !eink);
            first.Margin = new Thickness(0, 0, 0, -6);
            var second = CreateCoverView(books[1], 54, 81, elevated: false);
            second.Margin = new Thickness(0, 0, 0, -11);
            row.Children.Add(first);
            row.Children.Add(second);
        }
        else if (books.Count == 1)
        {
            var only = CreateCoverView(books[0], 58, 87, elevated: !eink);
            only.Margin = new Thickness(0, 0, 0, -6);
            var ghost = new Border
            {
                Width = 52,
                Height = 78,
                CornerRadius = new CornerRadius(4),
                Background = ThemeHelper.Brush(tone.FolioBack),
                BorderBrush = ThemeHelper.Brush(tone.FolioLine),
                BorderThickness = new Thickness(0.5),
                Margin = new Thickness(0, 0, 0, -13),
            };
            row.Children.Add(only);
            row.Children.Add(ghost);
        }
        return row;
    }

    private void RenderCollectionsWall()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        bool eink = _library.Prefs.GlobalEinkMode;
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (_collections.Count == 0)
        {
            var sand = ResolveCollectionClothTone("empty", "books", dark, eink);
            var emptyPreview = new Border
            {
                Width = 176,
                Height = 92,
                CornerRadius = new CornerRadius(10),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0.5, 0),
                    EndPoint = new Windows.Foundation.Point(0.5, 1),
                    GradientStops = { new GradientStop { Color = sand.WellTop, Offset = 0 }, new GradientStop { Color = sand.WellBottom, Offset = 1 } },
                },
                BorderBrush = ThemeHelper.Brush(sand.WellBorder),
                BorderThickness = new Thickness(0.5),
                Child = BuildExLibrisWell("藏", "books", sand, dark, eink),
            };

            var topBox = new StackPanel { Spacing = 12, Padding = new Thickness(26, 22, 26, 18), HorizontalAlignment = HorizontalAlignment.Center };
            topBox.Children.Add(emptyPreview);
            topBox.Children.Add(new TextBlock { Text = "还没有合集", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 17, Foreground = ThemeHelper.Brush(palette.T1), HorizontalAlignment = HorizontalAlignment.Center });
            topBox.Children.Add(new TextBlock { Text = "按主题、心境或阅读计划建几个书匣，把书放进去慢慢读。", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T3), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });

            var createBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "+ 新建合集", HorizontalAlignment = HorizontalAlignment.Center };
            createBtn.Click += NewCollection_Click;

            var cardBody = new StackPanel();
            cardBody.Children.Add(topBox);
            cardBody.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(palette.Line) });
            cardBody.Children.Add(new Border { Padding = new Thickness(0, 12, 0, 12), Background = ThemeHelper.Brush(palette.Inset), Child = createBtn });

            SubPageStack.Children.Add(new Border
            {
                MaxWidth = 360,
                Margin = new Thickness(0, 72, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = new CornerRadius(18),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(1),
                Child = cardBody,
            });
            L10n.ApplyTree(SubPageStack);
            return;
        }

        var pinSet = _library.Prefs.PinnedCollectionIds.ToHashSet(StringComparer.Ordinal);
        int pinnedCount = _collections.Count(c => pinSet.Contains(c.Id));
        if (pinnedCount == 0 && _collectionScope == "pinned") _collectionScope = "all";

        // Scope tabs (全部 / 固定在侧栏) + Sort menu (1:1 MacCollections.swift:228-284)
        var topRow = new Grid { Margin = new Thickness(0, 2, 0, 10) };
        var scopeSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Button MakeScopeTab(string key, string label, int count)
        {
            bool on = _collectionScope == key;
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = label, FontSize = 12.5, FontWeight = on ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(palette.T1) });
            sp.Children.Add(new TextBlock { Text = count.ToString(), FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center });
            var b = new Button
            {
                Height = 28,
                MinHeight = 28,
                Padding = new Thickness(10, 0, 10, 0),
                CornerRadius = new CornerRadius(8),
                Background = on ? ThemeHelper.Brush(dark ? Color.FromArgb(20, 255, 255, 255) : Color.FromArgb(18, 42, 43, 46)) : new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = sp,
            };
            b.Click += (_, _) => { _collectionScope = key; RenderCollectionsWall(); };
            return b;
        }
        scopeSp.Children.Add(MakeScopeTab("all", "全部", _collections.Count));
        if (pinnedCount > 0)
            scopeSp.Children.Add(MakeScopeTab("pinned", "固定在侧栏", pinnedCount));
        topRow.Children.Add(scopeSp);

        string sortName = _collectionSort switch
        {
            "name" => "名字",
            "count" => "书的多少",
            "own" => "自己排的",
            _ => "最近用过",
        };
        var sortFlyout = new MenuFlyout();
        foreach (var (k, lbl) in new[] { ("recent", "最近用过"), ("name", "名字"), ("count", "书的多少"), ("own", "自己排的") })
        {
            var mi = new MenuFlyoutItem { Text = (_collectionSort == k ? "✓ " : "") + L10n.T(lbl) };
            string key = k;
            mi.Click += (_, _) =>
            {
                _collectionSort = key;
                _library.Prefs.CollectionSort = key;
                _library.SavePrefs();
                RenderCollectionsWall();
            };
            sortFlyout.Items.Add(mi);
        }
        var sortSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        sortSp.Children.Add(new TextBlock { Text = sortName, FontSize = 11.5, Foreground = ThemeHelper.Brush(palette.T2) });
        sortSp.Children.Add(YYIcons.Make("updown", 10, ThemeHelper.Brush(palette.T3)));
        var sortBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(8),
            UseSystemFocusVisuals = false,
            Content = sortSp,
            Flyout = sortFlyout,
        };
        AutomationProperties.SetName(sortBtn, L10n.T(sortName));
        topRow.Children.Add(sortBtn);
        SubPageStack.Children.Add(topRow);

        IEnumerable<BookCollection> filtered = _collections;
        if (_collectionScope == "pinned") filtered = filtered.Where(c => pinSet.Contains(c.Id));
        string trimmedQuery = _collectionQuery.Trim();
        if (!string.IsNullOrEmpty(trimmedQuery)) filtered = filtered.Where(c => NameMatcher.Matches(c.Name, trimmedQuery));

        var ordered = (_collectionSort switch
        {
            "name" => filtered.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "count" => filtered.OrderByDescending(c => c.Count).ThenByDescending(c => c.UsedAt),
            "own" => filtered,
            _ => filtered.OrderByDescending(c => c.UsedAt).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
        }).ToArray();

        if (ordered.Length == 0)
        {
            var noneSp = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 72, 0, 0) };
            noneSp.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(trimmedQuery) ? "还没有固定到侧栏的合集" : $"没有找到“{trimmedQuery}”相关的合集",
                FontSize = 12.5,
                Foreground = ThemeHelper.Brush(palette.T2),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(trimmedQuery))
            {
                var quickNewBtn = new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = $"新建合集“{trimmedQuery}”",
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                quickNewBtn.Click += (_, _) =>
                {
                    try { _library.CreateCollection(trimmedQuery); } catch { }
                    _collectionQuery = "";
                    RefreshBooks();
                };
                noneSp.Children.Add(quickNewBtn);
            }
            SubPageStack.Children.Add(noneSp);
            L10n.ApplyTree(SubPageStack);
            return;
        }

        // Pre-group up to 3 books per collection (preferring books with cover art, then recently opened/added)
        var booksByCol = new Dictionary<string, List<BookSummary>>(StringComparer.Ordinal);
        foreach (var b in _keptBooks.OrderByDescending(x => !string.IsNullOrEmpty(x.CoverPath) && File.Exists(x.CoverPath) ? 1 : 0)
                                    .ThenByDescending(x => Math.Max(x.OpenedAt ?? 0, x.AddedAt)))
        {
            foreach (string cid in b.Collections)
            {
                if (!booksByCol.TryGetValue(cid, out var list))
                {
                    list = new List<BookSummary>(3);
                    booksByCol[cid] = list;
                }
                if (list.Count < 3) list.Add(b);
            }
        }

        double availW = PaperSheet.ActualWidth > 0 ? PaperSheet.ActualWidth - 48 : 760;
        int cols = Math.Clamp((int)Math.Floor((availW + 16) / 234.0), 2, 4);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 16 };
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (ordered.Length + cols - 1) / cols;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int i = 0; i < ordered.Length; i++)
        {
            var col = ordered[i];
            bool isPinned = pinSet.Contains(col.Id);
            booksByCol.TryGetValue(col.Id, out var colBooks);
            colBooks ??= [];
            string resolvedIcon = YYIcons.CollectionIconName(col.Name, col.Icon);
            var tone = ResolveCollectionClothTone(col.Id, resolvedIcon, dark, eink);

            var ctx = new MenuFlyout();
            var pinMi = new MenuFlyoutItem { Text = L10n.T(isPinned ? "取消固定" : "固定到侧栏") };
            pinMi.Click += (_, _) =>
            {
                if (isPinned) _library.Prefs.PinnedCollectionIds.Remove(col.Id);
                else if (!_library.Prefs.PinnedCollectionIds.Contains(col.Id)) _library.Prefs.PinnedCollectionIds.Add(col.Id);
                _library.SavePrefs();
                RefreshBooks();
            };
            var renMi = new MenuFlyoutItem { Text = L10n.T("重命名…") };
            renMi.Click += async (_, _) => await RenameCollectionAsync(col);
            var iconSub = BuildCollectionIconSubMenu(col);
            var delMi = new MenuFlyoutItem { Text = L10n.T("删除合集…"), Foreground = ThemeHelper.Brush(palette.Danger) };
            delMi.Click += async (_, _) => await ConfirmDeleteCollectionAsync(col);
            ctx.Items.Add(pinMi);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(renMi);
            ctx.Items.Add(iconSub);
            if (_collectionSort == "own")
            {
                int curIdx = i;
                var moveUp = new MenuFlyoutItem { Text = L10n.T("往前挪"), IsEnabled = curIdx > 0 };
                moveUp.Click += (_, _) =>
                {
                    var ids = _collections.Select(x => x.Id).ToList();
                    int pos = ids.IndexOf(col.Id);
                    if (pos > 0)
                    {
                        (ids[pos - 1], ids[pos]) = (ids[pos], ids[pos - 1]);
                        try { _library.Core.ReorderCollections(ids.ToArray()); } catch { }
                        RefreshBooks();
                    }
                };
                var moveDown = new MenuFlyoutItem { Text = L10n.T("往后挪"), IsEnabled = curIdx + 1 < ordered.Length };
                moveDown.Click += (_, _) =>
                {
                    var ids = _collections.Select(x => x.Id).ToList();
                    int pos = ids.IndexOf(col.Id);
                    if (pos >= 0 && pos + 1 < ids.Count)
                    {
                        (ids[pos + 1], ids[pos]) = (ids[pos], ids[pos + 1]);
                        try { _library.Core.ReorderCollections(ids.ToArray()); } catch { }
                        RefreshBooks();
                    }
                };
                ctx.Items.Add(moveUp);
                ctx.Items.Add(moveDown);
            }
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delMi);

            // Recessed mineral bookcloth pocket (1:1 MacCollections.swift:376-428)
            var wellGrid = new Grid();
            wellGrid.Children.Add(colBooks.Count == 0
                ? BuildExLibrisWell(col.Name, col.Icon, tone, dark, eink)
                : BuildCollectionCoverStage(colBooks, tone, eink));

            // Subtle bottom pocket lip shadow
            if (!eink)
            {
                wellGrid.Children.Add(new Border
                {
                    Height = 14,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    IsHitTestVisible = false,
                    Background = new LinearGradientBrush
                    {
                        StartPoint = new Windows.Foundation.Point(0.5, 0),
                        EndPoint = new Windows.Foundation.Point(0.5, 1),
                        GradientStops =
                        {
                            new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 },
                            new GradientStop { Color = Color.FromArgb(dark ? (byte)56 : (byte)12, 0, 0, 0), Offset = 1 },
                        },
                    },
                });
            }

            // Top badges inside the pocket (left icon badge if populated, right pin permanently visible when pinned & manage pill on hover)
            var topControls = new Grid { Padding = new Thickness(8), VerticalAlignment = VerticalAlignment.Top };
            if (colBooks.Count > 0)
            {
                topControls.Children.Add(new Border
                {
                    Width = 22,
                    Height = 22,
                    CornerRadius = new CornerRadius(11),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)176 : (byte)204, palette.Card.R, palette.Card.G, palette.Card.B)),
                    BorderBrush = ThemeHelper.Brush(tone.WellBorder),
                    BorderThickness = new Thickness(0.5),
                    Child = YYIcons.Make(resolvedIcon, 10.5, ThemeHelper.Brush(tone.Ink)),
                });
            }

            var rightPills = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            string pinTargetId = col.Id;
            bool curPinned = isPinned;
            var pinPill = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)216 : (byte)235, palette.Card.R, palette.Card.G, palette.Card.B)),
                BorderBrush = ThemeHelper.Brush(tone.WellBorder),
                BorderThickness = new Thickness(0.5),
                UseSystemFocusVisuals = false,
                Opacity = curPinned ? 1 : 0,
                Content = YYIcons.Make("pin", 10, ThemeHelper.Brush(curPinned ? tint.Accent : palette.T3), filled: curPinned),
            };
            ToolTipService.SetToolTip(pinPill, curPinned ? "取消固定" : "固定到侧栏");
            AutomationProperties.SetName(pinPill, L10n.T(curPinned ? "取消固定" : "固定到侧栏"));
            pinPill.Click += (_, _) =>
            {
                if (curPinned) _library.Prefs.PinnedCollectionIds.Remove(pinTargetId);
                else if (!_library.Prefs.PinnedCollectionIds.Contains(pinTargetId)) _library.Prefs.PinnedCollectionIds.Add(pinTargetId);
                _library.SavePrefs();
                RefreshBooks();
            };
            var morePill = new Button
            {
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)216 : (byte)235, palette.Card.R, palette.Card.G, palette.Card.B)),
                BorderBrush = ThemeHelper.Brush(tone.WellBorder),
                BorderThickness = new Thickness(0.5),
                UseSystemFocusVisuals = false,
                Opacity = 0,
                Content = L10n.Skip(new TextBlock { Text = "···", FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.T2) }),
                Flyout = ctx,
            };
            ToolTipService.SetToolTip(morePill, $"管理合集“{col.Name}”");
            AutomationProperties.SetName(morePill, L10n.T($"管理合集“{col.Name}”"));
            rightPills.Children.Add(pinPill);
            rightPills.Children.Add(morePill);
            topControls.Children.Add(rightPills);

            var wellBorder = new Border
            {
                Height = 114,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(6, 6, 6, 0),
                Background = new LinearGradientBrush
                {
                    StartPoint = new Windows.Foundation.Point(0.5, 0),
                    EndPoint = new Windows.Foundation.Point(0.5, 1),
                    GradientStops = { new GradientStop { Color = tone.WellTop, Offset = 0 }, new GradientStop { Color = tone.WellBottom, Offset = 1 } },
                },
                BorderBrush = ThemeHelper.Brush(tone.WellBorder),
                BorderThickness = new Thickness(0.5),
                Child = wellGrid,
            };

            // Porcelain bookplate footer (Height 52)
            var footer = new Grid { Height = 52, Padding = new Thickness(13, 0, 12, 0), ColumnSpacing = 8 };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var fMeta = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            fMeta.Children.Add(L10n.Skip(new TextBlock
            {
                Text = col.Name,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }));
            string subStr = col.Count == 0
                ? "还没有书"
                : (col.UsedAt > 0
                    ? $"{col.Count} 本 · {ShortAgo(col.UsedAt)}"
                    : $"{col.Count} 本");
            fMeta.Children.Add(new TextBlock
            {
                Text = subStr,
                FontSize = 11,
                Foreground = ThemeHelper.Brush(palette.T3),
                Opacity = col.Count == 0 ? 0.75 : 1.0,
                MaxLines = 1,
            });
            footer.Children.Add(fMeta);
            var chev = YYIcons.Make("chevron-right", 9.5, ThemeHelper.Brush(palette.T3));
            chev.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(chev, 1);
            footer.Children.Add(chev);

            var cardBody = new StackPanel();
            cardBody.Children.Add(wellBorder);
            cardBody.Children.Add(footer);

            var cardRoot = new Grid();
            var openHit = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(16),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                ContextFlyout = ctx,
            };
            AutomationProperties.SetName(openHit, col.Name);
            string cid = col.Id;
            string cname = col.Name;
            openHit.Click += (_, _) =>
            {
                _cameFromCollectionsWall = true;
                _activeFilter = $"col:{cid}";
                UpdateNavSelectionVisuals();
                RenderCurrentView();
            };

            cardBody.IsHitTestVisible = false;
            cardRoot.Children.Add(cardBody);
            cardRoot.Children.Add(openHit);
            // Place interactive pin & manage pills above the open hit area
            var topOverlay = new Border { Height = 114, Margin = new Thickness(6, 6, 6, 0), VerticalAlignment = VerticalAlignment.Top, Child = topControls };
            cardRoot.Children.Add(topOverlay);

            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(18, 255, 255, 255) : palette.Line),
                BorderThickness = new Thickness(0.5),
                AllowDrop = true,
                Child = cardRoot,
            };
            if (!eink && !dark) ThemeHelper.ApplyElevation(cardBorder, PaperInnerGrid, 6f);

            bool menuOpen = false;
            void SyncPills(bool hovered)
            {
                bool showAll = hovered || menuOpen;
                pinPill.Opacity = (showAll || curPinned) ? 1 : 0;
                morePill.Opacity = showAll ? 1 : 0;
            }
            ctx.Opened += (_, _) => { menuOpen = true; SyncPills(true); };
            ctx.Closed += (_, _) => { menuOpen = false; SyncPills(false); };
            cardBorder.PointerEntered += (_, _) => SyncPills(true);
            cardBorder.PointerExited += (_, _) => SyncPills(false);

            cardBorder.DragOver += (_, de) =>
            {
                if (de.DataView.Properties.ContainsKey("yuyue-book-id")
                    || de.DataView.Contains(StandardDataFormats.Text)
                    || de.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    de.AcceptedOperation = DataPackageOperation.Copy;
                    de.Handled = true;
                    HideDropOverlay();
                    cardBorder.BorderBrush = ThemeHelper.Brush(tint.Accent);
                    cardBorder.BorderThickness = new Thickness(1.2);
                }
            };
            cardBorder.DragLeave += (_, _) =>
            {
                cardBorder.BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(18, 255, 255, 255) : palette.Line);
                cardBorder.BorderThickness = new Thickness(0.5);
            };
            cardBorder.Drop += async (_, de) =>
            {
                de.Handled = true;
                HideDropOverlay();
                var deferral = de.GetDeferral();
                try
                {
                    cardBorder.BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(18, 255, 255, 255) : palette.Line);
                    cardBorder.BorderThickness = new Thickness(0.5);
                    if (de.DataView.Contains(StandardDataFormats.StorageItems))
                    {
                        var items = await de.DataView.GetStorageItemsAsync();
                        var paths = items.Select(it => it.Path).Where(fp => !string.IsNullOrEmpty(fp)).ToArray();
                        if (paths.Length > 0) await ImportPathsAsync(paths, openFirst: false, targetCollectionId: cid, source: "share_in");
                        return;
                    }
                    string? bid = null;
                    if (de.DataView.Properties.TryGetValue("yuyue-book-id", out var rawBid) && rawBid is string propBid && !string.IsNullOrEmpty(propBid))
                    {
                        bid = propBid;
                    }
                    else if (de.DataView.Contains(StandardDataFormats.Text))
                    {
                        string txt = await de.DataView.GetTextAsync();
                        if (txt.StartsWith("yuyue-book:", StringComparison.Ordinal))
                            bid = txt["yuyue-book:".Length..];
                    }
                    if (!string.IsNullOrEmpty(bid))
                    {
                        _library.SetInCollection(cid, bid, true);
                        RefreshBooks();
                        ShowToast($"已放进合集“{cname}”", canUndo: false);
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            };

            Grid.SetRow(cardBorder, i / cols);
            Grid.SetColumn(cardBorder, i % cols);
            grid.Children.Add(cardBorder);
        }

        SubPageStack.Children.Add(grid);
        L10n.ApplyTree(SubPageStack);
    }

    // ---------- Events ----------

    private void ToggleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;
        var target = key switch { "library" => GroupLibrary, "notes" => GroupNotes, "collections" => GroupCollections, _ => null };
        if (target is null) return;
        bool willCollapse = target.Visibility == Visibility.Visible;
        target.Visibility = willCollapse ? Visibility.Collapsed : Visibility.Visible;
        if (willCollapse)
        {
            if (!_library.Prefs.ClosedSidebarGroups.Contains(key)) _library.Prefs.ClosedSidebarGroups.Add(key);
        }
        else
        {
            _library.Prefs.ClosedSidebarGroups.Remove(key);
        }
        _library.SavePrefs();
        UpdateSidebarGroupChevrons();
    }

    public void SwitchTab(string filter)
    {
#if DEBUG
        if (filter.StartsWith("record:", StringComparison.OrdinalIgnoreCase))
        {
            string q = filter[7..];
            var match = _keptBooks.FirstOrDefault(b =>
                b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (q.Equals("hlm", StringComparison.OrdinalIgnoreCase) && b.Authors.Any(a => a.Contains("Cao", StringComparison.OrdinalIgnoreCase))))
                ?? _keptBooks.FirstOrDefault();
            if (match is not null) _recordBuilder.OpenBookId = match.Id;
            _activeFilter = "progress";
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            TrackActiveShelfScreen();
            return;
        }
        if (filter.Equals("podcast-inline", StringComparison.OrdinalIgnoreCase))
        {
            var iss = _library.Podcasts.GetIssues().FirstOrDefault();
            if (iss is not null) _podcastBuilder.OpenIssueId = iss.Id;
            _activeFilter = "podcasts";
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            TrackActiveShelfScreen();
            return;
        }
        if (filter.StartsWith("quick:", StringComparison.OrdinalIgnoreCase))
        {
            string q = filter[6..];
            _activeFilter = "all";
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            TrackActiveShelfScreen();
            QuickSearchOverlay.Visibility = Visibility.Visible;
            QuickSearchBox.Text = q;
            UpdateQuickSearchResults(q);
            return;
        }
#endif
        if (filter == "books") filter = "all";
        if (filter == "notes") InvalidateNotesCache();
        _cameFromCollectionsWall = false;
        _activeFilter = filter;
        UpdateNavSelectionVisuals();
        RenderCurrentView();
        TrackActiveShelfScreen();
    }

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            if (tag == "notes") InvalidateNotesCache();
            _cameFromCollectionsWall = false;
            _activeFilter = tag;
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            TrackActiveShelfScreen();
        }
    }

    private MenuFlyoutSubItem BuildCollectionIconSubMenu(BookCollection col)
    {
        var iconSub = new MenuFlyoutSubItem { Text = L10n.T("更换图标") };
        string colId = col.Id;
        foreach (var (symbol, label) in YYIcons.CollectionIconChoices)
        {
            string locLabel = L10n.T(label);
            var icItem = new MenuFlyoutItem { Text = col.Icon == symbol ? $"✓ {locLabel}" : locLabel };
            string nextIc = symbol;
            icItem.Click += (_, _) =>
            {
                try { _library.Core.SetCollectionIcon(colId, nextIc); } catch { }
                RefreshBooks();
            };
            iconSub.Items.Add(icItem);
        }
        iconSub.Items.Add(new MenuFlyoutSeparator());
        string autoLabel = L10n.T("按名字自动选");
        var autoItem = new MenuFlyoutItem { Text = string.IsNullOrEmpty(col.Icon) ? $"✓ {autoLabel}" : autoLabel };
        autoItem.Click += (_, _) =>
        {
            try { _library.Core.SetCollectionIcon(colId, null); } catch { }
            RefreshBooks();
        };
        iconSub.Items.Add(autoItem);
        return iconSub;
    }

    private void ApplySidebarVisibility(bool visible)
    {
        SidebarColumn.Width = visible ? new GridLength(220) : new GridLength(0);
        SidebarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PaperSheet.Margin = visible ? new Thickness(0, 0, 8, 8) : new Thickness(8, 0, 8, 8);
        if (visible) YYChrome.AnimateSlideX(SidebarPanel, fromX: -16f, durationMs: 170);
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        if (_narrowAutoCollapsed)
        {
            bool currentlyShown = SidebarPanel.Visibility == Visibility.Visible;
            ApplySidebarVisibility(!currentlyShown);
            return;
        }
        _sidebarVisible = !_sidebarVisible;
        _library.Prefs.ShelfSidebarShown = _sidebarVisible;
        _library.SavePrefs();
        ApplySidebarVisibility(_sidebarVisible);
    }

    private void SyncSortMenuVisuals()
    {
        SortItemRecent.IsChecked = _sortKey == "recent";
        SortItemAdded.IsChecked = _sortKey == "added";
        SortItemTitle.IsChecked = _sortKey == "title";
        SortItemAuthor.IsChecked = _sortKey == "author";
        string raw = _sortKey switch
        {
            "added" => "加入时间",
            "title" => "书名",
            "author" => "作者",
            _ => "最近读过",
        };
        SortLabelText.Text = L10n.T(raw);
    }

    internal void SyncShelfSortFromPrefs()
    {
        string pref = _library.Prefs.BooksSort;
        if (pref is "recent" or "added" or "title" or "author")
        {
            _sortKey = pref;
            SyncSortMenuVisuals();
        }
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string key)
        {
            _sortKey = key;
            _library.Prefs.BooksSort = key;
            if (key is "recent" or "added" or "title")
                _recordBuilder.SortKey = key;
            _library.SavePrefs();
            SyncSortMenuVisuals();
            RenderCurrentView();
        }
    }

    private void SyncStatus_Click(object sender, RoutedEventArgs e)
    {
        if (!_library.Sync.IsEnabled)
        {
            App.CurrentApp.OpenSettings("sync");
            return;
        }

        var flyout = new MenuFlyout();
        var syncNowItem = new MenuFlyoutItem { Text = L10n.T("立即同步") };
        syncNowItem.Click += async (_, _) =>
        {
            UpdateSyncFooter();
            try
            {
                await _library.Sync.SyncNowAsync();
            }
            catch { }
            if (_closed) return;
            RefreshBooks();
            UpdateSyncFooter();
        };
        var settingsItem = new MenuFlyoutItem { Text = L10n.T("同步设置…") };
        settingsItem.Click += (_, _) => App.CurrentApp.OpenSettings("sync");
        flyout.Items.Add(syncNowItem);
        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(settingsItem);
        flyout.ShowAt(SyncFooterBtn);
    }

    private void OpenSettings_Click(object sender, RoutedEventArgs e) => App.CurrentApp.OpenSettings("general");
    private async void Feedback_Click(object sender, RoutedEventArgs e) => await _library.Updates.ShowFeedbackComposerAsync(this, RootGrid.XamlRoot);
    private async void WifiTransfer_Click(object sender, RoutedEventArgs e) => await ShelfFeaturesDialogs.ShowWifiTransferDialogAsync(RootGrid.XamlRoot, _library, RefreshBooks);
    private async void RemoteLibrary_Click(object sender, RoutedEventArgs e) => await _library.RemoteLibraries.ShowRemoteLibraryModalAsync(RootGrid.XamlRoot, RefreshBooks);
    private async void RestoreSnapshotMenu_Click(object sender, RoutedEventArgs e)
    {
        if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library))
        {
            RefreshBooks();
            ShowToast("已从快照恢复书库数据", canUndo: false);
        }
    }

    private void QuickSearchTrigger_Click(object sender, RoutedEventArgs e) => OpenQuickSearch();

    private void OpenQuickSearch()
    {
        QuickSearchOverlay.Visibility = Visibility.Visible;
        QuickSearchBox.Text = "";
        UpdateQuickSearchResults("");
        QuickSearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseQuickSearch()
    {
        try { _quickSearchCts?.Cancel(); } catch { }
        QuickSearchOverlay.Visibility = Visibility.Collapsed;
    }

    private void QuickSearchClose_Click(object sender, RoutedEventArgs e) => CloseQuickSearch();

    private async Task<XamlRoot?> WaitForXamlRootAsync(int timeoutMs = 4000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_closed && RootGrid.XamlRoot is null && sw.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(30);
        return RootGrid.XamlRoot;
    }

    private void QuickSearchBackdrop_Pressed(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, QuickSearchOverlay)) CloseQuickSearch();
    }

    private void QuickSearchBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateQuickSearchResults(QuickSearchBox.Text);

    private void QuickSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            CloseQuickSearch();
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.Enter)
        {
            TriggerAskCompanionFromQuickSearch(QuickSearchBox.Text.Trim());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down && _quickSearchItems.Count > 0)
        {
            SetQuickSearchSelection((_quickSearchSelectedIdx + 1) % _quickSearchItems.Count);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up && _quickSearchItems.Count > 0)
        {
            int next = _quickSearchSelectedIdx <= 0 ? _quickSearchItems.Count - 1 : _quickSearchSelectedIdx - 1;
            SetQuickSearchSelection(next);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && _quickSearchItems.Count > 0)
        {
            int idx = _quickSearchSelectedIdx >= 0 && _quickSearchSelectedIdx < _quickSearchItems.Count ? _quickSearchSelectedIdx : 0;
            _quickSearchItems[idx].Activate();
            e.Handled = true;
        }
    }

    private void SetQuickSearchSelection(int index)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        _quickSearchSelectedIdx = index;

        for (int i = 0; i < _quickSearchItems.Count; i++)
        {
            var (btn, _, openHint) = _quickSearchItems[i];
            if (i == index)
            {
                btn.Background = ThemeHelper.Brush(i == 0
                    ? Color.FromArgb(dark ? (byte)36 : (byte)24, tint.Accent.R, tint.Accent.G, tint.Accent.B)
                    : palette.Inset);
                if (openHint is not null) openHint.Visibility = Visibility.Visible;
                btn.StartBringIntoView();
            }
            else
            {
                btn.Background = i == 0
                    ? ThemeHelper.Brush(Color.FromArgb(dark ? (byte)18 : (byte)12, tint.Accent.R, tint.Accent.G, tint.Accent.B))
                    : new SolidColorBrush(Colors.Transparent);
                if (openHint is not null) openHint.Visibility = Visibility.Collapsed;
            }
        }
    }

    private PodcastIssue? TryResolvePodcastIssueForWord(string? bookKey)
    {
        if (string.IsNullOrWhiteSpace(bookKey)) return null;
        string candId = bookKey.StartsWith("podcast:", StringComparison.Ordinal) ? bookKey[8..] : bookKey;
        var issues = _library.Podcasts.GetIssues();
        return issues.FirstOrDefault(i => i.Id == candId)
            ?? issues.FirstOrDefault(i => string.Equals(i.Title, bookKey, StringComparison.Ordinal));
    }

    private string ResolveVocabSourceTitle(string? bookKey, string fallback = "")
    {
        if (string.IsNullOrWhiteSpace(bookKey)) return fallback;
        var bk = _keptBooks.FirstOrDefault(b => b.Id == bookKey);
        if (bk is not null) return bk.Title;
        var pod = TryResolvePodcastIssueForWord(bookKey);
        if (pod is not null) return pod.Title;
        return bookKey.StartsWith("podcast:", StringComparison.Ordinal) ? "播客单集" : fallback;
    }

    private string? ResolveVocabLanguage(string? bookKey)
    {
        if (string.IsNullOrWhiteSpace(bookKey)) return null;
        var bk = _keptBooks.FirstOrDefault(b => b.Id == bookKey);
        if (bk is not null && !string.IsNullOrWhiteSpace(bk.Language)) return bk.Language;
        var pod = TryResolvePodcastIssueForWord(bookKey);
        if (pod is not null && !string.IsNullOrWhiteSpace(pod.Language)) return pod.Language;
        return null;
    }

    private void TriggerAskCompanionFromQuickSearch(string q)
    {
        var targetBook = _quickSearchBestCompanionBook
            ?? QuickSearchDialog.ResolveCompanionTargetBook([], null, _shelfBooks);
        CloseQuickSearch();
        if (targetBook is not null)
        {
            TryOpenBook(targetBook, askQuestion: string.IsNullOrWhiteSpace(q) ? null : q);
        }
        else
        {
            ShowToast("先打开一本书", canUndo: false);
        }
    }

    // 1:1 QuickSearch.swift：Row 0 固定「问书伴」，下方分组：最近读过/书名和篇目、全文（Core.SearchBook）、笔记
    private void UpdateQuickSearchResults(string query)
    {
        try { _quickSearchCts?.Cancel(); _quickSearchCts?.Dispose(); } catch { }
        var cts = new CancellationTokenSource();
        _quickSearchCts = cts;
        var ct = cts.Token;

        var q = query.Trim();
        RenderQuickSearchRows(q, tocMatches: [], fullTextHits: [], hitBookCount: 0, totalLineCount: 0, noteMatches: [], searching: !string.IsNullOrEmpty(q));

        if (string.IsNullOrEmpty(q)) return;

        var shelfSnapshot = _shelfBooks.ToArray();
        var keptSnapshot = _keptBooks.ToArray();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(120, ct); } catch { return; }
            if (ct.IsCancellationRequested) return;

            var tocMatches = new List<(BookSummary Book, uint SectionIndex, string Title)>();
            foreach (var b in shelfSnapshot.Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var det = _library.GetBookDetail(b.Id);
                    for (uint sIdx = 0; sIdx < det.Sections.Length; sIdx++)
                    {
                        string? sTitle = det.Sections[sIdx].Title;
                        if (!string.IsNullOrWhiteSpace(sTitle) && NameMatcher.Matches(sTitle, q))
                        {
                            tocMatches.Add((b, sIdx, sTitle));
                            if (tocMatches.Count >= 6) break;
                        }
                    }
                    if (tocMatches.Count >= 6) break;
                }
                catch { }
            }

            var fullTextHits = new List<(BookSummary Book, SearchHit Hit, string Chapter)>();
            var bookHitBuckets = new List<(BookSummary Book, SearchHit[] Hits, BookDetail? Detail)>();
            int hitBookCount = 0;
            int totalLineCount = 0;
            bool lineCapped = false;
            // True round-robin across ALL matching text books (1:1 QuickSearch.swift:193-203)
            foreach (var b in shelfSnapshot.Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio))
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    var hits = _library.Core.SearchBook(b.Id, q);
                    if (hits.Length > 0)
                    {
                        hitBookCount++;
                        totalLineCount += hits.Length;
                        if (hits.Length >= 500) lineCapped = true;
                        BookDetail? det = null;
                        try { det = _library.GetBookDetail(b.Id); } catch { }
                        bookHitBuckets.Add((b, hits, det));
                    }
                }
                catch { }
            }

            for (int round = 0; fullTextHits.Count < 8; round++)
            {
                bool anyInRound = false;
                foreach (var (b, hits, det) in bookHitBuckets)
                {
                    if (round < hits.Length)
                    {
                        anyInRound = true;
                        var h = hits[round];
                        string chap = det is not null && h.Section < det.Sections.Length ? (det.Sections[h.Section].Title ?? "") : "";
                        fullTextHits.Add((b, h, chap));
                        if (fullTextHits.Count >= 8) break;
                    }
                }
                if (!anyInRound) break;
            }

            var noteMatches = new List<(BookSummary Book, Mark Mark)>();
            foreach (var b in keptSnapshot)
            {
                if (ct.IsCancellationRequested) return;
                try
                {
                    foreach (var mk in _library.GetMarks(b.Id))
                    {
                        if (mk.Text.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                            (mk.Note?.Contains(q, StringComparison.OrdinalIgnoreCase) == true))
                        {
                            noteMatches.Add((b, mk));
                        }
                    }
                }
                catch { }
            }

            if (ct.IsCancellationRequested) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_closed || ct.IsCancellationRequested || QuickSearchOverlay.Visibility != Visibility.Visible) return;
                RenderQuickSearchRows(q, tocMatches, fullTextHits, hitBookCount, totalLineCount, noteMatches, searching: false, lineCapped: lineCapped);
            });
        }, ct);
    }

    private void RenderQuickSearchRows(
        string q,
        IReadOnlyList<(BookSummary Book, uint SectionIndex, string Title)> tocMatches,
        IReadOnlyList<(BookSummary Book, SearchHit Hit, string Chapter)> fullTextHits,
        int hitBookCount,
        int totalLineCount,
        IReadOnlyList<(BookSummary Book, Mark Mark)> noteMatches,
        bool searching,
        bool lineCapped = false)
    {
        QuickSearchResults.Children.Clear();
        _quickSearchItems.Clear();
        _quickSearchSelectedIdx = -1;

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        int totalNotes = 0;
        foreach (var b in _keptBooks)
            if (_markCounts.TryGetValue(b.Id, out var c)) totalNotes += (int)c;
        QuickSearchScopeText.Text = L10n.T(searching
            ? "正在书库和笔记里检索…"
            : $"在 {_keptBooks.Count} 本书和 {totalNotes} 条笔记里搜");

        UIElement GroupHeader(string title, string? right = null)
        {
            var g = new Grid { Margin = new Thickness(10, 8, 10, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = L10n.T(title),
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T3),
                VerticalAlignment = VerticalAlignment.Center,
            });
            if (!string.IsNullOrEmpty(right))
            {
                var rtb = new TextBlock
                {
                    Text = L10n.T(right),
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(palette.T3),
                    Opacity = 0.85,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(rtb, 1);
                g.Children.Add(rtb);
            }
            return g;
        }

        Border MakeOpenHintBadge() => new()
        {
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(palette.Card),
            BorderBrush = ThemeHelper.Brush(tint.Edge),
            BorderThickness = new Thickness(0.5),
            Padding = new Thickness(6, 1.5, 6, 1.5),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = new TextBlock
            {
                Text = L10n.T("↩ 打开"),
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(tint.Accent),
            },
        };

        void RegisterRow(Button btn, Action activate, FrameworkElement? openHint = null)
        {
            int idx = _quickSearchItems.Count;
            _quickSearchItems.Add((btn, activate, openHint));
            btn.Click += (_, _) => activate();
            btn.PointerEntered += (_, _) => SetQuickSearchSelection(idx);
            QuickSearchResults.Children.Add(btn);
        }

        // Group 1：最近读过 / 书名和篇目（支持拼音首字母 NameMatcher）
        var bookMatches = string.IsNullOrEmpty(q)
            ? _shelfBooks.OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt)).Take(5).ToArray()
            : _shelfBooks.Where(b =>
                NameMatcher.Matches(b.Title, q) ||
                b.Authors.Any(a => NameMatcher.Matches(a, q))).Take(8).ToArray();

        var passageHitBook = fullTextHits.Select(h => h.Book).FirstOrDefault(QuickSearchDialog.IsCompanionEligible);
        _quickSearchBestCompanionBook = QuickSearchDialog.ResolveCompanionTargetBook(
            string.IsNullOrEmpty(q) ? [] : bookMatches,
            passageHitBook,
            _shelfBooks);

        // Row 0：始终展示「问书伴」（#29：排除有声书、PDF、未下载与纯记录书）
        var askGrid = new Grid { ColumnSpacing = 10 };
        askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        askGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        askGrid.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(9),
            Background = ThemeHelper.Brush(tint.Soft),
            Child = YYIcons.Make("sparkle", 13.5, ThemeHelper.Brush(tint.Accent)),
        });
        var askMeta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        askMeta.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(q) ? "问书伴" : $"问书伴：“{q}”",
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        string askSubText = _quickSearchBestCompanionBook is not null
            ? $"在《{_quickSearchBestCompanionBook.Title}》里只用你读过的部分回答，带出处"
            : "只用你读过的部分回答，带出处";
        askMeta.Children.Add(new TextBlock { Text = askSubText, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(askMeta, 1);
        askGrid.Children.Add(askMeta);
        var keycap = new Border
        {
            CornerRadius = new CornerRadius(5),
            Background = ThemeHelper.Brush(palette.Inset),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = "Ctrl+↵", FontSize = 10, Foreground = ThemeHelper.Brush(palette.T3) },
        };
        Grid.SetColumn(keycap, 2);
        askGrid.Children.Add(keycap);

        var askBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = ThemeHelper.Brush(Color.FromArgb(dark ? (byte)18 : (byte)12, tint.Accent.R, tint.Accent.G, tint.Accent.B)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 6, 10, 6),
            UseSystemFocusVisuals = false,
            Content = askGrid,
        };
        RegisterRow(askBtn, () => TriggerAskCompanionFromQuickSearch(q));

        if (bookMatches.Length > 0 || tocMatches.Count > 0)
        {
            string? countNote = string.IsNullOrEmpty(q) ? null : $"{bookMatches.Length + tocMatches.Count} 项";
            QuickSearchResults.Children.Add(GroupHeader(string.IsNullOrEmpty(q) ? "最近读过" : "书名和篇目", countNote));
            foreach (var book in bookMatches)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(CreateCoverView(book, 20, 28, elevated: false));
                var tb = L10n.Skip(QuickSearchDialog.BuildHighlightedBlock(book.Title, q, 13, Microsoft.UI.Text.FontWeights.Normal, palette.T1, tint.Accent));
                tb.MaxLines = 1;
                tb.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                string subLabel = $"{book.Authors.FirstOrDefault() ?? ""} · {L10n.T(FormatShelfNote(book))}".Trim(' ', '·');
                var sub = L10n.Skip(QuickSearchDialog.BuildHighlightedBlock(subLabel, q, 11, Microsoft.UI.Text.FontWeights.Normal, palette.T3, tint.Accent));
                sub.MaxLines = 1;
                sub.VerticalAlignment = VerticalAlignment.Center;
                var openBadge = MakeOpenHintBadge();
                var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightSlot.Children.Add(sub);
                rightSlot.Children.Add(openBadge);
                Grid.SetColumn(rightSlot, 2);
                row.Children.Add(rightSlot);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6, 10, 6),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                var bk = book;
                RegisterRow(btn, () => { CloseQuickSearch(); TryOpenBook(bk); }, openBadge);
            }

            foreach (var (book, secIdx, secTitle) in tocMatches)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(CreateCoverView(book, 20, 28, elevated: false));
                var tb = L10n.Skip(QuickSearchDialog.BuildHighlightedBlock(secTitle, q, 12.5, Microsoft.UI.Text.FontWeights.Normal, palette.T1, tint.Accent));
                tb.MaxLines = 1;
                tb.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                var sub = L10n.Skip(new TextBlock { Text = $"{L10n.T("章节")} · {book.Title}", FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                var openBadge = MakeOpenHintBadge();
                var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightSlot.Children.Add(sub);
                rightSlot.Children.Add(openBadge);
                Grid.SetColumn(rightSlot, 2);
                row.Children.Add(rightSlot);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6, 10, 6),
                    UseSystemFocusVisuals = false,
                    Content = row,
                };
                var bk = book;
                uint targetSec = secIdx;
                RegisterRow(btn, () => { CloseQuickSearch(); TryOpenBook(bk, section: targetSec, sentence: 0); }, openBadge);
            }
        }

        if (string.IsNullOrEmpty(q))
        {
            L10n.ApplyTree(QuickSearchResults);
            if (_quickSearchItems.Count > 1) SetQuickSearchSelection(1);
            else if (_quickSearchItems.Count > 0) SetQuickSearchSelection(0);
            return;
        }

        if (fullTextHits.Count > 0)
        {
            QuickSearchResults.Children.Add(GroupHeader("全文", $"{totalLineCount}{(lineCapped ? "+" : "")} 处 · {hitBookCount} 本书"));
            foreach (var (b, hit, chap) in fullTextHits)
            {
                var r = new Grid { ColumnSpacing = 10 };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(CreateCoverView(b, 20, 28, elevated: false));

                var sentTb = L10n.Skip(new TextBlock
                {
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(palette.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                if (!string.IsNullOrEmpty(hit.Before)) sentTb.Inlines.Add(new Run { Text = hit.Before });
                var ul = new Underline();
                ul.Inlines.Add(new Run { Text = hit.Matched, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                sentTb.Inlines.Add(ul);
                if (!string.IsNullOrEmpty(hit.After)) sentTb.Inlines.Add(new Run { Text = hit.After });
                Grid.SetColumn(sentTb, 1);
                r.Children.Add(sentTb);

                string srcLabel = string.IsNullOrWhiteSpace(chap) ? b.Title : $"{b.Title} · {chap}";
                var src = L10n.Skip(new TextBlock { Text = srcLabel, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 160, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                var openBadge = MakeOpenHintBadge();
                var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightSlot.Children.Add(src);
                rightSlot.Children.Add(openBadge);
                Grid.SetColumn(rightSlot, 2);
                r.Children.Add(rightSlot);

                var hb = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6, 10, 6),
                    UseSystemFocusVisuals = false,
                    Content = r,
                };
                var hitBook = b;
                uint sec = hit.Section;
                int sent = (int)hit.Sentence;
                string searchQ = q;
                RegisterRow(hb, () => { CloseQuickSearch(); TryOpenBook(hitBook, section: sec, sentence: sent, searchQuery: searchQ); }, openBadge);
            }
        }

        if (noteMatches.Count > 0)
        {
            QuickSearchResults.Children.Add(GroupHeader("笔记", $"{noteMatches.Count} 条"));
            foreach (var (b, mk) in noteMatches.Take(3))
            {
                var r = new Grid { ColumnSpacing = 10 };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(CreateCoverView(b, 20, 28, elevated: false));

                var sp = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                var mkTb = L10n.Skip(QuickSearchDialog.BuildHighlightedBlock(mk.Text, q, 12.5, Microsoft.UI.Text.FontWeights.Normal, palette.T1, tint.Accent));
                mkTb.FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily());
                mkTb.MaxLines = 1;
                sp.Children.Add(mkTb);
                if (!string.IsNullOrWhiteSpace(mk.Note))
                {
                    var ntTb = L10n.Skip(QuickSearchDialog.BuildHighlightedBlock(mk.Note, q, 11.5, Microsoft.UI.Text.FontWeights.Normal, tint.Accent, tint.Accent));
                    ntTb.FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily());
                    ntTb.MaxLines = 1;
                    sp.Children.Add(ntTb);
                }
                Grid.SetColumn(sp, 1);
                r.Children.Add(sp);

                var src = L10n.Skip(new TextBlock { Text = b.Title, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                var openBadge = MakeOpenHintBadge();
                var rightSlot = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                rightSlot.Children.Add(src);
                rightSlot.Children.Add(openBadge);
                Grid.SetColumn(rightSlot, 2);
                r.Children.Add(rightSlot);

                var nb = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(10, 6, 10, 6),
                    UseSystemFocusVisuals = false,
                    Content = r,
                };
                var noteBook = b;
                uint sec = mk.Section;
                int sent = (int)mk.Start.Sentence;
                RegisterRow(nb, () => { CloseQuickSearch(); TryOpenBook(noteBook, section: sec, sentence: sent); }, openBadge);
            }
        }

        if (!searching && bookMatches.Length == 0 && tocMatches.Count == 0 && fullTextHits.Count == 0 && noteMatches.Count == 0)
        {
            QuickSearchResults.Children.Add(new TextBlock
            {
                Text = "书里和笔记里都没找到。按 Ctrl+↵ 问书伴试试",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(palette.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 22, 0, 18),
            });
        }

        L10n.ApplyTree(QuickSearchResults);
        if (_quickSearchItems.Count > 1) SetQuickSearchSelection(1);
        else if (_quickSearchItems.Count > 0) SetQuickSearchSelection(0);
    }

    private void ToggleFullScreen()
    {
        try
        {
            var appWin = this.AppWindow;
            if (appWin is null) return;
            appWin.SetPresenter(appWin.Presenter.Kind == AppWindowPresenterKind.FullScreen
                ? AppWindowPresenterKind.Overlapped
                : AppWindowPresenterKind.FullScreen);
        }
        catch { }
    }

    private void RootGrid_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_activeFilter != "podcasts" || QuickSearchOverlay.Visibility == Visibility.Visible) return;
        if (YYChrome.HasBlockingModalOrFlyoutOpen(RootGrid.XamlRoot)) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl || alt) return;

        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        if (focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox) return;

        if (e.Key is VirtualKey.Space or VirtualKey.H or VirtualKey.I
            or VirtualKey.Number1 or VirtualKey.Number2 or VirtualKey.Number3)
        {
            if (_podcastBuilder.HandleKeyDown(e.Key))
                e.Handled = true;
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (YYChrome.HasBlockingModalOrFlyoutOpen(RootGrid.XamlRoot)) return;

        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        bool inTextInput = focused is TextBox or RichEditBox or PasswordBox or AutoSuggestBox;

        if (ctrl && !alt && e.Key == VirtualKey.W)
        {
            Close();
            e.Handled = true;
        }
        else if (ctrl && !alt && e.Key == VirtualKey.K)
        {
            if (QuickSearchOverlay.Visibility == Visibility.Visible) CloseQuickSearch();
            else OpenQuickSearch();
            e.Handled = true;
        }
        else if (ctrl && !alt && e.Key == VirtualKey.F)
        {
            TextBox? activeFilterBox = _activeFilter switch
            {
                "notes" => _activeNotesFilterBox,
                "words" => _activeWordsFilterBox,
                "collections" => _activeCollectionsFilterBox,
                _ => null,
            };
            if (activeFilterBox is not null && activeFilterBox.IsLoaded && activeFilterBox.Visibility == Visibility.Visible)
            {
                activeFilterBox.Focus(FocusState.Programmatic);
                activeFilterBox.SelectAll();
            }
            else
            {
                OpenQuickSearch();
            }
            e.Handled = true;
        }
        else if (!ctrl && !alt && e.Key == VirtualKey.F11)
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (ctrl && alt && (e.Key == VirtualKey.S || e.Key == VirtualKey.N))
        {
            ToggleSidebar_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !alt && !inTextInput && e.Key == VirtualKey.Z && ToastBanner.Visibility == Visibility.Visible && _toastUndoAction is not null)
        {
            ToastUndo_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !alt && (e.Key == VirtualKey.O || e.Key == VirtualKey.I))
        {
            ImportButton_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && !alt && e.Key == (VirtualKey)188)
        {
            App.CurrentApp.OpenSettings("general");
            e.Handled = true;
        }
        else if (ctrl && !alt && !inTextInput && e.Key is >= VirtualKey.Number1 and <= VirtualKey.Number7)
        {
            string targetTab = e.Key switch
            {
                VirtualKey.Number1 => "all",
                VirtualKey.Number2 => "progress",
                VirtualKey.Number3 => "podcasts",
                VirtualKey.Number4 => "audio",
                VirtualKey.Number5 => "notes",
                VirtualKey.Number6 => "words",
                VirtualKey.Number7 => "collections",
                _ => "all",
            };
            CloseQuickSearch();
            SwitchTab(targetTab);
            e.Handled = true;
        }
        else if (ctrl && e.Key == (VirtualKey)219 || alt && e.Key == VirtualKey.Left)
        {
            if (_activeFilter == "progress" && _recordBuilder.OpenBookId is not null)
            {
                _recordBuilder.OpenBookId = null;
                RefreshCurrentSubPage();
                e.Handled = true;
            }
            else if (_activeFilter == "podcasts" && _podcastBuilder.OpenIssueId is not null)
            {
                _podcastBuilder.OpenIssueId = null;
                RefreshCurrentSubPage();
                e.Handled = true;
            }
            else if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
            {
                _cameFromCollectionsWall = false;
                _activeFilter = "collections";
                UpdateNavSelectionVisuals();
                RenderCurrentView();
                e.Handled = true;
            }
        }
        else if (e.Key == VirtualKey.Escape)
        {
            if (QuickSearchOverlay.Visibility == Visibility.Visible)
            {
                CloseQuickSearch();
                e.Handled = true;
            }
            else if (!inTextInput && _activeFilter == "progress" && _recordBuilder.OpenBookId is not null)
            {
                _recordBuilder.OpenBookId = null;
                RefreshCurrentSubPage();
                e.Handled = true;
            }
            else if (!inTextInput && _activeFilter == "podcasts")
            {
                if (_podcastBuilder.HandleKeyDown(VirtualKey.Escape))
                {
                    e.Handled = true;
                }
                else if (_podcastBuilder.OpenIssueId is not null)
                {
                    _podcastBuilder.OpenIssueId = null;
                    RefreshCurrentSubPage();
                    e.Handled = true;
                }
            }
            else if (!inTextInput && _activeFilter.StartsWith("col:", StringComparison.Ordinal))
            {
                _cameFromCollectionsWall = false;
                _activeFilter = "collections";
                UpdateNavSelectionVisuals();
                RenderCurrentView();
                e.Handled = true;
            }
            else if (this.AppWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen)
            {
                try { this.AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped); } catch { }
                e.Handled = true;
            }
        }
        else if (!ctrl && !alt && !inTextInput && _activeFilter == "podcasts" && QuickSearchOverlay.Visibility != Visibility.Visible)
        {
            if (_podcastBuilder.HandleKeyDown(e.Key))
                e.Handled = true;
        }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var exts = _activeFilter == "audio"
            ? AudiobookImporter.Extensions.Concat([".zip"]).ToArray()
            : EbookImportExtensions.Concat(AudiobookImporter.Extensions).Concat([".zip"]).ToArray();
        var files = await NativePickerHelper.PickMultipleFilesAsync(this, exts, PickerLocationId.DocumentsLibrary, PickerViewMode.List);
        if (files.Count == 0 || _closed) return;
        await ImportPathsAsync(files.ToArray(), openFirst: false);
    }

    private async void ImportAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await NativePickerHelper.PickSingleFolderAsync(this, PickerLocationId.MusicLibrary);
        if (string.IsNullOrEmpty(folder) || _closed) return;
        await ImportPathsAsync([folder], openFirst: false);
    }

    // 1:1 Library.swift:825-835 (booksInside): recursively enumerate ebook files inside a directory, skipping hidden entries
    private static List<string> EnumerateBooksInsideFolder(string dir)
    {
        var found = new List<string>();
        var stack = new Stack<string>();
        stack.Push(dir);
        while (stack.Count > 0)
        {
            string cur = stack.Pop();
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(cur))
                {
                    string dname = Path.GetFileName(sub);
                    if (!string.IsNullOrEmpty(dname) && !dname.StartsWith('.'))
                        stack.Push(sub);
                }
                foreach (var f in Directory.EnumerateFiles(cur))
                {
                    string fname = Path.GetFileName(f);
                    if (string.IsNullOrEmpty(fname) || fname.StartsWith('.')) continue;
                    if (EbookImportExtensions.Contains(Path.GetExtension(f)))
                        found.Add(f);
                }
            }
            catch { }
        }
        found.Sort(StringComparer.OrdinalIgnoreCase);
        return found;
    }

    private void ImportQueueCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _importCardHoverPaused = true;
        _importCardDismissTimer?.Stop();
    }

    private void ImportQueueCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_importCardHoverPaused) return;
        _importCardHoverPaused = false;
        if (ImportQueueCard.Visibility == Visibility.Visible)
            _importCardDismissTimer?.Start();
    }

    private void HideImportQueueCard()
    {
        _importCardDismissTimer?.Stop();
        _importCardDismissTimer = null;
        _importCardHoverPaused = false;
        ImportQueueCard.Visibility = Visibility.Collapsed;
        ImportQueueCard.Child = null;
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> paths, bool openFirst, string? targetCollectionId = null, string source = "file_picker")
    {
        string? effectiveColId = targetCollectionId
            ?? (_activeFilter.StartsWith("col:", StringComparison.Ordinal) ? _activeFilter[4..] : null);

        // #23: only already-downloaded real shelf books count as duplicates; cloud placeholders & paper records upgrade to "✓ 已导入"
        var existingIds = _keptBooks
            .Where(b => b.Available && !b.Record)
            .Select(b => b.Id)
            .ToHashSet(StringComparer.Ordinal);
        var imported = new List<BookSummary>();
        var duplicates = new List<BookSummary>();
        int errorCount = 0;

        var snapshotPaths = paths.Where(p => string.Equals(Path.GetExtension(p), ".yuyue", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (snapshotPaths.Length > 0)
        {
            var xr = await WaitForXamlRootAsync();
            if (xr is not null)
            {
                foreach (var sp in snapshotPaths)
                {
                    if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, xr, _library, sp))
                    {
                        RefreshBooks();
                        ShowToast("已从快照恢复书库数据", canUndo: false);
                    }
                }
            }
        }
        if (snapshotPaths.Length == paths.Count) return;

        var rawCopy = paths.ToArray();
        var (ebookFiles, audioBatches) = await Task.Run(() =>
        {
            var ebooks = new List<string>();
            var batches = new List<string[]>();
            var looseAudio = new List<string>();

            foreach (var raw in rawCopy)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (string.Equals(Path.GetExtension(raw), ".yuyue", StringComparison.OrdinalIgnoreCase)) continue;

                if (Directory.Exists(raw))
                {
                    var booksInside = EnumerateBooksInsideFolder(raw);
                    if (booksInside.Count > 0)
                    {
                        ebooks.AddRange(booksInside);
                    }
                    else if (AudiobookImporter.IsAudiobookOrArchivePath(raw))
                    {
                        batches.Add([raw]);
                    }
                    continue;
                }

                if (!File.Exists(raw)) continue;
                string ext = Path.GetExtension(raw).ToLowerInvariant();
                if (EbookImportExtensions.Contains(ext))
                {
                    ebooks.Add(raw);
                }
                else if (ext is ".m4b" or ".zip" || AudiobookImporter.DrmExtensions.Contains(ext))
                {
                    // Each .m4b or .zip archive is its own standalone audiobook
                    batches.Add([raw]);
                }
                else if (AudiobookImporter.Extensions.Contains(ext))
                {
                    looseAudio.Add(raw);
                }
            }

            if (looseAudio.Count > 0)
                batches.Add(looseAudio.ToArray());

            return (ebooks, batches);
        });
        if (_closed) return;

        bool useInteractiveAudioSheet = audioBatches.Count == 1 && ebookFiles.Count == 0 && RootGrid.XamlRoot is not null && !openFirst;
        int totalQueue = ebookFiles.Count + (useInteractiveAudioSheet ? 0 : audioBatches.Count);

        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        TextBlock? headerTitleText = null;
        Border? progressFill = null;
        const double progressTrackWidth = 296.0;
        var rowViews = new List<(TextBlock TitleTb, TextBlock SubTb, ContentControl StatusHost)>(totalQueue);
        int doneCount = 0;

        void UpdateProgressVisuals()
        {
            if (headerTitleText is not null)
                headerTitleText.Text = L10n.T($"正在导入 {doneCount} / {totalQueue}…");
            if (progressFill is not null)
                progressFill.Width = totalQueue > 0 ? Math.Clamp(progressTrackWidth * doneCount / totalQueue, 0, progressTrackWidth) : 0;
        }

        UIElement MakeStatusWaiting() => new TextBlock
        {
            Text = L10n.T("等待中"),
            FontSize = 11,
            Foreground = ThemeHelper.Brush(p.T3),
            VerticalAlignment = VerticalAlignment.Center,
        };

        UIElement MakeStatusImporting() => new ProgressRing
        {
            IsActive = true,
            Width = 14,
            Height = 14,
            Foreground = ThemeHelper.Brush(tint.Accent),
            VerticalAlignment = VerticalAlignment.Center,
        };

        UIElement MakeStatusSuccess() => YYIcons.Make("checkmark", 11, ThemeHelper.Brush(p.Ok));

        UIElement MakeStatusDuplicate(string bookId)
        {
            var btn = new Button
            {
                Height = 22,
                MinHeight = 22,
                Padding = new Thickness(7, 0, 7, 0),
                CornerRadius = new CornerRadius(11),
                Background = ThemeHelper.Brush(p.Inset),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = new TextBlock
                {
                    Text = L10n.T("已在书架"),
                    FontSize = 10.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T2),
                },
            };
            btn.Click += (_, _) => RevealAndHighlightBook(bookId);
            return btn;
        }

        UIElement MakeStatusError() => new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = ThemeHelper.Brush(p.Danger),
            Child = L10n.Skip(new TextBlock
            {
                Text = "!",
                FontSize = 10,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }),
        };

        if (totalQueue >= 1)
        {
            _importCardDismissTimer?.Stop();
            _importCardDismissTimer = null;
            _importCardHoverPaused = false;

            var cardStack = new StackPanel();
            var headGrid = new Grid { Padding = new Thickness(14, 10, 8, 8), ColumnSpacing = 8 };
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            headerTitleText = new TextBlock
            {
                Text = L10n.T($"正在导入 0 / {totalQueue}…"),
                FontSize = 12.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(p.T1),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            headGrid.Children.Add(headerTitleText);

            var closeBtn = new Button
            {
                Width = 24,
                Height = 24,
                MinWidth = 24,
                MinHeight = 24,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(12),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                UseSystemFocusVisuals = false,
                Content = YYIcons.Make("xmark", 10, ThemeHelper.Brush(p.T3)),
            };
            AutomationProperties.SetName(closeBtn, L10n.T("关闭"));
            closeBtn.Click += (_, _) => HideImportQueueCard();
            Grid.SetColumn(closeBtn, 1);
            headGrid.Children.Add(closeBtn);
            cardStack.Children.Add(headGrid);

            progressFill = new Border
            {
                Height = 2,
                Width = 0,
                CornerRadius = new CornerRadius(1),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = ThemeHelper.Brush(tint.Accent),
            };
            cardStack.Children.Add(new Border
            {
                Height = 2,
                Width = progressTrackWidth,
                Margin = new Thickness(12, 0, 12, 4),
                CornerRadius = new CornerRadius(1),
                Background = ThemeHelper.Brush(p.Inset),
                Child = progressFill,
            });

            var rowsStack = new StackPanel();
            var initialLabels = new List<string>(totalQueue);
            foreach (var path in ebookFiles)
                initialLabels.Add(Path.GetFileNameWithoutExtension(path));
            if (!useInteractiveAudioSheet)
            {
                foreach (var batch in audioBatches)
                    initialLabels.Add(batch.Length == 1 ? Path.GetFileNameWithoutExtension(batch[0]) : L10n.T("有声书"));
            }

            for (int i = 0; i < initialLabels.Count; i++)
            {
                if (i > 0)
                    rowsStack.Children.Add(new Border { Height = 0.5, Background = ThemeHelper.Brush(p.Line), Margin = new Thickness(14, 0, 14, 0) });

                var rg = new Grid { Padding = new Thickness(14, 7, 12, 7), ColumnSpacing = 8 };
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var metaSp = new StackPanel { Spacing = 1.5, VerticalAlignment = VerticalAlignment.Center };
                var titleTb = L10n.Skip(new TextBlock
                {
                    Text = initialLabels[i],
                    FontSize = 12,
                    FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                    Foreground = ThemeHelper.Brush(p.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                var subTb = new TextBlock
                {
                    Visibility = Visibility.Collapsed,
                    FontSize = 10.5,
                    Foreground = ThemeHelper.Brush(p.Danger),
                    TextWrapping = TextWrapping.Wrap,
                };
                metaSp.Children.Add(titleTb);
                metaSp.Children.Add(subTb);
                rg.Children.Add(metaSp);

                var statusHost = new ContentControl
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Content = MakeStatusWaiting(),
                };
                Grid.SetColumn(statusHost, 1);
                rg.Children.Add(statusHost);

                rowViews.Add((titleTb, subTb, statusHost));
                rowsStack.Children.Add(rg);
            }

            cardStack.Children.Add(new ScrollViewer
            {
                MaxHeight = 216,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(0, 0, 0, 6),
                Content = rowsStack,
            });

            ImportQueueCard.Child = cardStack;
            ImportQueueCard.Visibility = Visibility.Visible;
            YYChrome.AnimateEntrance(ImportQueueCard, fromY: 10f, durationMs: 165);
        }

        int rowIdx = 0;

        // Import ebooks first so simultaneous ebook + audiobook drops auto-pair cleanly
        foreach (var path in ebookFiles)
        {
            if (_closed) return;
            var curRow = rowIdx < rowViews.Count ? rowViews[rowIdx] : default;
            if (curRow.StatusHost is not null)
                curRow.StatusHost.Content = MakeStatusImporting();
            UpdateProgressVisuals();

            try
            {
                var book = await _library.ImportBookWithExtrasAsync(path);
                if (_closed) return;
                if (!string.IsNullOrEmpty(effectiveColId))
                    _library.SetInCollection(effectiveColId, book.Id, true);
                if (curRow.TitleTb is not null)
                    curRow.TitleTb.Text = book.Title;

                if (existingIds.Contains(book.Id))
                {
                    duplicates.Add(book);
                    if (curRow.StatusHost is not null)
                        curRow.StatusHost.Content = MakeStatusDuplicate(book.Id);
                }
                else
                {
                    existingIds.Add(book.Id);
                    imported.Add(book);
                    if (curRow.StatusHost is not null)
                        curRow.StatusHost.Content = MakeStatusSuccess();
                    _library.Analytics.TrackBookImported(book.Format.ToString(), source, path);
                }
            }
            catch (CoreException.Protected)
            {
                errorCount++;
                if (curRow.SubTb is not null)
                {
                    curRow.SubTb.Text = L10n.T("这本书有版权保护（DRM），打不开");
                    curRow.SubTb.Visibility = Visibility.Visible;
                }
                if (curRow.StatusHost is not null)
                    curRow.StatusHost.Content = MakeStatusError();
            }
            catch (Exception ex)
            {
                errorCount++;
                if (curRow.SubTb is not null)
                {
                    curRow.SubTb.Text = L10n.T(ex.Message);
                    curRow.SubTb.Visibility = Visibility.Visible;
                }
                if (curRow.StatusHost is not null)
                    curRow.StatusHost.Content = MakeStatusError();
            }

            rowIdx++;
            doneCount++;
            UpdateProgressVisuals();
        }

        foreach (var batch in audioBatches)
        {
            if (_closed) return;
            var curRow = !useInteractiveAudioSheet && rowIdx < rowViews.Count ? rowViews[rowIdx] : default;
            if (curRow.StatusHost is not null)
                curRow.StatusHost.Content = MakeStatusImporting();
            if (!useInteractiveAudioSheet)
                UpdateProgressVisuals();

            try
            {
                BookSummary? audioBook = useInteractiveAudioSheet
                    ? await AudiobookImporter.ShowImportSheetAsync(this, RootGrid.XamlRoot!, _library, batch)
                    : await AudiobookImporter.ImportFilesAsync(_library, batch);
                if (_closed) return;
                if (audioBook is not null)
                {
                    if (!string.IsNullOrEmpty(effectiveColId))
                        _library.SetInCollection(effectiveColId, audioBook.Id, true);
                    if (curRow.TitleTb is not null)
                        curRow.TitleTb.Text = audioBook.Title;

                    if (existingIds.Contains(audioBook.Id))
                    {
                        duplicates.Add(audioBook);
                        if (curRow.StatusHost is not null)
                            curRow.StatusHost.Content = MakeStatusDuplicate(audioBook.Id);
                    }
                    else
                    {
                        existingIds.Add(audioBook.Id);
                        imported.Add(audioBook);
                        if (curRow.StatusHost is not null)
                            curRow.StatusHost.Content = MakeStatusSuccess();
                        _library.Analytics.TrackBookImported(audioBook.Format.ToString(), source, batch.FirstOrDefault());
                    }
                }
            }
            catch (Exception ex)
            {
                errorCount++;
                if (curRow.SubTb is not null)
                {
                    curRow.SubTb.Text = L10n.T(ex.Message);
                    curRow.SubTb.Visibility = Visibility.Visible;
                }
                if (curRow.StatusHost is not null)
                    curRow.StatusHost.Content = MakeStatusError();
                else
                    ShowToast($"导入失败：{ex.Message}", canUndo: false, isDelete: true);
            }

            if (!useInteractiveAudioSheet)
            {
                rowIdx++;
                doneCount++;
                UpdateProgressVisuals();
            }
        }

        if (_closed) return;
        RefreshBooks();

        if (totalQueue >= 1 && headerTitleText is not null)
        {
            if (progressFill is not null)
                progressFill.Width = progressTrackWidth;

            if (errorCount == 0 && duplicates.Count == 0 && imported.Count > 0)
            {
                headerTitleText.Text = L10n.T(imported.Count == 1
                    ? $"已导入《{imported[0].Title}》"
                    : $"已导入 {imported.Count} 本书");
                _importCardDismissTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.4) };
                _importCardDismissTimer.Tick += (_, _) =>
                {
                    if (_importCardHoverPaused) return;
                    HideImportQueueCard();
                };
                if (!_importCardHoverPaused)
                    _importCardDismissTimer.Start();
            }
            else if (errorCount == 0 && imported.Count == 0 && duplicates.Count > 0)
            {
                headerTitleText.Text = L10n.T(duplicates.Count == 1
                    ? $"《{duplicates[0].Title}》已在书架里"
                    : $"{duplicates.Count} 本书已在书架里");
            }
            else if (errorCount > 0)
            {
                headerTitleText.Text = imported.Count > 0
                    ? L10n.T($"已导入 {imported.Count} 本 · {errorCount} 项失败")
                    : L10n.T($"导入失败 · 共 {errorCount} 项");
            }
            else
            {
                headerTitleText.Text = L10n.T($"已导入 {imported.Count} 本 · {duplicates.Count} 本已在书架");
            }

            if (openFirst)
            {
                var toOpen = imported.FirstOrDefault() ?? duplicates.FirstOrDefault();
                if (toOpen is not null) TryOpenBook(toOpen);
            }
            return;
        }

        if (imported.Count == 0 && duplicates.Count == 1 && errorCount == 0)
        {
            var dup = duplicates[0];
            ShowToast($"《{dup.Title}》已在书架里", canUndo: true, isDelete: false, undo: () => RevealAndHighlightBook(dup.Id), actionLabel: "在书架里显示");
            if (openFirst) TryOpenBook(dup);
        }
        else if (imported.Count > 0)
        {
            ShowToast(imported.Count == 1 ? $"已导入《{imported[0].Title}》" : $"已导入 {imported.Count} 本书", canUndo: false);
            if (openFirst) TryOpenBook(imported[0]);
        }
    }

    private async void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        string? name = await YYChrome.ShowPromptAsync(
            RootGrid.XamlRoot,
            dark,
            "新建合集",
            placeholder: "合集的名字，例如：睡前、科幻",
            confirmText: "好",
            cancelText: "取消",
            tint: _library.Prefs.Tint);
        if (!string.IsNullOrWhiteSpace(name))
        {
            try
            {
                var col = _library.CreateCollection(name.Trim());
                _cameFromCollectionsWall = _activeFilter == "collections";
                _activeFilter = $"col:{col.Id}";
                RefreshBooks();
            }
            catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
        }
    }

    private void OnDropLeaveTimerTick(object? sender, object e) => HideDropOverlay();

    private void HideDropOverlay()
    {
        _dropLeaveTimer?.Stop();
        _dragStorageCount = 0;
        _probingDragItems = false;
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void UpdateDropOverlayCopy()
    {
        string? colName = _activeFilter.StartsWith("col:", StringComparison.Ordinal)
            ? _collections.FirstOrDefault(c => c.Id == _activeFilter[4..])?.Name
            : null;

        if (!string.IsNullOrEmpty(colName))
        {
            DropOverlayTitleText.Text = L10n.T($"放进「{colName}」");
            DropOverlaySubtitleText.Text = _dragStorageCount > 1
                ? L10n.T($"松开即可导入这 {_dragStorageCount} 个文件")
                : L10n.T("书名、作者和封面从文件里读出来，之后都能改");
        }
        else
        {
            DropOverlayTitleText.Text = _dragStorageCount > 1
                ? L10n.T($"松开即可导入这 {_dragStorageCount} 个文件")
                : L10n.T("松开即可导入书库");
            DropOverlaySubtitleText.Text = L10n.T("书名、作者和封面从文件里读出来，之后都能改");
        }
    }

    private async Task ProbeDragStorageCountAsync(DataPackageView dataView)
    {
        try
        {
            var items = await dataView.GetStorageItemsAsync();
            if (_closed) return;
            _dragStorageCount = items.Count;
            if (DropOverlay.Visibility == Visibility.Visible)
                UpdateDropOverlayCopy();
        }
        catch { }
        finally
        {
            _probingDragItems = false;
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        _dropLeaveTimer?.Stop();
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (!_probingDragItems && _dragStorageCount == 0)
        {
            _probingDragItems = true;
            _ = ProbeDragStorageCountAsync(e.DataView);
        }
        if (DropOverlay.Visibility != Visibility.Visible)
        {
            DropOverlay.Visibility = Visibility.Visible;
            L10n.ApplyTree(DropOverlay);
        }
        UpdateDropOverlayCopy();
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e)
    {
        if (DropOverlay.Visibility != Visibility.Visible) return;
        var pt = e.GetPosition(RootGrid);
        const double edge = 8;
        if (pt.X <= edge || pt.Y <= edge || pt.X >= RootGrid.ActualWidth - edge || pt.Y >= RootGrid.ActualHeight - edge)
        {
            HideDropOverlay();
            return;
        }
        _dropLeaveTimer?.Stop();
        _dropLeaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _dropLeaveTimer.Tick -= OnDropLeaveTimerTick;
        _dropLeaveTimer.Tick += OnDropLeaveTimerTick;
        _dropLeaveTimer.Start();
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.Select(it => it.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            string? targetColId = _activeFilter.StartsWith("col:", StringComparison.Ordinal) ? _activeFilter[4..] : null;
            if (paths.Length > 0) await ImportPathsAsync(paths, openFirst: false, targetCollectionId: targetColId, source: "share_in");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void ToastClose_Click(object sender, RoutedEventArgs e)
    {
        _toastUndoAction = null;
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastUndoAction;
        _toastUndoAction = null;
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    private void ToastBanner_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _toastHoverPaused = true;
        _toastTimer?.Stop();
    }

    private void ToastBanner_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_toastHoverPaused) return;
        _toastHoverPaused = false;
        if (ToastBanner.Visibility == Visibility.Visible)
            _toastTimer?.Start();
    }

    public void ShowToast(
        string text,
        bool canUndo,
        bool isDelete = false,
        Action? undo = null,
        string? detail = null,
        string? actionLabel = null,
        string? iconKind = null)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        _toastHoverPaused = false;
        _toastUndoAction = canUndo ? undo : null;

        string resolvedIconKind = iconKind ?? (isDelete ? "danger" : "check");
        if (resolvedIconKind == "cloud")
        {
            ToastDisc.Width = 22;
            ToastDisc.Height = 22;
            ToastDisc.CornerRadius = new CornerRadius(11);
            ToastDisc.Background = ThemeHelper.Brush(p.Inset);
            ToastCheckIcon.Content = YYIcons.Make("icloud.and.arrow.down", 12, ThemeHelper.Brush(p.T2));
        }
        else if (resolvedIconKind == "danger")
        {
            ToastDisc.Width = 17;
            ToastDisc.Height = 17;
            ToastDisc.CornerRadius = new CornerRadius(8.5);
            ToastDisc.Background = ThemeHelper.Brush(p.Danger);
            ToastCheckIcon.Content = new TextBlock
            {
                Text = "!",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            };
        }
        else
        {
            ToastDisc.Width = 17;
            ToastDisc.Height = 17;
            ToastDisc.CornerRadius = new CornerRadius(8.5);
            ToastDisc.Background = ThemeHelper.Brush(Color.FromArgb(255, 45, 190, 96));
            ToastCheckIcon.Content = YYIcons.Make("checkmark", 9.5, new SolidColorBrush(Colors.White));
        }

        ToastText.Text = L10n.T(text);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            ToastDetailText.Text = L10n.T(detail);
            ToastDetailText.Visibility = Visibility.Visible;
        }
        else
        {
            ToastDetailText.Text = "";
            ToastDetailText.Visibility = Visibility.Collapsed;
        }

        ToastUndoButton.Content = L10n.T(actionLabel ?? "撤销");
        ToastUndoButton.Visibility = canUndo && undo is not null ? Visibility.Visible : Visibility.Collapsed;
        ToastBanner.Visibility = Visibility.Visible;
        YYChrome.AnimateEntrance(ToastBanner, fromY: 10f, durationMs: 165);

        _toastTimer?.Stop();
        double secs = (canUndo || !string.IsNullOrWhiteSpace(detail)) ? 6 : 3.2;
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(secs) };
        _toastTimer.Tick += (_, _) =>
        {
            if (_toastHoverPaused) return;
            _toastTimer.Stop();
            _toastUndoAction = null;
            ToastBanner.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }

    internal static string ShortAgo(long unixMs)
    {
        if (unixMs <= 0) return "";
        var diff = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (diff.TotalMinutes < 2) return "刚刚";
        if (diff.TotalHours < 1) return $"{Math.Max(1, (int)diff.TotalMinutes)} 分钟前";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 2) return "昨天";
        if (diff.TotalDays < 30) return $"{(int)diff.TotalDays} 天前";
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime;
        return dt.Year == DateTime.Now.Year ? $"{dt.Month}月{dt.Day}日" : $"{dt:yyyy/MM/dd}";
    }
}
