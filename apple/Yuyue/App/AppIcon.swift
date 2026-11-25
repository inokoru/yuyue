import SwiftUI
#if os(iOS)
import UIKit
#elseif os(macOS)
import AppKit
#endif

/// The six finalized Apple app icons, ordered to lead with the monochrome paper pair and then
/// mirror `YYTint.all` from left to right.
nonisolated enum AppIconChoice: String, CaseIterable, Identifiable, Sendable, Hashable {
    case `default` = "default"
    case night = "night"
    case camel = "camel"
    case sage = "sage"
    case mist = "mist"
    case blush = "blush"

    static let storageKey = "appearance.appIcon"

    var id: String { rawValue }

    /// Value passed to `UIApplication.setAlternateIconName(_:)`; `nil` selects the primary `AppIcon`.
    var alternateIconName: String? {
        switch self {
        case .default: nil
        case .night: "AppIconNight"
        case .camel: "AppIconCamelCloth"
        case .sage: "AppIconSageCloth"
        case .mist: "AppIconMistCloth"
        case .blush: "AppIconBlushCloth"
        }
    }

    /// Underlying `.icon` / `.appiconset` asset name.
    var assetName: String {
        alternateIconName ?? "AppIcon"
    }

    /// Rendered preview image set in `Assets.xcassets` (`ictool` light/dark outputs).
    var previewAssetName: String {
        "IconPreview_\(assetName)"
    }

    var title: String {
        switch self {
        case .default: String(localized: "宣纸")
        case .night: String(localized: "暗夜")
        case .camel: String(localized: "暖纸")
        case .sage: String(localized: "草木")
        case .mist: String(localized: "烟雨")
        case .blush: String(localized: "海棠")
        }
    }

    init(alternateIconName: String?) {
        guard let alternateIconName else {
            self = .default
            return
        }
        self = Self.allCases.first { $0.alternateIconName == alternateIconName } ?? .default
    }

    static var stored: AppIconChoice {
        UserDefaults.standard.string(forKey: storageKey).flatMap(Self.init(rawValue:)) ?? .default
    }

    #if os(iOS)
    @MainActor
    static var currentIOS: AppIconChoice {
        AppIconChoice(alternateIconName: UIApplication.shared.alternateIconName)
    }
    #endif

    #if os(macOS)
    @MainActor
    private static var lastAppliedMacState: (choice: AppIconChoice, isDark: Bool)?

    /// Applies the chosen Dock icon while running on macOS.
    ///
    /// Selecting `.default` clears `NSApp.applicationIconImage` (`nil`) so macOS 26+ renders
    /// the native dynamic Liquid Glass `AppIcon.icon` across light, dark, clear, and tinted modes.
    @MainActor
    static func applyMacDockIcon(for choice: AppIconChoice? = nil, force: Bool = false) {
        guard let app = NSApp else { return }
        let target = choice ?? stored
        let isDark = PaperAppearance.currentEffectiveDark()
        if !force, let last = lastAppliedMacState, last.choice == target, last.isDark == isDark {
            return
        }
        if target == .default {
            app.applicationIconImage = nil
            lastAppliedMacState = (target, isDark)
            return
        }
        guard let image = renderMacDockIcon(for: target, isDark: isDark) else { return }
        app.applicationIconImage = image
        lastAppliedMacState = (target, isDark)
    }

    /// Renders an alternate icon on Apple's 824/1024 Dock grid (512pt canvas, centered 412pt card,
    /// soft downward Dock shadow) so its optical size matches native macOS icons.
    @MainActor
    static func renderMacDockIcon(for choice: AppIconChoice, isDark: Bool) -> NSImage? {
        guard let source = NSImage(named: choice.previewAssetName) else { return nil }
        let appearance = NSAppearance(named: isDark ? .darkAqua : .aqua) ?? NSApp?.effectiveAppearance
        var resolved: CGImage?
        let resolve = {
            var proposed = NSRect(x: 0, y: 0, width: 512, height: 512)
            resolved = source.cgImage(forProposedRect: &proposed, context: nil, hints: nil)
        }
        if let appearance {
            appearance.performAsCurrentDrawingAppearance(resolve)
        } else {
            resolve()
        }
        guard let cgSource = resolved else { return nil }

        // Draw at 2x (1024x1024 px -> 512x512 pt) in unscaled pixel space so CGRect(100,100,824,824),
        // shadow offset (0, -12), and blur 28 map 1:1 to 512pt CGRect(50,50,412,412) with offset (0, -6) and blur 14.
        let px = 1024
        guard let space = CGColorSpace(name: CGColorSpace.sRGB),
              let cg = CGContext(
                  data: nil,
                  width: px,
                  height: px,
                  bitsPerComponent: 8,
                  bytesPerRow: 0,
                  space: space,
                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
              )
        else { return nil }

        cg.interpolationQuality = .high
        cg.saveGState()
        cg.setShadow(
            offset: CGSize(width: 0, height: -12),
            blur: 28,
            color: NSColor(srgbRed: 0, green: 0, blue: 0, alpha: 0.24).cgColor
        )
        cg.draw(cgSource, in: CGRect(x: 100, y: 100, width: 824, height: 824))
        cg.restoreGState()

        guard let out = cg.makeImage() else { return nil }
        return NSImage(cgImage: out, size: NSSize(width: 512, height: 512))
    }
    #endif
}
