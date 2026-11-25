import WebKit
import YuyueCore
#if os(iOS)
import UIKit
typealias PlatformView = UIView
#else
import AppKit
import QuartzCore
typealias PlatformView = NSView
#endif

protocol ReaderPagerDelegate: AnyObject {
    /// A right-click on the text, before its menu opens.
    func pager(_ pager: ReaderPager, contextAt message: ReaderMessage)
    /// The visible page changed, or the section was laid out again.
    func pagerMoved(_ pager: ReaderPager)
    /// The reader started turning pages themselves (a swipe, a key).
    func pagerTurnedByHand(_ pager: ReaderPager)
    func pager(_ pager: ReaderPager, tapped message: ReaderMessage)
    /// The ink line started on, or moved on to, `page` of the current section.
    func pager(_ pager: ReaderPager, inkReachedPage page: Int)
    /// Text was selected on the page, or the selection went away (`text` nil).
    func pager(_ pager: ReaderPager, selected message: ReaderMessage)
    /// A double click on `sentence`.
    func pager(_ pager: ReaderPager, doubleTapped message: ReaderMessage)
    /// 气泡: a bubble's avatar or name was clicked (`choice` nil), or a name it offers ('' for 别人…).
    func pager(_ pager: ReaderPager, bubble line: BubbleLine, section: UInt32, choice: String?, at point: CGPoint)
    /// Scrolled past the end of the book's last chapter.
    func pagerReachedBookEnd(_ pager: ReaderPager)
    #if os(iOS)
    /// Scrolling: the reader moved on down the page (controls away), back up it, or reached an end of it.
    func pager(_ pager: ReaderPager, barsAway away: Bool)
    #endif
}

/// Shows the book one section at a time and turns its pages (reader contract: native owns
/// everything that moves). The current section's neighbours are kept loaded on both sides, so
/// turning into the next chapter is as quick as turning a page.
///
/// iPhone and iPad: each section is a WebView whose own scroll view pages through it; those
/// sit side by side in a paging scroll view. A swipe past a section's last page is handed to
/// the outer scroll view without lifting the finger, so chapters join like pages.
/// Mac: CSS scroll snapping inside the WebView; keys, clicks and the toolbar turn pages.
final class ReaderPager: PlatformView {
    weak var delegate: ReaderPagerDelegate?
    let core: YuyueCore
    let bookID: String
    /// Used for the line length, which decides the side margins.
    var fontSize: Double = 18
    var mode: LayoutMode {
        didSet { if mode != oldValue { modeChanged() } }
    }
    var background: PlatformColor {
        didSet {
            slots.forEach { $0.setBackground(background) }
            updatePullCueColors()
        }
    }
    /// Ink & accent colours from the active paper, used for the scrolled chapter-pull cue.
    var ink: PlatformColor = {
        #if os(iOS)
        return .label
        #else
        return .labelColor
        #endif
    }() {
        didSet { updatePullCueColors() }
    }
    var accent: PlatformColor = {
        #if os(iOS)
        return .tintColor
        #else
        return .controlAccentColor
        #endif
    }() {
        didSet { updatePullCueColors() }
    }
    /// Optional chapter title lookup for the pull-to-cross cue.
    var chapterTitle: ((UInt32) -> String?)?

    enum PullEdge: Equatable, Sendable { case previous, next }
    nonisolated static let pullThreshold: CGFloat = 64
    nonisolated static let pullHysteresis: CGFloat = 8

    /// Overscroll past a scrolled section's vertical edges (works for short sections where maxY == 0).
    nonisolated static func verticalOverscroll(y: CGFloat, maxY: CGFloat) -> (edge: PullEdge, amount: CGFloat)? {
        let clampedMax = max(0, maxY)
        if y > clampedMax + 0.5 { return (.next, y - clampedMax) }
        if y < -0.5 { return (.previous, -y) }
        return nil
    }

    /// Hysteretic arming so small thumb jitter around the threshold does not double-fire haptics.
    nonisolated static func isPullArmed(amount: CGFloat, wasArmed: Bool, threshold: CGFloat = pullThreshold) -> Bool {
        wasArmed ? amount >= threshold - pullHysteresis : amount >= threshold
    }

    private let messages = MessageProxy()
    private var schemeHandler: SchemeHandler!
    private var configuration: WKWebViewConfiguration!
    /// Loaded sections in reading order: the current one and its neighbours.
    private var slots: [SectionPage] = []
    private(set) var current: SectionPage? {
        didSet { if translating, let current, current !== oldValue, current.section != nil { translate(current) } }
    }
    private var spare: [SectionPage] = []
    private var pendingOpen: (section: UInt32, target: SectionPage.Target)?
    private var appliedGeometry: PageGeometry?
    private var verticalTransitioning = false

    #if os(iOS)
    private let outer = SectionScrollView()
    private let pullCue = PullCueView()
    /// Whether the bottom floating capsule is currently visible (used to position the bottom pull cue clear of it).
    var bottomCapsuleVisible = true
    /// Directions the outer scroll view may move during the current drag.
    private var outerMayMove: (forward: Bool, backward: Bool) = (false, false)
    /// Scrolled vertical pull-to-cross state.
    private var pullEdge: PullEdge?
    private var pullArmed = false
    private let pullHaptic = UIImpactFeedbackGenerator(style: .light)
    /// Scrolling: where the page was last, and how far the hand has moved it one way.
    private var barsLastY: CGFloat = 0
    private var barsTravel: CGFloat = 0
    private var arranging = false
    /// Paginated mode temporarily locks scrolling while a text selection is active.
    private var holding = false
    /// Cover page-turn gesture & overlay state (iOS).
    private lazy var coverPan = UIPanGestureRecognizer(target: self, action: #selector(handleCoverPan(_:)))
    private var coverSnapshot: UIView?
    private let coverShadow: UIView = {
        let v = UIView()
        v.isUserInteractionEnabled = false
        v.isHidden = true
        let g = CAGradientLayer()
        g.colors = [
            UIColor.black.withAlphaComponent(0.22).cgColor,
            UIColor.black.withAlphaComponent(0.08).cgColor,
            UIColor.clear.cgColor,
        ]
        g.startPoint = CGPoint(x: 0, y: 0.5)
        g.endPoint = CGPoint(x: 1, y: 0.5)
        v.layer.addSublayer(g)
        return v
    }()
    private var coverActive = false
    private var coverForward = true
    private var coverFromPage = 0
    private var coverFromSlot = 0
    private var coverTargetPage = 0
    private var coverTargetSlot = 0
    private var coverProgress: CGFloat = 0
    private var coverAnimating = false
    #else
    private let pullCue = MacPullCueView()
    private struct MacPullState {
        var session = 0
        var checked = false
        var mayPrev = false
        var mayNext = false
        var edge: PullEdge?
        var accum: CGFloat = 0
        var armed = false
    }
    private var macPull = MacPullState()
    private var macLastWheelY: CGFloat = -1
    private var macLastWheelMovedAt: TimeInterval = 0
    private var macLastWheelEdgeAt: TimeInterval = 0
    private var macLastCrossAt: TimeInterval = 0
    private var macWheelEdge: PullEdge?
    private var macWheelAccum: CGFloat = 0
    private var macCueHideTask: Task<Void, Never>?
    #endif

    init(core: YuyueCore, bookID: String, mode: LayoutMode, background: PlatformColor) {
        self.core = core
        self.bookID = bookID
        self.mode = mode
        self.background = background
        super.init(frame: .zero)
        messages.receiver = self
        schemeHandler = SchemeHandler(core: core, bookID: bookID) { [weak self] in
            self?.geometry() ?? PageGeometry(paging: .snap, margin: 24, top: 56, bottom: 88, columns: 1, reducedMotion: false)
        }
        configuration = .reader(schemeHandler: schemeHandler, messages: messages)
        #if os(iOS)
        outer.isPagingEnabled = true
        outer.showsHorizontalScrollIndicator = false
        outer.showsVerticalScrollIndicator = false
        outer.contentInsetAdjustmentBehavior = .never
        outer.delaysContentTouches = false
        outer.alwaysBounceHorizontal = true
        outer.alwaysBounceVertical = false
        outer.scrollsToTop = false
        outer.decelerationRate = .fast
        outer.delegate = self
        addSubview(outer)
        coverPan.delegate = self
        addGestureRecognizer(coverPan)
        addSubview(coverShadow)
        pullCue.isUserInteractionEnabled = false
        pullCue.alpha = 0
        addSubview(pullCue)
        syncScrollMode()
        #else
        wantsLayer = true
        pullCue.alphaValue = 0
        pullCue.isHidden = true
        addSubview(pullCue)
        #endif
        updatePullCueColors()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    #if os(iOS)
    private static var webKitPrewarmed = false
    private static var warmWebView: WKWebView?

    /// Spawns WebKit's shared XPC WebContent process shortly after app launch while idle,
    /// eliminating the 250–400 ms cold-process delay when opening the first book.
    static func prewarmWebKitIfNeeded() {
        guard !webKitPrewarmed else { return }
        webKitPrewarmed = true
        let wv = WKWebView(frame: .zero, configuration: WKWebViewConfiguration())
        wv.loadHTMLString("<!doctype html><meta charset=utf-8>", baseURL: nil)
        warmWebView = wv
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(4))
            warmWebView = nil
        }
    }
    #endif

    // MARK: Opening

    func open(section: UInt32, target: SectionPage.Target) {
        guard bounds.width > 0 else {
            pendingOpen = (section, target)
            return
        }
        pendingOpen = nil
        pin(section: section, target: target)
        if let current, current.section == section, current.isReady {
            Task {
                await current.go(to: target, animated: false)
                moved()
            }
            return
        }
        #if os(iOS)
        holdStill(false)
        #endif
        resetVerticalPull()
        for slot in slots { recycle(slot) }
        let page = makePage()
        page.load(bookID: bookID, section: section, target: target)
        slots = [page]
        current = page
        show(page)
        arrange()
    }

    /// The words changed underneath (替换规则): loads the sections again, at the same place.
    func reload() {
        guard let here = position ?? current?.section.map({ Position(section: $0, sentence: 0) }) else { return }
        current = nil
        open(section: here.section, target: .sentence(Int(here.sentence)))
    }

