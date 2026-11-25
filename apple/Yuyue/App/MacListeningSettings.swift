import SwiftUI
import Foundation
#if os(macOS)
import AppKit
#endif

/// 每日一言 for voice auditioning:
/// - Chinese books pull once per calendar day from Hitokoto (文学 / 诗词 / 哲学), cached in UserDefaults,
///   with curated literary lines ready immediately offline.
/// - English books rotate curated literary classics by date, with manual refresh cycling cleanly.
@MainActor @Observable
final class DailyQuote {
    struct Entry: Equatable, Sendable {
        let text: String
        let source: String
    }

    static let shared = DailyQuote()

    nonisolated private static let hitokotoURL = URL(string: "https://v1.hitokoto.cn/?c=d&c=i&c=k&max_length=46")!
    nonisolated private static let genericSources: Set<String> = ["原创", "网络", "一言", "未知", "佚名", "网友", "其他", "自创"]

    private static let builtinZh: [Entry] = [
        Entry(text: "江上之清风，与山间之明月，耳得之而为声，目遇之而成色。", source: "苏轼《赤壁赋》"),
        Entry(text: "庭下如积水空明，水中藻、荇交横，盖竹柏影也。", source: "苏轼《记承天寺夜游》"),
        Entry(text: "行到水穷处，坐看云起时。偶然值林叟，谈笑无还期。", source: "王维《终南别业》"),
        Entry(text: "醉后不知天在水，满船清梦压星河。", source: "唐温如《题龙阳县青草湖》"),
        Entry(text: "落霞与孤鹜齐飞，秋水共长天一色。", source: "王勃《滕王阁序》"),
        Entry(text: "赌书消得泼茶香，当时只道是寻常。", source: "纳兰性德《浣溪沙》"),
        Entry(text: "我见青山多妩媚，料青山见我应如是。", source: "辛弃疾《贺新郎》"),
        Entry(text: "一个人只拥有此生此世是不够的，他还应该拥有诗意的世界。", source: "王小波《万寿寺》"),
        Entry(text: "太阳，它每时每刻都是夕阳也都是旭日。", source: "史铁生《我与地坛》"),
        Entry(text: "溪流如弓背，山路如弓弦，故远近有了小小差异。", source: "沈从文《边城》"),
        Entry(text: "花开了，就像睡醒了似的。鸟飞了，就像在天上逛似的。", source: "萧红《呼兰河传》"),
        Entry(text: "月光如流水一般，静静地泻在这一片叶子和花上。", source: "朱自清《荷塘月色》"),
        Entry(text: "最妙的是下点小雪呀。看吧，山上的矮松越发的青黑，树尖上顶着一髻儿白花。", source: "老舍《济南的冬天》"),
        Entry(text: "雾凇沆砀，天与云与山与水，上下一白。", source: "张岱《湖心亭看雪》"),
    ]

    private static let builtinEn: [Entry] = [
        Entry(text: "I declare after all there is no enjoyment like reading!", source: "Jane Austen · Pride and Prejudice"),
        Entry(text: "Whatever our souls are made of, his and mine are the same.", source: "Emily Brontë · Wuthering Heights"),
        Entry(text: "I am no bird; and no net ensnares me: I am a free human being with an independent will.", source: "Charlotte Brontë · Jane Eyre"),
        Entry(text: "The world is full of obvious things which nobody by any chance ever observes.", source: "Arthur Conan Doyle"),
        Entry(text: "We are all in the gutter, but some of us are looking at the stars.", source: "Oscar Wilde"),
        Entry(text: "To live is the rarest thing in the world. Most people exist, that is all.", source: "Oscar Wilde"),
        Entry(text: "Only that day dawns to which we are awake. There is more day to dawn.", source: "Henry David Thoreau · Walden"),
        Entry(text: "I went to the woods because I wished to live deliberately.", source: "Henry David Thoreau · Walden"),
        Entry(text: "So we beat on, boats against the current, borne back ceaselessly into the past.", source: "F. Scott Fitzgerald · The Great Gatsby"),
        Entry(text: "Mrs. Dalloway said she would buy the flowers herself.", source: "Virginia Woolf · Mrs Dalloway"),
        Entry(text: "A room without books is like a body without a soul.", source: "Cicero"),
        Entry(text: "Hope is the thing with feathers that perches in the soul, and sings the tune without the words.", source: "Emily Dickinson"),
        Entry(text: "You have power over your mind — not outside events. Realize this, and you will find strength.", source: "Marcus Aurelius · Meditations"),
        Entry(text: "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.", source: "Jane Austen · Pride and Prejudice"),
    ]

    private var entries: [String: Entry] = [:]
    private var entryDates: [String: String] = [:]
    private var offsets: [String: Int] = [:]
    private(set) var refreshing = false

    init() {
        let today = Self.todayKey()
        for lang in ["zh", "en"] {
            if let saved = Self.savedEntry(lang: lang, today: today) {
                entries[lang] = saved
                entryDates[lang] = today
            }
        }
    }

    private static func norm(_ tag: String) -> String {
        tag.lowercased().hasPrefix("en") ? "en" : "zh"
    }

    private static func pool(_ lang: String) -> [Entry] {
        lang == "en" ? builtinEn : builtinZh
    }

    private static func todayKey() -> String {
        let comps = Calendar.current.dateComponents([.year, .month, .day], from: Date())
        return String(format: "%04d-%02d-%02d", comps.year ?? 2026, comps.month ?? 1, comps.day ?? 1)
    }

    private static func savedEntry(lang: String, today: String) -> Entry? {
        let ud = UserDefaults.standard
        guard ud.string(forKey: "voice.daily.\(lang).date") == today,
              let savedText = ud.string(forKey: "voice.daily.\(lang).text")?.trimmingCharacters(in: .whitespacesAndNewlines),
              !savedText.isEmpty else { return nil }
        return Entry(text: savedText, source: ud.string(forKey: "voice.daily.\(lang).source") ?? "")
    }

    private static func builtinForToday(_ lang: String, offset: Int = 0) -> Entry {
        let list = pool(lang)
        let days = Int(Date().timeIntervalSince1970 / 86_400)
        let mod = ((days + offset) % list.count + list.count) % list.count
        return list[mod]
    }

    /// Pure read-only lookup safe to call from SwiftUI `View.body` without mutating `@Observable` state.
    func current(_ tag: String) -> Entry {
        let lang = Self.norm(tag)
        let today = Self.todayKey()
        if let cached = entries[lang], entryDates[lang] == today || offsets[lang] != nil {
            return cached
        }
        if let saved = Self.savedEntry(lang: lang, today: today) {
            return saved
        }
        return Self.builtinForToday(lang, offset: offsets[lang] ?? 0)
    }

