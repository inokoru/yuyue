using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Yuyue.Audiobook;
using Yuyue.Design;

namespace Yuyue.Core;

public sealed record RemoteSourceConfig(
    string Id,
    string Name,
    string Kind, // "opds" | "webdav"
    string Url,
    string Username);

public sealed record RemoteEntryItem(
    string Title,
    string Subtitle,
    string Url,
    bool IsFolder,
    string? FormatExt,
    long? SizeBytes,
    string? CoverUrl = null,
    string? Author = null);

public sealed class ShareStreamManifest
{
    public string SourceId { get; set; } = "";
    public string BookId { get; set; } = "";
    public List<ShareStreamTrack> Tracks { get; set; } = [];
}

public sealed class ShareStreamTrack
{
    public int Index { get; set; }
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long LastPlayedUnixSec { get; set; }
}

/// <summary>
/// 1:1 port of <c>apple/Yuyue/App/RemoteLibrary.swift</c>:
/// OPDS 1.x Atom catalogue browser (with OpenSearch description XML resolution, cover thumbnails,
/// pagination &amp; format ranking), WebDAV remote book/audiobook folder browser,
/// WebDAV <c>ShareStream</c> ("边下边听" via HTTP Range head+tail 1 MB hash → <c>Core.AudiobookReserve</c>
/// → on-demand track download → <c>Core.AudiobookFileArrived</c> + 24h auto-cleanup),
/// persistent <c>已在书架</c> tracking, download progress %, and edit-source UI.
/// </summary>
public sealed class RemoteLibraryService
{
    private static readonly string[] FormatPriority =
        ["epub", "azw3", "mobi", "azw", "pdf", "txt", "m4b", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav"];

    private const int HashSliceBytes = 1024 * 1024; // 1 MB head & tail for AudioIdParts

    private readonly LibraryService _library;

    public RemoteLibraryService(LibraryService library)
    {
        _library = library;
    }

    public List<RemoteSourceConfig> GetSources()
    {
        if (string.IsNullOrWhiteSpace(_library.Prefs.RemoteSourcesJson)) return [];
        try { return JsonSerializer.Deserialize<List<RemoteSourceConfig>>(_library.Prefs.RemoteSourcesJson) ?? []; }
        catch { return []; }
    }

    public void SaveSources(List<RemoteSourceConfig> sources)
    {
        _library.Prefs.RemoteSourcesJson = JsonSerializer.Serialize(sources);
        _library.SavePrefs();
    }

    public void RemoveSource(string id)
    {
        var list = GetSources();
        list.RemoveAll(s => s.Id == id);
        _library.Secrets.Set($"source.{id}.password", "");
        SaveSources(list);
    }

    private HttpClient CreateClient(RemoteSourceConfig src, int timeoutSec = 30)
    {
        string pwd = _library.Secrets.Get($"source.{src.Id}.password");
        var handler = new HttpClientHandler();
        if (!string.IsNullOrEmpty(src.Username) || !string.IsNullOrEmpty(pwd))
        {
            handler.Credentials = new NetworkCredential(src.Username, pwd);
            handler.PreAuthenticate = true;
        }
        var client = new HttpClient(handler) { Timeout = System.TimeSpan.FromSeconds(timeoutSec) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yuyue/0.1");
        if (!string.IsNullOrEmpty(src.Username))
        {
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{src.Username}:{pwd}"));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }
        return client;
    }

    public async Task<(bool Ok, string Message)> ProbeSourceAsync(RemoteSourceConfig src, CancellationToken ct = default)
    {
        try
        {
            var (items, _, _) = await FetchPageAsync(src, null, ct);
            return (true, $"连接正常（共 {items.Count} 项）");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    public async Task<(IReadOnlyList<RemoteEntryItem> Items, string? SearchTemplate, string? NextPageUrl)> FetchPageAsync(
        RemoteSourceConfig src, string? overrideUrl = null, CancellationToken ct = default)
    {
        string targetUrl = overrideUrl ?? src.Url;
        using var client = CreateClient(src);

        if (src.Kind == "webdav")
        {
            if (!targetUrl.EndsWith('/')) targetUrl += "/";
            var baseUri = new Uri(targetUrl);
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), baseUri);
            req.Headers.Add("Depth", "1");
            using var resp = await client.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            string xml = await resp.Content.ReadAsStringAsync(ct);
            var doc = XDocument.Parse(xml);
            XNamespace dav = "DAV:";

            var folders = new List<RemoteEntryItem>();
            var files = new List<RemoteEntryItem>();

            foreach (var r in doc.Descendants(dav + "response"))
            {
                string? href = r.Element(dav + "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                var itemUri = new Uri(baseUri, href);
                if (Uri.Compare(itemUri, baseUri, UriComponents.Path, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0)
                    continue;

                bool isDir = r.Descendants(dav + "collection").Any();
                string rawName = Uri.UnescapeDataString(itemUri.Segments.LastOrDefault()?.Trim('/') ?? "");
                if (string.IsNullOrEmpty(rawName) || rawName.StartsWith('.')) continue;

                if (isDir)
                {
                    folders.Add(new RemoteEntryItem(rawName, "文件夹", itemUri.ToString(), true, null, null));
                }
                else
                {
                    string ext = Path.GetExtension(rawName).TrimStart('.').ToLowerInvariant();
                    if (!FormatPriority.Contains(ext)) continue;
                    long? len = long.TryParse(r.Descendants(dav + "getcontentlength").FirstOrDefault()?.Value, out var l) ? l : null;
                    string sizeNote = len is > 0 ? $"{len.Value / 1024.0 / 1024.0:0.1} MB" : "";
                    files.Add(new RemoteEntryItem(
                        Path.GetFileNameWithoutExtension(rawName),
                        string.IsNullOrEmpty(sizeNote) ? ext.ToUpperInvariant() : $"{ext.ToUpperInvariant()} · {sizeNote}",
                        itemUri.ToString(),
                        false,
                        ext,
                        len));
                }
            }

            var combined = folders.OrderBy(x => x.Title).Concat(files.OrderBy(x => x.Title)).ToArray();
            return (combined, null, null);
        }
        else
        {
            var baseUri = new Uri(targetUrl);
            string xml = await client.GetStringAsync(baseUri, ct);
            var doc = XDocument.Parse(xml);
            XNamespace atom = "http://www.w3.org/2005/Atom";

            string? nextUrl = null;
            string? searchTpl = null;
            string? openSearchXmlUrl = null;
            foreach (var link in doc.Root?.Elements(atom + "link") ?? Enumerable.Empty<XElement>())
            {
                string rel = link.Attribute("rel")?.Value ?? "";
                string type = (link.Attribute("type")?.Value ?? "").ToLowerInvariant();
                string? href = link.Attribute("href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                string resolved = UpgradeIfHttps(baseUri, new Uri(baseUri, href)).ToString();

                if (rel == "next") nextUrl = resolved;
                else if (rel == "search")
                {
                    if (resolved.Contains("{searchTerms}", StringComparison.Ordinal))
                        searchTpl = resolved;
                    else if (type.Contains("opensearchdescription+xml"))
                        openSearchXmlUrl = resolved;
                }
            }

            // Resolve OpenSearch Description XML (1:1 RemoteLibrary.swift)
            if (searchTpl is null && openSearchXmlUrl is not null)
            {
                try
                {
                    string osXml = await client.GetStringAsync(openSearchXmlUrl, ct);
                    var osDoc = XDocument.Parse(osXml);
                    var urlEl = osDoc.Descendants().FirstOrDefault(e =>
                        e.Name.LocalName.Equals("Url", StringComparison.OrdinalIgnoreCase) &&
                        (e.Attribute("template")?.Value.Contains("{searchTerms}", StringComparison.Ordinal) == true));
                    if (urlEl?.Attribute("template")?.Value is string tpl)
                    {
                        var osBase = new Uri(openSearchXmlUrl);
                        searchTpl = UpgradeIfHttps(baseUri, new Uri(osBase, tpl)).ToString();
                    }
                }
                catch { }
            }

            var items = new List<RemoteEntryItem>();
            foreach (var entry in doc.Descendants(atom + "entry"))
            {
                string title = entry.Element(atom + "title")?.Value.Trim() ?? "未命名条目";
                string author = entry.Element(atom + "author")?.Element(atom + "name")?.Value.Trim() ?? "";
                var links = entry.Elements(atom + "link").ToArray();

                (string Url, string Ext)? bestAcq = null;
                int bestRank = int.MaxValue;
                string? subFeedUrl = null;
                string? coverUrl = null;

                foreach (var lk in links)
                {
                    string rel = lk.Attribute("rel")?.Value ?? "";
                    string type = (lk.Attribute("type")?.Value ?? "").ToLowerInvariant();
                    string? href = lk.Attribute("href")?.Value;
                    if (string.IsNullOrEmpty(href)) continue;
                    string full = UpgradeIfHttps(baseUri, new Uri(baseUri, href)).ToString();

                    if (rel.Contains("image/thumbnail", StringComparison.OrdinalIgnoreCase) ||
                        (coverUrl is null && (rel.EndsWith("/image", StringComparison.OrdinalIgnoreCase) || type.StartsWith("image/", StringComparison.Ordinal))))
                    {
                        coverUrl = full;
                        continue;
                    }

                    if (type.Contains("application/atom+xml") && !rel.Contains("acquisition"))
                    {
                        subFeedUrl ??= full;
                        continue;
                    }

                    string? ext = InferFormatFromLink(type, full);
                    if (ext is not null && (rel.Contains("acquisition") || FormatPriority.Contains(ext)))
                    {
                        int rank = Array.IndexOf(FormatPriority, ext);
                        if (rank >= 0 && rank < bestRank)
                        {
                            bestRank = rank;
                            bestAcq = (full, ext);
                        }
                    }
                }

                if (bestAcq is var (acqUrl, acqExt))
                {
                    string sub = string.IsNullOrEmpty(author) ? acqExt.ToUpperInvariant() : $"{author} · {acqExt.ToUpperInvariant()}";
                    items.Add(new RemoteEntryItem(title, sub, acqUrl, false, acqExt, null, coverUrl, string.IsNullOrEmpty(author) ? null : author));
                }
                else if (subFeedUrl is not null)
                {
                    string sum = entry.Element(atom + "content")?.Value.Trim() ?? entry.Element(atom + "summary")?.Value.Trim() ?? "分类目录";
                    items.Add(new RemoteEntryItem(title, sum, subFeedUrl, true, null, null, coverUrl));
                }
            }

            return (items, searchTpl, nextUrl);
        }
    }

    public async Task<BookSummary> DownloadAndImportEntryAsync(
        RemoteSourceConfig src,
        RemoteEntryItem item,
        Action<double>? onProgress = null,
        CancellationToken ct = default)
    {
        using var client = CreateClient(src, timeoutSec: 180);
        string ext = "." + (item.FormatExt ?? "epub").TrimStart('.');
        string tempDir = Path.Combine(Path.GetTempPath(), "yuyue-remote-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        string safeName = string.Concat(item.Title.Split(Path.GetInvalidFileNameChars())).Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = "book";
        string dest = Path.Combine(tempDir, safeName + ext);

        try
        {
            using var resp = await client.GetAsync(item.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? item.SizeBytes ?? 0;
            await using (var remote = await resp.Content.ReadAsStreamAsync(ct))
            await using (var local = File.Create(dest))
            {
                byte[] buf = new byte[65536];
                long done = 0;
                int read;
                while ((read = await remote.ReadAsync(buf, ct)) > 0)
                {
                    await local.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0) onProgress?.Invoke(Math.Clamp((double)done / total, 0, 1));
                }
            }

            BookSummary imported = AudiobookImporter.Extensions.Contains(ext.ToLowerInvariant())
                ? await AudiobookImporter.ImportFilesAsync(_library, [dest])
                : await _library.ImportBookWithExtrasAsync(dest, fallbackTitle: item.Title, fallbackAuthor: item.Author);

            _library.Prefs.RemoteAcquiredUrls.Add(item.Url);
            _library.SavePrefs();
            return imported;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// 1:1 WebDAV <c>ShareStream</c> ("边下边听"): hashes head 1 MB + tail 1 MB of each WebDAV audio track via
    /// HTTP Range, calls <see cref="YuyueCore.AudioIdParts"/> + <see cref="YuyueCore.AudiobookReserve"/>,
    /// downloads the first track so playback starts immediately, and saves a stream manifest for on-demand fetching.
    /// </summary>
    public async Task<BookSummary> ReserveWebDavAudiobookStreamAsync(
        RemoteSourceConfig src,
        string bookTitle,
        IReadOnlyList<RemoteEntryItem> audioTracks,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        if (audioTracks.Count == 0)
            throw new InvalidOperationException("当前文件夹里没有可播放的音频文件。");

        using var client = CreateClient(src, timeoutSec: 120);
        var parts = new List<AudioIdPart>(audioTracks.Count);
        var files = new List<AudioFile>(audioTracks.Count);
        var manifestTracks = new List<ShareStreamTrack>(audioTracks.Count);

        for (int i = 0; i < audioTracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tr = audioTracks[i];
            onStatus?.Invoke($"正在计算第 {i + 1}/{audioTracks.Count} 轨指纹…");

            long size = tr.SizeBytes ?? 0;
            if (size <= 0)
            {
                using var headReq = new HttpRequestMessage(HttpMethod.Head, tr.Url);
                using var headResp = await client.SendAsync(headReq, ct);
                size = headResp.Content.Headers.ContentLength ?? 0;
            }
            if (size <= 0) throw new InvalidOperationException($"无法获取音频大小：{tr.Title}");

            byte[] headSlice = await FetchRangeBytesAsync(client, tr.Url, 0, Math.Min(size - 1, HashSliceBytes - 1), ct);
            long tailStart = Math.Max(0, size - HashSliceBytes);
            byte[] tailSlice = tailStart == 0 ? headSlice : await FetchRangeBytesAsync(client, tr.Url, tailStart, size - 1, ct);

            parts.Add(new AudioIdPart((ulong)size, headSlice, tailSlice));
            string fileName = Path.GetFileName(new Uri(tr.Url).AbsolutePath);
            fileName = Uri.UnescapeDataString(string.IsNullOrEmpty(fileName) ? $"{tr.Title}.{tr.FormatExt ?? "mp3"}" : fileName);
            files.Add(new AudioFile(fileName, 0));
            manifestTracks.Add(new ShareStreamTrack
            {
                Index = i,
                Url = tr.Url,
                FileName = fileName,
                SizeBytes = size,
            });
        }

        string bookId = _library.Core.AudioIdParts(parts.ToArray());
        var import = new AudioImport(files.ToArray(), bookTitle, [], [], null);
        var summary = _library.Core.AudiobookReserve(bookId, import);

        string manifestDir = Path.Combine(_library.DataDir, "audiobooks", bookId);
        Directory.CreateDirectory(manifestDir);
        var manifest = new ShareStreamManifest { SourceId = src.Id, BookId = bookId, Tracks = manifestTracks };
        File.WriteAllText(Path.Combine(manifestDir, "share-stream.json"), JsonSerializer.Serialize(manifest));

        onStatus?.Invoke("正在缓存第 1 轨，马上就能听…");
        await EnsureStreamTrackAsync(bookId, 0, ct);
        _library.Prefs.RemoteAcquiredUrls.Add(audioTracks[0].Url);
        _library.SavePrefs();
        return summary;
    }

    public async Task<bool> EnsureStreamTrackAsync(string bookId, int trackIndex, CancellationToken ct = default)
    {
        string manifestPath = Path.Combine(_library.DataDir, "audiobooks", bookId, "share-stream.json");
        if (!File.Exists(manifestPath)) return false;
        ShareStreamManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<ShareStreamManifest>(File.ReadAllText(manifestPath)); }
        catch { return false; }
        if (manifest is null) return false;

        var track = manifest.Tracks.FirstOrDefault(t => t.Index == trackIndex);
        var src = GetSources().FirstOrDefault(s => s.Id == manifest.SourceId);
        if (track is null || src is null) return false;

        string tmpDir = Path.Combine(Path.GetTempPath(), "yuyue-stream-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmpDir);
        string tmpPath = Path.Combine(tmpDir, track.FileName);
        try
        {
            using var client = CreateClient(src, timeoutSec: 180);
            using var resp = await client.GetAsync(track.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using (var remote = await resp.Content.ReadAsStreamAsync(ct))
            await using (var local = File.Create(tmpPath))
                await remote.CopyToAsync(local, 65536, ct);

            double secs = 0;
            try { secs = await AudiobookImporter.ProbeDurationSecondsAsync(tmpPath); } catch { }
            _library.Core.AudiobookFileArrived(bookId, (uint)trackIndex, tmpPath, secs);
            track.LastPlayedUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
            PruneOldStreamTracks(manifest);
            return true;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>Auto-cleans cached stream tracks listened more than 24 hours ago (1:1 ShareStream 24h cleanup).</summary>
    private void PruneOldStreamTracks(ShareStreamManifest manifest)
    {
        try
        {
            long cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
            var rec = _library.Core.AudioRecording(manifest.BookId);
            foreach (var tr in manifest.Tracks)
            {
                if (tr.LastPlayedUnixSec > 0 && tr.LastPlayedUnixSec < cutoff && tr.Index < rec.Files.Length)
                {
                    string fp = rec.Files[tr.Index].Path;
                    if (File.Exists(fp)) try { File.Delete(fp); } catch { }
                }
            }
        }
        catch { }
    }

    private static async Task<byte[]> FetchRangeBytesAsync(HttpClient client, string url, long from, long to, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new RangeHeaderValue(from, to);
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task ShowRemoteLibraryModalAsync(XamlRoot xamlRoot, Action onImported)
    {
        bool dark = ThemeHelper.IsDark(xamlRoot.Content is FrameworkElement fe ? fe.ActualTheme : ElementTheme.Light);
        var p = ThemeHelper.Palette(dark);
        var tint = ThemeHelper.TintColors(_library.Prefs.Tint, dark);

        var sources = GetSources();
        RemoteSourceConfig? activeSrc = sources.FirstOrDefault();
        var navStack = new Stack<string>();
        string? currentSearchTpl = null;
        string? editingSourceId = null;

        var root = new Grid { Width = 620, Height = 470, ColumnSpacing = 12 };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(178) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var leftList = new StackPanel { Spacing = 4 };
        var rightStack = new Grid { RowSpacing = 8 };
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rightStack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rightStack.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var topBar = new Grid { ColumnSpacing = 6 };
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        topBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var backBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], Content = "‹ 上级", Visibility = Visibility.Collapsed };
        var searchBox = new TextBox { PlaceholderText = "搜索 OPDS 书目（回车）…", Visibility = Visibility.Collapsed };
        var streamAllBtn = new Button { Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"], Content = "边下边听整本", Height = 26, FontSize = 11, Visibility = Visibility.Collapsed };
        topBar.Children.Add(backBtn);
        Grid.SetColumn(searchBox, 1);
        topBar.Children.Add(searchBox);
        Grid.SetColumn(streamAllBtn, 2);
        topBar.Children.Add(streamAllBtn);
        rightStack.Children.Add(topBar);

        var entriesPanel = new StackPanel { Spacing = 4 };
        var scroll = new ScrollViewer { Content = entriesPanel };
        Grid.SetRow(scroll, 1);
        rightStack.Children.Add(scroll);

        var statusText = new TextBlock { FontSize = 11.5, Foreground = ThemeHelper.Brush(p.T3), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(statusText, 2);
        rightStack.Children.Add(statusText);

        IReadOnlyList<RemoteEntryItem> currentAudioItems = [];
        string currentFolderTitle = "有声书";

        streamAllBtn.Click += async (_, _) =>
        {
            if (activeSrc is null || currentAudioItems.Count == 0) return;
            streamAllBtn.IsEnabled = false;
            try
            {
                var b = await ReserveWebDavAudiobookStreamAsync(activeSrc, currentFolderTitle, currentAudioItems, s => statusText.Text = s);
                statusText.Text = $"✓ 已加入《{b.Title}》（边下边听，已缓存首轨）";
                onImported();
            }
            catch (Exception ex)
            {
                statusText.Text = $"边下边听准备失败：{ex.Message}";
            }
            finally { streamAllBtn.IsEnabled = true; }
        };

        async Task LoadUrlAsync(string? url = null, string? folderTitle = null)
        {
            entriesPanel.Children.Clear();
            currentAudioItems = [];
            streamAllBtn.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(folderTitle)) currentFolderTitle = folderTitle;
            backBtn.Visibility = navStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (activeSrc is null)
            {
                statusText.Text = "先在左侧添加一个 Calibre-Web (OPDS) 或 WebDAV 书库源。";
                return;
            }
            statusText.Text = $"正在读取 {activeSrc.Name}…";
            try
            {
                var (items, searchTpl, nextUrl) = await FetchPageAsync(activeSrc, url);
                if (searchTpl is not null) currentSearchTpl = searchTpl;
                searchBox.Visibility = currentSearchTpl is not null ? Visibility.Visible : Visibility.Collapsed;
                statusText.Text = $"共 {items.Count} 项";

                if (activeSrc.Kind == "webdav")
                {
                    var audios = items.Where(i => !i.IsFolder && !string.IsNullOrEmpty(i.FormatExt) && AudiobookImporter.Extensions.Contains("." + i.FormatExt)).ToArray();
                    if (audios.Length > 0)
                    {
                        currentAudioItems = audios;
                        streamAllBtn.Visibility = Visibility.Visible;
                    }
                }

                foreach (var it in items)
                {
                    var entry = it;
                    bool alreadyOwned = !entry.IsFolder && _library.Prefs.RemoteAcquiredUrls.Contains(entry.Url);

                    var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 10, 7) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    if (!string.IsNullOrEmpty(entry.CoverUrl))
                    {
                        var thumb = new Border { Width = 24, Height = 34, CornerRadius = new CornerRadius(3), Background = ThemeHelper.Brush(p.Inset) };
                        try { thumb.Child = new Image { Source = new BitmapImage(new Uri(entry.CoverUrl)), Stretch = Stretch.UniformToFill }; } catch { }
                        row.Children.Add(thumb);
                    }
                    else
                    {
                        row.Children.Add(YYIcons.Make(entry.IsFolder ? "folder" : "book", 13.5, ThemeHelper.Brush(entry.IsFolder ? tint.Accent : p.T2)));
                    }

                    var meta = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
                    meta.Children.Add(new TextBlock { Text = entry.Title, FontSize = 12.5, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Foreground = ThemeHelper.Brush(p.T1), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    meta.Children.Add(new TextBlock { Text = entry.Subtitle, FontSize = 10.5, Foreground = ThemeHelper.Brush(p.T3), MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis });
                    Grid.SetColumn(meta, 1);
                    row.Children.Add(meta);

                    var actBtn = new Button
                    {
                        Style = (Style)Application.Current.Resources[entry.IsFolder || alreadyOwned ? "YYPillButtonStyle" : "YYPrimaryPillButtonStyle"],
                        Content = entry.IsFolder ? "打开 ›" : (alreadyOwned ? "✓ 已在书架" : "放入书架"),
                        Height = 25,
                        MinHeight = 25,
                        FontSize = 11,
                        IsEnabled = !alreadyOwned,
                    };
                    actBtn.Click += async (_, _) =>
                    {
                        if (entry.IsFolder)
                        {
                            navStack.Push(url ?? activeSrc.Url);
                            await LoadUrlAsync(entry.Url, entry.Title);
                        }
                        else
                        {
                            actBtn.IsEnabled = false;
                            actBtn.Content = "下载中…";
                            try
                            {
                                var b = await DownloadAndImportEntryAsync(activeSrc, entry, frac =>
                                {
                                    actBtn.DispatcherQueue.TryEnqueue(() => actBtn.Content = $"{Math.Round(frac * 100)}%");
                                });
                                actBtn.Content = "✓ 已在书架";
                                statusText.Text = $"已导入《{b.Title}》";
                                onImported();
                            }
                            catch (Exception ex)
                            {
                                actBtn.IsEnabled = true;
                                actBtn.Content = "重试";
                                statusText.Text = $"下载失败：{ex.Message}";
                            }
                        }
                    };
                    Grid.SetColumn(actBtn, 2);
                    row.Children.Add(actBtn);

                    entriesPanel.Children.Add(new Border
                    {
                        CornerRadius = new CornerRadius(8),
                        Background = ThemeHelper.Brush(p.Card),
                        BorderBrush = ThemeHelper.Brush(p.Line),
                        BorderThickness = new Thickness(1),
                        Child = row,
                    });
                }

                if (!string.IsNullOrEmpty(nextUrl))
                {
                    var moreBtn = new Button { Style = (Style)Application.Current.Resources["YYPillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Center, Content = "下一页 ›" };
                    string nu = nextUrl!;
                    moreBtn.Click += async (_, _) => { navStack.Push(url ?? activeSrc.Url); await LoadUrlAsync(nu); };
                    entriesPanel.Children.Add(moreBtn);
                }
            }
            catch (Exception ex)
            {
                statusText.Text = $"读取失败：{ex.Message}";
            }
        }

        void RebuildSourcesSidebar()
        {
            leftList.Children.Clear();
            leftList.Children.Add(new TextBlock { Text = "远程书库", FontSize = 11.5, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = ThemeHelper.Brush(p.T3), Margin = new Thickness(4, 0, 0, 4) });
            foreach (var s in sources)
            {
                var src = s;
                bool sel = activeSrc?.Id == src.Id;
                var b = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(8),
                    Background = sel ? ThemeHelper.Brush(tint.Soft) : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 7, 10, 7),
                    Content = new StackPanel
                    {
                        Spacing = 1,
                        Children =
                        {
                            new TextBlock { Text = src.Name, FontSize = 12.5, FontWeight = sel ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal, Foreground = ThemeHelper.Brush(sel ? tint.Accent : p.T1) },
                            new TextBlock { Text = src.Kind.ToUpperInvariant(), FontSize = 10, Foreground = ThemeHelper.Brush(p.T3) },
                        },
                    },
                };
                var ctx = new MenuFlyout();
                var editMi = new MenuFlyoutItem { Text = "编辑此书库源" };
                editMi.Click += (_, _) => { editingSourceId = src.Id; RebuildSourcesSidebar(); };
                var delMi = new MenuFlyoutItem { Text = "移除此书库源", Foreground = ThemeHelper.Brush(p.Danger) };
                delMi.Click += (_, _) =>
                {
                    RemoveSource(src.Id);
                    sources = GetSources();
                    activeSrc = sources.FirstOrDefault();
                    if (editingSourceId == src.Id) editingSourceId = null;
                    RebuildSourcesSidebar();
                    _ = LoadUrlAsync();
                };
                ctx.Items.Add(editMi);
                ctx.Items.Add(delMi);
                b.ContextFlyout = ctx;
                b.Click += async (_, _) =>
                {
                    activeSrc = src;
                    navStack.Clear();
                    currentSearchTpl = null;
                    RebuildSourcesSidebar();
                    await LoadUrlAsync();
                };
                leftList.Children.Add(b);
            }

            // Inline add / edit source card
            var editing = editingSourceId is not null ? sources.FirstOrDefault(x => x.Id == editingSourceId) : null;
            var nameIn = new TextBox { PlaceholderText = "名称（如 Calibre 书库）", Text = editing?.Name ?? "", FontSize = 11.5 };
            var urlIn = new TextBox { PlaceholderText = "OPDS 或 WebDAV 地址", Text = editing?.Url ?? "", FontSize = 11.5 };
            var userIn = new TextBox { PlaceholderText = "账号（可选）", Text = editing?.Username ?? "", FontSize = 11.5 };
            var pwdIn = new PasswordBox { PlaceholderText = "密码（可选）", Password = editing is not null ? _library.Secrets.Get($"source.{editing.Id}.password") : "", FontSize = 11.5 };
            var kindCombo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 11.5 };
            kindCombo.Items.Add(new ComboBoxItem { Content = "OPDS 目录 (Calibre / Komga)", Tag = "opds" });
            kindCombo.Items.Add(new ComboBoxItem { Content = "WebDAV 文件夹", Tag = "webdav" });
            kindCombo.SelectedIndex = editing?.Kind == "webdav" ? 1 : 0;

            var addSrcBtn = new Button
            {
                Style = (Style)Application.Current.Resources["YYPrimaryPillButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Content = editing is not null ? "保存修改" : "＋ 检测并添加",
                Height = 26,
                FontSize = 11.5,
            };
            addSrcBtn.Click += async (_, _) =>
            {
                string rawUrl = urlIn.Text.Trim();
                if (string.IsNullOrWhiteSpace(rawUrl)) return;
                if (!rawUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                    !rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    rawUrl = "https://" + rawUrl;
                if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var parsedUri) || string.IsNullOrEmpty(parsedUri.Host))
                {
                    statusText.Text = "地址格式不正确，请输入完整的 http(s) 链接";
                    return;
                }
                string id = editing?.Id ?? Guid.NewGuid().ToString("N")[..10];
                string kind = (kindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "opds";
                string nm = string.IsNullOrWhiteSpace(nameIn.Text) ? parsedUri.Host : nameIn.Text.Trim();
                var cfg = new RemoteSourceConfig(id, nm, kind, parsedUri.ToString(), userIn.Text.Trim());
                if (!string.IsNullOrEmpty(pwdIn.Password)) _library.Secrets.Set($"source.{id}.password", pwdIn.Password);

                addSrcBtn.IsEnabled = false;
                statusText.Text = $"正在探测 {nm}…";
                var (ok, msg) = await ProbeSourceAsync(cfg);
                statusText.Text = msg;
                addSrcBtn.IsEnabled = true;

                if (editing is not null)
                {
                    int idx = sources.FindIndex(x => x.Id == id);
                    if (idx >= 0) sources[idx] = cfg;
                }
                else
                {
                    sources.Add(cfg);
                }
                editingSourceId = null;
                SaveSources(sources);
                activeSrc = cfg;
                navStack.Clear();
                RebuildSourcesSidebar();
                if (ok) await LoadUrlAsync();
            };

            leftList.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = ThemeHelper.Brush(p.Inset),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new StackPanel { Spacing = 5, Children = { kindCombo, nameIn, urlIn, userIn, pwdIn, addSrcBtn } },
            });
        }

        backBtn.Click += async (_, _) =>
        {
            if (navStack.Count > 0) await LoadUrlAsync(navStack.Pop());
        };
        searchBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter && currentSearchTpl is not null && !string.IsNullOrWhiteSpace(searchBox.Text))
            {
                navStack.Push(activeSrc?.Url ?? "");
                await LoadUrlAsync(currentSearchTpl.Replace("{searchTerms}", Uri.EscapeDataString(searchBox.Text.Trim())));
            }
        };

        RebuildSourcesSidebar();
        _ = LoadUrlAsync();

        root.Children.Add(new ScrollViewer { Content = leftList });
        Grid.SetColumn(rightStack, 1);
        root.Children.Add(rightStack);

        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "OPDS 与 WebDAV 远程书库",
            Content = root,
            CloseButtonText = "完成",
        }.ShowAsync();
    }

    private static Uri UpgradeIfHttps(Uri baseUri, Uri target)
    {
        if (baseUri.Scheme == "https" && target.Scheme == "http" && string.Equals(baseUri.Host, target.Host, StringComparison.OrdinalIgnoreCase))
        {
            var ub = new UriBuilder(target) { Scheme = "https", Port = -1 };
            return ub.Uri;
        }
        return target;
    }

    private static string? InferFormatFromLink(string mime, string url)
    {
        if (mime.Contains("epub")) return "epub";
        if (mime.Contains("pdf")) return "pdf";
        if (mime.Contains("mobipocket") || mime.Contains("x-mobi")) return "mobi";
        if (mime.Contains("audiobook") || mime.Contains("mp4")) return "m4b";
        if (mime.Contains("mpeg")) return "mp3";

        try
        {
            string ext = Path.GetExtension(new Uri(url).AbsolutePath).TrimStart('.').ToLowerInvariant();
            if (FormatPriority.Contains(ext)) return ext;
        }
        catch { }
        return null;
    }
}
