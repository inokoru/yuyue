using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Playback;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.Storage.Streams;
using Yuyue.Core;

namespace Yuyue.Reader;

/// <summary>
/// Built-in selectable page-turn sound presets, 1:1 mirrored with macOS (<c>PageTurn.swift</c>)
/// and Android (<c>PageTurnSound</c>).
/// </summary>
public enum PageTurnSoundStyle
{
    SoftPaper,
    CrispBook,
    RicePaper,
    Hardcover,
    WoodClick,
    InkDrop,
}

public sealed record PageTurnSoundStyleInfo(
    PageTurnSoundStyle Style,
    string Key,
    string Title,
    string Subtitle,
    string Icon);

/// <summary>
/// User-added custom page-turn sound stored locally as a trimmed, normalized 44.1 kHz mono WAV.
/// </summary>
public sealed class CustomPageTurnSound
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int DurationMs { get; set; }
    public long CreatedAtUnixMs { get; set; }

    public string SelectionKey => CustomPageTurnSoundStore.CustomPrefix + Id;
}

/// <summary>
/// Result of decoding, trimming leading/trailing silence, capping duration, and normalizing audio,
/// along with a focused source window &amp; waveform envelope for interactive preview/trimming.
/// </summary>
public sealed record ProcessedCustomSound(
    byte[] WavData,
    int DurationMs,
    float[] SourceSamples,
    int SampleRate,
    double TrimStart,
    double TrimEnd,
    double AutoTrimStart,
    double AutoTrimEnd,
    float[] Waveform);

public enum CustomSoundErrorKind
{
    Unreadable,
    SilentOrEmpty,
    TooShort,
    SaveFailed,
}

public sealed class CustomSoundException : Exception
{
    public CustomSoundErrorKind Kind { get; }

    public CustomSoundException(CustomSoundErrorKind kind, string? detail = null)
        : base(detail ?? MessageFor(kind))
    {
        Kind = kind;
    }

    public string UserMessage => MessageFor(Kind);

    public static string MessageFor(CustomSoundErrorKind kind) => kind switch
    {
        CustomSoundErrorKind.Unreadable => L10n.T("这段音频读不出来，换一个试试"),
        CustomSoundErrorKind.SilentOrEmpty => L10n.T("没有检测到明显声音，再试一次"),
        CustomSoundErrorKind.TooShort => L10n.T("声音太短了，换一段试试"),
        CustomSoundErrorKind.SaveFailed => L10n.T("保存自定义音效失败，请重试"),
        _ => L10n.T("处理音效失败，请重试"),
    };
}

/// <summary>
/// Pure DSP synthesizer &amp; custom-clip processor for page-turn sounds.
/// Exact mathematical port of macOS <c>PageTurnSound</c> and <c>CustomPageTurnSounds</c>
/// (<c>apple/Yuyue/Reader/PageTurn.swift</c>): 44.1 kHz mono 16-bit PCM WAV with 3 subtle variants per style.
/// </summary>
public static class PageTurnSoundSynthesizer
{
    public const int SampleRate = 44_100;
    public const int VariantCount = 3;
    public const double MaxDurationSeconds = 0.65;
    public const double MinDurationSeconds = 0.025;
    public const int WaveformBarCount = 40;

    public static readonly IReadOnlyList<PageTurnSoundStyleInfo> AllStyles =
    [
        new(PageTurnSoundStyle.SoftPaper, "softPaper", "轻纸", "柔和薄页", "doc-lines"),
        new(PageTurnSoundStyle.CrispBook, "crispBook", "脆页", "清爽新书", "book-open"),
        new(PageTurnSoundStyle.RicePaper, "ricePaper", "宣纸", "绵厚柔润", "leaf"),
        new(PageTurnSoundStyle.Hardcover, "hardcover", "精装", "沉稳厚页", "book"),
        new(PageTurnSoundStyle.WoodClick, "woodClick", "轻叩", "指尖木案", "sparkle"),
        new(PageTurnSoundStyle.InkDrop, "inkDrop", "滴墨", "水墨落砚", "sparkles"),
    ];

    public static PageTurnSoundStyle ParseStyle(string? raw) => (raw ?? "").Trim() switch
    {
        "softPaper" => PageTurnSoundStyle.SoftPaper,
        "crispBook" => PageTurnSoundStyle.CrispBook,
        "ricePaper" => PageTurnSoundStyle.RicePaper,
        "hardcover" => PageTurnSoundStyle.Hardcover,
        "woodClick" => PageTurnSoundStyle.WoodClick,
        "inkDrop" => PageTurnSoundStyle.InkDrop,
        _ => PageTurnSoundStyle.SoftPaper,
    };

    public static string ToKey(PageTurnSoundStyle style) => style switch
    {
        PageTurnSoundStyle.SoftPaper => "softPaper",
        PageTurnSoundStyle.CrispBook => "crispBook",
        PageTurnSoundStyle.RicePaper => "ricePaper",
        PageTurnSoundStyle.Hardcover => "hardcover",
        PageTurnSoundStyle.WoodClick => "woodClick",
        PageTurnSoundStyle.InkDrop => "inkDrop",
        _ => "softPaper",
    };

    public static PageTurnSoundStyleInfo InfoFor(PageTurnSoundStyle style) =>
        AllStyles[(int)style];

    public static byte[] SynthesizeWav(PageTurnSoundStyle style, int variant = 0)
    {
        int v = ((variant % VariantCount) + VariantCount) % VariantCount;
        short[] samples = RenderSamples(style, v);
        return EncodeWav(samples, SampleRate);
    }

