import SwiftUI

/// 显示语言: follow the system, or 中文 / English for 语阅 alone.
///
/// Kept where macOS and iOS keep a per-app language (`AppleLanguages` in the app's own defaults,
/// the same setting as System Settings › the app › Language), so both places agree. The strings
/// are picked when the app starts, so a change shows after it opens again: on the Mac 语阅 reopens
/// itself and the settings window comes back with an ink line sweeping the old words into the new;
/// on the iPhone the system relaunches it, and the first screen gets the same sweep.
enum AppLanguage: String, CaseIterable, Identifiable {
    case system, zh, en

    var id: String { rawValue }

    private static let key = "AppleLanguages"
    /// The language the app last opened in, to know when it changed.
    private static let lastKey = "language.last"

    /// What was chosen.
    static var chosen: AppLanguage {
        guard let bundle = Bundle.main.bundleIdentifier,
              let langs = UserDefaults.standard.persistentDomain(forName: bundle)?[key] as? [String],
              let first = langs.first else { return .system }
        return first.hasPrefix("en") ? .en : .zh
    }

    static func choose(_ l: AppLanguage) {
        switch l {
        case .system: UserDefaults.standard.removeObject(forKey: key)
        case .zh: UserDefaults.standard.set(["zh-Hans"], forKey: key)
        case .en: UserDefaults.standard.set(["en"], forKey: key)
        }
    }

    /// The language the app is showing now.
    static var running: AppLanguage {
        Bundle.main.preferredLocalizations.first?.hasPrefix("en") == true ? .en : .zh
    }

    /// The system's own language, as far as 语阅 has it.
    static var systemLanguage: AppLanguage {
        let global = UserDefaults(suiteName: UserDefaults.globalDomain)?.array(forKey: key) as? [String] ?? Locale.preferredLanguages
        let pick = Bundle.preferredLocalizations(from: Bundle.main.localizations.filter { $0 != "Base" }, forPreferences: global).first
        return pick?.hasPrefix("en") == true ? .en : .zh
    }

    /// The language this choice shows.
    var effective: AppLanguage { self == .system ? Self.systemLanguage : self }

    var displayName: String {
        switch self {
        case .system: String(localized: "跟随系统")
        case .zh: "中文"
        case .en: "English"
        }
    }

    static var systemDisplayName: String {
        systemLanguage == .en ? "English" : "中文"
    }

    /// Title shown in the restart confirmation alert on macOS and iOS.
    @MainActor static func restartPromptTitle(for target: AppLanguage) -> String {
        let label: String = switch target {
        case .system:
            String(localized: "跟随系统（\(systemDisplayName)）")
        case .zh:
            "中文"
        case .en:
            "English"
        }
        return String(localized: "切换为 \(label)？")
    }

    /// Opened in another language than last time: the first screen sweeps it in. Asked once a launch.
    static let changedSinceLast: Bool = {
        let now = running.rawValue
        let last = UserDefaults.standard.string(forKey: lastKey)
        UserDefaults.standard.set(now, forKey: lastKey)
        return last != nil && last != now
    }()
}

/// The ink line: a soft green edge moving left to right (or back), the old picture on the far side of it.
struct InkSweep: View {
    /// What was there before; nil sweeps over nothing (only the line passes).
    let old: Image?
    var forward = true
    /// How wide the green edge is: the whole window takes 140, one tile less.
    var band: CGFloat = 140
    /// Held still this long first (the old 语阅 is still going away).
    var delay: Double = 0
    var done: () -> Void = {}
    @State private var x: CGFloat = 0
    @State private var gone = false

    var body: some View {
        GeometryReader { g in
            let w = g.size.width
            let edge = forward ? x * (w + band + 20) - band : w + 20 - x * (w + band + 20)
            ZStack(alignment: .topLeading) {
                if let old {
                    old.resizable().frame(width: w, height: g.size.height)
                        .mask(alignment: .leading) {
                            Rectangle()
                                .frame(width: forward ? max(0, w - edge - band + 20) : max(0, edge + 20))
                                .frame(maxWidth: .infinity, alignment: forward ? .trailing : .leading)
                        }
                }
                LinearGradient(stops: [.init(color: .clear, location: 0), .init(color: YYTint.standard.fill.opacity(0.07), location: 0.7),
                                       .init(color: YYTint.standard.fill.opacity(0.3), location: 0.98), .init(color: .clear, location: 1)],
                               startPoint: forward ? .leading : .trailing, endPoint: forward ? .trailing : .leading)
                    .frame(width: band)
                    .offset(x: edge)
            }
            .opacity(gone ? 0 : 1)
        }
        .allowsHitTesting(false)
        .onAppear {
            // 减弱动态效果: no sweep, the new words are simply there.
            if YYMotion.reduce { x = 1; gone = true; done(); return }
            withAnimation(.timingCurve(0.4, 0, 0.3, 1, duration: 0.65).delay(delay)) { x = 1 } completion: {
                withAnimation(.easeOut(duration: 0.2)) { gone = true } completion: { done() }
            }
        }
    }
}

