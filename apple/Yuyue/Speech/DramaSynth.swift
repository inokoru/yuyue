import AVFoundation
import CryptoKit
import Foundation
import Observation
import YuyueCore
#if os(iOS)
import UIKit
#endif

/// 广播剧's voices: the core casts the book with the voices this device has, then hands out each
/// clip to make (a stretch in one voice); this renders it, listens back (the transcriber's text
/// against the book's, by reading), and hands both back. A clip that doesn't pass is made again,
/// then without acting, then by the narrator — the core decides. Clips are kept beside the
/// book; a scene is laid out and mixed by the core's mixer when it's played or exported.
@MainActor final class DramaSynth {
    static let shared = DramaSynth()
    /// Posted with the book's id when a section's clips are all made.
    static let sectionMade = Notification.Name("yuyue.dramaSectionMade")
    /// Posted with the book's id when 广播剧开关 / 旁白提示词 / 情感 / 环境音 / 音效 / 后台预合成 changes.
    static let settingsChanged = Notification.Name("yuyue.dramaSettingsChanged")
    /// Per-book Live cache cap on mobile / Live pre-synthesis (500 MB).
    nonisolated static let maxLiveCacheBytes: Int64 = 500_000_000

    /// Synthesis is wanted (the user asked for 整本提前做好, or a scene is being played).
    private(set) var on = false
    private var loops: [String: Task<Void, Never>] = [:]

    #if os(iOS)
    private struct PendingRun {
        let core: YuyueCore
        let dataDir: URL
        let language: String
        let section: UInt32
        let narrator: String?
    }
    private var waitingForPower: [String: PendingRun] = [:]
    private var batteryObserver: (any NSObjectProtocol)?
    #endif

    private init() {
        #if os(iOS)
        UIDevice.current.isBatteryMonitoringEnabled = true
        batteryObserver = NotificationCenter.default.addObserver(
            forName: UIDevice.batteryStateDidChangeNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            MainActor.assumeIsolated { self?.batteryChanged() }
        }
        #endif
    }

    #if os(iOS)
    /// 手机上整本提前合成只在接上电源时跑（模拟器状态为 `.unknown`，视作已接电以便调试）。
    static var isPluggedIn: Bool {
        #if DEBUG
        if UserDefaults.standard.bool(forKey: "debugDramaIgnoreBattery") { return true }
        #endif
        UIDevice.current.isBatteryMonitoringEnabled = true
        switch UIDevice.current.batteryState {
        case .charging, .full, .unknown:
            return true
        case .unplugged:
            return false
        @unknown default:
            return false
        }
    }

    private func batteryChanged() {
        if Self.isPluggedIn {
            let queued = waitingForPower
            waitingForPower.removeAll()
            for (book, run) in queued where Self.preparing(book) {
                start(
                    core: run.core,
                    dataDir: run.dataDir,
                    book: book,
                    language: run.language,
                    section: run.section,
                    narrator: run.narrator
                )
            }
        } else {
            for book in Array(loops.keys) {
                loops[book]?.cancel()
                loops[book] = nil
            }
            on = false
            if let ctx = LiveVoiceModel.context { DramaWork.tellCore(ctx.core) }
        }
    }
    #endif

    /// 广播剧是否对该书启用：以同步存储的 `dramaEnabled` 为唯一真源；
    /// 若已在其他设备关掉广播剧但本机仍残留 `drama.prepare.<book>`，自动清理停止。
    nonisolated static func isDramaActive(_ core: YuyueCore, _ book: String) -> Bool {
        let enabled = core.dramaEnabled(bookId: book)
        if !enabled, UserDefaults.standard.bool(forKey: "drama.prepare.\(book)") {
            UserDefaults.standard.set(false, forKey: "drama.prepare.\(book)")
            Task { @MainActor in
                DramaSynth.shared.stop(book)
            }
        }
        return enabled
    }

    /// 旁白回退升级：当即时层与声音包就绪且用户未手选非系统声音时，优先使用该语言默认旁白声音卡。
    static func effectiveNarrator(for language: String, chosen: String?) -> String? {
        if let chosen,
           chosen.hasPrefix("kokoro.") || chosen.hasPrefix("card.") || chosen.hasPrefix("cloned.") || chosen.hasPrefix("cloud.") || chosen.hasPrefix("actor.") {
            return chosen
        }
        if KokoroModel.isInstalled, CardVoice.supportsLanguage(language),
           let def = KokoroModel.defaultNarrator(for: language) {
            return def.id
        }
        if LiveVoiceModel.isInstalled, CardVoice.supportsLanguage(language),
           let def = VoicePack.defaultNarrator(for: language) {
            return def.id
        }
        return chosen ?? SystemVoice.best(for: language)?.id
    }

