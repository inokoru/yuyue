import SwiftUI
import YuyueCore

/// 替换规则: this book's rules and the ones for every book, each saying what
/// becomes what, marked 正则 and 只改朗读, with a switch to turn one off. They run from the top
/// down and can be dragged into another order. A book open in a window follows at once.
struct ReplaceRulesView: View {
    let core: YuyueCore
    let bookID: String
    let bookTitle: String
    /// ‹, when this sheet was opened from 排版. × still just closes.
    var back: (() -> Void)? = nil
    var done: (() -> Void)?

    enum Scope: Hashable { case book, all }

    @State private var scope = Scope.book
    @State private var mine: [ReplaceRule] = []
    @State private var shared: [ReplaceRule] = []
    @State private var editing: Editing?
    @State private var commonShown = false
    @State private var failure: String?
    /// The rules just deleted, for 撤销 a few seconds.
    @State private var removed: [ReplaceRule] = []
    @State private var removedToken = UUID()
    /// Header + rows + footer on the Mac sheet.
    @State private var paper: CGFloat = 0
    @State private var commonPaper: CGFloat = 0

    struct Editing: Identifiable {
        var rule: ReplaceRule
        var id: String { rule.id.isEmpty ? "new" : rule.id }
    }

    private var rules: [ReplaceRule] { scope == .book ? mine : shared }