#if os(macOS)
import AppKit

extension Notification.Name {
    /// 现在重开 under 语言.
    static let relaunchForLanguage = Notification.Name("yuyue.relaunchForLanguage")
    /// Ask the user to confirm restarting 语阅 in a new language.
    static let confirmLanguageRelaunch = Notification.Name("yuyue.confirmLanguageRelaunch")
}

extension AppLanguage {
    private static var snapshotURL: URL { FileManager.default.temporaryDirectory.appending(path: "yuyue-language-sweep.png") }
    private static let pendingKey = "language.sweepSettings"
    private static let frameKey = "language.sweepFrame"
    private static let forwardKey = "language.sweepForward"
    /// Changes the language the usual way, quit and open again, without the window going anywhere:
    /// a picture of the settings window and its place are kept, this 语阅 quits, and the one opened
    /// after it puts its settings window back in the same place under that picture. The ink line then
    /// sweeps the picture away.
    @MainActor static func handOver(_ window: NSWindow, to language: AppLanguage) {
        if let view = window.contentView, let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) {
            view.cacheDisplay(in: view.bounds, to: rep)
            if let png = rep.representation(using: .png, properties: [:]) { try? png.write(to: snapshotURL) }
        }
        let d = UserDefaults.standard
        d.set(true, forKey: pendingKey)
        d.set(NSStringFromRect(window.frame), forKey: frameKey)
        // Into English it reads left to right; back into 中文 the line comes the other way.
        d.set(language == .en, forKey: forwardKey)
        d.synchronize()
        // Opened again only once this one is gone, so there's never a second 语阅 in the Dock.
        let reopen = Process()
        reopen.executableURL = URL(filePath: "/bin/sh")
        reopen.arguments = ["-c", "while /bin/kill -0 \(ProcessInfo.processInfo.processIdentifier) 2>/dev/null; do /bin/sleep 0.1; done; /usr/bin/open \"$0\"", Bundle.main.bundlePath]
        do {
            try reopen.run()
        } catch {
            // Can't open it again: the new language comes with the next launch.
            for key in [pendingKey, frameKey, forwardKey] { d.removeObject(forKey: key) }
            try? FileManager.default.removeItem(at: snapshotURL)
            return
        }
        NSApp.terminate(nil)
    }

    /// Opened again after a language change: the picture to sweep from, read once.
    @MainActor static let pendingSweep: (picture: NSImage, forward: Bool)? = {
        guard UserDefaults.standard.bool(forKey: pendingKey), let picture = NSImage(contentsOf: snapshotURL) else { return nil }
        try? FileManager.default.removeItem(at: snapshotURL)
        return (picture, UserDefaults.standard.bool(forKey: forwardKey))
    }()

    /// The new settings window is where the old one was, under its picture.
    @MainActor static func takeOver(_ window: NSWindow) {
        let d = UserDefaults.standard
        if let s = d.string(forKey: frameKey) { window.setFrame(NSRectFromString(s), display: true) }
        for key in [pendingKey, frameKey, forwardKey] { d.removeObject(forKey: key) }
        NSApp.activate()
        window.makeKeyAndOrderFront(nil)
    }

    @MainActor static var settingsPending: Bool { UserDefaults.standard.bool(forKey: pendingKey) }

    /// Waits until any attached sheet (such as the native confirmation `.alert`) finishes dismissing
    /// and the window chrome settles before snapshotting and handing over to the relaunched process.
    @MainActor static func handOverWhenClear(_ window: NSWindow, to language: AppLanguage, attemptsRemaining: Int = 20) {
        let target = window.sheetParent ?? window
        if target.attachedSheet != nil, attemptsRemaining > 0 {
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) {
                handOverWhenClear(target, to: language, attemptsRemaining: attemptsRemaining - 1)
            }
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.08) {
            handOver(target, to: language)
        }
    }
}

/// Hands the settings window to `AppLanguage.takeOver` once it is on screen.
struct LanguageTakeOver: NSViewRepresentable {
    final class Host: NSView {
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window else { return }
            DispatchQueue.main.async { AppLanguage.takeOver(window) }
        }
    }
    func makeNSView(context: Context) -> Host { Host() }
    func updateNSView(_ nsView: Host, context: Context) {}
}

