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
    @State private var statsShown = false
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
        .sheet(isPresented: $statsShown) { MacStatsSheet(library: library) { statsShown = false } }
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
            Button { statsShown = true } label: {
                HStack(spacing: 5) {
                    Image(systemName: "chart.bar").font(.yy(size: 11, weight: .semibold))
                    Text("阅读统计")
                }
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 13)
                .frame(height: 28)
            }
            .buttonStyle(YYPillChrome())
            .help(String(localized: "按周、月、年查看阅读与听书时长及年度热力图"))
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

/// Mac 阅读统计面板: 周 / 月 / 年切换、读听时长柱状图、年度阅读热力图与每本书时长明细.
struct MacStatsSheet: View {
    let library: Library
    let done: () -> Void

    @State private var period = Period.year
    @State private var anchor = Date()
    @State private var times: [DayTime] = []
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    enum Period: String, CaseIterable, Identifiable {
        case week, month, year
        var id: String { rawValue }
        var name: String {
            switch self {
            case .week: String(localized: "周")
            case .month: String(localized: "月")
            case .year: String(localized: "年")
            }
        }
        static var calendar: Calendar {
            var c = Calendar.current
            c.firstWeekday = 2
            return c
        }
        var component: Calendar.Component {
            switch self {
            case .week: .weekOfYear
            case .month: .month
            case .year: .year
            }
        }
        func days(containing date: Date) -> [Date] {
            let cal = Self.calendar
            guard let range = cal.dateInterval(of: component, for: date) else { return [date] }
            var out: [Date] = []
            var d = range.start
            while d < range.end {
                out.append(d)
                d = cal.date(byAdding: .day, value: 1, to: d) ?? range.end
            }
            return out
        }
    }

