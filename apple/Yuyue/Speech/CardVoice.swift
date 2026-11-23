import AVFoundation
import Foundation
import OnnxRuntimeC
import YuyueCore

nonisolated enum CardVoiceError: LocalizedError {
    case modelNotInstalled
    case synthesisFailed

    var errorDescription: String? {
        switch self {
        case .modelNotInstalled:
            String(localized: "本机声音卡模型尚未准备好，请先下载完成或改用系统声音")
        case .synthesisFailed:
            String(localized: "本机声音卡这一句合成失败，请重试或换一个声音")
        }
    }
}

/// 本机声音卡（自带 48 张声音、为某本书生成的角色声音、本机零样本克隆声音）。
/// 统一走同一份参考音 + 文字特征，由 `ZipVoice-Distill-Int8` 与 `Vocos 24 kHz` 即时合成。
nonisolated struct CardVoice: Voice {
    let card: VoiceCard
    var bookID: String? = nil

    var id: String { card.id }
    var name: String { card.name }

    var qualityName: String? {
        switch card.origin {
        case .cloned: String(localized: "克隆")
        default: nil
        }
    }

    var isOffline: Bool { true }

    var effectiveBookID: String? {
        if let bookID { return bookID }
        if case let .designed(book, _) = card.origin { return book }
        return nil
    }

    func speaks(_ language: String) -> Bool {
        Self.supportsLanguage(language)
    }

    /// 即时层模型当前支持中、英双语（`zh*` / `cmn*` / `en*`）；日文等语言回退系统声音。
    static func supportsLanguage(_ language: String) -> Bool {
        let t = language.replacingOccurrences(of: "_", with: "-").lowercased()
        return t == "zh" || t.hasPrefix("zh-") || t == "cmn" || t.hasPrefix("cmn-")
            || t == "en" || t.hasPrefix("en-")
    }

    static func canonicalLanguage(_ language: String) -> String {
        let t = language.replacingOccurrences(of: "_", with: "-").lowercased()
        return (t == "en" || t.hasPrefix("en-")) ? "en" : "zh"
    }

    static func inferLanguage(from text: String, fallback: String) -> String {
        if !fallback.isEmpty, supportsLanguage(fallback) {
            return canonicalLanguage(fallback)
        }
        let hasCJK = text.unicodeScalars.contains { (0x4E00 ... 0x9FFF).contains($0.value) || (0x3400 ... 0x4DBF).contains($0.value) }
        if hasCJK { return "zh" }
        let hasLatin = text.unicodeScalars.contains { ($0.value >= 65 && $0.value <= 90) || ($0.value >= 97 && $0.value <= 122) }
        if hasLatin { return "en" }
        return canonicalLanguage(fallback.isEmpty ? "zh" : fallback)
    }

    /// 供声音列表展示的一行简短描述（性别 · 年龄 · 标签 / 已录语气档数）。
    var summaryLine: String {
        switch card.origin {
        case .cloned:
            let extra = card.clips.filter { $0.style != .neutral }.count
            return extra > 0
                ? String(localized: "本机 · 已录 \(extra + 1) 档语气")
                : String(localized: "本机 · 平常语气")
        case .designed:
            var parts: [String] = [String(localized: "本书角色")]
            if let g = Self.genderLabel(card.gender) { parts.append(g) }
            if let a = Self.ageLabel(card.age) { parts.append(a) }
            return parts.joined(separator: " · ")
        case .builtin:
            var parts: [String] = []
            if let g = Self.genderLabel(card.gender) { parts.append(g) }
            if let a = Self.ageLabel(card.age) { parts.append(a) }
            if !card.tags.isEmpty {
                parts.append(card.tags.joined(separator: String(localized: "、")))
            }
            return parts.joined(separator: " · ")
        }
    }

    static func genderLabel(_ gender: CharacterGender) -> String? {
        switch gender {
        case .m: String(localized: "男")
        case .f: String(localized: "女")
        case .unknown: nil
        }
    }

    static func ageLabel(_ age: CharacterAge) -> String? {
        switch age {
        case .child: String(localized: "童声")
        case .teen: String(localized: "少年")
        case .young: String(localized: "青年")
        case .middle: String(localized: "中年")
        case .old: String(localized: "老年")
        case .unknown: nil
        }
    }

    func render(_ text: String, language: String) async throws -> Speech? {
        try await render(text, language: language, style: .neutral)
    }

    func render(_ text: String, language: String, style: CardStyle) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else {
            return nil
        }
        let lang = Self.inferLanguage(from: text, fallback: language.isEmpty ? card.lang : language)
        guard let ctx = LiveVoiceModel.context, LiveVoiceModel.modelInstalled(ctx.dataDir) else {
            throw CardVoiceError.modelNotInstalled
        }
        let plain = ctx.core.dramaPlainText(text: text)
        if let samples = try await LiveVoiceEngine.shared.synthesizeCard(
            core: ctx.core,
            dataDir: ctx.dataDir,
            card: card,
            bookID: effectiveBookID,
            text: text,
            plainText: plain,
            language: lang,
            style: style
        ), let speech = Self.speech(from: samples, sampleRate: 24_000) {
            return speech
        }
        // 不静默切换成系统声音，避免读到一半突然换人；明确报错交由播放器告知用户。
        try Task.checkCancellation()
        throw CardVoiceError.synthesisFailed
    }

    /// 克隆录音刚录完尚未存卡时，直接拿参考音文件合成试听句。
    static func previewReference(
        path: String,
        refText: String,
        refLang: String,
        text: String,
        language: String
    ) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }),
              let ctx = LiveVoiceModel.context, LiveVoiceModel.modelInstalled(ctx.dataDir) else {
            return nil
        }
        let lang = inferLanguage(from: text, fallback: language)
        let plain = ctx.core.dramaPlainText(text: text)
        guard let samples = try await LiveVoiceEngine.shared.synthesizeRawReference(
            core: ctx.core,
            dataDir: ctx.dataDir,
            clipPath: path,
            clipText: refText,
            clipLang: canonicalLanguage(refLang),
            text: text,
            plainText: plain,
            language: lang
        ) else {
            return nil
        }
        return speech(from: samples, sampleRate: 24_000)
    }

    static func speech(from samples: [Float], sampleRate: Double) -> Speech? {
        guard !samples.isEmpty,
              let fmt = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: 1),
              let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: AVAudioFrameCount(samples.count)) else {
            return nil
        }
        buf.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { ptr in
            if let base = ptr.baseAddress, let dst = buf.floatChannelData?[0] {
                dst.update(from: base, count: samples.count)
            }
        }
        return Speech.make(from: [buf])
    }
}

