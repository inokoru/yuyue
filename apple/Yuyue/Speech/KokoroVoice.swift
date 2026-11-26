import AVFoundation
import CryptoKit
import Foundation
import Network
import Observation
import OnnxRuntimeC
import SwiftUI
import YuyueCore

/// 跨端轻量离线神经旁白模型（`Kokoro-82M-v1.1-zh Int8`，Apache-2.0）。
///
/// 与操作系统自带合成音彻底分离，作为「语阅离线语音」独立推荐来源提供：
/// - 峰值内存仅约 350 MB，支持 Mac、Windows、Android 与 4 GB 以上 iPhone / iPad；
/// - 优先从魔搭 ModelScope / GitHub Releases 拉取合并压缩包 `kokoro-v1_1-1.zip`，若尚未同步或超时，
///   自动无感回退至 `hf-mirror.com` / `huggingface.co`（`csukuangfj/kokoro-int8-multi-lang-v1_1`）逐文件断点续传，
///   全链路均强制核对 BLAKE3 摘要。
nonisolated enum KokoroModel {
    static let engine = "kokoro-v1_1-1"
    static let folderName = "kokoro-v1_1-1"
    static let archiveName = "kokoro-v1_1-1.zip"
    static let bundleURL = URL(string: "https://github.com/inokoru/yuyue/releases/download/models-1/kokoro-v1_1-1.zip")!
    static let bundleDigest = "0fb9a0af735001f293549bfcf096c0a2e834472b419ecdbbd1d83531c3b791a3"
    static let bundleBytes: Int64 = 131_005_940
    static let totalMegabytes = 125

    static let ready = Notification.Name("yuyue.kokoroReady")

    struct RawFile: Sendable {
        let name: String
        let bytes: Int64
        let blake3: String
    }

    static let rawFiles: [RawFile] = [
        RawFile(name: "model.int8.onnx", bytes: 114_203_914, blake3: "9c85d5920c66ca9ff88844b0de859df9badc29a3cada0e3696b993f111cf58b6"),
        RawFile(name: "voices.bin", bytes: 53_790_720, blake3: "07d6924043f9d4b7fb13149f74ee702321c9a02baadf743bc9c307499471bcd6"),
        RawFile(name: "tokens.txt", bytes: 1_111, blake3: "6f6916d8b098628f18a2d4c66cf9a38af6de85e78ba8343a8787ff4bd9630885"),
        RawFile(name: "lexicon-zh.txt", bytes: 2_120_131, blake3: "d1c4160a6ffcee2fdba5b1a42c3c358f0eec8063935cac22db341ebfee782950"),
        RawFile(name: "lexicon-us-en.txt", bytes: 5_956_885, blake3: "6c7e13b534ce7bf20884295e1ae3687010ed444ad01ef9a101d23a9dbef3a01e"),
    ]

    #if os(iOS)
    static let deviceUnsupported = ProcessInfo.processInfo.physicalMemory < 3_500_000_000
    #else
    static let deviceUnsupported = false
    #endif

    static func folder(_ dataDir: URL) -> URL {
        Aligner.folder(dataDir).appending(path: folderName, directoryHint: .isDirectory)
    }

    static func installed(_ dataDir: URL) -> Bool {
        guard !deviceUnsupported else { return false }
        let dir = folder(dataDir)
        let fm = FileManager.default
        return rawFiles.allSatisfy {
            fm.fileExists(atPath: dir.appending(path: $0.name).path(percentEncoded: false))
        }
    }

    static var isInstalled: Bool {
        guard let ctx = LiveVoiceModel.context else { return false }
        return installed(ctx.dataDir)
    }

    static func register(_ core: YuyueCore, _ dataDir: URL) {
        guard installed(dataDir) else { return }
        _ = core.kokoroSetDir(dir: folder(dataDir).path(percentEncoded: false))
    }

    @concurrent
    static func registerAsync(_ core: YuyueCore, _ dataDir: URL) async {
        register(core, dataDir)
    }

    /// 静态兜底音色元数据（供设置页在未加载权重或首次打开时展示完整音色数量与名称）。
    static let fallbackSpeakers: [KokoroSpeaker] = [
        KokoroSpeaker(id: "kokoro.zf_001", voiceId: "zf_001", sid: 3, name: "清禾", lang: "zh", gender: .f, narrator: true, summary: "温润清朗 · 默认中文女声"),
        KokoroSpeaker(id: "kokoro.zf_002", voiceId: "zf_002", sid: 4, name: "知夏", lang: "zh", gender: .f, narrator: false, summary: "柔和舒缓 · 适合散文与小说"),
        KokoroSpeaker(id: "kokoro.zf_004", voiceId: "zf_004", sid: 6, name: "云舒", lang: "zh", gender: .f, narrator: false, summary: "沉静端庄 · 适合历史与社科"),
        KokoroSpeaker(id: "kokoro.zf_006", voiceId: "zf_006", sid: 8, name: "茗烟", lang: "zh", gender: .f, narrator: false, summary: "轻快自然 · 适合随笔与故事"),
        KokoroSpeaker(id: "kokoro.zf_018", voiceId: "zf_018", sid: 12, name: "晚晴", lang: "zh", gender: .f, narrator: false, summary: "柔和细腻 · 适合夜读"),
        KokoroSpeaker(id: "kokoro.zf_028", voiceId: "zf_028", sid: 20, name: "素心", lang: "zh", gender: .f, narrator: false, summary: "干净利落 · 适合非虚构长文"),
        KokoroSpeaker(id: "kokoro.zf_079", voiceId: "zf_079", sid: 46, name: "凌霜", lang: "zh", gender: .f, narrator: false, summary: "清冷干练 · 适合独立女主"),
        KokoroSpeaker(id: "kokoro.zf_074", voiceId: "zf_074", sid: 41, name: "映月", lang: "zh", gender: .f, narrator: false, summary: "婉转清澈 · 适合古典与言情"),
        KokoroSpeaker(id: "kokoro.zf_090", voiceId: "zf_090", sid: 53, name: "疏桐", lang: "zh", gender: .f, narrator: false, summary: "沉着知性 · 适合熟龄女角"),
        KokoroSpeaker(id: "kokoro.zf_094", voiceId: "zf_094", sid: 56, name: "晴川", lang: "zh", gender: .f, narrator: false, summary: "明朗清脆 · 适合少女角色"),
        KokoroSpeaker(id: "kokoro.zf_024", voiceId: "zf_024", sid: 17, name: "念安", lang: "zh", gender: .f, narrator: false, summary: "温婉平和 · 适合日常对话"),
        KokoroSpeaker(id: "kokoro.zf_027", voiceId: "zf_027", sid: 19, name: "语微", lang: "zh", gender: .f, narrator: false, summary: "轻柔低语 · 适合细腻内心戏"),
        KokoroSpeaker(id: "kokoro.zf_019", voiceId: "zf_019", sid: 13, name: "含烟", lang: "zh", gender: .f, narrator: false, summary: "灵动俏皮 · 适合活泼角色"),
        KokoroSpeaker(id: "kokoro.zf_076", voiceId: "zf_076", sid: 43, name: "听兰", lang: "zh", gender: .f, narrator: false, summary: "雅致从容 · 适合长辈与配角"),
        KokoroSpeaker(id: "kokoro.zm_010", voiceId: "zm_010", sid: 59, name: "沉舟", lang: "zh", gender: .m, narrator: true, summary: "醇厚稳重 · 推荐中文男声"),
        KokoroSpeaker(id: "kokoro.zm_009", voiceId: "zm_009", sid: 58, name: "松风", lang: "zh", gender: .m, narrator: false, summary: "清朗平和 · 适合纪实与传记"),
        KokoroSpeaker(id: "kokoro.zm_012", voiceId: "zm_012", sid: 61, name: "远山", lang: "zh", gender: .m, narrator: false, summary: "低沉内敛 · 适合经典文学"),
        KokoroSpeaker(id: "kokoro.zm_025", voiceId: "zm_025", sid: 67, name: "怀瑾", lang: "zh", gender: .m, narrator: false, summary: "青年明快 · 适合科幻与通识"),
        KokoroSpeaker(id: "kokoro.zm_035", voiceId: "zm_035", sid: 73, name: "竹轩", lang: "zh", gender: .m, narrator: false, summary: "温厚从容 · 适合长篇慢听"),
        KokoroSpeaker(id: "kokoro.zm_052", voiceId: "zm_052", sid: 78, name: "景行", lang: "zh", gender: .m, narrator: false, summary: "清晰有力 · 适合论著与技术"),
        KokoroSpeaker(id: "kokoro.zm_098", voiceId: "zm_098", sid: 101, name: "沧澜", lang: "zh", gender: .m, narrator: false, summary: "苍劲浑厚 · 适合长者与反派"),
        KokoroSpeaker(id: "kokoro.zm_056", voiceId: "zm_056", sid: 82, name: "拂晓", lang: "zh", gender: .m, narrator: false, summary: "爽朗少年 · 适合年轻男角"),
        KokoroSpeaker(id: "kokoro.zm_041", voiceId: "zm_041", sid: 75, name: "临渊", lang: "zh", gender: .m, narrator: false, summary: "冷静深沉 · 适合悬疑与谋士"),
        KokoroSpeaker(id: "kokoro.zm_082", voiceId: "zm_082", sid: 95, name: "昭明", lang: "zh", gender: .m, narrator: false, summary: "端正大气 · 适合正派主角"),
        KokoroSpeaker(id: "kokoro.zm_033", voiceId: "zm_033", sid: 71, name: "墨白", lang: "zh", gender: .m, narrator: false, summary: "温润书生 · 适合文人角色"),
        KokoroSpeaker(id: "kokoro.zm_095", voiceId: "zm_095", sid: 98, name: "听涛", lang: "zh", gender: .m, narrator: false, summary: "豪迈粗犷 · 适合侠客配角"),
        KokoroSpeaker(id: "kokoro.zm_080", voiceId: "zm_080", sid: 93, name: "敬之", lang: "zh", gender: .m, narrator: false, summary: "沉稳温和 · 适合中年男角"),
        KokoroSpeaker(id: "kokoro.zm_096", voiceId: "zm_096", sid: 99, name: "扶摇", lang: "zh", gender: .m, narrator: false, summary: "轻快松弛 · 适合幽默角色"),
        KokoroSpeaker(id: "kokoro.af_maple", voiceId: "af_maple", sid: 0, name: "Maple", lang: "en", gender: .f, narrator: true, summary: "Warm American narrator · Default"),
        KokoroSpeaker(id: "kokoro.bf_vale", voiceId: "bf_vale", sid: 2, name: "Vale", lang: "en", gender: .f, narrator: true, summary: "Crisp British RP narrator"),
        KokoroSpeaker(id: "kokoro.af_sol", voiceId: "af_sol", sid: 1, name: "Sol", lang: "en", gender: .f, narrator: false, summary: "Bright conversational American"),
        KokoroSpeaker(id: "kokoro.zm_010.en", voiceId: "zm_010", sid: 59, name: "Arthur (Bilingual)", lang: "en", gender: .m, narrator: false, summary: "Calm bilingual male narrator"),
    ]

    /// Extracts an L2-normalized 256-dim timbre vector directly from `voices.bin` (`[103, 510, 256]` f32)
    /// so `casting.rs` maximizes acoustic separation between characters sharing scenes at zero extra cost.
    static func styleEmbedding(sid: UInt32, dataDir: URL) -> [Float] {
        guard sid < 103 else { return [] }
        let url = folder(dataDir).appending(path: "voices.bin")
        guard let fh = try? FileHandle(forReadingFrom: url) else { return [] }
        defer { try? fh.close() }
        let buckets: [UInt64] = [10, 20, 40, 80]
        var acc = [Float](repeating: 0, count: 256)
        let bytesPerVec = 256 * MemoryLayout<Float>.size
        for b in buckets {
            let offset = (UInt64(sid) * 510 + b) * UInt64(bytesPerVec)
            guard (try? fh.seek(toOffset: offset)) != nil,
                  let data = try? fh.read(upToCount: bytesPerVec),
                  data.count == bytesPerVec else { return [] }
            data.withUnsafeBytes { raw in
                let ptr = raw.bindMemory(to: Float.self)
                for i in 0 ..< 256 {
                    acc[i] += Float(bitPattern: ptr[i].bitPattern.littleEndian)
                }
            }
        }
        let norm = sqrt(acc.reduce(Float(0)) { $0 + $1 * $1 })
        guard norm > 1e-6 else { return [] }
        return acc.map { $0 / norm }
    }

    static var totalVoices: Int { fallbackSpeakers.count }

    static func allSpeakers() -> [KokoroSpeaker] {
        if let ctx = LiveVoiceModel.context {
            let list = ctx.core.kokoroVoices()
            if !list.isEmpty { return list }
        }
        return fallbackSpeakers
    }

    static func builtinVoices(for language: String) -> [KokoroVoice] {
        guard !deviceUnsupported, CardVoice.supportsLanguage(language) else { return [] }
        let wantLang = CardVoice.canonicalLanguage(language)
        if let ctx = LiveVoiceModel.context, installed(ctx.dataDir) {
            register(ctx.core, ctx.dataDir)
        }
        return allSpeakers()
            .filter { $0.lang.lowercased().hasPrefix(wantLang) }
            .map(KokoroVoice.init(speaker:))
    }

    static func defaultNarrator(for language: String) -> KokoroVoice? {
        guard isInstalled, CardVoice.supportsLanguage(language) else { return nil }
        let voices = builtinVoices(for: language)
        return voices.first(where: { $0.speaker.narrator }) ?? voices.first
    }

    static func find(_ id: String) -> KokoroVoice? {
        guard id.hasPrefix("kokoro."), isInstalled else { return nil }
        return allSpeakers().first(where: { $0.id == id }).map(KokoroVoice.init(speaker:))
    }

    @concurrent
    static func ensureInstalled(
        into dataDir: URL,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let target = folder(dataDir)
        let parent = target.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: parent, withIntermediateDirectories: true)

        // 1. Try single compressed bundle from ModelScope (`nowtin/yuyue-models`) / GitHub Releases (`models-1`)
        do {
            try await LiveVoiceModel.ensureArchive(
                into: target,
                folderName: folderName,
                defaultURL: bundleURL,
                digest: bundleDigest,
                debugKey: "debugKokoroURL",
                expectedBytes: bundleBytes,
                progress: progress
            )
            if installed(dataDir) { return }
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            // Fall through to direct upstream HuggingFace / hf-mirror multi-source download.
        }

        // 2. Fallback: stream raw verified files directly from hf-mirror (CN) / Hugging Face
        let stage = parent.appending(path: ".kokoro-stage", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: stage, withIntermediateDirectories: true)
        let totalRaw = max(1, rawFiles.reduce(Int64(0)) { $0 + $1.bytes })
        var completedBytes: Int64 = 0

        let hfBase = "https://huggingface.co/csukuangfj/kokoro-int8-multi-lang-v1_1/resolve/main/"
        let mirrorBase = "https://hf-mirror.com/csukuangfj/kokoro-int8-multi-lang-v1_1/resolve/main/"
        let bases = ModelDownload.prefersChinaMirror ? [mirrorBase, hfBase] : [hfBase, mirrorBase]

        for rf in rawFiles {
            try Task.checkCancellation()
            let dst = stage.appending(path: rf.name)
            if let sz = ModelDownload.fileSize(dst), sz == rf.bytes,
               (try? ModelDownload.verifyDigest(of: dst, blake3: rf.blake3)) == true {
                completedBytes += rf.bytes
                progress(min(0.99, Double(completedBytes) / Double(totalRaw)))
                continue
            }
            let part = stage.appending(path: ".\(rf.name).part")
            let urls = bases.compactMap { URL(string: $0 + rf.name) }
            let baseDone = completedBytes
            try await ModelDownload.downloadWithResume(
                urls: urls,
                to: part,
                expectedBytes: rf.bytes,
                expectedBlake3: rf.blake3
            ) { done, _ in
                let cur = Double(baseDone + min(done, rf.bytes)) / Double(totalRaw)
                progress(min(0.99, max(0, cur)))
            }
            try? FileManager.default.removeItem(at: dst)
            try FileManager.default.moveItem(at: part, to: dst)
            completedBytes += rf.bytes
            progress(min(0.99, Double(completedBytes) / Double(totalRaw)))
        }

        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: stage, to: target)
        progress(1.0)
    }
}

