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
/// POSTed to ntfy.sh (docs/dev/feedback.md). 语阅 is open source: the title, body, optional device
/// info, and all attachments become a public GitHub Issue so anyone in the community can help debug.
/// No email or contact info is collected; without a network the message waits on this device.
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
        var titlePlaceholder: String {
            switch self {
            case .bug: String(localized: "一句话概括遇到的问题")
            case .idea: String(localized: "一句话概括你的建议")
            case .other: String(localized: "一句话标题")
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

    /// One attachment: a screenshot, or another file such as a book that won't open. A picture is
    /// shrunk first (at most 1600 px on the long side, re-encoded as JPEG, so what the original
    /// carried — place, camera, time — is gone); anything else goes as it is, up to the core's 1.5 MB.
    nonisolated struct Attachment: Identifiable, @unchecked Sendable {
        let id = UUID()
        let name: String
        let data: Data
        let preview: CGImage?
        /// A picture shrunk to a JPEG (inline image in the public Issue); other files become download links.
        var isShot: Bool { preview != nil }
    }

    /// The core's own caps (`TITLE_MAX`, `TEXT_MAX`), counted in Unicode scalars as Rust counts chars.
    static let titleMax = 60
    static let textMax = 800
    static let filesMax = 3
    nonisolated static let fileBytesMax = 1_500_000
    static let issuesURL = URL(string: "https://github.com/inokoru/yuyue/issues")!
    static let supportEmail = "yuyue.app@icloud.com"
    static let mailURL = URL(string: "mailto:\(supportEmail)")!
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
    /// to the bar; × dismisses the bar directly without reopening the form.
    func shoot() {
        guard !shooting else { return }
        shooting = true
        // After the sheet has slid away.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.3) { [self] in
            ShotBar.show(shoot: { [self] in capture() }, cancel: { [self] in cancelShot() })
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
                ShotBar.show(shoot: { [self] in capture() }, cancel: { [self] in cancelShot() })
            }
        }
    }

    private func cancelShot() {
        ShotBar.hide()
        shooting = false
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
        // Clean up keys removed in earlier versions (daily cap & saved email).
        UserDefaults.standard.removeObject(forKey: "feedback.sentAt")
        UserDefaults.standard.removeObject(forKey: "feedback.email")
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

    /// Public builds carry no topic; the form says so and does not send.
    var isConfigured: Bool { !topic.isEmpty }

    // MARK: Sending

    /// `withInfo` false: the person turned 附加信息 off; version and system stay here.
    ///
    /// Attachments go first, each its own ntfy attachment; the report then names them.
    /// They need a network now: if one fails nothing is counted and the form keeps everything.
    /// Only the words wait in the queue (ntfy keeps an attachment 3 hours, too short to hold).
    func send(_ category: Category, title: String, text: String, withInfo: Bool = true,
              files: [Attachment] = []) async throws -> Outcome {
        guard isConfigured else { throw Problem.notConfigured }
        var ids: [String] = []
        for (n, file) in files.prefix(Self.filesMax).enumerated() {
            do {
                ids.append(try await upload(file, n: n + 1))
            } catch Problem.busy { throw Problem.filesBusy } catch { throw Problem.filesOffline }
        }
        let body = try feedbackMessage(draft: FeedbackDraft(
            category: category.rawValue,
            title: title,
            text: text,
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
            // At most 10, the newest kept.
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

    /// One public attachment. Screenshots go up as JPEG marked `v2shot`; other files go up raw
    /// marked `v2file <percent-encoded-name>` with an ASCII-safe `Filename` header.
    private func upload(_ file: Attachment, n: Int) async throws -> String {
        let headers: [String: String]
        if file.isShot {
            headers = ["Filename": "shot-\(n).jpg", "X-Message": "v2shot"]
        } else {
            let ext = Self.safeExt(file.name)
            let encoded = Self.encodeHeaderName(file.name)
            let mark = encoded.isEmpty ? "v2file" : "v2file \(encoded)"
            headers = ["Filename": "file-\(n)\(ext)", "X-Message": mark]
        }
        let data = try await ntfy("PUT", file.data, timeout: 40, headers: headers)
        guard let reply = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let id = reply["id"] as? String, !id.isEmpty else { throw URLError(.badServerResponse) }
        return id
    }

    private static let headerNameAllowed = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789._-~")

    private static func safeExt(_ name: String) -> String {
        let ext = (name as NSString).pathExtension.lowercased()
        guard !ext.isEmpty, ext.count <= 10,
              ext.unicodeScalars.allSatisfy({ ($0 >= "a" && $0 <= "z") || ($0 >= "0" && $0 <= "9") }) else {
            return ".bin"
        }
        return ".\(ext)"
    }

    private static func encodeHeaderName(_ name: String) -> String {
        let base = (name as NSString).lastPathComponent.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !base.isEmpty else { return "" }
        let clipped = clip(base, 80)
        return clipped.addingPercentEncoding(withAllowedCharacters: headerNameAllowed) ?? ""
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

    // MARK: Draft

    /// What the form held when it closed, so nothing written is lost. Cleared once it is sent
    /// (or queued). Attachments are files beside it, in order.
    private struct Draft: Codable {
        var category: String
        var title: String?
        var text: String
        var withInfo: Bool
        var files: [String]
    }

    private let draftKey = "feedback.draft"

    private var draftDir: URL {
        URL.applicationSupportDirectory.appending(path: "Yuyue/Feedback/Draft", directoryHint: .isDirectory)
    }

    func saveDraft(category: Category, title: String, text: String, withInfo: Bool) {
        var draft = loadDraftRecord() ?? Draft(category: category.rawValue, title: "", text: "", withInfo: true, files: [])
        draft.category = category.rawValue
        draft.title = title
        draft.text = text
        draft.withInfo = withInfo
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
        var draft = loadDraftRecord() ?? Draft(category: Category.bug.rawValue, title: "", text: "", withInfo: true, files: [])
        draft.files = names
        writeDraft(draft)
    }

    func loadDraft() -> (category: Category, title: String, text: String, withInfo: Bool, files: [Attachment])? {
        guard let draft = loadDraftRecord() else { return nil }
        let files: [Attachment] = draft.files.enumerated().compactMap { i, name in
            guard let data = try? Data(contentsOf: draftDir.appending(path: "\(i)")) else { return nil }
            return Self.restored(name: name, data: data)
        }
        return (Category(rawValue: draft.category) ?? .bug, draft.title ?? "", draft.text, draft.withInfo, files)
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
        if (draft.title ?? "").isEmpty && draft.text.isEmpty && draft.files.isEmpty && draft.withInfo
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
        let ver = v.patchVersion == 0
            ? "\(v.majorVersion).\(v.minorVersion)"
            : "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)"
        return "\(name) \(ver)"
    }

    /// 附加信息 as the form lists it; the same values go in the report. More may join later.
    static var info: [(label: String, value: String)] {
        [
            (String(localized: "版本"), String(localized: "语阅 \(appVersion)")),
            (String(localized: "系统"), osVersion),
            (String(localized: "设备"), device),
        ]
    }

    /// Compact one-line summary shown in the collapsed 附带设备信息 row.
    static var shortInfo: String {
        let short = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        return String(localized: "语阅 \(short) · \(osVersion) · \(device)")
    }

    static var device: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        #endif
    }

    static func clip(_ text: String, _ max: Int) -> String {
        guard text.unicodeScalars.count > max else { return text }
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.prefix(max))
        return String(scalars)
    }
}

/// The form: 类别, 标题, 详细描述 (Markdown, with attachments), 附加信息, a mailto hint for
/// private replies, then 发送. A sheet on the phone and on the Mac.
struct FeedbackForm: View {
    let close: () -> Void
    @State private var category = Feedback.Category.bug
    @State private var title = ""
    @State private var text = ""
    @State private var sending = false
    @State private var done = false
    @State private var notice: String?
    @State private var withInfo = true
    @State private var infoExpanded = false
    @State private var files: [Feedback.Attachment] = []
    @State private var choosingFiles = false
    @State private var restored = false
    #if os(macOS)
    @State private var pasteMonitor: Any?
    @State private var writing = false
    @State private var dropping = false
    @FocusState private var titleFocused: Bool
    #else
    private enum Field { case title, body }
    @State private var picks: [PhotosPickerItem] = []
    @State private var choosingPhotos = false
    @FocusState private var field: Field?
    #endif
    private let feedback = Feedback.shared
    private var tint: YYTint { YYTint.standard }

    private var titleBlank: Bool { title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    private var textBlank: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    private var blank: Bool { titleBlank || textBlank }
    private var hasDraft: Bool { !titleBlank || !textBlank || !files.isEmpty }
    private var canSend: Bool {
        !blank && !sending && feedback.isConfigured
    }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: YYUI.s(3)) {
                YYSheetHeader(title: String(localized: "反馈"), close: close)
                if !done {
                    Text("语阅还在早期，哪里不对、想要什么，直接写。")
                        .font(YYUI.font(12))
                        .foregroundStyle(YYColor.t3)
                }
            }
            .padding(.leading, YYUI.s(20))
            .padding(.trailing, YYUI.s(16))
            #if os(iOS)
            .padding(.top, YYUI.sheetTop)
            #else
            .padding(.top, YYUI.s(14))
            #endif
            .padding(.bottom, YYUI.s(10))
            if done {
                thanks
            } else {
                form
                    .disabled(sending)
                footer
            }
        }
        .background(YYColor.bg)
        .task { await feedback.flush() }
        .onAppear(perform: restore)
        .onChange(of: category) { save() }
        .onChange(of: title) { save() }
        .onChange(of: text) { save() }
        .onChange(of: withInfo) { save() }
        .onChange(of: files.map(\.id)) { if restored { feedback.saveDraftFiles(files) } }
        #if os(macOS)
        .onAppear(perform: watchPaste)
        .onDisappear {
            if let pasteMonitor { NSEvent.removeMonitor(pasteMonitor) }
            pasteMonitor = nil
        }
        #endif
        .airTrafficScreen("Feedback")
        #if DEBUG
        .task { await debugFill() }
        #endif
    }

    #if DEBUG
    private func debugFill() async {
        guard UserDefaults.standard.string(forKey: "debugFeedback") == "full" else { return }
        title = "打开《红楼梦》第三回翻到一半卡死"
        text = "打开《红楼梦》第三回，翻到一半就**卡住**了，点哪里都没反应。\n\n- 重启后还是一样\n- 别的书正常\n- 换成 PDF 版也一样\n\n第三回开头那几页都能翻，到「林黛玉抛父进京都」后面一点开始卡，卡住以后窗口拖不动，只能强制退出。"
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
        await attach(name: "sample.txt", data: Data("debug\n".utf8))
    }
    #endif

    // MARK: Draft

    /// Back to where the person left off, then straight to typing.
    private func restore() {
        guard !restored else { return }
        if let draft = feedback.loadDraft() {
            category = draft.category
            title = draft.title
            text = draft.text
            withInfo = draft.withInfo
            files = draft.files
        }
        restored = true
        #if os(macOS)
        if let shot = feedback.takePendingShot() {
            Task { await attach(name: String(localized: "截图") + ".png", data: shot) }
        }
        #endif
        // After the sheet has risen, or the focus goes nowhere.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            #if os(macOS)
            if title.isEmpty { titleFocused = true } else { writing = true }
            #else
            field = title.isEmpty ? .title : .body
            #endif
        }
    }

    private func save() {
        guard restored, !done else { return }
        feedback.saveDraft(category: category, title: title, text: text, withInfo: withInfo)
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
                if !feedback.isConfigured {
                    // Builds without a topic and key (see docs/dev/feedback.md) can't send; mail still works.
                    HStack(spacing: YYUI.s(4)) {
                        Text("这个版本发不出反馈。可以发邮件到")
                        Link(destination: Feedback.mailURL) {
                            Text(verbatim: Feedback.supportEmail)
                                .foregroundStyle(tint.accent)
                        }
                        .buttonStyle(.plain)
                    }
                    .font(YYUI.font(12))
                    .foregroundStyle(YYColor.t2)
                    .padding(YYUI.s(11))
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(12)))
                }
                YYSegments(items: Feedback.Category.allCases.map { ($0, $0.title) }, selection: $category)
                titleField
                editor
                infoCard
                mailHint
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
            .padding(.top, YYUI.s(2))
            .padding(.bottom, YYUI.s(12))
        }
        .scrollBounceBehavior(.basedOnSize)
    }

    private var isTitleFocused: Bool {
        #if os(macOS)
        titleFocused
        #else
        field == .title
        #endif
    }

    private var isEditorFocused: Bool {
        #if os(macOS)
        writing
        #else
        field == .body
        #endif
    }

    private var titleField: some View {
        HStack(spacing: YYUI.s(8)) {
            TextField(category.titlePlaceholder, text: $title)
                .textFieldStyle(.plain)
                .font(YYUI.font(14, .medium))
                .foregroundStyle(YYColor.t1)
                #if os(macOS)
                .focused($titleFocused)
                .onSubmit {
                    titleFocused = false
                    writing = true
                }
                #else
                .focused($field, equals: .title)
                .submitLabel(.next)
                .onSubmit { field = .body }
                #endif
            if title.unicodeScalars.count >= Feedback.titleMax - 15 {
                Text("\(title.unicodeScalars.count)/\(Feedback.titleMax)")
                    .font(YYUI.font(10.5).monospacedDigit())
                    .foregroundStyle(title.unicodeScalars.count > Feedback.titleMax - 5 ? YYAlertTone.warn.ink : YYColor.t3)
            }
        }
        .padding(.horizontal, YYUI.s(14))
        .padding(.vertical, YYUI.s(10))
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
        .overlay {
            RoundedRectangle(cornerRadius: YYUI.s(14))
                .strokeBorder(isTitleFocused ? tint.edge : YYColor.line, lineWidth: isTitleFocused ? 1 : 0.5)
        }
        .onChange(of: title) { _, new in
            let flat = new.replacingOccurrences(of: "\r\n", with: " ")
                .replacingOccurrences(of: "\n", with: " ")
                .replacingOccurrences(of: "\r", with: " ")
            let clipped = Feedback.clip(flat, Feedback.titleMax)
            if clipped != new { title = clipped }
        }
    }

    private var editor: some View {
        VStack(alignment: .leading, spacing: 0) {
            ZStack(alignment: .topLeading) {
                // The box grows with the words: an invisible copy sets the height, the editor fills it.
                Text(verbatim: text + "\n ")
                    .font(YYUI.font(14))
                    .padding(.horizontal, YYUI.s(5))
                    #if os(macOS)
                    .padding(.top, YYUI.s(4))
                    .padding(.bottom, YYUI.s(8))
                    #else
                    .padding(.vertical, YYUI.s(8))
                    #endif
                    .frame(maxWidth: .infinity, minHeight: YYUI.s(104), alignment: .topLeading)
                    .opacity(0)
                    .accessibilityHidden(true)
                #if os(macOS)
                MacFeedbackEditor(text: $text, writing: $writing)
                    .padding(.top, YYUI.s(4))
                #else
                TextEditor(text: $text)
                    .focused($field, equals: .body)
                    .font(YYUI.font(14))
                    .scrollContentBackground(.hidden)
                    .scrollIndicators(.never)
                    .scrollDisabled(true)
                #endif
                if text.isEmpty {
                    Text(category.placeholder)
                        .font(YYUI.font(14))
                        .foregroundStyle(YYColor.t3)
                        #if os(macOS)
                        .padding(.top, YYUI.s(4))
                        .padding(.leading, YYUI.s(5))
                        #else
                        .padding(.top, YYUI.s(8))
                        .padding(.leading, YYUI.s(5))
                        #endif
                        .animation(nil, value: category)
                        .allowsHitTesting(false)
                }
            }
            .padding(.horizontal, YYUI.s(9))
            .padding(.top, YYUI.s(8))
            .padding(.bottom, YYUI.s(4))

            if !files.isEmpty {
                fileStrip
                    .padding(.horizontal, YYUI.s(12))
                    .padding(.bottom, YYUI.s(10))
            }

            Rectangle()
                .fill(YYColor.line)
                .frame(height: 0.5)

            HStack(spacing: YYUI.s(4)) {
                if files.count < Feedback.filesMax {
                    addFile
                    #if os(macOS)
                    shotButton
                    #endif
                }
                Spacer(minLength: 0)
                #if os(macOS)
                Text("支持 ⌘V 粘图 · 拖入文件")
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                    .help(String(localized: "也支持 Markdown：**粗体**、`代码`、- 列表"))
                #else
                Text(verbatim: "Markdown")
                    .font(YYUI.font(10.5))
                    .foregroundStyle(YYColor.t3)
                #endif
                // Only near the end: a count from the first word reads like a quota.
                if text.unicodeScalars.count >= Feedback.textMax - 200 {
                    Text("\(text.unicodeScalars.count)/\(Feedback.textMax)")
                        .font(YYUI.font(10.5).monospacedDigit())
                        .foregroundStyle(text.unicodeScalars.count > Feedback.textMax - 60 ? YYAlertTone.warn.ink : YYColor.t3)
                }
            }
            .padding(.horizontal, YYUI.s(10))
            .padding(.vertical, YYUI.s(7))
        }
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
        .overlay {
            RoundedRectangle(cornerRadius: YYUI.s(14))
                .strokeBorder(isEditorFocused ? tint.edge : YYColor.line, lineWidth: isEditorFocused ? 1 : 0.5)
        }
        #if os(macOS)
        .overlay {
            if dropping {
                VStack(spacing: YYUI.s(5)) {
                    Image(systemName: "arrow.down.doc")
                        .font(YYUI.font(18, .medium))
                    Text("松手添加截图或文件")
                        .font(YYUI.font(12.5, .medium))
                }
                .foregroundStyle(tint.accent)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .background(tint.soft.opacity(0.92), in: .rect(cornerRadius: YYUI.s(14)))
                .overlay {
                    RoundedRectangle(cornerRadius: YYUI.s(14))
                        .strokeBorder(tint.accent, style: StrokeStyle(lineWidth: 1.5, dash: [5, 4]))
                }
                .allowsHitTesting(false)
                .transition(.opacity)
            }
        }
        .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.14), value: dropping)
        #endif
        .onChange(of: text) { _, new in
            let clipped = Feedback.clip(new, Feedback.textMax)
            if clipped != new { text = clipped }
        }
        #if os(macOS)
        // A screenshot on the Mac is a file: drag it, or anything else, onto the box.
        .dropDestination(for: URL.self) { urls, _ in
            take(urls)
            return true
        } isTargeted: { targeted in
            dropping = targeted
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
        #if os(iOS)
        Menu {
            Button("照片", systemImage: "photo") { choosingPhotos = true }
            Button("文件", systemImage: "folder") { choosingFiles = true }
            Button("粘贴图片", systemImage: "doc.on.clipboard") { pasteImages() }
        } label: {
            HStack(spacing: YYUI.s(4)) {
                Image(systemName: "paperclip").font(YYUI.font(11.5, .medium))
                Text("附件")
            }
            .font(YYUI.font(12))
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, YYUI.s(8))
            .frame(height: YYUI.s(26))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(String(localized: "加附件"))
        #else
        FeedbackToolButton(title: String(localized: "附件"), symbol: "paperclip",
                           help: String(localized: "截图或文件，最多 3 个。也可以拖进来，或者 ⌘V 粘贴")) {
            choosingFiles = true
        }
        .accessibilityLabel(String(localized: "加附件"))
        #endif
    }

    #if os(macOS)
    private var shotButton: some View {
        FeedbackToolButton(title: String(localized: "截图"), symbol: "camera.viewfinder",
                           help: String(localized: "反馈先收起来，去到要截的界面，再点屏幕下方的「截图」。截完自动回到这里")) {
            save()
            feedback.shoot()
            close()
        }
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

    /// Compact single-line device summary card, expandable on click.
    private var infoCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: YYUI.s(10)) {
                Button {
                    guard withInfo else { return }
                    withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.18)) {
                        infoExpanded.toggle()
                    }
                } label: {
                    HStack(spacing: YYUI.s(6)) {
                        Text("附带设备信息")
                            .font(YYUI.font(12.5))
                            .foregroundStyle(YYColor.t1)
                            .fixedSize()
                        Text(withInfo ? Feedback.shortInfo : String(localized: "已关闭，查问题会难一点"))
                            .font(YYUI.font(11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                            .truncationMode(.tail)
                        if withInfo {
                            Image(systemName: "chevron.down")
                                .font(YYUI.font(9, .semibold))
                                .foregroundStyle(YYColor.t3)
                                .rotationEffect(.degrees(infoExpanded ? 180 : 0))
                        }
                        Spacer(minLength: 0)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .disabled(!withInfo)
                Toggle(String(localized: "附带设备信息"), isOn: $withInfo.animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.18)))
                    .labelsHidden()
                    .toggleStyle(.switch)
                    .tint(tint.accent)
                    #if os(macOS)
                    .controlSize(.small)
                    #endif
                    .onChange(of: withInfo) { _, on in
                        if !on { infoExpanded = false }
                    }
            }
            .padding(.horizontal, YYUI.s(14))
            .frame(height: YYUI.s(40))

            if withInfo && infoExpanded {
                Rectangle()
                    .fill(YYColor.line)
                    .frame(height: 0.5)
                    .padding(.horizontal, YYUI.s(14))
                HStack(alignment: .top, spacing: YYUI.s(16)) {
                    ForEach(Feedback.info, id: \.label) { item in
                        VStack(alignment: .leading, spacing: YYUI.s(2)) {
                            Text(item.label)
                                .font(YYUI.font(10.5))
                                .foregroundStyle(YYColor.t3)
                            Text(verbatim: item.value)
                                .font(YYUI.font(11.5))
                                .foregroundStyle(YYColor.t2)
                                .textSelection(.enabled)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                    }
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.vertical, YYUI.s(10))
                .transition(.opacity)
            }
        }
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
        .overlay {
            RoundedRectangle(cornerRadius: YYUI.s(12))
                .strokeBorder(YYColor.line, lineWidth: 0.5)
        }
    }

    /// Out-of-band mail link for anyone who wants a reply or prefers not to post publicly.
    /// Opening Mail lets the user write directly without the app ever collecting their email.
    private var mailHint: some View {
        HStack(spacing: YYUI.s(5)) {
            Image(systemName: "envelope")
                .font(YYUI.font(11))
                .foregroundStyle(YYColor.t3)
            Text("需要回复或不想公开？")
                .foregroundStyle(YYColor.t3)
            Link(destination: Feedback.mailURL) {
                Text("直接发邮件 ↗")
                    .foregroundStyle(tint.accent)
            }
            .buttonStyle(.plain)
            Spacer(minLength: 0)
        }
        .font(YYUI.font(11.5))
        .padding(.horizontal, YYUI.s(4))
    }

    private var meta: String {
        feedback.isConfigured ? "" : String(localized: "这个版本发不出反馈")
    }

    private func resetDraft() {
        withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15)) {
            title = ""
            text = ""
            files = []
            notice = nil
        }
        feedback.clearDraft()
    }

    /// Always-visible disclosure pinned inside the fixed footer above Send so users never miss
    /// that feedback bodies and attachments are published publicly on GitHub Issues.
    private var publicNotice: some View {
        HStack(spacing: YYUI.s(4)) {
            Image(systemName: "globe")
                .font(YYUI.font(11, .medium))
                .foregroundStyle(tint.accent.opacity(0.88))
            Text("正文和附件将公开在")
            Link(destination: Feedback.issuesURL) {
                Text(verbatim: "GitHub Issues ↗")
                    .foregroundStyle(tint.accent)
            }
            .buttonStyle(.plain)
            Text("· 请勿上传隐私或版权书")
                .lineLimit(1)
                .minimumScaleFactor(0.85)
            Spacer(minLength: 0)
        }
        .font(YYUI.font(11))
        .foregroundStyle(YYColor.t2)
    }

    private var footer: some View {
        VStack(spacing: 0) {
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            #if os(macOS)
            VStack(alignment: .leading, spacing: YYUI.s(10)) {
                publicNotice
                HStack(spacing: YYUI.s(8)) {
                    if !meta.isEmpty {
                        Text(meta)
                            .font(YYUI.font(11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    } else if hasDraft && !sending {
                        HStack(spacing: YYUI.s(6)) {
                            Text("草稿已自动保存")
                            Button("清空", action: resetDraft)
                                .buttonStyle(.plain)
                                .foregroundStyle(YYColor.t2)
                                .underline()
                        }
                        .font(YYUI.font(11))
                        .foregroundStyle(YYColor.t3)
                        .transition(.opacity)
                    }
                    Spacer(minLength: YYUI.s(8))
                    Button(String(localized: "取消"), action: close)
                        .buttonStyle(YYButtonStyle(kind: .plain))
                        .disabled(sending)
                    Button(action: submit) {
                        HStack(spacing: YYUI.s(6)) {
                            if sending {
                                ProgressView()
                                    .controlSize(.mini)
                                    .tint(tint.onBlack)
                            }
                            Text(sending ? String(localized: "正在发送…") : String(localized: "发送"))
                        }
                    }
                    .buttonStyle(YYButtonStyle(kind: .main, hint: sending ? nil : "⌘↩"))
                    .disabled(!canSend)
                    .keyboardShortcut(.return, modifiers: .command)
                }
            }
            .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15), value: hasDraft)
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(10))
            .padding(.bottom, YYUI.s(14))
            .fixedSize(horizontal: false, vertical: true)
            #else
            VStack(spacing: YYUI.s(8)) {
                publicNotice
                Button(action: submit) {
                    HStack(spacing: YYUI.s(6)) {
                        if sending {
                            ProgressView()
                                .controlSize(.small)
                                .tint(tint.onBlack)
                        }
                        Text(sending ? String(localized: "正在发送…") : String(localized: "发送"))
                    }
                }
                .buttonStyle(YYButtonStyle(kind: .main, wide: true, height: 44))
                .disabled(!canSend)
                if !meta.isEmpty {
                    Text(meta).font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                } else if hasDraft && !sending {
                    HStack(spacing: YYUI.s(6)) {
                        Text("草稿已自动保存")
                        Button("清空", action: resetDraft)
                            .buttonStyle(.plain)
                            .foregroundStyle(YYColor.t2)
                    }
                    .font(YYUI.font(11))
                    .foregroundStyle(YYColor.t3)
                }
            }
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(10))
            .padding(.bottom, YYUI.s(12))
            #endif
        }
    }

    private func submit() {
        guard canSend else { return }
        sending = true
        notice = nil
        Task {
            defer { sending = false }
            do {
                switch try await feedback.send(category, title: title, text: text, withInfo: withInfo,
                                               files: files) {
                case .sent:
                    AirTraffic.trackFeedbackSubmitted(
                        imageCount: files.prefix(Feedback.filesMax).count { $0.isShot },
                        includeDeviceInfo: withInfo
                    )
                    feedback.clearDraft()
                    withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.25)) {
                        done = true
                    }
                case .queued(let busy):
                    title = ""
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

    private var thanks: some View {
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
            Text("稍后会出现在 GitHub 问题板上。")
                .font(YYUI.font(12.5))
                .foregroundStyle(YYColor.t2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
            HStack(spacing: YYUI.s(4)) {
                Text("需要回复或补充私密内容？")
                    .foregroundStyle(YYColor.t3)
                Link(destination: Feedback.mailURL) {
                    Text("直接发邮件 ↗")
                        .foregroundStyle(tint.accent)
                }
                .buttonStyle(.plain)
            }
            .font(YYUI.font(11.5))
            HStack(spacing: YYUI.s(10)) {
                Button("再写一条") {
                    title = ""
                    text = ""
                    category = .bug
                    withInfo = true
                    infoExpanded = false
                    files = []
                    done = false
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
private struct FeedbackToolButton: View {
    let title: String
    let symbol: String
    let help: String
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: YYUI.s(4)) {
                Image(systemName: symbol).font(YYUI.font(11.5, .medium))
                Text(title)
            }
            .font(YYUI.font(12))
            .foregroundStyle(hover ? YYColor.t1 : YYColor.t2)
            .padding(.horizontal, YYUI.s(8))
            .frame(height: YYUI.s(26))
            .background(hover ? YYColor.inset : .clear, in: .rect(cornerRadius: YYUI.s(7)))
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .onHover { hover = $0 }
        .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.13), value: hover)
        .help(help)
    }
}

/// Plain NSTextView for the Mac feedback box: keeps the caret aligned with the 14pt placeholder
/// from the first focus (SwiftUI's TextEditor starts with a 0pt top inset until the first keystroke).
private struct MacFeedbackEditor: NSViewRepresentable {
    @Binding var text: String
    @Binding var writing: Bool

    func makeCoordinator() -> Coordinator { Coordinator(text: $text, writing: $writing) }

    func makeNSView(context: Context) -> NSScrollView {
        let scroll = NSTextView.scrollableTextView()
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.automaticallyAdjustsContentInsets = false
        scroll.contentInsets = NSEdgeInsets()
        guard let old = scroll.documentView as? NSTextView,
              let container = old.textContainer else { return scroll }
        let tv = FeedbackNSTextView(frame: old.frame, textContainer: container)
        container.widthTracksTextView = true
        container.lineFragmentPadding = YYUI.s(5)
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.isHorizontallyResizable = false
        tv.isVerticallyResizable = true
        tv.autoresizingMask = [.width]
        tv.allowsUndo = true
        tv.onFocusChange = { focused in
            DispatchQueue.main.async {
                if context.coordinator.writing.wrappedValue != focused {
                    context.coordinator.writing.wrappedValue = focused
                }
            }
        }
        tv.delegate = context.coordinator
        tv.isRichText = false
        tv.drawsBackground = false
        let font = NSFont.systemFont(ofSize: YYUI.s(14))
        tv.font = font
        tv.textColor = NSColor(YYColor.t1)
        tv.typingAttributes = [.font: font, .foregroundColor: NSColor(YYColor.t1)]
        tv.textContainerInset = NSSize(width: 0, height: 0)
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.insertionPointColor = NSColor(YYTint.standard.accent)
        tv.string = text
        scroll.documentView = tv
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        scroll.hasVerticalScroller = false
        guard let tv = scroll.documentView as? NSTextView else { return }
        tv.insertionPointColor = NSColor(YYTint.standard.accent)
        if tv.string != text { tv.string = text }
        if writing, scroll.window?.firstResponder !== tv, !context.coordinator.requestedFocus {
            context.coordinator.requestedFocus = true
            scroll.window?.makeFirstResponder(tv)
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var text: Binding<String>
        var writing: Binding<Bool>
        var requestedFocus = false
        init(text: Binding<String>, writing: Binding<Bool>) {
            self.text = text
            self.writing = writing
        }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            text.wrappedValue = tv.string
        }
    }
}

private final class FeedbackNSTextView: NSTextView {
    var onFocusChange: ((Bool) -> Void)?

    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok { onFocusChange?(true) }
        return ok
    }

    override func resignFirstResponder() -> Bool {
        let ok = super.resignFirstResponder()
        if ok { onFocusChange?(false) }
        return ok
    }
}

/// The bar that floats while the person goes to what they want to shoot. A non-activating
/// panel: clicks on it don't take the focus from the window being shot, and it stays above
/// every window and Space.
@MainActor
enum ShotBar {
    private static var panel: NSPanel?

    static func show(shoot: @escaping () -> Void, cancel: @escaping () -> Void) {
        hide()
        let panel = NSPanel(contentRect: .zero, styleMask: [.nonactivatingPanel, .borderless], backing: .buffered, defer: false)
        panel.level = .floating
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.isMovableByWindowBackground = true
        panel.backgroundColor = .clear
        panel.isOpaque = false
        panel.hasShadow = true
        panel.hidesOnDeactivate = false
        let host = NSHostingView(rootView: ShotBarView(shoot: shoot, cancel: cancel))
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
    let cancel: () -> Void
    private var tint: YYTint { YYTint.standard }

    var body: some View {
        HStack(spacing: YYUI.s(10)) {
            Image(systemName: "exclamationmark.bubble").foregroundStyle(tint.accent)
            Text("去到要截的界面，点")
                .foregroundStyle(YYColor.t2)
            Button(action: shoot) {
                Label("截图", systemImage: "camera.viewfinder")
            }
            .buttonStyle(YYButtonStyle(kind: .main))
            .keyboardShortcut(.defaultAction)
            ShotBarCloseButton(action: cancel)
        }
        .font(YYUI.font(13))
        .padding(.leading, YYUI.s(16))
        .padding(.trailing, YYUI.s(10))
        .padding(.vertical, YYUI.s(8))
        .background(.regularMaterial, in: .capsule)
        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
        .padding(YYUI.s(10))
        .fixedSize()
    }
}

private struct ShotBarCloseButton: View {
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: "xmark")
                .font(YYUI.font(10.5, .bold))
                .foregroundStyle(hover ? .white : YYColor.danger)
                .frame(width: YYUI.s(28), height: YYUI.s(28))
                .background(hover ? YYColor.danger : YYColor.danger.opacity(0.12), in: .circle)
                .overlay(Circle().strokeBorder(YYColor.danger.opacity(hover ? 0 : 0.3), lineWidth: 0.75))
                .contentShape(.circle)
        }
        .buttonStyle(YYPressStyle(scale: 0.94))
        .onHover { hover = $0 }
        .animation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.13), value: hover)
        .help(String(localized: "关闭截图条"))
        .accessibilityLabel(String(localized: "关闭截图条"))
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
