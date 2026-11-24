using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using Yuyue.Core;

namespace Yuyue.Tests;

public sealed class AirTrafficTests
{
    private static readonly DateTimeOffset FixedUtc = new(2026, 11, 25, 2, 40, 0, System.TimeSpan.Zero);

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public ConcurrentQueue<(Uri? Uri, string? ContentType, string Body)> Requests { get; } = new();
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnSendAsync { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                : "";
            Requests.Enqueue((request.RequestUri, request.Content?.Headers.ContentType?.MediaType, body));

            if (OnSendAsync is not null)
                return await OnSendAsync(request, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":1}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static (AirTrafficClient Client, List<JsonDocument> Captured, Func<Task> Flush) CreateCapturingClient(
        DevicePrefs prefs,
        Action? savePrefs = null,
        string appVersion = "0.1.3",
        ulong appBuild = 5,
        string osVersion = "Microsoft Windows NT 10.0.22631.0")
    {
        var captured = new List<JsonDocument>();
        var gate = new object();
        var client = new AirTrafficClient(
            prefs,
            savePrefs ?? (() => { }),
            apiKey: "test_airtraffic_key",
            trafficHost: "https://apps.air.build/traffic",
            customSender: (payload, _) =>
            {
                lock (gate) captured.Add(JsonDocument.Parse(payload));
                return Task.CompletedTask;
            },
            utcNow: () => FixedUtc,
            appVersion: appVersion,
            appBuild: appBuild,
            osVersion: osVersion);

        return (client, captured, () => client.FlushAsync(System.TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CapturePayload_MatchesPostHogEnvelopeAndStandardProperties()
    {
        using var handler = new RecordingHttpHandler();
        var prefs = new DevicePrefs();
        using var client = new AirTrafficClient(
            prefs,
            () => { },
            apiKey: "phc_test_key",
            trafficHost: "https://apps.air.build/traffic",
            httpHandler: handler,
            utcNow: () => FixedUtc,
            appVersion: "0.1.3",
            appBuild: 5,
            osVersion: "Microsoft Windows NT 10.0.22631.0");

        client.TrackBookImported("epub", "file_picker", "secret-book-title.epub");
        await client.FlushAsync(System.TimeSpan.FromSeconds(2));

        Assert.True(handler.Requests.TryDequeue(out var req));
        Assert.Equal("https://apps.air.build/traffic/capture/", req.Uri?.AbsoluteUri);
        Assert.Equal("application/json", req.ContentType);

        using var doc = JsonDocument.Parse(req.Body);
        var root = doc.RootElement;
        Assert.Equal("phc_test_key", root.GetProperty("api_key").GetString());
        Assert.Equal("book_imported", root.GetProperty("event").GetString());
        Assert.Equal(prefs.AnalyticsDistinctId, root.GetProperty("distinct_id").GetString());
        Assert.True(Guid.TryParseExact(prefs.AnalyticsDistinctId, "D", out _));
        Assert.Equal("2026-11-25T02:40:00.000Z", root.GetProperty("timestamp").GetString());

        var props = root.GetProperty("properties");
        Assert.False(props.GetProperty("$process_person_profile").GetBoolean());
        Assert.Equal("yuyue-windows", props.GetProperty("$lib").GetString());
        Assert.Equal("0.1.3", props.GetProperty("$lib_version").GetString());
        Assert.Equal("Windows", props.GetProperty("$os").GetString());
        Assert.Equal("Microsoft Windows NT 10.0.22631.0", props.GetProperty("$os_version").GetString());
        Assert.Equal("0.1.3", props.GetProperty("$app_version").GetString());
        Assert.Equal("5", props.GetProperty("$app_build").GetString());
        Assert.Equal("windows", props.GetProperty("platform").GetString());
        Assert.Equal("epub", props.GetProperty("format").GetString());
        Assert.Equal("file_picker", props.GetProperty("source").GetString());

        // Free-text filename must never leak into the serialized payload.
        Assert.DoesNotContain("secret-book-title", req.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DistinctId_GeneratesOnceAndPersistsAcrossInstances()
    {
        var prefs = new DevicePrefs();
        int saves = 0;

        using (var first = new AirTrafficClient(prefs, () => saves++, apiKey: "k", customSender: (_, _) => Task.CompletedTask))
        {
            Assert.True(Guid.TryParseExact(first.DistinctId, "D", out _));
            Assert.Equal(first.DistinctId, prefs.AnalyticsDistinctId);
            Assert.Equal(1, saves);
        }

        string persistedJson = JsonSerializer.Serialize(prefs);
        var reloaded = JsonSerializer.Deserialize<DevicePrefs>(persistedJson)!;
        int secondSaves = 0;

        using (var second = new AirTrafficClient(reloaded, () => secondSaves++, apiKey: "k", customSender: (_, _) => Task.CompletedTask))
        {
            Assert.Equal(prefs.AnalyticsDistinctId, second.DistinctId);
            Assert.Equal(0, secondSaves);
        }
    }

    [Fact]
    public void DevicePrefs_MissingAnalyticsEnabledDefaultsToTrue()
    {
        var legacy = JsonSerializer.Deserialize<DevicePrefs>("{}")!;
        Assert.True(legacy.AnalyticsEnabled);
        Assert.Equal("", legacy.AnalyticsDistinctId);
        Assert.Equal("", legacy.AnalyticsLastVersion);

        var disabled = JsonSerializer.Deserialize<DevicePrefs>("{\"AnalyticsEnabled\":false}")!;
        Assert.False(disabled.AnalyticsEnabled);
    }

    [Fact]
    public async Task Lifecycle_InstalledThenSameVersionThenUpdatedSequence()
    {
        var prefs = new DevicePrefs();

        // 1. First install (0.1.3)
        {
            var (c1, cap1, flush1) = CreateCapturingClient(prefs, appVersion: "0.1.3", appBuild: 5);
            using (c1)
            {
                c1.TrackAppLaunch();
                await flush1();
            }

            Assert.Equal(2, cap1.Count);
            Assert.Equal("Application Installed", cap1[0].RootElement.GetProperty("event").GetString());
            Assert.Equal("0.1.3", cap1[0].RootElement.GetProperty("properties").GetProperty("version").GetString());
            Assert.Equal("5", cap1[0].RootElement.GetProperty("properties").GetProperty("build").GetString());
            Assert.Equal("windows", cap1[0].RootElement.GetProperty("properties").GetProperty("platform").GetString());

            Assert.Equal("Application Opened", cap1[1].RootElement.GetProperty("event").GetString());
            Assert.False(cap1[1].RootElement.GetProperty("properties").GetProperty("from_background").GetBoolean());
            Assert.Equal("0.1.3", prefs.AnalyticsLastVersion);
            foreach (var d in cap1) d.Dispose();
        }

        // 2. Subsequent launch on same version (0.1.3) -> only Application Opened
        {
            var (c2, cap2, flush2) = CreateCapturingClient(prefs, appVersion: "0.1.3", appBuild: 5);
            using (c2)
            {
                c2.TrackAppLaunch();
                c2.TrackAppBackgrounded();
                c2.TrackAppReopened();
                await flush2();
            }

            Assert.Equal(3, cap2.Count);
            Assert.Equal("Application Opened", cap2[0].RootElement.GetProperty("event").GetString());
            Assert.False(cap2[0].RootElement.GetProperty("properties").GetProperty("from_background").GetBoolean());
            Assert.Equal("Application Backgrounded", cap2[1].RootElement.GetProperty("event").GetString());
            Assert.Equal("Application Opened", cap2[2].RootElement.GetProperty("event").GetString());
            Assert.True(cap2[2].RootElement.GetProperty("properties").GetProperty("from_background").GetBoolean());
            foreach (var d in cap2) d.Dispose();
        }

        // 3. Upgrade to 0.2.0 -> Application Updated + Application Opened
        {
            var (c3, cap3, flush3) = CreateCapturingClient(prefs, appVersion: "0.2.0", appBuild: 9);
            using (c3)
            {
                c3.TrackAppLaunch();
                await flush3();
            }

            Assert.Equal(2, cap3.Count);
            Assert.Equal("Application Updated", cap3[0].RootElement.GetProperty("event").GetString());
            var updProps = cap3[0].RootElement.GetProperty("properties");
            Assert.Equal("0.1.3", updProps.GetProperty("previous_version").GetString());
            Assert.Equal("0.2.0", updProps.GetProperty("version").GetString());
            Assert.Equal("9", updProps.GetProperty("build").GetString());
            Assert.Equal("windows", updProps.GetProperty("platform").GetString());

            Assert.Equal("Application Opened", cap3[1].RootElement.GetProperty("event").GetString());
            Assert.False(cap3[1].RootElement.GetProperty("properties").GetProperty("from_background").GetBoolean());
            Assert.Equal("0.2.0", prefs.AnalyticsLastVersion);
            foreach (var d in cap3) d.Dispose();
        }
    }

    [Fact]
    public async Task TrackScreen_DeduplicatesConsecutiveIdenticalAndReemitsOnChange()
    {
        var prefs = new DevicePrefs();
        var (client, captured, flush) = CreateCapturingClient(prefs);
        using (client)
        {
            client.TrackScreen("Shelf");
            client.TrackScreen("Shelf");
            client.TrackScreen("Reader");
            client.TrackScreen("Reader");
            client.TrackScreen("Shelf");
            client.TrackScreen("About");
            await flush();
        }

        string?[] names = captured
            .Select(d => d.RootElement.GetProperty("properties").GetProperty("$screen_name").GetString())
            .ToArray();
        Assert.Equal(new[] { "Shelf", "Reader", "Shelf", "About" }, names);
        Assert.All(captured, d =>
        {
            Assert.Equal("$screen", d.RootElement.GetProperty("event").GetString());
            Assert.Equal("windows", d.RootElement.GetProperty("properties").GetProperty("platform").GetString());
        });
        foreach (var d in captured) d.Dispose();
    }

    [Fact]
    public void Normalizers_MapAllowedValuesAndFallbacks()
    {
        // Import formats (including .azw3 precedence over BookFormat.Mobi)
        Assert.Equal("epub", AirTrafficClient.NormalizeImportFormat("Epub"));
        Assert.Equal("txt", AirTrafficClient.NormalizeImportFormat("Txt"));
        Assert.Equal("pdf", AirTrafficClient.NormalizeImportFormat("Pdf"));
        Assert.Equal("mobi", AirTrafficClient.NormalizeImportFormat("Mobi", "novel.mobi"));
        Assert.Equal("azw3", AirTrafficClient.NormalizeImportFormat("Mobi", "C:\\Books\\novel.azw3"));
        Assert.Equal("audio", AirTrafficClient.NormalizeImportFormat("Audio", "track01.m4b"));
        Assert.Equal("other", AirTrafficClient.NormalizeImportFormat("Podcast"));
        Assert.Equal("other", AirTrafficClient.NormalizeImportFormat("unknown_fmt", "archive.rar"));

        // Import sources
        Assert.Equal("file_picker", AirTrafficClient.NormalizeImportSource("file_picker"));
        Assert.Equal("wifi", AirTrafficClient.NormalizeImportSource("wifi"));
        Assert.Equal("opds", AirTrafficClient.NormalizeImportSource("OPDS"));
        Assert.Equal("webdav", AirTrafficClient.NormalizeImportSource("WebDAV"));
        Assert.Equal("share_in", AirTrafficClient.NormalizeImportSource("share_in"));
        Assert.Equal("other", AirTrafficClient.NormalizeImportSource("watched_folder"));

        // Reading formats (.azw3 overrides Mobi; audio maps to other)
        Assert.Equal("epub", AirTrafficClient.NormalizeReadingFormat("Epub"));
        Assert.Equal("txt", AirTrafficClient.NormalizeReadingFormat("Txt"));
        Assert.Equal("pdf", AirTrafficClient.NormalizeReadingFormat("Pdf"));
        Assert.Equal("mobi", AirTrafficClient.NormalizeReadingFormat("Mobi", "book.mobi"));
        Assert.Equal("azw3", AirTrafficClient.NormalizeReadingFormat("Mobi", "book.AZW3"));
        Assert.Equal("other", AirTrafficClient.NormalizeReadingFormat("Audio"));
        Assert.Equal("other", AirTrafficClient.NormalizeReadingFormat(null));

        // Paper themes
        Assert.Equal("plain", AirTrafficClient.NormalizePaperTheme("plain"));
        Assert.Equal("kami", AirTrafficClient.NormalizePaperTheme("Kami"));
        Assert.Equal("dark", AirTrafficClient.NormalizePaperTheme("dark"));
        Assert.Equal("kaminight", AirTrafficClient.NormalizePaperTheme("KamiNight"));
        Assert.Equal("sage", AirTrafficClient.NormalizePaperTheme("sage"));
        Assert.Equal("eink", AirTrafficClient.NormalizePaperTheme("eink"));
        Assert.Equal("other", AirTrafficClient.NormalizePaperTheme("custom-uuid-123"));

        // TTS engines
        Assert.Equal("local_kokoro", AirTrafficClient.NormalizeTtsEngine("kokoro.zh_female"));
        Assert.Equal("local_kokoro", AirTrafficClient.NormalizeTtsEngine("local_kokoro"));
        Assert.Equal("system", AirTrafficClient.NormalizeTtsEngine("system"));
        Assert.Equal("system", AirTrafficClient.NormalizeTtsEngine("sys:Microsoft-Xiaoxiao"));
        Assert.Equal("elevenlabs", AirTrafficClient.NormalizeTtsEngine("elevenlabs"));
        Assert.Equal("openai", AirTrafficClient.NormalizeTtsEngine("openai"));
        Assert.Equal("edge", AirTrafficClient.NormalizeTtsEngine("edge"));
        Assert.Equal("azure", AirTrafficClient.NormalizeTtsEngine("azure"));
        Assert.Equal("custom", AirTrafficClient.NormalizeTtsEngine("local"));
        Assert.Equal("custom", AirTrafficClient.NormalizeTtsEngine("voicestudio"));
        Assert.Equal("other", AirTrafficClient.NormalizeTtsEngine("free"));
        Assert.Equal("other", AirTrafficClient.NormalizeTtsEngine("volcengine"));

        // TTS surfaces
        Assert.Equal("reader", AirTrafficClient.NormalizeTtsSurface("reader"));
        Assert.Equal("podcast", AirTrafficClient.NormalizeTtsSurface("podcast"));
        Assert.Equal("audiobook", AirTrafficClient.NormalizeTtsSurface("audiobook"));
        Assert.Equal("other", AirTrafficClient.NormalizeTtsSurface("preview"));

        // Audio feature kinds
        Assert.Equal("audiobook_play", AirTrafficClient.NormalizeAudioKind("audiobook_play"));
        Assert.Equal("audiobook_align", AirTrafficClient.NormalizeAudioKind("audiobook_align"));
        Assert.Equal("podcast_added", AirTrafficClient.NormalizeAudioKind("podcast_added"));
        Assert.Equal("podcast_play", AirTrafficClient.NormalizeAudioKind("podcast_play"));
        Assert.Equal("podcast_transcribe", AirTrafficClient.NormalizeAudioKind("podcast_transcribe"));
        Assert.Null(AirTrafficClient.NormalizeAudioKind("invalid_kind"));
        Assert.Null(AirTrafficClient.NormalizeAudioKind(""));
    }

    [Fact]
    public async Task CustomEvents_EmitNormalizedPropertiesAndClampFeedbackImageCount()
    {
        var prefs = new DevicePrefs();
        var (client, captured, flush) = CreateCapturingClient(prefs);
        using (client)
        {
            client.TrackBookImported("Mobi", "webdav", "D:\\Lib\\ThreeBody.azw3");
            client.TrackReadingOpened("Mobi", "KamiNight", "D:\\Lib\\ThreeBody.azw3");
            client.TrackTtsStarted("kokoro.zh_1", "reader");
            client.TrackAudioFeatureUsed("podcast_transcribe");
            client.TrackAudioFeatureUsed("unknown_kind_ignored");
            client.TrackFeedbackSubmitted(-3, includeDeviceInfo: false);
            client.TrackFeedbackSubmitted(9, includeDeviceInfo: true);
            await flush();
        }

        Assert.Equal(6, captured.Count);

        Assert.Equal("book_imported", captured[0].RootElement.GetProperty("event").GetString());
        Assert.Equal("azw3", captured[0].RootElement.GetProperty("properties").GetProperty("format").GetString());
        Assert.Equal("webdav", captured[0].RootElement.GetProperty("properties").GetProperty("source").GetString());

        Assert.Equal("reading_opened", captured[1].RootElement.GetProperty("event").GetString());
        Assert.Equal("azw3", captured[1].RootElement.GetProperty("properties").GetProperty("format").GetString());
        Assert.Equal("kaminight", captured[1].RootElement.GetProperty("properties").GetProperty("paper_theme").GetString());

        Assert.Equal("tts_started", captured[2].RootElement.GetProperty("event").GetString());
        Assert.Equal("local_kokoro", captured[2].RootElement.GetProperty("properties").GetProperty("engine").GetString());
        Assert.Equal("reader", captured[2].RootElement.GetProperty("properties").GetProperty("surface").GetString());

        Assert.Equal("audio_feature_used", captured[3].RootElement.GetProperty("event").GetString());
        Assert.Equal("podcast_transcribe", captured[3].RootElement.GetProperty("properties").GetProperty("kind").GetString());

        var fbZero = captured[4].RootElement.GetProperty("properties");
        Assert.Equal("feedback_submitted", captured[4].RootElement.GetProperty("event").GetString());
        Assert.False(fbZero.GetProperty("has_images").GetBoolean());
        Assert.Equal(0, fbZero.GetProperty("image_count").GetInt32());
        Assert.False(fbZero.GetProperty("include_device_info").GetBoolean());

        var fbMax = captured[5].RootElement.GetProperty("properties");
        Assert.Equal("feedback_submitted", captured[5].RootElement.GetProperty("event").GetString());
        Assert.True(fbMax.GetProperty("has_images").GetBoolean());
        Assert.Equal(4, fbMax.GetProperty("image_count").GetInt32());
        Assert.True(fbMax.GetProperty("include_device_info").GetBoolean());

        // Zero free-text or PII / person-profile keys in any emitted payload.
        string[] forbiddenKeys =
        [
            "$set", "$set_once", "email", "title", "book_title", "author",
            "filename", "file_path", "path", "note", "highlight", "query", "url", "error",
        ];
        foreach (var doc in captured)
        {
            string evt = doc.RootElement.GetProperty("event").GetString() ?? "";
            Assert.DoesNotContain(evt, new[] { "identify", "$identify", "$set", "$set_once", "alias" });
            var props = doc.RootElement.GetProperty("properties");
            Assert.False(props.GetProperty("$process_person_profile").GetBoolean());
            foreach (string bad in forbiddenKeys)
                Assert.False(props.TryGetProperty(bad, out _), $"Forbidden key '{bad}' present in event '{evt}'");
            Assert.DoesNotContain("ThreeBody", doc.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
            doc.Dispose();
        }
    }

    [Fact]
    public async Task SetEnabled_FalseSuppressesAndDropsQueue_TrueResumes()
    {
        var prefs = new DevicePrefs();
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var startedFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentEvents = new ConcurrentQueue<string>();

        using var client = new AirTrafficClient(
            prefs,
            () => { },
            apiKey: "test_key",
            customSender: async (payload, _) =>
            {
                using var doc = JsonDocument.Parse(payload);
                string evt = doc.RootElement.GetProperty("event").GetString() ?? "";
                if (evt == "$screen" && !startedFirst.Task.IsCompleted)
                {
                    startedFirst.TrySetResult(true);
                    await gate.Task.ConfigureAwait(false);
                }
                sentEvents.Enqueue(evt);
            },
            utcNow: () => FixedUtc);

        // Hold first event in-flight while enqueuing additional events behind it.
        client.TrackScreen("Shelf");
        await startedFirst.Task.WaitAsync(System.TimeSpan.FromSeconds(2));

        client.TrackBookImported("epub", "wifi");
        client.TrackTtsStarted("edge", "reader");

        // Disabling immediately drops queued (unsent) events and suppresses subsequent events.
        client.SetEnabled(false);
        Assert.False(client.IsEnabled);
        Assert.False(prefs.AnalyticsEnabled);

        client.TrackReadingOpened("epub", "kami");
        client.TrackAudioFeatureUsed("audiobook_play");

        gate.TrySetResult(true);
        await client.FlushAsync(System.TimeSpan.FromSeconds(2));

        // Only the already-in-flight first event finished; queued and post-disable events were dropped/suppressed.
        Assert.Equal(new[] { "$screen" }, sentEvents.ToArray());

        // Re-enabling resumes tracking and resets screen deduplication so "Shelf" emits again.
        client.SetEnabled(true);
        Assert.True(client.IsEnabled);
        Assert.True(prefs.AnalyticsEnabled);

        client.TrackScreen("Shelf");
        client.TrackFeedbackSubmitted(2, includeDeviceInfo: true);
        await client.FlushAsync(System.TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { "$screen", "$screen", "feedback_submitted" }, sentEvents.ToArray());
    }

    [Fact]
    public async Task EmptyDistinctIdAfterRestore_RegeneratesBeforeCapture()
    {
        var prefs = new DevicePrefs();
        int saveCalls = 0;
        var captured = new ConcurrentQueue<string>();

        using var client = new AirTrafficClient(
            prefs,
            () => saveCalls++,
            apiKey: "test_key",
            customSender: (payload, _) =>
            {
                captured.Enqueue(payload);
                return Task.CompletedTask;
            },
            utcNow: () => FixedUtc);

        // Simulate snapshot restore clearing AnalyticsDistinctId.
        prefs.AnalyticsDistinctId = "";
        int savesBefore = saveCalls;

        client.TrackScreen("Shelf");
        await client.FlushAsync(System.TimeSpan.FromSeconds(2));

        Assert.True(captured.TryDequeue(out var raw));
        using var doc = JsonDocument.Parse(raw);
        string distinctId = doc.RootElement.GetProperty("distinct_id").GetString() ?? "";
        Assert.False(string.IsNullOrWhiteSpace(distinctId));
        Assert.Equal(distinctId, prefs.AnalyticsDistinctId);
        Assert.True(saveCalls > savesBefore);
    }

    [Fact]
    public async Task Dispose_WithStalledNetwork_DoesNotThrowOrStallLong()
    {
        var prefs = new DevicePrefs();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var client = new AirTrafficClient(
            prefs,
            () => { },
            apiKey: "test_key",
            customSender: async (_, _) =>
            {
                started.TrySetResult(true);
                await release.Task.ConfigureAwait(false);
            },
            utcNow: () => FixedUtc);

        client.TrackScreen("Shelf");
        await started.Task.WaitAsync(System.TimeSpan.FromSeconds(2));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        client.Dispose();
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1200, $"Dispose stalled for {sw.ElapsedMilliseconds}ms");

        // Unblock background sender after Dispose returns; must unwind without unhandled exceptions.
        release.TrySetResult(true);
        await Task.Delay(50);
    }
}
