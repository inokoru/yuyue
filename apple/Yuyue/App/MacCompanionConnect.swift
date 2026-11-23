import SwiftUI

// 设置 · 书伴, a service added: its key is changed straight in its row in the right column
// not in the sheet. Once what's typed looks whole it's
// tried, and kept only if the service answers. 添加服务商 stays a sheet.

extension CompanionProvider {
    /// Its address can be changed: every service but this Mac's own. Left as it is, the
    /// service's usual one.
    var hasAddress: Bool { kind != .apple }
}

extension Companion {
    /// What went wrong, said in the settings themselves: no "去设置" while already there.
    static func formMessage(for error: Error) -> String {
        switch error as? CompanionError {
        case .badKey?: String(localized: "Key 不对，换一个再试")
        case .notReady?: String(localized: "还没填好")
        default: message(for: error)
        }
    }
}

extension AutoConnect {
    /// The key was refused (or the account is empty): the key's row is at fault, not the address.
    var keyAtFault: Bool {
        guard failed else { return false }
        switch error as? CompanionError {
        case .badKey?, .noCredit?: return true
        default: return false
        }
    }

    /// Anything else that failed: the address couldn't be reached or didn't answer as expected.
    var addressAtFault: Bool { failed && !keyAtFault }
}

extension CompanionConfig {
    /// What 检查连接 does, here and in the forms: reach it, list its models, settle on one (the
    /// one chosen, or a good one it has), and have that model answer one short question.
    struct Tried {
        var outcome: AutoConnect.Outcome
        /// What answered: with the model settled on.
        var config: CompanionConfig
        var models: [String]
    }

    @MainActor func tryIt() async throws -> Tried {
        let models = (try? await Companion.models(self))?.sorted() ?? []
        var c = self
        if !models.isEmpty, c.model.isEmpty || (c.model == provider.defaultModel && !models.contains(c.model)) {
            c.model = ModelNames.recommended[provider.id]?.first { models.contains($0.id) }?.id ?? models[0]
        }
        guard !c.model.isEmpty else { throw CompanionError.noModel }
        let host = (url.isEmpty ? provider.defaultURL : URL(string: url))?.host() ?? provider.title
        var ticks: [AutoConnect.Tick] = [.init(title: String(localized: "连得上 \(host)"))]
        if !key.isEmpty { ticks.append(.init(title: String(localized: "Key 有效"))) }
        if !models.isEmpty { ticks.append(.init(title: String(localized: "找到 \(models.count) 个模型"))) }
        switch await CompanionTest.run(c) {
        case .success(let t):
            ticks.append(.init(title: String(localized: "\(ModelNames.name(c.model)) 答上来了"),
                               detail: String(localized: "用了 \(t.formatted(.number.precision(.fractionLength(1)))) 秒，之后能换模型")))
        case .failure(let e):
            throw e
        }
        return Tried(outcome: .init(ticks: ticks, banner: String(localized: "\(provider.title) · 已连上")), config: c, models: models)
    }
}

/// A service's key and, for local ones and the user's own, its address, in their own card.
/// The key is shown masked; pressed, it's typed again in the row. The address is always a field:
/// change it and, once it reads as a whole address, it's tried. What answers is kept; Esc puts
/// back what was there.
struct ServiceKeyCard<Extra: View>: View {
    let provider: CompanionProvider
    /// iPhone: 检查连接, as the card's last row.
    let extra: Extra
    @State private var editingKey = false
    @State private var key = ""
    @State private var url = ""
    @State private var connect = AutoConnect()
    private var focus: CompanionFocus { .shared }

    init(provider: CompanionProvider, @ViewBuilder extra: () -> Extra) {
        self.provider = provider
        self.extra = extra()
    }

