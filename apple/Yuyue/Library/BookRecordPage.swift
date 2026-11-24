import SwiftUI
import YuyueCore

/// 书的详情: the cover and your score at the top, the one thing to do next, then 目录 (as the
/// page at the front of a book, or as squares) and 笔记 (your line on it, tags, what you
/// marked). The Mac lays it out wide; the phone in one column.
private final class StatusRectBox {
    var rect: CGRect = .zero
}

struct BookRecordPage: View {
    let library: Library
    let bookID: String
    var wide = false
    let open: (BookSummary, Library.OpenIntent?) -> Void
    /// 放进合集…: the shelf's own picker.
    var pickCollection: (BookSummary) -> Void = { _ in }

    enum Tab: Hashable { case contents, about, notes, log, podcast }

    @State private var tab = Tab.contents
    @State private var detail: BookDetail?
    @State private var position: Position?
    @State private var latestProgressAt: Int64?
    @State private var timeSpent: (read: Int, listen: Int) = (0, 0)
    @State private var marks: [Mark] = []
    @State private var expanded = false
    @AppStorage("book.contentsAsGrid") private var asGrid = false
    @State private var comment = ""
    @State private var newTag = ""
    @State private var deleting: BookSummary?
    @State private var about: String?
    /// The Mac's status menu: the app's own white card, under the button.
    @State private var statusMenuShown = false
    @State private var statusButtonFrame = CGRect.zero
    @State private var statusRectBox = StatusRectBox()
    @State private var podcastMentions: [BookPodcastMention] = []
    @State private var brewingKind: StudioKind?
    @State private var hoveredPodcastKey: String?
    @FocusState private var commentFocused: Bool
    @Environment(\.colorScheme) private var scheme
    private var tint: YYTint { YYTint.standard }

    private struct StudioKindLaunch: Identifiable {
        let kind: StudioKind
        var id: StudioKind { kind }
    }

    private var book: BookSummary? { library.book(bookID) }

    var body: some View {
        Group {
            if let book {
                ScrollView {
                    VStack(alignment: .leading, spacing: 0) {
                        header(book)
                        if !wide {
                            standing(book).padding(.top, 16)
                            next(book).padding(.top, 12)
                        }
                        tabs(book).padding(.top, wide ? 30 : 20)
                        Group {
                            switch tab {
                            case .contents where !book.record: contents
                            case .about: aboutPage(book)
                            case .log: logPage(book)
                            case .podcast: podcastPage(book)
                            default: notes(book)
                            }
                        }
                        .id(tab)
                        .transition(YYMotion.swap)
                        .padding(.top, 14)
                    }
                    .frame(maxWidth: wide ? 760 : .infinity, alignment: .leading)
                    .padding(.leading, wide ? 22 : 16)
                    .padding(.trailing, wide ? 28 : 16)
                    .padding(.bottom, 40)
                    .animation(YYMotion.standard, value: tab)
                }
                .scrollIndicators(.never)
                .coordinateSpace(.named("recordPage"))
                .overlay(alignment: .topLeading) {
                    #if os(macOS)
                    if statusMenuShown {
                        ZStack(alignment: .topLeading) {
                            Color.clear.contentShape(.rect).onTapGesture { statusMenuShown = false }
                            StatusMenuCard(book: book) { library.setStatus(book, $0) } pickCollection: {
                                pickCollection(book)
                            } delete: {
                                deleting = book
                            } close: {
                                statusMenuShown = false
                            }
                            .offset(x: statusButtonFrame.minX, y: statusButtonFrame.maxY + 6)
                            .transition(.opacity.combined(with: .scale(scale: 0.96, anchor: .topLeading)))
                        }
                    }
                    #endif
                }
                .animation(YYMotion.quick, value: statusMenuShown)
                .task(id: book.id) {
                    load(book)
                    #if DEBUG
                    // `-debugStatusMenu 1` opens the status menu; `-debugBookTab log|about|notes` that page.
                    if UserDefaults.standard.bool(forKey: "debugStatusMenu") { statusMenuShown = true }
                    switch UserDefaults.standard.string(forKey: "debugBookTab") {
                    case "log": tab = .log
                    case "about": tab = .about
                    case "notes": tab = .notes
                    default: break
                    }
                    #endif
                }
                .onChange(of: book.progress) { load(book) }
                .deletingRecord($deleting, library: library)
                .sheet(item: Binding(
                    get: { brewingKind.map { StudioKindLaunch(kind: $0) } },
                    set: { brewingKind = $0?.kind }
                )) { box in
                    PodcastStudioSheet(
                        library: library,
                        initialKind: box.kind,
                        preselectedBookIDs: [book.id],
                        onCreated: { _ in load(book) },
                        onClose: { brewingKind = nil }
                    )
                }
            } else {
                Text("这本书不在了").font(.yy(size: 13)).foregroundStyle(YYColor.t3)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
            }
        }
        .background(YYColor.bg)
    }

