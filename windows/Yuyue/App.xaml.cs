using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;
using Yuyue.Audiobook;
using Yuyue.Core;
using Yuyue.Pdf;
using Yuyue.Podcast;
using Yuyue.Reader;
using Yuyue.Settings;
using Yuyue.Shelf;

namespace Yuyue;

public partial class App : Application
{
    public static App CurrentApp => (App)Current;

    public LibraryService Library { get; private set; } = null!;

    private ShelfWindow? _shelfWindow;
    private SettingsWindow? _settingsWindow;
    private readonly Dictionary<string, ReaderWindow> _readerWindows = new();
    private readonly Dictionary<string, PdfReaderWindow> _pdfWindows = new();
    private readonly Dictionary<string, AudiobookWindow> _audioWindows = new();
    private readonly Dictionary<string, PodcastReaderWindow> _podcastWindows = new();
    private DispatcherTimer? _syncTimer;
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiQueue;
    private Windows.UI.ViewManagement.UISettings? _uiSettings;
    private object? _activeAudioClaimant;
    private Action? _pauseActiveAudio;
    private readonly ConditionalWeakTable<object, object> _claimOwners = new();
    private bool _exitingApp;

    /// <summary>
    /// Binds a child audio engine (such as PodcastWorkspaceBuilder's internal TTS player) to its owning surface
    /// so internal TTS synthesis/playback does not self-abort its own owner's audio claim.
    /// </summary>
    public void BindAudioClaimOwner(object child, object owner)
    {
        if (child is null || owner is null || ReferenceEquals(child, owner)) return;
        _claimOwners.AddOrUpdate(child, owner);
    }

    private object NormalizeClaimant(object claimant)
    {
        object cur = claimant;
        for (int i = 0; i < 4 && _claimOwners.TryGetValue(cur, out var owner) && !ReferenceEquals(cur, owner); i++)
        {
            cur = owner;
        }
        return cur;
    }

    /// <summary>
    /// 1:1 <c>NowPlaying.swift</c> (<c>yyAudioClaimed</c>): whenever any reader, PDF, audiobook, or podcast engine
    /// starts speaking/playing, any previously playing engine yields and pauses cleanly.
    /// </summary>
    public void ClaimAudioPlayback(object claimant, Action pauseCallback)
    {
        object normClaimant = NormalizeClaimant(claimant);
        object? normActive = _activeAudioClaimant is not null ? NormalizeClaimant(_activeAudioClaimant) : null;
        if (normActive is not null && !ReferenceEquals(normActive, normClaimant))
        {
            try { _pauseActiveAudio?.Invoke(); } catch { }
        }
        if (!ReferenceEquals(normActive, normClaimant) || ReferenceEquals(claimant, normClaimant))
        {
            _pauseActiveAudio = pauseCallback;
        }
        _activeAudioClaimant = normClaimant;
    }

