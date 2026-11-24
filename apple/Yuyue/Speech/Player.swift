import AVFoundation
import MediaPlayer
import YuyueCore
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// Reads a book aloud one sentence at a time, for every voice alike.
///
/// A sentence is rendered to audio before it plays, so its length is known exactly and the
/// reader's ink line keeps pace with the voice. Two sentences are kept rendered ahead, so
/// sentences and chapters follow one another without a gap. Speed is applied while playing,
/// so changing it takes effect at once. The lock screen shows the book and takes play, pause,
/// next and previous sentence.
@Observable
final class Player {
    enum State { case stopped, loading, playing, paused }

    /// A sentence of the book.
    nonisolated struct Spot: Equatable, Sendable {
        var section: UInt32
        var sentence: Int

        var position: Position { Position(section: section, sentence: UInt32(max(0, sentence))) }
    }

    /// 几句一起循环的范围（同一章节内）。
    nonisolated struct LoopRange: Equatable, Sendable {
        var section: UInt32
        var first: Int
        var last: Int
    }

    private(set) var state = State.stopped
    /// The sentence being read or paused on.
    private(set) var spot: Spot?
    /// Speed, 0.5–3.
    var rate: Float = 1 {
        didSet { if rate != oldValue { rateChanged(from: oldValue) } }
    }
    var failure: String?
    /// 设备过热（`.critical`）回退系统声音时在朗读面板顶部温和告知，进下一章恢复后自动消失。
    private(set) var thermalNotice: String?
    /// 离线默认旁白在播放/暂停期间下载完成后，提供给用户确认切换的候选声音（不自动静默切声）。
    private(set) var readyOfflineVoice: (any Voice)?
    /// 睡眠定时: when to stop on its own. It always stops between sentences.
    var sleep = Sleep.off {
        didSet { if sleep != oldValue { sleepChanged(from: oldValue) } }
    }
    /// Where the timer was set: offered the next time the book opens (回到昨晚).
    @ObservationIgnored private(set) var sleepFrom: Spot?
    /// The last 20 seconds of a timer: the voice fading; a key or a shake gives 10 minutes more.
    private(set) var fading = false
    @ObservationIgnored private var fadeTask: Task<Void, Never>?
    /// 单句循环: reads the same sentence over and over — or, with `loopTo`, a few sentences
    /// (from where it was when the end was chosen, to that end).
    var loop = false {
        didSet { if !loop { loopRange = nil } }
    }
    /// 几句一起循环: the first and last sentence read over and over (in one section).
    private(set) var loopRange: LoopRange?
    /// 念完停一下: quiet after each sentence, to say it after the voice.
    enum PauseAfter: Equatable { case off, seconds(Double), asLong }
    var pauseAfter = PauseAfter.off
    /// 有声书: the narrator's recording of the book, read instead of a voice where it has the
    /// sentence. Sentences it doesn't have are read by the voice (用朗读补上).
    @ObservationIgnored var recording: Recording?
    /// Where the first sentence starts when it isn't its own start (搜索 found a word in it).
    @ObservationIgnored private var startWord: (Spot, Double)?
    /// 原声: listening to the recording rather than a voice.
    var usesRecording = true {
        didSet { if usesRecording != oldValue { restartHere() } }
    }
    /// Whether the narrator is reading now.
    var narrated: Bool { recording != nil && usesRecording }
    /// 有声书: what the map says — sentences to pass, talk not in the book to play or pass.
    @ObservationIgnored var guide: RecordingGuide?
    /// Talk in the recording that isn't in the book, being played now.
    private(set) var extraPlaying: ExtraSpan?
    /// 片头 passed as listening began, for the capsule to offer it for 5 seconds.
    private(set) var skippedHead: ExtraSpan?
    /// Play the 片头 before the next sentence, once.
    @ObservationIgnored private var wantHead = false

    /// What is read, one section after another: a book's chapters from the core, or a PDF's
    /// pages, each a section of its own.
    struct Script {
        var sentences: (UInt32) -> [String]
        /// The next section with something to read, and the one before.
        var next: (UInt32) -> UInt32?
        var previous: (UInt32) -> UInt32?
        var language: (UInt32) -> String
        /// What the lock screen calls the section.
        var title: (UInt32) -> String?
        /// 播客: the voice for one sentence (its host's); none reads the whole book in one voice.
        var voice: ((UInt32, Int, String) -> (any Voice)?)? = nil
        /// 分角色: the characters' own voices, where the reader has given any.
        var roles: BookRoles? = nil

        static func book(_ core: YuyueCore, _ detail: BookDetail) -> Script {
            let id = detail.summary.id
            return Script(
                sentences: { (try? core.spokenSentences(bookId: id, section: $0)) ?? [] },
                next: { (try? core.nextSection(bookId: id, section: $0, speech: true)) ?? nil },
                previous: { (try? core.previousSection(bookId: id, section: $0, speech: true)) ?? nil },
                language: { i in detail.sections.indices.contains(Int(i)) ? detail.sections[Int(i)].lang : detail.summary.language },
                title: { detail.chapterTitle(for: $0) })
        }
    }

    nonisolated enum Sleep: Equatable, Sendable {
        case off
        /// 本句读完
        case sentence
        /// 本章结束
        case chapter
        /// After some minutes: the moment to stop.
        case until(Date, minutes: Int)
    }

    nonisolated enum PlayedAction: Equatable, Sendable {
        case sleepAt(Spot)
        case loopTo(Spot)
        case advance
    }

    /// The ink line: the sentence, its length and how much of it has been read (both in ms at
    /// the current speed), and whether it is moving.
    @ObservationIgnored var onSentence: ((Spot, Double, Double, Bool) -> Void)?
    @ObservationIgnored var onStop: (() -> Void)?
    /// The book was read to its last sentence (after `onStop`): 连着听 moves on to the next episode.
    @ObservationIgnored var onFinish: (() -> Void)?
    /// The sleep timer stopped the voice, paused on this sentence.
    @ObservationIgnored var onSleep: ((Spot) -> Void)?
    /// 双击耳机 (下一曲 to the headphones): the sentence just heard, to highlight.
    @ObservationIgnored var onMarkHeard: ((Spot) -> Void)?
    /// The listener explicitly jumped to another sentence (skip / skipExtra).
    @ObservationIgnored var onSkip: (() -> Void)?

    @ObservationIgnored private let detail: BookDetail
    /// 《书名》, for the 换声音 panel's 试听 line.
    var bookTitle: String { detail.summary.title }
    @ObservationIgnored private let script: Script
    @ObservationIgnored private let engine = AVAudioEngine()
    @ObservationIgnored private let node = AVAudioPlayerNode()
    @ObservationIgnored private let speed = AVAudioUnitTimePitch()

