import AVFoundation
import SwiftUI
import YuyueCore

/// Shared visual primitives for Yuyue's warm-paper Podcast interface:
/// - Warm-paper surfaces (`#EFECE4` typographic covers, `#E2DCD2` hairline rules, pure `#FFFFFF` cards
///   with `0.5px` ring + soft shadow instead of heavy borders).
/// - Borderless warm-paper primary pills (`tint.soft` fill + `tint.accent` ink, zero harsh outline).
/// - Refined Songti / Georgia medium-weight editorial typography.
enum PodcastStyle {
    /// Paper typographic cover surface (`#EFECE4` in light mode, warm charcoal in dark mode).
    static func paperCover(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(yy: YYRGBA(44, 42, 39)) : Color(yy: YYRGBA(239, 236, 228))
    }

    /// Subtle inner cover hairline (`rgba(42,43,46,.10)` in light mode).
    static func paperCoverBorder(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.08) : Color(yy: YYRGBA(42, 43, 46, 0.10))
    }

    /// Studio 3-entry character badges (`导`, `桌`, `词`).
    static func studioBadgeColors(for kind: StudioKind, scheme: ColorScheme) -> (bg: Color, fg: Color) {
        if scheme == .dark {
            switch kind {
            case .bookGuide: return (Color(yy: YYRGBA(45, 74, 110, 0.28)), Color(yy: YYRGBA(150, 190, 232)))
            case .roundtable: return (Color(yy: YYRGBA(70, 96, 65, 0.28)), Color(yy: YYRGBA(168, 206, 160)))
            case .vocabRadio: return (Color(yy: YYRGBA(93, 76, 122, 0.28)), Color(yy: YYRGBA(196, 180, 228)))
            case .weeklyDigest: return (YYTint.standard.soft, YYTint.standard.accent)
            }
        }
        switch kind {
        case .bookGuide: return (Color(yy: YYRGBA(230, 237, 244)), Color(yy: YYRGBA(45, 74, 110)))
        case .roundtable: return (Color(yy: YYRGBA(232, 239, 229)), Color(yy: YYRGBA(70, 96, 65)))
        case .vocabRadio: return (Color(yy: YYRGBA(238, 234, 245)), Color(yy: YYRGBA(93, 76, 122)))
        case .weeklyDigest: return (YYTint.standard.soft, YYTint.standard.accent)
        }
    }

    /// Warm inner book-leaf surface inside stationery cards (`#FAF8F3` in light, muted charcoal in dark).
    static func leafSurface(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color(yy: YYRGBA(28, 27, 24)) : Color(yy: YYRGBA(250, 248, 243))
    }

    /// Subtle 0.5pt pill hairline ring.
    static func pillBorder(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.14) : Color(yy: YYRGBA(60, 50, 38, 0.16))
    }

    /// Soft shadow under porcelain white pills and circular icon buttons.
    static func pillShadow(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.black.opacity(0.32) : Color(yy: YYRGBA(30, 24, 18, 0.06))
    }

    /// Airy delicate active-row tint wash across all Yuyue tints.
    static func rowTintWash(_ tint: YYTint, _ scheme: ColorScheme) -> Color {
        tint.accent.opacity(scheme == .dark ? 0.14 : 0.095)
    }

    /// Soft hairline accent ring (28% accent).
    static func accentRing(_ tint: YYTint, _ scheme: ColorScheme) -> Color {
        tint.accent.opacity(scheme == .dark ? 0.36 : 0.28)
    }

    #if os(macOS)
    private static let hasSongtiLight = NSFont(name: "STSongti-SC-Light", size: 12) != nil
    private static let hasSongtiRegular = NSFont(name: "STSongti-SC-Regular", size: 12) != nil
    #elseif os(iOS)
    private static let hasSongtiLight = UIFont(name: "STSongti-SC-Light", size: 12) != nil
    private static let hasSongtiRegular = UIFont(name: "STSongti-SC-Regular", size: 12) != nil
    #endif

    /// Refined Songti serif for editorial headings and literary excerpts.
    /// macOS `Songti SC` ships Light/Regular/Bold/Black — `.semibold` jumps to heavy Bold,
    /// so `STSongti-SC-Regular` / `STSongti-SC-Light` preserves delicate bookroom typography.
    static func songti(_ size: CGFloat, light: Bool = false) -> SwiftUI.Font {
        let s = size * TextScale.shared.factor
        #if os(macOS) || os(iOS)
        if light ? hasSongtiLight : hasSongtiRegular {
            return .custom(light ? "STSongti-SC-Light" : "STSongti-SC-Regular", fixedSize: s)
        }
        #endif
        return YYUI.serif(size, .regular)
    }

    /// Delicate stationery hairline.
    static func hairline(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.085) : Color(yy: YYRGBA(45, 40, 34, 0.095))
    }

    /// Ultra-soft row divider.
    static func softDivider(_ scheme: ColorScheme) -> Color {
        scheme == .dark ? Color.white.opacity(0.05) : Color(yy: YYRGBA(45, 40, 34, 0.06))
    }

    /// Refined editorial serif for excerpts and clips.
    static func editorialFont(for text: String, size: CGFloat, weight: SwiftUI.Font.Weight = .regular) -> SwiftUI.Font {
        songti(size, light: false)
    }
}

