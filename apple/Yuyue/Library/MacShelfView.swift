import SwiftUI
import YuyueCore
#if os(macOS)
import AppKit
import UniformTypeIdentifiers

private final class MainOriginBox {
    var origin: CGPoint = .zero
}

/// The shelf window: the library, reading status and the user's collections down the side;
/// the books being read and then all of them in the middle.
struct MacShelfView: View {
    @State private var confirming: YYConfirm?
    @Bindable var library: Library
    @State private var filter: ShelfFilter?

    init(library: Library) {
        self.library = library
        let saved = ShelfFilter.parse(UserDefaults.standard.string(forKey: ShelfFilter.prefKey))
        let valid = ShelfFilter.resolveValid(saved, collectionIDs: Set(library.collections.map(\.id)))
        _filter = State(initialValue: valid)
    }
    /// Whether the 在读 card strip shows every reading book inline instead of capping at one row.
    @State private var readingExpanded = false
    @State private var readingWidth: CGFloat = 0
    /// The sidebar's white selection slides from row to row.
    @Namespace private var sidebarSelection
    /// 搜索 · 问 ⌘K, and what to start it with.
    @State private var quick: String?
    @State private var sort = ShelfSort.recent
    @State private var importerShown = false
    @State private var menu: OpenMenu?
    @State private var picking: String?
    #if DEBUG
    @State private var debugPicking: String?
    #endif
    @State private var editing: BookSummary?
    @State private var deletingRecord: BookSummary?
    @State private var pickingSheet: String?
    @State private var feedbackShown = false
    @State private var mainWidth: CGFloat = 0
    @State private var mainOriginBox = MainOriginBox()
    @State private var menuSize = CGSize(width: 214, height: 280)
    @State private var dropCount: Int?
    /// A book 在书架里显示 pointed at, ringed for a moment.
    @State private var revealed: String?
    @State private var pendingReveal: String?
    @State private var newName = ""
    @State private var renaming: String?
    @State private var renamingPromptID: String?
    @State private var renamingPromptText = ""
    @State private var cameFromWall = false
    @State private var dropCollection: String?
    @FocusState private var nameFocused: Bool
    @Environment(\.openWindow) private var openWindow
    @Environment(\.openSettings) private var openSettings
    @Environment(\.colorScheme) private var scheme

    private struct PickingID: Identifiable { let id: String }

    private struct OpenMenu: Equatable {
        let book: String
        let at: CGPoint
    }

    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage("general.reopenLast") private var reopenLast = false
    @State private var reopened = false
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var shelfScreenName: String {
        switch filter {
        case .podcasts: "Podcasts"
        case .notes, .words: "Notes"
        default: "Shelf"
        }
    }

