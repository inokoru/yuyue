using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 Windows port of <c>apple/Yuyue/Speech/VoiceTap.swift</c>:
/// <list type="bullet">
///   <item>Loudness normalization targeting -20 dBFS speech (clamped to -6..+12 dB) across 10 sample windows.</item>
///   <item><c>人声清楚一点</c> real-time DSP filter chain: 80 Hz 2nd-order Butterworth high-pass biquad,
///     +3 dB @ 3 kHz presence peak biquad (Q=1), 3:1 dynamic compressor over -24 dBFS (10 ms attack / 150 ms release),
///     and -1 dBFS tanh soft ceiling to prevent clipping.</item>
/// </list>
/// </summary>
public sealed class VoiceTapDsp
{
    public float Gain { get; set; } = 1.0f;
    public bool Clearer { get; set; }

    private double _rate = 44_100.0;
    private int _channels = 2;
    private Biquad[] _hp = [new(), new()];
    private Biquad[] _presence = [new(), new()];
    private float _envelope;

    public VoiceTapDsp(float gain = 1.0f, bool clearer = false)
    {
        Gain = gain;
        Clearer = clearer;
    }

    public void Prepare(double sampleRate, int channels)
    {
        _rate = Math.Max(8_000.0, sampleRate);
        _channels = Math.Max(1, channels);
        int n = Math.Max(2, _channels);
        _hp = new Biquad[n];
        _presence = new Biquad[n];
        for (int c = 0; c < n; c++)
        {
            _hp[c] = Biquad.HighPass(80.0, _rate);
            _presence[c] = Biquad.Peak(3_000.0, 3.0, 1.0, _rate);
        }
        _envelope = 0f;
    }

    /// <summary>Processes interleaved float32 samples in-place.</summary>
    public void ProcessInterleaved(Span<float> interleaved)
    {
        if (interleaved.IsEmpty) return;
        float g = Gain;
        bool clear = Clearer;
        const float threshold = 0.063f; // -24 dBFS
        float attack = (float)Math.Exp(-1.0 / (0.010 * _rate));
        float release = (float)Math.Exp(-1.0 / (0.150 * _rate));
        const float ceiling = 0.89f; // -1 dBFS
        int stride = _channels;

        for (int i = 0; i < interleaved.Length; i++)
        {
            int ch = stride > 1 ? i % stride : 0;
            if (ch >= _hp.Length) ch = _hp.Length - 1;
            float x = interleaved[i] * g;
            if (clear)
            {
                x = _hp[ch].Run(x);
                x = _presence[ch].Run(x);
                if (ch == 0)
                {
                    float level = MathF.Abs(x);
                    _envelope = level > _envelope
                        ? attack * _envelope + (1f - attack) * level
                        : release * _envelope + (1f - release) * level;
                }
                if (_envelope > threshold)
                {
                    float over = _envelope / threshold;
                    x *= MathF.Pow(over, (1f / 3f) - 1f);
                }
            }
            if (MathF.Abs(x) > ceiling)
            {
                float s = x < 0f ? -1f : 1f;
                x = s * (ceiling + (1f - ceiling) * MathF.Tanh((MathF.Abs(x) - ceiling) / (1f - ceiling)));
            }
            interleaved[i] = x;
        }
    }

    /// <summary>Processes interleaved 16-bit PCM samples in-place (for WAV / MediaStreamSource streaming).</summary>
    public void ProcessPcm16(Span<short> pcm16)
    {
        if (pcm16.IsEmpty) return;
        float[] buf = new float[pcm16.Length];
        for (int i = 0; i < pcm16.Length; i++) buf[i] = pcm16[i] / 32768f;
        ProcessInterleaved(buf);
        for (int i = 0; i < pcm16.Length; i++)
            pcm16[i] = (short)Math.Clamp(MathF.Round(buf[i] * 32767f), short.MinValue, short.MaxValue);
    }