    var body: some View {
        VStack(spacing: 0) {
            if let done {
                YYSheetHeader(title: String(localized: "替换规则"), subtitle: bookTitle, back: back, close: done)
                    .padding(.leading, YYUI.s(20))
                    .padding(.trailing, YYUI.s(16))
                    .padding(.top, YYUI.sheetTop)
                    .padding(.bottom, YYUI.s(4))
                    .yySheetBlock()
            }
            rulesList
            if let done {
                YYSheetFooter(
                    extraTitle: String(localized: "＋ 常用规则"),
                    extra: { commonShown = true },
                    primaryTitle: String(localized: "好了"),
                    onPrimary: done
                )
                .yySheetBlock()
            }
        }
        .background(YYColor.bg)
        #if os(macOS)
        .frame(width: done == nil ? nil : YYUI.s(420))
        .fixedSize(horizontal: done != nil, vertical: done != nil)
        .paperWindow()
        .onPreferenceChange(YYSheetMeasure.self) { if done != nil { paper = $0 } }
        .background { if done != nil { MacPaperSize(width: YYUI.s(420), height: paper) } }
        #else
        .toolbar(done == nil ? .visible : .hidden, for: .navigationBar)
        .navigationBarTitleDisplayMode(.inline)
        .navigationTitle("替换规则")
        #endif
        .toolbar {
            #if os(iOS)
            if done == nil {
                ToolbarItem(placement: .primaryAction) {
                    Button("添加规则", systemImage: "plus") { editing = Editing(rule: blank) }
                }
            }
            #endif
        }
        .sheet(item: $editing) { e in
            ReplaceRuleEditor(core: core, bookID: bookID, bookTitle: bookTitle, rule: e.rule) { saved in
                editing = nil
                if let saved {
                    changed(saved.book)
                    // 删掉这条规则 in the editor: gone from the lists now, so it can be brought back.
                    if !saved.id.isEmpty, !(mine + shared).contains(where: { $0.id == saved.id }) { justRemoved([saved]) }
                }
            }
            .paperWindow()
        }
        .sheet(isPresented: $commonShown) { common }
        #if DEBUG
        .task {
            // `-debugRuleEdit 1`: 打开编辑器添一条新规则;`-debugRulesCommon 1`: 从常用规则里挑。
            let d = UserDefaults.standard
            if d.bool(forKey: "debugRuleEdit") { editing = Editing(rule: blank) }
            if d.bool(forKey: "debugRulesCommon") { commonShown = true }
        }
        #endif
        .yyNotice(String(localized: "没能改好"), message: failure ?? "",
                  isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } }))
        .onAppear(perform: load)
    }

    /// The rules. A Mac sheet hugs them; a phone page still scrolls to fill.
    @ViewBuilder private var rulesList: some View {
        let rows = VStack(alignment: .leading, spacing: 0) {
            YYSegments(items: [(Scope.book, String(localized: "这本书")), (Scope.all, String(localized: "全部书"))], selection: $scope,
                       counts: [.book: mine.count, .all: shared.count])
                .padding(.top, YYUI.s(10))
                .padding(.bottom, YYUI.s(12))
            YYGroup {
                if rules.isEmpty {
                    Text(scope == .book ? String(localized: "这本书还没有自己的规则") : String(localized: "还没有用于全部书的规则"))
                        .font(YYUI.font(11.5)).foregroundStyle(YYColor.t3)
                        .frame(minHeight: YYUI.s(44))
                }
                ForEach(rules, id: \.id) { rule in
                    RuleRow(rule: rule) { on in setEnabled(rule, on) } edit: { editing = Editing(rule: rule) } remove: {
                        if let i = rules.firstIndex(where: { $0.id == rule.id }) { delete(IndexSet(integer: i)) }
                    }
                    .draggable(rule.id)
                    .dropDestination(for: String.self) { ids, _ in
                        guard let id = ids.first, let from = rules.firstIndex(where: { $0.id == id }),
                              let to = rules.firstIndex(where: { $0.id == rule.id }), from != to else { return false }
                        move(IndexSet(integer: from), to > from ? to + 1 : to)
                        return true
                    }
                }
                if !removed.isEmpty { undoRow.transition(.opacity) }
                // A pushed page on the phone has + in its bar already.
                if showsInlineAdd {
                    Button { editing = Editing(rule: blank) } label: {
                        HStack(spacing: YYUI.s(6)) {
                            Image(systemName: "plus")
                            Text("添加一条规则")
                        }
                        .font(YYUI.font(11.5, .medium))
                        .foregroundStyle(YYTint.standard.accent)
                        .frame(maxWidth: .infinity, minHeight: YYUI.s(38), alignment: .leading)
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
            // A sheet has ＋ 常用规则 in its footer; a pushed page has no footer, so it lists it here.
            if done == nil || (scope == .book && !shared.isEmpty) {
                YYGroup {
                    if done == nil {
                        Button { commonShown = true } label: {
                            YYRow(String(localized: "从常用规则里挑"), icon: "slider.horizontal.3", value: String(localized: "网址水印、多余空行…"), chevron: true)
                        }
                        .buttonStyle(.plain)
                    }
                    if scope == .book, !shared.isEmpty {
                        Button { scope = .all } label: {
                            YYRow(String(localized: "全部书的 \(shared.count) 条也在生效"), icon: "square.stack.3d.up", value: String(localized: "看看"), chevron: true)
                        }
                        .buttonStyle(.plain)
                    }
                }
                .padding(.top, YYUI.s(12))
            }
            YYNote(String(localized: "规则从上往下依次执行，按住一条可以拖动顺序。只改朗读的规则不动屏幕上的字，只改读出来的音。"))
        }
        #if os(macOS)
        if done != nil {
            YYFitScroll(maxHeight: YYUI.s(420)) {
                rows
                    .padding(.horizontal, YYUI.s(20))
                    .padding(.bottom, YYUI.s(4))
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
        } else {
            YYPage { rows }
        }
        #else
        YYPage { rows }
        #endif
    }

    private var showsInlineAdd: Bool {
        #if os(iOS)
        done != nil
        #else
        true
        #endif
    }

    private var blank: ReplaceRule {
        ReplaceRule(id: "", book: scope == .book ? bookID : nil, find: "", replace: "", regex: false,
                    speechOnly: false, enabled: true, order: 0)
    }

    private func load() {
        mine = (try? core.replaceRules(book: bookID)) ?? []
        shared = (try? core.replaceRules(book: nil)) ?? []
    }

    private func changed(_ book: String?) {
        load()
        NotificationCenter.default.post(name: .replaceRulesChanged, object: book)
    }

    private func run(_ book: String?, _ work: () throws -> Void) {
        do {
            try work()
        } catch {
            failure = Library.message(for: error)
        }
        changed(book)
    }

    private func setEnabled(_ rule: ReplaceRule, _ on: Bool) {
        run(rule.book) { try core.setReplaceRuleEnabled(id: rule.id, enabled: on) }
    }

    private func delete(_ offsets: IndexSet) {
        let gone = offsets.map { rules[$0] }
        withAnimation(YYMotion.layout) {
            run(scope == .book ? bookID : nil) {
                for rule in gone { try core.deleteReplaceRule(id: rule.id) }
            }
        }
        justRemoved(gone)
    }

    private func justRemoved(_ gone: [ReplaceRule]) {
        withAnimation(YYMotion.standard) { removed = gone }
        let token = UUID()
        removedToken = token
        Task {
            try? await Task.sleep(for: .seconds(5))
            if removedToken == token { withAnimation(YYMotion.standard) { removed = [] } }
        }
    }

    /// 删掉了“A → B” · 撤销, for five seconds after a rule goes.
    private var undoRow: some View {
        HStack(spacing: YYUI.s(8)) {
            Image(systemName: "trash").foregroundStyle(YYColor.t3)
            Text(removed.count == 1
                 ? String(localized: "删掉了“\(removed[0].find) → \(removed[0].replace)”")
                 : String(localized: "删掉了 \(removed.count) 条规则"))
                .foregroundStyle(YYColor.t2)
                .lineLimit(1)
            Spacer(minLength: YYUI.s(8))
            Button("撤销") {
                let back = removed
                withAnimation(YYMotion.layout) {
                    run(back.first?.book) {
                        for rule in back { try core.restoreReplaceRule(id: rule.id) }
                    }
                    removed = []
                }
            }
            .font(YYUI.font(11.5, .semibold))
            .foregroundStyle(YYTint.standard.accent)
            .buttonStyle(.plain)
            #if os(macOS)
            .keyboardShortcut("z", modifiers: .command)
            #endif
        }
        .font(YYUI.font(11.5))
        .frame(minHeight: YYUI.s(38))
    }

    /// Gives the moved rule an order between its new neighbours.
    private func move(_ from: IndexSet, _ to: Int) {
        var list = rules
        list.move(fromOffsets: from, toOffset: to)
        guard let moved = from.first.map({ rules[$0] }), let i = list.firstIndex(where: { $0.id == moved.id }) else { return }
        let before = i > 0 ? list[i - 1].order : nil
        let after = i < list.count - 1 ? list[i + 1].order : nil
        let order = switch (before, after) {
        case let (b?, a?): (b + a) / 2
        case let (b?, nil): b + 1
        case let (nil, a?): a - 1
        case (nil, nil): moved.order
        }
        if scope == .book { mine = list } else { shared = list }
        run(moved.book) { try core.moveReplaceRule(id: moved.id, order: order) }
    }

    // MARK: 从常用规则里挑

    private var commonList: some View {
        YYGroup {
            ForEach(core.commonReplaceRules(), id: \.title) { c in
                let added = rules.contains { $0.find == c.rule.find && $0.regex == c.rule.regex }
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(3)) {
                        Text(c.title).font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text(c.detail).font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                    }
                    Spacer()
                    Button(added ? String(localized: "✓ 加上了") : String(localized: "＋ 加上")) {
                        guard !added else { return }
                        var rule = c.rule
                        rule.book = scope == .book ? bookID : nil
                        run(rule.book) { _ = try core.saveReplaceRule(rule: rule) }
                    }
                    .buttonStyle(YYButtonStyle(kind: added ? .plain : .card, size: .small, ink: added ? YYColor.t3 : nil))
                    .allowsHitTesting(!added)
                }
                .padding(.vertical, YYUI.s(10))
            }
        }
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(12))
            .padding(.bottom, YYUI.s(4))
            .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var common: some View {
        VStack(spacing: 0) {
            YYSheetHeader(
                title: scope == .book ? String(localized: "加到这本书") : String(localized: "加到全部书"),
                subtitle: bookTitle,
                close: { commonShown = false }
            )
            .padding(.leading, YYUI.s(20))
            .padding(.trailing, YYUI.s(16))
            .padding(.top, YYUI.sheetGrabberTop)
            .padding(.bottom, YYUI.s(4))
            .yySheetBlock()
            #if os(macOS)
            YYFitScroll(maxHeight: YYUI.s(420)) { commonList }
            #else
            // iPhone: the list takes what the sheet leaves, so the title and 好了 stay in sight.
            ScrollView { commonList }
                .scrollBounceBehavior(.basedOnSize)
            #endif
            YYSheetFooter(
                meta: String(localized: "加完还能改、能关"),
                primaryTitle: String(localized: "好了"),
                onPrimary: { commonShown = false }
            )
            .yySheetBlock()
        }
        .background(YYColor.bg)
        .onPreferenceChange(YYSheetMeasure.self) { commonPaper = $0 }
        .paperWindow()
        #if os(macOS)
        .frame(width: YYUI.s(420))
        .fixedSize(horizontal: true, vertical: true)
        .background(MacPaperSize(width: YYUI.s(420), height: commonPaper))
        #else
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        #endif
    }
}

/// What is found, an arrow, what it becomes (or 删掉), its tags and its switch.
/// A rule turned off goes grey.
private struct RuleRow: View {
    let rule: ReplaceRule
    let toggle: @MainActor (Bool) -> Void
    let edit: () -> Void
    let remove: () -> Void

    var body: some View {
        HStack(spacing: YYUI.s(10)) {
            Button(action: edit) {
                VStack(alignment: .leading, spacing: YYUI.s(4)) {
                    Text(rule.find)
                        .font(rule.regex ? YYUI.font(11.5, .medium, design: .monospaced) : YYUI.font(12, .semibold))
                        .foregroundStyle(rule.enabled ? YYColor.t1 : YYColor.t3)
                        .lineLimit(1)
                    HStack(spacing: YYUI.s(5)) {
                        Image(systemName: "arrow.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
                        if rule.replace.isEmpty {
                            Text("删掉").foregroundStyle(rule.enabled ? YYColor.t2 : YYColor.t3)
                        } else {
                            Text(rule.replace).foregroundStyle(rule.enabled ? YYColor.t1 : YYColor.t3).lineLimit(1)
                        }
                        if rule.regex { YYTag(text: String(localized: "正则")) }
                        if rule.speechOnly { YYTag(text: String(localized: "只改朗读"), icon: "ear") }
                    }
                    .font(YYUI.font(10.5))
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            YYSwitch(isOn: Binding(get: { rule.enabled }, set: { val in toggle(val) }), label: rule.enabled ? String(localized: "关掉这条规则") : String(localized: "打开这条规则"))
        }
        .padding(.vertical, YYUI.s(10))
        .contextMenu {
            Button("编辑", systemImage: "pencil", action: edit)
            Button("删掉", systemImage: "trash", role: .destructive, action: remove)
        }
    }
}

/// 编辑一条规则: 查找, 替换为, the 正则 switch, 改哪里 and 用于, and underneath a
/// preview that follows the typing: how many places in this book, the first one before and after.
struct ReplaceRuleEditor: View {
    let core: YuyueCore
    let bookID: String
    let bookTitle: String
    /// Nil when cancelled.
    let finish: (ReplaceRule?) -> Void
    @State private var rule: ReplaceRule
    private let originalBook: String?
    @State private var preview: RulePreview?
    @State private var detail: BookDetail?
    @State private var failure: String?
    @FocusState private var typing: Bool

    init(core: YuyueCore, bookID: String, bookTitle: String, rule: ReplaceRule, finish: @escaping (ReplaceRule?) -> Void) {
        self.core = core
        self.bookID = bookID
        self.bookTitle = bookTitle
        self.finish = finish
        _rule = State(initialValue: rule)
        originalBook = rule.book
    }

    private var isNew: Bool { rule.id.isEmpty }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: YYUI.s(12)) {
                YYSheetHeader(title: isNew ? String(localized: "添加规则") : String(localized: "编辑规则")) { finish(nil) }
                    .padding(.bottom, YYUI.s(2))
                YYGroup {
                    field(String(localized: "查找"), prompt: String(localized: "要换掉的字"), text: $rule.find, mono: rule.regex)
                        .focused($typing)
                    field(String(localized: "替换为"), prompt: String(localized: "留空就是删掉"), text: $rule.replace, mono: rule.regex)
                }
                YYGroup {
                    YYRow(title: String(localized: "正则表达式")) { YYSwitch(isOn: $rule.regex, label: String(localized: "正则表达式")) }
                    segmentRow(String(localized: "改哪里")) {
                        YYSegments(items: [(false, String(localized: "显示和朗读")), (true, String(localized: "只改朗读"))], selection: $rule.speechOnly, small: true)
                    }
                    segmentRow(String(localized: "用于")) {
                        YYSegments(items: [(true, String(localized: "这本书")), (false, String(localized: "全部书"))],
                                   selection: Binding(get: { rule.book != nil }, set: { rule.book = $0 ? bookID : nil }), small: true)
                    }
                }
                previewCard
                Button("保存", action: save)
                    .buttonStyle(YYButtonStyle(kind: .main, size: .large, wide: true))
                    .keyboardShortcut(.defaultAction)
                    .disabled(rule.find.isEmpty || preview?.error != nil)
                if !isNew {
                    Button("删掉这条规则") {
                        do {
                            try core.deleteReplaceRule(id: rule.id)
                            finish(rule)
                        } catch {
                            failure = Library.message(for: error)
                        }
                    }
                    .font(YYUI.font(11.5, .medium))
                    .foregroundStyle(YYColor.danger)
                    .buttonStyle(.plain)
                    .frame(maxWidth: .infinity)
                }
            }
            .padding(.horizontal, YYUI.s(16))
            .padding(.bottom, YYUI.s(16))
            .padding(.top, YYUI.sheetGrabberTop)
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .task(id: PreviewKey(rule: rule)) { await refresh() }
        .task { detail = try? core.book(id: bookID) }
        .yyNotice(String(localized: "没能保存"), message: failure ?? "",
                  isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } }))
        .onAppear { if isNew { typing = true } }
        #if os(macOS)
        .frame(width: 440, height: 560)
        #else
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        #endif
    }

    private func field(_ label: String, prompt: String, text: Binding<String>, mono: Bool) -> some View {
        YYField(label: label) {
            TextField(label, text: text, prompt: Text(prompt).foregroundStyle(YYColor.t3))
                .textFieldStyle(.plain)
                .font(mono ? YYUI.font(11.5, design: .monospaced) : YYUI.font(11.5))
                .autocorrectionDisabled()
                #if os(iOS)
                .textInputAutocapitalization(.never)
                #endif
        }
    }

    private func segmentRow<Segments: View>(_ title: String, @ViewBuilder segments: () -> Segments) -> some View {
        let segs = segments()
        return ViewThatFits(in: .horizontal) {
            HStack(spacing: YYUI.s(10)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).fixedSize(horizontal: true, vertical: false)
                Spacer(minLength: YYUI.s(8))
                segs.fixedSize(horizontal: true, vertical: false)
            }
            .frame(minHeight: YYUI.s(38))
            VStack(alignment: .leading, spacing: YYUI.s(6)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                segs
            }
            .padding(.vertical, YYUI.s(8))
            VStack(alignment: .leading, spacing: YYUI.s(6)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                ScrollView(.horizontal, showsIndicators: false) {
                    segs.fixedSize(horizontal: true, vertical: false)
                }
            }
            .padding(.vertical, YYUI.s(8))
        }
    }

    // MARK: Preview

    /// How many places in this book, and the first one before and after.
    private var previewCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack {
                if let error = preview?.error {
                    Text(error).foregroundStyle(YYColor.danger)
                } else if let p = preview, !rule.find.isEmpty {
                    Text(p.count == 0 ? String(localized: "这本书里没有找到") : String(localized: "这本书里有 \(p.count)\(p.more ? "+" : "") 处"))
                } else {
                    Text("预览")
                }
                Spacer()
                if let first = preview?.first {
                    Text([String(localized: "第 1 处"), detail?.chapterTitle(for: first.section)].compactMap { $0 }.joined(separator: " · "))
                        .font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
            }
            .font(YYUI.font(10.5, .semibold))
            .foregroundStyle(YYColor.t1)
            if let first = preview?.first {
                line(String(localized: "改前"), before(first)).padding(.top, YYUI.s(7))
                line(String(localized: "改后"), after(first)).padding(.top, YYUI.s(7))
                if rule.speechOnly {
                    Text("屏幕上还是改前的样子，只有朗读按改后的读")
                        .font(YYUI.font(10)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(7))
                }
            } else {
                Text(rule.find.isEmpty ? String(localized: "写好要查找的字，这里马上显示改前改后") : String(localized: "没有可以预览的地方"))
                    .font(YYUI.font(10.5)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(7))
            }
        }
        .padding(.top, YYUI.s(10))
        .padding(.bottom, YYUI.s(11))
        .padding(.horizontal, YYUI.s(12))
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(14)).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func line(_ label: String, _ text: AttributedString) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: YYUI.s(8)) {
            Text(label).font(YYUI.font(10)).foregroundStyle(YYColor.t3)
            Text(text).font(YYUI.font(11.5, design: .serif)).foregroundStyle(YYColor.t1).lineSpacing(YYUI.s(11.5) * 0.7)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private func before(_ s: RuleSample) -> AttributedString {
        var matched = AttributedString(s.matched)
        matched.strikethroughStyle = Text.LineStyle(pattern: .solid, color: YYColor.danger)
        matched.foregroundColor = YYColor.danger
        matched.backgroundColor = YYColor.danger.opacity(0.09)
        return AttributedString(s.lead) + matched + AttributedString(s.tail)
    }

    private func after(_ s: RuleSample) -> AttributedString {
        var replaced = AttributedString(s.replaced)
        replaced.backgroundColor = YYTint.standard.accentSoft
        return AttributedString(s.lead) + replaced + AttributedString(s.tail)
    }

    /// What the preview depends on.
    private struct PreviewKey: Equatable {
        let find: String, replace: String, regex: Bool

        init(rule: ReplaceRule) {
            find = rule.find
            replace = rule.replace
            regex = rule.regex
        }
    }

    private func refresh() async {
        try? await Task.sleep(for: .milliseconds(250))
        guard !Task.isCancelled else { return }
        let core = core, book = bookID, rule = rule
        let found = await Self.previewRule(core, book: book, rule: rule)
        guard !Task.isCancelled else { return }
        preview = found
    }

    @concurrent nonisolated private static func previewRule(_ core: YuyueCore, book: String, rule: ReplaceRule) async -> RulePreview? {
        try? core.previewReplaceRule(book: book, rule: rule)
    }

    private func save() {
        do {
            let saved = try core.saveReplaceRule(rule: rule)
            // Moved between this book and every book: the old list changes too.
            if !isNew, originalBook != saved.book { NotificationCenter.default.post(name: .replaceRulesChanged, object: originalBook) }
            finish(saved)
        } catch let CoreError.Invalid(message) {
            failure = message
        } catch {
            failure = Library.message(for: error)
        }
    }
}

