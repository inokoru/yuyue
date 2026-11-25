// Draws all 6 finalized Apple app icons from 关于's 64-unit 阅 logo geometry:
//   1. AppIcon            — 宣纸 (Rice paper)  [Primary default, Liquid Glass Floating]
//   2. AppIconNight       — 暗夜 (Night)       [Always-dark obsidian paper]
//   3. AppIconCamelCloth  — 暖纸 (Paper)       [Hardcover camel bookcloth]
//   4. AppIconSageCloth   — 草木 (Sage)        [Hardcover moss-sage bookcloth]
//   5. AppIconMistCloth   — 烟雨 (Mist)        [Hardcover slate-indigo bookcloth]
//   6. AppIconBlushCloth  — 海棠 (Blush)       [Hardcover dusty-rose bookcloth]
//
// Usage:
//   swift scripts/make_app_icon.swift                 # writes directly into apple/Yuyue/ and Assets.xcassets/
//   swift scripts/make_app_icon.swift --out <dir>     # writes into <dir>/apple-icons and <dir>/xcassets
//
// Writes:
//   - <Name>.icon        (iOS 26+ & macOS 26+ Icon Composer 3-group Light/Dark/Tinted bundles)
//   - <Name>.appiconset  (iOS 17-25 Light/Dark/Tinted & macOS 14-15 primary fallbacks)
// See docs/dev/app-icon.md.

import AppKit
import Foundation

struct RGBA {
    let r: CGFloat, g: CGFloat, b: CGFloat, a: CGFloat
    init(_ r: Int, _ g: Int, _ b: Int, _ a: CGFloat = 1.0) {
        self.r = CGFloat(r) / 255.0
        self.g = CGFloat(g) / 255.0
        self.b = CGFloat(b) / 255.0
        self.a = a
    }
    var nsColor: NSColor { NSColor(srgbRed: r, green: g, blue: b, alpha: a) }
    var cgColor: CGColor { nsColor.cgColor }
    var srgb: String { String(format: "srgb:%.5f,%.5f,%.5f,%.5f", r, g, b, a) }
}

struct IconSpec {
    let id: String
    let isPrimary: Bool
    let bgLightTop: RGBA
    let bgLightBot: RGBA
    let glyphLight: RGBA
    let ruleLight: RGBA
    let bgDarkTop: RGBA
    let bgDarkBot: RGBA
    let glyphDark: RGBA
    let ruleDark: RGBA
    let lightShadow: Double
    let isLightCard: Bool
}

