import Darwin
import Foundation
import ImageIO
import SwiftUI
import UniformTypeIdentifiers
#if os(iOS)
import PhotosUI
import UIKit
#else
import AppKit
#endif

/// 反馈: single-channel private submission to AirFeedback (never public, no email collected).
/// Images are downscaled and re-encoded as stripped JPEGs; drafts persist locally until sent
/// or explicitly cleared.
@Observable
final class Feedback {
    static let shared = Feedback()

    nonisolated enum Problem: LocalizedError, Sendable {
        case network
        case rejected(String)
        case notImage
        case tooBig(String)
        case unreadable(String)

        var errorDescription: String? {
            switch self {
            case .network:
                String(localized: "当前没有网络或服务暂时不可用，内容和图片已保存在草稿里，稍后再点发送即可。")
            case .rejected(let reason):
                reason
            case .notImage:
                String(localized: "只支持图片或截图，不支持上传普通文件。")
            case .tooBig(let name):
                name.isEmpty
                    ? String(localized: "图片太大，压缩后仍超过 8 MB。")
                    : String(localized: "「\(name)」太大，压缩后仍超过 8 MB。")
            case .unreadable(let name):
                name.isEmpty
                    ? String(localized: "这张图片读不出来。")
                    : String(localized: "「\(name)」读不出来。")
            }
        }
    }

    /// One attached image: downscaled to at most 2048 px on the long side and re-encoded as JPEG
    /// with all source metadata (EXIF, GPS, camera) stripped.
    nonisolated struct Attachment: Identifiable, @unchecked Sendable {
        let id = UUID()
        let name: String
        let data: Data
        let preview: CGImage
    }

    static let endpoint = URL(string: "https://apps.air.build/v1/ingest/feedback")!
    static let crashEndpoint = "https://apps.air.build/v1/ingest/crashes"
    static let appKey = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg"

    static var teamID: String? {
        #if os(iOS)
        guard let raw = Bundle.main.object(forInfoDictionaryKey: "YuyueAppleTeamID") as? String else { return nil }
        let value = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.count == 10 ? value : nil
        #else
        return nil
        #endif
    }

    static let textMax = 4000
    static let filesMax = 4
    nonisolated static let fileBytesMax = 8_000_000
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

    private init() {
        // Clean up keys removed in earlier versions.
        UserDefaults.standard.removeObject(forKey: "feedback.sentAt")
        UserDefaults.standard.removeObject(forKey: "feedback.email")
        UserDefaults.standard.removeObject(forKey: "feedback.queue")
        #if DEBUG
        // -debugFeedback 1|full: the window catalog opens the form (full: with words and screenshot).
        requested = UserDefaults.standard.string(forKey: "debugFeedback") != nil
        #endif
    }

    // MARK: Sending

    func send(text: String, withInfo: Bool, files: [Attachment]) async throws {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty || !files.isEmpty else { return }

        let body: [String: Any] = [
            "message": trimmed,
            "appVersion": withInfo ? Self.shortVersion : "",
            "appBuild": withInfo ? Self.buildVersion : "",
            "deviceModel": withInfo ? Self.hardwareModel : "",
            "osVersion": withInfo ? Self.osVersion : "",
            "locale": withInfo ? Locale.current.identifier : "",
            "images": files.prefix(Self.filesMax).map {
                ["mediaType": "image/jpeg", "data": $0.data.base64EncodedString()]
            },
        ]

        let bytes = try JSONSerialization.data(withJSONObject: body)
        let proof = await AirDeviceProof.headers(
            for: bytes, endpoint: Self.endpoint, appKey: Self.appKey, teamID: Self.teamID
        )

        var request = URLRequest(url: Self.endpoint)
        request.httpMethod = "POST"
        request.setValue("Bearer \(Self.appKey)", forHTTPHeaderField: "Authorization")
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        for (name, value) in proof {
            request.setValue(value, forHTTPHeaderField: name)
        }
        request.httpBody = bytes
        request.timeoutInterval = 60

        let data: Data
        let response: URLResponse
        do {
            (data, response) = try await URLSession.shared.data(for: request)
        } catch {
            throw Problem.network
        }

        guard let http = response as? HTTPURLResponse else {
            throw Problem.network
        }
        if !proof.isEmpty {
            await AirDeviceProof.handle(status: http.value(forHTTPHeaderField: "X-Attest-Status"))
        }
        guard (200..<300).contains(http.statusCode) else {
            if let reason = (try? JSONSerialization.jsonObject(with: data) as? [String: Any])?["reason"] as? String {
                let cleaned = reason.trimmingCharacters(in: .whitespacesAndNewlines)
                if !cleaned.isEmpty { throw Problem.rejected(cleaned) }
            }
            throw Problem.network
        }
    }

