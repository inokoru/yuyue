using Xunit;
using Yuyue.Core;
using Yuyue.Design;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class UpstreamParityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly LibraryService _library;

    public UpstreamParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-upstream-parity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _library = new LibraryService(_tempDir);
    }

    public void Dispose()
    {
        try { _library.Dispose(); } catch { }
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void KokoroModel_Exposes32CuratedSpeakersAndCoreFfiWorks()
    {
        var all = _library.Kokoro.GetAllSpeakers();
        Assert.Equal(32, all.Count);
        Assert.Equal(28, _library.Kokoro.GetSpeakers("zh-Hans").Count);
        Assert.Equal(4, _library.Kokoro.GetSpeakers("en-US").Count);

        Assert.NotNull(_library.Kokoro.FindSpeaker("kokoro.zf_001"));
        Assert.NotNull(_library.Kokoro.FindSpeaker("kokoro.af_maple"));
        Assert.Null(_library.Kokoro.FindSpeaker("card.builtin.narrator"));

        // Verify all 5 direct fallback files have 64-char BLAKE3 digests
        Assert.Equal(5, KokoroVoiceModel.RawFiles.Length);
        foreach (var rf in KokoroVoiceModel.RawFiles)
        {
            Assert.True(rf.Bytes > 0);
            Assert.Equal(64, rf.Blake3.Length);
        }

        // Verify live Rust FFI KokoroClauses & KokoroPostprocess
        string[] clauses = _library.Core.KokoroClauses("春天的风轻轻吹过窗台，翻开书页的声音格外安静。", "zh");
        Assert.NotEmpty(clauses);
        float[] processed = _library.Core.KokoroPostprocess(new float[2400], 24_000);
        Assert.NotNull(processed);
    }

    [Fact]
    public void DevicePrefs_EinkGrayAndResizableWidthsPersist()
    {
        Assert.True(_library.Prefs.GlobalEinkGray);
        Assert.Equal(304.0, _library.Prefs.SettingsInspectorWidth);
        Assert.Equal(320.0, _library.Prefs.ReaderPanelWidth);

        _library.Prefs.GlobalEinkMode = true;
        _library.Prefs.GlobalEinkGray = false;
        _library.Prefs.SettingsInspectorWidth = 360.0;
        _library.Prefs.ReaderPanelWidth = 400.0;
        _library.SavePrefs();

        using var reloaded = new LibraryService(_tempDir);
        Assert.True(reloaded.Prefs.GlobalEinkMode);
        Assert.False(reloaded.Prefs.GlobalEinkGray);
        Assert.Equal(360.0, reloaded.Prefs.SettingsInspectorWidth);
        Assert.Equal(400.0, reloaded.Prefs.ReaderPanelWidth);
    }

    [Fact]
    public void CompanionClient_PersistsCachedModelsStarsRecentsAndInfersMakers()
    {
        _library.Companion.SaveCachedModels("openrouter", [
            "anthropic/claude-sonnet-4.5",
            "openai/gpt-5-mini",
            "google/gemini-2.5-flash",
            "deepseek/deepseek-chat",
            "anthropic/claude-sonnet-4.5", // duplicate should be deduped
        ]);

        var cached = _library.Companion.GetCachedModels("openrouter");
        Assert.Equal(4, cached.Count);

        Assert.False(_library.Companion.IsModelStarred("openrouter", "openai/gpt-5-mini"));
        Assert.True(_library.Companion.ToggleStarModel("openrouter", "openai/gpt-5-mini"));
        Assert.True(_library.Companion.IsModelStarred("openrouter", "openai/gpt-5-mini"));

        _library.Companion.RecordModelUsed("openrouter", "google/gemini-2.5-flash");
        Assert.Contains("google/gemini-2.5-flash", _library.Prefs.AiRecentModels["openrouter"]);
        Assert.Contains("openrouter:google/gemini-2.5-flash", _library.Prefs.GlobalRecentAiModels);

        Assert.Equal("Anthropic", CompanionClient.InferModelMaker("anthropic/claude-sonnet-4.5"));
        Assert.Equal("OpenAI", CompanionClient.InferModelMaker("gpt-5-mini"));
        Assert.Equal("Google", CompanionClient.InferModelMaker("gemini-2.5-flash"));
        Assert.Equal("DeepSeek", CompanionClient.InferModelMaker("deepseek-chat"));
        Assert.Equal("Qwen", CompanionClient.InferModelMaker("qwen-plus"));
        Assert.Equal("本机", CompanionClient.InferRouteBadge("ollama", "qwen2.5:7b"));
        Assert.Equal("聚合路由", CompanionClient.InferRouteBadge("openrouter", "anthropic/claude-sonnet-4.5"));
        Assert.Equal("轻量", CompanionClient.InferRouteBadge("openai", "gpt-5-mini"));
    }

    [Fact]
    public void BookcaseWallAndPaperBookClothTones_AreDeterministicAndHandleEink()
    {
        var c1 = BookVisuals.PaperClothColor("红楼梦", dark: false);
        var c2 = BookVisuals.PaperClothColor("红楼梦", dark: false);
        Assert.Equal(c1, c2);

        var tLight = BookVisuals.ResolveCollectionClothTone("col-1", "globe", dark: false, eink: false);
        var tEink = BookVisuals.ResolveCollectionClothTone("col-1", "globe", dark: false, eink: true);
        Assert.NotEqual(tLight.WellTop, tEink.WellTop);

        var zhEmb = BookVisuals.ResolveExLibrisEmblem("文学经典", null);
        Assert.False(zhEmb.IsSymbol);
        Assert.Equal("文", zhEmb.Value);

        var enEmb = BookVisuals.ResolveExLibrisEmblem("Sci-Fi", null);
        Assert.False(enEmb.IsSymbol);
        Assert.Equal("S", enEmb.Value);

        var yrEmb = BookVisuals.ResolveExLibrisEmblem("2026", null);
        Assert.False(yrEmb.IsSymbol);
        Assert.Equal("2026", yrEmb.Value);

        var symEmb = BookVisuals.ResolveExLibrisEmblem("任意名称", "moon");
        Assert.True(symEmb.IsSymbol);
        Assert.Equal("moon", symEmb.Value);
    }

    [Fact]
    public void DeepLinksUpdaterManifestAndCrashLogRotation_WorkCleanly()
    {
        // yuyue:// protocol deep links
        var openLink = App.ParseDeepLinkUri("yuyue://open/book-123");
        Assert.NotNull(openLink);
        Assert.Equal("open", openLink!.Value.Action);
        Assert.Equal("book-123", openLink.Value.Target);

        var settingsLink = App.ParseDeepLinkUri("yuyue://settings/listening");
        Assert.Equal(("settings", "listening"), settingsLink);

        var bareSettings = App.ParseDeepLinkUri("yuyue://settings");
        Assert.Equal(("settings", null), bareSettings);

        Assert.Null(App.ParseDeepLinkUri("https://example.com/open/123"));

        // Arch-aware Windows updater manifest JSON
        const string json = """
        {
          "windows": {
            "x64": { "url": "https://github.com/inokoru/yuyue/releases/download/v0.2.0/Yuyue-0.2.0-windows-x64-Setup.exe", "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" },
            "arm64": { "url": "https://github.com/inokoru/yuyue/releases/download/v0.2.0/Yuyue-0.2.0-windows-arm64-Setup.exe", "sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
          }
        }
        """;
        var x64 = UpdateAndFeedbackService.ParseWinInstallerJson(json, "x64");
        Assert.EndsWith("windows-x64-Setup.exe", x64.Url);
        Assert.Equal(new string('a', 64), x64.Sha256);

        var arm = UpdateAndFeedbackService.ParseWinInstallerJson(json, "arm64");
        Assert.EndsWith("windows-arm64-Setup.exe", arm.Url);
        Assert.Equal(new string('b', 64), arm.Sha256);

        // SHA256SUMS parser
        string shaSample = new string('c', 64);
        string sums = $"{shaSample}  Yuyue-0.2.0-windows-x64-Setup.exe\n{new string('d', 64)}  Yuyue-0.2.0-windows-x64.zip\n";
        Assert.Equal(shaSample, UpdateAndFeedbackService.ParseSha256Sums(sums, "Yuyue-0.2.0-windows-x64-Setup.exe"));
        Assert.Null(UpdateAndFeedbackService.ParseSha256Sums(sums, "missing.exe"));

        // Crash log rotation at 2 MB
        string logPath = Path.Combine(_tempDir, "yuyue.log");
        File.WriteAllText(logPath, new string('x', 2 * 1024 * 1024 + 128));
        App.AppendCrashLog(_tempDir, "TEST", "rotation check");
        Assert.True(File.Exists(logPath + ".1"));
        Assert.Contains("rotation check", File.ReadAllText(logPath));
    }

    [Fact]
    public void PaperAppearance_AutoMatchesLightAndDarkPapers()
    {
        var rs = _library.GetReaderSettings() with { Paper = Paper.Kami, CustomPaper = null };
        _library.Prefs.PaperFollowAppearance = true;

        var day = PaperAppearance.Resolve(_library, rs, appDarkOverride: false);
        Assert.Equal(Paper.Kami, day.Preset);
        Assert.False(day.IsDark);

        var night = PaperAppearance.Resolve(_library, rs, appDarkOverride: true);
        Assert.Equal(Paper.KamiNight, night.Preset);
        Assert.True(night.IsDark);

        // Turning off PaperFollowAppearance keeps the user's chosen paper even in dark mode
        _library.Prefs.PaperFollowAppearance = false;
        var fixedDay = PaperAppearance.Resolve(_library, rs, appDarkOverride: true);
        Assert.Equal(Paper.Kami, fixedDay.Preset);
    }
}