let icons: [IconSpec] = [
    // 1. 宣纸 · 液态玻璃浮层 (Primary Default)
    IconSpec(
        id: "AppIcon",
        isPrimary: true,
        bgLightTop: RGBA(252, 251, 248), bgLightBot: RGBA(237, 232, 221),
        glyphLight: RGBA(28, 29, 32), ruleLight: RGBA(43, 76, 126),
        bgDarkTop: RGBA(35, 33, 29), bgDarkBot: RGBA(21, 20, 17),
        glyphDark: RGBA(238, 234, 224), ruleDark: RGBA(143, 168, 200),
        lightShadow: 0.22,
        isLightCard: true
    ),
    // 2. 暗夜 (Always-dark obsidian edition)
    IconSpec(
        id: "AppIconNight",
        isPrimary: false,
        bgLightTop: RGBA(37, 35, 31), bgLightBot: RGBA(23, 22, 19),
        glyphLight: RGBA(238, 233, 222), ruleLight: RGBA(143, 168, 200),
        bgDarkTop: RGBA(28, 27, 24), bgDarkBot: RGBA(16, 15, 13),
        glyphDark: RGBA(242, 238, 229), ruleDark: RGBA(148, 174, 206),
        lightShadow: 0.28,
        isLightCard: false
    ),
    // 3. 暖纸 · 精装布面 (Anchored to YYTint.camel #8A653F)
    IconSpec(
        id: "AppIconCamelCloth",
        isPrimary: false,
        bgLightTop: RGBA(143, 104, 64), bgLightBot: RGBA(110, 78, 45),
        glyphLight: RGBA(252, 248, 242), ruleLight: RGBA(238, 218, 194),
        bgDarkTop: RGBA(56, 44, 33), bgDarkBot: RGBA(36, 28, 21),
        glyphDark: RGBA(242, 231, 219), ruleDark: RGBA(184, 138, 94),
        lightShadow: 0.24,
        isLightCard: false
    ),
    // 4. 草木 · 精装布面 (Anchored to YYTint.sage #4F7657)
    IconSpec(
        id: "AppIconSageCloth",
        isPrimary: false,
        bgLightTop: RGBA(79, 118, 87), bgLightBot: RGBA(59, 91, 66),
        glyphLight: RGBA(247, 250, 247), ruleLight: RGBA(204, 226, 208),
        bgDarkTop: RGBA(35, 48, 38), bgDarkBot: RGBA(23, 32, 25),
        glyphDark: RGBA(226, 238, 228), ruleDark: RGBA(116, 166, 128),
        lightShadow: 0.24,
        isLightCard: false
    ),
    // 5. 烟雨 · 精装布面 (Anchored to YYTint.mist #4F6D88)
    IconSpec(
        id: "AppIconMistCloth",
        isPrimary: false,
        bgLightTop: RGBA(79, 109, 136), bgLightBot: RGBA(58, 83, 106),
        glyphLight: RGBA(246, 249, 252), ruleLight: RGBA(198, 218, 234),
        bgDarkTop: RGBA(35, 46, 58), bgDarkBot: RGBA(22, 30, 39),
        glyphDark: RGBA(226, 235, 244), ruleDark: RGBA(130, 165, 194),
        lightShadow: 0.24,
        isLightCard: false
    ),
    // 6. 海棠 · 精装布面 (Anchored to YYTint.blush #A5656F)
    IconSpec(
        id: "AppIconBlushCloth",
        isPrimary: false,
        bgLightTop: RGBA(165, 101, 111), bgLightBot: RGBA(133, 77, 87),
        glyphLight: RGBA(253, 247, 248), ruleLight: RGBA(240, 208, 213),
        bgDarkTop: RGBA(58, 39, 43), bgDarkBot: RGBA(38, 25, 28),
        glyphDark: RGBA(244, 228, 230), ruleDark: RGBA(196, 129, 139),
        lightShadow: 0.24,
        isLightCard: false
    ),
]

let srgbSpace = CGColorSpace(name: CGColorSpace.sRGB)!

func bitmap(_ px: Int, opaque: Bool = false, _ draw: (CGContext) -> Void) -> CGImage {
    let info = opaque ? CGImageAlphaInfo.noneSkipLast : .premultipliedLast
    let cg = CGContext(
        data: nil, width: px, height: px, bitsPerComponent: 8, bytesPerRow: 0,
        space: srgbSpace, bitmapInfo: info.rawValue
    )!
    cg.translateBy(x: 0, y: CGFloat(px))
    cg.scaleBy(x: 1, y: -1)
    NSGraphicsContext.current = NSGraphicsContext(cgContext: cg, flipped: true)
    draw(cg)
    return cg.makeImage()!
}

func save(_ image: CGImage, _ url: URL) {
    let dest = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil)!
    CGImageDestinationAddImage(dest, image, nil)
    CGImageDestinationFinalize(dest)
}

func fillGradient(_ cg: CGContext, rect: CGRect, top: RGBA, bot: RGBA) {
    let grad = CGGradient(colorsSpace: srgbSpace, colors: [top.cgColor, bot.cgColor] as CFArray, locations: [0, 1])!
    cg.drawLinearGradient(
        grad,
        start: CGPoint(x: rect.midX, y: rect.minY),
        end: CGPoint(x: rect.midX, y: rect.maxY),
        options: [.drawsBeforeStartLocation, .drawsAfterEndLocation]
    )
}