    /// The voices the core casts `language` with:
    /// - When Live voice cards are installed for zh/en: the 48 built-in cards + this book's
    ///   designed character cards (`card.d.*`), never `SystemVoice` or cloned cards.
    /// - Otherwise (e.g. Japanese or < 6 GB iPhone): `SystemVoice` fallback.
    /// - Plus any debug `ActorVoice`s and paid `CloudVoice`s when narrator is cloud.
    static func voices(for language: String, narrator: String?, book: String? = nil) -> [VoiceInfo] {
        var out: [VoiceInfo] = []
        if LiveVoiceModel.isInstalled, CardVoice.supportsLanguage(language), let ctx = LiveVoiceModel.context {
            LiveVoiceModel.register(ctx.core, ctx.dataDir)
            let builtin = VoicePack.builtinCards(for: language)
            let designed = book.flatMap { try? ctx.core.cardsOfBook(bookId: $0) } ?? []
            for card in builtin + designed {
                out.append(ctx.core.cardVoiceInfo(card: card, embedding: card.embedding, acts: true))
            }
        }
        if out.isEmpty {
            out += systemVoices(for: language)
        }
        out += actorVoices(language)
        if let n = narrator, n.hasPrefix("cloud.") {
            let provider = n.split(separator: ".").dropFirst().first.map(String.init)
            out += CloudVoice.all().filter { $0.provider.rawValue == provider && $0.speaks(language) }.map { v in
                let gender: CharacterGender = switch v.gender {
                case "male": .m
                case "female": .f
                default: .unknown
                }
                let age: CharacterAge = switch v.age {
                case "young": .young
                case "middle_aged", "middle aged": .middle
                case "old": .old
                default: .unknown
                }
                return VoiceInfo(id: v.id, langs: [v.language ?? language], gender: gender, age: age, tags: [], embedding: [], acts: true, narratorOk: true, loudnessLufs: -20)
            }
        }
        return out
    }

    /// Whether a book's play is cast with voices that say a line differently each time (the cloud
    /// ones, and a model on this Mac that acts).
    nonisolated static func castsCloud(narrator: String?) -> Bool {
        narrator?.hasPrefix("cloud.") == true || !ActorVoice.all().isEmpty
    }

    /// 本机会演的声音: a model on this Mac that acts, when one is set up; it reads every language.
    nonisolated private static func actorVoices(_ language: String) -> [VoiceInfo] {
        ActorVoice.all().map { v in
            let gender: CharacterGender = switch v.gender {
            case "male": .m
            case "female": .f
            default: .unknown
            }
            let age: CharacterAge = switch v.age {
            case "young": .young
            case "middle", "middle_aged": .middle
            case "old": .old
            default: .unknown
            }
            return VoiceInfo(id: v.id, langs: [language], gender: gender, age: age, tags: [], embedding: [], acts: true, narratorOk: true, loudnessLufs: -20)
        }
    }

    nonisolated private static func systemVoices(for language: String) -> [VoiceInfo] {
        SystemVoice.all(for: language).map { v in
            var gender: CharacterGender = switch v.gender {
            case .male: .m
            case .female: .f
            default: .unknown
            }
            var age: CharacterAge = .unknown
            // The system's character voices don't say their sex; it's the same in every language.
            if v.id.contains(".eloquence."), let known = eloquence[v.name] {
                gender = known.0
                age = known.1
            }
            return VoiceInfo(id: v.id, langs: [v.language], gender: gender, age: age, tags: [], embedding: [], acts: false, narratorOk: !v.id.contains(".eloquence."), loudnessLufs: -20)
        }
    }

    nonisolated private static let eloquence: [String: (CharacterGender, CharacterAge)] = [
        "Eddy": (.m, .young), "Reed": (.m, .middle), "Rocko": (.m, .young), "Grandpa": (.m, .old),
        "Flo": (.f, .young), "Sandy": (.f, .middle), "Shelley": (.f, .young), "Grandma": (.f, .old),
    ]

    /// Where a book's clips are kept.
    nonisolated static func clipsFolder(_ dataDir: URL, book: String) -> URL {
        dataDir.appending(path: "drama/\(book)/clips", directoryHint: .isDirectory)
    }

    /// Where Live-layer on-demand sentence audio is cached (kept separate from Studio/manifest clips).
    nonisolated static func liveClipsFolder(_ dataDir: URL, book: String) -> URL {
        dataDir.appending(path: "drama/\(book)/live-clips", directoryHint: .isDirectory)
    }

    /// A clip's file: AAC in CAF, one channel at 48 kHz, 64 kbps — three seconds in about 26 KB.
    /// Clips made before were float PCM under the same name; they're read the same way.
    nonisolated static func clipURL(_ dataDir: URL, book: String, key: String) -> URL {
        clipsFolder(dataDir, book: book).appending(path: key + ".caf")
    }

    nonisolated static func clipMade(_ dataDir: URL, book: String, key: String) -> Bool {
        FileManager.default.fileExists(atPath: clipURL(dataDir, book: book, key: key).path(percentEncoded: false))
    }

    nonisolated private static var clipSettings: [String: Any] {
        // 40 kbps mono AAC-LC is transparent for speech and cuts clip storage/sync size by ~37%.
        [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 1, AVEncoderBitRateKey: 40_000]
    }

