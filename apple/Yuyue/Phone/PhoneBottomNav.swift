#if os(iOS)
import SwiftUI
import UIKit

/// Every destination that can appear in the phone's bottom navigation bar.
/// Shares canonical raw IDs and normalization rules with Android (`BottomNav.kt`).
enum PhoneTab: String, CaseIterable, Identifiable, Hashable, Sendable {
    case shelf
    case notes
    case podcasts
    case audio
    case progress
    case search
    case me

    var id: String { rawValue }

    var title: String {
        switch self {
        case .shelf: String(localized: "书架")
        case .notes: String(localized: "笔记")
        case .podcasts: String(localized: "播客")
        case .audio: String(localized: "有声")
        case .progress: String(localized: "进度")
        case .search: String(localized: "搜索")
        case .me: String(localized: "我的")
        }
    }

    var subtitle: String {
        switch self {
        case .shelf: String(localized: "书、进度、播客、有声与合集")
        case .notes: String(localized: "划线、想法、回顾与生词")
        case .podcasts: String(localized: "订阅频道与单集收听")
        case .audio: String(localized: "有声书与听读同步")
        case .progress: String(localized: "在读、想读与读过记录")
        case .search: String(localized: "搜书、正文、笔记与问书伴")
        case .me: String(localized: "阅读统计与全部设置")
        }
    }

    /// Custom asset icon name when available (nil falls back to `sfSymbol`).
    var assetLine: String? {
        switch self {
        case .shelf: "tab-shelf-line"
        case .notes: "tab-notes"
        case .podcasts: "radio"
        case .search: "tab-search"
        case .me: "tab-me"
        case .audio, .progress: nil
        }
    }

    var assetFill: String? {
        switch self {
        case .shelf: "tab-shelf"
        case .notes: "tab-notes-fill"
        case .podcasts: "radio-fill"
        case .search: "tab-search-fill"
        case .me: "tab-me-fill"
        case .audio, .progress: nil
        }
    }

    var sfSymbol: String {
        switch self {
        case .shelf: "books.vertical"
        case .notes: "doc.text"
        case .podcasts: "microphone"
        case .audio: "headphones"
        case .progress: "bookmark"
        case .search: "magnifyingglass"
        case .me: "person"
        }
    }

    var sfSymbolFill: String {
        switch self {
        case .shelf: "books.vertical.fill"
        case .notes: "doc.text.fill"
        case .podcasts: "microphone.fill"
        case .audio: "headphones"
        case .progress: "bookmark.fill"
        case .search: "magnifyingglass"
        case .me: "person.fill"
        }
    }

    /// True for destinations that can fold inside 书架 ⌃⌄ when not placed directly on the bottom bar.
    var isShelfSub: Bool { self == .progress || self == .podcasts || self == .audio || self == .notes }

    var isPinned: Bool { self == .me }
}

/// Immutable snapshot of both bottom bar tabs and shelf-folded visibility preferences.
/// Guarantees strict mutual exclusion: any shelf sub-entry promoted to `tabs` leaves 书架 ⌃⌄.
struct BottomNavConfig: Equatable, Sendable {
    let tabs: [PhoneTab]
    let hiddenShelfSubs: Set<PhoneTab>

    init(tabs: [PhoneTab], hiddenShelfSubs: Set<PhoneTab> = []) {
        self.tabs = tabs
        self.hiddenShelfSubs = hiddenShelfSubs
    }
}

/// Persistence, normalization, shelf-fold coordination, mutation helpers, and quick presets.
enum BottomNavPrefs {
    static let key = "appearance.bottomTabs"
    static let shelfHiddenKey = "appearance.shelfHiddenPlaces"
    static let minTabs = 2
    static let maxTabs = 5

    static let defaultTabs: [PhoneTab] = [.shelf, .notes, .me, .search]
    static let defaultRaw = serialize(defaultTabs)
    static let defaultHiddenShelfRaw = ""

    /// Shelf sub-places in `PlacePanel`'s canonical order after 书.
    static let shelfSubTabs: [PhoneTab] = [.progress, .podcasts, .audio, .notes]

    struct Preset: Identifiable, Hashable {
        let id: String
        let name: String
        let tabs: [PhoneTab]
        let hiddenShelfSubs: Set<PhoneTab>
    }

    static var presets: [Preset] {
        [
            Preset(id: "default", name: String(localized: "标准"), tabs: defaultTabs, hiddenShelfSubs: []),
            Preset(id: "minimal", name: String(localized: "极简"), tabs: [.shelf, .me], hiddenShelfSubs: Set(shelfSubTabs)),
            Preset(id: "podcast", name: String(localized: "播客"), tabs: [.shelf, .podcasts, .me, .search], hiddenShelfSubs: []),
            Preset(id: "audio", name: String(localized: "听书"), tabs: [.shelf, .audio, .podcasts, .me], hiddenShelfSubs: []),
        ]
    }

    static func serialize(_ tabs: [PhoneTab]) -> String {
        tabs.map(\.rawValue).joined(separator: ",")
    }

    static func parseHiddenShelfSubs(_ raw: String?) -> Set<PhoneTab> {
        var result = Set<PhoneTab>()
        for token in (raw ?? "").split(separator: ",") {
            let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
            if let tab = PhoneTab(rawValue: trimmed), tab.isShelfSub {
                result.insert(tab)
            }
        }
        return result
    }

    static func serializeHiddenShelfSubs(_ hidden: Set<PhoneTab>) -> String {
        shelfSubTabs.filter { hidden.contains($0) }.map(\.rawValue).joined(separator: ",")
    }

    static func config(tabsRaw: String?, hiddenShelfRaw: String?) -> BottomNavConfig {
        BottomNavConfig(tabs: parse(tabsRaw), hiddenShelfSubs: parseHiddenShelfSubs(hiddenShelfRaw))
    }

    static func parse(_ raw: String?) -> [PhoneTab] {
        var seen = Set<PhoneTab>()
        var result: [PhoneTab] = []
        for token in (raw ?? "").split(separator: ",") {
            let trimmed = token.trimmingCharacters(in: .whitespacesAndNewlines)
            if let tab = PhoneTab(rawValue: trimmed), seen.insert(tab).inserted {
                result.append(tab)
            }
        }
        if result.isEmpty { return defaultTabs }
        if !result.contains(.me) { result.append(.me) }
        if result.count > maxTabs {
            var trimmed = Array(result.prefix(maxTabs))
            if !trimmed.contains(.me) {
                trimmed[maxTabs - 1] = .me
            }
            result = trimmed
        }
        if result.count < minTabs, let fallback = PhoneTab.allCases.first(where: { !result.contains($0) }) {
            result.insert(fallback, at: 0)
        }
        return result
    }

    /// Entries currently folded inside 书架 ⌃⌄ (strictly disjoint from `config.tabs`).
    static func foldedInShelf(_ config: BottomNavConfig) -> [PhoneTab] {
        guard config.tabs.contains(.shelf) else { return [] }
        return shelfSubTabs.filter { !config.tabs.contains($0) && !config.hiddenShelfSubs.contains($0) }
    }

    /// Shelf places to display inside 书架's PlacePanel / PlaceStack (`[.books]` + folded sub-places).
    static func foldedShelfPlaces(_ config: BottomNavConfig) -> [ShelfPlace] {
        var places: [ShelfPlace] = [.books]
        for sub in foldedInShelf(config) {
            switch sub {
            case .progress: places.append(.progress)
            case .podcasts: places.append(.podcasts)
            case .audio: places.append(.audio)
            case .notes: places.append(.notes)
            default: break
            }
        }
        return places
    }

    /// Entries currently hidden from both the bottom bar and 书架 ⌃⌄.
    static func hiddenEntries(_ config: BottomNavConfig) -> [PhoneTab] {
        let folded = Set(foldedInShelf(config))
        return PhoneTab.allCases.filter { !$0.isPinned && !config.tabs.contains($0) && !folded.contains($0) }
    }