    /// Keeps the sections on either side of the current one loaded, and lets the others go.
    private func loadNeighbours() {
        guard let current, let section = current.section else { return }
        var wanted: [SectionPage] = []
        if let previous = try? core.previousSection(bookId: bookID, section: section, speech: false) {
            wanted.append(page(for: previous, target: .end))
        }
        wanted.append(current)
        if let next = try? core.nextSection(bookId: bookID, section: section, speech: false) {
            wanted.append(page(for: next, target: .start))
        }
        for slot in slots where !wanted.contains(where: { $0 === slot }) { recycle(slot) }
        slots = wanted
        arrange()
        // The section just left keeps the page it was on; turning back into it starts at its edge.
        for slot in slots { join(slot) }
    }

    /// Puts a neighbour on the page that joins the current section: its last page when it comes
    /// before, its first when it comes after. Turning or swiping into it then arrives there.
    private func join(_ page: SectionPage) {
        guard page !== current, page.isReady,
              let i = slots.firstIndex(where: { $0 === page }),
              let c = slots.firstIndex(where: { $0 === current })
        else { return }
        Task { await page.go(to: i < c ? .end : .start, animated: false) }
    }

    private func page(for section: UInt32, target: SectionPage.Target) -> SectionPage {
        if let loaded = slots.first(where: { $0.section == section }) { return loaded }
        let page = makePage()
        page.load(bookID: bookID, section: section, target: target)
        return page
    }

    private func makePage() -> SectionPage {
        if let page = spare.popLast() {
            page.setBackground(background)
            #if os(iOS)
            configure(page.webView.scrollView)
            #endif
            return page
        }
        let page = SectionPage(configuration: configuration, background: background)
        #if os(iOS)
        page.webView.scrollView.delegate = self
        configure(page.webView.scrollView)
        outer.addSubview(page.webView)
        #else
        page.webView.onKey = { [weak self] event in self?.key(event) ?? false }
        page.webView.onScrollWheel = { [weak self] event in self?.handleScrollWheel(event) }
        page.webView.menuItems = { [weak self] in self?.menuItems?() ?? [] }
        page.webView.isHidden = true
        // Hidden neighbours are laid out at the pager's size too, so their pages are the real ones.
        page.webView.frame = bounds
        page.webView.autoresizingMask = [.width, .height]
        addSubview(page.webView, positioned: .below, relativeTo: pullCue)
        #endif
        return page
    }

    private func recycle(_ page: SectionPage) {
        page.clear()
        #if os(iOS)
        page.webView.transform = .identity
        page.webView.scrollView.isScrollEnabled = true
        #else
        page.webView.isHidden = true
        #endif
        if !spare.contains(where: { $0 === page }) { spare.append(page) }
    }

    // MARK: The reader's messages

    func received(_ m: ReaderMessage, from webView: WKWebView, frame: WKFrameInfo) {
        guard let page = slots.first(where: { $0.webView === webView }), page.sentByCurrentDocument(frame) else { return }
        switch m.type {
        case .ready, .layout:
            if m.type == .ready {
                page.call("yy.setUi(u)", ["u": AppLanguage.running == .en ? "en" : "zh"])
                // First: it rebuilds the section's boxes, the rest are drawn on them.
                if bubbles { sendBubbles(to: page) }
                sendMarks(to: page)
                sendHits(to: page)
                sendSource(to: page)
                sendForeign(to: page)
            }
            page.take(m)
            Task {
                let arrived = await page.settle()
                if arrived {
                    sendInk(to: page)
                } else {
                    // Laid out again, a neighbour may have moved off its joining page.
                    join(page)
                }
                // Arriving at the target is not a move: a position the pager was opened at stays,
                // even if the page it lands on reads as an earlier one (a window still taking its
                // size); and only a turn writes over where the book was left.
                if page === current {
                    moved(relaidOut: true)
                    if slots.count == 1 {
                        #if os(iOS)
                        if !outer.isDragging, !outer.isDecelerating { loadNeighbours() }
                        #else
                        loadNeighbours()
                        #endif
                    }
                }
            }
        case .tap:
            if page === current { delegate?.pager(self, tapped: m) }
        case .doubleTap:
            if page === current { delegate?.pager(self, doubleTapped: m) }
        case .context:
            if page === current { delegate?.pager(self, contextAt: m) }
        case .activePage:
            if page === current, let p = m.page { delegate?.pager(self, inkReachedPage: p) }
        case .page:
            // A swipe on the trackpad; or, while settling, the page a new layout ended on.
            if let p = m.page {
                page.page = p
                if page === current { moved(relaidOut: page.settling) }
            }
        case .scroll:
            // Kept for a neighbour too: it may have been scrolled to its target before it comes in.
            page.scrolledTo = m.sentence
            if page === current { moved() }
        case .selection:
            if page === current {
                #if os(iOS)
                holdStill(m.start != nil)
                #endif
                delegate?.pager(self, selected: m)
            }
        case .bubble:
            if page === current, let section = page.section, let i = m.line, let lines = bubbleLines[section], lines.indices.contains(i) {
                delegate?.pager(self, bubble: lines[i], section: section, choice: m.choice, at: CGPoint(x: m.x ?? 0, y: m.y ?? 0))
            }
        case .log:
            break
        }
    }

    /// The reading position: where the reader last moved to. Only moves change it. A new layout
    /// (a resize, another type size) keeps the same text in view under new page breaks, and the
    /// page it ends on can start well before this sentence; taking that page's first sentence
    /// instead would move the position back a little with every layout, and a book reopened at
    /// another size a page or more before where it was left.
    private(set) var position: Position?

    private func moved(relaidOut: Bool = false) {
        if !relaidOut || position?.section != current?.section, let here = onScreen { position = here }
        delegate?.pagerMoved(self)
    }

    /// A jump to a sentence makes it the position as long as its page is on screen: it is where
    /// the reader left off, even when the page it lands on now starts earlier. Any other jump
    /// takes the position from where it lands.
    private func pin(section: UInt32, target: SectionPage.Target) {
        if case .sentence(let s, _) = target {
            position = Position(section: section, sentence: UInt32(max(0, s)))
        } else {
            position = nil
        }
    }

    /// Keeps `position` in step with the voice while reading aloud on the current page.
    func setSpokenPosition(_ spoken: Position) {
        position = spoken
    }

    /// The first sentence on the page, or the first in view; in pages, the position while its page is
    /// still the one on screen.
    private var onScreen: Position? {
        guard let current, let section = current.section, let layout = current.layout else { return nil }
        if mode == .scrolled {
            return Position(section: section, sentence: UInt32(max(0, current.scrolledTo ?? layout.firstSentence(on: current.page))))
        }
        if let position, position.section == section, layout.page(of: Int(position.sentence)) == current.page { return position }
        return Position(section: section, sentence: UInt32(max(0, layout.firstSentence(on: current.page))))
    }

    // MARK: Turning pages

    /// 沉浸阅读 can turn whole pages without sliding.
    var calm = false {
        didSet {
            #if os(iOS)
            if calm != oldValue { syncScrollMode() }
            #endif
        }
    }
    /// Horizontal pagination animation: slide, cover (with edge shadow), or none (instant).
    var pageTurn: PageTurn = PageTurnPrefs.current {
        didSet {
            #if os(iOS)
            if pageTurn != oldValue {
                cancelCover()
                syncScrollMode()
            }
            #endif
        }
    }
    var isReadingAloud = false

    var reduceMotion: Bool {
        #if os(iOS)
        calm || pageTurn == .none || UIAccessibility.isReduceMotionEnabled
        #else
        calm || pageTurn == .none || NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        #endif
    }

    #if os(iOS)
    private var useCover: Bool {
        !calm && mode == .paginated && pageTurn == .cover && !UIAccessibility.isReduceMotionEnabled
    }

    private func syncScrollMode() {
        let enableNativeScroll = mode != .paginated || (!holding && !useCover)
        outer.isScrollEnabled = mode == .paginated && !holding && !useCover
        for page in slots + spare {
            page.webView.scrollView.isScrollEnabled = enableNativeScroll
        }
        coverPan.isEnabled = useCover && !holding
    }
    #endif

    /// Shows `section` with `target` in view: a turn when it is a neighbour, a jump otherwise.
    func show(section: UInt32, target: SectionPage.Target, animated: Bool) {
        guard let current else { return open(section: section, target: target) }
        if current.section == section {
            // A page turn: the position becomes the page's, as after any other.
            if case .sentence(let s, _) = target, let layout = current.layout {
                turn(to: layout.page(of: s), animated: animated)
            } else {
                Task {
                    await current.go(to: target, animated: false)
                    moved()
                }
            }
            return
        }
        guard let i = slots.firstIndex(where: { $0 === current }),
              let j = slots.firstIndex(where: { $0.section == section }), abs(i - j) == 1, slots[j].isReady
        else { return open(section: section, target: target) }
        let neighbour = slots[j]
        Task {
            await neighbour.go(to: target, animated: false)
            // The slots can have changed meanwhile.
            guard neighbour.section == section, let j = slots.firstIndex(where: { $0 === neighbour }) else {
                return open(section: section, target: target)
            }
            pin(section: section, target: target)
            move(toSlot: j, animated: animated)
        }
    }