// MARK: - 灵动听书胶囊 · 纠音与反馈视图（Mac & iPhone 共用）

/// Built-in phonetic quick-picks for frequently mispronounced Chinese polyphones.
private enum PolyphoneHints {
    struct Option: Hashable {
        let text: String
        let pinyin: String
    }

    private static let table: [String: [Option]] = [
        "贾赦": [Option(text: "贾社", pinyin: "shè"), Option(text: "贾舍", pinyin: "shě")],
        "单于": [Option(text: "蝉于", pinyin: "chán yú")],
        "赦": [Option(text: "社", pinyin: "shè"), Option(text: "舍", pinyin: "shě")],
        "行": [Option(text: "杭", pinyin: "háng"), Option(text: "形", pinyin: "xíng")],
        "重": [Option(text: "仲", pinyin: "zhòng"), Option(text: "虫", pinyin: "chóng")],
        "长": [Option(text: "掌", pinyin: "zhǎng"), Option(text: "常", pinyin: "cháng")],
        "乐": [Option(text: "月", pinyin: "yuè"), Option(text: "勒", pinyin: "lè")],
        "朝": [Option(text: "招", pinyin: "zhāo"), Option(text: "潮", pinyin: "cháo")],
        "差": [Option(text: "拆", pinyin: "chāi"), Option(text: "岔", pinyin: "chà")],
        "解": [Option(text: "谢", pinyin: "xiè"), Option(text: "姐", pinyin: "jiě")],
        "角": [Option(text: "绝", pinyin: "jué"), Option(text: "脚", pinyin: "jiǎo")],
        "数": [Option(text: "树", pinyin: "shù"), Option(text: "暑", pinyin: "shǔ")],
        "率": [Option(text: "虑", pinyin: "lǜ"), Option(text: "帅", pinyin: "shuài")],
        "给": [Option(text: "己", pinyin: "jǐ"), Option(text: "轨", pinyin: "gěi")],
        "还": [Option(text: "环", pinyin: "huán"), Option(text: "孩", pinyin: "hái")],
        "藏": [Option(text: "脏", pinyin: "zàng"), Option(text: "仓", pinyin: "cáng")],
        "传": [Option(text: "赚", pinyin: "zhuàn"), Option(text: "船", pinyin: "chuán")],
        "调": [Option(text: "吊", pinyin: "diào"), Option(text: "条", pinyin: "tiáo")],
        "盛": [Option(text: "胜", pinyin: "shèng"), Option(text: "成", pinyin: "chéng")],
        "省": [Option(text: "醒", pinyin: "xǐng"), Option(text: "审", pinyin: "shěng")],
        "泊": [Option(text: "博", pinyin: "bó"), Option(text: "坡", pinyin: "pō")],
        "露": [Option(text: "路", pinyin: "lù"), Option(text: "漏", pinyin: "lòu")],
        "降": [Option(text: "酱", pinyin: "jiàng"), Option(text: "祥", pinyin: "xiáng")],
        "校": [Option(text: "叫", pinyin: "jiào"), Option(text: "笑", pinyin: "xiào")],
        "血": [Option(text: "谢", pinyin: "xuè"), Option(text: "写", pinyin: "xiě")],
        "载": [Option(text: "在", pinyin: "zài"), Option(text: "宰", pinyin: "zǎi")],
        "折": [Option(text: "蛇", pinyin: "shé"), Option(text: "哲", pinyin: "zhé")],
        "着": [Option(text: "招", pinyin: "zhāo"), Option(text: "浊", pinyin: "zhuó")],
        "拗": [Option(text: "傲", pinyin: "ào"), Option(text: "扭", pinyin: "niù")],
        "殷": [Option(text: "烟", pinyin: "yān"), Option(text: "音", pinyin: "yīn")],
        "阿": [Option(text: "啊", pinyin: "ā"), Option(text: "鹅", pinyin: "ē")],
        "禅": [Option(text: "蝉", pinyin: "chán"), Option(text: "善", pinyin: "shàn")],
        "尉": [Option(text: "玉", pinyin: "yù"), Option(text: "卫", pinyin: "wèi")],
        "区": [Option(text: "欧", pinyin: "ōu"), Option(text: "驱", pinyin: "qū")],
        "仇": [Option(text: "求", pinyin: "qiú"), Option(text: "筹", pinyin: "chóu")],
        "朴": [Option(text: "瓢", pinyin: "piáo"), Option(text: "普", pinyin: "pǔ")],
        "查": [Option(text: "渣", pinyin: "zhā"), Option(text: "茶", pinyin: "chá")],
        "盖": [Option(text: "哥", pinyin: "gě"), Option(text: "钙", pinyin: "gài")],
        "任": [Option(text: "人", pinyin: "rén"), Option(text: "认", pinyin: "rèn")],
        "华": [Option(text: "化", pinyin: "huà"), Option(text: "划", pinyin: "huá")]
    ]

