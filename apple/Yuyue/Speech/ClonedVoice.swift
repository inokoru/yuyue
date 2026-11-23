import AVFoundation
import Foundation
import YuyueCore

/// 克隆声音: 本机零样本克隆声音卡（`VoiceCard`，录音不出设备），以及可选的 ElevenLabs 云端克隆备份。
nonisolated struct ClonedVoice: Voice {
    nonisolated struct Meta: Codable, Sendable {
        var id: String
        var name: String
        var created: Date
        /// 录音念的文字（导入或旧版本可能为空，迁移时尝试本机听写补齐）。
        var text: String?
        var seconds: Double
        /// ElevenLabs 侧的 voice_id（仅在用户主动开启“也用 ElevenLabs 念”或旧版已建时存在）。
        var remote: String?
        var engine: String?
        var useElevenLabs: Bool?
        var needsRerecord: Bool?
    }

    let meta: Meta
    var id: String { meta.id }
    var name: String { meta.name }
    var qualityName: String? { String(localized: "克隆") }
    var isOffline: Bool { false }
    /// ElevenLabs 备份是否可用。
    var isReady: Bool { meta.remote != nil && CloneEngine.available }

    func speaks(_ language: String) -> Bool { true }

    func render(_ text: String, language: String) async throws -> Speech? {
        guard let remote = meta.remote else { throw CloneEngine.Failure.notReady }
        return try await CloudVoice(provider: .elevenlabs, voiceID: remote, name: name, language: nil).render(text, language: language)
    }
}

/// 列表与详情页展示的一条克隆声音项（合并本机 `VoiceCard` 与可选 `Meta`）。
nonisolated struct ClonedEntry: Identifiable, Sendable {
    var id: String
    var name: String
    var card: VoiceCard?
    var meta: ClonedVoice.Meta?

    var needsRerecord: Bool {
        card == nil && (meta?.needsRerecord == true || meta?.remote == nil)
    }

    var isPlayable: Bool {
        if card != nil && LiveVoiceModel.isModelInstalled { return true }
        if let meta, meta.remote != nil, CloneEngine.available { return true }
        return false
    }

    var useElevenLabs: Bool {
        meta?.useElevenLabs ?? false
    }

    var subtitle: String {
        if let card {
            let extra = card.clips.filter { $0.style != .neutral }.count
            if extra > 0 {
                return String(localized: "本机 · 已录 \(extra + 1) 档语气")
            }
            return LiveVoiceModel.isModelInstalled
                ? String(localized: "本机 · 平常语气")
                : (meta?.remote != nil && CloneEngine.available ? "ElevenLabs" : String(localized: "本机 · 等朗读声音下好后可用"))
        }
        if meta?.needsRerecord == true {
            return String(localized: "要重录 · 旧录音缺对应文字")
        }
        if meta?.remote != nil, CloneEngine.available {
            return "ElevenLabs"
        }
        return String(localized: "要重录")
    }
}

enum ClonedVoices {
    static let changed = Notification.Name("yuyue.clonedVoicesChanged")

    nonisolated static var dir: URL {
        if let ctx = LiveVoiceModel.context {
            return ctx.dataDir.appending(path: "clones", directoryHint: .isDirectory)
        }
        return FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Yuyue", directoryHint: .isDirectory)
            .appending(path: "clones", directoryHint: .isDirectory)
    }

    nonisolated static func folder(_ id: String) -> URL { dir.appending(path: id, directoryHint: .isDirectory) }
    nonisolated static func sample(_ id: String) -> URL { folder(id).appending(path: "sample.m4a") }
    nonisolated static func metaURL(_ id: String) -> URL { folder(id).appending(path: "meta.json") }

    /// 旧版与 ElevenLabs 元数据列表。
    nonisolated static func all() -> [ClonedVoice] {
        let ids = (try? FileManager.default.contentsOfDirectory(atPath: dir.path(percentEncoded: false))) ?? []
        return ids.compactMap { id in
            guard let data = try? Data(contentsOf: metaURL(id)),
                  let meta = try? JSONDecoder().decode(ClonedVoice.Meta.self, from: data) else { return nil }
            return ClonedVoice(meta: meta)
        }
        .sorted { $0.meta.created > $1.meta.created }
    }

