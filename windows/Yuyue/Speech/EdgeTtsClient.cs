using System.Globalization;
using System.Net.WebSockets;
using System.Text;

namespace Yuyue.Speech;

/// <summary>
/// 1:1 port of <c>apple/Yuyue/Speech/EdgeTTS.swift</c>:
/// Microsoft Edge Read Aloud WebSocket TTS client (keyless, v2 <c>Sec-MS-GEC</c> CS64 + salted SHA-256 token).
/// Streams 24 kHz / 48 kbps mono MP3 chunks until <c>Path:turn.end</c>.
/// </summary>
public static class EdgeTtsClient
{
    public const string TrustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4";
    public const string ChromiumFullVersion = "143.0.3650.75";
    public const string ChromiumMajorVersion = "143";
    public const string SecMsGecVersion = $"2-{ChromiumFullVersion}";
    public const string WssBaseUrl = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1";
    public const string OriginHeader = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
    public const string UserAgentHeader = $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{ChromiumMajorVersion}.0.0.0 Safari/537.36 Edg/{ChromiumMajorVersion}.0.0.0";

    private const long WinEpoch = 11_644_473_600L;
    private const long TicksPerSecond = 10_000_000L;
    private static readonly object SkewLock = new();
    private static double _clockSkewSeconds;

    public static async Task<byte[]> SynthesizeMp3Async(
        string text,
        string voiceId,
        string language,
        CancellationToken ct = default)
    {
        string cleaned = SanitizeText(text);
        if (string.IsNullOrEmpty(cleaned))
            throw new InvalidOperationException("朗读内容为空");

        string fullVoice = FormatVoiceName(voiceId);
        string xmlLang = ResolvedXmlLang(voiceId, language);
        var chunks = SplitChunks(cleaned, maxUtf8Bytes: 2800);

        var parts = new List<byte[]>(chunks.Count);
        foreach (string chunk in chunks)
        {
            ct.ThrowIfCancellationRequested();
            byte[] mp3 = await SynthesizeChunkWithRetryAsync(chunk, fullVoice, xmlLang, ct);
            parts.Add(mp3);
        }

        byte[] combined = CloudVoiceService.CombineMp3Chunks(parts);
        if (combined.Length == 0)
            throw new InvalidOperationException("微软 Edge 返回空音频数据");
        return combined;
    }

    public static Task<byte[]> SynthesizeAsync(
        string text,
        string voiceId,
        string language,
        CancellationToken ct = default) =>
        SynthesizeMp3Async(text, voiceId, language, ct);

