#if os(iOS)
import SwiftUI
import YuyueCore

/// The reader's top-right `···` quick control bottom sheet:
/// 1. Screen brightness light-bar with a slim "Follow System" micro-switch header + 3 quick paper cards
/// 2. 2×2 core stage (left: compact charcoal toggle switches for Focus & Bilingual; right: AI Companion & Book Notes)
/// 3. Page turn animation strip + sound toggle
/// 4. Bottom 3-card (4-card for TXT) bookstore tool row (Replace Rules, Export Notes, Book Info, Encoding)
struct PhoneReaderMoreSheet: View {
    @Bindable var model: ReaderModel
    let openNotes: () -> Void
    let openInfo: () -> Void
    let openImmersiveSettings: () -> Void
    let openCompanion: () -> Void
    let openRules: () -> Void
    let openTxtLayout: () -> Void
    let exportMarkdown: () -> Void
    let dismiss: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage(ReaderBrightness.followKey) private var followSystem = true
    @AppStorage(ReaderBrightness.valueKey) private var readerBrightness = 0.6
    @AppStorage(ReaderBrightness.eyeCareKey) private var eyeCare = false
    @AppStorage(PageTurnPrefs.key) private var pageTurnRaw = PageTurn.slide.rawValue
    @AppStorage(PageTurnPrefs.soundKey) private var pageTurnSound = false
    @AppStorage(EInk.key) private var einkOn = false
    @State private var activeRuleCount = 0
    @State private var measuredHeight: CGFloat = 0

    private var dark: Bool { scheme == .dark }
    private var format: BookFormat? { model.detail?.summary.format }
    private var isPdf: Bool { format == .pdf }
    private var isTxt: Bool { format == .txt }
    private var showTranslate: Bool { !isPdf && format != .podcast }
    private var showTurn: Bool { model.settings.mode == .paginated && !einkOn }
    private var resolvedPaper: PaperAppearance.Resolved { PaperAppearance.shared.resolve(model.settings) }
    private var estimatedHeight: CGFloat {
        if einkOn {
            return 350 + (showTranslate ? 54 : 0)
        }
        return 456 + (showTranslate ? 54 : 0) + (showTurn ? 56 : 0)
    }
    private var sheetHeight: CGFloat {
        measuredHeight > 0 ? measuredHeight : estimatedHeight
    }

    // Palette tuned to warm paper-and-ink clarity matching the finalized prototype.
    private var sheetBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#1C1D20") : Color(hex: "#F4F1EA")) }
    private var cardBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#272930") : Color(hex: "#FFFFFF")) }
    private var insetBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#2F3137") : Color(hex: "#ECE8DF")) }
    private var recessedBg: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#212227") : Color(hex: "#E9E4DB")) }
    private var lightTrackBg: Color { einkOn ? YYColor.inset : (dark ? Color(hex: "#2C2E34") : Color(hex: "#E8E3D8")) }
    private var lightFillBg: Color { einkOn ? YYColor.card : (dark ? Color(hex: "#3A3D45") : Color(hex: "#FFFFFF")) }
    private var borderColor: Color { einkOn ? YYColor.t1 : (dark ? Color.white.opacity(0.08) : Color.black.opacity(0.075)) }
    private var ink: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#EDEDEE") : Color(hex: "#262523")) }
    private var ink2: Color { einkOn ? YYColor.t2 : (dark ? Color(hex: "#9A9895") : Color(hex: "#736E67")) }
    private var switchOnTrack: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#E6E4DF") : Color(hex: "#3B3936")) }
    private var switchOnKnob: Color { einkOn ? YYColor.bg : (dark ? Color(hex: "#1C1D20") : Color(hex: "#FFFFFF")) }
    private var switchOffTrack: Color { einkOn ? YYColor.line : (dark ? Color(hex: "#363840") : Color(hex: "#DDD8CE")) }
    private var iconActiveBg: Color { einkOn ? YYColor.inset : (dark ? Color(hex: "#343740") : Color(hex: "#F0ECE4")) }
    private var companionIconBg: Color { einkOn ? YYColor.inset : (dark ? Color(hex: "#303137") : Color(hex: "#F2EEE8")) }
    private var companionIconFg: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#D1C4B2") : Color(hex: "#6B5A46")) }
    private var notesNumFg: Color { einkOn ? YYColor.t1 : (dark ? Color(hex: "#E2E0DA") : Color(hex: "#2F3339")) }
    private var strokeWidth: CGFloat { einkOn ? 1 : 0.5 }

