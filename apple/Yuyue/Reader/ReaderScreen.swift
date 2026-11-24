import SwiftUI
import Translation
import YuyueCore

/// A book open for reading: the pages, and the controls around them.
struct ReaderScreen: View {
    let library: Library
    let bookID: String
    @State private var model: ReaderModel?
    @State private var pdf: PdfModel?
    @State private var audio: AudiobookPlayer?
    #if os(macOS)
    /// 重新导入 from the card, when this window is the one the reader is looking at.
    @State private var reimporting = false
    #endif

    var body: some View {
        Group {
            if let model {
                if model.detail == nil {
                    // Nothing to read: don't keep a window whose title bar only says 读书.
                    UnopenedBook(library: library, message: model.failure ?? String(localized: "这本书读不出来，文件可能坏了"))
                } else {
                    ReaderContent(model: model)
                        // A book built again comes with a new model; the pages go with it.
                        .id(ObjectIdentifier(model))
                        #if os(macOS)
                        .background(ReaderWindowShape(kind: .book))
                        #endif
                }
            } else if let pdf {
                if !pdf.opened {
                    UnopenedBook(library: library, message: pdf.failure ?? String(localized: "这本书读不出来，文件可能坏了"))
                } else {
                    #if os(macOS)
                    MacPdfView(model: pdf)
                        .background(ReaderWindowShape(kind: .book))
                    #else
                    PhonePdfView(model: pdf)
                    #endif
                }
            } else if let audio {
                #if os(macOS)
                AudiobookScreen(library: library, player: audio)
                    .background(ReaderWindowShape(kind: .book))
                #else
                AudiobookScreen(library: library, player: audio) { library.presented = nil }
                #endif
            } else {
                YYColor.bg.ignoresSafeArea()
            }
        }
        #if os(macOS)
        .background(OneWindowPerBook(bookID: bookID))
        .bookBlocked(library) { reimporting = true }
        .fileImporter(isPresented: $reimporting, allowedContentTypes: Library.importTypes, allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { Task { await library.importFiles(urls) } }
        }
        #endif
        .onAppear {
            guard model == nil, pdf == nil, audio == nil else { return }
            #if os(iOS)
            // Still being read aloud from the shelf: pick it up as it is.
            if let held = library.nowPlaying, held.bookID == bookID {
                if let held = held as? ReaderModel { model = held; held.consumeIntent() } else if let held = held as? PdfModel { pdf = held } else if let held = held as? AudiobookPlayer { audio = held }
                library.nowPlaying = nil
                if model != nil || pdf != nil || audio != nil { return }
            } else if let held = library.nowPlaying {
                // Another book: one voice at a time.
                held.stopAndClose()
                library.nowPlaying = nil
            }
            #else
            // 播客: the episode the 播放条 is playing opens on the voice as it is, and stays the
            // 播放条's. Another book opening doesn't stop it.
            if let held = library.nowPlaying as? ReaderModel, held.bookID == bookID {
                model = held
                library.openModels.add(held)
                return
            }
            #endif
            let book = library.books.first(where: { $0.id == bookID })
            let format = book?.format
            if format == .audio, let book {
                audio = AudiobookPlayer(library: library, book: book)
                // 接着听 plays at once; opened from the shelf, it waits for ▶.
                switch library.takeIntent(for: bookID) {
                case .listen: audio?.play()
                case .listenFromStart:
                    audio?.seek(to: 0)
                    audio?.play()
                default: break
                }
            } else if format == .pdf {
                pdf = PdfModel(library: library, bookID: bookID)
            } else {
                model = ReaderModel(library: library, bookID: bookID)
                #if os(macOS)
                if let model { library.openModels.add(model) }
                #endif
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .bookRebuilt)) { n in
            // Read another way (编码和目录): the chapters changed under the open pages.
            guard n.object as? String == bookID, model != nil else { return }
            model?.retire()
            model = ReaderModel(library: library, bookID: bookID)
        }
        .onReceive(NotificationCenter.default.publisher(for: .bookRemoved)) { n in
            guard n.object as? String == bookID else { return }
            model?.abandon()
            pdf?.abandon()
            audio?.abandon()
            #if os(macOS)
            if let model { library.openModels.remove(model) }
            OneWindowPerBook.closeWindow(bookID)
            #else
            if library.presented == bookID { library.presented = nil }
            #endif
        }
        .onDisappear {
            #if os(iOS)
            // Closing the pages doesn't stop the voice: the 播放条 over the tabs carries on with it.
            if let model, model.isPlaying {
                model.flush()
                library.nowPlaying = model
            } else if let pdf, pdf.isPlaying {
                library.nowPlaying = pdf
            } else if let audio, audio.isPlaying {
                library.nowPlaying = audio
            } else {
                model?.close()
                pdf?.close()
                audio?.close()
            }
            #else
            if let model { library.openModels.remove(model) }
            // An episode playing, or paused, in the 播放条 goes on there without its window.
            if let model, library.nowPlaying === model {
                model.flush()
            } else {
                model?.close()
            }
            pdf?.close()
            audio?.close()
            #endif
            // The shelf shows how far each book has been read; this one has just moved on.
            library.reload()
        }
    }
}