    // MARK: Draft

    /// What the form held when it closed, so nothing written is lost. Cleared only on successful
    /// send or explicit 清空. Images are stored beside it, in order.
    private struct Draft: Codable {
        var text: String
        var withInfo: Bool
        var files: [String]
    }

    private let draftKey = "feedback.draft"

    private var draftDir: URL {
        URL.applicationSupportDirectory.appending(path: "Yuyue/Feedback/Draft", directoryHint: .isDirectory)
    }

    func saveDraft(text: String, withInfo: Bool) {
        var draft = loadDraftRecord() ?? Draft(text: "", withInfo: true, files: [])
        draft.text = text
        draft.withInfo = withInfo
        writeDraft(draft)
    }

    func saveDraftFiles(_ files: [Attachment]) {
        let fm = FileManager.default
        try? fm.removeItem(at: draftDir)
        var names: [String] = []
        let kept = Array(files.prefix(Self.filesMax))
        if !kept.isEmpty {
            try? fm.createDirectory(at: draftDir, withIntermediateDirectories: true)
            for (i, file) in kept.enumerated() {
                if (try? file.data.write(to: draftDir.appending(path: "\(i)"))) != nil {
                    names.append(file.name)
                }
            }
        }
        var draft = loadDraftRecord() ?? Draft(text: "", withInfo: true, files: [])
        draft.files = names
        writeDraft(draft)
    }

    func loadDraft() -> (text: String, withInfo: Bool, files: [Attachment])? {
        guard let draft = loadDraftRecord() else { return nil }
        let files: [Attachment] = draft.files.prefix(Self.filesMax).enumerated().compactMap { i, name in
            guard let data = try? Data(contentsOf: draftDir.appending(path: "\(i)")) else { return nil }
            return Self.restored(name: name, data: data)
        }
        return (draft.text, draft.withInfo, files)
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
        if draft.text.isEmpty && draft.files.isEmpty && draft.withInfo {
            UserDefaults.standard.removeObject(forKey: draftKey)
        } else if let data = try? JSONEncoder().encode(draft) {
            UserDefaults.standard.set(data, forKey: draftKey)
        }
    }