    func ensureToday(_ tag: String, canUpdateUI: @escaping @MainActor () -> Bool = { true }) async {
        let lang = Self.norm(tag)
        let today = Self.todayKey()
        if entryDates[lang] != today, let saved = Self.savedEntry(lang: lang, today: today) {
            entryDates[lang] = today
            if entries[lang] != saved {
                entries[lang] = saved
            }
        }
        guard lang == "zh" else { return }
        let ud = UserDefaults.standard
        if ud.string(forKey: "voice.daily.zh.date") == today && ud.bool(forKey: "voice.daily.zh.online") {
            return
        }
        guard let fetched = await Self.fetchHitokoto(timeout: 3.0) else { return }
        save(lang: "zh", today: today, entry: fetched, online: true)
        if canUpdateUI() {
            withAnimation(YYMotion.standard) {
                entryDates["zh"] = today
                entries["zh"] = fetched
            }
        }
    }

    @discardableResult
    func refresh(_ tag: String) async -> Entry {
        let lang = Self.norm(tag)
        let previous = current(lang).text
        refreshing = true
        defer { refreshing = false }
        let today = Self.todayKey()
        var next: Entry?
        var online = false
        if lang == "zh" {
            if let first = await Self.fetchHitokoto(timeout: 1.8, avoid: previous) {
                next = first
            } else {
                next = await Self.fetchHitokoto(timeout: 1.5, avoid: previous)
            }
            online = next != nil
        }
        let chosen = next ?? nextBuiltin(lang: lang, avoid: previous)
        save(lang: lang, today: today, entry: chosen, online: online)
        withAnimation(YYMotion.standard) {
            entryDates[lang] = today
            entries[lang] = chosen
        }
        return chosen
    }

    private func nextBuiltin(lang: String, avoid: String) -> Entry {
        let list = Self.pool(lang)
        var step = (offsets[lang] ?? 0) + 1
        var candidate = Self.builtinForToday(lang, offset: step)
        if candidate.text == avoid && list.count > 1 {
            step += 1
            candidate = Self.builtinForToday(lang, offset: step)
        }
        offsets[lang] = step
        return candidate
    }

    private func save(lang: String, today: String, entry: Entry, online: Bool) {
        let ud = UserDefaults.standard
        ud.set(today, forKey: "voice.daily.\(lang).date")
        ud.set(entry.text, forKey: "voice.daily.\(lang).text")
        ud.set(entry.source, forKey: "voice.daily.\(lang).source")
        ud.set(online, forKey: "voice.daily.\(lang).online")
    }

    nonisolated private struct HitokotoPayload: Decodable {
        let hitokoto: String?
        let from: String?
        let from_who: String?
    }

    nonisolated private static func fetchHitokoto(timeout: TimeInterval, avoid: String? = nil) async -> Entry? {
        var req = URLRequest(url: hitokotoURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: timeout)
        req.setValue("application/json", forHTTPHeaderField: "Accept")
        req.setValue("Yuyue/0.1", forHTTPHeaderField: "User-Agent")
        guard let (data, resp) = try? await URLSession.shared.data(for: req),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let payload = try? JSONDecoder().decode(HitokotoPayload.self, from: data) else {
            return nil
        }
        let text = (payload.hitokoto ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard text.count >= 4, text.count <= 80, !text.contains("我冒了严寒") else { return nil }
        if let avoid, text == avoid { return nil }
        return Entry(text: text, source: formatSource(rawWho: payload.from_who, rawFrom: payload.from))
    }

    nonisolated static func formatSource(rawWho: String?, rawFrom: String?) -> String {
        let cleanWho = (rawWho ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let cleanFrom = (rawFrom ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        let who = (cleanWho.isEmpty || genericSources.contains(cleanWho)) ? "" : cleanWho
        let from = (cleanFrom.isEmpty || genericSources.contains(cleanFrom)) ? "" : cleanFrom
        let wrappedFrom: String = if from.isEmpty {
            ""
        } else if from.hasPrefix("《") || from.contains("·") || from.unicodeScalars.allSatisfy({ $0.value < 128 }) {
            from
        } else {
            "《\(from)》"
        }
        if !who.isEmpty && !wrappedFrom.isEmpty {
            return who == from ? who : "\(who)\(wrappedFrom)"
        }
        return !who.isEmpty ? who : wrappedFrom
    }
}

/// What the right column of 朗读 shows, and the state the two columns share.
@MainActor @Observable
final class ListeningFocus {
    static let shared = ListeningFocus()

    /// The service shown in the right column (its key / endpoint); nil: the voice card.
    var page: CloudVoice.Provider?
    /// Adding a new voice source from the catalog sheet.
    var addingSheet = false
    /// Optional provider to jump straight to inside the add sheet (e.g. debug launch arg).
    var addingProvider: CloudVoice.Provider?
    /// The source whose voices the middle shows; nil: the list of sources.
    var source: VoiceSource?
    /// Which kind of book the voices are being chosen for.
    var book = VoiceBook.defaultForUI
    /// The voice last clicked to listen to, for the right column's card.
    var listening: String?
    /// Active sources (with a key, explicitly enabled, or auto-discovered on localhost).
    private(set) var added: [CloudVoice.Provider] = []
    var tick = 0
    /// This Mac's panel is in the right column (⚙).
    var macPanel = false
    /// What you sent, by book; none yet: the book's own line.
    private var lines: [VoiceBook: String] = [:]
    /// The voices heard since the line last changed; the others keep a red dot.
    private(set) var heard: Set<String> = []
    private var probing = false
    @ObservationIgnored private var userPickedBook = false
    @ObservationIgnored private var syncedDefaultBook = VoiceBook.defaultForUI
    #if DEBUG
    var debugApplied = false
    #endif

    private init() {
        for name in [AppLanguage.changed, NSLocale.currentLocaleDidChangeNotification] {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.syncDefaultBookIfNeeded() }
            }
        }
    }

    func selectBook(_ next: VoiceBook) {
        userPickedBook = true
        book = next
        listening = nil
        Voices.stopPreview()
    }

    func syncDefaultBookIfNeeded() {
        let target = VoiceBook.defaultForUI
        if syncedDefaultBook != target {
            syncedDefaultBook = target
            userPickedBook = false
        }
        guard !userPickedBook else { return }
        if book != target { book = target }
    }

    func hasCustomLine(_ book: VoiceBook) -> Bool { lines[book] != nil }
    func customLine(_ book: VoiceBook) -> String? { lines[book] }
    func line(_ book: VoiceBook) -> String { lines[book] ?? book.line }

    func listen(_ voice: any Voice, text: String) {
        if voice is KokoroVoice, !KokoroModel.isInstalled {
            Voices.stopPreview()
            page = nil
            macPanel = false
            listening = voice.id
            KokoroDownload.shared.queueSelection(voice.id, for: book.key)
            tick &+= 1
            return
        }
        if PreviewState.shared.voiceID == voice.id { Voices.stopPreview(); return }
        page = nil
        macPanel = false
        listening = voice.id
        heard.insert(voice.id)
        Voices.preview(voice, text: text)
    }

    func send(_ text: String, book: VoiceBook) {
        let t = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !t.isEmpty else { return }
        Voices.stopPreview()
        lines[book] = t
        heard = []
        if let v = book.current { listen(v, text: t) }
    }

    func clearCustomLine(_ book: VoiceBook) {
        Voices.stopPreview()
        lines.removeValue(forKey: book)
        heard = []
    }

    func pick(_ voice: any Voice, book: VoiceBook) {
        if voice is KokoroVoice, !KokoroModel.isInstalled {
            Voices.stopPreview()
            KokoroDownload.shared.queueSelection(voice.id, for: book.key)
            withAnimation(YYMotion.layout) {
                listening = voice.id
                tick &+= 1
            }
            return
        }
        Voices.stopPreview()
        heard.insert(voice.id)
        withAnimation(YYMotion.layout) {
            book.choose(voice)
            listening = voice.id
            tick &+= 1
        }
    }

    func open(_ s: VoiceSource) {
        Voices.stopPreview()
        syncDefaultBookIfNeeded()
        source = s
        listening = nil
        macPanel = false
        if case .cloud(let p) = s, page != p { page = nil }
    }

    func close() {
        Voices.stopPreview()
        source = nil
        listening = nil
        macPanel = false
    }

    func reload() {
        syncDefaultBookIfNeeded()
        added = CloudVoice.Provider.allCases.filter(\.isSetUp)
        if let p = page, !p.isSetUp { page = nil }
        tick &+= 1
        probeLocalIfNeeded()
    }

    func probeLocalIfNeeded() {
        guard !probing else { return }
        probing = true
        Task {
            let changed = await LocalVoiceProbe.shared.scanAll()
            self.probing = false
            if changed {
                withAnimation(YYMotion.standard) {
                    self.added = CloudVoice.Provider.allCases.filter(\.isSetUp)
                    self.tick &+= 1
                }
            }
        }
    }
}

extension CloudVoice.Provider {
    var voiceCount: Int { CloudVoice.all().filter { $0.provider == self }.count }