    /// Whether 书架 has anything folded inside it (sub-places or collections) to warrant ⌃⌄ / ⌄.
    static func shelfHasSwitcher(_ config: BottomNavConfig, hasCollections: Bool) -> Bool {
        config.tabs.contains(.shelf) && (!foldedInShelf(config).isEmpty || hasCollections)
    }

    static func activePresetId(_ config: BottomNavConfig) -> String? {
        for preset in presets {
            guard preset.tabs == config.tabs else { continue }
            let subsMatch = shelfSubTabs.allSatisfy { sub in
                config.tabs.contains(sub) || (config.hiddenShelfSubs.contains(sub) == preset.hiddenShelfSubs.contains(sub))
            }
            if subsMatch { return preset.id }
        }
        return nil
    }

    static func isDefault(_ config: BottomNavConfig) -> Bool {
        activePresetId(config) == "default"
    }

    static func hidden(in active: [PhoneTab]) -> [PhoneTab] {
        PhoneTab.allCases.filter { !active.contains($0) }
    }

    static func canRemove(_ tab: PhoneTab, from active: [PhoneTab]) -> Bool {
        !tab.isPinned && active.count > minTabs && active.contains(tab)
    }

    static func remove(_ tab: PhoneTab, from active: [PhoneTab]) -> [PhoneTab] {
        guard canRemove(tab, from: active) else { return active }
        return parse(serialize(active.filter { $0 != tab }))
    }

    /// Inserts before trailing 搜索 when 搜索 sits last, preserving the detached search orb layout.
    static func add(_ tab: PhoneTab, to active: [PhoneTab], at index: Int? = nil) -> [PhoneTab] {
        guard !active.contains(tab), active.count < maxTabs else { return active }
        var out = active
        if let index {
            out.insert(tab, at: min(max(0, index), out.count))
        } else if tab != .search, out.last == .search {
            out.insert(tab, at: out.count - 1)
        } else {
            out.append(tab)
        }
        return parse(serialize(out))
    }

    /// Replaces `target` in place with `replacement`, or swaps slots if `replacement` is already active.
    static func replace(_ target: PhoneTab, with replacement: PhoneTab, in active: [PhoneTab]) -> [PhoneTab] {
        guard target != replacement, let targetIndex = active.firstIndex(of: target) else { return active }
        var out = active
        if let existingIndex = active.firstIndex(of: replacement) {
            out[targetIndex] = replacement
            out[existingIndex] = target
        } else {
            guard !target.isPinned else { return active }
            out[targetIndex] = replacement
        }
        return parse(serialize(out))
    }

    static func move(from source: Int, to destination: Int, in active: [PhoneTab]) -> [PhoneTab] {
        guard source != destination, active.indices.contains(source), active.indices.contains(destination) else {
            return active
        }
        var out = active
        let item = out.remove(at: source)
        out.insert(item, at: destination)
        return out
    }

    // MARK: Config-level mutations (coordinated bottom bar + 书架 ⌃⌄ fold state)

    static func insert(_ tab: PhoneTab, into config: BottomNavConfig, at index: Int? = nil) -> BottomNavConfig {
        guard !config.tabs.contains(tab), config.tabs.count < maxTabs else { return config }
        return BottomNavConfig(tabs: add(tab, to: config.tabs, at: index), hiddenShelfSubs: config.hiddenShelfSubs)
    }

    static func replace(_ target: PhoneTab, with replacement: PhoneTab, in config: BottomNavConfig) -> BottomNavConfig {
        guard target != replacement, config.tabs.contains(target) else { return config }
        if config.tabs.contains(replacement) {
            return BottomNavConfig(tabs: replace(target, with: replacement, in: config.tabs), hiddenShelfSubs: config.hiddenShelfSubs)
        }
        guard !target.isPinned else { return config }
        let nextTabs = replace(target, with: replacement, in: config.tabs)
        var nextHidden = config.hiddenShelfSubs
        if target.isShelfSub {
            if nextTabs.contains(.shelf) {
                nextHidden.remove(target)
            } else {
                nextHidden.insert(target)
            }
        }
        return BottomNavConfig(tabs: nextTabs, hiddenShelfSubs: nextHidden)
    }

    static func move(from source: Int, to destination: Int, in config: BottomNavConfig) -> BottomNavConfig {
        BottomNavConfig(tabs: move(from: source, to: destination, in: config.tabs), hiddenShelfSubs: config.hiddenShelfSubs)
    }

    static func foldIntoShelf(_ tab: PhoneTab, in config: BottomNavConfig) -> BottomNavConfig {
        guard tab.isShelfSub, config.tabs.contains(.shelf) else { return config }
        if config.tabs.contains(tab), !canRemove(tab, from: config.tabs) { return config }
        let nextTabs = config.tabs.contains(tab) ? remove(tab, from: config.tabs) : config.tabs
        var nextHidden = config.hiddenShelfSubs
        nextHidden.remove(tab)
        return BottomNavConfig(tabs: nextTabs, hiddenShelfSubs: nextHidden)
    }

    static func hide(_ tab: PhoneTab, in config: BottomNavConfig) -> BottomNavConfig {
        guard !tab.isPinned else { return config }
        if config.tabs.contains(tab), !canRemove(tab, from: config.tabs) { return config }
        let nextTabs = config.tabs.contains(tab) ? remove(tab, from: config.tabs) : config.tabs
        var nextHidden = config.hiddenShelfSubs
        if tab.isShelfSub { nextHidden.insert(tab) }
        return BottomNavConfig(tabs: nextTabs, hiddenShelfSubs: nextHidden)
    }

    static func applyPreset(_ preset: Preset) -> BottomNavConfig {
        BottomNavConfig(tabs: preset.tabs, hiddenShelfSubs: preset.hiddenShelfSubs)
    }

    static func usesDetachedSearchOrb(_ tabs: [PhoneTab]) -> Bool {
        tabs.count >= 3 && tabs.last == .search
    }

    static func summary(_ raw: String?) -> String {
        parse(raw).map(\.title).joined(separator: " · ")
    }
}

// MARK: - 我的 › 外观 › 底部导航 (v4 Direct-Manipulation UI)

private enum NavDragSource: Equatable { case bar, shelf, hidden }
private enum NavDragMode: Equatable { case rail, ghost }

private enum NavDropIntent: Equatable {
    case reorder
    case insert(Int)
    case replace(PhoneTab)
    case moveTo(Int)
    case fold
    case hide
}

private struct NavPopoverAnchor: Equatable {
    let tab: PhoneTab
    let source: NavDragSource
    let frame: CGRect
}

struct PhoneBottomNavSettings: View {
    var collectionCount: Int = 0

    @AppStorage(BottomNavPrefs.key) private var rawTabs = BottomNavPrefs.defaultRaw
    @AppStorage(BottomNavPrefs.shelfHiddenKey) private var rawHiddenSubs = BottomNavPrefs.defaultHiddenShelfRaw
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(EInk.key) private var eink = false
    @Environment(\.colorScheme) private var scheme

    // Zone frames in `bottomNavRoot` coordinate space
    @State private var rootSize: CGSize = .zero
    @State private var heroFrame: CGRect = .zero
    @State private var trackFrame: CGRect = .zero
    @State private var orbFrame: CGRect = .zero
    @State private var shelfZoneFrame: CGRect = .zero
    @State private var hiddenZoneFrame: CGRect = .zero
    @State private var slotFrames: [PhoneTab: CGRect] = [:]

