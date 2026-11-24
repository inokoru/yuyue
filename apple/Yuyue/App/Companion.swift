import Foundation
#if canImport(FoundationModels)
import FoundationModels
#endif

/// Where 书伴's answers come from: the model on this device, or a service the
/// user added with their own key. No service of ours sits in between; keys stay in the
/// app's own store and are not synced. 添加服务商 picks from `catalog`, grouped by how it's reached
/// (登录 / API Key / 本机和局域网), or adds one of the user's own
/// (any OpenAI-shaped address). The logos are in shared/provider-icons.
struct CompanionProvider: Hashable, Identifiable {
    /// How it's asked.
    enum Kind { case apple, anthropic, openAI }
    /// Its group in 添加服务商: how it's reached.
    enum Group: CaseIterable {
        /// Signed in to on its own page (OpenRouter's sign-in for apps); a key can still be pasted.
        case signIn
        case key
        /// A model or gateway on this computer or the network: Ollama, LM Studio, magpie.
        case local

        var title: String {
            switch self {
            case .signIn: String(localized: "登录账号")
            case .key: "API Key"
            case .local: String(localized: "本机和局域网")
            }
        }
    }

    let id: String
    let title: String
    var kind = Kind.openAI
    var group = Group.key
    /// Who the bill goes to, for the key form.
    var account = String(localized: "服务商")
    var defaultModel = ""
    /// Lighter/cheaper model for bulk fast-tier tasks (e.g. scene attribution, homophone polish).
    var defaultFastModel = ""
    var defaultURL: URL?
    var keyHint = "sk-…"
    /// What else it's searched by: its company, its Chinese name.
    var aliases = ""
    /// A line under its name in 添加服务商.
    var blurb: String?
    /// Sent when no key is filled in: magpie's gateway takes any.
    var defaultKey = ""

    static let apple = CompanionProvider(id: "apple", title: String(localized: "Apple 智能"), kind: .apple, group: .local)

    /// What 添加服务商 offers, in its order. The OpenAI-shaped ones differ only in address.
    static let catalog: [CompanionProvider] = [
        .init(id: "openrouter", title: "OpenRouter", group: .signIn, account: "OpenRouter", defaultModel: "openrouter/auto",
              defaultURL: URL(string: "https://openrouter.ai/api/v1/"), keyHint: "sk-or-…",
              blurb: String(localized: "登录就能用，几百个模型一个账号")),
        .init(id: "claude", title: "Claude", kind: .anthropic, account: "Anthropic", defaultModel: "claude-sonnet-4-5", defaultFastModel: "claude-haiku-4-5",
              defaultURL: URL(string: "https://api.anthropic.com/v1/"), keyHint: "sk-ant-…", aliases: "anthropic 克劳德"),
        .init(id: "openai", title: "OpenAI", account: "OpenAI", defaultModel: "gpt-5-mini", defaultFastModel: "gpt-5-mini",
              defaultURL: URL(string: "https://api.openai.com/v1/"), aliases: "chatgpt gpt"),
        .init(id: "gemini", title: "Gemini", account: "Google", defaultModel: "gemini-2.5-flash", defaultFastModel: "gemini-2.5-flash",
              defaultURL: URL(string: "https://generativelanguage.googleapis.com/v1beta/openai/"), keyHint: "AIza…", aliases: "google 谷歌"),
        .init(id: "deepseek", title: "DeepSeek", account: "DeepSeek", defaultModel: "deepseek-chat", defaultFastModel: "deepseek-chat",
              defaultURL: URL(string: "https://api.deepseek.com/v1/"), aliases: "深度求索"),
        .init(id: "dashscope", title: String(localized: "通义千问"), account: String(localized: "阿里云百炼"), defaultModel: "qwen-plus", defaultFastModel: "qwen-turbo",
              defaultURL: URL(string: "https://dashscope.aliyuncs.com/compatible-mode/v1/"), aliases: "qwen 阿里 百炼 dashscope"),
        .init(id: "moonshot", title: "Kimi", account: String(localized: "月之暗面"), defaultModel: "kimi-k2-turbo-preview", defaultFastModel: "kimi-k2-turbo-preview",
              defaultURL: URL(string: "https://api.moonshot.cn/v1/"), aliases: "moonshot 月之暗面"),
        .init(id: "zhipu", title: String(localized: "智谱 GLM"), account: String(localized: "智谱"), defaultModel: "glm-4.6", defaultFastModel: "glm-4-flash",
              defaultURL: URL(string: "https://open.bigmodel.cn/api/paas/v4/"), aliases: "zhipu glm z.ai 清言"),
        .init(id: "volcengine", title: String(localized: "豆包"), account: String(localized: "火山引擎"), defaultModel: "doubao-seed-1-6-250615", defaultFastModel: "doubao-seed-1-6-flash-250615",
              defaultURL: URL(string: "https://ark.cn-beijing.volces.com/api/v3/"), aliases: "doubao 火山引擎 volcengine 字节"),
        .init(id: "siliconflow", title: String(localized: "硅基流动"), account: String(localized: "硅基流动"), defaultModel: "deepseek-ai/DeepSeek-V3", defaultFastModel: "deepseek-ai/DeepSeek-V3",
              defaultURL: URL(string: "https://api.siliconflow.cn/v1/"), aliases: "siliconflow"),
        .init(id: "minimax", title: "MiniMax", account: "MiniMax", defaultModel: "MiniMax-M2", defaultFastModel: "MiniMax-M2",
              defaultURL: URL(string: "https://api.minimaxi.com/v1/"), aliases: "海螺 稀宇"),
        .init(id: "xai", title: "Grok", account: "xAI", defaultModel: "grok-4", defaultFastModel: "grok-3-mini",
              defaultURL: URL(string: "https://api.x.ai/v1/"), keyHint: "xai-…", aliases: "xai"),
        .init(id: "mistral", title: "Mistral", account: "Mistral", defaultModel: "mistral-small-latest", defaultFastModel: "mistral-small-latest",
              defaultURL: URL(string: "https://api.mistral.ai/v1/"), keyHint: ""),
        .init(id: "groq", title: "Groq", account: "Groq", defaultModel: "llama-3.3-70b-versatile", defaultFastModel: "llama-3.1-8b-instant",
              defaultURL: URL(string: "https://api.groq.com/openai/v1/"), keyHint: "gsk_…"),
        // On a phone "localhost" is the phone: the address is left to fill.
        .init(id: "magpie", title: "magpie", group: .local, defaultURL: local(3425, host: "127.0.0.1"),
              keyHint: String(localized: "局域网里用才要填网关 Key"), aliases: "喜鹊 网关 gateway",
              blurb: String(localized: "连接本机或局域网中的 magpie 模型网关"), defaultKey: "magpie"),
        .init(id: "ollama", title: "Ollama", group: .local, defaultURL: local(11434), keyHint: String(localized: "不用填")),
        .init(id: "lmstudio", title: "LM Studio", group: .local, defaultURL: local(1234), keyHint: String(localized: "不用填")),
    ]

