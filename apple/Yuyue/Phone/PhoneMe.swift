#if os(iOS)
import SwiftUI
import YuyueCore

/// 我的: this week's reading and listening on top, the year's totals under it,
/// then every setting in groups: 同步; 外观; 外文 and 引擎; 存储 and 关于.
struct PhoneMe: View {
    let library: Library
    @State private var week: [DayTime] = []
    @State private var marks = 0
    @State private var used: Int64?
    @State private var lastSnapshot: Int64?
    @AppStorage(ImmersivePrefs.alwaysKey) private var immersiveAlways = false
    @AppStorage(EInk.key) private var einkOn = false
    @State private var words = 0
    @State private var feedbackShown = false
    #if DEBUG
    @State private var debugPage: String?
    #endif
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    /// Scrolled past 我的: the small title pinned at the top.
    @State private var titlePinned = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    /// Regular width (iPhone Duo open, iPad): the column at most 640 wide, in the middle.
    @Environment(\.horizontalSizeClass) private var sizeClass
    @Environment(PhoneChrome.self) private var chrome
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        NavigationStack {
            Group {
                if InnerDisplay.isOn(sizeClass) {
                    // iPhone Duo's inner display: account and listening left of the fold, the rest
                    // right of it; each column scrolls on its own.
                    FoldSplit {
                        column { leftGroups }
                    } trailing: {
                        column { rightGroups }
                    }
                    .background(YYColor.bg)
                } else {
                    YYPage {
                        VStack(alignment: .leading, spacing: 0) {
                            // The title is the page's own, as on the shelf.
                            PageTitle(symbol: "person", title: "我的")
                                .padding(.top, 14)
                                .padding(.bottom, 4)
                            // The Mac's order: 外观; 读和听; 连接; 这台设备 — under 这周, over 关于.
                            weekLink
                            lookGroup
                            listenGroup
                            syncGroup
                            storageGroup
                            aboutGroup
                        }
                        .frame(maxWidth: sizeClass == .regular ? 640 : .infinity)
                        .frame(maxWidth: .infinity)
                    }
                }
            }
            .navigationTitle("我的")
            .toolbar(InnerDisplay.isOn(sizeClass) ? .automatic : .hidden, for: .navigationBar)
            // Scrolled past the title, 我的 stays at the top, as the system's bar had it.
            .onScrollGeometryChange(for: Bool.self) { $0.contentOffset.y + $0.contentInsets.top > 50 } action: { _, past in
                withAnimation(YYMotion.quick) { titlePinned = past }
            }
            .overlay(alignment: .top) {
                if titlePinned, !InnerDisplay.isOn(sizeClass) {
                    VStack(spacing: 0) {
                        Text("我的")
                            .font(.headline)
                            .foregroundStyle(YYColor.t1)
                            .frame(maxWidth: .infinity)
                            .frame(height: 44)
                            .background { YYColor.bg.ignoresSafeArea(edges: .top) }
                        LinearGradient(colors: [YYColor.bg, YYColor.fade0], startPoint: .top, endPoint: .bottom)
                            .frame(height: 14)
                            .allowsHitTesting(false)
                    }
                    .transition(.opacity)
                }
            }
            #if DEBUG
            .navigationDestination(item: $debugPage) { debugDestination($0) }
            #endif
        }
        .id(chrome.mePop)
        .onChange(of: chrome.mePop) {
            titlePinned = false
            #if DEBUG
            debugPage = nil
            #endif
        }
        #if DEBUG
        .task {
            guard debugPage == nil, let page = UserDefaults.standard.string(forKey: "debugMe"), page != "root" else { return }
            try? await Task.sleep(for: .milliseconds(600))
            debugPage = page
        }
        #endif
        .task(id: library.books.map(\.id)) { await loadSummary() }
        .task {
            let dir = library.dataDir
            used = await StorageUsage.measure(dir: dir, active: [:]).total
            let snapshots = Snapshots(library: library)
            await snapshots.load()
            lastSnapshot = snapshots.saved.first?.about.madeAt
        }
        .onReceive(NotificationCenter.default.publisher(for: .notesChanged)) { _ in load() }
        .onReceive(NotificationCenter.default.publisher(for: .vocabChanged)) { _ in load() }
        .onReceive(NotificationCenter.default.publisher(for: .snapshotsChanged)) { _ in
            Task {
                let snapshots = Snapshots(library: library)
                await snapshots.load()
                lastSnapshot = snapshots.saved.first?.about.madeAt
            }
        }
        .sheet(isPresented: $feedbackShown) {
            FeedbackForm { feedbackShown = false }
        }
    }

    // MARK: Pieces

    /// iPhone Duo's left column: 这周, 同步, 朗读 and 引擎.
    @ViewBuilder private var leftGroups: some View {
        weekLink
        lookGroup
        listenGroup
    }

    /// iPhone Duo's right column: 外观, 存储, 关于; its top in line with the week card's.
    @ViewBuilder private var rightGroups: some View {
        syncGroup.padding(.top, YYUI.s(14))
        storageGroup
        aboutGroup
    }

    /// One column of iPhone Duo's two: at most 480 wide, in the middle of its pane, scrolling on its own.
    private func column<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        YYPage {
            VStack(alignment: .leading, spacing: 0) { content() }
                .frame(maxWidth: 480)
                .frame(maxWidth: .infinity)
        }
    }

    private var weekLink: some View {
        NavigationLink { PhoneStats(library: library) } label: { weekCard }
            .buttonStyle(YYPressStyle(scale: 0.985))
            .padding(.top, YYUI.s(14))
            .padding(.bottom, YYUI.s(10))
    }

    // The Mac's settings sidebar, in its order and its groups: 外观; 读和听; 连接; 这台设备.

    private var lookGroup: some View {
        group(nil) {
            link(String(localized: "通用"), "slider.horizontal.3", nil) { PhoneGeneral(library: library) }
            link(String(localized: "外观"), "sun.max", "\(mode.title) · \(tint.name)") { PhoneAppearance() }
        }
    }

    private var listenGroup: some View {
        group(String(localized: "阅读与朗读")) {
            link(String(localized: "阅读"), "book", nil) { PhoneReading(library: library) }
            link(String(localized: "外文"), "globe", nil) { PhoneForeign(library: library) }
            link(String(localized: "朗读"), "headphones", nil) { PhoneListening() }
        }
    }

    private var syncGroup: some View {
        group(String(localized: "连接")) {
            link(String(localized: "书伴"), "text.bubble", CompanionConfig.current.provider.kind == .apple ? SettingsDevice.name : CompanionConfig.current.provider.title) { PhoneCompanionSettings() }
            link(String(localized: "同步"), "icloud", syncDetail) { PhoneSync(library: library) }
            link(String(localized: "笔记导出"), "square.and.arrow.up", exportDetail) { PhoneNoteExport() }
        }
    }

    private var storageGroup: some View {
        group(SettingsDevice.name) {
            link(String(localized: "存储"), "internaldrive", used.map(PhoneStorage.size)) { PhoneStorage(library: library) }
            link(String(localized: "数据"), "shippingbox", lastSnapshot.map(Snapshots.day) ?? String(localized: "换设备、备份")) { PhoneSnapshot(library: library) }
            link(String(localized: "最近改过"), "clock.arrow.circlepath", changedToday) { PhoneSettingsHistory(library: library) }
        }
    }

    /// How many settings were changed today, as the Mac's clock badge says it.
    private var changedToday: String? {
        let n = SettingsLog.shared.entries.filter { $0.undone != true && Calendar.current.isDateInToday($0.date) }.count
        return n > 0 ? String(localized: "今天 \(n) 处") : nil
    }

    /// The targets switched on, as the Mac lists them; or that none is yet.
    private var exportDetail: String {
        let on = NoteExport.offered.map(\.title)
        return on.isEmpty ? String(localized: "未连接") : on.joined(separator: " · ")
    }

    private var aboutGroup: some View {
        group(nil) {
            Button { feedbackShown = true } label: {
                YYRow(String(localized: "反馈"), icon: "exclamationmark.bubble", value: Feedback.early ? String(localized: "早期版本") : nil, chevron: true)
                    .frame(minHeight: YYUI.s(36))
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            // A new version lights its dot here; 检查更新 is inside.
            NavigationLink { PhoneAbout() } label: {
                YYRow(title: String(localized: "关于"), icon: "info.circle",
                      value: UpdateCenter.enabled && UpdateCenter.shared.showsBadge
                          ? UpdateCenter.shared.offer?.display
                          : Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String,
                      chevron: true) {
                    if UpdateCenter.enabled && UpdateCenter.shared.showsBadge {
                        Circle().fill(YYColor.danger).frame(width: 8, height: 8)
                    }
                }
                .frame(minHeight: YYUI.s(36))
            }
            .buttonStyle(.plain)
        }
    }

    /// One group of settings, under its small grey name as in the Mac's sidebar.
    private func group<Content: View>(_ title: String?, @ViewBuilder _ rows: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            if let title {
                Text(title).font(YYUI.font(10, .medium)).foregroundStyle(YYColor.t3)
                    .padding(.horizontal, YYUI.s(4)).padding(.top, YYUI.s(4)).padding(.bottom, YYUI.s(6))
            }
            YYGroup { rows() }
        }
        // A group without a name sits as far from the one above as a named group's name does.
        .padding(.top, title == nil ? YYUI.s(6) : 0)
        .padding(.bottom, YYUI.s(10))
    }

    #if DEBUG
    /// `-debugMe <page>`: 外观, 书伴 (`companion:<id>` one service), 笔记导出 (`notes:<target>`), …
    @ViewBuilder private func debugDestination(_ page: String) -> some View {
        let parts = page.split(separator: ":", maxSplits: 1).map(String.init)
        switch parts[0] {
        case "appearance": PhoneAppearance()
        case "companion":
            if parts.count > 1, let p = parts[1] == "apple" ? CompanionProvider.apple : CompanionProvider.find(parts[1]) {
                PhoneCompanionService(provider: p)
            } else {
                PhoneCompanionSettings()
            }
        case "notes":
            PhoneNoteExport(debugTarget: parts.count > 1 ? NoteExport.Target(rawValue: parts[1]) : nil)
        case "listening": PhoneListening()
        case "reading": PhoneReading(library: library)
        case "general": PhoneGeneral(library: library)
        case "voices": PhoneVoiceSourcePage(source: parts.count > 1 ? VoiceSource(rawValue: parts[1]) ?? .mac : .mac)
        case "history": PhoneSettingsHistory(library: library)
        case "foreign": PhoneForeign(library: library)
        case "sync": PhoneSync(library: library)
        case "storage": PhoneStorage(library: library)
        case "sources": PhoneSources(library: library)
        case "wifi": PhoneWifi(library: library)
        case "snapshot": PhoneSnapshot(library: library)
        case "eink": PhoneEInk(library: library)
        case "stats": PhoneStats(library: library)
        case "about": PhoneAbout()
        default: Text(page)
        }
    }
    #endif

    private func link<Destination: View>(_ title: String, _ symbol: String, _ value: String?,
                                         @ViewBuilder _ destination: @escaping () -> Destination) -> some View {
        NavigationLink(destination: destination) {
            YYRow(title, icon: symbol, value: value, chevron: true).frame(minHeight: YYUI.s(36))
        }
        .buttonStyle(.plain)
    }

    private func load() {
        Task { await loadSummary() }
    }

    private func loadSummary() async {
        let days = PhoneStats.Period.week.days(containing: Date())
        guard let first = days.first, let last = days.last else { return }
        let fromDay = Notes.day(first)
        let toDay = Notes.day(last)
        let bookIDs = library.books.filter(\.available).map(\.id)
        let core = library.core
        let summary = await Self.fetchSummary(core: core, fromDay: fromDay, toDay: toDay, bookIDs: bookIDs)
        guard !Task.isCancelled else { return }
        week = summary.week
        marks = summary.marks
        words = summary.words
    }

    @concurrent nonisolated private static func fetchSummary(
        core: YuyueCore,
        fromDay: String,
        toDay: String,
        bookIDs: [String]
    ) async -> (week: [DayTime], marks: Int, words: Int) {
        let week = (try? core.dayTimes(from: fromDay, to: toDay)) ?? []
        var marks = 0
        for id in bookIDs {
            if Task.isCancelled { break }
            marks += (try? core.marks(book: id))?.count ?? 0
        }
        let words = ((try? core.words()) ?? []).count { !$0.known }
        return (week, marks, words)
    }

    private var finishedThisYear: Int {
        let year = Calendar.current.component(.year, from: Date())
        return library.books.count { b in
            guard b.status == .finished else { return false }
            let at = b.finishedAt ?? b.openedAt ?? b.addedAt
            return Calendar.current.component(.year, from: Notes.date(at)) == year
        }
    }

    private var syncDetail: String {
        let sync = library.sync
        guard sync.isOn else { return String(localized: "未开启") }
        let place = switch sync.method {
        case .webdav: "WebDAV"
        case .googleDrive: "Google Drive"
        default: "iCloud"
        }
        if case .failed = sync.state { return String(localized: "\(place) · 同步未成功") }
        return [place, sync.lastSynced.map(PhoneSync.ago)].compactMap { $0 }.joined(separator: " · ")
    }

    // MARK: 这周

    /// This week's reading and listening, a column a day stacked, and under them the
    /// books finished this year, the lines marked and the words kept.
    private var weekCard: some View {
        let days = PhoneStats.Period.week.days(containing: Date())
        let read = week.reduce(0) { $0 + Int($1.readSeconds) }
        let listen = week.reduce(0) { $0 + Int($1.listenSeconds) }
        return VStack(alignment: .leading, spacing: 0) {
            HStack {
                Text("这周").foregroundStyle(YYColor.t3)
                Spacer()
                HStack(spacing: 1) {
                    Text("阅读统计")
                    Image(systemName: "chevron.right").font(YYUI.font(8, .semibold))
                }
                .foregroundStyle(YYColor.t2)
            }
            .font(YYUI.font(10.5))
            HStack(spacing: YYUI.s(20)) {
                periodTotal(String(localized: "读"), read, tint.fill)
                periodTotal(String(localized: "听"), listen, YYColor.tint)
            }
            .padding(.top, YYUI.s(6))
            WeekBars(days: days, times: week, tint: tint)
                .frame(height: YYUI.s(70))
                .padding(.top, YYUI.s(10))
            HStack(spacing: 0) {
                tile("\(finishedThisYear)", String(localized: "本"), String(localized: "今年读完"))
                Rectangle().fill(YYColor.line).frame(width: 0.5)
                tile(marks.formatted(), String(localized: "句"), String(localized: "划线"))
                Rectangle().fill(YYColor.line).frame(width: 0.5)
                tile(words.formatted(), String(localized: "个"), String(localized: "生词"))
            }
            .fixedSize(horizontal: false, vertical: true)
            .padding(.top, YYUI.s(11))
            .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
            .padding(.top, YYUI.s(12))
        }
        .padding(.vertical, YYUI.s(12))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
        .contentShape(.rect)
    }

    /// One of the three counts.
    private func tile(_ number: String, _ unit: String, _ label: String) -> some View {
        VStack(spacing: 1) {
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(2)) {
                Text(number).font(YYUI.font(15, .bold).monospacedDigit()).foregroundStyle(YYColor.t1)
                if !unit.isEmpty { Text(unit).font(YYUI.font(10, .medium)).foregroundStyle(YYColor.t2) }
            }
            Text(label).font(YYUI.font(10)).foregroundStyle(YYColor.t3)
        }
        .frame(maxWidth: .infinity)
    }
}