    public static short[] RenderSamples(PageTurnSoundStyle style, int variant)
    {
        double sr = SampleRate;
        double[] pitchScales = [1.0, 0.965, 1.04];
        double[] durScales = [1.0, 1.04, 0.96];
        int v = ((variant % VariantCount) + VariantCount) % VariantCount;
        double pitchScale = pitchScales[v];
        double durScale = durScales[v];
        ulong styleIndex = (ulong)style;
        var rng = new Rng(unchecked(0x51A7_E21CUL + styleIndex * 7_919UL + (ulong)v * 104_729UL));

        switch (style)
        {
            case PageTurnSoundStyle.SoftPaper:
            {
                double dur = 0.225 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var p3 = new Pink();
                var bpArc = new Biquad(BiquadKind.Bp, 600.0, 1.15);
                var lpArc = new Biquad(BiquadKind.Lp, 3900.0 * pitchScale, 0.707);
                var bpAir = new Biquad(BiquadKind.Bp, 280.0, 0.85);
                var bpSet = new Biquad(BiquadKind.Bp, 480.0, 1.05);
                var lpSet = new Biquad(BiquadKind.Lp, 2100.0 * pitchScale, 0.707);
                double brown = 0.0;
                var buf = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / n;
                    if ((i & 15) == 0)
                    {
                        double sweep = 520.0 + 1040.0 * Math.Pow(Math.Sin(Math.PI * Math.Min(1.0, u * 1.12)), 1.3);
                        bpArc.Set(BiquadKind.Bp, sweep * pitchScale, 1.15);
                        double airF = (220.0 + 260.0 * Math.Sin(Math.PI * u)) * pitchScale;
                        bpAir.Set(BiquadKind.Bp, airF, 0.85);
                        double setF = (380.0 + 260.0 * Math.Max(0.0, (u - 0.5) * 2.0)) * pitchScale;
                        bpSet.Set(BiquadKind.Bp, setF, 1.05);
                    }
                    double w1 = rng.NextSigned();
                    double w2 = rng.NextSigned();
                    double w3 = rng.NextSigned();
                    double pk1 = p1.Next(w1);
                    brown = 0.96 * brown + 0.08 * w2;
                    double pk3 = p3.Next(w3);

                    double envArc = Arch(u, 0.38);
                    double envAir = Arch(u, 0.32);
                    double envSet = Gauss(u, 0.72, 0.13);

                    double l1 = lpArc.Run(bpArc.Run(pk1)) * envArc * 1.15;
                    double l2 = bpAir.Run(brown) * envAir * 0.85;
                    double l3 = lpSet.Run(bpSet.Run(pk3)) * envSet * 0.42;
                    buf[i] = l1 + l2 + l3;
                }
                return FinalizeSamples(buf, targetPeak: 0.52, fadeInMs: 6.0, fadeOutMs: 18.0);
            }

            case PageTurnSoundStyle.CrispBook:
            {
                double dur = 0.185 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var p2 = new Pink();
                var p3 = new Pink();
                var bpFlick = new Biquad(BiquadKind.Bp, 2100.0 * pitchScale, 1.6);
                var bpArc = new Biquad(BiquadKind.Bp, 1200.0, 1.25);
                var lpArc = new Biquad(BiquadKind.Lp, 5000.0 * pitchScale, 0.707);
                var bpCrackle = new Biquad(BiquadKind.Bp, 2400.0 * pitchScale, 1.9);
                var bpSet = new Biquad(BiquadKind.Bp, 640.0 * pitchScale, 1.35);
                var lpSet = new Biquad(BiquadKind.Lp, 2800.0 * pitchScale, 0.707);
                var buf = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / n;
                    double t = i / sr;
                    if ((i & 15) == 0)
                    {
                        double sweep = 880.0 + 1750.0 * Math.Pow(Math.Sin(Math.PI * Math.Min(1.0, u * 1.15)), 1.15);
                        bpArc.Set(BiquadKind.Bp, sweep * pitchScale, 1.25);
                    }
                    double w1 = rng.NextSigned();
                    double w2 = rng.NextSigned();
                    double w3 = rng.NextSigned();
                    double pk1 = p1.Next(w1);
                    double pk2 = p2.Next(w2);
                    double pk3 = p3.Next(w3);

                    double envFlick = Gauss(t, 0.015, 0.0075);
                    double envArc = Arch(u, 0.40);
                    double envSet = Gauss(u, 0.76, 0.095);

                    double imp = rng.NextUnit() < 0.004 ? w2 * 0.7 : 0.0;
                    double crackle = bpCrackle.Run(imp) * envArc * 0.35;

                    double l1 = bpFlick.Run(pk2) * envFlick * 0.42;
                    double l2 = lpArc.Run(bpArc.Run(pk1)) * envArc * 1.10;
                    double l3 = lpSet.Run(bpSet.Run(pk3)) * envSet * 0.50;
                    buf[i] = l1 + l2 + l3 + crackle;
                }
                return FinalizeSamples(buf, targetPeak: 0.54, fadeInMs: 3.0, fadeOutMs: 14.0);
            }