    nonisolated static func find(_ id: String) -> ClonedVoice? {
        all().first { $0.meta.id == id }
    }

    /// 供 `Voices.find("cloned.xxx")` 路由：
    /// 1. 若用户未强制 ElevenLabs、即时层已装好且本机有 `VoiceCard` → `CardVoice`；
    /// 2. 若填了 ElevenLabs Key 且有 `remote`（且打开了“也用 ElevenLabs 念”或即时层未装好）→ `ClonedVoice`（走 `CloudVoice`）；
    /// 3. 若本机有 `VoiceCard` 且即时层装好（即使打开了 ElevenLabs 但无 Key/remote）→ `CardVoice`；
    /// 4. 否则回 `nil`（由调用方回退旁白）。
    nonisolated static func resolveVoice(_ id: String, bookID: String? = nil) -> (any Voice)? {
        let meta = find(id)?.meta
        let card = LiveVoiceModel.context?.core.cardFind(bookId: bookID, id: id)
        let wantsEleven = meta?.useElevenLabs == true && CloneEngine.available && meta?.remote != nil
        if wantsEleven {
            return meta.map(ClonedVoice.init)
        }
        if LiveVoiceModel.isModelInstalled, let card {
            return CardVoice(card: card, bookID: bookID)
        }
        if let meta, meta.remote != nil, CloneEngine.available {
            return ClonedVoice(meta: meta)
        }
        return meta.map(ClonedVoice.init)
    }

    /// 合并核心里的克隆卡（含跨设备同步过来的）与本地 `meta.json` 项。
    nonisolated static func entries() -> [ClonedEntry] {
        let cards = LiveVoiceModel.context?.core.cardsCloned() ?? []
        let metas = Dictionary(uniqueKeysWithValues: all().map { ($0.meta.id, $0.meta) })
        var seen = Set<String>()
        var out: [ClonedEntry] = []
        for card in cards {
            seen.insert(card.id)
            out.append(ClonedEntry(id: card.id, name: card.name, card: card, meta: metas[card.id]))
        }
        for v in all() where !seen.contains(v.meta.id) {
            out.append(ClonedEntry(id: v.meta.id, name: v.meta.name, card: nil, meta: v.meta))
        }
        return out
    }

    /// 新建本机零样本克隆声音卡（不联网、不要求任何 Key）。
    static func saveLocalCard(
        core: YuyueCore,
        dataDir: URL,
        existingID: String? = nil,
        samples: [Float],
        rate: UInt32,
        text: String,
        lang: String,
        name: String
    ) async throws -> VoiceCard {
        let id = existingID ?? ("cloned." + UUID().uuidString.lowercased())
        let dstDir = folder(id)
        try FileManager.default.createDirectory(at: dstDir, withIntermediateDirectories: true)
        let wavPath = dstDir.appending(path: "neutral.wav").path(percentEncoded: false)
        let secs = try core.cardWriteWav(path: wavPath, samples: samples, rate: rate)

        let prepared = (try? core.cardDecodeClip(path: wavPath, targetRate: 24_000)) ?? samples
        var embedding: [Float] = []
        if SpeakerPrint.installed(dataDir) {
            embedding = await SpeakerPrint.print(core, dataDir, samples: prepared, rate: 24_000) ?? []
        }
        let gender = core.speakerVoiceGender(samples: prepared, rate: 24_000)
        let existing = core.cardFind(bookId: nil, id: id)
        var clips = existing?.clips.filter { $0.style != .neutral } ?? []
        clips.insert(CardClip(style: .neutral, file: "neutral.wav", text: text, seconds: secs), at: 0)

        let card = VoiceCard(
            id: id,
            name: name,
            gender: gender,
            age: existing?.age ?? .unknown,
            tags: existing?.tags ?? [],
            lang: CardVoice.canonicalLanguage(lang),
            origin: .cloned,
            clips: clips,
            description: "",
            version: (existing?.version ?? 0) + 1,
            narrator: false,
            rate: 0,
            embedding: embedding,
            sync: existing?.sync ?? false
        )
        let saved = try core.cardSave(bookId: nil, card: card, sourceDir: dstDir.path(percentEncoded: false))
        if !embedding.isEmpty {
            core.speakerSetPrint(voice: saved.id, embedding: embedding)
        }
        var meta = find(id)?.meta ?? ClonedVoice.Meta(id: id, name: name, created: Date(), text: text, seconds: Double(secs))
        meta.name = name
        meta.text = text
        meta.seconds = Double(secs)
        meta.needsRerecord = false
        try? write(meta)
        await MainActor.run {
            NotificationCenter.default.post(name: changed, object: nil)
        }
        return saved
    }

