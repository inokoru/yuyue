import SwiftUI
#if os(macOS)
import AppKit
#else
import UIKit
#endif

#if os(macOS)

/// 设置 · 书伴. The middle is one list: this Mac's own (Apple 智能), the services added, 添加服务商.
/// Choosing one puts it in the right column: its model, 书伴用它, its key, 检查连接, 移除. Its
/// model opens a list there that can be searched: 在用, 最近用过, 推荐, then all it lists.
struct CompanionSettings: View {
    let library: Library
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let choice = focus.choice
        let _ = focus.tick
        VStack(alignment: .leading, spacing: 12) {
            SettingsHeading(String(localized: "模型服务"))
            SettingsCard {
                let apple = Companion.appleAvailability == nil
                ServiceRow(selected: focus.page == .service(CompanionProvider.apple.id) || focus.page == nil && choice.current.provider.kind == .apple,
                           dim: !apple) {
                    ServiceTile(mark: .thisMac)
                } title: {
                    Text("这台 Mac")
                    if apple && choice.current.provider.kind == .apple { InUsePill() }
                } detail: {
                    Text("Apple 智能 · 本地运行，免费免联网")
                } trailing: {
                    switch Companion.appleState {
                    case .available: EmptyView()
                    case .notEnabled: SettingsPill(String(localized: "未开启"), tone: .warn)
                    case .downloading: SettingsPill(String(localized: "准备中"), tone: .warn)
                    case .unsupported: SettingsPill(String(localized: "当前系统不支持"), tone: .warn)
                    }
                } action: { focus.page = .service(CompanionProvider.apple.id) }

                ForEach(choice.added) { p in
                    let config = CompanionConfig.saved(p)
                    ServiceRow(selected: focus.shows(p.id) || focus.page == nil && choice.current.provider == p) {
                        ServiceTile(mark: .provider(p))
                    } title: {
                        Text(p.title)
                        if choice.current.provider == p { InUsePill() }
                    } detail: {
                        Text(([config.model.isEmpty ? String(localized: "未选择模型") : ModelNames.name(config.model)]
                              + (config.key.isEmpty ? [] : ["Key " + config.maskedKey])).joined(separator: " · "))
                    } trailing: {
                        EmptyView()
                    } action: { focus.page = .service(p.id) }
                }

                ServiceRow(selected: false) {
                    ServiceTile(mark: .add)
                } title: {
                    Text("添加服务商").foregroundStyle(YYColor.t2)
                } detail: {
                    Text("Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、本机 Ollama…")
                } trailing: {
                    EmptyView()
                } action: { focus.sheet = .add }
            }
            SettingsNote(String(localized: "API Key 仅保存在这台 Mac 上，不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"))
            CompanionUsageAndCostSection()
            Spacer(minLength: 0)
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
        }
        .sheet(item: Binding(get: { focus.sheet }, set: { focus.sheet = $0 }), onDismiss: { focus.reload() }) {
            CompanionProviderSheet(start: $0).buttonStyle(.automatic)
        }
        .onAppear {
            focus.reload()
            #if DEBUG
            // `-debugCompanion add` opens 添加服务商, `edit:<id>` its form, `<id>` that service in the right column.
            if let d = UserDefaults.standard.string(forKey: "debugCompanion") {
                if d == "add" { focus.sheet = .add }
                else if d.hasPrefix("edit:"), let p = CompanionProvider.find(String(d.dropFirst(5))) { focus.sheet = .edit(p) }
                else { focus.page = .service(d) }
            }
            #endif
        }
    }
}
#endif

/// What the right column of 书伴 shows, and the state the two columns share.
@MainActor @Observable
final class CompanionFocus {
    static let shared = CompanionFocus()

    enum Page: Hashable {
        case service(String)
        case models(String)
    }

    /// nil: the service 书伴 uses.
    var page: Page?
    /// 添加服务商 stays a sheet; a service added is changed in the right column.
    var sheet: CompanionProviderSheet.Start?
    let choice = CompanionChoice()
    /// Bumped when the defaults change (an undo, a sheet), so both columns read them again.
    var tick = 0

    func reload() {
        choice.reload()
        if case .service(let id) = page, id != CompanionProvider.apple.id, CompanionProvider.find(id).map({ p in choice.added.contains(p) }) != true { page = nil }
        tick &+= 1
    }

    func shows(_ id: String) -> Bool { page == .service(id) || page == .models(id) }

    /// The service the right column is about.
    var provider: CompanionProvider {
        switch page {
        case .service(let id), .models(let id): CompanionProvider.find(id) ?? choice.current.provider
        case nil: choice.current.provider
        }
    }
}

// MARK: - The right column

/// 书伴 in the right column: the service chosen in the middle, or its models.
struct CompanionInspector: View {
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let p = focus.provider
        Group {
            if case .models = focus.page {
                ModelList(provider: p)
            } else {
                ServicePanel(provider: p)
            }
        }
        .id(p.id + (focus.page.map { "\($0)" } ?? ""))
        .transition(.opacity.combined(with: .offset(y: 3)))
    }
}

