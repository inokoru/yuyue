using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
namespace Yuyue.Core;

public enum SyncStateKind
{
    Idle,
    Syncing,
    Failed,
}

/// <summary>
/// Cross-platform sync compatible 1:1 with macOS/iOS/Android <c>Sync.swift</c> &amp; <c>GoogleDrive.swift</c>:
/// each device writes only its own <c>&lt;deviceId&gt;.yuyue-sync.json</c> and reads peers' files;
/// book files live beside them as <c>&lt;bookId&gt;.book.&lt;ext&gt;</c>, podcast issues as <c>&lt;id&gt;.pod-issue.json</c>,
/// and podcast clips as <c>&lt;file&gt;.pod-clip</c>.
/// Supports WebDAV (坚果云 / Nextcloud / etc.), local synced folders (OneDrive / iCloud for Windows / Syncthing),
/// and Google Drive hidden <c>appDataFolder</c> via OAuth 2.0 loopback PKCE.
/// </summary>
public sealed class SyncService
{
    private const string Suffix = ".yuyue-sync.json";
    public const string GoogleRefreshSecretKey = "sync.google.refresh";

    private readonly LibraryService _library;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _keepUpCts;

    public SyncStateKind State { get; private set; } = SyncStateKind.Idle;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSynced { get; private set; }

    public event Action? StatusChanged;
    public event Action? LibraryChanged;

    public SyncService(LibraryService library)
    {
        _library = library;
        if (library.Prefs.LastSyncedUnixMs is long ms && ms > 0)
        {
            LastSynced = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
    }

    public string EffectiveGoogleClientId =>
        !string.IsNullOrWhiteSpace(_library.Prefs.GoogleClientId)
            ? _library.Prefs.GoogleClientId.Trim()
            : (Environment.GetEnvironmentVariable("YY_GOOGLE_CLIENT_ID")?.Trim() ?? "");

    public bool IsEnabled => _library.Prefs.SyncMethod switch
    {
        "webdav" => !string.IsNullOrWhiteSpace(_library.Prefs.WebDavUrl),
        "folder" => !string.IsNullOrWhiteSpace(_library.Prefs.SyncLocalFolder),
        "gdrive" => !string.IsNullOrWhiteSpace(_library.Secrets.Get(GoogleRefreshSecretKey))
                    && !string.IsNullOrWhiteSpace(EffectiveGoogleClientId),
        _ => false,
    };

    public string StatusSummary()
    {
        if (State == SyncStateKind.Syncing) return "正在同步…";
        if (State == SyncStateKind.Failed) return LastError ?? "同步未成功";
        if (!IsEnabled) return "尚未开启同步";

        string place = _library.Prefs.SyncMethod switch
        {
            "webdav" => "WebDAV",
            "gdrive" => string.IsNullOrWhiteSpace(_library.Prefs.GoogleEmail) ? "Google Drive" : $"Google Drive ({_library.Prefs.GoogleEmail})",
            _ => "同步文件夹",
        };
        if (LastSynced is null) return $"{place} · 待同步";
        var delta = DateTimeOffset.Now - LastSynced.Value;
        string ago = delta.TotalMinutes < 1 ? "刚刚同步"
            : delta.TotalHours < 1 ? $"{(int)delta.TotalMinutes} 分钟前同步"
            : delta.TotalDays < 1 ? $"今天 {LastSynced.Value.LocalDateTime:HH:mm} 同步"
            : $"{LastSynced.Value.LocalDateTime:M/d HH:mm} 同步";
        return $"{place} · {ago}";
    }

    public void StartKeepUpLoop()
    {
        if (_keepUpCts is not null) return;
        _keepUpCts = new CancellationTokenSource();
        var ct = _keepUpCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (IsEnabled) await SyncNowAsync();
                    await Task.Delay(System.TimeSpan.FromMinutes(5), ct);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    public void StopSync()
    {
        if (_library.Prefs.SyncMethod == "gdrive")
        {
            _library.Secrets.Set(GoogleRefreshSecretKey, "");
            _library.Prefs.GoogleEmail = "";
        }
        _library.Prefs.SyncMethod = "none";
        _library.SavePrefs();
        State = SyncStateKind.Idle;
        LastError = null;
        StatusChanged?.Invoke();
    }

    /// <summary>
    /// 1:1 <c>GoogleAuth.signIn</c> + <c>Sync.connectGoogle</c> using Google's Desktop OAuth 2.0 loopback IP PKCE flow.
    /// </summary>
    public async Task ConnectGoogleDriveAsync(CancellationToken ct = default)
    {
        string clientId = EffectiveGoogleClientId;
        string clientSecret = _library.Secrets.Get("sync.google.secret");
        if (string.IsNullOrWhiteSpace(clientId) || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.OrdinalIgnoreCase))
            throw new SyncDiagnosticException("请先填入 Google Cloud OAuth 桌面端 Client ID（以 .apps.googleusercontent.com 结尾）。");

        string verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        string challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        string state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://127.0.0.1:{port}/oauth2redirect";

        string authUrl = "https://accounts.google.com/o/oauth2/v2/auth" +
            $"?client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&response_type=code" +
            $"&scope={Uri.EscapeDataString("https://www.googleapis.com/auth/drive.appdata")}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            "&code_challenge_method=S256" +
            $"&state={Uri.EscapeDataString(state)}" +
            "&prompt=consent&access_type=offline";

        Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(System.TimeSpan.FromMinutes(3));
        await using var stopListenerReg = timeoutCts.Token.Register(() =>
        {
            try { listener.Stop(); } catch { }
        });

        string? code = null;
        try
        {
            while (!timeoutCts.IsCancellationRequested)
            {
                using var tcp = await listener.AcceptTcpClientAsync(timeoutCts.Token);
                using var net = tcp.GetStream();
                var buf = new byte[16 * 1024];
                int n = await net.ReadAsync(buf.AsMemory(0, buf.Length), timeoutCts.Token);
                if (n <= 0) continue;

                string reqText = Encoding.UTF8.GetString(buf, 0, n);
                string target = reqText.Split(' ').Skip(1).FirstOrDefault() ?? "";
                if (!target.StartsWith("/oauth2redirect", StringComparison.Ordinal))
                {
                    byte[] ignore = Encoding.UTF8.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
                    await net.WriteAsync(ignore, timeoutCts.Token);
                    continue;
                }

                var q = ParseQuery(target);
                q.TryGetValue("code", out var gotCode);
                q.TryGetValue("state", out var gotState);
                bool ok = !string.IsNullOrEmpty(gotCode) && gotState == state && !q.ContainsKey("error");

                string msg = ok ? "已经登录 Google Drive，可以回到语阅了。" : "没有登录成功，回到语阅再试一次。";
                byte[] html = Encoding.UTF8.GetBytes(
                    $"<!doctype html><meta charset=utf-8><body style='font:16px \"Microsoft YaHei UI\",sans-serif;padding:48px 24px;text-align:center'>{msg}</body>");
                byte[] head = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {html.Length}\r\nConnection: close\r\n\r\n");
                await net.WriteAsync(head, timeoutCts.Token);
                await net.WriteAsync(html, timeoutCts.Token);
                await net.FlushAsync(timeoutCts.Token);

                if (!ok) throw new SyncDiagnosticException("Google 账号授权已取消或验证失败。");
                code = gotCode;
                break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            if (!timeoutCts.IsCancellationRequested)
                throw;
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(code))
            throw new SyncDiagnosticException("等待浏览器完成 Google 授权超时。");

        var form = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code!,
            ["code_verifier"] = verifier,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = redirectUri,
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret.Trim();

        using var http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };
        using var tokenRes = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form), ct);
        var tokenBody = await tokenRes.Content.ReadAsStringAsync(ct);
        if (!tokenRes.IsSuccessStatusCode)
            throw new SyncDiagnosticException($"换取 Google 令牌失败（{(int)tokenRes.StatusCode}）：{tokenBody}");