    private struct Line {
        let spot: Spot
        let speech: Speech
        /// The recording's own talk after `spot`, not in the book.
        var extra: ExtraSpan? = nil
    }

    /// Bumped whenever the queue is thrown away, so late callbacks for it are ignored.
    @ObservationIgnored private var generation = 0
    /// Bumped whenever staged/in-flight rendering is discarded without stopping the playing node.
    @ObservationIgnored private var renderToken = 0
    /// Sentences handed to the audio node, in order; the first is the one playing.
    @ObservationIgnored private var queued: [Line] = []
    /// Sentences already rendered ahead in memory, waiting to be handed to the audio node.
    @ObservationIgnored private var staged: [Line] = []
    /// Whether the first queued sentence has started.
    @ObservationIgnored private var started = false
    @ObservationIgnored private var nextToRender: Spot?
    @ObservationIgnored private var rendering: Task<Void, Never>?
    /// Seconds of the playing sentence's audio heard before the last resume.
    @ObservationIgnored private var heard: Double = 0
    @ObservationIgnored private var resumedAt: Date?
    @ObservationIgnored private var texts: [UInt32: [String]] = [:]
    @ObservationIgnored private var voices: [String: (any Voice)?] = [:]
    /// 上一次因朗读出错而停下时的位置，供「重试」或「换成系统声音」后从原地续播。
    @ObservationIgnored private var failedSpot: Spot?
    /// 整章锁定的广播剧音质层（`Studio` vs `Live`），绝不在章内句间切换音质。
    @ObservationIgnored private var sectionLayers: [UInt32: DramaLayer] = [:]
    /// 设备 `.critical` 过热时回退到系统声音的章节号。
    @ObservationIgnored private var thermalFallbackSection: UInt32?
    @ObservationIgnored private var thermalState: ProcessInfo.ThermalState = ProcessInfo.processInfo.thermalState
    @ObservationIgnored private var fetchingAhead: (section: UInt32, sentence: Int, voice: String, task: Task<Void, Never>)?
    /// Sliding prefetch window for paid cloud voices: keeps the next few sentences warm
    /// without billing for the entire rest of the chapter on a quick preview or early stop.
    private static let cloudPrefetchWindow = 4
    @ObservationIgnored private var artwork: MPMediaItemArtwork?
    @ObservationIgnored private var observers: [any NSObjectProtocol] = []
    @ObservationIgnored private var remoteTargets: [(MPRemoteCommand, Any)] = []
    #if os(iOS)
    @ObservationIgnored private var resumeAfterInterruption = false
    #endif
    private static weak var remoteOwner: Player?
    private static let ahead = 2
    private static let nodeAhead = 2
    private static let liveAheadNormalSeconds: Double = 60
    private static let liveAheadSeriousSeconds: Double = 15
    private static let studioEngineID = "studio-qwen3tts-1.7b-1"

    /// For 听成一部剧's clips.
    @ObservationIgnored private let core: YuyueCore

