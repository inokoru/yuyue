#if os(iOS)
import SwiftUI
import YuyueCore

/// 读过 on the phone: a row a year, a book a tall narrow strip — a slice down the middle of its
/// cover blurred into a field of colour, the title written down it, the day it was finished on top.
/// Touching one opens the book; holding it, the same menu as a card's.
struct PhoneFinishedStrips: View {
    let books: [BookSummary]
    let library: Library
    let show: (String) -> Void
    let read: (BookSummary) -> Void
    let pick: (BookSummary) -> Void
    let delete: (BookSummary) -> Void

    /// The year a book was finished in; one it doesn't know, the last time it was opened.
    private static func year(_ b: BookSummary) -> Int {
        Calendar.current.component(.year, from: Notes.date(b.finishedAt ?? b.openedAt ?? b.addedAt))
    }

    private var years: [(year: Int, books: [BookSummary])] {
        var order: [Int] = []
        var by: [Int: [BookSummary]] = [:]
        for b in books {
            let y = Self.year(b)
            if by[y] == nil { order.append(y) }
            by[y, default: []].append(b)
        }
        return order.sorted(by: >).map { ($0, by[$0] ?? []) }
    }

    var body: some View {
        LazyVStack(alignment: .leading, spacing: 0) {
            ForEach(years, id: \.year) { group in
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(verbatim: String(group.year))
                        .font(YYUI.serif(19, .medium))
                        .foregroundStyle(YYColor.t1)
                    Text(String(localized: "\(group.books.count) 本"))
                        .font(.footnote)
                        .foregroundStyle(YYColor.t3)
                    Spacer()
                    let minutes = group.books.reduce(0) { $0 + $1.readMinutes + $1.listenMinutes }
                    if minutes > 0 {
                        Text(String(localized: "约 \(BookSummary.hours(minutes))"))
                            .font(.footnote)
                            .foregroundStyle(YYColor.t3)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.top, 22)
                .padding(.bottom, 10)
                ScrollView(.horizontal, showsIndicators: false) {
                    LazyHStack(spacing: 5) {
                        ForEach(group.books) { book in
                            Button { show(book.id) } label: { FinishedStrip(book: book) }
                                .buttonStyle(YYPressStyle(scale: 0.97))
                                .contextMenu {
                                    Button(String(localized: "翻开"), systemImage: "book") { read(book) }
                                    Divider()
                                    StatusMenuItems(book: book, library: library, pickCollection: { pick(book) }, delete: { delete(book) })
                                }
                        }
                    }
                    .padding(.horizontal, 20)
                }
                .scrollClipDisabled()
            }
        }
        .animation(YYMotion.layout, value: books.map(\.id))
    }
}

/// One book on 读过 — and, narrower, a book on 在读 that isn't the one open.
struct FinishedStrip: View {
    let book: BookSummary
    static let size = CGSize(width: 44, height: 232)
    private let size: CGSize
    /// On top: the day it was finished (读过), or how far (在读).
    private let note: String?
    @State private var image: CGImage?
    @AppStorage(EInk.key) private var eink = false
    @AppStorage(EInk.grayscaleKey) private var einkGray = true
    private var tones = CoverTones.shared

    init(book: BookSummary, size: CGSize = FinishedStrip.size, note: String? = nil) {
        self.book = book
        self.size = size
        self.note = note ?? book.finishedAt.map { Notes.date($0).formatted(.dateTime.month(.defaultDigits).day()).replacingOccurrences(of: "/", with: ".") }
    }

    private static let stone = Color.yyDynamic(light: YYRGBA(235, 230, 221), dark: YYRGBA(58, 53, 47))
    private static let stoneDeep = Color.yyDynamic(light: YYRGBA(214, 206, 193), dark: YYRGBA(40, 36, 32))
    private static let darkInk = Color(red: 40 / 255, green: 32 / 255, blue: 24 / 255).opacity(0.82)

    /// Light words on a dark cover, dark ones on a light cover or none.
    private var lightInk: Bool {
        guard image != nil, let t = tones.tones[book.id] else { return false }
        return t.luminance < 0.62
    }

    private var ink: Color { image == nil ? YYColor.t1 : (lightInk ? .white.opacity(0.92) : Self.darkInk) }

    var body: some View {
        ZStack(alignment: .top) {
            field
            VStack(spacing: 10) {
                if let note {
                    Text(verbatim: note)
                        .font(YYUI.serif(10))
                        .opacity(0.75)
                }
                title
            }
            .foregroundStyle(ink)
            .shadow(color: lightInk ? .black.opacity(0.25) : .white.opacity(image == nil ? 0 : 0.3), radius: 4)
            .padding(.top, 12)
        }
        .frame(width: size.width, height: size.height)
        .clipShape(.rect(cornerRadius: 9))
        .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(.black.opacity(0.06), lineWidth: 0.5))
        .grayscale(eink && einkGray ? 1 : 0)
        .contentShape(.rect(cornerRadius: 9))
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(book.title)
        .task(id: book.coverPath) {
            guard let cg = await Thumbnail.load(book.coverPath, maxPixels: 300) else { return }
            // The stone gives way to the cover, not in a blink.
            withAnimation(YYMotion.standard) { image = cg }
            if let t = await Self.measureTone(cg) {
                tones.set(t, for: book.id)
            }
        }
    }

    /// The cover's middle, blurred into colour; warm stone when there's no cover.
    @ViewBuilder private var field: some View {
        if let image {
            Image(decorative: image, scale: 1)
                .resizable()
                .scaledToFill()
                .frame(width: size.width, height: size.height)
                .scaleEffect(1.25)
                .blur(radius: 9, opaque: true)
                .saturation(1.1)
        } else {
            LinearGradient(colors: [Self.stone, Self.stoneDeep], startPoint: .top, endPoint: .bottom)
        }
    }

    /// Down the strip: Chinese and Japanese a character under the next; other titles turned on their side.
    @ViewBuilder private var title: some View {
        let t = book.title.trimmingCharacters(in: .whitespaces)
        if t.unicodeScalars.contains(where: { (0x3000...0x9FFF).contains($0.value) || (0xF900...0xFAFF).contains($0.value) }) {
            let chars = Array(t.filter { $0 != " " })
            // As many as fit down the strip (10 on 读过's), the last turned into ︙.
            let fit = Int((size.height - 52) / 17.5)
            let shown = chars.count > fit ? Array(chars.prefix(fit - 1)) + ["︙"] : chars
            VStack(spacing: 4) {
                ForEach(Array(shown.enumerated()), id: \.offset) { _, c in
                    Text(String(c)).font(YYUI.serif(13.5))
                }
            }
        } else {
            Text(t)
                .font(YYUI.serif(13))
                .tracking(0.5)
                .lineLimit(1)
                .frame(width: size.height - 52, alignment: .leading)
                .rotationEffect(.degrees(90))
                .frame(width: size.width, height: size.height - 52)
        }
    }

    @concurrent nonisolated private static func measureTone(_ image: CGImage) async -> CoverTones.Tone? {
        CoverTones.measure(image)
    }
}
#endif
