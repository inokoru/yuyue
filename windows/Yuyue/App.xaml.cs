using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Pdf;
using Yuyue.Reader;
using Yuyue.UI.Audiobook;
using Yuyue.UI.Library;
using Yuyue.UI.Pdf;
using Yuyue.UI.Reader;
using Yuyue.UI.Settings;
using Yuyue.UI.Shell;

namespace Yuyue;

public interface IDebugScreenshotCapable
{
    Task<bool> SaveCompositeScreenshotAsync(string path);
}

public partial class App : Application
{
    public static App CurrentApp => (App)Current;

    public LibraryService Library { get; private set; } = null!;

    private MainWindow? _mainWindow;
    private LibraryView? _libraryView;
    private readonly List<MainWindow> _secondaryWindows = [];
    private MiniPlayerWindow? _miniPlayerWindow;

    private DispatcherTimer? _syncTimer;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private Windows.UI.ViewManagement.UISettings? _uiSettings;
    private object? _activeAudioClaimant;
    private Action? _pauseActiveAudio;
    private readonly ConditionalWeakTable<object, object> _claimOwners = new();
    private bool _exitingApp;
    private bool _isHiddenInTray;

    public bool IsExitingApp => _exitingApp;
    public IMiniPlayerSource? ActiveMiniPlayerSource { get; private set; }

    public void SetActiveMiniPlayerSource(IMiniPlayerSource? source)
    {
        ActiveMiniPlayerSource = source;
        _miniPlayerWindow?.BindSource(source);
        foreach (var w in EnumerateAllShellWindows())
            w.DispatcherQueue.TryEnqueue(w.UpdateTaskbarThumbToolbar);
    }

    public void ClearActiveMiniPlayerSource(IMiniPlayerSource? source)
    {
        if (source is not null && !ReferenceEquals(ActiveMiniPlayerSource, source)) return;
        SetActiveMiniPlayerSource(null);
    }

    public void ReleaseAudioClaim(object claimant)
    {
        if (claimant is null) return;
        object normClaimant = NormalizeClaimant(claimant);
        object? normActive = _activeAudioClaimant is not null ? NormalizeClaimant(_activeAudioClaimant) : null;
        if (ReferenceEquals(normActive, normClaimant) || ReferenceEquals(_activeAudioClaimant, claimant))
        {
            _activeAudioClaimant = null;
            _pauseActiveAudio = null;
        }
    }

    public void RestoreAndSelectTab(MainWindow? preferredOwner, ITabContent tab)
    {
        MainWindow? host = EnumerateAllShellWindows().FirstOrDefault(w => w.Tabs.Contains(tab)) ?? preferredOwner;
        if (host is null)
        {
            ShowShelf();
            return;
        }
        if (ReferenceEquals(host, _mainWindow) && _isHiddenInTray)
        {
            _isHiddenInTray = false;
            Library.Analytics.TrackAppReopened();
        }
        try { host.AppWindow.Show(); } catch { }
        host.Activate();
        BringWindowToFront(host);
        host.SelectTab(tab);
    }

    public void ShowMiniPlayer(IMiniPlayerSource? source = null)
    {
        if (source is not null) ActiveMiniPlayerSource = source;
        if (_miniPlayerWindow is null)
        {
            _miniPlayerWindow = new MiniPlayerWindow(Library);
            _miniPlayerWindow.Closed += (_, _) => _miniPlayerWindow = null;
        }
        _miniPlayerWindow.BindSource(ActiveMiniPlayerSource);
        _miniPlayerWindow.ApplyAppearance();
        _miniPlayerWindow.Activate();
        BringWindowToFront(_miniPlayerWindow);
    }

    private IEnumerable<MainWindow> EnumerateAllShellWindows()
    {
        if (_mainWindow is not null) yield return _mainWindow;
        foreach (var w in _secondaryWindows.ToArray()) yield return w;
    }

    public void BindAudioClaimOwner(object child, object owner)
    {
        if (child is null || owner is null || ReferenceEquals(child, owner)) return;
        _claimOwners.AddOrUpdate(child, owner);
    }

    private object NormalizeClaimant(object claimant)
    {
        object cur = claimant;
        for (int i = 0; i < 4 && _claimOwners.TryGetValue(cur, out var owner) && !ReferenceEquals(cur, owner); i++)
            cur = owner;
        return cur;
    }

    public void ClaimAudioPlayback(object claimant, Action pauseCallback)
    {
        object normClaimant = NormalizeClaimant(claimant);
        object? normActive = _activeAudioClaimant is not null ? NormalizeClaimant(_activeAudioClaimant) : null;
        if (normActive is not null && !ReferenceEquals(normActive, normClaimant))
        {
            try { _pauseActiveAudio?.Invoke(); } catch { }
        }
        if (!ReferenceEquals(normActive, normClaimant) || ReferenceEquals(claimant, normClaimant))
            _pauseActiveAudio = pauseCallback;
        _activeAudioClaimant = normClaimant;
    }