    var inUse: Bool {
        ["zh", "en"].contains { UserDefaults.standard.string(forKey: "player.voice.\($0)")?.hasPrefix("cloud.\(rawValue).") == true }
    }

    var maskedKey: String { Self.mask(key ?? "") }

    static func mask(_ key: String) -> String {
        guard key.count > 8 else { return String(repeating: "•", count: key.count) }
        return String(key.prefix(key.hasPrefix("sk") ? 3 : 0)) + "••••" + String(key.suffix(4))
    }

    var account: String {
        switch self {
        case .edge: String(localized: "免 Key · 微软 Edge 神经语音")
        case .free: String(localized: "免 Key · 零配置在线语音")
        case .volcengine: String(localized: "火山引擎豆包语音")
        case .fish: "Fish Audio"
        case .xiaomi: String(localized: "小米开放平台")
        case .siliconflow: String(localized: "硅基流动")
        case .dashscope: String(localized: "阿里云百炼")
        case .minimax: "MiniMax"
        case .openai: "OpenAI"
        case .azure: "Microsoft Azure"
        case .elevenlabs: "ElevenLabs"
        case .deepgram: "Deepgram"
        case .voicestudio, .voicebox, .mlxaudio, .speaches: String(localized: "本机或局域网")
        case .local: String(localized: "OpenAI 兼容接口")
        }
    }

    var blurb: String {
        switch self {
        case .edge: String(localized: "免 Key 直连微软 Edge 自然神经语音，含晓晓、云希、Ava 等多语种音色")
        case .free: String(localized: "0 MB 免下载、免注册免 Key，已读句子缓存在本机")
        case .volcengine: String(localized: "豆包大模型语音，中文播客与小说自然度高")
        case .fish: String(localized: "OpenAudio S1 高表现力中英音色与社区声音库")
        case .xiaomi: String(localized: "中英 8 个精品音色，支持语气指令")
        case .siliconflow: String(localized: "CosyVoice2 音色与克隆，可复用书伴 Key")
        case .dashscope: String(localized: "千问3-TTS，约 0.1 元/千字，可复用书伴 Key")
        case .minimax: String(localized: "Speech-2.8 高拟真情绪与呼吸感")
        case .openai: String(localized: "10 个音色，支持中英双语与语气指令")
        case .azure: String(localized: "中文音色丰富，每月 50 万字免费")
        case .elevenlabs: String(localized: "自然度高，适合外文朗读与克隆")
        case .deepgram: String(localized: "Aura-2 英文自然极速语音，注册含体验金")
        case .voicestudio: String(localized: "自动连接本机 3900 端口与克隆音色")
        case .voicebox: String(localized: "自动连接本机 17493 端口多引擎语音")
        case .mlxaudio: String(localized: "Apple Silicon 原生加速本地语音")
        case .speaches: String(localized: "Docker 开源语音服务，适合本机或 NAS")
        case .local: String(localized: "GPT-SoVITS、Kokoro、unspeech 或局域网地址")
        }
    }

    var shortTag: String {
        if let _ = companionSharedKey {
            return String(localized: "可复用书伴 Key")
        }
        return switch self {
        case .edge: String(localized: "免 Key · 微软神经语音")
        case .free: String(localized: "免 Key · 即点即听")
        case .volcengine: String(localized: "豆包大模型语音")
        case .fish: String(localized: "OpenAudio S1")
        case .xiaomi: String(localized: "限时免费 · 8 音色")
        case .siliconflow: String(localized: "CosyVoice2 与克隆")
        case .dashscope: String(localized: "千问3 · 按字低价")
        case .minimax: String(localized: "高拟真情绪")
        case .azure: String(localized: "每月 50 万字免费")
        case .openai: String(localized: "中英双语 10 音色")
        case .elevenlabs: String(localized: "外文朗读与克隆")
        case .deepgram: String(localized: "英文极速 · 含体验金")
        case .voicestudio: String(localized: "本机开源 · 3900")
        case .voicebox: String(localized: "本机开源 · 17493")
        case .mlxaudio: String(localized: "Mac 原生 · 8000")
        case .speaches: String(localized: "Docker · 8000")
        case .local: String(localized: "OpenAI 兼容接口")
        }
    }

