using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
    string DefaultKey = "");

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/Companion.swift</c>, <c>CompanionProviders.swift</c> &amp; <c>OpenRouterSignIn.swift</c>:
/// unified LLM client for 书伴 &amp; 播客编辑部 supporting OpenRouter PKCE OAuth sign-in, Anthropic native <c>/v1/messages</c>,
/// OpenAI-compatible <c>/chat/completions</c>, SSE streaming for long generations, and live <c>GET /models</c> discovery.
/// All API keys stay strictly in local DPAPI <see cref="SecretStore"/>.
/// </summary>
public sealed class CompanionClient
{
    public static readonly IReadOnlyList<AiProviderPreset> Presets =
    [
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", "openrouter/auto", true,
            Group: "signIn", KeyHint: "sk-or-…", Blurb: "登录就能用，几百个模型一个账号"),
        new("claude", "Claude", "https://api.anthropic.com/v1", "claude-sonnet-4-5", true,
            Kind: "anthropic", KeyHint: "sk-ant-…", Aliases: "anthropic 克劳德"),
        new("openai", "OpenAI", "https://api.openai.com/v1", "gpt-5-mini", true,
            Aliases: "chatgpt gpt"),
        new("gemini", "Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", true,
            KeyHint: "AIza…", Aliases: "google 谷歌"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat", true,
            Aliases: "深度求索"),
        new("dashscope", "通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-plus", true,
            Aliases: "qwen 阿里 百炼 dashscope"),
        new("moonshot", "Kimi", "https://api.moonshot.cn/v1", "kimi-k2-turbo-preview", true,
            Aliases: "moonshot 月之暗面"),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4.6", true,
            Aliases: "zhipu glm z.ai 清言"),
        new("volcengine", "豆包", "https://ark.cn-beijing.volces.com/api/v3", "doubao-seed-1-6-250615", true,
            Aliases: "doubao 火山引擎 volcengine 字节"),
        new("siliconflow", "硅基流动", "https://api.siliconflow.cn/v1", "deepseek-ai/DeepSeek-V3", true,
            Aliases: "siliconflow"),
        new("minimax", "MiniMax", "https://api.minimaxi.com/v1", "MiniMax-M2", true,
            Aliases: "海螺 稀宇"),
        new("xai", "Grok", "https://api.x.ai/v1", "grok-4", true,
            KeyHint: "xai-…", Aliases: "xai"),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", "mistral-small-latest", true,
            KeyHint: ""),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", true,
            KeyHint: "gsk_…"),
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

    private readonly LibraryService _library;
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(180) };

    public CompanionClient(LibraryService library)
    {
        _library = library;
    }

    public static AiProviderPreset? FindPreset(string id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

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

    public async Task<(bool Ok, string Message)> TestConnectionAsync()
    {
        try
        {
            var reply = await ChatAsync("用一个词回答：天空通常是什么颜色？", "你是一个简短的助手。", maxTokens: 64);
            return (true, $"连接成功：{reply.Trim()}");
        }
        catch (Exception ex)
        {
            return (false, FormatErrorMessage(ex));
        }
    }

    /// <summary>
    /// Answers a reader's question grounded strictly in passages from the book (avoiding spoilers past current section).
    /// </summary>
    public async Task<string> AskBookAsync(
        string bookId,
        string bookTitle,
        uint currentSection,
        string question,
        string? selectedQuote = null,
        bool wholeBook = false)
    {
        var keywords = ExtractKeywords(question, selectedQuote);
        var excerpts = new List<string>();
        foreach (var kw in keywords.Take(3))
        {
            var hits = _library.SearchBook(bookId, kw)
                .Where(h => wholeBook || h.Section <= currentSection)
                .Take(4);
            foreach (var h in hits)
            {
                var line = $"[第{h.Section + 1}节] {h.Before}{h.Matched}{h.After}";
                if (!excerpts.Contains(line)) excerpts.Add(line);
            }
        }

        var system = new StringBuilder();
        system.AppendLine($"你是“语阅”的 AI 书伴，正在陪读者阅读《{bookTitle}》。");
        system.AppendLine(wholeBook
            ? "可以结合全书内容回答，回答简洁克制、忠于原著。"
            : "严格不要剧透当前章节之后的情节！仅基于读者已读部分与所给原文片段回答，回答简洁克制。");

        var user = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(selectedQuote))
            user.AppendLine($"读者选中的原文：\n“{selectedQuote}”\n");
        if (excerpts.Count > 0)
        {
            user.AppendLine("书内检索到的相关段落：");
            foreach (var ex in excerpts.Take(8)) user.AppendLine("- " + ex);
            user.AppendLine();
        }
        user.AppendLine("读者的问题：" + question);

        return await ChatAsync(user.ToString(), system.ToString(), maxTokens: 1024);
    }

    public async Task<string> ChatAsync(string userPrompt, string? systemPrompt, int maxTokens = 1024)
    {
        var p = _library.Prefs;
        var preset = FindPreset(p.AiProvider);
        var baseUrl = (p.AiBaseUrl?.Trim().TrimEnd('/') is { Length: > 0 } u ? u : preset?.BaseUrl?.TrimEnd('/')) ?? "";
        var model = (p.AiModel?.Trim() is { Length: > 0 } m ? m : preset?.DefaultModel) ?? "";
        var rawKey = _library.Secrets.Get("ai.apiKey");
        if (string.IsNullOrWhiteSpace(rawKey))
            rawKey = _library.Secrets.Get($"ai.key.{p.AiProvider}");
        if (string.IsNullOrWhiteSpace(rawKey) && preset is { DefaultKey.Length: > 0 })
            rawKey = preset.DefaultKey;

        if (string.IsNullOrEmpty(baseUrl))
            throw new InvalidOperationException("书伴还没接上：在设置 · 书伴里添加一个服务商");
        if (string.IsNullOrEmpty(model))
            throw new InvalidOperationException("未选择模型");

        bool isAnthropic = preset?.Kind == "anthropic" || baseUrl.Contains("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
        string text = isAnthropic
            ? await SendAnthropicAsync(baseUrl, model, rawKey, systemPrompt, userPrompt, maxTokens)
            : await SendOpenAiAsync(baseUrl, model, rawKey, systemPrompt, userPrompt, maxTokens);

        var trimmed = text.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new InvalidOperationException("没有回答，换个问法试试");
        return trimmed;
    }

    private static async Task<string> SendAnthropicAsync(
        string baseUrl, string model, string apiKey, string? systemPrompt, string userPrompt, int maxTokens)
    {
        string endpoint = baseUrl.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl.TrimEnd('/') + "/messages";
        bool stream = maxTokens > 2048;

        var bodyDict = new Dictionary<string, object>
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["messages"] = new[] { new { role = "user", content = userPrompt } },
            ["stream"] = stream,
        };
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            bodyDict["system"] = systemPrompt!;

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.TryAddWithoutValidation("x-api-key", apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        req.Content = new StringContent(JsonSerializer.Serialize(bodyDict), Encoding.UTF8, "application/json");

        if (stream)
        {
            return await ReadSseStreamAsync(req, ev =>
            {
                if (ev.TryGetProperty("type", out var tp) && tp.GetString() == "content_block_delta"
                    && ev.TryGetProperty("delta", out var delta)
                    && delta.TryGetProperty("text", out var txt))
                {
                    return txt.GetString();
                }
                return null;
            });
        }

        using var res = await Http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        EnsureSuccessOrThrow((int)res.StatusCode, raw);

        using var doc = JsonDocument.Parse(raw);
        if (!doc.RootElement.TryGetProperty("content", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            return "";
        var sb = new StringBuilder();
        foreach (var b in blocks.EnumerateArray())
        {
            if (b.TryGetProperty("text", out var t) && t.GetString() is string s)
                sb.Append(s);
        }
        return sb.ToString();
    }

    private static async Task<string> SendOpenAiAsync(
        string baseUrl, string model, string apiKey, string? systemPrompt, string userPrompt, int maxTokens)
    {
        string endpoint = baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : baseUrl.TrimEnd('/') + "/chat/completions";
        bool stream = maxTokens > 2048;

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPrompt))
            messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userPrompt });

        var payload = JsonSerializer.Serialize(new
        {
            model,
            temperature = 0.3,
            max_tokens = maxTokens,
            stream,
            messages,
        });

        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        if (stream)
        {
            return await ReadSseStreamAsync(req, ev =>
            {
                if (ev.TryGetProperty("choices", out var ch) && ch.ValueKind == JsonValueKind.Array && ch.GetArrayLength() > 0)
                {
                    var first = ch[0];
                    if (first.TryGetProperty("delta", out var d) && d.TryGetProperty("content", out var c))
                        return c.GetString();
                }
                return null;
            });
        }

        using var res = await Http.SendAsync(req);
        var raw = await res.Content.ReadAsStringAsync();
        EnsureSuccessOrThrow((int)res.StatusCode, raw);

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static async Task<string> ReadSseStreamAsync(
        HttpRequestMessage req, Func<JsonElement, string?> extractDelta)
    {
        using var res = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        if (!res.IsSuccessStatusCode)
        {
            var errBody = await res.Content.ReadAsStringAsync();
            EnsureSuccessOrThrow((int)res.StatusCode, errBody);
        }

        using var stream = await res.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var sb = new StringBuilder();
        while (await reader.ReadLineAsync() is string line)
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

    /// <summary>
    /// 1:1 <c>Companion.models(config)</c>: queries <c>GET .../models</c> for Anthropic, OpenAI, Gemini, Ollama, LM Studio, etc.
    /// </summary>
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

    /// <summary>
    /// 1:1 port of <c>apple/Yuyue/App/OpenRouterSignIn.swift</c>:
    /// PKCE S256 localhost callback OAuth sign-in that exchanges an authorization code for a dedicated OpenRouter key.
    /// </summary>
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
            401 or 403 => new InvalidOperationException("Key 不对，去设置 · 书伴里重新填"),
            402 => new InvalidOperationException("账号余额不够了"),
            429 when kind.Contains("quota") || kind.Contains("credit") || kind.Contains("balance") =>
                new InvalidOperationException("账号余额不够了"),
            429 or 529 => new InvalidOperationException("问得太快了，等一会儿再问"),
            400 when kind.Contains("credit") || kind.Contains("balance") =>
                new InvalidOperationException("账号余额不够了"),
            _ => new InvalidOperationException(
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

    private static IEnumerable<string> ExtractKeywords(string question, string? quote)
    {
        var raw = (quote ?? "") + " " + question;
        var tokens = raw.Split([' ', '\t', '\r', '\n', '，', '。', '？', '！', '“', '”', '、', ',', '.', '?', '!'],
            StringSplitOptions.RemoveEmptyEntries);
        return tokens.Where(t => t.Length >= 2).Distinct().OrderByDescending(t => t.Length);
    }
}
