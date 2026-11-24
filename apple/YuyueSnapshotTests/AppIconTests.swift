import XCTest
#if canImport(UIKit)
import UIKit
#elseif canImport(AppKit)
import AppKit
#endif
@testable import Yuyue

final class AppIconTests: XCTestCase {
    func testIconOrderAndIdentifiers() {
        XCTAssertEqual(
            AppIconChoice.allCases.map(\.id),
            ["default", "night", "camel", "sage", "mist", "blush"]
        )
        XCTAssertEqual(AppIconChoice.default.id, "default")
        XCTAssertNil(AppIconChoice.default.alternateIconName)
        XCTAssertEqual(AppIconChoice.default.assetName, "AppIcon")
        XCTAssertEqual(AppIconChoice.night.alternateIconName, "AppIconNight")
        XCTAssertEqual(AppIconChoice.camel.alternateIconName, "AppIconCamelCloth")
        XCTAssertEqual(AppIconChoice.sage.alternateIconName, "AppIconSageCloth")
        XCTAssertEqual(AppIconChoice.mist.alternateIconName, "AppIconMistCloth")
        XCTAssertEqual(AppIconChoice.blush.alternateIconName, "AppIconBlushCloth")
    }

    func testAlternateIconNameRoundTripAndFallback() {
        for choice in AppIconChoice.allCases {
            XCTAssertEqual(AppIconChoice(alternateIconName: choice.alternateIconName), choice)
            XCTAssertEqual(choice.previewAssetName, "IconPreview_\(choice.assetName)")
        }
        XCTAssertEqual(AppIconChoice(alternateIconName: "UnknownIcon"), .default)
        XCTAssertEqual(AppIconChoice(alternateIconName: ""), .default)
    }

    @MainActor
    func testAllPreviewAssetsLoadFromAppBundle() {
        for choice in AppIconChoice.allCases {
            #if canImport(UIKit)
            let image = UIImage(named: choice.previewAssetName)
            XCTAssertNotNil(image, "Missing iOS preview asset \(choice.previewAssetName)")
            #elseif canImport(AppKit)
            let image = NSImage(named: choice.previewAssetName)
            XCTAssertNotNil(image, "Missing macOS preview asset \(choice.previewAssetName)")
            #endif
        }
    }

    #if os(macOS)
    @MainActor
    func testMacDockIconOffscreenRendering() {
        for choice in AppIconChoice.allCases where choice != .default {
            for isDark in [false, true] {
                let rendered = AppIconChoice.renderMacDockIcon(for: choice, isDark: isDark)
                XCTAssertNotNil(rendered, "Failed to render Dock icon for \(choice.id) dark=\(isDark)")
                XCTAssertEqual(rendered?.size, NSSize(width: 512, height: 512))
            }
        }
    }
    #endif
}