/// Thin-stroke geometric icons for Podcast navigation and controls.
enum PodcastGlyph: Sendable {
    case allGrid
    case openBook
    case waves
    case weekly
    case roundtable
    case vocab
    case link
    case bookmark
    case bookmarkFilled
    case waveform
    case transcriptLines
    case plus
    case refresh
    case xmark
    case chevronDown
}

struct PodcastGlyphView: View {
    let glyph: PodcastGlyph
    var size: CGFloat = 15
    var lineWidth: CGFloat = 1.45

    var body: some View {
        Group {
            switch glyph {
            case .bookmarkFilled:
                PodcastGlyphPath(glyph: .bookmark)
                    .fill(style: FillStyle(eoFill: false))
                    .overlay(
                        PodcastGlyphPath(glyph: .bookmark)
                            .stroke(style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round))
                    )
            default:
                PodcastGlyphPath(glyph: glyph)
                    .stroke(style: StrokeStyle(lineWidth: lineWidth, lineCap: .round, lineJoin: .round))
            }
        }
        .frame(width: size, height: size)
    }
}

private nonisolated struct PodcastGlyphPath: Shape {
    let glyph: PodcastGlyph

    func path(in rect: CGRect) -> Path {
        var p = Path()
        let sx = rect.width / 16
        let sy = rect.height / 16
        func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
            CGPoint(x: rect.minX + x * sx, y: rect.minY + y * sy)
        }

        switch glyph {
        case .allGrid:
            for (x, y) in [(2.5, 2.5), (9.1, 2.5), (2.5, 9.1), (9.1, 9.1)] {
                p.addRoundedRect(
                    in: CGRect(x: rect.minX + x * sx, y: rect.minY + y * sy, width: 4.4 * sx, height: 4.4 * sy),
                    cornerSize: CGSize(width: 1.0 * sx, height: 1.0 * sy)
                )
            }
        case .openBook:
            // Open book left & right pages + center spine
            p.move(to: pt(2.4, 4.2))
            p.addQuadCurve(to: pt(8, 4.8), control: pt(5.4, 3.4))
            p.addQuadCurve(to: pt(13.6, 4.2), control: pt(10.6, 3.4))
            p.addLine(to: pt(13.6, 12.1))
            p.addQuadCurve(to: pt(8, 12.7), control: pt(10.6, 11.3))
            p.addQuadCurve(to: pt(2.4, 12.1), control: pt(5.4, 11.3))
            p.closeSubpath()
            p.move(to: pt(8, 4.8))
            p.addLine(to: pt(8, 12.7))
        case .waves:
            p.move(to: pt(2.5, 6.2))
            p.addQuadCurve(to: pt(8.0, 6.2), control: pt(5.2, 4.4))
            p.addQuadCurve(to: pt(13.5, 6.2), control: pt(10.8, 8.0))
            p.move(to: pt(2.5, 10.2))
            p.addQuadCurve(to: pt(8.0, 10.2), control: pt(5.2, 8.4))
            p.addQuadCurve(to: pt(13.5, 10.2), control: pt(10.8, 12.0))
        case .weekly:
            p.move(to: pt(3, 4.5)); p.addLine(to: pt(13, 4.5))
            p.move(to: pt(3, 8)); p.addLine(to: pt(10, 8))
            p.move(to: pt(3, 11.5)); p.addLine(to: pt(8, 11.5))
            p.addEllipse(in: CGRect(x: rect.minX + 10 * sx, y: rect.minY + 8.5 * sy, width: 4 * sx, height: 4 * sy))
        case .roundtable:
            p.addEllipse(in: CGRect(x: rect.minX + 2.7 * sx, y: rect.minY + 4.9 * sy, width: 6.2 * sx, height: 6.2 * sy))
            p.addEllipse(in: CGRect(x: rect.minX + 7.1 * sx, y: rect.minY + 4.9 * sy, width: 6.2 * sx, height: 6.2 * sy))
        case .vocab:
            // 文 on left
            p.move(to: pt(2.6, 4.6)); p.addLine(to: pt(7.8, 4.6))
            p.move(to: pt(5.2, 3.0)); p.addLine(to: pt(5.2, 4.6))
            p.move(to: pt(7.0, 4.6))
            p.addQuadCurve(to: pt(3.2, 11.4), control: pt(6.2, 8.6))
            p.move(to: pt(4.2, 6.4))
            p.addQuadCurve(to: pt(7.8, 11.0), control: pt(5.6, 9.2))
            // A on right
            p.move(to: pt(9.5, 12.2)); p.addLine(to: pt(11.6, 5.4)); p.addLine(to: pt(13.7, 12.2))
            p.move(to: pt(10.3, 10.0)); p.addLine(to: pt(12.9, 10.0))
        case .link:
            // Interlocking diagonal links
            p.move(to: pt(6.6, 9.4)); p.addLine(to: pt(9.4, 6.6))
            p.move(to: pt(5.3, 7.6))
            p.addLine(to: pt(3.8, 9.1))
            p.addCurve(to: pt(7.0, 12.3), control1: pt(2.5, 10.4), control2: pt(5.7, 13.6))
            p.addLine(to: pt(8.5, 10.8))
            p.move(to: pt(10.7, 8.4))
            p.addLine(to: pt(12.2, 6.9))
            p.addCurve(to: pt(9.0, 3.7), control1: pt(13.5, 5.6), control2: pt(10.3, 2.4))
            p.addLine(to: pt(7.5, 5.2))
        case .bookmark, .bookmarkFilled:
            p.move(to: pt(4.6, 3.0))
            p.addLine(to: pt(11.4, 3.0))
            p.addQuadCurve(to: pt(12.2, 3.8), control: pt(12.2, 3.0))
            p.addLine(to: pt(12.2, 13.2))
            p.addLine(to: pt(8.0, 10.4))
            p.addLine(to: pt(3.8, 13.2))
            p.addLine(to: pt(3.8, 3.8))
            p.addQuadCurve(to: pt(4.6, 3.0), control: pt(3.8, 3.0))
            p.closeSubpath()
        case .waveform:
            for (x, y0, y1) in [(2.8, 6.6, 9.4), (5.4, 4.0, 12.0), (8.0, 5.4, 10.6), (10.6, 3.2, 12.8), (13.2, 6.2, 9.8)] {
                p.move(to: pt(x, y0)); p.addLine(to: pt(x, y1))
            }
        case .transcriptLines:
            p.move(to: pt(3.0, 4.8)); p.addLine(to: pt(13.0, 4.8))
            p.move(to: pt(3.0, 8.0)); p.addLine(to: pt(10.6, 8.0))
            p.move(to: pt(3.0, 11.2)); p.addLine(to: pt(8.4, 11.2))
        case .plus:
            p.move(to: pt(8, 3.2)); p.addLine(to: pt(8, 12.8))
            p.move(to: pt(3.2, 8)); p.addLine(to: pt(12.8, 8))
        case .refresh:
            p.addArc(center: pt(8, 8), radius: 4.7 * sx, startAngle: .degrees(-45), endAngle: .degrees(260), clockwise: false)
            p.move(to: pt(11.2, 3.2)); p.addLine(to: pt(11.6, 5.6)); p.addLine(to: pt(9.2, 5.4))
        case .xmark:
            p.move(to: pt(4.5, 4.5)); p.addLine(to: pt(11.5, 11.5))
            p.move(to: pt(11.5, 4.5)); p.addLine(to: pt(4.5, 11.5))
        case .chevronDown:
            p.move(to: pt(4.2, 6.2)); p.addLine(to: pt(8.0, 10.0)); p.addLine(to: pt(11.8, 6.2))
        }
        return p
    }
}

