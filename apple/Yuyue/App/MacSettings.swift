import SwiftUI
import YuyueCore

#if os(macOS)

/// Keeps a weak reference to the settings `NSWindow` so confirmation alerts don't lose the parent window.
@MainActor
private final class SettingsWindowBox {
    weak var window: NSWindow?
}

/// 设置 (⌘,): the pages down a sidebar on the left, the page in the middle, and on the right what
/// was last changed, each change undoable, with a sample of the page where there's something to
/// see. Changes take at once; there is no 应用 button.
struct MacSettings: View {
    let library: Library
    @AppStorage("settings.page") private var page = SettingsPage.general
    /// Opened again for a new language: the old window's picture, swept away once this one is in its place.
    @State private var sweep = AppLanguage.pendingSweep
    @State private var pendingLanguage: AppLanguage?
    @State private var windowBox = SettingsWindowBox()

    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    /// One height for every page, so switching pages doesn't move the window; less on a small screen.
    private var height: CGFloat {
        min(640, (NSScreen.main?.visibleFrame.height ?? 800) - 140)
    }

    var body: some View {
        HStack(spacing: 0) {
            SettingsSidebar(page: $page, tint: tint)
                .ignoresSafeArea(.container, edges: .top)
            VStack(spacing: 0) {
                if page == .listening, ListeningFocus.shared.source != nil {
                    // One source's voices: no page title, and it scrolls only its own list.
                    ListeningPage()
                        .padding(.leading, 24)
                        .padding(.trailing, 24)
                        .padding(.top, 24)
                        .padding(.bottom, 12)
                        .ignoresSafeArea(.container, edges: .top)
                } else if page == .update {
                    ScrollView {
                        MacUpdateSettings(windowHeight: height)
                            .padding(.leading, 24)
                            .padding(.trailing, 26)
                            .padding(.top, 10)
                            .padding(.bottom, 24)
                            .frame(maxWidth: .infinity, alignment: .topLeading)
                            .buttonStyle(SettingsActionStyle())
                    }
                    .scrollBounceBehavior(.basedOnSize)
                    .padding(.trailing, 6)
                    .ignoresSafeArea(.container, edges: .top)
                    .id(page)
                    .transition(YYMotion.swap)
                } else {
                    SettingsPageHeader(page: page)
                    ScrollView {
                        content(page)
                    }
                    .scrollBounceBehavior(.basedOnSize)
                    // The scroller keeps clear of the line by the right column.
                    .padding(.trailing, 6)
                    // Rows slide under the header softly instead of being cut at its edge.
                    .mask {
                        VStack(spacing: 0) {
                            LinearGradient(colors: [.black.opacity(0), .black], startPoint: .top, endPoint: .bottom).frame(height: 14)
                            Rectangle()
                        }
                    }
                    // A new page starts at its top, and comes in a few points lower with a fade.
                    .id(page)
                    .transition(YYMotion.swap)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(YYColor.bg.ignoresSafeArea())
            if page.hasInspector {
                SettingsInspector(page: page, library: library)
                    .ignoresSafeArea(.container, edges: .top)
            }
        }
        // Nothing for the right column: the page takes the width, the corner stays where it was.
        .overlay(alignment: .topTrailing) {
            if !page.hasInspector {
                SettingsCorner(compact: true)
                    .padding(.trailing, 10)
                    .padding(.top, 13)
                    .ignoresSafeArea(.container, edges: .top)
            }
        }
        // The title bar is see-through over the three columns; no toolbar, no title in it.
        .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
        .toolbar(removing: .title)
        .frame(width: 980, height: height)
        .tint(tint.accent)
        .navigationTitle(page.title)
        .background(SettingsTitlebar { windowBox.window = $0 })
        // At the top of the sidebar, as in the shelf window.
        .trafficLights(CGPoint(x: 22, y: 20))
        .onAppear {
            SettingsLog.shared.library = library
        }
        .background(SettingsUndoKey())
        // Opened again in the new language: the old words swept away.
        .overlay(alignment: .top) {
            if let note = library.toast {
                UndoToast(toast: note, undo: { library.undo() })
                    .padding(.horizontal, 16)
                    .padding(.top, 10)
                    .transition(UndoToast.transition)
                    .id(note.id)
            }
        }
        .yyToastMotion(value: library.toast?.id)
        .alert(
            pendingLanguage.map(AppLanguage.restartPromptTitle(for:)) ?? "",
            isPresented: Binding(
                get: { pendingLanguage != nil },
                set: { if !$0 { pendingLanguage = nil } }
            ),
            presenting: pendingLanguage
        ) { pending in
            Button("立即重开", role: .destructive) {
                let from = MacLanguageCard.name(AppLanguage.chosen)
                AppLanguage.choose(pending)
                SettingsLog.shared.flash = .init(
                    date: .now,
                    title: String(localized: "语言"),
                    to: MacLanguageCard.name(pending),
                    from: from
                )
                pendingLanguage = nil
                NotificationCenter.default.post(name: .relaunchForLanguage, object: pending.effective)
            }
            Button("取消", role: .cancel) {
                pendingLanguage = nil
            }
        } message: { _ in
            Text("切换界面语言会重新启动语阅，并自动回到当前设置页。")
        }
        .overlay {
            if let sweep {
                InkSweep(old: Image(nsImage: sweep.picture), forward: sweep.forward, delay: 0.05) { self.sweep = nil }
                    .ignoresSafeArea()
                    .background(LanguageTakeOver())
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: .confirmLanguageRelaunch)) { note in
            guard let to = note.object as? AppLanguage else { return }
            pendingLanguage = to
        }
        // A new language: a new 语阅 takes this window's place, and the words are swept into it.
        .onReceive(NotificationCenter.default.publisher(for: .relaunchForLanguage)) { note in
            guard let to = note.object as? AppLanguage,
                  let window = windowBox.window ?? NSApp.keyWindow ?? NSApp.mainWindow else { return }
            AppLanguage.handOverWhenClear(window, to: to)
        }
    }

    @ViewBuilder private func content(_ p: SettingsPage) -> some View {
        Group {
            switch p {
            case .general: GeneralSettings()
            case .appearance: AppearanceSettings()
            case .reading: ReadingSettings(library: library)
            case .listening: ListeningPage()
            case .foreign: ForeignSettings(library: library)
            case .companion: CompanionSettings(library: library)
            case .notes: NoteExportSettings()
            case .sync: SyncSettings(library: library)
            case .storage: StorageSettings(library: library)
            case .data: DataSettings(library: library)
            case .update: MacUpdateSettings(windowHeight: height)
            }
        }
        .padding(.leading, 24)
        .padding(.trailing, 18)
        .padding(.top, 10)
        .padding(.bottom, 24)
        .frame(maxWidth: .infinity, alignment: .top)
        // Two kinds of button in 设置: the one solid main action a page may have sets its own
        // style; every other action is words, no fill, no border.
        .buttonStyle(SettingsActionStyle())
    }
}

#endif

enum SettingsPage: String, CaseIterable, Identifiable {
    case general, appearance, reading, foreign, listening, companion, sync, notes, storage, data, update

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: String(localized: "通用")
        case .appearance: String(localized: "外观")
        case .reading: String(localized: "阅读")
        case .foreign: String(localized: "外文")
        case .listening: String(localized: "朗读")
        case .companion: String(localized: "书伴")
        case .sync: String(localized: "同步")
        case .notes: String(localized: "笔记导出")
        case .storage: String(localized: "存储")
        case .data: String(localized: "数据")
        case .update: String(localized: "关于")
        }
    }

