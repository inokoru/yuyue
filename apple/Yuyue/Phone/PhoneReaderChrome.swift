#if os(iOS)
import SwiftUI
import UIKit
import YuyueCore

/// Independent reading brightness & eye-care preference keys on phone/tablet.
enum ReaderBrightness {
    static let followKey = "reader.brightnessFollowSystem"
    static let valueKey = "reader.brightness"
    static let eyeCareKey = "reader.eyeCare"

    @MainActor
    static var activeScreen: UIScreen? {
        UIApplication.shared.connectedScenes
            .compactMap { $0 as? UIWindowScene }
            .first { $0.activationState == .foregroundActive }?.screen
            ?? UIApplication.shared.connectedScenes.compactMap { ($0 as? UIWindowScene)?.screen }.first
    }
}

/// The phone's page: only the words and a quiet single-line footer at the bottom. A tap in the
/// middle floats 返回, the book's name and page, 搜索, 书签 and 更多 along the top together with
/// the full glass capsule at the bottom (目录, where the reader is, ‹ 朗读 ›, Aa). While reading
/// aloud with the chrome tucked away, a slim mini listening pill (swipe down to fold into a corner
/// orb) stays clear of the text. 书内搜索 takes over both; 沉浸阅读 leaves only a hairline.
struct PhoneReaderChrome: View {
    @Bindable var model: ReaderModel
    let close: () -> Void
    @State private var listenShown = false
    @State private var readyVoicePickerShown = false
    @State private var pickedReadyVoiceID: String?
    @State private var hitsShown = false
    @State private var infoShown = false
    @State private var immersiveShown = false
    @State private var moreShown = false
    @State private var pendingMoreAction: (() -> Void)?
    @State private var chromeActivity = 0
    @AppStorage(ImmersivePrefs.progressKey) private var hidesProgress = true
    @State private var exported: URL?
    @FocusState private var typing: Bool
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(\.scenePhase) private var scenePhase
    @AppStorage(ReaderBrightness.followKey) private var brightnessFollow = true
    @AppStorage(ReaderBrightness.valueKey) private var readerBrightness = 0.6
    @AppStorage(ReaderBrightness.eyeCareKey) private var eyeCare = false
    @State private var savedSystemBrightness: CGFloat?

    private var paper: YYPaper { model.paperTokens }

    private enum BottomBarMode: Equatable {
        case search, morphCapsule, fullCapsule, miniListen, miniOrb, footer, none
    }

    /// Mutually exclusive bottom chrome state:
    /// - 书内搜索 → searchCapsule
    /// - 灵动胶囊展开（改读音 / 记下这句 / 智能回退） → morphCapsule
    /// - 点中央唤出 / 有重要录音提示 → fullCapsule (float over bottom margin without reflow)
    /// - 听书中（或只听模式）且收起顶栏 → slim miniListen (or corner miniOrb when tucked / scrolling down)
    /// - 纯看书（非沉浸、非双页） → quiet single-line footer clear of the text
    private var bottomMode: BottomBarMode {
        if model.searchShown { return .search }
        if model.capsuleMode != .idle { return .morphCapsule }
        guard model.lookup == nil else { return .none }
        if model.chromeShown || (model.capsuleAlert != nil && !model.immersive && !model.barsAway) {
            return .fullCapsule
        }
        if !model.immersive && (model.isPlaying || model.listenOnly) {
            return (!model.listenOnly && (model.playerTucked || model.barsAway)) ? .miniOrb : .miniListen
        }
        if !model.immersive && !model.barsAway && !InnerDisplay.isOn(sizeClass) {
            return .footer
        }
        return .none
    }
    private struct ChromeAutoHideKey: Equatable {
        let shown: Bool
        let busy: Bool
        let immersive: Bool
        let activity: Int
    }

    /// True while any sheet, popover, panel, search bar, or morphed capsule is open so auto-hide pauses.
    private var chromeBusy: Bool {
        listenShown || readyVoicePickerShown || hitsShown || infoShown || immersiveShown || moreShown
            || model.settingsShown || model.tocShown || model.rulesShown || model.txtShown
            || model.panel != nil || model.askingFinished || model.searchShown
            || model.capsuleMode != .idle || exported != nil
    }

    /// The bars and cards: quick, no overshoot; with 减弱动态效果 they only fade.
    private static var barMotion: Animation { YYMotion.reduce ? .easeOut(duration: 0.15) : .spring(duration: 0.22, bounce: 0) }
    private static func slide(_ edge: Edge) -> AnyTransition { YYMotion.reduce ? .opacity : .move(edge: edge).combined(with: .opacity) }

