import SwiftUI
import YuyueCore

/// Podcast Episode Listening-Reading View ("访谈书页"):
/// - Top bar: `返回收听台` back button on the left; right tools with `26pt` recessed
///   segmented track switch (`英文原声` | `原声线中文` | `双语助教`), `盲听` pill (inline `6pt` blur on non-hovered
///   turns), and `原声划线` pill with bookmark glyph.
/// - Right-edge detached floating circular drawer triggers (`原声书摘 · 稍后听` + `声音卡 · 本机整理`,
///   zero layout jump on hover) and floating stationery side drawer (`PodcastDrawerContentView`).
/// - Centered `650pt` editorial sheet: show emblem kicker, `25pt` Songti title, meta bar, rounded
///   inset ad-fold bar (`已静默折叠 1 段赞助口播 · 展开`), and 2-column timestamp + speaker/serif turns with soft sage
///   active highlight (`PodcastStyle.rowTintWash`) and `1.6pt` dotted vocabulary underlines.
/// - Floating inline popover (`310pt` wide, `14pt` radius) anchored directly beneath the
///   clicked dotted word (`word /phonetic/`, Songti contextual definition, `语境释义`, `+ 收进生词本`) or inline book
///   source pill.
struct PodcastReaderView: View {
    let library: Library
    let issueID: String
    var onOpenBook: ((String) -> Void)? = nil
    var onSelectIssue: ((String) -> Void)? = nil
    let onClose: () -> Void

    struct VocabEntry: Equatable, Identifiable {
        var id: String { word.lowercased() }
        let word: String
        let phonetic: String
        let meaning: String
        let zhTerm: String?
    }

    enum InlinePopoverKind: Equatable {
        case vocab(VocabEntry)
        case source(bookID: String?, bookTitle: String, lockNote: String, quote: String)
    }

    struct InlinePopoverState: Equatable {
        let sentenceID: String
        var anchorRect: CGRect? = nil
        let kind: InlinePopoverKind
    }

    @State private var listenOnly = false
    @State private var expandedSkipIndices: Set<Int> = []
    @State private var interrupting = false
    @State private var interruptQuestion = ""
    @State private var sendingInterrupt = false
    @State private var latestInterruptReply: PodcastInterrupt?
    @State private var edition: PodcastEdition = .full
    @State private var track: PodcastTrack = .original
    @State private var scrubbingValue: Double?
    @State private var hoverScrubSec: Double?
    @State private var hoveringScrub = false
    @State private var hoveredControl: String?
    @State private var hoveredSentenceID: String?
    @State private var hoveredVocabID: String?
    @State private var peekedSentenceID: String?
    @State private var clipFlash = false
    @State private var clipFlashTask: Task<Void, Never>?

    // Right-edge floating circular triggers & stationery side drawer
    @State private var drawerOpen = false
    @State private var drawerTab: PodcastDrawerTab = .clips
    @State private var hasNewClipDot = false
    @State private var hoveredDrawerTrigger: PodcastDrawerTab?

    // Inline vocabulary / book-source popover & geometry tracking
    @State private var activePopover: InlinePopoverState?
    @State private var sentenceFrames: [String: CGRect] = [:]
    @State private var wordRectStore = PodcastWordRectStore()
    @State private var sheetWidth: CGFloat = 650
    @State private var savedVocabWords: [VocabWord] = []
    @State private var savedWordsSet: Set<String> = []

    // Bottom toast feedback
    @State private var transientToast: String?
    @State private var toastTask: Task<Void, Never>?

    @FocusState private var interruptFocused: Bool
    @Namespace private var readerTrackNS

    @Environment(\.colorScheme) private var scheme
    #if os(macOS)
    @Environment(\.shelfHeaderInset) private var headerInset
    #endif
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var hub: PodcastHub { library.podcastHub }
    private var issue: PodcastIssue? { hub.issue(issueID) }
    private var isActive: Bool { hub.activeIssueID == issueID }
    private var allSentences: [MagazineSentence] { hub.sentences(for: issueID) }
    private var highlightIDs: Set<String> { hub.highlightSet(for: issueID) }
    private var issueInterrupts: [PodcastInterrupt] { hub.interrupts(for: issueID) }
    private var issueClips: [PodcastClip] { hub.clips(for: issueID) }

    private var shownSentences: [MagazineSentence] {
        if edition == .highlights, !highlightIDs.isEmpty {
            return allSentences.filter { highlightIDs.contains($0.id) }
        }
        return allSentences
    }

    private var displayedTime: Double {
        if let scrubbingValue { return scrubbingValue }
        return isActive ? hub.currentTime : (issue?.positionSec ?? 0)
    }

    private var totalDuration: Double {
        if isActive, hub.duration > 0 { return hub.duration }
        return Double(issue?.durationSec ?? 0)
    }

    /// Active sentence ID even when paused, without subscribing the whole transcript view to 4Hz `hub.currentTime`.
    private var currentActiveSentenceID: String? {
        let spoken = shownSentences.filter { $0.skipReason == nil }
        if let scrubbingValue {
            return spoken.first(where: { scrubbingValue >= $0.t0 && scrubbingValue <= $0.t1 })?.id
                ?? spoken.last(where: { scrubbingValue >= $0.t0 })?.id
                ?? spoken.first?.id
        }
        if isActive, let liveID = hub.activeSentenceID {
            return liveID
        }
        let pos = issue?.positionSec ?? 0
        if let match = spoken.first(where: { pos >= $0.t0 && pos <= $0.t1 }) {
            return match.id
        }
        if let before = spoken.last(where: { pos >= $0.t0 }) {
            return before.id
        }
        return spoken.first?.id
    }