    var body: some View {
        ScrollView(.vertical, showsIndicators: false) {
            VStack(spacing: 10) {
                // 1. Brightness micro-header + full-width luminous light-bar + Day/Night + Eye-Care Warmth
                VStack(spacing: 8) {
                    brightnessSection
                    if !einkOn {
                        paperRow
                        eyeCareRow
                    }
                }

                // 2. Core switches (Focus Reading + Dialogue Bubbles) & Action entries (AI Companion + Book Notes)
                coreStage

                // 3. Translation 3-mode strip (原文 / 对照 / 仅译文) + Page Turn strip
                if showTranslate {
                    translationStrip
                }
                if showTurn {
                    pageTurnStrip
                }

                // 4. Bottom 3-Card (4-Card for TXT) Bookstore Tool Footer
                footerRow
            }
            .padding(.horizontal, 16)
            .padding(.top, YYUI.sheetGrabberTop)
            .padding(.bottom, 18)
            .frame(maxWidth: .infinity)
            .onGeometryChange(for: CGFloat.self, of: \.size.height) { h in
                let rounded = ceil(h)
                if rounded > 0, abs(rounded - measuredHeight) > 0.5 {
                    measuredHeight = rounded
                }
            }
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(sheetBg.ignoresSafeArea())
        .presentationDetents([.height(sheetHeight)])
        .presentationDragIndicator(.visible)
        .presentationBackground(sheetBg)
        .presentationBackgroundInteraction(.enabled)
        .task(id: model.bookID) {
            guard !isPdf else { return }
            let mine = (try? model.core.replaceRules(book: model.bookID)) ?? []
            let shared = (try? model.core.replaceRules(book: nil)) ?? []
            activeRuleCount = mine.filter(\.enabled).count + shared.filter(\.enabled).count
        }
    }

    // MARK: 1. Brightness & Papers

    private var brightnessSection: some View {
        VStack(spacing: 6) {
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
                        MoreMiniSwitch(
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
                .accessibilityLabel(Text("亮度跟随系统"))
                .accessibilityValue(Text(followSystem ? "已开启" : "已关闭"))
            }
            .padding(.horizontal, 4)

            // Full-width 48pt horizontal light-bar
            let displayFraction = min(max(followSystem ? 0.78 : readerBrightness, 0.05), 1.0)
            let pct = Int((min(max(readerBrightness, 0.05), 1.0) * 100).rounded())
            GeometryReader { g in
                let innerWidth = max(0, g.size.width - 4)
                ZStack(alignment: .leading) {
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .fill(lightTrackBg)
                    RoundedRectangle(cornerRadius: 13, style: .continuous)
                        .fill(lightFillBg)
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
                    .padding(.horizontal, 14)
                    .allowsHitTesting(false)
                }
                .clipShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
                .contentShape(.rect)
                .highPriorityGesture(
                    DragGesture(minimumDistance: 0)
                        .onChanged { value in
                            let ratio = min(max(value.location.x / max(1, g.size.width), 0), 1)
                            if followSystem { followSystem = false }
                            readerBrightness = min(max(0.05 + ratio * 0.95, 0.05), 1.0)
                        }
                )
            }
            .frame(height: 48)
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(Text("屏幕亮度"))
            .accessibilityValue(Text(followSystem ? String(localized: "跟随系统") : "\(pct)%"))
            .accessibilityAdjustableAction { direction in
                followSystem = false
                switch direction {
                case .increment: readerBrightness = min(1.0, readerBrightness + 0.1)
                case .decrement: readerBrightness = max(0.05, readerBrightness - 0.1)
                @unknown default: break
                }
            }
        }
    }

    private var paperRow: some View {
        let isDark = resolvedPaper.isDark
        let items: [(String, String, Bool)] = [
            (String(localized: "日间"), "sun.max", false),
            (String(localized: "夜间"), "moon", true),
        ]
        return HStack(spacing: 8) {
            ForEach(items, id: \.2) { label, icon, wantDark in
                let selected = isDark == wantDark
                Button {
                    if isDark != wantDark {
                        PaperAppearance.shared.togglePolarity(in: model.settings) { model.update($0) }
                    }
                } label: {
                    HStack(spacing: 7) {
                        Image(systemName: icon)
                            .font(.yy(size: 14, weight: .medium))
                            .foregroundStyle(selected ? ink : ink2)
                        Text(label)
                            .font(.yy(size: 12.5, weight: .semibold))
                            .foregroundStyle(selected ? ink : ink2)
                            .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity)
                    .frame(height: 42)
                    .background(selected ? cardBg : recessedBg, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 14, style: .continuous)
                            .strokeBorder(selected ? borderColor : .clear, lineWidth: 0.5)
                    )
                    .shadow(color: selected ? .black.opacity(0.08) : .clear, radius: 4, y: 2)
                }
                .buttonStyle(YYPressStyle(scale: 0.96))
                .accessibilityAddTraits(selected ? .isSelected : [])
            }
        }
    }

