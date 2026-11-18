import AVFoundation
import CryptoKit
import Foundation
import YuyueCore

/// 用户自带 Key 的云端语音: OpenAI, Microsoft (Azure Speech) and ElevenLabs, each with the
/// reader's own key from the keychain. Every sentence is kept once rendered, by voice and
/// text, so a chapter heard (or fetched ahead, see `prefetch`) plays again offline and costs
/// nothing the second time.
nonisolated final class CloudVoice: Voice, @unchecked Sendable {
    enum Provider: String, CaseIterable, Identifiable, Sendable {
        case openai, azure, elevenlabs
        var id: String { rawValue }

        var title: String {
            switch self {
            case .openai: "OpenAI"
            case .azure: String(localized: "微软 Azure")
            case .elevenlabs: "ElevenLabs"
            }
        }

        var keyName: String { "tts.\(rawValue).key" }
        /// Azure's region, such as eastasia.
        static let regionName = "tts.azure.region"
        var voicesName: String { "tts.\(rawValue).voices" }

        var key: String? { Keychain.read(keyName).flatMap { $0.isEmpty ? nil : $0 } }
        var isSetUp: Bool { key != nil && (self != .azure || !(UserDefaults.standard.string(forKey: Self.regionName) ?? "").isEmpty) }

        var keyHint: String {
            switch self {
            case .openai: "sk-…"
            case .azure: String(localized: "资源的密钥 1")
            case .elevenlabs: "sk_…"
            }
        }

        var help: String {
            switch self {
            case .openai: String(localized: "platform.openai.com › API keys 里建一个。费用按字数算，走你的账号。")
            case .azure: String(localized: "Azure 门户里建一个“语音”资源，拷它的密钥和区域（比如 eastasia）。有免费额度。")
            case .elevenlabs: String(localized: "elevenlabs.io › 个人资料 › API Keys。额度按字数算。")
            }
        }
    }

    let id: String
    let name: String
    let provider: Provider
    /// The voice's own language (Azure), or nil for one that reads every language.
    let language: String?
    /// The provider's id for it: OpenAI's voice name, Azure's ShortName, ElevenLabs' voice_id.
    let voiceID: String

    /// "male" / "female" and "young" / "middle_aged" / "old", where the provider says (广播剧's
    /// casting goes by them).
    let gender: String?
    let age: String?

    init(provider: Provider, voiceID: String, name: String, language: String?, gender: String? = nil, age: String? = nil) {
        self.provider = provider
        self.voiceID = voiceID
        self.name = name
        self.language = language
        self.gender = gender ?? (provider == .openai ? Self.openAIGender[voiceID] : nil)
        self.age = age
        id = "cloud.\(provider.rawValue).\(voiceID)"
    }

    /// 带语气: how a line of a play is said, in each provider's own terms — OpenAI by an
    /// instruction, ElevenLabs by tags around the text (its v3 model), Azure by a speaking style
    /// where the voice has one.
    struct Acting: Sendable, Hashable {
        var instruction: String
        var head: String
        var tail: String
        var style: String?
        var degree: Double

        var isFlat: Bool { instruction.isEmpty && head.isEmpty && tail.isEmpty && style == nil }
        var key: String { "\(instruction)|\(head)|\(tail)|\(style ?? "")|\(degree)" }
    }

    /// A line of a play: in pieces (the words the book gave a reading for apart, which Azure is
    /// told how to say), and acted.
    func render(parts: [SpokenPart], language: String, acting: Acting?) async throws -> Speech? {
        let text = parts.map(\.text).joined()
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else { return nil }
        let readings = provider == .azure ? parts.compactMap { p in p.reading.map { "\(p.text)=\($0)" } }.joined(separator: ",") : ""
        let acted = acting.flatMap { $0.isFlat ? nil : $0 }
        // A line as plain speech is the same sentence the reader hears: one cache for both.
        let file = acted == nil && readings.isEmpty ? cacheFile(text) : cacheFile(text + "\u{2}" + (acted?.key ?? "") + "\u{2}" + readings)
        if let data = try? Data(contentsOf: file) {
            return try Self.decode(data)
        }
        let data = try await fetch(text, language: language, parts: parts, acting: acted)
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        return try Self.decode(data)
    }

    var qualityName: String? { provider.title }
    var isOffline: Bool { false }

    func speaks(_ tag: String) -> Bool {
        language.map { SpokenLanguage.matches($0, tag) } ?? true
    }

    func render(_ text: String, language: String) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else { return nil }
        let file = cacheFile(text)
        if let data = try? Data(contentsOf: file) {
            return try Self.decode(data)
        }
        let data = try await fetch(text, language: language)
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        return try Self.decode(data)
    }

    /// Renders the sentences not kept yet, two at a time, so the chapter plays on without the
    /// network. Stops at the first failure; the player reports its own.
    @concurrent func prefetch(_ texts: [String], language: String) async {
        let missing = texts.filter { t in
            t.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) && !FileManager.default.fileExists(atPath: cacheFile(t).path)
        }
        var i = 0
        while i < missing.count, !Task.isCancelled {
            let pair = missing[i ..< min(i + 2, missing.count)]
            let ok = await withTaskGroup(of: Bool.self) { group in
                for t in pair { group.addTask { (try? await self.render(t, language: language)) != nil } }
                return await group.reduce(true) { $0 && $1 }
            }
            if !ok { return }
            i += 2
        }
    }

    // MARK: Cache

    static var cacheDir: URL {
        FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0].appending(path: "yuyue-voices", directoryHint: .isDirectory)
    }

    private func cacheFile(_ text: String) -> URL {
        let hash = SHA256.hash(data: Data((id + "\u{1}" + text).utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
        return Self.cacheDir.appending(path: provider.rawValue).appending(path: "\(hash).wav")
    }

    // MARK: Fetching

    enum Failure: LocalizedError {
        case noKey(Provider)
        case badKey(Provider)
        case noCredit(Provider)
        case busy(Provider)
        case server(Provider, Int, String?)
        case unreadable

        var errorDescription: String? {
            switch self {
            case .noKey(let p): String(localized: "还没填 \(p.title) 的 Key")
            case .badKey(let p): String(localized: "\(p.title) 的 Key 不对")
            case .noCredit(let p): String(localized: "\(p.title) 的额度用完了")
            case .busy(let p): String(localized: "\(p.title) 说太忙，等一会儿再试")
            case .server(let p, let code, let message): message.map { "\(p.title) \(code): \($0)" } ?? "\(p.title) \(code)"
            case .unreadable: String(localized: "声音数据读不出来")
            }
        }
    }

    private func fetch(_ text: String, language: String, parts: [SpokenPart]? = nil, acting: Acting? = nil) async throws -> Data {
        guard let key = provider.key else { throw Failure.noKey(provider) }
        var request: URLRequest
        switch provider {
        case .openai:
            request = URLRequest(url: URL(string: "https://api.openai.com/v1/audio/speech")!)
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var body: [String: Any] = ["model": "gpt-4o-mini-tts", "voice": voiceID, "input": text, "response_format": "wav"]
            if let i = acting?.instruction, !i.isEmpty { body["instructions"] = i }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
        case .azure:
            let region = UserDefaults.standard.string(forKey: Provider.regionName) ?? ""
            request = URLRequest(url: URL(string: "https://\(region).tts.speech.microsoft.com/cognitiveservices/v1")!)
            request.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
            request.setValue("application/ssml+xml", forHTTPHeaderField: "Content-Type")
            request.setValue("riff-24khz-16bit-mono-pcm", forHTTPHeaderField: "X-Microsoft-OutputFormat")
            request.setValue("Yuyue", forHTTPHeaderField: "User-Agent")
            let lang = self.language ?? SpokenLanguage.voiceLanguages(for: language).first ?? "en-US"
            func escape(_ t: String) -> String {
                t.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;").replacingOccurrences(of: ">", with: "&gt;")
            }
            // Words the book gave a reading for, said that way (pinyin with tone numbers).
            var inner = (parts ?? [SpokenPart(text: text, reading: nil)]).map { p in
                guard let r = p.reading, lang.hasPrefix("zh") else { return escape(p.text) }
                return "<phoneme alphabet='sapi' ph='\(Self.sapi(r))'>\(escape(p.text))</phoneme>"
            }.joined()
            if let style = acting?.style {
                inner = "<mstts:express-as style='\(style)' styledegree='\(String(format: "%.1f", acting?.degree ?? 1))'>\(inner)</mstts:express-as>"
            }
            request.httpBody = Data("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='\(lang)'><voice name='\(voiceID)'>\(inner)</voice></speak>".utf8)
        case .elevenlabs:
            request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/text-to-speech/\(voiceID)?output_format=pcm_24000")!)
            request.setValue(key, forHTTPHeaderField: "xi-api-key")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            // Tags are read by the v3 model only; plain speech stays on the one it always used.
            if let a = acting, !(a.head.isEmpty && a.tail.isEmpty) {
                request.httpBody = try JSONSerialization.data(withJSONObject: ["text": a.head + text + a.tail, "model_id": "eleven_v3"])
            } else {
                request.httpBody = try JSONSerialization.data(withJSONObject: ["text": text, "model_id": "eleven_multilingual_v2"])
            }
        }
        request.httpMethod = "POST"
        request.timeoutInterval = 30
        let (data, response) = try await URLSession.shared.data(for: request)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
        // ElevenLabs sends bare 16-bit samples; keep every voice's audio as WAV.
        return provider == .elevenlabs ? Self.wav(pcm: data, rate: 24_000) : data
    }

    static func failure(_ p: Provider, _ code: Int, _ data: Data) -> Failure {
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let detail = json?["detail"] as? [String: Any]
        let message = (json?["error"] as? [String: Any])?["message"] as? String ?? detail?["message"] as? String ?? json?["message"] as? String
        switch code {
        case 401, 403: return (detail?["status"] as? String) == "quota_exceeded" ? .noCredit(p) : .badKey(p)
        case 402: return .noCredit(p)
        case 429: return message?.contains("quota") == true ? .noCredit(p) : .busy(p)
        default: return .server(p, code, message)
        }
    }

    // MARK: Audio

    static func decode(_ data: Data) throws -> Speech? {
        let tmp = FileManager.default.temporaryDirectory.appending(path: "voice-\(UUID().uuidString).wav")
        try data.write(to: tmp)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let file = try AVAudioFile(forReading: tmp)
        guard file.length > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw Failure.unreadable
        }
        try file.read(into: buffer)
        return Speech.make(from: [buffer])
    }

    /// 16-bit mono samples in a WAV header.
    static func wav(pcm: Data, rate: Int) -> Data {
        var d = Data()
        func put<T: FixedWidthInteger>(_ v: T) { withUnsafeBytes(of: v.littleEndian) { d.append(contentsOf: $0) } }
        d.append(contentsOf: Array("RIFF".utf8)); put(UInt32(36 + pcm.count))
        d.append(contentsOf: Array("WAVEfmt ".utf8)); put(UInt32(16)); put(UInt16(1)); put(UInt16(1))
        put(UInt32(rate)); put(UInt32(rate * 2)); put(UInt16(2)); put(UInt16(16))
        d.append(contentsOf: Array("data".utf8)); put(UInt32(pcm.count))
        d.append(pcm)
        return d
    }

    // MARK: The voices

    /// OpenAI's voices read every language.
    static let openAIVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer"]
    /// How OpenAI's voices sound (it doesn't say).
    static let openAIGender: [String: String] = [
        "alloy": "female", "ash": "male", "ballad": "male", "coral": "female", "echo": "male",
        "fable": "male", "nova": "female", "onyx": "male", "sage": "female", "shimmer": "female",
    ]

    /// Pinyin with tone marks (jiǎ zhēn) as Azure's Chinese phonemes want it (jia 3 zhen 1).
    static func sapi(_ pinyin: String) -> String {
        let marks: [Character: (Character, Int)] = [
            "ā": ("a", 1), "á": ("a", 2), "ǎ": ("a", 3), "à": ("a", 4), "ē": ("e", 1), "é": ("e", 2), "ě": ("e", 3), "è": ("e", 4),
            "ī": ("i", 1), "í": ("i", 2), "ǐ": ("i", 3), "ì": ("i", 4), "ō": ("o", 1), "ó": ("o", 2), "ǒ": ("o", 3), "ò": ("o", 4),
            "ū": ("u", 1), "ú": ("u", 2), "ǔ": ("u", 3), "ù": ("u", 4), "ǖ": ("v", 1), "ǘ": ("v", 2), "ǚ": ("v", 3), "ǜ": ("v", 4), "ü": ("v", 0),
        ]
        return pinyin.lowercased().split(whereSeparator: { $0 == " " || $0 == "'" || $0 == "-" }).map { syllable -> String in
            var tone = 5
            let plain = String(syllable.map { c -> Character in
                guard let (base, t) = marks[c] else { return c }
                if t > 0 { tone = t }
                return base
            })
            // Already numbered (jia3): keep its number.
            if let last = plain.last, let n = last.wholeNumberValue { return "\(plain.dropLast()) \(n)" }
            return "\(plain) \(tone)"
        }.joined(separator: " ")
    }

    /// The voices of the providers that have a key, from the lists fetched when it was saved.
    static func all() -> [CloudVoice] {
        Provider.allCases.filter(\.isSetUp).flatMap { p -> [CloudVoice] in
            switch p {
            case .openai:
                return openAIVoices.map { CloudVoice(provider: p, voiceID: $0, name: $0.capitalized, language: nil) }
            case .azure, .elevenlabs:
                let saved = UserDefaults.standard.array(forKey: p.voicesName) as? [[String]] ?? []
                return saved.compactMap { v in
                    guard v.count >= 2 else { return nil }
                    func at(_ i: Int) -> String? { v.count > i && !v[i].isEmpty ? v[i] : nil }
                    return CloudVoice(provider: p, voiceID: v[0], name: v[1], language: at(2), gender: at(3), age: at(4))
                }
            }
        }
    }

    /// Checks the key by fetching the provider's voices, and keeps the list.
    static func refresh(_ p: Provider) async throws -> Int {
        guard let key = p.key else { throw Failure.noKey(p) }
        switch p {
        case .openai:
            var request = URLRequest(url: URL(string: "https://api.openai.com/v1/models/gpt-4o-mini-tts")!)
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            return openAIVoices.count
        case .azure:
            let region = UserDefaults.standard.string(forKey: Provider.regionName) ?? ""
            var request = URLRequest(url: URL(string: "https://\(region).tts.speech.microsoft.com/cognitiveservices/voices/list")!)
            request.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let list = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] ?? []
            // Only the neural voices for Chinese and English: the rest are hundreds.
            let voices: [[String]] = list.compactMap { v in
                guard let short = v["ShortName"] as? String, let locale = v["Locale"] as? String,
                      locale.hasPrefix("zh") || locale.hasPrefix("en") || locale.hasPrefix("ja") else { return nil }
                return [short, (v["LocalName"] as? String ?? short), locale, (v["Gender"] as? String ?? "").lowercased()]
            }
            UserDefaults.standard.set(voices, forKey: p.voicesName)
            return voices.count
        case .elevenlabs:
            var request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/voices")!)
            request.setValue(key, forHTTPHeaderField: "xi-api-key")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let list = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["voices"] as? [[String: Any]] ?? []
            let voices: [[String]] = list.compactMap { v in
                guard let id = v["voice_id"] as? String, let name = v["name"] as? String else { return nil }
                let labels = v["labels"] as? [String: Any] ?? [:]
                return [id, name, "", (labels["gender"] as? String ?? "").lowercased(), (labels["age"] as? String ?? "").lowercased()]
            }
            UserDefaults.standard.set(voices, forKey: p.voicesName)
            return voices.count
        }
    }
}