#if os(macOS)
/// A book has one window. Opening it again comes to the window it has; but windows put back from
/// last time (and any opened while they were being put back) can bring a book twice, so a second
/// window for a book brings the first forward and closes itself.
private struct SwitchBookKey: EnvironmentKey {
    nonisolated(unsafe) static let defaultValue: ((String) -> Void)? = nil
}

extension EnvironmentValues {
    /// Puts another book in this reading window.
    var switchBook: ((String) -> Void)? {
        get { self[SwitchBookKey.self] }
        set { self[SwitchBookKey.self] = newValue }
    }
}

struct OneWindowPerBook: NSViewRepresentable {
    let bookID: String

    @MainActor private static var open: [String: WeakWindow] = [:]
    private struct WeakWindow { weak var window: NSWindow? }

    /// The book is in a window already.
    @MainActor static func hasWindow(_ bookID: String) -> Bool {
        guard let window = open[bookID]?.window else { return false }
        return window.isVisible || window.isMiniaturized
    }

    @MainActor static func closeWindow(_ bookID: String) {
        open[bookID]?.window?.close()
        open[bookID] = nil
    }

    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        let bookID = bookID
        Task {
            guard let window = view.window else { return }
            if let other = Self.open[bookID]?.window, other !== window, other.isVisible || other.isMiniaturized {
                other.makeKeyAndOrderFront(nil)
                window.close()
                return
            }
            // A window switched to another book (搜索 · 问) no longer holds the one before.
            Self.open = Self.open.filter { $0.value.window != nil && $0.value.window !== window }
            Self.open[bookID] = WeakWindow(window: window)
        }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {}
}

/// A book keeps its own window size: it opens as the shelf does (with room for the
/// notes beside the column), and remembers how it was last sized.
private struct ReaderWindowShape: NSViewRepresentable {
    enum Kind: String {
        case book

        var standard: CGSize { CGSize(width: 1040, height: 720) }
    }

    let kind: Kind

    final class Coordinator {
        var observer: NSObjectProtocol?
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    static func dismantleNSView(_ nsView: NSView, coordinator: Coordinator) {
        coordinator.observer.map(NotificationCenter.default.removeObserver)
        coordinator.observer = nil
    }

    /// Tells when it is in a window: a moment later (as before) can be too soon.
    final class Host: NSView {
        var placed: ((NSWindow) -> Void)?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window, let placed else { return }
            self.placed = nil
            Task { placed(window) }
        }
    }

    func makeNSView(context: Context) -> NSView {
        let view = Host()
        let key = "window.size.\(kind.rawValue)", kind = kind
        view.placed = { window in
            let saved = UserDefaults.standard.string(forKey: key).map(NSSizeFromString) ?? kind.standard
            let frame = window.frame
            if !window.styleMask.contains(.fullScreen), let screen = window.screen?.visibleFrame,
               abs(frame.width - saved.width) > 1 || abs(frame.height - saved.height) > 1 {
                let width = min(saved.width, screen.width - 40), height = min(saved.height, screen.height - 40)
                window.setFrame(NSRect(x: frame.midX - width / 2, y: frame.maxY - height, width: width, height: height)
                    .intersection(screen), display: true)
            }
            context.coordinator.observer = NotificationCenter.default.addObserver(
                forName: NSWindow.didEndLiveResizeNotification, object: window, queue: .main
            ) { note in
                let window = note.object as? NSWindow
                MainActor.assumeIsolated {
                    guard let window, !window.styleMask.contains(.fullScreen) else { return }
                    UserDefaults.standard.set(NSStringFromSize(window.frame.size), forKey: key)
                }
            }
        }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {}
}
#endif

private struct ReaderContent: View {
    @AppStorage(ImmersivePrefs.statusKey) private var hidesStatusBar = true
    @Bindable var model: ReaderModel
    @Environment(\.dismiss) private var dismiss
    @Environment(\.scenePhase) private var scenePhase
    @State private var thought: Mark?
    @State private var translating: String?
    #if os(iOS)
    @State private var sharing: QuoteShare?
    /// 第一次进阅读器: where to tap, drawn once.
    @AppStorage("reader.tapHintShown") private var tapHintShown = false
    @State private var tapHint = false
    #endif

    private var paper: YYPaper { model.paperTokens }
    #if os(iOS)
    @Environment(\.horizontalSizeClass) private var sizeClass
    #endif