    /// <summary>
    /// Measures active speech loudness across 10 evenly spaced 5-second windows of a 16-bit PCM WAV file,
    /// returning linear gain targeting -20 dBFS clamped to [-6 dB, +12 dB] (1:1 <c>VoiceTap.measure</c>).
    /// </summary>
    public static float MeasureLoudnessFromWav(string wavPath)
    {
        if (!Aligner.TryReadWavInfo(wavPath, out var info) || info.SampleRate <= 0 || info.BitsPerSample != 16 || info.DataBytes <= 0)
            return 1.0f;

        int bytesPerFrame = info.Channels * 2;
        if (bytesPerFrame <= 0) return 1.0f;
        long totalFrames = info.DataBytes / bytesPerFrame;
        if (totalFrames <= 0) return 1.0f;

        double sum = 0.0;
        int n = 0;
        int blockFrames = Math.Max(1, (int)(info.SampleRate * 0.05)); // 50 ms blocks
        int windowFrames = info.SampleRate * 5;

        try
        {
            using var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] buf = new byte[windowFrames * bytesPerFrame];
            for (int k = 0; k < 10; k++)
            {
                long startFrame = (long)(totalFrames * (k + 0.5) / 10.0);
                int wantFrames = (int)Math.Min(windowFrames, totalFrames - startFrame);
                if (wantFrames < blockFrames) continue;

                fs.Seek(info.DataOffset + startFrame * bytesPerFrame, SeekOrigin.Begin);
                int wantBytes = wantFrames * bytesPerFrame;
                int got = 0;
                while (got < wantBytes)
                {
                    int r = fs.Read(buf, got, wantBytes - got);
                    if (r <= 0) break;
                    got += r;
                }
                int framesGot = got / bytesPerFrame;
                var shorts = MemoryMarshal.Cast<byte, short>(buf.AsSpan(0, framesGot * bytesPerFrame));
                int i = 0;
                while (i + blockFrames <= framesGot)
                {
                    double ms = 0.0;
                    for (int j = i; j < i + blockFrames; j++)
                    {
                        double s = shorts[j * info.Channels] / 32768.0;
                        ms += s * s;
                    }
                    ms /= blockFrames;
                    if (ms > 1e-5) { sum += ms; n++; }
                    i += blockFrames;
                }
            }
        }
        catch
        {
            return 1.0f;
        }

