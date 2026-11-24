#if os(macOS)
import SwiftUI
import YuyueCore

/// 笔记 in the shelf window: every book's highlights and thoughts.
/// By day, newest first; or a column of books with the chosen one's notes in book order. The dots
/// show one colour only; 导出 takes what is shown. Double-click goes back to the page.
struct MacNotesView: View {
    @Environment(\.shelfHeaderInset) private var headerInset
    let library: Library
    @Environment(\.openWindow) private var openWindow
    @Environment(\.colorScheme) private var scheme
    @AppStorage("notes.byBook") private var byBook = false
    /// 按书's column as covers only, like Preview's thumbnails, so the notes get the room.
    @AppStorage("notes.bookCovers") private var coversOnly = false
    @State private var notes: [BookNotes] = []
    @State private var only: Only = .all
    @State private var chosen: String?
    /// 今天回顾's deck, and the review under way.
    @State private var deck: Review?
    @State private var reviewing: Review?
    @State private var sharing: QuoteShare?
    @State private var writing: EditingMark?

    private struct EditingMark: Identifiable {
        let id: String
        let bookID: String
        let mark: Mark
    }
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    enum Only: Hashable {
        case all, thoughts
        case style(MarkStyle)

        func keeps(_ m: Mark) -> Bool {
            switch self {
            case .all: true
            case .thoughts: m.note != nil
            case .style(let s): m.style == s
            }
        }
    }

    struct BookNotes: Identifiable {
        let book: BookSummary
        let detail: BookDetail?
        let marks: [Mark]
        var id: String { book.id }
        var latest: Int64 { marks.map(\.at).max() ?? 0 }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            // 64 high, 4 of it above, the title in the middle; the list 22 in, 28 on the right.
            header
                .frame(height: 60)
                .padding(.top, 4)
                .padding(.leading, 22)
                .padding(.trailing, 28)
            if notes.isEmpty {
                empty
            } else if byBook {
                bookColumns
            } else {
                ScrollView {
                    VStack(alignment: .leading, spacing: 18) {
                        chips(for: allMarks).padding(.bottom, 4)
                        if let deck, !deck.items.isEmpty || deck.doneToday > 0 {
                            ReviewDeckCard(review: deck, library: library) { reviewing = deck }
                        }
                        byDay
                    }
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                    .padding(.top, 2)
                    .padding(.bottom, 28)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(.container, edges: .top)
        .background(YYColor.bg)
        .yyHideWindowToolbarChrome()
        .task(id: library.books.map(\.id)) { load() }
        .onReceive(NotificationCenter.default.publisher(for: .notesChanged)) { _ in load() }
        .sheet(item: $sharing) { quote in
            QuoteShareSheet(quote: quote) { sharing = nil }
        }
        .sheet(item: $writing) { item in
            ThoughtSheet(mark: item.mark) { text in
                library.setNote(text?.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty == false ? text : nil, book: item.bookID, mark: item.mark)
                writing = nil
            } cancel: {
                writing = nil
            }
        }
        .sheet(item: $reviewing, onDismiss: load) { review in
            MacReviewSheet(review: review, library: library) { item in
                reviewing = nil
                guard let n = notes.first(where: { $0.id == item.book }) else { return }
                open(n, item.mark)
            } only: { book in
                reviewing = Review(core: library.core, book: book)
            }
        }
    }

    private func load() {
        notes = library.books.filter(\.available).compactMap { book in
            guard let marks = try? library.core.marks(book: book.id), !marks.isEmpty else { return nil }
            return BookNotes(book: book, detail: try? library.core.book(id: book.id), marks: marks)
        }
        .sorted { $0.latest > $1.latest }
        deck = Review(core: library.core)
        #if DEBUG
        if UserDefaults.standard.bool(forKey: "debugReview"), !Self.debugShown {
            Self.debugShown = true
            reviewing = deck
        }
        // `-debugQuote 1`: the share book excerpt sheet of the first note with a thought (use with -debugSeed).
        if UserDefaults.standard.bool(forKey: "debugQuote"),
           let n = notes.first(where: { $0.marks.contains { $0.note != nil } }), let mark = n.marks.first(where: { $0.note != nil }) {
            sharing = Self.quote(n, mark)
        }
        // A sheet can't be captured from outside: draw it to a picture instead.
        if let name = UserDefaults.standard.string(forKey: "debugReviewShot"), let deck, !Self.debugShown {
            Self.debugShown = true
            deck.preview(step: UserDefaults.standard.integer(forKey: "debugReviewStep"))
            let r = ImageRenderer(content: MacReviewSheet(review: deck, library: library, open: { _ in }, only: { _ in }))
            r.scale = 2
            if let tiff = r.nsImage?.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                try? png.write(to: FileManager.default.temporaryDirectory.appending(path: name))
            }
        }
        // `-debugShareShot name.png`: the 书摘 card of the first note with a thought, as a picture
        // (`-share.look cover|paper|white|dark`, `-share.shape portrait|square|landscape`).
        if let name = UserDefaults.standard.string(forKey: "debugShareShot"),
           let n = notes.first(where: { $0.marks.contains { $0.note != nil } }), let mark = n.marks.first(where: { $0.note != nil }) {
            let d = UserDefaults.standard, quote = Self.quote(n, mark)
            Task {
                let cover = await Thumbnail.load(quote.coverPath, maxPixels: 240)
                let card = QuoteCard(quote: quote, look: d.string(forKey: "share.look").flatMap(QuoteLook.init) ?? .cover,
                                     shape: d.string(forKey: "share.shape").flatMap(QuoteShape.init) ?? .portrait,
                                     withNote: true, cover: cover, coverColor: cover.flatMap(QuoteShareSheet.deepColor))
                let r = ImageRenderer(content: card.padding(30).background(Color(white: 0.9)))
                r.scale = 2
                if let tiff = r.nsImage?.tiffRepresentation, let png = NSBitmapImageRep(data: tiff)?.representation(using: .png, properties: [:]) {
                    try? png.write(to: FileManager.default.temporaryDirectory.appending(path: name))
                }
            }
        }
        #endif
        if chosen == nil || !notes.contains(where: { $0.id == chosen }) { chosen = notes.first?.id }
    }