    /// Whether the right column has anything for this page (a sample, a card); without, it isn't shown.
    var hasInspector: Bool {
        switch self {
        case .general, .appearance, .sync, .update: false
        default: true
        }
    }

    /// Under the page's name in its header: what the page is for, in a line.
    var summary: String {
        switch self {
        case .general: String(localized: "语言、启动行为与导入方式")
        case .appearance: String(localized: "应用界面的外观与配色；书页纸色在「阅读」中设置")
        case .reading: String(localized: "新书的默认排版；单本书可在阅读页 Aa 中单独调整")
        case .foreign: String(localized: "查词、难词注释、生词与离线翻译")
        case .listening: String(localized: "朗读、查词发音与播客音色")
        case .companion: String(localized: "阅读问答与播客撰稿所用的大模型")
        case .sync: String(localized: "通过个人云盘在多台设备间同步")
        case .notes: String(localized: "将划线与想法发送到常用笔记软件")
        case .storage: String(localized: "书库、有声书与本地缓存占用的空间")
        case .data: String(localized: "本地备份快照与阅读记录导出")
        case .update: String(localized: "版本、更新和作者")
        }
    }

    /// The small grey line over a group of pages in the sidebar; 通用 and 外观 stand alone at the top.
    var group: String? {
        switch self {
        case .general, .appearance: nil
        case .reading, .foreign, .listening: String(localized: "阅读与朗读")
        case .companion, .sync, .notes: String(localized: "连接")
        case .storage, .data, .update: String(localized: "数据与关于")
        }
    }

    /// One set: SF Symbols, all line drawings of one weight.
    var symbol: String {
        switch self {
        case .general: "slider.horizontal.3"
        case .appearance: "sun.max"
        case .reading: "book"
        case .foreign: "character.book.closed"
        case .listening: "headphones"
        case .companion: "text.bubble"
        case .sync: "arrow.triangle.2.circlepath"
        case .notes: "square.and.arrow.up"
        case .storage: "internaldrive"
        case .data: "shippingbox"
        case .update: "info.circle"
        }
    }
}

#if os(macOS)
/// The page's name and what it's for, fixed over the page as it scrolls.
struct SettingsPageHeader: View {
    let page: SettingsPage

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(page.title).font(.yy(size: 16, weight: .semibold)).foregroundStyle(YYColor.t1)
            Text(page.summary).font(.yy(size: 11.5)).foregroundStyle(YYColor.t2).lineLimit(1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // Level with the first row of the sidebar, clear of the title bar.
        .padding(.leading, 24 + 16)
        .padding(.trailing, 24)
        .padding(.top, 4)
        .padding(.bottom, 6)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }
}

/// Every action but a page's main one: words in ink, red for one that throws something away,
/// faint while it can't be used.
struct SettingsActionStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        Label(configuration: configuration)
    }

    private struct Label: View {
        let configuration: Configuration
        @Environment(\.isEnabled) private var enabled

        var body: some View {
            configuration.label
                .font(.yy(size: 12.5, weight: .medium))
                .foregroundStyle(configuration.role == .destructive ? YYColor.danger : YYColor.t1)
                .opacity(!enabled ? 0.35 : configuration.isPressed ? 0.55 : 1)
                .contentShape(.rect)
        }
    }
}

/// ⌘Z in 设置 takes back the last change in 最近改过, unless a text field is being typed in, where
/// it undoes the typing as usual.
private struct SettingsUndoKey: NSViewRepresentable {
    final class Host: NSView {
        nonisolated(unsafe) private var monitor: Any?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            monitor.map(NSEvent.removeMonitor)
            monitor = nil
            guard window != nil else { return }
            monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
                guard event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
                      event.charactersIgnoringModifiers == "z" else { return event }
                let handled = MainActor.assumeIsolated { () -> Bool in
                    guard let window = self?.window, event.window === window,
                          !(window.firstResponder is NSText),
                          let last = SettingsLog.shared.entries.first(where: { $0.undo != nil && $0.undone != true }) else { return false }
                    SettingsLog.shared.undo(last)
                    return true
                }
                return handled ? nil : event
            }
        }

        deinit { monitor.map(NSEvent.removeMonitor) }
    }

    func makeNSView(context: Context) -> Host { Host() }
    func updateNSView(_ host: Host, context: Context) {}
}

/// The title bar is drawn over the three columns, see-through, with no line under it and no title
/// in it (the sidebar says which page this is). SwiftUI puts its own title and toolbar back
/// whenever it likes, and a toolbar is taller than the title bar, so every window update takes
/// them off again.
private struct SettingsTitlebar: NSViewRepresentable {
    var onWindow: ((NSWindow) -> Void)?

    final class Host: NSView {
        var onWindow: ((NSWindow) -> Void)?
        nonisolated(unsafe) private var observer: NSObjectProtocol?

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observer.map(NotificationCenter.default.removeObserver)
            guard let window else { return }
            onWindow?(window)
            tidy(window)
            observer = NotificationCenter.default.addObserver(forName: NSWindow.didUpdateNotification, object: window, queue: .main) { [weak self] note in
                guard let window = note.object as? NSWindow else { return }
                MainActor.assumeIsolated { self?.tidy(window) }
            }
        }

        deinit { observer.map(NotificationCenter.default.removeObserver) }

        func tidy(_ window: NSWindow) {
            if window.toolbar != nil { window.toolbar = nil }
            if window.titleVisibility != .hidden { window.titleVisibility = .hidden }
            if !window.titlebarAppearsTransparent { window.titlebarAppearsTransparent = true }
            if !window.styleMask.contains(.fullSizeContentView) { window.styleMask.insert(.fullSizeContentView) }
            if window.titlebarSeparatorStyle != .none { window.titlebarSeparatorStyle = .none }
            window.backgroundColor = SettingsSidebar.header
        }
    }

    func makeNSView(context: Context) -> Host {
        let host = Host()
        host.onWindow = onWindow
        return host
    }

    func updateNSView(_ host: Host, context: Context) {
        host.onWindow = onWindow
        if let window = host.window {
            onWindow?(window)
            host.tidy(window)
        }
    }
}

