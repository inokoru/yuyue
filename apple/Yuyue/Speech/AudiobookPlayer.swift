import AVFoundation
import MediaPlayer
import YuyueCore
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// 有声书 listened to on its own: the recording's files one after
/// another, by time. A position is a chapter and the milliseconds into it, saved like any book's,
/// so progress, 书签 and 听了多久 follow the book to other devices.
///
/// Speed doesn't change the pitch; −15 s and +30 s, or a sentence at a time once it's laid against
/// its e-book; after a pause of a while it goes back (a few seconds, or to the sentence or the
/// paragraph's start); 定时 stops after some minutes, the sentence or the chapter, fading for its
/// last 20 seconds. The lock screen, the headphones and the media keys take the same controls,
/// and the lock screen's 书签 highlights the sentence just heard.
///
/// 这本录音: 片头片尾 and music between chapters passed, long pauses cut short, the voice made
/// clearer, sentences the recording doesn't read read by a voice — each recording its own.
@Observable
final class AudiobookPlayer {
    let bookID: String
    private(set) var summary: BookSummary
    private(set) var recording: AudioRecording?
    private(set) var isPlaying = false
    /// Seconds from the start of the whole recording.
    private(set) var time: Double = 0
    /// 进度冲突: another device is further on (for 跳过去 / 留在这里).
    var elsewhere: ReaderModel.Elsewhere?

    /// 跳过去: on to where the other device is.
    func jumpElsewhere() {
        guard let e = elsewhere else { return }
        elsewhere = nil
        seek(to: seconds(at: e.progress.position))
    }
    var failure: String?
    var rate: Float = 1 {
        didSet {
            UserDefaults.standard.set(rate, forKey: Self.rateKey)
            if isPlaying { queue.rate = rate }
            updateNowPlaying()
        }
    }
    var sleep = Sleep.off {
        didSet {
            if oldValue == .off, sleep != .off { sleepFrom = time }
            armSleep()
        }
    }
    private(set) var bookmarks: [Bookmark] = []
    /// 这本录音.
    private(set) var settings: SkipSettings
    /// Laid against its e-book: sentences to step by, extras to pass.
    private(set) var hasMap = false
    private(set) var unreadCount = 0
    /// Seconds 缩短停顿 has saved on this recording.
    private(set) var saved: Double = 0
    /// Laid against its e-book: each chapter named as the book names it (第八回, 孔乙己), not as
    /// the files' tags do (`01 - kong yi ji`).
    private(set) var bookTitles: [Int: String] = [:]
    /// What's happening that isn't the book: 片头 passed, talk not in the book, 朗读补上.
    private(set) var notice: Notice?
    /// 划了一句, with 撤销.
    var toast: Library.Toast?
    /// The timer's last 20 seconds.
    private(set) var fading = false
    /// 回到昨晚: the timer stopped it, set at these seconds.
    private(set) var sleepReturn: (seconds: Double, date: Date)?

    enum Sleep: Equatable {
        case off
        /// 念完这句: stops where this sentence ends.
        case sentence(end: Double)
        /// 本集完
        case chapter
        case until(Date, minutes: Int)
    }

    struct Notice: Equatable {
        enum Kind { case head, extra, filling, fetching }
        let kind: Kind
        let text: String
        /// The tap's word (点这里听, 跳过), if it does anything.
        let action: String?
        var extra: ExtraSpan?
    }

    /// Sentences the recording doesn't read, between two it does.
    private struct UnreadRun {
        let at: Double
        let resume: Double
        let texts: [String]
    }

    static let back: Double = 15
    static let ahead: Double = 30

    @ObservationIgnored private let library: Library
    @ObservationIgnored var core: YuyueCore { library.core }
    @ObservationIgnored private let queue = AVQueuePlayer()
    /// Where each file starts in the whole recording.
    @ObservationIgnored private var starts: [Double] = []
    @ObservationIgnored private var items: [AVPlayerItem] = []
    /// File index of `items[0]` within `recording.files`.
    @ObservationIgnored private var itemsStart = 0
    @ObservationIgnored private var ticker: Any?
    @ObservationIgnored private var observers: [any NSObjectProtocol] = []
    @ObservationIgnored private var remoteTargets: [(MPRemoteCommand, Any)] = []
    #if os(iOS)
    @ObservationIgnored private var resumeAfterInterruption = false
    #endif
    @ObservationIgnored private var artwork: MPMediaItemArtwork?
    @ObservationIgnored private var pausedAt: Date?
    @ObservationIgnored private var listenSince: Date?
    @ObservationIgnored private var savedAt = Date.distantPast
    @ObservationIgnored private var sleepTask: Task<Void, Never>?
    @ObservationIgnored private var sleepFrom: Double?
    /// An extra listened to on purpose (点这里听): not passed again until it's over.
    @ObservationIgnored private var passing: ExtraSpan?
    @ObservationIgnored private var unsavedSaving: Double = 0
    @ObservationIgnored private var runs: [UnreadRun] = []
    @ObservationIgnored private var language = "zh"
    @ObservationIgnored private var speaker: AVSpeechSynthesizer?
    /// The sentences being read in place of the recording, while they are.
    @ObservationIgnored private var fillingRun: UnreadRun?
    @ObservationIgnored private var spoken: SpokenDone?
    @ObservationIgnored private let tap: VoiceTap
    @ObservationIgnored private var abandoned = false
    private static weak var remoteOwner: AudiobookPlayer?
    private static let rateKey = "audiobook.rate"

