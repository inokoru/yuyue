import SwiftUI
import YuyueCore
#if os(macOS)
import AppKit
#endif

/// 「酿一期书房电台」弹窗 — 与播客首页同源的文房纸墨语言：
/// - 统一顶底栏 + 严丝合缝等高双栏（唯一跨栏宋体主标题，零双标题打架、零侧栏空洞）；
/// - 左卡整合 4 档节目形态与播客首页同款「仅已读｜全书」防剧透切换；
/// - 右侧为高密度双列搜书网格 + 选题 + 明显分段的圆角胶囊时长进度条（与播客阅读页分段进度条同源）。
struct PodcastStudioSheet: View {
    let library: Library
    var initialKind: StudioKind = .bookGuide
    var preselectedBookIDs: [String] = []
    let onCreated: (PodcastIssue) -> Void
    let onClose: () -> Void

    enum BookFilter: String, CaseIterable {
        case reading
        case finished
        case all

        var label: String {
            switch self {
            case .reading: String(localized: "在读")
            case .finished: String(localized: "读过")
            case .all: String(localized: "全部")
            }
        }
    }

    @State private var kind: StudioKind
    @State private var selectedBookIDs: [String]
    @State private var bookQuery = ""
    @State private var bookFilter: BookFilter = .reading
    @State private var topic = ""
    @State private var minutes: UInt32 = 10
    @State private var noSpoiler = true
    @State private var voiceStyle: String
    @State private var hoveredKind: StudioKind?
    @State private var hoveredBookID: String?

    @Namespace private var filterSelection
    @Namespace private var spoilerSelection
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private let kinds: [StudioKind] = [.weeklyDigest, .bookGuide, .roundtable, .vocabRadio]

    init(
        library: Library,
        initialKind: StudioKind = .bookGuide,
        preselectedBookIDs: [String] = [],
        onCreated: @escaping (PodcastIssue) -> Void,
        onClose: @escaping () -> Void
    ) {
        self.library = library
        self.initialKind = initialKind
        self.preselectedBookIDs = preselectedBookIDs
        self.onCreated = onCreated
        self.onClose = onClose
        _kind = State(initialValue: initialKind)
        _selectedBookIDs = State(initialValue: preselectedBookIDs)
        _minutes = State(initialValue: Self.defaultDuration(for: initialKind))
        _voiceStyle = State(initialValue: library.podcastHub.studioVoiceStyle)
    }

    private static func defaultDuration(for kind: StudioKind) -> UInt32 {
        switch kind {
        case .weeklyDigest: 10
        case .bookGuide: 10
        case .roundtable: 15
        case .vocabRadio: 10
        }
    }

    private var allBooks: [BookSummary] {
        library.keptBooks
    }

    private var selectedBooks: [BookSummary] {
        selectedBookIDs.compactMap { id in allBooks.first { $0.id == id } }
    }

    /// Filtered & searched books — typing searches across the ENTIRE library regardless of tab.
    private var filteredBooks: [BookSummary] {
        let q = bookQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        if !q.isEmpty {
            return allBooks.filter { book in
                book.title.lowercased().contains(q)
                    || book.authors.contains { $0.lowercased().contains(q) }
            }
        }
        switch bookFilter {
        case .reading:
            let reading = allBooks.filter { $0.status == .reading }
            return reading.isEmpty ? allBooks : reading
        case .finished:
            return allBooks.filter { $0.status == .finished }
        case .all:
            return allBooks
        }
    }