    /// Turns one page; at a section's first or last page, into the neighbouring section.
    func turn(forward: Bool) {
        guard let current, let layout = current.layout else { return }
        let animated = !reduceMotion
        #if os(iOS)
        if mode == .scrolled {
            let scroll = current.webView.scrollView
            let step = scroll.bounds.height * 0.85
            let maxY = max(0, scroll.contentSize.height - scroll.bounds.height)
            let y = scroll.contentOffset.y
            if (forward && y < maxY - 1) || (!forward && y > 1) {
                scroll.setContentOffset(CGPoint(x: 0, y: min(maxY, max(0, y + (forward ? step : -step)))), animated: animated)
                return
            }
        } else {
            if useCover {
                if beginCover(forward: forward) {
                    settleCover(commit: true, velocity: 0)
                }
                return
            }
            let target = current.page + (forward ? 1 : -1)
            if target >= 0 && target < layout.pageCount {
                turn(to: target, animated: animated)
                playManualTurnSound()
                return
            }
        }
        guard let i = slots.firstIndex(where: { $0 === current }) else { return }
        let j = forward ? i + 1 : i - 1
        guard slots.indices.contains(j) else {
            if forward && mode == .scrolled { delegate?.pagerReachedBookEnd(self) }
            return
        }
        guard slots[j].isReady else { return }
        move(toSlot: j, animated: animated, snapEdge: true)
        if mode == .paginated { playManualTurnSound() }
        #else
        let generation = current.generation
        let wantCover = mode == .paginated && pageTurn == .cover && !reduceMotion
        Task {
            let call = forward ? "yy.nextPage(a)" : "yy.prevPage(a)"
            // The page it turned to, or -1 at the section's edge.
            guard let landed = await current.ask("\(call) ? yy.currentPage() : -1", ["a": animated && !wantCover]) as? Int,
                  current === self.current, current.generation == generation
            else { return }
            if landed >= 0 {
                if wantCover { applyMacCoverTransition(forward: forward) }
                current.page = min(landed, layout.pageCount - 1)
                playManualTurnSound()
                moved()
                return
            }
            guard let i = slots.firstIndex(where: { $0 === current }) else { return }
            let j = forward ? i + 1 : i - 1
            guard slots.indices.contains(j) else {
                if forward && mode == .scrolled { delegate?.pagerReachedBookEnd(self) }
                return
            }
            guard slots[j].isReady else { return }
            if wantCover { applyMacCoverTransition(forward: forward) }
            move(toSlot: j, animated: animated && !wantCover, snapEdge: true)
            if mode == .paginated { playManualTurnSound() }
        }
        #endif
    }

    private func playManualTurnSound() {
        guard !isReadingAloud else { return }
        PageTurnSound.shared.play()
    }

    #if os(macOS)
    private func applyMacCoverTransition(forward: Bool) {
        let tr = CATransition()
        tr.duration = 0.24
        tr.timingFunction = CAMediaTimingFunction(name: .easeOut)
        tr.type = forward ? .reveal : .moveIn
        tr.subtype = forward ? .fromRight : .fromLeft
        layer?.add(tr, forKey: "yyCoverTurn")
    }
    #endif

    /// Turns to a page of the current section.
    func turn(to page: Int, animated: Bool) {
        guard let current, let layout = current.layout else { return }
        let page = min(max(page, 0), layout.pageCount - 1)
        guard page != current.page else { return }
        #if os(iOS)
        if mode == .paginated {
            current.page = page
            current.webView.scrollView.setContentOffset(CGPoint(x: CGFloat(page) * bounds.width, y: 0), animated: animated)
            if !animated { moved() }
            return
        }
        #endif
        Task {
            await current.go(to: .page(page), animated: animated)
            moved()
        }
    }

    private func move(toSlot j: Int, animated: Bool, snapEdge: Bool = false) {
        #if os(iOS)
        guard let i = slots.firstIndex(where: { $0 === current }) else { return }
        if mode == .scrolled {
            crossVertically(toSlot: j, forward: j > i, pullOffset: 0, animated: animated, snapEdge: snapEdge)
            return
        }
        outerMayMove = (j > i, j < i)
        if animated {
            outer.setContentOffset(CGPoint(x: CGFloat(j) * bounds.width, y: 0), animated: true)
        } else {
            outer.contentOffset = CGPoint(x: CGFloat(j) * bounds.width, y: 0)
            settle()
        }
        #else
        if mode == .scrolled, let i = slots.firstIndex(where: { $0 === current }) {
            crossVerticallyMac(toSlot: j, forward: j > i, animated: animated, snapEdge: snapEdge)
            return
        }
        let page = slots[j]
        current = page
        show(page)
        loadNeighbours()
        moved()
        #endif
    }

    // MARK: The ink line

    private struct Ink {
        var section: UInt32
        var sentence: Int
        var durationMs: Double
        var elapsedMs: Double
        var playing: Bool
        /// 逐词跟读: [character, ms] pairs, laid end to end.
        var words: [Double]?
        var at = Date()
    }

    private var activeInk: Ink?

    /// Draws the ink line under a sentence being read: `elapsedMs` of `durationMs` read so far.
    /// A section loaded later picks it up where it has got to.
    func setInk(section: UInt32, sentence: Int, durationMs: Double, elapsedMs: Double, playing: Bool, words: [Double]? = nil) {
        activeInk = Ink(section: section, sentence: sentence, durationMs: durationMs, elapsedMs: elapsedMs, playing: playing, words: words)
        slots.forEach(sendInk)
    }

    func clearInk() {
        activeInk = nil
        slots.forEach(sendInk)
    }

    private func sendInk(to page: SectionPage) {
        guard let ink = activeInk, page.section == ink.section else { return page.call("yy.setActive(null)") }
        let elapsed = ink.elapsedMs + (ink.playing ? Date().timeIntervalSince(ink.at) * 1000 : 0)
        var options: [String: Any] = ["durationMs": ink.durationMs, "elapsedMs": min(elapsed, ink.durationMs), "playing": ink.playing]
        options["words"] = ink.words ?? NSNull()
        page.call("yy.setActive(s, o)", ["s": ink.sentence, "o": options])
    }

    // MARK: 气泡模式

    /// The book's dialogue as bubbles.
    var bubbles = false {
        didSet { if bubbles != oldValue { slots.forEach(sendBubbles) } }
    }

    /// Someone's side or name changed: draw the bubbles again.
    func bubblesChanged() {
        if bubbles { slots.forEach(sendBubbles) }
    }

    /// The lines last drawn per section: a click on a bubble says which by its place in the list.
    private var bubbleLines: [UInt32: [BubbleLine]] = [:]

    private func sendBubbles(to page: SectionPage) {
        guard let section = page.section else { return }
        guard bubbles else { return page.call("yy.setBubbles(null)") }
        let got = (try? core.sectionBubbles(bookId: bookID, section: section)) ?? []
        bubbleLines[section] = got
        let lines = got.map { l -> [String: Any] in
            ["first": Int(l.first), "last": Int(l.last), "start": Int(l.start), "end": Int(l.end), "who": l.who ?? NSNull(),
             "avatar": l.avatar, "color": l.color, "right": l.right, "choices": l.choices]
        }
        page.call("yy.setBubbles(l)", ["l": lines])
    }

    // MARK: Highlights, search hits, selection

    private var marks: [Mark] = []
    /// 沉浸阅读 can leave the page without highlights for a while; they are only hidden.
    var marksHidden = false {
        didSet { if marksHidden != oldValue { slots.forEach(sendMarks) } }
    }
    /// Marks made since the book was opened: shown even then, or a colour picked would leave
    /// nothing on the page.
    private var madeHere: Set<String> = []
    func keepShown(_ id: String) { madeHere.insert(id) }
    /// …and without 难词注释.
    var glossesHidden = false {
        didSet { if glossesHidden != oldValue { slots.forEach(sendGlosses) } }
    }
    private var hits: (query: String, current: SearchHit?)?

    /// The book's highlights; each section draws its own.
    func setMarks(_ marks: [Mark]) {
        self.marks = marks
        slots.forEach(sendMarks)
    }

    /// Marks where `query` turns up; `current` in the solid colour. nil clears.
    func setHits(query: String?, current: SearchHit?) {
        hits = query.map { ($0, current) }
        slots.forEach(sendHits)
    }

    /// 书伴's 出处: one sentence washed in the accent until something else is pointed at.
    private var source: Position?

    func setSource(_ position: Position?) {
        source = position
        slots.forEach(sendSource)
    }

    private func sendSource(to page: SectionPage) {
        guard let source, page.section == source.section else { return page.call("yy.setSource(null)") }
        page.call("yy.setSource(s)", ["s": Int(source.sentence)])
    }

    private func sendMarks(to page: SectionPage) {
        guard let section = page.section else { return }
        let list: [[String: Any]] = marks.filter { $0.section == section && (!marksHidden || madeHere.contains($0.id)) }.map { m in
            ["id": m.id,
             "start": ["s": Int(m.start.sentence), "o": Int(m.start.offset)],
             "end": ["s": Int(m.end.sentence), "o": Int(m.end.offset)],
             "style": m.style.css,
             "note": m.note != nil]
        }
        page.call("yy.setMarks(m)", ["m": list])
    }

    private func sendHits(to page: SectionPage) {
        guard let hits, page.section != nil else { return page.call("yy.setHits(null)") }
        var current: Any = NSNull()
        if let hit = hits.current, hit.section == page.section {
            current = ["s": Int(hit.sentence), "nth": Int(hit.nth)]
        }
        page.call("yy.setHits(q, c)", ["q": hits.query, "c": current])
    }

    func clearSelection() {
        current?.call("yy.clearSelection()")
        #if os(iOS)
        holdStill(false)
        #endif
    }

    #if os(iOS)
    /// While words are chosen in paginated mode the page stays put: dragging a handle near the
    /// edge would otherwise turn it. Releasing the hold always restores scrolling even if the
    /// layout mode changed while text was selected.
    private func holdStill(_ on: Bool) {
        if on {
            guard mode == .paginated else { return }
            holding = true
            syncScrollMode()
        } else {
            holding = false
            syncScrollMode()
        }
    }
    #endif

    // MARK: 外文原著

    /// Clicks on words look them up (点词).
    var lookup = false {
        didSet { if lookup != oldValue { slots.forEach(sendForeign) } }
    }
    /// 生词: dotted wherever they turn up.
    private var vocab: [String] = []
    /// The word being looked up.
    private var looking: (section: UInt32, start: ReaderMessage.Point, end: ReaderMessage.Point)?
    /// 译: translates whole paragraphs; the answers are kept per section.
    var translator: ((UInt32, [String]) async -> (texts: [String?], lang: String))?
    var onTranslateFailed: (() -> Void)?
    private(set) var translating = false
    private(set) var translationOnly = false
    private var translated: [UInt32: [[String: Any]]] = [:]
    private var inFlight: Set<UInt32> = []

    /// 难词注释: the harder words of a section and their meanings, from the core.
    var glosser: ((UInt32) -> [[String: String]])?
    private var glossed: [UInt32: [[String: String]]] = [:]

    /// The level or the 生词本 changed: work the meanings out again.
    func refreshGlosses() {
        guard glosser != nil else { return }
        glossed = [:]
        slots.forEach(sendGlosses)
    }