/// 跨端轻量旁白包下载状态机（Wi‑Fi 下首次朗读可静默准备，也支持设置页一键下载）。
@MainActor @Observable
final class KokoroDownload {
    static let shared = KokoroDownload()

    enum Phase: Equatable {
        case unsupported
        case idle
        case downloading(Double)
        case ready
        case failed

        enum Kind: Equatable { case unsupported, idle, downloading, ready, failed }

        var kind: Kind {
            switch self {
            case .unsupported: .unsupported
            case .idle: .idle
            case .downloading: .downloading
            case .ready: .ready
            case .failed: .failed
            }
        }
    }

    private static let pendingVoiceKey = "kokoro.pendingVoice"

    private(set) var phase: Phase = KokoroModel.deviceUnsupported ? .unsupported : .idle
    private(set) var isExpensive = false
    /// 用户在离线包尚未下好时点选的音色（key 为 `VoiceBook.key`，如 `player.voice.zh`），下好后自动启用。
    private(set) var pendingVoice: [String: String] = [:]
    @ObservationIgnored private var pathKnown = false
    @ObservationIgnored private var pathSatisfied = false
    @ObservationIgnored private var pendingWifiAutoStart = false
    @ObservationIgnored private var runID: UInt64 = 0
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var task: Task<Void, Never>?

