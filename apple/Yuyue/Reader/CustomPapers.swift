import SwiftUI
import YuyueCore
#if canImport(UIKit)
import UIKit
#else
import AppKit
#endif

// MARK: - 我的纸

/// The papers the reader made, after the six presets. The colours of each come from the core,
/// so every device draws the same page.
@MainActor @Observable
final class Papers {
    static let shared = Papers()
    /// A paper was made, changed or removed: open pages take it again.
    static let changed = Notification.Name("yuyue.papersChanged")

    private(set) var list: [CustomPaper] = []
    @ObservationIgnored private var tokens: [String: YYPaper] = [:]
    @ObservationIgnored private weak var core: YuyueCore?

    func load(_ core: YuyueCore) {
        self.core = core
        let fresh = (try? core.customPapers()) ?? []
        guard fresh != list else { return }
        list = fresh
        tokens = [:]
        NotificationCenter.default.post(name: Self.changed, object: nil)
    }

    func tokens(for id: String) -> YYPaper? {
        if let t = tokens[id] { return t }
        guard let paper = list.first(where: { $0.id == id }) else { return nil }
        let t = YYPaper(custom: paper)
        tokens[id] = t
        return t
    }

    func paper(_ id: String?) -> CustomPaper? { id.flatMap { id in list.first { $0.id == id } } }

    /// Saves it and returns it as stored.
    @discardableResult
    func save(_ paper: CustomPaper) throws -> CustomPaper? {
        guard let core else { return nil }
        let saved = try core.saveCustomPaper(paper: paper)
        load(core)
        return saved
    }

    func delete(_ id: String) {
        guard let core else { return }
        try? core.deleteCustomPaper(id: id)
        load(core)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }

    /// 撤销 after 删除.
    func restore(_ id: String) {
        guard let core else { return }
        try? core.restoreCustomPaper(id: id)
        load(core)
    }

    func rename(_ paper: CustomPaper, to name: String) {
        var renamed = paper
        renamed.name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !renamed.name.isEmpty, renamed.name != paper.name else { return }
        _ = try? save(renamed)
    }

    /// Starts a new paper from the current settings: a copy of 我的纸, or the made colour closest
    /// to the preset. No name: the core gives it one on 完成.
    func newDraft(from settings: ReaderSettings) -> CustomPaper {
        newDraft(from: PaperAppearance.shared.resolve(settings))
    }

    func newDraft(from resolved: PaperAppearance.Resolved) -> CustomPaper {
        if let mine = resolved.custom {
            return CustomPaper(id: "", name: "", bg: mine.bg, ink: mine.ink, layout: mine.layout, texture: mine.texture)
        }
        let preset = resolved.preset
        let night = preset == .dark || preset == .kamiNight
        let made = paperPalette(night: night).swatches
        let start = night ? made[0] : made[preset == .kami ? 1 : preset == .sage ? 2 : preset == .eink ? 5 : 0]
        return CustomPaper(id: "", name: "", bg: start.color, ink: nil,
                           layout: preset == .plain || preset == .dark ? .plain : .kami,
                           texture: preset == .eink ? .grain : .none)
    }

    /// 复制一张: straight onto the shelf of papers, named 豆沙绿 2 and so on.
    @discardableResult
    func duplicate(_ mine: CustomPaper) -> CustomPaper? {
        var copy = mine
        copy.id = ""
        let base = mine.name.replacingOccurrences(of: #" \d+$"#, with: "", options: .regularExpression)
        let taken = Set(list.map(\.name))
        copy.name = (2...).lazy.map { "\(base) \($0)" }.first { !taken.contains($0) } ?? base
        return try? save(copy)
    }
}

/// Identifies a paper editing session for `.sheet(item:)`, including a new paper with an empty id.
struct PaperDraft: Identifiable {
    let id = UUID()
    let paper: CustomPaper
}

extension YYPaper {
    /// 我的纸: its colours over the typography of the preset it reads like.
    init(custom: CustomPaper) {
        let c = paperColors(paper: custom)
        let preset = c.preset.tokens
        self.init(
            id: custom.id, name: custom.name, isDark: c.dark, hasNoiseTexture: custom.texture != .none,
            bg: Color(hex: c.bg), ink: Color(hex: c.ink), ink2: Color(hex: c.ink2), line: Color(hex: c.line), accent: Color(hex: c.accent),
            hl: Color(hex: c.hl), wave: Color(hex: c.wave), glass: Color(hex: c.glass), main: Color(hex: c.main), onMain: Color(hex: c.onMain),
            paperAccent: Color(hex: c.paperAccent),
            text: preset.text, title: preset.title, meta: preset.meta
        )
    }
}

extension ReaderSettings {
    /// The paper the page is on: 我的纸 when it is one (and still there), else the preset.
    var paperTokens: YYPaper {
        customPaper.flatMap { Papers.shared.tokens(for: $0) } ?? paper.tokens
    }