    var keyStep: (title: String, detail: String, url: URL) {
        switch self {
        case .edge:
            (String(localized: "了解微软 Edge 朗读语音"),
             String(localized: "免注册免填 Key，直连微软 Edge 神经语音（晓晓、云希、云健、Ava 等），已朗读句子缓存在本机"),
             URL(string: "https://www.microsoft.com/edge")!)
        case .free:
            (String(localized: "了解在线自然语音"),
             String(localized: "内置免 Key 公共直连语音线路，无需注册或填写 Key，已朗读句子缓存在本机"),
             URL(string: "https://dict.youdao.com")!)
        case .volcengine:
            (String(localized: "获取火山引擎 AppID 与 Token"),
             String(localized: "console.volcengine.com › 豆包语音 › 语音合成大模型，复制 AppID 与 Access Token"),
             URL(string: "https://console.volcengine.com/speech/app")!)
        case .fish:
            (String(localized: "获取 Fish Audio Key"),
             String(localized: "fish.audio › API › API Keys › 新建密钥，支持 OpenAudio S1 与社区音色"),
             URL(string: "https://fish.audio/app/api-keys/")!)
        case .xiaomi:
            (String(localized: "获取小米 MiMo Key"),
             String(localized: "platform.xiaomimimo.com › API Keys › 新建密钥，当前限时免费"),
             URL(string: "https://platform.xiaomimimo.com")!)
        case .siliconflow:
            (String(localized: "获取硅基流动 Key"),
             String(localized: "cloud.siliconflow.cn › API 密钥 › 新建密钥"),
             URL(string: "https://cloud.siliconflow.cn/account/ak")!)
        case .dashscope:
            (String(localized: "获取百炼 API-KEY"),
             String(localized: "百炼控制台 › API-KEY 管理 › 创建 API-KEY"),
             URL(string: "https://bailian.console.aliyun.com/?apiKey=1")!)
        case .minimax:
            (String(localized: "获取 MiniMax Key"),
             String(localized: "platform.minimaxi.com › 账户管理 › 接口密钥"),
             URL(string: "https://platform.minimaxi.com/user-center/basic-information/interface-key")!)
        case .openai:
            (String(localized: "获取 OpenAI Key"),
             String(localized: "platform.openai.com › API keys › 新建，拷下来"),
             URL(string: "https://platform.openai.com/api-keys")!)
        case .azure:
            (String(localized: "创建 Azure 语音资源"),
             String(localized: "Azure 门户 › 创建资源 › 语音服务；打开它的“密钥和终结点”，拷“密钥 1”"),
             URL(string: "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices")!)
        case .elevenlabs:
            (String(localized: "获取 ElevenLabs Key"),
             String(localized: "ElevenLabs › 个人资料 › API Keys › 新建"),
             URL(string: "https://elevenlabs.io/app/settings/api-keys")!)
        case .deepgram:
            (String(localized: "获取 Deepgram Key"),
             String(localized: "console.deepgram.com › API Keys › Create a New API Key"),
             URL(string: "https://console.deepgram.com")!)
        case .voicestudio:
            (String(localized: "打开 VoiceStudio 官网"),
             String(localized: "在本机打开 VoiceStudio 后会自动监听 127.0.0.1:3900，无需填写 Key"),
             URL(string: "https://voicestudio.sh")!)
        case .voicebox:
            (String(localized: "打开 Voicebox 主页"),
             String(localized: "在本机打开 Voicebox 后会自动监听 127.0.0.1:17493，无需填写 Key"),
             URL(string: "https://github.com/jamiepine/voicebox")!)
        case .mlxaudio:
            (String(localized: "打开 mlx-audio 主页"),
             String(localized: "运行 mlx_audio.server 后默认监听 127.0.0.1:8000，无需填写 Key"),
             URL(string: "https://github.com/Blaizzy/mlx-audio")!)
        case .speaches:
            (String(localized: "打开 Speaches 官网"),
             String(localized: "Docker 启动 Speaches 后默认监听 8000 端口，支持本机或局域网连接"),
             URL(string: "https://speaches.ai")!)
        case .local:
            (String(localized: "了解兼容语音接口"),
             String(localized: "填写任何兼容 OpenAI /v1/audio/speech 的本机或局域网地址即可"),
             URL(string: "https://github.com/moeru-ai/unspeech")!)
        }
    }

    static var pickWhere: String {
        #if os(macOS)
        String(localized: "直接点语音条试听选择")
        #else
        String(localized: "在“声音”里挑")
        #endif
    }

    static let regions: [(id: String, name: String)] = [
        ("eastasia", String(localized: "东亚")), ("southeastasia", String(localized: "东南亚")),
        ("japaneast", String(localized: "日本东部")), ("koreacentral", String(localized: "韩国中部")),
        ("eastus", String(localized: "美国东部")), ("westus2", String(localized: "美国西部 2")),
        ("westeurope", String(localized: "西欧")), ("uksouth", String(localized: "英国南部")),
    ]

    static var region: String {
        get { UserDefaults.standard.string(forKey: regionName).flatMap { $0.isEmpty ? nil : $0 } ?? "eastasia" }
        set { UserDefaults.standard.set(newValue, forKey: regionName) }
    }

    var endpointHost: String {
        switch self {
        case .edge:
            (UserDefaults.standard.string(forKey: urlName)?.trimmingCharacters(in: .whitespacesAndNewlines))
                .flatMap { $0.isEmpty ? nil : (URL(string: $0)?.host() ?? $0) } ?? "speech.platform.bing.com"
        case .free:
            (UserDefaults.standard.string(forKey: urlName)?.trimmingCharacters(in: .whitespacesAndNewlines))
                .flatMap { $0.isEmpty ? nil : (URL(string: $0)?.host() ?? $0) } ?? "dict.youdao.com / fanyi.baidu.com"
        case .volcengine: "openspeech.bytedance.com"
        case .fish: "api.fish.audio"
        case .xiaomi: "api.xiaomimimo.com"
        case .siliconflow: "api.siliconflow.cn"
        case .dashscope: "dashscope.aliyuncs.com"
        case .minimax: "api.minimaxi.com"
        case .openai: "api.openai.com"
        case .azure: "\(Self.region).tts.speech.microsoft.com"
        case .elevenlabs: "api.elevenlabs.io"
        case .deepgram: "api.deepgram.com"
        case .voicestudio, .voicebox, .mlxaudio, .speaches, .local:
            URL(string: savedURL)?.host() ?? savedURL
        }
    }

    func remove() {
        Keychain.delete(keyName)
        let d = UserDefaults.standard
        d.removeObject(forKey: voicesName)
        d.removeObject(forKey: urlName)
        d.removeObject(forKey: modelName)
        d.set(false, forKey: enabledName)
        if isFreeOnline {
            d.set(true, forKey: dismissedName)
        }
        if isLocal {
            d.set(true, forKey: dismissedName)
            LocalVoiceProbe.shared.clear(self)
        }
        if self == .local { d.removeObject(forKey: "tts.local.title") }
        if self == .azure { d.removeObject(forKey: Self.regionName) }
        if self == .volcengine { d.removeObject(forKey: Self.volcAppIDName) }
        for tag in ["zh", "en"] where d.string(forKey: "player.voice.\(tag)")?.hasPrefix("cloud.\(rawValue).") == true {
            d.removeObject(forKey: "player.voice.\(tag)")
        }
    }

    @MainActor func tryKey(_ key: String, region: String?, appID: String? = nil) async throws -> AutoConnect.Outcome {
        let oldKey = self.key
        let oldRegion = UserDefaults.standard.string(forKey: Self.regionName)
        let oldAppID = UserDefaults.standard.string(forKey: Self.volcAppIDName)
        Keychain.write(keyName, key)
        if let region { Self.region = region }
        if self == .volcengine, let appID {
            let clean = appID.trimmingCharacters(in: .whitespacesAndNewlines)
            if clean.isEmpty { UserDefaults.standard.removeObject(forKey: Self.volcAppIDName) }
            else { UserDefaults.standard.set(clean, forKey: Self.volcAppIDName) }
        }
        do {
            let n = try await CloudVoice.refresh(self)
            return .init(
                ticks: [
                    .init(title: String(localized: "连得上 \(endpointHost)")),
                    .init(title: String(localized: "Key 有效")),
                    .init(title: String(localized: "找到 \(n) 个音色"), detail: Self.pickWhere),
                ],
                banner: String(localized: "\(title) · 已连上")
            )
        } catch {
            if let oldKey { Keychain.write(keyName, oldKey) } else { Keychain.delete(keyName) }
            if self == .azure { UserDefaults.standard.set(oldRegion, forKey: Self.regionName) }
            if self == .volcengine { UserDefaults.standard.set(oldAppID, forKey: Self.volcAppIDName) }
            throw error
        }
    }

