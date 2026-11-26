import SwiftUI
import YuyueCore

// 书: the record the reader keeps of each book — 在读 · 想读 · 读过 · 搁置 · 弃读, a score,
// a line of their own, tags, and how far — shared by the Mac and the phone.

/// The faint ink under the thing chosen, as the Mac's sidebar marks its row.
enum RecordInk {
    static let chosen = Color.yyDynamic(light: YYRGBA(42, 43, 46, 0.07), dark: YYRGBA(255, 255, 255, 0.08))
    static let star = Color.yyDynamic(light: YYRGBA(216, 212, 206), dark: YYRGBA(78, 76, 73))
    static let faint = Color.yyDynamic(light: YYRGBA(179, 175, 169), dark: YYRGBA(110, 108, 104))
    static let leader = Color.yyDynamic(light: YYRGBA(214, 210, 204), dark: YYRGBA(70, 68, 65))
    /// Luminous warm cream-parchment card tint: cleanly distinct from both #FFFFFF ebook cards and #F8F5F0 canvas.
    static let paperCard = Color.yyDynamic(light: YYRGBA(252, 246, 233, 1), dark: YYRGBA(36, 32, 27, 1))
    static let paperBorder = Color.yyDynamic(light: YYRGBA(176, 148, 102, 0.24), dark: YYRGBA(218, 196, 162, 0.16))
    static let paperTagBg = Color.yyDynamic(light: YYRGBA(146, 118, 76, 0.11), dark: YYRGBA(218, 196, 162, 0.14))
    static let paperTagInk = Color.yyDynamic(light: YYRGBA(122, 96, 58, 1), dark: YYRGBA(218, 198, 166, 1))
    static let paperPillBg = Color.yyDynamic(light: YYRGBA(146, 118, 76, 0.09), dark: YYRGBA(218, 196, 162, 0.12))
    static let paperPillHover = Color.yyDynamic(light: YYRGBA(146, 118, 76, 0.16), dark: YYRGBA(218, 196, 162, 0.20))
}

extension ReadingStatus: @retroactive Identifiable {
    public var id: Self { self }
}

extension BookSummary {
    /// 读到 43% · 还剩 5 小时 — 11月3日开始
    var recordLine: String {
        if record {
            if let s = startedAt, status == .reading { return String(localized: "\(Self.day(s))开始") }
            return status.name
        }
        if progress <= 0 { return readMinutes > 0 ? String(localized: "约 \(Self.hours(readMinutes))") : String(localized: "还没读") }
        return "\(String(localized: "读到 \(percent)")) · \(remaining)"
    }

    /// 约 7 小时 · 约 40 分钟
    static func hours(_ minutes: UInt32) -> String {
        let m = Int(minutes)
        if m < 60 { return String(localized: "\(max(1, m)) 分钟") }
        let h = Int((Double(m) / 60).rounded())
        return String(localized: "\(h) 小时")
    }

    /// 刚刚 · 14 小时前 · 3 天前
    static func ago(_ ms: Int64) -> String {
        let date = Date(timeIntervalSince1970: Double(ms) / 1000)
        if Date().timeIntervalSince(date) < 60 { return String(localized: "刚刚") }
        return date.formatted(.relative(presentation: .named))
    }

    /// 11月3日读完 · 10月28日开始
    static func day(_ ms: Int64) -> String {
        Date(timeIntervalSince1970: Double(ms) / 1000).formatted(.dateTime.month(.defaultDigits).day())
    }

    /// The line under a book's title when it isn't being read: when, and how it went.
    var restLine: String {
        switch status {
        case .finished:
            return finishedAt.map { String(localized: "\(Self.day($0))读完") } ?? String(localized: "读过")
        case .paused, .dropped:
            let at = record ? (startedAt ?? addedAt) : (openedAt ?? addedAt)
            let when = Date(timeIntervalSince1970: Double(at) / 1000).formatted(.relative(presentation: .named))
            let place = !record && progress > 0 ? String(localized: "停在 \(percent)") : ""
            return [place, when].filter { !$0.isEmpty }.joined(separator: " · ")
        default:
            return recordLine
        }
    }

    /// Anything kept about it beyond the file: what 删除 has to ask about.
    var hasRecord: Bool {
        rating != nil || comment != nil || !tags.isEmpty || status == .finished || status == .paused || status == .dropped || manual != nil
    }
}

// MARK: 在读 · 想读 · 读过 · 搁置 · 弃读

/// The five, each with how many: the Mac sidebar's own row — the name, a grey count — with its
/// faint ink under the one shown; how they're sorted on the right.
struct StatusTabs: View {
    @Binding var status: ReadingStatus
    let counts: [ReadingStatus: Int]
    var size: CGFloat = 12.5
    @Binding var sort: BookSort
    /// The phone has no room beside the five: its sort stays where the shelf's is.
    var showsSort = true
    @Namespace private var chosen