    var body: some View {
        let config = CompanionConfig.saved(provider)
        VStack(alignment: .leading, spacing: 10) {
            PanelCard {
                if editingKey {
                    PanelField(label: "Key", text: $key, prompt: provider.needsKey ? String(localized: "贴上新的 Key") : String(localized: "可不填"),
                               secure: true, bad: connect.keyAtFault, autofocus: true, submit: { attempt(now: true) }, cancel: stopKey,
                               blur: { if key.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { stopKey() } })
                } else {
                    PanelButton {
                        Text(verbatim: "Key")
                        Spacer(minLength: 8)
                        Text(config.key.isEmpty ? (provider.needsKey ? String(localized: "还没填") : String(localized: "没填")) : config.maskedKey)
                            .font(.yy(size: PanelScale.k * 12, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1)
                        Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                    } action: {
                        key = ""
                        editingKey = true
                    }
                    .help(Text("换一个 Key"))
                }
                if provider.hasAddress {
                    PanelField(label: String(localized: "接口地址"), text: $url, prompt: provider.urlHint, bad: connect.addressAtFault && !editingKey,
                               submit: { attempt(now: true) }, cancel: { url = savedURL; connect.reset() })
                }
                extra
            }
            AutoConnectStatus(connect: connect)
            if case .idle = connect.phase {
                #if os(macOS)
                if editingKey {
                    PanelHint(String(localized: "粘贴后自动验证，有效才会替换；按 Esc 取消"))
                } else if provider.hasAddress && url != savedURL {
                    PanelHint(String(localized: "回车去试这个地址，能连上才换。Esc 改回去。"))
                }
                #else
                if editingKey {
                    PanelHint(String(localized: "粘贴后自动验证，有效才会替换。"))
                } else if provider.hasAddress && url != savedURL {
                    PanelHint(String(localized: "换行去试这个地址，能连上才换。"))
                }
                #endif
            }
        }
        .onAppear { url = savedURL }
        .onChange(of: key) { attempt(now: false) }
        .onChange(of: url) { attempt(now: false) }
    }

    /// What the address field starts from: the one kept, or the service's usual one.
    private var savedURL: String {
        let c = CompanionConfig.saved(provider)
        return c.url.isEmpty ? (provider.defaultURL?.absoluteString ?? "") : c.url
    }

    private func stopKey() {
        editingKey = false
        key = ""
        connect.reset()
    }

    private func attempt(now: Bool) {
        let saved = CompanionConfig.saved(provider)
        var c = saved
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        if editingKey && !k.isEmpty { c.key = k }
        if provider.hasAddress {
            let u = url.trimmingCharacters(in: .whitespaces)
            c.url = u == provider.defaultURL?.absoluteString ? "" : u
        }
        guard c != saved, c.isConnected else { connect.reset(); return }
        if !now {
            if editingKey && !k.isEmpty && !AutoConnect.looksLikeKey(k) { connect.reset(); return }
            if c.url != saved.url && !AutoConnect.looksLikeAddress(c.url.isEmpty ? savedURL : c.url) { connect.reset(); return }
        }
        var answered = c
        connect.start(String(localized: "正在试…"), delay: now ? .zero : .milliseconds(900), describe: Companion.formMessage(for:)) {
            let r = try await c.tryIt()
            answered = r.config
            return r.outcome
        } done: {
            answered.save()
            stopKey()
            url = savedURL
            focus.reload()
        }
    }
}

extension ServiceKeyCard where Extra == EmptyView {
    init(provider: CompanionProvider) { self.init(provider: provider) { EmptyView() } }
}

/// The sheet's form, on Mac and iPhone alike, in the right column's way: rows typed straight into, no 👁, no
/// 粘贴, no 取消 / 添加. Once the key (or the address) reads as whole it's tried; what answers is
/// added and 书伴 uses it, and the sheet closes. OpenRouter's sign-in is a row, not a bar.
struct MacProviderForm: View {
    let provider: CompanionProvider
    let adding: Bool
    let back: (() -> Void)?
    let close: () -> Void
    let done: () -> Void
    @State private var name = ""
    @State private var key = ""
    @State private var url = ""
    @State private var model = ""
    /// What the service listed, once reached.
    @State private var listed: [String] = []
    @State private var fetchingModels = false
    @State private var fetchError: String?
    /// 模型 pressed: its list in place of the form, ‹ back to it.
    @State private var choosingModel = false
    @State private var signingIn = false
    @State private var signIn = OpenRouterSignIn()
    @State private var connect = AutoConnect()

    var body: some View {
        if choosingModel {
            VStack(alignment: .leading, spacing: 12) {
                SheetTitle(title: String(localized: "选择模型"), logo: provider.isCustom ? nil : provider,
                           back: { choosingModel = false }, close: close)
                ModelList(provider: provider, back: { choosingModel = false }, draft: draft, initialModels: listed,
                          showsHeader: false,
                          fetched: { models in
                              listed = models
                              fetchError = nil
                          }) { id in
                    model = id
                    choosingModel = false
                }
                .padding(.horizontal, -16)
                .frame(maxHeight: .infinity, alignment: .top)
            }
        } else {
            form
        }
    }