    /// The paper as the swatches know it: a 我的纸 id, or a preset's.
    var paperChoice: String {
        if let id = customPaper, Papers.shared.paper(id) != nil { return id }
        return paper.tokens.id
    }
}

// MARK: - 纸色跟随外观

/// 纸色跟随外观: pairs light and dark papers with the app's effective appearance (`浅色` / `深色` / `跟随系统`)
/// and remembers the reader's preferred paper for each side without overwriting synced day settings at night.
@MainActor @Observable
final class PaperAppearance {
    static let shared = PaperAppearance()

    static let followKey = "paper.followAppearance"
    static let lightKey = "paper.light"
    static let darkKey = "paper.dark"

    /// A resolved paper ready for both native chrome and WebView HTML/JS.
    struct Resolved: Equatable {
        let preset: Paper
        let custom: CustomPaper?

        var tokens: YYPaper {
            if let custom { return Papers.shared.tokens(for: custom.id) ?? YYPaper(custom: custom) }
            return preset.tokens
        }

        var choiceID: String { custom?.id ?? preset.tokens.id }
        var isDark: Bool { custom.map { paperColors(paper: $0).dark } ?? preset.tokens.isDark }
        var texture: PaperTexture { custom?.texture ?? (preset.tokens.hasNoiseTexture ? .grain : .none) }
    }

    private(set) var follow: Bool
    private(set) var lightID: String?
    private(set) var darkID: String?
    /// The app's effective dark appearance, unaffected by a reader window's `.preferredColorScheme`.
    private(set) var isDark: Bool

    #if os(macOS)
    @ObservationIgnored private var appearanceObserver: NSKeyValueObservation?
    #endif

    private init() {
        let d = UserDefaults.standard
        follow = d.object(forKey: Self.followKey) as? Bool ?? true
        lightID = Self.nonEmpty(d.string(forKey: Self.lightKey))
        darkID = Self.nonEmpty(d.string(forKey: Self.darkKey))
        isDark = Self.currentEffectiveDark()
        #if os(macOS)
        ensureMacObserver()
        #endif
    }

    private static func nonEmpty(_ s: String?) -> String? {
        guard let s, !s.isEmpty else { return nil }
        return s
    }

    static func currentEffectiveDark() -> Bool {
        switch AppAppearance.current {
        case .light: return false
        case .dark: return true
        case .system:
            #if os(macOS)
            if let app = NSApp {
                return app.effectiveAppearance.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua
            }
            return UserDefaults.standard.string(forKey: "AppleInterfaceStyle") == "Dark"
            #else
            let style = UIApplication.shared.connectedScenes
                .compactMap { $0 as? UIWindowScene }
                .first?.traitCollection.userInterfaceStyle
                ?? UIScreen.main.traitCollection.userInterfaceStyle
            return style == .dark
            #endif
        }
    }

    #if os(macOS)
    func ensureMacObserver() {
        guard appearanceObserver == nil, let app = NSApp else { return }
        appearanceObserver = app.observe(\.effectiveAppearance, options: [.new]) { _, _ in
            Task { @MainActor in
                PaperAppearance.shared.refreshAppearance()
            }
        }
    }

    func refreshAppearance() {
        ensureMacObserver()
        setIsDark(Self.currentEffectiveDark())
    }
    #else
    func syncFromRoot(scheme: ColorScheme, mode: AppAppearance) {
        let next: Bool = switch mode {
        case .light: false
        case .dark: true
        case .system: scheme == .dark
        }
        setIsDark(next)
    }
    #endif

    private func setIsDark(_ next: Bool) {
        guard next != isDark else { return }
        isDark = next
        if follow {
            NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
        }
    }

