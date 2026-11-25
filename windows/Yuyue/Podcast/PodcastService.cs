using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.SpeechSynthesis;
using Windows.Storage;
using Windows.Storage.Streams;
using Yuyue.Core;
using Yuyue.Speech;

namespace Yuyue.Podcast;

public sealed record PodcastSearchHitDto(
    string Id,
    string Title,
    string Author,
    string FeedUrl,
    string? ArtworkUrl,
    string? Genre);

/// <summary>
/// 1:1 implementation of macOS <c>PodcastHub.swift</c> network, import, audio slicing, transcription &amp; Studio pipeline:
/// - Rust <see cref="YuyueCoreMethods.ParsePodcastRss"/> + Podcasting 2.0 <c>&lt;podcast:transcript&gt;</c> auto-fetch
/// - Single-episode webpage extraction (Xiaoyuzhou / Apple Podcasts / Substack)
/// - OPML batch import
/// - Apple iTunes public Podcast Directory search &amp; live top-charts discovery
/// - Local micro-clip audio slicing (<c>PodcastClipsDir</c>) and on-device Whisper/SAPI transcription
/// - Private Studio generation with spoiler-lock, voice style (solo/duo/English), and usage ledger tracking.
/// </summary>
public sealed class PodcastService
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(25) };
    private static readonly HttpClient DownloadHttp = new() { Timeout = System.TimeSpan.FromMinutes(10) };

    private readonly LibraryService _library;
    private readonly object _clipThoughtsLock = new();
    private Dictionary<string, string>? _clipThoughtsCache;
    private IReadOnlyList<PodcastSearchHitDto>? _topChartsCache;

    public PodcastService(LibraryService library)
    {
        _library = library;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yuyue/0.1");
        if (!DownloadHttp.DefaultRequestHeaders.UserAgent.Any())
            DownloadHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yuyue/0.1");
    }

    public string CacheDir => Path.Combine(_library.DataDir, "podcast-cache");
    private string ClipThoughtsPath => Path.Combine(_library.DataDir, "podcast-clip-thoughts.json");

    /// <summary>
    /// Returns the non-destructively edited clip thought if present, falling back to <see cref="PodcastClip.Thought"/>.
    /// Avoids destructive <c>PodcastDeleteClip</c> + <c>PodcastAddClip</c> cycles so clip IDs, timestamps, and audio slices stay intact.
    /// </summary>
    public string GetClipThought(PodcastClip clip)
    {
        lock (_clipThoughtsLock)
        {
            EnsureClipThoughtsLoadedLocked();
            if (_clipThoughtsCache!.TryGetValue(clip.Id, out var note))
                return note;
            return clip.Thought ?? "";
        }
    }

    public void SetClipThought(string clipId, string? thought)
    {
        if (string.IsNullOrWhiteSpace(clipId)) return;
        string normalized = string.IsNullOrWhiteSpace(thought) ? "" : thought.Trim();
        lock (_clipThoughtsLock)
        {
            EnsureClipThoughtsLoadedLocked();
            if (_clipThoughtsCache!.TryGetValue(clipId, out var prev) && prev == normalized)
                return;
            _clipThoughtsCache[clipId] = normalized;
            SaveClipThoughtsLocked();
        }
    }

    private void EnsureClipThoughtsLoadedLocked()
    {
        if (_clipThoughtsCache is not null) return;
        _clipThoughtsCache = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (File.Exists(ClipThoughtsPath))
            {
                string raw = File.ReadAllText(ClipThoughtsPath, Encoding.UTF8);
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
                if (parsed is not null)
                {
                    foreach (var kv in parsed)
                        _clipThoughtsCache[kv.Key] = kv.Value ?? "";
                }
            }
        }
        catch { }
    }

    private void SaveClipThoughtsLocked()
    {
        if (_clipThoughtsCache is null) return;
        try
        {
            Directory.CreateDirectory(_library.DataDir);
            string tmp = ClipThoughtsPath + ".tmp";
            string json = JsonSerializer.Serialize(_clipThoughtsCache);
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, ClipThoughtsPath, overwrite: true);
        }
        catch { }
    }

    public IReadOnlyList<PodcastChannel> GetChannels()
    {
        try { return _library.Core.PodcastChannels(); }
        catch { return []; }
    }

    public IReadOnlyList<PodcastIssue> GetIssues(PodcastPlacement? placement = null)
    {
        try { return _library.Core.PodcastIssues(placement); }
        catch { return []; }
    }

    /// <summary>
    /// Searches Apple's public iTunes Podcast Directory (zero self-hosted backend, 1:1 <c>PodcastHub.searchITunes</c>).
    /// </summary>
    public async Task<IReadOnlyList<PodcastSearchHitDto>> SearchItunesAsync(string query)
    {
        string q = query.Trim();
        if (string.IsNullOrEmpty(q)) return [];

        bool hasCjk = q.Any(ch => ch >= 0x4E00 && ch <= 0x9FFF);
        string[] urls = hasCjk
            ? [
                $"https://itunes.apple.com/search?media=podcast&entity=podcast&country=CN&limit=14&term={Uri.EscapeDataString(q)}",
                $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit=14&term={Uri.EscapeDataString(q)}",
            ]
            : [
                $"https://itunes.apple.com/search?media=podcast&entity=podcast&limit=14&term={Uri.EscapeDataString(q)}",
            ];

        var results = new List<PodcastSearchHitDto>();
        var seenFeeds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var apiUrl in urls)
        {
            try
            {
                string json = await Http.GetStringAsync(apiUrl);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("results", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var el in arr.EnumerateArray())
                {
                    string? feedUrl = el.TryGetProperty("feedUrl", out var fu) ? fu.GetString() : null;
                    if (string.IsNullOrWhiteSpace(feedUrl) || !seenFeeds.Add(feedUrl)) continue;

                    string id = el.TryGetProperty("collectionId", out var cid) ? cid.ToString() : feedUrl;
                    string title = (el.TryGetProperty("collectionName", out var cn) ? cn.GetString() : null) ?? "播客节目";
                    string author = (el.TryGetProperty("artistName", out var an) ? an.GetString() : null) ?? "";
                    string? art = (el.TryGetProperty("artworkUrl600", out var a6) ? a6.GetString() : null)
                        ?? (el.TryGetProperty("artworkUrl100", out var a1) ? a1.GetString() : null);
                    string? genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() : null;

                    results.Add(new PodcastSearchHitDto(id, title, author, feedUrl, art, genre));
                }

                if (results.Count >= 6) break;
            }
            catch { }
        }

        return results;
    }

    /// <summary>
    /// Live Apple Podcasts public top charts (CN + fallback curated picks) so discovery &amp; Add-Podcast sheet stay fresh.
    /// </summary>
    public async Task<IReadOnlyList<PodcastSearchHitDto>> FetchItunesTopChartsAsync()
    {
        if (_topChartsCache is { Count: > 0 }) return _topChartsCache;

        var fallback = new PodcastSearchHitDto[]
        {
            new("storyfm", "故事FM", "爱哲", "https://storyfm.cn/feed/episodes/", null, "社会与文化"),
            new("leftright", "忽左忽右", "JustPod", "https://feeds.fireside.fm/left-right/rss", null, "历史与文化"),
            new("lex", "Lex Fridman Podcast", "Lex Fridman", "https://lexfridman.com/feed/podcast/", null, "科技与哲学"),
            new("huberman", "Huberman Lab", "Andrew Huberman", "https://feeds.megaphone.fm/hubermanlab", null, "健康与科学"),
            new("dwarkesh", "Dwarkesh Podcast", "Dwarkesh Patel", "https://apple.dwarkesh-patel.com/feed.xml", null, "深度访谈"),
        };

        try
        {
            string chartJson = await Http.GetStringAsync("https://rss.applemarketingtools.com/api/v2/cn/podcasts/top/10/podcasts.json");
            using var doc = JsonDocument.Parse(chartJson);
            if (doc.RootElement.TryGetProperty("feed", out var feedEl)
                && feedEl.TryGetProperty("results", out var resArr)
                && resArr.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<string>();
                foreach (var item in resArr.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } id)
                        ids.Add(id);
                }

                if (ids.Count > 0)
                {
                    string lookupJson = await Http.GetStringAsync($"https://itunes.apple.com/lookup?id={string.Join(",", ids.Take(10))}&country=CN");
                    using var ldoc = JsonDocument.Parse(lookupJson);
                    var resolved = new List<PodcastSearchHitDto>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (ldoc.RootElement.TryGetProperty("results", out var larr) && larr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in larr.EnumerateArray())
                        {
                            string? feedUrl = el.TryGetProperty("feedUrl", out var fu) ? fu.GetString() : null;
                            if (string.IsNullOrWhiteSpace(feedUrl) || !seen.Add(feedUrl)) continue;
                            string id = el.TryGetProperty("collectionId", out var cid) ? cid.ToString() : feedUrl;
                            string title = (el.TryGetProperty("collectionName", out var cn) ? cn.GetString() : null) ?? "播客节目";
                            string author = (el.TryGetProperty("artistName", out var an) ? an.GetString() : null) ?? "";
                            string? art = (el.TryGetProperty("artworkUrl600", out var a6) ? a6.GetString() : null)
                                ?? (el.TryGetProperty("artworkUrl100", out var a1) ? a1.GetString() : null);
                            string? genre = el.TryGetProperty("primaryGenreName", out var gn) ? gn.GetString() : null;
                            resolved.Add(new PodcastSearchHitDto(id, title, author, feedUrl, art, genre));
                        }
                    }
                    if (resolved.Count >= 3)
                    {
                        _topChartsCache = resolved;
                        return resolved;
                    }
                }
            }
        }
        catch { }

        return fallback;
    }

    /// <summary>
    /// Subscribes to an RSS/Atom feed, or extracts a single episode from a webpage / direct audio URL (1:1 <c>PodcastHub.subscribeRSS</c>).
    /// </summary>
    public async Task<(PodcastChannel? Channel, PodcastIssue? SingleIssue)> AddUrlOrFeedAsync(string rawUrl, bool putLatestOnDesk = true, bool isRefresh = false)
    {
        string url = rawUrl.Trim();
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        string lowerPath = url.Split('?')[0].ToLowerInvariant();
        if (lowerPath.EndsWith(".mp3") || lowerPath.EndsWith(".m4a") || lowerPath.EndsWith(".ogg") || lowerPath.EndsWith(".opus") || lowerPath.EndsWith(".wav"))
        {
            string fileName = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(lowerPath));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "外部音频单集";
            var single = await Task.Run(() => _library.Core.PodcastImportSingleIssue(
                fileName, "单集直听", "从音频直链放入收听台", "", url, null, null, 900, "zh-CN", PodcastSourceKind.SingleImport));
            if (!isRefresh) _library.Analytics.TrackAudioFeatureUsed("podcast_added");
            return (null, single);
        }

        using var resp = await Http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        string body = await resp.Content.ReadAsStringAsync();
        string contentType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";

        bool looksXml = body.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || body.Contains("<rss", StringComparison.OrdinalIgnoreCase)
            || body.Contains("<feed", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml")
            || contentType.Contains("rss");

        if (looksXml)
        {
            try
            {
                var parsed = await Task.Run(() => YuyueCoreMethods.ParsePodcastRss(body));
                var channel = await Task.Run(() => _library.Core.PodcastUpsertChannelFromFeed(url, parsed, putLatestOnDesk));
                _ = PrefetchPodcast20TranscriptsAsync(channel.Id, parsed);
                if (!isRefresh) _library.Analytics.TrackAudioFeatureUsed("podcast_added");
                return (channel, null);
            }
            catch when (!contentType.Contains("xml"))
            {
                // Fall through to webpage parser if HTML disguised with XML doctype
            }
        }

        // Try single-episode webpage extraction (Xiaoyuzhou, Apple Podcasts, Substack, etc.)
        var webEp = await Task.Run(() => YuyueCoreMethods.ParseEpisodeWebpage(body, url));
        if (!string.IsNullOrWhiteSpace(webEp.AudioUrl))
        {
            var single = await Task.Run(() => _library.Core.PodcastImportSingleIssue(
                string.IsNullOrWhiteSpace(webEp.Title) ? "网页单集" : webEp.Title,
                string.IsNullOrWhiteSpace(webEp.ShowTitle) ? "单集直达" : webEp.ShowTitle,
                webEp.Summary ?? "",
                webEp.Summary ?? "",
                webEp.AudioUrl,
                null,
                webEp.ArtworkUrl,
                webEp.DurationSec > 0 ? webEp.DurationSec : 1200u,
                "zh-CN",
                PodcastSourceKind.SingleImport));
            if (!isRefresh) _library.Analytics.TrackAudioFeatureUsed("podcast_added");
            return (null, single);
        }

        if (!string.IsNullOrWhiteSpace(webEp.RssUrl) && !string.Equals(webEp.RssUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return await AddUrlOrFeedAsync(webEp.RssUrl, putLatestOnDesk, isRefresh);
        }

        // Final attempt as RSS so Rust gives a clear parse error
        var rssParsed = await Task.Run(() => YuyueCoreMethods.ParsePodcastRss(body));
        var ch = await Task.Run(() => _library.Core.PodcastUpsertChannelFromFeed(url, rssParsed, putLatestOnDesk));
        _ = PrefetchPodcast20TranscriptsAsync(ch.Id, rssParsed);
        if (!isRefresh) _library.Analytics.TrackAudioFeatureUsed("podcast_added");
        return (ch, null);
    }

    public async Task<PodcastChannel> SubscribeRssAsync(string feedUrl)
    {
        var (ch, iss) = await AddUrlOrFeedAsync(feedUrl, putLatestOnDesk: true);
        if (ch is not null) return ch;
        if (iss is not null)
        {
            return new PodcastChannel(
                Id: iss.ChannelId ?? iss.Id,
                FeedUrl: feedUrl,
                Title: iss.ShowTitle,
                Author: "",
                Summary: iss.Summary,
                Language: iss.Language,
                ArtworkUrl: iss.ArtworkUrl,
                AddedAt: iss.AddedAt,
                RefreshedAt: iss.AddedAt);
        }
        throw new InvalidOperationException("未能识别该链接中的播客订阅或音频单集");
    }

    /// <summary>
    /// Automatically downloads Podcasting 2.0 <c>&lt;podcast:transcript&gt;</c> (SRT / VTT / JSON) for newly imported episodes.
    /// </summary>
    private async Task PrefetchPodcast20TranscriptsAsync(string channelId, ParsedPodcastFeed feed)
    {
        try
        {
            var storedIssues = GetIssues().Where(i => i.ChannelId == channelId).ToArray();
            foreach (var ep in feed.Episodes.Where(e => e.Transcripts.Length > 0).Take(4))
            {
                var match = storedIssues.FirstOrDefault(i =>
                    (!string.IsNullOrEmpty(ep.AudioUrl) && i.AudioUrl == ep.AudioUrl) ||
                    string.Equals(i.Title, ep.Title, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                if (_library.Core.PodcastTranscript(match.Id).Length > 0) continue;

                var link = ep.Transcripts
                    .OrderBy(t => t.MimeType.Contains("json", StringComparison.OrdinalIgnoreCase) ? 0 :
                                  t.MimeType.Contains("srt", StringComparison.OrdinalIgnoreCase) ? 1 :
                                  t.MimeType.Contains("vtt", StringComparison.OrdinalIgnoreCase) ? 2 : 3)
                    .First();

                try
                {
                    string rawTrans = await Http.GetStringAsync(link.Url);
                    var sentences = await Task.Run(() => YuyueCoreMethods.ParsePodcastTranscript(rawTrans, link.MimeType));
                    if (sentences.Length > 0)
                    {
                        _library.Core.PodcastSetTranscript(match.Id, sentences, PodcastFidelity.L2Magazine);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    public async Task<int> ImportOpmlAsync(string filePath)
    {
        string xml = await File.ReadAllTextAsync(filePath);
        var feeds = await Task.Run(() => YuyueCoreMethods.ParsePodcastOpml(xml));
        if (feeds.Length == 0) return 0;

        int importedCount = 0;
        using var gate = new SemaphoreSlim(4, 4);
        var tasks = feeds.Take(30).Select(async f =>
        {
            if (string.IsNullOrWhiteSpace(f.XmlUrl)) return;
            await gate.WaitAsync();
            try
            {
                var (ch, _) = await AddUrlOrFeedAsync(f.XmlUrl, putLatestOnDesk: false, isRefresh: true);
                if (ch is not null) Interlocked.Increment(ref importedCount);
            }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        if (importedCount > 0) _library.Analytics.TrackAudioFeatureUsed("podcast_added");
        return importedCount;
    }

    public async Task<PodcastIssue> ImportLocalAudioAsync(string filePath)
    {
        uint durSec = 600;
        string title = Path.GetFileNameWithoutExtension(filePath);
        string author = "本地音频";
        try
        {
            var sf = await StorageFile.GetFileFromPathAsync(filePath);
            var music = await sf.Properties.GetMusicPropertiesAsync();
            if (music.Duration.TotalSeconds > 1) durSec = (uint)Math.Round(music.Duration.TotalSeconds);
            if (!string.IsNullOrWhiteSpace(music.Title)) title = music.Title.Trim();
            if (!string.IsNullOrWhiteSpace(music.Artist)) author = music.Artist.Trim();
            if (durSec == 600)
            {
                using var ms = MediaSource.CreateFromStorageFile(sf);
                await ms.OpenAsync();
                if (ms.Duration is { } d && d.TotalSeconds > 1) durSec = (uint)Math.Round(d.TotalSeconds);
            }
        }
        catch { }

        string storedPath = filePath;
        try
        {
            string localDir = Path.Combine(_library.DataDir, "podcasts", "local");
            Directory.CreateDirectory(localDir);
            string fullSrc = Path.GetFullPath(filePath);
            string fullLocalDir = Path.GetFullPath(localDir);
            if (!fullSrc.StartsWith(fullLocalDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(ext)) ext = ".mp3";
                string rawBase = Path.GetFileNameWithoutExtension(filePath);
                var invalid = Path.GetInvalidFileNameChars();
                string safeBase = new string(rawBase.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
                if (string.IsNullOrWhiteSpace(safeBase)) safeBase = "audio";
                if (safeBase.Length > 48) safeBase = safeBase[..48];
                string dst = Path.Combine(localDir, $"{safeBase}-{Guid.NewGuid().ToString("N")[..8]}{ext}");
                await Task.Run(() => File.Copy(fullSrc, dst, overwrite: true));
                if (File.Exists(dst)) storedPath = dst;
            }
        }
        catch { }

        var issue = await Task.Run(() => _library.Core.PodcastImportSingleIssue(
            title, author, "从本机音频文件直接放入收听台", "", null, storedPath, null, durSec, "zh-CN", PodcastSourceKind.LocalAudio));
        _library.Analytics.TrackAudioFeatureUsed("podcast_added");
        return issue;
    }

    /// <summary>
    /// Ensures a local copy of an issue's audio file exists (either its <see cref="PodcastIssue.LocalAudioPath"/>
    /// or cached in <see cref="CacheDir"/>) for clip slicing and on-device transcription.
    /// </summary>
    public async Task<string?> EnsureLocalAudioPathAsync(PodcastIssue issue, bool downloadIfRemote, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(issue.LocalAudioPath) && File.Exists(issue.LocalAudioPath))
            return issue.LocalAudioPath;

        Directory.CreateDirectory(CacheDir);
        string ext = ".mp3";
        if (!string.IsNullOrWhiteSpace(issue.AudioUrl))
        {
            string urlPath = issue.AudioUrl.Split('?')[0];
            string cand = Path.GetExtension(urlPath).ToLowerInvariant();
            if (cand is ".mp3" or ".m4a" or ".wav" or ".ogg" or ".opus" or ".flac") ext = cand;
        }
        string cachedPath = Path.Combine(CacheDir, $"{issue.Id}{ext}");
        if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 1024)
            return cachedPath;

        if (!downloadIfRemote || string.IsNullOrWhiteSpace(issue.AudioUrl)) return null;

        string tmp = cachedPath + ".part";
        using var resp = await DownloadHttp.GetAsync(issue.AudioUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await src.CopyToAsync(dst, ct);
        }
        if (File.Exists(cachedPath)) File.Delete(cachedPath);
        File.Move(tmp, cachedPath);
        return cachedPath;
    }

    /// <summary>
    /// Slices <c>[t0, t1]</c> from a local/cached podcast audio file into a standalone WAV inside <c>Core.PodcastClipsDir()</c>
    /// so saved <see cref="PodcastClip"/> items can replay original audio offline even if the feed disappears.
    /// For Studio TTS episodes without an audio stream, synthesizes the turn's audio directly into the WAV clip.
    /// </summary>
    public async Task<string?> SliceLocalAudioClipAsync(
        PodcastIssue issue,
        double t0,
        double t1,
        CancellationToken ct = default,
        string? fallbackText = null,
        string? voiceId = null)
    {
        try
        {
            double start = Math.Max(0, t0);
            double end = Math.Max(start + 1.0, t1);
            string clipsDir = _library.Core.PodcastClipsDir();
            Directory.CreateDirectory(clipsDir);
            string relName = $"clip-{issue.Id[..Math.Min(8, issue.Id.Length)]}-{(int)start}-{Guid.NewGuid().ToString("N")[..6]}.wav";
            string dstPath = Path.Combine(clipsDir, relName);

            string? localPath = await EnsureLocalAudioPathAsync(issue, downloadIfRemote: true, ct);
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                string scratchDir = Path.Combine(CacheDir, "pcm-work", issue.Id);
                Directory.CreateDirectory(scratchDir);

                string wavPath = await Aligner.EnsurePcm16kWavAsync(localPath, scratchDir, 0, ct);
                if (Aligner.TryReadWavInfo(wavPath, out var info) && info.SampleCount > 0)
                {
                    await Task.Run(() => WindowsTranscriber.WriteWavSlice(wavPath, info, start, end, dstPath), ct);
                    if (File.Exists(dstPath) && new FileInfo(dstPath).Length > 64)
                        return relName;
                }
            }

            string textToSpeak = fallbackText?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(textToSpeak))
            {
                try
                {
                    var sents = _library.Core.PodcastTranscript(issue.Id);
                    var matched = sents.Where(s => s.T1 > start + 0.05 && s.T0 < end - 0.05).ToArray();
                    if (matched.Length > 0)
                    {
                        textToSpeak = string.Join(" ", matched.Select(s => s.TextOrig)).Trim();
                    }
                    else if (sents.Length > 0)
                    {
                        var nearest = sents.OrderBy(s => Math.Abs(s.T0 - start)).First();
                        textToSpeak = nearest.TextOrig.Trim();
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(textToSpeak))
            {
                byte[]? synthWav = await SynthesizeClipWavBytesAsync(textToSpeak, voiceId, issue.Language, ct);
                if (synthWav is { Length: > 64 })
                {
                    await File.WriteAllBytesAsync(dstPath, synthWav, ct);
                    return File.Exists(dstPath) && new FileInfo(dstPath).Length > 64 ? relName : null;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<byte[]?> SynthesizeClipWavBytesAsync(string text, string? voiceId, string? issueLang, CancellationToken ct)
    {
        string plain = CardVoiceEngine.PlainSpokenText(text);
        if (string.IsNullOrWhiteSpace(plain)) return null;

        bool isEn = (issueLang ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        string lang = isEn ? "en-US" : "zh-Hans";
        string langPrefix = isEn ? "en" : "zh";
        string? norm = DramaSynthService.NormalizeVoiceId(voiceId);

        if (string.IsNullOrWhiteSpace(norm))
        {
            if (_library.LiveVoice.IsReady)
            {
                var card = _library.LiveVoice.GetBuiltinCards().FirstOrDefault(c => c.Lang.StartsWith(langPrefix, StringComparison.OrdinalIgnoreCase));
                if (card is not null) norm = card.Id;
            }
            if (string.IsNullOrWhiteSpace(norm) && _library.Kokoro.IsInstalled)
            {
                var spk = _library.Kokoro.GetSpeakers(langPrefix).FirstOrDefault();
                if (spk is not null) norm = spk.Id;
            }
            if (string.IsNullOrWhiteSpace(norm) && _library.CloudVoices.IsConfigured(CloudTtsProvider.Edge))
            {
                norm = isEn ? "cloud.edge.en-US-AndrewMultilingualNeural" : "cloud.edge.zh-CN-YunxiNeural";
            }
        }

        if (!string.IsNullOrWhiteSpace(norm))
        {
            if (norm.StartsWith("kokoro.", StringComparison.Ordinal) && _library.Kokoro.IsInstalled)
            {
                var spk = _library.Kokoro.FindSpeaker(norm);
                if (spk is not null)
                {
                    try
                    {
                        var wav = await KokoroEngine.Instance.SynthesizeWavAsync(
                            _library.Core, _library.Kokoro, _library.DataDir, spk, text, lang, ct);
                        if (wav is { Length: > 64 }) return wav;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }
                }
                return null;
            }
            else if (CloudVoiceService.TryParseFullId(norm, out _, out _))
            {
                try
                {
                    var wav = await _library.CloudVoices.SynthesizeWavAsync(norm, text, lang, null, ct);
                    if (wav is { Length: > 64 }) return wav;
                }
                catch (OperationCanceledException) { throw; }
                catch { return null; }
                return null;
            }
            else if (norm.StartsWith("card.", StringComparison.Ordinal) || norm.StartsWith("cloned.", StringComparison.Ordinal))
            {
                if (!_library.LiveVoice.IsReady) return null;
                var card = _library.LiveVoice.GetClonedCards().FirstOrDefault(c => c.Id == norm)
                    ?? _library.LiveVoice.GetBuiltinCards().FirstOrDefault(c => c.Id == norm);
                if (card is not null)
                {
                    try
                    {
                        var wav = await CardVoiceEngine.Instance.SynthesizeWavAsync(
                            _library.Core, _library.LiveVoice, _library.DataDir, card, null, text, lang, CardStyle.Neutral, ct);
                        if (wav is { Length: > 64 }) return wav;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { return null; }
                }
                return null;
            }
        }

        if (!string.IsNullOrWhiteSpace(norm) && !norm.StartsWith("sys:", StringComparison.Ordinal))
            return null;

        try
        {
            using var synth = new SpeechSynthesizer();
            string? sysId = norm is not null && norm.StartsWith("sys:", StringComparison.Ordinal) ? norm[4..] : null;
            var voices = AudioPlayer.GetSystemVoicesForLanguage(lang);
            var matchedVoice = sysId is not null
                ? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == sysId)
                : voices.FirstOrDefault();
            if (matchedVoice is not null) synth.Voice = matchedVoice;
            using var stream = await synth.SynthesizeTextToStreamAsync(plain).AsTask(ct);
            var bytes = new byte[stream.Size];
            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None).AsTask(ct);
            return bytes.Length > 64 ? bytes : null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>
    /// On-device transcription for an external/local podcast issue using <see cref="WindowsTranscriber.TranscribeFileAsync"/>
    /// (local Whisper / configured Whisper API / offline SAPI 5) and stores result via <c>Core.PodcastSetTranscript</c>.
    /// </summary>
    public async Task<int> TranscribeIssueOnDeviceAsync(string issueId, Action<string>? onStatus = null, CancellationToken ct = default)
    {
        var issue = _library.Core.PodcastIssue(issueId);
        onStatus?.Invoke("正在准备单集音频…");
        string? localPath = await EnsureLocalAudioPathAsync(issue, downloadIfRemote: true, ct);
        if (string.IsNullOrWhiteSpace(localPath))
            throw new InvalidOperationException("该单集没有可用的音频文件或直链，无法转写。");

        string scratchDir = Path.Combine(CacheDir, "transcribe", issueId);
        Directory.CreateDirectory(scratchDir);
        string lang = string.IsNullOrWhiteSpace(issue.Language) ? "zh-CN" : issue.Language;

        try
        {
            if (!SenseVoiceListener.IsInstalled(_library.DataDir) && !await WindowsTranscriber.HasWhisperEndpointAsync(_library, ct))
            {
                try
                {
                    onStatus?.Invoke("正在准备 SenseVoice 离线语音转写模型 (~228 MB)…");
                    await SenseVoiceListener.EnsureAsync(
                        _library.DataDir,
                        frac => onStatus?.Invoke($"正在下载 SenseVoice 离线转写模型 {(int)Math.Round(frac * 100)}%…"),
                        ct);
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    // Offline or disk full: fall through to built-in Windows SAPI
                }
            }

            onStatus?.Invoke("正在本机识别语音并生成时间轴文稿…");
            var words = await WindowsTranscriber.TranscribeFileAsync(
                _library,
                localPath,
                scratchDir,
                fileIndex: 0,
                globalOffsetSec: 0,
                durationSec: Math.Max(1.0, issue.DurationSec),
                lang: lang,
                onLocalSecondsDone: sec => onStatus?.Invoke($"已转写 {System.TimeSpan.FromSeconds(sec):mm\\:ss}…"),
                ct: ct);

            if (words.Length == 0)
                throw new InvalidOperationException("转写未识别出有效文字，请确认 SenseVoice 离线模型、本机 Whisper 服务或系统语音识别语言包可用。");

            var sentences = GroupWordsIntoMagazineSentences(words, issue.ShowTitle, lang);
            await Task.Run(() => _library.Core.PodcastSetTranscript(issueId, sentences, PodcastFidelity.L2Magazine), ct);
            if (sentences.Length > 0)
                _library.Analytics.TrackAudioFeatureUsed("podcast_transcribe");
            return sentences.Length;
        }
        finally
        {
            SenseVoiceListener.Unload();
            try { if (Directory.Exists(scratchDir)) Directory.Delete(scratchDir, recursive: true); } catch { }
        }
    }

    internal static MagazineSentence[] GroupWordsIntoMagazineSentences(HeardWord[] words, string defaultSpeaker, string lang)
    {
        bool isCjk = !lang.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var list = new List<MagazineSentence>();
        var sb = new StringBuilder();
        double segStart = words[0].T0;
        double segEnd = words[0].T1;

        void Flush()
        {
            string text = sb.ToString().Trim();
            if (!string.IsNullOrEmpty(text))
            {
                list.Add(new MagazineSentence(
                    Id: $"tr-{list.Count}",
                    Speaker: defaultSpeaker,
                    TextOrig: text,
                    TextTrans: null,
                    CoachNote: null,
                    T0: segStart,
                    T1: Math.Max(segStart + 0.5, segEnd),
                    SkipReason: null,
                    BookId: null,
                    BookTitle: null,
                    BookSection: null,
                    BookSentence: null,
                    BookQuote: null));
            }
            sb.Clear();
        }

        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (sb.Length == 0) segStart = w.T0;
            else if (!isCjk) sb.Append(' ');
            sb.Append(w.Text);
            segEnd = w.T1;

            bool endsPunct = w.Text.EndsWith('。') || w.Text.EndsWith('？') || w.Text.EndsWith('！')
                || w.Text.EndsWith('.') || w.Text.EndsWith('?') || w.Text.EndsWith('!');
            bool longPause = i + 1 < words.Length && (words[i + 1].T0 - w.T1) > 1.4;
            bool tooLong = (segEnd - segStart) >= 14.0 || sb.Length >= (isCjk ? 72 : 160);

            if (endsPunct || longPause || tooLong)
                Flush();
        }
        Flush();
        return list.ToArray();
    }

    public (int AudioFiles, long AudioBytes, int ClipFiles, long ClipBytes) GetStorageStats()
    {
        int aFiles = 0;
        long aBytes = 0;
        if (Directory.Exists(CacheDir))
        {
            foreach (var f in Directory.EnumerateFiles(CacheDir, "*.*", SearchOption.AllDirectories))
            {
                try { aFiles++; aBytes += new FileInfo(f).Length; } catch { }
            }
        }
        int cFiles = 0;
        long cBytes = 0;
        try
        {
            string clipsDir = _library.Core.PodcastClipsDir();
            if (Directory.Exists(clipsDir))
            {
                foreach (var f in Directory.EnumerateFiles(clipsDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    try { cFiles++; cBytes += new FileInfo(f).Length; } catch { }
                }
            }
        }
        catch { }
        return (aFiles, aBytes, cFiles, cBytes);
    }

    public long ClearAudioCache()
    {
        long freed = 0;
        if (!Directory.Exists(CacheDir)) return 0;
        foreach (var f in Directory.EnumerateFiles(CacheDir, "*.*", SearchOption.AllDirectories))
        {
            try
            {
                freed += new FileInfo(f).Length;
                File.Delete(f);
            }
            catch { }
        }
        return freed;
    }

    public async Task<PodcastIssue> GenerateStudioIssueAsync(
        StudioKind kind,
        string[] bookIds,
        uint minutes = 10,
        string? topic = null,
        bool noSpoiler = true,
        string? voiceStyle = null)
    {
        string style = voiceStyle ?? _library.Prefs.PodcastStudioVoiceStyle;
        var brief = await Task.Run(() => _library.Core.BuildStudioBrief(kind, bookIds, minutes, topic));
        string rawReply = "";
        if (_library.Companion.IsConfigured)
        {
            var sysSb = new StringBuilder(brief.SystemPrompt);
            if (!noSpoiler)
            {
                sysSb.Append("\n（补充说明：用户已主动开启「全书范围」讨论权限，可结合全书脉络深入剖析。）");
            }
            if (style == "studio-solo-zh")
            {
                sysSb.Append("\n（主播形式：请以单一主理人「林深」独白导读的形式撰写逐段讲稿，不要切分第二位主播。）");
            }
            else if (style == "studio-duo-en" && kind != StudioKind.VocabRadio)
            {
                sysSb.Append("\n(Voice format: Write a natural English two-host podcast conversation between Clara and David.)");
            }
            rawReply = await _library.Companion.ChatAsync(
                brief.UserPrompt,
                sysSb.ToString(),
                source: UsageSource.Podcast);
        }
        var draft = await Task.Run(() => _library.Core.FinalizeStudioScript(brief, rawReply));
        string lang = (kind == StudioKind.VocabRadio || style == "studio-duo-en") ? "en" : "zh-Hans";
        var created = await Task.Run(() => _library.Core.PodcastSaveStudioIssue(kind, draft, bookIds, lang));
        _library.Analytics.TrackAudioFeatureUsed("podcast_added");
        return created;
    }
}