    private func load(_ book: BookSummary) {
        if book.record {
            if tab == .contents { tab = .notes }
            detail = nil
            position = nil
            latestProgressAt = nil
            timeSpent = (0, 0)
        } else {
            detail = try? library.core.book(id: book.id)
            let progress = (try? library.core.latestProgress(bookId: book.id)) ?? nil
            position = progress?.position
            latestProgressAt = progress?.at
            timeSpent = library.timeSpent(on: book)
        }
        marks = ((try? library.core.marks(book: book.id)) ?? []).sorted { $0.at > $1.at }
        about = book.record ? nil : (try? library.core.bookDescription(book: book.id)) ?? nil
        podcastMentions = library.podcastHub.mentions(for: book)
        if !commentFocused { comment = book.comment ?? "" }
    }

    @ViewBuilder
    private func podcastPage(_ book: BookSummary) -> some View {
        let pct = Int((book.progress * 100).rounded())
        let metaText: String = {
            if !podcastMentions.isEmpty {
                return String(localized: "\(podcastMentions.count) 处提及 · 防剧透锁")
            }
            if pct > 0 && book.status != .finished {
                return String(localized: "已读 \(pct)% · 防剧透锁")
            }
            return String(localized: "由这本书与你的笔记酿制")
        }()

        PodcastStationeryCard(cornerRadius: 13) {
            VStack(alignment: .leading, spacing: 0) {
                HStack(alignment: .center, spacing: 8) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text(podcastMentions.isEmpty ? String(localized: "书房电台") : String(localized: "相关节目与片段"))
                            .font(PodcastStyle.songti(15))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)

                        Text(metaText)
                            .font(.yy(size: 11.5))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    }

                    Spacer(minLength: 8)

                    PodcastPaperPill(title: String(localized: "+ 酿一期"), accent: true) {
                        brewingKind = marks.isEmpty ? .bookGuide : .weeklyDigest
                    }
                }
                .padding(.bottom, 6)

