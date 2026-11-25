import AVFoundation
import SwiftUI
import YuyueCore
#if os(iOS)
import UIKit
#else
import AppKit
#endif

/// One open book: where the reader is, how the pages look, and the voice reading it aloud.
@Observable
final class ReaderModel {
    let bookID: String
    private(set) var detail: BookDetail? {
        didSet {
            cachedTocChapters = nil
            cachedUnitPrefix = nil
        }
    }
    @ObservationIgnored var cachedTocChapters: [TocChapter]?
    /// Prefix sums of `units` for `linear && kind == .text` sections: `prefix[i]` = total units in sections `< i`.
    @ObservationIgnored var cachedUnitPrefix: (prefix: [Double], total: Double)?
    private(set) var settings: ReaderSettings
    /// Whether this book keeps settings of its own (只用于这本书).
    private(set) var ownSettings = false
    /// The Mac window's panel on the right.
    var panel: ReaderPanel?
    /// Words handed to the companion (问), and where they sit on the page.
    var companionQuote: String?
    var companionAnchor: CompanionAnchor?
    /// 与书对话.
    let companion = BookCompanion()
    /// 搜索 · 问 ⌘K over this window.
    var quickSearchShown = false
    /// 外文原著: the word clicked, and its card.
    var lookup: WordLookup? {
        didSet { syncBottomCapsuleVisible() }
    }
    /// This book's words in the 生词本, in reading order.
    var bookWords: [VocabWord] = []
    /// 双击一句只翻这一句.
    var sentenceTranslation: SentenceTranslation?
    /// 译: 原文 / 译文对照 / 仅译文.
    var translateMode: TranslateMode = .off
    /// Last active translation style remembered for one-tap toolbar toggle.
    var lastActiveTranslateMode: TranslateMode = .bilingual
    var translateShown: Bool { translateMode != .off }
    @ObservationIgnored let translator = Translator()
    @ObservationIgnored var speaker: AVSpeechSynthesizer?
    /// 只听: the text hidden while the voice reads.
    var listenOnly = false
    /// 沉浸阅读: only the words on the page.
    var immersive = false {
        didSet {
            barsAway = false
            applyImmersive()
        }
    }

    private func applyImmersive() {
        // 墨水屏模式 turns whole pages, never slides them.
        pager.calm = (immersive && ImmersivePrefs.stillPages) || einkOn
        pager.marksHidden = immersive && ImmersivePrefs.hidesMarks
        pager.glossesHidden = immersive && ImmersivePrefs.hidesHints
        syncBottomCapsuleVisible()
        if immersive { panel = nil }
    }

    private func syncBottomCapsuleVisible() {
        #if os(iOS)
        pager.bottomCapsuleVisible = lookup == nil && ((!immersive && !barsAway) || chromeShown || searchShown)
        #endif
    }
    /// Local device 墨水屏模式 state (never synced across devices).
    private(set) var einkOn = EInk.on
    private(set) var einkGrayscale = EInk.grayscale
    /// The resolved paper currently applied to the pager, so appearance switches trigger an update.
    private var appliedPaper: PaperAppearance.Resolved
    /// The section on screen, and its page.
    private(set) var section: UInt32?
    private(set) var page = 0
    private(set) var pageCount = 1
    var chromeShown = false {
        didSet {
            if chromeShown { barsAway = false }
            syncBottomCapsuleVisible()
        }
    }
    /// Scrolling down the page sent the capsule away, and the top bar if it was up.
    private(set) var barsAway = false {
        didSet { syncBottomCapsuleVisible() }
    }
    private var chromeComesBack = false
    var tocShown = false
    /// Initial tab when opening TOC sheet: 0 = 目录, 1 = 书签, 2 = 笔记.
    var tocInitialTab = 0
    var settingsShown = false
    /// 替换规则, from 更多 or the menu bar.
    var rulesShown = false
    /// 替换规则 was opened from 排版, so its ‹ goes back there. From the menu it has no ‹.
    var rulesFromSettings = false
    /// 字样册 (更多字体), opened from the Mac Aa card onto the reader window so it isn't clipped to the popover.
    var fontBookShown = false
    /// 气泡模式: the dialogue as bubbles, kept per book.
    var bubbles = false {
        didSet {
            guard bubbles != oldValue else { return }
            pager.bubbles = bubbles
            try? core.setBookBubbles(bookId: bookID, on: bubbles)
        }
    }
    /// Whether the book has enough dialogue for 气泡 and 人物: books without (essays, papers)
    /// don't offer them.
    private(set) var hasDialogue = false
    @ObservationIgnored nonisolated(unsafe) private var castObserver: NSObjectProtocol?
    @ObservationIgnored nonisolated(unsafe) private var dramaObserver: NSObjectProtocol?
    /// 编码和目录, for a TXT.
    var txtShown = false
    /// Why the TXT may have been read wrong, for the hint that leads to 编码和目录.
    var txtDoubt: String?
    var failure: String?
    let player: Player?

    /// The sentence the reader is at in the current section: the one being read aloud, or the
    /// first on the page.
    private(set) var sentence = 0
    var marks: [Mark] = []
    var bookmarks: [Bookmark] = []
    var times: [UInt32: SectionTime] = [:]
    /// Text selected on the page, for the card over it.
    var selection: TextSelection?
    /// A highlight clicked on the page, for the card over it.
    var openMark: OpenMark?
    /// 气泡: a bubble's avatar clicked, the card for who says it over the page.
    var speakerPick: SpeakerPick?
    /// 别的人物… from that card: the whole cast to search.
    var speakerOthers: SpeakerPick?
    /// 脚注: a note marker tapped, its note in a card over the page.
    var openNote: OpenNote?
    /// 书内搜索.
    var searchShown = false {
        didSet { syncBottomCapsuleVisible() }
    }
    /// Bumped when the open search field should take the keyboard again.
    var searchFieldWanted = 0
    var query = ""
    var hits: [SearchHit] = []
    /// 局域网传录音: this book's recording, laid out on another device but not here, and a Mac
    /// nearby that has it.
    var missingRecording: String?
    var recordingOffer: RecordingFetch.Offer?
    /// 文字版: made from a recording's words; the page says so.
    @ObservationIgnored lazy var textVersion: Bool = core.audiobookIsTextVersion(book: bookID)
    /// 逐词跟读's word times, by "section.sentence".
    @ObservationIgnored var wordCache: [String: [Double]?] = [:]
    var hitIndex: Int?
    /// The colour a highlight gets when none is picked: the last one used.
    var lastStyle: MarkStyle = .yellow
    /// The undo note at the bottom of the page.
    var toast: Library.Toast?
    /// 进度冲突: where another device read to, offered when the book opens.
    var elsewhere: Elsewhere?
    /// 回到原处: where the reader was before jumping off (目录, 搜索, a link, 出处), kept until
    /// a few pages have been read in the new place.
    private(set) var returnTo: (position: Position, page: Int?)?
    /// 前情提要, over the page until 接着读 or a page is turned.
    var recap: Recap?
    /// 正文模式: the quote last right-clicked, for the menu's 谁说的.
    @ObservationIgnored var contextLine: (section: UInt32, line: BubbleLine, at: CGPoint)?
    /// The sentence last right-clicked, for the menu's 循环到这里.
    @ObservationIgnored var contextSentence: (section: UInt32, sentence: Int)?
    /// Bumped when the recording's guide (`RecordingGuide`) is loaded again.
    var guideVersion = 0
    /// Chapters' unread sentences shown dim, while the recording reads.
    @ObservationIgnored var unreadShown = false
    /// 回到昨晚: the timer stopped the voice last time; where it was set.
    var sleepReturn: SleepReturn?
    @ObservationIgnored private var turnsSinceJump = 0
    @ObservationIgnored private var jumpHits = false
    @ObservationIgnored private var jumpHitTurns = 0