    private static async Task<byte[]> SynthesizeChunkWithRetryAsync(
        string text,
        string fullVoice,
        string xmlLang,
        CancellationToken ct)
    {
        Exception? lastErr = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await SynthesizeSingleChunkAsync(text, fullVoice, xmlLang, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex) when (!ct.IsCancellationRequested && attempt == 0)
            {
                lastErr = new TimeoutException("微软 Edge 语音连接超时", ex);
                await TrySyncClockSkewAsync(ct);
                await Task.Delay(220, ct);
            }
            catch (WebSocketException ex) when (attempt == 0)
            {
                lastErr = ex;
                await TrySyncClockSkewAsync(ct);
                await Task.Delay(220, ct);
            }
            catch (Exception ex)
            {
                lastErr = ex;
                if (attempt == 0)
                {
                    await Task.Delay(220, ct);
                    continue;
                }
            }
        }
        throw lastErr ?? new InvalidOperationException("微软 Edge 语音连接失败");
    }

    internal static async Task<bool> TrySyncClockSkewAsync(CancellationToken ct = default)
    {
        string[] probeUrls =
        [
            "https://www.bing.com",
            "https://speech.platform.bing.com",
            "https://www.microsoft.com",
        ];

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (string url in probeUrls)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, url);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.Headers.Date is DateTimeOffset serverDt)
                {
                    double serverUnix = serverDt.ToUnixTimeMilliseconds() / 1000.0;
                    double nowUnix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    lock (SkewLock)
                    {
                        _clockSkewSeconds = serverUnix - nowUnix;
                    }
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch { }
        }
        return false;
    }

    private static async Task<byte[]> SynthesizeSingleChunkAsync(
        string text,
        string fullVoice,
        string xmlLang,
        CancellationToken ct)
    {
        string connId = RandomHex32();
        string gec = GenerateSecMsGecV2();
        var uri = new Uri($"{WssBaseUrl}?TrustedClientToken={TrustedClientToken}&ConnectionId={connId}&Sec-MS-GEC={gec}&Sec-MS-GEC-Version={SecMsGecVersion}");

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Pragma", "no-cache");
        ws.Options.SetRequestHeader("Cache-Control", "no-cache");
        ws.Options.SetRequestHeader("Origin", OriginHeader);
        ws.Options.SetRequestHeader("User-Agent", UserAgentHeader);
        ws.Options.SetRequestHeader("Accept-Language", "en-US,en;q=0.9");
        ws.Options.SetRequestHeader("Cookie", $"muid={RandomHex32().ToUpperInvariant()};");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var token = timeoutCts.Token;

        await ws.ConnectAsync(uri, token);

        string timestamp = JsDateString();
        string configPayload =
            $"X-Timestamp:{timestamp}\r\n" +
            "Content-Type:application/json; charset=utf-8\r\n" +
            "Path:speech.config\r\n\r\n" +
            "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"false\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";

        string escaped = EscapeXml(text);
        string ssml =
            $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{EscapeXml(xmlLang)}'>" +
            $"<voice name='{EscapeXml(fullVoice)}'>" +
            $"<prosody pitch='+0Hz' rate='+0%' volume='+0%'>{escaped}</prosody>" +
            "</voice></speak>";
        string ssmlPayload =
            $"X-RequestId:{RandomHex32()}\r\n" +
            "Content-Type:application/ssml+xml\r\n" +
            $"X-Timestamp:{timestamp}Z\r\n" +
            "Path:ssml\r\n\r\n" +
            ssml;

        await SendTextFrameAsync(ws, configPayload, token);
        await SendTextFrameAsync(ws, ssmlPayload, token);

        using var audioOut = new MemoryStream();
        using var frameBuf = new MemoryStream();
        byte[] recvBuf = new byte[16 * 1024];
        bool turnEnded = false;

        while (ws.State == WebSocketState.Open)
        {
            frameBuf.SetLength(0);
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(new ArraySegment<byte>(recvBuf), token);
                if (res.MessageType == WebSocketMessageType.Close)
                    break;
                if (res.Count > 0)
                    frameBuf.Write(recvBuf, 0, res.Count);
            }
            while (!res.EndOfMessage);

            if (res.MessageType == WebSocketMessageType.Close)
                break;

            byte[] frame = frameBuf.ToArray();
            if (res.MessageType == WebSocketMessageType.Text)
            {
                string txt = Encoding.UTF8.GetString(frame);
                if (txt.Contains("Path:turn.end", StringComparison.Ordinal))
                {
                    turnEnded = true;
                    break;
                }
            }
            else if (res.MessageType == WebSocketMessageType.Binary && frame.Length >= 2)
            {
                int headerLen = (frame[0] << 8) | frame[1];
                int bodyStart = 2 + headerLen;
                if (bodyStart <= frame.Length)
                {
                    string header = Encoding.UTF8.GetString(frame, 2, headerLen);
                    if (header.Contains("Path:audio", StringComparison.Ordinal) && bodyStart < frame.Length)
                    {
                        audioOut.Write(frame, bodyStart, frame.Length - bodyStart);
                    }
                }
            }
        }

        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        catch { }

        token.ThrowIfCancellationRequested();
        if (!turnEnded)
            throw new IOException("Edge TTS closed before Path:turn.end.");

        return audioOut.ToArray();
    }

    private static Task SendTextFrameAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        return ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    public static string FormatVoiceName(string voiceId)
    {
        string trimmed = voiceId.Trim();
        if (trimmed.StartsWith("Microsoft Server Speech Text to Speech Voice (", StringComparison.Ordinal))
            return trimmed;
        string[] parts = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return trimmed;
        string name = parts[^1];
        string locale = string.Join('-', parts[..^1]);
        return $"Microsoft Server Speech Text to Speech Voice ({locale}, {name})";
    }

    private static string ResolvedXmlLang(string voiceId, string fallback)
    {
        string[] parts = voiceId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0].Length >= 2 && parts[1].Length >= 2)
            return $"{parts[0]}-{parts[1]}";
        return string.IsNullOrWhiteSpace(fallback) ? "zh-CN" : fallback;
    }

    public static string SanitizeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (Rune r in text.EnumerateRunes())
        {
            int v = r.Value;
            if (v <= 8 || v == 11 || v == 12 || (v >= 14 && v <= 31))
                sb.Append(' ');
            else
                sb.Append(r.ToString());
        }
        return sb.ToString().Trim();
    }

    public static string EscapeXml(string text) => CloudVoiceService.EscapeXml(text);

    private static List<string> SplitChunks(string text, int maxUtf8Bytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maxUtf8Bytes)
            return [text];

        ReadOnlySpan<char> breaks = "。！？；，.!?;,\n ";
        var result = new List<string>();
        var cur = new StringBuilder();
        int curBytes = 0;

        foreach (Rune r in text.EnumerateRunes())
        {
            string s = r.ToString();
            int chBytes = Encoding.UTF8.GetByteCount(s);
            if (curBytes + chBytes > maxUtf8Bytes && cur.Length > 0)
            {
                string t = cur.ToString().Trim();
                if (!string.IsNullOrEmpty(t)) result.Add(t);
                cur.Clear();
                curBytes = 0;
            }
            cur.Append(s);
            curBytes += chBytes;
            if (curBytes >= maxUtf8Bytes * 2 / 3 && s.Length == 1 && breaks.Contains(s[0]))
            {
                string t = cur.ToString().Trim();
                if (!string.IsNullOrEmpty(t)) result.Add(t);
                cur.Clear();
                curBytes = 0;
            }
        }

        string rem = cur.ToString().Trim();
        if (!string.IsNullOrEmpty(rem)) result.Add(rem);
        return result.Count == 0 ? [text] : result;
    }

    private static string RandomHex32() => Guid.NewGuid().ToString("N");

    private static string JsDateString() =>
        DateTimeOffset.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", CultureInfo.InvariantCulture);

    // ---------- Sec-MS-GEC v2 Token (CS64 + Salted SHA-256, 1:1 EdgeTTS.swift) ----------

    public static string GenerateSecMsGecV2(double? unixTimestamp = null)
    {
        double skew;
        lock (SkewLock) skew = _clockSkewSeconds;
        double ts = unixTimestamp ?? (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 + skew);
        long secs = (long)Math.Floor(ts) + WinEpoch;
        long rounded = secs - ((secs % 300 + 300) % 300);
        long ticks = rounded * TicksPerSecond;
        byte[] payload = Encoding.UTF8.GetBytes($"{ticks}{TrustedClientToken}");
        byte[] digest = Sha256Digest(payload);
        var (c0, c1) = Cs64Digest(payload, digest);
        Span<byte> cs64Bytes = stackalloc byte[8];
        WriteLe32(c0, cs64Bytes, 0);
        WriteLe32(c1, cs64Bytes, 4);
        return $"{Convert.ToHexString(cs64Bytes)}-{Convert.ToHexString(digest)}";
    }

    private static readonly uint[] Sha256Initial =
    [
        0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
        0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u,
    ];

    private static readonly uint[] Sha256State =
    [
        0xFD9FBEA2u, 0x91243CE7u, 0xBE499094u, 0xCD2E1E0Au,
        0x6C006F49u, 0x04BD35ABu, 0xFEB0A7E4u, 0x03EFFBD2u,
    ];

    private static readonly uint[] Sha256Schedule =
    [
        0xEB24BF9Eu, 0x1558691Fu, 0xA0C09EE2u, 0x0CFA32BAu,
        0xA622EE8Cu, 0xD1DD9491u, 0xC0DD25A0u, 0xF9E70AB6u,
        0xF0C31573u, 0x7058CE4Eu, 0x7188E606u, 0x00E5AB33u,
        0xD258B060u, 0x72766564u,
    ];

    private static readonly uint[] Sha256RoundConstants =
    [
        0x428A2F98u, 0x71374491u, 0xB5C0FBCFu, 0xE9B5DBA5u, 0x3956C25Bu, 0x59F111F1u, 0x923F82A4u, 0xAB1C5ED5u,
        0xD807AA98u, 0x12835B01u, 0x243185BEu, 0x550C7DC3u, 0x72BE5D74u, 0x80DEB1FEu, 0x9BDC06A7u, 0xC19BF174u,
        0xE49B69C1u, 0xEFBE4786u, 0x0FC19DC6u, 0x240CA1CCu, 0x2DE92C6Fu, 0x4A7484AAu, 0x5CB0A9DCu, 0x76F988DAu,
        0x983E5152u, 0xA831C66Du, 0xB00327C8u, 0xBF597FC7u, 0xC6E00BF3u, 0xD5A79147u, 0x06CA6351u, 0x14292967u,
        0x27B70A85u, 0x2E1B2138u, 0x4D2C6DFCu, 0x53380D13u, 0x650A7354u, 0x766A0ABBu, 0x81C2C92Eu, 0x92722C85u,
        0xA2BFE8A1u, 0xA81A664Bu, 0xC24B8B70u, 0xC76C51A3u, 0xD192E819u, 0xD6990624u, 0xF40E3585u, 0x106AA070u,
        0x19A4C116u, 0x1E376C08u, 0x2748774Cu, 0x34B0BCB5u, 0x391C0CB3u, 0x4ED8AA4Au, 0x5B9CCA4Fu, 0x682E6FF3u,
        0x748F82EEu, 0x78A5636Fu, 0x84C87814u, 0x8CC70208u, 0x90BEFFFAu, 0xA4506CEBu, 0xBEF9A3F7u, 0xC67178F2u,
    ];

    private const uint Sha256Adjustment = 0xC9DC06A7u;
    private const uint Sha256Sigma0Adjustment = 0x059C0B80u;
    private const uint Sha256Sigma1Adjustment = 0x000B92C0u;

    private static readonly (uint C, uint Off)[] Cs64Coefficients =
    [
        (0xE82E91B1u, 0xB32700D0u), (0x44DE24C6u, 0x88DFF51Du),
        (0x3BEB65BCu, 0x29514038u), (0xC89C5933u, 0xB328CBDFu),
        (0x8D1EC211u, 0xD405B4D5u), (0x314EF5D5u, 0x26E55F3Cu),
        (0xE92491B1u, 0x607801C6u), (0xF0A5B9D5u, 0x82B2C1FBu),
        (0x06064CF3u, 0xFA51A3DAu), (0xDA79FA60u, 0x207CC464u),
        (0xBED20560u, 0x3C3C74F3u), (0x89F9C9F8u, 0xD2D14B61u),
        (0x35FA38C6u, 0xBB04A2C1u), (0xCB49E3C6u, 0xC5626167u),
    ];

    private const uint Cs64Factor = 0x82893933u;
    private const uint Cs64Adjustment0 = 0xF2F90B1Au;
    private const uint Cs64Adjustment1 = 0x0B1A0000u;

    private static uint Rol16(uint v) => (v << 16) | (v >> 16);
    private static uint Rotr32(uint v, int n) => (v >> n) | (v << (32 - n));
    private static uint Sigma0(uint v) => Rotr32(v, 7) ^ Rotr32(v, 18) ^ (v >> 3);
    private static uint Sigma1(uint v) => Rotr32(v, 17) ^ Rotr32(v, 19) ^ (v >> 10);

    private static uint[] Sha256Rounds(uint[] state, uint[] words, int firstRound = 0)
    {
        unchecked
        {
            uint a = state[0], b = state[1], c = state[2], d = state[3];
            uint e = state[4], f = state[5], g = state[6], h = state[7];
            for (int i = firstRound; i < 64; i++)
            {
                uint k = (firstRound == 14 && i == 14) ? Sha256Adjustment : Sha256RoundConstants[i];
                uint s1 = Rotr32(e, 6) ^ Rotr32(e, 11) ^ Rotr32(e, 25);
                uint ch = g ^ (e & (f ^ g));
                uint t1 = h + s1 + ch + k + words[i];
                uint s0 = Rotr32(a, 2) ^ Rotr32(a, 13) ^ Rotr32(a, 22);
                uint maj = (a & b) | (c & (a | b));
                uint t2 = s0 + maj;
                h = g; g = f; f = e; e = d + t1;
                d = c; c = b; b = a; a = t1 + t2;
            }
            return [a, b, c, d, e, f, g, h];
        }
    }

    private static void ExpandSchedule(uint[] words, int firstWord = 16)
    {
        unchecked
        {
            for (int i = firstWord; i < 64; i++)
                words[i] = Sigma1(words[i - 2]) + words[i - 7] + Sigma0(words[i - 15]) + words[i - 16];
        }
    }

    private static uint[] Sha256Init(ReadOnlySpan<byte> prefix)
    {
        unchecked
        {
            uint[] words = new uint[64];
            words[14] = ((uint)prefix[0] << 16) | ((uint)prefix[1] << 8) | prefix[2];
            words[15] = ((uint)prefix[3] << 24) | ((uint)prefix[4] << 16) | ((uint)prefix[5] << 8) | prefix[6];
            for (int i = 16; i < 30; i++)
            {
                uint value = Sha256Schedule[i - 16];
                if (i - 2 >= 14)
                {
                    uint s1 = Sigma1(words[i - 2]);
                    value += (i == 16) ? (s1 ^ Sha256Sigma1Adjustment) : s1;
                }
                if (i - 7 >= 14) value += words[i - 7];
                if (i - 15 >= 14)
                {
                    uint s0 = Sigma0(words[i - 15]);
                    value += (i == 29) ? (s0 ^ Sha256Sigma0Adjustment) : s0;
                }
                words[i] = value;
            }
            words[30] = Sigma1(words[28]) + words[23] + Sigma0(words[15]) + words[14] + Sha256Adjustment - Sha256RoundConstants[14];
            ExpandSchedule(words, 31);
            uint[] working = Sha256Rounds(Sha256State, words, 14);
            uint[] res = new uint[8];
            for (int i = 0; i < 8; i++) res[i] = Sha256Initial[i] + working[i];
            return res;
        }
    }

    private static byte[] Sha256Digest(byte[] data)
    {
        unchecked
        {
            int totalLen = 57 + data.Length;
            int tailLen = data.Length - 7;
            int padZeros = ((55 - totalLen) % 64 + 64) % 64;
            byte[] padded = new byte[tailLen + 1 + padZeros + 8];
            Array.Copy(data, 7, padded, 0, tailLen);
            padded[tailLen] = 0x80;
            ulong bitLen = (ulong)totalLen * 8UL;
            int bitPos = tailLen + 1 + padZeros;
            for (int b = 0; b < 8; b++)
                padded[bitPos + b] = (byte)((bitLen >> (56 - b * 8)) & 0xFFUL);

            uint[] state = Sha256Init(data.AsSpan(0, 7));
            uint[] words = new uint[64];
            for (int offset = 0; offset < padded.Length; offset += 64)
            {
                for (int j = 0; j < 16; j++)
                {
                    int p = offset + j * 4;
                    words[j] = ((uint)padded[p] << 24) | ((uint)padded[p + 1] << 16) | ((uint)padded[p + 2] << 8) | padded[p + 3];
                }
                ExpandSchedule(words, 16);
                uint[] working = Sha256Rounds(state, words, 0);
                for (int j = 0; j < 8; j++) state[j] += working[j];
            }

            byte[] outBytes = new byte[32];
            for (int j = 0; j < 8; j++)
            {
                uint v = state[j];
                outBytes[j * 4] = (byte)((v >> 24) & 0xFFu);
                outBytes[j * 4 + 1] = (byte)((v >> 16) & 0xFFu);
                outBytes[j * 4 + 2] = (byte)((v >> 8) & 0xFFu);
                outBytes[j * 4 + 3] = (byte)(v & 0xFFu);
            }
            return outBytes;
        }
    }

    private static List<uint> WordsLe4Even(byte[] data)
    {
        int rem = data.Length % 4;
        int paddedLen = rem == 0 ? data.Length : data.Length + (4 - rem);
        byte[] padded = new byte[paddedLen];
        Array.Copy(data, padded, data.Length);
        var words = new List<uint>(paddedLen / 4 + 1);
        for (int i = 0; i < paddedLen; i += 4)
        {
            uint w = padded[i] | ((uint)padded[i + 1] << 8) | ((uint)padded[i + 2] << 16) | ((uint)padded[i + 3] << 24);
            words.Add(w);
        }
        if (words.Count % 2 != 0) words.Add(0u);
        return words;
    }

    private static (uint C0, uint C1) Cs64Digest(byte[] data, byte[] digest)
    {
        unchecked
        {
            uint keyA = (digest[0] | ((uint)digest[1] << 8) | ((uint)digest[2] << 16) | ((uint)digest[3] << 24)) | 1u;
            uint keyB = (digest[4] | ((uint)digest[5] << 8) | ((uint)digest[6] << 16) | ((uint)digest[7] << 24)) | 1u;
            uint factor0 = keyA * Cs64Factor;
            uint factor1 = keyB * Cs64Factor;

            var terms = new List<uint>(Cs64Coefficients.Length + (data.Length + 8) / 4);
            var offsets = new List<uint>(Cs64Coefficients.Length + (data.Length + 8) / 4);

            for (int idx = 0; idx < Cs64Coefficients.Length; idx++)
            {
                var (coeff, off) = Cs64Coefficients[idx];
                terms.Add(coeff * (idx % 2 == 0 ? factor0 : factor1));
                offsets.Add(off);
            }

            byte[] prefixed = new byte[data.Length + 1];
            Array.Copy(data, 0, prefixed, 1, data.Length);
            var wList = WordsLe4Even(prefixed);
            for (int idx = 0; idx < wList.Count; idx++)
            {
                uint word = wList[idx];
                uint term = word * (idx % 2 == 0 ? keyA : keyB);
                uint offset = Rol16(word) * (idx % 2 == 0 ? 0xEF0569FBu : 0xC31713DBu);
                if (idx == 0)
                {
                    term += Cs64Adjustment0 * factor0;
                    offset += Cs64Adjustment1;
                }
                terms.Add(term);
                offsets.Add(offset);
            }

            uint swapAcc = 0, swapLast = 0, state = 0, acc = 0;
            for (int i = 0; i < terms.Count; i += 2)
            {
                uint x = terms[i];
                uint y = terms[i + 1];

                uint t = Rol16(offsets[i] + x);
                uint q = t * 0x76F50F34u;
                swapAcc += (Rol16(q) * 0xC3EFEA97u) + q;

                t = Rol16(offsets[i + 1] + y);
                q = t * 0x1881CE3Cu;
                swapLast = (Rol16(q) * 0x35BD1EC9u) + q;
                swapAcc += swapLast;

                state = (state * keyA) + x;
                state = Rol16(state) * 0xCF98B111u;
                state = Rol16(state) * 0x87085B9Fu;
                state = Rol16(state) * 0x12CEB96Du;
                state = Rol16(state) * 0x257E1D83u;
                acc += state;

                t = (state * keyB) + y;
                t = Rol16(t) * 0xA27416F5u;
                t = Rol16(t) * 0xD38396FFu;
                state = Rol16(Rol16(t) * 0x7C932B89u) * 0xBFA49F69u;
                acc += state;
            }
            return (swapLast ^ state, swapAcc ^ acc);
        }
    }

    private static void WriteLe32(uint v, Span<byte> buf, int idx)
    {
        buf[idx] = (byte)(v & 0xFFu);
        buf[idx + 1] = (byte)((v >> 8) & 0xFFu);
        buf[idx + 2] = (byte)((v >> 16) & 0xFFu);
        buf[idx + 3] = (byte)((v >> 24) & 0xFFu);
    }
}