    /// Re-reads `paper.followAppearance` after an undo in 设置历史.
    func reloadFromDefaults() {
        let d = UserDefaults.standard
        let next = d.object(forKey: Self.followKey) as? Bool ?? true
        if next != follow {
            follow = next
            NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
        }
    }

    func raw(_ settings: ReaderSettings) -> Resolved {
        if let id = settings.customPaper, let mine = Papers.shared.paper(id) {
            return Resolved(preset: paperColors(paper: mine).preset, custom: mine)
        }
        return Resolved(preset: settings.paper, custom: nil)
    }

    func resolve(_ settings: ReaderSettings) -> Resolved {
        let base = raw(settings)
        guard follow else { return base }
        if base.isDark == isDark { return base }
        let savedID = isDark ? darkID : lightID
        if let savedID, let saved = resolveID(savedID), saved.isDark == isDark {
            return saved
        }
        return Resolved(preset: Self.pairedPreset(for: base, dark: isDark), custom: nil)
    }

    private func resolveID(_ id: String) -> Resolved? {
        if let preset = Paper(id: id) {
            return Resolved(preset: preset, custom: nil)
        }
        if let mine = Papers.shared.paper(id) {
            return Resolved(preset: paperColors(paper: mine).preset, custom: mine)
        }
        return nil
    }

    /// Mirrors `core/src/paper.rs`'s `preset(layout, dark)`: Plain/Dark keep Sans; Kami/Sage/Eink keep Serif.
    nonisolated static func pairedPreset(for base: Resolved, dark: Bool) -> Paper {
        let layout = base.custom?.layout ?? base.preset
        switch (layout, dark) {
        case (.plain, false), (.dark, false): return .plain
        case (.plain, true), (.dark, true): return .dark
        case (_, false): return .kami
        case (_, true): return .kamiNight
        }
    }

    /// Picks a preset paper from Aa or 设置 · 阅读.
    func pick(preset: Paper, in settings: ReaderSettings, apply: ((inout ReaderSettings) -> Void) -> Void) {
        pick(Resolved(preset: preset, custom: nil), in: settings, apply: apply)
    }

    /// Picks a custom paper (我的纸) from Aa or 设置 · 阅读.
    func pick(custom: CustomPaper, in settings: ReaderSettings, apply: ((inout ReaderSettings) -> Void) -> Void) {
        let preset = paperColors(paper: custom).preset
        pick(Resolved(preset: preset, custom: custom), in: settings, apply: apply)
    }

    private func pick(_ chosen: Resolved, in settings: ReaderSettings, apply: ((inout ReaderSettings) -> Void) -> Void) {
        let base = raw(settings)
        remember(chosen.choiceID, dark: chosen.isDark)

        if follow {
            if chosen.isDark == isDark {
                // Same polarity as current appearance: stay on 跟随外观.
                if base.isDark == chosen.isDark {
                    apply { $0.paper = chosen.preset; $0.customPaper = chosen.custom?.id }
                } else if !isDark {
                    // Daytime choice: preserve prior dark base if none saved yet, then sync daytime choice.
                    if darkID == nil { remember(base.choiceID, dark: true) }
                    apply { $0.paper = chosen.preset; $0.customPaper = chosen.custom?.id }
                } else {
                    // Nighttime choice while base is light: keep synced daytime paper intact.
                    NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
                }
            } else {
                // Opposite polarity tapped: honor immediately and turn off 跟随外观.
                if base.isDark == isDark {
                    remember(base.choiceID, dark: isDark)
                }
                setFollowRaw(false)
                apply { $0.paper = chosen.preset; $0.customPaper = chosen.custom?.id }
            }
        } else {
            apply { $0.paper = chosen.preset; $0.customPaper = chosen.custom?.id }
        }
    }

    /// Turns 跟随外观 on or off. Turning off freezes the currently visible paper so the screen never flashes.
    func setFollow(_ on: Bool, in settings: ReaderSettings, apply: ((inout ReaderSettings) -> Void) -> Void) {
        guard on != follow else { return }
        if !on {
            let visible = resolve(settings)
            let base = raw(settings)
            if base.isDark != visible.isDark {
                remember(base.choiceID, dark: base.isDark)
            }
            setFollowRaw(false)
            if visible.preset != settings.paper || visible.custom?.id != settings.customPaper {
                apply { $0.paper = visible.preset; $0.customPaper = visible.custom?.id }
            } else {
                NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
            }
        } else {
            setFollowRaw(true)
            NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
        }
    }

