@preconcurrency import AVFoundation
import os

/// Something that reads a sentence aloud: the system voices and the cloud ones. Every voice
/// hands its audio to the same `Player`, which plays it, shows it on the lock screen and tells
/// the reader how long each sentence takes.
protocol Voice: Sendable {
    var id: String { get }
    var name: String { get }
    /// "高级", or the cloud it comes from: said after the name.
    var qualityName: String? { get }
    /// Made on the device, without the network.
    var isOffline: Bool { get }
    /// Whether the voice can read text in `language` (a BCP 47 tag such as zh-Hans).
    func speaks(_ language: String) -> Bool
    /// The sentence as audio in `Speech.format`; nil when there is nothing to say.
    func render(_ text: String, language: String) async throws -> Speech?
}

/// One rendered sentence.
nonisolated final class Speech: @unchecked Sendable {
    /// Every voice renders to this format, so the player never needs reconnecting.
    static let format = AVAudioFormat(standardFormatWithSampleRate: 48_000, channels: 1)!

    /// Immutable once made; the player only reads it.
    let buffer: AVAudioPCMBuffer

    init(buffer: AVAudioPCMBuffer) {
        self.buffer = buffer
    }

    var seconds: Double { Double(buffer.frameLength) / buffer.format.sampleRate }

    /// Joins a voice's buffers and converts them to `format`.
    static func make(from buffers: [AVAudioPCMBuffer]) -> Speech? {
        guard let first = buffers.first else { return nil }
        let frames = buffers.reduce(0) { $0 + Int($1.frameLength) }
        guard frames > 0, let converter = AVAudioConverter(from: first.format, to: format) else { return nil }
        let ratio = format.sampleRate / first.format.sampleRate
        let capacity = AVAudioFrameCount((Double(frames) * ratio).rounded(.up)) + 4096
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else { return nil }
        let next = OSAllocatedUnfairLock(initialState: 0)
        var error: NSError?
        let status = converter.convert(to: out, error: &error) { _, inputStatus in
            let index = next.withLock { current -> Int? in
                guard current < buffers.count else { return nil }
                defer { current += 1 }
                return current
            }
            guard let index else {
                inputStatus.pointee = .endOfStream
                return nil
            }
            inputStatus.pointee = .haveData
            return buffers[index]
        }
        guard status != .error, out.frameLength > 0 else { return nil }
        return Speech(buffer: out)
    }
}

/// Book languages are BCP 47 tags (zh-Hans, zh-Hant, en, …); voices use regional ones (zh-CN, en-US).
nonisolated enum SpokenLanguage {
    /// The voice languages that can read `tag`, best first.
    static func voiceLanguages(for tag: String) -> [String] {
        let t = tag.replacingOccurrences(of: "_", with: "-").lowercased()
        if t == "yue" || t.hasPrefix("zh-hk") || t.hasPrefix("zh-mo") || t.hasPrefix("zh-yue") { return ["zh-HK", "zh-TW", "zh-CN"] }
        if t.hasPrefix("zh-hant") || t.hasPrefix("zh-tw") { return ["zh-TW", "zh-HK", "zh-CN"] }
        if t == "zh" || t.hasPrefix("zh-") || t == "cmn" || t.hasPrefix("cmn-") { return ["zh-CN", "zh-TW"] }
        if t == "en" || t.hasPrefix("en-") { return ["en-US", "en-GB", "en"] }
        return [String(t.split(separator: "-").first ?? "")]
    }

    /// Whether a voice for `voiceLanguage` (zh-CN) can read text in `tag` (zh-Hans).
    static func matches(_ voiceLanguage: String, _ tag: String) -> Bool {
        voiceLanguages(for: tag).contains { voiceLanguage.lowercased().hasPrefix($0.lowercased()) }
    }
}

/// Every voice to choose from, grouped into 推荐 · 我的 · 云端 · 系统自带.
enum Voices {
    enum RecommendedState {
        case unsupported
        case needsDownload
        case ready([any Voice])
    }

    struct Catalog {
        /// nil when the language has no built-in neural voices (e.g. Japanese).
        var recommended: RecommendedState?
        var clones: [ClonedEntry]
        var bookCards: [CardVoice]
        var cloud: [CloudVoice]
        var system: [SystemVoice]
    }

    static func catalog(for tag: String, bookID: String? = nil) -> Catalog {
        let recommended: RecommendedState?
        if !CardVoice.supportsLanguage(tag) {
            recommended = nil
        } else if KokoroModel.isInstalled || LiveVoiceModel.isInstalled {
            var list: [any Voice] = []
            if KokoroModel.isInstalled {
                list += KokoroModel.builtinVoices(for: tag)
            }
            if LiveVoiceModel.isInstalled {
                list += VoicePack.builtinCards(for: tag).map { CardVoice(card: $0, bookID: nil) }
            }
            recommended = list.isEmpty ? .needsDownload : .ready(list)
        } else if KokoroModel.deviceUnsupported && LiveVoiceModel.deviceUnsupported {
            recommended = .unsupported
        } else {
            recommended = .needsDownload
        }

        let clones = ClonedVoices.entries()
        let bookCards: [CardVoice] = if let bookID, LiveVoiceModel.isModelInstalled,
                                        let cards = try? LiveVoiceModel.context?.core.cardsOfBook(bookId: bookID) {
            cards.map { CardVoice(card: $0, bookID: bookID) }
        } else {
            []
        }
        return Catalog(
            recommended: recommended,
            clones: clones,
            bookCards: bookCards,
            cloud: CloudVoice.all().filter { $0.speaks(tag) },
            system: SystemVoice.all(for: tag)
        )
    }

