using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yuyue.Core;

public sealed record NoteExportLastRecord(DateTimeOffset Date, string? Book, int Books, int Count);

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/NoteExport.swift</c> + <c>NoteExportMenu.swift</c>:
/// Incremental highlight/thought export to Obsidian (YAML + <c>^yy-id8</c> block IDs),
/// Notion (<c>2022-06-28</c> subpages &amp; 100-block batching), flomo (webhook),
/// 新枝 Xinzhi (<c>X-CLI-Token</c> memo API), and Annotated EPUB (<c>Core.ExportEpub</c>),
/// with FNV-1a 31-bit deduplication retained across disconnect/reconnect.
/// </summary>
public sealed class NoteExportService
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(30) };
    private readonly LibraryService _library;

    public NoteExportService(LibraryService library)
    {
        _library = library;
    }

    // 1:1 NoteExport.swift 31-bit FNV-1a hash over UTF-8 (text + "\u0001" + (note ?? ""))
    public static int ComputeMarkHash(Mark mark)
    {
        uint h = 2_166_136_261u;
        byte[] bytes = Encoding.UTF8.GetBytes($"{mark.Text}\u0001{mark.Note ?? ""}");
        foreach (byte b in bytes)
        {
            h ^= b;
            h *= 16_777_619u;
        }
        return (int)(h & 0x7FFF_FFFFu);
    }

    public static string? ExtractNotionPageId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string stripped = raw.Replace("-", "");
        var m = Regex.Matches(stripped, "[0-9a-fA-F]{32}").LastOrDefault();
        return m?.Value.ToLowerInvariant();
    }

    public static string SanitizeObsidianFileName(string title)
    {
        string s = Regex.Replace(title, @"[/\\:*?""<>|#^\[\]]", "-").Trim();
        return string.IsNullOrEmpty(s) ? "未命名书籍" : s;
    }

    public static string BuildObsidianBookMarkdown(LibraryService library, BookSummary book)
    {
        var marks = library.GetMarks(book.Id);
        var detail = library.GetBookDetail(book.Id);
        var sb = new StringBuilder();

        // YAML frontmatter (1:1 NoteExport.swift)
        sb.AppendLine("---");
        sb.AppendLine($"title: \"{book.Title.Replace("\"", "\\\"")}\"");
        if (book.Authors.Length > 0)
            sb.AppendLine($"author: \"{string.Join(" / ", book.Authors).Replace("\"", "\\\"")}\"");
        sb.AppendLine("source: 语阅");
        sb.AppendLine("tags: [语阅]");
        sb.AppendLine($"updated: {DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {book.Title}");
        sb.AppendLine();

        foreach (var group in marks.GroupBy(m => m.Section).OrderBy(g => g.Key))
        {
            string chapter = group.Key < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[group.Key].Title)
                ? detail.Sections[group.Key].Title!
                : $"第 {group.Key + 1} 节";
            sb.AppendLine($"## {chapter}");
            sb.AppendLine();

            foreach (var m in group)
            {
                string id8 = m.Id.Length >= 8 ? m.Id[..8] : m.Id;
                string quoteLines = m.Text.Trim().Replace("\r\n", "\n").Replace("\n", "\n> ");
                sb.AppendLine($"> {quoteLines} ^yy-{id8}");
                sb.AppendLine();
                if (!string.IsNullOrWhiteSpace(m.Note))
                {
                    sb.AppendLine(m.Note!.Trim());
                    sb.AppendLine();
                }
            }
        }
        return sb.ToString();
    }

    public async Task<(bool Ok, string PageTitle, string Message)> VerifyNotionAsync(string token, string pageUrlOrId, CancellationToken ct = default)
    {
        string tok = token.Trim();
        if (tok.Length < 20 || (!tok.StartsWith("ntn_", StringComparison.Ordinal) && !tok.StartsWith("secret_", StringComparison.Ordinal)))
            return (false, "", "Notion Token 需以 ntn_ 或 secret_ 开头");

        string? pageId = ExtractNotionPageId(pageUrlOrId);
        if (string.IsNullOrEmpty(pageId))
            return (false, "", "请粘贴包含 32 位页面 ID 的 Notion 页面链接");

        try
        {
            using (var meReq = MakeNotionRequest(HttpMethod.Get, "https://api.notion.com/v1/users/me", tok))
            using (var meResp = await Http.SendAsync(meReq, ct))
            {
                if (!meResp.IsSuccessStatusCode)
                    return (false, "", $"Token 校验未通过 ({(int)meResp.StatusCode})");
            }

            using var pageReq = MakeNotionRequest(HttpMethod.Get, $"https://api.notion.com/v1/pages/{pageId}", tok);
            using var pageResp = await Http.SendAsync(pageReq, ct);
            if (pageResp.StatusCode == HttpStatusCode.NotFound)
                return (false, "", "未找到该页面：请在 Notion 页面右上角「··· → Connections」里授权你的 Integration");
            if (!pageResp.IsSuccessStatusCode)
                return (false, "", $"读取页面失败 ({(int)pageResp.StatusCode})");

            string body = await pageResp.Content.ReadAsStringAsync(ct);
            string title = ParseNotionPageTitle(body) ?? "Notion 笔记页";
            return (true, title, $"已连接至「{title}」");
        }
        catch (Exception ex)
        {
            return (false, "", $"连接失败：{ex.Message}");
        }
    }

    public async Task<(bool Ok, string UserName, string Message)> VerifyXinzhiAsync(string token, CancellationToken ct = default)
    {
        string tok = token.Trim();
        if (string.IsNullOrEmpty(tok)) return (false, "", "请粘贴新枝 CLI Token");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.xinzhi.zone/api/cli/user/me");
            req.Headers.Add("X-CLI-Token", tok);
            req.Headers.Add("x-client", "CLI");
            req.Headers.Add("x-request-source", "xinzhi-cli");
            using var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, "", $"新枝认证失败 ({(int)resp.StatusCode})");

            string json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            bool success = doc.RootElement.TryGetProperty("success", out var sp) && sp.ValueKind == JsonValueKind.True;
            if (!success) return (false, "", "新枝返回认证无效");
            string name = "新枝账号";
            if (doc.RootElement.TryGetProperty("data", out var dp) && dp.TryGetProperty("name", out var np))
                name = np.GetString() ?? name;
            return (true, name, $"已连接新枝（{name}）");
        }
        catch (Exception ex)
        {
            return (false, "", $"连接失败：{ex.Message}");
        }
    }

    public static bool ValidateFlomoWebhook(string url) =>
        !string.IsNullOrWhiteSpace(url) &&
        url.Trim().StartsWith("https://flomoapp.com/iwh/", StringComparison.OrdinalIgnoreCase) &&
        url.Trim().Length > 30;

    public async Task<(int BooksCount, int MarksCount)> ExportToObsidianAsync(BookSummary? singleBook = null, CancellationToken ct = default)
    {
        string dir = _library.Prefs.ObsidianFolder;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            throw new InvalidOperationException("请先在「设置 → 笔记导出」中选择 Obsidian 文件夹");

        var targets = singleBook is not null ? [singleBook] : _library.GetKeptBooks();
        int booksDone = 0, marksDone = 0;
        foreach (var b in targets)
        {
            ct.ThrowIfCancellationRequested();
            var marks = _library.GetMarks(b.Id);
            if (marks.Count == 0) continue;
            string md = BuildObsidianBookMarkdown(_library, b);
            string filePath = Path.Combine(dir, $"{SanitizeObsidianFileName(b.Title)}.md");
            await File.WriteAllTextAsync(filePath, md, Encoding.UTF8, ct);
            booksDone++;
            marksDone += marks.Count;
        }
        RecordLast("obsidian", singleBook?.Title, booksDone, marksDone);
        return (booksDone, marksDone);
    }

    public async Task<(int BooksCount, int MarksCount)> ExportToNotionAsync(BookSummary? singleBook = null, CancellationToken ct = default)
    {
        string token = _library.Secrets.Get("export.notion.token").Trim();
        string? rootPageId = ExtractNotionPageId(_library.Prefs.NotionPageId);
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(rootPageId))
            throw new InvalidOperationException("请先在「设置 → 笔记导出」中配置 Notion Token 与目标页面");

        var sentMap = GetSentMap("notion");
        var targets = singleBook is not null ? [singleBook] : _library.GetKeptBooks();
        int booksDone = 0, marksDone = 0;

        foreach (var b in targets)
        {
            ct.ThrowIfCancellationRequested();
            var allMarks = _library.GetMarks(b.Id);
            var unsent = allMarks.Where(m => !sentMap.TryGetValue(m.Id, out int h) || h != ComputeMarkHash(m)).ToArray();
            if (unsent.Length == 0) continue;

            var detail = _library.GetBookDetail(b.Id);
            var blocks = BuildNotionBlocks(detail, unsent);
            if (blocks.Count == 0) continue;

            _library.Prefs.NotionBookPageIds.TryGetValue(b.Id, out string? bookPageId);
            int offset = 0;

            if (string.IsNullOrEmpty(bookPageId))
            {
                var firstBatch = blocks.Take(100).ToArray();
                bookPageId = await CreateNotionBookSubpageAsync(token, rootPageId, b, firstBatch, ct);
                _library.Prefs.NotionBookPageIds[b.Id] = bookPageId;
                offset = firstBatch.Length;
            }

            while (offset < blocks.Count)
            {
                var batch = blocks.Skip(offset).Take(100).ToArray();
                bool ok = await AppendNotionBlocksAsync(token, bookPageId!, batch, ct);
                if (!ok)
                {
                    // 404 recovery: page deleted in Notion -> recreate subpage cleanly
                    _library.Prefs.NotionBookPageIds.Remove(b.Id);
                    var firstBatch = blocks.Take(100).ToArray();
                    bookPageId = await CreateNotionBookSubpageAsync(token, rootPageId, b, firstBatch, ct);
                    _library.Prefs.NotionBookPageIds[b.Id] = bookPageId;
                    offset = firstBatch.Length;
                    continue;
                }
                offset += batch.Length;
            }

            foreach (var m in unsent) sentMap[m.Id] = ComputeMarkHash(m);
            booksDone++;
            marksDone += unsent.Length;
        }

        _library.SavePrefs();
        RecordLast("notion", singleBook?.Title, booksDone, marksDone);
        return (booksDone, marksDone);
    }

    public async Task<(int BooksCount, int MarksCount)> ExportToFlomoAsync(BookSummary? singleBook = null, CancellationToken ct = default)
    {
        string url = _library.Secrets.Get("export.flomo.url").Trim();
        if (string.IsNullOrEmpty(url)) url = _library.Prefs.FlomoWebhookUrl.Trim();
        if (!ValidateFlomoWebhook(url))
            throw new InvalidOperationException("请先在「设置 → 笔记导出」中填写有效的 flomo Webhook 地址");

        var sentMap = GetSentMap("flomo");
        var targets = singleBook is not null ? [singleBook] : _library.GetKeptBooks();
        int booksDone = 0, marksDone = 0;

        foreach (var b in targets)
        {
            var unsent = _library.GetMarks(b.Id).Where(m => !sentMap.TryGetValue(m.Id, out int h) || h != ComputeMarkHash(m)).ToArray();
            if (unsent.Length == 0) continue;

            string author = b.Authors.FirstOrDefault() ?? "";
            string bookTag = SanitizeTagName(b.Title);
            foreach (var m in unsent)
            {
                ct.ThrowIfCancellationRequested();
                var sb = new StringBuilder();
                sb.Append(m.Text.Trim());
                if (!string.IsNullOrWhiteSpace(m.Note)) sb.Append("\n\n").Append(m.Note!.Trim());
                sb.Append($"\n\n——《{b.Title}》{(string.IsNullOrEmpty(author) ? "" : " " + author)}\n#语阅/{bookTag}");

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(new { content = sb.ToString() }), Encoding.UTF8, "application/json"),
                };
                using var resp = await Http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("code", out var cp) && cp.GetInt32() != 0)
                    throw new InvalidOperationException("flomo 返回错误：" + json);

                sentMap[m.Id] = ComputeMarkHash(m);
                marksDone++;
            }
            booksDone++;
        }

        _library.SavePrefs();
        RecordLast("flomo", singleBook?.Title, booksDone, marksDone);
        return (booksDone, marksDone);
    }

    public async Task<(int BooksCount, int MarksCount)> ExportToXinzhiAsync(BookSummary? singleBook = null, CancellationToken ct = default)
    {
        string token = _library.Secrets.Get("export.xinzhi.token").Trim();
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException("请先在「设置 → 笔记导出」中连接新枝账号");

        var sentMap = GetSentMap("xinzhi");
        var targets = singleBook is not null ? [singleBook] : _library.GetKeptBooks();
        int booksDone = 0, marksDone = 0;

        foreach (var b in targets)
        {
            var unsent = _library.GetMarks(b.Id).Where(m => !sentMap.TryGetValue(m.Id, out int h) || h != ComputeMarkHash(m)).ToArray();
            if (unsent.Length == 0) continue;

            string author = b.Authors.FirstOrDefault() ?? "";
            string bookTag = $"语阅/{SanitizeTagName(b.Title)}";
            foreach (var m in unsent)
            {
                ct.ThrowIfCancellationRequested();
                var sb = new StringBuilder();
                sb.Append(m.Text.Trim());
                if (!string.IsNullOrWhiteSpace(m.Note)) sb.Append("\n\n").Append(m.Note!.Trim());
                sb.Append($"\n\n——《{b.Title}》{(string.IsNullOrEmpty(author) ? "" : " " + author)}");

                using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.xinzhi.zone/api/cli/note/create-memo");
                req.Headers.Add("X-CLI-Token", token);
                req.Headers.Add("x-client", "CLI");
                req.Headers.Add("x-request-source", "xinzhi-cli");
                req.Content = new StringContent(
                    JsonSerializer.Serialize(new { content = sb.ToString(), tagNames = new[] { bookTag } }),
                    Encoding.UTF8,
                    "application/json");
                using var resp = await Http.SendAsync(req, ct);
                resp.EnsureSuccessStatusCode();

                sentMap[m.Id] = ComputeMarkHash(m);
                marksDone++;
            }
            booksDone++;
        }

        _library.SavePrefs();
        RecordLast("xinzhi", singleBook?.Title, booksDone, marksDone);
        return (booksDone, marksDone);
    }

    public async Task ExportAnnotatedEpubAsync(string bookId, string outputPath)
    {
        string tmp = outputPath + ".part.epub";
        try
        {
            await Task.Run(() => _library.Core.ExportEpub(bookId, true, tmp));
            File.Move(tmp, outputPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public void DisconnectTarget(string target)
    {
        // Per NoteExport.swift: delete credentials & last record, but RETAIN .sent dedup map!
        switch (target)
        {
            case "obsidian":
                _library.Prefs.ObsidianFolder = "";
                _library.Prefs.ObsidianExportOn = false;
                break;
            case "notion":
                _library.Secrets.Set("export.notion.token", "");
                _library.Prefs.NotionPageId = "";
                _library.Prefs.NotionPageTitle = "";
                _library.Prefs.NotionExportOn = false;
                break;
            case "flomo":
                _library.Secrets.Set("export.flomo.url", "");
                _library.Prefs.FlomoWebhookUrl = "";
                _library.Prefs.FlomoExportOn = false;
                break;
            case "xinzhi":
                _library.Secrets.Set("export.xinzhi.token", "");
                _library.Prefs.XinzhiConnected = "";
                _library.Prefs.XinzhiExportOn = false;
                break;
        }
        _library.Prefs.ExportLastSummary.Remove(target);
        _library.SavePrefs();
    }

    private Dictionary<string, int> GetSentMap(string target)
    {
        if (!_library.Prefs.ExportSentHashes.TryGetValue(target, out var map))
        {
            map = new Dictionary<string, int>();
            _library.Prefs.ExportSentHashes[target] = map;
        }
        return map;
    }

    private void RecordLast(string target, string? bookTitle, int books, int count)
    {
        if (count <= 0) return;
        string summary = bookTitle is not null
            ? $"上次发送《{bookTitle}》{count} 条 · {DateTime.Now:M/d HH:mm}"
            : $"上次发送 {books} 本书共 {count} 条 · {DateTime.Now:M/d HH:mm}";
        _library.Prefs.ExportLastSummary[target] = summary;
        _library.SavePrefs();
    }

    private static string SanitizeTagName(string s) =>
        Regex.Replace(s, @"[\s#/\r\n]+", "_").Trim('_');

    private static HttpRequestMessage MakeNotionRequest(HttpMethod method, string url, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("Notion-Version", "2022-06-28");
        return req;
    }

    private static string? ParseNotionPageTitle(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("title", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    {
                        var parts = arr.EnumerateArray()
                            .Select(x => x.TryGetProperty("plain_text", out var pt) ? pt.GetString() ?? "" : "")
                            .ToArray();
                        string joined = string.Concat(parts).Trim();
                        if (!string.IsNullOrEmpty(joined)) return joined;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static List<object> BuildNotionBlocks(BookDetail detail, IReadOnlyList<Mark> marks)
    {
        static object[] RichTextChunks(string text)
        {
            var list = new List<object>();
            for (int i = 0; i < text.Length; i += 1900)
            {
                string part = text.Substring(i, Math.Min(1900, text.Length - i));
                list.Add(new { type = "text", text = new { content = part } });
            }
            if (list.Count == 0) list.Add(new { type = "text", text = new { content = "" } });
            return list.ToArray();
        }

        var blocks = new List<object>();
        foreach (var grp in marks.GroupBy(m => m.Section).OrderBy(g => g.Key))
        {
            string chapter = grp.Key < detail.Sections.Length && !string.IsNullOrWhiteSpace(detail.Sections[grp.Key].Title)
                ? detail.Sections[grp.Key].Title!
                : $"第 {grp.Key + 1} 节";
            blocks.Add(new
            {
                @object = "block",
                type = "heading_2",
                heading_2 = new { rich_text = RichTextChunks(chapter) },
            });

            foreach (var m in grp)
            {
                blocks.Add(new
                {
                    @object = "block",
                    type = "quote",
                    quote = new { rich_text = RichTextChunks(m.Text.Trim()) },
                });
                if (!string.IsNullOrWhiteSpace(m.Note))
                {
                    blocks.Add(new
                    {
                        @object = "block",
                        type = "paragraph",
                        paragraph = new { rich_text = RichTextChunks(m.Note!.Trim()) },
                    });
                }
            }
        }
        return blocks;
    }

    private static async Task<string> CreateNotionBookSubpageAsync(
        string token, string parentPageId, BookSummary book, object[] initialBlocks, CancellationToken ct)
    {
        string titleLine = book.Authors.Length > 0 ? $"《{book.Title}》 · {book.Authors[0]}" : $"《{book.Title}》";
        var payload = new
        {
            parent = new { page_id = parentPageId },
            properties = new
            {
                title = new
                {
                    title = new[] { new { type = "text", text = new { content = titleLine } } },
                },
            },
            children = initialBlocks,
        };
        using var req = MakeNotionRequest(HttpMethod.Post, "https://api.notion.com/v1/pages", token);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"创建 Notion 子页面失败 ({(int)resp.StatusCode})：{(body.Length > 140 ? body[..140] : body)}");
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    private static async Task<bool> AppendNotionBlocksAsync(
        string token, string bookPageId, object[] batch, CancellationToken ct)
    {
        using var req = MakeNotionRequest(HttpMethod.Patch, $"https://api.notion.com/v1/blocks/{bookPageId}/children", token);
        req.Content = new StringContent(JsonSerializer.Serialize(new { children = batch }), Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return false;
        resp.EnsureSuccessStatusCode();
        return true;
    }
}