/// One service: its model, 书伴用它, its key, 检查连接, 移除.
struct ServicePanel: View {
    let provider: CompanionProvider
    /// Opens its models; nil: the right column's own page.
    var openModels: (() -> Void)?
    /// After 移除.
    var removed: (() -> Void)?
    @State private var checking = false
    @State private var result: Result<TimeInterval, Error>?
    @State private var confirming: YYConfirm?
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let config = CompanionConfig.saved(provider)
        let inUse = focus.choice.current.provider == provider
        let apple = provider.kind == .apple
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: apple ? SettingsDevice.name : provider.title,
                        detail: inUse ? String(localized: "当前使用") : (apple ? String(localized: "Apple 智能") : provider.account)) {
                ServiceTile(mark: apple ? .thisMac : .provider(provider), size: 28)
            }
            if apple, Companion.appleState != .available {
                switch Companion.appleState {
                case .available: EmptyView()
                case .notEnabled(let why):
                    PanelCard {
                        PanelRow { Text("Apple 智能") } trailing: { SettingsPill(String(localized: "未开启"), tone: .warn) }
                    }
                    PanelHint(why + String(localized: "。开启后可在本地免费使用书伴，无需联网。"))
                    PanelCard {
                        PanelButton(systemImage: "arrow.up.forward.app", tint: YYColor.t1) {
                            #if os(macOS)
                            Text("打开系统设置")
                            #else
                            Text("打开设置")
                            #endif
                        } action: {
                            #if os(macOS)
                            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Siri-Settings.extension")!)
                            #else
                            if let url = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(url) }
                            #endif
                        }
                    }
                case .downloading(let why):
                    PanelCard {
                        PanelRow { Text("Apple 智能") } trailing: { SettingsPill(String(localized: "准备中"), tone: .warn) }
                    }
                    PanelHint(why)
                case .unsupported(let why):
                    PanelCard {
                        PanelRow { Text("Apple 智能") } trailing: { SettingsPill(String(localized: "不支持"), tone: .warn) }
                    }
                    PanelHint(why)
                }
            } else {
                PanelCard {
                    if !apple {
                        PanelButton {
                            Text("模型")
                            Spacer(minLength: 8)
                            Text(config.model.isEmpty ? String(localized: "选择…") : ModelNames.name(config.model))
                                .foregroundStyle(config.model.isEmpty ? YYColor.t3 : YYColor.t1).lineLimit(1)
                            Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: { if let openModels { openModels() } else { focus.page = .models(provider.id) } }
                    }
                    PanelRow { Text("设为当前服务") } trailing: {
                        Toggle("设为当前服务", isOn: Binding(get: { inUse }, set: { on in
                            guard on, !inUse else { return }
                            CompanionConfig.current = CompanionConfig.saved(provider)
                            focus.reload()
                        }))
                        .labelsHidden().toggleStyle(.switch).controlSize(.small).tint(YYTint.standard.fill)
                        .disabled(inUse)
                    }
                    #if os(iOS)
                    // iPhone: no card of its own; it checks what's above it.
                    if apple { check(config) }
                    #endif
                }
                #if os(macOS)
                if !apple {
                    ServiceKeyCard(provider: provider)
                }
                PanelCard { check(config) }
                #else
                if !apple {
                    ServiceKeyCard(provider: provider) { check(config) }
                }
                #endif
                if !apple {
                    PanelDestructive(String(localized: "移除 \(provider.title)")) {
                        confirming = YYConfirm(title: String(localized: "移除 \(provider.title)？"),
                                               message: String(localized: "它的 Key 也一起删掉。"),
                                               button: String(localized: "移除")) {
                            CompanionConfig.remove(provider)
                            focus.page = nil
                            focus.reload()
                            removed?()
                        }
                    }
                    .yyConfirm($confirming)
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }

    /// 检查连接, in green: asks it one short question; the answer is how long it took.
    private func check(_ config: CompanionConfig) -> some View {
        PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
            Text("检查连接").foregroundStyle(YYColor.ok)
            Spacer(minLength: 8)
            if checking {
                ProgressView().controlSize(.mini)
            } else {
                switch result {
                case .success(let t)?:
                    SettingsPill(String(localized: "正常 · \(t.formatted(.number.precision(.fractionLength(1)))) 秒"), tone: .ok)
                case .failure(let e)?:
                    Text(Companion.message(for: e)).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.danger)
                        .lineLimit(2).multilineTextAlignment(.trailing)
                case nil:
                    Text(config.isReady ? "" : config.isConnected ? String(localized: "先选模型") : String(localized: "还没填好"))
                        .font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
                }
            }
        } action: {
            guard config.isReady, !checking else { return }
            withAnimation(YYMotion.quick) {
                checking = true
                result = nil
            }
            Task {
                let r = await CompanionTest.run(config)
                // The spinner gives way to how long it took, or why not, with a fade.
                withAnimation(YYMotion.standard) {
                    result = r
                    checking = false
                }
            }
        }
    }
}

