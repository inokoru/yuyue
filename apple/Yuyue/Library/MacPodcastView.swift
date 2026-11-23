#if os(macOS)
import AppKit
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

/// macOS 播客首页 — 语阅宣白纸面 + 0.5pt 净白卡片 + 双栏留白书桌 + 右缘悬浮圆形抽屉入口。
/// 完整还原「左侧分组收听台（独立滚动）+ 右侧留白书桌（固定贴顶：问候 / 命令搜索条 / 5 颗圆形快捷入口 / 桌面播放卡）
/// + 右缘独立悬浮圆形书摘与声音卡抽屉」；全局纸色与点缀自动跟随「设置 · 外观」。
struct MacPodcastView: View {
    @Environment(\.shelfHeaderInset) private var headerInset
    let library: Library
    @Environment(\.openWindow) private var openWindow
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    enum ShowFilter: Hashable {
        case all
        case studio
        case show(String)
    }

    struct StudioLaunch: Identifiable {
        let id = UUID()
        var kind: StudioKind
        var bookIDs: [String] = []
    }

    private struct GroupSection: Identifiable {
        let id: String
        let title: String
        let meta: String
        let actionTitle: String?
        let actionAccent: Bool
        let action: (() -> Void)?
        let isNowPlayingGroup: Bool
        let issues: [PodcastIssue]
    }

    @State private var showFilter: ShowFilter = .all
    @State private var collapsedGroupIDs: Set<String> = []
    @State private var openedIssueID: String?
    @State private var adding = false
    @State private var addInitialQuery = ""
    @State private var studioSheet: StudioLaunch?
    @State private var refreshing = false
    @State private var importingOPML = false
    @State private var importingAudio = false

    // Right desk command search / paste-link state
    @State private var searchQuery = ""
    @State private var showSearchDrop = false
    @FocusState private var searchFocused: Bool
    @State private var preferredTrack: PodcastTrack = .dub

    // Floating right drawer state
    @State private var drawerOpen = false
    @State private var drawerTab: PodcastDrawerTab = .clips
    @State private var hasNewClipDot = false

    // Hover & toast feedback
    @State private var hoveredIssueID: String?
    @State private var hoveredStarterKind: StudioKind?
    @State private var hoveredDrawerTrigger: PodcastDrawerTab?
    @State private var transientToast: String?
    @State private var toastTask: Task<Void, Never>?

    private var hub: PodcastHub { library.podcastHub }

    private var looksLikeURL: Bool {
        let t = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return t.hasPrefix("http://") || t.hasPrefix("https://")
            || t.contains("xiaoyuzhou") || t.contains("podcasts.apple.com")
            || t.contains(".xml") || t.contains("/feed")
    }

    /// Distinct external show keys in stable display order (subscribed channels first, then standalone shows).
    private var externalShows: [(key: String, title: String, artworkURL: String?)] {
        var result: [(String, String, String?)] = []
        var seen = Set<String>()
        for ch in hub.channels {
            let key = ch.title.isEmpty ? ch.id : ch.title
            if seen.insert(key).inserted {
                result.append((key, key, ch.artworkUrl))
            }
        }
        for issue in hub.allIssues where issue.studioKind == nil {
            let key = issue.showTitle.isEmpty ? String(localized: "单集导入") : issue.showTitle
            if seen.insert(key).inserted {
                result.append((key, key, issue.artworkUrl))
            }
        }
        return result
    }

    var body: some View {
        Group {
            if let openedIssueID {
                PodcastReaderView(
                    library: library,
                    issueID: openedIssueID,
                    onOpenBook: { bookID in
                        if let book = library.book(bookID), library.open(book, nil) {
                            openWindow(id: "reader", value: bookID)
                        }
                    },
                    onSelectIssue: { nextID in
                        withAnimation(YYMotion.standard) { self.openedIssueID = nextID }
                    },
                    onClose: {
                        withAnimation(YYMotion.standard) { self.openedIssueID = nil }
                    }
                )
            } else {
                homeCanvas
            }
        }
        .toolbar(removing: .title)
        .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
        .sheet(isPresented: $adding) {
            PodcastAddSheet(library: library, initialQuery: addInitialQuery) {
                adding = false
                addInitialQuery = ""
            }
            .paperWindow()
        }
        .sheet(item: $studioSheet) { launch in
            PodcastStudioSheet(
                library: library,
                initialKind: launch.kind,
                preselectedBookIDs: launch.bookIDs,
                onCreated: { issue in
                    openedIssueID = issue.id
                },
                onClose: { studioSheet = nil }
            )
            .paperWindow()
        }
        .fileImporter(
            isPresented: $importingOPML,
            allowedContentTypes: [UTType.xml, UTType(filenameExtension: "opml") ?? .xml]
        ) { res in
            if case .success(let url) = res {
                Task {
                    let count = await hub.importOPML(from: url)
                    showToast(String(localized: "已导入 \(count) 档播客订阅"))
                }
            }
        }
        .fileImporter(
            isPresented: $importingAudio,
            allowedContentTypes: [.audio, .mp3, .mpeg4Audio]
        ) { res in
            if case .success(let url) = res {
                Task {
                    if let imported = await hub.importLocalAudio(from: url) {
                        showToast(String(localized: "已导入《\(imported.title)》"))
                    }
                }
            }
        }
        .task {
            await hub.loadDiscoveryIfNeeded()
        }
        .task {
            #if DEBUG
            switch UserDefaults.standard.string(forKey: "debugPodcast") {
            case "make":
                studioSheet = StudioLaunch(kind: .bookGuide)
            case "add":
                adding = true
            case "newsstand", "archive":
                openDrawer(.later)
            case "reader", "reader-orig", "reader-coach", "reader-interrupt":
                if let first = hub.activeIssue ?? hub.deskIssues.first {
                    openedIssueID = first.id
                }
            default:
                break
            }
            #endif
        }
        .onChange(of: searchFocused) { _, focused in
            withAnimation(YYMotion.quick) {
                showSearchDrop = focused || !searchQuery.isEmpty
            }
        }
        .onKeyPress("h") {
            guard !searchFocused, openedIssueID == nil else { return .ignored }
            clipCurrentMoment()
            return .handled
        }
        .onKeyPress(.space) {
            guard !searchFocused, openedIssueID == nil, hub.activeIssue != nil else { return .ignored }
            hub.togglePlay()
            return .handled
        }
        .onKeyPress(.escape) {
            if drawerOpen {
                closeDrawer()
                return .handled
            }
            if showSearchDrop || searchFocused {
                dismissSearch()
                return .handled
            }
            return .ignored
        }
    }

    // MARK: - Home Workspace Canvas