    var body: some View {
        layout
        .foregroundStyle(paper.ink)
        .buttonStyle(.plain)
        .overlay {
            if eyeCare, !paper.isEink {
                Color(hex: "#F5A63B")
                    .opacity(paper.isDark ? 0.08 : 0.12)
                    .ignoresSafeArea()
                    .allowsHitTesting(false)
                    .transition(.opacity)
            }
        }
        .animation(.easeInOut(duration: 0.2), value: eyeCare)
        // The banners sit inside the animations below, so they come and go with the bars.
        .overlay(alignment: .top) {
            if model.chapterOnlyHere, !model.searchShown {
                ChapterOnlyBanner(paper: paper)
                    .padding(.horizontal, 16)
                    .padding(.top, model.chromeShown ? 52 : 6)
                    .transition(.opacity)
            } else if model.textVersion, model.chromeShown, !model.searchShown {
                TextVersionBanner(paper: paper, core: model.core, book: model.bookID)
                    .padding(.horizontal, 16)
                    .padding(.top, 52)
                    .transition(.opacity)
            }
        }
        .animation(Self.barMotion, value: model.chromeShown)
        .animation(Self.barMotion, value: model.searchShown)
        .animation(Self.barMotion, value: model.immersive)
        .animation(Self.barMotion, value: model.barsAway)
        .animation(Self.barMotion, value: bottomMode)
        .animation(YYMotion.reduce ? .easeOut(duration: 0.16) : .spring(duration: 0.36, bounce: 0.12), value: model.capsuleMode)
        .animation(Self.barMotion, value: model.returnTo == nil)
        .animation(Self.barMotion, value: model.txtDoubt)
        .animation(Self.barMotion, value: model.recap)
        .animation(Self.barMotion, value: model.openNote)
        .animation(Self.barMotion, value: model.elsewhere == nil)
        .animation(Self.barMotion, value: model.sleepReturn)
        .animation(Self.barMotion, value: model.chapterOnlyHere)
        .animation(YYMotion.reduce ? nil : Self.barMotion, value: model.lookup == nil)
        .animation(Self.barMotion, value: model.player?.readyOfflineVoice?.id)
        // 跳过了片头: offered for 5 seconds.
        .task(id: model.player?.skippedHead) {
            guard model.player?.skippedHead != nil else { return }
            try? await Task.sleep(for: .seconds(5))
            model.player?.dismissHead()
        }
        .task(id: ChromeAutoHideKey(shown: model.chromeShown, busy: chromeBusy, immersive: model.immersive, activity: chromeActivity)) {
            // Auto-hide summoned chrome after idle (4 s in Focus Reading, 5 s in normal reading)
            // so the full top & bottom bars never stay stuck over the page text.
            guard model.chromeShown, !chromeBusy else { return }
            #if DEBUG
            if UserDefaults.standard.string(forKey: "debugSheet") == "chrome" { return }
            #endif
            try? await Task.sleep(for: .seconds(model.immersive ? 4 : 5))
            if !Task.isCancelled, model.chromeShown, !chromeBusy {
                model.chromeShown = false
            }
        }
        .sheet(isPresented: $moreShown, onDismiss: {
            if let action = pendingMoreAction {
                pendingMoreAction = nil
                action()
            }
        }) {
            PhoneReaderMoreSheet(
                model: model,
                openNotes: {
                    pendingMoreAction = {
                        model.tocInitialTab = 2
                        model.tocShown = true
                    }
                },
                openInfo: {
                    pendingMoreAction = { infoShown = true }
                },
                openImmersiveSettings: {
                    pendingMoreAction = { immersiveShown = true }
                },
                openCompanion: {
                    pendingMoreAction = {
                        model.chromeShown = false
                        model.panel = .companion
                    }
                },
                openRules: {
                    pendingMoreAction = { model.rulesShown = true }
                },
                openTxtLayout: {
                    pendingMoreAction = { model.txtShown = true }
                },
                exportMarkdown: {
                    pendingMoreAction = { exportNotes() }
                },
                dismiss: { moreShown = false }
            )
        }
        .sheet(isPresented: $listenShown) {
            ScrollView { ListenCard(model: model) }
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
                .presentationBackground(YYColor.card)
        }
        .sheet(isPresented: $readyVoicePickerShown, onDismiss: {
            pickedReadyVoiceID = nil
            model.player?.dismissReadyOfflineVoice()
        }) {
            readyVoicePickerSheet
        }
        .sheet(isPresented: $model.settingsShown) {
            ScrollView { TypesetCard(model: model) }
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
                .presentationBackgroundInteraction(.enabled(upThrough: .medium))
                .presentationBackground(YYColor.card)
        }
        .sheet(isPresented: $hitsShown) { hitList }
        .onReceive(NotificationCenter.default.publisher(for: Audiobooks.timingsChanged)) { _ in model.timingsChanged() }
        .sheet(isPresented: $model.rulesShown) {
            ReplaceRulesView(core: model.core, bookID: model.bookID, bookTitle: model.detail?.summary.title ?? "") {
                model.rulesShown = false
            }
        }
        .sheet(isPresented: $model.txtShown) {
            if let library = model.library {
                TxtLayoutView(library: library, bookID: model.bookID, bookTitle: model.detail?.summary.title ?? "",
                              go: { section in
                                  model.txtShown = false
                                  model.jump(section: section, sentence: 0)
                              }) {
                    model.txtShown = false
                }
            }
        }
        .sheet(isPresented: $immersiveShown) { PhoneImmersiveSheet(model: model) }
        .sheet(isPresented: $infoShown) {
            if let library = model.library, let book = library.books.first(where: { $0.id == model.bookID }) {
                PhoneBookInfo(library: library, book: book)
            }
        }
        .noteSender(model.noteSender)
        #if DEBUG
        // `-debugSheet listen|typeset|rules|immersive|info|companion|toc` opens that on the page.
        .task {
            guard let which = UserDefaults.standard.string(forKey: "debugSheet") else { return }
            try? await Task.sleep(for: .milliseconds(900))
            switch which {
            case "listen": listenShown = true
            case "typeset": model.settingsShown = true
            case "rules": model.rulesShown = true
            case "immersive": immersiveShown = true
            case "info": infoShown = true
            case "companion": model.panel = .companion
            case "toc": model.tocShown = true
            case "chrome": model.chromeShown = true
            default: break
            }
        }
        #endif
        .sheet(item: Binding(get: { exported.map(Shared.init) }, set: { exported = $0?.url })) { item in
            ActivitySheet(items: [item.url]).ignoresSafeArea()
        }
        .onAppear { applyBrightness(initial: true) }
        .onDisappear { restoreBrightness() }
        .onChange(of: brightnessFollow) { _, _ in applyBrightness(initial: false) }
        .onChange(of: readerBrightness) { _, _ in if !brightnessFollow { applyBrightness(initial: false) } }
        .onChange(of: scenePhase) { _, phase in
            if phase == .active { applyBrightness(initial: false) } else { restoreBrightness() }
        }
    }

    private func applyBrightness(initial: Bool) {
        guard let screen = ReaderBrightness.activeScreen else { return }
        if initial && savedSystemBrightness == nil { savedSystemBrightness = screen.brightness }
        if brightnessFollow {
            if let saved = savedSystemBrightness { screen.brightness = saved }
        } else {
            if savedSystemBrightness == nil { savedSystemBrightness = screen.brightness }
            screen.brightness = CGFloat(min(max(readerBrightness, 0.05), 1.0))
        }
    }

    private func restoreBrightness() {
        guard let saved = savedSystemBrightness, let screen = ReaderBrightness.activeScreen else { return }
        screen.brightness = saved
    }

    // MARK: Layout

    /// On the phone: the top bar, and the capsule with the cards above it, in one column at the
    /// bottom. On iPhone Duo's inner display the bottom goes under the right page (of the Duo
    /// spec), so it never sits on the fold.
    @ViewBuilder private var layout: some View {
        if InnerDisplay.isOn(sizeClass) {
            spreadLayout
        } else {
            VStack(spacing: 0) {
                topBar
                Spacer(minLength: 0)
                bottom
            }
            .padding(.horizontal, 12)
            .padding(.bottom, 4)
            .frame(maxWidth: 560)
            .frame(maxWidth: .infinity)
            .overlay(alignment: .bottom) { immersiveNote }
        }
    }