            case PageTurnSoundStyle.RicePaper:
            {
                double dur = 0.275 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var p2 = new Pink();
                var bpBody = new Biquad(BiquadKind.Bp, 420.0, 0.95);
                var lpBody = new Biquad(BiquadKind.Lp, 2000.0 * pitchScale, 0.65);
                var lpAir = new Biquad(BiquadKind.Lp, 460.0 * pitchScale, 0.707);
                var bpFiber = new Biquad(BiquadKind.Bp, 1050.0, 1.15);
                var lpFiber = new Biquad(BiquadKind.Lp, 2100.0 * pitchScale, 0.707);
                double brown1 = 0.0;
                double brown2 = 0.0;
                var buf = new double[n];
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / n;
                    if ((i & 15) == 0)
                    {
                        double sweep = 310.0 + 530.0 * Math.Pow(Math.Sin(Math.PI * u), 1.1);
                        bpBody.Set(BiquadKind.Bp, sweep * pitchScale, 0.95);
                        double fibF = (1120.0 - 420.0 * u) * pitchScale;
                        bpFiber.Set(BiquadKind.Bp, fibF, 1.15);
                    }
                    double w1 = rng.NextSigned();
                    double w2 = rng.NextSigned();
                    double pk1 = p1.Next(w1);
                    double pk2 = p2.Next(w2);
                    brown1 = 0.94 * brown1 + 0.12 * w1;
                    brown2 = 0.975 * brown2 + 0.06 * w2;

                    double envBody = 0.72 * Arch(u, 0.35) + 0.48 * Gauss(u, 0.68, 0.16);
                    double envAir = Arch(u, 0.45);
                    double envFib = Gauss(u, 0.52, 0.22);

                    double l1 = lpBody.Run(bpBody.Run(0.55 * pk1 + 0.45 * brown1)) * envBody * 1.2;
                    double l2 = lpAir.Run(brown2) * envAir * 0.9;
                    double l3 = lpFiber.Run(bpFiber.Run(pk2)) * envFib * 0.24;
                    buf[i] = l1 + l2 + l3;
                }
                return FinalizeSamples(buf, targetPeak: 0.48, fadeInMs: 10.0, fadeOutMs: 26.0);
            }

            case PageTurnSoundStyle.Hardcover:
            {
                double dur = 0.260 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var p2 = new Pink();
                var bpArc = new Biquad(BiquadKind.Bp, 650.0, 1.1);
                var lpArc = new Biquad(BiquadKind.Lp, 3700.0 * pitchScale, 0.707);
                var bpFlex = new Biquad(BiquadKind.Bp, 220.0, 1.05);
                var bpLand = new Biquad(BiquadKind.Bp, 420.0 * pitchScale, 1.2);
                var lpLand = new Biquad(BiquadKind.Lp, 1600.0 * pitchScale, 0.707);
                double brown = 0.0;
                var buf = new double[n];
                double thumpT0 = dur * 0.73;
                for (int i = 0; i < n; i++)
                {
                    double u = (double)i / n;
                    double t = i / sr;
                    if ((i & 15) == 0)
                    {
                        double sweep = 420.0 + 880.0 * Math.Pow(Math.Sin(Math.PI * Math.Min(1.0, u * 1.1)), 1.25);
                        bpArc.Set(BiquadKind.Bp, sweep * pitchScale, 1.1);
                        double flexF = (165.0 + 185.0 * Math.Sin(Math.PI * u)) * pitchScale;
                        bpFlex.Set(BiquadKind.Bp, flexF, 1.05);
                    }
                    double w1 = rng.NextSigned();
                    double w2 = rng.NextSigned();
                    double pk1 = p1.Next(w1);
                    double pk2 = p2.Next(w2);
                    brown = 0.965 * brown + 0.08 * w2;

                    double envArc = Arch(u, 0.38);
                    double envFlex = Arch(u, 0.34);
                    double envLand = Gauss(u, 0.75, 0.095);

                    double thump = 0.0;
                    double dt = t - thumpT0;
                    if (dt > 0.0)
                    {
                        double thEnv = Smoothstep(dt / 0.007) * Math.Exp(-dt / 0.026);
                        double f1 = 132.0 * pitchScale;
                        double f2 = 258.0 * pitchScale;
                        thump = (0.72 * Math.Sin(2.0 * Math.PI * f1 * dt) + 0.28 * Math.Sin(2.0 * Math.PI * f2 * dt)) * thEnv * 0.16;
                    }

                    double l1 = lpArc.Run(bpArc.Run(pk1)) * envArc * 1.05;
                    double l2 = bpFlex.Run(brown) * envFlex * 1.15;
                    double l3 = lpLand.Run(bpLand.Run(pk2)) * envLand * 0.55;
                    buf[i] = l1 + l2 + l3 + thump;
                }
                return FinalizeSamples(buf, targetPeak: 0.58, fadeInMs: 5.0, fadeOutMs: 20.0);
            }

            case PageTurnSoundStyle.WoodClick:
            {
                double dur = 0.110 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var bpHit = new Biquad(BiquadKind.Bp, 780.0 * pitchScale, 1.1);
                var lpHit = new Biquad(BiquadKind.Lp, 1450.0 * pitchScale, 0.707);
                var buf = new double[n];
                double ph1 = 0.0, ph2 = 0.0, ph3 = 0.0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / sr;
                    double bend = 1.0 + 0.06 * Math.Exp(-t / 0.004);
                    double f1 = 460.0 * pitchScale * bend;
                    double f2 = 1035.0 * pitchScale * bend;
                    double f3 = 1920.0 * pitchScale * bend;
                    ph1 += 2.0 * Math.PI * f1 / sr;
                    ph2 += 2.0 * Math.PI * f2 / sr;
                    ph3 += 2.0 * Math.PI * f3 / sr;

                    double atk = Smoothstep(t / 0.0025);
                    double m1 = 0.65 * Math.Sin(ph1) * Math.Exp(-t / 0.017);
                    double m2 = 0.28 * Math.Sin(ph2) * Math.Exp(-t / 0.009);
                    double m3 = 0.09 * Math.Sin(ph3) * Math.Exp(-t / 0.0045);

                    double w = rng.NextSigned();
                    double hit = lpHit.Run(bpHit.Run(p1.Next(w))) * Gauss(t, 0.003, 0.0025) * 0.55;
                    buf[i] = (m1 + m2 + m3) * atk + hit;
                }
                return FinalizeSamples(buf, targetPeak: 0.46, fadeInMs: 2.0, fadeOutMs: 12.0);
            }

            case PageTurnSoundStyle.InkDrop:
            default:
            {
                double dur = 0.145 * durScale;
                int n = (int)(sr * dur);
                var p1 = new Pink();
                var bpPlip = new Biquad(BiquadKind.Bp, 540.0 * pitchScale, 1.4);
                var buf = new double[n];
                double ph = 0.0, ph2 = 0.0;
                const double t0 = 0.008;
                for (int i = 0; i < n; i++)
                {
                    double t = i / sr;
                    double w = rng.NextSigned();
                    double plip = bpPlip.Run(p1.Next(w)) * Gauss(t, 0.006, 0.0035) * 0.18;
                    double drop = 0.0;
                    double dt = t - t0;
                    if (dt > 0.0)
                    {
                        double f = (620.0 + 440.0 * (1.0 - Math.Exp(-dt / 0.030))) * pitchScale;
                        ph += 2.0 * Math.PI * f / sr;
                        ph2 += 2.0 * Math.PI * (f * 2.0) / sr;
                        double env = Smoothstep(dt / 0.006) * Math.Exp(-dt / 0.030);
                        double env2 = Smoothstep(dt / 0.005) * Math.Exp(-dt / 0.014);
                        drop = Math.Sin(ph) * env + 0.11 * Math.Sin(ph2) * env2;
                    }
                    buf[i] = plip + drop;
                }
                return FinalizeSamples(buf, targetPeak: 0.44, fadeInMs: 2.0, fadeOutMs: 16.0);
            }
        }
    }

    public static byte[] EncodeWav(short[] samples, int sampleRate)
    {
        uint dataBytes = (uint)(samples.Length * 2);
        var data = new byte[44 + dataBytes];
        // "RIFF"
        data[0] = 0x52; data[1] = 0x49; data[2] = 0x46; data[3] = 0x46;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 36 + dataBytes);
        // "WAVE"
        data[8] = 0x57; data[9] = 0x41; data[10] = 0x56; data[11] = 0x45;
        // "fmt "
        data[12] = 0x66; data[13] = 0x6D; data[14] = 0x74; data[15] = 0x20;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20, 2), 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22, 2), 1); // mono
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24, 4), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28, 4), (uint)(sampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34, 2), 16);
        // "data"
        data[36] = 0x64; data[37] = 0x61; data[38] = 0x74; data[39] = 0x61;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40, 4), dataBytes);
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(44 + i * 2, 2), samples[i]);
        }
        return data;
    }

    /// <summary>
    /// Decodes a standard PCM (8/16/24/32-bit int) or 32-bit IEEE float WAV buffer into mono float samples,
    /// capped at <paramref name="maxSeconds"/> and resampled to <see cref="SampleRate"/> if needed.
    /// </summary>
    public static bool TryDecodePcmWav(byte[] wavBytes, out float[] monoSamples, out int sampleRate, double maxSeconds = 8.0)
    {
        monoSamples = [];
        sampleRate = 0;
        if (wavBytes.Length < 44) return false;
        if (wavBytes[0] != (byte)'R' || wavBytes[1] != (byte)'I' || wavBytes[2] != (byte)'F' || wavBytes[3] != (byte)'F')
            return false;
        if (wavBytes[8] != (byte)'W' || wavBytes[9] != (byte)'A' || wavBytes[10] != (byte)'V' || wavBytes[11] != (byte)'E')
            return false;

        ushort audioFormat = 0;
        ushort channels = 0;
        int sr = 0;
        ushort bitsPerSample = 0;
        int dataOffset = -1;
        int dataSize = 0;

        int pos = 12;
        while (pos + 8 <= wavBytes.Length)
        {
            string chunkId = System.Text.Encoding.ASCII.GetString(wavBytes, pos, 4);
            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.AsSpan(pos + 4, 4));
            int bodyStart = pos + 8;
            int avail = Math.Max(0, Math.Min((int)Math.Min(chunkSize, int.MaxValue), wavBytes.Length - bodyStart));

            if (chunkId == "fmt " && avail >= 16)
            {
                audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(bodyStart, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(bodyStart + 2, 2));
                sr = (int)BinaryPrimitives.ReadUInt32LittleEndian(wavBytes.AsSpan(bodyStart + 4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(wavBytes.AsSpan(bodyStart + 14, 2));
            }
            else if (chunkId == "data")
            {
                dataOffset = bodyStart;
                dataSize = avail;
                break;
            }

            long next = (long)bodyStart + chunkSize + (chunkSize & 1);
            if (next <= pos || next > wavBytes.Length) break;
            pos = (int)next;
        }

        if (dataOffset < 0 || dataSize <= 0 || channels == 0 || sr <= 0) return false;
        bool isPcm = audioFormat == 1;
        bool isFloat = audioFormat == 3 && bitsPerSample == 32;
        if (!isPcm && !isFloat) return false;
        int bytesPerSample = bitsPerSample / 8;
        if (bytesPerSample is not (1 or 2 or 3 or 4)) return false;

        int frameBytes = bytesPerSample * channels;
        int totalFrames = dataSize / frameBytes;
        int maxFrames = (int)Math.Min(totalFrames, Math.Max(1, sr * maxSeconds));
        if (maxFrames <= 0) return false;

        var raw = new float[maxFrames];
        for (int f = 0; f < maxFrames; f++)
        {
            int frameOff = dataOffset + f * frameBytes;
            double sum = 0.0;
            for (int c = 0; c < channels; c++)
            {
                int off = frameOff + c * bytesPerSample;
                double s = 0.0;
                if (isFloat)
                {
                    s = BitConverter.ToSingle(wavBytes, off);
                }
                else if (bitsPerSample == 16)
                {
                    s = BinaryPrimitives.ReadInt16LittleEndian(wavBytes.AsSpan(off, 2)) / 32768.0;
                }
                else if (bitsPerSample == 8)
                {
                    s = (wavBytes[off] - 128) / 128.0;
                }
                else if (bitsPerSample == 24)
                {
                    int v24 = wavBytes[off] | (wavBytes[off + 1] << 8) | (wavBytes[off + 2] << 16);
                    if ((v24 & 0x800000) != 0) v24 |= unchecked((int)0xFF000000);
                    s = v24 / 8388608.0;
                }
                else if (bitsPerSample == 32)
                {
                    s = BinaryPrimitives.ReadInt32LittleEndian(wavBytes.AsSpan(off, 4)) / 2147483648.0;
                }
                sum += s;
            }
            raw[f] = (float)(sum / channels);
        }

        if (sr == SampleRate)
        {
            monoSamples = raw;
            sampleRate = SampleRate;
            return true;
        }

        // Linear resample to 44.1 kHz
        double ratio = (double)SampleRate / sr;
        int outLen = Math.Max(1, (int)Math.Round(raw.Length * ratio));
        var resampled = new float[outLen];
        for (int i = 0; i < outLen; i++)
        {
            double srcPos = i / ratio;
            int idx0 = Math.Min(raw.Length - 1, (int)srcPos);
            int idx1 = Math.Min(raw.Length - 1, idx0 + 1);
            double frac = srcPos - idx0;
            resampled[i] = (float)(raw[idx0] * (1.0 - frac) + raw[idx1] * frac);
        }
        monoSamples = resampled;
        sampleRate = SampleRate;
        return true;
    }

    /// <summary>
    /// Pure DSP post-processor: DC + 60 Hz rumble HP filter → smart leading/trailing silence trim →
    /// 0.65 s max cap → smooth raised-cosine edge fades → peak normalization → 16-bit WAV + waveform.
    /// Exact port of macOS <c>CustomPageTurnSounds.trimAndNormalize</c>.
    /// </summary>
    public static ProcessedCustomSound TrimAndNormalize(float[] samples, int sampleRate)
    {
        if (samples is null || samples.Length == 0 || sampleRate <= 0)
            throw new CustomSoundException(CustomSoundErrorKind.SilentOrEmpty);

        int n = samples.Length;
        double mean = 0.0;
        for (int i = 0; i < n; i++) mean += samples[i];
        mean /= n;

        double rc = 1.0 / (2.0 * Math.PI * 60.0);
        double dt = 1.0 / sampleRate;
        double alpha = rc / (rc + dt);
        var cleaned = new double[n];
        double prevX = 0.0, prevY = 0.0, peak = 0.0;
        for (int i = 0; i < n; i++)
        {
            double x = samples[i] - mean;
            double y = alpha * (prevY + x - prevX);
            prevX = x;
            prevY = y;
            cleaned[i] = y;
            double ay = Math.Abs(y);
            if (ay > peak) peak = ay;
        }
        if (peak < 0.004)
            throw new CustomSoundException(CustomSoundErrorKind.SilentOrEmpty);

        // Trim leading silence (keep 8 ms pre-roll so natural attack transient stays intact).
        double startThresh = Math.Max(peak * 0.045, 0.006);
        int firstActive = 0;
        for (int i = 0; i < n; i++)
        {
            if (Math.Abs(cleaned[i]) >= startThresh)
            {
                firstActive = i;
                break;
            }
        }
        int preRoll = (int)(sampleRate * 0.008);
        int trimStart = Math.Max(0, firstActive - preRoll);

        // Trim trailing silence inside a max 0.65 s window from trimStart.
        int maxFrames = (int)(sampleRate * MaxDurationSeconds);
        int searchEnd = Math.Min(n, trimStart + maxFrames);
        double endThresh = Math.Max(peak * 0.022, 0.003);
        int lastActive = trimStart;
        for (int i = searchEnd - 1; i >= trimStart; i--)
        {
            if (Math.Abs(cleaned[i]) >= endThresh)
            {
                lastActive = i;
                break;
            }
        }
        int postRoll = (int)(sampleRate * 0.024);
        int trimEnd = Math.Min(searchEnd, lastActive + postRoll);
        int length = trimEnd - trimStart;
        int minFrames = (int)(sampleRate * MinDurationSeconds);
        if (length < minFrames)
            throw new CustomSoundException(CustomSoundErrorKind.TooShort);

        // Focused ~0.72–1.40 s context window around the active sound.
        int padBefore = (int)(sampleRate * 0.16);
        int padAfter = (int)(sampleRate * 0.22);
        int minWindow = (int)(sampleRate * 0.72);
        int maxWindow = (int)(sampleRate * 1.40);
        int winStart = Math.Max(0, trimStart - padBefore);
        int winEnd = Math.Min(n, Math.Max(trimEnd + padAfter, Math.Min(n, winStart + minWindow)));
        if (winEnd - winStart > maxWindow)
            winEnd = Math.Min(n, Math.Max(trimEnd, winStart + maxWindow));

        int winLen = Math.Max(1, winEnd - winStart);
        var windowSamples = new float[winLen];
        for (int i = 0; i < winLen; i++)
            windowSamples[i] = (float)cleaned[winStart + i];

        double relStart = (double)(trimStart - winStart) / winLen;
        double relEnd = (double)(trimEnd - winStart) / winLen;
        float[] waveform = ComputeWaveform(windowSamples, WaveformBarCount);
        bool truncated = trimEnd == trimStart + maxFrames && trimEnd < n;

        var slice = new double[length];
        Array.Copy(cleaned, trimStart, slice, 0, length);
        var (wav, durationMs) = EncodeNormalizedSlice(slice, sampleRate, truncated);

        return new ProcessedCustomSound(
            WavData: wav,
            DurationMs: durationMs,
            SourceSamples: windowSamples,
            SampleRate: sampleRate,
            TrimStart: relStart,
            TrimEnd: relEnd,
            AutoTrimStart: relStart,
            AutoTrimEnd: relEnd,
            Waveform: waveform);
    }

    public static ProcessedCustomSound? RenderTrimmed(
        ProcessedCustomSound baseSound,
        double startFraction,
        double endFraction)
    {
        int total = baseSound.SourceSamples.Length;
        if (total <= 0 || baseSound.SampleRate <= 0) return null;
        double clampedStart = Math.Clamp(startFraction, 0.0, 1.0);
        double clampedEnd = Math.Clamp(endFraction, clampedStart, 1.0);
        int startIdx = Math.Clamp((int)Math.Round(clampedStart * total), 0, total - 1);
        int maxFrames = (int)(baseSound.SampleRate * MaxDurationSeconds);
        int minFrames = (int)(baseSound.SampleRate * MinDurationSeconds);
        int endIdx = Math.Max(startIdx + minFrames, Math.Min(total, (int)Math.Round(clampedEnd * total)));
        if (endIdx - startIdx > maxFrames) endIdx = startIdx + maxFrames;
        if (endIdx > total || endIdx - startIdx < minFrames) return null;

        int len = endIdx - startIdx;
        var slice = new double[len];
        for (int i = 0; i < len; i++) slice[i] = baseSound.SourceSamples[startIdx + i];
        bool truncated = endIdx < total;
        try
        {
            var (wav, durationMs) = EncodeNormalizedSlice(slice, baseSound.SampleRate, truncated);
            return baseSound with
            {
                WavData = wav,
                DurationMs = durationMs,
                TrimStart = (double)startIdx / total,
                TrimEnd = (double)endIdx / total,
            };
        }
        catch
        {
            return null;
        }
    }

    public static float[] ComputeWaveform(float[] samples, int barCount)
    {
        if (barCount <= 0) return [];
        if (samples is null || samples.Length == 0)
        {
            var fallback = new float[barCount];
            Array.Fill(fallback, 0.1f);
            return fallback;
        }

        var peaks = new float[barCount];
        float maxPeak = 0.0001f;
        int n = samples.Length;
        for (int b = 0; b < barCount; b++)
        {
            int s0 = (b * n) / barCount;
            int s1 = Math.Max(s0 + 1, ((b + 1) * n) / barCount);
            float p = 0f;
            float sq = 0f;
            int end = Math.Min(n, s1);
            for (int i = s0; i < end; i++)
            {
                float a = Math.Abs(samples[i]);
                if (a > p) p = a;
                sq += a * a;
            }
            float rms = MathF.Sqrt(sq / Math.Max(1, end - s0));
            float v = p * 0.65f + rms * 0.35f;
            peaks[b] = v;
            if (v > maxPeak) maxPeak = v;
        }

        var outBars = new float[barCount];
        for (int b = 0; b < barCount; b++)
        {
            float norm = Math.Clamp(peaks[b] / maxPeak, 0f, 1f);
            outBars[b] = Math.Max(0.08f, MathF.Pow(norm, 0.72f));
        }
        return outBars;
    }

    private static (byte[] Wav, int DurationMs) EncodeNormalizedSlice(double[] rawSlice, int sampleRate, bool truncated)
    {
        var slice = (double[])rawSlice.Clone();
        double mean = 0.0;
        for (int i = 0; i < slice.Length; i++) mean += slice[i];
        mean /= slice.Length;
        for (int i = 0; i < slice.Length; i++) slice[i] -= mean;

        int fadeIn = Math.Min(slice.Length / 3, Math.Max(1, (int)(sampleRate * 0.004)));
        int fadeOut = Math.Min(slice.Length / 2, Math.Max(1, (int)(sampleRate * (truncated ? 0.065 : 0.022))));
        for (int i = 0; i < fadeIn; i++)
        {
            double u = (double)i / fadeIn;
            slice[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * u);
        }
        for (int i = 0; i < fadeOut; i++)
        {
            double u = (double)i / fadeOut;
            slice[slice.Length - 1 - i] *= 0.5 - 0.5 * Math.Cos(Math.PI * u);
        }

        double peak = 0.0;
        for (int i = 0; i < slice.Length; i++)
        {
            double a = Math.Abs(slice[i]);
            if (a > peak) peak = a;
        }
        if (peak <= 1e-5)
            throw new CustomSoundException(CustomSoundErrorKind.SilentOrEmpty);

        const double targetPeak = 0.60;
        double gain = targetPeak / peak;
        var pcm = new short[slice.Length];
        for (int i = 0; i < slice.Length; i++)
        {
            double v = Math.Clamp(slice[i] * gain, -1.0, 1.0);
            pcm[i] = (short)(v * short.MaxValue);
        }
        byte[] wav = EncodeWav(pcm, sampleRate);
        int durationMs = (int)Math.Round((double)slice.Length / sampleRate * 1000.0);
        return (wav, durationMs);
    }

    private static short[] FinalizeSamples(double[] raw, double targetPeak, double fadeInMs, double fadeOutMs)
    {
        int n = raw.Length;
        if (n == 0) return [];
        var buf = (double[])raw.Clone();
        double mean = 0.0;
        for (int i = 0; i < n; i++) mean += buf[i];
        mean /= n;
        for (int i = 0; i < n; i++) buf[i] -= mean;

        double sr = SampleRate;
        int fi = Math.Min(n, Math.Max(1, (int)(sr * fadeInMs / 1000.0)));
        int fo = Math.Min(n, Math.Max(1, (int)(sr * fadeOutMs / 1000.0)));
        for (int i = 0; i < fi; i++)
        {
            double u = (double)i / fi;
            buf[i] *= 0.5 - 0.5 * Math.Cos(Math.PI * u);
        }
        for (int i = 0; i < fo; i++)
        {
            double u = (double)i / fo;
            buf[n - 1 - i] *= 0.5 - 0.5 * Math.Cos(Math.PI * u);
        }

        double peak = 0.0;
        for (int i = 0; i < n; i++)
        {
            double a = Math.Abs(buf[i]);
            if (a > peak) peak = a;
        }
        double gain = peak > 1e-6 ? targetPeak / peak : 1.0;
        var pcm = new short[n];
        for (int i = 0; i < n; i++)
        {
            double clamped = Math.Clamp(buf[i] * gain, -1.0, 1.0);
            pcm[i] = (short)(clamped * short.MaxValue);
        }
        return pcm;
    }

    private static double Smoothstep(double u)
    {
        double c = Math.Clamp(u, 0.0, 1.0);
        return c * c * (3.0 - 2.0 * c);
    }

    private static double Gauss(double x, double mu, double sigma)
    {
        double d = (x - mu) / sigma;
        return Math.Exp(-0.5 * d * d);
    }

    private static double Arch(double u, double peak)
    {
        if (u <= 0.0 || u >= 1.0) return 0.0;
        if (u < peak) return Math.Pow(Math.Sin((u / peak) * Math.PI * 0.5), 1.6);
        return Math.Pow(Math.Cos(((u - peak) / (1.0 - peak)) * Math.PI * 0.5), 2.1);
    }

    private struct Rng
    {
        private ulong _state;

        public Rng(ulong seed)
        {
            ulong mixed = unchecked(seed * 0x9E37_79B9_7F4A_7C15UL);
            _state = mixed == 0 ? 1UL : mixed;
        }

        public double NextSigned()
        {
            _state = unchecked(_state * 6_364_136_223_846_793_005UL + 1_442_695_040_888_963_407UL);
            double v = ((uint)(_state >> 33)) & 0x7FFF_FFFFU;
            return (v / 2_147_483_647.0) * 2.0 - 1.0;
        }

        public double NextUnit() => 0.5 * (NextSigned() + 1.0);
    }

    private struct Pink
    {
        private double _b0, _b1, _b2;

        public double Next(double w)
        {
            _b0 = 0.99765 * _b0 + w * 0.0990460;
            _b1 = 0.96300 * _b1 + w * 0.2965164;
            _b2 = 0.57000 * _b2 + w * 1.0526913;
            return (_b0 + _b1 + _b2 + w * 0.1848) * 0.25;
        }
    }

    private enum BiquadKind { Lp, Bp }

    private struct Biquad
    {
        private double _b0, _b1, _b2, _a1, _a2, _z1, _z2;

        public Biquad(BiquadKind kind, double freq, double q)
        {
            _b0 = _b1 = _b2 = _a1 = _a2 = _z1 = _z2 = 0.0;
            Set(kind, freq, q);
        }

        public void Set(BiquadKind kind, double freq, double q)
        {
            double sr = SampleRate;
            double f = Math.Clamp(freq, 20.0, sr * 0.45);
            double w = 2.0 * Math.PI * f / sr;
            double s = Math.Sin(w);
            double c = Math.Cos(w);
            double alpha = s / (2.0 * Math.Max(0.1, q));
            double rawB0, rawB1, rawB2;
            if (kind == BiquadKind.Lp)
            {
                rawB0 = (1.0 - c) * 0.5;
                rawB1 = 1.0 - c;
                rawB2 = (1.0 - c) * 0.5;
            }
            else
            {
                rawB0 = alpha;
                rawB1 = 0.0;
                rawB2 = -alpha;
            }
            double a0 = 1.0 + alpha;
            _b0 = rawB0 / a0;
            _b1 = rawB1 / a0;
            _b2 = rawB2 / a0;
            _a1 = -2.0 * c / a0;
            _a2 = (1.0 - alpha) / a0;
        }

        public double Run(double x)
        {
            double y = _b0 * x + _z1;
            _z1 = _b1 * x - _a1 * y + _z2;
            _z2 = _b2 * x - _a2 * y;
            return y;
        }
    }
}

