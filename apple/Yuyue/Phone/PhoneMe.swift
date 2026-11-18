#if os(iOS)
import AuthenticationServices
import SwiftUI
@preconcurrency import Translation
import UniformTypeIdentifiers
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
                withAnimation(.easeOut(duration: 0.18)) { titlePinned = past }
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
        .task(id: library.books.map(\.id)) { load() }
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
            .buttonStyle(.plain)
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
                YYRow(String(localized: "反馈"), icon: "text.bubble", value: Feedback.early ? String(localized: "早期版本") : nil, chevron: true)
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
        let days = PhoneStats.Period.week.days(containing: Date())
        week = (try? library.core.dayTimes(from: Notes.day(days.first!), to: Notes.day(days.last!))) ?? []
        marks = library.books.filter(\.available).reduce(0) { $0 + ((try? library.core.marks(book: $1.id))?.count ?? 0) }
        words = ((try? library.core.words()) ?? []).count { !$0.known }
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
            return (0 ..< 12).map { m in
                let start = cal.date(byAdding: .month, value: m, to: days[0]) ?? days[0]
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
        case .week: WeekBars.weekday(days[i])
        case .month: i % 7 == 0 ? "\(i + 1)" : ""
        case .year:
            i % 2 != 0 ? ""
                : (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") ? String(localized: "\(i + 1) 月")
                : (Calendar.current.date(byAdding: .month, value: i, to: days[0]) ?? days[0]).formatted(.dateTime.month(.abbreviated))
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
        VStack(alignment: .leading, spacing: 0) {
            Label("怎么算的", systemImage: "info.circle")
                .font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                .padding(.bottom, YYUI.s(3))
            ForEach([String(localized: "只在翻页、滚动或朗读的时候计时。"), String(localized: "一页停了 5 分钟没翻，或者锁屏又没在听，就停表。"), String(localized: "倍速按实际时间算：1.5× 听 1 小时，记 1 小时。")], id: \.self) { line in
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(6)) {
                    Circle().fill(YYColor.t3).frame(width: 3, height: 3).alignmentGuide(.firstTextBaseline) { $0[.bottom] + YYUI.s(4) }
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

// MARK: - 外观

/// 外观: light, dark or the system's; 墨蓝 or 墨黑; and a look at both.
struct PhoneAppearance: View {
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    @AppStorage("shelf.showProgress") private var showProgress = true
    @AppStorage("shelf.coverSize") private var coverSize = 100.0
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(LiquidGlass.key) private var liquid = false
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "应用界面的外观与配色；书页纸色在「阅读」或 Aa 中设置"))
                .padding(.bottom, YYUI.s(12))
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(10)) {
                    Text("界面").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                    HStack(spacing: YYUI.s(10)) {
                        ForEach(AppAppearance.allCases) { m in modeTile(m) }
                    }
                }
                .padding(.vertical, YYUI.s(12))
            }
            .padding(.bottom, YYUI.s(12))
            YYGroup {
                HStack(alignment: .center) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("主题色").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("用于按钮、开关与进度标记").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: YYUI.s(6))
                    HStack(spacing: YYUI.s(6)) { ForEach(YYTint.all) { t in tintChoice(t) } }
                }
                .padding(.vertical, YYUI.s(12))
            }
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(8)) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("书架封面大小").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("随屏幕宽度自动排布").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    HStack(spacing: YYUI.s(10)) {
                        RoundedRectangle(cornerRadius: 1.5).fill(YYColor.tint).frame(width: 8, height: 11)
                        Slider(value: $coverSize, in: 80 ... 130, step: 10).tint(YYTint.standard.fill)
                            .accessibilityLabel(Text("书架封面大小"))
                        RoundedRectangle(cornerRadius: 2).fill(YYColor.tint).frame(width: 11, height: 16)
                    }
                }
                .padding(.vertical, YYUI.s(10))
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("封面下显示进度").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("关闭后仅显示书名").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $showProgress, label: String(localized: "封面下显示进度"))
                }
                .padding(.vertical, YYUI.s(10))
            }
            .padding(.top, YYUI.s(12))
            if LiquidGlass.supported {
                YYGroup {
                    HStack(spacing: YYUI.s(10)) {
                        VStack(alignment: .leading, spacing: YYUI.s(2)) {
                            Text("液态玻璃效果").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                            Text("浮动工具栏与提示条使用系统液态玻璃，关闭则为毛玻璃").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: YYUI.s(8))
                        YYSwitch(isOn: $liquid, label: String(localized: "液态玻璃效果"))
                    }
                    .padding(.vertical, YYUI.s(10))
                }
                .padding(.top, YYUI.s(12))
            }
        }
        .navigationTitle("外观")
        .navigationBarTitleDisplayMode(.inline)
        .animation(YYMotion.quick, value: tintID)
        .animation(YYMotion.standard, value: mode)
    }

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A little page in light, dark, or both split down a slant; the chosen one ringed.
    private func modeTile(_ m: AppAppearance) -> some View {
        let on = m == mode
        let light = (bg: Color(yy: YYRGBA(246, 245, 243)), bar: Color.white, title: Color(yy: YYRGBA(42, 43, 46)).opacity(0.75))
        let dark = (bg: Color(yy: YYRGBA(16, 17, 18)), bar: Color(yy: YYRGBA(27, 28, 30)), title: Color(yy: YYRGBA(237, 237, 238)).opacity(0.75))
        func page(_ c: (bg: Color, bar: Color, title: Color)) -> some View {
            ZStack(alignment: .topLeading) {
                c.bg
                GeometryReader { g in
                    VStack(alignment: .leading, spacing: YYUI.s(5)) {
                        RoundedRectangle(cornerRadius: 3).fill(c.title).frame(width: g.size.width * 0.55, height: YYUI.s(7)).padding(.bottom, YYUI.s(3))
                        RoundedRectangle(cornerRadius: 3).fill(c.bar).frame(height: YYUI.s(9))
                        RoundedRectangle(cornerRadius: 3).fill(c.bar).frame(height: YYUI.s(9))
                    }
                }
                .padding(.top, YYUI.s(13)).padding(.horizontal, YYUI.s(11))
            }
        }
        return Button { mode = m } label: {
            VStack(spacing: YYUI.s(7)) {
                ZStack {
                    switch m {
                    case .light: page(light)
                    case .dark: page(dark)
                    case .system:
                        page(light)
                        page(dark).mask(Slant())
                    }
                }
                .frame(height: YYUI.s(74))
                .clipShape(.rect(cornerRadius: YYUI.s(13)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(13)).strokeBorder(YYColor.line, lineWidth: 0.5))
                .padding(on ? YYUI.s(4.5) : 0)
                .overlay {
                    if on {
                        RoundedRectangle(cornerRadius: YYUI.s(13) + YYUI.s(4.5)).strokeBorder(tint.accentSoft, lineWidth: YYUI.s(2.5))
                        RoundedRectangle(cornerRadius: YYUI.s(13) + YYUI.s(2)).strokeBorder(tint.accent, lineWidth: YYUI.s(2)).padding(YYUI.s(2.5))
                    }
                }
                .padding(on ? 0 : YYUI.s(4.5))
                Text(m.title).font(YYUI.font(11, on ? .semibold : .regular)).foregroundStyle(on ? YYColor.t1 : YYColor.t2)
            }
            .padding(.horizontal, -YYUI.s(4.5))
            .frame(maxWidth: .infinity)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    /// The dark half of 跟随系统: right of a line leaning 20° off upright.
    private nonisolated struct Slant: Shape {
        func path(in r: CGRect) -> Path {
            let lean = r.height * tan(.pi / 9) / 2
            var p = Path()
            p.move(to: CGPoint(x: r.midX + lean, y: r.minY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.minY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.maxY))
            p.addLine(to: CGPoint(x: r.midX - lean, y: r.maxY))
            p.closeSubpath()
            return p
        }
    }

    /// A colour, ringed when chosen, its name under it — as the Mac's 外观 has them.
    private func tintChoice(_ t: YYTint) -> some View {
        let on = tintID == t.id
        return Button { tintID = t.id } label: {
            VStack(spacing: YYUI.s(5)) {
                Circle().fill(t.accent)
                    .frame(width: YYUI.s(22), height: YYUI.s(22)).padding(YYUI.s(2.5))
                    .overlay(Circle().strokeBorder(on ? t.accent : .clear, lineWidth: 2))
                Text(t.name).font(YYUI.font(10, on ? .semibold : .regular))
                    .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1).fixedSize()
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

// MARK: - 阅读

/// 阅读, as the Mac's: what a book opens with — paper, font, size, spacing, how pages turn and
/// how the text sits — with a sample of 《故乡》 on top; a book's own Aa changes only that book.
/// 沉浸阅读 and 墨水屏 sit here too, as the Mac keeps them under 阅读.
struct PhoneReading: View {
    let library: Library
    @State private var settings = ReaderSettings.standard
    @AppStorage(ImmersivePrefs.alwaysKey) private var immersiveAlways = false
    @AppStorage(ImmersivePrefs.lineKey) private var immersiveLine = true
    @AppStorage(ImmersivePrefs.marksKey) private var immersiveMarks = false
    @AppStorage(ImmersivePrefs.hintsKey) private var immersiveHints = true
    @AppStorage(ImmersivePrefs.stillKey) private var immersiveStill = true
    @AppStorage(ImmersivePrefs.statusKey) private var immersiveStatus = true
    @AppStorage(ImmersivePrefs.progressKey) private var immersiveProgress = true
    @AppStorage(EInk.key) private var einkOn = false

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "新书的默认排版；单本书可在阅读页 Aa 中单独调整"))
            sample.padding(.top, YYUI.s(12))
            YYGroup {
                tiles(String(localized: "纸色"), columns: 3) {
                    ForEach(YYPaper.all) { paper in
                        SettingsPaperTile(paper: paper, texture: paper.hasNoiseTexture ? .grain : .none, name: paper.name,
                                          selected: settings.paperChoice == paper.id) {
                            change { $0.paper = Paper(id: paper.id) ?? .plain; $0.customPaper = nil }
                        }
                    }
                    ForEach(Papers.shared.list, id: \.id) { mine in
                        SettingsPaperTile(paper: YYPaper(custom: mine), texture: mine.texture, name: mine.name,
                                          selected: settings.paperChoice == mine.id) {
                            let preset = paperColors(paper: mine).preset
                            change { $0.customPaper = mine.id; $0.paper = preset }
                        }
                    }
                }
                PhoneFontTiles(selected: FontShelf.id(of: settings)) { id in
                    change { FontShelf.shared.apply(id, to: &$0) }
                    FontShelf.shared.ensure(id)
                }
                HStack(spacing: YYUI.s(10)) {
                    Text("字号").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    Slider(value: Binding(get: { settings.fontSize }, set: { v in change { $0.fontSize = v.rounded() } }),
                           in: TypesetCard.sizes)
                        .tint(YYTint.standard.fill)
                    Text("\(Int(settings.fontSize))").font(YYUI.font(11).monospacedDigit()).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(20), alignment: .trailing)
                }
                .frame(minHeight: YYUI.s(38))
                YYRow(title: String(localized: "行距")) {
                    YYSegments(items: [(Double?.some(1.6), String(localized: "紧")), (nil, String(localized: "适中")), (.some(2.2), String(localized: "松"))],
                               selection: Binding(get: { settings.lineHeight }, set: { v in change { $0.lineHeight = v } }), small: true)
                        .fixedSize()
                }
            }
            .padding(.top, YYUI.s(12))
            YYGroup {
                YYRow(title: String(localized: "翻页方式")) {
                    YYSegments(items: [(LayoutMode.paginated, String(localized: "左右翻页")), (.scrolled, String(localized: "上下滚动"))],
                               selection: Binding(get: { settings.mode }, set: { v in change { $0.mode = v } }), small: true)
                        .fixedSize()
                }
                YYRow(title: String(localized: "两端对齐")) {
                    YYSwitch(isOn: Binding(get: { settings.justify ?? true }, set: { v in change { $0.justify = v } }), label: String(localized: "两端对齐"))
                }
                YYRow(title: String(localized: "首行缩进两字")) {
                    YYSwitch(isOn: Binding(get: { settings.indent ?? true }, set: { v in change { $0.indent = v } }), label: String(localized: "首行缩进两字"))
                }
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("中文标点挤压").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("收紧连续标点的留白").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: Binding(get: { settings.compress }, set: { v in change { $0.compress = v } }), label: String(localized: "中文标点挤压"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            .padding(.top, YYUI.s(12))
            PhoneSettingsHeading(String(localized: "沉浸阅读"))
            YYGroup {
                detailRow(String(localized: "进入与退出"), String(localized: "双击屏幕边缘进出，轻点中央临时唤出控制栏")) { EmptyView() }
                detailRow(String(localized: "打开书籍时自动进入"), nil) {
                    YYSwitch(isOn: $immersiveAlways, label: String(localized: "打开书籍时自动进入"))
                }
                detailRow(String(localized: "底部保留进度细线"), nil) {
                    YYSwitch(isOn: $immersiveLine, label: String(localized: "底部保留进度细线"))
                }
                detailRow(String(localized: "隐藏划线与想法"), String(localized: "退出沉浸后恢复显示")) {
                    YYSwitch(isOn: $immersiveMarks, label: String(localized: "隐藏划线与想法"))
                }
                detailRow(String(localized: "隐藏书伴提示与难词注释"), nil) {
                    YYSwitch(isOn: $immersiveHints, label: String(localized: "隐藏书伴提示与难词注释"))
                }
                detailRow(String(localized: "关闭翻页动画"), String(localized: "直接切换整页，无滑动过渡")) {
                    YYSwitch(isOn: $immersiveStill, label: String(localized: "关闭翻页动画"))
                }
                // The phone's own: its status bar and the line of where you are.
                detailRow(String(localized: "隐藏状态栏、时间和电量"), nil) {
                    YYSwitch(isOn: $immersiveStatus, label: String(localized: "隐藏状态栏、时间和电量"))
                }
                detailRow(String(localized: "隐藏进度、页码和剩余时间"), nil) {
                    YYSwitch(isOn: $immersiveProgress, label: String(localized: "隐藏进度、页码和剩余时间"))
                }
                detailRow(String(localized: "专注模式联动"), String(localized: "在系统「设置 › 专注模式」中添加「阅读」以自动开启免打扰")) { EmptyView() }
            }
            YYGroup {
                NavigationLink { PhoneEInk(library: library) } label: {
                    YYRow(String(localized: "墨水屏"), icon: "rectangle.portrait.on.rectangle.portrait", value: einkOn ? String(localized: "开") : String(localized: "关"), chevron: true)
                }
                .buttonStyle(.plain)
            }
            .padding(.top, YYUI.s(12))
        }
        .navigationTitle("阅读")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { settings = (try? library.core.readerSettings()) ?? .standard }
        .onReceive(NotificationCenter.default.publisher(for: .readerSettingsChanged)) { _ in
            settings = (try? library.core.readerSettings()) ?? settings
        }
    }

    /// A setting's name with a grey line under it, its control at the end — the Mac's SettingsRow.
    private func detailRow<C: View>(_ title: String, _ detail: String?, @ViewBuilder control: () -> C) -> some View {
        HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                if let detail {
                    Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: YYUI.s(8))
            control()
        }
        .padding(.vertical, YYUI.s(detail == nil ? 4 : 8))
        .frame(minHeight: YYUI.s(38))
    }

    /// The opening of 《故乡》 on the chosen paper, in the chosen font, size and spacing.
    private var sample: some View {
        let paper = ReaderPaperOf.paper(settings)
        let size = (settings.fontSize * 0.8).rounded()
        let id = FontShelf.id(of: settings)
        let name = FontShelf.shared.family(id)
        let line = "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"
        // A face still coming down: the few characters of the sample from it, until it's here.
        let preview = FontShelf.shared.isReady(id) ? nil : FontPreview.shared.font(id, text: line, size: size)
        let face: SwiftUI.Font = preview ?? name.map { .custom($0, fixedSize: size) } ?? .system(size: size)
        let lineHeight = settings.lineHeight ?? paper.text.lineHeight
        let indent = settings.indent ?? true ? "\u{3000}\u{3000}" : ""
        return VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack {
                Text("故乡")
                Spacer()
                Text(verbatim: "12 / 31").monospacedDigit()
            }
            .font(YYUI.font(8.5))
            .foregroundStyle(paper.ink.opacity(0.45))
            Text(indent + line)
                .font(face).lineSpacing(size * max(0, lineHeight - 1.25))
                .foregroundStyle(paper.ink)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, YYUI.s(14)).padding(.vertical, YYUI.s(12))
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .background { paper.bg.overlay { PaperTextureView(texture: ReaderPaperOf.texture(settings), dark: paper.isDark) } }
        .clipShape(.rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: paper.isDark ? 0 : 0.5))
        .onAppear { if !FontShelf.shared.isReady(id) { FontPreview.shared.load(id, text: line) } }
        .onChange(of: id) { if !FontShelf.shared.isReady(id) { FontPreview.shared.load(id, text: line) } }
        .animation(YYMotion.standard, value: settings.paperChoice)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("样张"))
    }

    private func tiles<Content: View>(_ title: String, columns: Int, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            Text(title).font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: YYUI.s(6)), count: columns), spacing: YYUI.s(6)) { content() }
        }
        .padding(.vertical, YYUI.s(12))
    }

    private func change(_ edit: (inout ReaderSettings) -> Void) {
        var new = settings
        edit(&new)
        guard new != settings else { return }
        SettingsLog.shared.reader(from: settings, to: new)
        settings = new
        try? library.core.setReaderSettings(settings: new)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }
}