    public string? GetActiveAudioBookId()
    {
        if (Library?.SystemIntegration.IsAudioPlaying != true) return null;
        foreach (var w in EnumerateAllShellWindows())
        {
            foreach (var tab in w.Tabs)
            {
                if (tab.IsAudioPlaying && !string.IsNullOrEmpty(tab.BookId))
                    return tab.BookId;
            }
        }
        return null;
    }

    public bool IsPodcastActiveAudioOwner() =>
        Library?.SystemIntegration.IsAudioPlaying == true
        && _libraryView is not null
        && _libraryView.IsAudioPlaying;

    public void ActivateActiveAudioSurface()
    {
        if (IsPodcastActiveAudioOwner())
        {
            ShowShelf();
            _libraryView?.SwitchTab("podcasts");
            return;
        }
        if (ActiveMiniPlayerSource is not null)
        {
            try
            {
                ActiveMiniPlayerSource.JumpToSource();
                return;
            }
            catch (ObjectDisposedException)
            {
                SetActiveMiniPlayerSource(null);
            }
        }
        foreach (var w in EnumerateAllShellWindows())
        {
            var playingTab = w.Tabs.FirstOrDefault(t => t.IsAudioPlaying);
            if (playingTab is not null)
            {
                RestoreAndSelectTab(w, playingTab);
                return;
            }
        }
        ShowShelf();
    }

    public void CloseBookWindows(string bookId)
    {
        if (string.IsNullOrEmpty(bookId)) return;
        foreach (var w in EnumerateAllShellWindows())
        {
            var tab = w.FindTabByBookId(bookId);
            if (tab is not null)
                _ = w.CloseTabAsync(tab, recordHistory: false);
        }
    }

    public void OpenBookRecordInPrimaryShelf(string bookId, string? subTab = null)
    {
        _libraryView?.OpenBookRecord(bookId, subTab);
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Library = new LibraryService();
        ApplyAccentResources();

        Library.Sync.LibraryChanged += () => _mainWindow?.DispatcherQueue.TryEnqueue(() => _libraryView?.RefreshBooks());
        Library.Sync.StatusChanged += () => _mainWindow?.DispatcherQueue.TryEnqueue(() => _libraryView?.UpdateSyncFooter());
        Library.WatchedFolder.Imported += () => _mainWindow?.DispatcherQueue.TryEnqueue(() => _libraryView?.RefreshBooks());

        var cliArgs = Environment.GetCommandLineArgs();
        bool startBackground = cliArgs.Any(a => a == "--background");
        _isHiddenInTray = startBackground;
        Library.Analytics.TrackAppLaunch(startBackground);

        var uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _uiQueue = uiQueue;
        Library.SystemIntegration.AttachUiQueue(uiQueue);
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => uiQueue.TryEnqueue(BroadcastAppearanceChanged);
        }
        catch { }

        Library.SystemIntegration.IsMainWindowHiddenInTray = () => _isHiddenInTray;
        Library.SystemIntegration.ShowShelfCallback = () => uiQueue.TryEnqueue(ShowShelf);
        Library.SystemIntegration.OpenSettingsCallback = () => uiQueue.TryEnqueue(() =>
        {
            if (_isHiddenInTray)
            {
                _isHiddenInTray = false;
                Library.Analytics.TrackAppReopened();
            }
            OpenSettings("general");
        });
        Library.SystemIntegration.PlayPodcastIssueCallback = id => uiQueue.TryEnqueue(() => OpenPodcastIssue(id));
        Library.SystemIntegration.ExitAppCallback = () => uiQueue.TryEnqueue(ExitApplicationCleanly);

        try { Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += OnInstanceActivated; } catch { }

        ReaderWebViewHost.PrewarmAsync(Library.DataDir);
        Library.Ecdict.PrewarmAsync();
        Speech.AudioPlayer.PrewarmVoicesAsync();

        if (startBackground)
            Library.SystemIntegration.EnsureTrayVisible(forceVisible: true);
        else
            ShowShelf();

        if (Library.Sync.IsEnabled) _ = Library.Sync.SyncNowAsync();
        _ = Library.Updates.CheckForUpdatesAsync(manual: false);
        _syncTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMinutes(5) };
        _syncTimer.Tick += (_, _) => { if (Library.Sync.IsEnabled) _ = Library.Sync.SyncNowAsync(); };
        _syncTimer.Start();

        var files = new List<string>();
        string? deepLinkUri = null;
        bool openSettings = false;
        string? settingsPage = null;
        string? shelfTab = null;
        string? viewModeOverride = null;
#if DEBUG
        bool seedDemo = false;
        string? debugOpenQuery = null;
        string? debugSplitQuery = null;
        string? debugRecordQuery = null;
        string? debugRecordTab = null;
        string? debugPanel = null;
        string? debugLeftPanel = null;
        bool debugListenBar = false;
        bool debugQuickSearch = false;
        string? debugTheme = null;
        uint? debugSection = null;