    /// Clears a deleted custom paper from the remembered light/dark slots.
    func forget(_ id: String) {
        let d = UserDefaults.standard
        var changed = false
        if lightID == id {
            lightID = nil
            d.removeObject(forKey: Self.lightKey)
            changed = true
        }
        if darkID == id {
            darkID = nil
            d.removeObject(forKey: Self.darkKey)
            changed = true
        }
        if changed {
            NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
        }
    }

    private func remember(_ id: String, dark: Bool) {
        let d = UserDefaults.standard
        if dark {
            guard darkID != id else { return }
            darkID = id
            d.set(id, forKey: Self.darkKey)
        } else {
            guard lightID != id else { return }
            lightID = id
            d.set(id, forKey: Self.lightKey)
        }
    }

    private func setFollowRaw(_ on: Bool) {
        follow = on
        UserDefaults.standard.set(on, forKey: Self.followKey)
    }

    /// Rewrites the opening `<html ...>` attributes before the WebView paints its first frame
    /// so opening a book at night never flashes white.
    func applyToHTML(_ body: String, resolved: Resolved) -> String {
        Self.applyToHTML(
            body,
            presetID: resolved.preset.tokens.id,
            customStyle: resolved.custom.map { paperStyle(paper: $0) },
            customTexture: resolved.custom?.texture.css
        )
    }

    nonisolated static func applyToHTML(_ body: String, presetID: String, customStyle: String?, customTexture: String?) -> String {
        guard let htmlStart = body.range(of: "<html "),
              let htmlEnd = body[htmlStart.lowerBound...].firstIndex(of: ">") else {
            return body
        }
        var tag = String(body[htmlStart.lowerBound ..< htmlEnd])
        guard tag.contains("data-yy-paper=") else { return body }

        tag = tag.replacingOccurrences(
            of: #"data-yy-paper="[^"]*""#,
            with: #"data-yy-paper="\#(presetID)""#,
            options: .regularExpression
        )
        tag = tag.replacingOccurrences(
            of: #" data-yy-texture="[^"]*""#,
            with: "",
            options: .regularExpression
        )
        if let customTexture, !customTexture.isEmpty, let styleRange = tag.range(of: " style=\"") {
            tag.insert(contentsOf: #" data-yy-texture="\#(customTexture)""#, at: styleRange.lowerBound)
        }
        if let styleRange = tag.range(of: #"style="[^"]*""#, options: .regularExpression) {
            var styleAttr = String(tag[styleRange])
            if let customRange = styleAttr.range(of: ";--yy-p-bg:") {
                styleAttr.replaceSubrange(customRange.lowerBound ..< styleAttr.index(before: styleAttr.endIndex), with: "")
            }
            if let customStyle, !customStyle.isEmpty {
                let closeQuote = styleAttr.index(before: styleAttr.endIndex)
                styleAttr.insert(contentsOf: ";" + customStyle, at: closeQuote)
            }
            tag.replaceSubrange(styleRange, with: styleAttr)
        }
        return body.replacingCharacters(in: htmlStart.lowerBound ..< htmlEnd, with: tag)
    }
}

extension Color {
    /// `#RRGGBB` or `#RRGGBBAA`.
    init(hex: String) {
        var v: UInt64 = 0
        let h = hex.trimmingCharacters(in: CharacterSet(charactersIn: "#"))
        Scanner(string: h).scanHexInt64(&v)
        let alpha = h.count == 8
        if alpha { self.init(yy: YYRGBA(Double(v >> 24 & 255), Double(v >> 16 & 255), Double(v >> 8 & 255), Double(v & 255) / 255)) }
        else { self.init(yy: YYRGBA(Double(v >> 16 & 255), Double(v >> 8 & 255), Double(v & 255))) }
    }

