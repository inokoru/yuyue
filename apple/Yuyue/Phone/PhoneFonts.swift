#if os(iOS)
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

// 字体 in 设置 › 阅读, as the Mac's: 永 in each face in a row of tiles — 系统、宋体、楷体, the faces
// you've picked, then 更多. A face still coming down fills with ink from the bottom, its name
// turned to the per cent; tapped then, it stops. 更多 opens half a sheet with the sample still
// showing above it: a face tapped there is chosen and fetched, and joins the row.

struct PhoneFontTiles: View {
    /// The face the settings are in.
    let selected: String
    /// Shown inside 设置 › 阅读; false in the book's Aa card where the tiles stand on their own.
    var titled = true
    let choose: (String) -> Void
    @Environment(\.horizontalSizeClass) private var sizeClass
    @State private var more = false
    @State private var confirming: YYConfirm?
    @State private var row = FontRow.ids()
    private var shelf: FontShelf { FontShelf.shared }

    /// Built-in three, the faces in the row, and the face in use when this book kept another.
    private var ids: [String] {
        var list = FontShelf.builtIn + row.filter { !FontShelf.builtIn.contains($0) }
        if !list.contains(selected) { list.append(selected) }
        return list
    }

    var body: some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            if titled {
                Text("字体").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: YYUI.s(6)), count: sizeClass == .compact ? 4 : 5), spacing: YYUI.s(6)) {
                ForEach(ids, id: \.self) { tile($0) }
                moreTile
            }
        }
        .padding(.vertical, titled ? YYUI.s(12) : 0)
        .sheet(isPresented: $more) {
            PhoneMoreFonts(selected: selected) { id in
                FontRow.add(id)
                row = FontRow.ids()
                choose(id)
            }
            .presentationDetents([.medium, .large])
            .presentationDragIndicator(.visible)
            .presentationBackgroundInteraction(.enabled(upThrough: .medium))
        }
        .yyConfirm($confirming)
        .onAppear { row = FontRow.ids() }
        .onReceive(NotificationCenter.default.publisher(for: .fontsChanged)) { _ in row = FontRow.ids() }
        .onReceive(NotificationCenter.default.publisher(for: FontRow.changed)) { _ in row = FontRow.ids() }
    }

    private func tile(_ id: String) -> some View {
        let on = selected == id
        let p = shelf.progress[id]
        let failed = p == nil && shelf.failure[id] != nil
        return Button {
            if p != nil { shelf.cancel(id) } else { choose(id) }
        } label: {
            VStack(spacing: YYUI.s(4)) {
                InkGlyph(id: id, size: YYUI.s(20)).frame(height: YYUI.s(24))
                Text(p.map { "\(Int($0 * 100))%" } ?? (failed ? String(localized: "重试") : shelf.name(id)))
                    .font(YYUI.font(9, on ? .semibold : .regular).monospacedDigit())
                    .foregroundStyle(failed ? YYColor.danger : on ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1).minimumScaleFactor(0.8)
            }
            .padding(.horizontal, YYUI.s(3))
            .frame(maxWidth: .infinity, minHeight: YYUI.s(58))
            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(10)))
            .overlay(alignment: .topTrailing) {
                if p == nil, !failed, shelf.size(id) != nil {
                    Image(systemName: "arrow.down")
                        .font(YYUI.font(7, .medium))
                        .foregroundStyle(YYColor.t3)
                        .padding(.top, YYUI.s(5))
                        .padding(.trailing, YYUI.s(5))
                }
            }
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(on ? YYTint.standard.accent : YYColor.line, lineWidth: on ? 1.5 : 0.5))
            .animation(YYMotion.quick, value: on)
            .contentShape(.rect(cornerRadius: YYUI.s(10)))
        }
        .buttonStyle(YYPressStyle(scale: 0.95))
        .sensoryFeedback(.selection, trigger: on)
        .contextMenu {
            if !FontShelf.builtIn.contains(id) {
                Button(String(localized: "从格子里拿掉"), systemImage: "minus.circle") {
                    FontRow.remove(id)
                    row = FontRow.ids()
                    if on { choose(fallback(id)) }
                }
                if shelf.info(id) != nil && shelf.isReady(id) || id.fileName != nil {
                    Button(String(localized: "删除字体"), systemImage: "trash", role: .destructive) { askDelete(id) }
                }
            }
        }
        .accessibilityLabel(Text(shelf.name(id)))
        .accessibilityValue(Text(p.map { "\(Int($0 * 100))%" } ?? (failed ? String(localized: "重试") : "")))
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    /// The last tile: three 永 cards fanned, as on the Mac.
    private var moreTile: some View {
        Button { more = true } label: {
            VStack(spacing: YYUI.s(4)) {
                ZStack {
                    moreCard("Kaiti SC").rotationEffect(.degrees(-10)).offset(x: -YYUI.s(4.5))
                    moreCard(nil).rotationEffect(.degrees(8)).offset(x: YYUI.s(4.5))
                    moreCard("Songti SC")
                }
                .frame(height: YYUI.s(24))
                Text("更多").font(YYUI.font(9)).foregroundStyle(YYColor.t2).lineLimit(1)
            }
            .frame(maxWidth: .infinity, minHeight: YYUI.s(58))
            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(10)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(YYColor.line, lineWidth: 0.8))
            .contentShape(.rect(cornerRadius: YYUI.s(10)))
        }
        .buttonStyle(YYPressStyle(scale: 0.95))
        .accessibilityLabel(Text("更多字体"))
    }

    private func moreCard(_ family: String?) -> some View {
        Text("永")
            .font(family.map { .custom($0, size: YYUI.s(9.5)) } ?? .system(size: YYUI.s(9.5)))
            .foregroundStyle(YYColor.t2)
            .frame(width: YYUI.s(16), height: YYUI.s(19))
            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(3)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(3)).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    /// The built-in face of the same kind: what a removed face's readers go back to.
    private func fallback(_ id: String) -> String {
        switch shelf.kind(id) {
        case .song, .fang: "serif"
        case .kai, .hand: "kai"
        case .hei: "system"
        }
    }

    private func askDelete(_ id: String) {
        let name = shelf.name(id)
        let size = shelf.info(id).map { "\(Int((Double($0.totalBytes) / 1_000_000).rounded())) MB" }
        confirming = YYConfirm(
            title: String(localized: "删除\(name)？"),
            message: size.map { String(localized: "这台手机上的 \($0) 字体文件会删掉；以后要用再下载。") } ?? String(localized: "拷进语阅的字体文件会删掉。"),
            button: String(localized: "删除")
        ) {
            let was = selected == id
            shelf.delete(id)
            FontRow.remove(id)
            row = FontRow.ids()
            if was { choose(fallback(id)) }
        }
    }
}