    /// The inner display: the top bar as on the phone; the capsule and its cards on the right
    /// page, trailing, as wide as the page less 16 each side and 24 above the safe area. Half
    /// folded, they start clear of the fold. Measured over the whole screen, as the pages are.
    private var spreadLayout: some View {
        GeometryReader { g in
            let width = g.size.width
            let trailing = g.safeAreaInsets.trailing
            // The right page starts past the gutter; half folded, at least 16 past the fold.
            let pageStart = max(width / 2 + CGFloat(ReaderPager.spreadMargin), (InnerDisplay.fold(in: g)?.maxX ?? 0) + 16)
            let barWidth = max(0, width - trailing - 16 - (pageStart + 16))
            // The left page ends before the gutter; half folded, at least 16 before the fold.
            let leftPageEnd = min(width / 2 - CGFloat(ReaderPager.spreadMargin), (InnerDisplay.fold(in: g)?.minX ?? width) - 16)
            VStack(spacing: 0) {
                // On the left page, so half folded it never crosses the fold.
                topBar
                    .padding(.horizontal, 12)
                    .frame(width: max(0, leftPageEnd - g.safeAreaInsets.leading - 16))
                    .padding(.leading, g.safeAreaInsets.leading + 4)
                    .padding(.top, g.safeAreaInsets.top)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Spacer(minLength: 0)
                bottom
                    .frame(width: barWidth)
                    .overlay(alignment: .bottom) { immersiveNote }
                    .padding(.trailing, trailing + 16)
                    .padding(.bottom, g.safeAreaInsets.bottom + 24)
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        // The pages' rectangle; the keyboard still pushes the search capsule up.
        .ignoresSafeArea(.container)
        .overlay(alignment: .bottom) {
            if model.immersive, bottomMode == .none, hidesProgress, ImmersivePrefs.keepsLine { hairline }
        }
    }

    @ViewBuilder private var topBar: some View {
        VStack(spacing: 8) {
            if model.searchShown {
                searchField.transition(Self.slide(.top))
            } else {
                if model.chromeShown {
                    top.transition(Self.slide(.top))
                }
                if let recap = model.recap {
                    HStack {
                        Spacer(minLength: 0)
                        RecapTopPill(recap: recap, tint: .standard, paper: paper) { model.toggleRecap() }
                    }
                    .padding(.top, model.chromeShown ? 0 : 4)
                    .transition(Self.slide(.top))
                    if recap.expanded == true, !recap.lines.isEmpty {
                        RecapCard(recap: recap, tint: .standard) { model.recap = nil }
                            .transition(Self.slide(.top))
                    }
                }
            }
        }
        .animation(YYMotion.standard, value: model.recap)
    }

    /// The bottom control surface (quiet footer, mini listen pill/orb, full capsule or search),
    /// plus transient cards and pills stacked cleanly above it.
    private var bottom: some View {
        let pillsAllowed = !model.barsAway && !model.immersive && !model.searchShown && model.lookup == nil
        return VStack(spacing: 0) {
            if let e = model.elsewhere {
                ElsewhereCard(elsewhere: e, tint: .standard, stay: { model.elsewhere = nil }, jump: { model.jumpElsewhere() })
                    .padding(.bottom, 10)
                    .transition(Self.slide(.bottom))
            }
            if let open = model.openNote {
                FootnoteCard(open: open, bookTitle: model.detail?.summary.title ?? "", tint: .standard,
                             toNotes: { model.goToNotes() }, done: { model.openNote = nil })
                    .padding(.bottom, 10)
                    .transition(Self.slide(.bottom))
            }
            if let back = model.sleepReturn, !model.searchShown {
                SleepReturnCard(when: back.when, place: "\(back.chapter) \(back.at)") { model.answerSleepReturn(go: $0) }
                    .padding(.horizontal, 2)
                    .padding(.bottom, 10)
                    .transition(Self.slide(.bottom))
            }
            if let back = model.returnTo, pillsAllowed {
                ReturnPill(page: back.page, paper: paper) { model.goBack() }
                    .padding(.bottom, bottomMode == .footer ? 10 : 8)
                    .transition(Self.slide(.bottom))
            } else if let doubt = model.txtDoubt, pillsAllowed {
                DoubtPill(doubt: doubt, paper: paper, open: { model.txtShown = true }, dismiss: { model.dismissDoubt() })
                    .padding(.bottom, bottomMode == .footer ? 10 : 8)
                    .transition(Self.slide(.bottom))
            }
            if let ready = model.player?.readyOfflineVoice,
               !model.searchShown, model.lookup == nil, !model.listenOnly {
                HStack {
                    Spacer(minLength: 0)
                    ReadyOfflineVoiceOrb(
                        voiceID: ready.id,
                        voiceName: Voices.title(ready),
                        paper: paper,
                        openPicker: {
                            pickedReadyVoiceID = ready.id
                            readyVoicePickerShown = true
                        },
                        dismiss: { model.player?.dismissReadyOfflineVoice() }
                    )
                }
                .padding(.bottom, bottomMode == .none ? 12 : 10)
                .transition(Self.slide(.bottom))
            }
            switch bottomMode {
            case .search:
                searchCapsule
                    .transition(Self.slide(.bottom))
            case .morphCapsule:
                morphCapsule
                    .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .bottom)))
            case .fullCapsule:
                capsule
                    .transition(Self.slide(.bottom))
            case .miniListen:
                miniListenPill
                    .transition(Self.slide(.bottom))
            case .miniOrb:
                HStack {
                    Spacer(minLength: 0)
                    PhoneMiniListenOrb(
                        paper: paper,
                        progress: model.chapterProgress,
                        onExpand: { model.expandListenBar() }
                    )
                }
                .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.85, anchor: .bottomTrailing).combined(with: .opacity))
            case .footer:
                readingFooter
                    .transition(.opacity)
            case .none:
                EmptyView()
            }
        }
    }

    /// Quiet single-line reading footer shown when pure-reading with the full chrome tucked away.
    /// Aligned with the page's text margins and non-interactive so edge taps always turn pages.
    private var readingFooter: some View {
        HStack(spacing: 12) {
            Text(model.chapterTitle ?? model.detail?.summary.title ?? "")
                .lineLimit(1)
                .truncationMode(.tail)
            Spacer(minLength: 8)
            Text(model.capsuleNote)
                .monospacedDigit()
                .lineLimit(1)
        }
        .font(.yy(size: 11))
        .foregroundStyle(paper.ink2.opacity(0.8))
        .padding(.horizontal, 12)
        .padding(.bottom, 4)
        .allowsHitTesting(false)
        .accessibilityHidden(true)
    }

    @ViewBuilder private var morphCapsule: some View {
        let isPron: Bool = if case .pronunciation = model.capsuleMode { true } else { false }
        let shape = RoundedRectangle(cornerRadius: isPron ? 24 : 23, style: .continuous)
        Group {
            switch model.capsuleMode {
            case .pronunciation(let find, let sentence):
                CapsulePronunciationPanel(
                    initialFind: find,
                    sentence: sentence,
                    paper: paper,
                    isPlaying: model.isPlaying,
                    compact: true,
                    onTogglePlay: { model.togglePlay() },
                    onSave: { f, r, all in model.applyPronunciationFix(find: f, replace: r, wholeLibrary: all) },
                    onClose: { model.dismissCapsuleMode() }
                )
            case .pronunciationDone, .marked, .rewound:
                CapsuleFeedbackStrip(
                    mode: model.capsuleMode,
                    paper: paper,
                    compact: true,
                    onUndoMark: { model.undoMarkedInCapsule(id: $0) },
                    onDismiss: { model.dismissCapsuleMode() }
                )
            case .idle:
                EmptyView()
            }
        }
        .frame(maxWidth: 420)
        .glass(paper, in: shape)
    }

    private var miniListenPill: some View {
        let n = model.sentenceCount
        let label = n > 0
            ? String(localized: "第 \(min(model.sentence + 1, n)) / \(n) 句")
            : (model.chapterTitle ?? model.detail?.summary.title ?? "")
        let rateBadge: String? = abs(model.rate - 1.0) >= 0.05
            ? "\(model.rate.formatted(.number.precision(.fractionLength(0...1))))×"
            : nil
        return PhoneMiniListenPill(
            paper: paper,
            label: label,
            rateBadge: rateBadge,
            isPlaying: model.isPlaying,
            progress: model.chapterProgress,
            onOpenListen: { listenShown = true },
            onMarkHeard: { model.markCurrentHeard() },
            onFixPronunciation: { model.promptPronunciation() },
            onPrevious: { model.stepSentence(forward: false) },
            onPause: { model.togglePlay() },
            onNext: { model.stepSentence(forward: true) },
            onTuck: { model.playerTucked = true }
        )
    }

    /// 沉浸, with the capsule away: the progress line, or the hairline.
    @ViewBuilder private var immersiveNote: some View {
        if model.immersive, bottomMode == .none {
            if !hidesProgress {
                Text(model.capsuleNote)
                    .font(.yy(size: 10).monospacedDigit())
                    .foregroundStyle(paper.ink2)
                    .padding(.bottom, 2)
                    .allowsHitTesting(false)
            } else if ImmersivePrefs.keepsLine, !InnerDisplay.isOn(sizeClass) {
                hairline
            }
        }
    }

    // MARK: Top

    private var top: some View {
        PhoneReaderTopBar(
            paper: paper,
            title: model.detail?.summary.title ?? "",
            subtitle: whereLine,
            bookmarked: model.bookmarkHere != nil,
            onClose: close,
            onSearch: { startSearch() },
            onBookmark: {
                chromeActivity &+= 1
                model.toggleBookmark()
            }
        ) {
            Button {
                moreShown = true
            } label: {
                readerIcon("ellipsis")
                    .background(paper.ink.opacity(0.09), in: .circle)
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel("更多")
        }
    }

    private var whereLine: String {
        // On a spread, the left page's number, as printed under it.
        let spread = InnerDisplay.isOn(sizeClass) && model.settings.mode == .paginated && !model.pager.chat
        return [model.chapterTitle == model.detail?.summary.title ? nil : model.chapterTitle,
                model.bookPage.map { String(localized: "第 \(spread ? $0 * 2 - 1 : $0) 页") }].compactMap { $0 }.joined(separator: " · ")
    }

    // MARK: The capsule

    @ViewBuilder private var readyVoicePickerSheet: some View {
        if let player = model.player {
            let lang = player.language
            let section = player.spot?.section ?? model.section ?? 0
            let bookLine = CloneVoiceView.nextBookSentence(core: model.core, bookID: model.bookID, section: section, player: player, language: lang)
            let previewText = bookLine.isEmpty ? VoicePickerSheet.sampleText(for: lang) : bookLine
            let currentName = player.currentVoice(for: lang).map(Voices.title)
            let keepLabel = currentName.map { String(localized: "暂不更换，继续用「\($0)」") } ?? String(localized: "暂不更换")
            VoicePickerSheet(
                title: String(localized: "选择声音"),
                language: lang,
                bookID: model.bookID,
                selectedID: player.currentVoice(for: lang)?.id,
                sampleText: VoicePickerSheet.sampleText(for: lang),
                previewSentence: bookLine.isEmpty ? nil : bookLine,
                sampleSource: player.bookTitle,
                readyVoiceID: pickedReadyVoiceID ?? player.readyOfflineVoice?.id,
                keepCurrentLabel: keepLabel,
                onPreview: { v in player.preview(v, text: previewText) },
                onSelect: { v in
                    let prevID = player.currentVoice(for: lang)?.id
                    player.choose(v, for: lang)
                    readyVoicePickerShown = false
                    if prevID != v.id {
                        model.show(Library.Toast(
                            icon: "checkmark.circle.fill",
                            title: String(localized: "已切换为「\(Voices.title(v))」"),
                            detail: String(localized: "从当前句继续")
                        ))
                    }
                },
                onClose: {
                    readyVoicePickerShown = false
                    player.dismissReadyOfflineVoice()
                }
            )
            .presentationDetents([.medium, .large])
            .presentationDragIndicator(.visible)
            .presentationBackground(YYColor.bg)
        }
    }

    private var capsule: some View {
        let reading = model.player?.spot != nil
        let n = model.sentenceCount
        return PhoneReaderCapsule(
            paper: paper,
            title: model.capsuleAlert?.title ?? (reading && n > 0 ? String(localized: "第 \(min(model.sentence + 1, n)) / \(n) 句") : (model.chapterTitle ?? model.detail?.summary.title ?? "")),
            note: model.capsuleAlert?.note ?? model.capsuleNote,
            noteAccent: model.capsuleAlert != nil,
            centerHint: model.capsuleAlert?.note ?? String(localized: "听书：声音、速度、睡眠定时"),
            playing: model.isPlaying,
            progress: model.chapterProgress,
            playEnabled: model.player != nil,
            sentenceStep: reading,
            onToc: {
                model.tocInitialTab = 0
                model.tocShown = true
            },
            onCenter: { if let alert = model.capsuleAlert { alert.action() } else { listenShown = true } },
            onPrevious: {
                chromeActivity &+= 1
                step(forward: false)
            },
            onPlay: {
                let starting = !model.isPlaying
                model.togglePlay()
                if starting {
                    // Collapse full chrome immediately into the slim mini listening pill.
                    model.chromeShown = false
                } else {
                    chromeActivity &+= 1
                }
            },
            onNext: {
                chromeActivity &+= 1
                step(forward: true)
            },
            onTypeset: { model.settingsShown = true }
        )
    }

    private func capButton(_ name: String) -> some View {
        Image(systemName: name).font(.yy(size: 14, weight: .semibold)).frame(minWidth: 38, minHeight: 44).contentShape(.rect)
    }

    private func step(forward: Bool) {
        if model.player?.spot != nil {
            model.stepSentence(forward: forward)
        } else {
            model.turn(forward: forward)
        }
    }

    private var hairline: some View {
        GeometryReader { g in
            Rectangle().fill(paper.ink.opacity(0.25)).frame(width: g.size.width * model.bookFraction)
        }
        .frame(height: 1)
        .ignoresSafeArea()
        .allowsHitTesting(false)
    }

    // MARK: 书内搜索

    private func startSearch() {
        model.chromeShown = false
        model.searchShown = true
        typing = true
    }

    private var searchField: some View {
        HStack(spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.yy(size: 14, weight: .medium)).foregroundStyle(paper.ink2)
                TextField("", text: $model.query, prompt: Text("搜这本书").foregroundStyle(paper.ink2))
                    .font(.yy(size: 15))
                    .focused($typing)
                    .submitLabel(.search)
                    .onChange(of: model.query) { model.search() }
                if !model.query.isEmpty {
                    Button { model.query = "" } label: {
                        Image(systemName: "xmark.circle.fill")
                            .foregroundStyle(paper.ink2)
                            .frame(width: 36, height: 36)
                            .contentShape(Rectangle().inset(by: -4))
                    }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel("清空")
                }
            }
            .padding(.leading, 12)
            .padding(.trailing, model.query.isEmpty ? 12 : 4)
            .frame(height: 40)
            .glass(paper, in: Capsule())
            Button { model.endSearch() } label: {
                Image(systemName: "xmark").font(.yy(size: 14, weight: .semibold))
                    .frame(width: 40, height: 40)
                    .glass(paper, in: Circle())
                    .contentShape(Circle().inset(by: -2))
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel("结束搜索")
        }
        .padding(.top, 4)
    }

    private var searchCapsule: some View {
        let q = model.query.trimmingCharacters(in: .whitespaces)
        return HStack(spacing: 2) {
            Button { typing = false; hitsShown = true } label: { capButton("list.bullet.rectangle") }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel("全部结果")
                .disabled(model.hits.isEmpty && q.isEmpty)
            VStack(alignment: .leading, spacing: 1) {
                Group {
                    if q.isEmpty {
                        Text("输入要找的字")
                    } else if model.hits.isEmpty {
                        Text("没找到“\(q)”")
                    } else if model.hitsCapped {
                        Text("第 \((model.hitIndex ?? 0) + 1) / \(model.hits.count)+ 处")
                    } else {
                        Text("第 \((model.hitIndex ?? 0) + 1) / \(model.hits.count) 处")
                    }
                }
                .font(.yy(size: 12, weight: .semibold))
                .contentTransition(.numericText())
                .animation(YYMotion.quick, value: model.hitIndex)
                Text(hitChapter ?? (q.isEmpty ? String(localized: "输完直接跳到第一处") : String(localized: "可以问问书伴")))
                    .font(.yy(size: 10.5)).foregroundStyle(paper.ink2)
            }
            .monospacedDigit()
            .lineLimit(1)
            .padding(.leading, 2)
            .frame(maxWidth: .infinity, alignment: .leading)
            Button { model.nextHit(forward: false) } label: { capButton("chevron.left") }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel("上一处")
                .disabled(model.hits.isEmpty)
            Button { model.nextHit(forward: true) } label: { capButton("chevron.right") }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel("下一处")
                .disabled(model.hits.isEmpty)
        }
        .padding(.horizontal, 5)
        .padding(.vertical, 4)
        .frame(minHeight: 50)
        .glass(paper, in: Capsule())
    }

    private var hitChapter: String? {
        guard let i = model.hitIndex, model.hits.indices.contains(i) else { return nil }
        let hit = model.hits[i]
        return [model.detail?.chapterTitle(for: hit.section), model.estimatedPage(section: hit.section, sentence: Int(hit.sentence)).map { String(localized: "第 \($0) 页") }]
            .compactMap { $0 }.joined(separator: " · ")
    }

    /// 全部结果: the same cards as 搜索结果 — 问书伴 first, then each place as the book's own
    /// sentence and where it is; the one showing is tinted.
    private var hitList: some View {
        let q = model.query.trimmingCharacters(in: .whitespaces)
        let tint = YYTint.standard
        return ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    YYSheetHeader(title: model.hits.isEmpty ? String(localized: "没找到") : (model.hitsCapped ? String(localized: "\(model.hits.count)+ 处") : String(localized: "\(model.hits.count) 处")),
                                  subtitle: q.isEmpty ? nil : "“\(q)”") { hitsShown = false }
                    if !q.isEmpty, model.detail?.summary.format != .pdf {
                        Button {
                            hitsShown = false
                            model.endSearch()
                            model.ask(q)
                        } label: {
                            HStack(spacing: YYUI.s(10)) {
                                Image(systemName: "sparkle")
                                    .font(YYUI.font(14, .semibold))
                                    .foregroundStyle(tint.accent)
                                    .frame(width: YYUI.s(32), height: YYUI.s(32))
                                    .background(tint.accentSoft, in: .rect(cornerRadius: YYUI.s(10)))
                                VStack(alignment: .leading, spacing: YYUI.s(1)) {
                                    Text("问书伴“\(q)”").font(YYUI.font(12, .semibold)).foregroundStyle(tint.accent).lineLimit(1)
                                    Text("只用你读过的部分回答，带出处").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                                }
                                Spacer(minLength: 0)
                                Image(systemName: "chevron.right").font(YYUI.font(10, .semibold)).foregroundStyle(YYColor.t3)
                            }
                            .padding(.vertical, YYUI.s(10))
                            .padding(.horizontal, YYUI.s(12))
                            .yyCard()
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .padding(.top, YYUI.s(14))
                    }
                    if !model.hits.isEmpty {
                        LazyVStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(model.hits.enumerated()), id: \.offset) { i, hit in
                                if i > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, YYUI.s(12)) }
                                Button {
                                    hitsShown = false
                                    model.show(hit: i)
                                } label: {
                                    VStack(alignment: .leading, spacing: YYUI.s(4)) {
                                        Text(highlighted(hit))
                                            .font(YYUI.font(12.5, design: .serif))
                                            .lineSpacing(YYUI.s(12.5) * 0.8)
                                            .foregroundStyle(YYColor.t1)
                                            .lineLimit(2)
                                        Text(place(hit))
                                            .font(YYUI.font(10)).monospacedDigit()
                                            .foregroundStyle(i == model.hitIndex ? tint.accent : YYColor.t3)
                                            .lineLimit(1)
                                    }
                                    .padding(.top, YYUI.s(10))
                                    .padding(.bottom, YYUI.s(9))
                                    .padding(.horizontal, YYUI.s(12))
                                    .frame(maxWidth: .infinity, alignment: .leading)
                                    .background(i == model.hitIndex ? tint.accentSoft : .clear)
                                    .contentShape(.rect)
                                }
                                .buttonStyle(.plain)
                                .id(i)
                            }
                        }
                        .background(YYColor.card)
                        .clipShape(.rect(cornerRadius: YYUI.s(14)))
                        .overlay(RoundedRectangle(cornerRadius: YYUI.s(14)).strokeBorder(YYColor.line, lineWidth: 0.5))
                        .padding(.top, YYUI.s(14))
                        if model.hitsCapped {
                            Text("仅显示前 \(QuickSearch.hitLimit) 处，输入更长的词可缩小范围")
                                .font(YYUI.font(10))
                                .foregroundStyle(YYColor.t3)
                                .frame(maxWidth: .infinity)
                                .padding(.top, YYUI.s(10))
                        }
                    }
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.top, YYUI.sheetGrabberTop)
                .padding(.bottom, YYUI.s(20))
            }
            .scrollIndicators(.never)
            .background(YYColor.bg)
            .onAppear {
                if let i = model.hitIndex { proxy.scrollTo(i, anchor: .center) }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }

    /// "第一章 · 第 3 页".
    private func place(_ hit: SearchHit) -> String {
        [model.detail?.chapterTitle(for: hit.section), model.estimatedPage(section: hit.section, sentence: Int(hit.sentence)).map { String(localized: "第 \($0) 页") }]
            .compactMap { $0 }.joined(separator: " · ")
    }

    private func highlighted(_ hit: SearchHit) -> AttributedString {
        var matched = AttributedString(hit.matched)
        matched.foregroundColor = YYTint.standard.accent
        matched.inlinePresentationIntent = .stronglyEmphasized
        matched.underlineStyle = .single
        return AttributedString(hit.before) + matched + AttributedString(hit.after)
    }

    // MARK: 导出笔记

    private struct Shared: Identifiable {
        let url: URL
        var id: URL { url }
    }

    private func exportNotes() {
        guard let library = model.library else { return }
        let noted = NotedBook.load(library, only: [model.bookID])
        guard !noted.isEmpty else {
            // Nothing to export isn't a failure: a passing note.
            model.show(Library.Toast(icon: "highlighter", title: String(localized: "这本书还没有划线和想法"), detail: String(localized: "选中文字点“划线”，就会出现在这里")))
            return
        }
        let title = model.detail?.summary.title ?? String(localized: "笔记")
        let dir = FileManager.default.temporaryDirectory.appending(path: "export-\(UUID().uuidString)")
        let url = dir.appending(path: String(localized: "\(title.replacingOccurrences(of: "/", with: "-")) · 笔记.md"))
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try Data(NotedBook.markdown(noted, title: title).utf8).write(to: url)
            exported = url
        } catch {
            model.failure = String(localized: "没导出来，再试一次")
        }
    }
}