/// 朗读, as the Mac's: 声音来源 — this device, OpenAI, Azure, ElevenLabs — each with what it reads
/// now; one opens its voices (or, not connected, its key); 语速 under them.
struct PhoneListening: View {
    @AppStorage("player.rate") private var rate = 1.0
    @State private var tick = 0
    private var shownRate: Double { rate == 0 ? 1 : rate }

    var body: some View {
        let _ = tick
        YYPage {
            PhoneSettingsLead(String(localized: "朗读、查词发音与播客音色"))
            PhoneSettingsHeading(String(localized: "声音来源"))
            YYGroup {
                ForEach(VoiceSource.all, id: \.self) { source in
                    NavigationLink { PhoneVoiceSourcePage(source: source) } label: { row(source) }
                        .buttonStyle(.plain)
                }
            }
            YYNote(String(localized: "云端语音使用你自己的 API Key，费用由服务商直接结算；已合成的句子缓存在本机，重听不重复计费。"))
            PhoneSettingsHeading(String(localized: "语速"))
            YYGroup {
                HStack(spacing: YYUI.s(8)) {
                    Text("慢").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Slider(value: Binding(get: { shownRate }, set: { rate = ($0 * 10).rounded() / 10 }), in: 0.8 ... 2.0)
                        .tint(YYTint.standard.fill)
                        .accessibilityLabel(Text("语速"))
                        .accessibilityValue(Text(String(format: "%.1f×", shownRate)))
                    Text("快").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Menu {
                        ForEach([0.8, 1.0, 1.2, 1.5, 2.0], id: \.self) { r in
                            Button { rate = r } label: {
                                if abs(shownRate - r) < 0.05 { Label(String(format: "%.1f×", r), systemImage: "checkmark") }
                                else { Text(String(format: "%.1f×", r)) }
                            }
                        }
                    } label: {
                        HStack(spacing: YYUI.s(3)) {
                            Text(String(format: "%.1f×", shownRate)).font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                            Image(systemName: "chevron.up.chevron.down").font(YYUI.font(8, .semibold)).foregroundStyle(YYColor.t3)
                        }
                        .padding(.leading, YYUI.s(4))
                        .contentShape(.rect)
                    }
                    .accessibilityLabel(Text("语速"))
                }
                .frame(minHeight: YYUI.s(40))
            }
            YYNote(String(localized: "与阅读页听书面板同步。"))
        }
        .navigationTitle("朗读")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { tick &+= 1 }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in tick &+= 1 }
    }

    /// A source: its tile, name and a line; then the books reading with it, or 填 Key.
    private func row(_ source: VoiceSource) -> some View {
        HStack(spacing: YYUI.s(10)) {
            ServiceTile(mark: source.mark, size: YYUI.s(26))
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(source.title).font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                Text(detail(source)).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: YYUI.s(6))
            VStack(alignment: .trailing, spacing: YYUI.s(1)) {
                let reading = VoiceBook.allCases.compactMap { b in b.current.flatMap { VoiceSource.of($0) == source ? "\(b.title) · \($0.name)" : nil } }
                if !source.isSetUp {
                    Text("未连接").font(YYUI.font(10.5)).foregroundStyle(YYColor.t2)
                } else {
                    ForEach(reading, id: \.self) {
                        Label($0, systemImage: "pin.fill").labelStyle(PinLabelStyle(size: 9))
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                }
            }
            Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
        }
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }

    private func detail(_ source: VoiceSource) -> String {
        switch source {
        case .mac: String(localized: "\(source.voiceCount) 个音色 · 本地免费，无需联网")
        case .cloud(let p): p.isSetUp ? String(localized: "\(p.voiceCount) 个音色 · \(p.blurb)") : p.blurb
        }
    }
}