    @MainActor func tryFree(customURL: String? = nil) async throws -> AutoConnect.Outcome {
        let d = UserDefaults.standard
        if let customURL {
            let clean = CloudVoice.normalizedBaseV1(customURL)
            if clean.isEmpty { d.removeObject(forKey: urlName) }
            else { d.set(clean, forKey: urlName) }
        }
        d.set(false, forKey: dismissedName)
        let n = try await CloudVoice.refresh(self)
        return .init(
            ticks: [
                .init(title: String(localized: "连得上 \(endpointHost)")),
                .init(title: String(localized: "免 Key 在线合成正常")),
                .init(title: String(localized: "可用 \(n) 个音色"), detail: Self.pickWhere),
            ],
            banner: String(localized: "\(title) · 已就绪")
        )
    }

    @MainActor func tryLocal(url: String, model: String, key: String, customTitle: String? = nil) async throws -> AutoConnect.Outcome {
        let d = UserDefaults.standard
        let oldURL = d.string(forKey: urlName)
        let oldModel = d.string(forKey: modelName)
        let oldEnabled = d.bool(forKey: enabledName)
        let oldDismissed = d.bool(forKey: dismissedName)
        let oldKey = self.key

        let cleanURL = CloudVoice.normalizedBaseV1(url)
        guard !cleanURL.isEmpty else { throw CloudVoice.Failure.noURL(self) }
        d.set(cleanURL, forKey: urlName)
        let cleanModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        if cleanModel.isEmpty { d.removeObject(forKey: modelName) } else { d.set(cleanModel, forKey: modelName) }
        let cleanKey = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if cleanKey.isEmpty { Keychain.delete(keyName) } else { Keychain.write(keyName, cleanKey) }
        if self == .local, let customTitle {
            let t = customTitle.trimmingCharacters(in: .whitespacesAndNewlines)
            if t.isEmpty { d.removeObject(forKey: "tts.local.title") } else { d.set(t, forKey: "tts.local.title") }
        }
        d.set(true, forKey: enabledName)
        d.set(false, forKey: dismissedName)

        do {
            let n = try await CloudVoice.refresh(self)
            return .init(
                ticks: [
                    .init(title: String(localized: "连得上 \(endpointHost)")),
                    .init(title: String(localized: "语音接口响应正常")),
                    .init(title: String(localized: "可用 \(n) 个音色"), detail: Self.pickWhere),
                ],
                banner: String(localized: "\(title) · 已连上")
            )
        } catch {
            if let oldURL { d.set(oldURL, forKey: urlName) } else { d.removeObject(forKey: urlName) }
            if let oldModel { d.set(oldModel, forKey: modelName) } else { d.removeObject(forKey: modelName) }
            d.set(oldEnabled, forKey: enabledName)
            d.set(oldDismissed, forKey: dismissedName)
            if let oldKey { Keychain.write(keyName, oldKey) } else { Keychain.delete(keyName) }
            throw error
        }
    }

    static func available(matching query: String) -> [CloudVoice.Provider] {
        let q = query.lowercased().replacingOccurrences(of: " ", with: "")
        return allCases.filter { p in
            guard !p.isSetUp || p == .local else { return false }
            guard !q.isEmpty else { return true }
            let hay = (p.rawValue + p.title + p.aliases + p.blurb).lowercased().replacingOccurrences(of: " ", with: "")
            return hay.contains(q)
        }
    }
}

// MARK: - 添加声音来源 Sheet (macOS & iOS)

struct VoiceProviderSheet: View {
    let initial: CloudVoice.Provider?
    let pick: (CloudVoice.Provider) -> Void
    @State private var picked: CloudVoice.Provider?
    @State private var query = ""
    @Environment(\.dismiss) private var dismiss

    #if os(macOS)
    private static let columns = 3
    #else
    private static let columns = 2
    #endif

    init(initial: CloudVoice.Provider? = nil, pick: @escaping (CloudVoice.Provider) -> Void) {
        self.initial = initial
        self.pick = pick
        _picked = State(initialValue: initial)
    }

    var body: some View {
        #if os(macOS)
        Group {
            if let picked {
                MacVoiceProviderForm(
                    provider: picked,
                    back: initial == nil ? { self.picked = nil } : nil,
                    close: { dismiss() },
                    done: {
                        dismiss()
                        pick(picked)
                    }
                )
                .id(picked.id)
                .transition(YYMotion.swap)
            } else {
                picker.transition(YYMotion.swap)
            }
        }
        .animation(YYMotion.standard, value: picked?.id)
        .padding(.horizontal, YYUI.s(16))
        .padding(.bottom, YYUI.s(16))
        .padding(.top, YYUI.sheetGrabberTop)
        .frame(width: 480, height: 468)
        .background(YYColor.bg)
        #else
        picker
            .padding(.horizontal, YYUI.s(16))
            .padding(.bottom, YYUI.s(16))
            .padding(.top, YYUI.sheetGrabberTop)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            .presentationDetents([.large])
            .presentationDragIndicator(.visible)
            .presentationBackground(YYColor.bg)
            .background(YYColor.bg)
        #endif
    }

    private var picker: some View {
        let matches = CloudVoice.Provider.available(matching: query).filter { $0 != .local }
        return VStack(alignment: .leading, spacing: 0) {
            YYSheetHeader(title: String(localized: "添加声音来源"), subtitle: String(localized: "朗读"), close: { dismiss() })
            HStack(spacing: YYUI.s(6)) {
                Image(systemName: "magnifyingglass").font(YYUI.font(10.5, .medium)).foregroundStyle(YYColor.t3)
                TextField("", text: $query, prompt: Text("搜索语音来源，比如 火山豆包、Fish Audio、小米、VoiceStudio"))
                    .textFieldStyle(.plain)
                    .font(YYUI.font(11.5))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                    .onSubmit { if let first = matches.first { choose(first) } }
            }
            .padding(.horizontal, YYUI.s(9))
            .frame(height: YYUI.s(30))
            .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(8)))
            .padding(.top, YYUI.s(10))