    var body: some View {
        HStack(spacing: 2) {
            ForEach(ReadingStatus.shelfOrder) { s in
                let on = s == status
                Button { withAnimation(YYMotion.standard) { status = s } } label: {
                    HStack(spacing: 6) {
                        Text(s.name).font(.yy(size: size, weight: on ? .semibold : .regular))
                        Text("\(counts[s] ?? 0)").font(.yy(size: 10.5).monospacedDigit()).foregroundStyle(YYColor.t3)
                    }
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, 10)
                    .frame(height: 28)
                    .background {
                        if on {
                            RoundedRectangle(cornerRadius: 8).fill(RecordInk.chosen)
                                .matchedGeometryEffect(id: "chosen", in: chosen)
                        }
                    }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
            if showsSort {
                Spacer(minLength: 8)
                sortMenu
            }
        }
    }

    private var sortMenu: some View {
            Menu {
                Picker("排序", selection: $sort) {
                    ForEach(BookSort.offered(for: status), id: \.self) { Text($0.name).tag($0) }
                }
                .pickerStyle(.inline)
                .labelsHidden()
            } label: {
                HStack(spacing: 4) {
                    Text(sort.name)
                    Image(systemName: "chevron.up.chevron.down").font(.yy(size: 9, weight: .semibold))
                }
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .fixedSize()
    }
}

/// How 书 lists the books in one status.
enum BookSort: String, CaseIterable {
    case recent, added, title, rating, color

    /// What a status offers: 颜色 only for 读过, whose books stand as strips of their covers' colour.
    static func offered(for status: ReadingStatus) -> [BookSort] {
        #if os(iOS)
        status == .finished ? allCases : allCases.filter { $0 != .color }
        #else
        allCases.filter { $0 != .color }
        #endif
    }

    var name: String {
        switch self {
        case .recent: String(localized: "最近读过")
        case .added: String(localized: "加入时间")
        case .title: String(localized: "书名")
        case .rating: String(localized: "评分")
        case .color: String(localized: "颜色")
        }
    }

    func sorted(_ books: [BookSummary]) -> [BookSummary] {
        let touched = { (b: BookSummary) in max(b.openedAt ?? 0, b.manual?.at ?? 0, b.finishedAt ?? 0, b.addedAt) }
        return switch self {
        case .recent: books.sorted { touched($0) > touched($1) }
        case .added: books.sorted { $0.addedAt > $1.addedAt }
        case .title: books.sorted { $0.title.localizedStandardCompare($1.title) == .orderedAscending }
        case .rating: books.sorted { (Int($0.rating ?? 0), touched($0)) > (Int($1.rating ?? 0), touched($1)) }
        case .color:
            books.sorted {
                let a = CoverTones.shared.colorKey($0.id), b = CoverTones.shared.colorKey($1.id)
                return a.0 != b.0 ? a.0 < b.0 : (a.1 != b.1 ? a.1 < b.1 : a.2 < b.2)
            }
        }
    }
}

/// 目录 · 简介 · 笔记: the Mac sidebar's own row — an icon, the name, a grey count — with its
/// faint ink under the one shown.
struct RecordTab: View {
    let symbol: String
    let title: String
    var count: Int?
    let on: Bool
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                Group {
                    if symbol.hasPrefix("asset.") {
                        Image(String(symbol.dropFirst(6)))
                            .resizable()
                            .renderingMode(.template)
                            .scaledToFit()
                    } else {
                        Image(systemName: symbol)
                            .font(.yy(size: 12))
                    }
                }
                .frame(width: 14, height: 14)
                .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                Text(title)
                    .font(.yy(size: 12, weight: on ? .semibold : .regular))
                    .foregroundStyle(YYColor.t1)
                if let count {
                    Text("\(count)").font(.yy(size: 10.5).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
            }
            .padding(.horizontal, 8)
            .frame(height: 28)
            .background(on ? RecordInk.chosen : .clear, in: .rect(cornerRadius: 8))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

/// A small icon button, as the sidebar's 设置 and 反馈: the ink under it when on.
struct RecordIconButton: View {
    let symbol: String
    let label: String
    var on = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.yy(size: 12))
                .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                .frame(width: 26, height: 26)
                .background(on ? RecordInk.chosen : .clear, in: .rect(cornerRadius: 7))
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(label)
        .accessibilityLabel(label)
    }
}

// MARK: Stars

/// 你的评分: five stars, tap one to give that many, the same again to take it back.
struct StarRow: View {
    let rating: UInt8?
    var size: CGFloat = 17
    var spacing: CGFloat = 3
    let set: (UInt8?) -> Void

    var body: some View {
        HStack(spacing: spacing) {
            ForEach(1...5, id: \.self) { n in
                let lit = Int(rating ?? 0) >= n
                Button {
                    set(rating == UInt8(n) ? nil : UInt8(n))
                } label: {
                    Image(systemName: "star.fill")
                        .font(.yy(size: size))
                        .foregroundStyle(lit ? YYColor.t1 : RecordInk.star)
                        .animation(YYMotion.quick, value: lit)
                        .contentShape(.rect)
                }
                .buttonStyle(YYPressStyle(scale: 0.9))
                .accessibilityLabel(String(localized: "\(n) 星"))
            }
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel(String(localized: "你的评分"))
    }
}

/// ★★★★☆, not to be tapped.
struct StarLine: View {
    let rating: UInt8
    var size: CGFloat = 11

    var body: some View {
        HStack(spacing: 1) {
            ForEach(1...5, id: \.self) { n in
                Image(systemName: "star.fill").font(.yy(size: size))
                    .foregroundStyle(Int(rating) >= n ? YYColor.t1 : RecordInk.star)
            }
        }
        .accessibilityElement()
        .accessibilityLabel(String(localized: "\(rating) 星"))
    }
}

// MARK: The menu of what to do with a book

/// 在读 ✓ · 想读 · 读过 · 搁置 · 弃读 — 放进合集… · 删除…
struct StatusMenuItems: View {
    let book: BookSummary
    let library: Library
    var pickCollection: (() -> Void)?
    var delete: (() -> Void)?

    var body: some View {
        ForEach(ReadingStatus.shelfOrder) { s in
            Button {
                library.setStatus(book, s)
            } label: {
                if s == book.status {
                    Label(s.name, systemImage: "checkmark")
                } else {
                    Label(s.name, systemImage: s.symbol)
                }
            }
        }
        if pickCollection != nil || delete != nil { Divider() }
        if let pickCollection {
            Button(String(localized: "放进合集…"), systemImage: "tray", action: pickCollection)
        }
        if let delete {
            Button(String(localized: "删除…"), systemImage: "trash", role: .destructive, action: delete)
        }
    }
}

/// 删除 a book with a record: keep the record, or let it all go.
extension View {
    func deletingRecord(_ book: Binding<BookSummary?>, library: Library) -> some View {
        confirmationDialog(
            book.wrappedValue.map { String(localized: "删除《\($0.title)》") } ?? "",
            isPresented: Binding(get: { book.wrappedValue != nil }, set: { if !$0 { book.wrappedValue = nil } }),
            titleVisibility: .visible,
            presenting: book.wrappedValue
        ) { b in
            if !b.record {
                Button(String(localized: "只删文件，留下记录")) { library.keepRecordOnly(b) }
            }
            Button(String(localized: "连记录一起删"), role: .destructive) { library.delete(b) }
            Button(String(localized: "取消"), role: .cancel) {}
        } message: { b in
            Text(b.record ? String(localized: "评分、短评和读书记录都会一起删掉。") : String(localized: "留下记录的话，评分、短评和读到哪都还在“书”里。"))
        }
    }
}

// MARK: One book, one card

/// A book on 进度: digital books sit on crisp white cards with chapter progress + 目录 / 接着读;
/// paper books sit on warm stationery cards with a 纸书 badge and status-only tracking (在读 · 想读 · 读过 · 搁置 · 弃读).
struct BookRecordCard: View {
    let book: BookSummary
    let library: Library
    var compact = false
    let show: () -> Void
    let read: () -> Void
    var pickCollection: (() -> Void)?
    var delete: (() -> Void)?
    /// The Mac: the status mark opens the app's own menu card where it was clicked, as right-clicking does.
    var menuAt: ((CGPoint) -> Void)?
    /// The Mac: 目录 lists the chapters and reading starts at the one picked (the card itself opens the book's page).
    var readAt: ((ChapterRow) -> Void)?
    @Environment(\.colorScheme) private var scheme
    @AppStorage(EInk.key) private var eink = false
    @State private var contentsShown = false
    @State private var cardHover = false
    private var tint: YYTint { YYTint.standard }

    private var rows: [ChapterRow] { library.chapters(of: book) }
    private var current: ChapterRow? { rows.first { if case .current = $0.state { true } else { false } } }

    var body: some View {
        HStack(alignment: .center, spacing: 12) {
            // Left & center body: clicking anywhere on the cover, title, author, or status line opens the book's record page.
            Button(action: show) {
                HStack(alignment: .center, spacing: 12) {
                    BookCover(book: book)
                        .frame(width: 48, height: 68)

                    VStack(alignment: .leading, spacing: 0) {
                        VStack(alignment: .leading, spacing: 3) {
                            Text(book.title)
                                .font(YYUI.serif(compact ? 14 : 15, .bold))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            bylineRow
                        }
                        Spacer(minLength: 6)
                        standing
                    }
                    .padding(.vertical, 1)
                    .frame(maxWidth: .infinity, minHeight: 72, alignment: .topLeading)
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("《\(book.title)》\(byline.isEmpty ? "" : "，\(byline)")")

            VStack(alignment: .trailing, spacing: 0) {
                if !book.record {
                    statusButton
                }
                Spacer(minLength: 8)
                quickActions
            }
            .frame(minHeight: 72)
        }
        .padding(.leading, 11)
        .padding(.trailing, 10)
        .padding(.vertical, 11)
        .frame(maxWidth: .infinity, minHeight: 94, alignment: .leading)
        .background(cardBackground, in: .rect(cornerRadius: YYRadius.block))
        .contentShape(.rect(cornerRadius: YYRadius.block))
        .onTapGesture(perform: show)
        .overlay(
            RoundedRectangle(cornerRadius: YYRadius.block)
                .strokeBorder(cardBorderColor, lineWidth: book.record ? 0.75 : 0.5)
        )
        .shadow(
            color: cardShadowColor,
            radius: eink ? 0 : (cardHover ? 9 : 5),
            y: eink ? 0 : (cardHover ? 3.5 : 2)
        )
        .offset(y: cardHover && !YYMotion.reduce ? -1 : 0)
        .animation(YYMotion.quick, value: cardHover)
        .onHover { cardHover = $0 }
        #if os(iOS)
        .contextMenu { StatusMenuItems(book: book, library: library, pickCollection: pickCollection, delete: delete) }
        #endif
        #if DEBUG
        // `-debugContents 紅樓` opens that card's 目录 once it's on screen.
        .task {
            if readAt != nil, let part = UserDefaults.standard.string(forKey: "debugContents"), book.title.contains(part) {
                try? await Task.sleep(for: .seconds(4))
                contentsShown = true
            }
        }
        #endif
    }

    private var cardBackground: Color {
        if eink { return YYColor.card }
        return book.record ? RecordInk.paperCard : YYColor.card
    }

    private var cardBorderColor: Color {
        if book.record { return RecordInk.paperBorder }
        return scheme == .dark ? Color.white.opacity(0.06) : Color(yy: YYRGBA(42, 43, 46, 0.06))
    }

    private var cardShadowColor: Color {
        if eink { return .clear }
        if scheme == .dark { return .black.opacity(cardHover ? 0.44 : 0.32) }
        return Color(yy: YYRGBA(42, 43, 46, cardHover ? 0.10 : 0.065))
    }

    @ViewBuilder private var bylineRow: some View {
        HStack(spacing: 6) {
            if !byline.isEmpty {
                Text(byline)
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(1)
            }
            if book.record {
                PaperBookTag()
            }
        }
    }

    @ViewBuilder private var statusButton: some View {
        Group {
            if let menuAt {
                StatusMark(status: book.status, showLabel: book.record)
                    .onTapGesture(coordinateSpace: .global) { menuAt($0) }
                    .accessibilityAddTraits(.isButton)
            } else {
                Menu {
                    StatusMenuItems(book: book, library: library, pickCollection: pickCollection, delete: delete)
                } label: {
                    StatusMark(status: book.status, showLabel: book.record)
                }
                .menuStyle(.button)
                .buttonStyle(.plain)
                .menuIndicator(.hidden)
                .fixedSize()
            }
        }
        .accessibilityLabel(String(localized: "改状态"))
    }

    /// Jane Austen · 14 小时前 — 史铁生
    private var byline: String {
        var parts = [book.authors.first ?? ""]
        if !book.record, let at = book.openedAt, book.status == .reading || book.status == .paused {
            parts.append(BookSummary.ago(at))
        }
        return parts.filter { !$0.isEmpty }.joined(separator: " · ")
    }

    // MARK: Where it's got to (ebooks track chapters & progress; paper books track status only)

    @ViewBuilder private var standing: some View {
        switch book.status {
        case .finished:
            VStack(alignment: .leading, spacing: 3) {
                HStack(spacing: 5) {
                    if let r = book.rating {
                        StarLine(rating: r, size: 10)
                        Text("·").foregroundStyle(YYColor.t3.opacity(0.6))
                    }
                    Text(book.restLine)
                }
                .font(.yy(size: 11).monospacedDigit())
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
                if let c = book.comment {
                    Text(c)
                        .font(YYUI.serif(11))
                        .foregroundStyle(YYColor.t2)
                        .lineLimit(1)
                }
            }
        case .want:
            Text(wantLine)
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
        default:
            if book.record {
                paperStatusLine
            } else {
                VStack(alignment: .leading, spacing: 5) {
                    progressMetaLine
                    ChapterBar(
                        rows: rows,
                        progress: book.progress,
                        height: 3.5,
                        muted: book.status == .paused || book.status == .dropped
                    )
                    .frame(maxWidth: 168, alignment: .leading)
                }
            }
        }
    }

    @ViewBuilder private var paperStatusLine: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(paperTimeText)
                .font(.yy(size: 11).monospacedDigit())
                .foregroundStyle(YYColor.t3)
                .lineLimit(1)
            if let c = book.comment, !c.isEmpty {
                Text(c)
                    .font(YYUI.serif(11))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(1)
            }
        }
    }

    private var paperTimeText: String {
        if book.status == .reading, let s = book.startedAt {
            return String(localized: "\(BookSummary.day(s))开始")
        }
        return BookSummary.ago(book.addedAt) + String(localized: "加的")
    }

    @ViewBuilder private var progressMetaLine: some View {
        let lead = book.status == .reading
            ? String(localized: "读到 \(compactWhereLine)")
            : String(localized: "停在 \(book.percent)")
        Group {
            if leftLine.isEmpty {
                Text(lead).foregroundStyle(tint.accent).fontWeight(.semibold)
            } else {
                Text(lead).foregroundStyle(tint.accent).fontWeight(.semibold)
                    + Text(" · ").foregroundStyle(YYColor.t3.opacity(0.55))
                    + Text(leftLine).foregroundStyle(YYColor.t3)
            }
        }
        .font(.yy(size: 11).monospacedDigit())
        .lineLimit(1)
    }

    /// 第 8 章 — 13% (short enough to share the compact horizontal line with remaining time)
    private var compactWhereLine: String {
        guard let current else { return book.percent }
        return ChapterTitle(current.title).number ?? String(localized: "第 \(current.number) 章")
    }

    /// 共 3 章 · 约 7 小时 — 昨天加的
    private var wantLine: String {
        if book.record {
            return BookSummary.ago(book.addedAt) + String(localized: "加的")
        }
        var parts: [String] = []
        if !rows.isEmpty { parts.append(String(localized: "共 \(rows.count) 章")) }
        if book.readMinutes > 0 { parts.append(String(localized: "约 \(BookSummary.hours(book.readMinutes))")) }
        return parts.isEmpty ? String(localized: "还没读") : parts.joined(separator: " · ")
    }

    // MARK: Compact bottom-right quick actions: ebooks get 目录 + 接着读; paper books get the 5-status switcher pill

    @ViewBuilder private var quickActions: some View {
        HStack(spacing: 4) {
            if book.record {
                if book.status == .finished, book.rating == nil {
                    Button(String(localized: "打分"), action: show)
                        .buttonStyle(CompactCardButton(main: false))
                }
                statusButton
            } else {
                switch book.status {
                case .finished:
                    if book.rating == nil {
                        Button(String(localized: "打分"), action: show)
                            .buttonStyle(CompactCardButton(main: false))
                    }
                case .want:
                    contentsIconButton
                    Button(String(localized: "开始读"), action: read)
                        .buttonStyle(CompactCardButton(main: true))
                default:
                    contentsIconButton
                    Button(String(localized: "接着读"), action: read)
                        .buttonStyle(CompactCardButton(main: true))
                }
            }
        }
    }

    /// Compact 目录 icon button: opens ChapterJumpList popover on Mac, or the book's page elsewhere.
    @ViewBuilder private var contentsIconButton: some View {
        if let readAt, !rows.isEmpty {
            CompactTocButton { contentsShown = true }
                .popover(isPresented: $contentsShown, arrowEdge: .trailing) {
                    ChapterJumpList(title: book.title, rows: rows) { row in
                        contentsShown = false
                        readAt(row)
                    }
                }
        } else if !rows.isEmpty {
            CompactTocButton(action: show)
        }
    }

    /// 还剩约 8 小时 — 昨天记的
    private var leftLine: String {
        switch book.status {
        case .reading, .paused, .dropped:
            if book.record {
                return book.manual.map { BookSummary.ago($0.at) + String(localized: "记的") } ?? ""
            }
            return book.progress > 0 ? book.remaining : ""
        case .want:
            return BookSummary.ago(book.addedAt) + String(localized: "加的")
        case .finished:
            return ""
        }
    }
}

/// Compact 24pt action button inside BookRecordCard (接着读 / 开始读 / 记进度 / 打分).
private struct CompactCardButton: ButtonStyle {
    var main: Bool
    @State private var hover = false

    func makeBody(configuration: Configuration) -> some View {
        let tint = YYTint.standard
        configuration.label
            .font(.yy(size: 11, weight: main ? .semibold : .medium).monospacedDigit())
            .foregroundStyle(main ? tint.onBlack : (hover ? YYColor.t1 : YYColor.t2))
            .lineLimit(1)
            .padding(.horizontal, 9)
            .frame(height: 24)
            .background(
                main ? tint.black : (hover ? YYColor.inset : YYColor.card),
                in: .rect(cornerRadius: 7)
            )
            .overlay(
                RoundedRectangle(cornerRadius: 7)
                    .strokeBorder(main ? tint.edge : YYColor.line, lineWidth: 0.5)
            )
            .brightness(main && hover ? -0.02 : 0)
            .contentShape(.rect(cornerRadius: 7))
            .scaleEffect(configuration.isPressed && !YYMotion.reduce ? 0.95 : 1)
            .animation(.easeOut(duration: 0.09), value: configuration.isPressed)
            .onHover { hover = $0 }
    }
}

/// Compact 24×24 目录 icon trigger inside BookRecordCard.
private struct CompactTocButton: View {
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Image(systemName: "list.bullet")
                .font(.yy(size: 11, weight: .medium))
                .foregroundStyle(hover ? YYColor.t1 : YYColor.t2)
                .frame(width: 24, height: 24)
                .background(hover ? YYColor.inset : YYColor.card, in: .rect(cornerRadius: 7))
                .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.line, lineWidth: 0.5))
                .contentShape(.rect(cornerRadius: 7))
        }
        .buttonStyle(YYPressStyle(scale: 0.92))
        .onHover { hover = $0 }
        .help(String(localized: "目录"))
        .accessibilityLabel(String(localized: "目录"))
    }
}