    static func suggestions(for word: String) -> [Option] {
        let trimmed = word.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return [] }
        if let exact = table[trimmed] { return exact }
        let chars = Array(trimmed)
        guard chars.count <= 4 else { return [] }
        // Check individual characters inside a short compound word (e.g. 贾赦 / 行家).
        for (idx, ch) in chars.enumerated() {
            if let opts = table[String(ch)] {
                return opts.map { opt in
                    var copy = chars
                    copy[idx] = Character(opt.text)
                    return Option(text: String(copy), pinyin: opt.pinyin)
                }
            }
        }
        return []
    }

    /// Finds the first likely polyphone range inside `chars` as a gentle initial selection if none was provided.
    static func firstPolyphoneRange(in chars: [Character]) -> ClosedRange<Int>? {
        for (i, ch) in chars.enumerated() where table[String(ch)] != nil {
            // Expand to 2-char word if preceded by a CJK character (often a surname/prefix like 贾赦).
            if i > 0, isCJK(chars[i - 1]) {
                return (i - 1)...i
            }
            return i...i
        }
        return nil
    }

    private static func isCJK(_ c: Character) -> Bool {
        guard let v = c.unicodeScalars.first?.value else { return false }
        return (0x4E00...0x9FFF).contains(v)
    }
}

private struct CharFramesKey: PreferenceKey {
    static let defaultValue: [Int: CGRect] = [:]
    static func reduce(value: inout [Int: CGRect], nextValue: () -> [Int: CGRect]) {
        value.merge(nextValue(), uniquingKeysWith: { _, new in new })
    }
}

