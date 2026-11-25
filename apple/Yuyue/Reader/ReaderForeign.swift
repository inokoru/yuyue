import AVFoundation
import Foundation
import SwiftUI
@preconcurrency import Translation
import YuyueCore
#if os(macOS)
import CoreServices
import Darwin

nonisolated private final class LookupBox: @unchecked Sendable {
    let word: String
    var raw: String?
    init(_ word: String) { self.word = word }
}
#endif

/// 外文原著: click a word and it is said aloud and looked up
/// in the system dictionary; keep it in the 生词本 and it is dotted in every book; 译 puts a
/// translation under each paragraph, and a double click translates one sentence.

/// 设置 · 外文.
enum ForeignPrefs {
    static let speakKey = "foreign.speak"
    static let accentKey = "foreign.accent"
    static let markKey = "foreign.mark"
    static let glossKey = "foreign.gloss"
    static let levelKey = "foreign.glossLevel"
    static let glossVocabKey = "foreign.glossVocab"
    static let aiTranslateKey = "foreign.aiTranslate"

    /// 难词注释's levels: the easiest exam that lists a word, 四级 marking the most.
    static let levels: [(name: String, level: UInt8)] = [(String(localized: "四级"), 2), (String(localized: "六级"), 3), (String(localized: "考研"), 4), (String(localized: "雅思"), 5), ("GRE", 6)]

    enum Accent: String {
        case british = "en-GB", american = "en-US"
    }

    /// 点词时读出来.
    static var speaks: Bool { UserDefaults.standard.object(forKey: speakKey) as? Bool ?? true }
    /// 生词在所有书里标出来.
    static var marks: Bool { UserDefaults.standard.object(forKey: markKey) as? Bool ?? true }
    /// 在难词上方写释义.
    static var glosses: Bool { UserDefaults.standard.object(forKey: glossKey) as? Bool ?? true }
    static var level: UInt8 { UserDefaults.standard.object(forKey: levelKey) == nil ? 3 : UInt8(clamping: UserDefaults.standard.integer(forKey: levelKey)) }
    /// 只标生词本里的词.
    static var glossVocabOnly: Bool { UserDefaults.standard.bool(forKey: glossVocabKey) }
    /// 优先用书伴模型翻译整页与整句.
    static var aiTranslate: Bool { UserDefaults.standard.object(forKey: aiTranslateKey) as? Bool ?? true }

    /// The voice for a book in `language`: English in the accent chosen.
    static func voice(for language: String?) -> AVSpeechSynthesisVoice? {
        let language = language ?? "en"
        if language.lowercased().hasPrefix("en") {
            let accent = UserDefaults.standard.string(forKey: accentKey).flatMap(Accent.init) ?? .british
            return AVSpeechSynthesisVoice(language: accent.rawValue)
        }
        return AVSpeechSynthesisVoice(language: language)
    }
}

/// The word clicked, and what its card shows.
struct WordLookup: Equatable {
    let word: String
    let section: UInt32
    let start: ReaderMessage.Point
    let end: ReaderMessage.Point
    /// The sentence it was met in.
    let context: String
    /// Where it is on the page.
    let rect: CGRect
    var definition: WordDefinition?
    /// From the translator, when the dictionary has nothing.
    var gloss: String?
    /// 本书出现 12 次.
    var count: Int?
    /// In the 生词本 already.
    var kept = false
    /// The imported MDict dictionary it came from, and its whole entry (完整释义).
    var dictionary: String?
    var html: String?

    /// What goes into the 生词本: the dictionary's headword, or the word as clicked, lower-cased
    /// unless it looks like a name.
    var headword: String {
        if let h = definition?.headword, !h.isEmpty { return h }
        let rest = word.dropFirst()
        return rest == rest.lowercased() ? word.lowercased() : word
    }
}

/// The short meaning, as the card shows it: "fortune /ˈfɔːtʃuːn/ n. 财富，大笔的钱；运气".
nonisolated struct WordDefinition: Equatable, Sendable {
    var headword: String
    var phonetic: String?
    /// "n.", "v.", "adj.".
    var partOfSpeech: String?
    var meaning: String

    /// "n. 财富，大笔的钱" for the 生词本.
    var line: String { [partOfSpeech, meaning].compactMap { $0 }.joined(separator: " ") }
}

