using Xunit;
using Yuyue.Core;
using Yuyue.Shelf;
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
    public void KokoroModel_Exposes16CuratedSpeakersAndCoreFfiWorks()
    {
        var all = _library.Kokoro.GetAllSpeakers();
        Assert.Equal(16, all.Count);
        Assert.Equal(12, _library.Kokoro.GetSpeakers("zh-Hans").Count);
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
        var c1 = ShelfWindow.PaperClothColor("红楼梦", dark: false);
        var c2 = ShelfWindow.PaperClothColor("红楼梦", dark: false);
        Assert.Equal(c1, c2);

        var tLight = ShelfWindow.ResolveCollectionClothTone("col-1", "globe", dark: false, eink: false);
        var tEink = ShelfWindow.ResolveCollectionClothTone("col-1", "globe", dark: false, eink: true);
        Assert.NotEqual(tLight.WellTop, tEink.WellTop);

        var zhEmb = ShelfWindow.ResolveExLibrisEmblem("文学经典", null);
        Assert.False(zhEmb.IsSymbol);
        Assert.Equal("文", zhEmb.Value);

        var enEmb = ShelfWindow.ResolveExLibrisEmblem("Sci-Fi", null);
        Assert.False(enEmb.IsSymbol);
        Assert.Equal("S", enEmb.Value);

        var yrEmb = ShelfWindow.ResolveExLibrisEmblem("2026", null);
        Assert.False(yrEmb.IsSymbol);
        Assert.Equal("2026", yrEmb.Value);

        var symEmb = ShelfWindow.ResolveExLibrisEmblem("任意名称", "moon");
        Assert.True(symEmb.IsSymbol);
        Assert.Equal("moon", symEmb.Value);
    }
}
