#if os(iOS)
import SwiftUI
import YuyueCore

/// 书伴 on the phone: the same talk as the Mac's panel, in a sheet over the
/// page. Half height to keep the page in sight; a 出处 tag turns the page underneath.
struct PhoneCompanion: View {
    @Bindable var model: ReaderModel
    @State private var draft = ""
    @State private var settings = false
    @State private var linesOpen = false
    @State private var modelPickerOpen = false
    @State private var slashManualOpen = false
    @State private var slashDismissedForDraft: String?
    @State private var promptTick = 0
    @State private var pickingThought = false
    @State private var pickedMessages: Set<BookCompanion.MessagePick> = []
    @State private var previewMerged = false
    /// Measured height of the bottom composer block (including its vertical padding)
    /// so the slash menu and ready-lines card float just above it without competing
    /// with `chat` for VStack height when the keyboard is open.
    @State private var askHeight: CGFloat = 98
    @FocusState private var typing: Bool
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage("companion.provider") private var activeProviderID = ""
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var companion: BookCompanion { model.companion }
    private var focus: CompanionFocus { .shared }

    private var slashQuery: String? {
        if let q = CompanionCommand.slashQuery(in: draft) {
            return slashDismissedForDraft == draft ? nil : q
        }
        return slashManualOpen ? "" : nil
    }

    private var slashMatches: [CompanionCommand] {
        guard let q = slashQuery else { return [] }
        return CompanionCommand.matching(q)
    }

    private var slashMenuOpen: Bool { !slashMatches.isEmpty }