    /// `#RRGGBB` in sRGB.
    var hex: String {
        #if canImport(UIKit)
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        UIColor(self).getRed(&r, green: &g, blue: &b, alpha: &a)
        #else
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .white
        let (r, g, b) = (c.redComponent, c.greenComponent, c.blueComponent)
        #endif
        let byte = { (v: CGFloat) in Int((min(max(v, 0), 1) * 255).rounded()) }
        return String(format: "#%02X%02X%02X", byte(r), byte(g), byte(b))
    }
}

// MARK: - 调纸

/// 调纸: 白天 or 夜里, six made colours for it, 微调 within bounds
/// that stay restful (色调、浓淡、深浅), a texture, and the text: picked for the colour or one of
/// four inks. Everything it can reach reads well, so there is nothing to warn about. No name to
/// type: the core names a new paper after the colour it is closest to. The page behind changes as
/// it is set; 取消 (or closing the card) puts it back.
struct PaperEditor: View {
    private let backTitle: String
    private let onPreview: (CustomPaper?) -> Void
    @State var draft: CustomPaper
    /// Done (saved, the page on it) or cancelled (nil).
    let finish: (CustomPaper?) -> Void
    @State private var night: Bool
    @State private var hsl: PaperHsl
    @State private var tuning = false
    @State private var doneHover = false
    @State private var failure: String?
    @State private var previewTask: Task<Void, Never>?

    init(model: ReaderModel, draft: CustomPaper, finish: @escaping (CustomPaper?) -> Void) {
        self.init(draft: draft, backTitle: String(localized: "排版"), onPreview: { model.previewPaper($0) }, finish: finish)
    }

    init(draft: CustomPaper, backTitle: String = String(localized: "取消"), onPreview: @escaping (CustomPaper?) -> Void = { _ in }, finish: @escaping (CustomPaper?) -> Void) {
        self.backTitle = backTitle
        self.onPreview = onPreview
        self.finish = finish
        _draft = State(initialValue: draft)
        _night = State(initialValue: paperColors(paper: draft).dark)
        _hsl = State(initialValue: paperHsl(color: draft.bg))
    }

    private var palette: PaperPalette { paperPalette(night: night) }
    private var colors: PaperColors { paperColors(paper: draft) }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            // `.ed-top`
            HStack {
                Button { onPreview(nil); finish(nil) } label: {
                    HStack(spacing: 2) {
                        Image(systemName: "chevron.left").font(.yy(size: 11, weight: .semibold))
                        Text(backTitle)
                    }
                    .foregroundStyle(YYColor.t2)
                    .contentShape(.rect)
                }
                .help(Text("取消"))
                Spacer()
                Text(draft.id.isEmpty ? String(localized: "调纸") : String(localized: "调整“\(draft.name)”"))
                    .font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                Spacer()
                Button { done() } label: {
                    // A light grey capsule, not the dark fill: the papers are what's bright on this card.
                    Text("完成")
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .padding(.horizontal, 14).frame(height: 26)
                        .background(YYColor.inset, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                        .brightness(doneHover ? -0.03 : 0)
                        .contentShape(.capsule)
                }
                .onHover { doneHover = $0 }
                .keyboardShortcut(.defaultAction)
            }
            .font(.yy(size: 12))
            .buttonStyle(.plain)

            Segments(items: [(false, String(localized: "白天")), (true, String(localized: "夜里"))],
                     selection: Binding(get: { night }, set: { switchSide($0) }), small: true) { _, t in Text(t) }

            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 6), count: 6), spacing: 8) {
                ForEach(palette.swatches, id: \.color) { swatch in
                    let made = CustomPaper(id: "", name: swatch.name, bg: swatch.color, ink: draft.ink, layout: draft.layout, texture: draft.texture)
                    PaperTile(paper: YYPaper(custom: made), texture: draft.texture, name: swatch.name, selected: draft.bg == swatch.color, small: true) {
                        hsl = paperHsl(color: swatch.color)
                        set { $0.bg = swatch.color }
                    }
                }
            }

