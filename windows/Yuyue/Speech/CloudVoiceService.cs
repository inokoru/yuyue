using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Yuyue.Core;

namespace Yuyue.Speech;

public enum CloudProviderGroup
{
    Domestic,
    Global,
    Local,
}

public enum CloudTtsProvider
{
    // Free / Domestic
    Edge,
    Free,
    Volcengine,
    Xiaomi,
    SiliconFlow,
    DashScope,
    MiniMax,
    // Global
    Fish,
    Azure,
    OpenAi,
    ElevenLabs,
    Deepgram,
    // Local & LAN OpenAI-compatible
    VoiceStudio,
    Voicebox,
    MlxAudio,
    Speaches,
    Local,
}

public sealed record CloudVoiceEntry(
    CloudTtsProvider Provider,
    string VoiceId,
    string Name,
    string? Language,
    string? Gender,
    string? Age)
{
    public string DisplayName => Name;
    public string FullId => $"cloud.{ProviderKey(Provider)}.{VoiceId}";

    public bool Speaks(string? lang)
    {
        if (string.IsNullOrWhiteSpace(Language) || string.IsNullOrWhiteSpace(lang)) return true;
        string a = Language.ToLowerInvariant();
        string b = lang.ToLowerInvariant();
        int da = a.IndexOfAny(['-', '_']);
        int db = b.IndexOfAny(['-', '_']);
        return (da > 0 ? a[..da] : a) == (db > 0 ? b[..db] : b);
    }

    public static string ProviderKey(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Edge => "edge",
        CloudTtsProvider.Free => "free",
        CloudTtsProvider.Volcengine => "volcengine",
        CloudTtsProvider.Xiaomi => "xiaomi",
        CloudTtsProvider.SiliconFlow => "siliconflow",
        CloudTtsProvider.DashScope => "dashscope",
        CloudTtsProvider.MiniMax => "minimax",
        CloudTtsProvider.Fish => "fish",
        CloudTtsProvider.Azure => "azure",
        CloudTtsProvider.OpenAi => "openai",
        CloudTtsProvider.ElevenLabs => "elevenlabs",
        CloudTtsProvider.Deepgram => "deepgram",
        CloudTtsProvider.VoiceStudio => "voicestudio",
        CloudTtsProvider.Voicebox => "voicebox",
        CloudTtsProvider.MlxAudio => "mlxaudio",
        CloudTtsProvider.Speaches => "speaches",
        CloudTtsProvider.Local => "local",
        _ => "openai",
    };

    public static bool TryParseProviderKey(string pKey, out CloudTtsProvider provider)
    {
        switch (pKey.ToLowerInvariant())
        {
            case "edge": provider = CloudTtsProvider.Edge; return true;
            case "free": provider = CloudTtsProvider.Free; return true;
            case "volcengine": provider = CloudTtsProvider.Volcengine; return true;
            case "fish": provider = CloudTtsProvider.Fish; return true;
            case "xiaomi": provider = CloudTtsProvider.Xiaomi; return true;
            case "siliconflow": provider = CloudTtsProvider.SiliconFlow; return true;
            case "dashscope": provider = CloudTtsProvider.DashScope; return true;
            case "minimax": provider = CloudTtsProvider.MiniMax; return true;
            case "azure": provider = CloudTtsProvider.Azure; return true;
            case "openai": provider = CloudTtsProvider.OpenAi; return true;
            case "elevenlabs": provider = CloudTtsProvider.ElevenLabs; return true;
            case "deepgram": provider = CloudTtsProvider.Deepgram; return true;
            case "voicestudio": provider = CloudTtsProvider.VoiceStudio; return true;
            case "voicebox": provider = CloudTtsProvider.Voicebox; return true;
            case "mlxaudio": provider = CloudTtsProvider.MlxAudio; return true;
            case "speaches": provider = CloudTtsProvider.Speaches; return true;
            case "local": provider = CloudTtsProvider.Local; return true;
            default: provider = CloudTtsProvider.OpenAi; return false;
        }
    }
}

public sealed record CloudVoiceActing(
    string Instruction = "",
    string Head = "",
    string Tail = "",
    string? Style = null,
    double Degree = 1.0)
{
    public bool IsFlat => string.IsNullOrEmpty(Instruction) && string.IsNullOrEmpty(Head) && string.IsNullOrEmpty(Tail) && string.IsNullOrEmpty(Style);
    public string CacheKeyPart => $"{Instruction}|{Head}|{Tail}|{Style ?? ""}|{Degree:0.0}";
}

/// <summary>
/// 1:1 port of <c>LocalVoiceProbe</c> in <c>apple/Yuyue/Speech/CloudVoice.swift</c>:
/// concurrent zero-config discovery for local/LAN OpenAI-compatible TTS servers
/// (VoiceStudio :3900, Voicebox :17493, mlx-audio / Speaches / Custom :8000).
/// </summary>
public sealed class LocalVoiceProbe
{
    public static LocalVoiceProbe Shared { get; } = new();

    public sealed record DiscoveredVoice(string Id, string Name, string Gender);
    public sealed record EndpointInfo(IReadOnlyList<DiscoveredVoice> Voices, string FirstModel);

    private readonly object _gate = new();
    private readonly HashSet<CloudTtsProvider> _live = [];
    private readonly HashSet<CloudTtsProvider> _checked = [];
    private readonly Dictionary<CloudTtsProvider, string> _urls = new();
    private readonly Dictionary<CloudTtsProvider, string> _models = new();
    private readonly Dictionary<CloudTtsProvider, IReadOnlyList<DiscoveredVoice>> _voices = new();

    public bool IsLive(CloudTtsProvider p) { lock (_gate) return _live.Contains(p); }
    public bool WasChecked(CloudTtsProvider p) { lock (_gate) return _checked.Contains(p); }
    public string? DiscoveredUrl(CloudTtsProvider p) { lock (_gate) return _urls.TryGetValue(p, out var u) ? u : null; }
    public string? DiscoveredModel(CloudTtsProvider p) { lock (_gate) return _models.TryGetValue(p, out var m) ? m : null; }
    public IReadOnlyList<DiscoveredVoice> DiscoveredVoices(CloudTtsProvider p) { lock (_gate) return _voices.TryGetValue(p, out var v) ? v : []; }

    public void RecordManualSuccess(CloudTtsProvider p, string baseV1, EndpointInfo info)
    {
        lock (_gate)
        {
            _live.Add(p);
            _checked.Add(p);
            _urls[p] = baseV1;
            if (!string.IsNullOrEmpty(info.FirstModel)) _models[p] = info.FirstModel;
            if (info.Voices.Count > 0) _voices[p] = info.Voices;
        }
    }

    public void Clear(CloudTtsProvider p)
    {
        lock (_gate)
        {
            _live.Remove(p);
            _voices.Remove(p);
        }
    }

