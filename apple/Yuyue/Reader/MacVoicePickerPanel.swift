#if os(macOS)
import AVFoundation
import SwiftUI
import YuyueCore

/// Mac：读书窗口里“换声音”的浮窗——上面搜和男女，左边四组，右边列表，底下说怎么试听。
struct MacVoicePickerPanel: View {
    let language: String
    let bookID: String?
    let selectedID: String?
    /// 《书名》, for the line at the bottom; nil when there is no book (a show's host).
    let sampleSource: String?
    let onPreview: (any Voice) -> Void
    let onSelect: (any Voice) -> Void
    let onOpenClone: (_ existingID: String?, _ initialName: String) -> Void
    let onOpenCloneDetail: (String) -> Void
    let onClose: () -> Void
    /// Pushed inside 人物声音: ‹ and whose voice it is (宝玉的声音), above the search.
    var title: String? = nil
    var onBack: (() -> Void)? = nil
    /// 宝玉在用: replaces a row's line when someone else already reads with it.
    var usedByLabel: ((String) -> String?)? = nil
    /// A character's first row: 跟旁白, reading with whatever the narrator reads.
    var follow: Follow? = nil

    struct Follow {
        var title: String = String(localized: "跟旁白")
        let narrator: (any Voice)?
        let detail: String
        let selected: Bool
        let pick: () -> Void
    }

    fileprivate enum Section: String, CaseIterable, Identifiable {
        case recommended, mine, cloud, system
        var id: String { rawValue }
    }

    private enum Sex: CaseIterable { case all, female, male }

    @State private var query = ""
    @State private var sex: Sex = .all
    @State private var focus: Section?
    @State private var hovered: String?
    @State private var hoverBack = false
    @State private var hoverClose = false
    @State private var hoverPreview: Task<Void, Never>?
    @State private var refreshTick = 0
    @State private var revealed = false
    /// The group lit in the rail without scrolling to its top (where the reading voice is).
    @State private var railLit: Section?
    private let tint = YYTint.standard

    /// One row of the list, whatever kind of voice it is.
    fileprivate struct Row: Identifiable {
        let id: String
        let name: String
        let badge: String?
        let narrator: Bool
        let detail: String
        let female: Bool?
        let voice: (any Voice)?
        let clone: ClonedEntry?
    }