    /// 在克隆卡详情里补录或重录某一档语气（`version += 1` 自动作废旧缓存）。
    static func saveStyleClip(
        core: YuyueCore,
        dataDir: URL,
        card: VoiceCard,
        style: CardStyle,
        samples: [Float],
        rate: UInt32,
        text: String
    ) async throws -> VoiceCard {
        let dstDir = folder(card.id)
        try FileManager.default.createDirectory(at: dstDir, withIntermediateDirectories: true)
        let fileName = Self.styleFileName(style)
        let wavPath = dstDir.appending(path: fileName).path(percentEncoded: false)
        let secs = try core.cardWriteWav(path: wavPath, samples: samples, rate: rate)

        var updated = card
        updated.clips.removeAll { $0.style == style }
        if style == .neutral {
            updated.clips.insert(CardClip(style: .neutral, file: fileName, text: text, seconds: secs), at: 0)
            if SpeakerPrint.installed(dataDir),
               let decoded = try? core.cardDecodeClip(path: wavPath, targetRate: 24_000),
               let emb = await SpeakerPrint.print(core, dataDir, samples: decoded, rate: 24_000) {
                updated.embedding = emb
                core.speakerSetPrint(voice: updated.id, embedding: emb)
            }
        } else {
            updated.clips.append(CardClip(style: style, file: fileName, text: text, seconds: secs))
        }
        updated.version += 1
        let saved = try core.cardSave(bookId: nil, card: updated, sourceDir: dstDir.path(percentEncoded: false))
        await MainActor.run {
            NotificationCenter.default.post(name: changed, object: nil)
        }
        return saved
    }

    nonisolated static func styleFileName(_ style: CardStyle) -> String {
        switch style {
        case .neutral: "neutral.wav"
        case .joy: "joy.wav"
        case .anger: "anger.wav"
        case .sad: "sad.wav"
        case .tense: "tense.wav"
        case .soft: "soft.wav"
        case .shout: "shout.wav"
        }
    }

    static func setSync(_ card: VoiceCard, _ sync: Bool, core: YuyueCore) throws -> VoiceCard {
        var updated = card
        updated.sync = sync
        let saved = try core.cardSave(bookId: nil, card: updated, sourceDir: folder(card.id).path(percentEncoded: false))
        NotificationCenter.default.post(name: changed, object: nil)
        return saved
    }

    /// 切换“也用 ElevenLabs 念”；打开且尚无 `remote` 时上传参考音创建。
    static func setUseElevenLabs(id: String, name: String, on: Bool) async throws -> ClonedVoice.Meta {
        var meta = find(id)?.meta ?? ClonedVoice.Meta(id: id, name: name, created: Date(), text: nil, seconds: 8)
        meta.name = name
        if on, meta.remote == nil {
            let wav = folder(id).appending(path: "neutral.wav")
            let m4a = sample(id)
            let uploadURL = FileManager.default.fileExists(atPath: wav.path(percentEncoded: false)) ? wav : m4a
            meta.remote = try await CloneEngine.make(name: name, sample: uploadURL)
            meta.engine = CloneEngine.name
        }
        meta.useElevenLabs = on
        try write(meta)
        await MainActor.run {
            NotificationCenter.default.post(name: changed, object: nil)
        }
        return meta
    }

    static func renameCard(id: String, to newName: String, core: YuyueCore) throws {
        let trimmed = newName.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        if var card = core.cardFind(bookId: nil, id: id) {
            card.name = trimmed
            _ = try core.cardSave(bookId: nil, card: card, sourceDir: folder(id).path(percentEncoded: false))
        }
        if var meta = find(id)?.meta {
            meta.name = trimmed
            try write(meta)
        }
        NotificationCenter.default.post(name: changed, object: nil)
    }

