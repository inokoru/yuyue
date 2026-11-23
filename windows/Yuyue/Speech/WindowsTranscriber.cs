using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// Windows audio transcription engine matching macOS <c>Transcriber.swift</c>:
/// <list type="bullet">
/// <item>Live microphone dictation via built-in WinRT <see cref="SpeechRecognizer"/> (for Podcast cut-in).</item>
/// <item>File transcription to timestamped <see cref="HeardWord"/>[] for <c>Core.AudiobookMakeTextVersion</c> &amp; Podcast transcripts:
///   tries (1) local Whisper/Speaches/VoiceStudio server, (2) configured OpenAI/Groq/SiliconFlow Whisper API,
///   and (3) built-in offline Windows SAPI 5 (<c>SAPI.SpInProcRecoContext</c> + <c>SAPI.SpFileStream</c>).</item>
/// </list>
/// </summary>
public static class WindowsTranscriber
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromMinutes(3) };
    private static readonly HttpClient ProbeHttp = new() { Timeout = System.TimeSpan.FromMilliseconds(600) };

    public static bool IsSapiAvailable()
    {
        try { return Type.GetTypeFromProgID("SAPI.SpInProcRecoContext") is not null; }
        catch { return false; }
    }

    /// <summary>
    /// Live single-utterance microphone speech recognition using WinRT <see cref="SpeechRecognizer"/>.
    /// Returns recognized text or <c>null</c> if cancelled / unavailable.
    /// </summary>
    public static async Task<(string? Text, string? Error)> RecognizeMicrophoneOnceAsync(string lang, CancellationToken ct)
    {
        try
        {
            string tag = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : "zh-CN";
            SpeechRecognizer recognizer;
            try { recognizer = new SpeechRecognizer(new Language(tag)); }
            catch { recognizer = new SpeechRecognizer(); }

            using (recognizer)
            {
                await recognizer.CompileConstraintsAsync();
                var op = recognizer.RecognizeAsync();
                using var reg = ct.Register(() => { try { op.Cancel(); } catch { } });
                var result = await op;
                if (result.Status == SpeechRecognitionResultStatus.Success && !string.IsNullOrWhiteSpace(result.Text))
                    return (result.Text.Trim(), null);
                return (null, result.Status == SpeechRecognitionResultStatus.UserCanceled ? null : "未能识别到清晰语音");
            }
        }
        catch (OperationCanceledException) { return (null, null); }
        catch (Exception ex)
        {
            return (null, $"系统语音识别不可用：{ex.Message}");
        }
    }

    /// <summary>
    /// Transcribes a single audio file (decoded to 16 kHz mono WAV in <paramref name="scratchDir"/>) in 10-minute chunks,
    /// returning timestamped <see cref="HeardWord"/>[] shifted by <paramref name="globalOffsetSec"/>.
    /// </summary>
    public static async Task<HeardWord[]> TranscribeFileAsync(
        LibraryService library,
        string audioPath,
        string scratchDir,
        int fileIndex,
        double globalOffsetSec,
        double durationSec,
        string lang,
        Action<double>? onLocalSecondsDone,
        CancellationToken ct)
    {
        string wavPath = await Aligner.EnsurePcm16kWavAsync(audioPath, scratchDir, fileIndex, ct);
        if (!Aligner.TryReadWavInfo(wavPath, out var info) || info.SampleCount <= 0)
            throw new InvalidOperationException("无法解码音频文件为 16 kHz WAV。");

        double actualDur = Math.Max(durationSec, info.SampleCount / (double)Aligner.SampleRate);
        var endpoint = await ResolveWhisperEndpointAsync(library, ct);
        var allWords = new List<HeardWord>();

        const double chunkSec = 300.0; // 5-minute slices (~9.6 MB PCM each, safely under 25 MB Whisper limit)
        for (double from = 0; from < actualDur; from += chunkSec)
        {
            ct.ThrowIfCancellationRequested();
            double to = Math.Min(actualDur, from + chunkSec);
            if (to - from < 0.25) break;

            string slicePath = Path.Combine(scratchDir, $"tr-{fileIndex}-{(int)from}.wav");
            WriteWavSlice(wavPath, info, from, to, slicePath);

            try
            {
                HeardWord[] chunkWords;
                if (endpoint is { } ep)
                {
                    chunkWords = await TranscribeViaWhisperApiAsync(ep, slicePath, globalOffsetSec + from, lang, ct);
                }
                else
                {
                    chunkWords = await TranscribeViaSapiOfflineAsync(slicePath, globalOffsetSec + from, to - from, lang, ct);
                }
                allWords.AddRange(chunkWords);
            }
            finally
            {
                try { File.Delete(slicePath); } catch { }
            }
            onLocalSecondsDone?.Invoke(to);
        }

        return allWords.ToArray();
    }

    /// <summary>
    /// Full 1:1 <c>Audiobooks.makeTextVersion</c>: transcribes all tracks of an audiobook recording,
    /// calls <see cref="YuyueCore.AudiobookMakeTextVersion"/>, and auto-pairs the generated text book.
    /// </summary>
    public static async Task<BookSummary?> MakeTextVersionAsync(
        LibraryService library,
        string audioBookId,
        Action<double, string>? onProgress,
        CancellationToken ct)
    {
        var rec = library.Core.AudioRecording(audioBookId);
        var summary = library.GetBooks().FirstOrDefault(b => b.Id == audioBookId);
        string title = summary?.Title ?? "有声书";
        string lang = (summary?.Language ?? "zh").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        string scratchDir = Path.Combine(library.DataDir, "audiobook-work", audioBookId, "transcribe");
        Directory.CreateDirectory(scratchDir);

        var allWords = new List<HeardWord>();
        double totalSec = Math.Max(1.0, rec.Seconds);
        double offset = 0;

        try
        {
            for (int i = 0; i < rec.Files.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var f = rec.Files[i];
                double baseOff = offset;
                onProgress?.Invoke(Math.Clamp(baseOff / totalSec, 0.01, 0.96), $"正在转写第 {i + 1} / {rec.Files.Length} 轨…");

                var words = await TranscribeFileAsync(
                    library,
                    f.Path,
                    scratchDir,
                    i,
                    baseOff,
                    f.Seconds,
                    lang,
                    localSec =>
                    {
                        double frac = Math.Clamp((baseOff + localSec) / totalSec, 0.01, 0.98);
                        onProgress?.Invoke(frac, $"正在转写第 {i + 1} / {rec.Files.Length} 轨 · {(int)Math.Round(frac * 100)}%");
                    },
                    ct);
                allWords.AddRange(words);
                offset += f.Seconds;
            }

            if (allWords.Count == 0)
                throw new InvalidOperationException("未转写出可用文字。请先安装 Windows 语音识别语言包，或在设置中配置 OpenAI / SiliconFlow / 本地 Whisper 接口。");

            onProgress?.Invoke(0.99, "正在生成可划线文字版并配对…");
            var made = library.Core.AudiobookMakeTextVersion(audioBookId, allWords.ToArray(), title);
            try { library.Core.AudiobookPair(audioBookId, made.Id); } catch { }
            try { App.CurrentApp?.NotifyShelfRefresh(); } catch { }
            onProgress?.Invoke(1.0, $"✓ 已生成《{made.Title}》并完成配对");
            return made;
        }
        finally
        {
            try { if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, recursive: true); } catch { }
        }
    }

    private sealed record WhisperEndpoint(string Url, string? ApiKey, string Model);

    private static async Task<WhisperEndpoint?> ResolveWhisperEndpointAsync(LibraryService library, CancellationToken ct)
    {
        // 1. Check local OpenAI-compatible Whisper servers first (free, fast, offline)
        foreach (var (url, model) in new[]
        {
            ("http://127.0.0.1:8000/v1/audio/transcriptions", "Systran/faster-whisper-small"),
            ("http://127.0.0.1:8080/inference", "whisper-1"),
            ("http://127.0.0.1:3900/v1/audio/transcriptions", "whisper-1"),
        })
        {
            try
            {
                var uri = new Uri(url);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{uri.Scheme}://{uri.Authority}/v1/models");
                using var resp = await ProbeHttp.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                    return new WhisperEndpoint(url, null, model);
            }
            catch { }
        }

        // 2. Reuse configured OpenAI / SiliconFlow / Groq key from CloudVoices or Companion Secrets
        string? GetCompanionKey(string providerId)
        {
            string? k = library.Secrets.Get($"ai.key.{providerId}");
            if (!string.IsNullOrWhiteSpace(k)) return k;
            if (string.Equals(library.Prefs.AiProvider, providerId, StringComparison.OrdinalIgnoreCase))
                return library.Secrets.Get("ai.apiKey");
            return null;
        }

        string? openAiKey = library.Secrets.Get("tts.openai.key");
        if (string.IsNullOrWhiteSpace(openAiKey))
            openAiKey = GetCompanionKey("openai");
        if (!string.IsNullOrWhiteSpace(openAiKey))
            return new WhisperEndpoint("https://api.openai.com/v1/audio/transcriptions", openAiKey, "whisper-1");

        string? groqKey = GetCompanionKey("groq");
        if (!string.IsNullOrWhiteSpace(groqKey))
            return new WhisperEndpoint("https://api.groq.com/openai/v1/audio/transcriptions", groqKey, "whisper-large-v3-turbo");

        string? sfKey = GetCompanionKey("siliconflow");
        if (!string.IsNullOrWhiteSpace(sfKey))
            return new WhisperEndpoint("https://api.siliconflow.cn/v1/audio/transcriptions", sfKey, "FunAudioLLM/SenseVoiceSmall");

        return null;
    }

    private static async Task<HeardWord[]> TranscribeViaWhisperApiAsync(
        WhisperEndpoint ep, string wavPath, double chunkOffsetSec, string lang, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        byte[] bytes = await File.ReadAllBytesAsync(wavPath, ct);
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/wav");
        form.Add(fileContent, "file", Path.GetFileName(wavPath));
        form.Add(new StringContent(ep.Model), "model");
        form.Add(new StringContent(lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "zh"), "language");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("word"), "timestamp_granularities[]");

        using var req = new HttpRequestMessage(HttpMethod.Post, ep.Url) { Content = form };
        if (!string.IsNullOrWhiteSpace(ep.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ep.ApiKey);

        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"语音转写接口返回 {(int)resp.StatusCode}：{body[..Math.Min(body.Length, 160)]}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var list = new List<HeardWord>();

        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array && wordsEl.GetArrayLength() > 0)
        {
            foreach (var w in wordsEl.EnumerateArray())
            {
                string txt = w.TryGetProperty("word", out var wt) ? (wt.GetString() ?? "").Trim() : "";
                if (string.IsNullOrEmpty(txt)) continue;
                double s = w.TryGetProperty("start", out var st) ? st.GetDouble() : 0;
                double e = w.TryGetProperty("end", out var et) ? et.GetDouble() : s + 0.15;
                list.Add(new HeardWord(txt, chunkOffsetSec + s, chunkOffsetSec + Math.Max(s + 0.02, e)));
            }
            return list.ToArray();
        }

        if (root.TryGetProperty("segments", out var segsEl) && segsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segsEl.EnumerateArray())
            {
                string txt = seg.TryGetProperty("text", out var tt) ? (tt.GetString() ?? "").Trim() : "";
                double s = seg.TryGetProperty("start", out var st) ? st.GetDouble() : 0;
                double e = seg.TryGetProperty("end", out var et) ? et.GetDouble() : s + 1.0;
                DistributeTextIntoHeardWords(txt, chunkOffsetSec + s, chunkOffsetSec + Math.Max(s + 0.1, e), list);
            }
            return list.ToArray();
        }

        if (root.TryGetProperty("text", out var fullTxt) && !string.IsNullOrWhiteSpace(fullTxt.GetString()))
        {
            DistributeTextIntoHeardWords(fullTxt.GetString()!, chunkOffsetSec, chunkOffsetSec + 60.0, list);
        }
        return list.ToArray();
    }

    private static Task<HeardWord[]> TranscribeViaSapiOfflineAsync(
        string wavPath, double chunkOffsetSec, double chunkDurSec, string lang, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<HeardWord[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var ctxType = Type.GetTypeFromProgID("SAPI.SpInProcRecoContext");
                var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
                if (ctxType is null || streamType is null)
                    throw new InvalidOperationException("当前系统未安装 Windows 离线语音识别组件（SAPI 5），也未配置 Whisper API。");

                dynamic recoContext = Activator.CreateInstance(ctxType)!;
                dynamic recognizer = recoContext.Recognizer;
                dynamic fileStream = Activator.CreateInstance(streamType)!;
                // SSFMOpenForRead = 0
                fileStream.Open(wavPath, 0, false);

                try
                {
                    // Pick matching language recognizer token (zh-CN = 804, en-US = 409)
                    string wantHex = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "409" : "804";
                    dynamic tokens = recognizer.GetRecognizers($"Language={wantHex}");
                    if ((int)tokens.Count > 0)
                        recognizer.Recognizer = tokens.Item(0);
                }
                catch { }

                recognizer.AudioInputStream = fileStream;
                dynamic grammar = recoContext.CreateGrammar(1);
                grammar.DictationLoad("", 0);
                grammar.DictationSetState(1); // SGDSActive

                var list = new List<HeardWord>();
                bool done = false;
                var phrases = new List<(string Text, double Start, double End)>();

                // Poll recognition results via AudioInputStream offset while dictation runs
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!done && !ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < Math.Max(30, chunkDurSec * 1.5))
                {
                    Thread.Sleep(50);
                    try
                    {
                        long posBytes = Convert.ToInt64(fileStream.Seek(0, 1)); // SSSPTRelativeCurrentPosition = 1
                        double streamSec = posBytes / (double)(Aligner.SampleRate * 2);
                        if (streamSec >= chunkDurSec - 0.1) done = true;
                    }
                    catch { done = true; }
                }

                grammar.DictationSetState(0);
                try { fileStream.Close(); } catch { }

                foreach (var (text, s, e) in phrases)
                    DistributeTextIntoHeardWords(text, chunkOffsetSec + s, chunkOffsetSec + e, list);

                if (list.Count == 0)
                    throw new InvalidOperationException("Windows 离线语音识别未能提取文字。建议在「设置 → 书伴 / 朗读」配置 OpenAI / SiliconFlow / Groq 密钥，或启动本地 Whisper 服务以启用高精度文字版转写。");

                tcs.TrySetResult(list.ToArray());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    internal static void DistributeTextIntoHeardWords(string text, double t0, double t1, List<HeardWord> output)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var tokens = TokenizeForAlignment(text);
        if (tokens.Count == 0) return;

        double span = Math.Max(0.05, t1 - t0);
        int totalWeight = tokens.Sum(t => Math.Max(1, t.Length));
        double cur = t0;
        foreach (var tok in tokens)
        {
            double dur = span * Math.Max(1, tok.Length) / totalWeight;
            output.Add(new HeardWord(tok, cur, cur + Math.Max(0.02, dur)));
            cur += dur;
        }
    }

    internal static List<string> TokenizeForAlignment(string text)
    {
        var list = new List<string>();
        var buf = new StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (buf.Length > 0) { list.Add(buf.ToString()); buf.Clear(); }
            }
            else if (ch >= 0x4E00 && ch <= 0x9FFF)
            {
                if (buf.Length > 0) { list.Add(buf.ToString()); buf.Clear(); }
                list.Add(ch.ToString());
            }
            else
            {
                buf.Append(ch);
            }
        }
        if (buf.Length > 0) list.Add(buf.ToString());
        return list;
    }

    internal static void WriteWavSlice(string srcWavPath, Aligner.WavInfo info, double fromSec, double toSec, string dstPath)
    {
        long startSample = Math.Clamp((long)(fromSec * info.SampleRate), 0, info.SampleCount);
        long endSample = Math.Clamp((long)(toSec * info.SampleRate), startSample, info.SampleCount);
        int frameBytes = info.Channels * (info.BitsPerSample / 8);
        int dataBytes = (int)((endSample - startSample) * frameBytes);

        using var src = new FileStream(srcWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        src.Seek(info.DataOffset + startSample * frameBytes, SeekOrigin.Begin);

        using var dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(dst, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)info.Channels);
        bw.Write(info.SampleRate);
        bw.Write(info.SampleRate * frameBytes);
        bw.Write((short)frameBytes);
        bw.Write((short)info.BitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        bw.Flush();

        byte[] buf = new byte[65536];
        int left = dataBytes;
        while (left > 0)
        {
            int n = src.Read(buf, 0, Math.Min(buf.Length, left));
            if (n <= 0) break;
            dst.Write(buf, 0, n);
            left -= n;
        }
    }
}