            ScrollView {
                VStack(alignment: .leading, spacing: YYUI.s(10)) {
                    ForEach(CloudVoice.Group.allCases, id: \.self) { group in
                        let list = matches.filter { $0.group == group }
                        if !list.isEmpty {
                            VStack(alignment: .leading, spacing: YYUI.s(2)) {
                                Text(group.title)
                                    .font(YYUI.font(10, .semibold))
                                    .foregroundStyle(YYColor.t3)
                                    .padding(.leading, YYUI.s(8))
                                    .padding(.bottom, YYUI.s(2))
                                LazyVGrid(
                                    columns: Array(repeating: GridItem(.flexible(), spacing: YYUI.s(2)), count: Self.columns),
                                    spacing: YYUI.s(2)
                                ) {
                                    ForEach(list) { p in providerTile(p) }
                                }
                            }
                        }
                    }
                    if matches.isEmpty {
                        Text("没找到匹配的内置渠道，可在下方添加自定义 OpenAI 兼容接口")
                            .font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                            .frame(maxWidth: .infinity).padding(.vertical, YYUI.s(36))
                    }
                }
                .padding(.vertical, YYUI.s(12))
            }
            .scrollBounceBehavior(.basedOnSize)

            Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, -YYUI.s(16))
            Button { choose(.local) } label: {
                HStack(spacing: YYUI.s(8)) {
                    Image(systemName: "plus").font(YYUI.font(10.5, .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(16), height: YYUI.s(16))
                    Text("自定义语音接口").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).fixedSize()
                    Text("GPT-SoVITS、Kokoro、unspeech 或局域网 /v1/audio/speech").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3).lineLimit(1)
                    Spacer(minLength: 0)
                }
            }
            .buttonStyle(PickRow())
            .padding(.top, YYUI.s(8))
        }
    }

    private func choose(_ p: CloudVoice.Provider) {
        #if os(macOS)
        if p.isFreeOnline {
            UserDefaults.standard.set(false, forKey: p.dismissedName)
            dismiss()
            pick(p)
        } else {
            picked = p
        }
        #else
        dismiss()
        pick(p)
        #endif
    }

    private func providerTile(_ p: CloudVoice.Provider) -> some View {
        Button { choose(p) } label: {
            HStack(spacing: YYUI.s(8)) {
                ZStack(alignment: .topTrailing) {
                    ServiceTile(mark: .cloud(p), size: YYUI.s(18))
                    if p.companionSharedKey != nil || p.isFreeOnline || p == .xiaomi || p == .azure || p == .deepgram {
                        Circle()
                            .fill(YYColor.ok)
                            .frame(width: 5, height: 5)
                            .offset(x: 1.5, y: -1.5)
                    }
                }
                Text(p.title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(1)
                Spacer(minLength: 0)
            }
        }
        .buttonStyle(PickRow())
        .help(Text("\(p.title) · \(p.blurb)"))
    }
}

#if os(macOS)
// MARK: - The right column (macOS)

struct ListeningInspector: View {
    private var focus: ListeningFocus { .shared }

    var body: some View {
        let _ = focus.tick
        Group {
            if let p = focus.page, p.isSetUp {
                CloudVoicePanel(provider: p)
            }
        }
        .id(focus.page?.id)
        .transition(.opacity.combined(with: .offset(y: 3)))
    }
}

private struct OutLinkGlyph: View {
    var body: some View {
        SVGShape(d: "M3.5 8.5l5-5M4.5 3.5h4v4", box: 12)
            .stroke(YYColor.t2, style: StrokeStyle(lineWidth: 1.5, lineCap: .round))
            .frame(width: 12, height: 12)
    }
}

struct MacVoicePanel: View {
    private var source: VoiceSource {
        if let s = ListeningFocus.shared.source, s == .neural || s == .mac { return s }
        return .neural
    }

    var body: some View {
        let _ = KokoroDownload.shared.phase
        let _ = LiveVoiceDownload.shared.phase
        VStack(alignment: .leading, spacing: 10) {
            if source == .neural {
                PanelHeader(title: source.title, detail: String(localized: "跨端轻量离线模型 · Apache-2.0")) {
                    ServiceTile(mark: source.mark, size: 34)
                }
                PanelCard {
                    PanelRow { Text("Kokoro 引擎") } trailing: {
                        SettingsPill(KokoroModel.isInstalled ? String(localized: "已就绪 · 16 音色") : String(localized: "待下载 · \(KokoroModel.totalMegabytes) MB"),
                                     tone: KokoroModel.isInstalled ? .ok : .warn)
                    }
                    PanelRow { Text("角色与克隆") } trailing: {
                        Text(LiveVoiceModel.isInstalled ? String(localized: "已就绪") : String(localized: "按需准备"))
                            .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    }
                    PanelRow { Text("费用") } trailing: {
                        Text("免费 · 不联网").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    }
                }
                if !KokoroModel.isInstalled {
                    PanelCard {
                        PanelButton(systemImage: "arrow.down.circle", tint: YYColor.ok) {
                            switch KokoroDownload.shared.phase {
                            case .downloading(let frac):
                                Text("正在下载离线语音 \(Int((frac * 100).rounded()))%").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 8)
                                ProgressView().controlSize(.mini)
                            default:
                                Text("一键下载 Kokoro 离线语音（\(KokoroModel.totalMegabytes) MB）").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 0)
                            }
                        } action: { KokoroDownload.shared.start() }
                    }
                }
                PanelHint(String(localized: "Kokoro-82M v1.1 Int8 跨端统一使用 ONNX Runtime 离线合成，无需联网即可自然朗读中英文书籍。"))
            } else {
                PanelHeader(title: source.title, detail: String(localized: "系统合成音 · 不推荐")) {
                    ServiceTile(mark: source.mark, size: 34)
                }
                PanelCard {
                    PanelRow { Text("评级") } trailing: {
                        SettingsPill(String(localized: "不推荐"), tone: .warn)
                    }
                    PanelRow { Text("声音") } trailing: {
                        Text("共 \(source.voiceCount) 个").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    }
                    PanelRow { Text("费用") } trailing: {
                        Text("免费 · 仅作兜底").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    }
                }
                PanelCard {
                    PanelButton {
                        OutLinkGlyph()
                        Text("下载更多系统声音").foregroundStyle(YYColor.t1)
                        Spacer(minLength: 0)
                    } action: {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Accessibility-Settings.extension?SpokenContent")!)
                    }
                }
                PanelHint(String(localized: "系统自带合成音机械感明显，仅建议在未下载离线语音且完全断网时兜底使用。"))
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }
}

