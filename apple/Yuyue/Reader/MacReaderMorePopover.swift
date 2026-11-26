#if os(macOS)
import SwiftUI
import YuyueCore

/// macOS reader brightness & eye-care preference keys (shared with iOS; on macOS drives soft window overlays).
enum MacReaderBrightness {
    static let followKey = "reader.brightnessFollowSystem"
    static let valueKey = "reader.brightness"
    static let eyeCareKey = "reader.eyeCare"
}

/// The macOS reader window's top-right `···` popover (304pt wide, matching TypesetCard):
/// 1. Display: 3-paper segmented strip + mobile-styled screen brightness bar
/// 2. Reading controls: page-turn animation, page-turn sound, Focus Reading (⌃⌘F), and Dialogue Bubbles
/// 3. Book tools (deduplicated from the toolbar): Replace Rules, Export Notes, Book Info (⌘I), TXT Encoding
struct MacReaderMorePopover: View {
    @Bindable var model: ReaderModel
    let openInfo: () -> Void
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage(MacReaderBrightness.followKey) private var followSystem = true
    @AppStorage(MacReaderBrightness.valueKey) private var readerBrightness = 0.72
    @AppStorage(MacReaderBrightness.eyeCareKey) private var eyeCare = false
    @AppStorage(PageTurnPrefs.key) private var pageTurnRaw = PageTurn.slide.rawValue
    @AppStorage(PageTurnPrefs.soundKey) private var pageTurnSound = false
    @AppStorage(PageTurnPrefs.soundStyleKey) private var pageTurnSoundStyleRaw = PageTurnSoundStyle.softPaper.rawValue
    @AppStorage(EInk.key) private var einkOn = false
    @State private var activeRuleCount = 0

    private var dark: Bool { scheme == .dark }
    private var format: BookFormat? { model.detail?.summary.format }
    private var isPdf: Bool { format == .pdf }
    private var isTxt: Bool { format == .txt }
    private var resolvedPaper: PaperAppearance.Resolved { PaperAppearance.shared.resolve(model.settings) }

    // Pure paper-and-ink desktop palette aligned with TypesetCard & finalized prototype.
    private var cardBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#3A3C43") : Color(hex: "#FFFFFF")) }
    private var insetBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#27292E") : Color(hex: "#ECE8E0")) }
    private var lineColor: Color { einkOn ? YYColor.t1 : (dark ? Color.white.opacity(0.08) : Color.black.opacity(0.075)) }
    private var ink: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#EDEDEE") : Color(hex: "#262523")) }
    private var ink2: Color { einkOn ? YYColor.t2 : (dark ? Color(hex: "#9A9895") : Color(hex: "#736E67")) }
    private var ink3: Color { einkOn ? YYColor.t3 : (dark ? Color(hex: "#6E6E73") : Color(hex: "#9C968E")) }
    private var rowHover: Color { einkOn ? YYColor.inset : (dark ? Color.white.opacity(0.07) : Color.black.opacity(0.048)) }
    private var switchOnTrack: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#E6E4DF") : Color(hex: "#3B3936")) }
    private var switchOnKnob: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#1C1D20") : Color(hex: "#FFFFFF")) }
    private var switchOffTrack: Color { einkOn ? YYColor.line : (dark ? Color(hex: "#3A3C44") : Color(hex: "#DBD6CC")) }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // Section 1: Display (Day/Night + Brightness + Eye-Care Warmth)
            VStack(spacing: 10) {
                if !einkOn { paperStrip }
                brightnessSection
                if !einkOn {
                    MacPopoverToggleRow(
                        icon: "eye",
                        title: String(localized: "护眼柔光"),
                        shortcut: nil,
                        isOn: eyeCare,
                        ink: ink,
                        ink2: ink2,
                        ink3: ink3,
                        hoverBg: rowHover,
                        onTrack: switchOnTrack,
                        onKnob: switchOnKnob,
                        offTrack: switchOffTrack
                    ) {
                        eyeCare.toggle()
                    }
                    .padding(.top, -2)
                }
            }
            .padding(.bottom, 8)

            divider