    var body: some View {
        pages
            .preferredColorScheme(paper.isDark ? .dark : .light)
            .onChange(of: scenePhase) { _, phase in
                if phase != .active { model.flush() }
            }
            .onChange(of: model.player?.thermalNotice) { _, notice in
                if let notice, !notice.isEmpty {
                    model.show(Library.Toast(icon: "thermometer.high", title: notice, detail: ""))
                }
            }
            .bookFailure($model.failure, opened: model.detail != nil)
            .yyNotice(String(localized: "朗读停下了"),
                      message: model.player?.failure ?? "",
                      isPresented: Binding(get: { model.player?.failure != nil }, set: { if !$0 { model.player?.failure = nil } }),
                      secondaryTitle: String(localized: "关上"),
                      primaryTitle: String(localized: "换成系统声音"),
                      onPrimary: { model.player?.useSystemVoice() })
    }

    #if os(iOS)
    private var pages: some View {
        pager
            .background(paper.bg.ignoresSafeArea())
            .overlay { if InnerDisplay.isOn(sizeClass) { SpreadNumbers(model: model) } }
            // VoiceOver: the taps on the page, as actions.
            .accessibilityAction(named: Text("下一页")) { model.turn(forward: true) }
            .accessibilityAction(named: Text("上一页")) { model.turn(forward: false) }
            .accessibilityAction(named: Text("菜单")) { model.chromeShown.toggle() }
            .overlay {
                if tapHint { PageTapHint(paper: paper).transition(.opacity).accessibilityHidden(true) }
            }
            .overlay {
                if model.listenOnly { ListenOnlyCover(model: model) }
            }
            .overlay { PhoneReaderChrome(model: model, close: { dismiss() }) }
            .overlay { cards.ignoresSafeArea() }
            .overlay(alignment: .bottom) {
                // 点词 / 双击一句 in a foreign book.
                if let lookup = model.lookup {
                    PhoneWordCard(model: model, lookup: lookup)
                        .modifier(SwipeDownDismiss(resetKey: lookup.word) { model.closeLookup() })
                        .padding(.horizontal, 12)
                        .padding(.bottom, 12)
                        .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
                } else if let sentence = model.sentenceTranslation {
                    PhoneSentenceCard(model: model, item: sentence)
                        .modifier(SwipeDownDismiss(resetKey: "\(sentence.sentence)") { model.sentenceTranslation = nil })
                        .padding(.horizontal, 12)
                        .padding(.bottom, 12)
                        .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
                }
            }
            .animation(YYMotion.standard, value: model.lookup?.word)
            .animation(YYMotion.standard, value: model.sentenceTranslation?.sentence)
            .overlay(alignment: .top) {
                if let toast = model.toast {
                    UndoToast(toast: toast, undo: { model.undoToast() }, dismiss: { model.toast = nil })
                        .padding(.horizontal, 16)
                        .padding(.top, model.chromeShown || model.searchShown ? 52 : 6)
                        .transition(UndoToast.transition)
                        .id(toast.id)
                }
            }
            .animation(YYMotion.standard, value: model.chromeShown || model.searchShown)
            .yyToastMotion(value: model.toast?.id)
            .animation(YYMotion.quick, value: model.selection)
            .animation(YYMotion.quick, value: model.openMark)
            .sheet(item: $thought) { mark in
                PhoneThoughtSheet(mark: mark) { text in
                    model.setNote(text, of: mark)
                    thought = nil
                } cancel: {
                    thought = nil
                }
            }
            .yySystemTranslation(presented: $translating, translator: model.translator)
            .statusBarHidden(model.immersive ? hidesStatusBar : !model.chromeShown)
            .persistentSystemOverlays(model.immersive && !model.chromeShown ? .hidden : .automatic)
            .modifier(SpeakerOthersSheet(model: model))
            .sheet(isPresented: $model.tocShown) {
                TocList(model: model)
                    .presentationDetents([.large])
            }
            .sheet(isPresented: $model.askingFinished) {
                if let library = model.library {
                    FinishedSheet(library: library, bookID: model.bookID) { model.askingFinished = false }
                        .presentationDetents([.height(380)])
                        .presentationBackground(YYColor.bg)
                }
            }
            .onChange(of: model.chromeShown) { _, shown in
                if shown { model.dismissCards() }
            }
            .sheet(isPresented: Binding(get: { model.panel == .companion }, set: { if !$0 { model.panel = nil } })) {
                PhoneCompanion(model: model)
                    .presentationDetents([.medium, .large])
                    .presentationBackgroundInteraction(.enabled(upThrough: .medium))
            }
            .sheet(item: $sharing) { PhoneQuoteShare(quote: $0) }
            .animation(.easeOut(duration: 0.2), value: model.bookmarkHere?.id)
            .onAppear {
                guard !tapHintShown, model.settings.mode == .paginated else { return }
                tapHintShown = true
                withAnimation(YYMotion.reduce ? nil : .easeOut(duration: 0.25)) { tapHint = true }
                Task {
                    try? await Task.sleep(for: .seconds(3))
                    withAnimation(YYMotion.reduce ? nil : .easeOut(duration: 0.2)) { tapHint = false }
                }
            }
    }