    private var readyQuestions: [String] {
        let _ = promptTick
        let _ = focus.tick
        return CompanionPrompts.suggestions(
            hasQuote: model.companionQuote != nil,
            followUps: companion.turns.last?.answer?.next,
            quoteDefaults: [String(localized: "这句是什么意思？"), String(localized: "为什么这么写？")],
            startDefaults: [String(localized: "前面发生了什么？"), String(localized: "这一章讲了什么？")]
        )
    }

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                mode.padding(.horizontal, 16).padding(.top, 4)
                chat
                if pickingThought {
                    pickBar
                        .padding(.horizontal, 16)
                        .padding(.top, 10)
                        .padding(.bottom, 8)
                } else {
                    VStack(alignment: .leading, spacing: 8) {
                        if let quote = model.companionQuote {
                            quoted(quote).transition(.opacity)
                        }
                        input
                    }
                    .animation(YYMotion.layout, value: model.companionQuote != nil)
                    .padding(.horizontal, 14)
                    .padding(.top, 8)
                    .padding(.bottom, 8)
                    .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { askHeight = $0 }
                }
            }
            .overlay {
                ZStack(alignment: .bottom) {
                    if !pickingThought, slashMenuOpen {
                        Color.clear
                            .contentShape(.rect)
                            .onTapGesture { dismissSlashMenu() }
                        slashCard
                            .padding(.horizontal, 14)
                            .transition(YYMotion.reduce ? .opacity : .opacity.combined(with: .scale(scale: 0.96, anchor: .bottom)))
                    } else if !pickingThought, linesOpen, !readyQuestions.isEmpty {
                        Color.clear
                            .contentShape(.rect)
                            .onTapGesture { linesOpen = false }
                        readyLinesCard
                            .padding(.horizontal, 14)
                            .transition(YYMotion.reduce ? .opacity : .opacity.combined(with: .scale(scale: 0.96, anchor: .bottom)))
                    }
                }
                .padding(.top, 8)
                .padding(.bottom, askHeight)
                .animation(YYMotion.quick, value: slashMenuOpen)
                .animation(YYMotion.quick, value: linesOpen)
                .animation(YYMotion.quick, value: slashMatches.map(\.id))
            }
            .background(YYColor.bg)
            .navigationTitle(pickingThought ? (pickedMessages.isEmpty ? String(localized: "勾选要存的消息") : String(localized: "已选 \(pickedMessages.count) 条")) : String(localized: "书伴"))
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    if pickingThought {
                        let all = allSelectablePicks
                        let allChosen = !all.isEmpty && pickedMessages == all
                        Button(allChosen ? String(localized: "取消全选") : String(localized: "全选")) {
                            withAnimation(YYMotion.quick) {
                                pickedMessages = allChosen ? [] : all
                                if allChosen { previewMerged = false }
                            }
                        }
                    } else {
                        Menu {
                            Button("多选存为想法", systemImage: "checklist") { enterPicking() }.disabled(companion.turns.isEmpty)
                            Divider()
                            Button("清空对话", systemImage: "trash") { companion.clear() }.disabled(companion.turns.isEmpty)
                            Button("书伴设置", systemImage: "gearshape") { settings = true }
                        } label: {
                            Image(systemName: "ellipsis")
                        }
                        .accessibilityLabel(Text("更多"))
                    }
                }
                ToolbarItem(placement: .confirmationAction) {
                    if pickingThought {
                        Button("取消") { exitPicking() }
                    } else {
                        Button("完成") { model.panel = nil }
                    }
                }
            }
            .sheet(isPresented: $settings, onDismiss: { focus.reload() }) {
                NavigationStack {
                    PhoneCompanionSettings()
                        .toolbar { ToolbarItem(placement: .confirmationAction) { Button("完成") { settings = false } } }
                }
            }
            .sheet(isPresented: $modelPickerOpen) {
                PhoneCompanionModelPicker(
                    openSettings: {
                        modelPickerOpen = false
                        Task { @MainActor in
                            try? await Task.sleep(for: .milliseconds(250))
                            settings = true
                        }
                    },
                    close: { modelPickerOpen = false }
                )
            }
        }
        .onChange(of: draft) { _, new in
            if slashDismissedForDraft != new { slashDismissedForDraft = nil }
            if CompanionCommand.slashQuery(in: new) != nil {
                slashManualOpen = false
                linesOpen = false
            } else if !new.isEmpty {
                slashManualOpen = false
                linesOpen = false
            }
        }
        .onChange(of: typing) { _, on in
            if on { linesOpen = false }
        }
        .onChange(of: linesOpen) { _, on in
            if on {
                typing = false
                dismissSlashMenu()
            }
        }
        .onReceive(NotificationCenter.default.publisher(for: CompanionPrompts.changed)) { _ in
            promptTick &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
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

    private var allSelectablePicks: Set<BookCompanion.MessagePick> {
        var out = Set<BookCompanion.MessagePick>()
        for turn in companion.turns {
            if !turn.question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                out.insert(.init(turnID: turn.id, role: .question))
            }
            if let a = turn.answer, !a.notYet {
                out.insert(.init(turnID: turn.id, role: .answer))
            }
        }
        return out
    }

    private func enterPicking(initial: BookCompanion.MessagePick? = nil) {
        typing = false
        linesOpen = false
        dismissSlashMenu()
        pickedMessages = initial.map { [$0] } ?? []
        previewMerged = false
        withAnimation(YYMotion.quick) { pickingThought = true }
    }

    private func exitPicking() {
        withAnimation(YYMotion.quick) {
            pickingThought = false
            previewMerged = false
            pickedMessages.removeAll()
        }
    }

    private func togglePick(_ pick: BookCompanion.MessagePick) {
        withAnimation(YYMotion.quick) {
            if pickedMessages.contains(pick) {
                pickedMessages.remove(pick)
                if pickedMessages.isEmpty { previewMerged = false }
            } else {
                pickedMessages.insert(pick)
            }
        }
    }

    private var pickBar: some View {
        let previewText = previewMerged && !pickedMessages.isEmpty
            ? BookCompanion.composedThought(from: companion.turns, picks: pickedMessages)
            : ""
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(String(localized: "可同时勾选你的提问与多条回答，合并存为 1 条想法"))
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if !pickedMessages.isEmpty {
                    Button(previewMerged ? String(localized: "收起预览") : String(localized: "预览合并")) {
                        withAnimation(YYMotion.quick) { previewMerged.toggle() }
                    }
                    .font(.yy(size: 12, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .buttonStyle(.plain)
                    .fixedSize()
                }
            }
            if !previewText.isEmpty {
                ScrollView {
                    Text(previewText)
                        .font(.yy(size: 13))
                        .lineSpacing(4)
                        .foregroundStyle(YYColor.t2)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .frame(maxHeight: 124)
                .padding(.horizontal, 12)
                .padding(.vertical, 9)
                .background(YYColor.inset, in: .rect(cornerRadius: 12))
                .transition(.opacity.combined(with: .move(edge: .bottom)))
            }
            HStack(spacing: 10) {
                Button(String(localized: "取消")) { exitPicking() }
                    .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                Spacer(minLength: 4)
                Button(pickedMessages.count > 1 ? String(localized: "合并存为想法 (\(pickedMessages.count))") : String(localized: "存为想法 (\(pickedMessages.count))")) {
                    if companion.saveThought(picks: pickedMessages) {
                        exitPicking()
                    }
                }
                .buttonStyle(YYButtonStyle(kind: .main, size: .small))
                .disabled(pickedMessages.isEmpty)
            }
        }
        .padding(12)
        .background(YYColor.card, in: .rect(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    // MARK: Turns

    private var chat: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 16) {
                    if companion.turns.isEmpty { welcome.transition(.opacity) }
                    ForEach(companion.turns) { turn in
                        VStack(alignment: .leading, spacing: 10) {
                            selectableMessage(pick: .init(turnID: turn.id, role: .question), enabled: pickingThought) {
                                bubble(turn.question)
                            }
                            selectableMessage(
                                pick: .init(turnID: turn.id, role: .answer),
                                enabled: pickingThought && (turn.answer.map { !$0.notYet } ?? false)
                            ) {
                                reply(turn).transition(.opacity)
                            }
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
            .onChange(of: companion.turns.last?.streaming) {
                if let last = companion.turns.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
    }

    @ViewBuilder private var welcome: some View {
        let _ = focus.tick
        let _ = activeProviderID
        let config = CompanionConfig.current
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 6) {
                Text("读到哪儿问到哪儿").font(.yy(size: 15, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text("可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，点一下就翻到那句；也可点回答下方的「存为想法」收进笔记。")
                    .font(.yy(size: 14)).foregroundStyle(YYColor.t2).lineSpacing(3)
            }
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.inset, in: .rect(cornerRadius: 16))

            if !config.isReady {
                phoneSetupCard(config)
            }
        }
    }

    private func phoneSetupCard(_ config: CompanionConfig) -> some View {
        let title: String = if config.isUnconfiguredFallback {
            String(localized: "添加 AI 服务商即可开始")
        } else if config.provider == .apple {
            String(localized: "开启或添加 AI 服务商")
        } else {
            String(localized: "完成 \(config.provider.title) 配置")
        }
        let subtitle: String = if config.isUnconfiguredFallback {
            String(localized: "支持 DeepSeek、通义千问、Claude、OpenAI、Ollama 等")
        } else if config.provider == .apple {
            Companion.appleAvailability ?? String(localized: "开启 Apple 智能，或添加其他服务商")
        } else {
            String(localized: "填入 API Key 后即可开始问答")
        }
        return Button { settings = true } label: {
            HStack(spacing: 12) {
                if config.provider == .apple {
                    Image(systemName: "sparkles")
                        .font(.yy(size: 15, weight: .semibold))
                        .foregroundStyle(tint.accent)
                        .frame(width: 34, height: 34)
                        .background(tint.accentSoft, in: .rect(cornerRadius: 10))
                } else {
                    ServiceTile(mark: .provider(config.provider), size: 34)
                }
                VStack(alignment: .leading, spacing: 3) {
                    Text(title)
                        .font(.yy(size: 14.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    Text(subtitle)
                        .font(.yy(size: 12.5))
                        .foregroundStyle(YYColor.t3)
                        .lineSpacing(2)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                HStack(spacing: 3) {
                    Text("去配置")
                        .font(.yy(size: 13, weight: .semibold))
                    Image(systemName: "chevron.right")
                        .font(.yy(size: 10.5, weight: .semibold))
                }
                .foregroundStyle(tint.accent)
                .fixedSize()
            }
            .padding(.horizontal, 14)
            .padding(.vertical, 13)
            .background(YYColor.card, in: .rect(cornerRadius: 16))
            .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.rect(cornerRadius: 16))
        }
        .buttonStyle(YYPressStyle(scale: 0.985))
    }

    @ViewBuilder
    private func selectableMessage<Content: View>(
        pick: BookCompanion.MessagePick,
        enabled: Bool,
        @ViewBuilder content: () -> Content
    ) -> some View {
        if enabled {
            let selected = pickedMessages.contains(pick)
            Button { togglePick(pick) } label: {
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.yy(size: 18, weight: .medium))
                        .foregroundStyle(selected ? tint.accent : YYColor.t3)
                        .padding(.top, 6)
                    content()
                        .allowsHitTesting(false)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .background(selected ? tint.accentSoft.opacity(0.45) : .clear, in: .rect(cornerRadius: 12))
                .overlay(
                    RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(selected ? tint.accent.opacity(0.32) : .clear, lineWidth: 1)
                )
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        } else {
            content()
        }
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
                    Text(cited(answer.text, sources: answer.sources))
                        .font(.yy(size: 15))
                        .lineSpacing(8)
                        .foregroundStyle(YYColor.t1)
                        .textSelection(.enabled)
                        .environment(\.openURL, OpenURLAction { url in
                            if let n = Int(url.host() ?? ""), let source = answer.sources[n] { companion.show(source) }
                            return .handled
                        })
                    if !pickingThought {
                        HStack(spacing: 8) {
                            saveThoughtButton(turn)
                            copyAnswerButton(answer)
                            multiPickButton(turn)
                            Spacer(minLength: 4)
                        }
                    }
                    HStack(spacing: 4) {
                        Image(systemName: turn.scope == .whole ? "book" : "sparkle").font(.yy(size: 10))
                        Text(turn.scope == .whole ? answer.label + String(localized: " · 用了全书") : answer.label)
                            .truncationMode(.middle)
                    }
                    .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                    .lineLimit(1)
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
        } else if let streaming = turn.streaming, !streaming.isEmpty {
            Text(cited(streaming, sources: [:], streaming: true))
                .font(.yy(size: 15))
                .lineSpacing(8)
                .foregroundStyle(YYColor.t1)
                .textSelection(.enabled)
        } else {
            HStack(spacing: 8) {
                ProgressView()
                Text(turn.thinking ? String(localized: "正在思考…") : String(localized: "正在翻阅原文…"))
                    .font(.yy(size: 13)).foregroundStyle(YYColor.t3)
            }
        }
    }

    private func saveThoughtButton(_ turn: BookCompanion.Turn) -> some View {
        let saved = turn.savedMarkID != nil
        return Button { companion.saveThought(for: turn.id) } label: {
            HStack(spacing: 4) {
                Image(systemName: saved ? "checkmark" : "square.and.pencil")
                    .font(.yy(size: 10.5, weight: .semibold))
                Text(saved ? String(localized: "已存想法") : String(localized: "存为想法"))
                    .font(.yy(size: 12, weight: saved ? .semibold : .medium))
            }
            .foregroundStyle(saved ? YYColor.ok : YYColor.t1)
            .padding(.horizontal, 11)
            .frame(height: 28)
            .background(saved ? YYColor.ok.opacity(0.12) : YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(saved ? YYColor.ok.opacity(0.32) : YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
    }

    private func copyAnswerButton(_ answer: BookCompanion.Answer) -> some View {
        Button {
            UIPasteboard.general.string = BookCompanion.noteText(from: answer.text)
            model.show(Library.Toast(icon: "checkmark", title: String(localized: "复制好了"), detail: ""))
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "doc.on.doc")
                    .font(.yy(size: 10, weight: .medium))
                Text(String(localized: "复制"))
                    .font(.yy(size: 12, weight: .medium))
            }
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
    }

    private func multiPickButton(_ turn: BookCompanion.Turn) -> some View {
        Button {
            enterPicking(initial: .init(turnID: turn.id, role: .answer))
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "checklist")
                    .font(.yy(size: 10, weight: .medium))
                Text(String(localized: "多选"))
                    .font(.yy(size: 12, weight: .medium))
            }
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 10)
            .frame(height: 28)
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
    }

    /// A failure only settings can mend: nothing set up, this device's own unavailable, a bad key.
    private func needsSetup(_ failed: String) -> Bool {
        !CompanionConfig.current.isReady || failed == CompanionError.badKey.errorDescription
            || failed == CompanionError.notReady.errorDescription || failed == Companion.appleAvailability
    }

    /// Renders Markdown (bold, lists, headings, inline code) and turns each `[n]` into a clickable `第 N 页` tag.
    private func cited(_ text: String, sources: [Int: BookCompanion.Source], streaming: Bool = false) -> AttributedString {
        var out = AttributedString()
        for segment in BookCompanion.richSegments(from: text, streaming: streaming) {
            switch segment {
            case .text(let s):
                out += AttributedString(s)
            case .bold(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 15, weight: .semibold)
                out += part
            case .italic(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 15).italic()
                out += part
            case .code(let s):
                var part = AttributedString(" \(s) ")
                part.font = .yy(size: 13.5, design: .monospaced)
                part.backgroundColor = YYColor.inset
                out += part
            case .strike(let s):
                var part = AttributedString(s)
                part.strikethroughStyle = .single
                part.foregroundColor = YYColor.t3
                out += part
            case .bullet:
                var dot = AttributedString("•\u{00A0}")
                dot.font = .yy(size: 15, weight: .bold)
                dot.foregroundColor = tint.accent
                out += dot
            case .quoteBar:
                var bar = AttributedString("▎\u{00A0}")
                bar.foregroundColor = YYColor.t3
                out += bar
            case .heading(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 15.5, weight: .semibold)
                out += part
            case .citation(let n):
                if let source = sources[n] {
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
            case .newline:
                out += AttributedString("\n")
            }
        }
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
            Button("不带这句", systemImage: "xmark") { model.clearCompanionQuote() }
                .labelStyle(.iconOnly)
                .font(.yy(size: 11, weight: .bold))
                .foregroundStyle(YYColor.t3)
                .buttonStyle(.plain)
        }
    }

    private var readyLinesList: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(readyQuestions, id: \.self) { q in
                Button {
                    linesOpen = false
                    send(q)
                } label: {
                    HStack(spacing: 8) {
                        Text(q)
                            .font(YYUI.serif(14))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(2)
                            .multilineTextAlignment(.leading)
                            .frame(maxWidth: .infinity, alignment: .leading)
                        Image(systemName: "arrow.up.right")
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(YYColor.t3)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 9)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .disabled(companion.busy)
            }
        }
        .padding(6)
    }

    private var readyLinesCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text("挑一句现成的")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 4)
                Button { linesOpen = false } label: {
                    Image(systemName: "xmark")
                        .font(.yy(size: 11, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 24, height: 24)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("关闭"))
            }
            .padding(.horizontal, 12)
            .padding(.top, 8)
            .padding(.bottom, 6)

            Rectangle().fill(YYColor.line).frame(height: 0.5)

            ViewThatFits(in: .vertical) {
                readyLinesList
                ScrollView {
                    readyLinesList
                }
                .scrollIndicators(.never)
                .scrollBounceBehavior(.basedOnSize)
            }
            .frame(maxHeight: 220)
        }
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
        .shadow(color: .black.opacity(scheme == .dark ? 0.3 : 0.1), radius: 14, y: 6)
    }

    private var slashList: some View {
        VStack(alignment: .leading, spacing: 2) {
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
                                    .fixedSize()
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

    private var slashCard: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text("指令")
                    .font(.yy(size: 11.5, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 4)
                Button {
                    if draft == "/" || draft == "／" { draft = "" }
                    dismissSlashMenu()
                } label: {
                    Image(systemName: "xmark")
                        .font(.yy(size: 11, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                        .frame(width: 24, height: 24)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("关闭"))
            }
            .padding(.horizontal, 12)
            .padding(.top, 8)
            .padding(.bottom, 6)

            Rectangle().fill(YYColor.line).frame(height: 0.5)

            ViewThatFits(in: .vertical) {
                slashList
                ScrollView {
                    slashList
                }
                .scrollIndicators(.never)
                .scrollBounceBehavior(.basedOnSize)
            }
            .frame(maxHeight: 220)
        }
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 14, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
        .shadow(color: .black.opacity(scheme == .dark ? 0.3 : 0.1), radius: 14, y: 6)
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
        let _ = focus.tick
        let _ = activeProviderID
        let config = CompanionConfig.current
        let unconfigured = config.isUnconfiguredFallback
        let isApple = config.provider.kind == .apple
        let modelTitle: String = if unconfigured {
            String(localized: "配置模型")
        } else if isApple {
            String(localized: "Apple 智能")
        } else if config.model.isEmpty {
            String(localized: "选择模型…")
        } else {
            ModelNames.name(config.model)
        }
        let empty = !canSubmitDraft

        return VStack(alignment: .leading, spacing: 8) {
            TextField(
                "",
                text: $draft,
                prompt: Text(companion.turns.isEmpty ? String(localized: "问这本书，或输入 / …") : String(localized: "继续问，或输入 / …")),
                axis: .vertical
            )
            .font(.yy(size: 15.5))
            .lineLimit(1 ... 5)
            .focused($typing)
            .padding(.horizontal, 2)

            HStack(spacing: 6) {
                if !readyQuestions.isEmpty {
                    Button {
                        withAnimation(YYMotion.quick) { linesOpen.toggle() }
                    } label: {
                        Image(systemName: "quote.opening")
                            .font(.yy(size: 12.5, weight: .semibold))
                            .foregroundStyle(linesOpen ? YYColor.t1 : YYColor.t2)
                            .frame(width: 30, height: 30)
                            .background(linesOpen ? YYColor.inset : .clear, in: .circle)
                            .contentShape(.circle)
                    }
                    .buttonStyle(.plain)
                    .disabled(companion.busy)
                    .accessibilityLabel(Text("挑一句现成的"))
                }

                Button { toggleSlashMenu() } label: {
                    Text("/")
                        .font(.system(size: 14, weight: .semibold, design: .monospaced))
                        .foregroundStyle(slashMenuOpen ? YYColor.t1 : YYColor.t2)
                        .frame(width: 30, height: 30)
                        .background(slashMenuOpen ? YYColor.inset : .clear, in: .circle)
                        .contentShape(.circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("快捷指令"))

                Button {
                    typing = false
                    linesOpen = false
                    dismissSlashMenu()
                    if unconfigured {
                        settings = true
                    } else {
                        modelPickerOpen = true
                    }
                } label: {
                    HStack(spacing: 5) {
                        if unconfigured {
                            Image(systemName: "plus.circle.fill")
                                .font(.yy(size: 13.5, weight: .medium))
                                .foregroundStyle(tint.accent)
                        } else {
                            ServiceTile(mark: isApple ? .thisMac : .provider(config.provider), size: 17)
                        }
                        Text(modelTitle)
                            .font(.yy(size: 12, weight: .semibold))
                            .foregroundStyle(config.isReady ? YYColor.t1 : YYColor.t2)
                            .lineLimit(1)
                            .truncationMode(.tail)
                        Image(systemName: unconfigured ? "chevron.right" : "chevron.up.chevron.down")
                            .font(.yy(size: 9, weight: .semibold))
                            .foregroundStyle(YYColor.t3)
                    }
                    .padding(.leading, unconfigured ? 8 : 6)
                    .padding(.trailing, 9)
                    .frame(height: 30)
                    .background(YYColor.bg, in: .capsule)
                    .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    .contentShape(.capsule)
                }
                .buttonStyle(YYPressStyle(scale: 0.97))
                .layoutPriority(1)
                .accessibilityLabel(Text(unconfigured ? "配置模型" : "选择模型"))

                Spacer(minLength: 4)

                Button { submitDraft() } label: {
                    Image(systemName: "arrow.up")
                        .font(.yy(size: 13.5, weight: .bold))
                        .foregroundStyle(empty ? YYColor.t3 : tint.onBlack)
                        .frame(width: 30, height: 30)
                        .background(empty ? YYColor.inset : tint.black, in: .circle)
                        .animation(YYMotion.quick, value: empty)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("发送"))
                .allowsHitTesting(!empty)
            }
        }
        .padding(.horizontal, 12)
        .padding(.top, 10)
        .padding(.bottom, 8)
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 18, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 18, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func toggleSlashMenu() {
        linesOpen = false
        if slashMenuOpen {
            if draft == "/" || draft == "／" { draft = "" }
            dismissSlashMenu()
            return
        }
        slashDismissedForDraft = nil
        if draft.isEmpty {
            draft = "/"
        } else {
            slashManualOpen = true
        }
        typing = true
    }

    private func dismissSlashMenu() {
        slashManualOpen = false
        if CompanionCommand.slashQuery(in: draft) != nil {
            slashDismissedForDraft = draft
        }
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
        case .recap, .chapter, .characters, .explain, .quote, .quiz, .custom:
            guard !companion.busy else { return }
            if let q = command.question(argument: arg, hasQuote: model.companionQuote != nil, chapterTitle: model.chapterTitle) {
                slashManualOpen = false
                slashDismissedForDraft = nil
                send(q, action: command.action)
            }
        case .scope(let target):
            slashManualOpen = false
            slashDismissedForDraft = nil
            companion.scope = target
            draft = ""
            if !arg.isEmpty, !companion.busy {
                companion.ask(arg, quote: model.companionQuote, scope: target, anchor: model.companionAnchor)
                model.clearCompanionQuote()
            }
        case .model:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            typing = false
            if CompanionConfig.current.isUnconfiguredFallback {
                settings = true
            } else {
                modelPickerOpen = true
            }
        case .settings:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            typing = false
            settings = true
        case .clear:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            companion.clear()
        }
    }

    private func send(_ text: String, action: CompanionCommand.Action? = nil) {
        let q = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, !companion.busy else { return }
        companion.ask(q, quote: model.companionQuote, anchor: model.companionAnchor, action: action)
        draft = ""
        linesOpen = false
        slashManualOpen = false
        slashDismissedForDraft = nil
        model.clearCompanionQuote()
    }
}

/// iOS quick model switcher for 书伴: search across all configured providers, filter by
/// ★ 常用收藏 / 🕘 最近用过 / provider / family, star favorites, or submit a custom model ID.
private struct PhoneCompanionModelPicker: View {
    enum Nav: Hashable {
        case starred
        case recent
        case provider(String)
    }

    let openSettings: () -> Void
    let close: () -> Void

    @State private var nav: Nav = .starred
    @State private var query = ""
    @State private var selectedFamily: String?
    @State private var livePools: [String: [String]] = [:]
    @State private var loadingProvider: String?
    @State private var starTick = 0
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var focus: CompanionFocus { .shared }

    private var allProviders: [CompanionProvider] {
        var out: [CompanionProvider] = Companion.isAppleSupported ? [.apple] : []
        var seen: Set<String> = Set(out.map(\.id))
        for p in CompanionProvider.added where seen.insert(p.id).inserted {
            out.append(p)
        }
        return out
    }

    private var readyProviders: [CompanionProvider] {
        allProviders.filter { CompanionConfig.saved($0).isConnected }
    }

    private var trimmedQuery: String {
        query.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    var body: some View {
        let _ = focus.tick
        let _ = starTick
        let current = CompanionConfig.current
        let starred = ModelNames.starredEntries(among: readyProviders)
        let recents = ModelNames.globalRecents(among: readyProviders)

        NavigationStack {
            VStack(spacing: 0) {
                searchBar
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    .padding(.bottom, 10)

                if trimmedQuery.isEmpty {
                    providerStrip(starredCount: starred.count, recentCount: recents.count)
                        .padding(.bottom, 8)
                }

                Rectangle().fill(YYColor.line).frame(height: 0.5)

                modelListPane(current: current, starred: starred, recents: recents)
            }
            .background(YYColor.bg)
            .navigationTitle("选择模型")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarLeading) {
                    Button("书伴设置") { openSettings() }
                }
                ToolbarItem(placement: .confirmationAction) {
                    Button("完成") { close() }
                }
            }
        }
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
        .presentationBackground(YYColor.bg)
        .onAppear {
            let initialStarred = ModelNames.starredEntries(among: readyProviders)
            if !initialStarred.isEmpty && current.isReady {
                nav = .starred
            } else if allProviders.contains(where: { $0.id == current.provider.id }) {
                nav = .provider(current.provider.id)
            } else if let first = allProviders.first {
                nav = .provider(first.id)
            } else {
                nav = .starred
            }
            Task { await warmPools() }
        }
        .onChange(of: nav) { _, next in
            selectedFamily = nil
            if case .provider(let pid) = next,
               let p = allProviders.first(where: { $0.id == pid }),
               p.kind != .apple,
               CompanionConfig.saved(p).isConnected {
                Task { await fetchModels(for: p, force: false) }
            }
        }
    }

    private var searchBar: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 14, weight: .medium))
                .foregroundStyle(YYColor.t3)
            TextField("", text: $query, prompt: Text("搜模型名、ID 或缩写（如 co46 / dsr1）…"))
                .font(.yy(size: 14.5))
                .foregroundStyle(YYColor.t1)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .submitLabel(.done)
                .onSubmit { submitSearch() }
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.yy(size: 15))
                        .foregroundStyle(YYColor.t3)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 38)
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func providerStrip(starredCount: Int, recentCount: Int) -> some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                navChip(target: .starred, title: String(localized: "收藏"), count: starredCount > 0 ? "\(starredCount)" : nil) {
                    Image(systemName: "star.fill")
                        .font(.yy(size: 11))
                        .foregroundStyle(Color(red: 0.85, green: 0.57, blue: 0.15))
                }
                navChip(target: .recent, title: String(localized: "最近"), count: recentCount > 0 ? "\(recentCount)" : nil) {
                    Image(systemName: "clock")
                        .font(.yy(size: 11, weight: .medium))
                        .foregroundStyle(YYColor.t2)
                }
                ForEach(allProviders) { p in
                    let cfg = CompanionConfig.saved(p)
                    navChip(
                        target: .provider(p.id),
                        title: p.title,
                        count: cfg.isConnected ? (p.kind == .apple ? nil : "\(pool(for: p).count)") : String(localized: "未配"),
                        dimmed: !cfg.isConnected
                    ) {
                        ServiceTile(mark: p.kind == .apple ? .thisMac : .provider(p), size: 17)
                    }
                }
            }
            .padding(.horizontal, 16)
        }
    }

    private func navChip<Icon: View>(
        target: Nav,
        title: String,
        count: String?,
        dimmed: Bool = false,
        @ViewBuilder icon: () -> Icon
    ) -> some View {
        let selected = nav == target
        return Button {
            withAnimation(YYMotion.quick) { nav = target }
        } label: {
            HStack(spacing: 5) {
                icon()
                Text(title)
                    .font(.yy(size: 12.5, weight: selected ? .semibold : .medium))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                if let count {
                    Text(count)
                        .font(.yy(size: 10.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
            }
            .opacity(dimmed ? 0.55 : 1)
            .padding(.horizontal, 10)
            .frame(height: 32)
            .background(selected ? YYColor.card : YYColor.inset, in: .capsule)
            .overlay(Capsule().strokeBorder(selected ? tint.accent.opacity(0.45) : YYColor.line, lineWidth: selected ? 1 : 0.5))
        }
        .buttonStyle(.plain)
    }

    @ViewBuilder
    private func modelListPane(
        current: CompanionConfig,
        starred: [ModelNames.Entry],
        recents: [ModelNames.Entry]
    ) -> some View {
        if allProviders.isEmpty {
            VStack(spacing: 12) {
                Image(systemName: "sparkles")
                    .font(.yy(size: 22, weight: .medium))
                    .foregroundStyle(tint.accent)
                    .frame(width: 44, height: 44)
                    .background(tint.accentSoft, in: .rect(cornerRadius: 12))
                Text("还没添加 AI 服务商")
                    .font(.yy(size: 15, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                Text("支持 DeepSeek、通义千问、Claude、OpenAI、Ollama 等")
                    .font(.yy(size: 13))
                    .foregroundStyle(YYColor.t3)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 28)
                Button(action: openSettings) {
                    HStack(spacing: 5) {
                        Text("去配置模型")
                        Image(systemName: "chevron.right").font(.yy(size: 10.5, weight: .semibold))
                    }
                    .font(.yy(size: 13.5, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                    .padding(.horizontal, 16)
                    .frame(height: 36)
                    .background(YYColor.card, in: .capsule)
                    .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                }
                .buttonStyle(.plain)
                .padding(.top, 2)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else if trimmedQuery.isEmpty, case .provider(let pid) = nav,
           let p = allProviders.first(where: { $0.id == pid }),
           !CompanionConfig.saved(p).isConnected {
            VStack(spacing: 12) {
                ServiceTile(mark: p.kind == .apple ? .thisMac : .provider(p), size: 36)
                Text(p.kind == .apple
                     ? (Companion.appleAvailability ?? String(localized: "Apple 智能暂不可用"))
                     : String(localized: "还没填好 \(p.title) 的 Key"))
                    .font(.yy(size: 13.5))
                    .foregroundStyle(YYColor.t2)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 28)
                Button("去设置 · 书伴") { openSettings() }
                    .buttonStyle(YYButtonStyle(kind: .main, size: .small))
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            let state = resolvedEntries(starred: starred, recents: recents)
            VStack(spacing: 0) {
                if state.families.count > 1 || state.refreshTarget != nil {
                    HStack(spacing: 8) {
                        if state.families.count > 1 {
                            ScrollView(.horizontal, showsIndicators: false) {
                                HStack(spacing: 6) {
                                    familyChip(label: String(localized: "全部"), value: nil)
                                    ForEach(state.families, id: \.self) { fam in
                                        familyChip(label: fam, value: fam)
                                    }
                                }
                            }
                        } else {
                            Text(state.refreshTarget?.title ?? "")
                                .font(.yy(size: 12, weight: .semibold))
                                .foregroundStyle(YYColor.t2)
                            Spacer(minLength: 0)
                        }
                        if let p = state.refreshTarget, p.kind != .apple {
                            Button { Task { await fetchModels(for: p, force: true) } } label: {
                                Group {
                                    if loadingProvider == p.id {
                                        ProgressView().controlSize(.small)
                                    } else {
                                        Image(systemName: "arrow.clockwise")
                                            .font(.yy(size: 13, weight: .medium))
                                            .foregroundStyle(YYColor.t2)
                                    }
                                }
                                .frame(width: 28, height: 28)
                            }
                            .buttonStyle(.plain)
                            .disabled(loadingProvider == p.id)
                            .accessibilityLabel(Text("刷新模型列表"))
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 8)
                    .overlay(alignment: .bottom) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                }

                ScrollView {
                    LazyVStack(spacing: 6) {
                        ForEach(state.entries) { entry in
                            modelRow(
                                entry: entry,
                                showProvider: state.showProvider,
                                isActive: isEntryActive(entry, current: current),
                                isStarred: ModelNames.isStarred(provider: entry.provider.id, model: entry.model)
                            )
                        }

                        if let customTarget = customIDTargetProvider,
                           !trimmedQuery.isEmpty,
                           !state.entries.contains(where: { $0.provider.id == customTarget.id && $0.model == trimmedQuery }) {
                            Button {
                                select(ModelNames.Entry(provider: customTarget, model: trimmedQuery))
                            } label: {
                                HStack(spacing: 8) {
                                    ServiceTile(mark: .provider(customTarget), size: 18)
                                    (Text(String(localized: "在 \(customTarget.title) 使用 ID："))
                                        .foregroundStyle(YYColor.t2)
                                     + Text(trimmedQuery)
                                        .font(.yy(size: 13, weight: .semibold, design: .monospaced))
                                        .foregroundStyle(tint.accent))
                                        .font(.yy(size: 13))
                                        .lineLimit(1)
                                    Spacer(minLength: 4)
                                    Image(systemName: "return")
                                        .font(.yy(size: 12, weight: .semibold))
                                        .foregroundStyle(tint.accent)
                                }
                                .padding(.horizontal, 12)
                                .padding(.vertical, 12)
                                .background(
                                    RoundedRectangle(cornerRadius: 12, style: .continuous)
                                        .strokeBorder(YYColor.line, style: StrokeStyle(lineWidth: 0.9, dash: [4, 3]))
                                )
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .padding(.top, 4)
                        }

                        if state.entries.isEmpty && trimmedQuery.isEmpty {
                            VStack(spacing: 8) {
                                Image(systemName: nav == .starred ? "star" : "clock")
                                    .font(.yy(size: 24, weight: .light))
                                    .foregroundStyle(YYColor.t3)
                                Text(nav == .starred
                                     ? String(localized: "点模型右侧的 ★ 把常用模型收进这里")
                                     : String(localized: "切换过的模型会出现在这里"))
                                    .font(.yy(size: 13))
                                    .foregroundStyle(YYColor.t3)
                                    .multilineTextAlignment(.center)
                            }
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 64)
                        }
                    }
                    .padding(.horizontal, 14)
                    .padding(.vertical, 10)
                }
                .scrollDismissesKeyboard(.interactively)
            }
        }
    }

    private func modelRow(
        entry: ModelNames.Entry,
        showProvider: Bool,
        isActive: Bool,
        isStarred: Bool
    ) -> some View {
        let isApple = entry.provider.kind == .apple
        let displayName = isApple ? String(localized: "Apple 智能（本机）") : ModelNames.name(entry.model)
        let route = isApple ? nil : ModelNames.routeBadge(entry.model)
        let blurb = ModelNames.blurb(for: entry.model, providerID: entry.provider.id)
        let subtitle: String = {
            var parts: [String] = []
            if showProvider { parts.append(entry.provider.title) }
            if let blurb {
                parts.append(blurb)
            } else if !isApple, entry.model != displayName {
                parts.append(entry.model)
            }
            return parts.joined(separator: " · ")
        }()

        return HStack(spacing: 6) {
            Button { select(entry) } label: {
                HStack(spacing: 10) {
                    if showProvider {
                        ServiceTile(mark: isApple ? .thisMac : .provider(entry.provider), size: 22)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(displayName)
                                .font(.yy(size: 14, weight: .semibold))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            if let route {
                                Text(route)
                                    .font(.yy(size: 10.5, weight: .medium, design: .monospaced))
                                    .foregroundStyle(YYColor.t2)
                                    .padding(.horizontal, 5)
                                    .padding(.vertical, 1.5)
                                    .background(YYColor.inset, in: .rect(cornerRadius: 4))
                                    .fixedSize()
                            }
                        }
                        if !subtitle.isEmpty {
                            Text(subtitle)
                                .font(.yy(size: 11.5))
                                .foregroundStyle(YYColor.t3)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)

            Button {
                ModelNames.toggleStar(provider: entry.provider.id, model: entry.model)
                starTick &+= 1
            } label: {
                Image(systemName: isStarred ? "star.fill" : "star")
                    .font(.yy(size: 13.5, weight: .medium))
                    .foregroundStyle(isStarred ? Color(red: 0.85, green: 0.57, blue: 0.15) : YYColor.t3.opacity(0.65))
                    .frame(width: 32, height: 32)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .accessibilityLabel(Text(isStarred ? "移出常用收藏" : "加入常用收藏"))

            if isActive {
                Image(systemName: "checkmark")
                    .font(.yy(size: 12.5, weight: .bold))
                    .foregroundStyle(tint.accent)
                    .frame(width: 18)
            }
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
        .background(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .fill(isActive ? tint.accentSoft.opacity(0.55) : YYColor.card)
        )
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .strokeBorder(isActive ? tint.accent.opacity(0.35) : YYColor.line, lineWidth: 0.5)
        )
    }

    private func familyChip(label: String, value: String?) -> some View {
        let on = selectedFamily == value
        return Button { selectedFamily = value } label: {
            Text(label)
                .font(.yy(size: 12, weight: on ? .semibold : .regular))
                .foregroundStyle(on ? tint.accent : YYColor.t2)
                .padding(.horizontal, 10)
                .frame(height: 26)
                .background(on ? tint.accentSoft : YYColor.inset, in: .capsule)
        }
        .buttonStyle(.plain)
    }

    private func pool(for provider: CompanionProvider) -> [String] {
        if let live = livePools[provider.id], !live.isEmpty {
            let saved = CompanionConfig.saved(provider).model
            if !saved.isEmpty && !live.contains(saved) {
                return [saved] + live
            }
            return live
        }
        return ModelNames.availablePool(for: provider)
    }

    private var customIDTargetProvider: CompanionProvider? {
        if case .provider(let pid) = nav,
           let p = readyProviders.first(where: { $0.id == pid && $0.kind != .apple }) {
            return p
        }
        let cur = CompanionConfig.current.provider
        if cur.kind != .apple, CompanionConfig.saved(cur).isConnected { return cur }
        return readyProviders.first(where: { $0.kind != .apple })
    }

    private func resolvedEntries(
        starred: [ModelNames.Entry],
        recents: [ModelNames.Entry]
    ) -> (entries: [ModelNames.Entry], families: [String], showProvider: Bool, refreshTarget: CompanionProvider?) {
        if !trimmedQuery.isEmpty {
            var hits: [ModelNames.Entry] = []
            for p in readyProviders {
                for m in pool(for: p) where ModelNames.matches(query: trimmedQuery, id: m, providerTitle: p.title) {
                    hits.append(ModelNames.Entry(provider: p, model: m))
                }
            }
            return (hits, [], true, nil)
        }
        switch nav {
        case .starred:
            return (starred, [], true, nil)
        case .recent:
            return (recents, [], true, nil)
        case .provider(let pid):
            guard let p = readyProviders.first(where: { $0.id == pid }) else {
                return ([], [], false, nil)
            }
            let models = pool(for: p)
            var seenFam = Set<String>()
            var fams: [String] = []
            for m in models {
                let f = ModelNames.family(of: m)
                if seenFam.insert(f).inserted { fams.append(f) }
            }
            let filtered = models.filter { selectedFamily == nil || ModelNames.family(of: $0) == selectedFamily }
            return (filtered.map { ModelNames.Entry(provider: p, model: $0) },
                    fams.count > 1 ? fams : [],
                    false,
                    p.kind == .apple ? nil : p)
        }
    }

    private func isEntryActive(_ entry: ModelNames.Entry, current: CompanionConfig) -> Bool {
        guard current.provider.id == entry.provider.id else { return false }
        if entry.provider.kind == .apple { return true }
        return current.model == entry.model
    }

    private func submitSearch() {
        guard !trimmedQuery.isEmpty else { return }
        let starred = ModelNames.starredEntries(among: readyProviders)
        let recents = ModelNames.globalRecents(among: readyProviders)
        let hits = resolvedEntries(starred: starred, recents: recents).entries
        if let first = hits.first {
            select(first)
        } else if let target = customIDTargetProvider {
            select(ModelNames.Entry(provider: target, model: trimmedQuery))
        }
    }

    private func select(_ entry: ModelNames.Entry) {
        let prev = CompanionConfig.current
        if prev.isReady {
            let prevModel = prev.provider.kind == .apple ? "apple-on-device" : prev.model
            ModelNames.recordGlobalRecent(provider: prev.provider.id, model: prevModel)
        }

        if entry.provider.kind == .apple {
            UserDefaults.standard.set(CompanionProvider.apple.id, forKey: "companion.provider")
            ModelNames.recordGlobalRecent(provider: CompanionProvider.apple.id, model: entry.model)
        } else {
            var next = CompanionConfig.saved(entry.provider)
            if !next.model.isEmpty && next.model != entry.model {
                ModelNames.used(next.model, provider: entry.provider.id)
            }
            next.model = entry.model
            CompanionConfig.current = next
            ModelNames.recordGlobalRecent(provider: entry.provider.id, model: entry.model)
        }
        focus.reload()
        close()
    }

    private func warmPools() async {
        for p in readyProviders where p.kind != .apple && ModelNames.cached(p.id).isEmpty {
            await fetchModels(for: p, force: false)
        }
    }

    private func fetchModels(for provider: CompanionProvider, force: Bool) async {
        guard provider.kind != .apple else { return }
        if !force, livePools[provider.id] != nil { return }
        let cfg = CompanionConfig.saved(provider)
        guard cfg.isConnected else { return }
        loadingProvider = provider.id
        defer { if loadingProvider == provider.id { loadingProvider = nil } }
        if let fetched = try? await Companion.models(cfg), !fetched.isEmpty {
            var ordered: [String] = []
            var seen = Set<String>()
            for r in ModelNames.resolvedRecommendations(for: provider.id, available: fetched) where seen.insert(r.id).inserted {
                ordered.append(r.id)
            }
            for m in fetched.sorted() where seen.insert(m).inserted {
                ordered.append(m)
            }
            livePools[provider.id] = ordered
        }
    }
}
#endif