// MARK: - Traffic lights

extension View {
    /// Puts the window's close, minimise and zoom buttons where the design has them: the
    /// close button's middle `close` from the window's top-left corner, the others after it at the
    /// system's spacing. AppKit lays them out again whenever it likes, so they are
    /// put back on every window update.
    func trafficLights(_ close: CGPoint, hidden: Bool = false) -> some View {
        background(TrafficLights(close: close, hidden: hidden))
    }
}

private struct TrafficLights: NSViewRepresentable {
    let close: CGPoint
    /// Faded away, as the shelf's are while its sidebar is put away; they fade back with it.
    var hidden = false

    final class Host: NSView {
        var close = CGPoint.zero
        var lightsHidden = false
        private var shownHidden: Bool?
        nonisolated(unsafe) private var observers: [NSObjectProtocol] = []

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.forEach(NotificationCenter.default.removeObserver)
            observers = []
            guard let window else { return }
            for name in [NSWindow.didUpdateNotification, NSWindow.didResizeNotification, NSWindow.didExitFullScreenNotification] {
                observers.append(NotificationCenter.default.addObserver(forName: name, object: window, queue: .main) { [weak self] note in
                    guard let window = note.object as? NSWindow else { return }
                    MainActor.assumeIsolated { self?.place(in: window) }
                })
            }
            place(in: window)
        }

        deinit { observers.forEach(NotificationCenter.default.removeObserver) }

        func place(in window: NSWindow) {
            guard !window.styleMask.contains(.fullScreen) else { return }
            let buttons = [NSWindow.ButtonType.closeButton, .miniaturizeButton, .zoomButton].compactMap { window.standardWindowButton($0) }
            guard buttons.count == 3, let bar = buttons[0].superview else { return }
            // A button below the title bar shows but can't be clicked: such a window needs a
            // toolbar, which makes the bar 52 high (SwiftUI owns it, so it isn't added here).
            guard bar.frame.height >= close.y + buttons[0].frame.height / 2 else { return }
            let step = buttons[1].frame.minX - buttons[0].frame.minX
            // The middle of the close button, in the bar's own coordinates.
            let top = window.frame.height - close.y
            let middle = bar.convert(NSPoint(x: close.x, y: top), from: nil)
            for (i, b) in buttons.enumerated() {
                let origin = NSPoint(x: (middle.x + CGFloat(i) * step - b.frame.width / 2).rounded(),
                                     y: (middle.y - b.frame.height / 2).rounded())
                if b.frame.origin != origin { b.setFrameOrigin(origin) }
            }
            fade(buttons)
        }

        /// The first time they just are as they should; after that they fade with the sidebar.
        private func fade(_ buttons: [NSButton]) {
            guard lightsHidden != shownHidden else { return }
            let first = shownHidden == nil
            shownHidden = lightsHidden
            let hide = lightsHidden
            if !hide { buttons.forEach { $0.isHidden = false } }
            if first {
                buttons.forEach { $0.alphaValue = hide ? 0 : 1; $0.isHidden = hide }
                return
            }
            NSAnimationContext.runAnimationGroup { context in
                context.duration = 0.3
                context.timingFunction = CAMediaTimingFunction(name: .easeOut)
                buttons.forEach { $0.animator().alphaValue = hide ? 0 : 1 }
            } completionHandler: {
                MainActor.assumeIsolated {
                    if self.shownHidden == true { buttons.forEach { $0.isHidden = true } }
                }
            }
        }
    }

    func makeNSView(context: Context) -> Host {
        let host = Host()
        host.close = close
        host.lightsHidden = hidden
        return host
    }

    func updateNSView(_ host: Host, context: Context) {
        host.close = close
        host.lightsHidden = hidden
        host.window.map(host.place)
    }
}

// MARK: - Pieces

/// A white card of rows, lines only between them.
struct SettingsCard<Content: View>: View {
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Group(subviews: content) { rows in
                ForEach(Array(rows.enumerated()), id: \.offset) { i, row in
                    if i > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 16) }
                    row
                }
            }
        }
        .background(YYColor.card, in: .rect(cornerRadius: 12))
        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(scheme == .dark ? Color(yy: YYRGBA(255, 255, 255, 0.06)) : YYColor.line, lineWidth: 0.5))
        .shadow(color: scheme == .dark ? .clear : Color(yy: YYRGBA(51, 46, 41, 0.04)), radius: 1, y: 1)
    }
}

/// A row: what it is and a line under it on the left, the control on the right.
struct SettingsRow<Trailing: View>: View {
    let title: String
    var detail: String?
    /// A list of switches, 38 high.
    var compact = false
    @ViewBuilder let trailing: Trailing

    init(_ title: String, detail: String? = nil, compact: Bool = false, @ViewBuilder trailing: () -> Trailing) {
        self.title = title
        self.detail = detail
        self.compact = compact
        self.trailing = trailing()
    }

    var body: some View {
        // 12.5 over 10.5, at least 46 high.
        HStack(alignment: .center, spacing: 18) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.t1)
                if let detail {
                    Text(detail).font(.yy(size: 10.5)).lineSpacing(2).foregroundStyle(YYColor.t3)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 12)
            trailing
                // A switch on the right says what it switches to VoiceOver: the row's title.
                .environment(\.settingsRowTitle, title)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, compact ? 8 : 10)
        .frame(minHeight: compact ? 38 : 46)
    }
}

extension SettingsRow where Trailing == EmptyView {
    init(_ title: String, detail: String? = nil) {
        self.init(title, detail: detail) { EmptyView() }
    }
}

/// A switch in the theme colour.
extension EnvironmentValues {
    /// The title of the settings row a control sits in.
    @Entry var settingsRowTitle = ""
}

struct SettingsSwitch: View {
    @Binding var isOn: Bool
    @Environment(\.settingsRowTitle) private var title

    var body: some View {
        Toggle(title, isOn: $isOn)
            .labelsHidden()
            .toggleStyle(.switch)
            // `.sw2` is 30 × 18: the small switch.
            .controlSize(.small)
            .tint(YYTint.standard.fill)
    }
}

/// The small grey heading over a card.
struct SettingsHeading: View {
    let text: String
    /// A quiet line at the right end, as 点一个，进去听它的声音.
    var note: String? = nil
    init(_ text: String, note: String? = nil) { self.text = text; self.note = note }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(text)
                .font(.yy(size: 11.5, weight: .semibold))
                .foregroundStyle(YYColor.t2)
            if let note {
                Spacer(minLength: 8)
                Text(note)
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
        }
        .padding(.leading, 16)
        .padding(.trailing, note == nil ? 0 : 12)
        .padding(.top, 6)
        .padding(.bottom, -6)
    }
}

// MARK: - 通用

