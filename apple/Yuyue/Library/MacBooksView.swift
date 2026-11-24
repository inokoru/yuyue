#if os(macOS)
import SwiftUI
import YuyueCore

/// 书 on the Mac: 在读 · 想读 · 读过 · 搁置 · 弃读 across the top, a card for each book, as many
/// columns as the window has room for; a card opens the book's page in its place, with 返回 at the top left.
struct MacBooksView: View {
    let library: Library
    var start = ReadingStatus.reading
    let open: (BookSummary, Library.OpenIntent?) -> Void
    /// Right-click: the shelf's own menu card, where the click was.
    var menu: (BookSummary, CGPoint?) -> Void = { _, _ in }
    /// 放进合集…: the shelf's own picker.
    var pick: (BookSummary) -> Void = { _ in }

    @Environment(\.shelfHeaderInset) private var headerInset
    @State private var status = ReadingStatus.reading
    @State private var shown: String?
    @State private var adding = false
    @State private var newTitle = ""
    @State private var deleting: BookSummary?
    @FocusState private var titleFocused: Bool

    private var counts: [ReadingStatus: Int] {
        Dictionary(grouping: library.keptBooks, by: \.status).mapValues(\.count)
    }

    @AppStorage("books.sort") private var sort = BookSort.recent

    private var books: [BookSummary] {
        sort.sorted(library.keptBooks.filter { $0.status == status })
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            if let shown {
                HStack(spacing: 6) {
                    Button {
                        withAnimation(YYMotion.standard) { self.shown = nil }
                    } label: {
                        HStack(spacing: 6) {
                            Image(systemName: "chevron.left").font(.yy(size: 13, weight: .semibold))
                            Text("进度").font(.yy(size: 13))
                        }
                        .foregroundStyle(YYColor.t2)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .keyboardShortcut("[", modifiers: .command)
                    Spacer()
                }
                .padding(.leading, 22 + headerInset)
                .padding(.trailing, 28)
                .frame(height: 60)
                .padding(.top, 4)
                BookRecordPage(library: library, bookID: shown, wide: true, open: open) { pick($0) }
                    .transition(.opacity.combined(with: .move(edge: .trailing)))
            } else {
                header
                    .frame(height: 60)
                    .padding(.top, 4)
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                StatusTabs(status: $status, counts: counts, sort: $sort)
                    .padding(.leading, 22)
                    .padding(.trailing, 28)
                let currentBooks = books
                if currentBooks.isEmpty {
                    empty
                } else {
                    ScrollView {
                        Group {
                            if currentBooks.count == 1, let book = currentBooks.first {
                                recordTile(book)
                                    .frame(maxWidth: 344, alignment: .leading)
                            } else {
                                LazyVGrid(columns: [GridItem(.adaptive(minimum: 294), spacing: 12)], spacing: 12) {
                                    ForEach(currentBooks) { book in
                                        recordTile(book)
                                    }
                                }
                            }
                        }
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.leading, 22)
                        .padding(.trailing, 28)
                        .padding(.top, 16)
                        .padding(.bottom, 28)
                        .animation(YYMotion.layout, value: currentBooks.map(\.id))
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea(.container, edges: .top)
        .background(YYColor.bg)
        .onAppear {
            status = start
            #if DEBUG
            // `-debugFilter books -debugBook 背影` opens that book's page; `-debugBooksTab finished` that tab.
            if let key = UserDefaults.standard.string(forKey: "debugBooksTab"), let s = ReadingStatus.shelfOrder.first(where: { "\($0)" == key }) { status = s }
            if let part = UserDefaults.standard.string(forKey: "debugBook") { shown = library.keptBooks.first { $0.title.contains(part) }?.id }
            #endif
        }
        .sheet(isPresented: $adding) { addSheet }
        .deletingRecord($deleting, library: library)
    }

    private func recordTile(_ book: BookSummary) -> some View {
        BookRecordCard(book: book, library: library, compact: true) {
            withAnimation(YYMotion.standard) { shown = book.id }
        } read: {
            open(book, nil)
        } pickCollection: {
            pick(book)
        } delete: {
            ask(delete: book)
        } menuAt: { point in
            menu(book, point)
        } readAt: { row in
            if case .current = row.state { open(book, nil) } else { open(book, .at(Position(section: row.section, sentence: 0))) }
        }
        .overlay(RightClickCatcher { menu(book, $0) })
    }

    private func ask(delete book: BookSummary) {
        if book.hasRecord || book.record { deleting = book } else { library.delete(book) }
    }

    private var header: some View {
        HStack(spacing: 12) {
            HStack(alignment: .firstTextBaseline, spacing: 12) {
                Text("进度").font(.yy(size: 22, weight: .bold)).foregroundStyle(YYColor.t1)
                    .padding(.leading, headerInset)
                Text("\(library.keptBooks.count) 本").font(.yy(size: 12).monospacedDigit()).foregroundStyle(YYColor.t2)
            }
            Spacer()
            Button { newTitle = ""; adding = true } label: {
                HStack(spacing: 5) {
                    Image(systemName: "plus").font(.yy(size: 11, weight: .semibold))
                    Text("加书")
                }
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 13)
                .frame(height: 28)
            }
            .buttonStyle(YYPillChrome())
            .help(String(localized: "加一本纸书或在别处读的书，打个书名就行"))
        }
    }

    private var empty: some View {
        VStack(spacing: 6) {
            Text(emptyLine).font(.yy(size: 13)).foregroundStyle(YYColor.t2)
            if status == .want || status == .reading {
                Button(String(localized: "加一本书")) { newTitle = ""; adding = true }
                    .buttonStyle(RecordButton(main: true))
                    .padding(.top, 8)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .padding(.bottom, 60)
    }

    private var emptyLine: String {
        switch status {
        case .reading: String(localized: "没有在读的书")
        case .want: String(localized: "还没有想读的书")
        case .finished: String(localized: "读完的书会在这里")
        case .paused: String(localized: "没有搁置的书")
        case .dropped: String(localized: "没有弃读的书")
        }
    }

    // MARK: 加一本书

    private var addSheet: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("加一本书").font(.yy(size: 15, weight: .semibold))
            TextField(String(localized: "书名"), text: $newTitle)
                .font(YYUI.serif(17, .bold))
                .textFieldStyle(.plain)
                .focused($titleFocused)
                .padding(.horizontal, 12)
                .frame(height: 40)
                .background(YYColor.card, in: .rect(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: 0.5))
                .onSubmit(add)
            HStack {
                Text("放进 \(status.name)").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                Spacer()
                Button(String(localized: "取消")) { adding = false }
                    .buttonStyle(RecordButton(main: false))
                    .keyboardShortcut(.cancelAction)
                Button(String(localized: "加入"), action: add)
                    .buttonStyle(RecordButton(main: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(newTitle.trimmingCharacters(in: .whitespaces).isEmpty)
            }
            Text("作者、页数、封面以后在书的页面里补，不补也行。")
                .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
        }
        .padding(20)
        .frame(width: 380)
        .background(YYColor.bg)
        .onAppear { titleFocused = true }
    }

    private func add() {
        let title = newTitle.trimmingCharacters(in: .whitespaces)
        guard !title.isEmpty else { return }
        library.addRecordBook(title, status: status)
        adding = false
    }
}
#endif