    /// Keeps `buffer` (mono, 48 kHz) as clip `key`, written beside and moved in whole.
    nonisolated static func writeClip(_ buffer: AVAudioPCMBuffer, _ dataDir: URL, book: String, key: String) throws {
        try writeCAF(buffer, to: clipURL(dataDir, book: book, key: key))
    }

    nonisolated static func writeCAF(_ buffer: AVAudioPCMBuffer, to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let part = url.deletingPathExtension().appendingPathExtension("part.caf")
        try? FileManager.default.removeItem(at: part)
        do {
            let file = try AVAudioFile(forWriting: part, settings: clipSettings, commonFormat: .pcmFormatFloat32, interleaved: false)
            if buffer.frameLength > 0 { try file.write(from: buffer) }
        }
        try compactCAF(Data(contentsOf: part)).write(to: part)
        try? FileManager.default.removeItem(at: url)
        try FileManager.default.moveItem(at: part, to: url)
    }

    /// A CAF file without its `free` chunks. CAF's chunks hold no offsets into the file, so
    /// leaving one out moves nothing else.
    nonisolated static func compactCAF(_ data: Data) -> Data {
        guard data.count > 8, data.prefix(4) == Data("caff".utf8) else { return data }
        let base = data.startIndex
        var out = data.prefix(8)
        var at = 8
        while at + 12 <= data.count {
            let size = data[base + at + 4 ..< base + at + 12].reduce(Int64(0)) { $0 << 8 | Int64($1) }
            let end = size < 0 ? data.count : min(data.count, at + 12 + Int(size))
            if data[base + at ..< base + at + 4] != Data("free".utf8) { out.append(data[base + at ..< base + end]) }
            at = end
        }
        return out
    }

    /// Clip `key`'s samples, mono 48 kHz; nil when it isn't made.
    nonisolated static func readClip(_ dataDir: URL, book: String, key: String) -> [Float]? {
        readCAFSamples(clipURL(dataDir, book: book, key: key))
    }