    // Active drag state
    @State private var draggingTab: PhoneTab?
    @State private var dragSource: NavDragSource?
    @State private var dragMode: NavDragMode = .rail
    @State private var ghostPoint: CGPoint = .zero
    @State private var railBaseX: CGFloat = 0
    @State private var railOffsetX: CGFloat = 0
    @State private var railOffsetY: CGFloat = 0
    @State private var dropIntent: NavDropIntent?
    @State private var placeholderIndex: Int?
    @State private var replaceHoverTab: PhoneTab?
    @State private var replaceBadge: String?
    @State private var heroDropActive = false
    @State private var shelfDropHover = false
    @State private var hiddenDropHover = false
    @State private var liveHint: String?

    // In-page frosted context popover & toast
    @State private var popover: NavPopoverAnchor?
    @State private var popoverSize: CGSize = CGSize(width: 236, height: 200)
    @State private var toastMsg: String?
    @State private var toastTask: Task<Void, Never>?

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var config: BottomNavConfig { BottomNavPrefs.config(tabsRaw: rawTabs, hiddenShelfRaw: rawHiddenSubs) }
    private var folded: [PhoneTab] { BottomNavPrefs.foldedInShelf(config) }
    private var hidden: [PhoneTab] { BottomNavPrefs.hiddenEntries(config) }
    private var shelfOn: Bool { config.tabs.contains(.shelf) }
    private var shelfHasSwitcher: Bool { BottomNavPrefs.shelfHasSwitcher(config, hasCollections: collectionCount > 0) }
    private var curPresetId: String? { BottomNavPrefs.activePresetId(config) }
    private var isDefault: Bool { BottomNavPrefs.isDefault(config) }
    private var previewLiquid: Bool { LiquidGlass.supported && !eink }
    private var detachedSearch: Bool { previewLiquid && BottomNavPrefs.usesDetachedSearchOrb(config.tabs) }
    private var trackTabs: [PhoneTab] { detachedSearch ? Array(config.tabs.dropLast()) : config.tabs }

    private var shelfZoneDropReady: Bool {
        guard let t = draggingTab, !t.isPinned else { return false }
        return t.isShelfSub && shelfOn && dragSource != .shelf
    }
    private var shelfZoneIneligible: Bool {
        guard let t = draggingTab, !t.isPinned else { return false }
        return !t.isShelfSub && dragSource == .bar
    }
    private var hiddenZoneDropReady: Bool {
        guard let t = draggingTab, !t.isPinned else { return false }
        return dragSource != .hidden
    }

    var body: some View {
        ZStack(alignment: .topLeading) {
            ScrollView {
                VStack(spacing: YYUI.s(14)) {
                    heroStage
                    presetRow
                    shelfFoldCard
                    hiddenZoneCard
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.top, YYUI.s(6))
                .padding(.bottom, YYUI.s(36))
                .frame(maxWidth: 480)
                .frame(maxWidth: .infinity)
            }
            .scrollDisabled(draggingTab != nil)
            .background(YYColor.bg)

            // Floating ghost pill during cross-zone drag
            if let tab = draggingTab, dragMode == .ghost {
                ghostPill(for: tab)
                    .position(ghostPoint)
                    .allowsHitTesting(false)
                    .zIndex(50)
            }

            // In-page Telegram-style frosted context popover (strictly inside viewport)
            if let anchor = popover {
                Color.black.opacity(0.14)
                    .ignoresSafeArea()
                    .onTapGesture { dismissPopover() }
                    .transition(.opacity)
                    .zIndex(40)

                inPagePopover(for: anchor)
                    .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.88).combined(with: .opacity))
                    .zIndex(45)
            }

