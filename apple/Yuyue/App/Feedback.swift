import Foundation
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore
#if os(iOS)
import PhotosUI
import UIKit
#else
import AppKit
#endif

/// 反馈: the core turns what the form holds into one ntfy message (`feedbackMessage`) and it is
/// POSTed to ntfy.sh (docs/dev/feedback.md). 语阅 is open source: the words become a public
/// GitHub Issue, and so do the screenshots unless the person turns that off. The email, logs and
/// other files are sealed so only the developer reads them. Without a network the message waits
/// on this device and goes on the next try.
@Observable
final class Feedback {
    static let shared = Feedback()

    enum Category: String, CaseIterable, Identifiable {
        case bug, idea, other
        var id: String { rawValue }
        var title: String {
            switch self {
            case .bug: String(localized: "问题")
            case .idea: String(localized: "建议")
            case .other: String(localized: "其他")
            }
        }
        var placeholder: String {
            switch self {
            case .bug: String(localized: "哪本书、哪一页，做了什么，结果怎样")
            case .idea: String(localized: "想要什么功能，或者哪里可以更顺手")
            case .other: String(localized: "想说的都可以")
            }
        }
    }

    /// `queued(busy:)`: kept on this device and sent later; `busy` when ntfy said too many
    /// (HTTP 429) rather than the network being gone.
    enum Outcome { case sent, queued(busy: Bool) }

    enum Problem: LocalizedError {
        case notConfigured, busy, filesOffline, filesBusy, tooBig(String), unreadable(String)
        var errorDescription: String? {
            switch self {
            case .notConfigured: String(localized: "这个版本没有配置反馈地址，发不出去。")
            case .busy: String(localized: "反馈服务现在太忙，过一会儿再试。")
            case .filesOffline: String(localized: "附件没传上去，可能是没网。写的都还在，联网后再点发送。")
            case .filesBusy: String(localized: "反馈服务现在太忙，附件没传上去。写的都还在，过一会儿再点发送。")
            case .tooBig(let name): String(localized: "「\(name)」太大了，一个附件最多 1.5 MB。")
            case .unreadable(let name): String(localized: "「\(name)」读不出来。")
            }
        }
    }

    /// One attachment: a screenshot, a log, a book that won't open. A picture is shrunk first (at
    /// most 1600 px on the long side, re-encoded as JPEG, so what the original carried — place,
    /// camera, time — is gone); anything else goes as it is, up to the core's 1.5 MB.
    nonisolated struct Attachment: Identifiable, @unchecked Sendable {
        let id = UUID()
        let name: String
        let data: Data
        let preview: CGImage?
        /// A picture (shrunk to a JPEG here); only these may go public.
        var isShot: Bool { preview != nil }
    }

    /// The core's own caps (`TEXT_MAX`, `CONTACT_MAX`), counted in Unicode scalars as Rust counts chars.
    static let textMax = 800
    static let contactMax = 80
    static let filesMax = 3
    nonisolated static let fileBytesMax = 1_500_000
    /// 早期版本: the entries wear the theme colour. Set to false once 语阅 settles.
    static let early = true

    /// Set by 帮助 › 给语阅反馈… and the sidebar; the Mac shelf window shows the sheet.
    var requested = false

    #if os(macOS)
    /// A screenshot taken from the form, waiting for the form to come back and take it in.
    private(set) var pendingShot: Data?
    /// The form steps aside while the person picks what to shoot.
    var shooting = false

