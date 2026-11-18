#if os(macOS)
import AVFoundation
import Foundation
import HuggingFace
@preconcurrency import MLX
@preconcurrency import MLXAudioCore
@preconcurrency import MLXAudioTTS
import MLXRandom
import Observation
import YuyueCore

/// Mac 精做层（`Qwen3-TTS 1.7B` 6-bit 量化双模型：Base 照卡克隆 + VoiceDesign 为缺声音的角色造七档声音卡）。
/// 只在 macOS 编译与运行，绝不进入 iOS。
nonisolated enum StudioVoiceModel {
    static let engine = "studio-qwen3tts-1.7b-1"
    static let folderName = "studio-qwen3tts-1"
    static let baseRepo = "mlx-community/Qwen3-TTS-12Hz-1.7B-Base-6bit"
    static let designRepo = "mlx-community/Qwen3-TTS-12Hz-1.7B-VoiceDesign-6bit"
    /// 6-bit 量化双模型（Base + VoiceDesign）合计约 3.2 GB。
    static let estimatedBytes: Int64 = 3_200_000_000
    static let changed = Notification.Name("yuyue.studioVoiceChanged")

    static func folder(_ dataDir: URL) -> URL {
        Aligner.folder(dataDir).appending(path: folderName, directoryHint: .isDirectory)
    }

    static func repoFolder(_ dataDir: URL, repo: String) -> URL {
        let sub = repo.replacingOccurrences(of: "/", with: "_")
        return folder(dataDir).appending(path: "mlx-audio/\(sub)", directoryHint: .isDirectory)
    }

    private static func repoReady(_ dir: URL) -> Bool {
        let fm = FileManager.default
        let configPath = dir.appending(path: "config.json").path(percentEncoded: false)
        var isTokDir: ObjCBool = false
        let tokPath = dir.appending(path: "speech_tokenizer").path(percentEncoded: false)
        guard fm.fileExists(atPath: configPath),
              fm.fileExists(atPath: tokPath, isDirectory: &isTokDir), isTokDir.boolValue,
              let files = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey]) else {
            return false
        }
        return files.contains { url in
            url.pathExtension == "safetensors"
                && ((try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) > 0
        }
    }

    static func installed(_ dataDir: URL) -> Bool {
        repoReady(repoFolder(dataDir, repo: baseRepo)) && repoReady(repoFolder(dataDir, repo: designRepo))
    }

    static var isInstalled: Bool {
        guard let ctx = LiveVoiceModel.context else { return false }
        return installed(ctx.dataDir)
    }

    /// 实算本机缓存目录占用字节数（按文件标识去重，避免硬链接重复计入）。
    static func diskBytes(_ dataDir: URL) -> Int64 {
        let root = folder(dataDir)
        let fm = FileManager.default
        guard let enumerator = fm.enumerator(
            at: root,
            includingPropertiesForKeys: [.isRegularFileKey, .fileAllocatedSizeKey, .fileSizeKey, .fileResourceIdentifierKey],
            options: [.skipsHiddenFiles]
        ) else { return 0 }
        var total: Int64 = 0
        var seen = Set<Data>()
        for case let url as URL in enumerator {
            guard let vals = try? url.resourceValues(forKeys: [.isRegularFileKey, .fileAllocatedSizeKey, .fileSizeKey, .fileResourceIdentifierKey]),
                  vals.isRegularFile == true else { continue }
            if let id = vals.fileResourceIdentifier as? Data, !seen.insert(id).inserted {
                continue
            }
            total += Int64(vals.fileAllocatedSize ?? vals.fileSize ?? 0)
        }
        return total
    }

    static func formatBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: max(bytes, 0), countStyle: .file)
    }

    static func qwenLanguage(for tag: String) -> String {
        tag.lowercased().hasPrefix("en") ? "english" : "chinese"
    }

    static func styleFileName(_ style: CardStyle) -> String {
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

    static func cosine(_ a: [Float], _ b: [Float]) -> Float {
        guard !a.isEmpty, a.count == b.count else { return 1 }
        var dot: Float = 0, na: Float = 0, nb: Float = 0
        for i in a.indices {
            dot += a[i] * b[i]
            na += a[i] * a[i]
            nb += b[i] * b[i]
        }
        let denom = (na.squareRoot() * nb.squareRoot())
        return denom > 1e-7 ? dot / denom : 0
    }
}