/// <summary>
/// Persists user-recorded and imported custom page-turn sounds under <c>&lt;DataDir&gt;/PageTurnSounds/</c>.
/// </summary>
public sealed class CustomPageTurnSoundStore
{
    public const string CustomPrefix = "custom:";
    public const int MaxCustomNameLength = 18;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _dir;
    private readonly string _indexPath;
    private readonly List<CustomPageTurnSound> _items = [];
    private readonly ConcurrentDictionary<string, byte[]> _wavCache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public event Action? Changed;

    public CustomPageTurnSoundStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "PageTurnSounds");
        _indexPath = Path.Combine(_dir, "index.json");
        LoadIndex();
    }

    public IReadOnlyList<CustomPageTurnSound> Items
    {
        get
        {
            lock (_gate) return _items.ToList();
        }
    }

    public static string? CustomIdFrom(string? selectionKey)
    {
        if (string.IsNullOrWhiteSpace(selectionKey)) return null;
        if (!selectionKey.StartsWith(CustomPrefix, StringComparison.Ordinal)) return null;
        string id = selectionKey[CustomPrefix.Length..].Trim();
        return string.IsNullOrEmpty(id) ? null : id;
    }

    public CustomPageTurnSound? GetItem(string? selectionKey)
    {
        string? id = CustomIdFrom(selectionKey);
        if (id is null) return null;
        lock (_gate) return _items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
    }

    public byte[]? GetWavBytes(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (_wavCache.TryGetValue(id, out var cached)) return cached;
        string path = FilePathFor(id);
        if (!File.Exists(path)) return null;
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            _wavCache[id] = bytes;
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public CustomPageTurnSound Save(ProcessedCustomSound processed, string? rawName)
    {
        Directory.CreateDirectory(_dir);
        string id = Guid.NewGuid().ToString("N")[..12];
        string name = SanitizeName(rawName, fallback: NextDefaultName());
        string wavPath = FilePathFor(id);
        try
        {
            File.WriteAllBytes(wavPath, processed.WavData);
        }
        catch (Exception ex)
        {
            throw new CustomSoundException(CustomSoundErrorKind.SaveFailed, ex.Message);
        }

        var item = new CustomPageTurnSound
        {
            Id = id,
            Name = name,
            DurationMs = processed.DurationMs,
            CreatedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        lock (_gate)
        {
            _items.Add(item);
            _wavCache[id] = processed.WavData;
            SaveIndexLocked();
        }
        Changed?.Invoke();
        return item;
    }

    public bool Rename(string id, string newName)
    {
        string trimmed = SanitizeName(newName, fallback: "");
        if (string.IsNullOrEmpty(trimmed)) return false;
        bool updated = false;
        lock (_gate)
        {
            var it = _items.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
            if (it is not null && !string.Equals(it.Name, trimmed, StringComparison.Ordinal))
            {
                it.Name = trimmed;
                SaveIndexLocked();
                updated = true;
            }
        }
        if (updated) Changed?.Invoke();
        return updated;
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _items.RemoveAll(x => string.Equals(x.Id, id, StringComparison.Ordinal)) > 0;
            if (removed) SaveIndexLocked();
        }
        _wavCache.TryRemove(id, out _);
        try
        {
            string path = FilePathFor(id);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>
    /// Decodes an imported audio file (<c>.wav</c>, <c>.mp3</c>, <c>.m4a</c>, <c>.flac</c>, <c>.ogg</c>, <c>.wma</c>)
    /// to 44.1 kHz mono PCM, trims silence, caps at 0.65 s, and normalizes peak loudness.
    /// </summary>
    public async Task<ProcessedCustomSound> ImportAudioFileAsync(string audioPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            throw new CustomSoundException(CustomSoundErrorKind.Unreadable);

        // Fast path for standard PCM/float WAV files (no temp file or MediaTranscoder overhead)
        if (string.Equals(Path.GetExtension(audioPath), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                byte[] wavBytes = await File.ReadAllBytesAsync(audioPath, ct).ConfigureAwait(false);
                if (PageTurnSoundSynthesizer.TryDecodePcmWav(wavBytes, out float[] samples, out int sr))
                    return PageTurnSoundSynthesizer.TrimAndNormalize(samples, sr);
            }
            catch (CustomSoundException) { throw; }
            catch { /* fall through to MediaTranscoder for ADPCM / exotic WAV codecs */ }
        }

        // General path: decode any Windows Media Foundation supported audio via MediaTranscoder
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-turn-sfx");
        Directory.CreateDirectory(tempDir);
        string tempWav = Path.Combine(tempDir, $"import-{Guid.NewGuid():N}.wav");
        try
        {
            var srcFile = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(audioPath));
            var dstFolder = await StorageFolder.GetFolderFromPathAsync(tempDir);
            var dstFile = await dstFolder.CreateFileAsync(Path.GetFileName(tempWav), CreationCollisionOption.ReplaceExisting);

            var profile = new MediaEncodingProfile
            {
                Audio = AudioEncodingProperties.CreatePcm((uint)PageTurnSoundSynthesizer.SampleRate, 1, 16),
                Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Wave },
            };
            var transcoder = new MediaTranscoder
            {
                HardwareAccelerationEnabled = true,
                TrimStartTime = System.TimeSpan.Zero,
                TrimStopTime = System.TimeSpan.FromSeconds(8.0),
            };
            var prep = await transcoder.PrepareFileTranscodeAsync(srcFile, dstFile, profile);
            if (!prep.CanTranscode)
                throw new CustomSoundException(CustomSoundErrorKind.Unreadable);

            var op = prep.TranscodeAsync();
            using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
            await op;
            ct.ThrowIfCancellationRequested();

            byte[] decoded = await File.ReadAllBytesAsync(tempWav, ct).ConfigureAwait(false);
            if (!PageTurnSoundSynthesizer.TryDecodePcmWav(decoded, out float[] samples, out int sr))
                throw new CustomSoundException(CustomSoundErrorKind.Unreadable);
            return PageTurnSoundSynthesizer.TrimAndNormalize(samples, sr);
        }
        catch (CustomSoundException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new CustomSoundException(CustomSoundErrorKind.Unreadable, ex.Message);
        }
        finally
        {
            try { if (File.Exists(tempWav)) File.Delete(tempWav); } catch { }
        }
    }

    private string FilePathFor(string id) => Path.Combine(_dir, $"{id}.wav");

    private string NextDefaultName()
    {
        int n;
        lock (_gate) n = _items.Count + 1;
        return $"{L10n.T("自定义")} {n}";
    }

    private static string SanitizeName(string? raw, string fallback)
    {
        string s = (raw ?? "").Trim();
        if (s.Length > MaxCustomNameLength) s = s[..MaxCustomNameLength].Trim();
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    private void LoadIndex()
    {
        lock (_gate)
        {
            _items.Clear();
            if (!File.Exists(_indexPath)) return;
            try
            {
                var list = JsonSerializer.Deserialize<List<CustomPageTurnSound>>(File.ReadAllText(_indexPath));
                if (list is null) return;
                foreach (var it in list)
                {
                    if (!string.IsNullOrWhiteSpace(it.Id) && File.Exists(FilePathFor(it.Id)))
                        _items.Add(it);
                }
            }
            catch { }
        }
    }

    private void SaveIndexLocked()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            string tmp = _indexPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, JsonOpts));
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch { }
    }
}

