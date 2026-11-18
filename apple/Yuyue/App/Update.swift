import Foundation
import Observation
import YuyueCore
#if os(macOS)
import AppKit
#else
import UIKit
#endif

/// 软件更新: what the red dot, 设置 › 关于 and the iPhone's 关于 page show.
///
/// `update.json` from the newest GitHub release says the version (`Updates.latest`); the notes of
/// every version since this one come from that release's `CHANGELOG.md`, parsed in the core. The
/// Mac hands the download and the swap to Sparkle (`Updates`), which checks the EdDSA signature;
/// an iPhone that installed the `.ipa` itself (`SIDELOAD`) only shows the notes and the release's
/// page. The App Store's build never asks.
///
/// Skip, remind and the last check stay on this device; nothing goes into the sync log.
///
/// `-YYUpdateBase http://<Mac>:8000/` reads a folder of one's own instead (a draft release).

struct UpdateNotes: Equatable {
    var lede: String
    var sections: [(title: String, items: [String])]
    static func == (lhs: UpdateNotes, rhs: UpdateNotes) -> Bool {
        lhs.lede == rhs.lede && lhs.sections.map(\.title) == rhs.sections.map(\.title) && lhs.sections.map(\.items) == rhs.sections.map(\.items)
    }
}

struct UpdateOffer: Equatable, Identifiable {
    var id: String { display }
    var display: String
    var publishedAt: String?
    var htmlURL: URL
    var notes: UpdateNotes
    /// Android's; the Mac's comes in Sparkle's appcast.
    var assetSize: Int64

    init(info: UpdateInfo, releases: [Release]) {
        display = info.version
        publishedAt = info.date.isEmpty ? nil : info.date
        htmlURL = URL(string: info.page) ?? URL(string: "https://github.com/\(UpdateRules.repo)/releases")!
        assetSize = Int64(info.size)
        let zh = Bundle.main.preferredLocalizations.first?.hasPrefix("zh") ?? true
        func pick(_ r: Release) -> [String] {
            let (a, b) = zh ? (r.notesZh, r.notesEn) : (r.notesEn, r.notesZh)
            return a.isEmpty ? b : a
        }
        // One version: its notes. Skipped a few: each version under its number, newest first.
        if releases.count <= 1 {
            notes = UpdateNotes(lede: "", sections: releases.first.map { [("", pick($0))] } ?? [])
        } else {
            notes = UpdateNotes(lede: "", sections: releases.map { r in
                (UpdateRules.monthDay(r.date).map { "\(r.version) · \($0)" } ?? r.version, pick(r))
            })
        }
    }
}

enum UpdateRules {
    static let repo = "inokoru/yuyue"
    static let remindInterval: TimeInterval = 3 * 24 * 60 * 60
    static let checkInterval: TimeInterval = 24 * 60 * 60
    static let signDays: TimeInterval = 7 * 24 * 60 * 60
    static let signWarnDays = 2

    static func megabytes(_ bytes: Int64) -> String { String(format: "%.1f MB", Double(bytes) / 1024 / 1024) }

    /// `2026-11-20` → `11月20日` (or `11/20` in English).
    static func monthDay(_ iso: String?) -> String? {
        guard let iso, iso.count >= 10,
              let month = Int(iso.dropFirst(5).prefix(2)), let day = Int(iso.dropFirst(8).prefix(2)),
              (1...12).contains(month), (1...31).contains(day) else { return nil }
        return String(localized: "\(month) 月 \(day) 日")
    }

    static func signatureRemainingDays(anchor: Date, now: Date) -> Int {
        let elapsed = max(0, now.timeIntervalSince(anchor))
        return max(0, Int(signDays / 86_400) - Int(elapsed / 86_400))
    }
}

private enum UpdateKeys {
    static let auto = "update.auto"
    static let skipped = "update.skipped"
    static let remindVersion = "update.remindVersion"
    static let remindUntil = "update.remindUntil"
    static let checked = "update.checked"
    static let lastSuccess = "update.lastSuccess"
    static let lastSuccessAuto = "update.lastSuccessAuto"
    static let lastWasCurrent = "update.lastWasCurrent"
    static let signAnchor = "update.signAnchor"
}

enum UpdatePhase: Equatable {
    case idle, checking, current, checkFailed
    case release, skipped, later
}

@MainActor @Observable
final class UpdateCenter {
    static let shared = UpdateCenter()
    nonisolated static let latest = "https://github.com/inokoru/yuyue/releases/latest/download/"

