#if os(iOS)
import SwiftUI
import YuyueCore

/// Where the phone's 书架 is: 书 · 进度 · 播客 · 有声 · 笔记, or one of your 合集. The title names it;
/// a tap on the title (or, scrolled, on the chip at the top left) opens the panel to go to another.
enum ShelfPlace: Hashable, Sendable {
    case books, progress, podcasts, audio, notes
    case collection(String)

    static let prefKey = "shelf.lastPlace"

    /// The shelf's four, in the panel's order (matching Mac's 书 · 进度 · 播客 · 有声).
    static let kinds: [ShelfPlace] = [.books, .progress, .podcasts, .audio]

    var storageKey: String {
        switch self {
        case .books: "books"
        case .progress: "progress"
        case .podcasts: "podcasts"
        case .audio: "audio"
        case .notes: "notes"
        case .collection(let id): "collection:\(id)"
        }
    }

    static func parse(_ raw: String?) -> ShelfPlace {
        guard let raw, !raw.isEmpty else { return .books }
        return switch raw {
        case "books": .books
        case "progress": .progress
        case "podcasts": .podcasts
        case "audio": .audio
        case "notes": .notes
        default:
            if raw.hasPrefix("collection:"), raw.count > 11 {
                .collection(String(raw.dropFirst(11)))
            } else {
                .books
            }
        }
    }

    static func resolveValid(
        _ place: ShelfPlace,
        foldedPlaces: [ShelfPlace],
        collectionIDs: Set<String>
    ) -> ShelfPlace {
        switch place {
        case .collection(let id):
            collectionIDs.contains(id) ? place : .books
        default:
            foldedPlaces.contains(place) ? place : .books
        }
    }
}

/// The buttons at the top right of a tab that draws its own title: icons in one white capsule.
struct TopButtons<Content: View>: View {
    @ViewBuilder let content: Content
    @AppStorage(EInk.key) private var eink = false

    var body: some View {
        HStack(spacing: 2) { content }
            .labelStyle(.iconOnly)
            .buttonStyle(YYPressStyle(scale: 0.92))
            .font(.system(size: 18, weight: .regular))
            .foregroundStyle(YYColor.t1)
            .padding(.horizontal, 6)
            .frame(height: 40)
            .background(YYColor.card, in: .capsule)
            .overlay { Capsule().strokeBorder(YYColor.line, lineWidth: 0.5) }
            .shadow(color: .black.opacity(eink ? 0 : 0.05), radius: 3, y: 1)
    }
}

/// One of `TopButtons`: a 40 × 40 place to press.
struct TopButton: View {
    let title: LocalizedStringKey
    var symbol: String? = nil
    var asset: String? = nil
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Group {
                if let asset {
                    Image(asset)
                        .resizable()
                        .renderingMode(.template)
                        .scaledToFit()
                        .frame(width: 21, height: 21)
                } else if let symbol {
                    Image(systemName: symbol)
                }
            }
            .frame(width: 40, height: 40)
            .contentShape(.rect)
        }
        .accessibilityLabel(Text(title))
    }
}

/// The shelf's title: the place's mark, its name in 宋体, and ⌄. The page that owns it adds the tap.
struct ShelfPlaceTitle: View {
    var symbol: String
    var name: String

    var body: some View {
        HStack(spacing: 11) {
            PlaceMark(symbol: symbol, size: 40, bare: true)
            HStack(spacing: 6) {
                Text(name)
                    .font(YYUI.serif(30, .medium))
                    .tracking(name.contains { $0.isASCII } || name.count > 4 ? 0 : 1)
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                    .allowsTightening(true)
                Image(systemName: "chevron.down")
                    .font(.system(size: 13, weight: .bold))
                    .foregroundStyle(YYColor.t3)
                    .padding(.top, 3)
            }
        }
        .frame(height: 40)
    }
}

/// A tab's own title, as the shelf has it: the mark and the name in 宋体 (笔记, 我的).
struct PageTitle: View {
    let symbol: String
    let title: LocalizedStringKey

    var body: some View {
        HStack(spacing: 11) {
            PlaceMark(symbol: symbol, size: 40, bare: true)
            Text(title)
                .font(YYUI.serif(30, .medium))
                .tracking(1)
                .foregroundStyle(YYColor.t1)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.isHeader)
    }
}