    private static func local(_ port: Int, host: String = "localhost") -> URL? {
        #if os(macOS)
        URL(string: "http://\(host):\(port)/v1/")
        #else
        nil
        #endif
    }

    /// One of the user's own: any OpenAI-shaped address, under a name they give it.
    static func custom(_ title: String, id: String = "custom." + UUID().uuidString.prefix(8).lowercased()) -> CompanionProvider {
        CompanionProvider(id: id, title: title, keyHint: String(localized: "可不填"))
    }

    var isCustom: Bool { id.hasPrefix("custom.") }

    /// Local models and the user's own addresses may go without.
    var needsKey: Bool { group != .local && !isCustom && kind != .apple }

    /// Whether calls to this provider typically bill the user's cloud API account.
    var isPaidCloud: Bool { kind != .apple && group != .local }

    /// Its logo in the asset catalogue (`scripts/provider-icons.py`); the user's own have none.
    var logo: String? { isCustom || kind == .apple ? nil : "provider-\(id)" }

    var modelHint: String { defaultModel.isEmpty ? String(localized: "比如 qwen3:8b") : defaultModel }

    var urlHint: String {
        if let defaultURL { return defaultURL.absoluteString }
        if id == "magpie" { return String(localized: "比如 http://192.168.1.5:3425/v1") }
        return group == .local ? String(localized: "比如 http://192.168.1.5:11434/v1") : "https://…/v1"
    }

    var detail: String? {
        kind == .apple ? String(localized: "在这台设备上，不联网") : nil
    }

    /// The first letter, on its little square.
    var mark: String { String(title.prefix(1)).uppercased() }

    /// A catalog entry or one of the user's own, by its id.
    static func find(_ id: String) -> CompanionProvider? {
        if id == apple.id { return apple }
        if let p = catalog.first(where: { $0.id == id }) { return p }
        guard id.hasPrefix("custom."), let name = UserDefaults.standard.string(forKey: "companion.name.\(id)") else { return nil }
        return custom(name, id: id)
    }

    /// The services the user added, in the order they were added.
    static var added: [CompanionProvider] {
        (UserDefaults.standard.stringArray(forKey: "companion.added") ?? []).compactMap(find)
    }

    /// `catalog` less what's already added, matching what's typed in its name, id or aliases.
    static func available(matching query: String) -> [CompanionProvider] {
        let taken = Set(added.map(\.id))
        let q = query.lowercased().filter { !" -_.".contains($0) }
        return catalog.filter { p in
            !taken.contains(p.id) && (q.isEmpty || "\(p.id)\(p.title)\(p.aliases)".lowercased().filter { $0 != " " }.contains(q))
        }
    }
}

/// The chosen engine and how to reach it.
struct CompanionConfig: Equatable {
    var provider: CompanionProvider
    var key: String
    var model: String
    /// Empty: the service's own address.
    var url: String
    /// Optional lightweight model override for fast-tier batch tasks; empty uses automatic pairing.
    var fastModel: String = ""

    static var current: CompanionConfig {
        get {
            let id = UserDefaults.standard.string(forKey: "companion.provider") ?? ""
            let p = CompanionProvider.added.first { $0.id == id } ?? .apple
            return saved(p)
        }
        set {
            newValue.save()
            UserDefaults.standard.set(newValue.provider.id, forKey: "companion.provider")
        }
    }

    /// What was filled in for a provider before, or its defaults.
    static func saved(_ p: CompanionProvider) -> CompanionConfig {
        let d = UserDefaults.standard
        let model = d.string(forKey: "companion.model.\(p.id)").flatMap { $0.isEmpty ? nil : $0 } ?? p.defaultModel
        return CompanionConfig(provider: p, key: Keychain.read("companion.key.\(p.id)") ?? "", model: model,
                               url: d.string(forKey: "companion.url.\(p.id)") ?? "",
                               fastModel: d.string(forKey: "companion.fastModel.\(p.id)") ?? "")
    }