    private(set) var phase: UpdatePhase = .idle
    private(set) var offer: UpdateOffer?
    var autoOn: Bool { didSet { UserDefaults.standard.set(autoOn, forKey: UpdateKeys.auto); if autoOn { Task { await check(manual: false) } } } }
    private(set) var skipped: String?
    private(set) var remindVersion: String?
    private(set) var remindUntil: Date?
    private(set) var lastSuccess: Date?
    private(set) var lastSuccessAuto = false
    private(set) var lastWasCurrent = false
    /// Set when 检查更新… is used, so the shelf window shows the dialog.
    var dialog: Dialog = .none
    private var booted = false
    private var signAnchor: Date?

    enum Dialog: Equatable { case none, flow, current, offline }

    /// Self-update is this build's job: the Mac's, and the iPhone's that was installed by hand.
    static var enabled: Bool {
        #if os(macOS) || SIDELOAD
        true
        #else
        false
        #endif
    }

    private init() {
        let d = UserDefaults.standard
        autoOn = d.object(forKey: UpdateKeys.auto) as? Bool ?? true
        skipped = d.string(forKey: UpdateKeys.skipped)
        remindVersion = d.string(forKey: UpdateKeys.remindVersion)
        if let t = d.object(forKey: UpdateKeys.remindUntil) as? Double { remindUntil = Date(timeIntervalSince1970: t) }
        if let t = d.object(forKey: UpdateKeys.lastSuccess) as? Double, t > 0 { lastSuccess = Date(timeIntervalSince1970: t) }
        lastSuccessAuto = d.bool(forKey: UpdateKeys.lastSuccessAuto)
        lastWasCurrent = d.bool(forKey: UpdateKeys.lastWasCurrent)
        if let t = d.object(forKey: UpdateKeys.signAnchor) as? Double { signAnchor = Date(timeIntervalSince1970: t) }
    }

    private var base: String {
        UserDefaults.standard.string(forKey: "YYUpdateBase").map { $0.hasSuffix("/") ? $0 : $0 + "/" } ?? Self.latest
    }

    static var current: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0" }

    var currentLabel: String {
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "1"
        return "\(Self.current) (\(build))"
    }

    var showsBadge: Bool {
        guard let offer else { return false }
        if skipped == offer.display { return false }
        if remindVersion == offer.display, let remindUntil, Date() < remindUntil { return false }
        return true
    }

    var signatureDaysLeft: Int? {
        #if os(iOS) && SIDELOAD
        guard let signAnchor else { return nil }
        return UpdateRules.signatureRemainingDays(anchor: signAnchor, now: Date())
        #else
        return nil
        #endif
    }

    var signatureWarn: Bool {
        guard let days = signatureDaysLeft else { return false }
        return days <= UpdateRules.signWarnDays
    }

    /// On opening: once a day at most, when 自动检查 is on.
    func boot() {
        guard Self.enabled, !booted else { return }
        booted = true
        #if os(macOS)
        NotificationCenter.default.addObserver(forName: NSMenu.didBeginTrackingNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { MacUpdateMenu.decorate() }
        }
        #endif
        #if os(iOS)
        if signAnchor == nil { resignNoted() }
        #endif
        let last = UserDefaults.standard.object(forKey: UpdateKeys.checked) as? Date
        if autoOn, last.map({ Date().timeIntervalSince($0) >= UpdateRules.checkInterval }) ?? true {
            Task { await check(manual: false) }
        } else {
            phase = lastSuccess == nil ? .idle : .current
        }
    }

    func resignNoted() {
        signAnchor = Date()
        UserDefaults.standard.set(signAnchor!.timeIntervalSince1970, forKey: UpdateKeys.signAnchor)
    }

    func checkFromMenu() {
        dialog = .flow
        Task {
            await check(manual: true)
            switch phase {
            case .current: dialog = .current
            case .checkFailed: dialog = .offline
            default: dialog = .flow
            }
        }
    }

    func check(manual: Bool) async {
        guard Self.enabled, phase != .checking else { return }
        let before = phase
        if manual { phase = .checking }
        let now = Date()
        UserDefaults.standard.set(now, forKey: UpdateKeys.checked)
        do {
            if let json = try await optionalText(base + "update.json") {
                let info = try parseUpdate(json: json)
                if compareVersions(a: info.version, b: Self.current) > 0 {
                    let log = info.changelog.isEmpty ? "" : ((try? await text(info.changelog)) ?? "")
                    let between = releasesAfter(text: log, current: Self.current)
                    offer = UpdateOffer(info: info, releases: between.contains { $0.version == info.version } ? between : [updateRelease(u: info)] + between)
                } else {
                    offer = nil
                }
            } else {
                offer = nil
            }
            lastSuccess = now
            lastSuccessAuto = !manual
            lastWasCurrent = offer == nil
            let d = UserDefaults.standard
            d.set(now.timeIntervalSince1970, forKey: UpdateKeys.lastSuccess)
            d.set(!manual, forKey: UpdateKeys.lastSuccessAuto)
            d.set(offer == nil, forKey: UpdateKeys.lastWasCurrent)
            reconcile()
        } catch {
            phase = manual ? .checkFailed : before
        }
    }