/// The card's corner: what the book is now (在读's book, 想读's bookmark…) and a small ⌄, a pad
/// under it on hover; paper books show the status name directly so switching 在读/读过/搁置/弃读/想读
/// is front and center.
private struct StatusMark: View {
    let status: ReadingStatus
    var showLabel = false
    @State private var hover = false

    var body: some View {
        HStack(spacing: showLabel ? 4 : 3) {
            if !showLabel {
                Image(systemName: status.symbol).font(.yy(size: 11.5, weight: .medium))
            }
            if showLabel {
                Text(status.name).font(.yy(size: 11, weight: .medium))
            }
            Image(systemName: "chevron.down").font(.yy(size: 7.5, weight: .bold))
        }
        .foregroundStyle(hover ? YYColor.t1 : (showLabel ? RecordInk.paperTagInk : YYColor.t3))
        .padding(.horizontal, showLabel ? 9 : 0)
        .frame(minWidth: showLabel ? nil : 34, minHeight: 24, maxHeight: 24)
        .background(
            showLabel ? (hover ? RecordInk.paperPillHover : RecordInk.paperPillBg) : (hover ? YYColor.inset : .clear),
            in: .rect(cornerRadius: 7)
        )
        .overlay {
            if showLabel {
                RoundedRectangle(cornerRadius: 7)
                    .strokeBorder(RecordInk.paperBorder, lineWidth: 0.5)
            }
        }
        .contentShape(.rect)
        .onHover { hover = $0 }
        .help(status.name)
    }
}

