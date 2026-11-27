using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Yuyue.Audiobook;
using Yuyue.Design;
using Yuyue.Pdf;
using Yuyue.Podcast;
using Yuyue.Settings;
using Yuyue.Speech;

namespace Yuyue.Core;

public sealed record WordLookupResult(string Headword, string? Phonetic, string Summary, string Source, string? Html = null);

/// <summary>
/// Device-local UI &amp; connection preferences (never synced; secrets stay in <see cref="SecretStore"/>).
/// </summary>
public sealed class DevicePrefs
{
    public string ThemeMode { get; set; } = "system"; // light | dark | system
    public string TintId { get; set; } = YYTints.Default.Id;
    public string AppIconId { get; set; } = "default"; // default | night | camel | sage | mist | blush
    public bool PaperFollowAppearance { get; set; } = true;
    public string PaperLightId { get; set; } = "";
    public string PaperDarkId { get; set; } = "";
    public double CoverWidth { get; set; } = 118.0;
    public bool ShowProgress { get; set; } = true;
    public bool TwoColumns { get; set; } = true;
    public bool ReopenLast { get; set; } = false;
    public bool KeepAwakeWhileListening { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public string UiLanguage { get; set; } = "system"; // system | zh-Hans | en
    public string WatchedFolder { get; set; } = "";
    public string ObsidianFolder { get; set; } = "";
    public bool ObsidianExportOn { get; set; } = true;
    public List<string> PinnedCollectionIds { get; set; } = [];

    // 外文阅读偏好（对齐 MacForeignSettings 默认值）
    public bool ForeignLookup { get; set; } = true;
    public bool ForeignSpeakOnLookup { get; set; } = true;
    public string ForeignAccent { get; set; } = "uk"; // uk | us
    public bool ForeignMarkVocab { get; set; } = true;
    public bool ForeignGlossEnabled { get; set; } = true;
    public byte GlossLevel { get; set; } = 3; // 2=四级, 3=六级, 4=考研, 5=雅思, 6=GRE (0=兼容旧版关闭)
    public bool GlossVocabOnly { get; set; } = false;
    public Dictionary<string, bool> BookTranslationsActive { get; set; } = new();
    public Dictionary<string, string> BookTranslateModes { get; set; } = new(); // off | bilingual | only（对齐 macOS TranslateMode）

    // 阅读窗口亮度与护眼柔光（对齐 MacReaderBrightness）
    public bool ReaderBrightnessFollow { get; set; } = true;
    public double ReaderBrightness { get; set; } = 0.72;
    public bool ReaderEyeCare { get; set; } = false;

    // 沉浸阅读偏好（对齐 MacSettings ReadingSettings）
    public bool ImmersiveAutoEnter { get; set; } = false;
    public bool ImmersiveKeepLine { get; set; } = true;
    public bool ImmersiveHideMarks { get; set; } = false;
    public bool ImmersiveHideHints { get; set; } = true;
    public bool ImmersiveNoAnimation { get; set; } = true;

    // 翻页音效偏好（对齐 macOS PageTurnPrefs）
    public bool PageTurnSound { get; set; } = false;
    public string PageTurnSoundStyle { get; set; } = "softPaper";
    public double PageTurnSoundVolume { get; set; } = 0.60;

    // PDF 阅读偏好（按书保存对齐 PdfModel.swift）
    public string PdfLayout { get; set; } = "scroll"; // scroll | single | double
    public bool PdfNight { get; set; } = false;
    public Dictionary<string, string> PdfBookLayouts { get; set; } = new();
    public HashSet<string> PdfNightBooks { get; set; } = new();

    // 书架与系统偏好（对齐 MacShelfView & MacSettings）
    public bool ShelfSidebarShown { get; set; } = true;
    public HashSet<string> ClosedSidebarGroups { get; set; } = new();
    public bool AutoLaunchOnLogin { get; set; } = false;
    public string UpdateChannel { get; set; } = "stable"; // stable | beta

    // 朗读偏好
    public string VoiceId { get; set; } = "";
    public string VoiceIdEn { get; set; } = "";
    public double SpeechRate { get; set; } = 1.0;
    public string CloudVoiceAzureRegion { get; set; } = "eastasia";
    public bool DramaPreRenderPhone { get; set; } = false;
    public HashSet<string> DramaPreparingBooks { get; set; } = new();
    public Dictionary<string, List<string[]>> CachedCloudVoices { get; set; } = new();
    public Dictionary<string, string> ClonedElevenLabsVoiceIds { get; set; } = new();
    public HashSet<string> ClonedUseElevenLabs { get; set; } = new();
    public Dictionary<string, string> CloudVoiceBaseUrls { get; set; } = new();
    public Dictionary<string, string> CloudVoiceModels { get; set; } = new();
    public Dictionary<string, string> CloudVoiceCustomNames { get; set; } = new();
    public HashSet<string> CloudVoiceEnabled { get; set; } = new();
    public HashSet<string> CloudVoiceDismissed { get; set; } = new();
    public bool AiTierRoutingEnabled { get; set; } = true;
    public bool DramaCloudSaveNarrator { get; set; } = true;
    public long AiDailyLlmTokenCap { get; set; } = 0; // 0 = unlimited
    public long AiDailyTtsCharCap { get; set; } = 0;  // 0 = unlimited
    public Dictionary<string, string> AiProviderFastModels { get; set; } = new();
    public List<string> PinnedFontIds { get; set; } = [];
    public string LastHighlightStyle { get; set; } = "Yellow";
    public bool GlobalEinkMode { get; set; } = false;
    public bool GlobalEinkGray { get; set; } = true;
    public double SettingsInspectorWidth { get; set; } = 304.0;
    public string SettingsLastPage { get; set; } = "general";
    public double ReaderPanelWidth { get; set; } = 320.0;
    public int ReaderWindowWidth { get; set; } = 1080;
    public int ReaderWindowHeight { get; set; } = 740;
    public int ShelfWindowWidth { get; set; } = 1040;
    public int ShelfWindowHeight { get; set; } = 720;
    public int SettingsWindowWidth { get; set; } = 1020;
    public int SettingsWindowHeight { get; set; } = 640;
    public int AudiobookWindowWidth { get; set; } = 960;
    public int AudiobookWindowHeight { get; set; } = 640;
    public string ReaderLastPanel { get; set; } = "";
    public string BooksSort { get; set; } = "recent";
    public string CollectionSort { get; set; } = "custom";
    public double AudiobookRate { get; set; } = 1.0;
    public string QuoteShareLook { get; set; } = "paper";
    public string QuoteShareShape { get; set; } = "card";
    public bool QuoteShareIncludeThought { get; set; } = true;
    public HashSet<string> DismissedTxtDoubtBooks { get; set; } = new();
    public HashSet<string> FinishedPromptedBooks { get; set; } = new();
    public bool FeedbackAttachInfo { get; set; } = true;
    public List<string> FeedbackDraftFiles { get; set; } = [];
    public HashSet<string> RemoteAcquiredUrls { get; set; } = new();
    public Dictionary<string, string> RemoteAcquiredBooks { get; set; } = new();
    public Dictionary<string, float> AudiobookLoudnessGain { get; set; } = new();
    public Dictionary<string, string> SyncCompanionSeenTags { get; set; } = new();
    public Dictionary<string, string> SyncCompanionSentStamps { get; set; } = new();
    public Dictionary<string, string> SyncAudioSeenTags { get; set; } = new();
    public Dictionary<string, string> SyncAudioSentStamps { get; set; } = new();

    public string GetVoiceForLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) && language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return !string.IsNullOrWhiteSpace(VoiceIdEn) ? VoiceIdEn : VoiceId;
        return VoiceId;
    }

    public void SetVoiceForLanguage(string? language, string voiceId)
    {
        if (!string.IsNullOrWhiteSpace(language) && language.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            VoiceIdEn = voiceId;
        else
            VoiceId = voiceId;
    }

    // 笔记导出目标偏好（Obsidian / Notion / flomo / 新枝）
    public bool NotionExportOn { get; set; } = false;
    public string NotionPageId { get; set; } = "";
    public string NotionPageTitle { get; set; } = "";
    public Dictionary<string, string> NotionBookPageIds { get; set; } = new();
    public bool FlomoExportOn { get; set; } = false;
    public string FlomoWebhookUrl { get; set; } = "";
    public bool XinzhiExportOn { get; set; } = false;
    public string XinzhiConnected { get; set; } = "";
    public Dictionary<string, Dictionary<string, int>> ExportSentHashes { get; set; } = new();
    public Dictionary<string, string> ExportLastSummary { get; set; } = new();

    // 更新、反馈、阅读续接与远程书库偏好
    public string UpdateSkippedVersion { get; set; } = "";
    public string UpdateRemindVersion { get; set; } = "";
    public long UpdateRemindUntilMs { get; set; }
    public long? UpdateLastCheckedMs { get; set; }
    public ulong? UpdateCheckedBuild { get; set; }
    public string FeedbackDraftText { get; set; } = "";
    public Dictionary<string, long[]> SleepReturnSpots { get; set; } = new();
    public Dictionary<string, string> AudiobookPairOverrides { get; set; } = new();
    public string RemoteSourcesJson { get; set; } = "";
    public string PodcastStudioVoiceStyle { get; set; } = "studio-duo-zh";
    public Dictionary<string, bool> PodcastRememberedVoices { get; set; } = new();

    // 同步配置
    public string SyncMethod { get; set; } = "none"; // none | webdav | folder | gdrive
    public string WebDavUrl { get; set; } = "https://dav.jianguoyun.com/dav/";
    public string WebDavUser { get; set; } = "";
    public string WebDavFolder { get; set; } = "语阅";
    public string SyncLocalFolder { get; set; } = "";
    public string GoogleClientId { get; set; } = "";
    public string GoogleEmail { get; set; } = "";
    public bool SyncReading { get; set; } = true;
    public bool SyncNotes { get; set; } = true;
    public bool SyncSettings { get; set; } = true;
    public bool SyncFiles { get; set; } = true;
    public bool SyncRecordings { get; set; } = false;
    public long? LastSyncedUnixMs { get; set; }
    public string SyncWrittenVersion { get; set; } = "";
    public List<string> SyncPreviousLeftOut { get; set; } = [];
    public Dictionary<string, string> SyncSeenTags { get; set; } = new();

    // AI 书伴配置（支持多服务商独立存储 Key / Model / URL）
    public string AiProvider { get; set; } = "deepseek";
    public string AiBaseUrl { get; set; } = "https://api.deepseek.com/v1";
    public string AiModel { get; set; } = "deepseek-chat";
    public List<string> ConfiguredAiProviders { get; set; } = ["deepseek"];
    public Dictionary<string, string> AiProviderModels { get; set; } = new();
    public Dictionary<string, List<string>> AiRecentModels { get; set; } = new();
    public Dictionary<string, List<string>> CachedAiProviderModels { get; set; } = new();
    public List<string> StarredAiModels { get; set; } = [];
    public List<string> GlobalRecentAiModels { get; set; } = [];
    public Dictionary<string, string> AiProviderUrls { get; set; } = new();
    public Dictionary<string, string> AiCustomProviderNames { get; set; } = new();
    public List<CompanionPromptItem>? CompanionQuickPrompts { get; set; }

    // 笔记页与背景白噪音偏好（对齐 MacNotesView & SoundLibrary）
    public bool NotesCoversOnly { get; set; } = false;
    public string AmbientSoundId { get; set; } = ""; // "" | rain | fire | stream | night | cafe | wind
    public double AmbientSoundVolume { get; set; } = 0.25;

    // 匿名使用统计偏好（默认开启，可随时在「关于」关闭）
    public bool AnalyticsEnabled { get; set; } = true;
    public string AnalyticsDistinctId { get; set; } = "";
    public string AnalyticsLastVersion { get; set; } = "";

    // Windows 11 原生交互与窗口壳层偏好
    public bool OpenBookInTab { get; set; } = true;
    public bool RestoreLastTabs { get; set; } = true;
    public List<SavedTabEntry> LastOpenTabs { get; set; } = [];
    public string CloseWindowBehavior { get; set; } = "tray_when_playing"; // tray_when_playing | always_tray | exit
    public bool ShelfSingleClickOpen { get; set; } = false; // false = 单击选中 + 双击打开 (Win11默认); true = 单击直开
    public string ShelfViewMode { get; set; } = "grid"; // grid | list
    public string ShelfSortDirection { get; set; } = "desc"; // asc | desc
    public string LastShelfSection { get; set; } = "all"; // all | progress | podcasts | audio | notes | words | collections | col:<id>
    public bool NavPaneCompact { get; set; } = false;
    public string ReaderSpaceAction { get; set; } = "smart"; // smart (静读翻页/听书暂停) | listen (始终朗读暂停)
    public bool UseWarmMica { get; set; } = true;
    public double ReaderLeftPaneWidth { get; set; } = 260.0;
    public bool ReaderLeftPaneOpen { get; set; } = false;
    public string ReaderLeftPaneTab { get; set; } = "toc"; // toc | bookmarks | search
    public int MainWindowWidth { get; set; } = 1240;
    public int MainWindowHeight { get; set; } = 780;
    public bool MainWindowMaximized { get; set; } = false;

    public ElementTheme ToElementTheme() => ThemeMode switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public YYTint Tint => ThemeHelper.ResolveTint(TintId);
}