/// <summary>
/// Polyphonic 4-voice sound-effects player for manual page turns &amp; preset/custom auditions.
/// Automatically muted during read-aloud / TTS and never hijacks Windows SMTC media controls.
/// </summary>
public sealed class PageTurnSoundService : IDisposable
{
    public const double DefaultVolume = 0.60;
    public const double MinVolume = 0.15;
    public const double MaxVolume = 1.0;

    private const int VoiceCount = 4;

    private sealed class VoiceSlot : IDisposable
    {
        public MediaPlayer? Player;
        public MediaSource? Source;
        public InMemoryRandomAccessStream? Stream;

        public void ClearSource()
        {
            try { Player?.Pause(); } catch { }
            try { if (Player is not null) Player.Source = null; } catch { }
            try { Source?.Dispose(); } catch { }
            try { Stream?.Dispose(); } catch { }
            Source = null;
            Stream = null;
        }

        public void Dispose()
        {
            ClearSource();
            try { Player?.Dispose(); } catch { }
            Player = null;
        }
    }

    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<(PageTurnSoundStyle Style, int Variant), byte[]> _builtInCache = new();
    private readonly VoiceSlot[] _voices = Enumerable.Range(0, VoiceCount).Select(_ => new VoiceSlot()).ToArray();
    private readonly object _playGate = new();
    private int _voiceCursor;
    private int _variantCursor;
    private bool _disposed;