/// Warm pill badge marking paper / external books so they stand apart from readable ebooks at a glance.
struct PaperBookTag: View {
    var body: some View {
        HStack(spacing: 3) {
            Image(systemName: "book.closed")
                .font(.yy(size: 8.5, weight: .semibold))
            Text("纸书")
                .font(.yy(size: 10, weight: .medium))
        }
        .foregroundStyle(RecordInk.paperTagInk)
        .padding(.horizontal, 5.5)
        .frame(height: 17)
        .background(RecordInk.paperTagBg, in: .rect(cornerRadius: 4.5))
        .fixedSize()
    }
}

/// 目录 from a card, laid out as a book's contents page: the number, the title in 宋体, a dotted
/// leader and how long it takes. To find one fast: type a number or part of a title (↩ opens the
/// first match); long books get 1–20 · 21–40 … to jump by; the bottom goes back to where reading is.
private struct ChapterJumpList: View {
    let title: String
    let rows: [ChapterRow]
    let pick: (ChapterRow) -> Void
    @State private var query = ""
    @FocusState private var searching: Bool
    private var tint: YYTint { YYTint.standard }

    private var current: ChapterRow? { rows.first { if case .current = $0.state { true } else { false } } }
    private var readCount: Int { rows.count { $0.state == .read } }
    /// 回 for a book whose chapters are 第一回…, otherwise 章.
    private var unit: String {
        let n = rows.lazy.compactMap { ChapterTitle($0.title).number }.first
        return n?.last.map(String.init).flatMap { ["回", "节", "卷", "篇"].contains($0) ? $0 : nil } ?? "章"
    }
    private var totalText: String {
        switch unit {
        case "节": String(localized: "共 \(rows.count) 节")
        case "卷": String(localized: "共 \(rows.count) 卷")
        case "篇": String(localized: "共 \(rows.count) 篇")
        case "回": String(localized: "共 \(rows.count) 回")
        default: String(localized: "共 \(rows.count) 章")
        }
    }
    private var readText: String {
        switch unit {
        case "节": String(localized: "读过 \(readCount) 节")
        case "卷": String(localized: "读过 \(readCount) 卷")
        case "篇": String(localized: "读过 \(readCount) 篇")
        case "回": String(localized: "读过 \(readCount) 回")
        default: String(localized: "读过 \(readCount) 章")
        }
    }
    private func isCurrent(_ r: ChapterRow) -> Bool { if case .current = r.state { true } else { false } }

    private var shown: [ChapterRow] {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return rows }
        if let n = Int(q) { return rows.filter { $0.number == n || String($0.number).hasPrefix(q) || $0.title.contains(q) } }
        return rows.filter { $0.title.localizedCaseInsensitiveContains(q) }
    }

    /// 1–20, 21–40 …: tens for a middling book, twenties for a long one, none for a short one.
    private var ranges: [ClosedRange<Int>] {
        guard rows.count > 30 else { return [] }
        let step = rows.count > 80 ? 20 : 10
        return stride(from: 0, to: rows.count, by: step).map { $0...min($0 + step, rows.count) - 1 }
    }

    var body: some View {
        ScrollViewReader { proxy in
            VStack(alignment: .leading, spacing: 0) {
                head
                search.padding(.horizontal, 14).padding(.top, 10)
                if query.isEmpty && !ranges.isEmpty {
                    rangeBar(proxy).padding(.top, 8)
                }
                Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.top, 10)
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(Array(shown.enumerated()), id: \.element.id) { i, row in
                            ChapterJumpRow(row: row, tint: tint) { pick(row) }.id(row.id)
                            if i + 1 < shown.count, !isCurrent(row), !isCurrent(shown[i + 1]) {
                                Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 8)
                            }
                        }
                        if shown.isEmpty {
                            Text("没有找到「\(query)」").font(.yy(size: 12)).foregroundStyle(YYColor.t3).padding(.vertical, 28)
                        }
                    }
                    .padding(.horizontal, 8).padding(.vertical, 6)
                }
                Rectangle().fill(YYColor.line).frame(height: 0.5)
                foot(proxy)
            }
            .frame(width: 360, height: 460)
            .background(YYColor.card)
            .onAppear {
                searching = true
                // The chapter being read in view; near the start the top already shows it.
                if let current, let i = rows.firstIndex(where: { $0.id == current.id }), i > 6 {
                    Task { proxy.scrollTo(current.id, anchor: .center) }
                }
            }
        }
    }

    private var head: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("目录").font(.yy(size: 15, weight: .semibold)).foregroundStyle(YYColor.t1)
            Text(title).font(.yy(size: 11.5)).foregroundStyle(YYColor.t3).lineLimit(1)
            Spacer(minLength: 4)
            Text(totalText).font(.yy(size: 11)).monospacedDigit().foregroundStyle(YYColor.t3)
        }
        .padding(.horizontal, 16).padding(.top, 14)
    }

    private var search: some View {
        HStack(spacing: 6) {
            Image(systemName: "magnifyingglass").font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
            TextField(String(localized: "第几章，或章名里的字"), text: $query)
                .textFieldStyle(.plain)
                .font(.yy(size: 12.5))
                .focused($searching)
                .onSubmit { if let first = shown.first { pick(first) } }
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 10).frame(height: 30)
        .background(YYColor.inset, in: .rect(cornerRadius: 8))
    }

    private func rangeBar(_ proxy: ScrollViewProxy) -> some View {
        ScrollView(.horizontal) {
            HStack(spacing: 2) {
                ForEach(ranges, id: \.lowerBound) { r in
                    let here = current.map { r.contains($0.number - 1) } ?? false
                    Button {
                        withAnimation(.smooth(duration: 0.25)) { proxy.scrollTo(rows[r.lowerBound].id, anchor: .top) }
                    } label: {
                        HStack(spacing: 4) {
                            if here { Circle().fill(tint.accent).frame(width: 5, height: 5) }
                            Text("\(rows[r.lowerBound].number)–\(rows[r.upperBound].number)")
                        }
                        .font(.yy(size: 11).monospacedDigit())
                        .foregroundStyle(here ? tint.accent : YYColor.t2)
                        .padding(.horizontal, 8).frame(height: 24)
                        .contentShape(.rect)
                    }
                    .buttonStyle(HoverPadStyle())
                }
            }
            .padding(.horizontal, 10)
        }
        .scrollIndicators(.never)
    }

    private func foot(_ proxy: ScrollViewProxy) -> some View {
        HStack(spacing: 6) {
            Text(readText).font(.yy(size: 11)).monospacedDigit().foregroundStyle(YYColor.t3)
            Spacer()
            if let current {
                Button {
                    query = ""
                    withAnimation(.smooth(duration: 0.25)) { proxy.scrollTo(current.id, anchor: .center) }
                } label: {
                    Label(String(localized: "读到的地方 · ") + (ChapterTitle(current.title).number ?? String(localized: "第 \(current.number) 章")), systemImage: "location")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(tint.accent)
                        .padding(.horizontal, 8).frame(height: 24)
                        .contentShape(.rect)
                }
                .buttonStyle(HoverPadStyle())
            }
        }
        .padding(.leading, 16).padding(.trailing, 8).frame(height: 38)
    }
}