/// One source's page, as the Mac's middle column: 中文书 / 英文书, the one reading pinned, your
/// line and every voice saying it as a voice message, the box to send them another line. ⚙ in
/// the bar: its key and connection (a cloud one), or what this device has. Not connected yet: the
/// key, typed straight in.
private struct PhoneVoiceSourcePage: View {
    let source: VoiceSource
    @State private var tick = 0

    var body: some View {
        let _ = tick
        Group {
            if source.isSetUp {
                VoiceSourcePage(source: source)
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    .padding(.bottom, 8)
                    .toolbar {
                        ToolbarItem(placement: .topBarTrailing) {
                            NavigationLink { PhoneSourceSettings(source: source) { tick &+= 1 } } label: {
                                Image(systemName: "gearshape")
                            }
                            .accessibilityLabel(Text("\(source.title) 的设置"))
                        }
                    }
            } else if case .cloud(let p) = source {
                ScrollView {
                    PhoneCloudKey(provider: p) { tick &+= 1 }.padding(.top, YYUI.s(10))
                }
                .scrollBounceBehavior(.basedOnSize)
                .transition(.opacity)
            }
        }
        // A Key that works: the voices take the key page's place, not in a blink.
        .animation(YYMotion.standard, value: source.isSetUp)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(YYColor.bg)
        .navigationTitle(source.title)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { ListeningFocus.shared.reload() }
    }
}

/// ⚙ on a source's page: what the Mac shows for it in the right column — this device's voices and
/// where to get better ones; a cloud one's key, region, 检查连接, and 断开 at the foot.
private struct PhoneSourceSettings: View {
    let source: VoiceSource
    let changed: () -> Void
    @State private var removing = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                switch source {
                case .mac:
                    PanelHeader(title: source.title, detail: String(localized: "系统自带")) { ServiceTile(mark: source.mark, size: 30) }
                    PanelCard {
                        PanelRow { Text("声音") } trailing: {
                            Text("\(source.voiceCount) 个").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                        }
                        PanelRow { Text("费用") } trailing: {
                            Text("免费 · 不联网").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                        }
                    }
                    PanelHint(String(localized: "前往「设置 › 辅助功能 › 朗读内容 › 声音」，下载增强或自然音色即可使用。"))
                case .cloud(let p):
                    PhoneCloudConnected(provider: p, changed: changed).padding(.horizontal, -16)
                    PanelDestructive(String(localized: "断开 \(p.title)")) { removing = true }
                        .confirmationDialog(String(localized: "断开 \(p.title)？"), isPresented: $removing) {
                            Button("断开", role: .destructive) {
                                p.remove()
                                changed()
                                dismiss()
                            }
                        } message: {
                            Text("将同时移除 API Key；使用该服务音色的书籍将切回本地音色。")
                        }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, YYUI.s(10))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// A cloud service not connected yet: its Key typed straight in (Azure's region above it),
/// tried once it looks whole; where to get one, as a row that opens the page.
private struct PhoneCloudKey: View {
    let provider: CloudVoice.Provider
    let connected: () -> Void
    @State private var key = ""
    @State private var region = CloudVoice.Provider.region
    @State private var connect = AutoConnect()
    @Environment(\.openURL) private var openURL

    var body: some View {
        let step = provider.keyStep
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.blurb) { ServiceTile(mark: .cloud(provider), size: 30) }
            PanelCard {
                if provider == .azure { PhoneRegionRow(region: $region) }
                PanelField(label: "Key", text: $key, prompt: provider == .azure ? String(localized: "密钥 1") : provider.keyHint,
                           secure: true, bad: connect.failed, submit: { attempt(now: true) })
                PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                    Text("检查连接").foregroundStyle(YYColor.ok)
                    Spacer(minLength: 8)
                    if connect.busy { ProgressView().controlSize(.mini) }
                    else if key.isEmpty { Text("先填 Key").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3) }
                } action: { attempt(now: true) }
                .opacity(key.isEmpty && !connect.busy ? 0.6 : 1)
            }
            AutoConnectStatus(connect: connect)
            PanelCard {
                PanelButton(systemImage: "arrow.up.forward", tint: YYColor.t1) {
                    Text(step.title)
                    Spacer(minLength: 0)
                } action: { openURL(step.url) }
            }
            PanelHint(step.detail + String(localized: "。API Key 仅保存在本机钥匙串，调用费用由服务商直接结算。"))
        }
        .padding(.horizontal, 16)
        .onChange(of: key) { attempt(now: false) }
        .onChange(of: region) { if !key.isEmpty { attempt(now: true) } }
    }

    private func attempt(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        let p = provider, r: String? = provider == .azure ? region : nil
        connect.start(String(localized: "正在连 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r)
        } done: { connected() }
    }
}

/// A connected cloud service: its Key (pressed, typed again in the row), Azure's region,
/// 检查连接 as the card's last row; 断开 at the foot of the page.
private struct PhoneCloudConnected: View {
    let provider: CloudVoice.Provider
    let changed: () -> Void
    @State private var editing = false
    @State private var key = ""
    @State private var region = CloudVoice.Provider.region
    @State private var connect = AutoConnect()
    @State private var check = AutoConnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.inUse ? String(localized: "当前使用") : provider.account) {
                ServiceTile(mark: .cloud(provider), size: 30)
            }
            PanelCard {
                if editing {
                    PanelField(label: "Key", text: $key, prompt: String(localized: "贴上新的 Key"), secure: true, bad: connect.failed,
                               autofocus: true, submit: { attempt(now: true) }, cancel: stop)
                } else {
                    PanelButton {
                        Text(verbatim: "Key")
                        Spacer(minLength: 8)
                        Text(provider.maskedKey).font(.yy(size: PanelScale.k * 12, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1)
                        Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                    } action: { editing = true }
                }
                if provider == .azure { PhoneRegionRow(region: $region) }
                PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                    Text("检查连接").foregroundStyle(YYColor.ok)
                    Spacer(minLength: 8)
                    switch check.phase {
                    case .idle: EmptyView()
                    case .checking: ProgressView().controlSize(.mini)
                    case .ok: SettingsPill(String(localized: "正常 · \(provider.voiceCount) 个音色"), tone: .ok)
                    case .failed(let why):
                        Text(why).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.danger).lineLimit(2).multilineTextAlignment(.trailing)
                    }
                } action: {
                    guard !check.busy, let k = provider.key else { return }
                    let p = provider, r: String? = provider == .azure ? region : nil
                    check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { changed() }
                }
            }
            if editing {
                AutoConnectStatus(connect: connect)
                if case .idle = connect.phase { PanelHint(String(localized: "粘贴后自动验证，有效才会替换。")) }
            }
        }
        .animation(YYMotion.standard, value: editing)
        .padding(.horizontal, 16)
        .onChange(of: key) { attempt(now: false) }
        .onChange(of: region) {
            guard let k = provider.key else { return }
            let p = provider, r = region
            check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { changed() }
        }
    }

    private func stop() {
        editing = false
        key = ""
        connect.reset()
    }

    private func attempt(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        let p = provider, r: String? = provider == .azure ? region : nil
        connect.start(String(localized: "正在试新的 Key…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r)
        } done: {
            stop()
            changed()
        }
    }
}

/// Azure's region, from a menu at the end of the row.
private struct PhoneRegionRow: View {
    @Binding var region: String