/// Flows individual character views seamlessly like natural book prose with zero horizontal gap.
private struct FlowCharLayout: Layout {
    var lineSpacing: CGFloat = 6

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let maxW = proposal.width ?? 420
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowH: CGFloat = 0
        for sub in subviews {
            let s = sub.sizeThatFits(.unspecified)
            if x > 0, x + s.width > maxW {
                x = 0
                y += rowH + lineSpacing
                rowH = 0
            }
            x += s.width
            rowH = max(rowH, s.height)
        }
        return CGSize(width: maxW, height: y + rowH)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let maxW = bounds.width
        var x: CGFloat = 0
        var y: CGFloat = 0
        var rowH: CGFloat = 0
        for sub in subviews {
            let s = sub.sizeThatFits(.unspecified)
            if x > 0, x + s.width > maxW {
                x = 0
                y += rowH + lineSpacing
                rowH = 0
            }
            sub.place(at: CGPoint(x: bounds.minX + x, y: bounds.minY + y), anchor: .topLeading, proposal: .unspecified)
            x += s.width
            rowH = max(rowH, s.height)
        }
    }
}

/// Real unbroken serif sentence supporting fluid 1:1 mouse/finger drag selection of any 1–8 characters.
private struct DragSelectSentenceView: View {
    let chars: [Character]
    @Binding var selection: ClosedRange<Int>?
    let paper: YYPaper
    let accent: Color
    let fontSize: CGFloat
    let onCommitRange: (ClosedRange<Int>) -> Void