    private func sendGlosses(to page: SectionPage) {
        guard let section = page.section, let glosser else { return }
        if glossesHidden { return page.call("yy.setGlosses(null)") }
        let list = glossed[section] ?? glosser(section)
        glossed[section] = list
        page.call("yy.setGlosses(g)", ["g": list])
    }

    func setVocab(_ words: [String]) {
        guard words != vocab else { return }
        vocab = words
        for page in slots where page.section != nil { page.call("yy.setVocab(w)", ["w": words]) }
    }

    func setLooking(section: UInt32, start: ReaderMessage.Point, end: ReaderMessage.Point) {
        looking = (section, start, end)
        slots.forEach(sendLooking)
    }

    func clearLooking() {
        guard looking != nil else { return }
        looking = nil
        slots.forEach(sendLooking)
    }

    /// 有声书: an amber line beside the paragraphs of sentences first…last, for a stretch the
    /// recording doesn't line up with.
    private var flag: (section: UInt32, first: Int, last: Int)?

    func setFlag(section: UInt32, first: Int, last: Int) {
        if let flag, flag == (section, first, last) { return }
        flag = (section, first, last)
        slots.forEach(sendFlag)
    }

    /// 有声书: by section, the sentences the recording doesn't read, shown a shade lighter.
    private var unread: [UInt32: [Int]] = [:]

    func setUnread(_ all: [UInt32: [Int]]) {
        guard all != unread else { return }
        unread = all
        slots.forEach(sendUnread)
    }

    private func sendUnread(to page: SectionPage) {
        guard let section = page.section else { return }
        guard let list = unread[section], !list.isEmpty else { return page.call("yy.setUnread(null)") }
        page.call("yy.setUnread(l, t)", ["l": list, "t": String(localized: "录音里没念")])
    }

    private func sendFlag(to page: SectionPage) {
        guard page.section != nil else { return }
        guard let flag, page.section == flag.section else { return page.call("yy.setFlag(null)") }
        page.call("yy.setFlag(a, b)", ["a": flag.first, "b": flag.last])
    }

    /// 译 on or off, and whether to hide original source paragraphs (仅译文).
    func setTranslating(_ on: Bool, only: Bool = false) {
        if on == translating, only == translationOnly { return }
        let wasOn = translating
        translating = on
        translationOnly = on ? only : false
        if on {
            if wasOn {
                // Already translated on screen: flip CSS mode instantly without re-fetching.
                for page in slots where page.section != nil {
                    page.call("yy.setTranslationOnly(o)", ["o": translationOnly])
                }
                if let current, current.section != nil, translated[current.section!] == nil {
                    translate(current)
                }
            } else if let current, current.section != nil {
                translate(current)
            }
        } else {
            for page in slots where page.section != nil { page.call("yy.setTranslations(null)") }
        }
    }

    func clearTranslatedCache() {
        translated.removeAll()
    }

    private func sendForeign(to page: SectionPage) {
        guard page.section != nil else { return }
        page.call("yy.setLookup(on)", ["on": lookup])
        if !vocab.isEmpty { page.call("yy.setVocab(w)", ["w": vocab]) }
        sendGlosses(to: page)
        sendLooking(to: page)
        sendFlag(to: page)
        sendUnread(to: page)
        if translating, page === current { translate(page) }
    }

    private func sendLooking(to page: SectionPage) {
        guard let looking, page.section == looking.section else { return page.call("yy.setLooking(null, null)") }
        page.call("yy.setLooking(a, b)", ["a": ["s": looking.start.s, "o": looking.start.o], "b": ["s": looking.end.s, "o": looking.end.o]])
    }

    private func translate(_ page: SectionPage) {
        guard let section = page.section else { return }
        if let done = translated[section] { return page.call("yy.setTranslations(t, o)", ["t": done, "o": translationOnly]) }
        guard !inFlight.contains(section) else { return }
        inFlight.insert(section)
        Task {
            defer { inFlight.remove(section) }
            guard let translator, let list = await page.ask("yy.paragraphs()") as? [[String: Any]], !list.isEmpty else { return }
            let texts = list.map { $0["text"] as? String ?? "" }
            let (out, lang) = await translator(section, texts)
            var items: [[String: Any]] = []
            for (p, t) in zip(list, out) {
                if let t, !t.isEmpty, let last = p["last"] { items.append(["last": last, "text": t, "lang": lang]) }
            }
            guard !items.isEmpty else {
                if translating, page === current { onTranslateFailed?() }
                return
            }
            translated[section] = items
            if translating, page.section == section { page.call("yy.setTranslations(t, o)", ["t": items, "o": translationOnly]) }
        }
    }

    /// Selects a highlight's text on the page, to show which one a card is about.
    func select(_ mark: Mark) {
        let start: [String: Any] = ["s": Int(mark.start.sentence), "o": Int(mark.start.offset)]
        let end: [String: Any] = ["s": Int(mark.end.sentence), "o": Int(mark.end.offset)]
        current?.call("yy.select(a, b)", ["a": start, "b": end])
    }

    /// Where a highlight is drawn on the current page, in the pager's coordinates.
    func rect(of mark: Mark) async -> CGRect? {
        guard let current, current.section == mark.section,
              let r = await current.ask("yy.markRect(id)", ["id": mark.id]) as? [String: Any],
              let x = (r["x"] as? NSNumber)?.doubleValue, let y = (r["y"] as? NSNumber)?.doubleValue,
              let w = (r["width"] as? NSNumber)?.doubleValue, let h = (r["height"] as? NSNumber)?.doubleValue
        else { return nil }
        return CGRect(x: x, y: y, width: w, height: h)
    }

    // MARK: Settings and geometry

    /// Sends reader settings (contract §4) to the loaded sections, together with the page
    /// geometry, which may depend on them. Sections loaded later get both from the core.
    func apply(settings: [String: Any]) {
        let g = geometry()
        appliedGeometry = g
        let all = settings.merging(Self.settings(for: g)) { $1 }
        for page in slots { page.call("yy.applySettings(s)", ["s": all]) }
    }

    private static func settings(for g: PageGeometry) -> [String: Any] {
        ["margin": g.margin, "top": g.top, "bottom": g.bottom, "cols": Int(g.columns),
         "paging": g.paging == .native ? "native" : "snap", "motion": g.reducedMotion ? "reduced" : "full",
         "eink": EInk.on, "einkGray": EInk.grayscaleImages]
    }

    /// The page geometry each device decides for itself (`PageGeometry`).
    /// 单栏 / 双栏: two pages side by side when the window is wide enough for them.
    var prefersTwoColumns = true {
        didSet { if prefersTwoColumns != oldValue { geometryChanged() } }
    }
    /// 播客: an episode's script is a chat, one host on the left and one on the right; two
    /// pages side by side would halve each bubble's width and lose that. Always one column.
    var chat = false {
        didSet { if chat != oldValue { geometryChanged() } }
    }

    #if os(iOS)
    /// iPhone Duo's inner display (an iPhone at regular width): pages open as a book, two side by
    /// side with a 64 pt gutter between them, the read-along bar under the right one.
    var spread = false {
        didSet { if spread != oldValue { geometryChanged() } }
    }
    /// Half folded: the fold, in the pager's coordinates. No text runs into it.
    var fold: CGRect? {
        didSet { if fold != oldValue { geometryChanged() } }
    }
    /// The spread's gutter is twice the margin (reader.css: column-gap = 2 × --yy-margin).
    static let spreadMargin = 32.0
    /// Under the spread's pages: the read-along bar 24 pt above the safe area, and the page
    /// numbers below it; the text stops above both.
    static let spreadBottom = 90.0
    #endif

    /// Decides whether the reader lays out one column or two for the given viewport size.
    /// On iOS, two columns require landscape (`width > height`) unless iPhone Duo's `spread` is active,
    /// so portrait iPads (e.g. 13" at 1032 pt wide) stay one centred column.
    nonisolated static func columnCount(
        width: Double,
        height: Double,
        spread: Bool,
        prefersTwoColumns: Bool,
        chat: Bool,
        mode: LayoutMode,
        requireLandscape: Bool
    ) -> UInt8 {
        let book = spread && !chat && mode == .paginated
        let wideEnough = width >= 880 && (!requireLandscape || width > height)
        return book || (prefersTwoColumns && !chat && wideEnough && mode == .paginated) ? 2 : 1
    }

    func geometry() -> PageGeometry {
        let width = Double(bounds.width)
        let height = Double(bounds.height)
        #if os(iOS)
        let book = spread && !chat && mode == .paginated
        let columns = Self.columnCount(
            width: width, height: height, spread: spread,
            prefersTwoColumns: prefersTwoColumns, chat: chat, mode: mode, requireLandscape: true
        )
        #else
        let book = false
        let columns = Self.columnCount(
            width: width, height: height, spread: false,
            prefersTwoColumns: prefersTwoColumns, chat: chat, mode: mode, requireLandscape: false
        )
        #endif
        // About 30 characters to a line at most (460 wide at 15); wider pages get
        // wider margins instead.
        let measure = fontSize * 31
        #if os(iOS)
        let margin: Double
        if book {
            margin = spreadMargin(width: width, measure: measure)
        } else if spread, mode == .scrolled {
            // Scrolling on the inner display: one column, no line longer than 640 pt, centred.
            margin = max(40, (width - min(measure, 640)) / 2).rounded()
        } else {
            margin = max(width < 500 ? 24 : 40, (width / Double(columns) - measure) / 2).rounded()
        }
        let top = Double(safeAreaInsets.top) + 28
        let bottom = Double(safeAreaInsets.bottom) + (book ? Self.spreadBottom : 40)
        let paging = Paging.native
        #else
        let margin = max(width < 500 ? 24 : 40, (width / Double(columns) - measure) / 2).rounded()
        // The reading capsule floats over the bottom of the page.
        let top = 40.0
        let bottom = 96.0
        let paging = Paging.snap
        #endif
        return PageGeometry(paging: paging, margin: margin, top: top.rounded(), bottom: bottom.rounded(), columns: columns, reducedMotion: reduceMotion)
    }

    #if os(iOS)
    /// A book's spread: 32 at the outer edges, 64 between the pages, and no line longer than
    /// 560 pt. Half folded, each page's text stops 16 pt short of the fold. The margin is the
    /// only knob: the columns' gap is always twice it, so pages stay a whole screen apart.
    private func spreadMargin(width: Double, measure: Double) -> Double {
        var margin = max(Self.spreadMargin, (width / 2 - min(measure, 560)) / 2)
        if let fold, fold.width > 0, fold.height > fold.width {
            // The left page ends at width/2 - margin, the right one starts at width/2 + margin.
            margin = max(margin, width / 2 - Double(fold.minX) + 16, Double(fold.maxX) - width / 2 + 16)
        }
        return margin.rounded(.up)
    }
    #endif