private struct GeneralSettings: View {
    @AppStorage("general.reopenLast") private var reopenLast = false
    @AppStorage("general.keepAwake") private var keepAwake = true
    @State private var langChoice = AppLanguage.chosen
    @State private var langLater = false
    @State private var langMenuOpen = false
    @State private var langTriggerBottom: CGFloat = 44

    private var canRelaunch: Bool {
        let library = SettingsLog.shared.library
        return library?.nowPlaying?.isPlaying != true && library?.audiobooks.job == nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsCard {
                MacLanguageCard(
                    choice: $langChoice,
                    later: $langLater,
                    menuOpen: $langMenuOpen,
                    onTriggerBottomChange: { bottom in
                        langTriggerBottom = bottom
                    }
                )
                SettingsRow(String(localized: "启动时继续上次阅读"), detail: String(localized: "关闭则进入书架")) {
                    SettingsSwitch(isOn: $reopenLast)
                }
                LoginItemRow()
                SettingsRow(String(localized: "朗读时防止系统休眠"), detail: String(localized: "合上屏幕仍会暂停朗读")) {
                    SettingsSwitch(isOn: $keepAwake)
                }
            }
            SettingsHeading(String(localized: "导入"))
            SettingsCard {
                SettingsRow(String(localized: "拖入书架即可导入"), detail: String(localized: "文件会复制到书库，移动或删除原文件不受影响")) {
                    Text(verbatim: "EPUB PDF MOBI AZW3 TXT").font(.yy(size: 11, design: .monospaced)).foregroundStyle(YYColor.t3)
                }
            }
            Spacer(minLength: 0)
        }
        .frame(maxWidth: .infinity, minHeight: 520, alignment: .top)
        .coordinateSpace(name: "generalSettings")
        .overlay(alignment: .topTrailing) {
            if langMenuOpen {
                ZStack(alignment: .topTrailing) {
                    Color.black.opacity(0.001)
                        .frame(maxWidth: .infinity, maxHeight: .infinity)
                        .contentShape(.rect)
                        .onTapGesture {
                            withAnimation(YYMotion.quick) { langMenuOpen = false }
                        }
                    Button("") {
                        withAnimation(YYMotion.quick) { langMenuOpen = false }
                    }
                    .buttonStyle(.plain)
                    .frame(width: 0, height: 0)
                    .opacity(0)
                    .keyboardShortcut(.cancelAction)

                    LanguagePaperDropdownMenu(selected: langChoice) { picked in
                        withAnimation(YYMotion.quick) { langMenuOpen = false }
                        pickLanguage(picked)
                    }
                    .padding(.top, langTriggerBottom + 6)
                    .padding(.trailing, 16)
                    .transition(.opacity.combined(with: .scale(scale: 0.95, anchor: .topTrailing)))
                }
            }
        }
        .onDisappear { langMenuOpen = false }
    }

    private func pickLanguage(_ l: AppLanguage) {
        guard langChoice != l else { return }
        let prev = langChoice
        guard l.effective != AppLanguage.running else {
            langLater = false
            langChoice = l
            AppLanguage.choose(l)
            SettingsLog.shared.flash = .init(date: .now, title: String(localized: "语言"), to: MacLanguageCard.name(l), from: MacLanguageCard.name(prev))
            return
        }
        if canRelaunch {
            // Keep langChoice unchanged until confirmation so cancelling never flickers and InkSweep snapshots cleanly.
            NotificationCenter.default.post(name: .confirmLanguageRelaunch, object: l)
        } else {
            langLater = true
            langChoice = l
            AppLanguage.choose(l)
            SettingsLog.shared.flash = .init(date: .now, title: String(localized: "语言"), to: MacLanguageCard.name(l), from: MacLanguageCard.name(prev))
        }
    }
}

// MARK: - 外观

