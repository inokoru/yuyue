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
/// `update.json` from the newest GitHub release says the version (`UpdateCenter.latest`); on the
/// 测试版 channel, from the newest release that has one, pre-releases included. Newer means a higher
/// build number, so a beta and its release can share a version. The notes of
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
    /// 1-based release ordinal from the changelog (e.g. 0.1.0 → 1, 0.1.2 → 3), when known.
    var edition: Int?
    var publishedAt: String?
    var htmlURL: URL
    var notes: UpdateNotes
    /// Android's; the Mac's comes in Sparkle's appcast.
    var assetSize: Int64

    init(info: UpdateInfo, releases: [Release], edition: Int? = nil) {
        display = info.version
        self.edition = edition
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

/// 更新渠道. Switching back to 正式版 never goes back a version: the next release, whose build
/// number is higher than every beta before it, is the update.
enum UpdateChannel: String, CaseIterable, Identifiable {
    case stable, beta
    var id: String { rawValue }
    var title: String {
        switch self {
        case .stable: String(localized: "正式版")
        case .beta: String(localized: "测试版")
        }
    }
    static var detail: String { String(localized: "测试版先拿到新功能，可能不稳定。换回正式版不会退回旧版本，下一个正式版出来时自动换过去") }
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

    /// 1-based release ordinal (oldest = 1) for `version` in a newest-first changelog.
    static func edition(of version: String, in releases: [Release]) -> Int? {
        guard !releases.isEmpty else { return nil }
        if let idx = releases.firstIndex(where: { $0.version == version }) {
            return releases.count - idx
        }
        if compareVersions(a: version, b: releases[0].version) > 0 {
            return releases.count + 1
        }
        return nil
    }
}

private enum UpdateKeys {
    static let auto = "update.auto"
    static let skipped = "update.skipped"
    static let remindVersion = "update.remindVersion"
    static let remindUntil = "update.remindUntil"
    static let checked = "update.checked"
    static let checkedBuild = "update.checkedBuild"
    static let lastSuccess = "update.lastSuccess"
    static let lastSuccessAuto = "update.lastSuccessAuto"
    static let lastWasCurrent = "update.lastWasCurrent"
    static let signAnchor = "update.signAnchor"
    static let channel = "update.channel"
    /// The release folder the last check read, for Sparkle's appcast.
    static let feedBase = "update.feedBase"
}

enum UpdatePhase: Equatable {
    case idle, checking, current, checkFailed
    case release, skipped, later
}

@MainActor @Observable
final class UpdateCenter {
    static let shared = UpdateCenter()
    nonisolated static let latest = "https://github.com/inokoru/yuyue/releases/latest/download/"
    nonisolated static let mirrorLatest = "https://cnb.cool/inokoru/yuyue/-/releases/latest/download/"

    private(set) var phase: UpdatePhase = .idle
    private(set) var offer: UpdateOffer?
    var autoOn: Bool { didSet { UserDefaults.standard.set(autoOn, forKey: UpdateKeys.auto); if autoOn { Task { await check(manual: false) } } } }
    var channel: UpdateChannel {
        didSet {
            guard channel != oldValue else { return }
            UserDefaults.standard.set(channel.rawValue, forKey: UpdateKeys.channel)
            Task { await check(manual: true, force: true) }
        }
    }
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
    private var activeCheck: Task<Void, Never>?
    private var activeCheckManual = false
    private var checkSeq = 0
    private var beforeCheck: UpdatePhase = .idle

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
        channel = d.string(forKey: UpdateKeys.channel).flatMap(UpdateChannel.init(rawValue:)) ?? .stable
        let savedSkipped = d.string(forKey: UpdateKeys.skipped)
        if savedSkipped == Self.current {
            d.removeObject(forKey: UpdateKeys.skipped)
            skipped = nil
        } else {
            skipped = savedSkipped
        }
        let savedRemind = d.string(forKey: UpdateKeys.remindVersion)
        if savedRemind == Self.current {
            d.removeObject(forKey: UpdateKeys.remindVersion)
            d.removeObject(forKey: UpdateKeys.remindUntil)
            remindVersion = nil
            remindUntil = nil
        } else {
            remindVersion = savedRemind
            if let t = d.object(forKey: UpdateKeys.remindUntil) as? Double { remindUntil = Date(timeIntervalSince1970: t) }
        }
        let recordedBuild = (d.object(forKey: UpdateKeys.checkedBuild) as? NSNumber)?.int64Value
        if recordedBuild == Self.build {
            if let t = d.object(forKey: UpdateKeys.lastSuccess) as? Double, t > 0 { lastSuccess = Date(timeIntervalSince1970: t) }
            lastSuccessAuto = d.bool(forKey: UpdateKeys.lastSuccessAuto)
            lastWasCurrent = d.bool(forKey: UpdateKeys.lastWasCurrent)
        } else {
            // After an upgrade (or before checkedBuild was tracked), drop the older build's check result.
            for key in [UpdateKeys.checked, UpdateKeys.lastSuccess, UpdateKeys.lastSuccessAuto, UpdateKeys.lastWasCurrent] {
                d.removeObject(forKey: key)
            }
        }
        if let t = d.object(forKey: UpdateKeys.signAnchor) as? Double { signAnchor = Date(timeIntervalSince1970: t) }
    }

    /// Ordered candidate release folders: China mirror (CNB) first on mainland locales, with GitHub fallback.
    private func candidateBases() async -> [String] {
        #if DEBUG
        if let own = UserDefaults.standard.string(forKey: "YYUpdateBase") {
            return [own.hasSuffix("/") ? own : own + "/"]
        }
        #endif
        let preferCN = ModelDownload.prefersChinaMirror
        if channel != .beta {
            return preferCN ? [Self.mirrorLatest, Self.latest] : [Self.latest, Self.mirrorLatest]
        }
        var bases: [String] = []
        if preferCN {
            if let cn = try? await Self.newestMirrorBase() { bases.append(cn) }
            if let gh = try? await Self.newestBase() { bases.append(gh) }
            bases.append(contentsOf: [Self.mirrorLatest, Self.latest])
        } else {
            if let gh = try? await Self.newestBase() { bases.append(gh) }
            if let cn = try? await Self.newestMirrorBase() { bases.append(cn) }
            bases.append(contentsOf: [Self.latest, Self.mirrorLatest])
        }
        var unique: [String] = []
        for b in bases where !unique.contains(b) { unique.append(b) }
        return unique
    }

    /// GitHub's list is newest first; drafts aren't shown to anyone else, `models-1` has no `update.json`.
    private static func newestBase() async throws -> String? {
        struct Asset: Decodable { let name: String }
        struct Release: Decodable { let tag_name: String; let draft: Bool; let assets: [Asset] }
        var request = URLRequest(
            url: URL(string: "https://api.github.com/repos/\(UpdateRules.repo)/releases?per_page=20")!,
            cachePolicy: .reloadIgnoringLocalCacheData,
            timeoutInterval: 15
        )
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        let newest = try JSONDecoder().decode([Release].self, from: data)
            .first { !$0.draft && $0.assets.contains { $0.name == "update.json" } }
        return newest.map { "https://github.com/\(UpdateRules.repo)/releases/download/\($0.tag_name)/" }
    }

    /// CNB public web JSON release list for beta channel discovery without token.
    private static func newestMirrorBase() async throws -> String? {
        struct Asset: Decodable { let name: String }
        struct Release: Decodable { let tag_ref: String; let is_draft: Bool?; let assets: [Asset]? }
        struct Wrapper: Decodable { let releases: [Release] }
        var request = URLRequest(
            url: URL(string: "https://cnb.cool/\(UpdateRules.repo)/-/releases?page=1&page_size=20")!,
            cachePolicy: .reloadIgnoringLocalCacheData,
            timeoutInterval: 15
        )
        request.setValue("application/vnd.cnb.web+json", forHTTPHeaderField: "Accept")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
        let newest = try JSONDecoder().decode(Wrapper.self, from: data).releases
            .first { !($0.is_draft ?? false) && ($0.assets ?? []).contains { $0.name == "update.json" } }
        guard let rawTag = newest?.tag_ref else { return nil }
        let tag = rawTag.hasPrefix("refs/tags/") ? String(rawTag.dropFirst("refs/tags/".count)) : rawTag
        return "https://cnb.cool/\(UpdateRules.repo)/-/releases/download/\(tag)/"
    }

    static var current: String { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0" }
    static var build: Int64 { Int64(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") ?? 0 }

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
        let staleOffer = lastSuccess != nil && !lastWasCurrent
        if autoOn, staleOffer || (last.map({ Date().timeIntervalSince($0) >= UpdateRules.checkInterval }) ?? true) {
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

    func check(manual: Bool, force: Bool = false) async {
        guard Self.enabled else { return }
        let running = activeCheck
        if let running, !force && (!manual || activeCheckManual) {
            await running.value
            return
        }
        if phase != .checking { beforeCheck = phase }
        let before = beforeCheck
        checkSeq += 1
        let seq = checkSeq
        running?.cancel()
        activeCheckManual = manual
        if manual { phase = .checking }
        let now = Date()
        UserDefaults.standard.set(now, forKey: UpdateKeys.checked)
        let task = Task {
            defer {
                if checkSeq == seq {
                    activeCheck = nil
                    activeCheckManual = false
                    if phase == .checking { phase = manual ? .checkFailed : before }
                }
            }
            do {
                let bases = await candidateBases()
                try Task.checkCancellation()
                var chosenBase: String?
                var loadedInfo: UpdateInfo?
                var lastError: Error?
                for base in bases {
                    try Task.checkCancellation()
                    do {
                        if let json = try await optionalText(base + "update.json") {
                            let info = try parseUpdate(json: json)
                            chosenBase = base
                            loadedInfo = info
                            break
                        } else if chosenBase == nil {
                            chosenBase = base
                        }
                    } catch is CancellationError {
                        throw CancellationError()
                    } catch {
                        lastError = error
                    }
                }
                guard checkSeq == seq, !Task.isCancelled else { return }
                if chosenBase == nil, let lastError { throw lastError }
                if let chosenBase { UserDefaults.standard.set(chosenBase, forKey: UpdateKeys.feedBase) }
                if let info = loadedInfo {
                    let newer = info.code > 0 ? Int64(info.code) > Self.build : compareVersions(a: info.version, b: Self.current) > 0
                    if newer {
                        let log = info.changelog.isEmpty ? "" : ((try? await text(info.changelog)) ?? "")
                        guard checkSeq == seq, !Task.isCancelled else { return }
                        let all = log.isEmpty ? [] : parseChangelog(text: log)
                        let between = releasesAfter(text: log, current: Self.current)
                        offer = UpdateOffer(
                            info: info,
                            releases: between.contains { $0.version == info.version } ? between : [updateRelease(u: info)] + between,
                            edition: UpdateRules.edition(of: info.version, in: all)
                        )
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
                d.set(Self.build, forKey: UpdateKeys.checkedBuild)
                d.set(now.timeIntervalSince1970, forKey: UpdateKeys.lastSuccess)
                d.set(!manual, forKey: UpdateKeys.lastSuccessAuto)
                d.set(offer == nil, forKey: UpdateKeys.lastWasCurrent)
                reconcile()
            } catch is CancellationError {
                if checkSeq == seq, phase == .checking { phase = before }
            } catch {
                if checkSeq == seq { phase = manual ? .checkFailed : before }
            }
        }
        activeCheck = task
        await task.value
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
        return lastSuccessAuto ? String(localized: "上次检查：\(when) · 自动") : String(localized: "上次检查：\(when)")
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
        let request = URLRequest(url: u, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        let (data, response) = try await URLSession.shared.data(for: request)
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
    private static let shortTimeFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .none
        f.timeStyle = .short
        return f
    }()

    static func phrase(_ date: Date) -> String {
        shortTimeFormatter.locale = Locale.current
        let clock = shortTimeFormatter.string(from: date)
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
