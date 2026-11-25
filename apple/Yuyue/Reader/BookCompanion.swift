import Foundation
import NaturalLanguage
import YuyueCore

/// Selection or existing highlight carried along when opening 书伴 via 问, so an answer can be
/// saved straight back onto that exact span as a thought.
nonisolated struct CompanionAnchor: Equatable, Sendable {
    let markID: String?
    let section: UInt32
    let start: TextPoint
    let end: TextPoint
    let text: String
}

/// 与书对话: questions about the book open in this window. By default only the
/// part already read is handed to the model, so it can't give away what comes later; 问全书
/// lifts that. The sentences it gets are numbered, and the answer points back at them: 出处.
@Observable
final class BookCompanion {
    nonisolated enum Scope: String, Sendable {
        /// 只用读过的部分.
        case read
        /// 问全书.
        case whole
    }

    nonisolated enum QueryKind: Equatable, Sendable {
        /// 总结本章: feed the current chapter's text in full reading order.
        case chapter
        /// 前情回顾 / 人物关系 / 梳理大纲: broader book coverage and structured length.
        case broad
        /// Specific question on a line, character or detail.
        case focused
    }

    struct Source: Codable, Equatable, Sendable {
        let section: UInt32
        let sentence: UInt32
        /// 第 13 页, worked out when the answer came.
        let page: Int?
        /// 问全书 only: past where the reader is, 没读到.
        var later: Bool? = nil
    }

    struct Answer: Codable, Equatable {
        /// With [n] where it leans on source n.
        let text: String
        let sources: [Int: Source]
        let label: String
        /// The excerpts didn't have it: maybe later in the book.
        let notYet: Bool
        /// Two questions to ask next, under the answer.
        var next: [String]? = nil
    }

    struct Turn: Identifiable {
        let id = UUID()
        let question: String
        let quote: String?
        let scope: Scope
        let anchor: CompanionAnchor?
        let askedAt: Position
        var streaming: String? = nil
        var thinking: Bool = false
        var answer: Answer?
        var failed: String?
        var savedMarkID: String? = nil
    }

    var scope = Scope.read
    private(set) var turns: [Turn] = []
    /// The source being shown on the page.
    var shown: Source?

    @ObservationIgnored weak var model: ReaderModel?
    @ObservationIgnored private var asking: Task<Void, Never>?

    var busy: Bool { turns.last.map { $0.answer == nil && $0.failed == nil } ?? false }

    func clear() {
        asking?.cancel()
        turns = []
        shown = nil
        model?.pager.setSource(nil)
    }

