using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Yuyue.Audiobook;
using Yuyue.Design;
using Yuyue.Pdf;
using Yuyue.Podcast;
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
    public double CoverWidth { get; set; } = 100.0;
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
    public byte GlossLevel { get; set; } = 3; // 0=关闭, 2=四级, 3=六级, 4=考研, 5=雅思, 6=GRE
    public bool GlossVocabOnly { get; set; } = false;

    // 沉浸阅读偏好（对齐 MacSettings ReadingSettings）
    public bool ImmersiveAutoEnter { get; set; } = false;
    public bool ImmersiveKeepLine { get; set; } = true;
    public bool ImmersiveHideMarks { get; set; } = false;
    public bool ImmersiveHideHints { get; set; } = true;
    public bool ImmersiveNoAnimation { get; set; } = true;

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
    public double SpeechRate { get; set; } = 1.0;
    public string CloudVoiceAzureRegion { get; set; } = "eastasia";
    public bool DramaPreRenderPhone { get; set; } = false;
    public Dictionary<string, List<string[]>> CachedCloudVoices { get; set; } = new();
    public Dictionary<string, string> ClonedElevenLabsVoiceIds { get; set; } = new();

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
    public string FeedbackDraftText { get; set; } = "";
    public string FeedbackEmail { get; set; } = "";
    public List<string> FeedbackOfflineQueue { get; set; } = [];
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
    public bool SyncFiles { get; set; } = false;
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
    public Dictionary<string, string> AiProviderUrls { get; set; } = new();
    public Dictionary<string, string> AiCustomProviderNames { get; set; } = new();

    // 笔记页与背景白噪音偏好（对齐 MacNotesView & SoundLibrary）
    public bool NotesCoversOnly { get; set; } = false;
    public string AmbientSoundId { get; set; } = ""; // "" | rain | fire | stream | night | cafe | wind
    public double AmbientSoundVolume { get; set; } = 0.25;

    public ElementTheme ToElementTheme() => ThemeMode switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    public YYTint Tint => YYTints.All.FirstOrDefault(t => t.Id == TintId) ?? YYTints.Default;
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
    public CompanionClient Companion { get; }
    public BuiltInEcdict Ecdict { get; }
    public LiveVoiceModel LiveVoice { get; }
    public CloudVoiceService CloudVoices { get; }
    public FontShelfService Fonts { get; }
    public PodcastService Podcasts { get; }
    public WatchedFolderService WatchedFolder { get; }
    public NoteExportService NoteExports { get; }
    public UpdateAndFeedbackService Updates { get; }
    public SystemIntegrationService SystemIntegration { get; }
    public RemoteLibraryService RemoteLibraries { get; }
    public RecordingShareServer RecordingShare { get; }

    private readonly string _prefsPath;

    public static readonly string[] SupportedExtensions = [".epub", ".txt", ".pdf", ".mobi", ".azw3"];

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
        Ecdict = new BuiltInEcdict();
        LiveVoice = new LiveVoiceModel(Core, DataDir);
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

        // Register downloaded sound library if present (1:1 SoundLibrary.swift)
        try
        {
            string soundsDir = Path.Combine(DataDir, "models", "sounds-1");
            if (File.Exists(Path.Combine(soundsDir, "library.json")))
                Core.DramaSetSoundLibrary(soundsDir);
        }
        catch { }
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

    public void SavePrefs()
    {
        try
        {
            File.WriteAllText(_prefsPath, JsonSerializer.Serialize(Prefs, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* ignore */ }
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

    public IReadOnlyList<BookCollection> GetCollections() => Core.Collections();
    public Dictionary<string, uint> GetMarkCounts() => Core.MarkCounts();
    public BookSummary ImportBook(string filePath) => Core.ImportBook(filePath);

    public async Task<BookSummary> ImportBookWithExtrasAsync(string filePath)
    {
        var summary = await Task.Run(() => Core.ImportBook(filePath));
        if (summary.Format == BookFormat.Pdf)
        {
            try
            {
                string orig = TryOriginalFile(summary.Id) ?? filePath;
                var info = await PdfInfoReader.ReadAsync(orig);
                summary = await Task.Run(() => Core.SetPdfInfo(summary.Id, info));
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
    public void SetStatus(string bookId, ReadingStatus status) => Core.SetStatus(bookId, status);
    public BookCollection CreateCollection(string name, string? bookId = null) => Core.CreateCollection(name, null, bookId);
    public void DeleteCollection(string collectionId) => Core.DeleteCollection(collectionId);
    public void SetInCollection(string collectionId, string bookId, bool inside) => Core.SetInCollection(collectionId, bookId, inside);

    // Reader settings & progress
    public ReaderSettings GetReaderSettings() => Core.ReaderSettings();
    public void SaveReaderSettings(ReaderSettings settings) => Core.SetReaderSettings(settings);
    public ReaderSettings? GetBookReaderSettings(string bookId) => Core.BookReaderSettings(bookId);
    public void SetBookReaderSettings(string bookId, ReaderSettings? settings) => Core.SetBookReaderSettings(bookId, settings);
    public Progress? GetProgress(string bookId) => Core.Progress(bookId);
    public void SaveProgress(string bookId, uint section, uint sentence) => Core.SaveProgress(bookId, new Position(section, sentence));
    public void AddTime(string bookId, uint section, uint readSec, uint listenSec) =>
        Core.AddTime(bookId, section, readSec, listenSec, DateTime.Now.ToString("yyyy-MM-dd"));

    // Marks, Bookmarks, Search, Footnotes
    public IReadOnlyList<Mark> GetMarks(string bookId) => Core.Marks(bookId);
    public Mark AddMark(string bookId, uint section, TextPoint start, TextPoint end, string text, MarkStyle style, string? note = null) =>
        Core.AddMark(bookId, section, start, end, text, style, note);
    public void SetMarkNote(string bookId, string markId, string? note) => Core.SetMarkNote(bookId, markId, note);
    public void SetMarkStyle(string bookId, string markId, MarkStyle style) => Core.SetMarkStyle(bookId, markId, style);
    public void DeleteMark(string bookId, string markId) => Core.DeleteMark(bookId, markId);
    public IReadOnlyList<Bookmark> GetBookmarks(string bookId) => Core.Bookmarks(bookId);
    public Bookmark AddBookmark(string bookId, uint section, uint sentence) => Core.AddBookmark(bookId, section, sentence);
    public void DeleteBookmark(string bookId, string bookmarkId) => Core.DeleteBookmark(bookId, bookmarkId);
    public IReadOnlyList<SearchHit> SearchBook(string bookId, string query) => Core.SearchBook(bookId, query);
    public Footnote? GetFootnote(string bookId, uint fromSection, string target) => Core.Footnote(bookId, fromSection, target);

    // Foreign & Dictionaries
    public IReadOnlyList<VocabWord> GetWords() => Core.Words();
    public VocabWord AddWord(string word, string? phonetic, string? meaning, string bookId, uint section, uint sentence, string context) =>
        Core.AddWord(word, phonetic, meaning, bookId, section, sentence, context);
    public void SetWordKnown(string word, bool known) => Core.SetWordKnown(word, known);
    public void DeleteWord(string word) => Core.DeleteWord(word);
    public IReadOnlyList<DictionaryInfo> GetDictionaries() => Core.Dictionaries();
    public DictionaryInfo ImportDictionary(string path) => Core.ImportDictionary(path);
    public void SetDictionaryEnabled(string id, bool enabled) => Core.SetDictionary(id, enabled);
    public void OrderDictionaries(string[] ids) => Core.OrderDictionaries(ids);
    public void RemoveDictionary(string id) => Core.RemoveDictionary(id);
    public DictEntry? LookUpWord(string word) => Core.LookUp(word);

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

    public void Dispose()
    {
        RecordingShare.Dispose();
        SystemIntegration.Dispose();
        WatchedFolder.Dispose();
        Core.Dispose();
    }
}