// MARK: - Harmonized Yuyue Stationery & Circular Controls (Shared Mac + iPhone)

/// Crisp 0.5pt stationery white card used for Podcast groups and the tabletop listening readout.
struct PodcastStationeryCard<Content: View>: View {
    var cornerRadius: CGFloat = 13
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        content
            .background(YYColor.card, in: RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                    .strokeBorder(PodcastStyle.hairline(scheme), lineWidth: 0.5)
            )
            .shadow(
                color: scheme == .dark ? Color.black.opacity(0.24) : Color(yy: YYRGBA(36, 32, 26, 0.035)),
                radius: 2,
                y: 1
            )
    }
}

/// Tactile porcelain-white stationery capsule (`0.5pt` stroke + whisper shadow,
/// pure white card fill, accent-colored ink when `accent == true`).
struct PodcastPaperPill: View {
    let title: String
    var symbol: String? = nil
    var glyph: PodcastGlyph? = nil
    var accent = false
    var height: CGFloat = 26
    var horizontalPadding: CGFloat = 11
    let action: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4.5) {
                if let glyph {
                    PodcastGlyphView(glyph: glyph, size: 11.5, lineWidth: 1.4)
                } else if let symbol {
                    Image(systemName: symbol)
                        .font(.system(size: 10, weight: .medium))
                }
                Text(title)
                    .font(.yy(size: 11.5, weight: accent ? .medium : .regular))
                    .lineLimit(1)
                    .fixedSize(horizontal: true, vertical: false)
            }
            .foregroundStyle(accent ? tint.accent : YYColor.t1)
            .padding(.horizontal, horizontalPadding)
            .frame(height: height)
            .background(Capsule().fill(YYColor.card))
            .overlay(
                Capsule().strokeBorder(PodcastStyle.pillBorder(scheme), lineWidth: 0.5)
            )
            .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
            .contentShape(Capsule())
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }
}