/// 双击一句只翻这一句.
struct SentenceTranslation: Equatable {
    let section: UInt32
    let sentence: Int
    let text: String
    let at: CGPoint
    var translated: String?
    var failed: String?
}

nonisolated enum SystemDictionary {
    private static let cache = Mutex<[String: WordDefinition?]>([:])

    /// Runs `define(_:)` off the main actor so DictionaryServices never stalls UI rendering.
    @concurrent static func defineAsync(_ word: String) async -> WordDefinition? {
        define(word)
    }

    /// The first entry the system dictionaries have for `word`, cut down to one line; nil when
    /// none has it (on a Mac, Dictionary › Settings decides which are on, and in what order).
    static func define(_ word: String) -> WordDefinition? {
        #if os(macOS)
        let key = word.lowercased()
        if let hit = cache.withLock({ $0[key] }) { return hit }
        // DictionaryServices overwrites a thread-local slot Swift Concurrency reads as its
        // current AsyncTask when called inside a Task; run the lookup on a bare pthread.
        let box = LookupBox(word)
        let ptr = Unmanaged.passRetained(box).toOpaque()
        var thread: pthread_t?
        if pthread_create(&thread, nil, { raw in
            let box = Unmanaged<LookupBox>.fromOpaque(raw).takeRetainedValue()
            let ns = box.word as NSString
            box.raw = DCSCopyTextDefinition(nil, box.word as CFString, CFRange(location: 0, length: ns.length))?.takeRetainedValue() as String?
            return nil
        }, ptr) == 0, let thread {
            pthread_join(thread, nil)
        } else {
            Unmanaged<LookupBox>.fromOpaque(ptr).release()
        }
        let parsed = box.raw.flatMap { raw -> WordDefinition? in
            #if DEBUG
            try? raw.write(to: URL(filePath: NSTemporaryDirectory()).appending(path: "dict-raw.txt"), atomically: true, encoding: .utf8)
            #endif
            return parse(raw, word: word)
        }
        cache.withLock {
            if $0.count >= 512 { $0.removeAll(keepingCapacity: true) }
            $0[key] = parsed
        }
        return parsed
        #else
        return nil
        #endif
    }

    private static let partsOfSpeech: [(String, String)] = [
        ("noun", "n."), ("verb", "v."), ("adjective", "adj."), ("adverb", "adv."), ("preposition", "prep."),
        ("conjunction", "conj."), ("pronoun", "pron."), ("exclamation", "int."), ("determiner", "det."),
        ("n.", "n."), ("vt.", "vt."), ("vi.", "vi."), ("v.", "v."), ("adj.", "adj."), ("adv.", "adv."), ("prep.", "prep."),
    ]

    /// "for·tune | ˈfôrCHən | noun 1 chance or luck as …" or "fortune /ˈfɔːtʃuːn/ n. ① 运气 …".
    static func parse(_ raw: String, word: String) -> WordDefinition? {
        var text = raw.replacingOccurrences(of: "\n", with: " ")
        var phonetic: String?
        var head = word
        if let m = text.firstMatch(of: /^(.+?)\s*\|\s*(.+?)\s*\|/) {
            head = String(m.1)
            // "BrE ˈfɔːtʃuːn, ˈfɔːtʃ(ə)n, AmE ˈfɔrtʃən": the first one.
            let first = m.2.replacingOccurrences(of: #"^(BrE|AmE)\s+"#, with: "", options: .regularExpression)
                .split(separator: ",").first.map(String.init) ?? String(m.2)
            phonetic = "/\(first.trimmingCharacters(in: .whitespaces))/"
            text = String(text[m.range.upperBound...])
        } else if let m = text.firstMatch(of: /^(.+?)\s+(\/[^\/]{1,40}\/)/) {
            head = String(m.1)
            phonetic = String(m.2)
            text = String(text[m.range.upperBound...])
        } else if text.lowercased().hasPrefix(word.lowercased()) {
            text = String(text.dropFirst(word.count))
        }
        head = head.replacingOccurrences(of: "·", with: "").replacingOccurrences(of: "•", with: "").trimmingCharacters(in: .whitespaces)
        if head.contains(" ") || head.count > 40 { head = word }
        text = text.trimmingCharacters(in: .whitespaces)
        var pos: String?
        let lower = text.lowercased()
        if let (name, short) = partsOfSpeech.first(where: { lower.hasPrefix($0.0 + " ") || lower.hasPrefix("a. " + $0.0) }) {
            pos = short
            if let range = text.range(of: name, options: .caseInsensitive) {
                text = String(text[range.upperBound...])
            }
        }
        // Numbering and labels: "1", "①", "A.", "[mass noun]", "(informal)".
        text = text.replacingOccurrences(of: #"^\s*(?:[A-Z]\.|\d+|[①-⑳])\s*"#, with: "", options: .regularExpression)
        text = text.replacingOccurrences(of: #"^\s*(?:\[[^\]]*\]|\([^)]*\))\s*"#, with: "", options: .regularExpression)
        var meaning: String
        // An English–Chinese entry: the Chinese of the first few senses, examples (▸) left out.
        var hans: [String] = []
        for sense in text.split(whereSeparator: { "①②③④⑤⑥⑦⑧⑨⑩".contains($0) }).prefix(5) {
            let head = sense.split(separator: "▸").first.map(String.init) ?? ""
            if let m = head.firstMatch(of: /\p{Han}[\p{Han}，、…]*/) {
                let h = String(m.0).trimmingCharacters(in: CharacterSet(charactersIn: "，、"))
                if !hans.contains(h) { hans.append(h) }
            }
        }
        if !hans.isEmpty {
            meaning = hans.prefix(3).joined(separator: "；")
        } else {
            meaning = String(text.prefix { !":;".contains($0) })
        }
        meaning = meaning.trimmingCharacters(in: .whitespaces)
        if meaning.count > 48 { meaning = String(meaning.prefix(46)) + "…" }
        guard !meaning.isEmpty else { return nil }
        return WordDefinition(headword: head, phonetic: phonetic, partOfSpeech: pos, meaning: meaning)
    }
}

/// The system's translation (Translation framework): on the device, free, no limit. Requests
/// queue here; the reader window runs them through `.translationTask(configuration)`, which
/// is also where the system offers to download a language the first time.
@Observable
final class Translator {
    /// Why the last request failed.
    private(set) var failure: String?
    @ObservationIgnored var source = Locale.Language(identifier: "en")
    @ObservationIgnored fileprivate let system: AnyObject?

    static let target = Locale.Language(identifier: "zh-Hans")

    /// System translation sheet (`translationPresentation`) is available from macOS 14.4 / iOS 17.4.
    static var supportsSystemPresentation: Bool {
        if #available(macOS 14.4, iOS 17.4, *) { true } else { false }
    }

    init() {
        if #available(macOS 15.0, iOS 18.0, *) {
            system = SystemTranslator()
        } else {
            system = nil
        }
    }

    /// The language pair is downloaded, so a request won't ask for anything.
    func installed() async -> Bool {
        if #available(macOS 15.0, iOS 18.0, *) {
            return await LanguageAvailability().status(from: source, to: Self.target) == .installed
        }
        return false
    }

    func noteFailure(_ message: String?) {
        failure = message
    }

    func translate(_ texts: [String]) async -> [String?] {
        guard !texts.isEmpty else { return [] }
        if #available(macOS 15.0, iOS 18.0, *), let impl = system as? SystemTranslator {
            let (out, err) = await impl.translate(texts, source: source)
            failure = err
            return out
        }
        failure = String(localized: "当前系统不支持")
        return [String?](repeating: nil, count: texts.count)
    }

    @available(macOS 15.0, iOS 18.0, *)
    static func message(for error: Error) -> String {
        if let e = error as? TranslationError {
            switch e {
            case TranslationError.unsupportedLanguagePairing, TranslationError.unsupportedSourceLanguage, TranslationError.unsupportedTargetLanguage:
                return String(localized: "系统翻译不支持这种语言")
            case TranslationError.unableToIdentifyLanguage:
                return String(localized: "认不出是什么语言")
            default:
                break
            }
        }
        return String(localized: "翻译没成功：要先在系统设置里下载这种语言")
    }
}