    nonisolated static func readCAFSamples(_ url: URL) -> [Float]? {
        guard let f = try? AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false) else { return nil }
        guard f.length > 0 else { return [] }
        guard let b = AVAudioPCMBuffer(pcmFormat: f.processingFormat, frameCapacity: AVAudioFrameCount(f.length)) else { return nil }
        try? f.read(into: b)
        return b.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: Int(b.frameLength))) } ?? []
    }

    nonisolated static func readCAFSpeech(_ url: URL) -> Speech? {
        guard let samples = readCAFSamples(url), !samples.isEmpty else { return nil }
        guard let out = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: AVAudioFrameCount(samples.count)) else { return nil }
        out.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { out.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
        return Speech(buffer: out)
    }

    /// 整本提前做好, by book: kept on this device (the clips are).
    static func preparing(_ book: String) -> Bool { UserDefaults.standard.bool(forKey: "drama.prepare.\(book)") }
    static func setPreparing(_ book: String, _ on: Bool) { UserDefaults.standard.set(on, forKey: "drama.prepare.\(book)") }

    /// Casts `book` and makes its clips from `section` on (then from the start), until the whole
    /// book is made or it's stopped.
    func start(core: YuyueCore, dataDir: URL, book: String, language: String, section: UInt32, narrator: String?) {
        #if os(iOS)
        waitingForPower[book] = PendingRun(core: core, dataDir: dataDir, language: language, section: section, narrator: narrator)
        guard Self.isPluggedIn else { return }
        #endif
        guard loops[book] == nil else { return }
        on = true
        let resolvedNarrator = Self.effectiveNarrator(for: language, chosen: narrator)
        DramaWork.cloudCast = Self.castsCloud(narrator: resolvedNarrator)
        DramaWork.tellCore(core)
        let voices = Self.voices(for: language, narrator: resolvedNarrator, book: book)
        guard !voices.isEmpty else {
            on = !loops.isEmpty
            DramaWork.tellCore(core)
            return
        }
        let narratorID: String
        if CompanionUsage.cloudSaveNarrator,
           resolvedNarrator?.hasPrefix("cloud.") == true,
           LiveVoiceModel.isInstalled,
           CardVoice.supportsLanguage(language),
           let freeLocal = VoicePack.defaultNarrator(for: language)?.id,
           voices.contains(where: { $0.id == freeLocal }) {
            // Hybrid cast: free on-device narrator (~60–75% of text) + cloud character voices.
            narratorID = freeLocal
        } else {
            narratorID = resolvedNarrator ?? voices.first!.id
        }
        loops[book] = Task(priority: .utility) { [weak self] in
            await Self.syncRoleVoices(core, book: book)
            await SpeakerPrint.ensure(dataDir)
            if let sound = try? core.dramaSound(bookId: book), sound.beds || sound.spots { await SoundLibrary.ensure(core, dataDir) }
            await SpeakerPrint.printVoices(core, dataDir, voices: voices, language: language)
            let plan = await Self.castVoices(core, book: book, voices: voices, narrator: narratorID)
            if let plan { LiveCastCache.shared.store(book: book, plan: plan) }
            #if DEBUG
            NSLog("drama cast: \(voices.count) voices, plan \(plan.map { "\($0.voices.count) cast, narrator \($0.narrator), dialogue \($0.dialogue)" } ?? "none")")
            #endif
            try? FileManager.default.createDirectory(at: Self.clipsFolder(dataDir, book: book), withIntermediateDirectories: true)
            let chinese = language.hasPrefix("zh")
            let sections = UInt32(max(1, (try? core.book(id: book).sections.count) ?? 1))
            var focus = section
            var passed: UInt32 = 0
            while !Task.isCancelled, passed < sections {
                #if os(iOS)
                let plugged = await MainActor.run { Self.isPluggedIn }
                if !plugged { break }
                #endif
                let f = focus
                guard let task = await Self.nextTask(core, book: book, focusSection: f) else {
                    Self.pruneLiveCacheIfNeeded(dataDir, book: book, keepFromSection: f)
                    focus = (focus + 2) % sections
                    passed += 2
                    NotificationCenter.default.post(name: Self.sectionMade, object: book)
                    continue
                }
                let (id, result): (String, DramaResult) = switch task {
                case let .tts(id, clip):
                    (id, await Self.make(core, dataDir, book: book, clip: clip, language: language, chinese: chinese, fallback: narratorID))
                case let .llm(id, _, _, _, _, _, _):
                    (id, .returned)
                case let .designVoice(id, _, description, sample):
                    (id, await Self.design(description: description, sample: sample, language: language))
                case let .sfx(id, _, description, seconds):
                    (id, await Self.sound(description: description, seconds: seconds))
                }
                if case .returned = result, !id.hasPrefix("tts:") { break }
                await Self.submitTask(core, book: book, id: id, result: Task.isCancelled ? .returned : result)
                if case .voice = result { LiveCastCache.shared.invalidate(book: book) }
                #if DEBUG
                if case let .clip(made, check) = result {
                    NSLog("drama clip \(made.key.prefix(8)) \(String(format: "%.1f", made.seconds))s cer \(String(format: "%.2f", check.cer)) missing \(check.longestMissing)")
                } else {
                    NSLog("drama task \(id.prefix(30)) → \(result)")
                }
                #endif
            }
            self?.loops[book] = nil
            self?.on = !(self?.loops.isEmpty ?? true)
            DramaWork.tellCore(core)
            NotificationCenter.default.post(name: Self.sectionMade, object: book)
        }
    }

    @concurrent nonisolated static func syncRoleVoices(_ core: YuyueCore, book: String) async {
        // Core reads `roles/book/{id}` directly as single source of truth; no one-way copy needed.
        _ = (core, book)
    }

    nonisolated private static func applyClipTuning(_ core: YuyueCore, book: String, clip: ClipAsk, speech: Speech) -> Speech {
        guard let tuning = core.dramaClipTuning(bookId: book, section: clip.section, sentence: clip.sentence, start: clip.start) else {
            return speech
        }
        guard abs(tuning.rate - 1.0) >= 0.01 || abs(tuning.pitch) >= 0.01 else {
            return speech
        }
        return TunedVoice.tune(speech, rate: Float(tuning.rate), pitch: Float(tuning.pitch)) ?? speech
    }

    @concurrent nonisolated static func castVoices(_ core: YuyueCore, book: String, voices: [VoiceInfo], narrator: String) async -> CastingPlan? {
        try? core.dramaCastVoices(bookId: book, voices: voices, narrator: narrator)
    }

    @concurrent nonisolated static func nextTask(_ core: YuyueCore, book: String, focusSection: UInt32) async -> DramaTask? {
        try? core.dramaNextTask(bookId: book, focusSection: focusSection)
    }

    @concurrent nonisolated static func submitTask(_ core: YuyueCore, book: String, id: String, result: DramaResult) async {
        _ = try? core.dramaSubmit(bookId: book, taskId: id, result: result)
    }

    /// A task that isn't a question for the language model: its id.
    nonisolated static func notModel(_ task: DramaTask) -> String? {
        switch task {
        case .llm: nil
        case let .tts(id, _), let .designVoice(id, _, _, _), let .sfx(id, _, _, _): id
        }
    }

    /// 造声音: 由外部本地语音服务 `ActorVoice.design` 生成角色声音。
    @concurrent nonisolated static func design(
        description: String,
        sample: String,
        language: String
    ) async -> DramaResult {
        do {
            guard let v = try await ActorVoice.design(description: description, sample: sample, language: language) else { return .returned }
            let gender: CharacterGender = v.gender == "male" ? .m : v.gender == "female" ? .f : .unknown
            return .voice(voice: VoiceInfo(id: v.id, langs: [language], gender: gender, age: .unknown, tags: [], embedding: [], acts: true, narratorOk: false, loudnessLufs: -20))
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    /// 生成音效: a sound the book writes, made by the sound model.
    @concurrent nonisolated static func sound(description: String, seconds: Float) async -> DramaResult {
        do {
            guard let speech = try await SfxMaker.make(description: description, seconds: Double(seconds)) else { return .returned }
            let n = Int(speech.buffer.frameLength)
            let samples = speech.buffer.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: n)) } ?? []
            return .sound(samples: samples)
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    func stop(_ book: String) {
        #if os(iOS)
        waitingForPower[book] = nil
        #endif
        loops[book]?.cancel()
        loops[book] = nil
        on = !loops.isEmpty
        if let ctx = LiveVoiceModel.context { DramaWork.tellCore(ctx.core) }
    }

    /// One clip: rendered in its voice (Live CardVoice / Actor / Cloud / System), kept as a file, and listened back.
    @concurrent nonisolated static func make(
        _ core: YuyueCore,
        _ dataDir: URL,
        book: String,
        clip: ClipAsk,
        language: String,
        chinese: Bool,
        fallback: String
    ) async -> DramaResult {
        let found: (any Voice)? = await MainActor.run {
            Voices.find(clip.voice, bookID: book) ?? Voices.find(fallback, bookID: book)
        }
        guard let voice = found else { return .failed(reason: "no voice") }
        let parts = core.dramaSpokenParts(text: clip.text)
        let plain = parts.map(\.text).joined()
        do {
            let rendered: Speech?
            if let cloud = voice as? CloudVoice {
                rendered = try await cloud.render(parts: parts, language: language, acting: acting(core, clip.act, chinese: chinese))
            } else if let actor = voice as? ActorVoice {
                rendered = try await actor.render(plain, instruction: core.dramaActInstruction(act: clip.act, chinese: chinese))
            } else if let cardVoice = voice as? CardVoice {
                let style = core.cardStyleFor(act: clip.act)
                rendered = try await cardVoice.render(clip.text, language: language, style: style)
            } else {
                rendered = try await voice.render(plain, language: language)
            }
            guard let rawSpeech = rendered else {
                if plain.contains(where: { $0.isLetter || $0.isNumber }) { return .failed(reason: "the voice gave nothing") }
                return .clip(clip: MadeClip(key: clip.key, seconds: 0, charTimes: []), check: ClipCheck(cer: 0, longestMissing: 0, voiceSim: 1, clipped: false, silenceHead: 0, silenceTail: 0, seconds: 0, chars: 0))
            }
            let speech = Self.applyClipTuning(core, book: book, clip: clip, speech: rawSpeech)
            try writeClip(speech.buffer, dataDir, book: book, key: clip.key)
            let url = clipURL(dataDir, book: book, key: clip.key)
            let n = Int(speech.buffer.frameLength)
            let samples = speech.buffer.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: n)) } ?? []
            var heard = plain
            if Transcriber.available {
                let r = await Transcriber.words(url: url, spans: [TimeSpan(t0: 0, t1: speech.seconds)], lang: chinese ? "zh" : "en", offset: 0)
                if case let .transcribe(words) = r { heard = words.map { $0.text }.joined(separator: chinese ? "" : " ") }
            }
            let print = await SpeakerPrint.print(core, dataDir, samples: samples, rate: Speech.format.sampleRate)
            let sim = print.map { core.speakerSimilarity(voice: clip.voice, embedding: $0) } ?? 1
            let check = core.dramaMeasureClip(text: clip.text, heard: heard, samples: samples, rate: UInt32(Speech.format.sampleRate), voiceSim: sim, chinese: chinese)
            #if DEBUG
            if check.cer > 0.08 { NSLog("drama check: “\(clip.text)” heard “\(heard)” cer \(check.cer)") }
            #endif
            return .clip(clip: MadeClip(key: clip.key, seconds: speech.seconds, charTimes: []), check: check)
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    /// A sentence of the play: its stretch of the scene, mixed — the voices where they stand,
    /// the room's echo, the room tone and the sounds the book writes, and the pause after it.
    /// nil while any of its clips isn't made (the voice reads it as usual).
    @concurrent nonisolated static func sentence(_ core: YuyueCore, _ dir: URL, book: String, section: UInt32, sentence: UInt32) async -> Speech? {
        SoundLibrary.register(core, dir)
        guard let w = (try? core.dramaSentenceWindow(bookId: book, section: section, sentence: sentence)) ?? nil else { return nil }
        var sources: [MixSource] = []
        for k in Set(w.clips) {
            guard let samples = readClip(dir, book: book, key: k) else { return nil }
            sources.append(MixSource(id: k, samples: samples, stereo: false))
        }
        let mono = DramaMixer(timeline: w.timeline, sources: sources).renderSpan(from: w.from, to: w.to)
        guard !mono.isEmpty, let out = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: AVAudioFrameCount(mono.count)) else { return nil }
        out.frameLength = AVAudioFrameCount(mono.count)
        mono.withUnsafeBufferPointer { out.floatChannelData![0].update(from: $0.baseAddress!, count: mono.count) }
        return Speech(buffer: out)
    }

    /// 当某章尚未完成精做（`DramaLayer == .live`）时，若本机已做好该句混音则直接播，
    /// 否则走统一的核心分角、读音标注、情感语气、快慢高低与 `DramaMixer` 混音管线（带环境音/音效/声场/混响），
    /// 落入 500 MB 上限的按章缓存。
    @concurrent nonisolated static func liveSentence(
        _ core: YuyueCore,
        _ dir: URL,
        book: String,
        section: UInt32,
        sentence: UInt32,
        language: String,
        narratorFallback: String?
    ) async -> Speech? {
        if let mixed = await Self.sentence(core, dir, book: book, section: section, sentence: sentence) {
            return mixed
        }
        guard let plan = await LiveCastCache.shared.plan(core: core, dataDir: dir, book: book, language: language, narrator: narratorFallback) else {
            return nil
        }
        SoundLibrary.register(core, dir)
        guard let asks = try? core.dramaLiveSentenceAsks(bookId: book, section: section, sentence: sentence), !asks.isEmpty else {
            return nil
        }
        let dramaMode = core.dramaMode(bookId: book)
        let sound = (try? core.dramaSound(bookId: book)) ?? DramaSound(acting: true, beds: true, spots: true)
        let chinese = language.hasPrefix("zh")

        var keyRaw = "v2:\(section):\(sentence):\(dramaMode):\(sound.acting):\(sound.beds):\(sound.spots)"
        for ask in asks {
            let tuning = core.dramaClipTuning(bookId: book, section: ask.section, sentence: ask.sentence, start: ask.start)
            let rateKey = tuning.map { String(format: "%.2f", $0.rate) } ?? "1.00"
            let pitchKey = tuning.map { String(format: "%.1f", $0.pitch) } ?? "0.0"
            keyRaw += "|\(ask.key)#\(rateKey)#\(pitchKey)"
        }
        let digest = SHA256.hash(data: Data(keyRaw.utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
        let cachedURL = liveClipsFolder(dir, book: book)
            .appending(path: "s-\(String(format: "%05d", section))/\(sentence)-\(digest).caf")
        if let hit = readCAFSpeech(cachedURL) {
            return hit
        }

        var madeClips: [MadeClip] = []
        var sources: [MixSource] = []
        var fallbackBuffers: [AVAudioPCMBuffer] = []
        let gapFrames = AVAudioFrameCount(Speech.format.sampleRate * 0.08)

        for (idx, ask) in asks.enumerated() {
            if Task.isCancelled { return nil }
            let chosenVoice: (any Voice)? = await MainActor.run {
                Voices.find(ask.voice, bookID: book)
                    ?? Voices.find(plan.narrator, bookID: book)
                    ?? SystemVoice.best(for: language)
            }
            guard let voice = chosenVoice else { continue }
            let parts = core.dramaSpokenParts(text: ask.text)
            let plain = parts.map(\.text).joined()
            let rendered: Speech?
            if let cardVoice = voice as? CardVoice {
                let style: CardStyle = sound.acting ? core.cardStyleFor(act: ask.act) : .neutral
                rendered = try? await cardVoice.render(ask.text, language: language, style: style)
            } else if let cloud = voice as? CloudVoice {
                rendered = try? await cloud.render(parts: parts, language: language, acting: sound.acting ? acting(core, ask.act, chinese: chinese) : nil)
            } else if let actor = voice as? ActorVoice {
                let instr = sound.acting ? core.dramaActInstruction(act: ask.act, chinese: chinese) : ""
                rendered = try? await actor.render(plain, instruction: instr)
            } else {
                rendered = try? await voice.render(plain, language: language)
            }
            guard let rawSpeech = rendered, rawSpeech.buffer.frameLength > 0 else {
                madeClips.append(MadeClip(key: ask.key, seconds: 0, charTimes: []))
                continue
            }
            let speech = applyClipTuning(core, book: book, clip: ask, speech: rawSpeech)
            let n = Int(speech.buffer.frameLength)
            let samples = speech.buffer.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: n)) } ?? []
            madeClips.append(MadeClip(key: ask.key, seconds: speech.seconds, charTimes: []))
            sources.append(MixSource(id: ask.key, samples: samples, stereo: false))
            if !fallbackBuffers.isEmpty, idx > 0, let gap = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: gapFrames) {
                gap.frameLength = gapFrames
                if let ch = gap.floatChannelData { ch[0].update(repeating: 0, count: Int(gapFrames)) }
                fallbackBuffers.append(gap)
            }
            fallbackBuffers.append(speech.buffer)
        }
        guard !sources.isEmpty else { return nil }

        let finalSpeech: Speech?
        if let w = (try? core.dramaLiveSentenceWindow(bookId: book, section: section, sentence: sentence, clips: madeClips)) ?? nil {
            let mono = DramaMixer(timeline: w.timeline, sources: sources).renderSpan(from: w.from, to: w.to)
            if !mono.isEmpty, let out = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: AVAudioFrameCount(mono.count)) {
                out.frameLength = AVAudioFrameCount(mono.count)
                mono.withUnsafeBufferPointer { out.floatChannelData![0].update(from: $0.baseAddress!, count: mono.count) }
                finalSpeech = Speech(buffer: out)
            } else {
                finalSpeech = Speech.make(from: fallbackBuffers)
            }
        } else {
            finalSpeech = Speech.make(from: fallbackBuffers)
        }
        guard let result = finalSpeech else { return nil }
        try? writeCAF(result.buffer, to: cachedURL)
        pruneLiveCacheIfNeeded(dir, book: book, keepFromSection: section)
        return result
    }

    /// 单本书即时层广播剧缓存上限 500 MB，超出时按章节顺序清理当前章之前最早的缓存。
    nonisolated static func pruneLiveCacheIfNeeded(_ dataDir: URL, book: String, keepFromSection: UInt32) {
        let root = liveClipsFolder(dataDir, book: book)
        let fm = FileManager.default
        guard let subdirs = try? fm.contentsOfDirectory(at: root, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles]) else {
            return
        }
        struct SecFolder {
            let section: UInt32
            let url: URL
            let bytes: Int64
        }
        var items: [SecFolder] = []
        var total: Int64 = 0
        for dir in subdirs {
            let name = dir.lastPathComponent
            let sec = name.hasPrefix("s-") ? UInt32(name.dropFirst(2)) ?? 0 : 0
            var size: Int64 = 0
            if let files = try? fm.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey]) {
                for f in files {
                    size += Int64((try? f.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
                }
            }
            total += size
            items.append(SecFolder(section: sec, url: dir, bytes: size))
        }
        guard total > maxLiveCacheBytes else { return }
        // 先清当前章节之前的旧章节，仍超限再从最早章节清。
        let ordered = items.sorted {
            let aBefore = $0.section < keepFromSection
            let bBefore = $1.section < keepFromSection
            if aBefore != bBefore { return aBefore }
            return $0.section < $1.section
        }
        for item in ordered where item.section != keepFromSection {
            guard total > maxLiveCacheBytes else { break }
            try? fm.removeItem(at: item.url)
            total -= item.bytes
        }
    }

    /// The acting a clip asks for, in the cloud voices' terms; nil for none.
    nonisolated private static func acting(_ core: YuyueCore, _ act: DramaAct, chinese: Bool) -> CloudVoice.Acting? {
        let instruction = core.dramaActInstruction(act: act, chinese: chinese)
        let tags = core.dramaActTags(act: act)
        var style: String? = switch act.volume {
        case .whisper: "whispering"
        case .shout: "shouting"
        default: nil
        }
        if style == nil, act.level > 0 {
            style = switch act.emotion {
            case .joy: "cheerful"
            case .anger: "angry"
            case .sadness: "sad"
            case .fear, .anxious: "fearful"
            case .disgust: "disgruntled"
            case .tender: "gentle"
            case .mocking: "unfriendly"
            case .weary: "depressed"
            case .surprise: "excited"
            default: nil
            }
        }
        let a = CloudVoice.Acting(instruction: instruction, head: tags.head, tail: tags.tail, style: style, degree: [0.8, 0.8, 1.2, 1.6][Int(min(act.level, 3))])
        return a.isFlat ? nil : a
    }

    /// A section as one stretch of sound, scene after scene through the core's mixer, as WAV
    /// (导出, and checking by ear). Clips not made yet are left out.
    @concurrent nonisolated static func mixSection(_ core: YuyueCore, _ dataDir: URL, book: String, section: UInt32) async -> Data? {
        SoundLibrary.register(core, dataDir)
        guard let timelines = try? core.dramaTimelines(bookId: book, section: section, library: []), !timelines.isEmpty else { return nil }
        var body = Data()
        var header: Data?
        for tl in timelines {
            var sources: [MixSource] = []
            var seen = Set<String>()
            for item in tl.items {
                guard case let .voice(clip, _, _, _, _, _, _, _, _, _) = item, seen.insert(clip).inserted else { continue }
                guard let samples = readClip(dataDir, book: book, key: clip) else { continue }
                sources.append(MixSource(id: clip, samples: samples, stereo: false))
            }
            let wav = DramaMixer(timeline: tl, sources: sources).renderWav()
            if header == nil { header = wav.prefix(44) }
            body.append(wav.dropFirst(44))
        }
        guard var out = header else { return nil }
        let bytes = UInt32(body.count)
        out.replaceSubrange(4..<8, with: withUnsafeBytes(of: (bytes + 36).littleEndian) { Data($0) })
        out.replaceSubrange(40..<44, with: withUnsafeBytes(of: bytes.littleEndian) { Data($0) })
        out.append(body)
        return out
    }

    /// 导出成有声书: the sections made so far, mixed, as one M4B (AAC) with a chapter each.
    @concurrent nonisolated static func exportM4B(_ core: YuyueCore, _ dataDir: URL, book: String, to out: URL) async -> Bool {
        guard let made = try? core.dramaMade(bookId: book), !made.madeSections.isEmpty,
              let detail = try? core.book(id: book) else { return false }
        let m4a = out.deletingPathExtension().appendingPathExtension("m4a")
        try? FileManager.default.removeItem(at: m4a)
        let settings: [String: Any] = [AVFormatIDKey: kAudioFormatMPEG4AAC, AVSampleRateKey: 48_000, AVNumberOfChannelsKey: 2, AVEncoderBitRateKey: 96_000]
        guard let pcm = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 48_000, channels: 2, interleaved: false) else { return false }
        var marks: [AudioMark] = []
        var frames: AVAudioFramePosition = 0
        do {
            let file = try AVAudioFile(forWriting: m4a, settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
            for s in made.madeSections {
                try Task.checkCancellation()
                guard let wav = await mixSection(core, dataDir, book: book, section: s), wav.count > 44 else { continue }
                let n = (wav.count - 44) / 4
                guard let buf = AVAudioPCMBuffer(pcmFormat: pcm, frameCapacity: AVAudioFrameCount(n)) else { continue }
                buf.frameLength = AVAudioFrameCount(n)
                wav.dropFirst(44).withUnsafeBytes { raw in
                    let v = raw.bindMemory(to: Int16.self)
                    let l = buf.floatChannelData![0], r = buf.floatChannelData![1]
                    for k in 0..<n {
                        l[k] = Float(Int16(littleEndian: v[2 * k])) / 32768
                        r[k] = Float(Int16(littleEndian: v[2 * k + 1])) / 32768
                    }
                }
                let title = detail.sections.indices.contains(Int(s)) ? detail.sections[Int(s)].title : nil
                marks.append(AudioMark(title: title, start: Double(frames) / 48_000))
                try file.write(from: buf)
                frames += AVAudioFramePosition(n)
            }
        } catch {
            return false
        }
        guard frames > 0 else { return false }
        try? core.dramaFinishM4b(bookId: book, path: m4a.path(percentEncoded: false), marks: marks)
        try? FileManager.default.removeItem(at: out)
        do { try FileManager.default.moveItem(at: m4a, to: out) } catch { return false }
        return true
    }
}

