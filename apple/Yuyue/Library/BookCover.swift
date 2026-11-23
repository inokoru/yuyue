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
        CoverFace(title: book.title, author: book.authors.first, pdf: book.format == .pdf, paper: book.record)
    }
}

/// The title page a cover shows when the book has no image. The shelf snapshot uses the same face.
struct CoverFace: View {
    var title: String
    var author: String?
    var pdf: Bool
    var paper = false
    private static let stone = Color.yyDynamic(light: YYRGBA(235, 230, 221), dark: YYRGBA(44, 40, 36))
    /// Restrained literary cloth-bound cover tones for paper books without custom cover art.
    private static let clothTones: [Color] = [
        Color.yyDynamic(light: YYRGBA(233, 227, 215), dark: YYRGBA(50, 45, 39)), // warm linen
        Color.yyDynamic(light: YYRGBA(221, 229, 225), dark: YYRGBA(39, 48, 45)), // muted sage
        Color.yyDynamic(light: YYRGBA(222, 227, 236), dark: YYRGBA(40, 45, 54)), // slate mist
        Color.yyDynamic(light: YYRGBA(235, 224, 218), dark: YYRGBA(54, 43, 39)), // dusty terracotta
        Color.yyDynamic(light: YYRGBA(229, 225, 235), dark: YYRGBA(47, 43, 53)), // soft wisteria
        Color.yyDynamic(light: YYRGBA(235, 230, 214), dark: YYRGBA(52, 48, 37)), // pale ochre
    ]

    private var surfaceColor: Color {
        guard paper else { return Self.stone }
        var hash: UInt64 = 5381
        for byte in title.utf8 { hash = ((hash &<< 5) &+ hash) &+ UInt64(byte) }
        return Self.clothTones[Int(hash % UInt64(Self.clothTones.count))]
    }

    var body: some View {
        // Sized to the cover, so it reads the same on a small card and in the grid.
        GeometryReader { g in
            let unit = g.size.width / 100
            ZStack(alignment: .topLeading) {
                surfaceColor
                if paper {
                    // Subtle physical book-spine crease along the left edge.
                    LinearGradient(
                        colors: [.black.opacity(0.08), .white.opacity(0.22), .black.opacity(0.05), .clear],
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                    .frame(width: max(3, 8 * unit))
                }
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
                .padding(.leading, paper ? 14 * unit : 11 * unit)
                .padding(.trailing, 10 * unit)
                .padding(.vertical, 11 * unit)
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