        if (n <= 0) return 1.0f;
        double db = 10.0 * Math.Log10(sum / n);
        double wantDb = Math.Clamp(-20.0 - db, -6.0, 12.0);
        return (float)Math.Pow(10.0, wantDb / 20.0);
    }

    /// <summary>
    /// Computes linear gain from a 30 ms dB loudness trace (e.g. <see cref="Aligner.ComputeLevels"/>),
    /// targeting -20 dBFS speech clamped to [-6 dB, +12 dB].
    /// </summary>
    public static float ComputeGainFromLevelsDb(ReadOnlySpan<float> levelsDb)
    {
        double sum = 0.0;
        int n = 0;
        for (int i = 0; i < levelsDb.Length; i++)
        {
            float db = levelsDb[i];
            if (db > -50f && float.IsFinite(db))
            {
                sum += Math.Pow(10.0, db / 10.0);
                n++;
            }
        }
        if (n == 0) return 1.0f;
        double meanDb = 10.0 * Math.Log10(sum / n);
        double wantDb = Math.Clamp(-20.0 - meanDb, -6.0, 12.0);
        return (float)Math.Pow(10.0, wantDb / 20.0);
    }

    /// <summary>
    /// Creates a processed WAV copy of <paramref name="pcmWavPath"/> with <see cref="VoiceTapDsp"/> applied
    /// (used for lossless real-time audition / PCM playback when <c>ClearerVoice</c> is active).
    /// </summary>
    public static bool ProcessWavFile(string inputWavPath, string outputWavPath, float gain, bool clearer)
    {
        if (!Aligner.TryReadWavInfo(inputWavPath, out var info) || info.BitsPerSample != 16 || info.DataBytes <= 0)
            return false;

        try
        {
            var dsp = new VoiceTapDsp(gain, clearer);
            dsp.Prepare(info.SampleRate, info.Channels);

            using var src = new FileStream(inputWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string tmp = outputWavPath + ".tmp";
            using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            {
                // Write canonical 44-byte WAV header
                Span<byte> hdr = stackalloc byte[44];
                hdr[0] = (byte)'R'; hdr[1] = (byte)'I'; hdr[2] = (byte)'F'; hdr[3] = (byte)'F';
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(4, 4), (uint)(info.DataBytes + 36));
                hdr[8] = (byte)'W'; hdr[9] = (byte)'A'; hdr[10] = (byte)'V'; hdr[11] = (byte)'E';
                hdr[12] = (byte)'f'; hdr[13] = (byte)'m'; hdr[14] = (byte)'t'; hdr[15] = (byte)' ';
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(16, 4), 16);
                BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(20, 2), 1);
                BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(22, 2), info.Channels);
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(24, 4), info.SampleRate);
                BinaryPrimitives.WriteInt32LittleEndian(hdr.Slice(28, 4), info.SampleRate * info.Channels * 2);
                BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(32, 2), (ushort)(info.Channels * 2));
                BinaryPrimitives.WriteUInt16LittleEndian(hdr.Slice(34, 2), 16);
                hdr[36] = (byte)'d'; hdr[37] = (byte)'a'; hdr[38] = (byte)'t'; hdr[39] = (byte)'a';
                BinaryPrimitives.WriteUInt32LittleEndian(hdr.Slice(40, 4), (uint)info.DataBytes);
                dst.Write(hdr);

                src.Seek(info.DataOffset, SeekOrigin.Begin);
                byte[] chunk = new byte[65536 - (65536 % Math.Max(2, info.Channels * 2))];
                long rem = info.DataBytes;
                while (rem > 0)
                {
                    int want = (int)Math.Min(chunk.Length, rem);
                    int got = src.Read(chunk, 0, want);
                    if (got <= 0) break;
                    int aligned = got - (got % (info.Channels * 2));
                    if (aligned > 0)
                    {
                        var shorts = MemoryMarshal.Cast<byte, short>(chunk.AsSpan(0, aligned));
                        dsp.ProcessPcm16(shorts);
                        dst.Write(chunk, 0, aligned);
                    }
                    rem -= got;
                }
            }
            if (File.Exists(outputWavPath)) File.Delete(outputWavPath);
            File.Move(tmp, outputWavPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Direct-form I 2nd-order biquad filter (1:1 <c>VoiceTap.swift:145-164</c>).</summary>
    public struct Biquad
    {
        public float B0 = 1, B1, B2, A1, A2;
        private float _x1, _x2, _y1, _y2;

        public Biquad() { }

        public Biquad(float b0, float b1, float b2, float a1, float a2)
        {
            B0 = b0; B1 = b1; B2 = b2; A1 = a1; A2 = a2;
        }

        public float Run(float x)
        {
            float y = B0 * x + B1 * _x1 + B2 * _x2 - A1 * _y1 - A2 * _y2;
            _x2 = _x1; _x1 = x; _y2 = _y1; _y1 = y;
            return y;
        }

        public static Biquad HighPass(double freq, double rate)
        {
            double w = 2.0 * Math.PI * freq / rate;
            double alpha = Math.Sin(w) / (2.0 * 0.707);
            double c = Math.Cos(w);
            double a0 = 1.0 + alpha;
            return new Biquad(
                (float)((1.0 + c) / 2.0 / a0),
                (float)(-(1.0 + c) / a0),
                (float)((1.0 + c) / 2.0 / a0),
                (float)(-2.0 * c / a0),
                (float)((1.0 - alpha) / a0));
        }

        public static Biquad Peak(double freq, double gainDb, double q, double rate)
        {
            double a = Math.Pow(10.0, gainDb / 40.0);
            double w = 2.0 * Math.PI * freq / rate;
            double alpha = Math.Sin(w) / (2.0 * q);
            double c = Math.Cos(w);
            double a0 = 1.0 + alpha / a;
            return new Biquad(
                (float)((1.0 + alpha * a) / a0),
                (float)(-2.0 * c / a0),
                (float)((1.0 - alpha * a) / a0),
                (float)(-2.0 * c / a0),
                (float)((1.0 - alpha / a) / a0));
        }
    }
}