/// The system's share sheet, for a file made on the spot.
struct ActivitySheet: UIViewControllerRepresentable {
    let items: [Any]

    func makeUIViewController(context: Context) -> UIActivityViewController {
        UIActivityViewController(activityItems: items, applicationActivities: nil)
    }

    func updateUIViewController(_ controller: UIActivityViewController, context: Context) {}
}

/// The glass row over a page: 返回, the book's name, 搜索 · 书签 · 更多. The words are the caller's;
/// the WebView under the real page is not part of this view.
struct PhoneReaderTopBar<More: View>: View {
    var paper: YYPaper
    var title: String
    var subtitle: String
    var bookmarked: Bool
    var onClose: () -> Void
    var onSearch: () -> Void
    var onBookmark: () -> Void
    @ViewBuilder var more: () -> More

    var body: some View {
        HStack(spacing: 8) {
            Button(action: onClose) {
                Image(systemName: "chevron.left").font(.yy(size: 15, weight: .semibold))
                    .frame(width: 40, height: 40)
                    .glass(paper, in: Circle())
                    .contentShape(Circle().inset(by: -2))
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel("返回书架")
            VStack(spacing: 1) {
                Text(title).font(.yy(size: 13, weight: .semibold)).lineLimit(1)
                Text(subtitle).font(.yy(size: 11)).foregroundStyle(paper.ink2).lineLimit(1).monospacedDigit()
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 4)
            .frame(maxWidth: .infinity)
            .frame(minHeight: 40)
            .glass(paper, in: Capsule())
            HStack(spacing: 0) {
                Button(action: onSearch) { readerIcon("magnifyingglass") }
                    .buttonStyle(YYPressStyle(scale: 0.92))
                    .accessibilityLabel("搜索这本书")
                Button(action: onBookmark) {
                    readerIcon(bookmarked ? "bookmark.fill" : "bookmark")
                        .foregroundStyle(bookmarked ? YYTint.standard.accent : paper.ink)
                        .contentTransition(.symbolEffect(.replace))
                        .animation(YYMotion.quick, value: bookmarked)
                }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel(bookmarked ? String(localized: "拿掉书签") : String(localized: "加书签"))
                more()
            }
            .padding(.horizontal, 3)
            .frame(height: 40)
            .glass(paper, in: Capsule())
        }
        .padding(.top, 4)
    }
}

/// The capsule under the page: 目录, where the reader is, ‹ 朗读 ›, Aa, and how far into the chapter.
struct PhoneReaderCapsule: View {
    var paper: YYPaper
    var title: String
    var note: String
    var noteAccent: Bool
    var centerHint: String = String(localized: "听书：声音、速度、睡眠定时")
    var playing: Bool
    var progress: Double
    var playEnabled: Bool
    var sentenceStep: Bool
    var onToc: () -> Void
    var onCenter: () -> Void
    var onPrevious: () -> Void
    var onPlay: () -> Void
    var onNext: () -> Void
    var onTypeset: () -> Void