    nonisolated static func canAutoStartOnWifi(pathKnown: Bool, isSatisfied: Bool, isExpensive: Bool) -> Bool {
        pathKnown && isSatisfied && !isExpensive
    }

    func queueSelection(_ voiceID: String, for bookKey: String) {
        pendingVoice[bookKey] = voiceID
        UserDefaults.standard.set(pendingVoice, forKey: Self.pendingVoiceKey)
        if phase == .idle || phase == .failed {
            start()
        }
    }

    func pendingVoiceName(for bookKey: String) -> String? {
        guard let id = pendingVoice[bookKey] else { return nil }
        return KokoroModel.allSpeakers().first(where: { $0.id == id })?.name
    }

    private init() {
        if let saved = UserDefaults.standard.dictionary(forKey: Self.pendingVoiceKey) as? [String: String] {
            pendingVoice = saved
        }
        guard !KokoroModel.deviceUnsupported else {
            phase = .unsupported
            return
        }
        monitor.pathUpdateHandler = { [weak self] path in
            let satisfied = path.status == .satisfied
            let expensive = path.isExpensive || path.usesInterfaceType(.cellular)
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.pathSatisfied = satisfied
                self.isExpensive = expensive
                self.pathKnown = true
                if self.pendingWifiAutoStart {
                    self.pendingWifiAutoStart = false
                    if Self.canAutoStartOnWifi(pathKnown: true, isSatisfied: satisfied, isExpensive: expensive),
                       self.phase == .idle, self.task == nil {
                        self.start()
                    }
                }
            }
        }
        monitor.start(queue: .global(qos: .utility))
        refresh()
    }

    func refresh() {
        if KokoroModel.deviceUnsupported {
            phase = .unsupported
            return
        }
        guard let ctx = LiveVoiceModel.context else { return }
        if KokoroModel.installed(ctx.dataDir) {
            applyPendingVoices()
            phase = .ready
            let core = ctx.core
            let dataDir = ctx.dataDir
            Task { await KokoroModel.registerAsync(core, dataDir) }
        } else if case .ready = phase {
            phase = .idle
        }
    }

    private func applyPendingVoices() {
        guard !pendingVoice.isEmpty else { return }
        for (bookKey, voiceID) in pendingVoice {
            let lang = bookKey.hasPrefix("player.voice.")
                ? String(bookKey.dropFirst("player.voice.".count))
                : bookKey
            Player.saveVoicePreference(voiceID, for: lang)
        }
        pendingVoice.removeAll()
        UserDefaults.standard.removeObject(forKey: Self.pendingVoiceKey)
    }

    func startIfWifi(for language: String) {
        guard CardVoice.supportsLanguage(language), !KokoroModel.deviceUnsupported else { return }
        refresh()
        if phase == .ready || task != nil || phase == .failed { return }
        guard pathKnown else {
            pendingWifiAutoStart = true
            return
        }
        guard Self.canAutoStartOnWifi(pathKnown: pathKnown, isSatisfied: pathSatisfied, isExpensive: isExpensive),
              phase == .idle else { return }
        start()
    }

    func start() {
        guard !KokoroModel.deviceUnsupported, task == nil, let ctx = LiveVoiceModel.context else { return }
        pendingWifiAutoStart = false
        if KokoroModel.installed(ctx.dataDir) {
            applyPendingVoices()
            phase = .ready
            let core = ctx.core
            let dataDir = ctx.dataDir
            Task { await KokoroModel.registerAsync(core, dataDir) }
            return
        }
        runID &+= 1
        let currentRun = runID
        phase = .downloading(0)
        let core = ctx.core
        let dataDir = ctx.dataDir
        let relay = ModelDownload.ProgressRelay { [weak self] frac in
            guard let self, self.runID == currentRun, case .downloading = self.phase else { return }
            self.phase = .downloading(frac)
        }
        task = Task { [weak self] in
            defer {
                if let self, self.runID == currentRun {
                    self.task = nil
                }
            }
            do {
                try await KokoroModel.ensureInstalled(into: dataDir) { frac in
                    relay.send(frac)
                }
                await KokoroModel.registerAsync(core, dataDir)
                guard let self, self.runID == currentRun else { return }
                self.applyPendingVoices()
                self.phase = .ready
                DramaWork.tellCore(core)
                NotificationCenter.default.post(name: KokoroModel.ready, object: nil)
            } catch is CancellationError {
                if let self, self.runID == currentRun, self.phase != .ready { self.phase = .idle }
            } catch {
                if let self, self.runID == currentRun { self.phase = .failed }
            }
        }
    }

    func cancel() {
        runID &+= 1
        pendingWifiAutoStart = false
        task?.cancel()
        task = nil
        if phase != .ready { phase = .idle }
    }
}