/// Between the phone's root and its shelf: 书架 tapped again while you're on it.
@Observable
final class PhoneChrome {
    /// The shelf's places stand up over the 书架 tab.
    var switching = false
    /// Incremented when tapping an already-selected tab to pop its stack back to root.
    var notesPop = 0
    var mePop = 0
    /// Where the shelf is, for the 书架 tab to wear: its name, and its symbol (nil: 书架's own).
    var shelfName: String?
    var shelfSymbol: String?
    /// Horizontal alignment of the pop-up PlaceStack above the 书架 tab wherever the user placed it.
    var shelfStackAlignment: SwiftUI.Alignment = .bottomLeading
    /// Whether 书架 currently has any folded sub-places or collections to switch between.
    var shelfHasSwitcher = true
    /// Whether the shelf tab is currently showing 播客 / 笔记 (for semantic `$screen` tracking).
    var onPodcasts = false
    var onNotes = false

    static var motion: Animation? { YYMotion.reduce ? nil : .spring(duration: 0.26, bounce: 0) }
    static var dismissMotion: Animation? { YYMotion.reduce ? nil : YYMotion.exit }
}

/// A place's mark: a square of warm paper with one thin line drawing on a horizon — round in the
/// chip, smaller in the panel's rows.
struct PlaceMark: View {
    let symbol: String
    var size: CGFloat = 56
    var round = false
    /// The titles' own: the line drawing alone, no paper square, one horizon.
    var bare = false

    static let paper = Color.yyDynamic(light: YYRGBA(243, 240, 232), dark: YYRGBA(43, 41, 37))
    static let ink = Color.yyDynamic(light: YYRGBA(43, 43, 43), dark: YYRGBA(233, 229, 220))

    var body: some View {
        if bare { line } else { square }
    }

    private var line: some View {
        VStack(spacing: size * 0.08) {
            glyph(sfSize: size * 0.5, assetSize: size * 0.62)
            Capsule().frame(width: size * 0.78, height: 1)
        }
        .foregroundStyle(Self.ink)
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    private var square: some View {
        let shape = RoundedRectangle(cornerRadius: round ? size / 2 : size * 0.25, style: .continuous)
        return ZStack {
            shape.fill(Self.paper)
            shape.strokeBorder(Self.ink.opacity(0.07), lineWidth: 0.5)
            VStack(spacing: size * 0.055) {
                glyph(sfSize: size * 0.34, assetSize: size * 0.42)
                VStack(spacing: size * 0.05) {
                    Capsule().frame(width: size * 0.6, height: max(0.7, size * 0.015))
                    Capsule().frame(width: size * 0.38, height: max(0.7, size * 0.015)).opacity(0.75)
                }
            }
            .foregroundStyle(Self.ink)
            .offset(y: size * 0.03)
        }
        .frame(width: size, height: size)
        .accessibilityHidden(true)
    }

    @ViewBuilder private func glyph(sfSize: CGFloat, assetSize: CGFloat) -> some View {
        if symbol.hasPrefix("asset.") {
            Image(String(symbol.dropFirst(6)))
                .resizable()
                .renderingMode(.template)
                .scaledToFit()
                .frame(width: assetSize, height: assetSize)
        } else {
            Image(systemName: symbol)
                .font(.system(size: sfSize, weight: .light))
        }
    }
}

/// What the title, the chip and the panel need to know of one place.
struct PlaceInfo: Identifiable {
    let place: ShelfPlace
    let name: String
    let symbol: String
    /// The panel's row: 38 本 / 在读 3 / 2 集新.
    let short: String
    /// Two lines at the panel's head; one line, joined, under the title.
    let meta: [String]
    /// 播客 with episodes not heard: a small red dot before `short`.
    var fresh = false
    var collection: BookCollection?

