import ImageIO
import SwiftUI
import YuyueCore

/// The cover, or a plain paper with the title when the book has none.
struct BookCover: View {
    let book: BookSummary
    /// Filling whatever it's given, edge to edge: no corners, line or shadow (a card's cover side).
    var bleed = false
    @State private var image: CGImage?
    /// 墨水屏模式: no shadows; optional grayscale for monochrome screens.
    @AppStorage(EInk.key) private var eink = false
    @AppStorage(EInk.grayscaleKey) private var einkGray = true

    var body: some View {
        let cached = image ?? Thumbnail.cached(book.coverPath, maxPixels: Thumbnail.defaultPixels)
        if bleed {
            Color.clear
                .overlay {
                    if book.format == .audio {
                        SquareCover(book: book, shadow: false).scaledToFill()
                    } else if let cached {
                        Image(decorative: cached, scale: 1).resizable().scaledToFill()
                    } else {
                        placeholder
                    }
                }
                .clipped()
                .grayscale(eink && einkGray ? 1 : 0)
                .task(id: book.coverPath) {
                    guard book.format != .audio, cached == nil else { return }
                    let loaded = await Thumbnail.load(book.coverPath, maxPixels: Thumbnail.defaultPixels)
                    if loaded != nil {
                        withAnimation(YYMotion.quick) { image = loaded }
                    }
                }
        } else if book.format == .audio {
            // A recording's cover is square, at the bottom of the book's place.
            Color.clear
                .aspectRatio(2 / 3, contentMode: .fit)
                .overlay(alignment: .bottom) {
                    SquareCover(book: book, shadow: false)
                }
        } else {
            page(cached: cached)
        }
    }

    private func page(cached: CGImage?) -> some View {
        Color.clear
            .aspectRatio(2 / 3, contentMode: .fit)
            .overlay {
                if let cached {
                    Image(decorative: cached, scale: 1).resizable().scaledToFill()
                } else {
                    placeholder
                }
            }
            .clipShape(.rect(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(YYColor.line, lineWidth: 0.5))
            .grayscale(eink && einkGray ? 1 : 0)
            .shadow(color: .black.opacity(eink ? 0 : 0.08), radius: 6, y: 3)
            .task(id: book.coverPath) {
                guard cached == nil else { return }
                // The title page gives way to the cover in a quick fade, not a pop.
                let loaded = await Thumbnail.load(book.coverPath, maxPixels: Thumbnail.defaultPixels)
                if loaded != nil {
                    withAnimation(YYMotion.quick) { image = loaded }
                }
            }
    }

    private var placeholder: some View {
        CoverFace(title: book.title, author: book.authors.first, pdf: book.format == .pdf)
    }
}

/// The title page a cover shows when the book has no image. The shelf snapshot uses the same face.
struct CoverFace: View {
    var title: String
    var author: String?
    var pdf: Bool
    private static let stone = Color.yyDynamic(light: YYRGBA(235, 230, 221), dark: YYRGBA(44, 40, 36))

    var body: some View {
        // Sized to the cover, so it reads the same on a small card and in the grid.
        GeometryReader { g in
            let unit = g.size.width / 100
            ZStack(alignment: .topLeading) {
                // Warm stone, a shade deeper than the page and the white card, so it doesn't read as empty.
                Self.stone
                VStack(alignment: .leading, spacing: 5 * unit) {
                    Text(title)
                        .font(YYFontFamily.serif.font(size: 14 * unit, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(4)
                    if let author {
                        Text(author)
                            .font(YYFontFamily.serif.font(size: 9.5 * unit))
                            .foregroundStyle(YYColor.t2)
                            .lineLimit(1)
                    }
                }
                .padding(11 * unit)
                if pdf {
                    Text(verbatim: "PDF")
                        .font(.system(size: 8.5 * unit, weight: .medium))
                        .tracking(0.6 * unit)
                        .foregroundStyle(YYColor.t2)
                        .padding(11 * unit)
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                }
            }
        }
        // The title and author are read out below the cover already.
        .accessibilityHidden(true)
    }
}

/// A cover with no file yet: the same plate [BookCover] draws around [CoverFace].
struct CoverPlate: View {
    var title: String
    var author: String?
    var pdf: Bool

    var body: some View {
        CoverFace(title: title, author: author, pdf: pdf)
            .aspectRatio(2 / 3, contentMode: .fit)
            .clipShape(.rect(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(YYColor.line, lineWidth: 0.5))
            .shadow(color: .black.opacity(0.08), radius: 6, y: 3)
    }
}

enum Thumbnail {
    #if os(iOS)
    nonisolated static let defaultPixels = 420
    #else
    nonisolated static let defaultPixels = 600
    #endif

    nonisolated private final class Box: @unchecked Sendable {
        let image: CGImage
        init(_ image: CGImage) { self.image = image }
    }

    nonisolated(unsafe) private static let cache: NSCache<NSString, Box> = {
        let c = NSCache<NSString, Box>()
        c.countLimit = 220
        c.totalCostLimit = 64 * 1024 * 1024
        return c
    }()

    nonisolated static func cached(_ path: String?, maxPixels: Int) -> CGImage? {
        guard let path, !path.isEmpty else { return nil }
        return cache.object(forKey: "\(maxPixels):\(path)" as NSString)?.image
    }

    /// Decodes a small version of an image file off the main thread, memoized in `NSCache`.
    @concurrent nonisolated static func load(_ path: String?, maxPixels: Int) async -> CGImage? {
        guard let path, !path.isEmpty else { return nil }
        let key = "\(maxPixels):\(path)" as NSString
        if let hit = cache.object(forKey: key)?.image { return hit }
        guard let source = CGImageSourceCreateWithURL(URL(filePath: path) as CFURL, nil) else { return nil }
        let options: [CFString: Any] = [
            kCGImageSourceCreateThumbnailFromImageAlways: true,
            kCGImageSourceThumbnailMaxPixelSize: maxPixels,
            kCGImageSourceCreateThumbnailWithTransform: true,
            kCGImageSourceShouldCacheImmediately: true,
        ]
        guard let cg = CGImageSourceCreateThumbnailAtIndex(source, 0, options as CFDictionary) else { return nil }
        let cost = cg.bytesPerRow * cg.height
        cache.setObject(Box(cg), forKey: key, cost: cost)
        return cg
    }
}