    /// iPhone Duo's inner display: two pages side by side, kept clear of the fold when half
    /// folded. The reader measures from the same full-screen rectangle as the pages.
    private var pager: some View {
        GeometryReader { g in
            let wide = InnerDisplay.isOn(sizeClass)
            // The pager lays out in unmirrored UIKit coordinates.
            PagerView(pager: model.pager, spread: wide, fold: wide ? InnerDisplay.fold(in: g, layoutDirectionBehavior: .fixed) : nil)
                .ignoresSafeArea()
        }
        .ignoresSafeArea()
    }

    /// 选中文字, or a highlight tapped: the same card as on the Mac, over the words.
    @ViewBuilder private var cards: some View {
        if let selection = model.selection {
            GeometryReader { g in
                SelectionCard(model: model, current: nil, thought: {
                    if let mark = model.mark() { thought = mark }
                }, translate: { translate(selection.text) }, ask: { askCompanion(selection.text) },
                share: { share(selection.text, note: nil, section: selection.section) })
                .fixedSize()
                .modifier(Above(rect: selection.rect, in: g.size))
            }
            .transition(.opacity)
        } else if let open = model.openMark {
            GeometryReader { g in
                SelectionCard(model: model, current: open.mark, thought: {
                    thought = open.mark
                    model.dismissCards()
                }, translate: { translate(open.mark.text) }, ask: { askCompanion(open.mark.text) },
                share: { share(open.mark.text, note: open.mark.note, section: open.mark.section) })
                .fixedSize()
                .modifier(Above(rect: CGRect(x: open.at.x - 1, y: open.at.y - 12, width: 2, height: 24), in: g.size))
            }
            .transition(.opacity)
        } else if let pick = model.speakerPick {
            GeometryReader { g in
                ZStack(alignment: .topLeading) {
                    Color.black.opacity(0.001).contentShape(.rect).onTapGesture { model.speakerPick = nil }
                    SpeakerCard(pick: pick, choose: { model.setSpeaker($0, section: pick.section, line: pick.line) },
                                others: { model.speakerOthers = pick; model.speakerPick = nil }, voice: nil)
                        .modifier(AtPoint(point: pick.at, size: g.size))
                }
            }
            .transition(.opacity)
        }
    }

    private func translate(_ text: String) {
        model.dismissCards()
        translating = text
    }

    /// 问: the words chosen go along to 书伴.
    private func askCompanion(_ quote: String?) {
        model.dismissCards()
        if let quote { model.companionQuote = quote }
        model.chromeShown = false
        model.panel = .companion
    }

    private func share(_ text: String, note: String?, section: UInt32) {
        let summary = model.detail?.summary
        let chapter = model.detail?.chapterTitle(for: section)
        sharing = QuoteShare(text: text, note: note, title: summary?.title ?? "", author: summary?.authors.first,
                             chapter: chapter == summary?.title ? nil : chapter, coverPath: summary?.coverPath)
        model.dismissCards()
    }

    #else
    private var pages: some View {
        MacReaderView(model: model)
    }
    #endif
}

#if os(iOS)
struct PagerView: UIViewRepresentable {
    let pager: ReaderPager
    /// iPhone Duo's inner display: two pages side by side.
    var spread = false
    /// Half folded: the fold, in the pager's coordinates.
    var fold: CGRect?
    func makeUIView(context: Context) -> ReaderPager { pager }
    func updateUIView(_ view: ReaderPager, context: Context) {
        view.spread = spread
        view.fold = fold
    }
}

/// iPhone Duo's inner display: the spread's page numbers, one under each page (12, secondary).
/// The core counts a spread as one page; the left one is its odd half.
private struct SpreadNumbers: View {
    let model: ReaderModel
    @AppStorage(ImmersivePrefs.progressKey) private var hidesProgress = true

