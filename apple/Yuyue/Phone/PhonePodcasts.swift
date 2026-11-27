#if os(iOS)
import SwiftUI
import YuyueCore

/// iPhone 播客首页 — 与 Mac 端同源的语阅宣白纸面 + 0.5pt 净白卡片 + 圆形工具钮 + 分段声轨。
/// 嵌入在 `PhoneShelf` 的「播客 ⌄」标题与底部悬浮岛之间；外观纸色自动跟随全局「设置 · 外观」。
struct PhonePodcasts: View {
    let library: Library
    var externalOpenedIssueID: Binding<String?>? = nil

    enum ShowFilter: Hashable {
        case all
        case studio
        case show(String)
    }

    private struct StudioKindBox: Identifiable {
        let kind: StudioKind
        var id: StudioKind { kind }
    }

    private struct IssueIDBox: Identifiable {
        let id: String
    }

    @State private var showFilter: ShowFilter = .all
    @State private var searchQuery = ""
    @State private var openedIssueID: String?
    @State private var adding = false
    @State private var addInitialQuery = ""
    @State private var studioKind: StudioKind?
    @State private var drawerPresented = false
    @State private var drawerTab: PodcastDrawerTab = .clips
    @State private var hasNewClipDot = false

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var hub: PodcastHub { library.podcastHub }