    /// 截图: the form steps aside and a small bar floats at the bottom of the screen. The person
    /// goes wherever the trouble is — a book, a page, a setting — then presses 截图 on the bar
    /// for the system's own crosshair (drag a region, Space for a window). The form comes back
    /// with the shot attached; what was written waits in the draft. Esc in the crosshair returns
    /// to the bar; 回到反馈 returns without a shot.
    func shoot() {
        guard !shooting else { return }
        shooting = true
        // After the sheet has slid away.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
            ShotBar.show(shoot: { [self] in capture() }, back: { [self] in finishShot(nil) })
        }
    }

    private func capture() {
        ShotBar.hide()
        Task {
            try? await Task.sleep(for: .milliseconds(150))
            if let shot = await Self.screencapture() {
                finishShot(shot)
            } else {
                // Esc: still looking.
                ShotBar.show(shoot: { [self] in capture() }, back: { [self] in finishShot(nil) })
            }
        }
    }

    private func finishShot(_ shot: Data?) {
        ShotBar.hide()
        pendingShot = shot
        shooting = false
        NSApp.activate()
        NSApp.windows.first { $0.identifier?.rawValue.hasPrefix("library") == true }?.makeKeyAndOrderFront(nil)
        requested = true
    }

    func takePendingShot() -> Data? {
        defer { pendingShot = nil }
        return pendingShot
    }

    private static func screencapture() async -> Data? {
        let url = FileManager.default.temporaryDirectory.appending(path: "feedback-\(UUID().uuidString).png")
        return await withCheckedContinuation { (done: CheckedContinuation<Data?, Never>) in
            let process = Process()
            process.executableURL = URL(filePath: "/usr/sbin/screencapture")
            // -i: the person chooses; -x: no shutter sound; -o: no shadow around a window.
            process.arguments = ["-i", "-x", "-o", url.path]
            process.terminationHandler = { _ in
                let data = try? Data(contentsOf: url)
                try? FileManager.default.removeItem(at: url)
                done.resume(returning: data)
            }
            do { try process.run() } catch { done.resume(returning: nil) }
        }
    }
    #endif
    private(set) var queued: [String]

    private let queueKey = "feedback.queue"
    private var flushing = false

    private init() {
        queued = UserDefaults.standard.stringArray(forKey: queueKey) ?? []
        // No daily cap any more; the old count goes.
        UserDefaults.standard.removeObject(forKey: "feedback.sentAt")
        #if DEBUG
        // -debugFeedback 1|full: the window catalog opens the form (full: with words and files).
        requested = UserDefaults.standard.string(forKey: "debugFeedback") != nil
        #endif
    }

    // MARK: Configuration

    private static func info(_ key: String) -> String {
        let value = (Bundle.main.object(forInfoDictionaryKey: key) as? String ?? "").trimmingCharacters(in: .whitespaces)
        return value.hasPrefix("$(") ? "" : value
    }

    private let topic = Feedback.info("YuyueFeedbackTopic")
    private let publicKey = Feedback.info("YuyueFeedbackPublicKey")

    /// Public builds carry neither; the form says so and does not send.
    var isConfigured: Bool { !topic.isEmpty && !publicKey.isEmpty }

    // MARK: Sending

    /// `withInfo` false: the person turned 附加信息 off; version and system stay here.
    /// `publicShots`: screenshots go up as they are, for the public Issue; otherwise sealed
    /// like every other file.
    ///
    /// Attachments go first, each its own ntfy attachment; the report then names them.
    /// They need a network now: if one fails nothing is counted and the form keeps everything.
    /// Only the words wait in the queue (ntfy keeps an attachment 3 hours, too short to hold).
    func send(_ category: Category, text: String, contact: String, withInfo: Bool = true, publicShots: Bool = true,
              files: [Attachment] = []) async throws -> Outcome {
        guard isConfigured else { throw Problem.notConfigured }
        var ids: [String] = []
        for (n, file) in files.prefix(Self.filesMax).enumerated() {
            let shown = publicShots && file.isShot
            let data = shown ? file.data : try sealFeedbackFile(publicKeyB64: publicKey, name: file.name, data: file.data)
            do {
                ids.append(try await upload(data, shown: shown, n: n + 1))
            } catch Problem.busy { throw Problem.filesBusy } catch { throw Problem.filesOffline }
        }
        let body = try feedbackMessage(publicKeyB64: publicKey, draft: FeedbackDraft(
            category: category.rawValue,
            text: text,
            contact: contact,
            appVersion: withInfo ? Self.appVersion : "",
            platform: withInfo ? Self.platform : "",
            osVersion: withInfo ? Self.osVersion : "",
            files: ids
        ))
        do {
            try await post(body)
            await flush()
            return .sent
        } catch {
            let busy = (error as? Problem).map { if case .busy = $0 { true } else { false } } ?? false
            // At most 10, the newest kept. The email in it is sealed.
            queued = Array((queued + [body]).suffix(10))
            UserDefaults.standard.set(queued, forKey: queueKey)
            return .queued(busy: busy)
        }
    }

    /// Sends what waited for a network, oldest first; stops at the first one that fails.
    func flush() async {
        guard isConfigured, !flushing, !queued.isEmpty else { return }
        flushing = true
        defer { flushing = false }
        while let body = queued.first {
            do { try await post(body) } catch { return }
            queued.removeFirst()
            UserDefaults.standard.set(queued, forKey: queueKey)
        }
    }

    private func post(_ body: String) async throws {
        _ = try await ntfy("POST", Data(body.utf8), timeout: 15)
    }

    /// One attachment. `shown`: a screenshot for the public Issue, the plain JPEG, marked `v2shot`.
    /// Otherwise a sealed box marked `v1file`, its name inside the box. Returns its ntfy id,
    /// which the report carries.
    private func upload(_ body: Data, shown: Bool, n: Int) async throws -> String {
        let headers = shown ? ["Filename": "shot-\(n).jpg", "X-Message": "v2shot"] : ["Filename": "f.bin", "X-Message": "v1file"]
        let data = try await ntfy("PUT", body, timeout: 40, headers: headers)
        guard let reply = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = reply["id"] as? String, !id.isEmpty else { throw URLError(.badServerResponse) }
        return id
    }

    private func ntfy(_ method: String, _ body: Data, timeout: TimeInterval, headers: [String: String] = [:]) async throws -> Data {
        guard let url = URL(string: "https://ntfy.sh/\(topic)") else { throw URLError(.badURL) }
        var request = URLRequest(url: url, timeoutInterval: timeout)
        request.httpMethod = method
        // Not pushed on through Google's FCM: the relay polls, nobody needs the push.
        request.setValue("no", forHTTPHeaderField: "X-Firebase")
        for (key, value) in headers { request.setValue(value, forHTTPHeaderField: key) }
        request.httpBody = body
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
        // 429: ntfy.sh's limit for this IP (shared by everyone behind the same carrier address).
        if http.statusCode == 429 { throw Problem.busy }
        guard (200..<300).contains(http.statusCode) else { throw URLError(.badServerResponse) }
        return data
    }

    // MARK: Draft and email

    /// What the form held when it closed, so nothing written is lost. Cleared once it is sent
    /// (or queued). Attachments are files beside it, in order.
    private struct Draft: Codable {
        var category: String
        var text: String
        var withInfo: Bool
        var files: [String]
        /// Missing in drafts from before the choice existed: on.
        var publicShots: Bool?
    }

    private let draftKey = "feedback.draft"
    private let emailKey = "feedback.email"

    private var draftDir: URL {
        URL.applicationSupportDirectory.appending(path: "Yuyue/Feedback/Draft", directoryHint: .isDirectory)
    }

    /// The email last typed, offered next time. Kept after sending.
    var savedEmail: String {
        get { UserDefaults.standard.string(forKey: emailKey) ?? "" }
        set {
            let t = newValue.trimmingCharacters(in: .whitespaces)
            if t.isEmpty { UserDefaults.standard.removeObject(forKey: emailKey) } else { UserDefaults.standard.set(t, forKey: emailKey) }
        }
    }

    func saveDraft(category: Category, text: String, withInfo: Bool, publicShots: Bool) {
        var draft = loadDraftRecord() ?? Draft(category: category.rawValue, text: "", withInfo: true, files: [])
        draft.category = category.rawValue
        draft.text = text
        draft.withInfo = withInfo
        draft.publicShots = publicShots
        writeDraft(draft)
    }

    func saveDraftFiles(_ files: [Attachment]) {
        let fm = FileManager.default
        try? fm.removeItem(at: draftDir)
        var names: [String] = []
        if !files.isEmpty {
            try? fm.createDirectory(at: draftDir, withIntermediateDirectories: true)
            for (i, file) in files.enumerated() {
                let stored = "\(i)"
                if (try? file.data.write(to: draftDir.appending(path: stored))) != nil { names.append(file.name) }
            }
        }
        var draft = loadDraftRecord() ?? Draft(category: Category.bug.rawValue, text: "", withInfo: true, files: [])
        draft.files = names
        writeDraft(draft)
    }

    func loadDraft() -> (category: Category, text: String, withInfo: Bool, publicShots: Bool, files: [Attachment])? {
        guard let draft = loadDraftRecord() else { return nil }
        let files: [Attachment] = draft.files.enumerated().compactMap { i, name in
            guard let data = try? Data(contentsOf: draftDir.appending(path: "\(i)")) else { return nil }
            return Self.restored(name: name, data: data)
        }
        return (Category(rawValue: draft.category) ?? .bug, draft.text, draft.withInfo, draft.publicShots ?? true, files)
    }

    func clearDraft() {
        UserDefaults.standard.removeObject(forKey: draftKey)
        try? FileManager.default.removeItem(at: draftDir)
    }

    private func loadDraftRecord() -> Draft? {
        guard let data = UserDefaults.standard.data(forKey: draftKey) else { return nil }
        return try? JSONDecoder().decode(Draft.self, from: data)
    }

    private func writeDraft(_ draft: Draft) {
        if draft.text.isEmpty && draft.files.isEmpty && draft.withInfo && draft.publicShots ?? true
            && draft.category == Category.bug.rawValue {
            UserDefaults.standard.removeObject(forKey: draftKey)
        } else if let data = try? JSONEncoder().encode(draft) {
            UserDefaults.standard.set(data, forKey: draftKey)
        }
    }

    /// An attachment read back from the draft: already shrunk, so only the picture is rebuilt.
    nonisolated private static func restored(name: String, data: Data) -> Attachment {
        var preview: CGImage?
        if name.lowercased().hasSuffix(".jpg"), let source = CGImageSourceCreateWithData(data as CFData, nil) {
            preview = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceThumbnailMaxPixelSize: 240,
            ] as CFDictionary)
        }
        return Attachment(name: name, data: data, preview: preview)
    }

    // MARK: Attachments

    /// A file the person picked or dropped → an `Attachment`. Pictures are shrunk; anything else
    /// must already fit.
    nonisolated static func attachment(name: String, data: Data) throws -> Attachment {
        let isImage = UTType(filenameExtension: (name as NSString).pathExtension)?.conforms(to: .image) ?? false
        if isImage || name.isEmpty, let shot = shrink(data) {
            let base = (name as NSString).deletingPathExtension
            return Attachment(name: (base.isEmpty ? String(localized: "截图") : base) + ".jpg", data: shot.jpeg, preview: shot.image)
        }
        guard !data.isEmpty else { throw Problem.unreadable(name) }
        guard data.count <= fileBytesMax else { throw Problem.tooBig(name) }
        return Attachment(name: name, data: data, preview: nil)
    }

    /// Any image's bytes → a JPEG under 1 MB and its picture, or nil when it isn't an image.
    /// Tries smaller and softer until it fits.
    nonisolated private static func shrink(_ data: Data) -> (jpeg: Data, image: CGImage)? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0 else { return nil }
        for (side, quality) in [(1600, 0.78), (1600, 0.6), (1200, 0.6), (900, 0.5)] {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,   // upright, as the person saw it
                kCGImageSourceThumbnailMaxPixelSize: side,
            ]
            guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
            let out = NSMutableData()
            guard let dest = CGImageDestinationCreateWithData(out, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
            // Only the pixels: no properties from the source are copied over.
            CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { return nil }
            if out.length <= 1_000_000 { return (out as Data, image) }
        }
        return nil
    }

    // MARK: What goes along

    static var appVersion: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let build = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        return "\(short) (\(build))"
    }

    static var platform: String {
        #if os(macOS)
        "macos"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "ipados" : "ios"
        #endif
    }

    static var osVersion: String {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        #if os(macOS)
        let name = "macOS"
        #else
        let name = UIDevice.current.userInterfaceIdiom == .pad ? "iPadOS" : "iOS"
        #endif
        return "\(name) \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
    }

    /// 附加信息 as the form lists it; the same values go in the report. More may join later.
    static var info: [(label: String, value: String)] {
        [
            (String(localized: "版本"), String(localized: "语阅 \(appVersion)")),
            (String(localized: "系统"), osVersion),
            (String(localized: "设备"), device),
        ]
    }

    static var device: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        #endif
    }

    /// Empty is fine (no reply wanted); otherwise something@something.something.
    static func isEmail(_ text: String) -> Bool {
        let t = text.trimmingCharacters(in: .whitespaces)
        return t.isEmpty || t.wholeMatch(of: /[^@\s]+@[^@\s]+\.[^@\s]+/) != nil
    }

    static func clip(_ text: String, _ max: Int) -> String {
        guard text.unicodeScalars.count > max else { return text }
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.prefix(max))
        return String(scalars)
    }
}