/// 更多字体: the faces by kind, each name written in its own face with a grey line under it. Tapped,
/// a face is chosen at once (the sample above shows it) and fetched if it isn't here.
struct PhoneMoreFonts: View {
    let selected: String
    let choose: (String) -> Void
    @Environment(\.dismiss) private var dismiss
    @State private var kind: Kind = .song
    @State private var importing = false
    @State private var failed = false
    private var shelf: FontShelf { FontShelf.shared }

    enum Kind: CaseIterable, Identifiable {
        case song, kai, hei, mine
        var id: Self { self }
        var title: String {
            switch self {
            case .song: String(localized: "宋体")
            case .kai: String(localized: "楷体")
            case .hei: String(localized: "黑体")
            case .mine: String(localized: "你自己的")
            }
        }
        /// The one character each chip shows in its own kind of face.
        var mark: (String, String?)? {
            switch self {
            case .song: ("宋", "Songti SC")
            case .kai: ("楷", "Kaiti SC")
            case .hei: ("黑", nil)
            case .mine: nil
            }
        }
    }

    private var ids: [String] {
        let all = FontShelf.builtIn + shelf.catalog.map(\.id)
        switch kind {
        case .mine: return shelf.files.map { "file:\($0)" }
        case .song: return all.filter { [.song, .fang].contains(shelf.kind($0)) }
        case .kai: return all.filter { [.kai, .hand].contains(shelf.kind($0)) }
        case .hei: return all.filter { shelf.kind($0) == .hei }
        }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack {
                Text("更多字体").font(YYUI.font(13, .semibold)).foregroundStyle(YYColor.t1)
                HStack {
                    Spacer()
                    Button("完成") { dismiss() }.font(YYUI.font(12.5)).foregroundStyle(YYColor.t2)
                }
            }
            .padding(.top, YYUI.sheetGrabberTop).padding(.bottom, YYUI.s(10))
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: YYUI.s(6)) { ForEach(Kind.allCases) { chip($0) } }
            }
            .padding(.bottom, YYUI.s(8))
            ScrollView {
                VStack(spacing: 0) {
                    if kind == .mine, ids.isEmpty {
                        VStack(spacing: YYUI.s(6)) {
                            Image(systemName: "textformat")
                                .font(.system(size: YYUI.s(15), weight: .regular))
                                .foregroundStyle(YYColor.t3)
                                .frame(width: YYUI.s(36), height: YYUI.s(36))
                                .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(10)))
                                .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(YYColor.line, lineWidth: 0.5))
                                .padding(.bottom, YYUI.s(2))
                            Text("还没有自定义字体")
                                .font(YYUI.font(12, .semibold))
                                .foregroundStyle(YYColor.t1)
                            Text("支持导入 .ttf、.otf 字体文件，仅保存在本机。")
                                .font(YYUI.font(10.5))
                                .foregroundStyle(YYColor.t3)
                                .multilineTextAlignment(.center)
                                .lineSpacing(2)
                                .fixedSize(horizontal: false, vertical: true)
                            Button { importing = true } label: {
                                HStack(spacing: YYUI.s(6)) {
                                    Image(systemName: "plus").font(YYUI.font(11.5, .medium))
                                    Text("从文件添加…").font(YYUI.font(11.5, .medium))
                                }
                                .foregroundStyle(YYColor.t1)
                                .padding(.horizontal, YYUI.s(14))
                                .frame(height: YYUI.s(32))
                                .background(YYColor.card, in: .capsule)
                                .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                            }
                            .buttonStyle(YYPressStyle(scale: 0.96))
                            .padding(.top, YYUI.s(8))
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.horizontal, YYUI.s(20))
                        .padding(.vertical, YYUI.s(28))
                    } else {
                        ForEach(ids, id: \.self) { row($0) }
                        Button { importing = true } label: {
                            HStack(spacing: YYUI.s(8)) {
                                Image(systemName: "plus").font(YYUI.font(13, .light))
                                Text("从文件添加…").font(YYUI.font(11.5))
                                Spacer()
                            }
                            .foregroundStyle(YYColor.t2)
                            .padding(.horizontal, YYUI.s(10)).frame(minHeight: YYUI.s(40))
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                    if failed {
                        Text("这个文件不是能用的字体").font(YYUI.font(10)).foregroundStyle(YYColor.danger)
                            .frame(maxWidth: .infinity, alignment: kind == .mine && ids.isEmpty ? .center : .leading)
                            .padding(.horizontal, YYUI.s(10))
                    }
                }
                .padding(.horizontal, -YYUI.s(6))
            }
        }
        .padding(.horizontal, YYUI.s(16))
        .background(YYColor.bg)
        .fileImporter(isPresented: $importing, allowedContentTypes: [.font, UTType(filenameExtension: "ttf") ?? .font, UTType(filenameExtension: "otf") ?? .font]) { result in
            guard case .success(let url) = result else { return }
            if let id = shelf.add(file: url) {
                failed = false
                kind = .mine
                choose(id)
            } else {
                failed = true
            }
        }
        .onAppear { kind = startKind }
    }

    /// Opens on the kind of the face in use.
    private var startKind: Kind {
        if selected.fileName != nil { return .mine }
        switch shelf.kind(selected) {
        case .song, .fang: return .song
        case .kai, .hand: return .kai
        case .hei: return .hei
        }
    }

    private func chip(_ k: Kind) -> some View {
        let on = kind == k
        return Button { kind = k } label: {
            HStack(spacing: YYUI.s(4)) {
                if let (c, family) = k.mark {
                    Text(verbatim: c).font(family.map { .custom($0, size: YYUI.s(12)) } ?? .system(size: YYUI.s(12))).foregroundStyle(YYColor.t1)
                }
                Text(k.title).font(YYUI.font(10.5, on ? .semibold : .regular)).foregroundStyle(on ? YYColor.t1 : YYColor.t2)
            }
            .padding(.horizontal, YYUI.s(10)).frame(height: YYUI.s(24))
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(on ? YYColor.t1 : YYColor.line, lineWidth: on ? 1 : 0.5))
            .animation(YYMotion.quick, value: on)
        }
        .buttonStyle(.plain)
    }

    private func row(_ id: String) -> some View {
        let on = selected == id
        let ready = shelf.isReady(id)
        let name = shelf.name(id)
        let p = shelf.progress[id]
        return Button {
            if p != nil { return }
            choose(id)
        } label: {
            HStack(spacing: YYUI.s(10)) {
                VStack(alignment: .leading, spacing: YYUI.s(1)) {
                    Text(name)
                        .font(ready ? shelf.font(id, size: YYUI.s(16)) : FontPreview.shared.font(id, text: name, size: YYUI.s(16)) ?? shelf.font(id, size: YYUI.s(16)))
                        .foregroundStyle(YYColor.t1)
                        .onAppear { if !ready { FontPreview.shared.load(id, text: name) } }
                    Text(detail(id)).font(YYUI.font(9.5)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: YYUI.s(8))
                if let p {
                    ProgressRing(value: p).frame(width: YYUI.s(15), height: YYUI.s(15))
                } else if !ready {
                    Image(systemName: "arrow.down.to.line").font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                }
            }
            .padding(.horizontal, YYUI.s(10)).frame(minHeight: YYUI.s(48))
            .background(on ? YYColor.inset.opacity(0.8) : .clear, in: .rect(cornerRadius: YYUI.s(10)))
            .animation(YYMotion.quick, value: on)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func detail(_ id: String) -> String {
        if FontShelf.builtIn.contains(id) { return String(localized: "语阅自带") }
        if id.fileName != nil { return String(localized: "从文件添加") }
        let size = shelf.info(id).map { "\(Int((Double($0.totalBytes) / 1_000_000).rounded())) MB" } ?? ""
        if let p = shelf.progress[id] { return String(localized: "开源 · \(size) · 下载中 \(Int(p * 100))%") }
        if shelf.isReady(id) { return String(localized: "开源 · \(size) · 已下载") }
        return String(localized: "开源 · \(size)")
    }
}
#endif
