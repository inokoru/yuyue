using System.Buffers.Binary;
using System.Text;
using Xunit;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Tests;

public sealed class AlignerTests : IDisposable
{
    private readonly string _tempDir;

    public AlignerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"yuyue-aligner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static string WriteSynthetic16kMonoWav(string path, short[] samples, bool includeJunkChunk)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.ASCII);

        byte[] junkPayload = includeJunkChunk ? new byte[17] : []; // odd size tests RIFF 2-byte word-alignment padding
        int junkTotal = includeJunkChunk ? 8 + junkPayload.Length + (junkPayload.Length & 1) : 0;
        int dataBytes = samples.Length * 2;
        int riffSize = 4 + (8 + 16) + junkTotal + (8 + dataBytes);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(riffSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((ushort)1); // PCM
        bw.Write((ushort)1); // Mono
        bw.Write(16_000);    // 16 kHz
        bw.Write(32_000);    // ByteRate
        bw.Write((ushort)2); // BlockAlign
        bw.Write((ushort)16);// BitsPerSample

        if (includeJunkChunk)
        {
            bw.Write(Encoding.ASCII.GetBytes("JUNK"));
            bw.Write(junkPayload.Length);
            bw.Write(junkPayload);
            if ((junkPayload.Length & 1) != 0) bw.Write((byte)0);
        }

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        byte[] pcm = new byte[dataBytes];
        for (int i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), samples[i]);
        bw.Write(pcm);

        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadWavInfo_ParsesStandardAndOddJunkChunkHeaders(bool includeJunk)
    {
        var samples = new short[16_000]; // 1.0 s
        string wav = WriteSynthetic16kMonoWav(Path.Combine(_tempDir, $"hdr-{includeJunk}.wav"), samples, includeJunk);

        Assert.True(Aligner.TryReadWavInfo(wav, out var info));
        Assert.Equal((ushort)1, info.AudioFormat);
        Assert.Equal((ushort)1, info.Channels);
        Assert.Equal(16_000, info.SampleRate);
        Assert.Equal((ushort)16, info.BitsPerSample);
        Assert.Equal(16_000L, info.SampleCount);
        Assert.Equal(32_000L, info.DataBytes);
    }

    [Fact]
    public void ComputeLevels_And_CoreAudiobookSpeech_DetectVoiceActivitySegment()
    {
        // Build 6.0 seconds @ 16 kHz: 1.5s silence -> 3.0s 440Hz tone (-6 dBFS) -> 1.5s silence
        const int sr = Aligner.SampleRate;
        var samples = new short[6 * sr];
        int speechStart = (int)(1.5 * sr);
        int speechEnd = (int)(4.5 * sr);
        for (int i = speechStart; i < speechEnd; i++)
        {
            samples[i] = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sr) * 16_384.0);
        }

        string wav = WriteSynthetic16kMonoWav(Path.Combine(_tempDir, "vad.wav"), samples, includeJunkChunk: true);
        float[] levels = Aligner.ComputeLevels(wav, CancellationToken.None);

        // 6.0s / 0.030s = 200 frames
        Assert.Equal(200, levels.Length);
        Assert.True(levels[10] < -70f, $"Silence frame should be < -70 dB, got {levels[10]}");
        Assert.True(levels[100] > -12f, $"440 Hz tone frame should be > -12 dB, got {levels[100]}");
        Assert.True(levels[185] < -70f, $"Trailing silence frame should be < -70 dB, got {levels[185]}");

        // Feed into Rust core's real VAD pipeline to verify FFI + speech-span detection end-to-end
        string libDir = Path.Combine(_tempDir, "core-lib");
        Directory.CreateDirectory(libDir);
        using var core = YuyueCore.Open(libDir, "yuyue-tests", "windows");
        var spans = core.AudiobookSpeech(levels, 0.03, 0.0);
        Assert.NotEmpty(spans);
        Assert.InRange(spans[0].T0, 1.0, 2.0);
        Assert.InRange(spans[0].T1, 4.0, 5.0);
    }
}