/// The form: 类别, 想说的 (Markdown, with attachments), an optional 邮箱, 附加信息, then 发送.
/// A sheet on the phone and on the Mac.
struct FeedbackForm: View {
    let close: () -> Void
    @State private var category = Feedback.Category.bug
    @State private var text = ""
    @State private var contact = ""
    @State private var sending = false
    @State private var done: Bool?          // true: left an email
    @State private var notice: String?
    @State private var withInfo = true
    /// Screenshots shown in the public Issue. On until the person turns it off; the card says what it means.
    @State private var publicShots = true
    @State private var files: [Feedback.Attachment] = []
    @State private var choosingFiles = false
    @State private var restored = false
    #if os(macOS)
    @State private var pasteMonitor: Any?
    #endif
    #if os(iOS)
    @State private var picks: [PhotosPickerItem] = []
    @State private var choosingPhotos = false
    #endif
    @FocusState private var writing: Bool
    private let feedback = Feedback.shared
    private var tint: YYTint { YYTint.standard }

    private var blank: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    private var emailOK: Bool { Feedback.isEmail(contact) }
    private var canSend: Bool {
        !blank && emailOK && !sending && feedback.isConfigured
    }

    var body: some View {
        VStack(spacing: 0) {
            YYSheetHeader(title: String(localized: "反馈"), close: close)
                .padding(.leading, YYUI.s(20))
                .padding(.trailing, YYUI.s(16))
                .padding(.top, YYUI.s(14))
                .padding(.bottom, YYUI.s(6))
            if let replied = done {
                thanks(replied)
            } else {
                form
                footer
            }
        }
        .background(YYColor.bg)
        .task { await feedback.flush() }
        .onAppear(perform: restore)
        .onChange(of: category) { save() }
        .onChange(of: text) { save() }
        .onChange(of: withInfo) { save() }
        .onChange(of: publicShots) { save() }
        .onChange(of: files.map(\.id)) { if restored { feedback.saveDraftFiles(files) } }
        .onChange(of: contact) { _, new in if Feedback.isEmail(new) { feedback.savedEmail = new } }
        #if os(macOS)
        .onAppear(perform: watchPaste)
        .onDisappear {
            if let pasteMonitor { NSEvent.removeMonitor(pasteMonitor) }
            pasteMonitor = nil
        }
        #endif
        #if DEBUG
        .task { await debugFill() }
        #endif
    }

