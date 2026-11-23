import SwiftUI
#if os(macOS)
import AppKit
#endif

/// What the right column of 朗读 shows, and the state the two columns share.
@MainActor @Observable
final class ListeningFocus {
    static let shared = ListeningFocus()

    /// The service shown in the right column (its key / endpoint); nil: the voice card.
    var page: CloudVoice.Provider?
    /// Adding a new voice source from the catalog sheet.
    var addingSheet = false
    /// The source whose voices the middle shows; nil: the list of sources.
    var source: VoiceSource?
    /// Which kind of book the voices are being chosen for.
    var book = VoiceBook.zh
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
    #if DEBUG
    var debugApplied = false
    #endif

    func line(_ book: VoiceBook) -> String { lines[book] ?? book.line }

    func listen(_ voice: any Voice, text: String) {
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

    func pick(_ voice: any Voice, book: VoiceBook) {
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
        added = CloudVoice.Provider.allCases.filter(\.isSetUp)
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
            } else {
                self.tick &+= 1
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
        if isLocal {
            d.set(true, forKey: dismissedName)
            LocalVoiceProbe.shared.clear(self)
        }
        if self == .local { d.removeObject(forKey: "tts.local.title") }
        if self == .azure { d.removeObject(forKey: Self.regionName) }
        for tag in ["zh", "en"] where d.string(forKey: "player.voice.\(tag)")?.hasPrefix("cloud.\(rawValue).") == true {
            d.removeObject(forKey: "player.voice.\(tag)")
        }
    }

    @MainActor func tryKey(_ key: String, region: String?) async throws -> AutoConnect.Outcome {
        let oldKey = self.key, oldRegion = UserDefaults.standard.string(forKey: Self.regionName)
        Keychain.write(keyName, key)
        if let region { Self.region = region }
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
            throw error
        }
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
    let pick: (CloudVoice.Provider) -> Void
    @State private var query = ""
    @State private var hovered: CloudVoice.Provider?
    @Environment(\.dismiss) private var dismiss

    private static let columns = 4

    var body: some View {
        let matches = CloudVoice.Provider.available(matching: query).filter { $0 != .local }
        VStack(alignment: .leading, spacing: 0) {
            YYSheetHeader(title: String(localized: "添加声音来源"), subtitle: String(localized: "朗读"), close: { dismiss() })
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").font(.yy(size: 11.5, weight: .medium)).foregroundStyle(YYColor.t3)
                TextField("", text: $query, prompt: Text("搜索语音来源，比如 小米、硅基流动、VoiceStudio"))
                    .textFieldStyle(.plain)
                    .font(.yy(size: 12.5))
                    .autocorrectionDisabled()
                    #if os(iOS)
                    .textInputAutocapitalization(.never)
                    #endif
                    .onSubmit { if let first = matches.first { choose(first) } }
            }
            .padding(.horizontal, 10)
            .frame(height: 32)
            .background(YYColor.inset, in: .rect(cornerRadius: 8))
            .padding(.top, 10)

            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    ForEach(CloudVoice.Group.allCases, id: \.self) { group in
                        let list = matches.filter { $0.group == group }
                        if !list.isEmpty {
                            VStack(alignment: .leading, spacing: 5) {
                                Text(group.title)
                                    .font(.yy(size: 11, weight: .semibold))
                                    .foregroundStyle(YYColor.t3)
                                    .padding(.leading, 4)
                                LazyVGrid(
                                    columns: Array(repeating: GridItem(.flexible(), spacing: 7), count: Self.columns),
                                    spacing: 7
                                ) {
                                    ForEach(list) { p in providerTile(p) }
                                }
                            }
                        }
                    }
                    if matches.isEmpty {
                        Text("没找到匹配的内置渠道，可在下方添加自定义 OpenAI 兼容接口")
                            .font(.yy(size: 12)).foregroundStyle(YYColor.t3)
                            .frame(maxWidth: .infinity).padding(.vertical, 32)
                    }
                }
                .padding(.vertical, 10)
            }
            .scrollBounceBehavior(.basedOnSize)