    struct Elsewhere {
        let progress: ReadingProgress
        /// How far this device is.
        let here: Double
        let text: String?
        let chapter: String?
    }

    @ObservationIgnored let pager: ReaderPager
    @ObservationIgnored let core: YuyueCore
    @ObservationIgnored private(set) weak var library: Library?
    @ObservationIgnored private var madeNoteSender: NoteSender?
    /// 导出笔记's sends, told on the page.
    var noteSender: NoteSender {
        if let s = madeNoteSender { return s }
        let s = NoteSender(core: core) { [weak self] in self?.show($0) }
        madeNoteSender = s
        return s
    }
    /// After a page is turned by hand the pages stop following the voice for a while, so the
    /// reader can look back without being pulled along.
    @ObservationIgnored var followFrom = Date.distantPast
    /// True after the reader manually turned or scrolled away while paused, so the capsule and
    /// sentence stepping follow what is now on screen rather than the old paused `player.spot`.
    @ObservationIgnored private var pausedOffSpot = false
    /// The last sentence reported by `player`, so pause/rate updates don't re-scroll in scrolled mode.
    @ObservationIgnored private var lastSpokenSpot: Player.Spot?
    @ObservationIgnored private var saved: Position?
    /// Opening a book only shows where it was; the position is written once the reader moves,
    /// so a glance on one device doesn't bury further reading on another.
    @ObservationIgnored private var opened = false
    @ObservationIgnored nonisolated(unsafe) private var saving: Task<Void, Never>?
    @ObservationIgnored var clock = TimeClock()
    @ObservationIgnored nonisolated(unsafe) var searching: Task<Void, Never>?
    @ObservationIgnored nonisolated(unsafe) private var watching: [Task<Void, Never>] = []
    /// The book is being built again; this model is about to be replaced and writes nothing more.
    @ObservationIgnored private var retired = false
    /// Heard at once, not later on a task: the core builds the book right after it's posted.
    @ObservationIgnored nonisolated(unsafe) private var rebuildObserver: NSObjectProtocol?
    @ObservationIgnored nonisolated(unsafe) private var terminationObserver: NSObjectProtocol?