    private var looksLikeURL: Bool {
        let t = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        return t.hasPrefix("http://") || t.hasPrefix("https://")
            || t.contains("xiaoyuzhou") || t.contains("podcasts.apple.com")
            || t.contains(".xml") || t.contains("/feed")
    }

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
        VStack(alignment: .leading, spacing: 13) {
            // Literary date sub-line + circular bookmark & add/brew buttons + recessed search command bar
            topUtilityBar

            // Horizontal show filter rail
            showFilterRail

            // Active tabletop listening card (same 3-zone stationery card in both active & ready states)
            if let current = hub.activeIssue {
                activeTabletopCard(current)
            } else {
                readyTabletopCard
            }

            // Section header + grouped stationery cards
            groupedStationList
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 24)
        .task {
            await hub.loadDiscoveryIfNeeded()
        }
        .sheet(isPresented: $adding) {
            PodcastAddSheet(library: library, initialQuery: addInitialQuery) {
                adding = false
                addInitialQuery = ""
            }
            .presentationDetents([.large])
            .presentationBackground(YYColor.bg)
        }
        .sheet(item: Binding(
            get: { studioKind.map { StudioKindBox(kind: $0) } },
            set: { studioKind = $0?.kind }
        )) { box in
            PodcastStudioSheet(
                library: library,
                initialKind: box.kind,
                onCreated: { issue in openedIssueID = issue.id },
                onClose: { studioKind = nil }
            )
            .presentationDetents([.large])
            .presentationBackground(YYColor.bg)
        }
        .sheet(isPresented: $drawerPresented) {
            PodcastDrawerContentView(
                library: library,
                tab: $drawerTab,
                isCompact: true,
                onOpenIssue: { id in
                    drawerPresented = false
                    openedIssueID = id
                },
                onClose: { drawerPresented = false }
            )
            .presentationDetents([.medium, .large])
            .presentationDragIndicator(.visible)
            .presentationBackground(YYColor.card)
        }
        .fullScreenCover(item: Binding(
            get: { (externalOpenedIssueID?.wrappedValue ?? openedIssueID).map { IssueIDBox(id: $0) } },
            set: { newBox in
                openedIssueID = newBox?.id
                externalOpenedIssueID?.wrappedValue = newBox?.id
            }
        )) { box in
            PodcastReaderView(library: library, issueID: box.id) {
                openedIssueID = nil
                externalOpenedIssueID?.wrappedValue = nil
            }
        }
        .onChange(of: openedIssueID) { _, newVal in
            if externalOpenedIssueID?.wrappedValue != newVal {
                externalOpenedIssueID?.wrappedValue = newVal
            }
        }
        .onChange(of: externalOpenedIssueID?.wrappedValue) { _, newVal in
            if openedIssueID != newVal {
                openedIssueID = newVal
            }
        }
    }

    // MARK: - Top Search Command Bar + Circular Utility Buttons

    private var topUtilityBar: some View {
        HStack(spacing: 9) {
            HStack(spacing: 8) {
                Image(systemName: looksLikeURL ? "link" : "magnifyingglass")
                    .font(.system(size: 13.5, weight: .medium))
                    .foregroundStyle(YYColor.t3)

                TextField(
                    "",
                    text: $searchQuery,
                    prompt: Text(String(localized: "搜节目、听过的句子，或粘贴链接…")).foregroundStyle(YYColor.t3)
                )
                    .textFieldStyle(.plain)
                    .font(.yy(size: 13))
                    .foregroundStyle(YYColor.t1)
                    .submitLabel(looksLikeURL ? .go : .search)
                    .onSubmit(handleSearchSubmit)

                if !searchQuery.isEmpty {
                    Button { searchQuery = "" } label: {
                        PodcastGlyphView(glyph: .xmark, size: 10, lineWidth: 1.8)
                            .foregroundStyle(YYColor.t3)
                            .frame(width: 32, height: 36)
                            .contentShape(Rectangle().inset(by: -4))
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel(String(localized: "清空"))
                }
            }
            .padding(.leading, 12)
            .padding(.trailing, searchQuery.isEmpty ? 12 : 4)
            .frame(height: 38)
            .background(YYColor.inset, in: RoundedRectangle(cornerRadius: 11, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 11, style: .continuous)
                    .strokeBorder(YYColor.line, lineWidth: 0.5)
            )

            PodcastCircleIconButton(
                glyph: .bookmark,
                size: 38,
                iconSize: 15.5,
                showDot: hasNewClipDot,
                accessibilityLabel: String(localized: "原声书摘与声音卡")
            ) {
                hasNewClipDot = false
                drawerTab = .clips
                drawerPresented = true
            }
            .contentShape(Circle().inset(by: -3))

            Menu {
                Button {
                    studioKind = .weeklyDigest
                } label: {
                    Label(String(localized: "酿一期书房电台"), systemImage: "sparkles")
                }
                Button {
                    addInitialQuery = ""
                    adding = true
                } label: {
                    Label(String(localized: "订阅播客或粘贴单集链接"), systemImage: "link")
                }
                Button {
                    drawerTab = .voices
                    drawerPresented = true
                } label: {
                    Label(String(localized: "声音卡与本机状态"), systemImage: "waveform")
                }
                if !library.podcastHub.channels.isEmpty {
                    Divider()
                    Button {
                        Task { await library.podcastHub.refreshAllChannels() }
                    } label: {
                        Label(String(localized: "刷新订阅节目"), systemImage: "arrow.clockwise")
                    }
                }
            } label: {
                PodcastGlyphView(glyph: .plus, size: 16, lineWidth: 1.55)
                    .foregroundStyle(tint.accent)
                    .frame(width: 38, height: 38)
                    .background(Circle().fill(PodcastStyle.rowTintWash(tint, scheme)))
                    .overlay(Circle().strokeBorder(PodcastStyle.accentRing(tint, scheme), lineWidth: 0.5))
                    .contentShape(Circle().inset(by: -3))
            }
            .accessibilityLabel(String(localized: "新建或添加播客"))
        }
    }

    // MARK: - Horizontal Show Filter Rail

    private var showFilterRail: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 7) {
                railFilterPill(title: String(localized: "全部"), target: .all, artworkURL: nil, isStudio: false, showMark: false)
                railFilterPill(title: String(localized: "书房电台"), target: .studio, artworkURL: nil, isStudio: true, showMark: true)
                ForEach(externalShows.prefix(8), id: \.key) { item in
                    railFilterPill(title: item.title, target: .show(item.key), artworkURL: item.artworkURL, isStudio: false, showMark: true)
                }
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 2)
        }
        .padding(.horizontal, -16)
    }

    private func railFilterPill(
        title: String,
        target: ShowFilter,
        artworkURL: String?,
        isStudio: Bool,
        showMark: Bool
    ) -> some View {
        let on = showFilter == target
        return Button {
            withAnimation(YYMotion.quick) {
                showFilter = on && target != .all ? .all : target
            }
        } label: {
            HStack(spacing: 6) {
                if showMark {
                    PodcastShowMark(title: title, artworkURL: artworkURL, isStudio: isStudio, size: 19)
                }
                Text(title)
                    .font(.yy(size: 12, weight: on ? .semibold : .medium))
                    .foregroundStyle(on ? tint.accent : YYColor.t2)
                    .lineLimit(1)
            }
            .padding(.leading, showMark ? 6 : 12)
            .padding(.trailing, 11)
            .frame(height: 32)
            .background(Capsule().fill(on ? PodcastStyle.rowTintWash(tint, scheme) : YYColor.card))
            .overlay(
                Capsule().strokeBorder(
                    on ? PodcastStyle.accentRing(tint, scheme) : PodcastStyle.pillBorder(scheme),
                    lineWidth: 0.5
                )
            )
            .shadow(color: on ? .clear : PodcastStyle.pillShadow(scheme), radius: 2, y: 1)
            .contentShape(Capsule())
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }

    // MARK: - Active & Ready Tabletop Listening Card (Exact `mob-song.png` Silhouette)

    private func activeTabletopCard(_ issue: PodcastIssue) -> some View {
        let isCurrent = hub.activeIssueID == issue.id
        let pos = isCurrent ? hub.currentTime : issue.positionSec
        let dur = isCurrent && hub.duration > 0 ? hub.duration : Double(issue.durationSec)
        let frac = dur > 0 ? min(1, max(0.04, pos / dur)) : 0
        let sentence = hub.activeSentence(for: issue.id)
        let excerpt: String = {
            if let sentence {
                return PodcastFormat.sentenceText(sentence, track: issue.track)
            }
            return issue.summary.isEmpty
                ? String(localized: "轻点进入访谈书页，查看逐字稿与原声摘录。")
                : issue.summary
        }()
        let supportsTrackSwitch = hub.supportsTrackSwitch(issue)

        return PodcastStationeryCard(cornerRadius: 15) {
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 10) {
                        PodcastShowMark(
                            title: issue.studioKind != nil ? String(localized: "书房电台") : issue.showTitle,
                            artworkURL: issue.artworkUrl,
                            isStudio: issue.studioKind != nil,
                            size: 32
                        )
                        VStack(alignment: .leading, spacing: 1) {
                            Text(issue.title)
                                .font(.yy(size: 13.5, weight: .semibold))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            let rem = PodcastFormat.remaining(position: pos, duration: dur)
                            Text([issue.showTitle, rem].filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.yy(size: 11.5))
                                .monospacedDigit()
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                        .onTapGesture { openedIssueID = issue.id }

                        Button {
                            hub.selectAndPlay(issue)
                        } label: {
                            Image(systemName: isCurrent && hub.isPlaying ? "pause.fill" : "play.fill")
                                .font(.system(size: 10.5, weight: .semibold))
                                .foregroundStyle(YYColor.card)
                                .offset(x: isCurrent && hub.isPlaying ? 0 : 1)
                                .frame(width: 30, height: 30)
                                .background(YYColor.t1, in: Circle())
                                .contentShape(Circle())
                        }
                        .buttonStyle(YYPressStyle(scale: 0.94))
                    }

                    Button {
                        openedIssueID = issue.id
                    } label: {
                        Text(excerpt)
                            .font(PodcastStyle.editorialFont(for: excerpt, size: 13, weight: .regular))
                            .foregroundStyle(YYColor.t1)
                            .lineSpacing(4)
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 11)
                            .padding(.vertical, 8.5)
                            .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                            .overlay(
                                RoundedRectangle(cornerRadius: 9, style: .continuous)
                                    .strokeBorder(YYColor.line, lineWidth: 0.5)
                            )
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .padding(.top, 9)

                    GeometryReader { g in
                        ZStack(alignment: .leading) {
                            Capsule().fill(YYColor.inset).frame(height: 2)
                            Capsule().fill(YYColor.t1).frame(width: max(3, g.size.width * frac), height: 2)
                        }
                    }
                    .frame(height: 2)
                    .padding(.top, 10)
                }
                .padding(.horizontal, 14)
                .padding(.top, 13)
                .padding(.bottom, 11)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                HStack(spacing: 8) {
                    if supportsTrackSwitch {
                        PodcastTrackSegmentedControl(selection: issue.track, compact: true) { newTrack in
                            hub.setTrack(newTrack, for: issue)
                        }
                    } else if let kind = issue.studioKind {
                        Text("\(kind.title) · 不剧透锁")
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(tint.accent)
                    } else {
                        Text(String(localized: "原声 · 书摘与插话就绪"))
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 6)
                    PodcastPaperPill(title: String(localized: "划线"), height: 24, horizontalPadding: 10) {
                        Task {
                            if await hub.markCurrentMoment() != nil {
                                hasNewClipDot = true
                            }
                        }
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(PodcastStyle.leafSurface(scheme))
            }
            .clipShape(RoundedRectangle(cornerRadius: 15, style: .continuous))
        }
    }

    private var readyTabletopCard: some View {
        let readingBook = library.keptBooks.first(where: { $0.status == .reading }) ?? library.keptBooks.first
        let title = if let book = readingBook {
            String(localized: "《\(book.title)》· 导读待酿制")
        } else {
            String(localized: "书房电台 · 准备就绪")
        }
        let meta = readingBook != nil
            ? String(localized: "书房电台 · 仅依已读进度 · 防剧透")
            : String(localized: "书房电台 · 本机整理")
        let preview = if let book = readingBook {
            String(localized: "轻点右侧播放键或「酿一期」，把《\(book.title)》酿成一期 8 分钟读前导读，绝不剧透后文。")
        } else {
            String(localized: "在上方搜公开播客、粘贴单集链接直听，或点「酿一期」把你的书架酿成私人电台。")
        }

        let startBrew = {
            studioKind = readingBook != nil ? .bookGuide : .weeklyDigest
        }

        return PodcastStationeryCard(cornerRadius: 15) {
            VStack(spacing: 0) {
                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 10) {
                        PodcastShowMark(title: String(localized: "书房电台"), isStudio: true, size: 32)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(title)
                                .font(.yy(size: 13.5, weight: .semibold))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            Text(meta)
                                .font(.yy(size: 11.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                        .onTapGesture(perform: startBrew)

                        Button(action: startBrew) {
                            Image(systemName: "play.fill")
                                .font(.system(size: 10.5, weight: .semibold))
                                .foregroundStyle(YYColor.card)
                                .offset(x: 1)
                                .frame(width: 30, height: 30)
                                .background(YYColor.t1, in: Circle())
                                .contentShape(Circle())
                        }
                        .buttonStyle(YYPressStyle(scale: 0.94))
                    }

                    Button(action: startBrew) {
                        Text(preview)
                            .font(YYUI.serif(13, .regular))
                            .foregroundStyle(YYColor.t1)
                            .lineSpacing(4)
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                            .padding(.horizontal, 11)
                            .padding(.vertical, 8.5)
                            .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 9, style: .continuous))
                            .overlay(
                                RoundedRectangle(cornerRadius: 9, style: .continuous)
                                    .strokeBorder(YYColor.line, lineWidth: 0.5)
                            )
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .padding(.top, 9)

                    ZStack(alignment: .leading) {
                        Capsule().fill(YYColor.inset).frame(height: 2)
                        Capsule().fill(YYColor.t1.opacity(0.25)).frame(width: 24, height: 2)
                    }
                    .padding(.top, 10)
                }
                .padding(.horizontal, 14)
                .padding(.top, 13)
                .padding(.bottom, 11)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                HStack(spacing: 8) {
                    Text(String(localized: "防剧透锁 · 仅基于已读进度"))
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(tint.accent)
                    Spacer(minLength: 6)
                    PodcastPaperPill(title: String(localized: "酿一期"), accent: true, height: 24, horizontalPadding: 10, action: startBrew)
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)
                .background(PodcastStyle.leafSurface(scheme))
            }
            .clipShape(RoundedRectangle(cornerRadius: 15, style: .continuous))
        }
    }

    // MARK: - Grouped Stationery Cards ("收听台")

    private var groupedStationList: some View {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        let matchesQuery: (PodcastIssue) -> Bool = { issue in
            guard !q.isEmpty else { return true }
            return issue.title.localizedCaseInsensitiveContains(q)
                || issue.showTitle.localizedCaseInsensitiveContains(q)
        }

        let studioList = hub.studioIssues.filter(matchesQuery)
        let showsToRender: [(key: String, title: String, artworkURL: String?)] = switch showFilter {
        case .all: externalShows
        case .studio: []
        case .show(let k): externalShows.filter { $0.key == k }
        }

        return VStack(alignment: .leading, spacing: 11) {
            HStack(alignment: .firstTextBaseline) {
                Text("收听台")
                    .font(PodcastStyle.songti(14.5))
                    .foregroundStyle(YYColor.t1)
                Spacer()
                Text(String(localized: "\(hub.allIssues.count) 期"))
                    .font(.yy(size: 11))
                    .monospacedDigit()
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 2)
            .padding(.top, 2)

            if showFilter == .all || showFilter == .studio {
                PodcastStationeryCard(cornerRadius: 14) {
                    VStack(alignment: .leading, spacing: 0) {
                        HStack(spacing: 8) {
                            PodcastShowMark(title: String(localized: "书房电台"), isStudio: true, size: 19)
                            Text("书房电台")
                                .font(PodcastStyle.songti(14.5))
                                .foregroundStyle(YYColor.t1)
                            Text(studioList.isEmpty ? String(localized: "防剧透") : String(localized: "\(studioList.count) 期"))
                                .font(.yy(size: 11))
                                .monospacedDigit()
                                .foregroundStyle(YYColor.t3)
                            Spacer()
                            PodcastPaperPill(title: String(localized: "+ 酿一期"), accent: true, height: 24, horizontalPadding: 10) {
                                studioKind = .weeklyDigest
                            }
                        }
                        .padding(.bottom, 6)

                        if studioList.isEmpty {
                            mobileStudioStarterRows
                        } else {
                            ForEach(Array(studioList.enumerated()), id: \.element.id) { idx, item in
                                if idx > 0 { Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5) }
                                mobileEpisodeRow(item)
                            }
                        }
                    }
                    .padding(.horizontal, 14)
                    .padding(.top, 12)
                    .padding(.bottom, 4)
                }
            }

            ForEach(showsToRender, id: \.key) { show in
                let eps = hub.allIssues.filter {
                    $0.studioKind == nil
                        && ($0.showTitle.isEmpty ? String(localized: "单集导入") : $0.showTitle) == show.key
                        && matchesQuery($0)
                }.sorted { $0.publishedAt > $1.publishedAt }
                let isSingleShow = showFilter == .show(show.key)
                let matchedChannel = hub.channel(forShowKey: show.key)
                let visibleEps = isSingleShow ? eps : Array(eps.prefix(5))
                if !eps.isEmpty {
                    PodcastStationeryCard(cornerRadius: 14) {
                        VStack(alignment: .leading, spacing: 0) {
                            HStack(spacing: 8) {
                                PodcastShowMark(title: show.title, artworkURL: show.artworkURL, size: 19)
                                Text(show.title)
                                    .font(PodcastStyle.songti(14.5))
                                    .foregroundStyle(YYColor.t1)
                                    .lineLimit(1)
                                Text(String(localized: "\(eps.count) 期"))
                                    .font(.yy(size: 11))
                                    .monospacedDigit()
                                    .foregroundStyle(YYColor.t3)
                                Spacer()
                                if isSingleShow {
                                    PodcastPaperPill(title: String(localized: "全部节目"), height: 23, horizontalPadding: 9) {
                                        withAnimation(YYMotion.quick) { showFilter = .all }
                                    }
                                } else if eps.count > 5 || matchedChannel != nil {
                                    PodcastPaperPill(title: String(localized: "查看全部"), height: 23, horizontalPadding: 9) {
                                        withAnimation(YYMotion.quick) { showFilter = .show(show.key) }
                                    }
                                }
                            }
                            .padding(.bottom, 6)

                            ForEach(Array(visibleEps.enumerated()), id: \.element.id) { idx, item in
                                if idx > 0 { Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5) }
                                mobileEpisodeRow(item)
                            }

                            if isSingleShow, let ch = matchedChannel, !hub.exhaustedChannelIDs.contains(ch.id) {
                                let loading = hub.loadingMoreChannelIDs.contains(ch.id)
                                Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5)
                                Button {
                                    Task { _ = await hub.loadMoreEpisodes(for: ch) }
                                } label: {
                                    HStack(spacing: 6) {
                                        if loading {
                                            ProgressView().controlSize(.mini)
                                        }
                                        Text(loading ? String(localized: "正在加载往期…") : String(localized: "加载更多往期"))
                                            .font(.yy(size: 12, weight: .medium))
                                            .foregroundStyle(loading ? YYColor.t3 : tint.accent)
                                    }
                                    .frame(maxWidth: .infinity)
                                    .padding(.vertical, 10)
                                    .contentShape(.rect)
                                }
                                .buttonStyle(.plain)
                                .disabled(loading)
                            }
                        }
                        .padding(.horizontal, 14)
                        .padding(.top, 12)
                        .padding(.bottom, 4)
                    }
                }
            }

            if showFilter == .all && externalShows.isEmpty && !hub.discoveredShows.isEmpty {
                mobileDiscoverCard
            }
        }
    }

    private var mobileStudioStarterRows: some View {
        let readingBook = library.keptBooks.first(where: { $0.status == .reading }) ?? library.keptBooks.first
        let weeklyTitle = if let book = readingBook {
            String(localized: "本周书房周刊：重读《\(book.title)》与夜间划线")
        } else {
            String(localized: "本周书房周刊：串联本周阅读与划线")
        }
        let starters: [(kind: StudioKind, title: String, accent: String, tail: String)] = [
            (
                .weeklyDigest,
                weeklyTitle,
                String(localized: "不剧透锁"),
                String(localized: "10 分")
            ),
            (
                .roundtable,
                String(localized: "跨书圆桌：挑两三本在读书同桌对谈"),
                String(localized: "跨书对谈"),
                String(localized: "15 分 · 防剧透")
            ),
            (
                .bookGuide,
                String(localized: "难书与论文导读：晦涩章节直觉精讲"),
                String(localized: "读前热身"),
                String(localized: "8 分")
            ),
            (
                .vocabRadio,
                String(localized: "生词伴听 · 通勤外语台（融入本周生词）"),
                String(localized: "生词本联动"),
                String(localized: "12 分 · 0.9×")
            ),
        ]

        return ForEach(Array(starters.enumerated()), id: \.element.kind) { idx, item in
            if idx > 0 { Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5) }
            HStack(spacing: 11) {
                VStack(alignment: .leading, spacing: 3) {
                    Text(item.title)
                        .font(.yy(size: 13.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(2)
                    HStack(spacing: 5) {
                        Text(item.accent).foregroundStyle(tint.accent)
                        Text("·").foregroundStyle(YYColor.t3)
                        Text(item.tail).foregroundStyle(YYColor.t3)
                    }
                    .font(.yy(size: 11.5))
                    .lineLimit(1)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(.rect)
                .onTapGesture { studioKind = item.kind }

                Button {
                    studioKind = item.kind
                } label: {
                    Image(systemName: "play.fill")
                        .font(.system(size: 9.5, weight: .semibold))
                        .foregroundStyle(YYColor.t2)
                        .offset(x: 1)
                        .frame(width: 29, height: 29)
                        .background(Circle().fill(YYColor.card))
                        .overlay(Circle().strokeBorder(PodcastStyle.pillBorder(scheme), lineWidth: 0.5))
                        .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                        .contentShape(Circle())
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
            }
            .padding(.vertical, 10)
        }
    }

    private func mobileEpisodeRow(_ item: PodcastIssue) -> some View {
        let isCur = hub.activeIssueID == item.id
        let isPlaying = isCur && hub.isPlaying
        let accentMeta: String = {
            if let kind = item.studioKind { return "\(kind.title) · 防剧透" }
            if item.language.lowercased().hasPrefix("en") { return PodcastFormat.trackLabel(item.track) }
            return String(localized: "中文原声")
        }()

        return HStack(spacing: 11) {
            VStack(alignment: .leading, spacing: 3) {
                Text(item.title)
                    .font(.yy(size: 13.5, weight: isCur ? .semibold : .medium))
                    .foregroundStyle(isCur ? tint.accent : YYColor.t1)
                    .lineLimit(2)
                HStack(spacing: 5) {
                    Text(accentMeta).foregroundStyle(tint.accent)
                    Text("·").foregroundStyle(YYColor.t3)
                    Text(PodcastFormat.compactDuration(item.durationSec)).monospacedDigit().foregroundStyle(YYColor.t3)
                }
                .font(.yy(size: 11.5))
                .lineLimit(1)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .contentShape(.rect)
            .onTapGesture { openedIssueID = item.id }

            Button {
                hub.selectAndPlay(item)
            } label: {
                Group {
                    if isPlaying {
                        PodcastEqualizerBars(active: true)
                    } else {
                        Image(systemName: "play.fill")
                            .font(.system(size: 9.5, weight: .semibold))
                            .foregroundStyle(isCur ? tint.accent : YYColor.t2)
                            .offset(x: 1)
                    }
                }
                .frame(width: 29, height: 29)
                .background(Circle().fill(isCur ? PodcastStyle.rowTintWash(tint, scheme) : YYColor.card))
                .overlay(
                    Circle().strokeBorder(
                        isCur ? PodcastStyle.accentRing(tint, scheme) : PodcastStyle.pillBorder(scheme),
                        lineWidth: 0.5
                    )
                )
                .shadow(color: isCur ? .clear : PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                .contentShape(Circle())
            }
            .buttonStyle(YYPressStyle(scale: 0.94))
        }
        .padding(.vertical, 10)
    }

    private var mobileDiscoverCard: some View {
        let subscribedFeeds = Set(hub.channels.map(\.feedUrl))
        return PodcastStationeryCard(cornerRadius: 14) {
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text("精选播客")
                        .font(YYUI.serif(14.5, .semibold))
                        .foregroundStyle(YYColor.t1)
                    Spacer()
                    Button { adding = true } label: {
                        Text("搜索更多 ›")
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.bottom, 6)

                ForEach(Array(hub.discoveredShows.prefix(4).enumerated()), id: \.element.id) { idx, hit in
                    if idx > 0 { Rectangle().fill(YYColor.line.opacity(0.65)).frame(height: 0.5) }
                    let isSub = subscribedFeeds.contains(hit.feedURL)
                    let busy = hub.subscribingFeedURLs.contains(hit.feedURL)
                    HStack(spacing: 10) {
                        PodcastShowMark(title: hit.title, artworkURL: hit.artworkURL, size: 28)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(hit.title)
                                .font(.yy(size: 13, weight: .medium))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            Text([hit.author, hit.genre].compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · "))
                                .font(.yy(size: 11))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        Spacer(minLength: 8)
                        if isSub {
                            Text("已订阅").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                        } else {
                            PodcastPaperPill(
                                title: busy ? String(localized: "订阅中") : String(localized: "订阅"),
                                accent: true,
                                height: 24,
                                horizontalPadding: 10
                            ) {
                                Task { _ = await hub.subscribeFeaturedShow(hit) }
                            }
                            .disabled(busy)
                        }
                    }
                    .padding(.vertical, 9)
                }
            }
            .padding(.horizontal, 14)
            .padding(.top, 12)
            .padding(.bottom, 6)
        }
    }

    // MARK: - Actions

    private func handleSearchSubmit() {
        let q = searchQuery.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return }
        if looksLikeURL {
            Task {
                if await hub.subscribeRSS(urlString: q, putLatestOnDesk: true) {
                    searchQuery = ""
                }
            }
        } else {
            addInitialQuery = q
            adding = true
        }
    }
}

/// iPhone Floating Mini Podcast Player Bar — modeled directly on macOS `macSlimPlayerBar`
/// and adapted for mobile touch ergonomics (`14pt` crisp stationery card, thin accent progress
/// line across the top, show mark + title/clock, `-15s · 30pt ▶ · +15s`, and quick clip button).
struct PhonePodcastMiniPlayerBar: View {
    let library: Library
    let issue: PodcastIssue
    let onOpenReader: () -> Void

    @State private var clipFlash = false
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(EInk.key) private var eink = false
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var hub: PodcastHub { library.podcastHub }
    private var isCurrent: Bool { hub.activeIssueID == issue.id }
    private var isPlaying: Bool { isCurrent && hub.isPlaying }
    private var pos: Double { isCurrent ? hub.currentTime : issue.positionSec }
    private var dur: Double { isCurrent && hub.duration > 0 ? hub.duration : Double(issue.durationSec) }
    private var frac: Double { dur > 0 ? min(1, max(0, pos / dur)) : 0 }

    var body: some View {
        VStack(spacing: 0) {
            // Thin accent progress rail along the top edge
            GeometryReader { g in
                ZStack(alignment: .leading) {
                    Rectangle().fill(eink ? YYColor.line.opacity(0.6) : YYColor.inset.opacity(0.8))
                    if frac > 0 {
                        Rectangle()
                            .fill(eink ? YYColor.t1 : tint.accent)
                            .frame(width: max(3, g.size.width * frac))
                    }
                }
            }
            .frame(height: 2.5)

            HStack(spacing: 8) {
                HStack(spacing: 9) {
                    PodcastShowMark(
                        title: issue.studioKind != nil ? String(localized: "书房电台") : issue.showTitle,
                        artworkURL: issue.artworkUrl,
                        isStudio: issue.studioKind != nil,
                        size: 28
                    )

                    VStack(alignment: .leading, spacing: 1.5) {
                        Text(issue.title)
                            .font(.yy(size: 12.5, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)
                        Text("\(PodcastFormat.clock(pos)) / \(PodcastFormat.clock(dur))")
                            .font(.yy(size: 10.5))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .contentShape(.rect)
                .onTapGesture(perform: onOpenReader)

                // macOS-matched transport (`-15s · 30pt ▶ · +15s · 划线`)
                HStack(spacing: 4) {
                    Button { hub.skip(by: -15) } label: {
                        Text("-15s")
                            .font(.yy(size: 10.5, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t2)
                            .frame(minWidth: 38, minHeight: 44)
                            .contentShape(.rect)
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel(String(localized: "后退 15 秒"))

                    Button { hub.selectAndPlay(issue) } label: {
                        Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                            .contentTransition(.symbolEffect(.replace))
                            .animation(YYMotion.quick, value: isPlaying)
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(YYColor.card)
                            .offset(x: isPlaying ? 0 : 1)
                            .frame(width: 30, height: 30)
                            .background(YYColor.t1, in: Circle())
                            .frame(minWidth: 38, minHeight: 44)
                            .contentShape(.rect)
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel(isPlaying ? String(localized: "暂停") : String(localized: "播放"))

                    Button { hub.skip(by: 15) } label: {
                        Text("+15s")
                            .font(.yy(size: 10.5, weight: .medium))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t2)
                            .frame(minWidth: 38, minHeight: 44)
                            .contentShape(.rect)
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel(String(localized: "前进 15 秒"))

                    Button {
                        Task {
                            if !isCurrent {
                                hub.start(issue, from: pos)
                                hub.pause()
                            }
                            if await hub.markCurrentMoment() != nil {
                                withAnimation(YYMotion.quick) { clipFlash = true }
                                try? await Task.sleep(for: .seconds(1.4))
                                withAnimation(YYMotion.quick) { clipFlash = false }
                            }
                        }
                    } label: {
                        PodcastGlyphView(
                            glyph: clipFlash ? .bookmarkFilled : .bookmark,
                            size: 13.5,
                            lineWidth: 1.45
                        )
                        .foregroundStyle(clipFlash ? tint.accent : YYColor.t2)
                        .frame(minWidth: 38, minHeight: 44)
                        .contentShape(.rect)
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel(String(localized: "原声划线"))
                }
                .fixedSize()
            }
            .padding(.horizontal, 10)
            .frame(height: 50)
        }
        .background(YYColor.card)
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(eink ? YYColor.line : PodcastStyle.hairline(scheme), lineWidth: eink ? 0.75 : 0.5)
        )
        .shadow(
            color: eink ? .clear : (scheme == .dark ? Color.black.opacity(0.45) : Color(yy: YYRGBA(25, 22, 18, 0.14))),
            radius: 16,
            y: 6
        )
    }
}
#endif