    private var eyeCareRow: some View {
        Button {
            eyeCare.toggle()
        } label: {
            HStack(spacing: 10) {
                Image(systemName: "eye")
                    .font(.yy(size: 14, weight: .medium))
                    .foregroundStyle(eyeCare ? ink : ink2)
                    .frame(width: 20)
                VStack(alignment: .leading, spacing: 1) {
                    Text("护眼柔光")
                        .font(.yy(size: 12.5, weight: .semibold))
                        .foregroundStyle(ink)
                    Text("暖色滤蓝光")
                        .font(.yy(size: 10.5))
                        .foregroundStyle(ink2)
                }
                Spacer(minLength: 8)
                MoreMiniSwitch(
                    isOn: eyeCare,
                    onTrack: switchOnTrack,
                    onKnob: switchOnKnob,
                    offTrack: switchOffTrack
                )
            }
            .padding(.horizontal, 12)
            .frame(height: 44)
            .background(cardBg, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.98))
    }

    // MARK: 2. Core Switches & Entries

    private var coreStage: some View {
        VStack(spacing: 8) {
            // Row 1: Reading Mode Switches (Focus Reading + Dialogue Bubbles when available)
            HStack(spacing: 8) {
                switchCard(
                    icon: "viewfinder",
                    title: String(localized: "沉浸阅读"),
                    subtitle: model.immersive ? String(localized: "已开启") : String(localized: "无打扰模式"),
                    isOn: model.immersive,
                    onTap: {
                        model.chromeShown = false
                        model.immersive.toggle()
                    },
                    onLongPress: {
                        dismiss()
                        openImmersiveSettings()
                    }
                )
                if model.hasDialogue {
                    switchCard(
                        icon: "bubble.left.and.bubble.right",
                        title: String(localized: "对话气泡"),
                        subtitle: model.bubbles ? String(localized: "已开启") : String(localized: "聊天气泡"),
                        isOn: model.bubbles,
                        onTap: {
                            model.bubbles.toggle()
                        },
                        onLongPress: nil
                    )
                }
            }

            // Row 2: Book Entries (AI Companion + Book Notes)
            HStack(spacing: 8) {
                if !isPdf {
                    Button {
                        dismiss()
                        openCompanion()
                    } label: {
                        HStack(spacing: 9) {
                            Image(systemName: "sparkle")
                                .font(.yy(size: 14, weight: .medium))
                                .foregroundStyle(companionIconFg)
                                .frame(width: 32, height: 32)
                                .background(companionIconBg, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                            VStack(alignment: .leading, spacing: 1) {
                                Text("AI 书伴")
                                    .font(.yy(size: 12, weight: .semibold))
                                    .foregroundStyle(ink)
                                    .lineLimit(1)
                                Text("总结 · 追问")
                                    .font(.yy(size: 10.5))
                                    .foregroundStyle(ink2)
                                    .lineLimit(1)
                            }
                            Spacer(minLength: 0)
                        }
                        .padding(.horizontal, 11)
                        .frame(maxWidth: .infinity)
                        .frame(height: 54)
                        .background(cardBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
                    }
                    .buttonStyle(YYPressStyle(scale: 0.97))
                }

                Button {
                    dismiss()
                    openNotes()
                } label: {
                    HStack(spacing: 6) {
                        VStack(alignment: .leading, spacing: 1) {
                            Text("本书笔记")
                                .font(.yy(size: 12, weight: .semibold))
                                .foregroundStyle(ink)
                                .lineLimit(1)
                            Text("划线与想法")
                                .font(.yy(size: 10.5))
                                .foregroundStyle(ink2)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 4)
                        Text("\(model.marks.count)")
                            .font(YYUI.serif(19, .bold))
                            .monospacedDigit()
                            .foregroundStyle(notesNumFg)
                    }
                    .padding(.horizontal, 13)
                    .frame(maxWidth: .infinity)
                    .frame(height: 54)
                    .background(cardBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                    .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
                }
                .buttonStyle(YYPressStyle(scale: 0.97))
            }
        }
    }

    private var translationStrip: some View {
        let modes: [(TranslateMode, String)] = [
            (.off, String(localized: "原文")),
            (.bilingual, String(localized: "对照")),
            (.only, String(localized: "仅译文")),
        ]
        return HStack(spacing: 8) {
            Text("译文")
                .font(.yy(size: 12, weight: .medium))
                .foregroundStyle(ink2)
                .padding(.leading, 4)
            HStack(spacing: 3) {
                ForEach(modes, id: \.0.rawValue) { mode, label in
                    let on = model.translateMode == mode
                    Button {
                        model.setTranslateMode(mode)
                    } label: {
                        Text(label)
                            .font(.yy(size: 12, weight: on ? .semibold : .medium))
                            .foregroundStyle(on ? ink : ink2)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity)
                            .frame(height: 28)
                            .background(on ? cardBg : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            .shadow(color: on ? .black.opacity(0.07) : .clear, radius: 1.5, y: 1)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(3)
            .background(insetBg, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        }
        .padding(.horizontal, 8)
        .frame(height: 44)
        .background(cardBg, in: RoundedRectangle(cornerRadius: 15, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 15, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
    }

    @ViewBuilder
    private func switchCard(
        icon: String,
        title: String,
        subtitle: String,
        isOn: Bool,
        onTap: @escaping () -> Void,
        onLongPress: (() -> Void)?
    ) -> some View {
        let content = HStack(spacing: 8) {
            Image(systemName: icon)
                .font(.yy(size: 13.5, weight: .medium))
                .foregroundStyle(isOn ? ink : ink2)
                .frame(width: 28, height: 28)
                .background(isOn ? iconActiveBg : insetBg, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                    .font(.yy(size: 12, weight: .semibold))
                    .foregroundStyle(ink)
                    .lineLimit(1)
                Text(subtitle)
                    .font(.yy(size: 10.5))
                    .foregroundStyle(ink2)
                    .lineLimit(1)
            }
            Spacer(minLength: 2)
            MoreMiniSwitch(
                isOn: isOn,
                onTrack: switchOnTrack,
                onKnob: switchOnKnob,
                offTrack: switchOffTrack
            )
        }
        .padding(.horizontal, 11)
        .frame(maxWidth: .infinity)
        .frame(height: 56)
        .background(cardBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
        .contentShape(.rect)

        if let onLongPress {
            content
                .onTapGesture(perform: onTap)
                .onLongPressGesture(perform: onLongPress)
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(Text(title))
                .accessibilityValue(Text(subtitle))
                .accessibilityAddTraits(.isButton)
        } else {
            Button(action: onTap) { content }
                .buttonStyle(YYPressStyle(scale: 0.97))
                .accessibilityElement(children: .ignore)
                .accessibilityLabel(Text(title))
                .accessibilityValue(Text(subtitle))
        }
    }

    // MARK: 3. Page Turn Strip + Sound

    private var pageTurnStrip: some View {
        let current = PageTurn.of(pageTurnRaw)
        let turns: [(PageTurn, String)] = [
            (.slide, String(localized: "滑动")),
            (.cover, String(localized: "覆盖")),
            (.none, String(localized: "无动画")),
        ]
        return HStack(spacing: 8) {
            Text("翻页")
                .font(.yy(size: 12, weight: .medium))
                .foregroundStyle(ink2)
                .padding(.leading, 4)
            HStack(spacing: 3) {
                ForEach(turns, id: \.0.rawValue) { pt, label in
                    let on = current == pt
                    Button {
                        pageTurnRaw = pt.rawValue
                        model.pager.pageTurn = pt
                    } label: {
                        Text(label)
                            .font(.yy(size: 12, weight: on ? .semibold : .medium))
                            .foregroundStyle(on ? ink : ink2)
                            .lineLimit(1)
                            .frame(maxWidth: .infinity)
                            .frame(height: 28)
                            .background(on ? cardBg : .clear, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
                            .shadow(color: on ? .black.opacity(0.07) : .clear, radius: 1.5, y: 1)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(3)
            .background(insetBg, in: RoundedRectangle(cornerRadius: 11, style: .continuous))

            Button {
                pageTurnSound.toggle()
            } label: {
                Image(systemName: pageTurnSound ? "speaker.wave.2" : "speaker.slash")
                    .font(.yy(size: 14, weight: .medium))
                    .foregroundStyle(pageTurnSound ? YYTint.standard.accent : ink2)
                    .frame(width: 40, height: 32)
                    .background(pageTurnSound ? YYTint.standard.accentSoft : insetBg, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 11, style: .continuous)
                            .strokeBorder(pageTurnSound ? YYTint.standard.accent.opacity(0.35) : .clear, lineWidth: 0.5)
                    )
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel(Text("翻页音效"))
            .accessibilityValue(Text(pageTurnSound ? "已开启" : "已关闭"))
        }
        .padding(.horizontal, 8)
        .frame(height: 46)
        .background(cardBg, in: RoundedRectangle(cornerRadius: 15, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 15, style: .continuous).strokeBorder(borderColor, lineWidth: 0.5))
    }

    // MARK: 4. Bookstore Tool Footer Cards

    private var footerRow: some View {
        let pctRead = min(max(Int((model.bookFraction * 100).rounded()), 0), 100)
        let formatName: String = switch format {
        case .epub: "EPUB"
        case .txt: "TXT"
        case .pdf: "PDF"
        default: "BOOK"
        }
        return HStack(spacing: 8) {
            if !isPdf {
                footerToolCard(
                    icon: "arrow.left.arrow.right",
                    title: String(localized: "替换规则"),
                    subtitle: activeRuleCount > 0 ? String(localized: "\(activeRuleCount) 条生效") : String(localized: "去广告 · 读音"),
                    accent: false
                ) {
                    dismiss()
                    openRules()
                }
            }

            Menu {
                Button("Markdown", systemImage: "doc.plaintext") {
                    dismiss()
                    exportMarkdown()
                }
                Divider()
                NoteSendItems(sender: model.noteSender, books: {
                    guard let library = model.library else { return [] }
                    return NotedBook.load(library, only: [model.bookID]).map(\.export)
                }, book: model.detail?.summary)
            } label: {
                footerToolCardLabel(
                    icon: "square.and.arrow.up",
                    title: String(localized: "导出笔记"),
                    subtitle: String(localized: "MD · 分享"),
                    accent: true
                )
            }
            .buttonStyle(.plain)

            footerToolCard(
                icon: "info.circle",
                title: String(localized: "书的信息"),
                subtitle: "\(formatName) · \(pctRead)%",
                accent: false
            ) {
                dismiss()
                openInfo()
            }

            if isTxt {
                footerToolCard(
                    icon: "doc.text.magnifyingglass",
                    title: String(localized: "编码目录"),
                    subtitle: String(localized: "乱码 · 章节"),
                    accent: false
                ) {
                    dismiss()
                    openTxtLayout()
                }
            }
        }
    }

    private func footerToolCard(
        icon: String,
        title: String,
        subtitle: String,
        accent: Bool,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            footerToolCardLabel(icon: icon, title: title, subtitle: subtitle, accent: accent)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
    }

    private func footerToolCardLabel(
        icon: String,
        title: String,
        subtitle: String,
        accent: Bool
    ) -> some View {
        VStack(spacing: 4) {
            Image(systemName: icon)
                .font(.yy(size: 13, weight: .medium))
                .foregroundStyle(accent && !einkOn ? YYTint.standard.accent : ink)
                .frame(width: 28, height: 28)
                .background(accent && !einkOn ? YYTint.standard.accentSoft : insetBg, in: Circle())
            Text(title)
                .font(.yy(size: 11, weight: .semibold))
                .foregroundStyle(ink)
                .lineLimit(1)
            Text(subtitle)
                .font(.yy(size: 10))
                .foregroundStyle(ink2)
                .lineLimit(1)
        }
        .padding(.horizontal, 6)
        .padding(.vertical, 10)
        .frame(maxWidth: .infinity)
        .frame(minHeight: 68)
        .background(cardBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(borderColor, lineWidth: strokeWidth))
    }
}

private struct MoreMiniSwitch: View {
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
                .shadow(color: .black.opacity(0.12), radius: 1, y: 0.5)
                .frame(width: 16, height: 16)
                .padding(2)
        }
        .frame(width: 34, height: 20)
        .animation(YYMotion.quick, value: isOn)
    }
}
#endif
