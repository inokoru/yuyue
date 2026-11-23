#if os(iOS)
import SwiftUI
import YuyueCore

/// The phone: 书架 · 笔记 · 我的, and 搜索 in its own circle (iOS 26 and later draw them as a floating
/// glass bar). A book opens over all of them, full screen; while one is read aloud with its
/// pages closed, a 播放条 sits above the tabs.
struct PhoneRoot: View {
    @Bindable var library: Library
    @State private var tab = PhoneTab.shelf
    @State private var chrome = PhoneChrome()
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage("phone.lastBook") private var lastBook = ""
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    @State private var reopened = false
    /// 重新导入 from the card when a book could not be opened.
    @State private var importing = false
    @State private var signClosed = false
    @Environment(\.scenePhase) private var phase
    @Environment(\.colorScheme) private var scheme
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    enum PhoneTab: Hashable { case shelf, notes, me, search }

    var body: some View {
        // 书架 tapped while already there: the shelf's places stand up over it.
        TabView(selection: Binding(get: { tab }, set: { new in
            if new == .shelf, tab == .shelf {
                let closing = chrome.switching
                withAnimation(closing ? PhoneChrome.dismissMotion : PhoneChrome.motion) { chrome.switching = !closing }
            } else {
                chrome.switching = false
            }
            if new == .notes, tab == .notes { chrome.notesPop &+= 1 }
            if new == .me, tab == .me { chrome.mePop &+= 1 }
            tab = new
        })) {
            Tab(value: .shelf) {
                PhoneShelf(library: library).nowPlayingBar(library, allowPodcast: false)
            } label: { shelfLabel }
            Tab(value: .notes) {
                PhoneNotes(library: library).nowPlayingBar(library, allowPodcast: true)
            } label: { tabLabel("笔记", .notes, line: "tab-notes", fill: "tab-notes-fill") }
            Tab(value: .me) {
                PhoneMe(library: library).nowPlayingBar(library, allowPodcast: true)
            } label: { meTab }
            // 搜索 in its own circle on the right, only the icon. iOS 27 no longer sets the search
            // tab apart by itself; the tab that stands apart is the `.prominent` one.
            Tab(value: .search, role: Self.searchRole) {
                PhoneSearch(library: library).nowPlayingBar(library, allowPodcast: true)
            } label: { searchLabel }
        }
        .environment(chrome)
        .tint(tint.accent)
        .preferredColorScheme(mode.scheme)
        .fullScreenCover(item: Binding(get: { library.presented.map(Presented.init) }, set: { library.presented = $0?.id })) { book in
            ReaderScreen(library: library, bookID: book.id)
                .id(book.id)
                .presentationBackground(YYColor.bg)
                .onAppear { lastBook = book.id }
                .onDisappear { if library.presented == nil { lastBook = "" } }
        }
        .fullScreenCover(item: Binding(
            get: { library.podcastHub.requestedOpenIssueID.map(Presented.init) },
            set: { library.podcastHub.requestedOpenIssueID = $0?.id }
        )) { item in
            PodcastReaderView(library: library, issueID: item.id) {
                library.podcastHub.requestedOpenIssueID = nil
            }
            .presentationBackground(YYColor.bg)
        }
        .onOpenURL { url in
            Task { await library.importFiles([url]) }
        }
        // One place for every tab: 书架, 我的 › 书库来源, 数据快照….
        .yyNotice(String(localized: "出了点问题"), message: library.alert ?? "", tone: .warn,
                  isPresented: Binding(get: { library.alert != nil }, set: { if !$0 { library.alert = nil } }))
        .bookBlocked(library) { importing = true }
        .fileImporter(isPresented: $importing, allowedContentTypes: Library.importTypes, allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { Task { await library.importFiles(urls) } }
        }
        .onChange(of: phase, initial: true) { _, now in
            if now == .active {
                Task { await library.watched.scan(into: library) }
                Task { await Feedback.shared.flush() }
            }
        }
        .openingSnapshots(library)
        .overlay(alignment: .top) {
            if let note = library.toast {
                UndoToast(toast: note, undo: { library.undo() })
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    .transition(UndoToast.transition)
                    .id(note.id)
            }
        }
        .yyToastMotion(value: library.toast?.id)
        .audiobookSheets(library, active: library.presented == nil)
        .recordingImportSheet(library)
        .task {
            Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(700))
                ReaderPager.prewarmWebKitIfNeeded()
            }
            UpdateCenter.shared.boot()
            await library.sync.keepUp()
        }
        .sheet(isPresented: Binding(get: { UpdateCenter.shared.signatureWarn && !signClosed }, set: { if !$0 { signClosed = true } })) {
            PhoneSignature()
        }
        .onAppear {
            // 上次停在书页上，打开 App 就直接回到那一页.
            // 最近改过 notices changes from launch on, and writes reading settings back through the core.
            SettingsLog.shared.library = library
            guard !reopened else { return }
            reopened = true
            // 设置 · 通用 · 打开语阅时接着读上次那本, as on the Mac; off, the shelf.
            if UserDefaults.standard.bool(forKey: "general.reopenLast"), !lastBook.isEmpty,
               let book = library.books.first(where: { $0.id == lastBook && $0.available }) {
                library.open(book, nil)
            }
            #if DEBUG
            // `-debugMe companion` opens 我的 on that page (see PhoneMe.debugPage).
            if UserDefaults.standard.string(forKey: "debugMe") != nil { tab = .me }
            // `-debugTab notes` opens on that tab.
            switch UserDefaults.standard.string(forKey: "debugTab") {
            case "notes": tab = .notes
            case "me": tab = .me
            case "search": tab = .search
            default: break
            }
            if let title = UserDefaults.standard.string(forKey: "debugOpen"),
               let book = library.books.first(where: { $0.title.contains(title) }) {
                library.open(book, nil)
            }
            #endif
        }
    }

    private struct Presented: Identifiable { let id: String }

    /// Our own line icons: outlined, and the `-f` filled one on the chosen tab. 书架's filled one
    /// carries a small ⌃⌄: tap it again and the shelf's places stand up over it.
    private func tabLabel(_ title: LocalizedStringKey, _ value: PhoneTab, line: String, fill: String) -> some View {
        Label(title, image: tab == value ? fill : line)
    }

    /// 搜索 stands in its own circle, where the system puts no pad under the chosen tab: chosen,
    /// the icon brings its own, as soft as the others'.
    @ViewBuilder private var searchLabel: some View {
        if #available(iOS 27.0, *), tab == .search {
            Label { Text("搜索") } icon: { Self.padded("tab-search-fill", ink: tint.accent, dark: scheme == .dark) }
        } else {
            tabLabel("搜索", .search, line: "tab-search", fill: "tab-search-fill")
        }
    }

    private static func padded(_ name: String, ink: Color, dark: Bool) -> Image {
        let renderer = ImageRenderer(content:
            // As the system's pad under the other chosen tabs: almost the whole circle, a soft
            // grey lit a little from above, a hairline of light round the top.
            ZStack {
                Circle().fill(LinearGradient(colors: dark ? [Color(white: 0.30), Color(white: 0.24)] : [Color(white: 0.945), Color(white: 0.905)],
                                             startPoint: .top, endPoint: .bottom))
                Circle().strokeBorder(LinearGradient(colors: [.white.opacity(dark ? 0.18 : 0.9), .clear], startPoint: .top, endPoint: .center), lineWidth: 0.6)
                Image(name).renderingMode(.template).foregroundStyle(ink)
            }
            .frame(width: 54, height: 54)
        )
        renderer.scale = 3
        return renderer.uiImage.map { Image(uiImage: $0.withRenderingMode(.alwaysOriginal)) } ?? Image(name)
    }

    /// 书架, or where the shelf is now (进度, 有声, 播客, a 合集) with its symbol. Chosen, the
    /// icon carries a small ⌃⌄: tap it again and the places stand up over it.
    @ViewBuilder private var shelfLabel: some View {
        let on = tab == .shelf
        if let symbol = chrome.shelfSymbol, let name = chrome.shelfName {
            Label {
                Text(name)
            } icon: {
                if on { Self.switchIcon(symbol) } else { Image(systemName: symbol) }
            }
        } else {
            Label("书架", image: on ? "tab-shelf-switch" : "tab-shelf-line")
        }
    }

    /// An SF symbol with the small ⌃⌄ beside it, drawn once into a template image for the tab bar.
    private static func switchIcon(_ symbol: String) -> Image {
        if let made = switchIcons[symbol] { return made }
        let renderer = ImageRenderer(content:
            // The ⌃⌄ hangs to the right; as much nothing on the left keeps the symbol over its name.
            HStack(spacing: 2.5) {
                Color.clear.frame(width: 6)
                Image(systemName: UIImage(systemName: symbol + ".fill") != nil ? symbol + ".fill" : symbol).font(.system(size: 19, weight: .medium))
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 7.5, weight: .bold))
                    .frame(width: 6)
            }
            .foregroundStyle(.black)
            .frame(height: 24)
        )
        renderer.scale = 3
        let image = renderer.uiImage.map { Image(uiImage: $0.withRenderingMode(.alwaysTemplate)) } ?? Image(systemName: symbol)
        switchIcons[symbol] = image
        return image
    }
    @MainActor private static var switchIcons: [String: Image] = [:]

    /// 我的, with a red dot when a new version is waiting. The shelf itself stays clear of it.
    private var meTab: some View {
        Label {
            Text("我的")
        } icon: {
            Image(tab == .me ? "tab-me-fill" : "tab-me")
                .overlay(alignment: .topTrailing) {
                    if UpdateCenter.shared.showsBadge {
                        Circle().fill(YYColor.danger).frame(width: 7, height: 7).offset(x: 2, y: -2)
                    }
                }
        }
    }

    private static var searchRole: TabRole {
        #if compiler(>=6.4)
        if #available(iOS 27.0, *) { return .prominent }
        #endif
        return .search
    }
}