    private var homeCanvas: some View {
        GeometryReader { geo in
            let w = geo.size.width
            let h = geo.size.height
            // Keep showTray, 「收听台」, and all left group cards on ONE razor-straight left edge,
            // cleanly clearing MacShelfView's 28pt sidebarButton (icon visual right edge ~37pt).
            let leadPad: CGFloat = headerInset > 0 ? (w >= 900 ? 52 : 50) : (w >= 1040 ? 34 : 24)
            let trailPad: CGFloat = 60
            let colGap: CGFloat = w >= 1060 ? 40 : (w >= 900 ? 28 : 20)
            let usable = max(520, w - leadPad - trailPad - colGap)
            let rightW: CGFloat = {
                if usable >= 900 { return min(440, usable * 0.426) }
                if usable >= 770 { return max(374, usable * 0.45) }
                return min(346, max(316, usable * 0.48))
            }()
            let leftW = max(280, usable - rightW)

            ZStack(alignment: .trailing) {
                VStack(alignment: .leading, spacing: 0) {
                    // Fixed top bar aligned to MacShelfView.lineY (y = 34.0pt from window top):
                    // height 52 + top padding 8 => center y = 34.0pt, exact match to sidebarButton (y = 23..45).
                    topBar(leadPad: leadPad, trailPad: trailPad)
                        .frame(height: 52)
                        .padding(.top, 8)
                        .padding(.bottom, 10)

                    // Asymmetric 2-column workspace: left station scrolls independently, right desk stays pinned
                    HStack(alignment: .top, spacing: colGap) {
                        ScrollView(.vertical) {
                            leftStationColumn
                                .padding(.bottom, 36)
                        }
                        .scrollIndicators(.never)
                        .frame(width: leftW)

                        ScrollView(.vertical) {
                            rightDeskColumn(width: rightW)
                                .padding(.bottom, 36)
                        }
                        .scrollIndicators(.never)
                        .scrollBounceBehavior(.basedOnSize)
                        .frame(width: rightW)
                    }
                    .padding(.leading, leadPad)
                    .padding(.trailing, trailPad)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                }
                .contentShape(.rect)
                .onTapGesture {
                    if showSearchDrop || searchFocused { dismissSearch() }
                }

                // Detached Right-Edge Circular Drawer Triggers (centered in the 60pt right gutter: 12pt | 36pt | 12pt)
                if !drawerOpen {
                    rightEdgeCircularTriggers
                        .padding(.trailing, 12)
                        .offset(y: -h * 0.04)
                        .transition(.move(edge: .trailing).combined(with: .opacity))
                }

                // Floating Stationery Side Drawer
                if drawerOpen {
                    Color.black.opacity(scheme == .dark ? 0.28 : 0.08)
                        .ignoresSafeArea()
                        .onTapGesture { closeDrawer() }
                        .transition(.opacity)

                    PodcastDrawerContentView(
                        library: library,
                        tab: $drawerTab,
                        onOpenIssue: { id in
                            withAnimation(YYMotion.standard) { openedIssueID = id }
                        },
                        onClose: closeDrawer
                    )
                    .frame(width: 386)
                    .clipShape(RoundedRectangle(cornerRadius: 15, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 15, style: .continuous)
                            .strokeBorder(scheme == .dark ? Color.white.opacity(0.12) : YYColor.line, lineWidth: 0.5)
                    )
                    .shadow(
                        color: scheme == .dark ? Color.black.opacity(0.55) : Color(yy: YYRGBA(30, 26, 20, 0.16)),
                        radius: 26,
                        y: 10
                    )
                    .padding(14)
                    .transition(.move(edge: .trailing).combined(with: .opacity))
                }

                // Subtle floating bottom confirmation toast
                if let transientToast {
                    VStack {
                        Spacer()
                        Text(transientToast)
                            .font(.yy(size: 12.5, weight: .medium))
                            .foregroundStyle(YYColor.card)
                            .padding(.horizontal, 16)
                            .padding(.vertical, 8)
                            .background(YYColor.t1, in: Capsule())
                            .shadow(color: Color.black.opacity(0.16), radius: 12, y: 4)
                            .padding(.bottom, 22)
                    }
                    .frame(maxWidth: .infinity)
                    .allowsHitTesting(false)
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                }
            }
        }
        .background(YYColor.bg)
        .ignoresSafeArea(.container, edges: .top)
        .animation(YYMotion.standard, value: drawerOpen)
        .animation(YYMotion.quick, value: transientToast)
    }

    // MARK: - Top Bar (Recessed Matte Bookplate Tray + Single Clean Add Button)

    private func topBar(leadPad: CGFloat, trailPad: CGFloat) -> some View {
        HStack(alignment: .center, spacing: 12) {
            ViewThatFits(in: .horizontal) {
                showTrayContent
                ScrollView(.horizontal, showsIndicators: false) {
                    showTrayContent
                }
                .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.leading, leadPad)

            Spacer(minLength: 12)

            HStack(spacing: 7) {
                if !hub.channels.isEmpty {
                    PodcastCircleIconButton(
                        symbol: "arrow.clockwise",
                        size: 32,
                        iconSize: 13,
                        active: refreshing,
                        accessibilityLabel: String(localized: "刷新订阅")
                    ) {
                        guard !refreshing else { return }
                        refreshing = true
                        Task {
                            await hub.refreshAllChannels()
                            refreshing = false
                            showToast(String(localized: "已刷新 \(hub.channels.count) 档订阅"))
                        }
                    }
                    .help(String(localized: "刷新全部已订阅频道"))
                }

                PodcastCircleIconButton(
                    glyph: .plus,
                    size: 32,
                    iconSize: 14.5,
                    accessibilityLabel: String(localized: "添加节目或导入单集")
                ) {
                    addInitialQuery = ""
                    adding = true
                }
                .help(String(localized: "添加节目 · 粘贴链接 / OPML / 本地音频"))
            }
            .padding(.trailing, trailPad)
        }
    }

    private var showTrayContent: some View {
        HStack(spacing: 3) {
            trayAllButton

            trayShowButton(
                filter: .studio,
                title: String(localized: "书房电台"),
                artworkURL: nil,
                isStudio: true
            )

            ForEach(externalShows.prefix(7), id: \.key) { item in
                trayShowButton(
                    filter: .show(item.key),
                    title: item.title,
                    artworkURL: item.artworkURL,
                    isStudio: false
                )
            }
        }
        .padding(3)
        .background(YYColor.inset, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .strokeBorder(YYColor.line, lineWidth: 0.5)
        )
        .fixedSize(horizontal: true, vertical: true)
    }