    @State private var frames: [Int: CGRect] = [:]
    @State private var anchor: Int?

    var body: some View {
        FlowCharLayout(lineSpacing: 6) {
            ForEach(Array(chars.enumerated()), id: \.offset) { index, ch in
                let selected = selection?.contains(index) ?? false
                Text(String(ch))
                    .font(YYUI.serif(fontSize, selected ? .semibold : .regular))
                    .foregroundStyle(selected ? accent : paper.ink)
                    .padding(.vertical, 1)
                    .background(selected ? accent.opacity(paper.isDark ? 0.28 : 0.16) : .clear, in: .rect(cornerRadius: 2))
                    .overlay(alignment: .bottom) {
                        if selected {
                            Rectangle().fill(accent).frame(height: 1.5)
                        }
                    }
                    .background(
                        GeometryReader { g in
                            Color.clear.preference(key: CharFramesKey.self, value: [index: g.frame(in: .named("charFlow"))])
                        }
                    )
            }
        }
        .coordinateSpace(name: "charFlow")
        .onPreferenceChange(CharFramesKey.self) { frames = $0 }
        .contentShape(Rectangle())
        .gesture(
            DragGesture(minimumDistance: 0, coordinateSpace: .named("charFlow"))
                .onChanged { value in
                    guard let hit = hitIndex(at: value.location) else { return }
                    if anchor == nil {
                        anchor = hit
                        selection = hit...hit
                    } else if let a = anchor {
                        let clamped = max(a - 7, min(a + 7, hit))
                        selection = min(a, clamped)...max(a, clamped)
                    }
                }
                .onEnded { _ in
                    anchor = nil
                    if let sel = selection {
                        onCommitRange(sel)
                    }
                }
        )
    }

    private func hitIndex(at point: CGPoint) -> Int? {
        if let exact = frames.first(where: { $0.value.insetBy(dx: -1, dy: -3).contains(point) }) {
            return exact.key
        }
        var bestIdx: Int?
        var bestDist = CGFloat.greatestFiniteMagnitude
        for (idx, rect) in frames {
            let dx = max(rect.minX - point.x, 0, point.x - rect.maxX)
            let dy = max(rect.minY - point.y, 0, point.y - rect.maxY)
            let dist = hypot(dx, dy)
            if dist < bestDist {
                bestDist = dist
                bestIdx = idx
            }
        }
        return bestDist <= 28 ? bestIdx : nil
    }
}

