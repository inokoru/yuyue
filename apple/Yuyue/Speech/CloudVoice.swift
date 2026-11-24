import AVFoundation
import CryptoKit
import Foundation
import os
import Synchronization
import YuyueCore

/// 外部语音来源：支持国内主流语音云（小米 MiMo、硅基流动、阿里云百炼、MiniMax）、国际语音云（微软 Azure、
/// OpenAI、ElevenLabs），以及本机/局域网开源语音服务（VoiceStudio、Voicebox、mlx-audio / Speaches /
/// Kokoro / unspeech 等任意 OpenAI `/v1/audio/speech` 兼容接口）。
///
/// 已合成的句子按「音色 + 文本 + 语气」持久缓存在本机，复听或离线重放零延迟、不重复计费。
nonisolated final class CloudVoice: Voice, @unchecked Sendable {
    enum Group: CaseIterable, Sendable {
        case domestic, global, local

        var title: String {
            switch self {
            case .domestic: String(localized: "国内服务")
            case .global: String(localized: "国际服务")
            case .local: String(localized: "本机与局域网")
            }
        }
    }

    enum Provider: String, CaseIterable, Identifiable, Sendable {
        case edge
        case free
        case volcengine
        case xiaomi
        case siliconflow
        case dashscope
        case minimax
        case fish
        case azure
        case openai
        case elevenlabs
        case deepgram
        case voicestudio
        case voicebox
        case mlxaudio
        case speaches
        case local

        var id: String { rawValue }

        var group: Group {
            switch self {
            case .free, .volcengine, .xiaomi, .siliconflow, .dashscope, .minimax: .domestic
            case .edge, .fish, .azure, .openai, .elevenlabs, .deepgram: .global
            case .voicestudio, .voicebox, .mlxaudio, .speaches, .local: .local
            }
        }

        var isLocal: Bool { group == .local }
        var isFreeOnline: Bool { self == .edge || self == .free }
        var isPaidCloud: Bool { !isLocal && !isFreeOnline }
        var needsKey: Bool { !isLocal && !isFreeOnline }

        var title: String {
            switch self {
            case .edge: String(localized: "微软 Edge")
            case .free: String(localized: "在线自然语音")
            case .volcengine: String(localized: "火山引擎（豆包）")
            case .xiaomi: String(localized: "小米 MiMo")
            case .siliconflow: String(localized: "硅基流动")
            case .dashscope: String(localized: "阿里云百炼")
            case .minimax: "MiniMax"
            case .fish: "Fish Audio"
            case .azure: String(localized: "微软 Azure")
            case .openai: "OpenAI"
            case .elevenlabs: "ElevenLabs"
            case .deepgram: "Deepgram"
            case .voicestudio: "VoiceStudio"
            case .voicebox: "Voicebox"
            case .mlxaudio: "mlx-audio"
            case .speaches: "Speaches"
            case .local: {
                let custom = UserDefaults.standard.string(forKey: "tts.local.title") ?? ""
                return custom.isEmpty ? String(localized: "自定义语音接口") : custom
            }()
            }
        }

        /// 顶部紧凑切换栏使用的短标题（避免在窄栏里被截断）。
        var tabTitle: String {
            switch self {
            case .edge: "Edge"
            case .free: String(localized: "在线语音")
            case .volcengine: String(localized: "火山豆包")
            case .xiaomi: String(localized: "小米 MiMo")
            case .siliconflow: String(localized: "硅基流动")
            case .dashscope: String(localized: "阿里百炼")
            case .minimax: "MiniMax"
            case .fish: "Fish"
            case .azure: "Azure"
            case .openai: "OpenAI"
            case .elevenlabs: "ElevenLabs"
            case .deepgram: "Deepgram"
            case .voicestudio: "VoiceStudio"
            case .voicebox: "Voicebox"
            case .mlxaudio: "mlx-audio"
            case .speaches: "Speaches"
            case .local: {
                let custom = (UserDefaults.standard.string(forKey: "tts.local.title") ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                if custom.isEmpty { return String(localized: "自定义") }
                return custom.count > 6 ? String(custom.prefix(6)) + "…" : custom
            }()
            }
        }

        /// 搜索别名（中文名、开源项目名、模型名）。
        var aliases: String {
            switch self {
            case .edge: "edge microsoft bing 微软 晓晓 云希 云健 免key 免费 在线 朗读"
            case .free: "free keyless sherpa kokoro melotts 免key 免费 在线"
            case .volcengine: "volcengine doubao bytedance 火山 豆包 字节 灿灿"
            case .xiaomi: "xiaomi mimo 小米 小爱 冰糖 茉莉"
            case .siliconflow: "siliconflow 硅基 cosyvoice indextts fish"
            case .dashscope: "dashscope aliyun qwen 阿里 百炼 通义 千问"
            case .minimax: "minimax 海螺 稀宇 speech"
            case .fish: "fish audio openaudio s1 鱼 社区克隆"
            case .azure: "microsoft azure 微软 晓晓 云希"
            case .openai: "openai gpt chatgpt alloy nova"
            case .elevenlabs: "elevenlabs 11labs"
            case .deepgram: "deepgram aura 英文"
            case .voicestudio: "voicestudio omnivoice 本地 克隆 3900"
            case .voicebox: "voicebox qwen3 kokoro chatterbox 17493"
            case .mlxaudio: "mlx-audio mlx apple silicon qwen3 kokoro 8000"
            case .speaches: "speaches docker kokoro piper whisper 8000"
            case .local: "local openai unspeech gptsovits localai kokoro 局域网 本机 8880"
            }
        }

        /// 推荐角标（如免费额度提示），保持克制。
        var badge: String? {
            switch self {
            case .edge, .free: String(localized: "免 Key")
            case .xiaomi: String(localized: "限时免费")
            case .volcengine, .fish, .azure, .deepgram: String(localized: "含免费额度")
            case .voicestudio, .voicebox, .mlxaudio, .speaches: String(localized: "本机开源")
            default: nil
            }
        }

        var keyName: String { "tts.\(rawValue).key" }
        static let regionName = "tts.azure.region"
        static let volcAppIDName = "tts.volcengine.appid"
        var voicesName: String { "tts.\(rawValue).voices" }
        var urlName: String { "tts.\(rawValue).url" }
        var modelName: String { "tts.\(rawValue).model" }
        var enabledName: String { "tts.\(rawValue).enabled" }
        var dismissedName: String { "tts.\(rawValue).dismissed" }

        var defaultURL: String {
            #if os(macOS)
            switch self {
            case .voicestudio: return "http://127.0.0.1:3900/v1"
            case .voicebox: return "http://127.0.0.1:17493/v1"
            case .mlxaudio, .speaches, .local: return "http://127.0.0.1:8000/v1"
            default: return ""
            }
            #else
            return ""
            #endif
        }

        var urlHint: String {
            if !defaultURL.isEmpty { return defaultURL }
            switch self {
            case .voicestudio: return "http://192.168.1.5:3900/v1"
            case .voicebox: return "http://192.168.1.5:17493/v1"
            default: return "http://192.168.1.5:8000/v1"
            }
        }

        var defaultModel: String {
            switch self {
            case .voicestudio: "omnivoice"
            case .voicebox: "tts-1"
            case .mlxaudio: "mlx-community/Kokoro-82M-bf16"
            case .speaches: "speaches-ai/Kokoro-82M-v1.0-ONNX"
            case .local: "kokoro"
            default: ""
            }
        }

        var savedURL: String {
            let u = (UserDefaults.standard.string(forKey: urlName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if !u.isEmpty { return u }
            if let probed = LocalVoiceProbe.shared.discoveredURL(for: self) { return probed }
            return defaultURL
        }

        var savedModel: String {
            let m = (UserDefaults.standard.string(forKey: modelName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            if !m.isEmpty { return m }
            if let probed = LocalVoiceProbe.shared.discoveredModel(for: self), !probed.isEmpty { return probed }
            return defaultModel
        }

        var key: String? { Keychain.read(keyName).flatMap { $0.isEmpty ? nil : $0 } }

        /// 若用户在「书伴」里已配置同一家服务商，可一键复用其 API Key。
        var companionSharedKey: String? {
            let id: String? = switch self {
            case .siliconflow: "siliconflow"
            case .dashscope: "dashscope"
            case .minimax: "minimax"
            case .openai: "openai"
            default: nil
            }
            guard let id, let k = Keychain.read("companion.key.\(id)"), !k.isEmpty else { return nil }
            return k
        }

        var isAutoDiscovered: Bool {
            guard isLocal, !UserDefaults.standard.bool(forKey: dismissedName) else { return false }
            return LocalVoiceProbe.shared.isLive(self)
        }

        var isSetUp: Bool {
            if isFreeOnline {
                return !UserDefaults.standard.bool(forKey: dismissedName)
            }
            if isLocal {
                let explicit = UserDefaults.standard.bool(forKey: enabledName) && !savedURL.isEmpty
                return explicit || isAutoDiscovered
            }
            if self == .volcengine {
                guard let k = key, !k.isEmpty else { return false }
                let appID = (UserDefaults.standard.string(forKey: Self.volcAppIDName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
                return !appID.isEmpty || k.contains("|")
            }
            return key != nil && (self != .azure || !(UserDefaults.standard.string(forKey: Self.regionName) ?? "").isEmpty)
        }

        var keyHint: String {
            switch self {
            case .edge, .free: String(localized: "免填 Key")
            case .volcengine: String(localized: "AppID|AccessToken（如 12345678|token…）")
            case .xiaomi, .siliconflow, .dashscope, .openai: "sk-…"
            case .fish: "fish_… / Bearer Key"
            case .minimax: "eyJ…"
            case .azure: String(localized: "资源的密钥 1")
            case .elevenlabs: "sk_…"
            case .deepgram: "Token…"
            case .voicestudio, .voicebox, .mlxaudio, .speaches, .local: String(localized: "本机不用填")
            }
        }

        var help: String {
            switch self {
            case .edge: String(localized: "免注册免填 Key，直连微软 Edge 自然神经语音（晓晓、云希、Ava 等），已朗读句子自动缓存在本机。")
            case .free: String(localized: "0 MB 免下载、免注册免填 Key，直连公共语音接口，已朗读句子自动缓存在本机。")
            case .volcengine: String(localized: "火山引擎语音技术控制台创建应用后，按「AppID|AccessToken」填入（竖线分隔），支持豆包灿灿 2.0 等自然声线。")
            case .xiaomi: String(localized: "platform.xiaomimimo.com 控制台新建 API Key，当前限时免费，支持自然语言语气控制。")
            case .siliconflow: String(localized: "cloud.siliconflow.cn › API 密钥新建。内置 CosyVoice2 音色并支持你在控制台克隆的声音。")
            case .dashscope: String(localized: "阿里云百炼控制台获取 API-KEY，千问3-TTS 按字计费（约 0.1 元/千字），新开通含免费体验包。")
            case .minimax: String(localized: "platform.minimaxi.com › 接口密钥新建。支持高拟真呼吸感与情绪朗读。")
            case .fish: String(localized: "fish.audio › API Keys 新建密钥，基于 OpenAudio S1 引擎，自动拉取热门中英社区旁白与自建音色。")
            case .openai: String(localized: "platform.openai.com › API keys 里建一个。费用按字数算，走你的账号。")
            case .azure: String(localized: "Azure 门户里建一个“语音”资源，拷它的密钥和区域（比如 eastasia）。有免费额度。")
            case .elevenlabs: String(localized: "elevenlabs.io › 个人资料 › API Keys。额度按字数算。")
            case .deepgram: String(localized: "console.deepgram.com 新建 API Key，Aura-2 英文极速低延迟，注册含免费体验金。")
            case .voicestudio: String(localized: "在电脑上启动 VoiceStudio（默认端口 3900），语阅会自动连上你在里面保存的克隆音色。")
            case .voicebox: String(localized: "在电脑上启动 Voicebox（默认端口 17493），直接复用其本地多引擎音色，完全离线。")
            case .mlxaudio: String(localized: "在 Mac 上运行 mlx-audio.server（默认端口 8000），利用 Apple Silicon 原生加速本地语音。")
            case .speaches: String(localized: "通过 Docker 部署 Speaches（默认端口 8000），适合本机或家里 NAS 局域网共享。")
            case .local: String(localized: "支持 GPT-SoVITS、Kokoro-FastAPI、LocalAI、unspeech 等任何兼容 OpenAI /v1/audio/speech 的本机或局域网服务。")
            }
        }
    }

    let id: String
    let name: String
    let provider: Provider
    /// The voice's own language (Azure / Xiaomi), or nil for one that reads every language.
    let language: String?
    /// The provider's id for it.
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

    /// 带语气: how a line of a play is said, in each provider's own terms — OpenAI / Xiaomi MiMo /
    /// Local OmniVoice & Qwen3-TTS by natural-language instruction, ElevenLabs by tags, Azure by
    /// speaking style, MiniMax by emotion.
    struct Acting: Sendable, Hashable {
        var instruction: String
        var head: String
        var tail: String
        var style: String?
        var degree: Double

        var isFlat: Bool { instruction.isEmpty && head.isEmpty && tail.isEmpty && style == nil }
        var key: String { "\(instruction)|\(head)|\(tail)|\(style ?? "")|\(degree)" }
    }

    func render(parts: [SpokenPart], language: String, acting: Acting?) async throws -> Speech? {
        let text = parts.map(\.text).joined()
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else { return nil }
        let readings = provider == .azure ? parts.compactMap { p in p.reading.map { "\(p.text)=\($0)" } }.joined(separator: ",") : ""
        let acted = acting.flatMap { $0.isFlat ? nil : $0 }
        let cacheKey = acted == nil && readings.isEmpty ? text : text + "\u{2}" + (acted?.key ?? "") + "\u{2}" + readings
        if let cached = readCached(cacheKey) {
            if provider.isPaidCloud { CompanionUsage.recordTts(chars: text.count, cached: true) }
            return cached
        }
        if provider.isPaidCloud { try CompanionUsage.checkTtsBudget(chars: text.count) }
        let (data, ext) = try await fetch(text, language: language, parts: parts, acting: acted)
        if provider.isPaidCloud { CompanionUsage.recordTts(chars: text.count, cached: false) }
        writeCached(data, key: cacheKey, ext: ext)
        return try Self.decode(data, ext: ext)
    }

    var qualityName: String? { provider.title }
    var isOffline: Bool { provider.isLocal }

    func speaks(_ tag: String) -> Bool {
        language.map { SpokenLanguage.matches($0, tag) } ?? true
    }

    func render(_ text: String, language: String) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else { return nil }
        if let cached = readCached(text) {
            if provider.isPaidCloud { CompanionUsage.recordTts(chars: text.count, cached: true) }
            return cached
        }
        if provider.isPaidCloud { try CompanionUsage.checkTtsBudget(chars: text.count) }
        let (data, ext) = try await fetch(text, language: language)
        if provider.isPaidCloud { CompanionUsage.recordTts(chars: text.count, cached: false) }
        writeCached(data, key: text, ext: ext)
        return try Self.decode(data, ext: ext)
    }

    /// Renders the sentences not kept yet, two at a time, so the chapter plays on without the
    /// network. Stops at the first failure; the player reports its own.
    @concurrent func prefetch(_ texts: [String], language: String) async {
        let missing = texts.filter { t in
            t.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) && !hasCached(t)
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

    static var persistentCacheDir: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        return base.appending(path: "Yuyue/cloud-voices", directoryHint: .isDirectory)
    }

    /// 300 MB persistent cap (~14 hours of 48 kbps mono MP3).
    static let maxPersistentCacheBytes: Int64 = 300_000_000

    private func hashKey(_ text: String) -> String {
        SHA256.hash(data: Data((id + "\u{1}" + text).utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
    }

    private func cacheFile(_ text: String, ext: String = "mp3") -> URL {
        Self.persistentCacheDir.appending(path: provider.rawValue).appending(path: "\(hashKey(text)).\(ext)")
    }

    private func legacyCacheFile(_ text: String) -> URL {
        Self.cacheDir.appending(path: provider.rawValue).appending(path: "\(hashKey(text)).wav")
    }

    private static let cacheExtensions = ["mp3", "wav", "flac", "ogg"]

    private func hasCached(_ text: String) -> Bool {
        let fm = FileManager.default
        return Self.cacheExtensions.contains { fm.fileExists(atPath: cacheFile(text, ext: $0).path) }
            || fm.fileExists(atPath: legacyCacheFile(text).path)
    }

    private func readCached(_ text: String) -> Speech? {
        let candidates = Self.cacheExtensions.map { (cacheFile(text, ext: $0), $0) }
            + [(legacyCacheFile(text), "wav")]
        for (url, ext) in candidates {
            if let data = try? Data(contentsOf: url), let speech = try? Self.decode(data, ext: ext) {
                try? FileManager.default.setAttributes([.modificationDate: Date()], ofItemAtPath: url.path)
                return speech
            }
        }
        return nil
    }

    private func writeCached(_ data: Data, key: String, ext: String) {
        let file = cacheFile(key, ext: ext)
        let dir = file.deletingLastPathComponent()
        let fm = FileManager.default
        try? fm.createDirectory(at: dir, withIntermediateDirectories: true)
        var root = Self.persistentCacheDir
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? root.setResourceValues(values)
        try? data.write(to: file, options: .atomic)
        Self.trimPersistentCacheIfNeeded()
    }

    private static let trimCounter = OSAllocatedUnfairLock(initialState: 0)

    private static func trimPersistentCacheIfNeeded() {
        let shouldRun = trimCounter.withLock { n -> Bool in
            n += 1
            if n >= 32 { n = 0; return true }
            return false
        }
        guard shouldRun else { return }
        let root = persistentCacheDir
        let cap = maxPersistentCacheBytes
        DispatchQueue.global(qos: .utility).async {
            let fm = FileManager.default
            guard let enumerator = fm.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]) else { return }
            var entries: [(URL, Int64, Date)] = []
            var total: Int64 = 0
            for case let url as URL in enumerator {
                guard let rv = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileSizeKey, .contentModificationDateKey]),
                      rv.isRegularFile == true else { continue }
                let size = Int64(rv.fileSize ?? 0)
                total += size
                entries.append((url, size, rv.contentModificationDate ?? .distantPast))
            }
            guard total > cap else { return }
            let target = Int64(Double(cap) * 0.8)
            for (url, size, _) in entries.sorted(by: { $0.2 < $1.2 }) {
                if total <= target { break }
                try? fm.removeItem(at: url)
                total -= size
            }
        }
    }

    // MARK: Fetching

    enum Failure: LocalizedError {
        case noKey(Provider)
        case noURL(Provider)
        case badKey(Provider)
        case noCredit(Provider)
        case budgetExceeded(String)
        case busy(Provider)
        case unreachable(Provider)
        case server(Provider, Int, String?)
        case unreadable

        var errorDescription: String? {
            switch self {
            case .noKey(let p): String(localized: "还没填 \(p.title) 的 Key")
            case .noURL(let p): String(localized: "还没填 \(p.title) 的服务地址")
            case .badKey(let p): String(localized: "\(p.title) 的 Key 不对")
            case .noCredit(let p): String(localized: "\(p.title) 的额度用完了")
            case .budgetExceeded(let detail): String(localized: "已达到今日云端朗读上限（\(detail)），已自动暂停。可在「设置 · 书伴」中调整。")
            case .busy(let p): String(localized: "\(p.title) 说太忙，等一会儿再试")
            case .unreachable(let p): String(localized: "连不上 \(p.title)，请确认本地语音服务已启动")
            case .server(let p, let code, let message): message.map { "\(p.title) \(code): \($0)" } ?? "\(p.title) \(code)"
            case .unreadable: String(localized: "声音数据读不出来")
            }
        }
    }

    private func fetch(_ text: String, language: String, parts: [SpokenPart]? = nil, acting: Acting? = nil) async throws -> (Data, String) {
        switch provider {
        case .edge:
            return try await Self.fetchEdge(text: text, voiceID: voiceID, language: language)

        case .free:
            return try await Self.fetchFreeOnline(text: text, voiceID: voiceID, language: language)

        case .volcengine:
            guard let (appID, token) = Self.volcengineCredentials() else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://openspeech.bytedance.com/api/v1/tts")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer;\(token)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let body: [String: Any] = [
                "app": ["appid": appID, "token": token, "cluster": "volcano_tts"],
                "user": ["uid": "yuyue"],
                "audio": ["voice_type": voiceID, "encoding": "mp3", "speed_ratio": 1.0],
                "request": ["reqid": UUID().uuidString, "text": text, "operation": "query"],
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
                throw Failure.unreadable
            }
            let apiCode = (json["code"] as? Int) ?? 0
            if apiCode != 3000 {
                let msg = json["message"] as? String
                if apiCode == 3001 || apiCode == 3003 || code == 401 || code == 403 {
                    throw Failure.badKey(provider)
                }
                throw Failure.server(provider, apiCode, msg)
            }
            guard let b64 = json["data"] as? String,
                  let audioData = Data(base64Encoded: b64, options: .ignoreUnknownCharacters),
                  !audioData.isEmpty else {
                throw Failure.unreadable
            }
            return (audioData, Self.sniffExt(audioData, fallback: "mp3"))

        case .fish:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://api.fish.audio/v1/tts")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.setValue("s1", forHTTPHeaderField: "model")
            var body: [String: Any] = [
                "text": text,
                "format": "mp3",
                "mp3_bitrate": 64,
            ]
            if !voiceID.isEmpty && voiceID != "default" {
                body["reference_id"] = voiceID
            }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, Self.sniffExt(data, fallback: "mp3"))

        case .xiaomi:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://api.xiaomimimo.com/v1/chat/completions")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var messages: [[String: String]] = []
            if let inst = acting?.instruction, !inst.isEmpty {
                messages.append(["role": "user", "content": inst])
            }
            messages.append(["role": "assistant", "content": text])
            let body: [String: Any] = [
                "model": "mimo-v2.5-tts",
                "messages": messages,
                "audio": ["format": "mp3", "voice": voiceID],
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let choices = json["choices"] as? [[String: Any]],
                  let msg = choices.first?["message"] as? [String: Any],
                  let audioObj = msg["audio"] as? [String: Any],
                  let b64 = audioObj["data"] as? String,
                  let audioData = Data(base64Encoded: b64, options: .ignoreUnknownCharacters),
                  !audioData.isEmpty else {
                throw Failure.unreadable
            }
            return (audioData, Self.sniffExt(audioData, fallback: "mp3"))

        case .siliconflow:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://api.siliconflow.cn/v1/audio/speech")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let (modelName, actualVoice) = Self.splitSiliconFlowVoice(voiceID)
            let body: [String: Any] = [
                "model": modelName,
                "voice": actualVoice,
                "input": text,
                "response_format": "mp3",
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, Self.sniffExt(data, fallback: "mp3"))

        case .dashscope:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let body: [String: Any] = [
                "model": "qwen3-tts-flash",
                "input": ["text": text, "voice": voiceID],
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let output = json["output"] as? [String: Any],
                  let audioObj = output["audio"] as? [String: Any] else {
                throw Failure.unreadable
            }
            if let b64 = audioObj["data"] as? String, !b64.isEmpty,
               let decoded = Data(base64Encoded: b64, options: .ignoreUnknownCharacters), !decoded.isEmpty {
                return (decoded, Self.sniffExt(decoded, fallback: "wav"))
            }
            if let urlStr = audioObj["url"] as? String, let audioURL = URL(string: urlStr.replacingOccurrences(of: "http://", with: "https://")) {
                let (audioData, audioResp) = try await URLSession.shared.data(from: audioURL)
                let aCode = (audioResp as? HTTPURLResponse)?.statusCode ?? 0
                guard (200 ..< 300).contains(aCode), !audioData.isEmpty else { throw Failure.unreadable }
                let fmt = (audioObj["format"] as? String)?.lowercased() ?? "wav"
                return (audioData, Self.sniffExt(audioData, fallback: fmt == "mp3" ? "mp3" : "wav"))
            }
            throw Failure.unreadable

        case .minimax:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://api.minimaxi.com/v1/t2a_v2")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 35
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var voiceSetting: [String: Any] = ["voice_id": voiceID, "speed": 1.0, "vol": 1.0, "pitch": 0]
            if let emo = Self.miniMaxEmotion(acting) { voiceSetting["emotion"] = emo }
            let body: [String: Any] = [
                "model": "speech-02-hd",
                "text": text,
                "stream": false,
                "voice_setting": voiceSetting,
                "audio_setting": ["sample_rate": 24000, "bitrate": 64000, "format": "mp3", "channel": 1],
                "output_format": "hex",
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            if let baseResp = json?["base_resp"] as? [String: Any],
               let status = baseResp["status_code"] as? Int, status != 0 {
                let msg = baseResp["status_msg"] as? String
                if status == 1004 || status == 2049 { throw Failure.badKey(provider) }
                if status == 1008 || status == 1026 { throw Failure.noCredit(provider) }
                throw Failure.server(provider, status, msg)
            }
            guard let dataObj = json?["data"] as? [String: Any],
                  let audioStr = dataObj["audio"] as? String, !audioStr.isEmpty else {
                throw Failure.unreadable
            }
            if audioStr.hasPrefix("http"), let audioURL = URL(string: audioStr) {
                var audioReq = URLRequest(url: audioURL)
                audioReq.timeoutInterval = 35
                let (audioData, audioResp) = try await URLSession.shared.data(for: audioReq)
                let aCode = (audioResp as? HTTPURLResponse)?.statusCode ?? 0
                guard (200 ..< 300).contains(aCode) else { throw Self.failure(provider, aCode, audioData) }
                guard !audioData.isEmpty else { throw Failure.unreadable }
                return (audioData, Self.sniffExt(audioData, fallback: "mp3"))
            }
            guard let audioData = Self.decodeHex(audioStr), !audioData.isEmpty else {
                throw Failure.unreadable
            }
            return (audioData, Self.sniffExt(audioData, fallback: "mp3"))

        case .openai:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var request = URLRequest(url: URL(string: "https://api.openai.com/v1/audio/speech")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 30
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            var body: [String: Any] = ["model": "gpt-4o-mini-tts", "voice": voiceID, "input": text, "response_format": "mp3"]
            if let i = acting?.instruction, !i.isEmpty { body["instructions"] = i }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, Self.sniffExt(data, fallback: "mp3"))

        case .azure:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            let region = (UserDefaults.standard.string(forKey: Provider.regionName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !region.isEmpty, let url = URL(string: "https://\(region).tts.speech.microsoft.com/cognitiveservices/v1") else {
                throw URLError(.badURL)
            }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 30
            request.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
            request.setValue("application/ssml+xml", forHTTPHeaderField: "Content-Type")
            request.setValue("audio-24khz-48kbitrate-mono-mp3", forHTTPHeaderField: "X-Microsoft-OutputFormat")
            request.setValue("Yuyue", forHTTPHeaderField: "User-Agent")
            let lang = self.language ?? SpokenLanguage.voiceLanguages(for: language).first ?? "en-US"
            var inner = (parts ?? [SpokenPart(text: text, reading: nil)]).map { p in
                guard let r = p.reading, lang.hasPrefix("zh") else { return Self.escapeXML(p.text) }
                return "<phoneme alphabet='sapi' ph='\(Self.escapeXML(Self.sapi(r)))'>\(Self.escapeXML(p.text))</phoneme>"
            }.joined()
            if let style = acting?.style {
                inner = "<mstts:express-as style='\(Self.escapeXML(style))' styledegree='\(String(format: "%.1f", acting?.degree ?? 1))'>\(inner)</mstts:express-as>"
            }
            request.httpBody = Data("<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='\(Self.escapeXML(lang))'><voice name='\(Self.escapeXML(voiceID))'>\(inner)</voice></speak>".utf8)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, "mp3")

        case .elevenlabs:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            guard let encodedID = voiceID.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed),
                  let url = URL(string: "https://api.elevenlabs.io/v1/text-to-speech/\(encodedID)?output_format=mp3_24000_48") else {
                throw URLError(.badURL)
            }
            var request = URLRequest(url: url)
            request.httpMethod = "POST"
            request.timeoutInterval = 30
            request.setValue(key, forHTTPHeaderField: "xi-api-key")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            if let a = acting, !(a.head.isEmpty && a.tail.isEmpty) {
                request.httpBody = try JSONSerialization.data(withJSONObject: ["text": a.head + text + a.tail, "model_id": "eleven_v3"])
            } else {
                request.httpBody = try JSONSerialization.data(withJSONObject: ["text": text, "model_id": "eleven_flash_v2_5"])
            }
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, "mp3")

        case .deepgram:
            guard let key = provider.key else { throw Failure.noKey(provider) }
            var comps = URLComponents(string: "https://api.deepgram.com/v1/speak")!
            comps.queryItems = [URLQueryItem(name: "model", value: voiceID), URLQueryItem(name: "encoding", value: "mp3")]
            var request = URLRequest(url: comps.url!)
            request.httpMethod = "POST"
            request.timeoutInterval = 30
            request.setValue("Token \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = try JSONSerialization.data(withJSONObject: ["text": text])
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, "mp3")

        case .voicestudio, .voicebox, .mlxaudio, .speaches, .local:
            guard let endpoint = Self.speechEndpoint(from: provider.savedURL) else {
                throw Failure.noURL(provider)
            }
            var request = URLRequest(url: endpoint)
            request.httpMethod = "POST"
            request.timeoutInterval = 90
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            if let key = provider.key, !key.isEmpty {
                request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            }
            var body: [String: Any] = [
                "model": provider.savedModel,
                "voice": voiceID,
                "input": text,
                "response_format": "wav",
            ]
            if let inst = acting?.instruction, !inst.isEmpty {
                body["instructions"] = inst
            }
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let data: Data
            let response: URLResponse
            do {
                (data, response) = try await URLSession.shared.data(for: request)
            } catch {
                throw Failure.unreachable(provider)
            }
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw Self.failure(provider, code, data) }
            return (data, Self.sniffExt(data, fallback: "wav"))
        }
    }

    static func speechEndpoint(from rawBase: String) -> URL? {
        var s = rawBase.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !s.isEmpty else { return nil }
        while s.hasSuffix("/") { s.removeLast() }
        if s.hasSuffix("/audio/speech") { return URL(string: s) }
        if !s.hasSuffix("/v1") && !s.contains("/v1/") {
            s += "/v1"
        }
        return URL(string: s + "/audio/speech")
    }

    static func normalizedBaseV1(_ rawBase: String) -> String {
        var s = rawBase.trimmingCharacters(in: .whitespacesAndNewlines)
        while s.hasSuffix("/") { s.removeLast() }
        if s.hasSuffix("/audio/speech") {
            s = String(s.dropLast("/audio/speech".count))
        }
        if !s.isEmpty && !s.hasSuffix("/v1") {
            s += "/v1"
        }
        return s
    }

    private static func splitSiliconFlowVoice(_ id: String) -> (model: String, voice: String) {
        if let bar = id.firstIndex(of: "|") {
            return (String(id[..<bar]), String(id[id.index(after: bar)...]))
        }
        if let colon = id.firstIndex(of: ":"), id.hasPrefix("FunAudioLLM/") || id.hasPrefix("fishaudio/") || id.hasPrefix("IndexTeam/") {
            return (String(id[..<colon]), id)
        }
        return ("FunAudioLLM/CosyVoice2-0.5B", id)
    }

    private static func miniMaxEmotion(_ acting: Acting?) -> String? {
        guard let a = acting else { return nil }
        let s = (a.instruction + " " + (a.style ?? "")).lowercased()
        if s.contains("happy") || s.contains("cheer") || s.contains("欢") || s.contains("喜") || s.contains("笑") { return "happy" }
        if s.contains("sad") || s.contains("悲") || s.contains("伤") || s.contains("哭") || s.contains("哽咽") { return "sad" }
        if s.contains("angry") || s.contains("怒") || s.contains("气") || s.contains("吼") { return "angry" }
        if s.contains("fear") || s.contains("恐") || s.contains("怕") || s.contains("颤") { return "fearful" }
        if s.contains("calm") || s.contains("gentle") || s.contains("平") || s.contains("柔") { return "calm" }
        return nil
    }

    private static func decodeHex(_ hex: String) -> Data? {
        let chars = Array(hex.utf8)
        guard chars.count >= 4, chars.count % 2 == 0 else { return nil }
        var out = Data(capacity: chars.count / 2)
        func val(_ b: UInt8) -> UInt8? {
            switch b {
            case 48...57: b - 48
            case 65...70: b - 55
            case 97...102: b - 87
            default: nil
            }
        }
        var i = 0
        while i < chars.count {
            guard let hi = val(chars[i]), let lo = val(chars[i + 1]) else { return nil }
            out.append((hi << 4) | lo)
            i += 2
        }
        return out
    }

    /// Escapes XML text and attribute values and strips XML 1.0 illegal control characters.
    static func escapeXML(_ text: String) -> String {
        var out = ""
        out.reserveCapacity(text.utf8.count)
        for scalar in text.unicodeScalars {
            let v = scalar.value
            let valid = v == 0x09 || v == 0x0A || v == 0x0D
                || (0x20 ... 0xD7FF).contains(v)
                || (0xE000 ... 0xFFFD).contains(v)
                || (0x10000 ... 0x10FFFF).contains(v)
            guard valid else { continue }
            switch scalar {
            case "&": out += "&amp;"
            case "<": out += "&lt;"
            case ">": out += "&gt;"
            case "'": out += "&apos;"
            case "\"": out += "&quot;"
            default: out.unicodeScalars.append(scalar)
            }
        }
        return out
    }

    static func sniffExt(_ data: Data, fallback: String) -> String {
        if data.count >= 4 {
            let head = Array(data.prefix(4))
            if head == [0x52, 0x49, 0x46, 0x46] { return "wav" }   // RIFF
            if head == [0x66, 0x4C, 0x61, 0x43] { return "flac" }  // fLaC
            if head == [0x4F, 0x67, 0x67, 0x53] { return "ogg" }   // OggS
            if head[0] == 0x49 && head[1] == 0x44 && head[2] == 0x33 { return "mp3" } // ID3
            if head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 { return "mp3" }
        }
        return fallback
    }

    static func failure(_ p: Provider, _ code: Int, _ data: Data) -> Failure {
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
        let detail = json?["detail"] as? [String: Any]
        let message = (json?["error"] as? [String: Any])?["message"] as? String
            ?? detail?["message"] as? String
            ?? json?["message"] as? String
        switch code {
        case 401, 403: return (detail?["status"] as? String) == "quota_exceeded" ? .noCredit(p) : .badKey(p)
        case 402: return .noCredit(p)
        case 429: return message?.contains("quota") == true ? .noCredit(p) : .busy(p)
        default: return .server(p, code, message)
        }
    }

    // MARK: Audio

    static func decode(_ data: Data, ext: String = "wav") throws -> Speech? {
        let realExt = sniffExt(data, fallback: ext)
        let tmp = FileManager.default.temporaryDirectory.appending(path: "voice-\(UUID().uuidString).\(realExt)")
        try data.write(to: tmp)
        defer { try? FileManager.default.removeItem(at: tmp) }
        let file = try AVAudioFile(forReading: tmp)
        guard file.length > 0, let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
            throw Failure.unreadable
        }
        try file.read(into: buffer)
        return Speech.make(from: [buffer])
    }

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

    // MARK: Built-in preset voice tables

    static let edgeVoices: [(id: String, name: String, lang: String, gender: String, age: String)] = [
        ("zh-CN-XiaoxiaoNeural", "晓晓 · 温暖自然女声", "zh-CN", "female", "young"),
        ("zh-CN-YunxiNeural", "云希 · 阳光清朗男声", "zh-CN", "male", "young"),
        ("zh-CN-YunjianNeural", "云健 · 沉稳播讲男声", "zh-CN", "male", "middle_aged"),
        ("zh-CN-XiaoyiNeural", "晓伊 · 灵动活泼女声", "zh-CN", "female", "young"),
        ("zh-CN-YunyangNeural", "云扬 · 新闻播报男声", "zh-CN", "male", "middle_aged"),
        ("zh-CN-YunxiaNeural", "云夏 · 少年清亮男声", "zh-CN", "male", "young"),
        ("zh-CN-liaoning-XiaobeiNeural", "晓北 · 东北风趣女声", "zh-CN", "female", "young"),
        ("zh-CN-shaanxi-XiaoniNeural", "晓妮 · 陕西温婉女声", "zh-CN", "female", "young"),
        ("zh-TW-HsiaoChenNeural", "曉臻 · 台湾温润女声", "zh-TW", "female", "young"),
        ("zh-TW-YunJheNeural", "雲哲 · 台湾沉稳男声", "zh-TW", "male", "middle_aged"),
        ("zh-HK-HiuMaanNeural", "曉曼 · 粤语自然女声", "zh-HK", "female", "young"),
        ("zh-HK-WanLungNeural", "雲龍 · 粤语沉稳男声", "zh-HK", "male", "middle_aged"),
        ("en-US-AvaMultilingualNeural", "Ava · Natural Multilingual", "en-US", "female", "young"),
        ("en-US-AndrewMultilingualNeural", "Andrew · Warm Multilingual", "en-US", "male", "middle_aged"),
        ("en-US-EmmaMultilingualNeural", "Emma · Clear Multilingual", "en-US", "female", "young"),
        ("en-US-BrianMultilingualNeural", "Brian · Calm Multilingual", "en-US", "male", "middle_aged"),
        ("en-US-AriaNeural", "Aria · Expressive American", "en-US", "female", "young"),
        ("en-US-GuyNeural", "Guy · Deep American", "en-US", "male", "middle_aged"),
        ("en-GB-SoniaNeural", "Sonia · British Female", "en-GB", "female", "young"),
        ("en-GB-RyanNeural", "Ryan · British Male", "en-GB", "male", "middle_aged"),
        ("ja-JP-NanamiNeural", "七海 · 自然日本語女声", "ja-JP", "female", "young"),
        ("ja-JP-KeitaNeural", "圭太 · 自然日本語男声", "ja-JP", "male", "young"),
    ]

    static let freeOnlineVoices: [(id: String, name: String, lang: String, gender: String, age: String)] = [
        ("zh_baidu", "知声 · 清晰播报", "zh-CN", "female", "middle_aged"),
        ("en_us", "Emma · American", "en-US", "female", "young"),
        ("en_uk", "Clara · British", "en-GB", "female", "young"),
    ]

    static let volcengineVoices: [(id: String, name: String, lang: String, gender: String, age: String)] = [
        ("BV700_V2_streaming", "灿灿 2.0 · 自然温暖女声", "zh-CN", "female", "young"),
        ("BV705_streaming", "炀炀 · 阳光自然男声", "zh-CN", "male", "young"),
        ("BV406_V2_streaming", "梓梓 2.0 · 灵动清亮女声", "zh-CN", "female", "young"),
        ("BV001_V2_streaming", "通用女声 2.0 · 沉稳旁白", "zh-CN", "female", "middle_aged"),
        ("BV002_V2_streaming", "通用男声 2.0 · 醇厚旁白", "zh-CN", "male", "middle_aged"),
        ("BV102_streaming", "儒雅青年 · 温和男声", "zh-CN", "male", "young"),
    ]

    static let fishPresets: [(id: String, name: String, gender: String, age: String)] = [
        ("default", "OpenAudio S1 · 默认自然旁白", "female", "young"),
    ]

    static let openAIVoices = ["alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer"]
    static let openAIGender: [String: String] = [
        "alloy": "female", "ash": "male", "ballad": "male", "coral": "female", "echo": "male",
        "fable": "male", "nova": "female", "onyx": "male", "sage": "female", "shimmer": "female",
    ]

    static let xiaomiVoices: [(id: String, name: String, lang: String, gender: String, age: String)] = [
        ("冰糖", "冰糖 · 清甜活泼", "zh-CN", "female", "young"),
        ("茉莉", "茉莉 · 温柔细腻", "zh-CN", "female", "young"),
        ("苏打", "苏打 · 清爽阳光", "zh-CN", "male", "young"),
        ("白桦", "白桦 · 沉稳厚重", "zh-CN", "male", "middle_aged"),
        ("Mia", "Mia · 自然女声", "en-US", "female", "young"),
        ("Chloe", "Chloe · 知性女声", "en-US", "female", "middle_aged"),
        ("Milo", "Milo · 明亮男声", "en-US", "male", "young"),
        ("Dean", "Dean · 浑厚男声", "en-US", "male", "middle_aged"),
    ]

    static let siliconFlowPresets: [(id: String, name: String, gender: String, age: String)] = [
        ("FunAudioLLM/CosyVoice2-0.5B:anna", "安娜 · 沉稳女声", "female", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:claire", "克莱尔 · 温柔女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:bella", "贝拉 · 热情女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:diana", "黛安娜 · 欢快女声", "female", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:alex", "亚历克斯 · 沉稳男声", "male", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:charles", "查尔斯 · 磁性男声", "male", "middle_aged"),
        ("FunAudioLLM/CosyVoice2-0.5B:david", "大卫 · 明亮男声", "male", "young"),
        ("FunAudioLLM/CosyVoice2-0.5B:benjamin", "本杰明 · 低沉男声", "male", "old"),
    ]

    static let dashScopeVoices: [(id: String, name: String, gender: String, age: String)] = [
        ("Cherry", "芊悦 · 阳光女声", "female", "young"),
        ("Serena", "苏瑶 · 温婉女声", "female", "young"),
        ("Ethan", "晨煦 · 温暖男声", "male", "young"),
        ("Chelsie", "千雪 · 灵动女声", "female", "young"),
    ]

    static let miniMaxPresets: [(id: String, name: String, gender: String, age: String)] = [
        ("female-tianmei", "温柔甜美 · 女声", "female", "young"),
        ("female-yujie", "知性御姐 · 女声", "female", "middle_aged"),
        ("female-shaonv", "元气少女 · 女声", "female", "young"),
        ("male-qn-qingse", "青涩青年 · 男声", "male", "young"),
        ("male-qn-jingying", "精英沉稳 · 男声", "male", "middle_aged"),
    ]

    static let deepgramVoices: [(id: String, name: String, gender: String, age: String)] = [
        ("aura-2-thalia-en", "Thalia · 温暖女声", "female", "young"),
        ("aura-2-andromeda-en", "Andromeda · 知性女声", "female", "middle_aged"),
        ("aura-2-helena-en", "Helena · 柔和女声", "female", "young"),
        ("aura-2-apollo-en", "Apollo · 沉稳男声", "male", "middle_aged"),
        ("aura-2-aries-en", "Aries · 明亮男声", "male", "young"),
    ]

    static let defaultLocalVoices: [(id: String, name: String, gender: String)] = [
        ("default", "默认音色", ""),
        ("alloy", "Alloy", "female"),
        ("nova", "Nova", "female"),
        ("onyx", "Onyx", "male"),
    ]

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
            if let last = plain.last, let n = last.wholeNumberValue { return "\(plain.dropLast()) \(n)" }
            return "\(plain) \(tone)"
        }.joined(separator: " ")
    }

    // MARK: Voice Catalog & Refresh

    static func all() -> [CloudVoice] {
        Provider.allCases.filter(\.isSetUp).flatMap { p -> [CloudVoice] in
            switch p {
            case .edge:
                return edgeVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: $0.lang, gender: $0.gender, age: $0.age) }
            case .free:
                return freeOnlineVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: $0.lang, gender: $0.gender, age: $0.age) }
            case .volcengine:
                return volcengineVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: $0.lang, gender: $0.gender, age: $0.age) }
            case .fish:
                let custom = savedTuples(for: p)
                let presets = fishPresets.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender, age: $0.age) }
                return custom.isEmpty ? presets : (presets + custom)
            case .openai:
                return openAIVoices.map { CloudVoice(provider: p, voiceID: $0, name: $0.capitalized, language: nil) }
            case .xiaomi:
                return xiaomiVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: $0.lang, gender: $0.gender, age: $0.age) }
            case .dashscope:
                return dashScopeVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender, age: $0.age) }
            case .siliconflow:
                let custom = savedTuples(for: p)
                let presets = siliconFlowPresets.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender, age: $0.age) }
                return custom + presets
            case .minimax:
                let custom = savedTuples(for: p)
                let presets = miniMaxPresets.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender, age: $0.age) }
                return custom + presets
            case .azure, .elevenlabs:
                return savedTuples(for: p)
            case .deepgram:
                return deepgramVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: "en-US", gender: $0.gender, age: $0.age) }
            case .voicestudio, .voicebox, .mlxaudio, .speaches, .local:
                let probed = LocalVoiceProbe.shared.discoveredVoices(for: p)
                if !probed.isEmpty {
                    return probed.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender.isEmpty ? nil : $0.gender) }
                }
                let saved = savedTuples(for: p)
                if !saved.isEmpty { return saved }
                return defaultLocalVoices.map { CloudVoice(provider: p, voiceID: $0.id, name: $0.name, language: nil, gender: $0.gender.isEmpty ? nil : $0.gender) }
            }
        }
    }

    private static func savedTuples(for p: Provider) -> [CloudVoice] {
        let saved = UserDefaults.standard.array(forKey: p.voicesName) as? [[String]] ?? []
        return saved.compactMap { v in
            guard v.count >= 2 else { return nil }
            func at(_ i: Int) -> String? { v.count > i && !v[i].isEmpty ? v[i] : nil }
            return CloudVoice(provider: p, voiceID: v[0], name: v[1], language: at(2), gender: at(3), age: at(4))
        }
    }

    /// Checks credentials / connectivity, fetches dynamic voices where supported, and caches them.
    static func refresh(_ p: Provider) async throws -> Int {
        switch p {
        case .edge:
            _ = try await fetchEdge(text: "你好，我来给你读这本书。", voiceID: "zh-CN-XiaoxiaoNeural", language: "zh-CN")
            return edgeVoices.count

        case .free:
            _ = try await fetchFreeOnline(text: "你好，我来给你读这本书。", voiceID: "zh_baidu", language: "zh-CN")
            return freeOnlineVoices.count

        case .volcengine:
            guard let (appID, token) = volcengineCredentials() else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://openspeech.bytedance.com/api/v1/tts")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 15
            request.setValue("Bearer;\(token)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let body: [String: Any] = [
                "app": ["appid": appID, "token": token, "cluster": "volcano_tts"],
                "user": ["uid": "yuyue"],
                "audio": ["voice_type": "BV700_V2_streaming", "encoding": "mp3", "speed_ratio": 1.0],
                "request": ["reqid": UUID().uuidString, "text": "好", "operation": "query"],
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: body)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            let apiCode = (json?["code"] as? Int) ?? 0
            guard apiCode == 3000 else {
                if apiCode == 3001 || apiCode == 3003 { throw Failure.badKey(p) }
                throw Failure.server(p, apiCode, json?["message"] as? String)
            }
            return volcengineVoices.count

        case .fish:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.fish.audio/model?page_size=24&sort_by=task_count")!)
            request.timeoutInterval = 15
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let items = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["items"] as? [[String: Any]] ?? []
            let fetched: [[String]] = items.compactMap { item in
                guard let id = (item["_id"] as? String) ?? (item["id"] as? String), !id.isEmpty,
                      let title = item["title"] as? String, !title.isEmpty else { return nil }
                return [id, title, "", "", ""]
            }
            UserDefaults.standard.set(fetched, forKey: p.voicesName)
            return fishPresets.count + fetched.count

        case .openai:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.openai.com/v1/models/gpt-4o-mini-tts")!)
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            return openAIVoices.count

        case .xiaomi:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.xiaomimimo.com/v1/models")!)
            request.timeoutInterval = 15
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            return xiaomiVoices.count

        case .siliconflow:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.siliconflow.cn/v1/audio/voice/list")!)
            request.timeoutInterval = 15
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let results = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["results"] as? [[String: Any]] ?? []
            let custom: [[String]] = results.compactMap { item in
                guard let uri = item["uri"] as? String, !uri.isEmpty else { return nil }
                let model = (item["model"] as? String) ?? "FunAudioLLM/CosyVoice2-0.5B"
                let name = (item["customName"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? uri
                return ["\(model)|\(uri)", "\(name) · 我的克隆", "", "", ""]
            }
            UserDefaults.standard.set(custom, forKey: p.voicesName)
            return siliconFlowPresets.count + custom.count

        case .dashscope:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://dashscope.aliyuncs.com/compatible-mode/v1/models")!)
            request.timeoutInterval = 15
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            return dashScopeVoices.count

        case .minimax:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.minimaxi.com/v1/t2a_v2")!)
            request.httpMethod = "POST"
            request.timeoutInterval = 15
            request.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization")
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let probe: [String: Any] = [
                "model": "speech-02-turbo",
                "text": "好",
                "stream": false,
                "voice_setting": ["voice_id": "female-tianmei"],
                "audio_setting": ["sample_rate": 24000, "bitrate": 32000, "format": "mp3", "channel": 1],
                "output_format": "hex",
            ]
            request.httpBody = try JSONSerialization.data(withJSONObject: probe)
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
            if let baseResp = json?["base_resp"] as? [String: Any],
               let status = baseResp["status_code"] as? Int, status != 0 {
                let msg = baseResp["status_msg"] as? String
                if status == 1004 || status == 2049 { throw Failure.badKey(p) }
                if status == 1008 || status == 1026 { throw Failure.noCredit(p) }
                throw Failure.server(p, status, msg)
            }
            return miniMaxPresets.count

        case .azure:
            guard let key = p.key else { throw Failure.noKey(p) }
            let region = (UserDefaults.standard.string(forKey: Provider.regionName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            guard !region.isEmpty, let url = URL(string: "https://\(region).tts.speech.microsoft.com/cognitiveservices/voices/list") else {
                throw URLError(.badURL)
            }
            var request = URLRequest(url: url)
            request.setValue(key, forHTTPHeaderField: "Ocp-Apim-Subscription-Key")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            let list = (try? JSONSerialization.jsonObject(with: data)) as? [[String: Any]] ?? []
            let voices: [[String]] = list.compactMap { v in
                guard let short = v["ShortName"] as? String, let locale = v["Locale"] as? String,
                      locale.hasPrefix("zh") || locale.hasPrefix("en") || locale.hasPrefix("ja") else { return nil }
                return [short, (v["LocalName"] as? String ?? short), locale, (v["Gender"] as? String ?? "").lowercased()]
            }
            UserDefaults.standard.set(voices, forKey: p.voicesName)
            return voices.count

        case .elevenlabs:
            guard let key = p.key else { throw Failure.noKey(p) }
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

        case .deepgram:
            guard let key = p.key else { throw Failure.noKey(p) }
            var request = URLRequest(url: URL(string: "https://api.deepgram.com/v1/projects")!)
            request.timeoutInterval = 15
            request.setValue("Token \(key)", forHTTPHeaderField: "Authorization")
            let (data, response) = try await URLSession.shared.data(for: request)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code) else { throw failure(p, code, data) }
            return deepgramVoices.count

        case .voicestudio, .voicebox, .mlxaudio, .speaches, .local:
            let base = normalizedBaseV1(p.savedURL)
            guard !base.isEmpty else { throw Failure.noURL(p) }
            let info = try await LocalVoiceProbe.inspectEndpoint(baseV1: base, key: p.key, timeout: 4.0)
            let rows: [[String]] = info.voices.map { [$0.id, $0.name, "", $0.gender, ""] }
            UserDefaults.standard.set(rows, forKey: p.voicesName)
            if !info.firstModel.isEmpty, (UserDefaults.standard.string(forKey: p.modelName) ?? "").isEmpty {
                UserDefaults.standard.set(info.firstModel, forKey: p.modelName)
            }
            LocalVoiceProbe.shared.recordManualSuccess(p, baseV1: base, info: info)
            return max(rows.count, defaultLocalVoices.count)
        }
    }

    static func volcengineCredentials() -> (appID: String, token: String)? {
        guard let rawKey = Provider.volcengine.key?.trimmingCharacters(in: .whitespacesAndNewlines), !rawKey.isEmpty else {
            return nil
        }
        if let pipe = rawKey.firstIndex(of: "|") {
            let app = String(rawKey[..<pipe]).trimmingCharacters(in: .whitespaces)
            let tok = String(rawKey[rawKey.index(after: pipe)...]).trimmingCharacters(in: .whitespaces)
            if !app.isEmpty, !tok.isEmpty { return (app, tok) }
        }
        let savedApp = (UserDefaults.standard.string(forKey: Provider.volcAppIDName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard !savedApp.isEmpty else { return nil }
        return (savedApp, rawKey)
    }

    /// 免 Key 微软 Edge 自然神经语音：默认直连 Edge Read Aloud WebSocket（v2 Sec-MS-GEC）；
    /// 也支持用户填入自建 OpenAI `/v1/audio/speech` 兼容网关（如 `openai-edge-tts`）。
    static func fetchEdge(text: String, voiceID: String, language: String) async throws -> (Data, String) {
        let customBase = (UserDefaults.standard.string(forKey: Provider.edge.urlName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if !customBase.isEmpty, let endpoint = speechEndpoint(from: customBase) {
            var req = URLRequest(url: endpoint, timeoutInterval: 25)
            req.httpMethod = "POST"
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let model = (UserDefaults.standard.string(forKey: Provider.edge.modelName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let payload: [String: Any] = [
                "model": model.isEmpty ? "tts-1" : model,
                "voice": voiceID,
                "input": text,
                "response_format": "mp3",
            ]
            req.httpBody = try JSONSerialization.data(withJSONObject: payload)
            let (data, resp) = try await URLSession.shared.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code), !data.isEmpty else { throw failure(.edge, code, data) }
            return (data, sniffExt(data, fallback: "mp3"))
        }

        do {
            let audio = try await EdgeTTS.synthesize(text: text, voiceID: voiceID, language: language)
            guard looksLikeAudio(audio) else { throw Failure.unreadable }
            return (audio, sniffExt(audio, fallback: "mp3"))
        } catch is CancellationError {
            throw CancellationError()
        } catch let EdgeTTS.EdgeError.httpStatus(code, _) {
            throw Failure.server(.edge, code, nil)
        } catch EdgeTTS.EdgeError.emptyAudio {
            throw Failure.unreadable
        } catch {
            throw Failure.server(.edge, 0, error.localizedDescription)
        }
    }

    /// 免 Key 在线自然语音：支持自定免密兼容网关，默认直连与当前所选音色严格对应的公共 MP3 语音接口，
    /// 不跨线路、不跨模型静默换声，连不上时直接向上报错告知用户。
    static func fetchFreeOnline(text: String, voiceID: String, language: String) async throws -> (Data, String) {
        let customBase = (UserDefaults.standard.string(forKey: Provider.free.urlName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if !customBase.isEmpty, let endpoint = speechEndpoint(from: customBase) {
            var req = URLRequest(url: endpoint, timeoutInterval: 25)
            req.httpMethod = "POST"
            req.setValue("application/json", forHTTPHeaderField: "Content-Type")
            let model = (UserDefaults.standard.string(forKey: Provider.free.modelName) ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
            let payload: [String: Any] = [
                "model": model.isEmpty ? "kokoro" : model,
                "voice": voiceID,
                "input": text,
                "response_format": "mp3",
            ]
            req.httpBody = try JSONSerialization.data(withJSONObject: payload)
            let (data, resp) = try await URLSession.shared.data(for: req)
            let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
            guard (200 ..< 300).contains(code), !data.isEmpty else { throw failure(.free, code, data) }
            return (data, sniffExt(data, fallback: "mp3"))
        }

        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { throw Failure.unreadable }

        let hasCJK = clean.unicodeScalars.contains { (0x4E00 ... 0x9FFF).contains($0.value) }
        let isEnglish = !hasCJK && (
            language.lowercased().hasPrefix("en")
                || voiceID.hasPrefix("en_")
                || voiceID == "0" || voiceID == "1" || voiceID == "2"
                || voiceID.hasPrefix("af_") || voiceID.hasPrefix("am_") || voiceID.hasPrefix("bf_")
        )

        let chunks = splitFreeChunks(clean, maxChars: 160)
        // 每个免费音色严格绑定单一线路，避免单句超时后静默切到另一家的不同声线。
        // dict.youdao.com/dictvoice 仅支持单词词典发音、整句会返回 HTTP 500，统一走百度整句语音线路。
        let route: FreeRoute
        if isEnglish {
            let preferUK = voiceID == "en_uk" || voiceID.hasPrefix("bf_")
            route = preferUK ? .baiduEnUk : .baiduEnUs
        } else {
            route = .baiduZh
        }

        var lastError: Error = Failure.unreachable(.free)
        for attempt in 0 ..< 2 {
            try Task.checkCancellation()
            if attempt > 0 {
                try await Task.sleep(for: .milliseconds(250))
            }
            do {
                var parts: [Data] = []
                parts.reserveCapacity(chunks.count)
                for chunk in chunks {
                    try Task.checkCancellation()
                    guard let url = freeRouteURL(route, text: chunk) else { throw Failure.unreadable }
                    var req = URLRequest(url: url, timeoutInterval: 12)
                    req.httpMethod = "GET"
                    req.setValue("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15", forHTTPHeaderField: "User-Agent")
                    let (data, resp) = try await URLSession.shared.data(for: req)
                    let code = (resp as? HTTPURLResponse)?.statusCode ?? 0
                    guard (200 ..< 300).contains(code), looksLikeAudio(data) else {
                        throw Failure.server(.free, code, nil)
                    }
                    parts.append(data)
                }
                if !parts.isEmpty {
                    return try combineAudioChunks(parts)
                }
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                lastError = error
            }
        }
        throw lastError
    }

    private enum FreeRoute {
        case youdaoZh, baiduZh, youdaoEnUk, baiduEnUs, baiduEnUk
    }

    private static func freeRouteURL(_ route: FreeRoute, text: String) -> URL? {
        var comps = URLComponents()
        comps.scheme = "https"
        switch route {
        case .youdaoZh:
            comps.host = "dict.youdao.com"
            comps.path = "/dictvoice"
            comps.queryItems = [
                URLQueryItem(name: "le", value: "zh"),
                URLQueryItem(name: "audio", value: text),
            ]
        case .youdaoEnUk:
            comps.host = "dict.youdao.com"
            comps.path = "/dictvoice"
            comps.queryItems = [
                URLQueryItem(name: "le", value: "en"),
                URLQueryItem(name: "type", value: "1"),
                URLQueryItem(name: "audio", value: text),
            ]
        case .baiduZh:
            comps.host = "fanyi.baidu.com"
            comps.path = "/gettts"
            comps.queryItems = [
                URLQueryItem(name: "lan", value: "zh"),
                URLQueryItem(name: "spd", value: "5"),
                URLQueryItem(name: "source", value: "web"),
                URLQueryItem(name: "text", value: text),
            ]
        case .baiduEnUs:
            comps.host = "fanyi.baidu.com"
            comps.path = "/gettts"
            comps.queryItems = [
                URLQueryItem(name: "lan", value: "en"),
                URLQueryItem(name: "spd", value: "3"),
                URLQueryItem(name: "source", value: "web"),
                URLQueryItem(name: "text", value: text),
            ]
        case .baiduEnUk:
            comps.host = "fanyi.baidu.com"
            comps.path = "/gettts"
            comps.queryItems = [
                URLQueryItem(name: "lan", value: "uk"),
                URLQueryItem(name: "spd", value: "3"),
                URLQueryItem(name: "source", value: "web"),
                URLQueryItem(name: "text", value: text),
            ]
        }
        return comps.url
    }

    private static func splitFreeChunks(_ text: String, maxChars: Int) -> [String] {
        guard text.count > maxChars else { return [text] }
        let breaks: Set<Character> = ["。", "！", "？", "；", "，", ".", "!", "?", ";", ","]
        var chunks: [String] = []
        var current = ""
        for ch in text {
            current.append(ch)
            if current.count >= maxChars || (current.count >= maxChars * 2 / 3 && breaks.contains(ch)) {
                let t = current.trimmingCharacters(in: .whitespacesAndNewlines)
                if !t.isEmpty { chunks.append(t) }
                current = ""
            }
        }
        let rem = current.trimmingCharacters(in: .whitespacesAndNewlines)
        if !rem.isEmpty { chunks.append(rem) }
        return chunks.isEmpty ? [text] : chunks
    }

    /// Combines multiple synthesized audio chunks cleanly.
    /// For MP3 chunks, strips ID3v2 headers, trailing ID3v1 tags, and Xing/Info/VBRI header
    /// frames so the concatenated stream has no mid-stream tags or single-chunk frame counts.
    /// For non-MP3 multi-chunk responses, decodes and joins PCM into a single WAV.
    static func combineAudioChunks(_ parts: [Data]) throws -> (Data, String) {
        guard !parts.isEmpty else { throw Failure.unreadable }
        if parts.count == 1 {
            return (parts[0], sniffExt(parts[0], fallback: "mp3"))
        }
        if parts.allSatisfy({ sniffExt($0, fallback: "mp3") == "mp3" }) {
            var combined = Data()
            combined.reserveCapacity(parts.reduce(0) { $0 + $1.count })
            for part in parts {
                combined.append(stripMP3Metadata(part))
            }
            guard !combined.isEmpty else { throw Failure.unreadable }
            return (combined, "mp3")
        }
        return try combineDecodedPCM(parts)
    }

    /// Strips leading ID3v2 tags, trailing ID3v1 tags, and leading Xing/Info/VBRI VBR info
    /// frames from an MP3 buffer, leaving raw MPEG audio frames safe to concatenate.
    static func stripMP3Metadata(_ data: Data) -> Data {
        var start = data.startIndex
        var end = data.endIndex

        // 1. Strip one or more leading ID3v2 headers ("ID3" + 2B version + 1B flags + 4B synchsafe size).
        while end - start >= 10,
              data[start] == 0x49, data[start + 1] == 0x44, data[start + 2] == 0x33,
              data[start + 3] < 0xFF, data[start + 4] < 0xFF,
              data[start + 6] < 0x80, data[start + 7] < 0x80,
              data[start + 8] < 0x80, data[start + 9] < 0x80 {
            let tagSize = (Int(data[start + 6]) << 21)
                | (Int(data[start + 7]) << 14)
                | (Int(data[start + 8]) << 7)
                | Int(data[start + 9])
            let footerSize = (data[start + 5] & 0x10) != 0 ? 10 : 0
            let skip = 10 + tagSize + footerSize
            guard skip > 0, start + skip <= end else { break }
            start += skip
        }

        // 2. Strip trailing 128-byte ID3v1 tag ("TAG...").
        if end - start >= 128,
           data[end - 128] == 0x54, data[end - 127] == 0x41, data[end - 126] == 0x47 {
            end -= 128
        }

        // 3. Sync to first valid MPEG audio frame and skip an optional Xing/Info/VBRI metadata frame.
        var i = start
        while i + 4 <= end {
            let b0 = data[i], b1 = data[i + 1], b2 = data[i + 2], b3 = data[i + 3]
            let version = (b1 >> 3) & 0x03
            let layer = (b1 >> 1) & 0x03
            let bitrateIdx = Int((b2 >> 4) & 0x0F)
            let srIdx = Int((b2 >> 2) & 0x03)
            if b0 == 0xFF, (b1 & 0xE0) == 0xE0,
               version != 1, layer != 0,
               (1 ... 14).contains(bitrateIdx), srIdx < 3 {
                start = i
                if layer == 1 {
                    let padding = Int((b2 >> 1) & 0x01)
                    let channelMode = (b3 >> 6) & 0x03
                    let frameLen: Int
                    let xingOffset: Int
                    if version == 3 {
                        let bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0]
                        let sampleRates = [44100, 48000, 32000]
                        frameLen = (144 * bitrates[bitrateIdx] * 1000) / sampleRates[srIdx] + padding
                        xingOffset = i + 4 + (channelMode == 3 ? 17 : 32)
                    } else {
                        let bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0]
                        let sampleRates = version == 2 ? [22050, 24000, 16000] : [11025, 12000, 8000]
                        frameLen = (72 * bitrates[bitrateIdx] * 1000) / sampleRates[srIdx] + padding
                        xingOffset = i + 4 + (channelMode == 3 ? 9 : 17)
                    }
                    let frameEnd = i + frameLen
                    if frameLen >= 4, frameEnd <= end {
                        let isXingOrInfo = xingOffset + 4 <= frameEnd && (
                            (data[xingOffset] == 0x58 && data[xingOffset + 1] == 0x69 && data[xingOffset + 2] == 0x6E && data[xingOffset + 3] == 0x67)
                            || (data[xingOffset] == 0x49 && data[xingOffset + 1] == 0x6E && data[xingOffset + 2] == 0x66 && data[xingOffset + 3] == 0x6F)
                        )
                        let isVBRI = i + 40 <= frameEnd
                            && data[i + 36] == 0x56 && data[i + 37] == 0x42 && data[i + 38] == 0x52 && data[i + 39] == 0x49
                        if isXingOrInfo || isVBRI {
                            start = frameEnd
                        }
                    }
                }
                break
            }
            i += 1
        }

        return start < end ? Data(data[start ..< end]) : data
    }

    private static func combineDecodedPCM(_ parts: [Data]) throws -> (Data, String) {
        var pcm = Data()
        var targetRate: Double = 0
        for part in parts {
            let ext = sniffExt(part, fallback: "wav")
            let tmp = FileManager.default.temporaryDirectory.appending(path: "voice-chunk-\(UUID().uuidString).\(ext)")
            try part.write(to: tmp)
            defer { try? FileManager.default.removeItem(at: tmp) }
            let file = try AVAudioFile(forReading: tmp)
            guard file.length > 0,
                  let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(file.length)) else {
                throw Failure.unreadable
            }
            try file.read(into: buffer)
            if targetRate == 0 { targetRate = file.processingFormat.sampleRate }
            guard let outFormat = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: targetRate, channels: 1, interleaved: true),
                  let converter = AVAudioConverter(from: buffer.format, to: outFormat) else {
                throw Failure.unreadable
            }
            let ratio = targetRate / max(1, buffer.format.sampleRate)
            let cap = AVAudioFrameCount(ceil(Double(buffer.frameLength) * ratio)) + 64
            guard let outBuffer = AVAudioPCMBuffer(pcmFormat: outFormat, frameCapacity: cap) else {
                throw Failure.unreadable
            }
            var consumed = false
            var convError: NSError?
            let status = converter.convert(to: outBuffer, error: &convError) { _, outStatus in
                if consumed {
                    outStatus.pointee = .endOfStream
                    return nil
                }
                consumed = true
                outStatus.pointee = .haveData
                return buffer
            }
            guard status != .error, convError == nil,
                  outBuffer.frameLength > 0,
                  let ch = outBuffer.int16ChannelData?[0] else {
                throw Failure.unreadable
            }
            pcm.append(UnsafeBufferPointer(start: ch, count: Int(outBuffer.frameLength)))
        }
        guard !pcm.isEmpty, targetRate > 0 else { throw Failure.unreadable }
        return (wav(pcm: pcm, rate: Int(targetRate)), "wav")
    }

    private static func looksLikeAudio(_ data: Data) -> Bool {
        guard data.count > 256 else { return false }
        let b0 = data[0], b1 = data[1], b2 = data[2], b3 = data[3]
        // MP3 frame sync (0xFF 0xE0..0xFF) or ID3 tag
        if b0 == 0xFF && (b1 & 0xE0) == 0xE0 { return true }
        if b0 == 0x49 && b1 == 0x44 && b2 == 0x33 { return true }
        // RIFF WAV / OggS / fLaC
        if b0 == 0x52 && b1 == 0x49 && b2 == 0x46 && b3 == 0x46 { return true }
        if b0 == 0x4F && b1 == 0x67 && b2 == 0x67 && b3 == 0x53 { return true }
        if b0 == 0x66 && b1 == 0x4C && b2 == 0x61 && b3 == 0x43 { return true }
        return false
    }
}

// MARK: - Local Zero-Config Auto-Discovery & Health Probe

/// 本机开源语音服务自动发现（VoiceStudio `:3900`、Voicebox `:17493`、mlx-audio / Speaches `:8000` / `:8880`）。
/// 打开「设置 › 朗读」时静默并发探测，450ms 超时，不阻塞任何界面。
nonisolated final class LocalVoiceProbe: Sendable {
    static let shared = LocalVoiceProbe()

    struct DiscoveredVoice: Sendable, Equatable {
        let id: String
        let name: String
        let gender: String
    }

    struct EndpointInfo: Sendable {
        var voices: [DiscoveredVoice]
        var firstModel: String
    }

    private struct State: Sendable {
        var live: Set<CloudVoice.Provider> = []
        var checked: Set<CloudVoice.Provider> = []
        var urls: [CloudVoice.Provider: String] = [:]
        var models: [CloudVoice.Provider: String] = [:]
        var voices: [CloudVoice.Provider: [DiscoveredVoice]] = [:]
    }

    private let state = Mutex(State())

    func isLive(_ p: CloudVoice.Provider) -> Bool {
        state.withLock { $0.live.contains(p) }
    }

    func wasChecked(_ p: CloudVoice.Provider) -> Bool {
        state.withLock { $0.checked.contains(p) }
    }

    func discoveredURL(for p: CloudVoice.Provider) -> String? {
        state.withLock { $0.urls[p] }
    }

    func discoveredModel(for p: CloudVoice.Provider) -> String? {
        state.withLock { $0.models[p] }
    }

    func discoveredVoices(for p: CloudVoice.Provider) -> [DiscoveredVoice] {
        state.withLock { $0.voices[p] ?? [] }
    }

    func recordManualSuccess(_ p: CloudVoice.Provider, baseV1: String, info: EndpointInfo) {
        state.withLock { st in
            st.live.insert(p)
            st.checked.insert(p)
            st.urls[p] = baseV1
            if !info.firstModel.isEmpty { st.models[p] = info.firstModel }
            if !info.voices.isEmpty { st.voices[p] = info.voices }
        }
    }

    func clear(_ p: CloudVoice.Provider) {
        state.withLock { st in
            st.live.remove(p)
            st.voices.removeValue(forKey: p)
        }
    }

    /// 扫描本机常见端口及已添加的局域网服务健康状态；有变化时返回 `true` 以便刷新界面。
    @concurrent func scanAll() async -> Bool {
        let targets: [(CloudVoice.Provider, [String])] = {
            var list: [(CloudVoice.Provider, [String])] = []
            #if os(macOS)
            let vsCustom = UserDefaults.standard.string(forKey: CloudVoice.Provider.voicestudio.urlName) ?? ""
            list.append((.voicestudio, [vsCustom.isEmpty ? "http://127.0.0.1:3900/v1" : CloudVoice.normalizedBaseV1(vsCustom)]))

            let vbCustom = UserDefaults.standard.string(forKey: CloudVoice.Provider.voicebox.urlName) ?? ""
            list.append((.voicebox, [vbCustom.isEmpty ? "http://127.0.0.1:17493/v1" : CloudVoice.normalizedBaseV1(vbCustom)]))

            for p in [CloudVoice.Provider.mlxaudio, .speaches, .local] where UserDefaults.standard.bool(forKey: p.enabledName) {
                let u = UserDefaults.standard.string(forKey: p.urlName) ?? p.defaultURL
                if !u.isEmpty { list.append((p, [CloudVoice.normalizedBaseV1(u)])) }
            }
            if !list.contains(where: { $0.0 == .mlxaudio || $0.0 == .speaches || $0.0 == .local }) {
                list.append((.mlxaudio, ["http://127.0.0.1:8000/v1"]))
            }
            #else
            for p in [CloudVoice.Provider.voicestudio, .voicebox, .mlxaudio, .speaches, .local] {
                let u = UserDefaults.standard.string(forKey: p.urlName) ?? ""
                if !u.isEmpty { list.append((p, [CloudVoice.normalizedBaseV1(u)])) }
            }
            #endif
            return list
        }()

        guard !targets.isEmpty else { return false }
        let before = state.withLock { $0.live }

        await withTaskGroup(of: (CloudVoice.Provider, String?, EndpointInfo?).self) { group in
            for (provider, candidates) in targets {
                let key = provider.key
                group.addTask {
                    for base in candidates where !base.isEmpty {
                        if let info = try? await Self.inspectEndpoint(baseV1: base, key: key, timeout: 0.55) {
                            return (provider, base, info)
                        }
                    }
                    return (provider, nil, nil)
                }
            }
            for await (provider, hitBase, info) in group {
                state.withLock { st in
                    st.checked.insert(provider)
                    if let hitBase, let info {
                        st.live.insert(provider)
                        st.urls[provider] = hitBase
                        if !info.firstModel.isEmpty { st.models[provider] = info.firstModel }
                        if !info.voices.isEmpty { st.voices[provider] = info.voices }
                    } else {
                        st.live.remove(provider)
                    }
                }
            }
        }

        let after = state.withLock { $0.live }
        return before != after
    }

    /// 探测单个 OpenAI 兼容语音服务端点，兼容 `/v1/audio/voices`、`/v1/voices`、Voicebox `/profiles` 与 `/v1/models`。
    static func inspectEndpoint(baseV1: String, key: String?, timeout: TimeInterval) async throws -> EndpointInfo {
        let root = baseV1.hasSuffix("/v1") ? String(baseV1.dropLast(3)) : baseV1
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = timeout
        config.timeoutIntervalForResource = timeout
        let session = URLSession(configuration: config)

        func getJSON(_ urlStr: String) async -> Any? {
            guard let url = URL(string: urlStr) else { return nil }
            var req = URLRequest(url: url, timeoutInterval: timeout)
            if let key, !key.isEmpty { req.setValue("Bearer \(key)", forHTTPHeaderField: "Authorization") }
            guard let (data, resp) = try? await session.data(for: req),
                  let code = (resp as? HTTPURLResponse)?.statusCode,
                  (200 ..< 300).contains(code) else { return nil }
            return try? JSONSerialization.jsonObject(with: data)
        }

        var foundVoices: [DiscoveredVoice] = []
        var firstModel = ""
        var anyReachable = false

        // 1. Try /v1/audio/voices (VoiceStudio, Kokoro-FastAPI, Speaches) & /v1/voices
        for path in ["\(baseV1)/audio/voices", "\(baseV1)/voices", "\(root)/profiles"] {
            if let json = await getJSON(path) {
                anyReachable = true
                let parsed = parseVoices(from: json)
                if !parsed.isEmpty {
                    foundVoices = parsed
                    break
                }
            }
        }

        // 2. Try /v1/models (Voicebox, mlx-audio, LocalAI, Speaches, VoiceStudio)
        if let json = await getJSON("\(baseV1)/models") {
            anyReachable = true
            if let dict = json as? [String: Any], let arr = dict["data"] as? [[String: Any]] {
                let ids = arr.compactMap { $0["id"] as? String }.filter { !$0.isEmpty }
                // Exclude pure LLM servers like plain Ollama unless a speech/tts model is present or explicitly configured
                let looksLikeSpeech = ids.contains { id in
                    let l = id.lowercased()
                    return l.contains("tts") || l.contains("voice") || l.contains("kokoro")
                        || l.contains("cosy") || l.contains("speech") || l.contains("chatterbox")
                        || l.contains("omni") || l.contains("melo") || l.contains("piper")
                }
                if let m = ids.first {
                    firstModel = m
                }
                if foundVoices.isEmpty && !looksLikeSpeech && timeout < 1.0 {
                    // During passive port scan, don't misidentify a pure text LLM server on :8000 as TTS.
                    anyReachable = false
                }
            }
        }

        guard anyReachable else { throw URLError(.cannotConnectToHost) }
        if foundVoices.isEmpty {
            foundVoices = CloudVoice.defaultLocalVoices.map { DiscoveredVoice(id: $0.id, name: $0.name, gender: $0.gender) }
        }
        return EndpointInfo(voices: foundVoices, firstModel: firstModel)
    }

    private static func parseVoices(from json: Any) -> [DiscoveredVoice] {
        var rawItems: [Any] = []
        if let arr = json as? [Any] {
            rawItems = arr
        } else if let dict = json as? [String: Any] {
            if let v = dict["voices"] as? [Any] { rawItems = v }
            else if let d = dict["data"] as? [Any] { rawItems = d }
            else if let p = dict["profiles"] as? [Any] { rawItems = p }
        }
        var seen = Set<String>()
        var out: [DiscoveredVoice] = []
        for item in rawItems {
            if let s = item as? String, !s.isEmpty, seen.insert(s).inserted {
                out.append(DiscoveredVoice(id: s, name: s, gender: guessGender(s)))
            } else if let d = item as? [String: Any] {
                let id = (d["voice_id"] as? String) ?? (d["id"] as? String) ?? (d["name"] as? String) ?? ""
                guard !id.isEmpty, seen.insert(id).inserted else { continue }
                let name = (d["name"] as? String) ?? (d["title"] as? String) ?? id
                let gender = ((d["gender"] as? String) ?? guessGender(id + " " + name)).lowercased()
                out.append(DiscoveredVoice(id: id, name: name, gender: gender))
            }
        }
        return out
    }

    private static func guessGender(_ text: String) -> String {
        let l = text.lowercased()
        if l.hasPrefix("af_") || l.hasPrefix("bf_") || l.hasPrefix("zf_") || l.contains("female") || l.contains("女") { return "female" }
        if l.hasPrefix("am_") || l.hasPrefix("bm_") || l.hasPrefix("zm_") || l.contains("male") || l.contains("男") { return "male" }
        return ""
    }
}
