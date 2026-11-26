import SwiftUI
import YuyueCore

// 分角色 · 认人和改人: what the cast page shows about who says what, the 没把握 list, the card
// for picking who says a line, and someone's names. The core keeps all of it (and syncs it);
// these only ask and show.

// MARK: The app's menu card, for both platforms

/// One row of the white menu card: the name on the left, an icon on the right.
struct DramaMenuRow: View {
    let title: String
    let symbol: String
    var color: Color = YYColor.t1
    var bold = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack {
                Text(title).font(YYUI.font(12.5, bold ? .semibold : .regular)).lineLimit(1)
                Spacer(minLength: 12)
                Image(systemName: symbol).font(YYUI.font(13)).frame(width: 15)
            }
            .foregroundStyle(color)
            .padding(.horizontal, 9)
            .frame(height: YYUI.s(28))
            .background(hover ? YYColor.inset : .clear, in: .rect(cornerRadius: 7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 5)
        #if os(macOS)
        .onHover { hover = $0 }
        #endif
    }
}

/// The card itself: a bold line on top, then groups with a hairline between.
struct DramaMenuCard<Content: View>: View {
    let title: String
    var detail: String?
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(title).font(YYUI.font(13, .bold)).foregroundStyle(YYColor.t1).lineLimit(1)
                if let detail { Text(detail).font(YYUI.font(10.5)).foregroundStyle(YYColor.t3).lineLimit(1) }
            }
            .padding(.horizontal, 14).padding(.top, 11).padding(.bottom, 6)
            content
        }
        .padding(.bottom, 4)
        .frame(width: YYUI.s(214))
        .yyFloatCard(.rect(cornerRadius: YYRadius.block), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
    }
}

/// A group of rows with the hairline above it.
struct DramaMenuGroup<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        VStack(spacing: 0) { content }
            .padding(.vertical, 4)
            .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 14) }
    }
}

/// Puts a card at a point, toward the lower right; to the left or up near the edges.
struct AtPoint: ViewModifier {
    let point: CGPoint
    let size: CGSize
    @State private var card = CGSize(width: 214, height: 260)
    @State private var shown = false

    func body(content: Content) -> some View {
        let margin: CGFloat = 8
        let toLeft = point.x + 4 + card.width > size.width - margin
        let above = point.y + 4 + card.height > size.height - margin
        let rawX = toLeft ? point.x - card.width - 4 : point.x + 4
        let rawY = above ? point.y - card.height - 4 : point.y + 4
        let maxX = size.width - card.width - margin
        let maxY = size.height - card.height - margin
        let x = maxX >= margin ? min(max(rawX, margin), maxX) : max(0, (size.width - card.width) / 2)
        let y = maxY >= margin ? min(max(rawY, margin), maxY) : max(0, (size.height - card.height) / 2)
        let origin = UnitPoint(x: toLeft ? 1 : 0, y: above ? 1 : 0)
        return content
            .onGeometryChange(for: CGSize.self) { $0.size } action: { card = $0 }
            .scaleEffect(shown || YYMotion.reduce ? 1 : 0.94, anchor: origin)
            .opacity(shown ? 1 : 0)
            .onAppear { withAnimation(YYMotion.standard) { shown = true } }
            .offset(x: x, y: y)
    }
}

// MARK: 这句是谁说的

/// A line on the page whose speaker is being picked.
struct SpeakerPick: Equatable, Identifiable {
    let section: UInt32
    let line: BubbleLine
    let at: CGPoint
    var people: [CastPick]
    var id: String { "\(section):\(line.first):\(line.lineStart)" }
}

/// 别的人物… for a line on the page: the whole cast met so far, searched.
struct SpeakerOthersSheet: ViewModifier {
    let model: ReaderModel

    func body(content: Content) -> some View {
        content.sheet(item: Binding(get: { model.speakerOthers }, set: { model.speakerOthers = $0 })) { pick in
            PeoplePicker(core: model.core, bookID: model.bookID, section: pick.section, pick: { name in
                model.setSpeaker(.person(name: name), section: pick.section, line: pick.line)
            }, close: { model.speakerOthers = nil })
            .presentationBackground(YYColor.bg)
        }
    }
}

/// The card a bubble's avatar opens: the people in the scene (the one now ticked), anyone else,
/// 众人, 不是台词, and the speaker's own page.
struct SpeakerCard: View {
    let pick: SpeakerPick
    let choose: (UserChoice) -> Void
    let others: () -> Void
    let voice: ((String) -> Void)?
    private let tint = YYTint.standard

