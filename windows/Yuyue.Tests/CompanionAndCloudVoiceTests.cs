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
    public void CloudVoiceService_ParsesAll17ProvidersAndEndpointsAndEvictsLruCache()
    {
        Assert.Equal(17, CloudVoiceService.AllProviders.Length);
        Assert.False(CloudVoiceService.IsPaidCloud(CloudTtsProvider.Edge));
        Assert.False(CloudVoiceService.NeedsKey(CloudTtsProvider.Edge));
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

    [Fact]
    public void EdgeTtsClient_GeneratesValidSecMsGecV2TokensAndFormatsVoiceNames()
    {
        Assert.Equal(
            "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2",
            EdgeTtsClient.GenerateSecMsGecV2(1700000000.0));
        Assert.Equal(
            "6D50DDDB91812C33-DD68EFB01898B0750C9797D51A5FF93471DFDAE6888A1039D532CBDCCE54545D",
            EdgeTtsClient.GenerateSecMsGecV2(1760000000.0));
        Assert.Equal(
            "Microsoft Server Speech Text to Speech Voice (zh-CN, XiaoxiaoNeural)",
            EdgeTtsClient.FormatVoiceName("zh-CN-XiaoxiaoNeural"));
        Assert.Equal(
            "Microsoft Server Speech Text to Speech Voice (zh-CN-liaoning, XiaobeiNeural)",
            EdgeTtsClient.FormatVoiceName("zh-CN-liaoning-XiaobeiNeural"));
        Assert.Equal(22, CloudVoiceService.EdgeBuiltinVoices.Length);
    }

    [Fact]
    public void CompanionClient_QaCacheSerializesAppleCamelCaseSchemaAndReadsLegacyWindowsSchema()
    {
        var sample = new CompanionClient.CompanionAnswer(
            Text: "宝玉初见黛玉便觉得眼熟 [1]。",
            Sources: [new CompanionClient.CompanionSource(1, 2, 8, "这个妹妹我曾见过的", Later: false, Chapter: "第三回")],
            NotInRead: false,
            NextQuestions: ["为什么觉得眼熟？"],
            ProviderLabel: "OpenRouter · gpt-5-mini");

        string json = CompanionClient.SerializeUpdatedQaCacheJson(null, "q1", sample);
        using (var doc = JsonDocument.Parse(json))
        {
            var q1 = doc.RootElement.GetProperty("q1");
            Assert.Equal("宝玉初见黛玉便觉得眼熟 [1]。", q1.GetProperty("text").GetString());
            Assert.False(q1.GetProperty("notYet").GetBoolean());
            Assert.Equal("OpenRouter · gpt-5-mini", q1.GetProperty("label").GetString());
            var s1 = q1.GetProperty("sources").GetProperty("1");
            Assert.Equal(2u, s1.GetProperty("section").GetUInt32());
            Assert.Equal(8u, s1.GetProperty("sentence").GetUInt32());
            Assert.Equal("第三回", s1.GetProperty("chapter").GetString());
            Assert.Equal("这个妹妹我曾见过的", s1.GetProperty("quote").GetString());
        }

        var roundTrip = CompanionClient.ParseCachedQaJson(json, "q1");
        Assert.NotNull(roundTrip);
        Assert.Equal(sample.Text, roundTrip!.Text);
        Assert.Single(roundTrip.Sources);
        Assert.Equal("第三回", roundTrip.Sources[0].Chapter);

        // Also reads legacy Windows PascalCase array schema seamlessly
        const string legacyJson = """
        {
          "legacy_q": {
            "Text": "旧缓存回答 [2]",
            "Sources": [{ "Index": 2, "Section": 1, "Sentence": 4, "Snippet": "原文片段", "Later": true }],
            "NotInRead": false,
            "Next": ["后续问题"],
            "Label": "DeepSeek"
          }
        }
        """;
        var legacy = CompanionClient.ParseCachedQaJson(legacyJson, "legacy_q");
        Assert.NotNull(legacy);
        Assert.Equal("旧缓存回答 [2]", legacy!.Text);
        Assert.Single(legacy.Sources);
        Assert.Equal(2, legacy.Sources[0].Index);
        Assert.True(legacy.Sources[0].Later);
    }

    [Fact]
    public void CloudVoiceService_BaiduFreeVoicesMp3StrippingAndXmlSanitization()
    {
        Assert.Equal(3, CloudVoiceService.FreeBuiltinVoices.Length);
        Assert.Contains(CloudVoiceService.FreeBuiltinVoices, v => v.Id == "zh_baidu");
        Assert.Contains(CloudVoiceService.FreeBuiltinVoices, v => v.Id == "en_us");
        Assert.Contains(CloudVoiceService.FreeBuiltinVoices, v => v.Id == "en_uk");
        Assert.Equal("zh_baidu", CloudVoiceService.NormalizeFreeVoiceId("zh_baidu", "en-US"));
        Assert.Equal("zh_baidu", CloudVoiceService.NormalizeFreeVoiceId("zh_female", "en-US"));
        Assert.Equal("en_us", CloudVoiceService.NormalizeFreeVoiceId("en_us", "zh-CN"));
        Assert.Equal("en_uk", CloudVoiceService.NormalizeFreeVoiceId("en_uk", "zh-CN"));

        // EscapeXml strips illegal XML 1.0 control chars and escapes quotes/entities
        string dirty = "A\u0000B\u0007C\u001FD & <tag attr=\"1\" s='2'>";
        Assert.Equal("ABCD &amp; &lt;tag attr=&quot;1&quot; s=&apos;2&apos;&gt;", CloudVoiceService.EscapeXml(dirty));

        // CombineMp3Chunks strips leading ID3v2 (10 + 4 bytes) and trailing ID3v1 (128 bytes "TAG...")
        byte[] payload1 = [0x11, 0x22, 0x33, 0x44];
        byte[] id3v2 = [0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, 0xAA, 0xBB, 0xCC, 0xDD];
        byte[] id3v1 = new byte[128];
        id3v1[0] = (byte)'T'; id3v1[1] = (byte)'A'; id3v1[2] = (byte)'G';
        byte[] chunk1 = [.. id3v2, .. payload1, .. id3v1];
        byte[] chunk2 = [.. id3v2, 0x55, 0x66];
        byte[] combined = CloudVoiceService.CombineMp3Chunks([chunk1, chunk2]);
        Assert.Equal(new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55, 0x66 }, combined);
    }

    [Fact]
    public void CompanionClient_StripsThinkingStreamsPreviewAndParsesRichMarkdownWithCitations()
    {
        // StripThinking removes completed blocks and flags unclosed <think>
        var (visDone, thinkDone) = CompanionClient.StripThinking("<think>step 1\nstep 2</think>\n宝玉很在意黛玉 [1]。");
        Assert.Equal("宝玉很在意黛玉 [1]。", visDone);
        Assert.False(thinkDone);

        var (visOpen, thinkOpen) = CompanionClient.StripThinking("<THINK>still reasoning...");
        Assert.Equal("", visOpen);
        Assert.True(thinkOpen);

        // FormatStreamingPreview hides trailing 接着问, partial [1, and partial 没读到
        var (s1, t1) = CompanionClient.FormatStreamingPreview("<think>ok</think>这一回主要写庆寿 [1\n接着问：下一步？", hideNotYet: true);
        Assert.Equal("这一回主要写庆寿", s1);
        Assert.False(t1);

        var (s2, _) = CompanionClient.FormatStreamingPreview("没读", hideNotYet: true);
        Assert.Equal("", s2);

        // SplitFollowUpQuestions also strips <think> blocks
        var (cleanAns, followUps) = CompanionClient.SplitFollowUpQuestions("<think>plan</think>\n核心内容 [2]。\n接着问：为何如此？｜结局如何？");
        Assert.Equal("核心内容 [2]。", cleanAns);
        Assert.Equal(["为何如此？", "结局如何？"], followUps);

        // ParseRichSegments handles headings, bullets, quote bars, CJK-adjacent bold with inline citations, code, italic, strike
        const string md = """
            ### 人物关系
            - **贾母**：「今日可大好了」 [1]，语气亲昵
            > *闲笔*里藏着 `伏笔` 和 ~~误解~~ [2]
            ---
            **重点 [3]**：细节决定成败
            """;
        var segs = CompanionClient.ParseRichSegments(md);
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Heading && s.Text == "人物关系");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Bullet);
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Bold && s.Text == "贾母");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Citation && s.CitationIndex == 1);
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.QuoteBar);
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Italic && s.Text == "闲笔");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Code && s.Text == "伏笔");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Strike && s.Text == "误解");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Citation && s.CitationIndex == 2);
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Bold && s.Text == "重点");
        Assert.Contains(segs, s => s.Kind == CompanionClient.CompanionRichKind.Citation && s.CitationIndex == 3);

        // Streaming mode trims trailing dangling markdown token
        var streamSegs = CompanionClient.ParseRichSegments("正在分析**", streaming: true);
        Assert.Single(streamSegs);
        Assert.Equal("正在分析", streamSegs[0].Text);
    }
}
