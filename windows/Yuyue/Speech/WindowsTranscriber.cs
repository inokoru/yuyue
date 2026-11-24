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
    /// Transcribes a short or full audio file into plain text (used by Voice Clone ASR auto-fill and quality verification).
    /// Returns <c>null</c> if transcription is unavailable or fails, without throwing.
    /// </summary>
    public static async Task<string?> TranscribeAsync(
        LibraryService library,
        string audioPath,
        string lang = "zh",
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return null;

        string scratchDir = Path.Combine(Path.GetTempPath(), $"yuyue-asr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratchDir);
        try
        {
            var words = await TranscribeFileAsync(library, audioPath, scratchDir, 0, 0.0, 0.0, lang, null, ct);
            if (words.Length == 0) return null;
            string joined = JoinHeardWords(words, lang);
            return string.IsNullOrWhiteSpace(joined) ? null : joined;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            try { if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, recursive: true); } catch { }
        }
    }

    private static string JoinHeardWords(HeardWord[] words, string lang)
    {
        var sb = new StringBuilder();
        bool preferSpaces = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        foreach (var w in words)
        {
            string t = (w.Text ?? "").Trim();
            if (string.IsNullOrEmpty(t)) continue;
            if (sb.Length > 0)
            {
                char prev = sb[^1];
                char next = t[0];
                bool prevCjk = IsCjkOrFullwidthPunct(prev);
                bool nextCjk = IsCjkOrFullwidthPunct(next);
                if (preferSpaces || (!prevCjk && !nextCjk && char.IsLetterOrDigit(prev) && char.IsLetterOrDigit(next)))
                    sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString().Trim();
    }

    private static bool IsCjkOrFullwidthPunct(char ch) =>
        (ch >= '\u4E00' && ch <= '\u9FFF') ||
        (ch >= '\u3400' && ch <= '\u4DBF') ||
        (ch >= '\u3000' && ch <= '\u303F') ||
        (ch >= '\uFF00' && ch <= '\uFFEF');

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SpEventNative
    {
        public ushort EventId;
        public ushort ParamType;
        public uint StreamNum;
        public ulong AudioStreamOffset;
        public nint WParam;
        public nint LParam;
    }

    private static readonly Guid IidSpEventSource = new("BE7A9CCE-5F9E-11D2-960F-00C04F8EE628");
    private static readonly Guid IidSpPhrase = new("1A5C0354-B621-4B5A-8791-D306ED379E53");

    private const ushort SpeiEndSrStream = 34;
    private const ushort SpeiRecognition = 38;
    private const ushort SpeiHypothesis = 39;
    private const ulong SpfeiFlagCheck = (1UL << 30) | (1UL << 33);
    private const ulong SpfeiAllSrEvents = 0x003FFFFC00000000UL | SpfeiFlagCheck;

    private static Task<HeardWord[]> TranscribeViaSapiOfflineAsync(
        string wavPath, double chunkOffsetSec, double chunkDurSec, string lang, CancellationToken ct)
    {
        var utterances = SegmentSpeechUtterances(wavPath, chunkDurSec);
        var tcs = new TaskCompletionSource<HeardWord[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            IntPtr pEventSource = IntPtr.Zero;
            IntPtr pUnkContext = IntPtr.Zero;
            string uttTempFile = Path.Combine(Path.GetTempPath(), $"yuyue-sapi-utt-{Guid.NewGuid():N}.wav");
            try
            {
                var ctxType = Type.GetTypeFromProgID("SAPI.SpInProcRecoContext");
                var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream");
                if (ctxType is null || streamType is null)
                    throw new InvalidOperationException("当前系统未安装 Windows 离线语音识别组件（SAPI 5），也未配置 Whisper API。");

                dynamic recoContext = Activator.CreateInstance(ctxType)!;
                dynamic recognizer = recoContext.Recognizer;
                try
                {
                    // Pick matching language recognizer token (zh-CN = 804, en-US = 409)
                    string wantHex = lang.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "409" : "804";
                    dynamic tokens = recognizer.GetRecognizers($"Language={wantHex}");
                    if ((int)tokens.Count > 0)
                        recognizer.Recognizer = tokens.Item(0);
                }
                catch { }

                pUnkContext = Marshal.GetIUnknownForObject(recoContext);
                Guid iidEv = IidSpEventSource;
                int qiHr = Marshal.QueryInterface(pUnkContext, ref iidEv, out pEventSource);
                if (qiHr < 0 || pEventSource == IntPtr.Zero)
                    throw new InvalidOperationException("无法绑定 SAPI 5 离线事件源（ISpEventSource）。");

                ConfigureSpEventSource(pEventSource);

                dynamic grammar = recoContext.CreateGrammar(1);
                grammar.DictationLoad("", 0);

                var phrases = new List<(string Text, double Start, double End)>();
                const double headPadSec = 0.20;
                const double tailPadSec = 0.45;

                foreach (var utt in utterances)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!WritePaddedUtteranceWav(wavPath, utt.StartSec, utt.EndSec, headPadSec, tailPadSec, uttTempFile))
                        continue;

                    DrainSpEvents(pEventSource, null, out _);

                    dynamic fileStream = Activator.CreateInstance(streamType)!;
                    try
                    {
                        // SSFMOpenForRead = 0
                        fileStream.Open(uttTempFile, 0, false);
                        recognizer.AudioInputStream = fileStream;
                        grammar.DictationSetState(1); // SGDSActive

                        var uttPieces = new List<(string Text, double RelEndSec)>();
                        string? lastHypothesis = null;
                        bool streamEnded = false;
                        double maxWaitSec = Math.Max(8.0, (utt.EndSec - utt.StartSec + headPadSec + tailPadSec) * 2.5);
                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        while (!streamEnded && !ct.IsCancellationRequested && sw.Elapsed.TotalSeconds < maxWaitSec)
                        {
                            WaitSpEvent(pEventSource, 80);
                            streamEnded = DrainSpEvents(pEventSource, uttPieces, out string? hyp);
                            if (!string.IsNullOrWhiteSpace(hyp))
                                lastHypothesis = hyp;
                        }

                        // Drain once more after stream completion to capture final committed recognition
                        _ = DrainSpEvents(pEventSource, uttPieces, out string? tailHyp);
                        if (!string.IsNullOrWhiteSpace(tailHyp))
                            lastHypothesis = tailHyp;

                        try { grammar.DictationSetState(0); } catch { }

                        if (uttPieces.Count > 0)
                        {
                            double cursor = utt.StartSec;
                            for (int i = 0; i < uttPieces.Count; i++)
                            {
                                var (pieceText, relEnd) = uttPieces[i];
                                double mappedEnd = relEnd > headPadSec
                                    ? Math.Clamp(utt.StartSec + (relEnd - headPadSec), cursor + 0.08, utt.EndSec)
                                    : utt.StartSec + (utt.EndSec - utt.StartSec) * ((i + 1.0) / uttPieces.Count);
                                if (i == uttPieces.Count - 1)
                                    mappedEnd = Math.Max(mappedEnd, utt.EndSec);
                                phrases.Add((pieceText, cursor, Math.Max(cursor + 0.08, mappedEnd)));
                                cursor = Math.Min(utt.EndSec - 0.02, mappedEnd);
                            }
                        }
                        else if (!string.IsNullOrWhiteSpace(lastHypothesis))
                        {
                            phrases.Add((lastHypothesis.Trim(), utt.StartSec, Math.Max(utt.StartSec + 0.10, utt.EndSec)));
                        }
                    }
                    finally
                    {
                        try { fileStream.Close(); } catch { }
                        try { Marshal.FinalReleaseComObject(fileStream); } catch { }
                    }
                }

                var list = new List<HeardWord>();
                foreach (var (text, s, e) in phrases)
                    DistributeTextIntoHeardWords(text, chunkOffsetSec + s, chunkOffsetSec + e, list);

                if (list.Count == 0)
                    throw new InvalidOperationException("Windows 离线语音识别未能提取文字。建议在「设置 → 书伴 / 朗读」配置 OpenAI / SiliconFlow / Groq 密钥，或启动本地 Whisper 服务以启用高精度文字版转写。");

                tcs.TrySetResult(list.ToArray());
            }
            catch (OperationCanceledException ex)
            {
                tcs.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
            finally
            {
                try { if (File.Exists(uttTempFile)) File.Delete(uttTempFile); } catch { }
                if (pEventSource != IntPtr.Zero) Marshal.Release(pEventSource);
                if (pUnkContext != IntPtr.Zero) Marshal.Release(pUnkContext);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private static unsafe void ConfigureSpEventSource(IntPtr pEventSource)
    {
        nint* vtbl = *(nint**)pEventSource;
        // ISpNotifySource::SetNotifyWin32Event is slot 7
        var setNotifyEvent = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[7];
        int hr = setNotifyEvent(pEventSource);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

        // ISpEventSource::SetInterest is slot 10
        var setInterest = (delegate* unmanaged[Stdcall]<IntPtr, ulong, ulong, int>)vtbl[10];
        hr = setInterest(pEventSource, SpfeiAllSrEvents, SpfeiAllSrEvents);
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static unsafe void WaitSpEvent(IntPtr pEventSource, uint timeoutMs)
    {
        nint* vtbl = *(nint**)pEventSource;
        // ISpNotifySource::WaitForNotifyEvent is slot 8
        var waitFn = (delegate* unmanaged[Stdcall]<IntPtr, uint, int>)vtbl[8];
        _ = waitFn(pEventSource, timeoutMs);
    }

    private static unsafe bool DrainSpEvents(
        IntPtr pEventSource,
        List<(string Text, double RelEndSec)>? recognizedPieces,
        out string? latestHypothesis)
    {
        latestHypothesis = null;
        bool streamEnded = false;
        nint* vtbl = *(nint**)pEventSource;
        // ISpEventSource::GetEvents is slot 11
        var getEvents = (delegate* unmanaged[Stdcall]<IntPtr, uint, SpEventNative*, uint*, int>)vtbl[11];

        SpEventNative ev = default;
        while (true)
        {
            uint fetched = 0;
            int hr = getEvents(pEventSource, 1, &ev, &fetched);
            if (hr < 0 || fetched == 0) break;

            try
            {
                if (ev.EventId == SpeiEndSrStream)
                {
                    streamEnded = true;
                }
                else if (ev.EventId == SpeiRecognition && ev.LParam != 0 && recognizedPieces is not null)
                {
                    string? txt = ExtractPhraseText(ev.LParam);
                    if (!string.IsNullOrWhiteSpace(txt))
                    {
                        double relEndSec = ev.AudioStreamOffset / (double)(Aligner.SampleRate * 2);
                        recognizedPieces.Add((txt.Trim(), relEndSec));
                    }
                }
                else if (ev.EventId == SpeiHypothesis && ev.LParam != 0)
                {
                    string? hyp = ExtractPhraseText(ev.LParam);
                    if (!string.IsNullOrWhiteSpace(hyp))
                        latestHypothesis = hyp.Trim();
                }
            }
            finally
            {
                ClearSpEvent(ref ev);
                ev = default;
            }
        }
        return streamEnded;
    }

    private static unsafe string? ExtractPhraseText(nint pRecoResult)
    {
        if (pRecoResult == 0) return null;
        Guid iidPhrase = IidSpPhrase;
        if (Marshal.QueryInterface(pRecoResult, ref iidPhrase, out IntPtr pPhrase) >= 0 && pPhrase != IntPtr.Zero)
        {
            try
            {
                nint* vtbl = *(nint**)pPhrase;
                // ISpPhrase::GetText(ULONG ulStart, ULONG ulCount, BOOL fUseTextReplacements, LPWSTR* ppszCoMemText, BYTE* pbDisplayAttributes) is slot 5
                var getText = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, IntPtr*, IntPtr, int>)vtbl[5];
                IntPtr pText = IntPtr.Zero;
                int hr = getText(pPhrase, 0xFFFF_FFFFu, 0xFFFF_FFFFu, 1, &pText, IntPtr.Zero);
                if (hr >= 0 && pText != IntPtr.Zero)
                {
                    try { return Marshal.PtrToStringUni(pText); }
                    finally { Marshal.FreeCoTaskMem(pText); }
                }
            }
            finally
            {
                Marshal.Release(pPhrase);
            }
        }

        try
        {
            dynamic obj = Marshal.GetObjectForIUnknown(pRecoResult);
            return (string?)obj.PhraseInfo.GetText(0, -1, true);
        }
        catch
        {
            return null;
        }
    }

    private static void ClearSpEvent(ref SpEventNative ev)
    {
        if (ev.LParam == 0) return;
        if (ev.ParamType is 1 or 2)
        {
            try { Marshal.Release(ev.LParam); } catch { }
        }
        else if (ev.ParamType is 3 or 4)
        {
            try { Marshal.FreeCoTaskMem(ev.LParam); } catch { }
        }
        ev.LParam = 0;
    }

    /// <summary>
    /// Segments a 16 kHz mono PCM WAV into speech utterances of at most 12 seconds by energy/silence VAD.
    /// </summary>
    private static List<(double StartSec, double EndSec)> SegmentSpeechUtterances(string wavPath, double fallbackDurSec)
    {
        var result = new List<(double StartSec, double EndSec)>();
        if (!Aligner.TryReadWavInfo(wavPath, out var info) || info.SampleCount <= 0 || info.BitsPerSample != 16)
        {
            for (double t = 0; t < Math.Max(0.3, fallbackDurSec); t += 10.0)
                result.Add((t, Math.Min(Math.Max(0.3, fallbackDurSec), t + 10.0)));
            return result;
        }

        double totalSec = info.SampleCount / (double)info.SampleRate;
        int frameSamples = Math.Max(160, info.SampleRate * 30 / 1000); // 30 ms frames
        int channels = Math.Max(1, (int)info.Channels);
        int totalFrames = (int)((info.SampleCount + frameSamples - 1) / frameSamples);
        if (totalFrames <= 0)
        {
            result.Add((0, Math.Max(0.3, totalSec)));
            return result;
        }

        var rms = new float[totalFrames];
        using (var fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            fs.Seek(info.DataOffset, SeekOrigin.Begin);
            byte[] buf = new byte[frameSamples * channels * 2];
            for (int f = 0; f < totalFrames; f++)
            {
                int read = fs.Read(buf, 0, buf.Length);
                if (read < 2) break;
                int samplesRead = read / (channels * 2);
                if (samplesRead <= 0) break;
                double sumSq = 0;
                for (int i = 0; i < samplesRead; i++)
                {
                    short s = BitConverter.ToInt16(buf, i * channels * 2);
                    double v = s / 32768.0;
                    sumSq += v * v;
                }
                rms[f] = (float)Math.Sqrt(sumSq / samplesRead);
            }
        }

        float[] sorted = (float[])rms.Clone();
        Array.Sort(sorted);
        float noiseFloor = sorted[(int)(sorted.Length * 0.20)];
        float peak90 = sorted[(int)(sorted.Length * 0.90)];
        float threshold = Math.Clamp(noiseFloor * 2.5f + (peak90 - noiseFloor) * 0.10f, 0.0035f, 0.025f);

        const int bridgeSilenceFrames = 12; // 360 ms pause bridge
        const int maxUttFrames = 380;       // ~11.4 s max per utterance
        const int minSplitFrames = 220;     // ~6.6 s min before looking for silence valley

        int iFrame = 0;
        while (iFrame < totalFrames)
        {
            if (rms[iFrame] < threshold)
            {
                iFrame++;
                continue;
            }

            int startF = iFrame;
            int lastSpeechF = iFrame;
            int f = iFrame + 1;
            while (f < totalFrames)
            {
                if (rms[f] >= threshold)
                {
                    lastSpeechF = f;
                }
                else if (f - lastSpeechF >= bridgeSilenceFrames)
                {
                    break;
                }

                if (f - startF >= maxUttFrames)
                {
                    // Split at quietest frame in [startF + minSplitFrames .. f]
                    int bestSplit = f;
                    float minE = float.MaxValue;
                    for (int k = startF + minSplitFrames; k <= f; k++)
                    {
                        if (rms[k] <= minE)
                        {
                            minE = rms[k];
                            bestSplit = k;
                        }
                    }
                    lastSpeechF = bestSplit;
                    f = bestSplit + 1;
                    break;
                }
                f++;
            }

            double s0 = Math.Max(0.0, (startF * frameSamples) / (double)info.SampleRate - 0.12);
            double s1 = Math.Min(totalSec, ((lastSpeechF + 1) * frameSamples) / (double)info.SampleRate + 0.12);
            if (s1 - s0 >= 0.22)
            {
                if (result.Count > 0 && s0 <= result[^1].EndSec + 0.08 && (s1 - result[^1].StartSec) <= 12.0)
                    result[^1] = (result[^1].StartSec, Math.Max(result[^1].EndSec, s1));
                else
                    result.Add((s0, s1));
            }

            iFrame = Math.Max(f, lastSpeechF + 1);
        }

        if (result.Count == 0)
        {
            for (double t = 0; t < totalSec; t += 10.0)
                result.Add((t, Math.Min(totalSec, t + 10.0)));
        }

        return result;
    }

    private static bool WritePaddedUtteranceWav(
        string srcWavPath,
        double fromSec,
        double toSec,
        double headSilenceSec,
        double tailSilenceSec,
        string dstPath)
    {
        if (!Aligner.TryReadWavInfo(srcWavPath, out var info) || info.SampleCount <= 0)
            return false;

        long startSample = Math.Clamp((long)(fromSec * info.SampleRate), 0, info.SampleCount);
        long endSample = Math.Clamp((long)(toSec * info.SampleRate), startSample, info.SampleCount);
        if (endSample <= startSample) return false;

        int frameBytes = info.Channels * (info.BitsPerSample / 8);
        int headBytes = Math.Max(0, (int)(headSilenceSec * info.SampleRate)) * frameBytes;
        int speechBytes = (int)((endSample - startSample) * frameBytes);
        int tailBytes = Math.Max(0, (int)(tailSilenceSec * info.SampleRate)) * frameBytes;
        int totalDataBytes = headBytes + speechBytes + tailBytes;

        using var src = new FileStream(srcWavPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        src.Seek(info.DataOffset + startSample * frameBytes, SeekOrigin.Begin);

        using var dst = new FileStream(dstPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(dst, Encoding.ASCII, leaveOpen: true);
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + totalDataBytes);
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
        bw.Write(totalDataBytes);
        bw.Flush();

        byte[] zeroBuf = new byte[Math.Min(16384, Math.Max(headBytes, tailBytes))];
        int leftHead = headBytes;
        while (leftHead > 0)
        {
            int n = Math.Min(zeroBuf.Length, leftHead);
            dst.Write(zeroBuf, 0, n);
            leftHead -= n;
        }

        byte[] copyBuf = new byte[65536];
        int leftSpeech = speechBytes;
        while (leftSpeech > 0)
        {
            int n = src.Read(copyBuf, 0, Math.Min(copyBuf.Length, leftSpeech));
            if (n <= 0) break;
            dst.Write(copyBuf, 0, n);
            leftSpeech -= n;
        }

        int leftTail = tailBytes;
        while (leftTail > 0)
        {
            int n = Math.Min(zeroBuf.Length, leftTail);
            dst.Write(zeroBuf, 0, n);
            leftTail -= n;
        }

        return true;
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
