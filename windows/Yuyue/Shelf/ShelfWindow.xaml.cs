using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private string _activeFilter = "all"; // all | progress | podcasts | audio | notes | words | collections | col:<id>
    private string _sortKey = "recent";   // recent | added | title | author
    private string _collectionSort = "recent"; // recent | name | count
    private bool _sidebarVisible = true;
    private bool _readingExpanded;
    private bool _notesByBook;
    private string _notesChip = "all"; // all | thoughts | yellow | blue | pink | wave
    private string? _notesChosenBookId;
    private bool _wordsByBook;
    private bool _wordsShowKnown;
    private Action? _toastUndoAction;
    private DispatcherTimer? _toastTimer;

    public XamlRoot WindowXamlRoot => RootGrid.XamlRoot;

    public ShelfWindow(LibraryService library)
    {
        _library = library;
        _recordBuilder = new BookRecordPageBuilder(this, library);
        _podcastBuilder = new PodcastWorkspaceBuilder(this, library);
        _sidebarVisible = _library.Prefs.ShelfSidebarShown;
        InitializeComponent();

        ThemeHelper.ResizeWindow(this, 1040, 720);
        SetTitleBar(TopDragBar);
        ThemeHelper.ApplyElevation(PaperSheet, DeskShadowReceiver, 20f);
        ThemeHelper.ApplyElevation(ToastBanner, PaperInnerGrid, 12f);

        if (!_sidebarVisible)
        {
            SidebarColumn.Width = new GridLength(0);
            SidebarPanel.Visibility = Visibility.Collapsed;
            PaperSheet.Margin = new Thickness(8, 0, 8, 8);
        }

        Closed += (_, _) => _podcastBuilder.Dispose();
        PaperSheet.SizeChanged += (_, _) =>
        {
            if (_activeFilter is "all" or "audio" || _activeFilter.StartsWith("col:", StringComparison.Ordinal))
                RenderShelfMain();
        };

        MountStaticIcons();
        ApplyAppearance();
        RefreshBooks();
        UpdateSyncFooter();
    }

    public void RefreshCurrentSubPage()
    {
        ConfigureHeaderForCurrentView();
        RenderCurrentView();
    }

    public void OpenPodcastInline(string issueId)
    {
        _podcastBuilder.OpenIssueId = issueId;
        _activeFilter = "podcasts";
        UpdateNavSelectionVisuals();
        RenderCurrentView();
    }

    public Task OpenStudioSheetForBookAsync(string bookId) =>
        _podcastBuilder.ShowStudioSheetAsync(StudioKind.BookGuide, bookId);

    public async Task PromptAddRecordBookAsync(ReadingStatus defaultStatus)
    {
        var added = await CustomPaperDialog.ShowAddRecordBookDialogAsync(RootGrid.XamlRoot, _library);
        if (added is not null)
        {
            _recordBuilder.ActiveStatus = added.Status;
            RefreshBooks();
        }
    }

    public void UpdateSyncFooter()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        SyncStatusText.Text = _library.Sync.StatusSummary();
        Color dotColor = _library.Sync.State switch
        {
            SyncStateKind.Syncing => tint.Accent,
            SyncStateKind.Failed => p.Danger,
            _ when _library.Sync.IsEnabled => p.Ok,
            _ => p.T3,
        };
        SyncDot.Fill = ThemeHelper.Brush(dotColor);
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
        EmptyIconHost.Content = YYIcons.Make("shelf", 34, t3);
        ToastCheckIcon.Content = YYIcons.Make("checkmark", 9.5, new SolidColorBrush(Colors.White));
        ToastUndoButton.Content = YYIcons.Make("arrow-undo", 11.5, t1);
        DropArrowIcon.Content = YYIcons.Make("arrow-down", 24, ThemeHelper.Brush(ThemeHelper.TintColors(_library.Prefs.Tint, dark).Accent));

        IconAll.Content = YYIcons.Make("shelf", 13.5, _activeFilter == "all" ? t1 : t2);
        IconProgress.Content = YYIcons.Make("book", 14, _activeFilter == "progress" ? t1 : t2);
        IconPodcasts.Content = YYIcons.Make("radio", 14, _activeFilter == "podcasts" ? t1 : t2);
        IconAudio.Content = YYIcons.Make("wave", 14, _activeFilter == "audio" ? t1 : t2);
        IconNotes.Content = YYIcons.Make("highlighter", 14, _activeFilter == "notes" ? t1 : t2);
        IconWords.Content = YYIcons.Make("a-wen", 14, _activeFilter == "words" ? t1 : t2);
        IconCollections.Content = YYIcons.Make("stack", 14, _activeFilter == "collections" ? t1 : t2);
    }

    public void ApplyAppearance()
    {
        RootGrid.RequestedTheme = _library.Prefs.ToElementTheme();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        ThemeHelper.ConfigureTitleBar(this, dark);
        MountStaticIcons();
        UpdateSyncFooter();

        double coverW = Math.Clamp(_library.Prefs.CoverWidth, 76, 148);
        BooksGridLayout.MinItemWidth = coverW;
        BooksGridLayout.MinItemHeight = coverW * 1.5 + (_library.Prefs.ShowProgress ? 48 : 32);

        RenderCurrentView();
    }

    public void RefreshBooks()
    {
        _shelfBooks = _library.GetShelfBooks();
        _keptBooks = _library.GetKeptBooks();
        _collections = _library.GetCollections();
        _markCounts = _library.GetMarkCounts();
        _words = _library.GetWords();

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
        try { podcastCount = _library.Podcasts.GetIssues().Count; } catch { }
        CountPodcasts.Text = CountText(podcastCount);
        CountAudio.Text = CountText(_shelfBooks.Count(b => b.Format == BookFormat.Audio));

        int totalNotes = 0;
        foreach (var b in _keptBooks)
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

        // 1:1 对齐 MacShelfView.swift lines 359-370：侧栏仅展示已固定的合集；未固定合集打开时高亮「合集 N ›」
        foreach (var col in _collections.Where(c => _library.Prefs.PinnedCollectionIds.Contains(c.Id)))
        {
            var colItem = col;
            var tag = $"col:{colItem.Id}";
            var grid = new Grid { ColumnSpacing = 8 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = YYIcons.Make(YYIcons.CollectionIconName(colItem.Name, colItem.Icon), 13, t2);
            var name = new TextBlock { Text = colItem.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
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
            var pinItem = new MenuFlyoutItem { Text = "取消固定" };
            pinItem.Click += (_, _) =>
            {
                _library.Prefs.PinnedCollectionIds.Remove(colItem.Id);
                _library.SavePrefs();
                RefreshBooks();
            };
            var renameItem = new MenuFlyoutItem { Text = "重命名…" };
            renameItem.Click += async (_, _) => await RenameCollectionAsync(colItem);
            var delItem = new MenuFlyoutItem { Text = "删除合集…", Foreground = ThemeHelper.Brush(p.Danger) };
            delItem.Click += async (_, _) => await ConfirmDeleteCollectionAsync(colItem);
            ctx.Items.Add(pinItem);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(renameItem);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(delItem);

            var btn = new Button
            {
                Style = (Style)Application.Current.Resources["YYSidebarItemStyle"],
                Tag = tag,
                Content = grid,
                ContextFlyout = ctx,
            };
            btn.Click += NavItem_Click;
            CollectionsStack.Children.Add(btn);
        }
    }

    private async Task RenameCollectionAsync(BookCollection col)
    {
        var input = new TextBox { Text = col.Name, PlaceholderText = "合集的名字" };
        var dlg = new ContentDialog
        {
            Title = "重命名合集",
            Content = input,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            try
            {
                _library.Core.RenameCollection(col.Id, input.Text.Trim());
                RefreshBooks();
            }
            catch (Exception ex) { ShowToast(ex.Message, canUndo: false); }
        }
    }

    private async Task ConfirmDeleteCollectionAsync(BookCollection col)
    {
        var dlg = new ContentDialog
        {
            Title = $"删除合集“{col.Name}”？",
            Content = "这个合集删掉，里面的书还在书架上。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            if (_activeFilter == $"col:{col.Id}") _activeFilter = "all";
            _library.Prefs.PinnedCollectionIds.Remove(col.Id);
            _library.SavePrefs();
            _library.DeleteCollection(col.Id);
            RefreshBooks();
            ShowToast($"已删除合集“{col.Name}”", canUndo: false, isDelete: true);
        }
    }

    private void UpdateNavSelectionVisuals()
    {
        var selectedBg = (Brush)Application.Current.Resources["YYSidebarSelectedBrush"];
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
        if (dock is null)
        {
            SubPageBottomDockHost.Content = null;
            SubPageBottomDockHost.Visibility = Visibility.Collapsed;
            SubPageScroll.Padding = new Thickness(22, 4, 28, 40);
            return;
        }
        ThemeHelper.ApplyElevation(dock, PaperInnerGrid, 14f);
        SubPageBottomDockHost.Content = dock;
        SubPageBottomDockHost.Visibility = Visibility.Visible;
        SubPageScroll.Padding = new Thickness(22, 4, 28, 86);
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
    }

    private void ConfigureHeaderForCurrentView()
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        var t1 = ThemeHelper.Brush(p.T1);

        HeaderLeftCustomHost.Children.Clear();
        HeaderRightHost.Children.Clear();
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

                HeaderRightHost.Children.Add(BuildHeaderSegments(_notesByBook, "按时间", "按书", byBook =>
                {
                    _notesByBook = byBook;
                    _notesChip = "all";
                    ConfigureHeaderForCurrentView();
                    RenderNotesPage();
                }));

                var expSp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
                expSp.Children.Add(YYIcons.Make("share-up", 11, t1));
                expSp.Children.Add(new TextBlock { Text = "导出", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var noteMenu = new MenuFlyout();
                var chosenBook = _notesByBook ? _keptBooks.FirstOrDefault(b => b.Id == _notesChosenBookId) : null;

                bool anyConnected = false;
                if (_library.Prefs.ObsidianExportOn && !string.IsNullOrWhiteSpace(_library.Prefs.ObsidianFolder) && Directory.Exists(_library.Prefs.ObsidianFolder))
                {
                    anyConnected = true;
                    var obsItem = new MenuFlyoutItem { Text = "发到 Obsidian 库" };
                    obsItem.Click += async (_, _) =>
                    {
                        try
                        {
                            var (booksN, marksN) = await _library.NoteExports.ExportToObsidianAsync(chosenBook);
                            ShowToast($"已发送 {marksN} 条笔记到 Obsidian（{booksN} 本书）", canUndo: false);
                        }
                        catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
                    };
                    noteMenu.Items.Add(obsItem);
                }

                if (_library.Prefs.NotionExportOn && !string.IsNullOrWhiteSpace(_library.Secrets.Get("export.notion.token")))
                {
                    anyConnected = true;
                    var notItem = new MenuFlyoutItem { Text = "发到 Notion" };
                    notItem.Click += async (_, _) =>
                    {
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToNotionAsync(chosenBook);
                            ShowToast(marksN > 0 ? $"已增量同步 {marksN} 条新笔记到 Notion" : "Notion 已是最新，没有新笔记需要发送", canUndo: false);
                        }
                        catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
                    };
                    noteMenu.Items.Add(notItem);
                }

                if (_library.Prefs.FlomoExportOn && NoteExportService.ValidateFlomoWebhook(_library.Secrets.Get("export.flomo.url") is { Length: > 0 } fu ? fu : _library.Prefs.FlomoWebhookUrl))
                {
                    anyConnected = true;
                    var floItem = new MenuFlyoutItem { Text = "发到 flomo" };
                    floItem.Click += async (_, _) =>
                    {
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToFlomoAsync(chosenBook);
                            ShowToast(marksN > 0 ? $"已发送 {marksN} 条卡片到 flomo" : "flomo 已是最新，没有未发过的划线", canUndo: false);
                        }
                        catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
                    };
                    noteMenu.Items.Add(floItem);
                }

                if (_library.Prefs.XinzhiExportOn && !string.IsNullOrWhiteSpace(_library.Secrets.Get("export.xinzhi.token")))
                {
                    anyConnected = true;
                    var xzItem = new MenuFlyoutItem { Text = "发到 新枝" };
                    xzItem.Click += async (_, _) =>
                    {
                        try
                        {
                            var (_, marksN) = await _library.NoteExports.ExportToXinzhiAsync(chosenBook);
                            ShowToast(marksN > 0 ? $"已发送 {marksN} 条笔记到新枝" : "新枝已是最新，没有未发过的划线", canUndo: false);
                        }
                        catch (Exception ex) { ShowToast(ex.Message, canUndo: false, isDelete: true); }
                    };
                    noteMenu.Items.Add(xzItem);
                }

                if (!anyConnected)
                {
                    var cfgObs = new MenuFlyoutItem { Text = "连接 Obsidian / Notion / flomo / 新枝…" };
                    cfgObs.Click += (_, _) => App.CurrentApp.OpenSettings("export");
                    noteMenu.Items.Add(cfgObs);
                }

                noteMenu.Items.Add(new MenuFlyoutSeparator());

                if (chosenBook is not null && chosenBook.Format is not BookFormat.Pdf and not BookFormat.Audio && !chosenBook.Record)
                {
                    var epubItem = new MenuFlyoutItem { Text = "导出带笔记的 EPUB (.epub)…" };
                    epubItem.Click += async (_, _) =>
                    {
                        var picker = new FileSavePicker { SuggestedFileName = $"{chosenBook.Title}（带笔记）" };
                        picker.FileTypeChoices.Add("EPUB 电子书", [".epub"]);
                        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                        var f = await picker.PickSaveFileAsync();
                        if (f is null) return;
                        try
                        {
                            await _library.NoteExports.ExportAnnotatedEpubAsync(chosenBook.Id, f.Path);
                            ShowToast($"已导出 {f.Name}", canUndo: false);
                        }
                        catch (Exception ex) { ShowToast($"导出失败：{ex.Message}", canUndo: false, isDelete: true); }
                    };
                    noteMenu.Items.Add(epubItem);
                }

                var mdItem = new MenuFlyoutItem { Text = chosenBook is not null ? "导出这本书的 Markdown (.md)…" : "导出全部 Markdown (.md)…" };
                mdItem.Click += async (_, _) =>
                {
                    var picker = new FileSavePicker { SuggestedFileName = chosenBook is not null ? $"{chosenBook.Title}-笔记" : $"语阅笔记-{DateTime.Now:yyyyMMdd}" };
                    picker.FileTypeChoices.Add("Markdown", [".md"]);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var f = await picker.PickSaveFileAsync();
                    if (f is null) return;
                    string md = chosenBook is not null ? NoteExportService.BuildObsidianBookMarkdown(_library, chosenBook) : NoteExporter.BuildLibraryMarkdown(_library);
                    await File.WriteAllTextAsync(f.Path, md);
                    ShowToast($"已导出笔记到 {f.Name}", canUndo: false);
                };
                noteMenu.Items.Add(mdItem);

                HeaderRightHost.Children.Add(new Button
                {
                    Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                    Content = expSp,
                    Flyout = noteMenu,
                    IsEnabled = totalMarks > 0,
                });
                break;
            }

            case "words":
            {
                int learning = _words.Count(w => !w.Known);
                int known = _words.Count(w => w.Known);
                if (_words.Count > 0) ViewSubtitleText.Text = $"在学 {learning} · 已掌握 {known}";

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
                    var picker = new FileSavePicker { SuggestedFileName = "语阅生词 Anki" };
                    picker.FileTypeChoices.Add("Anki 制表符卡片", [".txt"]);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var f = await picker.PickSaveFileAsync();
                    if (f is null) return;
                    var sb = new System.Text.StringBuilder("#separator:tab\n#html:true\n#tags column:3\n");
                    foreach (var w in _words.Where(w => w.Known == _wordsShowKnown))
                    {
                        string front = $"<b>{System.Net.WebUtility.HtmlEncode(w.Word)}</b>" + (string.IsNullOrEmpty(w.Phonetic) ? "" : $" /{System.Net.WebUtility.HtmlEncode(w.Phonetic.Trim('/'))}/");
                        string back = System.Net.WebUtility.HtmlEncode(w.Meaning ?? "") + (string.IsNullOrEmpty(w.Context) ? "" : $"<br><i>{System.Net.WebUtility.HtmlEncode(w.Context)}</i>");
                        sb.AppendLine($"{front}\t{back}\t语阅");
                    }
                    await File.WriteAllTextAsync(f.Path, sb.ToString());
                    ShowToast($"已导出 Anki 卡片到 {f.Name}", canUndo: false);
                };
                var csvItem = new MenuFlyoutItem { Text = "CSV 表格 (.csv)…" };
                csvItem.Click += async (_, _) =>
                {
                    var picker = new FileSavePicker { SuggestedFileName = "语阅生词" };
                    picker.FileTypeChoices.Add("CSV", [".csv"]);
                    InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
                    var f = await picker.PickSaveFileAsync();
                    if (f is null) return;
                    await File.WriteAllTextAsync(f.Path, NoteExporter.BuildAnkiCsv(_library), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                    ShowToast($"已导出 CSV 到 {f.Name}", canUndo: false);
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
                HeaderRightHost.Children.Add(expWordBtn);
                break;
            }

            case "collections":
                HeaderRightHost.Children.Add(PillWithIcon("plus", "新建合集", NewCollection_Click));
                break;

            default:
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
        if (_activeFilter == "audio") source = source.Where(b => b.Format == BookFormat.Audio);
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
                var single = CreateReadingCard(reading[0], tinted: true);
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
                    var cardEl = CreateReadingCard(reading[i], tinted: i == 0);
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
            BooksRepeater.ItemsSource = null;
            EmptyStatePanel.Visibility = Visibility.Visible;
            EmptyTitleText.Text = _activeFilter == "audio"
                ? "还没有有声书。把 M4B、MP3、M4A 拖进窗口，或点右上角「+ 导入」"
                : _activeFilter.StartsWith("col:", StringComparison.Ordinal)
                    ? "还没有书。把文件拖进来，或在书的右键菜单里加入这个合集"
                    : "书架是空的，把 EPUB、PDF、MOBI、TXT 拖进窗口就能开始读";
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            BooksRepeater.ItemsSource = rest.Select(CreateBookCell).ToArray();
        }
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
            BorderThickness = new Thickness(1),
        };
        ThemeHelper.ApplyElevation(card, PaperInnerGrid, 10f);

        // 首卡异步提取封面主色染色（移植自 Mac CoverHue.load）
        if (tinted && !string.IsNullOrEmpty(book.CoverPath))
        {
            _ = TintLeadCardAsync(card, book.CoverPath, dark);
        }

        var outer = new Grid();
        var row = new Grid { Padding = new Thickness(11, 10, 8, 12), ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(CreateCoverView(book, 46, 64, elevated: false));

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2.5 };
        info.Children.Add(new TextBlock
        {
            Text = book.Title,
            FontSize = 13.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = titleBrush,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var author = book.Authors.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(author))
        {
            info.Children.Add(new TextBlock { Text = author, FontSize = 11.5, Foreground = authorBrush, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
        }

        var pct = (int)Math.Round(book.Progress * 100);
        var metaLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 1, 0, 0) };
        metaLine.Children.Add(new TextBlock { Text = book.Format == BookFormat.Audio ? "有声 ·" : "图书 ·", FontSize = 11, Foreground = metaBrush });
        metaLine.Children.Add(new TextBlock { Text = pct > 0 ? $"{pct}%" : "刚开始", FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = pctBrush });
        if (FormatRemainingTail(book) is string tail)
        {
            metaLine.Children.Add(new TextBlock { Text = $"· {tail}", FontSize = 11, Foreground = metaBrush });
        }
        info.Children.Add(metaLine);
        Grid.SetColumn(info, 1);
        row.Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var listenBtn = new Button { Style = (Style)Application.Current.Resources["YYIconButtonStyle"], Content = YYIcons.Make("headphones", 13.5, iconBrush) };
        ToolTipService.SetToolTip(listenBtn, "听");
        listenBtn.Click += (_, _) => App.CurrentApp.OpenBook(book.Id, autoListen: true);

        var moreBtn = new Button
        {
            Style = (Style)Application.Current.Resources["YYIconButtonStyle"],
            Foreground = iconBrush,
            Content = new TextBlock { Text = "···", FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.Bold },
            Flyout = BuildBookMenuFlyout(book),
        };
        ToolTipService.SetToolTip(moreBtn, "更多");

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
        hitBtn.Click += async (_, _) =>
        {
            if (book.Record)
            {
                if (await CustomPaperDialog.ShowBookRecordDialogAsync(RootGrid.XamlRoot, _library, book))
                    RefreshBooks();
            }
            else
            {
                App.CurrentApp.OpenBook(book.Id);
            }
        };

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
        var trackWrap = new Grid { Height = 2.5, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false };
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
        DispatcherQueue.TryEnqueue(() => card.Background = ThemeHelper.Brush(color));
    }

    private UIElement CreateBookCell(BookSummary book)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        double w = Math.Clamp(_library.Prefs.CoverWidth, 76, 148);
        double h = Math.Round(w * 1.5);

        var root = new StackPanel { Width = w, Spacing = 0 };

        var cover = CreateCoverView(book, w, h, elevated: true);
        var coverBtn = new Button
        {
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            UseSystemFocusVisuals = false,
            CanDrag = true,
            Content = cover,
            ContextFlyout = BuildBookMenuFlyout(book),
        };
        string dragBookId = book.Id;
        coverBtn.DragStarting += (_, e) =>
        {
            e.Data.SetText("yuyue-book:" + dragBookId);
            e.Data.RequestedOperation = DataPackageOperation.Copy;
        };
        coverBtn.Click += (_, _) => App.CurrentApp.OpenBook(book.Id);
        root.Children.Add(coverBtn);

        root.Children.Add(new TextBlock
        {
            Text = book.Title,
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 9, 0, 2),
        });

        if (_library.Prefs.ShowProgress)
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

        return root;
    }

    public Border CreateCoverView(BookSummary book, double width, double height, bool elevated)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        bool isAudio = book.Format == BookFormat.Audio;
        double actualH = isAudio ? width : height;

        var inner = new Border
        {
            Width = width,
            Height = actualH,
            VerticalAlignment = isAudio ? VerticalAlignment.Bottom : VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(isAudio ? Math.Max(5, width * 0.08) : 4),
            BorderBrush = ThemeHelper.Brush(dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0)),
            BorderThickness = new Thickness(0.5),
            Background = ThemeHelper.Brush(isAudio ? palette.Inset : (dark ? Color.FromArgb(255, 44, 42, 39) : Color.FromArgb(255, 238, 234, 226))),
            Opacity = book.Available ? 1.0 : 0.45,
        };
        if (elevated) ThemeHelper.ApplyElevation(inner, PaperInnerGrid, 10f);

        var layer = new Grid();

        if (!string.IsNullOrEmpty(book.CoverPath) && File.Exists(book.CoverPath))
        {
            layer.Children.Add(new Image { Source = new BitmapImage(new Uri(book.CoverPath)), Stretch = Stretch.UniformToFill });
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
            var face = new Grid { Padding = new Thickness(12 * unit, 11 * unit, 10 * unit, 10 * unit) };
            var stack = new StackPanel { Spacing = 5 * unit };
            stack.Children.Add(new TextBlock
            {
                Text = book.Title,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = Math.Max(9, 14 * unit),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (book.Authors.Length > 0 && width >= 60)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = book.Authors[0],
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = Math.Max(8, 9.5 * unit),
                    Foreground = ThemeHelper.Brush(palette.T2),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            face.Children.Add(stack);

            if (book.Format == BookFormat.Pdf)
            {
                face.Children.Add(new TextBlock
                {
                    Text = "PDF",
                    FontSize = Math.Max(7, 8.5 * unit),
                    Foreground = ThemeHelper.Brush(palette.T2),
                    VerticalAlignment = VerticalAlignment.Bottom,
                });
            }
            layer.Children.Add(face);
        }

        // 右下角有声徽标 / 异地云端徽标（对齐 MacShelfView.swift:1040-1051）
        if (width >= 60 && !book.Available)
        {
            layer.Children.Add(new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(palette.Card),
                BorderBrush = ThemeHelper.Brush(palette.Line),
                BorderThickness = new Thickness(0.5),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 5, 5),
                Child = YYIcons.Make("cloud", 9.5, ThemeHelper.Brush(palette.T2)),
            });
        }
        else if (width >= 60 && isAudio)
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
        var flyout = new Flyout();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var root = new StackPanel { Width = 214, Spacing = 4 };

        // Header（无小封面、无三宫格方块，1:1 BookMenu.swift:122-150）
        var head = new StackPanel { Spacing = 4, Padding = new Thickness(8, 4, 8, 6) };
        head.Children.Add(new TextBlock
        {
            Text = book.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = ThemeHelper.Brush(palette.T1),
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (book.Authors.Length > 0)
        {
            head.Children.Add(new TextBlock
            {
                Text = string.Join("、", book.Authors),
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        if (book.Progress > 0.002 && book.Status != ReadingStatus.Finished)
        {
            double cl = Math.Clamp(book.Progress, 0.02, 1.0);
            var progGrid = new Grid { Height = 2.5, Margin = new Thickness(0, 3, 0, 1) };
            progGrid.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(palette.Inset) });
            var fg = new Grid();
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(cl, GridUnitType.Star) });
            fg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 1.0 - cl), GridUnitType.Star) });
            fg.Children.Add(new Border { CornerRadius = new CornerRadius(1.25), Background = ThemeHelper.Brush(tint.Fill) });
            progGrid.Children.Add(fg);
            head.Children.Add(progGrid);
        }

        string remLine = book.Status == ReadingStatus.Finished
            ? "读完了"
            : FormatRemainingTail(book) ?? (book.Progress > 0 ? $"{Math.Max(1, (int)Math.Round(book.Progress * 100))}%" : "还没读");
        head.Children.Add(new TextBlock { Text = remLine, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T2) });
        root.Children.Add(head);

        Border Hairline() => new() { Height = 0.5, Background = ThemeHelper.Brush(palette.Line), Margin = new Thickness(8, 2, 8, 2) };

        Button MenuRow(string label, string rightIcon, Action onClick, bool accent = false, bool danger = false)
        {
            var fg = danger ? ThemeHelper.Brush(palette.Danger)
                : accent ? ThemeHelper.Brush(tint.Accent)
                : ThemeHelper.Brush(palette.T1);
            var iconBrush = danger ? ThemeHelper.Brush(palette.Danger)
                : accent ? ThemeHelper.Brush(tint.Accent)
                : ThemeHelper.Brush(palette.T3);

            var g = new Grid { ColumnSpacing = 8 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                FontWeight = accent ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var ic = YYIcons.Make(rightIcon, 12.5, iconBrush);
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
                Padding = new Thickness(8, 0, 8, 0),
                UseSystemFocusVisuals = false,
                Content = g,
            };
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

        // Section 2：打开 / 朗读（纸书记录除外）
        if (!book.Record && book.Available)
        {
            root.Children.Add(Hairline());
            var openSec = new StackPanel { Spacing = 1 };
            bool isAudio = book.Format == BookFormat.Audio;
            string openLbl = isAudio
                ? (book.Progress > 0.005 ? "接着听" : "开始听")
                : (book.Progress > 0.005 ? "接着读" : "开始读");
            openSec.Children.Add(MenuRow(openLbl, isAudio ? "headphones" : "book", () => App.CurrentApp.OpenBook(book.Id), accent: true));
            openSec.Children.Add(MenuRow("从头听", "headphones", () => App.CurrentApp.OpenBook(book.Id, autoListen: true, section: 0, sentence: 0)));
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

        flyout.Content = root;
        return flyout;
    }

    private async Task HandleDeleteBookAsync(BookSummary book)
    {
        bool hasRecordData = (_markCounts.TryGetValue(book.Id, out var mc) && mc > 0)
            || book.Rating is > 0
            || !string.IsNullOrWhiteSpace(book.Comment)
            || book.Status == ReadingStatus.Finished;

        if (!book.Record && hasRecordData)
        {
            var dlg = new ContentDialog
            {
                Title = $"删除《{book.Title}》？",
                Content = "这本书里有你的阅读记录或笔记，可以只删书文件、在「进度」里留下记录。",
                PrimaryButtonText = "只删文件，留下记录",
                SecondaryButtonText = "连记录一起删",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = RootGrid.XamlRoot,
            };
            var res = await dlg.ShowAsync();
            if (res == ContentDialogResult.None) return;
            if (res == ContentDialogResult.Primary)
            {
                try { _library.Core.KeepRecordOnly(book.Id); } catch { }
                RefreshBooks();
                ShowToast($"已移除《{book.Title}》的文件，保留阅读记录", canUndo: false);
                return;
            }
        }

        string bid = book.Id;
        string bTitle = book.Title;
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

        byte[]? pickedCoverBytes = null;
        var coverPreviewHost = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Content = CreateCoverView(book, 58, 84, elevated: false),
        };
        var pickCoverBtn = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(10, 4, 10, 4),
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(tint.Accent),
            UseSystemFocusVisuals = false,
            Content = "换封面",
        };
        pickCoverBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var f = await picker.PickSingleFileAsync();
            if (f is not null)
            {
                pickedCoverBytes = await File.ReadAllBytesAsync(f.Path);
                coverPreviewHost.Content = new Border
                {
                    Width = 58,
                    Height = 84,
                    CornerRadius = new CornerRadius(4),
                    Child = new Image { Source = new BitmapImage(new Uri(f.Path)), Stretch = Stretch.UniformToFill },
                };
                pickCoverBtn.Content = "已换新封面";
            }
        };

        var titleBox = new TextBox { Header = "书名", PlaceholderText = "留空就用文件里的", Text = book.Title };
        var authorBox = new TextBox { Header = "作者", PlaceholderText = "几位作者用顿号隔开", Text = string.Join("、", book.Authors) };
        var coverNote = new TextBlock
        {
            Text = "只改书架上显示的名字，书的文件不动。",
            FontSize = 11.5,
            Foreground = ThemeHelper.Brush(p.T3),
            TextWrapping = TextWrapping.Wrap,
        };

        var dialog = new ContentDialog
        {
            Title = "编辑信息",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 340,
                Children = { coverPreviewHost, pickCoverBtn, titleBox, authorBox, coverNote },
            },
            PrimaryButtonText = "好",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var newTitle = titleBox.Text.Trim();
            var newAuthors = authorBox.Text
                .Split(['、', ',', '，', ';', '；', '/'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
            if (!string.IsNullOrEmpty(newTitle))
                _library.EditBookInfo(book.Id, newTitle, newAuthors);
            if (pickedCoverBytes is not null)
                _library.SetCover(book.Id, pickedCoverBytes);
            RefreshBooks();
            ShowToast("书籍信息已更新", canUndo: false);
        }
    }

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
        var searchBox = new TextBox { PlaceholderText = "找合集，或打个新名字回车创建…" };
        var rowsHost = new StackPanel { Spacing = 4 };

        void RebuildRows(string filter)
        {
            rowsHost.Children.Clear();
            string q = filter.Trim();
            var cols = _library.GetCollections()
                .Where(c => string.IsNullOrEmpty(q) || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => inSet.Contains(c.Id))
                .ThenByDescending(c => c.UsedAt)
                .ToArray();

            if (!string.IsNullOrEmpty(q) && !_library.GetCollections().Any(c => string.Equals(c.Name, q, StringComparison.OrdinalIgnoreCase)))
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
                    Padding = new Thickness(8, 6, 10, 6),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = createGrid,
                };
                createBtn.Click += (_, _) =>
                {
                    try
                    {
                        var created = _library.CreateCollection(q, book.Id);
                        inSet.Add(created.Id);
                        searchBox.Text = "";
                        RebuildRows("");
                        RefreshBooks();
                    }
                    catch { }
                };
                rowsHost.Children.Add(createBtn);
            }

            foreach (var col in cols)
            {
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
                    Background = ThemeHelper.Brush(p.Inset),
                    Child = YYIcons.Make(YYIcons.CollectionIconName(col.Name, col.Icon), 13, ThemeHelper.Brush(p.T1)),
                });
                var m = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                m.Children.Add(new TextBlock { Text = col.Name, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1) });
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
                    Padding = new Thickness(8, 6, 10, 6),
                    CornerRadius = new CornerRadius(10),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(inside ? tint.Edge : p.Line),
                    BorderThickness = new Thickness(1),
                    UseSystemFocusVisuals = false,
                    Content = g,
                };
                rowBtn.Click += (_, _) =>
                {
                    bool next = !inSet.Contains(cid);
                    if (next) inSet.Add(cid); else inSet.Remove(cid);
                    _library.SetInCollection(cid, book.Id, next);
                    RebuildRows(searchBox.Text);
                    RefreshBooks();
                };
                rowsHost.Children.Add(rowBtn);
            }
        }

        searchBox.TextChanged += (_, _) => RebuildRows(searchBox.Text);
        RebuildRows("");

        var dlg = new ContentDialog
        {
            Title = "加入合集",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 320,
                Children =
                {
                    new TextBlock { Text = book.Title, FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis },
                    searchBox,
                    new ScrollViewer { MaxHeight = 280, Content = rowsHost },
                },
            },
            CloseButtonText = "完成",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlg.ShowAsync();
    }

    // 1:1 对齐 Shelf.swift lines 54-64 & 260-269
    private static string FormatShelfNote(BookSummary book)
    {
        if (!book.Available) return "在别的设备上";
        int pct = (int)Math.Round(book.Progress * 100);
        if (book.Format == BookFormat.Audio)
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
        if (mark.Style is MarkStyle.Wave or MarkStyle.Underline or MarkStyle.Dotted)
        {
            Color lineCol = mark.Style == MarkStyle.Wave ? palette.HlR : palette.T2;
            var sp = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Left };
            sp.Children.Add(new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = fontSize * 1.65,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
            });
            sp.Children.Add(new Border
            {
                Height = mark.Style == MarkStyle.Dotted ? 1.0 : 1.5,
                CornerRadius = new CornerRadius(0.75),
                Background = ThemeHelper.Brush(lineCol),
                Opacity = mark.Style == MarkStyle.Dotted ? 0.55 : 0.85,
            });
            return sp;
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
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 2, 5, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = mark.Text,
                FontFamily = serif,
                FontSize = fontSize,
                LineHeight = fontSize * 1.65,
                Foreground = ThemeHelper.Brush(palette.T1),
                TextWrapping = TextWrapping.Wrap,
            },
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

    private void RenderNotesPage()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var bookNotes = _keptBooks
            .Select(b => (Book: b, Marks: _library.GetMarks(b.Id)))
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
            return;
        }

        if (_notesChosenBookId is null || !bookNotes.Any(b => b.Book.Id == _notesChosenBookId))
            _notesChosenBookId = bookNotes[0].Book.Id;

        // 1:1 ConflictSheet.swift: 存在多设备想法冲突时在笔记页顶部展示解决入口横幅
        try
        {
            var conflicts = _library.Core.NoteConflicts();
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

        bool MatchesChip(Mark m) => _notesChip switch
        {
            "thoughts" => !string.IsNullOrWhiteSpace(m.Note),
            "yellow" => m.Style == MarkStyle.Yellow,
            "blue" => m.Style == MarkStyle.Blue,
            "pink" => m.Style == MarkStyle.Pink,
            "wave" => m.Style == MarkStyle.Wave,
            _ => true,
        };

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
            if (!_notesByBook) AddChip("thoughts", "有想法", scopeMarks.Count(m => !string.IsNullOrWhiteSpace(m.Note)));
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
        string GetChapterTitle(string bookId, uint sec)
        {
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
            entry.Children.Add(BuildHighlightedQuote(mark, palette, dark, 14));

            if (!string.IsNullOrWhiteSpace(mark.Note))
            {
                var thoughtRow = new Grid { ColumnSpacing = 9, Margin = new Thickness(2, 2, 0, 0) };
                thoughtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                thoughtRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                thoughtRow.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                var tb = new TextBlock
                {
                    Text = mark.Note,
                    FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()),
                    FontSize = 13,
                    Foreground = ThemeHelper.Brush(tint.Accent),
                    TextWrapping = TextWrapping.Wrap,
                };
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
                left.Children.Add(new TextBlock
                {
                    Text = $"{book.Title} · {GetChapterTitle(book.Id, mark.Section)}",
                    FontSize = 11,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(palette.T2),
                    VerticalAlignment = VerticalAlignment.Center,
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                footer.Children.Add(left);
            }
            footer.Children.Add(new TextBlock
            {
                Text = showBookFooter ? dt.ToString("HH:mm") : $"{dt.Month} 月 {dt.Day} 日",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(palette.T3),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            });
            entry.Children.Add(footer);

            var ctx = new MenuFlyout();
            var jumpItem = new MenuFlyoutItem { Text = "回到书里这一页" };
            jumpItem.Click += (_, _) => App.CurrentApp.OpenBook(book.Id, section: mark.Section, sentence: (int)mark.Start.Sentence);

            var editNoteItem = new MenuFlyoutItem { Text = string.IsNullOrWhiteSpace(mark.Note) ? "写想法…" : "改想法…" };
            editNoteItem.Click += async (_, _) =>
            {
                var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, Text = mark.Note ?? "", PlaceholderText = "写下此刻的想法…" };
                var dlg = new ContentDialog
                {
                    Title = string.IsNullOrWhiteSpace(mark.Note) ? "写想法" : "改想法",
                    Content = new StackPanel
                    {
                        Spacing = 8,
                        Width = 360,
                        Children =
                        {
                            new TextBlock { Text = $"“{mark.Text}”", FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12.5, Foreground = ThemeHelper.Brush(palette.T2), MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap },
                            box,
                        },
                    },
                    PrimaryButtonText = "保存",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = RootGrid.XamlRoot,
                };
                if (await dlg.ShowAsync() == ContentDialogResult.Primary)
                {
                    _library.Core.SetMarkNote(book.Id, mark.Id, string.IsNullOrWhiteSpace(box.Text) ? null : box.Text.Trim());
                    RefreshBooks();
                }
            };

            var copyItem = new MenuFlyoutItem { Text = "复制" };
            copyItem.Click += (_, _) =>
            {
                var dp = new DataPackage();
                dp.SetText(string.IsNullOrWhiteSpace(mark.Note) ? mark.Text : $"{mark.Text}\n—— {mark.Note}");
                Clipboard.SetContent(dp);
                ShowToast("已复制划线", canUndo: false);
            };

            var shareItem = new MenuFlyoutItem { Text = "分享书摘…" };
            shareItem.Click += async (_, _) => await QuoteShareDialog.ShowAsync(this, RootGrid.XamlRoot, mark.Text, mark.Note, book.Title, book.Authors.FirstOrDefault());

            var delItem = new MenuFlyoutItem { Text = "删除划线", Foreground = ThemeHelper.Brush(palette.Danger) };
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
            btn.DoubleTapped += (_, _) => App.CurrentApp.OpenBook(book.Id, section: mark.Section, sentence: (int)mark.Start.Sentence);
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

            // 1:1 ReviewDeckCard（今天回顾三层叠卡，MacReviewView.swift:70-156）
            try
            {
                long dayStartMs = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
                var deck = _library.Core.ReviewToday(null, dayStartMs, 5);
                if (deck.Items.Length > 0 || deck.DoneToday > 0)
                {
                    SubPageStack.Children.Add(BuildReviewDeckCard(deck, dark));
                }
            }
            catch { }

            var byDay = bookNotes
                .SelectMany(bn => bn.Marks.Where(MatchesChip).Select(m => (bn.Book, Mark: m, Day: DateTimeOffset.FromUnixTimeMilliseconds(m.At).LocalDateTime.Date)))
                .OrderByDescending(x => x.Mark.At)
                .GroupBy(x => x.Day);

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
        else
        {
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
                foreach (var bn in bookNotes)
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
                    ToolTipService.SetToolTip(cBtn, bn.Book.Title);
                    string bid = bn.Book.Id;
                    cBtn.Click += (_, _) => { _notesChosenBookId = bid; _notesChip = "all"; ConfigureHeaderForCurrentView(); RenderNotesPage(); };
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
                    Text = $"{bookNotes.Length} 本书",
                    FontSize = 11.5,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Foreground = ThemeHelper.Brush(palette.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                headBar.Children.Add(BuildColumnSwitch());
                leftColWrap.Children.Add(headBar);

                var leftStack = new StackPanel { Spacing = 2 };
                foreach (var bn in bookNotes)
                {
                    bool on = bn.Book.Id == _notesChosenBookId;
                    int thoughtCnt = bn.Marks.Count(m => !string.IsNullOrWhiteSpace(m.Note));
                    var r = new Grid { ColumnSpacing = 10 };
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
                    r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    r.Children.Add(CreateCoverView(bn.Book, 26, 38, elevated: false));
                    var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
                    info.Children.Add(new TextBlock { Text = bn.Book.Title, FontSize = 12, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(on ? tint.Accent : palette.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    info.Children.Add(new TextBlock
                    {
                        Text = thoughtCnt > 0 ? $"划线 {bn.Marks.Count} · 想法 {thoughtCnt}" : $"划线 {bn.Marks.Count}",
                        FontSize = 10.5,
                        Foreground = ThemeHelper.Brush(palette.T3),
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
                    btn.Click += (_, _) => { _notesChosenBookId = bid; _notesChip = "all"; ConfigureHeaderForCurrentView(); RenderNotesPage(); };
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

            var chosen = bookNotes.First(b => b.Book.Id == _notesChosenBookId);
            int chosenThoughts = chosen.Marks.Count(m => !string.IsNullOrWhiteSpace(m.Note));
            var rightStack = new StackPanel { Spacing = 14 };
            var head = new Grid { ColumnSpacing = 14 };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.Children.Add(CreateCoverView(chosen.Book, 46, 66, elevated: false));
            var hMeta = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            hMeta.Children.Add(new TextBlock { Text = chosen.Book.Title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(palette.T1) });
            hMeta.Children.Add(new TextBlock
            {
                Text = $"{chosen.Book.Authors.FirstOrDefault() ?? ""} · 划线 {chosen.Marks.Count}{(chosenThoughts > 0 ? $" · 想法 {chosenThoughts}" : "")} · 按书里的顺序".Trim(' ', '·'),
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
            revBookBtn.Click += async (_, _) => await ShowReviewSheetAsync(chosenBookId);
            rightBtns.Children.Add(revBookBtn);

            var openBookBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "打开书" };
            openBookBtn.Click += (_, _) => App.CurrentApp.OpenBook(chosenBookId);
            rightBtns.Children.Add(openBookBtn);
            Grid.SetColumn(rightBtns, 2);
            head.Children.Add(rightBtns);

            rightStack.Children.Add(head);
            rightStack.Children.Add(BuildFilterChips(chosen.Marks));

            var filteredMarks = chosen.Marks.Where(MatchesChip)
                .OrderBy(m => m.Section).ThenBy(m => m.Start.Sentence).ThenBy(m => m.Start.Offset)
                .ToArray();

            foreach (var secGrp in filteredMarks.GroupBy(m => m.Section))
            {
                var chHead = new Grid { Margin = new Thickness(4, 4, 4, -6) };
                chHead.Children.Add(new TextBlock
                {
                    Text = GetChapterTitle(chosen.Book.Id, secGrp.Key),
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

            Grid.SetColumn(rightStack, 1);
            split.Children.Add(rightStack);
            SubPageStack.Children.Add(split);
        }
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
            mid.Children.Add(new TextBlock
            {
                Text = $"“{first.Mark.Text}”",
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 13.5,
                Foreground = ThemeHelper.Brush(p.T1),
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            mid.Children.Add(new TextBlock
            {
                Text = firstBook?.Title ?? "书摘",
                FontSize = 10.5,
                Foreground = ThemeHelper.Brush(p.T3),
            });
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
            Grid.SetColumn(startBtn, 3);
            inner.Children.Add(startBtn);
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
        long dayStartMs = new DateTimeOffset(DateTime.Today).ToUnixTimeMilliseconds();
        ReviewItem[] items;
        try
        {
            var deck = _library.Core.ReviewToday(onlyBookId, dayStartMs, 8);
            items = deck.Items;
        }
        catch { return; }

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
        var host = new StackPanel { Spacing = 14, Width = 420 };
        ContentDialog? dlgRef = null;
        PodcastClip[] allPodClips = [];
        try { allPodClips = _library.Core.PodcastClips(null); } catch { }

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
                    var rText = new TextBlock
                    {
                        Text = rit.Mark.Text,
                        FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                        FontSize = 12.5,
                        Foreground = ThemeHelper.Brush(isKept ? p.T1 : p.T3),
                        VerticalAlignment = VerticalAlignment.Center,
                        MaxLines = 1,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    };
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

                // 只回顾某一本书菜单（1:1 MacReviewView.swift:357-373）
                var booksWithMarks = _keptBooks.Where(b => b.Available && _markCounts.TryGetValue(b.Id, out var c) && c > 0).ToArray();
                if (booksWithMarks.Length > 0)
                {
                    var onlyMenu = new MenuFlyout();
                    foreach (var bk in booksWithMarks)
                    {
                        string bid = bk.Id;
                        var mi = new MenuFlyoutItem { Text = bk.Title };
                        mi.Click += (_, _) => { nextOnlyBookId = bid; dlgRef?.Hide(); };
                        onlyMenu.Items.Add(mi);
                    }
                    var onlyBtn = new Button
                    {
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                        Content = "只回顾某一本书 ›",
                        Flyout = onlyMenu,
                    };
                    sumCard.Children.Add(onlyBtn);
                }

                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(16),
                    Background = ThemeHelper.Brush(p.Card),
                    BorderBrush = ThemeHelper.Brush(p.Line),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(20, 18, 20, 16),
                    Child = sumCard,
                });

                var doneBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Height = 36, Content = "完成" };
                doneBtn.Click += (_, _) => dlgRef?.Hide();
                host.Children.Add(doneBtn);
                return;
            }

            var cur = items[idx];
            var book = _keptBooks.FirstOrDefault(b => b.Id == cur.Book);

            // Top progress segments
            var progGrid = new Grid { ColumnSpacing = 4 };
            for (int i = 0; i < items.Length; i++)
            {
                progGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var seg = new Border
                {
                    Height = 3,
                    CornerRadius = new CornerRadius(1.5),
                    Background = ThemeHelper.Brush(i <= idx ? tint.Fill : p.Inset),
                };
                Grid.SetColumn(seg, i);
                progGrid.Children.Add(seg);
            }
            host.Children.Add(progGrid);
            host.Children.Add(new TextBlock { Text = $"{(onlyBookId is null ? "今天回顾" : "回顾这本书")} · {idx + 1} / {items.Length}", FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), HorizontalAlignment = HorizontalAlignment.Center });

            var cardStack = new StackPanel { Spacing = 12 };
            cardStack.Children.Add(new TextBlock { Text = "“", FontFamily = new FontFamily("Georgia"), FontSize = 34, Foreground = ThemeHelper.Brush(tint.Accent), Margin = new Thickness(0, -6, 0, -14) });
            cardStack.Children.Add(new TextBlock
            {
                Text = cur.Mark.Text,
                FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                FontSize = 17.5,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                LineHeight = 29,
                Foreground = ThemeHelper.Brush(p.T1),
                TextWrapping = TextWrapping.Wrap,
            });

            if (!string.IsNullOrWhiteSpace(cur.Mark.Note))
            {
                var th = new Grid { ColumnSpacing = 8 };
                th.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2) });
                th.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                th.Children.Add(new Border { Background = ThemeHelper.Brush(tint.Accent), CornerRadius = new CornerRadius(1) });
                var nt = new TextBlock { Text = cur.Mark.Note, FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()), FontSize = 13, Foreground = ThemeHelper.Brush(tint.Accent), TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(nt, 1);
                th.Children.Add(nt);
                cardStack.Children.Add(th);
            }

            cardStack.Children.Add(new Border { Height = 1, Background = ThemeHelper.Brush(p.Line) });
            var foot = new Grid { ColumnSpacing = 10 };
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            foot.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (book is not null) foot.Children.Add(CreateCoverView(book, 26, 38, elevated: false));
            var bm = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            bm.Children.Add(new TextBlock { Text = book?.Title ?? "书摘", FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            bm.Children.Add(new TextBlock { Text = book?.Authors.FirstOrDefault() ?? "", FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3) });
            Grid.SetColumn(bm, 1);
            foot.Children.Add(bm);

            var rightFoot = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
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
                        var mp = new Windows.Media.Playback.MediaPlayer { AutoPlay = true };
                        mp.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(clipPath));
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
                FontSize = 11.5,
                Foreground = ThemeHelper.Brush(tint.Accent),
                Content = "回原文 ›",
            };
            jumpBtn.Click += (_, _) =>
            {
                dlgRef?.Hide();
                App.CurrentApp.OpenBook(cur.Book, section: cur.Mark.Section, sentence: (int)cur.Mark.Start.Sentence);
            };
            rightFoot.Children.Add(jumpBtn);
            Grid.SetColumn(rightFoot, 2);
            foot.Children.Add(rightFoot);
            cardStack.Children.Add(foot);

            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(16),
                Background = ThemeHelper.Brush(p.Card),
                BorderBrush = ThemeHelper.Brush(p.Line),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(20, 18, 20, 16),
                Child = cardStack,
            });

            var actGrid = new Grid { ColumnSpacing = 10 };
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            actGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var dropBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 36,
                Content = "✕ 不要了",
            };
            dropBtn.Click += (_, _) =>
            {
                keptMap[cur.Mark.Id] = false;
                try { _library.Core.SetMarkReview(cur.Book, cur.Mark.Id, false); } catch { }
                idx++;
                RenderCard();
            };

            var keepBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 36,
                Content = "✓ 留下",
            };
            keepBtn.Click += (_, _) =>
            {
                keptMap[cur.Mark.Id] = true;
                try { _library.Core.SetMarkReview(cur.Book, cur.Mark.Id, true); } catch { }
                idx++;
                RenderCard();
            };
            Grid.SetColumn(keepBtn, 1);
            actGrid.Children.Add(dropBtn);
            actGrid.Children.Add(keepBtn);
            host.Children.Add(actGrid);
        }

        RenderCard();
        dlgRef = new ContentDialog
        {
            Content = host,
            CloseButtonText = "关闭",
            XamlRoot = RootGrid.XamlRoot,
        };
        await dlgRef.ShowAsync();
        RenderNotesPage();
        if (nextOnlyBookId is not null)
            await ShowReviewSheetAsync(nextOnlyBookId);
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

        var shown = _words.Where(w => w.Known == _wordsShowKnown).OrderByDescending(w => w.At).ToArray();
        if (shown.Length == 0)
        {
            SubPageStack.Children.Add(new TextBlock
            {
                Text = _wordsShowKnown ? "暂无已掌握的词。将在学的词标为「已掌握」后移至此处" : "已全部掌握。新查的词加入生词本后会显示在这里",
                FontSize = 12,
                Foreground = ThemeHelper.Brush(palette.T3),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 56, 0, 0),
            });
            return;
        }

        // 全库词频统计（1:1 Vocab.swift:63-68）
        var freqMap = new Dictionary<string, WordCount>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var wc in _library.Core.WordCounts(shown.Select(w => w.Word).Distinct().ToArray()))
                freqMap[wc.Word] = wc;
        }
        catch { }

        string BookTitleOf(string id) => _keptBooks.FirstOrDefault(b => b.Id == id)?.Title ?? "外文书";

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
                wordLine.Children.Add(new TextBlock
                {
                    Text = w.Word,
                    FontFamily = new FontFamily(YYFontFamily.LatinSerif.WindowsFamily()),
                    FontSize = 18,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = ThemeHelper.Brush(palette.T1),
                });
                if (!string.IsNullOrWhiteSpace(w.Phonetic))
                    wordLine.Children.Add(new TextBlock { Text = $"/{w.Phonetic.Trim('/', '[', ']')}/", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T2), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(wordLine);

                var spkBtn = new Button
                {
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(14),
                    Background = ThemeHelper.Brush(palette.Inset),
                    BorderThickness = new Thickness(0),
                    HorizontalAlignment = HorizontalAlignment.Right,
                    UseSystemFocusVisuals = false,
                    Content = YYIcons.Make("speaker", 12, ThemeHelper.Brush(palette.T1)),
                };
                string speakText = w.Word;
                spkBtn.Click += (_, _) => _ = Yuyue.Speech.AudioPlayer.PreviewVoiceAsync(_library, _library.Prefs.VoiceId, 0.95, speakText);
                head.Children.Add(spkBtn);
                item.Children.Add(head);

                // POS 斜体词性拆分（1:1 MacWordsView.swift:239-243）
                if (!string.IsNullOrEmpty(w.Meaning))
                {
                    var meanTb = new TextBlock { FontSize = 13, Foreground = ThemeHelper.Brush(palette.T1), TextWrapping = TextWrapping.Wrap };
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

                // 原句目标词下划线高亮（1:1 Vocab.swift:41-52）
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
                    int hitPos = w.Context.IndexOf(w.Word, StringComparison.OrdinalIgnoreCase);
                    if (hitPos >= 0)
                    {
                        int endPos = hitPos + w.Word.Length;
                        while (endPos < w.Context.Length && char.IsLetter(w.Context[endPos])) endPos++;
                        if (hitPos > 0) ctxTb.Inlines.Add(new Run { Text = w.Context[..hitPos] });
                        var ul = new Underline();
                        ul.Inlines.Add(new Run { Text = w.Context[hitPos..endPos], Foreground = ThemeHelper.Brush(palette.T1), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                        ctxTb.Inlines.Add(ul);
                        if (endPos < w.Context.Length) ctxTb.Inlines.Add(new Run { Text = w.Context[endPos..] });
                    }
                    else
                    {
                        ctxTb.Text = w.Context;
                    }
                    item.Children.Add(ctxTb);
                }

                var foot = new Grid { Margin = new Thickness(0, 4, 0, 0) };
                var srcLeft = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                if (!_wordsByBook)
                    srcLeft.Children.Add(new TextBlock { Text = BookTitleOf(w.Book), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(palette.T2) });

                string freqLine = "只出现过这一次";
                if (freqMap.TryGetValue(w.Word, out var wc) && wc.Times > 1)
                {
                    freqLine = wc.Books > 1
                        ? $"在 {wc.Books} 本书里出现 {wc.Times} 次"
                        : $"在这本书里出现 {wc.Times} 次";
                }
                srcLeft.Children.Add(new TextBlock { Text = freqLine, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3) });
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

                // 右键菜单：回到书里这一句 / 读一遍 / 拷贝 / 删除（带撤销）
                var wCtx = new MenuFlyout();
                var wJump = new MenuFlyoutItem { Text = "回到书里这一句" };
                wJump.Click += (_, _) => App.CurrentApp.OpenBook(w.Book, section: w.Section, sentence: (int)w.Sentence);
                var wSpeak = new MenuFlyoutItem { Text = "读一遍" };
                wSpeak.Click += (_, _) => _ = Yuyue.Speech.AudioPlayer.PreviewVoiceAsync(_library, _library.Prefs.VoiceId, 0.95, speakText);
                var wCopy = new MenuFlyoutItem { Text = "拷贝" };
                wCopy.Click += (_, _) =>
                {
                    var dp = new DataPackage();
                    dp.SetText($"{w.Word}  {w.Meaning}".Trim());
                    Clipboard.SetContent(dp);
                    ShowToast("已拷贝生词", canUndo: false);
                };
                var wDel = new MenuFlyoutItem { Text = "删除", Foreground = ThemeHelper.Brush(palette.Danger) };
                VocabWord savedWord = w;
                wDel.Click += (_, _) =>
                {
                    _library.DeleteWord(savedWord.Word);
                    RefreshBooks();
                    ShowToast($"已删除生词“{savedWord.Word}”", canUndo: true, isDelete: true, undo: () =>
                    {
                        try
                        {
                            var restored = _library.AddWord(savedWord.Word, savedWord.Phonetic, savedWord.Meaning, savedWord.Book, savedWord.Section, savedWord.Sentence, savedWord.Context);
                            if (savedWord.Known) _library.SetWordKnown(restored.Word, true);
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

                item.DoubleTapped += (_, _) => App.CurrentApp.OpenBook(w.Book, section: w.Section, sentence: (int)w.Sentence);
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
    }

    private void RenderCollectionsWall()
    {
        SubPageStack.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        if (_collections.Count == 0)
        {
            var emptyStack = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 110, 0, 0) };
            emptyStack.Children.Add(YYIcons.Make("stack", 28, ThemeHelper.Brush(palette.T3)));
            emptyStack.Children.Add(new TextBlock { Text = "还没有合集。点右上角「+ 新建合集」，或者在一本书上右键「加入合集…」", FontSize = 12.5, Foreground = ThemeHelper.Brush(palette.T2), HorizontalAlignment = HorizontalAlignment.Center });
            SubPageStack.Children.Add(emptyStack);
            return;
        }

        // 顶部计数 + 右侧排序菜单（1:1 MacCollections.swift:87-97）
        var topRow = new Grid { Margin = new Thickness(2, 4, 2, 4) };
        topRow.Children.Add(new TextBlock
        {
            Text = $"{_collections.Count} 个",
            FontSize = 12.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = ThemeHelper.Brush(palette.T1),
            VerticalAlignment = VerticalAlignment.Center,
        });

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
            var mi = new MenuFlyoutItem { Text = (_collectionSort == k ? "✓ " : "") + lbl };
            string key = k;
            mi.Click += (_, _) => { _collectionSort = key; RenderCollectionsWall(); };
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
        topRow.Children.Add(sortBtn);
        SubPageStack.Children.Add(topRow);

        var ordered = (_collectionSort switch
        {
            "name" => _collections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase),
            "count" => _collections.OrderByDescending(c => c.Count).ThenByDescending(c => c.UsedAt),
            "own" => _collections.AsEnumerable(),
            _ => _collections.OrderByDescending(c => c.UsedAt),
        }).ToArray();

        var grid = new Grid { ColumnSpacing = 10, RowSpacing = 2 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int rows = (ordered.Length + 1) / 2;
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Color markPaper = dark ? Color.FromArgb(255, 43, 41, 37) : Color.FromArgb(255, 243, 240, 232);
        Color markInk = dark ? Color.FromArgb(255, 233, 229, 220) : Color.FromArgb(255, 43, 43, 43);

        for (int i = 0; i < ordered.Length; i++)
        {
            var col = ordered[i];
            bool isPinned = _library.Prefs.PinnedCollectionIds.Contains(col.Id);

            var markStack = new StackPanel { Spacing = 2.5, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
            markStack.Children.Add(YYIcons.Make(YYIcons.CollectionIconName(col.Name, col.Icon), 12.5, ThemeHelper.Brush(markInk)));
            markStack.Children.Add(new Border { Width = 20, Height = 1, Background = ThemeHelper.Brush(markInk), Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center });
            markStack.Children.Add(new Border { Width = 13, Height = 1, Background = ThemeHelper.Brush(markInk), Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Center });

            var markBadge = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(markPaper),
                BorderBrush = ThemeHelper.Brush(Color.FromArgb(20, markInk.R, markInk.G, markInk.B)),
                BorderThickness = new Thickness(0.5),
                Child = markStack,
            };

            var rowInner = new Grid { ColumnSpacing = 12, Height = 48 };
            rowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
            rowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowInner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowInner.Children.Add(markBadge);

            var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
            meta.Children.Add(new TextBlock { Text = col.Name, FontSize = 13.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(palette.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
            string usedStr = col.UsedAt > 0
                ? FormatDayName(DateTimeOffset.FromUnixTimeMilliseconds(col.UsedAt).LocalDateTime)
                : "没用过";
            meta.Children.Add(new TextBlock { Text = usedStr, FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
            Grid.SetColumn(meta, 1);
            rowInner.Children.Add(meta);

            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            if (isPinned) right.Children.Add(YYIcons.Make("pin", 10.5, ThemeHelper.Brush(tint.Accent)));
            right.Children.Add(new TextBlock { Text = $"{col.Count} 本", FontSize = 12, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center });
            right.Children.Add(YYIcons.Make("chevron-right", 10.5, ThemeHelper.Brush(palette.T3)));
            Grid.SetColumn(right, 2);
            rowInner.Children.Add(right);

            var ctx = new MenuFlyout();
            var pin = new MenuFlyoutItem { Text = isPinned ? "取消固定" : "固定到侧栏" };
            pin.Click += (_, _) =>
            {
                if (isPinned) _library.Prefs.PinnedCollectionIds.Remove(col.Id);
                else if (!_library.Prefs.PinnedCollectionIds.Contains(col.Id)) _library.Prefs.PinnedCollectionIds.Add(col.Id);
                _library.SavePrefs();
                RefreshBooks();
            };
            var ren = new MenuFlyoutItem { Text = "重命名…" };
            ren.Click += async (_, _) => await RenameCollectionAsync(col);

            var iconSub = new MenuFlyoutSubItem { Text = "更换图标" };
            foreach (var (icKey, icName) in new (string?, string)[]
            {
                (null, "自动按名字匹配"),
                ("book", "书本"),
                ("moon.stars", "睡前星月"),
                ("sparkles", "灵感星光"),
                ("headphones", "有声耳机"),
                ("globe", "外文地球"),
                ("leaf", "自然绿叶"),
                ("bookmark", "书签"),
            })
            {
                var icItem = new MenuFlyoutItem { Text = col.Icon == icKey ? $"✓ {icName}" : icName };
                string? nextIc = icKey;
                string colIdForIcon = col.Id;
                icItem.Click += (_, _) =>
                {
                    try { _library.Core.SetCollectionIcon(colIdForIcon, nextIc); } catch { }
                    RefreshBooks();
                };
                iconSub.Items.Add(icItem);
            }

            var del = new MenuFlyoutItem { Text = "删除合集…", Foreground = ThemeHelper.Brush(palette.Danger) };
            del.Click += async (_, _) => await ConfirmDeleteCollectionAsync(col);
            ctx.Items.Add(pin);
            ctx.Items.Add(new MenuFlyoutSeparator());
            ctx.Items.Add(ren);
            ctx.Items.Add(iconSub);
            if (_collectionSort == "own")
            {
                int curIdx = i;
                var moveUp = new MenuFlyoutItem { Text = "往前挪", IsEnabled = curIdx > 0 };
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
                var moveDown = new MenuFlyoutItem { Text = "往后挪", IsEnabled = curIdx + 1 < ordered.Length };
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
            ctx.Items.Add(del);

            var btn = new Button
            {
                Padding = new Thickness(8, 4, 12, 4),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                AllowDrop = true,
                UseSystemFocusVisuals = false,
                Content = rowInner,
                ContextFlyout = ctx,
            };
            string cid = col.Id;
            string cname = col.Name;
            btn.DragOver += (_, de) =>
            {
                if (de.DataView.Contains(StandardDataFormats.Text))
                    de.AcceptedOperation = DataPackageOperation.Copy;
            };
            btn.Drop += async (_, de) =>
            {
                if (!de.DataView.Contains(StandardDataFormats.Text)) return;
                string txt = await de.DataView.GetTextAsync();
                if (txt.StartsWith("yuyue-book:", StringComparison.Ordinal))
                {
                    string bid = txt["yuyue-book:".Length..];
                    _library.SetInCollection(cid, bid, true);
                    RefreshBooks();
                    ShowToast($"已放进合集“{cname}”", canUndo: false);
                }
            };
            btn.Click += (_, _) => { _activeFilter = $"col:{cid}"; UpdateNavSelectionVisuals(); RenderCurrentView(); };
            Grid.SetRow(btn, i / 2);
            Grid.SetColumn(btn, i % 2);
            grid.Children.Add(btn);
        }

        SubPageStack.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = ThemeHelper.Brush(palette.Card),
            BorderBrush = ThemeHelper.Brush(palette.Line),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            Child = grid,
        });
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
            return;
        }
        if (filter.Equals("podcast-inline", StringComparison.OrdinalIgnoreCase))
        {
            var iss = _library.Podcasts.GetIssues().FirstOrDefault();
            if (iss is not null) _podcastBuilder.OpenIssueId = iss.Id;
            _activeFilter = "podcasts";
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            return;
        }
        if (filter.StartsWith("quick:", StringComparison.OrdinalIgnoreCase))
        {
            string q = filter[6..];
            _activeFilter = "all";
            UpdateNavSelectionVisuals();
            RenderCurrentView();
            QuickSearchOverlay.Visibility = Visibility.Visible;
            QuickSearchBox.Text = q;
            UpdateQuickSearchResults(q);
            return;
        }
#endif
        _activeFilter = filter;
        UpdateNavSelectionVisuals();
        RenderCurrentView();
    }

    private void NavItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _activeFilter = tag;
            UpdateNavSelectionVisuals();
            RenderCurrentView();
        }
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarVisible = !_sidebarVisible;
        _library.Prefs.ShelfSidebarShown = _sidebarVisible;
        _library.SavePrefs();
        SidebarColumn.Width = _sidebarVisible ? new GridLength(220) : new GridLength(0);
        SidebarPanel.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        PaperSheet.Margin = _sidebarVisible ? new Thickness(0, 0, 8, 8) : new Thickness(8, 0, 8, 8);
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string key)
        {
            _sortKey = key;
            SortLabelText.Text = item.Text;
            RenderCurrentView();
        }
    }

    // 1:1 MacShelfView.swift:447-450：点击底部同步状态始终打开「设置 · 同步」
    private void SyncStatus_Click(object sender, RoutedEventArgs e) => App.CurrentApp.OpenSettings("sync");

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

    private void CloseQuickSearch() => QuickSearchOverlay.Visibility = Visibility.Collapsed;

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
    }

    private void TriggerAskCompanionFromQuickSearch(string q)
    {
        CloseQuickSearch();
        var latestBook = _shelfBooks
            .Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio && b.Format != BookFormat.Pdf)
            .OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt))
            .FirstOrDefault();
        if (latestBook is not null)
        {
            App.CurrentApp.OpenBook(latestBook.Id);
        }
        else
        {
            App.CurrentApp.OpenSettings("companion");
        }
    }

    // 1:1 QuickSearch.swift：Row 0 固定「问书伴」，下方分组：最近读过/书名和篇目、全文（Core.SearchBook）、笔记
    private void UpdateQuickSearchResults(string query)
    {
        QuickSearchResults.Children.Clear();
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var palette = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);
        var q = query.Trim();

        int totalNotes = 0;
        foreach (var b in _keptBooks)
            if (_markCounts.TryGetValue(b.Id, out var c)) totalNotes += (int)c;
        QuickSearchScopeText.Text = $"在 {_keptBooks.Count} 本书和 {totalNotes} 条笔记里搜";

        TextBlock GroupHeader(string title, string? right = null)
        {
            return new TextBlock
            {
                Text = right is null ? title : $"{title} · {right}",
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = ThemeHelper.Brush(palette.T3),
                Margin = new Thickness(10, 8, 10, 3),
            };
        }

        // Row 0：始终展示「问书伴」
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
        askMeta.Children.Add(new TextBlock { Text = "只用你读过的部分回答，带出处", FontSize = 10.5, Foreground = ThemeHelper.Brush(palette.T3) });
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
        askBtn.Click += (_, _) => TriggerAskCompanionFromQuickSearch(q);
        QuickSearchResults.Children.Add(askBtn);

        // Group 1：最近读过 / 书名和篇目
        var bookMatches = string.IsNullOrEmpty(q)
            ? _shelfBooks.OrderByDescending(b => Math.Max(b.OpenedAt ?? 0, b.AddedAt)).Take(5).ToArray()
            : _shelfBooks.Where(b =>
                b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))).Take(6).ToArray();

        if (bookMatches.Length > 0)
        {
            QuickSearchResults.Children.Add(GroupHeader(string.IsNullOrEmpty(q) ? "最近读过" : "书名和篇目", string.IsNullOrEmpty(q) ? null : $"{bookMatches.Length} 本"));
            foreach (var book in bookMatches)
            {
                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(CreateCoverView(book, 20, 28, elevated: false));
                var tb = new TextBlock { Text = book.Title, FontSize = 13, Foreground = ThemeHelper.Brush(palette.T1), VerticalAlignment = VerticalAlignment.Center, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(tb, 1);
                row.Children.Add(tb);

                var sub = new TextBlock
                {
                    Text = $"{book.Authors.FirstOrDefault() ?? ""} · {FormatShelfNote(book)}".Trim(' ', '·'),
                    FontSize = 11,
                    Foreground = ThemeHelper.Brush(palette.T3),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(sub, 2);
                row.Children.Add(sub);

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
                var bid = book.Id;
                btn.Click += (_, _) => { CloseQuickSearch(); App.CurrentApp.OpenBook(bid); };
                QuickSearchResults.Children.Add(btn);
            }
        }

        if (string.IsNullOrEmpty(q)) return;

        // Group 2：全文检索（Core.SearchBook 轮询书籍，1:1 QuickSearch.swift:145-166）
        var fullTextHits = new List<(BookSummary Book, SearchHit Hit)>();
        int hitBookCount = 0;
        int totalLineCount = 0;
        bool lineCapped = false;
        foreach (var b in _shelfBooks.Where(b => !b.Record && b.Available && b.Format != BookFormat.Audio).Take(8))
        {
            try
            {
                var hits = _library.Core.SearchBook(b.Id, q);
                if (hits.Length > 0)
                {
                    hitBookCount++;
                    totalLineCount += hits.Length;
                    if (hits.Length >= 500) lineCapped = true;
                    foreach (var h in hits.Take(2))
                        if (fullTextHits.Count < 4) fullTextHits.Add((b, h));
                }
            }
            catch { }
        }

        if (fullTextHits.Count > 0)
        {
            QuickSearchResults.Children.Add(GroupHeader("全文", $"{totalLineCount}{(lineCapped ? "+" : "")} 处 · {hitBookCount} 本书"));
            foreach (var (b, hit) in fullTextHits)
            {
                var r = new Grid { ColumnSpacing = 10 };
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                r.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                r.Children.Add(CreateCoverView(b, 20, 28, elevated: false));

                var sentTb = new TextBlock
                {
                    FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()),
                    FontSize = 12.5,
                    Foreground = ThemeHelper.Brush(palette.T1),
                    MaxLines = 1,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                if (!string.IsNullOrEmpty(hit.Before)) sentTb.Inlines.Add(new Run { Text = hit.Before });
                var ul = new Underline();
                ul.Inlines.Add(new Run { Text = hit.Matched, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = ThemeHelper.Brush(tint.Accent) });
                sentTb.Inlines.Add(ul);
                if (!string.IsNullOrEmpty(hit.After)) sentTb.Inlines.Add(new Run { Text = hit.After });
                Grid.SetColumn(sentTb, 1);
                r.Children.Add(sentTb);

                var src = new TextBlock { Text = b.Title, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(src, 2);
                r.Children.Add(src);

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
                string bid = b.Id;
                uint sec = hit.Section;
                int sent = (int)hit.Sentence;
                hb.Click += (_, _) => { CloseQuickSearch(); App.CurrentApp.OpenBook(bid, section: sec, sentence: sent); };
                QuickSearchResults.Children.Add(hb);
            }
        }

        // Group 3：笔记（最多展示 3 条）
        var noteMatches = new List<(BookSummary Book, Mark Mark)>();
        foreach (var b in _keptBooks)
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
                sp.Children.Add(new TextBlock { Text = mk.Text, FontFamily = new FontFamily(YYFontFamily.Serif.WindowsFamily()), FontSize = 12.5, Foreground = ThemeHelper.Brush(palette.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                if (!string.IsNullOrWhiteSpace(mk.Note))
                    sp.Children.Add(new TextBlock { Text = mk.Note, FontFamily = new FontFamily(YYFontFamily.Kaiti.WindowsFamily()), FontSize = 11.5, Foreground = ThemeHelper.Brush(tint.Accent), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(sp, 1);
                r.Children.Add(sp);

                var src = new TextBlock { Text = b.Title, FontSize = 11, Foreground = ThemeHelper.Brush(palette.T3), VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140, MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(src, 2);
                r.Children.Add(src);

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
                string bid = b.Id;
                uint sec = mk.Section;
                int sent = (int)mk.Start.Sentence;
                nb.Click += (_, _) => { CloseQuickSearch(); App.CurrentApp.OpenBook(bid, section: sec, sentence: sent); };
                QuickSearchResults.Children.Add(nb);
            }
        }

        if (bookMatches.Length == 0 && fullTextHits.Count == 0 && noteMatches.Count == 0)
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
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        bool alt = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == VirtualKey.K)
        {
            if (QuickSearchOverlay.Visibility == Visibility.Visible) CloseQuickSearch();
            else OpenQuickSearch();
            e.Handled = true;
        }
        else if (ctrl && alt && e.Key == VirtualKey.N)
        {
            ToggleSidebar_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.Z && ToastBanner.Visibility == Visibility.Visible && _toastUndoAction is not null)
        {
            ToastUndo_Click(sender, e);
            e.Handled = true;
        }
        else if (ctrl && e.Key == VirtualKey.O) { ImportButton_Click(sender, e); e.Handled = true; }
        else if (ctrl && e.Key == (VirtualKey)188) { App.CurrentApp.OpenSettings("general"); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape && QuickSearchOverlay.Visibility == Visibility.Visible) { CloseQuickSearch(); e.Handled = true; }
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, ViewMode = PickerViewMode.List };
        if (_activeFilter == "audio")
        {
            foreach (var ext in AudiobookImporter.Extensions) picker.FileTypeFilter.Add(ext);
            picker.FileTypeFilter.Add(".zip");
        }
        else
        {
            foreach (var ext in LibraryService.SupportedExtensions) picker.FileTypeFilter.Add(ext);
            foreach (var ext in AudiobookImporter.Extensions) picker.FileTypeFilter.Add(ext);
            picker.FileTypeFilter.Add(".zip");
        }
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await ImportPathsAsync(files.Select(f => f.Path).ToArray(), openFirst: false);
    }

    private async void ImportAudioFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await ImportPathsAsync([folder.Path], openFirst: false);
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> paths, bool openFirst)
    {
        var imported = new List<BookSummary>();
        var errors = new List<string>();

        var snapshotPaths = paths.Where(p => string.Equals(Path.GetExtension(p), ".yuyue", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var sp in snapshotPaths)
        {
            if (await SnapshotAndConflictDialogs.ShowSnapshotImportDialogAsync(this, RootGrid.XamlRoot, _library, sp))
            {
                RefreshBooks();
                ShowToast("已从快照恢复书库数据", canUndo: false);
            }
        }
        if (snapshotPaths.Length == paths.Count) return;

        var audioPaths = paths.Where(AudiobookImporter.IsAudiobookOrArchivePath).ToArray();
        if (audioPaths.Length > 0)
        {
            try
            {
                BookSummary? audioBook = RootGrid.XamlRoot is not null && !openFirst
                    ? await AudiobookImporter.ShowImportSheetAsync(this, RootGrid.XamlRoot, _library, audioPaths)
                    : await AudiobookImporter.ImportFilesAsync(_library, audioPaths);
                if (audioBook is not null)
                {
                    if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
                        _library.SetInCollection(_activeFilter[4..], audioBook.Id, true);
                    imported.Add(audioBook);
                }
            }
            catch (Exception ex) { errors.Add($"有声书: {ex.Message}"); }
        }

        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!LibraryService.SupportedExtensions.Contains(ext)) continue;
            try
            {
                var book = await _library.ImportBookWithExtrasAsync(path);
                if (_activeFilter.StartsWith("col:", StringComparison.Ordinal))
                    _library.SetInCollection(_activeFilter[4..], book.Id, true);
                imported.Add(book);
            }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }

        RefreshBooks();
        if (imported.Count > 0)
        {
            ShowToast(imported.Count == 1 ? $"已导入《{imported[0].Title}》" : $"已导入 {imported.Count} 本书", canUndo: false);
            if (openFirst) App.CurrentApp.OpenBook(imported[0].Id);
        }
        else if (errors.Count > 0) ShowToast($"导入失败：{errors[0]}", canUndo: false);
    }

    private async void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "合集的名字，例如：睡前、科幻" };
        var dialog = new ContentDialog
        {
            Title = "新建合集",
            Content = input,
            PrimaryButtonText = "好",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            try
            {
                var col = _library.CreateCollection(input.Text.Trim());
                _activeFilter = $"col:{col.Id}";
                RefreshBooks();
            }
            catch (Exception ex) { ShowToast(ex.Message, canUndo: false); }
        }
    }

    private void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            DropOverlay.Visibility = Visibility.Visible;
        }
    }

    private void RootGrid_DragLeave(object sender, DragEventArgs e) => DropOverlay.Visibility = Visibility.Collapsed;

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = items.Select(it => it.Path).Where(p => !string.IsNullOrEmpty(p)).ToArray();
        if (paths.Length > 0) await ImportPathsAsync(paths, openFirst: false);
    }

    private void ToastUndo_Click(object sender, RoutedEventArgs e)
    {
        var action = _toastUndoAction;
        _toastUndoAction = null;
        _toastTimer?.Stop();
        ToastBanner.Visibility = Visibility.Collapsed;
        action?.Invoke();
    }

    public void ShowToast(string text, bool canUndo, bool isDelete = false, Action? undo = null)
    {
        bool dark = ThemeHelper.IsDark(RootGrid.ActualTheme);
        var p = ThemeHelper.Palette(dark);
        _toastUndoAction = canUndo ? undo : null;
        ToastDisc.Background = ThemeHelper.Brush(isDelete ? p.Danger : Color.FromArgb(255, 45, 190, 96));
        ToastText.Text = text;
        ToastUndoButton.Visibility = canUndo && undo is not null ? Visibility.Visible : Visibility.Collapsed;
        ToastBanner.Visibility = Visibility.Visible;

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(canUndo ? 6 : 3) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            _toastUndoAction = null;
            ToastBanner.Visibility = Visibility.Collapsed;
        };
        _toastTimer.Start();
    }
}