    init(library: Library, bookID: String) {
        let core = library.core
        self.core = core
        self.library = library
        self.bookID = bookID
        var detail: BookDetail?
        var failure: String?
        if let primed = library.takePrimedDetail(bookID) {
            detail = primed
        } else {
            do {
                detail = try core.book(id: bookID)
            } catch {
                failure = Library.message(for: error)
            }
        }
        self.detail = detail
        scriptValue = (try? core.bookScript(bookId: bookID)) ?? nil
        let own = (try? core.bookReaderSettings(bookId: bookID)) ?? nil
        let settings = own ?? (try? core.readerSettings()) ?? .standard
        self.settings = settings
        self.ownSettings = own != nil
        let resolved = PaperAppearance.shared.resolve(settings)
        self.appliedPaper = resolved
        let initialPaper = EInk.on ? EInk.paper : resolved.tokens
        pager = ReaderPager(core: core, bookID: bookID, mode: settings.mode, background: initialPaper.platformBackground)
        pager.ink = initialPaper.platformInk
        pager.accent = initialPaper.platformAccent
        player = detail.map { Player(core: core, detail: $0) }
        self.failure = failure
        companion.model = self
        pager.fontSize = settings.fontSize
        pager.pageTurn = PageTurnPrefs.current
        pager.chapterTitle = { [weak self] section in self?.detail?.chapterTitle(for: section) }
        pager.delegate = self
        if let player {
            player.recording = Audiobooks.shared?.recording(for: bookID)
            loadFlags()
            let rate = UserDefaults.standard.float(forKey: Self.rateKey)
            if rate > 0 { player.rate = rate }
            player.onSentence = { [weak self] spot, duration, elapsed, playing in
                self?.pager.isReadingAloud = playing
                self?.reading(spot, durationMs: duration, elapsedMs: elapsed, playing: playing)
                self?.setListening(playing)
            }
            player.onStop = { [weak self] in
                self?.pager.isReadingAloud = false
                self?.lastSpokenSpot = nil
                self?.pausedOffSpot = false
                self?.flush()
                self?.pager.clearInk()
                self?.setListening(false)
            }
            player.onSleep = { [weak self] spot in self?.fellAsleep(at: spot) }
            player.onMarkHeard = { [weak self] spot in self?.markHeard(spot) }
            player.onSkip = { [weak self] in
                self?.followFrom = .distantPast
                self?.pausedOffSpot = false
                self?.lastSpokenSpot = nil
            }
        }
        // 我的 › 沉浸阅读 on: every book opens with only its words.
        immersive = ImmersivePrefs.always
        applyImmersive()
        loadNotes()
        setUpForeign()
        if detail?.summary.format == .txt, let doubt = (try? core.txtDoubt(bookId: bookID)) ?? nil,
           UserDefaults.standard.string(forKey: Self.doubtKey(bookID)) != doubt {
            txtDoubt = doubt
        }
        bubbles = (try? core.bookBubbles(bookId: bookID)) ?? false
        pager.bubbles = bubbles
        castObserver = NotificationCenter.default.addObserver(forName: CastContext.changed, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.pager.bubblesChanged() }
        }
        // The model answered a scene: the bubbles and who reads what follow.
        dramaObserver = NotificationCenter.default.addObserver(forName: DramaWork.updated, object: nil, queue: .main) { [weak self] n in
            let book = n.object as? String
            MainActor.assumeIsolated {
                guard let self, book == self.bookID else { return }
                self.pager.bubblesChanged()
                self.player?.speakersChanged()
            }
        }
        Task { [core, bookID, weak self] in
            // A few quotes in the first chapters with them: a book with dialogue.
            let n = await Self.countOpeningLines(core, bookID: bookID)
            self?.hasDialogue = n >= 6
        }
        rebuildObserver = NotificationCenter.default.addObserver(forName: .bookWillRebuild, object: nil, queue: nil) { [weak self] n in
            let id = n.object as? String
            MainActor.assumeIsolated {
                guard let self, id == self.bookID else { return }
                self.retire()
            }
        }
        #if os(macOS)
        let willTerminate = NSApplication.willTerminateNotification
        #else
        let willTerminate = UIApplication.willTerminateNotification
        #endif
        terminationObserver = NotificationCenter.default.addObserver(forName: willTerminate, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.tick(closing: true)
                self?.flush()
            }
        }
        watching = [
            Task { [weak self] in
                for await _ in NotificationCenter.default.notifications(named: .readerSettingsChanged) {
                    self?.sharedSettingsChanged()
                }
            },
            Task { [weak self] in
                // 我的纸 changed: the page on one takes its new colours.
                for await _ in NotificationCenter.default.notifications(named: Papers.changed) {
                    guard let self, self.previewing == nil else { continue }
                    self.applyToPager(self.settings)
                }
            },
            Task { [weak self] in
                for await n in NotificationCenter.default.notifications(named: .replaceRulesChanged) {
                    guard let self, n.object == nil || n.object as? String == self.bookID else { continue }
                    self.player?.textsChanged()
                    self.pager.reload()
                    if !self.query.isEmpty { self.search() }
                }
            },
            Task { [weak self] in
                // 整理错字 done: the 文字版's words changed.
                for await n in NotificationCenter.default.notifications(named: TextPolish.Run.finished) {
                    guard let self, n.object as? String == self.bookID else { continue }
                    self.player?.textsChanged()
                    self.pager.reload()
                }
            },
            Task { [weak self] in
                // 霞鹜文楷 arrived: the open sections load again, now with its @font-face.
                for await _ in NotificationCenter.default.notifications(named: .fontsChanged) {
                    self?.pager.reload()
                }
            },
            Task { [weak self] in
                for await n in NotificationCenter.default.notifications(named: .readerJump) {
                    guard let self, n.object as? String == self.bookID else { continue }
                    self.consumeIntent()
                }
            },
            Task { [weak self] in
                for await _ in NotificationCenter.default.notifications(named: .vocabChanged) { self?.loadVocab() }
            },
        ]
        if let detail {
            // Pick up where this book was left, on whichever device that was; or from the
            // top when the shelf said 从头听.
            let intent = library.takeIntent(for: bookID)
            let start = Position(section: detail.start, sentence: 0)
            let mine = try? core.progress(bookId: bookID)
            let latest = try? core.latestProgress(bookId: bookID)
            var position = intent == .listenFromStart ? start : mine?.position ?? latest?.position ?? start
            if case .at(let there, let query, let hit) = intent {
                position = there
                if let query, !query.isEmpty {
                    pager.setHits(query: query, current: hit)
                    jumpHits = true
                    jumpHitTurns = 0
                }
            }
            if case .ask(let q) = intent { ask(q) }
            // 进度冲突: this device has its own place, but another read on later, elsewhere.
            if intent == nil, let mine, let latest, !latest.thisDevice, latest.at > mine.at, latest.position != mine.position {
                let sentences = (try? core.sentences(bookId: bookID, section: latest.position.section)) ?? []
                let i = Int(latest.position.sentence)
                elsewhere = Elsewhere(progress: latest, here: mine.fraction,
                                      text: sentences.indices.contains(i) ? sentences[i] : nil,
                                      chapter: detail.chapterTitle(for: latest.position.section))
            }
            if intent == nil, elsewhere == nil, let last = latest ?? mine {
                startRecap(lastRead: last.at, here: position)
            }
            if intent == nil, elsewhere == nil { offerSleepReturn() }
            #if os(iOS)
            Task { await lookForRecording() }
            #endif
            pager.open(section: position.section, target: position.sentence == 0 ? .start : .sentence(Int(position.sentence)))
            if intent == .listen || intent == .listenFromStart, let player {
                player.play(from: position)
            }
        }
    }

    /// Consumes a pending intent when reusing an already-open reader model.
    func consumeIntent() {
        guard let intent = library?.takeIntent(for: bookID) else { return }
        switch intent {
        case .at(let p, let query, let hit):
            jump(section: p.section, sentence: Int(p.sentence))
            if let query, !query.isEmpty {
                pager.setHits(query: query, current: hit)
                jumpHits = true
                jumpHitTurns = 0
            } else {
                jumpHits = false
                pager.setHits(query: nil, current: nil)
            }
        case .ask(let q):
            ask(q)
        case .listen:
            if let position = pager.position { player?.play(from: position) }
        case .listenFromStart:
            if let start = detail?.start { player?.play(from: Position(section: start, sentence: 0)) }
        }
    }

    /// Captures the active selection or open highlight so 书伴 can save an answer back as its thought.
    func captureCompanionAnchor() {
        if let open = openMark {
            companionAnchor = CompanionAnchor(markID: open.mark.id, section: open.mark.section, start: open.mark.start, end: open.mark.end, text: open.mark.text)
        } else if let sel = selection {
            let trimmed = sel.text.trimmingCharacters(in: .whitespacesAndNewlines)
            companionAnchor = CompanionAnchor(
                markID: nil,
                section: sel.section,
                start: TextPoint(sentence: UInt32(max(0, sel.start.s)), offset: UInt32(max(0, sel.start.o))),
                end: TextPoint(sentence: UInt32(max(0, sel.end.s)), offset: UInt32(max(0, sel.end.o))),
                text: trimmed
            )
        } else {
            companionAnchor = nil
        }
    }

    func clearCompanionQuote() {
        companionQuote = nil
        companionAnchor = nil
    }

    /// Opens 书伴 with a question.
    func ask(_ question: String) {
        panel = .companion
        if !question.isEmpty {
            companion.ask(question, quote: companionQuote, anchor: companionAnchor)
            clearCompanionQuote()
        }
    }

    /// Writes the position, stops reading aloud. The window is going away.
    func close() {
        tick(closing: true)
        flush()
        player?.close()
        setListening(false)
        searching?.cancel()
        searching = nil
        watching.forEach { $0.cancel() }
        watching.removeAll()
        rebuildObserver.map(NotificationCenter.default.removeObserver)
        castObserver.map(NotificationCenter.default.removeObserver)
        dramaObserver.map(NotificationCenter.default.removeObserver)
        terminationObserver.map(NotificationCenter.default.removeObserver)
        rebuildObserver = nil
        castObserver = nil
        dramaObserver = nil
        terminationObserver = nil
    }

    /// Stops everything without writing progress back (the book was deleted or stripped to record-only).
    func abandon() {
        guard !retired else { return }
        retired = true
        saving?.cancel()
        saving = nil
        saved = nil
        searching?.cancel()
        searching = nil
        player?.close()
        setListening(false)
        watching.forEach { $0.cancel() }
        watching.removeAll()
        rebuildObserver.map(NotificationCenter.default.removeObserver)
        castObserver.map(NotificationCenter.default.removeObserver)
        dramaObserver.map(NotificationCenter.default.removeObserver)
        terminationObserver.map(NotificationCenter.default.removeObserver)
        rebuildObserver = nil
        castObserver = nil
        dramaObserver = nil
        terminationObserver = nil
    }

    deinit {
        saving?.cancel()
        searching?.cancel()
        watching.forEach { $0.cancel() }
        rebuildObserver.map(NotificationCenter.default.removeObserver)
        castObserver.map(NotificationCenter.default.removeObserver)
        dramaObserver.map(NotificationCenter.default.removeObserver)
        terminationObserver.map(NotificationCenter.default.removeObserver)
    }

    private static func doubtKey(_ bookID: String) -> String { "txtDoubtDismissed.\(bookID)" }

    /// 不用了 on the hint: it doesn't come back for the same doubt.
    func dismissDoubt() {
        if let txtDoubt { UserDefaults.standard.set(txtDoubt, forKey: Self.doubtKey(bookID)) }
        txtDoubt = nil
    }

    /// Before the book is built again (编码和目录): where the reader is gets written in the old
    /// chapters, so the core can carry it over, and nothing is written after that.
    func retire() {
        guard !retired else { return }
        close()
        retired = true
    }

    // MARK: Moving around

    func turn(forward: Bool) {
        turnedByHand()
        pager.turn(forward: forward)
    }

    func go(to item: TocItem) {
        leaving()
        turnedByHand()
        pager.show(section: item.section, target: item.anchor.map { .anchor($0) } ?? .start, animated: false)
    }

    func goToChapter(forward: Bool) {
        guard let section else { return }
        let next: UInt32?
        if forward {
            next = try? core.nextSection(bookId: bookID, section: section, speech: false)
        } else {
            next = try? core.previousSection(bookId: bookID, section: section, speech: false)
        }
        guard let next else { return }
        turnedByHand()
        pager.show(section: next, target: .start, animated: false)
    }

    /// 跳过去: to where the other device stopped; the voice goes along.
    func jumpElsewhere() {
        guard let e = elsewhere else { return }
        elsewhere = nil
        turnedByHand()
        opened = true
        let p = e.progress.position
        pager.show(section: p.section, target: .sentence(Int(p.sentence), align: .third), animated: false)
        if isPlaying { player?.play(from: p) }
    }

    /// About to jump: the place to come back to, unless one is kept already.
    func leaving() {
        turnsSinceJump = 0
        guard returnTo == nil, opened, let here = pager.position else { return }
        returnTo = (here, bookPage)
    }

    /// 回到第 N 页.
    func goBack() {
        guard let back = returnTo?.position else { return }
        returnTo = nil
        turnedByHand()
        pager.show(section: back.section, target: .sentence(Int(back.sentence), align: .third), animated: false)
    }

    var chapterTitle: String? {
        section.flatMap { detail?.chapterTitle(for: $0) }
    }

    /// "3 / 12" in paginated mode.
    var pageLabel: String? {
        settings.mode == .paginated && section != nil ? "\(page + 1) / \(pageCount)" : nil
    }

    private func turnedByHand() {
        followFrom = Date().addingTimeInterval(8)
        if player?.state == .paused { pausedOffSpot = true }
        clock.lastTurn = Date()
    }

    private var following: Bool { Date() >= followFrom }

    /// Opens a note in place; false when there's nothing there to show, and the link is followed.
    private func showNote(target: String, marker: ReaderMessage.Note) -> Bool {
        guard let section = pager.current?.section,
              let note = (try? core.footnote(bookId: bookID, fromSection: section, target: target)) ?? nil
        else { return false }
        selection = nil
        openNote = OpenNote(note: note, label: marker.label, before: marker.before, target: target)
        return true
    }

    /// 到注释页, with 回到原处 left behind.
    func goToNotes() {
        guard let open = openNote else { return }
        openNote = nil
        follow(link: open.target)
    }

    private func follow(link target: String) {
        let parts = target.split(separator: "#", maxSplits: 1)
        guard let first = parts.first, let section = UInt32(first) else { return }
        leaving()
        turnedByHand()
        pager.show(section: section, target: parts.count > 1 ? .anchor(String(parts[1]), align: .third) : .start, animated: false)
    }

    private func openExternal(_ url: URL) {
        guard let scheme = url.scheme?.lowercased(), ["http", "https", "mailto"].contains(scheme) else { return }
        #if os(iOS)
        UIApplication.shared.open(url)
        #else
        NSWorkspace.shared.open(url)
        #endif
    }

    // MARK: Reading aloud

    var isPlaying: Bool {
        player?.state == .playing || player?.state == .loading
    }

    /// 从正在念的这句循环到这里.
    func loop(to section: UInt32, _ sentence: Int) {
        player?.loopTo(section: section, sentence: sentence)
        dismissCards()
    }

    /// Pauses; or reads on from what's on screen. Paused on a sentence that is still on the
    /// page, it carries on from there.
    func togglePlay() {
        guard let player else { return }
        switch player.state {
        case .playing, .loading:
            player.pause()
        case .paused where !pausedOffSpot && player.spot.map(isOnScreen) == true:
            followFrom = .distantPast
            pausedOffSpot = false
            player.resume()
        case .paused, .stopped:
            guard let position = pager.position else { return }
            followFrom = .distantPast
            pausedOffSpot = false
            player.play(from: narratedStart(position))
        }
    }

    /// Steps one sentence forward or backward; when paused after manually moving to another
    /// page or scroll position, steps from what is currently on screen.
    func stepSentence(forward: Bool) {
        guard let player, let spot = player.spot else { return }
        let useScreen = player.state == .paused && (pausedOffSpot || !isOnScreen(spot))
        followFrom = .distantPast
        pausedOffSpot = false
        lastSpokenSpot = nil
        if useScreen, let position = pager.position {
            player.skip(forward: forward, from: position)
        } else {
            player.skip(forward: forward)
        }
    }

    var rate: Float {
        get { player?.rate ?? 1 }
        set {
            player?.rate = newValue
            UserDefaults.standard.set(newValue, forKey: Self.rateKey)
        }
    }

    private static let rateKey = "player.rate"

    private func isOnScreen(_ spot: Player.Spot) -> Bool {
        guard let current = pager.current, current.section == spot.section, let layout = current.layout else { return false }
        return settings.mode == .scrolled || layout.page(of: spot.sentence) == current.page
    }

    /// Lets the shelf show 听中 on this book.
    private func setListening(_ playing: Bool) {
        guard let library else { return }
        if playing {
            if library.listening != bookID { library.listening = bookID }
        } else if library.listening == bookID {
            library.listening = nil
        }
    }

    /// 逐词跟读: when each word of the sentence is said, from its start, as [character, ms] pairs —
    /// in the narrator's voice, with the setting on, where the recording's lay-out knows.
    private func wordTimes(_ spot: Player.Spot) -> [Double]? {
        guard let player, player.narrated, player.guide?.settings.wordFollow == true,
              let a = Audiobooks.shared?.audiobook(for: bookID),
              let start = player.recording?.start(section: spot.section, sentence: spot.sentence) else { return nil }
        let key = "\(spot.section).\(spot.sentence)"
        if let cached = wordCache[key] { return cached }
        let units = (try? core.audiobookUnits(bookId: a.id, section: spot.section, sentence: UInt32(spot.sentence))) ?? []
        let list = units.isEmpty ? nil : units.flatMap { [Double($0.at), max(0, ($0.seconds - start) * 1000)] }
        wordCache[key] = list
        return list
    }

    private func reading(_ spot: Player.Spot, durationMs: Double, elapsedMs: Double, playing: Bool) {
        let spotChanged = spot != lastSpokenSpot
        lastSpokenSpot = spot
        if spotChanged || playing { pausedOffSpot = false }
        if playing { sleepHeard() }
        if spot.section == section, sentence != spot.sentence { sentence = spot.sentence }
        let spoken = Position(section: spot.section, sentence: UInt32(max(0, spot.sentence)))
        if spot.section == pager.current?.section { pager.setSpokenPosition(spoken) }
        if playing || opened {
            opened = true
            save(spoken)
            if !playing { flush() }
        }
        showUnread()
        // A chapter the recording is only followed through as a whole: no ink line; the page
        // still keeps up with the sentence it's about at.
        if let player, player.narrated, player.guide?.chapterOnly(spot.section) == true {
            pager.clearInk()
            followSpoken(spot, spotChanged: spotChanged)
            return
        }
        pager.setInk(section: spot.section, sentence: spot.sentence, durationMs: durationMs, elapsedMs: elapsedMs, playing: playing, words: wordTimes(spot))
        followSpoken(spot, spotChanged: spotChanged)
    }

    /// Brings `spot` into view when following the voice: crosses chapters, turns pages in paginated
    /// mode, and scrolls to the sentence in scrolled mode.
    private func followSpoken(_ spot: Player.Spot, spotChanged: Bool) {
        guard following else { return }
        if spot.section != pager.current?.section {
            pager.show(section: spot.section, target: .sentence(spot.sentence, align: .third), animated: !pager.reduceMotion)
        } else if settings.mode == .scrolled, spotChanged, let current = pager.current {
            let section = spot.section
            let target = spot.sentence
            let animated = !pager.reduceMotion
            Task { [weak self] in
                guard self?.pager.current === current, current.section == section else { return }
                await current.go(to: .sentence(target, align: .third), animated: animated)
            }
        } else if settings.mode == .paginated, let current = pager.current, let layout = current.layout {
            let targetPage = layout.page(of: spot.sentence)
            if targetPage != current.page {
                pager.turn(to: targetPage, animated: !pager.reduceMotion)
            }
        }
    }

    // MARK: Settings

    func update(_ change: (inout ReaderSettings) -> Void) {
        var new = settings
        change(&new)
        guard new != settings else { return }
        settings = new
        do {
            if ownSettings {
                try core.setBookReaderSettings(bookId: bookID, settings: new)
            } else {
                try core.setReaderSettings(settings: new)
            }
        } catch {
            failure = Library.message(for: error)
        }
        applyToPager(new)
    }

    /// 只用于这本书: the book keeps its own settings from now on, or goes back to the shared ones.
    /// 简繁: this book shown in the other script, or (nil) as it is. Synced with the book.
    var script: Script? {
        get { scriptValue }
        set {
            guard newValue != scriptValue else { return }
            scriptValue = newValue
            do {
                try core.setBookScript(bookId: bookID, script: newValue)
            } catch {
                failure = Library.message(for: error)
            }
            // The pages, the voice, the chapter titles and the search all go through it.
            detail = try? core.book(id: bookID)
            NotificationCenter.default.post(name: .replaceRulesChanged, object: bookID)
        }
    }
    private var scriptValue: Script?

    /// A Chinese book, which 简繁 can be shown for.
    var isChinese: Bool { detail?.summary.language.hasPrefix("zh") ?? false }

    func setOwnSettings(_ on: Bool) {
        guard on != ownSettings else { return }
        ownSettings = on
        do {
            try core.setBookReaderSettings(bookId: bookID, settings: on ? settings : nil)
        } catch {
            failure = Library.message(for: error)
        }
        if !on, let shared = try? core.readerSettings(), shared != settings {
            settings = shared
            applyToPager(shared)
        }
    }

    /// 设置 · 阅读 changed the shared settings: books without their own follow.
    /// 墨水屏模式 and 纸色跟随外观 are local display overrides and never mutate the synced paper settings.
    private func sharedSettingsChanged() {
        let einkChanged = einkOn != EInk.on || einkGrayscale != EInk.grayscale
        if einkChanged {
            einkOn = EInk.on
            einkGrayscale = EInk.grayscale
        }
        let target = ownSettings ? ((try? core.bookReaderSettings(bookId: bookID)) ?? nil) : (try? core.readerSettings())
        let effective = target ?? settings
        let paperChanged = appliedPaper != PaperAppearance.shared.resolve(effective)
        if let target, target != settings {
            settings = target
            applyToPager(target)
        } else if einkChanged || paperChanged {
            applyToPager(settings)
        }
    }

    private func applyToPager(_ new: ReaderSettings) {
        let resolved = PaperAppearance.shared.resolve(new)
        appliedPaper = resolved
        pager.fontSize = new.fontSize
        pager.mode = new.mode
        let tokens = einkOn ? EInk.paper : resolved.tokens
        pager.background = tokens.platformBackground
        pager.ink = tokens.platformInk
        pager.accent = tokens.platformAccent
        pager.apply(settings: new.script(resolved: resolved))
        applyImmersive()
    }

    /// 调纸 being set: the page and the controls on `draft` until it is done or cancelled (nil).
    func previewPaper(_ draft: CustomPaper?) {
        guard let draft else {
            guard previewing != nil else { return }
            previewing = nil
            applyToPager(settings)
            return
        }
        let tokens = YYPaper(custom: draft)
        previewing = tokens
        pager.background = tokens.platformBackground
        pager.ink = tokens.platformInk
        pager.accent = tokens.platformAccent
        var script = settings.script()
        script["paper"] = paperColors(paper: draft).preset.tokens.id
        script["paperStyle"] = paperStyle(paper: draft)
        script["texture"] = draft.texture.css ?? NSNull()
        pager.apply(settings: script)
    }
    /// The paper 调纸 shows, over the one in the settings.
    private(set) var previewing: YYPaper?
    /// The paper the page and the controls over it are drawn on.
    var paperTokens: YYPaper { einkOn ? EInk.paper : (previewing ?? PaperAppearance.shared.resolve(settings).tokens) }

    // MARK: Progress

    private func save(_ position: Position) {
        guard position != saved, !retired else { return }
        saved = position
        guard saving == nil else { return }
        saving = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(300))
            guard !Task.isCancelled, let self else { return }
            self.saving = nil
            if let latest = self.saved { self.write(latest) }
        }
    }

    /// Writes a position still waiting to be written.
    func flush() {
        guard let saving else { return }
        saving.cancel()
        self.saving = nil
        if let saved { write(saved) }
    }

    private func write(_ position: Position) {
        do {
            try core.saveProgress(bookId: bookID, position: position)
        } catch {
            failure = Library.message(for: error)
        }
        askIfFinished()
    }

    /// 读完了？: at the very end, once a reading, ask rather than mark it 读过 unasked.
    var askingFinished = false

    private func askIfFinished(atEnd: Bool = false) {
        let key = "finishAsked.\(bookID)"
        guard !askingFinished, !UserDefaults.standard.bool(forKey: key),
              let book = library?.book(bookID), book.status != .finished, book.format != .audio else { return }
        if !atEnd {
            guard let fraction = (try? core.latestProgress(bookId: bookID))?.fraction, fraction >= 0.995 else { return }
        }
        UserDefaults.standard.set(true, forKey: key)
        askingFinished = true
    }
}

