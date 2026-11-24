using System.Collections.Concurrent;
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
    public bool AutoClear { get; set; }
    public HashSet<int> PassedFiles { get; set; } = [];
    public Dictionary<int, long> PassedAtUnixSec { get; set; } = new();
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
    private readonly object _streamGate = new();
    private readonly Dictionary<(string BookId, int TrackIndex), Task<bool>> _inflightTracks = new();
    private readonly ConcurrentDictionary<string, Task> _bgStreamLoops = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _wantedTrack = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Creates an HTTP client supporting HTTP Digest Auth challenges (e.g. Calibre Content Server) via
    /// <c>PreAuthenticate = false</c> + <see cref="NetworkCredential"/>, while falling back to an explicit
    /// <c>Basic</c> header if a WebDAV/OPDS server rejects unauthenticated requests without a Digest challenge.
    /// </summary>
    private HttpClient CreateClient(RemoteSourceConfig src, int timeoutSec = 30, string? passwordOverride = null)
    {
        string pwd = passwordOverride ?? _library.Secrets.Get($"source.{src.Id}.password");
        var inner = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PreAuthenticate = false,
        };
        bool hasCreds = !string.IsNullOrEmpty(src.Username) || !string.IsNullOrEmpty(pwd);
        if (hasCreds)
        {
            inner.Credentials = new NetworkCredential(src.Username, pwd);
        }

        HttpMessageHandler pipeline = !string.IsNullOrEmpty(src.Username)
            ? new BasicFallbackAuthHandler(inner, src.Username, pwd)
            : inner;

        var client = new HttpClient(pipeline, disposeHandler: true)
        {
            Timeout = System.TimeSpan.FromSeconds(timeoutSec),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yuyue/0.1");
        return client;
    }

    private sealed class BasicFallbackAuthHandler : DelegatingHandler
    {
        private readonly string _basicToken;

        public BasicFallbackAuthHandler(HttpMessageHandler innerHandler, string username, string password)
            : base(innerHandler)
        {
            _basicToken = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}"));
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = await base.SendAsync(request, cancellationToken);
            if (resp.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) ||
                request.Headers.Authorization is not null)
            {
                return resp;
            }

            // If server issued a Digest challenge, HttpClientHandler.Credentials already attempted Digest auth;
            // a 401 here means credentials were rejected — do not clobber with Basic.
            bool isDigest = resp.Headers.WwwAuthenticate.Any(h =>
                string.Equals(h.Scheme, "Digest", StringComparison.OrdinalIgnoreCase));
            if (isDigest)
                return resp;

            var retryReq = await CloneRequestAsync(request, cancellationToken);
            retryReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", _basicToken);
            resp.Dispose();
            return await base.SendAsync(retryReq, cancellationToken);
        }

        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage req, CancellationToken ct)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };
            foreach (var h in req.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (req.Content is not null)
            {
                byte[] bytes = await req.Content.ReadAsByteArrayAsync(ct);
                var content = new ByteArrayContent(bytes);
                foreach (var ch in req.Content.Headers)
                    content.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
                clone.Content = content;
            }
            return clone;
        }
    }

    public async Task<(bool Ok, string Message)> ProbeSourceAsync(
        RemoteSourceConfig src, CancellationToken ct = default, string? passwordOverride = null)
    {
        try
        {
            var (items, _, _) = await FetchPageAsync(src, null, ct, passwordOverride);
            return (true, $"连接正常（共 {items.Count} 项）");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    public async Task<(IReadOnlyList<RemoteEntryItem> Items, string? SearchTemplate, string? NextPageUrl)> FetchPageAsync(
        RemoteSourceConfig src, string? overrideUrl = null, CancellationToken ct = default, string? passwordOverride = null)
    {
        string targetUrl = overrideUrl ?? src.Url;
        using var client = CreateClient(src, passwordOverride: passwordOverride);

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
                    {
                        if (type.Contains("atom", StringComparison.OrdinalIgnoreCase) || searchTpl is null)
                            searchTpl = resolved;
                    }
                    else if (type.Contains("opensearchdescription"))
                        openSearchXmlUrl = resolved;
                }
            }

            // Resolve OpenSearch Description XML (1:1 RemoteLibrary.swift: prefer type*="atom")
            if (searchTpl is null && openSearchXmlUrl is not null)
            {
                try
                {
                    string osXml = await client.GetStringAsync(openSearchXmlUrl, ct);
                    searchTpl = ParseOpenSearchTemplate(osXml, baseUri, new Uri(openSearchXmlUrl));
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

                bool isAudio = AudiobookImporter.Extensions.Contains(ext.ToLowerInvariant());
                bool isPdf = ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                byte[]? coverBytes = null;
                if ((isAudio || isPdf) && !string.IsNullOrWhiteSpace(item.CoverUrl))
                {
                    try { coverBytes = await client.GetByteArrayAsync(item.CoverUrl, ct); }
                    catch { }
                }

                BookSummary imported;
                if (isAudio)
                {
                    var pending = await AudiobookImporter.PrepareImportAsync(_library, [dest]);
                    string stem = Path.GetFileNameWithoutExtension(dest);
                    string finalTitle = !string.IsNullOrWhiteSpace(item.Title) &&
                                        (string.IsNullOrWhiteSpace(pending.Title) || string.Equals(pending.Title, stem, StringComparison.OrdinalIgnoreCase))
                        ? item.Title.Trim()
                        : pending.Title;
                    string[] fallbackAuthors = SplitAuthors(item.Author);
                    string[] finalAuthors = pending.Authors.Length == 0 && fallbackAuthors.Length > 0 ? fallbackAuthors : pending.Authors;
                    byte[]? finalCover = pending.Cover ?? coverBytes;
                    var audioImport = new AudioImport(pending.Files.ToArray(), finalTitle, finalAuthors, pending.Marks, finalCover);
                    imported = await Task.Run(() => _library.Core.ImportAudiobook(audioImport), ct);
                    var matchedEbook = AudiobookImporter.FindMatchingShelfEbook(_library, finalTitle) ?? pending.MatchedEbook;
                    if (matchedEbook is not null)
                    {
                        try { _library.Core.AudiobookPair(imported.Id, matchedEbook.Id); } catch { }
                    }
                }
                else
                {
                    imported = await _library.ImportBookWithExtrasAsync(
                        dest,
                        fallbackTitle: item.Title,
                        fallbackAuthor: item.Author,
                        fallbackCover: coverBytes);
                }

                _library.Prefs.RemoteAcquiredBooks[item.Url] = imported.Id;
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
    /// HTTP Range, calls <see cref="YuyueCore.AudioIdParts"/> + <see cref="YuyueCore.AudiobookReserve"/>
    /// (with real per-file durations from <see cref="YuyueCore.AudiobookMap"/> when available),
    /// downloads the first track so playback starts immediately, and starts a sequential background pre-downloader.
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
            manifestTracks.Add(new ShareStreamTrack
            {
                Index = i,
                Url = tr.Url,
                FileName = fileName,
                SizeBytes = size,
            });
        }

        string bookId = _library.Core.AudioIdParts(parts.ToArray());
        double[] knownDurations = [];
        try { knownDurations = _library.Core.AudiobookMap(bookId)?.Durations ?? []; } catch { }

        var files = new AudioFile[manifestTracks.Count];
        for (int i = 0; i < manifestTracks.Count; i++)
        {
            double secs = i < knownDurations.Length && knownDurations[i] > 0 ? knownDurations[i] : 0;
            files[i] = new AudioFile(manifestTracks[i].FileName, secs);
        }

        var import = new AudioImport(files, bookTitle, [], [], null);
        var summary = _library.Core.AudiobookReserve(bookId, import);

        lock (_streamGate)
        {
            var existing = LoadManifestUnlocked(bookId);
            var manifest = new ShareStreamManifest
            {
                SourceId = src.Id,
                BookId = bookId,
                AutoClear = existing?.AutoClear ?? false,
                PassedFiles = existing?.PassedFiles ?? [],
                PassedAtUnixSec = existing?.PassedAtUnixSec ?? new(),
                Tracks = manifestTracks,
            };
            SaveManifestUnlocked(manifest);
        }

        _wantedTrack[bookId] = 0;
        onStatus?.Invoke("正在缓存第 1 轨，马上就能听…");
        await EnsureStreamTrackAsync(bookId, 0, ct);
        _wantedTrack.TryAdd(bookId, 1);
        StartBackgroundStreamLoop(bookId);

        _library.Prefs.RemoteAcquiredBooks[audioTracks[0].Url] = summary.Id;
        _library.SavePrefs();
        return summary;
    }

    public bool GetStreamAutoClear(string bookId)
    {
        lock (_streamGate)
        {
            return LoadManifestUnlocked(bookId)?.AutoClear ?? false;
        }
    }

    public void SetStreamAutoClear(string bookId, bool clear)
    {
        lock (_streamGate)
        {
            var manifest = LoadManifestUnlocked(bookId);
            if (manifest is null) return;
            manifest.AutoClear = clear;
            SaveManifestUnlocked(manifest);
            if (clear) PruneOldStreamTracksUnlocked(manifest);
        }
        if (!clear) StartBackgroundStreamLoop(bookId);
    }

    public void MarkStreamTrackPassed(string bookId, int trackIdx)
    {
        if (trackIdx < 0) return;
        lock (_streamGate)
        {
            var manifest = LoadManifestUnlocked(bookId);
            if (manifest is null) return;
            manifest.PassedFiles.Add(trackIdx);
            if (!manifest.PassedAtUnixSec.ContainsKey(trackIdx))
                manifest.PassedAtUnixSec[trackIdx] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            SaveManifestUnlocked(manifest);
            if (manifest.AutoClear)
                PruneOldStreamTracksUnlocked(manifest);
        }
    }

    public async Task<bool> EnsureStreamTrackAsync(string bookId, int trackIndex, CancellationToken ct = default)
    {
        _wantedTrack[bookId] = trackIndex;
        lock (_streamGate)
        {
            var manifest = LoadManifestUnlocked(bookId);
            if (manifest is not null)
            {
                // Listening back to an earlier/passed chapter removes it (and later ones) from passedFiles (#38)
                bool removed = false;
                foreach (int p in manifest.PassedFiles.Where(k => k >= trackIndex).ToArray())
                {
                    manifest.PassedFiles.Remove(p);
                    manifest.PassedAtUnixSec.Remove(p);
                    removed = true;
                }
                if (removed) SaveManifestUnlocked(manifest);
            }
        }

        bool ok = await DownloadOrJoinStreamTrackAsync(bookId, trackIndex, ct);
        StartBackgroundStreamLoop(bookId);
        return ok;
    }

    private Task<bool> DownloadOrJoinStreamTrackAsync(string bookId, int trackIndex, CancellationToken callerCt)
    {
        Task<bool> sharedTask;
        lock (_streamGate)
        {
            var key = (bookId, trackIndex);
            if (!_inflightTracks.TryGetValue(key, out sharedTask!))
            {
                sharedTask = DownloadStreamTrackCoreAsync(bookId, trackIndex);
                _inflightTracks[key] = sharedTask;
            }
        }
        return callerCt.CanBeCanceled ? sharedTask.WaitAsync(callerCt) : sharedTask;
    }

    private async Task<bool> DownloadStreamTrackCoreAsync(string bookId, int trackIndex)
    {
        try
        {
            try
            {
                var rec = _library.Core.AudioRecording(bookId);
                if (trackIndex >= 0 && trackIndex < rec.Files.Length &&
                    File.Exists(rec.Files[trackIndex].Path) &&
                    new FileInfo(rec.Files[trackIndex].Path).Length > 0)
                {
                    TouchStreamTrackPlayed(bookId, trackIndex);
                    return true;
                }
            }
            catch { }

            ShareStreamTrack? track;
            RemoteSourceConfig? src;
            lock (_streamGate)
            {
                var manifest = LoadManifestUnlocked(bookId);
                if (manifest is null) return false;
                track = manifest.Tracks.FirstOrDefault(t => t.Index == trackIndex);
                src = GetSources().FirstOrDefault(s => s.Id == manifest.SourceId);
            }
            if (track is null || src is null) return false;

            string tmpDir = Path.Combine(Path.GetTempPath(), "yuyue-stream-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(tmpDir);
            string tmpPath = Path.Combine(tmpDir, track.FileName);
            try
            {
                using var client = CreateClient(src, timeoutSec: 180);
                using var resp = await client.GetAsync(track.Url, HttpCompletionOption.ResponseHeadersRead);
                resp.EnsureSuccessStatusCode();
                await using (var remote = await resp.Content.ReadAsStreamAsync())
                await using (var local = File.Create(tmpPath))
                    await remote.CopyToAsync(local, 65536);

                double secs = 0;
                try { secs = await AudiobookImporter.ProbeDurationSecondsAsync(tmpPath); } catch { }
                _library.Core.AudiobookFileArrived(bookId, (uint)trackIndex, tmpPath, secs);
                TouchStreamTrackPlayed(bookId, trackIndex);
                return true;
            }
            finally
            {
                try { Directory.Delete(tmpDir, recursive: true); } catch { }
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            lock (_streamGate)
            {
                _inflightTracks.Remove((bookId, trackIndex));
            }
        }
    }

    private void StartBackgroundStreamLoop(string bookId)
    {
        _bgStreamLoops.GetOrAdd(bookId, id => Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    int? nextIdx = PickNextMissingStreamTrack(id);
                    if (nextIdx is null) break;
                    bool ok = await DownloadOrJoinStreamTrackAsync(id, nextIdx.Value, CancellationToken.None);
                    if (!ok) break;
                }
            }
            finally
            {
                _bgStreamLoops.TryRemove(id, out _);
            }
        }));
    }

    private int? PickNextMissingStreamTrack(string bookId)
    {
        lock (_streamGate)
        {
            var manifest = LoadManifestUnlocked(bookId);
            if (manifest is null || manifest.Tracks.Count <= 1) return null;
            AudioRecording rec;
            try { rec = _library.Core.AudioRecording(bookId); }
            catch { return null; }

            var skip = manifest.AutoClear ? manifest.PassedFiles : [];
            var missing = new List<int>();
            for (int i = 0; i < rec.Files.Length && i < manifest.Tracks.Count; i++)
            {
                if (skip.Contains(i)) continue;
                string fp = rec.Files[i].Path;
                if (!File.Exists(fp) || new FileInfo(fp).Length == 0)
                    missing.Add(i);
            }
            if (missing.Count == 0) return null;

            int wanted = _wantedTrack.GetValueOrDefault(bookId, 1);
            foreach (int idx in missing)
            {
                if (idx >= wanted) return idx;
            }
            // Only wrap around to earlier missing tracks when AutoClear is off (1:1 RemoteLibrary.swift:752-755)
            return manifest.AutoClear ? null : missing[0];
        }
    }

    private void TouchStreamTrackPlayed(string bookId, int trackIndex)
    {
        lock (_streamGate)
        {
            var manifest = LoadManifestUnlocked(bookId);
            if (manifest is null) return;
            var tr = manifest.Tracks.FirstOrDefault(t => t.Index == trackIndex);
            if (tr is not null)
                tr.LastPlayedUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Chapters strictly before trackIndex have been passed in sequential listening
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (int i = 0; i < trackIndex; i++)
            {
                if (!manifest.PassedAtUnixSec.ContainsKey(i))
                    manifest.PassedAtUnixSec[i] = now;
            }
            SaveManifestUnlocked(manifest);
            if (manifest.AutoClear)
                PruneOldStreamTracksUnlocked(manifest);
        }
    }

    private string GetManifestPath(string bookId) =>
        Path.Combine(_library.DataDir, "audiobooks", bookId, "share-stream.json");

    private ShareStreamManifest? LoadManifestUnlocked(string bookId)
    {
        string path = GetManifestPath(bookId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ShareStreamManifest>(File.ReadAllText(path)); }
        catch { return null; }
    }

    private void SaveManifestUnlocked(ShareStreamManifest manifest)
    {
        string path = GetManifestPath(manifest.BookId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".part";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Auto-cleans cached stream tracks passed/listened more than 24 hours ago when AutoClear is enabled.</summary>
    private void PruneOldStreamTracksUnlocked(ShareStreamManifest manifest)
    {
        if (!manifest.AutoClear) return;
        try
        {
            long cutoff = DateTimeOffset.UtcNow.AddHours(-24).ToUnixTimeSeconds();
            int currentWanted = _wantedTrack.GetValueOrDefault(manifest.BookId, int.MaxValue);
            var rec = _library.Core.AudioRecording(manifest.BookId);
            bool changed = false;
            foreach (var tr in manifest.Tracks)
            {
                if (tr.Index >= currentWanted || tr.Index >= rec.Files.Length) continue;
                long ts = manifest.PassedAtUnixSec.TryGetValue(tr.Index, out var passedAt) && passedAt > 0
                    ? passedAt
                    : tr.LastPlayedUnixSec;
                if (ts > 0 && ts <= cutoff)
                {
                    string fp = rec.Files[tr.Index].Path;
                    if (File.Exists(fp))
                    {
                        try
                        {
                            File.Delete(fp);
                            if (manifest.PassedFiles.Add(tr.Index)) changed = true;
                        }
                        catch { }
                    }
                }
            }
            if (changed) SaveManifestUnlocked(manifest);
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
        CancellationTokenSource? loadCts = null;
        int loadSeq = 0;

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
            var prevCts = loadCts;
            var myCts = new CancellationTokenSource();
            loadCts = myCts;
            int mySeq = ++loadSeq;
            try { prevCts?.Cancel(); prevCts?.Dispose(); } catch { }

            entriesPanel.Children.Clear();
            currentAudioItems = [];
            streamAllBtn.Visibility = Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(folderTitle)) currentFolderTitle = folderTitle;
            backBtn.Visibility = navStack.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            searchBox.Visibility = currentSearchTpl is not null ? Visibility.Visible : Visibility.Collapsed;

            if (activeSrc is null)
            {
                statusText.Text = "先在左侧添加一个 Calibre-Web (OPDS) 或 WebDAV 书库源。";
                return;
            }

            var reqSrc = activeSrc;
            statusText.Text = $"正在读取 {reqSrc.Name}…";
            statusText.Foreground = ThemeHelper.Brush(p.T3);
            try
            {
                var (items, searchTpl, nextUrl) = await FetchPageAsync(reqSrc, url, myCts.Token);
                if (mySeq != loadSeq || myCts.IsCancellationRequested) return;

                entriesPanel.Children.Clear();
                if (!string.IsNullOrEmpty(searchTpl)) currentSearchTpl = searchTpl;
                searchBox.Visibility = currentSearchTpl is not null ? Visibility.Visible : Visibility.Collapsed;
                statusText.Text = $"共 {items.Count} 项";

                if (reqSrc.Kind == "webdav")
                {
                    var audios = items.Where(i => !i.IsFolder && !string.IsNullOrEmpty(i.FormatExt) && AudiobookImporter.Extensions.Contains("." + i.FormatExt)).ToArray();
                    if (audios.Length > 0)
                    {
                        currentAudioItems = audios;
                        streamAllBtn.Visibility = Visibility.Visible;
                    }
                }

                var shelfBooks = _library.GetShelfBooks();
                foreach (var it in items)
                {
                    var entry = it;
                    bool alreadyOwned = IsAlreadyOwned(entry, shelfBooks, _library.Prefs.RemoteAcquiredBooks);

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
                            navStack.Push(url ?? reqSrc.Url);
                            await LoadUrlAsync(entry.Url, entry.Title);
                        }
                        else
                        {
                            actBtn.IsEnabled = false;
                            actBtn.Content = "下载中…";
                            try
                            {
                                var b = await DownloadAndImportEntryAsync(reqSrc, entry, frac =>
                                {
                                    actBtn.DispatcherQueue.TryEnqueue(() => actBtn.Content = $"{Math.Round(frac * 100)}%");
                                });
                                actBtn.Content = "✓ 已在书架";
                                actBtn.Style = (Style)Application.Current.Resources["YYPillButtonStyle"];
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
                    moreBtn.Click += async (_, _) => { navStack.Push(url ?? reqSrc.Url); await LoadUrlAsync(nu); };
                    entriesPanel.Children.Add(moreBtn);
                }
            }
            catch (OperationCanceledException) when (mySeq != loadSeq || myCts.IsCancellationRequested)
            {
                // Superseded by a newer navigation/search request
            }
            catch (Exception ex)
            {
                if (mySeq != loadSeq) return;
                // Keep discovered `currentSearchTpl` intact on transient network failure (#24)
                searchBox.Visibility = currentSearchTpl is not null ? Visibility.Visible : Visibility.Collapsed;
                statusText.Text = $"读取失败：{ex.Message}";
                statusText.Foreground = ThemeHelper.Brush(p.Danger);
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
                    navStack.Clear();
                    currentSearchTpl = null;
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
                    statusText.Foreground = ThemeHelper.Brush(p.Danger);
                    return;
                }
                string id = editing?.Id ?? Guid.NewGuid().ToString("N")[..10];
                string kind = (kindCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "opds";
                string nm = string.IsNullOrWhiteSpace(nameIn.Text) ? parsedUri.Host : nameIn.Text.Trim();
                var cfg = new RemoteSourceConfig(id, nm, kind, parsedUri.ToString(), userIn.Text.Trim());

                addSrcBtn.IsEnabled = false;
                statusText.Text = $"正在探测 {nm}…";
                statusText.Foreground = ThemeHelper.Brush(p.T3);
                var (ok, msg) = await SaveSourceAsync(cfg, pwdIn.Password);
                statusText.Text = msg;
                statusText.Foreground = ThemeHelper.Brush(ok ? p.Ok : p.Danger);
                addSrcBtn.IsEnabled = true;
                if (!ok) return;

                sources = GetSources();
                editingSourceId = null;
                activeSrc = cfg;
                navStack.Clear();
                currentSearchTpl = null;
                RebuildSourcesSidebar();
                await LoadUrlAsync();
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

        try
        {
            await new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "OPDS 与 WebDAV 远程书库",
                Content = root,
                CloseButtonText = "完成",
            }.ShowAsync();
        }
        finally
        {
            try { loadCts?.Cancel(); loadCts?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Probes <paramref name="cfg"/> first and only persists credentials &amp; source config when probing succeeds (#24).
    /// </summary>
    public async Task<(bool Ok, string Message)> SaveSourceAsync(
        RemoteSourceConfig cfg, string? password = null, CancellationToken ct = default)
    {
        var (ok, msg) = await ProbeSourceAsync(cfg, ct, passwordOverride: password);
        if (!ok) return (false, msg);

        if (password is not null)
            _library.Secrets.Set($"source.{cfg.Id}.password", password);

        var list = GetSources();
        int idx = list.FindIndex(x => x.Id == cfg.Id);
        if (idx >= 0) list[idx] = cfg;
        else list.Add(cfg);
        SaveSources(list);
        return (true, msg);
    }

    internal static string? ParseOpenSearchTemplate(string osXml, Uri feedBaseUri, Uri osDocUri)
    {
        var osDoc = XDocument.Parse(osXml);
        var urlEls = osDoc.Descendants()
            .Where(e => e.Name.LocalName.Equals("Url", StringComparison.OrdinalIgnoreCase) &&
                        (e.Attribute("template")?.Value.Contains("{searchTerms}", StringComparison.Ordinal) == true))
            .ToArray();
        var chosen = urlEls.FirstOrDefault(e =>
            (e.Attribute("type")?.Value ?? "").Contains("atom", StringComparison.OrdinalIgnoreCase))
            ?? urlEls.FirstOrDefault();

        if (chosen?.Attribute("template")?.Value is not string tpl || string.IsNullOrWhiteSpace(tpl))
            return null;

        string cleanTpl = tpl.Replace("&amp;", "&", StringComparison.Ordinal);
        string safePlaceholder = cleanTpl.Replace("{searchTerms}", "YYSEARCHTERMS", StringComparison.Ordinal);
        var resolved = UpgradeIfHttps(feedBaseUri, new Uri(osDocUri, safePlaceholder));
        return resolved.ToString().Replace("YYSEARCHTERMS", "{searchTerms}", StringComparison.Ordinal);
    }

    internal static bool IsAlreadyOwned(
        RemoteEntryItem entry,
        IReadOnlyList<BookSummary> shelfBooks,
        IReadOnlyDictionary<string, string>? acquiredBooks = null)
    {
        if (entry.IsFolder) return false;
        var present = shelfBooks.Where(b => b.Available && !b.Record).ToArray();
        if (acquiredBooks is not null && acquiredBooks.TryGetValue(entry.Url, out string? bookId) && present.Any(b => b.Id == bookId))
            return true;

        string entryTitle = NormalizePlain(entry.Title);
        if (string.IsNullOrEmpty(entryTitle)) return false;

        string[] entryAuthors = SplitAuthors(entry.Author)
            .Select(NormalizePlain)
            .Where(a => a.Length > 0)
            .ToArray();

        foreach (var b in present)
        {
            if (!string.Equals(NormalizePlain(b.Title), entryTitle, StringComparison.Ordinal))
                continue;

            if (entryAuthors.Length == 0 || b.Authors.Length == 0)
                return true;

            foreach (var ba in b.Authors)
            {
                string normBookAuthor = NormalizePlain(ba);
                if (normBookAuthor.Length == 0) continue;
                if (entryAuthors.Any(ea => ea == normBookAuthor || normBookAuthor.Contains(ea, StringComparison.Ordinal) || ea.Contains(normBookAuthor, StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    private static string[] SplitAuthors(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(['、', ',', '，', ';', '；', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizePlain(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || char.IsPunctuation(c) || char.IsSymbol(c)) continue;
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
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
