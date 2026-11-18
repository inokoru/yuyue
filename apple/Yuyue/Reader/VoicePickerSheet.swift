import SwiftUI
import YuyueCore

/// §7.6-2：朗读面板里的三态下载条（Wi‑Fi 自动准备进度 / 流量下手动下载 / 失败再试；无弹窗）。
struct LiveVoicePrepBanner: View {
    let language: String
    var alwaysShowIdle = false
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
                VStack(alignment: .leading, spacing: 6) {
                    HStack {
                        Text("正在准备更好的声音 \(Int((frac * 100).rounded()))%")
                            .font(.yy(size: 11.5, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t1)
                        Spacer()
                        Text("下好后从下一章换上")
                            .font(.yy(size: 10))
                            .foregroundStyle(YYColor.t3)
                    }
                    GeometryReader { geo in
                        ZStack(alignment: .leading) {
                            Capsule().fill(YYColor.line)
                            Capsule()
                                .fill(YYTint.standard.fill)
                                .frame(width: max(6, geo.size.width * CGFloat(min(1, max(0, frac)))))
                        }
                    }
                    .frame(height: 4)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .background(YYColor.inset, in: .rect(cornerRadius: 11))

            case .idle where alwaysShowIdle || download.isExpensive || !LiveVoiceModel.isInstalled:
                HStack(spacing: 8) {
                    Image(systemName: "arrow.down.circle")
                        .font(.yy(size: 13))
                        .foregroundStyle(YYColor.t2)
                    Text("更好的声音 · \(LiveVoiceModel.totalMegabytes) MB")
                        .font(.yy(size: 11.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                    Spacer()
                    Button {
                        download.start()
                    } label: {
                        Text("下载")
                            .font(.yy(size: 11, weight: .semibold))
                            .foregroundStyle(YYColor.t1)
                            .padding(.horizontal, 10)
                            .frame(height: 25)
                            .background(YYColor.card, in: .capsule)
                            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(YYColor.inset, in: .rect(cornerRadius: 11))

            case .failed:
                HStack(spacing: 8) {
                    Image(systemName: "exclamationmark.circle")
                        .font(.yy(size: 13))
                        .foregroundStyle(YYColor.warn)
                    Text("没下下来")
                        .font(.yy(size: 11.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                    Spacer()
                    Button {
                        download.start()
                    } label: {
                        Text("再试")
                            .font(.yy(size: 11, weight: .semibold))
                            .foregroundStyle(YYColor.t1)
                            .padding(.horizontal, 10)
                            .frame(height: 25)
                            .background(YYColor.card, in: .capsule)
                            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    .buttonStyle(.plain)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(YYColor.inset, in: .rect(cornerRadius: 11))

            default:
                EmptyView()
            }
        }
    }
}

/// §5.1 & §7.6-1：严格四分组声音列表（推荐 · 我的 · 云端 · 系统自带），
/// 供朗读浮卡、手机选声音抽屉、以及广播剧人物选声共用。
struct VoiceCatalogSections: View {
    let language: String
    let bookID: String?
    let selectedID: String?
    let sampleText: String
    var usedByLabel: ((String) -> String?)? = nil
    let onPreview: (any Voice) -> Void
    let onSelect: (any Voice) -> Void
    let onOpenClone: (_ existingID: String?, _ initialName: String) -> Void
    let onOpenCloneDetail: (String) -> Void

    @State private var refreshTick = 0

    var body: some View {
        let _ = refreshTick
        let _ = LiveVoiceDownload.shared.phase
        let catalog = Voices.catalog(for: language, bookID: bookID)

        Group {
            // 1. 推荐（该语言无自带卡时不显示整组；低内存手机显示“这台手机带不动”）
            if let recommended = catalog.recommended {
                YYLabel(String(localized: "推荐"))
                switch recommended {
                case .unsupported:
                    YYGroup {
                        HStack(spacing: YYUI.s(10)) {
                            Image(systemName: "iphone")
                                .font(YYUI.font(13))
                                .foregroundStyle(YYColor.t3)
                                .frame(width: YYUI.s(28), height: YYUI.s(28))
                                .background(YYColor.inset, in: .circle)
                            VStack(alignment: .leading, spacing: 2) {
                                Text("这台手机带不动")
                                    .font(YYUI.font(11.5, .semibold))
                                    .foregroundStyle(YYColor.t2)
                                Text("内存较小，继续用下面的系统或云端声音")
                                    .font(YYUI.font(10))
                                    .foregroundStyle(YYColor.t3)
                            }
                            Spacer()
                        }
                        .padding(.vertical, YYUI.s(8))
                    }

                case .needsDownload:
                    LiveVoicePrepBanner(language: language, alwaysShowIdle: true)

                case .ready(let cards):
                    if !cards.isEmpty {
                        YYGroup {
                            ForEach(cards, id: \.id) { cardVoice in
                                voiceRow(
                                    voice: cardVoice,
                                    title: cardVoice.name,
                                    badge: cardVoice.card.narrator ? String(localized: "默认旁白") : nil,
                                    detail: usedByLabel?(cardVoice.id) ?? cardVoice.summaryLine
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
                    voiceRow(
                        voice: cardVoice,
                        title: cardVoice.name,
                        badge: String(localized: "角色"),
                        detail: usedByLabel?(cardVoice.id) ?? cardVoice.summaryLine
                    )
                }
                Button {
                    onOpenClone(nil, "")
                } label: {
                    HStack(spacing: YYUI.s(10)) {
                        Image(systemName: "plus")
                            .font(YYUI.font(12, .semibold))
                            .foregroundStyle(YYColor.t1)
                            .frame(width: YYUI.s(28), height: YYUI.s(28))
                            .background(YYColor.inset, in: .circle)
                        VStack(alignment: .leading, spacing: 1) {
                            Text("克隆一个声音")
                                .font(YYUI.font(11.5, .semibold))
                                .foregroundStyle(YYColor.t1)
                            Text("念一句 8 秒的话，声音不出这台设备")
                                .font(YYUI.font(10))
                                .foregroundStyle(YYColor.t3)
                        }
                        Spacer()
                        Image(systemName: "chevron.right")
                            .font(YYUI.font(9, .semibold))
                            .foregroundStyle(YYColor.t3)
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
                        voiceRow(
                            voice: cloudVoice,
                            title: cloudVoice.name,
                            badge: cloudVoice.qualityName,
                            detail: usedByLabel?(cloudVoice.id) ?? String(localized: "云端（已读内容本地缓存）")
                        )
                    }
                }
            }

            // 4. 系统自带
            if !catalog.system.isEmpty {
                YYLabel(String(localized: "系统自带"))
                YYGroup {
                    ForEach(catalog.system, id: \.id) { sysVoice in
                        voiceRow(
                            voice: sysVoice,
                            title: sysVoice.name,
                            badge: sysVoice.qualityName,
                            detail: usedByLabel?(sysVoice.id) ?? String(localized: "系统语音 · 离线")
                        )
                    }
                }
            }
        }
        .onAppear {
            ClonedVoices.migrateLegacyIfNeeded()
        }
        .onReceive(NotificationCenter.default.publisher(for: ClonedVoices.changed)) { _ in
            refreshTick &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: LiveVoiceModel.ready)) { _ in
            refreshTick &+= 1
        }
    }

    private func clonedRow(_ entry: ClonedEntry) -> some View {
        let resolved = ClonedVoices.resolveVoice(entry.id, bookID: bookID)
        let selected = (selectedID == entry.id)
        return HStack(spacing: YYUI.s(10)) {
            if let resolved, entry.isPlayable {
                Button {
                    onPreview(resolved)
                } label: {
                    Image(systemName: "play.fill")
                        .font(YYUI.font(9))
                        .foregroundStyle(YYColor.t1)
                        .frame(width: YYUI.s(28), height: YYUI.s(28))
                        .background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("试听"))
            } else {
                Image(systemName: entry.needsRerecord ? "arrow.clockwise" : "waveform")
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                    .frame(width: YYUI.s(28), height: YYUI.s(28))
                    .background(YYColor.inset, in: .circle)
            }

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
                                .font(YYUI.font(11.5, .semibold))
                                .foregroundStyle(YYColor.t1)
                            Text("克隆")
                                .font(YYUI.font(9.5, .medium))
                                .foregroundStyle(YYColor.t2)
                                .padding(.horizontal, 5)
                                .padding(.vertical, 1)
                                .background(YYColor.inset, in: .capsule)
                        }
                        Text(usedByLabel?(entry.id) ?? entry.subtitle)
                            .font(YYUI.font(10))
                            .foregroundStyle(entry.needsRerecord ? YYColor.warn : YYColor.t3)
                            .lineLimit(1)
                    }
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark")
                            .font(YYUI.font(12, .semibold))
                            .foregroundStyle(YYTint.standard.accent)
                            .transition(.opacity)
                    }
                }
                .animation(YYMotion.quick, value: selected)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            Button {
                onOpenCloneDetail(entry.id)
            } label: {
                Image(systemName: "ellipsis")
                    .font(YYUI.font(11, .semibold))
                    .foregroundStyle(YYColor.t2)
                    .frame(width: YYUI.s(26), height: YYUI.s(26))
                    .background(YYColor.inset.opacity(0.6), in: .circle)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("\(entry.name)的详情"))
        }
        .padding(.vertical, YYUI.s(8))
    }

    private func voiceRow(voice: any Voice, title: String, badge: String?, detail: String) -> some View {
        let selected = (selectedID == voice.id)
        return HStack(spacing: YYUI.s(10)) {
            Button {
                onPreview(voice)
            } label: {
                Image(systemName: "play.fill")
                    .font(YYUI.font(9))
                    .foregroundStyle(YYColor.t1)
                    .frame(width: YYUI.s(28), height: YYUI.s(28))
                    .background(YYColor.inset, in: .circle)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text("试听"))

            Button {
                onSelect(voice)
            } label: {
                HStack(spacing: 6) {
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 5) {
                            Text(title)
                                .font(YYUI.font(11.5, .semibold))
                                .foregroundStyle(YYColor.t1)
                            if let badge {
                                Text(badge)
                                    .font(YYUI.font(9.5, .medium))
                                    .foregroundStyle(YYColor.t2)
                                    .padding(.horizontal, 5)
                                    .padding(.vertical, 1)
                                    .background(YYColor.inset, in: .capsule)
                            }
                        }
                        if !detail.isEmpty {
                            Text(detail)
                                .font(YYUI.font(10))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                    }
                    Spacer()
                    if selected {
                        Image(systemName: "checkmark")
                            .font(YYUI.font(12, .semibold))
                            .foregroundStyle(YYTint.standard.accent)
                            .transition(.opacity)
                    }
                }
                .animation(YYMotion.quick, value: selected)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, YYUI.s(8))
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
            YYPage {
                VoiceCatalogSections(
                    language: language,
                    bookID: bookID,
                    selectedID: currentID ?? selectedID,
                    sampleText: sampleText,
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
            .navigationTitle(title)
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItem(placement: .confirmationAction) {
                    Button(String(localized: "完成"), action: onClose)
                }
            }
            .navigationDestination(for: Route.self) { route in
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
        .onDisappear {
            Voices.stopPreview()
        }
        #if os(macOS)
        .frame(width: 380, height: 520)
        #endif
    }
}