/// Tactile circular icon button (`32pt` top-right, `36pt` right-edge drawer, `40pt` desk quick-launch).
struct PodcastCircleIconButton: View {
    var symbol: String? = nil
    var glyph: PodcastGlyph? = nil
    var size: CGFloat = 32
    var iconSize: CGFloat? = nil
    var active = false
    var accent = false
    var showDot = false
    var accessibilityLabel: String = ""
    let action: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        let tinted = active || accent
        let glyphSize = iconSize ?? size * 0.43
        Button(action: action) {
            ZStack(alignment: .topTrailing) {
                Group {
                    if let glyph {
                        PodcastGlyphView(glyph: glyph, size: glyphSize, lineWidth: 1.45)
                    } else if let symbol {
                        Image(systemName: symbol)
                            .font(.system(size: glyphSize, weight: .medium))
                    }
                }
                .foregroundStyle(tinted ? tint.accent : YYColor.t2)
                .frame(width: size, height: size)
                .background(
                    Circle().fill(tinted ? PodcastStyle.rowTintWash(tint, scheme) : YYColor.card)
                )
                .overlay(
                    Circle().strokeBorder(
                        tinted ? PodcastStyle.accentRing(tint, scheme) : PodcastStyle.pillBorder(scheme),
                        lineWidth: 0.5
                    )
                )
                .shadow(
                    color: tinted ? .clear : PodcastStyle.pillShadow(scheme),
                    radius: size >= 36 ? 4 : 2.5,
                    y: size >= 36 ? 2 : 1
                )

                if showDot {
                    Circle()
                        .fill(tint.accent)
                        .frame(width: 6, height: 6)
                        .overlay(Circle().strokeBorder(YYColor.card, lineWidth: 1.2))
                        .offset(x: -5, y: 5)
                }
            }
            .contentShape(Circle())
        }
        .buttonStyle(YYPressStyle(scale: 0.95))
        .accessibilityLabel(accessibilityLabel.isEmpty ? (symbol ?? "") : accessibilityLabel)
    }
}

/// Minimalist 19–32pt show bookplate mark: loads real show artwork when available, otherwise renders
/// a refined muted literary monogram (or open-book emblem for 书房电台).
struct PodcastShowMark: View {
    let title: String
    var artworkURL: String? = nil
    var isStudio = false
    var size: CGFloat = 22

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var cornerRadius: CGFloat {
        size >= 30 ? 8 : (size <= 20 ? 5 : 6)
    }

    private var palette: (bg: Color, fg: Color) {
        if isStudio {
            return (PodcastStyle.rowTintWash(tint, scheme), tint.accent)
        }
        let tones: [(YYRGBA, YYRGBA)] = [
            (YYRGBA(38, 41, 46), YYRGBA(245, 242, 235)),   // Ink slate
            (YYRGBA(94, 48, 53), YYRGBA(248, 240, 236)),   // Literary burgundy
            (YYRGBA(38, 64, 62), YYRGBA(236, 244, 241)),   // Deep spruce
            (YYRGBA(47, 71, 91), YYRGBA(238, 243, 248)),   // Slate blue
            (YYRGBA(82, 65, 48), YYRGBA(246, 240, 231)),   // Warm umber
        ]
        var hash: UInt64 = 5381
        for b in title.utf8 { hash = ((hash << 5) &+ hash) &+ UInt64(b) }
        let pick = tones[Int(hash % UInt64(tones.count))]
        return (Color(yy: pick.0), Color(yy: pick.1))
    }