nonisolated enum KokoroError: LocalizedError {
    case notInstalled

    var errorDescription: String? {
        switch self {
        case .notInstalled:
            String(localized: "离线语音包还在下载中，下好后即可离线朗读")
        }
    }
}

/// Kokoro 轻量离线旁白语音（`Kokoro-82M-v1.1-zh`）。
nonisolated struct KokoroVoice: Voice {
    let speaker: KokoroSpeaker

    var id: String { speaker.id }
    var name: String { speaker.name }
    var qualityName: String? { String(localized: "Kokoro 离线") }
    var isOffline: Bool { true }
    var summaryLine: String { speaker.summary }
    var isDefaultNarrator: Bool { speaker.id == "kokoro.zf_001" || speaker.id == "kokoro.af_maple" }

    func speaks(_ language: String) -> Bool {
        guard CardVoice.supportsLanguage(language) else { return false }
        let want = CardVoice.canonicalLanguage(language)
        return speaker.lang.lowercased().hasPrefix(want)
    }

    @concurrent func render(_ text: String, language: String) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else {
            return nil
        }
        let lang = CardVoice.inferLanguage(from: text, fallback: language.isEmpty ? speaker.lang : language)
        guard let ctx = LiveVoiceModel.context, KokoroModel.installed(ctx.dataDir) else {
            Task { @MainActor in
                KokoroDownload.shared.startIfWifi(for: lang)
            }
            throw KokoroError.notInstalled
        }
        if let samples = try await KokoroEngine.shared.synthesize(
            core: ctx.core,
            dataDir: ctx.dataDir,
            speaker: speaker,
            text: text,
            language: lang
        ), let speech = CardVoice.speech(from: samples, sampleRate: 24_000) {
            return speech
        }
        return nil
    }
}