    private func geometryChanged() {
        guard bounds.width > 0 else { return }
        let g = geometry()
        guard g != appliedGeometry else { return }
        let first = appliedGeometry == nil
        appliedGeometry = g
        if !first { apply(settings: [:]) }
    }

    /// The reader's `mode` itself arrives with the settings; this sets up the scrolling around it.
    private func modeChanged() {
        // The other mode shows the text from another start: the position is taken again from
        // what it shows, as the reader does.
        position = nil
        resetVerticalPull()
        #if os(iOS)
        holding = false
        for page in slots + spare { configure(page.webView.scrollView) }
        outer.isScrollEnabled = mode == .paginated
        delegate?.pager(self, barsAway: false)
        #endif
    }

    private func neighbourSlot(for edge: PullEdge) -> (index: Int, page: SectionPage)? {
        guard let current, let i = slots.firstIndex(where: { $0 === current }) else { return nil }
        let j = edge == .next ? i + 1 : i - 1
        guard slots.indices.contains(j) else { return nil }
        return (j, slots[j])
    }

    private func cueText(edge: PullEdge, armed: Bool, hasTarget: Bool, targetSection: UInt32?) -> String {
        guard hasTarget else { return String(localized: "已是最后一章") }
        let title = targetSection.flatMap { chapterTitle?($0) }?.trimmingCharacters(in: .whitespacesAndNewlines)
        let fallback = edge == .next ? String(localized: "下一章") : String(localized: "上一章")
        let dest = (title?.isEmpty == false) ? title! : fallback
        let verb: String
        #if os(iOS)
        if edge == .next {
            verb = armed ? String(localized: "松手进入") : String(localized: "继续上拉")
        } else {
            verb = armed ? String(localized: "松手回到") : String(localized: "继续下拉")
        }
        #else
        if edge == .next {
            verb = armed ? String(localized: "松手进入") : String(localized: "继续向下")
        } else {
            verb = armed ? String(localized: "松手回到") : String(localized: "继续向上")
        }
        #endif
        return "\(verb) · \(dest)"
    }

    private func updatePullCueColors() {
        pullCue.applyColors(background: background, ink: ink, accent: accent)
    }

    private func resetVerticalPull() {
        verticalTransitioning = false
        #if os(iOS)
        pullEdge = nil
        pullArmed = false
        for page in slots + spare {
            page.webView.transform = .identity
            page.webView.scrollView.isScrollEnabled = !(mode == .paginated && holding)
        }
        hidePullCue(animated: false)
        #else
        macPull = MacPullState()
        macWheelEdge = nil
        macWheelAccum = 0
        macLastWheelY = -1
        macCueHideTask?.cancel()
        macCueHideTask = nil
        hidePullCue(animated: false)
        #endif
    }

    // MARK: Layout

    #if os(iOS)
    override func layoutSubviews() {
        super.layoutSubviews()
        if !verticalTransitioning && outer.frame != bounds {
            outer.frame = bounds
            arrange()
        }
        if let edge = pullEdge { layoutPullCue(edge: edge) }
        afterLayout()
    }

    override func safeAreaInsetsDidChange() {
        super.safeAreaInsetsDidChange()
        geometryChanged()
        if let edge = pullEdge { layoutPullCue(edge: edge) }
    }
    #else
    override func layout() {
        super.layout()
        if let edge = macPull.edge ?? macWheelEdge, !pullCue.isHidden {
            layoutMacPullCue(edge: edge)
        }
        afterLayout()
    }

    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        // The page keys work as soon as the book opens, without clicking into the page first.
        if let current { window?.makeFirstResponder(current.webView) }
    }
    #endif

    private func afterLayout() {
        guard bounds.width > 0 else { return }
        geometryChanged()
        if let pendingOpen { open(section: pendingOpen.section, target: pendingOpen.target) }
    }

    private func arrange() {
        #if os(iOS)
        arranging = true
        defer { arranging = false }
        let w = bounds.width, h = bounds.height
        for (i, page) in slots.enumerated() {
            page.webView.transform = .identity
            page.webView.frame = CGRect(x: CGFloat(i) * w, y: 0, width: w, height: h)
            page.webView.isHidden = false
        }
        for page in spare {
            page.webView.transform = .identity
            page.webView.isHidden = true
        }
        outer.contentSize = CGSize(width: CGFloat(slots.count) * w, height: h)
        if let current, let i = slots.firstIndex(where: { $0 === current }) {
            outer.contentOffset = CGPoint(x: CGFloat(i) * w, y: 0)
        }
        syncScrollMode()
        #endif
    }

    private func show(_ page: SectionPage) {
        #if os(macOS)
        for slot in slots + spare where slot !== page { slot.webView.isHidden = true }
        page.webView.frame = bounds
        page.webView.isHidden = false
        window?.makeFirstResponder(page.webView)
        #endif
    }

    #if os(macOS)
    /// Keys the window wants before the page turns, such as 1–4 for a highlight colour.
    var keyHandler: ((NSEvent) -> Bool)?
    /// What right-clicking the page offers.
    var menuItems: (() -> [NSMenuItem])?

    private func key(_ event: NSEvent) -> Bool {
        if keyHandler?(event) == true { return true }
        guard event.modifierFlags.intersection([.command, .option, .control]).isEmpty else { return false }
        let forward: Bool
        switch event.keyCode {
        case 123, 116: forward = false  // ← page up
        case 124, 121: forward = true  // → page down
        // Scrolling, ↑ and ↓ move by lines as anywhere else; the other keys move by a screen
        // and carry on into the next section at the end of this one.
        case 126 where mode == .paginated: forward = false  // ↑
        case 125 where mode == .paginated: forward = true  // ↓
        case 49: forward = !event.modifierFlags.contains(.shift)  // space
        default: return false
        }
        delegate?.pagerTurnedByHand(self)
        turn(forward: forward)
        return true
    }

    private func scrollMetrics(of page: SectionPage) async -> (y: CGFloat, maxY: CGFloat)? {
        guard let arr = await page.ask("[window.pageYOffset, Math.max(0, (document.scrollingElement || document.documentElement).scrollHeight - window.innerHeight)]") as? [Any],
              arr.count == 2,
              let y = (arr[0] as? NSNumber)?.doubleValue,
              let maxY = (arr[1] as? NSNumber)?.doubleValue
        else { return nil }
        return (CGFloat(y), CGFloat(maxY))
    }

    private func handleScrollWheel(_ event: NSEvent) {
        guard mode == .scrolled, !verticalTransitioning, let current else { return }
        // Ignore inertial momentum so a fast flick stops cleanly at the chapter boundary.
        guard event.momentumPhase.isEmpty else { return }
        let dy = event.scrollingDeltaY
        let dx = event.scrollingDeltaX
        if event.phase.isEmpty {
            // Stepped mouse wheel (no gesture phases).
            guard abs(dy) > 0, abs(dy) >= abs(dx) else { return }
            let forward = dy < 0
            let step = min(max(abs(dy), 16), 24)
            let gen = current.generation
            Task { [weak self] in
                guard let self, let metrics = await self.scrollMetrics(of: current),
                      self.current === current, current.generation == gen,
                      self.mode == .scrolled, !self.verticalTransitioning
                else { return }
                let now = ProcessInfo.processInfo.systemUptime
                if self.macLastWheelY >= 0, abs(metrics.y - self.macLastWheelY) > 1 {
                    self.macLastWheelY = metrics.y
                    self.macLastWheelMovedAt = now
                    self.macWheelAccum = 0
                    self.macWheelEdge = nil
                    self.hidePullCue(animated: true)
                    return
                }
                self.macLastWheelY = metrics.y
                let atEdge = forward ? (metrics.maxY <= 1 || metrics.y >= metrics.maxY - 2) : (metrics.y <= 2)
                guard atEdge else {
                    self.macWheelAccum = 0
                    self.macWheelEdge = nil
                    self.hidePullCue(animated: true)
                    return
                }
                // Wait briefly after arriving at the edge so continuous wheel rolls stop there first.
                guard now - self.macLastWheelMovedAt >= 0.22 else { return }
                let edge: PullEdge = forward ? .next : .previous
                let neighbour = self.neighbourSlot(for: edge)
                if edge == .previous && neighbour == nil { return }
                if self.macWheelEdge != edge || now - self.macLastWheelEdgeAt > 0.9 {
                    self.macWheelAccum = 0
                }
                self.macWheelEdge = edge
                self.macLastWheelEdgeAt = now
                self.macWheelAccum += step
                let hasTarget = neighbour != nil
                self.showMacPullCue(edge: edge, text: self.cueText(edge: edge, armed: false, hasTarget: hasTarget, targetSection: neighbour?.page.section), armed: false, showIcon: hasTarget)
                self.scheduleMacCueHide()
                if self.macWheelAccum >= 30, now - self.macLastCrossAt > 0.45 {
                    self.macWheelAccum = 0
                    self.macWheelEdge = nil
                    self.macLastCrossAt = now
                    self.hidePullCue(animated: false)
                    if let neighbour, neighbour.page.isReady {
                        self.delegate?.pagerTurnedByHand(self)
                        self.move(toSlot: neighbour.index, animated: !self.reduceMotion, snapEdge: true)
                    } else if edge == .next && neighbour == nil {
                        self.delegate?.pagerReachedBookEnd(self)
                    }
                }
            }
            return
        }

        // Trackpad / Magic Mouse two-finger scroll with explicit phases.
        if event.phase == .began {
            macCueHideTask?.cancel()
            macWheelEdge = nil
            macWheelAccum = 0
            let nextSession = macPull.session + 1
            macPull = MacPullState(session: nextSession)
            let gen = current.generation
            Task { [weak self] in
                guard let self, let metrics = await self.scrollMetrics(of: current),
                      self.macPull.session == nextSession,
                      self.current === current, current.generation == gen
                else { return }
                self.macPull.checked = true
                self.macPull.mayPrev = metrics.y <= 2
                self.macPull.mayNext = metrics.maxY <= 1 || metrics.y >= metrics.maxY - 2
            }
            return
        }

        if event.phase == .changed {
            guard macPull.checked, abs(dy) >= abs(dx) else { return }
            let threshold: CGFloat = 72
            if macPull.edge == nil {
                if dy < 0, macPull.mayNext {
                    macPull.edge = .next
                } else if dy > 0, macPull.mayPrev {
                    macPull.edge = .previous
                }
            }
            guard let edge = macPull.edge else { return }
            let neighbour = neighbourSlot(for: edge)
            if edge == .previous && neighbour == nil { return }
            let delta = edge == .next ? -dy : dy
            macPull.accum = max(0, macPull.accum + delta)
            if macPull.accum <= 2 {
                macPull.armed = false
                hidePullCue(animated: true)
                return
            }
            let wasArmed = macPull.armed
            let armed = Self.isPullArmed(amount: macPull.accum, wasArmed: wasArmed, threshold: threshold)
            let hasTarget = neighbour != nil
            if armed && !wasArmed {
                NSHapticFeedbackManager.defaultPerformer.perform(.alignment, performanceTime: .now)
            }
            macPull.armed = armed
            showMacPullCue(
                edge: edge,
                text: cueText(edge: edge, armed: armed && hasTarget, hasTarget: hasTarget, targetSection: neighbour?.page.section),
                armed: armed && hasTarget,
                showIcon: hasTarget
            )
            return
        }

        if event.phase == .ended {
            let state = macPull
            macPull = MacPullState(session: state.session + 1)
            hidePullCue(animated: true)
            guard state.armed, let edge = state.edge else { return }
            if let neighbour = neighbourSlot(for: edge), neighbour.page.isReady {
                delegate?.pagerTurnedByHand(self)
                move(toSlot: neighbour.index, animated: !reduceMotion, snapEdge: true)
            } else if edge == .next && neighbourSlot(for: .next) == nil {
                delegate?.pagerReachedBookEnd(self)
            }
            return
        }

        if event.phase == .cancelled {
            macPull = MacPullState(session: macPull.session + 1)
            hidePullCue(animated: true)
        }
    }

    private func showMacPullCue(edge: PullEdge, text: String, armed: Bool, showIcon: Bool) {
        pullCue.configure(text: text, edge: edge, armed: armed, showIcon: showIcon, animated: !reduceMotion)
        layoutMacPullCue(edge: edge)
        pullCue.isHidden = false
        if reduceMotion {
            pullCue.alphaValue = 1
        } else {
            NSAnimationContext.runAnimationGroup { ctx in
                ctx.duration = 0.14
                pullCue.animator().alphaValue = 1
            }
        }
    }

    private func scheduleMacCueHide() {
        macCueHideTask?.cancel()
        macCueHideTask = Task { [weak self] in
            try? await Task.sleep(for: .milliseconds(1000))
            guard !Task.isCancelled, let self else { return }
            self.macWheelEdge = nil
            self.macWheelAccum = 0
            self.hidePullCue(animated: true)
        }
    }

    private func hidePullCue(animated: Bool) {
        if !animated || reduceMotion {
            pullCue.alphaValue = 0
            pullCue.isHidden = true
            return
        }
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.16
            pullCue.animator().alphaValue = 0
        }, completionHandler: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self, self.pullCue.alphaValue <= 0.01 else { return }
                self.pullCue.isHidden = true
            }
        })
    }

    private func layoutMacPullCue(edge: PullEdge) {
        let maxW = max(120, min(bounds.width - 48, 320))
        let size = pullCue.preferredSize(maxWidth: maxW)
        let x = ((bounds.width - size.width) / 2).rounded()
        // AppKit origin is bottom-left; clear the bottom reading capsule (96pt) or top bar (46pt).
        let y: CGFloat = edge == .next ? 92 : max(16, bounds.height - 46 - size.height)
        pullCue.frame = CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    private func crossVerticallyMac(toSlot j: Int, forward: Bool, animated: Bool, snapEdge: Bool) {
        guard !verticalTransitioning, let old = current, slots.indices.contains(j), slots[j].isReady else { return }
        let target = slots[j]
        hidePullCue(animated: false)
        if snapEdge {
            let p = forward ? 0 : max(0, (target.layout?.pageCount ?? 1) - 1)
            target.call("yy.scrollToPage(p, false)", ["p": p])
        }
        guard animated, !reduceMotion, bounds.height > 0 else {
            current = target
            show(target)
            loadNeighbours()
            moved()
            return
        }
        verticalTransitioning = true
        let h = bounds.height
        // AppKit bottom-up coordinates: next chapter rises from below (y = -h -> 0).
        target.webView.frame = bounds.offsetBy(dx: 0, dy: forward ? -h : h)
        target.webView.isHidden = false
        NSAnimationContext.runAnimationGroup({ ctx in
            ctx.duration = 0.24
            ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
            old.webView.animator().frame = self.bounds.offsetBy(dx: 0, dy: forward ? h : -h)
            target.webView.animator().frame = self.bounds
        }, completionHandler: { [weak self] in
            Task { @MainActor [weak self] in
                guard let self else { return }
                self.verticalTransitioning = false
                old.webView.frame = self.bounds
                guard self.current === old else { return }
                self.current = target
                self.show(target)
                self.loadNeighbours()
                self.moved()
            }
        })
    }
    #endif
}

