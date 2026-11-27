#if os(iOS)
import SwiftUI
import YuyueCore

/// The phone: customizable bottom tabs (default 书架 · 笔记 · 我的 · 搜索, iOS 26+ draws them as
/// a floating glass bar). A book opens over all of them, full screen; while one is read aloud with
/// its pages closed, a 播放条 sits above the tabs.
struct PhoneRoot: View {
    @Bindable var library: Library
    @AppStorage(BottomNavPrefs.key) private var rawTabs = BottomNavPrefs.defaultRaw
    @AppStorage(BottomNavPrefs.shelfHiddenKey) private var rawHiddenShelfSubs = BottomNavPrefs.defaultHiddenShelfRaw
    @State private var tab: PhoneTab
    @State private var chrome = PhoneChrome()
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage("general.reopenLast") private var reopenLast = true
    @AppStorage("phone.lastBook") private var lastBook = ""
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    @State private var reopened = false
    /// 重新导入 from the card when a book could not be opened.
    @State private var importing = false
    @State private var signClosed = false
    @Environment(\.scenePhase) private var phase
    @Environment(\.colorScheme) private var scheme
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var navConfig: BottomNavConfig { BottomNavPrefs.config(tabsRaw: rawTabs, hiddenShelfRaw: rawHiddenShelfSubs) }
    private var navTabs: [PhoneTab] { navConfig.tabs }

    init(library: Library) {
        self.library = library
        let initialTabs = BottomNavPrefs.parse(UserDefaults.standard.string(forKey: BottomNavPrefs.key))
        let initial = initialTabs.contains(.shelf) ? PhoneTab.shelf : (initialTabs.first ?? .me)
        _tab = State(initialValue: initial)
    }