    /// An image read back from the draft: already shrunk to JPEG, so only the preview is rebuilt.
    nonisolated private static func restored(name: String, data: Data) -> Attachment? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0,
              let uti = CGImageSourceGetType(source) as String?,
              UTType(uti)?.conforms(to: .image) == true,
              let preview = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: 240,
              ] as CFDictionary) else {
            return nil
        }
        return Attachment(name: name, data: data, preview: preview)
    }

    // MARK: Images

    /// Images only: rejects non-images, downscales to at most 2048 px on the long side, strips
    /// metadata, and re-encodes as JPEG under 8 MB.
    nonisolated static func attachment(name: String, data: Data) throws -> Attachment {
        guard !data.isEmpty else { throw Problem.unreadable(name) }
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              CGImageSourceGetCount(source) > 0,
              let uti = CGImageSourceGetType(source) as String?,
              UTType(uti)?.conforms(to: .image) == true else {
            throw Problem.notImage
        }
        guard let shot = shrink(source) else { throw Problem.tooBig(name) }
        let base = (name as NSString).deletingPathExtension
        let finalName = (base.isEmpty ? String(localized: "截图") : base) + ".jpg"
        return Attachment(name: finalName, data: shot.jpeg, preview: shot.image)
    }

    /// Downscales an image and encodes a metadata-free JPEG within `fileBytesMax`.
    nonisolated private static func shrink(_ source: CGImageSource) -> (jpeg: Data, image: CGImage)? {
        for (side, quality) in [(2048, 0.8), (2048, 0.65), (1600, 0.65), (1200, 0.55)] {
            let options: [CFString: Any] = [
                kCGImageSourceCreateThumbnailFromImageAlways: true,
                kCGImageSourceCreateThumbnailWithTransform: true,
                kCGImageSourceThumbnailMaxPixelSize: side,
            ]
            guard let image = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
            let out = NSMutableData()
            guard let dest = CGImageDestinationCreateWithData(out, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
            // Only the pixels: no properties or EXIF metadata from the source are copied over.
            CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: quality] as CFDictionary)
            guard CGImageDestinationFinalize(dest) else { return nil }
            if out.length <= fileBytesMax { return (out as Data, image) }
        }
        return nil
    }

    // MARK: Device Info

    static var shortVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? ""
    }

    static var buildVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? ""
    }

    static var appVersion: String {
        let short = shortVersion.isEmpty ? "?" : shortVersion
        let build = buildVersion.isEmpty ? "?" : buildVersion
        return "\(short) (\(build))"
    }

    static var hardwareModel: String {
        #if os(macOS)
        var size = 0
        if sysctlbyname("hw.model", nil, &size, nil, 0) == 0, size > 1 {
            var buffer = [UInt8](repeating: 0, count: size)
            if sysctlbyname("hw.model", &buffer, &size, nil, 0) == 0 {
                let model = String(decoding: buffer.prefix { $0 != 0 }, as: UTF8.self)
                if !model.isEmpty { return model }
            }
        }
        #endif
        var system = utsname()
        uname(&system)
        return withUnsafeBytes(of: &system.machine) { buffer in
            String(decoding: buffer.prefix { $0 != 0 }, as: UTF8.self)
        }
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

    static var device: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "iPad" : "iPhone"
        #endif
    }

    /// Expanded detail grid items for 附带设备信息.
    static var info: [(label: String, value: String)] {
        [
            (String(localized: "版本"), appVersion),
            (String(localized: "系统"), osVersion),
            (String(localized: "设备"), device),
            (String(localized: "语言"), Locale.current.identifier),
        ]
    }

    /// Compact one-line summary shown in the collapsed 附带设备信息 row.
    static var shortInfo: String {
        let short = shortVersion.isEmpty ? "?" : shortVersion
        return String(localized: "语阅 \(short) · \(osVersion) · \(device)")
    }

    static func clip(_ text: String, _ max: Int) -> String {
        guard text.unicodeScalars.count > max else { return text }
        var scalars = String.UnicodeScalarView()
        scalars.append(contentsOf: text.unicodeScalars.prefix(max))
        return String(scalars)
    }
}

/// Single-channel private feedback form: message + up to 4 images, optional device info, mailto
/// hint for replies, and sticky private-send footer.
struct FeedbackForm: View {
    let close: () -> Void
    @State private var text = ""
    @State private var sending = false
    @State private var done = false
    @State private var notice: String?
    @State private var withInfo = true
    @State private var infoExpanded = false
    @State private var files: [Feedback.Attachment] = []
    @State private var restored = false
    #if os(macOS)
    @State private var choosingFiles = false
    @State private var pasteMonitor: Any?
    @State private var writing = false
    @State private var dropping = false
    #else
    @State private var picks: [PhotosPickerItem] = []
    @State private var choosingPhotos = false
    @FocusState private var editorFocused: Bool
    #endif
    private let feedback = Feedback.shared
    private var tint: YYTint { YYTint.standard }

    private var textBlank: Bool { text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    private var hasDraft: Bool { !textBlank || !files.isEmpty }
    private var canSend: Bool { (!textBlank || !files.isEmpty) && !sending }

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
        .interactiveDismissDisabled(sending)
        .onAppear(perform: restore)
        .onChange(of: text) { save() }
        .onChange(of: withInfo) { save() }
        .onChange(of: files.map(\.id)) {
            if restored, !done { feedback.saveDraftFiles(files) }
        }
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
        text = "打开《红楼梦》第三回，翻到一半就卡住了，点哪里都没反应。\n\n重启后还是一样，别的书正常，换成 PDF 版也一样。\n\n第三回开头那几页都能翻，到「林黛玉抛父进京都」后面一点开始卡，卡住以后窗口拖不动，只能强制退出。"
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
    }
    #endif

    // MARK: Draft

    private func restore() {
        guard !restored else { return }
        if let draft = feedback.loadDraft() {
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
        focusEditor()
    }

    private func focusEditor() {
        // After the sheet has risen, or focus goes nowhere.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.35) {
            #if os(macOS)
            writing = true
            #else
            editorFocused = true
            #endif
        }
    }

    private func save() {
        guard restored, !done else { return }
        feedback.saveDraft(text: text, withInfo: withInfo)
    }