    init(core: YuyueCore, detail: BookDetail, script: Script? = nil) {
        self.detail = detail
        self.core = core
        self.script = script ?? .book(core, detail, roles: BookRoles(core: core, bookID: detail.summary.id))
        engine.attach(node)
        engine.attach(speed)
        engine.connect(node, to: speed, format: Speech.format)
        engine.connect(speed, to: engine.mainMixerNode, format: Speech.format)
        #if DEBUG
        // `-debugMute 1`: plays without a sound, for trying the player while someone works nearby.
        if UserDefaults.standard.bool(forKey: "debugMute") { node.volume = 0 }
        #endif
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            // The output changed (headphones, AirPlay): the engine stopped, so start again where we were.
            MainActor.assumeIsolated { self?.restartHere() }
        })
        observers.append(center.addObserver(forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.thermalChanged() }
        })
        observers.append(center.addObserver(forName: LiveVoiceModel.ready, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.liveVoiceBecameReady() }
        })
        observers.append(center.addObserver(forName: ClonedVoices.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.voices.removeAll() }
        })
        observers.append(center.addObserver(forName: .yyAudioClaimed, object: nil, queue: .main) { [weak self] note in
            let isOther = (note.object as AnyObject?) !== self
            MainActor.assumeIsolated {
                guard let self, isOther else { return }
                if self.state == .playing || self.state == .loading { self.pause() }
                self.releaseRemote()
            }
        })
        observers.append(center.addObserver(forName: DramaSynth.settingsChanged, object: nil, queue: .main) { [weak self] note in
            let targetBook = note.object as? String
            MainActor.assumeIsolated {
                guard let self, targetBook == nil || targetBook == self.detail.summary.id else { return }
                self.rolesChanged()
            }
        })
        observers.append(center.addObserver(forName: KokoroModel.ready, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.liveVoiceBecameReady() }
        })
        #if os(iOS)
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            let type = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt
            let options = note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0
            let began = type == AVAudioSession.InterruptionType.began.rawValue
            let ended = type == AVAudioSession.InterruptionType.ended.rawValue
            let shouldResume = AVAudioSession.InterruptionOptions(rawValue: options).contains(.shouldResume)
            MainActor.assumeIsolated {
                guard let self else { return }
                if began {
                    let wasActive = self.state == .playing || self.state == .loading
                    if wasActive { self.pause() }
                    self.resumeAfterInterruption = wasActive
                } else if ended {
                    let resume = self.resumeAfterInterruption && shouldResume
                    self.resumeAfterInterruption = false
                    if resume, self.state == .paused { self.resume() }
                }
            }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] note in
            let reason = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt
            // Headphones unplugged: stop talking out loud.
            guard reason == AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue else { return }
            MainActor.assumeIsolated { self?.pause() }
        })
        // 晃一下 while the timer fades out: 10 minutes more.
        observers.append(center.addObserver(forName: .yyShake, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.extendSleep() }
        })
        #endif
    }

    @ObservationIgnored private var ttsTrackedForRun = false

    // MARK: Controls

    /// Starts reading at `position`, or at the first sentence after it with something to read.
    /// `word`: in the narrator's voice, the first sentence starts at this moment of the recording
    /// (a word found by 搜索), not at its own start.
    func play(from position: Position, word: Double? = nil, paused: Bool = false) {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        failure = nil
        failedSpot = nil
        if case .until(let date, _) = sleep, date <= Date() { sleep = .off }
        reset()
        ttsTrackedForRun = false
        startWord = word.map { (Spot(section: position.section, sentence: Int(position.sentence)), $0) }
        guard let start = first(from: Spot(section: position.section, sentence: Int(position.sentence))) else {
            return stop()
        }
        if !paused {
            let startLang = language(of: start.section)
            if !narrated {
                KokoroDownload.shared.startIfWifi(for: startLang)
                LiveVoiceDownload.shared.startIfWifi(for: startLang)
            }
        }
        if let hotSec = thermalFallbackSection, hotSec != start.section, thermalState != .critical {
            thermalFallbackSection = nil
            thermalNotice = nil
        }
        spot = start
        nextToRender = start
        if paused {
            clearFadeIfNeeded()
            state = .paused
            claimRemote()
            loadArtwork()
            report()
            updateNowPlaying()
            return
        }
        state = .loading
        claimRemote()
        loadArtwork()
        updateNowPlaying()
        render()
    }

    func pause() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        guard state == .playing || state == .loading else { return }
        // 暂停即停预合成与云端预取，不继续在后台占算力、电量或额度。
        rendering?.cancel()
        rendering = nil
        cancelFetchAhead()
        RecordingFetch.shared.stopReadingDrama(bookID: detail.summary.id)
        heard = heardNow()
        resumedAt = nil
        node.pause()
        state = .paused
        clearFadeIfNeeded()
        report()
        updateNowPlaying()
    }

    func resume() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        guard state == .paused else { return }
        clearFadeIfNeeded()
        claimRemote()
        if !narrated, let current = spot, DramaSynth.isDramaActive(core, detail.summary.id) {
            RecordingFetch.shared.readingDrama(bookID: detail.summary.id, section: current.section, core: core)
        }
        if queued.isEmpty { feedNode() }
        guard !queued.isEmpty else {
            state = .loading
            updateNowPlaying()
            return render()
        }
        guard startEngine() else { return }
        node.play()
        if started {
            resumedAt = Date()
            state = .playing
            report()
        } else {
            began()
        }
        updateNowPlaying()
        render()
    }

    func stop() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        let wasStopped = state == .stopped
        cancelFetchAhead()
        RecordingFetch.shared.stopReadingDrama(bookID: detail.summary.id)
        reset()
        sectionLayers.removeAll()
        voices.removeAll()
        readyOfflineVoice = nil
        thermalFallbackSection = nil
        thermalNotice = nil
        state = .stopped
        clearFadeIfNeeded()
        spot = nil
        engine.stop()
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
        #endif
        updateNowPlaying()
        if !wasStopped { onStop?() }
    }

    /// The sentence being read or paused on, for the 播放条 under the shelf.
    var line: String? {
        guard let spot else { return nil }
        let all = sentences(spot.section)
        return all.indices.contains(spot.sentence) ? all[spot.sentence] : nil
    }

    /// How far into its section the voice is, 0–1.
    var sectionProgress: Double {
        guard let spot else { return 0 }
        let n = sentences(spot.section).count
        return n > 0 ? Double(spot.sentence + 1) / Double(n) : 0
    }

    /// 双击耳机: the sentence just heard is the one playing, or the one before when this one has
    /// only just begun. With nothing to highlight it in, moves on a sentence instead.
    func markHeard() {
        guard let spot, let onMarkHeard else { return skip(forward: true) }
        onMarkHeard(started && heardNow() < 1 ? before(spot) ?? spot : spot)
    }

    /// 跳过: past the recording's own talk being played, on to the next sentence.
    func skipExtra() {
        guard let line = queued.first, line.extra != nil else { return }
        let next = line.extra?.place == .head ? line.spot : after(line.spot)
        guard let next else { return stop() }
        onSkip?()
        play(from: next.position, paused: state == .paused)
    }

    /// 点这里听: the 片头 just passed, then on from the first sentence.
    func playHead() {
        guard let spot = spot ?? nextToRender, skippedHead != nil else { return }
        skippedHead = nil
        wantHead = true
        play(from: spot.position)
    }

    /// The 片头 offer is over.
    func dismissHead() { skippedHead = nil }

    /// Moves on, or back, by one sentence — from `origin` when given, else from `spot`.
    func skip(forward: Bool, from origin: Position? = nil) {
        let base: Spot?
        if let origin {
            base = Spot(section: origin.section, sentence: Int(origin.sentence))
        } else {
            base = spot
        }
        guard let base, var target = forward ? after(base) : before(base) else { return }
        // Past the sentences the recording doesn't read, or going back would land here again.
        if narrated, let guide {
            while guide.skips(target.section, target.sentence), let t = forward ? after(target) : before(target) { target = t }
        }
        onSkip?()
        play(from: target.position, paused: state == .paused)
    }

    /// Stops reading and lets go of the lock screen. The player can't be used afterwards.
    func close() {
        stop()
        releaseRemote()
        for observer in observers { NotificationCenter.default.removeObserver(observer) }
        observers = []
    }

    // MARK: The queue

    private func cancelFetchAhead() {
        fetchingAhead?.task.cancel()
        fetchingAhead = nil
    }

    private func reset() {
        generation += 1
        renderToken += 1
        extraPlaying = nil
        rendering?.cancel()
        rendering = nil
        cancelFetchAhead()
        node.stop()
        queued = []
        staged = []
        started = false
        nextToRender = nil
        heard = 0
        resumedAt = nil
    }

    /// Drops pre-rendered sentences not yet committed to the audio node and cancels in-flight synthesis,
    /// without interrupting the sentence currently playing.
    private func dropStaged() {
        renderToken += 1
        rendering?.cancel()
        rendering = nil
        cancelFetchAhead()
        staged = []
        if let last = queued.last {
            nextToRender = last.extra?.place == .head ? last.spot : after(last.spot)
        } else {
            nextToRender = spot
        }
    }

    /// 当前队列剩余可听秒数（按当前倍速折算）。
    private func queuedAheadSeconds() -> Double {
        guard let first = queued.first else {
            return staged.reduce(0.0) { $0 + $1.speech.seconds } / Double(max(rate, 0.5))
        }
        let firstLeft = max(0, first.speech.seconds - heardNow())
        let rest = queued.dropFirst().reduce(0.0) { $0 + $1.speech.seconds }
            + staged.reduce(0.0) { $0 + $1.speech.seconds }
        return (firstLeft + rest) / Double(max(rate, 0.5))
    }

    private func wantsMoreBuffered(at spot: Spot) -> Bool {
        if state == .paused || state == .stopped { return false }
        let buffered = queued.count + staged.count
        if buffered == 0 { return true }
        if buffered >= 40 { return false }
        if narrated { return buffered <= Self.ahead }
        if thermalState == .critical || thermalFallbackSection == spot.section {
            return buffered <= Self.ahead
        }
        let lang = language(of: spot.section)
        let usesLiveModel: Bool
        if DramaSynth.isDramaActive(core, detail.summary.id) {
            usesLiveModel = dramaLayer(for: spot.section) == .live && LiveVoiceModel.isInstalled
        } else {
            let v = voice(for: lang, section: spot.section)
            usesLiveModel = v is CardVoice
        }
        guard usesLiveModel else {
            return buffered <= Self.ahead
        }
        let target = thermalState == .serious ? Self.liveAheadSeriousSeconds : Self.liveAheadNormalSeconds
        return queuedAheadSeconds() < target
    }

    private func thermalChanged() {
        thermalState = ProcessInfo.processInfo.thermalState
        if thermalState == .critical, state == .playing || state == .loading, let current = spot {
            thermalFallbackSection = current.section
            thermalNotice = String(localized: "手机有点热，这一章先用系统声音")
        } else if thermalState != .critical, let hot = thermalFallbackSection, spot?.section != hot {
            thermalFallbackSection = nil
            thermalNotice = nil
        }
    }

    private func liveVoiceBecameReady() {
        // 正在听或暂停时不自动切声：停下状态仅清缓存供下次新开朗读使用；
        // 播放/暂停中则提示刚下好的离线默认旁白，由用户主动确认后再切换。
        if state == .stopped {
            readyOfflineVoice = nil
            voices.removeAll()
            return
        }
        guard !narrated else { return }
        let lang = language
        guard !hasSavedVoice(for: lang) else { return }
        let current = voices[lang].flatMap { $0 }
        if current is KokoroVoice || current is CardVoice { return }
        let candidate: (any Voice)? = (KokoroModel.isInstalled ? KokoroModel.defaultNarrator(for: lang) : nil)
            ?? (LiveVoiceModel.isInstalled ? VoicePack.defaultNarrator(for: lang) : nil)
        if let candidate {
            readyOfflineVoice = candidate
        }
    }

    /// 用户确认切换到刚下载完成的离线默认旁白。
    func acceptReadyOfflineVoice() {
        guard let next = readyOfflineVoice else { return }
        readyOfflineVoice = nil
        choose(next, for: language)
    }

    /// 用户暂不切换，收起就绪提示并继续使用当前声音。
    func dismissReadyOfflineVoice() {
        readyOfflineVoice = nil
    }

    private func dramaLayer(for section: UInt32) -> DramaLayer {
        if let locked = sectionLayers[section] { return locked }
        RecordingFetch.shared.applyPendingCards(for: detail.summary.id, core: core)
        let layer = (try? core.dramaSectionLayer(
            bookId: detail.summary.id,
            section: section,
            studioEngine: Self.studioEngineID
        )) ?? .live
        sectionLayers[section] = layer
        return layer
    }

    /// Renders sentences until enough are queued.
    private func render() {
        guard rendering == nil, state != .paused, state != .stopped else { return }
        let generation = generation
        let token = renderToken
        rendering = Task { [weak self] in
            while let self, self.generation == generation, self.renderToken == token, let spot = self.nextToRender {
                guard self.wantsMoreBuffered(at: spot) else { break }
                let language = self.language(of: spot.section)
                if self.thermalState == .critical {
                    self.thermalFallbackSection = spot.section
                    if self.thermalNotice == nil {
                        self.thermalNotice = String(localized: "手机有点热，这一章先用系统声音")
                    }
                }
                let forceSystemForHeat = (self.thermalFallbackSection == spot.section)
                var speech: Speech?
                if self.narrated, let recording = self.recording {
                    // Not in the recording, and not to be read by the voice: on to the next.
                    if let guide = self.guide, guide.skips(spot.section, spot.sentence) {
                        self.nextToRender = self.after(spot)
                        continue
                    }
                    // 片头: before the recording's first sentence, played when it isn't skipped
                    // (or asked for), else offered on the capsule.
                    if let guide = self.guide, let head = guide.head, guide.isFirst(spot.section, spot.sentence), self.queued.isEmpty, self.staged.isEmpty {
                        if self.wantHead || !guide.settings.headTail {
                            self.wantHead = false
                            if let clip = await Self.clip(recording, head.t0, head.t1) {
                                guard self.generation == generation, self.renderToken == token else { return }
                                self.schedule(Line(spot: spot, speech: clip, extra: head))
                            }
                        } else {
                            self.skippedHead = head
                        }
                    }
                    let word = self.startWord.flatMap { $0.0 == spot ? $0.1 : nil }
                    self.startWord = nil
                    speech = await Self.narrate(recording, spot, from: word)
                }
                // 听成一部剧: whole-chapter Studio vs Live layer switching.
                if speech == nil, !self.narrated, !forceSystemForHeat,
                   DramaSynth.isDramaActive(self.core, self.detail.summary.id),
                   let dir = Audiobooks.shared?.dataDir {
                    if self.spot?.section == spot.section {
                        RecordingFetch.shared.readingDrama(bookID: self.detail.summary.id, section: spot.section, core: self.core)
                    }
                    let layer = self.dramaLayer(for: spot.section)
                    let chosenNarrator = UserDefaults.standard.string(forKey: Self.voiceKey(language))
                    switch layer {
                    case .studio:
                        speech = await Self.play(self.core, dir, book: self.detail.summary.id, spot)
                        if speech == nil {
                            speech = await Self.playLive(self.core, dir, book: self.detail.summary.id, spot, language: language, narrator: chosenNarrator)
                        }
                    case .live:
                        speech = await Self.playLive(self.core, dir, book: self.detail.summary.id, spot, language: language, narrator: chosenNarrator)
                    }
                    if speech != nil, !self.ttsTrackedForRun {
                        self.ttsTrackedForRun = true
                        AirTraffic.trackTtsStarted(
                            engine: AirTraffic.ttsEngine(forVoiceID: chosenNarrator),
                            surface: "reader"
                        )
                    }
                }
                if speech == nil {
                    let customScriptVoice = forceSystemForHeat ? nil : self.script.voice?(spot.section, spot.sentence, language)
                    let segPlan: [(voice: any Voice, text: String)]? = (!forceSystemForHeat && customScriptVoice == nil)
                        ? self.script.roles?.segmentPlan(section: spot.section, sentence: spot.sentence, language: language) {
                            self.voice(for: language, section: spot.section)
                        }
                        : nil
                    if let segPlan {
                        do {
                            speech = try await BookRoles.renderSegments(segPlan, language: language)
                            if !self.ttsTrackedForRun {
                                self.ttsTrackedForRun = true
                                AirTraffic.trackTtsStarted(
                                    engine: AirTraffic.ttsEngine(for: segPlan.first?.voice),
                                    surface: "reader"
                                )
                            }
                        } catch {
                            if error is CancellationError || Task.isCancelled || self.generation != generation || self.renderToken != token { return }
                            self.failed(String(localized: "朗读出错了（\(error.localizedDescription)）"))
                            return
                        }
                    } else {
                        let chosenVoice: (any Voice)?
                        if forceSystemForHeat {
                            chosenVoice = SystemVoice.best(for: language)
                        } else {
                            chosenVoice = customScriptVoice ?? self.voice(for: language, section: spot.section)
                        }
                        guard let voice = chosenVoice else {
                            let name = Locale.current.localizedString(forLanguageCode: language) ?? language
                            self.failed(String(localized: "这台设备还没有\(name)声音。在设置的“辅助功能 › 朗读内容”里可以下载一个。"))
                            return
                        }
                        if let cloud = voice as? CloudVoice { self.fetchAhead(cloud, from: spot, language: language) }
                        do {
                            speech = try await voice.render(self.text(at: spot), language: language)
                            if !self.ttsTrackedForRun {
                                self.ttsTrackedForRun = true
                                AirTraffic.trackTtsStarted(
                                    engine: AirTraffic.ttsEngine(for: voice),
                                    surface: "reader"
                                )
                            }
                        } catch {
                            // Another voice chosen, or a jump: this sentence was called off, nothing failed.
                            if error is CancellationError || Task.isCancelled || self.generation != generation || self.renderToken != token { return }
                            // 不静默切换成其他声音，明确停下并告知用户原因，由用户决定重试或换声音。
                            self.failed(String(localized: "朗读出错了（\(error.localizedDescription)）"))
                            return
                        }
                    }
                }
                guard self.generation == generation, self.renderToken == token else { return }
                self.nextToRender = self.after(spot)
                // A sentence with nothing to say (a lone dash, say) is skipped.
                if let speech { self.schedule(Line(spot: spot, speech: speech)) }
                // The recording's own talk after it, when it's played.
                if self.narrated, let recording = self.recording, let e = self.guide?.extra(after: spot.section, spot.sentence),
                   let clip = await Self.clip(recording, e.t0, e.t1) {
                    guard self.generation == generation, self.renderToken == token else { return }
                    self.schedule(Line(spot: spot, speech: clip, extra: e))
                }
            }
            guard let self, self.generation == generation, self.renderToken == token else { return }
            self.rendering = nil
            // The book is over once the last sentence has played.
            if self.queued.isEmpty && self.staged.isEmpty && self.nextToRender == nil && self.state == .loading { self.stop() }
        }
    }

    /// 滑动窗口预取: a cloud voice fetches only the next few sentences ahead of playback,
    /// avoiding paying for the entire chapter if the listener stops or switches voices early.
    private func fetchAhead(_ voice: CloudVoice, from spot: Spot, language: String) {
        if let cur = fetchingAhead, cur.section == spot.section, cur.voice == voice.id,
           spot.sentence <= cur.sentence + 1 {
            return
        }
        fetchingAhead?.task.cancel()
        let all = sentences(spot.section)
        let start = spot.sentence + 1
        guard start < all.count else { fetchingAhead = nil; return }
        let end = min(all.count, start + Self.cloudPrefetchWindow)
        let texts = Array(all[start ..< end])
        fetchingAhead = (spot.section, spot.sentence, voice.id, Task(priority: .utility) { await voice.prefetch(texts, language: language) })
    }

    /// A sentence of the play: its stretch of the scene, mixed — the voices where they stand,
    /// the room's echo, the room tone and the sounds the book writes, and the pause after it.
    /// nil while any of its clips isn't made (the voice reads it as usual).
    @concurrent nonisolated private static func play(_ core: YuyueCore, _ dir: URL, book: String, _ spot: Spot) async -> Speech? {
        await DramaSynth.sentence(core, dir, book: book, section: spot.section, sentence: UInt32(spot.sentence))
    }

    @concurrent nonisolated private static func playLive(_ core: YuyueCore, _ dir: URL, book: String, _ spot: Spot, language: String, narrator: String?) async -> Speech? {
        await DramaSynth.liveSentence(core, dir, book: book, section: spot.section, sentence: UInt32(spot.sentence), language: language, narratorFallback: narrator)
    }

    @concurrent nonisolated private static func narrate(_ recording: Recording, _ spot: Spot, from: Double? = nil) async -> Speech? {
        recording.speech(section: spot.section, sentence: spot.sentence, from: from)
    }

    /// 试听 a stretch of the recording, without moving the book.
    func preview(from start: Double, to end: Double) {
        guard let recording else { return }
        if state == .playing || state == .loading { pause() }
        let token = Self.previewing.beginClaim()
        Task {
            guard let speech = await Self.clip(recording, start, end) else { return }
            Self.previewing.play(speech, ifCurrent: token)
        }
    }

    @concurrent nonisolated private static func clip(_ recording: Recording, _ start: Double, _ end: Double) async -> Speech? {
        recording.clip(from: start, to: end)
    }

    private static let previewing = ClipPlayer()

    /// 几句一起循环: from the sentence being read to `sentence` (either way round), over and
    /// over; the same section only.
    func loopTo(section: UInt32, sentence: Int) {
        guard let here = spot, here.section == section else { return }
        loopRange = LoopRange(section: section, first: min(here.sentence, sentence), last: max(here.sentence, sentence))
        loop = true
    }

    /// The sentence with the quiet 念完停一下 asks for after it.
    private func paced(_ speech: Speech) -> Speech {
        let quiet: Double = switch pauseAfter {
        case .off: 0
        case .seconds(let s): s
        case .asLong: min(Double(speech.buffer.frameLength) / speech.buffer.format.sampleRate, 20)
        }
        guard quiet > 0, let gap = AVAudioPCMBuffer(pcmFormat: speech.buffer.format, frameCapacity: AVAudioFrameCount(quiet * speech.buffer.format.sampleRate)) else { return speech }
        gap.frameLength = gap.frameCapacity
        if let p = gap.floatChannelData { for c in 0 ..< Int(gap.format.channelCount) { p[c].update(repeating: 0, count: Int(gap.frameLength)) } }
        return Speech.make(from: [speech.buffer, gap]) ?? speech
    }

    private func schedule(_ line: Line) {
        if queued.isEmpty, case .until(let date, _) = sleep, Date() >= date {
            fallAsleep(at: line.spot)
            return
        }
        let line = line.extra == nil ? Line(spot: line.spot, speech: paced(line.speech), extra: nil) : line
        staged.append(line)
        let wasEmpty = queued.isEmpty
        feedNode()
        guard wasEmpty, !queued.isEmpty, state == .loading || state == .playing else { return }
        guard startEngine() else { return }
        node.play()
        began()
    }

    private func feedNode() {
        let generation = generation
        while queued.count < Self.nodeAhead, !staged.isEmpty {
            let line = staged.removeFirst()
            node.scheduleBuffer(line.speech.buffer, completionCallbackType: .dataPlayedBack,
                                completionHandler: Self.onMain { [weak self] in self?.played(generation) })
            queued.append(line)
        }
    }

    private func played(_ generation: Int) {
        guard generation == self.generation, !queued.isEmpty else { return }
        let done = queued[0].spot
        let next = queued.count > 1 ? queued[1].spot : (staged.first?.spot ?? nextToRender)
        switch Self.nextActionAfterPlayed(done: done, next: next, sleep: sleep, loop: loop, loopRange: loopRange) {
        case .sleepAt(let target):
            return fallAsleep(at: target)
        case .loopTo(let target):
            return play(from: target.position)
        case .advance:
            break
        }
        queued.removeFirst()
        let wasEmpty = queued.isEmpty
        feedNode()
        started = false
        if !queued.isEmpty {
            if wasEmpty, state == .loading || state == .playing {
                guard startEngine() else { return }
                node.play()
            }
            // The node is already playing the next one.
            began()
        } else if nextToRender == nil && rendering == nil {
            // Read to the very end, not stopped by hand or by the timer.
            let finish = onFinish
            stop()
            finish?()
            return
        } else {
            // Rendering fell behind; the next sentence starts as soon as it's ready.
            state = .loading
        }
        render()
    }

    nonisolated static func shouldSleep(after done: Spot, next: Spot?, sleep: Sleep, now: Date = Date()) -> Bool {
        switch sleep {
        case .off: false
        case .sentence: true
        case .chapter: next == nil || next?.section != done.section
        case .until(let date, _): now >= date
        }
    }

    nonisolated static func nextActionAfterPlayed(
        done: Spot,
        next: Spot?,
        sleep: Sleep,
        loop: Bool,
        loopRange: LoopRange?,
        now: Date = Date()
    ) -> PlayedAction {
        let loopTarget: Spot? = if let r = loopRange, loop {
            (done.section != r.section || done.sentence >= r.last || done.sentence < r.first)
                ? Spot(section: r.section, sentence: r.first)
                : nil
        } else if loop {
            done
        } else {
            nil
        }
        if shouldSleep(after: done, next: next, sleep: sleep, now: now) {
            return .sleepAt(loopTarget ?? next ?? done)
        }
        if let target = loopTarget {
            return .loopTo(target)
        }
        return .advance
    }

    private func clearFadeIfNeeded() {
        if case .until(let date, _) = sleep, date <= Date() {
            sleep = .off
        } else if fading || engine.mainMixerNode.outputVolume < 1 {
            fading = false
            engine.mainMixerNode.outputVolume = 1
        }
    }

    private func sleepChanged(from old: Sleep) {
        if old == .off { sleepFrom = spot }
        fadeTask?.cancel()
        fading = false
        engine.mainMixerNode.outputVolume = 1
        guard case .until(let date, _) = sleep else { return }
        fadeTask = Task { [weak self] in
            let lead = date.timeIntervalSinceNow - Self.fadeSeconds
            if lead > 0 { try? await Task.sleep(for: .seconds(lead)) }
            while !Task.isCancelled, let self, case .until(let d, _) = self.sleep {
                let left = d.timeIntervalSinceNow
                guard self.state == .playing || self.state == .loading else {
                    // Paused: nothing fades, and no key is taken for 续 10 分钟; the time passing
                    // meanwhile ends the timer, so playing again doesn't stop a moment later.
                    if left <= 0 { self.sleep = .off; return }
                    if self.fading || self.engine.mainMixerNode.outputVolume < 1 {
                        self.fading = false
                        self.engine.mainMixerNode.outputVolume = 1
                    }
                    try? await Task.sleep(for: .milliseconds(500))
                    continue
                }
                if left <= 0 {
                    if self.state == .loading {
                        if let at = self.spot ?? self.nextToRender {
                            self.fallAsleep(at: at)
                        } else {
                            self.sleep = .off
                        }
                        return
                    }
                    if !self.fading { self.fading = true }
                    self.engine.mainMixerNode.outputVolume = 0
                    try? await Task.sleep(for: .milliseconds(200))
                    continue
                }
                if !self.fading { self.fading = true }
                self.engine.mainMixerNode.outputVolume = Float(max(0, min(1, left / Self.fadeSeconds)))
                try? await Task.sleep(for: .milliseconds(200))
            }
        }
    }

    static let fadeSeconds: Double = 20

    /// 续 10 分钟: while the timer fades out.
    func extendSleep() {
        guard fading, case .until(_, let m) = sleep else { return }
        sleep = .until(Date().addingTimeInterval(600), minutes: m)
    }

    /// Stops between sentences for the sleep timer, paused where it would have gone on.
    private func fallAsleep(at spot: Spot) {
        reset()
        let from = sleepFrom
        sleep = .off
        sleepFrom = from
        self.spot = spot
        nextToRender = spot
        state = .paused
        report()
        updateNowPlaying()
        onSleep?(spot)
    }

    private func began() {
        guard let line = queued.first else { return }
        let newSection = spot?.section != line.spot.section
        if extraPlaying != line.extra { extraPlaying = line.extra }
        if line.extra == nil, skippedHead != nil, spot != nil, spot != line.spot { skippedHead = nil }
        spot = line.spot
        if newSection {
            if let hot = thermalFallbackSection, hot != line.spot.section, thermalState != .critical {
                thermalFallbackSection = nil
                thermalNotice = nil
            }
            if !narrated, DramaSynth.isDramaActive(core, detail.summary.id) {
                RecordingFetch.shared.readingDrama(bookID: detail.summary.id, section: line.spot.section, core: core)
            }
        }
        started = true
        heard = 0
        resumedAt = Date()
        state = .playing
        report()
        if newSection { updateNowPlaying() }
    }

    private func failed(_ message: String) {
        let resumeSpot = spot ?? nextToRender
        stop()
        failedSpot = resumeSpot
        failure = message
    }

    private func heardNow() -> Double {
        heard + (resumedAt.map { Date().timeIntervalSince($0) * Double(rate) } ?? 0)
    }

    /// 听读书: the moment of the recording being heard now (whole-recording seconds), or nil
    /// when the narrator isn't reading.
    func recordingNow() -> Double? {
        guard narrated, let line = queued.first, line.extra == nil,
              let start = recording?.start(section: line.spot.section, sentence: line.spot.sentence) else { return nil }
        return start + min(line.speech.seconds, max(0, heardNow()))
     }

    private func report() {
        if started, let line = queued.first {
            guard line.extra == nil else { return }
            let rate = Double(rate)
            onSentence?(line.spot, line.speech.seconds / rate * 1000, heardNow() / rate * 1000, state == .playing)
        } else if state == .paused, let spot, extraPlaying == nil {
            onSentence?(spot, 0, 0, false)
        }
    }

    private func rateChanged(from old: Float) {
        // What was heard so far was heard at the old speed.
        if let resumedAt {
            heard += Date().timeIntervalSince(resumedAt) * Double(old)
            self.resumedAt = Date()
        }
        speed.rate = rate
        report()
        updateNowPlaying()
    }

    /// 分角色: who says a line changed. Committed sentences on the audio node finish;
    /// later ones are read by whoever says them now without cutting off or jumping the page.
    func speakersChanged() {
        script.roles?.reload()
        LiveCastCache.shared.invalidate(book: detail.summary.id)
        dropStaged()
        render()
    }

    /// 分角色: committed sentences on the audio node finish with their current voice; later ones use the new role.
    func rolesChanged() {
        script.roles?.reload()
        sectionLayers.removeAll()
        LiveCastCache.shared.invalidate(book: detail.summary.id)
        dropStaged()
        render()
    }

    private func restartHere() {
        guard let spot, state != .stopped else { return }
        play(from: spot.position, paused: state == .paused)
    }

    private func startEngine() -> Bool {
        #if os(iOS)
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .spokenAudio, policy: .longFormAudio)
            try session.setActive(true)
        } catch {
            failed(String(localized: "没法播放声音（\(error.localizedDescription)）"))
            return false
        }
        #endif
        if engine.isRunning { return true }
        do {
            engine.prepare()
            try engine.start()
            return true
        } catch {
            failed(String(localized: "没法播放声音（\(error.localizedDescription)）"))
            return false
        }
    }

    // MARK: The book's sentences

    /// The words changed (替换规则): sentences not yet rendered are read anew.
    func textsChanged() {
        texts = [:]
        cancelFetchAhead()
    }

    private func sentences(_ section: UInt32) -> [String] {
        if let cached = texts[section] { return cached }
        let loaded = script.sentences(section)
        texts[section] = loaded
        return loaded
    }

    private func text(at spot: Spot) -> String {
        let all = sentences(spot.section)
        return all.indices.contains(spot.sentence) ? all[spot.sentence] : ""
    }

    private func language(of section: UInt32) -> String {
        script.language(section)
    }

    // MARK: Voices

    private static func voiceKey(_ language: String) -> String { "player.voice." + language }

    /// The language of the section being read, or of the book.
    var language: String {
        (spot ?? failedSpot).map { language(of: $0.section) } ?? detail.summary.language
    }

    /// Whether the user has explicitly saved a voice override for `language`.
    func hasSavedVoice(for language: String) -> Bool {
        let canon = CardVoice.canonicalLanguage(language)
        return UserDefaults.standard.string(forKey: Self.voiceKey(language)) != nil
            || UserDefaults.standard.string(forKey: Self.voiceKey(canon)) != nil
    }

    /// The voice reading `language` now.
    func currentVoice(for language: String) -> (any Voice)? {
        voice(for: language, section: spot?.section)
    }

    /// Reads `language` with another voice from now on, starting the sentence again in it.
    func choose(_ voice: any Voice, for language: String) {
        UserDefaults.standard.set(voice.id, forKey: Self.voiceKey(language))
        readyOfflineVoice = nil
        voices[language] = voice
        restartHere()
    }

    /// The current voice failed: switch to a system voice on this device and continue from where it stopped.
    func useSystemVoice() {
        let lang = language
        let resumeAt = failedSpot ?? spot
        failure = nil
        if let sys = SystemVoice.best(for: lang) {
            UserDefaults.standard.set(sys.id, forKey: Self.voiceKey(lang))
            voices[lang] = sys
            if let resumeAt {
                play(from: resumeAt.position)
            } else {
                restartHere()
            }
        }
    }

    /// Retries reading from the sentence where playback failed.
    func retry() {
        guard let resumeAt = failedSpot ?? spot else {
            failure = nil
            return
        }
        failure = nil
        play(from: resumeAt.position)
    }

    /// 试听: a few words in `voice`, without moving the book.
    func preview(_ voice: any Voice, text: String) {
        if state == .playing || state == .loading { pause() }
        // A 试听 that doesn't come out isn't reading stopping: the picker says so in its own
        // footer (PreviewState.failedID), not the window's 朗读停下了 card.
        Voices.preview(voice, text: text)
    }

    private func voice(for language: String, section: UInt32? = nil) -> (any Voice)? {
        if let known = voices[language] { return known }
        let canon = CardVoice.canonicalLanguage(language)
        let savedID = UserDefaults.standard.string(forKey: Self.voiceKey(language))
            ?? UserDefaults.standard.string(forKey: Self.voiceKey(canon))
        if let savedID, let chosen = Voices.find(savedID, bookID: detail.summary.id) {
            voices[language] = chosen
            return chosen
        }
        if KokoroModel.isInstalled, let def = KokoroModel.defaultNarrator(for: language) {
            voices[language] = def
            return def
        }
        if LiveVoiceModel.isInstalled, let def = VoicePack.defaultNarrator(for: language) {
            voices[language] = def
            return def
        }
        if CloudVoice.Provider.free.isSetUp, CardVoice.supportsLanguage(language),
           let free = CloudVoice.all().first(where: { $0.provider == .free && $0.speaks(language) }) {
            voices[language] = free
            return free
        }
        let sys = SystemVoice.best(for: language)
        voices[language] = sys
        return sys
    }

    /// `spot` itself when it can be read, or the first sentence after it that can.
    private func first(from spot: Spot) -> Spot? {
        spot.sentence < sentences(spot.section).count
            ? Spot(section: spot.section, sentence: max(0, spot.sentence))
            : firstSentence(after: spot.section)
    }

    private func after(_ spot: Spot) -> Spot? {
        spot.sentence + 1 < sentences(spot.section).count
            ? Spot(section: spot.section, sentence: spot.sentence + 1)
            : firstSentence(after: spot.section)
    }

    private func before(_ spot: Spot) -> Spot? {
        if spot.sentence > 0 { return Spot(section: spot.section, sentence: spot.sentence - 1) }
        var section = spot.section
        while let previous = script.previous(section) {
            let count = sentences(previous).count
            if count > 0 { return Spot(section: previous, sentence: count - 1) }
            section = previous
        }
        return nil
    }

    private func firstSentence(after section: UInt32) -> Spot? {
        var section = section
        while let next = script.next(section) {
            if !sentences(next).isEmpty { return Spot(section: next, sentence: 0) }
            section = next
        }
        return nil
    }

    // MARK: Lock screen

    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        guard state != .stopped, let spot else {
            center.nowPlayingInfo = nil
            #if os(macOS)
            center.playbackState = .stopped
            #endif
            return
        }
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: script.title(spot.section) ?? detail.summary.title,
            MPMediaItemPropertyAlbumTitle: detail.summary.title,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
            MPNowPlayingInfoPropertyPlaybackRate: state == .playing ? Double(rate) : 0,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: 1.0,
        ]
        if let author = detail.summary.authors.first { info[MPMediaItemPropertyArtist] = author }
        if let artwork { info[MPMediaItemPropertyArtwork] = artwork }
        center.nowPlayingInfo = info
        #if os(macOS)
        center.playbackState = state == .paused ? .paused : .playing
        #endif
    }

    private func loadArtwork() {
        guard artwork == nil, let cover = detail.summary.coverPath else { return }
        Task { [weak self] in
            guard let image = await Thumbnail.load(cover, maxPixels: 600) else { return }
            self?.artwork = Self.artwork(image)
            self?.updateNowPlaying()
        }
    }

    /// Several books can be open on a Mac; the lock screen and media keys go to the one that
    /// last started reading.
    private func claimRemote() {
        NotificationCenter.default.post(name: .yyAudioClaimed, object: self)
        guard Self.remoteOwner !== self else { return }
        Self.remoteOwner?.releaseRemote()
        Self.remoteOwner = self
        let center = MPRemoteCommandCenter.shared()
        let handlers: [(MPRemoteCommand, @MainActor @Sendable (Player) -> Void)] = [
            (center.playCommand, { $0.resume() }),
            (center.pauseCommand, { $0.pause() }),
            (center.togglePlayPauseCommand, { $0.state == .playing || $0.state == .loading ? $0.pause() : $0.resume() }),
            (center.nextTrackCommand, { $0.markHeard() }),
            (center.previousTrackCommand, { $0.skip(forward: false) }),
            // The lock screen's and the car's 书签: the sentence just heard, highlighted.
            (center.bookmarkCommand, { $0.markHeard() }),
        ]
        for (command, action) in handlers {
            command.isEnabled = true
            let target = command.addTarget(handler: Self.command { [weak self] in
                // Any key while the timer fades out: 10 minutes more instead.
                guard let self else { return }
                if self.fading { self.extendSleep() } else { action(self) }
            })
            remoteTargets.append((command, target))
        }
        let speeds = center.changePlaybackRateCommand
        speeds.isEnabled = true
        speeds.supportedPlaybackRates = [0.75, 1, 1.25, 1.5, 2, 3]
        remoteTargets.append((speeds, speeds.addTarget(handler: Self.rateCommand { [weak self] in self?.rate = $0 })))
    }

    private func releaseRemote() {
        for (command, target) in remoteTargets { command.removeTarget(target) }
        remoteTargets = []
        if Self.remoteOwner === self { Self.remoteOwner = nil }
    }

    // AVFoundation and MediaPlayer call back on their own threads. These build the callbacks
    // outside the main actor, so they can run there, and hop to the main actor for the work.

    nonisolated private static func onMain(_ work: @escaping @MainActor @Sendable () -> Void) -> @Sendable (AVAudioPlayerNodeCompletionCallbackType) -> Void {
        { _ in Task { @MainActor in work() } }
    }

    nonisolated private static func command(_ work: @escaping @MainActor @Sendable () -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { _ in
            Task { @MainActor in work() }
            return .success
        }
    }

    nonisolated private static func rateCommand(_ work: @escaping @MainActor @Sendable (Float) -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { event in
            guard let rate = (event as? MPChangePlaybackRateCommandEvent)?.playbackRate else { return .commandFailed }
            Task { @MainActor in work(rate) }
            return .success
        }
    }

    nonisolated private static func artwork(_ cgImage: CGImage) -> MPMediaItemArtwork {
        let size = CGSize(width: cgImage.width, height: cgImage.height)
        #if os(iOS)
        let image = UIImage(cgImage: cgImage)
        #else
        let image = NSImage(cgImage: cgImage, size: size)
        #endif
        return MPMediaItemArtwork(boundsSize: size) { _ in image }
    }
}

