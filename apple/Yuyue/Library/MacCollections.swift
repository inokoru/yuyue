import SwiftUI
import YuyueCore
#if os(macOS)
import UniformTypeIdentifiers

/// 合集 kept in the shelf's sidebar (合集墙 › 固定到侧栏), in their order. The rest are on the
/// 合集墙 only. Kept on this Mac: the core's 合集 have no field for it, so it doesn't sync.
enum PinnedCollections {
    static let key = "shelf.pinnedCollections"

    static func ids(_ raw: String) -> [String] {
        raw.split(separator: "\n").map(String.init)
    }

    static func raw(_ ids: [String]) -> String {
        ids.joined(separator: "\n")
    }
}

/// 全部合集 (合集墙): a bookcase wall of recessed slipcase cards — real miniature book covers
/// tucked into a warm cloth pocket when populated, or a quiet letterpress Ex-Libris bookplate
/// when empty. Supports filtering, sorting, pinning to sidebar, drag-and-drop books/files, and management.
struct CollectionWall: View {
    @State private var confirming: YYConfirm?
    @Bindable var library: Library
    @Binding var pinnedRaw: String
    /// Room for the window's buttons when the sidebar is away.
    var leading: CGFloat = 0
    let open: (String) -> Void
    @AppStorage("collections.sort") private var sort = Sort.recent
    @State private var scope = Scope.all
    @State private var query = ""
    @State private var hovering: String?
    @State private var dropTarget: String?
    @State private var naming: Naming?
    @State private var name = ""
    @Namespace private var scopeNamespace
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @AppStorage(EInk.key) private var eink = false

    enum Scope: Hashable {
        case all, pinned
    }

    enum Sort: String, CaseIterable {
        case recent, name, count, own

        var title: String {
            switch self {
            case .recent: String(localized: "最近用过")
            case .name: String(localized: "名字")
            case .count: String(localized: "书的多少")
            case .own: String(localized: "自己排的")
            }
        }
    }

    /// 新建合集, or 重命名 of one.
    private struct Naming: Identifiable {
        var id: String { collection ?? "" }
        let collection: String?
    }

    private var pinned: [String] { PinnedCollections.ids(pinnedRaw) }

    private var pinnedCount: Int {
        let set = Set(pinned)
        return library.collections.count { set.contains($0.id) }
    }

    private var shown: [BookCollection] {
        let pinSet = Set(pinned)
        let matcher = NameMatcher(query)
        var list = library.collections
        if scope == .pinned {
            list = list.filter { pinSet.contains($0.id) }
        }
        if !matcher.isEmpty {
            list = list.filter { matcher.match($0.name) != nil }
        }
        switch sort {
        case .recent: return list.sorted { ($0.usedAt, $0.name) > ($1.usedAt, $1.name) }
        case .name: return list.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        case .count: return list.sorted { ($0.count, $0.usedAt) > ($1.count, $1.usedAt) }
        case .own: return list
        }
    }

    /// Pre-grouped books per collection so rendering cards stays O(N + B).
    private var booksByCollection: [String: [BookSummary]] {
        var map: [String: [BookSummary]] = [:]
        let ordered = library.keptBooks.sorted { a, b in
            let aCover = (a.coverPath?.isEmpty == false) ? 1 : 0
            let bCover = (b.coverPath?.isEmpty == false) ? 1 : 0
            if aCover != bCover { return aCover > bCover }
            let aTouch = max(a.openedAt ?? 0, a.addedAt)
            let bTouch = max(b.openedAt ?? 0, b.addedAt)
            return aTouch > bTouch
        }
        for book in ordered {
            for colID in book.collections {
                if map[colID, default: []].count < 3 {
                    map[colID, default: []].append(book)
                }
            }
        }
        return map
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // 64 high, 4 of it above, the title in the middle — level with 书 · 笔记 · 生词 · 进度.
            header
                .frame(height: 60)
                .padding(.top, 4)
                .padding(.leading, 22 + leading)
                .padding(.trailing, 28)
            if library.collections.isEmpty {
                emptyWall
            } else {
                toolbarRow
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                    .padding(.bottom, 12)
                ScrollView {
                    if shown.isEmpty {
                        filteredEmpty
                    } else {
                        let grouped = booksByCollection
                        LazyVGrid(
                            columns: [GridItem(.adaptive(minimum: 170, maximum: 216), spacing: 14, alignment: .top)],
                            alignment: .leading,
                            spacing: 14
                        ) {
                            ForEach(shown, id: \.id) { c in
                                card(c, books: grouped[c.id] ?? [])
                            }
                        }
                        .animation(YYMotion.layout, value: shown.map(\.id))
                        .padding(.leading, 22)
                        .padding(.trailing, 28)
                        .padding(.top, 4)
                        .padding(.bottom, 44)
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(.container, edges: .top)
        .background(YYColor.bg)
        .onChange(of: pinnedCount) { _, n in
            if n == 0, scope == .pinned { scope = .all }
        }
        .yyPrompt(naming?.collection == nil ? String(localized: "新建合集") : String(localized: "重命名合集"),
                  message: naming?.collection == nil ? String(localized: "起个名字，之后可以把书拖进去。") : "",
                  text: $name, prompt: String(localized: "合集的名字"),
                  isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } }),
                  confirm: naming?.collection == nil ? String(localized: "建好了") : String(localized: "保存"),
                  onConfirm: finishNaming)
        .yyConfirm($confirming)
    }

