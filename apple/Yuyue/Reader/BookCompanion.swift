import Foundation
import NaturalLanguage
import YuyueCore

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

    struct Source: Codable, Equatable {
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
        var answer: Answer?
        var failed: String?
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
    func ask(_ question: String, quote: String? = nil, scope: Scope? = nil) {
        let q = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, let model, !busy else { return }
        let scope = scope ?? self.scope
        let config = CompanionConfig.current
        turns.append(Turn(question: q, quote: quote, scope: scope))
        let index = turns.count - 1
        guard config.isReady else {
            turns[index].failed = config.provider == .apple ? Companion.appleAvailability ?? CompanionError.notReady.errorDescription : CompanionError.notReady.errorDescription
            return
        }
        let core = model.core
        let book = model.bookID
        let detail = model.detail
        let here = model.pager.position ?? (try? core.progress(bookId: book))?.position ?? Position(section: detail?.start ?? 0, sentence: 0)
        let earlier = turns.prefix(index).suffix(2).compactMap { t in t.answer.map { (t.question, $0.text) } }
        let cacheKey = [scope.rawValue, scope == .read ? "\(here.section)" : "", quote ?? "", q].joined(separator: "|")
        if let cached = Self.cache(model)?[cacheKey] {
            turns[index].answer = cached
            return
        }
        asking = Task { [weak self, weak model] in
            let excerpts = await Self.excerpts(core: core, book: book, question: q + " " + (quote ?? ""), upTo: scope == .read ? here : nil)
            guard let model, !Task.isCancelled else { return }
            let title = detail?.summary.title ?? ""
            let percent = Int((model.fraction(section: here.section, sentence: Int(here.sentence)) * 100).rounded())
            var system = "你是「语阅」里的书伴，陪读者读《\(title)》\(detail?.summary.author.map { "（\($0)）" } ?? "")。\n"
            switch scope {
            case .read:
                system += """
                读者现在读到全书的 \(percent)%。
                规则：
                1. 只根据下面给出的原文片段回答。不要用你知道的、这本书后面的情节，不要剧透。
                2. 用\(Companion.replyLanguage)，口语，简短，一般三到五句话。
                3. 说到原文里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                4. 如果这些片段不够回答，或者答案在读者还没读到的地方，只回答三个字：没读到
                5. 能回答的话，最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                """
            case .whole:
                system += """
                读者想用整本书来回答，不介意提前知道后面的情节。
                规则：
                1. 可以用整本书的内容，也可以结合你对这本书的了解。
                2. 用\(Companion.replyLanguage)，口语，简短，一般三到五句话。
                3. 说到下面原文片段里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                4. 最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                """
            }
            var prompt = ""
            if !earlier.isEmpty {
                prompt += "之前的对话：\n" + earlier.map { "读者：\($0.0)\n书伴：\($0.1)" }.joined(separator: "\n") + "\n\n"
            }
            prompt += "原文片段：\n"
            for (n, e) in excerpts.enumerated() {
                let chapter = detail?.chapterTitle(for: e.section).map { "（\($0)）" } ?? ""
                prompt += "[\(n + 1)]\(chapter)\(e.text)\n"
            }
            if let quote { prompt += "\n读者选中的句子：「\(quote)」\n" }
            prompt += "\n读者问：\(q)"
            do {
                let (text, next) = Self.split(try await Companion.answer(config, system: system, prompt: prompt, source: .companion))
                guard let self, !Task.isCancelled else { return }
                let notYet = scope == .read && text.hasPrefix("没读到")
                var sources: [Int: Source] = [:]
                for (n, e) in excerpts.enumerated() {
                    let later = scope == .whole && (e.section, e.sentence) > (here.section, here.sentence + 8)
                    sources[n + 1] = Source(section: e.section, sentence: e.sentence,
                                            page: model.estimatedPage(section: e.section, sentence: Int(e.sentence)),
                                            later: later ? true : nil)
                }
                // Only the ones it pointed at.
                let used = Set(Self.citations(in: text))
                let answer = Answer(text: text, sources: sources.filter { used.contains($0.key) }, label: config.label,
                                    notYet: notYet, next: notYet || next.isEmpty ? nil : next)
                if self.turns.indices.contains(index) { self.turns[index].answer = answer }
                Self.store(model, key: cacheKey, answer)
            } catch {
                guard let self, !Task.isCancelled, self.turns.indices.contains(index) else { return }
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

    /// The answer, and the 接着问 line taken off its end.
    static func split(_ reply: String) -> (String, [String]) {
        var lines = reply.components(separatedBy: "\n")
        guard let i = lines.lastIndex(where: { $0.contains("接着问") }) else { return (reply, []) }
        let line = lines.remove(at: i)
        let asks = line.components(separatedBy: "接着问").last?
            .trimmingCharacters(in: CharacterSet(charactersIn: "：: *"))
            .components(separatedBy: CharacterSet(charactersIn: "｜|"))
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty } ?? []
        return (lines.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines), Array(asks.prefix(2)))
    }

    static func citations(in text: String) -> [Int] {
        text.matches(of: /\[(\d+)\]/).compactMap { Int($0.1) }
    }

    // MARK: Finding the sentences

    struct Excerpt: Sendable {
        let section: UInt32
        let sentence: UInt32
        let text: String
    }

    nonisolated private static let stopWords: Set<String> = [
        "什么", "为什么", "怎么", "怎么样", "怎样", "如何", "哪里", "哪儿", "哪个", "谁", "是不是", "有没有",
        "这个", "那个", "这些", "那些", "这里", "那里", "这本书", "本书", "这本", "这章", "这一章", "一章",
        "意思", "后来", "之后", "以后", "现在", "时候", "为何", "是否", "可以", "能不能", "一个", "一下",
        "他们", "她们", "我们", "你们", "自己", "就是", "还是", "但是", "因为", "所以", "然后", "发生",
        "the", "and", "what", "why", "how", "who", "does", "did", "this", "that", "with", "about",
    ]

    /// Up to a dozen numbered sentences (with a little around each) to answer from: the ones
    /// sharing the most words with the question, then the last few read. With `upTo`, nothing
    /// after that place.
    @concurrent nonisolated static func excerpts(core: YuyueCore, book: String, question: String, upTo: Position?) async -> [Excerpt] {
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

        var score: [String: (Int, UInt32, UInt32)] = [:]
        for word in words {
            let hits = (try? core.searchBook(book: book, query: word)) ?? []
            for h in hits where allowed(h.section, h.sentence) {
                let key = "\(h.section)/\(h.sentence)"
                let old = score[key]?.0 ?? 0
                score[key] = (old + 1, h.section, h.sentence)
            }
        }
        var picked: [(UInt32, UInt32)] = []
        let ranked = score.values.sorted { a, b in a.0 != b.0 ? a.0 > b.0 : (a.1, a.2) > (b.1, b.2) }
        for (_, section, sentence) in ranked where picked.count < 8 {
            if picked.contains(where: { $0.0 == section && abs(Int($0.1) - Int(sentence)) <= 2 }) { continue }
            picked.append((section, sentence))
        }
        // The page being read: what "这句" and "刚才" are about.
        if let upTo {
            let end = Int(upTo.sentence) + 3
            for s in stride(from: end, through: max(0, end - 12), by: -4) where !picked.contains(where: { $0.0 == upTo.section && abs(Int($0.1) - s) <= 2 }) {
                if s < text(upTo.section).count { picked.append((upTo.section, UInt32(s))) }
            }
        }
        picked.sort { ($0.0, $0.1) < ($1.0, $1.1) }
        return picked.compactMap { section, sentence in
            let all = text(section)
            let i = Int(sentence)
            guard all.indices.contains(i) else { return nil }
            let from = max(0, i - 1), to = min(all.count - 1, i + 1)
            // A space between sentences: English ones would run together otherwise.
            let joined = all[from ... to].map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.joined(separator: " ").replacingOccurrences(of: "\n", with: " ")
            return Excerpt(section: section, sentence: sentence, text: String(joined.prefix(240)))
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

    /// Returns the filter text after a leading `/` (or full-width `／`) while the user is still
    /// typing the command token (before any whitespace or newline).
    static func slashQuery(in draft: String) -> String? {
        guard let first = draft.first, first == "/" || first == "／" else { return nil }
        let rest = draft.dropFirst()
        if rest.contains(where: \.isWhitespace) { return nil }
        return String(rest)
    }

    /// Filters commands by prefix/substring across English name, localized title, keywords, and subtitle.
    static func matching(_ query: String) -> [CompanionCommand] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        guard !q.isEmpty else { return all }
        let scored: [(Int, CompanionCommand)] = all.compactMap { cmd in
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
    static func parse(_ draft: String) -> Parsed? {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let first = trimmed.first, first == "/" || first == "／" else { return nil }
        let body = trimmed.dropFirst()
        let splitIndex = body.firstIndex(where: \.isWhitespace) ?? body.endIndex
        let token = String(body[..<splitIndex]).lowercased()
        guard !token.isEmpty else { return nil }
        let arg = String(body[splitIndex...]).trimmingCharacters(in: .whitespacesAndNewlines)
        guard let cmd = all.first(where: {
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
        case .scope, .model, .clear, .settings:
            return nil
        }
    }
}