extension ReaderModel: ReaderPagerDelegate {
    func pagerMoved(_ pager: ReaderPager) {
        guard let current = pager.current, let layout = current.layout else { return }
        if section != current.section {
            tick()
            // A chapter too short to scroll could not bring the capsule back.
            #if os(iOS)
            self.pager(pager, barsAway: false)
            #endif
        }
        section = current.section
        page = current.page
        pageCount = layout.pageCount
        if let position = pager.position {
            if opened { save(position) } else { opened = true; saved = position }
            if let spot = player?.spot, spot.section == current.section, isPlaying || !pausedOffSpot {
                sentence = spot.sentence
            } else {
                sentence = Int(position.sentence)
            }
        }
    }

    func pager(_ pager: ReaderPager, contextAt m: ReaderMessage) {
        contextSentence = m.sentence.flatMap { s in section.map { ($0, s) } }
        guard let s = m.sentence, let section else { contextLine = nil; return }
        locateLine(section: section, sentence: s, offset: m.offset ?? 0, at: CGPoint(x: m.x ?? 0, y: m.y ?? 0))
    }

    /// The quote (a line someone says) at this place, kept for 谁说的.
    private func locateLine(section: UInt32, sentence s: Int, offset o: Int, at point: CGPoint) {
        contextLine = nil
        // 正文模式: a quote right-clicked can be given to someone from the menu (谁说的).
        guard !bubbles, let lines = try? core.sectionBubbles(bookId: bookID, section: section) else { return }
        let here = { (l: BubbleLine) -> Bool in
            let afterStart = UInt32(s) > l.first || (UInt32(s) == l.first && UInt32(o) >= l.lineStart)
            let beforeEnd = UInt32(s) < l.last || (UInt32(s) == l.last && UInt32(o) < l.end)
            return afterStart && beforeEnd
        }
        if let l = lines.first(where: here) {
            contextLine = (section, l, point)
        }
    }

