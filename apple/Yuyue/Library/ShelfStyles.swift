import SwiftUI
import YuyueCore

/// Main bookshelf presentation style, mirrored on Android (`shelf.viewStyle`).
nonisolated enum ShelfViewStyle: String, CaseIterable, Hashable, Sendable {
    case grid
    case spine

    static func of(_ raw: String?) -> ShelfViewStyle {
        raw.flatMap(ShelfViewStyle.init(rawValue:)) ?? .grid
    }
}

enum ShelfStylePrefs {
    static let viewKey = "shelf.viewStyle"
    static let woodKey = "shelf.woodLedge"
}

nonisolated struct SpineMetrics: Equatable, Sendable {
    let width: CGFloat
    let height: CGFloat

    /// Deterministic physical spine width & height tiers per book (mirrors Android's formula).
    static func of(_ book: BookSummary, scale: CGFloat) -> SpineMetrics {
        let k = min(max(scale, 0.85), 1.25)
        var h: UInt32 = 2166136261
        for scalar in book.id.unicodeScalars {
            h = (h ^ scalar.value) &* 16777619
        }
        let mins = book.readMinutes + book.listenMinutes
        let wTier = min(3, Int(h & 3) + (mins > 180 ? 1 : 0))
        let hTier = Int((h >> 4) & 3)
        let baseW: CGFloat = switch wTier {
        case 0: 34
        case 1: 40
        case 2: 46
        default: 52
        }
        let baseH: CGFloat = switch hTier {
        case 0: 174
        case 1: 182
        case 2: 190
        default: 196
        }
        return SpineMetrics(width: baseW * k, height: baseH * k)
    }

    static func packRows(_ books: [BookSummary], maxWidth: CGFloat, scale: CGFloat, gap: CGFloat = 5) -> [[BookSummary]] {
        guard !books.isEmpty else { return [] }
        let limit = max(160, maxWidth)
        var rows: [[BookSummary]] = []
        var current: [BookSummary] = []
        var used: CGFloat = 0
        for b in books {
            let w = SpineMetrics.of(b, scale: scale).width
            let add = current.isEmpty ? w : (w + gap)
            if !current.isEmpty, used + add > limit {
                rows.append(current)
                current = [b]
                used = w
            } else {
                current.append(b)
                used += add
            }
        }
        if !current.isEmpty { rows.append(current) }
        return rows
    }
}

/// Light oak (or muted dark walnut in dark mode) shelf ledge drawn purely in SwiftUI; hidden in E-Ink.
struct ShelfWoodLedge: View {
    @AppStorage(EInk.key) private var eink = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        if !eink {
            let dark = scheme == .dark
            VStack(spacing: 0) {
                RoundedRectangle(cornerRadius: 3, style: .continuous)
                    .fill(
                        LinearGradient(
                            stops: dark
                                ? [
                                    .init(color: Color(red: 0x4B / 255, green: 0x3F / 255, blue: 0x34 / 255), location: 0.0),
                                    .init(color: Color(red: 0x3C / 255, green: 0x32 / 255, blue: 0x29 / 255), location: 0.32),
                                    .init(color: Color(red: 0x2F / 255, green: 0x27 / 255, blue: 0x20 / 255), location: 0.78),
                                    .init(color: Color(red: 0x23 / 255, green: 0x1D / 255, blue: 0x18 / 255), location: 1.0),
                                ]
                                : [
                                    .init(color: Color(red: 0xE8 / 255, green: 0xD8 / 255, blue: 0xBE / 255), location: 0.0),
                                    .init(color: Color(red: 0xD9 / 255, green: 0xC3 / 255, blue: 0xA1 / 255), location: 0.32),
                                    .init(color: Color(red: 0xC8 / 255, green: 0xAC / 255, blue: 0x84 / 255), location: 0.78),
                                    .init(color: Color(red: 0xB5 / 255, green: 0x96 / 255, blue: 0x6C / 255), location: 1.0),
                                ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                    )
                    .overlay(alignment: .top) {
                        Rectangle()
                            .fill(Color.white.opacity(dark ? 0.08 : 0.42))
                            .frame(height: 0.75)
                            .padding(.horizontal, 3)
                    }
                    .frame(height: 8)
                Rectangle()
                    .fill(
                        LinearGradient(
                            stops: [
                                .init(color: dark ? Color.black.opacity(0.34) : Color(red: 0x4A / 255, green: 0x37 / 255, blue: 0x20 / 255).opacity(0.15), location: 0),
                                .init(color: dark ? Color.black.opacity(0.12) : Color(red: 0x4A / 255, green: 0x37 / 255, blue: 0x20 / 255).opacity(0.055), location: 0.55),
                                .init(color: .clear, location: 1),
                            ],
                            startPoint: .top,
                            endPoint: .bottom
                        )
                    )
                    .frame(height: 10)
                    .padding(.horizontal, 4)
            }
            .allowsHitTesting(false)
            .accessibilityHidden(true)
        }
    }
}

/// Subtle grounding shadow beneath a spine row when wood ledge is off.
struct SpineGroundShadow: View {
    @AppStorage(EInk.key) private var eink = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        if eink {
            Rectangle().fill(YYColor.line).frame(height: 1)
        } else {
            Rectangle()
                .fill(
                    LinearGradient(
                        colors: [
                            scheme == .dark
                                ? Color.black.opacity(0.24)
                                : Color(red: 0x2C / 255, green: 0x24 / 255, blue: 0x1B / 255).opacity(0.10),
                            .clear,
                        ],
                        startPoint: .top,
                        endPoint: .bottom
                    )
                )
                .frame(height: 8)
                .padding(.horizontal, 4)
                .allowsHitTesting(false)
                .accessibilityHidden(true)
        }
    }
}