/// Morphing Glass Capsule pronunciation editor: unbroken drag-selectable sentence + live playback +
/// single concentric Spotlight-style unified input trough. Zero modal sheets, zero ugly word chips.
struct CapsulePronunciationPanel: View {
    let initialFind: String
    let sentence: String
    let paper: YYPaper
    let isPlaying: Bool
    var compact: Bool = false
    let onTogglePlay: () -> Void
    let onSave: (String, String, Bool) -> Bool
    let onClose: () -> Void

    @State private var chars: [Character]
    @State private var selRange: ClosedRange<Int>?
    @State private var manualFind: String
    @State private var replace: String = ""
    @State private var wholeLibrary = false
    @FocusState private var inputFocused: Bool

    private var accent: Color {
        paper.isEink ? paper.ink : YYTint.standard.accent
    }

    init(
        initialFind: String,
        sentence: String,
        paper: YYPaper,
        isPlaying: Bool,
        compact: Bool = false,
        onTogglePlay: @escaping () -> Void,
        onSave: @escaping (String, String, Bool) -> Bool,
        onClose: @escaping () -> Void
    ) {
        self.initialFind = initialFind
        self.sentence = sentence
        self.paper = paper
        self.isPlaying = isPlaying
        self.compact = compact
        self.onTogglePlay = onTogglePlay
        self.onSave = onSave
        self.onClose = onClose

        let trimmedSentence = sentence.trimmingCharacters(in: .whitespacesAndNewlines)
        let cappedChars = Array(trimmedSentence.prefix(200))
        _chars = State(initialValue: cappedChars)
        _manualFind = State(initialValue: initialFind)

        var initialRange: ClosedRange<Int>? = nil
        if !initialFind.isEmpty, let r = trimmedSentence.range(of: initialFind) {
            let start = trimmedSentence.distance(from: trimmedSentence.startIndex, to: r.lowerBound)
            let len = initialFind.count
            if start >= 0, start + len - 1 < cappedChars.count {
                initialRange = start...(start + len - 1)
            }
        } else if !cappedChars.isEmpty {
            initialRange = PolyphoneHints.firstPolyphoneRange(in: cappedChars)
        }
        _selRange = State(initialValue: initialRange)

        let effectiveFind: String
        if let r = initialRange {
            effectiveFind = String(cappedChars[r])
        } else {
            effectiveFind = initialFind
        }
        if let first = PolyphoneHints.suggestions(for: effectiveFind).first {
            _replace = State(initialValue: first.text)
        }
    }