            #if os(macOS)
            HStack(spacing: 6) {
                if let h = hovered {
                    Text(h.title).font(.yy(size: 11, weight: .semibold)).foregroundStyle(YYColor.t2).fixedSize()
                    Text("·").foregroundStyle(YYColor.t3)
                    Text(h.blurb).font(.yy(size: 11)).foregroundStyle(YYColor.t3).lineLimit(1)
                    if let badge = h.badge {
                        Text(badge)
                            .font(.yy(size: 10, weight: .medium))
                            .foregroundStyle(h == .xiaomi ? YYColor.ok : YYTint.standard.accent)
                            .fixedSize()
                    }
                } else {
                    Text("鼠标移到任意来源可查看详情；绿点含免费额度，钥匙图标可直接复用「书伴」Key")
                        .font(.yy(size: 11)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .frame(height: 20)
            .padding(.bottom, 6)
            #endif

            Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, -18)
            Button { choose(.local) } label: {
                HStack(spacing: 8) {
                    Image(systemName: "plus").font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: 20, height: 20)
                    Text("自定义语音接口").font(.yy(size: 12.5, weight: .medium)).foregroundStyle(YYColor.t1).fixedSize()
                    Text("GPT-SoVITS、Kokoro、unspeech 或局域网 /v1/audio/speech").font(.yy(size: 11)).foregroundStyle(YYColor.t3).lineLimit(1)
                    Spacer(minLength: 0)
                    Image(systemName: "chevron.right").font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYColor.t3)
                }
            }
            .buttonStyle(VoicePickRowStyle())
            .padding(.top, 6)
        }
        .padding(.horizontal, 18)
        .padding(.bottom, 12)
        .padding(.top, YYUI.sheetGrabberTop)
        #if os(macOS)
        .frame(width: 656, height: 476)
        #else
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .presentationBackground(YYColor.bg)
        #endif
        .background(YYColor.bg)
    }

    private func choose(_ p: CloudVoice.Provider) {
        dismiss()
        pick(p)
    }

    private func providerTile(_ p: CloudVoice.Provider) -> some View {
        Button { choose(p) } label: {
            #if os(macOS)
            HStack(spacing: 8) {
                ServiceTile(mark: .cloud(p), size: 24)
                VStack(alignment: .leading, spacing: 1.5) {
                    HStack(spacing: 4) {
                        Text(p.title)
                            .font(.yy(size: 12, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                            .lineLimit(1)
                        Spacer(minLength: 0)
                        if p.companionSharedKey != nil {
                            Image(systemName: "key.fill")
                                .font(.system(size: 8, weight: .semibold))
                                .foregroundStyle(YYColor.ok)
                        } else if p == .xiaomi || p == .azure || p == .deepgram {
                            Circle()
                                .fill(YYColor.ok)
                                .frame(width: 5, height: 5)
                        }
                    }
                    Text(p.shortTag)
                        .font(.yy(size: 10))
                        .foregroundStyle(p == .xiaomi || p.companionSharedKey != nil ? YYColor.ok : YYColor.t3)
                        .lineLimit(1)
                }
            }
            #else
            VStack(spacing: 5) {
                ZStack(alignment: .topTrailing) {
                    ServiceTile(mark: .cloud(p), size: 28)
                    if p.companionSharedKey != nil || p == .xiaomi || p == .azure || p == .deepgram {
                        Circle()
                            .fill(YYColor.ok)
                            .frame(width: 6, height: 6)
                            .offset(x: 2, y: -2)
                    }
                }
                Text(p.title)
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .minimumScaleFactor(0.85)
            }
            .frame(maxWidth: .infinity)
            #endif
        }
        .buttonStyle(VoiceGridTileStyle())
        .onHover { inside in
            if inside { hovered = p }
            else if hovered == p { hovered = nil }
        }
        .help(Text("\(p.title) · \(p.blurb)"))
    }
}

private struct VoiceGridTileStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Tile(configuration: configuration) }

    private struct Tile: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            #if os(macOS)
            let h: CGFloat = 48
            #else
            let h: CGFloat = 64
            #endif
            configuration.label
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .frame(maxWidth: .infinity, minHeight: h)
                .background(
                    hovering || configuration.isPressed ? YYColor.inset : YYColor.card,
                    in: .rect(cornerRadius: 9)
                )
                .overlay(
                    RoundedRectangle(cornerRadius: 9)
                        .strokeBorder(hovering ? YYColor.t3.opacity(0.45) : YYColor.line, lineWidth: 0.5)
                )
                .scaleEffect(configuration.isPressed ? 0.985 : 1.0)
                .animation(.easeOut(duration: 0.12), value: configuration.isPressed)
                .contentShape(.rect)
                .onHover { hovering = $0 }
        }
    }
}

private struct VoicePickRowStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Row(configuration: configuration) }

    private struct Row: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .padding(.horizontal, YYUI.s(8))
                .padding(.vertical, YYUI.s(6))
                .background(hovering || configuration.isPressed ? YYColor.inset : .clear, in: .rect(cornerRadius: YYUI.s(8)))
                .contentShape(.rect)
                .onHover { hovering = $0 }
        }
    }
}

#if os(macOS)
// MARK: - The right column (macOS)

struct ListeningInspector: View {
    private var focus: ListeningFocus { .shared }

