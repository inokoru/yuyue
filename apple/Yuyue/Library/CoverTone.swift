import CoreGraphics
import SwiftUI

/// A cover's one colour: what 读过's strips write their titles over, and what 按颜色 sorts by.
/// Measured once a cover is loaded, kept for the session.
@Observable
final class CoverTones {
    static let shared = CoverTones()

    struct Tone: Equatable {
        var hue: Double
        var saturation: Double
        /// 0 black … 1 white, as the eye sees it.
        var luminance: Double
    }

    private(set) var tones: [String: Tone] = [:]

    func set(_ tone: Tone, for id: String) {
        if tones[id] != tone { tones[id] = tone }
    }

    /// The cover drawn into one pixel.
    nonisolated static func measure(_ image: CGImage) -> Tone? {
        var pixel = [UInt8](repeating: 0, count: 4)
        guard let ctx = CGContext(data: &pixel, width: 1, height: 1, bitsPerComponent: 8, bytesPerRow: 4,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)
        else { return nil }
        ctx.interpolationQuality = .medium
        ctx.draw(image, in: CGRect(x: 0, y: 0, width: 1, height: 1))
        let r = Double(pixel[0]) / 255, g = Double(pixel[1]) / 255, b = Double(pixel[2]) / 255
        let hi = max(r, g, b), lo = min(r, g, b), d = hi - lo
        var h = 0.0
        if d > 0 {
            if hi == r { h = ((g - b) / d).truncatingRemainder(dividingBy: 6) }
            else if hi == g { h = (b - r) / d + 2 }
            else { h = (r - g) / d + 4 }
            h = (h * 60 + 360).truncatingRemainder(dividingBy: 360)
        }
        return Tone(hue: h, saturation: hi == 0 ? 0 : d / hi, luminance: 0.2126 * r + 0.7152 * g + 0.0722 * b)
    }

    /// 按颜色: the day's sky — night blue, blue, green, yellow, orange, red, pink, violet;
    /// the greys (and books without a cover) after them, light to dark.
    func colorKey(_ id: String) -> (Int, Double, Double) {
        guard let t = tones[id], t.saturation > 0.12 else { return (1, -(tones[id]?.luminance ?? 0.85), 0) }
        return (0, (600 - t.hue).truncatingRemainder(dividingBy: 360), t.luminance)
    }
}
