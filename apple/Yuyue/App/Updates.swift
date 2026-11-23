#if os(macOS)
import SwiftUI
import Sparkle

/// 下载并安装 on the Mac: Sparkle reads `appcast.xml` from the release `UpdateCenter` found, checks the
/// EdDSA signature, swaps the app and opens it again. It doesn't look by itself: `UpdateCenter`
/// checks once a day and shows the notes; Sparkle starts when 下载并安装 is pressed.
///
/// `-YYUpdateBase http://localhost:8000/` points it at a folder of one's own (a draft release
/// served from `dist/`), to try an update before it's published.
@MainActor
final class Updates: NSObject, SPUUpdaterDelegate {
    static let shared = Updates()

    private(set) var controller: SPUStandardUpdaterController!

    private override init() {
        super.init()
        controller = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: self, userDriverDelegate: nil)
        controller.updater.automaticallyChecksForUpdates = false
    }

    func check() { controller.checkForUpdates(nil) }

    nonisolated func feedURLString(for updater: SPUUpdater) -> String? {
        // The folder `UpdateCenter` found on its channel, so Sparkle installs the version it showed.
        let d = UserDefaults.standard
        #if DEBUG
        if let own = d.string(forKey: "YYUpdateBase") {
            return (own.hasSuffix("/") ? own : own + "/") + "appcast.xml"
        }
        #endif
        let base = d.string(forKey: "update.feedBase") ?? UpdateCenter.latest
        return base + "appcast.xml"
    }
}

/// 语阅 → 检查更新…, under 关于语阅.
struct UpdateCommands: Commands {
    var body: some Commands {
        CommandGroup(after: .appInfo) {
            Button("检查更新…") { UpdateCenter.shared.checkFromMenu() }
        }
    }
}
#endif