    private var days: [Date] { period.days(containing: anchor) }
    private var isCurrent: Bool { days.contains { Calendar.current.isDateInToday($0) } }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 12) {
                Text("阅读统计")
                    .font(.yy(size: 16, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                Spacer()
                YYSegments(items: Period.allCases.map { ($0, $0.name) }, selection: $period, small: true)
                    .frame(width: 156)
                Button("完成", action: done)
                    .buttonStyle(RecordButton(main: false))
                    .keyboardShortcut(.cancelAction)
            }
            .padding(.horizontal, 22)
            .padding(.top, 18)
            .padding(.bottom, 12)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    periodCard
                    if !perBook.isEmpty {
                        VStack(alignment: .leading, spacing: 8) {
                            HStack {
                                Text("每本书").font(.yy(size: 12, weight: .semibold)).foregroundStyle(YYColor.t2)
                                Spacer()
                                Text("\(periodWord) · 按时长").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                            }
                            .padding(.horizontal, 4)
                            VStack(spacing: 0) {
                                ForEach(Array(perBook.enumerated()), id: \.element.0.id) { idx, row in
                                    if idx > 0 {
                                        Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.leading, 48)
                                    }
                                    bookRow(row.0, row.1, row.2)
                                }
                            }
                            .padding(.horizontal, 14)
                            .background(YYColor.card, in: .rect(cornerRadius: 14))
                            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
                        }
                    }
                }
                .padding(.horizontal, 22)
                .padding(.bottom, 22)
            }
        }
        .frame(width: 600, height: 540)
        .background(YYColor.bg)
        .task(id: "\(period.rawValue)\(Notes.day(anchor))") { load() }
        .onChange(of: period) { anchor = Date() }
    }

    private var periodCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: 8) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(heading.0).font(.yy(size: 14, weight: .bold)).foregroundStyle(YYColor.t1)
                    Text(heading.1).font(.yy(size: 11).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
                Spacer()
                HStack(spacing: 4) {
                    stepButton("chevron.left", String(localized: "往前")) { move(-1) }
                    stepButton("chevron.right", String(localized: "往后")) { move(1) }.disabled(isCurrent)
                }
            }
            HStack(spacing: 22) {
                statTotal(String(localized: "读"), times.reduce(0) { $0 + Int($1.readSeconds) }, tint.fill)
                statTotal(String(localized: "听"), times.reduce(0) { $0 + Int($1.listenSeconds) }, YYColor.tint)
            }
            .padding(.top, 8)
            columns.frame(height: 60).padding(.top, 14)
            axis.padding(.top, 6)
            if period == .year {
                Rectangle().fill(YYColor.line).frame(height: 0.5)
                    .padding(.top, 14)
                ReadingHeatmapView(
                    heatmap: ReadingHeatmap(yearDays: days, times: times),
                    tint: tint,
                    cellSize: 8,
                    spacing: 2
                )
                .padding(.top, 12)
            }
            if !finished.isEmpty {
                Rectangle().fill(YYColor.line).frame(height: 0.5)
                    .padding(.top, 14)
                HStack(spacing: 10) {
                    HStack(spacing: -6) {
                        ForEach(finished.prefix(3), id: \.id) { b in
                            BookCover(book: b).frame(width: 22)
                                .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(YYColor.card, lineWidth: 1.5))
                        }
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(finishedHeading).font(.yy(size: 12, weight: .semibold)).foregroundStyle(YYColor.t1)
                        Text(finished.map(\.title).joined(separator: " · ")).font(.yy(size: 11)).foregroundStyle(YYColor.t3).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.top, 11)
            }
        }
        .padding(16)
        .background(YYColor.card, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func stepButton(_ symbol: String, _ label: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYColor.t1)
                .frame(width: 24, height: 24)
                .background(YYColor.inset, in: .circle)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    private func statTotal(_ label: String, _ seconds: Int, _ color: Color) -> some View {
        let m = seconds / 60
        let parts: [(Int, String)] = m < 60 ? [(m, String(localized: "分"))] : m % 60 == 0 ? [(m / 60, String(localized: "小时"))] : [(m / 60, String(localized: "小时")), (m % 60, String(localized: "分"))]
        return HStack(alignment: .firstTextBaseline, spacing: 2) {
            RoundedRectangle(cornerRadius: 2).fill(color).frame(width: 7, height: 7)
                .alignmentGuide(.firstTextBaseline) { $0[.bottom] }
                .padding(.trailing, 3)
            Text(label)
            ForEach(Array(parts.enumerated()), id: \.offset) { i, part in
                Text("\(part.0)").font(.yy(size: 20, weight: .bold).monospacedDigit()).foregroundStyle(YYColor.t1)
                    .padding(.leading, i == 0 ? 4 : 5).padding(.trailing, 1)
                Text(part.1)
            }
        }
        .font(.yy(size: 11))
        .foregroundStyle(YYColor.t2)
    }

    private var heading: (String, String) {
        guard let first = days.first, let last = days.last else { return ("", "") }
        let cal = Calendar.current
        let end = isCurrent ? min(Date(), last) : last
        if !(Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") {
            let day = { (d: Date) in String(cal.component(.day, from: d)) }
            let short = { (d: Date) in d.formatted(.dateTime.month(.abbreviated)) }
            switch period {
            case .week:
                return (periodWord, "\(short(first)) \(day(first)) – \(cal.isDate(first, equalTo: last, toGranularity: .month) ? "" : short(last) + " ")\(day(last))")
            case .month:
                return (first.formatted(.dateTime.month(.wide)), "\(short(first)) 1 – \(day(end))")
            case .year:
                return (String(cal.component(.year, from: first)), "\(short(first)) – \(short(end))")
            }
        }
        switch period {
        case .week:
            return (periodWord, String(localized: "\(Notes.monthDay(first)) – \(cal.component(.month, from: first) == cal.component(.month, from: last) ? String(localized: "\(cal.component(.day, from: last)) 日") : Notes.monthDay(last))"))
        case .month:
            return (String(localized: "\(cal.component(.month, from: first)) 月"), String(localized: "1 日 – \(cal.component(.day, from: end)) 日"))
        case .year:
            return (String(localized: "\(cal.component(.year, from: first)) 年"), String(localized: "1 – \(cal.component(.month, from: end)) 月"))
        }
    }

    private var slots: [(read: Int, listen: Int, today: Bool, future: Bool)] {
        let cal = Calendar.current
        let now = Date()
        if period == .year {
            guard let firstDay = days.first else { return [] }
            return (0 ..< 12).map { m in
                let start = cal.date(byAdding: .month, value: m, to: firstDay) ?? firstDay
                let prefix = String(Notes.day(start).prefix(7))
                let t = times.filter { $0.day.hasPrefix(prefix) }
                return (t.reduce(0) { $0 + Int($1.readSeconds) }, t.reduce(0) { $0 + Int($1.listenSeconds) },
                        cal.isDate(start, equalTo: now, toGranularity: .month), start > now)
            }
        }
        return days.map { d in
            let key = Notes.day(d)
            let t = times.filter { $0.day == key }
            return (t.reduce(0) { $0 + Int($1.readSeconds) }, t.reduce(0) { $0 + Int($1.listenSeconds) },
                    cal.isDateInToday(d), d > now)
        }
    }

    private var columns: some View {
        let slots = slots
        let most = max(1, slots.map { $0.read + $0.listen }.max() ?? 1)
        let width: CGFloat = period == .week ? 10 : 6
        return GeometryReader { g in
            HStack(alignment: .bottom, spacing: 0) {
                ForEach(Array(slots.enumerated()), id: \.offset) { _, s in
                    VStack(spacing: 1) {
                        Spacer(minLength: 0)
                        if s.future {
                            RoundedRectangle(cornerRadius: 1).fill(YYColor.line).frame(height: 2)
                        } else {
                            if s.listen > 0 {
                                UnevenRoundedRectangle(topLeadingRadius: 2, topTrailingRadius: 2).fill(YYColor.tint)
                                    .frame(height: g.size.height * Double(s.listen) / Double(most))
                            }
                            if s.read > 0 {
                                UnevenRoundedRectangle(topLeadingRadius: s.listen > 0 ? 0 : 2, topTrailingRadius: s.listen > 0 ? 0 : 2)
                                    .fill(tint.fill)
                                    .frame(height: g.size.height * Double(s.read) / Double(most))
                            }
                        }
                    }
                    .frame(width: s.today ? width + 2 : width)
                    .frame(maxWidth: .infinity)
                }
            }
        }
    }

    private var axis: some View {
        let n = period == .year ? 12 : days.count
        return HStack(spacing: 0) {
            ForEach(0 ..< n, id: \.self) { i in
                Text(axisLabel(i)).font(.yy(size: 10).monospacedDigit()).foregroundStyle(YYColor.t3)
                    .fixedSize()
                    .frame(maxWidth: .infinity)
            }
        }
    }

    private func axisLabel(_ i: Int) -> String {
        switch period {
        case .week:
            guard days.indices.contains(i) else { return "" }
            let idx = Calendar.current.component(.weekday, from: days[i]) - 1
            if Locale.preferredLanguages.first?.hasPrefix("zh") ?? false {
                return ["日", "一", "二", "三", "四", "五", "六"][idx]
            }
            return Calendar.current.shortWeekdaySymbols[idx]
        case .month:
            return i % 7 == 0 ? "\(i + 1)" : ""
        case .year:
            let base = days.first ?? anchor
            return i % 2 != 0 ? ""
                : (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") ? String(localized: "\(i + 1) 月")
                : (Calendar.current.date(byAdding: .month, value: i, to: base) ?? base).formatted(.dateTime.month(.abbreviated))
        }
    }

    private func bookRow(_ book: BookSummary, _ read: Int, _ listen: Int) -> some View {
        HStack(spacing: 10) {
            BookCover(book: book).frame(width: 24)
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .firstTextBaseline, spacing: 8) {
                    Text(book.title).font(.yy(size: 12, weight: .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Spacer(minLength: 0)
                    let doneLine = book.finishedAt.map { String(localized: "\(Notes.monthDay(Notes.date($0)))读完") } ?? String(localized: "读完了")
                    Text(book.status == .finished ? doneLine : String(localized: "\(book.percent) · \(book.remaining.replacingOccurrences(of: String(localized: "还剩"), with: String(localized: "还要")))"))
                        .font(.yy(size: 11).monospacedDigit()).foregroundStyle(YYColor.t2).lineLimit(1)
                }
                Text([read > 0 ? String(localized: "读 ") + Self.duration(read) : nil, listen > 0 ? String(localized: "听 ") + Self.duration(listen) : nil].compactMap { $0 }.joined(separator: " · "))
                    .font(.yy(size: 10.5).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
            }
        }
        .padding(.vertical, 9)
    }

    private func load() {
        guard let first = days.first, let last = days.last else { return }
        let fresh = (try? library.core.dayTimes(from: Notes.day(first), to: Notes.day(last))) ?? []
        withAnimation(YYMotion.standard) { times = fresh }
    }

    private func move(_ by: Int) {
        anchor = Period.calendar.date(byAdding: period.component, value: by, to: anchor) ?? anchor
    }

    private var periodWord: String {
        switch period {
        case .week: isCurrent ? String(localized: "这周") : String(localized: "那一周")
        case .month: isCurrent ? String(localized: "这个月") : String(localized: "那个月")
        case .year: isCurrent ? String(localized: "今年") : String(localized: "那年")
        }
    }

    private var finishedHeading: String {
        switch (period, isCurrent) {
        case (.week, true): String(localized: "这周读完 \(finished.count) 本")
        case (.week, false): String(localized: "那一周读完 \(finished.count) 本")
        case (.month, true): String(localized: "这个月读完 \(finished.count) 本")
        case (.month, false): String(localized: "那个月读完 \(finished.count) 本")
        case (.year, true): String(localized: "今年读完 \(finished.count) 本")
        case (.year, false): String(localized: "那年读完 \(finished.count) 本")
        }
    }

    private var finished: [BookSummary] {
        guard let first = days.first, let last = days.last,
              let end = Calendar.current.date(byAdding: .day, value: 1, to: last) else { return [] }
        return library.books.filter { b in
            guard b.status == .finished, let at = b.finishedAt else { return false }
            let d = Notes.date(at)
            return d >= first && d < end
        }
    }

    private var perBook: [(BookSummary, Int, Int)] {
        var sums: [String: (Int, Int)] = [:]
        for t in times {
            sums[t.book, default: (0, 0)].0 += Int(t.readSeconds)
            sums[t.book, default: (0, 0)].1 += Int(t.listenSeconds)
        }
        return sums.compactMap { id, s in library.books.first { $0.id == id }.map { ($0, s.0, s.1) } }
            .filter { $0.1 + $0.2 >= 60 }
            .sorted { $0.1 + $0.2 > $1.1 + $1.2 }
    }

    private static func duration(_ seconds: Int) -> String {
        let m = seconds / 60
        if m < 60 { return String(localized: "\(m) 分") }
        return m % 60 == 0 ? String(localized: "\(m / 60) 小时") : String(localized: "\(m / 60) 小时 \(m % 60) 分")
    }
}
#endif