extension SectionPage {
    func setBackground(_ color: PlatformColor) {
        #if os(iOS)
        webView.backgroundColor = color
        webView.scrollView.backgroundColor = color
        #else
        webView.underPageBackgroundColor = color
        #endif
    }
}

#if os(iOS)
/// Lets the sections' own scroll views track the same touch, so one swipe can turn the last
/// page of a section and carry on into the next.
final class SectionScrollView: UIScrollView, UIGestureRecognizerDelegate {
    func gestureRecognizer(_ g: UIGestureRecognizer, shouldRecognizeSimultaneouslyWith other: UIGestureRecognizer) -> Bool {
        g === panGestureRecognizer && other.view is UIScrollView && other.view !== self
    }
}

/// Minimal pill shown while pulling past a scrolled chapter's bottom or top edge.
private final class PullCueView: UIView {
    private let icon = UIImageView()
    private let label = UILabel()
    private var inkColor: UIColor = .label
    private var accentColor: UIColor = .tintColor
    private var isArmed = false
    private var currentEdge: ReaderPager.PullEdge = .next

    override init(frame: CGRect) {
        super.init(frame: frame)
        layer.cornerCurve = .continuous
        layer.borderWidth = 0.5
        icon.contentMode = .scaleAspectFit
        icon.preferredSymbolConfiguration = UIImage.SymbolConfiguration(pointSize: 11, weight: .semibold)
        label.font = .systemFont(ofSize: 12.5, weight: .medium)
        label.lineBreakMode = .byTruncatingTail
        addSubview(icon)
        addSubview(label)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    func applyColors(background: UIColor, ink: UIColor, accent: UIColor) {
        inkColor = ink
        accentColor = accent
        backgroundColor = background.withAlphaComponent(0.94)
        layer.borderColor = ink.withAlphaComponent(0.14).cgColor
        applyStateColors()
    }

    func configure(text: String, edge: ReaderPager.PullEdge, armed: Bool, showIcon: Bool, animated: Bool) {
        let changed = armed != isArmed || edge != currentEdge
        isArmed = armed
        currentEdge = edge
        label.text = text
        icon.isHidden = !showIcon
        if showIcon {
            icon.image = UIImage(systemName: edge == .next ? "arrow.up" : "arrow.down")
        }
        applyStateColors()
        let targetTransform: CGAffineTransform = armed ? CGAffineTransform(rotationAngle: .pi) : .identity
        if changed && animated {
            UIView.animate(withDuration: 0.16, delay: 0, options: [.curveEaseOut, .beginFromCurrentState]) {
                self.icon.transform = targetTransform
            }
        } else {
            icon.transform = targetTransform
        }
    }

    private func applyStateColors() {
        let fg = isArmed ? accentColor : inkColor.withAlphaComponent(0.72)
        icon.tintColor = fg
        label.textColor = fg
        layer.borderColor = (isArmed ? accentColor.withAlphaComponent(0.42) : inkColor.withAlphaComponent(0.14)).cgColor
    }

    func preferredSize(maxWidth: CGFloat) -> CGSize {
        let iconW: CGFloat = icon.isHidden ? 0 : 14
        let gap: CGFloat = icon.isHidden ? 0 : 6
        let padX: CGFloat = 14
        let padY: CGFloat = 7
        let textMax = max(40, maxWidth - padX * 2 - iconW - gap)
        let textSize = label.sizeThatFits(CGSize(width: textMax, height: 24))
        let w = min(maxWidth, ceil(textSize.width) + padX * 2 + iconW + gap)
        let h = max(30, ceil(textSize.height) + padY * 2)
        return CGSize(width: w, height: h)
    }

    override func layoutSubviews() {
        super.layoutSubviews()
        layer.cornerRadius = bounds.height / 2
        let padX: CGFloat = 14
        let iconW: CGFloat = icon.isHidden ? 0 : 14
        let gap: CGFloat = icon.isHidden ? 0 : 6
        if !icon.isHidden {
            icon.bounds = CGRect(x: 0, y: 0, width: iconW, height: 14)
            icon.center = CGPoint(x: padX + iconW / 2, y: bounds.midY)
        }
        let labelX = padX + iconW + gap
        label.frame = CGRect(x: labelX, y: 0, width: max(0, bounds.width - labelX - padX), height: bounds.height)
    }
}

extension ReaderPager: UIScrollViewDelegate {
    private func configure(_ scroll: UIScrollView) {
        let paged = mode == .paginated
        scroll.isPagingEnabled = paged
        // At a section's edge the outer scroll view takes over, so the inner one must not bounce.
        scroll.bounces = !paged
        scroll.alwaysBounceHorizontal = false
        scroll.alwaysBounceVertical = !paged
        scroll.isScrollEnabled = !(paged && holding)
    }

    private func x(of page: SectionPage) -> CGFloat {
        CGFloat(slots.firstIndex(where: { $0 === page }) ?? 0) * bounds.width
    }