    private var activeFind: String {
        if let r = selRange, r.lowerBound >= 0, r.upperBound < chars.count {
            return String(chars[r])
        }
        return manualFind.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private var canSubmit: Bool {
        !activeFind.isEmpty && !replace.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private var hints: [PolyphoneHints.Option] {
        PolyphoneHints.suggestions(for: activeFind)
    }

    var body: some View {
        VStack(alignment: .leading, spacing: compact ? 10 : 11) {
            // Top micro header: live acoustic wave + prompt + quick polyphone hints + continuous play/pause + close
            HStack(spacing: 8) {
                SpeakingBars(paused: !isPlaying)
                    .frame(width: 15, height: 14)
                Text(chars.isEmpty ? String(localized: "纠正读音") : String(localized: "划选读错的字"))
                    .font(.yy(size: 11.5, weight: .medium))
                    .foregroundStyle(paper.ink2)
                    .lineLimit(1)

                if !compact, !hints.isEmpty {
                    quickHintsRow
                }

                Spacer(minLength: 4)

                Button(action: onTogglePlay) {
                    Image(systemName: isPlaying ? "pause.fill" : "play.fill")
                        .font(.yy(size: 10.5, weight: .semibold))
                        .foregroundStyle(paper.ink2)
                        .frame(width: 24, height: 24)
                        .contentShape(.circle)
                }
                .buttonStyle(YYPressStyle(scale: 0.9))
                .accessibilityLabel(isPlaying ? Text("暂停朗读") : Text("继续朗读"))

                Button(action: onClose) {
                    Image(systemName: "xmark")
                        .font(.yy(size: 10.5, weight: .semibold))
                        .foregroundStyle(paper.ink2)
                        .frame(width: 24, height: 24)
                        .contentShape(.circle)
                }
                .buttonStyle(YYPressStyle(scale: 0.9))
                .accessibilityLabel(Text("收起"))
            }

            // Real unbroken book sentence (drag or tap any 1-8 characters; scrollable for long sentences)
            if !chars.isEmpty {
                ScrollView(.vertical, showsIndicators: false) {
                    DragSelectSentenceView(
                        chars: chars,
                        selection: $selRange,
                        paper: paper,
                        accent: accent,
                        fontSize: compact ? 14.5 : 15.5
                    ) { range in
                        let picked = String(chars[range])
                        manualFind = picked
                        if let first = PolyphoneHints.suggestions(for: picked).first {
                            replace = first.text
                        } else {
                            replace = ""
                        }
                        inputFocused = true
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: compact ? 64 : 68)
            }

            if compact, !hints.isEmpty {
                quickHintsRow
            }

            // Single sculptural concentric Spotlight-style input trough
            HStack(spacing: 8) {
                if chars.isEmpty {
                    TextField(String(localized: "错词"), text: $manualFind)
                        .textFieldStyle(.plain)
                        .font(YYUI.serif(13, .semibold))
                        .foregroundStyle(paper.ink)
                        .frame(width: 68)
                } else {
                    Text(activeFind.isEmpty ? String(localized: "选字") : activeFind)
                        .font(YYUI.serif(13, .semibold))
                        .foregroundStyle(activeFind.isEmpty ? paper.ink2.opacity(0.6) : paper.ink)
                        .lineLimit(1)
                        .frame(maxWidth: 82, alignment: .leading)
                        .fixedSize(horizontal: true, vertical: false)
                }

                Image(systemName: "arrow.right")
                    .font(.yy(size: 10.5, weight: .medium))
                    .foregroundStyle(paper.ink2.opacity(0.65))

                TextField(String(localized: "输入同音字…"), text: $replace)
                    .textFieldStyle(.plain)
                    .font(.yy(size: 13))
                    .foregroundStyle(paper.ink)
                    .focused($inputFocused)
                    .onSubmit { submit() }

                Button {
                    wholeLibrary.toggle()
                } label: {
                    Text(wholeLibrary ? String(localized: "全部书") : String(localized: "仅本书"))
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(paper.ink2)
                        .padding(.horizontal, 6)
                        .frame(height: 26)
                        .contentShape(.capsule)
                }
                .buttonStyle(YYPressStyle(scale: 0.95))
                .fixedSize()
                .help("点击切换生效范围：仅这本书 / 全部书")

                Button(action: submit) {
                    Image(systemName: "return")
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(paper.onMain)
                        .frame(width: 30, height: 30)
                        .background(canSubmit ? YYTint.standard.play(on: paper) : paper.ink.opacity(0.18), in: .circle)
                }
                .buttonStyle(YYPressStyle(scale: 0.92))
                .disabled(!canSubmit)
                .help("保存并立即重念这句")
                .accessibilityLabel(Text("保存并重念"))
            }
            .padding(.leading, 14)
            .padding(.trailing, 4)
            .padding(.vertical, 4)
            .background(
                (inputFocused ? YYColor.card : paper.ink.opacity(paper.isDark ? 0.08 : 0.05)),
                in: .capsule
            )
            .overlay(
                Capsule().strokeBorder(
                    inputFocused ? accent.opacity(0.42) : paper.line,
                    lineWidth: inputFocused ? 1 : 0.5
                )
            )
        }
        .padding(.horizontal, compact ? 14 : 16)
        .padding(.vertical, compact ? 13 : 14)
        .onAppear {
            if !activeFind.isEmpty {
                inputFocused = true
            }
        }
    }

    private var quickHintsRow: some View {
        HStack(spacing: 6) {
            ForEach(hints, id: \.self) { opt in
                Button {
                    replace = opt.text
                    _ = onSave(activeFind, opt.text, wholeLibrary)
                } label: {
                    HStack(spacing: 3) {
                        Text(opt.text).font(YYUI.serif(11.5, .medium))
                        Text(opt.pinyin).font(.yy(size: 10)).opacity(0.72)
                    }
                    .foregroundStyle(accent)
                    .padding(.horizontal, 7)
                    .padding(.vertical, 2)
                    .background(accent.opacity(paper.isDark ? 0.18 : 0.10), in: .capsule)
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
            }
        }
    }

    private func submit() {
        guard canSubmit else { return }
        _ = onSave(activeFind, replace, wholeLibrary)
    }
}

/// Compact 1-line morphing feedback inside the bottom listening capsule (replaces jarring top toasts).
struct CapsuleFeedbackStrip: View {
    let mode: ReaderModel.ListenCapsuleMode
    let paper: YYPaper
    var compact: Bool = false
    let onUndoMark: (String) -> Void
    let onDismiss: () -> Void

    private var accent: Color {
        paper.isEink ? paper.ink : YYTint.standard.accent
    }

    var body: some View {
        HStack(spacing: 10) {
            switch mode {
            case .pronunciationDone(let from, let to):
                SpeakingBars(paused: false)
                    .frame(width: 15, height: 14)
                HStack(spacing: 5) {
                    Text("「\(from)」→「\(to)」")
                        .font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(paper.ink)
                    Text("正在重念…")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(paper.ink2)
                }
                .lineLimit(1)
                Spacer(minLength: 4)
                Button("完成", action: onDismiss)
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(paper.ink2)
                    .buttonStyle(.plain)

            case .marked(let excerpt, let undoID):
                Image(systemName: "checkmark")
                    .font(.yy(size: 10, weight: .bold))
                    .foregroundStyle(accent)
                    .frame(width: 22, height: 22)
                    .background(accent.opacity(paper.isDark ? 0.20 : 0.12), in: .circle)
                HStack(spacing: 5) {
                    Text("已记下这句")
                        .font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(paper.ink)
                    if !excerpt.isEmpty {
                        Text("“\(excerpt)”")
                            .font(YYUI.serif(11.5))
                            .foregroundStyle(paper.ink2)
                            .lineLimit(1)
                    }
                }
                Spacer(minLength: 4)
                if let undoID {
                    Button("撤销") { onUndoMark(undoID) }
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(paper.ink)
                        .padding(.horizontal, 10)
                        .frame(height: 24)
                        .background(paper.ink.opacity(paper.isDark ? 0.12 : 0.07), in: .capsule)
                        .buttonStyle(YYPressStyle(scale: 0.94))
                }

            case .rewound(let sentenceNumber):
                Image(systemName: "gobackward")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(accent)
                HStack(spacing: 5) {
                    Text("已为你回退一句")
                        .font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(paper.ink)
                    Text("接上暂停前的语境")
                        .font(.yy(size: 11.5))
                        .foregroundStyle(paper.ink2)
                        .lineLimit(1)
                }
                Spacer(minLength: 4)
                Text("第 \(sentenceNumber) 句")
                    .font(.yy(size: 10.5).monospacedDigit())
                    .foregroundStyle(paper.ink2)

            case .idle, .pronunciation:
                EmptyView()
            }
        }
        .padding(.horizontal, compact ? 14 : 16)
        .frame(height: compact ? 46 : 48)
    }
}