struct AppearanceSettings: View {
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    @AppStorage("shelf.coverSize") private var coverSize = 100.0
    @AppStorage("shelf.showProgress") private var showProgress = true
    @AppStorage(LiquidGlass.key) private var liquid = false

    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @State private var hoverMode: AppAppearance?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsCard {
                // What 界面 covers is the page header's line; the card only chooses.
                HStack(alignment: .center) {
                    Text("界面").font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.t1)
                    Spacer()
                    HStack(spacing: 10) {
                        ForEach(AppAppearance.allCases) { m in modeChoice(m) }
                    }
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
            }
            SettingsCard {
                HStack(alignment: .center) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("主题色").font(.yy(size: 13, weight: .medium)).foregroundStyle(YYColor.t1)
                        Text("用于按钮、开关与进度标记").font(.yy(size: 11.5)).foregroundStyle(YYColor.t2)
                    }
                    Spacer()
                    HStack(spacing: 14) { ForEach(YYTint.all) { t in tintChoice(t) } }
                }
                .padding(16)
            }
            SettingsCard {
                SettingsRow(String(localized: "书架封面大小"), detail: String(localized: "每行本数随窗口宽度自动排布")) {
                    HStack(spacing: 10) {
                        RoundedRectangle(cornerRadius: 1.5).fill(YYColor.tint).frame(width: 8, height: 11)
                        Slider(value: $coverSize, in: 80 ... 130, step: 10)
                            .tint(tint.fill)
                            .frame(width: 180)
                        RoundedRectangle(cornerRadius: 2).fill(YYColor.tint).frame(width: 11, height: 16)
                    }
                }
                SettingsRow(String(localized: "封面下显示进度"), detail: String(localized: "关闭后仅显示书名")) {
                    SettingsSwitch(isOn: $showProgress)
                }
            }
            if LiquidGlass.supported {
                SettingsCard {
                    SettingsRow(String(localized: "液态玻璃效果"), detail: String(localized: "浮动工具栏与提示条使用系统液态玻璃，关闭则为毛玻璃")) {
                        SettingsSwitch(isOn: $liquid)
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .onChange(of: mode) { AppAppearance.apply() }
    }

    private func modeChoice(_ m: AppAppearance) -> some View {
        let on = mode == m
        let hovered = !on && hoverMode == m
        let strokeColor: Color = on ? tint.accent : (hovered ? YYColor.line : .clear)
        let strokeWidth: CGFloat = on ? 1.75 : 1
        return Button { mode = m } label: {
            VStack(spacing: 6) {
                ModeThumbnail(mode: m, accent: tint.accent)
                    .frame(width: 84, height: 54)
                    .padding(2.5)
                    .overlay(RoundedRectangle(cornerRadius: 9.5, style: .continuous).strokeBorder(strokeColor, lineWidth: strokeWidth))
                    .animation(YYMotion.quick, value: on)
                    .animation(YYMotion.quick, value: hovered)
                Text(m.title)
                    .font(.yy(size: 11.5, weight: on ? .semibold : .regular))
                    .foregroundStyle(on || hovered ? YYColor.t1 : YYColor.t2)
                    // 跟随系统 on one line, not broken in two under its picture.
                    .fixedSize()
            }
            .offset(y: hovered ? -1.5 : 0)
            .animation(YYMotion.quick, value: hovered)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
        .onHover { hoverMode = $0 ? m : (hoverMode == m ? nil : hoverMode) }
    }

    private func tintChoice(_ t: YYTint) -> some View {
        let on = tintID == t.id
        return Button { tintID = t.id } label: {
            VStack(spacing: 6) {
                Circle().fill(Color(t.accent.resolve(in: EnvironmentValues().with(\.colorScheme, .light))))
                    .frame(width: 26, height: 26).padding(3)
                    .overlay(Circle().strokeBorder(on ? t.accent : .clear, lineWidth: 2))
                    .animation(YYMotion.quick, value: on)
                Text(t.name).font(.yy(size: 11.5, weight: on ? .semibold : .regular))
                    .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
            }
        }.buttonStyle(.plain)
    }
}

private extension EnvironmentValues {
    func with<V>(_ path: WritableKeyPath<EnvironmentValues, V>, _ value: V) -> EnvironmentValues {
        var copy = self
        copy[keyPath: path] = value
        return copy
    }
}

/// 三帧缩略书页: miniature warm/night book leaves for 浅色, 深色, and a diagonal split for 跟随系统.
private struct ModeThumbnail: View {
    let mode: AppAppearance
    let accent: Color
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        ZStack {
            switch mode {
            case .light:
                fullLeaf(dark: false)
            case .dark:
                fullLeaf(dark: true)
            case .system:
                splitLeaf
            }
        }
        .clipShape(.rect(cornerRadius: 7, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 7, style: .continuous)
                .strokeBorder(scheme == .dark || mode == .dark ? Color.white.opacity(0.12) : Color(yy: YYRGBA(42, 43, 46, 0.11)), lineWidth: 0.5)
        )
    }

    private func fullLeaf(dark: Bool) -> some View {
        let bg = dark ? Color(yy: YYRGBA(29, 30, 33)) : Color(yy: YYRGBA(246, 243, 235))
        let titleInk = dark ? Color(yy: YYRGBA(237, 237, 238, 0.84)) : Color(yy: YYRGBA(42, 43, 46, 0.76))
        let rule = dark ? Color(yy: YYRGBA(154, 152, 149, 0.44)) : Color(yy: YYRGBA(138, 134, 130, 0.38))

        return ZStack(alignment: .topLeading) {
            bg
            LinearGradient(
                colors: [.black.opacity(dark ? 0.30 : 0.05), .clear],
                startPoint: .leading,
                endPoint: .trailing
            )
            .frame(width: 4.5)
            .frame(maxHeight: .infinity)

            VStack(alignment: .leading, spacing: 5.5) {
                Capsule().fill(titleInk).frame(width: 24, height: 3)
                VStack(alignment: .leading, spacing: 3.5) {
                    Capsule().fill(rule).frame(width: 50, height: 2)
                    Capsule().fill(rule).frame(width: 46, height: 2)
                    Capsule().fill(rule).frame(width: 34, height: 2)
                }
            }
            .padding(.leading, 12)
            .padding(.top, 12)

            Capsule()
                .fill(rule.opacity(0.7))
                .frame(width: 10, height: 1.5)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                .padding(.leading, 12)
                .padding(.bottom, 8)

            UnevenRoundedRectangle(bottomLeadingRadius: 1.2, bottomTrailingRadius: 1.2)
                .fill(accent.opacity(dark ? 0.88 : 0.78))
                .frame(width: 3.5, height: 8.5)
                .frame(maxWidth: .infinity, alignment: .topTrailing)
                .padding(.trailing, 8)
        }
    }

    private var splitLeaf: some View {
        ZStack {
            fullLeaf(dark: false)
            fullLeaf(dark: true)
                .mask(GeometryReader { g in
                    Path { p in
                        p.move(to: CGPoint(x: g.size.width * 0.60, y: 0))
                        p.addLine(to: CGPoint(x: g.size.width, y: 0))
                        p.addLine(to: CGPoint(x: g.size.width, y: g.size.height))
                        p.addLine(to: CGPoint(x: g.size.width * 0.40, y: g.size.height))
                        p.closeSubpath()
                    }
                })
            GeometryReader { g in
                Path { p in
                    p.move(to: CGPoint(x: g.size.width * 0.60, y: 0))
                    p.addLine(to: CGPoint(x: g.size.width * 0.40, y: g.size.height))
                }
                .stroke(Color.white.opacity(0.14), lineWidth: 0.5)
            }
        }
    }
}

// MARK: - 阅读

/// The settings every book uses unless it keeps its own (只用于这本书 under Aa).
private struct ReadingSettings: View {
    let library: Library
    @State private var settings = ReaderSettings.standard
    @AppStorage("reader.twoColumns") private var twoColumns = true
    @AppStorage(ImmersivePrefs.lineKey) private var immersiveLine = true
    @AppStorage(ImmersivePrefs.marksKey) private var immersiveMarks = false
    @AppStorage(ImmersivePrefs.hintsKey) private var immersiveHints = true
    @AppStorage(ImmersivePrefs.alwaysKey) private var immersiveAlways = false
    @AppStorage(ImmersivePrefs.stillKey) private var immersiveStill = true
    @State private var row = FontRow.ids()
    @State private var fontBook = false

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsCard {
                // The papers and fonts as tiles, so each is seen before it's chosen.
                tiles(String(localized: "纸色"), columns: YYPaper.all.count) {
                    ForEach(YYPaper.all) { paper in
                        SettingsPaperTile(paper: paper, texture: paper.hasNoiseTexture ? .grain : .none, name: paper.name, selected: settings.paperChoice == paper.id) {
                            change { $0.paper = Paper(id: paper.id) ?? .plain; $0.customPaper = nil }
                        }
                    }
                    // 我的纸 are made in the book's Aa.
                    ForEach(Papers.shared.list, id: \.id) { mine in
                        SettingsPaperTile(paper: YYPaper(custom: mine), texture: mine.texture, name: mine.name, selected: settings.paperChoice == mine.id) {
                            let preset = paperColors(paper: mine).preset
                            change { $0.customPaper = mine.id; $0.paper = preset }
                        }
                    }
                }
                // 字体: the built-in three, the faces picked before, and 更多字体 for the rest.
                tiles(String(localized: "字体"), columns: 5) {
                    ForEach(FontShelf.builtIn + row, id: \.self) { id in
                        FontTile(id: id, selected: FontShelf.id(of: settings) == id, pick: { useFace(id) },
                                 remove: FontShelf.builtIn.contains(id) ? nil : { removeFromRow(id) })
                    }
                    MoreFontsTile { fontBook = true }
                }
                .animation(YYMotion.layout, value: row)
                #if DEBUG
                // `-debugFontBook 1` opens 字样册.
                .task { if UserDefaults.standard.bool(forKey: "debugFontBook") { try? await Task.sleep(for: .seconds(0.6)); fontBook = true } }
                #endif
                .sheet(isPresented: $fontBook) {
                    FontBook(choose: { id in
                        FontRow.add(id)
                        row = FontRow.ids()
                        useFace(id)
                    }, current: FontShelf.id(of: settings))
                    .paperWindow()
                }
                SettingsRow(String(localized: "字号")) {
                    HStack(spacing: 10) {
                        Slider(value: Binding(get: { settings.fontSize }, set: { v in change { $0.fontSize = v.rounded() } }),
                               in: TypesetCard.sizes)
                            .frame(width: 180)
                        Text("\(Int(settings.fontSize))").monospacedDigit().foregroundStyle(YYColor.t2).frame(width: 22)
                    }
                }
                SettingsRow(String(localized: "行距")) {
                    YYSegments(items: [(Double?.some(1.6), String(localized: "紧")), (nil, String(localized: "适中")), (.some(2.2), String(localized: "松"))],
                               selection: Binding(get: { settings.lineHeight }, set: { v in change { $0.lineHeight = v } }), small: true)
                        .frame(minWidth: 160).fixedSize()
                }
            }
            SettingsCard {
                SettingsRow(String(localized: "翻页方式")) {
                    YYSegments(items: [(LayoutMode.paginated, String(localized: "左右翻页")), (.scrolled, String(localized: "上下滚动"))],
                               selection: Binding(get: { settings.mode }, set: { v in change { $0.mode = v } }), small: true)
                        .frame(minWidth: 160).fixedSize()
                }
                SettingsRow(String(localized: "宽窗口下双栏显示"), detail: String(localized: "左右并排显示两页")) {
                    SettingsSwitch(isOn: $twoColumns)
                }
                SettingsRow(String(localized: "两端对齐")) {
                    SettingsSwitch(isOn: Binding(get: { settings.justify ?? true }, set: { v in change { $0.justify = v } }))
                }
                SettingsRow(String(localized: "首行缩进两字")) {
                    SettingsSwitch(isOn: Binding(get: { settings.indent ?? true }, set: { v in change { $0.indent = v } }))
                }
                SettingsRow(String(localized: "中文标点挤压"), detail: String(localized: "收紧连续标点的留白")) {
                    SettingsSwitch(isOn: Binding(get: { settings.compress }, set: { v in change { $0.compress = v } }))
                }
            }
            SettingsHeading(String(localized: "沉浸阅读"))
            SettingsCard {
                SettingsRow(String(localized: "进入与退出"), detail: String(localized: "全屏隐藏界面，指针移至底部唤出控制栏")) {
                    Text("⌃⌘F 或 Esc").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                }
                SettingsRow(String(localized: "打开书籍时自动进入")) { SettingsSwitch(isOn: $immersiveAlways) }
                SettingsRow(String(localized: "底部保留进度细线")) { SettingsSwitch(isOn: $immersiveLine) }
                SettingsRow(String(localized: "隐藏划线与想法"), detail: String(localized: "退出沉浸后恢复显示")) { SettingsSwitch(isOn: $immersiveMarks) }
                SettingsRow(String(localized: "隐藏书伴提示与难词注释")) { SettingsSwitch(isOn: $immersiveHints) }
                SettingsRow(String(localized: "关闭翻页动画"), detail: String(localized: "直接切换整页，无滑动过渡")) { SettingsSwitch(isOn: $immersiveStill) }
                SettingsRow(String(localized: "专注模式联动"), detail: String(localized: "在系统「阅读」专注模式中关联语阅")) {
                    Button("专注模式设置…") {
                        if let url = URL(string: "x-apple.systempreferences:com.apple.Focus-Settings.extension") { NSWorkspace.shared.open(url) }
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .onAppear { settings = (try? library.core.readerSettings()) ?? .standard }
        // 撤销 in 最近改过 writes the old value back.
        .onReceive(NotificationCenter.default.publisher(for: .readerSettingsChanged)) { _ in
            settings = (try? library.core.readerSettings()) ?? settings
        }
    }

    /// A titled row of tiles in the card: what it is on top, the choices filling the width.
    private func tiles<Content: View>(_ title: String, detail: String? = nil, columns: Int, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.t1)
                if let detail { Text(detail).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3) }
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 8), count: columns), spacing: 8) { content() }
        }
        .padding(16)
    }

    /// A face chosen: in the settings now, downloading if it must (the page reads in the built-in
    /// face of its kind until it's here).
    private func useFace(_ id: String) {
        change { FontShelf.shared.apply(id, to: &$0) }
        FontShelf.shared.ensure(id)
    }

    private func removeFromRow(_ id: String) {
        FontRow.remove(id)
        row = FontRow.ids()
        if FontShelf.id(of: settings) == id {
            let fallback = switch FontShelf.shared.kind(id) {
            case .song, .fang: "serif"
            case .kai, .hand: "kai"
            case .hei: "system"
            }
            useFace(fallback)
        }
    }

    private func change(_ edit: (inout ReaderSettings) -> Void) {
        var new = settings
        edit(&new)
        guard new != settings else { return }
        SettingsLog.shared.reader(from: settings, to: new)
        settings = new
        try? library.core.setReaderSettings(settings: new)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }
}