    var body: some View {
        let _ = refreshTick
        let catalog = Voices.catalog(for: language, bookID: bookID)
        let groups = rows(catalog)

        VStack(spacing: 0) {
            if title != nil || onBack != nil { titleBar(title ?? String(localized: "声音")) }
            topBar
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            HStack(spacing: 0) {
                rail(catalog, groups)
                Rectangle().fill(YYColor.line).frame(width: 0.5)
                list(catalog, groups)
            }
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            footer
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(YYColor.card)
        .background {
            Button("", action: onBack ?? onClose).keyboardShortcut(.cancelAction).opacity(0)
        }
        .onDisappear { hoverPreview?.cancel() }
        .onReceive(NotificationCenter.default.publisher(for: ClonedVoices.changed)) { _ in refreshTick &+= 1 }
        .onReceive(NotificationCenter.default.publisher(for: KokoroModel.ready)) { _ in refreshTick &+= 1 }
        .onReceive(NotificationCenter.default.publisher(for: LiveVoiceModel.ready)) { _ in refreshTick &+= 1 }
    }

    // MARK: 上：搜 · 全部 / 女声 / 男声

    private func titleBar(_ title: String) -> some View {
        HStack(spacing: 6) {
            if let onBack {
                Button(action: onBack) {
                    Image(systemName: "chevron.left")
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(hoverBack ? YYColor.t1 : YYColor.t2)
                        .frame(width: 24, height: 24)
                        .background(hoverBack ? YYColor.inset : .clear, in: .rect(cornerRadius: 6))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .onHover { hoverBack = $0 }
                .animation(YYMotion.quick, value: hoverBack)
                .help(Text("返回"))
                .accessibilityLabel(Text("返回"))
            }
            Text(title)
                .font(.yy(size: 13.5, weight: .semibold))
                .foregroundStyle(YYColor.t1)
                .lineLimit(1)
            Spacer(minLength: 8)
            Button(action: onClose) {
                Image(systemName: "xmark")
                    .font(.yy(size: 10, weight: .bold))
                    .foregroundStyle(hoverClose ? YYColor.t1 : YYColor.t2)
                    .frame(width: 24, height: 24)
                    .background(hoverClose ? YYColor.line : YYColor.inset, in: .circle)
                    .contentShape(Circle().inset(by: -4))
            }
            .buttonStyle(.plain)
            .onHover { hoverClose = $0 }
            .animation(YYMotion.quick, value: hoverClose)
            .help(Text("关闭"))
            .accessibilityLabel(Text("关闭"))
        }
        .padding(.leading, onBack == nil ? 14 : 10)
        .padding(.trailing, 12)
        .frame(height: 40)
        .overlay(alignment: .bottom) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
    }

    private var topBar: some View {
        HStack(spacing: 9) {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t3)
            TextField(String(localized: "搜声音、形容词（稳、亮、青年…）"), text: $query)
                .textFieldStyle(.plain)
                .font(.yy(size: 13))
                .foregroundStyle(YYColor.t1)
            HStack(spacing: 2) {
                ForEach(Sex.allCases, id: \.self) { s in
                    let on = sex == s
                    Button {
                        withAnimation(YYMotion.quick) { sex = s }
                    } label: {
                        Text(sexLabel(s))
                            .font(.yy(size: 11.5, weight: on ? .semibold : .regular))
                            .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                            .padding(.horizontal, 8)
                                .padding(.vertical, 3.5)
                            .background {
                                if on {
                                    RoundedRectangle(cornerRadius: 5)
                                        .fill(YYColor.card)
                                        .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(YYColor.line, lineWidth: 0.5))
                                }
                            }
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(2)
            .background(YYColor.inset, in: .rect(cornerRadius: 7))
        }
        .padding(.leading, 14)
        .padding(.trailing, 12)
        .frame(height: 44)
    }

    private func sexLabel(_ s: Sex) -> String {
        switch s {
        case .all: String(localized: "全部")
        case .female: String(localized: "女声")
        case .male: String(localized: "男声")
        }
    }

    // MARK: 左：四组

    private func rail(_ catalog: Voices.Catalog, _ groups: [Section: [Row]]) -> some View {
        let current = focus ?? railLit ?? sections(catalog).first
        return VStack(alignment: .leading, spacing: 2) {
            ForEach(sections(catalog)) { section in
                let on = current == section
                Button {
                    focus = section
                } label: {
                    HStack(spacing: 7) {
                        Image(systemName: railIcon(section))
                            .font(.yy(size: 11))
                            .frame(width: 14)
                        Text(title(section))
                            .font(.yy(size: 12, weight: on ? .semibold : .regular))
                        Spacer(minLength: 4)
                        Text(railCount(section, catalog, groups))
                            .font(.yy(size: 10.5))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                    }
                    .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                    .padding(.horizontal, 8)
                    .frame(height: 29)
                    .background(on ? tint.soft : .clear, in: .rect(cornerRadius: 7))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 8)
        .frame(width: 136)
        .background(YYColor.inset.opacity(0.35))
    }

    private func railIcon(_ s: Section) -> String {
        switch s {
        case .recommended: "star.fill"
        case .mine: "face.smiling"
        case .cloud: "cloud.fill"
        case .system: "apple.logo"
        }
    }

    private func railCount(_ s: Section, _ catalog: Voices.Catalog, _ groups: [Section: [Row]]) -> String {
        if s == .recommended, case .needsDownload = catalog.recommended { return String(localized: "下载") }
        if s == .recommended, case .unsupported = catalog.recommended { return "–" }
        return "\(groups[s]?.count ?? 0)"
    }

    // MARK: 右：列表

    private func list(_ catalog: Voices.Catalog, _ groups: [Section: [Row]]) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    if let follow { followRow(follow) }
                    ForEach(sections(catalog)) { section in
                        header(section, catalog: catalog).id(section)
                        sectionBody(section, catalog, groups[section] ?? [])
                    }
                }
                .padding(.horizontal, 8)
                .padding(.top, 6)
                .padding(.bottom, 10)
            }
            .scrollIndicators(.automatic)
            // The voice reading now is in view when the panel opens, wherever its group is, and
            // the rail lights its group. Once: picking another later doesn't jump the list.
            .task(id: selectedID) {
                guard !revealed, let id = selectedID,
                      let section = Section.allCases.first(where: { groups[$0]?.contains { $0.id == id } == true }) else { return }
                revealed = true
                if section == .recommended, groups[.recommended]?.prefix(4).contains(where: { $0.id == id }) == true { return }
                try? await Task.sleep(for: .milliseconds(30))
                proxy.scrollTo(id, anchor: .center)
                railLit = section
            }
            .onChange(of: focus) { _, s in
                guard let s else { return }
                withAnimation(YYMotion.standard) { proxy.scrollTo(s, anchor: .top) }
            }
        }
    }

    private func header(_ s: Section, catalog: Voices.Catalog) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title(s)).font(.yy(size: 10.5, weight: .semibold))
            Spacer()
            if let note = headerNote(s, catalog: catalog) {
                Text(note).font(.yy(size: 10.5))
            }
        }
        .foregroundStyle(YYColor.t3)
        .padding(.horizontal, 8)
        .padding(.top, 8)
        .padding(.bottom, 4)
    }

    private func headerNote(_ s: Section, catalog: Voices.Catalog) -> String? {
        switch s {
        case .recommended:
            if case .needsDownload = catalog.recommended {
                String(localized: "Kokoro 轻量离线 · \(KokoroModel.totalMegabytes) MB")
            } else {
                String(localized: "跨端离线好声音")
            }
        case .mine: String(localized: "克隆与本书角色")
        case .system: String(localized: "不推荐 · 仅断网兜底")
        default: nil
        }
    }

    @ViewBuilder
    private func sectionBody(_ s: Section, _ catalog: Voices.Catalog, _ rows: [Row]) -> some View {
        switch (s, catalog.recommended) {
        case (.recommended, .needsDownload?):
            KokoroPrepBanner(language: language, alwaysShowIdle: true, pill: true)
                .padding(.horizontal, 4)
                .padding(.vertical, 2)
        case (.recommended, .unsupported?):
            Text("这台设备带不动离线语音，可使用在线自然语音（免 Key）")
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
        default:
            ForEach(rows) { row in
                voiceRow(row).id(row.id)
            }
            if s == .mine { cloneAction }
            if rows.isEmpty, s != .mine {
                Text(query.isEmpty ? String(localized: "没有") : String(localized: "没有对得上的"))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 6)
            }
        }
    }

    private func voiceRow(_ row: Row) -> some View {
        let selected = selectedID == row.id
        let isHovered = hovered == row.id
        return HStack(spacing: 9) {
            Text(String(row.name.prefix(1)))
                .font(.yy(size: 11.5, weight: .semibold))
                .foregroundStyle(selected ? tint.accent : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(selected ? tint.accent.opacity(0.16) : YYColor.inset, in: .circle)
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 5) {
                    Text(row.name)
                        .font(.yy(size: 12.5, weight: selected ? .semibold : .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    if let badge = row.badge {
                        Text(badge)
                            .font(.yy(size: 9.5, weight: row.narrator ? .semibold : .medium))
                            .foregroundStyle(row.narrator ? tint.accent : YYColor.t2)
                            .padding(.horizontal, 5)
                            .frame(height: 15)
                            .background(row.narrator ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: 4))
                    }
                }
                if !row.detail.isEmpty {
                    Text(row.detail)
                        .font(.yy(size: 10.5))
                        .foregroundStyle(row.clone?.needsRerecord == true ? YYColor.warn : YYColor.t3)
                        .lineLimit(1)
                }
            }
            Spacer(minLength: 6)
            if selected {
                Image(systemName: "checkmark")
                    .font(.yy(size: 11, weight: .bold))
                    .foregroundStyle(tint.accent)
            }
            if let clone = row.clone {
                Button {
                    onOpenCloneDetail(clone.id)
                } label: {
                    Image(systemName: "ellipsis")
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 22, height: 22)
                        .background(isHovered ? YYColor.inset : .clear, in: .rect(cornerRadius: 6))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .help(Text("打开卡详情"))
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(
            selected ? tint.soft : (isHovered ? YYColor.t1.opacity(0.04) : .clear),
            in: .rect(cornerRadius: 9)
        )
        .contentShape(.rect)
        .onTapGesture { choose(row) }
        .onHover { inside in hover(row, inside) }
        .animation(YYMotion.quick, value: isHovered)
        .animation(YYMotion.quick, value: selected)
    }

    /// 跟旁白 · 现在旁白是沉舟，旁白换了宝玉也跟着换.
    private func followRow(_ f: Follow) -> some View {
        let isHovered = hovered == "+follow"
        return HStack(spacing: 9) {
            Text(String((f.narrator?.name ?? String(localized: "旁")).prefix(1)))
                .font(.yy(size: 11.5, weight: .semibold))
                .foregroundStyle(f.selected ? tint.accent : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(f.selected ? tint.accent.opacity(0.16) : YYColor.inset, in: .circle)
            VStack(alignment: .leading, spacing: 1) {
                Text(f.title)
                    .font(.yy(size: 12.5, weight: f.selected ? .semibold : .medium))
                    .foregroundStyle(YYColor.t1)
                Text(f.detail)
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
            Spacer(minLength: 6)
            if f.selected {
                Image(systemName: "checkmark")
                    .font(.yy(size: 11, weight: .bold))
                    .foregroundStyle(tint.accent)
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(f.selected ? tint.soft : (isHovered ? YYColor.t1.opacity(0.04) : .clear), in: .rect(cornerRadius: 9))
        .contentShape(.rect)
        .onTapGesture { hoverPreview?.cancel(); f.pick() }
        .onHover { inside in
            if inside {
                hovered = "+follow"
                hoverPreview?.cancel()
                guard let v = f.narrator else { return }
                hoverPreview = Task { @MainActor in
                    try? await Task.sleep(for: .milliseconds(450))
                    guard !Task.isCancelled, hovered == "+follow" else { return }
                    onPreview(v)
                }
            } else if hovered == "+follow" {
                hovered = nil
                hoverPreview?.cancel()
                Voices.stopPreview()
            }
        }
        .padding(.bottom, 2)
    }

    private var cloneAction: some View {
        HStack(spacing: 9) {
            Image(systemName: "plus")
                .font(.yy(size: 12, weight: .semibold))
                .foregroundStyle(tint.accent)
                .frame(width: 28, height: 28)
                .background(tint.accentSoft, in: .circle)
            VStack(alignment: .leading, spacing: 1) {
                Text("克隆一个声音")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(tint.accent)
                Text("录 10 秒自己的声音 · 不联网、不花钱")
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
            }
            Spacer()
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(hovered == "+clone" ? YYColor.t1.opacity(0.04) : .clear, in: .rect(cornerRadius: 9))
        .contentShape(.rect)
        .onTapGesture { onOpenClone(nil, "") }
        .onHover { hovered = $0 ? "+clone" : (hovered == "+clone" ? nil : hovered) }
    }

    // MARK: 下：怎么试听

    private var footer: some View {
        let failed = PreviewState.shared.failedID.flatMap { id in rowName(id) }
        return HStack {
            if let failed {
                Text("\(failed)没试听出来，换一个试试")
                    .foregroundStyle(YYColor.danger)
                    .lineLimit(1)
            } else {
                Text(sampleSource.map { String(localized: "试听：《\($0)》停着的那一句") } ?? String(localized: "试听：一句样张"))
                    .lineLimit(1)
            }
            Spacer()
            Text("指着试听 · 点一下换")
        }
        .font(.yy(size: 11))
        .foregroundStyle(YYColor.t2)
        .padding(.horizontal, 12)
        .padding(.vertical, 8)
        .background(YYColor.inset.opacity(0.35))
    }

    private func rowName(_ id: String) -> String? {
        Voices.find(id, bookID: bookID)?.name ?? ClonedVoices.entries().first { $0.id == id }?.name
    }

    // MARK: 指着试听 · 点一下换

    private func hover(_ row: Row, _ inside: Bool) {
        if inside {
            hovered = row.id
            hoverPreview?.cancel()
            guard let voice = row.voice else { return }
            hoverPreview = Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(450))
                guard !Task.isCancelled, hovered == row.id else { return }
                onPreview(voice)
            }
        } else if hovered == row.id {
            hovered = nil
            hoverPreview?.cancel()
            Voices.stopPreview()
        }
    }

    private func choose(_ row: Row) {
        hoverPreview?.cancel()
        if let clone = row.clone {
            if clone.needsRerecord { return onOpenClone(clone.id, clone.name) }
            guard let voice = row.voice else { return onOpenCloneDetail(clone.id) }
            return onSelect(voice)
        }
        if let voice = row.voice { onSelect(voice) }
    }

    // MARK: 数据

    private func sections(_ catalog: Voices.Catalog) -> [Section] {
        Section.allCases.filter { $0 != .recommended || catalog.recommended != nil }
    }

    private func title(_ s: Section) -> String {
        switch s {
        case .recommended: String(localized: "推荐")
        case .mine: String(localized: "我的")
        case .cloud: String(localized: "云端")
        case .system: String(localized: "系统自带")
        }
    }

    private func rows(_ catalog: Voices.Catalog) -> [Section: [Row]] {
        var out: [Section: [Row]] = [:]
        if case let .ready(voices) = catalog.recommended {
            let defaultID = KokoroModel.defaultNarrator(for: language)?.id ?? VoicePack.defaultNarrator(for: language)?.id
            out[.recommended] = voices.map { v in
                let isDefault = v.id == defaultID
                if let k = v as? KokoroVoice {
                    return Row(
                        id: k.id,
                        name: k.name,
                        badge: isDefault ? String(localized: "默认旁白") : (k.speaker.narrator ? String(localized: "推荐旁白") : nil),
                        narrator: isDefault,
                        detail: k.summaryLine,
                        female: Self.female(k.speaker.gender),
                        voice: k,
                        clone: nil
                    )
                } else if let c = v as? CardVoice {
                    return Row(
                        id: c.id,
                        name: c.name,
                        badge: isDefault ? String(localized: "默认旁白") : (c.card.narrator ? String(localized: "角色旁白") : nil),
                        narrator: isDefault,
                        detail: c.summaryLine,
                        female: Self.female(c.card.gender),
                        voice: c,
                        clone: nil
                    )
                } else {
                    return Row(id: v.id, name: v.name, badge: v.qualityName, narrator: isDefault, detail: "", female: nil, voice: v, clone: nil)
                }
            }
        }
        var mine: [Row] = catalog.clones.map { e in
            let resolved = e.isPlayable ? ClonedVoices.resolveVoice(e.id, bookID: bookID) : nil
            return Row(id: e.id, name: e.name, badge: String(localized: "克隆"), narrator: false,
                       detail: e.subtitle, female: e.card.flatMap { Self.female($0.gender) }, voice: resolved, clone: e)
        }
        mine += catalog.bookCards.filter { $0.speaks(language) }.map { v in
            Row(id: v.id, name: v.name, badge: String(localized: "本书角色"), narrator: false,
                detail: VoiceCatalogSections.roleLine(v, bookTitle: sampleSource), female: Self.female(v.card.gender), voice: v, clone: nil)
        }
        out[.mine] = mine
        out[.cloud] = catalog.cloud.map { v in
            Row(id: v.id, name: v.name, badge: v.qualityName, narrator: false,
                detail: String(localized: "云端（已读内容本地缓存）"), female: nil, voice: v, clone: nil)
        }
        out[.system] = catalog.system.map { v in
            Row(id: v.id, name: v.name, badge: v.qualityName ?? String(localized: "不推荐"), narrator: false,
                detail: v.listLine,
                female: v.gender == .female ? true : (v.gender == .male ? false : nil), voice: v, clone: nil)
        }
        if let usedByLabel {
            out = out.mapValues { rows in
                rows.map { r in
                    guard let used = usedByLabel(r.id) else { return r }
                    return Row(id: r.id, name: r.name, badge: r.badge, narrator: r.narrator,
                               detail: used + " · " + r.detail, female: r.female, voice: r.voice, clone: r.clone)
                }
            }
        }
        let q = query.trimmingCharacters(in: .whitespaces).lowercased()
        return out.mapValues { rows in
            rows.filter { row in
                let sexOK: Bool = switch sex {
                case .all: true
                case .female: row.female == true
                case .male: row.female == false
                }
                let textOK = q.isEmpty
                    || row.name.lowercased().contains(q)
                    || row.detail.lowercased().contains(q)
                    || (row.badge?.lowercased().contains(q) ?? false)
                return sexOK && textOK
            }
        }
    }

    private static func female(_ g: CharacterGender) -> Bool? {
        switch g {
        case .f: true
        case .m: false
        case .unknown: nil
        }
    }
}
#endif