        using var doc = JsonDocument.Parse(tokenBody);
        string? refresh = doc.RootElement.TryGetProperty("refresh_token", out var rp) ? rp.GetString() : null;
        if (string.IsNullOrWhiteSpace(refresh))
            throw new SyncDiagnosticException("未获取到 Google 离线刷新令牌，请重试并在授权页勾选云端硬盘权限。");

        ct.ThrowIfCancellationRequested();
        _library.Secrets.Set(GoogleRefreshSecretKey, refresh);
        using var gdrive = new GoogleDriveSyncFolder(clientId, clientSecret, refresh);
        await gdrive.PrepareAsync();
        await gdrive.ListAsync();
        ct.ThrowIfCancellationRequested();
        _library.Prefs.GoogleEmail = await gdrive.TryGetEmailAsync() ?? "";
        _library.Prefs.SyncMethod = "gdrive";
        _library.SavePrefs();
        await SyncNowAsync();
    }

    public async Task<(bool Ok, string Message)> DiagnoseWebDavAsync(string url, string user, string password, string folder)
    {
        try
        {
            using var dav = new WebDavClient(url, user, password, folder);
            await dav.PrepareAsync();
            var list = await dav.ListAsync();
            return (true, $"连接成功（目录“{folder}”内共 {list.Count} 个文件）");
        }
        catch (SyncDiagnosticException ex)
        {
            return (false, ex.Message);
        }
        catch (TaskCanceledException)
        {
            return (false, "连接超时，请检查网络或服务器地址。");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"网络错误：{ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败：{ex.Message}");
        }
    }

    public async Task SyncNowAsync()
    {
        if (!IsEnabled) return;
        if (!await _gate.WaitAsync(0)) return;

        State = SyncStateKind.Syncing;
        LastError = null;
        StatusChanged?.Invoke();

        ISyncFolder? folder = null;
        try
        {
            folder = CreateFolder();
            await folder.PrepareAsync();

            var leftOut = BuildLeftOut();
            var curLeftOutNames = leftOut.Select(PartName).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var prevSet = _library.Prefs.SyncPreviousLeftOut.ToHashSet(StringComparer.Ordinal);
            var curSet = curLeftOutNames.ToHashSet(StringComparer.Ordinal);
            // 1:1 Sync.swift:298-300 — if a previously excluded part is turned back on, re-read all peer files from the start!
            if (prevSet.Count > 0 && !prevSet.IsSubsetOf(curSet))
            {
                _library.Prefs.SyncSeenTags.Clear();
            }

            uint applied = 0;

            // 1. Read peers' op logs
            string myFile = _library.Core.DeviceId() + Suffix;
            var remoteFiles = await folder.ListAsync();
            foreach (var (name, tag) in remoteFiles)
            {
                if (!name.EndsWith(Suffix, StringComparison.Ordinal) || name == myFile) continue;
                if (_library.Prefs.SyncSeenTags.TryGetValue(name, out var seenTag) && seenTag == tag) continue;

                var bytes = await folder.ReadAsync(name);
                var batch = Encoding.UTF8.GetString(bytes);
                applied += _library.Core.ImportChangesExcept(batch, leftOut);
                _library.Prefs.SyncSeenTags[name] = tag;
            }

            // 2. Write our own op log when our version changed
            string versionKey = _library.Core.SyncVersion() + "|" + string.Join(",", curLeftOutNames);
            if (!remoteFiles.ContainsKey(myFile) || _library.Prefs.SyncWrittenVersion != versionKey)
            {
                string payload = _library.Core.ExportChangesExcept("{}", leftOut);
                await folder.WriteAsync(myFile, Encoding.UTF8.GetBytes(payload));
                _library.Prefs.SyncWrittenVersion = versionKey;
            }
            _library.Prefs.SyncPreviousLeftOut = curLeftOutNames;

            // 3. Exchange book files (<id>.book.<ext>) — podcast books always sync, other books when SyncFiles is on
            int missedFiles = 0;
            var allBooks = _library.Core.Books();
            var eligibleBooks = allBooks.Where(b => _library.Prefs.SyncFiles || b.Format == BookFormat.Podcast).ToArray();

            foreach (var book in eligibleBooks)
            {
                if (!book.Available || book.Record) continue;
                string prefix = book.Id + ".book.";
                if (remoteFiles.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))) continue;
                try
                {
                    var orig = _library.Core.OriginalFile(book.Id);
                    if (File.Exists(orig))
                    {
                        var ext = Path.GetExtension(orig).TrimStart('.');
                        await folder.UploadFileAsync($"{book.Id}.book.{ext}", orig);
                    }
                }
                catch { /* try next round */ }
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "yuyue-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                foreach (var book in eligibleBooks)
                {
                    if (book.Available || book.Record) continue;
                    string prefix = book.Id + ".book.";
                    var remoteName = remoteFiles.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.Ordinal));
                    if (remoteName is null) continue;
                    try
                    {
                        var ext = Path.GetExtension(remoteName);
                        var safeTitle = SanitizeFileName(book.Title);
                        var dest = Path.Combine(tempDir, safeTitle + ext);
                        await folder.DownloadFileAsync(remoteName, dest);
                        _library.ImportBook(dest);
                        applied++;
                    }
                    catch
                    {
                        missedFiles++;
                    }
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }

            // 4. Exchange Podcast issues & clips (1:1 Sync.swift:359-422)
            applied += await ExchangePodcastFilesAsync(folder, remoteFiles, curSet, _library.Prefs.SyncFiles);

            // 5. Exchange Companion Q&A + Recap caches (1:1 Sync.swift:359-443)
            applied += await ExchangeCompanionCachesAsync(folder, remoteFiles, curSet);

            // 6. Exchange Audiobook recordings when SyncRecordings is on (1:1 Sync.swift:525-590)
            if (_library.Prefs.SyncRecordings)
            {
                if (await ExchangeAudiobooksAsync(folder, remoteFiles))
                    applied++;
            }

            LastSynced = DateTimeOffset.Now;
            _library.Prefs.LastSyncedUnixMs = LastSynced.Value.ToUnixTimeMilliseconds();
            _library.SavePrefs();

            if (missedFiles > 0)
            {
                State = SyncStateKind.Failed;
                LastError = $"有 {missedFiles} 本书的文件没下载下来，下次同步再试";
            }
            else
            {
                State = SyncStateKind.Idle;
                LastError = null;
            }

            if (applied > 0) LibraryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            State = SyncStateKind.Failed;
            LastError = ex.Message;
        }
        finally
        {
            (folder as IDisposable)?.Dispose();
            _gate.Release();
            StatusChanged?.Invoke();
        }
    }

    private async Task<uint> ExchangePodcastFilesAsync(
        ISyncFolder folder, Dictionary<string, string> remoteFiles, HashSet<string> leftOutNames, bool includeClips)
    {
        uint took = 0;
        try
        {
            string clipsDir = _library.Core.PodcastClipsDir();
            string? hubDir = Path.GetDirectoryName(clipsDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(hubDir)) return 0;

            if (!leftOutNames.Contains("reading"))
            {
                string issuesDir = Path.Combine(hubDir, "issues");
                Directory.CreateDirectory(issuesDir);
                foreach (var path in Directory.EnumerateFiles(issuesDir, "*.json"))
                {
                    string file = Path.GetFileName(path);
                    if (file.StartsWith('.')) continue;
                    string id = file[..^5];
                    string remote = $"{id}.pod-issue.json";
                    if (remoteFiles.ContainsKey(remote)) continue;
                    try { await folder.WriteAsync(remote, await File.ReadAllBytesAsync(path)); } catch { }
                }
                foreach (var remote in remoteFiles.Keys.Where(k => k.EndsWith(".pod-issue.json", StringComparison.Ordinal)))
                {
                    string id = remote[..^".pod-issue.json".Length];
                    if (string.IsNullOrEmpty(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..")) continue;
                    string dest = Path.Combine(issuesDir, $"{id}.json");
                    if (File.Exists(dest)) continue;
                    try
                    {
                        var bytes = await folder.ReadAsync(remote);
                        await File.WriteAllBytesAsync(dest, bytes);
                        took++;
                    }
                    catch { }
                }
            }

            if (includeClips && !leftOutNames.Contains("notes"))
            {
                Directory.CreateDirectory(clipsDir);
                foreach (var path in Directory.EnumerateFiles(clipsDir))
                {
                    string file = Path.GetFileName(path);
                    if (file.StartsWith('.')) continue;
                    string remote = $"{file}.pod-clip";
                    if (remoteFiles.ContainsKey(remote)) continue;
                    try { await folder.UploadFileAsync(remote, path); } catch { }
                }
                foreach (var remote in remoteFiles.Keys.Where(k => k.EndsWith(".pod-clip", StringComparison.Ordinal)))
                {
                    string file = remote[..^".pod-clip".Length];
                    if (string.IsNullOrEmpty(file) || file.StartsWith('.') || file.Contains('/') || file.Contains('\\') || file.Contains(".."))
                        continue;
                    string dest = Path.Combine(clipsDir, file);
                    if (File.Exists(dest)) continue;
                    string partial = Path.Combine(clipsDir, $".{file}.download");
                    try
                    {
                        await folder.DownloadFileAsync(remote, partial);
                        File.Move(partial, dest, overwrite: true);
                        took++;
                    }
                    catch
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                    }
                }
            }
        }
        catch { }
        return took;
    }

    /// <summary>
    /// 1:1 <c>Sync.exchangeCompanionCaches</c>: merges remote <c>&lt;id&gt;.companion.json</c> (local <c>companion/&lt;id&gt;.json</c>)
    /// and <c>&lt;id&gt;.recap.json</c> by JSON key without clobbering either device, then uploads local updates.
    /// </summary>
    private async Task<uint> ExchangeCompanionCachesAsync(
        ISyncFolder folder, Dictionary<string, string> remoteFiles, HashSet<string> leftOutNames)
    {
        if (leftOutNames.Contains("reading")) return 0;
        uint took = 0;
        try
        {
            string compDir = Path.Combine(_library.DataDir, "companion");
            Directory.CreateDirectory(compDir);
            var seen = _library.Prefs.SyncCompanionSeenTags;
            var sent = _library.Prefs.SyncCompanionSentStamps;

            static string? FileStamp(string path)
            {
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                return $"{fi.LastWriteTimeUtc.Ticks}-{fi.Length}";
            }

            static string? ToRemoteName(string localFile)
            {
                if (localFile.EndsWith(".recap.json", StringComparison.Ordinal))
                {
                    string id = localFile[..^".recap.json".Length];
                    return string.IsNullOrEmpty(id) ? null : $"{id}.recap.json";
                }
                if (localFile.EndsWith(".json", StringComparison.Ordinal))
                {
                    string id = localFile[..^".json".Length];
                    return string.IsNullOrEmpty(id) ? null : $"{id}.companion.json";
                }
                return null;
            }

            static string? ToLocalName(string remote)
            {
                if (remote.EndsWith(".recap.json", StringComparison.Ordinal))
                {
                    string id = remote[..^".recap.json".Length];
                    return string.IsNullOrEmpty(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..") ? null : $"{id}.recap.json";
                }
                if (remote.EndsWith(".companion.json", StringComparison.Ordinal))
                {
                    string id = remote[..^".companion.json".Length];
                    return string.IsNullOrEmpty(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..") ? null : $"{id}.json";
                }
                return null;
            }

            // Down: key-merge remote JSON object into local cache
            foreach (var (remote, tag) in remoteFiles)
            {
                if (seen.TryGetValue(remote, out var oldTag) && oldTag == tag) continue;
                string? local = ToLocalName(remote);
                if (local is null) continue;
                try
                {
                    byte[] remoteBytes = await folder.ReadAsync(remote);
                    string dest = Path.Combine(compDir, local);
                    string? localJson = File.Exists(dest) ? await File.ReadAllTextAsync(dest) : null;
                    string? mergedJson = MergeJsonObjectKeys(localJson, Encoding.UTF8.GetString(remoteBytes), out bool changed);
                    if (mergedJson is not null && (changed || !File.Exists(dest)))
                    {
                        string tmp = dest + ".part";
                        await File.WriteAllTextAsync(tmp, mergedJson);
                        File.Move(tmp, dest, overwrite: true);
                        took++;
                    }
                    seen[remote] = tag;
                }
                catch { }
            }

            // Up: upload local caches created or changed since last sync
            foreach (var path in Directory.EnumerateFiles(compDir, "*.json"))
            {
                string file = Path.GetFileName(path);
                if (file.StartsWith('.')) continue;
                string? remote = ToRemoteName(file);
                if (remote is null) continue;
                string? st = FileStamp(path);
                if (st is null) continue;
                if (sent.TryGetValue(remote, out var oldSt) && oldSt == st && remoteFiles.ContainsKey(remote))
                    continue;
                try
                {
                    byte[] data = await File.ReadAllBytesAsync(path);
                    if (data.Length > 0)
                    {
                        await folder.WriteAsync(remote, data);
                        sent[remote] = st;
                    }
                }
                catch { }
            }
        }
        catch { }
        return took;
    }

    internal static string? MergeJsonObjectKeys(string? localJson, string remoteJson, out bool added)
    {
        added = false;
        try
        {
            using var rDoc = JsonDocument.Parse(remoteJson);
            if (rDoc.RootElement.ValueKind != JsonValueKind.Object) return null;

            var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            JsonDocument? lDoc = null;
            if (!string.IsNullOrWhiteSpace(localJson))
            {
                try
                {
                    lDoc = JsonDocument.Parse(localJson);
                    if (lDoc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in lDoc.RootElement.EnumerateObject())
                            merged[prop.Name] = prop.Value.Clone();
                    }
                }
                catch { }
            }
            lDoc?.Dispose();

            foreach (var prop in rDoc.RootElement.EnumerateObject())
            {
                if (!merged.ContainsKey(prop.Name))
                {
                    merged[prop.Name] = prop.Value.Clone();
                    added = true;
                }
            }
            return JsonSerializer.Serialize(merged);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 1:1 <c>Sync.exchangeAudiobooks</c>: uploads <c>&lt;id&gt;.audio.&lt;part&gt;</c> (audio tracks, <c>cover.jpg</c>,
    /// <c>timings.json</c>) with <c>info.json</c> last, and downloads peers' completed recordings atomically.
    /// </summary>
    private async Task<bool> ExchangeAudiobooksAsync(ISyncFolder folder, Dictionary<string, string> remoteFiles)
    {
        bool gotAny = false;
        try
        {
            string audioRoot = Path.Combine(_library.DataDir, "audiobooks");
            Directory.CreateDirectory(audioRoot);
            var seen = _library.Prefs.SyncAudioSeenTags;
            var sent = _library.Prefs.SyncAudioSentStamps;

            static string? FileStamp(string path)
            {
                if (!File.Exists(path)) return null;
                var fi = new FileInfo(path);
                return $"{fi.LastWriteTimeUtc.Ticks}-{fi.Length}";
            }

            static List<string> ExtractParts(JsonElement infoObj)
            {
                var parts = new List<string>();
                if (infoObj.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in filesEl.EnumerateArray())
                    {
                        string? name = f.ValueKind == JsonValueKind.String
                            ? f.GetString()
                            : (f.TryGetProperty("file", out var fp) ? fp.GetString() : (f.TryGetProperty("name", out var np) ? np.GetString() : null));
                        if (!string.IsNullOrEmpty(name) && !name.Contains('/') && !name.Contains('\\'))
                            parts.Add(name);
                    }
                }
                parts.Add("cover.jpg");
                parts.Add("timings.json");
                return parts.Distinct(StringComparer.Ordinal).ToList();
            }

            // Up: local recordings under DataDir/audiobooks/<id>/
            foreach (var dir in Directory.EnumerateDirectories(audioRoot))
            {
                string id = Path.GetFileName(dir);
                if (string.IsNullOrEmpty(id) || id.StartsWith('.')) continue;
                string infoPath = Path.Combine(dir, "info.json");
                if (!File.Exists(infoPath)) continue;

                JsonElement infoRoot;
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(infoPath));
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    // Only sync aligned book recordings containing "book" string and "made" field (1:1 Sync.swift:548-549)
                    if (!doc.RootElement.TryGetProperty("book", out var bookProp) ||
                        bookProp.ValueKind != JsonValueKind.String ||
                        string.IsNullOrEmpty(bookProp.GetString()))
                        continue;
                    if (!doc.RootElement.TryGetProperty("made", out var madeProp) ||
                        madeProp.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        continue;
                    infoRoot = doc.RootElement.Clone();
                }
                catch { continue; }

                bool whole = true;
                var parts = ExtractParts(infoRoot);
                parts.Add("info.json"); // always last!
                foreach (string part in parts)
                {
                    if (part == "info.json" && !whole) break;
                    string localPath = Path.Combine(dir, part);
                    string? st = FileStamp(localPath);
                    if (st is null) continue;
                    string remoteName = $"{id}.audio.{part}";
                    if (remoteFiles.ContainsKey(remoteName) && sent.TryGetValue(remoteName, out var oldSt) && oldSt == st)
                        continue;
                    try
                    {
                        await folder.UploadFileAsync(remoteName, localPath);
                        sent[remoteName] = st;
                    }
                    catch { whole = false; }
                }
            }

            // Down: peer recordings whose <id>.audio.info.json is present
            foreach (var (name, tag) in remoteFiles)
            {
                if (!name.EndsWith(".audio.info.json", StringComparison.Ordinal)) continue;
                if (seen.TryGetValue(name, out var oldTag) && oldTag == tag) continue;

                string id = name[..^".audio.info.json".Length];
                if (string.IsNullOrEmpty(id) || id.Contains('/') || id.Contains('\\') || id.Contains("..")) continue;

                byte[] infoBytes;
                JsonElement infoRoot;
                try
                {
                    infoBytes = await folder.ReadAsync(name);
                    using var doc = JsonDocument.Parse(infoBytes);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    infoRoot = doc.RootElement.Clone();
                }
                catch { continue; }

                string here = Path.Combine(audioRoot, id);
                Directory.CreateDirectory(here);
                bool whole = true;
                foreach (string part in ExtractParts(infoRoot))
                {
                    string remote = $"{id}.audio.{part}";
                    if (!remoteFiles.TryGetValue(remote, out var remoteTag)) continue;
                    string dest = Path.Combine(here, part);
                    bool present = File.Exists(dest);
                    if (present && (part != "timings.json" || (seen.TryGetValue(remote, out var st) && st == remoteTag)))
                    {
                        if (!sent.ContainsKey(remote) && FileStamp(dest) is { } ls) sent[remote] = ls;
                        continue;
                    }
                    string partial = dest + ".download";
                    try
                    {
                        await folder.DownloadFileAsync(remote, partial);
                        File.Move(partial, dest, overwrite: true);
                        seen[remote] = remoteTag;
                        if (FileStamp(dest) is { } ns) sent[remote] = ns;
                    }
                    catch
                    {
                        try { if (File.Exists(partial)) File.Delete(partial); } catch { }
                        whole = false;
                    }
                }
                if (!whole) continue;

                string infoDest = Path.Combine(here, "info.json");
                byte[]? curInfo = File.Exists(infoDest) ? await File.ReadAllBytesAsync(infoDest) : null;
                if (curInfo is null || !curInfo.SequenceEqual(infoBytes))
                {
                    string tmp = infoDest + ".part";
                    await File.WriteAllBytesAsync(tmp, infoBytes);
                    File.Move(tmp, infoDest, overwrite: true);
                    gotAny = true;
                }
                if (FileStamp(infoDest) is { } ist) sent[name] = ist;
                seen[name] = tag;
            }
        }
        catch { }
        return gotAny;
    }

    private SyncPart[] BuildLeftOut()
    {
        var list = new List<SyncPart>();
        if (!_library.Prefs.SyncReading) list.Add(SyncPart.Reading);
        if (!_library.Prefs.SyncNotes) list.Add(SyncPart.Notes);
        if (!_library.Prefs.SyncSettings) list.Add(SyncPart.Settings);
        return list.ToArray();
    }

    private static string PartName(SyncPart p) => p switch
    {
        SyncPart.Reading => "reading",
        SyncPart.Notes => "notes",
        SyncPart.Settings => "settings",
        _ => "",
    };

    private ISyncFolder CreateFolder()
    {
        if (_library.Prefs.SyncMethod == "folder")
        {
            var dir = _library.Prefs.SyncLocalFolder;
            if (string.IsNullOrWhiteSpace(dir)) throw new SyncDiagnosticException("请先选择一个本地同步文件夹。");
            return new LocalSyncFolder(dir);
        }

        if (_library.Prefs.SyncMethod == "gdrive")
        {
            var clientId = EffectiveGoogleClientId;
            var secret = _library.Secrets.Get("sync.google.secret");
            var refresh = _library.Secrets.Get(GoogleRefreshSecretKey);
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(refresh))
                throw new SyncDiagnosticException("Google Drive 尚未登录或已过期，请重新登录。");
            return new GoogleDriveSyncFolder(clientId, secret, refresh);
        }

        var url = _library.Prefs.WebDavUrl;
        var user = _library.Prefs.WebDavUser;
        var pwd = _library.Secrets.Get("sync.webdav.password");
        var folder = string.IsNullOrWhiteSpace(_library.Prefs.WebDavFolder) ? "语阅" : _library.Prefs.WebDavFolder;
        return new WebDavClient(url, user, pwd, folder);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(cleaned) ? "book" : cleaned;
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static Dictionary<string, string> ParseQuery(string pathAndQuery)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        int idx = pathAndQuery.IndexOf('?');
        if (idx < 0 || idx + 1 >= pathAndQuery.Length) return map;
        foreach (var pair in pathAndQuery[(idx + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq > 0)
                map[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return map;
    }

    private interface ISyncFolder
    {
        Task PrepareAsync();
        Task<Dictionary<string, string>> ListAsync();
        Task<byte[]> ReadAsync(string name);
        Task WriteAsync(string name, byte[] data);
        Task UploadFileAsync(string name, string localPath);
        Task DownloadFileAsync(string name, string localPath);
    }

    private sealed class LocalSyncFolder(string dir) : ISyncFolder
    {
        public Task PrepareAsync()
        {
            Directory.CreateDirectory(dir);
            return Task.CompletedTask;
        }

        public Task<Dictionary<string, string>> ListAsync()
        {
            var map = new Dictionary<string, string>();
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(f);
                map[info.Name] = $"{info.LastWriteTimeUtc.Ticks}-{info.Length}";
            }
            return Task.FromResult(map);
        }

        public Task<byte[]> ReadAsync(string name) => File.ReadAllBytesAsync(Path.Combine(dir, name));

        public async Task WriteAsync(string name, byte[] data)
        {
            var target = Path.Combine(dir, name);
            var tmp = target + ".tmp";
            await File.WriteAllBytesAsync(tmp, data);
            File.Move(tmp, target, overwrite: true);
        }

        public Task UploadFileAsync(string name, string localPath)
        {
            var target = Path.Combine(dir, name);
            var tmp = target + ".tmp";
            File.Copy(localPath, tmp, overwrite: true);
            File.Move(tmp, target, overwrite: true);
            return Task.CompletedTask;
        }

        public Task DownloadFileAsync(string name, string localPath)
        {
            File.Copy(Path.Combine(dir, name), localPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    internal static string BuildWebDavFolderUrl(string rawUrl, string folder)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
            throw new SyncDiagnosticException("服务器地址格式不正确（需以 https:// 开头）。");

        var cleanBase = uri.ToString().TrimEnd('/');
        var segments = (folder ?? "")
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.EscapeDataString(s.Trim()))
            .Where(s => s.Length > 0)
            .ToArray();

        return segments.Length == 0
            ? cleanBase + "/"
            : $"{cleanBase}/{string.Join("/", segments)}/";
    }

    private sealed class WebDavClient : ISyncFolder, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _rootBaseUrl;
        private readonly string[] _folderSegments;
        private readonly string _baseDirUrl;

        public WebDavClient(string rawUrl, string user, string password, string folder)
        {
            if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri))
                throw new SyncDiagnosticException("服务器地址格式不正确（需以 https:// 开头）。");

            _rootBaseUrl = uri.ToString().TrimEnd('/');
            _folderSegments = (folder ?? "")
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
            _baseDirUrl = BuildWebDavFolderUrl(rawUrl!, folder ?? "");

            _http = new HttpClient { Timeout = System.TimeSpan.FromMinutes(4) };
            if (!string.IsNullOrEmpty(user))
            {
                var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
                _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
            }
        }

        public async Task PrepareAsync()
        {
            // Create nested WebDAV directories segment-by-segment (fixes "Books/Yuyue" escaping bug!)
            string cur = _rootBaseUrl;
            foreach (string seg in _folderSegments)
            {
                cur += "/" + Uri.EscapeDataString(seg);
                using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), cur + "/");
                using var res = await _http.SendAsync(req);
                int code = (int)res.StatusCode;
                if (code == 401)
                    throw new SyncDiagnosticException("账号或应用密码不正确（401）。坚果云请使用第三方应用密码。");
                if (code == 403)
                    throw new SyncDiagnosticException("没有此目录的访问权限（403）。");
                if (code == 409)
                    throw new SyncDiagnosticException($"无法创建上级目录（409）：{seg}。请检查路径是否存在。");
                if (code == 429)
                    throw new SyncDiagnosticException("请求过于频繁，被服务器限流（429），请稍后重试。");
                if (code == 404)
                    throw new SyncDiagnosticException("上级路径不存在（404），请检查 WebDAV 地址。");
                if (code == 507)
                    throw new SyncDiagnosticException("网盘剩余空间不足（507）。");
                if (code >= 500)
                    throw new SyncDiagnosticException($"WebDAV 服务器内部错误（{code}）。");
            }
        }

        public async Task<Dictionary<string, string>> ListAsync()
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseDirUrl);
            req.Headers.Add("Depth", "1");
            req.Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"utf-8\"?><d:propfind xmlns:d=\"DAV:\"><d:prop><d:getetag/><d:getlastmodified/><d:getcontentlength/></d:prop></d:propfind>",
                Encoding.UTF8, "application/xml");

            using var res = await _http.SendAsync(req);
            ThrowIfFailed(res);

            var xml = await res.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xml);
            var map = new Dictionary<string, string>();
            var baseUri = new Uri(_baseDirUrl);

            foreach (var resp in doc.Descendants().Where(e => e.Name.LocalName == "response"))
            {
                var href = resp.Elements().FirstOrDefault(e => e.Name.LocalName == "href")?.Value;
                if (string.IsNullOrEmpty(href)) continue;
                var full = Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs : new Uri(baseUri, href);
                var seg = Uri.UnescapeDataString(full.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "");
                if (string.IsNullOrEmpty(seg) || full.AbsolutePath.TrimEnd('/').Equals(baseUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    continue;

                var etag = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "getetag")?.Value ?? "";
                var mtime = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "getlastmodified")?.Value ?? "";
                var len = resp.Descendants().FirstOrDefault(e => e.Name.LocalName == "getcontentlength")?.Value ?? "";
                map[seg] = $"{etag}|{mtime}|{len}";
            }
            return map;
        }

        public async Task<byte[]> ReadAsync(string name)
        {
            using var res = await _http.GetAsync(_baseDirUrl + Uri.EscapeDataString(name));
            ThrowIfFailed(res);
            return await res.Content.ReadAsByteArrayAsync();
        }

        public async Task WriteAsync(string name, byte[] data)
        {
            using var content = new ByteArrayContent(data);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var res = await _http.PutAsync(_baseDirUrl + Uri.EscapeDataString(name), content);
            ThrowIfFailed(res);
        }

        public async Task UploadFileAsync(string name, string localPath)
        {
            await using var fs = File.OpenRead(localPath);
            using var content = new StreamContent(fs);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var res = await _http.PutAsync(_baseDirUrl + Uri.EscapeDataString(name), content);
            ThrowIfFailed(res);
        }

        public async Task DownloadFileAsync(string name, string localPath)
        {
            using var res = await _http.GetAsync(_baseDirUrl + Uri.EscapeDataString(name), HttpCompletionOption.ResponseHeadersRead);
            ThrowIfFailed(res);
            await using var fs = File.Create(localPath);
            await res.Content.CopyToAsync(fs);
        }

        private static void ThrowIfFailed(HttpResponseMessage res)
        {
            if (res.IsSuccessStatusCode || (int)res.StatusCode == 207) return;
            throw res.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new SyncDiagnosticException("账号或应用密码不正确（401）。坚果云请使用第三方应用密码。"),
                HttpStatusCode.Forbidden => new SyncDiagnosticException("没有权限读写该目录（403）。"),
                HttpStatusCode.NotFound => new SyncDiagnosticException("同步目录不存在（404）。"),
                HttpStatusCode.Conflict => new SyncDiagnosticException("目录路径冲突（409），请检查文件夹层级。"),
                (HttpStatusCode)429 => new SyncDiagnosticException("请求过于频繁（429），坚果云触发限流，请稍后再试。"),
                (HttpStatusCode)507 => new SyncDiagnosticException("网盘空间不足（507）。"),
                _ => new SyncDiagnosticException($"WebDAV 返回错误 {(int)res.StatusCode} ({res.ReasonPhrase})。"),
            };
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>
    /// 1:1 <c>GoogleDriveFolder</c> in <c>apple/Yuyue/App/GoogleDrive.swift</c>:
    /// syncs into Google Drive's hidden <c>appDataFolder</c> (shared with iOS/macOS/Android under the same Cloud project).
    /// </summary>
    private sealed class GoogleDriveSyncFolder(string clientId, string clientSecret, string refreshToken) : ISyncFolder, IDisposable
    {
        private readonly HttpClient _http = new() { Timeout = System.TimeSpan.FromMinutes(5) };
        private readonly Dictionary<string, string> _fileIds = new(StringComparer.Ordinal);
        private string? _accessToken;
        private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

        public async Task PrepareAsync()
        {
            _ = await GetAccessTokenAsync(forceRefresh: false);
        }

        private async Task<string> GetAccessTokenAsync(bool forceRefresh)
        {
            if (!forceRefresh && !string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken!;

            var form = new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["grant_type"] = "refresh_token",
            };
            if (!string.IsNullOrWhiteSpace(clientSecret))
                form["client_secret"] = clientSecret.Trim();

            using var res = await _http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
            var raw = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new SyncDiagnosticException("Google Drive 授权已过期，请在设置 · 同步里重新登录。");

            using var doc = JsonDocument.Parse(raw);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
            double expSec = doc.RootElement.TryGetProperty("expires_in", out var ep) ? ep.GetDouble() : 3500;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expSec - 60));
            return _accessToken;
        }

        public async Task<Dictionary<string, string>> ListAsync()
        {
            var outMap = new Dictionary<string, string>(StringComparer.Ordinal);
            _fileIds.Clear();
            string? pageToken = null;

            do
            {
                string url = "https://www.googleapis.com/drive/v3/files?spaces=appDataFolder" +
                    "&fields=nextPageToken,files(id,name,md5Checksum,modifiedTime)&pageSize=1000" +
                    (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");

                var bytes = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in files.EnumerateArray())
                    {
                        string id = f.GetProperty("id").GetString() ?? "";
                        string name = f.GetProperty("name").GetString() ?? "";
                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                        string tag = (f.TryGetProperty("md5Checksum", out var m) ? m.GetString() : null)
                            ?? (f.TryGetProperty("modifiedTime", out var mt) ? mt.GetString() : null)
                            ?? id;
                        _fileIds[name] = id;
                        outMap[name] = tag;
                    }
                }
                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var np) ? np.GetString() : null;
            } while (!string.IsNullOrEmpty(pageToken));

            return outMap;
        }

        public async Task<byte[]> ReadAsync(string name)
        {
            if (!_fileIds.ContainsKey(name)) await ListAsync();
            if (!_fileIds.TryGetValue(name, out var id))
                throw new SyncDiagnosticException($"Google Drive 中找不到文件 {name}");
            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media";
            return await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        }

        public async Task WriteAsync(string name, byte[] data)
        {
            if (_fileIds.TryGetValue(name, out var existingId))
            {
                string patchUrl = $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(existingId)}?uploadType=media";
                await SendAuthorizedAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
                    {
                        Content = new ByteArrayContent(data),
                    };
                    req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    return req;
                });
                return;
            }

            string boundary = "yuyue-" + Guid.NewGuid().ToString("N");
            byte[] meta = JsonSerializer.SerializeToUtf8Bytes(new { name, parents = new[] { "appDataFolder" } });
            using var ms = new MemoryStream();
            ms.Write(Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n"));
            ms.Write(meta);
            ms.Write(Encoding.UTF8.GetBytes($"\r\n--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\n"));
            ms.Write(data);
            ms.Write(Encoding.UTF8.GetBytes($"\r\n--{boundary}--\r\n"));
            byte[] payload = ms.ToArray();

            var resp = await SendAuthorizedAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart&fields=id")
                {
                    Content = new ByteArrayContent(payload),
                };
                req.Content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/related; boundary={boundary}");
                return req;
            });
            using var doc = JsonDocument.Parse(resp);
            if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetString() is string newId)
                _fileIds[name] = newId;
        }

        public async Task UploadFileAsync(string name, string localPath)
        {
            if (!_fileIds.ContainsKey(name)) await ListAsync();

            string? uploadLocation = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await GetAccessTokenAsync(attempt > 0);
                using var initReq = _fileIds.TryGetValue(name, out var existingId)
                    ? new HttpRequestMessage(HttpMethod.Patch, $"https://www.googleapis.com/upload/drive/v3/files/{Uri.EscapeDataString(existingId)}?uploadType=resumable")
                    : new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=resumable&fields=id")
                    {
                        Content = new StringContent(JsonSerializer.Serialize(new { name, parents = new[] { "appDataFolder" } }), Encoding.UTF8, "application/json"),
                    };
                initReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                initReq.Headers.TryAddWithoutValidation("X-Upload-Content-Type", "application/octet-stream");

                using var initRes = await _http.SendAsync(initReq);
                if (initRes.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!initRes.IsSuccessStatusCode)
                    throw new SyncDiagnosticException($"Google Drive 创建上传会话失败（{(int)initRes.StatusCode}）");
                uploadLocation = initRes.Headers.Location?.ToString();
                break;
            }

            if (string.IsNullOrEmpty(uploadLocation))
                throw new SyncDiagnosticException("Google Drive 未返回上传会话地址。");

            await using var fs = File.OpenRead(localPath);
            using var putReq = new HttpRequestMessage(HttpMethod.Put, uploadLocation)
            {
                Content = new StreamContent(fs),
            };
            putReq.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var putRes = await _http.SendAsync(putReq);
            var raw = await putRes.Content.ReadAsStringAsync();
            if (!putRes.IsSuccessStatusCode)
                throw new SyncDiagnosticException($"Google Drive 上传文件失败（{(int)putRes.StatusCode}）");
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (doc.RootElement.TryGetProperty("id", out var idProp) && idProp.GetString() is string newId)
                    _fileIds[name] = newId;
            }
            catch { }
        }

        public async Task DownloadFileAsync(string name, string localPath)
        {
            if (!_fileIds.ContainsKey(name)) await ListAsync();
            if (!_fileIds.TryGetValue(name, out var id))
                throw new SyncDiagnosticException($"Google Drive 中找不到文件 {name}");

            string url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(id)}?alt=media";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await GetAccessTokenAsync(attempt > 0);
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!res.IsSuccessStatusCode)
                    throw new SyncDiagnosticException($"Google Drive 下载文件失败（{(int)res.StatusCode}）");
                await using var fs = File.Create(localPath);
                await res.Content.CopyToAsync(fs);
                return;
            }
        }

        public async Task<string?> TryGetEmailAsync()
        {
            try
            {
                var bytes = await SendAuthorizedAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress)"));
                using var doc = JsonDocument.Parse(bytes);
                return doc.RootElement.GetProperty("user").GetProperty("emailAddress").GetString();
            }
            catch
            {
                return null;
            }
        }

        private async Task<byte[]> SendAuthorizedAsync(Func<HttpRequestMessage> buildReq)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string token = await GetAccessTokenAsync(attempt > 0);
                using var req = buildReq();
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var res = await _http.SendAsync(req);
                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) continue;
                if (!res.IsSuccessStatusCode)
                    throw new SyncDiagnosticException($"Google Drive 返回错误 {(int)res.StatusCode}");
                return await res.Content.ReadAsByteArrayAsync();
            }
            throw new SyncDiagnosticException("Google Drive 授权已失效，请重新登录。");
        }

        public void Dispose() => _http.Dispose();
    }

    private sealed class SyncDiagnosticException(string message) : Exception(message);
}