    var body: some View {
        deskCanvas
            .fileImporter(isPresented: $importerShown, allowedContentTypes: Library.importTypes, allowsMultipleSelection: true) { result in
            if case .success(let urls) = result {
                Task { await library.importFiles(urls, into: currentCollection) }
            }
        }
        .onAppear(perform: takeOpenedFiles)
        // Opened again for a new language: straight back to 设置, where it was changed.
        .onAppear { if AppLanguage.settingsPending { openSettings() } }
        .onReceive(NotificationCenter.default.publisher(for: .filesOpened)) { _ in takeOpenedFiles() }
        .onReceive(NotificationCenter.default.publisher(for: .showPodcasts)) { _ in filter = .podcasts }
        .openingSnapshots(library)
        .audiobookSheets(library)
        .recordingImportSheet(library)
        .sheet(item: $editing) { book in
            EditInfoSheet(library: library, book: book)
        }
        .sheet(item: Binding(get: { pickingSheet.map(PickingID.init) }, set: { pickingSheet = $0?.id })) { p in
            CollectionPicker(library: library, bookID: p.id) { pickingSheet = nil }
                .presentationBackground(YYColor.bg)
        }
        .deletingRecord($deletingRecord, library: library)
        // 反馈 from the sidebar or 帮助 › 给语阅反馈… (which may come from a book's window).
        .sheet(isPresented: $feedbackShown) {
            FeedbackForm { feedbackShown = false }
                .frame(width: 500)
                .frame(minHeight: 520, idealHeight: 532)
        }
        .onChange(of: Feedback.shared.requested, initial: true) { _, asked in
            guard asked else { return }
            Feedback.shared.requested = false
            feedbackShown = true
        }
        .yyNotice(String(localized: "出了点问题"), message: library.alert ?? "", tone: .warn,
                  isPresented: Binding(get: { library.alert != nil }, set: { if !$0 { library.alert = nil } }))
        .bookBlocked(library) { importerShown = true }
        .updateDialog()
        .airTrafficScreen(shelfScreenName)
        .tint(tint.accent)
        .onAppear {
            AppAppearance.apply()
            MacMenuBar.openWindow = openWindow
            // 打开语阅时接着读上次那本: once, when the app starts.
            guard reopenLast, !reopened else { return }
            reopened = true
            if let last = library.books.filter({ $0.openedAt != nil }).max(by: { ($0.openedAt ?? 0) < ($1.openedAt ?? 0) }),
               library.open(last, nil) {
                openWindow(id: "reader", value: last.id)
            }
        }
        .task { await library.sync.keepUp() }
        .task(id: library.books.map(\.id)) { countNotes() }
        .onReceive(NotificationCenter.default.publisher(for: .notesChanged)) { _ in countNotes() }
        .onReceive(NotificationCenter.default.publisher(for: .vocabChanged)) { _ in countNotes() }
        #if DEBUG
        .task {
            // `-debugImport <file>|<file>` imports them as 导入 would, even onto an empty shelf;
            // `-debugOpen 红楼` opens the first book whose title has that in it.
            Task {
                if let path = UserDefaults.standard.string(forKey: "debugImport") { await library.importFiles(path.split(separator: "|").map { URL(filePath: String($0)) }) }
                // A recording goes in after the books, in a task of its own.
                try? await Task.sleep(for: .seconds(2))
                // `-debugAudiobook 1` shows the first 有声书's page; `-debugAudiobook remake` makes it again.
                if let what = UserDefaults.standard.string(forKey: "debugAudiobook"), let a = library.audiobooks.all.first {
                    if what == "remake", let book = library.books.first(where: { $0.id == a.book }) { library.audiobooks.make(a, with: book) } else { library.audiobooks.showing = a.id }
                }
                // `-debugPair 1` lays the first 有声书 on the shelf against the first e-book (合成).
                if UserDefaults.standard.bool(forKey: "debugPair") {
                    while library.pendingRecording != nil { try? await Task.sleep(for: .milliseconds(300)) }
                    if let audio = library.books.first(where: { $0.format == .audio }), let ebook = library.books.first(where: { $0.format != .audio }) {
                        library.pair(audio, with: ebook)
                    }
                }
                if let title = UserDefaults.standard.string(forKey: "debugOpen"), let book = library.books.first(where: { $0.title.contains(title) }),
                   library.open(book, nil) {
                    openWindow(id: "reader", value: book.id)
                }
            }
            // `-debugPodcast 1` opens on 播客; `desk` on its 编辑部, `make` with 配一档节目.
            if UserDefaults.standard.string(forKey: "debugPodcast") != nil { filter = .podcasts }
            // `-debugMenu 1` / `-debugPick 1` open the card or the picker on the first book,
            // to look at them without clicking.
            guard let first = shown.first else { return }
            if UserDefaults.standard.bool(forKey: "debugMenu") { menu = OpenMenu(book: first.id, at: CGPoint(x: 300, y: 300)) }
            // `-debugCollections "睡前|科幻|通勤听"` makes those (the first with the first book in it).
            if let names = UserDefaults.standard.string(forKey: "debugCollections"), library.collections.isEmpty {
                for (i, name) in names.split(separator: "|").enumerated() { library.createCollection(String(name), with: i == 0 ? first.id : nil) }
            }
            if UserDefaults.standard.bool(forKey: "debugPick") { debugPicking = first.id }
            // `-debugPop 1`: the real popover, beside the book.
            if UserDefaults.standard.bool(forKey: "debugPop") { picking = first.id }
            if UserDefaults.standard.bool(forKey: "debugEdit") { editing = first }
            // `-debugCover <image path>` puts that image on the first book, as 换封面 would.
            if let path = UserDefaults.standard.string(forKey: "debugCover"), let data = FileManager.default.contents(atPath: path) {
                library.setCover(first, image: data)
            }
            if UserDefaults.standard.bool(forKey: "debugToast") { library.debugToast() }
            // `-debugBlocked missing`: 找不到书; `-debugBlocked bad`: 这本书打不开.
            switch UserDefaults.standard.string(forKey: "debugBlocked") {
            case "missing":
                library.blocked = BlockedBook(
                    bookID: "", title: String(localized: "找不到这本书的文件"),
                    message: String(localized: "可能被移走或删掉了。从书架上删掉，再导入一次就好。"), reimport: true)
            case "bad":
                library.blocked = BlockedBook(
                    bookID: "", title: String(localized: "这本书打不开"),
                    message: String(localized: "文件可能坏了，或者是不支持的格式。"), reimport: false)
            default: break
            }
            // `-debugFilter notes|words` opens that page of the sidebar; `-notes.byBook 1` 按书.
            switch UserDefaults.standard.string(forKey: "debugFilter") {
            case "notes": filter = .notes
            case "words": filter = .words
            case "podcasts": filter = .podcasts
            case "audio": filter = .audio
            case "books": filter = .books
            default: break
            }
            // `-debugSettings sync` opens 设置 on that page.
            if let page = UserDefaults.standard.string(forKey: "debugSettings") {
                UserDefaults.standard.set(page, forKey: "settings.page")
                openSettings()
            }
            // `-debugQuick 父亲` opens 搜索 · 问 with that typed.
            if let q = UserDefaults.standard.string(forKey: "debugQuick") { quick = q }
            // `-debugDrop 3` shows what dragging three files in looks like.
            if UserDefaults.standard.object(forKey: "debugDrop") != nil { dropCount = UserDefaults.standard.integer(forKey: "debugDrop") }
        }
        .overlay(alignment: .topTrailing) {
            if let id = debugPicking {
                CollectionPicker(library: library, bookID: id).clipShape(.rect(cornerRadius: 12)).padding(40)
            }
        }
        #endif
        .overlay {
            if let q = quick {
                QuickSearchPanel(library: library, query: q) { quick = nil } ask: { question in
                    ask(question)
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .quickSearch)) { _ in quick = quick == nil ? "" : nil }
        .onChange(of: filter) { old, next in
            UserDefaults.standard.set((next ?? .all).storageKey, forKey: ShelfFilter.prefKey)
            if case .collection = next, old != .collections {
                // Navigated directly from the sidebar rather than the wall.
                cameFromWall = false
            } else if case .collection = next {
                // Keep cameFromWall when arriving from .collections.
            } else {
                cameFromWall = false
            }
        }
        .onChange(of: library.collections.map(\.id)) { _, ids in
            if case .collection(let id) = filter, !ids.contains(id) {
                filter = .all
            }
        }
        .yyPrompt(
            String(localized: "重命名合集"),
            message: "",
            text: $renamingPromptText,
            prompt: String(localized: "合集的名字"),
            isPresented: Binding(get: { renamingPromptID != nil }, set: { if !$0 { renamingPromptID = nil } }),
            confirm: String(localized: "保存")
        ) {
            let trimmed = renamingPromptText.trimmingCharacters(in: .whitespaces)
            if let id = renamingPromptID, !trimmed.isEmpty {
                library.renameCollection(id, to: trimmed)
            }
            renamingPromptID = nil
        }
        .yyConfirm($confirming)
    }

    private var deskCanvas: some View {
        // 首页导航: the sidebar is written straight on the desk, no
        // card; the page is a sheet of paper lifted off it, 8 in from the window's edges. Put away,
        // the sidebar slides out to the left while the paper widens to the window's left edge.
        ZStack(alignment: .topLeading) {
            Group {
                switch filter {
                case .notes: MacNotesView(library: library)
                case .words: MacWordsView(library: library)
                case .podcasts: MacPodcastView(library: library)
                case .books, .status:
                    MacBooksView(library: library, start: { if case .status(let s) = filter { s } else { .reading } }(), open: open) { book, point in
                        showMenu(for: book, at: point)
                    } pick: { book in
                        pickingSheet = book.id
                    }
                    .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { rect in
                        mainOriginBox.origin = rect.origin
                        let w = rect.width.rounded()
                        if abs(mainWidth - w) > 0.5 { mainWidth = w }
                    }
                    .overlay { menuLayer.animation(YYMotion.quick, value: menu?.book) }
                case .collections:
                    CollectionWall(library: library, pinnedRaw: $pinnedRaw, leading: headerInset) {
                        cameFromWall = true
                        filter = .collection($0)
                    }
                default: main
                }
            }
            .id(filter)
            .transition(YYMotion.swap)
            .animation(YYMotion.standard, value: filter)
            .environment(\.shelfHeaderInset, headerInset)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .clipShape(.rect(cornerRadius: ShelfDesk.paperRadius))
            .background { ShelfDesk.Paper() }
            .padding([.top, .bottom, .trailing], ShelfDesk.margin)
            .padding(.leading, paperLeading)
            sidebar
                .frame(width: 212)
                .padding(8)
                .offset(x: sidebarShown ? 0 : -240)
                .opacity(sidebarShown ? 1 : 0)
                .allowsHitTesting(sidebarShown)
                .accessibilityHidden(!sidebarShown)
        }
        .overlay(alignment: .top) { toast.padding(.leading, sidebarShown ? 228 : 0).padding(.horizontal, 16) }
        .yyToastMotion(value: library.toast?.id)
        .ignoresSafeArea(.container, edges: .top)
        .onReceive(NotificationCenter.default.publisher(for: .toggleShelfSidebar)) { _ in toggleSidebar(animated: false) }
        .onGeometryChange(for: Bool.self) { $0.size.width < 760 } action: { narrow in
            withAnimation(Self.slide) {
                narrowWindow = narrow
                if !narrow { openedWhileNarrow = false }
            }
        }
        .background(ShelfDesk.fill)
        .overlay(alignment: .topLeading) {
            sidebarButton
                .padding(.leading, paperLeading + 16)
                .padding(.top, Self.lineY - 11)
                .ignoresSafeArea(.container, edges: .top)
        }
        .yyHideWindowToolbarChrome()
        .trafficLights(CGPoint(x: 30, y: 24), hidden: !sidebarShown)
        .noScrollEdgeFade()
    }

    /// 问书伴 from ⌘K: in the book read last.
    private func ask(_ question: String) {
        guard let book = library.books.filter({ $0.available && $0.openedAt != nil }).max(by: { ($0.openedAt ?? 0) < ($1.openedAt ?? 0) }) else {
            library.note(icon: "sparkle", String(localized: "先打开一本书"), String(localized: "书伴陪你读一本书，打开以后再问"))
            return
        }
        guard library.ask(question, in: book.id) else { return }
        openWindow(id: "reader", value: book.id)
    }

    // MARK: Sidebar

    /// The list, then 同步 at its foot: below it, not over it, so a long list of 合集 stops
    /// above 同步 instead of running under. On the desk itself, no card behind it.
    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 0) {
            // The window's own buttons sit here.
            Color.clear.frame(height: 10)
            searchField
                .padding(.top, 16)
                .padding(.horizontal, 10)
            ScrollView {
                sidebarList
                    .padding(.horizontal, 10)
                    .padding(.bottom, 8)
            }
            .scrollIndicators(.never)
            sidebarFoot
                .padding(.horizontal, 10)
        }
        .padding(.top, 16)
        .padding(.bottom, 8)
        .frame(maxHeight: .infinity, alignment: .top)
        .focusable()
        .focusEffectDisabled()
        .onMoveCommand(perform: moveFilter)
    }

    private var searchField: some View {
        Button { quick = "" } label: {
            HStack(spacing: 7) {
                Image(systemName: "magnifyingglass").font(.yy(size: 11.5))
                Text("搜索 · 问").font(.yy(size: 12))
                Spacer()
                Text("⌘K").font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
            }
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 9)
            .frame(height: 30)
            .background(ShelfDesk.field, in: .rect(cornerRadius: 9))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private var sidebarList: some View {
        sidebarRows
            .environment(\.sidebarSelection, sidebarSelection)
            .animation(YYMotion.layout, value: filter)
            .onChange(of: filter) { _, _ in readingExpanded = false }
    }

    private var sidebarRows: some View {
        VStack(alignment: .leading, spacing: 0) {
            sidebarGroup(String(localized: "资料库"), key: "library")
            if !closedGroups.contains("library") {
                sidebarRow(.all, String(localized: "书"), "asset." + PhoneTabIcon.shelf, library.shelfBooks.count)
                // 进度: every book kept, files or not, by 在读 · 想读 · 读过 · 搁置 · 弃读; the count is 在读.
                sidebarRow(.books, String(localized: "进度"), ProtoIcon.book.rawValue, library.keptBooks.count { $0.status == .reading })
                // 播客: how many issues are currently on the Desk (no unread backlog count).
                sidebarRow(.podcasts, String(localized: "播客"), "asset.radio", library.podcastHub.deskIssues.count)
                sidebarRow(.audio, String(localized: "有声"), ProtoIcon.wave.rawValue, library.shelfBooks.count(where: library.hasAudio))
            }
            sidebarGroup(String(localized: "笔记"), key: "notes")
            if !closedGroups.contains("notes") {
                // The phone's 笔记 tab icon.
                sidebarRow(.notes, String(localized: "笔记"), "square.and.pencil", noteCount)
                sidebarRow(.words, String(localized: "生词本"), "translate", wordCount)
            }
            sidebarGroup(String(localized: "合集"), key: "collections")
            if !closedGroups.contains("collections") {
                allCollectionsRow
                ForEach(pinnedCollections, id: \.id) { c in
                    collectionRow(c)
                }
            }
        }
    }

    /// “合集 14 ›”: the 合集墙. A book held over it a moment brings up every 合集 to drop it on.
    private var allCollectionsRow: some View {
        Button { filter = .collections } label: {
            SidebarRow(symbol: "square.stack", trailing: "\(library.collections.count) ›",
                       on: filter == .collections || (currentCollection.map { id in !pinnedCollections.contains { $0.id == id } } ?? false),
                       drop: dropOverAll, tint: tint) {
                Text("合集")
            }
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(filter == .collections ? .isSelected : [])
        .onDrop(of: [.utf8PlainText, .plainText], isTargeted: Binding(get: { dropOverAll }, set: { over in
            dropOverAll = over
            dropTimer?.cancel()
            if over {
                dropTimer = Task {
                    try? await Task.sleep(for: .milliseconds(500))
                    if !Task.isCancelled { dropListShown = true }
                }
            }
        })) { _ in false }
        .popover(isPresented: $dropListShown, arrowEdge: .trailing) {
            CollectionDropList(library: library) { dropListShown = false }
                .presentationBackground(YYColor.card)
        }
    }

    /// A click folds the group away or back; the arrow shows on hover, as in 访达.
    private func sidebarGroup(_ title: String, key: String) -> some View {
        SidebarGroupTitle(title: title, closed: closedGroups.contains(key)) { toggleGroup(key) }
    }

    private func sidebarRow(_ f: ShelfFilter, _ title: String, _ symbol: String, _ count: Int) -> some View {
        Button { filter = f } label: {
            SidebarRow(symbol: symbol, count: count, on: filter == f) { Text(title) }
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(filter == f ? .isSelected : [])
    }

    /// The rows in order, for ↑ ↓ on the sidebar.
    private var filterOrder: [ShelfFilter] {
        [.all, .books, .podcasts, .audio, .notes, .words, .collections]
            + pinnedCollections.map { .collection($0.id) }
    }

    private func moveFilter(_ direction: MoveCommandDirection) {
        let order = filterOrder
        guard let i = order.firstIndex(of: filter ?? .all) else { return }
        var transaction = Transaction()
        transaction.disablesAnimations = true
        withTransaction(transaction) {
            switch direction {
            case .up where i > 0: filter = order[i - 1]
            case .down where i < order.count - 1: filter = order[i + 1]
            default: break
            }
        }
    }

    /// The sidebar's foot: 同步 on the left, then 设置 and 反馈 as two quiet icons on the right.
    private var sidebarFoot: some View {
        HStack(spacing: 2) {
            syncStatus
            SidebarFootButton(symbol: "gearshape", label: String(localized: "设置")) { openSettings() }
            SidebarFootButton(symbol: "exclamationmark.bubble", label: String(localized: "反馈")) {
                feedbackShown = true
            }
        }
    }

    /// 同步 at the foot of the sidebar; a click opens 设置 · 同步.
    private var syncStatus: some View {
        let sync = library.sync
        let (dot, text): (Color, String) = switch sync.state {
        case .syncing: (tint.accent, String(localized: "正在同步…"))
        case .failed: (YYColor.danger, String(localized: "同步未成功"))
        case .idle where !sync.isOn: (YYColor.t3, String(localized: "尚未开启同步"))
        case .idle: (YYColor.ok, syncPlace(sync.method) + " · " + syncedAgo(sync.lastSynced))
        }
        let devices = sync.isOn && sync.devices.count > 1 ? String(localized: "\(sync.devices.count) 台设备") : nil
        return Button {
            UserDefaults.standard.set(SettingsPage.sync.rawValue, forKey: "settings.page")
            openSettings()
        } label: {
            HStack(spacing: 8) {
                Circle().fill(dot).frame(width: 6, height: 6)
                Text(text)
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(1)
                Spacer(minLength: 0)
                if let devices {
                    Text(devices).font(.yy(size: 10)).foregroundStyle(YYColor.t3)
                }
            }
            .padding(8)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func syncPlace(_ method: Sync.Method) -> String {
        switch method {
        case .icloud: "iCloud"
        case .webdav: "WebDAV"
        case .googleDrive: "Google Drive"
        case .none: String(localized: "同步")
        }
    }

    /// 刚刚同步, 5 分钟前同步, 今天 9:40 同步.
    private func syncedAgo(_ date: Date?) -> String {
        let ago = date.map(Sync.ago) ?? String(localized: "刚刚")
        return ago + (ago.last?.isNumber == true ? String(localized: " 同步") : String(localized: "同步"))
    }

    private func collectionRow(_ c: BookCollection) -> some View {
        Group {
            if renaming == c.id {
                SidebarRow(symbol: CollectionIcon.symbol(for: c)) {
                    TextField("合集的名字", text: $newName)
                        .textFieldStyle(.plain)
                        .focused($nameFocused)
                        .onSubmit { finishRenaming(c) }
                        .onExitCommand { renaming = nil }
                }
                .onChange(of: nameFocused) { if !nameFocused { finishRenaming(c) } }
            } else {
                Button { filter = .collection(c.id) } label: {
                    SidebarRow(symbol: CollectionIcon.symbol(for: c), count: Int(c.count),
                               on: filter == .collection(c.id), drop: dropCollection == c.id, tint: tint) {
                        Text(c.name)
                    }
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(filter == .collection(c.id) ? .isSelected : [])
            }
        }
        // Books dragged here go in; it lights up while one hovers over it.
        // Files from Finder are imported straight into it.
        .onDrop(of: [.fileURL, .utf8PlainText, .plainText], delegate: ShelfDrop(hovering: Binding {
            dropCollection == c.id ? 0 : nil
        } set: { on in
            dropCollection = on != nil ? c.id : (dropCollection == c.id ? nil : dropCollection)
        }, acceptsBooks: true) { urls in
            Task { await library.importFiles(urls, into: c.id) }
        } books: { ids in
            for id in ids {
                if id.hasPrefix(PinnedCollections.dragPrefix) {
                    movePinned(String(id.dropFirst(PinnedCollections.dragPrefix.count)), before: c.id)
                } else if library.books.contains(where: { $0.id == id }) {
                    library.setBook(id, in: c.id, true)
                }
            }
        })
        // Pinned 合集 are put in order by dragging one onto another.
        .draggable(PinnedCollections.dragPrefix + c.id) {
            SidebarRow(symbol: CollectionIcon.symbol(for: c)) { Text(c.name) }.frame(width: 180)
        }
        .contextMenu {
            Button("取消固定") {
                pinnedRaw = PinnedCollections.raw(PinnedCollections.ids(pinnedRaw).filter { $0 != c.id })
            }
            Divider()
            Button("重命名") {
                newName = c.name
                renaming = c.id
                nameFocused = true
            }
            CollectionIconMenu(collection: c, library: library)
            Button("删除合集…", role: .destructive) {
                confirming = YYConfirm(title: String(localized: "删除合集“\(c.name)”？"), message: String(localized: "这个合集删掉，里面的书还在书架上。"), button: String(localized: "删除")) { if filter == .collection(c.id) { filter = .all }; pinnedRaw = PinnedCollections.raw(PinnedCollections.ids(pinnedRaw).filter { $0 != c.id }); library.deleteCollection(c) }
            }
        }
    }

    /// A pinned 合集 dropped on another goes just before it.
    private func movePinned(_ id: String, before target: String) {
        guard id != target else { return }
        var ids = PinnedCollections.ids(pinnedRaw).filter { $0 != id }
        let i = ids.firstIndex(of: target) ?? ids.count
        ids.insert(id, at: i)
        withAnimation(YYMotion.layout) { pinnedRaw = PinnedCollections.raw(ids) }
    }

    private func finishRenaming(_ c: BookCollection) {
        guard renaming == c.id else { return }
        renaming = nil
        let name = newName.trimmingCharacters(in: .whitespaces)
        if !name.isEmpty, name != c.name { library.renameCollection(c.id, to: name) }
    }

    // MARK: 收起 / 展开

    /// 显示侧栏 / 隐藏侧栏 (⌘⌥N, for both windows). A window too narrow
    /// for the sidebar puts it away by itself, until it's asked for again. Remembered.
    @AppStorage("shelf.sidebarShown") private var sidebarWanted = true
    @State private var narrowWindow = false
    @State private var openedWhileNarrow = false

    /// The middle of 显示侧栏 and the page's title, from the window's top.
    static let lineY: CGFloat = 34
    /// Crisp and direct, without overshoot; instant with 减弱动态效果.
    static var slide: Animation? { YYMotion.reduce ? nil : .spring(duration: 0.24, bounce: 0) }

    private var sidebarShown: Bool { sidebarWanted && (!narrowWindow || openedWhileNarrow) }

    /// Where the paper starts: just past the sidebar, or 8 in from the window's edge without it.
    private var paperLeading: CGFloat { sidebarShown ? 220 : ShelfDesk.margin }

    /// The page's title leaves room for 显示侧栏 before it.
    private var headerInset: CGFloat { 30 }

    private func toggleSidebar(animated: Bool = true) {
        let apply = {
            if sidebarShown {
                sidebarWanted = false
                openedWhileNarrow = false
            } else {
                sidebarWanted = true
                openedWhileNarrow = narrowWindow
            }
        }
        if animated { withAnimation(Self.slide, apply) } else { apply() }
    }

    private var sidebarButton: some View {
        SidebarToggle(shown: sidebarShown) { toggleSidebar() }
    }

    /// 资料库, 状态, 笔记, 合集: each folds away by its title, and stays so.
    @AppStorage("shelf.closedGroups") private var closedGroupsRaw = ""
    private var closedGroups: Set<String> { Set(closedGroupsRaw.split(separator: ",").map(String.init)) }

    private func toggleGroup(_ key: String) {
        var set = closedGroups
        if set.contains(key) { set.remove(key) } else { set.insert(key) }
        withAnimation(YYMotion.layout) { closedGroupsRaw = set.sorted().joined(separator: ",") }
    }

    // MARK: 合集 in the sidebar

    @AppStorage(PinnedCollections.key) private var pinnedRaw = ""
    private var pinnedCollections: [BookCollection] {
        PinnedCollections.ids(pinnedRaw).compactMap { id in library.collections.first { $0.id == id } }
    }
    /// A book held over “合集”: after a moment, every 合集 to drop it on.
    @State private var dropOverAll = false
    @State private var dropListShown = false
    @State private var dropTimer: Task<Void, Never>?

    @State private var noteCount = 0
    @State private var wordCount = 0

    private func countNotes() {
        let counts = (try? library.core.markCounts()) ?? [:]
        noteCount = library.books.filter(\.available).reduce(0) { $0 + Int(counts[$1.id] ?? 0) }
        wordCount = (try? library.core.words().count) ?? 0
    }

    // MARK: Main

    private var currentCollection: String? {
        if case .collection(let id) = filter { id } else { nil }
    }

    private var activeCollection: BookCollection? {
        guard let id = currentCollection else { return nil }
        return library.collections.first { $0.id == id }
    }

    private func collectionHeaderMenu(_ c: BookCollection) -> some View {
        let ids = PinnedCollections.ids(pinnedRaw)
        let isPinned = ids.contains(c.id)
        return Menu {
            Button(isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏")) {
                var next = ids
                if let i = next.firstIndex(of: c.id) { next.remove(at: i) } else { next.append(c.id) }
                pinnedRaw = PinnedCollections.raw(next)
            }
            Divider()
            Button("重命名") {
                renamingPromptText = c.name
                renamingPromptID = c.id
            }
            CollectionIconMenu(collection: c, library: library)
            Divider()
            Button("删除合集…", role: .destructive) {
                confirming = YYConfirm(
                    title: String(localized: "删除合集“\(c.name)”？"),
                    message: String(localized: "这个合集删掉，里面的书还在书架上。"),
                    button: String(localized: "删除")
                ) {
                    let backToWall = cameFromWall
                    filter = backToWall ? .collections : .all
                    pinnedRaw = PinnedCollections.raw(PinnedCollections.ids(pinnedRaw).filter { $0 != c.id })
                    library.deleteCollection(c)
                }
            }
        } label: {
            Image(systemName: "ellipsis")
                .font(.yy(size: 11.5, weight: .semibold))
                .foregroundStyle(YYColor.t1)
                .frame(width: 28, height: 28)
                .background {
                    Circle().fill(YYColor.pill)
                        .overlay { Circle().strokeBorder(YYColor.pillLine, lineWidth: 0.5) }
                        .shadow(color: .black.opacity(scheme == .dark || eink ? 0 : 0.04), radius: 2, y: 1)
                }
                .contentShape(.circle)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
        .help(String(localized: "管理合集“\(c.name)”"))
        .accessibilityLabel(String(localized: "管理合集“\(c.name)”"))
    }

    private var title: String {
        switch filter ?? .all {
        case .all: return String(localized: "书")
        case .books: return String(localized: "进度")
        case .podcasts: return String(localized: "播客")
        case .audio: return String(localized: "有声")
        case .status(let s): return s.name
        case .collection(let id): return library.collections.first { $0.id == id }?.name ?? String(localized: "合集")
        case .collections: return String(localized: "全部合集")
        case .notes: return String(localized: "笔记")
        case .words: return String(localized: "生词本")
        }
    }

    private var shown: [BookSummary] {
        var books = library.shelfBooks
        switch filter ?? .all {
        case .all: break
        case .books: break
        case .audio: books = books.filter(library.hasAudio)
        case .status(let s): books = books.filter { $0.status == s }
        case .collection(let id): books = books.filter { $0.collections.contains(id) }
        case .notes, .words, .collections, .podcasts: break
        }
        return sort.sorted(books)
    }

    /// Every book being read that this page would card; the cards show the first three.
    private var allReading: [BookSummary] {
        guard filter == .all || filter == .books || filter == .audio else { return [] }
        let all = filter == .all, audio = filter == .audio
        return library.shelfBooks.filter { b in
            b.status == .reading && (all || !audio || library.hasAudio(b))
        }
    }

    /// Matches the adaptive grid (`minimum: 266, spacing: 12`) so the collapsed strip fills one row.
    private var readingCap: Int {
        let w = readingWidth > 0 ? readingWidth : max(0, mainWidth - 50)
        guard w > 0 else { return 3 }
        return max(1, Int((w + 12) / (266 + 12)))
    }

    private var reading: [BookSummary] {
        readingExpanded ? allReading : Array(allReading.prefix(readingCap))
    }

    /// 设置 · 外观 · 书架封面大小 and 封面下面写进度.
    @AppStorage("shelf.coverSize") private var coverSize = 100.0
    @AppStorage("shelf.showProgress") private var showProgress = true
    /// 墨水屏 has no glass.
    @AppStorage(EInk.key) private var eink = false

    /// At the default size a 1040-wide window holds eight covers about 80 wide. The
    /// covers are sized from the window, room for a scroller always left: sized from inside the
    /// scroller, two rows that just fit grow taller without it, need it again, and never settle.
    /// The room left is generous and the width rounded down: with scrollers always shown, a grid
    /// even a point too wide widens the window's column, which widens the grid, without end.
    private var columns: [GridItem] {
        let room = max(0, mainWidth - 50 - 24)
        let count = max(1, Int((room + 18) / (coverSize - 24 + 18)))
        // Flexible, not fixed: fixed widths made the grid as wide as the window it was measured
        // in, so the window could grow (the sidebar put away) but never be made narrower again.
        // At most the cover size set in 设置: the room left over stays at the end of the row
        // rather than blowing the covers up.
        let width = max(40, ((room - 18 * CGFloat(count - 1)) / CGFloat(count)).rounded(.down))
        return Array(repeating: GridItem(.flexible(minimum: 40, maximum: width), spacing: 18, alignment: .top), count: count)
    }

    private var mainHeader: some View {
        let col = activeCollection
        let showBack = col.map { c in cameFromWall || !pinnedCollections.contains(where: { $0.id == c.id }) } ?? false
        return HStack(spacing: 8) {
            if let col, showBack {
                Button { filter = .collections } label: {
                    HStack(spacing: 4) {
                        Image(systemName: "chevron.left").font(.yy(size: 12, weight: .semibold))
                        Text("合集").font(.yy(size: 12.5))
                    }
                    .foregroundStyle(YYColor.t2)
                    .padding(.vertical, 4)
                    .padding(.trailing, 4)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .keyboardShortcut("[", modifiers: .command)
                .help(String(localized: "回到全部合集"))
                .padding(.leading, headerInset)

                Text(col.name)
                    .font(.yy(size: 22, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
            } else {
                Text(title)
                    .font(.yy(size: 22, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .padding(.leading, headerInset)
            }
            Spacer()
            if let col {
                collectionHeaderMenu(col)
            }
            importButton
        }
        .frame(height: 60)
        .padding(.top, 4)
        .padding(.bottom, 5)
        .padding(.leading, 22)
        .padding(.trailing, 28)
    }

    private var main: some View {
        let currentShown = shown
        let currentAllReading = allReading
        let currentReading = readingExpanded ? currentAllReading : Array(currentAllReading.prefix(readingCap))
        return ScrollViewReader { scroll in
        Group {
            if currentShown.isEmpty {
                VStack(alignment: .leading, spacing: 0) {
                    mainHeader

                    if !library.imports.isEmpty {
                        MacImportCard(library: library, tint: tint) { id in reveal(id) }
                            .padding(.leading, 22)
                            .padding(.trailing, 28)
                            .padding(.bottom, 18)
                            .transition(.opacity.combined(with: .move(edge: .top)))
                    }

                    empty
                        .padding(.horizontal, 28)
                        .padding(.bottom, 56)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        mainHeader

                        if !library.imports.isEmpty {
                            MacImportCard(library: library, tint: tint) { id in reveal(id) }
                                .padding(.leading, 22)
                                .padding(.trailing, 28)
                                .padding(.bottom, 18)
                                .transition(.opacity.combined(with: .move(edge: .top)))
                        }

                        if !currentReading.isEmpty {
                            VStack(alignment: .leading, spacing: 0) {
                                readingHeader(count: currentAllReading.count)
                                Group {
                                    if currentReading.count == 1, let book = currentReading.first {
                                        // 暂时关闭封面取色染色，恢复浅色卡片；需要时改回 tinted: true
                                        // readingTile(book, tinted: true)
                                        readingTile(book, tinted: false)
                                            .frame(maxWidth: 316, alignment: .leading)
                                    } else {
                                        LazyVGrid(columns: [GridItem(.adaptive(minimum: 266), spacing: 12)], spacing: 12) {
                                            ForEach(currentReading) { book in
                                                // readingTile(book, tinted: true)
                                                readingTile(book, tinted: false)
                                            }
                                        }
                                    }
                                }
                                // A book moving into or out of the cards moves, not jumps.
                                .animation(YYMotion.layout, value: currentReading.map(\.id))
                                .onGeometryChange(for: CGFloat.self) { $0.size.width.rounded() } action: { readingWidth = $0 }
                            }
                            .padding(.leading, 22)
                            .padding(.trailing, 28)
                            .padding(.top, 14)
                            .padding(.bottom, 20)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .background(readingBand)
                            .padding(.bottom, 20)
                        }

                        // The books in the 在读 cards above aren't shown again below them.
                        let carded = Set(currentReading.map(\.id))
                        let books = currentShown.filter { !carded.contains($0.id) }
                        if !books.isEmpty {
                            VStack(alignment: .leading, spacing: 0) {
                                HStack {
                                    Text(carded.isEmpty
                                         ? String(localized: "\(books.count) 本")
                                         : String(localized: "其余 \(books.count) 本"))
                                        .font(.yy(size: 12.5, weight: .semibold))
                                        .foregroundStyle(YYColor.t1)
                                    Spacer()
                                    sortMenu
                                }
                                .padding(.horizontal, 2)
                                .padding(.bottom, 14)

                                if case .status(let s) = filter ?? .all {
                                    statusShelves(s, books)
                                } else {
                                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                                        ForEach(books) { book in
                                            cell(book).id(book.id)
                                                .transition(.scale(scale: 0.9).combined(with: .opacity))
                                        }
                                    }
                                    // Sorted another way, a book brought in or taken out, each moves to its place.
                                    .animation(YYMotion.layout, value: books.map(\.id))
                                }
                            }
                            .padding(.leading, 22)
                            .padding(.trailing, 28)
                        }
                    }
                    .padding(.bottom, 80)
                }
                .scrollDisabled(menu != nil)
            }
        }
        // The title sits level with the window's buttons, as in the shelf's design.
        .ignoresSafeArea(.container, edges: .top)
        .blur(radius: dropCount != nil ? 5 : 0)
        .background(YYColor.bg)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { rect in
            mainOriginBox.origin = rect.origin
            let w = rect.width.rounded()
            if abs(mainWidth - w) > 0.5 { mainWidth = w }
        }
        .overlay { menuLayer.animation(YYMotion.quick, value: menu?.book) }
        .overlay { dropHint }
        // Files held over the shelf: it softens and the hint fades up, both quickly.
        .animation(YYMotion.quick, value: dropCount != nil)
        .onDrop(of: [.fileURL], delegate: ShelfDrop(hovering: $dropCount) { urls in
            Task { await library.importFiles(urls, into: currentCollection) }
        })
        .animation(YYMotion.standard, value: library.imports.isEmpty)
        .onChange(of: pendingReveal, initial: true) { _, target in
            guard let target, filter == .all else { return }
            pendingReveal = nil
            Task {
                try? await Task.sleep(for: .milliseconds(80))
                withAnimation(YYMotion.standard) { scroll.scrollTo(target, anchor: .center) }
                revealed = target
                try? await Task.sleep(for: .seconds(1.6))
                withAnimation(YYMotion.standard) { if revealed == target { revealed = nil } }
            }
        }
        }
        .yyHideWindowToolbarChrome(removeSidebarToggle: true)
        .onChange(of: allReading.count <= readingCap) { _, fits in if fits { readingExpanded = false } }
    }

    private var importButton: some View {
        Button { importerShown = true } label: {
            HStack(spacing: 5) {
                if library.importing > 0 {
                    ProgressView().controlSize(.mini)
                    Text("正在导入")
                } else {
                    Image(systemName: "plus").font(.yy(size: 11, weight: .semibold))
                    Text("导入")
                }
            }
            .font(.yy(size: 12))
            .foregroundStyle(YYColor.t1)
            .padding(.horizontal, 13)
            .frame(height: 28)
        }
        .buttonStyle(YYPillChrome())
        .keyboardShortcut("o")
        .help("导入书（⌘O）：EPUB、PDF、MOBI、AZW3、TXT；也可以直接拖进窗口")
    }

    /// A status page cut into 书, 有声书: each shelf with books in it, the empty ones left out.
    private func statusShelves(_ s: ReadingStatus, _ books: [BookSummary]) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(StatusShelf.allCases, id: \.self) { shelf in
                let list = books.filter { shelf.holds($0, in: library) }
                if !list.isEmpty {
                    sectionHeader("\(shelf.name) · \(list.count)", trailing: shelf.verb(s))
                    LazyVGrid(columns: columns, alignment: .leading, spacing: 20) {
                        ForEach(list) { book in
                            cell(book).id(book.id)
                                .transition(.scale(scale: 0.9).combined(with: .opacity))
                        }
                    }
                    .animation(YYMotion.layout, value: list.map(\.id))
                    .padding(.bottom, 28)
                }
            }
        }
    }

    /// One 在读 card with reveal ring and right-click / `···` menu wired.
    private func readingTile(_ book: BookSummary, tinted: Bool) -> some View {
        ReadingCard(
            book: book,
            listening: library.listening == book.id,
            tinted: tinted,
            menuAt: { showMenu(for: book, at: $0) }
        ) { intent in
            open(book, intent)
        }
        .id(book.id)
        .overlay {
            if revealed == book.id {
                RoundedRectangle(cornerRadius: YYRadius.block).strokeBorder(tint.accent, lineWidth: 2.5).padding(-3)
            }
        }
        .overlay(RightClickCatcher { showMenu(for: book, at: $0) })
        .transition(.scale(scale: 0.96).combined(with: .opacity))
    }

    /// Subtle horizontal shelf band behind the 在读 cards so elevated white cards float cleanly.
    private var readingBand: some View {
        let top = Color.yyDynamic(light: YYRGBA(42, 43, 46, 0.022), dark: YYRGBA(255, 255, 255, 0.018))
        let bottom = Color.yyDynamic(light: YYRGBA(42, 43, 46, 0.036), dark: YYRGBA(255, 255, 255, 0.032))
        let hair = Color.yyDynamic(light: YYRGBA(42, 43, 46, 0.045), dark: YYRGBA(255, 255, 255, 0.045))
        return LinearGradient(colors: [top, bottom], startPoint: .top, endPoint: .bottom)
            .overlay(alignment: .top) { Rectangle().fill(hair).frame(height: 0.5) }
            .overlay(alignment: .bottom) { Rectangle().fill(hair).frame(height: 0.5) }
    }

    /// 在读: capped at one row by default, with an inline toggle to show all of them.
    @ViewBuilder
    private func readingHeader(count: Int) -> some View {
        if count > readingCap {
            HStack {
                Text("在读")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                Spacer()
                Button {
                    withAnimation(YYMotion.layout) { readingExpanded.toggle() }
                } label: {
                    HStack(spacing: 4) {
                        Text(readingExpanded ? String(localized: "收起") : String(localized: "全部 \(count) 本"))
                        Image(systemName: readingExpanded ? "chevron.up" : "chevron.down")
                            .font(.yy(size: 9, weight: .semibold))
                    }
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t2)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 2)
            .padding(.bottom, 10)
        } else {
            sectionHeader(String(localized: "在读"), trailing: String(localized: "\(count) 本"))
        }
    }

    private func sectionHeader(_ text: String, trailing: String) -> some View {
        HStack {
            Text(text)
                .font(.yy(size: 12.5, weight: .semibold))
                .foregroundStyle(YYColor.t1)
            Spacer()
            Text(trailing)
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
        }
        .padding(.horizontal, 2)
        .padding(.bottom, 10)
    }

    private var sortMenu: some View {
        Menu {
            Picker("排序", selection: $sort) {
                ForEach(ShelfSort.allCases, id: \.self) { Text($0.name).tag($0) }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            HStack(spacing: 4) {
                Text(sort.name)
                Image(systemName: "chevron.up.chevron.down").font(.yy(size: 9, weight: .semibold))
            }
            .font(.yy(size: 11.5))
            .foregroundStyle(YYColor.t2)
            .contentShape(.rect)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .menuIndicator(.hidden)
        .fixedSize()
    }

    @ViewBuilder private var empty: some View {
        if filter == .audio {
            VStack(spacing: 10) {
                Image(systemName: "waveform")
                    .font(.yy(size: 28, weight: .light))
                    .foregroundStyle(YYColor.t3)
                Text("还没有有声书")
                    .font(.yy(size: 13, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                Text("把 M4B、MP3、M4A 拖进窗口，或导入音频与书架上的电子书合成听读同步。")
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t2)
                    .multilineTextAlignment(.center)
                    .lineSpacing(3)
                    .frame(maxWidth: 340)
                Button { importerShown = true } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "plus").font(.yy(size: 11, weight: .semibold))
                        Text("导入有声书")
                    }
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, 13)
                    .frame(height: 28)
                }
                .buttonStyle(YYPillChrome())
                .padding(.top, 6)
            }
        } else {
            let (symbol, text): (String, String) = if library.books.isEmpty {
                ("books.vertical", String(localized: "书架是空的，把 EPUB、PDF、MOBI、TXT 拖进窗口就能开始读"))
            } else if let colID = currentCollection {
                if pinnedCollections.contains(where: { $0.id == colID }) {
                    ("tray", String(localized: "还没有书。把书拖到左边这个合集上，或者把文件拖进来"))
                } else {
                    ("tray", String(localized: "还没有书。把文件拖进窗口，或在书架上右键一本书“加入合集…”"))
                }
            } else {
                ("book.closed", String(localized: "这里还没有书"))
            }
            MacShelfEmpty(symbol: symbol, text: text)
        }
    }

    private func cell(_ book: BookSummary) -> some View {
        Button { open(book, nil) } label: {
            VStack(alignment: .leading, spacing: 0) {
                BookCover(book: book)
                    .opacity(book.available ? 1 : 0.45)
                    // The cover lifts under the pointer.
                    .yyLift()
                    .overlay {
                        if revealed == book.id {
                            RoundedRectangle(cornerRadius: 4).strokeBorder(tint.accent, lineWidth: 2.5).padding(-4)
                        }
                    }
                    .overlay(alignment: .bottomTrailing) {
                        if !book.available {
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
                    .padding(.bottom, 9)
                    .popover(isPresented: Binding(get: { picking == book.id }, set: { if !$0 { picking = nil } }), arrowEdge: .trailing) {
                        CollectionPicker(library: library, bookID: book.id) { picking = nil }
                            .presentationBackground(YYColor.bg)
                    }
                Text(book.title)
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .padding(.bottom, 2)
                if showProgress { note(book) }
            }
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle())
        .overlay(RightClickCatcher { showMenu(for: book, at: $0) })
        .draggable(book.id) {
            BookCover(book: book).frame(width: 60)
        }
        .accessibilityLabel("《\(book.title)》\(book.author.map { "，\($0)" } ?? "")，\(book.shelfNote)")
        .accessibilityAction(named: "更多") { showMenu(for: book, at: nil) }
    }

    @ViewBuilder private func note(_ book: BookSummary) -> some View {
        if library.listening == book.id {
            Label("正在听 · \(book.percent)", systemImage: "headphones")
                .labelStyle(TightLabel())
                .font(.yy(size: 10.5))
                .monospacedDigit()
                .foregroundStyle(tint.accent)
        } else {
            Text(library.shelfNote(book))
                .font(.yy(size: 10.5))
                .monospacedDigit()
                .foregroundStyle(YYColor.t3)
        }
    }

    // MARK: Right-click card

    /// Opens the card where the click was (`point` in the window), or near the top when
    /// asked for from the keyboard or VoiceOver.
    private func showMenu(for book: BookSummary, at point: CGPoint?) {
        picking = nil
        let origin = mainOriginBox.origin
        let at = point.map { CGPoint(x: $0.x - origin.x, y: $0.y - origin.y) } ?? CGPoint(x: 40, y: 80)
        menu = OpenMenu(book: book.id, at: at)
    }

    @ViewBuilder private var menuLayer: some View {
        if let menu, let book = library.books.first(where: { $0.id == menu.book }) {
            GeometryReader { g in
                ZStack(alignment: .topLeading) {
                    // A click anywhere else puts it away.
                    Color.clear
                        .contentShape(.rect)
                        .onTapGesture { self.menu = nil }
                        .overlay(RightClickCatcher { _ in self.menu = nil })
                    BookMenuCard(book: book, perform: { perform($0, on: book) }, close: { self.menu = nil },
                                 setStatus: { library.setStatus(book, $0) })
                        .id(menu.book)
                        .modifier(MenuPop(at: menu.at, in: g.size, menuSize: $menuSize))
                        .transition(.opacity)
                }
            }
        }
    }

    /// From the pointer toward the lower right; to the left or up near the window's edges,
    /// scaling around the card's own anchored corner before applying offset.
    private struct MenuPop: ViewModifier {
        let at: CGPoint
        let size: CGSize
        @Binding var menuSize: CGSize
        @State private var shown = false

        init(at: CGPoint, in size: CGSize, menuSize: Binding<CGSize>) {
            self.at = at
            self.size = size
            self._menuSize = menuSize
        }

        func body(content: Content) -> some View {
            let margin: CGFloat = 8
            let flippedX = at.x + 2 + menuSize.width > size.width - margin
            let flippedY = at.y + 2 + menuSize.height > size.height - margin
            let x = flippedX ? at.x - menuSize.width - 2 : at.x + 2
            let y = flippedY ? at.y - menuSize.height - 2 : at.y + 2
            let anchor = UnitPoint(x: flippedX ? 1 : 0, y: flippedY ? 1 : 0)
            return content
                .onGeometryChange(for: CGSize.self, of: \.size) { menuSize = $0 }
                .scaleEffect(shown || YYMotion.reduce ? 1 : 0.96, anchor: anchor)
                .opacity(shown ? 1 : 0)
                .onAppear { withAnimation(YYMotion.quick) { shown = true } }
                .offset(x: max(margin, x), y: max(margin, y))
        }
    }

    private func perform(_ action: BookAction, on book: BookSummary) {
        menu = nil
        switch action {
        case .resume: open(book, nil)
        case .listenFromStart: open(book, .listenFromStart)
        case .audiobook: library.audiobooks.showing = library.audiobooks.audiobook(for: book.id)?.id
        case .toggleFinished: library.setFinished(book, book.status != .finished)
        case .editInfo: editing = book
        case .addToCollection:
            // After the card has gone, so the two don't overlap. 书 has no cover to hang the
            // popover from: a sheet there.
            Task {
                if filter == .books || { if case .status = filter { true } else { false } }() { pickingSheet = book.id } else { picking = book.id }
            }
        case .revealInFinder:
            if let url = library.originalFile(of: book) {
                NSWorkspace.shared.activateFileViewerSelecting([url])
            }
        case .delete:
            // Kept something of it (a score, a line, 读过…): ask whether to keep that.
            if book.hasRecord || book.record { deletingRecord = book } else { library.delete(book) }
        }
    }

    /// Files opened with 语阅 from 访达, all of them on one import card.
    private func takeOpenedFiles() {
        let urls = AppDelegate.waiting
        guard !urls.isEmpty else { return }
        AppDelegate.waiting = []
        Task { await library.importFiles(urls, source: "share_in") }
    }

    private func open(_ book: BookSummary, _ intent: Library.OpenIntent?) {
        guard book.available else {
            library.note(icon: "icloud.and.arrow.down", String(localized: "《\(book.title)》在别的设备上"), String(localized: "把同一个文件拖进来，进度和笔记就接上了"))
            return
        }
        guard library.open(book, intent) else { return }
        openWindow(id: "reader", value: book.id)
    }

    // MARK: Overlays

    @ViewBuilder private var toast: some View {
        if let note = library.toast {
            UndoToast(toast: note, undo: { library.undo() })
                .padding(.top, 10)
                .transition(UndoToast.transition)
                .id(note.id)
        }
    }

    @ViewBuilder private var dropHint: some View {
        if let count = dropCount {
            MacDropHint(count: count, place: currentCollection == nil ? String(localized: "书") : title, tint: tint)
                .transition(.opacity)
        }
    }

    /// 在书架里显示: back to 全部, scrolled to the book, ringed for a moment.
    private func reveal(_ id: String) {
        if filter != .all { filter = .all }
        pendingReveal = id
    }
}

/// The two shelves a status page is cut into. A book is on one of them: a book with an audiobook
/// on 有声书, the rest on 书. The words for a shelf follow its kind: 在听 and 听完 for audiobooks.
private enum StatusShelf: CaseIterable {
    case books, audio

    var name: String {
        switch self {
        case .books: String(localized: "书")
        case .audio: String(localized: "有声书")
        }
    }

    func holds(_ book: BookSummary, in library: Library) -> Bool {
        switch self {
        case .books: !library.hasAudio(book)
        case .audio: library.hasAudio(book)
        }
    }

    /// The status in this shelf's words, as its header says it on the right.
    func verb(_ status: ReadingStatus) -> String {
        switch (status, self) {
        case (.reading, .audio): String(localized: "在听")
        case (.reading, _): String(localized: "在读")
        case (.want, .audio): String(localized: "想听")
        case (.want, _): String(localized: "想读")
        case (.finished, .audio): String(localized: "听完")
        case (.finished, _): String(localized: "读完")
        case (.paused, _): String(localized: "搁置")
        case (.dropped, _): String(localized: "弃读")
        }
    }
}

/// 首页导航: the shelf window is a desk a shade darker than the page, the sidebar written on
/// it, and the page a sheet of paper lying on it.
enum ShelfDesk {
    static let fill = Color.yyDynamic(light: YYRGBA(234, 232, 227), dark: YYRGBA(12, 12, 13))
    /// The row being shown: a faint wash, not a white card (white is for what's on the paper).
    static let on = Color.yyDynamic(light: YYRGBA(42, 43, 46, 0.07), dark: YYRGBA(255, 255, 255, 0.08))
    /// 搜索 · 问 on the desk: darker than the desk, as the page's own fields are than the page.
    static let field = Color.yyDynamic(light: YYRGBA(0, 0, 0, 0.05), dark: YYRGBA(255, 255, 255, 0.06))
    static let margin: CGFloat = 8
    static let paperRadius: CGFloat = 12

    /// The paper: the page's colour, a hairline edge and a soft shadow under it.
    struct Paper: View {
        @Environment(\.colorScheme) private var scheme

        var body: some View {
            let shape = RoundedRectangle(cornerRadius: ShelfDesk.paperRadius)
            // The hairline is `box-shadow: 0 0 0 .5px`: just outside the paper, over the desk, so
            // the edge reads crisp. Drawn inside it fell on the paper and all but vanished.
            let ring = shape.inset(by: -0.25)
            if scheme == .dark {
                shape.fill(YYColor.bg)
                    .shadow(color: .black.opacity(0.4), radius: 12, y: 8)
                    .overlay(ring.stroke(.white.opacity(0.07), lineWidth: 0.5))
            } else {
                shape.fill(YYColor.bg)
                    .shadow(color: Color(yy: YYRGBA(51, 46, 41, 0.06)), radius: 12, y: 8)
                    .shadow(color: Color(yy: YYRGBA(51, 46, 41, 0.06)), radius: 1.5, y: 1)
                    .overlay(ring.stroke(.black.opacity(0.07), lineWidth: 0.5))
            }
        }
    }
}

/// An icon at the sidebar's foot, beside 同步: 26 square, the desk's faint wash under the pointer.
private struct SidebarFootButton: View {
    var symbol: String? = nil
    var asset: String? = nil
    let label: String
    var ink: Color?
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Group {
                if let asset {
                    Image(asset)
                        .resizable()
                        .renderingMode(.template)
                        .scaledToFit()
                        .frame(width: 16, height: 16)
                } else if let symbol {
                    Image(systemName: symbol)
                        .font(.yy(size: 12.5))
                }
            }
            .foregroundStyle(ink ?? (hover ? YYColor.t1 : YYColor.t2))
            .frame(width: 26, height: 26)
            .background(hover ? ShelfDesk.on : .clear, in: .rect(cornerRadius: 7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15), value: hover)
        .help(label)
        .accessibilityLabel(label)
    }
}

/// An icon in grey, the name, the count on the right. The row being shown is a
/// white card; a collection with a book dragged over it lights up in the accent.
/// A group's title folds it away: a click on it, or on the arrow that shows while the
/// pointer is over it (as in 访达).
private struct SidebarGroupTitle: View {
    let title: String
    let closed: Bool
    let toggle: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: toggle) {
            HStack(spacing: 4) {
                Text(title)
                    .font(.yy(size: 10.5, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 4)
                Image(systemName: "chevron.right")
                    .font(.yy(size: 9, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                    .rotationEffect(.degrees(closed ? 0 : 90))
                    .opacity(hover || closed ? 1 : 0)
                    .animation(YYMotion.quick, value: hover)
            }
            .padding(.horizontal, 8)
            .padding(.top, 14)
            .padding(.bottom, 4)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .accessibilityAddTraits(.isHeader)
        .accessibilityValue(closed ? String(localized: "已收起") : String(localized: "已展开"))
        .help(closed ? String(localized: "展开") : String(localized: "收起"))
    }
}

private struct SidebarRow<Title: View>: View {
    let symbol: String
    var count: Int?
    /// In place of the count: “14 ›”.
    var trailing: String?
    var on = false
    var drop = false
    var tint = YYTint.standard
    var color: Color?
    @ViewBuilder let title: Title
    @Environment(\.sidebarSelection) private var selection

    var body: some View {
        HStack(spacing: 8) {
            // Every icon in the same 14 × 14 box, wide ones too.
            Group {
                if let drawn = ProtoIcon(rawValue: symbol) {
                    drawn.shape.stroke(style: StrokeStyle(lineWidth: drawn.line, lineCap: .round, lineJoin: .round))
                } else if symbol.hasPrefix("asset.") {
                    // 全部 (the phone's 书架 tab icon): an SVG in the asset catalogue.
                    Image(String(symbol.dropFirst(6))).resizable().renderingMode(.template).scaledToFit()
                } else {
                    Image(systemName: symbol).resizable().scaledToFit()
                }
            }
            .frame(width: 14, height: 14)
            .foregroundStyle(drop ? tint.accent : color ?? (on ? YYColor.t1 : YYColor.t2))
            title
                .font(.yy(size: 12, weight: on || drop ? .semibold : .regular))
                .foregroundStyle(drop ? tint.accent : color ?? YYColor.t1)
                .lineLimit(1)
            Spacer(minLength: 4)
            if let text = trailing ?? count?.formatted() {
                Text(text)
                    .font(.yy(size: 10.5).monospacedDigit())
                    .foregroundStyle(drop ? tint.accent : YYColor.t3)
                    // A count rolls when a book comes or goes.
                    .contentTransition(.numericText(value: Double(count ?? 0)))
                    .animation(YYMotion.standard, value: count)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 28)
        .background {
            if drop {
                RoundedRectangle(cornerRadius: 8).fill(tint.accentSoft)
                    .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(tint.accent, lineWidth: 1.5))
            } else if on {
                // One white shape for the whole sidebar, so a new choice slides it there.
                if let selection {
                    RoundedRectangle(cornerRadius: 8).fill(ShelfDesk.on)
                        .matchedGeometryEffect(id: "selection", in: selection)
                } else {
                    RoundedRectangle(cornerRadius: 8).fill(ShelfDesk.on)
                }
            }
        }
        .animation(YYMotion.quick, value: drop)
        .contentShape(.rect)
    }
}

private struct SidebarSelectionKey: EnvironmentKey {
    static let defaultValue: Namespace.ID? = nil
}

extension EnvironmentValues {
    /// The sidebar's selection, shared by its rows so it slides between them.
    var sidebarSelection: Namespace.ID? {
        get { self[SidebarSelectionKey.self] }
        set { self[SidebarSelectionKey.self] = newValue }
    }
}

/// 编辑信息: the cover and 换封面, the title and author the shelf shows.
struct EditInfoSheet: View {
    let library: Library
    let book: BookSummary
    @State private var title: String
    @State private var author: String
    @Environment(\.dismiss) private var dismiss

    init(library: Library, book: BookSummary) {
        self.library = library
        self.book = book
        _title = State(initialValue: book.title)
        _author = State(initialValue: book.authors.joined(separator: String(localized: "、")))
    }

    private var current: BookSummary { library.books.first { $0.id == book.id } ?? book }

    var body: some View {
        VStack(alignment: .leading, spacing: YYUI.s(12)) {
            YYSheetHeader(title: String(localized: "编辑信息")) { dismiss() }
            VStack(spacing: YYUI.s(8)) {
                BookCover(book: current)
                    .frame(width: YYUI.s(58))
                    .shadow(color: Color(yy: YYRGBA(30, 30, 30, 0.16)), radius: YYUI.s(6), y: YYUI.s(4))
                Button("换封面", action: pickCover)
                    .buttonStyle(.plain)
                    .font(YYUI.font(11, .semibold))
                    .foregroundStyle(YYTint.standard.accent)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, YYUI.s(4))
            .dropDestination(for: URL.self) { urls, _ in
                guard let url = urls.first, let data = try? Data(contentsOf: url) else { return false }
                library.setCover(current, image: data)
                return true
            }
            YYGroup {
                YYField(label: String(localized: "书名"), labelWidth: 44) {
                    TextField("", text: $title, prompt: Text("留空就用文件里的")).textFieldStyle(.plain)
                }
                YYField(label: String(localized: "作者"), labelWidth: 44) {
                    TextField("", text: $author, prompt: Text("几位作者用顿号隔开")).textFieldStyle(.plain)
                }
            }
            .foregroundStyle(YYColor.t1)
            YYNote(String(localized: "只改书架上显示的名字，书的文件不动。封面也可以直接拖一张图到上面。"))
                .padding(.top, -YYUI.s(6))
            Button("好") {
                let authors = author.split(whereSeparator: { "、,，;；/".contains($0) }).map { $0.trimmingCharacters(in: .whitespaces) }
                if title != book.title || authors != book.authors {
                    library.editInfo(book, title: title, authors: authors)
                }
                dismiss()
            }
            .buttonStyle(YYButtonStyle(kind: .main, size: .large, wide: true))
            .keyboardShortcut(.defaultAction)
        }
        .padding(YYUI.s(16))
        .frame(width: 360)
        .background(YYColor.bg)
    }

    private func pickCover() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.image]
        panel.prompt = String(localized: "用作封面")
        guard panel.runModal() == .OK, let url = panel.url, let data = try? Data(contentsOf: url) else { return }
        library.setCover(current, image: data)
    }
}

/// The shelf with nothing to show. The window catalog still shoots the whole window; this is the
/// empty state on its own, for the snapshot tests.
struct MacShelfEmpty: View {
    var symbol: String
    var text: String

    var body: some View {
        VStack(spacing: 10) {
            Image(systemName: symbol).font(.yy(size: 28, weight: .light)).foregroundStyle(YYColor.t3)
            Text(text).font(.yy(size: 12.5)).foregroundStyle(YYColor.t2)
        }
        .frame(maxWidth: .infinity, minHeight: 260)
    }
}
#endif

enum ShelfSort: CaseIterable {
    case recent, added, title, author

    var name: String {
        switch self {
        case .recent: String(localized: "最近读过")
        case .added: String(localized: "加入时间")
        case .title: String(localized: "书名")
        case .author: String(localized: "作者")
        }
    }

    func sorted(_ books: [BookSummary]) -> [BookSummary] {
        switch self {
        case .recent: books
        case .added: books.sorted { $0.addedAt > $1.addedAt }
        case .title: books.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        case .author: books.sorted { ($0.author ?? "").localizedStandardCompare($1.author ?? "") == .orderedAscending }
        }
    }
}

/// A book being read: compact horizontal card with cover, progress, listen toggle, and menu.
struct ReadingCard: View {
    let book: BookSummary
    let listening: Bool
    var tinted = false
    var menuAt: ((CGPoint) -> Void)? = nil
    let open: (Library.OpenIntent?) -> Void
    @Environment(\.colorScheme) private var scheme
    private var tint: YYTint { YYTint.standard }
    #if os(macOS)
    /// The cover's own colour, which the lead card is dyed in.
    @State private var hue: CoverHue?
    @State private var cardHover = false
    @State private var listenHover = false
    @State private var moreHover = false
    @AppStorage(EInk.key) private var eink = false
    #endif

    var body: some View {
        #if os(macOS)
        macBody
        #else
        phoneBody
        #endif
    }

    #if os(macOS)
    /// Compact horizontal book card: inset cover, three-line info stack,
    /// right-side 🎧 & ··· icon cluster, and a whisper-thin bottom progress hairline.
    private var macBody: some View {
        let useTint = tinted && !eink
        let tintColors = useTint ? (hue ?? CoverHue.cached(book.coverPath) ?? .paper).continueGradient(dark: scheme == .dark) : nil
        let titleColor: Color = useTint ? .white : YYColor.t1
        let authorColor: Color = useTint ? .white.opacity(0.85) : YYColor.t2
        let metaColor: Color = useTint ? .white.opacity(0.68) : YYColor.t3
        let pctColor: Color = useTint ? .white.opacity(0.95) : tint.accent

        return ZStack {
            // Whole card opens the book directly (no bulky bottom "接着读" bar).
            Button { open(audio ? .listen : nil) } label: { Color.clear }
                .buttonStyle(.plain)
                .accessibilityLabel("《\(book.title)》\(book.author.map { "，\($0)" } ?? "")，\(book.percent)")

            HStack(spacing: 12) {
                BookCover(book: book)
                    .frame(width: 46, height: 64)
                    .shadow(color: useTint ? .black.opacity(0.24) : .clear, radius: 4, y: 2)
                    .allowsHitTesting(false)

                VStack(alignment: .leading, spacing: 2.5) {
                    Text(book.title)
                        .font(.yy(size: 13.5, weight: .semibold))
                        .foregroundStyle(titleColor)
                        .lineLimit(1)
                    if let author = book.author, !author.isEmpty {
                        Text(author)
                            .font(.yy(size: 11.5))
                            .foregroundStyle(authorColor)
                            .lineLimit(1)
                    }
                    macMetaLine(metaColor: metaColor, pctColor: pctColor)
                        .padding(.top, 1)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .allowsHitTesting(false)

                HStack(spacing: 2) {
                    listenButton(useTint: useTint, tintBg: tintColors?.right ?? .clear)
                    if let menuAt {
                        moreButton(useTint: useTint, menuAt: menuAt)
                    }
                }
            }
            .padding(.horizontal, 11)
            .padding(.vertical, 11)
            .frame(maxWidth: .infinity, minHeight: 88, alignment: .leading)
        }
        .overlay(alignment: .bottom) {
            progressHairline(useTint: useTint)
                .padding(.horizontal, 12)
                .allowsHitTesting(false)
        }
        .background {
            if let colors = tintColors {
                RoundedRectangle(cornerRadius: YYRadius.block)
                    .fill(
                        LinearGradient(
                            colors: [colors.left, colors.right],
                            startPoint: .leading,
                            endPoint: .trailing
                        )
                    )
            } else {
                RoundedRectangle(cornerRadius: YYRadius.block)
                    .fill(YYColor.card)
            }
        }
        .clipShape(.rect(cornerRadius: YYRadius.block))
        .overlay {
            RoundedRectangle(cornerRadius: YYRadius.block)
                .strokeBorder(
                    useTint
                        ? Color.white.opacity(0.14)
                        : (scheme == .dark ? Color.white.opacity(0.06) : Color(yy: YYRGBA(42, 43, 46, 0.06))),
                    lineWidth: 0.5
                )
        }
        .shadow(
            color: cardShadowColor(useTint: useTint),
            radius: eink ? 0 : (cardHover ? 9 : 5),
            y: eink ? 0 : (cardHover ? 4 : 2)
        )
        .offset(y: cardHover && !YYMotion.reduce ? -1 : 0)
        .animation(YYMotion.quick, value: cardHover)
        .animation(.easeOut(duration: 0.22), value: hue)
        .onHover { cardHover = $0 }
        .help(book.remaining.isEmpty ? book.title : "\(book.title) · \(book.remaining)")
        .task(id: book.coverPath) {
            guard tinted else { return }
            if let hit = CoverHue.cached(book.coverPath) {
                hue = hit
            } else {
                hue = await CoverHue.load(book.coverPath)
            }
        }
    }

    private func cardShadowColor(useTint: Bool) -> Color {
        if eink { return .clear }
        if scheme == .dark { return .black.opacity(cardHover ? 0.44 : 0.32) }
        if useTint { return .black.opacity(cardHover ? 0.18 : 0.13) }
        return Color(yy: YYRGBA(42, 43, 46, cardHover ? 0.10 : 0.065))
    }

    @ViewBuilder
    private func macMetaLine(metaColor: Color, pctColor: Color) -> some View {
        let tail: String? = if book.format == .pdf, book.page > 0 {
            String(localized: "第 \(book.page) 页")
        } else if !book.remaining.isEmpty {
            book.remaining
        } else {
            nil
        }
        let kind = audio ? String(localized: "有声") : String(localized: "图书")
        Group {
            if listening {
                (Text(String(localized: "正在听")).foregroundStyle(pctColor).fontWeight(.semibold)
                 + Text(" · \(book.percent)").foregroundStyle(metaColor)
                 + Text(tail.map { " · \($0)" } ?? "").foregroundStyle(metaColor))
            } else {
                (Text("\(kind) · ").foregroundStyle(metaColor)
                 + Text(book.percent).foregroundStyle(pctColor).fontWeight(.semibold)
                 + Text(tail.map { " · \($0)" } ?? "").foregroundStyle(metaColor))
            }
        }
        .font(.yy(size: 11).monospacedDigit())
        .lineLimit(1)
        .contentTransition(.numericText(value: book.progress))
    }

    private func progressHairline(useTint: Bool) -> some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(useTint ? Color.white.opacity(0.18) : (scheme == .dark ? Color.white.opacity(0.08) : Color(yy: YYRGBA(42, 43, 46, 0.06))))
                Capsule()
                    .fill(useTint ? Color.white.opacity(0.88) : tint.fill)
                    .frame(width: min(g.size.width, max(10, g.size.width * min(1, book.progress))))
            }
        }
        .frame(height: 2.5)
        .animation(YYMotion.standard, value: book.progress)
    }

    private func listenButton(useTint: Bool, tintBg: Color) -> some View {
        let fg: Color = if listening {
            useTint ? tintBg : tint.accent
        } else {
            useTint ? .white.opacity(listenHover ? 1 : 0.82) : (listenHover ? YYColor.t1 : YYColor.t3)
        }
        let bg: Color = if listening {
            useTint ? .white.opacity(0.94) : tint.accentSoft
        } else if listenHover {
            useTint ? .white.opacity(0.18) : YYColor.inset
        } else {
            .clear
        }
        return Button { open(listening ? nil : .listen) } label: {
            Image(systemName: listening ? "waveform" : "headphones")
                .contentTransition(.symbolEffect(.replace))
                .symbolEffect(.variableColor.iterative, options: .repeating, isActive: listening)
                .font(.yy(size: 13, weight: .medium))
                .foregroundStyle(fg)
                .frame(width: 28, height: 28)
                .background(bg, in: .rect(cornerRadius: 8))
                .contentShape(.rect(cornerRadius: 8))
                .animation(YYMotion.quick, value: listening)
        }
        .buttonStyle(YYPressStyle(scale: 0.92))
        .onHover { listenHover = $0 }
        .help(listening ? String(localized: "正在听") : String(localized: "听"))
        .accessibilityLabel(listening ? String(localized: "正在听") : String(localized: "听"))
    }

    private func moreButton(useTint: Bool, menuAt: @escaping (CGPoint) -> Void) -> some View {
        let fg: Color = useTint ? .white.opacity(moreHover ? 1 : 0.82) : (moreHover ? YYColor.t1 : YYColor.t3)
        let bg: Color = moreHover ? (useTint ? .white.opacity(0.18) : YYColor.inset) : .clear
        return Image(systemName: "ellipsis")
            .font(.yy(size: 13, weight: .semibold))
            .foregroundStyle(fg)
            .frame(width: 28, height: 28)
            .background(bg, in: .rect(cornerRadius: 8))
            .contentShape(.rect(cornerRadius: 8))
            .onHover { moreHover = $0 }
            .onTapGesture(coordinateSpace: .global) { menuAt($0) }
            .help(String(localized: "更多"))
            .accessibilityLabel(String(localized: "更多"))
            .accessibilityAddTraits(.isButton)
    }
    #endif

    private var phoneBody: some View {
        HStack(alignment: .top, spacing: 12) {
            BookCover(book: book)
                .frame(width: 44)
                .onTapGesture { open(nil) }
            VStack(alignment: .leading, spacing: 0) {
                Text(book.title)
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .padding(.bottom, 3)
                Text(meta)
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
                    .padding(.bottom, 8)
                ProgressLine(value: book.progress)
                    .padding(.bottom, 10)
                HStack(spacing: 6) {
                    // 有声书 has only 接着听, moving while it plays.
                    if audio, listening {
                        pill(String(localized: "正在听"), symbol: "waveform", fill: tint.accentSoft, text: tint.accent) { open(nil) }
                    } else if audio {
                        pill(String(localized: "接着听"), fill: tint.black, text: tint.onBlack) { open(.listen) }
                    } else {
                        pill(String(localized: "接着读"), fill: tint.black, text: tint.onBlack) { open(nil) }
                        if listening {
                            pill(String(localized: "正在听"), symbol: "waveform", fill: tint.accentSoft, text: tint.accent) { open(nil) }
                        } else {
                            pill(String(localized: "听"), fill: YYColor.inset, text: YYColor.t1) { open(.listen) }
                        }
                    }
                }
            }
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(YYColor.card, in: .rect(cornerRadius: YYRadius.block))
        .yyShadow(scheme == .dark ? YYShadow.ringDark : YYShadow.ringLight)
    }

    private var audio: Bool { book.format == .audio }

    private var meta: String {
        if book.format == .pdf, book.page > 0 { return [book.author, String(localized: "第 \(book.page) 页"), book.percent].compactMap { $0 }.joined(separator: " · ") }
        // The percentage is on 接着读 already; here, how long is left.
        return [book.author, book.remaining, listening ? String(localized: "正在听") : nil].compactMap { $0 }.joined(separator: " · ")
    }

    private func pill(_ title: String, symbol: String? = nil, fill: Color, text: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if let symbol {
                    Image(systemName: symbol).symbolEffect(.variableColor.iterative, options: .repeating)
                }
                Text(title).fixedSize()
            }
            .font(.yy(size: 11.5, weight: .medium))
            .foregroundStyle(text)
            .padding(.horizontal, 12)
            .frame(height: 26)
            .background(fill, in: .capsule)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
    }
}

#if os(macOS)
/// Apple Books–style dominant cover pigment analysis and horizontal linear gradient
/// for 在读 ("Continue") cards: trims outer border artifacts, clusters pixels by area
/// across 24 circular hue bins plus dark/light neutral buckets, normalizes relative
/// luminance for crisp white text contrast, and steps down smoothly from leading to trailing.
nonisolated struct CoverHue: Equatable, Sendable {
    enum Kind: Equatable, Sendable {
        case darkNeutral
        case chromatic(hue: Double, saturation: Double, brightness: Double)
        case lightPaper(hue: Double, saturation: Double)
    }

    var kind: Kind

    /// No cover: the warm 纸本 paper placeholder tone.
    static let paper = CoverHue(kind: .lightPaper(hue: 0.105, saturation: 0.08))

    private static let cache = Mutex<[String: CoverHue]>([:])

    static func cached(_ path: String?) -> CoverHue? {
        guard let path, !path.isEmpty else { return .paper }
        return cache.withLock { $0[path] }
    }

    /// Horizontal 90° linear gradient (`leading` → `trailing`) matching Apple Books' Continue cards.
    func continueGradient(dark: Bool) -> (left: Color, right: Color) {
        switch kind {
        case .darkNeutral:
            let vL = dark ? 0.25 : 0.302
            let vR = dark ? 0.21 : 0.259
            return (
                Color(.sRGB, red: vL, green: vL, blue: dark ? vL + 0.005 : vL),
                Color(.sRGB, red: vR, green: vR, blue: dark ? vR + 0.005 : vR)
            )
        case .chromatic(var h, var s, let v):
            // Warm-shift bright yellow/orange slightly toward amber/terracotta so darkening never turns olive-muddy.
            if h > 0.055 && h < 0.18 && v > 0.65 {
                h = max(0.032, h - 0.038)
                s = min(0.90, s * 1.06)
            }
            // Recover diluted HSV saturation when bringing pastel tints down into mid-tone luminance.
            if v > 0.82 && s < 0.48 {
                s = min(0.66, max(s * 1.62, 0.42))
            } else {
                s = min(0.88, max(s * 0.96, 0.28))
            }
            if dark {
                s = min(0.76, s * 0.88)
            }

            let targetLum = dark
                ? min(0.13, max(0.065, 0.06 + 0.07 * v))
                : min(0.185, max(0.09, 0.08 + 0.115 * v))

            var lo = 0.18, hi = 0.82
            for _ in 0 ..< 18 {
                let mid = (lo + hi) * 0.5
                let (rr, gg, bb) = Self.hsvToRGB(h: h, s: s, v: mid)
                if Self.relativeLuminance(r: rr, g: gg, b: bb) < targetLum {
                    lo = mid
                } else {
                    hi = mid
                }
            }
            let vL = (lo + hi) * 0.5
            let vR = max(0.16, vL * 0.86)
            let sR = max(0.18, s * 0.93)
            let (lR, lG, lB) = Self.hsvToRGB(h: h, s: s, v: vL)
            let (rR, rG, rB) = Self.hsvToRGB(h: h, s: sR, v: vR)
            return (
                Color(.sRGB, red: lR, green: lG, blue: lB),
                Color(.sRGB, red: rR, green: rG, blue: rB)
            )
        case .lightPaper(let h, let s):
            let sat = min(0.14, max(s * 1.5, 0.04))
            let vL = dark ? 0.30 : 0.53
            let vR = dark ? 0.25 : 0.46
            let (lR, lG, lB) = Self.hsvToRGB(h: h, s: sat, v: vL)
            let (rR, rG, rB) = Self.hsvToRGB(h: h, s: sat * 0.9, v: vR)
            return (
                Color(.sRGB, red: lR, green: lG, blue: lB),
                Color(.sRGB, red: rR, green: rG, blue: rB)
            )
        }
    }

    /// Samples a 64×64 sRGB thumbnail off the main thread and extracts the dominant cover palette.
    @concurrent nonisolated static func load(_ path: String?) async -> CoverHue? {
        guard let path, !path.isEmpty else { return .paper }
        if let hit = cached(path) { return hit }
        guard let image = await Thumbnail.load(path, maxPixels: 64) else { return nil }

        let n = 64
        var buf = [UInt8](repeating: 0, count: n * n * 4)
        let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB()
        guard let context = CGContext(
            data: &buf,
            width: n,
            height: n,
            bitsPerComponent: 8,
            bytesPerRow: n * 4,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        context.interpolationQuality = .medium
        context.draw(image, in: CGRect(x: 0, y: 0, width: n, height: n))

        struct Bin { var area = 0.0; var r = 0.0; var g = 0.0; var b = 0.0 }
        var bins = Array(repeating: Bin(), count: 24)
        var darkArea = 0.0, midArea = 0.0, lightArea = 0.0, total = 0.0
        var edgeDark = 0.0, edgeTotal = 0.0
        var lightRGB = (0.0, 0.0, 0.0)
        var midRGB = (0.0, 0.0, 0.0)

        let margin = 4
        for y in margin ..< (n - margin) {
            for x in margin ..< (n - margin) {
                let i = (y * n + x) * 4
                guard buf[i + 3] >= 128 else { continue }
                let r = Double(buf[i]) / 255.0
                let g = Double(buf[i + 1]) / 255.0
                let b = Double(buf[i + 2]) / 255.0
                let (h, s, v) = rgbToHSV(r: r, g: g, b: b)

                let isEdge = (x < margin + 6 || x >= n - margin - 6 || y < margin + 6 || y >= n - margin - 6)
                let w = isEdge ? 1.35 : 1.0
                total += w
                if isEdge {
                    edgeTotal += 1
                    if v < 0.24 { edgeDark += 1 }
                }

                if s >= 0.15 && v >= 0.15 && !(v > 0.95 && s < 0.20) {
                    let idx = min(23, Int(h * 24.0))
                    bins[idx].area += w
                    bins[idx].r += r * w
                    bins[idx].g += g * w
                    bins[idx].b += b * w
                } else if v < 0.22 {
                    darkArea += w
                } else if v > 0.76 {
                    lightArea += w
                    lightRGB.0 += r * w
                    lightRGB.1 += g * w
                    lightRGB.2 += b * w
                } else {
                    midArea += w
                    midRGB.0 += r * w
                    midRGB.1 += g * w
                    midRGB.2 += b * w
                }
            }
        }

        guard total > 0 else { return .paper }

        var bestIdx = 0
        var bestChromaArea = 0.0
        for i in 0 ..< 24 {
            let p = (i + 23) % 24
            let nx = (i + 1) % 24
            let a = bins[i].area + bins[p].area + bins[nx].area
            if a > bestChromaArea {
                bestChromaArea = a
                bestIdx = i
            }
        }

        let chromaFrac = bestChromaArea / total
        let darkFrac = darkArea / total
        let edgeDarkFrac = edgeDark / max(1.0, edgeTotal)

        let extracted: CoverHue
        if (darkFrac >= 0.44 && darkFrac > chromaFrac * 1.02) || (darkFrac >= 0.36 && edgeDarkFrac >= 0.56) {
            extracted = CoverHue(kind: .darkNeutral)
        } else if chromaFrac >= 0.16 {
            let p = (bestIdx + 23) % 24
            let nx = (bestIdx + 1) % 24
            let aSum = max(1e-6, bins[bestIdx].area + bins[p].area + bins[nx].area)
            let cr = (bins[bestIdx].r + bins[p].r + bins[nx].r) / aSum
            let cg = (bins[bestIdx].g + bins[p].g + bins[nx].g) / aSum
            let cb = (bins[bestIdx].b + bins[p].b + bins[nx].b) / aSum
            let (h, s, v) = rgbToHSV(r: cr, g: cg, b: cb)
            extracted = CoverHue(kind: .chromatic(hue: h, saturation: s, brightness: v))
        } else {
            let base: (Double, Double, Double) = if lightArea >= midArea && lightArea > 0 {
                (lightRGB.0 / lightArea, lightRGB.1 / lightArea, lightRGB.2 / lightArea)
            } else if midArea > 0 {
                (midRGB.0 / midArea, midRGB.1 / midArea, midRGB.2 / midArea)
            } else {
                (0.90, 0.88, 0.84)
            }
            let (h, s, _) = rgbToHSV(r: base.0, g: base.1, b: base.2)
            extracted = CoverHue(kind: .lightPaper(hue: h, saturation: s))
        }

        cache.withLock { store in
            if store.count > 256 { store.removeAll(keepingCapacity: true) }
            store[path] = extracted
        }
        return extracted
    }

    private static func rgbToHSV(r: Double, g: Double, b: Double) -> (Double, Double, Double) {
        let mx = max(r, g, b)
        let mn = min(r, g, b)
        let d = mx - mn
        var h = 0.0
        if d > 1e-5 {
            if mx == r {
                h = ((g - b) / d).truncatingRemainder(dividingBy: 6.0)
            } else if mx == g {
                h = (b - r) / d + 2.0
            } else {
                h = (r - g) / d + 4.0
            }
            h /= 6.0
            if h < 0 { h += 1.0 }
        }
        let s = mx > 1e-4 ? d / mx : 0.0
        return (h, s, mx)
    }

    private static func hsvToRGB(h: Double, s: Double, v: Double) -> (Double, Double, Double) {
        let wrapped = (h.truncatingRemainder(dividingBy: 1.0) + 1.0).truncatingRemainder(dividingBy: 1.0) * 6.0
        let i = Int(floor(wrapped))
        let f = wrapped - Double(i)
        let p = v * (1.0 - s)
        let q = v * (1.0 - s * f)
        let t = v * (1.0 - s * (1.0 - f))
        switch i % 6 {
        case 0: return (v, t, p)
        case 1: return (q, v, p)
        case 2: return (p, v, t)
        case 3: return (p, q, v)
        case 4: return (t, p, v)
        default: return (v, p, q)
        }
    }

    private static func relativeLuminance(r: Double, g: Double, b: Double) -> Double {
        func linear(_ c: Double) -> Double {
            c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4)
        }
        return 0.2126 * linear(r) + 0.7152 * linear(g) + 0.0722 * linear(b)
    }
}
#endif

extension Notification.Name {
    /// 显示或收起侧栏 from the menu bar (⌘⌥N) while the shelf window is in front.
    static let toggleShelfSidebar = Notification.Name("yuyue.toggleShelfSidebar")
}

/// 显示侧栏 / 隐藏侧栏: a small plain icon, the page's grey, a pad under it on hover and while the
/// sidebar is away, as the window's buttons are small and quiet beside it.
private struct SidebarToggle: View {
    let shown: Bool
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: "sidebar.left")
                .font(.yy(size: 12.5, weight: .regular))
                .foregroundStyle(hover ? YYColor.t1 : YYColor.t2)
                .frame(width: 28, height: 22)
                .background(hover ? YYColor.inset : .clear, in: .rect(cornerRadius: 6))
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(shown ? String(localized: "隐藏侧栏 ⌘⌥N") : String(localized: "显示侧栏 ⌘⌥N"))
        .accessibilityLabel(shown ? String(localized: "隐藏侧栏") : String(localized: "显示侧栏"))
    }
}

/// Our own icons where no system one matches, drawn as SVGs (16 × 16, lines).
nonisolated enum ProtoIcon: String {
    /// `#i-wavef`: 有声.
    case wave = "proto.wave"
    /// 书: a closed book, a bookmark ribbon hanging from its top (待定方案 7 · A).
    case book = "proto.book"

    var shape: some Shape { ProtoShape(icon: self) }

    /// The line at the sidebar's 14 points, matched by eye to the system icons beside it.
    var line: CGFloat {
        switch self {
        case .wave: 1.6
        case .book: 1.3
        }
    }
}

nonisolated struct ProtoShape: Shape {
    let icon: ProtoIcon

    func path(in rect: CGRect) -> Path {
        var p = Path()
        switch icon {
        case .wave:
            // M2 8h.5 M4.5 5.5v5 M7 3v10 M9.5 5v6 M12 6.5v3 M14 8h.01
            p.move(to: CGPoint(x: 2, y: 8)); p.addLine(to: CGPoint(x: 2.5, y: 8))
            for (x, y0, y1) in [(4.5, 5.5, 10.5), (7.0, 3.0, 13.0), (9.5, 5.0, 11.0), (12.0, 6.5, 9.5)] {
                p.move(to: CGPoint(x: x, y: y0)); p.addLine(to: CGPoint(x: x, y: y1))
            }
            p.move(to: CGPoint(x: 14, y: 8)); p.addLine(to: CGPoint(x: 14.01, y: 8))
        case .book:
            // In a 24 box: M6 3.5h11a1 1 0 0 1 1 1v15a1 1 0 0 1-1 1H6.5A1.5 1.5 0 0 1 5 19V5
            // a1.5 1.5 0 0 1 1-1.5z M5 17.5h13 M14 3.5v7l-1.75-1.4L10.5 10.5v-7
            var b = Path()
            b.move(to: CGPoint(x: 6.5, y: 3.5))
            b.addLine(to: CGPoint(x: 17, y: 3.5))
            b.addQuadCurve(to: CGPoint(x: 18, y: 4.5), control: CGPoint(x: 18, y: 3.5))
            b.addLine(to: CGPoint(x: 18, y: 19.5))
            b.addQuadCurve(to: CGPoint(x: 17, y: 20.5), control: CGPoint(x: 18, y: 20.5))
            b.addLine(to: CGPoint(x: 6.5, y: 20.5))
            b.addQuadCurve(to: CGPoint(x: 5, y: 19), control: CGPoint(x: 5, y: 20.5))
            b.addLine(to: CGPoint(x: 5, y: 5))
            b.addQuadCurve(to: CGPoint(x: 6.5, y: 3.5), control: CGPoint(x: 5, y: 3.5))
            b.closeSubpath()
            b.move(to: CGPoint(x: 5, y: 17.5)); b.addLine(to: CGPoint(x: 18, y: 17.5))
            b.move(to: CGPoint(x: 14, y: 3.5)); b.addLine(to: CGPoint(x: 14, y: 10.5))
            b.addLine(to: CGPoint(x: 12.25, y: 9.1)); b.addLine(to: CGPoint(x: 10.5, y: 10.5))
            b.addLine(to: CGPoint(x: 10.5, y: 3.5))
            // Its 13 × 17 body centred and filling the 16 box, as tall as the icons beside it.
            p.addPath(b.applying(CGAffineTransform(translationX: 8, y: 8).scaledBy(x: 16 / 18.5, y: 16 / 18.5).translatedBy(x: -11.5, y: -12)))
        }
        return p.applying(CGAffineTransform(translationX: rect.minX, y: rect.minY).scaledBy(x: rect.width / 16, y: rect.height / 16))
    }
}