/// 阅, 30 units, bold Songti SC, centred horizontally and 3 units above vertical center.
func drawGlyph(_ cg: CGContext, origin: CGPoint = .zero, color: NSColor, unit u: CGFloat = 16) {
    let font = NSFontManager.shared.font(withFamily: "Songti SC", traits: .boldFontMask, weight: 9, size: 30 * u)
        ?? NSFont.systemFont(ofSize: 30 * u, weight: .bold)
    let line = CTLineCreateWithAttributedString(
        NSAttributedString(string: "阅", attributes: [.font: font, .foregroundColor: color])
    )
    var ascent: CGFloat = 0, descent: CGFloat = 0
    let width = CTLineGetTypographicBounds(line, &ascent, &descent, nil)
    let top = origin.y + (64 * u - (ascent + descent)) / 2 - 3 * u
    cg.saveGState()
    cg.textMatrix = CGAffineTransform(scaleX: 1, y: -1)
    cg.textPosition = CGPoint(x: origin.x + (64 * u - width) / 2, y: top + ascent)
    CTLineDraw(line, cg)
    cg.restoreGState()
}

/// Rule: 20u in from each side, 13u up from bottom, 24u wide × 2.5u tall pill.
func drawRule(_ cg: CGContext, origin: CGPoint = .zero, color: NSColor, unit u: CGFloat = 16) {
    let r = CGRect(x: origin.x + 20 * u, y: origin.y + (64 - 13 - 2.5) * u, width: 24 * u, height: 2.5 * u)
    cg.setFillColor(color.cgColor)
    cg.addPath(CGPath(roundedRect: r, cornerWidth: r.height / 2, cornerHeight: r.height / 2, transform: nil))
    cg.fillPath()
}

/// Legacy macOS 14-15 icon card on Apple's 824-of-1024 grid (100px inset) with soft shadow.
func macLegacyIcon(_ spec: IconSpec, px: Int) -> CGImage {
    bitmap(px) { cg in
        let k = CGFloat(px) / 1024.0
        let u = 824.0 * k / 64.0
        let o = CGPoint(x: 100.0 * k, y: 100.0 * k)
        let cardRect = CGRect(origin: o, size: CGSize(width: 64.0 * u, height: 64.0 * u))
        let card = CGPath(roundedRect: cardRect, cornerWidth: 15.0 * u, cornerHeight: 15.0 * u, transform: nil)

        cg.saveGState()
        cg.setShadow(
            offset: CGSize(width: 0, height: 12 * k),
            blur: 28 * k,
            color: NSColor(srgbRed: 30 / 255, green: 28 / 255, blue: 24 / 255, alpha: 0.24).cgColor
        )
        cg.addPath(card)
        cg.setFillColor(spec.bgLightTop.cgColor)
        cg.fillPath()
        cg.restoreGState()

        cg.saveGState()
        cg.addPath(card)
        cg.clip()
        fillGradient(cg, rect: cardRect, top: spec.bgLightTop, bot: spec.bgLightBot)
        drawGlyph(cg, origin: o, color: spec.glyphLight.nsColor, unit: u)
        drawRule(cg, origin: o, color: spec.ruleLight.nsColor, unit: u)
        cg.restoreGState()

        cg.addPath(card)
        let strokeColor = spec.isLightCard
            ? NSColor.black.withAlphaComponent(0.10).cgColor
            : NSColor.white.withAlphaComponent(0.14).cgColor
        cg.setStrokeColor(strokeColor)
        cg.setLineWidth(max(1.0, 0.5 * u / 4.0))
        cg.strokePath()
    }
}

/// Legacy iOS 17-25 full-bleed 1024px square (no alpha; system applies squircle mask).
func iosLegacyIcon(top: RGBA, bot: RGBA, glyph: RGBA, rule: RGBA) -> CGImage {
    bitmap(1024, opaque: true) { cg in
        fillGradient(cg, rect: CGRect(x: 0, y: 0, width: 1024, height: 1024), top: top, bot: bot)
        drawGlyph(cg, origin: .zero, color: glyph.nsColor, unit: 16)
        drawRule(cg, origin: .zero, color: rule.nsColor, unit: 16)
    }
}

