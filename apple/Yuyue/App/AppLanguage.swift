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
}

extension AppLanguage {
    private static var snapshotURL: URL { FileManager.default.temporaryDirectory.appending(path: "yuyue-language-sweep.png") }
    private static let pendingKey = "language.sweepSettings"
    private static let frameKey = "language.sweepFrame"
    private static let forwardKey = "language.sweepForward"
    /// The new 语阅 tells the old one its settings window is up and covered, so the old one can go.
    private static let readyNote = Notification.Name("app.yuyue.languageReady")

    /// Changes the language without the window going anywhere: a picture of the settings window is
    /// kept, a new 语阅 opens behind it and puts its own settings window in the same place under that
    /// picture, and only then does this one quit. The ink line then sweeps the picture away.
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
        let center = DistributedNotificationCenter.default()
        var token: NSObjectProtocol?
        token = center.addObserver(forName: readyNote, object: nil, queue: .main) { _ in
            token.map(center.removeObserver)
            MainActor.assumeIsolated {
                // The front is handed to the new one; macOS won't let it take it on its own.
                if let next {
                    NSApp.yieldActivation(to: next)
                    next.activate(from: .current)
                }
                // A moment for the front to move over, so nothing else comes up in between.
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.1) { NSApp.terminate(nil) }
            }
        }
        let config = NSWorkspace.OpenConfiguration()
        config.createsNewApplicationInstance = true
        config.activates = false
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config) { app, _ in
            DispatchQueue.main.async { next = app }
        }
        // If the new one never says so, go anyway.
        DispatchQueue.main.asyncAfter(deadline: .now() + 8) { NSApp.terminate(nil) }
    }

    /// The new 语阅, once it is running.
    @MainActor private static var next: NSRunningApplication?

    /// Opened again after a language change: the picture to sweep from, read once.
    @MainActor static let pendingSweep: (picture: NSImage, forward: Bool)? = {
        guard UserDefaults.standard.bool(forKey: pendingKey), let picture = NSImage(contentsOf: snapshotURL) else { return nil }
        try? FileManager.default.removeItem(at: snapshotURL)
        return (picture, UserDefaults.standard.bool(forKey: forwardKey))
    }()

    /// The new settings window is where the old one was, under its picture: the old 语阅 may quit.
    @MainActor static func takeOver(_ window: NSWindow) {
        let d = UserDefaults.standard
        if let s = d.string(forKey: frameKey) { window.setFrame(NSRectFromString(s), display: true) }
        for key in [pendingKey, frameKey, forwardKey] { d.removeObject(forKey: key) }
        window.orderFrontRegardless()
        DistributedNotificationCenter.default().postNotificationName(readyNote, object: nil, deliverImmediately: true)
        // Taken up once the old one has yielded the front.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.15) {
            NSApp.activate()
            window.makeKeyAndOrderFront(nil)
        }
    }

    @MainActor static var settingsPending: Bool { UserDefaults.standard.bool(forKey: pendingKey) }
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

/// 设置 › 通用 › 语言: three small paper slips on the right, each in its own typeface, with a short
/// underline rule under the one chosen (the same mark as the 阅 icon). Picking one that differs from
/// what's showing changes it at once: 语阅 opens again behind the window, and an ink line sweeps
/// the words into the new language. Only while something is being read aloud or made is it left for next time.
struct MacLanguageCard: View {
    @State private var choice = AppLanguage.chosen
    @State private var later = false
    /// Handed over to a new 语阅; nothing more to pick here.
    @State private var going = false
    /// Whether it may change now; not while something is being read aloud or made.
    let canRelaunch: () -> Bool
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

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
            HStack(spacing: 7) {
                ForEach(AppLanguage.allCases) { l in
                    LanguageSlip(language: l, selected: choice == l, tint: tint) { pick(l) }
                }
            }
        }
        .padding(.leading, 16)
        .padding(.trailing, 14)
        .padding(.vertical, 12)
        .animation(YYMotion.standard, value: choice)
        .animation(YYMotion.standard, value: later)
    }

    private var subtitle: String {
        if later {
            return String(localized: "正在朗读或生成有声书，下次启动时生效")
        }
        if choice == .system {
            return AppLanguage.systemLanguage == .en
                ? String(localized: "当前：English · 切换后自动重开，并回到当前页")
                : String(localized: "当前：中文 · 切换后自动重开，并回到当前页")
        }
        return String(localized: "切换后自动重开，并回到当前页")
    }

    private func pick(_ l: AppLanguage) {
        guard choice != l, !going else { return }
        let from = name(choice)
        choice = l
        AppLanguage.choose(l)
        SettingsLog.shared.flash = .init(date: .now, title: String(localized: "语言"), to: name(l), from: from)
        guard l.effective != AppLanguage.running else { later = false; return }
        later = !canRelaunch()
        if !later {
            going = true
            // A beat for the slip to take the choice, then the picture is taken.
            DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) {
                NotificationCenter.default.post(name: .relaunchForLanguage, object: l.effective)
            }
        }
    }

    private func name(_ l: AppLanguage) -> String {
        switch l {
        case .system: String(localized: "跟随系统")
        case .zh: "中文"
        case .en: "English"
        }
    }
}

private struct LanguageSlip: View {
    let language: AppLanguage
    let selected: Bool
    let tint: YYTint
    let action: () -> Void
    @Environment(\.colorScheme) private var scheme
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            ZStack {
                word
                    .offset(y: -2)
                    .padding(.horizontal, 6)
            }
            .frame(width: 94, height: 48)
            .background(fill, in: .rect(cornerRadius: 10))
            .overlay {
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(selected ? tint.edge : (hover ? YYColor.tint : YYColor.line),
                                  lineWidth: selected ? 1 : 0.5)
            }
            .overlay(alignment: .bottom) {
                Capsule()
                    .fill(tint.accent)
                    .frame(width: 16, height: 2)
                    .padding(.bottom, 7.5)
                    .scaleEffect(x: selected ? 1 : 0.4, y: 1)
                    .opacity(selected ? 1 : 0)
            }
            .shadow(color: selected
                        ? (scheme == .dark ? .black.opacity(0.28) : Color(yy: YYRGBA(40, 32, 22, 0.09)))
                        : .clear,
                    radius: 3.5, y: 2)
            .contentShape(.rect(cornerRadius: 10))
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
        .onHover { hover = $0 }
        .animation(YYMotion.quick, value: hover)
        .accessibilityLabel(Text(accessibilityName))
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private var fill: Color {
        if selected || hover { return YYColor.card }
        return scheme == .dark ? Color(yy: YYRGBA(38, 39, 43)) : Color(yy: YYRGBA(249, 247, 243))
    }

    @ViewBuilder private var word: some View {
        switch language {
        case .system:
            Text("跟随系统")
                .font(.yy(size: 12.5, weight: selected ? .semibold : .medium))
                .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
        case .zh:
            Text(verbatim: "中文")
                .font(YYUI.serif(16.5, .semibold))
                .tracking(0.8)
                .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
        case .en:
            Text(verbatim: "English")
                .font(.system(size: 16.5, weight: .medium, design: .serif).italic())
                .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
        }
    }

    private var accessibilityName: String {
        switch language {
        case .system: String(localized: "跟随系统")
        case .zh: "中文"
        case .en: "English"
        }
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
