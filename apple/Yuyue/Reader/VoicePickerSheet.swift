import SwiftUI
import YuyueCore

/// 朗读面板里的三态下载条（Wi‑Fi 自动准备进度 / 流量下手动下载 / 失败再试；无弹窗）。
struct LiveVoicePrepBanner: View {
    let language: String
    var alwaysShowIdle = false
    /// In the picker: 下载 / 再试 as a pill beside 更好的声音, not a word.
    var pill = false
    private var download: LiveVoiceDownload { .shared }

    var body: some View {
        Group { content }
            // Idle → downloading → done swap in place; the bar fills rather than jumps.
            .animation(YYMotion.standard, value: download.phase)
    }

    @ViewBuilder private var content: some View {
        if CardVoice.supportsLanguage(language) {
            switch download.phase {
            case .downloading(let frac):
                line {
                    (Text("正在准备更好的声音 ").foregroundStyle(YYColor.t2)
                        + Text("\(Int((frac * 100).rounded()))%").fontWeight(.semibold).foregroundStyle(YYColor.t1))
                        .monospacedDigit()
                    Spacer(minLength: 8)
                    ZStack(alignment: .leading) {
                        Capsule().fill(YYColor.line)
                        Capsule()
                            .fill(YYTint.standard.fill)
                            .frame(width: max(4, 88 * CGFloat(min(1, max(0, frac)))))
                    }
                    .frame(width: 88, height: 3)
                }
                .help(String(localized: "下好后从下一章换上"))

            case .idle where alwaysShowIdle || download.isExpensive || !LiveVoiceModel.isInstalled:
                line {
                    Text("更好的声音 · \(LiveVoiceModel.totalMegabytes) MB").foregroundStyle(YYColor.t2)
                    Spacer(minLength: 8)
                    action(String(localized: "下载"), color: YYTint.standard.accent) { download.start() }
                }

            case .failed:
                line {
                    Text("没下下来").foregroundStyle(YYColor.t2)
                    Spacer(minLength: 8)
                    action(String(localized: "再试"), color: YYColor.danger) { download.start() }
                }

            default:
                EmptyView()
            }
        }
    }