    private var trayAllButton: some View {
        let on = showFilter == .all
        return Button {
            withAnimation(YYMotion.quick) { showFilter = .all }
        } label: {
            PodcastGlyphView(glyph: .allGrid, size: 13.5, lineWidth: 1.45)
                .foregroundStyle(on ? tint.accent : YYColor.t3)
                .frame(width: 30, height: 28)
                .background {
                    if on {
                        RoundedRectangle(cornerRadius: 8, style: .continuous)
                            .fill(YYColor.card)
                            .overlay(
                                RoundedRectangle(cornerRadius: 8, style: .continuous)
                                    .strokeBorder(PodcastStyle.pillBorder(scheme), lineWidth: 0.5)
                            )
                            .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                    }
                }
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(String(localized: "全部节目"))
        .accessibilityLabel(String(localized: "全部节目"))
    }

    private func trayShowButton(
        filter target: ShowFilter,
        title: String,
        artworkURL: String?,
        isStudio: Bool
    ) -> some View {
        let on = showFilter == target
        return Button {
            withAnimation(YYMotion.quick) {
                showFilter = on ? .all : target
            }
        } label: {
            HStack(spacing: 6) {
                PodcastShowMark(title: title, artworkURL: artworkURL, isStudio: isStudio, size: 19)
                if on {
                    Text(title)
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .padding(.trailing, 3)
                }
            }
            .padding(.horizontal, 5.5)
            .frame(minWidth: 30, minHeight: 28)
            .background {
                if on {
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(YYColor.card)
                        .overlay(
                            RoundedRectangle(cornerRadius: 8, style: .continuous)
                                .strokeBorder(PodcastStyle.pillBorder(scheme), lineWidth: 0.5)
                        )
                        .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                }
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(title)
    }

    // MARK: - Left Column: Structured Collapsible Podcast Groups

    private var groupSections: [GroupSection] {
        let inProgress = hub.inProgressIssues
        let inProgressIDs = Set(inProgress.map(\.id))
        var sections: [GroupSection] = []

        switch showFilter {
        case .all:
            if !inProgress.isEmpty {
                sections.append(GroupSection(
                    id: "playing-now",
                    title: String(localized: "正在听"),
                    meta: String(localized: "\(inProgress.count) 期未完"),
                    actionTitle: String(localized: "接着听"),
                    actionAccent: false,
                    action: {
                        if let first = inProgress.first { hub.selectAndPlay(first) }
                    },
                    isNowPlayingGroup: true,
                    issues: inProgress
                ))
            }

            let studioAll = hub.studioIssues
            let studioRest = studioAll.filter { !inProgressIDs.contains($0.id) }
            let studioShown = studioRest.isEmpty && !studioAll.isEmpty ? [] : studioRest
            if studioAll.isEmpty || !studioShown.isEmpty {
                let studioMeta: String = if studioAll.isEmpty {
                    String(localized: "由你的书架酿制")
                } else if studioShown.count < studioAll.count {
                    String(localized: "其余 \(studioShown.count) 期 · 由你的书架酿制")
                } else {
                    String(localized: "\(studioShown.count) 期 · 由你的书架酿制")
                }
                sections.append(GroupSection(
                    id: "studio",
                    title: String(localized: "书房电台"),
                    meta: studioMeta,
                    actionTitle: String(localized: "+ 酿一期"),
                    actionAccent: true,
                    action: { studioSheet = StudioLaunch(kind: .weeklyDigest) },
                    isNowPlayingGroup: false,
                    issues: studioShown
                ))
            }

            for show in externalShows {
                let allShow = hub.allIssues.filter {
                    $0.studioKind == nil && ($0.showTitle.isEmpty ? String(localized: "单集导入") : $0.showTitle) == show.key
                }
                // Do not duplicate an episode already shown in 「正在听」
                let rest = allShow.filter { !inProgressIDs.contains($0.id) }
                guard !rest.isEmpty else { continue }
                let prefix = rest.count < allShow.count
                    ? String(localized: "其余 \(rest.count) 期")
                    : String(localized: "\(rest.count) 期")
                let hasEnglish = rest.contains { $0.language.lowercased().hasPrefix("en") }
                let suffix = hasEnglish ? String(localized: " · 原声线中文就绪") : String(localized: " · 中文深度访谈")
                sections.append(GroupSection(
                    id: "show-\(show.key)",
                    title: show.title,
                    meta: prefix + suffix,
                    actionTitle: nil,
                    actionAccent: false,
                    action: nil,
                    isNowPlayingGroup: false,
                    issues: Array(rest.prefix(5))
                ))
            }

        case .studio:
            let studioAll = hub.studioIssues
            sections.append(GroupSection(
                id: "studio",
                title: String(localized: "书房电台"),
                meta: studioAll.isEmpty
                    ? String(localized: "由你的书架酿制")
                    : String(localized: "\(studioAll.count) 期 · 防剧透锁"),
                actionTitle: String(localized: "+ 酿一期"),
                actionAccent: true,
                action: { studioSheet = StudioLaunch(kind: .weeklyDigest) },
                isNowPlayingGroup: false,
                issues: studioAll
            ))

        case .show(let key):
            let list = hub.allIssues.filter {
                $0.studioKind == nil && ($0.showTitle.isEmpty ? String(localized: "单集导入") : $0.showTitle) == key
            }
            sections.append(GroupSection(
                id: "show-\(key)",
                title: key,
                meta: String(localized: "\(list.count) 期"),
                actionTitle: String(localized: "查看全部"),
                actionAccent: false,
                action: { withAnimation(YYMotion.quick) { showFilter = .all } },
                isNowPlayingGroup: false,
                issues: list
            ))
        }

        return sections
    }

    private var leftStationColumn: some View {
        let sections = groupSections
        let showDiscover = showFilter == .all && externalShows.isEmpty
        let allIDs = sections.map(\.id) + (showDiscover ? ["discover"] : [])
        let allCollapsed = !allIDs.isEmpty && allIDs.allSatisfy { collapsedGroupIDs.contains($0) }
        let headerTitle: String = switch showFilter {
        case .all: String(localized: "收听台")
        case .studio: String(localized: "书房电台")
        case .show(let name): name
        }
        let groupCount = sections.count + (showDiscover ? 1 : 0)

        return VStack(alignment: .leading, spacing: 12) {
            HStack(alignment: .center, spacing: 10) {
                HStack(alignment: .firstTextBaseline, spacing: 9) {
                    Text(headerTitle)
                        .font(PodcastStyle.songti(16))
                        .foregroundStyle(YYColor.t1)
                    Text(String(localized: "\(groupCount) 组 · \(hub.allIssues.count) 期"))
                        .font(.yy(size: 11.5))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t3)
                }
                Spacer()
                PodcastPaperPill(
                    title: allCollapsed ? String(localized: "全部展开") : String(localized: "全部折叠"),
                    symbol: allCollapsed ? "chevron.right" : nil,
                    glyph: allCollapsed ? nil : .chevronDown
                ) {
                    withAnimation(YYMotion.quick) {
                        if allCollapsed {
                            collapsedGroupIDs.removeAll()
                        } else {
                            collapsedGroupIDs = Set(allIDs)
                        }
                    }
                }
            }
            .frame(height: 28)

            ForEach(sections) { section in
                groupCard(section)
            }

            if showDiscover {
                discoverGroupCard
            }
        }
    }

    private func groupCard(_ section: GroupSection) -> some View {
        let collapsed = collapsedGroupIDs.contains(section.id)
        return PodcastStationeryCard(cornerRadius: 13) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .center, spacing: 8) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Button {
                            withAnimation(YYMotion.quick) { toggleCollapse(section.id) }
                        } label: {
                            Text(section.title)
                                .font(PodcastStyle.songti(15))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                        }
                        .buttonStyle(.plain)

                        Text(section.meta)
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    }

                    Spacer(minLength: 8)

                    if let actionTitle = section.actionTitle, let action = section.action {
                        PodcastPaperPill(title: actionTitle, accent: section.actionAccent, action: action)
                    }

                    Button {
                        withAnimation(YYMotion.quick) { toggleCollapse(section.id) }
                    } label: {
                        PodcastGlyphView(glyph: .chevronDown, size: 12, lineWidth: 1.6)
                            .foregroundStyle(YYColor.t3)
                            .rotationEffect(.degrees(collapsed ? -90 : 0))
                            .frame(width: 26, height: 24)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.bottom, collapsed ? 0 : 6)

                if !collapsed {
                    if section.id == "studio" && section.issues.isEmpty {
                        studioStarterRows
                    } else {
                        VStack(spacing: 0) {
                            ForEach(Array(section.issues.enumerated()), id: \.element.id) { idx, issue in
                                let isCurrent = hub.activeIssueID == issue.id
                                let prevIsCurrent = idx > 0 && hub.activeIssueID == section.issues[idx - 1].id
                                if idx > 0 && !isCurrent && !prevIsCurrent {
                                    Rectangle()
                                        .fill(scheme == .dark ? Color.white.opacity(0.05) : YYColor.line.opacity(0.65))
                                        .frame(height: 0.5)
                                }
                                episodeRow(issue, showProgress: section.isNowPlayingGroup || issue.positionSec > 0)
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 13)
            .padding(.bottom, collapsed ? 13 : 8)
        }
    }

    private func episodeRow(_ issue: PodcastIssue, showProgress: Bool) -> some View {
        let isCurrent = hub.activeIssueID == issue.id
        let isPlaying = isCurrent && hub.isPlaying
        let isHovered = hoveredIssueID == issue.id
        let dur = isCurrent && hub.duration > 0 ? hub.duration : Double(issue.durationSec)
        let pos = isCurrent ? hub.currentTime : issue.positionSec
        let frac = dur > 0 ? min(1, max(0.04, pos / dur)) : 0
        let isSavedLater = issue.placement == .archive

        let noteText: String = {
            if let kind = issue.studioKind {
                return "\(kind.title) · \(String(localized: "防剧透"))"
            }
            if issue.language.lowercased().hasPrefix("en") {
                return PodcastFormat.trackLabel(issue.track)
            }
            return String(localized: "中文原声")
        }()

        let tailMeta: String = {
            if showProgress, dur > 0 {
                return PodcastFormat.remaining(position: pos, duration: dur)
            }
            let d = PodcastFormat.shortDate(issue.publishedAt)
            let len = PodcastFormat.compactDuration(issue.durationSec)
            return [len, d].filter { !$0.isEmpty && $0 != "—" }.joined(separator: " · ")
        }()

        return HStack(spacing: 11) {
            Button {
                hub.selectAndPlay(issue)
            } label: {
                Group {
                    if isPlaying {
                        PodcastEqualizerBars(active: true)
                    } else {
                        Image(systemName: "play.fill")
                            .font(.system(size: 8.5, weight: .semibold))
                            .foregroundStyle(isCurrent || isHovered ? tint.accent : YYColor.t3.opacity(0.55))
                    }
                }
                .frame(width: 14, height: 22, alignment: .leading)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            PodcastShowMark(
                title: issue.studioKind != nil ? String(localized: "书房电台") : issue.showTitle,
                artworkURL: issue.artworkUrl,
                isStudio: issue.studioKind != nil,
                size: 22
            )

            VStack(alignment: .leading, spacing: 2) {
                Text(issue.title)
                    .font(.yy(size: 13.5, weight: isCurrent ? .medium : .regular))
                    .foregroundStyle(isCurrent ? tint.accent : YYColor.t1)
                    .lineLimit(1)

                HStack(spacing: 5) {
                    if showProgress {
                        Text(issue.showTitle)
                            .foregroundStyle(YYColor.t3)
                        Text("·").foregroundStyle(YYColor.t3)
                    }
                    Text(noteText)
                        .fontWeight(.regular)
                        .foregroundStyle(tint.accent)
                    if !tailMeta.isEmpty {
                        Text("·").foregroundStyle(YYColor.t3)
                        Text(tailMeta)
                            .foregroundStyle(YYColor.t3)
                    }
                }
                .font(.yy(size: 11.5))
                .lineLimit(1)

                if showProgress && dur > 0 {
                    ZStack(alignment: .leading) {
                        Capsule().fill(YYColor.inset).frame(width: 108, height: 2)
                        Capsule().fill(tint.accent).frame(width: 108 * frac, height: 2)
                    }
                    .padding(.top, 4)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)

            HStack(spacing: 3) {
                rowGlyphButton(
                    glyph: .transcriptLines,
                    active: false,
                    help: String(localized: "展开访谈书页")
                ) {
                    withAnimation(YYMotion.standard) { openedIssueID = issue.id }
                }
                rowGlyphButton(
                    glyph: isSavedLater ? .bookmarkFilled : .bookmark,
                    active: isSavedLater,
                    help: isSavedLater ? String(localized: "已在稍后听") : String(localized: "存入稍后听")
                ) {
                    hub.toggleSavedLater(issue)
                    if let msg = hub.lastNotice { showToast(msg) }
                }
            }
            .opacity(isHovered || isCurrent || isSavedLater ? 1 : 0.28)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 9, style: .continuous)
                .fill(isCurrent ? PodcastStyle.rowTintWash(tint, scheme) : (isHovered ? YYColor.inset.opacity(0.55) : Color.clear))
        )
        .padding(.horizontal, -10)
        .contentShape(.rect)
        .onHover { over in
            hoveredIssueID = over ? issue.id : (hoveredIssueID == issue.id ? nil : hoveredIssueID)
        }
        .onTapGesture {
            withAnimation(YYMotion.standard) { openedIssueID = issue.id }
        }
        .contextMenu {
            Button(String(localized: "展开访谈书页")) { openedIssueID = issue.id }
            Button(isPlaying ? String(localized: "暂停") : String(localized: "播放")) { hub.selectAndPlay(issue) }
            Button(isSavedLater ? String(localized: "移回在听") : String(localized: "存入稍后听")) { hub.toggleSavedLater(issue) }
            Divider()
            Button(String(localized: "删除"), role: .destructive) { hub.delete(issue) }
        }
    }

    private func rowGlyphButton(glyph: PodcastGlyph, active: Bool, help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            PodcastGlyphView(glyph: glyph, size: 13.5, lineWidth: 1.45)
                .foregroundStyle(active ? tint.accent : YYColor.t3)
                .frame(width: 26, height: 26)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(help)
    }

    /// Crisp stationery starter rows when no Studio episodes have been brewed yet — matches episodeRow geometry 1:1.
    private var studioStarterRows: some View {
        let readingBook = library.keptBooks.first(where: { $0.status == .reading }) ?? library.keptBooks.first
        let items: [(kind: StudioKind, title: String, accent: String, tail: String)] = [
            (
                .weeklyDigest,
                readingBook != nil
                    ? String(localized: "本周书房周刊：重读《\(readingBook!.title)》与近期划线")
                    : String(localized: "本周书房周刊：串联本周阅读与夜间划线"),
                String(localized: "不剧透锁"),
                String(localized: "10 分 · 本机酿制")
            ),
            (
                .roundtable,
                String(localized: "跨书圆桌：挑两三本在读书同桌对谈"),
                String(localized: "跨书对谈"),
                String(localized: "15 分 · 防剧透")
            ),
            (
                .bookGuide,
                readingBook != nil
                    ? String(localized: "难书导读：《\(readingBook!.title)》直觉热身精讲")
                    : String(localized: "难书与论文导读：晦涩章节直觉精讲"),
                String(localized: "读前热身"),
                String(localized: "8 分 · 附原文锚点")
            ),
            (
                .vocabRadio,
                String(localized: "生词伴听 · 通勤外语台（融入本周生词）"),
                String(localized: "生词本联动"),
                String(localized: "12 分 · 语速 0.9×")
            ),
        ]

        return VStack(spacing: 0) {
            ForEach(Array(items.enumerated()), id: \.element.kind) { idx, item in
                let isHovered = hoveredStarterKind == item.kind
                if idx > 0 {
                    Rectangle().fill(scheme == .dark ? Color.white.opacity(0.05) : YYColor.line.opacity(0.65)).frame(height: 0.5)
                }
                HStack(spacing: 11) {
                    Image(systemName: "play.fill")
                        .font(.system(size: 8.5, weight: .semibold))
                        .foregroundStyle(isHovered ? tint.accent : YYColor.t3.opacity(0.5))
                        .frame(width: 14, height: 22, alignment: .leading)

                    PodcastShowMark(title: String(localized: "书房电台"), isStudio: true, size: 22)

                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.title)
                            .font(.yy(size: 13.5, weight: .regular))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)

                        HStack(spacing: 5) {
                            Text(item.accent)
                                .fontWeight(.regular)
                                .foregroundStyle(tint.accent)
                            Text("·").foregroundStyle(YYColor.t3)
                            Text(item.tail)
                                .foregroundStyle(YYColor.t3)
                        }
                        .font(.yy(size: 11.5))
                        .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    HStack(spacing: 3) {
                        PodcastGlyphView(glyph: .transcriptLines, size: 13.5, lineWidth: 1.45)
                            .foregroundStyle(YYColor.t3)
                            .frame(width: 26, height: 26)
                        PodcastGlyphView(glyph: .bookmark, size: 13.5, lineWidth: 1.45)
                            .foregroundStyle(YYColor.t3)
                            .frame(width: 26, height: 26)
                    }
                    .opacity(isHovered ? 0.85 : 0.25)
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 10)
                .background(
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .fill(isHovered ? YYColor.inset.opacity(0.55) : Color.clear)
                )
                .padding(.horizontal, -10)
                .contentShape(.rect)
                .onHover { over in
                    hoveredStarterKind = over ? item.kind : (hoveredStarterKind == item.kind ? nil : hoveredStarterKind)
                }
                .onTapGesture {
                    let ids = (item.kind == .bookGuide || item.kind == .weeklyDigest) ? (readingBook.map { [$0.id] } ?? []) : []
                    studioSheet = StudioLaunch(kind: item.kind, bookIDs: ids)
                }
            }
        }
    }

    /// Live Apple Podcasts discovery card shown quietly when no external subscriptions exist yet.
    private var discoverGroupCard: some View {
        let collapsed = collapsedGroupIDs.contains("discover")
        let subscribedFeeds = Set(hub.channels.map(\.feedUrl))
        let picks = Array(hub.discoveredShows.prefix(4))

        return PodcastStationeryCard(cornerRadius: 13) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(spacing: 8) {
                    Button {
                        withAnimation(YYMotion.quick) { toggleCollapse("discover") }
                    } label: {
                        Text("精选播客")
                            .font(PodcastStyle.songti(15))
                            .foregroundStyle(YYColor.t1)
                    }
                    .buttonStyle(.plain)

                    Text("公开目录 · 一键订阅")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)

                    Spacer()

                    PodcastPaperPill(title: String(localized: "搜更多")) {
                        adding = true
                    }

                    Button {
                        withAnimation(YYMotion.quick) { toggleCollapse("discover") }
                    } label: {
                        PodcastGlyphView(glyph: .chevronDown, size: 12, lineWidth: 1.6)
                            .foregroundStyle(YYColor.t3)
                            .rotationEffect(.degrees(collapsed ? -90 : 0))
                            .frame(width: 24, height: 24)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.bottom, collapsed ? 0 : 6)

                if !collapsed {
                    if hub.isLoadingDiscovery && picks.isEmpty {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text("正在连接公开播客目录…")
                                .font(.yy(size: 12))
                                .foregroundStyle(YYColor.t3)
                        }
                        .padding(.vertical, 12)
                    } else if picks.isEmpty {
                        HStack(spacing: 10) {
                            Text("可搜索播客、粘贴单集网页 / RSS 链接，或导入 OPML / 本地音频")
                                .font(.yy(size: 12))
                                .foregroundStyle(YYColor.t3)
                            Spacer()
                            PodcastPaperPill(title: String(localized: "添加节目"), accent: true) {
                                adding = true
                            }
                        }
                        .padding(.vertical, 12)
                    } else {
                        VStack(spacing: 0) {
                            ForEach(Array(picks.enumerated()), id: \.element.id) { idx, hit in
                                if idx > 0 {
                                    Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5)
                                }
                                let isSub = subscribedFeeds.contains(hit.feedURL)
                                let busy = hub.subscribingFeedURLs.contains(hit.feedURL)
                                HStack(spacing: 11) {
                                    PodcastShowMark(title: hit.title, artworkURL: hit.artworkURL, size: 22)
                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(hit.title)
                                            .font(.yy(size: 13.5, weight: .medium))
                                            .foregroundStyle(YYColor.t1)
                                            .lineLimit(1)
                                        Text([hit.author, hit.genre].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                                            .font(.yy(size: 11.5))
                                            .foregroundStyle(YYColor.t3)
                                            .lineLimit(1)
                                    }
                                    Spacer(minLength: 8)
                                    if isSub {
                                        Text("已订阅")
                                            .font(.yy(size: 11, weight: .medium))
                                            .foregroundStyle(YYColor.t3)
                                    } else {
                                        PodcastPaperPill(
                                            title: busy ? String(localized: "订阅中") : String(localized: "订阅"),
                                            accent: true,
                                            height: 24,
                                            horizontalPadding: 10
                                        ) {
                                            Task {
                                                let ok = await hub.subscribeFeaturedShow(hit)
                                                if ok { showToast(String(localized: "已订阅《\(hit.title)》")) }
                                            }
                                        }
                                        .disabled(busy)
                                    }
                                }
                                .padding(.vertical, 10)
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 13)
            .padding(.bottom, collapsed ? 13 : 8)
        }
    }

    // MARK: - Right Column: Airy Literary Listening Desk

    private func rightDeskColumn(width rightW: CGFloat) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            // Greeting + Literary Chinese Date (e.g. 十一月廿二日 · 星期日)
            HStack(alignment: .firstTextBaseline, spacing: 11) {
                Text(PodcastFormat.greeting())
                    .font(PodcastStyle.songti(30))
                    .foregroundStyle(YYColor.t1)
                HStack(alignment: .center, spacing: 6) {
                    Circle()
                        .fill(tint.accent.opacity(0.68))
                        .frame(width: 4, height: 4)
                        .offset(y: -3.5)
                    Text(PodcastFormat.editorialDate())
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                }
            }
            .padding(.top, -4)

            // Quiet clipped quote line (click to open clips drawer)
            Button {
                openDrawer(.clips)
            } label: {
                Group {
                    if let clip = hub.clips.first {
                        (Text("「\(clip.quoteText)」")
                            .font(PodcastStyle.songti(13.5))
                            .foregroundStyle(YYColor.t2)
                         + Text("  \(clip.showTitle) · \(PodcastFormat.clock(clip.t0))")
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3))
                    } else {
                        (Text("「把声音读成一本书，把书架酿成一档只属于你的私人电台。」")
                            .font(PodcastStyle.songti(13.5))
                            .foregroundStyle(YYColor.t2)
                         + Text("  书房电台")
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3))
                    }
                }
                .lineSpacing(5)
                .lineLimit(2)
                .multilineTextAlignment(.leading)
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .padding(.top, 10)