            // Section 2: Reading Modes & Switches (deduplicated from toolbar)
            VStack(spacing: 4) {
                if model.settings.mode == .paginated, !einkOn {
                    pageTurnRow
                    MacPopoverToggleRow(
                        icon: pageTurnSound ? "speaker.wave.2" : "speaker.slash",
                        title: String(localized: "翻页音效"),
                        shortcut: nil,
                        isOn: pageTurnSound,
                        ink: ink,
                        ink2: ink2,
                        ink3: ink3,
                        hoverBg: rowHover,
                        onTrack: switchOnTrack,
                        onKnob: switchOnKnob,
                        offTrack: switchOffTrack
                    ) {
                        let next = !pageTurnSound
                        pageTurnSound = next
                        if next { PageTurnSound.shared.previewSelection(pageTurnSoundStyleRaw) }
                    }
                    if pageTurnSound {
                        PageTurnSoundStyleGrid(
                            selectedRaw: $pageTurnSoundStyleRaw,
                            showSubtitle: false,
                            compact: true,
                            cardBg: cardBg,
                            insetBg: insetBg,
                            ink: ink,
                            ink2: ink2,
                            borderColor: lineColor,
                            eink: einkOn
                        )
                        .padding(.horizontal, 8)
                        .padding(.vertical, 4)
                    }
                }
                MacPopoverToggleRow(
                    icon: "viewfinder",
                    title: String(localized: "沉浸阅读"),
                    shortcut: "⌃⌘F",
                    isOn: model.immersive,
                    ink: ink,
                    ink2: ink2,
                    ink3: ink3,
                    hoverBg: rowHover,
                    onTrack: switchOnTrack,
                    onKnob: switchOnKnob,
                    offTrack: switchOffTrack
                ) {
                    let next = !model.immersive
                    if next { dismiss() }
                    model.immersive = next
                }
                if model.hasDialogue {
                    MacPopoverToggleRow(
                        icon: "bubble.left.and.bubble.right",
                        title: String(localized: "对话气泡"),
                        shortcut: nil,
                        isOn: model.bubbles,
                        ink: ink,
                        ink2: ink2,
                        ink3: ink3,
                        hoverBg: rowHover,
                        onTrack: switchOnTrack,
                        onKnob: switchOnKnob,
                        offTrack: switchOffTrack
                    ) {
                        model.bubbles.toggle()
                    }
                }
            }
            .padding(.vertical, 6)

            divider