/// Text only; a pale pad under it while the pointer is on it.
private struct HoverPadStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Pad(configuration: configuration) }

    private struct Pad: View {
        let configuration: ButtonStyleConfiguration
        @State private var hover = false
        var body: some View {
            configuration.label
                .background(hover || configuration.isPressed ? YYColor.inset : .clear, in: .rect(cornerRadius: 6))
                .onHover { hover = $0 }
        }
    }
}

/// One chapter, as the reader's own 目录 draws it: 第一回 in a column, the name, and under it
/// 约 17 分钟 (翻过 once past); the one being read on the theme's wash with how far it's got.
private struct ChapterJumpRow: View {
    let row: ChapterRow
    let tint: YYTint
    let action: () -> Void
    @State private var hover = false

    private var within: Double? { if case .current(let f) = row.state { f } else { nil } }
    private var current: Bool { within != nil }
    private var past: Bool { row.state == .read }

    var body: some View {
        let t = ChapterTitle(row.title)
        Button(action: action) {
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(t.number ?? String(localized: "第 \(row.number) 章"))
                    .font(.yy(size: 11, weight: current ? .semibold : .regular))
                    .monospacedDigit()
                    .foregroundStyle(current ? tint.accent : past ? YYColor.t3 : YYColor.t2)
                    .lineLimit(1)
                    .frame(minWidth: 52, alignment: .leading)
                    .fixedSize(horizontal: true, vertical: false)
                VStack(alignment: .leading, spacing: 3) {
                    Text(t.name.isEmpty ? (t.number ?? String(localized: "第 \(row.number) 章")) : t.name)
                        .font(.yy(size: current ? 12.5 : 12, weight: current ? .semibold : .regular))
                        .lineLimit(current ? 2 : 1)
                        .foregroundStyle(current ? tint.accent : past ? YYColor.t3 : YYColor.t1)
                        .multilineTextAlignment(.leading)
                    Text(note)
                        .font(.yy(size: 10.5, weight: current ? .medium : .regular))
                        .foregroundStyle(current ? tint.accent : YYColor.t3)
                        .monospacedDigit()
                        .lineLimit(1)
                    if let within {
                        ProgressLine(value: within, height: 2.5).padding(.top, 5)
                    }
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 8)
            .padding(.top, current ? 11 : 9)
            .padding(.bottom, current ? 12 : 9)
            .background(current ? tint.accentSoft : hover ? YYColor.inset : .clear, in: .rect(cornerRadius: 11))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
    }

    private var note: String {
        if let within {
            let left = max(1, Int((Double(row.minutes) * (1 - within)).rounded()))
            return String(localized: "读到 \(Int((within * 100).rounded()))% · 还剩 \(left) 分钟")
        }
        if past { return String(localized: "翻过") }
        return row.minutes > 0 ? String(localized: "约 \(row.minutes) 分钟") : ""
    }
}

/// How far a book has got, a chapter at a time: read chapters in the theme's colour, the one
/// being read filled as far as it's got, the rest pale. A book of one chapter, or of very many
/// (红楼梦's 120 回), is one line.
struct ChapterBar: View {
    let rows: [ChapterRow]
    let progress: Double
    var height: CGFloat = 5
    var muted = false
    private var tint: YYTint { YYTint.standard }
    private static let empty = Color.yyDynamic(light: YYRGBA(235, 233, 229), dark: YYRGBA(54, 55, 58))
    private var fillColor: Color { muted ? YYColor.t3.opacity(0.55) : tint.fill }

    var body: some View {
        Canvas { ctx, size in
            guard size.width > 0, size.height > 0 else { return }
            let emptyShading = GraphicsContext.Shading.color(Self.empty)
            let fillShading = GraphicsContext.Shading.color(fillColor)
            if rows.count >= 2 && rows.count <= 40 {
                let count = rows.count
                let spacing: CGFloat = 2
                let segW = max(1, (size.width - spacing * CGFloat(count - 1)) / CGFloat(count))
                for (i, row) in rows.enumerated() {
                    let x = CGFloat(i) * (segW + spacing)
                    let bgRect = CGRect(x: x, y: 0, width: segW, height: size.height)
                    let clipPath = Path(roundedRect: bgRect, cornerRadius: 2)
                    ctx.fill(clipPath, with: emptyShading)
                    switch row.state {
                    case .read:
                        ctx.fill(clipPath, with: fillShading)
                    case .current(let f):
                        let fw = segW * max(0.15, f)
                        ctx.drawLayer { layer in
                            layer.clip(to: clipPath)
                            layer.fill(Path(CGRect(x: x, y: 0, width: fw, height: size.height)), with: fillShading)
                        }
                    case .unread:
                        break
                    }
                }
            } else {
                let radius = size.height / 2
                let fullRect = CGRect(origin: .zero, size: size)
                ctx.fill(Path(roundedRect: fullRect, cornerRadius: radius), with: emptyShading)
                let fw = min(size.width, max(5, size.width * min(1, progress)))
                ctx.fill(Path(roundedRect: CGRect(x: 0, y: 0, width: fw, height: size.height), cornerRadius: radius), with: fillShading)
            }
        }
        .frame(height: height)
        .accessibilityElement()
        .accessibilityValue("\(Int((progress * 100).rounded()))%")
    }
}

/// A thin line, filled as far as the book has got.
struct RecordProgressLine: View {
    let value: Double
    var height: CGFloat = 3

    var body: some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(YYColor.line)
                Capsule().fill(YYTint.standard.fill).frame(width: max(height, g.size.width * min(1, value)))
            }
        }
        .frame(height: height)
        .accessibilityElement()
        .accessibilityValue("\(Int((value * 100).rounded()))%")
    }
}