    private func atStart(_ s: UIScrollView) -> Bool { s.contentOffset.x <= 1 }
    private func atEnd(_ s: UIScrollView) -> Bool { s.contentOffset.x >= s.contentSize.width - s.bounds.width - 1 }

    func scrollViewWillBeginDragging(_ scrollView: UIScrollView) {
        barsLastY = scrollView.contentOffset.y
        barsTravel = 0
        delegate?.pagerTurnedByHand(self)
        if mode == .scrolled, scrollView === current?.webView.scrollView {
            pullEdge = nil
            pullArmed = false
            hidePullCue(animated: false)
            pullHaptic.prepare()
            return
        }
        guard scrollView === outer, let current else { return }
        let inner = current.webView.scrollView
        outerMayMove = (atEnd(inner), atStart(inner))
    }

    /// The controls follow the hand only: the ink line moving the page doesn't send them away.
    /// At either end of a section they come back, for 下一章 is on them.
    private func trackBars(_ scroll: UIScrollView) {
        guard mode == .scrolled, !verticalTransitioning, scroll === current?.webView.scrollView else { return }
        let y = scroll.contentOffset.y
        let moved = y - barsLastY
        barsLastY = y
        if y <= 0 || y >= scroll.contentSize.height - scroll.bounds.height - 1 {
            barsTravel = 0
            delegate?.pager(self, barsAway: false)
            return
        }
        guard scroll.isDragging || scroll.isDecelerating else {
            barsTravel = 0
            return
        }
        barsTravel = moved * barsTravel < 0 ? moved : barsTravel + moved
        if abs(barsTravel) >= 24 {
            delegate?.pager(self, barsAway: barsTravel > 0)
            barsTravel = 0
        }
    }

    private func updateVerticalPull(_ scroll: UIScrollView) {
        guard scroll.isDragging else {
            if pullEdge != nil {
                pullEdge = nil
                pullArmed = false
                hidePullCue(animated: true)
            }
            return
        }
        let maxY = max(0, scroll.contentSize.height - scroll.bounds.height)
        guard let over = Self.verticalOverscroll(y: scroll.contentOffset.y, maxY: maxY) else {
            if pullEdge != nil {
                pullEdge = nil
                pullArmed = false
                hidePullCue(animated: false)
            }
            return
        }
        let neighbour = neighbourSlot(for: over.edge)
        if over.edge == .previous && neighbour == nil {
            if pullEdge != nil {
                pullEdge = nil
                pullArmed = false
                hidePullCue(animated: false)
            }
            return
        }
        let wasArmed = (pullEdge == over.edge) && pullArmed
        let armed = Self.isPullArmed(amount: over.amount, wasArmed: wasArmed)
        let hasTarget = neighbour != nil
        if armed && !wasArmed {
            pullHaptic.impactOccurred()
        }
        pullEdge = over.edge
        pullArmed = armed
        pullCue.configure(
            text: cueText(edge: over.edge, armed: armed && hasTarget, hasTarget: hasTarget, targetSection: neighbour?.page.section),
            edge: over.edge,
            armed: armed && hasTarget,
            showIcon: hasTarget,
            animated: !reduceMotion
        )
        layoutPullCue(edge: over.edge)
        let progress = min(1, max(0, (over.amount - 8) / (Self.pullThreshold - 8)))
        pullCue.alpha = progress
    }

    private func layoutPullCue(edge: PullEdge) {
        let maxW = max(120, min(bounds.width - 36, 300))
        let size = pullCue.preferredSize(maxWidth: maxW)
        let x = ((bounds.width - size.width) / 2).rounded()
        let y: CGFloat
        if edge == .next {
            let clearance: CGFloat = bottomCapsuleVisible ? 66 : 16
            y = bounds.height - safeAreaInsets.bottom - clearance - size.height
        } else {
            y = safeAreaInsets.top + 14
        }
        pullCue.frame = CGRect(x: x, y: y, width: size.width, height: size.height)
    }

    private func hidePullCue(animated: Bool) {
        guard pullCue.alpha > 0 else { return }
        if !animated || reduceMotion {
            pullCue.alpha = 0
            return
        }
        UIView.animate(withDuration: 0.16, delay: 0, options: [.curveEaseOut, .beginFromCurrentState]) {
            self.pullCue.alpha = 0
        }
    }

    private func crossVertically(toSlot j: Int, forward: Bool, pullOffset: CGFloat, animated: Bool, snapEdge: Bool) {
        guard !verticalTransitioning, let current, let i = slots.firstIndex(where: { $0 === current }),
              slots.indices.contains(j), slots[j].isReady
        else { return }
        let target = slots[j]
        if snapEdge {
            let tsv = target.webView.scrollView
            let tMaxY = max(0, tsv.contentSize.height - tsv.bounds.height)
            tsv.setContentOffset(CGPoint(x: 0, y: forward ? 0 : tMaxY), animated: false)
        }
        let csv = current.webView.scrollView
        if pullOffset > 0 {
            csv.isScrollEnabled = false
            let cMaxY = max(0, csv.contentSize.height - csv.bounds.height)
            csv.setContentOffset(CGPoint(x: 0, y: forward ? cMaxY : 0), animated: false)
        }
        hidePullCue(animated: false)
        guard animated, !reduceMotion, bounds.height > 0 else {
            csv.isScrollEnabled = !(mode == .paginated && holding)
            self.current = target
            loadNeighbours()
            moved()
            return
        }
        verticalTransitioning = true
        let w = bounds.width, h = bounds.height
        let shift = min(max(0, pullOffset), h * 0.4)
        target.webView.transform = .identity
        target.webView.frame = CGRect(x: CGFloat(i) * w, y: 0, width: w, height: h)
        target.webView.isHidden = false
        outer.bringSubviewToFront(target.webView)
        current.webView.transform = CGAffineTransform(translationX: 0, y: forward ? -shift : shift)
        target.webView.transform = CGAffineTransform(translationX: 0, y: forward ? (h - shift) : -(h - shift))
        UIView.animate(
            withDuration: 0.28, delay: 0,
            usingSpringWithDamping: 1.0, initialSpringVelocity: 0,
            options: [.curveEaseOut]
        ) {
            current.webView.transform = CGAffineTransform(translationX: 0, y: forward ? -h : h)
            target.webView.transform = .identity
        } completion: { [weak self] _ in
            current.webView.transform = .identity
            target.webView.transform = .identity
            guard let self else { return }
            csv.isScrollEnabled = !(self.mode == .paginated && self.holding)
            self.verticalTransitioning = false
            guard self.current === current else { return }
            self.current = target
            self.loadNeighbours()
            self.moved()
        }
    }

    func scrollViewDidScroll(_ scrollView: UIScrollView) {
        trackBars(scrollView)
        if !arranging, !verticalTransitioning, mode == .scrolled, scrollView === current?.webView.scrollView {
            updateVerticalPull(scrollView)
            return
        }
        guard !arranging, mode == .paginated, let current, bounds.width > 0 else { return }
        let inner = current.webView.scrollView
        let base = x(of: current)
        let d = outer.contentOffset.x - base
        if scrollView === outer {
            // Within a section the section's own pages turn; the outer view only moves at its edges.
            if (d > 0 && !outerMayMove.forward) || (d < 0 && !outerMayMove.backward) {
                outer.contentOffset.x = base
            }
        } else if scrollView === inner {
            if d > 0.5 {
                inner.contentOffset.x = max(0, inner.contentSize.width - inner.bounds.width)
            } else if d < -0.5 {
                inner.contentOffset.x = 0
            }
        } else if scrollView.isDragging || scrollView.isDecelerating,
                  let i = slots.firstIndex(where: { $0.webView.scrollView === scrollView }),
                  let c = slots.firstIndex(where: { $0 === current }) {
            // A neighbour seen during a turn keeps showing the page that joins this section.
            scrollView.contentOffset.x = i < c ? max(0, scrollView.contentSize.width - scrollView.bounds.width) : 0
        }
    }

    func scrollViewWillEndDragging(_ scrollView: UIScrollView, withVelocity velocity: CGPoint, targetContentOffset: UnsafeMutablePointer<CGPoint>) {
        if mode == .scrolled, !verticalTransitioning, scrollView === current?.webView.scrollView {
            let maxY = max(0, scrollView.contentSize.height - scrollView.bounds.height)
            let activeEdge = pullEdge
            let wasArmed = pullArmed
            pullEdge = nil
            pullArmed = false
            if let over = Self.verticalOverscroll(y: scrollView.contentOffset.y, maxY: maxY),
               activeEdge == over.edge,
               Self.isPullArmed(amount: over.amount, wasArmed: wasArmed) {
                hidePullCue(animated: false)
                if let neighbour = neighbourSlot(for: over.edge), neighbour.page.isReady {
                    targetContentOffset.pointee = CGPoint(x: 0, y: over.edge == .next ? maxY : 0)
                    crossVertically(toSlot: neighbour.index, forward: over.edge == .next, pullOffset: over.amount, animated: !reduceMotion, snapEdge: true)
                    return
                } else if over.edge == .next && neighbourSlot(for: .next) == nil {
                    delegate?.pagerReachedBookEnd(self)
                    return
                }
            }
            hidePullCue(animated: true)
            return
        }
        guard scrollView === outer, let current else { return }
        let base = x(of: current)
        let target = targetContentOffset.pointee.x
        if (target > base && !outerMayMove.forward) || (target < base && !outerMayMove.backward) {
            targetContentOffset.pointee.x = base
        }
    }

    func scrollViewDidEndDragging(_ scrollView: UIScrollView, willDecelerate decelerate: Bool) {
        if mode == .scrolled, !verticalTransitioning, scrollView === current?.webView.scrollView {
            pullEdge = nil
            pullArmed = false
            hidePullCue(animated: true)
        }
        if !decelerate { ended(scrollView) }
    }

    func scrollViewDidEndDecelerating(_ scrollView: UIScrollView) {
        ended(scrollView)
    }

    func scrollViewDidEndScrollingAnimation(_ scrollView: UIScrollView) {
        ended(scrollView)
    }

    private func ended(_ scrollView: UIScrollView) {
        if scrollView === outer {
            settle()
        } else if let current, scrollView === current.webView.scrollView, mode == .paginated, bounds.width > 0 {
            let page = Int((scrollView.contentOffset.x / bounds.width).rounded())
            let changed = page != current.page
            current.page = page
            if changed { playManualTurnSound() }
            moved()
        }
    }