    private var glyph: String {
        let trimmed = title.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first else { return "P" }
        return String(first).uppercased()
    }

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                .fill(palette.bg)
            if !isStudio, let artworkURL, let url = URL(string: artworkURL) {
                AsyncImage(url: url) { phase in
                    if case .success(let image) = phase {
                        image.resizable().scaledToFill()
                    } else {
                        monogram
                    }
                }
            } else {
                monogram
            }
        }
        .frame(width: size, height: size)
        .clipShape(RoundedRectangle(cornerRadius: cornerRadius, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: cornerRadius, style: .continuous)
                .strokeBorder(
                    isStudio
                        ? PodcastStyle.accentRing(tint, scheme)
                        : (scheme == .dark ? Color.white.opacity(0.18) : Color.black.opacity(0.12)),
                    lineWidth: 0.5
                )
        )
    }

    @ViewBuilder
    private var monogram: some View {
        if isStudio {
            PodcastGlyphView(glyph: .openBook, size: size * 0.56, lineWidth: 1.45)
                .foregroundStyle(palette.fg)
        } else {
            Text(glyph)
                .font(PodcastStyle.songti(size * 0.52))
                .foregroundStyle(palette.fg)
        }
    }
}

/// Recessed matte-inset segmented control for `原声 / 原声线中文 / 双语`.
struct PodcastTrackSegmentedControl: View {
    let selection: PodcastTrack
    var compact = false
    let onSelect: (PodcastTrack) -> Void

    @Namespace private var trackNS
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private let options: [(PodcastTrack, String)] = [
        (.original, String(localized: "原声")),
        (.dub, String(localized: "原声线中文")),
        (.coach, String(localized: "双语")),
    ]