/// 串行管理 `Kokoro-82M-v1.1-zh Int8` ONNX 会话与句子级缓存。
actor KokoroEngine {
    static let shared = KokoroEngine()

    private static let clauseGapSamples = Int(0.10 * 24_000)

    private var openDir: URL?
    private nonisolated(unsafe) var handle: OpaquePointer?

    deinit {
        if let handle {
            yy_ort_kokoro_close(handle)
        }
    }

    func close() {
        if let handle {
            yy_ort_kokoro_close(handle)
            self.handle = nil
            self.openDir = nil
        }
    }

    func synthesize(
        core: YuyueCore,
        dataDir: URL,
        speaker: KokoroSpeaker,
        text: String,
        language: String
    ) throws -> [Float]? {
        try Task.checkCancellation()
        guard let ort = ensureSession(core: core, dataDir: dataDir) else { return nil }

        let cacheURL = Self.cacheFile(speakerID: speaker.id, lang: language, text: text)
        if let cached = Self.readPCM(cacheURL) {
            return cached
        }

        let clauses = core.kokoroClauses(text: text, lang: language)
        guard !clauses.isEmpty else { return nil }
        var out: [Float] = []
        for clause in clauses {
            try Task.checkCancellation()
            let tokens = core.kokoroTokens(text: clause, lang: language)
            guard !tokens.isEmpty else { continue }
            guard let chunk = Self.runClause(ort: ort, tokens: tokens, sid: Int32(speaker.sid), speed: 1.0) else {
                return nil
            }
            var clean = core.kokoroPostprocess(samples: chunk, rate: 24_000)
            if clean.isEmpty {
                // Retry once with slight token/speed perturbation if INT8 STFT hits 0/0 NaN or rail-pinning.
                let nudged = tokens.count < 508 ? tokens + [tokens.last!] : tokens
                if let retry = Self.runClause(ort: ort, tokens: nudged, sid: Int32(speaker.sid), speed: 1.015) {
                    clean = core.kokoroPostprocess(samples: retry, rate: 24_000)
                }
            }
            guard !clean.isEmpty else { continue }
            if !out.isEmpty {
                out.append(contentsOf: repeatElement(Float(0), count: Self.clauseGapSamples))
            }
            out.append(contentsOf: clean)
        }
        guard !out.isEmpty else { return nil }
        Self.writePCM(out, to: cacheURL)
        return out
    }

    private func ensureSession(core: YuyueCore, dataDir: URL) -> OpaquePointer? {
        guard !KokoroModel.deviceUnsupported, KokoroModel.installed(dataDir) else { return nil }
        KokoroModel.register(core, dataDir)
        let dir = KokoroModel.folder(dataDir)
        if let handle, openDir == dir {
            return handle
        }
        if let handle {
            yy_ort_kokoro_close(handle)
            self.handle = nil
            openDir = nil
        }
        let modelPath = dir.appending(path: "model.int8.onnx").path(percentEncoded: false)
        let voicesPath = dir.appending(path: "voices.bin").path(percentEncoded: false)
        #if os(macOS)
        let threads: Int32 = 4
        #else
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let threads = Int32(min(4, max(2, cores - 2)))
        #endif
        let opened = modelPath.withCString { mp in
            voicesPath.withCString { vp in
                yy_ort_kokoro_open(mp, vp, threads)
            }
        }
        guard let opened else { return nil }
        handle = opened
        openDir = dir
        return opened
    }

    nonisolated private static func runClause(ort: OpaquePointer, tokens: [Int64], sid: Int32, speed: Float = 1.0) -> [Float]? {
        var audio = YYKokoroAudio()
        let rc = tokens.withUnsafeBufferPointer { ptr in
            yy_ort_kokoro_synth(ort, ptr.baseAddress, Int64(tokens.count), sid, speed, &audio)
        }
        defer { yy_ort_kokoro_free(&audio) }
        guard rc == 0, audio.count > 0, let base = audio.samples else { return nil }
        return Array(UnsafeBufferPointer(start: base, count: Int(audio.count)))
    }

    nonisolated private static func cacheFile(speakerID: String, lang: String, text: String) -> URL {
        let key = SHA256.hash(data: Data("kokoro-v1_1-v2|\(speakerID)|\(lang)|\(text)".utf8))
            .prefix(16).map { String(format: "%02x", $0) }.joined()
        return FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask)[0]
            .appending(path: "yuyue-kokoro", directoryHint: .isDirectory)
            .appending(path: "\(key).f32")
    }

    nonisolated private static func readPCM(_ url: URL) -> [Float]? {
        guard let data = try? Data(contentsOf: url), data.count >= 16, data.count % 4 == 0 else { return nil }
        var out = [Float](repeating: 0, count: data.count / 4)
        _ = out.withUnsafeMutableBytes { data.copyBytes(to: $0) }
        return out
    }

    nonisolated private static func writePCM(_ samples: [Float], to url: URL) {
        let dir = url.deletingLastPathComponent()
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let data = samples.withUnsafeBufferPointer { Data(buffer: $0) }
        try? data.write(to: url, options: .atomic)
    }
}