    private var form: some View {
        VStack(alignment: .leading, spacing: 12) {
            SheetTitle(title: provider.isCustom && adding ? String(localized: "自己添加") : provider.title,
                       logo: provider.isCustom ? nil : provider, back: back, close: close)
            ScrollView {
                VStack(alignment: .leading, spacing: 10) {
                    if provider.group == .signIn {
                        PanelCard {
                            PanelButton(systemImage: "person.crop.circle", tint: YYColor.t1) {
                                Text("用 \(provider.title) 账号登录")
                                Spacer(minLength: 6)
                                if signingIn { ProgressView().controlSize(.mini) } else {
                                    Image(systemName: "arrow.up.forward").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                                }
                            } action: { signInNow() }
                        }
                        PanelLabel(String(localized: "或者贴一个已有的 Key"))
                    }
                    PanelCard {
                        if provider.isCustom {
                            PanelField(label: String(localized: "名称"), text: $name, prompt: String(localized: "比如 公司的模型"), mono: false, autofocus: true)
                        }
                        PanelField(label: "API Key", text: $key, prompt: provider.needsKey ? provider.keyHint : String(localized: "可不填"),
                                   secure: true, bad: connect.keyAtFault, autofocus: !provider.isCustom && provider.group != .signIn,
                                   submit: { attempt(now: true) })
                        PanelField(label: String(localized: "接口地址"), text: $url, prompt: provider.urlHint, bad: connect.addressAtFault,
                                   submit: { attempt(now: true) })
                        modelRow
                        fetchModelsRow
                        PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                            Text(adding ? String(localized: "检查并添加") : String(localized: "检查连接")).foregroundStyle(YYColor.ok)
                            Spacer(minLength: 8)
                            if connect.busy {
                                ProgressView().controlSize(.mini)
                            } else if !filled {
                                Text(provider.needsKey && draft.key.isEmpty ? String(localized: "先填 Key")
                                     : draft.model.isEmpty ? String(localized: "先选模型") : String(localized: "还没填好"))
                                    .font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
                            }
                        } action: {
                            guard filled, !connect.busy else { return }
                            attempt(now: true)
                        }
                        .opacity(filled || connect.busy ? 1 : 0.6)
                    }
                    if let fetchError {
                        Text(fetchError).font(.yy(size: PanelScale.k * 11)).lineSpacing(2).foregroundStyle(YYColor.danger)
                            .fixedSize(horizontal: false, vertical: true)
                            .padding(.horizontal, 12).padding(.top, -4)
                    }
                    AutoConnectStatus(connect: connect)
                    if case .idle = connect.phase, fetchError == nil {
                        PanelHint(note)
                    }
                }
                .padding(.horizontal, 1)
                .padding(.top, 2)
            }
            .scrollBounceBehavior(.basedOnSize)
        }
        .onAppear {
            if url.isEmpty { url = provider.defaultURL?.absoluteString ?? "" }
            if model.isEmpty { model = CompanionConfig.saved(provider).model }
        }
        .onChange(of: model) { attempt(now: false) }
        .onChange(of: key) {
            fetchError = nil
            attempt(now: false)
        }
        .onChange(of: url) {
            fetchError = nil
            attempt(now: false)
        }
        .onChange(of: name) { if provider.isCustom { attempt(now: false) } }
    }

    private var note: String {
        let start = String(localized: "填好就自己去连，连上就加上。")
        if provider.group == .local { return start + String(localized: "本机或局域网里跑的，提问和书里相关的几段原文只发到这个地址。") }
        if provider.isCustom { return start + String(localized: "提问和书里相关的几段原文直接发到这个地址，语阅不经手。") }
        #if os(macOS)
        let kept = String(localized: "Key 只存在这台 Mac 上，费用走你自己的 \(provider.account) 账号。")
        #else
        let kept = String(localized: "Key 只存在这台设备的钥匙串里，费用走你自己的 \(provider.account) 账号。")
        #endif
        return (provider.blurb.map { $0 + String(localized: "。") } ?? "") + start + kept
    }

    /// Enough typed to try: a key where one is needed, an address, and a model to ask (a good one
    /// is settled on when the service lists its own; a local one's must be typed).
    private var filled: Bool {
        draft.isConnected && !(provider.needsKey && draft.key.isEmpty)
    }

