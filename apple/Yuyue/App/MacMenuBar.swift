#if os(macOS)
import ServiceManagement
import SwiftUI
import YuyueCore

/// 菜单栏里的语阅: shown while a podcast issue is active, a book is being listened to, or 声音工坊 is working.
@MainActor
final class MacMenuBar: NSObject, NSMenuDelegate {
    private let library: Library
    private var item: NSStatusItem?
    /// SwiftUI's way to open a window, handed over by the shelf when it first shows.
    static var openWindow: OpenWindowAction?
    private var podcastHub: PodcastHub { library.podcastHub }

    init(library: Library) {
        self.library = library
        super.init()
        follow()
        followPlaying()
    }

    /// Shown while a podcast issue is active or a book is playing.
    private func follow() {
        let on = withObservationTracking {
            podcastHub.isPlaying || podcastHub.activeIssueID != nil || library.nowPlaying != nil
        } onChange: { [weak self] in
            Task { @MainActor in self?.follow() }
        }
        if on, item == nil {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
            item.button?.image = MenuBarIcon.still
            item.button?.toolTip = String(localized: "语阅 · 播客")
            let menu = NSMenu()
            menu.delegate = self
            item.menu = menu
            self.item = item
        } else if on, let item {
            item.button?.toolTip = String(localized: "语阅 · 播客")
        } else if !on, let item {
            NSStatusBar.system.removeStatusItem(item)
            self.item = nil
        }
        animate()
    }

    /// The three lines in the 门 move while a voice is playing, and stand still when it stops.
    private var frames: Timer?
    private var playing = false
    private var screenAsleep = false
    private var frameIndex = 0
    nonisolated(unsafe) private var sleepObservers: [NSObjectProtocol] = []

    private func installSleepObserversIfNeeded() {
        guard sleepObservers.isEmpty else { return }
        let nc = NSWorkspace.shared.notificationCenter
        sleepObservers = [
            nc.addObserver(forName: NSWorkspace.screensDidSleepNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.screenAsleep = true
                    self?.animate()
                }
            },
            nc.addObserver(forName: NSWorkspace.screensDidWakeNotification, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated {
                    self?.screenAsleep = false
                    self?.animate()
                }
            },
        ]
    }

    deinit {
        let nc = NSWorkspace.shared.notificationCenter
        sleepObservers.forEach { nc.removeObserver($0) }
    }

    private func followPlaying() {
        playing = withObservationTracking { podcastHub.isPlaying || library.nowPlaying?.isPlaying == true } onChange: { [weak self] in
            Task { @MainActor in self?.followPlaying() }
        }
        animate()
    }

    private func animate() {
        installSleepObserversIfNeeded()
        let moving = playing && item != nil && !screenAsleep && !NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        if moving, frames == nil {
            let preRendered = MenuBarIcon.loopFrames
            let timer = Timer(timeInterval: 1.0 / 10, repeats: true) { [weak self] _ in
                MainActor.assumeIsolated {
                    guard let self, !preRendered.isEmpty else { return }
                    self.frameIndex = (self.frameIndex + 1) % preRendered.count
                    self.item?.button?.image = preRendered[self.frameIndex]
                }
            }
            // .common: keeps moving while the menu is open.
            RunLoop.main.add(timer, forMode: .common)
            frames = timer
        } else if !moving, let timer = frames {
            timer.invalidate()
            frames = nil
            item?.button?.image = MenuBarIcon.still
        }
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        if let active = podcastHub.activeIssue {
            menu.addItem(.sectionHeader(title: podcastHub.isPlaying ? String(localized: "正在放") : String(localized: "暂停在")))
            menu.addItem(NSMenuItem(title: active.title, action: nil, keyEquivalent: ""))
            menu.addItem(action(podcastHub.isPlaying ? String(localized: "暂停") : String(localized: "接着放")) { [weak self] in
                self?.podcastHub.togglePlay()
            })
            menu.addItem(.separator())
        }
        let desk = podcastHub.deskIssues.filter { $0.id != podcastHub.activeIssueID }.prefix(5)
        if !desk.isEmpty {
            menu.addItem(.sectionHeader(title: String(localized: "书桌待听")))
            for issue in desk {
                menu.addItem(action(issue.title) { [weak self] in self?.podcastHub.selectAndPlay(issue) })
            }
            menu.addItem(.separator())
        }
        menu.addItem(action(String(localized: "打开书架")) {
            Self.openWindow?(id: "library")
            NSApp.activate()
        })
        let quit = NSMenuItem(title: String(localized: "退出语阅"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        menu.addItem(quit)
    }

    private func action(_ title: String, _ run: @escaping () -> Void) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: #selector(Run.go), keyEquivalent: "")
        let target = Run(run)
        item.target = target
        item.representedObject = target
        return item
    }

    private final class Run: NSObject {
        let run: () -> Void
        init(_ run: @escaping () -> Void) { self.run = run }
        @objc func go() { run() }
    }
}