    public async Task<bool> ScanAllAsync(LibraryService library, CancellationToken ct = default)
    {
        var targets = new List<(CloudTtsProvider Provider, string BaseV1, string? Key)>();

        string vsUrl = library.CloudVoices.GetSavedUrl(CloudTtsProvider.VoiceStudio);
        targets.Add((CloudTtsProvider.VoiceStudio, CloudVoiceService.NormalizedBaseV1(vsUrl), library.CloudVoices.GetApiKey(CloudTtsProvider.VoiceStudio)));

        string vbUrl = library.CloudVoices.GetSavedUrl(CloudTtsProvider.Voicebox);
        targets.Add((CloudTtsProvider.Voicebox, CloudVoiceService.NormalizedBaseV1(vbUrl), library.CloudVoices.GetApiKey(CloudTtsProvider.Voicebox)));

        bool any8000 = false;
        foreach (var p in new[] { CloudTtsProvider.MlxAudio, CloudTtsProvider.Speaches, CloudTtsProvider.Local })
        {
            string pKey = CloudVoiceEntry.ProviderKey(p);
            if (library.Prefs.CloudVoiceEnabled.Contains(pKey) || library.Prefs.CloudVoiceBaseUrls.ContainsKey(pKey))
            {
                string u = CloudVoiceService.NormalizedBaseV1(library.CloudVoices.GetSavedUrl(p));
                if (!string.IsNullOrEmpty(u))
                {
                    targets.Add((p, u, library.CloudVoices.GetApiKey(p)));
                    any8000 = true;
                }
            }
        }
        if (!any8000)
        {
            targets.Add((CloudTtsProvider.Speaches, "http://127.0.0.1:8000/v1", null));
        }

        HashSet<CloudTtsProvider> before;
        lock (_gate) before = new HashSet<CloudTtsProvider>(_live);

        var tasks = targets.Select(async t =>
        {
            try
            {
                var info = await InspectEndpointAsync(t.BaseV1, t.Key, System.TimeSpan.FromMilliseconds(550), passiveScan: true, ct);
                return (t.Provider, HitBase: (string?)t.BaseV1, Info: (EndpointInfo?)info);
            }
            catch
            {
                return (t.Provider, HitBase: (string?)null, Info: (EndpointInfo?)null);
            }
        }).ToArray();

        var results = await Task.WhenAll(tasks);
        lock (_gate)
        {
            foreach (var (provider, hitBase, info) in results)
            {
                _checked.Add(provider);
                if (!string.IsNullOrEmpty(hitBase) && info is not null)
                {
                    _live.Add(provider);
                    _urls[provider] = hitBase;
                    if (!string.IsNullOrEmpty(info.FirstModel)) _models[provider] = info.FirstModel;
                    if (info.Voices.Count > 0) _voices[provider] = info.Voices;
                }
                else
                {
                    _live.Remove(provider);
                }
            }
            return !before.SetEquals(_live);
        }
    }

    public static async Task<EndpointInfo> InspectEndpointAsync(
        string baseV1,
        string? apiKey,
        System.TimeSpan timeout,
        bool passiveScan = false,
        CancellationToken ct = default)
    {
        string cleanBase = CloudVoiceService.NormalizedBaseV1(baseV1);
        string root = cleanBase.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? cleanBase[..^3] : cleanBase;

        using var http = new HttpClient { Timeout = timeout };

        async Task<JsonDocument?> GetJsonAsync(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var resp = await http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return null;
                string body = await resp.Content.ReadAsStringAsync(ct);
                return JsonDocument.Parse(body);
            }
            catch { return null; }
        }

        var foundVoices = new List<DiscoveredVoice>();
        string firstModel = "";
        bool anyReachable = false;

        foreach (string path in new[] { $"{cleanBase}/audio/voices", $"{cleanBase}/voices", $"{root}/profiles" })
        {
            using var doc = await GetJsonAsync(path);
            if (doc is null) continue;
            anyReachable = true;
            var parsed = ParseVoices(doc.RootElement);
            if (parsed.Count > 0)
            {
                foundVoices = parsed;
                break;
            }
        }

        using (var doc = await GetJsonAsync($"{cleanBase}/models"))
        {
            if (doc is not null)
            {
                anyReachable = true;
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("data", out var dataArr) &&
                    dataArr.ValueKind == JsonValueKind.Array)
                {
                    var ids = new List<string>();
                    foreach (var el in dataArr.EnumerateArray())
                    {
                        if (el.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                            ids.Add(id);
                    }
                    if (ids.Count > 0) firstModel = ids[0];
                    bool looksLikeSpeech = ids.Any(id =>
                    {
                        string l = id.ToLowerInvariant();
                        return l.Contains("tts") || l.Contains("voice") || l.Contains("kokoro")
                            || l.Contains("cosy") || l.Contains("speech") || l.Contains("chatterbox")
                            || l.Contains("omni") || l.Contains("melo") || l.Contains("piper");
                    });
                    if (foundVoices.Count == 0 && !looksLikeSpeech && passiveScan)
                        anyReachable = false;
                }
            }
        }

        if (!anyReachable)
            throw new HttpRequestException("无法连接到本地语音服务");

        if (foundVoices.Count == 0)
        {
            foundVoices = CloudVoiceService.DefaultLocalVoices
                .Select(v => new DiscoveredVoice(v.Id, v.Name, v.Gender))
                .ToList();
        }
        return new EndpointInfo(foundVoices, firstModel);
    }

    internal static List<DiscoveredVoice> ParseVoices(JsonElement root)
    {
        JsonElement arr = default;
        bool hasArr = false;
        if (root.ValueKind == JsonValueKind.Array)
        {
            arr = root;
            hasArr = true;
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array) { arr = v; hasArr = true; }
            else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array) { arr = d; hasArr = true; }
            else if (root.TryGetProperty("profiles", out var p) && p.ValueKind == JsonValueKind.Array) { arr = p; hasArr = true; }
        }
        if (!hasArr) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<DiscoveredVoice>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                string s = item.GetString() ?? "";
                if (!string.IsNullOrEmpty(s) && seen.Add(s))
                    list.Add(new DiscoveredVoice(s, s, GuessGender(s)));
            }
            else if (item.ValueKind == JsonValueKind.Object)
            {
                string id = TryGetStr(item, "voice_id") ?? TryGetStr(item, "id") ?? TryGetStr(item, "name") ?? "";
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
                string name = TryGetStr(item, "name") ?? TryGetStr(item, "title") ?? id;
                string gender = (TryGetStr(item, "gender") ?? GuessGender($"{id} {name}")).ToLowerInvariant();
                list.Add(new DiscoveredVoice(id, name, gender));
            }
        }
        return list;
    }

    private static string? TryGetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string GuessGender(string text)
    {
        string l = text.ToLowerInvariant();
        if (l.StartsWith("af_") || l.StartsWith("bf_") || l.StartsWith("zf_") || l.Contains("female") || l.Contains('女'))
            return "female";
        if (l.StartsWith("am_") || l.StartsWith("bm_") || l.StartsWith("zm_") || l.Contains("male") || l.Contains('男'))
            return "male";
        return "";
    }
}

