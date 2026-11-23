#if os(iOS)
import SwiftUI
import YuyueCore

/// 书伴 on the phone: the same talk as the Mac's panel, in a sheet over the
/// page. Half height to keep the page in sight; a 出处 tag turns the page underneath.
struct PhoneCompanion: View {
    @Bindable var model: ReaderModel
    @State private var draft = ""
    @State private var settings = false
    @State private var slashDismissedForDraft: String?
    @FocusState private var typing: Bool
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var companion: BookCompanion { model.companion }

    private var slashMatches: [CompanionCommand] {
        guard let q = CompanionCommand.slashQuery(in: draft),
              slashDismissedForDraft != draft
        else { return [] }
        return CompanionCommand.matching(q)
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                mode.padding(.horizontal, 16).padding(.top, 4)
                chat
                VStack(alignment: .leading, spacing: 8) {
                    if !slashMatches.isEmpty {
                        slashCard.transition(.opacity)
                    } else {
                        if let quote = model.companionQuote { quoted(quote).transition(.opacity) }
                        suggestions
                    }
                    input
                }
                .animation(YYMotion.layout, value: model.companionQuote != nil)
                .animation(YYMotion.quick, value: slashMatches.isEmpty)
                .padding(.horizontal, 16)
                .padding(.top, 10)
                .padding(.bottom, 4)
            }
            .background(YYColor.bg)
            .navigationTitle("书伴")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Menu {
                        Button("清空对话", systemImage: "trash") { companion.clear() }.disabled(companion.turns.isEmpty)
                        Button("书伴设置", systemImage: "gearshape") { settings = true }
                    } label: {
                        Image(systemName: "ellipsis")
                    }
                    .accessibilityLabel(Text("更多"))
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { model.panel = nil }
                }
            }
            .sheet(isPresented: $settings) {
                NavigationStack {
                    PhoneCompanionSettings()
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { settings = false } } }
                }
            }
        }
        .onChange(of: draft) { _, new in
            if slashDismissedForDraft != new { slashDismissedForDraft = nil }
        }
        .onDisappear { model.pager.setSource(nil) }
    }

    private var mode: some View {
        VStack(alignment: .leading, spacing: 6) {
            Picker("", selection: Bindable(companion).scope) {
                Text("仅限已读").tag(BookCompanion.Scope.read)
                Text("全书范围").tag(BookCompanion.Scope.whole)
            }
            .labelsHidden()
            .pickerStyle(.segmented)
            Text(companion.scope == .read ? readUpTo + String(localized: "，不涉及后续情节") : String(localized: "检索整本书，可能包含后续剧透"))
                .font(.yy(size: 12)).foregroundStyle(YYColor.t3).padding(.horizontal, 2)
        }
    }

    private var readUpTo: String {
        if let page = model.bookPage { return String(localized: "截至第 \(page) 页") }
        return String(localized: "截至当前位置")
    }

    // MARK: Turns

    private var chat: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if companion.turns.isEmpty { welcome.transition(.opacity) }
                    ForEach(companion.turns) { turn in
                        VStack(alignment: .leading, spacing: 10) {
                            bubble(turn.question)
                            reply(turn).transition(.opacity)
                        }
                        // 正在翻阅原文… gives way to the answer (or the failure) with a fade.
                        .animation(YYMotion.standard, value: turn.answer != nil || turn.failed != nil)
                        .transition(.opacity)
                        .id(turn.id)
                    }
                }
                .animation(YYMotion.standard, value: companion.turns.map(\.id))
                .padding(16)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollDismissesKeyboard(.interactively)
            .onChange(of: companion.turns.map(\.id)) {
                if let last = companion.turns.last { withAnimation(YYMotion.standard) { proxy.scrollTo(last.id, anchor: .bottom) } }
            }
            .onChange(of: companion.busy) {
                if let last = companion.turns.last { withAnimation(YYMotion.standard) { proxy.scrollTo(last.id, anchor: .bottom) } }
            }
        }
    }

    @ViewBuilder private var welcome: some View {
        let config = CompanionConfig.current
        VStack(alignment: .leading, spacing: 8) {
            Text("读到哪儿问到哪儿").font(.yy(size: 15, weight: .semibold)).foregroundStyle(YYColor.t1)
            Text("可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，点一下就翻到那句。")
                .font(.yy(size: 14)).foregroundStyle(YYColor.t2).lineSpacing(3)
            if !config.isReady {
                Text(config.provider == .apple ? Companion.appleAvailability ?? "" : String(localized: "还没填好 \(config.provider.title) 的 Key"))
                    .font(.yy(size: 13)).foregroundStyle(YYColor.t3).lineSpacing(3)
                Button("去设置 · 书伴") { settings = true }
                    .buttonStyle(YYButtonStyle(kind: .main, size: .small)).padding(.top, 2)
            }
        }
        .padding(16)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(YYColor.inset, in: .rect(cornerRadius: 16))
    }

    private func bubble(_ text: String) -> some View {
        HStack {
            Spacer(minLength: 48)
            Text(text)
                .font(.yy(size: 15))
                .lineSpacing(3)
                .multilineTextAlignment(.leading)
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 14).padding(.vertical, 9)
                .background {
                    YYShapeLift(shape: UnevenRoundedRectangle(topLeadingRadius: 16, bottomLeadingRadius: 16, bottomTrailingRadius: 4, topTrailingRadius: 16),
                                lift: .secondary(scheme: scheme, onPaper: false, hover: false))
                }
                .textSelection(.enabled)
        }
    }

    @ViewBuilder private func reply(_ turn: BookCompanion.Turn) -> some View {
        if let answer = turn.answer {
            VStack(alignment: .leading, spacing: 8) {
                if answer.notYet {
                    Text("\(readUpTo)，这件事可能要到后面才写。切换至「全书范围」即可作答，但可能包含后续剧透。")
                        .font(.yy(size: 15)).lineSpacing(8).foregroundStyle(YYColor.t1)
                    Button { companion.ask(turn.question, quote: turn.quote, scope: .whole) } label: {
                        HStack(spacing: 8) {
                            Text("用全书回答").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
                            Label("可能剧透", systemImage: "exclamationmark.triangle")
                                .font(.yy(size: 11, weight: .semibold)).foregroundStyle(YYColor.warn)
                        }
                        .padding(.horizontal, 14).frame(height: 34)
                        .background(YYColor.card, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line))
                    }
                    .buttonStyle(.plain)
                    .disabled(companion.busy)
                } else {
                    Text(cited(answer))
                        .font(.yy(size: 15))
                        .lineSpacing(8)
                        .foregroundStyle(YYColor.t1)
                        .textSelection(.enabled)
                        .environment(\.openURL, OpenURLAction { url in
                            if let n = Int(url.host() ?? ""), let source = answer.sources[n] { companion.show(source) }
                            return .handled
                        })
                    HStack(spacing: 4) {
                        Image(systemName: turn.scope == .whole ? "book" : "sparkle").font(.yy(size: 10))
                        Text(turn.scope == .whole ? answer.label + String(localized: " · 用了全书") : answer.label)
                    }
                    .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                }
            }
        } else if let failed = turn.failed {
            VStack(alignment: .leading, spacing: 8) {
                Label(failed, systemImage: "exclamationmark.circle")
                    .font(.yy(size: 13)).foregroundStyle(YYColor.danger)
                // Only the last one: the way out once, not under every failed question.
                if turn.id == companion.turns.last?.id, needsSetup(failed) {
                    Button { settings = true } label: {
                        HStack(spacing: 6) {
                            Text("去设置 · 书伴")
                            Image(systemName: "chevron.right").font(.yy(size: 11, weight: .semibold))
                        }
                        .font(.yy(size: 13, weight: .medium)).foregroundStyle(YYColor.t1)
                        .padding(.horizontal, 12).frame(height: 32)
                        .background(YYColor.inset, in: .rect(cornerRadius: 9))
                    }
                    .buttonStyle(.plain)
                }
            }
        } else {
            HStack(spacing: 8) {
                ProgressView()
                Text("正在翻阅原文…").font(.yy(size: 13)).foregroundStyle(YYColor.t3)
            }
        }
    }

    /// A failure only settings can mend: nothing set up, this device's own unavailable, a bad key.
    private func needsSetup(_ failed: String) -> Bool {
        !CompanionConfig.current.isReady || failed == CompanionError.badKey.errorDescription
            || failed == CompanionError.notReady.errorDescription || failed == Companion.appleAvailability
    }

    /// The answer with each [n] turned into a little 第 N 页 tag that goes there.
    private func cited(_ answer: BookCompanion.Answer) -> AttributedString {
        var out = AttributedString()
        var rest = Substring(answer.text)
        while let m = rest.firstMatch(of: /\s?\[(\d+)\]/) {
            out += AttributedString(String(rest[..<m.range.lowerBound]))
            if let n = Int(m.1), let source = answer.sources[n] {
                let place = source.page.map { String(localized: "第 \($0) 页") } ?? String(localized: "出处")
                let label = " " + place + (source.later == true ? String(localized: " · 未读") : "") + " "
                var tag = AttributedString(label.replacingOccurrences(of: " ", with: "\u{00A0}"))
                tag.font = .yy(size: 11, weight: .semibold)
                if source.later == true, companion.shown != source {
                    tag.foregroundColor = tint.accent
                    tag.underlineStyle = Text.LineStyle(pattern: .dash, color: tint.accent)
                } else {
                    tag.foregroundColor = companion.shown == source ? YYColor.card : tint.accent
                    tag.backgroundColor = companion.shown == source ? tint.accent : tint.accentSoft
                }
                tag.link = URL(string: "yuyue-cite://\(n)")
                out += AttributedString(" ") + tag
            }
            rest = rest[m.range.upperBound...]
        }
        out += AttributedString(String(rest))
        return out
    }

    // MARK: Asking

    private func quoted(_ quote: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(quote)
                .font(YYUI.serif(13))
                .foregroundStyle(YYColor.t2)
                .lineLimit(2)
                .padding(.leading, 9)
                .overlay(alignment: .leading) { Rectangle().fill(tint.accent).frame(width: 1.5) }
            Spacer(minLength: 0)
            Button("不带这句", systemImage: "xmark") { model.companionQuote = nil }
                .labelStyle(.iconOnly)
                .font(.yy(size: 11, weight: .bold))
                .foregroundStyle(YYColor.t3)
                .buttonStyle(.plain)
        }
    }

    private var suggestions: some View {
        let list = model.companionQuote != nil ? [String(localized: "这句是什么意思？"), String(localized: "为什么这么写？")]
            : companion.turns.last?.answer?.next ?? [String(localized: "前面发生了什么？"), String(localized: "这一章讲了什么？")]
        return ScrollView(.horizontal) {
            HStack(spacing: 8) {
                ForEach(list, id: \.self) { s in
                    Button { send(s) } label: {
                        Text(s)
                            .font(.yy(size: 13)).foregroundStyle(YYColor.t2)
                            .padding(.horizontal, 13).frame(height: 32)
                            .background(YYColor.card, in: .capsule)
                            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    .buttonStyle(YYPressStyle(scale: 0.97))
                    .disabled(companion.busy)
                }
            }
        }
        .scrollIndicators(.never)
    }

    private var slashCard: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 2) {
                ForEach(slashMatches) { cmd in
                    let disabled = companion.busy && cmd.category == .ask
                    let isCurrentScope: Bool = {
                        if case .scope(let s) = cmd.action { return s == companion.scope }
                        return false
                    }()
                    Button {
                        if !disabled { executeCommand(cmd, argument: "") }
                    } label: {
                        HStack(spacing: 10) {
                            Image(systemName: cmd.icon)
                                .font(.yy(size: 14, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .frame(width: 28, height: 28)
                                .background(YYColor.inset, in: .rect(cornerRadius: 8))
                            VStack(alignment: .leading, spacing: 1) {
                                HStack(spacing: 6) {
                                    Text(cmd.slashName)
                                        .font(.system(size: YYUI.s(13.5), weight: .semibold, design: .monospaced))
                                        .foregroundStyle(YYColor.t1)
                                    Text(cmd.title)
                                        .font(.yy(size: 13.5, weight: .medium))
                                        .foregroundStyle(YYColor.t1)
                                        .lineLimit(1)
                                }
                                Text(cmd.subtitle)
                                    .font(.yy(size: 12))
                                    .foregroundStyle(YYColor.t3)
                                    .lineLimit(1)
                            }
                            Spacer(minLength: 4)
                            if isCurrentScope {
                                Text("当前")
                                    .font(.yy(size: 11, weight: .medium))
                                    .foregroundStyle(tint.accent)
                                    .padding(.horizontal, 6)
                                    .padding(.vertical, 2)
                                    .background(tint.accentSoft, in: .capsule)
                            }
                        }
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .frame(minHeight: 44)
                        .contentShape(.rect)
                        .opacity(disabled ? 0.42 : 1)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(6)
        }
        .scrollIndicators(.never)
        .frame(maxHeight: 220)
        .background(YYColor.card, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private var canSubmitDraft: Bool {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }
        if let parsed = CompanionCommand.parse(draft), parsed.command.category == .control, parsed.argument.isEmpty {
            return true
        }
        return !companion.busy
    }

    private var input: some View {
        HStack(alignment: .bottom, spacing: 8) {
            TextField(
                "",
                text: $draft,
                prompt: Text(companion.turns.isEmpty ? String(localized: "问这本书，或输入 / …") : String(localized: "继续问，或输入 / …")),
                axis: .vertical
            )
            .font(.yy(size: 16))
            .lineLimit(1 ... 5)
            .focused($typing)
            .padding(.vertical, 10)
            // As the Mac's: a rounded square. Nothing to send: grey, not a faded wash of the colour.
            let empty = !canSubmitDraft
            Button { submitDraft() } label: {
                Image(systemName: "arrow.up").font(.yy(size: 14, weight: .bold))
                    .foregroundStyle(empty ? YYColor.t3 : tint.onBlack)
                    .frame(width: 34, height: 34)
                    .background(empty ? YYColor.inset : tint.black, in: .rect(cornerRadius: 11))
                    .animation(YYMotion.quick, value: empty)
            }
            .accessibilityLabel(Text("发送"))
            .buttonStyle(.plain)
            .padding(.bottom, 5)
            .allowsHitTesting(!empty)
        }
        .padding(.leading, 16).padding(.trailing, 5)
        .background(YYColor.card, in: .rect(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func submitDraft() {
        if let parsed = CompanionCommand.parse(draft) {
            executeCommand(parsed.command, argument: parsed.argument)
            return
        }
        send(draft)
    }

    private func executeCommand(_ command: CompanionCommand, argument: String) {
        let arg = argument.trimmingCharacters(in: .whitespacesAndNewlines)
        switch command.action {
        case .recap, .chapter, .characters, .explain, .quote, .quiz:
            guard !companion.busy else { return }
            if let q = command.question(argument: arg, hasQuote: model.companionQuote != nil, chapterTitle: model.chapterTitle) {
                slashDismissedForDraft = nil
                send(q)
            }
        case .scope(let target):
            slashDismissedForDraft = nil
            companion.scope = target
            draft = ""
            if !arg.isEmpty, !companion.busy {
                companion.ask(arg, quote: model.companionQuote, scope: target)
                model.companionQuote = nil
            }
        case .model, .settings:
            slashDismissedForDraft = nil
            draft = ""
            settings = true
        case .clear:
            slashDismissedForDraft = nil
            draft = ""
            companion.clear()
        }
    }

    private func send(_ text: String) {
        let q = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, !companion.busy else { return }
        companion.ask(q, quote: model.companionQuote)
        draft = ""
        slashDismissedForDraft = nil
        model.companionQuote = nil
    }
}
#endif