/// The buttons on 书: the main one the theme's wash with its colour, the other warm white.
/// Rounded squares, not pills.
struct RecordButton: ButtonStyle {
    var main: Bool
    var compact = false
    var big = false

    func makeBody(configuration: Configuration) -> some View {
        let tint = YYTint.standard
        let height: CGFloat = big ? 44 : (compact ? 28 : 32)
        let radius: CGFloat = big ? 12 : (compact ? 9 : 10)
        configuration.label
            .labelStyle(RecordLabelStyle())
            .font(.yy(size: big ? 14.5 : (compact ? 12 : 13), weight: main ? .semibold : .regular).monospacedDigit())
            .foregroundStyle(main ? tint.onBlack : YYColor.t1)
            .lineLimit(1)
            .minimumScaleFactor(0.85)
            .padding(.horizontal, big ? 18 : (compact ? 10 : 12))
            .frame(height: height)
            .background(main ? tint.black : YYColor.pill, in: .rect(cornerRadius: radius))
            .overlay(RoundedRectangle(cornerRadius: radius).strokeBorder(main ? tint.edge : YYColor.pillLine, lineWidth: 0.5))
            .contentShape(.rect(cornerRadius: radius))
            .scaleEffect(configuration.isPressed ? 0.97 : 1)
            .animation(.easeOut(duration: 0.1), value: configuration.isPressed)
    }
}

private struct RecordLabelStyle: LabelStyle {
    func makeBody(configuration: Configuration) -> some View {
        HStack(spacing: 4) {
            configuration.icon.font(.yy(size: 11, weight: .semibold))
            configuration.title
        }
    }
}

// MARK: 目录

/// One chapter of the book's contents, and where the reader stands in it.
struct ChapterRow: Identifiable {
    enum State: Equatable { case read, current(Double), unread }
    let id: Int
    let number: Int
    let title: String
    let section: UInt32
    let minutes: Int
    let state: State
}

enum Chapters {
    /// The chapters from the book's contents (top level), or its sections when it has none.
    static func rows(_ detail: BookDetail, at position: Position?) -> [ChapterRow] {
        let sections = detail.sections
        // From where reading starts: the title page and the book's own contents page aren't chapters.
        let first = detail.start
        let isText = { (i: UInt32) in sections.indices.contains(Int(i)) && sections[Int(i)].kind == .text && sections[Int(i)].linear }
        let contentsPage = ["目录", "目次", "contents", "table of contents"]
        var starts: [(String, UInt32)] = detail.toc
            .filter { $0.depth == 0 && $0.section >= first && isText($0.section) }
            .filter { !contentsPage.contains($0.title.trimmingCharacters(in: .whitespaces).lowercased()) }
            // The title page (“The Great Gatsby by F. Scott Fitzgerald”) isn't a chapter.
            .filter { !$0.title.localizedCaseInsensitiveContains(detail.summary.title) || $0.title.count > detail.summary.title.count * 3 }
            .map { ($0.title, $0.section) }
        if starts.count < 2 {
            starts = sections.enumerated().compactMap { i, s in
                guard UInt32(i) >= first, s.linear, s.kind == .text, !s.continued else { return nil }
                return (s.title ?? String(localized: "第 \(i + 1) 节"), UInt32(i))
            }
        }
        // In order, one entry per start.
        var seen = Set<UInt32>()
        starts = starts.sorted { $0.1 < $1.1 }.filter { seen.insert($0.1).inserted }
        let units = sections.map { Double(max(1, $0.units)) }
        let all = max(1, units.reduce(0, +))
        let minutesPerUnit = Double(detail.summary.readMinutes) / all
        let at = position.map { Int($0.section) } ?? -1
        return starts.enumerated().map { i, start in
            let from = Int(start.1)
            let to = i + 1 < starts.count ? Int(starts[i + 1].1) : sections.count
            let span = from < to ? Array(from..<to) : [from]
            let spanUnits = span.reduce(0.0) { $0 + (units.indices.contains($1) ? units[$1] : 0) }
            let state: ChapterRow.State
            if detail.summary.status == .finished || at >= to {
                state = .read
            } else if at >= from, let position {
                let before = span.filter { $0 < at }.reduce(0.0) { $0 + units[$1] }
                let inSection = Double(position.sentence) / Double(max(1, sections[at].sentences))
                state = .current(min(0.99, (before + inSection * units[at]) / max(1, spanUnits)))
            } else {
                state = .unread
            }
            return ChapterRow(id: i, number: i + 1, title: start.0, section: start.1,
                              minutes: max(1, Int((spanUnits * minutesPerUnit).rounded())), state: state)
        }
    }
}

/// 目录 as the page at the front of a book: the title in 宋体, a run of dots, and on the right
/// what it comes to. Read chapters a small tick before them; the one being read a line.
struct RecordChapterList: View {
    let rows: [ChapterRow]
    var expanded: Bool
    var fontSize: CGFloat = 13.5
    let expand: () -> Void
    let open: (ChapterRow) -> Void
    private var tint: YYTint { YYTint.standard }

    private var shown: [ChapterRow] {
        guard !expanded, rows.count > 9 else { return rows }
        let cur = rows.firstIndex { if case .current = $0.state { true } else { false } }
            ?? rows.lastIndex { $0.state == .read }.map { $0 + 1 } ?? 0
        let from = max(0, min(cur - 3, rows.count - 8))
        return Array(rows[from..<min(rows.count, from + 8)])
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(shown) { row in
                Button { open(row) } label: { line(row) }
                    .buttonStyle(.plain)
            }
            if !expanded, rows.count > shown.count {
                Button(action: expand) {
                    Text("展开全部 \(rows.count) 章")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(YYColor.t3)
                        .padding(.leading, 30)
                        .frame(height: 30)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.leading, 26)
    }

    private func line(_ row: ChapterRow) -> some View {
        let current: Double? = if case .current(let f) = row.state { f } else { nil }
        let read = row.state == .read
        return HStack(spacing: 12) {
            Text("\(row.number)")
                .font(.yy(size: 11, weight: current != nil ? .semibold : .regular).monospacedDigit())
                .foregroundStyle(current != nil ? tint.accent : RecordInk.faint)
                .frame(width: 18, alignment: .trailing)
            Text(row.title)
                .font(YYUI.serif(fontSize, current != nil ? .bold : .regular))
                .foregroundStyle(read ? RecordInk.faint : YYColor.t1)
                .lineLimit(1)
                .layoutPriority(1)
            Dots().frame(minWidth: 16)
            Group {
                if let current {
                    Text("读到 \(Int((current * 100).rounded()))%").fontWeight(.semibold).foregroundStyle(tint.accent)
                } else if read {
                    Text("读过").foregroundStyle(RecordInk.faint)
                } else {
                    Text("\(row.minutes) 分钟").foregroundStyle(RecordInk.faint)
                }
            }
            .font(.yy(size: 11).monospacedDigit())
        }
        .frame(height: 31)
        .overlay(alignment: .leading) {
            if read {
                Image(systemName: "checkmark")
                    .font(.yy(size: 9.5, weight: .bold))
                    .foregroundStyle(tint.fill)
                    .offset(x: -26)
            } else if current != nil {
                Capsule().fill(tint.accent).frame(width: 2.5, height: 13).offset(x: -14)
            }
        }
        .contentShape(.rect)
    }
}

/// The run of dots between a chapter's title and its number.
private struct Dots: View {
    var body: some View {
        GeometryReader { g in
            Path { p in
                var x: CGFloat = 1
                while x < g.size.width {
                    p.addEllipse(in: CGRect(x: x, y: g.size.height / 2 + 3, width: 1.2, height: 1.2))
                    x += 4
                }
            }
            .fill(RecordInk.leader)
        }
        .frame(height: 14)
    }
}

/// 目录 as squares: read ones the theme's wash, the one being read solid, the rest white.
/// Pointing at one shows its chapter's name and how long it takes; a key to the colours below.
struct ChapterGrid: View {
    let rows: [ChapterRow]
    var cell: CGFloat = 30
    let open: (ChapterRow) -> Void
    @State private var hovered: Int?
    private var tint: YYTint { YYTint.standard }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            LazyVGrid(columns: [GridItem(.adaptive(minimum: cell, maximum: cell + 6), spacing: 6)], alignment: .leading, spacing: 6) {
                ForEach(rows) { row in
                    Button { open(row) } label: { square(row) }
                        .buttonStyle(YYPressStyle(scale: 0.94))
                        .onHover { hovered = $0 ? row.id : (hovered == row.id ? nil : hovered) }
                        .overlay(alignment: .top) {
                            if hovered == row.id { tip(row).offset(y: cell + 8).zIndex(2) }
                        }
                        .zIndex(hovered == row.id ? 1 : 0)
                        .accessibilityLabel("\(row.number) · \(row.title)")
                }
            }
            if let cur = rows.first(where: { if case .current = $0.state { true } else { false } }), case .current(let f) = cur.state {
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text("\(cur.number)").font(.yy(size: 12, weight: .semibold).monospacedDigit()).foregroundStyle(tint.accent)
                    Text(cur.title).font(YYUI.serif(13.5, .bold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Text("读到 \(Int((f * 100).rounded()))%").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                }
            }
            HStack(spacing: 16) {
                key(String(localized: "读过"), fill: tint.black, edge: tint.edge)
                key(String(localized: "读到"), fill: tint.fill, edge: .clear)
                key(String(localized: "没读"), fill: YYColor.card, edge: YYColor.line)
            }
        }
    }