    static func quote(_ n: BookNotes, _ mark: Mark) -> QuoteShare {
        let chapter = n.detail?.chapterTitle(for: mark.section)
        return QuoteShare(text: mark.text, note: mark.note, title: n.book.title, author: n.book.author,
                          chapter: chapter == n.book.title ? nil : chapter, coverPath: n.book.coverPath)
    }

    #if DEBUG
    private static var debugShown = false
    #endif

    private var allMarks: [Mark] { notes.flatMap(\.marks) }

    // MARK: Header

    private var header: some View {
        HStack(spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text("笔记").font(.yy(size: 22, weight: .bold)).foregroundStyle(YYColor.t1)
                    .padding(.leading, headerInset)
                Text("\(allMarks.count) 条 · 来自 \(notes.count) 本书")
                    .font(.yy(size: 12).monospacedDigit()).foregroundStyle(YYColor.t2)
            }
            Spacer()
            Segments(items: [(false, String(localized: "按时间")), (true, String(localized: "按书"))], selection: $byBook, small: true) { Text($1) }
                .frame(minWidth: 156)
                .fixedSize()
                .onChange(of: byBook) { only = .all }
            NoteExportMenu(sender: library.noteSender,
                           markdown: markdown, name: exportTitle + ".md",
                           books: { exportBooks.map { NoteExport.Book(id: $0.book.id, title: $0.book.title, author: $0.book.author, chapters: chapters($0)) } },
                           book: byBook ? exportBooks.first?.book : nil) {
                ShelfMenuPill(title: "导出", systemImage: "square.and.arrow.up")
            }
            .disabled(notes.isEmpty)
        }
    }