            // Floating pill toast feedback
            if let msg = toastMsg {
                VStack {
                    Spacer()
                    Text(msg)
                        .font(YYUI.font(10.5, .medium))
                        .foregroundStyle(Color(yy: YYRGBA(247, 244, 238)))
                        .lineLimit(1)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                        .background(Color(yy: YYRGBA(28, 27, 25, 0.92)), in: .capsule)
                        .shadow(color: .black.opacity(0.24), radius: 14, y: 6)
                        .padding(.bottom, 16)
                        .padding(.horizontal, 20)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .allowsHitTesting(false)
                .transition(.move(edge: .bottom).combined(with: .opacity))
                .zIndex(60)
            }
        }
        .coordinateSpace(name: "bottomNavRoot")
        .onGeometryChange(for: CGSize.self, of: \.size) { rootSize = $0 }
        .navigationTitle("底部导航")
        .navigationBarTitleDisplayMode(.inline)
        .toolbar {
            ToolbarItem(placement: .topBarTrailing) {
                Button("恢复默认") {
                    dismissPopover()
                    commit(BottomNavPrefs.applyPreset(BottomNavPrefs.presets[0]), feedback: String(localized: "已切换「标准」"))
                }
                .font(YYUI.font(11.5, .medium))
                .foregroundStyle(tint.accent)
                .disabled(isDefault)
                .opacity(isDefault ? 0.30 : 1)
            }
        }
        .onDisappear {
            clearDragVisuals()
            popover = nil
            toastTask?.cancel()
        }
    }

    // MARK: Zone 1 — Atmospheric Warm-Paper Hero Stage

    private var heroStage: some View {
        let stageTop = scheme == .dark ? Color(yy: YYRGBA(36, 33, 30)) : Color(yy: YYRGBA(239, 234, 224))
        let stageBot = scheme == .dark ? Color(yy: YYRGBA(25, 23, 21)) : Color(yy: YYRGBA(231, 225, 213))
        let glassFill = scheme == .dark ? Color(yy: YYRGBA(38, 37, 35, 0.80)) : Color(yy: YYRGBA(252, 251, 248, 0.80))
        let glassStroke = scheme == .dark ? Color.white.opacity(0.16) : Color.white.opacity(0.94)
        let stageBorder: Color = heroDropActive
            ? tint.accent
            : (scheme == .dark ? Color.white.opacity(0.08) : Color(yy: YYRGBA(90, 74, 54, 0.14)))

        return VStack(spacing: 0) {
            // Subtle warm book spines peeking above the translucent glass bar
            HStack(alignment: .bottom, spacing: 8) {
                heroSpine(w: 30, h: 46, top: Color(yy: YYRGBA(31, 83, 104)), bot: Color(yy: YYRGBA(18, 41, 51)))
                heroSpine(w: 26, h: 38, top: Color(yy: YYRGBA(123, 106, 58)), bot: Color(yy: YYRGBA(67, 56, 27)))
                heroSpine(w: 32, h: 50, top: Color(yy: YYRGBA(219, 214, 202)), bot: Color(yy: YYRGBA(191, 185, 172)))
                heroSpine(w: 28, h: 42, top: Color(yy: YYRGBA(185, 183, 238)), bot: Color(yy: YYRGBA(111, 212, 170)))
                heroSpine(w: 24, h: 36, top: Color(yy: YYRGBA(200, 138, 124)), bot: Color(yy: YYRGBA(141, 82, 71)))
            }
            .frame(height: 50, alignment: .bottom)
            .opacity(eink ? 0.24 : 0.50)
            .blur(radius: eink ? 0 : 0.5)
            .allowsHitTesting(false)
            .padding(.bottom, -12)

            // Live editable glass / classic bottom bar
            HStack(spacing: 9) {
                HStack(spacing: 0) {
                    ForEach(Array(trackTabs.enumerated()), id: \.element) { idx, tab in
                        if placeholderIndex == idx, let incoming = draggingTab {
                            insertPlaceholder(incoming.title)
                        }
                        editableDockSlot(tab, index: idx)
                    }
                    if let pIdx = placeholderIndex, pIdx >= trackTabs.count, let incoming = draggingTab {
                        insertPlaceholder(incoming.title)
                    }
                }
                .padding(4)
                .frame(height: previewLiquid ? 60 : 58)
                .background {
                    if previewLiquid {
                        Capsule()
                            .fill(glassFill)
                            .overlay { Capsule().strokeBorder(glassStroke, lineWidth: 0.75) }
                            .shadow(color: .black.opacity(scheme == .dark ? 0.44 : 0.14), radius: 14, y: 6)
                    } else {
                        RoundedRectangle(cornerRadius: 16, style: .continuous)
                            .fill(YYColor.card)
                            .overlay { RoundedRectangle(cornerRadius: 16, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5) }
                            .shadow(color: .black.opacity(eink ? 0 : 0.06), radius: 9, y: 3)
                    }
                }
                .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { trackFrame = $0 }
                .animation(.spring(duration: 0.30, bounce: 0.12), value: trackTabs)
                .animation(.spring(duration: 0.24, bounce: 0.12), value: placeholderIndex)

                if detachedSearch {
                    editableSearchOrb(glassFill: glassFill, glassStroke: glassStroke)
                }
            }
            .zIndex(3)

            Text(liveHint ?? String(localized: "左右拖动排序 · 向下拖出移走 · 轻点槽位替换"))
                .font(YYUI.font(9.5, liveHint != nil ? .medium : .regular))
                .foregroundStyle(liveHint != nil ? tint.accent : YYColor.t2)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
                .padding(.top, 11)
        }
        .padding(.horizontal, 12)
        .padding(.top, 16)
        .padding(.bottom, 12)
        .background(
            LinearGradient(colors: [stageTop, stageBot], startPoint: .top, endPoint: .bottom),
            in: .rect(cornerRadius: 22, style: .continuous)
        )
        .overlay {
            RoundedRectangle(cornerRadius: 22, style: .continuous)
                .strokeBorder(stageBorder, lineWidth: heroDropActive ? 1.5 : 0.5)
        }
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { heroFrame = $0 }
    }

    private func heroSpine(w: CGFloat, h: CGFloat, top: Color, bot: Color) -> some View {
        UnevenRoundedRectangle(cornerRadii: .init(topLeading: 6, bottomLeading: 2, bottomTrailing: 2, topTrailing: 6))
            .fill(LinearGradient(colors: [top, bot], startPoint: .top, endPoint: .bottom))
            .frame(width: w, height: h)
    }

    private func insertPlaceholder(_ title: String) -> some View {
        Text(title)
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(tint.accent)
            .lineLimit(1)
            .frame(maxWidth: .infinity)
            .frame(height: 46)
            .background(tint.accentSoft, in: .capsule)
            .overlay {
                Capsule()
                    .strokeBorder(tint.accent, style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
            }
            .padding(.horizontal, 2)
            .transition(.scale(scale: 0.72).combined(with: .opacity))
    }

    private func editableDockSlot(_ tab: PhoneTab, index: Int) -> some View {
        let isRailDrag = draggingTab == tab && dragMode == .rail
        let isGhosted = draggingTab == tab && dragMode == .ghost
        let isReplace = replaceHoverTab == tab
        let isMenuOpen = popover?.source == .bar && popover?.tab == tab
        let hasSwitch = tab == .shelf && shelfHasSwitcher

        let ink: Color = (isRailDrag || isReplace || isMenuOpen) ? tint.accent : YYColor.t1
        let bg: Color = if isReplace || isMenuOpen {
            tint.accentSoft
        } else if isRailDrag {
            YYColor.card
        } else {
            .clear
        }
        let borderColor: Color = if isReplace {
            tint.accent
        } else if isMenuOpen || isRailDrag {
            tint.edge
        } else {
            .clear
        }
        let borderWidth: CGFloat = isReplace ? 1.8 : 1.0

        return ZStack(alignment: .top) {
            VStack(spacing: 2) {
                HStack(spacing: 1.5) {
                    if hasSwitch { Color.clear.frame(width: 7, height: 1) }
                    tabIcon(tab, active: isRailDrag || isReplace, size: 20)
                    if hasSwitch {
                        Image(systemName: "chevron.up.chevron.down")
                            .font(.system(size: 7.5, weight: .bold))
                            .opacity(0.65)
                    }
                }
                .foregroundStyle(ink)
                Text(tab.title)
                    .font(.system(size: 10, weight: (isRailDrag || isReplace || isMenuOpen) ? .semibold : .medium))
                    .foregroundStyle(ink)
                    .lineLimit(1)
            }
            .frame(maxWidth: .infinity)
            .frame(height: 52)
            .background(bg, in: .capsule)
            .overlay { Capsule().strokeBorder(borderColor, lineWidth: borderWidth) }
            .shadow(color: .black.opacity(isRailDrag ? 0.18 : 0), radius: 10, y: 5)
            .contentShape(.capsule)

            if isReplace, let badge = replaceBadge {
                replaceBadgePill(badge)
                    .offset(y: -23)
            }
        }
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { slotFrames[tab] = $0 }
        .scaleEffect(isRailDrag ? 1.06 : (isReplace ? 0.95 : (isGhosted ? 0.90 : 1.0)))
        .opacity(isGhosted ? 0.16 : 1.0)
        .offset(x: isRailDrag ? railOffsetX : 0, y: isRailDrag ? railOffsetY : 0)
        .zIndex(isRailDrag ? 25 : (isReplace ? 10 : 0))
        .animation(.spring(duration: 0.24, bounce: 0.12), value: isRailDrag)
        .animation(.spring(duration: 0.20, bounce: 0.14), value: isReplace)
        .gesture(barSlotGesture(tab: tab, isDetachedOrb: false))
        .accessibilityAction(named: Text("左移")) {
            if index > 0 { commit(BottomNavPrefs.move(from: index, to: index - 1, in: config)) }
        }
        .accessibilityAction(named: Text("右移")) {
            if index < trackTabs.count - 1 { commit(BottomNavPrefs.move(from: index, to: index + 1, in: config)) }
        }
    }

    private func editableSearchOrb(glassFill: Color, glassStroke: Color) -> some View {
        let isRailDrag = draggingTab == .search && dragMode == .rail
        let isGhosted = draggingTab == .search && dragMode == .ghost
        let isReplace = replaceHoverTab == .search
        let isMenuOpen = popover?.source == .bar && popover?.tab == .search
        let ink: Color = (isRailDrag || isReplace || isMenuOpen) ? tint.accent : YYColor.t1
        let bg: Color = (isReplace || isMenuOpen) ? tint.accentSoft : (isRailDrag ? YYColor.card : glassFill)
        let strokeColor: Color = isReplace ? tint.accent : ((isMenuOpen || isRailDrag) ? tint.edge : glassStroke)
        let strokeWidth: CGFloat = isReplace ? 1.8 : ((isMenuOpen || isRailDrag) ? 1.0 : 0.75)

        return ZStack(alignment: .top) {
            tabIcon(.search, active: isRailDrag || isReplace, size: 21)
                .foregroundStyle(ink)
                .frame(width: 60, height: 60)
                .background(bg, in: .circle)
                .overlay { Circle().strokeBorder(strokeColor, lineWidth: strokeWidth) }
                .shadow(color: .black.opacity(scheme == .dark ? 0.44 : 0.14), radius: 14, y: 6)
                .contentShape(.circle)

            if isReplace, let badge = replaceBadge {
                replaceBadgePill(badge)
                    .offset(y: -23)
            }
        }
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: {
            orbFrame = $0
            slotFrames[.search] = $0
        }
        .scaleEffect(isRailDrag ? 1.06 : (isReplace ? 0.95 : (isGhosted ? 0.90 : 1.0)))
        .opacity(isGhosted ? 0.16 : 1.0)
        .offset(x: isRailDrag ? railOffsetX : 0, y: isRailDrag ? railOffsetY : 0)
        .zIndex(isRailDrag ? 25 : (isReplace ? 10 : 0))
        .animation(.spring(duration: 0.24, bounce: 0.12), value: isRailDrag)
        .gesture(barSlotGesture(tab: .search, isDetachedOrb: true))
    }

    private func replaceBadgePill(_ text: String) -> some View {
        Text(text)
            .font(.system(size: 10, weight: .semibold))
            .foregroundStyle(.white)
            .lineLimit(1)
            .fixedSize()
            .padding(.horizontal, 8)
            .padding(.vertical, 2.5)
            .background(tint.accent, in: .capsule)
            .shadow(color: .black.opacity(0.20), radius: 6, y: 3)
            .allowsHitTesting(false)
    }

    // MARK: Zone 2 — Native Segmented Presets (matching Yuyue's YYSegments language)

    private var presetRow: some View {
        YYSegments(
            items: BottomNavPrefs.presets.map { (Optional($0.id), $0.name) },
            selection: Binding<String?>(
                get: { curPresetId },
                set: { pickedId in
                    guard let id = pickedId, let preset = BottomNavPrefs.presets.first(where: { $0.id == id }) else { return }
                    dismissPopover()
                    commit(BottomNavPrefs.applyPreset(preset), feedback: String(localized: "已切换「\(preset.name)」"))
                }
            )
        )
    }

    // MARK: Zone 3 — 书架内收起 Card

    private var shelfFoldCard: some View {
        let bg: Color = shelfDropHover ? tint.accentSoft : YYColor.card
        let border: Color = shelfDropHover ? tint.accent : (shelfZoneDropReady ? tint.edge : YYColor.line)
        let alpha: Double = shelfZoneIneligible ? 0.36 : (shelfOn ? 1.0 : 0.55)

        return VStack(alignment: .leading, spacing: 11) {
            HStack(spacing: 6) {
                Text("书架内收起")
                    .font(YYUI.font(11.5, .semibold))
                    .foregroundStyle(YYColor.t1)
                HStack(spacing: 2) {
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.system(size: 7.5, weight: .bold))
                    Text("点书架切换")
                        .font(.system(size: 10, weight: .medium))
                }
                .foregroundStyle(tint.accent)
                .padding(.horizontal, 7)
                .padding(.vertical, 2.5)
                .background(tint.accentSoft, in: .capsule)

                Spacer(minLength: 4)
                Text(shelfOn ? "轻点放到底栏 · 拖入可插位" : "需先开启书架")
                    .font(YYUI.font(9.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }

            if !folded.isEmpty || (shelfOn && collectionCount > 0) {
                FlowWrapLayout(horizontalSpacing: 8, verticalSpacing: 8) {
                    ForEach(folded) { tab in
                        shelfEntryTile(tab)
                    }
                    if shelfOn && collectionCount > 0 {
                        passiveCollectionsTile
                    }
                }
                .animation(.spring(duration: 0.30, bounce: 0.12), value: folded)
            } else {
                Text(shelfOn
                     ? "已全部移到底栏或隐藏，书架自动省去折叠箭头"
                     : "「书架」当前已隐藏，点下方恢复即可折叠收纳")
                    .font(YYUI.font(10))
                    .foregroundStyle(YYColor.t3)
                    .padding(.vertical, 2)
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 13)
        .background(bg, in: .rect(cornerRadius: 20, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 20, style: .continuous)
                .strokeBorder(border, lineWidth: shelfDropHover ? 1.5 : 0.5)
        }
        .opacity(alpha)
        .animation(.easeOut(duration: 0.18), value: shelfDropHover)
        .animation(.easeOut(duration: 0.18), value: shelfZoneIneligible)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { shelfZoneFrame = $0 }
    }

    private func shelfEntryTile(_ tab: PhoneTab) -> some View {
        let isGhosted = draggingTab == tab
        return HStack(spacing: 5) {
            HStack(spacing: 7) {
                tabIcon(tab, active: false, size: 15)
                    .foregroundStyle(tint.accent)
                    .frame(width: 28, height: 28)
                    .background(YYColor.card, in: .rect(cornerRadius: 9, style: .continuous))
                    .shadow(color: .black.opacity(0.06), radius: 2, y: 1)
                Text(tab.title)
                    .font(YYUI.font(11, .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
            }
            .contentShape(.rect)
            .gesture(chipGesture(tab: tab, source: .shelf))

            Button {
                dismissPopover()
                commit(BottomNavPrefs.hide(tab, in: config), feedback: String(localized: "已隐藏「\(tab.title)」"))
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 9.5, weight: .bold))
                    .foregroundStyle(YYColor.t3)
                    .frame(width: 22, height: 22)
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "隐藏\(tab.title)"))
        }
        .padding(.leading, 7)
        .padding(.trailing, 5)
        .padding(.vertical, 6)
        .background(YYColor.bg, in: .rect(cornerRadius: 14, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(YYColor.line, lineWidth: 0.5)
        }
        .opacity(isGhosted ? 0.22 : 1.0)
        .scaleEffect(isGhosted ? 0.94 : 1.0)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { slotFrames[tab] = $0 }
    }

    private var passiveCollectionsTile: some View {
        HStack(spacing: 7) {
            Image(systemName: "rectangle.stack")
                .font(.system(size: 13.5, weight: .regular))
                .foregroundStyle(YYColor.t2)
                .frame(width: 28, height: 28)
                .background(YYColor.inset, in: .rect(cornerRadius: 9, style: .continuous))
            Text(String(localized: "合集 (\(collectionCount))"))
                .font(YYUI.font(10.5, .medium))
                .foregroundStyle(YYColor.t2)
                .lineLimit(1)
        }
        .padding(.leading, 7)
        .padding(.trailing, 12)
        .padding(.vertical, 6)
        .overlay {
            RoundedRectangle(cornerRadius: 14, style: .continuous)
                .strokeBorder(YYColor.line, style: StrokeStyle(lineWidth: 1, dash: [4, 3]))
        }
        .opacity(0.72)
    }

    // MARK: Zone 4 — Recessed Matte 已隐藏 Tray (distinct from raised white 书架内收起 card)

    private var hiddenZoneCard: some View {
        let idleTrayBg = scheme == .dark ? Color(yy: YYRGBA(20, 18, 16)) : Color(yy: YYRGBA(238, 234, 226))
        let idleTrayBorder = scheme == .dark ? Color.white.opacity(0.05) : Color(yy: YYRGBA(51, 46, 41, 0.07))
        let bg: Color = hiddenDropHover
            ? YYColor.danger.opacity(0.14)
            : (hiddenZoneDropReady ? YYColor.danger.opacity(0.06) : idleTrayBg)
        let activeDrop = hiddenZoneDropReady || hiddenDropHover
        let dashStroke: Color = hiddenDropHover ? YYColor.danger : YYColor.danger.opacity(0.52)

        return Group {
            if hidden.isEmpty {
                Text("把不看的入口向下拖到这里隐藏")
                    .font(YYUI.font(10))
                    .foregroundStyle(hiddenDropHover ? YYColor.danger : YYColor.t3)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 14)
                    .padding(.horizontal, 14)
            } else {
                VStack(alignment: .leading, spacing: 9) {
                    HStack {
                        Text("已隐藏")
                            .font(YYUI.font(10.5, .semibold))
                            .foregroundStyle(YYColor.t2)
                        Spacer()
                        Text("轻点加回底栏 · 拖入任意区")
                            .font(YYUI.font(9.5))
                            .foregroundStyle(YYColor.t3)
                    }
                    FlowWrapLayout(horizontalSpacing: 8, verticalSpacing: 8) {
                        ForEach(hidden) { tab in
                            hiddenChip(tab)
                        }
                    }
                    .animation(.spring(duration: 0.30, bounce: 0.12), value: hidden)
                }
                .padding(.horizontal, 14)
                .padding(.vertical, 12)
            }
        }
        .background(bg, in: .rect(cornerRadius: 18, style: .continuous))
        .overlay {
            if activeDrop {
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .strokeBorder(dashStroke, style: StrokeStyle(lineWidth: 1.25, dash: [6, 4]))
            } else {
                RoundedRectangle(cornerRadius: 18, style: .continuous)
                    .strokeBorder(idleTrayBorder, lineWidth: 0.5)
            }
        }
        .animation(.easeOut(duration: 0.18), value: hiddenDropHover)
        .animation(.easeOut(duration: 0.18), value: hiddenZoneDropReady)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { hiddenZoneFrame = $0 }
    }

    private func hiddenChip(_ tab: PhoneTab) -> some View {
        let isGhosted = draggingTab == tab
        return HStack(spacing: 6) {
            Image(systemName: "plus")
                .font(.system(size: 9.5, weight: .bold))
                .foregroundStyle(YYColor.t2)
                .frame(width: 18, height: 18)
                .background(YYColor.inset, in: .circle)
            tabIcon(tab, active: false, size: 14)
                .foregroundStyle(YYColor.t2)
            Text(tab.title)
                .font(YYUI.font(10.5, .medium))
                .foregroundStyle(YYColor.t2)
                .lineLimit(1)
        }
        .padding(.leading, 8)
        .padding(.trailing, 12)
        .padding(.vertical, 7)
        .background(YYColor.card, in: .capsule)
        .overlay { Capsule().strokeBorder(YYColor.line, lineWidth: 0.5) }
        .shadow(color: scheme == .dark || eink ? .clear : .black.opacity(0.05), radius: 2, y: 1)
        .opacity(isGhosted ? 0.22 : 1.0)
        .contentShape(.capsule)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("bottomNavRoot")) } action: { slotFrames[tab] = $0 }
        .gesture(chipGesture(tab: tab, source: .hidden))
    }

    // MARK: Floating Ghost Pill & In-Page Frosted Popover

    private func ghostPill(for tab: PhoneTab) -> some View {
        HStack(spacing: 7) {
            tabIcon(tab, active: true, size: 18)
                .foregroundStyle(tint.accent)
            Text(tab.title)
                .font(YYUI.font(11.5, .semibold))
                .foregroundStyle(tint.accent)
        }
        .padding(.leading, 11)
        .padding(.trailing, 15)
        .padding(.vertical, 9)
        .background(.ultraThinMaterial, in: .capsule)
        .overlay { Capsule().strokeBorder(tint.edge, lineWidth: 0.75) }
        .shadow(color: .black.opacity(0.22), radius: 18, y: 8)
        .scaleEffect(1.06)
    }

    private func inPagePopover(for anchor: NavPopoverAnchor) -> some View {
        let tab = anchor.tab
        let mw: CGFloat = 236
        let mh: CGFloat = max(140, popoverSize.height)
        let margin: CGFloat = 12
        let clampedX = min(max(margin, anchor.frame.midX - mw / 2), max(margin, rootSize.width - mw - margin))
        let belowY = anchor.frame.maxY + 8
        let aboveY = anchor.frame.minY - mh - 8
        let y = (belowY + mh <= rootSize.height - margin || aboveY < margin) ? max(margin, belowY) : max(margin, aboveY)
        let removable = BottomNavPrefs.canRemove(tab, from: config.tabs)

        return VStack(alignment: .leading, spacing: 4) {
            if anchor.source == .bar {
                let idx = config.tabs.firstIndex(of: tab) ?? 0
                if tab.isPinned {
                    HStack {
                        Text(String(localized: "\(tab.title) · 常驻底栏"))
                        Spacer()
                        Text(String(localized: "第 \(idx + 1) 格")).monospacedDigit()
                    }
                    .font(YYUI.font(9.5, .semibold))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)

                    if idx > 0 {
                        popoverActionRow("chevron.left", title: String(localized: "向左移一格"), color: YYColor.t1) {
                            dismissPopover()
                            commit(BottomNavPrefs.move(from: idx, to: idx - 1, in: config))
                        }
                    }
                    if idx < config.tabs.count - 1 {
                        popoverActionRow("chevron.right", title: String(localized: "向右移一格"), color: YYColor.t1) {
                            dismissPopover()
                            commit(BottomNavPrefs.move(from: idx, to: idx + 1, in: config))
                        }
                    }
                } else {
                    HStack {
                        Text(String(localized: "替换「\(tab.title)」为"))
                        Spacer()
                        Text(String(localized: "第 \(idx + 1) 格")).monospacedDigit()
                    }
                    .font(YYUI.font(9.5, .semibold))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)

                    let candidates = PhoneTab.allCases.filter { $0 != tab && !$0.isPinned }
                    popoverTileGrid(candidates) { picked in
                        dismissPopover()
                        let msg = config.tabs.contains(picked)
                            ? String(localized: "已互换「\(tab.title)」与「\(picked.title)」")
                            : String(localized: "已将「\(tab.title)」替换为「\(picked.title)」")
                        commit(BottomNavPrefs.replace(tab, with: picked, in: config), feedback: msg)
                    }

                    if removable {
                        Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 6).padding(.vertical, 2)
                        if tab.isShelfSub && shelfOn {
                            popoverActionRow("arrow.down", title: String(localized: "收起进书架 ⌃⌄"), color: tint.accent) {
                                dismissPopover()
                                commit(BottomNavPrefs.foldIntoShelf(tab, in: config), feedback: String(localized: "「\(tab.title)」已收起进书架 ⌃⌄"))
                            }
                        }
                        popoverActionRow("eye.slash", title: String(localized: "隐藏「\(tab.title)」"), color: YYColor.danger) {
                            dismissPopover()
                            commit(BottomNavPrefs.hide(tab, in: config), feedback: String(localized: "已隐藏「\(tab.title)」"))
                        }
                    }
                }
            } else {
                Text(String(localized: "底栏已满，用「\(tab.title)」替换…"))
                    .font(YYUI.font(9.5, .semibold))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 4)

                let replaceable = config.tabs.filter { !$0.isPinned }
                popoverTileGrid(replaceable, prefix: String(localized: "换 ")) { slot in
                    dismissPopover()
                    commit(
                        BottomNavPrefs.replace(slot, with: tab, in: config),
                        feedback: String(localized: "已将「\(slot.title)」替换为「\(tab.title)」")
                    )
                }

                if anchor.source == .hidden && tab.isShelfSub && shelfOn {
                    Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 6).padding(.vertical, 2)
                    popoverActionRow("arrow.down", title: String(localized: "先收起进书架 ⌃⌄"), color: tint.accent) {
                        dismissPopover()
                        commit(BottomNavPrefs.foldIntoShelf(tab, in: config), feedback: String(localized: "「\(tab.title)」已收起进书架 ⌃⌄"))
                    }
                }
            }
        }
        .padding(8)
        .frame(width: mw)
        .background(.ultraThinMaterial, in: .rect(cornerRadius: 18, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 18, style: .continuous)
                .strokeBorder(scheme == .dark ? Color.white.opacity(0.16) : Color.white.opacity(0.92), lineWidth: 0.75)
        }
        .shadow(color: .black.opacity(0.24), radius: 22, y: 10)
        .onGeometryChange(for: CGSize.self, of: \.size) { popoverSize = $0 }
        .offset(x: clampedX, y: y)
    }

    private func popoverTileGrid(_ items: [PhoneTab], prefix: String = "", onPick: @escaping (PhoneTab) -> Void) -> some View {
        let cols = Array(repeating: GridItem(.flexible(), spacing: 5), count: 3)
        return LazyVGrid(columns: cols, spacing: 5) {
            ForEach(items) { item in
                Button { onPick(item) } label: {
                    VStack(spacing: 3) {
                        tabIcon(item, active: false, size: 17)
                        Text(prefix.isEmpty ? item.title : "\(prefix)\(item.title)")
                            .font(.system(size: 10.5, weight: .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(YYColor.t1)
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 8)
                    .background(YYColor.inset, in: .rect(cornerRadius: 10, style: .continuous))
                    .contentShape(.rect)
                }
                .buttonStyle(YYPressStyle(scale: 0.95))
            }
        }
        .padding(.horizontal, 2)
        .padding(.vertical, 2)
    }

    private func popoverActionRow(_ symbol: String, title: String, color: Color, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: symbol)
                    .font(.system(size: 13.5, weight: .medium))
                    .frame(width: 18)
                Text(title)
                    .font(YYUI.font(11, .medium))
                    .lineLimit(1)
                Spacer()
            }
            .foregroundStyle(color)
            .padding(.horizontal, 10)
            .padding(.vertical, 9)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    // MARK: Gestures (1:1 Rail Drag + Cross-Zone Ghost + 1-Tap Direct Actions)

    private func barSlotGesture(tab: PhoneTab, isDetachedOrb: Bool) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("bottomNavRoot"))
            .onChanged { value in
                let dist = hypot(value.translation.width, value.translation.height)
                if draggingTab == nil {
                    guard dist > 8 else { return }
                    popover = nil
                    draggingTab = tab
                    dragSource = .bar
                    dragMode = .rail
                    railBaseX = 0
                    railOffsetX = 0
                    railOffsetY = 0
                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                }
                guard draggingTab == tab else { return }

                let dxTotal = value.translation.width
                let dyTotal = value.translation.height

                if dragMode == .rail {
                    if dyTotal > 42 {
                        if tab.isPinned {
                            let extra = dyTotal - 42
                            railOffsetY = min(18, extra * 0.18 + 8)
                            railOffsetX = dxTotal - railBaseX
                            liveHint = String(localized: "「我的」常驻底栏，可左右拖动调整位置")
                            return
                        }
                        dragMode = .ghost
                        ghostPoint = value.location
                        updateGhostTarget(tab: tab, source: .bar, point: value.location)
                        return
                    }

                    if isDetachedOrb {
                        if dxTotal < -24 {
                            dragMode = .ghost
                            ghostPoint = value.location
                            updateGhostTarget(tab: tab, source: .bar, point: value.location)
                            return
                        }
                        railOffsetX = dxTotal * 0.4
                        railOffsetY = max(-10, dyTotal * 0.4)
                        liveHint = String(localized: "向左拖入胶囊调顺序 · 向下拖出隐藏")
                        return
                    }

                    let slots = trackTabs
                    let slotW = max(48, (trackFrame.width - 8) / CGFloat(max(1, slots.count)))
                    let maxTrackIdx = max(0, slots.count - 1)
                    var curIdx = config.tabs.firstIndex(of: tab) ?? 0
                    var localX = dxTotal - railBaseX
                    let threshold = slotW * 0.52

                    if localX > threshold, curIdx < maxTrackIdx {
                        commit(BottomNavPrefs.move(from: curIdx, to: curIdx + 1, in: config))
                        railBaseX += slotW
                        localX = dxTotal - railBaseX
                        curIdx += 1
                    } else if localX < -threshold, curIdx > 0 {
                        commit(BottomNavPrefs.move(from: curIdx, to: curIdx - 1, in: config))
                        railBaseX -= slotW
                        localX = dxTotal - railBaseX
                        curIdx -= 1
                    }

                    if curIdx == 0, localX < 0 { localX *= 0.25 }
                    if curIdx == maxTrackIdx, localX > 0 {
                        if detachedSearch, localX > slotW * 0.65 {
                            dragMode = .ghost
                            ghostPoint = value.location
                            updateGhostTarget(tab: tab, source: .bar, point: value.location)
                            return
                        }
                        localX *= 0.28
                    }

                    railOffsetX = localX
                    railOffsetY = tab.isPinned ? min(10, max(-6, dyTotal * 0.16)) : max(-10, dyTotal * 0.35)
                    dropIntent = .reorder
                    liveHint = tab.isPinned
                        ? String(localized: "「我的」固定在第 \(curIdx + 1) 格")
                        : String(localized: "松手固定在第 \(curIdx + 1) 格 · 向下拖出移走")
                } else {
                    ghostPoint = value.location
                    updateGhostTarget(tab: tab, source: .bar, point: value.location)
                }
            }
            .onEnded { value in
                if draggingTab == nil {
                    let frame = slotFrames[tab] ?? CGRect(origin: value.location, size: CGSize(width: 60, height: 52))
                    handleTap(tab: tab, source: .bar, frame: frame)
                    return
                }
                guard draggingTab == tab else { return }
                let finalMode = dragMode
                let finalIntent = dropIntent
                withAnimation(.spring(duration: 0.28, bounce: 0.14)) {
                    clearDragVisuals()
                }
                if finalMode == .ghost {
                    executeDrop(tab: tab, source: .bar, intent: finalIntent)
                }
            }
    }

    private func chipGesture(tab: PhoneTab, source: NavDragSource) -> some Gesture {
        DragGesture(minimumDistance: 0, coordinateSpace: .named("bottomNavRoot"))
            .onChanged { value in
                let dist = hypot(value.translation.width, value.translation.height)
                if draggingTab == nil {
                    guard dist > 8 else { return }
                    popover = nil
                    draggingTab = tab
                    dragSource = source
                    dragMode = .ghost
                    ghostPoint = value.location
                    UIImpactFeedbackGenerator(style: .light).impactOccurred()
                    updateGhostTarget(tab: tab, source: source, point: value.location)
                    return
                }
                guard draggingTab == tab else { return }
                ghostPoint = value.location
                updateGhostTarget(tab: tab, source: source, point: value.location)
            }
            .onEnded { value in
                if draggingTab == nil {
                    let frame = slotFrames[tab] ?? CGRect(origin: value.location, size: CGSize(width: 80, height: 36))
                    handleTap(tab: tab, source: source, frame: frame)
                    return
                }
                guard draggingTab == tab else { return }
                let finalIntent = dropIntent
                clearDragVisuals()
                executeDrop(tab: tab, source: source, intent: finalIntent)
            }
    }

    private func updateGhostTarget(tab: PhoneTab, source: NavDragSource, point: CGPoint) {
        let slots = trackTabs
        heroDropActive = false
        shelfDropHover = false
        hiddenDropHover = false
        replaceHoverTab = nil
        replaceBadge = nil
        dropIntent = nil

        // 1. Over Hero Bottom Bar?
        if heroFrame.insetBy(dx: -10, dy: -10).contains(point) {
            heroDropActive = true

            if detachedSearch, orbFrame.insetBy(dx: -6, dy: -6).contains(point) {
                placeholderIndex = nil
                if tab == .search {
                    dropIntent = .reorder
                    liveHint = String(localized: "松手保持「搜索」独立圆球")
                    return
                }
                replaceHoverTab = .search
                replaceBadge = source == .bar ? String(localized: "互换位置") : String(localized: "替换 \(PhoneTab.search.title)")
                dropIntent = .replace(.search)
                liveHint = source == .bar
                    ? String(localized: "松手与「\(PhoneTab.search.title)」互换位置")
                    : String(localized: "松手用「\(tab.title)」替换「\(PhoneTab.search.title)」")
                return
            }

            if source == .bar {
                placeholderIndex = nil
                if !slots.isEmpty, trackFrame.width > 1 {
                    let rel = min(0.9999, max(0, (point.x - trackFrame.minX) / trackFrame.width))
                    let targetIdx = min(slots.count - 1, Int(rel * CGFloat(slots.count)))
                    let targetTab = slots[targetIdx]
                    if targetTab == tab {
                        dropIntent = .reorder
                        liveHint = String(localized: "松手将「\(tab.title)」放回原位")
                    } else {
                        replaceHoverTab = targetTab
                        replaceBadge = String(localized: "移到此处")
                        dropIntent = .moveTo(targetIdx)
                        liveHint = String(localized: "松手将「\(tab.title)」移到第 \(targetIdx + 1) 格")
                    }
                }
                return
            }

            // From shelf/hidden into bottom bar: Full -> Replace, Non-full -> Fixed-point magnetic Placeholder
            if !slots.isEmpty, trackFrame.width > 1 {
                let rel = min(0.9999, max(0, (point.x - trackFrame.minX) / trackFrame.width))
                if config.tabs.count >= BottomNavPrefs.maxTabs {
                    placeholderIndex = nil
                    let targetIdx = min(slots.count - 1, Int(rel * CGFloat(slots.count)))
                    let targetTab = slots[targetIdx]
                    if !targetTab.isPinned {
                        replaceHoverTab = targetTab
                        replaceBadge = String(localized: "替换 \(targetTab.title)")
                        dropIntent = .replace(targetTab)
                        liveHint = String(localized: "底栏已满 · 松手替换「\(targetTab.title)」")
                    } else {
                        liveHint = String(localized: "「我的」常驻底栏，请移到其他槽位替换")
                    }
                } else {
                    let bucket = min(slots.count, Int(rel * CGFloat(slots.count + 1)))
                    if placeholderIndex != bucket {
                        placeholderIndex = bucket
                        UISelectionFeedbackGenerator().selectionChanged()
                    }
                    dropIntent = .insert(bucket)
                    liveHint = String(localized: "松手插入第 \(bucket + 1) 格（轻点已有槽位可直接替换）")
                }
            }
            return
        }

        placeholderIndex = nil

        // 2. Over 书架内收起 card (shelfSub only)
        if shelfZoneFrame.insetBy(dx: -4, dy: -4).contains(point), tab.isShelfSub, shelfOn, source != .shelf {
            shelfDropHover = true
            dropIntent = .fold
            liveHint = String(localized: "松手收起「\(tab.title)」到书架 ⌃⌄")
            return
        }

        // 3. Over 已隐藏 zone (or pulled clearly below hero stage for non-shelfSub bar items)
        let pulledDownToHide = source == .bar && !tab.isShelfSub && point.y > heroFrame.maxY + 24
        if (hiddenZoneFrame.insetBy(dx: -12, dy: -12).contains(point) || pulledDownToHide), !tab.isPinned, source != .hidden {
            hiddenDropHover = true
            dropIntent = .hide
            liveHint = String(localized: "松手隐藏「\(tab.title)」")
            return
        }

        liveHint = nil
    }

    private func handleTap(tab: PhoneTab, source: NavDragSource, frame: CGRect) {
        if source == .bar {
            withAnimation(.spring(duration: 0.24, bounce: 0.14)) {
                popover = NavPopoverAnchor(tab: tab, source: source, frame: frame)
            }
        } else if config.tabs.count < BottomNavPrefs.maxTabs {
            let msg = tab.isShelfSub
                ? String(localized: "「\(tab.title)」已放到底栏（自动移出书架收起）")
                : String(localized: "「\(tab.title)」已放到底栏")
            commit(BottomNavPrefs.insert(tab, into: config), feedback: msg)
        } else {
            withAnimation(.spring(duration: 0.24, bounce: 0.14)) {
                popover = NavPopoverAnchor(tab: tab, source: source, frame: frame)
            }
        }
    }

    private func executeDrop(tab: PhoneTab, source: NavDragSource, intent: NavDropIntent?) {
        switch intent {
        case .insert(let idx):
            let msg = tab.isShelfSub
                ? String(localized: "「\(tab.title)」已放到底栏（自动移出书架收起）")
                : String(localized: "「\(tab.title)」已放到底栏")
            commit(BottomNavPrefs.insert(tab, into: config, at: idx), feedback: msg)
        case .replace(let target):
            let msg = config.tabs.contains(tab)
                ? String(localized: "已互换「\(target.title)」与「\(tab.title)」")
                : String(localized: "已将「\(target.title)」替换为「\(tab.title)」")
            commit(BottomNavPrefs.replace(target, with: tab, in: config), feedback: msg)
        case .moveTo(let idx):
            if let cur = config.tabs.firstIndex(of: tab) {
                commit(BottomNavPrefs.move(from: cur, to: idx, in: config))
            }
        case .fold:
            if config.tabs.contains(tab), !BottomNavPrefs.canRemove(tab, from: config.tabs) {
                showToast(String(localized: "底栏至少保留 2 个入口"))
            } else {
                commit(BottomNavPrefs.foldIntoShelf(tab, in: config), feedback: String(localized: "「\(tab.title)」已收起进书架 ⌃⌄"))
            }
        case .hide:
            if config.tabs.contains(tab), !BottomNavPrefs.canRemove(tab, from: config.tabs) {
                showToast(String(localized: "底栏至少保留 2 个入口，可直接拖新入口覆盖替换"))
            } else {
                commit(BottomNavPrefs.hide(tab, in: config), feedback: String(localized: "已隐藏「\(tab.title)」"))
            }
        case .reorder, nil:
            break
        }
    }

    private func commit(_ next: BottomNavConfig, feedback: String? = nil) {
        let nextTabs = BottomNavPrefs.serialize(next.tabs)
        let nextHidden = BottomNavPrefs.serializeHiddenShelfSubs(next.hiddenShelfSubs)
        if nextTabs != rawTabs || nextHidden != rawHiddenSubs {
            withAnimation(.spring(duration: 0.32, bounce: 0.12)) {
                rawTabs = nextTabs
                rawHiddenSubs = nextHidden
            }
            UISelectionFeedbackGenerator().selectionChanged()
        }
        if let feedback { showToast(feedback) }
    }

    private func dismissPopover() {
        withAnimation(YYMotion.exit) { popover = nil }
    }

    private func clearDragVisuals() {
        draggingTab = nil
        dragSource = nil
        dragMode = .rail
        railBaseX = 0
        railOffsetX = 0
        railOffsetY = 0
        dropIntent = nil
        placeholderIndex = nil
        replaceHoverTab = nil
        replaceBadge = nil
        heroDropActive = false
        shelfDropHover = false
        hiddenDropHover = false
        liveHint = nil
    }

    private func showToast(_ msg: String) {
        toastTask?.cancel()
        withAnimation(YYMotion.quick) { toastMsg = msg }
        toastTask = Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(2100))
            guard !Task.isCancelled else { return }
            withAnimation(YYMotion.exit) { toastMsg = nil }
        }
    }

    @ViewBuilder
    private func tabIcon(_ item: PhoneTab, active: Bool, size: CGFloat) -> some View {
        if let asset = active ? item.assetFill : item.assetLine {
            Image(asset)
                .resizable()
                .renderingMode(.template)
                .scaledToFit()
                .frame(width: size, height: size)
        } else {
            Image(systemName: active ? item.sfSymbolFill : item.sfSymbol)
                .font(.system(size: size * 0.88, weight: active ? .semibold : .regular))
                .frame(width: size, height: size)
        }
    }
}