/// Seven columns, listening stacked on reading; today's column wider and its label
/// dark, the days to come empty.
private struct WeekBars: View {
    let days: [Date]
    let times: [DayTime]
    let tint: YYTint

    var body: some View {
        let sums = days.map { d -> (Int, Int) in
            let key = Notes.day(d)
            let t = times.filter { $0.day == key }
            return (t.reduce(0) { $0 + Int($1.readSeconds) }, t.reduce(0) { $0 + Int($1.listenSeconds) })
        }
        let most = max(1, sums.map { $0.0 + $0.1 }.max() ?? 1)
        HStack(alignment: .bottom, spacing: 0) {
            ForEach(Array(days.enumerated()), id: \.offset) { i, day in
                let today = Calendar.current.isDateInToday(day)
                VStack(spacing: 0) {
                    GeometryReader { g in
                        VStack(spacing: 1) {
                            Spacer(minLength: 0)
                            if sums[i].1 > 0 {
                                UnevenRoundedRectangle(topLeadingRadius: 2.5, topTrailingRadius: 2.5).fill(YYColor.tint)
                                    .frame(height: g.size.height * Double(sums[i].1) / Double(most))
                            }
                            if sums[i].0 > 0 {
                                UnevenRoundedRectangle(topLeadingRadius: sums[i].1 > 0 ? 0 : 2.5, bottomLeadingRadius: 1,
                                                       bottomTrailingRadius: 1, topTrailingRadius: sums[i].1 > 0 ? 0 : 2.5)
                                    .fill(tint.fill)
                                    .frame(height: g.size.height * Double(sums[i].0) / Double(most))
                            }
                        }
                        .frame(width: YYUI.s(today ? 12 : 8))
                        .frame(maxWidth: .infinity)
                    }
                    Text(today ? String(localized: "今天") : Self.weekday(day))
                        .font(YYUI.font(10, today ? .semibold : .regular))
                        .foregroundStyle(today ? YYColor.t1 : YYColor.t3)
                        .fixedSize()
                        .padding(.top, YYUI.s(5))
                }
                .frame(maxWidth: .infinity)
            }
        }
    }

