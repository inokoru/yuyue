using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Yuyue.Core;

public sealed record AiProviderPreset(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    bool NeedsKey,
    string Kind = "openai",
    string Group = "key",
    string KeyHint = "sk-…",
    string? Blurb = null,
    string Aliases = "",
    string DefaultKey = "",
    string DefaultFastModel = "",
    string KeyUrl = "");

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/Companion.swift</c>, <c>CompanionProviders.swift</c>,
/// <c>OpenRouterSignIn.swift</c> &amp; <c>Reader/BookCompanion.swift</c>:
/// unified LLM client for 书伴, 广播剧认人, 错字整理, 前情提要 &amp; 播客编辑部 with:
/// <list type="bullet">
/// <item>OpenRouter PKCE OAuth sign-in, Anthropic native <c>/v1/messages</c>, and OpenAI-compatible <c>/chat/completions</c></item>
/// <item>Fast-tier model routing with automatic HTTP 400/404 fallback to primary model</item>
/// <item>Anthropic &amp; OpenRouter Claude <c>cache_control: ephemeral</c> prompt caching (<see cref="SplitUtf8Prefix"/>)</item>
/// <item>30-day token usage accounting (<see cref="CompanionUsageLedger"/>) &amp; daily budget cap enforcement</item>
/// <item>Persistent per-book Q&amp;A disk cache (<c>companion/{bookId}.json</c>), dynamic <c>接着问</c> parsing &amp; <c>[n]</c> citations</item>
/// </list>
/// </summary>
public sealed class CompanionClient
{
    public static readonly IReadOnlyList<AiProviderPreset> Presets =
    [
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openrouter/auto", true,
            Group: "signIn", KeyHint: "sk-or-…", Blurb: "登录就能用，几百个模型一个账号",
            KeyUrl: "https://openrouter.ai/keys"),
        new("claude", "Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-5", true,
            Kind: "anthropic", KeyHint: "sk-ant-…", Aliases: "anthropic 克劳德",
            DefaultFastModel: "claude-haiku-4-5", KeyUrl: "https://console.anthropic.com/settings/keys"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-5-mini", true,
            Aliases: "chatgpt gpt", DefaultFastModel: "gpt-5-mini", KeyUrl: "https://platform.openai.com/api-keys"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", true,
            KeyHint: "AIza…", Aliases: "google 谷歌", DefaultFastModel: "gemini-2.5-flash", KeyUrl: "https://aistudio.google.com/apikey"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true,
            Aliases: "深度求索", DefaultFastModel: "deepseek-chat", KeyUrl: "https://platform.deepseek.com/api_keys"),
        new("dashscope", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", true,
            Aliases: "qwen 阿里 百炼 dashscope", DefaultFastModel: "qwen-turbo", KeyUrl: "https://bailian.console.aliyun.com/"),
        new("moonshot", "Kimi", "https://api.moonshot.cn/v1", "kimi-k2-turbo-preview", true,
            Aliases: "moonshot 月之暗面", DefaultFastModel: "kimi-k2-turbo-preview", KeyUrl: "https://platform.moonshot.cn/console/api-keys"),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.6", true,
            Aliases: "zhipu glm z.ai 清言", DefaultFastModel: "glm-4-flash", KeyUrl: "https://open.bigmodel.cn/usercenter/apikeys"),
        new("volcengine", "豆包", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-250615", true,
            Aliases: "doubao 火山引擎 volcengine 字节", DefaultFastModel: "doubao-seed-1-6-flash-250615", KeyUrl: "https://console.volcengine.com/ark/region:ark+cn-beijing/apiKey"),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", true,
            Aliases: "siliconflow", DefaultFastModel: "deepseek-ai/DeepSeek-V3", KeyUrl: "https://cloud.siliconflow.cn/account/ak"),
        new("minimax", "MiniMax", "https://api.minimaxi.com/v1", "MiniMax-M2", true,
            Aliases: "海螺 稀宇", DefaultFastModel: "MiniMax-M2", KeyUrl: "https://platform.minimaxi.com/user-center/basic-information/interface-key"),
        new("xai", "Grok", "https://api.x.ai/v1", "grok-4", true,
            KeyHint: "xai-…", Aliases: "xai", DefaultFastModel: "grok-3-mini", KeyUrl: "https://console.x.ai/"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true,
            KeyHint: "", DefaultFastModel: "mistral-small-latest", KeyUrl: "https://console.mistral.ai/api-keys"),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true,
            KeyHint: "gsk_…", DefaultFastModel: "llama-3.1-8b-instant", KeyUrl: "https://console.groq.com/keys"),
        new("magpie", "magpie", "http://127.0.0.1:3425/v1", "claude-sonnet-4-5", false,
            Group: "local", KeyHint: "局域网里用才要填网关 Key",
            Blurb: "转接你在 magpie 里登录的 Claude、ChatGPT 账号", Aliases: "喜鹊 网关 gateway", DefaultKey: "magpie"),
        new("ollama", "Ollama", "http://localhost:11434/v1", "qwen2.5:7b", false,
            Group: "local", KeyHint: "不用填"),
        new("lmstudio", "LM Studio", "http://localhost:1234/v1", "qwen2.5-7b-instruct", false,
            Group: "local", KeyHint: "不用填"),
        new("custom", "自定义 OpenAI 接口", "", "", false,
            Group: "key", KeyHint: "可不填"),
    ];

    private static readonly Regex CitationRx = new(@"\[(\d+)\]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions CacheJsonOpts = new() { WriteIndented = false };
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(240) };

    private readonly LibraryService _library;

    public CompanionClient(LibraryService library)
    {
        _library = library;
    }

    public static AiProviderPreset? FindPreset(string id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ReplyLanguage => L10n.IsEnglish ? "英文（English）" : "简体中文";

    public bool IsPaidCloudProvider(string? providerId = null, string? baseUrl = null)
    {
        string id = providerId ?? _library.Prefs.AiProvider;
        var preset = FindPreset(id);
        if (preset is not null)
            return preset.Group != "local";
        string u = (baseUrl ?? _library.Prefs.AiBaseUrl ?? "").ToLowerInvariant();
        if (u.Contains("localhost") || u.Contains("127.0.0.1") || u.Contains("192.168.") || u.Contains("10.0."))
            return false;
        return true;
    }

    public bool IsCurrentProviderPaidCloud => IsPaidCloudProvider(_library.Prefs.AiProvider, _library.Prefs.AiBaseUrl);

    public string CurrentProviderLabel
    {
        get
        {
            var p = _library.Prefs;
            var preset = FindPreset(p.AiProvider);
            string title = preset?.Name ?? (p.AiCustomProviderNames.TryGetValue(p.AiProvider, out var cn) ? cn : p.AiProvider);
            string model = !string.IsNullOrWhiteSpace(p.AiModel) ? p.AiModel.Trim() : (preset?.DefaultModel ?? "");
            return string.IsNullOrEmpty(model) ? title : $"{title} · {model}";
        }
    }

    /// <summary>
    /// 1:1 <c>CompanionConfig.resolvedFastModel</c>: picks the lighter/cheaper model for bulk tasks
    /// (drama speaker identification, homophone polish, recap), or <c>null</c> when already lightweight.
    /// </summary>
    public string? ResolvedFastModel(string? providerId = null, string? primaryModel = null)
    {
        string id = providerId ?? _library.Prefs.AiProvider;
        var preset = FindPreset(id);
        string model = (primaryModel ?? _library.Prefs.AiModel ?? preset?.DefaultModel ?? "").Trim();
        if (string.IsNullOrEmpty(model)) return null;

        if (_library.Prefs.AiProviderFastModels.TryGetValue(id, out var manual) && !string.IsNullOrWhiteSpace(manual))
        {
            string m = manual.Trim();
            return string.Equals(m, model, StringComparison.Ordinal) ? null : m;
        }

        string lower = model.ToLowerInvariant();
        if (lower.Contains("haiku") || lower.Contains("mini") || lower.Contains("nano")
            || lower.Contains("flash") || lower.Contains("turbo") || lower.Contains("small")
            || lower.Contains("instant") || lower.Contains("lite"))
            return null;

        if (string.Equals(id, "openrouter", StringComparison.OrdinalIgnoreCase))
        {
            if (lower.StartsWith("anthropic/claude-", StringComparison.Ordinal)) return "anthropic/claude-haiku-4-5";
            if (lower.StartsWith("openai/gpt-5", StringComparison.Ordinal)) return "openai/gpt-5-mini";
            if (lower.StartsWith("openai/gpt-4o", StringComparison.Ordinal)) return "openai/gpt-4o-mini";
            if (lower.StartsWith("google/gemini-2.5-pro", StringComparison.Ordinal) || lower.StartsWith("google/gemini-pro", StringComparison.Ordinal)) return "google/gemini-2.5-flash";
            if (lower.StartsWith("deepseek/deepseek-r1", StringComparison.Ordinal)) return "deepseek/deepseek-chat";
            return null;
        }

        string? cand = preset?.DefaultFastModel;
        return !string.IsNullOrWhiteSpace(cand) && !string.Equals(cand, model, StringComparison.Ordinal) ? cand : null;
    }

    public bool IsConfigured
    {
        get
        {
            var p = _library.Prefs;
            if (string.IsNullOrWhiteSpace(p.AiBaseUrl) || string.IsNullOrWhiteSpace(p.AiModel)) return false;
            var preset = FindPreset(p.AiProvider);
            if (preset is { NeedsKey: false } || p.AiProvider.StartsWith("custom.", StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(_library.Secrets.Get("ai.apiKey"))
                || !string.IsNullOrWhiteSpace(_library.Secrets.Get($"ai.key.{p.AiProvider}"));
        }
    }

    public sealed record CompanionSource(int Index, uint Section, uint Sentence, string Snippet, bool Later = false, string? Chapter = null);
    public sealed record CompanionAnswer(
        string Text,
        IReadOnlyList<CompanionSource> Sources,
        bool NotInRead,
        IReadOnlyList<string>? NextQuestions = null,
        string? ProviderLabel = null);

    private sealed class CachedQaEntry
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("sources")]
        public Dictionary<string, CachedQaSource> Sources { get; set; } = new();

        [JsonPropertyName("notYet")]
        public bool NotYet { get; set; }

        [JsonPropertyName("next")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Next { get; set; }

        [JsonPropertyName("label")]
        public string Label { get; set; } = "";
    }

    private sealed class CachedQaSource
    {
        [JsonPropertyName("section")]
        public uint Section { get; set; }

        [JsonPropertyName("sentence")]
        public uint Sentence { get; set; }

        [JsonPropertyName("chapter")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Chapter { get; set; }

        [JsonPropertyName("quote")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Quote { get; set; }

        [JsonPropertyName("later")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Later { get; set; }
    }

    public async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        try
        {
            var reply = await ChatAsync("用一个词回答：天空通常是什么颜色？", "你是一个简短的助手。", maxTokens: 64, source: UsageSource.Other);
            return (true, $"连接成功：{reply.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, FormatErrorMessage(ex));
        }
    }

    /// <summary>
    /// Tests a specific provider's endpoint/key/model WITHOUT switching the user's active companion provider.
    /// </summary>
    public async Task<(bool Ok, string Message)> TestProviderAsync(string providerId, string baseUrl, string model, string apiKey)
    {
        try
        {
            var preset = FindPreset(providerId);
            string url = !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl.Trim().TrimEnd('/') : (preset?.BaseUrl?.TrimEnd('/') ?? "");
            string mdl = !string.IsNullOrWhiteSpace(model) ? model.Trim() : (preset?.DefaultModel ?? "");
            string key = !string.IsNullOrWhiteSpace(apiKey) ? apiKey.Trim() : (preset?.DefaultKey ?? "");
            if (string.IsNullOrEmpty(url)) return (false, "未填写接口地址");
            if (string.IsNullOrEmpty(mdl)) return (false, "未选择模型");

            bool isAnthropic = preset?.Kind == "anthropic" || url.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
            bool paid = IsPaidCloudProvider(providerId, url);
            string text = isAnthropic
                ? await SendAnthropicAsync(url, mdl, key, "你是一个简短的助手。", "用一个词回答：天空通常是什么颜色？", 64, 0, paid, UsageSource.Other, default)
                : await SendOpenAiAsync(providerId, url, mdl, key, "你是一个简短的助手。", "用一个词回答：天空通常是什么颜色？", 64, 0, paid, UsageSource.Other, default);
            return (true, $"连接成功：{text.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, FormatErrorMessage(ex));
        }
    }

    /// <summary>
    /// Probes local Ollama (:11434), LM Studio (:1234), and magpie (:3425) endpoints in parallel (500 ms timeout)
    /// so Settings can highlight live local LLM servers automatically.
    /// </summary>
    public static async Task<Dictionary<string, IReadOnlyList<string>>> ProbeLocalProvidersAsync(CancellationToken ct = default)
    {
        var targets = new (string Id, string Url, string Key)[]
        {
            ("ollama", "http://127.0.0.1:11434/v1/models", ""),
            ("lmstudio", "http://127.0.0.1:1234/v1/models", ""),
            ("magpie", "http://127.0.0.1:3425/v1/models", "magpie"),
        };
        using var probe = new HttpClient { Timeout = System.TimeSpan.FromMilliseconds(550) };
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        var tasks = targets.Select(async t =>
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
                if (!string.IsNullOrEmpty(t.Key))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.Key);
                using var resp = await probe.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) return (t.Id, Models: (IReadOnlyList<string>?)null);
                string raw = await resp.Content.ReadAsStringAsync(ct);
                var ids = ParseModelList(raw);
                return (t.Id, Models: (IReadOnlyList<string>?)ids);
            }
            catch { return (t.Id, Models: (IReadOnlyList<string>?)null); }
        }).ToArray();

        foreach (var (id, models) in await Task.WhenAll(tasks))
        {
            if (models is not null) result[id] = models;
        }
        return result;
    }

    /// <summary>
    /// 1:1 <c>BookCompanion.ask</c>: structured companion Q&amp;A with disk cache (<c>companion/{bookId}.json</c>),
    /// chapter-annotated excerpts (<c>i-1..i+1</c>, <c>upTo+8</c>), dynamic <c>接着问</c> suggestions,
    /// numbered <c>[n]</c> citations (only keeping cited sources), and <c>Later</c> spoiler badges in whole-book mode.
    /// </summary>
    public async Task<CompanionAnswer> AskBookStructuredAsync(
        string bookId,
        string bookTitle,
        uint currentSection,
        int currentSentence,
        string question,
        string? selectedQuote = null,
        bool wholeBook = false,
        IReadOnlyList<(string Role, string Content)>? history = null,
        BookDetail? detail = null,
        double readingFraction = 0,
        CancellationToken ct = default)
    {
        string cleanQ = question.Trim();
        string scopeKey = wholeBook ? "whole" : "read";
        string cacheKey = $"{scopeKey}|{(wholeBook ? "" : currentSection.ToString())}|{selectedQuote ?? ""}|{cleanQ}";

        // Cache lookup (skip when multi-turn history exists so follow-ups reflect prior turns)
        if ((history is null || history.Count == 0) && TryLoadCachedQa(bookId, cacheKey) is { } cached)
            return cached;

        var picked = new List<(uint Section, uint Sentence)>();
        var sentCache = new Dictionary<uint, IReadOnlyList<string>>();
        IReadOnlyList<string> GetSents(uint sec)
        {
            if (!sentCache.TryGetValue(sec, out var list))
            {
                try { list = _library.GetSentences(bookId, sec); }
                catch { list = Array.Empty<string>(); }
                sentCache[sec] = list;
            }
            return list;
        }

        bool Allowed(uint sec, uint sent) =>
            wholeBook || sec < currentSection || (sec == currentSection && sent <= (uint)Math.Max(0, currentSentence + 8));

        // 1. Score keyword search hits (1:1 BookCompanion.excerpts)
        var scores = new Dictionary<(uint Sec, uint Sent), int>();
        var keywords = ExtractKeywords(cleanQ, selectedQuote).Take(8).ToArray();
        foreach (var kw in keywords)
        {
            try
            {
                var hits = _library.SearchBook(bookId, kw);
                foreach (var h in hits)
                {
                    if (!Allowed(h.Section, h.Sentence)) continue;
                    var key = (h.Section, h.Sentence);
                    scores[key] = scores.GetValueOrDefault(key) + 1;
                }
            }
            catch { }
        }

        foreach (var kv in scores.OrderByDescending(k => k.Value).ThenByDescending(k => k.Key.Sec).ThenByDescending(k => k.Key.Sent))
        {
            if (picked.Count >= 8) break;
            if (picked.Any(x => x.Section == kv.Key.Sec && Math.Abs((int)x.Sentence - (int)kv.Key.Sent) <= 2)) continue;
            picked.Add(kv.Key);
        }

        // 2. Include surrounding sentences near current reading spot (stride -4 from +3 down to -12)
        if (!wholeBook)
        {
            var curSents = GetSents(currentSection);
            int end = Math.Min(curSents.Count - 1, Math.Max(0, currentSentence) + 3);
            for (int s = end; s >= Math.Max(0, end - 12); s -= 4)
            {
                if (s < 0 || s >= curSents.Count) continue;
                if (picked.Any(x => x.Section == currentSection && Math.Abs((int)x.Sentence - s) <= 2)) continue;
                picked.Add((currentSection, (uint)s));
            }
        }
        else if (picked.Count == 0)
        {
            var curSents = GetSents(currentSection);
            if (currentSentence >= 0 && currentSentence < curSents.Count)
                picked.Add((currentSection, (uint)currentSentence));
        }

        picked.Sort((a, b) => a.Section != b.Section ? a.Section.CompareTo(b.Section) : a.Sentence.CompareTo(b.Sentence));

        var allSources = new List<CompanionSource>();
        for (int idx = 0; idx < picked.Count; idx++)
        {
            var (sec, sent) = picked[idx];
            var all = GetSents(sec);
            int i = (int)sent;
            if (i < 0 || i >= all.Count) continue;
            int from = Math.Max(0, i - 1);
            int to = Math.Min(all.Count - 1, i + 1);
            var sb = new StringBuilder();
            for (int k = from; k <= to; k++)
            {
                string part = all[k].Trim().Replace('\n', ' ');
                if (part.Length == 0) continue;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(part);
            }
            string snippet = sb.ToString();
            if (snippet.Length > 240) snippet = snippet[..240];
            bool later = wholeBook && (sec > currentSection || (sec == currentSection && sent > (uint)Math.Max(0, currentSentence + 8)));
            string? chapName = detail is not null && sec < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[sec].Title)
                ? detail.Sections[sec].Title
                : null;
            allSources.Add(new CompanionSource(allSources.Count + 1, sec, sent, snippet, later, chapName));
        }

        string authorSuffix = detail?.Summary.Authors is { Length: > 0 } a && !string.IsNullOrWhiteSpace(a[0]) ? $"（{a[0]}）" : "";
        int percent = Math.Clamp((int)Math.Round(readingFraction * 100.0), 0, 100);
        string replyLang = ReplyLanguage;

        var system = new StringBuilder();
        system.AppendLine($"你是「语阅」里的书伴，陪读者读《{bookTitle}》{authorSuffix}。");
        if (!wholeBook)
        {
            system.AppendLine($"读者现在读到全书的 {percent}%。");
            system.AppendLine("规则：");
            system.AppendLine("1. 只根据下面给出的原文片段回答。不要用你知道的、这本书后面的情节，不要剧透。");
            system.AppendLine($"2. 用{replyLang}，口语，简短，一般三到五句话。");
            system.AppendLine("3. 说到原文里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。");
            system.AppendLine("4. 如果这些片段不够回答，或者答案在读者还没读到的地方，只回答三个字：没读到");
            system.AppendLine("5. 能回答的话，最后另起一行，写「接着问：」，后面是读者可能想接着问的两个短问题，用「｜」隔开。");
        }
        else
        {
            system.AppendLine("读者想用整本书来回答，不介意提前知道后面的情节。");
            system.AppendLine("规则：");
            system.AppendLine("1. 可以用整本书的内容，也可以结合你对这本书的了解。");
            system.AppendLine($"2. 用{replyLang}，口语，简短，一般三到五句话。");
            system.AppendLine("3. 说到下面原文片段里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。");
            system.AppendLine("4. 最后另起一行，写「接着问：」，后面是读者可能想接着问的两个短问题，用「｜」隔开。");
        }

        var user = new StringBuilder();
        if (history is { Count: > 0 })
        {
            user.AppendLine("之前的对话：");
            foreach (var (role, content) in history.TakeLast(4))
            {
                string tag = role == "user" ? "读者" : "书伴";
                string shortText = content.Length > 260 ? content[..260] + "…" : content;
                user.AppendLine($"{tag}：{shortText}");
            }
            user.AppendLine();
        }
        user.AppendLine("原文片段：");
        foreach (var s in allSources)
        {
            string chapTitle = detail is not null && s.Section < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[s.Section].Title)
                ? $"（{detail.Sections[s.Section].Title}）"
                : "";
            user.AppendLine($"[{s.Index}]{chapTitle}{s.Snippet}");
        }
        if (!string.IsNullOrWhiteSpace(selectedQuote))
            user.AppendLine($"\n读者选中的句子：「{selectedQuote}」");
        user.AppendLine($"\n读者问：{cleanQ}");

        string rawReply = await ChatAsync(user.ToString(), system.ToString(), maxTokens: 1024, fast: false, cachePrefixLen: 0, source: UsageSource.Companion, ct: ct);
        var (answerText, followUps) = SplitFollowUpQuestions(rawReply);

        bool notInRead = !wholeBook && (
            answerText.StartsWith("没读到", StringComparison.Ordinal) ||
            answerText.Contains("还没提到", StringComparison.Ordinal) ||
            answerText.Contains("目前读到的地方还没", StringComparison.Ordinal));

        var citedIds = ExtractCitations(answerText);
        var finalSources = notInRead || citedIds.Count == 0
            ? Array.Empty<CompanionSource>()
            : allSources.Where(s => citedIds.Contains(s.Index)).ToArray();

        var result = new CompanionAnswer(
            answerText,
            finalSources,
            notInRead,
            notInRead || followUps.Count == 0 ? null : followUps,
            CurrentProviderLabel);

        if (history is null || history.Count == 0)
            SaveCachedQa(bookId, cacheKey, result);

        return result;
    }

    public static (string AnswerText, IReadOnlyList<string> FollowUps) SplitFollowUpQuestions(string reply)
    {
        var lines = reply.Replace("\r\n", "\n").Split('\n').ToList();
        int idx = lines.FindLastIndex(l => l.Contains("接着问", StringComparison.Ordinal));
        if (idx < 0) return (reply.Trim(), Array.Empty<string>());

        string line = lines[idx];
        lines.RemoveAt(idx);
        int pos = line.IndexOf("接着问", StringComparison.Ordinal);
        string tail = line[(pos + 3)..].Trim(' ', '：', ':', '*', '\t');
        var asks = tail
            .Split(['｜', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim(' ', '\t', '·', '-', '•', '1', '2', '.', '、'))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Take(2)
            .ToArray();

        return (string.Join("\n", lines).Trim(), asks);
    }

    public static HashSet<int> ExtractCitations(string text)
    {
        var set = new HashSet<int>();
        foreach (Match m in CitationRx.Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, out int n)) set.Add(n);
        }
        return set;
    }

    private string GetQaCachePath(string bookId) => Path.Combine(_library.DataDir, "companion", $"{bookId}.json");

    private CompanionAnswer? TryLoadCachedQa(string bookId, string key) => LoadCachedQa(bookId, key);

    internal CompanionAnswer? LoadCachedQa(string bookId, string key)
    {
        try
        {
            string path = GetQaCachePath(bookId);
            if (!File.Exists(path)) return null;
            return ParseCachedQaJson(File.ReadAllText(path), key);
        }
        catch { return null; }
    }

    internal void SaveCachedQa(string bookId, string key, CompanionAnswer ans)
    {
        try
        {
            string path = GetQaCachePath(bookId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string? existingJson = File.Exists(path) ? File.ReadAllText(path) : null;
            string serialized = SerializeUpdatedQaCacheJson(existingJson, key, ans);
            string tmp = path + ".part";
            File.WriteAllText(tmp, serialized);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private void StoreCachedQa(string bookId, string key, CompanionAnswer ans) => SaveCachedQa(bookId, key, ans);

    internal static CompanionAnswer? ParseCachedQaJson(string json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        if (!doc.RootElement.TryGetProperty(key, out var entryEl) || entryEl.ValueKind != JsonValueKind.Object)
            return null;
        return ParseCachedQaEntry(entryEl);
    }

    internal static string SerializeUpdatedQaCacheJson(string? existingJson, string key, CompanionAnswer ans)
    {
        var map = new Dictionary<string, CachedQaEntry>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(existingJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name == key || prop.Value.ValueKind != JsonValueKind.Object) continue;
                        if (ParseCachedQaEntry(prop.Value) is { } parsed)
                            map[prop.Name] = ToCachedQaEntry(parsed);
                    }
                }
            }
            catch { }
        }
        map[key] = ToCachedQaEntry(ans);
        return JsonSerializer.Serialize(map, CacheJsonOpts);
    }

    private static CachedQaEntry ToCachedQaEntry(CompanionAnswer ans)
    {
        var dict = new Dictionary<string, CachedQaSource>(StringComparer.Ordinal);
        foreach (var s in ans.Sources)
        {
            dict[s.Index.ToString()] = new CachedQaSource
            {
                Section = s.Section,
                Sentence = s.Sentence,
                Chapter = string.IsNullOrEmpty(s.Chapter) ? null : s.Chapter,
                Quote = string.IsNullOrEmpty(s.Snippet) ? null : s.Snippet,
                Later = s.Later ? true : null,
            };
        }
        return new CachedQaEntry
        {
            Text = ans.Text,
            Sources = dict,
            NotYet = ans.NotInRead,
            Next = ans.NextQuestions is { Count: > 0 } ? ans.NextQuestions.ToList() : null,
            Label = ans.ProviderLabel ?? "",
        };
    }

    private static CompanionAnswer? ParseCachedQaEntry(JsonElement el)
    {
        static string? GetStr(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

        static bool? GetBool(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;

        static uint? GetUInt(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetUInt32(out uint v) ? v : null;

        static List<string>? GetStrList(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
            var list = new List<string>();
            foreach (var item in p.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s);
            }
            return list.Count > 0 ? list : null;
        }

        static CompanionSource ParseSourceObj(JsonElement srcObj, int fallbackIdx)
        {
            int idx = srcObj.TryGetProperty("Index", out var ip) && ip.ValueKind == JsonValueKind.Number && ip.TryGetInt32(out int vi)
                ? vi
                : (srcObj.TryGetProperty("index", out var ip2) && ip2.ValueKind == JsonValueKind.Number && ip2.TryGetInt32(out int vi2) ? vi2 : fallbackIdx);
            uint sec = GetUInt(srcObj, "section") ?? GetUInt(srcObj, "Section") ?? 0;
            uint sent = GetUInt(srcObj, "sentence") ?? GetUInt(srcObj, "Sentence") ?? 0;
            string? chap = GetStr(srcObj, "chapter") ?? GetStr(srcObj, "Chapter");
            string quote = GetStr(srcObj, "quote") ?? GetStr(srcObj, "Snippet") ?? GetStr(srcObj, "snippet") ?? "";
            bool later = GetBool(srcObj, "later") ?? GetBool(srcObj, "Later") ?? false;
            return new CompanionSource(idx, sec, sent, quote, later, chap);
        }

        string text = GetStr(el, "text") ?? GetStr(el, "Text") ?? "";
        if (string.IsNullOrWhiteSpace(text)) return null;

        bool notYet = GetBool(el, "notYet") ?? GetBool(el, "NotInRead") ?? false;
        string? label = GetStr(el, "label") ?? GetStr(el, "Label");
        var next = GetStrList(el, "next") ?? GetStrList(el, "Next");

        var sources = new List<CompanionSource>();
        if (el.TryGetProperty("sources", out var srcEl) || el.TryGetProperty("Sources", out srcEl))
        {
            if (srcEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in srcEl.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                    int idx = int.TryParse(prop.Name, out int parsedIdx) ? parsedIdx : (sources.Count + 1);
                    sources.Add(ParseSourceObj(prop.Value, idx));
                }
                sources.Sort((a, b) => a.Index.CompareTo(b.Index));
            }
            else if (srcEl.ValueKind == JsonValueKind.Array)
            {
                var arr = srcEl.EnumerateArray().ToArray();
                for (int i = 0; i < arr.Length; i++)
                {
                    if (arr[i].ValueKind == JsonValueKind.Object)
                    {
                        sources.Add(ParseSourceObj(arr[i], sources.Count + 1));
                    }
                    else if (arr[i].ValueKind == JsonValueKind.Number && arr[i].TryGetInt32(out int altIdx) &&
                             i + 1 < arr.Length && arr[i + 1].ValueKind == JsonValueKind.Object)
                    {
                        sources.Add(ParseSourceObj(arr[i + 1], altIdx));
                        i++;
                    }
                }
            }
        }

        return new CompanionAnswer(text, sources, notYet, next, string.IsNullOrEmpty(label) ? null : label);
    }

    public async Task<string> AskBookAsync(
        string bookId,
        string bookTitle,
        uint currentSection,
        string question,
        string? selectedQuote = null,
        bool wholeBook = false)
    {
        var ans = await AskBookStructuredAsync(bookId, bookTitle, currentSection, int.MaxValue / 2, question, selectedQuote, wholeBook);
        return ans.Text;
    }

    /// <summary>
    /// 1:1 <c>Companion.answer</c> with budget gating, fast-tier model routing + HTTP 400/404 primary fallback,
    /// prompt caching, streaming usage tracking, and persistent 30-day ledger recording.
    /// </summary>
    public async Task<string> ChatAsync(
        string userPrompt,
        string? systemPrompt,
        int maxTokens = 1024,
        bool fast = false,
        int cachePrefixLen = 0,
        UsageSource source = UsageSource.Other,
        CancellationToken ct = default)
    {
        var p = _library.Prefs;
        var preset = FindPreset(p.AiProvider);
        var baseUrl = (p.AiBaseUrl?.Trim().TrimEnd('/') is { Length: > 0 } u ? u : preset?.BaseUrl?.TrimEnd('/')) ?? "";
        var primaryModel = (p.AiModel?.Trim() is { Length: > 0 } m ? m : preset?.DefaultModel) ?? "";
        var rawKey = _library.Secrets.Get($"ai.key.{p.AiProvider}");
        if (string.IsNullOrWhiteSpace(rawKey))
            rawKey = _library.Secrets.Get("ai.apiKey");
        if (string.IsNullOrWhiteSpace(rawKey) && preset is { DefaultKey.Length: > 0 })
            rawKey = preset.DefaultKey;

        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("书伴还没接上：在设置 · 书伴里添加一个服务商");
        if (string.IsNullOrEmpty(primaryModel))
            throw new InvalidOperationException("未选择模型");

        bool paid = IsPaidCloudProvider(p.AiProvider, baseUrl);
        _library.Usage.CheckLlmBudget(paid);

        string effectiveModel = primaryModel;
        if (fast && p.AiTierRoutingEnabled && ResolvedFastModel(p.AiProvider, primaryModel) is { Length: > 0 } lighter)
            effectiveModel = lighter;

        bool isAnthropic = preset?.Kind == "anthropic" || baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);

        string text;
        try
        {
            text = isAnthropic
                ? await SendAnthropicAsync(baseUrl, effectiveModel, rawKey, systemPrompt, userPrompt, maxTokens, cachePrefixLen, paid, source, ct)
                : await SendOpenAiAsync(p.AiProvider, baseUrl, effectiveModel, rawKey, systemPrompt, userPrompt, maxTokens, cachePrefixLen, paid, source, ct);
        }
        catch (HttpStatusException ex) when (effectiveModel != primaryModel && (ex.StatusCode == 400 || ex.StatusCode == 404))
        {
            text = isAnthropic
                ? await SendAnthropicAsync(baseUrl, primaryModel, rawKey, systemPrompt, userPrompt, maxTokens, cachePrefixLen, paid, source, ct)
                : await SendOpenAiAsync(p.AiProvider, baseUrl, primaryModel, rawKey, systemPrompt, userPrompt, maxTokens, cachePrefixLen, paid, source, ct);
        }

        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException("没有回答，换个问法试试");
        return trimmed;
    }

    /// <summary>
    /// Splits <paramref name="text"/> at UTF-8 byte offset <paramref name="byteLen"/>, snapping cleanly to a
    /// valid Unicode char boundary so prompt cache prefixes never corrupt multi-byte characters.
    /// </summary>
    public static (string Prefix, string Suffix)? SplitUtf8Prefix(string text, int byteLen)
    {
        if (byteLen <= 0 || string.IsNullOrEmpty(text)) return null;
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        if (byteLen >= bytes.Length) return null;

        int splitByte = byteLen;
        // Back up past UTF-8 continuation bytes (10xxxxxx)
        while (splitByte > 0 && (bytes[splitByte] & 0xC0) == 0x80)
            splitByte--;
        if (splitByte <= 0 || splitByte >= bytes.Length) return null;

        string prefix = Encoding.UTF8.GetString(bytes, 0, splitByte);
        string suffix = Encoding.UTF8.GetString(bytes, splitByte, bytes.Length - splitByte);
        if (prefix.Length == 0 || suffix.Length == 0) return null;
        return (prefix, suffix);
    }

    public static int EstimateTokens(string text)
    {
        int cjk = 0, other = 0;
        foreach (char ch in text)
        {
            if (ch >= 0x2E80) cjk++;
            else other++;
        }
        return Math.Max(1, (int)(cjk * 0.72 + other * 0.28));
    }

    private async Task<string> SendAnthropicAsync(
        string baseUrl, string model, string apiKey, string? systemPrompt, string userPrompt,
        int maxTokens, int cachePrefixLen, bool paid, UsageSource source, CancellationToken ct)
    {
        string endpoint = baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl.TrimEnd('/') + "/messages";
        bool stream = maxTokens > 2048;

        string sysText = systemPrompt ?? "";
        bool useCache = cachePrefixLen > 0 || Encoding.UTF8.GetByteCount(sysText) >= 1024;
        object? systemPayload = string.IsNullOrWhiteSpace(sysText)
            ? null
            : (useCache
                ? new object[] { new { type = "text", text = sysText, cache_control = new { type = "ephemeral" } } }
                : sysText);

        object userContent = SplitUtf8Prefix(userPrompt, cachePrefixLen) is { } parts
            ? new object[]
            {
                new { type = "text", text = parts.Prefix, cache_control = new { type = "ephemeral" } },
                new { type = "text", text = parts.Suffix },
            }
            : userPrompt;

        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new[] { new { role = "user", content = userContent } },
            ["stream"] = stream,
        };
        if (systemPayload is not null)
            bodyDict["system"] = systemPayload;

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");

        if (stream)
        {
            long inTok = 0, outTok = 0, cachedTok = 0;
            string reply = await ReadSseStreamAsync(req, ev =>
            {
                string? kind = ev.TryGetProperty("type", out var tp) ? tp.GetString() : null;
                if (kind == "message_start" &&
                    ev.TryGetProperty("message", out var msg) &&
                    msg.TryGetProperty("usage", out var u))
                {
                    inTok = TryLong(u, "input_tokens") + TryLong(u, "cache_creation_input_tokens");
                    cachedTok = TryLong(u, "cache_read_input_tokens");
                }
                else if (kind == "message_delta" && ev.TryGetProperty("usage", out var ud))
                {
                    long o = TryLong(ud, "output_tokens");
                    if (o > 0) outTok = o;
                }
                if (kind == "content_block_delta" &&
                    ev.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("text", out var txt))
                {
                    return txt.GetString();
                }
                return null;
            }, ct);

            if (inTok == 0 && outTok == 0)
            {
                inTok = EstimateTokens(sysText + userPrompt);
                outTok = EstimateTokens(reply);
            }
            _library.Usage.RecordLlm(inTok, outTok, cachedTok, paid, source);
            return reply;
        }

        using var res = await Http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        EnsureSuccessOrThrow((int)res.StatusCode, raw);

        using var doc = JsonDocument.Parse(raw);
        var sb = new StringBuilder();
        if (doc.RootElement.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in blocks.EnumerateArray())
            {
                if (b.TryGetProperty("text", out var t) && t.GetString() is string s)
                    sb.Append(s);
            }
        }
        string replyText = sb.ToString();
        if (doc.RootElement.TryGetProperty("usage", out var usageEl))
        {
            long inTok = TryLong(usageEl, "input_tokens") + TryLong(usageEl, "cache_creation_input_tokens");
            long outTok = TryLong(usageEl, "output_tokens");
            long cachedTok = TryLong(usageEl, "cache_read_input_tokens");
            _library.Usage.RecordLlm(inTok, outTok, cachedTok, paid, source);
        }
        else
        {
            _library.Usage.RecordLlm(EstimateTokens(sysText + userPrompt), EstimateTokens(replyText), 0, paid, source);
        }
        return replyText;
    }

    private async Task<string> SendOpenAiAsync(
        string providerId, string baseUrl, string model, string apiKey, string? systemPrompt, string userPrompt,
        int maxTokens, int cachePrefixLen, bool paid, UsageSource source, CancellationToken ct)
    {
        string endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl.TrimEnd('/') + "/chat/completions";
        bool stream = maxTokens > 2048;
        bool isOpenRouterClaude = string.Equals(providerId, "openrouter", StringComparison.OrdinalIgnoreCase)
            && model.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase);

        var messages = new List<object>();
        if (isOpenRouterClaude && SplitUtf8Prefix(userPrompt, cachePrefixLen) is { } parts)
        {
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = new object[] { new { type = "text", text = systemPrompt, cache_control = new { type = "ephemeral" } } } });
            messages.Add(new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = parts.Prefix, cache_control = new { type = "ephemeral" } },
                    new { type = "text", text = parts.Suffix },
                },
            });
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = userPrompt });
        }

        var payloadObj = new Dictionary<string, object>
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["max_tokens"] = maxTokens,
            ["stream"] = stream,
            ["messages"] = messages,
        };
        if (stream && providerId is "openai" or "deepseek" or "openrouter" or "dashscope" or "moonshot")
            payloadObj["stream_options"] = new { include_usage = true };

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(payloadObj), Encoding.UTF8, "application/json");

        if (stream)
        {
            long inTok = 0, outTok = 0, cachedTok = 0;
            string reply = await ReadSseStreamAsync(req, ev =>
            {
                if (ev.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    var parsed = ParseOpenAiUsage(u);
                    if (parsed.Input > 0) inTok = parsed.Input;
                    if (parsed.Output > 0) outTok = parsed.Output;
                    if (parsed.Cached > 0) cachedTok = parsed.Cached;
                }
                if (ev.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
                {
                    var first = ch[0];
                    if (first.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c))
                        return c.GetString();
                }
                return null;
            }, ct);

            if (inTok == 0 && outTok == 0)
            {
                inTok = EstimateTokens((systemPrompt ?? "") + userPrompt);
                outTok = EstimateTokens(reply);
            }
            _library.Usage.RecordLlm(inTok, outTok, cachedTok, paid, source);
            return reply;
        }

        using var res = await Http.SendAsync(req, ct);
        var raw = await res.Content.ReadAsStringAsync(ct);
        EnsureSuccessOrThrow((int)res.StatusCode, raw);

        using var doc = JsonDocument.Parse(raw);
        string replyText = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        if (doc.RootElement.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            var parsed = ParseOpenAiUsage(usageEl);
            _library.Usage.RecordLlm(parsed.Input, parsed.Output, parsed.Cached, paid, source);
        }
        else
        {
            _library.Usage.RecordLlm(EstimateTokens((systemPrompt ?? "") + userPrompt), EstimateTokens(replyText), 0, paid, source);
        }
        return replyText;
    }

    internal static (long Input, long Output, long Cached) ParseOpenAiUsage(JsonElement u)
    {
        long prompt = TryLong(u, "prompt_tokens");
        if (prompt == 0) prompt = TryLong(u, "input_tokens");
        long completion = TryLong(u, "completion_tokens");
        if (completion == 0) completion = TryLong(u, "output_tokens");
        long cached = 0;
        if (u.TryGetProperty("prompt_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object)
            cached = TryLong(details, "cached_tokens");
        if (cached == 0) cached = TryLong(u, "prompt_cache_hit_tokens");
        if (cached == 0) cached = TryLong(u, "cache_read_input_tokens");
        return (Math.Max(0, prompt - cached), completion, cached);
    }

    private static long TryLong(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n))
            return n;
        return 0;
    }

    private static async Task<string> ReadSseStreamAsync(
        HttpRequestMessage req, Func<JsonElement, string?> extractDelta, CancellationToken ct)
    {
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync(ct);
            EnsureSuccessOrThrow((int)res.StatusCode, errBody);
        }

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync(ct) is string line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var data = line[5..].Trim();
            if (data == "[DONE]") break;
            try
            {
                using var doc = JsonDocument.Parse(data);
                if (extractDelta(doc.RootElement) is string piece)
                    sb.Append(piece);
            }
            catch { }
        }
        return sb.ToString();
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(string providerId, string? overrideUrl = null, string? overrideKey = null)
    {
        var preset = FindPreset(providerId);
        string baseUrl = (overrideUrl?.Trim().TrimEnd('/') is { Length: > 0 } u
            ? u
            : (_library.Prefs.AiProviderUrls.TryGetValue(providerId, out var saved) && !string.IsNullOrWhiteSpace(saved)
                ? saved.Trim().TrimEnd('/')
                : preset?.BaseUrl?.TrimEnd('/'))) ?? "";
        if (string.IsNullOrEmpty(baseUrl)) return [];

        string key = overrideKey ?? _library.Secrets.Get($"ai.key.{providerId}");
        if (string.IsNullOrWhiteSpace(key) && providerId == _library.Prefs.AiProvider)
            key = _library.Secrets.Get("ai.apiKey");
        if (string.IsNullOrWhiteSpace(key) && preset is { DefaultKey.Length: > 0 })
            key = preset.DefaultKey;

        string endpoint = baseUrl.EndsWith("/models", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl + "/models";

        using var cts = new CancellationTokenSource(System.TimeSpan.FromSeconds(15));
        using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
        bool isAnthropic = preset?.Kind == "anthropic" || baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
        if (isAnthropic)
        {
            req.Headers.TryAddWithoutValidation("x-api-key", key);
            req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var res = await Http.SendAsync(req, cts.Token);
        if (!res.IsSuccessStatusCode) return [];
        var raw = await res.Content.ReadAsStringAsync(cts.Token);
        var parsed = ParseModelList(raw);
        if (parsed.Count > 0)
            SaveCachedModels(providerId, parsed);
        return parsed;
    }

    public IReadOnlyList<string> GetCachedModels(string providerId) =>
        _library.Prefs.CachedAiProviderModels.TryGetValue(providerId, out var list) && list is { Count: > 0 }
            ? list
            : Array.Empty<string>();

    public void SaveCachedModels(string providerId, IReadOnlyList<string> models)
    {
        if (string.IsNullOrWhiteSpace(providerId) || models.Count == 0) return;
        _library.Prefs.CachedAiProviderModels[providerId] = models
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToList();
        _library.SavePrefs();
    }

    public bool IsModelStarred(string providerId, string modelId) =>
        _library.Prefs.StarredAiModels.Contains($"{providerId}:{modelId}", StringComparer.OrdinalIgnoreCase);

    public bool ToggleStarModel(string providerId, string modelId)
    {
        string key = $"{providerId}:{modelId}";
        int idx = _library.Prefs.StarredAiModels.FindIndex(x => string.Equals(x, key, StringComparison.OrdinalIgnoreCase));
        bool starred;
        if (idx >= 0)
        {
            _library.Prefs.StarredAiModels.RemoveAt(idx);
            starred = false;
        }
        else
        {
            _library.Prefs.StarredAiModels.Insert(0, key);
            starred = true;
        }
        _library.SavePrefs();
        return starred;
    }

    public void RecordModelUsed(string providerId, string modelId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || string.IsNullOrWhiteSpace(modelId)) return;
        if (!_library.Prefs.AiRecentModels.TryGetValue(providerId, out var rec))
        {
            rec = [];
            _library.Prefs.AiRecentModels[providerId] = rec;
        }
        rec.RemoveAll(x => string.Equals(x, modelId, StringComparison.OrdinalIgnoreCase));
        rec.Insert(0, modelId);
        if (rec.Count > 5) rec.RemoveRange(5, rec.Count - 5);

        string gKey = $"{providerId}:{modelId}";
        _library.Prefs.GlobalRecentAiModels.RemoveAll(x => string.Equals(x, gKey, StringComparison.OrdinalIgnoreCase));
        _library.Prefs.GlobalRecentAiModels.Insert(0, gKey);
        if (_library.Prefs.GlobalRecentAiModels.Count > 12)
            _library.Prefs.GlobalRecentAiModels.RemoveRange(12, _library.Prefs.GlobalRecentAiModels.Count - 12);
        _library.SavePrefs();
    }

    public static string InferModelMaker(string modelId)
    {
        string m = (modelId ?? "").Trim().ToLowerInvariant();
        if (m.Contains("claude") || m.StartsWith("anthropic/")) return "Anthropic";
        if (m.Contains("gpt") || m.StartsWith("openai/") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4")) return "OpenAI";
        if (m.Contains("gemini") || m.StartsWith("google/")) return "Google";
        if (m.Contains("deepseek")) return "DeepSeek";
        if (m.Contains("qwen") || m.Contains("qwq")) return "Qwen";
        if (m.Contains("kimi") || m.Contains("moonshot")) return "Moonshot";
        if (m.Contains("glm") || m.Contains("zhipu")) return "Zhipu";
        if (m.Contains("doubao") || m.Contains("seed-")) return "Doubao";
        if (m.Contains("minimax")) return "MiniMax";
        if (m.Contains("llama") || m.StartsWith("meta-llama/")) return "Meta";
        if (m.Contains("mistral") || m.Contains("mixtral")) return "Mistral";
        if (m.Contains("grok") || m.StartsWith("x-ai/")) return "xAI";
        return "其它";
    }

    public static string? InferRouteBadge(string providerId, string modelId)
    {
        string m = (modelId ?? "").ToLowerInvariant();
        if (providerId is "ollama" or "lmstudio") return "本机";
        if (providerId is "openrouter" or "siliconflow")
            return m.Contains("free") ? "免费路由" : "聚合路由";
        if (m.Contains("flash") || m.Contains("mini") || m.Contains("haiku") || m.Contains("turbo") || m.Contains("nano"))
            return "轻量";
        if (m.Contains("reasoner") || m.Contains("r1") || m.Contains("opus") || m.Contains("pro"))
            return "强推";
        return "直连";
    }

    private static IReadOnlyList<string> ParseModelList(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        void AddModelId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            string clean = id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id;
            if (!string.IsNullOrWhiteSpace(clean) && seen.Add(clean))
                list.Add(clean);
        }

        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object)
        {
            JsonElement arr = default;
            bool hasArr = (root.TryGetProperty("data", out arr) && arr.ValueKind == JsonValueKind.Array)
                || (root.TryGetProperty("models", out arr) && arr.ValueKind == JsonValueKind.Array);
            if (hasArr)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String) AddModelId(item.GetString());
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        if (item.TryGetProperty("id", out var idProp)) AddModelId(idProp.GetString());
                        else if (item.TryGetProperty("name", out var nameProp)) AddModelId(nameProp.GetString());
                    }
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) AddModelId(item.GetString());
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    if (item.TryGetProperty("id", out var idProp)) AddModelId(idProp.GetString());
                    else if (item.TryGetProperty("name", out var nameProp)) AddModelId(nameProp.GetString());
                }
            }
        }

        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    public static async Task<string> SignInWithOpenRouterAsync(CancellationToken ct = default)
    {
        string verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        string challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string state = Guid.NewGuid().ToString("D");

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        string callbackUrl = $"http://localhost:{port}/callback";
        string authUrl = $"https://openrouter.ai/auth" +
            $"?callback_url={Uri.EscapeDataString(callbackUrl)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&key_label={Uri.EscapeDataString("语阅 · 书伴")}";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(System.TimeSpan.FromMinutes(3));

        string? code = null;
        while (!timeoutCts.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(timeoutCts.Token);
            using var net = client.GetStream();
            var buf = new byte[16 * 1024];
            int read = await net.ReadAsync(buf.AsMemory(0, buf.Length), timeoutCts.Token);
            if (read <= 0) continue;

            string reqText = Encoding.UTF8.GetString(buf, 0, read);
            string target = reqText.Split(' ').Skip(1).FirstOrDefault() ?? "";
            if (!target.StartsWith("/callback", StringComparison.Ordinal))
            {
                byte[] ignore = Encoding.UTF8.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                await net.WriteAsync(ignore, timeoutCts.Token);
                continue;
            }

            var query = ParseQueryString(target);
            query.TryGetValue("code", out var gotCode);
            query.TryGetValue("state", out var gotState);
            bool ok = !string.IsNullOrEmpty(gotCode) && gotState == state;

            string pageMsg = ok ? "已经登录 OpenRouter，可以回到语阅了。" : "没有登录成功，回到语阅再试一次。";
            byte[] html = Encoding.UTF8.GetBytes(
                $"<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width'><body style='font:16px \"Microsoft YaHei UI\",sans-serif;padding:48px 24px;text-align:center'>{pageMsg}</body>");
            byte[] head = Encoding.UTF8.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
            await net.WriteAsync(head, timeoutCts.Token);
            await net.WriteAsync(html, timeoutCts.Token);
            await net.FlushAsync(timeoutCts.Token);

            if (!ok) throw new InvalidOperationException("OpenRouter 没给 Key，再登录一次");
            code = gotCode;
            break;
        }

        listener.Stop();
        if (string.IsNullOrEmpty(code))
            throw new OperationCanceledException("没有登录");

        using var exchReq = new HttpRequestMessage(HttpMethod.Post, "https://openrouter.ai/api/v1/auth/keys")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    code,
                    code_verifier = verifier,
                    code_challenge_method = "S256",
                }),
                Encoding.UTF8,
                "application/json"),
        };
        using var exchRes = await Http.SendAsync(exchReq, ct);
        var exchBody = await exchRes.Content.ReadAsStringAsync(ct);
        if (!exchRes.IsSuccessStatusCode)
            throw new InvalidOperationException("OpenRouter 没给 Key，再登录一次");

        using var keyDoc = JsonDocument.Parse(exchBody);
        var key = keyDoc.RootElement.TryGetProperty("key", out var kp) ? kp.GetString() : null;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenRouter 没给 Key，再登录一次");
        return key;
    }

    private sealed class HttpStatusException : InvalidOperationException
    {
        public int StatusCode { get; }
        public HttpStatusException(int statusCode, string message) : base(message) => StatusCode = statusCode;
    }

    private static void EnsureSuccessOrThrow(int code, string raw)
    {
        if (code is >= 200 and < 300) return;

        string? msg = null;
        string kind = "";
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object)
                {
                    msg = err.TryGetProperty("message", out var mp) ? mp.GetString() : null;
                    string tp = err.TryGetProperty("type", out var t) ? t.ToString() : "";
                    string cd = err.TryGetProperty("code", out var c) ? c.ToString() : "";
                    kind = $"{tp} {cd} {msg}".ToLowerInvariant();
                }
                else if (err.ValueKind == JsonValueKind.String)
                {
                    msg = err.GetString();
                    kind = msg?.ToLowerInvariant() ?? "";
                }
            }
            else if (doc.RootElement.TryGetProperty("message", out var topMsg))
            {
                msg = topMsg.GetString();
                kind = msg?.ToLowerInvariant() ?? "";
            }
        }
        catch { }

        throw code switch
        {
            401 or 403 => new HttpStatusException(code, "Key 不对，去设置 · 书伴里重新填"),
            402 => new HttpStatusException(code, "账号余额不够了"),
            429 when kind.Contains("quota") || kind.Contains("credit") || kind.Contains("balance") =>
                new HttpStatusException(code, "账号余额不够了"),
            429 or 529 => new HttpStatusException(code, "问得太快了，等一会儿再问"),
            400 when kind.Contains("credit") || kind.Contains("balance") =>
                new HttpStatusException(code, "账号余额不够了"),
            _ => new HttpStatusException(
                code,
                string.IsNullOrWhiteSpace(msg)
                    ? $"服务器回了 {code}"
                    : $"服务器回了 {code}：{(msg.Length > 180 ? msg[..180] : msg)}"),
        };
    }

    public static string FormatErrorMessage(Exception ex) => ex switch
    {
        TaskCanceledException => "等太久没回答，再问一次",
        HttpRequestException => "连不上服务器，看看接口地址",
        _ => ex.Message,
    };

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static Dictionary<string, string> ParseQueryString(string pathAndQuery)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int qIdx = pathAndQuery.IndexOf('?');
        if (qIdx < 0 || qIdx + 1 >= pathAndQuery.Length) return map;
        foreach (var pair in pathAndQuery[(qIdx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
                map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return map;
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "什么", "为什么", "怎么", "怎样", "哪里", "哪些", "这个", "那个", "这些", "那些",
        "是不是", "有没有", "可以", "一下", "帮我", "请问", "介绍", "分析", "解释",
        "这里", "那里", "他们", "她们", "我们", "你们", "自己", "因为", "所以", "但是", "如果",
        "这本书", "本书", "这本", "这章", "这一章", "一章", "意思", "后来", "之后", "以后", "现在", "时候",
        "what", "why", "how", "who", "where", "when", "this", "that", "these", "those",
        "with", "from", "have", "about", "does", "there", "their", "would", "could", "should",
    };

    private static IEnumerable<string> ExtractKeywords(string question, string? quote)
    {
        var raw = ((quote ?? "") + " " + question).Trim();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string s)
        {
            string t = s.Trim();
            if (t.Length < 2 || StopWords.Contains(t)) return;
            if (seen.Add(t)) result.Add(t);
        }

        var parts = raw.Split(
            [' ', '\t', '\r', '\n', '，', '。', '？', '！', '“', '”', '‘', '’', '、', '；', '：', '（', '）', '《', '》',
             ',', '.', '?', '!', '"', '\'', ';', ':', '(', ')', '-', '—', '的', '了', '吗', '呢', '吧', '在', '是', '和', '与', '对', '把', '被', '让', '给', '到'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            bool hasCjk = part.Any(c => c >= '\u4E00' && c <= '\u9FFF');
            if (!hasCjk)
            {
                if (part.Length >= 3) Add(part);
                continue;
            }

            string clean = part;
            foreach (var sw in new[] { "为什么", "怎么样", "是什么", "怎么", "什么", "请问", "帮我", "能否", "一下", "分析", "梳理", "解释" })
                clean = clean.Replace(sw, " ");

            foreach (var sub in clean.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (sub.Length <= 4)
                {
                    Add(sub);
                }
                else
                {
                    for (int len = 3; len >= 2; len--)
                    {
                        for (int i = 0; i <= sub.Length - len; i++)
                            Add(sub.Substring(i, len));
                    }
                }
            }
        }

        return result.OrderByDescending(s => s.Length >= 2 && s.Length <= 4 ? 10 : s.Length).ThenByDescending(s => s.Length);
    }
}