    static func all(for tag: String) -> [any Voice] {
        all(for: tag, bookID: nil)
    }

    static func all(for tag: String, bookID: String?) -> [any Voice] {
        let cat = catalog(for: tag, bookID: bookID)
        var out: [any Voice] = []
        if case let .ready(cards) = cat.recommended {
            out += cards
        }
        for entry in cat.clones where entry.isPlayable {
            if let v = ClonedVoices.resolveVoice(entry.id, bookID: bookID), v.speaks(tag) {
                out.append(v)
            }
        }
        out += cat.bookCards.filter { $0.speaks(tag) }
        out += cat.cloud
        out += cat.system
        return out
    }

    static func find(_ id: String) -> (any Voice)? {
        find(id, bookID: nil)
    }

    static func find(_ id: String, bookID: String?) -> (any Voice)? {
        if id.hasPrefix("kokoro.") {
            return KokoroModel.find(id)
        }
        if id.hasPrefix("card.") {
            guard let ctx = LiveVoiceModel.context else { return nil }
            LiveVoiceModel.register(ctx.core, ctx.dataDir)
            if let card = ctx.core.cardFind(bookId: bookID, id: id) {
                return CardVoice(card: card, bookID: bookID)
            }
            if bookID == nil, id.hasPrefix("card.d."), let books = try? ctx.core.books() {
                for b in books {
                    if let card = ctx.core.cardFind(bookId: b.id, id: id) {
                        return CardVoice(card: card, bookID: b.id)
                    }
                }
            }
            return nil
        }
        if id.hasPrefix("cloned.") { return ClonedVoices.resolveVoice(id, bookID: bookID) }
        if id.hasPrefix("cloud.") { return CloudVoice.all().first { $0.id == id } }
        if id.hasPrefix("actor.") { return ActorVoice.all().first { $0.id == id } }
        return SystemVoice.all().first { $0.id == id }
    }

    /// "名字 · 高级"
    static func title(_ v: any Voice) -> String {
        [v.name, v.qualityName].compactMap { $0 }.joined(separator: " · ")
    }

    /// 试听: system voices speak straight away; a cloud voice is fetched, then played.
    @MainActor static func preview(_ voice: any Voice, text: String, failed: @escaping (String) -> Void = { _ in }) {
        if let v = voice as? SystemVoice { return SystemVoice.preview(v, text: text) }
        previewing?.cancel()
        PreviewState.shared.began(voice.id, seconds: nil)
        previewing = Task {
            do {
                guard let speech = try await voice.render(text, language: ""), !Task.isCancelled else {
                    PreviewState.shared.ended(voice.id)
                    return
                }
                Previewer.shared.play(speech, id: voice.id)
            } catch is CancellationError {
                // Pointed at another row, or stopped: not a failure.
                PreviewState.shared.ended(voice.id)
            } catch {
                PreviewState.shared.ended(voice.id)
                guard !Task.isCancelled else { return }
                PreviewState.shared.failed(voice.id)
                failed(error.localizedDescription)
            }
        }
    }

    /// Stops whatever 试听 is saying.
    @MainActor static func stopPreview() {
        previewing?.cancel()
        SystemVoice.stopPreview()
        Previewer.shared.stop()
        PreviewState.shared.ended(nil)
    }

    @MainActor private static var previewing: Task<Void, Never>?
}

/// Which voice 试听 is saying something with, and how far through it is: for the voice the page
/// shows as playing. `progress` is nil while a cloud voice is still being fetched.
@MainActor @Observable
final class PreviewState {
    static let shared = PreviewState()

    private(set) var voiceID: String?
    private(set) var started: Date?
    /// How long the sentence takes; nil until it's known (a system voice says it as it goes).
    private(set) var seconds: Double?
    /// The last 试听 that didn't come out: said quietly where it was tried, never as a dialog.
    private(set) var failedID: String?

    func began(_ id: String, seconds: Double?) {
        failedID = nil
        voiceID = id
        started = seconds == nil ? nil : .now
        self.seconds = seconds
    }

    /// Known only once it starts sounding.
    func sounding(_ id: String, seconds: Double) {
        guard voiceID == id else { return }
        started = .now
        self.seconds = seconds
    }

    func failed(_ id: String) { failedID = id }

    /// nil: whatever is playing; otherwise only if it's still that one.
    func ended(_ id: String?) {
        guard id == nil || voiceID == id else { return }
        voiceID = nil
        started = nil
        seconds = nil
    }

    /// 0…1 at `date`, or nil while it hasn't started sounding.
    func progress(at date: Date) -> Double? {
        guard let started, let seconds, seconds > 0 else { return nil }
        return min(1, date.timeIntervalSince(started) / seconds)
    }
}

/// Plays one rendered sentence, for 试听 with a cloud voice.
@MainActor private final class Previewer {
    static let shared = Previewer()
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()

    init() {
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: Speech.format)
    }

    func play(_ speech: Speech, id: String) {
        node.stop()
        if !engine.isRunning { try? engine.start() }
        generation += 1
        let mine = generation
        node.scheduleBuffer(speech.buffer, completionCallbackType: .dataPlayedBack) { _ in
            Task { @MainActor in
                guard Previewer.shared.generation == mine else { return }
                PreviewState.shared.ended(id)
            }
        }
        node.play()
        PreviewState.shared.sounding(id, seconds: speech.seconds)
    }

    func stop() {
        generation += 1
        node.stop()
    }

    /// Bumped for every sentence, so a stopped one's callback doesn't end the next.
    private var generation = 0
}