            // `.grp`
            VStack(spacing: 0) {
                Button { withAnimation(YYMotion.layout) { tuning.toggle() } } label: {
                    HStack {
                        Text("微调颜色").foregroundStyle(YYColor.t1)
                        Spacer()
                        Text("色调、浓淡、深浅").foregroundStyle(YYColor.t3).font(.yy(size: 10.5))
                        Image(systemName: "chevron.right")
                            .font(.yy(size: 9, weight: .semibold)).foregroundStyle(YYColor.t3)
                            .rotationEffect(.degrees(tuning ? 90 : 0))
                    }
                    .font(.yy(size: 12))
                    .frame(height: 38)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                if tuning {
                    VStack(spacing: 2) {
                        tune(String(localized: "色调"), \.h, 0 ... 359, track: stride(from: 0.0, through: 360, by: 60).map { swatchColor(h: $0, s: max(hsl.s, 30)) })
                        tune(String(localized: "浓淡"), \.s, 0 ... palette.saturationMax, track: [swatchColor(s: 0), swatchColor(s: palette.saturationMax)])
                        // 深浅: light on the left on both sides, dark on the right.
                        tune(String(localized: "深浅"), \.l, palette.lightnessMin ... palette.lightnessMax,
                             track: [swatchColor(l: palette.lightnessMax), swatchColor(l: palette.lightnessMin)], reversed: true)
                    }
                    .padding(.bottom, 8)
                    .transition(.opacity)
                }
                row(String(localized: "纹理")) {
                    HStack(spacing: 6) {
                        ForEach([PaperTexture.none, .grain, .xuan, .linen], id: \.self) { t in
                            let on = draft.texture == t
                            Button { set { $0.texture = t } } label: {
                                Color(hex: colors.bg)
                                    .overlay { PaperTextureView(texture: t, dark: colors.dark) }
                                    .frame(width: 34, height: 26)
                                    .clipShape(.rect(cornerRadius: 7))
                                    .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(.black.opacity(0.14), lineWidth: 0.5))
                                    .padding(2)
                                    .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(on ? YYColor.t1 : .clear, lineWidth: 1.5))
                            }
                            .buttonStyle(.plain)
                            .help(Text(t.title))
                            .accessibilityLabel(Text(t.title))
                            .accessibilityAddTraits(on ? .isSelected : [])
                        }
                    }
                }
                row(String(localized: "字色")) {
                    HStack(spacing: 8) {
                        Button { set { $0.ink = nil } } label: {
                            Text("自动")
                                .font(.yy(size: 9.5, weight: .semibold))
                                .foregroundStyle(draft.ink == nil ? YYColor.t1 : YYColor.t2)
                                .padding(.horizontal, 8).frame(height: 22)
                                .background(YYColor.inset, in: .capsule)
                                .padding(1.5)
                                .overlay(Capsule().strokeBorder(draft.ink == nil ? YYColor.t1 : .clear, lineWidth: 1.5))
                        }
                        ForEach(palette.inks, id: \.color) { ink in
                            let on = draft.ink == ink.color
                            Button { set { $0.ink = ink.color } } label: {
                                Text(ink.name)
                                    .font(.yy(size: 9.5, weight: .semibold))
                                    .foregroundStyle(Color(hex: ink.color))
                                    .frame(width: 22, height: 22)
                                    .background(Color(hex: colors.bg), in: .circle)
                                    .overlay(Circle().strokeBorder(.black.opacity(0.18), lineWidth: 0.5))
                                    .padding(1.5)
                                    .overlay(Circle().strokeBorder(on ? YYColor.t1 : .clear, lineWidth: 1.5))
                            }
                            .help(Text(ink.name))
                            .accessibilityLabel(Text(ink.name))
                        }
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 12)
            .background(YYColor.card, in: .rect(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))

            if let failure {
                Text(failure).font(.yy(size: 11)).foregroundStyle(YYColor.danger)
            }
        }
        .onAppear { preview() }
        // Closed without 完成 (a click outside the card, the sheet pulled down): as 取消.
        .onDisappear { onPreview(nil) }
    }

    private func row(_ title: String, @ViewBuilder _ content: () -> some View) -> some View {
        HStack {
            Text(title).font(.yy(size: 12)).foregroundStyle(YYColor.t1)
            Spacer()
            content()
        }
        .frame(minHeight: 40)
        .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
    }

    private func tune(_ title: String, _ key: WritableKeyPath<PaperHsl, Double>, _ range: ClosedRange<Double>, track: [Color], reversed: Bool = false) -> some View {
        HStack(spacing: 10) {
            Text(title).font(.yy(size: 11)).foregroundStyle(YYColor.t2).fixedSize().frame(minWidth: 30, alignment: .leading)
            TrackSlider(value: Binding(get: { hsl[keyPath: key] }, set: { v in
                hsl[keyPath: key] = v
                let bg = paperColor(hsl: hsl, night: night)
                draft.bg = bg
                schedulePreview()
            }), range: range, track: track, thumb: Color(hex: colors.bg), reversed: reversed, label: title)
        }
        #if os(macOS)
        .frame(height: 30)
        #else
        .frame(minHeight: 44)
        #endif
    }

    /// The colour 微调 would land on with one part changed: for the slider tracks.
    private func swatchColor(h: Double? = nil, s: Double? = nil, l: Double? = nil) -> Color {
        Color(hex: paperColor(hsl: PaperHsl(h: h ?? hsl.h, s: s ?? hsl.s, l: l ?? hsl.l), night: night))
    }

    /// 白天 ↔ 夜里: the first made colour of that side, its text picked for it.
    private func switchSide(_ toNight: Bool) {
        guard toNight != night, let first = paperPalette(night: toNight).swatches.first else { return }
        night = toNight
        hsl = paperHsl(color: first.color)
        set { $0.bg = first.color; $0.ink = nil }
    }

    private func set(_ change: (inout CustomPaper) -> Void) {
        change(&draft)
        preview()
    }

    private func preview() {
        previewTask?.cancel()
        previewTask = nil
        onPreview(draft)
    }

    private func schedulePreview() {
        guard previewTask == nil else { return }
        previewTask = Task {
            try? await Task.sleep(for: .milliseconds(40))
            previewTask = nil
            guard !Task.isCancelled else { return }
            onPreview(draft)
        }
    }

    private func done() {
        previewTask?.cancel()
        previewTask = nil
        do {
            let saved = try Papers.shared.save(draft)
            onPreview(nil)
            finish(saved)
        } catch {
            failure = Library.message(for: error)
        }
    }
}