    /// Only show the pinned selected token bar when library has > 6 books, or actively searching,
    /// or any selected book is outside the currently visible filter list — avoids duplicate noise on small shelves
    /// while guaranteeing zero lost selections on 500-book libraries.
    private var showsSelectedTokenBar: Bool {
        guard !selectedBooks.isEmpty else { return false }
        if allBooks.count > 6 || !bookQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return true
        }
        let visibleIDs = Set(filteredBooks.map(\.id))
        return selectedBookIDs.contains { !visibleIDs.contains($0) }
    }

    var body: some View {
        #if os(macOS)
        VStack(alignment: .leading, spacing: 13) {
            unifiedTopBar

            // Equal-height Swiss-grid split: left format+spoiler card matches right pane bottom pixel-for-pixel
            HStack(alignment: .top, spacing: 14) {
                leftCard
                    .frame(width: 174)
                    .frame(maxHeight: .infinity)

                composerBody
                    .frame(maxWidth: .infinity, alignment: .topLeading)
            }
            .fixedSize(horizontal: false, vertical: true)

            unifiedFooter
        }
        .padding(.horizontal, 20)
        .padding(.vertical, 16)
        .frame(width: 720)
        .background(YYColor.bg)
        .onAppear {
            seedDefaultBooksIfEmpty()
            if allBooks.filter({ $0.status == .reading }).isEmpty {
                bookFilter = .all
            }
            for delay in [0.0, 0.15, 0.35] {
                DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                    for w in NSApp.windows where w.isSheet && w.isVisible { w.makeFirstResponder(nil) }
                }
            }
        }
        #else
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    mobileKindStrip
                    composerBody
                    mobileFooter
                }
                .padding(16)
            }
            .background(YYColor.bg)
            .navigationTitle(String(localized: "酿一期书房电台"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .cancellationAction) {
                    Button(String(localized: "取消"), action: onClose)
                }
            }
        }
        .onAppear {
            seedDefaultBooksIfEmpty()
            if allBooks.filter({ $0.status == .reading }).isEmpty {
                bookFilter = .all
            }
        }
        #endif
    }

    private func seedDefaultBooksIfEmpty() {
        if selectedBookIDs.isEmpty {
            let maxCount = kind == .roundtable ? min(2, allBooks.count) : 1
            selectedBookIDs = Array(allBooks.prefix(maxCount).map(\.id))
        }
    }

    @ViewBuilder
    private func railIcon(for item: StudioKind, active: Bool) -> some View {
        Group {
            switch item {
            case .weeklyDigest:
                PodcastGlyphView(glyph: .weekly, size: 13.5, lineWidth: 1.45)
            case .bookGuide:
                PodcastGlyphView(glyph: .openBook, size: 13.5, lineWidth: 1.45)
            case .roundtable:
                PodcastGlyphView(glyph: .roundtable, size: 13.5, lineWidth: 1.45)
            case .vocabRadio:
                PodcastGlyphView(glyph: .vocab, size: 13.5, lineWidth: 1.45)
            }
        }
        .foregroundStyle(active ? tint.accent : YYColor.t2)
        .frame(width: 18, height: 18)
    }

    private func formatMicroNote(for item: StudioKind) -> String {
        switch item {
        case .weeklyDigest: String(localized: "串起本周划线")
        case .bookGuide: String(localized: "精讲难懂章节")
        case .roundtable: String(localized: "多书观点对谈")
        case .vocabRadio: String(localized: "生词自然伴听")
        }
    }

    // MARK: - Unified Full-Span Top Bar & Equal-Height Left Card (Layout 4)

    private var headerSubtitle: String {
        switch kind {
        case .bookGuide: String(localized: "读前热身精讲 · 拆解核心脉络")
        default: kind.subtitle
        }
    }

    #if os(macOS)
    private var unifiedTopBar: some View {
        HStack(alignment: .center, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 9) {
                Text("酿一期书房电台")
                    .font(PodcastStyle.songti(19))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                Text("·")
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                Text(headerSubtitle)
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            PodcastCircleIconButton(
                glyph: .xmark,
                size: 28,
                iconSize: 11,
                accessibilityLabel: String(localized: "关闭"),
                action: onClose
            )
            .focusable(false)
            .keyboardShortcut(.cancelAction)
        }
        .frame(height: 28)
    }

    private var leftCard: some View {
        PodcastStationeryCard(cornerRadius: 13) {
            VStack(spacing: 0) {
                // Top tier: 4 clean format rows (zero icon-circle clutter)
                VStack(spacing: 2) {
                    ForEach(kinds, id: \.self) { item in
                        let active = kind == item
                        let hovered = hoveredKind == item

                        Button {
                            withAnimation(YYMotion.quick) { kind = item }
                        } label: {
                            HStack(spacing: 9) {
                                railIcon(for: item, active: active)

                                VStack(alignment: .leading, spacing: 1.5) {
                                    Text(item.title)
                                        .font(.yy(size: 12.5, weight: active ? .medium : .regular))
                                        .foregroundStyle(active ? tint.accent : YYColor.t1)
                                        .lineLimit(1)
                                    Text(formatMicroNote(for: item))
                                        .font(.yy(size: 10))
                                        .foregroundStyle(active ? tint.accent.opacity(0.75) : YYColor.t3)
                                        .lineLimit(1)
                                }

                                Spacer(minLength: 0)
                            }
                            .padding(.horizontal, 10)
                            .frame(height: 44)
                            .background(
                                RoundedRectangle(cornerRadius: 8.5, style: .continuous)
                                    .fill(
                                        active
                                            ? PodcastStyle.rowTintWash(tint, scheme)
                                            : (hovered ? PodcastStyle.leafSurface(scheme) : Color.clear)
                                    )
                            )
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .onHover { over in
                            hoveredKind = over ? item : (hoveredKind == item ? nil : hoveredKind)
                        }
                    }
                }
                .padding(6)

                Spacer(minLength: 0)

                Rectangle()
                    .fill(PodcastStyle.hairline(scheme))
                    .frame(height: 0.5)

                // Lower warm-leaf tier: Homepage-native recessed 2-way `仅已读 | 全书` switch
                VStack(alignment: .leading, spacing: 6) {
                    Text("防剧透范围")
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(YYColor.t2)

                    spoilerSegmentControl
                }
                .padding(10)
                .background(PodcastStyle.leafSurface(scheme))
            }
            .clipShape(RoundedRectangle(cornerRadius: 13, style: .continuous))
        }
    }

    private var unifiedFooter: some View {
        HStack(spacing: 8) {
            if let status = library.podcastHub.studioStatus {
                ProgressView().controlSize(.small)
                Text(status)
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(1)
            } else {
                voiceMenuPill
                if !CompanionConfig.current.isReady {
                    Text(String(localized: "未接书伴模型 · 将用本机离线摘要成稿"))
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)
                        .help(String(localized: "在「设置 · 书伴」连接大模型后可生成完整双人对谈"))
                }
            }

            Spacer(minLength: 8)

            PodcastPaperPill(title: String(localized: "取消"), height: 30, horizontalPadding: 14, action: onClose)

            Button(action: generate) {
                Text(
                    library.podcastHub.isGeneratingStudio
                        ? String(localized: "正在酿制…")
                        : String(localized: "开始生成")
                )
                .font(.yy(size: 12, weight: .medium))
                .foregroundStyle(YYColor.card)
                .padding(.horizontal, 16)
                .frame(height: 30)
                .background(YYColor.t1, in: Capsule())
                .contentShape(Capsule())
            }
            .buttonStyle(YYPressStyle(scale: 0.97))
            .disabled(library.podcastHub.isGeneratingStudio)
        }
        .padding(.top, 2)
    }
    #endif

    /// Podcast-Homepage-native recessed matte 2-way switch (`仅已读 | 全书`) matching `在读 / 读过 / 全部` & `原声 / 原声线中文`.
    private var spoilerSegmentControl: some View {
        HStack(spacing: 2) {
            spoilerSegmentButton(title: String(localized: "仅已读"), icon: "lock.fill", value: true)
            spoilerSegmentButton(title: String(localized: "全书"), icon: nil, value: false)
        }
        .padding(2)
        .frame(height: 27)
        .background(
            RoundedRectangle(cornerRadius: 8, style: .continuous)
                .fill(scheme == .dark ? Color.white.opacity(0.065) : Color(yy: YYRGBA(236, 234, 227)))
        )
    }

    private func spoilerSegmentButton(title: String, icon: String?, value: Bool) -> some View {
        let on = noSpoiler == value
        return Button {
            withAnimation(YYMotion.quick) { noSpoiler = value }
        } label: {
            HStack(spacing: 3.5) {
                if let icon {
                    Image(systemName: icon)
                        .font(.system(size: 8.5, weight: .semibold))
                }
                Text(title)
                    .font(.yy(size: 11, weight: on ? .medium : .regular))
                    .lineLimit(1)
            }
            .foregroundStyle(on ? tint.accent : YYColor.t3)
            .frame(maxWidth: .infinity)
            .frame(height: 23)
            .background {
                if on {
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(YYColor.card)
                        .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 0.5)
                        .matchedGeometryEffect(id: "spoilerSegPill", in: spoilerSelection)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(value
              ? String(localized: "仅依据已读进度与划线生成，绝不剧透后文")
              : String(localized: "允许参考全书内容生成，适合已读或泛览"))
    }

    // MARK: - Mobile Kind Strip

    private var mobileKindStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 6) {
                ForEach(kinds, id: \.self) { item in
                    let active = kind == item
                    Button {
                        withAnimation(YYMotion.quick) { kind = item }
                    } label: {
                        HStack(spacing: 6) {
                            railIcon(for: item, active: active)
                            Text(item.title)
                                .font(.yy(size: 12.5, weight: active ? .medium : .regular))
                        }
                        .foregroundStyle(active ? tint.accent : YYColor.t2)
                        .padding(.horizontal, 12)
                        .frame(height: 30)
                        .background(active ? YYColor.card : YYColor.inset.opacity(0.7), in: Capsule())
                        .overlay(
                            Capsule().strokeBorder(
                                active ? PodcastStyle.pillBorder(scheme) : Color.clear,
                                lineWidth: 0.5
                            )
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
        }
    }

    private var mobileFooter: some View {
        VStack(alignment: .leading, spacing: 6) {
            if !CompanionConfig.current.isReady {
                Text(String(localized: "未接书伴模型 · 将用本机离线摘要成稿（可在「我的 · 书伴」连接模型）"))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(2)
            }
            HStack(spacing: 8) {
                voiceMenuPill
                Spacer(minLength: 8)
                Button(action: generate) {
                    Text(
                        library.podcastHub.isGeneratingStudio
                            ? String(localized: "正在酿制…")
                            : String(localized: "开始生成")
                    )
                    .font(.yy(size: 12.5, weight: .medium))
                    .foregroundStyle(YYColor.card)
                    .padding(.horizontal, 18)
                    .frame(height: 34)
                    .background(YYColor.t1, in: Capsule())
                    .contentShape(Capsule())
                }
                .buttonStyle(YYPressStyle(scale: 0.97))
                .disabled(library.podcastHub.isGeneratingStudio)
            }
        }
        .padding(.top, 4)
    }

    private func voiceStyleTitle(_ style: String, cloned: [ClonedEntry]) -> String {
        switch style {
        case "studio-duo-en":
            return String(localized: "Clara & David")
        case "studio-solo-zh":
            return String(localized: "林深 · 单人")
        case let s where s.hasPrefix("cloned:"):
            let id = String(s.dropFirst("cloned:".count))
            return cloned.first(where: { $0.id == id })?.name ?? String(localized: "我的声纹")
        default:
            return String(localized: "林深 · 苏晚")
        }
    }

    private var voiceMenuPill: some View {
        let cloned = ClonedVoices.entries()
        return Menu {
            Button(String(localized: "林深 · 苏晚（书房双人对谈）")) { voiceStyle = "studio-duo-zh" }
            Button(String(localized: "Clara & David（自然英语对谈）")) { voiceStyle = "studio-duo-en" }
            Button(String(localized: "林深（单人沉静导读）")) { voiceStyle = "studio-solo-zh" }
            if !cloned.isEmpty {
                Divider()
                ForEach(cloned) { entry in
                    Button(entry.name) { voiceStyle = "cloned:\(entry.id)" }
                }
            }
        } label: {
            HStack(spacing: 6) {
                PodcastGlyphView(glyph: .waveform, size: 12, lineWidth: 1.5)
                    .foregroundStyle(tint.accent)
                Text(voiceStyleTitle(voiceStyle, cloned: cloned))
                    .font(.yy(size: 12, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                PodcastGlyphView(glyph: .chevronDown, size: 10, lineWidth: 1.5)
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 11)
            .frame(height: 30)
            .background(YYColor.card, in: Capsule())
            .overlay(Capsule().strokeBorder(PodcastStyle.pillBorder(scheme), lineWidth: 0.5))
            .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .fixedSize()
    }

    // MARK: - Composer Body

    private var composerBody: some View {
        VStack(alignment: .leading, spacing: 11) {
            if kind != .vocabRadio {
                scalableBookGridSection
            } else {
                vocabPreviewCard
            }

            // Settings Stationery Card (Topic + Segmented Duration Scrubber + iOS Spoiler Control)
            PodcastStationeryCard(cornerRadius: 13) {
                VStack(spacing: 0) {
                    HStack(alignment: .center, spacing: 10) {
                        Text("选题")
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .frame(width: 34, alignment: .leading)

                        TextField(
                            "",
                            text: $topic,
                            prompt: Text(String(localized: "选填 · 留空则按最近划线自动拟题")).foregroundStyle(YYColor.t3.opacity(0.65))
                        )
                        .textFieldStyle(.plain)
                        .font(.yy(size: 12.5))
                        .foregroundStyle(YYColor.t1)

                        if !topic.isEmpty {
                            Button { topic = "" } label: {
                                PodcastGlyphView(glyph: .xmark, size: 9.5, lineWidth: 1.7)
                                    .foregroundStyle(YYColor.t3)
                                    .frame(width: 18, height: 18)
                                    .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.horizontal, 14)
                    .frame(height: 39)

                    Rectangle()
                        .fill(PodcastStyle.softDivider(scheme))
                        .frame(height: 0.5)
                        .padding(.leading, 14)

                    StudioDurationPickerRow(minutes: $minutes)
                        .padding(.horizontal, 14)
                        .frame(height: 50)

                    #if os(iOS)
                    Rectangle()
                        .fill(PodcastStyle.softDivider(scheme))
                        .frame(height: 0.5)
                        .padding(.leading, 14)

                    HStack(spacing: 10) {
                        Text("防剧透")
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .frame(width: 44, alignment: .leading)
                        Spacer()
                        spoilerSegmentControl
                            .frame(width: 146)
                    }
                    .padding(.horizontal, 14)
                    .frame(height: 44)
                    #endif
                }
            }
        }
    }

    // MARK: - Compact 2-Column Scalable Book Picker (Search + Tabs + High-Density 2-Col Grid + Smart Selected Chips)

    private var scalableBookGridSection: some View {
        let gridCols = [
            GridItem(.flexible(), spacing: 8),
            GridItem(.flexible(), spacing: 8),
        ]

        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("参考书目")
                    .font(PodcastStyle.songti(14))
                    .foregroundStyle(YYColor.t1)

                Text(kind == .bookGuide ? String(localized: "单选精讲") : String(localized: "可多选"))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)

                Spacer()

                if !selectedBookIDs.isEmpty {
                    Text(String(localized: "已选 \(selectedBookIDs.count) 本"))
                        .font(.yy(size: 11, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(tint.accent)

                    if selectedBookIDs.count > 1 || kind != .bookGuide {
                        Button {
                            withAnimation(YYMotion.quick) { selectedBookIDs.removeAll() }
                        } label: {
                            Text("清空")
                                .font(.yy(size: 11))
                                .foregroundStyle(YYColor.t3)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
            .padding(.horizontal, 2)

            // Smart pinned selected tokens (only when >6 books, searching, or hidden under another filter)
            if showsSelectedTokenBar {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 6) {
                        ForEach(selectedBooks) { book in
                            let pct = Int((book.progress * 100).rounded())
                            HStack(spacing: 5) {
                                Text(book.title)
                                    .font(.yy(size: 11.5, weight: .medium))
                                    .foregroundStyle(tint.accent)
                                    .lineLimit(1)
                                if pct > 0 {
                                    Text("\(pct)%")
                                        .font(.yy(size: 10))
                                        .monospacedDigit()
                                        .foregroundStyle(tint.accent.opacity(0.75))
                                }
                                Button {
                                    withAnimation(YYMotion.quick) {
                                        selectedBookIDs.removeAll { $0 == book.id }
                                    }
                                } label: {
                                    PodcastGlyphView(glyph: .xmark, size: 8.5, lineWidth: 1.7)
                                        .foregroundStyle(tint.accent.opacity(0.75))
                                        .frame(width: 14, height: 14)
                                        .contentShape(.rect)
                                }
                                .buttonStyle(.plain)
                            }
                            .padding(.leading, 10)
                            .padding(.trailing, 6)
                            .frame(height: 24)
                            .background(YYColor.card, in: Capsule())
                            .overlay(
                                Capsule().strokeBorder(PodcastStyle.accentRing(tint, scheme), lineWidth: 0.5)
                            )
                            .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1, y: 0.5)
                        }
                    }
                    .padding(.vertical, 1)
                }
            }

            // Stationery Card: Top Search + Filter Strip + Compact 2-Column Scrollable Book Grid
            PodcastStationeryCard(cornerRadius: 13) {
                VStack(spacing: 0) {
                    // Recessed matte search + segmented filter tabs (Homepage twin)
                    HStack(spacing: 8) {
                        HStack(spacing: 6) {
                            Image(systemName: "magnifyingglass")
                                .font(.system(size: 11, weight: .medium))
                                .foregroundStyle(YYColor.t3)

                            TextField(
                                "",
                                text: $bookQuery,
                                prompt: Text(String(localized: "搜书名、作者（共 \(allBooks.count) 本）…"))
                                    .foregroundStyle(YYColor.t3.opacity(0.78))
                            )
                            .textFieldStyle(.plain)
                            .font(.yy(size: 12))
                            .foregroundStyle(YYColor.t1)

                            if !bookQuery.isEmpty {
                                Button { bookQuery = "" } label: {
                                    PodcastGlyphView(glyph: .xmark, size: 9, lineWidth: 1.7)
                                        .foregroundStyle(YYColor.t3)
                                        .frame(width: 16, height: 16)
                                        .contentShape(.rect)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                        .padding(.horizontal, 9)
                        .frame(height: 28)
                        .background(
                            RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .fill(scheme == .dark ? Color.white.opacity(0.06) : Color(yy: YYRGBA(236, 234, 227)))
                        )

                        HStack(spacing: 2) {
                            ForEach(BookFilter.allCases, id: \.self) { tab in
                                let on = bookFilter == tab && bookQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                Button {
                                    withAnimation(YYMotion.quick) {
                                        bookQuery = ""
                                        bookFilter = tab
                                    }
                                } label: {
                                    Text(tab.label)
                                        .font(.yy(size: 11, weight: on ? .medium : .regular))
                                        .foregroundStyle(on ? YYColor.t1 : YYColor.t3)
                                        .padding(.horizontal, 8)
                                        .frame(height: 24)
                                        .background {
                                            if on {
                                                RoundedRectangle(cornerRadius: 6.5, style: .continuous)
                                                    .fill(YYColor.card)
                                                    .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 0.5)
                                                    .matchedGeometryEffect(id: "bookFilterPill", in: filterSelection)
                                            }
                                        }
                                        .contentShape(.rect)
                                }
                                .buttonStyle(.plain)
                            }
                        }
                        .padding(2)
                        .background(
                            RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .fill(scheme == .dark ? Color.white.opacity(0.06) : Color(yy: YYRGBA(236, 234, 227)))
                        )
                        .fixedSize()
                    }
                    .padding(.horizontal, 10)
                    .frame(height: 42)
                    .background(PodcastStyle.leafSurface(scheme))

                    Rectangle().fill(PodcastStyle.hairline(scheme)).frame(height: 0.5)

                    // Compact 2-column grid — shows 6–8 books cleanly at once, zero wasted middle space!
                    if allBooks.isEmpty {
                        Text("书架里还没有书，将根据你的选题直接生成。")
                            .font(.yy(size: 12))
                            .foregroundStyle(YYColor.t3)
                            .frame(maxWidth: .infinity, minHeight: 132, alignment: .center)
                    } else if filteredBooks.isEmpty {
                        VStack(spacing: 6) {
                            Text(
                                bookQuery.isEmpty
                                    ? String(localized: "「\(bookFilter.label)」里暂时没有书")
                                    : String(localized: "没有找到「\(bookQuery)」")
                            )
                            .font(.yy(size: 12))
                            .foregroundStyle(YYColor.t3)

                            if bookFilter != .all || !bookQuery.isEmpty {
                                PodcastPaperPill(title: String(localized: "看全部书目"), height: 24, horizontalPadding: 10) {
                                    bookQuery = ""
                                    bookFilter = .all
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, minHeight: 148, alignment: .center)
                    } else {
                        let rowCount = max(1, (filteredBooks.count + 1) / 2)
                        let naturalH = CGFloat(rowCount) * 46 + CGFloat(max(0, rowCount - 1)) * 8 + 20
                        let maxH: CGFloat = showsSelectedTokenBar ? 134 : 160
                        let needsScroll = naturalH > maxH
                        ScrollView {
                            LazyVGrid(columns: gridCols, spacing: 8) {
                                ForEach(filteredBooks) { book in
                                    bookGridTile(book, selected: selectedBookIDs.contains(book.id))
                                }
                            }
                            .padding(10)
                        }
                        .scrollIndicators(.hidden)
                        .frame(height: min(maxH, naturalH))
                        .mask {
                            if needsScroll {
                                VStack(spacing: 0) {
                                    Rectangle()
                                    LinearGradient(colors: [.black, .black.opacity(0.08)], startPoint: .top, endPoint: .bottom)
                                        .frame(height: 14)
                                }
                            } else {
                                Rectangle()
                            }
                        }
                    }
                }
                .clipShape(RoundedRectangle(cornerRadius: 13, style: .continuous))
            }
        }
    }

    private func bookGridTile(_ book: BookSummary, selected: Bool) -> some View {
        let hovered = hoveredBookID == book.id
        let pct = Int((book.progress * 100).rounded())
        let author = book.authors.first ?? ""
        let statusTag: String = {
            if book.status == .finished { return String(localized: "读过") }
            if pct > 0 { return "\(pct)%" }
            return book.status == .want ? String(localized: "想读") : String(localized: "未读")
        }()
        let subline = [author, statusTag].filter { !$0.isEmpty }.joined(separator: " · ")

        return Button {
            withAnimation(YYMotion.quick) {
                if selected {
                    selectedBookIDs.removeAll { $0 == book.id }
                } else {
                    if kind == .bookGuide {
                        selectedBookIDs = [book.id]
                    } else {
                        selectedBookIDs.append(book.id)
                    }
                }
            }
        } label: {
            HStack(spacing: 9) {
                BookCover(book: book, bleed: true)
                    .frame(width: 22, height: 30)
                    .clipShape(RoundedRectangle(cornerRadius: 3, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 3, style: .continuous)
                            .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
                    )

                VStack(alignment: .leading, spacing: 2) {
                    Text(book.title)
                        .font(.yy(size: 12.5, weight: selected ? .medium : .regular))
                        .foregroundStyle(selected ? tint.accent : YYColor.t1)
                        .lineLimit(1)
                    Text(subline)
                        .font(.yy(size: 10.5))
                        .monospacedDigit()
                        .foregroundStyle(selected ? tint.accent.opacity(0.75) : YYColor.t3)
                        .lineLimit(1)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                if selected {
                    Image(systemName: "checkmark")
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(tint.accent)
                        .frame(width: 16)
                }
            }
            .padding(.horizontal, 9)
            .frame(height: 46)
            .background(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .fill(
                        selected
                            ? PodcastStyle.rowTintWash(tint, scheme)
                            : (hovered ? YYColor.inset.opacity(0.65) : PodcastStyle.leafSurface(scheme))
                    )
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10, style: .continuous)
                    .strokeBorder(
                        selected ? PodcastStyle.accentRing(tint, scheme) : PodcastStyle.softDivider(scheme),
                        lineWidth: 0.5
                    )
            )
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { over in
            hoveredBookID = over ? book.id : (hoveredBookID == book.id ? nil : hoveredBookID)
        }
    }

    private var vocabPreviewCard: some View {
        let words = ((try? library.core.words()) ?? []).filter { !$0.known }
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("生词素材")
                    .font(PodcastStyle.songti(14))
                    .foregroundStyle(YYColor.t1)
                Text(words.isEmpty ? String(localized: "通用英语精听") : String(localized: "自动编入自然对话"))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
                Spacer()
                if !words.isEmpty {
                    Text(String(localized: "共 \(min(15, words.count)) 词"))
                        .font(.yy(size: 11, weight: .medium))
                        .monospacedDigit()
                        .foregroundStyle(tint.accent)
                }
            }
            .padding(.horizontal, 2)

            PodcastStationeryCard(cornerRadius: 13) {
                VStack(alignment: .leading, spacing: 8) {
                    Text(words.isEmpty
                         ? String(localized: "生词本目前为空，将生成一期通用英语精听")
                         : String(localized: "待复习 \(min(15, words.count)) 个生词将自然编入双人对话"))
                        .font(.yy(size: 13, weight: .medium))
                        .foregroundStyle(YYColor.t1)

                    if words.isEmpty {
                        Text("阅读书页或收听英文播客时点词收藏，待复习生词会自动汇入这里，在真实语境里反复复现。")
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineSpacing(3)
                            .fixedSize(horizontal: false, vertical: true)
                    } else {
                        Text(words.prefix(14).map(\.word).joined(separator: " · "))
                            .font(.yy(size: 12))
                            .foregroundStyle(YYColor.t2)
                            .lineSpacing(4)
                            .lineLimit(3)
                    }
                    Spacer(minLength: 0)
                }
                .padding(14)
                .frame(maxWidth: .infinity, minHeight: 158, alignment: .topLeading)
            }
        }
    }

    private func generate() {
        Task {
            if let issue = await library.podcastHub.generateStudioEpisode(
                kind: kind,
                bookIDs: selectedBookIDs,
                minutes: minutes,
                topic: topic,
                noSpoiler: noSpoiler,
                voiceStyle: voiceStyle
            ) {
                onCreated(issue)
                onClose()
            }
        }
    }
}

/// Segmented Capsule Duration Scrubber — direct visual sibling of `PodcastReaderView`'s segmented track:
/// - 5 clearly separated rounded capsule time blocks (`5–10` | `10–15` | `15–30` | `30–45` | `45–60` min) with crisp `7pt` gaps.
/// - Dynamically extends to `60–90` (or `3–10`) whenever the user types a custom value outside `5...60`, so the
///   knob position and scale labels NEVER contradict the readout (`60 分钟`, `75 分钟`, `90 分钟`, `3 分钟` all land honestly!).
/// - Deep accent sage fill across completed/active segments, warm-stone gray for remaining segments, and a crisp
///   porcelain-ringed dark-ink bead knob (`YYColor.t1` inside `YYColor.card` halo with soft drop shadow).
private struct StudioDurationPickerRow: View {
    @Binding var minutes: UInt32

    private struct TimeSegment: Identifiable {
        let start: UInt32
        let end: UInt32
        var id: String { "\(start)-\(end)" }
    }

    private let minMinutes: UInt32 = 3
    private let maxMinutes: UInt32 = 90
    private let segmentGap: CGFloat = 7
    private let trackHeight: CGFloat = 6.0
    private let horizontalInset: CGFloat = 7.5

    @State private var isDragging = false
    @State private var frozenStops: [UInt32]?
    @State private var isHoveringTrack = false
    @State private var isHoveringReadout = false
    @State private var editingNumber = false
    @State private var textValue = ""
    @FocusState private var inputFocused: Bool

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var computedStops: [UInt32] {
        var list: [UInt32] = [minutes < 5 ? 3 : 5, 10, 15, 30, 45, 60]
        if minutes > 60 {
            list.append(90)
        }
        return list
    }

    private var activeStops: [UInt32] {
        frozenStops ?? computedStops
    }

    private var activeSegments: [TimeSegment] {
        let s = activeStops
        guard s.count >= 2 else { return [] }
        return (0..<(s.count - 1)).map { TimeSegment(start: s[$0], end: s[$0 + 1]) }
    }

    private var unfilledColor: Color {
        scheme == .dark ? Color.white.opacity(0.16) : Color(yy: YYRGBA(45, 40, 34, 0.16))
    }

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            Text("时长")
                .font(.yy(size: 12, weight: .medium))
                .foregroundStyle(YYColor.t2)
                .frame(width: 34, alignment: .leading)

            segmentedTrack
                .frame(maxWidth: .infinity)
                .frame(height: 38)

            durationPill
                .frame(width: 72, height: 28)
        }
        .onAppear { textValue = "\(minutes)" }
        .onChange(of: minutes) { _, v in
            if !editingNumber { textValue = "\(v)" }
        }
    }

    // MARK: - Segmented Capsule Track + Dark-Ink Porcelain-Halo Knob

    private var segmentedTrack: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let stops = activeStops
            let segments = activeSegments
            let count = max(1, segments.count)
            let span = max(1, w - horizontalInset * 2)
            let segW = max(1, (span - segmentGap * CGFloat(count - 1)) / CGFloat(count))
            let trackY: CGFloat = 13.5
            let knobX = xPosition(for: minutes, segments: segments, segW: segW)

            ZStack(alignment: .topLeading) {
                // Distinct rounded capsule segments separated by unmistakable 7pt gaps
                HStack(spacing: segmentGap) {
                    ForEach(segments) { seg in
                        let fillRatio = fillFraction(for: seg, value: minutes)
                        ZStack(alignment: .leading) {
                            Capsule()
                                .fill(unfilledColor)
                                .frame(width: segW, height: trackHeight)

                            if fillRatio > 0 {
                                Capsule()
                                    .fill(tint.accent)
                                    .frame(width: max(trackHeight, segW * fillRatio), height: trackHeight)
                            }
                        }
                        .frame(width: segW, height: trackHeight)
                        .clipShape(Capsule())
                    }
                }
                .offset(x: horizontalInset, y: trackY - trackHeight / 2)
                .allowsHitTesting(false)

                // Exact `PodcastReaderView` dark-ink bead framed by crisp white porcelain ring + soft shadow
                ZStack {
                    Circle()
                        .fill(YYColor.card)
                        .frame(width: isDragging ? 15.5 : 14, height: isDragging ? 15.5 : 14)
                        .shadow(
                            color: Color.black.opacity(scheme == .dark ? 0.45 : 0.20),
                            radius: isDragging ? 4.5 : 3.2,
                            y: 1
                        )
                    Circle()
                        .fill(YYColor.t1)
                        .frame(width: isDragging ? 9.5 : 8.5, height: isDragging ? 9.5 : 8.5)
                }
                .position(x: knobX, y: trackY)
                .animation(isDragging ? nil : YYMotion.quick, value: knobX)
                .animation(YYMotion.quick, value: isDragging)
                .allowsHitTesting(false)

                // Crisp boundary labels (`5  10  15  30  45  60`, expanding cleanly to `90` / `3` when custom)
                ForEach(Array(stops.enumerated()), id: \.element) { idx, stop in
                    let lx = stopX(index: idx, segmentCount: segments.count, segW: segW)
                    let active = minutes == stop
                    Text("\(stop)")
                        .font(.yy(size: 10, weight: active ? .semibold : .regular))
                        .monospacedDigit()
                        .foregroundStyle(active ? tint.accent : YYColor.t3.opacity(isHoveringTrack ? 0.92 : 0.78))
                        .position(x: lx, y: 29.5)
                        .allowsHitTesting(false)
                }
            }
            .contentShape(.rect)
            .onHover { isHoveringTrack = $0 }
            .gesture(
                DragGesture(minimumDistance: 0, coordinateSpace: .local)
                    .onChanged { value in
                        if editingNumber { commitText() }
                        if frozenStops == nil { frozenStops = computedStops }
                        let dragStops = frozenStops ?? stops
                        let dragSegs = (0..<(dragStops.count - 1)).map { TimeSegment(start: dragStops[$0], end: dragStops[$0 + 1]) }
                        let dragSegW = max(1, (span - segmentGap * CGFloat(dragSegs.count - 1)) / CGFloat(dragSegs.count))

                        let moved = abs(value.translation.width) > 2
                        if moved && !isDragging { isDragging = true }
                        let snapPx: CGFloat = moved ? 5.5 : 10
                        let next = minutesFor(x: value.location.x, stops: dragStops, segments: dragSegs, segW: dragSegW, snapPixels: snapPx)
                        if next != minutes {
                            if moved {
                                minutes = next
                            } else {
                                withAnimation(YYMotion.quick) { minutes = next }
                            }
                        }
                    }
                    .onEnded { _ in
                        isDragging = false
                        withAnimation(YYMotion.quick) {
                            frozenStops = nil
                        }
                    }
            )
            .accessibilityElement(children: .ignore)
            .accessibilityLabel(String(localized: "节目时长"))
            .accessibilityValue(String(localized: "\(minutes) 分钟"))
            .accessibilityAdjustableAction { direction in
                switch direction {
                case .increment: minutes = min(maxMinutes, minutes + 1)
                case .decrement: minutes = max(minMinutes, minutes - 1)
                @unknown default: break
                }
            }
        }
    }

    // MARK: - Unified Editable Duration Capsule (`10 分钟`, Click to Type 3–90)

    private var durationPill: some View {
        Group {
            if editingNumber {
                HStack(alignment: .firstTextBaseline, spacing: 2) {
                    TextField("", text: $textValue)
                        .textFieldStyle(.plain)
                        .font(.yy(size: 12.5, weight: .semibold))
                        .monospacedDigit()
                        .multilineTextAlignment(.trailing)
                        .foregroundStyle(tint.accent)
                        .frame(width: 26)
                        #if os(iOS)
                        .keyboardType(.numberPad)
                        #endif
                        .focused($inputFocused)
                        .onSubmit(commitText)
                        .onChange(of: inputFocused) { _, focused in
                            if !focused && editingNumber { commitText() }
                        }
                    Text("分钟")
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t2)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(RoundedRectangle(cornerRadius: 8, style: .continuous).fill(YYColor.card))
                .overlay(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .strokeBorder(PodcastStyle.accentRing(tint, scheme), lineWidth: 1)
                )
            } else {
                Button {
                    textValue = "\(minutes)"
                    editingNumber = true
                    DispatchQueue.main.async { inputFocused = true }
                } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 2.5) {
                        Text("\(minutes)")
                            .font(.yy(size: 12.5, weight: .semibold))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t1)
                        Text("分钟")
                            .font(.yy(size: 11))
                            .foregroundStyle(YYColor.t2)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(isHoveringReadout ? YYColor.card : PodcastStyle.leafSurface(scheme))
                    )
                    .overlay(
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .strokeBorder(
                                isHoveringReadout
                                    ? PodcastStyle.accentRing(tint, scheme)
                                    : PodcastStyle.pillBorder(scheme),
                                lineWidth: 0.5
                            )
                    )
                    .contentShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
                }
                .buttonStyle(YYPressStyle(scale: 0.97))
                .onHover { isHoveringReadout = $0 }
                .help(String(localized: "点击可直接输入 3–90 分钟"))
            }
        }
    }

    // MARK: - Segment Geometry Mapping

    private func fillFraction(for seg: TimeSegment, value: UInt32) -> CGFloat {
        if value >= seg.end { return 1 }
        if value <= seg.start { return 0 }
        return CGFloat(value - seg.start) / CGFloat(seg.end - seg.start)
    }

    private func stopX(index: Int, segmentCount: Int, segW: CGFloat) -> CGFloat {
        if index <= 0 { return horizontalInset }
        if index >= segmentCount {
            return horizontalInset + CGFloat(segmentCount) * segW + CGFloat(segmentCount - 1) * segmentGap
        }
        // Center of gap between segment (index - 1) and segment (index)
        let prevRight = horizontalInset + CGFloat(index) * segW + CGFloat(index - 1) * segmentGap
        return prevRight + segmentGap / 2
    }

    private func xPosition(for value: UInt32, segments: [TimeSegment], segW: CGFloat) -> CGFloat {
        guard let first = segments.first, let last = segments.last else { return horizontalInset }
        if value <= first.start { return stopX(index: 0, segmentCount: segments.count, segW: segW) }
        if value >= last.end { return stopX(index: segments.count, segmentCount: segments.count, segW: segW) }

        for (i, seg) in segments.enumerated() {
            if value == seg.end && i < segments.count - 1 {
                return stopX(index: i + 1, segmentCount: segments.count, segW: segW)
            }
            if value >= seg.start && value <= seg.end {
                let segLeft = horizontalInset + CGFloat(i) * (segW + segmentGap)
                let t = CGFloat(value - seg.start) / CGFloat(seg.end - seg.start)
                return segLeft + segW * t
            }
        }
        return stopX(index: 0, segmentCount: segments.count, segW: segW)
    }

    private func minutesFor(
        x: CGFloat,
        stops: [UInt32],
        segments: [TimeSegment],
        segW: CGFloat,
        snapPixels: CGFloat
    ) -> UInt32 {
        for (idx, stop) in stops.enumerated() {
            if abs(x - stopX(index: idx, segmentCount: segments.count, segW: segW)) <= snapPixels {
                return stop
            }
        }
        if x <= stopX(index: 0, segmentCount: segments.count, segW: segW) { return stops.first ?? minMinutes }
        if x >= stopX(index: segments.count, segmentCount: segments.count, segW: segW) { return stops.last ?? maxMinutes }

        for (i, seg) in segments.enumerated() {
            let segLeft = horizontalInset + CGFloat(i) * (segW + segmentGap)
            let segRight = segLeft + segW
            if x >= segLeft && x <= segRight {
                let t = (x - segLeft) / max(1, segW)
                let val = Double(seg.start) + Double(t) * Double(seg.end - seg.start)
                return UInt32(val.rounded())
            }
            if i < segments.count - 1 && x > segRight && x < segRight + segmentGap {
                return seg.end
            }
        }
        return minutes
    }

    private func commitText() {
        let digits = textValue.filter(\.isNumber)
        if let parsed = UInt32(digits) {
            withAnimation(YYMotion.quick) {
                minutes = max(minMinutes, min(maxMinutes, parsed))
            }
        }
        textValue = "\(minutes)"
        inputFocused = false
        editingNumber = false
    }
}
