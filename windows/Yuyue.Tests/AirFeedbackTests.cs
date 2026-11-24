using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;
using Yuyue.Core;

namespace Yuyue.Tests;

public sealed class AirFeedbackTests
{
    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int CallCount { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            if (request.Content is not null)
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _handler(request, cancellationToken);
        }
    }

    [Fact]
    public void CodePointLength_And_ClipMessage_HandleAsciiCjkAndEmoji()
    {
        Assert.Equal(0, AirFeedbackClient.CodePointLength(null));
        Assert.Equal(0, AirFeedbackClient.CodePointLength(""));
        Assert.Equal(5, AirFeedbackClient.CodePointLength("hello"));
        Assert.Equal(4, AirFeedbackClient.CodePointLength("语阅真棒"));

        // Astral emoji (😀, 📖) occupy 2 UTF-16 code units each, but 1 Unicode scalar value each
        string mixed = "a语😀b📖";
        Assert.Equal(7, mixed.Length);
        Assert.Equal(5, AirFeedbackClient.CodePointLength(mixed));

        Assert.Equal("", AirFeedbackClient.ClipMessage(null, 5));
        Assert.Equal("", AirFeedbackClient.ClipMessage(mixed, 0));
        Assert.Equal("a语", AirFeedbackClient.ClipMessage(mixed, 2));
        Assert.Equal("a语😀", AirFeedbackClient.ClipMessage(mixed, 3));
        Assert.Equal("a语😀b", AirFeedbackClient.ClipMessage(mixed, 4));
        Assert.Equal(mixed, AirFeedbackClient.ClipMessage(mixed, 5));
        Assert.Equal(mixed, AirFeedbackClient.ClipMessage(mixed, 100));

        // Verify clipping near MaxMessageLength with astral emoji does not split surrogate pairs
        string manyEmoji = string.Concat(Enumerable.Repeat("🎉", AirFeedbackClient.MaxMessageLength + 10));
        string clipped = AirFeedbackClient.ClipMessage(manyEmoji);
        Assert.Equal(AirFeedbackClient.MaxMessageLength, AirFeedbackClient.CodePointLength(clipped));
        Assert.True(clipped.EnumerateRunes().All(r => r.Value == 0x1F389));
    }

    [Fact]
    public void CanSubmit_AllowsTextOnlyAndImageOnly_RejectsEmptyAndSending()
    {
        Assert.False(AirFeedbackClient.CanSubmit(null, 0, sending: false));
        Assert.False(AirFeedbackClient.CanSubmit("", 0, sending: false));
        Assert.False(AirFeedbackClient.CanSubmit("   \r\n\t  ", 0, sending: false));

        Assert.True(AirFeedbackClient.CanSubmit("翻页有点卡", 0, sending: false));
        Assert.True(AirFeedbackClient.CanSubmit("", 1, sending: false));
        Assert.True(AirFeedbackClient.CanSubmit("   ", 2, sending: false));
        Assert.True(AirFeedbackClient.CanSubmit("附带截图", 4, sending: false));

        Assert.False(AirFeedbackClient.CanSubmit("翻页有点卡", 0, sending: true));
        Assert.False(AirFeedbackClient.CanSubmit("", 1, sending: true));
        Assert.False(AirFeedbackClient.CanSubmit("附带截图", 2, sending: true));
    }

    [Fact]
    public void BuildPayloadJson_WithDeviceInfo_IncludesAllFieldsAndCapsFourImages()
    {
        var info = new AirFeedbackClient.DeviceInfo(
            AppVersion: "0.1.3",
            AppBuild: "5",
            DeviceModel: "Windows (x64)",
            OsVersion: "Windows 11 (Build 26100)",
            Locale: "zh-CN");

        var images = new List<byte[]>
        {
            Array.Empty<byte>(), // empty entry should be skipped
            new byte[] { 0xFF, 0xD8, 0x01 },
            new byte[] { 0xFF, 0xD8, 0x02 },
            new byte[] { 0xFF, 0xD8, 0x03 },
            new byte[] { 0xFF, 0xD8, 0x04 },
            new byte[] { 0xFF, 0xD8, 0x05 }, // 5th valid image should be dropped
        };

        string json = AirFeedbackClient.BuildPayloadJson("  第 3 章排版换行有点偏  ", info, images);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("第 3 章排版换行有点偏", root.GetProperty("message").GetString());
        Assert.Equal("0.1.3", root.GetProperty("appVersion").GetString());
        Assert.Equal("5", root.GetProperty("appBuild").GetString());
        Assert.Equal("Windows (x64)", root.GetProperty("deviceModel").GetString());
        Assert.Equal("Windows 11 (Build 26100)", root.GetProperty("osVersion").GetString());
        Assert.Equal("zh-CN", root.GetProperty("locale").GetString());

        var imgArr = root.GetProperty("images");
        Assert.Equal(JsonValueKind.Array, imgArr.ValueKind);
        Assert.Equal(4, imgArr.GetArrayLength());

        for (int i = 0; i < 4; i++)
        {
            var entry = imgArr[i];
            Assert.Equal("image/jpeg", entry.GetProperty("mediaType").GetString());
            Assert.Equal(Convert.ToBase64String(new byte[] { 0xFF, 0xD8, (byte)(i + 1) }), entry.GetProperty("data").GetString());
        }
    }

    [Fact]
    public void BuildPayloadJson_WithoutDeviceInfo_SendsEmptyDeviceFields()
    {
        string json = AirFeedbackClient.BuildPayloadJson("仅发正文", deviceInfo: null, Array.Empty<byte[]>());
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("仅发正文", root.GetProperty("message").GetString());
        Assert.Equal("", root.GetProperty("appVersion").GetString());
        Assert.Equal("", root.GetProperty("appBuild").GetString());
        Assert.Equal("", root.GetProperty("deviceModel").GetString());
        Assert.Equal("", root.GetProperty("osVersion").GetString());
        Assert.Equal("", root.GetProperty("locale").GetString());
        Assert.Equal(0, root.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task SendAsync_SendsBearerHeaderAndJsonPayload_HandlesSuccess()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
        }));
        using var http = new HttpClient(handler);

        var info = new AirFeedbackClient.DeviceInfo("0.1.3", "5", "Windows (arm64)", "Windows 11 (Build 26100)", "en-US");
        var img = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };

        var res = await AirFeedbackClient.SendAsync(
            message: "Reading view looks great",
            deviceInfo: info,
            jpegImages: [img],
            httpClient: http,
            endpoint: "https://example.test/v1/ingest/feedback",
            appKey: "test-key-123");

        Assert.True(res.Success);
        Assert.Equal(201, res.StatusCode);
        Assert.Null(res.ErrorMessage);
        Assert.Equal(1, handler.CallCount);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest!.Method);
        Assert.Equal("https://example.test/v1/ingest/feedback", handler.LastRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key-123", handler.LastRequest.Headers.Authorization?.Parameter);
        Assert.Equal("application/json", handler.LastRequest.Content?.Headers.ContentType?.MediaType);

        Assert.NotNull(handler.LastRequestBody);
        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("Reading view looks great", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("Windows (arm64)", doc.RootElement.GetProperty("deviceModel").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("images").GetArrayLength());

        // Empty submission should short-circuit without making a network request
        var emptyRes = await AirFeedbackClient.SendAsync("   ", null, Array.Empty<byte[]>(), httpClient: http);
        Assert.False(emptyRes.Success);
        Assert.Equal(0, emptyRes.StatusCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SendAsync_ParsesServerReason_OnHttpError()
    {
        var handlerWithReason = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"reason":"Image payload exceeds limit"}""", Encoding.UTF8, "application/json"),
        }));
        using var http1 = new HttpClient(handlerWithReason);

        var res1 = await AirFeedbackClient.SendAsync(
            message: "test",
            deviceInfo: null,
            jpegImages: Array.Empty<byte[]>(),
            httpClient: http1,
            endpoint: "https://example.test/v1/ingest/feedback",
            appKey: "test-key");

        Assert.False(res1.Success);
        Assert.Equal(400, res1.StatusCode);
        Assert.Equal("Image payload exceeds limit", res1.ErrorMessage);

        // Non-2xx without JSON reason falls back to the localized retry hint
        var handlerNoReason = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("Service Unavailable", Encoding.UTF8, "text/plain"),
        }));
        using var http2 = new HttpClient(handlerNoReason);

        var res2 = await AirFeedbackClient.SendAsync(
            message: "test",
            deviceInfo: null,
            jpegImages: Array.Empty<byte[]>(),
            httpClient: http2,
            endpoint: "https://example.test/v1/ingest/feedback",
            appKey: "test-key");

        Assert.False(res2.Success);
        Assert.Equal(503, res2.StatusCode);
        Assert.Equal(AirFeedbackClient.OfflineRetryMessage, res2.ErrorMessage);
    }

    [Fact]
    public async Task SendAsync_ReturnsOfflineRetryMessage_OnNetworkException()
    {
        var netHandler = new StubHttpMessageHandler((_, _) => throw new HttpRequestException("No route to host"));
        using var httpNet = new HttpClient(netHandler);

        var resNet = await AirFeedbackClient.SendAsync(
            message: "offline test",
            deviceInfo: null,
            jpegImages: Array.Empty<byte[]>(),
            httpClient: httpNet,
            endpoint: "https://example.test/v1/ingest/feedback",
            appKey: "test-key");

        Assert.False(resNet.Success);
        Assert.Equal(0, resNet.StatusCode);
        Assert.Equal(AirFeedbackClient.OfflineRetryMessage, resNet.ErrorMessage);

        var timeoutHandler = new StubHttpMessageHandler((_, _) => throw new TaskCanceledException("Request timed out"));
        using var httpTimeout = new HttpClient(timeoutHandler);

        var resTimeout = await AirFeedbackClient.SendAsync(
            message: "timeout test",
            deviceInfo: null,
            jpegImages: Array.Empty<byte[]>(),
            httpClient: httpTimeout,
            endpoint: "https://example.test/v1/ingest/feedback",
            appKey: "test-key");

        Assert.False(resTimeout.Success);
        Assert.Equal(0, resTimeout.StatusCode);
        Assert.Equal(AirFeedbackClient.OfflineRetryMessage, resTimeout.ErrorMessage);
    }
}