// MARK: - 朗读: MacVoicePicker.swift

// MARK: - 书伴

// MARK: - 数据

/// A line under a card, on the cards' one left edge: what to know, never something to click.
struct SettingsNote: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.yy(size: 11)).lineSpacing(2).foregroundStyle(YYColor.t3)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 16)
    }
}

/// 数据快照 and 导出阅读记录, as on iPhone: what goes in and how big, 生成快照,
/// 导入快照…, and the ones made before.
private struct DataSettings: View {
    let library: Library
    @State private var snapshots: Snapshots
    @State private var exporting = false
    @State private var importing: Importing?
    @State private var confirming: YYConfirm?

    struct Importing: Identifiable {
        let url: URL
        let info: SnapshotInfo
        var id: URL { url }
    }

    init(library: Library) {
        self.library = library
        _snapshots = State(initialValue: Snapshots(library: library))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsHeading(String(localized: "生成快照"))
            SettingsCard {
                part(String(localized: "进度和书签"), count: snapshots.counts.map { Snapshots.books($0.read) }, \.progress)
                part(String(localized: "笔记"), count: snapshots.counts.map { Snapshots.items($0.notes) }, \.notes)
                part(String(localized: "生词"), count: snapshots.counts.map { Snapshots.words($0.words) }, \.words)
                part(String(localized: "设置和替换规则"), count: nil, \.settings)
                part(String(localized: "书文件"), count: snapshots.counts.map { Snapshots.size($0.fileBytes) }, \.files)
                HStack(spacing: 12) {
                    (Text(snapshots.parts.files ? String(localized: "含书文件 · 约 ") : String(localized: "不含书文件 · 约 "))
                        + Text(snapshots.counts == nil ? "…" : StorageSettings.size(snapshots.estimate)).fontWeight(.semibold).foregroundStyle(YYColor.t1))
                        .font(.yy(size: 12).monospacedDigit()).foregroundStyle(YYColor.t3)
                    if let problem = snapshots.problem {
                        Text(problem).font(.yy(size: 11.5)).foregroundStyle(YYColor.danger).lineLimit(2)
                    }
                    Spacer()
                    Button("导入快照…") { pick() }
                        .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                    Button {
                        Task { await snapshots.make() }
                    } label: {
                        if snapshots.making { ProgressView().controlSize(.small) } else { Text("生成快照") }
                    }
                    .buttonStyle(YYButtonStyle(kind: .main, size: .small))
                    .disabled(snapshots.making)
                }
                .padding(.horizontal, 16)
                .padding(.vertical, 11)
            }
            SettingsCard {
                SettingsRow(String(localized: "导出阅读记录"), detail: String(localized: "导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph")) {
                    Button("导出…") { exporting = true }
                        .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                }
            }
            if !snapshots.saved.isEmpty {
                SettingsHeading(String(localized: "历史快照"))
                SettingsCard {
                    ForEach(snapshots.saved) { s in
                        SettingsRow(Snapshots.when(s.about.madeAt),
                                    detail: ([Snapshots.from(s.about)] + (s.about.parts.files ? [String(localized: "含书文件")] : []) + [Snapshots.size(s.about.bytes)]).joined(separator: " · ")) {
                            HStack(spacing: 8) {
                                Button("导入") { open(s.url) }
                                ShareLink(item: s.url) { Image(systemName: "square.and.arrow.up").font(.yy(size: 12)) }
                                    .help("分享")
                            }
                        }
                        .contextMenu {
                            Button("在访达中显示") { NSWorkspace.shared.activateFileViewerSelecting([s.url]) }
                            Divider()
                            Button("删除…", role: .destructive) {
                                confirming = YYConfirm(title: String(localized: "删除这份快照？"),
                                                       message: String(localized: "\(Snapshots.when(s.about.madeAt)) 生成的快照文件会删掉。书库里的东西不受影响。"),
                                                       button: String(localized: "删除")) { snapshots.delete(s) }
                            }
                        }
                    }
                }
            }
            SettingsNote(String(localized: "快照为单个 .yuyue 文件，在 Mac、iPhone 与 Android 上打开即可恢复书库。"))
            Spacer(minLength: 0)
        }
        .task { await snapshots.load() }
        .onReceive(NotificationCenter.default.publisher(for: .snapshotsChanged)) { note in
            if note.object as AnyObject? !== snapshots { Task { await snapshots.load() } }
        }
        // The sheets keep their own buttons, not 设置's words.
        .sheet(isPresented: $exporting) { ReadingExportSheet(library: library).buttonStyle(.automatic) }
        .sheet(item: $importing) { SnapshotImportSheet(snapshots: snapshots, url: $0.url, info: $0.info).buttonStyle(.automatic) }
        .yyConfirm($confirming)
    }

    private func part(_ title: String, count: String?, _ key: WritableKeyPath<SnapshotParts, Bool>) -> some View {
        SettingsRow(title, detail: count) {
            SettingsSwitch(isOn: Binding { snapshots.parts[keyPath: key] } set: { snapshots.parts[keyPath: key] = $0 })
        }
    }

    private func pick() {
        let panel = NSOpenPanel()
        panel.allowedContentTypes = [.yuyueSnapshot]
        panel.prompt = String(localized: "导入")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        open(url)
    }

    private func open(_ url: URL) {
        do {
            let (local, info) = try snapshots.preview(url)
            importing = Importing(url: local, info: info)
        } catch {
            snapshots.problem = Library.message(for: error)
        }
    }
}