    var body: some View {
        Group {
            if let issue {
                GeometryReader { geo in
                    let h = geo.size.height
                    ZStack(alignment: .trailing) {
                        VStack(spacing: 0) {
                            topBar(issue)

                            magazineColumn(issue)
                                .mask {
                                    VStack(spacing: 0) {
                                        Rectangle()
                                        LinearGradient(colors: [.black, .black.opacity(0)], startPoint: .top, endPoint: .bottom)
                                            .frame(height: 14)
                                    }
                                }

                            slimPlayerDock(issue)
                        }
                        .overlay(alignment: .bottom) {
                            interruptOverlay(issue)
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity)

                        #if os(macOS)
                        // Right-Edge Detached Floating Circular Drawer Triggers (`.index-tabs`)
                        if !drawerOpen {
                            rightEdgeCircularTriggers
                                .padding(.trailing, 12)
                                .offset(y: -h * 0.04)
                                .transition(.move(edge: .trailing).combined(with: .opacity))
                        }

                        // Floating Stationery Side Drawer (`.harbor-drawer`)
                        if drawerOpen {
                            Color.black.opacity(scheme == .dark ? 0.28 : 0.08)
                                .ignoresSafeArea()
                                .onTapGesture { closeDrawer() }
                                .transition(.opacity)

                            PodcastDrawerContentView(
                                library: library,
                                tab: $drawerTab,
                                onOpenIssue: { targetID in
                                    closeDrawer()
                                    if targetID != issue.id {
                                        onSelectIssue?(targetID)
                                    }
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
                        #endif

                        // Floating bottom confirmation toast (`.toast`)
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
                                    .padding(.bottom, 64)
                            }
                            .frame(maxWidth: .infinity)
                            .allowsHitTesting(false)
                            .transition(.move(edge: .bottom).combined(with: .opacity))
                        }
                    }
                }
                .background(YYColor.bg)
                #if os(macOS)
                .ignoresSafeArea(.container, edges: .top)
                #endif
                .animation(YYMotion.standard, value: interrupting)
                .animation(YYMotion.standard, value: drawerOpen)
                .animation(YYMotion.quick, value: clipFlash)
                .animation(YYMotion.quick, value: transientToast)
                .animation(YYMotion.quick, value: activePopover)
                .onAppear {
                    edition = issue.edition
                    track = issue.track
                    reloadSavedWords()
                    #if DEBUG
                    let dbg = UserDefaults.standard.string(forKey: "debugPodcast")
                    if dbg == "reader-coach" { track = .coach }
                    if dbg == "reader-orig" { track = .original }
                    if dbg == "reader-interrupt" { interrupting = true }
                    if dbg == "reader-reply" {
                        interrupting = true
                        latestInterruptReply = issueInterrupts.first
                    }
                    #endif
                }
                .onReceive(NotificationCenter.default.publisher(for: .vocabChanged)) { _ in
                    reloadSavedWords()
                }
                .onDisappear {
                    hub.stopVoiceInterrupt()
                    hub.stopSpeakingInterruptAnswer()
                }
            } else {
                ContentUnavailableView(String(localized: "这期节目不在了"), systemImage: "headphones")
            }
        }
        #if os(macOS)
        .onKeyPress("h") {
            if !interruptFocused {
                triggerClipMoment()
                return .handled
            }
            return .ignored
        }
        .onKeyPress("1") {
            if !interruptFocused, let issue, supportsDub(issue) {
                switchTrack(.original, for: issue)
                return .handled
            }
            return .ignored
        }
        .onKeyPress("2") {
            if !interruptFocused, let issue, supportsDub(issue) {
                switchTrack(.dub, for: issue)
                return .handled
            }
            return .ignored
        }
        .onKeyPress("3") {
            if !interruptFocused, let issue, supportsDub(issue) {
                switchTrack(.coach, for: issue)
                return .handled
            }
            return .ignored
        }
        .onKeyPress("i") {
            if !interruptFocused {
                if interrupting {
                    closeInterrupt()
                } else {
                    beginInterrupt()
                }
                return .handled
            }
            return .ignored
        }
        .onKeyPress(.escape) {
            if activePopover != nil {
                withAnimation(YYMotion.quick) { activePopover = nil }
                return .handled
            }
            if drawerOpen {
                closeDrawer()
                return .handled
            }
            if interrupting {
                closeInterrupt()
                return .handled
            }
            return .ignored
        }
        .onKeyPress(.space) {
            if !interruptFocused, let issue {
                hub.selectAndPlay(issue)
                return .handled
            }
            return .ignored
        }
        #endif
    }

    // MARK: - Top Bar (`.rd-top` on Mac, `.mob-rd-top` + `.mob-rd-track` on iPhone)

    @ViewBuilder
    private func topBar(_ issue: PodcastIssue) -> some View {
        #if os(macOS)
        HStack(spacing: 16) {
            Button(action: onClose) {
                HStack(spacing: 5) {
                    Image(systemName: "chevron.left")
                        .font(.system(size: 11.5, weight: .semibold))
                    Text("返回收听台")
                        .font(.yy(size: 12.5, weight: .medium))
                }
                .foregroundStyle(hoveredControl == "back" ? YYColor.t1 : YYColor.t2)
                .padding(.leading, 6)
                .padding(.trailing, 10)
                .padding(.vertical, 5)
                .background(
                    RoundedRectangle(cornerRadius: 8, style: .continuous)
                        .fill(hoveredControl == "back" ? YYColor.inset : Color.clear)
                )
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .onHover { over in
                hoveredControl = over ? "back" : (hoveredControl == "back" ? nil : hoveredControl)
            }

            Spacer(minLength: 12)

            // `.rd-tools`: `26pt` recessed track switch + `盲听` pill + `原声划线` pill
            HStack(spacing: 10) {
                if supportsDub(issue) {
                    readerTrackSwitch(issue)
                } else {
                    Text(studioAccentNote(issue))
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(tint.accent)
                        .padding(.horizontal, 8)
                }

                blindTogglePill
                clipActionPill
            }
        }
        .frame(height: 52)
        .padding(.top, 8)
        .padding(.leading, headerInset > 0 ? 52 : 36)
        .padding(.trailing, 32)
        .background(YYColor.bg)
        .overlay(alignment: .bottom) {
            Rectangle().fill(YYColor.line).frame(height: 0.5)
        }
        #else
        VStack(spacing: 0) {
            // `.mob-rd-top`
            HStack(spacing: 12) {
                Button(action: onClose) {
                    HStack(spacing: 5) {
                        Image(systemName: "chevron.left")
                            .font(.system(size: 12, weight: .semibold))
                        Text("播客")
                            .font(.yy(size: 13, weight: .medium))
                    }
                    .foregroundStyle(YYColor.t2)
                    .padding(.vertical, 5)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)

                Spacer(minLength: 8)

                HStack(spacing: 6) {
                    blindTogglePill
                    clipActionPill
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 10)
            .background(YYColor.bg)
            .overlay(alignment: .bottom) {
                Rectangle().fill(YYColor.line).frame(height: 0.5)
            }

            // `.mob-rd-track`
            HStack(spacing: 8) {
                if supportsDub(issue) {
                    readerTrackSwitch(issue)
                } else {
                    Text(studioAccentNote(issue))
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(tint.accent)
                }
                Spacer(minLength: 6)
                Text(supportsDub(issue) ? String(localized: "同句无缝切轨") : String(localized: "防剧透锁生效"))
                    .font(.yy(size: 11))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 8)
            .background(YYColor.card)
            .overlay(alignment: .bottom) {
                Rectangle().fill(YYColor.line).frame(height: 0.5)
            }
        }
        #endif
    }

    private var blindTogglePill: some View {
        PodcastPaperPill(
            title: listenOnly ? String(localized: "退出盲听") : String(localized: "盲听"),
            accent: listenOnly,
            height: 26,
            horizontalPadding: 11
        ) {
            withAnimation(YYMotion.quick) {
                listenOnly.toggle()
                peekedSentenceID = nil
            }
            showToast(
                listenOnly
                    ? String(localized: "已开启盲听 · 悬停或轻点句子可偷看")
                    : String(localized: "已退出盲听")
            )
        }
    }

    private var clipActionPill: some View {
        PodcastPaperPill(
            title: clipFlash ? String(localized: "已记原声") : String(localized: "原声划线"),
            glyph: clipFlash ? .bookmarkFilled : .bookmark,
            accent: true,
            height: 26,
            horizontalPadding: 11
        ) {
            triggerClipMoment()
        }
    }

    /// `.seg-switch`: `height: 26pt` (`22pt` inner pill + `2pt` padding), exact prototype proportions.
    private func readerTrackSwitch(_ issue: PodcastIssue) -> some View {
        let origTitle = issue.language.lowercased().hasPrefix("en")
            ? String(localized: "英文原声")
            : String(localized: "原声")
        let options: [(PodcastTrack, String)] = [
            (.original, origTitle),
            (.dub, String(localized: "原声线中文")),
            (.coach, String(localized: "双语助教")),
        ]
        return HStack(spacing: 2) {
            ForEach(options, id: \.0) { item, label in
                let isOn = track == item
                Button {
                    switchTrack(item, for: issue)
                } label: {
                    Text(label)
                        .font(.yy(size: 11, weight: isOn ? .semibold : .medium))
                        .foregroundStyle(isOn ? tint.accent : YYColor.t3)
                        .lineLimit(1)
                        .fixedSize(horizontal: true, vertical: false)
                        .padding(.horizontal, 9)
                        .frame(height: 22)
                        .background {
                            if isOn {
                                Capsule()
                                    .fill(YYColor.card)
                                    .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                                    .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                                    .matchedGeometryEffect(id: "readerTrackPill", in: readerTrackNS)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(YYColor.inset, in: Capsule())
        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
        .fixedSize(horizontal: true, vertical: false)
    }

    private func switchTrack(_ newTrack: PodcastTrack, for issue: PodcastIssue) {
        withAnimation(YYMotion.quick) {
            track = newTrack
            activePopover = nil
        }
        hub.setViewMode(issueID: issue.id, edition: edition, track: newTrack)
        if let updated = hub.issue(issue.id) {
            hub.setTrack(newTrack, for: updated)
        }
        showToast(String(localized: "同句无缝切换：\(readerTrackLabel(newTrack, for: issue))"))
    }

    private func readerTrackLabel(_ t: PodcastTrack, for issue: PodcastIssue? = nil) -> String {
        switch t {
        case .original:
            if let issue, !issue.language.lowercased().hasPrefix("en") {
                return String(localized: "原声")
            }
            return String(localized: "英文原声")
        case .dub:
            return String(localized: "原声线中文")
        case .coach:
            return String(localized: "双语助教")
        }
    }

    private func supportsDub(_ issue: PodcastIssue) -> Bool {
        #if DEBUG
        if let dbg = UserDefaults.standard.string(forKey: "debugPodcast"),
           dbg == "reader-coach" || dbg == "reader-orig" {
            return true
        }
        #endif
        return hub.supportsTrackSwitch(issue)
    }

    private func studioAccentNote(_ issue: PodcastIssue) -> String {
        if let kind = issue.studioKind {
            return "\(kind.title) · 防剧透锁"
        }
        return shownSentences.isEmpty ? String(localized: "原声播放中") : String(localized: "原声 · 逐字稿对齐")
    }

    private func issueSubMeta(_ issue: PodcastIssue) -> String {
        let mins = max(1, Int(round(totalDuration / 60)))
        if issue.studioKind != nil {
            return String(localized: "\(mins) 分 · 防剧透")
        }
        let speakers = Set(allSentences.filter { $0.skipReason == nil }.compactMap(\.speaker)).count
        if speakers > 0 {
            return String(localized: "\(mins) 分 · 已识 \(max(2, speakers)) 位声纹")
        }
        return shownSentences.isEmpty
            ? String(localized: "\(mins) 分 · 原声播放")
            : String(localized: "\(mins) 分 · 逐字稿就绪")
    }

    // MARK: - Editorial Sheet & Transcript Turns (`.rd-scroll` + `.rd-sheet`)

    private func magazineColumn(_ issue: PodcastIssue) -> some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    // 1. `.rd-kicker`: Show emblem + show name + duration/speaker meta
                    HStack(spacing: 8) {
                        PodcastShowMark(
                            title: issue.studioKind != nil ? String(localized: "书房电台") : issue.showTitle,
                            artworkURL: issue.artworkUrl,
                            isStudio: issue.studioKind != nil,
                            size: 19
                        )
                        Text(issue.showTitle)
                        Text("·")
                        Text(issueSubMeta(issue))
                    }
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .padding(.bottom, 10)

                    // 2. `.rd-title`: 25pt Songti serif title
                    Text(issue.title)
                        .font(PodcastStyle.songti(25))
                        .foregroundStyle(YYColor.t1)
                        .lineSpacing(6)

                    // 3. `.rd-meta-bar`: current track + interaction hint + bottom 0.5pt hairline
                    HStack(spacing: 6) {
                        Text(
                            supportsDub(issue)
                                ? String(localized: "当前声轨：\(readerTrackLabel(track, for: issue))")
                                : studioAccentNote(issue)
                        )
                        Text("·")
                        Text(
                            supportsDub(issue)
                                ? String(localized: "点任意句子即刻跳播，点虚线词查词收进生词本")
                                : String(localized: "点任意句子即刻跳播，点书名签核对原书出处")
                        )
                    }
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .padding(.top, 10)
                    .padding(.bottom, 18)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .overlay(alignment: .bottom) {
                        Rectangle().fill(YYColor.line).frame(height: 0.5)
                    }
                    .padding(.bottom, 12)

                    if shownSentences.isEmpty {
                        l0ShownotesFallback(issue)
                            .padding(.top, 8)
                    } else {
                        transcriptList(issue)
                    }
                }
                .frame(maxWidth: 650, alignment: .leading)
                .coordinateSpace(name: "rdSheetSpace")
                .onGeometryChange(for: CGFloat.self) { geo in
                    geo.size.width
                } action: { newWidth in
                    if newWidth > 0 { sheetWidth = newWidth }
                }
                .overlay(alignment: .topLeading) {
                    if let pop = activePopover,
                       let sentRect = sentenceFrames[pop.sentenceID],
                       let sent = shownSentences.first(where: { $0.id == pop.sentenceID }) {
                        let anchor = pop.anchorRect ?? sentRect
                        let popW: CGFloat = min(310, max(240, sheetWidth - 16))
                        let clampedX = max(0, min(sheetWidth - popW, anchor.midX - popW / 2))
                        inlinePopoverCard(pop, sentence: sent, issue: issue)
                            .frame(width: popW)
                            .offset(x: clampedX, y: anchor.maxY + 8)
                            .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .top)))
                            .zIndex(200)
                    }
                }
                #if os(macOS)
                .padding(.leading, 36)
                .padding(.trailing, 58)
                .padding(.top, 30)
                .padding(.bottom, 150)
                #else
                .padding(.horizontal, 18)
                .padding(.top, 18)
                .padding(.bottom, 140)
                #endif
                .frame(maxWidth: .infinity)
                .background(
                    Color.clear
                        .contentShape(.rect)
                        .onTapGesture {
                            if activePopover != nil {
                                withAnimation(YYMotion.quick) { activePopover = nil }
                            }
                        }
                )
            }
            .scrollIndicators(.never)
            .onChange(of: hub.activeSentenceID) { _, newID in
                if let newID {
                    withAnimation(YYMotion.standard) { proxy.scrollTo(newID, anchor: .center) }
                }
            }
        }
    }

    private func transcriptList(_ issue: PodcastIssue) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            ForEach(Array(shownSentences.enumerated()), id: \.element.id) { idx, sent in
                if sent.skipReason != nil {
                    adFoldRow(sent, index: idx)
                        .id(sent.id)
                } else {
                    turnRow(sent, issue: issue)
                        .id(sent.id)
                }
            }
        }
    }

    /// `.rd-ad-fold`: Rounded 9pt `YYColor.inset` bar with `已静默折叠 1 段赞助口播（00:48）` on left and `展开` on right.
    private func adFoldRow(_ sent: MagazineSentence, index idx: Int) -> some View {
        let isExpanded = expandedSkipIndices.contains(idx)
        let durText = PodcastFormat.clock(max(1, sent.t1 - sent.t0))
        let summaryText: String = {
            if let trans = sent.textTrans, !trans.isEmpty {
                return trans
            }
            return String(localized: "已静默折叠 1 段赞助口播（\(durText)）")
        }()

        return Button {
            withAnimation(YYMotion.quick) {
                if isExpanded {
                    expandedSkipIndices.remove(idx)
                } else {
                    expandedSkipIndices.insert(idx)
                }
            }
        } label: {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 4) {
                    Text(summaryText)
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                    if isExpanded {
                        Text(sent.textOrig)
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3.opacity(0.82))
                            .padding(.top, 2)
                    }
                }
                Spacer(minLength: 8)
                Text(isExpanded ? String(localized: "收起") : String(localized: "展开"))
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 8.5)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.inset, in: RoundedRectangle(cornerRadius: 9, style: .continuous))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .padding(.vertical, 8)
    }

    /// `.rd-turn`: 2-column row (`50pt` timestamp on left + speaker & serif text on right),
    /// active sage background card (`PodcastStyle.rowTintWash`), inline blind-mode blur,
    /// dotted vocabulary underlines (`.vocab-w`), and inline book source pills (`.src-link`).
    private func turnRow(_ sent: MagazineSentence, issue: PodcastIssue) -> some View {
        let isAct = currentActiveSentenceID == sent.id
        let isHov = hoveredSentenceID == sent.id
        let isPeeked = isHov || peekedSentenceID == sent.id
        let shouldBlur = listenOnly && !isPeeked
        let spk = speakerMeta(for: sent, issue: issue)
        let disp = turnDisplay(for: sent, issue: issue)

        return HStack(alignment: .top, spacing: 14) {
            #if os(macOS)
            Text(PodcastFormat.clock(sent.t0))
                .font(.yy(size: 11))
                .monospacedDigit()
                .foregroundStyle(YYColor.t3)
                .frame(width: 50, alignment: .leading)
                .padding(.top, 4)
            #endif

            VStack(alignment: .leading, spacing: 0) {
                // `.rd-spk`
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(spk.name)
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(tint.accent)
                    #if os(macOS)
                    if !spk.tag.isEmpty {
                        Text(spk.tag)
                            .font(.yy(size: 11))
                            .foregroundStyle(YYColor.t3)
                    }
                    #else
                    Text(PodcastFormat.clock(sent.t0))
                        .font(.yy(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t3)
                    #endif
                }
                .padding(.bottom, 5)

                // `.rd-text` + optional `.src-link`
                VStack(alignment: .leading, spacing: 7) {
                    decoratedSentenceText(disp.main, sentence: sent, issue: issue)
                        .lineSpacing(10)
                        .multilineTextAlignment(.leading)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .onGeometryChange(for: CGRect.self) { geo in
                            geo.frame(in: .named("rdSheetSpace"))
                        } action: { rect in
                            sentenceFrames[sent.id] = rect
                        }

                    if let bTitle = sent.bookTitle, !bTitle.isEmpty {
                        sourceLinkPill(sentence: sent, bookTitle: bTitle)
                    }

                    // `.rd-text-sub` (shown in `双语助教` mode)
                    if let sub = disp.sub, !sub.isEmpty {
                        Text(sub)
                            .font(PodcastStyle.songti(13.5))
                            .foregroundStyle(YYColor.t2)
                            .lineSpacing(6)
                            .multilineTextAlignment(.leading)
                            .padding(.top, 2)
                    }
                }
                .blur(radius: shouldBlur ? 6 : 0)
                .opacity(shouldBlur ? 0.4 : 1.0)
                .animation(YYMotion.quick, value: shouldBlur)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 13)
        .background(
            RoundedRectangle(cornerRadius: 11, style: .continuous)
                .fill(
                    isAct
                        ? PodcastStyle.rowTintWash(tint, scheme)
                        : (isHov ? YYColor.inset.opacity(0.55) : Color.clear)
                )
        )
        .padding(.horizontal, -12)
        .contentShape(.rect)
        .onTapGesture {
            selectTurn(sent, issue: issue)
        }
        #if os(macOS)
        .onHover { over in
            withAnimation(YYMotion.quick) {
                hoveredSentenceID = over ? sent.id : (hoveredSentenceID == sent.id ? nil : hoveredSentenceID)
            }
        }
        #endif
    }

    private func selectTurn(_ sent: MagazineSentence, issue: PodcastIssue) {
        if listenOnly && peekedSentenceID != sent.id {
            withAnimation(YYMotion.quick) { peekedSentenceID = sent.id }
        }
        if activePopover != nil {
            withAnimation(YYMotion.quick) { activePopover = nil }
        }
        if !isActive {
            hub.start(issue, from: sent.t0)
        } else {
            hub.jump(to: sent)
        }
    }

    private func speakerMeta(for sent: MagazineSentence, issue: PodcastIssue) -> (name: String, tag: String) {
        let raw = (sent.speaker ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let name = raw.isEmpty ? issue.showTitle : raw
        if issue.studioKind != nil {
            let tag = issue.language.lowercased().hasPrefix("en")
                ? String(localized: "温和英音")
                : String(localized: "本地声卡")
            return (name, tag)
        }
        if supportsDub(issue) {
            switch track {
            case .original:
                return (name, String(localized: "原声线 · 8.1s 采样"))
            case .dub:
                return (name, String(localized: "原声线克隆 · 保留原本韵律"))
            case .coach:
                return (name, String(localized: "原声线 + 双语助教"))
            }
        }
        return (name, String(localized: "原声"))
    }

    private func turnDisplay(for sent: MagazineSentence, issue: PodcastIssue) -> (main: String, sub: String?) {
        if !supportsDub(issue) {
            return (sent.textTrans ?? sent.textOrig, nil)
        }
        switch track {
        case .original:
            return (sent.textOrig, nil)
        case .dub:
            return (sent.textTrans ?? sent.textOrig, nil)
        case .coach:
            let sub = (sent.textTrans?.isEmpty == false) ? sent.textTrans : nil
            return (sent.textOrig, sub)
        }
    }

    // MARK: - Dotted Vocabulary Underlines (`.vocab-w`) & Book Source Pills (`.src-link`)

    private static let builtinLexicon: [String: (phonetic: String, meaning: String, zhTerm: String)] = [
        "generalization": (
            "/ˌdʒen.rəl.əˈzeɪ.ʃən/",
            "泛化能力：把已有经验迁移到全新未见过问题的能力",
            "泛化能力"
        ),
        "fluid intelligence": (
            "/ˈfluː.ɪd ɪnˈtel.ɪ.dʒəns/",
            "流体智力：不依赖既有知识储备、现场推理解决陌生问题的能力",
            "流体智力"
        ),
        "heuristics": (
            "/hjʊəˈrɪs.tɪks/",
            "启发式方法：凭经验快速判断的捷径规则",
            "启发式套路"
        ),
        "saturation": (
            "/ˌsætʃ.əˈreɪ.ʃən/",
            "饱和度：指基准测试分数很快刷满却不代表能力真正突破",
            "跑分饱和"
        ),
        "primitives": (
            "/ˈprɪm.ɪ.tɪvz/",
            "基本原语：可组合的最小操作单元",
            "基本原语"
        ),
        "test-time adaptation": (
            "/ˈtest.taɪm ˌæd.æpˈteɪ.ʃən/",
            "测试时适应：面对新题实时搜索与修正推理路径",
            "测试时现场适应"
        ),
        "source of truth": (
            "/sɔːs əv truːθ/",
            "唯一真实数据源：系统中的基准事实来源",
            "唯一真实数据源"
        ),
        "agency": (
            "/ˈeɪ.dʒən.si/",
            "能动性：自主决定与掌控行动的能力",
            "掌控"
        ),
        "oases": (
            "/əʊˈeɪ.siːz/",
            "绿洲（oasis 复数）：沙漠贸易路线中的水源与停驻点",
            "绿洲"
        ),
        "prosody": (
            "/ˈprɒz.ə.di/",
            "韵律：语音中的语调、停顿与轻重节奏",
            "韵律"
        ),
        "fastidious": (
            "/fæsˈtɪd.i.əs/",
            "挑剔的：对细节一丝不苟的（摘自 Pride and Prejudice）",
            "fastidious"
        ),
        "zero-copy": (
            "/ˈzɪə.rəʊ ˈkɒp.i/",
            "零拷贝：直接切片读取而不重复分配内存的流式解析方式",
            "零拷贝"
        ),
        "latency": (
            "/ˈleɪ.tən.si/",
            "延迟：从输入触发到响应输出之间的等待周期",
            "延迟"
        ),
        "moat": (
            "/məʊt/",
            "护城河：难以被竞争对手复制的长期结构性壁垒",
            "护城河"
        ),
    ]

    private func vocabEntries(for sent: MagazineSentence) -> [VocabEntry] {
        var entries: [VocabEntry] = []
        var seen = Set<String>()

        // 1. Explicit coachNote ("word：meaning")
        if let note = sent.coachNote?.trimmingCharacters(in: .whitespacesAndNewlines), !note.isEmpty {
            let parts = note.split(maxSplits: 1, whereSeparator: { $0 == "：" || $0 == ":" })
            if parts.count == 2 {
                let rawWord = String(parts[0]).trimmingCharacters(in: .whitespacesAndNewlines)
                let key = rawWord.lowercased()
                var meaning = String(parts[1]).trimmingCharacters(in: .whitespacesAndNewlines)
                if meaning.hasSuffix("。") { meaning.removeLast() }
                if !meaning.contains("："), let commaIdx = meaning.firstIndex(of: "，") {
                    meaning.replaceSubrange(commaIdx...commaIdx, with: "：")
                }
                let builtin = Self.builtinLexicon[key]
                let ph = builtin?.phonetic ?? SystemDictionary.define(rawWord)?.phonetic ?? "/\(key)/"
                let zhPrefix = meaning.split(separator: "：").first.map(String.init)
                if seen.insert(key).inserted {
                    entries.append(
                        VocabEntry(
                            word: rawWord,
                            phonetic: ph,
                            meaning: builtin?.meaning ?? meaning,
                            zhTerm: builtin?.zhTerm ?? zhPrefix
                        )
                    )
                }
            }
        }

        // 2. Built-in lexicon matches in `sent.textOrig`
        let lowerOrig = sent.textOrig.lowercased()
        for (wordKey, info) in Self.builtinLexicon where !seen.contains(wordKey) {
            if lowerOrig.contains(wordKey) {
                seen.insert(wordKey)
                entries.append(
                    VocabEntry(
                        word: wordKey,
                        phonetic: info.phonetic,
                        meaning: info.meaning,
                        zhTerm: info.zhTerm
                    )
                )
            }
        }

        // 3. User's own 生词本 words appearing in this sentence
        for w in savedVocabWords where !w.known {
            let key = w.word.lowercased()
            guard !seen.contains(key), key.count >= 3, lowerOrig.contains(key) else { continue }
            seen.insert(key)
            entries.append(
                VocabEntry(
                    word: w.word,
                    phonetic: w.phonetic ?? "/\(key)/",
                    meaning: w.meaning ?? String(localized: "已加入生词本的词条"),
                    zhTerm: nil
                )
            )
        }

        return entries
    }

    private func decoratedSentenceText(_ text: String, sentence sent: MagazineSentence, issue: PodcastIssue) -> some View {
        let entries = vocabEntries(for: sent)
        let isLatinText = text.unicodeScalars.filter { $0.isASCII && CharacterSet.letters.contains($0) }.count > text.count / 3
        let bodyFont: SwiftUI.Font = isLatinText
            ? .custom("Georgia", size: 16.5 * TextScale.shared.factor)
            : PodcastStyle.songti(16.5)

        struct SpanMatch {
            let range: Range<String.Index>
            let entry: VocabEntry
        }
        var matches: [SpanMatch] = []
        for entry in entries {
            let candidates = [entry.word, entry.zhTerm].compactMap { $0 }.filter { !$0.isEmpty }
            for term in candidates {
                if let r = text.range(of: term, options: [.caseInsensitive]) {
                    let overlaps = matches.contains { $0.range.overlaps(r) }
                    if !overlaps {
                        matches.append(SpanMatch(range: r, entry: entry))
                        break
                    }
                }
            }
        }
        matches.sort { $0.range.lowerBound < $1.range.lowerBound }

        let composedText: Text = {
            guard !matches.isEmpty else {
                return Text(text)
                    .font(bodyFont)
                    .foregroundStyle(YYColor.t1)
            }
            var result = Text("")
            var cursor = text.startIndex
            for m in matches {
                if cursor < m.range.lowerBound {
                    let plain = String(text[cursor..<m.range.lowerBound])
                    result = result + Text(plain)
                        .font(bodyFont)
                        .foregroundStyle(YYColor.t1)
                }
                let matchedStr = String(text[m.range])
                let vocabKey = "\(sent.id):\(m.entry.word.lowercased())"
                let isWordHovered = hoveredVocabID == vocabKey
                let isWordActive: Bool = {
                    guard let pop = activePopover, pop.sentenceID == sent.id else { return false }
                    if case .vocab(let e) = pop.kind {
                        return e.word.caseInsensitiveCompare(m.entry.word) == .orderedSame
                    }
                    return false
                }()

                let wordText: Text
                if #available(macOS 15.0, iOS 18.0, *) {
                    wordText = Text(matchedStr)
                        .font(bodyFont)
                        .foregroundStyle(tint.accent)
                        .customAttribute(
                            PodcastDottedWordAttribute(
                                key: vocabKey,
                                isHighlighted: isWordHovered || isWordActive,
                                store: wordRectStore
                            )
                        )
                } else {
                    wordText = Text(matchedStr)
                        .font(bodyFont)
                        .foregroundStyle(tint.accent)
                        .underline(true, pattern: .dot, color: tint.accent)
                }

                result = result + wordText
                cursor = m.range.upperBound
            }
            if cursor < text.endIndex {
                let tail = String(text[cursor..<text.endIndex])
                result = result + Text(tail)
                    .font(bodyFont)
                    .foregroundStyle(YYColor.t1)
            }
            return result
        }()

        let renderedText = Group {
            if #available(macOS 15.0, iOS 18.0, *) {
                composedText.textRenderer(PodcastDottedTextRenderer(dotColor: tint.accent))
            } else {
                composedText
            }
        }
        return renderedText
            .contentShape(.rect)
            #if os(macOS)
            .onContinuousHover(coordinateSpace: .local) { phase in
                switch phase {
                case .active(let loc):
                    let hit = matches.first { m in
                        let k = "\(sent.id):\(m.entry.word.lowercased())"
                        if let r = wordRectStore.rects[k] {
                            return r.insetBy(dx: -8, dy: -6).contains(loc)
                        }
                        return false
                    }
                    let nextID = hit.map { "\(sent.id):\($0.entry.word.lowercased())" }
                    if hoveredVocabID != nextID {
                        hoveredVocabID = nextID
                    }
                case .ended:
                    if hoveredVocabID?.hasPrefix("\(sent.id):") == true {
                        hoveredVocabID = nil
                    }
                }
            }
            #endif
            .highPriorityGesture(
                SpatialTapGesture(coordinateSpace: .local).onEnded { value in
                    let loc = value.location
                    if let hit = matches.first(where: { m in
                        let k = "\(sent.id):\(m.entry.word.lowercased())"
                        if let r = wordRectStore.rects[k] {
                            return r.insetBy(dx: -12, dy: -10).contains(loc)
                        }
                        return false
                    }) {
                        let k = "\(sent.id):\(hit.entry.word.lowercased())"
                        let localRect = wordRectStore.rects[k] ?? CGRect(x: loc.x - 30, y: loc.y - 10, width: 60, height: 20)
                        let baseRect = sentenceFrames[sent.id] ?? .zero
                        let wordSheetRect = CGRect(
                            x: baseRect.minX + localRect.minX,
                            y: baseRect.minY + localRect.minY,
                            width: localRect.width,
                            height: localRect.height
                        )
                        let next = InlinePopoverState(
                            sentenceID: sent.id,
                            anchorRect: wordSheetRect,
                            kind: .vocab(hit.entry)
                        )
                        withAnimation(YYMotion.quick) {
                            if let cur = activePopover, cur.sentenceID == sent.id, cur.kind == .vocab(hit.entry) {
                                activePopover = nil
                            } else {
                                activePopover = next
                            }
                        }
                    } else {
                        selectTurn(sent, issue: issue)
                    }
                }
            )
    }

    /// `.src-link`: Compact inline porcelain pill for book source verification (`《红楼梦》· 第二回`).
    private func sourceLinkPill(sentence sent: MagazineSentence, bookTitle: String) -> some View {
        let formatted = formattedBookTitle(bookTitle)
        let lockNote = String(localized: "已读进度内 · 防剧透")
        let quoteText: String = {
            if let q = sent.bookQuote, !q.isEmpty {
                return q.hasPrefix("“") || q.hasPrefix("「") ? q : "“\(q)”"
            }
            return String(localized: "“所有引用均严格锁定在你已读到的章节进度之内。”")
        }()
        let targetKind = InlinePopoverKind.source(
            bookID: sent.bookId,
            bookTitle: formatted,
            lockNote: lockNote,
            quote: quoteText
        )
        let isOpen = activePopover?.sentenceID == sent.id && activePopover?.kind == targetKind

        return Button {
            withAnimation(YYMotion.quick) {
                if isOpen {
                    activePopover = nil
                } else {
                    activePopover = InlinePopoverState(
                        sentenceID: sent.id,
                        anchorRect: sentenceFrames[sent.id],
                        kind: targetKind
                    )
                }
            }
        } label: {
            Text(formatted)
                .font(.yy(size: 11, weight: .medium))
                .foregroundStyle(tint.accent)
                .padding(.horizontal, 9)
                .padding(.vertical, 3)
                .background(
                    Capsule().fill(isOpen ? PodcastStyle.rowTintWash(tint, scheme) : YYColor.card)
                )
                .overlay(
                    Capsule().strokeBorder(
                        isOpen ? PodcastStyle.accentRing(tint, scheme) : PodcastStyle.pillBorder(scheme),
                        lineWidth: 0.5
                    )
                )
                .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                .contentShape(Capsule())
        }
        .buttonStyle(.plain)
    }

    private func formattedBookTitle(_ raw: String) -> String {
        if raw.contains("《") { return raw }
        if let dotRange = raw.range(of: " · ") {
            let book = raw[..<dotRange.lowerBound]
            let rest = raw[dotRange.upperBound...]
            return "《\(book)》\(rest)"
        }
        return "《\(raw)》"
    }

    // MARK: - Floating Inline Popover Card

    private func inlinePopoverCard(_ pop: InlinePopoverState, sentence sent: MagazineSentence, issue: PodcastIssue) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            switch pop.kind {
            case .vocab(let entry):
                let saved = isWordSaved(entry.word)
                Text("\(entry.word) \(entry.phonetic)")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .padding(.bottom, 6)

                Text(entry.meaning)
                    .font(PodcastStyle.songti(13.5))
                    .foregroundStyle(YYColor.t1)
                    .lineSpacing(5.5)
                    .fixedSize(horizontal: false, vertical: true)

                Rectangle()
                    .fill(PodcastStyle.softDivider(scheme))
                    .frame(height: 0.5)
                    .padding(.top, 10)
                    .padding(.bottom, 9)

                HStack {
                    Text("语境释义")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                    Spacer()
                    Button {
                        toggleVocabSaved(entry, sentence: sent, issue: issue)
                    } label: {
                        Text(saved ? String(localized: "已在生词本 ✓") : String(localized: "+ 收进生词本"))
                            .font(.yy(size: 11.5, weight: .semibold))
                            .foregroundStyle(tint.accent)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }

            case .source(let bookID, let bookTitle, let lockNote, let quote):
                Text("原书锚点 · \(bookTitle)")
                    .font(.yy(size: 12, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .padding(.bottom, 6)

                Text(quote)
                    .font(PodcastStyle.songti(13.5))
                    .foregroundStyle(YYColor.t1)
                    .lineSpacing(5.5)
                    .fixedSize(horizontal: false, vertical: true)

                Rectangle()
                    .fill(PodcastStyle.softDivider(scheme))
                    .frame(height: 0.5)
                    .padding(.top, 10)
                    .padding(.bottom, 9)

                HStack {
                    Text(lockNote)
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                    Spacer()
                    Button {
                        withAnimation(YYMotion.quick) { activePopover = nil }
                        if let bookID {
                            onOpenBook?(bookID)
                        }
                        showToast(String(localized: "已定位至原书对应页码"))
                    } label: {
                        Text("打开原书 →")
                            .font(.yy(size: 11.5, weight: .semibold))
                            .foregroundStyle(tint.accent)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
        )
        .shadow(
            color: scheme == .dark ? Color.black.opacity(0.55) : Color(yy: YYRGBA(25, 22, 18, 0.14)),
            radius: 20,
            y: 8
        )
    }

    private func reloadSavedWords() {
        let words = (try? library.core.words()) ?? []
        savedVocabWords = words
        savedWordsSet = Set(words.filter { !$0.known }.map { $0.word.lowercased() })
    }

    private func isWordSaved(_ word: String) -> Bool {
        savedWordsSet.contains(word.lowercased())
    }

    private func toggleVocabSaved(_ entry: VocabEntry, sentence sent: MagazineSentence, issue: PodcastIssue) {
        let key = entry.word.lowercased()
        if savedWordsSet.contains(key) {
            try? library.core.deleteWord(word: entry.word)
            savedWordsSet.remove(key)
            NotificationCenter.default.post(name: .vocabChanged, object: nil)
            showToast(String(localized: "已从生词本移出"))
        } else {
            let targetBookID = sent.bookId
                ?? library.books.first(where: { $0.language.lowercased().hasPrefix("en") })?.id
                ?? library.books.first?.id
                ?? issue.id
            _ = try? library.core.addWord(
                word: entry.word,
                phonetic: entry.phonetic,
                meaning: entry.meaning,
                book: targetBookID,
                section: sent.bookSection ?? 0,
                sentence: sent.bookSentence ?? 0,
                context: sent.textOrig
            )
            savedWordsSet.insert(key)
            NotificationCenter.default.post(name: .vocabChanged, object: nil)
            showToast(String(localized: "已收进生词本 · 下期生词伴听自动融入"))
        }
    }

    private func l0ShownotesFallback(_ issue: PodcastIssue) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: "waveform")
                    .font(.system(size: 13))
                    .foregroundStyle(tint.accent)
                VStack(alignment: .leading, spacing: 2) {
                    Text("原声播放中 · 文字稿尚未就绪")
                        .font(.yy(size: 12.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                    Text("随时可按 H 记下原声或点「插话」，本地音频支持本机转文字。")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                }
                Spacer()
                if issue.localAudioPath != nil {
                    PodcastPaperPill(title: String(localized: "本机转文字"), accent: true, height: 26, horizontalPadding: 11) {
                        Task { await hub.transcribeOnDevice(issueID: issue.id) }
                    }
                }
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 11)
            .background(YYColor.inset, in: RoundedRectangle(cornerRadius: 10, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 10, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))

            if !issue.shownotes.isEmpty {
                Text(issue.shownotes)
                    .font(PodcastStyle.songti(14.5))
                    .foregroundStyle(YYColor.t2)
                    .lineSpacing(8)
                    .padding(.top, 4)
            }
        }
    }

    // MARK: - Right-Edge Detached Floating Circular Drawer Buttons (`.index-tabs`)

    #if os(macOS)
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
    #endif

    // MARK: - Bottom Footer Player Bar (`.rd-foot`) & Voice+Text Cut-In Interject

    private struct PlayerSegment: Identifiable {
        let id: Int
        let topicIndex: Int?
        let title: String
        let startSec: Double
        let endSec: Double
        let isAd: Bool
        let hasBookMention: Bool
        let clipTimes: [Double]

        var duration: Double { max(1, endSec - startSec) }
    }

    private func playerSegments(for issue: PodcastIssue) -> [PlayerSegment] {
        let total = max(1, totalDuration)
        let adKeywords = ["寒暄", "赞助", "广告", "片头", "片尾", "sponsor", "intro", "outro", "ad"]
        let sortedChapters = issue.chapters.sorted { $0.startSec < $1.startSec }

        if !sortedChapters.isEmpty {
            var raw: [(title: String, start: Double, end: Double)] = []
            if sortedChapters[0].startSec > 15 {
                raw.append((String(localized: "开场"), 0, sortedChapters[0].startSec))
            }
            for (idx, ch) in sortedChapters.enumerated() {
                let start = max(0, ch.startSec)
                let nextStart = idx + 1 < sortedChapters.count ? sortedChapters[idx + 1].startSec : total
                let end = min(total, max(start + 1, ch.endSec ?? nextStart))
                raw.append((ch.title, start, end))
            }
            var topicCounter = 0
            return raw.enumerated().map { idx, item in
                let lower = item.title.lowercased()
                let spanSents = allSentences.filter { $0.t0 >= item.start - 1 && $0.t0 < item.end }
                let onlySkipped = !spanSents.isEmpty && spanSents.allSatisfy { $0.skipReason != nil }
                let isAd = onlySkipped || adKeywords.contains { lower.contains($0) }
                let hasBook = spanSents.contains { $0.bookId != nil }
                let clipsInSpan = issueClips.filter { $0.t0 >= item.start && $0.t0 <= item.end }.map(\.t0)
                let tIdx: Int? = isAd ? nil : { topicCounter += 1; return topicCounter }()
                return PlayerSegment(
                    id: idx,
                    topicIndex: tIdx,
                    title: item.title,
                    startSec: item.start,
                    endSec: item.end,
                    isAd: isAd,
                    hasBookMention: hasBook,
                    clipTimes: clipsInSpan
                )
            }
        }

        if let firstAd = allSentences.first(where: { $0.skipReason != nil }), firstAd.t1 < total - 15 {
            let split = max(12, firstAd.t1)
            let adClips = issueClips.filter { $0.t0 < split }.map(\.t0)
            let mainClips = issueClips.filter { $0.t0 >= split }.map(\.t0)
            let hasBook = allSentences.contains { $0.bookId != nil }
            return [
                PlayerSegment(id: 0, topicIndex: nil, title: String(localized: "寒暄与赞助"), startSec: 0, endSec: split, isAd: true, hasBookMention: false, clipTimes: adClips),
                PlayerSegment(id: 1, topicIndex: 1, title: issue.showTitle, startSec: split, endSec: total, isAd: false, hasBookMention: hasBook, clipTimes: mainClips),
            ]
        }

        let hasBook = allSentences.contains { $0.bookId != nil }
        return [
            PlayerSegment(
                id: 0,
                topicIndex: 1,
                title: issue.showTitle,
                startSec: 0,
                endSec: total,
                isAd: false,
                hasBookMention: hasBook,
                clipTimes: issueClips.map(\.t0)
            )
        ]
    }

    private func activeSegment(in segments: [PlayerSegment], at sec: Double) -> PlayerSegment? {
        segments.last { sec >= $0.startSec } ?? segments.first
    }

    /// Sits cleanly BELOW the transcript scroll view so zero lines of reading text are ever blocked.
    private func slimPlayerDock(_ issue: PodcastIssue) -> some View {
        let segs = playerSegments(for: issue)
        return PodcastLiveTimeScope(
            hub: hub,
            isActive: isActive,
            fallbackTime: issue.positionSec,
            scrubbingValue: scrubbingValue
        ) { liveTime in
            #if os(macOS)
            macSlimPlayerBar(issue, segments: segs, liveTime: liveTime)
                .padding(.horizontal, 20)
                .padding(.top, 4)
                .padding(.bottom, 16)
            #else
            phoneFloatingCard(issue, segments: segs, liveTime: liveTime)
                .padding(.horizontal, 14)
                .padding(.top, 4)
                .padding(.bottom, 10)
            #endif
        }
        .frame(maxWidth: .infinity)
    }

    /// Floating Voice+Text Cut-In Card that only appears above the slim player bar when actively interjecting.
    @ViewBuilder
    private func interruptOverlay(_ issue: PodcastIssue) -> some View {
        if interrupting {
            floatingInterruptCard(issue)
                #if os(macOS)
                .padding(.horizontal, 20)
                .padding(.bottom, 68)
                #else
                .padding(.horizontal, 14)
                .padding(.bottom, 96)
                #endif
                .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }

    /// Floating Voice-First + Text + Quick-Chips Cut-In Q&A Card — shares the exact two-tier
    /// `YYColor.card` + `PodcastStyle.leafSurface` stationery language of the Podcast Homepage.
    private func floatingInterruptCard(_ issue: PodcastIssue) -> some View {
        let canSend = !interruptQuestion.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        let linkedBookTitle = allSentences.first(where: { $0.bookTitle != nil })?.bookTitle
        let showingReply = latestInterruptReply != nil && !hub.isListeningInterrupt && interruptQuestion.isEmpty && !sendingInterrupt

        return VStack(alignment: .leading, spacing: 0) {
            if showingReply, let reply = latestInterruptReply {
                // Upper pure-white answer zone
                VStack(alignment: .leading, spacing: 7) {
                    HStack(spacing: 6) {
                        Circle()
                            .fill(tint.accent)
                            .frame(width: 6, height: 6)
                        Text("书伴已答")
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(tint.accent)
                        Text("· 已存入右侧「插话」")
                            .font(.yy(size: 11))
                            .foregroundStyle(YYColor.t3)
                        Spacer()
                        PodcastPaperPill(
                            title: hub.isSpeakingInterruptReply ? String(localized: "停止朗读") : String(localized: "听回答"),
                            symbol: hub.isSpeakingInterruptReply ? "stop.fill" : "speaker.wave.2",
                            accent: hub.isSpeakingInterruptReply,
                            height: 24,
                            horizontalPadding: 9
                        ) {
                            if hub.isSpeakingInterruptReply {
                                hub.stopSpeakingInterruptAnswer()
                            } else {
                                hub.speakInterruptAnswer(reply.answer)
                            }
                        }
                    }

                    Text("你问：\(reply.question)")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)

                    Text(reply.answer)
                        .font(PodcastStyle.songti(14))
                        .foregroundStyle(YYColor.t1)
                        .lineSpacing(5)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 12)
                .background(YYColor.card)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                // Lower warm-leaf action bar
                HStack(spacing: 7) {
                    PodcastPaperPill(title: String(localized: "语音追问"), symbol: "mic.fill", accent: true, height: 26, horizontalPadding: 10) {
                        latestInterruptReply = nil
                        toggleVoiceDictation(issue)
                    }

                    PodcastPaperPill(title: String(localized: "打字追问"), height: 26, horizontalPadding: 10) {
                        latestInterruptReply = nil
                        interruptFocused = true
                    }

                    Spacer()

                    Button {
                        closeInterrupt()
                    } label: {
                        Text("明白了，继续 -3s")
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(YYColor.card)
                            .padding(.horizontal, 12)
                            .frame(height: 26)
                            .background(YYColor.t1, in: Capsule())
                            .contentShape(Capsule())
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                }
                .padding(.horizontal, 16)
                .frame(height: 40)
                .background(PodcastStyle.leafSurface(scheme))
            } else {
                // Upper pure-white voice + text composer row
                HStack(spacing: 9) {
                    Button {
                        toggleVoiceDictation(issue)
                    } label: {
                        HStack(spacing: 5.5) {
                            if hub.isListeningInterrupt {
                                VoiceLevelBars(level: hub.interruptVoiceLevel, tint: YYColor.card)
                                Text(canSend ? String(localized: "说完发送") : String(localized: "聆听中…"))
                                    .font(.yy(size: 11.5, weight: .medium))
                            } else {
                                Image(systemName: "mic.fill")
                                    .font(.system(size: 10.5, weight: .medium))
                                Text("语音说")
                                    .font(.yy(size: 11.5, weight: .medium))
                            }
                        }
                        .foregroundStyle(hub.isListeningInterrupt ? YYColor.card : tint.accent)
                        .padding(.horizontal, 10)
                        .frame(height: 26)
                        .background(Capsule().fill(hub.isListeningInterrupt ? tint.accent : YYColor.card))
                        .overlay(
                            Capsule().strokeBorder(
                                hub.isListeningInterrupt ? Color.clear : PodcastStyle.pillBorder(scheme),
                                lineWidth: 0.5
                            )
                        )
                        .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                        .contentShape(Capsule())
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                    .help(hub.isListeningInterrupt ? String(localized: "结束收音并发送提问") : String(localized: "用麦克风直接开口提问（端侧语音转写）"))

                    TextField(
                        "",
                        text: $interruptQuestion,
                        prompt: Text(
                            hub.isListeningInterrupt
                                ? String(localized: "正在收音，请直接开口说…")
                                : String(localized: "点「语音说」直接开口，或直接打字追问…")
                        )
                        .foregroundStyle(YYColor.t3.opacity(0.82))
                    )
                    .textFieldStyle(.plain)
                    .font(.yy(size: 12.5))
                    .foregroundStyle(YYColor.t1)
                    .focused($interruptFocused)
                    .onSubmit { sendInterrupt(issue) }

                    HStack(spacing: 6) {
                        if sendingInterrupt {
                            ProgressView().controlSize(.small)
                                .frame(width: 40, height: 26)
                        } else {
                            Button {
                                sendInterrupt(issue)
                            } label: {
                                Text("发送")
                                    .font(.yy(size: 11.5, weight: .medium))
                                    .foregroundStyle(canSend ? YYColor.card : YYColor.t3)
                                    .padding(.horizontal, 11)
                                    .frame(height: 26)
                                    .background(canSend ? YYColor.t1 : YYColor.inset, in: Capsule())
                                    .contentShape(Capsule())
                            }
                            .buttonStyle(YYPressStyle(scale: 0.96))
                            .disabled(!canSend)
                            .animation(YYMotion.quick, value: canSend)
                        }

                        PodcastPaperPill(title: String(localized: "继续 -3s"), height: 26, horizontalPadding: 10) {
                            closeInterrupt()
                        }
                    }
                    .fixedSize()
                }
                .padding(.horizontal, 14)
                .frame(height: 44)
                .background(YYColor.card)

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                // Lower warm-leaf status & quick-ask strip
                HStack(spacing: 6) {
                    Circle()
                        .fill(tint.accent)
                        .frame(width: 5, height: 5)
                    Text(hub.interruptVoiceHint ?? (hub.isListeningInterrupt ? String(localized: "节目已压低 · 端侧实时转写中") : String(localized: "已压低声 · 语音或一键追问")))
                        .font(.yy(size: 11))
                        .foregroundStyle(hub.interruptVoiceHint != nil ? YYColor.t2 : YYColor.t3)
                        .lineLimit(1)

                    Spacer(minLength: 6)

                    if !hub.isListeningInterrupt && interruptQuestion.isEmpty {
                        quickAskChip(String(localized: "用大白话再讲一遍"), issue: issue)
                        if let bookTitle = linkedBookTitle {
                            quickAskChip(String(localized: "和《\(bookTitle)》有何关联？"), issue: issue)
                        } else {
                            quickAskChip(String(localized: "这个观点有什么局限？"), issue: issue)
                        }
                    }
                }
                .padding(.horizontal, 14)
                .frame(height: 34)
                .background(PodcastStyle.leafSurface(scheme))
            }
        }
        .clipShape(RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
        )
        .shadow(
            color: scheme == .dark ? Color.black.opacity(0.45) : Color(yy: YYRGBA(51, 46, 41, 0.10)),
            radius: 18,
            y: 6
        )
        .frame(maxWidth: 620)
    }

    private func quickAskChip(_ text: String, issue: PodcastIssue) -> some View {
        PodcastPaperPill(title: text, height: 22, horizontalPadding: 9) {
            interruptQuestion = text
            sendInterrupt(issue)
        }
    }

    /// macOS Ultra-Slim `44pt` Single-Row Stationery Player Bar:
    /// - Shares the Podcast Homepage's exact typography & controls (`-15s · ▶ · +15s`, `1.0×`, `划线 [H]`,
    ///   pure white porcelain `PodcastPaperPill`「插话」) inside a crisp `12pt` stationery card.
    /// - Sits cleanly below the scroll viewport (never covers a single line of text) while keeping the full
    ///   segmented information-density scrubber + clickable chapter index menu + hover chapter preview pill.
    private func macSlimPlayerBar(_ issue: PodcastIssue, segments: [PlayerSegment], liveTime: Double) -> some View {
        let isPlayingNow = isActive && hub.isPlaying
        let curSeg = activeSegment(in: segments, at: liveTime)
        let topicCount = segments.filter { !$0.isAd }.count

        return HStack(spacing: 8) {
            // 1. Homepage-matched typographic transport (`-15s · 28pt ▶ · +15s`)
            HStack(spacing: 2) {
                readerTextControl("-15s", id: "back") { hub.skip(by: -15) }

                Button { hub.selectAndPlay(issue) } label: {
                    Image(systemName: isPlayingNow ? "pause.fill" : "play.fill")
                        .contentTransition(.symbolEffect(.replace))
                        .animation(YYMotion.quick, value: isPlayingNow)
                        .font(.system(size: 10, weight: .semibold))
                        .foregroundStyle(YYColor.card)
                        .offset(x: isPlayingNow ? 0 : 1)
                        .frame(width: 28, height: 28)
                        .background(YYColor.t1, in: Circle())
                        .contentShape(Circle())
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
                .padding(.horizontal, 2)
                .help(isPlayingNow ? String(localized: "暂停 (Space)") : String(localized: "播放 (Space)"))

                readerTextControl("+15s", id: "fwd") { hub.skip(by: 15) }
            }
            .fixedSize()

            // 2. Compact Chapter Menu + Segmented Information-Density Scrubber + Time
            HStack(spacing: 7) {
                if segments.count > 1, let curSeg {
                    Menu {
                        ForEach(segments) { seg in
                            Button {
                                if !isActive { hub.start(issue, from: seg.startSec) }
                                else { hub.seek(to: seg.startSec) }
                            } label: {
                                let prefix = seg.isAd
                                    ? String(localized: "[赞助]")
                                    : (seg.topicIndex.map { String(format: "%02d", $0) } ?? "")
                                let bookMark = seg.hasBookMention ? " · 📖" : ""
                                let curMark = seg.id == curSeg.id ? " ✓" : ""
                                Text("\(PodcastFormat.clock(seg.startSec))  \(prefix) \(seg.title)\(bookMark)\(curMark)")
                            }
                        }
                    } label: {
                        HStack(spacing: 3) {
                            if curSeg.isAd {
                                Text("赞助")
                                    .font(.yy(size: 10, weight: .medium))
                                    .foregroundStyle(YYColor.t3)
                            } else if let tIdx = curSeg.topicIndex, topicCount > 0 {
                                Text(String(format: "%02d/%02d", tIdx, topicCount))
                                    .font(.yy(size: 10.5, weight: .medium))
                                    .monospacedDigit()
                                    .foregroundStyle(tint.accent)
                            }
                            PodcastGlyphView(glyph: .chevronDown, size: 9, lineWidth: 1.5)
                                .foregroundStyle(YYColor.t3)
                        }
                        .padding(.horizontal, 5)
                        .frame(height: 24)
                        .background(
                            RoundedRectangle(cornerRadius: 6, style: .continuous)
                                .fill(hoveredControl == "chapter" ? YYColor.inset.opacity(0.65) : Color.clear)
                        )
                        .contentShape(.rect)
                    }
                    .menuStyle(.button)
                    .buttonStyle(.plain)
                    .onHover { over in hoveredControl = over ? "chapter" : (hoveredControl == "chapter" ? nil : hoveredControl) }
                    .help(curSeg.title)
                    .fixedSize()
                }

                segmentedScrubber(issue, segments: segments, liveTime: liveTime)

                Text(PodcastFormat.clock(liveTime))
                    .font(.yy(size: 10.5, weight: .medium))
                    .monospacedDigit()
                    .foregroundStyle(YYColor.t3)
                    .fixedSize()
            }
            .frame(maxWidth: .infinity)
            .opacity(interrupting ? 0.45 : 1)

            // 3. Homepage-matched actions (`1.0×` · `划线 [H]` · porcelain `🎙 插话`)
            HStack(spacing: 4) {
                speedMenu

                Button {
                    triggerClipMoment()
                } label: {
                    HStack(spacing: 4) {
                        if clipFlash {
                            Image(systemName: "checkmark")
                                .font(.system(size: 9.5, weight: .bold))
                                .foregroundStyle(tint.accent)
                            Text("已记")
                                .font(.yy(size: 11, weight: .medium))
                                .foregroundStyle(tint.accent)
                        } else {
                            Text("划线")
                                .font(.yy(size: 11, weight: .medium))
                                .foregroundStyle(hoveredControl == "clip" ? YYColor.t1 : YYColor.t2)
                            Text("H")
                                .font(.yy(size: 9.5, weight: .medium))
                                .foregroundStyle(YYColor.t3)
                                .padding(.horizontal, 4)
                                .frame(height: 15)
                                .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 4, style: .continuous))
                                .overlay(
                                    RoundedRectangle(cornerRadius: 4, style: .continuous)
                                        .strokeBorder(YYColor.line, lineWidth: 0.5)
                                )
                        }
                    }
                    .padding(.horizontal, 5)
                    .frame(height: 26)
                    .background(
                        RoundedRectangle(cornerRadius: 6, style: .continuous)
                            .fill(hoveredControl == "clip" && !clipFlash ? YYColor.inset.opacity(0.6) : Color.clear)
                    )
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .onHover { over in hoveredControl = over ? "clip" : (hoveredControl == "clip" ? nil : hoveredControl) }
                .help(String(localized: "记这句原声书摘 (H)"))

                if interrupting {
                    Button {
                        closeInterrupt()
                    } label: {
                        HStack(spacing: 4) {
                            Image(systemName: "mic.fill")
                                .font(.system(size: 9.5, weight: .medium))
                            Text("插话中")
                                .font(.yy(size: 11, weight: .medium))
                        }
                        .foregroundStyle(YYColor.card)
                        .padding(.horizontal, 10)
                        .frame(height: 26)
                        .background(tint.accent, in: Capsule())
                        .contentShape(Capsule())
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                } else {
                    PodcastPaperPill(
                        title: String(localized: "插话"),
                        symbol: "mic",
                        accent: true,
                        height: 26,
                        horizontalPadding: 10
                    ) {
                        beginInterrupt()
                    }
                    .help(String(localized: "压低节目声，语音或打字插话提问 (I)"))
                }
            }
            .fixedSize()
        }
        .padding(.horizontal, 10)
        .frame(height: 44)
        .frame(maxWidth: 620)
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
        )
        .shadow(
            color: scheme == .dark ? Color.black.opacity(0.32) : Color(yy: YYRGBA(36, 32, 26, 0.06)),
            radius: 8,
            y: 2
        )
    }

    private func readerTextControl(_ label: String, id: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(label)
                .font(.yy(size: 11, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(hoveredControl == id ? YYColor.t1 : YYColor.t2)
                .padding(.horizontal, 6)
                .frame(height: 26)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(hoveredControl == id ? YYColor.inset.opacity(0.6) : Color.clear)
                )
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { over in hoveredControl = over ? id : (hoveredControl == id ? nil : hoveredControl) }
    }

    /// iOS matching floating two-tier stationery podcast card (`YYColor.card` + `PodcastStyle.leafSurface`).
    private func phoneFloatingCard(_ issue: PodcastIssue, segments: [PlayerSegment], liveTime: Double) -> some View {
        let isPlayingNow = isActive && hub.isPlaying
        let curSeg = activeSegment(in: segments, at: liveTime)
        let topicCount = segments.filter { !$0.isAd }.count

        return VStack(spacing: 0) {
            VStack(spacing: 5) {
                HStack(spacing: 6) {
                    if segments.count > 1, let curSeg {
                        Menu {
                            ForEach(segments) { seg in
                                Button {
                                    if !isActive { hub.start(issue, from: seg.startSec) }
                                    else { hub.seek(to: seg.startSec) }
                                } label: {
                                    Text("\(PodcastFormat.clock(seg.startSec))  \(seg.title)")
                                }
                            }
                        } label: {
                            HStack(spacing: 5) {
                                if let tIdx = curSeg.topicIndex, topicCount > 0 {
                                    Text(String(format: "%02d/%02d", tIdx, topicCount))
                                        .font(.yy(size: 10.5, weight: .medium))
                                        .monospacedDigit()
                                        .foregroundStyle(tint.accent)
                                }
                                Text(curSeg.title)
                                    .font(.yy(size: 11.5, weight: .medium))
                                    .foregroundStyle(YYColor.t1)
                                    .lineLimit(1)
                                PodcastGlyphView(glyph: .chevronDown, size: 9.5, lineWidth: 1.5)
                                    .foregroundStyle(YYColor.t3)
                            }
                        }
                        .buttonStyle(.plain)
                    } else {
                        Text(issue.showTitle)
                            .font(.yy(size: 11.5, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .lineLimit(1)
                    }
                    Spacer(minLength: 6)
                    Text("\(PodcastFormat.clock(liveTime)) / \(PodcastFormat.clock(totalDuration))")
                        .font(.yy(size: 10))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }
                segmentedScrubber(issue, segments: segments, liveTime: liveTime)
            }
            .padding(.horizontal, 14)
            .padding(.top, 10)
            .padding(.bottom, 7)
            .background(
                UnevenRoundedRectangle(
                    topLeadingRadius: 14,
                    bottomLeadingRadius: 0,
                    bottomTrailingRadius: 0,
                    topTrailingRadius: 14,
                    style: .continuous
                )
                .fill(YYColor.card)
            )

            Rectangle().fill(YYColor.line).frame(height: 0.5)

            HStack(spacing: 6) {
                HStack(spacing: 3) {
                    readerTextControl("-15s", id: "p-back") { hub.skip(by: -15) }

                    Button { hub.selectAndPlay(issue) } label: {
                        Image(systemName: isPlayingNow ? "pause.fill" : "play.fill")
                            .font(.system(size: 11, weight: .semibold))
                            .foregroundStyle(YYColor.card)
                            .offset(x: isPlayingNow ? 0 : 1)
                            .frame(width: 30, height: 30)
                            .background(YYColor.t1, in: Circle())
                    }
                    .buttonStyle(YYPressStyle(scale: 0.94))
                    .padding(.horizontal, 2)

                    readerTextControl("+15s", id: "p-fwd") { hub.skip(by: 15) }
                    speedMenu
                }
                Spacer(minLength: 4)
                HStack(spacing: 6) {
                    Button { triggerClipMoment() } label: {
                        PodcastGlyphView(glyph: clipFlash ? .bookmarkFilled : .bookmark, size: 13.5, lineWidth: 1.45)
                            .foregroundStyle(clipFlash ? tint.accent : YYColor.t2)
                            .frame(width: 28, height: 28)
                    }
                    .buttonStyle(.plain)

                    PodcastPaperPill(
                        title: String(localized: "插话"),
                        symbol: interrupting ? "mic.fill" : "mic",
                        accent: true,
                        height: 26,
                        horizontalPadding: 10
                    ) {
                        if interrupting { closeInterrupt() } else { beginInterrupt() }
                    }
                }
            }
            .padding(.horizontal, 12)
            .frame(height: 44)
            .background(
                UnevenRoundedRectangle(
                    topLeadingRadius: 0,
                    bottomLeadingRadius: 14,
                    bottomTrailingRadius: 14,
                    topTrailingRadius: 0,
                    style: .continuous
                )
                .fill(PodcastStyle.leafSurface(scheme))
            )
        }
        .overlay(
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
        )
        .shadow(
            color: scheme == .dark ? Color.black.opacity(0.45) : Color(yy: YYRGBA(51, 46, 41, 0.12)),
            radius: 18,
            y: 6
        )
    }

    private struct SegLayout {
        let seg: PlayerSegment
        let x: CGFloat
        let width: CGFloat
    }

    private func computeSegLayouts(segments: [PlayerSegment], totalWidth: CGFloat, gap: CGFloat) -> [SegLayout] {
        guard !segments.isEmpty else { return [] }
        let usable = max(1, totalWidth - gap * CGFloat(max(0, segments.count - 1)))
        let totalDur = max(1, segments.reduce(0) { $0 + $1.duration })
        let minWeight = segments.count > 1 ? totalDur * 0.065 : totalDur
        let weights = segments.map { max($0.duration, minWeight) }
        let weightSum = max(1, weights.reduce(0, +))

        var out: [SegLayout] = []
        var cursor: CGFloat = 0
        for (i, seg) in segments.enumerated() {
            let w = usable * CGFloat(weights[i] / weightSum)
            out.append(SegLayout(seg: seg, x: cursor, width: max(4, w)))
            cursor += w + gap
        }
        return out
    }

    private func xPosition(for sec: Double, layouts: [SegLayout]) -> CGFloat {
        guard let first = layouts.first, let last = layouts.last else { return 0 }
        for item in layouts {
            if sec <= item.seg.endSec || item.seg.id == last.seg.id {
                let frac = min(1, max(0, (sec - item.seg.startSec) / item.seg.duration))
                return item.x + item.width * CGFloat(frac)
            }
        }
        return first.x
    }

    private func time(for x: CGFloat, layouts: [SegLayout]) -> Double {
        guard let first = layouts.first else { return 0 }
        var target = first
        for item in layouts {
            if x >= item.x - 1.5 {
                target = item
            } else {
                break
            }
        }
        let frac = min(1, max(0, Double((x - target.x) / max(1, target.width))))
        return target.seg.startSec + target.seg.duration * frac
    }

    /// Segmented Information-Density Scrubber:
    /// - Visually separates chapters with `3pt` gaps so listeners read structure at a glance.
    /// - Dims ad/intro folds, highlights active topic in `tint.accent`, marks saved clips in warm amber,
    ///   and glides a live floating preview pill (topic + clock) on hover or drag.
    private func segmentedScrubber(_ issue: PodcastIssue, segments: [PlayerSegment], liveTime: Double) -> some View {
        let activeDrag = hoveringScrub || scrubbingValue != nil
        let gap: CGFloat = segments.count > 1 ? 3.5 : 0
        let previewSec = scrubbingValue ?? hoverScrubSec
        let curSegID = activeSegment(in: segments, at: liveTime)?.id
        let previewSeg = previewSec.flatMap { activeSegment(in: segments, at: $0) }
        let clipGold = Color(yy: YYRGBA(201, 154, 59))
        let idleTrack = scheme == .dark
            ? Color.white.opacity(0.14)
            : Color(yy: YYRGBA(38, 34, 28, 0.13))
        let hoverTrack = scheme == .dark
            ? Color.white.opacity(0.22)
            : Color(yy: YYRGBA(38, 34, 28, 0.20))

        return GeometryReader { geo in
            let w = max(1, geo.size.width)
            let layouts = computeSegLayouts(segments: segments, totalWidth: w, gap: gap)
            let knobSize: CGFloat = activeDrag ? 10.5 : 8.5
            let knobX = max(knobSize / 2, min(w - knobSize / 2, xPosition(for: liveTime, layouts: layouts)))

            ZStack(alignment: .leading) {
                ForEach(layouts, id: \.seg.id) { item in
                    let isCurrent = item.seg.id == curSegID
                    let isHoveredSeg = previewSeg?.id == item.seg.id && previewSec != nil
                    let barH: CGFloat = item.seg.isAd
                        ? (isHoveredSeg ? 4.5 : 2.6)
                        : (isHoveredSeg ? 6.2 : (isCurrent ? (activeDrag ? 5.2 : 4.6) : (activeDrag ? 4.2 : 3.6)))
                    let fillFrac: CGFloat = {
                        if liveTime >= item.seg.endSec { return 1 }
                        if liveTime <= item.seg.startSec { return 0 }
                        return CGFloat((liveTime - item.seg.startSec) / item.seg.duration)
                    }()

                    ZStack(alignment: .leading) {
                        Capsule()
                            .fill(isHoveredSeg ? hoverTrack : idleTrack)
                            .frame(width: item.width, height: barH)

                        if fillFrac > 0 {
                            Capsule()
                                .fill(
                                    item.seg.isAd
                                        ? YYColor.t3.opacity(0.55)
                                        : (isCurrent ? tint.accent : YYColor.t1.opacity(0.70))
                                )
                                .frame(width: max(barH, item.width * fillFrac), height: barH)
                        }

                        // Saved clip markers ("原声摘") inside this segment
                        ForEach(Array(item.seg.clipTimes.enumerated()), id: \.offset) { _, clipSec in
                            let cFrac = min(1, max(0, CGFloat((clipSec - item.seg.startSec) / item.seg.duration)))
                            Capsule()
                                .fill(clipGold)
                                .frame(width: 2.5, height: barH + 2.5)
                                .offset(x: max(0, min(item.width - 2.5, item.width * cFrac - 1.25)))
                        }
                    }
                    .opacity(item.seg.isAd ? (isHoveredSeg ? 0.78 : 0.42) : 1.0)
                    .position(x: item.x + item.width / 2, y: geo.size.height / 2)
                }

                // Active playhead knob
                Circle()
                    .fill(scrubbingValue != nil ? tint.accent : YYColor.t1)
                    .frame(width: knobSize, height: knobSize)
                    .overlay(Circle().stroke(YYColor.card, lineWidth: 1.5))
                    .shadow(color: .black.opacity(0.16), radius: 2, y: 1)
                    .position(x: knobX, y: geo.size.height / 2)

                // Floating live hover/drag preview tooltip (`03 · 流体智力与 ARC-AGI · 14:22`)
                if let pSec = previewSec, let pSeg = previewSeg {
                    let rawX = xPosition(for: pSec, layouts: layouts)
                    let clampedX = max(56, min(w - 56, rawX))
                    HStack(spacing: 5) {
                        if pSeg.isAd {
                            Text("赞助")
                                .font(.yy(size: 9.5, weight: .semibold))
                                .foregroundStyle(YYColor.card.opacity(0.72))
                        } else if let tIdx = pSeg.topicIndex, segments.count > 1 {
                            Text(String(format: "%02d", tIdx))
                                .font(.yy(size: 10, weight: .semibold))
                                .monospacedDigit()
                                .foregroundStyle(tint.accent)
                        }
                        if segments.count > 1 {
                            Text(pSeg.title)
                                .font(.yy(size: 10.5, weight: .medium))
                                .lineLimit(1)
                            Text("·")
                                .opacity(0.45)
                        }
                        Text(PodcastFormat.clock(pSec))
                            .font(.yy(size: 10.5, weight: .semibold))
                            .monospacedDigit()
                    }
                    .foregroundStyle(YYColor.card)
                    .padding(.horizontal, 9)
                    .frame(height: 23)
                    .frame(maxWidth: 240)
                    .background(YYColor.t1, in: Capsule())
                    .shadow(color: .black.opacity(0.18), radius: 7, y: 2)
                    .fixedSize()
                    .position(x: clampedX, y: -18)
                    .allowsHitTesting(false)
                }
            }
            .frame(maxHeight: .infinity)
            .contentShape(.rect)
            #if os(macOS)
            .onContinuousHover { phase in
                switch phase {
                case .active(let loc):
                    hoveringScrub = true
                    hoverScrubSec = time(for: loc.x, layouts: layouts)
                case .ended:
                    hoveringScrub = false
                    hoverScrubSec = nil
                }
            }
            #endif
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { v in
                        guard totalDuration > 0 else { return }
                        scrubbingValue = time(for: v.location.x, layouts: layouts)
                    }
                    .onEnded { v in
                        guard totalDuration > 0 else { return }
                        let target = time(for: v.location.x, layouts: layouts)
                        scrubbingValue = nil
                        if !isActive { hub.start(issue, from: target) }
                        else { hub.seek(to: target) }
                    }
            )
        }
        .frame(height: 18)
        .animation(YYMotion.quick, value: hoveringScrub)
    }

    private var speedMenu: some View {
        let isHovered = hoveredControl == "speed"
        return Menu {
            ForEach([Float(0.8), 1.0, 1.2, 1.5, 1.8, 2.0], id: \.self) { r in
                Button(String(format: "%.1f×", r)) { hub.rate = r }
            }
        } label: {
            Text(String(format: "%.1f×", hub.rate))
                .font(.yy(size: 11, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(isHovered ? YYColor.t1 : YYColor.t2)
                .padding(.horizontal, 6)
                .frame(height: 26)
                .background(
                    RoundedRectangle(cornerRadius: 6, style: .continuous)
                        .fill(isHovered ? YYColor.inset.opacity(0.6) : Color.clear)
                )
        }
        .menuStyle(.button)
        .buttonStyle(.plain)
        .fixedSize()
        .onHover { over in hoveredControl = over ? "speed" : (hoveredControl == "speed" ? nil : hoveredControl) }
        .help(String(localized: "播放倍速"))
    }


    private func triggerClipMoment() {
        Task {
            if let issue, !isActive {
                hub.start(issue, from: displayedTime)
                hub.pause()
            }
            if await hub.markCurrentMoment() != nil {
                if !drawerOpen { hasNewClipDot = true }
                clipFlashTask?.cancel()
                clipFlash = true
                showToast(String(localized: "已记下原声片段，存入右侧「书摘」"))
                clipFlashTask = Task {
                    try? await Task.sleep(for: .seconds(1.5))
                    if !Task.isCancelled { clipFlash = false }
                }
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

    private func beginInterrupt() {
        latestInterruptReply = nil
        interrupting = true
        hub.setDucking(true)
        interruptFocused = true
    }

    private func closeInterrupt() {
        interrupting = false
        latestInterruptReply = nil
        interruptQuestion = ""
        interruptFocused = false
        hub.resumeAfterInterrupt()
    }

    private func toggleVoiceDictation(_ issue: PodcastIssue) {
        if hub.isListeningInterrupt {
            hub.stopVoiceInterrupt()
            let trimmed = interruptQuestion.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty {
                sendInterrupt(issue)
            }
        } else {
            latestInterruptReply = nil
            interruptFocused = false
            let locale = issue.language.lowercased().hasPrefix("en") ? "en-US" : "zh-CN"
            Task {
                let started = await hub.startVoiceInterrupt(language: locale) { partial in
                    interruptQuestion = partial
                }
                if !started {
                    interruptFocused = true
                }
            }
        }
    }

    private func sendInterrupt(_ issue: PodcastIssue) {
        hub.stopVoiceInterrupt()
        let q = interruptQuestion.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty else { return }
        sendingInterrupt = true
        Task {
            let saved = await hub.submitInterrupt(issueID: issue.id, question: q)
            interruptQuestion = ""
            sendingInterrupt = false
            if let saved {
                latestInterruptReply = saved
            }
        }
    }
}

/// Shared mutable cache of rendered vocabulary run bounding boxes inside each sentence's `Text` view.
private final class PodcastWordRectStore: @unchecked Sendable {
    var rects: [String: CGRect] = [:]
}

/// Custom `TextAttribute` marking inline vocabulary words for `1.6pt` dotted accent underlining (`.vocab-w`).
@available(macOS 15.0, iOS 18.0, *)
private struct PodcastDottedWordAttribute: TextAttribute, Hashable {
    let key: String
    let isHighlighted: Bool
    let store: PodcastWordRectStore

    static func == (lhs: Self, rhs: Self) -> Bool {
        lhs.key == rhs.key && lhs.isHighlighted == rhs.isHighlighted && lhs.store === rhs.store
    }

    func hash(into hasher: inout Hasher) {
        hasher.combine(key)
        hasher.combine(isHighlighted)
        hasher.combine(ObjectIdentifier(store))
    }
}

/// Custom `TextRenderer` that records the exact typographic bounding box of each vocabulary word,
/// draws a soft accent wash on hover/active (`.vocab-w:hover`), and strokes a `1.6pt` dotted accent line
/// (`border-bottom: 1.5px dotted var(--accent)`) directly beneath the word.
@available(macOS 15.0, iOS 18.0, *)
private struct PodcastDottedTextRenderer: TextRenderer {
    let dotColor: Color

    func draw(layout: Text.Layout, in context: inout GraphicsContext) {
        for line in layout {
            for run in line {
                if let attr = run[PodcastDottedWordAttribute.self] {
                    let rect = run.typographicBounds.rect
                    attr.store.rects[attr.key] = rect
                    if attr.isHighlighted {
                        let bgRect = rect.insetBy(dx: -3, dy: -1)
                        context.fill(
                            RoundedRectangle(cornerRadius: 4, style: .continuous).path(in: bgRect),
                            with: .color(dotColor.opacity(0.12))
                        )
                    }
                    context.draw(run)
                    var path = Path()
                    let y = rect.maxY + 1.5
                    path.move(to: CGPoint(x: rect.minX, y: y))
                    path.addLine(to: CGPoint(x: rect.maxX, y: y))
                    context.stroke(
                        path,
                        with: .color(dotColor),
                        style: StrokeStyle(lineWidth: 1.6, lineCap: .round, dash: [0.5, 3.6])
                    )
                } else {
                    context.draw(run)
                }
            }
        }
    }
}

/// Isolates 4Hz `hub.currentTime` observation exclusively to the bottom player dock so the
/// multi-hundred-sentence transcript view is never invalidated on sub-second audio progress ticks.
private struct PodcastLiveTimeScope<Content: View>: View {
    let hub: PodcastHub
    let isActive: Bool
    let fallbackTime: Double
    let scrubbingValue: Double?
    @ViewBuilder let content: (Double) -> Content

    var body: some View {
        let t = scrubbingValue ?? (isActive ? hub.currentTime : fallbackTime)
        content(t)
    }
}

/// Subtle organic 5-bar live acoustic indicator driven by real microphone RMS level during voice interject.
private struct VoiceLevelBars: View {
    let level: CGFloat
    let tint: Color

    private let weights: [CGFloat] = [0.45, 0.85, 1.0, 0.72, 0.40]

    var body: some View {
        HStack(spacing: 1.8) {
            ForEach(Array(weights.enumerated()), id: \.offset) { _, w in
                Capsule()
                    .fill(tint)
                    .frame(width: 2, height: max(4, min(13, 3.5 + 10.5 * level * w)))
            }
        }
        .frame(height: 13)
        .animation(.easeOut(duration: 0.08), value: level)
    }
}