    /// Adds the service, or keeps the changes to one added before.
    func save() {
        guard provider.kind != .apple else { return }
        let d = UserDefaults.standard
        let id = provider.id
        if provider.isCustom { d.set(provider.title, forKey: "companion.name.\(id)") }
        d.set(model, forKey: "companion.model.\(id)")
        d.set(url, forKey: "companion.url.\(id)")
        if fastModel.isEmpty { d.removeObject(forKey: "companion.fastModel.\(id)") } else { d.set(fastModel, forKey: "companion.fastModel.\(id)") }
        if key.isEmpty { Keychain.delete("companion.key.\(id)") } else { Keychain.write("companion.key.\(id)", key) }
        let ids = CompanionProvider.added.map(\.id)
        if !ids.contains(id) { d.set(ids + [id], forKey: "companion.added") }
    }

    /// Forgets the service and its key. If it was answering, the device's own takes over.
    static func remove(_ p: CompanionProvider) {
        let d = UserDefaults.standard
        d.set(CompanionProvider.added.map(\.id).filter { $0 != p.id }, forKey: "companion.added")
        for k in ["model", "fastModel", "url", "name"] { d.removeObject(forKey: "companion.\(k).\(p.id)") }
        Keychain.delete("companion.key.\(p.id)")
        if d.string(forKey: "companion.provider") == p.id { d.removeObject(forKey: "companion.provider") }
    }

    /// The lighter model used for bulk fast-tier tasks (scene attribution, homophone polish, recap),
    /// or nil when the primary model is already lightweight or no pairing applies.
    var resolvedFastModel: String? {
        guard provider.kind != .apple else { return nil }
        let manual = fastModel.trimmingCharacters(in: .whitespacesAndNewlines)
        if !manual.isEmpty { return manual == model ? nil : manual }
        let lower = model.lowercased()
        // Already a lightweight/cheap model: keep as-is.
        if lower.contains("haiku") || lower.contains("mini") || lower.contains("nano")
            || lower.contains("flash") || lower.contains("turbo") || lower.contains("small")
            || lower.contains("instant") || lower.contains("lite") {
            return nil
        }
        if provider.id == "openrouter" {
            if lower.hasPrefix("anthropic/claude-") { return "anthropic/claude-haiku-4-5" }
            if lower.hasPrefix("openai/gpt-5") { return "openai/gpt-5-mini" }
            if lower.hasPrefix("openai/gpt-4o") { return "openai/gpt-4o-mini" }
            if lower.hasPrefix("google/gemini-2.5-pro") || lower.hasPrefix("google/gemini-pro") { return "google/gemini-2.5-flash" }
            if lower.hasPrefix("deepseek/deepseek-r1") { return "deepseek/deepseek-chat" }
            return nil
        }
        let candidate = provider.defaultFastModel
        return !candidate.isEmpty && candidate != model ? candidate : nil
    }

    /// Returns a copy routed to the lighter model when `fast` is requested and tier routing is on.
    func routed(fast: Bool) -> CompanionConfig {
        guard fast, CompanionUsage.tierRoutingEnabled, let lighter = resolvedFastModel else { return self }
        var copy = self
        copy.model = lighter
        return copy
    }

    /// Its key and address are in: enough to add it, and to list its models.
    var isConnected: Bool {
        switch provider.kind {
        case .apple: return Companion.appleAvailability == nil
        default:
            let typed = url.trimmingCharacters(in: .whitespaces)
            let reachable = typed.isEmpty ? provider.defaultURL != nil : URL(string: typed)?.scheme?.hasPrefix("http") == true
            return reachable && (!provider.needsKey || !key.isEmpty) && !provider.title.isEmpty
        }
    }

    /// Filled in enough to ask: connected, and a model chosen.
    var isReady: Bool { isConnected && (provider.kind == .apple || !model.isEmpty) }

    /// What goes in the request: the key typed in, or the one the service takes without one.
    var sentKey: String { key.isEmpty ? provider.defaultKey : key }

    /// "sk-ant-…3f9a": the saved key, shown without giving it away.
    var maskedKey: String {
        guard key.count > 8 else { return String(repeating: "•", count: key.count) }
        return String(key.prefix(key.hasPrefix("sk-") ? 3 : 0)) + "…" + String(key.suffix(4))
    }

    /// "Apple 智能 · 在这台 Mac 上", "DeepSeek · deepseek-chat": under each answer.
    var label: String {
        switch provider.kind {
        case .apple:
            #if os(macOS)
            String(localized: "Apple 智能 · 在这台 Mac 上")
            #else
            String(localized: "Apple 智能 · 在这台设备上")
            #endif
        default: "\(provider.title) · \(model)"
        }
    }
}

/// 测试一下: one short question, how long it took or what went wrong.
enum CompanionTest {
    static func run(_ config: CompanionConfig) async -> Result<TimeInterval, Error> {
        let start = Date()
        do {
            _ = try await Companion.answer(config, system: String(localized: "你是一个简短的助手。"), prompt: String(localized: "用一个词回答：天空通常是什么颜色？"))
            return .success(Date().timeIntervalSince(start))
        } catch {
            return .failure(error)
        }
    }
}

enum CompanionError: LocalizedError {
    case notReady
    case unavailable(String)
    case badKey
    case noCredit
    case budgetExceeded(String)
    case busy
    case status(Int, String?)
    case empty
    /// Reached, but no model to ask: the service listed none and none was typed.
    case noModel

    var errorDescription: String? {
        switch self {
        case .notReady: String(localized: "书伴还没接上：在设置 · 书伴里添加一个服务商")
        case .unavailable(let why): why
        case .badKey: String(localized: "Key 不对，去设置 · 书伴里重新填")
        case .noCredit: String(localized: "账号余额不够了")
        case .budgetExceeded(let detail): String(localized: "已达到今日用量上限（\(detail)），已自动暂停。可在「设置 · 书伴」中调整。")
        case .busy: String(localized: "问得太快了，等一会儿再问")
        case .status(let code, let message): message.map { String(localized: "服务器回了 \(code)：\($0)") } ?? String(localized: "服务器回了 \(code)")
        case .empty: String(localized: "没有回答，换个问法试试")
        case .noModel: String(localized: "未选择模型")
        }
    }
}