            // Section 3: Book Tools (deduplicated from toolbar)
            actionRows
                .padding(.top, 4)
        }
        .frame(width: 280)
        .padding(12)
        .task(id: model.bookID) {
            guard !isPdf else { return }
            let mine = (try? model.core.replaceRules(book: model.bookID)) ?? []
            let shared = (try? model.core.replaceRules(book: nil)) ?? []
            activeRuleCount = mine.filter(\.enabled).count + shared.filter(\.enabled).count
        }
    }

    private var divider: some View {
        Rectangle()
            .fill(lineColor)
            .frame(height: 0.5)
            .padding(.horizontal, -2)
            .padding(.vertical, 2)
    }

    // MARK: 1. Paper & Brightness

    private var paperStrip: some View {
        let isDark = resolvedPaper.isDark
        let items: [(String, String, Bool)] = [
            (String(localized: "日间"), "sun.max", false),
            (String(localized: "夜间"), "moon", true),
        ]
        return HStack(spacing: 4) {
            ForEach(items, id: \.2) { label, icon, wantDark in
                let on = isDark == wantDark
                Button {
                    if isDark != wantDark {
                        PaperAppearance.shared.togglePolarity(in: model.settings) { model.update($0) }
                    }
                } label: {
                    HStack(spacing: 6) {
                        Image(systemName: icon)
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(on ? ink : ink2)
                        Text(label)
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(on ? ink : ink2)
                            .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity)
                    .frame(height: 26)
                    .background(on ? cardBg : .clear, in: RoundedRectangle(cornerRadius: 7.5, style: .continuous))
                    .shadow(color: on ? .black.opacity(0.09) : .clear, radius: 2, y: 1)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(3)
        .background(insetBg, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
    }

    private var brightnessSection: some View {
        let clamped = min(max(readerBrightness, 0.35), 1.0)
        let displayFraction = followSystem ? 0.78 : clamped
        let pct = Int((clamped * 100).rounded())
        return VStack(spacing: 6) {
            HStack {
                Text("屏幕亮度")
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(ink2)
                Spacer()
                Button {
                    followSystem.toggle()
                } label: {
                    HStack(spacing: 6) {
                        Text("跟随系统")
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(followSystem ? ink : ink2)
                        MacMoreMiniSwitch(
                            isOn: followSystem,
                            onTrack: switchOnTrack,
                            onKnob: switchOnKnob,
                            offTrack: switchOffTrack
                        )
                    }
                    .padding(.vertical, 2)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, 2)

            GeometryReader { g in
                let innerWidth = max(0, g.size.width - 4)
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 13, style: .continuous)
                        .fill(insetBg)
                    RoundedRectangle(cornerRadius: 10.5, style: .continuous)
                        .fill(cardBg)
                        .opacity(followSystem ? 0.58 : 0.96)
                        .shadow(color: einkOn ? .clear : .black.opacity(0.06), radius: 2, y: 1)
                        .frame(width: max(20, innerWidth * displayFraction))
                        .padding(2)
                    HStack(spacing: 8) {
                        Image(systemName: "sun.max")
                            .font(.yy(size: 14, weight: .medium))
                            .foregroundStyle(ink2)
                        Text(followSystem ? String(localized: "跟随系统") : "\(pct)%")
                            .font(.yy(size: 12, weight: .semibold))
                            .monospacedDigit()
                            .foregroundStyle(ink)
                    }
                    .padding(.horizontal, 12)
                    .allowsHitTesting(false)
                }
                .clipShape(RoundedRectangle(cornerRadius: 13, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 13, style: .continuous).strokeBorder(lineColor, lineWidth: einkOn ? 1 : 0.5))
                .contentShape(.rect)
                .gesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { value in
                            let ratio = min(max(value.location.x / max(1, g.size.width), 0), 1)
                            if followSystem { followSystem = false }
                            readerBrightness = min(max(0.35 + ratio * 0.65, 0.35), 1.0)
                        }
                )
            }
            .frame(height: 40)
            .help("拖动微调阅读窗口柔光亮度")
        }
    }

    // MARK: 2. Page Turn Segmented Row

    private var pageTurnRow: some View {
        let current = PageTurn.of(pageTurnRaw)
        let turns: [(PageTurn, String)] = [
            (.slide, String(localized: "滑动")),
            (.cover, String(localized: "覆盖")),
            (.none, String(localized: "无")),
        ]
        return HStack(spacing: 8) {
            Image(systemName: "book.pages")
                .font(.yy(size: 13, weight: .medium))
                .foregroundStyle(ink2)
                .frame(width: 16)
            Text("翻页动画")
                .font(.yy(size: 12.5, weight: .medium))
                .foregroundStyle(ink)
            Spacer(minLength: 4)
            HStack(spacing: 2) {
                ForEach(turns, id: \.0.rawValue) { pt, label in
                    let on = current == pt
                    Button {
                        pageTurnRaw = pt.rawValue
                        model.pager.pageTurn = pt
                    } label: {
                        Text(label)
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(on ? ink : ink2)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity)
                            .frame(height: 22)
                            .background(on ? cardBg : .clear, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                            .shadow(color: on ? .black.opacity(0.08) : .clear, radius: 1.5, y: 1)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(2)
            .frame(width: 168)
            .background(insetBg, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        }
        .padding(.horizontal, 8)
        .frame(height: 32)
    }

    // MARK: 3. Book Tools

    private var actionRows: some View {
        let pctRead = min(max(Int((model.bookFraction * 100).rounded()), 0), 100)
        let formatName: String = switch format {
        case .epub: "EPUB"
        case .txt: "TXT"
        case .pdf: "PDF"
        case .podcast: "PODCAST"
        default: "BOOK"
        }
        return VStack(spacing: 2) {
            if model.hasDialogue {
                MacPopoverActionRow(
                    asset: "cast",
                    iconColor: ink2,
                    title: String(localized: "广播剧"),
                    ink: ink,
                    hoverBg: rowHover
                ) {
                    HStack(spacing: 4) {
                        Text("人物 · 配音")
                            .font(.yy(size: 11))
                            .foregroundStyle(ink3)
                        Image(systemName: "chevron.right")
                            .font(.yy(size: 9.5, weight: .semibold))
                            .foregroundStyle(ink3)
                    }
                } action: {
                    dismiss()
                    model.panel = model.panel == .cast ? nil : .cast
                }
            }

            if !isPdf {
                MacPopoverActionRow(
                    icon: "arrow.left.arrow.right",
                    iconColor: ink2,
                    title: String(localized: "替换规则"),
                    ink: ink,
                    hoverBg: rowHover
                ) {
                    HStack(spacing: 4) {
                        Text(activeRuleCount > 0 ? String(localized: "\(activeRuleCount) 条生效") : String(localized: "去广告 · 读音"))
                            .font(.yy(size: 11))
                            .foregroundStyle(ink3)
                        Image(systemName: "chevron.right")
                            .font(.yy(size: 9.5, weight: .semibold))
                            .foregroundStyle(ink3)
                    }
                } action: {
                    dismiss()
                    model.rulesFromSettings = false
                    model.rulesShown = true
                }
            }

            NoteExportMenu(
                sender: model.noteSender,
                markdown: notesMarkdown,
                name: String(localized: "\(model.detail?.summary.title ?? "") 笔记.md"),
                books: { exportBook.map { [$0] } ?? [] },
                book: model.detail?.summary,
                fullWidth: true
            ) {
                MacPopoverRowChrome(icon: "square.and.arrow.up", iconColor: ink2, title: String(localized: "导出笔记"), ink: ink, hoverBg: rowHover) {
                    HStack(spacing: 4) {
                        Text("MD · 分享")
                            .font(.yy(size: 11))
                            .foregroundStyle(ink3)
                        Image(systemName: "chevron.right")
                            .font(.yy(size: 9.5, weight: .semibold))
                            .foregroundStyle(ink3)
                    }
                }
                .frame(width: 280)
            }

            MacPopoverActionRow(
                icon: "info.circle",
                iconColor: ink2,
                title: String(localized: "书的信息"),
                ink: ink,
                hoverBg: rowHover
            ) {
                HStack(spacing: 6) {
                    Text("\(formatName) · \(pctRead)%")
                        .font(.yy(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(ink3)
                    Text("⌘I")
                        .font(.yy(size: 10.5))
                        .foregroundStyle(ink3)
                }
            } action: {
                dismiss()
                openInfo()
            }

            if isTxt {
                MacPopoverActionRow(
                    icon: "doc.text.magnifyingglass",
                    iconColor: ink2,
                    title: String(localized: "编码和目录"),
                    ink: ink,
                    hoverBg: rowHover
                ) {
                    HStack(spacing: 4) {
                        Text("乱码 · 章节")
                            .font(.yy(size: 11))
                            .foregroundStyle(ink3)
                        Image(systemName: "chevron.right")
                            .font(.yy(size: 9.5, weight: .semibold))
                            .foregroundStyle(ink3)
                    }
                } action: {
                    dismiss()
                    model.txtShown = true
                }
            }
        }
    }

    private var exportBook: NoteExport.Book? {
        guard let book = model.detail?.summary, !model.marks.isEmpty else { return nil }
        var chapters: [(String, [Mark])] = []
        for m in model.marks {
            let title = model.detail?.chapterTitle(for: m.section) ?? book.title
            if chapters.last?.0 == title { chapters[chapters.count - 1].1.append(m) } else { chapters.append((title, [m])) }
        }
        return NoteExport.Book(id: book.id, title: book.title, author: book.author, chapters: chapters)
    }

    private var notesMarkdown: String {
        var out = String(localized: "# 《\(model.detail?.summary.title ?? "")》笔记\n")
        for (chapter, list) in exportBook?.chapters ?? [] {
            out += "\n## \(chapter)\n"
            for m in list {
                out += "\n> \(m.text)\n"
                if let note = m.note { out += "\n\(note)\n" }
            }
        }
        return out
    }
}

private struct MacMoreMiniSwitch: View {
    let isOn: Bool
    let onTrack: Color
    let onKnob: Color
    let offTrack: Color

    var body: some View {
        ZStack(alignment: isOn ? .trailing : .leading) {
            Capsule()
                .fill(isOn ? onTrack : offTrack)
            Circle()
                .fill(isOn ? onKnob : .white)
                .shadow(color: .black.opacity(0.14), radius: 1, y: 0.5)
                .frame(width: 13, height: 13)
                .padding(2)
        }
        .frame(width: 30, height: 17)
        .animation(YYMotion.quick, value: isOn)
    }
}

private struct MacPopoverToggleRow: View {
    let icon: String
    let title: String
    let shortcut: String?
    let isOn: Bool
    let ink: Color
    let ink2: Color
    let ink3: Color
    let hoverBg: Color
    let onTrack: Color
    let onKnob: Color
    let offTrack: Color
    let action: () -> Void

    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 10) {
                Image(systemName: icon)
                    .font(.yy(size: 13, weight: .medium))
                    .foregroundStyle(ink2)
                    .frame(width: 16)
                Text(title)
                    .font(.yy(size: 12.5, weight: .medium))
                    .foregroundStyle(ink)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if let shortcut {
                    Text(shortcut)
                        .font(.yy(size: 10.5))
                        .foregroundStyle(ink3)
                        .padding(.trailing, 2)
                }
                MacMoreMiniSwitch(isOn: isOn, onTrack: onTrack, onKnob: onKnob, offTrack: offTrack)
            }
            .padding(.horizontal, 8)
            .frame(height: 32)
            .background(hovered ? hoverBg : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }
}

private struct MacPopoverActionRow<Trailing: View>: View {
    var icon: String? = nil
    var asset: String? = nil
    let iconColor: Color
    let title: String
    let ink: Color
    let hoverBg: Color
    @ViewBuilder let trailing: () -> Trailing
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            MacPopoverRowChrome(icon: icon, asset: asset, iconColor: iconColor, title: title, ink: ink, hoverBg: hoverBg, trailing: trailing)
        }
        .buttonStyle(.plain)
    }
}

private struct MacPopoverRowChrome<Trailing: View>: View {
    var icon: String? = nil
    var asset: String? = nil
    let iconColor: Color
    let title: String
    let ink: Color
    let hoverBg: Color
    @ViewBuilder let trailing: () -> Trailing

    @State private var hovered = false

    var body: some View {
        HStack(spacing: 10) {
            Group {
                if let asset {
                    Image(asset)
                        .renderingMode(.template)
                        .resizable()
                        .scaledToFit()
                        .frame(width: 14.5, height: 14.5)
                } else if let icon {
                    Image(systemName: icon)
                        .font(.yy(size: 13, weight: .medium))
                }
            }
            .foregroundStyle(iconColor)
            .frame(width: 16)
            Text(title)
                .font(.yy(size: 12.5, weight: .medium))
                .foregroundStyle(ink)
                .lineLimit(1)
            Spacer(minLength: 4)
            trailing()
        }
        .padding(.horizontal, 8)
        .frame(height: 32)
        .background(hovered ? hoverBg : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
        .contentShape(.rect)
        .onHover { hovered = $0 }
    }
}

/// Compact 3-row macOS popover anchored to the toolbar `译` button:
/// 原文 / 双语对照 / 仅译文, each with a crisp icon and active checkmark.
struct MacReaderTranslatePopover: View {
    @Bindable var model: ReaderModel
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(EInk.key) private var einkOn = false

    private var dark: Bool { scheme == .dark }
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var ink: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#EDEDEE") : Color(hex: "#262523")) }
    private var ink2: Color { einkOn ? YYColor.t2 : (dark ? Color(hex: "#9A9895") : Color(hex: "#736E67")) }
    private var accent: Color { einkOn ? YYColor.t1 : (dark ? tint.paperAccentDark : tint.paperAccentLight) }
    private var rowHover: Color { einkOn ? YYColor.inset : (dark ? Color.white.opacity(0.07) : Color.black.opacity(0.048)) }

    var body: some View {
        let items: [(TranslateMode, String, String)] = [
            (.off, "doc.plaintext", String(localized: "原文")),
            (.bilingual, "rectangle.split.1x2", String(localized: "双语对照")),
            (.only, "translate", String(localized: "仅译文")),
        ]
        VStack(spacing: 2) {
            ForEach(items, id: \.0.rawValue) { mode, icon, title in
                let on = model.translateMode == mode
                MacTranslateOptionRow(
                    icon: icon,
                    title: title,
                    selected: on,
                    ink: ink,
                    ink2: ink2,
                    accent: accent,
                    hoverBg: rowHover
                ) {
                    model.setTranslateMode(mode)
                    dismiss()
                }
            }
        }
        .padding(6)
        .frame(width: 162)
    }
}

private struct MacTranslateOptionRow: View {
    let icon: String
    let title: String
    let selected: Bool
    let ink: Color
    let ink2: Color
    let accent: Color
    let hoverBg: Color
    let action: () -> Void

    @State private var hovered = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: icon)
                    .font(.yy(size: 13, weight: .medium))
                    .foregroundStyle(selected ? accent : ink2)
                    .frame(width: 16)
                Text(title)
                    .font(.yy(size: 12.5, weight: selected ? .semibold : .medium))
                    .foregroundStyle(ink)
                    .lineLimit(1)
                Spacer(minLength: 6)
                Image(systemName: "checkmark")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(accent)
                    .opacity(selected ? 1 : 0)
            }
            .padding(.horizontal, 10)
            .frame(height: 32)
            .background(hovered ? hoverBg : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovered = $0 }
    }
}
#endif