    var body: some View {
        DramaMenuCard(title: String(localized: "这句是谁说的")) {
            DramaMenuGroup {
                ForEach(pick.people.prefix(6), id: \.name) { p in
                    let on = p.name == pick.line.who
                    DramaMenuRow(title: p.name, symbol: on ? "checkmark" : "person", color: on ? tint.accent : YYColor.t1, bold: on) {
                        choose(.person(name: p.name))
                    }
                }
            }
            DramaMenuGroup {
                DramaMenuRow(title: String(localized: "别的人物…"), symbol: "magnifyingglass", action: others)
                DramaMenuRow(title: String(localized: "众人"), symbol: "person.2") { choose(.group(name: String(localized: "众人"))) }
                DramaMenuRow(title: String(localized: "不是台词"), symbol: "text.alignleft") { choose(.notSpeech) }
            }
            if let who = pick.line.who, let voice {
                DramaMenuGroup {
                    DramaMenuRow(title: String(localized: "\(who)的声音…"), symbol: "waveform") { voice(who) }
                }
            }
        }
    }
}

// MARK: 别的人物…

/// Everyone met so far, searched; a name nobody has becomes someone new.
struct PeoplePicker: View {
    let core: YuyueCore
    let bookID: String
    let section: UInt32
    var title = String(localized: "是谁说的")
    var except: String?
    let pick: (String) -> Void
    let close: () -> Void
    @State private var everyone: [CastPick] = []
    @State private var query = ""
    @FocusState private var focused: Bool

