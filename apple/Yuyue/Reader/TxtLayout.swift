import SwiftUI
import YuyueCore

/// 编码和目录, for a TXT: which encoding it is read in, with the first lines to
/// see whether they read, and the rule that finds its chapters, with what it found. A chapter far
/// longer than the others is pointed out. 完成 builds the book again the chosen way.
struct TxtLayoutView: View {
    let library: Library
    let bookID: String
    let bookTitle: String
    /// Opens the reader at a section; none outside the reader.
    var go: ((UInt32) -> Void)?
    let done: () -> Void

    @State private var applied = TxtChoice(encoding: nil, rule: nil)
    @State private var choice = TxtChoice(encoding: nil, rule: nil)
    @State private var rule = ""
    @State private var layout: TxtLayout?
    @State private var working = false
    @State private var failure: String?
    /// Why the preview could not be read. Distinct from a rule that doesn't compile (`layout.error`).
    @State private var readFailure: String?
    @State private var loaded = false
    /// All chapters, opened from 全部 N 章. Nil is the encoding page.
    @State private var all: TxtLayout?
    /// Header + rows + footer, so the Mac window is the paper and not the rows alone.
    @State private var paper: CGFloat = 0

    private static let encodings: [(String?, String)] = [(nil, String(localized: "自动")), ("UTF-8", "UTF-8"), ("GB18030", "GB18030"), ("Big5", "Big5")]
    /// Chapters listed before the rest are folded away.
    private static let shownChapters = 3

    private var core: YuyueCore { library.core }
    private var changed: Bool { choice != applied }

    var body: some View {
        VStack(spacing: 0) {
            YYSheetHeader(
                title: all == nil ? String(localized: "编码和目录") : String(localized: "\(all!.chapters.count) 章"),
                subtitle: all == nil ? fileLine : nil,
                back: all == nil ? nil : { all = nil },
                // Not while the book is being laid out again: half done is worse than either.
                close: { if !working { done() } }
            )
            .padding(.leading, YYUI.s(20))
            .padding(.trailing, YYUI.s(16))
            .padding(.top, YYUI.s(16))
            .padding(.bottom, YYUI.s(4))
            .yySheetBlock()
            sheetBody
            if all == nil {
                YYSheetFooter(
                    meta: String(localized: "自动识别还认得序章、楔子、Chapter 1"),
                    secondaryTitle: String(localized: "取消"),
                    onSecondary: done,
                    secondaryDisabled: working,
                    primaryTitle: String(localized: "保存"),
                    primaryHint: "↩",
                    onPrimary: finish,
                    primaryDisabled: working || layout?.error != nil
                )
                .yySheetBlock()
            }
        }
        .background(YYColor.bg)
        .interactiveDismissDisabled(working)
        .toolbar(.hidden, for: .automatic)
        #if os(macOS)
        .frame(width: YYUI.s(460))
        .fixedSize(horizontal: true, vertical: true)
        #endif
        .paperWindow()
        .onPreferenceChange(YYSheetMeasure.self) { paper = $0 }
        #if os(macOS)
        .background(MacPaperSize(width: YYUI.s(460), height: paper))
        #endif
        .overlay {
            if working {
                ProgressView(String(localized: "正在按新的方式整理这本书…"))
                    .padding(20)
                    .yyGlass(RoundedRectangle(cornerRadius: 14), fill: .clear, shadow: 0)
                    .transition(.opacity)
            }
        }
        .animation(YYMotion.quick, value: working)
        .task(id: choice) { await refresh() }
        .onAppear(perform: load)
        .yyNotice(String(localized: "没能改好"), message: failure ?? "",
                  isPresented: Binding(get: { failure != nil }, set: { if !$0 { failure = nil } }))
    }

