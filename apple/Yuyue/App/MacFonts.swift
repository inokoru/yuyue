import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

// 字体 in 设置 › 阅读: a row of tiles (系统、宋体、楷体 and the faces you've picked), each 永 in its
// face; a face still downloading fills with ink from the bottom. The last tile opens 字样册.

/// The faces in the row after the built-in three, in the order they were picked. On this device only.
enum FontRow {
    static let key = "fonts.row"
    static let changed = Notification.Name("yuyue.fontRowChanged")

    static func ids() -> [String] {
        (UserDefaults.standard.array(forKey: key) as? [String]) ?? ["wenkai"]
    }

    static func add(_ id: String) {
        guard !FontShelf.builtIn.contains(id) else { return }
        var all = ids()
        if !all.contains(id) { all.append(id) }
        UserDefaults.standard.set(all, forKey: key)
        NotificationCenter.default.post(name: changed, object: nil)
    }

    static func remove(_ id: String) {
        UserDefaults.standard.set(ids().filter { $0 != id }, forKey: key)
        NotificationCenter.default.post(name: changed, object: nil)
    }
}

/// 永 in a face. Not here yet: a pale 永, with the ink rising in it as the face comes down.
struct InkGlyph: View {
    let id: String
    var size: CGFloat = 26
    var text = "永"
    private var shelf: FontShelf { FontShelf.shared }

    var body: some View {
        let ready = shelf.isReady(id)
        let face: SwiftUI.Font = ready ? shelf.font(id, size: size)
            : FontPreview.shared.font(id, text: text, size: size) ?? shelf.font(id, size: size)
        ZStack {
            if ready {
                Text(text).font(face).foregroundStyle(YYColor.t1)
            } else {
                Text(text).font(face).foregroundStyle(YYColor.t1.opacity(0.18))
                Text(text).font(face).foregroundStyle(YYColor.t1)
                    .mask(alignment: .bottom) {
                        GeometryReader { g in
                            Rectangle().frame(height: g.size.height * (shelf.progress[id] ?? 0))
                                .frame(maxHeight: .infinity, alignment: .bottom)
                        }
                    }
                    .animation(.linear(duration: 0.25), value: shelf.progress[id])
            }
        }
        .onAppear { if !ready { FontPreview.shared.load(id, text: text) } }
    }
}

#if os(macOS)
/// One face to choose in the row.
struct FontTile: View {
    let id: String
    let selected: Bool
    let pick: () -> Void
    var remove: (() -> Void)?
    var compact = false
    @State private var hover = false
    private var shelf: FontShelf { FontShelf.shared }