    private func line<C: View>(@ViewBuilder _ c: () -> C) -> some View {
        HStack(spacing: 8) { c() }
            .font(.yy(size: 11.5))
            .padding(.horizontal, 11)
            .frame(minHeight: 34)
            .background(YYColor.bg, in: .rect(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    @ViewBuilder
    private func action(_ title: String, color: Color, _ run: @escaping () -> Void) -> some View {
        if pill {
            Button(title, action: run)
                .buttonStyle(YYButtonStyle(kind: color == YYColor.danger ? .danger : .soft, size: .small, height: 24))
        } else {
        Button(action: run) {
            Text(title)
                .font(.yy(size: 11.5, weight: .semibold))
                .foregroundStyle(color)
                .padding(.horizontal, 4)
                .frame(height: 26)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        }
    }
}

/// 严格四分组声音列表（推荐 · 我的 · 云端 · 系统自带），
/// 供朗读浮卡、手机选声音抽屉、以及广播剧人物选声共用。
struct VoiceCatalogSections: View {
    let language: String
    let bookID: String?
    let selectedID: String?
    let sampleText: String
    var usedByLabel: ((String) -> String?)? = nil
    /// 《书名》: a role made for this book reads 为《…》生成.
    var bookTitle: String? = nil
    let onPreview: (any Voice) -> Void
    let onSelect: (any Voice) -> Void
    let onOpenClone: (_ existingID: String?, _ initialName: String) -> Void
    let onOpenCloneDetail: (String) -> Void

    @State private var refreshTick = 0
    private let tint = YYTint.standard

    var body: some View {
        let _ = refreshTick
        let _ = KokoroDownload.shared.phase
        let _ = LiveVoiceDownload.shared.phase
        let catalog = Voices.catalog(for: language, bookID: bookID)
        let defaultID = KokoroModel.defaultNarrator(for: language)?.id ?? VoicePack.defaultNarrator(for: language)?.id

        Group {
            // 1. 推荐（Kokoro 轻量离线旁白 + 本机角色卡）
            if let recommended = catalog.recommended {
                YYLabel(String(localized: "推荐"))
                switch recommended {
                case .unsupported:
                    YYGroup {
                        Text("这台设备带不动离线语音，可使用云端「在线自然语音（免 Key）」")
                            .font(YYUI.font(10.5))
                            .foregroundStyle(YYColor.t3)
                            .padding(.vertical, YYUI.s(10))
                            .frame(maxWidth: .infinity, alignment: .leading)
                    }

                case .needsDownload:
                    YYGroup {
                        downloadRow
                    }

                case .ready(let cards):
                    if !cards.isEmpty {
                        YYGroup {
                            ForEach(Array(cards.enumerated()), id: \.element.id) { _, cardVoice in
                                let isDefault = cardVoice.id == defaultID
                                let isNarrator = (cardVoice as? CardVoice)?.card.narrator == true
                                    || (cardVoice as? KokoroVoice)?.speaker.narrator == true
                                let summary = (cardVoice as? CardVoice)?.summaryLine
                                    ?? (cardVoice as? KokoroVoice)?.summaryLine
                                    ?? cardVoice.qualityName
                                    ?? ""
                                row(
                                    voice: cardVoice,
                                    badge: isDefault ? String(localized: "默认旁白") : (isNarrator ? String(localized: "推荐旁白") : nil),
                                    green: isDefault,
                                    detail: usedByLabel?(cardVoice.id) ?? summary
                                )
                            }
                        }
                    }
                }
            }

            // 2. 我的（本机克隆卡 + 当前书生成的角色卡 + 克隆一个声音入口）
            YYLabel(String(localized: "我的"))
            YYGroup {
                ForEach(catalog.clones) { entry in
                    clonedRow(entry)
                }
                ForEach(catalog.bookCards.filter { $0.speaks(language) }, id: \.id) { cardVoice in
                    row(
                        voice: cardVoice,
                        badge: String(localized: "本书角色"),
                        detail: usedByLabel?(cardVoice.id) ?? Self.roleLine(cardVoice, bookTitle: bookTitle)
                    )
                }
                Button {
                    onOpenClone(nil, "")
                } label: {
                    HStack(spacing: YYUI.s(10)) {
                        Image(systemName: "plus")
                            .font(YYUI.font(12, .semibold))
                            .foregroundStyle(tint.accent)
                            .frame(width: YYUI.s(28), height: YYUI.s(28))
                            .background(tint.accentSoft, in: .circle)
                        VStack(alignment: .leading, spacing: 1) {
                            Text("克隆一个声音")
                                .font(YYUI.font(12.5, .semibold))
                                .foregroundStyle(tint.accent)
                            Text("录 10 秒自己的声音 · 不联网、不花钱")
                                .font(YYUI.font(10.5))
                                .foregroundStyle(YYColor.t3)
                        }
                        Spacer()
                    }
                    .padding(.vertical, YYUI.s(8))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }

            // 3. 云端
            if !catalog.cloud.isEmpty {
                YYLabel(String(localized: "云端"))
                YYGroup {
                    ForEach(catalog.cloud, id: \.id) { cloudVoice in
                        row(
                            voice: cloudVoice,
                            badge: cloudVoice.qualityName,
                            detail: usedByLabel?(cloudVoice.id) ?? String(localized: "云端（已读内容本地缓存）")
                        )
                    }
                }
            }

            // 4. 系统自带（不推荐）
            if !catalog.system.isEmpty {
                YYLabel(String(localized: "系统自带 · 不推荐"))
                YYGroup {
                    ForEach(catalog.system, id: \.id) { sysVoice in
                        row(
                            voice: sysVoice,
                            badge: sysVoice.qualityName ?? String(localized: "不推荐"),
                            detail: usedByLabel?(sysVoice.id) ?? sysVoice.listLine
                        )
                    }
                }
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: ClonedVoices.changed)) { _ in
            refreshTick &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: KokoroModel.ready)) { _ in
            refreshTick &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: LiveVoiceModel.ready)) { _ in
            refreshTick &+= 1
        }
    }

    /// 为《红楼梦》生成 · 少年男声.
    static func roleLine(_ v: CardVoice, bookTitle: String?) -> String {
        let voice = [CardVoice.ageLabel(v.card.age), v.card.gender == .m ? String(localized: "男声") : v.card.gender == .f ? String(localized: "女声") : nil]
            .compactMap { $0 }.joined()
        let made = bookTitle.map { String(localized: "为《\($0)》生成") }
        let parts = [made, voice.isEmpty ? nil : voice].compactMap { $0 }
        return parts.isEmpty ? v.summaryLine : parts.joined(separator: " · ")
    }

    /// 语阅离线语音（Kokoro） · 125 MB / 跨端自然朗读 · 断网可用 · 下载; while it comes, its percent.
    @ViewBuilder private var downloadRow: some View {
        let phase = KokoroDownload.shared.phase
        Button {
            if case .downloading = phase { return }
            KokoroDownload.shared.start()
        } label: {
            HStack(spacing: YYUI.s(10)) {
                VStack(alignment: .leading, spacing: 1) {
                    switch phase {
                    case .downloading(let frac):
                        Text("正在准备离线语音 \(Int((frac * 100).rounded()))%")
                            .monospacedDigit()
                            .font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    case .failed:
                        Text("离线语音没下下来").font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    default:
                        Text("语阅离线语音（Kokoro） · \(KokoroModel.totalMegabytes) MB").font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    }
                    Text(phase == .idle
                        ? String(localized: "点此下载 · 跨端自然朗读，断网可用")
                        : String(localized: "下载期间可先选下方「在线自然语音」立刻听"))
                        .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                }
                Spacer()
                switch phase {
                case .downloading: ProgressView().controlSize(.small)
                case .failed:
                    Text("再试")
                        .font(YYUI.font(11.5, .semibold))
                        .foregroundStyle(YYColor.danger)
                        .padding(.horizontal, 10)
                        .frame(height: 25)
                        .background(YYColor.danger.opacity(0.12), in: .capsule)
                default:
                    Text("下载")
                        .font(YYUI.font(11.5, .semibold))
                        .foregroundStyle(tint.accent)
                        .padding(.horizontal, 10)
                        .frame(height: 25)
                        .background(tint.accentSoft, in: .capsule)
                }
            }
            .padding(.vertical, YYUI.s(9))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func avatar(_ name: String, selected: Bool, plus: Bool = false) -> some View {
        Text(String(name.prefix(1)))
            .font(YYUI.font(11.5, .semibold))
            .foregroundStyle(selected ? tint.accent : YYColor.t2)
            .frame(width: YYUI.s(28), height: YYUI.s(28))
            .background(selected ? tint.accent.opacity(0.16) : YYColor.inset, in: .circle)
    }

    private func badgeView(_ text: String, green: Bool) -> some View {
        Text(text)
            .font(YYUI.font(9.5, green ? .semibold : .medium))
            .foregroundStyle(green ? tint.accent : YYColor.t2)
            .padding(.horizontal, 5)
            .padding(.vertical, 1)
            .background(green ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: 4))
    }

    /// The selected row's wash runs to the card's edges.
    private func selectedWash(_ on: Bool) -> some View {
        Rectangle()
            .fill(on ? tint.soft : .clear)
            .padding(.horizontal, -YYUI.s(12))
    }

    private func clonedRow(_ entry: ClonedEntry) -> some View {
        let resolved = ClonedVoices.resolveVoice(entry.id, bookID: bookID)
        let selected = (selectedID == entry.id)
        return HStack(spacing: YYUI.s(10)) {
            Button {
                if let resolved, entry.isPlayable { onPreview(resolved) }
            } label: {
                avatar(entry.name, selected: selected)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("试听"))

            Button {
                if entry.needsRerecord {
                    onOpenClone(entry.id, entry.name)
                } else if let resolved, entry.isPlayable {
                    onSelect(resolved)
                } else {
                    onOpenCloneDetail(entry.id)
                }
            } label: {
                HStack(spacing: 6) {
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 5) {
                            Text(entry.name)
                                .font(YYUI.font(12.5, .semibold))
                                .foregroundStyle(YYColor.t1)
                            badgeView(String(localized: "克隆"), green: false)
                        }
                        Text(usedByLabel?(entry.id) ?? entry.subtitle)
                            .font(YYUI.font(10.5))
                            .foregroundStyle(entry.needsRerecord ? YYColor.warn : YYColor.t3)
                            .lineLimit(1)
                    }
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark")
                            .font(YYUI.font(12, .bold))
                            .foregroundStyle(tint.accent)
                            .transition(.opacity)
                    }
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            Button {
                onOpenCloneDetail(entry.id)
            } label: {
                Image(systemName: "ellipsis")
                    .font(YYUI.font(12))
                    .foregroundStyle(YYColor.t3)
                    .frame(width: YYUI.s(26), height: YYUI.s(26))
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("\(entry.name)的详情"))
        }
        .padding(.vertical, YYUI.s(8))
        .background(selectedWash(selected))
        .animation(YYMotion.quick, value: selected)
    }

    /// 首字 · 名字 [标] / 一行说明 · ✓. The initial plays a line; the rest picks it.
    private func row(voice: any Voice, badge: String?, green: Bool = false, detail: String) -> some View {
        let selected = (selectedID == voice.id)
        return HStack(spacing: YYUI.s(10)) {
            Button {
                onPreview(voice)
            } label: {
                avatar(voice.name, selected: selected)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("试听"))

            Button {
                onSelect(voice)
            } label: {
                HStack(spacing: 6) {
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 5) {
                            Text(voice.name)
                                .font(YYUI.font(12.5, .semibold))
                                .foregroundStyle(YYColor.t1)
                            if let badge { badgeView(badge, green: green) }
                        }
                        if !detail.isEmpty {
                            Text(detail)
                                .font(YYUI.font(10.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                    }
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark")
                            .font(YYUI.font(12, .bold))
                            .foregroundStyle(tint.accent)
                            .transition(.opacity)
                    }
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, YYUI.s(8))
        .background(selectedWash(selected))
        .animation(YYMotion.quick, value: selected)
    }
}

/// 朗读面板点“换声音”弹出的完整四分组选声音面板（支持内部直接进入克隆与克隆详情）。
struct VoicePickerSheet: View {
    let title: String
    let language: String
    let bookID: String?
    let selectedID: String?
    let sampleText: String
    let previewSentence: String?
    /// 《书名》 for the Mac panel's 试听 line.
    var sampleSource: String? = nil
    let onPreview: (any Voice) -> Void
    let onSelect: (any Voice) -> Void
    let onClose: () -> Void

    private enum Route: Hashable {
        case clone(existingID: String?, initialName: String)
        case detail(String)
    }

    @State private var path: [Route] = []
    @State private var currentID: String?

    var body: some View {
        NavigationStack(path: $path) {
            root
            .navigationDestination(for: Route.self) { route in destination(route) }
        }
        #if DEBUG
        // `-debugVoiceRoute clone`: straight into 克隆一个声音.
        .task {
            switch UserDefaults.standard.string(forKey: "debugVoiceRoute") {
            case "clone":
                try? await Task.sleep(for: .seconds(0.5))
                path.append(.clone(existingID: nil, initialName: ""))
            case "detail":
                // A made-up 我的声音 with two tones, from a built-in card.
                try? await Task.sleep(for: .seconds(0.5))
                path.append(.detail("debug"))
            default: break
            }
        }
        #endif
        .onDisappear {
            Voices.stopPreview()
        }
        #if os(macOS)
        // The list is a fixed panel; a clone page or a card's detail is a card as tall as it is.
        .frame(width: path.isEmpty ? 486 : CloneCardPage<EmptyView>.macWidth, height: path.isEmpty ? 476 : nil)
        .fixedSize(horizontal: false, vertical: !path.isEmpty)
        #endif
    }

    @ViewBuilder private var root: some View {
        #if os(macOS)
        MacVoicePickerPanel(
            language: language,
            bookID: bookID,
            selectedID: currentID ?? selectedID,
            sampleSource: sampleSource,
            onPreview: onPreview,
            onSelect: { v in
                currentID = v.id
                onSelect(v)
            },
            onOpenClone: { existing, initName in path.append(.clone(existingID: existing, initialName: initName)) },
            onOpenCloneDetail: { id in path.append(.detail(id)) },
            onClose: onClose,
            title: title
        )
        .toolbar(.hidden)
        #else
        phoneList
        #endif
    }

    private var phoneList: some View {
        VStack(spacing: 0) {
            YYSheetHeader(title: title, close: onClose)
                .padding(.horizontal, YYUI.s(16))
                .padding(.top, YYUI.sheetTop)
                .padding(.bottom, YYUI.s(6))
            YYPage {
                VoiceCatalogSections(
                    language: language,
                    bookID: bookID,
                    selectedID: currentID ?? selectedID,
                    sampleText: sampleText,
                    bookTitle: sampleSource,
                    onPreview: onPreview,
                    onSelect: { v in
                        currentID = v.id
                        onSelect(v)
                    },
                    onOpenClone: { existing, initName in
                        path.append(.clone(existingID: existing, initialName: initName))
                    },
                    onOpenCloneDetail: { id in
                        path.append(.detail(id))
                    }
                )
            }
        }
        .background(YYColor.bg)
        .toolbar(.hidden)
    }

    @ViewBuilder private func destination(_ route: Route) -> some View {
                if let ctx = LiveVoiceModel.context {
                    switch route {
                    case let .clone(existingID, initialName):
                        CloneVoiceView(
                            core: ctx.core,
                            dataDir: ctx.dataDir,
                            language: language,
                            existingID: existingID,
                            initialName: initialName,
                            previewSentence: previewSentence ?? sampleText,
                            bookTitle: sampleSource,
                            onSaved: { saved in
                                let voice = ClonedVoices.resolveVoice(saved.id, bookID: bookID) ?? CardVoice(card: saved, bookID: bookID)
                                currentID = voice.id
                                onSelect(voice)
                                if !path.isEmpty { path.removeLast() }
                            },
                            onClose: {
                                if !path.isEmpty { path.removeLast() }
                            }
                        )
                    case let .detail(entryID):
                        ClonedCardDetailView(
                            entryID: entryID,
                            core: ctx.core,
                            dataDir: ctx.dataDir,
                            language: language
                        )
                    }
                }
    }
}