    /// The rows. On the Mac the scroll is only as tall as they are, so 取消 / 改好了
    /// sit under the chapters. A pushed page on the phone still fills.
    @ViewBuilder private var sheetBody: some View {
        let rows = VStack(alignment: .leading, spacing: 0) {
            if let all {
                YYGroup {
                    ForEach(Array(all.chapters.enumerated()), id: \.offset) { i, c in
                        chapterRow(c, long: all.long.map(Int.init) == i)
                    }
                }
                .padding(.top, YYUI.s(12))
            } else {
                encodingPart
                chapterPart
                if let layout, let long = layout.long.map(Int.init), layout.chapters.indices.contains(long) {
                    warning(layout, long)
                } else if let doubt = layout?.doubt, layout?.unreadable == 0 {
                    Label(doubt, systemImage: "exclamationmark.triangle.fill")
                        .font(YYUI.font(11)).foregroundStyle(YYColor.warn)
                        .padding(.top, YYUI.s(10))
                }
            }
        }
        #if os(macOS)
        YYFitScroll(maxHeight: YYUI.s(520)) {
            rows
                .padding(.horizontal, YYUI.s(20))
                .padding(.bottom, YYUI.s(4))
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        #else
        YYPage { rows }
        #endif
    }

    private var fileLine: String {
        let size = layout.map { ByteCountFormatter.string(fromByteCount: Int64($0.size), countStyle: .file) }
        return ["\(bookTitle).txt", size].compactMap { $0 }.joined(separator: " · ")
    }

    // MARK: 编码

    @ViewBuilder private var encodingPart: some View {
        sheetLabel(String(localized: "编码"), layout.map { String(localized: "自动识别为 \($0.detected)") })
        EncodingChoices(
            items: Self.encodings.map { value, name in
                (value, value == nil ? [String(localized: "自动"), layout?.detected].compactMap { $0 }.joined(separator: " · ") : name)
            },
            selection: $choice.encoding
        )
        previewCard
        if let n = layout?.unreadable, n > 0 {
            Label("有 \(n) 处字认不出来，换一种编码试试", systemImage: "exclamationmark.triangle.fill")
                .font(YYUI.font(10.5)).foregroundStyle(YYColor.danger)
                .padding(.top, YYUI.s(8))
                .padding(.horizontal, YYUI.s(4))
        }
    }

    /// The first chapter's title and lines, in the encoding chosen.
    private var previewCard: some View {
        VStack(alignment: .leading, spacing: YYUI.s(4)) {
            if let layout, !layout.preview.isEmpty {
                if let title = layout.previewTitle {
                    Text(title).font(YYUI.font(12.5, .bold, design: .serif))
                }
                if let first = layout.preview.first {
                    Text("\u{3000}\u{3000}" + first)
                        .font(YYUI.font(12.5, design: .serif))
                        .lineSpacing(YYUI.s(12.5) * 0.9)
                        .lineLimit(2)
                }
            } else {
                Text(readFailure ?? (layout?.error == nil ? String(localized: "正在读…") : String(localized: "这种编码读不出字")))
                    .font(YYUI.font(12.5)).foregroundStyle(YYColor.t3)
            }
        }
        .foregroundStyle(YYColor.t1)
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.vertical, YYUI.s(12))
        .padding(.horizontal, YYUI.s(14))
        .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
        .padding(.top, YYUI.s(10))
    }

