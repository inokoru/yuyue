using System.Net.Http;
using System.Text.Json;
using Windows.Media.Core;
using Windows.Storage;
using Yuyue.Core;

namespace Yuyue.Podcast;

public sealed record PodcastSearchHitDto(
    string Id,
    string Title,
    string Author,
    string FeedUrl,
    string? ArtworkUrl,
    string? Genre);

/// <summary>
/// 1:1 implementation of macOS <c>PodcastHub.swift</c> network, import &amp; Studio pipeline:
/// - Rust <see cref="YuyueCoreMethods.ParsePodcastRss"/> + Podcasting 2.0 <c>&lt;podcast:transcript&gt;</c> auto-fetch (<see cref="YuyueCoreMethods.ParsePodcastTranscript"/>)
/// - Single-episode webpage extraction (<see cref="YuyueCoreMethods.ParseEpisodeWebpage"/> for Xiaoyuzhou / Apple Podcasts / Substack)
/// - OPML batch import (<see cref="YuyueCoreMethods.ParsePodcastOpml"/>)
/// - Apple iTunes public Podcast Directory search
/// - Private Studio generation with spoiler-lock control.
/// </summary>
public sealed class PodcastService
{
    private static readonly HttpClient Http = new() { Timeout = System.TimeSpan.FromSeconds(25) };

    private readonly LibraryService _library;

    public PodcastService(LibraryService library)
    {
        _library = library;
        if (!Http.DefaultRequestHeaders.UserAgent.Any())
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Yuyue/0.1");
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
    /// Subscribes to an RSS/Atom feed, or extracts a single episode from a webpage / direct audio URL (1:1 <c>PodcastHub.subscribeRSS</c>).
    /// </summary>
    public async Task<(PodcastChannel? Channel, PodcastIssue? SingleIssue)> AddUrlOrFeedAsync(string rawUrl, bool putLatestOnDesk = true)
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
            return (null, single);
        }

        if (!string.IsNullOrWhiteSpace(webEp.RssUrl) && !string.Equals(webEp.RssUrl, url, StringComparison.OrdinalIgnoreCase))
        {
            return await AddUrlOrFeedAsync(webEp.RssUrl, putLatestOnDesk);
        }

        // Final attempt as RSS so Rust gives a clear parse error
        var rssParsed = await Task.Run(() => YuyueCoreMethods.ParsePodcastRss(body));
        var ch = await Task.Run(() => _library.Core.PodcastUpsertChannelFromFeed(url, rssParsed, putLatestOnDesk));
        _ = PrefetchPodcast20TranscriptsAsync(ch.Id, rssParsed);
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

                // Prefer JSON/SRT/VTT transcript links
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
                var (ch, _) = await AddUrlOrFeedAsync(f.XmlUrl, putLatestOnDesk: false);
                if (ch is not null) Interlocked.Increment(ref importedCount);
            }
            catch { }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
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

        return await Task.Run(() => _library.Core.PodcastImportSingleIssue(
            title, author, "从本机音频文件直接放入收听台", "", null, filePath, null, durSec, "zh-CN", PodcastSourceKind.LocalAudio));
    }

    public async Task<PodcastIssue> GenerateStudioIssueAsync(
        StudioKind kind,
        string[] bookIds,
        uint minutes = 10,
        string? topic = null,
        bool noSpoiler = true)
    {
        var brief = await Task.Run(() => _library.Core.BuildStudioBrief(kind, bookIds, minutes, topic));
        string rawReply = "";
        if (_library.Companion.IsConfigured)
        {
            string sys = brief.SystemPrompt;
            if (!noSpoiler)
            {
                sys += "\n（补充说明：用户已主动开启「全书范围」讨论权限，可结合全书脉络深入剖析。）";
            }
            rawReply = await _library.Companion.ChatAsync(brief.UserPrompt, sys);
        }
        var draft = await Task.Run(() => _library.Core.FinalizeStudioScript(brief, rawReply));
        string lang = kind == StudioKind.VocabRadio ? "en" : "zh-Hans";
        return await Task.Run(() => _library.Core.PodcastSaveStudioIssue(kind, draft, bookIds, lang));
    }
}