    /// <summary>
    /// Closes any open reader, PDF, audiobook, or podcast window for a deleted or record-only book (#26).
    /// </summary>
    public void CloseBookWindows(string bookId)
    {
        if (string.IsNullOrEmpty(bookId)) return;
        if (_readerWindows.Remove(bookId, out var rwin))
        {
            try { rwin.Close(); } catch { }
        }
        if (_pdfWindows.Remove(bookId, out var pwin))
        {
            try { pwin.Close(); } catch { }
        }
        if (_audioWindows.Remove(bookId, out var awin))
        {
            try { awin.Close(); } catch { }
        }
        if (_podcastWindows.Remove(bookId, out var podWin))
        {
            try { podWin.Close(); } catch { }
        }
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

        Library.Sync.LibraryChanged += () => _shelfWindow?.DispatcherQueue.TryEnqueue(() => _shelfWindow.RefreshBooks());
        Library.Sync.StatusChanged += () => _shelfWindow?.DispatcherQueue.TryEnqueue(() => _shelfWindow.UpdateSyncFooter());
        Library.WatchedFolder.Imported += () => _shelfWindow?.DispatcherQueue.TryEnqueue(() => _shelfWindow.RefreshBooks());

        var cliArgs = Environment.GetCommandLineArgs();
        bool startBackground = cliArgs.Any(a => a == "--background");

        var uiQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _uiQueue = uiQueue;
        Library.SystemIntegration.AttachUiQueue(uiQueue);
        try
        {
            _uiSettings = new Windows.UI.ViewManagement.UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => uiQueue.TryEnqueue(BroadcastAppearanceChanged);
        }
        catch { }
        Library.SystemIntegration.ShowShelfCallback = () => uiQueue.TryEnqueue(ShowShelf);
        Library.SystemIntegration.OpenSettingsCallback = () => uiQueue.TryEnqueue(() => OpenSettings("general"));
        Library.SystemIntegration.PlayPodcastIssueCallback = id => uiQueue.TryEnqueue(() => OpenPodcastIssue(id));
        Library.SystemIntegration.ExitAppCallback = () => uiQueue.TryEnqueue(() =>
        {
            _exitingApp = true;
            try { _shelfWindow?.Close(); } catch { }
            Library.Dispose();
            Environment.Exit(0);
        });

        try
        {
            Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().Activated += OnInstanceActivated;
        }
        catch { }

        // Prewarm shared WebView2 environment in the background (TECH.md §3)
        ReaderWebViewHost.PrewarmAsync(Library.DataDir);

        if (startBackground)
            Library.SystemIntegration.EnsureTrayVisible(forceVisible: true);
        else
            ShowShelf();

        // Background auto-sync (startup + every 5 minutes when enabled), 24h silent update check, & offline feedback flush
        if (Library.Sync.IsEnabled) _ = Library.Sync.SyncNowAsync();
        _ = Library.Updates.CheckForUpdatesAsync(manual: false);
        _ = Library.Updates.FlushOfflineFeedbackQueueAsync();
        _syncTimer = new DispatcherTimer { Interval = System.TimeSpan.FromMinutes(5) };
        _syncTimer.Tick += (_, _) => { if (Library.Sync.IsEnabled) _ = Library.Sync.SyncNowAsync(); };
        _syncTimer.Start();

        var files = new List<string>();
        string? deepLinkUri = null;
        bool openSettings = false;
        string? settingsPage = null;
        string? shelfTab = null;
#if DEBUG
        bool seedDemo = false;
        string? debugOpenQuery = null;
        string? debugPanel = null;
        bool debugListenBar = false;
        uint? debugSection = null;
#endif
        for (int i = 1; i < cliArgs.Length; i++)
        {
            if (cliArgs[i] == "--library") { i++; continue; }
            if (cliArgs[i] == "--background") continue;
            if (cliArgs[i] == "--tab" && i + 1 < cliArgs.Length) { shelfTab = cliArgs[++i]; continue; }
#if DEBUG
            if (cliArgs[i] == "--seed-demo") { seedDemo = true; continue; }
            if (cliArgs[i] == "--open" && i + 1 < cliArgs.Length) { debugOpenQuery = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--section" && i + 1 < cliArgs.Length && uint.TryParse(cliArgs[++i], out var ds)) { debugSection = ds; continue; }
            if (cliArgs[i] == "--panel" && i + 1 < cliArgs.Length) { debugPanel = cliArgs[++i]; continue; }
            if (cliArgs[i] == "--listen-bar") { debugListenBar = true; continue; }
#endif
            if (cliArgs[i] == "--settings")
            {
                openSettings = true;
                if (i + 1 < cliArgs.Length && !cliArgs[i + 1].StartsWith("-") && !File.Exists(cliArgs[i + 1]))
                {
                    settingsPage = cliArgs[++i];
                }
                continue;
            }
            if (cliArgs[i].StartsWith("yuyue://", StringComparison.OrdinalIgnoreCase))
            {
                deepLinkUri = cliArgs[i];
                continue;
            }
            if (File.Exists(cliArgs[i])) files.Add(cliArgs[i]);
        }

#if DEBUG
        if (seedDemo) SeedDemoLibrary();
#endif

        if (files.Count > 0 && _shelfWindow is not null)
        {
            _ = _shelfWindow.ImportPathsAsync(files, openFirst: true);
        }
#if DEBUG
        else if (!string.IsNullOrEmpty(debugOpenQuery))
        {
            var match = Library.GetKeptBooks().FirstOrDefault(b =>
                b.Title.Contains(debugOpenQuery, StringComparison.OrdinalIgnoreCase) ||
                b.Id.Contains(debugOpenQuery, StringComparison.OrdinalIgnoreCase) ||
                b.Authors.Any(a => a.Contains(debugOpenQuery, StringComparison.OrdinalIgnoreCase)) ||
                (debugOpenQuery.Equals("hlm", StringComparison.OrdinalIgnoreCase) && b.Authors.Any(a => a.Contains("Cao", StringComparison.OrdinalIgnoreCase))));
            if (match is not null)
            {
                OpenBook(match.Id, section: debugSection);
                if (_readerWindows.TryGetValue(match.Id, out var rwin))
                    rwin.ConfigureDebugView(debugPanel, debugListenBar);
                else if (_pdfWindows.TryGetValue(match.Id, out var pwin) && !string.IsNullOrEmpty(debugPanel))
                    pwin.OpenDebugSidePanel(debugPanel);
            }
        }
#endif
        else if (!startBackground && Library.Prefs.ReopenLast)
        {
            var last = Library.GetShelfBooks()
                .Where(b => b.OpenedAt is not null)
                .OrderByDescending(b => b.OpenedAt)
                .FirstOrDefault();
            if (last is not null) OpenBook(last.Id);
        }

        if (shelfTab is not null)
        {
            _shelfWindow?.SwitchTab(shelfTab);
        }

        if (openSettings)
        {
            OpenSettings(settingsPage ?? "general");
        }

        if (!string.IsNullOrEmpty(deepLinkUri))
        {
            HandleDeepLink(deepLinkUri);
        }

#if DEBUG
        if (Environment.GetEnvironmentVariable("YUYUE_DUMP_WINDOW") is string dumpWinPath && !string.IsNullOrEmpty(dumpWinPath))
        {
            _ = DumpActiveWindowForDebugAsync(dumpWinPath);
        }
#endif
    }

#if DEBUG
    private async Task DumpActiveWindowForDebugAsync(string path)
    {
        await Task.Delay(1200);
        if (Environment.GetEnvironmentVariable("YUYUE_DEBUG_DIALOG") is { Length: > 0 } dlgKind)
        {
            if (_settingsWindow is not null) _settingsWindow.OpenDebugSheet(dlgKind);
            else _shelfWindow?.OpenDebugSheet(dlgKind);
            await Task.Delay(900);
        }
        else
        {
            await Task.Delay(600);
        }
        UIElement? target = _settingsWindow?.Content
            ?? _audioWindows.Values.LastOrDefault()?.Content
            ?? _pdfWindows.Values.LastOrDefault()?.Content
            ?? _readerWindows.Values.LastOrDefault()?.Content
            ?? _shelfWindow?.Content;
        if (target?.XamlRoot is { } xr)
        {
            foreach (var pop in Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(xr))
            {
                if (pop.Child is Microsoft.UI.Xaml.Controls.ContentDialog cd && cd.Content is UIElement bodyEl)
                {
                    target = bodyEl;
                    break;
                }
            }
        }
        if (target is not null)
        {
            try { await QuoteShareDialog.SaveElementPngAsync(target, path); } catch { }
        }
    }
#endif

#if DEBUG
    private void SeedDemoLibrary()
    {
        if (Library.GetShelfBooks().Count > 0) return;
        string? repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", ".."));
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

            var studioBooks = new[] { hlmId, gatsbyId }.Where(x => x is not null).Cast<string>().ToArray();
            if (studioBooks.Length > 0)
            {
                var brief = Library.Core.BuildStudioBrief(StudioKind.WeeklyDigest, studioBooks, 8, null);
                var draft = Library.Core.FinalizeStudioScript(brief, "");
                Library.Core.PodcastSaveStudioIssue(StudioKind.WeeklyDigest, draft, studioBooks, "zh-Hans");
            }
        }
        catch { }

        _shelfWindow?.RefreshBooks();
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

    private void CheckExitIfAllWindowsClosed()
    {
        if (_exitingApp) return;
        if (_shelfWindow is not null
            || _readerWindows.Count > 0
            || _pdfWindows.Count > 0
            || _audioWindows.Count > 0
            || _podcastWindows.Count > 0)
        {
            return;
        }
        if (Library?.Prefs.AutoLaunchOnLogin == true)
            return;

        _exitingApp = true;
        try { _syncTimer?.Stop(); } catch { }
        try { _pauseActiveAudio?.Invoke(); } catch { }
        try { _settingsWindow?.Close(); } catch { }
        try { Library?.Dispose(); } catch { }
        Environment.Exit(0);
    }

    public void ShowShelf()
    {
        if (_shelfWindow is null)
        {
            _shelfWindow = new ShelfWindow(Library);
            _shelfWindow.AppWindow.Closing += (_, args) =>
            {
                // Only keep resident in system tray when user explicitly enabled login auto-start
                if (!_exitingApp && Library.Prefs.AutoLaunchOnLogin)
                {
                    args.Cancel = true;
                    Library.SystemIntegration.EnsureTrayVisible(forceVisible: true);
                    try { _shelfWindow?.AppWindow.Hide(); } catch { }
                    return;
                }

                // If no reader windows remain open, pause active audio before closing
                if (_readerWindows.Count == 0 && _pdfWindows.Count == 0 && _audioWindows.Count == 0 && _podcastWindows.Count == 0)
                {
                    try { _pauseActiveAudio?.Invoke(); } catch { }
                }
            };
            _shelfWindow.Closed += (_, _) =>
            {
                _shelfWindow = null;
                CheckExitIfAllWindowsClosed();
            };
        }
        try { _shelfWindow.AppWindow.Show(); } catch { }
        _shelfWindow.Activate();
        BringWindowToFront(_shelfWindow);
    }

    public void OpenPodcastIssue(string issueId)
    {
        ShowShelf();
        _shelfWindow?.OpenPodcastInline(issueId);
    }

    public void OpenBook(string bookId, bool autoListen = false, uint? section = null, int? sentence = null, string? askQuestion = null, string? searchQuery = null)
    {
        if (_audioWindows.TryGetValue(bookId, out var existingAudio))
        {
            existingAudio.Activate();
            if (section == 0 && sentence == 0) existingAudio.JumpToStartAndPlay();
            else if (autoListen) existingAudio.EnsurePlaying();
            return;
        }

        if (_pdfWindows.TryGetValue(bookId, out var existingPdf))
        {
            existingPdf.Activate();
            if (sentence is int page) existingPdf.JumpToPage(page);
            if (autoListen) existingPdf.TriggerListen();
            return;
        }

        if (_readerWindows.TryGetValue(bookId, out var existing))
        {
            existing.Activate();
            if (section is uint s) existing.JumpTo(s, sentence ?? 0);
            if (autoListen) existing.TriggerListen();
            if (!string.IsNullOrWhiteSpace(askQuestion)) existing.AskCompanion(askQuestion);
            if (!string.IsNullOrWhiteSpace(searchQuery)) existing.HighlightSearch(searchQuery);
            return;
        }

        BookFormat fmt = BookFormat.Epub;
        string knownTitle = "书籍";
        try
        {
            var found = Library.GetKeptBooks().FirstOrDefault(b => b.Id == bookId);
            if (found is not null)
            {
                fmt = found.Format;
                knownTitle = found.Title;
            }
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
            if (_shelfWindow is not null)
            {
                _ = _shelfWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    // Yield one frame so WindowXamlRoot is ready if ShelfWindow was just created
                    await Task.Delay(50);
                    if (_shelfWindow?.WindowXamlRoot is XamlRoot xr)
                    {
                        await ReaderRecapAndSleepHelper.ShowBookOpenFailureAsync(
                            _shelfWindow,
                            xr,
                            Library,
                            knownTitle,
                            failTitle,
                            failDetail,
                            reimportedId =>
                            {
                                _shelfWindow?.RefreshBooks();
                                OpenBook(reimportedId, autoListen);
                                return Task.CompletedTask;
                            });
                    }
                });
            }
            return;
        }