/// Tracks real 30-day LLM token & Cloud TTS character usage, cache hit breakdown, source
/// attribution, and daily budget limits so BYOK costs stay transparent and bounded.
nonisolated enum CompanionUsage {
    enum Source: String, CaseIterable, Sendable {
        case drama
        case companion
        case polish
        case recap
        case podcast
        case other

        var title: String {
            switch self {
            case .drama: String(localized: "广播剧认人")
            case .companion: String(localized: "书伴问答")
            case .polish: String(localized: "错字整理")
            case .recap: String(localized: "前情提要")
            case .podcast: String(localized: "播客")
            case .other: String(localized: "其他")
            }
        }
    }

    struct DayRecord: Codable, Sendable, Equatable, Identifiable {
        let date: String
        var cachedInput: Int = 0
        var missInput: Int = 0
        var output: Int = 0
        var requests: Int = 0
        var sources: [String: Int] = [:]
        var ttsChars: Int = 0
        var ttsSavedChars: Int = 0

        var id: String { date }
        var totalTokens: Int { cachedInput + missInput + output }
        var totalInput: Int { cachedInput + missInput }
        var cacheHitRate: Double? {
            totalInput > 0 ? Double(cachedInput) / Double(totalInput) : nil
        }
        var shortLabel: String {
            let parts = date.split(separator: "-")
            guard parts.count == 3, let m = Int(parts[1]), let d = Int(parts[2]) else { return date }
            return "\(m)/\(d)"
        }
    }

    struct SourceShare: Sendable, Equatable {
        let source: Source
        let tokens: Int
        let share: Double
    }

    struct Snapshot30Days: Sendable, Equatable {
        let days: [DayRecord]
        let totalTokens: Int
        let cachedInputTokens: Int
        let missInputTokens: Int
        let outputTokens: Int
        let peakTokens: Int
        let peakIndex: Int
        let averageTokens: Double
        let sources: [SourceShare]
        let ttsChars: Int
        let ttsSavedChars: Int

        static let empty = Snapshot30Days(
            days: [],
            totalTokens: 0,
            cachedInputTokens: 0,
            missInputTokens: 0,
            outputTokens: 0,
            peakTokens: 0,
            peakIndex: 0,
            averageTokens: 0,
            sources: [],
            ttsChars: 0,
            ttsSavedChars: 0
        )

        var cacheHitRate: Double? {
            let totalIn = cachedInputTokens + missInputTokens
            return totalIn > 0 ? Double(cachedInputTokens) / Double(totalIn) : nil
        }
    }

    static let changed = Notification.Name("yuyue.companionUsageChanged")
    private static let lock = Mutex<[String: DayRecord]?>(nil)

    private enum Keys {
        static let ledger = "usage.days30.v1"
        static let tierRouting = "companion.cost.tierRouting"
        static let dailyTokenLimit = "companion.cost.dailyTokenLimit"
        static let dailyTtsCharLimit = "companion.cost.dailyTtsCharLimit"
        static let cloudSaveNarrator = "drama.cost.cloudSaveNarrator"
    }

    /// Route bulk fast-tier tasks (scene attribution, homophone polish, recap) to a lighter model. Default: on.
    static var tierRoutingEnabled: Bool {
        get { UserDefaults.standard.object(forKey: Keys.tierRouting) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: Keys.tierRouting); notify() }
    }

    /// Daily token cap for paid cloud LLMs (0 = unlimited).
    static var dailyTokenLimit: Int {
        get { UserDefaults.standard.integer(forKey: Keys.dailyTokenLimit) }
        set { UserDefaults.standard.set(max(0, newValue), forKey: Keys.dailyTokenLimit); notify() }
    }

    /// Daily character cap for paid cloud TTS (0 = unlimited).
    static var dailyTtsCharLimit: Int {
        get { UserDefaults.standard.integer(forKey: Keys.dailyTtsCharLimit) }
        set { UserDefaults.standard.set(max(0, newValue), forKey: Keys.dailyTtsCharLimit); notify() }
    }

    /// Keep narrator on free local voice when casting cloud drama. Default: on.
    static var cloudSaveNarrator: Bool {
        get { UserDefaults.standard.object(forKey: Keys.cloudSaveNarrator) as? Bool ?? true }
        set { UserDefaults.standard.set(newValue, forKey: Keys.cloudSaveNarrator); notify() }
    }

    private static func dateKey(_ date: Date = Date()) -> String {
        let cal = Calendar(identifier: .gregorian)
        let c = cal.dateComponents([.year, .month, .day], from: date)
        return String(format: "%04d-%02d-%02d", c.year ?? 2026, c.month ?? 1, c.day ?? 1)
    }

    private static func last30DateKeys(now: Date = Date()) -> [String] {
        let cal = Calendar(identifier: .gregorian)
        let startOfToday = cal.startOfDay(for: now)
        return (-29 ... 0).compactMap { offset in
            cal.date(byAdding: .day, value: offset, to: startOfToday).map(dateKey)
        }
    }

    private static func loadLedgerLocked(_ cached: inout [String: DayRecord]?, from d: UserDefaults) -> [String: DayRecord] {
        if let existing = cached { return existing }
        guard let data = d.data(forKey: Keys.ledger),
              let arr = try? JSONDecoder().decode([DayRecord].self, from: data) else {
            cached = [:]
            return [:]
        }
        let map = Dictionary(uniqueKeysWithValues: arr.map { ($0.date, $0) })
        cached = map
        return map
    }

    private static func saveLedgerLocked(_ map: [String: DayRecord], cached: inout [String: DayRecord]?, to d: UserDefaults) {
        let keep = Set(last30DateKeys())
        let pruned = map.values.filter { keep.contains($0.date) }.sorted { $0.date < $1.date }
        cached = Dictionary(uniqueKeysWithValues: pruned.map { ($0.date, $0) })
        if let data = try? JSONEncoder().encode(pruned) {
            d.set(data, forKey: Keys.ledger)
        }
    }

    static func checkLlmBudget(paid: Bool) throws {
        guard paid else { return }
        let limit = dailyTokenLimit
        guard limit > 0 else { return }
        let today = dateKey()
        let used = lock.withLock { cached -> Int in
            let map = loadLedgerLocked(&cached, from: UserDefaults.standard)
            return map[today]?.totalTokens ?? 0
        }
        if used >= limit {
            throw CompanionError.budgetExceeded(formatCount(limit) + " token")
        }
    }

    static func recordLlm(input: Int, output: Int, cached: Int, paid: Bool, source: Source = .other) {
        guard paid, input > 0 || output > 0 || cached > 0 else { return }
        let today = dateKey()
        lock.withLock { store in
            let d = UserDefaults.standard
            var map = loadLedgerLocked(&store, from: d)
            var rec = map[today] ?? DayRecord(date: today)
            let c = max(0, cached)
            let m = max(0, input)
            let o = max(0, output)
            rec.cachedInput += c
            rec.missInput += m
            rec.output += o
            rec.requests += 1
            rec.sources[source.rawValue, default: 0] += c + m + o
            map[today] = rec
            saveLedgerLocked(map, cached: &store, to: d)
        }
        notify()
    }

    static func checkTtsBudget(chars: Int) throws {
        let limit = dailyTtsCharLimit
        guard limit > 0 else { return }
        let today = dateKey()
        let used = lock.withLock { cached -> Int in
            let map = loadLedgerLocked(&cached, from: UserDefaults.standard)
            return map[today]?.ttsChars ?? 0
        }
        if used + chars > limit {
            throw CloudVoice.Failure.budgetExceeded(formatCount(limit) + String(localized: "字"))
        }
    }

    static func recordTts(chars: Int, cached: Bool) {
        guard chars > 0 else { return }
        let today = dateKey()
        lock.withLock { store in
            let d = UserDefaults.standard
            var map = loadLedgerLocked(&store, from: d)
            var rec = map[today] ?? DayRecord(date: today)
            if cached {
                rec.ttsSavedChars += chars
            } else {
                rec.ttsChars += chars
            }
            map[today] = rec
            saveLedgerLocked(map, cached: &store, to: d)
        }
        notify()
    }

    static func snapshot30Days() -> Snapshot30Days {
        let keys = last30DateKeys()
        let map = lock.withLock { cached in loadLedgerLocked(&cached, from: UserDefaults.standard) }
        let days = keys.map { map[$0] ?? DayRecord(date: $0) }
        var total = 0, cached = 0, miss = 0, out = 0, tts = 0, ttsSaved = 0
        var peak = 0, peakIndex = max(0, days.count - 1)
        var sourceTotals: [Source: Int] = [:]
        for (i, day) in days.enumerated() {
            let t = day.totalTokens
            total += t
            cached += day.cachedInput
            miss += day.missInput
            out += day.output
            tts += day.ttsChars
            ttsSaved += day.ttsSavedChars
            if t > peak {
                peak = t
                peakIndex = i
            }
            for (k, v) in day.sources where v > 0 {
                let s = Source(rawValue: k) ?? .other
                sourceTotals[s, default: 0] += v
            }
        }
        let breakdown: [SourceShare] = total > 0
            ? sourceTotals.map { SourceShare(source: $0.key, tokens: $0.value, share: Double($0.value) / Double(total)) }
                .sorted { $0.tokens > $1.tokens }
            : []
        return Snapshot30Days(
            days: days,
            totalTokens: total,
            cachedInputTokens: cached,
            missInputTokens: miss,
            outputTokens: out,
            peakTokens: peak,
            peakIndex: peakIndex,
            averageTokens: Double(total) / Double(max(1, days.count)),
            sources: breakdown,
            ttsChars: tts,
            ttsSavedChars: ttsSaved
        )
    }

    static func formatCount(_ n: Int) -> String {
        if n >= 10_000 {
            let wan = Double(n) / 10_000.0
            if wan >= 100 { return String(format: "%.0f 万", wan) }
            let s = String(format: "%.1f", wan)
            return (s.hasSuffix(".0") ? String(s.dropLast(2)) : s) + " 万"
        }
        if n >= 1_000 {
            let k = Double(n) / 1_000.0
            let s = String(format: "%.1f", k)
            return (s.hasSuffix(".0") ? String(s.dropLast(2)) : s) + "K"
        }
        return "\(n)"
    }

    private static func notify() {
        DispatchQueue.main.async {
            NotificationCenter.default.post(name: changed, object: nil)
        }
    }
}