/// A service's models in the right column: a search line; then 在用, 最近用过, 推荐 and all of
/// them (grouped by maker when the service resells others'). Typing narrows the list; return
/// takes the first; a name not in it can still be used.
struct ModelList: View {
    let provider: CompanionProvider
    /// Back to the service; nil: the right column's own page.
    var back: (() -> Void)?
    /// Adding it: what's typed so far, to list the models with; nothing saved yet.
    var draft: CompanionConfig?
    /// Already fetched models from the form, so opening the list shows them immediately.
    var initialModels: [String] = []
    /// Called whenever models are successfully fetched from the server.
    var fetched: (([String]) -> Void)?
    /// Adding it: the model chosen goes back to the form instead of being saved.
    var picked: ((String) -> Void)?
    @State private var query = ""
    @State private var models: [String] = []
    @State private var loading = false
    @State private var loaded = false
    @State private var errorMessage: String?
    @FocusState private var searching: Bool
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let current = draft?.model ?? CompanionConfig.saved(provider).model
        VStack(alignment: .leading, spacing: 10) {
            // iPhone: the navigation bar has ‹ and the title.
            #if os(macOS)
            HStack(spacing: 8) {
                Button { goBack() } label: {
                    Image(systemName: "chevron.left").font(.yy(size: PanelScale.k * 11, weight: .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: 24, height: 24).contentShape(.circle)
                }
                .buttonStyle(HoverCircleStyle())
                .help(Text("回到 \(provider.title)"))
                ServiceTile(mark: .provider(provider), size: 26)
                Text("选择模型").font(.yy(size: PanelScale.k * 13.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                Spacer(minLength: 0)
                refreshButton
            }
            .padding(.leading, -4)
            #endif

            HStack(spacing: 7) {
                Image(systemName: "magnifyingglass").font(.yy(size: PanelScale.k * 11.5)).foregroundStyle(YYColor.t3)
                TextField("", text: $query, prompt: Text(models.isEmpty ? String(localized: "搜模型名或直接输入模型 ID") : String(localized: "搜 \(models.count) 个模型：名字或 id")))
                    .textFieldStyle(.plain).font(.yy(size: PanelScale.k * 12.5))
                    .focused($searching)
                    .onSubmit { if let first = hits.first { choose(first) } else if !trimmed.isEmpty { choose(trimmed) } }
                    .onKeyPress(.escape) {
                        if query.isEmpty { goBack() } else { query = "" }
                        return .handled
                    }
                #if os(iOS)
                refreshButton
                #endif
            }
            .padding(.horizontal, 10)
            .frame(height: 30)
            .background(YYColor.inset, in: .rect(cornerRadius: 8))

            if trimmed.isEmpty { sections(current: current) } else { results(current: current) }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .task {
            #if os(macOS)
            searching = true
            #endif
            if models.isEmpty, !initialModels.isEmpty {
                models = initialModels
                loaded = true
            }
            await refresh()
        }
    }

    private var refreshButton: some View {
        Button { Task { await refresh() } } label: {
            Group {
                if loading {
                    ProgressView().controlSize(.mini)
                } else {
                    Image(systemName: "arrow.clockwise").font(.yy(size: PanelScale.k * 11, weight: .medium)).foregroundStyle(YYColor.t2)
                }
            }
            .frame(width: 24, height: 24)
            .contentShape(.circle)
        }
        .buttonStyle(HoverCircleStyle())
        .disabled(loading)
        .help(Text("获取模型列表"))
        .accessibilityLabel(Text("获取模型列表"))
    }

    private func refresh() async {
        guard !loading else { return }
        let config = draft ?? CompanionConfig.saved(provider)
        guard config.isConnected else {
            withAnimation(YYMotion.standard) {
                loading = false
                loaded = true
                errorMessage = provider.needsKey && config.key.isEmpty
                    ? String(localized: "先填好 API Key 再获取模型列表")
                    : String(localized: "先填好接口地址再获取模型列表")
            }
            return
        }
        withAnimation(YYMotion.quick) {
            loading = true
            errorMessage = nil
        }
        do {
            let list = try await Companion.models(config).sorted()
            withAnimation(YYMotion.standard) {
                models = list
                loaded = true
                loading = false
                errorMessage = nil
            }
            fetched?(list)
        } catch {
            withAnimation(YYMotion.standard) {
                loaded = true
                loading = false
                errorMessage = Companion.formMessage(for: error)
            }
        }
    }

    private var trimmed: String { query.trimmingCharacters(in: .whitespaces) }

    private var hits: [String] {
        let q = trimmed.lowercased()
        return models.filter { $0.lowercased().contains(q) || ModelNames.name($0).lowercased().contains(q) }
    }

    @ViewBuilder private func sections(current: String) -> some View {
        let recs = ModelNames.recommended[provider.id, default: []].filter { r in models.contains(r.id) }
        let recent = ModelNames.recent(provider.id).filter { $0 != current && (models.isEmpty || models.contains($0)) }
        if !current.isEmpty && !recs.contains(where: { $0.id == current }) {
            PanelLabel(String(localized: "在用"))
            PanelCard { item(current, current: current) }
        }
        if !recent.isEmpty {
            PanelLabel(String(localized: "最近用过"))
            PanelCard { ForEach(recent, id: \.self) { item($0, current: current) } }
        }
        if !recs.isEmpty {
            PanelLabel(String(localized: "推荐"))
            PanelCard { ForEach(recs, id: \.id) { item($0.id, why: $0.why, current: current) } }
        }
        if loading && models.isEmpty {
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text("正在获取模型列表…").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
            }
            .padding(.leading, 12)
        } else if let errorMessage {
            PanelCard {
                PanelButton(systemImage: "arrow.clockwise", tint: YYColor.t1) {
                    Text("重新获取模型列表")
                    Spacer(minLength: 6)
                } action: { Task { await refresh() } }
            }
            PanelHint(String(localized: "未能获取模型列表：\(errorMessage)。也可直接在上方输入模型 ID 后按回车使用。"))
        } else if models.isEmpty && loaded {
            PanelCard {
                PanelButton(systemImage: "arrow.clockwise", tint: YYColor.t1) {
                    Text("重新获取模型列表")
                    Spacer(minLength: 6)
                } action: { Task { await refresh() } }
            }
            PanelHint(String(localized: "服务商返回了空的模型列表；也可直接在上方输入模型 ID 后按回车使用。"))
        } else if !models.isEmpty {
            PanelLabel(String(localized: "全部 \(models.count) 个"))
            let groups = ModelNames.groups(models)
            if groups.count > 1 {
                ForEach(groups, id: \.maker) { g in
                    HStack(spacing: 6) {
                        Text(g.maker).font(.yy(size: PanelScale.k * 11, weight: .semibold)).foregroundStyle(YYColor.t3)
                        Spacer()
                        Text("\(g.models.count)").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).monospacedDigit()
                    }
                    .padding(.horizontal, 12).padding(.bottom, -4)
                    PanelCard { ForEach(g.models, id: \.self) { item($0, current: current) } }
                }
            } else {
                PanelCard { ForEach(models, id: \.self) { item($0, current: current) } }
            }
        }
    }

    @ViewBuilder private func results(current: String) -> some View {
        let found = hits
        Text(found.isEmpty ? String(localized: "没有叫这个的") : String(localized: "找到 \(found.count) 个 · 回车选第一个"))
            .font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).padding(.leading, 12)
        PanelCard {
            ForEach(Array(found.prefix(40).enumerated()), id: \.element) { i, m in
                item(m, current: current, first: i == 0, highlight: trimmed)
            }
            if !models.contains(trimmed) {
                PanelButton {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("用“\(trimmed)”这个名字").foregroundStyle(YYColor.t1)
                        Text("列表里没有的新模型，照这个名字去问").font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                } action: { choose(trimmed) }
            }
        }
    }

    private func item(_ id: String, why: String? = nil, current: String, first: Bool = false, highlight: String = "") -> some View {
        let on = id == current
        return PanelButton(selected: on, outlined: first) {
            VStack(alignment: .leading, spacing: 1) {
                Text(ModelNames.name(id)).font(.yy(size: PanelScale.k * 12.5, weight: on ? .semibold : .regular)).foregroundStyle(YYColor.t1).lineLimit(1)
                if let why {
                    Text(why).font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(YYColor.t2)
                } else if ModelNames.name(id) != id {
                    Text(id).font(.yy(size: PanelScale.k * 10, design: .monospaced)).foregroundStyle(YYColor.t3).lineLimit(1).truncationMode(.middle)
                }
            }
            Spacer(minLength: 6)
            if on { Image(systemName: "checkmark").font(.yy(size: PanelScale.k * 11, weight: .bold)).foregroundStyle(YYColor.t1) }
        } action: { choose(id) }
    }

    private func choose(_ id: String) {
        if let picked { picked(id); return }
        var config = CompanionConfig.saved(provider)
        let old = config.model
        if old != id {
            ModelNames.used(old, provider: provider.id)
            config.model = id
            config.save()
            #if os(macOS)
            SettingsLog.shared.record(title: String(localized: "\(provider.title) 的模型"), to: ModelNames.name(id), from: ModelNames.name(old),
                                      undo: .defaults(key: "companion.model.\(provider.id)", old: .string(old)))
            #endif
        }
        goBack()
        focus.reload()
    }