    /// 谁说的: the card for the line right-clicked, where it was.
    func pickSpeakerFromMenu() {
        guard let c = contextLine else { return }
        if selection != nil { pager.clearSelection() }
        selection = nil
        pager(pager, bubble: c.line, section: c.section, choice: nil, at: c.at)
    }

    func pager(_ pager: ReaderPager, bubble line: BubbleLine, section: UInt32, choice: String?, at point: CGPoint) {
        selection = nil
        openMark = nil
        if let choice, !choice.isEmpty {
            // A name the unsure bubble offered: settled where it was tapped.
            setSpeaker(.person(name: choice), section: section, line: line)
            return
        }
        let core = core, id = bookID
        let pick = SpeakerPick(section: section, line: line, at: point, people: [])
        if choice != nil {
            // 别人…: straight to the whole cast.
            speakerOthers = pick
            return
        }
        speakerPick = pick
        Task {
            let people = await Self.loadLinePeople(core, bookID: id, section: section, sentence: line.first)
            if speakerPick?.line == line { speakerPick?.people = people }
        }
    }

    #if DEBUG
    /// `-debugSpeakerPick 1`: the card for the first bubble in the section, as if its avatar was clicked.
    func debugSpeakerPick() {
        guard let section, let line = (try? core.sectionBubbles(bookId: bookID, section: section))?.first(where: { $0.who != nil }) else { return }
        pager(pager, bubble: line, section: section, choice: nil, at: CGPoint(x: 260, y: 360))
    }
    #endif