    /// 模型: opens the live model list (or lets the user search/type any ID).
    private var modelRow: some View {
        PanelButton {
            Text("模型")
            Spacer(minLength: 8)
            Text(model.isEmpty ? String(localized: "选择…") : ModelNames.name(model))
                .foregroundStyle(model.isEmpty ? YYColor.t3 : YYColor.t1).lineLimit(1)
            Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
        } action: { choosingModel = true }
    }

    /// Explicit action to fetch the live model list from `GET …/models` and open the picker.
    private var fetchModelsRow: some View {
        PanelButton(systemImage: "arrow.clockwise", tint: YYColor.t1) {
            Text("获取模型列表")
            Spacer(minLength: 8)
            if fetchingModels {
                ProgressView().controlSize(.mini)
            } else if !listed.isEmpty {
                Text(String(localized: "已获取 \(listed.count) 个")).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
            } else if !filled {
                Text(provider.needsKey && draft.key.isEmpty ? String(localized: "先填 Key") : String(localized: "先填接口地址"))
                    .font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
            }
        } action: {
            fetchModelsNow()
        }
        .opacity(filled || fetchingModels ? 1 : 0.6)
    }

    private func fetchModelsNow() {
        guard !fetchingModels else { return }
        let c = draft
        guard c.isConnected else {
            fetchError = provider.needsKey && c.key.isEmpty
                ? String(localized: "请先填好 API Key")
                : String(localized: "请先填好接口地址")
            return
        }
        fetchingModels = true
        fetchError = nil
        connect.reset()
        Task {
            defer { fetchingModels = false }
            do {
                let found = try await Companion.models(c).sorted()
                listed = found
                if found.isEmpty {
                    fetchError = String(localized: "服务商返回了空的模型列表，可点上方“模型”手动输入 ID")
                } else {
                    if model.isEmpty || (model == provider.defaultModel && !found.contains(model)) {
                        model = ModelNames.recommended[provider.id]?.first { found.contains($0.id) }?.id ?? found[0]
                    }
                    choosingModel = true
                }
            } catch {
                fetchError = Companion.formMessage(for: error)
            }
        }
    }

    private var draft: CompanionConfig {
        var c = CompanionConfig.saved(provider)
        if provider.isCustom {
            let n = name.trimmingCharacters(in: .whitespaces)
            let host = URL(string: url.trimmingCharacters(in: .whitespaces))?.host()
            c.provider = .custom(n.isEmpty ? (host ?? "") : n, id: provider.id)
        }
        c.key = key.trimmingCharacters(in: .whitespacesAndNewlines)
        let u = url.trimmingCharacters(in: .whitespaces)
        c.url = u == provider.defaultURL?.absoluteString ? "" : u
        c.model = model.trimmingCharacters(in: .whitespaces)
        return c
    }

    private func attempt(now: Bool) {
        guard !choosingModel, !fetchingModels else { return }
        let c = draft
        guard c.isConnected else { connect.reset(); return }
        if !now {
            // Once the user has opened/fetched the model list, let them pick and confirm manually
            // or re-try on submit/check instead of closing the sheet unexpectedly.
            if !listed.isEmpty { return }
            let typedKey = !c.key.isEmpty
            if provider.needsKey && !AutoConnect.looksLikeKey(c.key) { connect.reset(); return }
            if typedKey && !AutoConnect.looksLikeKey(c.key) { connect.reset(); return }
            if !c.url.isEmpty && !AutoConnect.looksLikeAddress(c.url) { connect.reset(); return }
            if !provider.needsKey && !typedKey && c.url.isEmpty { connect.reset(); return }
        }
        var answered = c
        connect.start(String(localized: "正在连 \(c.provider.title)…"), delay: now ? .zero : .milliseconds(800), describe: Companion.formMessage(for:)) {
            let r = try await c.tryIt()
            answered = r.config
            listed = r.models
            model = r.config.model
            return r.outcome
        } done: {
            if adding { CompanionConfig.current = answered } else { answered.save() }
            done()
        }
    }

    private func signInNow() {
        guard !signingIn else { return }
        signingIn = true
        Task {
            defer { signingIn = false }
            do {
                key = try await signIn.run()
                attempt(now: true)
            } catch {
                connect.reset()
            }
        }
    }
}