    private func goBack() {
        if let back { back() } else { focus.page = .service(provider.id) }
    }
}

// MARK: - Model names

/// Model ids as people say them, the few worth suggesting per service, and the ones used lately.
enum ModelNames {
    struct Pick { let id: String; let name: String; let why: String }

    static let recommended: [String: [Pick]] = [
        "claude": [.init(id: "claude-opus-4-5", name: "Claude Opus 4.5", why: String(localized: "最聪明，读懂难书")),
                   .init(id: "claude-sonnet-4-5", name: "Claude Sonnet 4.5", why: String(localized: "聪明又快，平时就用它")),
                   .init(id: "claude-haiku-4-5", name: "Claude Haiku 4.5", why: String(localized: "最快最便宜，查词够用"))],
        "openai": [.init(id: "gpt-5", name: "GPT-5", why: String(localized: "最聪明")),
                   .init(id: "gpt-5-mini", name: "GPT-5 mini", why: String(localized: "快又便宜"))],
        "deepseek": [.init(id: "deepseek-chat", name: "DeepSeek Chat", why: String(localized: "中文好，平时就用它")),
                     .init(id: "deepseek-reasoner", name: "DeepSeek Reasoner", why: String(localized: "想得深，慢一点"))],
        "gemini": [.init(id: "gemini-2.5-pro", name: "Gemini 2.5 Pro", why: String(localized: "最聪明")),
                   .init(id: "gemini-2.5-flash", name: "Gemini 2.5 Flash", why: String(localized: "快又便宜"))],
        "openrouter": [.init(id: "anthropic/claude-sonnet-4-5", name: "Claude Sonnet 4.5", why: String(localized: "读书最稳")),
                       .init(id: "google/gemini-2.5-flash", name: "Gemini 2.5 Flash", why: String(localized: "快又便宜")),
                       .init(id: "deepseek/deepseek-chat", name: "DeepSeek Chat", why: String(localized: "中文好，很便宜"))],
        "moonshot": [.init(id: "kimi-k2-turbo-preview", name: "Kimi K2 Turbo", why: String(localized: "长书一口气读完"))],
        "dashscope": [.init(id: "qwen-max", name: String(localized: "通义 Max"), why: String(localized: "最聪明")),
                      .init(id: "qwen-plus", name: String(localized: "通义 Plus"), why: String(localized: "快又便宜"))],
        "zhipu": [.init(id: "glm-4.6", name: "GLM-4.6", why: String(localized: "中文好"))],
    ]

    private static let names: [String: String] = Dictionary(
        recommended.values.flatMap { $0 }.map { ($0.id, $0.name) }, uniquingKeysWith: { a, _ in a })

    /// "Claude Sonnet 5.5" for the ones known; otherwise the id.
    static func name(_ id: String) -> String { names[id] ?? id }

    // The last three used, newest first.
    static func recent(_ provider: String) -> [String] {
        UserDefaults.standard.stringArray(forKey: "companion.recent.\(provider)") ?? []
    }

    static func used(_ id: String, provider: String) {
        guard !id.isEmpty else { return }
        let list = [id] + recent(provider).filter { $0 != id }
        UserDefaults.standard.set(Array(list.prefix(3)), forKey: "companion.recent.\(provider)")
    }

    /// "anthropic/claude-…" by the part before the slash, when enough of them have one.
    static func groups(_ ids: [String]) -> [(maker: String, models: [String])] {
        guard ids.count > 12, ids.filter({ $0.contains("/") }).count * 2 > ids.count else { return [("", ids)] }
        let grouped = Dictionary(grouping: ids) { $0.split(separator: "/").first.map(String.init) ?? "" }
        return grouped.keys.sorted().map { ($0.isEmpty ? String(localized: "其他") : $0, grouped[$0]!) }
    }
}

// MARK: - Pieces

/// A service's logo on a white tile, as System Settings shows apps; this Mac's own is Apple 智能's
/// many-coloured mark.
struct ServiceTile: View {
    enum Mark { case provider(CompanionProvider), cloud(CloudVoice.Provider), thisMac, laptop, phone, add }
    let mark: Mark
    var size: CGFloat = 30