    /// Who says a line, from the reader: kept (and synced), the bubbles and the voices follow.
    func setSpeaker(_ choice: UserChoice, section: UInt32, line: BubbleLine) {
        speakerPick = nil
        speakerOthers = nil
        let core = core, id = bookID
        Task {
            await Self.saveSpeaker(core, bookID: id, section: section, sentence: line.first, start: line.lineStart, choice: choice)
            player?.speakersChanged()
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }

    @concurrent nonisolated private static func countOpeningLines(_ core: YuyueCore, bookID: String) async -> Int {
        var count = 0
        for s in UInt32(0) ..< 12 {
            if Task.isCancelled { break }
            count += ((try? core.sectionScript(bookId: bookID, section: s))?.lines.count) ?? 0
            if count >= 6 { break }
        }
        return count
    }

    @concurrent nonisolated private static func loadLinePeople(_ core: YuyueCore, bookID: String, section: UInt32, sentence: UInt32) async -> [CastPick] {
        (try? core.dramaLinePeople(bookId: bookID, section: section, sentence: sentence)) ?? []
    }

    @concurrent nonisolated private static func saveSpeaker(_ core: YuyueCore, bookID: String, section: UInt32, sentence: UInt32, start: UInt32, choice: UserChoice) async {
        _ = try? core.dramaSetSpeaker(bookId: bookID, section: section, sentence: sentence, start: start, choice: choice)
    }

    func pagerTurnedByHand(_ pager: ReaderPager) {
        turnedByHand()
        if openNote != nil { openNote = nil }
        if recap != nil { recap = nil }
        // Read on a few pages where the jump landed: no going back any more.
        if returnTo != nil {
            turnsSinceJump += 1
            if turnsSinceJump >= 3 { returnTo = nil }
        }
        if jumpHits, !searchShown {
            jumpHitTurns += 1
            if jumpHitTurns >= 3 {
                jumpHits = false
                pager.setHits(query: nil, current: nil)
            }
        }
    }

    func pager(_ pager: ReaderPager, tapped m: ReaderMessage) {
        if lookup != nil || sentenceTranslation != nil {
            closeLookup()
            sentenceTranslation = nil
            return
        }
        #if os(iOS)
        if chromeShown {
            if openMark != nil { openMark = nil }
            if openNote != nil { openNote = nil }
            chromeShown = false
            return
        }
        #endif
        if let id = m.mark, let mark = marks.first(where: { $0.id == id }) {
            selection = nil
            if openMark?.mark.id == id {
                openMark = nil
            } else {
                openMark = OpenMark(mark: mark, at: CGPoint(x: m.x ?? 0, y: m.y ?? 0))
            }
            return
        }
        if openMark != nil {
            openMark = nil
            return
        }
        if let word = m.word, isForeign, m.link == nil {
            return look(up: word)
        }
        if openNote != nil, m.link == nil {
            openNote = nil
            return
        }
        if let link = m.link {
            if let target = link.target, let marker = link.note, showNote(target: target, marker: marker) {
                return
            } else if let target = link.target {
                follow(link: target)
            } else if let external = link.external, let url = URL(string: external) {
                openExternal(url)
            }
            return
        }
        // The outer thirds turn pages, the middle brings up the controls.
        let x = m.x ?? 0, width = m.width ?? 0
        if settings.mode == .paginated, width > 0, x < width * 0.3 || x > width * 0.7 {
            return turn(forward: x > width * 0.7)
        }
        #if os(iOS)
        chromeShown = true
        #endif
    }

    func pagerReachedBookEnd(_ pager: ReaderPager) {
        askIfFinished(atEnd: true)
    }

    #if os(iOS)
    func pager(_ pager: ReaderPager, barsAway away: Bool) {
        guard !searchShown, away != barsAway else { return }
        if away {
            chromeComesBack = chromeShown && !immersive
            chromeShown = false
            barsAway = true
        } else {
            barsAway = false
            if chromeComesBack { chromeShown = true }
            chromeComesBack = false
        }
    }
    #endif

    func pager(_ pager: ReaderPager, selected m: ReaderMessage) {
        guard let section = pager.current?.section, let start = m.start, let end = m.end,
              let text = m.text, let r = m.rect
        else {
            selection = nil
            return
        }
        openMark = nil
        #if os(iOS)
        // 正文模式 on the phone: words chosen inside a quote offer 谁说的 on the card.
        locateLine(section: section, sentence: start.s, offset: start.o, at: CGPoint(x: r.x + r.width / 2, y: r.y + r.height))
        #endif
        selection = TextSelection(section: section, start: start, end: end, text: text,
                                  rect: CGRect(x: r.x, y: r.y, width: r.width, height: r.height))
    }

    func pager(_ pager: ReaderPager, inkReachedPage page: Int) {
        guard following, settings.mode == .paginated, page != pager.current?.page else { return }
        pager.turn(to: page, animated: !pager.reduceMotion)
    }
}

extension Notification.Name {
    /// The shared reader settings changed in 设置 · 阅读.
    static let readerSettingsChanged = Notification.Name("yuyue.readerSettingsChanged")
    /// `Library.open(_:at:)`: the book's id.
    static let readerJump = Notification.Name("yuyue.readerJump")
    /// A highlight or thought was added, changed or removed in a book window: the book's id.
    static let notesChanged = Notification.Name("yuyue.notesChanged")
    /// 替换规则 changed: the book's id, or nil for the rules for every book.
    static let replaceRulesChanged = Notification.Name("yuyue.replaceRulesChanged")
    /// A TXT is about to be read another way (编码和目录): open readers write where they are and stop.
    static let bookWillRebuild = Notification.Name("yuyue.bookWillRebuild")
    /// …and has been: its book id. Readers open it again.
    static let bookRebuilt = Notification.Name("yuyue.bookRebuilt")
}

extension BookDetail {
    /// The chapter a section belongs to, as the table of contents names it.
    nonisolated func chapterTitle(for section: UInt32) -> String? {
        if let item = toc.first(where: { $0.section == section }) ?? toc.last(where: { $0.section < section }) {
            return item.title
        }
        let i = Int(section)
        return sections.indices.contains(i) ? sections[i].title : nil
    }
}

extension ReaderSettings {
    static let standard = ReaderSettings(
        paper: .plain, mode: .paginated, font: nil, fontSize: YYReader.defaultFontSize,
        lineHeight: nil, paragraphSpacing: nil, justify: nil, indent: nil, compress: true, customPaper: nil
    )