/// Lightweight wrapping flow layout for tactile chips in 书架内收起 & 已隐藏.
private struct FlowWrapLayout: Layout {
    var horizontalSpacing: CGFloat = 8
    var verticalSpacing: CGFloat = 8

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxW = proposal.width ?? .infinity
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowH: CGFloat = 0
        var usedW: CGFloat = 0

        for sub in subviews {
            let sz = sub.sizeThatFits(.unspecified)
            if x > 0 && x + sz.width > maxW {
                y += rowH + verticalSpacing
                x = 0
                rowH = 0
            }
            usedW = max(usedW, x + sz.width)
            rowH = max(rowH, sz.height)
            x += sz.width + horizontalSpacing
        }
        return CGSize(width: proposal.width ?? usedW, height: y + rowH)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        var x = bounds.minX
        var y = bounds.minY
        var rowH: CGFloat = 0

        for sub in subviews {
            let sz = sub.sizeThatFits(.unspecified)
            if x > bounds.minX && x + sz.width > bounds.maxX {
                y += rowH + verticalSpacing
                x = bounds.minX
                rowH = 0
            }
            sub.place(at: CGPoint(x: x, y: y), proposal: ProposedViewSize(sz))
            rowH = max(rowH, sz.height)
            x += sz.width + horizontalSpacing
        }
    }
}
#endif
