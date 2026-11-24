using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Yuyue.Core;

/// <summary>
/// Pure .NET 8 client for single-channel AirFeedback submission (<c>POST /v1/ingest/feedback</c>).
/// Intentionally has no WinUI XAML dependencies so it can be unit-tested headlessly in <c>Yuyue.Tests</c>.
/// </summary>
public static class AirFeedbackClient
{
    public const string Endpoint = "https://apps.air.build/v1/ingest/feedback";
    public const string AppKey = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg";
    public const int MaxMessageLength = 4000;
    public const int MaxImages = 4;
    public const uint MaxImageLongSide = 2048;
    public const int MaxImageBytes = 8_000_000;

    public const string OfflineRetryMessage = "当前没有网络或服务暂时不可用，内容和图片已保存在草稿里，稍后再点发送即可。";
    public const string EmptySubmissionMessage = "请先写点内容或添加图片。";

    public sealed record DeviceInfo(
        string AppVersion,
        string AppBuild,
        string DeviceModel,
        string OsVersion,
        string Locale);

    public sealed record SendResult(
        bool Success,
        int StatusCode,
        string? ErrorMessage);

    private static readonly HttpClient DefaultHttp = CreateDefaultHttpClient();

    private static HttpClient CreateDefaultHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Yuyue-Windows");
        return client;
    }

    /// <summary>
    /// Counts Unicode scalar values (<see cref="Rune"/>) in <paramref name="text"/>.
    /// </summary>
    public static int CodePointLength(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int count = 0;
        foreach (var _ in text.EnumerateRunes())
            count++;
        return count;
    }

    /// <summary>
    /// Clips <paramref name="text"/> to at most <paramref name="max"/> Unicode scalar values without splitting surrogate pairs.
    /// </summary>
    public static string ClipMessage(string? text, int max = MaxMessageLength)
    {
        if (string.IsNullOrEmpty(text) || max <= 0) return "";
        int count = 0;
        int utf16Index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count >= max)
                return text[..Math.Min(utf16Index, text.Length)];
            count++;
            utf16Index += rune.Utf16SequenceLength;
        }
        return text;
    }

    /// <summary>
    /// Returns true when the form can be submitted: not currently sending, and has non-whitespace text or at least one image.
    /// </summary>
    public static bool CanSubmit(string? text, int imageCount, bool sending) =>
        !sending && (!string.IsNullOrWhiteSpace(text) || imageCount > 0);

    /// <summary>
    /// Builds the AirFeedback JSON payload with message, optional device info fields, and up to <see cref="MaxImages"/> JPEG images.
    /// </summary>
    public static string BuildPayloadJson(string? message, DeviceInfo? deviceInfo, IReadOnlyList<byte[]> jpegImages)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("message", ClipMessage((message ?? "").Trim()));
            writer.WriteString("appVersion", deviceInfo?.AppVersion ?? "");
            writer.WriteString("appBuild", deviceInfo?.AppBuild ?? "");
            writer.WriteString("deviceModel", deviceInfo?.DeviceModel ?? "");
            writer.WriteString("osVersion", deviceInfo?.OsVersion ?? "");
            writer.WriteString("locale", deviceInfo?.Locale ?? "");

            writer.WriteStartArray("images");
            if (jpegImages is not null)
            {
                int written = 0;
                foreach (var bytes in jpegImages)
                {
                    if (bytes is not { Length: > 0 }) continue;
                    if (written >= MaxImages) break;
                    writer.WriteStartObject();
                    writer.WriteString("mediaType", "image/jpeg");
                    writer.WriteString("data", Convert.ToBase64String(bytes));
                    writer.WriteEndObject();
                    written++;
                }
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Sends feedback via HTTP POST to AirFeedback using Bearer authentication.
    /// </summary>
    public static async Task<SendResult> SendAsync(
        string? message,
        DeviceInfo? deviceInfo,
        IReadOnlyList<byte[]> jpegImages,
        HttpClient? httpClient = null,
        string endpoint = Endpoint,
        string appKey = AppKey,
        CancellationToken cancellationToken = default)
    {
        int validImages = 0;
        if (jpegImages is not null)
        {
            foreach (var bytes in jpegImages)
            {
                if (bytes is { Length: > 0 }) validImages++;
            }
        }

        if (!CanSubmit(message, validImages, sending: false))
            return new SendResult(false, 0, EmptySubmissionMessage);

        string payload = BuildPayloadJson(message, deviceInfo, jpegImages ?? Array.Empty<byte[]>());
        var client = httpClient ?? DefaultHttp;
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appKey);

        try
        {
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            int code = (int)resp.StatusCode;
            if (resp.IsSuccessStatusCode)
                return new SendResult(true, code, null);

            string? reason = null;
            try
            {
                string body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                reason = TryParseErrorReason(body);
            }
            catch { }

            return new SendResult(false, code, string.IsNullOrWhiteSpace(reason) ? OfflineRetryMessage : reason);
        }
        catch (HttpRequestException)
        {
            return new SendResult(false, 0, OfflineRetryMessage);
        }
        catch (TaskCanceledException)
        {
            return new SendResult(false, 0, OfflineRetryMessage);
        }
    }

    private static string? TryParseErrorReason(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("reason", out var reasonProp) &&
                reasonProp.ValueKind == JsonValueKind.String)
            {
                string? reason = reasonProp.GetString()?.Trim();
                if (!string.IsNullOrEmpty(reason)) return reason;
            }
        }
        catch { }
        return null;
    }
}