    private var shown: [CastPick] {
        let q = query.trimmingCharacters(in: .whitespaces)
        let list = everyone.filter { $0.name != except }
        return q.isEmpty ? list : list.filter { $0.name.contains(q) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            YYSheetHeader(title: title, close: close)
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(YYUI.font(12)).foregroundStyle(YYColor.t3)
                TextField(String(localized: "名字"), text: $query)
                    .textFieldStyle(.plain)
                    .font(YYUI.font(13))
                    .focused($focused)
                    .onSubmit { if let first = shown.first { pick(first.name) } else { addNew() } }
            }
            .padding(.horizontal, 10)
            .frame(height: YYUI.s(32))
            .background(YYColor.inset, in: .rect(cornerRadius: 9))
            ScrollView {
                YYGroup {
                    let q = query.trimmingCharacters(in: .whitespaces)
                    if !q.isEmpty, !everyone.contains(where: { $0.name == q }) {
                        Button(action: addNew) {
                            HStack(spacing: 10) {
                                Image(systemName: "plus").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t2)
                                    .frame(width: YYUI.s(26), height: YYUI.s(26)).background(YYColor.inset, in: .circle)
                                Text(String(localized: "新的人物“\(q)”")).font(YYUI.font(12.5))
                                Spacer()
                            }
                            .padding(.vertical, 8).contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                    ForEach(shown, id: \.name) { p in
                        Button { pick(p.name) } label: {
                            HStack(spacing: 10) {
                                DramaAvatar(text: p.avatar, color: Color(hex: p.color), size: 26)
                                Text(p.name).font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                                Spacer()
                            }
                            .padding(.vertical, 8).contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 16)
        #if os(iOS)
        .padding(.top, YYUI.sheetTop)
        #else
        .padding(.top, 16)
        .frame(width: 340, height: 460)
        #endif
        .background(YYColor.bg)
        .task {
            everyone = await Self.loadEveryone(core, bookID: bookID, section: section)
            focused = true
        }
    }

    @concurrent nonisolated private static func loadEveryone(_ core: YuyueCore, bookID: String, section: UInt32) async -> [CastPick] {
        (try? core.dramaEveryone(bookId: bookID, uptoSection: section)) ?? []
    }

    private func addNew() {
        let q = query.trimmingCharacters(in: .whitespaces)
        if !q.isEmpty { pick(q) }
    }
}

struct DramaAvatar: View {
    let text: String
    let color: Color
    var size: CGFloat = 26

    var body: some View {
        Text(text).font(YYUI.font(size * 0.44, .semibold)).foregroundStyle(color)
            .frame(width: YYUI.s(size), height: YYUI.s(size))
            .background(color.opacity(0.18), in: .circle)
    }
}

// MARK: 认人 at the top of the cast

/// Compact header capsule (`待确认 24 ›`) placed on the right of the cast panel title bar.
struct DramaUnsurePill: View {
    let count: Int
    var tint: YYTint = .standard
    let action: () -> Void
    @Environment(\.colorScheme) private var scheme

    private var pillLineColor: Color {
        scheme == .dark ? Color(yy: YYRGBA(60, 56, 51)) : Color(yy: YYRGBA(221, 217, 209))
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 4) {
                Text("待确认")
                    .font(YYUI.font(11, .medium))
                    .foregroundStyle(YYColor.t2)
                Text("\(count)")
                    .font(YYUI.font(11, .semibold))
                    .monospacedDigit()
                    .foregroundStyle(tint.accent)
                Image(systemName: "chevron.right")
                    .font(YYUI.font(9, .semibold))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, YYUI.s(9))
            .frame(height: YYUI.s(24))
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(pillLineColor, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }
}

/// How far the book's lines are known: what the rules found, the model reading it, or done.
struct DramaStatusBlock: View {
    let core: YuyueCore
    let bookID: String
    let section: UInt32
    var openUnsure: (() -> Void)? = nil

    @State private var lines = 0
    @State private var known = 0
    @State private var unsureHere = 0
    @State private var unsureSoFar = 0
    @State private var characters = 0
    @State private var sectionChars = 0
    @State private var chinese = true
    @State private var loaded = false
    @State private var scopeChoice: ScopeChoice = .chapter
    @Environment(\.colorScheme) private var scheme
    private var work: DramaWork { DramaWork.shared }

    private enum ScopeChoice: Hashable {
        case chapter, book
    }

    private var trayColor: Color {
        scheme == .dark ? Color(yy: YYRGBA(29, 27, 24)) : Color(yy: YYRGBA(249, 247, 243))
    }

    private var pillLineColor: Color {
        scheme == .dark ? Color(yy: YYRGBA(60, 56, 51)) : Color(yy: YYRGBA(221, 217, 209))
    }

    var body: some View {
        let book = work.book(bookID)
        let progress = book.progress
        Group {
            if !loaded || (lines == 0 && book.state != .running) {
                // Not EmptyView: a view that isn't there never runs its task.
                Color.clear.frame(height: 0)
            } else if progress?.castReady == true, book.state != .running {
                Text(unsureSoFar == 0
                     ? String(localized: "读到这里的台词都认好了")
                     : String(localized: "角色已识别 · 未写明说话人的句子由旁白自然衔接"))
                    .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 4)
            } else {
                mainCard(book: book, progress: progress)
            }
        }
        .task(id: section) { await load() }
        .onReceive(NotificationCenter.default.publisher(for: DramaWork.updated)) { n in
            if (n.object as? String) == bookID { Task { await load() } }
        }
        .onReceive(NotificationCenter.default.publisher(for: CastContext.changed)) { _ in Task { await load() } }
    }

    private func cardChrome<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            content()
        }
        .background(YYColor.card, in: .rect(cornerRadius: 15))
        .overlay(
            RoundedRectangle(cornerRadius: 15)
                .strokeBorder(scheme == .dark ? Color.white.opacity(0.08) : YYColor.line, lineWidth: 0.5)
        )
        .shadow(color: scheme == .dark ? .clear : Color(yy: YYRGBA(45, 40, 34, 0.035)), radius: 8, y: 3)
    }

    private func hairlinePillButton(_ title: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Text(title)
                .font(YYUI.font(11.5, .medium))
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, YYUI.s(12))
                .frame(height: YYUI.s(28))
                .background(YYColor.card, in: .capsule)
                .overlay(Capsule().strokeBorder(pillLineColor, lineWidth: 0.5))
                .shadow(color: scheme == .dark ? .clear : Color(yy: YYRGBA(45, 40, 34, 0.05)), radius: 2, y: 1)
                .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }

    private func inkProgress(_ fraction: Double) -> some View {
        GeometryReader { g in
            ZStack(alignment: .leading) {
                Capsule().fill(YYColor.inset)
                Capsule()
                    .fill(YYColor.t1)
                    .frame(width: g.size.width * min(1, max(0, fraction)))
            }
        }
        .frame(height: 2)
    }

    @ViewBuilder private func mainCard(book: DramaWork.Book, progress: DramaProgress?) -> some View {
        switch book.state {
        case .running:
            runningCard(book: book, progress: progress)
        case .stopped(let why):
            stoppedCard(why)
        default:
            countedCard(progress)
        }
    }