    var body: some View {
        Button {
            if compact, shelf.progress[id] != nil { shelf.cancel(id) } else { pick() }
        } label: {
            VStack(spacing: compact ? 3 : 6) {
                InkGlyph(id: id, size: compact ? 20 : 26).frame(height: compact ? 24 : 30)
                Text(compact ? (shelf.progress[id].map { "\(Int($0 * 100))%" } ?? shelf.name(id)) : shelf.name(id))
                    .font(.yy(size: compact ? 10 : 11.5, weight: selected ? .semibold : .regular).monospacedDigit())
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            .padding(.horizontal, compact ? 4 : 6)
            .padding(.top, compact ? 8 : 12)
            .padding(.bottom, compact ? 7 : 10)
            .frame(maxWidth: .infinity)
            .overlay(alignment: .topTrailing) {
                badge.padding(.top, compact ? 4 : 6).padding(.trailing, compact ? 4 : 7)
            }
            .overlay(RoundedRectangle(cornerRadius: compact ? 9 : 11).strokeBorder(selected ? YYTint.standard.accent : YYColor.line, lineWidth: selected ? 1.5 : 0.5))
            .contentShape(.rect(cornerRadius: compact ? 9 : 11))
            .animation(.easeOut(duration: 0.15), value: selected)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .contextMenu {
            if let remove { Button("从格子里拿掉", action: remove) }
        }
        .accessibilityLabel(Text(shelf.name(id)))
        .accessibilityValue(Text(shelf.progress[id].map { "\(Int($0 * 100))%" } ?? ""))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    @ViewBuilder private var badge: some View {
        if let p = shelf.progress[id] {
            if hover {
                Button { shelf.cancel(id) } label: {
                    Image(systemName: "xmark").font(.yy(size: compact ? 7 : 7.5, weight: .bold)).foregroundStyle(YYColor.t2)
                        .frame(width: compact ? 13 : 15, height: compact ? 13 : 15).background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .help(Text("停下"))
            } else if !compact {
                Text("\(Int(p * 100))%").font(.yy(size: 10).monospacedDigit()).foregroundStyle(YYColor.t3)
            }
        } else if shelf.failure[id] != nil {
            Text("重试").font(.yy(size: compact ? 8.5 : 10)).foregroundStyle(YYColor.danger)
        } else if let size = shelf.size(id) {
            if compact {
                Image(systemName: "arrow.down").font(.yy(size: 7.5, weight: .medium)).foregroundStyle(YYColor.t3)
            } else {
                HStack(spacing: 2) {
                    Image(systemName: "arrow.down").font(.yy(size: 8, weight: .medium))
                    Text(size)
                }
                .font(.yy(size: 10)).foregroundStyle(YYColor.t3)
            }
        }
    }
}

/// The last tile: three 永 cards fanned, 更多字体 under them.
struct MoreFontsTile: View {
    let open: () -> Void
    var compact = false
    @State private var hover = false

    var body: some View {
        let fan = hover && !YYMotion.reduce
        Button(action: open) {
            VStack(spacing: compact ? 3 : 6) {
                ZStack {
                    card("Kaiti SC").rotationEffect(.degrees(fan ? -16 : -10)).offset(x: fan ? (compact ? -7 : -9) : (compact ? -4 : -5))
                    card("PingFang SC").rotationEffect(.degrees(fan ? 14 : 8)).offset(x: fan ? (compact ? 7 : 9) : (compact ? 4 : 5))
                    card("Songti SC")
                }
                .frame(height: compact ? 24 : 30)
                Text(compact ? "更多" : "更多字体").font(.yy(size: compact ? 10 : 11.5)).foregroundStyle(YYColor.t2).lineLimit(1)
            }
            .padding(.horizontal, compact ? 4 : 6)
            .padding(.top, compact ? 8 : 12)
            .padding(.bottom, compact ? 7 : 10)
            .frame(maxWidth: .infinity)
            .background(hover ? YYColor.card.opacity(0.6) : .clear, in: .rect(cornerRadius: compact ? 9 : 11))
            .overlay(RoundedRectangle(cornerRadius: compact ? 9 : 11).strokeBorder(YYColor.line, lineWidth: 0.8))
            .contentShape(.rect(cornerRadius: compact ? 9 : 11))
        }
        .buttonStyle(.plain)
        .onHover { h in withAnimation(YYMotion.reduce ? nil : .snappy(duration: 0.22)) { hover = h } }
        .help(Text("更多字体"))
        .accessibilityLabel(Text("更多字体"))
    }

    private func card(_ family: String) -> some View {
        Text("永").font(.custom(family, size: compact ? 11 : 14)).foregroundStyle(YYColor.t2)
            .frame(width: compact ? 19 : 24, height: compact ? 21 : 26)
            .background(YYColor.card, in: .rect(cornerRadius: compact ? 3 : 4))
            .overlay(RoundedRectangle(cornerRadius: compact ? 3 : 4).strokeBorder(YYColor.line, lineWidth: 0.5))
    }
}

// MARK: - 字样册

/// 更多字体: groups down the left, each face in its own name in the middle, and on the right a
/// sheet of paper — 永 in a 米字格, the face's name, and a passage the reader can rewrite.
struct FontBook: View {
    /// Picked: the face to use (downloading, if it must).
    let choose: (String) -> Void
    let current: String
    @Environment(\.dismiss) private var dismiss
    @State private var group: FontGroup = .all
    @State private var pick: String?
    @State private var text = FontBook.passage
    @State private var importing = false
    @State private var macFaces: [String] = []
    private var shelf: FontShelf { FontShelf.shared }

    static let passage = String(localized: "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响。")

    enum FontGroup: Hashable, CaseIterable {
        case all, song, kai, hei, fang, hand, mac

        var title: String {
            switch self {
            case .all: String(localized: "全部")
            case .song: String(localized: "宋")
            case .kai: String(localized: "楷")
            case .hei: String(localized: "黑")
            case .fang: String(localized: "仿宋")
            case .hand: String(localized: "手写")
            case .mac: String(localized: "这台 Mac")
            }
        }

        /// The group's mark: the word written in that look.
        var mark: (String, String?) {
            switch self {
            case .all: ("字", nil)
            case .song: ("宋", "Songti SC")
            case .kai: ("楷", "Kaiti SC")
            case .hei: ("黑", nil)
            case .fang: ("仿", "Songti SC")
            case .hand: ("手", "Kaiti SC")
            case .mac: ("", nil)
            }
        }

        var kind: FontKind? {
            switch self {
            case .song: .song
            case .kai: .kai
            case .hei: .hei
            case .fang: .fang
            case .hand: .hand
            case .all, .mac: nil
            }
        }
    }

    private var all: [String] {
        FontShelf.builtIn + shelf.catalog.map(\.id) + shelf.files.map { "file:\($0)" } + macFaces
    }

    private func ids(in g: FontGroup) -> [String] {
        switch g {
        case .all: all
        case .mac: macFaces
        default: all.filter { shelf.kind($0) == g.kind }
        }
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                groups
                Divider()
                list
                Divider()
                paper
            }
            Divider()
            footer
        }
        .frame(width: 800, height: 520)
        .background(YYColor.bg)
        .onAppear {
            macFaces = shelf.macFaces()
            pick = pick ?? current
        }
        .fileImporter(isPresented: $importing, allowedContentTypes: [.font], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result else { return }
            let added = urls.compactMap { shelf.add(file: $0) }
            if let first = added.first { pick = first; group = .all }
        }
    }

    private var groups: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text("字样册").font(.yy(size: 14, weight: .bold)).padding(.horizontal, 8).padding(.bottom, 10)
            ForEach(FontGroup.allCases.filter { $0 == .all || !ids(in: $0).isEmpty }, id: \.self) { g in
                if g == .mac { Divider().padding(.horizontal, 8).padding(.vertical, 6) }
                let on = group == g
                Button { group = g } label: {
                    HStack(spacing: 9) {
                        FontGroup.markView(g)
                        Text(g.title).font(.yy(size: 12.5, weight: on ? .semibold : .regular)).foregroundStyle(YYColor.t1)
                        Spacer(minLength: 4)
                        Text("\(ids(in: g).count)").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                    }
                    .padding(.horizontal, 8).frame(height: 30)
                    .background(on ? ShelfDesk.on : .clear, in: .rect(cornerRadius: 8))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            Spacer()
        }
        .padding(.horizontal, 8).padding(.top, 18).padding(.bottom, 10)
        .frame(width: 150)
        .background(Color(nsColor: SettingsSidebar.header))
    }

    private var list: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 0) {
                if group == .all {
                    section(String(localized: "可以下载"), shelf.catalog.map(\.id))
                    section(String(localized: "你添加的"), shelf.files.map { "file:\($0)" })
                    section(String(localized: "这台 Mac"), macFaces)
                    section(String(localized: "自带"), FontShelf.builtIn)
                } else {
                    section(nil, ids(in: group))
                }
            }
            .padding(8)
        }
        .frame(width: 262)
    }

    @ViewBuilder private func section(_ title: String?, _ ids: [String]) -> some View {
        if !ids.isEmpty {
            if let title {
                Text(title).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3).padding(.horizontal, 10).padding(.top, 10).padding(.bottom, 4)
            }
            ForEach(ids, id: \.self) { id in row(id) }
        }
    }

    private func row(_ id: String) -> some View {
        let on = pick == id
        return Button { withAnimation(.easeOut(duration: 0.15)) { pick = id } } label: {
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(shelf.name(id))
                        .font(shelf.isReady(id) ? shelf.font(id, size: 17) : FontPreview.shared.font(id, text: shelf.name(id), size: 17) ?? shelf.font(id, size: 17))
                        .foregroundStyle(YYColor.t1).lineLimit(1)
                    Text(meta(id)).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: 6)
                status(id)
            }
            .padding(.horizontal, 10).padding(.vertical, 7)
            .background(on ? ShelfDesk.on : .clear, in: .rect(cornerRadius: 9))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .simultaneousGesture(TapGesture(count: 2).onEnded { pick = id; use() })
        .contextMenu {
            if id.fileName != nil || (shelf.info(id) != nil && shelf.isReady(id)) {
                Button("删除字体文件", role: .destructive) { shelf.delete(id); FontRow.remove(id) }
            }
        }
        .onAppear { FontPreview.shared.load(id, text: shelf.name(id)) }
    }

    @ViewBuilder private func status(_ id: String) -> some View {
        if let p = shelf.progress[id] {
            ProgressRing(value: p).frame(width: 14, height: 14)
        } else if FontRow.ids().contains(id) || FontShelf.builtIn.contains(id), shelf.isReady(id) {
            Image(systemName: "checkmark").font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYTint.standard.accent)
        } else if shelf.failure[id] != nil {
            Text("没下完").font(.yy(size: 10.5)).foregroundStyle(YYColor.danger)
        } else if let size = shelf.size(id) {
            HStack(spacing: 3) { Image(systemName: "arrow.down").font(.yy(size: 9)); Text(size) }
                .font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
        }
    }

    private func meta(_ id: String) -> String {
        let kind = Self.kindName(shelf.kind(id))
        if shelf.info(id) != nil { return String(localized: "开源 · \(kind)") }
        if id.fileName != nil { return String(localized: "你添加的 · \(kind)") }
        if id.localFamily != nil { return String(localized: "这台 Mac · \(kind)") }
        return String(localized: "自带 · \(kind)")
    }

    static func kindName(_ k: FontKind) -> String {
        switch k {
        case .song: String(localized: "宋")
        case .kai: String(localized: "楷")
        case .hei: String(localized: "黑")
        case .fang: String(localized: "仿宋")
        case .hand: String(localized: "手写")
        }
    }

    /// The paper on the right: 永 in a 米字格, the name, and the passage in the face.
    private var paper: some View {
        let id = pick ?? current
        let ready = shelf.isReady(id)
        let face = { (size: CGFloat, text: String) -> SwiftUI.Font in
            ready ? shelf.font(id, size: size) : FontPreview.shared.font(id, text: text, size: size) ?? shelf.font(id, size: size)
        }
        return VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .bottom, spacing: 18) {
                MiZiGe { Text("永").font(face(80, "永")).foregroundStyle(Self.ink) }
                    .frame(width: 104, height: 104)
                VStack(alignment: .leading, spacing: 6) {
                    Text(shelf.name(id)).font(face(24, shelf.name(id))).foregroundStyle(Self.ink).lineLimit(1).minimumScaleFactor(0.6)
                    Text(about(id)).font(.yy(size: 11)).foregroundStyle(Self.ink2)
                }
                .padding(.bottom, 2)
            }
            Rectangle().fill(Self.rule).frame(height: 0.5).padding(.vertical, 16)
            TextEditor(text: $text)
                .font(face(15.5, text))
                .foregroundStyle(Self.ink)
                .scrollContentBackground(.hidden)
                .lineSpacing(15.5 * 0.9)
                .frame(maxHeight: .infinity)
                .onChange(of: text) { FontPreview.shared.load(id, text: text) }
            Text("春眠不觉晓 · 天地玄黄 · 0123456789 · Aa Bb").font(face(13, "春眠不觉晓 · 天地玄黄 · 0123456789 · Aa Bb")).foregroundStyle(Self.ink2).padding(.top, 6)
            Text("点上面这段字可以改成你想看的；换字体时它跟着走").font(.yy(size: 10.5)).foregroundStyle(Self.ink2.opacity(0.75)).padding(.top, 6)
        }
        .padding(.horizontal, 26).padding(.top, 24).padding(.bottom, 16)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .background(Self.paperColor)
        .id(id)
        .transition(.opacity)
        .animation(.easeOut(duration: 0.18), value: id)
        .onAppear {
            FontPreview.shared.load(id, text: "永")
            FontPreview.shared.load(id, text: text)
        }
    }

    private func about(_ id: String) -> String {
        let kind = Self.kindName(shelf.kind(id))
        if let info = shelf.info(id) {
            let mb = "\(Int((Double(info.totalBytes) / 1_000_000).rounded())) MB"
            return String(localized: "\(kind) · 开源（\(info.license)） · \(mb)")
        }
        if id.fileName != nil { return String(localized: "\(kind) · 你添加的") }
        if id.localFamily != nil { return String(localized: "\(kind) · 这台 Mac 上的") }
        return String(localized: "\(kind) · 语阅自带")
    }

    private var footer: some View {
        let id = pick ?? current
        let using = id == current && shelf.isReady(id)
        let needs = !shelf.isReady(id) && shelf.info(id) != nil
        return HStack(spacing: 8) {
            Button("从文件添加…") { importing = true }
            Spacer()
            Button("取消") { dismiss() }.keyboardShortcut(.cancelAction)
            Button(using ? String(localized: "正在用") : needs ? String(localized: "下载并选用 · \(shelf.size(id) ?? "")") : String(localized: "选用"), action: use)
                .keyboardShortcut(.defaultAction)
                .disabled(using)
        }
        .buttonStyle(YYButtonStyle(kind: .plain))
        .padding(.horizontal, 16).padding(.vertical, 12)
    }

    private func use() {
        guard let id = pick, !(id == current && shelf.isReady(id)) else { return }
        dismiss()
        choose(id)
    }

    // The paper's colours: 纸本.
    static let paperColor = Color(red: 245 / 255, green: 244 / 255, blue: 237 / 255)
    static let ink = Color(red: 35 / 255, green: 36 / 255, blue: 31 / 255)
    static let ink2 = Color(red: 138 / 255, green: 133 / 255, blue: 119 / 255)
    static let rule = Color(red: 226 / 255, green: 223 / 255, blue: 211 / 255)
}