    init(library: Library, book: BookSummary) {
        self.library = library
        bookID = book.id
        summary = book
        let savedRate = UserDefaults.standard.float(forKey: Self.rateKey)
        rate = savedRate > 0 ? savedRate : 1
        let s = (try? library.core.audiobookSkipSettings(bookId: book.id)) ?? SkipSettings(headTail: true, middle: false, shortenChapterGaps: true, clearerVoice: false, shortenPauses: false, fillUnread: false, wordFollow: false)
        settings = s
        let gain = UserDefaults.standard.float(forKey: Self.gainKey(book.id))
        tap = VoiceTap(gain: gain > 0 ? gain : 1, clearer: s.clearerVoice)
        queue.actionAtItemEnd = .advance
        queue.audiovisualBackgroundPlaybackPolicy = .continuesIfPossible
        do {
            let r = try core.audioRecording(bookId: book.id)
            recording = r
            var at = 0.0
            for f in r.files {
                starts.append(at)
                at += f.seconds
            }
            let mine = try? core.progress(bookId: book.id)
            let latest = try? core.latestProgress(bookId: book.id)
            if let p = mine ?? latest {
                time = seconds(at: p.position)
            }
            // 进度冲突: another device listened on later, half a minute or more from here.
            if let mine, let latest, !latest.thisDevice, latest.at > mine.at, abs(seconds(at: latest.position) - time) >= 30 {
                let i = chapter(at: seconds(at: latest.position))
                elsewhere = ReaderModel.Elsewhere(progress: latest, here: mine.fraction, text: nil, chapter: "\(ReaderModel.shortTitle(title(i)))")
            }
        } catch {
            failure = Library.message(for: error)
        }
        bookmarks = (try? core.bookmarks(book: book.id)) ?? []
        loadMap()
        offerSleepReturn()
        measureLoudness()
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: Audiobooks.timingsChanged, object: nil, queue: .main) { [weak self] note in
            let id = note.object as? String
            MainActor.assumeIsolated {
                guard let self, id == nil || id == self.bookID else { return }
                self.loadMap()
            }
        })
        if ShareStream.shared.streams(book.id) {
            ShareStream.shared.tidy(book.id, at: currentFile, core: core)
            reloadRecording()
        }
        observers.append(center.addObserver(forName: ShareStream.arrived, object: book.id, queue: .main) { [weak self] note in
            let file = note.userInfo?["file"] as? Int ?? -1
            MainActor.assumeIsolated { self?.arrived(file) }
        })
        observers.append(center.addObserver(forName: AVPlayerItem.didPlayToEndTimeNotification, object: nil, queue: .main) { [weak self] note in
            let item = note.object as? AVPlayerItem
            MainActor.assumeIsolated { self?.finished(item) }
        })
        observers.append(center.addObserver(forName: .yyAudioClaimed, object: nil, queue: .main) { [weak self] note in
            let isOther = (note.object as AnyObject?) !== self
            MainActor.assumeIsolated {
                guard let self, isOther else { return }
                if self.isPlaying { self.pause() }
                self.releaseRemote()
            }
        })
        #if os(macOS)
        observers.append(center.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.close() }
        })
        #else
        observers.append(center.addObserver(forName: UIApplication.willTerminateNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.close() }
        })
        #endif
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
                    let wasPlaying = self.isPlaying
                    if wasPlaying { self.pause() }
                    self.resumeAfterInterruption = wasPlaying
                } else if ended {
                    let resume = self.resumeAfterInterruption && shouldResume
                    self.resumeAfterInterruption = false
                    if resume, !self.isPlaying { self.play() }
                }
            }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] note in
            let reason = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt
            // Headphones out: don't go on out loud.
            guard reason == AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue else { return }
            MainActor.assumeIsolated { self?.pause() }
        })
        // 晃一下 while the timer fades out: 10 minutes more.
        observers.append(center.addObserver(forName: .yyShake, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.extendSleep() }
        })
        #endif
    }

    // MARK: 这本录音

    /// The map, the settings and what follows from them, again (after 合成, 微调 or a change on
    /// another device).
    private func loadMap() {
        settings = (try? core.audiobookSkipSettings(bookId: bookID)) ?? settings
        tap.clearer = settings.clearerVoice
        saved = (try? core.audiobookSaved(bookId: bookID)) ?? 0
        guard let map = (try? core.audiobookMap(bookId: bookID)) ?? nil, map.rows.contains(where: { $0.by != .unread }) else {
            hasMap = false
            unreadCount = 0
            runs = []
            bookTitles = [:]
            return
        }
        hasMap = true
        unreadCount = map.rows.count { $0.by == .unread }
        if let detail = try? core.book(id: map.bookId) {
            var titles: [Int: String] = [:]
            var last: UInt32?
            for (i, c) in map.chapters.enumerated() {
                guard let s = c.bookSection, let name = detail.chapterTitle(for: s) else { continue }
                // A chapter of the book read over two of the recording's.
                titles[i] = s == last ? String(localized: "\(name)（续）") : name
                last = s
            }
            bookTitles = titles
        }
        language = (try? core.book(id: map.bookId).summary.language) ?? "zh"
        // Each run of sentences not read, with where it falls in the recording.
        var out: [UnreadRun] = []
        var texts: [UInt32: [String]] = [:]
        var lastEnd: Double?
        var pending: [String] = []
        for r in map.rows {
            if r.by == .unread {
                if texts[r.section] == nil { texts[r.section] = (try? core.sentences(bookId: map.bookId, section: r.section)) ?? [] }
                if let t = texts[r.section], t.indices.contains(Int(r.sentence)) { pending.append(t[Int(r.sentence)]) }
                continue
            }
            if !pending.isEmpty, let at = lastEnd {
                out.append(UnreadRun(at: at, resume: Double(r.t0Ms) / 1000, texts: pending))
            }
            pending = []
            lastEnd = Double(r.t1Ms) / 1000
        }
        runs = out
    }

    func set(_ s: SkipSettings) {
        settings = s
        tap.clearer = s.clearerVoice
        if let books = Audiobooks.shared { books.set(s, for: bookID) } else { try? core.audiobookSetSkipSettings(bookId: bookID, s: s) }
    }

    /// 全书还剩: what's left, less what 缩短停顿 will save of it.
    var remaining: Double {
        let left = max(0, total - time)
        guard settings.shortenPauses, hasMap else { return left }
        return max(0, left - ((try? core.audiobookPauseSavings(bookId: bookID, seconds: time)) ?? 0))
    }

    private static func gainKey(_ id: String) -> String { "audiobook.gain." + id }

    /// 响度统一: measured once, kept.
    private func measureLoudness() {
        guard UserDefaults.standard.float(forKey: Self.gainKey(bookID)) == 0, let files = recording?.files, !files.isEmpty else { return }
        let urls = files.map { URL(filePath: $0.path) }
        let id = bookID
        Task { [weak self] in
            let g = await VoiceTap.measure(urls)
            UserDefaults.standard.set(g, forKey: Self.gainKey(id))
            self?.tap.gain = g
        }
    }

    // MARK: Where it is

    var total: Double { recording?.seconds ?? 0 }
    var chapters: [AudioChapter] { recording?.chapters ?? [] }

    /// The chapter `time` is in.
    var chapter: Int { chapter(at: time) }

    func chapter(at t: Double) -> Int {
        max(0, (chapters.lastIndex { $0.start <= t + 0.05 }) ?? 0)
    }

    /// `第 3 集`, or the recording's own name for it.
    func title(_ i: Int) -> String {
        if let t = bookTitles[i] { return t }
        return chapters.indices.contains(i) ? chapters[i].title ?? String(localized: "第 \(i + 1) 集") : summary.title
    }

    /// Seconds into the chapter being heard, and how long it is.
    var inChapter: (at: Double, of: Double) {
        guard chapters.indices.contains(chapter) else { return (0, 0) }
        let c = chapters[chapter]
        return (time - c.start, c.seconds)
    }

    private func seconds(at p: Position) -> Double {
        guard chapters.indices.contains(Int(p.section)) else { return 0 }
        return min(total, chapters[Int(p.section)].start + Double(p.sentence) / 1000)
    }

    private func position(at t: Double) -> Position {
        let i = chapter(at: t)
        let into = chapters.indices.contains(i) ? t - chapters[i].start : 0
        return Position(section: UInt32(i), sentence: UInt32(max(0, into * 1000).rounded()))
    }

    // MARK: Controls

    func togglePlay() { isPlaying ? pause() : play() }

    func play() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        guard let recording, !recording.files.isEmpty, !isPlaying else { return }
        #if os(iOS)
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .spokenAudio, policy: .longFormAudio)
            try session.setActive(true)
        } catch {
            failure = String(localized: "没法播放声音（\(error.localizedDescription)）")
            return
        }
        #endif
        #if os(iOS)
        // One voice at a time.
        if let held = library.nowPlaying, held !== self { library.nowPlaying = nil; held.stopAndClose() }
        #endif
        // Picked up after a while: back to the sentence's start, the one before or the
        // paragraph's, by how long it's been; a few seconds when it isn't laid out.
        var moved = false
        if let pausedAt {
            let gap = Date().timeIntervalSince(pausedAt)
            // Without a map the core goes back to a pause in the speech (≥ 0.4 s) instead.
            if let to = try? core.audiobookResumeAt(bookId: bookID, seconds: time, paused: gap), to < time - 0.05 {
                time = max(hasMap || !chapters.indices.contains(chapter) ? 0 : chapters[chapter].start, to)
                moved = true
            }
        }
        // 听写、精修 near here first, while it's being listened to.
        Audiobooks.shared?.work(on: bookID, at: time)
        // Listened on since the timer stopped it: nothing to ask next time.
        UserDefaults.standard.removeObject(forKey: Self.sleepKey(bookID))
        sleepReturn = nil
        // Heard to the end: from the start again.
        if waiting == nil, total > 0, time >= total - 0.5, recording.files.last.map({ $0.seconds > 0 }) ?? true {
            time = 0
            moved = true
        }
        pausedAt = nil
        // The queue goes where `time` is: changing `time` alone doesn't move what's heard, and a
        // queue played to its end has nothing left in it.
        if items.isEmpty || moved || queue.currentItem == nil { load(at: time) }
        queue.playImmediately(atRate: rate)
        isPlaying = true
        library.listening = bookID
        listened()
        startTicking()
        claimRemote()
        loadArtwork()
        armSleep()
        updateNowPlaying()
    }

    func pause() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        guard isPlaying else { return }
        // Paused while the voice reads what the recording leaves out: back to just before
        // them, so playing on reads them again rather than passing them by.
        let rewind = fillingRun.map { max(0, $0.at - 0.3) }
        stopFilling()
        // The timer waits while paused: no fading, no stopping, no key taken for 续 10 分钟.
        sleepTask?.cancel()
        if fading {
            fading = false
            queue.volume = 1
        }
        queue.pause()
        isPlaying = false
        pausedAt = Date()
        readTime()
        if let rewind { jump(to: rewind) }
        listened()
        save()
        if library.listening == bookID { library.listening = nil }
        updateNowPlaying()
    }

    func skip(_ by: Double) { seek(to: time + by) }

    /// ← →, 上一首 / 下一首: a sentence, once laid against the e-book; 15 seconds either way otherwise.
    func step(forward: Bool) {
        if hasMap, let t = (try? core.audiobookStep(bookId: bookID, seconds: time, forward: forward)) ?? nil {
            seek(to: t)
        } else {
            skip(forward ? Self.back : -Self.back)
        }
    }

    /// 点这里听 / 跳过 on the notice.
    func noticeAction() {
        guard let n = notice else { return }
        notice = nil
        switch n.kind {
        case .head:
            guard let e = n.extra else { return }
            passing = e
            seek(to: e.t0)
            if !isPlaying { play() }
        case .extra:
            if let e = n.extra { passing = nil; seek(to: e.t1) }
        case .filling:
            break
        case .fetching:
            // Tapped while waiting: it stays, and playing goes on when the file comes.
            notice = n
        }
    }

    func dismissNotice() {
        if notice?.kind == .head { notice = nil }
    }

    func seek(to t: Double) {
        // Elsewhere while the voice fills in: it stops, or the two would talk at once.
        stopFilling()
        time = min(max(0, t), max(0, total - 0.1))
        pausedAt = nil
        load(at: time)
        if isPlaying { queue.playImmediately(atRate: rate) }
        save(now: !isPlaying)
        updateNowPlaying()
    }

    func go(toChapter i: Int) {
        guard chapters.indices.contains(i) else { return }
        seek(to: chapters[i].start)
        if !isPlaying { play() }
    }

    func close() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        stopFilling()
        if isPlaying {
            readTime()
            listened(stopping: true)
        }
        queue.pause()
        isPlaying = false
        if !abandoned { save(now: true) }
        if library.listening == bookID { library.listening = nil }
        sleepTask?.cancel()
        if let ticker { queue.removeTimeObserver(ticker) }
        ticker = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
        releaseRemote()
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        queue.removeAllItems()
        items = []
        itemsStart = 0
    }

    func abandon() {
        #if os(iOS)
        resumeAfterInterruption = false
        #endif
        guard !abandoned else { return }
        abandoned = true
        stopFilling()
        queue.pause()
        isPlaying = false
        if library.listening == bookID { library.listening = nil }
        sleepTask?.cancel()
        if let ticker { queue.removeTimeObserver(ticker) }
        ticker = nil
        observers.forEach(NotificationCenter.default.removeObserver)
        observers = []
        releaseRemote()
        MPNowPlayingInfoCenter.default().nowPlayingInfo = nil
        queue.removeAllItems()
        items = []
        itemsStart = 0
    }

    /// 长按改名: a chapter's own name, or empty to have it called 第 n 集.
    func renameChapter(_ i: Int, _ name: String) {
        do {
            summary = try core.renameAudioChapter(bookId: bookID, chapter: UInt32(i), title: name)
            recording = try core.audioRecording(bookId: bookID)
            updateNowPlaying()
        } catch {
            failure = Library.message(for: error)
        }
    }

    // MARK: 书签

    func addBookmark() {
        let p = position(at: time)
        do {
            _ = try core.addBookmark(book: bookID, section: p.section, sentence: p.sentence)
            bookmarks = (try? core.bookmarks(book: bookID)) ?? bookmarks
        } catch {
            failure = Library.message(for: error)
        }
    }

    func deleteBookmark(_ b: Bookmark) {
        try? core.deleteBookmark(book: bookID, id: b.id)
        bookmarks.removeAll { $0.id == b.id }
    }

    func seconds(of b: Bookmark) -> Double { seconds(at: Position(section: b.section, sentence: b.sentence)) }

    /// The lock screen's 书签: the sentence just heard, highlighted in the e-book (in the colour
    /// last used); a 书签 when the recording isn't laid against one.
    func highlightHeard() {
        guard hasMap, let s = (try? core.audiobookHeardSentence(bookId: bookID, seconds: time)) ?? nil,
              let all = try? core.sentences(bookId: s.book, section: s.section), all.indices.contains(Int(s.sentence)) else {
            addBookmark()
            toast = Library.Toast(icon: "bookmark", title: String(localized: "记了一个书签"), detail: "\(title(chapter)) \(Self.clock(inChapter.at))")
            return
        }
        let raw = all[Int(s.sentence)]
        let text = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !text.isEmpty, let range = raw.range(of: text) else { return }
        let point = { TextPoint(sentence: s.sentence, offset: UInt32(raw.utf16.distance(from: raw.startIndex, to: $0))) }
        let known = Set(((try? core.marks(book: s.book)) ?? []).map(\.id))
        let style = UserDefaults.standard.string(forKey: "reader.markStyle").flatMap(MarkStyle.init(css:)) ?? .yellow
        guard let mark = try? core.markHeard(book: s.book, section: s.section, start: point(range.lowerBound), end: point(range.upperBound), text: text, style: style) else { return }
        let new = !known.contains(mark.id)
        let core = core
        toast = Library.Toast(icon: "highlighter", title: new ? String(localized: "划了一句") : String(localized: "这句已经划过了"), detail: "“\(text.prefix(24))”",
                              undo: new ? { try? core.deleteMark(book: s.book, id: mark.id) } : nil)
    }

    // MARK: Playing the files

    /// 边下边听: waiting for this file to come from the share before playing on.
    private(set) var waiting: Int?

    /// 边下边听: whether this recording comes from a share.
    var streaming: Bool { ShareStream.shared.streams(bookID) }

    /// Whether file `i` is on this device.
    func isHere(file i: Int) -> Bool {
        guard let recording, recording.files.indices.contains(i) else { return true }
        return FileManager.default.fileExists(atPath: recording.files[i].path)
    }

    /// The file chapter `i` starts in.
    func file(ofChapter i: Int) -> Int {
        guard chapters.indices.contains(i) else { return 0 }
        if let recording, chapters.count == recording.files.count, recording.files[i].seconds <= 0 {
            return i
        }
        return fileIndex(at: chapters[i].start + 0.01)
    }

    /// Files here, of all.
    var filesHere: (have: Int, total: Int) {
        guard let recording else { return (0, 0) }
        return (recording.files.indices.filter(isHere).count, recording.files.count)
    }

    /// In the chapter now: how far into it is here to play on from the start (the file coming
    /// down counted as far as it has come), and where each file after its first starts in it.
    var hereInChapter: (down: Double, ticks: [Double]) {
        guard let recording, chapters.indices.contains(chapter) else { return (0, []) }
        let c = chapters[chapter]
        let end = c.start + c.seconds
        let fetching = ShareStream.shared.fetching[bookID]
        var down = 0.0
        var ticks: [Double] = []
        var open = true
        for (i, s) in starts.enumerated() where i < recording.files.count {
            let e = s + recording.files[i].seconds
            guard e > c.start + 0.01, s < end - 0.01 else { continue }
            if s > c.start + 0.01 { ticks.append(s - c.start) }
            guard open else { continue }
            if isHere(file: i) {
                down = min(end, e) - c.start
            } else {
                if fetching?.file == i { down = max(down, min(end, s + recording.files[i].seconds * (fetching?.fraction ?? 0)) - c.start) }
                open = false
            }
        }
        return (max(0, down), ticks)
    }

    /// 听过的自动清掉, for this recording.
    var clearsListened: Bool {
        get { _ = clearsTick; return ShareStream.shared.clears(bookID) }
        set {
            ShareStream.shared.setClears(bookID, newValue, at: currentFile, core: core)
            clearsTick += 1
            reloadRecording()
        }
    }
    private var clearsTick = 0

    /// The file being listened to.
    var currentFile: Int { waiting ?? fileIndex(at: time) }

    /// Resolves which file index covers `t`. When unarrived trailing files have zero duration and
    /// therefore share `starts[k] == ... == starts[n-1] == total`, returns the first such file `k`
    /// rather than jumping to the last file `n - 1`.
    private func fileIndex(at t: Double) -> Int {
        guard let recording, !starts.isEmpty else { return 0 }
        let count = min(starts.count, recording.files.count)
        let clamped = min(max(0, t), total)
        for i in 0 ..< count {
            let s = starts[i]
            let d = recording.files[i].seconds
            if clamped < s + d { return i }
            if d <= 0 && clamped <= s { return i }
        }
        return max(0, count - 1)
    }

    /// The recording again from the core: a file that came from the share may know its length now.
    private func reloadRecording() {
        guard let r = try? core.audioRecording(bookId: bookID) else { return }
        recording = r
        starts = []
        var at = 0.0
        for f in r.files {
            starts.append(at)
            at += f.seconds
        }
    }

    /// 边下边听: the file isn't here yet; it's fetched first, and playing goes on when it comes.
    private func wait(for file: Int) {
        waiting = file
        // Shown on the play button (a ring filling) and a line under it, not as a notice.
        ShareStream.shared.want(bookID, file: file, core: core)
    }

    /// A file came from the share: if it's the one waited for, on we go.
    private func arrived(_ file: Int) {
        reloadRecording()
        guard let w = waiting, w == file else { return }
        waiting = nil
        let into = starts.indices.contains(file) ? max(0, time - starts[file]) : 0
        load(file: file, into: into)
        if isPlaying { queue.playImmediately(atRate: rate) }
    }

    /// The queue from the file `t` falls in, sought to `t` — up to the first file not here yet.
    private func load(at t: Double) {
        let i = fileIndex(at: t)
        let into = starts.indices.contains(i) ? max(0, t - starts[i]) : 0
        load(file: i, into: into)
    }

    private func load(file i: Int, into: Double) {
        guard let recording, recording.files.indices.contains(i) else { return }
        queue.removeAllItems()
        let here = recording.files[i...].prefix { FileManager.default.fileExists(atPath: $0.path) }
        if here.isEmpty {
            items = []
            itemsStart = 0
            wait(for: i)
            return
        }
        waiting = nil
        itemsStart = i
        items = here.map { f in
            let item = AVPlayerItem(url: URL(filePath: f.path))
            item.audioTimePitchAlgorithm = .timeDomain
            // 响度统一, 人声清楚一点.
            let tap = tap
            Task { @MainActor in
                if let track = try? await item.asset.loadTracks(withMediaType: .audio).first { item.audioMix = VoiceTap.mix(track, tap) }
            }
            return item
        }
        for item in items { queue.insert(item, after: nil) }
        if into > 0.05 {
            queue.seek(to: CMTime(seconds: into, preferredTimescale: 1000), toleranceBefore: .zero, toleranceAfter: .zero)
        }
    }

    private func finished(_ item: AVPlayerItem?) {
        guard let item, item === items.last else { return }
        // 边下边听: continue into the next file after the loaded contiguous segment.
        if let recording {
            let next = itemsStart + items.count
            if next < recording.files.count {
                time = starts.indices.contains(next) ? starts[next] : total
                if FileManager.default.fileExists(atPath: recording.files[next].path) {
                    load(file: next, into: 0)
                    if isPlaying { queue.playImmediately(atRate: rate) }
                } else {
                    items = []
                    itemsStart = 0
                    wait(for: next)
                }
                return
            }
        }
        // The last file heard to its end: the book is done.
        time = total
        isPlaying = false
        pausedAt = nil
        listened()
        save(now: true)
        if library.listening == bookID { library.listening = nil }
        sleep = .off
        updateNowPlaying()
    }

    /// To `t` within the file playing when it's there (a pause cut short, 片头 passed), else the
    /// queue from its file.
    private func jump(to t: Double) {
        time = min(max(0, t), max(0, total - 0.1))
        let i = fileIndex(at: time)
        if let item = queue.currentItem, let k = items.firstIndex(where: { $0 === item }),
           itemsStart + k == i, starts.indices.contains(i) {
            item.seek(to: CMTime(seconds: time - starts[i], preferredTimescale: 1000), toleranceBefore: .zero, toleranceAfter: .zero, completionHandler: nil)
        } else {
            load(at: time)
            if isPlaying { queue.playImmediately(atRate: rate) }
        }
    }

    private func startTicking() {
        guard ticker == nil else { return }
        ticker = queue.addPeriodicTimeObserver(forInterval: CMTime(seconds: 0.15, preferredTimescale: 600), queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
    }

    private func tick() {
        guard isPlaying, speaker == nil else { return }
        let before = chapter
        let was = time
        readTime()
        if follow(from: was) { return }
        if sleep == .chapter, chapter != before {
            // 本集完: stop at the start of the next one.
            seek(to: chapters[chapter].start)
            sleep = .off
            pause()
            return
        }
        if chapter != before { updateNowPlaying() }
        if Date().timeIntervalSince(savedAt) > 10 { save() }
        if let since = listenSince, Date().timeIntervalSince(since) > 60 { listened() }
    }

    /// `time` from the file playing and how far into it.
    private func readTime() {
        guard let item = queue.currentItem, let i = items.firstIndex(where: { $0 === item }) else { return }
        let fileIndex = itemsStart + i
        guard starts.indices.contains(fileIndex) else { return }
        let at = CMTimeGetSeconds(item.currentTime())
        guard at.isFinite else { return }
        time = min(total, starts[fileIndex] + max(0, at))
    }

    /// What the map says about the moment: pass it, cut it short, say what it is. True when
    /// playing went somewhere else.
    private func follow(from was: Double) -> Bool {
        guard hasMap, let m = try? core.audiobookMoment(bookId: bookID, seconds: time) else { return false }
        if let p = passing {
            if m.extra == p { return false }
            passing = nil
        }
        if let to = m.skipTo {
            if let e = m.extra {
                if e.place == .tail, to >= total - 0.5 {
                    // 片尾: the book is over.
                    time = total
                    pause()
                    return true
                }
                if e.place == .head {
                    notice = Notice(kind: .head, text: String(localized: "跳过了片头 \(Self.clock(e.t1 - e.t0)) · 点这里听"), action: String(localized: "点这里听"), extra: e)
                }
            } else {
                // A long pause cut short.
                unsavedSaving += to - time
                saved += to - time
            }
            jump(to: to)
            return true
        }
        if let e = m.extra, e.place == .middle {
            let n = Notice(kind: .extra, text: String(localized: "不在书里的话 · \(Self.clock(e.t1 - e.t0))"), action: String(localized: "跳过"), extra: e)
            if notice != n { notice = n }
        } else if notice?.kind == .extra {
            notice = nil
        }
        // 没念的用朗读补上: at the end of the sentence before them.
        if settings.fillUnread, time > was, let run = runs.first(where: { was < $0.at && time >= $0.at - 0.05 }) {
            fill(run)
            return true
        }
        if case .sentence(let end) = sleep, time >= end - 0.05 {
            fallAsleep()
            return true
        }
        return false
    }

    /// Reads what the recording leaves out with a voice, then on from where it picks up again.
    private func fill(_ run: UnreadRun) {
        queue.pause()
        notice = Notice(kind: .filling, text: String(localized: "录音里没念这里，朗读补上"), action: nil)
        let synth = AVSpeechSynthesizer()
        let done = SpokenDone { [weak self] in
            guard let self, self.speaker === synth else { return }
            self.speaker = nil
            self.spoken = nil
            self.fillingRun = nil
            self.notice = nil
            self.jump(to: run.resume)
            if self.isPlaying { self.queue.playImmediately(atRate: self.rate) }
        }
        synth.delegate = done
        speaker = synth
        spoken = done
        fillingRun = run
        let voice = AVSpeechSynthesisVoice(language: language)
        for (k, t) in run.texts.enumerated() {
            let u = AVSpeechUtterance(string: t)
            u.voice = voice
            u.rate = AVSpeechUtteranceDefaultSpeechRate * min(1.6, max(0.8, rate))
            done.last = k == run.texts.count - 1 ? u : done.last
            synth.speak(u)
        }
    }

    private func stopFilling() {
        guard let speaker else { return }
        self.speaker = nil
        spoken = nil
        fillingRun = nil
        speaker.stopSpeaking(at: .immediate)
        if notice?.kind == .filling { notice = nil }
    }

    // MARK: Progress and time listened

    private func save(now: Bool = false) {
        guard !abandoned, recording != nil else { return }
        savedAt = Date()
        if unsavedSaving > 0 {
            try? core.audiobookAddSaved(bookId: bookID, seconds: unsavedSaving)
            unsavedSaving = 0
        }
        do {
            try core.saveProgress(bookId: bookID, position: position(at: time))
        } catch {
            failure = Library.message(for: error)
        }
        if now { library.reload() }
    }

    private func listened(stopping: Bool = false) {
        let now = Date()
        if let since = listenSince {
            let seconds = now.timeIntervalSince(since)
            if seconds > 0, seconds < 600 {
                try? core.addTime(book: bookID, section: UInt32(chapter), readSeconds: 0, listenSeconds: UInt32(seconds.rounded()), day: Notes.today)
            }
        }
        listenSince = isPlaying && !stopping ? now : nil
    }

    // MARK: 定时

    private func armSleep() {
        sleepTask?.cancel()
        fading = false
        queue.volume = 1
        guard case .until(let date, _) = sleep else { return }
        // Its time passed while paused: over, rather than stopping as soon as it plays.
        if date <= Date() {
            sleep = .off
            return
        }
        guard isPlaying else { return }
        sleepTask = Task { [weak self] in
            let lead = date.timeIntervalSinceNow - Player.fadeSeconds
            if lead > 0 { try? await Task.sleep(for: .seconds(lead)) }
            // The last 20 seconds fade out; a key or a shake in them gives 10 minutes more.
            while !Task.isCancelled, let self, case .until(let d, _) = self.sleep {
                let left = d.timeIntervalSinceNow
                if left <= 0 { break }
                if !self.fading { self.fading = true }
                self.queue.volume = Float(min(1, left / Player.fadeSeconds))
                try? await Task.sleep(for: .milliseconds(200))
            }
            guard !Task.isCancelled else { return }
            self?.fallAsleep()
        }
    }

    /// The timer stopped it: where it was set is offered next time (回到昨晚).
    private func fallAsleep() {
        let from = sleepFrom ?? time
        pause()
        sleep = .off
        UserDefaults.standard.set([from, Date().timeIntervalSince1970], forKey: Self.sleepKey(bookID))
    }

    /// 续 10 分钟.
    func extendSleep() {
        guard fading, case .until(_, let m) = sleep else { return }
        sleep = .until(Date().addingTimeInterval(600), minutes: m)
    }

    /// 念完这句: stops where the sentence being heard ends.
    var canStopAtSentence: Bool { hasMap }

    func sleepAtSentenceEnd() {
        guard let b = (try? core.audiobookSentenceBounds(bookId: bookID, seconds: time)) ?? nil else { return }
        sleep = .sentence(end: b.t1)
    }

    private static func sleepKey(_ id: String) -> String { "sleepFrom." + id }

    private func offerSleepReturn() {
        guard let v = UserDefaults.standard.array(forKey: Self.sleepKey(bookID)) as? [Double], v.count == 2,
              Date().timeIntervalSince1970 - v[1] < 7 * 86_400 else { return }
        sleepReturn = (v[0], Date(timeIntervalSince1970: v[1]))
    }

    func answerSleepReturn(go: Bool) {
        if go, let r = sleepReturn { seek(to: r.seconds) }
        sleepReturn = nil
        UserDefaults.standard.removeObject(forKey: Self.sleepKey(bookID))
    }

    // MARK: Lock screen

    private func updateNowPlaying() {
        let center = MPNowPlayingInfoCenter.default()
        guard Self.remoteOwner === self else { return }
        let (at, of) = inChapter
        var info: [String: Any] = [
            MPMediaItemPropertyTitle: title(chapter),
            MPMediaItemPropertyAlbumTitle: summary.title,
            MPNowPlayingInfoPropertyMediaType: MPNowPlayingInfoMediaType.audio.rawValue,
            MPMediaItemPropertyPlaybackDuration: of,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: at,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? Double(rate) : 0,
            MPNowPlayingInfoPropertyDefaultPlaybackRate: 1.0,
            MPNowPlayingInfoPropertyChapterNumber: chapter,
            MPNowPlayingInfoPropertyChapterCount: chapters.count,
        ]
        if let author = summary.authors.first { info[MPMediaItemPropertyArtist] = author }
        if let artwork { info[MPMediaItemPropertyArtwork] = artwork }
        center.nowPlayingInfo = info
        #if os(macOS)
        center.playbackState = isPlaying ? .playing : .paused
        #endif
    }

    private func loadArtwork() {
        guard artwork == nil, let cover = summary.coverPath else { return }
        Task { [weak self] in
            guard let image = await Thumbnail.load(cover, maxPixels: 600) else { return }
            self?.artwork = Self.artwork(image)
            self?.updateNowPlaying()
        }
    }

    /// The lock screen and media keys go to the one that last started playing.
    private func claimRemote() {
        NotificationCenter.default.post(name: .yyAudioClaimed, object: self)
        guard Self.remoteOwner !== self else { return }
        Self.remoteOwner?.releaseRemote()
        Self.remoteOwner = self
        let center = MPRemoteCommandCenter.shared()
        let handlers: [(MPRemoteCommand, @MainActor @Sendable (AudiobookPlayer) -> Void)] = [
            (center.playCommand, { $0.play() }),
            (center.pauseCommand, { $0.pause() }),
            (center.togglePlayPauseCommand, { $0.togglePlay() }),
            (center.skipBackwardCommand, { $0.skip(-Self.back) }),
            (center.skipForwardCommand, { $0.skip(Self.ahead) }),
            // Headphones and cars send 上一曲 / 下一曲: a sentence, or 15 seconds.
            (center.previousTrackCommand, { $0.step(forward: false) }),
            (center.nextTrackCommand, { $0.step(forward: true) }),
            // The lock screen's, the car's and the watch's 书签: the sentence just heard, highlighted.
            (center.bookmarkCommand, { $0.highlightHeard() }),
        ]
        center.skipBackwardCommand.preferredIntervals = [NSNumber(value: Self.back)]
        center.skipForwardCommand.preferredIntervals = [NSNumber(value: Self.ahead)]
        for (command, action) in handlers {
            command.isEnabled = true
            let target = command.addTarget(handler: Self.command { [weak self] in
                // Any key while the timer fades out: 10 minutes more instead.
                guard let self else { return }
                if self.fading { self.extendSleep() } else { action(self) }
            })
            remoteTargets.append((command, target))
        }
        let position = center.changePlaybackPositionCommand
        position.isEnabled = true
        remoteTargets.append((position, position.addTarget(handler: Self.positionCommand { [weak self] t in
            guard let self, self.chapters.indices.contains(self.chapter) else { return }
            self.seek(to: self.chapters[self.chapter].start + t)
        })))
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

    // MediaPlayer calls back on its own threads; these hop to the main actor for the work.

    nonisolated private static func command(_ work: @escaping @MainActor @Sendable () -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { _ in
            Task { @MainActor in work() }
            return .success
        }
    }

    nonisolated private static func positionCommand(_ work: @escaping @MainActor @Sendable (Double) -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { event in
            guard let t = (event as? MPChangePlaybackPositionCommandEvent)?.positionTime else { return .commandFailed }
            Task { @MainActor in work(t) }
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

    /// `14:22`, `1:02:05`.
    static func clock(_ seconds: Double) -> String {
        let s = Int(max(0, seconds).rounded(.down))
        return s >= 3600 ? String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60) : String(format: "%d:%02d", s / 60, s % 60)
    }
}

/// Says when the last of a run of utterances has been spoken.
private final class SpokenDone: NSObject, AVSpeechSynthesizerDelegate, @unchecked Sendable {
    var last: AVSpeechUtterance?
    private let done: @MainActor () -> Void

    init(_ done: @escaping @MainActor () -> Void) {
        self.done = done
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        let id = ObjectIdentifier(utterance)
        Task { @MainActor in
            if let last = self.last, ObjectIdentifier(last) == id { self.done() }
        }
    }
}