    private func countedCard(_ p: DramaProgress?) -> some View {
        let missing = max(0, lines - known)
        let chapterDone = lines > 0 && missing == 0 && p?.castReady != true && DramaWork.config != nil
        return cardChrome {
            if chapterDone {
                HStack(spacing: 12) {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("这一章 \(lines) 句台词已认全")
                            .font(YYUI.font(12.5, .medium)).monospacedDigit().foregroundStyle(YYColor.t1)
                        Text(bookCostLabel)
                            .font(YYUI.font(10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 8)
                    hairlinePillButton(String(localized: "+ 认全书")) {
                        work.start(core: core, bookID: bookID, section: section, scope: .wholeBook)
                    }
                }
                .padding(.horizontal, YYUI.s(14))
                .padding(.vertical, YYUI.s(13))
            } else {
                VStack(alignment: .leading, spacing: YYUI.s(11)) {
                    HStack(spacing: YYUI.s(10)) {
                        Image(systemName: "sparkle")
                            .font(YYUI.font(12, .medium))
                            .foregroundStyle(YYColor.t1)
                            .frame(width: YYUI.s(30), height: YYUI.s(30))
                            .background(YYColor.inset, in: .rect(cornerRadius: 9))
                        VStack(alignment: .leading, spacing: 2) {
                            Text("本章台词 · \(known) / \(lines)")
                                .font(YYUI.font(12.5, .semibold)).monospacedDigit().foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            Text(subtitleText(progress: p, missing: missing))
                                .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                                .lineLimit(2)
                        }
                        Spacer(minLength: 6)
                        if p?.castReady != true, DramaWork.config != nil {
                            hairlinePillButton(scopeChoice == .chapter ? String(localized: "开始认本章") : String(localized: "开始认全书")) {
                                work.start(
                                    core: core,
                                    bookID: bookID,
                                    section: section,
                                    scope: scopeChoice == .chapter ? .sectionOnly(section) : .wholeBook
                                )
                            }
                        }
                    }
                    inkProgress(lines > 0 ? Double(known) / Double(lines) : 0)
                }
                .padding(YYUI.s(14))

                if p?.castReady != true, DramaWork.config != nil {
                    Rectangle().fill(YYColor.line).frame(height: 0.5)
                    HStack(spacing: 8) {
                        scopeToggle
                        Spacer(minLength: 8)
                        Text(scopeChoice == .chapter ? chapterCostLabel : bookCostLabel)
                            .font(YYUI.font(10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                    }
                    .padding(.horizontal, YYUI.s(14))
                    .padding(.vertical, YYUI.s(9))
                    .background(trayColor)
                }
            }
        }
    }

    private func subtitleText(progress p: DramaProgress?, missing: Int) -> String {
        if p?.castReady == true {
            return unsureSoFar > 0
                ? String(localized: "未写明说话人的句子由旁白自然衔接")
                : String(localized: "读到这里的台词都认好了")
        }
        if DramaWork.config == nil {
            return String(localized: "在「设置 › 书伴」中配置大模型后可自动补全")
        }
        return missing > 0
            ? String(localized: "其余 \(missing) 句将由旁白衔接，可按需用大模型补全")
            : String(localized: "本章台词已全部识别")
    }

    private var scopeToggle: some View {
        HStack(spacing: 2) {
            scopeItem(.chapter, title: String(localized: "仅本章"))
            scopeItem(.book, title: String(localized: "全书"))
        }
        .padding(2.5)
        .background(YYColor.inset, in: .capsule)
    }

    private func scopeItem(_ choice: ScopeChoice, title: String) -> some View {
        let active = scopeChoice == choice
        return Button {
            withAnimation(YYMotion.quick) { scopeChoice = choice }
        } label: {
            Text(title)
                .font(YYUI.font(11, active ? .medium : .regular))
                .foregroundStyle(active ? YYColor.t1 : YYColor.t2)
                .padding(.horizontal, YYUI.s(10))
                .padding(.vertical, YYUI.s(3))
                .background {
                    if active {
                        Capsule()
                            .fill(YYColor.card)
                            .shadow(color: scheme == .dark ? .clear : Color.black.opacity(0.07), radius: 1.5, y: 0.5)
                    }
                }
                .contentShape(.capsule)
        }
        .buttonStyle(.plain)
    }

    private func runningCard(book: DramaWork.Book, progress p: DramaProgress?) -> some View {
        let onlySection = work.isSectionOnly(bookID)
        let frac: Double = if onlySection {
            lines > 0 ? max(0.12, Double(known) / Double(lines)) : 0.25
        } else {
            p.map { $0.sections > 0 ? max(0.05, Double($0.sectionsDone) / Double($0.sections)) : 0 } ?? 0.1
        }
        return cardChrome {
            VStack(alignment: .leading, spacing: YYUI.s(11)) {
                HStack(spacing: YYUI.s(10)) {
                    ProgressView()
                        .controlSize(.small)
                        .tint(YYColor.card)
                        .frame(width: YYUI.s(30), height: YYUI.s(30))
                        .background(YYColor.t1, in: .rect(cornerRadius: 9))
                    VStack(alignment: .leading, spacing: 2) {
                        Text(onlySection ? String(localized: "正在认本章") : String(localized: "正在认全书"))
                            .font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                        if onlySection {
                            Text(CompanionUsage.tierRoutingEnabled
                                 ? String(localized: "已认出 \(known) / \(lines) 句 · 轻量模型")
                                 : String(localized: "已认出 \(known) / \(lines) 句"))
                                .font(YYUI.font(10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        } else if let p {
                            Text("第 \(p.sectionsDone) / \(p.sections) 章")
                                .font(YYUI.font(10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        }
                    }
                    Spacer(minLength: 6)
                    hairlinePillButton(String(localized: "暂停")) { work.pause(bookID) }
                }
                inkProgress(frac)
            }
            .padding(YYUI.s(14))

            Rectangle().fill(YYColor.line).frame(height: 0.5)
            HStack(spacing: 8) {
                Text("合上书也会继续，退出语阅即停")
                    .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3).lineLimit(1)
                Spacer(minLength: 6)
                if book.sentChars > 0 {
                    Text("已发出约 \(CompanionUsage.formatCount(book.sentChars)) 字")
                        .font(YYUI.font(10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                }
            }
            .padding(.horizontal, YYUI.s(14))
            .padding(.vertical, YYUI.s(8))
            .background(trayColor)
        }
    }

    private func stoppedCard(_ why: String) -> some View {
        cardChrome {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("停下了")
                        .font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    Text(why)
                        .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                        .fixedSize(horizontal: false, vertical: true)
                }
                Spacer(minLength: 8)
                hairlinePillButton(String(localized: "接着认")) {
                    work.start(
                        core: core,
                        bookID: bookID,
                        section: section,
                        scope: scopeChoice == .chapter ? .sectionOnly(section) : .wholeBook
                    )
                }
            }
            .padding(YYUI.s(14))
        }
    }

    private var chapterCostLabel: String {
        let t = DramaWork.estimate(characters: max(sectionChars, 600), chinese: chinese)
        return String(localized: "约 \(CompanionUsage.formatCount(t.tokensIn + t.tokensOut)) token")
    }

    /// 73 万字 · 约 180 万: whole book estimate.
    private var bookCostLabel: String {
        let wan = max(1, characters / 10000)
        let t = DramaWork.estimate(characters: characters, chinese: chinese)
        return String(localized: "\(wan) 万字 · 约 \(max(1, (t.tokensIn + t.tokensOut) / 10000)) 万")
    }

    nonisolated private struct Counts: Sendable {
        var lines = 0, known = 0, unsureHere = 0, unsureSoFar = 0, characters = 0, sectionChars = 0, chinese = true
    }

    private func load() async {
        let c = await Self.loadCounts(core, bookID: bookID, section: section)
        guard !Task.isCancelled else { return }
        // The first count is simply there; later ones (a pick, the model's progress) move the bar
        // and swap the lines in place.
        withAnimation(loaded ? YYMotion.standard : nil) {
            lines = c.lines
            known = c.known
            unsureHere = c.unsureHere
            unsureSoFar = c.unsureSoFar
            characters = c.characters
            sectionChars = c.sectionChars
            chinese = c.chinese
            loaded = true
        }
        await work.refresh(core: core, bookID: bookID)
    }

    @concurrent nonisolated private static func loadCounts(_ core: YuyueCore, bookID: String, section: UInt32) async -> Counts {
        var c = Counts()
        if let play = try? core.dramaSection(bookId: bookID, section: section) {
            var seen = Set<UInt32>()
            for seg in play.segments {
                guard let u = seg.utterance, seen.insert(u).inserted, seg.kind != .narration else { continue }
                c.lines += 1
                if case .unsure = seg.speaker { c.unsureHere += 1 } else { c.known += 1 }
            }
        }
        c.unsureSoFar = (try? core.dramaReview(bookId: bookID, uptoSection: section).count) ?? 0
        if let n = (try? core.dramaProgress(bookId: bookID))?.sections {
            var chars = 0, secChars = 0, han = 0
            for k in 0 ..< n {
                if Task.isCancelled { break }
                for sentence in (try? core.sentences(bookId: bookID, section: UInt32(k))) ?? [] {
                    chars += sentence.count
                    if UInt32(k) == section { secChars += sentence.count }
                    if k < 3 { han += sentence.unicodeScalars.filter { (0x4E00 ... 0x9FFF).contains($0.value) }.count }
                }
            }
            c.characters = chars
            c.sectionChars = secChars
            c.chinese = han > 50
        }
        return c
    }
}

// MARK: 没把握的句子

/// One line at a time: the scene's text with the line marked, the likeliest two, and the rest
/// behind 别人…. A pick settles the scene again at once; lines that settle the most come first.
struct UnsureView: View {
    let core: YuyueCore
    let bookID: String
    let section: UInt32
    var compact = false
    /// The chapter's own title (第八回), for the line over each card.
    var chapter: ((UInt32) -> String?)?

    @State private var items: [UnsureLine] = []
    @State private var loaded = false
    @State private var picks = 0
    @State private var settled = 0
    @State private var undo: (line: UnsureLine, name: String, freed: Int)?
    @State private var picking: UnsureLine?
    @State private var looks: [String: CastPick] = [:]
    @FocusState private var focused: Bool
    private let tint = YYTint.standard

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let undo {
                HStack(spacing: 8) {
                    Text("✓ \(undo.name)").font(YYUI.font(11.5, .semibold)).foregroundStyle(tint.accent)
                    if undo.freed > 0 { Text("也定了 \(undo.freed) 句").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3) }
                    Spacer()
                    Button(String(localized: "撤销")) { takeBack() }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                }
                .padding(.horizontal, 12).padding(.vertical, 8)
                .yyCard()
                .transition(.opacity)
            }
            if let line = items.first {
                card(line)
                if line.frees > 0 {
                    Text("定了这句，后面 \(line.frees) 句也跟着定。")
                        .font(YYUI.font(10)).foregroundStyle(YYColor.t3).padding(.horizontal, 2)
                }
            } else if loaded {
                VStack(spacing: 4) {
                    Image(systemName: "checkmark").font(YYUI.font(20, .semibold)).foregroundStyle(tint.accent)
                    Text("读到这里的台词都认好了").font(YYUI.font(12.5, .semibold))
                    if picks > 0 { Text("点了 \(picks) 下，带走 \(settled) 句").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3) }
                }
                .frame(maxWidth: .infinity)
                .padding(.vertical, 22)
                .yyCard()
            }
        }
        .animation(YYMotion.standard, value: items.first?.text)
        // ✓ 宝玉 · 撤销 comes in after the list has settled, in a later update of its own.
        .animation(YYMotion.standard, value: undo?.name)
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onKeyPress(characters: .decimalDigits) { k in
            guard let line = items.first, let n = Int(k.characters), (1 ... line.names.count).contains(n) else { return .ignored }
            choose(line, .person(name: line.names[n - 1]), name: line.names[n - 1])
            return .handled
        }
        .task(id: section) { await load(); focused = true }
        .sheet(item: Binding(get: { picking.map(Picking.init) }, set: { picking = $0?.line })) { p in
            PeoplePicker(core: core, bookID: bookID, section: section, pick: { name in
                picking = nil
                choose(p.line, .person(name: name), name: name)
            }, close: { picking = nil })
            .presentationBackground(YYColor.bg)
        }
    }

    private struct Picking: Identifiable {
        let line: UnsureLine
        var id: String { "\(line.section):\(line.sentence):\(line.start)" }
    }

    private func card(_ line: UnsureLine) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(whereLine(line)).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                .padding(.horizontal, 12).padding(.top, 10)
            Text(context(line))
                .font(YYFontFamily.serif.font(size: YYUI.s(compact ? 14.5 : 13)))
                .lineSpacing(5)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 12).padding(.top, 6).padding(.bottom, 10)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            VStack(alignment: .leading, spacing: 6) {
                Text("这句是谁说的？").font(YYUI.font(11)).foregroundStyle(YYColor.t3).padding(.bottom, 2)
                ForEach(Array(line.names.prefix(2).enumerated()), id: \.offset) { i, name in
                    Button { choose(line, .person(name: name), name: name) } label: {
                        HStack(spacing: 9) {
                            DramaAvatar(text: looks[name]?.avatar ?? String(name.last ?? "?"), color: Color(hex: looks[name]?.color ?? "#888888"), size: 24)
                            Text(name).font(YYUI.font(compact ? 14 : 12, .semibold)).foregroundStyle(YYColor.t1)
                            Text(i == 0 ? String(localized: "最可能") : (line.present.contains(name) ? String(localized: "在场") : String(localized: "也可能")))
                                .font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            Spacer()
                            #if os(macOS)
                            Text("\(i + 1)").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                                .frame(width: 16, height: 16)
                                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(YYColor.line, lineWidth: 0.5))
                            #endif
                        }
                        .padding(.horizontal, 9).padding(.vertical, compact ? 10 : 7)
                        .background(i == 0 ? tint.accentSoft : YYColor.bg, in: .rect(cornerRadius: 10))
                        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(i == 0 ? tint.edge : YYColor.line, lineWidth: 0.5))
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                HStack(spacing: 6) {
                    Button(String(localized: "别人…")) { picking = line }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                    Button(String(localized: "不是台词")) { choose(line, .notSpeech, name: String(localized: "不是台词")) }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                    Spacer()
                    Button { skip() } label: {
                        Text("跳过 →").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.top, 3)
            }
            .padding(12)
        }
        .yyCard()
    }