    var body: some View {
        if model.settings.mode == .paginated, !model.pager.chat, !(model.immersive && hidesProgress),
           let page = model.bookPage {
            let paper = model.paperTokens
            HStack(spacing: 0) {
                ForEach([page * 2 - 1, page * 2], id: \.self) { n in
                    Text(verbatim: "\(n)")
                        .font(.yy(size: 12).monospacedDigit())
                        .foregroundStyle(paper.ink2)
                        .frame(maxWidth: .infinity)
                }
            }
            .frame(maxHeight: .infinity, alignment: .bottom)
            // One row, 8 above the read-along bar (24 above the safe area, 40 tall), so the
            // right page's number isn't under it; the text ends above at spreadBottom (90).
            .padding(.bottom, 72)
            // Halves of the whole screen, as the pages are.
            .ignoresSafeArea(edges: .horizontal)
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }
}

/// iPhone Duo's inner display: two panes, one each side. Half folded, the line between them is
/// the fold and each pane keeps 24 from it; flat, the line is the screen's middle with a 32 gap.
/// Measured over the whole screen, so the tab bar's lopsided safe area doesn't move the line.
/// The panes keep the safe area at the outer edges.
struct FoldSplit<Leading: View, Trailing: View>: View {
    @ViewBuilder var leading: Leading
    @ViewBuilder var trailing: Trailing

    var body: some View {
        GeometryReader { g in
            let full = g.size.width + g.safeAreaInsets.leading + g.safeAreaInsets.trailing
            let fold = InnerDisplay.fold(in: g, layoutDirectionBehavior: .fixed)
                .map { $0.offsetBy(dx: g.safeAreaInsets.leading, dy: 0) }
            // In whole-screen coordinates: where the left pane ends and the right one starts.
            let leftEnd = fold.map { $0.minX - 24 } ?? (full / 2 - 16)
            let rightStart = fold.map { $0.maxX + 24 } ?? (full / 2 + 16)
            HStack(spacing: 0) {
                leading
                    .frame(width: max(0, leftEnd - g.safeAreaInsets.leading))
                Color.clear.frame(width: max(0, rightStart - leftEnd))
                trailing
                    .frame(width: max(0, full - g.safeAreaInsets.trailing - rightStart))
            }
            .frame(maxHeight: .infinity)
        }
    }
}

/// iPhone Duo's inner display: an iPhone at regular width. iPad keeps its own pages.
@MainActor enum InnerDisplay {
    static func isOn(_ sizeClass: UserInterfaceSizeClass?) -> Bool {
        sizeClass == .regular && UIDevice.current.userInterfaceIdiom == .phone
    }

    /// Half folded like a book: the fold, in the coordinates of the view `proxy` measures. nil
    /// flat open, folded like a laptop, and on every other iPhone.
    static func fold(in proxy: GeometryProxy, layoutDirectionBehavior: LayoutDirectionBehavior = .mirrors) -> CGRect? {
        #if compiler(>=6.4)
        if #available(iOS 27.1, *) {
            guard let frame = proxy.reservedRegions(kind: .division, layoutDirectionBehavior: layoutDirectionBehavior).first?.frame,
                  frame.height > frame.width
            else { return nil }
            return frame
        }
        #endif
        return nil
    }
}
#else
struct PagerView: NSViewRepresentable {
    let pager: ReaderPager
    func makeNSView(context: Context) -> ReaderPager { pager }
    func updateNSView(_ view: ReaderPager, context: Context) {}
}
#endif

/// 目录: the book on top, then 目录 · 书签 · 笔记. The chapter being read sits in
/// the middle in the accent, with a line for how far into it; the ones read are faded; each says
/// about how long it takes.
private struct TocList: View {
    let model: ReaderModel
    @Environment(\.dismiss) private var dismiss
    @State private var tab = Tab.toc
    @State private var thought: Mark?
    private let tint = YYTint.standard

    private enum Tab { case toc, bookmarks, notes }

    var body: some View {
        let chapters = model.tocChapters
        let current = model.currentChapter(in: chapters)
        VStack(alignment: .leading, spacing: YYUI.s(12)) {
            header(chapters, current)
                .padding(.horizontal, YYUI.s(16))
            YYSegments(items: [(Tab.toc, String(localized: "目录")), (.bookmarks, String(localized: "书签")), (.notes, String(localized: "笔记"))], selection: $tab,
                       counts: [.bookmarks: model.bookmarks.count, .notes: model.marks.count].filter { $0.value > 0 })
                .padding(.horizontal, YYUI.s(14))
            ZStack(alignment: .top) {
                Group {
                    switch tab {
                    case .toc: toc(chapters, current)
                    case .bookmarks: bookmarks
                    case .notes: notes
                    }
                }
                .id(tab)
                .transition(YYMotion.swap)
            }
            .frame(maxHeight: .infinity, alignment: .top)
            .animation(YYMotion.standard, value: tab)
        }
        #if os(iOS)
        .padding(.top, YYUI.sheetGrabberTop)
        #else
        .padding(.top, YYUI.s(20))
        #endif
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(YYColor.bg)
        #if os(iOS)
        .presentationDragIndicator(.visible)
        #endif
        .sheet(item: $thought) { mark in
            #if os(iOS)
            PhoneThoughtSheet(mark: mark) { text in
                model.setNote(text, of: mark)
                thought = nil
            } cancel: {
                thought = nil
            }
            #endif
        }
    }