#endif
        for (int i = 1; i < cliArgs.Length; i++)
        {
            if (cliArgs[i] == "--library") { i++; continue; }
            if (cliArgs[i] == "--background") continue;
            if (cliArgs[i] == "--tab" && i + 1 < cliArgs.Length) { shelfTab = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--view" && i + 1 < cliArgs.Length) { viewModeOverride = cliArgs[++i]; continue; }
#if DEBUG
            if (cliArgs[i] == "--seed-demo") { seedDemo = true; continue; }
            if (cliArgs[i] == "--open" && i + 1 < cliArgs.Length) { debugOpenQuery = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--split" && i + 1 < cliArgs.Length) { debugSplitQuery = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--record" && i + 1 < cliArgs.Length) { debugRecordQuery = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--record-tab" && i + 1 < cliArgs.Length) { debugRecordTab = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--section" && i + 1 < cliArgs.Length && uint.TryParse(cliArgs[++i], out var ds)) { debugSection = ds; continue; }
            if (cliArgs[i] == "--panel" && i + 1 < cliArgs.Length) { debugPanel = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--left-panel" && i + 1 < cliArgs.Length) { debugLeftPanel = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--listen-bar") { debugListenBar = true; continue; }
            if (cliArgs[i] == "--quick-search") { debugQuickSearch = true; continue; }
            if (cliArgs[i] == "--dark") { debugTheme = "dark"; continue; }
            if (cliArgs[i] == "--light") { debugTheme = "light"; continue; }
#endif
            if (cliArgs[i] == "--settings")
            {
                openSettings = true;
                if (i + 1 < cliArgs.Length && !cliArgs[i + 1].StartsWith("-") && !File.Exists(cliArgs[i + 1]))
                    settingsPage = cliArgs[++i];
                continue;
            }
            if (cliArgs[i].StartsWith("yuyue://", StringComparison.OrdinalIgnoreCase))
            {
                deepLinkUri = cliArgs[i];
                continue;
            }
            if (File.Exists(cliArgs[i])) files.Add(cliArgs[i]);
        }

        if (!string.IsNullOrWhiteSpace(viewModeOverride))
        {
            Library.Prefs.ShelfViewMode = viewModeOverride;
            Library.SavePrefs();
            _libraryView?.ApplyAppearance();
        }

#if DEBUG
        if (!string.IsNullOrEmpty(debugTheme))
        {
            Library.Prefs.ThemeMode = debugTheme;
            Library.SavePrefs();
            BroadcastAppearanceChanged();
        }
        if (seedDemo) SeedDemoLibrary();
#endif

        if (files.Count > 0 && _libraryView is not null)
        {
            _ = _libraryView.ImportPathsAsync(files, openFirst: true, source: "share_in");
        }
#if DEBUG
        else if (!string.IsNullOrEmpty(debugOpenQuery))
        {
            BookSummary? MatchBook(string q) => Library.GetKeptBooks().FirstOrDefault(b =>
                b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                b.Authors.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                (q.Equals("hlm", StringComparison.OrdinalIgnoreCase) && b.Authors.Any(a => a.Contains("Cao", StringComparison.OrdinalIgnoreCase))));

            var match = MatchBook(debugOpenQuery);
            if (match is not null)
            {
                string? splitId = !string.IsNullOrEmpty(debugSplitQuery) ? MatchBook(debugSplitQuery)?.Id : null;
                OpenBook(match.Id, section: debugSection, splitBookId: splitId);
                if (_mainWindow?.FindTabByBookId(match.Id) is ReaderTabView rtab)
                {
                    if (!string.IsNullOrEmpty(debugLeftPanel)) rtab.PrimaryPane.OpenLeftPane(debugLeftPanel);
                    rtab.PrimaryPane.ConfigureDebugView(debugPanel, debugListenBar);
                }
            }
        }
#endif
        else if (!startBackground && !openSettings && shelfTab is null)
        {
            if (Library.Prefs.RestoreLastTabs && Library.Prefs.LastOpenTabs.Count > 0)
            {
                var existingIds = Library.GetShelfBooks().Select(b => b.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var saved in Library.Prefs.LastOpenTabs.Take(6))
                {
                    if (existingIds.Contains(saved.BookId))
                        OpenBook(saved.BookId, splitBookId: saved.SplitBookId);
                }
            }
            else if (Library.Prefs.ReopenLast)
            {
                var last = Library.GetShelfBooks()
                    .Where(b => b.OpenedAt is not null)
                    .OrderByDescending(b => b.OpenedAt)
                    .FirstOrDefault();
                if (last is not null) OpenBook(last.Id);
            }
        }

#if DEBUG
        if (!string.IsNullOrEmpty(debugRecordQuery))
        {
            var recMatch = Library.GetKeptBooks().FirstOrDefault(b =>
                b.Title.Contains(debugRecordQuery, StringComparison.OrdinalIgnoreCase) ||
                b.Id.Contains(debugRecordQuery, StringComparison.OrdinalIgnoreCase) ||
                (debugRecordQuery.Equals("hlm", StringComparison.OrdinalIgnoreCase) && b.Authors.Any(a => a.Contains("Cao", StringComparison.OrdinalIgnoreCase))));
            if (recMatch is not null)
            {
                ShowShelf();
                _libraryView?.OpenBookRecord(recMatch.Id, debugRecordTab);
            }
        }
#endif

        if (shelfTab is not null)
        {
            ShowShelf();
            _libraryView?.SwitchTab(shelfTab);
        }

        if (openSettings)
            OpenSettings(settingsPage ?? "general");

#if DEBUG
        if (debugQuickSearch && _mainWindow is { } qsWin)
        {
            qsWin.DispatcherQueue.TryEnqueue(async () =>
            {
                await Task.Delay(350);
                if (qsWin.WindowXamlRoot is { } xr)
                    _ = Shelf.QuickSearchDialog.ShowAsync(xr, Library, qsWin.ActiveTab?.BookId);
            });
        }
#endif

        if (!string.IsNullOrEmpty(deepLinkUri))
            HandleDeepLink(deepLinkUri);

#if DEBUG
        if (Environment.GetEnvironmentVariable("YUYUE_DUMP_WINDOW") is string dumpWinPath && !string.IsNullOrEmpty(dumpWinPath))
            _ = DumpActiveWindowForDebugAsync(dumpWinPath);
#endif
    }