    var body: some View {
        let cfg = navConfig
        let tabs = cfg.tabs
        let detachedSearch = BottomNavPrefs.usesDetachedSearchOrb(tabs)
        let regularTabs = detachedSearch ? Array(tabs.dropLast()) : tabs
        // 书架 tapped while already there: the shelf's folded places stand up over it (only when switcher is active).
        let tabSelection = Binding<PhoneTab>(get: { tab }, set: { new in
            if new == .shelf, tab == .shelf, chrome.shelfHasSwitcher {
                let closing = chrome.switching
                withAnimation(closing ? PhoneChrome.dismissMotion : PhoneChrome.motion) { chrome.switching = !closing }
            } else {
                chrome.switching = false
            }
            if new == .notes, tab == .notes { chrome.notesPop &+= 1 }
            if new == .me, tab == .me { chrome.mePop &+= 1 }
            tab = new
        })
        Group {
            if #available(iOS 18.0, *) {
                TabView(selection: tabSelection) {
                    ForEach(regularTabs) { item in
                        Tab(value: item) {
                            tabContent(item)
                        } label: {
                            tabViewLabel(item, detachedSearch: false)
                        }
                    }
                    // 搜索 in its own circle on the right when placed last with 3+ tabs.
                    if detachedSearch {
                        Tab(value: PhoneTab.search, role: Self.searchRole) {
                            tabContent(.search)
                        } label: {
                            tabViewLabel(.search, detachedSearch: true)
                        }
                    }
                }
            } else {
                TabView(selection: tabSelection) {
                    ForEach(tabs) { item in
                        tabContent(item)
                            .tabItem { tabViewLabel(item, detachedSearch: false) }
                            .tag(item)
                    }
                }
            }
        }
        .environment(chrome)
        .onChange(of: cfg, initial: true) { _, currentCfg in
            let current = currentCfg.tabs
            let switcherNow = BottomNavPrefs.shelfHasSwitcher(currentCfg, hasCollections: !library.collections.isEmpty)
            chrome.shelfHasSwitcher = switcherNow
            if !current.contains(tab), let fallback = current.first {
                tab = fallback
            }
            let detached = BottomNavPrefs.usesDetachedSearchOrb(current)
            let main = detached ? Array(current.dropLast()) : current
            if let shelfIdx = main.firstIndex(of: .shelf), switcherNow {
                chrome.shelfStackAlignment = if shelfIdx == 0 {
                    .bottomLeading
                } else if shelfIdx == main.count - 1 && !detached {
                    .bottomTrailing
                } else {
                    .bottom
                }
            } else {
                chrome.switching = false
            }
            if !switcherNow {
                chrome.shelfName = nil
                chrome.shelfSymbol = nil
            }
        }
        .onChange(of: library.collections.count, initial: true) { _, count in
            let switcherNow = BottomNavPrefs.shelfHasSwitcher(navConfig, hasCollections: count > 0)
            chrome.shelfHasSwitcher = switcherNow
            if !switcherNow { chrome.switching = false }
        }
        .onChange(of: tab) { _, new in
            switch new {
            case .shelf: AirTraffic.screen(chrome.onPodcasts ? "Podcasts" : (chrome.onNotes ? "Notes" : "Shelf"))
            case .podcasts: AirTraffic.screen("Podcasts")
            case .audio, .progress: AirTraffic.screen("Shelf")
            case .notes: AirTraffic.screen("Notes")
            case .me: AirTraffic.screen("Me")
            case .search: break
            }
        }
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
            .airTrafficScreen("Podcasts")
            .presentationBackground(YYColor.bg)
        }
        .onOpenURL { url in
            Task { await library.importFiles([url], source: "share_in") }
        }
        // One place for every tab: 书架, 我的 › 书库来源, 数据快照….
        .yyNotice(String(localized: "出了点问题"), message: library.alert ?? "", tone: .warn,
                  isPresented: Binding(get: { library.alert != nil }, set: { if !$0 { library.alert = nil } }))
        // Background read-aloud / audiobook failure while the reader is closed and playing in 播放条.
        .yyNotice(String(localized: "朗读停下了"),
                  message: library.nowPlaying?.player?.failure ?? "",
                  isPresented: Binding(
                      get: { library.presented == nil && library.nowPlaying?.player?.failure != nil },
                      set: { if !$0 { library.nowPlaying?.player?.failure = nil } }
                  ),
                  secondaryTitle: String(localized: "关上"),
                  primaryTitle: String(localized: "换成系统声音"),
                  onPrimary: { library.nowPlaying?.player?.useSystemVoice() })
        .yyNotice(String(localized: "出了点问题"),
                  message: (library.nowPlaying as? AudiobookPlayer)?.failure ?? "",
                  tone: .warn,
                  isPresented: Binding(
                      get: { library.presented == nil && (library.nowPlaying as? AudiobookPlayer)?.failure != nil },
                      set: { if !$0 { (library.nowPlaying as? AudiobookPlayer)?.failure = nil } }
                  ))
        .bookBlocked(library) { importing = true }
        .fileImporter(isPresented: $importing, allowedContentTypes: Library.importTypes, allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { Task { await library.importFiles(urls) } }
        }
        .onChange(of: phase, initial: true) { _, now in
            if now == .active {
                PaperAppearance.shared.syncFromRoot(scheme: scheme, mode: mode)
                Task { await library.watched.scan(into: library) }
            }
        }
        .onChange(of: scheme, initial: true) { _, now in
            PaperAppearance.shared.syncFromRoot(scheme: now, mode: mode)
        }
        .onChange(of: mode, initial: true) { _, now in
            PaperAppearance.shared.syncFromRoot(scheme: scheme, mode: now)
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
        // TEMP-DEBUG-BEGIN
        .sheet(isPresented: .constant(UserDefaults.standard.string(forKey: "yyDraft") != nil)) {
            CustomPageTurnSoundSheet(selectedRaw: .constant(PageTurnSoundStyle.softPaper.rawValue))
        }
        // TEMP-DEBUG-END
        .onAppear {
            // 上次停在书页上，打开 App 就直接回到那一页.
            // 最近改过 notices changes from launch on, and writes reading settings back through the core.
            SettingsLog.shared.library = library
            guard !reopened else { return }
            reopened = true
            // 设置 · 通用 · 启动时继续上次阅读: 上次停在书页上就直接回到书页；关闭则进入书架.
            if reopenLast, library.presented == nil, !lastBook.isEmpty,
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

    @ViewBuilder private func tabContent(_ item: PhoneTab) -> some View {
        switch item {
        case .shelf:
            PhoneShelf(library: library).nowPlayingBar(library, allowPodcast: false)
        case .notes:
            PhoneNotes(library: library).airTrafficScreen("Notes").nowPlayingBar(library, allowPodcast: true)
        case .podcasts:
            PhoneShelf(library: library, fixedPlace: .podcasts).nowPlayingBar(library, allowPodcast: false)
        case .audio:
            PhoneShelf(library: library, fixedPlace: .audio).nowPlayingBar(library, allowPodcast: true)
        case .progress:
            PhoneShelf(library: library, fixedPlace: .progress).nowPlayingBar(library, allowPodcast: true)
        case .me:
            PhoneMe(library: library).nowPlayingBar(library, allowPodcast: true)
        case .search:
            PhoneSearch(library: library).nowPlayingBar(library, allowPodcast: true)
        }
    }

    @ViewBuilder private func tabViewLabel(_ item: PhoneTab, detachedSearch: Bool) -> some View {
        switch item {
        case .shelf:
            shelfLabel
        case .notes:
            tabLabel("笔记", .notes, line: "tab-notes", fill: "tab-notes-fill")
        case .podcasts:
            tabLabel("播客", .podcasts, line: "radio", fill: "radio-fill")
        case .audio:
            Label("有声", systemImage: "headphones")
        case .progress:
            Label("进度", systemImage: tab == .progress ? "bookmark.fill" : "bookmark")
        case .me:
            meTab
        case .search:
            searchLabel(detached: detachedSearch)
        }
    }

    /// Our own line icons: outlined, and the `-f` filled one on the chosen tab. 书架's filled one
    /// carries a small ⌃⌄: tap it again and the shelf's places stand up over it.
    private func tabLabel(_ title: LocalizedStringKey, _ value: PhoneTab, line: String, fill: String) -> some View {
        Label(title, image: tab == value ? fill : line)
    }

    /// 搜索 stands in its own circle when detached on the right, where the system puts no pad
    /// under the chosen tab: chosen, the icon brings its own, as soft as the others'.
    @ViewBuilder private func searchLabel(detached: Bool) -> some View {
        if detached, #available(iOS 27.0, *), tab == .search {
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
    /// icon carries a small ⌃⌄ when any folded places or collections exist; otherwise stays clean.
    @ViewBuilder private var shelfLabel: some View {
        let on = tab == .shelf
        let switcher = chrome.shelfHasSwitcher
        if switcher, let symbol = chrome.shelfSymbol, let name = chrome.shelfName {
            Label {
                Text(name)
            } icon: {
                if on {
                    Self.switchIcon(symbol)
                } else if symbol.hasPrefix("asset.") {
                    Image(String(symbol.dropFirst(6)))
                } else {
                    Image(systemName: symbol)
                }
            }
        } else if switcher {
            Label("书架", image: on ? "tab-shelf-switch" : "tab-shelf-line")
        } else {
            Label("书架", image: on ? "tab-shelf" : "tab-shelf-line")
        }
    }

    /// An SF symbol or asset icon with the small ⌃⌄ beside it, drawn once into a template image for the tab bar.
    private static func switchIcon(_ symbol: String) -> Image {
        if let made = switchIcons[symbol] { return made }
        let renderer = ImageRenderer(content:
            // The ⌃⌄ hangs to the right; as much nothing on the left keeps the symbol over its name.
            HStack(spacing: 2.5) {
                Color.clear.frame(width: 6)
                if symbol.hasPrefix("asset.") {
                    let base = String(symbol.dropFirst(6))
                    let fillName = base + "-fill"
                    Image(UIImage(named: fillName) != nil ? fillName : base)
                        .resizable()
                        .renderingMode(.template)
                        .scaledToFit()
                        .frame(width: 22, height: 22)
                } else {
                    Image(systemName: UIImage(systemName: symbol + ".fill") != nil ? symbol + ".fill" : symbol).font(.system(size: 19, weight: .medium))
                }
                Image(systemName: "chevron.up.chevron.down").font(.system(size: 7.5, weight: .bold))
                    .frame(width: 6)
            }
            .foregroundStyle(.black)
            .frame(height: 24)
        )
        renderer.scale = 3
        let fallback = symbol.hasPrefix("asset.") ? Image(String(symbol.dropFirst(6))) : Image(systemName: symbol)
        let image = renderer.uiImage.map { Image(uiImage: $0.withRenderingMode(.alwaysTemplate)) } ?? fallback
        switchIcons[symbol] = image
        return image
    }
    @MainActor private static var switchIcons: [String: Image] = [:]

    /// 我的, with a red dot when a new version is waiting. The shelf itself stays clear of it.
    private var meTab: some View {
        let selected = tab == .me
        let name = selected ? "tab-me-fill" : "tab-me"
        return Label {
            Text("我的")
        } icon: {
            if UpdateCenter.shared.showsBadge {
                if #available(iOS 18.0, *) {
                    Image(name)
                        .overlay(alignment: .topTrailing) {
                            Circle().fill(YYColor.danger).frame(width: 7, height: 7).offset(x: 2, y: -2)
                        }
                } else {
                    Self.badgedTabIcon(name, ink: selected ? tint.accent : Color(uiColor: .secondaryLabel), scheme: scheme)
                }
            } else {
                Image(name)
            }
        }
    }

    /// Pre-renders a tab icon with an update red dot into a single `.alwaysOriginal` UIImage so
    /// iOS 17 `.tabItem` displays the dot instead of stripping SwiftUI `.overlay` modifiers.
    private static func badgedTabIcon(_ name: String, ink: Color, scheme: ColorScheme) -> Image {
        let renderer = ImageRenderer(content:
            Image(name)
                .renderingMode(.template)
                .foregroundStyle(ink)
                .frame(width: 24, height: 24)
                .overlay(alignment: .topTrailing) {
                    Circle().fill(YYColor.danger).frame(width: 7, height: 7).offset(x: 2, y: -2)
                }
                .padding(2)
                .environment(\.colorScheme, scheme)
        )
        renderer.scale = 3
        return renderer.uiImage.map { Image(uiImage: $0.withRenderingMode(.alwaysOriginal)) } ?? Image(name)
    }

    @available(iOS 18.0, *)
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

/// Cover, the sentence being read, ‹ ▶ ›, and a thin chapter progress rail along the top edge.
/// Matches `PhonePodcastMiniPlayerBar` (`14pt` continuous stationery card, flat bordered card on
/// 墨水屏模式). A tap on the book info goes back to the page.
struct NowPlayingBar: View {
    let playing: any NowPlaying
    let book: BookSummary
    let open: () -> Void
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(EInk.key) private var eink = false
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    /// 有声书: −15 s / +30 s, and the time.
    private var audio: AudiobookPlayer? { playing as? AudiobookPlayer }

    private var progress: Double {
        let raw = audio.map { $0.inChapter.of > 0 ? $0.inChapter.at / $0.inChapter.of : 0 } ?? playing.player?.sectionProgress ?? 0
        return min(1, max(0, raw))
    }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        VStack(spacing: 0) {
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Rectangle().fill(eink ? YYColor.line.opacity(0.6) : YYColor.inset.opacity(0.8))
                    if progress > 0 {
                        Rectangle()
                            .fill(eink ? YYColor.t1 : tint.accent)
                            .frame(width: max(3, g.size.width * progress))
                    }
                }
            }
            .frame(height: 2.5)

            HStack(spacing: 8) {
                HStack(spacing: 10) {
                    if audio != nil {
                        SquareCover(book: book, shadow: false)
                            .frame(width: 34, height: 34)
                    } else {
                        BookCover(book: book)
                            .frame(width: 26, height: 39)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        Text(audio.map { $0.title($0.chapter) } ?? playing.player?.line ?? book.title)
                            .font(.yy(size: 12.5, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)
                        Text(audio.map { "\(book.title) · \(AudiobookPlayer.clock($0.inChapter.at)) / \(AudiobookPlayer.clock($0.inChapter.of))" } ?? book.title)
                            .font(.yy(size: 10.5))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .contentShape(.rect)
                .onTapGesture(perform: open)

                HStack(spacing: 2) {
                    if let audio {
                        Button { audio.skip(-AudiobookPlayer.back) } label: {
                            Image(systemName: "gobackward.15")
                                .font(.system(size: 14.5, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .frame(width: 34, height: 44)
                                .contentShape(.rect)
                        }
                        .accessibilityLabel(String(localized: "后退 15 秒"))
                    } else {
                        Button { playing.player?.skip(forward: false) } label: {
                            Image(systemName: "backward.end.fill")
                                .font(.system(size: 12.5, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .frame(width: 34, height: 44)
                                .contentShape(.rect)
                        }
                        .accessibilityLabel(String(localized: "上一句"))
                    }

                    Button { playing.togglePlay() } label: {
                        Image(systemName: playing.isPlaying ? "pause.fill" : "play.fill")
                            .contentTransition(.symbolEffect(.replace))
                            .animation(YYMotion.quick, value: playing.isPlaying)
                            .font(.system(size: 12, weight: .semibold))
                            .foregroundStyle(YYColor.card)
                            .offset(x: playing.isPlaying ? 0 : 1)
                            .frame(width: 32, height: 32)
                            .background(YYColor.t1, in: Circle())
                            .frame(width: 40, height: 44)
                            .contentShape(.rect)
                    }
                    .accessibilityLabel(playing.isPlaying ? String(localized: "暂停") : String(localized: "接着听"))

                    if let audio {
                        Button { audio.skip(AudiobookPlayer.ahead) } label: {
                            Image(systemName: "goforward.30")
                                .font(.system(size: 14.5, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .frame(width: 34, height: 44)
                                .contentShape(.rect)
                        }
                        .accessibilityLabel(String(localized: "前进 30 秒"))
                    } else {
                        Button { playing.player?.skip(forward: true) } label: {
                            Image(systemName: "forward.end.fill")
                                .font(.system(size: 12.5, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .frame(width: 34, height: 44)
                                .contentShape(.rect)
                        }
                        .accessibilityLabel(String(localized: "下一句"))
                    }
                }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .fixedSize()
            }
            .padding(.leading, 10)
            .padding(.trailing, 6)
            .frame(height: 52)
        }
        .background(YYColor.card)
        .clipShape(shape)
        .overlay(shape.strokeBorder(YYColor.line, lineWidth: eink ? 0.75 : 0.5))
        .shadow(
            color: eink ? .clear : (scheme == .dark ? Color.black.opacity(0.45) : Color(yy: YYRGBA(25, 22, 18, 0.14))),
            radius: 16,
            y: 6
        )
        .accessibilityElement(children: .contain)
        .accessibilityLabel("正在听《\(book.title)》")
        .accessibilityAction(named: Text("回到正在读的页"), open)
    }
}
#endif