    private func whereLine(_ line: UnsureLine) -> String {
        var parts: [String] = []
        if let title = chapter?(line.section) { parts.append(title) }
        if !line.place.isEmpty { parts.append(line.place) }
        if !line.present.isEmpty { parts.append(String(localized: "在场 ") + line.present.prefix(5).joined(separator: " ")) }
        return parts.joined(separator: " · ")
    }

    /// The scene around the line, the line itself dark and marked.
    private func context(_ line: UnsureLine) -> AttributedString {
        var out = AttributedString()
        let at = Int(line.sentence) - Int(line.contextFirst)
        for (i, s) in line.context.enumerated() {
            if i == at, let r = s.range(of: line.text) {
                var before = AttributedString(String(s[..<r.lowerBound])); before.foregroundColor = YYColor.t3
                var mid = AttributedString(line.text)
                mid.foregroundColor = YYColor.t1
                mid.backgroundColor = YYColor.hlY.opacity(0.55)
                var after = AttributedString(String(s[r.upperBound...])); after.foregroundColor = YYColor.t3
                out += before + mid + after
            } else {
                var a = AttributedString(s)
                a.foregroundColor = i < at ? YYColor.t3 : YYColor.t3.opacity(0.7)
                out += a
            }
        }
        return out
    }

    private func choose(_ line: UnsureLine, _ choice: UserChoice, name: String) {
        let before = items.count
        let core = core, id = bookID
        Task {
            await Self.saveSpeaker(core, bookID: id, line: line, choice: choice)
            await load()
            picks += 1
            let freed = max(0, before - items.count - 1)
            settled += freed + 1
            undo = (line, name, freed)
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }

    private func takeBack() {
        guard let u = undo else { return }
        undo = nil
        let core = core, id = bookID
        Task {
            await Self.clearSpeaker(core, bookID: id, line: u.line)
            picks = max(0, picks - 1)
            settled = max(0, settled - u.freed - 1)
            await load()
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }

    private func skip() {
        guard !items.isEmpty else { return }
        undo = nil
        items.append(items.removeFirst())
    }

    private func load() async {
        let s = section
        let (list, everyone) = await Self.loadReview(core, bookID: bookID, section: s)
        // The chapter being read first, then the lines that settle the most.
        items = list.filter { $0.section == s } + list.filter { $0.section != s }
        looks = Dictionary(everyone.map { ($0.name, $0) }, uniquingKeysWith: { a, _ in a })
        loaded = true
    }

    @concurrent nonisolated private static func saveSpeaker(_ core: YuyueCore, bookID: String, line: UnsureLine, choice: UserChoice) async {
        _ = try? core.dramaSetSpeaker(bookId: bookID, section: line.section, sentence: line.sentence, start: line.start, choice: choice)
    }

    @concurrent nonisolated private static func clearSpeaker(_ core: YuyueCore, bookID: String, line: UnsureLine) async {
        _ = try? core.dramaClearSpeaker(bookId: bookID, section: line.section, sentence: line.sentence, start: line.start)
    }

    @concurrent nonisolated private static func loadReview(_ core: YuyueCore, bookID: String, section: UInt32) async -> ([UnsureLine], [CastPick]) {
        ((try? core.dramaReview(bookId: bookID, uptoSection: section)) ?? [],
         (try? core.dramaEveryone(bookId: bookID, uptoSection: section)) ?? [])
    }
}

// MARK: 可能是同一个人

/// Two rows the model thought one person, asked: 合成一个 or 不是.
struct SameSuggestionRow: View {
    let core: YuyueCore
    let bookID: String
    let suggestion: SameSuggestion
    let avatar: (String) -> (String, Color)

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                person(suggestion.a)
                Text("和").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                person(suggestion.b)
                Text("可能是同一个人").font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            HStack(spacing: 6) {
                Button(String(localized: "合成一个")) { edit(.merge(into: suggestion.a, from: suggestion.b)) }
                    .buttonStyle(YYButtonStyle(kind: .main, size: .small))
                Button(String(localized: "不是")) { edit(.notSame(a: suggestion.a, b: suggestion.b)) }
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
            }
        }
        .padding(.vertical, YYUI.s(10))
    }