    var body: some View {
        PanelRow { Text("区域") } trailing: {
            Menu {
                ForEach(CloudVoice.Provider.regions, id: \.id) { r in
                    Button { region = r.id } label: {
                        if r.id == region { Label("\(r.name) · \(r.id)", systemImage: "checkmark") } else { Text("\(r.name) · \(r.id)") }
                    }
                }
            } label: {
                HStack(spacing: 4) {
                    Text(CloudVoice.Provider.regions.first { $0.id == region }.map { "\($0.name) · \($0.id)" } ?? region)
                        .font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                    Image(systemName: "chevron.up.chevron.down").font(.yy(size: PanelScale.k * 8.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                }
            }
        }
    }
}

// MARK: - 外文阅读

/// 外文阅读: saying a word aloud, 难词注释, the 生词 marks, and the
/// system's offline translation.
struct PhoneForeign: View {
    let library: Library
    @AppStorage(ForeignPrefs.speakKey) private var speak = true
    @AppStorage(ForeignPrefs.accentKey) private var accent = ForeignPrefs.Accent.british
    @AppStorage(ForeignPrefs.markKey) private var mark = true
    @AppStorage(ForeignPrefs.glossKey) private var gloss = true
    @AppStorage(ForeignPrefs.levelKey) private var level = 3
    @AppStorage(ForeignPrefs.glossVocabKey) private var glossVocab = false
    @State private var status: LanguageAvailability.Status?
    @State private var download: TranslationSession.Configuration?
    @State private var failed: String?

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "查词、难词注释、生词与离线翻译"))
            groupLabel(String(localized: "查词")).padding(.top, YYUI.s(16))
            YYGroup {
                YYRow(title: String(localized: "查词时自动发音")) { YYSwitch(isOn: $speak, label: String(localized: "查词时自动发音")) }
                YYRow(title: String(localized: "英文发音")) {
                    YYSegments(items: [(ForeignPrefs.Accent.british, String(localized: "英音")), (ForeignPrefs.Accent.american, String(localized: "美音"))], selection: $accent, small: true)
                        .fixedSize(horizontal: true, vertical: false)
                }
            }
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "MDict 词典"))
            PhoneMDictList(core: library.core)
            YYNote(String(localized: "按列表顺序优先查词，左滑可移除。"))
                .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "难词注释"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("难词行间释义").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("阅读英文书时，在较难词汇上方标注简短中文词义").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $gloss, label: String(localized: "难词行间释义"))
                }
                .padding(.vertical, YYUI.s(8))
                VStack(alignment: .leading, spacing: YYUI.s(6)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("词汇难度").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("等级越高，仅标注更生僻的词").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    let levelSegments = YYSegments(items: ForeignPrefs.levels.map { (Int($0.level), $0.name) }, selection: $level, small: true)
                    ViewThatFits(in: .horizontal) {
                        levelSegments
                        ScrollView(.horizontal, showsIndicators: false) {
                            levelSegments.fixedSize(horizontal: true, vertical: false)
                        }
                    }
                    .disabled(!gloss || glossVocab)
                    .opacity(!gloss || glossVocab ? 0.5 : 1)
                    .animation(YYMotion.quick, value: !gloss || glossVocab)
                }
                .padding(.vertical, YYUI.s(8))
                YYRow(title: String(localized: "仅标注生词本中的词")) { YYSwitch(isOn: $glossVocab, label: String(localized: "仅标注生词本中的词")).disabled(!gloss) }
                glossPreview
            }
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "生词"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(3)) {
                        Text("在书中标记生词").font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text("…this truth is so well \(Text("fixed").foregroundStyle(YYColor.t1).underline(mark, pattern: .dot, color: YYTint.standard.accent)) in the minds…")
                            .font(.custom("Georgia", size: YYUI.s(11)))
                            .foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: 0)
                    YYSwitch(isOn: $mark, label: String(localized: "在书中标记生词"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            YYNote(String(localized: "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记。"))
                .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "翻译"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("系统离线翻译").font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text("本地离线运行，原文不上传；供整页译文与双击整句翻译使用").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    translationState
                }
                .padding(.vertical, YYUI.s(8))
                if let failed {
                    Label(failed, systemImage: "exclamationmark.circle").font(YYUI.font(10.5)).foregroundStyle(YYColor.danger)
                        .frame(minHeight: YYUI.s(34))
                        .transition(.opacity)
                }
            }
            .animation(YYMotion.standard, value: failed)
            YYNote(String(localized: "原文不出这台设备。"))
        }
        .navigationTitle("外文")
        .navigationBarTitleDisplayMode(.inline)
        .onChange(of: "\(gloss)\(level)\(glossVocab)\(mark)") { NotificationCenter.default.post(name: .vocabChanged, object: nil) }
        .task { await check() }
        .translationTask(download) { session in
            do {
                try await session.prepareTranslation()
                failed = nil
            } catch {
                failed = Translator.message(for: error)
            }
            download = nil
            await check()
        }
    }

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A line of Austen with the gloss over the hard word, changing as the settings do.
    private var glossPreview: some View {
        VStack(alignment: .leading, spacing: YYUI.s(4)) {
            Text("预览").font(YYUI.font(10)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(6))
            HStack(alignment: .lastTextBaseline, spacing: 0) {
                Text(verbatim: "It is a truth universally ")
                VStack(spacing: 0) {
                    Text(verbatim: "公认").font(YYUI.font(9)).foregroundStyle(YYColor.t2)
                        .opacity(gloss && !glossVocab && level <= 4 ? 1 : 0)
                    Text(verbatim: "acknowledged")
                }
                Text(verbatim: ", that a…")
            }
            .font(.custom("Georgia", size: YYUI.s(12)))
            .foregroundStyle(YYColor.t1)
            .lineLimit(1)
            .minimumScaleFactor(0.8)
            .padding(.bottom, YYUI.s(6))
        }
        .animation(YYMotion.standard, value: "\(gloss)\(level)\(glossVocab)")
    }

    @ViewBuilder private var translationState: some View {
        switch status {
        case .installed?:
            Image(systemName: "checkmark").font(YYUI.font(11, .bold)).foregroundStyle(YYTint.standard.accent)
                .accessibilityLabel("英文 → 中文 已下载")
        case .supported?:
            Button("下载英文 → 中文") {
                download = .init(source: Locale.Language(identifier: "en"), target: Translator.target)
            }
            .buttonStyle(YYButtonStyle(kind: .soft, size: .small))
            .disabled(download != nil)
        case .unsupported?:
            Text("当前系统不支持").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
        default:
            ProgressView().controlSize(.small)
        }
    }

    private func check() async {
        let s = await LanguageAvailability().status(from: Locale.Language(identifier: "en"), to: Translator.target)
        withAnimation(YYMotion.standard) { status = s }
    }
}

// MARK: - 书伴

/// 书伴, as the Mac's: one list — this device's own (Apple 智能), the services added, 添加服务商.
/// A service opens its page: model, 书伴用它, its key typed in the row, 检查连接, 移除.
/// 添加服务商 is the picker; a service picked is typed into and tried on its own.
struct PhoneCompanionSettings: View {
    @State private var sheet: CompanionProviderSheet.Start?
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let choice = focus.choice
        let _ = focus.tick
        let apple = Companion.appleAvailability == nil
        YYPage {
            PhoneSettingsLead(String(localized: "阅读问答与播客撰稿所用的大模型"))
            PhoneSettingsHeading(String(localized: "模型服务"))
            YYGroup {
                NavigationLink { PhoneCompanionService(provider: .apple) } label: {
                    row(.thisMac, dim: !apple) {
                        Text(SettingsDevice.name)
                        if apple && choice.current.provider.kind == .apple { SettingsPill(String(localized: "当前使用"), tone: .ok) }
                    } detail: {
                        Text("Apple 智能 · 本地运行，免费免联网")
                    } trailing: {
                        switch Companion.appleState {
                        case .available: EmptyView()
                        case .notEnabled: SettingsPill(String(localized: "未开启"), tone: .warn)
                        case .downloading: SettingsPill(String(localized: "准备中"), tone: .warn)
                        case .unsupported: SettingsPill(String(localized: "当前系统不支持"), tone: .warn)
                        }
                    }
                }
                .buttonStyle(.plain)
                ForEach(choice.added) { p in
                    let config = CompanionConfig.saved(p)
                    NavigationLink { PhoneCompanionService(provider: p) } label: {
                        row(.provider(p)) {
                            Text(p.title)
                            if choice.current.provider == p { SettingsPill(String(localized: "当前使用"), tone: .ok) }
                        } detail: {
                            Text(([config.model.isEmpty ? String(localized: "未选择模型") : ModelNames.name(config.model)]
                                  + (config.key.isEmpty ? [] : ["Key " + config.maskedKey])).joined(separator: " · "))
                        } trailing: {
                            EmptyView()
                        }
                    }
                    .buttonStyle(.plain)
                }
                Button { sheet = .add } label: {
                    row(.add) {
                        Text("添加服务商").foregroundStyle(YYColor.t2)
                    } detail: {
                        Text("Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、本机 Ollama…")
                    } trailing: {
                        EmptyView()
                    }
                }
                .buttonStyle(.plain)
            }
            YYNote(String(localized: "API Key 仅保存在本机钥匙串，不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"))
        }
        .navigationTitle("书伴")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $sheet, onDismiss: { focus.reload() }) { CompanionProviderSheet(start: $0) }
        .onAppear {
            focus.reload()
            #if DEBUG
            // `-debugCompanion add` opens 添加服务商, `edit:<id>` that service's form.
            if let d = UserDefaults.standard.string(forKey: "debugCompanion"), sheet == nil {
                if d == "add" { sheet = .add }
                else if d.hasPrefix("edit:"), let p = CompanionProvider.find(String(d.dropFirst(5))) { sheet = .edit(p) }
            }
            #endif
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
        }
    }

    /// A service: its tile, its name, a grey line; the chevron at the end.
    private func row<Title: View, Detail: View, Trailing: View>(
        _ mark: ServiceTile.Mark, dim: Bool = false,
        @ViewBuilder title: () -> Title, @ViewBuilder detail: () -> Detail, @ViewBuilder trailing: () -> Trailing
    ) -> some View {
        HStack(spacing: YYUI.s(10)) {
            ServiceTile(mark: mark, size: YYUI.s(26)).opacity(dim ? 0.5 : 1)
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                HStack(spacing: YYUI.s(6)) { title() }
                    .font(YYUI.font(11.5, .medium)).foregroundStyle(dim ? YYColor.t3 : YYColor.t1)
                detail().font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: YYUI.s(8))
            trailing()
            Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
        }
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }
}

/// One service, as the Mac's right column shows it; its models one page further.
struct PhoneCompanionService: View {
    let provider: CompanionProvider
    @State private var models = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            ServicePanel(provider: provider, openModels: { models = true }, removed: { dismiss() })
                .padding(.top, YYUI.s(10))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { CompanionFocus.shared.reload() }
        .navigationDestination(isPresented: $models) {
            ScrollView {
                ModelList(provider: provider, back: { models = false })
                    .padding(.top, YYUI.s(10))
            }
            .scrollBounceBehavior(.basedOnSize)
            .background(YYColor.bg)
            .navigationTitle("选择模型")
            .navigationBarTitleDisplayMode(.inline)
        }
    }
}

// MARK: - 同步

/// 同步: how it is going and each device's last book on top, the three ways in the
/// middle, what goes along below.
struct PhoneSync: View {
    let library: Library
    @State private var picking = false
    @State private var signingIn = false
    @State private var problem: String?
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession
    private let tint = YYTint.standard