    #if DEBUG
    private func debugFill() async {
        guard UserDefaults.standard.string(forKey: "debugFeedback") == "full" else { return }
        text = "打开《红楼梦》第三回，翻到一半就**卡住**了，点哪里都没反应。\n\n- 重启后还是一样\n- 别的书正常\n- 换成 PDF 版也一样\n\n第三回开头那几页都能翻，到「林黛玉抛父进京都」后面一点开始卡，卡住以后窗口拖不动，只能强制退出。"
        contact = "reader@example.com"
        let ctx = CGContext(data: nil, width: 1200, height: 800, bitsPerComponent: 8, bytesPerRow: 0,
                            space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        ctx?.setFillColor(CGColor(red: 0.95, green: 0.94, blue: 0.91, alpha: 1)); ctx?.fill(CGRect(x: 0, y: 0, width: 1200, height: 800))
        ctx?.setFillColor(CGColor(red: 0.25, green: 0.45, blue: 0.38, alpha: 1)); ctx?.fill(CGRect(x: 120, y: 160, width: 960, height: 480))
        if let image = ctx?.makeImage() {
            let out = NSMutableData()
            if let dest = CGImageDestinationCreateWithData(out, UTType.png.identifier as CFString, 1, nil) {
                CGImageDestinationAddImage(dest, image, nil)
                CGImageDestinationFinalize(dest)
                await attach(name: "截屏 2026-11-11.png", data: out as Data)
            }
        }
        await attach(name: "yuyue.log", data: Data("debug\n".utf8))
    }
    #endif

    // MARK: Draft

    /// Back to where the person left off, then straight to typing.
    private func restore() {
        guard !restored else { return }
        if let draft = feedback.loadDraft() {
            category = draft.category
            text = draft.text
            withInfo = draft.withInfo
            publicShots = draft.publicShots
            files = draft.files
        }
        contact = feedback.savedEmail
        restored = true
        #if os(macOS)
        if let shot = feedback.takePendingShot() {
            Task { await attach(name: String(localized: "截图") + ".png", data: shot) }
        }
        #endif
        // After the sheet has risen, or the focus goes nowhere.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) { writing = true }
    }

    private func save() {
        guard restored, done == nil else { return }
        feedback.saveDraft(category: category, text: text, withInfo: withInfo, publicShots: publicShots)
    }

    #if os(macOS)
    /// ⌘V with a picture or a file on the clipboard attaches it; text pastes as usual.
    private func watchPaste() {
        guard pasteMonitor == nil else { return }
        pasteMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { event in
            guard event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command,
                  event.charactersIgnoringModifiers == "v",
                  event.window?.sheetParent != nil else { return event }
            let board = NSPasteboard.general
            if let urls = board.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
                take(urls)
                return nil
            }
            if let data = board.data(forType: .png) ?? board.data(forType: .tiff) {
                Task { await attach(name: "", data: data) }
                return nil
            }
            return event
        }
    }
    #endif

