using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class SpeechAndDramaTests : IDisposable
{
    private readonly string _tempDir;

    public SpeechAndDramaTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-speech-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public void VoiceTapDsp_HighPassAttenuatesSubBassAndSoftCeilingCapsPeaks()
    {
        var dsp = new VoiceTapDsp(gain: 1.0f, clearer: true);
        dsp.Prepare(48_000, 1);

        // 30 Hz sine wave should be noticeably attenuated by 80 Hz 2nd-order HPF
        float[] lowFreq = new float[48_000];
        for (int i = 0; i < lowFreq.Length; i++)
            lowFreq[i] = 0.04f * MathF.Sin(2f * MathF.PI * 30f * i / 48_000f);

        float rmsBefore = Rms(lowFreq.AsSpan(12_000));
        dsp.ProcessInterleaved(lowFreq);
        float rmsAfter = Rms(lowFreq.AsSpan(12_000));
        Assert.True(rmsAfter < rmsBefore * 0.4f, $"Expected 30 Hz HPF attenuation, before={rmsBefore}, after={rmsAfter}");

        // Huge gain spikes must be soft-limited to <= 1.0 via -1 dBFS tanh soft ceiling
        var hot = new VoiceTapDsp(gain: 10.0f, clearer: false);
        hot.Prepare(48_000, 1);
        float[] loud = [0.1f, -0.1f, 0.5f, -0.9f];
        hot.ProcessInterleaved(loud);
        Assert.True(MathF.Abs(loud[0]) < 0.99f && MathF.Abs(loud[0]) > 0.89f, $"Expected smooth soft clip for 1.0 input, got {loud[0]}");
        foreach (var s in loud)
            Assert.True(MathF.Abs(s) <= 1.0f, $"Sample exceeded soft ceiling: {s}");
    }

    [Fact]
    public void VoiceTapDsp_MeasureLoudnessAndProcessWavRoundTrip()
    {
        string srcWav = Path.Combine(_tempDir, "quiet.wav");
        string dstWav = Path.Combine(_tempDir, "processed.wav");

        // Create 1 second of 1 kHz tone at -34 dBFS (~0.02 amplitude)
        const int sr = 24_000;
        byte[] pcm = new byte[sr * 2];
        for (int i = 0; i < sr; i++)
        {
            short v = (short)Math.Round(0.02 * 32767.0 * Math.Sin(2.0 * Math.PI * 1000.0 * i / sr));
            pcm[i * 2] = (byte)(v & 0xFF);
            pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
        }
        File.WriteAllBytes(srcWav, CloudVoiceService.WrapPcm16MonoInWav(pcm, sr));

        float gain = VoiceTapDsp.MeasureLoudnessFromWav(srcWav);
        // -20 dBFS target vs ~-37 dBFS RMS -> boosted, clamped <= +12 dB (~3.98x)
        Assert.InRange(gain, 1.5f, 4.05f);

        bool ok = VoiceTapDsp.ProcessWavFile(srcWav, dstWav, gain, clearer: true);
        Assert.True(ok);
        Assert.True(File.Exists(dstWav));
        Assert.True(Aligner.TryReadWavInfo(dstWav, out var outInfo));
        Assert.Equal(sr, outInfo.SampleRate);
        Assert.Equal(1, outInfo.Channels);
        Assert.Equal(sr, outInfo.SampleCount);
    }

    [Fact]
    public void DramaSynthService_NormalizeVoiceIdStripsLegacyCardPrefix()
    {
        Assert.Equal("card.b.01", DramaSynthService.NormalizeVoiceId("card:card.b.01"));
        Assert.Equal("cloned.abc123", DramaSynthService.NormalizeVoiceId("card:cloned.abc123"));
        Assert.Equal("card.b.02", DramaSynthService.NormalizeVoiceId("card.b.02"));
        Assert.Equal("cloud.openai.nova", DramaSynthService.NormalizeVoiceId("cloud.openai.nova"));
        Assert.Equal("cloud.edge.zh-CN-XiaoxiaoNeural", DramaSynthService.NormalizeVoiceId("edge.zh-CN-XiaoxiaoNeural"));
        Assert.Equal("cloud.edge.en-US-JennyNeural", DramaSynthService.NormalizeVoiceId("cloud.edge.en-US-JennyNeural"));
        Assert.Equal("sys.Microsoft Huihui", DramaSynthService.NormalizeVoiceId("sys.Microsoft Huihui"));
        Assert.Equal("auto", DramaSynthService.NormalizeVoiceId("auto"));
    }

    [Fact]
    public void AudioPlayer_ResampleMono16AndTranscriberTokenization()
    {
        short[] src = new short[240];
        for (int i = 0; i < src.Length; i++) src[i] = (short)(i * 100);
        short[] up = AudioPlayer.ResampleMono16(src, 24_000, 48_000);
        Assert.Equal(480, up.Length);
        Assert.Equal(src[0], up[0]);

        var words = new List<HeardWord>();
        WindowsTranscriber.DistributeTextIntoHeardWords("你好 world 呀", 10.0, 14.0, words);
        Assert.Equal(4, words.Count);
        Assert.Equal("你", words[0].Text);
        Assert.Equal("好", words[1].Text);
        Assert.Equal("world", words[2].Text);
        Assert.Equal("呀", words[3].Text);
        Assert.True(words[0].T0 >= 10.0 && words[^1].T1 <= 14.05);
    }

    [Fact]
    public void RustCore_DramaCastVoicesRoundTripProducesCastingPlan()
    {
        string libDir = Path.Combine(_tempDir, "drama-lib");
        Directory.CreateDirectory(libDir);
        string epub = TestPaths.Fixture("books", "beiying.epub");
        Assert.True(File.Exists(epub));

        using var core = YuyueCore.Open(libDir, "yuyue-tests", "windows");
        var book = core.ImportBook(epub);
        var voices = new[]
        {
            new VoiceInfo("card.b.01", ["zh"], CharacterGender.F, CharacterAge.Young, [], [], true, true, -20f),
            new VoiceInfo("card.b.02", ["zh"], CharacterGender.M, CharacterAge.Middle, [], [], true, true, -20f),
            new VoiceInfo("card.b.03", ["zh"], CharacterGender.M, CharacterAge.Old, [], [], true, true, -20f),
        };

        var plan = core.DramaCastVoices(book.Id, voices, "card.b.02");
        Assert.NotNull(plan);
        Assert.Equal("card.b.02", plan.Narrator);

        // Explicit narrator outside the local voice pool (Kokoro, cloud, cloned, system) must be preserved verbatim.
        foreach (string externalNarrator in new[]
        {
            "kokoro.zf_001",
            "cloud.edge.zh-CN-XiaoxiaoNeural",
            "cloned.custom01",
            "sys:Microsoft-Xiaoxiao",
        })
        {
            var extPlan = core.DramaCastVoices(book.Id, voices, externalNarrator);
            Assert.NotNull(extPlan);
            Assert.Equal(externalNarrator, extPlan.Narrator);
        }
    }

    private static float Rms(ReadOnlySpan<float> span)
    {
        double sum = 0;
        for (int i = 0; i < span.Length; i++) sum += span[i] * span[i];
        return (float)Math.Sqrt(sum / Math.Max(1, span.Length));
    }
}