    static func weekday(_ d: Date) -> String {
        // Chinese keeps the single characters 一…日; other languages use their own short names (Mon…Sun).
        let i = Calendar.current.component(.weekday, from: d) - 1
        if Locale.preferredLanguages.first?.hasPrefix("zh") ?? false {
            return ["日", "一", "二", "三", "四", "五", "六"][i]
        }
        return Calendar.current.shortWeekdaySymbols[i]
    }
}

/// "■ 读 5 小时 12 分", the numbers big.
fileprivate func periodTotal(_ label: String, _ seconds: Int, _ color: Color) -> some View {
    let m = seconds / 60
    let parts: [(Int, String)] = m < 60 ? [(m, String(localized: "分"))] : m % 60 == 0 ? [(m / 60, String(localized: "小时"))] : [(m / 60, String(localized: "小时")), (m % 60, String(localized: "分"))]
    return HStack(alignment: .firstTextBaseline, spacing: YYUI.s(2)) {
        RoundedRectangle(cornerRadius: 2).fill(color).frame(width: YYUI.s(7), height: YYUI.s(7))
            .alignmentGuide(.firstTextBaseline) { $0[.bottom] }
            .padding(.trailing, YYUI.s(3))
        Text(label)
        ForEach(Array(parts.enumerated()), id: \.offset) { i, part in
            Text("\(part.0)").font(YYUI.font(19, .bold).monospacedDigit()).foregroundStyle(YYColor.t1)
                .contentTransition(.numericText())
                .padding(.leading, YYUI.s(i == 0 ? 4 : 5)).padding(.trailing, YYUI.s(1))
            Text(part.1)
        }
    }
    .font(YYUI.font(10.5))
    .foregroundStyle(YYColor.t2)
    .lineLimit(1)
}