/// 门里出声: 阅's 门 (ResourceHanRounded Medium) with three lines of voice inside, short, long,
/// middle. `menubar` is the whole still icon; `menubar-door` is the 门 alone, with the lines drawn
/// over it here, a little longer and shorter each frame, so they move like a voice speaking.
enum MenuBarIcon {
    private static let size = NSSize(width: 18, height: 18)

    static let still: NSImage? = {
        let image = NSImage(named: "menubar")
        image?.isTemplate = true
        image?.size = size
        return image
    }()

    private static let door = NSImage(named: "menubar-door")

    /// x in points, half the still length, how long one rise and fall takes, where it starts.
    /// From menubar.svg: 1000 units over 18 pt, lines centred 0.59 down, 64 units wide.
    private static let lines: [(x: CGFloat, half: CGFloat, period: Double, phase: Double)] = [
        (5.94, 1.44, 0.62, 0.0), (9.04, 3.6, 0.9, 1.9), (12.13, 2.34, 0.74, 3.7),
    ]
    private static let middle: CGFloat = 18 * 0.59
    private static let width: CGFloat = 18 * 0.064

    /// Pre-rendered looping frames so playback doesn't allocate & redraw an NSImage 10–12×/sec.
    static let loopFrames: [NSImage] = (0 ..< 30).map { i in
        frame(at: Double(i) * 0.1)
    }

    /// The icon `t` seconds into playing.
    static func frame(at t: TimeInterval) -> NSImage {
        let image = NSImage(size: size, flipped: true) { rect in
            door?.draw(in: rect)
            NSColor.black.setStroke()
            for l in lines {
                // Between about half and a little over the still length.
                let k = 0.8 + 0.32 * sin(2 * .pi * t / l.period + l.phase)
                let half = max(l.half * k, 0.5)
                let path = NSBezierPath()
                path.move(to: NSPoint(x: l.x, y: middle - half))
                path.line(to: NSPoint(x: l.x, y: middle + half))
                path.lineWidth = width
                path.lineCapStyle = .round
                path.stroke()
            }
            return true
        }
        image.isTemplate = true
        return image
    }
}

/// 开机时打开语阅: a login item, so shows are written at their time after a restart too. Started
/// that way it opens no window, only the menu bar (see `AppDelegate`).
enum LoginItem {
    private static var cachedStatus: SMAppService.Status?
    private static var refreshTask: Task<SMAppService.Status, Never>?

    /// Last cached login-item status without blocking the main thread on synchronous XPC.
    static var cached: SMAppService.Status { cachedStatus ?? .notRegistered }
    static var isOn: Bool { cached == .enabled }
    /// Turned on, but macOS wants it allowed in 系统设置 › 通用 › 登录项.
    static var needsApproval: Bool { cached == .requiresApproval }

    /// Fetches `SMAppService.mainApp.status` off the main thread and updates the cache.
    @discardableResult
    static func refresh() async -> SMAppService.Status {
        if let task = refreshTask { return await task.value }
        let task = Task { await fetchStatus() }
        refreshTask = task
        let status = await task.value
        cachedStatus = status
        refreshTask = nil
        return status
    }

    @concurrent nonisolated private static func fetchStatus() async -> SMAppService.Status {
        SMAppService.mainApp.status
    }

    static func set(_ on: Bool) async throws -> SMAppService.Status {
        let status = try await setOffMain(on)
        cachedStatus = status
        return status
    }

    @concurrent nonisolated private static func setOffMain(_ on: Bool) async throws -> SMAppService.Status {
        if on { try SMAppService.mainApp.register() } else { try await SMAppService.mainApp.unregister() }
        return SMAppService.mainApp.status
    }

    static func openSettings() { SMAppService.openSystemSettingsLoginItems() }

    /// This launch was macOS starting the login items.
    @MainActor static var launchedAtLogin: Bool {
        let event = NSAppleEventManager.shared().currentAppleEvent
        return event?.eventID == kAEOpenApplication && event?.paramDescriptor(forKeyword: keyAEPropData)?.enumCodeValue == keyAELaunchedAsLogInItem
    }
}
#endif