#if DEBUG
    private async Task DumpActiveWindowForDebugAsync(string path)
    {
        bool hasReader = _mainWindow?.ActiveTab is ReaderTabView;
        int baseWait = hasReader ? 5200 : 1400;
        if (int.TryParse(Environment.GetEnvironmentVariable("YUYUE_DUMP_DELAY_MS"), out int customDelay) && customDelay >= 200)
            baseWait = customDelay;
        await Task.Delay(baseWait);

        MainWindow? activeWin = _secondaryWindows.LastOrDefault() ?? _mainWindow;
        if (activeWin is not null)
        {
            try { await activeWin.SaveCompositeScreenshotAsync(path); } catch { }
        }
        if (Environment.GetEnvironmentVariable("YUYUE_DUMP_EXIT") == "1")
        {
            await Task.Delay(120);
            ExitApplicationCleanly();
        }
    }

    private void SeedDemoLibrary()
    {
        if (Library.GetShelfBooks().Count > 0) return;
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));
        string fixtures = Path.Combine(repoRoot, "fixtures", "books");
        if (!Directory.Exists(fixtures)) return;

        string? hlmId = null, gatsbyId = null, prideId = null, beiyingId = null, pdfId = null;
        foreach (var file in new[] { "hongloumeng.epub", "great-gatsby.epub", "pride-and-prejudice.epub", "beiying.epub", "sample.pdf" })
        {
            string full = Path.Combine(fixtures, file);
            if (!File.Exists(full)) continue;
            try
            {
                var b = Library.ImportBook(full);
                if (file.StartsWith("hongloumeng")) hlmId = b.Id;
                else if (file.StartsWith("great-gatsby")) gatsbyId = b.Id;
                else if (file.StartsWith("pride")) prideId = b.Id;
                else if (file.StartsWith("beiying")) beiyingId = b.Id;
                else { pdfId = b.Id; Library.SetStatus(b.Id, ReadingStatus.Finished); }
            }
            catch { }
        }

        if (pdfId is not null)
        {
            try
            {
                string? pdfPath = Library.TryOriginalFile(pdfId);
                if (!string.IsNullOrEmpty(pdfPath) && File.Exists(pdfPath))
                {
                    var layout = PdfInfoReader.ExtractPageLayout(pdfPath, 0);
                    Library.Core.SetPdfInfo(pdfId, new PdfInfo(5, "sample", [], (ulong)Math.Max(100, layout.Text.Length * 5), null));
                    var sents = YuyueCoreMethods.PageSentences(layout.Text);
                    var body = sents.Where(s => s.Text.Trim().Length >= 10).ToArray();
                    if (body.Length >= 1)
                        Library.AddMark(pdfId, 0, new TextPoint(0, body[0].Start), new TextPoint(0, body[0].End), body[0].Text.Trim(), MarkStyle.Yellow, null);
                    if (body.Length >= 5)
                        Library.AddMark(pdfId, 0, new TextPoint(0, body[4].Start), new TextPoint(0, body[4].End), body[4].Text.Trim(), MarkStyle.Wave, "平淡白描里全是沉重。");
                }
            }
            catch { }
        }

        if (beiyingId is not null) Library.SetStatus(beiyingId, ReadingStatus.Finished);
        if (prideId is not null) Library.SetStatus(prideId, ReadingStatus.Want);

        uint PickProseSection(string id, double targetRatio)
        {
            var d = Library.GetBookDetail(id);
            int start = Math.Clamp((int)Math.Round(d.Sections.Length * targetRatio), 0, Math.Max(0, d.Sections.Length - 1));
            for (int offset = 0; offset < d.Sections.Length; offset++)
            {
                uint cand = (uint)((start + offset) % Math.Max(1, d.Sections.Length));
                var sents = Library.GetSpokenSentences(id, cand);
                if (sents.Count >= 10 && sents.Sum(s => s.Length) >= 900) return cand;
            }
            return (uint)start;
        }

        if (gatsbyId is not null)
        {
            Library.SetStatus(gatsbyId, ReadingStatus.Reading);
            uint gSec = PickProseSection(gatsbyId, 0.24);
            Library.SaveProgress(gatsbyId, gSec, 280);
            Library.AddTime(gatsbyId, gSec, 1260, 420);
            try
            {
                Library.AddWord("vulnerable", "/ˈvʌlnərəbl/", "adj. 易受伤害的；脆弱的", gatsbyId, gSec, 0, "In my younger and more vulnerable years my father gave me some advice.");
                var w2 = Library.AddWord("consequence", "/ˈkɒnsɪkwəns/", "n. 结果；重要性", gatsbyId, gSec, 2, "Reserving judgments is a matter of infinite hope.");
                Library.SetWordKnown(w2.Word, true);
            }
            catch { }
        }

        if (hlmId is not null)
        {
            Library.SetStatus(hlmId, ReadingStatus.Reading);
            uint sec = PickProseSection(hlmId, 0.36);
            Library.SaveProgress(hlmId, sec, 0);
            Library.AddTime(hlmId, sec, 2400, 900);
            try
            {
                var sents = Library.GetSpokenSentences(hlmId, sec);
                if (sents.Count >= 5)
                {
                    string s0 = sents[1];
                    string s1 = sents[2];
                    string s2 = sents[4];
                    Library.AddMark(hlmId, sec, new TextPoint(1, 0), new TextPoint(1, (uint)Math.Min(28, s0.Length)), s0[..Math.Min(28, s0.Length)], MarkStyle.Yellow, null);
                    Library.AddMark(hlmId, sec, new TextPoint(2, 0), new TextPoint(2, (uint)Math.Min(32, s1.Length)), s1[..Math.Min(32, s1.Length)], MarkStyle.Wave, "所有的伏笔都在不经意的闲笔里悄然种下。");
                    Library.AddMark(hlmId, sec, new TextPoint(4, 0), new TextPoint(4, (uint)Math.Min(26, s2.Length)), s2[..Math.Min(26, s2.Length)], MarkStyle.Pink, "读懂热闹很容易，读出苍凉却要走很远的路。");
                }
            }
            catch { }
        }

        try
        {
            var c1 = Library.CreateCollection("文学经典", hlmId);
            if (beiyingId is not null) Library.SetInCollection(c1.Id, beiyingId, true);
            var c2 = Library.CreateCollection("英文原版", gatsbyId);
            if (prideId is not null) Library.SetInCollection(c2.Id, prideId, true);
            Library.CreateCollection("睡前读物", beiyingId);
            Library.Prefs.PinnedCollectionIds = [c1.Id, c2.Id];
            Library.SavePrefs();
        }
        catch { }

        _libraryView?.RefreshBooks();
    }