    var body: some View {
        let accentColor = paper.isEink ? paper.ink : YYTint.standard.accent
        let pillBg: Color = noteAccent
            ? (paper.isEink ? paper.bg : YYTint.standard.accentSoft)
            : (paper.isEink ? paper.bg : paper.ink.opacity(paper.isDark ? 0.09 : 0.055))
        let pillBorder: Color = noteAccent
            ? (paper.isEink ? paper.ink : YYTint.standard.accent.opacity(0.32))
            : (paper.isEink ? paper.line : paper.ink.opacity(paper.isDark ? 0.11 : 0.065))
        HStack(spacing: 2) {
            Button(action: onToc) { capButton("list.bullet", width: 36) }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel("目录、书签和笔记")
            Button(action: onCenter) {
                HStack(spacing: 4) {
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 4) {
                            if noteAccent {
                                Image(systemName: "exclamationmark.circle.fill")
                                    .font(.yy(size: 10, weight: .semibold))
                                    .foregroundStyle(accentColor)
                            } else if playing || sentenceStep {
                                SpeakingBars(paused: !playing)
                                    .frame(width: 11, height: 10)
                            } else {
                                Image(systemName: "headphones")
                                    .font(.yy(size: 10, weight: .semibold))
                                    .foregroundStyle(paper.ink2)
                            }
                            Text(title)
                                .font(.yy(size: 12, weight: .semibold))
                                .foregroundStyle(paper.ink)
                        }
                        Text(note)
                            .font(.yy(size: 10.5, weight: noteAccent ? .semibold : .regular))
                            .foregroundStyle(noteAccent ? accentColor : paper.ink2)
                    }
                    .monospacedDigit()
                    .lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)

                    Image(systemName: noteAccent ? "chevron.right" : "chevron.up")
                        .font(.yy(size: 9.5, weight: .semibold))
                        .foregroundStyle(noteAccent ? accentColor : paper.ink2.opacity(0.85))
                }
                .padding(.leading, 9)
                .padding(.trailing, 7)
                .frame(maxWidth: .infinity)
                .frame(minHeight: 40)
                .background(pillBg, in: RoundedRectangle(cornerRadius: 16, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 16, style: .continuous)
                        .strokeBorder(pillBorder, lineWidth: 0.5)
                )
                .contentShape(RoundedRectangle(cornerRadius: 16, style: .continuous))
            }
            .buttonStyle(YYPressStyle(scale: 0.96))
            .accessibilityHint(centerHint)
            Button(action: onPrevious) { capButton("chevron.left", width: 30) }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel(sentenceStep ? String(localized: "上一句") : String(localized: "上一页"))
            Button(action: onPlay) {
                Image(systemName: playing ? "pause.fill" : "play.fill")
                    // ▶ and ❚❚ turn into each other.
                    .contentTransition(.symbolEffect(.replace))
                    .animation(YYMotion.quick, value: playing)
                    .font(.yy(size: 14, weight: .semibold))
                    .foregroundStyle(paper.onMain)
                    .frame(width: 40, height: 40)
                    // The theme colour on every paper but e-ink, as the other play buttons.
                    .background(YYTint.standard.play(on: paper), in: .circle)
                    .contentShape(Circle().inset(by: -2))
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel(playing ? String(localized: "暂停") : String(localized: "朗读"))
            .disabled(!playEnabled)
            Button(action: onNext) { capButton("chevron.right", width: 30) }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel(sentenceStep ? String(localized: "下一句") : String(localized: "下一页"))
            Button(action: onTypeset) {
                Text("Aa").font(.yy(size: 14, weight: .semibold)).frame(minWidth: 36, minHeight: 44).contentShape(Rectangle().inset(by: -4))
            }
            .buttonStyle(YYPressStyle(scale: 0.92))
            .accessibilityLabel("排版")
        }
        .padding(.horizontal, 5)
        .padding(.vertical, 4)
        .frame(minHeight: 50)
        .glass(paper, in: Capsule())
        .overlay(alignment: .bottom) {
            GeometryReader { g in
                Capsule().fill(paper.line)
                Capsule().fill(YYTint.standard.accent).frame(width: g.size.width * progress)
            }
            .animation(YYMotion.standard, value: progress)
            .frame(height: 2)
            .padding(.horizontal, 24)
        }
    }

    private func capButton(_ name: String, width: CGFloat = 38) -> some View {
        Image(systemName: name)
            .font(.yy(size: 14, weight: .semibold))
            .frame(minWidth: width, minHeight: 44)
            .contentShape(Rectangle().inset(by: -4))
    }
}