/// 设置 › 通用 › 语言: 高定纸墨下拉 merged into the first row of the main 通用 card.
/// Picking a language with a different effective locale opens a system-style confirmation modal
/// before restarting 语阅 and sweeping the words into the new language.
struct MacLanguageCard: View {
    @Binding var choice: AppLanguage
    @Binding var later: Bool
    @Binding var menuOpen: Bool
    var onTriggerBottomChange: (CGFloat) -> Void = { _ in }
    @State private var triggerHover = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        HStack(alignment: .center, spacing: 16) {
            VStack(alignment: .leading, spacing: 2) {
                Text("语言").font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.t1)
                Text(subtitle)
                    .font(.yy(size: 10.5))
                    .lineSpacing(2)
                    .foregroundStyle(later ? tint.accent : YYColor.t3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 12)
            Button {
                withAnimation(YYMotion.quick) {
                    menuOpen.toggle()
                }
            } label: {
                HStack(spacing: 8) {
                    LanguageSealBadge(language: choice, onTrigger: true)
                    Text(Self.name(choice))
                        .font(.yy(size: 12.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    if choice == .system {
                        Text(Self.systemDisplayName)
                            .font(.yy(size: 10.5, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .lineLimit(1)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1.5)
                            .background(systemTagFill, in: .capsule)
                    }
                    Spacer(minLength: 4)
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.yy(size: 9.5, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                }
                .padding(.leading, 6)
                .padding(.trailing, 10)
                .frame(width: 180, height: 32)
                .background(triggerFill, in: .rect(cornerRadius: 9, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .strokeBorder(triggerBorder, lineWidth: menuOpen ? 1 : 0.5)
                }
                .shadow(color: .black.opacity(scheme == .dark ? 0.22 : 0.03), radius: 1.5, y: 1)
                .contentShape(.rect(cornerRadius: 9, style: .continuous))
            }
            .buttonStyle(YYPressStyle(scale: 0.98))
            .onHover { triggerHover = $0 }
            .onGeometryChange(for: CGFloat.self) { proxy in
                proxy.frame(in: .named("generalSettings")).maxY
            } action: { bottom in
                onTriggerBottomChange(bottom)
            }
            .animation(YYMotion.quick, value: triggerHover)
            .animation(YYMotion.quick, value: menuOpen)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 10)
        .frame(minHeight: 46)
        .animation(YYMotion.standard, value: later)
    }

    private var triggerFill: Color {
        if scheme == .dark {
            if menuOpen || triggerHover {
                return Color(yy: YYRGBA(48, 50, 55))
            }
            return YYColor.inset
        }
        if menuOpen {
            return YYColor.card
        }
        if triggerHover {
            return Color(yy: YYRGBA(241, 237, 229))
        }
        return YYColor.bg
    }

    private var triggerBorder: Color {
        if menuOpen {
            return tint.edge
        }
        return scheme == .dark ? Color.white.opacity(0.10) : Color(yy: YYRGBA(80, 65, 45, 0.14))
    }

    private var systemTagFill: Color {
        scheme == .dark ? Color.white.opacity(0.09) : Color(yy: YYRGBA(42, 43, 46, 0.06))
    }

    private var subtitle: String {
        if later {
            return String(localized: "正在朗读或生成有声书，下次启动时生效")
        }
        if choice == .system {
            return AppLanguage.systemLanguage == .en
                ? String(localized: "当前：English · 切换需确认重开，并回到当前页")
                : String(localized: "当前：中文 · 切换需确认重开，并回到当前页")
        }
        return String(localized: "切换需确认重开语阅，并回到当前页")
    }

    static var systemDisplayName: String { AppLanguage.systemDisplayName }

    static func name(_ l: AppLanguage) -> String { l.displayName }
}
#endif

/// The floating warm-paper dropdown menu without a bulky NSPopover arrow.
struct LanguagePaperDropdownMenu: View {
    let selected: AppLanguage
    let pick: (AppLanguage) -> Void
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            LanguagePaperDropdownRow(
                language: .zh,
                title: "中文",
                detail: String(localized: "简体中文界面"),
                isSelected: selected == .zh,
                tint: tint
            ) {
                pick(.zh)
            }
            LanguagePaperDropdownRow(
                language: .en,
                title: "English",
                detail: "English interface",
                isSelected: selected == .en,
                tint: tint
            ) {
                pick(.en)
            }
            Rectangle()
                .fill(YYColor.line)
                .frame(height: 0.5)
                .padding(.horizontal, 8)
                .padding(.vertical, 4)
            LanguagePaperDropdownRow(
                language: .system,
                title: String(localized: "跟随系统"),
                detail: AppLanguage.systemLanguage == .en
                    ? String(localized: "当前：English")
                    : String(localized: "当前：中文"),
                isSelected: selected == .system,
                tint: tint
            ) {
                pick(.system)
            }
        }
        .padding(5)
        .frame(width: 218, alignment: .leading)
        // Solid paper: --menu-bg is opaque, so nothing behind shows through.
        .background(scheme == .dark ? Color(yy: YYRGBA(38, 39, 43)) : Color(yy: YYRGBA(252, 251, 248)), in: .rect(cornerRadius: 12, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .inset(by: -0.25)
                .stroke(scheme == .dark ? Color.white.opacity(0.12) : Color(yy: YYRGBA(51, 46, 41, 0.12)), lineWidth: 0.5)
                .allowsHitTesting(false)
        }
        .background {
            let shadowRadius: CGFloat = 12
            let spread: CGFloat = 6
            let primaryColor = scheme == .dark ? Color(yy: YYRGBA(0, 0, 0, 0.65)) : Color(yy: YYRGBA(38, 30, 20, 0.20))
            let primaryY: CGFloat = scheme == .dark ? 18 : 14
            let primaryBlur: CGFloat = scheme == .dark ? 40 : 36
            ZStack {
                RoundedRectangle(cornerRadius: max(0, shadowRadius - spread), style: .continuous)
                    .fill(primaryColor)
                    .padding(spread)
                    .offset(y: primaryY)
                    .blur(radius: primaryBlur / 2)
                if scheme != .dark {
                    RoundedRectangle(cornerRadius: shadowRadius, style: .continuous)
                        .fill(Color(yy: YYRGBA(38, 30, 20, 0.06)))
                        .offset(y: 2)
                        .blur(radius: 3)
                }
            }
            .allowsHitTesting(false)
        }
    }
}

private struct LanguagePaperDropdownRow: View {
    let language: AppLanguage
    let title: String
    let detail: String
    let isSelected: Bool
    let tint: YYTint
    let action: () -> Void
    @State private var hover = false
    @Environment(\.colorScheme) private var scheme

    private var rowFill: Color {
        if isSelected {
            return scheme == .dark ? tint.accent.opacity(0.18) : tint.soft
        }
        if hover {
            return scheme == .dark ? Color.white.opacity(0.08) : Color(yy: YYRGBA(246, 243, 236))
        }
        return .clear
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                LanguageSealBadge(language: language, highlighted: isSelected, tint: tint)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title)
                        .font(.yy(size: 12.5, weight: isSelected ? .semibold : .medium))
                        .foregroundStyle(isSelected ? tint.accent : YYColor.t1)
                    Text(detail)
                        .font(.yy(size: 10.5))
                        .foregroundStyle(isSelected ? tint.accent.opacity(0.78) : YYColor.t3)
                }
                Spacer(minLength: 0)
                if isSelected {
                    Image(systemName: "checkmark")
                        .font(.yy(size: 10.5, weight: .bold))
                        .foregroundStyle(tint.accent)
                }
            }
            .padding(.horizontal, 9)
            .frame(height: 40)
            .background(rowFill, in: .rect(cornerRadius: 8, style: .continuous))
            .contentShape(.rect(cornerRadius: 8, style: .continuous))
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .animation(YYMotion.quick, value: hover)
        .accessibilityAddTraits(isSelected ? .isSelected : [])
    }
}

