import Foundation

/// Microsoft Edge Read Aloud WebSocket TTS client (keyless, v2 `Sec-MS-GEC` token).
///
/// Protocol matches `rany2/edge-tts` v7.3.x:
/// - Handshake carries `TrustedClientToken`, `ConnectionId`, `Sec-MS-GEC` (`<16-hex CS64>-<64-hex SHA256>`),
///   `Sec-MS-GEC-Version`, Edge `User-Agent`, `Origin`, and random `muid` cookie.
/// - On HTTP 403 handshake failure with a `Date` response header, adjusts clock skew and retries once.
/// - Streams 24 kHz / 48 kbps mono MP3 chunks until `Path:turn.end`.
nonisolated enum EdgeTTS {
    static let trustedClientToken = "6A5AA1D4EAFF4E9FB37E23D68491D6F4"
    static let chromiumFullVersion = "143.0.3650.75"
    static let chromiumMajorVersion = "143"
    static let secMsGecVersion = "2-\(chromiumFullVersion)"
    static let wssBaseURL = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
    static let origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold"
    static let userAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/\(chromiumMajorVersion).0.0.0 Safari/537.36 Edg/\(chromiumMajorVersion).0.0.0"

    private static let winEpoch: Int64 = 11_644_473_600
    private static let ticksPerSecond: Int64 = 10_000_000
    private static let clockSkewSeconds = Mutex<Double>(0.0)

    enum EdgeError: Error, Sendable {
        case httpStatus(Int, Date?)
        case protocolError(String)
        case emptyAudio
    }

    // MARK: - Public Synthesis API

    static func synthesize(text: String, voiceID: String, language: String) async throws -> Data {
        let cleaned = sanitizeText(text)
        guard !cleaned.isEmpty else { throw EdgeError.emptyAudio }

        let fullVoice = formatVoiceName(voiceID)
        let xmlLang = resolvedXMLLang(voiceID: voiceID, fallback: language)
        let chunks = splitChunks(cleaned, maxUTF8Bytes: 2800)

        var combined = Data()
        for chunk in chunks {
            try Task.checkCancellation()
            let mp3 = try await synthesizeChunkWithRetry(
                text: chunk,
                fullVoice: fullVoice,
                xmlLang: xmlLang
            )
            combined.append(mp3)
        }
        guard !combined.isEmpty else { throw EdgeError.emptyAudio }
        return combined
    }

    private static func synthesizeChunkWithRetry(
        text: String,
        fullVoice: String,
        xmlLang: String
    ) async throws -> Data {
        var lastError: Error = EdgeError.emptyAudio
        for attempt in 0 ..< 2 {
            try Task.checkCancellation()
            do {
                return try await synthesizeSingleChunk(text: text, fullVoice: fullVoice, xmlLang: xmlLang)
            } catch is CancellationError {
                throw CancellationError()
            } catch let EdgeError.httpStatus(code, serverDate) {
                lastError = EdgeError.httpStatus(code, serverDate)
                if code == 403, let serverDate, attempt == 0 {
                    adjustClockSkew(serverDate: serverDate)
                    continue
                }
                if attempt == 0 {
                    try await Task.sleep(for: .milliseconds(250))
                    continue
                }
            } catch {
                lastError = error
                if attempt == 0 {
                    try await Task.sleep(for: .milliseconds(250))
                    continue
                }
            }
        }
        throw lastError
    }

    private static func synthesizeSingleChunk(
        text: String,
        fullVoice: String,
        xmlLang: String
    ) async throws -> Data {
        let connID = randomHex32()
        let gec = generateSecMsGecV2()
        let urlString = "\(wssBaseURL)?TrustedClientToken=\(trustedClientToken)&ConnectionId=\(connID)&Sec-MS-GEC=\(gec)&Sec-MS-GEC-Version=\(secMsGecVersion)"
        guard let url = URL(string: urlString) else {
            throw EdgeError.protocolError("Invalid Edge TTS URL")
        }

        var request = URLRequest(url: url, timeoutInterval: 20)
        request.httpShouldHandleCookies = false
        request.setValue("no-cache", forHTTPHeaderField: "Pragma")
        request.setValue("no-cache", forHTTPHeaderField: "Cache-Control")
        request.setValue(origin, forHTTPHeaderField: "Origin")
        request.setValue(userAgent, forHTTPHeaderField: "User-Agent")
        request.setValue("en-US,en;q=0.9", forHTTPHeaderField: "Accept-Language")
        request.setValue("muid=\(randomHex32().uppercased());", forHTTPHeaderField: "Cookie")

        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 20
        config.timeoutIntervalForResource = 35
        config.httpCookieStorage = nil
        let session = URLSession(configuration: config)
        let ws = session.webSocketTask(with: request)
        ws.maximumMessageSize = 16 * 1024 * 1024

        defer {
            ws.cancel(with: .normalClosure, reason: nil)
            session.invalidateAndCancel()
        }

        return try await withTaskCancellationHandler {
            ws.resume()

            let timestamp = jsDateString()
            let configPayload =
                "X-Timestamp:\(timestamp)\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                #"{"context":{"synthesis":{"audio":{"metadataoptions":{"sentenceBoundaryEnabled":"false","wordBoundaryEnabled":"false"},"outputFormat":"audio-24khz-48kbitrate-mono-mp3"}}}}"# +
                "\r\n"

            let escaped = escapeXML(text)
            let ssml =
                "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='\(xmlLang)'>" +
                "<voice name='\(fullVoice)'>" +
                "<prosody pitch='+0Hz' rate='+0%' volume='+0%'>\(escaped)</prosody>" +
                "</voice></speak>"
            let ssmlPayload =
                "X-RequestId:\(randomHex32())\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                "X-Timestamp:\(timestamp)Z\r\n" +
                "Path:ssml\r\n\r\n" +
                ssml

            do {
                try await ws.send(.string(configPayload))
                try await ws.send(.string(ssmlPayload))
            } catch {
                if let http = ws.response as? HTTPURLResponse, http.statusCode != 101 {
                    throw EdgeError.httpStatus(http.statusCode, parseHTTPDate(http.value(forHTTPHeaderField: "Date")))
                }
                throw error
            }

            return try await collectAudioFrames(
                receive: { try await ws.receive() },
                httpResponse: { ws.response as? HTTPURLResponse }
            )
        } onCancel: {
            ws.cancel(with: .goingAway, reason: nil)
            session.invalidateAndCancel()
        }
    }

    static func collectAudioFrames(
        receive: () async throws -> URLSessionWebSocketTask.Message,
        httpResponse: () -> HTTPURLResponse? = { nil }
    ) async throws -> Data {
        var audio = Data()
        var turnEnded = false
        while true {
            try Task.checkCancellation()
            let msg: URLSessionWebSocketTask.Message
            do {
                msg = try await receive()
            } catch {
                if Task.isCancelled { throw CancellationError() }
                if let http = httpResponse(), http.statusCode != 101 {
                    throw EdgeError.httpStatus(http.statusCode, parseHTTPDate(http.value(forHTTPHeaderField: "Date")))
                }
                throw error
            }

            switch msg {
            case .string(let textFrame):
                if textFrame.contains("Path:turn.end") {
                    turnEnded = true
                    break
                }
            case .data(let frame):
                guard frame.count >= 2 else { continue }
                let headerLen = (Int(frame[frame.startIndex]) << 8) | Int(frame[frame.startIndex + 1])
                let bodyStart = 2 + headerLen
                guard bodyStart <= frame.count else { continue }
                let headerBytes = frame.subdata(in: 2 ..< bodyStart)
                if let header = String(data: headerBytes, encoding: .utf8),
                   header.contains("Path:audio"),
                   bodyStart < frame.count {
                    audio.append(frame[bodyStart...])
                }
            @unknown default:
                break
            }
            if turnEnded { break }
        }
        try Task.checkCancellation()
        guard turnEnded && !audio.isEmpty else {
            throw turnEnded ? EdgeError.emptyAudio : EdgeError.protocolError("Stream ended before Path:turn.end")
        }
        return audio
    }

    // MARK: - Formatting & Sanitization

    /// Converts short voice IDs (`zh-CN-XiaoxiaoNeural`, `zh-CN-liaoning-XiaobeiNeural`)
    /// into Edge's canonical `Microsoft Server Speech Text to Speech Voice (locale, Name)` format.
    static func formatVoiceName(_ voiceID: String) -> String {
        let trimmed = voiceID.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("Microsoft Server Speech Text to Speech Voice (") {
            return trimmed
        }
        let parts = trimmed.split(separator: "-")
        guard parts.count >= 3 else { return trimmed }
        let name = String(parts.last!)
        let locale = parts.dropLast().joined(separator: "-")
        return "Microsoft Server Speech Text to Speech Voice (\(locale), \(name))"
    }

    private static func resolvedXMLLang(voiceID: String, fallback: String) -> String {
        let parts = voiceID.split(separator: "-")
        if parts.count >= 2, parts[0].count >= 2, parts[1].count >= 2 {
            return "\(parts[0])-\(parts[1])"
        }
        return fallback.isEmpty ? "zh-CN" : fallback
    }

    static func sanitizeText(_ text: String) -> String {
        var scalars = String.UnicodeScalarView()
        scalars.reserveCapacity(text.unicodeScalars.count)
        for s in text.unicodeScalars {
            let v = s.value
            if (v <= 8) || (v == 11) || (v == 12) || (v >= 14 && v <= 31) {
                scalars.append(" ")
            } else {
                scalars.append(s)
            }
        }
        return String(scalars).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    static func escapeXML(_ text: String) -> String {
        text
            .replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&apos;")
    }

    private static func splitChunks(_ text: String, maxUTF8Bytes: Int) -> [String] {
        guard text.utf8.count > maxUTF8Bytes else { return [text] }
        let breaks: Set<Character> = ["。", "！", "？", "；", "，", ".", "!", "?", ";", ",", "\n", " "]
        var out: [String] = []
        var cur = ""
        var curBytes = 0
        for ch in text {
            let chBytes = ch.utf8.count
            if curBytes + chBytes > maxUTF8Bytes && !cur.isEmpty {
                let t = cur.trimmingCharacters(in: .whitespacesAndNewlines)
                if !t.isEmpty { out.append(t) }
                cur = ""
                curBytes = 0
            }
            cur.append(ch)
            curBytes += chBytes
            if curBytes >= maxUTF8Bytes * 2 / 3 && breaks.contains(ch) {
                let t = cur.trimmingCharacters(in: .whitespacesAndNewlines)
                if !t.isEmpty { out.append(t) }
                cur = ""
                curBytes = 0
            }
        }
        let rem = cur.trimmingCharacters(in: .whitespacesAndNewlines)
        if !rem.isEmpty { out.append(rem) }
        return out.isEmpty ? [text] : out
    }

    private static func randomHex32() -> String {
        UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    private static func jsDateString() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0)
        f.dateFormat = "EEE MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'"
        return f.string(from: Date())
    }

    private static func parseHTTPDate(_ raw: String?) -> Date? {
        guard let raw, !raw.isEmpty else { return nil }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(secondsFromGMT: 0)
        for fmt in [
            "EEE, dd MMM yyyy HH:mm:ss zzz",
            "EEEE, dd-MMM-yy HH:mm:ss zzz",
            "EEE MMM d HH:mm:ss yyyy",
        ] {
            f.dateFormat = fmt
            if let d = f.date(from: raw) { return d }
        }
        return nil
    }

    private static func adjustClockSkew(serverDate: Date) {
        let now = Date().timeIntervalSince1970
        clockSkewSeconds.withLock { skew in
            let client = now + skew
            skew += serverDate.timeIntervalSince1970 - client
        }
    }

    // MARK: - Sec-MS-GEC v2 Token (CS64 + Salted SHA-256)

    /// Generates a v2 `Sec-MS-GEC` token (`"<16-HEX>-<64-HEX>"`), matching `rany2/edge-tts` `drm.py`.
    static func generateSecMsGecV2(unixTimestamp: Double? = nil) -> String {
        let ts: Double = if let unixTimestamp {
            unixTimestamp
        } else {
            Date().timeIntervalSince1970 + clockSkewSeconds.withLock { $0 }
        }
        let secs = Int64(floor(ts)) + winEpoch
        let rounded = secs - ((secs % 300 + 300) % 300)
        let ticks = rounded * ticksPerSecond
        let payload = Array("\(ticks)\(trustedClientToken)".utf8)
        let digest = sha256Digest(payload)
        let (c0, c1) = cs64Digest(payload, digest: digest)
        var cs64Bytes = [UInt8](repeating: 0, count: 8)
        writeLE32(c0, to: &cs64Bytes, at: 0)
        writeLE32(c1, to: &cs64Bytes, at: 4)
        return "\(hexUpper(cs64Bytes))-\(hexUpper(digest))"
    }

    private static let sha256Initial: [UInt32] = [
        0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
        0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
    ]
    private static let sha256State: [UInt32] = [
        0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A,
        0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2,
    ]
    private static let sha256Schedule: [UInt32] = [
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA,
        0xA622EE8C, 0xD1DD9491, 0xC0DD25A0, 0xF9E70AB6,
        0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33,
        0xD258B060, 0x72766564,
    ]
    private static let sha256RoundConstants: [UInt32] = [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ]
    private static let sha256Adjustment: UInt32 = 0xC9DC06A7
    private static let sha256Sigma0Adjustment: UInt32 = 0x059C0B80
    private static let sha256Sigma1Adjustment: UInt32 = 0x000B92C0

    private static let cs64Coefficients: [(UInt32, UInt32)] = [
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D),
        (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C),
        (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464),
        (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    ]
    private static let cs64Factor: UInt32 = 0x82893933
    private static let cs64Adjustment0: UInt32 = 0xF2F90B1A
    private static let cs64Adjustment1: UInt32 = 0x0B1A0000

    private static func rol16(_ v: UInt32) -> UInt32 { (v << 16) | (v >> 16) }
    private static func rotr32(_ v: UInt32, _ n: UInt32) -> UInt32 { (v >> n) | (v << (32 - n)) }
    private static func sigma0(_ v: UInt32) -> UInt32 { rotr32(v, 7) ^ rotr32(v, 18) ^ (v >> 3) }
    private static func sigma1(_ v: UInt32) -> UInt32 { rotr32(v, 17) ^ rotr32(v, 19) ^ (v >> 10) }

    private static func sha256Rounds(state: [UInt32], words: [UInt32], firstRound: Int = 0) -> [UInt32] {
        var a = state[0], b = state[1], c = state[2], d = state[3]
        var e = state[4], f = state[5], g = state[6], h = state[7]
        for i in firstRound ..< 64 {
            let k: UInt32 = (firstRound == 14 && i == 14) ? sha256Adjustment : sha256RoundConstants[i]
            let s1 = rotr32(e, 6) ^ rotr32(e, 11) ^ rotr32(e, 25)
            let ch = g ^ (e & (f ^ g))
            let t1 = h &+ s1 &+ ch &+ k &+ words[i]
            let s0 = rotr32(a, 2) ^ rotr32(a, 13) ^ rotr32(a, 22)
            let maj = (a & b) | (c & (a | b))
            let t2 = s0 &+ maj
            h = g; g = f; f = e; e = d &+ t1
            d = c; c = b; b = a; a = t1 &+ t2
        }
        return [a, b, c, d, e, f, g, h]
    }

    private static func expandSchedule(_ words: inout [UInt32], from firstWord: Int = 16) {
        for i in firstWord ..< 64 {
            let w = sigma1(words[i - 2]) &+ words[i - 7] &+ sigma0(words[i - 15]) &+ words[i - 16]
            words.append(w)
        }
    }

    private static func sha256Init(_ prefix: ArraySlice<UInt8>) -> [UInt32] {
        let base = prefix.startIndex
        var words = [UInt32](repeating: 0, count: 14)
        words.reserveCapacity(64)
        let w14 = (UInt32(prefix[base]) << 16) | (UInt32(prefix[base + 1]) << 8) | UInt32(prefix[base + 2])
        let w15 = (UInt32(prefix[base + 3]) << 24) | (UInt32(prefix[base + 4]) << 16) | (UInt32(prefix[base + 5]) << 8) | UInt32(prefix[base + 6])
        words.append(w14)
        words.append(w15)
        for i in 16 ..< 30 {
            var value = sha256Schedule[i - 16]
            if i - 2 >= 14 {
                let s1 = sigma1(words[i - 2])
                value = value &+ (i == 16 ? (s1 ^ sha256Sigma1Adjustment) : s1)
            }
            if i - 7 >= 14 {
                value = value &+ words[i - 7]
            }
            if i - 15 >= 14 {
                let s0 = sigma0(words[i - 15])
                value = value &+ (i == 29 ? (s0 ^ sha256Sigma0Adjustment) : s0)
            }
            words.append(value)
        }
        let w30 = sigma1(words[28]) &+ words[23] &+ sigma0(words[15]) &+ words[14] &+ sha256Adjustment &- sha256RoundConstants[14]
        words.append(w30)
        expandSchedule(&words, from: 31)
        let working = sha256Rounds(state: sha256State, words: words, firstRound: 14)
        return (0 ..< 8).map { sha256Initial[$0] &+ working[$0] }
    }

    private static func sha256Digest(_ data: [UInt8]) -> [UInt8] {
        precondition(data.count >= 7)
        let totalLen = 57 + data.count
        var padded = Array(data[7...])
        padded.append(0x80)
        let padZeros = ((55 - totalLen) % 64 + 64) % 64
        padded.append(contentsOf: repeatElement(UInt8(0), count: padZeros))
        let bitLen = UInt64(totalLen) * 8
        for shift in stride(from: 56, through: 0, by: -8) {
            padded.append(UInt8((bitLen >> shift) & 0xFF))
        }
        var state = sha256Init(data[0 ..< 7])
        var offset = 0
        while offset < padded.count {
            var words: [UInt32] = []
            words.reserveCapacity(64)
            for j in 0 ..< 16 {
                let p = offset + j * 4
                let w = (UInt32(padded[p]) << 24) | (UInt32(padded[p + 1]) << 16) | (UInt32(padded[p + 2]) << 8) | UInt32(padded[p + 3])
                words.append(w)
            }
            expandSchedule(&words)
            let working = sha256Rounds(state: state, words: words)
            for j in 0 ..< 8 { state[j] = state[j] &+ working[j] }
            offset += 64
        }
        var out = [UInt8](repeating: 0, count: 32)
        for j in 0 ..< 8 {
            let v = state[j]
            out[j * 4] = UInt8((v >> 24) & 0xFF)
            out[j * 4 + 1] = UInt8((v >> 16) & 0xFF)
            out[j * 4 + 2] = UInt8((v >> 8) & 0xFF)
            out[j * 4 + 3] = UInt8(v & 0xFF)
        }
        return out
    }

    private static func wordsLE4Even(_ data: [UInt8]) -> [UInt32] {
        var padded = data
        let rem = padded.count % 4
        if rem != 0 {
            padded.append(contentsOf: repeatElement(UInt8(0), count: 4 - rem))
        }
        if padded.isEmpty { return [] }
        var words: [UInt32] = []
        words.reserveCapacity(padded.count / 4 + 1)
        var i = 0
        while i < padded.count {
            let w = UInt32(padded[i])
                | (UInt32(padded[i + 1]) << 8)
                | (UInt32(padded[i + 2]) << 16)
                | (UInt32(padded[i + 3]) << 24)
            words.append(w)
            i += 4
        }
        if words.count % 2 != 0 { words.append(0) }
        return words
    }

    private static func cs64Digest(_ data: [UInt8], digest: [UInt8]) -> (UInt32, UInt32) {
        let keyA = (UInt32(digest[0]) | (UInt32(digest[1]) << 8) | (UInt32(digest[2]) << 16) | (UInt32(digest[3]) << 24)) | 1
        let keyB = (UInt32(digest[4]) | (UInt32(digest[5]) << 8) | (UInt32(digest[6]) << 16) | (UInt32(digest[7]) << 24)) | 1
        let factor0 = keyA &* cs64Factor
        let factor1 = keyB &* cs64Factor

        var terms: [UInt32] = []
        var offsets: [UInt32] = []
        terms.reserveCapacity(cs64Coefficients.count + (data.count + 8) / 4)
        offsets.reserveCapacity(cs64Coefficients.count + (data.count + 8) / 4)

        for (i, (coeff, off)) in cs64Coefficients.enumerated() {
            terms.append(coeff &* (i % 2 == 0 ? factor0 : factor1))
            offsets.append(off)
        }

        var prefixed = [UInt8](repeating: 0, count: data.count + 1)
        for i in 0 ..< data.count { prefixed[i + 1] = data[i] }

        for (i, word) in wordsLE4Even(prefixed).enumerated() {
            var term = word &* (i % 2 == 0 ? keyA : keyB)
            var offset = rol16(word) &* (i % 2 == 0 ? 0xEF0569FB : 0xC31713DB)
            if i == 0 {
                term = term &+ (cs64Adjustment0 &* factor0)
                offset = offset &+ cs64Adjustment1
            }
            terms.append(term)
            offsets.append(offset)
        }

        var swapAcc: UInt32 = 0
        var swapLast: UInt32 = 0
        var state: UInt32 = 0
        var acc: UInt32 = 0
        var i = 0
        while i < terms.count {
            let x = terms[i]
            let y = terms[i + 1]

            var t = rol16(offsets[i] &+ x)
            var q = t &* 0x76F50F34
            swapAcc = swapAcc &+ ((rol16(q) &* 0xC3EFEA97) &+ q)

            t = rol16(offsets[i + 1] &+ y)
            q = t &* 0x1881CE3C
            swapLast = (rol16(q) &* 0x35BD1EC9) &+ q
            swapAcc = swapAcc &+ swapLast

            state = (state &* keyA) &+ x
            state = rol16(state) &* 0xCF98B111
            state = rol16(state) &* 0x87085B9F
            state = rol16(state) &* 0x12CEB96D
            state = rol16(state) &* 0x257E1D83
            acc = acc &+ state

            t = (state &* keyB) &+ y
            t = rol16(t) &* 0xA27416F5
            t = rol16(t) &* 0xD38396FF
            state = rol16(rol16(t) &* 0x7C932B89) &* 0xBFA49F69
            acc = acc &+ state

            i += 2
        }
        return (swapLast ^ state, swapAcc ^ acc)
    }

    private static func writeLE32(_ v: UInt32, to buf: inout [UInt8], at idx: Int) {
        buf[idx] = UInt8(v & 0xFF)
        buf[idx + 1] = UInt8((v >> 8) & 0xFF)
        buf[idx + 2] = UInt8((v >> 16) & 0xFF)
        buf[idx + 3] = UInt8((v >> 24) & 0xFF)
    }

    private static func hexUpper(_ bytes: [UInt8]) -> String {
        let digits = Array("0123456789ABCDEF".utf8)
        var out = [UInt8](repeating: 0, count: bytes.count * 2)
        for (i, b) in bytes.enumerated() {
            out[i * 2] = digits[Int(b >> 4)]
            out[i * 2 + 1] = digits[Int(b & 0x0F)]
        }
        return String(decoding: out, as: UTF8.self)
    }
}