    private func person(_ name: String) -> some View {
        let (a, c) = avatar(name)
        return HStack(spacing: 5) {
            DramaAvatar(text: a, color: c, size: 22)
            Text(name).font(YYUI.font(11.5, .semibold)).lineLimit(1)
        }
    }

    private func edit(_ e: CastEdit) {
        let core = core, id = bookID
        Task {
            await CastCore.editCast(core, bookID: id, edit: e)
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }
}

// MARK: 人物页 · 名字和男女

/// How the book calls someone (the shown name first; × makes a name someone of its own, ＋ adds
/// one the book uses), and whether they're a man or a woman.
struct RoleNamesSection: View {
    let core: YuyueCore
    let bookID: String
    let name: String
    @State private var names: [String] = []
    @State private var gender = CharacterGender.unknown
    @State private var adding = false
    @State private var newName = ""

    var body: some View {
        YYGroup {
            VStack(alignment: .leading, spacing: 8) {
                Text("书里怎么叫").font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                Flow(spacing: 5) {
                    ForEach(Array(names.enumerated()), id: \.element) { i, n in chip(n, main: i == 0) }
                    Button { adding = true } label: {
                        Text("＋").font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                            .padding(.horizontal, 9).frame(height: YYUI.s(22))
                            .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel("加一个叫法")
                }
                .animation(YYMotion.standard, value: names)
            }
            .padding(.vertical, YYUI.s(10))
            let genderSegments = YYSegments(
                items: [(CharacterGender.m, String(localized: "男")), (.f, String(localized: "女")), (.unknown, String(localized: "不知道"))],
                selection: Binding(get: { gender }, set: { g in gender = g; edit(.setGender(name: name, gender: g)) }),
                small: true
            )
            ViewThatFits(in: .horizontal) {
                HStack(spacing: YYUI.s(10)) {
                    Image(systemName: "person").font(YYUI.font(13)).foregroundStyle(YYColor.t2).frame(width: YYUI.s(15))
                    Text("是").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).fixedSize(horizontal: true, vertical: false)
                    Spacer(minLength: YYUI.s(8))
                    genderSegments.fixedSize(horizontal: true, vertical: false)
                }
                .frame(minHeight: YYUI.s(38))
                VStack(alignment: .leading, spacing: YYUI.s(6)) {
                    HStack(spacing: YYUI.s(10)) {
                        Image(systemName: "person").font(YYUI.font(13)).foregroundStyle(YYColor.t2).frame(width: YYUI.s(15))
                        Text("是").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    }
                    genderSegments
                }
                .padding(.vertical, YYUI.s(8))
            }
        }
        .task { await load() }
        .yyPrompt(String(localized: "书里还怎么叫\(name)"), text: $newName, prompt: String(localized: "名字"),
                  isPresented: $adding, confirm: String(localized: "加上")) {
            let n = newName.trimmingCharacters(in: .whitespaces)
            newName = ""
            adding = false
            if !n.isEmpty { edit(.addName(to: name, name: n)) }
        }
    }