/// The 20 × 20 warm-paper seal emblem holding 文, Aa, or the device glyph.
struct LanguageSealBadge: View {
    let language: AppLanguage
    var highlighted = false
    var onTrigger = false
    var tint: YYTint = .byDefault
    @Environment(\.colorScheme) private var scheme

    private var fill: Color {
        if highlighted {
            return scheme == .dark ? tint.accent.opacity(0.20) : YYColor.card
        }
        if onTrigger {
            return scheme == .dark ? Color.white.opacity(0.08) : YYColor.card
        }
        return scheme == .dark ? Color.white.opacity(0.07) : YYColor.bg
    }

    private var border: Color {
        if highlighted {
            return tint.edge
        }
        if onTrigger {
            return scheme == .dark ? Color.white.opacity(0.11) : YYColor.line
        }
        return scheme == .dark ? Color.white.opacity(0.10) : YYColor.line
    }

    private var glyphColor: Color {
        if highlighted {
            return tint.accent
        }
        return scheme == .dark ? YYColor.t1.opacity(0.82) : YYColor.t2
    }

    var body: some View {
        ZStack {
            switch language {
            case .zh:
                Text(verbatim: "文")
                    .font(YYUI.serif(11.5, .semibold))
            case .en:
                Text(verbatim: "Aa")
                    .font(.system(size: 11, weight: .semibold, design: .serif).italic())
            case .system:
                #if os(macOS)
                MacDisplayMark()
                    .stroke(style: StrokeStyle(lineWidth: 1.35, lineCap: .round, lineJoin: .round))
                    .frame(width: 12, height: 12)
                #else
                LanguageMark(language: .system)
                    .stroke(style: StrokeStyle(lineWidth: 1.35, lineCap: .round, lineJoin: .round))
                    .frame(width: 12.5, height: 12.5)
                #endif
            }
        }
        .foregroundStyle(glyphColor)
        .frame(width: 20, height: 20)
        .background(fill, in: .rect(cornerRadius: 5.5, style: .continuous))
        .overlay {
            RoundedRectangle(cornerRadius: 5.5, style: .continuous)
                .strokeBorder(border, lineWidth: 0.5)
        }
    }
}