                if podcastMentions.isEmpty {
                    bookStudioStarterRows(book)
                } else {
                    VStack(spacing: 0) {
                        ForEach(Array(podcastMentions.prefix(5).enumerated()), id: \.offset) { idx, m in
                            let key = "mention-\(idx)-\(m.issueId)"
                            let isHovered = hoveredPodcastKey == key
                            let isCurrent = library.podcastHub.activeIssueID == m.issueId
                            let isPlaying = isCurrent && library.podcastHub.isPlaying

                            if idx > 0 {
                                Rectangle()
                                    .fill(scheme == .dark ? Color.white.opacity(0.05) : YYColor.line.opacity(0.65))
                                    .frame(height: 0.5)
                            }

                            HStack(spacing: 11) {
                                Group {
                                    if isPlaying {
                                        PodcastEqualizerBars(active: true)
                                    } else {
                                        Image(systemName: "play.fill")
                                            .font(.system(size: 8.5, weight: .semibold))
                                            .foregroundStyle(isCurrent || isHovered ? tint.accent : YYColor.t3.opacity(0.55))
                                    }
                                }
                                .frame(width: 14, height: 22, alignment: .leading)

                                PodcastShowMark(
                                    title: m.showTitle.isEmpty ? String(localized: "书房电台") : m.showTitle,
                                    isStudio: m.showTitle.isEmpty || m.showTitle == String(localized: "书房电台"),
                                    size: 22
                                )

                                VStack(alignment: .leading, spacing: 2) {
                                    Text(m.issueTitle)
                                        .font(.yy(size: 13.5, weight: isCurrent ? .medium : .regular))
                                        .foregroundStyle(isCurrent ? tint.accent : YYColor.t1)
                                        .lineLimit(1)

                                    HStack(spacing: 5) {
                                        Text(m.showTitle.isEmpty ? String(localized: "书房电台") : m.showTitle)
                                            .foregroundStyle(tint.accent)
                                        if !m.snippet.isEmpty {
                                            Text("·").foregroundStyle(YYColor.t3)
                                            Text(m.snippet)
                                                .foregroundStyle(YYColor.t3)
                                        }
                                    }
                                    .font(.yy(size: 11.5))
                                    .lineLimit(1)
                                }
                                .frame(maxWidth: .infinity, alignment: .leading)

                                PodcastPaperPill(
                                    title: String(localized: "跳到 \(PodcastFormat.clock(m.atSec))"),
                                    symbol: "play.fill",
                                    accent: isCurrent,
                                    height: 24,
                                    horizontalPadding: 9
                                ) {
                                    if let iss = library.podcastHub.issue(m.issueId) {
                                        library.podcastHub.start(iss, from: m.atSec)
                                    }
                                }
                            }
                            .padding(.horizontal, 10)
                            .padding(.vertical, 10)
                            .background(
                                RoundedRectangle(cornerRadius: 9, style: .continuous)
                                    .fill(isCurrent ? PodcastStyle.rowTintWash(tint, scheme) : (isHovered ? YYColor.inset.opacity(0.55) : Color.clear))
                            )
                            .padding(.horizontal, -10)
                            .contentShape(.rect)
                            .onHover { over in
                                hoveredPodcastKey = over ? key : (hoveredPodcastKey == key ? nil : hoveredPodcastKey)
                            }
                            .onTapGesture {
                                if let iss = library.podcastHub.issue(m.issueId) {
                                    library.podcastHub.start(iss, from: m.atSec)
                                }
                            }
                        }
                    }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, 13)
            .padding(.bottom, 8)
        }
    }

    private func bookStudioStarterRows(_ book: BookSummary) -> some View {
        let items: [(kind: StudioKind, title: String, accent: String, tail: String)] = [
            (
                .bookGuide,
                String(localized: "难书导读：《\(book.title)》核心脉络与直觉精讲"),
                String(localized: "读前热身"),
                String(localized: "10 分 · 附原文锚点")
            ),
            (
                .weeklyDigest,
                marks.isEmpty
                    ? String(localized: "书房周刊：围绕《\(book.title)》已读进度串讲")
                    : String(localized: "书房周刊：重读《\(book.title)》与 \(marks.count) 条划线"),
                String(localized: "不剧透锁"),
                String(localized: "10 分 · 仅依已读进度")
            ),
        ]

        return VStack(spacing: 0) {
            ForEach(Array(items.enumerated()), id: \.element.kind) { idx, item in
                let key = "starter-\(idx)"
                let isHovered = hoveredPodcastKey == key

                if idx > 0 {
                    Rectangle()
                        .fill(scheme == .dark ? Color.white.opacity(0.05) : YYColor.line.opacity(0.65))
                        .frame(height: 0.5)
                }

                HStack(spacing: 11) {
                    Image(systemName: "play.fill")
                        .font(.system(size: 8.5, weight: .semibold))
                        .foregroundStyle(isHovered ? tint.accent : YYColor.t3.opacity(0.5))
                        .frame(width: 14, height: 22, alignment: .leading)

                    PodcastShowMark(title: String(localized: "书房电台"), isStudio: true, size: 22)

                    VStack(alignment: .leading, spacing: 2) {
                        Text(item.title)
                            .font(.yy(size: 13.5, weight: .regular))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)

                        HStack(spacing: 5) {
                            Text(item.accent)
                                .fontWeight(.regular)
                                .foregroundStyle(tint.accent)
                            Text("·").foregroundStyle(YYColor.t3)
                            Text(item.tail)
                                .foregroundStyle(YYColor.t3)
                        }
                        .font(.yy(size: 11.5))
                        .lineLimit(1)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)

                    HStack(spacing: 3) {
                        PodcastGlyphView(glyph: .transcriptLines, size: 13.5, lineWidth: 1.45)
                            .foregroundStyle(YYColor.t3)
                            .frame(width: 26, height: 26)
                    }
                    .opacity(isHovered ? 0.85 : 0.25)
                }
                .padding(.horizontal, 10)
                .padding(.vertical, 10)
                .background(
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .fill(isHovered ? YYColor.inset.opacity(0.55) : Color.clear)
                )
                .padding(.horizontal, -10)
                .contentShape(.rect)
                .onHover { over in
                    hoveredPodcastKey = over ? key : (hoveredPodcastKey == key ? nil : hoveredPodcastKey)
                }
                .onTapGesture {
                    brewingKind = item.kind
                }
            }
        }
    }

    private var rows: [ChapterRow] {
        guard let detail else { return [] }
        return Chapters.rows(detail, at: position)
    }

    private var current: ChapterRow? {
        rows.first { if case .current = $0.state { true } else { false } }
    }

    // MARK: Top

    private func header(_ book: BookSummary) -> some View {
        HStack(alignment: .top, spacing: wide ? 26 : 16) {
            BookCover(book: book).frame(width: wide ? 120 : 104)
            VStack(alignment: .leading, spacing: 0) {
                Text(book.title)
                    .font(YYUI.serif(wide ? 25 : 20, .bold))
                    .foregroundStyle(YYColor.t1)
                    .lineSpacing(3)
                    .lineLimit(3)
                if let author = book.author {
                    Text(book.authors.joined(separator: " · ").isEmpty ? author : book.authors.joined(separator: " · "))
                        .font(.yy(size: 13)).foregroundStyle(YYColor.t2).padding(.top, 4)
                }
                if book.record {
                    PaperBookTag().padding(.top, 6)
                } else {
                    Text(meta(book)).font(.yy(size: 11.5)).foregroundStyle(YYColor.t3).padding(.top, 3)
                }
                HStack(spacing: 10) {
                    Text("你的评分").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                    StarRow(rating: book.rating, size: wide ? 16 : 17) { library.setRating(book, $0) }
                }
                .padding(.top, 12)
                if wide {
                    Spacer(minLength: 16)
                    HStack(spacing: 8) {
                        next(book)
                        statusButton(book)
                        Text(standingLine(book)).font(.yy(size: 11.5)).foregroundStyle(YYColor.t3).padding(.leading, 6)
                    }
                }
            }
            .frame(maxWidth: .infinity, minHeight: wide ? 173 : nil, alignment: .topLeading)
        }
    }

    /// EPUB · 34 章 · 约 9 小时 — 纸书
    private func meta(_ book: BookSummary) -> String {
        if book.record { return String(localized: "纸书") }
        var parts = [book.format.label]
        if !rows.isEmpty { parts.append(String(localized: "\(rows.count) 章")) }
        if book.readMinutes > 0 { parts.append(String(localized: "约 \(BookSummary.hours(book.readMinutes))")) }
        return parts.joined(separator: " · ")
    }

    /// 10月28日开始 · 读到 43% · 11月3日读完
    private func standingLine(_ book: BookSummary) -> String {
        var parts: [String] = []
        if let s = book.startedAt { parts.append(String(localized: "\(BookSummary.day(s))开始")) }
        if book.status == .finished, let f = book.finishedAt {
            parts.append(String(localized: "\(BookSummary.day(f))读完"))
        }
        if !book.record {
            let spent = timeSpent
            if spent.read > 0 { parts.append(String(localized: "读了 \(Self.duration(spent.read))")) }
            if spent.listen > 0 { parts.append(String(localized: "听了 \(Self.duration(spent.listen))")) }
            if book.status != .finished, book.progress > 0 { parts.append(book.remaining) }
        }
        return parts.joined(separator: " · ")
    }

    /// The phone's line under the cover: when, and the status to change.
    private func standing(_ book: BookSummary) -> some View {
        let line = standingLine(book)
        return HStack(spacing: 10) {
            Text(line.isEmpty ? book.recordLine : line)
                .font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                .frame(maxWidth: .infinity, alignment: .leading)
            statusButton(book)
        }
    }

    @ViewBuilder private func statusButton(_ book: BookSummary) -> some View {
        #if os(macOS)
        Button {
            statusButtonFrame = statusRectBox.rect
            statusMenuShown.toggle()
        } label: {
            HStack(spacing: 5) {
                Image(systemName: book.status.symbol).font(.yy(size: 11))
                Text(book.status.name)
                Image(systemName: "chevron.down").font(.yy(size: 9, weight: .semibold))
            }
        }
        .buttonStyle(RecordButton(main: false, big: !wide))
        .fixedSize()
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .named("recordPage")) } action: { rect in
            statusRectBox.rect = rect
            if statusMenuShown { statusButtonFrame = rect }
        }
        .accessibilityLabel(String(localized: "状态：\(book.status.name)"))
        #else
        Menu {
            StatusMenuItems(book: book, library: library, pickCollection: { pickCollection(book) }, delete: { deleting = book })
        } label: {
            HStack(spacing: 5) {
                Image(systemName: book.status.symbol).font(.yy(size: 11))
                Text(book.status.name)
                Image(systemName: "chevron.down").font(.yy(size: 9, weight: .semibold))
            }
        }
        .menuStyle(.button)
        .buttonStyle(RecordButton(main: false, big: !wide))
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel(String(localized: "状态：\(book.status.name)"))
        #endif
    }

    /// The one thing to do next: 接着读 · 第 15 章, 开始读, 重读 (ebooks), or status transition (paper books).
    @ViewBuilder private func next(_ book: BookSummary) -> some View {
        if book.record {
            let (title, target): (String, ReadingStatus) = switch book.status {
            case .want: (String(localized: "开始读"), .reading)
            case .reading: (String(localized: "记为读过"), .finished)
            case .finished: (String(localized: "重读"), .reading)
            case .paused, .dropped: (String(localized: "接着读"), .reading)
            }
            Button { library.setStatus(book, target) } label: {
                Text(title).frame(maxWidth: wide ? nil : .infinity)
            }
            .buttonStyle(RecordButton(main: true, big: !wide))
        } else {
            Button { open(book, nil) } label: {
                Text(nextTitle(book)).frame(maxWidth: wide ? nil : .infinity)
            }
            .buttonStyle(RecordButton(main: true, big: !wide))
            .keyboardShortcut(.defaultAction)
        }
    }

    private func nextTitle(_ book: BookSummary) -> String {
        switch book.status {
        case .finished: return String(localized: "重读")
        case .want where book.progress == 0: return String(localized: "开始读")
        default:
            if let current { return String(localized: "接着读 · 第 \(current.number) 章") }
            return String(localized: "接着读")
        }
    }

    /// 6 小时 · 40 分钟
    static func duration(_ minutes: Int) -> String {
        if minutes < 60 { return String(localized: "\(max(1, minutes)) 分钟") }
        let h = minutes / 60, m = minutes % 60
        return m == 0 || h >= 10 ? String(localized: "\(h) 小时") : String(localized: "\(h) 小时 \(m) 分")
    }

    // MARK: 目录 · 笔记

    private func tabs(_ book: BookSummary) -> some View {
        HStack(spacing: 2) {
            if !book.record {
                RecordTab(symbol: "book", title: String(localized: "目录"), count: rows.isEmpty ? nil : rows.count, on: tab == .contents) { tab = .contents }
            }
            RecordTab(symbol: "doc.text", title: String(localized: "简介"), on: tab == .about) { tab = .about }
            RecordTab(symbol: "square.and.pencil", title: String(localized: "笔记"), count: marks.count + (book.comment == nil ? 0 : 1), on: tab == .notes) { tab = .notes }
            RecordTab(symbol: "clock", title: String(localized: "记录"), count: log(book).count, on: tab == .log) { tab = .log }
            if !podcastMentions.isEmpty {
                RecordTab(symbol: "headphones", title: String(localized: "播客"), count: podcastMentions.count, on: tab == .podcast) { tab = .podcast }
            }
            Spacer(minLength: 8)
            if tab != .podcast {
                PodcastPaperPill(
                    title: String(localized: "书房电台"),
                    glyph: .openBook,
                    height: 26,
                    horizontalPadding: 10
                ) {
                    brewingKind = marks.isEmpty ? .bookGuide : .weeklyDigest
                }
            }
        }
        .padding(.leading, wide ? -8 : 0)
    }

    @ViewBuilder private var contents: some View {
        if rows.isEmpty {
            Text("这本书没有目录").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
        } else {
            VStack(alignment: .leading, spacing: 10) {
                HStack(spacing: 2) {
                    if let current, case .current(let f) = current.state {
                        Text("读到第 \(current.number) 章 · \(Int((f * 100).rounded()))%")
                            .font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 8)
                    RecordIconButton(symbol: "list.bullet", label: String(localized: "列表"), on: !asGrid) { asGrid = false }
                    RecordIconButton(symbol: "square.grid.2x2", label: String(localized: "格子"), on: asGrid) { asGrid = true }
                }
                .padding(.bottom, 6)
                .overlay(alignment: .bottom) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                if asGrid {
                    ChapterGrid(rows: rows) { openChapter($0) }
                } else {
                    RecordChapterList(rows: rows, expanded: expanded, fontSize: wide ? 13.5 : 14) {
                        withAnimation(YYMotion.standard) { expanded = true }
                    } open: { openChapter($0) }
                }
            }
        }
    }

    private func openChapter(_ row: ChapterRow) {
        guard let book else { return }
        // The chapter being read opens where it was left; any other at its start.
        if case .current = row.state { open(book, nil) } else { open(book, .at(Position(section: row.section, sentence: 0))) }
    }

    // MARK: 简介 · 记录

    private func aboutPage(_ book: BookSummary) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            if let about {
                Text(about)
                    .font(YYUI.serif(13.5))
                    .foregroundStyle(YYColor.t1)
                    .lineSpacing(7)
                    .textSelection(.enabled)
            } else {
                Text(book.record ? String(localized: "纸书没有简介，可以在笔记里写几句。") : String(localized: "这本书没有附简介。"))
                    .font(.yy(size: 12.5)).foregroundStyle(YYColor.t3)
            }
            VStack(alignment: .leading, spacing: 6) {
                infoLine(String(localized: "作者"), book.authors.joined(separator: " · "))
                if !book.record { infoLine(String(localized: "格式"), meta(book)) }
                infoLine(String(localized: "加入"), BookSummary.day(book.addedAt))
            }
            .padding(.top, 4)
            tags(book).padding(.top, 2)
        }
    }

    private func infoLine(_ name: String, _ value: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 12) {
            Text(name).font(.yy(size: 11.5)).foregroundStyle(YYColor.t3).frame(width: 36, alignment: .leading)
            Text(value.isEmpty ? "—" : value).font(.yy(size: 12.5)).foregroundStyle(YYColor.t1)
        }
    }

    private struct LogLine: Identifiable {
        let id: String
        let at: Int64
        let text: String
        var bold: String = ""
    }

    /// 记录: what happened to this book, newest first — added, begun, how far, marks by the
    /// day, put aside, finished — from what the library already keeps.
    private func log(_ book: BookSummary) -> [LogLine] {
        var out: [LogLine] = [LogLine(id: "added", at: book.addedAt, text: String(localized: "加进来"))]
        if let s = book.startedAt { out.append(LogLine(id: "started", at: s, text: String(localized: "开始读"))) }
        if !book.record, let at = latestProgressAt, let current {
            out.append(LogLine(id: "at", at: at, text: String(localized: "读到 "), bold: String(localized: "第 \(current.number) 章 · \(current.title)")))
        }
        let byDay = Dictionary(grouping: marks) { Calendar.current.startOfDay(for: Date(timeIntervalSince1970: Double($0.at) / 1000)) }
        for (day, list) in byDay {
            out.append(LogLine(id: "marks-\(day.timeIntervalSince1970)", at: Int64(day.timeIntervalSince1970 * 1000) + 1, text: String(localized: "划了 \(list.count) 句")))
        }
        if let f = book.finishedAt { out.append(LogLine(id: "done", at: f, text: String(localized: "读完"), bold: book.rating.map { String(repeating: "★", count: Int($0)) } ?? "")) }
        if book.status == .paused || book.status == .dropped, let at = book.openedAt ?? book.manual?.at {
            out.append(LogLine(id: "status", at: at + 1, text: book.status.name))
        }
        return out.sorted { $0.at > $1.at }
    }

    private func logPage(_ book: BookSummary) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(log(book)) { line in
                HStack(alignment: .firstTextBaseline, spacing: 14) {
                    Text(Date(timeIntervalSince1970: Double(line.at) / 1000).formatted(.dateTime.month(.twoDigits).day(.twoDigits)))
                        .font(.yy(size: 11.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 44, alignment: .leading)
                    (Text(line.text).foregroundStyle(YYColor.t2) + Text(line.bold).foregroundStyle(YYColor.t1).fontWeight(.medium))
                        .font(.yy(size: 12.5))
                    Spacer(minLength: 0)
                }
                .padding(.vertical, 8)
                .overlay(alignment: .bottom) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
            }
        }
        .frame(maxWidth: 520, alignment: .leading)
    }

    private func notes(_ book: BookSummary) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("短评").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
                Spacer()
                Text("只有你看得到").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
            }
            TextField(String(localized: "写一句（可以不写）"), text: $comment, axis: .vertical)
                .font(YYUI.serif(13.5))
                .lineLimit(2...8)
                .lineSpacing(4)
                .textFieldStyle(.plain)
                .focused($commentFocused)
                .padding(.horizontal, 12)
                .padding(.vertical, 10)
                .background(YYColor.card, in: .rect(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(YYColor.line, lineWidth: 0.5))
                .padding(.top, 8)
                .onSubmit { saveComment(book) }
                .onChange(of: commentFocused) { if !commentFocused { saveComment(book) } }
            tags(book).padding(.top, 12)
            if !marks.isEmpty {
                HStack {
                    Text("划线").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
                    Spacer()
                    Text("\(marks.count) 条").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                }
                .padding(.top, 22)
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(marks.prefix(30), id: \.id) { mark in
                        markRow(book, mark)
                        if mark.id != marks.prefix(30).last?.id { Divider().overlay(YYColor.line) }
                    }
                }
                .padding(.top, 4)
            }
        }
    }

    private func saveComment(_ book: BookSummary) {
        let text = comment.trimmingCharacters(in: .whitespacesAndNewlines)
        if text != (book.comment ?? "") { library.setComment(book, text) }
    }

    private func tags(_ book: BookSummary) -> some View {
        HStack(spacing: 6) {
            ForEach(book.tags, id: \.self) { tag in
                Button { library.setTag(book, tag, false) } label: {
                    Text(tag).font(.yy(size: 12)).foregroundStyle(YYColor.t1)
                        .padding(.horizontal, 9).frame(height: 26)
                        .background(YYColor.pill, in: .rect(cornerRadius: 7))
                        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.pillLine, lineWidth: 0.5))
                }
                .buttonStyle(.plain)
                .help(String(localized: "点一下拿掉"))
            }
            HStack(spacing: 3) {
                Image(systemName: "plus").font(.yy(size: 10, weight: .semibold))
                TextField(String(localized: "标签"), text: $newTag)
                    .textFieldStyle(.plain)
                    .frame(width: 64)
                    .onSubmit {
                        let t = newTag.trimmingCharacters(in: .whitespaces)
                        if !t.isEmpty { library.setTag(book, t, true) }
                        newTag = ""
                    }
            }
            .font(.yy(size: 12))
            .foregroundStyle(YYColor.t3)
            .padding(.horizontal, 9).frame(height: 26)
            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.pillLine, lineWidth: 0.5))
        }
    }

    private func markRow(_ book: BookSummary, _ mark: Mark) -> some View {
        Button {
            open(book, .at(Position(section: mark.section, sentence: mark.start.sentence)))
        } label: {
            VStack(alignment: .leading, spacing: 4) {
                Text(mark.text)
                    .font(YYUI.serif(13))
                    .foregroundStyle(YYColor.t1)
                    .lineSpacing(4)
                    .lineLimit(4)
                    .multilineTextAlignment(.leading)
                if let note = mark.note {
                    Text(note).font(.yy(size: 12)).foregroundStyle(YYColor.t2).lineLimit(3)
                }
                Text(BookSummary.day(mark.at)).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(.vertical, 10)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }
}

private extension BookFormat {
    var label: String {
        switch self {
        case .epub: "EPUB"
        case .txt: "TXT"
        case .pdf: "PDF"
        case .mobi: "Kindle"
        case .audio: String(localized: "有声书")
        case .podcast: String(localized: "播客")
        }
    }
}
