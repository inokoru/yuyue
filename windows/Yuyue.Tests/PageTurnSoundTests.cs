using System.Buffers.Binary;
using Xunit;
using Yuyue.Core;
using Yuyue.Reader;

namespace Yuyue.Tests;

public sealed class PageTurnSoundTests
{
    [Fact]
    public void AllSixPresets_SynthesizeValidDistinctVariants()
    {
        Assert.Equal(6, PageTurnSoundSynthesizer.AllStyles.Count);

        foreach (var info in PageTurnSoundSynthesizer.AllStyles)
        {
            byte[][] variants = new byte[PageTurnSoundSynthesizer.VariantCount][];
            for (int v = 0; v < PageTurnSoundSynthesizer.VariantCount; v++)
            {
                byte[] wav = PageTurnSoundSynthesizer.SynthesizeWav(info.Style, v);
                variants[v] = wav;

                Assert.True(wav.Length > 44, $"{info.Key} variant {v} too short");
                Assert.Equal((byte)'R', wav[0]);
                Assert.Equal((byte)'I', wav[1]);
                Assert.Equal((byte)'F', wav[2]);
                Assert.Equal((byte)'F', wav[3]);
                Assert.Equal((byte)'W', wav[8]);
                Assert.Equal((byte)'A', wav[9]);
                Assert.Equal((byte)'V', wav[10]);
                Assert.Equal((byte)'E', wav[11]);

                uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4));
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2));
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2));
                uint dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4));
                Assert.Equal((uint)PageTurnSoundSynthesizer.SampleRate, sampleRate);
                Assert.Equal((ushort)1, channels);
                Assert.Equal((ushort)16, bits);
                Assert.Equal((uint)(wav.Length - 44), dataBytes);

                // Must be audible (non-silent)
                short maxAbs = 0;
                for (int off = 44; off + 1 < wav.Length; off += 2)
                {
                    short s = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(off, 2));
                    short a = s == short.MinValue ? short.MaxValue : Math.Abs(s);
                    if (a > maxAbs) maxAbs = a;
                }
                Assert.True(maxAbs >= 8_000, $"{info.Key} v{v} peak {maxAbs} unexpectedly low");
            }

            // 3 subtle variants should differ in length or waveform content
            Assert.False(variants[0].AsSpan().SequenceEqual(variants[1]), $"{info.Key} v0 == v1");
            Assert.False(variants[0].AsSpan().SequenceEqual(variants[2]), $"{info.Key} v0 == v2");
        }
    }

    [Fact]
    public void TryDecodePcmWav_And_TrimAndNormalize_WorkEndToEnd()
    {
        // Build a 1.5s test clip: 0.4s silence + 0.25s 600 Hz burst + 0.85s silence
        int sr = PageTurnSoundSynthesizer.SampleRate;
        var samples = new float[(int)(sr * 1.5)];
        int burstStart = (int)(sr * 0.40);
        int burstLen = (int)(sr * 0.25);
        for (int i = 0; i < burstLen; i++)
        {
            double env = Math.Sin(Math.PI * i / burstLen);
            samples[burstStart + i] = (float)(0.25 * env * Math.Sin(2.0 * Math.PI * 600.0 * i / sr));
        }

        var processed = PageTurnSoundSynthesizer.TrimAndNormalize(samples, sr);
        Assert.InRange(processed.DurationMs, 200, 350);
        Assert.Equal(PageTurnSoundSynthesizer.WaveformBarCount, processed.Waveform.Length);
        Assert.All(processed.Waveform, b => Assert.InRange(b, 0.08f, 1.0f));
        Assert.True(processed.TrimStart >= 0.0 && processed.TrimEnd <= 1.0 && processed.TrimEnd > processed.TrimStart);

        // Decoded output WAV must be valid PCM
        Assert.True(PageTurnSoundSynthesizer.TryDecodePcmWav(processed.WavData, out float[] decoded, out int decodedSr));
        Assert.Equal(sr, decodedSr);
        Assert.True(decoded.Length > 0);

        // User manual micro-trim works cleanly
        var retr = PageTurnSoundSynthesizer.RenderTrimmed(processed, processed.TrimStart, processed.TrimEnd);
        Assert.NotNull(retr);
        Assert.True(retr!.WavData.Length > 44);
    }

    [Fact]
    public void TrimAndNormalize_RejectsSilence_And_CapsLongClips()
    {
        int sr = PageTurnSoundSynthesizer.SampleRate;
        var silence = new float[sr / 2];
        var exSilent = Assert.Throws<CustomSoundException>(() => PageTurnSoundSynthesizer.TrimAndNormalize(silence, sr));
        Assert.Equal(CustomSoundErrorKind.SilentOrEmpty, exSilent.Kind);

        // 2.0 s continuous tone must be capped to <= 0.65 s (~650 ms)
        var longTone = new float[sr * 2];
        for (int i = 0; i < longTone.Length; i++)
            longTone[i] = (float)(0.4 * Math.Sin(2.0 * Math.PI * 520.0 * i / sr));
        var capped = PageTurnSoundSynthesizer.TrimAndNormalize(longTone, sr);
        Assert.InRange(capped.DurationMs, 600, 660);
    }

    [Fact]
    public async Task CustomPageTurnSoundStore_PersistsRenamesDeletesAndImportsWav()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"yuyue-pts-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Write a synthetic WAV file to disk and import it
            string srcWav = Path.Combine(dir, "sample-flip.wav");
            await File.WriteAllBytesAsync(srcWav, PageTurnSoundSynthesizer.SynthesizeWav(PageTurnSoundStyle.CrispBook, 0));

            var store = new CustomPageTurnSoundStore(dir);
            var processed = await store.ImportAudioFileAsync(srcWav);
            var saved = store.Save(processed, "我的脆页");

            Assert.Single(store.Items);
            Assert.Equal("我的脆页", saved.Name);
            Assert.StartsWith("custom:", saved.SelectionKey);
            Assert.NotNull(store.GetWavBytes(saved.Id));

            // Reload from disk in a fresh store instance
            var reloaded = new CustomPageTurnSoundStore(dir);
            Assert.Single(reloaded.Items);
            Assert.Equal(saved.Id, reloaded.Items[0].Id);
            Assert.Equal("我的脆页", reloaded.Items[0].Name);

            // Rename & delete
            Assert.True(reloaded.Rename(saved.Id, "新书翻页"));
            Assert.Equal("新书翻页", reloaded.GetItem(saved.SelectionKey)?.Name);
            Assert.True(reloaded.Delete(saved.Id));
            Assert.Empty(reloaded.Items);
            Assert.Null(reloaded.GetWavBytes(saved.Id));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DevicePrefs_PageTurnSoundDefaults_MatchMacOS()
    {
        var prefs = new DevicePrefs();
        Assert.False(prefs.PageTurnSound);
        Assert.Equal("softPaper", prefs.PageTurnSoundStyle);
        Assert.Equal(0.60, prefs.PageTurnSoundVolume, precision: 3);
    }
}