    private func key(_ name: String, fill: Color, edge: Color) -> some View {
        HStack(spacing: 6) {
            RoundedRectangle(cornerRadius: 3).fill(fill)
                .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(edge, lineWidth: 0.5))
                .frame(width: 12, height: 12)
            Text(name).font(.yy(size: 11)).foregroundStyle(YYColor.t3)
        }
    }

    /// 第 22 章 · 面壁者  约 18 分钟
    private func tip(_ row: ChapterRow) -> some View {
        HStack(spacing: 6) {
            Text("第 \(row.number) 章 · \(row.title)").font(YYUI.serif(11.5, .bold)).foregroundStyle(YYColor.t1)
            Text("约 \(row.minutes) 分钟").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
        }
        .lineLimit(1)
        .fixedSize()
        .padding(.horizontal, 9)
        .padding(.vertical, 5)
        .background(YYColor.card, in: .rect(cornerRadius: 8))
        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(YYColor.line, lineWidth: 0.5))
        .shadow(color: .black.opacity(0.14), radius: 8, y: 4)
        .allowsHitTesting(false)
    }

    private func square(_ row: ChapterRow) -> some View {
        let current = if case .current = row.state { true } else { false }
        let read = row.state == .read
        return Text("\(row.number)")
            .font(.yy(size: 11, weight: current ? .bold : .regular).monospacedDigit())
            .foregroundStyle(current ? .white : (read ? tint.accent : YYColor.t3))
            .frame(width: cell, height: cell)
            .background(current ? tint.fill : (read ? tint.black : YYColor.card), in: .rect(cornerRadius: 7))
            .overlay {
                RoundedRectangle(cornerRadius: 7)
                    .strokeBorder(hovered == row.id ? tint.accent : (current ? .clear : (read ? tint.edge : YYColor.line)), lineWidth: hovered == row.id ? 1.5 : 0.5)
            }
    }
}

// MARK: 读完了

/// At the last page: how long it took, a score and a line if you like, then 记为读过 — or
/// 还没读完, and it isn't asked again this reading.
struct FinishedSheet: View {
    let library: Library
    let bookID: String
    let done: () -> Void
    @State private var rating: UInt8?
    @State private var comment = ""

    private var book: BookSummary? { library.book(bookID) }

    var body: some View {
        VStack(spacing: 0) {
            Text("读完了《\(book?.title ?? "")》")
                .font(.yy(size: 17, weight: .semibold))
                .foregroundStyle(YYColor.t1)
                .multilineTextAlignment(.center)
            if let line = took {
                Text(line).font(.yy(size: 12.5)).foregroundStyle(YYColor.t2).padding(.top, 4)
            }
            StarRow(rating: rating, size: 28, spacing: 12) { rating = $0 }
                .padding(.top, 18)
            TextField(String(localized: "写一句短评（可以不写）"), text: $comment, axis: .vertical)
                .font(YYUI.serif(13.5))
                .lineLimit(2...5)
                .textFieldStyle(.plain)
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYColor.card, in: .rect(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(YYColor.line, lineWidth: 0.5))
                .padding(.top, 16)
            HStack(spacing: 8) {
                Button { done() } label: { Text("还没读完").frame(maxWidth: .infinity) }
                    .buttonStyle(RecordButton(main: false, big: true))
                    .keyboardShortcut(.cancelAction)
                Button {
                    if let book {
                        library.setStatus(book, .finished)
                        if let rating { library.setRating(book, rating) }
                        let text = comment.trimmingCharacters(in: .whitespacesAndNewlines)
                        if !text.isEmpty { library.setComment(book, text) }
                    }
                    done()
                } label: { Text("记为读过").frame(maxWidth: .infinity) }
                    .buttonStyle(RecordButton(main: true, big: true))
                    .keyboardShortcut(.defaultAction)
            }
            .padding(.top, 14)
        }
        .padding(20)
        .frame(maxWidth: 420)
        .background(YYColor.bg)
        .onAppear { rating = book?.rating; comment = book?.comment ?? "" }
    }

    /// 12 天 · 9 小时 · 21 条笔记
    private var took: String? {
        guard let book else { return nil }
        var parts: [String] = []
        if let started = book.startedAt {
            let days = max(1, Int((Date().timeIntervalSince1970 * 1000 - Double(started)) / 86_400_000) + 1)
            parts.append(String(localized: "\(days) 天"))
        }
        let spent = library.timeSpent(on: book)
        if spent.read + spent.listen > 0 { parts.append(BookRecordPage.duration(spent.read + spent.listen)) }
        let notes = library.noteCount(of: book)
        if notes > 0 { parts.append(String(localized: "\(notes) 条笔记")) }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }
}

// MARK: - 年度阅读热力图

