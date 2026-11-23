#if DEBUG && os(macOS)
import AppKit
import ScreenCaptureKit
import WebKit

/// `-debugShots <dir> -debugShotName <名字> [-debugShotDelay 秒]`：等界面稳下来（启动参数、
/// 延迟出现的卡片都到位），把这个 app 的每个窗口——sheet、popover 也算——存进
/// `<dir>/<名字>/1.png、2.png`（最前面的在前），然后退出。截自己的窗口走 ScreenCaptureKit
/// 不需要屏幕录制权限；万一不行就退回 NSView 自绘（会少标题栏那一条）。
/// window-catalog.sh 一次开一个弹窗，就能攒出一套全窗口图册。搭配 `-debugLibrary`
/// 指一个临时书库，不碰真的。
enum DebugShots {
    static func run() {
        let d = UserDefaults.standard
        // 换语言的整套动效：打开设置 › 通用，两秒后换成另一种语言（会真的重开）。
        // The 语阅 opened again after it only takes over, it doesn't swap again.
        if d.bool(forKey: "debugLanguageSwap"), AppLanguage.pendingSweep == nil {
            d.set(SettingsPage.general.rawValue, forKey: "settings.page")
            // `-debugSettings general` alongside opens 设置.
            DispatchQueue.main.asyncAfter(deadline: .now() + 3) {
                let to: AppLanguage = AppLanguage.running == .en ? .zh : .en
                AppLanguage.choose(to)
                NotificationCenter.default.post(name: .relaunchForLanguage, object: to)
            }
            return
        }
        guard let dir = d.string(forKey: "debugShots"), let name = d.string(forKey: "debugShotName") else { return }
        // `wait` 是下限：TXT 解码、导入、合成这类就绪在窗口集合里看不出来，只能等；
        // 之后窗口集合再稳一秒才拍（等晚挂的 sheet/popover），最多让 8 秒。
        let wait = max(d.double(forKey: "debugShotDelay"), 2.5)
        // 截图参数会顺带写下这两处偏好（读者面板、设置页），走之前还原成启动前的样子。
        let saved = ["reader.panel": d.object(forKey: "reader.panel"), "settings.page": d.object(forKey: "settings.page")]
        let folder = URL(filePath: dir, directoryHint: .isDirectory).appendingPathComponent(name)
        // 兜底：万一卡在 ScreenCaptureKit 或退出流程，到点强退，别让 window-catalog.sh 空等。
        DispatchQueue.main.asyncAfter(deadline: .now() + wait + 45) { exit(1) }
        Task { @MainActor in
            try? await Task.sleep(for: .seconds(wait))
            await waitUntilWindowsSettle(deadline: 8)
            await shoot(into: folder)
            for (key, value) in saved {
                if let value { d.set(value, forKey: key) } else { d.removeObject(forKey: key) }
            }
            CFPreferencesAppSynchronize(kCFPreferencesCurrentApplication)
            // 图册副本是即抛的：terminate 在某些弹窗下会挂（脚本只好 120 秒后强杀），PNG 已
            // 落盘，直接走。
            exit(0)
        }
    }