    var id: ShelfPlace { place }
}

/// The panel that grows out of the title (or the chip): the place's mark and name where the title
/// was, then a row for each place and your 合集, and 新建合集 at the end.
struct PlacePanel: View {
    @State private var confirming: YYConfirm?
    @AppStorage(EInk.key) private var eink = false
    let current: PlaceInfo
    let kinds: [PlaceInfo]
    let collections: [PlaceInfo]
    let go: (ShelfPlace) -> Void
    let newCollection: () -> Void
    let rename: (BookCollection) -> Void
    let delete: (BookCollection) -> Void
    let library: Library

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 13) {
                PlaceMark(symbol: current.symbol)
                Text(current.name)
                    .font(YYUI.serif(31, .medium))
                    .tracking(current.name.contains { $0.isASCII } || current.name.count > 4 ? 0 : 1)
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .minimumScaleFactor(0.65)
                    .allowsTightening(true)
                Spacer(minLength: 8)
                VStack(alignment: .trailing, spacing: 2) {
                    ForEach(current.meta, id: \.self) { Text($0) }
                }
                .font(.yy(size: 13.5))
                .monospacedDigit()
                .foregroundStyle(YYColor.t3)
                .padding(.trailing, 4)
            }
            .padding(.horizontal, 6)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
                .padding(.top, 14)
                .padding(.bottom, 8)
            ScrollView {
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(kinds) { row($0) }
                    Text("合集")
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 8)
                        .padding(.top, 12)
                        .padding(.bottom, 4)
                    ForEach(collections) { info in
                        collectionRow(info)
                    }
                    Button(action: newCollection) {
                        HStack(spacing: 12) {
                            RoundedRectangle(cornerRadius: 10, style: .continuous)
                                .strokeBorder(YYColor.line, lineWidth: 1)
                                .frame(width: 36, height: 36)
                                .overlay { Image(systemName: "plus").font(.system(size: 15, weight: .regular)) }
                            Text("新建合集").font(.yy(size: 16))
                            Spacer()
                        }
                        .foregroundStyle(YYColor.t3)
                        .padding(.leading, 6)
                        .frame(minHeight: 52)
                        .contentShape(.rect)
                    }
                    .buttonStyle(PlaceRowStyle(on: false))
                }
            }
            .scrollBounceBehavior(.basedOnSize)
            .frame(maxHeight: 470)
            .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.top, 15)
        .padding(.horizontal, 10)
        .padding(.bottom, 10)
        .background {
            RoundedRectangle(cornerRadius: 28, style: .continuous)
                .fill(YYColor.card)
                .shadow(color: .black.opacity(eink ? 0 : 0.16), radius: 30, y: 18)
                .shadow(color: .black.opacity(eink ? 0 : 0.04), radius: 4, y: 1)
        }
        .overlay { RoundedRectangle(cornerRadius: 28, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5) }
        .yyConfirm($confirming)
    }

    @ViewBuilder private func collectionMenuItems(_ c: BookCollection) -> some View {
        Button("重命名", systemImage: "pencil") { rename(c) }
        CollectionIconMenu(collection: c, library: library)
        Button("删除…", systemImage: "trash", role: .destructive) {
            confirming = YYConfirm(
                title: String(localized: "删除合集“\(c.name)”？"),
                message: String(localized: "这个合集删掉，里面的书还在书架上。"),
                button: String(localized: "删除")
            ) { delete(c) }
        }
    }

    @ViewBuilder private func collectionRow(_ info: PlaceInfo) -> some View {
        if let c = info.collection {
            let on = info.place == current.place
            HStack(spacing: 0) {
                Button { go(info.place) } label: {
                    rowContent(info, on: on, trailing: 4)
                }
                .buttonStyle(PlaceRowStyle(on: false))
                .accessibilityAddTraits(on ? .isSelected : [])
                Menu {
                    collectionMenuItems(c)
                } label: {
                    Image(systemName: "ellipsis")
                        .font(.system(size: 14, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 36, height: 44)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .padding(.trailing, 4)
                .accessibilityLabel(String(localized: "管理合集“\(c.name)”"))
            }
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(YYColor.inset.opacity(on ? 0.75 : 0))
            }
            .contextMenu { collectionMenuItems(c) }
        } else {
            row(info)
        }
    }

    private func row(_ info: PlaceInfo) -> some View {
        let on = info.place == current.place
        return Button { go(info.place) } label: {
            rowContent(info, on: on, trailing: 12)
        }
        .buttonStyle(PlaceRowStyle(on: on))
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func rowContent(_ info: PlaceInfo, on: Bool, trailing: CGFloat) -> some View {
        HStack(spacing: 12) {
            PlaceMark(symbol: info.symbol, size: 36)
            Text(info.name)
                .font(.yy(size: 16, weight: on ? .semibold : .medium))
                .foregroundStyle(YYColor.t1)
                .lineLimit(1)
            Spacer(minLength: 8)
            HStack(spacing: 6) {
                if info.fresh { Circle().fill(YYColor.danger).frame(width: 6, height: 6) }
                Text(info.short).monospacedDigit()
            }
            .font(.yy(size: 13))
            .foregroundStyle(info.fresh ? YYColor.t2 : YYColor.t3)
            Image(systemName: "checkmark")
                .font(.system(size: 13, weight: .bold))
                .foregroundStyle(YYColor.t1)
                .opacity(on ? 1 : 0)
                .frame(width: 16)
        }
        .padding(.leading, 6)
        .padding(.trailing, trailing)
        .frame(minHeight: 52)
        .contentShape(.rect)
    }
}

/// A row in the panel: a light grey ground on the place you're in, and while pressed.
private struct PlaceRowStyle: ButtonStyle {
    let on: Bool
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .background {
                RoundedRectangle(cornerRadius: 14, style: .continuous)
                    .fill(YYColor.inset.opacity(configuration.isPressed ? 1 : on ? 0.75 : 0))
            }
            .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
    }
}