    var body: some View {
        let r = size * 0.25
        Group {
            switch mark {
            case .add:
                Image(systemName: "plus").font(.yy(size: PanelScale.k * size * 0.4, weight: .medium)).foregroundStyle(YYColor.t2)
                    .frame(width: size, height: size)
                    .overlay(RoundedRectangle(cornerRadius: r).strokeBorder(YYColor.tint, style: .init(lineWidth: 1)))
            case .thisMac:
                Image(systemName: !ServiceTile.hasSymbol("apple.intelligence") ? "sparkles" : "apple.intelligence")
                    .font(.system(size: size * 0.52, weight: .medium))
                    .foregroundStyle(LinearGradient(colors: [Color(red: 1, green: 0.62, blue: 0.2), Color(red: 0.98, green: 0.3, blue: 0.5),
                                                             Color(red: 0.62, green: 0.36, blue: 0.98), Color(red: 0.2, green: 0.6, blue: 1)],
                                                    startPoint: .topLeading, endPoint: .bottomTrailing))
                    .frame(width: size, height: size)
                    .background(tile(r))
            case .phone:
                Image(systemName: "iphone").font(.system(size: size * 0.5, weight: .regular))
                    .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    .frame(width: size, height: size)
                    .background(tile(r))
            case .laptop:
                let glyph: CGFloat = size <= 20 ? 12 : size * 19 / 32
                ZStack {
                    RoundedRectangle(cornerRadius: 1.2 * glyph / 16).stroke(lineWidth: 1.3 * glyph / 16)
                        .frame(width: 11 * glyph / 16, height: 7.5 * glyph / 16)
                        .offset(y: -1.25 * glyph / 16)
                    Rectangle().frame(width: 14 * glyph / 16, height: 1.3 * glyph / 16)
                        .offset(y: 4.8 * glyph / 16)
                }
                .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                .frame(width: size, height: size)
                .background(tile(r))
            case .cloud(let p):
                Group {
                    if ServiceTile.hasImage("brand-\(p.id)") {
                        Image("brand-\(p.id)").resizable().scaledToFit()
                    } else if ServiceTile.hasImage("provider-\(p.id)") {
                        Image("provider-\(p.id)").resizable().scaledToFit().foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else if p == .xiaomi {
                        Text("mi")
                            .font(.system(size: PanelScale.k * size * 0.36, weight: .bold, design: .rounded))
                            .foregroundStyle(.white)
                            .frame(width: size * 0.64, height: size * 0.64)
                            .background(Color(red: 1.0, green: 0.41, blue: 0.0), in: .rect(cornerRadius: size * 0.16))
                    } else if p == .voicestudio {
                        Image(systemName: "waveform")
                            .font(.system(size: size * 0.44, weight: .semibold))
                            .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else if p == .voicebox {
                        Image(systemName: "speaker.wave.2.fill")
                            .font(.system(size: size * 0.4, weight: .medium))
                            .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else if p == .mlxaudio {
                        Image(systemName: "cpu")
                            .font(.system(size: size * 0.42, weight: .medium))
                            .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else if p == .speaches {
                        Image(systemName: "server.rack")
                            .font(.system(size: size * 0.38, weight: .medium))
                            .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else if p == .local {
                        Image(systemName: "dot.radiowaves.left.and.right")
                            .font(.system(size: size * 0.42, weight: .medium))
                            .foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else {
                        Text(String(p.title.prefix(1)).uppercased()).font(.yy(size: PanelScale.k * size * 0.42, weight: .semibold)).foregroundStyle(Color(yy: YYRGBA(110, 107, 104, 1)))
                    }
                }
                .frame(width: size * 0.64, height: size * 0.64)
                .frame(width: size, height: size)
                .background(tile(r))
            case .provider(let p):
                Group {
                    if let logo = p.logo {
                        Image(logo).resizable().scaledToFit().foregroundStyle(Color(yy: YYRGBA(42, 43, 46, 1)))
                    } else {
                        Text(p.mark).font(.yy(size: PanelScale.k * size * 0.42, weight: .semibold)).foregroundStyle(Color(yy: YYRGBA(110, 107, 104, 1)))
                    }
                }
                .frame(width: size * 0.6, height: size * 0.6)
                .frame(width: size, height: size)
                .background(tile(r))
            }
        }
        .frame(width: size, height: size)
    }

    static func hasImage(_ name: String) -> Bool {
        #if os(macOS)
        NSImage(named: name) != nil
        #else
        UIImage(named: name) != nil
        #endif
    }

    static func hasSymbol(_ name: String) -> Bool {
        #if os(macOS)
        NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil
        #else
        UIImage(systemName: name) != nil
        #endif
    }

    private func tile(_ r: CGFloat) -> some View {
        RoundedRectangle(cornerRadius: r).fill(.white)
            .overlay(RoundedRectangle(cornerRadius: r).strokeBorder(.black.opacity(0.1), lineWidth: 0.5))
            .shadow(color: .black.opacity(size > 20 ? 0.05 : 0), radius: 1, y: 1)
    }
}

/// A row of 书伴's list: tile, name, a line; the whole row is the button. The chosen one is tinted.
struct ServiceRow<Tile: View, Title: View, Detail: View, Trailing: View>: View {
    var selected: Bool
    var dim = false
    @ViewBuilder var tile: Tile
    @ViewBuilder var title: Title
    @ViewBuilder var detail: Detail
    @ViewBuilder var trailing: Trailing
    var action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 12) {
                tile.opacity(dim ? 0.5 : 1)
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 7) { title }
                        .font(.yy(size: PanelScale.k * 13, weight: .medium)).foregroundStyle(dim ? YYColor.t3 : YYColor.t1)
                    detail.font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: 8)
                trailing
                Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 10, weight: .semibold)).foregroundStyle(YYColor.t3)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .frame(minHeight: 52 * PanelScale.k)
            .background(selected ? YYColor.t1.opacity(0.06) : hovering ? YYColor.t1.opacity(0.04) : .clear,
                        in: .rect(cornerRadius: 9))
            .padding(4)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }
}

/// "书伴在用", green.
private struct InUsePill: View {
    var body: some View { SettingsPill(String(localized: "当前使用"), tone: .ok) }
}

/// A small status capsule: says a state, isn't pressed.
struct SettingsPill: View {
    enum Tone { case ok, warn, quiet }
    let text: String
    let tone: Tone
    init(_ text: String, tone: Tone) { self.text = text; self.tone = tone }

    var body: some View {
        let color: Color = switch tone {
        case .ok: YYColor.ok
        case .warn: YYColor.warn
        case .quiet: YYColor.t3
        }
        Text(text)
            .font(.yy(size: PanelScale.k * 10.5, weight: .medium))
            .foregroundStyle(color)
            .padding(.horizontal, 7)
            .frame(height: 18)
            .background(tone == .quiet ? YYColor.inset : color.opacity(0.13), in: .capsule)
            .fixedSize()
    }
}

/// The right column's header: a tile, a name, a line.
struct PanelHeader<Mark: View>: View {
    let title: String
    var detail: String?
    @ViewBuilder var mark: Mark

    var body: some View {
        HStack(spacing: 10) {
            mark
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.yy(size: PanelScale.k * 14, weight: .semibold)).foregroundStyle(YYColor.t1)
                if let detail { Text(detail).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3) }
            }
            Spacer(minLength: 0)
        }
        .padding(.bottom, 2)
    }
}

/// The right column's small white card of rows.
struct PanelCard<Content: View>: View {
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Group(subviews: content) { rows in
                ForEach(Array(rows.enumerated()), id: \.offset) { i, row in
                    if i > 0 { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 10) }
                    row
                }
            }
        }
        .padding(3)
        .background(YYColor.card, in: .rect(cornerRadius: 11))
        .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(scheme == .dark ? Color(yy: YYRGBA(255, 255, 255, 0.06)) : YYColor.line, lineWidth: 0.5))
    }
}

/// A row of a `PanelCard` that says something.
struct PanelRow<Label: View, Trailing: View>: View {
    @ViewBuilder var label: Label
    @ViewBuilder var trailing: Trailing

    var body: some View {
        HStack(spacing: 10) {
            label.font(.yy(size: PanelScale.k * 12.5)).foregroundStyle(YYColor.t1)
            Spacer(minLength: 8)
            trailing
        }
        .padding(.horizontal, 10)
        .frame(minHeight: 38 * PanelScale.k)
    }
}