// MARK: - 存储

private struct StorageSettings: View {
    let library: Library
    @State private var usage: StorageUsage?
    @State private var cleaning = false
    @State private var confirming: YYConfirm?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsCard {
                VStack(alignment: .leading, spacing: 12) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("已用空间").font(.yy(size: 13)).foregroundStyle(YYColor.t2)
                        Text(usage.map { Self.size($0.total) } ?? "…")
                            .font(.yy(size: 22, weight: .bold)).foregroundStyle(YYColor.t1)
                        Spacer()
                        if let free = usage?.free {
                            Text("本机可用 \(Self.size(free))").font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
                        }
                    }
                    if let usage { bar(usage) }
                }
                .padding(16)
                SettingsRow(String(localized: "最近删除 · \(usage?.deleted.count ?? 0) 本（\(Self.size(usage?.deletedBytes ?? 0))）"),
                            detail: String(localized: "保留 30 天以便恢复，到期自动清理")) {
                    Button("立即清空", role: .destructive) {
                        let n = usage?.deleted.count ?? 0
                        confirming = YYConfirm(title: String(localized: "清空最近删除的 \(n) 本书？"),
                                               message: String(localized: "清空后文件将彻底删除，无法恢复。"),
                                               button: String(localized: "立即清空")) { purge() }
                    }
                        .disabled((usage?.deleted.isEmpty ?? true) || cleaning)
                }
                if modelBytes > 0 {
                    SettingsRow(String(localized: "语音对齐模型"), detail: String(localized: "用于有声书句级对齐；移除后下次合成时重新下载（约 270 MB）")) {
                        HStack(spacing: 10) {
                            Text(Self.size(modelBytes))
                                .font(.yy(size: 12)).monospacedDigit().foregroundStyle(YYColor.t2)
                            Button("移除", role: .destructive) {
                                confirming = YYConfirm(title: String(localized: "移除语音对齐模型？"),
                                                       message: String(localized: "已合成的有声书不受影响；下次合成时将重新下载（约 270 MB）。"),
                                                       button: String(localized: "移除")) {
                                    try? FileManager.default.removeItem(at: Aligner.folder(library.dataDir))
                                    Task { await measure() }
                                }
                            }
                            .disabled(library.audiobooks.job != nil)
                        }
                    }
                }
                SettingsRow(String(localized: "书伴问答缓存"), detail: String(localized: "同章节重复提问直接读取本地缓存，不重复调用模型")) {
                    HStack(spacing: 10) {
                        Text(Self.size(companionBytes))
                            .font(.yy(size: 12)).monospacedDigit().foregroundStyle(YYColor.t2)
                        Button("清理", role: .destructive) {
                            confirming = YYConfirm(title: String(localized: "清理书伴问答缓存？"),
                                                   message: String(localized: "清理后，相同问题或前情回顾将重新请求模型。"),
                                                   button: String(localized: "清理")) {
                                try? FileManager.default.removeItem(at: companionDir)
                                companionBytes = Self.bytes(in: companionDir)
                            }
                        }
                        .disabled(companionBytes == 0)
                    }
                }
                KeepHeardRow()
            }
            if let usage, !usage.books.isEmpty {
                SettingsHeading(String(localized: "各书占用空间"))
                SettingsCard {
                    ForEach(usage.books.prefix(12), id: \.id) { b in
                        SettingsRow(b.title) {
                            Text(Self.size(b.bytes)).monospacedDigit().foregroundStyle(YYColor.t2)
                        }
                    }
                }
            }
            Spacer(minLength: 0)
        }
        .task { await measure() }
        .onAppear { companionBytes = Self.bytes(in: companionDir) }
        .yyConfirm($confirming)
    }

    @State private var companionBytes: Int64 = 0
    /// The alignment model and the 有声书 recordings, told apart from 其他.
    @State private var modelBytes: Int64 = 0
    @State private var audioBytes: Int64 = 0

    /// Everything under `dir`, folders and all.
    nonisolated private static func tree(_ dir: URL) -> Int64 {
        guard let e = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey]) else { return 0 }
        var total: Int64 = 0
        for case let url as URL in e {
            let v = try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey])
            if v?.isRegularFile == true { total += Int64(v?.totalFileAllocatedSize ?? 0) }
        }
        return total
    }
    private var companionDir: URL { library.dataDir.appending(path: "companion", directoryHint: .isDirectory) }

    private static func bytes(in dir: URL) -> Int64 {
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        return files.reduce(0) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
    }

    private func bar(_ u: StorageUsage) -> some View {
        let parts: [(String, Int64, Color)] = [(String(localized: "书文件"), u.bookBytes, YYTint.standard.fill),
                                               (String(localized: "有声书录音"), audioBytes, YYTint.standard.fill.opacity(0.55)),
                                               (String(localized: "对齐模型"), modelBytes, YYColor.t3),
                                               (String(localized: "已删除"), u.deletedBytes, YYColor.danger.opacity(0.7)),
                                               (String(localized: "其他"), max(0, u.total - u.bookBytes - u.deletedBytes - audioBytes - modelBytes), YYColor.tint)]
            .filter { $0.1 > 0 || $0.0 == String(localized: "书文件") }
        return VStack(alignment: .leading, spacing: 8) {
            GeometryReader { g in
                HStack(spacing: 2) {
                    ForEach(parts.filter { $0.1 > 0 }, id: \.0) { p in
                        Rectangle().fill(p.2)
                            .frame(width: max(3, g.size.width * Double(p.1) / Double(max(1, u.total))))
                    }
                    Spacer(minLength: 0)
                }
            }
            .frame(height: 8)
            .clipShape(.capsule)
            HStack(spacing: 16) {
                ForEach(parts, id: \.0) { p in
                    HStack(spacing: 5) {
                        Circle().fill(p.2).frame(width: 7, height: 7)
                        Text("\(p.0) \(Self.size(p.1))").font(.yy(size: 11)).foregroundStyle(YYColor.t2)
                    }
                }
            }
        }
    }

    private func measure() async {
        let dir = library.dataDir
        let active = Dictionary(library.books.map { ($0.id, $0.title) }, uniquingKeysWith: { a, _ in a })
        usage = await StorageUsage.measure(dir: dir, active: active)
        let (model, audio) = await Self.extraBytes(dir: dir)
        modelBytes = model
        audioBytes = audio
    }

    @concurrent nonisolated private static func extraBytes(dir: URL) async -> (Int64, Int64) {
        (tree(Aligner.folder(dir)), tree(dir.appending(path: "audiobooks", directoryHint: .isDirectory)))
    }

    private func purge() {
        cleaning = true
        _ = try? library.core.purgeDeleted(olderThanMs: 0)
        Task {
            await measure()
            cleaning = false
        }
    }

    static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}