    private func chip(_ n: String, main: Bool) -> some View {
        HStack(spacing: 3) {
            Button { if !main { edit(.show(name: n)) } } label: {
                Text(n).font(YYUI.font(11, main ? .semibold : .regular)).foregroundStyle(YYColor.t1)
            }
            .buttonStyle(.plain)
            .help(main ? "" : String(localized: "列表和气泡里用这个名字"))
            if !main {
                Button { edit(.split(name: n)) } label: {
                    Image(systemName: "xmark").font(YYUI.font(7.5, .semibold)).foregroundStyle(YYColor.t3).frame(width: 14, height: 14)
                }
                .buttonStyle(.plain)
                .help(String(localized: "这不是\(name)：拆出去成另一个人"))
            }
        }
        .padding(.leading, 9).padding(.trailing, main ? 9 : 4)
        .frame(height: YYUI.s(22))
        .background(YYColor.bg, in: .rect(cornerRadius: 7))
        .overlay(RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func edit(_ e: CastEdit) {
        let core = core, id = bookID
        Task {
            await CastCore.editCast(core, bookID: id, edit: e)
            await load()
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }

    private func load() async {
        let (ns, g) = await Self.loadRole(core, bookID: bookID, name: name)
        names = ns.isEmpty ? [name] : ns
        gender = g
    }

    @concurrent nonisolated private static func loadRole(_ core: YuyueCore, bookID: String, name: String) async -> ([String], CharacterGender) {
        ((try? core.dramaNames(bookId: bookID, name: name)) ?? [],
         (try? core.dramaGender(bookId: bookID, name: name)) ?? .unknown)
    }
}

/// What right-clicking (long-pressing) someone in the cast offers.
struct CastRowActions {
    let core: YuyueCore
    let bookID: String
    let member: CastMember

    func edit(_ e: CastEdit) {
        let core = core, id = bookID
        Task {
            await CastCore.editCast(core, bookID: id, edit: e)
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }
}
