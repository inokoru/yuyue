#if os(iOS)
import SwiftUI
import YuyueCore

/// 书架 on the phone: the books being read across the top, to read or listen on at a tap; all
/// the books below, in an order. The title names where the shelf is — 书, 进度, 有声, 播客 or one
/// of your 合集 — and a tap on it (or, scrolled, on the chip at the top left) opens the panel
/// that goes to another.
struct PhoneShelf: View {
    @State private var confirming: YYConfirm?
    @Bindable var library: Library
    @Environment(PhoneChrome.self) private var chrome
    @State private var filter = ShelfFilter.all
    @AppStorage("shelf.sort") private var sortName = "recent"
    @State private var importerShown = false
    @State private var feedbackShown = false
    @State private var importShown = false
    @State private var wifiShown = false
    @State private var remoteShown = false
    @State private var watchShown = false
    /// The places panel, and whether it grew out of the title or the chip.
    @State private var panelFrom: PanelFrom?
    /// The title and the chip, in global coordinates, for the panel to grow out of.
    @State private var titleFrame: CGRect = .zero
    @State private var chipFrame: CGRect = .zero
    /// Scrolled past the title: the chip shows at the top left.
    @State private var scrolled = false
    /// Scrolled past the top podcast tabletop card: the floating `.mob-np` mini player bar appears above the tab bar.
    @State private var podcastScrolled = false
    @State private var openedPodcastIssueID: String?
    @State private var naming = false
    @State private var newName = ""
    @State private var renaming: BookCollection?
    /// Back to the top when going to another place.
    @State private var scrollTopTick = 0
    /// 进度's order, the one its cards keep.
    @AppStorage("books.sort") private var bookSort = BookSort.recent
    @State private var editing: BookSummary?
    /// 书: which of 在读 · 想读 · 读过 · 搁置 · 弃读, the book whose page is open, and 加一本书.
    @State private var bookStatus = ReadingStatus.reading
    @State private var showing: String?
    @State private var addingBook = false
    @State private var deletingRecord: BookSummary?
    @State private var picking: BookSummary?
    @State private var selecting = false
    @State private var selected: Set<String> = []
    @AppStorage("shelf.showProgress") private var showProgress = true
    @AppStorage("shelf.coverSize") private var coverSize = 100.0
    /// Regular width (iPhone Duo open, iPad): a fixed even grid, 继续 above it, the welcome in two columns.
    @Environment(\.horizontalSizeClass) private var sizeClass
    /// The shelf's own width, for the grid's column count and the welcome's stacked rows.
    @State private var width: CGFloat = 0
    /// iPhone Duo half folded: the fold and the grid's area, both in global coordinates.
    @State private var fold: CGRect?
    @State private var gridArea: CGRect = .zero
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var regular: Bool { sizeClass == .regular }
    private var inset: CGFloat { regular ? 32 : 20 }

    private var sort: ShelfSort {
        get { ShelfSort.allCases.first { "\($0)" == sortName } ?? .recent }
    }