#endif

/// 界面 light · dark · following the system, kept in `appearance.mode`.
enum AppAppearance: String, CaseIterable, Identifiable {
    case light, dark, system

    var id: String { rawValue }
    var title: String {
        switch self {
        case .light: String(localized: "浅色")
        case .dark: String(localized: "深色")
        case .system: String(localized: "跟随系统")
        }
    }

    static let key = "appearance.mode"

    static var current: AppAppearance {
        UserDefaults.standard.string(forKey: key).flatMap(AppAppearance.init) ?? .system
    }

    #if os(macOS)
    /// Puts every window in the chosen appearance.
    static func apply() {
        switch current {
        case .light: NSApp.appearance = NSAppearance(named: .aqua)
        case .dark: NSApp.appearance = NSAppearance(named: .darkAqua)
        case .system: NSApp.appearance = nil
        }
    }
    #endif

    #if os(iOS)
    var scheme: ColorScheme? {
        switch self {
        case .light: .light
        case .dark: .dark
        case .system: nil
        }
    }
    #endif
}


/// Where the library's space goes.
struct StorageUsage: Sendable {
    struct Book: Sendable {
        let id: String
        let title: String
        let bytes: Int64
    }

    var total: Int64 = 0
    var free: Int64?
    var books: [Book] = []
    var deleted: [String] = []
    var deletedBytes: Int64 = 0
    var bookBytes: Int64 { books.reduce(0) { $0 + $1.bytes } }

    @concurrent static func measure(dir: URL, active: [String: String]) async -> StorageUsage {
        var u = StorageUsage()
        u.total = bytes(in: dir)
        let booksDir = dir.appending(path: "books", directoryHint: .isDirectory)
        let ids = (try? FileManager.default.contentsOfDirectory(atPath: booksDir.path(percentEncoded: false))) ?? []
        for id in ids where !id.hasPrefix(".") {
            let size = bytes(in: booksDir.appending(path: id, directoryHint: .isDirectory))
            if let title = active[id] {
                u.books.append(Book(id: id, title: title, bytes: size))
            } else {
                u.deleted.append(id)
                u.deletedBytes += size
            }
        }
        u.books.sort { $0.bytes > $1.bytes }
        u.free = (try? dir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?
            .volumeAvailableCapacityForImportantUsage
        return u
    }

    nonisolated private static func bytes(in dir: URL) -> Int64 {
        guard let e = FileManager.default.enumerator(at: dir, includingPropertiesForKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey]) else { return 0 }
        var sum: Int64 = 0
        for case let url as URL in e {
            let v = try? url.resourceValues(forKeys: [.totalFileAllocatedSizeKey, .isRegularFileKey])
            if v?.isRegularFile == true { sum += Int64(v?.totalFileAllocatedSize ?? 0) }
        }
        return sum
    }
}