    // MARK: Writing

    private var form: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: YYUI.s(12)) {
                Text("语阅还在早期，哪里不对、想要什么，直接写。")
                    .font(YYUI.font(12.5))
                    .foregroundStyle(YYColor.t2)
                if !feedback.isConfigured {
                    // Builds without a topic and key (docs/dev/feedback.md §4) can't send; mail still works.
                    Text("这个版本发不出反馈。可以发邮件到 \(Text(verbatim: "yuyue.app@icloud.com").foregroundStyle(tint.accent))。")
                        .font(YYUI.font(12))
                        .foregroundStyle(YYColor.t2)
                        .textSelection(.enabled)
                        .padding(YYUI.s(11))
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(12)))
                }
                YYSegments(items: Feedback.Category.allCases.map { ($0, $0.title) }, selection: $category)
                editor
                if files.contains(where: \.isShot) {
                    shotsCard
                }
                emailField
                infoCard
                Label("内容会公开在 GitHub 上。邮箱、日志和其他文件只有开发者能看到", systemImage: "globe")
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, YYUI.s(2))
                if let notice {
                    HStack(alignment: .top, spacing: YYUI.s(8)) {
                        Image(systemName: "exclamationmark.circle").foregroundStyle(YYAlertTone.warn.ink)
                        Text(notice).foregroundStyle(YYColor.t1)
                    }
                    .font(YYUI.font(12))
                    .padding(YYUI.s(11))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYAlertTone.warn.ink.opacity(0.1), in: .rect(cornerRadius: YYUI.s(12)))
                    .transition(.opacity)
                }
            }
            .animation(YYMotion.standard, value: notice)
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(4))
            .padding(.bottom, YYUI.s(4))
        }
        .scrollBounceBehavior(.basedOnSize)
    }

    private var editor: some View {
        ZStack(alignment: .topLeading) {
            // The box grows with the words: an invisible copy sets the height, the editor fills it.
            Text(verbatim: text + "\n ")
                .font(YYUI.font(14))
                .padding(.horizontal, YYUI.s(5))
                .padding(.vertical, YYUI.s(8))
                .frame(maxWidth: .infinity, minHeight: YYUI.s(130), alignment: .topLeading)
                .opacity(0)
                .accessibilityHidden(true)
            TextEditor(text: $text)
                .focused($writing)
                .font(YYUI.font(14))
                .scrollContentBackground(.hidden)
                .scrollIndicators(.never)
                .scrollDisabled(true)
            if text.isEmpty {
                Text(category.placeholder)
                    .font(YYUI.font(14))
                    .foregroundStyle(YYColor.t3)
                    .padding(.top, YYUI.s(8))
                    .padding(.leading, YYUI.s(5))
                    .allowsHitTesting(false)
            }
        }
        .padding(.horizontal, YYUI.s(9))
        .padding(.top, YYUI.s(6))
        .padding(.bottom, YYUI.s(files.isEmpty ? 36 : 104))
        // Under the words, inside the box: the attachments, then 附件 and the count.
        .overlay(alignment: .bottom) {
            VStack(alignment: .leading, spacing: YYUI.s(8)) {
                if !files.isEmpty { fileStrip }
                HStack(spacing: YYUI.s(8)) {
                    if files.count < Feedback.filesMax {
                        addFile
                        #if os(macOS)
                        shotButton
                        #endif
                    }
                    Spacer(minLength: 0)
                    Text(verbatim: "Markdown")
                        .font(YYUI.font(10.5))
                        .foregroundStyle(YYColor.t3)
                        .help(String(localized: "可以用 Markdown：**粗体**、`代码`、- 列表"))
                    // Only near the end: a count from the first word reads like a quota.
                    if text.unicodeScalars.count >= Feedback.textMax - 200 {
                        Text("\(text.unicodeScalars.count)/\(Feedback.textMax)")
                            .font(YYUI.font(10.5).monospacedDigit())
                            .foregroundStyle(text.unicodeScalars.count > Feedback.textMax - 60 ? YYAlertTone.warn.ink : YYColor.t3)
                    }
                }
            }
            .padding(.horizontal, YYUI.s(10))
            .padding(.bottom, YYUI.s(9))
        }
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(14)).strokeBorder(writing ? tint.edge : YYColor.line, lineWidth: writing ? 1 : 0.5))
        .onChange(of: text) { _, new in
            let clipped = Feedback.clip(new, Feedback.textMax)
            if clipped != new { text = clipped }
        }
        #if os(macOS)
        // A screenshot on the Mac is a file: drag it, or anything else, onto the box.
        .dropDestination(for: URL.self) { urls, _ in
            take(urls)
            return true
        }
        #else
        .photosPicker(isPresented: $choosingPhotos, selection: $picks, maxSelectionCount: Feedback.filesMax - files.count, matching: .images)
        .onChange(of: picks) { _, items in
            guard !items.isEmpty else { return }
            picks = []
            Task {
                for item in items {
                    guard let data = try? await item.loadTransferable(type: Data.self) else { continue }
                    await attach(name: "", data: data)
                }
            }
        }
        #endif
        .fileImporter(isPresented: $choosingFiles, allowedContentTypes: [.item], allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { take(urls) }
        }
    }

    private var addFile: some View {
        let label = HStack(spacing: YYUI.s(4)) {
            Image(systemName: "paperclip").font(YYUI.font(11, .medium))
            Text("附件")
        }
        .font(YYUI.font(11.5))
        .foregroundStyle(YYColor.t2)
        .padding(.horizontal, YYUI.s(6))
        .frame(height: YYUI.s(24))
        .contentShape(.rect)
        #if os(iOS)
        return Menu {
            Button("照片", systemImage: "photo") { choosingPhotos = true }
            Button("文件", systemImage: "folder") { choosingFiles = true }
            Button("粘贴图片", systemImage: "doc.on.clipboard") { pasteImages() }
        } label: { label }
            .buttonStyle(.plain)
            .accessibilityLabel(String(localized: "加附件"))
        #else
        return Button { choosingFiles = true } label: { label }
            .buttonStyle(.plain)
            .help(String(localized: "截图、日志或别的文件，最多 3 个。也可以拖进来，或者 ⌘V 粘贴"))
            .accessibilityLabel(String(localized: "加附件"))
        #endif
    }

    #if os(macOS)
    private var shotButton: some View {
        Button {
            save()
            feedback.shoot()
            close()
        } label: {
            HStack(spacing: YYUI.s(4)) {
                Image(systemName: "camera.viewfinder").font(YYUI.font(11, .medium))
                Text("截图")
            }
            .font(YYUI.font(11.5))
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, YYUI.s(6))
            .frame(height: YYUI.s(24))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(String(localized: "反馈先收起来，去到要截的界面，再点屏幕下方的「截图」。截完自动回到这里"))
    }
    #endif

    #if os(iOS)
    private func pasteImages() {
        let board = UIPasteboard.general
        guard board.hasImages, let images = board.images, !images.isEmpty else {
            notice = String(localized: "剪贴板里没有图片。")
            return
        }
        Task {
            for image in images.prefix(Feedback.filesMax - files.count) {
                if let data = image.pngData() { await attach(name: "", data: data) }
            }
        }
    }
    #endif

    private var fileStrip: some View {
        HStack(spacing: YYUI.s(8)) {
            ForEach(files) { file in
                tile(file)
                    .frame(width: YYUI.s(56), height: YYUI.s(56))
                    .clipShape(.rect(cornerRadius: YYUI.s(9)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(9)).strokeBorder(YYColor.line, lineWidth: 0.5))
                    .help(file.name)
                    .overlay(alignment: .topTrailing) {
                        Button {
                            withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15)) {
                                files.removeAll { $0.id == file.id }
                            }
                        } label: {
                            Image(systemName: "xmark")
                                .font(YYUI.font(8, .bold))
                                .foregroundStyle(.white)
                                .frame(width: YYUI.s(18), height: YYUI.s(18))
                                .background(.black.opacity(0.55), in: .circle)
                                .contentShape(.circle)
                        }
                        .buttonStyle(.plain)
                        .offset(x: YYUI.s(5), y: YYUI.s(-5))
                        .accessibilityLabel(String(localized: "去掉「\(file.name)」"))
                    }
            }
        }
        .padding(.top, YYUI.s(5))
        .transition(.opacity)
    }

    @ViewBuilder
    private func tile(_ file: Feedback.Attachment) -> some View {
        if let preview = file.preview {
            Image(decorative: preview, scale: 1).resizable().scaledToFill()
        } else {
            VStack(spacing: YYUI.s(3)) {
                Image(systemName: "doc").font(YYUI.font(17))
                Text(verbatim: (file.name as NSString).pathExtension.uppercased().prefix(5).description)
                    .font(YYUI.font(8.5, .semibold))
                    .lineLimit(1)
            }
            .foregroundStyle(YYColor.t2)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(YYColor.inset)
        }
    }

    private func take(_ urls: [URL]) {
        let picked: [(String, Data?)] = urls.prefix(max(0, Feedback.filesMax - files.count)).map { url in
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            // Read a little past the cap, so a big file says so instead of loading whole.
            let data = try? FileHandle(forReadingFrom: url).read(upToCount: Feedback.fileBytesMax * 20)
            return (url.lastPathComponent, data)
        }
        Task {
            for (name, data) in picked {
                guard let data else { notice = Feedback.Problem.unreadable(name).errorDescription; continue }
                await attach(name: name, data: data)
            }
        }
    }

    private func attach(name: String, data: Data) async {
        do {
            let file = try await Self.makeAttachment(name: name, data: data)
            guard files.count < Feedback.filesMax else { return }
            withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15)) { files.append(file) }
        } catch {
            notice = (error as? LocalizedError)?.errorDescription
        }
    }

    @concurrent nonisolated private static func makeAttachment(name: String, data: Data) async throws -> Feedback.Attachment {
        try Feedback.attachment(name: name, data: data)
    }

    private var emailField: some View {
        HStack(spacing: YYUI.s(10)) {
            Text("邮箱").font(YYUI.font(13)).foregroundStyle(YYColor.t1)
            TextField("想收到回复就留一个，可不填", text: $contact)
                .textFieldStyle(.plain)
                .font(YYUI.font(13))
                .multilineTextAlignment(.trailing)
                .textContentType(.emailAddress)
                #if os(iOS)
                .keyboardType(.emailAddress)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                #endif
                .onChange(of: contact) { _, new in
                    let clipped = Feedback.clip(new, Feedback.contactMax)
                    if clipped != new { contact = clipped }
                }
        }
        .padding(.horizontal, YYUI.s(14))
        .frame(height: YYUI.s(42))
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(emailOK ? YYColor.line : YYAlertTone.warn.ink, lineWidth: emailOK ? 0.5 : 1))
    }

    /// What goes along besides the words, shown as it is. On unless the person turns it off.
    private var infoCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: YYUI.s(10)) {
                VStack(alignment: .leading, spacing: YYUI.s(2)) {
                    Text("附加信息").font(YYUI.font(13)).foregroundStyle(YYColor.t1)
                    Text(withInfo ? "一起发出，方便查问题" : "不发送，查问题会难一点")
                        .font(YYUI.font(11))
                        .foregroundStyle(YYColor.t3)
                }
                Spacer(minLength: 0)
                Toggle(String(localized: "附加信息"), isOn: $withInfo.animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.18)))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .tint(tint.accent)
                    #if os(macOS)
                    .controlSize(.small)
                    #endif
            }
            .padding(.vertical, YYUI.s(10))
            if withInfo {
                Rectangle().fill(YYColor.line).frame(height: 0.5)
                VStack(alignment: .leading, spacing: YYUI.s(6)) {
                    ForEach(Feedback.info, id: \.label) { item in
                        HStack(alignment: .firstTextBaseline, spacing: YYUI.s(12)) {
                            Text(item.label)
                                .foregroundStyle(YYColor.t3)
                                .frame(width: YYUI.s(34), alignment: .leading)
                            Text(verbatim: item.value)
                                .foregroundStyle(YYColor.t2)
                                .textSelection(.enabled)
                        }
                        .font(YYUI.font(12))
                    }
                }
                .padding(.vertical, YYUI.s(10))
                .transition(.opacity)
            }
        }
        .padding(.horizontal, YYUI.s(14))
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    /// Only with a screenshot attached. On by default; what it means is said right there, and off
    /// still sends the screenshots, sealed for the developer alone.
    private var shotsCard: some View {
        HStack(alignment: .top, spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(2)) {
                Text("截图公开在 GitHub 上").font(YYUI.font(13)).foregroundStyle(YYColor.t1)
                Text(publicShots
                     ? "语阅是开源项目，截图和文字一起公开，别人也能帮忙查。发之前看一眼截图里有没有隐私。"
                     : "截图照样发出，只有开发者能看到。")
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Toggle(String(localized: "截图公开在 GitHub 上"), isOn: $publicShots)
                .labelsHidden()
                .toggleStyle(.switch)
                .tint(tint.accent)
                #if os(macOS)
                .controlSize(.small)
                #endif
        }
        .padding(.vertical, YYUI.s(10))
        .padding(.horizontal, YYUI.s(14))
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
        .transition(.opacity)
    }

    private var meta: String {
        if !feedback.isConfigured { return String(localized: "这个版本发不出反馈") }
        if !emailOK { return String(localized: "邮箱好像不对") }
        return ""
    }

    private var footer: some View {
        #if os(macOS)
        YYSheetFooter(
            meta: meta.isEmpty ? nil : meta,
            secondaryTitle: String(localized: "取消"),
            onSecondary: close,
            primaryTitle: sending ? String(localized: "正在发送…") : String(localized: "发送"),
            primaryHint: "⌘↩",
            onPrimary: submit,
            primaryDisabled: !canSend
        )
        // ↩ writes a new line in the box; ⌘↩ sends.
        .background {
            Button("", action: submit)
                .keyboardShortcut(.return, modifiers: .command)
                .disabled(!canSend)
                .opacity(0)
                .accessibilityHidden(true)
        }
        #else
        VStack(spacing: YYUI.s(6)) {
            Button(sending ? String(localized: "正在发送…") : String(localized: "发送"), action: submit)
                .buttonStyle(YYButtonStyle(kind: .main, wide: true, height: 44))
                .disabled(!canSend)
            if !meta.isEmpty {
                Text(meta).font(YYUI.font(11)).foregroundStyle(emailOK ? YYColor.t3 : YYAlertTone.warn.ink)
            }
        }
        .padding(.horizontal, YYUI.s(20))
        .padding(.top, YYUI.s(10))
        .padding(.bottom, YYUI.s(12))
        #endif
    }

    private func submit() {
        guard canSend else { return }
        sending = true
        notice = nil
        Task {
            defer { sending = false }
            do {
                switch try await feedback.send(category, text: text, contact: contact, withInfo: withInfo,
                                               publicShots: publicShots, files: files) {
                case .sent:
                    feedback.clearDraft()
                    withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.25)) {
                        done = !contact.trimmingCharacters(in: .whitespaces).isEmpty
                    }
                case .queued(let busy):
                    text = ""
                    files = []
                    feedback.clearDraft()
                    notice = busy
                        ? String(localized: "反馈服务现在太忙，这条先存在这台设备上，过一会儿自动发出，不用再点。")
                        : String(localized: "没网，这条先存在这台设备上，联网后自动发出，不用再点。待发 \(feedback.queued.count) 条。")
                }
            } catch {
                notice = (error as? LocalizedError)?.errorDescription ?? String(localized: "没发出去，内容有问题。")
            }
        }
    }

    // MARK: Sent

    private func thanks(_ replied: Bool) -> some View {
        VStack(spacing: YYUI.s(12)) {
            Spacer(minLength: YYUI.s(24))
            Image(systemName: "checkmark")
                .font(YYUI.font(24, .semibold))
                .foregroundStyle(tint.accent)
                .frame(width: YYUI.s(60), height: YYUI.s(60))
                .background(tint.black, in: .circle)
                .overlay(Circle().strokeBorder(tint.edge, lineWidth: 0.5))
                .symbolEffect(.bounce, value: done)
            Text("收到了，谢谢").font(YYUI.font(17, .semibold)).foregroundStyle(YYColor.t1)
            Text(replied ? "会认真看。需要的话，会发邮件回复你。" : "会认真看。没留邮箱，所以不会回复你。")
                .font(YYUI.font(12.5))
                .foregroundStyle(YYColor.t2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
            HStack(spacing: YYUI.s(10)) {
                Button("再写一条") {
                    text = ""
                    category = .bug
                    withInfo = true
                    publicShots = true
                    files = []
                    done = nil
                }
                .buttonStyle(YYButtonStyle(kind: .plain))
                Button("完成", action: close)
                    .buttonStyle(YYButtonStyle(kind: .main))
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, YYUI.s(6))
            Spacer(minLength: YYUI.s(32))
        }
        .frame(maxWidth: .infinity)
        .padding(.horizontal, YYUI.s(20))
    }
}

