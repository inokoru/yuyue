import Foundation
import Network
import Observation
import YuyueCore

/// 即时层朗读模型 (`ZipVoice-Distill-Int8` + `Vocos 24 kHz`) 与自带声音包 (`voices-1`)。
/// 界面上合称“朗读声音”，首次点朗读时在 Wi‑Fi 下静默准备，下完从下一章起切换成默认旁白。
nonisolated enum LiveVoiceModel {
    static let engine = "live-zipvoice-1"
    static let modelFolderName = "live-zipvoice-1"
    static let modelURL = URL(string: "https://github.com/inokoru/yuyue/releases/download/models-1/live-zipvoice-1.zip")!
    static let modelDigest = "7a2542b6301088152211d6edf4bd66ac3527fd29ec85ecf9b35fd4cc059b6bf9"
    static let modelBytes: Int64 = 149_776_622

    static let packFolderName = "voices-1"
    static let packURL = URL(string: "https://github.com/inokoru/yuyue/releases/download/models-1/voices-1.zip")!
    static let packDigest = "2d35635523d08b1b2d9d5cbfb49695a1eb424bbe7d4eafe51e1c255414d44f2c"
    static let packBytes: Int64 = 14_002_684

    static let totalMegabytes = 160
    static let ready = Notification.Name("yuyue.liveVoiceReady")

    /// 低内存手机保护：物理内存不足 6 GB 档的 iOS 设备不装、不跑即时层，避免杀后台或发烫。
    #if os(iOS)
    static let deviceUnsupported = ProcessInfo.processInfo.physicalMemory < 5_500_000_000
    #else
    static let deviceUnsupported = false
    #endif

    private static let held = Mutex<(core: YuyueCore, dataDir: URL)?>(nil)

    static var context: (core: YuyueCore, dataDir: URL)? {
        held.withLock { $0 }
    }

    static func modelFolder(_ dataDir: URL) -> URL {
        Aligner.folder(dataDir).appending(path: modelFolderName, directoryHint: .isDirectory)
    }

    static func packFolder(_ dataDir: URL) -> URL {
        Aligner.folder(dataDir).appending(path: packFolderName, directoryHint: .isDirectory)
    }

    static func modelInstalled(_ dataDir: URL) -> Bool {
        guard !deviceUnsupported else { return false }
        let dir = modelFolder(dataDir)
        let fm = FileManager.default
        return fm.fileExists(atPath: dir.appending(path: "encoder.int8.onnx").path(percentEncoded: false))
            && fm.fileExists(atPath: dir.appending(path: "decoder.int8.onnx").path(percentEncoded: false))
            && fm.fileExists(atPath: dir.appending(path: "vocos_24khz.onnx").path(percentEncoded: false))
    }

    static func packInstalled(_ dataDir: URL) -> Bool {
        guard !deviceUnsupported else { return false }
        return FileManager.default.fileExists(atPath: packFolder(dataDir).appending(path: "voices.json").path(percentEncoded: false))
    }

    static func installed(_ dataDir: URL) -> Bool {
        modelInstalled(dataDir) && packInstalled(dataDir)
    }

    static var isModelInstalled: Bool {
        guard let ctx = context else { return false }
        return modelInstalled(ctx.dataDir)
    }

    static var isInstalled: Bool {
        guard let ctx = context else { return false }
        return installed(ctx.dataDir)
    }

    /// 在启动及下载完成时向核心登记词表与自带声音包目录（幂等、开销极小）。
    static func register(_ core: YuyueCore, _ dataDir: URL) {
        guard !deviceUnsupported else { return }
        if modelInstalled(dataDir) {
            _ = core.liveVoiceSetDir(dir: modelFolder(dataDir).path(percentEncoded: false))
        }
        if packInstalled(dataDir) {
            _ = core.cardsSetPack(dir: packFolder(dataDir).path(percentEncoded: false))
        }
    }

    /// 书库打开时绑定全局句柄，并在首帧之后异步登记本地已有模型与声音包，避免冷启动白屏。
    static func attach(core: YuyueCore, dataDir: URL) {
        held.withLock { $0 = (core, dataDir) }
        Task { @MainActor in
            await Self.registerAsync(core, dataDir)
            LiveVoiceDownload.shared.refresh()
            KokoroDownload.shared.refresh()
            #if DEBUG
            if !installed(dataDir),
               UserDefaults.standard.string(forKey: "debugLiveVoiceURL") != nil
                || UserDefaults.standard.string(forKey: "debugVoicePackURL") != nil {
                LiveVoiceDownload.shared.start()
            }
            #endif
            DramaWork.tellCore(core)
        }
    }

    @concurrent static func registerAsync(_ core: YuyueCore, _ dataDir: URL) async {
        register(core, dataDir)
        KokoroModel.register(core, dataDir)
    }

    /// 下载并解压单个组件压缩包（支持多源自动回退、HTTP Range 断点续传与 `-debugLiveVoiceURL` / `-debugVoicePackURL` 覆盖）。
    @concurrent
    static func ensureArchive(
        into target: URL,
        folderName: String,
        defaultURL: URL,
        digest: String,
        debugKey: String,
        expectedBytes: Int64,
        progress: @escaping @Sendable (Double) -> Void
    ) async throws {
        let dir = target.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let partFile = dir.appending(path: ".\(folderName).zip.part")
        let alreadyOnDisk = ModelDownload.fileSize(partFile) ?? 0
        let free = (try? dir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage
        if let free, free + alreadyOnDisk < expectedBytes * 2 {
            throw CocoaError(.fileWriteOutOfSpace)
        }
        let assetName = defaultURL.lastPathComponent
        var urls = ModelDownload.candidateURLs(for: assetName, officialURL: defaultURL)
        var want: String? = digest.isEmpty ? nil : digest
        #if DEBUG
        if let override = UserDefaults.standard.string(forKey: debugKey), !override.isEmpty,
           let local = debugURL(override) ?? URL(string: override) {
            urls = [local]
            want = nil
        }
        #endif
        try await ModelDownload.downloadWithResume(
            urls: urls,
            to: partFile,
            expectedBytes: expectedBytes,
            expectedBlake3: want
        ) { done, total in
            let denom = max(total, expectedBytes, 1)
            progress(min(0.96, max(0, Double(done) / Double(denom) * 0.96)))
        }
        progress(0.97)
        let unpack = dir.appending(path: "unpack-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: unpack) }
        try unpackModel(zip: partFile.path(percentEncoded: false), to: unpack.path(percentEncoded: false))
        progress(0.99)
        let nested = unpack.appending(path: folderName, directoryHint: .isDirectory)
        let source = FileManager.default.fileExists(atPath: nested.path(percentEncoded: false)) ? nested : unpack
        try? FileManager.default.removeItem(at: target)
        try FileManager.default.moveItem(at: source, to: target)
        try? FileManager.default.removeItem(at: partFile)
        progress(1.0)
    }
}

/// 自带声音包（48 张 CC0 声音卡，七档语气）。
nonisolated enum VoicePack {
    /// Cards in the pack, every language: what 推荐 promises before it is downloaded.
    static let totalCards = 48
    static func folder(_ dataDir: URL) -> URL { LiveVoiceModel.packFolder(dataDir) }
    static func installed(_ dataDir: URL) -> Bool { LiveVoiceModel.packInstalled(dataDir) }

    static func register(_ core: YuyueCore, _ dataDir: URL) {
        guard installed(dataDir) else { return }
        _ = core.cardsSetPack(dir: folder(dataDir).path(percentEncoded: false))
    }

    /// 对应语言的自带声音卡，默认旁白排最前（中文优先 `card.b.01`，英文优先 `card.b.33`）。
    static func builtinCards(for language: String) -> [VoiceCard] {
        guard !LiveVoiceModel.deviceUnsupported, CardVoice.supportsLanguage(language),
              let ctx = LiveVoiceModel.context else { return [] }
        LiveVoiceModel.register(ctx.core, ctx.dataDir)
        let wantLang = CardVoice.canonicalLanguage(language)
        let preferredID = wantLang == "en" ? "card.b.33" : "card.b.01"
        let all = ctx.core.cardsBuiltin().filter { $0.lang.lowercased().hasPrefix(wantLang) }
        return all.sorted { a, b in
            if (a.id == preferredID) != (b.id == preferredID) { return a.id == preferredID }
            if a.narrator != b.narrator { return a.narrator && !b.narrator }
            return a.id < b.id
        }
    }

    /// 即时层与声音包均就绪时，这门语言的默认旁白声音卡。
    static func defaultNarrator(for language: String) -> CardVoice? {
        guard LiveVoiceModel.isInstalled, CardVoice.supportsLanguage(language) else { return nil }
        let cards = builtinCards(for: language)
        guard !cards.isEmpty else { return nil }
        let wantLang = CardVoice.canonicalLanguage(language)
        let picked: VoiceCard? = if wantLang == "en" {
            cards.first { $0.id == "card.b.33" }
                ?? cards.first { $0.narrator && $0.gender == .f }
                ?? cards.first { $0.narrator }
                ?? cards.first
        } else {
            cards.first { $0.id == "card.b.01" }
                ?? cards.first { $0.narrator && $0.gender == .m }
                ?? cards.first { $0.narrator }
                ?? cards.first
        }
        return picked.map { CardVoice(card: $0, bookID: nil) }
    }
}

/// 统一管理“朗读声音”（即时层模型 + 声音包）的网络判断、合并进度与失败重试。
@MainActor @Observable
final class LiveVoiceDownload {
    static let shared = LiveVoiceDownload()

    enum Phase: Equatable {
        case unsupported
        case idle
        case downloading(Double)
        case ready
        case failed
    }

    private(set) var phase: Phase = LiveVoiceModel.deviceUnsupported ? .unsupported : .idle
    private(set) var isExpensive = false
    @ObservationIgnored private var pathKnown = false
    @ObservationIgnored private var pathSatisfied = false
    @ObservationIgnored private var pendingWifiAutoStart = false
    @ObservationIgnored private var runID: UInt64 = 0
    @ObservationIgnored private let monitor = NWPathMonitor()
    @ObservationIgnored private var task: Task<Void, Never>?

    private init() {
        guard !LiveVoiceModel.deviceUnsupported else {
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
                    if KokoroDownload.canAutoStartOnWifi(pathKnown: true, isSatisfied: satisfied, isExpensive: expensive),
                       self.phase == .idle, self.task == nil {
                        self.start()
                    }
                }
            }
        }
        monitor.start(queue: .global(qos: .utility))
        refresh()
    }

    #if DEBUG
    /// `-debugLivePhase idle|downloading|failed`：图册里摆出 的三种样子。
    func debugShow(_ name: String) {
        switch name {
        case "downloading": phase = .downloading(0.43)
        case "failed": phase = .failed
        case "idle": phase = .idle
        default: break
        }
    }
    #endif

    func refresh() {
        if LiveVoiceModel.deviceUnsupported {
            phase = .unsupported
            return
        }
        guard let ctx = LiveVoiceModel.context else { return }
        if LiveVoiceModel.installed(ctx.dataDir) {
            phase = .ready
            let core = ctx.core
            let dataDir = ctx.dataDir
            Task { await LiveVoiceModel.registerAsync(core, dataDir) }
        } else if case .ready = phase {
            phase = .idle
        }
    }

    /// 首次点朗读时调用：Wi‑Fi 下静默开下，蜂窝流量下保持 `.idle` 等用户手动点“下载”。
    func startIfWifi(for language: String) {
        guard CardVoice.supportsLanguage(language), !LiveVoiceModel.deviceUnsupported else { return }
        refresh()
        if phase == .ready || task != nil { return }
        // 若上次失败，不自动无限重试，留给面板上的“再试”按钮。
        if phase == .failed { return }
        guard pathKnown else {
            pendingWifiAutoStart = true
            return
        }
        guard KokoroDownload.canAutoStartOnWifi(pathKnown: pathKnown, isSatisfied: pathSatisfied, isExpensive: isExpensive),
              phase == .idle else { return }
        start()
    }

    /// 用户手动点“下载”或“再试”（或 Wi‑Fi 自动触发）。
    func start() {
        guard !LiveVoiceModel.deviceUnsupported, task == nil, let ctx = LiveVoiceModel.context else { return }
        pendingWifiAutoStart = false
        if LiveVoiceModel.installed(ctx.dataDir) {
            phase = .ready
            let core = ctx.core
            let dataDir = ctx.dataDir
            Task { await LiveVoiceModel.registerAsync(core, dataDir) }
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
            let needModel = !LiveVoiceModel.modelInstalled(dataDir)
            let needPack = !LiveVoiceModel.packInstalled(dataDir)
            let modelWeight = needModel && needPack ? 0.90 : (needModel ? 1.0 : 0.0)
            let packWeight = needModel && needPack ? 0.10 : (needPack ? 1.0 : 0.0)
            do {
                if needModel {
                    try await LiveVoiceModel.ensureArchive(
                        into: LiveVoiceModel.modelFolder(dataDir),
                        folderName: LiveVoiceModel.modelFolderName,
                        defaultURL: LiveVoiceModel.modelURL,
                        digest: LiveVoiceModel.modelDigest,
                        debugKey: "debugLiveVoiceURL",
                        expectedBytes: LiveVoiceModel.modelBytes
                    ) { frac in
                        relay.send(frac * modelWeight)
                    }
                }
                if needPack {
                    let base = needModel ? modelWeight : 0
                    try await LiveVoiceModel.ensureArchive(
                        into: LiveVoiceModel.packFolder(dataDir),
                        folderName: LiveVoiceModel.packFolderName,
                        defaultURL: LiveVoiceModel.packURL,
                        digest: LiveVoiceModel.packDigest,
                        debugKey: "debugVoicePackURL",
                        expectedBytes: LiveVoiceModel.packBytes
                    ) { frac in
                        relay.send(min(0.99, base + frac * packWeight))
                    }
                }
                await LiveVoiceModel.registerAsync(core, dataDir)
                guard let self, self.runID == currentRun else { return }
                self.phase = .ready
                DramaWork.tellCore(core)
                NotificationCenter.default.post(name: LiveVoiceModel.ready, object: nil)
            } catch is CancellationError {
                if let self, self.runID == currentRun, self.phase != .ready { self.phase = .idle }
            } catch {
                if let self, self.runID == currentRun { self.phase = .failed }
            }
        }
    }
}