/// 管理 Mac 设置页“精做的声音”下载、进度取消、磁盘占用与一键删除。
@MainActor @Observable
final class StudioVoiceCenter {
    static let shared = StudioVoiceCenter()

    enum Phase: Equatable {
        case idle
        case downloading(fraction: Double)
        case ready(bytes: Int64)
        case failed(String)
    }

    private(set) var phase: Phase = .idle
    @ObservationIgnored private var downloadTask: Task<Void, Never>?

    private init() {
        refresh()
    }

    func refresh() {
        guard downloadTask == nil, let ctx = LiveVoiceModel.context else { return }
        if StudioVoiceModel.installed(ctx.dataDir) {
            let used = StudioVoiceModel.diskBytes(ctx.dataDir)
            phase = .ready(bytes: used > 0 ? used : StudioVoiceModel.estimatedBytes)
        } else if case .ready = phase {
            phase = .idle
        }
    }

    func download() {
        guard downloadTask == nil, let ctx = LiveVoiceModel.context else { return }
        if StudioVoiceModel.installed(ctx.dataDir) {
            refresh()
            return
        }
        phase = .downloading(fraction: 0)
        let core = ctx.core
        let dataDir = ctx.dataDir
        downloadTask = Task { [weak self] in
            defer { self?.downloadTask = nil }
            do {
                let root = StudioVoiceModel.folder(dataDir)
                try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
                let cache = HubCache(cacheDirectory: root)
                let client = HubClient(cache: cache)
                let repos = [StudioVoiceModel.baseRepo, StudioVoiceModel.designRepo]
                for (index, repo) in repos.enumerated() {
                    try Task.checkCancellation()
                    guard let repoID = Repo.ID(rawValue: repo) else { continue }
                    let baseFrac = Double(index) * 0.5
                    _ = try await ModelUtils.resolveOrDownloadModel(
                        client: client,
                        cache: cache,
                        repoID: repoID,
                        requiredExtension: "safetensors",
                        additionalMatchingPatterns: ["speech_tokenizer/*"],
                        progressHandler: { [weak self] p in
                            guard let self, case .downloading = self.phase else { return }
                            let step = min(1, max(0, p.fractionCompleted))
                            self.phase = .downloading(fraction: min(0.99, baseFrac + step * 0.5))
                        }
                    )
                    // 快照已落入 `mlx-audio/<repo>`，清理 Hub 临时 blobs 副本以免双倍占盘。
                    let hubRepo = cache.repoDirectory(repo: repoID, kind: .model)
                    try? FileManager.default.removeItem(at: hubRepo)
                }
                let used = StudioVoiceModel.diskBytes(dataDir)
                guard let self else { return }
                self.phase = .ready(bytes: used > 0 ? used : StudioVoiceModel.estimatedBytes)
                DramaWork.tellCore(core)
                NotificationCenter.default.post(name: StudioVoiceModel.changed, object: nil)
            } catch is CancellationError {
                if let self { self.phase = .idle }
            } catch {
                if let self { self.phase = .failed(error.localizedDescription) }
            }
        }
    }

    func cancel() {
        downloadTask?.cancel()
        downloadTask = nil
        phase = .idle
    }

    func remove() {
        cancel()
        guard let ctx = LiveVoiceModel.context else { return }
        let dataDir = ctx.dataDir
        let core = ctx.core
        Task {
            await StudioVoiceEngine.shared.unload()
            try? FileManager.default.removeItem(at: StudioVoiceModel.folder(dataDir))
            self.phase = .idle
            DramaWork.tellCore(core)
            NotificationCenter.default.post(name: StudioVoiceModel.changed, object: nil)
        }
    }
}