    public CustomPageTurnSoundStore Store { get; }

    public PageTurnSoundService(LibraryService library)
    {
        _library = library;
        Store = new CustomPageTurnSoundStore(library.DataDir);
    }

    public string ResolveActiveSelectionKey(string? raw = null)
    {
        string key = raw ?? _library.Prefs.PageTurnSoundStyle;
        if (Store.GetItem(key) is { } custom) return custom.SelectionKey;
        return PageTurnSoundSynthesizer.ToKey(PageTurnSoundSynthesizer.ParseStyle(key));
    }

    /// <summary>
    /// Plays the active page-turn sound if enabled and not currently reading aloud.
    /// </summary>
    public void PlayManualTurn(bool isReadingAloud)
    {
        if (_disposed || isReadingAloud || !_library.Prefs.PageTurnSound) return;
        PlaySelection(_library.Prefs.PageTurnSoundStyle, _library.Prefs.PageTurnSoundVolume);
    }

    public void Preview(PageTurnSoundStyle style, double? volume = null)
    {
        if (_disposed) return;
        PlayBuiltIn(style, volume ?? _library.Prefs.PageTurnSoundVolume);
    }

    public void PreviewSelection(string? selectionKey, double? volume = null)
    {
        if (_disposed) return;
        PlaySelection(selectionKey ?? _library.Prefs.PageTurnSoundStyle, volume ?? _library.Prefs.PageTurnSoundVolume);
    }

