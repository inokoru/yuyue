using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Yuyue.Audiobook;

namespace Yuyue.Core;

/// <summary>
/// Local LAN HTTP server serving <c>Assets/Shared/wifi-page.html</c> and its resumable
/// <c>/queue</c> + <c>/chunk</c> upload endpoints without requiring administrator URL ACLs.
/// </summary>
public sealed class WifiTransferService : IDisposable
{
    private sealed class UploadEntry
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required long Size { get; init; }
        public required string TempPath { get; init; }
        public long Received;
        public bool Imported;
    }

    private readonly LibraryService _library;
    private readonly ConcurrentDictionary<string, UploadEntry> _uploads = new();
    private readonly string _tempDir;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public string Url { get; private set; } = "";
    public event Action<string>? LogEvent;
    public event Action? BookImported;

    public WifiTransferService(LibraryService library)
    {
        _library = library;
        _tempDir = Path.Combine(library.DataDir, "cache", "wifi-upload");
        Directory.CreateDirectory(_tempDir);
    }

    public string Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        string ip = GetLanIpAddress();
        Url = $"http://{ip}:{port}";
        _ = AcceptLoopAsync(_listener, _cts.Token);
        return Url;
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch { break; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                await using var stream = client.GetStream();
                var (method, target, contentLen, bodyPrefix) = await ReadHttpHeaderAsync(stream, ct);
                if (string.IsNullOrEmpty(method)) return;

                var uri = new Uri("http://localhost" + (target.StartsWith('/') ? target : "/" + target));
                string path = uri.AbsolutePath;

                if (method == "GET" && path == "/")
                {
                    byte[] html = Encoding.UTF8.GetBytes(BuildRenderedWifiPage());
                    await WriteHttpResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, ct);
                    return;
                }

                byte[] body = await ReadBodyExactAsync(stream, contentLen, bodyPrefix, ct);

                if (method == "POST" && path == "/queue")
                {
                    using var doc = JsonDocument.Parse(body);
                    var have = new Dictionary<string, long>();
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        string id = el.GetProperty("id").GetString() ?? "";
                        string name = Path.GetFileName(el.GetProperty("name").GetString() ?? "book.epub");
                        long size = el.GetProperty("size").GetInt64();
                        if (string.IsNullOrEmpty(id)) continue;

                        var entry = _uploads.GetOrAdd(id, _ => new UploadEntry
                        {
                            Id = id,
                            Name = name,
                            Size = size,
                            TempPath = Path.Combine(_tempDir, $"{id}-{name}"),
                        });
                        if (File.Exists(entry.TempPath)) entry.Received = new FileInfo(entry.TempPath).Length;
                        have[id] = entry.Received;
                        LogEvent?.Invoke($"准备接收：{name}");
                    }
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(have);
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", json, ct);
                    return;
                }

                if (method == "PUT" && path == "/chunk")
                {
                    var query = ParseQuery(uri.Query);
                    string id = query.GetValueOrDefault("id", "");
                    long offset = long.TryParse(query.GetValueOrDefault("offset", "0"), out var o) ? o : 0;

                    if (!_uploads.TryGetValue(id, out var entry))
                    {
                        byte[] err = JsonSerializer.SerializeToUtf8Bytes(new { error = "未注册的文件队列" });
                        await WriteHttpResponseAsync(stream, "200 OK", "application/json", err, ct);
                        return;
                    }

                    await using (var fs = new FileStream(entry.TempPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    {
                        fs.Seek(offset, SeekOrigin.Begin);
                        await fs.WriteAsync(body, ct);
                        entry.Received = fs.Length;
                    }

                    bool done = entry.Received >= entry.Size;
                    string? importError = null;
                    if (done && !entry.Imported)
                    {
                        entry.Imported = true;
                        try
                        {
                            string ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                            BookSummary summary;
                            if (AudiobookImporter.Extensions.Contains(ext))
                                summary = await AudiobookImporter.ImportFilesAsync(_library, [entry.TempPath]);
                            else if (LibraryService.SupportedExtensions.Contains(ext))
                                summary = await _library.ImportBookWithExtrasAsync(entry.TempPath);
                            else
                                throw new InvalidOperationException($"不支持的文件格式：{ext}");

                            try { File.Delete(entry.TempPath); } catch { }
                            LogEvent?.Invoke($"✓ 已导入《{summary.Title}》");
                            BookImported?.Invoke();
                        }
                        catch (Exception ex)
                        {
                            importError = ex.Message;
                            LogEvent?.Invoke($"✕ 导入失败 ({entry.Name})：{ex.Message}");
                        }
                    }

                    byte[] resp = importError is not null
                        ? JsonSerializer.SerializeToUtf8Bytes(new { error = importError })
                        : JsonSerializer.SerializeToUtf8Bytes(new { received = entry.Received, done });
                    await WriteHttpResponseAsync(stream, "200 OK", "application/json", resp, ct);
                    return;
                }

                await WriteHttpResponseAsync(stream, "404 Not Found", "text/plain", "Not Found"u8.ToArray(), ct);
            }
            catch { }
        }
    }

    private static string BuildRenderedWifiPage()
    {
        string templatePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Shared", "wifi-page.html");
        string html = File.Exists(templatePath)
            ? File.ReadAllText(templatePath)
            : "<!doctype html><html><body><h1>语阅 · 局域网传书</h1></body></html>";

        return html
            .Replace("{{lang}}", "zh-Hans")
            .Replace("{{title}}", "语阅 · 局域网传书")
            .Replace("{{heading}}", "传书到语阅")
            .Replace("{{formats}}", "支持 EPUB、PDF、MOBI、AZW3、TXT 与 M4B / MP3 有声书")
            .Replace("{{drop}}", "点击选书，或把文件拖到这里")
            .Replace("{{many}}", "可一次选择多本书")
            .Replace("{{sending}}", "传输列表")
            .Replace("{{files}}", "\"共 %d 个文件\"")
            .Replace("{{done}}", "\"已加入书架\"")
            .Replace("{{queued}}", "\"等待中\"")
            .Replace("{{importing}}", "\"正在整理入库…\"")
            .Replace("{{gone}}", "连接中断，正在重试…")
            .Replace("{{awake}}", "传书期间请保持电脑端「局域网传书」窗口打开。");
    }

    private static async Task<(string Method, string Target, long ContentLength, byte[] BodyPrefix)> ReadHttpHeaderAsync(
        NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        byte[] buf = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0 && ms.Length < 65536)
        {
            int r = await stream.ReadAsync(buf, ct);
            if (r <= 0) return ("", "", 0, Array.Empty<byte>());
            ms.Write(buf, 0, r);
            headerEnd = FindDoubleCrlf(ms.GetBuffer(), (int)ms.Length);
        }
        if (headerEnd < 0) return ("", "", 0, Array.Empty<byte>());

        byte[] all = ms.ToArray();
        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return ("", "", 0, Array.Empty<byte>());

        string[] reqParts = lines[0].Split(' ');
        if (reqParts.Length < 2) return ("", "", 0, Array.Empty<byte>());

        long contentLen = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0 && lines[i][..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                long.TryParse(lines[i][(colon + 1)..].Trim(), out contentLen);
            }
        }

        int bodyStart = headerEnd + 4;
        byte[] prefix = bodyStart < all.Length ? all[bodyStart..] : Array.Empty<byte>();
        return (reqParts[0].ToUpperInvariant(), reqParts[1], contentLen, prefix);
    }

    private static int FindDoubleCrlf(byte[] data, int len)
    {
        for (int i = 0; i <= len - 4; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task<byte[]> ReadBodyExactAsync(NetworkStream stream, long contentLen, byte[] prefix, CancellationToken ct)
    {
        if (contentLen <= 0) return Array.Empty<byte>();
        byte[] result = new byte[contentLen];
        int copied = (int)Math.Min(prefix.Length, contentLen);
        Array.Copy(prefix, 0, result, 0, copied);
        int offset = copied;
        while (offset < contentLen)
        {
            int r = await stream.ReadAsync(result.AsMemory(offset, (int)(contentLen - offset)), ct);
            if (r <= 0) break;
            offset += r;
        }
        return result;
    }

    private static async Task WriteHttpResponseAsync(NetworkStream stream, string status, string contentType, byte[] body, CancellationToken ct)
    {
        string hdr = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        byte[] hdrBytes = Encoding.ASCII.GetBytes(hdr);
        await stream.WriteAsync(hdrBytes, ct);
        if (body.Length > 0) await stream.WriteAsync(body, ct);
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq > 0) dict[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return dict;
    }

    private static string GetLanIpAddress()
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        return ua.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose() => Stop();
}