// MARK: - 阅读统计

/// 阅读统计: by week, month or year, reading and listening apart; the books
/// finished; each book's time; and, at the bottom, how the time is counted.
struct PhoneStats: View {
    let library: Library
    @State private var period = Period.week
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

        /// Weeks start on Monday.
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

        /// Every day of the period `date` falls in.
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
        YYPage {
            YYSegments(items: Period.allCases.map { ($0, $0.name) }, selection: $period)
                .padding(.top, YYUI.s(10))
            periodCard.padding(.top, YYUI.s(12))
            if !perBook.isEmpty {
                YYLabel(String(localized: "每本书"), String(localized: "\(periodWord) · 按时长"))
                YYGroup {
                    ForEach(perBook, id: \.0.id) { book, read, listen in bookRow(book, read, listen) }
                }
                .padding(.bottom, YYUI.s(12))
            }
            how.padding(.top, perBook.isEmpty ? YYUI.s(12) : YYUI.s(2))
        }
        .navigationTitle("阅读统计")
        .navigationBarTitleDisplayMode(.inline)
        .task(id: "\(period.rawValue)\(Notes.day(anchor))") { load() }
        .onChange(of: period) { anchor = Date() }
    }

    /// The period, its way back and on, reading and listening, a column a day (a
    /// month in a year), and the books finished in it.
    private var periodCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .center, spacing: YYUI.s(6)) {
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(6)) {
                    Text(heading.0).font(YYUI.font(13, .bold)).foregroundStyle(YYColor.t1)
                    Text(heading.1).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
                Spacer()
                HStack(spacing: YYUI.s(4)) {
                    step("chevron.left", String(localized: "往前")) { move(-1) }
                    step("chevron.right", String(localized: "往后")) { move(1) }.disabled(isCurrent)
                }
            }
            HStack(spacing: YYUI.s(20)) {
                periodTotal(String(localized: "读"), times.reduce(0) { $0 + Int($1.readSeconds) }, tint.fill)
                periodTotal(String(localized: "听"), times.reduce(0) { $0 + Int($1.listenSeconds) }, YYColor.tint)
            }
            .padding(.top, YYUI.s(6))
            columns.frame(height: YYUI.s(50)).padding(.top, YYUI.s(12))
            axis.padding(.top, YYUI.s(5))
            if finished.isEmpty {
                Color.clear.frame(height: YYUI.s(12))
            } else {
                HStack(spacing: YYUI.s(10)) {
                    HStack(spacing: -YYUI.s(6)) {
                        ForEach(finished.prefix(3), id: \.id) { b in
                            BookCover(book: b).frame(width: YYUI.s(20))
                                .overlay(RoundedRectangle(cornerRadius: 3).strokeBorder(YYColor.card, lineWidth: 1.5))
                        }
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        Text(finishedHeading).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text(finished.map(\.title).joined(separator: " · ")).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.top, YYUI.s(10))
                .padding(.bottom, YYUI.s(11))
                .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                .padding(.top, YYUI.s(12))
            }
        }
        .padding(.top, YYUI.s(12))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
    }

    private func step(_ symbol: String, _ label: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol).font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t1)
                .frame(width: YYUI.s(24), height: YYUI.s(24))
                .background(YYColor.inset, in: .circle)
        }
        .buttonStyle(.plain)
        .accessibilityLabel(label)
    }

    /// "10 月" and "1 日 – 11 日"; "这周" and the days; "2026 年" and the months so far.
    private var heading: (String, String) {
        guard let first = days.first, let last = days.last else { return ("", "") }
        let cal = Calendar.current
        let end = isCurrent ? min(Date(), last) : last
        // Other languages go by month names: "October", "Oct 1 – 21", "Jan – Oct".
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

    /// One slot per day (per month over a year): listening on top of reading, today wider, the
    /// days to come a nub.
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
        let width: CGFloat = period == .week ? 8 : 5
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
                    .frame(width: YYUI.s(s.today ? width + 2 : width))
                    .frame(maxWidth: .infinity)
                }
            }
        }
    }

    /// Under the columns: the weekdays; 1, 8, 15, 22, 29 of a month; every other month of a year.
    private var axis: some View {
        let n = period == .year ? 12 : days.count
        return HStack(spacing: 0) {
            ForEach(0 ..< n, id: \.self) { i in
                Text(axisLabel(i)).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                    .fixedSize()
                    .frame(maxWidth: .infinity)
            }
        }
    }

    private func axisLabel(_ i: Int) -> String {
        switch period {
        case .week: return days.indices.contains(i) ? WeekBars.weekday(days[i]) : ""
        case .month: return i % 7 == 0 ? "\(i + 1)" : ""
        case .year:
            let base = days.first ?? anchor
            return i % 2 != 0 ? ""
                : (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") ? String(localized: "\(i + 1) 月")
                : (Calendar.current.date(byAdding: .month, value: i, to: base) ?? base).formatted(.dateTime.month(.abbreviated))
        }
    }

    /// A book, how far and how long to go, and the time read and listened.
    private func bookRow(_ book: BookSummary, _ read: Int, _ listen: Int) -> some View {
        HStack(spacing: YYUI.s(10)) {
            BookCover(book: book).frame(width: YYUI.s(24))
            VStack(alignment: .leading, spacing: YYUI.s(2)) {
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(8)) {
                    Text(book.title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Spacer(minLength: 0)
                    Text(book.status == .finished ? finishedLine(book) : String(localized: "\(book.percent) · \(book.remaining.replacingOccurrences(of: String(localized: "还剩"), with: String(localized: "还要")))"))
                        .font(YYUI.font(10.5).monospacedDigit()).foregroundStyle(YYColor.t2).lineLimit(1)
                }
                Text([read > 0 ? String(localized: "读 ") + Self.duration(read) : nil, listen > 0 ? String(localized: "听 ") + Self.duration(listen) : nil].compactMap { $0 }.joined(separator: " · "))
                    .font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
            }
        }
        .padding(.vertical, YYUI.s(8))
    }

    /// How the time is counted, on a grey card.
    private var how: some View {
        let dotOffset = YYUI.s(4)
        return VStack(alignment: .leading, spacing: 0) {
            Label("怎么算的", systemImage: "info.circle")
                .font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                .padding(.bottom, YYUI.s(3))
            ForEach([String(localized: "只在翻页、滚动或朗读的时候计时。"), String(localized: "一页停了 5 分钟没翻，或者锁屏又没在听，就停表。"), String(localized: "倍速按实际时间算：1.5× 听 1 小时，记 1 小时。")], id: \.self) { line in
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(6)) {
                    Circle().fill(YYColor.t3).frame(width: 3, height: 3).alignmentGuide(.firstTextBaseline) { $0[.bottom] + dotOffset }
                    Text(line).fixedSize(horizontal: false, vertical: true)
                }
                .font(YYUI.font(10.5)).foregroundStyle(YYColor.t2)
                .padding(.vertical, YYUI.s(2))
            }
        }
        .padding(.top, YYUI.s(11))
        .padding(.bottom, YYUI.s(12))
        .padding(.horizontal, YYUI.s(13))
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(14)))
    }

    private func load() {
        guard let first = days.first, let last = days.last else { return }
        let fresh = (try? library.core.dayTimes(from: Notes.day(first), to: Notes.day(last))) ?? []
        // Another week or month: the bars grow or shrink to it and the totals roll.
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

    private func finishedLine(_ b: BookSummary) -> String {
        b.finishedAt.map { String(localized: "\(Notes.monthDay(Notes.date($0)))读完") } ?? String(localized: "读完了")
    }

    private var perBook: [(BookSummary, Int, Int)] {
        var sums: [String: (Int, Int)] = [:]
        for t in times {
            sums[t.book, default: (0, 0)].0 += Int(t.readSeconds)
            sums[t.book, default: (0, 0)].1 += Int(t.listenSeconds)
        }
        return sums.compactMap { id, s in library.books.first { $0.id == id }.map { ($0, s.0, s.1) } }
            .sorted { $0.1 + $0.2 > $1.1 + $1.2 }
    }

    /// 5 小时 12 分.
    static func duration(_ seconds: Int) -> String {
        let m = seconds / 60
        if m < 60 { return String(localized: "\(m) 分") }
        return m % 60 == 0 ? String(localized: "\(m / 60) 小时") : String(localized: "\(m / 60) 小时 \(m % 60) 分")
    }
}
#endif
