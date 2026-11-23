using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yuyue.Core;

namespace Yuyue.Speech;

public enum CloudTtsProvider
{
    Free,
    Volcengine,
    Fish,
    OpenAi,
    Azure,
    ElevenLabs,
}

public sealed record CloudVoiceEntry(
    CloudTtsProvider Provider,
    string VoiceId,
    string DisplayName,
    string? Language,
    string? Gender,
    string? Age)
{
    public string FullId => $"cloud.{ProviderKey(Provider)}.{VoiceId}";

    public static string ProviderKey(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Free => "free",
        CloudTtsProvider.Volcengine => "volcengine",
        CloudTtsProvider.Fish => "fish",
        CloudTtsProvider.OpenAi => "openai",
        CloudTtsProvider.Azure => "azure",
        _ => "elevenlabs",
    };
}

public sealed record CloudVoiceActing(
    string Instruction = "",
    string Head = "",
    string Tail = "",
    string? Style = null,
    double Degree = 1.0)
{
    public bool IsFlat =>
        string.IsNullOrEmpty(Instruction) &&
        string.IsNullOrEmpty(Head) &&
        string.IsNullOrEmpty(Tail) &&
        string.IsNullOrEmpty(Style);

    public string CacheKeyPart => $"{Instruction}|{Head}|{Tail}|{Style ?? ""}|{Degree:0.0}";
}

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Speech/CloudVoice.swift</c>:
/// OpenAI (<c>gpt-4o-mini-tts</c>), Microsoft Azure Speech (SSML + <c>mstts:express-as</c> + SAPI polyphone pinyin),
/// and ElevenLabs (<c>eleven_multilingual_v2</c> / <c>eleven_v3</c> 24 kHz PCM wrapped in RIFF WAV),
/// with local SHA-256 WAV disk cache and 2-way concurrent sentence prefetching.
/// </summary>
public sealed class CloudVoiceService
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(30) };

    public static readonly (string Id, string Name, string Locale, string Gender, int Sid)[] FreeBuiltinVoices =
    [
        ("zf_001", "晓语 · 温和女声", "zh-CN", "female", 55),
        ("zf_002", "清禾 · 清润女声", "zh-CN", "female", 56),
        ("zm_010", "云泽 · 沉稳男声", "zh-CN", "male", 100),
        ("zm_009", "知行 · 明朗男声", "zh-CN", "male", 99),
        ("af_heart", "Heart · Warm Narrator", "en-US", "female", 3),
        ("af_bella", "Bella · Clear Female", "en-US", "female", 2),
        ("am_michael", "Michael · Steady Male", "en-US", "male", 16),
        ("bf_emma", "Emma · British Narrator", "en-GB", "female", 21),
    ];

    public static readonly (string Id, string Name, string Locale, string Gender)[] VolcengineBuiltinVoices =
    [
        ("zh_female_cancan_mars_bigtts", "灿灿 · 温暖女声", "zh-CN", "female"),
        ("zh_female_shuangkuaisisi_moon_bigtts", "思思 · 爽快自然", "zh-CN", "female"),
        ("zh_male_wennuanahu_moon_bigtts", "阿虎 · 温暖男声", "zh-CN", "male"),
        ("zh_male_shaonianzixin_moon_bigtts", "梓新 · 少年男声", "zh-CN", "male"),
        ("zh_female_wanwanxiaohe_moon_bigtts", "小荷 · 温婉旁白", "zh-CN", "female"),
        ("en_female_anna_mars_bigtts", "Anna · English Narrator", "en-US", "female"),
    ];

    public static readonly (string Id, string Name, string Locale, string Gender)[] FishBuiltinVoices =
    [
        ("default", "OpenAudio S1 · 默认自然旁白", "zh-CN", "female"),
        ("7f92f8afb8ec43bf81429cc1c9199cb1", "丁真 · 社区热榜示例", "zh-CN", "male"),
        ("54a5170264694bfc8e9ad98df7bd89c3", "温柔电台女声", "zh-CN", "female"),
    ];

    public static readonly (string Id, string Gender)[] OpenAiBuiltinVoices =
    [
        ("alloy", "female"),
        ("ash", "male"),
        ("ballad", "male"),
        ("coral", "female"),
        ("echo", "male"),
        ("fable", "male"),
        ("nova", "female"),
        ("onyx", "male"),
        ("sage", "female"),
        ("shimmer", "female"),
    ];

    private static readonly (string Id, string Name, string Locale, string Gender)[] DefaultAzureVoices =
    [
        ("zh-CN-XiaoxiaoNeural", "晓晓 (自然女声)", "zh-CN", "female"),
        ("zh-CN-YunxiNeural", "云希 (清朗男声)", "zh-CN", "male"),
        ("zh-CN-XiaoyiNeural", "晓伊 (温婉女声)", "zh-CN", "female"),
        ("zh-CN-YunjianNeural", "云健 (沉稳旁白)", "zh-CN", "male"),
        ("en-US-JennyNeural", "Jenny (English)", "en-US", "female"),
        ("en-US-GuyNeural", "Guy (English)", "en-US", "male"),
    ];

    private readonly LibraryService _library;

    public CloudVoiceService(LibraryService library)
    {
        _library = library;
    }

    public static bool TryParseFullId(string? id, out CloudTtsProvider provider, out string voiceId)
    {
        provider = CloudTtsProvider.OpenAi;
        voiceId = "";
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("cloud.", StringComparison.OrdinalIgnoreCase))
            return false;

        int secondDot = id.IndexOf('.', 6);
        if (secondDot <= 6 || secondDot >= id.Length - 1) return false;
        string pKey = id[6..secondDot].ToLowerInvariant();
        voiceId = id[(secondDot + 1)..];
        provider = pKey switch
        {
            "free" => CloudTtsProvider.Free,
            "volcengine" => CloudTtsProvider.Volcengine,
            "fish" => CloudTtsProvider.Fish,
            "openai" => CloudTtsProvider.OpenAi,
            "azure" => CloudTtsProvider.Azure,
            "elevenlabs" => CloudTtsProvider.ElevenLabs,
            _ => (CloudTtsProvider)(-1),
        };
        return Enum.IsDefined(typeof(CloudTtsProvider), provider);
    }

    public bool IsConfigured(CloudTtsProvider provider)
    {
        if (provider == CloudTtsProvider.Free)
            return _library.Secrets.Get("tts.free.dismissed") != "1";
        string key = _library.Secrets.Get($"tts.{CloudVoiceEntry.ProviderKey(provider)}.key");
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (provider == CloudTtsProvider.Volcengine)
            return key.Contains('|') || !string.IsNullOrWhiteSpace(_library.Secrets.Get("tts.volcengine.appid"));
        if (provider == CloudTtsProvider.Azure)
            return !string.IsNullOrWhiteSpace(_library.Prefs.CloudVoiceAzureRegion);
        return true;
    }

    public IReadOnlyList<CloudVoiceEntry> GetVoices(CloudTtsProvider provider)
    {
        string pKey = CloudVoiceEntry.ProviderKey(provider);
        if (provider == CloudTtsProvider.Free)
        {
            return FreeBuiltinVoices
                .Select(v => new CloudVoiceEntry(CloudTtsProvider.Free, v.Id, v.Name, v.Locale, v.Gender, null))
                .ToArray();
        }
        if (provider == CloudTtsProvider.Volcengine)
        {
            return VolcengineBuiltinVoices
                .Select(v => new CloudVoiceEntry(CloudTtsProvider.Volcengine, v.Id, v.Name, v.Locale, v.Gender, null))
                .ToArray();
        }
        if (provider == CloudTtsProvider.OpenAi)
        {
            return OpenAiBuiltinVoices
                .Select(v => new CloudVoiceEntry(
                    CloudTtsProvider.OpenAi,
                    v.Id,
                    char.ToUpperInvariant(v.Id[0]) + v.Id[1..],
                    null,
                    v.Gender,
                    null))
                .ToArray();
        }

        if (_library.Prefs.CachedCloudVoices.TryGetValue(pKey, out var rows) && rows.Count > 0)
        {
            return rows
                .Where(r => r.Length >= 2)
                .Select(r => new CloudVoiceEntry(
                    provider,
                    r[0],
                    r[1],
                    r.Length > 2 && !string.IsNullOrEmpty(r[2]) ? r[2] : null,
                    r.Length > 3 && !string.IsNullOrEmpty(r[3]) ? r[3] : null,
                    r.Length > 4 && !string.IsNullOrEmpty(r[4]) ? r[4] : null))
                .ToArray();
        }

        if (provider == CloudTtsProvider.Fish)
        {
            return FishBuiltinVoices
                .Select(v => new CloudVoiceEntry(CloudTtsProvider.Fish, v.Id, v.Name, v.Locale, v.Gender, null))
                .ToArray();
        }

        if (provider == CloudTtsProvider.Azure)
        {
            return DefaultAzureVoices
                .Select(v => new CloudVoiceEntry(CloudTtsProvider.Azure, v.Id, v.Name, v.Locale, v.Gender, null))
                .ToArray();
        }

        return Array.Empty<CloudVoiceEntry>();
    }

    public IReadOnlyList<CloudVoiceEntry> GetAllConfiguredVoices()
    {
        var list = new List<CloudVoiceEntry>();
        foreach (var p in new[] { CloudTtsProvider.Free, CloudTtsProvider.Volcengine, CloudTtsProvider.Fish, CloudTtsProvider.OpenAi, CloudTtsProvider.Azure, CloudTtsProvider.ElevenLabs })
        {
            if (IsConfigured(p)) list.AddRange(GetVoices(p));
        }
        return list;
    }

    public async Task<(bool Ok, int Count, string Message)> RefreshVoicesAsync(CloudTtsProvider provider, CancellationToken ct = default)
    {
        string pKey = CloudVoiceEntry.ProviderKey(provider);
        string apiKey = _library.Secrets.Get($"tts.{pKey}.key").Trim();
        if (provider != CloudTtsProvider.Free && string.IsNullOrEmpty(apiKey))
            return (false, 0, "请先填写 API Key");

        try
        {
            switch (provider)
            {
                case CloudTtsProvider.Free:
                {
                    _library.Secrets.Set("tts.free.dismissed", "");
                    byte[]? probe = await FetchFreeOnlineWavAsync("zf_001", "你好", "zh-CN", ct);
                    if (probe is null || probe.Length < 128)
                        return (false, 0, "在线公共线路当前繁忙，请稍后再试或下载本地离线模型");
                    return (true, FreeBuiltinVoices.Length, $"已连通免 Key 在线自然语音（{FreeBuiltinVoices.Length} 个音色可用）");
                }

                case CloudTtsProvider.Volcengine:
                {
                    var (appId, token) = ResolveVolcengineCredentials(apiKey);
                    if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(token))
                        return (false, 0, "请同时填写火山引擎 AppID 与 Access Token");
                    var probeBody = new
                    {
                        app = new { appid = appId, token = token, cluster = "volcano_tts" },
                        user = new { uid = "yuyue-win-probe" },
                        audio = new { voice_type = "zh_female_cancan_mars_bigtts", encoding = "wav", rate = 24000 },
                        request = new { reqid = Guid.NewGuid().ToString(), text = "好", text_type = "plain", operation = "query" },
                    };
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://openspeech.bytedance.com/api/v1/tts");
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer;{token}");
                    req.Content = new StringContent(JsonSerializer.Serialize(probeBody), Encoding.UTF8, "application/json");
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, 0, await FormatHttpErrorAsync("火山豆包", resp));
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (doc.RootElement.TryGetProperty("code", out var cProp) && cProp.GetInt32() != 3000)
                        return (false, 0, "火山引擎 AppID 或 Token 验证未通过");
                    return (true, VolcengineBuiltinVoices.Length, $"已连通火山引擎豆包语音（{VolcengineBuiltinVoices.Length} 个精选音色）");
                }

                case CloudTtsProvider.Fish:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.fish.audio/model?page_size=20&page_number=1&sort_by=score");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, 0, await FormatHttpErrorAsync("Fish Audio", resp));
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    var rows = new List<string[]>
                    {
                        new[] { "default", "OpenAudio S1 · 默认自然旁白", "zh-CN", "female", "" },
                    };
                    if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in items.EnumerateArray())
                        {
                            string id = el.TryGetProperty("_id", out var ip) ? ip.GetString() ?? "" : "";
                            string title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(title))
                                rows.Add(new[] { id, title, "", "", "" });
                        }
                    }
                    _library.Prefs.CachedCloudVoices[pKey] = rows;
                    _library.SavePrefs();
                    return (true, rows.Count, $"已连通 Fish Audio（共加载 {rows.Count} 个 OpenAudio S1 音色）");
                }

                case CloudTtsProvider.OpenAi:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.openai.com/v1/models/gpt-4o-mini-tts");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, 0, await FormatHttpErrorAsync("OpenAI", resp));
                    return (true, OpenAiBuiltinVoices.Length, $"已连通 OpenAI TTS（{OpenAiBuiltinVoices.Length} 个内置音色可用）");
                }

                case CloudTtsProvider.Azure:
                {
                    string region = _library.Prefs.CloudVoiceAzureRegion.Trim();
                    if (string.IsNullOrEmpty(region)) return (false, 0, "请填写 Azure 区域（如 eastasia）");

                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
                    req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                    req.Headers.UserAgent.ParseAdd("Yuyue");
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, 0, await FormatHttpErrorAsync("Azure", resp));

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var rows = new List<string[]>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string locale = el.TryGetProperty("Locale", out var lp) ? lp.GetString() ?? "" : "";
                        if (!locale.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                            !locale.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
                            !locale.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
                            continue;

                        string shortName = el.TryGetProperty("ShortName", out var sp) ? sp.GetString() ?? "" : "";
                        if (string.IsNullOrEmpty(shortName)) continue;
                        string localName = el.TryGetProperty("LocalName", out var lnp) ? lnp.GetString() ?? shortName : shortName;
                        string gender = el.TryGetProperty("Gender", out var gp) ? (gp.GetString() ?? "").ToLowerInvariant() : "";
                        rows.Add([shortName, $"{localName} ({locale})", locale, gender, ""]);
                    }
                    _library.Prefs.CachedCloudVoices[pKey] = rows;
                    _library.SavePrefs();
                    return (true, rows.Count, $"已连通 Azure 语音服务（共拉取 {rows.Count} 个中英日音色）");
                }

                default:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
                    req.Headers.Add("xi-api-key", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode)
                        return (false, 0, await FormatHttpErrorAsync("ElevenLabs", resp));

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var rows = new List<string[]>();
                    if (doc.RootElement.TryGetProperty("voices", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in vArr.EnumerateArray())
                        {
                            string vid = el.TryGetProperty("voice_id", out var vp) ? vp.GetString() ?? "" : "";
                            string name = el.TryGetProperty("name", out var np) ? np.GetString() ?? vid : vid;
                            if (string.IsNullOrEmpty(vid)) continue;
                            string gender = "", age = "";
                            if (el.TryGetProperty("labels", out var lbl) && lbl.ValueKind == JsonValueKind.Object)
                            {
                                if (lbl.TryGetProperty("gender", out var g)) gender = (g.GetString() ?? "").ToLowerInvariant();
                                if (lbl.TryGetProperty("age", out var a)) age = (a.GetString() ?? "").ToLowerInvariant();
                            }
                            rows.Add([vid, name, "", gender, age]);
                        }
                    }
                    _library.Prefs.CachedCloudVoices[pKey] = rows;
                    _library.SavePrefs();
                    return (true, rows.Count, $"已连通 ElevenLabs（共拉取 {rows.Count} 个音色）");
                }
            }
        }
        catch (Exception ex)
        {
            return (false, 0, $"连接失败：{ex.Message}");
        }
    }

    public CloudVoiceActing MapDramaActing(DramaAct? act, bool chinese)
    {
        if (act is null) return new CloudVoiceActing();
        string instruction = "";
        string head = "", tail = "";
        try
        {
            instruction = _library.Core.DramaActInstruction(act, chinese);
            var tags = _library.Core.DramaActTags(act);
            head = tags.Head;
            tail = tags.Tail;
        }
        catch { }

        string? azureStyle = act.Volume switch
        {
            ActVolume.Whisper => "whispering",
            ActVolume.Shout => "shouting",
            _ when act.Level > 0 => act.Emotion switch
            {
                ActEmotion.Joy => "cheerful",
                ActEmotion.Anger => "angry",
                ActEmotion.Sadness => "sad",
                ActEmotion.Fear or ActEmotion.Anxious => "fearful",
                ActEmotion.Disgust => "disgruntled",
                ActEmotion.Tender => "gentle",
                ActEmotion.Mocking => "unfriendly",
                ActEmotion.Surprise => "excited",
                _ => null,
            },
            _ => null,
        };
        double[] degrees = [0.8, 0.8, 1.2, 1.6];
        double degree = degrees[Math.Min((int)act.Level, 3)];
        return new CloudVoiceActing(instruction, head, tail, azureStyle, degree);
    }

    public async Task<byte[]?> SynthesizeWavAsync(
        string fullVoiceId,
        string rawText,
        string language = "zh-CN",
        CloudVoiceActing? acting = null,
        CancellationToken ct = default)
    {
        if (!TryParseFullId(fullVoiceId, out var provider, out var voiceId)) return null;
        string plain = CardVoiceEngine.PlainSpokenText(rawText);
        if (!plain.Any(char.IsLetterOrDigit)) return null;

        string pKey = CloudVoiceEntry.ProviderKey(provider);
        string apiKey = _library.Secrets.Get($"tts.{pKey}.key").Trim();
        if (provider != CloudTtsProvider.Free && string.IsNullOrEmpty(apiKey)) return null;

        var act = acting ?? new CloudVoiceActing();
        SpokenPart[]? parts = null;
        string readingsKey = "";
        if (provider == CloudTtsProvider.Azure && language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                parts = _library.Core.DramaSpokenParts(rawText);
                var poly = parts.Where(p => !string.IsNullOrEmpty(p.Reading)).Select(p => $"{p.Text}={p.Reading}").ToArray();
                if (poly.Length > 0) readingsKey = string.Join(",", poly);
            }
            catch { }
        }

        string cachePath = GetCacheFilePath(pKey, fullVoiceId, plain, act, readingsKey);
        if (File.Exists(cachePath))
        {
            try { return await File.ReadAllBytesAsync(cachePath, ct); } catch { }
        }

        byte[] wavBytes;
        switch (provider)
        {
            case CloudTtsProvider.Free:
            {
                byte[]? freeWav = await FetchFreeOnlineWavAsync(voiceId, plain, language, ct);
                if (freeWav is null || freeWav.Length <= 44) return null;
                wavBytes = freeWav;
                break;
            }

            case CloudTtsProvider.Volcengine:
            {
                var (appId, token) = ResolveVolcengineCredentials(apiKey);
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(token)) return null;
                var body = new
                {
                    app = new { appid = appId, token = token, cluster = "volcano_tts" },
                    user = new { uid = "yuyue-reader" },
                    audio = new { voice_type = voiceId, encoding = "wav", rate = 24000, speed_ratio = 1.0 },
                    request = new { reqid = Guid.NewGuid().ToString(), text = plain, text_type = "plain", operation = "query" },
                };
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://openspeech.bytedance.com/api/v1/tts");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer;{token}");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("火山豆包", resp));
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                string b64 = doc.RootElement.TryGetProperty("data", out var dp) ? dp.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(b64)) return null;
                wavBytes = Convert.FromBase64String(b64);
                break;
            }

            case CloudTtsProvider.Fish:
            {
                object payload = voiceId == "default"
                    ? new { text = plain, format = "wav", sample_rate = 24000, latency = "normal" }
                    : new { text = plain, reference_id = voiceId, format = "wav", sample_rate = 24000, latency = "normal" };
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.fish.audio/v1/tts");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Headers.TryAddWithoutValidation("model", "s1");
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("Fish Audio", resp));
                wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                break;
            }

            case CloudTtsProvider.OpenAi:
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                object payload = string.IsNullOrWhiteSpace(act.Instruction)
                    ? new { model = "gpt-4o-mini-tts", voice = voiceId, input = plain, response_format = "wav" }
                    : new { model = "gpt-4o-mini-tts", voice = voiceId, input = plain, response_format = "wav", instructions = act.Instruction };
                req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("OpenAI", resp));
                wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                break;
            }

            case CloudTtsProvider.Azure:
            {
                string region = _library.Prefs.CloudVoiceAzureRegion.Trim();
                if (string.IsNullOrEmpty(region)) throw new InvalidOperationException("未配置 Azure 区域");
                string xmlLang = ResolveXmlLang(voiceId, language);
                string inner = BuildAzureSsmlInner(plain, parts, xmlLang, act);
                string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='{xmlLang}'><voice name='{EscapeXml(voiceId)}'>{inner}</voice></speak>";

                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1");
                req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                req.Headers.Add("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm");
                req.Headers.UserAgent.ParseAdd("Yuyue");
                req.Content = new StringContent(ssml, Encoding.UTF8, "application/ssml+xml");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("Azure", resp));
                wavBytes = await resp.Content.ReadAsByteArrayAsync(ct);
                break;
            }

            default:
            {
                bool hasTags = !(string.IsNullOrEmpty(act.Head) && string.IsNullOrEmpty(act.Tail));
                string modelId = hasTags ? "eleven_v3" : "eleven_multilingual_v2";
                string inputText = hasTags ? $"{act.Head}{plain}{act.Tail}" : plain;

                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=pcm_24000");
                req.Headers.Add("xi-api-key", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new { text = inputText, model_id = modelId }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("ElevenLabs", resp));
                byte[] pcm = await resp.Content.ReadAsByteArrayAsync(ct);
                wavBytes = WrapPcm16MonoInWav(pcm, 24_000);
                break;
            }
        }

        if (wavBytes.Length > 44)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                string tmp = cachePath + ".part";
                await File.WriteAllBytesAsync(tmp, wavBytes, ct);
                File.Move(tmp, cachePath, overwrite: true);
            }
            catch { }
        }
        return wavBytes;
    }

    public async Task PrefetchAsync(string fullVoiceId, IEnumerable<string> sentences, string language, CancellationToken ct)
    {
        if (!TryParseFullId(fullVoiceId, out var provider, out _)) return;
        string pKey = CloudVoiceEntry.ProviderKey(provider);
        var act = new CloudVoiceActing();
        var pending = sentences
            .Where(s => CardVoiceEngine.PlainSpokenText(s).Any(char.IsLetterOrDigit))
            .Where(s => !File.Exists(GetCacheFilePath(pKey, fullVoiceId, CardVoiceEngine.PlainSpokenText(s), act, "")))
            .Take(8)
            .ToArray();

        for (int i = 0; i < pending.Length; i += 2)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (i + 1 < pending.Length)
                {
                    await Task.WhenAll(
                        SynthesizeWavAsync(fullVoiceId, pending[i], language, null, ct),
                        SynthesizeWavAsync(fullVoiceId, pending[i + 1], language, null, ct));
                }
                else
                {
                    await SynthesizeWavAsync(fullVoiceId, pending[i], language, null, ct);
                }
            }
            catch
            {
                break; // Stop prefetch immediately on first failure per CloudVoice.swift
            }
        }
    }

    private (string AppId, string Token) ResolveVolcengineCredentials(string rawKey)
    {
        int bar = rawKey.IndexOf('|');
        if (bar > 0 && bar < rawKey.Length - 1)
            return (rawKey[..bar].Trim(), rawKey[(bar + 1)..].Trim());
        return (_library.Secrets.Get("tts.volcengine.appid").Trim(), rawKey);
    }

    private static async Task<byte[]?> FetchFreeOnlineWavAsync(string voiceId, string text, string language, CancellationToken ct)
    {
        bool isZh = language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) || text.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        int sid = FreeBuiltinVoices.FirstOrDefault(v => v.Id == voiceId).Sid;
        if (sid <= 0) sid = isZh ? 55 : 3;

        string[] bases =
        [
            "https://www.modelscope.cn/api/v1/studio/k2-fsa/text-to-speech/gradio",
            "https://k2-fsa-text-to-speech.hf.space",
        ];
        string langLabel = isZh ? "Chinese+English (kokoro-v1.1-zh)" : "English (kokoro-v1.0)";
        string modelLabel = isZh ? "csukuangfj/kokoro-int8-multi-lang-v1_1|103" : "csukuangfj/kokoro-int8-en-v0_19|11";
        foreach (string b in bases)
        {
            byte[]? wav = await CallGradioWavAsync(b, "process", new object[] { langLabel, modelLabel, text, sid, 1.0 }, ct);
            if (wav is not null) return wav;
        }
        // 不跨模型静默回退到不同音色，失败时向上报错告知用户。
        return null;
    }

    private static async Task<byte[]?> CallGradioWavAsync(string baseUrl, string fnName, object[] args, CancellationToken ct)
    {
        try
        {
            string joinUrl = $"{baseUrl.TrimEnd('/')}/gradio_api/call/{fnName}";
            using var joinReq = new HttpRequestMessage(HttpMethod.Post, joinUrl)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { data = args }), Encoding.UTF8, "application/json"),
            };
            using var joinResp = await Http.SendAsync(joinReq, ct);
            if (!joinResp.IsSuccessStatusCode) return null;
            using var joinDoc = JsonDocument.Parse(await joinResp.Content.ReadAsStringAsync(ct));
            string eventId = joinDoc.RootElement.TryGetProperty("event_id", out var ep) ? ep.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(eventId)) return null;

            using var sseResp = await Http.GetAsync($"{joinUrl}/{eventId}", ct);
            if (!sseResp.IsSuccessStatusCode) return null;
            string sseText = await sseResp.Content.ReadAsStringAsync(ct);
            string? audioUrl = null;
            string lastEvent = "";
            foreach (string rawLine in sseText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("event:", StringComparison.Ordinal))
                    lastEvent = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal) && lastEvent == "complete")
                {
                    using var arrDoc = JsonDocument.Parse(line[5..].Trim());
                    if (arrDoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in arrDoc.RootElement.EnumerateArray())
                        {
                            if (el.ValueKind != JsonValueKind.Object) continue;
                            if (el.TryGetProperty("url", out var uProp) && uProp.GetString() is { } u && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            {
                                audioUrl = u;
                                break;
                            }
                            if (el.TryGetProperty("path", out var pProp) && pProp.GetString() is { } p && !string.IsNullOrEmpty(p))
                            {
                                audioUrl = $"{baseUrl.TrimEnd('/')}/gradio_api/file={p}";
                                break;
                            }
                        }
                    }
                }
            }
            if (string.IsNullOrEmpty(audioUrl)) return null;
            byte[] bytes = await Http.GetByteArrayAsync(audioUrl, ct);
            return bytes.Length > 128 ? bytes : null;
        }
        catch
        {
            return null;
        }
    }

    private string GetCacheFilePath(string providerKey, string fullVoiceId, string plainText, CloudVoiceActing act, string readings)
    {
        string cacheKey = (act.IsFlat && string.IsNullOrEmpty(readings))
            ? plainText
            : $"{plainText}\u0002{act.CacheKeyPart}\u0002{readings}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{fullVoiceId}\u0001{cacheKey}"));
        string hex32 = Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
        return Path.Combine(_library.DataDir, "cache", "yuyue-voices", providerKey, $"{hex32}.wav");
    }

    private static string ResolveXmlLang(string voiceId, string language)
    {
        int dash1 = voiceId.IndexOf('-');
        int dash2 = dash1 > 0 ? voiceId.IndexOf('-', dash1 + 1) : -1;
        if (dash1 == 2 && dash2 == 5) return voiceId[..5];
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "zh-CN";
        if (language.StartsWith("ja", StringComparison.OrdinalIgnoreCase)) return "ja-JP";
        return "en-US";
    }

    private static string BuildAzureSsmlInner(string plain, SpokenPart[]? parts, string xmlLang, CloudVoiceActing act)
    {
        string inner;
        if (parts is { Length: > 0 } && xmlLang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                string esc = EscapeXml(p.Text);
                if (!string.IsNullOrWhiteSpace(p.Reading))
                    sb.Append($"<phoneme alphabet='sapi' ph='{EscapeXml(ToSapiPinyin(p.Reading!))}'>{esc}</phoneme>");
                else
                    sb.Append(esc);
            }
            inner = sb.ToString();
        }
        else
        {
            inner = EscapeXml(plain);
        }

        if (!string.IsNullOrEmpty(act.Style))
            inner = $"<mstts:express-as style='{EscapeXml(act.Style!)}' styledegree='{act.Degree:0.0}'>{inner}</mstts:express-as>";

        return inner;
    }

    private static string ToSapiPinyin(string pinyin)
    {
        var toneMap = new Dictionary<char, (string Base, int Tone)>
        {
            ['ā'] = ("a", 1), ['á'] = ("a", 2), ['ǎ'] = ("a", 3), ['à'] = ("a", 4),
            ['ē'] = ("e", 1), ['é'] = ("e", 2), ['ě'] = ("e", 3), ['è'] = ("e", 4),
            ['ī'] = ("i", 1), ['í'] = ("i", 2), ['ǐ'] = ("i", 3), ['ì'] = ("i", 4),
            ['ō'] = ("o", 1), ['ó'] = ("o", 2), ['ǒ'] = ("o", 3), ['ò'] = ("o", 4),
            ['ū'] = ("u", 1), ['ú'] = ("u", 2), ['ǔ'] = ("u", 3), ['ù'] = ("u", 4),
            ['ǖ'] = ("v", 1), ['ǘ'] = ("v", 2), ['ǚ'] = ("v", 3), ['ǜ'] = ("v", 4),
        };

        var outSyllables = new List<string>();
        foreach (var raw in pinyin.ToLowerInvariant().Split([' ', '\'', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            int tone = 5;
            var sb = new StringBuilder();
            foreach (char c in raw)
            {
                if (toneMap.TryGetValue(c, out var m)) { sb.Append(m.Base); tone = m.Tone; }
                else if (c == 'ü') sb.Append('v');
                else if (c is >= '1' and <= '5') tone = c - '0';
                else if (char.IsLetter(c)) sb.Append(c);
            }
            if (sb.Length > 0) outSyllables.Add($"{sb} {tone}");
        }
        return string.Join(" ", outSyllables);
    }

    private static string EscapeXml(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("'", "&apos;");

    public static byte[] WrapPcm16MonoInWav(byte[] pcmBytes, int sampleRate)
    {
        var wav = new byte[44 + pcmBytes.Length];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + pcmBytes.Length).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes((short)1).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * 2).CopyTo(wav, 28);
        BitConverter.GetBytes((short)2).CopyTo(wav, 32);
        BitConverter.GetBytes((short)16).CopyTo(wav, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(pcmBytes.Length).CopyTo(wav, 40);
        Buffer.BlockCopy(pcmBytes, 0, wav, 44, pcmBytes.Length);
        return wav;
    }

    private static async Task<string> FormatHttpErrorAsync(string provider, HttpResponseMessage resp)
    {
        string body = "";
        try { body = await resp.Content.ReadAsStringAsync(); } catch { }
        int code = (int)resp.StatusCode;
        if (code is 401 or 403)
        {
            if (body.Contains("quota_exceeded", StringComparison.OrdinalIgnoreCase))
                return $"{provider} 账户额度已用尽";
            return $"{provider} API Key 无效或无权限 ({code})";
        }
        if (code == 402) return $"{provider} 账户余额不足 (402)";
        if (code == 429)
            return body.Contains("quota", StringComparison.OrdinalIgnoreCase)
                ? $"{provider} 额度已用尽 (429)"
                : $"{provider} 请求过于频繁，请稍后再试 (429)";
        string shortBody = body.Length > 140 ? body[..140] + "…" : body;
        return $"{provider} 服务返回错误 ({code})：{shortBody}";
    }
}
