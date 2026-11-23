#if os(iOS)
import SwiftUI
import YuyueCore

/// 在读 on the phone: the books in a row of narrow strips, the one picked opened out to its cover;
/// what's left of the row stands empty, fainter to the right. Under them, as the shelf's title,
/// a thin drawing on a line beside the name in 宋体 — the line running on is how far it's read.
/// One book: its cover, the words beside it. None: the empty row, and where one comes from.
struct PhoneReadingShelf<Menu: View>: View {
    let books: [BookSummary]
    let library: Library
    let open: (BookSummary, Library.OpenIntent?) -> Void
    @ViewBuilder let menu: (BookSummary) -> Menu

    @State private var picked: String?
    @State private var width: CGFloat = 0

    static var cover: CGSize { CGSize(width: 136, height: 204) }
    static var strip: CGSize { CGSize(width: 42, height: 204) }
    static var gap: CGFloat { 6 }

    private var current: BookSummary? {
        books.first { $0.id == picked } ?? books.first
    }

    var body: some View {
        Group {
            if books.isEmpty {
                VStack(alignment: .leading, spacing: 20) {
                    row
                    ReadingLine(symbol: "book", title: String(localized: "还没有在读的书"),
                                meta: String(localized: "在下面点一本，它就会立在这里"), progress: 0)
                }
            } else if books.count == 1, let book = books.first {
                one(book)
            } else if let book = current {
                VStack(alignment: .leading, spacing: 20) {
                    row
                    line(book)
                }
            }
        }
        .onGeometryChange(for: CGFloat.self, of: \.size.width) { width = $0 }
    }

    /// The strips, then as many empty ones as the row has room for.
    private var row: some View {
        let used = books.isEmpty ? 0 : Self.cover.width + CGFloat(books.count - 1) * (Self.strip.width + Self.gap)
        let empty = max(0, Int((width - used + (books.isEmpty ? Self.gap : 0)) / (Self.strip.width + Self.gap)))
        let activeIndex = books.firstIndex { $0.id == current?.id } ?? 0
        return ScrollView(.horizontal) {
            HStack(spacing: Self.gap) {
                if !books.isEmpty {
                    ZStack(alignment: .topLeading) {
                        ForEach(Array(books.enumerated()), id: \.element.id) { idx, book in
                            strip(book, index: idx, activeIndex: activeIndex)
                        }
                    }
                    .frame(width: used, height: Self.cover.height, alignment: .topLeading)
                }
                ForEach(0..<empty, id: \.self) { i in
                    RoundedRectangle(cornerRadius: 9)
                        .fill(Self.vacant)
                        .frame(width: Self.strip.width, height: Self.strip.height)
                        .opacity((books.isEmpty ? 1 : 0.75) * (1 - Double(i) / (Double(empty) + 0.6)))
                        .accessibilityHidden(true)
                }
            }
        }
        .scrollIndicators(.never)
        .scrollDisabled(used <= width)
        .scrollClipDisabled()
    }

    private func strip(_ book: BookSummary, index: Int, activeIndex: Int) -> some View {
        let on = index == activeIndex
        let visualW = on ? Self.cover.width : Self.strip.width
        let x = CGFloat(index) * (Self.strip.width + Self.gap) + (index > activeIndex ? Self.cover.width - Self.strip.width : 0)
        let mask = StripMask(width: visualW)
        return Button {
            if on { open(book, nil) } else {
                withAnimation(YYMotion.reduce ? nil : .spring(duration: 0.32, bounce: 0)) { picked = book.id }
            }
        } label: {
            ZStack(alignment: .topLeading) {
                FinishedStrip(book: book, size: Self.strip, note: book.percent)
                    .frame(width: Self.cover.width, alignment: .leading)
                    .opacity(on ? 0 : (book.available ? 1 : 0.55))
                BookCover(book: book, bleed: true)
                    .opacity(book.available ? 1 : 0.45)
                    .overlay(alignment: .bottomTrailing) {
                        if !book.available { unavailableBadge }
                    }
                    .frame(width: Self.cover.width, height: Self.cover.height)
                    .opacity(on ? 1 : 0)
            }
            .frame(width: Self.cover.width, height: Self.cover.height, alignment: .leading)
            .clipShape(mask)
            .overlay(mask.stroke(.black.opacity(0.06), lineWidth: 0.5))
        }
        .buttonStyle(StripPressStyle(anchorX: visualW / (2 * Self.cover.width)))
        .contentShape(.interaction, mask)
        .contentShape(.contextMenuPreview, mask)
        .contextMenu { menu(book) } preview: { BookPreview(book: book) }
        .offset(x: x)
        .zIndex(on ? 1 : 0)
        .accessibilityLabel(on ? String(localized: "接着读《\(book.title)》") : book.title)
    }