/// 缓存即时层按句播放所需的分角方案与章节剧本，选声/认人变动时自动失效。
nonisolated final class LiveCastCache: Sendable {
    static let shared = LiveCastCache()

    private struct State {
        var plans: [String: CastingPlan] = [:]
        var sections: [String: (section: UInt32, value: DramaSection)] = [:]
        var casts: [String: [DramaCharacter]] = [:]
    }

    private let lock = Mutex(State())

    private init() {
        let center = NotificationCenter.default
        for raw in ["yuyue.castChanged", "yuyue.dramaUpdated", "yuyue.dramaSettingsChanged", "yuyue.clonedVoicesChanged", "yuyue.liveVoiceReady"] {
            center.addObserver(forName: Notification.Name(raw), object: nil, queue: nil) { note in
                if let book = note.object as? String {
                    LiveCastCache.shared.invalidate(book: book)
                } else {
                    LiveCastCache.shared.invalidateAll()
                }
            }
        }
    }

    func store(book: String, plan: CastingPlan) {
        lock.withLock {
            $0.plans[book] = plan
        }
    }

    func invalidate(book: String) {
        lock.withLock {
            $0.plans[book] = nil
            $0.sections[book] = nil
            $0.casts[book] = nil
        }
    }

    func invalidateAll() {
        lock.withLock {
            $0.plans.removeAll()
            $0.sections.removeAll()
            $0.casts.removeAll()
        }
    }

    @concurrent func plan(core: YuyueCore, dataDir: URL, book: String, language: String, narrator: String?) async -> CastingPlan? {
        if let existing = lock.withLock({ $0.plans[book] }) { return existing }
        await DramaSynth.syncRoleVoices(core, book: book)
        let voices = await MainActor.run {
            let resolved = DramaSynth.effectiveNarrator(for: language, chosen: narrator)
            return DramaSynth.voices(for: language, narrator: resolved, book: book)
        }
        guard !voices.isEmpty else { return nil }
        await SpeakerPrint.printVoices(core, dataDir, voices: voices, language: language)
        let resolvedNarrator = await MainActor.run {
            DramaSynth.effectiveNarrator(for: language, chosen: narrator) ?? voices.first!.id
        }
        guard let made = try? core.dramaCastVoices(bookId: book, voices: voices, narrator: resolvedNarrator) else {
            return nil
        }
        store(book: book, plan: made)
        return made
    }

    func section(core: YuyueCore, book: String, section: UInt32) -> DramaSection? {
        if let hit = lock.withLock({ $0.sections[book] }), hit.section == section {
            return hit.value
        }
        guard let loaded = try? core.dramaSection(bookId: book, section: section) else { return nil }
        lock.withLock { $0.sections[book] = (section, loaded) }
        return loaded
    }

    func characters(core: YuyueCore, book: String) -> [DramaCharacter] {
        if let hit = lock.withLock({ $0.casts[book] }) { return hit }
        let loaded = (try? core.dramaCast(bookId: book, uptoSection: UInt32.max)) ?? []
        lock.withLock { $0.casts[book] = loaded }
        return loaded
    }
}