/// <summary>
/// 1:1 Windows implementation of <c>apple/Yuyue/Speech/CloudVoice.swift</c>:
/// supports all 13 voice providers (4 domestic: Xiaomi MiMo, SiliconFlow, DashScope, MiniMax;
/// 4 global: Azure, OpenAI, ElevenLabs, Deepgram; 5 local/LAN OpenAI-compatible: VoiceStudio, Voicebox,
/// mlx-audio, Speaches, Custom Local), 300 MB persistent LRU audio cache, Companion API key reuse,
/// paid-cloud daily character budget enforcement, and automatic MP3→WAV decoding for seamless mixer/player use.
/// </summary>
public sealed class CloudVoiceService
{
    public const long MaxPersistentCacheBytes = 300_000_000L; // 300 MB

    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(45) };
    private static readonly HttpClient LocalHttp = new() { Timeout = System.TimeSpan.FromSeconds(90) };
    private int _trimCounter;

    public static readonly CloudTtsProvider[] AllProviders =
    [
        CloudTtsProvider.Edge,
        CloudTtsProvider.Free,
        CloudTtsProvider.Volcengine,
        CloudTtsProvider.Xiaomi,
        CloudTtsProvider.SiliconFlow,
        CloudTtsProvider.DashScope,
        CloudTtsProvider.MiniMax,
        CloudTtsProvider.Fish,
        CloudTtsProvider.Azure,
        CloudTtsProvider.OpenAi,
        CloudTtsProvider.ElevenLabs,
        CloudTtsProvider.Deepgram,
        CloudTtsProvider.VoiceStudio,
        CloudTtsProvider.Voicebox,
        CloudTtsProvider.MlxAudio,
        CloudTtsProvider.Speaches,
        CloudTtsProvider.Local,
    ];

    public static CloudProviderGroup ProviderGroup(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Free or CloudTtsProvider.Volcengine or CloudTtsProvider.Xiaomi or CloudTtsProvider.SiliconFlow or CloudTtsProvider.DashScope or CloudTtsProvider.MiniMax => CloudProviderGroup.Domestic,
        CloudTtsProvider.Edge or CloudTtsProvider.Fish or CloudTtsProvider.Azure or CloudTtsProvider.OpenAi or CloudTtsProvider.ElevenLabs or CloudTtsProvider.Deepgram => CloudProviderGroup.Global,
        _ => CloudProviderGroup.Local,
    };

    public static bool IsLocalProvider(CloudTtsProvider p) => ProviderGroup(p) == CloudProviderGroup.Local;
    public static bool IsFreeOnline(CloudTtsProvider p) => p is CloudTtsProvider.Edge or CloudTtsProvider.Free;
    public static bool IsPaidCloud(CloudTtsProvider p) => !IsLocalProvider(p) && !IsFreeOnline(p);
    public static bool NeedsKey(CloudTtsProvider p) => !IsLocalProvider(p) && !IsFreeOnline(p);

    public static string ProviderDisplayName(CloudTtsProvider p, DevicePrefs? prefs = null) => p switch
    {
        CloudTtsProvider.Edge => "微软 Edge",
        CloudTtsProvider.Free => "在线自然语音",
        CloudTtsProvider.Volcengine => "火山豆包",
        CloudTtsProvider.Fish => "Fish Audio",
        CloudTtsProvider.Xiaomi => "小米 MiMo",
        CloudTtsProvider.SiliconFlow => "硅基流动",
        CloudTtsProvider.DashScope => "阿里云百炼",
        CloudTtsProvider.MiniMax => "MiniMax",
        CloudTtsProvider.Azure => "微软 Azure",
        CloudTtsProvider.OpenAi => "OpenAI",
        CloudTtsProvider.ElevenLabs => "ElevenLabs",
        CloudTtsProvider.Deepgram => "Deepgram",
        CloudTtsProvider.VoiceStudio => "VoiceStudio",
        CloudTtsProvider.Voicebox => "Voicebox",
        CloudTtsProvider.MlxAudio => "mlx-audio",
        CloudTtsProvider.Speaches => "Speaches",
        CloudTtsProvider.Local => prefs is not null
            && prefs.CloudVoiceCustomNames.TryGetValue("local", out var custom)
            && !string.IsNullOrWhiteSpace(custom)
                ? custom.Trim()
                : "自定义语音接口",
        _ => p.ToString(),
    };

    public static string? ProviderBadge(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Edge or CloudTtsProvider.Free => "免 Key",
        CloudTtsProvider.Xiaomi => "限时免费",
        CloudTtsProvider.Volcengine or CloudTtsProvider.Fish or CloudTtsProvider.Azure or CloudTtsProvider.Deepgram => "含免费额度",
        CloudTtsProvider.VoiceStudio or CloudTtsProvider.Voicebox or CloudTtsProvider.MlxAudio or CloudTtsProvider.Speaches => "本机开源",
        _ => null,
    };

    public static string ProviderKeyHint(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Edge or CloudTtsProvider.Free => "免填 Key",
        CloudTtsProvider.Volcengine => "AppID|Access Token 或单独填 Access Token",
        CloudTtsProvider.Fish => "Fish Audio API Key",
        CloudTtsProvider.Xiaomi or CloudTtsProvider.SiliconFlow or CloudTtsProvider.DashScope or CloudTtsProvider.OpenAi => "sk-…",
        CloudTtsProvider.MiniMax => "eyJ…",
        CloudTtsProvider.Azure => "资源的密钥 1",
        CloudTtsProvider.ElevenLabs => "sk_…",
        CloudTtsProvider.Deepgram => "Token…",
        _ => "本机不用填",
    };

    public static string ProviderHelp(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.Edge => "免注册免填 Key，直连微软 Edge 自然神经语音（晓晓、云希、Ava 等），已朗读句子自动缓存在本机。",
        CloudTtsProvider.Free => "无需 API Key，经由开源公共线路试听或朗读中英自然人声；高频朗读建议下载本机离线模型。",
        CloudTtsProvider.Volcengine => "火山引擎语音合成（豆包大模型音色），在控制台新建应用获取 AppID 与 Access Token。",
        CloudTtsProvider.Fish => "fish.audio 控制台新建 API Key，支持 OpenAudio S1 自然旁白与社区音色库。",
        CloudTtsProvider.Xiaomi => "platform.xiaomimimo.com 控制台新建 API Key，支持自然语言语气控制。",
        CloudTtsProvider.SiliconFlow => "cloud.siliconflow.cn › API 密钥新建。内置 CosyVoice2 音色并支持控制台克隆音色。",
        CloudTtsProvider.DashScope => "阿里云百炼控制台获取 API-KEY，千问3-TTS 按字计费，新开通含免费体验包。",
        CloudTtsProvider.MiniMax => "platform.minimaxi.com › 接口密钥新建。支持高拟真呼吸感与情绪朗读。",
        CloudTtsProvider.OpenAi => "platform.openai.com › API keys 里建一个。费用按字数算，走你的账号。",
        CloudTtsProvider.Azure => "Azure 门户里建一个「语音」资源，拷贝密钥和区域（如 eastasia），有免费额度。",
        CloudTtsProvider.ElevenLabs => "elevenlabs.io › 个人资料 › API Keys。额度按字数算。",
        CloudTtsProvider.Deepgram => "console.deepgram.com 新建 API Key，Aura-2 英文极速低延迟，注册含免费体验金。",
        CloudTtsProvider.VoiceStudio => "在本机启动 VoiceStudio（默认端口 3900），语阅会自动连上你在里面保存的克隆音色。",
        CloudTtsProvider.Voicebox => "在本机启动 Voicebox（默认端口 17493），直接复用其本地多引擎音色，完全离线。",
        CloudTtsProvider.MlxAudio => "连接运行 mlx-audio.server 的局域网或本机（默认端口 8000）。",
        CloudTtsProvider.Speaches => "通过 Docker 部署 Speaches（默认端口 8000），适合本机或家里 NAS 局域网共享。",
        CloudTtsProvider.Local => "支持 GPT-SoVITS、Kokoro-FastAPI、LocalAI、unspeech 等任何兼容 OpenAI /v1/audio/speech 的服务。",
        _ => "",
    };

    public static string DefaultLocalUrl(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.VoiceStudio => "http://127.0.0.1:3900/v1",
        CloudTtsProvider.Voicebox => "http://127.0.0.1:17493/v1",
        CloudTtsProvider.MlxAudio or CloudTtsProvider.Speaches or CloudTtsProvider.Local => "http://127.0.0.1:8000/v1",
        _ => "",
    };

    public static string DefaultLocalModel(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.VoiceStudio => "omnivoice",
        CloudTtsProvider.Voicebox => "tts-1",
        CloudTtsProvider.MlxAudio => "mlx-community/Kokoro-82M-bf16",
        CloudTtsProvider.Speaches => "speaches-ai/Kokoro-82M-v1.0-ONNX",
        CloudTtsProvider.Local => "kokoro",
        _ => "",
    };

    public static string? CompanionSharedProviderId(CloudTtsProvider p) => p switch
    {
        CloudTtsProvider.SiliconFlow => "siliconflow",
        CloudTtsProvider.DashScope => "dashscope",
        CloudTtsProvider.MiniMax => "minimax",
        CloudTtsProvider.OpenAi => "openai",
        _ => null,
    };

    public static readonly (string Id, string Name, string Locale, string Gender, string Age)[] EdgeBuiltinVoices =
    [
        ("zh-CN-XiaoxiaoNeural", "晓晓 · 温暖自然女声", "zh-CN", "female", "young"),
        ("zh-CN-YunxiNeural", "云希 · 阳光清朗男声", "zh-CN", "male", "young"),
        ("zh-CN-YunjianNeural", "云健 · 沉稳播讲男声", "zh-CN", "male", "middle_aged"),
        ("zh-CN-XiaoyiNeural", "晓伊 · 灵动活泼女声", "zh-CN", "female", "young"),
        ("zh-CN-YunyangNeural", "云扬 · 新闻播报男声", "zh-CN", "male", "middle_aged"),
        ("zh-CN-YunxiaNeural", "云夏 · 少年清亮男声", "zh-CN", "male", "young"),
        ("zh-CN-liaoning-XiaobeiNeural", "晓北 · 东北风趣女声", "zh-CN", "female", "young"),
        ("zh-CN-shaanxi-XiaoniNeural", "晓妮 · 陕西温婉女声", "zh-CN", "female", "young"),
        ("zh-TW-HsiaoChenNeural", "曉臻 · 台湾温润女声", "zh-TW", "female", "young"),
        ("zh-TW-YunJheNeural", "雲哲 · 台湾沉稳男声", "zh-TW", "male", "middle_aged"),
        ("zh-HK-HiuMaanNeural", "曉曼 · 粤语自然女声", "zh-HK", "female", "young"),
        ("zh-HK-WanLungNeural", "雲龍 · 粤语沉稳男声", "zh-HK", "male", "middle_aged"),
        ("en-US-AvaMultilingualNeural", "Ava · Natural Multilingual", "en-US", "female", "young"),
        ("en-US-AndrewMultilingualNeural", "Andrew · Warm Multilingual", "en-US", "male", "middle_aged"),
        ("en-US-EmmaMultilingualNeural", "Emma · Clear Multilingual", "en-US", "female", "young"),
        ("en-US-BrianMultilingualNeural", "Brian · Calm Multilingual", "en-US", "male", "middle_aged"),
        ("en-US-AriaNeural", "Aria · Expressive American", "en-US", "female", "young"),
        ("en-US-GuyNeural", "Guy · Deep American", "en-US", "male", "middle_aged"),
        ("en-GB-SoniaNeural", "Sonia · British Female", "en-GB", "female", "young"),
        ("en-GB-RyanNeural", "Ryan · British Male", "en-GB", "male", "middle_aged"),
        ("ja-JP-NanamiNeural", "七海 · 自然日本語女声", "ja-JP", "female", "young"),
        ("ja-JP-KeitaNeural", "圭太 · 自然日本語男声", "ja-JP", "male", "young"),
    ];

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
        ("alloy", "female"), ("ash", "male"), ("ballad", "male"), ("coral", "female"), ("echo", "male"),
        ("fable", "male"), ("nova", "female"), ("onyx", "male"), ("sage", "female"), ("shimmer", "female"),
    ];

    public static readonly (string Id, string Name, string Lang, string Gender, string Age)[] XiaomiVoices =
    [
        ("冰糖", "冰糖 · 清甜活泼", "zh-CN", "female", "young"),
        ("茉莉", "茉莉 · 温柔细腻", "zh-CN", "female", "young"),
        ("苏打", "苏打 · 清爽阳光", "zh-CN", "male", "young"),
        ("白桦", "白桦 · 沉稳厚重", "zh-CN", "male", "middle_aged"),
        ("Mia", "Mia · 自然女声", "en-US", "female", "young"),
        ("Chloe", "Chloe · 知性女声", "en-US", "female", "middle_aged"),
        ("Milo", "Milo · 明亮男声", "en-US", "male", "young"),
        ("Dean", "Dean · 浑厚男声", "en-US", "male", "middle_aged"),
    ];

    public static readonly (string Id, string Name, string Gender, string Age)[] SiliconFlowPresets =
    [
        ("FunAudioLLM/CosyVoice2-0.5B:anna", "安娜 · 沉稳女声", "female", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:claire", "克莱尔 · 温柔女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:bella", "贝拉 · 热情女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:diana", "黛安娜 · 欢快女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:alex", "亚历克斯 · 沉稳男声", "male", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:charles", "查尔斯 · 磁性男声", "male", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:david", "大卫 · 明亮男声", "male", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:benjamin", "本杰明 · 低沉男声", "male", "old"),
    ];

    public static readonly (string Id, string Name, string Gender, string Age)[] DashScopeVoices =
    [
        ("Cherry", "芊悦 · 阳光女声", "female", "young"),
        ("Serena", "苏瑶 · 温婉女声", "female", "young"),
        ("Ethan", "晨煦 · 温暖男声", "male", "young"),
        ("Chelsie", "千雪 · 灵动女声", "female", "young"),
    ];

    public static readonly (string Id, string Name, string Gender, string Age)[] MiniMaxPresets =
    [
        ("female-tianmei", "温柔甜美 · 女声", "female", "young"),
        ("female-yujie", "知性御姐 · 女声", "female", "middle_aged"),
        ("female-shaonv", "元气少女 · 女声", "female", "young"),
        ("male-qn-qingse", "青涩青年 · 男声", "male", "young"),
        ("male-qn-jingying", "精英沉稳 · 男声", "male", "middle_aged"),
    ];

    public static readonly (string Id, string Name, string Gender, string Age)[] DeepgramVoices =
    [
        ("aura-2-thalia-en", "Thalia · 温暖女声", "female", "young"),
        ("aura-2-andromeda-en", "Andromeda · 知性女声", "female", "middle_aged"),
        ("aura-2-helena-en", "Helena · 柔和女声", "female", "young"),
        ("aura-2-apollo-en", "Apollo · 沉稳男声", "male", "middle_aged"),
        ("aura-2-aries-en", "Aries · 明亮男声", "male", "young"),
    ];

    public static readonly (string Id, string Name, string Gender)[] DefaultLocalVoices =
    [
        ("default", "默认音色", ""),
        ("alloy", "Alloy", "female"),
        ("nova", "Nova", "female"),
        ("onyx", "Onyx", "male"),
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

    public string PersistentCacheDir => Path.Combine(_library.DataDir, "cloud-voices");

    public static bool TryParseFullId(string? id, out CloudTtsProvider provider, out string voiceId)
    {
        provider = CloudTtsProvider.OpenAi;
        voiceId = "";
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("cloud.", StringComparison.OrdinalIgnoreCase))
            return false;

        int secondDot = id.IndexOf('.', 6);
        if (secondDot <= 6 || secondDot >= id.Length - 1) return false;
        string pKey = id[6..secondDot];
        voiceId = id[(secondDot + 1)..];
        return CloudVoiceEntry.TryParseProviderKey(pKey, out provider);
    }

    public string GetApiKey(CloudTtsProvider p) =>
        _library.Secrets.Get($"tts.{CloudVoiceEntry.ProviderKey(p)}.key").Trim();

    public void SetApiKey(CloudTtsProvider p, string key) =>
        _library.Secrets.Set($"tts.{CloudVoiceEntry.ProviderKey(p)}.key", key.Trim());

    public string? GetCompanionSharedKey(CloudTtsProvider p)
    {
        string? compId = CompanionSharedProviderId(p);
        if (string.IsNullOrEmpty(compId)) return null;
        string k = _library.Secrets.Get($"ai.key.{compId}").Trim();
        if (string.IsNullOrEmpty(k) && string.Equals(_library.Prefs.AiProvider, compId, StringComparison.OrdinalIgnoreCase))
            k = _library.Secrets.Get("ai.apiKey").Trim();
        return string.IsNullOrEmpty(k) ? null : k;
    }

    public string GetSavedUrl(CloudTtsProvider p)
    {
        string pKey = CloudVoiceEntry.ProviderKey(p);
        if (_library.Prefs.CloudVoiceBaseUrls.TryGetValue(pKey, out var u) && !string.IsNullOrWhiteSpace(u))
            return u.Trim();
        if (LocalVoiceProbe.Shared.DiscoveredUrl(p) is { Length: > 0 } probed)
            return probed;
        return DefaultLocalUrl(p);
    }

    public string GetSavedModel(CloudTtsProvider p)
    {
        string pKey = CloudVoiceEntry.ProviderKey(p);
        if (_library.Prefs.CloudVoiceModels.TryGetValue(pKey, out var m) && !string.IsNullOrWhiteSpace(m))
            return m.Trim();
        if (LocalVoiceProbe.Shared.DiscoveredModel(p) is { Length: > 0 } probed)
            return probed;
        return DefaultLocalModel(p);
    }

    public bool IsAutoDiscovered(CloudTtsProvider p)
    {
        if (!IsLocalProvider(p)) return false;
        string pKey = CloudVoiceEntry.ProviderKey(p);
        if (_library.Prefs.CloudVoiceDismissed.Contains(pKey)) return false;
        return LocalVoiceProbe.Shared.IsLive(p);
    }

    public bool IsConfigured(CloudTtsProvider provider)
    {
        if (provider == CloudTtsProvider.Edge)
            return _library.Secrets.Get("tts.edge.dismissed") != "1";
        if (provider == CloudTtsProvider.Free)
            return _library.Secrets.Get("tts.free.dismissed") != "1";
        if (IsLocalProvider(provider))
        {
            string pKey = CloudVoiceEntry.ProviderKey(provider);
            bool explicitOn = _library.Prefs.CloudVoiceEnabled.Contains(pKey) && !string.IsNullOrWhiteSpace(GetSavedUrl(provider));
            return explicitOn || IsAutoDiscovered(provider);
        }
        string key = GetApiKey(provider);
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
        switch (provider)
        {
            case CloudTtsProvider.Edge:
                return EdgeBuiltinVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Locale, v.Gender, v.Age))
                    .ToArray();

            case CloudTtsProvider.Free:
                return FreeBuiltinVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Locale, v.Gender, null))
                    .ToArray();

            case CloudTtsProvider.Volcengine:
                return VolcengineBuiltinVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Locale, v.Gender, null))
                    .ToArray();

            case CloudTtsProvider.Fish:
            {
                var saved = SavedTuples(provider, pKey);
                if (saved.Count > 0) return saved;
                return FishBuiltinVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Locale, v.Gender, null))
                    .ToArray();
            }

            case CloudTtsProvider.OpenAi:
                return OpenAiBuiltinVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, char.ToUpperInvariant(v.Id[0]) + v.Id[1..], null, v.Gender, null))
                    .ToArray();

            case CloudTtsProvider.Xiaomi:
                return XiaomiVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Lang, v.Gender, v.Age))
                    .ToArray();

            case CloudTtsProvider.DashScope:
                return DashScopeVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, null, v.Gender, v.Age))
                    .ToArray();

            case CloudTtsProvider.SiliconFlow:
                return SavedTuples(provider, pKey)
                    .Concat(SiliconFlowPresets.Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, null, v.Gender, v.Age)))
                    .ToArray();

            case CloudTtsProvider.MiniMax:
                return SavedTuples(provider, pKey)
                    .Concat(MiniMaxPresets.Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, null, v.Gender, v.Age)))
                    .ToArray();

            case CloudTtsProvider.Deepgram:
                return DeepgramVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, "en-US", v.Gender, v.Age))
                    .ToArray();

            case CloudTtsProvider.Azure:
            {
                var saved = SavedTuples(provider, pKey);
                if (saved.Count > 0) return saved;
                return DefaultAzureVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, v.Locale, v.Gender, null))
                    .ToArray();
            }

            case CloudTtsProvider.ElevenLabs:
                return SavedTuples(provider, pKey);

            default:
            {
                var probed = LocalVoiceProbe.Shared.DiscoveredVoices(provider);
                if (probed.Count > 0)
                    return probed.Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, null, string.IsNullOrEmpty(v.Gender) ? null : v.Gender, null)).ToArray();
                var saved = SavedTuples(provider, pKey);
                if (saved.Count > 0) return saved;
                return DefaultLocalVoices
                    .Select(v => new CloudVoiceEntry(provider, v.Id, v.Name, null, string.IsNullOrEmpty(v.Gender) ? null : v.Gender, null))
                    .ToArray();
            }
        }
    }

    private IReadOnlyList<CloudVoiceEntry> SavedTuples(CloudTtsProvider provider, string pKey)
    {
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
        return Array.Empty<CloudVoiceEntry>();
    }

    public IReadOnlyList<CloudVoiceEntry> GetAllConfiguredVoices()
    {
        var list = new List<CloudVoiceEntry>();
        foreach (var p in AllProviders)
        {
            if (IsConfigured(p)) list.AddRange(GetVoices(p));
        }
        return list;
    }

    public async Task<(bool Ok, int Count, string Message)> RefreshVoicesAsync(CloudTtsProvider provider, CancellationToken ct = default)
    {
        string pKey = CloudVoiceEntry.ProviderKey(provider);
        string apiKey = GetApiKey(provider);
        string name = ProviderDisplayName(provider, _library.Prefs);
        if (NeedsKey(provider) && string.IsNullOrEmpty(apiKey))
            return (false, 0, $"请先填写 {name} API Key");

        try
        {
            switch (provider)
            {
                case CloudTtsProvider.Edge:
                {
                    _library.Secrets.Set("tts.edge.dismissed", "");
                    byte[] mp3 = await FetchEdgeAudioAsync("zh-CN-XiaoxiaoNeural", "你好，我来给你读这本书。", "zh-CN", ct);
                    if (mp3.Length < 128)
                        return (false, 0, "微软 Edge 语音线路当前繁忙，请稍后再试");
                    return (true, EdgeBuiltinVoices.Length, $"已连通微软 Edge 神经语音（{EdgeBuiltinVoices.Length} 个音色可用）");
                }

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
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    return (true, OpenAiBuiltinVoices.Length, $"已连通 OpenAI TTS（{OpenAiBuiltinVoices.Length} 个内置音色可用）");
                }

                case CloudTtsProvider.Xiaomi:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.xiaomimimo.com/v1/models");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    return (true, XiaomiVoices.Length, $"已连通小米 MiMo（{XiaomiVoices.Length} 个音色可用）");
                }

                case CloudTtsProvider.SiliconFlow:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.siliconflow.cn/v1/audio/voice/list");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var custom = new List<string[]>();
                    if (doc.RootElement.TryGetProperty("results", out var resArr) && resArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in resArr.EnumerateArray())
                        {
                            string uri = item.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(uri)) continue;
                            string mdl = item.TryGetProperty("model", out var m) && !string.IsNullOrWhiteSpace(m.GetString())
                                ? m.GetString()! : "FunAudioLLM/CosyVoice2-0.5B";
                            string cname = item.TryGetProperty("customName", out var cn) && !string.IsNullOrWhiteSpace(cn.GetString())
                                ? cn.GetString()! : uri;
                            custom.Add([$"{mdl}|{uri}", $"{cname} · 我的克隆", "", "", ""]);
                        }
                    }
                    _library.Prefs.CachedCloudVoices[pKey] = custom;
                    _library.SavePrefs();
                    int total = SiliconFlowPresets.Length + custom.Count;
                    return (true, total, $"已连通硅基流动（{SiliconFlowPresets.Length} 预设 + {custom.Count} 克隆音色）");
                }

                case CloudTtsProvider.DashScope:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://dashscope.aliyuncs.com/compatible-mode/v1/models");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    return (true, DashScopeVoices.Length, $"已连通阿里云百炼（{DashScopeVoices.Length} 个千问3-TTS 音色）");
                }

                case CloudTtsProvider.MiniMax:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minimaxi.com/v1/t2a_v2");
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    req.Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        model = "speech-02-turbo",
                        text = "好",
                        stream = false,
                        voice_setting = new { voice_id = "female-tianmei" },
                        audio_setting = new { sample_rate = 24000, bitrate = 32000, format = "mp3", channel = 1 },
                        output_format = "hex",
                    }), Encoding.UTF8, "application/json");
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    string json = await resp.Content.ReadAsStringAsync(ct);
                    ValidateMiniMaxBaseResp(json, name);
                    return (true, MiniMaxPresets.Length, $"已连通 MiniMax（{MiniMaxPresets.Length} 个高拟真音色）");
                }

                case CloudTtsProvider.Azure:
                {
                    string region = _library.Prefs.CloudVoiceAzureRegion.Trim();
                    if (string.IsNullOrEmpty(region)) return (false, 0, "请填写 Azure 区域（如 eastasia）");

                    using var req = new HttpRequestMessage(HttpMethod.Get, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
                    req.Headers.Add("Ocp-Apim-Subscription-Key", apiKey);
                    req.Headers.UserAgent.ParseAdd("Yuyue");
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));

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

                case CloudTtsProvider.ElevenLabs:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
                    req.Headers.Add("xi-api-key", apiKey);
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));

                    string json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var rows = new List<string[]>();
                    if (doc.RootElement.TryGetProperty("voices", out var vArr) && vArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in vArr.EnumerateArray())
                        {
                            string vid = el.TryGetProperty("voice_id", out var vp) ? vp.GetString() ?? "" : "";
                            string vname = el.TryGetProperty("name", out var np) ? np.GetString() ?? vid : vid;
                            if (string.IsNullOrEmpty(vid)) continue;
                            string gender = "", age = "";
                            if (el.TryGetProperty("labels", out var lbl) && lbl.ValueKind == JsonValueKind.Object)
                            {
                                if (lbl.TryGetProperty("gender", out var g)) gender = (g.GetString() ?? "").ToLowerInvariant();
                                if (lbl.TryGetProperty("age", out var a)) age = (a.GetString() ?? "").ToLowerInvariant();
                            }
                            rows.Add([vid, vname, "", gender, age]);
                        }
                    }
                    _library.Prefs.CachedCloudVoices[pKey] = rows;
                    _library.SavePrefs();
                    return (true, rows.Count, $"已连通 ElevenLabs（共拉取 {rows.Count} 个音色）");
                }

                case CloudTtsProvider.Deepgram:
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepgram.com/v1/projects");
                    req.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
                    using var resp = await Http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) return (false, 0, await FormatHttpErrorAsync(name, resp));
                    return (true, DeepgramVoices.Length, $"已连通 Deepgram（{DeepgramVoices.Length} 个 Aura-2 音色）");
                }

                default:
                {
                    string baseV1 = NormalizedBaseV1(GetSavedUrl(provider));
                    if (string.IsNullOrWhiteSpace(baseV1))
                        return (false, 0, $"请先填写 {name} 服务地址");
                    var info = await LocalVoiceProbe.InspectEndpointAsync(baseV1, string.IsNullOrEmpty(apiKey) ? null : apiKey, System.TimeSpan.FromSeconds(4), passiveScan: false, ct);
                    var rows = info.Voices.Select(v => new[] { v.Id, v.Name, "", v.Gender, "" }).ToList();
                    _library.Prefs.CachedCloudVoices[pKey] = rows;
                    _library.Prefs.CloudVoiceEnabled.Add(pKey);
                    _library.Prefs.CloudVoiceDismissed.Remove(pKey);
                    if (!string.IsNullOrEmpty(info.FirstModel) && !_library.Prefs.CloudVoiceModels.ContainsKey(pKey))
                        _library.Prefs.CloudVoiceModels[pKey] = info.FirstModel;
                    _library.SavePrefs();
                    LocalVoiceProbe.Shared.RecordManualSuccess(provider, baseV1, info);
                    int cnt = Math.Max(rows.Count, DefaultLocalVoices.Length);
                    return (true, cnt, $"已连通 {name}（发现 {cnt} 个音色）");
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
                ActEmotion.Weary => "depressed",
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
        string apiKey = GetApiKey(provider);
        if (NeedsKey(provider) && string.IsNullOrEmpty(apiKey)) return null;

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
        string legacyPath = GetLegacyCacheFilePath(pKey, fullVoiceId, plain, act, readingsKey);
        foreach (string hit in new[] { cachePath, legacyPath })
        {
            if (File.Exists(hit))
            {
                try
                {
                    byte[] cached = await File.ReadAllBytesAsync(hit, ct);
                    if (cached.Length > 44)
                    {
                        try { File.SetLastWriteTimeUtc(hit, DateTime.UtcNow); } catch { }
                        if (IsPaidCloud(provider)) _library.Usage.RecordTts(plain.Length, cached: true);
                        return cached;
                    }
                }
                catch { }
            }
        }

        if (IsPaidCloud(provider))
            _library.Usage.CheckTtsBudget(plain.Length);

        byte[] rawAudio = await FetchProviderAudioAsync(provider, voiceId, apiKey, plain, language, parts, act, ct);
        if (IsPaidCloud(provider))
            _library.Usage.RecordTts(plain.Length, cached: false);

        byte[] wavBytes = await EnsureWavBytesAsync(rawAudio, ct);
        if (wavBytes.Length > 44)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                string tmp = cachePath + ".part";
                await File.WriteAllBytesAsync(tmp, wavBytes, ct);
                File.Move(tmp, cachePath, overwrite: true);
                TrimPersistentCacheIfNeeded();
            }
            catch { }
        }
        return wavBytes;
    }

    private async Task<byte[]> FetchEdgeAudioAsync(string voiceId, string plain, string language, CancellationToken ct)
    {
        if (_library.Prefs.CloudVoiceBaseUrls.TryGetValue("edge", out var customBase) && !string.IsNullOrWhiteSpace(customBase))
        {
            string baseV1 = NormalizedBaseV1(customBase);
            string mdl = _library.Prefs.CloudVoiceModels.TryGetValue("edge", out var cm) && !string.IsNullOrWhiteSpace(cm)
                ? cm.Trim()
                : "tts-1";
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseV1}/audio/speech");
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = mdl,
                voice = voiceId,
                input = plain,
                response_format = "mp3",
            }), Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync("微软 Edge", resp));
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }

        return await EdgeTtsClient.SynthesizeMp3Async(plain, voiceId, language, ct);
    }

    private async Task<byte[]> FetchProviderAudioAsync(
        CloudTtsProvider provider,
        string voiceId,
        string apiKey,
        string plain,
        string language,
        SpokenPart[]? parts,
        CloudVoiceActing act,
        CancellationToken ct)
    {
        string name = ProviderDisplayName(provider, _library.Prefs);
        switch (provider)
        {
            case CloudTtsProvider.Edge:
                return await FetchEdgeAudioAsync(voiceId, plain, language, ct);

            case CloudTtsProvider.Free:
            {
                byte[]? freeWav = await FetchFreeOnlineWavAsync(voiceId, plain, language, ct);
                if (freeWav is null || freeWav.Length <= 44)
                    throw new InvalidOperationException("在线公共线路当前繁忙，请稍后再试或切换其他声音");
                return freeWav;
            }

            case CloudTtsProvider.Volcengine:
            {
                var (appId, token) = ResolveVolcengineCredentials(apiKey);
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(token))
                    throw new InvalidOperationException("请先填写火山引擎 AppID 与 Access Token");
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
                if (string.IsNullOrEmpty(b64)) throw new InvalidOperationException("火山豆包返回空音频数据");
                return Convert.FromBase64String(b64);
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
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }

            case CloudTtsProvider.Xiaomi:
            {
                var msgs = new List<object>();
                if (!string.IsNullOrWhiteSpace(act.Instruction))
                    msgs.Add(new { role = "user", content = act.Instruction });
                msgs.Add(new { role = "assistant", content = plain });
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.xiaomimimo.com/v1/chat/completions");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = "mimo-v2.5-tts",
                    messages = msgs,
                    audio = new { format = "wav", voice = voiceId },
                }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                string? b64 = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("audio")
                    .GetProperty("data")
                    .GetString();
                if (string.IsNullOrWhiteSpace(b64)) throw new InvalidOperationException("小米 MiMo 返回空音频数据");
                return Convert.FromBase64String(b64);
            }

            case CloudTtsProvider.SiliconFlow:
            {
                var (modelName, actualVoice) = SplitSiliconFlowVoice(voiceId);
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.siliconflow.cn/v1/audio/speech");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = modelName,
                    voice = actualVoice,
                    input = plain,
                    response_format = "wav",
                }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }

            case CloudTtsProvider.DashScope:
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = "qwen3-tts-flash",
                    input = new { text = plain, voice = voiceId },
                }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var audioObj = doc.RootElement.GetProperty("output").GetProperty("audio");
                if (audioObj.TryGetProperty("data", out var dataProp) && dataProp.GetString() is { Length: > 0 } b64)
                    return Convert.FromBase64String(b64);
                if (audioObj.TryGetProperty("url", out var urlProp) && urlProp.GetString() is { Length: > 0 } audioUrl)
                {
                    string secureUrl = audioUrl.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
                    return await Http.GetByteArrayAsync(secureUrl, ct);
                }
                throw new InvalidOperationException("阿里云百炼返回空音频数据");
            }

            case CloudTtsProvider.MiniMax:
            {
                var voiceSetting = new Dictionary<string, object>
                {
                    ["voice_id"] = voiceId,
                    ["speed"] = 1.0,
                    ["vol"] = 1.0,
                    ["pitch"] = 0,
                };
                if (MiniMaxEmotion(act) is { Length: > 0 } emo)
                    voiceSetting["emotion"] = emo;

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.minimaxi.com/v1/t2a_v2");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model = "speech-02-hd",
                    text = plain,
                    stream = false,
                    voice_setting = voiceSetting,
                    audio_setting = new { sample_rate = 24000, bitrate = 64000, format = "wav", channel = 1 },
                    output_format = "hex",
                }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                string json = await resp.Content.ReadAsStringAsync(ct);
                ValidateMiniMaxBaseResp(json, name);
                using var doc = JsonDocument.Parse(json);
                string audioStr = doc.RootElement.GetProperty("data").GetProperty("audio").GetString() ?? "";
                if (audioStr.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return await Http.GetByteArrayAsync(audioStr, ct);
                return Convert.FromHexString(audioStr);
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
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                return await resp.Content.ReadAsByteArrayAsync(ct);
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
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }

            case CloudTtsProvider.ElevenLabs:
            {
                bool hasTags = !(string.IsNullOrEmpty(act.Head) && string.IsNullOrEmpty(act.Tail));
                string modelId = hasTags ? "eleven_v3" : "eleven_flash_v2_5";
                string inputText = hasTags ? $"{act.Head}{plain}{act.Tail}" : plain;

                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=pcm_24000");
                req.Headers.Add("xi-api-key", apiKey);
                req.Content = new StringContent(JsonSerializer.Serialize(new { text = inputText, model_id = modelId }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                byte[] pcm = await resp.Content.ReadAsByteArrayAsync(ct);
                return WrapPcm16MonoInWav(pcm, 24_000);
            }

            case CloudTtsProvider.Deepgram:
            {
                string url = $"https://api.deepgram.com/v1/speak?model={Uri.EscapeDataString(voiceId)}&encoding=linear16&container=wav&sample_rate=24000";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.TryAddWithoutValidation("Authorization", $"Token {apiKey}");
                req.Content = new StringContent(JsonSerializer.Serialize(new { text = plain }), Encoding.UTF8, "application/json");
                using var resp = await Http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }

            default:
            {
                string? endpoint = SpeechEndpoint(GetSavedUrl(provider));
                if (string.IsNullOrEmpty(endpoint))
                    throw new InvalidOperationException($"还没填 {name} 的服务地址");

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
                if (!string.IsNullOrEmpty(apiKey))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                var body = new Dictionary<string, object>
                {
                    ["model"] = GetSavedModel(provider),
                    ["voice"] = voiceId,
                    ["input"] = plain,
                    ["response_format"] = "wav",
                };
                if (!string.IsNullOrWhiteSpace(act.Instruction))
                    body["instructions"] = act.Instruction;
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

                HttpResponseMessage resp;
                try { resp = await LocalHttp.SendAsync(req, ct); }
                catch { throw new InvalidOperationException($"连不上 {name}，请确认本地语音服务已启动"); }
                using (resp)
                {
                    if (!resp.IsSuccessStatusCode) throw new InvalidOperationException(await FormatHttpErrorAsync(name, resp));
                    return await resp.Content.ReadAsByteArrayAsync(ct);
                }
            }
        }
    }

    private static async Task<byte[]> EnsureWavBytesAsync(byte[] data, CancellationToken ct)
    {
        if (data.Length < 12) return data;
        string ext = SniffExt(data, "wav");
        if (ext == "wav") return data;

        // Decode MP3/OGG/FLAC returned by a provider into 24 kHz mono 16-bit PCM WAV via Windows Media Foundation
        string tmpDir = Path.Combine(Path.GetTempPath(), "yuyue-tts-decode");
        Directory.CreateDirectory(tmpDir);
        string id = Guid.NewGuid().ToString("N");
        string inFile = Path.Combine(tmpDir, $"{id}.{ext}");
        string outFile = Path.Combine(tmpDir, $"{id}.wav");
        try
        {
            await File.WriteAllBytesAsync(inFile, data, ct);
            var src = await StorageFile.GetFileFromPathAsync(inFile);
            var folder = await StorageFolder.GetFolderFromPathAsync(tmpDir);
            var dst = await folder.CreateFileAsync($"{id}.wav", CreationCollisionOption.ReplaceExisting);
            var profile = new MediaEncodingProfile
            {
                Audio = AudioEncodingProperties.CreatePcm(24_000, 1, 16),
                Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Wave },
            };
            var transcoder = new MediaTranscoder();
            var prep = await transcoder.PrepareFileTranscodeAsync(src, dst, profile);
            if (!prep.CanTranscode) return data;
            await prep.TranscodeAsync().AsTask(ct);
            return await File.ReadAllBytesAsync(outFile, ct);
        }
        catch
        {
            return data;
        }
        finally
        {
            try { File.Delete(inFile); } catch { }
            try { File.Delete(outFile); } catch { }
        }
    }

    public async Task PrefetchAsync(string fullVoiceId, IEnumerable<string> sentences, string language, CancellationToken ct)
    {
        if (!TryParseFullId(fullVoiceId, out var provider, out _)) return;
        string pKey = CloudVoiceEntry.ProviderKey(provider);
        var act = new CloudVoiceActing();
        var pending = sentences
            .Where(s => CardVoiceEngine.PlainSpokenText(s).Any(char.IsLetterOrDigit))
            .Where(s => !HasCached(pKey, fullVoiceId, CardVoiceEngine.PlainSpokenText(s), act, ""))
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
                break;
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

    private bool HasCached(string providerKey, string fullVoiceId, string plainText, CloudVoiceActing act, string readings) =>
        File.Exists(GetCacheFilePath(providerKey, fullVoiceId, plainText, act, readings))
        || File.Exists(GetLegacyCacheFilePath(providerKey, fullVoiceId, plainText, act, readings));

    private string GetCacheFilePath(string providerKey, string fullVoiceId, string plainText, CloudVoiceActing act, string readings)
    {
        string hex32 = ComputeCacheHash(fullVoiceId, plainText, act, readings);
        return Path.Combine(PersistentCacheDir, providerKey, $"{hex32}.wav");
    }

    private string GetLegacyCacheFilePath(string providerKey, string fullVoiceId, string plainText, CloudVoiceActing act, string readings)
    {
        string hex32 = ComputeCacheHash(fullVoiceId, plainText, act, readings);
        return Path.Combine(_library.DataDir, "cache", "yuyue-voices", providerKey, $"{hex32}.wav");
    }

    private static string ComputeCacheHash(string fullVoiceId, string plainText, CloudVoiceActing act, string readings)
    {
        string cacheKey = (act.IsFlat && string.IsNullOrEmpty(readings))
            ? plainText
            : $"{plainText}\u0002{act.CacheKeyPart}\u0002{readings}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{fullVoiceId}\u0001{cacheKey}"));
        return Convert.ToHexString(digest, 0, 16).ToLowerInvariant();
    }

    private void TrimPersistentCacheIfNeeded()
    {
        if (Interlocked.Increment(ref _trimCounter) % 32 != 0) return;
        string root = PersistentCacheDir;
        _ = Task.Run(() => TrimCacheDirectoryToCap(root, MaxPersistentCacheBytes));
    }

    /// <summary>
    /// Evicts oldest-modified files from <paramref name="rootDir"/> down to 80% of <paramref name="capBytes"/>
    /// whenever total size exceeds <paramref name="capBytes"/> (1:1 <c>CloudVoice.trimPersistentCacheIfNeeded</c>).
    /// </summary>
    public static int TrimCacheDirectoryToCap(string rootDir, long capBytes)
    {
        if (!Directory.Exists(rootDir) || capBytes <= 0) return 0;
        try
        {
            var files = new DirectoryInfo(rootDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Select(fi => (fi.FullName, fi.Length, fi.LastWriteTimeUtc))
                .ToList();
            long total = files.Sum(f => f.Length);
            if (total <= capBytes) return 0;

            long target = (long)(capBytes * 0.8);
            int removed = 0;
            foreach (var f in files.OrderBy(x => x.LastWriteTimeUtc))
            {
                if (total <= target) break;
                try
                {
                    File.Delete(f.FullName);
                    total -= f.Length;
                    removed++;
                }
                catch { }
            }
            return removed;
        }
        catch { return 0; }
    }

    public static string? SpeechEndpoint(string rawBase)
    {
        string s = (rawBase ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(s)) return null;
        if (s.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase)) return s;
        if (!s.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) && !s.Contains("/v1/", StringComparison.OrdinalIgnoreCase))
            s += "/v1";
        return s + "/audio/speech";
    }

    public static string NormalizedBaseV1(string rawBase)
    {
        string s = (rawBase ?? "").Trim().TrimEnd('/');
        if (s.EndsWith("/audio/speech", StringComparison.OrdinalIgnoreCase))
            s = s[..^"/audio/speech".Length].TrimEnd('/');
        if (!string.IsNullOrEmpty(s) && !s.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            s += "/v1";
        return s;
    }

    internal static (string Model, string Voice) SplitSiliconFlowVoice(string id)
    {
        int bar = id.IndexOf('|');
        if (bar > 0) return (id[..bar], id[(bar + 1)..]);
        int colon = id.IndexOf(':');
        if (colon > 0 && (id.StartsWith("FunAudioLLM/", StringComparison.Ordinal) || id.StartsWith("fishaudio/", StringComparison.Ordinal) || id.StartsWith("IndexTeam/", StringComparison.Ordinal)))
            return (id[..colon], id);
        return ("FunAudioLLM/CosyVoice2-0.5B", id);
    }

    internal static string? MiniMaxEmotion(CloudVoiceActing? act)
    {
        if (act is null) return null;
        string s = $"{act.Instruction} {act.Style ?? ""}".ToLowerInvariant();
        if (s.Contains("happy") || s.Contains("cheer") || s.Contains('欢') || s.Contains('喜') || s.Contains('笑')) return "happy";
        if (s.Contains("sad") || s.Contains("depressed") || s.Contains('悲') || s.Contains('伤') || s.Contains('哭') || s.Contains("哽咽")) return "sad";
        if (s.Contains("angry") || s.Contains('怒') || s.Contains('气') || s.Contains('吼')) return "angry";
        if (s.Contains("fear") || s.Contains('恐') || s.Contains('怕') || s.Contains('颤')) return "fearful";
        if (s.Contains("calm") || s.Contains("gentle") || s.Contains('平') || s.Contains('柔')) return "calm";
        return null;
    }

    private static void ValidateMiniMaxBaseResp(string json, string providerName)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("base_resp", out var br) &&
            br.TryGetProperty("status_code", out var sc) &&
            sc.ValueKind == JsonValueKind.Number &&
            sc.GetInt32() is int status && status != 0)
        {
            string msg = br.TryGetProperty("status_msg", out var sm) ? sm.GetString() ?? "" : "";
            if (status is 1004 or 2049) throw new InvalidOperationException($"{providerName} API Key 无效 ({status})");
            if (status is 1008 or 1026) throw new InvalidOperationException($"{providerName} 账户余额不足 ({status})");
            throw new InvalidOperationException($"{providerName} 返回错误 ({status})：{msg}");
        }
    }

    public static string SniffExt(byte[] data, string fallback)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46) return "wav";
            if (data[0] == 0x66 && data[1] == 0x4C && data[2] == 0x61 && data[3] == 0x43) return "flac";
            if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53) return "ogg";
            if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) return "mp3";
            if (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0) return "mp3";
        }
        return fallback;
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
        System.Buffer.BlockCopy(pcmBytes, 0, wav, 44, pcmBytes.Length);
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