    /// Asks, in `scope` (the current one when nil). Words chosen on the page go along as `quote`.
    func ask(
        _ question: String,
        quote: String? = nil,
        scope: Scope? = nil,
        anchor: CompanionAnchor? = nil,
        action: CompanionCommand.Action? = nil
    ) {
        let q = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, let model, !busy else { return }
        let scope = scope ?? self.scope
        let config = CompanionConfig.current
        let detail = model.detail
        let here = model.pager.position ?? (try? model.core.progress(bookId: model.bookID))?.position ?? Position(section: detail?.start ?? 0, sentence: 0)
        let resolvedAnchor = quote != nil ? (anchor ?? model.companionAnchor) : nil
        turns.append(Turn(question: q, quote: quote, scope: scope, anchor: resolvedAnchor, askedAt: here))
        let index = turns.count - 1
        guard config.isReady else {
            turns[index].failed = config.provider == .apple ? Companion.appleAvailability ?? CompanionError.notReady.errorDescription : CompanionError.notReady.errorDescription
            return
        }
        let core = model.core
        let book = model.bookID
        let kind = Self.classify(question: q, action: action)
        let structured = kind != .focused
        let earlier = turns.prefix(index).suffix(2).compactMap { t in t.answer.map { (t.question, $0.text) } }
        let placeTag = kind == .chapter ? "\(here.section):\(here.sentence / 15)" : (scope == .read ? "\(here.section)" : "")
        let cacheKey = [scope.rawValue, placeTag, quote ?? "", q].joined(separator: "|")
        if let cached = Self.cache(model)?[cacheKey] {
            turns[index].answer = cached
            return
        }
        let chapterSpan = Self.chapterSections(detail: detail, section: here.section)
        let startSection = Int(detail?.start ?? 0)
        asking = Task { [weak self, weak model] in
            let excerpts = await Self.excerpts(
                core: core,
                book: book,
                question: q + " " + (quote ?? ""),
                here: here,
                upTo: scope == .read ? here : nil,
                kind: kind,
                chapterSections: chapterSpan,
                bookStart: startSection
            )
            guard let model, !Task.isCancelled else { return }
            let title = detail?.summary.title ?? ""
            let percent = Int((model.fraction(section: here.section, sentence: Int(here.sentence)) * 100).rounded())
            let styleRule = structured
                ? "用\(Companion.replyLanguage)，条理清晰，可用简短分段或要点梳理关键脉络，紧扣原文，不说空话。"
                : "用\(Companion.replyLanguage)，口语，简短，一般三到五句话。"
            var system = "你是「语阅」里的书伴，陪读者读《\(title)》\(detail?.summary.author.map { "（\($0)）" } ?? "")。\n"
            switch scope {
            case .read:
                system += """
                读者现在读到全书的 \(percent)%。
                规则：
                1. 只根据下面给出的原文片段回答。不要用你知道的、这本书后面的情节，不要剧透。
                2. \(styleRule)
                3. 说到原文里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                4. 如果这些片段不够回答，或者答案在读者还没读到的地方，只回答三个字：没读到
                5. 能回答的话，最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                """
            case .whole:
                system += """
                读者想用整本书来回答，不介意提前知道后面的情节。
                规则：
                1. 可以用整本书的内容，也可以结合你对这本书的了解。
                2. \(styleRule)
                3. 说到下面原文片段里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                4. 最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                """
            }
            var prompt = ""
            if !earlier.isEmpty {
                prompt += "之前的对话：\n" + earlier.map { "读者：\($0.0)\n书伴：\($0.1)" }.joined(separator: "\n") + "\n\n"
            }
            prompt += kind == .chapter ? "当前章节原文片段（按阅读顺序）：\n" : "原文片段：\n"
            var prebuiltSources: [Int: Source] = [:]
            for (n, e) in excerpts.enumerated() {
                let chapter = detail?.chapterTitle(for: e.section).map { "（\($0)）" } ?? ""
                prompt += "[\(n + 1)]\(chapter)\(e.text)\n"
                let later = scope == .whole && (e.section, e.sentence) > (here.section, here.sentence + 8)
                prebuiltSources[n + 1] = Source(
                    section: e.section,
                    sentence: e.sentence,
                    page: model.estimatedPage(section: e.section, sentence: Int(e.sentence)),
                    later: later ? true : nil
                )
            }
            if let quote { prompt += "\n读者选中的句子：「\(quote)」\n" }
            prompt += "\n读者问：\(q)"
            do {
                let rawReply = try await Companion.answer(
                    config,
                    system: system,
                    prompt: prompt,
                    maxTokens: structured ? 2048 : 1024,
                    source: .companion,
                    onStream: { [weak self] raw in
                        let (preview, isThinking) = Self.streamingPreview(from: raw, hideNotYet: scope == .read)
                        Task { @MainActor [weak self] in
                            guard let self, self.turns.indices.contains(index), self.turns[index].answer == nil else { return }
                            self.turns[index].thinking = isThinking
                            if !preview.isEmpty {
                                self.turns[index].streaming = preview
                            }
                        }
                    }
                )
                let (text, next) = Self.split(rawReply)
                guard let self, !Task.isCancelled else { return }
                let notYet = scope == .read && text.hasPrefix("没读到")
                // Only the ones it pointed at.
                let used = Set(Self.citations(in: text))
                let answer = Answer(
                    text: text,
                    sources: prebuiltSources.filter { used.contains($0.key) },
                    label: config.label,
                    notYet: notYet,
                    next: notYet || next.isEmpty ? nil : next
                )
                if self.turns.indices.contains(index) {
                    self.turns[index].streaming = nil
                    self.turns[index].thinking = false
                    self.turns[index].answer = answer
                }
                Self.store(model, key: cacheKey, answer)
            } catch {
                guard let self, !Task.isCancelled, self.turns.indices.contains(index) else { return }
                self.turns[index].streaming = nil
                self.turns[index].thinking = false
                self.turns[index].failed = Companion.message(for: error)
            }
        }
    }

    /// 出处: the page goes there and the sentence is washed in the accent.
    func show(_ source: Source) {
        shown = source
        model?.jump(section: source.section, sentence: Int(source.sentence))
        model?.pager.setSource(Position(section: source.section, sentence: source.sentence))
    }