/// A row of a `PanelCard` that is pressed: the whole row, lit on hover.
struct PanelButton<Label: View>: View {
    var systemImage: String?
    var tint: Color = YYColor.t1
    var destructive = false
    var selected = false
    /// The one return would take.
    var outlined = false
    @ViewBuilder var label: Label
    var action: () -> Void
    @State private var hovering = false

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                if let systemImage {
                    Image(systemName: systemImage).font(.yy(size: PanelScale.k * 12.5, weight: .medium)).foregroundStyle(tint).frame(width: 16)
                }
                label
            }
            .font(.yy(size: PanelScale.k * 12.5))
            .foregroundStyle(tint)
            .padding(.horizontal, 10)
            .padding(.vertical, 6)
            .frame(maxWidth: .infinity, minHeight: 38 * PanelScale.k, alignment: .leading)
            .background(background, in: .rect(cornerRadius: 8))
            .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(YYColor.t1, lineWidth: outlined ? 1.5 : 0))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
    }

    private var background: Color {
        if selected { return YYColor.t1.opacity(0.06) }
        if hovering { return destructive ? YYColor.danger.opacity(0.08) : YYColor.t1.opacity(0.045) }
        return .clear
    }
}

/// 移除, 断开: on the Mac a red row in its own card; on iPhone, as its Settings do, the red words
/// centred at the foot of the page, a little apart from the rest.
struct PanelDestructive: View {
    let title: String
    let action: () -> Void
    init(_ title: String, action: @escaping () -> Void) { self.title = title; self.action = action }

    var body: some View {
        #if os(macOS)
        PanelCard {
            PanelButton(tint: YYColor.danger, destructive: true, label: { Text(title) }, action: action)
        }
        #else
        PanelCard {
            Button(role: .destructive, action: action) {
                Text(title).font(.yy(size: PanelScale.k * 12.5)).foregroundStyle(YYColor.danger)
                    .frame(maxWidth: .infinity, minHeight: 38 * PanelScale.k)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .padding(.top, 10)
        #endif
    }
}

/// The small grey label over a `PanelCard`.
struct PanelLabel: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(.yy(size: PanelScale.k * 11, weight: .semibold)).foregroundStyle(YYColor.t3)
            .padding(.leading, 12).padding(.top, 2).padding(.bottom, -4)
    }
}

/// A line of help under a `PanelCard`.
struct PanelHint: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(.yy(size: PanelScale.k * 11)).lineSpacing(2).foregroundStyle(YYColor.t3)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, 12).padding(.top, -4)
    }
}

/// A round button that only shows itself on hover: ‹ in the right column's header.
struct HoverCircleStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        HoverCircle(configuration: configuration)
    }

    private struct HoverCircle: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .background(Circle().fill(YYColor.t1.opacity(configuration.isPressed ? 0.1 : hovering ? 0.06 : 0)))
                .onHover { hovering = $0 }
        }
    }
}

/// Diagonal 135° hatch fill for cached input tokens in the 30-day usage chart & legend swatch.
private struct CachedHatchFill: View {
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        let bg = scheme == .dark ? Color(yy: YYRGBA(180, 134, 90, 0.22)) : Color(yy: YYRGBA(245, 230, 211))
        let stripe = scheme == .dark ? Color(yy: YYRGBA(212, 155, 99)) : Color(yy: YYRGBA(181, 119, 59))
        Canvas { ctx, size in
            ctx.fill(Path(CGRect(origin: .zero, size: size)), with: .color(bg))
            let step: CGFloat = 5
            var x: CGFloat = -size.height
            while x < size.width + size.height {
                var p = Path()
                p.move(to: CGPoint(x: x, y: size.height))
                p.addLine(to: CGPoint(x: x + size.height, y: 0))
                ctx.stroke(p, with: .color(stripe), lineWidth: 1)
                x += step
            }
        }
    }
}

/// 30-day stacked bar chart + legend + typographic source breakdown (Mac & iOS).
struct CompanionTokenUsageView: View {
    let snapshot: CompanionUsage.Snapshot30Days
    @State private var inspectedIndex: Int?
    @Environment(\.colorScheme) private var scheme

    private var missColor: Color {
        scheme == .dark ? Color(yy: YYRGBA(107, 103, 98)) : Color(yy: YYRGBA(200, 195, 188))
    }
    private var outputColor: Color {
        scheme == .dark ? Color(yy: YYRGBA(221, 218, 213)) : Color(yy: YYRGBA(59, 57, 54))
    }
    private var gridColor: Color {
        scheme == .dark ? Color.white.opacity(0.08) : Color(yy: YYRGBA(42, 43, 46, 0.08))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                HStack(alignment: .firstTextBaseline, spacing: 7) {
                    Text("Token 用量")
                        .font(.yy(size: 13, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                    Text("近 30 天")
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t3)
                }
                HStack(alignment: .firstTextBaseline, spacing: 10) {
                    Text(snapshot.totalTokens > 0 ? CompanionUsage.formatCount(snapshot.totalTokens) : "0")
                        .font(.yy(size: 26, weight: .semibold))
                        .monospacedDigit()
                        .foregroundStyle(YYColor.t1)
                    if let rate = snapshot.cacheHitRate, snapshot.cachedInputTokens > 0 {
                        Text("缓存命中 \(Int((rate * 100).rounded()))%")
                            .font(.yy(size: 11.5))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.ok)
                    }
                }
            }