/// 串行执行 ONNX Runtime 推理与 16 份参考音特征 LRU 缓存，保证手机端峰值内存稳定在预算内。
actor LiveVoiceEngine {
    static let shared = LiveVoiceEngine()

    private struct PromptKey: Hashable {
        let cardID: String
        let version: UInt32
        let style: CardStyle
    }

    private static let numSteps: Int32 = 3
    private static let tShift: Float = 0.5
    private static let guidanceScale: Float = 3.0
    private static let clauseGapSamples = Int(0.12 * 24_000) // 段间留 0.12 秒静音
    private static let maxCachedPrompts = 16

    private var openDir: URL?
    private nonisolated(unsafe) var handle: OpaquePointer?
    private var timesteps: [Float] = []
    private var promptLRU: [(key: PromptKey, prompt: ZipVoicePrompt)] = []

    deinit {
        if let handle {
            yy_ort_close(handle)
        }
    }

    func synthesizeCard(
        core: YuyueCore,
        dataDir: URL,
        card: VoiceCard,
        bookID: String?,
        text: String,
        plainText: String,
        language: String,
        style: CardStyle
    ) throws -> [Float]? {
        try Task.checkCancellation()
        guard let ort = ensureSession(core: core, dataDir: dataDir),
              let resolved = core.cardClipResolved(bookId: bookID, id: card.id, style: style) else {
            return nil
        }
        let prompt = try cachedPrompt(
            core: core,
            key: PromptKey(cardID: card.id, version: card.version, style: resolved.style),
            path: resolved.file,
            text: resolved.text,
            lang: card.lang
        )
        let baseSeed = Self.seed(for: card.id, version: card.version, style: resolved.style, text: text)
        var bestAudible: [Float]?
        for attempt in 0 ..< 3 {
            try Task.checkCancellation()
            let seed = baseSeed &+ UInt64(attempt) &* 0x9E37_79B9_7F4A_7C15
            guard let samples = try runClauses(
                core: core,
                ort: ort,
                prompt: prompt,
                text: text,
                language: language,
                seed: seed
            ), !samples.isEmpty else { continue }
            if core.liveVoiceCheck(
                samples: samples,
                rate: 24_000,
                text: plainText,
                lang: language,
                expectedRate: card.rate
            ) {
                return samples
            }
            if bestAudible == nil, Self.isAudible(samples) {
                bestAudible = samples
            }
        }
        // 若严格时长/静音门槛三次均未过，但本声卡本身产出了有效人声，仍保留同一声卡结果，绝不中途偷换系统音。
        return bestAudible
    }

    private static func isAudible(_ samples: [Float]) -> Bool {
        guard !samples.isEmpty else { return false }
        var sumSq: Double = 0
        for s in samples {
            if !s.isFinite { return false }
            sumSq += Double(s) * Double(s)
        }
        let rms = (sumSq / Double(samples.count)).squareRoot()
        return rms >= 1e-4
    }

    func synthesizeRawReference(
        core: YuyueCore,
        dataDir: URL,
        clipPath: String,
        clipText: String,
        clipLang: String,
        text: String,
        plainText: String,
        language: String
    ) throws -> [Float]? {
        try Task.checkCancellation()
        guard let ort = ensureSession(core: core, dataDir: dataDir) else { return nil }
        let prompt = try core.liveVoicePrompt(path: clipPath, text: clipText, lang: clipLang)
        let baseSeed = Self.seed(for: clipPath, version: 1, style: .neutral, text: text)
        for attempt in 0 ..< 2 {
            try Task.checkCancellation()
            let seed = attempt == 0 ? baseSeed : (baseSeed &+ 0x9E37_79B9_7F4A_7C15)
            guard let samples = try runClauses(
                core: core,
                ort: ort,
                prompt: prompt,
                text: text,
                language: language,
                seed: seed
            ) else { continue }
            if core.liveVoiceCheck(
                samples: samples,
                rate: 24_000,
                text: plainText,
                lang: language,
                expectedRate: 0
            ) {
                return samples
            }
            if attempt == 1 && !samples.isEmpty {
                return samples
            }
        }
        return nil
    }

    private func ensureSession(core: YuyueCore, dataDir: URL) -> OpaquePointer? {
        guard !LiveVoiceModel.deviceUnsupported, LiveVoiceModel.modelInstalled(dataDir) else { return nil }
        LiveVoiceModel.register(core, dataDir)
        let dir = LiveVoiceModel.modelFolder(dataDir)
        if let handle, openDir == dir {
            if timesteps.isEmpty {
                timesteps = core.liveVoiceTimesteps(numSteps: UInt32(Self.numSteps), tShift: Self.tShift)
            }
            return handle
        }
        if let handle {
            yy_ort_close(handle)
            self.handle = nil
            openDir = nil
        }
        let enc = dir.appending(path: "encoder.int8.onnx").path(percentEncoded: false)
        let dec = dir.appending(path: "decoder.int8.onnx").path(percentEncoded: false)
        let voc = dir.appending(path: "vocos_24khz.onnx").path(percentEncoded: false)
        #if os(macOS)
        let threads: Int32 = 4
        #else
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let threads = Int32(min(4, max(2, cores - 2)))
        #endif
        let opened = enc.withCString { ep in
            dec.withCString { dp in
                voc.withCString { vp in
                    yy_ort_open(ep, dp, vp, threads)
                }
            }
        }
        guard let opened else { return nil }
        handle = opened
        openDir = dir
        timesteps = core.liveVoiceTimesteps(numSteps: UInt32(Self.numSteps), tShift: Self.tShift)
        return opened
    }

    private func cachedPrompt(
        core: YuyueCore,
        key: PromptKey,
        path: String,
        text: String,
        lang: String
    ) throws -> ZipVoicePrompt {
        if let idx = promptLRU.firstIndex(where: { $0.key == key }) {
            let entry = promptLRU.remove(at: idx)
            promptLRU.insert(entry, at: 0)
            return entry.prompt
        }
        let built = try core.liveVoicePrompt(path: path, text: text, lang: lang)
        promptLRU.insert((key, built), at: 0)
        if promptLRU.count > Self.maxCachedPrompts {
            promptLRU.removeLast(promptLRU.count - Self.maxCachedPrompts)
        }
        return built
    }

    private func runClauses(
        core: YuyueCore,
        ort: OpaquePointer,
        prompt: ZipVoicePrompt,
        text: String,
        language: String,
        seed: UInt64
    ) throws -> [Float]? {
        let clauses = core.liveVoiceClauses(text: text, lang: language)
        guard !clauses.isEmpty, !prompt.promptTokens.isEmpty, prompt.promptFrames > 0,
              timesteps.count >= Int(Self.numSteps) + 1 else {
            return nil
        }
        var out: [Float] = []
        for (idx, clause) in clauses.enumerated() {
            try Task.checkCancellation()
            let tokens = core.liveVoiceTokens(text: clause, lang: language)
            guard !tokens.isEmpty else { continue }
            let clauseSeed = seed &+ UInt64(idx) &* 0x517C_C1B7_2722_0A95
            guard let specOut = Self.runOrtClause(
                ort: ort,
                tokens: tokens,
                promptTokens: prompt.promptTokens,
                promptFeatures: prompt.promptFeatures,
                promptFrames: prompt.promptFrames,
                timesteps: timesteps,
                numSteps: Self.numSteps,
                guidanceScale: Self.guidanceScale,
                seed: clauseSeed
            ) else {
                return nil
            }
            let pcm = core.liveVoiceIstft(
                mag: specOut.mag,
                xCos: specOut.xCos,
                ySin: specOut.ySin,
                frames: specOut.frames
            )
            guard !pcm.isEmpty else { continue }
            if !out.isEmpty {
                out.append(contentsOf: repeatElement(Float(0), count: Self.clauseGapSamples))
            }
            out.append(contentsOf: pcm)
        }
        return out.isEmpty ? nil : out
    }

    nonisolated private static func runOrtClause(
        ort: OpaquePointer,
        tokens: [Int64],
        promptTokens: [Int64],
        promptFeatures: [Float],
        promptFrames: UInt32,
        timesteps: [Float],
        numSteps: Int32,
        guidanceScale: Float,
        seed: UInt64
    ) -> (mag: [Float], xCos: [Float], ySin: [Float], frames: UInt32)? {
        var spec = YYVocosSpec()
        let rc = tokens.withUnsafeBufferPointer { tokPtr in
            promptTokens.withUnsafeBufferPointer { ptokPtr in
                promptFeatures.withUnsafeBufferPointer { pfeatPtr in
                    timesteps.withUnsafeBufferPointer { tsPtr in
                        yy_ort_synth(
                            ort,
                            tokPtr.baseAddress,
                            Int64(tokens.count),
                            ptokPtr.baseAddress,
                            Int64(promptTokens.count),
                            pfeatPtr.baseAddress,
                            Int64(promptFrames),
                            tsPtr.baseAddress,
                            numSteps,
                            1.0,
                            guidanceScale,
                            seed,
                            &spec
                        )
                    }
                }
            }
        }
        defer { yy_ort_free_spec(&spec) }
        guard rc == 0, spec.frames > 0,
              let magPtr = spec.mag, let cosPtr = spec.x_cos, let sinPtr = spec.y_sin else {
            return nil
        }
        let count = 513 * Int(spec.frames)
        return (
            mag: Array(UnsafeBufferPointer(start: magPtr, count: count)),
            xCos: Array(UnsafeBufferPointer(start: cosPtr, count: count)),
            ySin: Array(UnsafeBufferPointer(start: sinPtr, count: count)),
            frames: UInt32(spec.frames)
        )
    }

    private static func seed(for cardID: String, version: UInt32, style: CardStyle, text: String) -> UInt64 {
        var h: UInt64 = 0xCBF2_9CE4_8422_2325
        for b in "\(cardID)@\(version)/\(style)|\(text)".utf8 {
            h ^= UInt64(b)
            h &*= 0x0000_0100_0000_01B3
        }
        return h == 0 ? 1 : h
    }
}