    /// The cover, the book, where the reader is in it.
    private func header(_ chapters: [TocChapter], _ current: Int?) -> some View {
        let book = model.detail?.summary
        let at = current.flatMap { chapters[safe: $0] }.map { c in ChapterTitle(c.title).number ?? c.title }
        return HStack(spacing: YYUI.s(11)) {
            if let book { BookCover(book: book).frame(width: YYUI.s(30)) }
            VStack(alignment: .leading, spacing: YYUI.s(3)) {
                Text(book?.title ?? String(localized: "目录")).font(YYUI.font(14, .bold)).foregroundStyle(YYColor.t1)
                Text([book?.authors.first, at.map { String(localized: "读到\($0)") }, String(localized: "全书 \(Int((model.bookFraction * 100).rounded()))%")].compactMap { $0 }.joined(separator: " · "))
                    .font(YYUI.font(10)).monospacedDigit().foregroundStyle(YYColor.t3)
            }
            .lineLimit(1)
            Spacer(minLength: 0)
            Button { model.toggleBookmark() } label: {
                Image(systemName: model.bookmarkHere == nil ? "bookmark" : "bookmark.fill")
                    .font(YYUI.font(11.5, .semibold))
                    .foregroundStyle(model.bookmarkHere == nil ? YYColor.t1 : tint.accent)
                    .contentTransition(.symbolEffect(.replace))
                    .frame(width: YYUI.s(26), height: YYUI.s(26))
                    .background(model.bookmarkHere == nil ? YYColor.inset : tint.accentSoft, in: .circle)
                    .animation(YYMotion.quick, value: model.bookmarkHere == nil)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(model.bookmarkHere == nil ? String(localized: "加书签") : String(localized: "拿掉书签"))
            Button { dismiss() } label: {
                Image(systemName: "xmark").font(YYUI.font(10, .semibold)).foregroundStyle(YYColor.t1)
                    .frame(width: YYUI.s(26), height: YYUI.s(26))
                    .background(YYColor.inset, in: .circle)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("关闭")
        }
    }

    // MARK: 目录

    @ViewBuilder private func toc(_ chapters: [TocChapter], _ current: Int?) -> some View {
        if chapters.isEmpty {
            empty(String(localized: "这本书没有目录"), String(localized: "书里没写章节，往后翻就行"))
        } else {
            tocList(chapters, current)
        }
    }

    private func tocList(_ chapters: [TocChapter], _ current: Int?) -> some View {
        let main = chapters.filter { !$0.isBoilerplate }
        let appendix = chapters.filter(\.isBoilerplate)
        let numberWidth = ChapterTitle.columnWidth(for: (main.isEmpty ? chapters : main).map(\.title), minWidth: 46)
        return ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: YYUI.s(14)) {
                    if !main.isEmpty {
                        LazyVStack(spacing: 0) {
                            ForEach(Array(main.enumerated()), id: \.element.id) { index, c in
                                if index > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                                chapterRow(c, current: current, numberWidth: numberWidth)
                                    .id(c.id)
                            }
                        }
                        .padding(.horizontal, YYUI.s(12))
                        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
                        .overlay(RoundedRectangle(cornerRadius: YYUI.s(14)).strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    if !appendix.isEmpty {
                        VStack(alignment: .leading, spacing: YYUI.s(6)) {
                            Text("附录与版权")
                                .font(YYUI.font(10.5))
                                .foregroundStyle(YYColor.t3)
                                .padding(.horizontal, YYUI.s(6))
                            LazyVStack(spacing: 0) {
                                ForEach(Array(appendix.enumerated()), id: \.element.id) { index, c in
                                    if index > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                                    boilerplateRow(c, current: current)
                                        .id(c.id)
                                }
                            }
                            .padding(.horizontal, YYUI.s(12))
                            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
                            .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
                        }
                    }
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.bottom, YYUI.s(20))
            }
            .scrollIndicators(.never)
            .onAppear {
                if let current { proxy.scrollTo(current, anchor: .center) }
            }
        }
    }

    private func boilerplateRow(_ c: TocChapter, current: Int?) -> some View {
        let now = c.id == current
        return Button {
            model.go(to: c.item)
            dismiss()
        } label: {
            HStack(spacing: YYUI.s(8)) {
                Image(systemName: "doc.text")
                    .font(YYUI.font(10.5))
                    .foregroundStyle(now ? tint.accent : YYColor.t3)
                Text(c.title)
                    .font(YYUI.font(11, now ? .semibold : .regular))
                    .foregroundStyle(now ? tint.accent : YYColor.t2)
                    .lineLimit(1)
                Spacer(minLength: YYUI.s(6))
                Text("版权")
                    .font(YYUI.font(9.5))
                    .foregroundStyle(now ? tint.accent : YYColor.t3)
                    .padding(.horizontal, YYUI.s(6))
                    .padding(.vertical, YYUI.s(2))
                    .background(now ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: YYUI.s(5)))
            }
            .padding(.vertical, YYUI.s(9))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    /// 第几回, the name, the minutes; this one in the accent, its name whole, a line under.
    private func chapterRow(_ c: TocChapter, current: Int?, numberWidth: CGFloat) -> some View {
        let now = c.id == current
        let done = (current ?? -1) > c.id
        let t = ChapterTitle(c.title)
        let color = now ? tint.accent : YYColor.t1
        let minutes = model.minutes(of: c)
        let note = now ? String(localized: "还剩 \(max(1, Int((Double(minutes) * (1 - model.within(c))).rounded()))) 分钟") : String(localized: "\(minutes) 分钟")
        return Button {
            model.go(to: c.item)
            dismiss()
        } label: {
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(6)) {
                if let number = t.number {
                    Text(number)
                        .font(YYUI.font(10.5, now ? .semibold : .regular))
                        .foregroundStyle(now ? tint.accent : YYColor.t3)
                        .lineLimit(1)
                        .frame(width: YYUI.s(numberWidth), alignment: .leading)
                }
                VStack(alignment: .leading, spacing: YYUI.s(8)) {
                    HStack(alignment: .firstTextBaseline, spacing: YYUI.s(6)) {
                        Text(t.name)
                            .font(YYUI.font(11.5, now ? .semibold : .regular))
                            .foregroundStyle(done ? YYColor.t3 : color)
                            .lineLimit(now ? nil : 1)
                            .lineSpacing(now ? YYUI.s(11.5) * 0.6 : 0)
                            .multilineTextAlignment(.leading)
                            .padding(.leading, CGFloat(c.depth) * YYUI.s(12))
                        Spacer(minLength: 0)
                        Text(note)
                            .font(YYUI.font(10, now ? .semibold : .regular)).monospacedDigit()
                            .foregroundStyle(now ? tint.accent : YYColor.t3)
                            .lineLimit(1)
                            .fixedSize()
                    }
                    if now { ProgressLine(value: model.within(c), height: YYUI.s(3)) }
                }
            }
            .padding(.vertical, YYUI.s(11))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    // MARK: 书签 · 笔记

    /// The same white cards, kept as a list so a row can be swiped away.
    private func cards(@ViewBuilder _ rows: () -> some View) -> some View {
        List { Section { rows() } }
            #if os(iOS)
            .listStyle(.insetGrouped)
            .listSectionSpacing(0)
            .contentMargins(.top, 0, for: .scrollContent)
            #endif
            .scrollContentBackground(.hidden)
    }

    @ViewBuilder private var bookmarks: some View {
        if model.bookmarks.isEmpty {
            empty(String(localized: "还没有书签"), String(localized: "点右上角的书签键，就给这一页加上书签"), action: (String(localized: "给这一页加书签"), { model.toggleBookmark() }))
        } else {
            cards {
                Button { model.toggleBookmark() } label: {
                    HStack(spacing: YYUI.s(8)) {
                        Image(systemName: model.bookmarkHere == nil ? "bookmark" : "bookmark.fill")
                            .font(YYUI.font(12, .semibold))
                        Text(model.bookmarkHere == nil ? String(localized: "给这一页加书签") : String(localized: "拿掉这一页的书签"))
                            .font(YYUI.font(12, .semibold))
                    }
                    .foregroundStyle(tint.accent)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                ForEach(model.bookmarks, id: \.id) { mark in
                    Button {
                        model.jump(section: mark.section, sentence: Int(mark.sentence))
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: YYUI.s(4)) {
                            HStack(spacing: YYUI.s(5)) {
                                Image(systemName: "bookmark.fill").font(YYUI.font(9)).foregroundStyle(tint.accent)
                                Text(place(section: mark.section, sentence: Int(mark.sentence))).lineLimit(1)
                                Spacer()
                                Text(mark.at.dayText)
                            }
                            .font(YYUI.font(10)).monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                            Text(mark.text)
                                .font(YYUI.font(11.5))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(2)
                        }
                        .padding(.vertical, YYUI.s(3))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .swipeActions {
                        Button("删除", role: .destructive) { model.deleteBookmark(mark) }
                    }
                }
            }
        }
    }

    @ViewBuilder private var notes: some View {
        if model.marks.isEmpty {
            empty(String(localized: "还没有划线"), String(localized: "长按选中文字，点一个颜色就划上"))
        } else {
            cards {
                ForEach(model.marks, id: \.id) { mark in
                    Button {
                        model.go(to: mark)
                        dismiss()
                    } label: {
                        VStack(alignment: .leading, spacing: YYUI.s(5)) {
                            MarkText(mark: mark)
                                .font(YYUI.font(12.5, design: .serif))
                                .lineSpacing(YYUI.s(12.5) * 0.7)
                                .foregroundStyle(YYColor.t1)
                            if let note = mark.note {
                                Text(note)
                                    .font(.custom("Kaiti SC", size: YYUI.s(12)))
                                    .foregroundStyle(tint.accent)
                                    .padding(.leading, YYUI.s(9))
                                    .overlay(alignment: .leading) { Rectangle().fill(tint.accent).frame(width: 1.5) }
                            }
                            Text(place(section: mark.section, sentence: Int(mark.start.sentence)))
                                .font(YYUI.font(10)).monospacedDigit()
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        .padding(.vertical, YYUI.s(4))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .contextMenu {
                        Button(mark.note == nil ? String(localized: "写想法") : String(localized: "改想法"), systemImage: "pencil") { thought = mark }
                        Menu("换个样子", systemImage: "paintpalette") {
                            ForEach(MarkStyle.colours + MarkStyle.lines, id: \.self) { style in
                                Button(style.name) { model.setStyle(style, of: mark) }
                            }
                        }
                        Button("删除划线", systemImage: "trash", role: .destructive) { model.deleteMark(mark) }
                    }
                    .swipeActions {
                        Button("删除", role: .destructive) { model.deleteMark(mark) }
                    }
                }
            }
        }
    }

    /// "第三回 · 第 3 页", or the chapter when the pages are not counted yet.
    private func place(section: UInt32, sentence: Int) -> String {
        let chapter = model.detail?.chapterTitle(for: section)
        let page = model.estimatedPage(section: section, sentence: sentence).map { String(localized: "第 \($0) 页") }
        return [chapter, page].compactMap { $0 }.joined(separator: " · ")
    }

    private func empty(_ title: String, _ detail: String, action: (title: String, run: () -> Void)? = nil) -> some View {
        VStack(spacing: YYUI.s(5)) {
            Text(title).font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t2)
            Text(detail).font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            if let action {
                Button(action.title, action: action.run)
                    .buttonStyle(YYButtonStyle(kind: .main))
                    .padding(.top, YYUI.s(10))
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

#if os(iOS)
/// 第一次进阅读器: three faint zones — 上一页, 菜单, 下一页 — gone after a few seconds.
private struct PageTapHint: View {
    let paper: YYPaper

    var body: some View {
        HStack(spacing: 0) {
            zone(String(localized: "上一页"), "chevron.left", 0.3)
            zone(String(localized: "菜单"), "hand.tap", 0.4)
            zone(String(localized: "下一页"), "chevron.right", 0.3)
        }
        .ignoresSafeArea()
        .allowsHitTesting(false)
    }

    private func zone(_ title: String, _ symbol: String, _ share: CGFloat) -> some View {
        GeometryReader { g in
            VStack(spacing: 8) {
                Image(systemName: symbol).font(.yy(size: 20, weight: .medium))
                Text(title).font(.yy(size: 14, weight: .semibold))
            }
            .foregroundStyle(paper.ink.opacity(0.7))
            .frame(width: g.size.width, height: g.size.height)
            .background(paper.ink.opacity(title == String(localized: "菜单") ? 0.07 : 0.035))
            .overlay(alignment: .trailing) {
                if title != String(localized: "下一页") {
                    Rectangle().fill(paper.ink.opacity(0.12)).frame(width: 0.5)
                }
            }
        }
        .containerRelativeFrame(.horizontal) { w, _ in w * share }
    }
}

/// 1:1 downward finger tracking with upward rubber-band for bottom lookup/sentence cards.
private struct SwipeDownDismiss: ViewModifier {
    let resetKey: String
    let dismiss: () -> Void
    @State private var dragY: CGFloat = 0

    func body(content: Content) -> some View {
        content
            .offset(y: YYMotion.reduce ? 0 : (dragY >= 0 ? dragY : dragY * 0.18))
            .onChange(of: resetKey) { dragY = 0 }
            .gesture(
                DragGesture(minimumDistance: 12)
                    .onChanged { value in
                        guard !YYMotion.reduce else { return }
                        dragY = value.translation.height
                    }
                    .onEnded { value in
                        guard !YYMotion.reduce else {
                            if value.translation.height > 64 {
                                withAnimation(YYMotion.exit) { dismiss() }
                            }
                            return
                        }
                        if value.translation.height > 64 || value.predictedEndTranslation.height > 140 {
                            withAnimation(YYMotion.exit) { dismiss() }
                        } else {
                            withAnimation(YYMotion.standard) { dragY = 0 }
                        }
                    }
            )
    }
}
#endif