    var body: some View {
        let _ = focus.tick
        Group {
            if let p = focus.page {
                if p.isSetUp { CloudVoicePanel(provider: p) } else { CloudVoiceKeyPanel(provider: p) }
            }
        }
        .id(focus.page.map { $0.id + ($0.isSetUp ? ".on" : "") })
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
    private let source = VoiceSource.mac

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: source.title, detail: String(localized: "本机离线声音")) {
                ServiceTile(mark: source.mark, size: 34)
            }
            PanelCard {
                PanelRow { Text("声音") } trailing: {
                    Text("共 \(source.voiceCount) 个").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                }
                PanelRow { Text("费用") } trailing: {
                    Text("免费 · 不联网").font(.yy(size: 12)).foregroundStyle(YYColor.t2)
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
            PanelHint(String(localized: "自带 48 个离线人声与声音克隆开箱即用；也可前往系统设置下载更多 Apple 自然音色。"))
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }
}

/// One not set up yet: cloud Key (with 1-tap reuse from 书伴) OR local endpoint URL + Model.
private struct CloudVoiceKeyPanel: View {
    let provider: CloudVoice.Provider
    @State private var key = ""
    @State private var region = CloudVoice.Provider.region
    @State private var url = ""
    @State private var model = ""
    @State private var customName = ""
    @State private var connect = AutoConnect()
    private var focus: ListeningFocus { .shared }

    var body: some View {
        let step = provider.keyStep
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.blurb) {
                ServiceTile(mark: .cloud(provider), size: 34)
            }

            if provider.isLocal {
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
                        if connect.busy { ProgressView().controlSize(.mini) }
                    } action: { attemptLocal(now: true) }
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
                }
                PanelCard {
                    if provider == .azure { RegionRow(region: $region) }
                    PanelField(label: "Key", text: $key, prompt: provider == .azure ? String(localized: "密钥 1") : provider.keyHint,
                               secure: true, bad: connect.failed, autofocus: provider.companionSharedKey == nil, submit: { attemptCloud(now: true) })
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
            PanelHint(step.detail + (provider.isLocal ? "" : String(localized: "。API Key 仅保存在本机，调用费用由服务商直接结算。")))
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .onAppear {
            if provider.isLocal {
                url = provider.savedURL
                model = provider.savedModel
                if provider == .local { customName = UserDefaults.standard.string(forKey: "tts.local.title") ?? "" }
            }
        }
        .onChange(of: key) { if !provider.isLocal { attemptCloud(now: false) } }
        .onChange(of: region) { if !provider.isLocal && !key.isEmpty { attemptCloud(now: true) } }
    }

    private func attemptCloud(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        let p = provider, r: String? = provider == .azure ? region : nil
        connect.start(String(localized: "正在连 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r)
        } done: {
            focus.reload()
            focus.page = nil
            focus.open(.cloud(p))
        }
    }

    private func attemptLocal(now: Bool) {
        let u = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !u.isEmpty : AutoConnect.looksLikeAddress(u) else { connect.reset(); return }
        let p = provider, m = model, k = key, n = customName
        connect.start(String(localized: "正在探测 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryLocal(url: u, model: m, key: k, customTitle: n)
        } done: {
            focus.reload()
            focus.page = nil
            focus.open(.cloud(p))
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
                if provider.isLocal {
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
                        PanelField(label: "Key", text: $key, prompt: String(localized: "贴上新的 Key"), secure: true, bad: connect.failed,
                                   autofocus: true, submit: { attempt(now: true) }, cancel: stopEditing,
                                   blur: { if key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { stopEditing() } })
                    } else {
                        PanelButton {
                            Text(verbatim: "Key")
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
                if case .idle = connect.phase, !provider.isLocal {
                    PanelHint(String(localized: "粘贴后自动验证，有效才会替换；按 Esc 取消"))
                }
            }
            PanelCard { checkRow }
            PanelCard {
                PanelButton(tint: YYColor.danger, destructive: true) {
                    Text("断开 \(provider.title)")
                } action: {
                    confirming = YYConfirm(
                        title: String(localized: "断开 \(provider.title)？"),
                        message: provider.isLocal
                            ? String(localized: "断开后将不再显示在列表中；使用该服务音色的书籍将切回本地自带音色。")
                            : String(localized: "将同时移除 API Key；使用该服务音色的书籍将切回本地音色。"),
                        button: String(localized: "断开")
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
        .onChange(of: key) { if !provider.isLocal { attempt(now: false) } }
        .onChange(of: region) {
            guard !provider.isLocal, let k = provider.key else { return }
            let p = provider, r = region
            check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { focus.reload() }
        }
    }

    private func stopEditing() {
        editing = false
        key = ""
        connect.reset()
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
        let p = provider, r: String? = provider == .azure ? region : nil
        connect.start(String(localized: "正在试新的 Key…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r)
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
            if p.isLocal {
                check.start("", delay: .zero) {
                    try await p.tryLocal(url: p.savedURL, model: p.savedModel, key: p.key ?? "")
                } done: { focus.reload() }
            } else if let k = p.key {
                let r: String? = p == .azure ? region : nil
                check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { focus.reload() }
            }
        }
    }
}
#endif
