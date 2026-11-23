using System.Text;
using System.Text.Json;
using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class CompanionAndCloudVoiceTests : IDisposable
{
    private readonly string _tempDir;

    public CompanionAndCloudVoiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-waveb-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void CompanionUsageLedger_Persists30DaysAndEnforcesLlmAndTtsCaps()
    {
        long llmCap = 10_000;
        long ttsCap = 1_000;
        var ledger = new CompanionUsageLedger(_tempDir, () => llmCap, () => ttsCap);

        ledger.CheckLlmBudget(paid: true);
        ledger.RecordLlm(input: 3_000, output: 1_000, cached: 4_000, paid: true, source: UsageSource.Drama);
        ledger.RecordLlm(input: 1_500, output: 500, cached: 0, paid: true, source: UsageSource.Companion);

        // Total today = 10_000 >= llmCap -> paid call throws, free local call passes
        Assert.Throws<BudgetExceededException>(() => ledger.CheckLlmBudget(paid: true));
        ledger.CheckLlmBudget(paid: false);

        // Reload fresh instance from disk and verify snapshot & cache hit rate
        var reloaded = new CompanionUsageLedger(_tempDir, () => 0, () => ttsCap);
        var snap = reloaded.Snapshot30Days();
        Assert.Equal(10_000, snap.TotalTokens);
        Assert.Equal(4_000, snap.CachedInputTokens);
        Assert.Equal(4_500, snap.MissInputTokens);
        Assert.Equal(1_500, snap.OutputTokens);
        Assert.NotNull(snap.CacheHitRate);
        Assert.InRange(snap.CacheHitRate!.Value, 0.47, 0.48);
        Assert.Equal(UsageSource.Drama, snap.Sources[0].Source);

        // TTS cache hits do not consume budget; live synthesis does
        reloaded.RecordTts(chars: 5_000, cached: true);
        reloaded.CheckTtsBudget(800);
        reloaded.RecordTts(chars: 800, cached: false);
        Assert.Throws<BudgetExceededException>(() => reloaded.CheckTtsBudget(300));

        Assert.Equal("50 万", CompanionUsageLedger.FormatCount(500_000));
        Assert.Equal("1.5K", CompanionUsageLedger.FormatCount(1_500));
    }

    [Fact]
    public void CloudVoiceService_ParsesAll16ProvidersAndEndpointsAndEvictsLruCache()
    {
        Assert.Equal(16, CloudVoiceService.AllProviders.Length);
        Assert.False(CloudVoiceService.IsPaidCloud(CloudTtsProvider.Free));
        Assert.False(CloudVoiceService.NeedsKey(CloudTtsProvider.Free));
        foreach (var p in CloudVoiceService.AllProviders)
        {
            string fullId = $"cloud.{CloudVoiceEntry.ProviderKey(p)}.sample_voice";
            Assert.True(CloudVoiceService.TryParseFullId(fullId, out var parsed, out var vid));
            Assert.Equal(p, parsed);
            Assert.Equal("sample_voice", vid);
        }

        Assert.Equal("http://127.0.0.1:3900/v1/audio/speech", CloudVoiceService.SpeechEndpoint("http://127.0.0.1:3900"));
        Assert.Equal("http://127.0.0.1:8000/v1/audio/speech", CloudVoiceService.SpeechEndpoint("http://127.0.0.1:8000/v1/"));
        Assert.Equal("http://127.0.0.1:8000/v1", CloudVoiceService.NormalizedBaseV1("http://127.0.0.1:8000/v1/audio/speech"));

        var sf1 = CloudVoiceService.SplitSiliconFlowVoice("FunAudioLLM/CosyVoice2-0.5B|speech:myclone");
        Assert.Equal("FunAudioLLM/CosyVoice2-0.5B", sf1.Model);
        Assert.Equal("speech:myclone", sf1.Voice);

        Assert.Equal("sad", CloudVoiceService.MiniMaxEmotion(new CloudVoiceActing("悲伤低沉")));
        Assert.Equal("wav", CloudVoiceService.SniffExt([(byte)'R', (byte)'I', (byte)'F', (byte)'F'], "mp3"));
        Assert.Equal("mp3", CloudVoiceService.SniffExt([(byte)'I', (byte)'D', (byte)'3', 0x03], "wav"));

        // LocalVoiceProbe JSON parser
        using var doc = JsonDocument.Parse("""{"voices":[{"id":"af_heart","name":"Heart"},{"voice_id":"zm_yunjian","name":"云健","gender":"male"}]}""");
        var voices = LocalVoiceProbe.ParseVoices(doc.RootElement);
        Assert.Equal(2, voices.Count);
        Assert.Equal("female", voices[0].Gender);
        Assert.Equal("male", voices[1].Gender);

        // 300 MB LRU cache trimmer (tested with a tiny 1 KB cap)
        string cacheDir = Path.Combine(_tempDir, "tts-cache");
        Directory.CreateDirectory(cacheDir);
        for (int i = 0; i < 4; i++)
        {
            string f = Path.Combine(cacheDir, $"f{i}.wav");
            File.WriteAllBytes(f, new byte[400]);
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-10 + i));
        }
        int removed = CloudVoiceService.TrimCacheDirectoryToCap(cacheDir, 1000);
        Assert.Equal(2, removed);
        Assert.False(File.Exists(Path.Combine(cacheDir, "f0.wav")));
        Assert.False(File.Exists(Path.Combine(cacheDir, "f1.wav")));
        Assert.True(File.Exists(Path.Combine(cacheDir, "f2.wav")));
        Assert.True(File.Exists(Path.Combine(cacheDir, "f3.wav")));
    }

    [Fact]
    public void CompanionClient_Utf8SplitFollowUpCitationsAndUsageParsing()
    {
        // Multi-byte UTF-8 boundary snapping must never split mid-CJK character
        string cjk = "人物表：林黛玉、贾宝玉\n正文开始";
        int midByte = Encoding.UTF8.GetByteCount("人物表") + 1; // intentionally mid-char inside '：'
        var split = CompanionClient.SplitUtf8Prefix(cjk, midByte);
        Assert.NotNull(split);
        Assert.Equal(cjk, split!.Value.Prefix + split.Value.Suffix);
        Assert.DoesNotContain('\uFFFD', split.Value.Prefix);
        Assert.DoesNotContain('\uFFFD', split.Value.Suffix);

        // Follow-up & citation extraction
        var (ans, next) = CompanionClient.SplitFollowUpQuestions("他其实早就知道了 [1][3]。\n接着问：他为什么隐瞒？｜后来怎么样了？");
        Assert.Equal("他其实早就知道了 [1][3]。", ans);
        Assert.Equal(2, next.Count);
        Assert.Equal("他为什么隐瞒？", next[0]);
        Assert.Equal("后来怎么样了？", next[1]);

        var cites = CompanionClient.ExtractCitations(ans);
        Assert.Contains(1, cites);
        Assert.Contains(3, cites);
        Assert.DoesNotContain(2, cites);

        // OpenAI / DeepSeek / OpenRouter usage parser
        using var uDoc = JsonDocument.Parse("""{"prompt_tokens":1000,"completion_tokens":200,"prompt_tokens_details":{"cached_tokens":600}}""");
        var u = CompanionClient.ParseOpenAiUsage(uDoc.RootElement);
        Assert.Equal(400, u.Input);
        Assert.Equal(200, u.Output);
        Assert.Equal(600, u.Cached);
    }
}