@available(macOS 15.0, iOS 18.0, *)
@Observable
fileprivate final class SystemTranslator {
    var configuration: TranslationSession.Configuration?
    @ObservationIgnored private var queue: [(texts: [String], done: CheckedContinuation<([String?], String?), Never>)] = []
    @ObservationIgnored private var running = false
    @ObservationIgnored private var activeSource: Locale.Language?

    func translate(_ texts: [String], source: Locale.Language) async -> ([String?], String?) {
        await withCheckedContinuation { done in
            queue.append((texts, done))
            guard !running else { return }
            activeSource = source
            configuration = .init(source: source, target: Translator.target)
        }
    }

    func run(_ session: TranslationSession) async {
        guard !running else { return }
        running = true
        let source = activeSource ?? Locale.Language(identifier: "en")
        defer {
            running = false
            configuration = nil
        }
        if await LanguageAvailability().status(from: source, to: Translator.target) != .installed {
            do {
                try await session.prepareTranslation()
            } catch {
                failAll(Translator.message(for: error))
                return
            }
            guard await LanguageAvailability().status(from: source, to: Translator.target) == .installed else {
                failAll(String(localized: "翻译没成功：要先在系统设置里下载这种语言"))
                return
            }
        }
        while !queue.isEmpty {
            let (texts, done) = queue.removeFirst()
            do {
                var out = [String?](repeating: nil, count: texts.count)
                for r in try await session.translations(from: Self.makeRequests(texts)) {
                    if let i = r.clientIdentifier.flatMap(Int.init), out.indices.contains(i) { out[i] = r.targetText }
                }
                done.resume(returning: (out, nil))
            } catch {
                let msg = Translator.message(for: error)
                done.resume(returning: ([String?](repeating: nil, count: texts.count), msg))
                failAll(msg)
                return
            }
        }
    }

    private func failAll(_ message: String) {
        while !queue.isEmpty {
            let (texts, done) = queue.removeFirst()
            done.resume(returning: ([String?](repeating: nil, count: texts.count), message))
        }
    }

    nonisolated private static func makeRequests(_ texts: [String]) -> sending [TranslationSession.Request] {
        texts.enumerated().map { TranslationSession.Request(sourceText: $1, clientIdentifier: String($0)) }
    }
}