    /// As `yy.applySettings` takes them (reader contract §4). nil means the paper's default.
    func script(resolved: PaperAppearance.Resolved? = nil) -> [String: Any] {
        func js(_ value: Any?) -> Any { value ?? NSNull() }
        let r = resolved ?? PaperAppearance.shared.resolve(self)
        return [
            "paper": r.preset.tokens.id,
            "paperStyle": js(r.custom.map { paperStyle(paper: $0) }),
            "texture": js(r.custom?.texture.css),
            "mode": mode == .paginated ? "paginated" : "scrolled",
            "font": js(cssFont),
            "fontSize": fontSize,
            "lineHeight": js(lineHeight),
            "paragraphSpacing": js(paragraphSpacing),
            "justify": js(justify),
            "indent": js(indent),
            "compress": compress,
        ]
    }

    /// Matches `document.rs` + `fonts.rs`: `fontFace` CSS family first (when set), followed by the
    /// built-in fallback stack, or the built-in keyword alone.
    var cssFont: String? {
        if let raw = fontFace, let family = Self.cssFamily(for: raw) {
            let fallback = switch font {
            case .sans?: "sans"
            case .serif?: "serif"
            case .kai?: "kaiti"
            case .wenKai?: "wenkai"
            case nil: "p-font"
            }
            return "\(family), var(--yy-\(fallback))"
        }
        return font?.name
    }