    private nonisolated struct StripMask: Shape {
        var width: CGFloat
        nonisolated var animatableData: CGFloat {
            get { width }
            set { width = newValue }
        }
        func path(in rect: CGRect) -> Path {
            Path(roundedRect: CGRect(x: 0, y: 0, width: width, height: rect.height), cornerRadius: 9)
        }
    }

    private struct StripPressStyle: ButtonStyle {
        var anchorX: CGFloat
        func makeBody(configuration: Configuration) -> some View {
            configuration.label
                .scaleEffect(configuration.isPressed && !YYMotion.reduce ? 0.97 : 1, anchor: UnitPoint(x: anchorX, y: 0.5))
                .animation(.easeOut(duration: configuration.isPressed ? 0.08 : YYDuration.quick), value: configuration.isPressed)
        }
    }

    /// Two or more: the picked book's line under the row.
    private func line(_ book: BookSummary) -> some View {
        ReadingLine(symbol: mark(book), title: book.title, meta: meta(book), progress: book.progress,
                    percent: book.percent, listen: { listen(book) }, read: { read(book) })
            .id(book.id)
            .transition(.opacity)
    }

    /// One book: the cover, its name and author at the top beside it, how far at the bottom.
    private func one(_ book: BookSummary) -> some View {
        HStack(alignment: .top, spacing: 18) {
            Button { read(book) } label: {
                BookCover(book: book, bleed: true)
                    .opacity(book.available ? 1 : 0.45)
                    .overlay(alignment: .bottomTrailing) {
                        if !book.available { unavailableBadge }
                    }
                    .frame(width: Self.cover.width, height: Self.cover.height)
                    .clipShape(.rect(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(.black.opacity(0.06), lineWidth: 0.5))
            }
            .buttonStyle(YYPressStyle(scale: 0.97))
            .contextMenu { menu(book) } preview: { BookPreview(book: book) }
            VStack(alignment: .leading, spacing: 0) {
                Text(book.title)
                    .font(YYUI.serif(21, .medium))
                    .tracking(0.5)
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(3)
                if let author = book.author {
                    Text(author).font(.yy(size: 13)).foregroundStyle(YYColor.t3).lineLimit(1).padding(.top, 5)
                }
                Spacer(minLength: 10)
                Text(meta(book)).font(.yy(size: 12.5)).foregroundStyle(YYColor.t3).lineLimit(1).padding(.bottom, 14)
                HStack {
                    ReadingMark(symbol: mark(book)) { listen(book) }
                    Spacer()
                    Text(book.percent).font(.yy(size: 12.5).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
                .padding(.bottom, 8)
                ReadingRule(value: book.progress)
            }
            .padding(.top, 4)
            .frame(height: Self.cover.height)
            .contentShape(.rect)
            .onTapGesture { read(book) }
        }
    }

    private func playing(_ book: BookSummary) -> Bool {
        library.listening == book.id || library.nowPlaying?.bookID == book.id
    }

    private func mark(_ book: BookSummary) -> String {
        if !book.available { return "icloud.and.arrow.down" }
        return playing(book) ? "waveform" : "headphones"
    }

    private var unavailableBadge: some View {
        Image(systemName: "icloud.and.arrow.down")
            .font(.yy(size: 11, weight: .semibold))
            .foregroundStyle(YYColor.t2)
            .padding(6)
            .background(YYColor.card, in: .circle)
            .padding(6)
    }

    /// The line: an 有声书's tap listens; while it plays, back to it.
    private func read(_ book: BookSummary) {
        open(book, book.format == .audio && !playing(book) ? .listen : nil)
    }

    private func listen(_ book: BookSummary) {
        open(book, playing(book) ? nil : .listen)
    }

    /// Who wrote it and how long is left (a PDF: which page).
    private func meta(_ book: BookSummary) -> String {
        if book.format == .pdf, book.page > 0 { return [book.author, String(localized: "第 \(book.page) 页")].compactMap { $0 }.joined(separator: " · ") }
        return [book.author, book.remaining].compactMap { $0 }.joined(separator: " · ")
    }

    static var vacant: Color { ReadingRule.vacant }
}

/// The line under 在读's row: the mark and the name in one line, the rule under them that is
/// how far, the grey line under it in line with the name.
private struct ReadingLine: View {
    let symbol: String
    let title: String
    let meta: String
    let progress: Double
    var percent: String?
    var listen: (() -> Void)?
    var read: (() -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                ReadingMark(symbol: symbol, action: listen)
                Text(title)
                    .font(YYUI.serif(22, .medium))
                    .tracking(0.5)
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                Spacer(minLength: 8)
                if let percent {
                    Text(percent).font(.yy(size: 12.5).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
            }
            .padding(.bottom, 8)
            ReadingRule(value: progress)
            Text(meta)
                .font(.yy(size: 12.5))
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
                .padding(.top, 9)
                .padding(.leading, 46)
        }
        .contentShape(.rect)
        .onTapGesture { read?() }
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(read == nil ? [] : .isButton)
    }
}

/// The thin drawing at the head of the line; with an action, a button (听).
private struct ReadingMark: View {
    let symbol: String
    var action: (() -> Void)?

    var body: some View {
        let mark = Group {
            switch symbol {
            // Thin line drawings on a 34 × 34 sheet, 1.5 thick.
            case "headphones": LineDrawing.headset.stroke(PlaceMark.ink, style: LineDrawing.style)
            case "book": LineDrawing.book.stroke(PlaceMark.ink, style: LineDrawing.style)
            default:
                Image(systemName: symbol)
                    .font(.system(size: 19, weight: .light))
                    .foregroundStyle(symbol == "icloud.and.arrow.down" ? YYColor.t2 : PlaceMark.ink)
                    .symbolEffect(.variableColor.iterative, options: .repeating, isActive: symbol == "waveform")
            }
        }
        .frame(width: 34, height: 34)
        if let action {
            let label = switch symbol {
            case "icloud.and.arrow.down": String(localized: "在别的设备上")
            case "waveform": String(localized: "正在听")
            default: String(localized: "听")
            }
            Button(action: action) { mark.contentShape(.rect) }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .accessibilityLabel(label)
        } else {
            mark.accessibilityHidden(true)
        }
    }
}

/// 在读's thin drawings: the headphones (a band, two rounded cups) and
/// the open book. Paths on a 34 × 34 sheet, scaled to whatever they're given.
private nonisolated struct LineDrawing: Shape {
    let draw: @Sendable (inout Path) -> Void
    static let style = StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round)

    nonisolated func path(in rect: CGRect) -> Path {
        var p = Path()
        draw(&p)
        let k = min(rect.width, rect.height) / 34
        return p.applying(CGAffineTransform(scaleX: k, y: k).translatedBy(x: (rect.width / k - 34) / 2, y: (rect.height / k - 34) / 2))
    }

    /// M9 22v-6a8 8 0 0 1 16 0v6 · M6.5 18.5h4v7h-4z · M23.5 18.5h4v7h-4z
    static let headset = LineDrawing { p in
        p.move(to: CGPoint(x: 9, y: 22))
        p.addLine(to: CGPoint(x: 9, y: 16))
        // The band: a half circle over the top, radius 8.
        p.addCurve(to: CGPoint(x: 17, y: 8), control1: CGPoint(x: 9, y: 11.58), control2: CGPoint(x: 12.58, y: 8))
        p.addCurve(to: CGPoint(x: 25, y: 16), control1: CGPoint(x: 21.42, y: 8), control2: CGPoint(x: 25, y: 11.58))
        p.addLine(to: CGPoint(x: 25, y: 22))
        p.addRect(CGRect(x: 6.5, y: 18.5, width: 4, height: 7))
        p.addRect(CGRect(x: 23.5, y: 18.5, width: 4, height: 7))
    }

    /// M5 9c4-1.6 8-1.6 12 1 4-2.6 8-2.6 12-1v15c-4-1.4-8-1.4-12 1-4-2.4-8-2.4-12-1z · M17 10v16
    static let book = LineDrawing { p in
        p.move(to: CGPoint(x: 5, y: 9))
        p.addCurve(to: CGPoint(x: 17, y: 10), control1: CGPoint(x: 9, y: 7.4), control2: CGPoint(x: 13, y: 7.4))
        p.addCurve(to: CGPoint(x: 29, y: 9), control1: CGPoint(x: 21, y: 7.4), control2: CGPoint(x: 25, y: 7.4))
        p.addLine(to: CGPoint(x: 29, y: 24))
        p.addCurve(to: CGPoint(x: 17, y: 25), control1: CGPoint(x: 25, y: 22.6), control2: CGPoint(x: 21, y: 22.6))
        p.addCurve(to: CGPoint(x: 5, y: 24), control1: CGPoint(x: 13, y: 22.6), control2: CGPoint(x: 9, y: 22.6))
        p.closeSubpath()
        p.move(to: CGPoint(x: 17, y: 10))
        p.addLine(to: CGPoint(x: 17, y: 26))
    }
}

/// A hairline the width of the row; the part read drawn over it in ink, never shorter than the
/// mark above it.
private struct ReadingRule: View {
    let value: Double
    static let hair = Color.yyDynamic(light: YYRGBA(226, 220, 210), dark: YYRGBA(58, 53, 47))
    /// 在读's empty places.
    static let vacant = Color.yyDynamic(light: YYRGBA(236, 230, 220), dark: YYRGBA(42, 38, 34))

    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(Self.hair).frame(height: 1.5)
                Capsule().fill(YYColor.t1).frame(width: min(g.size.width, max(34, g.size.width * value)), height: 2)
            }
            .frame(maxHeight: .infinity)
        }
        .frame(height: 2)
        .animation(YYMotion.standard, value: value)
        .accessibilityHidden(true)
    }
}
#endif