extension View {
    /// The 播放条 at the bottom of a tab, while a book or podcast carries on with its pages closed.
    func nowPlayingBar(_ library: Library, allowPodcast: Bool = false) -> some View {
        safeAreaInset(edge: .bottom, spacing: 0) {
            if let playing = library.nowPlaying, let book = library.books.first(where: { $0.id == playing.bookID }) {
                NowPlayingBar(playing: playing, book: book) { library.presented = book.id }
                    .padding(.horizontal, 16)
                    .padding(.bottom, 8)
                    .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
            } else if allowPodcast, library.podcastHub.isPlaying, let activeEp = library.podcastHub.activeIssue {
                PhonePodcastMiniPlayerBar(library: library, issue: activeEp) {
                    library.podcastHub.requestedOpenIssueID = activeEp.id
                }
                .padding(.horizontal, 16)
                .padding(.bottom, 8)
                .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
            }
        }
        .animation(YYMotion.standard, value: library.nowPlaying?.bookID)
        .animation(YYMotion.standard, value: library.podcastHub.isPlaying)
    }
}

/// Cover, the sentence being read, ‹ ▶ ›, and a thin line for the chapter underneath. A tap
/// anywhere else goes back to the page.
struct NowPlayingBar: View {
    let playing: any NowPlaying
    let book: BookSummary
    let open: () -> Void
    private let tint = YYTint.standard
    /// 有声书: −15 s / +30 s, and the time.
    private var audio: AudiobookPlayer? { playing as? AudiobookPlayer }