    public void PreviewWav(byte[] wavBytes, double? volume = null)
    {
        if (_disposed || wavBytes is null || wavBytes.Length < 44) return;
        PlayWavBytes(wavBytes, volume ?? _library.Prefs.PageTurnSoundVolume);
    }

    private void PlaySelection(string? raw, double volume)
    {
        string? customId = CustomPageTurnSoundStore.CustomIdFrom(raw);
        if (customId is not null)
        {
            byte[]? wav = Store.GetWavBytes(customId);
            if (wav is not null)
            {
                PlayWavBytes(wav, volume);
                return;
            }
        }
        PlayBuiltIn(PageTurnSoundSynthesizer.ParseStyle(raw), volume);
    }

    private void PlayBuiltIn(PageTurnSoundStyle style, double volume)
    {
        int variant = Math.Abs(Interlocked.Increment(ref _variantCursor) - 1) % PageTurnSoundSynthesizer.VariantCount;
        byte[] wav = _builtInCache.GetOrAdd((style, variant), static k =>
            PageTurnSoundSynthesizer.SynthesizeWav(k.Style, k.Variant));
        PlayWavBytes(wav, volume);
    }

    private void PlayWavBytes(byte[] wavBytes, double volume)
    {
        double clamped = Math.Clamp(volume, MinVolume, MaxVolume);
        double gain = clamped * 0.80;

        lock (_playGate)
        {
            if (_disposed) return;
            var slot = _voices[_voiceCursor++ % VoiceCount];
            try
            {
                slot.ClearSource();
                if (slot.Player is null)
                {
                    var mp = new MediaPlayer
                    {
                        AutoPlay = false,
                        IsLoopingEnabled = false,
                        AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                    };
                    try { mp.CommandManager.IsEnabled = false; } catch { }
                    slot.Player = mp;
                }

                var mem = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(mem.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(wavBytes);
                    writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                    writer.DetachStream();
                }
                mem.Seek(0);
                var src = MediaSource.CreateFromStream(mem, "audio/wav");
                slot.Stream = mem;
                slot.Source = src;
                slot.Player.Volume = gain;
                slot.Player.Source = src;
                slot.Player.Play();
            }
            catch
            {
                // Gracefully ignore playback failures on headless/no-audio environments.
                slot.ClearSource();
            }
        }
    }

    public void Dispose()
    {
        lock (_playGate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var v in _voices) v.Dispose();
        }
    }
}