    /// 窗口集合（本进程、层 0）连续一秒没变化才算就绪，避免固定延时拍早了、弹窗还没挂上来；
    /// 到 deadline（`wait`）为止。
    private static func waitUntilWindowsSettle(deadline: Double) async {
        func snapshot() -> Set<Int> {
            let info = (CGWindowListCopyWindowInfo([.excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
            let pid = Int(ProcessInfo.processInfo.processIdentifier)
            return Set(info.filter { ($0["kCGWindowOwnerPID"] as? Int) == pid && ($0["kCGWindowLayer"] as? Int) == 0 }
                          .compactMap { $0["kCGWindowNumber"] as? Int })
        }
        let start = Date()
        var last = snapshot()
        var stableSince = start
        while Date().timeIntervalSince(start) < deadline {
            try? await Task.sleep(for: .milliseconds(200))
            let now = snapshot()
            if now == last {
                if Date().timeIntervalSince(stableSince) >= 1 { return }
            } else {
                last = now
                stableSince = Date()
            }
        }
    }

    nonisolated private struct CaptureTimeout: Error {}
    nonisolated private struct UncheckedBox<T>: @unchecked Sendable { let value: T }

    private static func shareableContentWithTimeout() async throws -> SCShareableContent {
        try await withThrowingTaskGroup(of: UncheckedBox<SCShareableContent>.self) { group in
            group.addTask {
                UncheckedBox(value: try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true))
            }
            group.addTask {
                try await Task.sleep(for: .seconds(3))
                throw CaptureTimeout()
            }
            let first = try await group.next()!
            group.cancelAll()
            return first.value
        }
    }

    private static func shoot(into folder: URL) async {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var images: [(id: Int, cg: CGImage)] = []
        do {
            // z 序 CGWindowList 还给得出，拿它来把最前的排最前。
            let pid = Int(ProcessInfo.processInfo.processIdentifier)
            let info = (CGWindowListCopyWindowInfo([.excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
            let order = info.filter { ($0["kCGWindowOwnerPID"] as? Int) == pid as Int? && ($0["kCGWindowLayer"] as? Int) == 0 }
                            .compactMap { $0["kCGWindowNumber"] as? Int }
            let content = try await shareableContentWithTimeout()
            let mine = content.windows
                .filter { w in
                    guard let own = w.owningApplication?.processID, own == pid_t(pid) else { return false }
                    return w.frame.width > 150 && w.frame.height > 80
                }
                .sorted { (order.firstIndex(of: Int($0.windowID)) ?? .max) < (order.firstIndex(of: Int($1.windowID)) ?? .max) }
            // 被盖住、屏幕睡着的窗 macOS 不渲染,截出来是黑的:先把 app 激活,逐扇置前,
            // 等一拍再截。
            NSApp.activate(ignoringOtherApps: true)
            for w in mine {
                if let live = NSApp.windows.first(where: { $0.windowNumber == w.windowID }) {
                    live.makeKeyAndOrderFront(nil)
                    try? await Task.sleep(for: .milliseconds(400))
                }
                var config = SCStreamConfiguration()
                // 输出就照窗口的尺寸来(2x),不指定的话给的是整屏的缓冲,黑边得自己裁。
                config.width = Int(w.frame.width) * 2
                config.height = Int(w.frame.height) * 2
                config.showsCursor = false
                if let img = try? await SCScreenshotManager.captureImage(contentFilter: SCContentFilter(desktopIndependentWindow: w), configuration: config) {
                    images.append((id: Int(w.windowID), cg: img))
                }
            }
        } catch {
            NSLog("debugShots: ScreenCaptureKit 不行（\(error)），退回自绘")
        }
        if images.isEmpty { images = await selfDrawn() }
        for (i, shot) in images.enumerated() {
            let rep = NSBitmapImageRep(cgImage: shot.cg)
            if let png = rep.representation(using: .png, properties: [:]) {
                try? png.write(to: folder.appendingPathComponent("\(i + 1).png"))
            }
        }
        NSLog("debugShots: \(images.count) 个窗口 → \(folder.path)")
    }

    private static func findWebViews(in view: NSView) -> [WKWebView] {
        var found: [WKWebView] = []
        if let wv = view as? WKWebView, !wv.isHidden, wv.bounds.width > 1, wv.bounds.height > 1 {
            found.append(wv)
        }
        for sub in view.subviews where !sub.isHidden && sub.alphaValue > 0.01 {
            found.append(contentsOf: findWebViews(in: sub))
        }
        return found
    }

    /// 没有权限时的保底：自己画 contentView 的上一层（连标题栏一起）。SC 给不出才用。
    private static func selfDrawn() async -> [(id: Int, cg: CGImage)] {
        var shots: [(id: Int, cg: CGImage)] = []
        let pid = Int(ProcessInfo.processInfo.processIdentifier)
        let info = (CGWindowListCopyWindowInfo([.excludeDesktopElements], kCGNullWindowID) as? [[String: Any]]) ?? []
        let order = info.filter { ($0["kCGWindowOwnerPID"] as? Int) == pid && ($0["kCGWindowLayer"] as? Int) == 0 }
                        .compactMap { $0["kCGWindowNumber"] as? Int }
        let sorted = NSApp.windows.filter(\.isVisible).sorted {
            (order.firstIndex(of: $0.windowNumber) ?? .max) < (order.firstIndex(of: $1.windowNumber) ?? .max)
        }
        for w in sorted {
            guard let frameView = w.contentView?.superview else { continue }
            let rect = frameView.bounds
            guard rect.width > 150, rect.height > 80 else { continue }
            var standIns: [NSView] = []
            for wv in findWebViews(in: frameView) {
                if let snap = try? await wv.takeSnapshot(configuration: nil) {
                    let iv = NSImageView(frame: wv.bounds)
                    iv.imageScaling = .scaleAxesIndependently
                    iv.image = snap
                    wv.addSubview(iv)
                    standIns.append(iv)
                }
            }
            frameView.layoutSubtreeIfNeeded()
            if let rep = frameView.bitmapImageRepForCachingDisplay(in: rect) {
                frameView.cacheDisplay(in: rect, to: rep)
                if let cg = rep.cgImage {
                    shots.append((id: w.windowNumber, cg: cg))
                }
            }
            for iv in standIns { iv.removeFromSuperview() }
        }
        return shots
    }
}
#elseif DEBUG && os(iOS)
import UIKit
import WebKit

enum DebugShots {
    private static var started = false

    @MainActor
    static func run() {
        guard !started else { return }
        let d = UserDefaults.standard
        guard let dirSpec = d.string(forKey: "debugShots"),
              let name = d.string(forKey: "debugShotName") else { return }
        started = true
        let wait = max(d.double(forKey: "debugShotDelay"), 2.5)
        let baseDir = dirSpec.hasPrefix("~/")
            ? URL(filePath: NSHomeDirectory()).appending(path: String(dirSpec.dropFirst(2)), directoryHint: .isDirectory)
            : URL(filePath: dirSpec, directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: baseDir, withIntermediateDirectories: true)
        let outFile = baseDir.appending(path: "\(name).png")
        DispatchQueue.main.asyncAfter(deadline: .now() + wait + 30) { exit(1) }

        Task { @MainActor in
            try? await Task.sleep(for: .seconds(wait))
            await shoot(to: outFile)
            CFPreferencesAppSynchronize(kCFPreferencesCurrentApplication)
            exit(0)
        }
    }

    @MainActor
    private static func findWebViews(in view: UIView) -> [WKWebView] {
        var found: [WKWebView] = []
        if let wv = view as? WKWebView, !wv.isHidden, wv.alpha > 0.01, wv.bounds.width > 1, wv.bounds.height > 1 {
            found.append(wv)
        }
        for sub in view.subviews where !sub.isHidden && sub.alpha > 0.01 {
            found.append(contentsOf: findWebViews(in: sub))
        }
        return found
    }

    @MainActor
    private static func shoot(to outFile: URL) async {
        let scenes = UIApplication.shared.connectedScenes.compactMap { $0 as? UIWindowScene }
        guard let scene = scenes.first(where: { $0.activationState == .foregroundActive }) ?? scenes.first,
              let window = scene.windows.first(where: \.isKeyWindow) ?? scene.windows.first(where: { !$0.isHidden }) else {
            NSLog("debugShots(iOS): no active window")
            return
        }
        var standIns: [UIView] = []
        for wv in findWebViews(in: window) {
            if let snap = try? await wv.takeSnapshot(configuration: nil) {
                let iv = UIImageView(frame: wv.bounds)
                iv.image = snap
                iv.contentMode = .scaleToFill
                wv.addSubview(iv)
                standIns.append(iv)
            }
        }
        window.layoutIfNeeded()
        let format = UIGraphicsImageRendererFormat.default()
        format.scale = window.screen.scale
        format.opaque = true
        let renderer = UIGraphicsImageRenderer(bounds: window.bounds, format: format)
        let img = renderer.image { _ in
            for w in scene.windows where !w.isHidden && w.alpha > 0.01 {
                w.drawHierarchy(in: w.bounds, afterScreenUpdates: true)
            }
        }
        for iv in standIns { iv.removeFromSuperview() }
        if let png = img.pngData() {
            try? png.write(to: outFile, options: .atomic)
            NSLog("debugShots(iOS): wrote \(outFile.path) (\(png.count) bytes)")
        }
    }
}
#endif