    private var sync: Sync { library.sync }

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "通过个人云盘在多台设备间同步"))
            statusCard.padding(.top, YYUI.s(12)).padding(.bottom, YYUI.s(10))
            ConflictsBanner(library: library, spacing: YYUI.s(10))
            groupLabel(String(localized: "同步方式")).padding(.top, YYUI.s(8))
            YYGroup {
                method(.icloud, "icloud", "iCloud", sync.method == .icloud && sync.isOn ? String(localized: "iCloud 云盘中你选择的文件夹") : String(localized: "使用 iCloud 云盘中的文件夹"))
                NavigationLink { PhoneWebDAV(sync: sync) } label: {
                    methodLabel(.webdav, "server.rack", "WebDAV",
                                sync.method == .webdav ? (sync.webdav.map { $0.url.host() ?? $0.url.absoluteString } ?? "") : String(localized: "坚果云、群晖、Nextcloud…"))
                }
                .buttonStyle(.plain)
                method(.googleDrive, "globe", "Google Drive",
                       GoogleAuth.clientID == nil ? String(localized: "适合跨 Android 同步 · 即将支持") : sync.method == .googleDrive ? (sync.googleEmail ?? String(localized: "已登录")) : String(localized: "用你自己的 Google 账号"))
                    .disabled(GoogleAuth.clientID == nil || signingIn)
                if signingIn {
                    HStack(spacing: YYUI.s(8)) {
                        ProgressView().controlSize(.small)
                        Text("在弹出的页面里登录 Google…").font(YYUI.font(11)).foregroundStyle(YYColor.t2)
                    }
                    .frame(minHeight: YYUI.s(38))
                }
                if let problem {
                    Label(problem, systemImage: "exclamationmark.circle").font(YYUI.font(11)).foregroundStyle(YYColor.danger)
                        .frame(minHeight: YYUI.s(38))
                }
            }
            .animation(YYMotion.standard, value: signingIn)
            .animation(YYMotion.standard, value: problem)
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "同步内容"))
            YYGroup {
                included(String(localized: "进度和书签"), .reading)
                included(String(localized: "笔记和生词"), .notes)
                included(String(localized: "设置和替换规则"), .settings)
                described(String(localized: "书文件"), String(localized: "仅在 Wi‑Fi 下传输，占用你的网盘空间；播客单集体积很小，始终同步")) {
                    YYSwitch(isOn: Binding(get: { sync.syncsFiles }, set: { sync.syncFiles($0) }), label: String(localized: "书文件"))
                }
                described(String(localized: "有声书录音"), String(localized: "仅在 Wi‑Fi 下同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读")) {
                    YYSwitch(isOn: Binding(get: { sync.syncsRecordings }, set: { sync.syncRecordings($0) }), label: String(localized: "有声书录音"))
                }
            }
            .padding(.bottom, YYUI.s(14))
            if sync.isOn {
                YYGroup {
                    Button { sync.stop() } label: {
                        Text("停止同步").font(YYUI.font(11.5)).foregroundStyle(YYColor.danger)
                            .frame(maxWidth: .infinity, minHeight: YYUI.s(38))
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.bottom, YYUI.s(14))
                .transition(.opacity)
            }
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(5)) {
                Image(systemName: "lock").font(YYUI.font(9.5))
                Text("无需注册账号，数据仅在你的设备与个人网盘之间传输。")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(YYUI.font(10))
            .foregroundStyle(YYColor.t3)
            .padding(.horizontal, YYUI.s(4))
        }
        // 停止同步 and the devices come and go with the sync, not in a blink.
        .animation(YYMotion.standard, value: sync.isOn)
        .animation(YYMotion.layout, value: sync.devices.map(\.id))
        .navigationTitle("同步")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { sync.loadDevices() }
        .fileImporter(isPresented: $picking, allowedContentTypes: [.folder]) { result in
            guard case .success(let url) = result else { return }
            do { try sync.choose(folder: url) } catch { library.alert = Library.message(for: error) }
        }
    }

    /// How it went and when, 立即同步, then every device and how long since it synced.
    /// Red when it failed; for WebDAV a tap goes to the connection check.
    private var statusCard: some View {
        VStack(spacing: 0) {
            HStack(spacing: YYUI.s(10)) {
                statusIcon
                VStack(alignment: .leading, spacing: YYUI.s(2)) {
                    Text(headline).font(YYUI.font(14, .bold)).foregroundStyle(failed ? YYColor.danger : YYColor.t1)
                    Text(subline).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(2)
                }
                Spacer(minLength: 0)
                if sync.isOn {
                    if failed, sync.method == .webdav {
                        NavigationLink { PhoneWebDAV(sync: sync, checkNow: true) } label: { Text("查一查") }
                            .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                    } else {
                        Button("立即同步") { sync.now() }
                            .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                            .disabled(sync.state == .syncing)
                    }
                }
            }
            if !sync.devices.isEmpty {
                VStack(spacing: 0) {
                    ForEach(sync.devices, id: \.id) { d in
                        Rectangle().fill(YYColor.line).frame(height: 0.5)
                        HStack(spacing: YYUI.s(9)) {
                            Image(systemName: Self.symbol(d.platform)).font(YYUI.font(12)).foregroundStyle(YYColor.t2).frame(width: YYUI.s(14))
                            let name = d.name.isEmpty ? String(localized: "设备") : d.name
                            let label = d.thisDevice ? (d.name.isEmpty || d.name == Library.platformWord ? String(localized: "这台 \(Library.platformWord)") : String(localized: "\(d.name) · 本机")) : name
                            Text(label)
                                .font(YYUI.font(11)).foregroundStyle(YYColor.t1).lineLimit(1)
                            Spacer(minLength: YYUI.s(8))
                            Text(detail(of: d)).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
                        }
                        .frame(height: YYUI.s(34))
                    }
                }
                .padding(.top, YYUI.s(12))
            }
        }
        .padding(.top, YYUI.s(14))
        .padding(.bottom, YYUI.s(sync.devices.isEmpty ? 14 : 2))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
    }

    private var failed: Bool {
        if case .failed = sync.state { return true }
        return false
    }

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A switch with a grey line under its name, as the Mac's rows have.
    private func described<C: View>(_ title: String, _ detail: String, @ViewBuilder control: () -> C) -> some View {
        HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: YYUI.s(8))
            control()
        }
        .padding(.vertical, YYUI.s(8))
    }

    private func included(_ title: String, _ part: SyncPart) -> some View {
        YYRow(title: title) {
            YYSwitch(isOn: Binding(get: { sync.includes(part) }, set: { sync.include(part, $0) }), label: title)
        }
    }

    private func method(_ m: Sync.Method, _ symbol: String, _ title: String, _ detail: String) -> some View {
        Button { pick(m) } label: { methodLabel(m, symbol, title, detail) }
            .buttonStyle(.plain)
    }

    /// A small icon tile, the name and a grey line, a tick on the one in use.
    private func methodLabel(_ m: Sync.Method, _ symbol: String, _ title: String, _ detail: String) -> some View {
        let on = sync.method == m && sync.isOn
        return HStack(spacing: YYUI.s(10)) {
            Image(systemName: symbol)
                .font(YYUI.font(12, .medium))
                .foregroundStyle(on ? tint.accent : YYColor.t1)
                .frame(width: YYUI.s(26), height: YYUI.s(26))
                .background(on ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: YYUI.s(7)))
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: 0)
            if on { Image(systemName: "checkmark").font(YYUI.font(11, .bold)).foregroundStyle(tint.accent).transition(.opacity) }
        }
        .animation(YYMotion.quick, value: on)
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }

    private func pick(_ m: Sync.Method) {
        switch m {
        case .icloud:
            picking = true
        case .googleDrive:
            guard !signingIn else { return }
            signingIn = true
            problem = nil
            Task {
                do {
                    try await sync.connectGoogle { url, scheme in
                        try await webAuthenticationSession.authenticate(using: url, callback: .customScheme(scheme), additionalHeaderFields: [:])
                    }
                } catch {
                    problem = Sync.message(for: error)
                }
                signingIn = false
            }
        default:
            break
        }
    }

    /// A tick on green, a turning arrow, or ! on red.
    @ViewBuilder private var statusIcon: some View {
        let (symbol, color): (String, Color) = switch sync.state {
        case .syncing: ("arrow.triangle.2.circlepath", tint.accent)
        case .failed: ("exclamationmark", YYColor.danger)
        case .idle: sync.isOn ? ("checkmark", YYColor.ok) : ("icloud.slash", YYColor.t3)
        }
        Image(systemName: symbol)
            .font(YYUI.font(12, .bold))
            .foregroundStyle(color)
            .symbolEffect(.rotate, isActive: sync.state == .syncing)
            .contentTransition(.symbolEffect(.replace))
            .frame(width: YYUI.s(32), height: YYUI.s(32))
            .background(color.opacity(0.14), in: .circle)
            .animation(YYMotion.standard, value: sync.state)
            .animation(YYMotion.standard, value: sync.isOn)
    }

    private var headline: String {
        switch sync.state {
        case .syncing: return String(localized: "正在同步…")
        case .failed: return String(localized: "同步未成功")
        case .idle: return sync.isOn ? (sync.lastSynced == nil ? String(localized: "已连上") : String(localized: "已同步")) : String(localized: "尚未开启同步")
        }
    }

    /// "2 分钟前 · iCloud", or why it failed.
    private var subline: String {
        if case .failed(let why) = sync.state { return why }
        guard sync.isOn else { return String(localized: "选择下方任一方式，跨设备同步进度、笔记与设置") }
        let place = switch sync.method {
        case .webdav: "WebDAV"
        case .googleDrive: "Google Drive"
        default: "iCloud"
        }
        return [sync.lastSynced.map(Self.ago), place].compactMap { $0 }.joined(separator: " · ")
    }

    private func detail(of d: DeviceInfo) -> String {
        if d.thisDevice { return String(localized: "刚刚") }
        guard let at = d.lastAt else { return String(localized: "还没同步过") }
        return Self.ago(Date(timeIntervalSince1970: Double(at) / 1000))
    }

    static func ago(_ date: Date) -> String {
        let s = Date().timeIntervalSince(date)
        if s < 60 { return String(localized: "刚刚") }
        if s < 3600 { return String(localized: "\(Int(s / 60)) 分钟前") }
        if Calendar.current.isDateInToday(date) { return String(localized: "今天 ") + date.formatted(.dateTime.hour().minute()) }
        if Calendar.current.isDateInYesterday(date) { return String(localized: "昨天 ") + date.formatted(.dateTime.hour().minute()) }
        return date.formatted(.dateTime.month().day())
    }

    private static func symbol(_ platform: String) -> String {
        switch platform {
        case "ios": "iphone"
        case "ipados": "ipad"
        case "android": "smartphone"
        default: "laptopcomputer"
        }
    }
}

/// WebDAV: 地址, 账号, 密码, 文件夹; 连接 runs the check step by step and
/// keeps the account once every step passes.
private struct PhoneWebDAV: View {
    let sync: Sync
    /// Opened from a failed sync: check straight away.
    var checkNow = false
    @Environment(\.dismiss) private var dismiss
    @State private var url = ""
    @State private var user = ""
    @State private var password = ""
    @State private var folder = String(localized: "语阅")
    @State private var showPassword = false
    @State private var check: WebDAVCheck?
    @State private var checking = false

    private var account: Sync.WebDAVAccount? {
        guard let u = URL(string: url.trimmingCharacters(in: .whitespaces)), u.scheme?.hasPrefix("http") == true, u.host() != nil else { return nil }
        return Sync.WebDAVAccount(url: u, user: user.trimmingCharacters(in: .whitespaces), password: password,
                                  folder: folder.trimmingCharacters(in: CharacterSet(charactersIn: "/ ")))
    }