    var body: some View {
        NavigationStack {
            Group {
                if regular, library.shelfBooks.isEmpty, place == .books {
                    welcomeSplit
                        .safeAreaInset(edge: .top, spacing: 0) {
                            placeHeader
                                .padding(.horizontal, inset)
                                .padding(.top, 14)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                } else {
                    shelf
                }
            }
            .background(YYColor.bg)
            .background {
                GeometryReader { g in
                    let f = InnerDisplay.isOn(sizeClass) ? InnerDisplay.fold(in: g, layoutDirectionBehavior: .fixed) : nil
                    let global = f.map { $0.offsetBy(dx: g.frame(in: .global).minX, dy: 0) }
                    Color.clear
                        .onAppear { fold = global }
                        .onChange(of: global) { _, v in fold = v }
                }
                .ignoresSafeArea()
            }
            .toolbar { toolbar }
            // The title is the shelf's own, at the very top; the bar only while picking books.
            .toolbar(selecting ? .visible : .hidden, for: .navigationBar)
            .toolbar(selecting ? .hidden : .automatic, for: .tabBar)
            // The first tab wears the place: 进度, 播客, 有声 or the 合集, with its symbol.
            .onChange(of: info(place).name, initial: true) { _, _ in
                let now = info(place)
                chrome.shelfName = place == .books ? nil : now.name
                chrome.shelfSymbol = place == .books ? nil : now.symbol
            }
            #if DEBUG
            .onAppear {
                switch UserDefaults.standard.string(forKey: "debugPlace") {
                case "podcasts", "podcast": kind = .podcasts
                case "progress", "books": kind = .books
                case "audio": kind = .audio
                default: break
                }
                if UserDefaults.standard.bool(forKey: "debugPlacePanel") {
                    DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) {
                        openPanel(.title)
                    }
                }
            }
            #endif
            .safeAreaInset(edge: .bottom) {
                if selecting {
                    selectionBar
                } else if ((kind == .podcasts && podcastScrolled) || (kind != .podcasts && library.podcastHub.isPlaying && library.nowPlaying == nil)),
                          let activeEp = library.podcastHub.activeIssue ?? library.podcastHub.deskIssues.first ?? library.podcastHub.allIssues.first {
                    PhonePodcastMiniPlayerBar(library: library, issue: activeEp) {
                        if kind == .podcasts {
                            openedPodcastIssueID = activeEp.id
                        } else {
                            library.podcastHub.requestedOpenIssueID = activeEp.id
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.bottom, 8)
                    .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .scale(scale: 0.97)).combined(with: .opacity))
                }
            }

            .navigationDestination(isPresented: $wifiShown) { PhoneWifi(library: library) }
            .navigationDestination(isPresented: $remoteShown) { PhoneRemoteStart(library: library) }
            .navigationDestination(isPresented: $watchShown) { PhoneWatchedFolder(library: library) }
            .navigationDestination(item: $showing) { id in
                BookRecordPage(library: library, bookID: id) { book, intent in open(book, intent) } pickCollection: { picking = $0 }
                    .toolbar(.hidden, for: .tabBar)
            }
            .sheet(isPresented: $addingBook) {
                PhoneAddBook(library: library, status: bookStatus) { addingBook = false }
                    .presentationDetents([.height(200)])
                    .presentationBackground(YYColor.bg)
            }
            .deletingRecord($deletingRecord, library: library)
        }
        .sheet(isPresented: $feedbackShown) {
            FeedbackForm { feedbackShown = false }
        }
        .sheet(isPresented: $importShown) {
            PhoneImportSheet(watching: library.watched.name) {
                importShown = false
                importerShown = true
            } overWifi: {
                importShown = false
                wifiShown = true
            } remote: {
                importShown = false
                remoteShown = true
            } watch: {
                importShown = false
                watchShown = true
            }
        }
        .fileImporter(isPresented: $importerShown, allowedContentTypes: Library.importTypes, allowsMultipleSelection: true) { result in
            if case .success(let urls) = result {
                Task { await library.importFiles(urls, into: currentCollection) }
            }
        }
        .overlay { placesLayer }
        .yyPrompt(String(localized: "新建合集"), message: String(localized: "起个名字，之后可以把书放进去。"),
                  text: $newName, prompt: String(localized: "合集的名字"), isPresented: $naming,
                  confirm: String(localized: "建好了")) {
            let name = newName.trimmingCharacters(in: .whitespaces)
            if !name.isEmpty, let id = library.createCollection(name) { go(.collection(id)) }
            naming = false
        }
        .yyPrompt(String(localized: "重命名合集"), text: $newName, prompt: String(localized: "合集的名字"),
                  isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } }),
                  confirm: String(localized: "保存")) {
            let name = newName.trimmingCharacters(in: .whitespaces)
            if let c = renaming, !name.isEmpty, name != c.name { library.renameCollection(c.id, to: name) }
            renaming = nil
        }
        .sheet(item: $editing) { book in
            PhoneBookInfo(library: library, book: book)
        }
        .sheet(item: $picking) { book in
            CollectionPicker(library: library, bookID: book.id) { picking = nil }
                .frame(maxWidth: .infinity)
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
                .presentationBackground(YYColor.bg)
        }
        .yyConfirm($confirming)
    }

    /// Everything but the regular-width welcome, in one scroll.
    private var shelf: some View {
        ScrollViewReader { scrollProxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    Color.clear.frame(height: 0).id("shelf-top")
                    placeHeader
                        .padding(.horizontal, inset)
                        .padding(.top, 14)
                    if let book = continuing {
                        continueRow(book)
                            .padding(.horizontal, inset)
                            .padding(.top, 18)
                    }
                    if readingRowShown {
                        readingRow.padding(.top, 28)
                    }
                    if kind == .podcasts {
                        Color.clear.frame(height: 18)
                        PhonePodcasts(library: library, externalOpenedIssueID: $openedPodcastIssueID)
                    } else if kind == .books {
                        Color.clear.frame(height: 18)
                        PhoneBooks(library: library, status: $bookStatus, deleting: $deletingRecord) { showing = $0 } read: { open($0, nil) } pick: { picking = $0 }
                    } else if library.shelfBooks.isEmpty, place == .books {
                        welcome
                    } else if shown.isEmpty {
                        Color.clear.frame(height: 18)
                        empty
                    } else {
                        Color.clear.frame(height: 18)
                        // 全部, how many, and their order — as 在读's own row above.
                        HStack {
                            Text("全部").font(.subheadline.weight(.semibold)).foregroundStyle(YYColor.t1)
                            Text(String(localized: "\(shown.count) 本")).font(.footnote).foregroundStyle(YYColor.t3)
                            Spacer()
                            sortMenu
                        }
                        .padding(.horizontal, inset)
                        .padding(.bottom, 12)
                        grid
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .onGeometryChange(for: CGFloat.self, of: \.size.width) { width = $0 }
                .padding(.bottom, 24)
            }
            .onChange(of: scrollTopTick) {
                scrollProxy.scrollTo("shelf-top", anchor: .top)
            }
            // Past the title, its chip takes its place at the top left.
            .yyOnScrollOffsetPast(72, includeTopInset: true) { past in
                withAnimation(YYMotion.standard) { scrolled = past }
            }
            // Past the top active podcast card (> 160pt), the floating `.mob-np` mini player bar slides in above the tab bar.
            .yyOnScrollOffsetPast(160, includeTopInset: true) { past in
                withAnimation(YYMotion.standard) { podcastScrolled = past }
            }
        }
    }

    /// The covers. Half folded (iPhone Duo), two grids of 2, one each side of the fold, each
    /// with its own 32 pt sides; otherwise one grid.
    @ViewBuilder private var grid: some View {
        if regular, let fold, gridArea.width > 0, fold.minX > gridArea.minX, fold.maxX < gridArea.maxX {
            let left = shown.enumerated().filter { $0.offset % 4 < 2 }.map(\.element)
            let right = shown.enumerated().filter { $0.offset % 4 >= 2 }.map(\.element)
            let half = [GridItem](repeating: GridItem(.flexible(), spacing: 28, alignment: .top), count: 2)
            HStack(alignment: .top, spacing: 0) {
                LazyVGrid(columns: half, alignment: .leading, spacing: 32) {
                    ForEach(left) { cell($0) }
                }
                .padding(.horizontal, inset)
                .frame(width: max(0, fold.minX - gridArea.minX))
                Color.clear.frame(width: fold.width)
                LazyVGrid(columns: half, alignment: .leading, spacing: 32) {
                    ForEach(right) { cell($0) }
                }
                .padding(.horizontal, inset)
                .frame(width: max(0, gridArea.maxX - fold.maxX))
            }
            .animation(YYMotion.layout, value: shown.map(\.id))
            .measuringGridArea($gridArea)
        } else {
            LazyVGrid(columns: columns, alignment: .leading, spacing: regular ? 32 : 22) {
                ForEach(shown) { book in
                    cell(book).transition(YYMotion.reduce ? .opacity : .scale(scale: 0.9).combined(with: .opacity))
                }
            }
            // Sorted or filtered another way, each book moves to its new place.
            .animation(YYMotion.layout, value: shown.map(\.id))
            .padding(.horizontal, inset)
            .measuringGridArea($gridArea)
        }
    }

    /// Compact: as many covers as fit. Regular: an even count, so the covers fall either side of
    /// iPhone Duo's fold — 6 across a wide shelf, else 4.
    private var columns: [GridItem] {
        guard regular else {
            // 设置 · 外观 · 书架封面大小: 100 is the size it always had.
            let k = coverSize / 100
            return [GridItem(.adaptive(minimum: 96 * k, maximum: 130 * k), spacing: 18, alignment: .top)]
        }
        return Array(repeating: GridItem(.flexible(), spacing: 28, alignment: .top), count: width >= 900 ? 6 : 4)
    }

    // MARK: What is shown

    private var currentCollection: String? {
        if case .collection(let id) = filter { id } else { nil }
    }

    /// 书 · 进度 · 播客 · 有声, within what the title picked.
    @State private var kind = Kind.all
    private enum Kind: CaseIterable { case all, books, podcasts, audio
        var name: String {
            switch self {
            case .all: String(localized: "书")
            case .books: String(localized: "进度")
            case .podcasts: String(localized: "播客")
            case .audio: String(localized: "有声")
            }
        }
    }

    private var shown: [BookSummary] {
        var books = library.shelfBooks
        switch filter {
        case .status(let s): books = books.filter { $0.status == s }
        case .collection(let id): books = books.filter { $0.collections.contains(id) }
        default: break
        }
        switch kind {
        case .all: break
        case .books: break
        case .audio: books = books.filter(library.hasAudio)
        case .podcasts: return []
        }
        return sort.sorted(books)
    }

    private var reading: [BookSummary] {
        guard filter == .all else { return [] }
        return library.shelfBooks.filter { b in
            guard b.status == .reading else { return false }
            return switch kind {
            case .all: true
            case .books: true
            case .audio: library.hasAudio(b)
            case .podcasts: false
            }
        }
    }

    /// 继续 (regular width only): the book last opened that has been begun and isn't finished.
    private var continuing: BookSummary? {
        guard regular, filter == .all, !selecting, kind != .podcasts else { return nil }
        return library.shelfBooks
            .filter { b in
                guard b.available, b.status != .finished, b.progress > 0 || b.openedAt != nil else { return false }
                return kind != .audio || library.hasAudio(b)
            }
            .max { ($0.openedAt ?? 0) < ($1.openedAt ?? 0) }
    }

    /// 在读, without the book 继续 already shows.
    private var readingShown: [BookSummary] {
        guard let id = continuing?.id else { return reading }
        return reading.filter { $0.id != id }
    }

    // MARK: Top

    // MARK: Places

    enum PanelFrom { case title, chip }

    /// Where the shelf is now.
    private var place: ShelfPlace {
        if case .collection(let id) = filter { return .collection(id) }
        return switch kind {
        case .all: .books
        case .books: .progress
        case .audio: .audio
        case .podcasts: .podcasts
        }
    }

    private func go(_ to: ShelfPlace) {
        withAnimation(YYMotion.layout) {
            switch to {
            case .books: filter = .all; kind = .all
            case .progress: filter = .all; kind = .books
            case .audio: filter = .all; kind = .audio
            case .podcasts: filter = .all; kind = .podcasts
            case .collection(let id): filter = .collection(id); kind = .all
            }
        }
        scrollTopTick &+= 1
    }

    private func info(_ p: ShelfPlace) -> PlaceInfo {
        switch p {
        case .books:
            let all = library.shelfBooks
            let reading = all.count { $0.status == .reading }
            return PlaceInfo(place: p, name: String(localized: "书"), symbol: "book",
                             short: String(localized: "\(all.count) 本"),
                             meta: [String(localized: "\(all.count) 本"), String(localized: "在读 \(reading)")])
        case .progress:
            let kept = library.keptBooks
            let reading = kept.count { $0.status == .reading }
            let want = kept.count { $0.status == .want }
            return PlaceInfo(place: p, name: String(localized: "进度"), symbol: "bookmark",
                             short: String(localized: "在读 \(reading)"),
                             meta: [String(localized: "在读 \(reading)"), String(localized: "想读 \(want)")])
        case .audio:
            let audio = library.shelfBooks.filter(library.hasAudio)
            let listening = audio.count { $0.status == .reading }
            return PlaceInfo(place: p, name: String(localized: "有声"), symbol: "headphones",
                             short: listening > 0 ? String(localized: "在听 \(listening)") : String(localized: "\(audio.count) 本"),
                             meta: [String(localized: "\(audio.count) 本"), String(localized: "在听 \(listening)")])
        case .podcasts:
            let fresh = library.podcastHub.deskIssues.count
            let channels = library.podcastHub.channels.count
            return PlaceInfo(place: p, name: String(localized: "播客"), symbol: "microphone",
                             short: fresh > 0 ? String(localized: "\(fresh) 期新") : String(localized: "\(channels) 档"),
                             meta: [String(localized: "\(channels) 档频道"), String(localized: "\(fresh) 期未听")],
                             fresh: fresh > 0)
        case .collection(let id):
            let c = library.collections.first { $0.id == id }
            let count = Int(c?.count ?? 0)
            return PlaceInfo(place: p, name: c?.name ?? String(localized: "合集"),
                             symbol: c.map(CollectionIcon.symbol(for:)) ?? "square.stack",
                             short: String(localized: "\(count) 本"),
                             meta: [String(localized: "合集"), String(localized: "\(count) 本")], collection: c)
        }
    }

    /// The title: the place's mark, its name in 宋体 and ⌄; under it what's in it, and the order.
    /// The title: the place's mark, and beside it its name in 宋体 with ⌄ over what's in it — one
    /// block, its words on one edge. The buttons and the order sit at the right of the same row.
    private var placeHeader: some View {
        let now = info(place)
        return Button { openPanel(.title) } label: {
            ShelfPlaceTitle(symbol: now.symbol, name: now.name)
                .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.98))
        .opacity(panelFrom == .title ? 0 : 1)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { titleFrame = $0 }
        .accessibilityLabel("\(now.name)，\(now.meta.joined(separator: "，"))，换一处看")
        // Room for the buttons at the right of this row.
        .padding(.trailing, 150)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// 进度 sorts its cards its own way; 播客 has its own order.
    @ViewBuilder private var sortMenu: some View {
        if place == .progress {
            Menu {
                Picker("排序", selection: $bookSort) {
                    ForEach(BookSort.offered(for: bookStatus), id: \.self) { Text($0.name).tag($0) }
                }
            } label: { sortLabel(bookSort.name) }
        } else if place != .podcasts {
            Menu {
                Picker("排序", selection: $sortName) {
                    ForEach(ShelfSort.allCases, id: \.self) { Text($0.name).tag("\($0)") }
                }
            } label: { sortLabel(sort.name) }
        }
    }

    private func sortLabel(_ name: String) -> some View {
        ShelfSortLabel(name: name)
    }

    /// The chip (scrolled down, on the shelf itself) and the panel, over the whole stack.
    private var placesLayer: some View {
        GeometryReader { g in
            let origin = g.frame(in: .global).origin
            ZStack(alignment: .topLeading) {
                if barShown {
                    // Scrolled: the shelf fades out under the chip and the buttons.
                    LinearGradient(colors: [YYColor.bg, YYColor.bg.opacity(0.85), YYColor.bg.opacity(0)], startPoint: .top, endPoint: .bottom)
                        .frame(height: 74)
                        .ignoresSafeArea(edges: .top)
                        .allowsHitTesting(false)
                        .opacity(scrolled ? 1 : 0)
                    topButtons
                        .padding(.top, 14)
                        .padding(.trailing, 16)
                        .frame(maxWidth: .infinity, alignment: .trailing)
                }
                if chrome.switching {
                    Color.black.opacity(0.06)
                        .ignoresSafeArea()
                        .onTapGesture { withAnimation(PhoneChrome.dismissMotion) { chrome.switching = false } }
                        .transition(.opacity)
                    PlaceStack(current: place,
                               kinds: ShelfPlace.kinds.map(info),
                               collections: library.collections.map { info(.collection($0.id)) }) { to in
                        withAnimation(PhoneChrome.dismissMotion) { chrome.switching = false }
                        if to != place { go(to) }
                    }
                    .padding(.leading, inset - 8)
                    .padding(.bottom, 10)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                    .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.92, anchor: .bottomLeading).combined(with: .opacity))
                }
                if chipShown {
                    PlaceChip(info: info(place)) { openPanel(.chip) }
                        .padding(.top, 11.5)
                        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { chipFrame = $0 }
                        .padding(.leading, inset)
                        .opacity(panelFrom == .chip ? 0 : 1)
                        .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.92, anchor: .leading).combined(with: .opacity))
                }
                if let from = panelFrom {
                    Color.black.opacity(0.001)
                        .ignoresSafeArea()
                        .onTapGesture { closePanel() }
                    // Under the bar's buttons, never over them; from the title, its head where the title was.
                    let top = max((from == .title ? titleFrame.minY - 15 : chipFrame.minY) - origin.y, 58)
                    PlacePanel(current: info(place),
                               kinds: ShelfPlace.kinds.map(info),
                               collections: library.collections.map { info(.collection($0.id)) },
                               go: { to in
                                   closePanel()
                                   if to != place { go(to) }
                               },
                               newCollection: {
                                   closePanel()
                                   newName = ""
                                   naming = true
                               },
                               rename: { c in
                                   closePanel()
                                   newName = c.name
                                   renaming = c
                               },
                               delete: { c in
                                   if place == .collection(c.id) { go(.books) }
                                   library.deleteCollection(c)
                               },
                               library: library)
                        // Across the screen, the same margin either side.
                        .frame(maxWidth: 520)
                        .padding(.horizontal, 16)
                        .frame(maxWidth: .infinity)
                        .offset(y: top)
                        .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.95, anchor: UnitPoint(x: 0.12, y: 0.06)).combined(with: .opacity))
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
    }

    /// Only on the shelf itself, scrolled, and not while picking books.
    private var chipShown: Bool { scrolled && barShown }

    /// The shelf itself, not a page pushed over it, and not picking books.
    private var barShown: Bool {
        !selecting && showing == nil && !wifiShown && !remoteShown && !watchShown
    }

    /// 反馈, 导入 (加书 in 进度) and 选择, at the top right, staying there as the shelf scrolls.
    private var topButtons: some View {
        TopButtons {
            TopButton(title: "反馈", symbol: "exclamationmark.bubble") { feedbackShown = true }
            if library.importing > 0 {
                ProgressView().frame(width: 40, height: 40)
            } else {
                TopButton(title: kind == .books ? "加书" : "导入", symbol: "plus") {
                    if kind == .books { addingBook = true } else { importShown = true }
                }
            }
            TopButton(title: "选择", symbol: "checkmark.circle") { withAnimation(YYMotion.layout) { selecting = true } }
                .disabled(shown.isEmpty)
                .opacity(shown.isEmpty ? 0.35 : 1)
        }
        // 导入 turns into its spinner and back, not in a blink.
        .animation(YYMotion.quick, value: library.importing > 0)
    }

    private func openPanel(_ from: PanelFrom) {
        chrome.switching = false
        withAnimation(YYMotion.reduce ? nil : .spring(duration: 0.32, bounce: 0)) { panelFrom = from }
    }

    private func closePanel() {
        withAnimation(YYMotion.exit) { panelFrom = nil }
    }

    @ToolbarContentBuilder private var toolbar: some ToolbarContent {
        if selecting {
            ToolbarItem(placement: .topBarLeading) {
                Button(selected.count == shown.count ? String(localized: "全不选") : String(localized: "全选")) {
                    selected = selected.count == shown.count ? [] : Set(shown.map(\.id))
                }
            }
            ToolbarItem(placement: .principal) {
                Text(selected.isEmpty ? String(localized: "选择书") : String(localized: "已选 \(selected.count) 本")).font(.headline)
            }
            ToolbarItem(placement: .topBarTrailing) {
                Button("完成") { stopSelecting() }.fontWeight(.semibold)
            }
        }
    }

    /// 在读 — shown only when there is at least one book being read.
    private var readingRowShown: Bool {
        !selecting && kind != .books && !readingShown.isEmpty
    }

    private var readingRow: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("在读").font(.subheadline.weight(.semibold)).foregroundStyle(YYColor.t1)
                Spacer()
                Text(String(localized: "\(readingShown.count) 本")).font(.footnote).foregroundStyle(YYColor.t3)
            }
            PhoneReadingShelf(books: readingShown, library: library, open: { open($0, $1) }) { actions(for: $0) }
                .padding(.top, 4)
        }
        .padding(.horizontal, inset)
    }

    /// What this is and where things are kept, then the
    /// three ways to put books in. Narrower than 420 pt (iPhone Duo closed, beside its side bar)
    /// the three tiles would cut their names short, so they stack as rows instead.
    private var welcome: some View {
        PhoneShelfWelcome(narrow: width < 520, files: $importerShown, wifi: $wifiShown, remote: $remoteShown)
    }

    /// Regular width with no books: what this is on the left (40%), the three ways in as rows on
    /// the right. On iPhone Duo half folded, the arrangement puts each side on its own half.
    @ViewBuilder private var welcomeSplit: some View {
        #if compiler(>=6.4)
        if #available(iOS 27.1, *) {
            ArrangementView {
                welcomeIntro
                    .splitArrangementLayoutRatio(0.4)
            } secondary: {
                welcomeWays
            }
            .arrangementViewStyle(.split)
        } else {
            welcomeSplitFallback
        }
        #else
        welcomeSplitFallback
        #endif
    }

    private var welcomeSplitFallback: some View {
        GeometryReader { g in
            HStack(spacing: 0) {
                welcomeIntro.frame(width: g.size.width * 0.4)
                welcomeWays
            }
        }
    }

    private var welcomeIntro: some View {
        VStack(alignment: .leading, spacing: 12) {
            PhoneShelfWelcomeTitle(size: 34)
            PhoneShelfWelcomeLine().font(.yy(size: 15))
        }
        .frame(maxWidth: 360, alignment: .leading)
        .padding(.horizontal, 32)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .leading)
    }

    private var welcomeWays: some View {
        PhoneShelfWays(row: 72, files: $importerShown, wifi: $wifiShown, remote: $remoteShown)
            .frame(maxWidth: 440)
            .padding(.horizontal, 32)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    @ViewBuilder private var empty: some View {
        if kind == .audio {
            audiobookEmpty
        } else {
            let (symbol, text): (String, String) = if library.books.isEmpty {
                ("books.vertical", String(localized: "书架是空的。点右上角的加号，导入 EPUB、PDF、MOBI、TXT"))
            } else if currentCollection != nil {
                ("tray", String(localized: "这个合集里还没有书。长按一本书，点「加入合集」"))
            } else {
                ("book.closed", String(localized: "这里还没有书"))
            }
            VStack(spacing: 12) {
                Image(systemName: symbol).font(.yy(size: 34, weight: .light)).foregroundStyle(YYColor.t3)
                Text(text).font(.subheadline).foregroundStyle(YYColor.t2).multilineTextAlignment(.center)
                if library.books.isEmpty {
                    Button("导入书") { importerShown = true }
                        .buttonStyle(.borderedProminent)
                        .padding(.top, 4)
                }
            }
            .padding(.horizontal, 40)
            .frame(maxWidth: .infinity, minHeight: 300)
        }
    }

    /// Warm-paper stationery empty state tailored specifically for 有声书 (matches Podcast page visual craft).
    private var audiobookEmpty: some View {
        VStack(spacing: 14) {
            PodcastStationeryCard(cornerRadius: 16) {
                VStack(spacing: 0) {
                    VStack(spacing: 12) {
                        PodcastGlyphView(glyph: .waveform, size: 20, lineWidth: 1.55)
                            .foregroundStyle(tint.accent)
                            .frame(width: 46, height: 46)
                            .background(PodcastStyle.rowTintWash(tint, scheme), in: Circle())
                            .overlay(Circle().strokeBorder(PodcastStyle.accentRing(tint, scheme), lineWidth: 0.5))

                        VStack(spacing: 5) {
                            Text("还没有有声书")
                                .font(PodcastStyle.songti(17.5))
                                .foregroundStyle(YYColor.t1)
                            Text("导入 M4B、MP3、M4A 音频文件，或将录音与书架里的电子书合成，听读同步翻页。")
                                .font(.yy(size: 12.5))
                                .foregroundStyle(YYColor.t3)
                                .multilineTextAlignment(.center)
                                .lineSpacing(4)
                        }
                    }
                    .padding(.horizontal, 22)
                    .padding(.top, 26)
                    .padding(.bottom, 20)

                    Rectangle().fill(YYColor.line).frame(height: 0.5)

                    HStack(spacing: 10) {
                        PodcastPaperPill(
                            title: String(localized: "导入有声书"),
                            glyph: .plus,
                            accent: true,
                            height: 32,
                            horizontalPadding: 14
                        ) {
                            importerShown = true
                        }

                        PodcastPaperPill(
                            title: String(localized: "Wi‑Fi 传书"),
                            symbol: "wifi",
                            height: 32,
                            horizontalPadding: 14
                        ) {
                            wifiShown = true
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 13)
                    .background(PodcastStyle.leafSurface(scheme))
                }
                .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            }

            HStack(spacing: 16) {
                Label("本地离线播放", systemImage: "internaldrive")
                Label("支持听读同步", systemImage: "text.alignleft")
            }
            .font(.yy(size: 11))
            .foregroundStyle(YYColor.t3)
            .padding(.top, 2)
        }
        .padding(.horizontal, inset)
        .padding(.top, 20)
        .frame(maxWidth: .infinity)
    }

    // MARK: 继续

    /// Regular width: the book last opened, large, over the left half; the right half stays empty.
    private func continueRow(_ book: BookSummary) -> some View {
        let listening = library.listening == book.id || library.nowPlaying?.bookID == book.id
        return VStack(alignment: .leading, spacing: 10) {
            Text("继续").font(.subheadline.weight(.semibold)).foregroundStyle(YYColor.t1)
            HStack(alignment: .center, spacing: 16) {
                BookCover(book: book)
                    .frame(width: 104, height: 156)
                    .onTapGesture { open(book, nil) }
                VStack(alignment: .leading, spacing: 0) {
                    Text(book.title)
                        .font(YYUI.serif(20, .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(2)
                        .padding(.bottom, 4)
                    if let author = book.author {
                        Text(author).font(.yy(size: 14)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                    Spacer(minLength: 10)
                    ProgressLine(value: book.progress, height: 4)
                        .padding(.bottom, 6)
                    Text([book.percent, book.remaining].joined(separator: " · "))
                        .font(.yy(size: 13)).monospacedDigit()
                        .foregroundStyle(YYColor.t2)
                        .lineLimit(1)
                        .padding(.bottom, 10)
                    HStack(spacing: 8) {
                        // 有声书 has only 听, as on the 在读 cards.
                        if book.format != .audio {
                            continueButton(String(localized: "读"), filled: true) { open(book, nil) }
                        }
                        continueButton(listening ? String(localized: "正在听") : String(localized: "听"), filled: book.format == .audio) {
                            open(book, listening ? nil : .listen)
                        }
                    }
                }
                .padding(.vertical, 6)
            }
            .padding(.leading, 16)
            .padding(.trailing, 16)
            .padding(.vertical, 6)
            .frame(width: max(0, width / 2 - inset - 14), alignment: .leading)
            .frame(minHeight: 168)
            .yyCard()
            .contextMenu { actions(for: book) } preview: { BookPreview(book: book) }
        }
    }

    private func continueButton(_ title: String, filled: Bool, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(.yy(size: 15, weight: .semibold))
                .foregroundStyle(filled ? tint.onBlack : tint.accent)
                .lineLimit(1)
                .frame(maxWidth: .infinity)
                .frame(height: 40)
                .background(filled ? tint.black : tint.accentSoft, in: .capsule)
                .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
    }

    // MARK: Books

    private func cell(_ book: BookSummary) -> some View {
        Button {
            if selecting {
                if selected.contains(book.id) { selected.remove(book.id) } else { selected.insert(book.id) }
            } else {
                open(book, nil)
            }
        } label: {
            VStack(alignment: .leading, spacing: 0) {
                BookCover(book: book)
                    .opacity(book.available ? 1 : 0.45)
                    .overlay(alignment: .bottomTrailing) {
                        if selecting {
                            Image(systemName: selected.contains(book.id) ? "checkmark.circle.fill" : "circle")
                                .font(.yy(size: 22))
                                .foregroundStyle(selected.contains(book.id) ? tint.accent : .white)
                                .contentTransition(.symbolEffect(.replace))
                                .background(Circle().fill(selected.contains(book.id) ? .white : .black.opacity(0.25)).padding(2))
                                .padding(6)
                                .animation(YYMotion.quick, value: selected.contains(book.id))
                        } else if !book.available {
                            Image(systemName: "icloud.and.arrow.down")
                                .font(.yy(size: 11, weight: .semibold))
                                .foregroundStyle(YYColor.t2)
                                .padding(6)
                                .background(YYColor.card, in: .circle)
                                .padding(6)
                        } else if library.hasAudio(book) {
                            AudioBadge()
                        }
                    }
                    .padding(.bottom, 8)
                Text(book.title)
                    .font(regular ? SwiftUI.Font.yy(size: 15, weight: .semibold) : SwiftUI.Font.footnote.weight(.semibold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .padding(.bottom, 2)
                if showProgress { note(book) }
            }
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle())
        .contextMenu {
            if !selecting { actions(for: book) }
        } preview: {
            BookPreview(book: book)
        }
        .accessibilityLabel("《\(book.title)》\(book.author.map { "，\($0)" } ?? "")，\(book.shelfNote)")
    }

    @ViewBuilder private func note(_ book: BookSummary) -> some View {
        if library.listening == book.id || library.nowPlaying?.bookID == book.id {
            Label("正在听 · \(book.percent)", systemImage: "headphones")
                .labelStyle(TightLabel())
                .font(regular ? SwiftUI.Font.yy(size: 13) : SwiftUI.Font.caption2)
                .foregroundStyle(tint.accent)
        } else {
            Text(library.shelfNote(book))
                .font(regular ? SwiftUI.Font.yy(size: 13) : SwiftUI.Font.caption2)
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
        }
    }

    /// 长按: three common ones in a row (接着读 · 从头听 · 标为读完), then 编辑信息 · 加入合集 ·
    /// 删除, as confirmed in the design.
    @ViewBuilder private func actions(for book: BookSummary) -> some View {
        ControlGroup {
            Button(resumeTitle(book), systemImage: "book") { open(book, nil) }
            Button("从头听", systemImage: "headphones") { open(book, .listenFromStart) }
            Button(BookAction.toggleFinished.title(for: book), systemImage: BookAction.toggleFinished.symbol(for: book)) {
                library.setFinished(book, book.status != .finished)
            }
        }
        .controlGroupStyle(.compactMenu)
        if let recording = library.audiobooks.audiobook(for: book.id) {
            Button("有声书", systemImage: "waveform") { library.audiobooks.showing = recording.id }
        }
        Section {
            Button("编辑信息", systemImage: "pencil") { editing = book }
            Button("加入合集", systemImage: "tray.full") { picking = book }
            Button("删除…", systemImage: "trash", role: .destructive) { confirming = YYConfirm(title: String(localized: "删除《\(book.title)》？"), message: String(localized: "书会移到已删除，30 天内可以在存储里撤销。"), button: String(localized: "删除")) { library.delete(book) } }
        }
    }

    /// 没读过的写"开始读"，读完的写"重读".
    private func resumeTitle(_ book: BookSummary) -> String {
        if book.status == .finished { return String(localized: "重读") }
        return BookAction.resume.title(for: book)
    }

    private func open(_ book: BookSummary, _ intent: Library.OpenIntent?) {
        guard book.available else {
            library.note(icon: "icloud.and.arrow.down", String(localized: "《\(book.title)》在别的设备上"), String(localized: "把同一个文件导入这台手机，进度和笔记就接上了"))
            return
        }
        library.open(book, intent)
    }

    // MARK: 多选

    private var selectionBar: some View {
        let books = library.books.filter { selected.contains($0.id) }
        return HStack(spacing: 0) {
            barButton(String(localized: "标为读完"), "checkmark") {
                for book in books { library.setFinished(book, true) }
                stopSelecting()
            }
            barButton(String(localized: "删除"), "trash", danger: true) {
                let title = books.count == 1
                    ? String(localized: "删除《\(books[0].title)》？")
                    : String(localized: "删除这 \(books.count) 本书？")
                confirming = YYConfirm(
                    title: title,
                    message: String(localized: "书会移到已删除，30 天内可以在存储里撤销。"),
                    button: String(localized: "删除")
                ) {
                    library.delete(books)
                    stopSelecting()
                }
            }
        }
        .disabled(books.isEmpty)
        .padding(.horizontal, 8)
        .modifier(CapsuleGlass())
        .padding(.horizontal, 60)
        .padding(.bottom, 8)
        .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
    }

    private func barButton(_ title: String, _ symbol: String, danger: Bool = false, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 3) {
                Image(systemName: symbol).font(.yy(size: 17))
                Text(title).font(.caption2)
            }
            .foregroundStyle(danger ? YYColor.danger : YYColor.t1)
            .frame(maxWidth: .infinity)
            .frame(height: 54)
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
    }

    private func stopSelecting() {
        withAnimation(YYMotion.layout) {
            selecting = false
            selected = []
        }
    }
}

/// The closed sort menu's label: the name and the up-down chevron.
struct ShelfSortLabel: View {
    var name: String

    var body: some View {
        HStack(spacing: 4) {
            Text(name)
            Image(systemName: "chevron.up.chevron.down").font(.yy(size: 10, weight: .semibold))
        }
        .font(.footnote)
        .foregroundStyle(YYColor.t2)
    }
}

/// What this is, then the three ways to put books in. Narrow stacks the ways as rows.
struct PhoneShelfWelcome: View {
    var narrow: Bool
    @Binding var files: Bool
    @Binding var wifi: Bool
    @Binding var remote: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            PhoneShelfWelcomeTitle(size: YYUI.s(21))
            PhoneShelfWelcomeLine()
                .font(YYUI.font(11)).lineSpacing(YYUI.s(11) * 0.7)
                .padding(.top, YYUI.s(6))
            PhoneShelfWays(row: narrow ? 64 : nil, files: $files, wifi: $wifi, remote: $remote)
                .padding(.top, YYUI.s(16))
        }
        .padding(.horizontal, 20)
        .padding(.top, YYUI.s(14))
    }
}

struct PhoneShelfWelcomeTitle: View {
    var size: CGFloat

    var body: some View {
        Text("把书放进来，\n读或者听都行。")
            .font(YYUI.serif(size, .bold))
            .tracking(size * 0.02)
            .lineSpacing(size * 0.5)
            .foregroundStyle(YYColor.t1)
    }
}

struct PhoneShelfWelcomeLine: View {
    var body: some View {
        Text("不用注册。书、进度和笔记都存在你自己的设备里。").foregroundStyle(YYColor.t2)
    }
}

/// The three ways in: tiles (`row` nil), or full-width rows of that height.
struct PhoneShelfWays: View {
    var row: CGFloat?
    @Binding var files: Bool
    @Binding var wifi: Bool
    @Binding var remote: Bool

    var body: some View {
        if let row {
            VStack(spacing: 10) {
                way("folder", String(localized: "从文件导入"), String(localized: "本机或云盘里的"), row: row) { files = true }
                way("wifi", String(localized: "Wi‑Fi 传书"), String(localized: "用电脑浏览器传"), row: row) { wifi = true }
                way("server.rack", String(localized: "远程书库"), String(localized: "Calibre、NAS"), row: row) { remote = true }
            }
        } else {
            HStack(alignment: .top, spacing: YYUI.s(8)) {
                tile("folder", String(localized: "从文件导入"), String(localized: "本机或云盘里的")) { files = true }
                tile("wifi", String(localized: "Wi‑Fi 传书"), String(localized: "用电脑浏览器传")) { wifi = true }
                tile("server.rack", String(localized: "远程书库"), String(localized: "Calibre、NAS")) { remote = true }
            }
        }
    }

    private func way(_ symbol: String, _ title: String, _ detail: String, row: CGFloat, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 12) {
                Image(systemName: symbol)
                    .font(.yy(size: 20, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                    .frame(width: 36, height: 36)
                    .background(YYColor.inset, in: .circle)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.yy(size: 15, weight: .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Text(detail).font(.yy(size: 13)).foregroundStyle(YYColor.t2).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                Image(systemName: "chevron.right")
                    .font(.yy(size: 13, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.leading, 14)
            .padding(.trailing, 16)
            .frame(maxWidth: .infinity, minHeight: row, alignment: .leading)
            .background(YYColor.card, in: .rect(cornerRadius: 16))
            .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.rect(cornerRadius: 16))
        }
        .buttonStyle(YYPressStyle())
    }

    private func tile(_ symbol: String, _ title: String, _ detail: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(alignment: .leading, spacing: YYUI.s(10)) {
                Image(systemName: symbol)
                    .font(YYUI.font(12, .medium))
                    .foregroundStyle(YYColor.t1)
                    .frame(width: YYUI.s(28), height: YYUI.s(28))
                    .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(8)))
                VStack(alignment: .leading, spacing: YYUI.s(2)) {
                    Text(title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1).minimumScaleFactor(0.8)
                    Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(2)
                }
            }
            .padding(.horizontal, YYUI.s(10))
            .padding(.top, YYUI.s(12))
            .padding(.bottom, YYUI.s(11))
            .frame(maxWidth: .infinity, alignment: .leading)
            .yyCard()
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.98))
    }
}

/// One book on the shelf when it has no cover file yet: the plate, the name, the line under it.
struct PhoneShelfBookCell: View {
    var title: String
    var author: String?
    var note: String
    var pdf = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            CoverPlate(title: title, author: author, pdf: pdf)
                .padding(.bottom, 8)
            Text(title)
                .font(.footnote.weight(.semibold))
                .foregroundStyle(YYColor.t1)
                .lineLimit(1)
                .padding(.bottom, 2)
            Text(note)
                .font(.caption2)
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
        }
    }
}

/// What floats up when a book is pressed: the cover a size smaller, the title, and how far —
/// a thin line and how long is left, not a percentage.
struct BookPreview: View {
    let book: BookSummary

    var body: some View {
        HStack(spacing: 12) {
            BookCover(book: book).frame(width: 44)
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(book.title).font(.subheadline.weight(.semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    if let author = book.author {
                        Text(author).font(.caption).foregroundStyle(YYColor.t3).lineLimit(1)
                    }
                }
                HStack(spacing: 10) {
                    if book.progress > 0, book.status != .finished {
                        ProgressLine(value: book.progress, height: 2.5)
                    }
                    Text(book.remaining)
                        .font(.caption)
                        .foregroundStyle(YYColor.t2)
                        .fixedSize()
                    if book.progress == 0 || book.status == .finished { Spacer(minLength: 0) }
                }
            }
        }
        .padding(14)
        .frame(width: 300)
        .background(YYColor.card)
    }
}

private extension View {
    /// The grid's area in global coordinates, before its own sides.
    func measuringGridArea(_ area: Binding<CGRect>) -> some View {
        onGeometryChange(for: CGRect.self, of: { $0.frame(in: .global) }) { area.wrappedValue = $0 }
    }
}
#endif