/// Slim 34pt listening pill shown while reading aloud with top chrome dismissed.
/// Fits strictly below `--yy-bottom` (`safeArea + 44`), so it never covers the last line of text.
/// Swipe down or tap the trailing chevron to tuck into `PhoneMiniListenOrb`.
struct PhoneMiniListenPill: View {
    var paper: YYPaper
    var label: String
    var rateBadge: String?
    var isPlaying: Bool = true
    var progress: Double
    var onOpenListen: () -> Void
    var onMarkHeard: () -> Void
    var onFixPronunciation: () -> Void
    var onPrevious: () -> Void
    var onPause: () -> Void
    var onNext: () -> Void
    var onTuck: () -> Void

    @State private var dragY: CGFloat = 0

    private var accentColor: Color {
        paper.isEink ? paper.ink : YYTint.standard.accent
    }

    var body: some View {
        let clampedProgress = min(1, max(0, progress))
        HStack(spacing: 2) {
            Button(action: onOpenListen) {
                HStack(spacing: 5) {
                    SpeakingBars(paused: !isPlaying)
                        .frame(width: 13, height: 12)
                    Text(label)
                        .font(.yy(size: 11.5, weight: .medium))
                        .foregroundStyle(paper.ink)
                        .monospacedDigit()
                        .lineLimit(1)
                    if let rateBadge {
                        Text(rateBadge)
                            .font(.yy(size: 10, weight: .medium))
                            .foregroundStyle(paper.ink2)
                            .monospacedDigit()
                            .lineLimit(1)
                    }
                    Image(systemName: "chevron.up")
                        .font(.yy(size: 8.5, weight: .semibold))
                        .foregroundStyle(paper.ink2.opacity(0.75))
                }
                .padding(.leading, 10)
                .padding(.trailing, 4)
                .frame(maxWidth: .infinity, alignment: .leading)
                .frame(height: 36)
                .contentShape(Rectangle().inset(by: -5))
            }
            .buttonStyle(YYPressStyle(scale: 0.97))
            .accessibilityLabel(label)
            .accessibilityHint(String(localized: "听书：声音、速度、睡眠定时"))

            Button(action: onMarkHeard) {
                Image(systemName: "bookmark")
                    .font(.yy(size: 11.5, weight: .medium))
                    .foregroundStyle(paper.ink2)
                    .frame(width: 28, height: 34)
                    .contentShape(Rectangle().inset(by: -4))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel("记下这句")

            Button(action: onFixPronunciation) {
                Image(systemName: "waveform")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(accentColor)
                    .frame(width: 26, height: 26)
                    .background(accentColor.opacity(paper.isDark ? 0.18 : 0.10), in: .circle)
                    .contentShape(Circle().inset(by: -6))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel("纠正读音")

            Rectangle()
                .fill(paper.line)
                .frame(width: 0.5, height: 14)
                .padding(.horizontal, 2)

            Button(action: onPrevious) {
                Image(systemName: "chevron.left")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(paper.ink)
                    .frame(width: 24, height: 34)
                    .contentShape(Rectangle().inset(by: -4))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel("上一句")

            Button(action: onPause) {
                Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                    .font(.yy(size: 11, weight: .semibold))
                    .foregroundStyle(paper.onMain)
                    .frame(width: 28, height: 28)
                    .background(YYTint.standard.play(on: paper), in: .circle)
                    .contentShape(Circle().inset(by: -8))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel(isPlaying ? "暂停" : "播放")

            Button(action: onNext) {
                Image(systemName: "chevron.right")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(paper.ink)
                    .frame(width: 24, height: 34)
                    .contentShape(Rectangle().inset(by: -4))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel("下一句")

            Button(action: onTuck) {
                Image(systemName: "chevron.down")
                    .font(.yy(size: 10.5, weight: .semibold))
                    .foregroundStyle(paper.ink2)
                    .frame(width: 24, height: 34)
                    .contentShape(Rectangle().inset(by: -4))
            }
            .buttonStyle(YYPressStyle(scale: 0.9))
            .accessibilityLabel("收起播放条")
        }
        .padding(.trailing, 4)
        .frame(height: 38)
        .frame(maxWidth: 360)
        .glass(paper, in: Capsule())
        .overlay(alignment: .bottom) {
            GeometryReader { g in
                Capsule().fill(paper.line)
                Capsule().fill(accentColor).frame(width: g.size.width * clampedProgress)
            }
            .animation(YYMotion.standard, value: clampedProgress)
            .frame(height: 1.5)
            .padding(.horizontal, 18)
        }
        .offset(y: YYMotion.reduce ? 0 : (dragY >= 0 ? dragY : dragY * 0.18))
        .opacity(YYMotion.reduce ? 1 : max(0.35, 1.0 - Double(max(0, dragY)) / 80.0))
        .gesture(
            DragGesture(minimumDistance: 8)
                .onChanged { value in
                    guard !YYMotion.reduce else { return }
                    guard abs(value.translation.height) >= abs(value.translation.width) else { return }
                    dragY = value.translation.height
                }
                .onEnded { value in
                    if value.translation.height > 28 || value.predictedEndTranslation.height > 72 {
                        dragY = 0
                        withAnimation(YYMotion.standard) { onTuck() }
                    } else {
                        withAnimation(YYMotion.quick) { dragY = 0 }
                    }
                }
        )
    }
}

/// Compact 36pt corner listening orb shown when the user tucks `PhoneMiniListenPill` or scrolls down
/// while listening. Displays a hairline progress ring; tapping expands back to the mini pill.
struct PhoneMiniListenOrb: View {
    var paper: YYPaper
    var progress: Double
    var onExpand: () -> Void

    private var accentColor: Color {
        paper.isEink ? paper.ink : YYTint.standard.accent
    }

    var body: some View {
        let clamped = min(1, max(0, progress))
        Button(action: onExpand) {
            ZStack {
                Circle()
                    .stroke(paper.line, lineWidth: 2)
                Circle()
                    .trim(from: 0, to: clamped)
                    .stroke(accentColor, style: StrokeStyle(lineWidth: 2, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(YYMotion.standard, value: clamped)
                Image(systemName: "waveform")
                    .font(.yy(size: 13.5, weight: .semibold))
                    .foregroundStyle(accentColor)
            }
            .frame(width: 36, height: 36)
            .glass(paper, in: Circle())
            .contentShape(Circle().inset(by: -4))
        }
        .buttonStyle(YYPressStyle(scale: 0.92))
        .accessibilityLabel("展开播放条")
        .accessibilityValue("\(Int((clamped * 100).rounded()))%")
    }
}

/// The chrome over a page of 《故乡》. The sentence is a fixture; the bars are the ones the reader uses.
/// The WebView that sets the real page is not in this picture.
struct PhoneReaderChromeFixture: View {
    var body: some View {
        let paper = YYPaper.kami
        ZStack(alignment: .top) {
            paper.bg
            VStack(alignment: .leading, spacing: 0) {
                Text("故乡")
                    .font(YYUI.serif(22, .bold))
                    .foregroundStyle(paper.ink)
                Text("我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。")
                    .font(YYUI.serif(17))
                    .foregroundStyle(paper.ink)
                    .lineSpacing(10)
                    .padding(.top, 18)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.horizontal, 28)
            .padding(.top, 96)
            VStack {
                PhoneReaderTopBar(
                    paper: paper, title: "故乡", subtitle: String(localized: "第 1 页"), bookmarked: true,
                    onClose: {}, onSearch: {}, onBookmark: {}
                ) {
                    readerIcon("ellipsis").background(paper.ink.opacity(0.09), in: .circle)
                }
                Spacer()
                PhoneReaderCapsule(
                    paper: paper, title: "故乡", note: String(localized: "第 1 页"), noteAccent: false,
                    playing: false, progress: 0.08, playEnabled: true, sentenceStep: false,
                    onToc: {}, onCenter: {}, onPrevious: {}, onPlay: {}, onNext: {}, onTypeset: {}
                )
            }
            .padding(.horizontal, 12)
            .padding(.top, 8)
            .padding(.bottom, 12)
        }
    }
}

fileprivate func readerIcon(_ name: String) -> some View {
    Image(systemName: name)
        .font(.yy(size: 15, weight: .medium))
        .frame(width: 36, height: 36)
        .contentShape(Circle().inset(by: -4))
}

private extension View {
    /// The page's frosted glass, as on the capsule. On 墨水屏模式, no glass and no shadow:
    /// pure white with a black border and tighter corners.
    @ViewBuilder func glass(_ paper: YYPaper, in shape: some Shape) -> some View {
        if paper.id == EInk.paper.id {
            let flat = shape is Circle ? AnyShape(Circle()) : AnyShape(RoundedRectangle(cornerRadius: 12))
            background(paper.bg, in: flat)
                .overlay(flat.stroke(paper.ink, lineWidth: 1))
        } else {
            yyGlass(shape, fill: paper.glass, line: paper.line, shadow: paper.isDark ? 0.4 : 0.1)
        }
    }
}
#endif