/// Plays a clip on its own, for 试听.
final class ClipPlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private var requestID = 0
    nonisolated(unsafe) private var claimObserver: (any NSObjectProtocol)?

    init() {
        engine.attach(node)
        engine.connect(node, to: engine.mainMixerNode, format: Speech.format)
        claimObserver = NotificationCenter.default.addObserver(forName: .yyAudioClaimed, object: nil, queue: .main) { [weak self] note in
            let isOther = (note.object as AnyObject?) !== self
            MainActor.assumeIsolated {
                guard let self, isOther else { return }
                self.stop()
            }
        }
    }

    deinit {
        if let claimObserver { NotificationCenter.default.removeObserver(claimObserver) }
    }

    func beginClaim() -> Int {
        stop()
        NotificationCenter.default.post(name: .yyAudioClaimed, object: self)
        return requestID
    }

    func play(_ speech: Speech, ifCurrent token: Int? = nil) {
        if let token, token != requestID { return }
        if token == nil {
            NotificationCenter.default.post(name: .yyAudioClaimed, object: self)
        }
        node.stop()
        if !engine.isRunning {
            engine.prepare()
            try? engine.start()
        }
        node.scheduleBuffer(speech.buffer, completionHandler: nil)
        node.play()
    }

    func stop() {
        requestID &+= 1
        node.stop()
    }
}

#if os(iOS)
extension Notification.Name {
    /// The phone was shaken (the window hears it while the app is in front).
    static let yyShake = Notification.Name("yuyue.shake")
}

extension UIWindow {
    override open func motionEnded(_ motion: UIEvent.EventSubtype, with event: UIEvent?) {
        if motion == .motionShake { NotificationCenter.default.post(name: .yyShake, object: nil) }
        super.motionEnded(motion, with: event)
    }
}
#endif