    // MARK: Chrome

    private var header: some View {
        HStack(spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text("全部合集")
                    .font(.yy(size: 22, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                if !library.collections.isEmpty {
                    let totalBooks = library.keptBooks.count { !$0.collections.isEmpty }
                    Text(totalBooks > 0
                         ? String(localized: "\(library.collections.count) 个 · 收纳 \(totalBooks) 本")
                         : String(localized: "\(library.collections.count) 个"))
                        .font(.yy(size: 12).monospacedDigit())
                        .foregroundStyle(YYColor.t2)
                }
            }
            Spacer(minLength: 12)
            if library.collections.count >= 5 {
                filterField
            }
            Button {
                name = ""
                naming = Naming(collection: nil)
            } label: {
                HStack(spacing: 5) {
                    Image(systemName: "plus").font(.yy(size: 11, weight: .semibold))
                    Text("新建合集")
                }
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 13)
                .frame(height: 28)
            }
            .buttonStyle(YYPillChrome())
        }
    }

    private var filterField: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 11, weight: .medium))
                .foregroundStyle(YYColor.t3)
            TextField("", text: $query, prompt: Text("找合集").foregroundStyle(YYColor.t3))
                .textFieldStyle(.plain)
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t1)
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("清空"))
            }
        }
        .padding(.horizontal, 10)
        .frame(width: 156, height: 28)
        .background(YYColor.inset.opacity(scheme == .dark ? 0.85 : 0.72), in: .capsule)
        .overlay { Capsule().strokeBorder(YYColor.line.opacity(0.7), lineWidth: 0.5) }
    }

    private var toolbarRow: some View {
        HStack(spacing: 2) {
            scopeTab(.all, title: String(localized: "全部"), count: library.collections.count)
            if pinnedCount > 0 {
                scopeTab(.pinned, title: String(localized: "固定在侧栏"), count: pinnedCount)
            }
            Spacer(minLength: 8)
            sortMenu
        }
    }

    private func scopeTab(_ target: Scope, title: String, count: Int) -> some View {
        let on = scope == target
        return Button {
            withAnimation(YYMotion.standard) { scope = target }
        } label: {
            HStack(spacing: 6) {
                Text(title).font(.yy(size: 12.5, weight: on ? .semibold : .regular))
                Text("\(count)").font(.yy(size: 10.5).monospacedDigit()).foregroundStyle(YYColor.t3)
            }
            .foregroundStyle(YYColor.t1)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background {
                if on {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(RecordInk.chosen)
                        .matchedGeometryEffect(id: "collectionScope", in: scopeNamespace)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private var sortMenu: some View {
        Menu {
            Picker("排序", selection: $sort) {
                ForEach(Sort.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            .pickerStyle(.inline)
            .labelsHidden()
        } label: {
            HStack(spacing: 4) {
                Text(sort.title)
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

    // MARK: Empty States

    private var emptyWall: some View {
        let tone = CollectionClothTone.sand(scheme: scheme, eink: eink)
        let shape = RoundedRectangle(cornerRadius: 10, style: .continuous)
        return VStack(spacing: 14) {
            ExLibrisWellPreview(title: "藏", explicitSymbol: "books.vertical", tone: tone, scheme: scheme, eink: eink)
                .frame(width: 176, height: 92)
                .clipShape(shape)
                .overlay { shape.strokeBorder(tone.wellBorder, lineWidth: 0.5) }
            VStack(spacing: 6) {
                Text("还没有合集")
                    .font(PodcastStyle.songti(17))
                    .foregroundStyle(YYColor.t1)
                Text("按主题、心境或阅读计划建几个书匣，把书放进去慢慢读。")
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .multilineTextAlignment(.center)
                    .lineSpacing(3)
            }
        }
        .padding(.horizontal, 28)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.bottom, 48)
    }

    private var filteredEmpty: some View {
        VStack(spacing: 8) {
            Text(query.isEmpty ? String(localized: "还没有固定到侧栏的合集") : String(localized: "没有找到“\(query)”相关的合集"))
                .font(.yy(size: 12.5))
                .foregroundStyle(YYColor.t2)
            if !query.isEmpty {
                Button {
                    let seed = query.trimmingCharacters(in: .whitespaces)
                    query = ""
                    if !seed.isEmpty {
                        _ = library.createCollection(seed)
                    }
                } label: {
                    Text("新建合集“\(query.trimmingCharacters(in: .whitespaces))”")
                        .font(.yy(size: 12, weight: .medium))
                        .foregroundStyle(tint.accent)
                }
                .buttonStyle(.plain)
            }
        }
        .frame(maxWidth: .infinity)
        .padding(.top, 72)
    }

    // MARK: Bookcase Card

    private func card(_ c: BookCollection, books: [BookSummary]) -> some View {
        let isPinned = pinned.contains(c.id)
        let hover = hovering == c.id
        let target = dropTarget == c.id
        let explicitSymbol = c.icon ?? CollectionIcon.guess(for: c.name)
        let tone = CollectionClothTone.resolve(for: c, explicitSymbol: explicitSymbol, scheme: scheme, eink: eink)
        let matcher = NameMatcher(query)
        let shape = RoundedRectangle(cornerRadius: 14, style: .continuous)
        let wellShape = RoundedRectangle(cornerRadius: 9.5, style: .continuous)

        return Button { open(c.id) } label: {
            VStack(alignment: .leading, spacing: 0) {
                // Top recessed bookcloth pocket
                ZStack(alignment: .top) {
                    LinearGradient(colors: [tone.wellTop, tone.wellBottom], startPoint: .top, endPoint: .bottom)

                    // Books or Ex-Libris folios tucked into the bottom of the well
                    Group {
                        if books.isEmpty {
                            ExLibrisWellPreview(
                                title: c.name,
                                explicitSymbol: explicitSymbol,
                                tone: tone,
                                scheme: scheme,
                                eink: eink,
                                hover: hover
                            )
                        } else {
                            CollectionCoverStage(books: books, tone: tone, eink: eink, hover: hover)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)

                    // Subtle bottom pocket lip shadow
                    VStack {
                        Spacer(minLength: 0)
                        LinearGradient(
                            colors: [.clear, .black.opacity(eink ? 0 : (scheme == .dark ? 0.22 : 0.04))],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                        .frame(height: 12)
                    }
                    .allowsHitTesting(false)

                    // Top badges inside the well
                    HStack(alignment: .top, spacing: 6) {
                        if !books.isEmpty, let explicitSymbol {
                            Image(systemName: explicitSymbol)
                                .font(.system(size: 9.5, weight: .medium))
                                .foregroundStyle(tone.ink.opacity(0.82))
                                .frame(width: 20, height: 20)
                                .background(YYColor.card.opacity(scheme == .dark ? 0.68 : 0.78), in: .circle)
                                .overlay { Circle().strokeBorder(tone.wellBorder, lineWidth: 0.5) }
                                .accessibilityHidden(true)
                        }
                        Spacer(minLength: 0)
                        wellControls(c, isPinned: isPinned, hover: hover, tone: tone)
                    }
                    .padding(7)
                }
                .frame(height: 92)
                .clipShape(wellShape)
                .overlay { wellShape.strokeBorder(target ? tint.edge : tone.wellBorder, lineWidth: 0.5) }
                .padding(.top, 5)
                .padding(.horizontal, 5)

                // Porcelain bookplate footer
                HStack(alignment: .center, spacing: 8) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text(matcher.isEmpty ? AttributedString(c.name) : matcher.highlighted(c.name, color: tint.accent))
                            .font(.yy(size: 13, weight: .semibold))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)
                        Text(subtitle(for: c))
                            .font(.yy(size: 10.5).monospacedDigit())
                            .foregroundStyle(c.count == 0 ? YYColor.t3.opacity(0.75) : YYColor.t3)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 4)
                    Image(systemName: "chevron.right")
                        .font(.yy(size: 9, weight: .semibold))
                        .foregroundStyle(YYColor.t3.opacity(hover ? 0.8 : 0))
                        .offset(x: hover && !YYMotion.reduce ? 1 : -2)
                }
                .padding(.horizontal, 11)
                .frame(height: 46)
            }
            .background(target ? tint.accentSoft : YYColor.card, in: shape)
            .overlay {
                shape.strokeBorder(
                    target ? tint.accent : (scheme == .dark ? Color.white.opacity(0.07) : YYColor.line),
                    lineWidth: target ? 1.2 : 0.5
                )
            }
            .shadow(
                color: Color(yy: YYRGBA(51, 46, 41, scheme == .dark || eink ? 0 : (hover ? 0.09 : 0.035))),
                radius: hover ? 14 : 3,
                y: hover ? 6 : 1.5
            )
            .offset(y: hover && !YYMotion.reduce && !eink ? -2 : 0)
            .contentShape(shape)
            .animation(YYMotion.quick, value: hover)
            .animation(YYMotion.quick, value: target)
        }
        .buttonStyle(YYPressStyle(scale: 0.985))
        .onHover { hovering = $0 ? c.id : (hovering == c.id ? nil : hovering) }
        .onDrop(of: [.fileURL, .utf8PlainText, .plainText], delegate: ShelfDrop(hovering: Binding {
            dropTarget == c.id ? 0 : nil
        } set: { on in
            dropTarget = on != nil ? c.id : (dropTarget == c.id ? nil : dropTarget)
        }, acceptsBooks: true) { urls in
            Task { await library.importFiles(urls, into: c.id) }
        } books: { ids in
            for id in ids where !id.hasPrefix(PinnedCollections.dragPrefix) {
                if library.books.contains(where: { $0.id == id }) {
                    library.setBook(id, in: c.id, true)
                }
            }
        })
        .contextMenu {
            collectionMenuItems(c, isPinned: isPinned)
        }
        .accessibilityLabel("\(c.name)，\(Int(c.count)) 本")
        .accessibilityAction(named: isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏")) { togglePin(c.id) }
    }

    private func subtitle(for c: BookCollection) -> String {
        let n = Int(c.count)
        guard n > 0 else { return String(localized: "还没有书") }
        if c.usedAt > 0 {
            let when = ShortAgo.short(Date(timeIntervalSince1970: Double(c.usedAt) / 1000))
            return String(localized: "\(n) 本") + " · " + when
        }
        return String(localized: "\(n) 本")
    }

    @ViewBuilder
    private func wellControls(_ c: BookCollection, isPinned: Bool, hover: Bool, tone: CollectionClothTone) -> some View {
        if hover {
            HStack(spacing: 4) {
                Button { togglePin(c.id) } label: {
                    Image(systemName: isPinned ? "pin.slash" : "pin")
                        .font(.yy(size: 9.5, weight: .medium))
                        .foregroundStyle(isPinned ? tint.accent : YYColor.t2)
                        .frame(width: 22, height: 22)
                        .background(YYColor.card.opacity(scheme == .dark ? 0.88 : 0.94), in: .circle)
                        .overlay { Circle().strokeBorder(YYColor.line, lineWidth: 0.5) }
                        .contentShape(.circle)
                }
                .buttonStyle(.plain)
                .help(isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏"))
                .accessibilityLabel(isPinned ? Text("取消固定") : Text("固定到侧栏"))

                Menu {
                    collectionMenuItems(c, isPinned: isPinned)
                } label: {
                    Image(systemName: "ellipsis")
                        .font(.yy(size: 10, weight: .semibold))
                        .foregroundStyle(YYColor.t2)
                        .frame(width: 22, height: 22)
                        .background(YYColor.card.opacity(scheme == .dark ? 0.88 : 0.94), in: .circle)
                        .overlay { Circle().strokeBorder(YYColor.line, lineWidth: 0.5) }
                        .contentShape(.circle)
                }
                .menuStyle(.button)
                .buttonStyle(.plain)
                .menuIndicator(.hidden)
                .fixedSize()
                .accessibilityLabel(String(localized: "管理合集“\(c.name)”"))
            }
            .transition(.opacity)
        } else if isPinned {
            Image(systemName: "pin.fill")
                .font(.yy(size: 9, weight: .semibold))
                .foregroundStyle(tint.accent)
                .frame(width: 20, height: 20)
                .background(YYColor.card.opacity(scheme == .dark ? 0.82 : 0.9), in: .circle)
                .overlay { Circle().strokeBorder(tone.wellBorder, lineWidth: 0.5) }
                .accessibilityHidden(true)
        }
    }

    @ViewBuilder
    private func collectionMenuItems(_ c: BookCollection, isPinned: Bool) -> some View {
        Button(isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏")) { togglePin(c.id) }
        Divider()
        Button("重命名") {
            name = c.name
            naming = Naming(collection: c.id)
        }
        CollectionIconMenu(collection: c, library: library)
        if sort == .own {
            Button("往前挪") { move(c.id, by: -1) }
            Button("往后挪") { move(c.id, by: 1) }
        }
        Divider()
        Button("删除合集…", role: .destructive) {
            confirming = YYConfirm(
                title: String(localized: "删除合集“\(c.name)”？"),
                message: String(localized: "这个合集删掉，里面的书还在书架上。"),
                button: String(localized: "删除")
            ) {
                pinnedRaw = PinnedCollections.raw(pinned.filter { $0 != c.id })
                library.deleteCollection(c)
            }
        }
    }

    private func togglePin(_ id: String) {
        var ids = pinned
        if let i = ids.firstIndex(of: id) { ids.remove(at: i) } else { ids.append(id) }
        pinnedRaw = PinnedCollections.raw(ids)
    }

    /// 自己排的: one place earlier or later.
    private func move(_ id: String, by step: Int) {
        var ids = library.collections.map(\.id)
        guard let i = ids.firstIndex(of: id), ids.indices.contains(i + step) else { return }
        ids.swapAt(i, i + step)
        library.reorderCollections(ids)
    }

    private func finishNaming() {
        let text = name.trimmingCharacters(in: .whitespaces)
        defer { naming = nil }
        guard !text.isEmpty else { return }
        if let id = naming?.collection {
            library.renameCollection(id, to: text)
        } else {
            _ = library.createCollection(text)
        }
    }
}

// MARK: - Bookcloth Palette & Cover Stage

/// Warm mineral & dyed-paper bookcloth tones matching Yuyue's宣纸 palette — earthy, low-saturation
/// linen washes in light mode, warm charcoal weaves in dark mode, neutral stone in E-Ink mode.
private struct CollectionClothTone {
    let wellTop: Color
    let wellBottom: Color
    let wellBorder: Color
    let ink: Color
    let folioFront: Color
    let folioBack: Color
    let folioBorder: Color
    let folioLine: Color

    static func sand(scheme: ColorScheme, eink: Bool) -> CollectionClothTone {
        make(
            lightTop: YYRGBA(243, 239, 231), lightBottom: YYRGBA(237, 231, 220),
            darkTop: YYRGBA(37, 34, 30), darkBottom: YYRGBA(32, 29, 26),
            lightInk: YYRGBA(116, 92, 66), darkInk: YYRGBA(198, 174, 146),
            scheme: scheme, eink: eink
        )
    }

    static func resolve(for c: BookCollection, explicitSymbol: String?, scheme: ColorScheme, eink: Bool) -> CollectionClothTone {
        if eink {
            return make(
                lightTop: YYRGBA(238, 237, 233), lightBottom: YYRGBA(232, 231, 226),
                darkTop: YYRGBA(34, 34, 34), darkBottom: YYRGBA(30, 30, 30),
                lightInk: YYRGBA(60, 60, 60), darkInk: YYRGBA(200, 200, 200),
                scheme: scheme, eink: true
            )
        }
        let kind: Int = if let s = explicitSymbol {
            switch s {
            case "globe", "eye": 1                             // 芦苇青 Reed Celadon
            case "sparkle", "clock": 2                         // 苍烟灰 Warm Mist Slate
            case "moon", "key": 3                              // 松烟黛 Warm Dusk Slate
            case "heart", "quote.opening", "sun.max": 4        // 赭茶红 Warm Clay Blush
            case "headphones", "cup.and.saucer": 0             // 暖宣沙 Warm Linen Sand
            default: 5                                         // 素石白 Tea Stone
            }
        } else {
            stableBucket(c.id, modulo: 6)
        }
        return switch kind {
        case 1: // 芦苇青 Reed Celadon (warm olive-sage, harmonized with YYPaper.sage)
            make(
                lightTop: YYRGBA(237, 239, 231), lightBottom: YYRGBA(230, 233, 222),
                darkTop: YYRGBA(32, 36, 32), darkBottom: YYRGBA(28, 32, 28),
                lightInk: YYRGBA(78, 98, 76), darkInk: YYRGBA(162, 184, 158),
                scheme: scheme, eink: false
            )
        case 2: // 苍烟灰 Warm Mist Slate (paper-warmed slate, never icy blue)
            make(
                lightTop: YYRGBA(237, 239, 241), lightBottom: YYRGBA(229, 232, 236),
                darkTop: YYRGBA(32, 35, 39), darkBottom: YYRGBA(28, 31, 35),
                lightInk: YYRGBA(74, 92, 110), darkInk: YYRGBA(158, 178, 198),
                scheme: scheme, eink: false
            )
        case 3: // 松烟黛 Warm Dusk Ink (muted warm charcoal-indigo, never candy lavender)
            make(
                lightTop: YYRGBA(239, 237, 239), lightBottom: YYRGBA(232, 229, 233),
                darkTop: YYRGBA(35, 34, 39), darkBottom: YYRGBA(30, 29, 34),
                lightInk: YYRGBA(90, 84, 106), darkInk: YYRGBA(174, 168, 192),
                scheme: scheme, eink: false
            )
        case 4: // 赭茶红 Warm Clay Terracotta (earthy warm rose-clay)
            make(
                lightTop: YYRGBA(243, 237, 234), lightBottom: YYRGBA(237, 229, 225),
                darkTop: YYRGBA(39, 33, 33), darkBottom: YYRGBA(34, 29, 29),
                lightInk: YYRGBA(130, 88, 84), darkInk: YYRGBA(202, 162, 158),
                scheme: scheme, eink: false
            )
        case 5: // 素石白 Tea Stone (warm travertine)
            make(
                lightTop: YYRGBA(241, 238, 232), lightBottom: YYRGBA(234, 230, 222),
                darkTop: YYRGBA(35, 34, 32), darkBottom: YYRGBA(31, 30, 28),
                lightInk: YYRGBA(96, 90, 80), darkInk: YYRGBA(182, 176, 166),
                scheme: scheme, eink: false
            )
        default:
            sand(scheme: scheme, eink: false)
        }
    }

    private static func stableBucket(_ s: String, modulo: Int) -> Int {
        var h: UInt64 = 5381
        for byte in s.utf8 {
            h = ((h &<< 5) &+ h) &+ UInt64(byte)
        }
        return Int(h % UInt64(modulo))
    }

    private static func make(
        lightTop: YYRGBA, lightBottom: YYRGBA,
        darkTop: YYRGBA, darkBottom: YYRGBA,
        lightInk: YYRGBA, darkInk: YYRGBA,
        scheme: ColorScheme, eink: Bool
    ) -> CollectionClothTone {
        let dark = scheme == .dark
        let ink = Color(yy: dark ? darkInk : lightInk)
        return CollectionClothTone(
            wellTop: Color(yy: dark ? darkTop : lightTop),
            wellBottom: Color(yy: dark ? darkBottom : lightBottom),
            wellBorder: dark ? Color.white.opacity(0.06) : Color(yy: lightInk).opacity(0.10),
            ink: ink,
            folioFront: dark ? Color(yy: YYRGBA(46, 45, 42)) : Color(yy: YYRGBA(253, 252, 249)),
            folioBack: dark ? Color.white.opacity(0.04) : Color.white.opacity(0.55),
            folioBorder: dark ? Color.white.opacity(0.10) : Color(yy: lightInk).opacity(0.15),
            folioLine: dark ? Color.white.opacity(0.07) : Color(yy: lightInk).opacity(0.10)
        )
    }
}

/// Real miniature 2:3 book covers standing inside a collection's pocket without clipping titles.
private struct CollectionCoverStage: View {
    let books: [BookSummary]
    let tone: CollectionClothTone
    let eink: Bool
    var hover = false

    var body: some View {
        let lift: CGFloat = hover && !YYMotion.reduce && !eink ? -2 : 0
        ZStack(alignment: .bottom) {
            if books.count >= 3 {
                HStack(alignment: .bottom, spacing: 6) {
                    BookCover(book: books[1])
                        .frame(width: 43)
                        .offset(y: 11)
                    BookCover(book: books[0])
                        .frame(width: 48)
                        .shadow(color: .black.opacity(eink ? 0 : 0.10), radius: 5, y: 2)
                        .offset(y: 6 + lift)
                    BookCover(book: books[2])
                        .frame(width: 43)
                        .offset(y: 11)
                }
            } else if books.count == 2 {
                HStack(alignment: .bottom, spacing: 7) {
                    BookCover(book: books[0])
                        .frame(width: 47)
                        .shadow(color: .black.opacity(eink ? 0 : 0.10), radius: 5, y: 2)
                        .offset(y: 6 + lift)
                    BookCover(book: books[1])
                        .frame(width: 45)
                        .offset(y: 10)
                }
            } else if let only = books.first {
                HStack(alignment: .bottom, spacing: 7) {
                    BookCover(book: only)
                        .frame(width: 48)
                        .shadow(color: .black.opacity(eink ? 0 : 0.10), radius: 5, y: 2)
                        .offset(y: 6 + lift)
                    RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                        .fill(tone.folioBack)
                        .overlay {
                            RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                                .strokeBorder(tone.folioLine, lineWidth: 0.5)
                        }
                        .frame(width: 43, height: 64.5)
                        .offset(y: 12)
                }
            }
        }
        .accessibilityHidden(true)
    }
}

/// Three serene stationery folios with an Ex-Libris emblem (thematic symbol or Songti monogram)
/// for empty collections, so even multiple unthemed empty collections feel distinct and literary.
private struct ExLibrisWellPreview: View {
    let title: String
    let explicitSymbol: String?
    let tone: CollectionClothTone
    let scheme: ColorScheme
    let eink: Bool
    var hover = false

    private enum Emblem {
        case symbol(String)
        case text(String, CGFloat)
    }

    private var emblem: Emblem {
        if let explicitSymbol { return .symbol(explicitSymbol) }
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return .symbol(CollectionIcon.fallback) }
        if trimmed.allSatisfy(\.isASCII) {
            if trimmed.allSatisfy(\.isNumber) {
                switch trimmed.count {
                case 1...2: return .text(trimmed, 13.5)
                case 3...4: return .text(trimmed, 10.5)
                default: return .symbol(CollectionIcon.fallback)
                }
            }
            if trimmed.count <= 3 {
                return .text(trimmed.uppercased(), trimmed.count == 1 ? 14 : 12)
            }
            if let first = trimmed.first(where: \.isLetter) {
                return .text(String(first).uppercased(), 14)
            }
            return .symbol(CollectionIcon.fallback)
        }
        return .text(String(trimmed.prefix(1)), 14.5)
    }

    var body: some View {
        let lift: CGFloat = hover && !YYMotion.reduce && !eink ? -1.5 : 0
        ZStack(alignment: .bottom) {
            LinearGradient(colors: [tone.wellTop, tone.wellBottom], startPoint: .top, endPoint: .bottom)

            folioBack
                .frame(width: 40, height: 60)
                .offset(x: -28, y: 13)
            folioBack
                .frame(width: 40, height: 60)
                .offset(x: 28, y: 13)

            // Front Ex-Libris bookplate
            ZStack {
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .fill(tone.folioFront)
                RoundedRectangle(cornerRadius: 4, style: .continuous)
                    .strokeBorder(tone.folioBorder, lineWidth: 0.5)
                RoundedRectangle(cornerRadius: 2, style: .continuous)
                    .strokeBorder(tone.ink.opacity(scheme == .dark ? 0.13 : 0.09), lineWidth: 0.5)
                    .padding(4)

                VStack(spacing: 5) {
                    switch emblem {
                    case .symbol(let name):
                        Image(systemName: name)
                            .font(.system(size: 13.5, weight: .light))
                            .foregroundStyle(tone.ink.opacity(0.76))
                            .frame(height: 16)
                    case .text(let label, let pt):
                        Text(label)
                            .font(YYFontFamily.serif.font(size: pt, weight: .medium))
                            .foregroundStyle(tone.ink.opacity(0.78))
                            .frame(height: 16)
                    }
                    VStack(spacing: 2) {
                        Capsule()
                            .fill(tone.ink.opacity(0.22))
                            .frame(width: 15, height: 0.8)
                        Capsule()
                            .fill(tone.ink.opacity(0.14))
                            .frame(width: 9, height: 0.8)
                    }
                }
                .offset(y: -3.5)
            }
            .frame(width: 48, height: 71)
            .shadow(color: .black.opacity(eink || scheme == .dark ? 0 : 0.045), radius: 4, y: 1.5)
            .offset(y: 7 + lift)
        }
        .accessibilityHidden(true)
    }

    private var folioBack: some View {
        RoundedRectangle(cornerRadius: 3.5, style: .continuous)
            .fill(tone.folioBack)
            .overlay {
                RoundedRectangle(cornerRadius: 3.5, style: .continuous)
                    .strokeBorder(tone.folioLine, lineWidth: 0.5)
            }
    }
}

/// 最近用过: 今天, 昨天, 3 天前, 10 月 2 日.
enum ShortAgo {
    static func short(_ date: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) { return String(localized: "今天") }
        if cal.isDateInYesterday(date) { return String(localized: "昨天") }
        let days = cal.dateComponents([.day], from: cal.startOfDay(for: date), to: cal.startOfDay(for: .now)).day ?? 0
        if days < 7 { return String(localized: "\(days) 天前") }
        return date.formatted(.dateTime.month().day())
    }
}

/// The ids of books dragged from the shelf (each a cover's `draggable(book.id)`).
enum BookDrag {
    static func ids(_ providers: [NSItemProvider]) async -> [String] {
        var out: [String] = []
        for p in providers where p.canLoadObject(ofClass: String.self) {
            if let s = await load(p) { out.append(s) }
        }
        return out
    }

    private static func load(_ provider: NSItemProvider) async -> String? {
        await withCheckedContinuation { done in
            _ = provider.loadObject(ofClass: String.self) { s, _ in done.resume(returning: s) }
        }
    }
}

/// A book held over the sidebar's “合集” for a moment: every 合集, to drop it on one. Typing
/// finds one; the last row makes a new one with the book in it.
struct CollectionDropList: View {
    @Bindable var library: Library
    let done: () -> Void
    @State private var query = ""
    @State private var target: String?
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var shown: [BookCollection] {
        let all = library.collections.sorted { $0.usedAt > $1.usedAt }
        let q = query.trimmingCharacters(in: .whitespaces)
        return q.isEmpty ? all : all.filter { $0.name.localizedCaseInsensitiveContains(q) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            TextField("", text: $query, prompt: Text("放进哪个合集… 打字找"))
                .textFieldStyle(.plain)
                .font(.yy(size: 11.5))
                .padding(.horizontal, 9)
                .frame(height: 28)
                .background(YYColor.inset, in: .rect(cornerRadius: 8))
                .padding(.bottom, 2)
            ScrollView {
                VStack(spacing: 2) {
                    ForEach(shown, id: \.id) { c in
                        row(c.name, count: Int(c.count), id: c.id) { ids in
                            for id in ids { library.setBook(id, in: c.id, true) }
                        }
                    }
                }
            }
            .frame(maxHeight: 260)
            .fixedSize(horizontal: false, vertical: true)
            row(query.trimmingCharacters(in: .whitespaces).isEmpty ? String(localized: "＋ 新建合集并放进去") : String(localized: "＋ 新建合集“\(query.trimmingCharacters(in: .whitespaces))”并放进去"),
                count: nil, id: "+new", accent: true) { ids in
                let name = query.trimmingCharacters(in: .whitespaces)
                guard let first = ids.first, let id = library.createCollection(name.isEmpty ? String(localized: "新合集") : name, with: first) else { return }
                for book in ids.dropFirst() { library.setBook(book, in: id, true) }
            }
        }
        .padding(8)
        .frame(width: 230)
    }

    private func row(_ title: String, count: Int?, id: String, accent: Bool = false, put: @escaping @MainActor ([String]) -> Void) -> some View {
        let hot = target == id
        return HStack {
            Text(title).lineLimit(1)
            Spacer(minLength: 4)
            if let count { Text("\(count)").font(.yy(size: 10.5)).foregroundStyle(hot ? .white.opacity(0.8) : YYColor.t3) }
        }
        .font(.yy(size: 12))
        .foregroundStyle(hot ? .white : accent ? tint.accent : YYColor.t1)
        .padding(.horizontal, 9)
        .frame(height: 28)
        .background(hot ? tint.fill : .clear, in: .rect(cornerRadius: 8))
        .animation(YYMotion.quick, value: hot)
        .contentShape(.rect)
        .onDrop(of: [.utf8PlainText, .plainText], isTargeted: Binding(get: { hot }, set: { target = $0 ? id : (target == id ? nil : target) })) { providers in
            Task {
                let ids = await BookDrag.ids(providers)
                put(ids)
                done()
            }
            return true
        }
    }
}

extension PinnedCollections {
    /// What a pinned row carries when dragged, to tell it from a book's id.
    static let dragPrefix = "yuyue-pinned:"
}
#endif

extension EnvironmentValues {
    /// Room the shelf window's pages leave at the left of their title for the window's buttons
    /// (and 显示侧栏) when the sidebar is put away.
    @Entry var shelfHeaderInset: CGFloat = 0
}