func fillValue(_ top: RGBA, _ bot: RGBA) -> [String: Any] {
    top.srgb == bot.srgb ? ["solid": top.srgb] : ["linear-gradient": [top.srgb, bot.srgb]]
}

let fm = FileManager.default
let repoRoot = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
let args = CommandLine.arguments

let iconsRoot: URL
let xcassetsRoot: URL
if let idx = args.firstIndex(of: "--out"), idx + 1 < args.count {
    let outRoot = URL(fileURLWithPath: args[idx + 1])
    iconsRoot = outRoot.appendingPathComponent("apple-icons")
    xcassetsRoot = outRoot.appendingPathComponent("xcassets")
    try fm.createDirectory(at: iconsRoot, withIntermediateDirectories: true)
    try fm.createDirectory(at: xcassetsRoot, withIntermediateDirectories: true)
    let rootContents: [String: Any] = ["info": ["author": "xcode", "version": 1]]
    try JSONSerialization.data(withJSONObject: rootContents, options: [.prettyPrinted, .sortedKeys])
        .write(to: xcassetsRoot.appendingPathComponent("Contents.json"))
} else {
    iconsRoot = repoRoot.appendingPathComponent("apple/Yuyue")
    xcassetsRoot = repoRoot.appendingPathComponent("apple/Yuyue/Assets.xcassets")
}

