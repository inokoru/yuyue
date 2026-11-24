using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Windows.Networking.Connectivity;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 本机模型的统一多源自动下载（国内优先魔搭镜像，失败无感回退 GitHub Releases）、
/// HTTP Range 断点续传、30 秒单次读包停滞超时、本地 <c>file://</c> 源支持，以及非计费网络检测。
/// </summary>
public static class ModelDownload
{
    public const string GitHubReleaseBase = "https://github.com/inokoru/yuyue/releases/download/models-1/";
    public const string ModelScopeRepo = "nowtin/yuyue-models";
    public const string ModelScopeBase = $"https://modelscope.cn/models/{ModelScopeRepo}/resolve/master/";

    private static readonly System.TimeSpan RequestStallTimeout = System.TimeSpan.FromSeconds(30);

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    /// <summary>
    /// Returns <c>true</c> when Windows reports a metered / roaming / over-data-limit connection.
    /// </summary>
    public static bool IsMeteredNetwork()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return true;
            var cost = profile.GetConnectionCost();
            if (cost is null) return false;
            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                || cost.Roaming
                || cost.OverDataLimit;
        }
        catch
        {
            return false;
        }
    }

    public static bool PrefersChinaMirror()
    {
        try
        {
            if (string.Equals(RegionInfo.CurrentRegion.TwoLetterISORegionName, "CN", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }

        string tz = TimeZoneInfo.Local.Id;
        if (tz is "China Standard Time" or "Asia/Shanghai" or "Asia/Chongqing" or "Asia/Harbin" or "Asia/Urumqi")
            return true;

        string culture = CultureInfo.CurrentUICulture.Name;
        if (culture.StartsWith("zh-CN", StringComparison.OrdinalIgnoreCase) ||
            culture.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    public static List<string> CandidateUrls(string asset, string? officialUrl = null, string? mirrorPath = null)
    {
        string rel = (mirrorPath ?? asset).TrimStart('/');
        string official = officialUrl ?? (GitHubReleaseBase + asset);
        string mirror = ModelScopeBase + rel;
        var raw = PrefersChinaMirror() ? new[] { mirror, official } : new[] { official, mirror };

        var result = new List<string>();
        foreach (string u in raw)
        {
            if (!string.IsNullOrEmpty(u) && !result.Contains(u, StringComparer.Ordinal))
                result.Add(u);
        }
        return result;
    }

    /// <summary>
    /// 多源自动回退 + HTTP Range 断点续传；支持 <c>file://</c> 本地源，单包超过 30 秒无字节流入即切源；
    /// 取消或断网时保留 <paramref name="partFile"/>，仅当 BLAKE3 校验失败时删除损坏文件并切换下一个源重下。
    /// </summary>
    public static async Task DownloadWithResumeAsync(
        IReadOnlyList<string> urls,
        string partFile,
        long expectedBytes,
        string? expectedBlake3,
        Action<long, long> onBytes,
        CancellationToken ct)
    {
        if (urls.Count == 0) throw new InvalidOperationException("没有可用的下载地址");
        string? parent = Path.GetDirectoryName(partFile);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

        if (File.Exists(partFile))
        {
            long existing = new FileInfo(partFile).Length;
            if (expectedBytes > 0 && existing >= expectedBytes)
            {
                if (await MatchesDigestAsync(partFile, expectedBlake3, expectedBytes, ct))
                {
                    onBytes(existing, existing);
                    return;
                }
                File.Delete(partFile);
            }
        }

        Exception lastError = new HttpRequestException("无法连接下载服务器");
        foreach (string url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var fileUri) && fileUri.IsFile)
                {
                    await CopyLocalFileAsync(fileUri.LocalPath, partFile, onBytes, ct);
                }
                else
                {
                    await StreamOneSourceAsync(url, partFile, expectedBytes, expectedBlake3, onBytes, ct);
                }
                if (await MatchesDigestAsync(partFile, expectedBlake3, expectedBytes, ct))
                    return;
                if (File.Exists(partFile)) File.Delete(partFile);
                lastError = new InvalidOperationException("文件校验不一致");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }
        throw lastError;
    }

    private static async Task CopyLocalFileAsync(string srcPath, string partFile, Action<long, long> onBytes, CancellationToken ct)
    {
        if (!File.Exists(srcPath))
            throw new FileNotFoundException("找不到本地模型源文件", srcPath);
        long total = new FileInfo(srcPath).Length;
        await using var src = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
        await using var dst = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, useAsync: true);
        byte[] buf = new byte[65536];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            onBytes(done, Math.Max(total, done));
        }
    }

    private enum RangeFetchOutcome
    {
        Ok,
        RangeNotSatisfiable,
        MismatchedPartialRestart,
    }

    private static async Task StreamOneSourceAsync(
        string url,
        string partFile,
        long expectedBytes,
        string? expectedBlake3,
        Action<long, long> onBytes,
        CancellationToken ct)
    {
        long offset = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
        var outcome = await FetchRangeAsync(url, partFile, offset, expectedBytes, onBytes, ct);
        if (outcome == RangeFetchOutcome.RangeNotSatisfiable)
        {
            if (offset > 0 && await MatchesDigestAsync(partFile, expectedBlake3, expectedBytes, ct))
            {
                onBytes(offset, offset);
                return;
            }
            if (File.Exists(partFile)) File.Delete(partFile);
            var retry = await FetchRangeAsync(url, partFile, 0, expectedBytes, onBytes, ct);
            if (retry != RangeFetchOutcome.Ok)
                throw new HttpRequestException("下载服务器返回无效的分片响应");
        }
        else if (outcome == RangeFetchOutcome.MismatchedPartialRestart)
        {
            if (File.Exists(partFile)) File.Delete(partFile);
            var retry = await FetchRangeAsync(url, partFile, 0, expectedBytes, onBytes, ct);
            if (retry != RangeFetchOutcome.Ok)
                throw new HttpRequestException("下载服务器返回不匹配的 HTTP 206 Content-Range");
        }
    }

    private static async Task<RangeFetchOutcome> FetchRangeAsync(
        string url,
        string partFile,
        long offset,
        long expectedBytes,
        Action<long, long> onBytes,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset > 0)
            req.Headers.Range = new RangeHeaderValue(offset, null);
        else
            req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };

        using var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        headCts.CancelAfter(RequestStallTimeout);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headCts.Token);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return RangeFetchOutcome.RangeNotSatisfiable;
        resp.EnsureSuccessStatusCode();

        bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent;
        if (isPartial)
        {
            long? rangeStart = resp.Content.Headers.ContentRange?.From ?? TryParseContentRangeStart(resp);
            bool validResume = offset > 0
                && File.Exists(partFile)
                && new FileInfo(partFile).Length == offset
                && rangeStart == offset;
            if (!validResume)
            {
                try { if (File.Exists(partFile)) File.Delete(partFile); } catch { }
                return RangeFetchOutcome.MismatchedPartialRestart;
            }
        }

        long? contentLen = resp.Content.Headers.ContentLength;
        long total = isPartial
            ? (resp.Content.Headers.ContentRange?.Length ?? (contentLen is > 0 ? offset + contentLen.Value : Math.Max(expectedBytes, offset)))
            : (contentLen is > 0 ? contentLen.Value : expectedBytes);

        long done = isPartial ? offset : 0;
        if (done > 0) onBytes(done, Math.Max(total, done));

        await using var remote = await resp.Content.ReadAsStreamAsync(ct);
        await using var local = new FileStream(
            partFile,
            isPartial ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            65536,
            useAsync: true);

        byte[] buf = new byte[65536];
        long lastReported = done;
        while (true)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(RequestStallTimeout);
            int read = await remote.ReadAsync(buf, readCts.Token);
            if (read <= 0) break;

            await local.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (done - lastReported >= 262_144)
            {
                lastReported = done;
                onBytes(done, Math.Max(total, done));
            }
        }
        onBytes(done, Math.Max(total, done));
        return RangeFetchOutcome.Ok;
    }

    private static long? TryParseContentRangeStart(HttpResponseMessage resp)
    {
        if (!resp.Content.Headers.TryGetValues("Content-Range", out var vals))
            return null;
        foreach (string raw in vals)
        {
            string s = raw.Trim();
            if (!s.StartsWith("bytes", StringComparison.OrdinalIgnoreCase)) continue;
            string rest = s[5..].Trim();
            int dash = rest.IndexOf('-');
            if (dash > 0 && long.TryParse(rest[..dash].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long from))
                return from;
        }
        return null;
    }

    internal static async Task<bool> MatchesDigestAsync(string path, string? expectedBlake3, long expectedBytes = 0, CancellationToken ct = default)
    {
        if (!File.Exists(path)) return false;
        if (expectedBytes > 0 && new FileInfo(path).Length != expectedBytes) return false;
        if (string.IsNullOrEmpty(expectedBlake3)) return true;
        string actual = await Task.Run(() => YuyueCoreMethods.FileDigest(path), ct);
        return string.Equals(actual, expectedBlake3, StringComparison.OrdinalIgnoreCase);
    }
}