    static func deleteCard(id: String, core: YuyueCore) {
        try? core.cardDelete(bookId: nil, cardId: id)
        try? FileManager.default.removeItem(at: folder(id))
        NotificationCenter.default.post(name: changed, object: nil)
    }

    static func rename(_ voice: ClonedVoice, to name: String) throws {
        if let ctx = LiveVoiceModel.context {
            try renameCard(id: voice.id, to: name, core: ctx.core)
        } else {
            var meta = voice.meta
            meta.name = name
            try write(meta)
            NotificationCenter.default.post(name: changed, object: nil)
        }
    }

    static func delete(_ voice: ClonedVoice) {
        if let ctx = LiveVoiceModel.context {
            deleteCard(id: voice.id, core: ctx.core)
        } else {
            try? FileManager.default.removeItem(at: folder(voice.id))
            NotificationCenter.default.post(name: changed, object: nil)
        }
    }

    /// 解码本地任意受系统支持的音频文件（m4a / wav / mp3 / caf）为单声道 `[Float]` 与采样率。
    nonisolated static func decodeAudioFile(_ url: URL) -> (samples: [Float], rate: UInt32)? {
        guard let file = try? AVAudioFile(forReading: url), file.length > 0 else { return nil }
        let format = file.processingFormat
        let count = AVAudioFrameCount(file.length)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: count),
              (try? file.read(into: buffer)) != nil,
              buffer.frameLength > 0,
              let chData = buffer.floatChannelData else { return nil }
        let n = Int(buffer.frameLength)
        let channels = Int(format.channelCount)
        if channels == 1 {
            return (Array(UnsafeBufferPointer(start: chData[0], count: n)), UInt32(format.sampleRate))
        }
        var mono = [Float](repeating: 0, count: n)
        for c in 0 ..< channels {
            let p = chData[c]
            for i in 0 ..< n { mono[i] += p[i] }
        }
        let inv = 1.0 / Float(channels)
        for i in 0 ..< n { mono[i] *= inv }
        return (mono, UInt32(format.sampleRate))
    }

    nonisolated private static func write(_ meta: ClonedVoice.Meta) throws {
        try FileManager.default.createDirectory(at: folder(meta.id), withIntermediateDirectories: true)
        let encoder = JSONEncoder()
        encoder.outputFormatting = .prettyPrinted
        try encoder.encode(meta).write(to: metaURL(meta.id), options: .atomic)
    }
}

/// ElevenLabs 云端即时克隆（可选开启）。
nonisolated enum CloneEngine {
    enum Failure: LocalizedError {
        case notReady
        case engine(String)

        var errorDescription: String? {
            switch self {
            case .notReady: String(localized: "这个克隆的声音还不能念：要先下好朗读声音，或在设置里接上 ElevenLabs")
            case .engine(let m): m
            }
        }
    }

    static let name = "ElevenLabs"
    static var available: Bool { CloudVoice.Provider.elevenlabs.key != nil }

    static func make(name: String, sample: URL) async throws -> String {
        guard let key = CloudVoice.Provider.elevenlabs.key else { throw Failure.notReady }
        let boundary = "yuyue-" + UUID().uuidString
        let isWav = sample.pathExtension.lowercased() == "wav"
        let fileName = isWav ? "sample.wav" : "sample.m4a"
        let mime = isWav ? "audio/wav" : "audio/mp4"
        var body = Data()
        func field(_ k: String, _ v: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(k)\"\r\n\r\n\(v)\r\n".utf8))
        }
        field("name", "语阅 · \(name)")
        field("remove_background_noise", "true")
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"files\"; filename=\"\(fileName)\"\r\nContent-Type: \(mime)\r\n\r\n".utf8))
        body.append(try Data(contentsOf: sample))
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: URL(string: "https://api.elevenlabs.io/v1/voices/add")!)
        request.httpMethod = "POST"
        request.setValue(key, forHTTPHeaderField: "xi-api-key")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        request.timeoutInterval = 60
        let (data, response) = try await URLSession.shared.data(for: request)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200 ..< 300).contains(code) else { throw CloudVoice.failure(.elevenlabs, code, data) }
        guard let id = ((try? JSONSerialization.jsonObject(with: data)) as? [String: Any])?["voice_id"] as? String else {
            throw Failure.engine(String(localized: "ElevenLabs 没有给回声音"))
        }
        return id
    }
}