    var body: some View {
        YYPage {
            YYGroup {
                YYField(label: String(localized: "地址"), labelWidth: 44) {
                    TextField("", text: $url, prompt: Text("https://dav.jianguoyun.com/dav/"))
                        .font(YYUI.font(11, design: .monospaced))
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                YYField(label: String(localized: "账号"), labelWidth: 44) {
                    TextField("", text: $user, prompt: Text("邮箱或用户名"))
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                YYField(label: String(localized: "密码"), labelWidth: 44) {
                    HStack(spacing: YYUI.s(8)) {
                        Group {
                            if showPassword {
                                TextField("", text: $password).textInputAutocapitalization(.never).autocorrectionDisabled()
                            } else {
                                SecureField("", text: $password, prompt: Text("坚果云填应用密码"))
                            }
                        }
                        .font(YYUI.font(11.5, design: .monospaced))
                        Button { showPassword.toggle() } label: {
                            Image(systemName: showPassword ? "eye.slash" : "eye").font(YYUI.font(12)).foregroundStyle(YYColor.t3)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(showPassword ? String(localized: "藏起密码") : String(localized: "显示密码"))
                    }
                }
                YYField(label: String(localized: "文件夹"), labelWidth: 44) {
                    HStack(spacing: 0) {
                        Text("/").foregroundStyle(YYColor.t3)
                        TextField("", text: $folder).textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
            }
            .foregroundStyle(YYColor.t1)
            .padding(.top, YYUI.s(10))
            Group {
                if let check {
                    YYLabel(String(localized: "连接检查"), check.summary).padding(.top, -YYUI.s(4))
                    WebDAVCheckCard(check: check)
                } else {
                    YYNote(String(localized: "密码存在这台设备的钥匙串里。点连接会一项一项查：服务器、证书、账号密码、文件夹读写。"))
                }
            }
            .transition(.opacity)
            .animation(YYMotion.standard, value: check == nil)
            Button(buttonTitle) {
                if check?.passed == true { dismiss() } else { run() }
            }
            .buttonStyle(YYButtonStyle(kind: .main, size: .large, wide: true))
            .disabled(account == nil || checking)
            .padding(.top, YYUI.s(18))
        }
        .navigationTitle("WebDAV")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            if let a = sync.webdav {
                url = a.url.absoluteString
                user = a.user
                password = a.password
                folder = a.folder
                if checkNow, check == nil { run() }
            }
        }
        .onChange(of: [url, user, password, folder]) { if !checking, check?.passed == true { check = nil } }
    }

    private var buttonTitle: String {
        if checking { return String(localized: "正在检查…") }
        guard let check else { return sync.method == .webdav ? String(localized: "重新连接") : String(localized: "连接") }
        return check.passed ? String(localized: "好了") : String(localized: "重试")
    }

    private func run() {
        guard let account else { return }
        checking = true
        Task {
            let result = await WebDAVCheck.run(account) { check = $0 }
            check = result
            checking = false
            if result.passed { sync.keep(account) }
        }
    }
}

// MARK: - 存储空间

/// 存储空间: what 语阅 takes, the books deleted (kept 30 days), and each book from the largest.
struct PhoneStorage: View {
    let library: Library
    @State private var usage: StorageUsage?
    @State private var companionCache: Int64 = 0
    @AppStorage(Podcasts.keepHeardKey) private var keepHeard = false
    @State private var cleaning = false
    private let tint = YYTint.standard

    @State private var info: BookSummary?

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "书库、有声书与本地缓存占用的空间"))
            useCard.padding(.top, YYUI.s(12)).padding(.bottom, YYUI.s(14))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("最近删除 · \(usage?.deleted.count ?? 0) 本（\(Self.size(usage?.deletedBytes ?? 0))）")
                            .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                        Text("保留 30 天以便恢复，到期自动清理").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                    Button("立即清空") { purge() }
                        .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                        .disabled((usage?.deleted.isEmpty ?? true) || cleaning)
                }
                .padding(.vertical, YYUI.s(9))
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("书伴问答缓存 · \(Self.size(companionCache))")
                            .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                        Text("同章节重复提问直接读取本地缓存，不重复调用模型").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    Button("清理") {
                        try? FileManager.default.removeItem(at: companionDir)
                        measureCompanion()
                    }
                    .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                    .disabled(companionCache == 0)
                }
                .padding(.vertical, YYUI.s(9))
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("永久保留已听播客").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text(keepHeard ? String(localized: "已听单集始终保留") : String(localized: "关闭时，听完满 30 天自动清理（含划线或书签的单集始终保留）"))
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    YYSwitch(isOn: $keepHeard, label: String(localized: "永久保留已听播客"))
                }
                .padding(.vertical, YYUI.s(9))
            }
            if let usage, !usage.books.isEmpty {
                YYLabel(String(localized: "各书占用空间"), "")
                YYGroup {
                    ForEach(usage.books.prefix(30), id: \.id) { b in bookRow(b) }
                }
            }
        }
        .navigationTitle("存储")
        .navigationBarTitleDisplayMode(.inline)
        .task { await measure() }
        .onAppear(perform: measureCompanion)
        .sheet(item: $info) { PhoneBookInfo(library: library, book: $0) }
    }

    private var companionDir: URL { library.dataDir.appending(path: "companion", directoryHint: .isDirectory) }

    private func measureCompanion() {
        let files = (try? FileManager.default.contentsOfDirectory(at: companionDir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        companionCache = files.reduce(0) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
    }

    /// The total, what is left on the device, one bar split by kind and its legend.
    private var useCard: some View {
        let parts = parts
        let total = max(1, parts.reduce(0) { $0 + $1.1 })
        return VStack(alignment: .leading, spacing: 0) {
            Text("已用空间").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(3)) {
                let (number, unit) = Self.split(usage?.total ?? 0)
                Text(usage == nil ? "…" : number).font(YYUI.font(26, .bold).monospacedDigit()).foregroundStyle(YYColor.t1)
                    .contentTransition(.numericText())
                Text(usage == nil ? "" : unit).font(YYUI.font(12, .semibold)).foregroundStyle(YYColor.t1)
                Spacer(minLength: YYUI.s(8))
                if let free = usage?.free {
                    Text("本机可用 \(Self.size(free))").font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
            }
            .padding(.top, 1)
            GeometryReader { g in
                HStack(spacing: 2) {
                    ForEach(Array(parts.enumerated()).filter { $0.element.1 > 0 }, id: \.offset) { i, p in
                        Rectangle().fill(shade(i)).frame(width: max(3, (g.size.width - 6) * Double(p.1) / Double(total)))
                    }
                    Spacer(minLength: 0)
                }
            }
            .frame(height: YYUI.s(10))
            .clipShape(.rect(cornerRadius: YYUI.s(5)))
            .padding(.top, YYUI.s(10))
            LazyVGrid(columns: [GridItem(.flexible(), spacing: YYUI.s(16)), GridItem(.flexible())], alignment: .leading, spacing: YYUI.s(7)) {
                ForEach(Array(parts.enumerated()), id: \.offset) { i, p in
                    HStack(spacing: YYUI.s(6)) {
                        RoundedRectangle(cornerRadius: 2).fill(shade(i)).frame(width: YYUI.s(8), height: YYUI.s(8))
                        Text(p.0).foregroundStyle(YYColor.t2)
                        Spacer(minLength: 0)
                        Text(Self.size(p.1)).fontWeight(.medium).monospacedDigit().foregroundStyle(YYColor.t1)
                    }
                    .font(YYUI.font(10.5))
                }
            }
            .padding(.top, YYUI.s(12))
        }
        .padding(.vertical, YYUI.s(13))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
    }

    /// 书文件, the books deleted, and everything else (the library, notes, covers).
    private var parts: [(String, Int64)] {
        guard let u = usage else { return [(String(localized: "书文件"), 0), (String(localized: "已删除"), 0), (String(localized: "其他"), 0)] }
        return [(String(localized: "书文件"), u.bookBytes), (String(localized: "已删除"), u.deletedBytes),
                (String(localized: "其他"), max(0, u.total - u.bookBytes - u.deletedBytes))]
    }

    /// The four greys, from the ink down.
    private func shade(_ i: Int) -> Color {
        [YYColor.t1, YYColor.t1.opacity(0.62), YYColor.t1.opacity(0.36), YYColor.t1.opacity(0.21)][min(i, 3)]
    }

    /// Cover, title and kind, the size in ink.
    private func bookRow(_ b: StorageUsage.Book) -> some View {
        let book = library.books.first { $0.id == b.id }
        return Button { info = book } label: {
            HStack(spacing: YYUI.s(10)) {
                if let book { BookCover(book: book).frame(width: YYUI.s(24)) }
                VStack(alignment: .leading, spacing: YYUI.s(1)) {
                    Text(b.title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Text(book.map(Self.kind) ?? "").font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: YYUI.s(8))
                Text(Self.size(b.bytes)).font(YYUI.font(11, .semibold).monospacedDigit()).foregroundStyle(YYColor.t1)
            }
            .padding(.vertical, YYUI.s(7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private static func kind(_ b: BookSummary) -> String {
        switch b.format {
        case .pdf: "PDF"
        case .txt: "TXT"
        case .mobi: "MOBI"
        case .epub: "EPUB"
        case .audio: String(localized: "有声书")
        case .podcast: String(localized: "播客")
        }
    }

    /// "6.8" and "GB".
    private static func split(_ bytes: Int64) -> (String, String) {
        let s = size(bytes)
        guard let space = s.lastIndex(of: " ") else { return (s, "") }
        return (String(s[..<space]), String(s[s.index(after: space)...]))
    }

    private func measure() async {
        let active = Dictionary(library.books.map { ($0.id, $0.title) }, uniquingKeysWith: { a, _ in a })
        let measured = await StorageUsage.measure(dir: library.dataDir, active: active)
        // The total rolls to its number and the bar grows into its parts.
        withAnimation(YYMotion.standard) { usage = measured }
    }

    private func purge() {
        cleaning = true
        _ = try? library.core.purgeDeleted(olderThanMs: 0)
        Task {
            await measure()
            cleaning = false
        }
    }

    static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}

// MARK: - 关于

/// 关于: free, no server, nothing collected; the licences, privacy, and feedback
/// by mail, as there is no server to send it to.
struct PhoneAbout: View {
    private var version: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
        let b = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        return b.map { "\(v) (\($0))" } ?? v
    }

    var body: some View {
        YYPage {
            VStack(spacing: 0) {
                // 阅 on paper, the blue rule under it.
                Text("阅")
                    .font(YYUI.font(30, .bold, design: .serif))
                    .foregroundStyle(Color(yy: YYRGBA(28, 29, 32)))
                    .padding(.bottom, YYUI.s(6))
                    .frame(width: YYUI.s(64), height: YYUI.s(64))
                    .overlay(alignment: .bottom) {
                        Capsule().fill(Color(yy: YYRGBA(43, 76, 126))).frame(height: YYUI.s(2.5))
                            .padding(.horizontal, YYUI.s(20)).padding(.bottom, YYUI.s(13))
                    }
                    .background(Color(yy: YYRGBA(251, 250, 247)), in: .rect(cornerRadius: YYUI.s(15)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(15)).strokeBorder(.black.opacity(0.1), lineWidth: 0.5))
                    .shadow(color: Color(yy: YYRGBA(30, 28, 24, 0.14)), radius: YYUI.s(10), y: YYUI.s(8))
                Text("语阅").font(YYUI.font(17, .bold)).foregroundStyle(YYColor.t1).padding(.top, YYUI.s(10))
                Text("版本 \(version)").font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).padding(.top, 2)
                Text("免费，没有服务器，不收集任何数据。")
                    .font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    .padding(.top, YYUI.s(22)).padding(.bottom, YYUI.s(26))
            }
            .frame(maxWidth: .infinity)
            .padding(.top, YYUI.s(24))
            YYGroup {
                if UpdateCenter.enabled {
                    NavigationLink { PhoneUpdate() } label: {
                        YYRow(title: String(localized: "检查更新"), icon: "arrow.down.circle",
                              value: UpdateCenter.shared.showsBadge ? UpdateCenter.shared.offer?.display : nil, chevron: true) {
                            if UpdateCenter.shared.showsBadge {
                                Circle().fill(YYColor.danger).frame(width: 8, height: 8)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                }
                NavigationLink { PhoneTextPage(title: String(localized: "开源许可"), text: Self.licences) } label: {
                    YYRow(String(localized: "开源许可"), icon: "chevron.left.forwardslash.chevron.right", chevron: true)
                }
                .buttonStyle(.plain)
                NavigationLink { PhoneTextPage(title: String(localized: "隐私说明"), text: Self.privacy) } label: {
                    YYRow(String(localized: "隐私说明"), icon: "checkmark.shield", chevron: true)
                }
                .buttonStyle(.plain)
            }
            YYGroup {
                Link(destination: URL(string: "https://github.com/nowtin")!) {
                    YYRow(String(localized: "作者"), icon: "person", value: "nowtin", chevron: true)
                }
                .buttonStyle(.plain)
                Link(destination: URL(string: "https://github.com/inokoru/yuyue")!) {
                    YYRow(String(localized: "源代码"), icon: "link", value: String(localized: "GitHub"), chevron: true)
                }
                .buttonStyle(.plain)
                Link(destination: URL(string: "https://github.com/inokoru/yuyue/issues")!) {
                    YYRow(String(localized: "问题与建议"), icon: "exclamationmark.bubble", value: String(localized: "前往 GitHub 反馈"), chevron: true)
                }
                .buttonStyle(.plain)
            }
            .padding(.top, YYUI.s(12))
            PencilSignature()
                .frame(maxWidth: .infinity)
                .padding(.top, YYUI.s(96))
                .padding(.bottom, YYUI.s(40))
        }
        .navigationTitle("关于")
        .navigationBarTitleDisplayMode(.inline)
    }

    static var licences: String {
        String(localized: "语阅用到了这些开源项目，感谢它们的作者：") + "\n\n"
            + "UniFFI (MPL-2.0) · rusqlite / SQLite (MIT / Public Domain) · serde, serde_json, regex, encoding_rs, chardetng, zip, html5ever, roxmltree, ICU4X, blake3, percent-encoding, thiserror, aho-corasick, base64, crypto_box, flate2, getrandom, lewton, markup5ever_rcdom, ripemd (MIT / Apache-2.0) · rars, rust-lzo1x (MIT) · foliate-js (MIT) · zhconv (GPL-2.0-or-later) · OpenCC dictionaries (Apache-2.0) · ECDICT (MIT) · pinyin, pypinyin (MIT) · ONNX Runtime (MIT) · OkHttp, JNA (Apache-2.0) · AndroidX, Jetpack Compose, Media3, kotlinx.coroutines, Haze (Apache-2.0) · Sparkle, MLX, mlx-audio-swift (MIT) · Simple Icons (CC0 1.0) · ZipVoice weights (CC-BY-NC 4.0) · Vocos (MIT) · Qwen3-TTS (Apache-2.0) · voices-1, sounds-1 (CC0 1.0) · Meta MMS (CC-BY-NC 4.0) · 3D-Speaker CAM++ (Apache-2.0) · SenseVoice (FunASR Model License)"
            + "\n\n" + String(localized: "完整的许可文本随源代码一起发布。")
    }

    static var privacy: String {
        [String(localized: "语阅没有服务器，也不收集任何数据：没有账号、没有统计、没有广告。"),
         String(localized: "书、进度、笔记和生词都存在这台设备上。打开同步时，它们只在你的设备和你自己选的网盘（iCloud 云盘、WebDAV 或 Google Drive）之间传，语阅看不到。"),
         String(localized: "书伴默认用设备自带的模型，不联网。你填了自己的 Key 时，提问和书里相关的几段原文会直接发给你选的服务商，费用和隐私条款按你和它的约定；Key 只存在这台设备的钥匙串里。"),
         String(localized: "云端声音和笔记导出（Notion、flomo、新枝）也只在你填了 Key 或链接时联网：要读的句子、要发的笔记直接发给你选的服务。读过的声音存在这台设备上。"),
         String(localized: "翻译用系统的离线翻译，原文不出这台设备。"),
         String(localized: "检查更新：从 GitHub 下载安装的版本每天从 GitHub 取一个小文件，看有没有新版本，不带任何关于你和设备的信息；在设置 › 关于 › 检查更新里能关掉。App Store 和 Google Play 的版本不查，由商店更新。")].joined(separator: "\n\n")
    }
}

private struct PhoneTextPage: View {
    let title: String
    let text: String

    var body: some View {
        YYPage {
            Text(text)
                .font(YYUI.font(11.5))
                .lineSpacing(YYUI.s(11.5) * 0.7)
                .foregroundStyle(YYColor.t1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, YYUI.s(14))
                .padding(.horizontal, YYUI.s(14))
                .yyCard()
                .padding(.top, YYUI.s(10))
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
    }
}
// MARK: - 最近改过

/// 最近改过: the settings changed on this device, grouped by day with consecutive edits to the
/// same setting folded, rich value chips (paper dot, font face, switch, 1-tap voice preview),
/// locked-width 改回 / 还原 actions, category filters, and tap-to-jump to each setting's page.
struct PhoneSettingsHistory: View {
    let library: Library
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @State private var filterPage: String?
    @State private var expanded: Set<UUID> = []
    @State private var feedback: FeedbackNote?
    @State private var destinationPage: SettingsPage?
    @State private var confirming: YYConfirm?
    private var log: SettingsLog { .shared }
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private struct FeedbackNote: Equatable {
        let id = UUID()
        let text: String
    }

    var body: some View {
        let pres = log.presentation(filterPage: filterPage)
        YYPage {
            if log.entries.isEmpty {
                emptyState
            } else {
                topControls(pres: pres)
                ForEach(pres.days) { dayGroup in
                    PhoneSettingsHeading(SettingsLog.dayName(dayGroup.day))
                    YYGroup {
                        ForEach(dayGroup.runs) { run in
                            let open = expanded.contains(run.id)
                            row(
                                run.head,
                                superseded: pres.superseded.contains(run.head.id),
                                olderCount: run.older.count,
                                isExpanded: open,
                                toggleExpand: {
                                    withAnimation(.snappy(duration: 0.2)) {
                                        if open { expanded.remove(run.id) } else { expanded.insert(run.id) }
                                    }
                                }
                            )
                            if !run.older.isEmpty, open {
                                ForEach(run.older) { sub in
                                    row(
                                        sub,
                                        superseded: pres.superseded.contains(sub.id),
                                        olderCount: 0,
                                        isExpanded: false,
                                        isSubStep: true,
                                        toggleExpand: {}
                                    )
                                }
                            }
                        }
                    }
                }
                YYGroup {
                    Button(role: .destructive) {
                        confirming = YYConfirm(
                            title: String(localized: "清空最近改过的记录？"),
                            message: String(localized: "设置本身不变，只是不能再从这里一条条撤销。"),
                            button: String(localized: "清空")
                        ) {
                            Voices.stopPreview()
                            withAnimation(.snappy(duration: 0.2)) { log.clear() }
                        }
                    } label: {
                        Text("清空记录")
                            .font(YYUI.font(11.5))
                            .foregroundStyle(YYColor.danger)
                            .frame(maxWidth: .infinity, minHeight: YYUI.s(38))
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.top, YYUI.s(20))
            }
        }
        .navigationTitle("最近改过")
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $destinationPage) { page in
            destination(for: page)
        }
        .yyConfirm($confirming)
        .task(id: feedback?.id) {
            guard feedback != nil else { return }
            try? await Task.sleep(for: .seconds(1.8))
            withAnimation(.easeOut(duration: 0.2)) { feedback = nil }
        }
        .onDisappear {
            Voices.stopPreview()
        }
    }

    private var emptyState: some View {
        VStack(spacing: YYUI.s(8)) {
            Image(systemName: "clock.arrow.circlepath")
                .font(.system(size: YYUI.s(15), weight: .regular))
                .foregroundStyle(YYColor.t3)
                .frame(width: YYUI.s(36), height: YYUI.s(36))
                .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(10)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(scheme == .dark ? .white.opacity(0.07) : YYColor.line, lineWidth: 0.5))
                .padding(.bottom, YYUI.s(2))
            Text("还没有修改记录")
                .font(YYUI.font(12, .semibold))
                .foregroundStyle(YYColor.t1)
            Text("调整过的设置会记在这里，\n每一项都能随时改回去。")
                .font(YYUI.font(10.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, YYUI.s(44))
    }

    private func topControls(pres: SettingsLog.Presentation) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack(spacing: YYUI.s(8)) {
                let latest = pres.latestUndoable
                Button {
                    if let latest { performUndo(latest) }
                } label: {
                    HStack(spacing: YYUI.s(5)) {
                        Image(systemName: "arrow.uturn.backward")
                            .font(.system(size: YYUI.s(9.5), weight: .semibold))
                        Text(latest.map { String(localized: "改回「\($0.title)」") } ?? String(localized: "无可改回项"))
                            .font(YYUI.font(10.5, .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(latest != nil ? YYColor.t1 : YYColor.t3)
                    .padding(.horizontal, YYUI.s(10))
                    .frame(height: YYUI.s(26))
                    .background(YYColor.card, in: .capsule)
                    .overlay(Capsule().strokeBorder(YYColor.pillLine, lineWidth: 0.5))
                    .contentShape(.capsule)
                }
                .buttonStyle(HistoryPressStyle())
                .disabled(latest == nil)
                .opacity(latest == nil ? 0.5 : 1)

                Spacer(minLength: YYUI.s(4))

                if let feedback {
                    HStack(spacing: YYUI.s(4)) {
                        Image(systemName: "checkmark")
                            .font(.system(size: YYUI.s(8.5), weight: .bold))
                        Text(feedback.text)
                            .font(YYUI.font(10, .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, YYUI.s(8))
                    .padding(.vertical, YYUI.s(3))
                    .background(tint.accentSoft, in: .capsule)
                    .overlay(Capsule().strokeBorder(tint.edge, lineWidth: 0.5))
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
                } else {
                    Text(String(localized: "\(log.entries.count) 项记录"))
                        .font(YYUI.font(10).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
            }

            if pres.pageCounts.count > 1 {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: YYUI.s(5)) {
                        filterPill(title: String(localized: "全部"), count: log.entries.count, selected: pres.activeFilter == nil) {
                            withAnimation(.snappy(duration: 0.18)) { filterPage = nil }
                        }
                        ForEach(pres.pageCounts, id: \.page.id) { item in
                            filterPill(title: item.page.title, count: item.count, selected: pres.activeFilter == item.page.rawValue) {
                                withAnimation(.snappy(duration: 0.18)) { filterPage = item.page.rawValue }
                            }
                        }
                    }
                    .padding(.vertical, 1)
                }
            }
        }
        .padding(.top, YYUI.s(10))
        .padding(.bottom, YYUI.s(2))
    }

    private func filterPill(title: String, count: Int, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: YYUI.s(4)) {
                Text(title)
                    .font(YYUI.font(10.5, selected ? .semibold : .regular))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                Text("\(count)")
                    .font(YYUI.font(9.5).monospacedDigit())
                    .foregroundStyle(selected ? tint.accent : YYColor.t3)
            }
            .padding(.horizontal, YYUI.s(10))
            .frame(height: YYUI.s(24))
            .background(selected ? YYColor.card : .clear, in: .capsule)
            .overlay(Capsule().strokeBorder(selected ? YYColor.pillLine : YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(HistoryPressStyle())
    }

    private func row(
        _ entry: SettingsLog.Entry,
        superseded: Bool,
        olderCount: Int,
        isExpanded: Bool,
        isSubStep: Bool = false,
        toggleExpand: @escaping () -> Void
    ) -> some View {
        let page = entry.page.flatMap(SettingsPage.init(rawValue:))
        let undone = entry.undone == true
        return HStack(alignment: .center, spacing: YYUI.s(10)) {
            Image(systemName: page?.symbol ?? "slider.horizontal.3")
                .font(YYUI.font(isSubStep ? 10 : 11))
                .foregroundStyle(YYColor.t2)
                .frame(width: YYUI.s(isSubStep ? 22 : 25), height: YYUI.s(isSubStep ? 22 : 25))
                .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(6.5)))
                .opacity(isSubStep ? 0.72 : 1)

            VStack(alignment: .leading, spacing: YYUI.s(3.5)) {
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(5)) {
                    Text(entry.title)
                        .font(YYUI.font(11.5, .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .layoutPriority(1)
                    if let page {
                        HStack(spacing: 2) {
                            Text(page.title).lineLimit(1)
                            Image(systemName: "chevron.right")
                                .font(.system(size: YYUI.s(7.5), weight: .semibold))
                                .opacity(0.6)
                        }
                        .font(YYUI.font(10))
                        .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(4))
                    Text(entry.date.formatted(date: .omitted, time: .shortened))
                        .font(YYUI.font(9.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }

                HStack(spacing: YYUI.s(5)) {
                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.fromRaw,
                        text: entry.from,
                        color: undone ? YYColor.t1 : YYColor.t2,
                        fontSize: YYUI.s(10.5),
                        tint: tint
                    )
                    Image(systemName: "arrow.right")
                        .font(YYUI.font(8, .medium))
                        .foregroundStyle(YYColor.t3)
                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.toRaw,
                        text: entry.to,
                        color: undone ? YYColor.t3 : (superseded ? YYColor.t2 : YYColor.t1),
                        fontSize: YYUI.s(10.5),
                        strikethrough: undone,
                        tint: tint
                    )
                    .fontWeight(undone || superseded ? .regular : .semibold)
                }
                .font(YYUI.font(10.5))
                .lineLimit(1)

                if olderCount > 0 {
                    Button(action: toggleExpand) {
                        HStack(spacing: 3) {
                            Text(isExpanded
                                 ? String(localized: "收起早先 \(olderCount) 次")
                                 : String(localized: "早先 \(olderCount) 次调整"))
                            Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                                .font(.system(size: YYUI.s(7.5), weight: .semibold))
                        }
                        .font(YYUI.font(9.5))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, YYUI.s(7))
                        .frame(height: YYUI.s(19))
                        .background(YYColor.inset, in: .capsule)
                        .contentShape(.capsule)
                    }
                    .buttonStyle(HistoryPressStyle())
                    .padding(.top, 1)
                }
            }
            .opacity(undone ? 0.68 : (superseded ? 0.84 : 1))

            actionSlot(entry: entry, undone: undone)
                .frame(width: YYUI.s(56), alignment: .trailing)
        }
        .padding(.leading, isSubStep ? YYUI.s(4) : 0)
        .padding(.vertical, YYUI.s(8.5))
        .contentShape(.rect)
        .onTapGesture {
            if let page { destinationPage = page }
        }
    }

    @ViewBuilder
    private func actionSlot(entry: SettingsLog.Entry, undone: Bool) -> some View {
        if undone, entry.redo != nil {
            Button { performRedo(entry) } label: {
                HStack(spacing: 3) {
                    Image(systemName: "arrow.uturn.forward")
                        .font(.system(size: YYUI.s(8.5), weight: .semibold))
                    Text("还原")
                        .font(YYUI.font(10, .medium))
                }
                .foregroundStyle(YYColor.t2)
                .frame(width: YYUI.s(54), height: YYUI.s(23))
                .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
        } else if !undone, entry.undo != nil {
            Button { performUndo(entry) } label: {
                HStack(spacing: 3) {
                    Image(systemName: "arrow.uturn.backward")
                        .font(.system(size: YYUI.s(8.5), weight: .semibold))
                    Text("改回")
                        .font(YYUI.font(10, .medium))
                }
                .foregroundStyle(YYColor.t1)
                .frame(width: YYUI.s(54), height: YYUI.s(23))
                .background(YYColor.pill, in: .capsule)
                .overlay(Capsule().strokeBorder(YYColor.pillLine, lineWidth: 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
            .accessibilityLabel(Text("改回 \(entry.from)"))
        }
    }

    private func performUndo(_ entry: SettingsLog.Entry) {
        withAnimation(.snappy(duration: 0.2)) {
            log.undo(entry)
            feedback = FeedbackNote(text: String(localized: "已改回「\(entry.from)」"))
        }
    }

    private func performRedo(_ entry: SettingsLog.Entry) {
        withAnimation(.snappy(duration: 0.2)) {
            log.redo(entry)
            feedback = FeedbackNote(text: String(localized: "已还原「\(entry.to)」"))
        }
    }

    @ViewBuilder
    private func destination(for page: SettingsPage) -> some View {
        switch page {
        case .general: PhoneGeneral(library: library)
        case .appearance: PhoneAppearance()
        case .reading: PhoneReading(library: library)
        case .foreign: PhoneForeign(library: library)
        case .listening: PhoneListening()
        case .companion: PhoneCompanionSettings()
        case .sync: PhoneSync(library: library)
        case .notes: PhoneNoteExport()
        case .storage: PhoneStorage(library: library)
        case .data: PhoneSnapshot(library: library)
        case .update: PhoneAbout()
        }
    }
}
// MARK: - 通用

/// 通用, as the Mac's: what opening 语阅 does, and how books come in — 书库来源 and Wi‑Fi 传书
/// being the phone's ways in, where the Mac takes them dragged onto its window.
struct PhoneGeneral: View {
    let library: Library
    @AppStorage("general.reopenLast") private var reopenLast = false

    @State private var language = AppLanguage.chosen

    var body: some View {
        YYPage {
            PhoneSettingsHeading(String(localized: "语言"))
            YYGroup {
                ForEach(AppLanguage.allCases) { languageRow($0) }
            }
            if language.effective != AppLanguage.running {
                Text(language.effective == .en
                     ? String(localized: "下次打开语阅换成 English。想现在就换：从后台划掉语阅，再打开。")
                     : String(localized: "下次打开语阅换成简体中文。想现在就换：从后台划掉语阅，再打开。"))
                    .font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.horizontal, YYUI.s(4)).padding(.top, YYUI.s(7))
                    .transition(.opacity.combined(with: .move(edge: .top)))
            }
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("启动时继续上次阅读").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("关闭则进入书架").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $reopenLast, label: String(localized: "启动时继续上次阅读"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            .padding(.top, YYUI.s(12))
            PhoneSettingsHeading(String(localized: "导入"))
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(4)) {
                    Text("在「文件」中选择书籍，用语阅打开即可导入").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    Text("文件会复制到书库，移动或删除原文件不受影响").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Text(verbatim: "EPUB  PDF  MOBI  AZW3  TXT").font(YYUI.font(9.5, design: .monospaced)).foregroundStyle(YYColor.t2)
                        .padding(.top, YYUI.s(2))
                }
                .padding(.vertical, YYUI.s(10))
                NavigationLink { PhoneSources(library: library) } label: {
                    YYRow(String(localized: "书库来源"), icon: "server.rack",
                          value: library.sources.all.isEmpty ? nil : String(localized: "\(library.sources.all.count) 个"), chevron: true)
                }
                .buttonStyle(.plain)
                NavigationLink { PhoneWifi(library: library) } label: {
                    YYRow(String(localized: "Wi‑Fi 传书"), icon: "wifi", chevron: true)
                }
                .buttonStyle(.plain)
            }
        }
        .navigationTitle("通用")
        .navigationBarTitleDisplayMode(.inline)
        .animation(YYMotion.standard, value: language)
    }

    /// One of 跟随系统 / 简体中文 / English: its mark drawn like the rows' symbols below, a tick on the
    /// one chosen. The words change when 语阅 opens again.
    private func languageRow(_ l: AppLanguage) -> some View {
        Button {
            guard language != l else { return }
            language = l
            AppLanguage.choose(l)
        } label: {
            HStack(spacing: YYUI.s(10)) {
                LanguageMark(language: l)
                    .stroke(YYColor.t2, style: StrokeStyle(lineWidth: 1.45, lineCap: .round, lineJoin: .round))
                    .frame(width: YYUI.s(17.5), height: YYUI.s(17.5))
                    .frame(width: YYUI.s(15))
                VStack(alignment: .leading, spacing: YYUI.s(1.5)) {
                    Text(languageName(l)).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(1)
                    if l == .system {
                        Text(AppLanguage.systemLanguage == .en ? String(localized: "当前：English") : String(localized: "当前：简体中文"))
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                    }
                }
                Spacer(minLength: YYUI.s(8))
                if language == l {
                    Image(systemName: "checkmark").font(YYUI.font(11, .medium)).foregroundStyle(YYColor.t1)
                        .transition(.scale(scale: 0.8).combined(with: .opacity))
                }
            }
            .padding(.vertical, l == .system ? YYUI.s(7) : 0)
            .frame(minHeight: YYUI.s(38))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .sensoryFeedback(.selection, trigger: language)
        .accessibilityAddTraits(language == l ? .isSelected : [])
    }

    private func languageName(_ l: AppLanguage) -> String {
        switch l {
        case .system: String(localized: "跟随系统")
        case .zh: "简体中文"
        case .en: "English"
        }
    }
}

#endif