    private func chips(for marks: [Mark]) -> some View {
        HStack(spacing: 6) {
            chip(.all, String(localized: "全部"), marks.count)
            if !byBook { chip(.thoughts, String(localized: "有想法"), marks.count { $0.note != nil }) }
            Rectangle().fill(YYColor.line).frame(width: 0.5, height: 16).padding(.horizontal, 4)
            ForEach(MarkStyle.colours, id: \.self) { style in
                let n = marks.count { $0.style == style }
                if n > 0 { chip(.style(style), nil, n, dot: style) }
            }
        }
    }

    /// The name, then the count in grey.
    private func chip(_ value: Only, _ name: String?, _ count: Int, dot: MarkStyle? = nil) -> some View {
        let on = only == value
        return Button { only = on && value != .all ? .all : value } label: {
            HStack(spacing: 4) {
                if let dot {
                    if dot == .wave {
                        Image(systemName: "scribble").font(.yy(size: 13, weight: .bold)).foregroundStyle(YYColor.hlR)
                    } else {
                        Circle()
                            .fill(dot.dot)
                            .frame(width: 10, height: 10)
                            .overlay(Circle().strokeBorder(on ? YYColor.card : Color.black.opacity(0.08), lineWidth: on ? 1.5 : 0.5))
                    }
                }
                if let name { Text(name) }
                Text(count.formatted()).monospacedDigit()
                    .foregroundStyle(on ? AnyShapeStyle(tint.accent.opacity(0.7)) : AnyShapeStyle(YYColor.t3))
            }
            .font(.yy(size: 11, weight: on ? .semibold : .regular))
            .foregroundStyle(on ? tint.accent : YYColor.t2)
            .padding(.horizontal, 11).frame(height: 26)
            // The one chosen: the theme's wash and hairline, as 生词's 在学 / 掌握了.
            .background(on ? tint.black : YYColor.pill, in: .capsule)
            .overlay(Capsule().strokeBorder(on ? tint.edge : YYColor.pillLine, lineWidth: 0.5))
            .animation(YYMotion.quick, value: on)
        }
        .buttonStyle(.plain)
    }

    // MARK: 按时间

    private struct Day: Identifiable {
        let id: Date
        var items: [(BookNotes, Mark)]
    }

    private var days: [Day] {
        let cal = Calendar.current
        var out: [Day] = []
        let items = notes.flatMap { n in n.marks.filter(only.keeps).map { (n, $0) } }.sorted { $0.1.at > $1.1.at }
        for item in items {
            let day = cal.startOfDay(for: Notes.date(item.1.at))
            if out.last?.id == day { out[out.count - 1].items.append(item) } else { out.append(Day(id: day, items: [item])) }
        }
        return out
    }