for spec in icons {
    // 1. Icon Composer (.icon) bundle for iOS 26+ & macOS 26+
    let pkg = iconsRoot.appendingPathComponent("\(spec.id).icon")
    let assets = pkg.appendingPathComponent("Assets")
    try? fm.removeItem(at: pkg)
    try fm.createDirectory(at: assets, withIntermediateDirectories: true)

    save(bitmap(1024) { drawGlyph($0, color: spec.glyphLight.nsColor) }, assets.appendingPathComponent("glyph-light.png"))
    save(bitmap(1024) { drawRule($0, color: spec.ruleLight.nsColor) }, assets.appendingPathComponent("rule-light.png"))
    save(bitmap(1024) { drawGlyph($0, color: spec.glyphDark.nsColor) }, assets.appendingPathComponent("glyph-dark.png"))
    save(bitmap(1024) { drawRule($0, color: spec.ruleDark.nsColor) }, assets.appendingPathComponent("rule-dark.png"))
    save(bitmap(1024) { drawGlyph($0, color: RGBA(255, 255, 255).nsColor) }, assets.appendingPathComponent("glyph-mono.png"))
    save(bitmap(1024) { drawRule($0, color: RGBA(218, 218, 218).nsColor) }, assets.appendingPathComponent("rule-mono.png"))

    let lightGroup: [String: Any] = [
        "name": "Light",
        "hidden-specializations": [
            ["value": false],
            ["appearance": "dark", "value": true],
            ["appearance": "tinted", "value": true],
        ],
        "layers": [
            ["name": "glyph-light", "image-name": "glyph-light.png", "glass": true],
            ["name": "rule-light", "image-name": "rule-light.png", "glass": true],
        ],
        "shadow": ["kind": "neutral", "opacity": spec.lightShadow],
        "specular": true,
        "translucency": ["enabled": false, "value": 0.0],
    ]

    let darkGroup: [String: Any] = [
        "name": "Dark",
        "hidden-specializations": [
            ["value": true],
            ["appearance": "dark", "value": false],
        ],
        "layers": [
            ["name": "glyph-dark", "image-name": "glyph-dark.png", "glass": true],
            ["name": "rule-dark", "image-name": "rule-dark.png", "glass": true],
        ],
        "shadow": ["kind": "neutral", "opacity": 0.30],
        "specular": true,
        "translucency": ["enabled": false, "value": 0.0],
    ]

    let tintedGroup: [String: Any] = [
        "name": "Tinted",
        "hidden-specializations": [
            ["value": true],
            ["appearance": "tinted", "value": false],
        ],
        "layers": [
            ["name": "glyph-mono", "image-name": "glyph-mono.png", "glass": true],
            ["name": "rule-mono", "image-name": "rule-mono.png", "glass": true],
        ],
        "shadow": ["kind": "neutral", "opacity": 0.25],
        "specular": true,
        "translucency": ["enabled": true, "value": 0.25],
    ]

    let iconJSON: [String: Any] = [
        "fill-specializations": [
            ["value": fillValue(spec.bgLightTop, spec.bgLightBot)],
            ["appearance": "dark", "value": fillValue(spec.bgDarkTop, spec.bgDarkBot)],
        ],
        "groups": [lightGroup, darkGroup, tintedGroup],
        "supported-platforms": ["squares": "shared"],
    ]
    try JSONSerialization.data(withJSONObject: iconJSON, options: [.prettyPrinted, .sortedKeys])
        .write(to: pkg.appendingPathComponent("icon.json"))

    // 2. Legacy Asset Catalog (.appiconset) for iOS 17-25 & macOS 14-15
    let appiconset = xcassetsRoot.appendingPathComponent("\(spec.id).appiconset")
    try? fm.removeItem(at: appiconset)
    try fm.createDirectory(at: appiconset, withIntermediateDirectories: true)

    save(
        iosLegacyIcon(top: spec.bgLightTop, bot: spec.bgLightBot, glyph: spec.glyphLight, rule: spec.ruleLight),
        appiconset.appendingPathComponent("icon_ios.png")
    )
    save(
        iosLegacyIcon(top: spec.bgDarkTop, bot: spec.bgDarkBot, glyph: spec.glyphDark, rule: spec.ruleDark),
        appiconset.appendingPathComponent("icon_ios_dark.png")
    )
    save(
        iosLegacyIcon(top: RGBA(34, 34, 34), bot: RGBA(16, 16, 16), glyph: RGBA(255, 255, 255), rule: RGBA(218, 218, 218)),
        appiconset.appendingPathComponent("icon_ios_tinted.png")
    )

    var images: [[String: Any]] = [
        [
            "filename": "icon_ios.png",
            "idiom": "universal",
            "platform": "ios",
            "size": "1024x1024",
        ],
        [
            "appearances": [["appearance": "luminosity", "value": "dark"]],
            "filename": "icon_ios_dark.png",
            "idiom": "universal",
            "platform": "ios",
            "size": "1024x1024",
        ],
        [
            "appearances": [["appearance": "luminosity", "value": "tinted"]],
            "filename": "icon_ios_tinted.png",
            "idiom": "universal",
            "platform": "ios",
            "size": "1024x1024",
        ],
    ]

    if spec.isPrimary {
        let macSlots: [(size: String, scale: String, px: Int)] = [
            ("16x16", "1x", 16), ("16x16", "2x", 32),
            ("32x32", "1x", 32), ("32x32", "2x", 64),
            ("128x128", "1x", 128), ("128x128", "2x", 256),
            ("256x256", "1x", 256), ("256x256", "2x", 512),
            ("512x512", "1x", 512), ("512x512", "2x", 1024),
        ]
        for px in [16, 32, 64, 128, 256, 512, 1024] {
            save(macLegacyIcon(spec, px: px), appiconset.appendingPathComponent("icon_\(px).png"))
        }
        for s in macSlots {
            images.insert([
                "filename": "icon_\(s.px).png",
                "idiom": "mac",
                "scale": s.scale,
                "size": s.size,
            ], at: 0)
        }
    }

    let contents: [String: Any] = [
        "images": images,
        "info": ["author": "xcode", "version": 1],
    ]
    try JSONSerialization.data(withJSONObject: contents, options: [.prettyPrinted, .sortedKeys])
        .write(to: appiconset.appendingPathComponent("Contents.json"))
}

print("Generated \(icons.count) .icon bundles into \(iconsRoot.path) and .appiconset fallbacks into \(xcassetsRoot.path)")