    var body: some View {
        HStack(spacing: 10) {
            BookCover(book: book)
                .frame(width: 30)
            VStack(alignment: .leading, spacing: 2) {
                Text(audio.map { $0.title($0.chapter) } ?? playing.player?.line ?? book.title)
                    .font(.footnote.weight(.medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                Text(audio.map { "\(book.title) · \(AudiobookPlayer.clock($0.inChapter.at)) / \(AudiobookPlayer.clock($0.inChapter.of))" } ?? book.title)
                    .font(.caption2.monospacedDigit())
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            HStack(spacing: 0) {
                if let audio {
                    Button("后退 15 秒", systemImage: "gobackward.15") { audio.skip(-AudiobookPlayer.back) }
                        .frame(width: 44, height: 44)
                        .contentShape(.rect)
                } else {
                    Button("上一句", systemImage: "backward.end.fill") { playing.player?.skip(forward: false) }
                        .frame(width: 44, height: 44)
                        .contentShape(.rect)
                }
                Button(playing.isPlaying ? String(localized: "暂停") : String(localized: "接着听"), systemImage: playing.isPlaying ? "pause.fill" : "play.fill") {
                    playing.togglePlay()
                }
                .contentTransition(.symbolEffect(.replace))
                .animation(YYMotion.quick, value: playing.isPlaying)
                .font(.title3)
                .frame(width: 44, height: 44)
                .contentShape(.rect)
                if let audio {
                    Button("前进 30 秒", systemImage: "goforward.30") { audio.skip(AudiobookPlayer.ahead) }
                        .frame(width: 44, height: 44)
                        .contentShape(.rect)
                } else {
                    Button("下一句", systemImage: "forward.end.fill") { playing.player?.skip(forward: true) }
                        .frame(width: 44, height: 44)
                        .contentShape(.rect)
                }
            }
        }
        .labelStyle(.iconOnly)
        .buttonStyle(YYPressStyle(scale: 0.92))
        .foregroundStyle(YYColor.t1)
        .padding(.leading, 10)
        .padding(.trailing, 8)
        .padding(.vertical, 7)
        .overlay(alignment: .bottom) {
            GeometryReader { g in
                Capsule().fill(tint.accent)
                    .frame(width: g.size.width * (audio.map { $0.inChapter.of > 0 ? $0.inChapter.at / $0.inChapter.of : 0 } ?? playing.player?.sectionProgress ?? 0), height: 2)
            }
            .frame(height: 2)
            .padding(.horizontal, 22)
            .padding(.bottom, 2)
        }
        .contentShape(.capsule)
        .onTapGesture(perform: open)
        .modifier(CapsuleGlass())
        .accessibilityElement(children: .contain)
        .accessibilityLabel("正在听《\(book.title)》")
        .accessibilityAction(named: Text("回到正在读的页"), open)
    }
}
#endif