    private func reconcile() {
        guard let offer else { phase = lastSuccess == nil ? .idle : .current; return }
        if skipped == offer.display { phase = .skipped; return }
        if remindVersion == offer.display, let remindUntil, Date() < remindUntil { phase = .later; return }
        phase = .release
    }

    func skip() {
        guard let offer else { return }
        skipped = offer.display
        UserDefaults.standard.set(skipped, forKey: UpdateKeys.skipped)
        phase = .skipped
        dialog = .none
    }

    func remindLater() {
        guard let offer else { return }
        remindVersion = offer.display
        remindUntil = Date().addingTimeInterval(UpdateRules.remindInterval)
        UserDefaults.standard.set(remindVersion, forKey: UpdateKeys.remindVersion)
        UserDefaults.standard.set(remindUntil?.timeIntervalSince1970, forKey: UpdateKeys.remindUntil)
        phase = .later
        dialog = .none
    }

    func lookAnyway() {
        skipped = nil
        remindVersion = nil
        remindUntil = nil
        for key in [UpdateKeys.skipped, UpdateKeys.remindVersion, UpdateKeys.remindUntil] { UserDefaults.standard.removeObject(forKey: key) }
        phase = offer == nil ? .current : .release
    }

    #if os(macOS)
    /// 下载并安装: Sparkle downloads, checks the signature, swaps the app and opens it again.
    func install() {
        dialog = .none
        Updates.shared.check()
    }
    #endif

    func checkedLine() -> String {
        guard let lastSuccess else { return "" }
        let when = UpdateWhen.phrase(lastSuccess)
        if lastWasCurrent { return String(localized: "已是最新 · \(when) 检查过") }
        return lastSuccessAuto ? String(localized: "\(when) · 自动") : when
    }

    func offlineFine() -> String {
        guard let lastSuccess, lastWasCurrent else { return String(localized: "书和笔记不受影响。") }
        return String(localized: "上次查到的还是\(UpdateWhen.phrase(lastSuccess))，当时已是最新。书和笔记不受影响。")
    }

    private func text(_ url: String) async throws -> String {
        guard let s = try await optionalText(url) else { throw URLError(.fileDoesNotExist) }
        return s
    }

    private func optionalText(_ url: String) async throws -> String? {
        guard let u = URL(string: url) else { throw URLError(.badURL) }
        let (data, response) = try await URLSession.shared.data(from: u)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        if code == 404 { return nil }
        guard code == 200 else { throw URLError(.badServerResponse) }
        return String(decoding: data, as: UTF8.self)
    }
}

enum UpdateDevice {
    static var name: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        #endif
    }
}

enum UpdateWhen {
    static func phrase(_ date: Date) -> String {
        let time = DateFormatter()
        time.locale = Locale.current
        time.dateStyle = .none
        time.timeStyle = .short
        let clock = time.string(from: date)
        if Calendar.current.isDateInToday(date) { return String(localized: "今天 \(clock)") }
        if Calendar.current.isDateInYesterday(date) { return String(localized: "昨天 \(clock)") }
        return "\(monthDay(date)) \(clock)"
    }

    static func monthDay(_ date: Date) -> String {
        let c = Calendar.current.dateComponents([.month, .day], from: date)
        return String(localized: "\(c.month ?? 1) 月 \(c.day ?? 1) 日")
    }
}

#if os(macOS)
enum MacUpdateMenu {
    /// 检查更新… with a red dot when a new version is waiting.
    static func decorate() {
        guard let menu = NSApp.mainMenu?.item(at: 0)?.submenu else { return }
        let label = String(localized: "检查更新…")
        for item in menu.items where item.title == label || item.attributedTitle?.string.hasPrefix(label) == true {
            let title = NSMutableAttributedString(string: label, attributes: [.font: NSFont.menuFont(ofSize: 0), .foregroundColor: NSColor.labelColor])
            if UpdateCenter.shared.showsBadge {
                title.append(NSAttributedString(string: "  ●", attributes: [
                    .font: NSFont.menuFont(ofSize: 0),
                    .foregroundColor: NSColor(srgbRed: 194 / 255, green: 71 / 255, blue: 58 / 255, alpha: 1),
                ]))
            }
            item.attributedTitle = title
        }
    }
}
#endif
