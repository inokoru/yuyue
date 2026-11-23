#if os(iOS)
import SwiftUI
import YuyueCore

/// 书 on the phone, under 全部 · 书 · 有声: 在读 · 想读 · 读过 · 搁置 · 弃读, then a card for each
/// book — the files on this phone and the ones kept only as a record.
struct PhoneBooks: View {
    let library: Library
    @Binding var status: ReadingStatus
    @Binding var deleting: BookSummary?
    let show: (String) -> Void
    let read: (BookSummary) -> Void
    var pick: (BookSummary) -> Void = { _ in }

    private var counts: [ReadingStatus: Int] {
        Dictionary(grouping: library.keptBooks, by: \.status).mapValues(\.count)
    }

    @AppStorage("books.sort") private var sort = BookSort.recent

    private var books: [BookSummary] {
        let these = library.keptBooks.filter { $0.status == status }
        // 颜色 is 读过's own; elsewhere a stored 颜色 reads as 最近读过.
        if sort == .color, status != .finished { return BookSort.recent.sorted(these) }
        return sort.sorted(these)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ScrollView(.horizontal, showsIndicators: false) {
                StatusTabs(status: $status, counts: counts, size: 13.5, sort: $sort, showsSort: false)
                    .padding(.horizontal, 20)
            }
            .scrollBounceBehavior(.basedOnSize)
            // How many in this one, and their order: the shelf's 全部 row.
            if !books.isEmpty {
                HStack {
                    Text(String(localized: "\(books.count) 本")).font(.footnote).foregroundStyle(YYColor.t3)
                    Spacer()
                    Menu {
                        Picker("排序", selection: $sort) {
                            ForEach(BookSort.offered(for: status), id: \.self) { Text($0.name).tag($0) }
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Text(sort.name)
                            Image(systemName: "chevron.up.chevron.down").font(.yy(size: 10, weight: .semibold))
                        }
                        .font(.footnote)
                        .foregroundStyle(YYColor.t2)
                    }
                }
                .padding(.horizontal, 20)
                .padding(.top, 10)
            }
            if books.isEmpty {
                Text(emptyLine)
                    .font(.yy(size: 13))
                    .foregroundStyle(YYColor.t3)
                    .frame(maxWidth: .infinity)
                    .padding(.top, 60)
                    .transition(.opacity)
            } else if status == .finished {
                PhoneFinishedStrips(books: books, library: library, show: show, read: read, pick: pick) { book in
                    if book.hasRecord || book.record { deleting = book } else { library.delete(book) }
                }
            } else {
                LazyVStack(spacing: 10) {
                    ForEach(books) { book in
                        BookRecordCard(book: book, library: library) {
                            show(book.id)
                        } read: {
                            read(book)
                        } pickCollection: {
                            pick(book)
                        } delete: {
                            if book.hasRecord || book.record { deleting = book } else { library.delete(book) }
                        }
                    }
                }
                .padding(.horizontal, 20)
                .padding(.top, 12)
            }
        }
        // Rows re-sort and close up; another tab, or its last book going, crossfades.
        .animation(YYMotion.layout, value: books.map(\.id))
        .animation(YYMotion.quick, value: status)
    }

    private var emptyLine: String {
        switch status {
        case .reading: String(localized: "没有在读的书")
        case .want: String(localized: "还没有想读的书，右上角 ＋ 加一本")
        case .finished: String(localized: "读完的书会在这里")
        case .paused: String(localized: "没有搁置的书")
        case .dropped: String(localized: "没有弃读的书")
        }
    }
}

/// 加一本书: a title, and it's in.
struct PhoneAddBook: View {
    let library: Library
    let status: ReadingStatus
    let done: () -> Void
    @State private var title = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 12) {
                Text("书")
                    .font(YYUI.serif(15))
                    .foregroundStyle(YYColor.t3)
                    .frame(width: 36, height: 52)
                    .background(YYColor.card, in: .rect(cornerRadius: 3))
                    .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(YYColor.line, lineWidth: 0.5))
                TextField(String(localized: "书名"), text: $title)
                    .font(YYUI.serif(19, .bold))
                    .focused($focused)
                    .submitLabel(.done)
                    .onSubmit(add)
            }
            HStack {
                Text("放进 \(status.name)").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                Spacer()
                Button(String(localized: "加入"), action: add)
                    .buttonStyle(RecordButton(main: true))
                    .disabled(title.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .padding(20)
        .onAppear { focused = true }
    }

    private func add() {
        let t = title.trimmingCharacters(in: .whitespaces)
        guard !t.isEmpty else { return }
        library.addRecordBook(t, status: status)
        done()
    }
}
#endif