public sealed record SavedTabEntry(string BookId, string Kind, string? SplitBookId = null);

public sealed class CompanionPromptItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Command { get; set; } = "";
    public string Prompt { get; set; } = "";
}

/// <summary>
/// 1:1 port of macOS <c>CompanionPrompts</c> (<c>BookCompanion.swift:936</c>):
/// user-saved quick prompts (max 8) with customizable <c>/command</c> tokens, surfaced in Companion quick suggestions
/// and the <c>/</c> slash command menu.
/// </summary>
public static class CompanionPromptsHelper
{
    public const int MaxCount = 8;
    public const int MaxLength = 160;
    public const int MaxCommandLength = 20;

    public static readonly HashSet<string> ReservedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "recap", "chapter", "characters", "explain", "quote", "quiz",
        "read", "whole", "model", "clear", "settings",
    };

    public static List<CompanionPromptItem> CreateDefaults() =>
    [
        new() { Id = "default-grammar", Command = "grammar", Prompt = L10n.T("拆解这句语法与长难句") },
        new() { Id = "default-baihua", Command = "baihua", Prompt = L10n.T("把这段古文译成现代白话") },
        new() { Id = "default-logic", Command = "logic", Prompt = L10n.T("提炼核心论点与逻辑") },
    ];

    public static List<CompanionPromptItem> GetItems(DevicePrefs prefs) =>
        prefs.CompanionQuickPrompts is null
            ? CreateDefaults()
            : Normalize(prefs.CompanionQuickPrompts);

    public static bool IsDefault(DevicePrefs prefs)
    {
        if (prefs.CompanionQuickPrompts is null) return true;
        var cur = Normalize(prefs.CompanionQuickPrompts);
        var def = CreateDefaults();
        if (cur.Count != def.Count) return false;
        for (int i = 0; i < cur.Count; i++)
        {
            if (!string.Equals(cur[i].Id, def[i].Id, StringComparison.Ordinal)
                || !string.Equals(cur[i].Command, def[i].Command, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cur[i].Prompt, def[i].Prompt, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    public static string NextDefaultCommand(IEnumerable<CompanionPromptItem> existing)
    {
        var used = new HashSet<string>(ReservedCommands, StringComparer.OrdinalIgnoreCase);
        foreach (var it in existing)
            if (!string.IsNullOrWhiteSpace(it.Command)) used.Add(it.Command);
        int n = 1;
        while (used.Contains($"ask{n}")) n++;
        return $"ask{n}";
    }

    public static string SanitizeCommand(
        string? raw,
        string fallback,
        IEnumerable<CompanionPromptItem> existing,
        string? excludingId = null)
    {
        static string CleanToken(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var trimmed = s.Trim().TrimStart('/', '／');
            var chars = trimmed.Where(c => !char.IsWhiteSpace(c)).ToArray();
            return new string(chars).ToLowerInvariant();
        }

        string stripped = CleanToken(raw);
        string cleanFallback = CleanToken(fallback);
        string baseCmd = string.IsNullOrEmpty(stripped)
            ? (string.IsNullOrEmpty(cleanFallback) ? "ask1" : cleanFallback)
            : stripped;
        if (baseCmd.Length > MaxCommandLength) baseCmd = baseCmd[..MaxCommandLength];

        var used = new HashSet<string>(ReservedCommands, StringComparer.OrdinalIgnoreCase);
        foreach (var it in existing)
        {
            if (excludingId is not null && string.Equals(it.Id, excludingId, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrWhiteSpace(it.Command)) used.Add(it.Command);
        }
        if (!used.Contains(baseCmd)) return baseCmd;

        int n = 2;
        while (true)
        {
            string suffix = n.ToString();
            int prefixLen = Math.Max(1, MaxCommandLength - suffix.Length);
            string candidate = (baseCmd.Length > prefixLen ? baseCmd[..prefixLen] : baseCmd) + suffix;
            if (!used.Contains(candidate)) return candidate;
            n++;
        }
    }

    public static List<CompanionPromptItem> Normalize(IEnumerable<CompanionPromptItem> rawItems)
    {
        var seenPrompts = new HashSet<string>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var outList = new List<CompanionPromptItem>();
        foreach (var raw in rawItems)
        {
            if (raw is null) continue;
            string p = (raw.Prompt ?? "").Trim();
            if (p.Length > MaxLength) p = p[..MaxLength];
            if (string.IsNullOrEmpty(p) || !seenPrompts.Add(p)) continue;

            string id = (raw.Id ?? "").Trim();
            if (string.IsNullOrEmpty(id) || !seenIds.Add(id))
            {
                id = Guid.NewGuid().ToString("N");
                seenIds.Add(id);
            }
            string fb = NextDefaultCommand(outList);
            string cmd = SanitizeCommand(raw.Command, fb, outList, excludingId: id);
            outList.Add(new CompanionPromptItem { Id = id, Command = cmd, Prompt = p });
            if (outList.Count >= MaxCount) break;
        }
        return outList;
    }

    public static bool Upsert(DevicePrefs prefs, CompanionPromptItem item)
    {
        var list = GetItems(prefs);
        string p = (item.Prompt ?? "").Trim();
        if (p.Length > MaxLength) p = p[..MaxLength];
        if (string.IsNullOrEmpty(p)) return false;

        int idx = list.FindIndex(x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
        if (idx >= 0)
        {
            if (list.Where((_, i) => i != idx).Any(x => string.Equals(x.Prompt, p, StringComparison.Ordinal)))
                return false;
            string cmd = SanitizeCommand(item.Command, list[idx].Command, list, excludingId: item.Id);
            list[idx] = new CompanionPromptItem { Id = item.Id, Command = cmd, Prompt = p };
            prefs.CompanionQuickPrompts = Normalize(list);
            return true;
        }

        if (list.Count >= MaxCount || list.Any(x => string.Equals(x.Prompt, p, StringComparison.Ordinal)))
            return false;
        string id = string.IsNullOrWhiteSpace(item.Id) ? Guid.NewGuid().ToString("N") : item.Id.Trim();
        string fb = NextDefaultCommand(list);
        string newCmd = SanitizeCommand(item.Command, fb, list, excludingId: id);
        list.Add(new CompanionPromptItem { Id = id, Command = newCmd, Prompt = p });
        prefs.CompanionQuickPrompts = Normalize(list);
        return true;
    }

    public static void Remove(DevicePrefs prefs, string id)
    {
        var list = GetItems(prefs);
        list.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        prefs.CompanionQuickPrompts = Normalize(list);
    }

    public static void ResetDefaults(DevicePrefs prefs)
    {
        prefs.CompanionQuickPrompts = null;
    }
}

/// <summary>
/// Single process-wide facade over <see cref="YuyueCore"/> (Rust).
/// All rules, parsing, progress and operation-log writes live in core; the UI only draws.
/// </summary>
public sealed class LibraryService : IDisposable
{
    public YuyueCore Core { get; }
    public string DataDir { get; }
    public DevicePrefs Prefs { get; }
    public SecretStore Secrets { get; }
    public SyncService Sync { get; }
    public CompanionUsageLedger Usage { get; }
    public CompanionClient Companion { get; }
    public BuiltInEcdict Ecdict { get; }
    public KokoroVoiceModel Kokoro { get; }
    public LiveVoiceModel LiveVoice { get; }
    public AudiobookWork AudiobookWork { get; }
    public CloudVoiceService CloudVoices { get; }
    public FontShelfService Fonts { get; }
    public PodcastService Podcasts { get; }
    public WatchedFolderService WatchedFolder { get; }
    public NoteExportService NoteExports { get; }
    public UpdateAndFeedbackService Updates { get; }
    public SystemIntegrationService SystemIntegration { get; }
    public RemoteLibraryService RemoteLibraries { get; }
    public RecordingShareServer RecordingShare { get; }
    public Yuyue.Reader.PageTurnSoundService PageTurnSounds { get; }
    public SettingsHistoryStore SettingsHistory { get; }
    public AirTrafficClient Analytics { get; }

    private readonly string _prefsPath;

    public static readonly string[] SupportedExtensions = [".epub", ".txt", ".pdf", ".mobi", ".azw3", ".azw", ".prc"];

    public LibraryService(string? customDataDir = null)
    {
        DataDir = ResolveDataDir(customDataDir);
        Directory.CreateDirectory(DataDir);
        _prefsPath = Path.Combine(DataDir, "windows-prefs.json");
        bool firstLaunch = !File.Exists(_prefsPath);
        Prefs = LoadPrefs(_prefsPath);
        Secrets = new SecretStore(DataDir);

        ApplyUiLanguage();

        var deviceName = Environment.MachineName;
        if (string.IsNullOrWhiteSpace(deviceName)) deviceName = "Windows PC";

        Core = YuyueCore.Open(DataDir, deviceName, "windows");
        if (firstLaunch)
        {
            try
            {
                var rs = Core.ReaderSettings();
                if (rs.Paper == Paper.Plain)
                {
                    Core.SetReaderSettings(rs with { Paper = Paper.Kami });
                }
            }
            catch { }
            SavePrefs();
        }
        Analytics = new AirTrafficClient(Prefs, SavePrefs);
        Usage = new CompanionUsageLedger(DataDir, () => Prefs.AiDailyLlmTokenCap, () => Prefs.AiDailyTtsCharCap);
        Ecdict = new BuiltInEcdict();
        Kokoro = new KokoroVoiceModel(Core, DataDir, (langKey, vid) =>
        {
            Prefs.SetVoiceForLanguage(langKey, vid);
            SavePrefs();
            Yuyue.Speech.DramaSynthService.InvalidateAllCasts();
        });
        LiveVoice = new LiveVoiceModel(Core, DataDir);
        AudiobookWork = new AudiobookWork(Core, DataDir, () => Analytics.TrackAudioFeatureUsed("audiobook_align"));
        CloudVoices = new CloudVoiceService(this);
        Fonts = new FontShelfService(Core.FontsDir());
        Sync = new SyncService(this);
        Companion = new CompanionClient(this);
        Podcasts = new PodcastService(this);
        WatchedFolder = new WatchedFolderService(this);
        NoteExports = new NoteExportService(this);
        Updates = new UpdateAndFeedbackService(this);
        SystemIntegration = new SystemIntegrationService(this);
        RemoteLibraries = new RemoteLibraryService(this);
        RecordingShare = new RecordingShareServer(this);
        RecordingShare.Start();
        PageTurnSounds = new Yuyue.Reader.PageTurnSoundService(this);
        SettingsHistory = new SettingsHistoryStore(DataDir)
        {
            PersistentActuator = ApplyPersistedHistoryRecord,
        };

        // Register downloaded sound library if present (1:1 SoundLibrary.swift)
        try
        {
            string soundsDir = Path.Combine(DataDir, "models", "sounds-1");
            if (File.Exists(Path.Combine(soundsDir, "library.json")))
                Core.DramaSetSoundLibrary(soundsDir);
        }
        catch { }

        // Background purge of books deleted more than 30 days ago (1:1 Apple Library.swift)
        var coreRef = Core;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000).ConfigureAwait(false);
                coreRef.PurgeDeleted(30L * 24 * 3600 * 1000);
            }
            catch { }
        });
    }

    public void ApplyUiLanguage()
    {
        string uiLang = Prefs.UiLanguage switch
        {
            "en" => "en",
            "zh-Hans" => "zh-Hans",
            _ => CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh-Hans",
        };
        L10n.IsEnglish = uiLang == "en";
        YuyueCoreMethods.SetUiLanguage(uiLang);
    }

    private static DevicePrefs LoadPrefs(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<DevicePrefs>(File.ReadAllText(path)) ?? new DevicePrefs();
            }
        }
        catch { /* fall back to defaults */ }
        return new DevicePrefs();
    }

    public event Action? PrefsSaved;
    public event Action? ReaderSettingsSaved;

    public void SavePrefs()
    {
        try
        {
            File.WriteAllText(_prefsPath, JsonSerializer.Serialize(Prefs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
        PrefsSaved?.Invoke();
    }

    private static string ResolveDataDir(string? customDataDir)
    {
        if (!string.IsNullOrWhiteSpace(customDataDir)) return customDataDir;

        var args = Environment.GetCommandLineArgs();
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--library") return Path.GetFullPath(args[i + 1]);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Yuyue");
    }

    // Books & Shelf
    public IReadOnlyList<BookSummary> GetBooks() => Core.Books();

    public IReadOnlyList<BookSummary> GetShelfBooks() =>
        Core.Books().Where(b => b.Format != BookFormat.Podcast && !b.Record).ToArray();

    public IReadOnlyList<BookSummary> GetKeptBooks() =>
        Core.Books().Where(b => b.Format != BookFormat.Podcast).ToArray();

    public (IReadOnlyList<BookSummary> ShelfBooks, IReadOnlyList<BookSummary> KeptBooks) GetShelfAndKeptBooks()
    {
        var all = Core.Books();
        var kept = all.Where(b => b.Format != BookFormat.Podcast).ToArray();
        var shelf = kept.Where(b => !b.Record).ToArray();
        return (shelf, kept);
    }

    public IReadOnlyList<BookCollection> GetCollections() => Core.Collections();
    public Dictionary<string, uint> GetMarkCounts() => Core.MarkCounts();
    public BookSummary ImportBook(string filePath) => Core.ImportBook(filePath);

    public async Task<BookSummary> ImportBookWithExtrasAsync(string filePath, string? fallbackTitle = null, string? fallbackAuthor = null, byte[]? fallbackCover = null)
    {
        string stem = Path.GetFileNameWithoutExtension(filePath);
        string? cleanTitle = string.IsNullOrWhiteSpace(fallbackTitle) ? null : fallbackTitle.Trim();
        string[] cleanAuthors = string.IsNullOrWhiteSpace(fallbackAuthor)
            ? []
            : fallbackAuthor.Split(['、', ',', '，', ';', '；', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var summary = await Task.Run(() => Core.ImportBook(filePath));
        if (summary.Format == BookFormat.Pdf)
        {
            if (summary.ReadMinutes != 0) return summary;
            try
            {
                string orig = TryOriginalFile(summary.Id) ?? filePath;
                var info = await PdfInfoReader.ReadAsync(orig);
                string? title = string.IsNullOrWhiteSpace(info.Title) || info.Title == stem ? (cleanTitle ?? info.Title) : info.Title;
                string[] authors = info.Authors.Length == 0 && cleanAuthors.Length > 0 ? cleanAuthors : info.Authors;
                byte[]? cover = info.Cover ?? fallbackCover;
                var merged = new PdfInfo(info.Pages, title, authors, info.Characters, cover);
                summary = await Task.Run(() => Core.SetPdfInfo(summary.Id, merged));
            }
            catch { }
            return summary;
        }
        bool needsTitle = cleanTitle is not null && (string.IsNullOrWhiteSpace(summary.Title) || summary.Title == stem);
        bool needsAuthors = cleanAuthors.Length > 0 && summary.Authors.Length == 0;
        if (needsTitle || needsAuthors)
        {
            try
            {
                summary = await Task.Run(() => Core.EditBookInfo(
                    summary.Id,
                    needsTitle ? cleanTitle! : summary.Title,
                    needsAuthors ? cleanAuthors : summary.Authors));
            }
            catch { }
        }
        if (string.IsNullOrEmpty(summary.CoverPath) && fallbackCover is { Length: > 0 })
        {
            try
            {
                summary = await Task.Run(() => Core.SetCover(summary.Id, fallbackCover));
            }
            catch { }
        }
        return summary;
    }
    public BookDetail GetBookDetail(string bookId) => Core.Book(bookId);
    public BookSummary EditBookInfo(string bookId, string title, string[] authors) => Core.EditBookInfo(bookId, title, authors);
    public BookSummary SetCover(string bookId, byte[] imageBytes) => Core.SetCover(bookId, imageBytes);
    public string? TryOriginalFile(string bookId) { try { return Core.OriginalFile(bookId); } catch { return null; } }
    public void DeleteBook(string bookId) => Core.DeleteBook(bookId);
    public BookSummary RestoreBook(string bookId) => Core.RestoreBook(bookId);
    public BookSummary KeepRecordOnly(string bookId) => Core.KeepRecordOnly(bookId);
    public void SetFinished(string bookId, bool finished) => Core.SetFinished(bookId, finished);
    public uint PurgeDeleted(long olderThanMs) => Core.PurgeDeleted(olderThanMs);
    public void SetStatus(string bookId, ReadingStatus status) => Core.SetStatus(bookId, status);
    public BookCollection CreateCollection(string name, string? bookId = null) => Core.CreateCollection(name, null, bookId);
    public void DeleteCollection(string collectionId) => Core.DeleteCollection(collectionId);
    public void RestoreCollection(string collectionId) => Core.RestoreCollection(collectionId);
    public void SetInCollection(string collectionId, string bookId, bool inside) => Core.SetInCollection(collectionId, bookId, inside);
    public void RestoreCustomPaper(string paperId) => Core.RestoreCustomPaper(paperId);

    // Reader settings & progress
    public ReaderSettings GetReaderSettings() => Core.ReaderSettings();
    public void SaveReaderSettings(ReaderSettings settings)
    {
        Core.SetReaderSettings(settings);
        ReaderSettingsSaved?.Invoke();
    }
    public ReaderSettings? GetBookReaderSettings(string bookId) => Core.BookReaderSettings(bookId);
    public void SetBookReaderSettings(string bookId, ReaderSettings? settings) => Core.SetBookReaderSettings(bookId, settings);
    public Progress? GetProgress(string bookId) => Core.Progress(bookId);
    public void SaveProgress(string bookId, uint section, uint sentence) => Core.SaveProgress(bookId, new Position(section, sentence));
    public void SaveListening(Listening listening) => Core.SaveListening(listening);
    public void AddTime(string bookId, uint section, uint readSec, uint listenSec) =>
        Core.AddTime(bookId, section, readSec, listenSec, DateTime.Now.ToString("yyyy-MM-dd"));
    public IReadOnlyList<DayTime> GetDayTimes(string fromDay, string toDay) => Core.DayTimes(fromDay, toDay);

    // Marks, Bookmarks, Daily Review, Search, Footnotes
    public IReadOnlyList<Mark> GetMarks(string bookId) => Core.Marks(bookId);
    public Mark AddMark(string bookId, uint section, TextPoint start, TextPoint end, string text, MarkStyle style, string? note = null) =>
        Core.AddMark(bookId, section, start, end, text, style, note);
    public void SetMarkNote(string bookId, string markId, string? note) => Core.SetMarkNote(bookId, markId, note);
    public void SetMarkStyle(string bookId, string markId, MarkStyle style) => Core.SetMarkStyle(bookId, markId, style);
    public void DeleteMark(string bookId, string markId) => Core.DeleteMark(bookId, markId);
    public ReviewDeck GetReviewToday(string? bookId = null, long? dayStartMs = null, uint count = 5) =>
        Core.ReviewToday(bookId, dayStartMs ?? ReadingHeatmapBuilder.LocalDayStartUnixMs(DateTimeOffset.Now), count);
    public void SetMarkReview(string bookId, string markId, bool keep) => Core.SetMarkReview(bookId, markId, keep);
    public IReadOnlyList<Bookmark> GetBookmarks(string bookId) => Core.Bookmarks(bookId);
    public Bookmark AddBookmark(string bookId, uint section, uint sentence) => Core.AddBookmark(bookId, section, sentence);
    public void DeleteBookmark(string bookId, string bookmarkId) => Core.DeleteBookmark(bookId, bookmarkId);
    public void RestoreBookmark(string bookId, string bookmarkId) => Core.RestoreBookmark(bookId, bookmarkId);
    public IReadOnlyList<SearchHit> SearchBook(string bookId, string query) => Core.SearchBook(bookId, query);
    public Footnote? GetFootnote(string bookId, uint fromSection, string target) => Core.Footnote(bookId, fromSection, target);

    // Foreign & Dictionaries
    public IReadOnlyList<VocabWord> GetWords() => Core.Words();
    public VocabWord AddWord(string word, string? phonetic, string? meaning, string bookId, uint section, uint sentence, string context) =>
        Core.AddWord(word, phonetic, meaning, bookId, section, sentence, context);
    public void SetWordKnown(string word, bool known) => Core.SetWordKnown(word, known);
    public void DeleteWord(string word) => Core.DeleteWord(word);
    public void RestoreWord(string word) => Core.RestoreWord(word);
    public uint GetBookWordCount(string bookId, string word) => Core.BookWordCount(bookId, word);
    public IReadOnlyList<DictionaryInfo> GetDictionaries() => Core.Dictionaries();
    public DictionaryInfo ImportDictionary(string path) => Core.ImportDictionary(path);
    public void SetDictionaryEnabled(string id, bool enabled) => Core.SetDictionary(id, enabled);
    public void OrderDictionaries(string[] ids) => Core.OrderDictionaries(ids);
    public void RemoveDictionary(string id) => Core.RemoveDictionary(id);

    public WordLookupResult? LookUpWordRich(string word)
    {
        try
        {
            var mdict = Core.LookUp(word);
            if (mdict is not null)
                return new WordLookupResult(mdict.Headword, null, mdict.Summary, mdict.Dictionary, mdict.Html);
        }
        catch { }

        var builtIn = Ecdict.LookUp(word);
        if (builtIn is not null)
            return new WordLookupResult(builtIn.Headword, builtIn.Phonetic, builtIn.Meaning, "内置简明英汉词典");

        return null;
    }

    public IReadOnlyList<Gloss> GetGlosses(string bookId, uint section, byte level, bool vocabOnly)
    {
        try { return Core.Glosses(bookId, section, level, vocabOnly); }
        catch { return Array.Empty<Gloss>(); }
    }

    // Sections & Serving
    public IReadOnlyList<string> GetSentences(string bookId, uint section) => Core.Sentences(bookId, section);
    public IReadOnlyList<string> GetSpokenSentences(string bookId, uint section)
    {
        try { return Core.SpokenSentences(bookId, section); }
        catch { return Core.Sentences(bookId, section); }
    }
    public uint? NextSection(string bookId, uint section, bool speech = false) => Core.NextSection(bookId, section, speech);
    public uint? PreviousSection(string bookId, uint section, bool speech = false) => Core.PreviousSection(bookId, section, speech);
    public Served Serve(string path, PageGeometry geometry) => Core.Serve(path, geometry);
    public string FontsDir => Core.FontsDir();

    // Devices & Snapshots
    public IReadOnlyList<DeviceInfo> GetDevices() => Core.Devices();
    public SnapshotCounts GetSnapshotCounts() => Core.SnapshotCounts();
    public ulong MakeSnapshot(string path, SnapshotParts parts) => Core.MakeSnapshot(path, parts);
    public SnapshotAbout PeekSnapshot(string path) => Core.PeekSnapshot(path);
    public uint RestoreSnapshot(string path, bool replace) => Core.RestoreSnapshot(path, replace);
    public void ExportEpub(string bookId, bool includeNotes, string path) => Core.ExportEpub(bookId, includeNotes, path);

    private bool ApplyPersistedHistoryRecord(SettingsHistoryRecord rec, bool applyUndo)
    {
        try
        {
            string? raw = applyUndo ? rec.OldRawJson : rec.NewRawJson;
            if (string.IsNullOrEmpty(rec.PrefKey) || raw is null) return false;

            if (rec.PrefKey == "reader:settings")
            {
                var restored = JsonSerializer.Deserialize<ReaderSettings>(raw);
                if (restored is null) return false;
                Core.SetReaderSettings(restored);
                return true;
            }

            if (rec.PrefKey.StartsWith("prefs:", StringComparison.Ordinal))
            {
                string propName = rec.PrefKey["prefs:".Length..];
                var prop = typeof(DevicePrefs).GetProperty(propName);
                if (prop is null || !prop.CanWrite) return false;
                object? val = JsonSerializer.Deserialize(raw, prop.PropertyType);
                prop.SetValue(Prefs, val);
                if (propName == nameof(DevicePrefs.AnalyticsEnabled))
                    Analytics.SetEnabled(Prefs.AnalyticsEnabled);
                else
                    SavePrefs();
                return true;
            }
        }
        catch { }
        return false;
    }

    public void Dispose()
    {
        try { Analytics.Dispose(); } catch { }
        Kokoro.CancelDownload();
        KokoroEngine.Instance.CloseSession();
        AudiobookWork.Dispose();
        PageTurnSounds.Dispose();
        RecordingShare.Dispose();
        SystemIntegration.Dispose();
        WatchedFolder.Dispose();
        Core.Dispose();
    }
}