/// 书架 tapped again: the places stand up in a column over the tab, nearest it the one you're
/// on, and your 合集 above them. A tap goes there; anywhere else, or 书架 again, puts it away.
struct PlaceStack: View {
    let current: ShelfPlace
    let kinds: [PlaceInfo]
    let collections: [PlaceInfo]
    let go: (ShelfPlace) -> Void
    @AppStorage(EInk.key) private var eink = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if !collections.isEmpty {
                ScrollView {
                    VStack(spacing: 0) { ForEach(collections) { row($0) } }
                }
                .scrollBounceBehavior(.basedOnSize)
                .frame(maxHeight: 52 * 3)
                .fixedSize(horizontal: false, vertical: true)
                Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 10).padding(.vertical, 4)
            }
            ForEach(kinds.reversed()) { row($0) }
        }
        .padding(6)
        .frame(width: 236)
        .background {
            RoundedRectangle(cornerRadius: 24, style: .continuous)
                .fill(YYColor.card)
                .shadow(color: .black.opacity(eink ? 0 : 0.16), radius: 24, y: 12)
                .shadow(color: .black.opacity(eink ? 0 : 0.04), radius: 3, y: 1)
        }
        .overlay { RoundedRectangle(cornerRadius: 24, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5) }
    }

    private func row(_ info: PlaceInfo) -> some View {
        let on = info.place == current
        return Button { go(info.place) } label: {
            HStack(spacing: 11) {
                PlaceMark(symbol: info.symbol, size: 34)
                Text(info.name)
                    .font(.yy(size: 16, weight: on ? .semibold : .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                Spacer(minLength: 6)
                HStack(spacing: 5) {
                    if info.fresh { Circle().fill(YYColor.danger).frame(width: 6, height: 6) }
                    Text(info.short).monospacedDigit()
                }
                .font(.yy(size: 12.5))
                .foregroundStyle(info.fresh ? YYColor.t2 : YYColor.t3)
            }
            .padding(.leading, 6)
            .padding(.trailing, 10)
            .frame(minHeight: 50)
            .contentShape(.rect)
        }
        .buttonStyle(PlaceRowStyle(on: on))
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

/// Scrolled down, the title's place in a white capsule at the top left: the mark, the name and a
/// line under it, and ⌃⌄ in a small grey circle. A tap opens the panel from here.
struct PlaceChip: View {
    let info: PlaceInfo
    let open: () -> Void
    @AppStorage(EInk.key) private var eink = false

    var body: some View {
        Button(action: open) {
            HStack(spacing: 10) {
                PlaceMark(symbol: info.symbol, size: 33, round: true)
                VStack(alignment: .leading, spacing: 1) {
                    Text(info.name).font(YYUI.serif(16, .medium)).foregroundStyle(YYColor.t1)
                    Text(info.short).font(.yy(size: 11)).foregroundStyle(YYColor.t3).monospacedDigit()
                }
                .lineLimit(1)
                Image(systemName: "chevron.up.chevron.down")
                    .font(.system(size: 8.5, weight: .bold))
                    .foregroundStyle(YYColor.t2)
                    .frame(width: 20, height: 20)
                    .background(YYColor.inset, in: .circle)
                    .padding(.leading, 2)
            }
            .padding(.leading, 6)
            .padding(.trailing, 11)
            .padding(.vertical, 6)
            .frame(minHeight: 45)
            .background(YYColor.card, in: .capsule)
            .overlay { Capsule().strokeBorder(YYColor.line, lineWidth: 0.5) }
            .shadow(color: .black.opacity(eink ? 0 : 0.1), radius: 9, y: 4)
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
        .accessibilityLabel("\(info.name)，换一处看")
    }
}
#endif