    var body: some View {
        HStack(spacing: 2) {
            ForEach(options, id: \.0) { track, label in
                let isOn = selection == track
                Button {
                    withAnimation(YYMotion.quick) { onSelect(track) }
                } label: {
                    Text(label)
                        .font(.yy(size: compact ? 10.5 : 11, weight: isOn ? .semibold : .medium))
                        .foregroundStyle(isOn ? tint.accent : YYColor.t3)
                        .lineLimit(1)
                        .fixedSize(horizontal: true, vertical: false)
                        .padding(.horizontal, compact ? 7 : 8.5)
                        .frame(height: 22)
                        .background {
                            if isOn {
                                Capsule()
                                    .fill(YYColor.card)
                                    .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                                    .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                                    .matchedGeometryEffect(id: "trackPill", in: trackNS)
                            }
                        }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(YYColor.inset, in: Capsule())
        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
        .fixedSize(horizontal: true, vertical: false)
    }
}

/// Quiet 3-bar audio equalizer indicator for actively playing rows.
struct PodcastEqualizerBars: View {
    var active = true
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @State private var pulse = false

    var body: some View {
        HStack(alignment: .bottom, spacing: 1.5) {
            Capsule().fill(tint.accent).frame(width: 2, height: active && pulse ? 9 : 4)
            Capsule().fill(tint.accent).frame(width: 2, height: active && !pulse ? 10 : 5)
            Capsule().fill(tint.accent).frame(width: 2, height: active && pulse ? 7 : 3)
        }
        .frame(height: 11, alignment: .bottom)
        .onAppear {
            guard active, !YYMotion.reduce else { return }
            withAnimation(.easeInOut(duration: 0.55).repeatForever(autoreverses: true)) {
                pulse = true
            }
        }
    }
}

/// Shared tab identifier for the floating Mac side drawer & iPhone bottom sheet.
enum PodcastDrawerTab: Hashable {
    case clips, later, voices, local
}

/// Shared stationery drawer content used by both Mac right-edge drawer and iPhone bottom sheet.
struct PodcastDrawerContentView: View {
    let library: Library
    @Binding var tab: PodcastDrawerTab
    var isCompact = false
    let onOpenIssue: (String) -> Void
    let onClose: () -> Void

    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @Namespace private var tabNS

    @State private var clipNotes: [String: String] = [:]
    @State private var playingVoiceID: String?
    @State private var auditionTask: Task<Void, Never>?

    private var hub: PodcastHub { library.podcastHub }

    private var savedLaterIssues: [PodcastIssue] {
        hub.archiveIssues + hub.newsstandIssues
    }

    private struct VoiceItem: Identifiable {
        let id: String
        let name: String
        let role: String
        let sample: String
        let auditionText: String
        let isStudio: Bool
        let isSelf: Bool
    }

    private var voiceItems: [VoiceItem] {
        var items: [VoiceItem] = [
            VoiceItem(
                id: "studio-lin",
                name: String(localized: "林深 · 书房主播"),
                role: String(localized: "本地声卡 · 沉静温和"),
                sample: String(localized: "书房电台默认主讲"),
                auditionText: String(localized: "欢迎回到书房电台，所有内容都严格停在你读到的页码之前。"),
                isStudio: true,
                isSelf: false
            ),
            VoiceItem(
                id: "studio-su",
                name: String(localized: "苏晚 · 理性对谈"),
                role: String(localized: "本地声卡 · 清晰克制"),
                sample: String(localized: "跨书圆桌搭档"),
                auditionText: String(localized: "把同一件事放进另一本书的语境里重说一遍，理解才会真正立体起来。"),
                isStudio: true,
                isSelf: false
            ),
        ]
        for entry in ClonedVoices.entries() {
            items.append(VoiceItem(
                id: "clone-\(entry.id)",
                name: entry.name,
                role: String(localized: "嘉宾声纹 · 本机提取"),
                sample: entry.subtitle,
                auditionText: String(localized: "已在本机安全区就绪，支持跨期自动复用该声纹。"),
                isStudio: false,
                isSelf: false
            ))
        }
        items.append(VoiceItem(
            id: "self-clone",
            name: String(localized: "我的声纹（本机克隆）"),
            role: String(localized: "仅存本机安全区 · 不上传"),
            sample: String(localized: "可在周刊中播报自己的批注"),
            auditionText: String(localized: "录制或导入个人声纹后，周刊会用你的声音轻声读出你留下的批注。"),
            isStudio: false,
            isSelf: true
        ))
        return items
    }

    private var visibleTabs: [(PodcastDrawerTab, String)] {
        if isCompact {
            return [
                (.clips, String(localized: "书摘")),
                (.later, String(localized: "稍后听")),
                (.voices, String(localized: "声卡")),
                (.local, String(localized: "本机")),
            ]
        }
        if tab == .clips || tab == .later {
            return [
                (.clips, String(localized: "原声书摘 · \(hub.clips.count)")),
                (.later, String(localized: "稍后听 · \(savedLaterIssues.count)")),
            ]
        }
        return [
            (.voices, String(localized: "声音卡 · \(voiceItems.count)")),
            (.local, String(localized: "本机整理")),
        ]
    }

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 10) {
                HStack(spacing: 2) {
                    ForEach(visibleTabs, id: \.0) { item, title in
                        let on = tab == item
                        Button {
                            withAnimation(YYMotion.quick) { tab = item }
                        } label: {
                            Text(title)
                                .font(.yy(size: 11.5, weight: on ? .semibold : .medium))
                                .foregroundStyle(on ? tint.accent : YYColor.t3)
                                .padding(.horizontal, 10)
                                .frame(height: 24)
                                .background {
                                    if on {
                                        Capsule()
                                            .fill(YYColor.card)
                                            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                                            .shadow(color: PodcastStyle.pillShadow(scheme), radius: 1.5, y: 1)
                                            .matchedGeometryEffect(id: "drawerTab", in: tabNS)
                                    }
                                }
                                .contentShape(Capsule())
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(2.5)
                .background(YYColor.inset, in: Capsule())
                .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))

                Spacer()

                Button(action: onClose) {
                    PodcastGlyphView(glyph: .xmark, size: 13.5, lineWidth: 1.5)
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 28, height: 28)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(String(localized: "关闭"))
            }
            .padding(.horizontal, 16)
            .padding(.vertical, 12)

            Rectangle().fill(YYColor.line).frame(height: 0.5)

            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    switch tab {
                    case .clips: clipsList
                    case .later: laterList
                    case .voices: voicesList
                    case .local: localStatusList
                    }
                }
                .padding(16)
            }
            .scrollIndicators(.never)
        }
        .background(YYColor.card)
        .onDisappear {
            auditionTask?.cancel()
            hub.stopAudition()
            playingVoiceID = nil
        }
    }

    @ViewBuilder
    private var clipsList: some View {
        if hub.clips.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("还没有原声书摘")
                    .font(YYUI.serif(14.5, .medium))
                    .foregroundStyle(YYColor.t1)
                Text("收听时点「划线」或按 H，即可截取当前句子的原声片段永久留存，整集音频清理后也不丢失。")
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .lineSpacing(4)
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 11, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
        } else {
            ForEach(hub.clips, id: \.id) { clip in
                let isPlaying = hub.playingClipID == clip.id
                let dur = max(1, clip.t1 - clip.t0)
                let noteBinding = Binding<String>(
                    get: { clipNotes[clip.id] ?? clip.thought ?? "" },
                    set: { clipNotes[clip.id] = $0 }
                )
                VStack(alignment: .leading, spacing: 8) {
                    Text("「\(clip.quoteText)」")
                        .font(PodcastStyle.editorialFont(for: clip.quoteText, size: 13.5, weight: .regular))
                        .foregroundStyle(YYColor.t1)
                        .lineSpacing(4)

                    HStack(spacing: 8) {
                        Text("\(clip.showTitle) · \(PodcastFormat.clock(clip.t0))")
                            .font(.yy(size: 11))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                        Spacer()
                        PodcastPaperPill(
                            title: isPlaying ? String(localized: "播放中…") : String(format: "%.1fs", dur),
                            symbol: isPlaying ? "stop.fill" : "play.fill",
                            accent: isPlaying,
                            height: 23,
                            horizontalPadding: 9
                        ) {
                            hub.replayClip(clip)
                        }
                        Button {
                            onClose()
                            onOpenIssue(clip.issueId)
                        } label: {
                            Image(systemName: "chevron.right")
                                .font(.system(size: 10.5, weight: .medium))
                                .foregroundStyle(YYColor.t3)
                                .frame(width: 23, height: 23)
                                .background(Color.clear, in: RoundedRectangle(cornerRadius: 6, style: .continuous))
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .help(String(localized: "回到原句"))
                    }

                    // Dashed divider + inline thought input (`.clip-note-in`)
                    PodcastDashedRule()
                        .stroke(YYColor.line, style: StrokeStyle(lineWidth: 0.5, dash: [3, 3]))
                        .frame(height: 1)
                        .padding(.top, 2)

                    TextField(
                        "",
                        text: noteBinding,
                        prompt: Text(String(localized: "添一句随想…")).foregroundStyle(YYColor.t3.opacity(0.85))
                    )
                    .textFieldStyle(.plain)
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t2)
                }
                .padding(.horizontal, 13)
                .padding(.vertical, 12)
                .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 11, style: .continuous))
                .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
                .contextMenu {
                    Button(String(localized: "跳到原句")) {
                        onClose()
                        onOpenIssue(clip.issueId)
                    }
                    Button(String(localized: "删除书摘"), role: .destructive) {
                        hub.deleteClip(clip)
                    }
                }
            }
        }
    }

    @ViewBuilder
    private var laterList: some View {
        if savedLaterIssues.isEmpty {
            Text("稍后听空着 · 在搜索条粘贴单集链接或点书签即可存到这里")
                .font(.yy(size: 12.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
                .frame(maxWidth: .infinity)
                .padding(.vertical, 36)
        } else {
            VStack(spacing: 0) {
                ForEach(Array(savedLaterIssues.enumerated()), id: \.element.id) { idx, item in
                    if idx > 0 { Rectangle().fill(PodcastStyle.softDivider(scheme)).frame(height: 0.5) }
                    HStack(alignment: .top, spacing: 10) {
                        PodcastShowMark(title: item.showTitle, artworkURL: item.artworkUrl, isStudio: item.studioKind != nil, size: 22)
                            .padding(.top, 1)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(item.title)
                                .font(.yy(size: 13, weight: .medium))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(2)
                            Text("\(item.showTitle) · \(PodcastFormat.compactDuration(item.durationSec))")
                                .font(.yy(size: 11.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .contentShape(.rect)
                        .onTapGesture {
                            onClose()
                            onOpenIssue(item.id)
                        }

                        HStack(spacing: 3) {
                            Button {
                                hub.move(item, to: .desk)
                                hub.selectAndPlay(item)
                                onClose()
                            } label: {
                                Image(systemName: "play.fill")
                                    .font(.system(size: 10, weight: .medium))
                                    .foregroundStyle(YYColor.t3)
                                    .frame(width: 24, height: 24)
                                    .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .help(String(localized: "立即收听"))

                            Button {
                                hub.move(item, to: .desk)
                            } label: {
                                PodcastGlyphView(glyph: .xmark, size: 11, lineWidth: 1.5)
                                    .foregroundStyle(YYColor.t3)
                                    .frame(width: 24, height: 24)
                                    .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .help(String(localized: "移出稍后听"))
                        }
                    }
                    .padding(.vertical, 11)
                }
            }
        }
    }

    @ViewBuilder
    private var voicesList: some View {
        VStack(spacing: 0) {
            ForEach(Array(voiceItems.enumerated()), id: \.element.id) { idx, item in
                if idx > 0 { Rectangle().fill(PodcastStyle.softDivider(scheme)).frame(height: 0.5) }
                let isPlaying = playingVoiceID == item.id
                let isRemembered = hub.isVoiceRemembered(item.id)
                HStack(alignment: .center, spacing: 10) {
                    PodcastShowMark(
                        title: item.name,
                        isStudio: item.isStudio,
                        size: 22
                    )
                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.name)
                            .font(.yy(size: 13, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)
                        Text("\(item.role) · \(item.sample)")
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(2)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    HStack(spacing: 8) {
                        PodcastPaperPill(
                            title: isPlaying ? String(localized: "试听中") : String(localized: "试听"),
                            accent: isPlaying,
                            height: 24,
                            horizontalPadding: 9
                        ) {
                            auditionVoice(item)
                        }

                        // `.vc-toggle`: 32×18pt tactile pill switch
                        Button {
                            withAnimation(YYMotion.quick) {
                                hub.setVoiceRemembered(item.id, !isRemembered)
                            }
                        } label: {
                            ZStack(alignment: isRemembered ? .trailing : .leading) {
                                Capsule()
                                    .fill(isRemembered ? tint.accent : YYColor.inset)
                                    .frame(width: 32, height: 18)
                                Circle()
                                    .fill(YYColor.card)
                                    .frame(width: 14, height: 14)
                                    .shadow(color: Color.black.opacity(0.18), radius: 1.5, y: 1)
                                    .padding(2)
                            }
                            .contentShape(Capsule())
                        }
                        .buttonStyle(.plain)
                        .help(String(localized: "跨期记住声纹"))
                    }
                    .fixedSize()
                }
                .padding(.vertical, 11)
            }
        }
    }

    private func auditionVoice(_ item: VoiceItem) {
        auditionTask?.cancel()
        hub.stopAudition()
        if playingVoiceID == item.id {
            playingVoiceID = nil
            return
        }
        playingVoiceID = item.id
        auditionTask = Task {
            await hub.auditionVoiceCard(id: item.id, text: item.auditionText, language: "zh-CN")
            if !Task.isCancelled, playingVoiceID == item.id {
                playingVoiceID = nil
            }
        }
    }

    @ViewBuilder
    private var localStatusList: some View {
        let dlPhase = LiveVoiceDownload.shared.phase
        VStack(alignment: .leading, spacing: 9) {
            statusCard(
                title: String(localized: "本机播客书库"),
                detail: String(localized: "已订阅 \(hub.channels.count) 档频道 · 书桌 \(hub.deskIssues.count) 期 · 原声书摘 \(hub.clips.count) 条")
            )
            VStack(alignment: .leading, spacing: 8) {
                HStack(spacing: 8) {
                    Text(LiveVoiceModel.isInstalled
                         ? String(localized: "本机语音引擎已就绪")
                         : String(localized: "系统朗读回退模式"))
                        .font(.yy(size: 13, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                    Spacer()
                    if !LiveVoiceModel.isInstalled {
                        switch dlPhase {
                        case .downloading(let frac):
                            Text("\(Int((frac * 100).rounded()))%")
                                .font(.yy(size: 11.5, weight: .medium))
                                .monospacedDigit()
                                .foregroundStyle(tint.accent)
                        case .idle, .failed:
                            PodcastPaperPill(
                                title: dlPhase == .failed ? String(localized: "重试下载") : String(localized: "下载本机声音"),
                                accent: true,
                                height: 22,
                                horizontalPadding: 9
                            ) {
                                LiveVoiceDownload.shared.start()
                            }
                        default:
                            EmptyView()
                        }
                    }
                }
                Text(LiveVoiceModel.isInstalled
                     ? String(localized: "书房电台与双语中文配音均使用本机离线神经声卡合成，不经过云端。")
                     : String(localized: "未安装本机声音包时，书房电台自动使用系统双人语音朗读；下载后自动切换为本机神经声卡。"))
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t3)
                    .lineSpacing(3)
            }
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 11, style: .continuous))
            .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))

            statusCard(
                title: String(localized: "本地优先 · 无账号"),
                detail: String(localized: "不设推荐信息流、不弹未读红点；书摘、声音卡与私人电台全部保存在你的设备里。")
            )
        }
    }

    private func statusCard(title: String, detail: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title)
                .font(.yy(size: 13, weight: .medium))
                .foregroundStyle(YYColor.t1)
            Text(detail)
                .font(.yy(size: 11.5))
                .foregroundStyle(YYColor.t3)
                .lineSpacing(3)
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(PodcastStyle.leafSurface(scheme), in: RoundedRectangle(cornerRadius: 11, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 11, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
    }
}

private nonisolated struct PodcastDashedRule: Shape {
    func path(in rect: CGRect) -> Path {
        var p = Path()
        p.move(to: CGPoint(x: rect.minX, y: rect.midY))
        p.addLine(to: CGPoint(x: rect.maxX, y: rect.midY))
        return p
    }
}