    #if os(macOS)
    /// ⌘V with an image or image file on the clipboard attaches it; text pastes as usual.
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
        #if os(iOS)
        .scrollDismissesKeyboard(.interactively)
        #endif
    }

    private var isEditorFocused: Bool {
        #if os(macOS)
        writing
        #else
        editorFocused
        #endif
    }

    private var editor: some View {
        let len = text.unicodeScalars.count
        return VStack(alignment: .leading, spacing: 0) {
            #if os(macOS)
            ZStack(alignment: .topLeading) {
                // The box grows with the words: an invisible copy sets the height, the editor fills it.
                Text(verbatim: text + "\n ")
                    .font(YYUI.font(14))
                    .padding(.horizontal, YYUI.s(5))
                    .padding(.top, YYUI.s(4))
                    .padding(.bottom, YYUI.s(8))
                    .frame(maxWidth: .infinity, minHeight: YYUI.s(132), alignment: .topLeading)
                    .opacity(0)
                    .accessibilityHidden(true)
                MacFeedbackEditor(text: $text, writing: $writing)
                    .padding(.top, YYUI.s(4))
                if text.isEmpty {
                    Text("遇到什么问题、想要什么功能，或哪本书打开不对，都可以直接写")
                        .font(YYUI.font(14))
                        .foregroundStyle(YYColor.t3)
                        .padding(.top, YYUI.s(4))
                        .padding(.leading, YYUI.s(5))
                        .allowsHitTesting(false)
                }
            }
            .padding(.horizontal, YYUI.s(9))
            .padding(.top, YYUI.s(8))
            .padding(.bottom, YYUI.s(4))
            #else
            TextField(
                String(localized: "反馈内容"),
                text: $text,
                prompt: Text("遇到什么问题、想要什么功能，或哪本书打开不对，都可以直接写")
                    .foregroundStyle(YYColor.t3),
                axis: .vertical
            )
            .focused($editorFocused)
            .font(YYUI.font(14))
            .foregroundStyle(YYColor.t1)
            .lineLimit(6...)
            .frame(maxWidth: .infinity, minHeight: YYUI.s(128), alignment: .topLeading)
            .padding(.horizontal, YYUI.s(14))
            .padding(.top, YYUI.s(12))
            .padding(.bottom, YYUI.s(8))
            .contentShape(.rect)
            .onTapGesture { editorFocused = true }
            #endif

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
                    addImage
                    #if os(macOS)
                    shotButton
                    #endif
                }
                Spacer(minLength: 0)
                HStack(spacing: YYUI.s(8)) {
                    if files.isEmpty {
                        Text("最多 4 张图")
                            .font(YYUI.font(11))
                            .foregroundStyle(YYColor.t3)
                    } else {
                        Text("图片 \(files.count)/4")
                            .font(YYUI.font(11).monospacedDigit())
                            .foregroundStyle(YYColor.t3)
                    }
                    if len >= 3600 {
                        Text(verbatim: "\(len)/\(Feedback.textMax)")
                            .font(YYUI.font(10.5).monospacedDigit())
                            .foregroundStyle(len >= 3900 ? YYAlertTone.warn.ink : YYColor.t3)
                    }
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
                    Image(systemName: "photo.on.rectangle")
                        .font(YYUI.font(18, .medium))
                    Text("松手添加图片")
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
        .dropDestination(for: URL.self) { urls, _ in
            take(urls)
            return true
        } isTargeted: { targeted in
            dropping = targeted
        }
        .fileImporter(isPresented: $choosingFiles, allowedContentTypes: [.image], allowsMultipleSelection: true) { result in
            if case .success(let urls) = result { take(urls) }
        }
        #else
        .photosPicker(
            isPresented: $choosingPhotos,
            selection: $picks,
            maxSelectionCount: max(1, Feedback.filesMax - files.count),
            matching: .images
        )
        .onChange(of: picks) { _, items in
            guard !items.isEmpty else { return }
            picks = []
            Task {
                for item in items {
                    guard files.count < Feedback.filesMax else { break }
                    guard let data = try? await item.loadTransferable(type: Data.self) else { continue }
                    await attach(name: "", data: data)
                }
            }
        }
        #endif
    }

    private var addImage: some View {
        #if os(iOS)
        Menu {
            Button("从相册选图", systemImage: "photo.on.rectangle") { choosingPhotos = true }
            Button("粘贴剪贴板图片", systemImage: "doc.on.clipboard") { pasteImages() }
        } label: {
            HStack(spacing: YYUI.s(4)) {
                Image(systemName: "photo.on.rectangle").font(YYUI.font(11.5, .medium))
                Text("图片")
            }
            .font(YYUI.font(12))
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, YYUI.s(8))
            .frame(height: YYUI.s(26))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(String(localized: "添加图片"))
        #else
        FeedbackToolButton(title: String(localized: "图片"), symbol: "photo.on.rectangle",
                           help: String(localized: "最多 4 张图片。也可以拖进来，或者 ⌘V 粘贴")) {
            choosingFiles = true
        }
        .accessibilityLabel(String(localized: "添加图片"))
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
            for image in images.prefix(max(0, Feedback.filesMax - files.count)) {
                if let data = image.pngData() { await attach(name: "", data: data) }
            }
        }
    }
    #endif

    private var fileStrip: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: YYUI.s(8)) {
                ForEach(files) { file in
                    Image(decorative: file.preview, scale: 1)
                        .resizable()
                        .scaledToFill()
                        .frame(width: YYUI.s(58), height: YYUI.s(58))
                        .clipShape(.rect(cornerRadius: YYUI.s(10)))
                        .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(YYColor.line, lineWidth: 0.5))
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
            .padding(.top, YYUI.s(6))
            .padding(.trailing, YYUI.s(6))
        }
        .scrollBounceBehavior(.basedOnSize, axes: .horizontal)
        .transition(.opacity)
    }

    #if os(macOS)
    private func take(_ urls: [URL]) {
        guard files.count < Feedback.filesMax else {
            notice = String(localized: "最多添加 4 张图片。")
            return
        }
        let remaining = max(0, Feedback.filesMax - files.count)
        let picked: [(String, Data?, Bool)] = urls.prefix(remaining).map { url in
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            if let type = try? url.resourceValues(forKeys: [.contentTypeKey]).contentType,
               type != .data, type != .item, !type.conforms(to: .image) {
                return (url.lastPathComponent, nil, true)
            }
            let data = try? FileHandle(forReadingFrom: url).read(upToCount: Feedback.fileBytesMax * 8)
            return (url.lastPathComponent, data, false)
        }
        Task {
            for (name, data, rejectedNonImage) in picked {
                if rejectedNonImage {
                    notice = Feedback.Problem.notImage.errorDescription
                    continue
                }
                guard let data else {
                    notice = Feedback.Problem.unreadable(name).errorDescription
                    continue
                }
                await attach(name: name, data: data)
            }
        }
    }
    #endif

    private func attach(name: String, data: Data) async {
        guard files.count < Feedback.filesMax else {
            notice = String(localized: "最多添加 4 张图片。")
            return
        }
        do {
            let file = try await Self.makeAttachment(name: name, data: data)
            guard files.count < Feedback.filesMax else { return }
            notice = nil
            withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15)) { files.append(file) }
        } catch {
            notice = (error as? LocalizedError)?.errorDescription
        }
    }

    @concurrent nonisolated private static func makeAttachment(name: String, data: Data) async throws -> Feedback.Attachment {
        try Feedback.attachment(name: name, data: data)
    }

    /// Collapsible device summary card, default ON and collapsed.
    private var infoCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: YYUI.s(10)) {
                Button {
                    guard withInfo else { return }
                    #if os(iOS)
                    editorFocused = false
                    #endif
                    withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.18)) {
                        infoExpanded.toggle()
                    }
                } label: {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        HStack(spacing: YYUI.s(5)) {
                            Text("附带设备信息")
                                .font(YYUI.font(12.5, .medium))
                                .foregroundStyle(YYColor.t1)
                            if withInfo {
                                Image(systemName: "chevron.down")
                                    .font(YYUI.font(9, .semibold))
                                    .foregroundStyle(YYColor.t3)
                                    .rotationEffect(.degrees(infoExpanded ? 180 : 0))
                            }
                        }
                        Text(withInfo ? Feedback.shortInfo : String(localized: "本次不附带"))
                            .font(YYUI.font(11))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                            .truncationMode(.tail)
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
            .padding(.vertical, YYUI.s(10))

            if withInfo && infoExpanded {
                Rectangle()
                    .fill(YYColor.line)
                    .frame(height: 0.5)
                    .padding(.horizontal, YYUI.s(14))
                LazyVGrid(
                    columns: [
                        GridItem(.flexible(), spacing: YYUI.s(12), alignment: .leading),
                        GridItem(.flexible(), spacing: YYUI.s(12), alignment: .leading),
                    ],
                    alignment: .leading,
                    spacing: YYUI.s(10)
                ) {
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

    private var mailHint: some View {
        HStack(spacing: YYUI.s(5)) {
            Image(systemName: "envelope")
                .font(YYUI.font(11))
                .foregroundStyle(YYColor.t3)
            Text("需要回复？")
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

    private func resetDraft() {
        withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.15)) {
            text = ""
            files = []
            notice = nil
        }
        feedback.clearDraft()
    }

    private var privacyNotice: some View {
        HStack(spacing: YYUI.s(5)) {
            Image(systemName: "lock.fill")
                .font(YYUI.font(10.5, .medium))
                .foregroundStyle(YYColor.ok)
            Text("仅发给开发者，不会公开")
                .font(YYUI.font(11.5))
                .foregroundStyle(YYColor.t2)
            Spacer(minLength: 0)
        }
    }

    private var footer: some View {
        VStack(spacing: 0) {
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            #if os(macOS)
            VStack(alignment: .leading, spacing: YYUI.s(10)) {
                privacyNotice
                HStack(spacing: YYUI.s(8)) {
                    if hasDraft && !sending {
                        HStack(spacing: YYUI.s(6)) {
                            Text("草稿已自动保存")
                            Text(verbatim: "·")
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
                            Text(sending ? String(localized: "正在发送…") : String(localized: "发送给开发者"))
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
                privacyNotice
                Button(action: submit) {
                    HStack(spacing: YYUI.s(6)) {
                        if sending {
                            ProgressView()
                                .controlSize(.small)
                                .tint(tint.onBlack)
                        }
                        Text(sending ? String(localized: "正在发送…") : String(localized: "发送给开发者"))
                    }
                }
                .buttonStyle(YYButtonStyle(kind: .main, wide: true, height: 44))
                .disabled(!canSend)
                if hasDraft && !sending {
                    HStack(spacing: YYUI.s(6)) {
                        Text("草稿已自动保存")
                        Text(verbatim: "·")
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
                try await feedback.send(text: text, withInfo: withInfo, files: files)
                AirTraffic.trackFeedbackSubmitted(
                    imageCount: min(files.count, Feedback.filesMax),
                    includeDeviceInfo: withInfo
                )
                feedback.clearDraft()
                withAnimation(.easeOut(duration: YYMotion.reduce ? 0.01 : 0.25)) {
                    done = true
                }
            } catch {
                notice = (error as? LocalizedError)?.errorDescription
                    ?? Feedback.Problem.network.errorDescription
            }
        }
    }

    // MARK: Sent

    private var thanks: some View {
        VStack(spacing: YYUI.s(12)) {
            Spacer(minLength: YYUI.s(24))
            Image(systemName: "checkmark")
                .font(YYUI.font(24, .semibold))
                .foregroundStyle(YYColor.ok)
                .frame(width: YYUI.s(60), height: YYUI.s(60))
                .background(YYColor.ok.opacity(0.14), in: .circle)
                .overlay(Circle().strokeBorder(YYColor.ok.opacity(0.32), lineWidth: 0.5))
                .symbolEffect(.bounce, value: done)
            Text("已发给开发者，谢谢")
                .font(YYUI.font(17, .semibold))
                .foregroundStyle(YYColor.t1)
            Text("这条反馈仅开发者可见，不会公开。")
                .font(YYUI.font(12.5))
                .foregroundStyle(YYColor.t2)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 260)
            HStack(spacing: 0) {
                Text("如需回复，可发邮件至 ")
                    .foregroundStyle(YYColor.t3)
                Link(destination: Feedback.mailURL) {
                    Text(verbatim: Feedback.supportEmail)
                        .foregroundStyle(tint.accent)
                }
                .buttonStyle(.plain)
            }
            .font(YYUI.font(11.5))
            HStack(spacing: YYUI.s(10)) {
                Button("再写一条") {
                    text = ""
                    withInfo = true
                    infoExpanded = false
                    files = []
                    notice = nil
                    done = false
                    focusEditor()
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
        } else if !writing {
            context.coordinator.requestedFocus = false
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