extension FontBook.FontGroup {
    @ViewBuilder static func markView(_ g: FontBook.FontGroup) -> some View {
        let (word, family) = g.mark
        SwiftUI.Group {
            if g == .mac {
                Image(systemName: "desktopcomputer").font(.yy(size: 10))
            } else {
                Text(word).font(family.map { .custom($0, size: 13) } ?? .yy(size: 12.5, weight: .medium))
            }
        }
        .foregroundStyle(YYColor.t1)
        .frame(width: 20, height: 20)
        .background(YYColor.card, in: .rect(cornerRadius: 5))
        .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(YYColor.line, lineWidth: 0.5))
    }
}
#endif

/// A 米字格: the square a character is practised in, with its cross and diagonals in faint red.
struct MiZiGe<Content: View>: View {
    @ViewBuilder let content: Content
    private static var red: Color { Color(red: 194 / 255, green: 69 / 255, blue: 58 / 255) }

    var body: some View {
        content
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background {
                GeometryReader { g in
                    let w = g.size.width, h = g.size.height
                    Path { p in
                        p.move(to: CGPoint(x: w / 2, y: 0)); p.addLine(to: CGPoint(x: w / 2, y: h))
                        p.move(to: CGPoint(x: 0, y: h / 2)); p.addLine(to: CGPoint(x: w, y: h / 2))
                    }
                    .stroke(Self.red.opacity(0.18), lineWidth: 1)
                    Path { p in
                        p.move(to: .zero); p.addLine(to: CGPoint(x: w, y: h))
                        p.move(to: CGPoint(x: w, y: 0)); p.addLine(to: CGPoint(x: 0, y: h))
                    }
                    .stroke(Self.red.opacity(0.11), style: StrokeStyle(lineWidth: 0.8, dash: [3, 3]))
                }
            }
            .overlay(Rectangle().strokeBorder(Self.red.opacity(0.32), lineWidth: 1))
    }
}

/// A thin ring filling clockwise.
struct ProgressRing: View {
    let value: Double

    var body: some View {
        ZStack {
            Circle().stroke(YYColor.line, lineWidth: 2)
            Circle().trim(from: 0, to: value).stroke(YYColor.t1, style: StrokeStyle(lineWidth: 2, lineCap: .round)).rotationEffect(.degrees(-90))
        }
        .animation(.linear(duration: 0.2), value: value)
    }
}