        if (fmt == BookFormat.Audio)
        {
            bool fromStart = section == 0 && sentence == 0;
            var audioWin = new AudiobookWindow(Library, bookId, autoPlay: autoListen, startFromBeginning: fromStart);
            _audioWindows[bookId] = audioWin;
            audioWin.Closed += (_, _) =>
            {
                _audioWindows.Remove(bookId);
                _shelfWindow?.RefreshBooks();
                CheckExitIfAllWindowsClosed();
            };
            audioWin.Activate();
            return;
        }

        if (fmt == BookFormat.Pdf)
        {
            uint? startPage = sentence is int p && p >= 0 ? (uint)p : null;
            var pdfWin = new PdfReaderWindow(Library, bookId, autoListen, startPage);
            _pdfWindows[bookId] = pdfWin;
            pdfWin.Closed += (_, _) =>
            {
                _pdfWindows.Remove(bookId);
                _shelfWindow?.RefreshBooks();
                CheckExitIfAllWindowsClosed();
            };
            pdfWin.Activate();
            return;
        }

        var win = new ReaderWindow(Library, bookId, autoListen, section, sentence, askQuestion, searchQuery);
        _readerWindows[bookId] = win;
        win.Closed += (_, _) =>
        {
            _readerWindows.Remove(bookId);
            _shelfWindow?.RefreshBooks();
            CheckExitIfAllWindowsClosed();
        };
        win.Activate();
    }

    public void OpenSettings(string? initialPage = null)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(Library, initialPage ?? "general");
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        else if (initialPage is not null)
        {
            _settingsWindow.NavigateTo(initialPage);
        }
        _settingsWindow.Activate();
        BringWindowToFront(_settingsWindow);
    }

    public void BroadcastAppearanceChanged()
    {
        ApplyAccentResources();
        _shelfWindow?.ApplyAppearance();
        foreach (var r in _readerWindows.Values) r.OnAppearanceChanged();
        foreach (var p in _pdfWindows.Values) p.ApplyTintAndChrome();
        foreach (var a in _audioWindows.Values) a.ApplyAppearance();
        foreach (var pod in _podcastWindows.Values) pod.ApplyAppearance();
        _settingsWindow?.ApplyAppearance();
    }

    public void BroadcastReaderSettingsChanged()
    {
        foreach (var r in _readerWindows.Values) r.ReloadReaderSettingsFromCore();
    }

    public void BroadcastVoiceChanged()
    {
        foreach (var r in _readerWindows.Values) r.ReloadVoicePrefs();
        foreach (var p in _pdfWindows.Values) p.ReloadVoicePrefs();
    }

    public void BroadcastForeignPrefsChanged()
    {
        foreach (var r in _readerWindows.Values) r.ReloadForeignPrefs();
    }

    public void ApplyAccentResources()
    {
        var tint = Library.Prefs.Tint;
        string[] baseKeys =
        [
            "YYAccentBrush",
            "AccentFillColorDefaultBrush",
            "SliderTrackValueFill",
            "ToggleSwitchFillOn",
            "TextControlBorderBrushFocused",
        ];
        string[] hoverKeys =
        [
            "AccentFillColorSecondaryBrush",
            "SliderTrackValueFillPointerOver",
            "ToggleSwitchFillOnPointerOver",
        ];
        string[] pressKeys =
        [
            "AccentFillColorTertiaryBrush",
            "SliderTrackValueFillPressed",
            "ToggleSwitchFillOnPressed",
        ];

        static Windows.UI.Color ShiftShade(Windows.UI.Color c, int delta) => Windows.UI.Color.FromArgb(
            c.A,
            (byte)Math.Clamp(c.R + delta, 0, 255),
            (byte)Math.Clamp(c.G + delta, 0, 255),
            (byte)Math.Clamp(c.B + delta, 0, 255));

        void UpdateDict(ResourceDictionary dict, Design.YYTintColors c, bool dark)
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

        bool appDark = Design.ThemeHelper.IsDark(Library.Prefs.ToElementTheme());
        Design.ThemeHelper.ApplyControlThemeResources(Resources, appDark, tint);
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
                _shelfWindow?.SwitchTab(target);
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
            {
                deepLinkUri = protoArgs.Uri.AbsoluteUri;
            }
            else if (e.Data is Windows.ApplicationModel.Activation.IFileActivatedEventArgs fileArgs)
            {
                foreach (var item in fileArgs.Files)
                {
                    if (item is Windows.Storage.StorageFile sf && File.Exists(sf.Path))
                        files.Add(sf.Path);
                }
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
                            ? tokens[++i]
                            : "general";
                        continue;
                    }
                    if (tok.StartsWith("yuyue://", StringComparison.OrdinalIgnoreCase))
                    {
                        deepLinkUri = tok;
                        continue;
                    }
                    if (File.Exists(tok)) files.Add(tok);
                }
            }
        }
        catch { }

        _uiQueue?.TryEnqueue(() =>
        {
            ShowShelf();
            if (shelfTab is not null) _shelfWindow?.SwitchTab(shelfTab);
            if (settingsPage is not null) OpenSettings(settingsPage);
            if (!string.IsNullOrEmpty(deepLinkUri)) HandleDeepLink(deepLinkUri);
            if (files.Count > 0 && _shelfWindow is not null)
                _ = _shelfWindow.ImportPathsAsync(files, openFirst: true);
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

    public void NotifyShelfRefresh() => _shelfWindow?.RefreshBooks();

    public void ReloadAllAfterRestore()
    {
        ApplyAccentResources();
        _shelfWindow?.ApplyAppearance();
        _shelfWindow?.RefreshBooks();
        foreach (var r in _readerWindows.Values) r.ReloadAfterSnapshotRestore();
        foreach (var p in _pdfWindows.Values) p.ReloadAfterSnapshotRestore();
        _settingsWindow?.ApplyAppearance();
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
                // Rotate log file at 2 MB so long-running installs never grow unbounded
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