/// Kokoro 轻量离线包内嵌下载横幅（无模态弹窗，一键下载 / 实时进度 / 失败重试）。
struct KokoroPrepBanner: View {
    let language: String
    var alwaysShowIdle = true
    var pill = false
    var pendingName: String? = nil
    private var download: KokoroDownload { .shared }

    var body: some View {
        Group { content }
            .animation(YYMotion.standard, value: download.phase.kind)
    }

    @ViewBuilder private var content: some View {
        if CardVoice.supportsLanguage(language) {
            switch download.phase {
            case .downloading(let frac):
                line {
                    VStack(alignment: .leading, spacing: 1) {
                        (Text("正在下载 Kokoro 轻量离线语音 ")
                            .foregroundStyle(YYColor.t1)
                            + Text("\(Int((frac * 100).rounded()))%")
                            .fontWeight(.semibold)
                            .foregroundStyle(YYTint.standard.accent))
                            .monospacedDigit()
                        Text(pendingName.map { String(localized: "下好后将自动启用「\($0)」· 下载期间可先听在线自然语音") }
                            ?? String(localized: "下载完成后永久免联网朗读 · 期间可先听在线自然语音"))
                            .font(.yy(size: 10.5))
                            .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 8)
                    ZStack(alignment: .leading) {
                        Capsule().fill(YYColor.line)
                        Capsule()
                            .fill(YYTint.standard.fill)
                            .frame(width: max(4, 88 * CGFloat(min(1, max(0, frac)))))
                            .animation(YYMotion.reduce ? nil : .linear(duration: 0.12), value: frac)
                    }
                    .frame(width: 88, height: 4)
                }

            case .idle where alwaysShowIdle || !KokoroModel.isInstalled:
                line {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("下载 Kokoro 跨端轻量语音 · 约 \(KokoroModel.totalMegabytes) MB")
                            .font(.yy(size: 12, weight: .semibold))
                            .foregroundStyle(YYColor.t1)
                        Text("16 个中英自然旁白 · 低内存秒出声 · 完全离线")
                            .font(.yy(size: 10.5))
                            .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 8)
                    action(String(localized: "一键下载"), color: YYTint.standard.accent) { download.start() }
                }

            case .failed:
                line {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("Kokoro 语音包未完成下载（支持断点续传）")
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                        Text("先用「在线自然语音（免 Key）」同样好听")
                            .font(.yy(size: 10.5))
                            .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 8)
                    action(String(localized: "继续下载"), color: YYColor.danger) { download.start() }
                }

            default:
                EmptyView()
            }
        }
    }

    private func line<C: View>(@ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: 8) { c() }
            .font(.yy(size: 11.5))
            .padding(.horizontal, 12)
            .padding(.vertical, 8)
            .frame(minHeight: 44)
            .background(YYColor.card, in: .rect(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    @ViewBuilder
    private func action(_ title: String, color: Color, _ run: @escaping () -> Void) -> some View {
        if pill {
            Button(title, action: run)
                .buttonStyle(YYButtonStyle(kind: color == YYColor.danger ? .danger : .soft, size: .small, height: 26))
        } else {
            Button(action: run) {
                Text(title)
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(color)
                    .padding(.horizontal, 10)
                    .frame(height: 26)
                    .background(color.opacity(0.11), in: .capsule)
                    .contentShape(.capsule)
            }
            .buttonStyle(.plain)
        }
    }
}