extension View {
    @ViewBuilder func yySystemTranslation(presented text: Binding<String?>, translator: Translator) -> some View {
        let base = Group {
            if #available(macOS 14.4, iOS 17.4, *) {
                self.translationPresentation(
                    isPresented: Binding(get: { text.wrappedValue != nil }, set: { if !$0 { text.wrappedValue = nil } }),
                    text: text.wrappedValue ?? ""
                )
            } else {
                self.onChange(of: text.wrappedValue, initial: true) { _, newValue in
                    if newValue != nil { text.wrappedValue = nil }
                }
            }
        }
        if #available(macOS 15.0, iOS 18.0, *), let impl = translator.system as? SystemTranslator {
            base.translationTask(impl.configuration) { session in await impl.run(session) }
        } else {
            base
        }
    }
}

/// 书伴逐段译文对照（含外文译中、文言译现代白话）与按章本地缓存.
nonisolated enum ParagraphTranslation {
    enum Target: Equatable, Sendable {
        /// 文言或深奥中文 → 现代简体白话文.
        case zhModern
        /// 外文 → 简体中文.
        case zhHans
        /// 中文或外文 → 英文.
        case en

        var htmlLang: String {
            switch self {
            case .zhModern, .zhHans: "zh-Hans"
            case .en: "en"
            }
        }

        var cacheTag: String {
            switch self {
            case .zhModern: "zh-baihua"
            case .zhHans: "zh-Hans"
            case .en: "en"
            }
        }

        var systemPrompt: String {
            switch self {
            case .zhModern:
                "把下面按编号排列的原文逐段译为通顺自然的现代简体白话文（若某段已是明白晓畅的现代汉语，则简明释义或保持通顺直译）。严格保留每段开头的 [编号] 标记，每段译文对应一个编号，不要遗漏、合并或输出额外说明。"
            case .zhHans:
                "把下面按编号排列的原文逐段准确、自然地译为简体中文。严格保留每段开头的 [编号] 标记，每段译文对应一个编号，不要遗漏、合并或输出额外说明。"
            case .en:
                "Translate each numbered paragraph below naturally and accurately into English. Strictly keep the leading [number] marker for each paragraph, one translation per number, without merging, skipping, or adding extra commentary."
            }
        }

        var sentencePrompt: String {
            switch self {
            case .zhModern:
                "把下面这句原文译为通顺自然的现代简体白话文，只输出译文。"
            case .zhHans:
                "把下面这句原文准确、自然地译为简体中文，只输出译文。"
            case .en:
                "Translate the following sentence naturally and accurately into English. Output only the translation."
            }
        }
    }

    static func target(texts: [String], bookLanguage: String?, uiChinese: Bool) -> Target {
        let prefix = bookLanguage?.lowercased().split(whereSeparator: { $0 == "-" || $0 == "_" }).first.map(String.init) ?? ""
        let sample = texts.prefix(8).joined()
        var hasKana = false
        var hanCount = 0
        var letterCount = 0
        for s in sample.unicodeScalars {
            if (0x3040 ... 0x30FF).contains(s.value) { hasKana = true }
            if s.properties.isIdeographic { hanCount += 1 }
            if s.properties.isAlphabetic { letterCount += 1 }
        }
        let isChineseSource = !hasKana && prefix != "ja" && prefix != "ko"
            && (prefix == "zh" || (letterCount > 0 && Double(hanCount) / Double(letterCount) >= 0.45))
        if uiChinese {
            return isChineseSource ? .zhModern : .zhHans
        }
        return (!isChineseSource && prefix == "en") ? .zhHans : .en
    }

    static func parseNumbered(_ raw: String, count: Int) -> [String?] {
        guard count > 0 else { return [] }
        let cleaned = BookCompanion.stripThinking(raw).visible.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !cleaned.isEmpty else { return [String?](repeating: nil, count: count) }
        var out = [String?](repeating: nil, count: count)
        var currentIndex: Int?
        var buffer: [String] = []
        func flush() {
            guard let idx = currentIndex, out.indices.contains(idx) else { return }
            let joined = buffer.joined(separator: "\n").trimmingCharacters(in: .whitespacesAndNewlines)
            if !joined.isEmpty { out[idx] = joined }
        }
        for line in cleaned.components(separatedBy: .newlines) {
            if let (num, rest) = matchBracketTag(line), (1 ... count).contains(num) {
                flush()
                currentIndex = num - 1
                buffer = rest.isEmpty ? [] : [rest]
            } else if currentIndex != nil {
                buffer.append(line)
            }
        }
        flush()
        if out.contains(where: { $0 != nil }) { return out }
        if count == 1 {
            let single = stripSinglePrefix(cleaned)
            return [single.isEmpty ? nil : single]
        }
        return out
    }

    private static func matchBracketTag(_ line: String) -> (Int, String)? {
        let trimmed = line.drop(while: { $0 == " " || $0 == "\t" })
        let open: Character
        let close: Character
        if trimmed.first == "[" {
            open = "["; close = "]"
        } else if trimmed.first == "【" {
            open = "【"; close = "】"
        } else {
            return nil
        }
        guard trimmed.first == open else { return nil }
        let afterOpen = trimmed.dropFirst()
        let digits = afterOpen.prefix(while: \.isWholeNumber)
        guard !digits.isEmpty, let num = Int(digits) else { return nil }
        let afterDigits = afterOpen.dropFirst(digits.count)
        guard afterDigits.first == close else { return nil }
        let rest = afterDigits.dropFirst().drop(while: { $0 == " " || $0 == "\t" || $0 == ":" || $0 == "：" })
        return (num, String(rest))
    }

    private static func stripSinglePrefix(_ text: String) -> String {
        if let (_, rest) = matchBracketTag(text), !rest.isEmpty {
            return rest.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return text
    }

    static func cacheFile(dataDir: URL?, bookID: String, section: UInt32, target: Target) -> URL? {
        dataDir?
            .appending(path: "translations", directoryHint: .isDirectory)
            .appending(path: bookID, directoryHint: .isDirectory)
            .appending(path: "s\(section)-\(target.cacheTag).json")
    }

    @concurrent static func loadCache(at url: URL?) async -> [String: String] {
        guard let url, let data = try? Data(contentsOf: url),
              let dict = try? JSONDecoder().decode([String: String].self, from: data)
        else { return [:] }
        return dict
    }

    @concurrent static func saveCache(_ dict: [String: String], to url: URL?) async {
        guard let url, !dict.isEmpty else { return }
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(dict) {
            try? data.write(to: url, options: .atomic)
        }
    }

    static func batches(of texts: [String], maxItems: Int = 10, maxChars: Int = 2600) -> [[String]] {
        var result: [[String]] = []
        var current: [String] = []
        var chars = 0
        for t in texts where !t.isEmpty {
            if !current.isEmpty && (current.count >= maxItems || chars + t.count > maxChars) {
                result.append(current)
                current = []
                chars = 0
            }
            current.append(t)
            chars += t.count
        }
        if !current.isEmpty { result.append(current) }
        return result
    }
}

/// Reading translation display mode: original only, side-by-side bilingual, or translation only.
enum TranslateMode: String, CaseIterable, Sendable {
    case off
    case bilingual
    case only
}

extension ReaderModel {
    /// Read in a language other than Chinese: words can be looked up and translated.
    var isForeign: Bool {
        guard let language = detail?.summary.language.lowercased(), !language.isEmpty else { return false }
        return !["zh", "ja", "ko", "und"].contains { language.hasPrefix($0) }
    }

    func setUpForeign() {
        guard detail?.summary.format != .pdf else { return }
        if let language = detail?.summary.language, isForeign { translator.source = Locale.Language(identifier: language) }
        pager.translator = { [weak self] section, texts in
            guard let self else { return ([], "zh-Hans") }
            return await self.translateParagraphs(section: section, texts: texts)
        }
        pager.onTranslateFailed = { [weak self] in
            guard let self else { return }
            self.setTranslateShown(false)
            let fallback = self.isForeign
                ? String(localized: "翻译没成功：要先在系统设置里下载这种语言，或在「设置 · 书伴」配好模型")
                : String(localized: "先在「设置 · 书伴」里配好模型，才能译文对照")
            let msg = self.translator.failure ?? fallback
            self.show(Library.Toast(icon: "exclamationmark.circle", title: msg, detail: ""))
        }
        // 译 left on last time comes back only when it can run as it is: a book opening (or the app
        // restoring its windows) never puts the system's download sheet up unasked.
        let savedMode: TranslateMode? = {
            if let raw = UserDefaults.standard.string(forKey: translateModeKey), let m = TranslateMode(rawValue: raw) {
                return m == .off ? nil : m
            }
            return UserDefaults.standard.bool(forKey: translateKey) ? .bilingual : nil
        }()
        if let savedMode {
            let aiReady = (ForeignPrefs.aiTranslate || !isForeign) && CompanionConfig.current.isReady
            if aiReady {
                setTranslateMode(savedMode)
            } else if isForeign {
                Task { if await translator.installed() { setTranslateMode(savedMode) } }
            }
        }
        guard isForeign else { return }
        pager.lookup = true
        let core = core, bookID = bookID
        // 难词注释 is only for English: the list is English–Chinese.
        if detail?.summary.language.lowercased().hasPrefix("en") == true {
            pager.glosser = { section in
                guard ForeignPrefs.glosses else { return [] }
                let list = (try? core.glosses(bookId: bookID, section: section, level: ForeignPrefs.level, vocabOnly: ForeignPrefs.glossVocabOnly)) ?? []
                return list.map { ["form": $0.form, "gloss": $0.gloss] }
            }
        }
        loadVocab()
    }

    private func translateParagraphs(section: UInt32, texts: [String]) async -> (texts: [String?], lang: String) {
        guard !texts.isEmpty else { return ([], "zh-Hans") }
        let uiChinese = (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh")
        let target = ParagraphTranslation.target(texts: texts, bookLanguage: detail?.summary.language, uiChinese: uiChinese)
        let config = CompanionConfig.current
        let useAI = (ForeignPrefs.aiTranslate || !isForeign) && config.isReady
        if useAI {
            let cacheURL = ParagraphTranslation.cacheFile(dataDir: library?.dataDir, bookID: bookID, section: section, target: target)
            var cache = await ParagraphTranslation.loadCache(at: cacheURL)
            var seen = Set<String>()
            let missing = texts.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
                .filter { !$0.isEmpty && cache[$0] == nil && seen.insert($0).inserted }
            var aiError: String?
            for batch in ParagraphTranslation.batches(of: missing) {
                if Task.isCancelled { break }
                let prompt = batch.enumerated().map { "[\($0.offset + 1)] \($0.element)" }.joined(separator: "\n\n")
                do {
                    let raw = try await Companion.answer(config, system: target.systemPrompt, prompt: prompt, maxTokens: 2048, fast: true, source: .companion)
                    let parsed = ParagraphTranslation.parseNumbered(raw, count: batch.count)
                    var wrote = false
                    for (src, dst) in zip(batch, parsed) {
                        if let dst, !dst.isEmpty {
                            cache[src] = dst
                            wrote = true
                        }
                    }
                    if wrote { await ParagraphTranslation.saveCache(cache, to: cacheURL) }
                } catch {
                    aiError = Companion.message(for: error)
                    break
                }
            }
            let mapped = texts.map { cache[$0.trimmingCharacters(in: .whitespacesAndNewlines)] }
            if mapped.contains(where: { $0 != nil }) {
                translator.noteFailure(nil)
                return (mapped, target.htmlLang)
            }
            translator.noteFailure(aiError)
            if isForeign, await translator.installed() {
                return (await translator.translate(texts), "zh-Hans")
            }
            return ([String?](repeating: nil, count: texts.count), target.htmlLang)
        }
        guard isForeign else {
            translator.noteFailure(String(localized: "先在「设置 · 书伴」里配好模型，才能译文对照"))
            return ([String?](repeating: nil, count: texts.count), target.htmlLang)
        }
        return (await translator.translate(texts), "zh-Hans")
    }

    private var translateKey: String { "translate.\(bookID)" }
    private var translateModeKey: String { "translateMode.\(bookID)" }

    /// 生词本 words still being learned, for the dotted line.
    func loadVocab() {
        guard isForeign else { return }
        let words = (try? core.words()) ?? []
        pager.setVocab(ForeignPrefs.marks ? words.filter { !$0.known }.map(\.word) : [])
        pager.refreshGlosses()
        let here = words.filter { $0.book == bookID }.sorted { ($0.section, $0.sentence) < ($1.section, $1.sentence) }
        if here != bookWords { bookWords = here }
    }

    /// 译: switches between 原文 (.off), 译文对照 (.bilingual), and 仅译文 (.only).
    func setTranslateMode(_ mode: TranslateMode) {
        if mode != .off, !isForeign, !CompanionConfig.current.isReady {
            show(Library.Toast(icon: "exclamationmark.circle", title: String(localized: "先在「设置 · 书伴」里配好模型，才能译文对照"), detail: ""))
            return
        }
        translateMode = mode
        if mode != .off { lastActiveTranslateMode = mode }
        UserDefaults.standard.set(mode != .off, forKey: translateKey)
        UserDefaults.standard.set(mode.rawValue, forKey: translateModeKey)
        pager.setTranslating(mode != .off, only: mode == .only)
    }

    /// 译 in the tools: toggles between 原文 and the last active translation mode.
    func setTranslateShown(_ on: Bool) {
        setTranslateMode(on ? (lastActiveTranslateMode == .off ? .bilingual : lastActiveTranslateMode) : .off)
    }

    // MARK: 点词

    /// Says the word and opens its card.
    func look(up word: ReaderMessage.Word, quiet: Bool = false) {
        guard let section = pager.current?.section else { return }
        dismissCards()
        sentenceTranslation = nil
        // The dictionaries the reader imported first, then the system's.
        let entry = (try? core.lookUp(word: word.text)) ?? nil
        let definition = entry.map { WordDefinition(headword: $0.headword, phonetic: nil, partOfSpeech: nil, meaning: $0.summary) }
            ?? SystemDictionary.define(word.text)
        var l = WordLookup(word: word.text, section: section, start: word.start, end: word.end, context: word.sentence,
                           rect: CGRect(x: word.rect.x, y: word.rect.y, width: word.rect.width, height: word.rect.height),
                           definition: definition)
        l.dictionary = entry?.dictionary
        l.html = entry?.html
        let words = (try? core.words()) ?? []
        l.kept = words.contains { $0.word.lowercased() == l.headword.lowercased() && !$0.known }
        l.count = (try? core.bookWordCount(book: bookID, word: l.headword)).map(Int.init)
        lookup = l
        pager.setLooking(section: section, start: word.start, end: word.end)
        if !quiet, ForeignPrefs.speaks { say(word.text) }
        if definition == nil {
            Task {
                guard await translator.installed() else { return }
                let gloss = await translator.translate([word.text]).first ?? nil
                if lookup?.word == word.text, lookup?.section == section { lookup?.gloss = gloss }
            }
        }
    }

    func closeLookup() {
        guard lookup != nil else { return }
        lookup = nil
        pager.clearLooking()
    }

    /// The word, read in the book's language.
    func say(_ text: String) {
        let speaker = speaker ?? AVSpeechSynthesizer()
        self.speaker = speaker
        speaker.stopSpeaking(at: .immediate)
        let u = AVSpeechUtterance(string: text)
        u.voice = ForeignPrefs.voice(for: detail?.summary.language)
        u.rate = AVSpeechUtteranceDefaultSpeechRate * 0.9
        speaker.speak(u)
    }

    /// 加入生词本, or out of it again.
    func keep(_ on: Bool) {
        guard var l = lookup else { return }
        do {
            if on {
                let meaning = l.definition?.line ?? l.gloss
                _ = try core.addWord(word: l.headword, phonetic: l.definition?.phonetic, meaning: meaning, book: bookID,
                                     section: l.section, sentence: UInt32(max(0, l.start.s)), context: l.context)
                show(Library.Toast(icon: "character.book.closed", title: String(localized: "加入了生词本"), detail: l.headword))
            } else {
                try core.deleteWord(word: l.headword)
            }
            l.kept = on
            lookup = l
            NotificationCenter.default.post(name: .vocabChanged, object: nil)
        } catch {
            failure = Library.message(for: error)
        }
    }

    // MARK: 双击一句

    func pager(_ pager: ReaderPager, doubleTapped m: ReaderMessage) {
        #if os(iOS)
        // 双击屏幕边缘进出沉浸: the outer eighth on either side, where no words are.
        if let x = m.x.map({ CGFloat($0) }), pager.bounds.width > 0, x < pager.bounds.width * 0.12 || x > pager.bounds.width * 0.88 {
            chromeShown = false
            immersive.toggle()
            return
        }
        #endif
        guard isForeign, let s = m.sentence, let section = pager.current?.section else { return }
        dismissCards()
        closeLookup()
        let sentences = (try? core.sentences(bookId: bookID, section: section)) ?? []
        guard sentences.indices.contains(s) else { return }
        let text = sentences[s].trimmingCharacters(in: .whitespacesAndNewlines)
        sentenceTranslation = SentenceTranslation(section: section, sentence: s, text: text, at: CGPoint(x: m.x ?? 0, y: m.y ?? 0))
        Task {
            var out: String?
            var errMsg: String?
            let cfg = CompanionConfig.current
            if ForeignPrefs.aiTranslate, cfg.isReady {
                let uiChinese = (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh")
                let target = ParagraphTranslation.target(texts: [text], bookLanguage: detail?.summary.language, uiChinese: uiChinese)
                do {
                    let raw = try await Companion.answer(cfg, system: target.sentencePrompt, prompt: text, maxTokens: 512, fast: true, source: .companion)
                    out = BookCompanion.stripThinking(raw).visible.trimmingCharacters(in: .whitespacesAndNewlines)
                    if out?.isEmpty == true { out = nil }
                } catch {
                    errMsg = Companion.message(for: error)
                }
            }
            if out == nil {
                out = await translator.translate([text]).first ?? nil
            }
            guard sentenceTranslation?.sentence == s, sentenceTranslation?.section == section else { return }
            if let out {
                sentenceTranslation?.translated = out
            } else {
                sentenceTranslation?.failed = errMsg ?? translator.failure ?? String(localized: "翻译没成功")
            }
        }
    }
}

extension Notification.Name {
    /// A word was put in or taken out of the 生词本.
    static let vocabChanged = Notification.Name("yuyue.vocabChanged")
}
