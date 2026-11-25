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
                    SettingsPill(String(localized: "不推荐"), tone: .warn)
                    if apple && choice.current.provider.kind == .apple { InUsePill() }
                } detail: {
                    Text("Apple 智能 · 上下文短、中文文学理解弱，仅作离线简单兜底")
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
                            Text("模型").fixedSize()
                            Spacer(minLength: 8)
                            if let badge = ModelNames.routeBadge(config.model) {
                                Text(badge)
                                    .font(.yy(size: PanelScale.k * 10, weight: .medium, design: .monospaced))
                                    .foregroundStyle(YYColor.t3)
                                    .padding(.horizontal, 5)
                                    .padding(.vertical, 1.5)
                                    .background(YYColor.inset, in: .rect(cornerRadius: 4))
                                    .fixedSize()
                            }
                            Text(config.model.isEmpty ? String(localized: "选择…") : ModelNames.name(config.model))
                                .foregroundStyle(config.model.isEmpty ? YYColor.t3 : YYColor.t1)
                                .lineLimit(1)
                                .truncationMode(.middle)
                            Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: { if let openModels { openModels() } else { focus.page = .models(provider.id) } }
                        .help(config.model.isEmpty ? String(localized: "选择或输入模型") : config.model)
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
                if apple {
                    PanelHint(String(localized: "不推荐作为主力模型：Apple 智能上下文窗口短（约 4K），中文长章节问答与播客撰稿质量有限。建议添加 DeepSeek、通义千问、OpenRouter 或连接本机 Ollama。"))
                }
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

/// A service's models: pinned search & maker filter chips on top, then a smooth lazy list of
/// 在用, 最近用过, 推荐, and all fetched models underneath. Typing narrows the list or lets the
/// user submit any custom model ID directly.
struct ModelList: View {
    let provider: CompanionProvider
    /// Back to the service; nil: the right column's own page.
    var back: (() -> Void)?
    /// Adding it: what's typed so far, to list the models with; nothing saved yet.
    var draft: CompanionConfig?
    /// Already fetched models from the form, so opening the list shows them immediately.
    var initialModels: [String] = []
    /// False when embedded inside a sheet or iOS NavigationStack that already renders a title bar.
    var showsHeader = true
    /// Called whenever models are successfully fetched from the server.
    var fetched: (([String]) -> Void)?
    /// Adding it: the model chosen goes back to the form instead of being saved.
    var picked: ((String) -> Void)?

    @State private var query = ""
    @State private var selectedMaker: String?
    @State private var models: [String] = []
    @State private var loading = false
    @State private var loaded = false
    @State private var errorMessage: String?
    @FocusState private var searching: Bool
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let current = draft?.model ?? CompanionConfig.saved(provider).model
        let groups = ModelNames.groups(models)
        VStack(alignment: .leading, spacing: 8) {
            VStack(alignment: .leading, spacing: 8) {
                #if os(macOS)
                if showsHeader {
                    HStack(spacing: 8) {
                        Button { goBack() } label: {
                            Image(systemName: "chevron.left").font(.yy(size: PanelScale.k * 11, weight: .semibold)).foregroundStyle(YYColor.t2)
                                .frame(width: 24, height: 24).contentShape(.circle)
                        }
                        .buttonStyle(HoverCircleStyle())
                        .help(Text("回到 \(provider.title)"))
                        ServiceTile(mark: .provider(provider), size: 24)
                        Text("选择模型").font(.yy(size: PanelScale.k * 13.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                        Spacer(minLength: 0)
                        refreshButton
                    }
                    .padding(.leading, -4)
                }
                #endif

                HStack(spacing: 7) {
                    Image(systemName: "magnifyingglass").font(.yy(size: PanelScale.k * 11.5)).foregroundStyle(YYColor.t3)
                    TextField("", text: $query, prompt: Text(models.isEmpty ? String(localized: "搜模型名或直接输入模型 ID") : String(localized: "搜 \(models.count) 个模型，或输入新 ID")))
                        .textFieldStyle(.plain)
                        .font(.yy(size: PanelScale.k * 12.5))
                        .autocorrectionDisabled()
                        #if os(iOS)
                        .textInputAutocapitalization(.never)
                        #endif
                        .focused($searching)
                        .onSubmit {
                            if let first = hits.first { choose(first) }
                            else if !trimmed.isEmpty { choose(trimmed) }
                        }
                        .onKeyPress(.escape) {
                            if query.isEmpty { goBack() } else { query = "" }
                            return .handled
                        }
                    if !query.isEmpty {
                        Button { query = "" } label: {
                            Image(systemName: "xmark.circle.fill")
                                .font(.yy(size: PanelScale.k * 11.5))
                                .foregroundStyle(YYColor.t3)
                        }
                        .buttonStyle(.plain)
                    }
                    if !showsHeader {
                        refreshButton
                    }
                }
                .padding(.horizontal, 10)
                .frame(height: 32)
                .background(YYColor.inset, in: .rect(cornerRadius: 8))

                if trimmed.isEmpty, groups.count > 1 {
                    makerFilterBar(groups: groups, total: models.count)
                }
            }
            .padding(.horizontal, 16)

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 10) {
                    if trimmed.isEmpty {
                        sections(current: current, groups: groups)
                    } else {
                        results(current: current)
                    }
                }
                .padding(.horizontal, 16)
                .padding(.top, 2)
                .padding(.bottom, 16)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .frame(maxHeight: .infinity, alignment: .top)
        .task {
            #if os(macOS)
            searching = true
            #endif
            if models.isEmpty {
                let seed = !initialModels.isEmpty ? initialModels : ModelNames.cached(provider.id)
                if !seed.isEmpty {
                    models = seed
                    loaded = true
                }
            }
            await refresh()
        }
    }

    private func makerFilterBar(groups: [(maker: String, models: [String])], total: Int) -> some View {
        ScrollView(.horizontal) {
            HStack(spacing: 5) {
                makerChip(title: String(localized: "全部"), count: total, active: selectedMaker == nil) {
                    selectedMaker = nil
                }
                ForEach(groups, id: \.maker) { g in
                    makerChip(title: ModelNames.makerTitle(g.maker), count: g.models.count, active: selectedMaker == g.maker) {
                        selectedMaker = selectedMaker == g.maker ? nil : g.maker
                    }
                }
            }
            .padding(.vertical, 1)
        }
        .scrollIndicators(.never)
    }

    private func makerChip(title: String, count: Int, active: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 4) {
                Text(title)
                    .font(.yy(size: PanelScale.k * 11, weight: active ? .semibold : .regular))
                Text("\(count)")
                    .font(.yy(size: PanelScale.k * 10).monospacedDigit())
                    .opacity(0.7)
            }
            .foregroundStyle(active ? YYColor.card : YYColor.t2)
            .padding(.horizontal, 8)
            .frame(height: 23)
            .background(active ? YYColor.t1 : YYColor.inset, in: .capsule)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
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
        .help(Text("刷新模型列表"))
        .accessibilityLabel(Text("刷新模型列表"))
    }

    private func refresh() async {
        guard !loading else { return }
        let config = draft ?? CompanionConfig.saved(provider)
        guard config.isConnected else {
            withAnimation(YYMotion.standard) {
                loading = false
                loaded = true
                errorMessage = provider.needsKey && config.key.isEmpty
                    ? String(localized: "先填好 API Key 可获取完整模型列表，也可直接点选下方常用模型")
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
                if !list.isEmpty { models = list }
                loaded = true
                loading = false
                errorMessage = nil
                if let selectedMaker, !ModelNames.groups(models).contains(where: { $0.maker == selectedMaker }) {
                    self.selectedMaker = nil
                }
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

    private var searchablePool: [String] {
        var seen = Set<String>()
        let presets = ModelNames.resolvedRecommendations(for: provider.id, available: models).map(\.id)
        return (models + presets).filter { !$0.isEmpty && seen.insert($0).inserted }
    }

    private var hits: [String] {
        let tokens = trimmed.lowercased().split(separator: " ").map(String.init)
        guard !tokens.isEmpty else { return [] }
        return searchablePool.filter { id in
            let hay = (id + " " + ModelNames.name(id)).lowercased()
            return tokens.allSatisfy { hay.contains($0) }
        }
    }

    @ViewBuilder private func sections(current: String, groups: [(maker: String, models: [String])]) -> some View {
        if let selectedMaker, let group = groups.first(where: { $0.maker == selectedMaker }) {
            PanelLabel(String(localized: "\(ModelNames.makerTitle(group.maker)) · \(group.models.count) 个"))
            PanelCard { ForEach(group.models, id: \.self) { item($0, current: current) } }
        } else {
            let recs = ModelNames.resolvedRecommendations(for: provider.id, available: models)
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
                .padding(.vertical, 4)
            } else if let errorMessage, models.isEmpty {
                PanelCard {
                    PanelButton(systemImage: "arrow.clockwise", tint: YYColor.t1) {
                        Text("重新获取模型列表")
                        Spacer(minLength: 6)
                    } action: { Task { await refresh() } }
                }
                PanelHint(String(localized: "\(errorMessage)。也可直接在上方输入框填入模型 ID 按回车。"))
            } else if models.isEmpty && loaded {
                PanelCard {
                    PanelButton(systemImage: "arrow.clockwise", tint: YYColor.t1) {
                        Text("重新获取模型列表")
                        Spacer(minLength: 6)
                    } action: { Task { await refresh() } }
                }
                PanelHint(String(localized: "未返回在线模型列表；可直接点选推荐模型，或在上方输入任意模型 ID 按回车。"))
            }
            if !models.isEmpty {
                if groups.count > 1 {
                    ForEach(groups, id: \.maker) { g in
                        HStack(spacing: 6) {
                            Text(ModelNames.makerTitle(g.maker)).font(.yy(size: PanelScale.k * 11, weight: .semibold)).foregroundStyle(YYColor.t3)
                            Spacer()
                            Text("\(g.models.count)").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).monospacedDigit()
                        }
                        .padding(.horizontal, 12).padding(.bottom, -4)
                        PanelCard { ForEach(g.models, id: \.self) { item($0, current: current) } }
                    }
                } else {
                    PanelLabel(String(localized: "全部 \(models.count) 个"))
                    PanelCard { ForEach(models, id: \.self) { item($0, current: current) } }
                }
            }
        }
    }

    @ViewBuilder private func results(current: String) -> some View {
        let found = hits
        Text(found.isEmpty ? String(localized: "列表里未找到，按回车直接使用输入的 ID") : String(localized: "找到 \(found.count) 个 · 回车选第一个"))
            .font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).padding(.leading, 12)
        PanelCard {
            ForEach(Array(found.prefix(60).enumerated()), id: \.element) { i, m in
                item(m, current: current, first: i == 0, highlight: trimmed)
            }
            if !searchablePool.contains(trimmed) {
                PanelButton(outlined: found.isEmpty) {
                    VStack(alignment: .leading, spacing: 1.5) {
                        Text("使用「\(trimmed)」").font(.yy(size: PanelScale.k * 12.5, weight: .medium)).foregroundStyle(YYColor.t1)
                        Text("自定义模型 ID · 直接按此名称请求接口").font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                } action: { choose(trimmed) }
            }
        }
    }

    private func item(_ id: String, why: String? = nil, current: String, first: Bool = false, highlight: String = "") -> some View {
        let on = id == current
        let friendly = ModelNames.name(id)
        let badge = ModelNames.routeBadge(id)
        return PanelButton(selected: on, outlined: first) {
            VStack(alignment: .leading, spacing: 1.5) {
                HStack(spacing: 6) {
                    Text(friendly)
                        .font(.yy(size: PanelScale.k * 12.5, weight: on ? .semibold : .regular))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .truncationMode(.middle)
                    if let badge {
                        Text(badge)
                            .font(.yy(size: PanelScale.k * 9.5, weight: .medium, design: .monospaced))
                            .foregroundStyle(YYColor.t3)
                            .padding(.horizontal, 4.5)
                            .padding(.vertical, 1)
                            .background(YYColor.inset, in: .rect(cornerRadius: 4))
                            .fixedSize()
                    }
                }
                if let why {
                    Text(why).font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(YYColor.t2).lineLimit(1)
                } else if friendly != id {
                    Text(id).font(.yy(size: PanelScale.k * 10, design: .monospaced)).foregroundStyle(YYColor.t3).lineLimit(1).truncationMode(.middle)
                }
            }
            Spacer(minLength: 6)
            if on { Image(systemName: "checkmark").font(.yy(size: PanelScale.k * 11, weight: .bold)).foregroundStyle(YYColor.t1) }
        } action: { choose(id) }
        .help(id)
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
        // If the currently selected provider isn't ready (e.g. missing Key) while this one is ready,
        // promote this service automatically so 书伴 works right away.
        if !CompanionConfig.current.isReady && config.isReady {
            CompanionConfig.current = config
        }
        goBack()
        focus.reload()
    }

    private func goBack() {
        if let back { back() } else { focus.page = .service(provider.id) }
    }
}

// MARK: - Model names

/// Model ids as people say them, curated presets per service, cached server lists, and recent picks.
enum ModelNames {
    struct Pick { let id: String; let name: String; let why: String }

    static let recommended: [String: [Pick]] = [
        "claude": [.init(id: "claude-opus-4-6", name: "Claude Opus 4.6", why: String(localized: "最聪明，读懂难书")),
                   .init(id: "claude-sonnet-4-6", name: "Claude Sonnet 4.6", why: String(localized: "聪明又快，平时首选")),
                   .init(id: "claude-sonnet-4-5", name: "Claude Sonnet 4.5", why: String(localized: "均衡稳定，文笔自然")),
                   .init(id: "claude-haiku-4-5", name: "Claude Haiku 4.5", why: String(localized: "最快最省，速查够用"))],
        "openai": [.init(id: "gpt-5", name: "GPT-5", why: String(localized: "旗舰推理，适合深读")),
                   .init(id: "gpt-5-mini", name: "GPT-5 mini", why: String(localized: "快又省，日常问答首选")),
                   .init(id: "gpt-4.1", name: "GPT-4.1", why: String(localized: "长文与指令遵循稳")),
                   .init(id: "o4-mini", name: "o4-mini", why: String(localized: "擅长逻辑分析"))],
        "deepseek": [.init(id: "deepseek-chat", name: "DeepSeek V3", why: String(localized: "中文自然，快又便宜")),
                     .init(id: "deepseek-reasoner", name: "DeepSeek R1", why: String(localized: "深度思考，适合难书拆解"))],
        "gemini": [.init(id: "gemini-2.5-pro", name: "Gemini 2.5 Pro", why: String(localized: "超长上下文，整本书吃透")),
                   .init(id: "gemini-2.5-flash", name: "Gemini 2.5 Flash", why: String(localized: "极速响应，性价比高"))],
        "openrouter": [.init(id: "anthropic/claude-sonnet-4-5", name: "Claude Sonnet 4.5", why: String(localized: "读书最稳")),
                       .init(id: "google/gemini-2.5-pro", name: "Gemini 2.5 Pro", why: String(localized: "超长上下文，问全书稳")),
                       .init(id: "google/gemini-2.5-flash", name: "Gemini 2.5 Flash", why: String(localized: "快又便宜")),
                       .init(id: "deepseek/deepseek-chat", name: "DeepSeek V3", why: String(localized: "中文好，很省"))],
        "moonshot": [.init(id: "kimi-k2-turbo-preview", name: "Kimi K2 Turbo", why: String(localized: "长书一口气读完")),
                     .init(id: "kimi-latest", name: "Kimi Latest", why: String(localized: "自动跟进最新主力模型")),
                     .init(id: "moonshot-v1-128k", name: "Moonshot 128K", why: String(localized: "长章节检索稳定"))],
        "dashscope": [.init(id: "qwen-max", name: String(localized: "通义千问 Max"), why: String(localized: "中文旗舰")),
                      .init(id: "qwen-plus", name: String(localized: "通义千问 Plus"), why: String(localized: "均衡好用，平时首选")),
                      .init(id: "qwq-plus", name: "QwQ Plus", why: String(localized: "深度推理"))],
        "zhipu": [.init(id: "glm-4.6", name: "GLM-4.6", why: String(localized: "中文旗舰，理解细腻")),
                  .init(id: "glm-4-plus", name: "GLM-4 Plus", why: String(localized: "均衡稳定")),
                  .init(id: "glm-4-flash", name: "GLM-4 Flash", why: String(localized: "极速免费层常用"))],
        "volcengine": [.init(id: "doubao-seed-1-6-250615", name: String(localized: "豆包 Seed 1.6"), why: String(localized: "主力综合模型")),
                       .init(id: "doubao-seed-1-6-thinking-250615", name: String(localized: "豆包 Seed 1.6 思考"), why: String(localized: "深度思考")),
                       .init(id: "doubao-seed-1-6-flash-250615", name: String(localized: "豆包 Seed 1.6 Flash"), why: String(localized: "轻量极速"))],
        "siliconflow": [.init(id: "deepseek-ai/DeepSeek-V3", name: "DeepSeek V3", why: String(localized: "日常首选")),
                        .init(id: "deepseek-ai/DeepSeek-R1", name: "DeepSeek R1", why: String(localized: "深度推理")),
                        .init(id: "Qwen/Qwen3-235B-A22B", name: "Qwen3 235B", why: String(localized: "通义开源旗舰"))],
        "minimax": [.init(id: "MiniMax-M2", name: "MiniMax M2", why: String(localized: "长文本与文学语感好")),
                    .init(id: "MiniMax-Text-01", name: "MiniMax Text 01", why: String(localized: "超长上下文"))],
        "xai": [.init(id: "grok-4", name: "Grok 4", why: String(localized: "旗舰推理")),
                .init(id: "grok-3-mini", name: "Grok 3 mini", why: String(localized: "轻量快速"))],
        "mistral": [.init(id: "mistral-large-latest", name: "Mistral Large", why: String(localized: "西文理解细腻")),
                    .init(id: "mistral-small-latest", name: "Mistral Small", why: String(localized: "快速轻量"))],
        "groq": [.init(id: "llama-3.3-70b-versatile", name: "Llama 3.3 70B", why: String(localized: "极速响应")),
                 .init(id: "llama-3.1-8b-instant", name: "Llama 3.1 8B", why: String(localized: "毫秒级返回"))],
    ]

    private static let names: [String: String] = Dictionary(
        recommended.values.flatMap { $0 }.map { ($0.id, $0.name) }, uniquingKeysWith: { a, _ in a })

    /// Resolves recommended models against live fetched `available` IDs when present,
    /// or returns curated presets when the endpoint doesn't expose `GET /models`.
    /// For multi-provider gateways (`magpie`, custom relays), surfaces smart top picks from `available`.
    static func resolvedRecommendations(for providerID: String, available: [String]) -> [Pick] {
        let presets = recommended[providerID, default: []]
        if available.isEmpty {
            return presets
        }
        if !presets.isEmpty {
            var matched: [Pick] = []
            var seen = Set<String>()
            for p in presets {
                if available.contains(p.id), seen.insert(p.id).inserted {
                    matched.append(p)
                } else if let variant = available.first(where: {
                    $0.hasPrefix(p.id + "-") || $0.hasSuffix("/" + p.id) || $0.contains("/" + p.id + "-")
                }), seen.insert(variant).inserted {
                    matched.append(Pick(id: variant, name: name(variant), why: p.why))
                }
            }
            if !matched.isEmpty { return matched }
        }
        // Gateway / local relay heuristics (e.g. magpie, OneAPI, LiteLLM): pick up to 4 standout models.
        let patterns: [(needle: String, why: String)] = [
            ("claude-sonnet-4", String(localized: "读书最稳，兼顾速度与理解")),
            ("claude-opus-4", String(localized: "最聪明，适合啃难书")),
            ("gemini-2.5-pro", String(localized: "超长上下文，适合全书检索")),
            ("gemini-2.5-flash", String(localized: "快又省，日常随手问")),
            ("gpt-5", String(localized: "综合推理强")),
            ("deepseek-r1", String(localized: "深度思考")),
            ("deepseek-chat", String(localized: "中文自然流畅")),
            ("deepseek-v3", String(localized: "中文自然流畅")),
        ]
        var picks: [Pick] = []
        var used = Set<String>()
        for pat in patterns where picks.count < 4 {
            if let hit = available.first(where: { $0.lowercased().contains(pat.needle) && !used.contains($0) }) {
                used.insert(hit)
                picks.append(Pick(id: hit, name: name(hit), why: pat.why))
            }
        }
        return picks
    }

    /// Optional routing prefix before `/` (e.g. `"auto"` in `"auto/claude-opus-4-6"`), shown as a subtle badge.
    static func routeBadge(_ id: String) -> String? {
        guard let slash = id.firstIndex(of: "/") else { return nil }
        let prefix = String(id[..<slash]).trimmingCharacters(in: .whitespaces)
        guard !prefix.isEmpty, prefix.count <= 14 else { return nil }
        return prefix
    }

    /// Friendly maker title for filter chips (`"anthropic"` → `"Anthropic"`, `"auto"` → `"Auto"`).
    static func makerTitle(_ raw: String) -> String {
        switch raw.lowercased() {
        case "auto": "Auto"
        case "anthropic": "Anthropic"
        case "openai": "OpenAI"
        case "google": "Google"
        case "deepseek", "deepseek-ai": "DeepSeek"
        case "qwen": "Qwen"
        case "meta-llama", "meta": "Llama"
        case "x-ai", "xai": "xAI"
        case "mistralai", "mistral": "Mistral"
        case "moonshotai", "moonshot": "Kimi"
        case "z-ai", "zhipu": "GLM"
        default: raw
        }
    }

    /// Human-readable model name (`"auto/claude-opus-4-6-20260205"` → `"Claude Opus 4.6"`),
    /// keeping the distinguishing model family & version front and center instead of truncating after a prefix.
    static func name(_ id: String) -> String {
        if let exact = names[id] { return exact }
        let trimmed = id.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return id }

        // Strip leading maker/route prefix ("auto/", "anthropic/", etc.)
        let coreRaw: String
        if let slash = trimmed.lastIndex(of: "/"), slash < trimmed.index(before: trimmed.endIndex) {
            coreRaw = String(trimmed[trimmed.index(after: slash)...])
        } else {
            coreRaw = trimmed
        }
        if let exactCore = names[coreRaw] { return exactCore }

        // Strip trailing 8-digit release date (-20250929, -20241022) while preserving suffixes like :thinking or :free
        var tagSuffix = ""
        var base = coreRaw
        if let colon = base.firstIndex(of: ":") {
            let tag = String(base[base.index(after: colon)...])
            base = String(base[..<colon])
            if tag.lowercased() == "thinking" { tagSuffix = " Thinking" }
            else if tag.lowercased() == "free" { tagSuffix = " (Free)" }
            else if !tag.isEmpty { tagSuffix = " · \(tag)" }
        }
        if let m = base.wholeMatch(of: /(.+?)-20\d{6}/) {
            base = String(m.1)
        }
        if let exactBase = names[base] { return exactBase + tagSuffix }

        let lower = base.lowercased()
        if lower.hasPrefix("claude-") {
            return formatClaude(String(base.dropFirst(7))) + tagSuffix
        }
        if lower.hasPrefix("gpt-") {
            return "GPT-" + String(base.dropFirst(4)) + tagSuffix
        }
        if lower.hasPrefix("gemini-") {
            return "Gemini " + formatHyphenated(String(base.dropFirst(7))) + tagSuffix
        }
        if lower.hasPrefix("deepseek-") {
            let rest = String(base.dropFirst(9))
            if rest.lowercased() == "chat" { return "DeepSeek V3" + tagSuffix }
            if rest.lowercased() == "reasoner" { return "DeepSeek R1" + tagSuffix }
            return "DeepSeek " + formatHyphenated(rest) + tagSuffix
        }
        if lower.hasPrefix("qwen") {
            return formatHyphenated(base, capitalizeFirst: true) + tagSuffix
        }
        return coreRaw
    }

    private static func formatClaude(_ rest: String) -> String {
        // e.g. "opus-4-6" -> "Claude Opus 4.6", "3-7-sonnet" -> "Claude 3.7 Sonnet", "opus-4-5-thinking" -> "Claude Opus 4.5 Thinking"
        let parts = rest.split(separator: "-").map(String.init)
        var tokens: [String] = []
        var i = 0
        while i < parts.count {
            let p = parts[i]
            if let _ = Int(p), i + 1 < parts.count, let next = Int(parts[i + 1]), next < 20 {
                tokens.append("\(p).\(next)")
                i += 2
            } else {
                tokens.append(p.prefix(1).uppercased() + p.dropFirst())
                i += 1
            }
        }
        return "Claude " + tokens.joined(separator: " ")
    }

    private static func formatHyphenated(_ s: String, capitalizeFirst: Bool = false) -> String {
        let parts = s.split(separator: "-").map(String.init)
        return parts.enumerated().map { idx, p in
            let low = p.lowercased()
            if ["pro", "flash", "lite", "mini", "nano", "max", "plus", "turbo", "preview", "thinking", "v3", "r1"].contains(low) {
                return low.count <= 2 ? low.uppercased() : low.prefix(1).uppercased() + low.dropFirst()
            }
            if idx == 0 && capitalizeFirst {
                return p.prefix(1).uppercased() + p.dropFirst()
            }
            return p
        }.joined(separator: " ")
    }

    // MARK: Cached server models & recents

    static func cached(_ provider: String) -> [String] {
        UserDefaults.standard.stringArray(forKey: "companion.models.\(provider)") ?? []
    }

    static func saveCached(_ list: [String], provider: String) {
        guard !provider.isEmpty, !list.isEmpty else { return }
        UserDefaults.standard.set(Array(list.sorted().prefix(500)), forKey: "companion.models.\(provider)")
    }

    // The last four used on a single provider, newest first.
    static func recent(_ provider: String) -> [String] {
        UserDefaults.standard.stringArray(forKey: "companion.recent.\(provider)") ?? []
    }

    static func used(_ id: String, provider: String) {
        guard !id.isEmpty else { return }
        let list = [id] + recent(provider).filter { $0 != id }
        UserDefaults.standard.set(Array(list.prefix(4)), forKey: "companion.recent.\(provider)")
        recordGlobalRecent(provider: provider, model: id)
    }

    // MARK: Cross-provider favorites, global recents & fuzzy search

    struct Entry: Identifiable, Hashable {
        let provider: CompanionProvider
        let model: String
        var id: String { "\(provider.id)::\(model)" }
    }

    private static let appleModelID = "apple-on-device"

    static func entryKey(provider: String, model: String) -> String {
        "\(provider)::\(model)"
    }

    private static func resolveEntry(_ key: String, validProviders: [String: CompanionProvider]) -> Entry? {
        guard let sep = key.range(of: "::") else { return nil }
        let pid = String(key[..<sep.lowerBound])
        let mid = String(key[sep.upperBound...])
        guard !mid.isEmpty, let p = validProviders[pid] else { return nil }
        return Entry(provider: p, model: mid)
    }

    static func starredEntries(among providers: [CompanionProvider]) -> [Entry] {
        let map = Dictionary(uniqueKeysWithValues: providers.map { ($0.id, $0) })
        let raw = UserDefaults.standard.stringArray(forKey: "companion.starred") ?? []
        return raw.compactMap { resolveEntry($0, validProviders: map) }
    }

    static func isStarred(provider: String, model: String) -> Bool {
        let key = entryKey(provider: provider, model: model)
        return (UserDefaults.standard.stringArray(forKey: "companion.starred") ?? []).contains(key)
    }

    static func toggleStar(provider: String, model: String) {
        let key = entryKey(provider: provider, model: model)
        var list = UserDefaults.standard.stringArray(forKey: "companion.starred") ?? []
        if let idx = list.firstIndex(of: key) {
            list.remove(at: idx)
        } else {
            list.insert(key, at: 0)
        }
        UserDefaults.standard.set(Array(list.prefix(30)), forKey: "companion.starred")
    }

    static func globalRecents(among providers: [CompanionProvider]) -> [Entry] {
        let map = Dictionary(uniqueKeysWithValues: providers.map { ($0.id, $0) })
        let raw = UserDefaults.standard.stringArray(forKey: "companion.recentGlobal") ?? []
        return raw.compactMap { resolveEntry($0, validProviders: map) }
    }

    static func recordGlobalRecent(provider: String, model: String) {
        guard !provider.isEmpty, !model.isEmpty else { return }
        let key = entryKey(provider: provider, model: model)
        let existing = UserDefaults.standard.stringArray(forKey: "companion.recentGlobal") ?? []
        let updated = [key] + existing.filter { $0 != key }
        UserDefaults.standard.set(Array(updated.prefix(8)), forKey: "companion.recentGlobal")
    }

    /// Immediate 0ms model pool for a provider: cached live server list, or curated presets + saved model.
    static func availablePool(for provider: CompanionProvider) -> [String] {
        if provider.kind == .apple { return [appleModelID] }
        let saved = CompanionConfig.saved(provider).model
        var out: [String] = []
        var seen = Set<String>()
        func add(_ m: String) {
            let t = m.trimmingCharacters(in: .whitespacesAndNewlines)
            if !t.isEmpty, seen.insert(t).inserted { out.append(t) }
        }
        let serverList = cached(provider.id)
        if !serverList.isEmpty {
            add(saved)
            for m in recent(provider.id) where serverList.contains(m) { add(m) }
            for r in resolvedRecommendations(for: provider.id, available: serverList) { add(r.id) }
            for m in serverList { add(m) }
        } else {
            add(saved)
            for m in recent(provider.id) { add(m) }
            for r in recommended[provider.id, default: []] { add(r.id) }
            add(provider.defaultModel)
        }
        return out
    }

    /// Model family chip label regardless of gateway routing prefix (`"auto/claude-opus-4-6"` → `"Claude"`).
    static func family(of id: String) -> String {
        let low = id.lowercased()
        if low == appleModelID { return "Apple" }
        if low.contains("claude") { return "Claude" }
        if low.contains("gpt-") || low.contains("/o1") || low.contains("/o3") || low.contains("/o4")
            || low.hasPrefix("o1") || low.hasPrefix("o3") || low.hasPrefix("o4") || low.contains("chatgpt") { return "GPT" }
        if low.contains("gemini") || low.contains("gemma") { return "Gemini" }
        if low.contains("deepseek") { return "DeepSeek" }
        if low.contains("qwen") || low.contains("qwq") { return "Qwen" }
        if low.contains("glm") || low.contains("chatglm") { return "GLM" }
        if low.contains("kimi") || low.contains("moonshot") { return "Kimi" }
        if low.contains("llama") { return "Llama" }
        if low.contains("grok") { return "Grok" }
        if low.contains("mistral") || low.contains("codestral") || low.contains("pixtral") { return "Mistral" }
        if low.contains("doubao") { return String(localized: "豆包") }
        if low.contains("minimax") { return "MiniMax" }
        return String(localized: "其他")
    }

    /// Substring + compact initials/version subsequence match (e.g. `"co46"` matches `"Claude Opus 4.6"`).
    static func matches(query: String, id: String, providerTitle: String = "") -> Bool {
        let q = query.lowercased().filter { !$0.isWhitespace }
        if q.isEmpty { return true }
        let display = id == appleModelID ? String(localized: "Apple 智能 本机") : name(id)
        let full = "\(display) \(id) \(providerTitle)".lowercased()
        let stripped = full.filter { !$0.isWhitespace }
        if stripped.contains(q) { return true }

        // Build compact abbreviation from word initials + digits ("Claude Opus 4.6" -> "co46")
        var abbr = ""
        var prevBoundary = true
        for ch in display.lowercased() {
            if ch.isNumber {
                abbr.append(ch)
                prevBoundary = false
            } else if ch.isLetter {
                if prevBoundary { abbr.append(ch) }
                prevBoundary = false
            } else {
                prevBoundary = true
            }
        }
        if abbr.contains(q) { return true }

        // Ordered subsequence match across display name + raw id for fast shorthand queries
        let compactTarget = (display + id).lowercased().filter { $0.isLetter || $0.isNumber }
        var qi = q.startIndex
        for ch in compactTarget where qi < q.endIndex {
            if ch == q[qi] { qi = q.index(after: qi) }
        }
        return qi == q.endIndex
    }

    /// Short recommendation tag if this model matches a curated pick.
    static func blurb(for id: String, providerID: String) -> String? {
        if id == appleModelID { return String(localized: "在这台 Mac 上，不联网") }
        if let hit = recommended[providerID]?.first(where: { $0.id == id || id.hasSuffix("/" + $0.id) || id.hasPrefix($0.id + "-") }) {
            return hit.why
        }
        let low = id.lowercased()
        if low.contains("opus-4") { return String(localized: "深度精读") }
        if low.contains("sonnet-4") { return String(localized: "均衡主力") }
        if low.contains("haiku") { return String(localized: "极速轻量") }
        if low.contains("gemini-2.5-pro") { return String(localized: "超长全书检索") }
        if low.contains("deepseek-r1") || low.contains("reasoner") || low.contains("thinking") || low.contains("qwq") { return String(localized: "深度思考") }
        return nil
    }

    /// "anthropic/claude-…" by the part before the slash, when at least 2 groups exist.
    static func groups(_ ids: [String]) -> [(maker: String, models: [String])] {
        guard ids.count > 8, ids.contains(where: { $0.contains("/") }) else { return [("", ids)] }
        let grouped = Dictionary(grouping: ids) { id -> String in
            guard let slash = id.firstIndex(of: "/") else { return "" }
            return String(id[..<slash])
        }
        guard grouped.keys.count > 1 else { return [("", ids)] }
        return grouped.keys.sorted { a, b in
            if a == "auto" { return true }
            if b == "auto" { return false }
            if a.isEmpty { return false }
            if b.isEmpty { return true }
            return a.localizedStandardCompare(b) == .orderedAscending
        }.map { ($0.isEmpty ? String(localized: "其他") : $0, grouped[$0]!) }
    }
}

// MARK: - Pieces

/// A service's logo on a white tile, as System Settings shows apps; this Mac's own is Apple 智能's
/// many-coloured mark.
struct ServiceTile: View {
    enum Mark { case provider(CompanionProvider), cloud(CloudVoice.Provider), kokoro, thisMac, laptop, phone, add }
    let mark: Mark
    var size: CGFloat = 30

    var body: some View {
        let r = size * 0.25
        Group {
            switch mark {
            case .kokoro:
                Image(systemName: "waveform")
                    .font(.system(size: size * 0.46, weight: .semibold))
                    .foregroundStyle(YYTint.standard.accent)
                    .frame(width: size, height: size)
                    .background(tile(r))
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

                    } else if p == .free {
                        Image(systemName: "globe.asia.australia.fill")
                            .font(.system(size: size * 0.44, weight: .medium))
                            .foregroundStyle(YYColor.ok)
                    } else if p == .fish {
                        Image(systemName: "fish.fill")
                            .font(.system(size: size * 0.42, weight: .medium))
                            .foregroundStyle(Color(red: 0.14, green: 0.52, blue: 0.94))
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

    private static var imageCache: [String: Bool] = [:]
    private static var symbolCache: [String: Bool] = [:]

    static func hasImage(_ name: String) -> Bool {
        if let hit = imageCache[name] { return hit }
        #if os(macOS)
        let ok = NSImage(named: name) != nil
        #else
        let ok = UIImage(named: name) != nil
        #endif
        imageCache[name] = ok
        return ok
    }

    static func hasSymbol(_ name: String) -> Bool {
        if let hit = symbolCache[name] { return hit }
        #if os(macOS)
        let ok = NSImage(systemSymbolName: name, accessibilityDescription: nil) != nil
        #else
        let ok = UIImage(systemName: name) != nil
        #endif
        symbolCache[name] = ok
        return ok
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
        YYDividedVStack(inset: 10) { content }
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
                        .monospacedDigit()
                        .lineLimit(1)
                        .fixedSize(horizontal: true, vertical: false)
                        .foregroundStyle(active ? YYColor.t1 : YYColor.t2)
                        .padding(.horizontal, 8)
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
                .accessibilityLabel(title)
                .accessibilityAddTraits(active ? .isSelected : [])
            }
        }
        .padding(2)
        .background(YYColor.inset, in: .capsule)
        .fixedSize(horizontal: true, vertical: false)
    }
}

/// 用量与省费: clean 30-day Token 用量 card + 2-row 省费 card on macOS and iOS.
struct CompanionUsageAndCostSection: View {
    @State private var snapshot = CompanionUsage.Snapshot30Days.empty
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
            SettingsRow(String(localized: "每日用量上限"), detail: String(localized: "达到后自动暂停后台批量任务"), stackWhenNarrow: true) {
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

#if os(macOS)
// MARK: - Reader Companion Model Picker (Scheme A: Split Provider Rail + Model List)

/// Compact model trigger pill inside 书伴's input box: shows provider tile, clean model name,
/// provider tag, and opens the Scheme A split multi-provider model popover.
struct CompanionModelPill: View {
    @Binding var open: Bool
    let openSettings: () -> Void

    @AppStorage("companion.provider") private var activeProviderID = ""
    @State private var hover = false
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let _ = activeProviderID
        let config = CompanionConfig.current
        let isApple = config.provider.kind == .apple
        let title: String = if isApple {
            String(localized: "Apple 智能")
        } else if config.model.isEmpty {
            String(localized: "选择模型…")
        } else {
            ModelNames.name(config.model)
        }

        Button { open.toggle() } label: {
            HStack(spacing: 5) {
                ServiceTile(mark: isApple ? .thisMac : .provider(config.provider), size: 16)
                Text(title)
                    .font(.yy(size: 11, weight: .semibold))
                    .foregroundStyle(config.isReady ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1)
                    .truncationMode(.tail)
                if !isApple {
                    Text(config.provider.title)
                        .font(.yy(size: 9.5, weight: .medium))
                        .foregroundStyle(YYColor.t2)
                        .lineLimit(1)
                        .padding(.horizontal, 4.5)
                        .padding(.vertical, 1.5)
                        .background(YYColor.inset, in: .rect(cornerRadius: 4))
                        .fixedSize(horizontal: true, vertical: false)
                }
                Image(systemName: "chevron.up.chevron.down")
                    .font(.yy(size: 8.5, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.leading, 5)
            .padding(.trailing, 7)
            .frame(height: 26)
            .background(hover || open ? YYColor.inset : YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
            .shadow(color: .black.opacity(0.03), radius: 1, y: 1)
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(config.isReady ? config.label : String(localized: "切换服务商与模型"))
        .accessibilityLabel(Text("选择模型"))
        .popover(isPresented: $open, arrowEdge: .bottom) {
            CompanionModelPicker(
                openSettings: {
                    open = false
                    openSettings()
                },
                close: { open = false }
            )
            .presentationBackground(YYColor.card)
        }
    }
}

/// Scheme A Split Master-Detail Popover:
/// Left vertical rail holds ★ 常用收藏, 🕘 最近用过, and all added providers with real brand tiles;
/// Right pane provides family filter chips, subsequence abbreviation search (`co46`, `dsr1`),
/// hover-to-star favorites, and instant custom model ID submission.
private struct CompanionModelPicker: View {
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
    @FocusState private var searchFocused: Bool
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var focus: CompanionFocus { .shared }

    private var allProviders: [CompanionProvider] {
        var out: [CompanionProvider] = [.apple]
        var seen: Set<String> = [CompanionProvider.apple.id]
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

        VStack(spacing: 0) {
            searchHeader
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            HStack(spacing: 0) {
                leftRail(starredCount: starred.count, recentCount: recents.count)
                Rectangle().fill(YYColor.line).frame(width: 0.5)
                rightPane(current: current, starred: starred, recents: recents)
            }
        }
        .frame(width: 460, height: 368)
        .background(YYColor.card)
        .onAppear {
            let initialStarred = ModelNames.starredEntries(among: readyProviders)
            if !initialStarred.isEmpty && current.isReady {
                nav = .starred
            } else {
                nav = .provider(current.provider.id)
            }
            searchFocused = true
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

    // MARK: Search Header

    private var searchHeader: some View {
        HStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.yy(size: 11.5, weight: .medium))
                .foregroundStyle(YYColor.t3)
            TextField("", text: $query, prompt: Text("搜全量模型、ID 或缩写（如 co46 / dsr1）…"))
                .textFieldStyle(.plain)
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t1)
                .focused($searchFocused)
                .onSubmit { submitSearch() }
            if !query.isEmpty {
                Button { query = "" } label: {
                    Image(systemName: "xmark.circle.fill")
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                }
                .buttonStyle(.plain)
                .help(Text("清除搜索"))
            } else {
                Button(action: openSettings) {
                    Image(systemName: "slider.horizontal.3")
                        .font(.yy(size: 11.5, weight: .medium))
                        .foregroundStyle(YYColor.t2)
                        .frame(width: 24, height: 24)
                        .contentShape(.rect)
                }
                .buttonStyle(HoverCircleStyle())
                .help(Text("书伴设置…"))
            }
        }
        .padding(.horizontal, 12)
        .frame(height: 40)
    }

    // MARK: Left Provider Rail

    private func leftRail(starredCount: Int, recentCount: Int) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 2) {
                railRow(target: .starred, title: String(localized: "常用收藏"), badge: "\(starredCount)") {
                    Image(systemName: "star.fill")
                        .font(.yy(size: 10.5))
                        .foregroundStyle(Color(red: 0.85, green: 0.57, blue: 0.15))
                        .frame(width: 17, height: 17)
                }
                railRow(target: .recent, title: String(localized: "最近用过"), badge: "\(recentCount)") {
                    Image(systemName: "clock")
                        .font(.yy(size: 10.5, weight: .medium))
                        .foregroundStyle(YYColor.t2)
                        .frame(width: 17, height: 17)
                }

                Rectangle().fill(YYColor.line).frame(height: 0.5)
                    .padding(.horizontal, 6).padding(.vertical, 5)

                Text(String(localized: "服务商 (\(readyProviders.count))"))
                    .font(.yy(size: 10, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                    .padding(.horizontal, 8)
                    .padding(.bottom, 2)

                ForEach(allProviders) { p in
                    let cfg = CompanionConfig.saved(p)
                    let countText: String = if p.kind == .apple {
                        cfg.isConnected ? "1" : String(localized: "未开启")
                    } else if !cfg.isConnected {
                        String(localized: "未填")
                    } else {
                        "\(pool(for: p).count)"
                    }
                    railRow(target: .provider(p.id), title: p.title, badge: countText, dimmed: !cfg.isConnected) {
                        ServiceTile(mark: p.kind == .apple ? .thisMac : .provider(p), size: 17)
                    }
                }
            }
            .padding(6)
        }
        .scrollIndicators(.never)
        .frame(width: 148)
        .background(YYColor.bg)
    }

    private func railRow<Icon: View>(
        target: Nav,
        title: String,
        badge: String,
        dimmed: Bool = false,
        @ViewBuilder icon: () -> Icon
    ) -> some View {
        let selected = trimmedQuery.isEmpty && nav == target
        return Button {
            query = ""
            nav = target
        } label: {
            HStack(spacing: 7) {
                icon()
                Text(title)
                    .font(.yy(size: 11.5, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1)
                Spacer(minLength: 2)
                Text(badge)
                    .font(.yy(size: 10).monospacedDigit())
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
            .opacity(dimmed ? 0.55 : 1)
            .padding(.horizontal, 7)
            .frame(height: 29)
            .background {
                if selected {
                    RoundedRectangle(cornerRadius: 8)
                        .fill(YYColor.card)
                        .shadow(color: .black.opacity(0.05), radius: 1.5, y: 1)
                        .overlay(RoundedRectangle(cornerRadius: 8).strokeBorder(YYColor.line, lineWidth: 0.5))
                }
            }
            .contentShape(.rect(cornerRadius: 8))
        }
        .buttonStyle(.plain)
    }

    // MARK: Right Model List Pane

    @ViewBuilder
    private func rightPane(
        current: CompanionConfig,
        starred: [ModelNames.Entry],
        recents: [ModelNames.Entry]
    ) -> some View {
        if trimmedQuery.isEmpty, case .provider(let pid) = nav,
           let p = allProviders.first(where: { $0.id == pid }),
           !CompanionConfig.saved(p).isConnected {
            unconfiguredState(for: p)
        } else {
            let state = resolvedEntries(starred: starred, recents: recents)
            VStack(spacing: 0) {
                if state.families.count > 1 || state.refreshTarget != nil {
                    HStack(spacing: 6) {
                        if state.families.count > 1 {
                            ScrollView(.horizontal) {
                                HStack(spacing: 4) {
                                    familyChip(label: String(localized: "全部"), value: nil)
                                    ForEach(state.families, id: \.self) { fam in
                                        familyChip(label: fam, value: fam)
                                    }
                                }
                            }
                            .scrollIndicators(.never)
                        } else {
                            Text(state.refreshTarget?.title ?? "")
                                .font(.yy(size: 11, weight: .semibold))
                                .foregroundStyle(YYColor.t2)
                            Spacer(minLength: 0)
                        }
                        if let p = state.refreshTarget, p.kind != .apple {
                            Button { Task { await fetchModels(for: p, force: true) } } label: {
                                Group {
                                    if loadingProvider == p.id {
                                        ProgressView().controlSize(.mini)
                                    } else {
                                        Image(systemName: "arrow.clockwise")
                                            .font(.yy(size: 10.5, weight: .medium))
                                            .foregroundStyle(YYColor.t2)
                                    }
                                }
                                .frame(width: 22, height: 22)
                                .contentShape(.circle)
                            }
                            .buttonStyle(HoverCircleStyle())
                            .disabled(loadingProvider == p.id)
                            .help(Text("从服务商刷新模型列表"))
                        }
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 6)
                    .overlay(alignment: .bottom) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                }

                ScrollView {
                    LazyVStack(spacing: 2) {
                        ForEach(state.entries) { entry in
                            CompanionModelRow(
                                entry: entry,
                                showProvider: state.showProvider,
                                isActive: isEntryActive(entry, current: current),
                                isStarred: ModelNames.isStarred(provider: entry.provider.id, model: entry.model),
                                accent: tint.accent,
                                selectedFill: tint.accentSoft.opacity(0.65),
                                onPick: { select(entry) },
                                onToggleStar: {
                                    ModelNames.toggleStar(provider: entry.provider.id, model: entry.model)
                                    starTick &+= 1
                                }
                            )
                        }

                        if let customTarget = customIDTargetProvider,
                           !trimmedQuery.isEmpty,
                           !state.entries.contains(where: { $0.provider.id == customTarget.id && $0.model == trimmedQuery }) {
                            Button {
                                select(ModelNames.Entry(provider: customTarget, model: trimmedQuery))
                            } label: {
                                HStack(spacing: 6) {
                                    ServiceTile(mark: .provider(customTarget), size: 15)
                                    (Text(String(localized: "在 \(customTarget.title) 使用 ID："))
                                        .foregroundStyle(YYColor.t2)
                                     + Text(trimmedQuery)
                                        .font(.yy(size: 11.5, weight: .semibold, design: .monospaced))
                                        .foregroundStyle(tint.accent))
                                        .font(.yy(size: 11.5))
                                        .lineLimit(1)
                                    Spacer(minLength: 4)
                                    Text("↵").font(.yy(size: 11)).foregroundStyle(tint.accent)
                                }
                                .padding(.horizontal, 10)
                                .padding(.vertical, 8)
                                .background(
                                    RoundedRectangle(cornerRadius: 8)
                                        .strokeBorder(YYColor.line, style: StrokeStyle(lineWidth: 0.8, dash: [4, 3]))
                                )
                                .contentShape(.rect(cornerRadius: 8))
                            }
                            .buttonStyle(.plain)
                            .padding(.top, 4)
                        }

                        if state.entries.isEmpty && trimmedQuery.isEmpty {
                            emptyHint
                        }
                    }
                    .padding(6)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
    }

    private func familyChip(label: String, value: String?) -> some View {
        let on = selectedFamily == value
        return Button { selectedFamily = value } label: {
            Text(label)
                .font(.yy(size: 10.5, weight: on ? .semibold : .regular))
                .foregroundStyle(on ? tint.accent : YYColor.t2)
                .padding(.horizontal, 8)
                .frame(height: 21)
                .background(on ? tint.accentSoft : YYColor.inset, in: .capsule)
                .contentShape(.capsule)
        }
        .buttonStyle(.plain)
    }

    private func unconfiguredState(for provider: CompanionProvider) -> some View {
        VStack(spacing: 10) {
            ServiceTile(mark: provider.kind == .apple ? .thisMac : .provider(provider), size: 32)
            Text(provider.kind == .apple
                 ? (Companion.appleAvailability ?? String(localized: "Apple 智能暂不可用"))
                 : String(localized: "还没填好 \(provider.title) 的 Key"))
                .font(.yy(size: 12))
                .foregroundStyle(YYColor.t2)
                .multilineTextAlignment(.center)
                .padding(.horizontal, 20)
            Button("去设置 · 书伴") { openSettings() }
                .buttonStyle(YYButtonStyle(kind: .main, size: .small))
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var emptyHint: some View {
        VStack(spacing: 6) {
            Image(systemName: nav == .starred ? "star" : "clock")
                .font(.yy(size: 20, weight: .light))
                .foregroundStyle(YYColor.t3)
            Text(nav == .starred
                 ? String(localized: "点模型右侧的 ★ 把常用模型收进这里")
                 : String(localized: "切换过的模型会出现在这里"))
                .font(.yy(size: 11.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 72)
    }

    // MARK: Data & Selection

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

private struct CompanionModelRow: View {
    let entry: ModelNames.Entry
    let showProvider: Bool
    let isActive: Bool
    let isStarred: Bool
    let accent: Color
    let selectedFill: Color
    let onPick: () -> Void
    let onToggleStar: () -> Void

    @State private var hover = false

    var body: some View {
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

        HStack(spacing: 4) {
            Button(action: onPick) {
                HStack(spacing: 8) {
                    if showProvider {
                        ServiceTile(mark: isApple ? .thisMac : .provider(entry.provider), size: 17)
                    }
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 5) {
                            Text(displayName)
                                .font(.yy(size: 12, weight: .semibold))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(1)
                            if let route {
                                Text(route)
                                    .font(.yy(size: 9.5, weight: .medium, design: .monospaced))
                                    .foregroundStyle(YYColor.t2)
                                    .padding(.horizontal, 4.5)
                                    .padding(.vertical, 1)
                                    .background(YYColor.inset, in: .rect(cornerRadius: 4))
                                    .fixedSize()
                            }
                        }
                        if !subtitle.isEmpty {
                            Text(subtitle)
                                .font(.yy(size: 10))
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

            Button(action: onToggleStar) {
                Image(systemName: isStarred ? "star.fill" : "star")
                    .font(.yy(size: 11, weight: .medium))
                    .foregroundStyle(isStarred ? Color(red: 0.85, green: 0.57, blue: 0.15) : YYColor.t3)
                    .frame(width: 24, height: 24)
                    .opacity(isStarred ? 1 : (hover ? 0.85 : 0))
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .help(isStarred ? Text("移出常用收藏") : Text("加入常用收藏"))

            Button(action: onPick) {
                Image(systemName: "checkmark")
                    .font(.yy(size: 10.5, weight: .bold))
                    .foregroundStyle(accent)
                    .frame(width: 14, height: 24)
                    .opacity(isActive ? 1 : 0)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 9)
        .padding(.vertical, 6.5)
        .background(
            RoundedRectangle(cornerRadius: 8.5)
                .fill(isActive ? selectedFill : (hover ? YYColor.inset.opacity(0.75) : .clear))
        )
        .onHover { hover = $0 }
    }
}
#endif