enum Companion {
    enum AppleState: Equatable {
        case available
        case notEnabled(String)
        case downloading(String)
        case unsupported(String)

        var reason: String? {
            switch self {
            case .available: nil
            case .notEnabled(let why), .downloading(let why), .unsupported(let why): why
            }
        }
    }

    static var appleState: AppleState {
        #if targetEnvironment(simulator)
        return .unsupported(String(localized: "模拟器用不了 Apple 智能，可以填一个 Key"))
        #else
        #if canImport(FoundationModels)
        if #available(macOS 26, iOS 26, *) {
            switch SystemLanguageModel.default.availability {
            case .available: return .available
            case .unavailable(.deviceNotEligible): return .unsupported(String(localized: "这台设备用不了 Apple 智能，可以填一个 Key"))
            case .unavailable(.appleIntelligenceNotEnabled):
                #if os(macOS)
                return .notEnabled(String(localized: "Apple 智能还没打开：系统设置 › Apple 智能"))
                #else
                return .notEnabled(String(localized: "Apple 智能还没打开：设置 › Apple 智能"))
                #endif
            case .unavailable(.modelNotReady): return .downloading(String(localized: "Apple 智能还在下载模型，过一会儿再试"))
            case .unavailable: return .unsupported(String(localized: "Apple 智能现在用不了"))
            }
        }
        #endif
        return .unsupported(String(localized: "要 macOS 26 或 iOS 26 以上才有 Apple 智能，可以填一个 Key"))
        #endif
    }

    /// Why the model on this device can't answer, or nil when it can.
    static var appleAvailability: String? { appleState.reason }

    /// One answer: `system` says how to behave, `prompt` carries the excerpts and the question.
    /// The language the companion answers in: the interface's. The prompts stay Chinese, and so
    /// do the markers in them (没读到, 接着问) that the replies are read back by.
    nonisolated static var replyLanguage: String {
        (Bundle.main.preferredLocalizations.first ?? "zh").hasPrefix("zh") ? "简体中文" : "英文（English）"
    }

    /// `maxTokens`: how long the answer may be; 播客's script is longer than 书伴's answers.
    /// `cachePrefixLen`: UTF-8 byte length of the stable leading prefix in `prompt` (e.g. a book's cast block),
    /// marked for prompt caching where supported.
    /// `fast`: when true and tier routing is enabled, routes to the provider's lighter/cheaper model with
    /// automatic fallback to the primary model if the lighter model isn't available on the user's endpoint.
    static func answer(
        _ config: CompanionConfig,
        system: String,
        prompt: String,
        maxTokens: Int = 1024,
        cachePrefixLen: Int = 0,
        fast: Bool = false,
        source: CompanionUsage.Source = .other
    ) async throws -> String {
        try CompanionUsage.checkLlmBudget(paid: config.provider.isPaidCloud)
        let effective = config.routed(fast: fast)
        do {
            return try await perform(effective, system: system, prompt: prompt, maxTokens: maxTokens, cachePrefixLen: cachePrefixLen, source: source)
        } catch CompanionError.status(let code, _) where effective.model != config.model && (code == 400 || code == 404) {
            return try await perform(config, system: system, prompt: prompt, maxTokens: maxTokens, cachePrefixLen: cachePrefixLen, source: source)
        }
    }

    private static func perform(
        _ config: CompanionConfig,
        system: String,
        prompt: String,
        maxTokens: Int,
        cachePrefixLen: Int,
        source: CompanionUsage.Source
    ) async throws -> String {
        let text: String
        switch config.provider.kind {
        case .apple: text = try await apple(system: system, prompt: prompt)
        case .anthropic: text = try await claude(config, system: system, prompt: prompt, maxTokens: maxTokens, cachePrefixLen: cachePrefixLen, source: source)
        case .openAI: text = try await openAI(config, system: system, prompt: prompt, maxTokens: maxTokens, cachePrefixLen: cachePrefixLen, source: source)
        }
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { throw CompanionError.empty }
        return trimmed
    }

    /// Splits `text` at UTF-8 byte offset `byteLen`, snapping cleanly to a valid character boundary.
    nonisolated static func splitUTF8Prefix(_ text: String, byteLen: Int) -> (prefix: String, suffix: String)? {
        guard byteLen > 0 else { return nil }
        let utf8 = text.utf8
        guard byteLen < utf8.count else { return nil }
        var idx = utf8.index(utf8.startIndex, offsetBy: byteLen)
        while idx > text.startIndex, idx.samePosition(in: text) == nil {
            idx = utf8.index(before: idx)
        }
        guard let splitAt = idx.samePosition(in: text), splitAt > text.startIndex, splitAt < text.endIndex else { return nil }
        return (String(text[..<splitAt]), String(text[splitAt...]))
    }

    nonisolated private static func estimateTokens(_ text: String) -> Int {
        // Rough fallback when a provider omits `usage`: ~0.7 token per CJK char, ~0.3 per ASCII char.
        var cjk = 0, other = 0
        for s in text.unicodeScalars {
            if s.value >= 0x2E80 { cjk += 1 } else { other += 1 }
        }
        return max(1, Int(Double(cjk) * 0.72 + Double(other) * 0.28))
    }

    private static func apple(system: String, prompt: String) async throws -> String {
        #if canImport(FoundationModels)
        if #available(macOS 26, iOS 26, *) {
            if let why = appleAvailability { throw CompanionError.unavailable(why) }
            let session = LanguageModelSession(instructions: system)
            return try await session.respond(to: prompt).content
        }
        #endif
        throw CompanionError.unavailable(appleAvailability ?? "")
    }

    private static func base(_ config: CompanionConfig) throws -> URL {
        let typed = config.url.trimmingCharacters(in: .whitespaces)
        guard let url = typed.isEmpty ? config.provider.defaultURL : URL(string: typed.hasSuffix("/") ? typed : typed + "/") else {
            throw CompanionError.notReady
        }
        return url
    }

    private static func claude(_ config: CompanionConfig, system: String, prompt: String, maxTokens: Int, cachePrefixLen: Int, source: CompanionUsage.Source) async throws -> String {
        var r = URLRequest(url: try base(config).appending(path: "messages"), timeoutInterval: maxTokens > 1024 ? 240 : 90)
        r.httpMethod = "POST"
        r.setValue("application/json", forHTTPHeaderField: "content-type")
        r.setValue(config.sentKey, forHTTPHeaderField: "x-api-key")
        r.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        let long = maxTokens > 2048
        let useCache = cachePrefixLen > 0 || system.utf8.count >= 1024
        let systemPayload: Any = useCache
            ? [["type": "text", "text": system, "cache_control": ["type": "ephemeral"]]]
            : system
        let userContent: Any
        if let parts = splitUTF8Prefix(prompt, byteLen: cachePrefixLen) {
            userContent = [
                ["type": "text", "text": parts.prefix, "cache_control": ["type": "ephemeral"]],
                ["type": "text", "text": parts.suffix],
            ]
        } else {
            userContent = prompt
        }
        r.httpBody = try JSONSerialization.data(withJSONObject: [
            "model": config.model, "max_tokens": maxTokens, "system": systemPayload,
            "messages": [["role": "user", "content": userContent]], "stream": long,
        ])
        // A long answer takes minutes; streamed, the line stays busy and isn't dropped.
        if long {
            var inTok = 0, outTok = 0, cachedTok = 0
            let reply = try await stream(r) { event in
                let kind = event["type"] as? String
                if kind == "message_start",
                   let msg = event["message"] as? [String: Any],
                   let u = msg["usage"] as? [String: Any] {
                    inTok = (u["input_tokens"] as? Int ?? 0) + (u["cache_creation_input_tokens"] as? Int ?? 0)
                    cachedTok = u["cache_read_input_tokens"] as? Int ?? 0
                } else if kind == "message_delta", let u = event["usage"] as? [String: Any] {
                    if let o = u["output_tokens"] as? Int { outTok = o }
                }
                return kind == "content_block_delta" ? (event["delta"] as? [String: Any])?["text"] as? String : nil
            }
            if inTok == 0 && outTok == 0 {
                inTok = estimateTokens(system + prompt)
                outTok = estimateTokens(reply)
            }
            CompanionUsage.recordLlm(input: inTok, output: outTok, cached: cachedTok, paid: config.provider.isPaidCloud, source: source)
            return reply
        }
        let json = try await send(r)
        let blocks = json["content"] as? [[String: Any]] ?? []
        let reply = blocks.compactMap { $0["text"] as? String }.joined()
        if let u = json["usage"] as? [String: Any] {
            let inTok = (u["input_tokens"] as? Int ?? 0) + (u["cache_creation_input_tokens"] as? Int ?? 0)
            let outTok = u["output_tokens"] as? Int ?? 0
            let cachedTok = u["cache_read_input_tokens"] as? Int ?? 0
            CompanionUsage.recordLlm(input: inTok, output: outTok, cached: cachedTok, paid: config.provider.isPaidCloud, source: source)
        } else {
            CompanionUsage.recordLlm(input: estimateTokens(system + prompt), output: estimateTokens(reply), cached: 0, paid: config.provider.isPaidCloud, source: source)
        }
        return reply
    }

    /// OpenAI, DeepSeek, Gemini (its OpenAI-shaped endpoint), Ollama and the rest.
    private static func openAI(_ config: CompanionConfig, system: String, prompt: String, maxTokens: Int, cachePrefixLen: Int, source: CompanionUsage.Source) async throws -> String {
        var r = URLRequest(url: try base(config).appending(path: "chat/completions"), timeoutInterval: maxTokens > 1024 ? 240 : 120)
        r.httpMethod = "POST"
        r.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if !config.sentKey.isEmpty { r.setValue("Bearer \(config.sentKey)", forHTTPHeaderField: "Authorization") }
        let long = maxTokens > 2048
        let isOpenRouterClaude = config.provider.id == "openrouter" && config.model.lowercased().hasPrefix("anthropic/")
        let messages: [[String: Any]]
        if isOpenRouterClaude, let parts = splitUTF8Prefix(prompt, byteLen: cachePrefixLen) {
            messages = [
                ["role": "system", "content": [["type": "text", "text": system, "cache_control": ["type": "ephemeral"]]]],
                ["role": "user", "content": [
                    ["type": "text", "text": parts.prefix, "cache_control": ["type": "ephemeral"]],
                    ["type": "text", "text": parts.suffix],
                ]],
            ]
        } else {
            messages = [["role": "system", "content": system], ["role": "user", "content": prompt]]
        }
        var payload: [String: Any] = [
            "model": config.model, "temperature": 0.3, "max_tokens": maxTokens, "stream": long,
            "messages": messages,
        ]
        if long && ["openai", "deepseek", "openrouter", "dashscope", "moonshot"].contains(config.provider.id) {
            payload["stream_options"] = ["include_usage": true]
        }
        r.httpBody = try JSONSerialization.data(withJSONObject: payload)
        if long {
            var inTok = 0, outTok = 0, cachedTok = 0
            let reply = try await stream(r) { event in
                if let u = event["usage"] as? [String: Any] {
                    let parsed = Self.parseOpenAIUsage(u)
                    if parsed.input > 0 { inTok = parsed.input }
                    if parsed.output > 0 { outTok = parsed.output }
                    if parsed.cached > 0 { cachedTok = parsed.cached }
                }
                return ((event["choices"] as? [[String: Any]])?.first?["delta"] as? [String: Any])?["content"] as? String
            }
            if inTok == 0 && outTok == 0 {
                inTok = estimateTokens(system + prompt)
                outTok = estimateTokens(reply)
            }
            CompanionUsage.recordLlm(input: inTok, output: outTok, cached: cachedTok, paid: config.provider.isPaidCloud, source: source)
            return reply
        }
        let json = try await send(r)
        let choices = json["choices"] as? [[String: Any]] ?? []
        let reply = (choices.first?["message"] as? [String: Any])?["content"] as? String ?? ""
        if let u = json["usage"] as? [String: Any] {
            let parsed = Self.parseOpenAIUsage(u)
            CompanionUsage.recordLlm(input: parsed.input, output: parsed.output, cached: parsed.cached, paid: config.provider.isPaidCloud, source: source)
        } else {
            CompanionUsage.recordLlm(input: estimateTokens(system + prompt), output: estimateTokens(reply), cached: 0, paid: config.provider.isPaidCloud, source: source)
        }
        return reply
    }

    nonisolated private static func parseOpenAIUsage(_ u: [String: Any]) -> (input: Int, output: Int, cached: Int) {
        let prompt = u["prompt_tokens"] as? Int ?? u["input_tokens"] as? Int ?? 0
        let completion = u["completion_tokens"] as? Int ?? u["output_tokens"] as? Int ?? 0
        let details = u["prompt_tokens_details"] as? [String: Any]
        let cached = details?["cached_tokens"] as? Int
            ?? u["prompt_cache_hit_tokens"] as? Int
            ?? u["cache_read_input_tokens"] as? Int
            ?? 0
        return (max(0, prompt - cached), completion, cached)
    }

    /// The models the service lists (`GET …/models`, the same shape from Anthropic, OpenAI and the
    /// rest), for the 模型 menu. Ollama, LM Studio and magpie list what's there to use.
    static func models(_ config: CompanionConfig) async throws -> [String] {
        var r = URLRequest(url: try base(config).appending(path: "models"), timeoutInterval: 20)
        if config.provider.kind == .anthropic {
            r.setValue(config.sentKey, forHTTPHeaderField: "x-api-key")
            r.setValue("2023-06-01", forHTTPHeaderField: "anthropic-version")
        } else if !config.sentKey.isEmpty {
            r.setValue("Bearer \(config.sentKey)", forHTTPHeaderField: "Authorization")
        }
        let raw = try await sendAny(r)
        let list: [[String: Any]]
        if let dict = raw as? [String: Any] {
            list = dict["data"] as? [[String: Any]] ?? dict["models"] as? [[String: Any]] ?? []
        } else if let array = raw as? [[String: Any]] {
            list = array
        } else if let strings = raw as? [String] {
            let cleaned = Array(Set(strings.map { $0.hasPrefix("models/") ? String($0.dropFirst(7)) : $0 }.filter { !$0.isEmpty })).sorted()
            if !cleaned.isEmpty { ModelNames.saveCached(cleaned, provider: config.provider.id) }
            return cleaned
        } else {
            list = []
        }
        var seen = Set<String>()
        let result = list.compactMap { ($0["id"] ?? $0["name"]) as? String }
            .map { $0.hasPrefix("models/") ? String($0.dropFirst(7)) : $0 }
            .filter { !$0.isEmpty && seen.insert($0).inserted }
        if !result.isEmpty {
            ModelNames.saveCached(result, provider: config.provider.id)
        }
        return result
    }

    /// A streamed answer (server-sent events), joined: `text` takes what each event adds.
    private static func stream(_ request: URLRequest, text: ([String: Any]) -> String?) async throws -> String {
        var r = request
        r.timeoutInterval = 120
        let (bytes, response) = try await URLSession.shared.bytes(for: r)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200 ..< 300).contains(code) else {
            // Read what it says, and fail the way `send` would.
            var body = Data()
            for try await b in bytes { body.append(b) }
            try check(code, body)
            throw CompanionError.status(code, nil)
        }
        var out = ""
        for try await line in bytes.lines {
            guard line.hasPrefix("data:") else { continue }
            let payload = line.dropFirst(5).trimmingCharacters(in: .whitespaces)
            if payload == "[DONE]" { break }
            guard let event = (try? JSONSerialization.jsonObject(with: Data(payload.utf8))) as? [String: Any] else { continue }
            if let piece = text(event) { out += piece }
        }
        return out
    }

    private static func send(_ r: URLRequest) async throws -> [String: Any] {
        (try await sendAny(r)) as? [String: Any] ?? [:]
    }

    private static func sendAny(_ r: URLRequest) async throws -> Any {
        let (data, response) = try await URLSession.shared.data(for: r)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        try check(code, data)
        return (try? JSONSerialization.jsonObject(with: data)) ?? [:]
    }

    /// Throws what an error status means: a wrong key, no credit, busy.
    private static func check(_ code: Int, _ data: Data) throws {
        let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] ?? [:]
        guard (200 ..< 300).contains(code) else {
            let error = json["error"] as? [String: Any]
            let message = error?["message"] as? String ?? json["message"] as? String
            let kind = "\(error?["type"] ?? "") \(error?["code"] ?? "") \(message ?? "")".lowercased()
            switch code {
            case 401, 403: throw CompanionError.badKey
            case 402: throw CompanionError.noCredit
            case 429 where kind.contains("quota") || kind.contains("credit") || kind.contains("balance"): throw CompanionError.noCredit
            case 429, 529: throw CompanionError.busy
            case 400 where kind.contains("credit") || kind.contains("balance"): throw CompanionError.noCredit
            default: throw CompanionError.status(code, message)
            }
        }
    }

    static func message(for error: Error) -> String {
        switch error {
        case let e as CompanionError: e.errorDescription ?? String(localized: "出错了")
        case let e as URLError where e.code == .notConnectedToInternet: String(localized: "没有联网")
        case let e as URLError where e.code == .timedOut: String(localized: "等太久没回答，再问一次")
        case let e as URLError where e.code == .cannotFindHost || e.code == .cannotConnectToHost: String(localized: "连不上服务器，看看接口地址")
        default: error.localizedDescription
        }
    }
}