    private static func cssFamily(for face: String) -> String? {
        func quote(_ s: String) -> String {
            let cleaned = s.filter { !["\"", "\\", "<", ">", ";", "{", "}"].contains($0) }
            return "\"\(cleaned)\""
        }
        if face.hasPrefix("local:") {
            let family = face.dropFirst(6).trimmingCharacters(in: .whitespaces)
            return family.isEmpty ? nil : quote(family)
        }
        if face.hasPrefix("file:") {
            let name = String(face.dropFirst(5))
            guard !name.isEmpty, !name.contains(where: { ["/", "\\", "\"", "'"].contains($0) }),
                  !name.hasPrefix("."), FontShelf.isFontFile(name) else { return nil }
            return quote("yy-\(face)")
        }
        if FontShelf.shared.catalog.contains(where: { $0.id == face && $0.id != "wenkai" }) {
            return quote("yy-\(face)")
        }
        return nil
    }
}

extension ReaderFont {
    var name: String {
        switch self {
        case .sans: "sans"
        case .serif: "serif"
        case .kai: "kai"
        case .wenKai: "wenkai"
        }
    }
}

extension Paper {
    var tokens: YYPaper {
        switch self {
        case .plain: .plain
        case .dark: .dark
        case .kami: .kami
        case .kamiNight: .kaminight
        case .sage: .sage
        case .eink: .eink
        }
    }

    init?(id: String) {
        switch id {
        case "plain": self = .plain
        case "dark": self = .dark
        case "kami": self = .kami
        case "kaminight": self = .kamiNight
        case "sage": self = .sage
        case "eink": self = .eink
        default: return nil
        }
    }
}

extension YYPaper {
    var platformBackground: PlatformColor { PlatformColor(bg) }
    var platformInk: PlatformColor { PlatformColor(ink) }
    var platformAccent: PlatformColor { PlatformColor(accent) }
}

/// 沉浸阅读: what goes when only the words should stay. Each can be kept.
/// 墨水屏模式 (我的 › 墨水屏): local device display adaptation — pure white/black high-contrast
/// page with no grain, marks as black lines, optional grayscale for covers/illustrations,
/// zero animation, no shadows or glass. Never mutates the synced paper settings.
enum EInk {
    static let key = "eink.on"
    static let grayscaleKey = "eink.grayscale"
    static var on: Bool { UserDefaults.standard.bool(forKey: key) }
    static var grayscale: Bool { UserDefaults.standard.object(forKey: grayscaleKey) as? Bool ?? true }
    static var grayscaleImages: Bool { on && grayscale }

    /// High-contrast pure white/black surface for real e-ink displays (no grey background or grain).
    static let paper = YYPaper(
        id: "eink-mode", name: String(localized: "墨水屏"), isDark: false, hasNoiseTexture: false,
        bg: Color(yy: YYRGBA(255, 255, 255)), ink: Color(yy: YYRGBA(0, 0, 0)), ink2: Color(yy: YYRGBA(68, 68, 68)),
        line: Color(yy: YYRGBA(204, 204, 204)), accent: Color(yy: YYRGBA(0, 0, 0)),
        hl: Color(yy: YYRGBA(0, 0, 0, 0)), wave: Color(yy: YYRGBA(0, 0, 0)), glass: Color(yy: YYRGBA(255, 255, 255)),
        main: Color(yy: YYRGBA(0, 0, 0)), onMain: Color(yy: YYRGBA(255, 255, 255)),
        paperAccent: Color(yy: YYRGBA(0, 0, 0)),
        text: YYPaper.eink.text, title: YYPaper.eink.title, meta: YYPaper.eink.meta
    )

    static func set(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: key)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }

    static func setGrayscale(_ on: Bool) {
        UserDefaults.standard.set(on, forKey: grayscaleKey)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }
}

enum ImmersivePrefs {
    static let lineKey = "immersive.line"
    static let marksKey = "immersive.hideMarks"
    static let stillKey = "immersive.stillPages"
    static let statusKey = "immersive.hideStatus"
    static let progressKey = "immersive.hideProgress"
    static let quietKey = "immersive.quiet"
    static let hintsKey = "immersive.hideHints"
    static let alwaysKey = "immersive.always"

    private static func flag(_ key: String, _ otherwise: Bool = true) -> Bool { UserDefaults.standard.object(forKey: key) as? Bool ?? otherwise }
    /// A hairline at the very bottom for how far through the book.
    static var keepsLine: Bool { flag(lineKey) }
    /// Off at first: a highlight is the reader's own and stays unless asked.
    static var hidesMarks: Bool { flag(marksKey, false) }
    static var stillPages: Bool { flag(stillKey) }
    static var hidesStatusBar: Bool { flag(statusKey) }
    /// Off, a line of where you are and how long the chapter has left stays at the bottom.
    static var hidesProgress: Bool { flag(progressKey) }
    /// 难词注释 and such: nothing on the page but the book's own words.
    static var hidesHints: Bool { flag(hintsKey) }
    /// 读书时通知不打扰: iOS lets no app turn on 勿扰 itself, so this asks the system's 专注模式.
    static var quiet: Bool { flag(quietKey) }
    /// 我的 › 沉浸阅读: every book opens in it.
    static var always: Bool { flag(alwaysKey, false) }
}