/// Pure Monday-aligned annual reading heatmap model shared by iOS and macOS Reading Stats.
nonisolated struct ReadingHeatmap: Equatable, Sendable {
    struct Cell: Equatable, Sendable {
        let dayKey: String
        let date: Date?
        let readSeconds: Int
        let listenSeconds: Int
        let isToday: Bool
        let isFuture: Bool

        var totalSeconds: Int { readSeconds + listenSeconds }
        var level: Int { ReadingHeatmap.level(for: totalSeconds) }
    }

    struct MonthMarker: Equatable, Sendable {
        let weekIndex: Int
        let month: Int
    }

    let weeks: [[Cell]]
    let monthMarkers: [MonthMarker]
    let activeDays: Int
    let longestStreak: Int

    static func level(for totalSeconds: Int) -> Int {
        switch totalSeconds {
        case ..<1: 0
        case ..<900: 1
        case ..<1800: 2
        case ..<3600: 3
        default: 4
        }
    }

    init(yearDays: [Date], times: [DayTime], now: Date = Date(), calendar: Calendar = .current) {
        guard let first = yearDays.first else {
            self.weeks = []
            self.monthMarkers = []
            self.activeDays = 0
            self.longestStreak = 0
            return
        }
        var byDay: [String: (read: Int, listen: Int)] = [:]
        for t in times {
            var cur = byDay[t.day] ?? (0, 0)
            cur.read += Int(t.readSeconds)
            cur.listen += Int(t.listenSeconds)
            byDay[t.day] = cur
        }

        let formatter = DateFormatter()
        formatter.calendar = Calendar(identifier: .gregorian)
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = calendar.timeZone
        formatter.dateFormat = "yyyy-MM-dd"

        let weekday = calendar.component(.weekday, from: first) // 1 = Sun ... 7 = Sat
        let mondayOffset = (weekday + 5) % 7
        var flat: [Cell] = (0 ..< mondayOffset).map { idx in
            Cell(dayKey: "pad-lead-\(idx)", date: nil, readSeconds: 0, listenSeconds: 0, isToday: false, isFuture: false)
        }

        var active = 0
        var bestStreak = 0
        var run = 0
        var markers: [MonthMarker] = []
        var lastMarkerWeek = -10

        for d in yearDays {
            let key = formatter.string(from: d)
            let sums = byDay[key] ?? (0, 0)
            let isToday = calendar.isDate(d, inSameDayAs: now)
            let isFuture = !isToday && d > now
            let cell = Cell(dayKey: key, date: d, readSeconds: sums.read, listenSeconds: sums.listen, isToday: isToday, isFuture: isFuture)
            let weekIdx = flat.count / 7
            if calendar.component(.day, from: d) == 1 {
                let m = calendar.component(.month, from: d)
                if weekIdx - lastMarkerWeek >= 2 {
                    markers.append(MonthMarker(weekIndex: weekIdx, month: m))
                    lastMarkerWeek = weekIdx
                }
            }
            flat.append(cell)
            if !isFuture {
                if cell.totalSeconds > 0 {
                    active += 1
                    run += 1
                    if run > bestStreak { bestStreak = run }
                } else {
                    run = 0
                }
            }
        }

        let rem = flat.count % 7
        if rem != 0 {
            for idx in 0 ..< (7 - rem) {
                flat.append(Cell(dayKey: "pad-tail-\(idx)", date: nil, readSeconds: 0, listenSeconds: 0, isToday: false, isFuture: false))
            }
        }

        var cols: [[Cell]] = []
        cols.reserveCapacity(flat.count / 7)
        var i = 0
        while i < flat.count {
            cols.append(Array(flat[i ..< min(i + 7, flat.count)]))
            i += 7
        }
        self.weeks = cols
        self.monthMarkers = markers
        self.activeDays = active
        self.longestStreak = bestStreak
    }
}

/// Interactive GitHub-style annual reading heatmap for iOS and macOS Reading Stats.
struct ReadingHeatmapView: View {
    let heatmap: ReadingHeatmap
    let tint: YYTint
    var cellSize: CGFloat = 8.5
    var spacing: CGFloat = 2.5
    @State private var selectedKey: String?

    private var selectedCell: ReadingHeatmap.Cell? {
        guard let selectedKey else { return nil }
        for week in heatmap.weeks {
            if let hit = week.first(where: { $0.dayKey == selectedKey && $0.date != nil }) {
                return hit
            }
        }
        return nil
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("年度热力图")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                Text("活跃 \(heatmap.activeDays) 天 · 最长连续 \(heatmap.longestStreak) 天")
                    .font(.yy(size: 10.5).monospacedDigit())
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 6)
                legend
            }

            ScrollViewReader { proxy in
                ScrollView(.horizontal, showsIndicators: false) {
                    VStack(alignment: .leading, spacing: 4) {
                        monthHeader
                        HStack(alignment: .top, spacing: spacing) {
                            ForEach(Array(heatmap.weeks.enumerated()), id: \.offset) { wIdx, week in
                                VStack(spacing: spacing) {
                                    ForEach(week, id: \.dayKey) { cell in
                                        daySquare(cell)
                                    }
                                }
                                .id(wIdx)
                            }
                        }
                    }
                    .padding(.vertical, 1)
                }
                .onAppear {
                    if let todayWeek = heatmap.weeks.firstIndex(where: { $0.contains(where: \.isToday) }), todayWeek > 26 {
                        proxy.scrollTo(todayWeek, anchor: .trailing)
                    }
                }
            }

            if let cell = selectedCell, let date = cell.date {
                Text(detailLine(for: cell, date: date))
                    .font(.yy(size: 10.5).monospacedDigit())
                    .foregroundStyle(YYColor.t2)
            }
        }
    }

    private var monthHeader: some View {
        let step = cellSize + spacing
        let totalWidth = CGFloat(max(1, heatmap.weeks.count)) * step - spacing
        return ZStack(alignment: .leading) {
            Color.clear.frame(width: totalWidth, height: 12)
            ForEach(heatmap.monthMarkers, id: \.weekIndex) { marker in
                Text(monthLabel(marker.month))
                    .font(.yy(size: 9.5).monospacedDigit())
                    .foregroundStyle(YYColor.t3)
                    .fixedSize()
                    .offset(x: CGFloat(marker.weekIndex) * step)
            }
        }
    }

    @ViewBuilder
    private func daySquare(_ cell: ReadingHeatmap.Cell) -> some View {
        if cell.date == nil {
            Color.clear.frame(width: cellSize, height: cellSize)
        } else {
            let isSelected = selectedKey == cell.dayKey
            RoundedRectangle(cornerRadius: 2)
                .fill(fillColor(level: cell.level, future: cell.isFuture))
                .frame(width: cellSize, height: cellSize)
                .overlay {
                    if isSelected {
                        RoundedRectangle(cornerRadius: 2).strokeBorder(YYColor.t1, lineWidth: 1)
                    } else if cell.isToday {
                        RoundedRectangle(cornerRadius: 2).strokeBorder(tint.accent.opacity(0.65), lineWidth: 0.8)
                    }
                }
                .contentShape(.rect)
                .onTapGesture {
                    if !cell.isFuture {
                        selectedKey = isSelected ? nil : cell.dayKey
                    }
                }
        }
    }

    private var legend: some View {
        HStack(spacing: 3) {
            Text("少").font(.yy(size: 9.5)).foregroundStyle(YYColor.t3)
            ForEach(0 ..< 5, id: \.self) { lv in
                RoundedRectangle(cornerRadius: 1.5)
                    .fill(fillColor(level: lv, future: false))
                    .frame(width: 7, height: 7)
            }
            Text("多").font(.yy(size: 9.5)).foregroundStyle(YYColor.t3)
        }
    }

    private func fillColor(level: Int, future: Bool) -> Color {
        if future { return YYColor.inset.opacity(0.45) }
        return switch level {
        case 0: YYColor.inset
        case 1: tint.fill.opacity(0.26)
        case 2: tint.fill.opacity(0.48)
        case 3: tint.fill.opacity(0.72)
        default: tint.fill
        }
    }

    private func monthLabel(_ month: Int) -> String {
        if (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") {
            return String(localized: "\(month) 月")
        }
        let symbols = Calendar.current.shortMonthSymbols
        return symbols.indices.contains(month - 1) ? symbols[month - 1] : "\(month)"
    }

    private func detailLine(for cell: ReadingHeatmap.Cell, date: Date) -> String {
        let dayText = Notes.monthDay(date)
        guard cell.totalSeconds > 0 else {
            return "\(dayText) · " + String(localized: "未阅读")
        }
        var parts: [String] = [dayText]
        if cell.readSeconds > 0 { parts.append(String(localized: "读 ") + Self.duration(cell.readSeconds)) }
        if cell.listenSeconds > 0 { parts.append(String(localized: "听 ") + Self.duration(cell.listenSeconds)) }
        return parts.joined(separator: " · ")
    }

    private static func duration(_ seconds: Int) -> String {
        let m = max(1, seconds / 60)
        if m < 60 { return String(localized: "\(m) 分") }
        return m % 60 == 0 ? String(localized: "\(m / 60) 小时") : String(localized: "\(m / 60) 小时 \(m % 60) 分")
    }
}