extension PaperTexture {
    /// `data-yy-texture` (reader contract §2).
    var css: String? {
        switch self {
        case .none: nil
        case .grain: "grain"
        case .xuan: "xuan"
        case .linen: "linen"
        }
    }

    var title: String {
        switch self {
        case .none: String(localized: "无纹理")
        case .grain: String(localized: "细颗粒")
        case .xuan: String(localized: "宣纸")
        case .linen: String(localized: "亚麻")
        }
    }
}

/// A paper as a small tile with 文 in its ink, its texture on it.
struct PaperTile: View {
    let paper: YYPaper
    var texture: PaperTexture = .none
    let name: String
    let selected: Bool
    var small = false
    let pick: () -> Void

    var body: some View {
        Button(action: pick) {
            VStack(spacing: 6) {
                Text("文")
                    .font(YYUI.serif(15))
                    .foregroundStyle(paper.ink)
                    // 调纸's six to a row share the card's width; a fixed 44 made them touch.
                    .frame(width: small ? nil : 46, height: small ? 34 : 38)
                    .frame(maxWidth: small ? .infinity : nil)
                    .background { paper.bg.overlay { PaperTextureView(texture: texture, dark: paper.isDark) } }
                    .clipShape(.rect(cornerRadius: 10))
                    .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: paper.isDark ? 0 : 0.5))
                    .padding(2)
                    .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(selected ? YYColor.t1 : .clear, lineWidth: 1.5))
                Text(name)
                    .font(.yy(size: 10.5, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.5)
                    .frame(maxWidth: 50)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(Text(name))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// 纹理 drawn natively, to look like the page's (reader.css `data-yy-texture`).
/// Pre-rendered once per `(texture, dark)` into a small tileable CGImage instead of issuing thousands
/// of Canvas fill calls on every layout or slider frame.
struct PaperTextureView: View {
    let texture: PaperTexture
    let dark: Bool

    var body: some View {
        if texture != .none, let tile = Self.tile(texture: texture, dark: dark) {
            Image(decorative: tile, scale: 2)
                .resizable(resizingMode: .tile)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }

    private struct Key: Hashable {
        let texture: PaperTexture
        let dark: Bool
    }

    private static var tiles: [Key: CGImage] = [:]

    private static func tile(texture: PaperTexture, dark: Bool) -> CGImage? {
        let key = Key(texture: texture, dark: dark)
        if let hit = tiles[key] { return hit }
        let ptSide = 120
        let scale = 2
        let pxSide = ptSide * scale
        let colorSpace = CGColorSpaceCreateDeviceRGB()
        guard let ctx = CGContext(
            data: nil,
            width: pxSide,
            height: pxSide,
            bitsPerComponent: 8,
            bytesPerRow: 0,
            space: colorSpace,
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }
        ctx.scaleBy(x: CGFloat(scale), y: CGFloat(scale))
        let w = Double(ptSide), h = Double(ptSide)
        var seed: UInt64 = 0x9E3779B97F4A7C15
        func next() -> Double {
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            return Double(seed >> 33) / Double(1 << 31)
        }
        switch texture {
        case .none:
            return nil
        case .grain:
            ctx.setFillColor(dark ? CGColor(gray: 1, alpha: 0.07) : CGColor(gray: 0, alpha: 0.09))
            for _ in 0 ..< Int(w * h / 7) {
                ctx.fill(CGRect(x: next() * w, y: next() * h, width: 0.8, height: 0.8))
            }
        case .xuan:
            ctx.setStrokeColor(CGColor(red: 0.5, green: 0.44, blue: 0.32, alpha: dark ? 0.16 : 0.11))
            ctx.setLineWidth(0.6)
            for _ in 0 ..< Int(w * h / 90) {
                let x = next() * w, y = next() * h, len = 6 + next() * 22
                let dy = (next() - 0.5) * 2
                ctx.move(to: CGPoint(x: x, y: y))
                ctx.addLine(to: CGPoint(x: x + len, y: y + dy))
            }
            ctx.strokePath()
        case .linen:
            ctx.setFillColor(CGColor(gray: 0.5, alpha: 0.09))
            for x in stride(from: 0.0, to: w, by: 3) { ctx.fill(CGRect(x: x, y: 0, width: 0.6, height: h)) }
            for y in stride(from: 0.0, to: h, by: 3) { ctx.fill(CGRect(x: 0, y: y, width: w, height: 0.6)) }
        }
        guard let image = ctx.makeImage() else { return nil }
        tiles[key] = image
        return image
    }
}

/// A slider on a coloured track (`.sl input`): the track shows where it goes, the knob is the colour.
struct TrackSlider: View {
    @Binding var value: Double
    let range: ClosedRange<Double>
    let track: [Color]
    let thumb: Color
    var reversed = false
    let label: String

    var body: some View {
        GeometryReader { g in
            let w = max(g.size.width - 20, 1)
            let t = (value - range.lowerBound) / max(range.upperBound - range.lowerBound, 1e-9)
            ZStack(alignment: .leading) {
                Capsule()
                    .fill(LinearGradient(colors: track, startPoint: .leading, endPoint: .trailing))
                    .frame(height: 6)
                    .padding(.horizontal, 10)
                Circle()
                    .fill(thumb)
                    .overlay(Circle().strokeBorder(.white, lineWidth: 2.5))
                    .shadow(color: .black.opacity(0.25), radius: 2, y: 1)
                    .frame(width: 20, height: 20)
                    .offset(x: (reversed ? 1 - t : t) * w)
            }
            .frame(maxHeight: .infinity)
            .contentShape(.rect)
            #if os(macOS)
            .gesture(DragGesture(minimumDistance: 0).onChanged { drag in
                seek(at: drag.location.x, width: w)
            })
            #else
            .onTapGesture { point in
                seek(at: point.x, width: w)
            }
            .gesture(DragGesture(minimumDistance: 8).onChanged { drag in
                seek(at: drag.location.x, width: w)
            })
            #endif
        }
        #if os(macOS)
        .frame(height: 26)
        #else
        .frame(height: 44)
        #endif
        .accessibilityElement()
        .accessibilityLabel(Text(label))
        .accessibilityValue(Text("\(Int(((value - range.lowerBound) / max(range.upperBound - range.lowerBound, 1e-9) * 100).rounded()))%"))
        .accessibilityAdjustableAction { direction in
            let step = (range.upperBound - range.lowerBound) / 20
            value = min(max(value + (direction == .increment ? step : -step), range.lowerBound), range.upperBound)
        }
    }

    private func seek(at x: CGFloat, width w: CGFloat) {
        let p = min(max((x - 10) / w, 0), 1)
        value = range.lowerBound + (reversed ? 1 - p : p) * (range.upperBound - range.lowerBound)
    }
}