/// Sheet form to configure and connect a new voice source (matches 书伴's `MacProviderForm`).
/// Only enters the main 声音来源 list once connection succeeds; ‹ returns to the catalog, × cancels cleanly.
private struct MacVoiceProviderForm: View {
    let provider: CloudVoice.Provider
    let back: (() -> Void)?
    let close: () -> Void
    let done: () -> Void
    @State private var key = ""
    @State private var appID = UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName) ?? ""
    @State private var region = CloudVoice.Provider.region
    @State private var url = ""
    @State private var model = ""
    @State private var customName = ""
    @State private var connect = AutoConnect()

    var body: some View {
        let step = provider.keyStep
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: YYUI.s(6)) {
                if let back {
                    Button(action: back) {
                        Image(systemName: "chevron.left").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t2)
                            .frame(width: YYUI.s(20), height: YYUI.s(26))
                            .contentShape(Rectangle().inset(by: -8))
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(Text("返回"))
                }
                ServiceTile(mark: .cloud(provider), size: 22)
                YYSheetHeader(
                    title: provider == .local ? String(localized: "自定义语音接口") : provider.title,
                    subtitle: String(localized: "朗读"),
                    close: close
                )
            }
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    Text(provider.blurb)
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 2)

                    if provider.isFreeOnline {
                        PanelCard {
                            PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                                Text("开启 \(provider.title)").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 8)
                                if connect.busy { ProgressView().controlSize(.mini) }
                            } action: {
                                let p = provider
                                connect.start(String(localized: "正在检查在线语音线路…"), delay: .zero) {
                                    try await p.tryFree()
                                } done: {
                                    done()
                                }
                            }
                        }
                    } else if provider.isLocal {
                        PanelCard {
                            if provider == .local {
                                PanelField(label: String(localized: "名称"), text: $customName, prompt: String(localized: "比如 本机 Kokoro"), mono: false)
                            }
                            PanelField(label: String(localized: "地址"), text: $url, prompt: provider.urlHint, bad: connect.failed, autofocus: true, submit: { attemptLocal(now: true) })
                            PanelField(label: String(localized: "模型"), text: $model, prompt: provider.defaultModel, submit: { attemptLocal(now: true) })
                            PanelField(label: "Key", text: $key, prompt: provider.keyHint, secure: true, submit: { attemptLocal(now: true) })
                            PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                                Text("连接并获取音色").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 8)
                                if connect.busy {
                                    ProgressView().controlSize(.mini)
                                } else if !localFilled {
                                    Text("还没填好").font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                                }
                            } action: {
                                guard localFilled, !connect.busy else { return }
                                attemptLocal(now: true)
                            }
                            .opacity(localFilled || connect.busy ? 1 : 0.6)
                        }
                    } else {
                        if let shared = provider.companionSharedKey {
                            PanelCard {
                                PanelButton(systemImage: "key.fill", tint: YYColor.ok) {
                                    VStack(alignment: .leading, spacing: 1) {
                                        Text("复用「书伴」的 Key").font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.ok).lineLimit(1)
                                        Text(CloudVoice.Provider.mask(shared)).font(.yy(size: 10.5, design: .monospaced)).foregroundStyle(YYColor.t3)
                                    }
                                    Spacer(minLength: 6)
                                    if connect.busy { ProgressView().controlSize(.mini) }
                                } action: {
                                    key = shared
                                    attemptCloud(now: true)
                                }
                            }
                            PanelLabel(String(localized: "或者贴一个已有的 Key"))
                        }
                        PanelCard {
                            if provider == .azure { RegionRow(region: $region) }
                            if provider == .volcengine {
                                PanelField(label: "AppID", text: $appID, prompt: String(localized: "应用 AppID"),
                                           autofocus: true, submit: { attemptCloud(now: true) })
                            }
                            PanelField(label: provider == .volcengine ? "Token" : "Key",
                                       text: $key,
                                       prompt: provider == .azure ? String(localized: "密钥 1") : (provider == .volcengine ? String(localized: "Access Token") : provider.keyHint),
                                       secure: true, bad: connect.failed,
                                       autofocus: provider.companionSharedKey == nil && provider != .volcengine,
                                       submit: { attemptCloud(now: true) })
                            PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                                Text("检查并添加").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 8)
                                if connect.busy {
                                    ProgressView().controlSize(.mini)
                                } else if !cloudFilled {
                                    Text(key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
                                         ? String(localized: "先填 Key")
                                         : String(localized: "还没填好"))
                                        .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                                }
                            } action: {
                                guard cloudFilled, !connect.busy else { return }
                                attemptCloud(now: true)
                            }
                            .opacity(cloudFilled || connect.busy ? 1 : 0.6)
                        }
                    }

                    AutoConnectStatus(connect: connect)
                    PanelCard {
                        PanelButton {
                            OutLinkGlyph()
                            Text(step.title).foregroundStyle(YYColor.t1)
                            Spacer(minLength: 0)
                        } action: { NSWorkspace.shared.open(step.url) }
                    }
                    PanelHint(step.detail + (provider.isLocal || provider.isFreeOnline ? "" : String(localized: "。API Key 仅保存在本机，调用费用由服务商直接结算。")))
                }
                .padding(.horizontal, 1)
                .padding(.top, 2)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .onAppear {
            if provider.isLocal {
                url = provider.savedURL
                model = provider.savedModel
                if provider == .local { customName = UserDefaults.standard.string(forKey: "tts.local.title") ?? "" }
            }
        }
        .onDisappear { connect.reset() }
        .onChange(of: key) { if !provider.isLocal && !provider.isFreeOnline { attemptCloud(now: false) } }
        .onChange(of: region) { if !provider.isLocal && !provider.isFreeOnline && !key.isEmpty { attemptCloud(now: true) } }
    }

    private var cloudFilled: Bool {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !k.isEmpty else { return false }
        if provider == .volcengine, !k.contains("|") {
            return !appID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        }
        return true
    }

    private var localFilled: Bool {
        !url.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func attemptCloud(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        if provider == .volcengine, !k.contains("|"), appID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            if !now { return }
        }
        let p = provider
        let r: String? = provider == .azure ? region : nil
        let a: String? = provider == .volcengine ? appID : nil
        connect.start(String(localized: "正在连 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r, appID: a)
        } done: {
            done()
        }
    }

    private func attemptLocal(now: Bool) {
        let u = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !u.isEmpty : AutoConnect.looksLikeAddress(u) else { connect.reset(); return }
        let p = provider, m = model, k = key, n = customName
        connect.start(String(localized: "正在探测 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryLocal(url: u, model: m, key: k, customTitle: n)
        } done: {
            done()
        }
    }
}

private struct RegionRow: View {
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
                        .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    Image(systemName: "chevron.up.chevron.down").font(.yy(size: 8.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                }
            }
            .menuStyle(.button)
            .buttonStyle(.plain)
            .menuIndicator(.hidden)
            .fixedSize()
        }
    }
}

