using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Yuyue.Core;

namespace Yuyue.Speech;

/// <summary>
/// 本机模型的统一多源自动下载（国内优先魔搭镜像，失败无感回退 GitHub Releases）与 HTTP Range 断点续传。
/// </summary>
public static class ModelDownload
{
    public const string GitHubReleaseBase = "https://github.com/inokoru/yuyue/releases/download/models-1/";
    public const string ModelScopeRepo = "nowtin/yuyue-models";
    public const string ModelScopeBase = $"https://modelscope.cn/models/{ModelScopeRepo}/resolve/master/";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
    })
    {
        Timeout = System.TimeSpan.FromMinutes(30),
    };

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
    /// 多源自动回退 + HTTP Range 断点续传；取消或断网时保留 <paramref name="partFile"/>，
    /// 仅当 BLAKE3 校验失败时删除损坏文件并切换下一个源重下。
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
                bool hasDigest = !string.IsNullOrEmpty(expectedBlake3);
                bool valid = hasDigest
                    ? await MatchesDigestAsync(partFile, expectedBlake3, ct)
                    : (existing == expectedBytes);
                if (valid)
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
                await StreamOneSourceAsync(url, partFile, expectedBytes, expectedBlake3, onBytes, ct);
                if (await MatchesDigestAsync(partFile, expectedBlake3, ct))
                    return;
                if (File.Exists(partFile)) File.Delete(partFile);
                lastError = new InvalidOperationException("文件校验不一致");
            }
            catch (OperationCanceledException)
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

    private static async Task StreamOneSourceAsync(
        string url,
        string partFile,
        long expectedBytes,
        string? expectedBlake3,
        Action<long, long> onBytes,
        CancellationToken ct)
    {
        long offset = File.Exists(partFile) ? new FileInfo(partFile).Length : 0;
        var status = await FetchRangeAsync(url, partFile, offset, expectedBytes, onBytes, ct);
        if (status == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            bool hasDigest = !string.IsNullOrEmpty(expectedBlake3);
            bool looksComplete = hasDigest
                ? await MatchesDigestAsync(partFile, expectedBlake3, ct)
                : (expectedBytes > 0 && offset == expectedBytes);
            if (offset > 0 && looksComplete)
            {
                onBytes(offset, offset);
                return;
            }
            if (File.Exists(partFile)) File.Delete(partFile);
            var retry = await FetchRangeAsync(url, partFile, 0, expectedBytes, onBytes, ct);
            if ((int)retry < 200 || (int)retry >= 300)
                throw new HttpRequestException($"HTTP {(int)retry}");
        }
    }

    private static async Task<HttpStatusCode> FetchRangeAsync(
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

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            return resp.StatusCode;
        resp.EnsureSuccessStatusCode();

        bool isPartial = resp.StatusCode == HttpStatusCode.PartialContent && offset > 0;
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
        int read;
        while ((read = await remote.ReadAsync(buf, ct)) > 0)
        {
            await local.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (done - lastReported >= 262_144)
            {
                lastReported = done;
                onBytes(done, Math.Max(total, done));
            }
        }
        onBytes(done, Math.Max(total, done));
        return resp.StatusCode;
    }

    private static async Task<bool> MatchesDigestAsync(string path, string? expectedBlake3, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(expectedBlake3)) return true;
        if (!File.Exists(path)) return false;
        string actual = await Task.Run(() => YuyueCoreMethods.FileDigest(path), ct);
        return string.Equals(actual, expectedBlake3, StringComparison.OrdinalIgnoreCase);
    }
}