    private var byDay: some View {
        LazyVStack(alignment: .leading, spacing: 18) {
            ForEach(days) { day in
                HStack(alignment: .top, spacing: ShelfNotesLayout.columnSpacing) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(Notes.dayName(day.id)).font(.yy(size: 13, weight: .bold)).foregroundStyle(YYColor.t1)
                        Text(Notes.monthDay(day.id)).font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        Text("\(day.items.count) 条").font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                    }
                    .frame(width: ShelfNotesLayout.sideWidth, alignment: .leading)
                    .padding(.top, ShelfNotesLayout.sideTopPadding)
                    NoteCard {
                        ForEach(day.items, id: \.1.id) { n, mark in
                            entry(n, mark) {
                                HStack(spacing: 6) {
                                    BookCover(book: n.book).frame(width: 13)
                                    Text(n.book.title).font(.yy(size: 10.5, weight: .semibold)).foregroundStyle(YYColor.t2)
                                    if let chapter = n.detail?.chapterTitle(for: mark.section), chapter != n.book.title {
                                        Text("· \(chapter)").font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
                                    }
                                    Spacer()
                                    NoteStamp(mark: mark, text: Notes.time(mark.at), size: 10.5)
                                }
                                .lineLimit(1)
                            }
                        }
                    }
                    // A note taken out closes up; the ones under it move up into its place.
                    .animation(YYMotion.layout, value: day.items.map(\.1.id))
                }
            }
        }
        .animation(YYMotion.layout, value: days.map(\.id))
    }

    // MARK: 按书

    private var bookColumns: some View {
        HStack(alignment: .top, spacing: 24) {
            if coversOnly {
                coverRail
            } else {
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .center, spacing: 6) {
                        Text("\(notes.count) 本书").font(.yy(size: 12.5, weight: .semibold).monospacedDigit()).foregroundStyle(YYColor.t1)
                        Spacer()
                        ColumnSwitch(coversOnly: $coversOnly)
                    }
                    .frame(height: 22)
                    .padding(.leading, 4)
                    // The card holds the rows only, scrolling once they outgrow the window.
                    ScrollView {
                        VStack(spacing: 1) {
                            ForEach(notes) { bookRow($0) }
                        }
                        .padding(5)
                        .background(YYColor.card, in: .rect(cornerRadius: 14))
                        .yyShadow(scheme == .dark ? YYShadow.ringDark : YYShadow.ringLight)
                        .padding(1).padding(.bottom, 20)
                    }
                    .scrollIndicators(.never)
                    .scrollBounceBehavior(.basedOnSize)
                }
                .frame(width: 250)
                .padding(.top, 4)
            }
            ScrollView {
                if let n = notes.first(where: { $0.id == chosen }) {
                    bookNotes(n).padding(.top, 4).padding(.bottom, 28)
                }
            }
            .scrollIndicators(.automatic)
        }
        .padding(.leading, 22)
        .padding(.trailing, 28)
    }

    /// The column as covers: the switch stays in the row where 「N 本书」 sits, then one narrow
    /// card of small covers, the chosen one ringed.
    private var coverRail: some View {
        VStack(spacing: 8) {
            ColumnSwitch(coversOnly: $coversOnly)
                .frame(height: 22)
            ScrollView {
                VStack(spacing: 10) {
                    ForEach(notes) { n in
                        let on = chosen == n.id
                        Button { chosen = n.id; only = .all } label: {
                            BookCover(book: n.book)
                                .frame(width: 30)
                                .padding(3)
                                .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(on ? tint.accent : .clear, lineWidth: 1.5))
                                .opacity(on ? 1 : 0.75)
                                .animation(YYMotion.quick, value: on)
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .help(n.book.title)
                    }
                }
                .padding(.vertical, 8)
            }
            .scrollIndicators(.never)
            .scrollBounceBehavior(.basedOnSize)
            .frame(width: 48)
            .fixedSize(horizontal: false, vertical: true)
            .background(YYColor.card, in: .rect(cornerRadius: 12))
            .yyShadow(scheme == .dark ? YYShadow.ringDark : YYShadow.ringLight)
            .padding(1)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .padding(.top, 4)
    }

    private func bookRow(_ n: BookNotes) -> some View {
        let on = chosen == n.id
        return Button { chosen = n.id; only = .all } label: {
            HStack(spacing: 10) {
                BookCover(book: n.book).frame(width: 26)
                VStack(alignment: .leading, spacing: 2) {
                    Text(n.book.title).font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(on ? tint.accent : YYColor.t1)
                    Text(Notes.counts(n.marks)).font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                }
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                Text(Notes.shortDay(Notes.date(n.latest))).font(.yy(size: 10.5)).monospacedDigit()
                    .foregroundStyle(on ? tint.accent : YYColor.t3)
                    .frame(maxHeight: .infinity, alignment: .top).padding(.top, 4)
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.leading, 8).padding(.trailing, 10).padding(.vertical, 7)
            .background(on ? tint.soft : .clear, in: .rect(cornerRadius: 10))
            .animation(YYMotion.quick, value: on)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func bookNotes(_ n: BookNotes) -> some View {
        VStack(alignment: .leading, spacing: 16) {
            HStack(alignment: .center, spacing: 14) {
                BookCover(book: n.book).frame(width: 46)
                VStack(alignment: .leading, spacing: 1) {
                    Text(n.book.title).font(.yy(size: 19, weight: .bold)).foregroundStyle(YYColor.t1).padding(.bottom, 3)
                    Text([n.book.author, String(localized: "读到 \(n.book.percent)")].compactMap { $0 }.joined(separator: " · "))
                    Text(Notes.counts(n.marks) + String(localized: " · 按书里的顺序"))
                }
                .font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                .lineLimit(1)
                .frame(maxWidth: .infinity, alignment: .leading)
                smallButton(String(localized: "回顾"), "arrow.trianglehead.2.clockwise") { reviewing = Review(core: library.core, book: n.book) }
                smallButton(String(localized: "打开书"), "book") { open(n, nil) }
            }
            chips(for: n.marks)
            ForEach(chapters(n), id: \.0) { title, marks in
                VStack(alignment: .leading, spacing: 8) {
                    HStack(alignment: .firstTextBaseline, spacing: 12) {
                        Text(title).font(.yy(size: 12, weight: .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                        Spacer()
                        Text("\(marks.count) 条").font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                    }
                    .padding(.horizontal, 4)
                    NoteCard {
                        ForEach(marks, id: \.id) { mark in
                            entry(n, mark) {
                                HStack {
                                    Spacer()
                                    NoteStamp(mark: mark, text: Notes.monthDay(Notes.date(mark.at)), size: 10.5)
                                }
                            }
                        }
                    }
                    .animation(YYMotion.layout, value: marks.map(\.id))
                }
            }
        }
    }

    /// A small white button in the book's heading.
    private func smallButton(_ title: String, _ icon: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: icon)
                .labelStyle(SmallButtonLabel())
                .font(.yy(size: 10.5, weight: .semibold))
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 10).frame(height: 26)
                .background(YYColor.card, in: .capsule)
                .yyShadow(scheme == .dark ? YYShadow.ringDark : YYShadow.ringLight)
        }
        .buttonStyle(.plain)
    }

    private struct SmallButtonLabel: LabelStyle {
        func makeBody(configuration: Configuration) -> some View {
            HStack(spacing: 5) { configuration.icon.font(.yy(size: 11, weight: .medium)); configuration.title }
        }
    }

    /// The book's notes in reading order, under their chapters.
    private func chapters(_ n: BookNotes) -> [(String, [Mark])] {
        var out: [(String, [Mark])] = []
        let marks = n.marks.filter(only.keeps).sorted {
            ($0.section, $0.start.sentence, $0.start.offset) < ($1.section, $1.start.sentence, $1.start.offset)
        }
        for m in marks {
            let title = n.detail?.chapterTitle(for: m.section) ?? n.book.title
            if out.last?.0 == title { out[out.count - 1].1.append(m) } else { out.append((title, [m])) }
        }
        return out
    }

    // MARK: One note

    private func entry(_ n: BookNotes, _ mark: Mark, @ViewBuilder footer: () -> some View) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            MarkText(mark: mark)
                .font(YYUI.serif(14))
                .tracking(0.28)
                .lineSpacing(8)
                .foregroundStyle(YYColor.t1)
            if let note = mark.note {
                Text(note)
                    .font(YYFontFamily.kaiti.font(size: 13))
                    .lineSpacing(4)
                    .foregroundStyle(tint.accent)
                    .padding(.leading, 10)
                    .overlay(alignment: .leading) { Rectangle().fill(tint.accent).frame(width: 1.5) }
                    .padding(.top, 6)
            }
            footer().padding(.top, 9)
        }
        .padding(.top, 14).padding(.bottom, 12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .contentShape(.rect)
        .onTapGesture(count: 2) { open(n, mark) }
        .contextMenu {
            Button("回到书里这一页") { open(n, mark) }
            Button(mark.note == nil ? String(localized: "写想法…") : String(localized: "改想法…")) {
                writing = EditingMark(id: "\(n.book.id)/\(mark.id)", bookID: n.book.id, mark: mark)
            }
            Button("复制") {
                NSPasteboard.general.clearContents()
                NSPasteboard.general.setString(mark.text + (mark.note.map { "\n\n" + $0 } ?? ""), forType: .string)
            }
            Button("分享书摘…") { sharing = Self.quote(n, mark) }
            // As on iPhone; the note at the bottom of the shelf offers 撤销.
            Divider()
            Button("删除划线", role: .destructive) { library.deleteMarks([(n.book.id, mark)]) }
        }
    }

    private func open(_ n: BookNotes, _ mark: Mark?) {
        let ok = if let mark {
            library.open(n.book.id, at: Position(section: mark.section, sentence: mark.start.sentence))
        } else {
            library.open(n.book, nil)
        }
        guard ok else { return }
        openWindow(id: "reader", value: n.book.id)
    }

    private var empty: some View {
        VStack(spacing: 10) {
            Image(systemName: "quote.opening").font(.yy(size: 28, weight: .light)).foregroundStyle(YYColor.t3)
            Text("还没有笔记").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
            Text("读书时选中文字，按 1–4 就划上；所有书的划线和想法都会在这里")
                .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    // MARK: 导出

    private var exportTitle: String {
        byBook ? String(localized: "《\(notes.first { $0.id == chosen }?.book.title ?? "")》的笔记") : String(localized: "全部笔记")
    }

    /// What is shown: in 按书 only the chosen book.
    private var exportBooks: [BookNotes] { byBook ? notes.filter { $0.id == chosen } : notes }

    /// Markdown of what is shown.
    private var markdown: String {
        let books = exportBooks
        var out = byBook ? "# \(exportTitle)\n" : String(localized: "# 语阅笔记\n")
        for n in books {
            if !byBook { out += "\n## 《\(n.book.title)》\n" }
            for (title, marks) in chapters(n) {
                out += "\n\(byBook ? "##" : "###") \(title)\n"
                for m in marks {
                    out += "\n> \(m.text)\n"
                    if let note = m.note { out += "\n\(note)\n" }
                }
            }
        }
        return out
    }

}

/// 按书's column as names or as covers: two small icons in the same tray as 按时间 | 按书, the
/// chosen one on a white chip. 48 wide, so it fits over the column of covers too.
private struct ColumnSwitch: View {
    @Binding var coversOnly: Bool

    var body: some View {
        HStack(spacing: 2) {
            cell(false, "list.bullet", String(localized: "显示书名"))
            cell(true, "rectangle.grid.1x2", String(localized: "只看封面"))
        }
        .padding(2)
        .background(YYColor.inset, in: .rect(cornerRadius: 8))
    }

    private func cell(_ value: Bool, _ symbol: String, _ help: String) -> some View {
        let on = coversOnly == value
        return Button {
            withAnimation(YYMotion.layout) { coversOnly = value }
        } label: {
            Image(systemName: symbol)
                .font(.yy(size: 10.5, weight: on ? .semibold : .regular))
                .foregroundStyle(on ? YYColor.t1 : YYColor.t3)
                .frame(width: 21, height: 18)
                .background(on ? YYColor.card : .clear, in: .rect(cornerRadius: 6))
                .shadow(color: .black.opacity(on ? 0.07 : 0), radius: 1.5, y: 0.5)
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(help)
        .accessibilityLabel(help)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}
#endif