#endif

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;

    private static void BringWindowToFront(Window win)
    {
        try
        {
            IntPtr hwnd = WindowNative.GetWindowHandle(win);
            if (hwnd != IntPtr.Zero)
            {
                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                SetForegroundWindow(hwnd);
            }
        }
        catch { }
    }

    private void ExitApplicationCleanly()
    {
        if (_exitingApp) return;
        _exitingApp = true;
        try { _syncTimer?.Stop(); } catch { }
        try { _pauseActiveAudio?.Invoke(); } catch { }
        if (_mainWindow is not null && Library.Prefs.RestoreLastTabs)
        {
            Library.Prefs.LastOpenTabs = _mainWindow.CaptureOpenTabsState();
            Library.SavePrefs();
        }
        try { _miniPlayerWindow?.Close(); } catch { }
        foreach (var w in _secondaryWindows.ToArray())
            try { w.ForceCloseWindow(); } catch { }
        try { _mainWindow?.ForceCloseWindow(); } catch { }
        try { Library?.Dispose(); } catch { }
        Environment.Exit(0);
    }

    private void CheckExitIfAllWindowsClosed()
    {
        if (_exitingApp) return;
        if (_mainWindow is not null || _secondaryWindows.Count > 0) return;
        if (_isHiddenInTray) return;
        if (Library?.Prefs.AutoLaunchOnLogin == true && Library.Prefs.CloseWindowBehavior != "exit") return;
        ExitApplicationCleanly();
    }

    private MainWindow EnsureMainWindow()
    {
        if (_mainWindow is not null) return _mainWindow;

        var win = new MainWindow(Library, isPrimaryShell: true);
        _libraryView = new LibraryView(win, Library);
        win.AddTab(_libraryView, select: true);

        win.SettingsTabFactory = page => new SettingsView(win, Library, page, win.CloseSettings);
        win.FilesDroppedHandler = async paths =>
        {
            if (_libraryView is not null)
                await _libraryView.ImportPathsAsync(paths, openFirst: false, source: "drag_drop");
        };
        win.TabTearOutRequested += TearOutTabToNewWindow;
        win.ShellWindowClosingToTray += _ =>
        {
            _isHiddenInTray = true;
            Library.Analytics.TrackAppBackgrounded();
        };
        win.Closed += (_, _) =>
        {
            _mainWindow = null;
            _libraryView = null;
            CheckExitIfAllWindowsClosed();
        };

        _mainWindow = win;
        return win;
    }

    public void ShowShelf()
    {
        var win = EnsureMainWindow();
        if (_isHiddenInTray)
        {
            _isHiddenInTray = false;
            Library.Analytics.TrackAppReopened();
        }
        try { win.AppWindow.Show(); } catch { }
        win.Activate();
        BringWindowToFront(win);
        if (_libraryView is not null) win.SelectTab(_libraryView);
    }

    public void OpenPodcastIssue(string issueId)
    {
        ShowShelf();
        _libraryView?.OpenPodcastInline(issueId);
    }

    public void TryOpenBookFromAnywhere(BookSummary book, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        if (book.Record || !book.Available)
        {
            ShowShelf();
            _libraryView?.TryOpenBook(book, autoListen, section, sentence, askQuestion, searchQuery);
            return;
        }
        OpenBook(book.Id, autoListen, section, sentence, askQuestion, searchQuery);
    }

    public void OpenBookInNewWindow(string bookId, bool autoListen = false)
    {
        OpenBook(bookId, autoListen, forceNewWindow: true);
    }

    public void TearOutTabToNewWindow(MainWindow source, ITabContent tab)
    {
        var detached = source.DetachTabForTransfer(tab);
        if (detached is null) return;
        var win = CreateSecondaryWindow();
        detached.SetOwnerWindow(win);
        win.AddTab(detached, select: true);
        win.Activate();
        BringWindowToFront(win);
    }

    private MainWindow CreateSecondaryWindow()
    {
        var win = new MainWindow(Library, isPrimaryShell: false);
        win.SettingsTabFactory = page => new SettingsView(win, Library, page, win.CloseSettings);
        win.FilesDroppedHandler = async paths =>
        {
            ShowShelf();
            if (_libraryView is not null)
                await _libraryView.ImportPathsAsync(paths, openFirst: false, source: "drag_drop");
        };
        win.TabTearOutRequested += TearOutTabToNewWindow;
        win.Closed += (_, _) =>
        {
            _secondaryWindows.Remove(win);
            CheckExitIfAllWindowsClosed();
        };
        _secondaryWindows.Add(win);
        return win;
    }

    public void OpenBook(
        string bookId,
        bool autoListen = false,
        uint? section = null,
        int? sentence = null,
        string? askQuestion = null,
        string? searchQuery = null,
        string? splitBookId = null,
        bool forceNewWindow = false)
    {
        // 1. Focus existing open tab if already open in any window
        foreach (var w in EnumerateAllShellWindows())
        {
            var existing = w.FindTabByBookId(bookId);
            if (existing is not null)
            {
                try { w.AppWindow.Show(); } catch { }
                w.Activate();
                BringWindowToFront(w);
                w.SelectTab(existing);

                if (existing is ReaderTabView rtab)
                {
                    if (section is uint s) rtab.PrimaryPane.JumpTo(s, sentence ?? 0);
                    if (autoListen) rtab.PrimaryPane.TriggerListen();
                    if (!string.IsNullOrWhiteSpace(askQuestion)) rtab.PrimaryPane.AskCompanion(askQuestion);
                    if (!string.IsNullOrWhiteSpace(searchQuery)) rtab.PrimaryPane.HighlightSearch(searchQuery);
                    if (!string.IsNullOrWhiteSpace(splitBookId)) rtab.OpenSplitBook(splitBookId);
                }
                else if (existing is PdfTabView ptab)
                {
                    if (sentence is int page) ptab.JumpToPage(page);
                    if (autoListen) ptab.TriggerListen();
                }
                else if (existing is AudiobookTabView atab)
                {
                    if (section == 0 && sentence == 0) atab.JumpToStartAndPlay();
                    else if (autoListen) atab.EnsurePlaying();
                }
                return;
            }
        }

        // 2. Determine book format & validate local file
        BookFormat fmt = BookFormat.Epub;
        string knownTitle = "书籍";
        try
        {
            var found = Library.GetKeptBooks().FirstOrDefault(b => b.Id == bookId);
            if (found is not null) { fmt = found.Format; knownTitle = found.Title; }
            else
            {
                var det = Library.GetBookDetail(bookId);
                fmt = det.Summary.Format;
                knownTitle = det.Summary.Title;
            }
        }
        catch { }

        if (!ReaderRecapAndSleepHelper.TryValidateBookOpen(Library, bookId, fmt, out string failTitle, out string failDetail))
        {
            ShowShelf();
            if (_mainWindow is not null)
            {
                _ = _mainWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    await Task.Delay(50);
                    if (_mainWindow?.WindowXamlRoot is XamlRoot xr)
                    {
                        await ReaderRecapAndSleepHelper.ShowBookOpenFailureAsync(
                            _mainWindow, xr, Library, knownTitle, failTitle, failDetail,
                            reimportedId =>
                            {
                                _libraryView?.RefreshBooks();
                                OpenBook(reimportedId, autoListen);
                                return Task.CompletedTask;
                            });
                    }
                });
            }
            return;
        }

        // 3. Choose target window (single-window TabView by default, or standalone window if configured)
        MainWindow targetWin = (!forceNewWindow && Library.Prefs.OpenBookInTab)
            ? EnsureMainWindow()
            : CreateSecondaryWindow();

        ITabContent tab = fmt switch
        {
            BookFormat.Audio => new AudiobookTabView(targetWin, Library, bookId, autoPlay: autoListen, startFromBeginning: section == 0 && sentence == 0),
            BookFormat.Pdf => new PdfTabView(targetWin, Library, bookId, autoListen, sentence is int p && p >= 0 ? (uint)p : null),
            _ => new ReaderTabView(targetWin, Library, bookId, autoListen, section, sentence, askQuestion, searchQuery, splitBookId),
        };

        targetWin.AddTab(tab, select: true);
        try { targetWin.AppWindow.Show(); } catch { }
        targetWin.Activate();
        BringWindowToFront(targetWin);
    }

    public void OpenSettings(string? initialPage = null)
    {
        var win = EnsureMainWindow();
        try { win.AppWindow.Show(); } catch { }
        win.Activate();
        BringWindowToFront(win);
        win.ShowSettings(initialPage ?? "general");
    }

    public void BroadcastAppearanceChanged()
    {
        ApplyAccentResources();
        foreach (var w in EnumerateAllShellWindows()) w.ApplyAppearance();
        _miniPlayerWindow?.ApplyAppearance();
    }

    public void BroadcastReaderSettingsChanged()
    {
        foreach (var w in EnumerateAllShellWindows())
            foreach (var tab in w.Tabs) tab.OnReaderSettingsChanged();
    }

    public void BroadcastVoiceChanged()
    {
        foreach (var w in EnumerateAllShellWindows())
            foreach (var tab in w.Tabs) tab.OnVoiceChanged();
    }

    public void BroadcastForeignPrefsChanged()
    {
        foreach (var w in EnumerateAllShellWindows())
            foreach (var tab in w.Tabs) tab.OnForeignPrefsChanged();
    }

    public void ApplyAccentResources()
    {
        var tint = Library.Prefs.Tint;
        string[] baseKeys =
        [
            "YYAccentBrush",
            "AccentFillColorDefaultBrush",
            "SliderTrackValueFill",
            "SliderThumbBackground",
            "ToggleSwitchFillOn",
            "TextControlBorderBrushFocused",
        ];
        string[] hoverKeys =
        [
            "AccentFillColorSecondaryBrush",
            "SliderTrackValueFillPointerOver",
            "SliderThumbBackgroundPointerOver",
            "ToggleSwitchFillOnPointerOver",
        ];
        string[] pressKeys =
        [
            "AccentFillColorTertiaryBrush",
            "SliderTrackValueFillPressed",
            "SliderThumbBackgroundPressed",
            "ToggleSwitchFillOnPressed",
        ];

        static Windows.UI.Color ShiftShade(Windows.UI.Color c, int delta) => Windows.UI.Color.FromArgb(
            c.A,
            (byte)Math.Clamp(c.R + delta, 0, 255),
            (byte)Math.Clamp(c.G + delta, 0, 255),
            (byte)Math.Clamp(c.B + delta, 0, 255));

        void UpdateDict(ResourceDictionary dict, YYTintColors c, bool dark)
        {
            var hover = ShiftShade(c.Fill, dark ? 14 : -12);
            var press = ShiftShade(c.Fill, dark ? -10 : -24);
            foreach (var k in baseKeys)
                if (dict.TryGetValue(k, out var v) && v is Microsoft.UI.Xaml.Media.SolidColorBrush b) b.Color = c.Fill;
            foreach (var k in hoverKeys)
                if (dict.TryGetValue(k, out var v) && v is Microsoft.UI.Xaml.Media.SolidColorBrush b) b.Color = hover;
            foreach (var k in pressKeys)
                if (dict.TryGetValue(k, out var v) && v is Microsoft.UI.Xaml.Media.SolidColorBrush b) b.Color = press;
            if (dict.TryGetValue("YYAccentSoftBrush", out var s) && s is Microsoft.UI.Xaml.Media.SolidColorBrush soft) soft.Color = c.Soft;
            if (dict.TryGetValue("YYAccentEdgeBrush", out var e) && e is Microsoft.UI.Xaml.Media.SolidColorBrush edge) edge.Color = c.Edge;
        }

        bool appDark = ThemeHelper.IsDark(Library.Prefs.ToElementTheme());
        ThemeHelper.ApplyControlThemeResources(Resources, appDark, tint);
        if (Resources.ThemeDictionaries.TryGetValue("Light", out var l) && l is ResourceDictionary ld) UpdateDict(ld, tint.Light, dark: false);
        if (Resources.ThemeDictionaries.TryGetValue("Dark", out var d) && d is ResourceDictionary dd) UpdateDict(dd, tint.Dark, dark: true);
    }

    internal static (string Action, string? Target)? ParseDeepLinkUri(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri))
            return null;
        if (!string.Equals(uri.Scheme, "yuyue", StringComparison.OrdinalIgnoreCase))
            return null;
        string action = (uri.Host ?? "").Trim().ToLowerInvariant();
        string target = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        if (string.IsNullOrEmpty(action)) return null;
        return (action, string.IsNullOrWhiteSpace(target) ? null : target);
    }

    private void HandleDeepLink(string rawUri)
    {
        var parsed = ParseDeepLinkUri(rawUri);
        if (parsed is null) return;
        var (action, target) = parsed.Value;
        switch (action)
        {
            case "open" when !string.IsNullOrWhiteSpace(target):
                OpenBook(target);
                break;
            case "settings":
                OpenSettings(target ?? "general");
                break;
            case "tab" when !string.IsNullOrWhiteSpace(target):
                ShowShelf();
                _libraryView?.SwitchTab(target);
                break;
            case "podcast" when !string.IsNullOrWhiteSpace(target):
                OpenPodcastIssue(target);
                break;
            default:
                ShowShelf();
                break;
        }
    }

    private void OnInstanceActivated(object? sender, Microsoft.Windows.AppLifecycle.AppActivationArguments e)
    {
        var files = new List<string>();
        string? deepLinkUri = null;
        string? settingsPage = null;
        string? shelfTab = null;

        try
        {
            if (e.Data is Windows.ApplicationModel.Activation.IProtocolActivatedEventArgs protoArgs && protoArgs.Uri is not null)
                deepLinkUri = protoArgs.Uri.AbsoluteUri;
            else if (e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            {
                foreach (var item in fileArgs.Files)
                    if (item is Windows.Storage.StorageFile sf && File.Exists(sf.Path)) files.Add(sf.Path);
            }
            else if (e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launchArgs
                     && !string.IsNullOrWhiteSpace(launchArgs.Arguments))
            {
                var tokens = SplitCommandLineArgs(launchArgs.Arguments);
                for (int i = 0; i < tokens.Count; i++)
                {
                    string tok = tokens[i];
                    if (tok == "--library") { i++; continue; }
                    if (tok == "--background" || tok == "--relaunch") continue;
                    if (tok == "--tab" && i + 1 < tokens.Count) { shelfTab = tokens[++i]; continue; }
                    if (tok == "--settings")
                    {
                        settingsPage = (i + 1 < tokens.Count && !tokens[i + 1].StartsWith("-") && !File.Exists(tokens[i + 1]))
                            ? tokens[++i] : "general";
                        continue;
                    }
                    if (tok.StartsWith("yuyue://", StringComparison.OrdinalIgnoreCase)) { deepLinkUri = tok; continue; }
                    if (File.Exists(tok)) files.Add(tok);
                }
            }
        }
        catch { }

        _uiQueue?.TryEnqueue(() =>
        {
            bool wasHidden = _isHiddenInTray;
            ShowShelf();
            if (!wasHidden) Library.Analytics.TrackAppReopened();
            if (shelfTab is not null) _libraryView?.SwitchTab(shelfTab);
            if (settingsPage is not null) OpenSettings(settingsPage);
            if (!string.IsNullOrEmpty(deepLinkUri)) HandleDeepLink(deepLinkUri);
            if (files.Count > 0 && _libraryView is not null)
                _ = _libraryView.ImportPathsAsync(files, openFirst: true, source: "share_in");
        });
    }

    private static List<string> SplitCommandLineArgs(string cmdLine)
    {
        var result = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool inQuotes = false;
        foreach (char ch in cmdLine)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (cur.Length > 0) { result.Add(cur.ToString()); cur.Clear(); }
                continue;
            }
            cur.Append(ch);
        }
        if (cur.Length > 0) result.Add(cur.ToString());
        return result;
    }

    public void NotifyShelfRefresh() => _libraryView?.RefreshBooks();

    public void ReloadAllAfterRestore()
    {
        ApplyAccentResources();
        foreach (var w in EnumerateAllShellWindows())
        {
            w.ApplyAppearance();
            foreach (var tab in w.Tabs) tab.OnSnapshotRestored();
        }
        _miniPlayerWindow?.ApplyAppearance();
    }

    private static readonly object LogLock = new();

    internal static void AppendCrashLog(string? dataDir, string tag, string message, Exception? ex = null)
    {
        try
        {
            string logDir = string.IsNullOrWhiteSpace(dataDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Yuyue")
                : dataDir;
            Directory.CreateDirectory(logDir);
            string logPath = Path.Combine(logDir, "yuyue.log");
            lock (LogLock)
            {
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 2L * 1024L * 1024L)
                {
                    string prev = logPath + ".1";
                    if (File.Exists(prev)) File.Delete(prev);
                    File.Move(logPath, prev);
                }
                string detail = ex is not null ? $"\r\n{ex}" : "";
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {tag}: {message}{detail}\r\n\r\n");
            }
        }
        catch { }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        System.Diagnostics.Debug.WriteLine($"[Yuyue Unhandled] {e.Exception}");
        AppendCrashLog(Library?.DataDir, "UNHANDLED", e.Message, e.Exception);
    }
}