            if snapshot.totalTokens == 0 {
                Text("近 30 天暂无调用记录")
                    .font(.yy(size: 12))
                    .foregroundStyle(YYColor.t3)
                    .padding(.bottom, 2)
            } else {
                VStack(alignment: .leading, spacing: 10) {
                    chartBlock
                    legendRow
                }
                if !snapshot.sources.isEmpty {
                    sourceBreakdown
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var chartBlock: some View {
        let peak = max(1, snapshot.peakTokens)
        let meanFrac = min(1.0, max(0.0, 1.0 - snapshot.averageTokens / Double(peak)))
        let showMean = meanFrac >= 0.16 && snapshot.averageTokens >= 1
        let peakCenter = (Double(snapshot.peakIndex) + 0.5) / Double(max(1, snapshot.days.count))

        return VStack(spacing: 6) {
            HStack(alignment: .top, spacing: 6) {
                // Y-axis labels (Peak & Daily Avg)
                GeometryReader { geo in
                    ZStack(alignment: .topTrailing) {
                        Text(CompanionUsage.formatCount(snapshot.peakTokens))
                            .font(.yy(size: 9.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                            .offset(y: -5)
                        if showMean {
                            Text(CompanionUsage.formatCount(Int(snapshot.averageTokens.rounded())))
                                .font(.yy(size: 9.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                                .offset(y: geo.size.height * meanFrac - 5)
                        }
                    }
                    .frame(maxWidth: .infinity, alignment: .trailing)
                }
                .frame(width: 38, height: 104)

                // Plot area: 3 reference hairlines + 30 stacked columns
                GeometryReader { geo in
                    ZStack(alignment: .bottomLeading) {
                        // Peak, Avg, Baseline hairlines
                        Path { p in
                            p.move(to: CGPoint(x: 0, y: 0.5))
                            p.addLine(to: CGPoint(x: geo.size.width, y: 0.5))
                            if showMean {
                                let y = (geo.size.height * meanFrac).rounded() + 0.5
                                p.move(to: CGPoint(x: 0, y: y))
                                p.addLine(to: CGPoint(x: geo.size.width, y: y))
                            }
                            p.move(to: CGPoint(x: 0, y: geo.size.height - 0.5))
                            p.addLine(to: CGPoint(x: geo.size.width, y: geo.size.height - 0.5))
                        }
                        .stroke(gridColor, lineWidth: 0.5)

                        HStack(alignment: .bottom, spacing: 2.5) {
                            ForEach(Array(snapshot.days.enumerated()), id: \.element.id) { idx, day in
                                barColumn(day: day, index: idx, peak: peak, height: geo.size.height)
                            }
                        }
                        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottom)
                    }
                }
                .frame(height: 104)
            }

            // X-axis dates: start, peak (accented), end
            HStack(spacing: 6) {
                Spacer().frame(width: 38)
                GeometryReader { geo in
                    let w = geo.size.width
                    let peakX = min(max(w * peakCenter, 18), max(18, w - 18))
                    ZStack(alignment: .topLeading) {
                        if peakCenter > 0.22, let first = snapshot.days.first {
                            Text(first.shortLabel)
                                .font(.yy(size: 9.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        }
                        if peakCenter < 0.78, let last = snapshot.days.last {
                            Text(last.shortLabel)
                                .font(.yy(size: 9.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                                .frame(maxWidth: .infinity, alignment: .trailing)
                        }
                        if snapshot.days.indices.contains(snapshot.peakIndex) {
                            Text(snapshot.days[snapshot.peakIndex].shortLabel)
                                .font(.yy(size: 9.5, weight: .medium)).monospacedDigit()
                                .foregroundStyle(YYTint.standard.accent)
                                .position(x: peakX, y: 6)
                        }
                    }
                }
                .frame(height: 14)
            }

            // Selected / hovered day detail bar
            if let idx = inspectedIndex, snapshot.days.indices.contains(idx), snapshot.days[idx].totalTokens > 0 {
                dayDetailStrip(snapshot.days[idx])
                    .transition(.opacity)
            }
        }
    }

    private func barColumn(day: CompanionUsage.DayRecord, index: Int, peak: Int, height: CGFloat) -> some View {
        let total = day.totalTokens
        let h = total > 0 ? max(3, height * CGFloat(total) / CGFloat(peak)) : 0
        let outH = total > 0 ? h * CGFloat(day.output) / CGFloat(total) : 0
        let missH = total > 0 ? h * CGFloat(day.missInput) / CGFloat(total) : 0
        let cacheH = max(0, h - outH - missH)
        let active = inspectedIndex == index

        return VStack(spacing: 0) {
            Spacer(minLength: 0)
            if total > 0 {
                VStack(spacing: 0) {
                    if outH > 0 { outputColor.frame(height: outH) }
                    if missH > 0 { missColor.frame(height: missH) }
                    if cacheH > 0 { CachedHatchFill().frame(height: cacheH) }
                }
                .clipShape(.rect(topLeadingRadius: 2, bottomLeadingRadius: 0, bottomTrailingRadius: 0, topTrailingRadius: 2))
                .opacity(inspectedIndex == nil || active ? 1 : 0.45)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .contentShape(.rect)
        #if os(macOS)
        .onHover { inside in
            if inside && total > 0 {
                inspectedIndex = index
            } else if !inside && inspectedIndex == index {
                inspectedIndex = nil
            }
        }
        #else
        .onTapGesture {
            guard total > 0 else { return }
            inspectedIndex = (inspectedIndex == index) ? nil : index
        }
        #endif
    }

    private func dayDetailStrip(_ day: CompanionUsage.DayRecord) -> some View {
        HStack(spacing: 10) {
            Text(day.date)
                .font(.yy(size: 11, weight: .medium)).monospacedDigit().foregroundStyle(YYColor.t1)
            Text("总 \(day.totalTokens.formatted())")
                .font(.yy(size: 11)).monospacedDigit().foregroundStyle(YYColor.t2)
            if day.cachedInput > 0 {
                Text("缓存 \(day.cachedInput.formatted())")
                    .font(.yy(size: 11)).monospacedDigit().foregroundStyle(YYColor.ok)
            }
            Text("输出 \(day.output.formatted())")
                .font(.yy(size: 11)).monospacedDigit().foregroundStyle(YYColor.t3)
            Spacer(minLength: 0)
            Text("\(day.requests) 次")
                .font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 6)
        .background(YYColor.inset, in: .rect(cornerRadius: 8))
    }

    private var legendRow: some View {
        HStack(spacing: 14) {
            if snapshot.cachedInputTokens > 0 {
                HStack(spacing: 5) {
                    CachedHatchFill().frame(width: 9, height: 9).clipShape(.rect(cornerRadius: 2))
                    Text("缓存命中输入")
                }
            }
            if snapshot.missInputTokens > 0 {
                HStack(spacing: 5) {
                    missColor.frame(width: 9, height: 9).clipShape(.rect(cornerRadius: 2))
                    Text("常规输入")
                }
            }
            if snapshot.outputTokens > 0 {
                HStack(spacing: 5) {
                    outputColor.frame(width: 9, height: 9).clipShape(.rect(cornerRadius: 2))
                    Text("输出")
                }
            }
        }
        .font(.yy(size: 10.5))
        .foregroundStyle(YYColor.t3)
    }

    private var sourceBreakdown: some View {
        HStack(spacing: 18) {
            ForEach(snapshot.sources.prefix(4), id: \.source) { item in
                HStack(alignment: .firstTextBaseline, spacing: 5) {
                    Text(item.source.title)
                        .font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
                    let pct = max(1, Int((item.share * 100).rounded()))
                    Text("\(pct)%")
                        .font(.yy(size: 11.5, weight: .medium)).monospacedDigit().foregroundStyle(YYColor.t1)
                }
            }
            Spacer(minLength: 0)
        }
    }
}

/// Pill segmented control for daily token budget cap (matches Yuyue warm-paper aesthetic).
private struct DailyCapPills: View {
    @Binding var limit: Int
    @Environment(\.colorScheme) private var scheme

    private static let presets: [(String, Int)] = [
        (String(localized: "不限"), 0),
        (String(localized: "20 万"), 200_000),
        (String(localized: "50 万"), 500_000),
        (String(localized: "100 万"), 1_000_000),
    ]

    var body: some View {
        HStack(spacing: 2) {
            ForEach(Self.presets, id: \.1) { title, value in
                let active = limit == value
                Button {
                    withAnimation(YYMotion.quick) { limit = value }
                } label: {
                    Text(title)
                        .font(.yy(size: 11, weight: active ? .medium : .regular))
                        .foregroundStyle(active ? YYColor.t1 : YYColor.t2)
                        .padding(.horizontal, 9)
                        .padding(.vertical, 4)
                        .background {
                            if active {
                                Capsule()
                                    .fill(YYColor.card)
                                    .shadow(color: scheme == .dark ? .clear : Color.black.opacity(0.06), radius: 1.5, y: 0.5)
                            }
                        }
                        .contentShape(.capsule)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(2)
        .background(YYColor.inset, in: .capsule)
    }
}

/// 用量与省费: clean 30-day Token 用量 card + 2-row 省费 card on macOS and iOS.
struct CompanionUsageAndCostSection: View {
    @State private var snapshot = CompanionUsage.snapshot30Days()
    @State private var tierRouting = CompanionUsage.tierRoutingEnabled
    @State private var dailyTokenLimit = CompanionUsage.dailyTokenLimit

    var body: some View {
        let current = CompanionConfig.current
        let showCloudTts = snapshot.ttsChars > 0 || snapshot.ttsSavedChars > 0 || !CloudVoice.all().isEmpty

        #if os(macOS)
        SettingsHeading(String(localized: "用量"))
        SettingsCard {
            CompanionTokenUsageView(snapshot: snapshot)
                .padding(18)
            if showCloudTts {
                HStack(alignment: .firstTextBaseline) {
                    Text("云端朗读 · 近 30 天")
                        .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    Spacer(minLength: 12)
                    HStack(spacing: 4) {
                        Text("\(CompanionUsage.formatCount(snapshot.ttsChars)) 字")
                            .font(.yy(size: 12, weight: .medium)).monospacedDigit().foregroundStyle(YYColor.t1)
                        if snapshot.ttsSavedChars > 0 {
                            Text("· 本地缓存省 \(CompanionUsage.formatCount(snapshot.ttsSavedChars)) 字")
                                .font(.yy(size: 11.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        }
                    }
                }
                .padding(.horizontal, 18)
                .padding(.vertical, 12)
            }
        }

        SettingsHeading(String(localized: "省费"))
        SettingsCard {
            SettingsRow(String(localized: "批量任务自动用轻量模型"), detail: tierHint(for: current)) {
                SettingsSwitch(isOn: $tierRouting)
            }
            SettingsRow(String(localized: "每日用量上限"), detail: String(localized: "达到后自动暂停后台批量任务")) {
                DailyCapPills(limit: $dailyTokenLimit)
            }
        }
        SettingsNote(String(localized: "统计为本机估算值，以服务商实际账单为准。书伴问答与前情提要的纯文本缓存会随同步在你的设备间复用。"))
            .onChange(of: tierRouting) { CompanionUsage.tierRoutingEnabled = tierRouting }
            .onChange(of: dailyTokenLimit) { CompanionUsage.dailyTokenLimit = dailyTokenLimit }
            .onReceive(NotificationCenter.default.publisher(for: CompanionUsage.changed)) { _ in
                snapshot = CompanionUsage.snapshot30Days()
            }
            .onAppear { snapshot = CompanionUsage.snapshot30Days() }
        #else
        PhoneSettingsHeading(String(localized: "用量"))
        YYGroup {
            CompanionTokenUsageView(snapshot: snapshot)
                .padding(.vertical, 12)
            if showCloudTts {
                HStack(alignment: .firstTextBaseline) {
                    Text("云端朗读 · 近 30 天")
                        .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
                    Spacer(minLength: 8)
                    Text(snapshot.ttsSavedChars > 0
                         ? String(localized: "\(CompanionUsage.formatCount(snapshot.ttsChars)) 字 · 省 \(CompanionUsage.formatCount(snapshot.ttsSavedChars))")
                         : String(localized: "\(CompanionUsage.formatCount(snapshot.ttsChars)) 字"))
                        .font(.yy(size: 12)).monospacedDigit().foregroundStyle(YYColor.t1)
                }
                .padding(.vertical, 10)
            }
        }

        PhoneSettingsHeading(String(localized: "省费"))
        YYGroup {
            HStack(spacing: 12) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("批量任务自动用轻量模型").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                    Text(tierHint(for: current)).font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                }
                Spacer(minLength: 8)
                Toggle("批量任务自动用轻量模型", isOn: $tierRouting)
                    .labelsHidden().tint(YYTint.standard.fill)
            }
            .padding(.vertical, 10)
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    VStack(alignment: .leading, spacing: 2) {
                        Text("每日用量上限").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("达到后自动暂停后台批量任务").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer()
                }
                DailyCapPills(limit: $dailyTokenLimit)
            }
            .padding(.vertical, 10)
        }
        YYNote(String(localized: "统计为本机估算值，以服务商实际账单为准。书伴问答与前情提要的纯文本缓存会随同步在你的设备间复用。"))
            .onChange(of: tierRouting) { CompanionUsage.tierRoutingEnabled = tierRouting }
            .onChange(of: dailyTokenLimit) { CompanionUsage.dailyTokenLimit = dailyTokenLimit }
            .onReceive(NotificationCenter.default.publisher(for: CompanionUsage.changed)) { _ in
                snapshot = CompanionUsage.snapshot30Days()
            }
            .onAppear { snapshot = CompanionUsage.snapshot30Days() }
        #endif
    }

    private func tierHint(for config: CompanionConfig) -> String {
        if let fast = config.resolvedFastModel {
            return String(localized: "认人、错字整理、提要优先用 \(ModelNames.name(fast))，并自动启用提示词缓存")
        }
        return String(localized: "认人、错字整理、提要优先用同厂轻量模型，并自动启用提示词缓存")
    }
}
