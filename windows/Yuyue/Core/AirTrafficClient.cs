using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Yuyue.Core;

/// <summary>
/// Lightweight, dependency-free PostHog <c>/capture/</c> client for anonymous AirTraffic analytics.
/// <para>
/// Privacy contract:
/// <list type="bullet">
///   <item>Never sends <c>identify</c>, <c>$identify</c>, <c>$set</c>, <c>$set_once</c>, or <c>alias</c>.</item>
///   <item>Always attaches <c>$process_person_profile: false</c>.</item>
///   <item>Never emits free text (book titles, authors, filenames, paths, notes, highlights, queries, URLs, emails, or error strings).</item>
///   <item>All network failures are silently swallowed; queue is bounded to 64 events (drop-oldest).</item>
/// </list>
/// </para>
/// </summary>
public sealed class AirTrafficClient : IDisposable
{
    internal const string DefaultTrafficHost = "https://apps.air.build/traffic";
    private const string DefaultApiKey = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg";
    private const string LibName = "yuyue-windows";
    private const string PlatformName = "windows";
    private const int MaxQueueCapacity = 64;

    private static readonly HashSet<string> BlockedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "identify",
        "$identify",
        "$set",
        "$set_once",
        "alias",
        "$alias",
        "$create_alias",
    };

    private static readonly HashSet<string> BlockedPropertyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "$set",
        "$set_once",
        "email",
        "name",
        "title",
        "book_title",
        "author",
        "filename",
        "file_path",
        "path",
        "note",
        "highlight",
        "query",
        "search_query",
        "url",
        "error",
        "error_message",
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly DevicePrefs _prefs;
    private readonly Action _savePrefs;
    private readonly string _apiKey;
    private readonly string _captureUrl;
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Func<string, CancellationToken, Task>? _customSender;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly string _appVersion;
    private readonly string _appBuild;
    private readonly string _osVersion;

    private readonly object _gate = new();
    private readonly Queue<string> _queue = new(MaxQueueCapacity);
    private readonly List<TaskCompletionSource<bool>> _idleWaiters = new();
    private readonly SemaphoreSlim _signal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _cts = new();

    private Task? _drainTask;
    private bool _enabled;
    private bool _inFlight;
    private bool _disposed;
    private int _disposeOnce;
    private string? _lastScreenName;

    public AirTrafficClient(DevicePrefs prefs, Action savePrefs)
        : this(prefs, savePrefs, apiKey: DefaultApiKey, trafficHost: DefaultTrafficHost)
    {
    }

    internal AirTrafficClient(
        DevicePrefs prefs,
        Action savePrefs,
        string? apiKey = null,
        string? trafficHost = null,
        HttpMessageHandler? httpHandler = null,
        Func<string, CancellationToken, Task>? customSender = null,
        Func<DateTimeOffset>? utcNow = null,
        string? appVersion = null,
        ulong? appBuild = null,
        string? osVersion = null)
    {
        _prefs = prefs ?? throw new ArgumentNullException(nameof(prefs));
        _savePrefs = savePrefs ?? throw new ArgumentNullException(nameof(savePrefs));
        _apiKey = (apiKey ?? DefaultApiKey).Trim();
        string host = string.IsNullOrWhiteSpace(trafficHost) ? DefaultTrafficHost : trafficHost.Trim();
        _captureUrl = host.TrimEnd('/') + "/capture/";
        _customSender = customSender;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? UpdateAndFeedbackService.CurrentVersion : appVersion.Trim();
        _appBuild = (appBuild ?? UpdateAndFeedbackService.CurrentBuild).ToString(CultureInfo.InvariantCulture);
        _osVersion = string.IsNullOrWhiteSpace(osVersion) ? Environment.OSVersion.VersionString : osVersion.Trim();

        if (_customSender is null)
        {
            _httpClient = httpHandler is not null
                ? new HttpClient(httpHandler, disposeHandler: false)
                : new HttpClient();
            _httpClient.Timeout = System.TimeSpan.FromSeconds(5);
            _ownsHttpClient = true;
        }

        if (string.IsNullOrWhiteSpace(_prefs.AnalyticsDistinctId))
        {
            _prefs.AnalyticsDistinctId = Guid.NewGuid().ToString("D");
            try { _savePrefs(); } catch { }
        }

        _enabled = _prefs.AnalyticsEnabled;
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate) return _enabled && !_disposed;
        }
    }

    public string DistinctId => _prefs.AnalyticsDistinctId;

    public void SetEnabled(bool enabled)
    {
        bool changed;
        lock (_gate)
        {
            if (_disposed) return;
            changed = _enabled != enabled || _prefs.AnalyticsEnabled != enabled;
            _enabled = enabled;
            _prefs.AnalyticsEnabled = enabled;
            if (!enabled)
            {
                _queue.Clear();
                _lastScreenName = null;
                if (!_inFlight) CompleteIdleWaitersLocked();
            }
        }

        if (changed)
        {
            try { _savePrefs(); } catch { }
        }
    }

    // ---------- Lifecycle ----------

    public void TrackAppLaunch(bool startBackground = false)
    {
        if (!IsEnabled) return;

        string prevVersion = _prefs.AnalyticsLastVersion ?? "";
        if (string.IsNullOrWhiteSpace(prevVersion))
        {
            CaptureInternal("Application Installed", new Dictionary<string, object?>
            {
                ["version"] = _appVersion,
                ["build"] = _appBuild,
                ["platform"] = PlatformName,
            });
            _prefs.AnalyticsLastVersion = _appVersion;
            try { _savePrefs(); } catch { }
        }
        else if (!string.Equals(prevVersion, _appVersion, StringComparison.Ordinal))
        {
            CaptureInternal("Application Updated", new Dictionary<string, object?>
            {
                ["previous_version"] = prevVersion,
                ["version"] = _appVersion,
                ["build"] = _appBuild,
                ["platform"] = PlatformName,
            });
            _prefs.AnalyticsLastVersion = _appVersion;
            try { _savePrefs(); } catch { }
        }

        if (!startBackground)
        {
            CaptureInternal("Application Opened", new Dictionary<string, object?>
            {
                ["from_background"] = false,
                ["platform"] = PlatformName,
            });
        }
    }

    public void TrackAppReopened()
    {
        CaptureInternal("Application Opened", new Dictionary<string, object?>
        {
            ["from_background"] = true,
            ["platform"] = PlatformName,
        });
    }

    public void TrackAppBackgrounded()
    {
        CaptureInternal("Application Backgrounded", new Dictionary<string, object?>
        {
            ["platform"] = PlatformName,
        });
        // Non-blocking nudge: drain worker starts immediately upon enqueue.
        _ = FlushAsync(System.TimeSpan.FromMilliseconds(1500));
    }

    // ---------- Screen Tracking ----------

    public void TrackScreen(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        string trimmed = name.Trim();

        lock (_gate)
        {
            if (!_enabled || _disposed) return;
            if (string.Equals(_lastScreenName, trimmed, StringComparison.Ordinal)) return;
            _lastScreenName = trimmed;
        }

        CaptureInternal("$screen", new Dictionary<string, object?>
        {
            ["$screen_name"] = trimmed,
            ["platform"] = PlatformName,
        });
    }

    // ---------- Typed Custom Events ----------

    public void TrackBookImported(string? formatRaw, string? sourceRaw, string? filename = null)
    {
        CaptureInternal("book_imported", new Dictionary<string, object?>
        {
            ["format"] = NormalizeImportFormat(formatRaw, filename),
            ["source"] = NormalizeImportSource(sourceRaw),
            ["platform"] = PlatformName,
        });
    }

    public void TrackReadingOpened(string? formatRaw, string? paperThemeRaw, string? filename = null)
    {
        CaptureInternal("reading_opened", new Dictionary<string, object?>
        {
            ["format"] = NormalizeReadingFormat(formatRaw, filename),
            ["paper_theme"] = NormalizePaperTheme(paperThemeRaw),
            ["platform"] = PlatformName,
        });
    }

    public void TrackTtsStarted(string? engineRaw, string? surfaceRaw)
    {
        CaptureInternal("tts_started", new Dictionary<string, object?>
        {
            ["engine"] = NormalizeTtsEngine(engineRaw),
            ["surface"] = NormalizeTtsSurface(surfaceRaw),
            ["platform"] = PlatformName,
        });
    }

    public void TrackAudioFeatureUsed(string? kind)
    {
        string? normalized = NormalizeAudioKind(kind);
        if (normalized is null) return;

        CaptureInternal("audio_feature_used", new Dictionary<string, object?>
        {
            ["kind"] = normalized,
            ["platform"] = PlatformName,
        });
    }

    public void TrackFeedbackSubmitted(int imageCount, bool includeDeviceInfo)
    {
        int clamped = Math.Clamp(imageCount, 0, 4);
        CaptureInternal("feedback_submitted", new Dictionary<string, object?>
        {
            ["has_images"] = clamped > 0,
            ["image_count"] = clamped,
            ["include_device_info"] = includeDeviceInfo,
            ["platform"] = PlatformName,
        });
    }

    // ---------- Pure Enum Normalizers ----------

    internal static string NormalizeImportFormat(string? raw, string? filename = null)
    {
        string fileExt = ExtractCleanExtension(filename);
        // Check .azw3 before BookFormat.Mobi since Rust core groups .azw3 under BookFormat.Mobi.
        if (fileExt == "azw3") return "azw3";

        string norm = (raw ?? "").Trim().ToLowerInvariant().TrimStart('.');
        string? mapped = norm switch
        {
            "epub" => "epub",
            "txt" or "text" => "txt",
            "pdf" => "pdf",
            "azw3" => "azw3",
            "mobi" or "azw" or "prc" => "mobi",
            "audio" or "audiobook" or "mp3" or "m4a" or "m4b" or "flac" or "ogg" or "opus" or "wav" or "aac" or "wma" => "audio",
            _ => null,
        };
        if (mapped is not null) return mapped;

        if (!string.IsNullOrEmpty(fileExt))
        {
            return fileExt switch
            {
                "epub" => "epub",
                "txt" => "txt",
                "pdf" => "pdf",
                "azw3" => "azw3",
                "mobi" or "azw" or "prc" => "mobi",
                "mp3" or "m4a" or "m4b" or "flac" or "ogg" or "opus" or "wav" or "aac" or "wma" => "audio",
                _ => "other",
            };
        }

        return "other";
    }

    internal static string NormalizeImportSource(string? raw)
    {
        string norm = (raw ?? "").Trim().ToLowerInvariant();
        return norm switch
        {
            "file_picker" or "picker" or "filepicker" => "file_picker",
            "wifi" or "wi-fi" or "wifi_transfer" => "wifi",
            "opds" => "opds",
            "webdav" => "webdav",
            "share_in" or "share" or "open_in" or "drop" or "drag_drop" => "share_in",
            _ => "other",
        };
    }

    internal static string NormalizeReadingFormat(string? raw, string? filename = null)
    {
        string fileExt = ExtractCleanExtension(filename);
        if (fileExt == "azw3") return "azw3";

        string norm = (raw ?? "").Trim().ToLowerInvariant().TrimStart('.');
        string? mapped = norm switch
        {
            "epub" => "epub",
            "txt" or "text" => "txt",
            "pdf" => "pdf",
            "azw3" => "azw3",
            "mobi" or "azw" or "prc" => "mobi",
            _ => null,
        };
        if (mapped is not null) return mapped;

        if (!string.IsNullOrEmpty(fileExt))
        {
            return fileExt switch
            {
                "epub" => "epub",
                "txt" => "txt",
                "pdf" => "pdf",
                "azw3" => "azw3",
                "mobi" or "azw" or "prc" => "mobi",
                _ => "other",
            };
        }

        return "other";
    }

    internal static string NormalizePaperTheme(string? raw)
    {
        string norm = (raw ?? "").Trim().ToLowerInvariant();
        return norm switch
        {
            "plain" or "white" or "light" => "plain",
            "kami" or "sepia" or "warm" => "kami",
            "dark" or "night" => "dark",
            "kaminight" or "kami_night" or "kami-night" => "kaminight",
            "sage" or "green" => "sage",
            "eink" or "e-ink" or "e_ink" => "eink",
            _ => "other",
        };
    }

    internal static string NormalizeTtsEngine(string? raw)
    {
        string norm = (raw ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(norm)) return "other";

        if (norm is "local_kokoro" or "kokoro" || norm.StartsWith("kokoro.", StringComparison.Ordinal))
            return "local_kokoro";
        if (norm is "system" or "sapi" or "winrt" || norm.StartsWith("sys:", StringComparison.Ordinal))
            return "system";
        if (norm is "elevenlabs" or "eleven_labs" || norm.StartsWith("cloud.elevenlabs.", StringComparison.Ordinal))
            return "elevenlabs";
        if (norm == "openai" || norm.StartsWith("cloud.openai.", StringComparison.Ordinal))
            return "openai";
        if (norm == "edge" || norm.StartsWith("cloud.edge.", StringComparison.Ordinal))
            return "edge";
        if (norm == "azure" || norm.StartsWith("cloud.azure.", StringComparison.Ordinal))
            return "azure";
        if (norm is "custom" or "local" or "voicestudio" or "voicebox" or "mlxaudio" or "speaches"
            || norm.StartsWith("cloud.local.", StringComparison.Ordinal)
            || norm.StartsWith("cloud.voicestudio.", StringComparison.Ordinal)
            || norm.StartsWith("cloud.voicebox.", StringComparison.Ordinal)
            || norm.StartsWith("cloud.mlxaudio.", StringComparison.Ordinal)
            || norm.StartsWith("cloud.speaches.", StringComparison.Ordinal))
            return "custom";

        return "other";
    }

    internal static string NormalizeTtsSurface(string? raw)
    {
        string norm = (raw ?? "").Trim().ToLowerInvariant();
        return norm switch
        {
            "reader" or "pdf" or "ebook" => "reader",
            "podcast" => "podcast",
            "audiobook" => "audiobook",
            _ => "other",
        };
    }

    internal static string? NormalizeAudioKind(string? raw)
    {
        string norm = (raw ?? "").Trim().ToLowerInvariant();
        return norm switch
        {
            "audiobook_play" => "audiobook_play",
            "audiobook_align" => "audiobook_align",
            "podcast_added" => "podcast_added",
            "podcast_play" => "podcast_play",
            "podcast_transcribe" => "podcast_transcribe",
            _ => null,
        };
    }

    private static string ExtractCleanExtension(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        try
        {
            return Path.GetExtension(filename.Trim()).ToLowerInvariant().TrimStart('.');
        }
        catch
        {
            return "";
        }
    }

    // ---------- Queue, Serialization & Flush ----------

    private void CaptureInternal(string eventName, Dictionary<string, object?>? extraProps)
    {
        if (string.IsNullOrWhiteSpace(eventName) || BlockedEvents.Contains(eventName)) return;
        if (string.IsNullOrEmpty(_apiKey)) return;

        string payloadJson;
        lock (_gate)
        {
            if (!_enabled || _disposed) return;

            var props = new Dictionary<string, object?>
            {
                ["$process_person_profile"] = false,
                ["$lib"] = LibName,
                ["$lib_version"] = _appVersion,
                ["$os"] = "Windows",
                ["$os_version"] = _osVersion,
                ["$app_version"] = _appVersion,
                ["$app_build"] = _appBuild,
                ["platform"] = PlatformName,
            };

            if (extraProps is not null)
            {
                foreach (var (k, v) in extraProps)
                {
                    if (string.IsNullOrWhiteSpace(k) || BlockedPropertyKeys.Contains(k)) continue;
                    props[k] = v;
                }
            }
            // Enforce invariant unconditionally.
            props["$process_person_profile"] = false;
            props["platform"] = PlatformName;

            if (string.IsNullOrWhiteSpace(_prefs.AnalyticsDistinctId))
            {
                _prefs.AnalyticsDistinctId = Guid.NewGuid().ToString("D");
                try { _savePrefs(); } catch { }
            }

            var envelope = new Dictionary<string, object?>
            {
                ["api_key"] = _apiKey,
                ["event"] = eventName,
                ["distinct_id"] = _prefs.AnalyticsDistinctId,
                ["timestamp"] = _utcNow().ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["properties"] = props,
            };

            try
            {
                payloadJson = JsonSerializer.Serialize(envelope, JsonOptions);
            }
            catch
            {
                return;
            }

            while (_queue.Count >= MaxQueueCapacity)
                _queue.Dequeue();
            _queue.Enqueue(payloadJson);

            EnsureDrainStartedLocked();
        }

        try { _signal.Release(); } catch { }
    }

    private void EnsureDrainStartedLocked()
    {
        if (_drainTask is null)
            _drainTask = Task.Run(DrainLoopAsync);
    }

    private async Task DrainLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await _signal.WaitAsync(_cts.Token).ConfigureAwait(false);

                while (!_cts.IsCancellationRequested)
                {
                    string? payload;
                    lock (_gate)
                    {
                        if (!_enabled || _queue.Count == 0)
                        {
                            if (!_enabled) _queue.Clear();
                            _inFlight = false;
                            CompleteIdleWaitersLocked();
                            break;
                        }

                        payload = _queue.Dequeue();
                        _inFlight = true;
                    }

                    try
                    {
                        await SendPayloadAsync(payload, _cts.Token).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Silently swallow network/cancellation/serialization failures.
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            lock (_gate)
            {
                _inFlight = false;
                CompleteIdleWaitersLocked();
            }
        }
    }

    private async Task SendPayloadAsync(string payloadJson, CancellationToken ct)
    {
        if (_customSender is not null)
        {
            await _customSender(payloadJson, ct).ConfigureAwait(false);
            return;
        }

        if (_httpClient is null) return;
        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        using var resp = await _httpClient.PostAsync(_captureUrl, content, ct).ConfigureAwait(false);
        // Best-effort fire-and-forget; ignore non-2xx responses.
    }

    public async Task FlushAsync(System.TimeSpan timeout)
    {
        TaskCompletionSource<bool>? waiter = null;
        lock (_gate)
        {
            if (_queue.Count == 0 && !_inFlight) return;
            waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _idleWaiters.Add(waiter);
            EnsureDrainStartedLocked();
        }

        try { _signal.Release(); } catch { }

        if (timeout <= System.TimeSpan.Zero)
        {
            RemoveIdleWaiter(waiter);
            return;
        }

        using var delayCts = new CancellationTokenSource();
        var delayTask = Task.Delay(timeout, delayCts.Token);
        var finished = await Task.WhenAny(waiter.Task, delayTask).ConfigureAwait(false);
        if (ReferenceEquals(finished, waiter.Task))
        {
            delayCts.Cancel();
        }
        else
        {
            RemoveIdleWaiter(waiter);
        }
    }

    private void RemoveIdleWaiter(TaskCompletionSource<bool> waiter)
    {
        lock (_gate)
        {
            _idleWaiters.Remove(waiter);
        }
    }

    private void CompleteIdleWaitersLocked()
    {
        if (_idleWaiters.Count == 0) return;
        foreach (var w in _idleWaiters)
            w.TrySetResult(true);
        _idleWaiters.Clear();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0) return;

        bool hasWork;
        lock (_gate)
        {
            hasWork = _queue.Count > 0 || _inFlight;
        }

        if (hasWork)
        {
            try
            {
                FlushAsync(System.TimeSpan.FromMilliseconds(250)).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        Task? drain;
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
            CompleteIdleWaitersLocked();
            drain = _drainTask;
        }

        try { _cts.Cancel(); } catch { }

        if (drain is null || drain.IsCompleted)
        {
            DisposeHandles();
            return;
        }

        bool finished = false;
        try { finished = drain.Wait(System.TimeSpan.FromMilliseconds(150)); } catch { finished = drain.IsCompleted; }

        if (finished)
        {
            DisposeHandles();
        }
        else
        {
            _ = drain.ContinueWith(_ => DisposeHandles(), TaskScheduler.Default);
        }
    }

    private void DisposeHandles()
    {
        try { _cts.Dispose(); } catch { }
        try { _signal.Dispose(); } catch { }
        if (_ownsHttpClient)
        {
            try { _httpClient?.Dispose(); } catch { }
        }
    }
}