#if os(macOS)
private struct MacDisplayMark: Shape {
    nonisolated func path(in rect: CGRect) -> Path {
        var p = Path()
        p.addRoundedRect(in: CGRect(x: 6, y: 7, width: 22, height: 15), cornerSize: CGSize(width: 2.5, height: 2.5))
        p.move(to: CGPoint(x: 17, y: 22))
        p.addLine(to: CGPoint(x: 17, y: 26.5))
        p.move(to: CGPoint(x: 12.5, y: 26.5))
        p.addLine(to: CGPoint(x: 21.5, y: 26.5))
        let k = min(rect.width, rect.height) / 34
        return p.applying(CGAffineTransform(translationX: rect.minX + (rect.width - 34 * k) / 2, y: rect.minY + (rect.height - 34 * k) / 2).scaledBy(x: k, y: k))
    }
}
#endif

#if os(iOS)
/// Opened in a new language: the ink line passes over the first screen once.
struct LanguageSweepOnce: View {
    @State private var shown = AppLanguage.changedSinceLast

    var body: some View {
        if shown {
            InkSweep(old: nil) { shown = false }
                .ignoresSafeArea()
        }
    }
}
#endif

/// The marks in front of the three 语言 rows: a phone, 文 and A, drawn as the shelf's own lines
/// on a 34 × 34 sheet and scaled to whatever they're given.
struct LanguageMark: Shape {
    let language: AppLanguage

    nonisolated func path(in rect: CGRect) -> Path {
        var p = Path()
        switch language {
        case .system:
            p.addRoundedRect(in: CGRect(x: 10.5, y: 5, width: 13, height: 24), cornerSize: CGSize(width: 3.5, height: 3.5))
            p.move(to: CGPoint(x: 15.5, y: 25.5)); p.addLine(to: CGPoint(x: 18.5, y: 25.5))
        case .zh:
            p.move(to: CGPoint(x: 17, y: 5.5)); p.addLine(to: CGPoint(x: 17, y: 9))
            p.move(to: CGPoint(x: 8, y: 12.5)); p.addLine(to: CGPoint(x: 26, y: 12.5))
            p.move(to: CGPoint(x: 22.5, y: 12.5))
            p.addCurve(to: CGPoint(x: 8.5, y: 28.5), control1: CGPoint(x: 20.7, y: 20.1), control2: CGPoint(x: 16.1, y: 25.1))
            p.move(to: CGPoint(x: 11.2, y: 12.5))
            p.addCurve(to: CGPoint(x: 26.5, y: 28.5), control1: CGPoint(x: 13.5, y: 19.9), control2: CGPoint(x: 18.6, y: 24.9))
        case .en:
            p.move(to: CGPoint(x: 9, y: 28.5)); p.addLine(to: CGPoint(x: 17, y: 6)); p.addLine(to: CGPoint(x: 25, y: 28.5))
            p.move(to: CGPoint(x: 11.9, y: 20.5)); p.addLine(to: CGPoint(x: 22.1, y: 20.5))
        }
        let k = min(rect.width, rect.height) / 34
        return p.applying(CGAffineTransform(translationX: rect.minX + (rect.width - 34 * k) / 2, y: rect.minY + (rect.height - 34 * k) / 2).scaledBy(x: k, y: k))
    }
}