    /// Grey section name, with the quiet note on the right. The dialog sheet's labels, not the page's.
    private func sheetLabel(_ title: String, _ trailing: String?) -> some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title).font(YYUI.font(12, .semibold)).foregroundStyle(YYColor.t2)
            Spacer(minLength: YYUI.s(8))
            if let trailing { Text(trailing).font(YYUI.font(12)).foregroundStyle(YYColor.t3).lineLimit(1) }
        }
        .padding(.top, YYUI.s(12))
        .padding(.bottom, YYUI.s(6))
        .padding(.horizontal, YYUI.s(2))
    }

    // MARK: 目录

    @ViewBuilder private var chapterPart: some View {
        sheetLabel(String(localized: "目录"), layout.flatMap { $0.error == nil ? String(localized: "识别出 \($0.chapters.count) 章") : nil })
        YYGroup {
            YYField(label: String(localized: "规则"), labelWidth: 36) { ruleField }
            if let error = layout?.error {
                Text(error).font(YYUI.font(10.5)).foregroundStyle(YYColor.danger).frame(minHeight: YYUI.s(34))
            } else if let layout {
                let chapters = layout.chapters
                let long = layout.long.map(Int.init)
                ForEach(Array(chapters.prefix(Self.shownChapters).enumerated()), id: \.offset) { i, c in
                    chapterRow(c, long: i == long)
                }
                if chapters.count > Self.shownChapters {
                    if let long, long >= Self.shownChapters {
                        Button { all = layout } label: {
                            Text("⋯").font(YYUI.font(11)).kerning(YYUI.s(2)).foregroundStyle(YYColor.t3)
                                .frame(maxWidth: .infinity).frame(height: YYUI.s(22)).contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel("全部 \(chapters.count) 章")
                        chapterRow(chapters[long], long: true)
                    } else {
                        Button { all = layout } label: {
                            YYRow(String(localized: "全部 \(chapters.count) 章"), chevron: true)
                        }
                        .buttonStyle(.plain)
                    }
                }
            }
        }
        if choice.rule != nil {
            Button("用回自动识别") {
                rule = ""
                choice.rule = nil
            }
            .font(YYUI.font(10.5, .medium))
            .buttonStyle(.plain)
            .foregroundStyle(YYTint.standard.accent)
            .padding(.top, YYUI.s(6))
            .padding(.horizontal, YYUI.s(4))
        }
    }

    private var ruleField: some View {
        TextField("规则", text: $rule, prompt: Text(layout?.defaultRule ?? "").foregroundStyle(YYColor.t2))
            .textFieldStyle(.plain)
            .font(YYUI.font(10.5, design: .monospaced))
            .autocorrectionDisabled()
            #if os(iOS)
            .textInputAutocapitalization(.never)
            #endif
            .onSubmit { choice.rule = rule.isEmpty ? nil : rule }
            .onChange(of: rule) { _, new in
                // Typed rules are tried once the typing pauses; see `refresh`.
                choice.rule = new.trimmingCharacters(in: .whitespaces).isEmpty ? nil : new
            }
    }

    /// A chapter and how long it is, the one too long in the warning colour.
    private func chapterRow(_ c: TxtChapter, long: Bool) -> some View {
        HStack(spacing: YYUI.s(8)) {
            Text(c.title).font(YYUI.font(12.5, .semibold)).lineLimit(1).foregroundStyle(YYColor.t1)
            Spacer()
            Text("\(c.chars.formatted()) 字")
                .font(YYUI.font(11, long ? .semibold : .regular).monospacedDigit())
                .foregroundStyle(long ? YYColor.warn : YYColor.t3)
        }
        .frame(height: YYUI.s(34))
    }

    // MARK: 长得不正常的一章

    /// Which two chapters a long stretch lies between, how long the others are, and
    /// the way there.
    private func warning(_ layout: TxtLayout, _ long: Int) -> some View {
        let c = layout.chapters[long]
        // Chapter numbers as the reader counts them: the untitled start isn't one.
        let n = long + (layout.chapters.first?.title == String(localized: "开头") ? 0 : 1)
        let hasNext = layout.chapters.indices.contains(long + 1)
        let sizes = layout.chapters.map(\.chars).sorted()
        let usual = sizes[sizes.count / 2]
        return HStack(alignment: .top, spacing: YYUI.s(9)) {
            Image(systemName: "exclamationmark.triangle.fill").font(YYUI.font(13)).foregroundStyle(YYColor.warn)
            VStack(alignment: .leading, spacing: YYUI.s(2)) {
                Text(hasNext ? String(localized: "第 \(n) 和 \(n + 1) 章之间有 \(wan(c.chars))字，可能漏了一章") : String(localized: "第 \(n) 章之后有 \(wan(c.chars))字，可能漏了几章"))
                    .font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t1)
                Text("别的章都在 \(roughly(usual))字上下，多半是这里有一章的标题写法不一样，没认出来。")
                    .font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    .fixedSize(horizontal: false, vertical: true)
                if let go, !changed {
                    Button("去第 \(n) 章看看") { go(c.section) }
                        .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                        .padding(.top, YYUI.s(7))
                } else if go != nil {
                    Text("点完成后可以去这一章看看").font(YYUI.font(10)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(4))
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(.top, YYUI.s(11))
        .padding(.bottom, YYUI.s(12))
        .padding(.horizontal, YYUI.s(12))
        .background(YYColor.warn.opacity(0.11), in: .rect(cornerRadius: YYUI.s(14)))
        .padding(.top, YYUI.s(10))
    }

    /// 31,240 → "3 万", 15,600 → "1.6 万", 9,794 → "1 万".
    private func wan(_ n: UInt32) -> String {
        let w = Double(n) / 10_000
        let tenths = Int((w * 10).rounded())
        return w >= 2 || tenths % 10 == 0 ? String(localized: "\(Int(w.rounded())) 万") : String(localized: "\(tenths / 10).\(tenths % 10) 万")
    }

    /// 3,105 → "3 千", 820 → "800 ".
    private func roughly(_ n: UInt32) -> String {
        if n >= 10_000 { return wan(n) }
        if n >= 1_000 { return String(localized: "\(Int((Double(n) / 1_000).rounded())) 千") }
        return "\(Int((Double(n) / 100).rounded()) * 100) "
    }

    // MARK: Work

    private func load() {
        guard !loaded else { return }
        loaded = true
        let now = (try? core.txtChoice(bookId: bookID)) ?? TxtChoice(encoding: nil, rule: nil)
        applied = now
        choice = now
        rule = now.rule ?? ""
    }

    private func refresh() async {
        // A rule being typed is tried once the typing pauses; a tapped encoding at once.
        if layout != nil { try? await Task.sleep(for: .milliseconds(250)) }
        guard !Task.isCancelled else { return }
        // On this actor. A detached task that hops back here never finishes, and the
        // preview stays on 正在读.
        do {
            let found = try core.txtLayout(bookId: bookID, choice: choice)
            guard !Task.isCancelled else { return }
            layout = found
            readFailure = nil
        } catch {
            guard !Task.isCancelled else { return }
            readFailure = Library.message(for: error)
        }
    }

    private func finish() {
        guard changed else { return done() }
        working = true
        let core = core, book = bookID, choice = choice
        // Open readers write where they are, in the old chapters, before the core moves it.
        NotificationCenter.default.post(name: .bookWillRebuild, object: book)
        Task {
            do {
                try await Self.applyChoice(core, book: book, choice: choice)
                working = false
                library.reload()
                NotificationCenter.default.post(name: .bookRebuilt, object: book)
                done()
            } catch {
                working = false
                library.reload()
                NotificationCenter.default.post(name: .bookRebuilt, object: book)
                failure = Library.message(for: error)
            }
        }
    }

    @concurrent nonisolated private static func applyChoice(_ core: YuyueCore, book: String, choice: TxtChoice) async throws {
        _ = try core.setTxtChoice(bookId: book, choice: choice)
    }
}

/// Encoding choices as the dialog sheet draws them: a pill track, the chosen one a theme-colour wash.
private struct EncodingChoices: View {
    let items: [(String?, String)]
    @Binding var selection: String?
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        HStack(spacing: 0) {
            ForEach(Array(items.enumerated()), id: \.offset) { _, item in
                let on = selection == item.0
                Button { selection = item.0 } label: {
                    Text(item.1)
                        .font(YYUI.font(12, on ? .semibold : .regular))
                        .foregroundStyle(on ? YYTint.standard.onBlack : YYColor.t2)
                        .lineLimit(1)
                        .minimumScaleFactor(0.75)
                        .frame(maxWidth: .infinity)
                        .frame(height: YYUI.s(26))
                        .background { if on { Capsule().fill(YYTint.standard.black) } }
                        .contentShape(Capsule())
                }
                .buttonStyle(.plain)
            }
        }
        .padding(3)
        .background { YYCapsuleLift(lift: .secondary(scheme: scheme, onPaper: false, hover: false)) }
    }
}