            // Recessed matte command search & paste-link bar + floating dropdown overlay
            commandSearchSection
                .padding(.top, 16)
                .zIndex(40)

            // 5 Circular Quick-Launch Buttons (Exact Approved Circles, Adaptive Spacing)
            circularQuickLaunchRow(availableWidth: rightW)
                .padding(.top, 16)
                .zIndex(1)

            // Single Compact Tabletop Listening Card (Exact Prototype Silhouette in BOTH Active & Ready States)
            Group {
                if let current = hub.activeIssue {
                    tabletopPlayerCard(current)
                } else {
                    tabletopReadyCard
                }
            }
            .padding(.top, 22)
            .zIndex(1)
        }
    }

    private var commandSearchSection: some View {
        HStack(spacing: 9) {
            Image(systemName: looksLikeURL ? "link" : "magnifyingglass")
                .font(.system(size: 13.5, weight: .medium))
                .foregroundStyle(YYColor.t3)

            TextField(
                "",
                text: $searchQuery,
                prompt: Text(String(localized: "搜节目、听过的句子，或粘贴单集链接…")).foregroundStyle(YYColor.t3)
            )
                .textFieldStyle(.plain)
                .font(.yy(size: 13))
                .foregroundStyle(YYColor.t1)
                .focused($searchFocused)
                .onSubmit { handleSearchSubmit() }

            if !searchQuery.isEmpty {
                Button {
                    searchQuery = ""
                } label: {
                    PodcastGlyphView(glyph: .xmark, size: 10, lineWidth: 1.8)
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 20, height: 20)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
            } else {
                Text("/")
                    .font(.yy(size: 10.5, weight: .medium))
                    .foregroundStyle(YYColor.t3)
                    .frame(width: 20, height: 20)
                    .background(YYColor.card, in: RoundedRectangle(cornerRadius: 5, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 5, style: .continuous)
                            .strokeBorder(YYColor.line, lineWidth: 0.5)
                    )
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 40)
        .background(searchFocused ? YYColor.card : YYColor.inset, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .strokeBorder(searchFocused ? PodcastStyle.accentRing(tint, scheme) : YYColor.line, lineWidth: searchFocused ? 1 : 0.5)
        )
        .shadow(
            color: searchFocused ? PodcastStyle.pillShadow(scheme) : .clear,
            radius: 8,
            y: 3
        )
        .overlay(alignment: .top) {
            if showSearchDrop {
                commandDropdownCard
                    .offset(y: 47)
                    .shadow(
                        color: scheme == .dark ? Color.black.opacity(0.45) : Color(yy: YYRGBA(30, 26, 20, 0.14)),
                        radius: 20,
                        y: 8
                    )
                    .transition(.opacity.combined(with: .offset(y: -4)))
            }
        }
    }

    private var commandDropdownCard: some View {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        let matchingIssues = q.isEmpty ? [] : Array(hub.allIssues.filter {
            $0.title.localizedCaseInsensitiveContains(q) || $0.showTitle.localizedCaseInsensitiveContains(q)
        }.prefix(4))

        return PodcastStationeryCard(cornerRadius: 13) {
            VStack(alignment: .leading, spacing: 2) {
                if q.isEmpty {
                    Text("快捷直达")
                        .font(.yy(size: 10.5, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 9)
                        .padding(.top, 5)
                        .padding(.bottom, 3)

                    dropdownActionRow(
                        symbol: "link",
                        title: String(localized: "粘贴单集链接或 RSS（免订阅直听）"),
                        subtitle: String(localized: "支持小宇宙、Apple Podcasts、单集网页或音频直链"),
                        trailing: String(localized: "去导入")
                    ) {
                        dismissSearch()
                        adding = true
                    }
                    dropdownActionRow(
                        symbol: "waveform",
                        title: String(localized: "导入本地音频文件"),
                        subtitle: String(localized: "把 .mp3 / .m4a 讲座或访谈放进收听台"),
                        trailing: String(localized: "选择")
                    ) {
                        dismissSearch()
                        importingAudio = true
                    }
                    dropdownActionRow(
                        symbol: "square.and.arrow.down",
                        title: String(localized: "导入 OPML 订阅列表"),
                        subtitle: String(localized: "从其他播客应用一键迁入"),
                        trailing: String(localized: "导入")
                    ) {
                        dismissSearch()
                        importingOPML = true
                    }
                } else if looksLikeURL {
                    Text("识别到链接")
                        .font(.yy(size: 10.5, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 9)
                        .padding(.top, 5)
                    dropdownActionRow(
                        symbol: "arrow.down.circle",
                        title: hub.isAddingFeed ? String(localized: "正在解析并导入…") : String(localized: "直接导入此单集 / RSS"),
                        subtitle: q,
                        trailing: String(localized: "导入")
                    ) {
                        importURLFromCommand(q)
                    }
                } else {
                    if !matchingIssues.isEmpty {
                        Text("本地单集")
                            .font(.yy(size: 10.5, weight: .semibold))
                            .foregroundStyle(YYColor.t3)
                            .padding(.horizontal, 9)
                            .padding(.top, 5)
                        ForEach(matchingIssues, id: \.id) { item in
                            Button {
                                dismissSearch()
                                withAnimation(YYMotion.standard) { openedIssueID = item.id }
                            } label: {
                                HStack(spacing: 9) {
                                    PodcastShowMark(title: item.showTitle, artworkURL: item.artworkUrl, isStudio: item.studioKind != nil, size: 19)
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text(item.title)
                                            .font(.yy(size: 12.5, weight: .medium))
                                            .foregroundStyle(YYColor.t1)
                                            .lineLimit(1)
                                        Text(item.showTitle)
                                            .font(.yy(size: 11))
                                            .foregroundStyle(YYColor.t3)
                                            .lineLimit(1)
                                    }
                                    Spacer(minLength: 6)
                                    Text("打开")
                                        .font(.yy(size: 11, weight: .semibold))
                                        .foregroundStyle(tint.accent)
                                }
                                .padding(.horizontal, 9)
                                .padding(.vertical, 7)
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                        }
                    }

                    dropdownActionRow(
                        symbol: "magnifyingglass",
                        title: String(localized: "在公开播客目录搜索「\(q)」"),
                        subtitle: String(localized: "查找 RSS 订阅源并加入收听台"),
                        trailing: String(localized: "搜索")
                    ) {
                        let queryCopy = q
                        dismissSearch()
                        addInitialQuery = queryCopy
                        adding = true
                    }
                }
            }
            .padding(6)
        }
    }

    private func dropdownActionRow(
        symbol: String,
        title: String,
        subtitle: String,
        trailing: String,
        action: @escaping () -> Void
    ) -> some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: symbol)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundStyle(YYColor.t2)
                    .frame(width: 22, height: 22)
                    .background(YYColor.inset, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.yy(size: 12.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    Text(subtitle)
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)
                }
                Spacer(minLength: 6)
                Text(trailing)
                    .font(.yy(size: 11, weight: .semibold))
                    .foregroundStyle(tint.accent)
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 7)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func circularQuickLaunchRow(availableWidth: CGFloat) -> some View {
        // Flush-aligned 5 circular quick buttons: first aligns cleanly under the search icon,
        // last aligns cleanly under the `/` keycap, with equal spacing between all 5 circles.
        HStack(alignment: .top, spacing: 0) {
            quickCircleItem(glyph: .plus, title: String(localized: "酿一期"), accent: true) {
                studioSheet = StudioLaunch(kind: .weeklyDigest)
            }
            Spacer(minLength: 4)
            quickCircleItem(glyph: .weekly, title: String(localized: "书房周刊")) {
                openOrBrewStudio(.weeklyDigest)
            }
            Spacer(minLength: 4)
            quickCircleItem(glyph: .roundtable, title: String(localized: "跨书圆桌")) {
                openOrBrewStudio(.roundtable)
            }
            Spacer(minLength: 4)
            quickCircleItem(glyph: .vocab, title: String(localized: "生词伴听")) {
                openOrBrewStudio(.vocabRadio)
            }
            Spacer(minLength: 4)
            quickCircleItem(glyph: .link, title: String(localized: "贴单集")) {
                pasteLinkOrFocusSearch()
            }
        }
        .frame(width: availableWidth)
    }

    private func quickCircleItem(
        glyph: PodcastGlyph,
        title: String,
        accent: Bool = false,
        action: @escaping () -> Void
    ) -> some View {
        VStack(spacing: 6) {
            PodcastCircleIconButton(
                glyph: glyph,
                size: 40,
                iconSize: 16,
                accent: accent,
                accessibilityLabel: title,
                action: action
            )
            Text(title)
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
        }
        .frame(width: 48)
    }

    // MARK: - Tabletop Now-Playing Card (Active & Ready States Share Exact Silhouette)

    private func tabletopPlayerCard(_ issue: PodcastIssue) -> some View {
        let isCurrent = hub.activeIssueID == issue.id
        let pos = isCurrent ? hub.currentTime : issue.positionSec
        let dur = isCurrent && hub.duration > 0 ? hub.duration : Double(issue.durationSec)
        let frac = dur > 0 ? min(1, max(0, pos / dur)) : 0
        let sentence = hub.activeSentence(for: issue.id)
        let excerptText: String = {
            if let sentence {
                return PodcastFormat.sentenceText(sentence, track: issue.track)
            }
            return issue.summary.isEmpty
                ? String(localized: "点击展开访谈书页，查看逐字稿、章节与原声摘录。")
                : issue.summary
        }()
        let speakerLabel = sentence?.speaker ?? issue.showTitle
        let supportsTrackSwitch = hub.supportsTrackSwitch(issue)

        return PodcastStationeryCard(cornerRadius: 14) {
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    // Header
                    HStack(spacing: 11) {
                        PodcastShowMark(
                            title: issue.studioKind != nil ? String(localized: "书房电台") : issue.showTitle,
                            artworkURL: issue.artworkUrl,
                            isStudio: issue.studioKind != nil,
                            size: 32
                        )
                        VStack(alignment: .leading, spacing: 1) {
                            Text(issue.title)
                                .font(.yy(size: 13.5, weight: .medium))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            let status = isCurrent && hub.isPlaying ? String(localized: "正在播放") : String(localized: "暂停中")
                            let rem = PodcastFormat.remaining(position: pos, duration: dur)
                            Text([issue.showTitle, status, rem].filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.yy(size: 11.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        PodcastPaperPill(title: String(localized: "展开书页"), accent: true) {
                            withAnimation(YYMotion.standard) { openedIssueID = issue.id }
                        }
                    }

                    // Warm book-leaf live sentence preview
                    Button {
                        withAnimation(YYMotion.standard) { openedIssueID = issue.id }
                    } label: {
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text(speakerLabel)
                                    .font(.yy(size: 11, weight: .medium))
                                    .foregroundStyle(tint.accent)
                                    .lineLimit(1)
                                Spacer()
                                Text(issue.studioKind != nil ? String(localized: "本地声卡 · 不剧透锁") : PodcastFormat.trackLabel(issue.track))
                                    .font(.yy(size: 10.5))
                                    .foregroundStyle(YYColor.t3)
                            }
                            Text(excerptText)
                                .font(PodcastStyle.songti(14))
                                .foregroundStyle(YYColor.t1)
                                .lineSpacing(5)
                                .lineLimit(2)
                                .multilineTextAlignment(.leading)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .padding(.horizontal, 13)
                        .padding(.vertical, 11)
                        .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: 10, style: .continuous)
                                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
                        )
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .padding(.top, 13)

                    // Interactive progress scrubber
                    VStack(spacing: 3) {
                        GeometryReader { geo in
                            let width = max(1, geo.size.width)
                            ZStack(alignment: .leading) {
                                Capsule().fill(YYColor.inset).frame(height: 2.5)
                                Capsule().fill(YYColor.t1).frame(width: max(3, width * frac), height: 2.5)
                            }
                            .frame(maxHeight: .infinity)
                            .contentShape(.rect)
                            .gesture(
                                DragGesture(minimumDistance: 0)
                                    .onEnded { drag in
                                        guard dur > 0 else { return }
                                        let ratio = min(1, max(0, drag.location.x / width))
                                        hub.seek(to: dur * ratio)
                                    }
                            )
                        }
                        .frame(height: 14)

                        HStack {
                            Text(PodcastFormat.clock(pos))
                            Spacer()
                            Text(PodcastFormat.clock(dur))
                        }
                        .font(.yy(size: 10.5))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t3)
                    }
                    .padding(.top, 12)
                }
                .padding(.horizontal, 17)
                .padding(.top, 15)
                .padding(.bottom, 13)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                // Footer controls on warm leaf surface — ViewThatFits guarantees zero "..." truncation at any width!
                tabletopFooterBar(
                    isPlaying: isCurrent && hub.isPlaying,
                    supportsTrackSwitch: supportsTrackSwitch,
                    track: issue.track,
                    studioNote: issue.studioKind.map { "\($0.title) · 不剧透" } ?? String(localized: "原声 · 书摘与插话就绪"),
                    onSkipBack: { hub.skip(by: -15) },
                    onTogglePlay: { hub.selectAndPlay(issue) },
                    onSkipForward: { hub.skip(by: 15) },
                    onCycleRate: {
                        hub.cycleRate()
                        showToast(String(localized: "倍速 \(String(format: "%.1f×", hub.rate))"))
                    },
                    onSelectTrack: { newTrack in
                        hub.setTrack(newTrack, for: issue)
                        showToast(String(localized: "已切至\(PodcastFormat.trackLabel(newTrack))"))
                    },
                    onClip: { clipCurrentMoment() }
                )
            }
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
    }

    /// Empty-library tabletop card preserving the 3-zone stationery player silhouette.
    private var tabletopReadyCard: some View {
        let readingBook = library.keptBooks.first(where: { $0.status == .reading }) ?? library.keptBooks.first
        let cardTitle = readingBook != nil
            ? String(localized: "《\(readingBook!.title)》· 导读待酿制")
            : String(localized: "书房电台 · 准备就绪")
        let cardMeta: String = {
            if let book = readingBook {
                let pct = Int((book.progress * 100).rounded())
                return pct > 0
                    ? String(localized: "书房电台 · 已读 \(pct)% · 绝不剧透")
                    : String(localized: "书房电台 · 读前 8 分钟热身")
            }
            return String(localized: "书房电台 · 本机整理 · 防剧透")
        }()
        let previewLine = readingBook != nil
            ? String(localized: "点右上角「酿一期」或下方播放键，把《\(readingBook!.title)》已读章节与划线酿成一期私人电台；或在上方粘贴任意单集链接直听。")
            : String(localized: "在上方粘贴单集链接、搜索公开播客目录，或点「酿一期」把你的书架酿成不剧透的私人电台。")

        let openBrew = {
            let ids = readingBook.map { [$0.id] } ?? []
            studioSheet = StudioLaunch(kind: readingBook != nil ? .bookGuide : .weeklyDigest, bookIDs: ids)
        }

        return PodcastStationeryCard(cornerRadius: 14) {
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 11) {
                        PodcastShowMark(title: String(localized: "书房电台"), isStudio: true, size: 32)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(cardTitle)
                                .font(.yy(size: 13.5, weight: .medium))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            Text(cardMeta)
                                .font(.yy(size: 11.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        PodcastPaperPill(title: String(localized: "酿一期"), accent: true, action: openBrew)
                    }

                    Button(action: openBrew) {
                        VStack(alignment: .leading, spacing: 5) {
                            HStack {
                                Text("林深 · 书房主播")
                                    .font(.yy(size: 11, weight: .medium))
                                    .foregroundStyle(tint.accent)
                                    .lineLimit(1)
                                Spacer()
                                Text("本地声卡 · 防剧透锁")
                                    .font(.yy(size: 10.5))
                                    .foregroundStyle(YYColor.t3)
                            }
                            Text(previewLine)
                                .font(PodcastStyle.songti(14))
                                .foregroundStyle(YYColor.t1)
                                .lineSpacing(5)
                                .lineLimit(2)
                                .multilineTextAlignment(.leading)
                                .frame(maxWidth: .infinity, alignment: .leading)
                        }
                        .padding(.horizontal, 13)
                        .padding(.vertical, 11)
                        .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 10, style: .continuous))
                        .overlay(
                            RoundedRectangle(cornerRadius: 10, style: .continuous)
                                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
                        )
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .padding(.top, 13)

                    VStack(spacing: 3) {
                        ZStack(alignment: .leading) {
                            Capsule().fill(YYColor.inset).frame(height: 2.5)
                            Capsule().fill(YYColor.t1.opacity(0.25)).frame(width: 24, height: 2.5)
                        }
                        .frame(height: 14)

                        HStack {
                            Text("00:00")
                            Spacer()
                            Text("08:00")
                        }
                        .font(.yy(size: 10.5))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t3)
                    }
                    .padding(.top, 12)
                }
                .padding(.horizontal, 17)
                .padding(.top, 15)
                .padding(.bottom, 13)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                tabletopFooterBar(
                    isPlaying: false,
                    supportsTrackSwitch: false,
                    track: preferredTrack,
                    studioNote: String(localized: "防剧透锁 · 仅基于已读进度"),
                    onSkipBack: { showToast(String(localized: "先酿一期或订阅节目即可收听")) },
                    onTogglePlay: openBrew,
                    onSkipForward: { showToast(String(localized: "先酿一期或订阅节目即可收听")) },
                    onCycleRate: {
                        hub.cycleRate()
                        showToast(String(localized: "默认倍速 \(String(format: "%.1f×", hub.rate))"))
                    },
                    onSelectTrack: { newTrack in
                        preferredTrack = newTrack
                        showToast(String(localized: "默认外语声轨：\(PodcastFormat.trackLabel(newTrack))"))
                    },
                    onClip: { openDrawer(.clips) }
                )
            }
            .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        }
    }

    private func tabletopFooterBar(
        isPlaying: Bool,
        supportsTrackSwitch: Bool,
        track: PodcastTrack,
        studioNote: String?,
        onSkipBack: @escaping () -> Void,
        onTogglePlay: @escaping () -> Void,
        onSkipForward: @escaping () -> Void,
        onCycleRate: @escaping () -> Void,
        onSelectTrack: @escaping (PodcastTrack) -> Void,
        onClip: @escaping () -> Void
    ) -> some View {
        let transport: some View = HStack(spacing: 3) {
            compactTextControl("-15s", action: onSkipBack)

            Button(action: onTogglePlay) {
                Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                    .font(.system(size: 10.5, weight: .semibold))
                    .foregroundStyle(YYColor.card)
                    .offset(x: isPlaying ? 0 : 1)
                    .frame(width: 30, height: 30)
                    .background(YYColor.t1, in: Circle())
                    .contentShape(Circle())
            }
            .buttonStyle(YYPressStyle(scale: 0.94))
            .padding(.horizontal, 2)

            compactTextControl("+15s", action: onSkipForward)
            compactTextControl(String(format: "%.1f×", hub.rate), action: onCycleRate)
        }
        .fixedSize(horizontal: true, vertical: false)

        let clipButton: some View = Button(action: onClip) {
            HStack(spacing: 4) {
                Text("划线")
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(YYColor.t2)
                Text("H")
                    .font(.yy(size: 9.5, weight: .medium))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 4)
                    .frame(height: 16)
                    .background(YYColor.card, in: RoundedRectangle(cornerRadius: 4, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: 4, style: .continuous)
                            .strokeBorder(YYColor.line, lineWidth: 0.5)
                    )
            }
            .padding(.horizontal, 4)
            .frame(height: 26)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .fixedSize(horizontal: true, vertical: false)
        .help(String(localized: "原声划线 (H)"))

        return ViewThatFits(in: .horizontal) {
            // Layout 1: Full 1-row footer bar
            HStack(spacing: 6) {
                transport
                Spacer(minLength: 4)
                if supportsTrackSwitch {
                    PodcastTrackSegmentedControl(selection: track, compact: false, onSelect: onSelectTrack)
                } else if let studioNote {
                    Text(studioNote)
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(tint.accent)
                        .fixedSize(horizontal: true, vertical: false)
                }
                clipButton
            }
            .frame(maxWidth: .infinity)

            // Layout 2: Slightly tighter 1-row bar (fits ~350pt without wrapping)
            HStack(spacing: 4) {
                transport
                Spacer(minLength: 2)
                if supportsTrackSwitch {
                    PodcastTrackSegmentedControl(selection: track, compact: true, onSelect: onSelectTrack)
                } else if let studioNote {
                    Text(studioNote)
                        .font(.yy(size: 10.5, weight: .medium))
                        .foregroundStyle(tint.accent)
                        .fixedSize(horizontal: true, vertical: false)
                }
                clipButton
            }
            .frame(maxWidth: .infinity)

            // Layout 3: Clean balanced 2-row wrap on very narrow windows (ZERO "..." truncation ever!)
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 6) {
                    transport
                    Spacer(minLength: 4)
                    clipButton
                }
                if supportsTrackSwitch {
                    PodcastTrackSegmentedControl(selection: track, compact: false, onSelect: onSelectTrack)
                } else if let studioNote {
                    Text(studioNote)
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(tint.accent)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 13)
        .padding(.vertical, 9)
        .background(PodcastStyle.leafSurface(scheme))
    }

    private func compactTextControl(_ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(label)
                .font(.yy(size: 11, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .padding(.horizontal, 4.5)
                .frame(height: 26)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    // MARK: - Right-Edge Detached Floating Circular Drawer Buttons

    private var rightEdgeCircularTriggers: some View {
        VStack(spacing: 10) {
            edgeTriggerButton(
                tab: .clips,
                glyph: .bookmark,
                tooltip: String(localized: "原声书摘 · 稍后听"),
                showDot: hasNewClipDot
            )
            edgeTriggerButton(
                tab: .voices,
                glyph: .waveform,
                tooltip: String(localized: "声音卡 · 本机整理"),
                showDot: false
            )
        }
    }

    private func edgeTriggerButton(
        tab target: PodcastDrawerTab,
        glyph: PodcastGlyph,
        tooltip: String,
        showDot: Bool
    ) -> some View {
        PodcastCircleIconButton(
            glyph: glyph,
            size: 36,
            iconSize: 15.5,
            showDot: showDot,
            accessibilityLabel: tooltip
        ) {
            openDrawer(target)
        }
        .onHover { over in
            withAnimation(YYMotion.quick) {
                hoveredDrawerTrigger = over ? target : (hoveredDrawerTrigger == target ? nil : hoveredDrawerTrigger)
            }
        }
        .overlay(alignment: .trailing) {
            if hoveredDrawerTrigger == target {
                Text(tooltip)
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .fixedSize()
                    .padding(.horizontal, 10)
                    .padding(.vertical, 5)
                    .background(YYColor.card, in: Capsule())
                    .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    .shadow(color: PodcastStyle.pillShadow(scheme), radius: 8, y: 2)
                    .padding(.trailing, 44)
                    .allowsHitTesting(false)
                    .transition(.opacity.combined(with: .offset(x: 4)))
            }
        }
    }

    // MARK: - Actions

    private func toggleCollapse(_ id: String) {
        if collapsedGroupIDs.contains(id) {
            collapsedGroupIDs.remove(id)
        } else {
            collapsedGroupIDs.insert(id)
        }
    }

    private func openDrawer(_ tab: PodcastDrawerTab) {
        withAnimation(YYMotion.standard) {
            drawerTab = tab
            drawerOpen = true
            if tab == .clips || tab == .later { hasNewClipDot = false }
        }
    }

    private func closeDrawer() {
        withAnimation(YYMotion.standard) {
            drawerOpen = false
        }
    }

    private func openOrBrewStudio(_ kind: StudioKind) {
        if let existing = hub.studioIssues.first(where: { $0.studioKind == kind }) {
            withAnimation(YYMotion.standard) { openedIssueID = existing.id }
        } else {
            studioSheet = StudioLaunch(kind: kind)
        }
    }

    private func pasteLinkOrFocusSearch() {
        if let str = NSPasteboard.general.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines),
           str.hasPrefix("http://") || str.hasPrefix("https://") {
            searchQuery = str
            searchFocused = true
            showSearchDrop = true
        } else {
            searchFocused = true
            showSearchDrop = true
        }
    }

    private func handleSearchSubmit() {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return }
        if looksLikeURL {
            importURLFromCommand(q)
        } else {
            dismissSearch()
            addInitialQuery = q
            adding = true
        }
    }

    private func importURLFromCommand(_ url: String) {
        Task {
            let ok = await hub.subscribeRSS(urlString: url, putLatestOnDesk: true)
            if ok {
                dismissSearch()
                showToast(String(localized: "已导入并放到收听台"))
            } else if let err = hub.addError {
                showToast(err)
            }
        }
    }

    private func dismissSearch() {
        searchFocused = false
        showSearchDrop = false
    }

    private func clipCurrentMoment() {
        guard hub.activeIssue != nil else { return }
        Task {
            if await hub.markCurrentMoment() != nil {
                if !drawerOpen { hasNewClipDot = true }
                showToast(String(localized: "已记下原声片段，收入「原声书摘」"))
            }
        }
    }

    private func showToast(_ text: String) {
        toastTask?.cancel()
        transientToast = text
        toastTask = Task {
            try? await Task.sleep(for: .seconds(2.2))
            if !Task.isCancelled {
                transientToast = nil
            }
        }
    }
}
#endif