/// One connected service: Key or Local endpoint, proactive health status, 检查连接, 断开.
struct CloudVoicePanel: View {
    let provider: CloudVoice.Provider
    @State private var editing = false
    @State private var key = ""
    @State private var appID = UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName) ?? ""
    @State private var url = ""
    @State private var model = ""
    @State private var region = CloudVoice.Provider.region
    @State private var connect = AutoConnect()
    @State private var check = AutoConnect()
    @State private var confirming: YYConfirm?
    private var focus: ListeningFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let localLive = !provider.isLocal || LocalVoiceProbe.shared.isLive(provider)
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.inUse ? String(localized: "当前使用") : provider.account) {
                ServiceTile(mark: .cloud(provider), size: 34)
            }
            PanelCard {
                if provider.isFreeOnline {
                    PanelRow { Text("模式") } trailing: {
                        SettingsPill(String(localized: "免 Key · 零配置"), tone: .ok)
                    }
                    if editing {
                        PanelField(label: String(localized: "线路"), text: $url, prompt: String(localized: "留空用默认直连线路，或填 /v1 兼容地址"), autofocus: true, submit: saveFree, cancel: stopEditing)
                        PanelButton(systemImage: "checkmark", tint: YYColor.ok) {
                            Text("保存线路").foregroundStyle(YYColor.ok)
                            Spacer(minLength: 0)
                        } action: { saveFree() }
                    } else {
                        PanelButton {
                            Text("线路").fixedSize()
                            Spacer(minLength: 8)
                            Text(provider.endpointHost)
                                .font(.yy(size: 11, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1).truncationMode(.middle)
                            Image(systemName: "chevron.right").font(.yy(size: 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: {
                            url = UserDefaults.standard.string(forKey: provider.urlName) ?? ""
                            editing = true
                        }
                    }
                } else if provider.isLocal {
                    if editing {
                        PanelField(label: String(localized: "地址"), text: $url, prompt: provider.urlHint, autofocus: true, submit: saveLocal, cancel: stopEditing)
                        PanelField(label: String(localized: "模型"), text: $model, prompt: provider.defaultModel, submit: saveLocal, cancel: stopEditing)
                        PanelButton(systemImage: "checkmark", tint: YYColor.ok) {
                            Text("保存并重连").foregroundStyle(YYColor.ok)
                            Spacer(minLength: 0)
                        } action: { saveLocal() }
                    } else {
                        PanelButton {
                            Text("地址").fixedSize()
                            Spacer(minLength: 8)
                            Text(provider.savedURL)
                                .font(.yy(size: 11, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1).truncationMode(.middle)
                            Image(systemName: "chevron.right").font(.yy(size: 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: {
                            url = provider.savedURL
                            model = provider.savedModel
                            editing = true
                        }
                        PanelRow { Text("状态").fixedSize() } trailing: {
                            SettingsPill(localLive ? String(localized: "运行中") : String(localized: "服务未启动"), tone: localLive ? .ok : .warn)
                        }
                        PanelRow { Text("模型").fixedSize() } trailing: {
                            Text(provider.savedModel).font(.yy(size: 11.5, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1).truncationMode(.middle)
                        }
                    }
                } else {
                    if editing {
                        if provider == .volcengine {
                            PanelField(label: "AppID", text: $appID, prompt: String(localized: "应用 AppID"), submit: { attempt(now: true) }, cancel: stopEditing)
                        }
                        PanelField(label: provider == .volcengine ? "Token" : "Key",
                                   text: $key, prompt: String(localized: "贴上新的 Key"), secure: true, bad: connect.failed,
                                   autofocus: true, submit: { attempt(now: true) }, cancel: stopEditing,
                                   blur: { if key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { stopEditing() } })
                    } else {
                        if provider == .volcengine, let savedApp = UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName), !savedApp.isEmpty {
                            PanelRow { Text(verbatim: "AppID") } trailing: {
                                Text(savedApp).font(.yy(size: 11.5, design: .monospaced)).foregroundStyle(YYColor.t2)
                            }
                        }
                        PanelButton {
                            Text(verbatim: provider == .volcengine ? "Token" : "Key")
                            Spacer(minLength: 8)
                            Text(provider.maskedKey)
                                .font(.yy(size: 12, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1)
                            Image(systemName: "chevron.right").font(.yy(size: 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: { editing = true }
                        .help(Text("换一个 Key"))
                    }
                    if provider == .azure { RegionRow(region: $region) }
                }
                PanelRow { Text("声音") } trailing: {
                    Text("共 \(provider.voiceCount) 个").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                }
            }
            if editing {
                AutoConnectStatus(connect: connect)
                if case .idle = connect.phase, !provider.isLocal, !provider.isFreeOnline {
                    PanelHint(String(localized: "粘贴后自动验证，有效才会替换；按 Esc 取消"))
                }
            }
            PanelCard { checkRow }
            PanelCard {
                PanelButton(tint: YYColor.danger, destructive: true) {
                    Text(provider.isFreeOnline ? String(localized: "隐藏 \(provider.title)") : String(localized: "断开 \(provider.title)"))
                } action: {
                    confirming = YYConfirm(
                        title: provider.isFreeOnline ? String(localized: "隐藏 \(provider.title)？") : String(localized: "断开 \(provider.title)？"),
                        message: provider.isFreeOnline
                            ? String(localized: "隐藏后可在「添加声音来源」随时重新开启；正在使用该来源的书籍将切回本地语音。")
                            : (provider.isLocal
                                ? String(localized: "断开后将不再显示在列表中；使用该服务音色的书籍将切回本地自带音色。")
                                : String(localized: "将同时移除 API Key；使用该服务音色的书籍将切回本地音色。")),
                        button: provider.isFreeOnline ? String(localized: "隐藏") : String(localized: "断开")
                    ) {
                        provider.remove()
                        focus.page = nil
                        if focus.source == .cloud(provider) { focus.close() }
                        focus.reload()
                    }
                }
            }
            .yyConfirm($confirming)
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .onChange(of: key) { if !provider.isLocal && !provider.isFreeOnline { attempt(now: false) } }
        .onChange(of: region) {
            guard !provider.isLocal, !provider.isFreeOnline, let k = provider.key else { return }
            let p = provider, r = region
            check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { focus.reload() }
        }
    }

    private func stopEditing() {
        editing = false
        key = ""
        connect.reset()
    }

    private func saveFree() {
        let p = provider, u = url
        connect.start(String(localized: "正在验证在线线路…"), delay: .zero) {
            try await p.tryFree(customURL: u)
        } done: {
            stopEditing()
            focus.reload()
        }
    }

    private func saveLocal() {
        let p = provider, u = url, m = model, k = provider.key ?? ""
        connect.start(String(localized: "正在重连 \(p.title)…"), delay: .zero) {
            try await p.tryLocal(url: u, model: m, key: k)
        } done: {
            stopEditing()
            focus.reload()
        }
    }

    private func attempt(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        if provider == .volcengine, !k.contains("|"), appID.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, !now {
            return
        }
        let p = provider
        let r: String? = provider == .azure ? region : nil
        let a: String? = provider == .volcengine ? appID : nil
        connect.start(String(localized: "正在试新的 Key…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r, appID: a)
        } done: {
            stopEditing()
            focus.reload()
        }
    }

    private var checkRow: some View {
        PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
            Text(provider.isLocal ? "刷新音色与状态" : "检查连接").foregroundStyle(YYColor.ok)
            Spacer(minLength: 8)
            switch check.phase {
            case .idle: EmptyView()
            case .checking: ProgressView().controlSize(.mini)
            case .ok: SettingsPill(String(localized: "正常 · \(provider.voiceCount) 个音色"), tone: .ok)
            case .failed(let why):
                Text(why).font(.yy(size: 11)).foregroundStyle(YYColor.danger).lineLimit(2).multilineTextAlignment(.trailing)
            }
        } action: {
            guard !check.busy else { return }
            let p = provider
            if p.isFreeOnline {
                check.start("", delay: .zero) {
                    try await p.tryFree()
                } done: { focus.reload() }
            } else if p.isLocal {
                check.start("", delay: .zero) {
                    try await p.tryLocal(url: p.savedURL, model: p.savedModel, key: p.key ?? "")
                } done: { focus.reload() }
            } else if let k = p.key {
                let r: String? = p == .azure ? region : nil
                let a: String? = p == .volcengine ? UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName) : nil
                check.start("", delay: .zero) { try await p.tryKey(k, region: r, appID: a) } done: { focus.reload() }
            }
        }
    }
}
#endif