    /// Saves a completed answer onto the anchor highlight (or the cited / current sentence) as a thought.
    func saveThought(for turnID: UUID) {
        guard let index = turns.firstIndex(where: { $0.id == turnID }),
              let answer = turns[index].answer, !answer.notYet,
              let model else { return }
        if let savedID = turns[index].savedMarkID,
           let existing = model.marks.first(where: { $0.id == savedID }) {
            model.go(to: existing)
            return
        }
        let clean = Self.noteText(from: answer.text)
        guard !clean.isEmpty else { return }
        let turn = turns[index]

        // 1. Existing highlight tapped via 问: append/update its thought.
        if let markID = turn.anchor?.markID,
           let targetMark = model.marks.first(where: { $0.id == markID }) {
            let previousNote = targetMark.note
            let combined = Self.mergeNote(existing: previousNote, addition: clean)
            do {
                try model.core.setMarkNote(book: model.bookID, id: markID, note: combined)
                model.reloadNotes()
                turns[index].savedMarkID = markID
                model.show(Library.Toast(
                    icon: "square.and.pencil",
                    title: String(localized: "已存进想法"),
                    detail: "“\(targetMark.text.prefix(16))”",
                    undo: { [weak self, weak model] in
                        guard let model else { return }
                        try? model.core.setMarkNote(book: model.bookID, id: markID, note: previousNote)
                        model.reloadNotes()
                        if let i = self?.turns.firstIndex(where: { $0.id == turnID }) {
                            self?.turns[i].savedMarkID = nil
                        }
                    }
                ))
            } catch {
                model.failure = Library.message(for: error)
            }
            return
        }

        // 2. Fresh text selection handed via 问: highlight that exact span with the thought.
        if let anchor = turn.anchor, !anchor.text.isEmpty {
            do {
                let created = try model.core.addMark(
                    book: model.bookID,
                    section: anchor.section,
                    start: anchor.start,
                    end: anchor.end,
                    text: anchor.text,
                    style: model.lastStyle,
                    note: clean
                )
                model.pager.keepShown(created.id)
                model.reloadNotes()
                turns[index].savedMarkID = created.id
                model.show(Library.Toast(
                    icon: "square.and.pencil",
                    title: String(localized: "已存进想法"),
                    detail: "“\(created.text.prefix(16))”",
                    undo: { [weak self, weak model] in
                        guard let model else { return }
                        try? model.core.deleteMark(book: model.bookID, id: created.id)
                        model.reloadNotes()
                        if let i = self?.turns.firstIndex(where: { $0.id == turnID }) {
                            self?.turns[i].savedMarkID = nil
                        }
                    }
                ))
            } catch {
                model.failure = Library.message(for: error)
            }
            return
        }

        // 3. No selection quote: anchor to the first cited sentence, or the sentence where the question was asked.
        let firstCited = answer.sources.keys.sorted().first.flatMap { answer.sources[$0] }
        let targetSection = firstCited?.section ?? turn.askedAt.section
        let targetSentence = Int(firstCited?.sentence ?? turn.askedAt.sentence)
        guard let all = try? model.core.sentences(bookId: model.bookID, section: targetSection), !all.isEmpty else { return }
        let clamped = min(max(0, targetSentence), all.count - 1)
        let chosenIndex = (clamped ..< all.count).first { !all[$0].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            ?? (0 ..< clamped).reversed().first { !all[$0].trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
            ?? clamped
        let raw = all[chosenIndex]
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, let range = raw.range(of: trimmed) else { return }
        let start = TextPoint(sentence: UInt32(chosenIndex), offset: UInt32(raw.utf16.distance(from: raw.startIndex, to: range.lowerBound)))
        let end = TextPoint(sentence: UInt32(chosenIndex), offset: UInt32(raw.utf16.distance(from: raw.startIndex, to: range.upperBound)))

        if let existing = model.marks.first(where: { $0.section == targetSection && $0.start == start && $0.end == end }) {
            let previousNote = existing.note
            let combined = Self.mergeNote(existing: previousNote, addition: clean)
            do {
                try model.core.setMarkNote(book: model.bookID, id: existing.id, note: combined)
                model.reloadNotes()
                turns[index].savedMarkID = existing.id
                model.show(Library.Toast(
                    icon: "square.and.pencil",
                    title: String(localized: "已存进想法"),
                    detail: "“\(existing.text.prefix(16))”",
                    undo: { [weak self, weak model] in
                        guard let model else { return }
                        try? model.core.setMarkNote(book: model.bookID, id: existing.id, note: previousNote)
                        model.reloadNotes()
                        if let i = self?.turns.firstIndex(where: { $0.id == turnID }) {
                            self?.turns[i].savedMarkID = nil
                        }
                    }
                ))
            } catch {
                model.failure = Library.message(for: error)
            }
            return
        }

        do {
            let created = try model.core.addMark(
                book: model.bookID,
                section: targetSection,
                start: start,
                end: end,
                text: trimmed,
                style: model.lastStyle,
                note: clean
            )
            model.pager.keepShown(created.id)
            model.reloadNotes()
            turns[index].savedMarkID = created.id
            model.show(Library.Toast(
                icon: "square.and.pencil",
                title: String(localized: "已存进想法"),
                detail: "“\(created.text.prefix(16))”",
                undo: { [weak self, weak model] in
                    guard let model else { return }
                    try? model.core.deleteMark(book: model.bookID, id: created.id)
                    model.reloadNotes()
                    if let i = self?.turns.firstIndex(where: { $0.id == turnID }) {
                        self?.turns[i].savedMarkID = nil
                    }
                }
            ))
        } catch {
            model.failure = Library.message(for: error)
        }
    }

    nonisolated private static func mergeNote(existing: String?, addition: String) -> String {
        guard let old = existing?.trimmingCharacters(in: .whitespacesAndNewlines), !old.isEmpty else {
            return addition
        }
        if old.contains(addition) { return old }
        return "\(old)\n\n\(addition)"
    }

    /// Strips `[n]` citation markers so an answer saved into 想法 reads cleanly in notes & exports.
    nonisolated static func noteText(from answerText: String) -> String {
        answerText
            .replacingOccurrences(of: #"[ \t\u00A0]*\[\d+\]"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Removes `<think>…</think>` blocks from reasoning models, reporting whether an unclosed `<think>` is still streaming.
    nonisolated static func stripThinking(_ raw: String) -> (visible: String, thinking: Bool) {
        let completed = raw.replacingOccurrences(of: #"(?is)<think>.*?</think>"#, with: "", options: .regularExpression)
        if let open = completed.range(of: "<think>", options: .caseInsensitive) {
            let prefix = String(completed[..<open.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
            return (prefix, true)
        }
        return (completed.trimmingCharacters(in: .whitespacesAndNewlines), false)
    }

    /// Live preview string during SSE streaming: hides `<think>`, hides the trailing `接着问` line,
    /// and trims an incomplete `[1` bracket at the very tail.
    nonisolated static func streamingPreview(from raw: String, hideNotYet: Bool) -> (visible: String, thinking: Bool) {
        let (cleaned, thinking) = stripThinking(raw)
        guard !cleaned.isEmpty else { return ("", thinking) }
        if hideNotYet, cleaned.hasPrefix("没读到") { return ("", thinking) }
        var lines = cleaned.components(separatedBy: "\n")
        if let i = lines.lastIndex(where: { $0.contains("接着问") }) {
            lines.removeSubrange(i...)
        }
        var body = lines.joined(separator: "\n")
            .replacingOccurrences(of: #"\s*\[\d*$"#, with: "", options: .regularExpression)
            .trimmingCharacters(in: .whitespacesAndNewlines)
        if hideNotYet, "没读到".hasPrefix(body) { body = "" }
        return (body, thinking)
    }

    /// The answer (with `<think>` blocks removed), and the 接着问 line taken off its end.
    nonisolated static func split(_ reply: String) -> (String, [String]) {
        let cleaned = stripThinking(reply).visible
        var lines = cleaned.components(separatedBy: "\n")
        guard let i = lines.lastIndex(where: { $0.contains("接着问") }) else { return (cleaned, []) }
        let line = lines.remove(at: i)
        let asks = line.components(separatedBy: "接着问").last?
            .trimmingCharacters(in: CharacterSet(charactersIn: "：: *"))
            .components(separatedBy: CharacterSet(charactersIn: "｜|"))
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty } ?? []
        return (lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines), Array(asks.prefix(2)))
    }

    nonisolated static func citations(in text: String) -> [Int] {
        text.matches(of: /\[(\d+)\]/).compactMap { Int($0.1) }
    }

    nonisolated static func classify(question: String, action: CompanionCommand.Action? = nil) -> QueryKind {
        if let action {
            switch action {
            case .chapter: return .chapter
            case .recap, .characters, .quote, .quiz: return .broad
            default: break
            }
        }
        let lower = question.lowercased()
        let chapterMarkers = ["这一章", "本章", "这章", "当前章", "这回", "本回", "这一回", "这一节", "本节", "this chapter", "current chapter"]
        if chapterMarkers.contains(where: { lower.contains($0) }) {
            return .chapter
        }
        let broadMarkers = [
            "前面发生", "前情", "目前为止", "主要人物", "人物关系", "出场人物",
            "总结", "概括", "梳理", "大纲", "脉络", "主旨", "分析", "思考题", "考考我", "挑几句", "挑两三句",
            "what happened", "so far", "main characters", "summarize", "summary", "outline", "analyze",
        ]
        if broadMarkers.contains(where: { lower.contains($0) }) {
            return .broad
        }
        return .focused
    }

    nonisolated static func chapterSections(detail: BookDetail?, section: UInt32) -> Range<Int> {
        guard let detail else { return Int(section) ..< Int(section) + 1 }
        let total = max(1, detail.sections.count)
        let toc = detail.toc
        if let entry = toc.last(where: { $0.section <= section }) ?? toc.first(where: { $0.section == section }) {
            let start = Int(entry.section)
            let end = toc.first(where: { $0.section > entry.section }).map { Int($0.section) } ?? total
            return start ..< max(start + 1, end)
        }
        let cur = min(Int(section), total - 1)
        return cur ..< cur + 1
    }

    // MARK: Finding the sentences

    nonisolated struct Excerpt: Sendable {
        let section: UInt32
        let sentence: UInt32
        let text: String
    }

    nonisolated private static let stopWords: Set<String> = [
        "什么", "为什么", "怎么", "怎么样", "怎样", "如何", "哪里", "哪儿", "哪个", "谁", "是不是", "有没有",
        "这个", "那个", "这些", "那些", "这里", "那里", "这本书", "本书", "这本", "这章", "这一章", "一章",
        "意思", "后来", "之后", "以后", "现在", "时候", "为何", "是否", "可以", "能不能", "一个", "一下",
        "他们", "她们", "我们", "你们", "自己", "就是", "还是", "但是", "因为", "所以", "然后", "发生",
        "总结", "概括", "梳理", "讲了", "主要", "内容", "目前", "为止", "前面", "人物", "关系", "出场",
        "the", "and", "what", "why", "how", "who", "does", "did", "this", "that", "with", "about",
    ]

    /// Builds numbered passages `[1]…[K]` grounded in the book:
    /// - `.chapter`: chunks the entire current chapter (within `upTo` when spoiler-free) into sequential passages
    ///   so chapter summaries see the whole chapter rather than keyword fragments.
    /// - `.broad`: combines keyword hits with milestone passages across read sections and recent prose.
    /// - `.focused`: top keyword matches plus the sentences around the current reading spot.
    @concurrent nonisolated static func excerpts(
        core: YuyueCore,
        book: String,
        question: String,
        here: Position,
        upTo: Position?,
        kind: QueryKind,
        chapterSections: Range<Int>,
        bookStart: Int
    ) async -> [Excerpt] {
        let tokenizer = NLTokenizer(unit: .word)
        tokenizer.string = question
        var words = Set<String>()
        tokenizer.enumerateTokens(in: question.startIndex ..< question.endIndex) { range, _ in
            let w = question[range].lowercased()
            let latin = w.unicodeScalars.allSatisfy { $0.isASCII }
            if (latin ? w.count >= 3 : w.count >= 2), !stopWords.contains(w) { words.insert(w) }
            return true
        }
        var sentences: [UInt32: [String]] = [:]
        func text(_ section: UInt32) -> [String] {
            if let s = sentences[section] { return s }
            let s = (try? core.sentences(bookId: book, section: section)) ?? []
            sentences[section] = s
            return s
        }
        func allowed(_ section: UInt32, _ sentence: UInt32) -> Bool {
            guard let upTo else { return true }
            return section < upTo.section || (section == upTo.section && sentence <= upTo.sentence + 8)
        }

        // 1. Chapter-level context: sequential ~480-char passages across the current chapter.
        if kind == .chapter {
            var chapterChunks: [Excerpt] = []
            for sec in chapterSections where sec >= 0 {
                let secU = UInt32(sec)
                if let upTo, secU > upTo.section { break }
                let all = text(secU)
                guard !all.isEmpty else { continue }
                let limit = (upTo != nil && secU == upTo!.section) ? min(all.count, Int(upTo!.sentence) + 9) : all.count
                var idx = 0
                while idx < limit {
                    let startSent = idx
                    var parts: [String] = []
                    var chars = 0
                    while idx < limit, chars < 480 || parts.isEmpty {
                        let s = all[idx].trimmingCharacters(in: .whitespacesAndNewlines).replacingOccurrences(of: "\n", with: " ")
                        if !s.isEmpty {
                            parts.append(s)
                            chars += s.count
                        }
                        idx += 1
                    }
                    if !parts.isEmpty {
                        chapterChunks.append(Excerpt(section: secU, sentence: UInt32(startSent), text: String(parts.joined(separator: " ").prefix(540))))
                    }
                }
            }
            if !chapterChunks.isEmpty {
                let maxChunks = 24
                if chapterChunks.count <= maxChunks { return chapterChunks }
                // Extra-long chapter: keep first, last, keyword-matching chunks, chunk near `here`, and evenly strided chunks.
                var chosen = Set<Int>([0, chapterChunks.count - 1])
                if let nearIdx = chapterChunks.lastIndex(where: { ($0.section, $0.sentence) <= (here.section, here.sentence) }) {
                    chosen.insert(nearIdx)
                }
                if !words.isEmpty {
                    for (ci, chunk) in chapterChunks.enumerated() where chosen.count < maxChunks / 2 {
                        let lower = chunk.text.lowercased()
                        if words.contains(where: { lower.contains($0) }) { chosen.insert(ci) }
                    }
                }
                let step = Double(chapterChunks.count - 1) / Double(maxChunks - 1)
                for k in 0 ..< maxChunks where chosen.count < maxChunks {
                    chosen.insert(min(chapterChunks.count - 1, Int((Double(k) * step).rounded())))
                }
                return chosen.sorted().prefix(maxChunks).map { chapterChunks[$0] }
            }
        }

        // 2. Keyword search + broad/focused context.
        var score: [String: (Int, UInt32, UInt32)] = [:]
        for word in words {
            let hits = (try? core.searchBook(book: book, query: word)) ?? []
            for h in hits where allowed(h.section, h.sentence) {
                let key = "\(h.section)/\(h.sentence)"
                let old = score[key]?.0 ?? 0
                score[key] = (old + 1, h.section, h.sentence)
            }
        }
        let keywordCap = kind == .broad ? 10 : 10
        var picked: [(UInt32, UInt32)] = []
        let ranked = score.values.sorted { a, b in a.0 != b.0 ? a.0 > b.0 : (a.1, a.2) > (b.1, b.2) }
        for (_, section, sentence) in ranked where picked.count < keywordCap {
            if picked.contains(where: { $0.0 == section && abs(Int($0.1) - Int(sentence)) <= 2 }) { continue }
            picked.append((section, sentence))
        }

        // Recent context near the reading spot.
        let anchorPos = upTo ?? here
        let recentSpan = kind == .broad ? 20 : 12
        let end = Int(anchorPos.sentence) + 3
        for s in stride(from: end, through: max(0, end - recentSpan), by: -4) where !picked.contains(where: { $0.0 == anchorPos.section && abs(Int($0.1) - s) <= 2 }) {
            if s < text(anchorPos.section).count { picked.append((anchorPos.section, UInt32(s))) }
        }

        // For broad narrative questions (recap / characters / quotes) with sparse keyword hits,
        // sample milestone passages across earlier read sections so the model sees the story arc.
        if kind == .broad, picked.count < 16 {
            let maxSec = Int(anchorPos.section)
            let minSec = max(0, min(bookStart, maxSec))
            if maxSec >= minSec {
                let totalSecs = maxSec - minSec + 1
                let strideSec = max(1, totalSecs / 8)
                for sec in stride(from: minSec, through: maxSec, by: strideSec) where picked.count < 16 {
                    let secU = UInt32(sec)
                    let all = text(secU)
                    guard !all.isEmpty else { continue }
                    let cap = (upTo != nil && secU == anchorPos.section) ? min(all.count, Int(anchorPos.sentence) + 1) : all.count
                    guard cap > 0 else { continue }
                    for probe in [0, cap / 2] where probe < cap && picked.count < 16 {
                        if !picked.contains(where: { $0.0 == secU && abs(Int($0.1) - probe) <= 3 }) {
                            picked.append((secU, UInt32(probe)))
                        }
                    }
                }
            }
        }

        picked.sort { ($0.0, $0.1) < ($1.0, $1.1) }
        let charLimit = kind == .broad ? 360 : 280
        let tailWindow = kind == .broad ? 2 : 1
        return picked.compactMap { section, sentence in
            let all = text(section)
            let i = Int(sentence)
            guard all.indices.contains(i) else { return nil }
            let from = max(0, i - 1), to = min(all.count - 1, i + tailWindow)
            // A space between sentences: English ones would run together otherwise.
            let joined = all[from ... to].map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.joined(separator: " ").replacingOccurrences(of: "\n", with: " ")
            return Excerpt(section: section, sentence: sentence, text: String(joined.prefix(charLimit)))
        }
    }

    // MARK: Kept answers

    /// 按"书 + 章节"缓存: the same question at the same place costs nothing the second time.
    private static func file(_ model: ReaderModel) -> URL? {
        model.library.map { $0.dataDir.appending(path: "companion", directoryHint: .isDirectory).appending(path: "\(model.bookID).json") }
    }

    private static func cache(_ model: ReaderModel) -> [String: Answer]? {
        guard let url = file(model), let data = try? Data(contentsOf: url) else { return nil }
        return try? JSONDecoder().decode([String: Answer].self, from: data)
    }

    private static func store(_ model: ReaderModel, key: String, _ answer: Answer) {
        guard let url = file(model) else { return }
        var all = cache(model) ?? [:]
        all[key] = answer
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? JSONEncoder().encode(all).write(to: url, options: .atomic)
    }
}

// MARK: - Custom quick prompts

/// 用户保存在「设置 · 书伴」里的常用提问，同时出现在底部建议栏和 `/` 快捷指令里.
nonisolated enum CompanionPrompts {
    static let key = "companion.customPrompts"
    static let maxCount = 8
    static let maxLength = 120
    static let changed = Notification.Name("yuyue.companionPromptsChanged")

    static var presets: [String] {
        [
            String(localized: "拆解这句语法与长难句"),
            String(localized: "把这段古文译成现代白话"),
            String(localized: "提炼核心论点与逻辑"),
        ]
    }

    static var list: [String] {
        normalized(UserDefaults.standard.stringArray(forKey: key) ?? [])
    }

    static func normalized(_ items: [String]) -> [String] {
        var seen = Set<String>()
        var out: [String] = []
        for raw in items {
            let trimmed = String(raw.trimmingCharacters(in: .whitespacesAndNewlines).prefix(maxLength))
            guard !trimmed.isEmpty, seen.insert(trimmed).inserted else { continue }
            out.append(trimmed)
            if out.count >= maxCount { break }
        }
        return out
    }

    static func adding(_ prompt: String, to current: [String]) -> [String] {
        let trimmed = String(prompt.trimmingCharacters(in: .whitespacesAndNewlines).prefix(maxLength))
        guard !trimmed.isEmpty else { return normalized(current) }
        let base = normalized(current)
        if base.contains(trimmed) || base.count >= maxCount { return base }
        return base + [trimmed]
    }

    static func removing(_ prompt: String, from current: [String]) -> [String] {
        normalized(current).filter { $0 != prompt }
    }

    static func add(_ prompt: String) {
        let next = adding(prompt, to: list)
        guard next != list else { return }
        UserDefaults.standard.set(next, forKey: key)
        NotificationCenter.default.post(name: changed, object: nil)
    }

    static func remove(_ prompt: String) {
        let next = removing(prompt, from: list)
        guard next != list else { return }
        UserDefaults.standard.set(next, forKey: key)
        NotificationCenter.default.post(name: changed, object: nil)
    }

    static func suggestions(
        hasQuote: Bool,
        followUps: [String]?,
        quoteDefaults: [String],
        startDefaults: [String],
        custom: [String] = list
    ) -> [String] {
        let primary: [String]
        let secondary: [String]
        if hasQuote {
            primary = custom
            secondary = quoteDefaults
        } else if let followUps, !followUps.isEmpty {
            primary = followUps
            secondary = custom
        } else {
            primary = custom
            secondary = startDefaults
        }
        var seen = Set<String>()
        return (primary + secondary).filter { !$0.isEmpty && seen.insert($0).inserted }
    }
}

// MARK: - Slash commands

/// Slash commands (`/recap`, `/clear`, …) shared by Mac and iOS 书伴 composers.
nonisolated struct CompanionCommand: Identifiable, Hashable, Sendable {
    enum Category: Hashable, Sendable {
        /// Sends a reading prompt to 书伴 (respecting current scope and optional quote).
        case ask
        /// Controls 书伴 locally without calling the model.
        case control
    }

    enum Action: Hashable, Sendable {
        case recap
        case chapter
        case characters
        case explain
        case quote
        case quiz
        case custom(String)
        case scope(BookCompanion.Scope)
        case model
        case clear
        case settings
    }

    struct Parsed: Equatable, Sendable {
        let command: CompanionCommand
        let argument: String
    }

    let id: String
    /// Command token without the leading `/`, e.g. `"recap"`.
    let name: String
    let title: String
    let subtitle: String
    let icon: String
    let category: Category
    let action: Action
    /// Whether the command accepts an optional trailing argument (e.g. `/characters 闰土`).
    let takesArgument: Bool
    /// Extra keywords matched alongside `name` and `title` (Chinese + English).
    let keywords: [String]

    var slashName: String { "/\(name)" }

    static var all: [CompanionCommand] {
        [
            CompanionCommand(
                id: "recap",
                name: "recap",
                title: String(localized: "回顾前情"),
                subtitle: String(localized: "梳理目前为止发生了什么"),
                icon: "arrow.counterclockwise",
                category: .ask,
                action: .recap,
                takesArgument: true,
                keywords: ["前情", "回顾", "剧情", "摘要", "summary", "previously"]
            ),
            CompanionCommand(
                id: "chapter",
                name: "chapter",
                title: String(localized: "总结本章"),
                subtitle: String(localized: "概括当前章节的主要内容"),
                icon: "doc.text",
                category: .ask,
                action: .chapter,
                takesArgument: true,
                keywords: ["本章", "章节", "总结", "这一章", "summary"]
            ),
            CompanionCommand(
                id: "characters",
                name: "characters",
                title: String(localized: "人物关系"),
                subtitle: String(localized: "梳理已出场人物与关系"),
                icon: "person.2",
                category: .ask,
                action: .characters,
                takesArgument: true,
                keywords: ["人物", "角色", "关系", "谁", "who", "cast"]
            ),
            CompanionCommand(
                id: "explain",
                name: "explain",
                title: String(localized: "解释含义"),
                subtitle: String(localized: "解读选中句子或当前段落"),
                icon: "text.magnifyingglass",
                category: .ask,
                action: .explain,
                takesArgument: true,
                keywords: ["解释", "解析", "意思", "含义", "meaning"]
            ),
            CompanionCommand(
                id: "quote",
                name: "quote",
                title: String(localized: "挑出金句"),
                subtitle: String(localized: "从已读内容里找几句值得回味的话"),
                icon: "text.quote",
                category: .ask,
                action: .quote,
                takesArgument: true,
                keywords: ["金句", "摘录", "名句", "句子", "highlight"]
            ),
            CompanionCommand(
                id: "quiz",
                name: "quiz",
                title: String(localized: "考考我"),
                subtitle: String(localized: "根据已读内容出两道思考题"),
                icon: "questionmark.bubble",
                category: .ask,
                action: .quiz,
                takesArgument: true,
                keywords: ["考考我", "测验", "提问", "思考", "test"]
            ),
        ] + customCommands(from: CompanionPrompts.list) + [
            CompanionCommand(
                id: "read",
                name: "read",
                title: String(localized: "仅限已读"),
                subtitle: String(localized: "切回不剧透模式"),
                icon: "eye.slash",
                category: .control,
                action: .scope(.read),
                takesArgument: true,
                keywords: ["已读", "不剧透", "防剧透", "spoiler"]
            ),
            CompanionCommand(
                id: "whole",
                name: "whole",
                title: String(localized: "全书范围"),
                subtitle: String(localized: "检索整本书（可能剧透）"),
                icon: "book",
                category: .control,
                action: .scope(.whole),
                takesArgument: true,
                keywords: ["全书", "剧透", "整本", "all"]
            ),
            CompanionCommand(
                id: "model",
                name: "model",
                title: String(localized: "切换模型"),
                subtitle: String(localized: "打开模型与服务商选择"),
                icon: "sparkles",
                category: .control,
                action: .model,
                takesArgument: false,
                keywords: ["模型", "服务商", "切换", "provider"]
            ),
            CompanionCommand(
                id: "clear",
                name: "clear",
                title: String(localized: "清空对话"),
                subtitle: String(localized: "清除当前这本书的对话记录"),
                icon: "trash",
                category: .control,
                action: .clear,
                takesArgument: false,
                keywords: ["清空", "新对话", "清除", "重置", "reset", "new"]
            ),
            CompanionCommand(
                id: "settings",
                name: "settings",
                title: String(localized: "书伴设置"),
                subtitle: String(localized: "配置服务商与 API Key"),
                icon: "gearshape",
                category: .control,
                action: .settings,
                takesArgument: false,
                keywords: ["设置", "配置", "key", "api"]
            ),
        ]
    }

    static func customCommands(from prompts: [String]) -> [CompanionCommand] {
        prompts.enumerated().map { index, prompt in
            CompanionCommand(
                id: "custom-\(index + 1)",
                name: "p\(index + 1)",
                title: prompt,
                subtitle: String(localized: "常用提问"),
                icon: "text.bubble",
                category: .ask,
                action: .custom(prompt),
                takesArgument: true,
                keywords: ["常用", "自定义", "custom", "prompt"]
            )
        }
    }

    /// Returns the filter text after a leading `/` (or full-width `／`) while the user is still
    /// typing the command token (before any whitespace or newline).
    static func slashQuery(in draft: String) -> String? {
        guard let first = draft.first, first == "/" || first == "／" else { return nil }
        let rest = draft.dropFirst()
        if rest.contains(where: \.isWhitespace) { return nil }
        return String(rest)
    }

    /// Filters commands by prefix/substring across English name, localized title, keywords, and subtitle.
    static func matching(_ query: String, in commands: [CompanionCommand] = all) -> [CompanionCommand] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !q.isEmpty else { return commands }
        let scored: [(Int, CompanionCommand)] = commands.compactMap { cmd in
            if cmd.name.hasPrefix(q) { return (0, cmd) }
            if cmd.title.lowercased().hasPrefix(q) { return (1, cmd) }
            if cmd.keywords.contains(where: { $0.lowercased().hasPrefix(q) }) { return (2, cmd) }
            if cmd.name.contains(q)
                || cmd.title.lowercased().contains(q)
                || cmd.keywords.contains(where: { $0.lowercased().contains(q) })
                || cmd.subtitle.lowercased().contains(q)
            {
                return (3, cmd)
            }
            return nil
        }
        return scored.sorted { $0.0 < $1.0 }.map(\.1)
    }

    /// Parses a submitted draft starting with `/` (or `／`) into a known command + optional argument.
    static func parse(_ draft: String, in commands: [CompanionCommand] = all) -> Parsed? {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first, first == "/" || first == "／" else { return nil }
        let body = trimmed.dropFirst()
        let splitIndex = body.firstIndex(where: \.isWhitespace) ?? body.endIndex
        let token = String(body[..<splitIndex]).lowercased()
        guard !token.isEmpty else { return nil }
        let arg = String(body[splitIndex...]).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let cmd = commands.first(where: {
            $0.name == token || $0.title.lowercased() == token
        }) else {
            return nil
        }
        return Parsed(command: cmd, argument: arg)
    }

    /// Builds the natural-language question sent to `BookCompanion` for `.ask` commands.
    func question(argument: String, hasQuote: Bool, chapterTitle: String?) -> String? {
        let arg = argument.trimmingCharacters(in: .whitespacesAndNewlines)
        switch action {
        case .recap:
            if !arg.isEmpty { return String(localized: "帮我回顾一下目前为止关于「\(arg)」的情节。") }
            return String(localized: "帮我回顾一下目前为止发生了什么？")
        case .chapter:
            if !arg.isEmpty { return String(localized: "结合「\(arg)」，总结一下这一章讲了什么？") }
            if let chapterTitle, !chapterTitle.isEmpty {
                return String(localized: "总结一下「\(chapterTitle)」这一章讲了什么？")
            }
            return String(localized: "总结一下这一章讲了什么？")
        case .characters:
            if !arg.isEmpty { return String(localized: "介绍一下「\(arg)」是谁，以及和其他人物的关系。") }
            return String(localized: "梳理一下目前出场的主要人物和他们之间的关系。")
        case .explain:
            if !arg.isEmpty { return String(localized: "解释一下「\(arg)」在书里的意思和作用。") }
            if hasQuote { return String(localized: "这句是什么意思？结合上下文解释一下。") }
            return String(localized: "结合上下文，解释一下当前这一段的意思和用意。")
        case .quote:
            if !arg.isEmpty { return String(localized: "从已读内容里挑几句和「\(arg)」相关的精彩句子，并标出出处。") }
            return String(localized: "从目前读过的内容里，挑两三句值得回味的句子并简短点评。")
        case .quiz:
            if !arg.isEmpty { return String(localized: "围绕「\(arg)」，根据我已读的内容出两道思考题考考我。") }
            return String(localized: "根据我目前读过的内容，出两道有意思的思考题考考我。")
        case .custom(let prompt):
            return arg.isEmpty ? prompt : "\(prompt)\n\(arg)"
        case .scope, .model, .clear, .settings:
            return nil
        }
    }
}