    /// After the outer view comes to rest: if it rests on a neighbour, that section is current now.
    private func settle() {
        outerMayMove = (false, false)
        guard bounds.width > 0 else { return }
        let i = Int((outer.contentOffset.x / bounds.width).rounded())
        guard slots.indices.contains(i) else { return }
        if slots[i] !== current {
            current = slots[i]
            loadNeighbours()
            playManualTurnSound()
        }
        moved()
    }

    // MARK: Cover transition (iOS: 1:1 drag + tap with trailing edge shadow)

    @objc private func handleCoverPan(_ pan: UIPanGestureRecognizer) {
        guard useCover, !holding, !coverAnimating, bounds.width > 0 else { return }
        let tx = pan.translation(in: self).x
        let vx = pan.velocity(in: self).x
        switch pan.state {
        case .began:
            delegate?.pagerTurnedByHand(self)
        case .changed:
            if !coverActive, abs(tx) > 4 {
                _ = beginCover(forward: tx < 0)
            }
            if coverActive {
                let raw = coverForward ? (-tx / bounds.width) : (tx / bounds.width)
                renderCover(progress: raw)
            }
        case .ended, .cancelled, .failed:
            guard coverActive else { return }
            let flingCommit = coverForward ? (vx < -420) : (vx > 420)
            let flingCancel = coverForward ? (vx > 420) : (vx < -420)
            let commit: Bool
            if pan.state != .ended {
                commit = false
            } else if flingCommit {
                commit = true
            } else if flingCancel {
                commit = false
            } else {
                commit = coverProgress >= 0.35
            }
            settleCover(commit: commit, velocity: vx)
        default:
            break
        }
    }

    private func beginCover(forward: Bool) -> Bool {
        cancelCover()
        guard let current, let layout = current.layout, let i = slots.firstIndex(where: { $0 === current }),
              bounds.width > 0, bounds.height > 0 else { return false }
        let nextPage = current.page + (forward ? 1 : -1)
        let targetSlot: Int
        let targetPage: Int
        if nextPage >= 0 && nextPage < layout.pageCount {
            targetSlot = i
            targetPage = nextPage
        } else {
            let j = forward ? i + 1 : i - 1
            guard slots.indices.contains(j), slots[j].isReady else { return false }
            targetSlot = j
            targetPage = forward ? 0 : max(0, (slots[j].layout?.pageCount ?? 1) - 1)
        }
        guard let snap = current.webView.snapshotView(afterScreenUpdates: false) else { return false }
        snap.frame = bounds
        snap.isUserInteractionEnabled = false

        coverActive = true
        coverForward = forward
        coverFromSlot = i
        coverFromPage = current.page
        coverTargetSlot = targetSlot
        coverTargetPage = targetPage
        coverProgress = 0
        coverSnapshot = snap

        // Position the destination page inside `outer` before revealing it.
        arranging = true
        let dest = slots[targetSlot]
        dest.webView.scrollView.setContentOffset(CGPoint(x: CGFloat(targetPage) * bounds.width, y: 0), animated: false)
        outer.contentOffset = CGPoint(x: CGFloat(targetSlot) * bounds.width, y: 0)
        arranging = false

        if forward {
            // Snapshot of outgoing page glides left over stationary destination.
            insertSubview(snap, aboveSubview: outer)
            outer.transform = .identity
        } else {
            // Snapshot of outgoing page stays stationary underneath while incoming `outer` glides in from left.
            insertSubview(snap, belowSubview: outer)
            outer.transform = CGAffineTransform(translationX: -bounds.width, y: 0)
        }
        coverShadow.frame = CGRect(x: 0, y: 0, width: 22, height: bounds.height)
        coverShadow.layer.sublayers?.first?.frame = coverShadow.bounds
        bringSubviewToFront(coverShadow)
        bringSubviewToFront(pullCue)
        coverShadow.isHidden = false
        renderCover(progress: 0)
        return true
    }

    private func renderCover(progress: CGFloat) {
        let p = min(1, max(0, progress))
        coverProgress = p
        let w = bounds.width
        let edgeX: CGFloat
        if coverForward {
            let x = -p * w
            coverSnapshot?.transform = CGAffineTransform(translationX: x, y: 0)
            edgeX = x + w
        } else {
            let x = (-1 + p) * w
            coverSnapshot?.transform = .identity
            outer.transform = CGAffineTransform(translationX: x, y: 0)
            edgeX = x + w
        }
        coverShadow.frame = CGRect(x: edgeX, y: 0, width: 22, height: bounds.height)
        coverShadow.layer.sublayers?.first?.frame = coverShadow.bounds
        let bell = max(0, 1 - abs(p * 2 - 1))
        coverShadow.alpha = (p <= 0.002 || p >= 0.998) ? 0 : (0.35 + 0.65 * bell)
    }

    private func settleCover(commit: Bool, velocity: CGFloat) {
        guard coverActive else { return }
        coverAnimating = true
        let targetProgress: CGFloat = commit ? 1 : 0
        let remaining = abs(targetProgress - coverProgress)
        let duration = max(0.14, min(0.28, Double(remaining) * 0.28))
        UIView.animate(
            withDuration: duration,
            delay: 0,
            usingSpringWithDamping: 1.0,
            initialSpringVelocity: min(12, abs(velocity) / max(1, bounds.width)),
            options: [.curveEaseOut, .beginFromCurrentState]
        ) {
            self.renderCover(progress: targetProgress)
        } completion: { [weak self] _ in
            guard let self else { return }
            self.coverAnimating = false
            self.finishCover(commit: commit)
        }
    }

    private func finishCover(commit: Bool) {
        coverSnapshot?.removeFromSuperview()
        coverSnapshot = nil
        coverShadow.isHidden = true
        outer.transform = .identity
        let wasActive = coverActive
        coverActive = false
        guard wasActive else { return }
        if commit {
            if slots.indices.contains(coverTargetSlot) {
                let target = slots[coverTargetSlot]
                target.page = coverTargetPage
                if target !== current {
                    current = target
                    loadNeighbours()
                }
            }
            playManualTurnSound()
            moved()
        } else {
            arranging = true
            if slots.indices.contains(coverFromSlot) {
                let orig = slots[coverFromSlot]
                orig.webView.scrollView.setContentOffset(CGPoint(x: CGFloat(coverFromPage) * bounds.width, y: 0), animated: false)
                outer.contentOffset = CGPoint(x: CGFloat(coverFromSlot) * bounds.width, y: 0)
            }
            arranging = false
        }
    }

    private func cancelCover() {
        guard coverActive else { return }
        coverAnimating = false
        finishCover(commit: false)
    }
}

extension ReaderPager: UIGestureRecognizerDelegate {
    override func gestureRecognizerShouldBegin(_ gestureRecognizer: UIGestureRecognizer) -> Bool {
        guard gestureRecognizer === coverPan else { return super.gestureRecognizerShouldBegin(gestureRecognizer) }
        guard useCover, !holding else { return false }
        let v = coverPan.velocity(in: self)
        return abs(v.x) > abs(v.y) * 1.2
    }
}
#else
/// Minimal pill shown on macOS while scrolling past a chapter's vertical boundary.
private final class MacPullCueView: NSView {
    private let icon = NSImageView()
    private let label = NSTextField(labelWithString: "")
    private var bgColor: NSColor = .windowBackgroundColor
    private var inkColor: NSColor = .labelColor
    private var accentColor: NSColor = .controlAccentColor
    private var isArmed = false

    override init(frame: CGRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.borderWidth = 0.5
        icon.imageScaling = .scaleProportionallyDown
        label.font = .systemFont(ofSize: 12, weight: .medium)
        label.lineBreakMode = .byTruncatingTail
        label.maximumNumberOfLines = 1
        addSubview(icon)
        addSubview(label)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { fatalError() }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func applyColors(background: NSColor, ink: NSColor, accent: NSColor) {
        bgColor = background
        inkColor = ink
        accentColor = accent
        updateLayerColors()
    }

    func configure(text: String, edge: ReaderPager.PullEdge, armed: Bool, showIcon: Bool, animated: Bool) {
        isArmed = armed
        label.stringValue = text
        icon.isHidden = !showIcon
        if showIcon {
            let symbol = edge == .next ? (armed ? "arrow.up" : "arrow.down") : (armed ? "arrow.down" : "arrow.up")
            let cfg = NSImage.SymbolConfiguration(pointSize: 10.5, weight: .semibold)
            icon.image = NSImage(systemSymbolName: symbol, accessibilityDescription: nil)?.withSymbolConfiguration(cfg)
        }
        updateLayerColors()
    }

    private func updateLayerColors() {
        let fg = isArmed ? accentColor : inkColor.withAlphaComponent(0.72)
        icon.contentTintColor = fg
        label.textColor = fg
        layer?.backgroundColor = bgColor.withAlphaComponent(0.94).cgColor
        layer?.borderColor = (isArmed ? accentColor.withAlphaComponent(0.42) : inkColor.withAlphaComponent(0.16)).cgColor
    }

    func preferredSize(maxWidth: CGFloat) -> CGSize {
        let iconW: CGFloat = icon.isHidden ? 0 : 13
        let gap: CGFloat = icon.isHidden ? 0 : 6
        let padX: CGFloat = 13
        let padY: CGFloat = 6
        let textMax = max(40, maxWidth - padX * 2 - iconW - gap)
        let textW = min(textMax, ceil(label.attributedStringValue.size().width) + 2)
        let textH = ceil(label.fittingSize.height)
        return CGSize(width: min(maxWidth, textW + padX * 2 + iconW + gap), height: max(28, textH + padY * 2))
    }

    override func layout() {
        super.layout()
        layer?.cornerRadius = bounds.height / 2
        updateLayerColors()
        let padX: CGFloat = 13
        let iconW: CGFloat = icon.isHidden ? 0 : 13
        let gap: CGFloat = icon.isHidden ? 0 : 6
        if !icon.isHidden {
            icon.frame = CGRect(x: padX, y: (bounds.height - 13) / 2, width: iconW, height: 13)
        }
        let lh = ceil(label.fittingSize.height)
        let lx = padX + iconW + gap
        label.frame = CGRect(x: lx, y: (bounds.height - lh) / 2, width: max(0, bounds.width - lx - padX), height: lh)
    }
}
#endif
