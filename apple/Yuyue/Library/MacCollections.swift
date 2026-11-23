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

/// 全部合集 (合集墙): the phone's place panel — one white card, a row for each 合集 with its paper
/// mark, its name, when it was last used and how many. New, rename, delete and the order are here;
/// a pin on the row under the pointer (or 右键) keeps it in the sidebar.
struct CollectionWall: View {
    @State private var confirming: YYConfirm?
    @Bindable var library: Library
    @Binding var pinnedRaw: String
    /// Room for the window's buttons when the sidebar is away.
    var leading: CGFloat = 0
    let open: (String) -> Void
    @AppStorage("collections.sort") private var sort = Sort.recent
    @State private var hovering: String?
    @State private var dropTarget: String?
    @State private var naming: Naming?
    @State private var name = ""
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @AppStorage(EInk.key) private var eink = false

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

    private var shown: [BookCollection] {
        let all = library.collections
        switch sort {
        case .recent: return all.sorted { $0.usedAt > $1.usedAt }
        case .name: return all.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        case .count: return all.sorted { $0.count > $1.count }
        case .own: return all
        }
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
                VStack(spacing: 10) {
                    Image(systemName: "square.stack").font(.yy(size: 28, weight: .light)).foregroundStyle(YYColor.t3)
                    Text("还没有合集。点右上角“新建合集”，或者在一本书上右键“加入合集…”")
                        .font(.yy(size: 12.5)).foregroundStyle(YYColor.t2)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
            } else {
                ScrollView {
                    HStack {
                        Text("\(library.collections.count) 个")
                            .font(.yy(size: 12.5, weight: .semibold).monospacedDigit())
                            .foregroundStyle(YYColor.t1)
                        Spacer()
                        sortMenu
                    }
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                    .padding(.top, 9)
                    .padding(.bottom, 14)
                    // App Store's 分类: a white tile for each 合集, its paper mark at the top right,
                    // the name and how many at the bottom left.
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 236), spacing: 14, alignment: .top)], alignment: .leading, spacing: 14) {
                        ForEach(shown, id: \.id) { row($0) }
                    }
                    // Re-sorted or reordered: the tiles move to their new places.
                    .animation(YYMotion.layout, value: shown.map(\.id))
                    // One left edge with the head and the count above.
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                    .padding(.bottom, 40)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(.container, edges: .top)
        .background(YYColor.bg)
        .yyPrompt(naming?.collection == nil ? String(localized: "新建合集") : String(localized: "重命名合集"),
                  message: naming?.collection == nil ? String(localized: "起个名字，之后可以把书拖进去。") : "",
                  text: $name, prompt: String(localized: "合集的名字"),
                  isPresented: Binding(get: { naming != nil }, set: { if !$0 { naming = nil } }),
                  confirm: naming?.collection == nil ? String(localized: "建好了") : String(localized: "保存"),
                  onConfirm: finishNaming)
        .yyConfirm($confirming)
    }

    /// The shelf's 最近读过 ⇅: grey words, a menu.
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

    private var header: some View {
        HStack(spacing: 12) {
            Text("全部合集")
                .font(.yy(size: 22, weight: .bold))
                .foregroundStyle(YYColor.t1)
            Spacer()
            // The same as 导入 on the shelf.
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

    /// One tile, as App Store's 分类: the paper mark at the top right, the name and how many at the
    /// bottom left. It rises a little under the pointer; a book held over it tints it.
    private func row(_ c: BookCollection) -> some View {
        let isPinned = pinned.contains(c.id)
        let hover = hovering == c.id
        let target = dropTarget == c.id
        let shape = RoundedRectangle(cornerRadius: 16, style: .continuous)
        return Button { open(c.id) } label: {
            ZStack(alignment: .topTrailing) {
                VStack(alignment: .leading, spacing: 3) {
                    Spacer(minLength: 0)
                    Text(c.name)
                        .font(.yy(size: 18, weight: .bold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    HStack(spacing: 4) {
                        Text(String(localized: "\(Int(c.count)) 本"))
                            .font(.yy(size: 11.5).monospacedDigit())
                            .foregroundStyle(YYColor.t3)
                        pin(c, isPinned: isPinned, hover: hover)
                    }
                    .frame(height: 18)
                }
                .padding(.trailing, 84)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                CollectionMark(symbol: CollectionIcon.symbol(for: c), size: 60)
            }
            .padding(.leading, 16)
            .padding([.top, .trailing], 16)
            .padding(.bottom, 12)
            .frame(height: 118)
            .background(target ? tint.accentSoft : YYColor.card, in: shape)
            .overlay { shape.strokeBorder(target ? tint.edge : YYColor.line, lineWidth: 0.5) }
            .shadow(color: .black.opacity(eink ? 0 : (hover ? 0.09 : 0.025)), radius: hover ? 12 : 2, y: hover ? 6 : 1)
            .offset(y: hover && !YYMotion.reduce ? -2 : 0)
            .contentShape(shape)
            .animation(YYMotion.quick, value: hover)
            .animation(YYMotion.quick, value: target)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 ? c.id : (hovering == c.id ? nil : hovering) }
        // Books dragged onto a row go in.
        .onDrop(of: [.utf8PlainText, .plainText], isTargeted: Binding(get: { dropTarget == c.id }, set: { dropTarget = $0 ? c.id : (dropTarget == c.id ? nil : dropTarget) })) { providers in
            Task {
                for id in await BookDrag.ids(providers) where !id.hasPrefix(PinnedCollections.dragPrefix) {
                    library.setBook(id, in: c.id, true)
                }
            }
            return true
        }
        .contextMenu {
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
                confirming = YYConfirm(title: String(localized: "删除合集“\(c.name)”？"), message: String(localized: "这个合集删掉，里面的书还在书架上。"), button: String(localized: "删除")) { pinnedRaw = PinnedCollections.raw(pinned.filter { $0 != c.id }); library.deleteCollection(c) }
            }
        }
        .accessibilityLabel("\(c.name)，\(Int(c.count)) 本")
        .accessibilityAction(named: isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏")) { togglePin(c.id) }
    }

    /// Pinned: a small pin after the count. Under the pointer: a small round button to pin or unpin.
    @ViewBuilder private func pin(_ c: BookCollection, isPinned: Bool, hover: Bool) -> some View {
        if hover {
            Button { togglePin(c.id) } label: {
                Image(systemName: isPinned ? "pin.slash" : "pin")
                    .accessibilityLabel(isPinned ? Text("取消固定") : Text("固定到侧栏"))
                    .font(.yy(size: 9.5, weight: .medium))
                    .foregroundStyle(tint.accent)
                    .frame(width: 20, height: 20)
                    .background(YYColor.card, in: .circle)
                    .overlay { Circle().strokeBorder(YYColor.line, lineWidth: 0.5) }
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .help(isPinned ? String(localized: "取消固定") : String(localized: "固定到侧栏"))
            .transition(.opacity)
        } else if isPinned {
            Image(systemName: "pin.fill")
                .font(.yy(size: 9))
                .foregroundStyle(tint.accent)
                .frame(width: 20, height: 20)
                .accessibilityHidden(true)
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

/// The phone's place mark on the Mac: a square of warm paper, one thin line drawing over two
/// horizons.
struct CollectionMark: View {
    let symbol: String
    var size: CGFloat = 36

    static let paper = Color.yyDynamic(light: YYRGBA(243, 240, 232), dark: YYRGBA(43, 41, 37))
    static let ink = Color.yyDynamic(light: YYRGBA(43, 43, 43), dark: YYRGBA(233, 229, 220))

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: size * 0.25, style: .continuous)
        ZStack {
            shape.fill(Self.paper)
            shape.strokeBorder(Self.ink.opacity(0.07), lineWidth: 0.5)
            VStack(spacing: size * 0.055) {
                Image(systemName: symbol)
                    .font(.system(size: size * 0.34, weight: .light))
                VStack(spacing: size * 0.05) {
                    Capsule().frame(width: size * 0.6, height: max(0.7, size * 0.015))
                    Capsule().frame(width: size * 0.38, height: max(0.7, size * 0.015)).opacity(0.75)
                }
            }
            .foregroundStyle(Self.ink)
            .offset(y: size * 0.03)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
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