#if os(macOS)
/// The bar that floats while the person goes to what they want to shoot. A non-activating
/// panel: clicks on it don't take the focus from the window being shot, and it stays above
/// every window and Space.
@MainActor
enum ShotBar {
    private static var panel: NSPanel?

    static func show(shoot: @escaping () -> Void, back: @escaping () -> Void) {
        hide()
        let panel = NSPanel(contentRect: .zero, styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isMovableByWindowBackground = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        let host = NSHostingView(rootView: ShotBarView(shoot: shoot, back: back))
        host.setFrameSize(host.fittingSize)
        panel.contentView = host
        panel.setContentSize(host.fittingSize)
        if let screen = NSScreen.main?.visibleFrame {
            panel.setFrameOrigin(NSPoint(x: screen.midX - host.fittingSize.width / 2, y: screen.minY + 64))
        }
        panel.orderFrontRegardless()
        self.panel = panel
    }

    static func hide() {
        panel?.orderOut(nil)
        panel = nil
    }
}

private struct ShotBarView: View {
    let shoot: () -> Void
    let back: () -> Void
    private var tint: YYTint { YYTint.standard }

    var body: some View {
        HStack(spacing: YYUI.s(10)) {
            Image(systemName: "text.bubble").foregroundStyle(tint.accent)
            Text("去到要截的界面，然后点")
                .foregroundStyle(YYColor.t2)
            Button(action: shoot) {
                Label("截图", systemImage: "camera.viewfinder")
            }
            .buttonStyle(YYButtonStyle(kind: .main))
            .keyboardShortcut(.defaultAction)
            Button("回到反馈", action: back)
                .buttonStyle(YYButtonStyle(kind: .plain))
        }
        .font(YYUI.font(13))
        .padding(.leading, YYUI.s(16))
        .padding(.trailing, YYUI.s(8))
        .padding(.vertical, YYUI.s(8))
        .background(.regularMaterial, in: .capsule)
        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
        .padding(YYUI.s(10))
        .fixedSize()
    }
}

/// 帮助 › 给语阅反馈…, in every window: brings the shelf window forward and opens the form there.
struct FeedbackCommands: Commands {
    @Environment(\.openWindow) private var openWindow

    var body: some Commands {
        CommandGroup(after: .help) {
            Button("给语阅反馈…") {
                openWindow(id: "library")
                Feedback.shared.requested = true
            }
        }
    }
}
#endif