/// 串行跑 Qwen3-TTS 1.7B Base（照卡对应语气档克隆，§3.5b）与 VoiceDesign（造七档专属角色卡，§4.2 / §5.3）。
actor StudioVoiceEngine {
    static let shared = StudioVoiceEngine()

    private struct CondKey: Hashable {
        let cardID: String
        let version: UInt32
        let style: CardStyle
        let language: String
    }

    private var loadedDir: URL?
    private var baseModel: Qwen3TTSModel?
    private var designModel: Qwen3TTSModel?
    private var condLRU: [(key: CondKey, cond: Qwen3TTSModel.Qwen3TTSReferenceConditioning)] = []
    private static let maxCondCache = 12

    func unload() {
        baseModel = nil
        designModel = nil
        loadedDir = nil
        condLRU.removeAll()
        Memory.clearCache()
    }

    /// §3.5 b：取角色声音卡在 `cardStyleFor(act)` 档的参考音与文字，由 Qwen3-TTS Base 1.7B 克隆生成。
    func synthesizeClip(
        core: YuyueCore,
        dataDir: URL,
        book: String,
        clip: ClipAsk,
        plainText: String,
        language: String
    ) async throws -> Speech? {
        try Task.checkCancellation()
        guard plainText.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }),
              StudioVoiceModel.installed(dataDir) else {
            return nil
        }
        LiveVoiceModel.register(core, dataDir)
        let style = core.cardStyleFor(act: clip.act)
        guard let resolved = core.cardClipResolved(bookId: book, id: clip.voice, style: style) else {
            return nil
        }
        let cardVersion = core.cardFind(bookId: book, id: clip.voice)?.version ?? 1
        let model = try await ensureBase(dataDir: dataDir)
        let qwenLang = StudioVoiceModel.qwenLanguage(for: language)
        let key = CondKey(cardID: clip.voice, version: cardVersion, style: resolved.style, language: qwenLang)
        let cond = try conditioning(
            core: core,
            model: model,
            key: key,
            clipPath: resolved.file,
            clipText: resolved.text,
            qwenLang: qwenLang
        )
        var params = model.defaultGenerationParameters
        params.temperature = 0.8
        let audio = try await model.generate(
            text: plainText,
            conditioning: cond,
            generationParameters: params
        )
        let raw = audio.asType(.float32).reshaped([-1]).asArray(Float.self)
        Memory.clearCache()
        guard !raw.isEmpty else { return nil }
        let normalized = core.cardPreparePcm(samples: raw, rate: UInt32(model.sampleRate), targetRate: 24_000)
        return CardVoice.speech(from: normalized, sampleRate: 24_000)
    }

    /// §4.2 & §5.3：用 Qwen3-TTS VoiceDesign 1.7B 按人物描述 + 七档说话指令一次造齐整张角色声音卡并存进这本书。
    func designCharacterCard(
        core: YuyueCore,
        dataDir: URL,
        book: String,
        character: UInt32,
        description: String,
        language: String
    ) async throws -> VoiceInfo? {
        try Task.checkCancellation()
        guard StudioVoiceModel.installed(dataDir) else { return nil }
        await SpeakerPrint.ensure(dataDir)
        let model = try await ensureDesign(dataDir: dataDir)

        let lang = CardVoice.canonicalLanguage(language)
        let chinese = (lang == "zh")
        let qwenLang = StudioVoiceModel.qwenLanguage(for: lang)

        let bookPrefix = String(book.filter { $0.isASCII && ($0.isLetter || $0.isNumber) }.prefix(8))
        let safePrefix = bookPrefix.isEmpty ? "book" : bookPrefix
        let cardID = "card.d.\(safePrefix).\(character)"
        let existingVersion = core.cardFind(bookId: book, id: cardID)?.version ?? 0

        let castList = (try? core.dramaCast(bookId: book, uptoSection: UInt32.max)) ?? []
        let matchedChar = castList.first { $0.id == character }
        let cardName = matchedChar.flatMap { !$0.shown.isEmpty ? $0.shown : $0.names.first } ?? String(localized: "角色 \(character)")
        let gender = (matchedChar?.gender).flatMap { $0 == .unknown ? nil : $0 } ?? Self.inferGender(from: description)
        let age = (matchedChar?.age).flatMap { $0 == .unknown ? nil : $0 } ?? Self.inferAge(from: description)

        let stageDir = FileManager.default.temporaryDirectory
            .appending(path: "yy-design-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: stageDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: stageDir) }

        let allStyles: [CardStyle] = [.neutral, .joy, .anger, .sad, .tense, .soft, .shout]
        var clips: [CardClip] = []
        var neutralEmbedding: [Float] = []

        for (styleIdx, style) in allStyles.enumerated() {
            try Task.checkCancellation()
            let script = core.cardScript(lang: lang, style: style)
            let styleInst = core.cardStyleInstruction(style: style, chinese: chinese)
            let instruct = chinese ? "\(description)。\(styleInst)" : "\(description). \(styleInst)"
            let fileName = StudioVoiceModel.styleFileName(style)
            let filePath = stageDir.appending(path: fileName).path(percentEncoded: false)

            var accepted = false
            for attempt in 0 ..< 3 {
                try Task.checkCancellation()
                MLXRandom.seed(UInt64(character &* 977 &+ UInt32(styleIdx * 31 + attempt + 1)))
                var params = model.defaultGenerationParameters
                params.temperature = 0.85
                let audio = try await model.generate(
                    text: script,
                    voice: instruct,
                    refAudio: nil,
                    refText: nil,
                    language: qwenLang,
                    generationParameters: params
                )
                let raw = audio.asType(.float32).reshaped([-1]).asArray(Float.self)
                Memory.clearCache()
                guard !raw.isEmpty else { continue }
                let prepared = core.cardPreparePcm(samples: raw, rate: UInt32(model.sampleRate), targetRate: 24_000)
                let secs = Float(prepared.count) / 24_000
                guard secs >= 4.0 && secs <= 15.0 else { continue }

                let emb = await SpeakerPrint.print(core, dataDir, samples: prepared, rate: 24_000) ?? []
                if style == .neutral {
                    let writtenSecs = try core.cardWriteWav(path: filePath, samples: prepared, rate: 24_000)
                    neutralEmbedding = emb
                    clips.append(CardClip(style: .neutral, file: fileName, text: script, seconds: writtenSecs))
                    accepted = true
                    break
                } else {
                    let sim = (!neutralEmbedding.isEmpty && !emb.isEmpty)
                        ? StudioVoiceModel.cosine(neutralEmbedding, emb)
                        : 1.0
                    if sim >= 0.55 {
                        let writtenSecs = try core.cardWriteWav(path: filePath, samples: prepared, rate: 24_000)
                        clips.append(CardClip(style: style, file: fileName, text: script, seconds: writtenSecs))
                        accepted = true
                        break
                    }
                }
            }
            // neutral 必有：若前 3 次因时长边界未中，放宽收下最后一次；其余六档不过 0.55 则省略回退用 neutral（§10）。
            if style == .neutral && !accepted {
                var params = model.defaultGenerationParameters
                params.temperature = 0.8
                let audio = try await model.generate(
                    text: script,
                    voice: instruct,
                    refAudio: nil,
                    refText: nil,
                    language: qwenLang,
                    generationParameters: params
                )
                let raw = audio.asType(.float32).reshaped([-1]).asArray(Float.self)
                Memory.clearCache()
                guard !raw.isEmpty else { return nil }
                let writtenSecs = try core.cardWriteWav(path: filePath, samples: raw, rate: UInt32(model.sampleRate))
                let decoded = (try? core.cardDecodeClip(path: filePath, targetRate: 24_000)) ?? []
                neutralEmbedding = await SpeakerPrint.print(core, dataDir, samples: decoded, rate: 24_000) ?? []
                clips.append(CardClip(style: .neutral, file: fileName, text: script, seconds: writtenSecs))
            }
        }

        guard clips.contains(where: { $0.style == .neutral }) else { return nil }
        let card = VoiceCard(
            id: cardID,
            name: cardName,
            gender: gender,
            age: age,
            tags: [],
            lang: lang,
            origin: .designed(book: book, character: character),
            clips: clips,
            description: description,
            version: existingVersion + 1,
            narrator: false,
            rate: 0,
            embedding: neutralEmbedding,
            sync: true
        )
        let saved = try core.cardSave(bookId: book, card: card, sourceDir: stageDir.path(percentEncoded: false))
        if !neutralEmbedding.isEmpty {
            core.speakerSetPrint(voice: saved.id, embedding: neutralEmbedding)
        }
        return core.cardVoiceInfo(card: saved, embedding: neutralEmbedding, acts: true)
    }

    private func ensureBase(dataDir: URL) async throws -> Qwen3TTSModel {
        Memory.cacheLimit = 256 * 1024 * 1024
        let root = StudioVoiceModel.folder(dataDir)
        if loadedDir != root {
            baseModel = nil
            designModel = nil
            condLRU.removeAll()
            loadedDir = root
        }
        if let baseModel { return baseModel }
        // 跑 Base 时释放 VoiceDesign，避免两个 1.7B 模型同时常驻内存。
        designModel = nil
        Memory.clearCache()
        let loaded = try await Qwen3TTSModel.fromModelDirectory(StudioVoiceModel.repoFolder(dataDir, repo: StudioVoiceModel.baseRepo))
        baseModel = loaded
        return loaded
    }

    private func ensureDesign(dataDir: URL) async throws -> Qwen3TTSModel {
        Memory.cacheLimit = 256 * 1024 * 1024
        let root = StudioVoiceModel.folder(dataDir)
        if loadedDir != root {
            baseModel = nil
            designModel = nil
            condLRU.removeAll()
            loadedDir = root
        }
        if let designModel { return designModel }
        baseModel = nil
        condLRU.removeAll()
        Memory.clearCache()
        let loaded = try await Qwen3TTSModel.fromModelDirectory(StudioVoiceModel.repoFolder(dataDir, repo: StudioVoiceModel.designRepo))
        designModel = loaded
        return loaded
    }

    private func conditioning(
        core: YuyueCore,
        model: Qwen3TTSModel,
        key: CondKey,
        clipPath: String,
        clipText: String,
        qwenLang: String
    ) throws -> Qwen3TTSModel.Qwen3TTSReferenceConditioning {
        if let idx = condLRU.firstIndex(where: { $0.key == key }) {
            let item = condLRU.remove(at: idx)
            condLRU.insert(item, at: 0)
            return item.cond
        }
        let samples = try core.cardDecodeClip(path: clipPath, targetRate: UInt32(model.sampleRate))
        let refAudio = MLXArray(samples).materialized()
        let built = try model.prepareReferenceConditioning(refAudio: refAudio, refText: clipText, language: qwenLang)
        condLRU.insert((key, built), at: 0)
        if condLRU.count > Self.maxCondCache {
            condLRU.removeLast(condLRU.count - Self.maxCondCache)
        }
        return built
    }

    private static func inferGender(from description: String) -> CharacterGender {
        let lower = description.lowercased()
        if lower.contains("female") || lower.contains("woman") || lower.contains("girl") || description.contains("女") {
            return .f
        }
        if lower.contains("male") || lower.contains("man") || lower.contains("boy") || description.contains("男") {
            return .m
        }
        return .unknown
    }

    private static func inferAge(from description: String) -> CharacterAge {
        let lower = description.lowercased()
        if description.contains("少") || description.contains("童") || description.contains("孩") || lower.contains("child") || lower.contains("teen") || lower.contains("boy") || lower.contains("girl") {
            return .child
        }
        if description.contains("老") || lower.contains("old") || lower.contains("elder") {
            return .old
        }
        if description.contains("中年") || lower.contains("middle") {
            return .middle
        }
        if description.contains("青年") || description.contains("年轻") || lower.contains("young") {
            return .young
        }
        return .unknown
    }
}
#endif