#if os(iOS)
/// One shelf row of vertical book spines on phone/iPad, aligned along their bottom edge.
struct PhoneSpineShelfRow<Menu: View>: View {
    let books: [BookSummary]
    let scale: CGFloat
    let showProgress: Bool
    let woodLedge: Bool
    let selecting: Bool
    let selected: Set<String>
    let tap: (BookSummary) -> Void
    @ViewBuilder let menu: (BookSummary) -> Menu
    @AppStorage(EInk.key) private var eink = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .bottom, spacing: 5) {
                ForEach(books) { book in
                    spineCell(book)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 2)
            if woodLedge {
                ShelfWoodLedge()
            } else {
                SpineGroundShadow()
            }
        }
    }

    private func spineCell(_ book: BookSummary) -> some View {
        let m = SpineMetrics.of(book, scale: scale)
        let chosen = selected.contains(book.id)
        let shape = UnevenRoundedRectangle(topLeadingRadius: 5, bottomLeadingRadius: 2, bottomTrailingRadius: 2, topTrailingRadius: 5)
        let note = (showProgress && book.progress > 0) ? book.percent : nil
        return Button { tap(book) } label: {
            ZStack(alignment: .bottom) {
                FinishedStrip(book: book, size: CGSize(width: m.width, height: m.height), note: note)
                if !eink {
                    LinearGradient(
                        stops: [
                            .init(color: .white.opacity(0.16), location: 0.0),
                            .init(color: .clear, location: 0.14),
                            .init(color: .clear, location: 0.84),
                            .init(color: .black.opacity(0.18), location: 1.0),
                        ],
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                    .allowsHitTesting(false)
                }
                if selecting {
                    Image(systemName: chosen ? "checkmark.circle.fill" : "circle")
                        .font(.yy(size: 16, weight: .semibold))
                        .foregroundStyle(chosen ? YYTint.standard.accent : .white)
                        .background(Circle().fill(chosen ? Color.white : Color.black.opacity(0.3)))
                        .padding(.bottom, 6)
                } else if book.format == .audio {
                    Image(systemName: "headphones")
                        .font(.yy(size: 9, weight: .semibold))
                        .foregroundStyle(.white)
                        .frame(width: 16, height: 16)
                        .background(Circle().fill(Color.black.opacity(0.32)))
                        .padding(.bottom, 6)
                }
            }
            .frame(width: m.width, height: m.height)
            .clipShape(shape)
            .overlay(
                shape.strokeBorder(
                    chosen ? YYTint.standard.accent : (scheme == .dark ? Color.white.opacity(0.08) : Color.black.opacity(0.08)),
                    lineWidth: chosen ? 1.5 : 0.5
                )
            )
            .opacity(book.available ? 1 : 0.5)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
        .contextMenu { if !selecting { menu(book) } } preview: { BookPreview(book: book) }
    }
}
#endif
