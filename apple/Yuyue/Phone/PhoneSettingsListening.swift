#if os(iOS)
import SwiftUI
import YuyueCore

/// 朗读, as the Mac's: 声音来源 — this device, OpenAI, Azure, ElevenLabs — each with what it reads
/// now; one opens its voices (or, not connected, its key); 语速 under them.
struct PhoneListening: View {
    @AppStorage("player.rate") private var rate = 1.0
    @State private var tick = 0
    @State private var addingSheet = false
    @State private var pickedProvider: CloudVoice.Provider?
    private var shownRate: Double { rate == 0 ? 1 : rate }

    var body: some View {
        let _ = tick
        let _ = KokoroDownload.shared.phase
        let sources = VoiceSource.active()
        YYPage {
            PhoneSettingsLead(String(localized: "朗读、查词发音与播客音色"))
            PhoneSettingsHeading(String(localized: "声音来源"))
            YYGroup {
                ForEach(sources, id: \.self) { source in
                    NavigationLink { PhoneVoiceSourcePage(source: source) } label: { row(source) }
                        .buttonStyle(.plain)
                }
                Button { addingSheet = true } label: {
                    HStack(spacing: YYUI.s(10)) {
                        ServiceTile(mark: .add, size: YYUI.s(26))
                        VStack(alignment: .leading, spacing: YYUI.s(1)) {
                            Text("添加声音来源").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t2)
                            Text("火山豆包、Fish Audio、小米 MiMo（限时免费）、硅基流动、局域网语音…")
                                .font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                        }
                        Spacer(minLength: YYUI.s(6))
                        Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
                    }
                    .padding(.vertical, YYUI.s(8))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            YYNote(String(localized: "推荐下载「语阅离线语音（Kokoro）」断网畅听，或直接使用免 Key 的「在线自然语音」。系统自带语音机械感较重（不推荐），仅作断网兜底。"))
            PhoneSettingsHeading(String(localized: "语速"))
            YYGroup {
                HStack(spacing: YYUI.s(8)) {
                    Text("慢").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Slider(value: Binding(get: { shownRate }, set: { rate = ($0 * 10).rounded() / 10 }), in: 0.8 ... 2.0)
                        .tint(YYTint.standard.fill)
                        .accessibilityLabel(Text("语速"))
                        .accessibilityValue(Text(String(format: "%.1f×", shownRate)))
                    Text("快").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Menu {
                        ForEach([0.8, 1.0, 1.2, 1.5, 2.0], id: \.self) { r in
                            Button { rate = r } label: {
                                if abs(shownRate - r) < 0.05 { Label(String(format: "%.1f×", r), systemImage: "checkmark") }
                                else { Text(String(format: "%.1f×", r)) }
                            }
                        }
                    } label: {
                        HStack(spacing: YYUI.s(3)) {
                            Text(String(format: "%.1f×", shownRate)).font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                            Image(systemName: "chevron.up.chevron.down").font(YYUI.font(8, .semibold)).foregroundStyle(YYColor.t3)
                        }
                        .padding(.leading, YYUI.s(4))
                        .contentShape(.rect)
                    }
                    .accessibilityLabel(Text("语速"))
                }
                .frame(minHeight: YYUI.s(40))
            }
            YYNote(String(localized: "与阅读页听书面板同步。"))
        }
        .navigationTitle("朗读")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(isPresented: $addingSheet) {
            VoiceProviderSheet { picked in
                if picked.isFreeOnline {
                    UserDefaults.standard.set(false, forKey: picked.dismissedName)
                    ListeningFocus.shared.reload()
                }
                pickedProvider = picked
            }
        }
        .navigationDestination(item: $pickedProvider) { p in
            PhoneVoiceSourcePage(source: .cloud(p))
        }
        .onAppear {
            ListeningFocus.shared.reload()
            tick &+= 1
        }
        .onReceive(NotificationCenter.default.publisher(for: KokoroModel.ready)) { _ in tick &+= 1 }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in tick &+= 1 }
    }

    private func row(_ source: VoiceSource) -> some View {
        HStack(spacing: YYUI.s(10)) {
            ServiceTile(mark: source.mark, size: YYUI.s(26)).opacity(source == .mac ? 0.65 : 1)
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                HStack(spacing: YYUI.s(5)) {
                    Text(source.title)
                        .font(YYUI.font(11.5, .medium))
                        .foregroundStyle(source == .mac ? YYColor.t2 : YYColor.t1)
                        .lineLimit(1)
                    if source == .neural {
                        SettingsPill(String(localized: "推荐"), tone: .ok)
                    } else if source == .mac {
                        SettingsPill(String(localized: "不推荐"), tone: .warn)
                    } else if case .cloud(let p) = source, let badge = p.badge {
                        SettingsPill(badge, tone: p.isFreeOnline || p == .xiaomi ? .ok : .quiet)
                    }
                }
                Text(detail(source)).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: YYUI.s(6))
            VStack(alignment: .trailing, spacing: YYUI.s(1)) {
                let reading = VoiceBook.allCases.compactMap { b in b.current.flatMap { VoiceSource.of($0) == source ? "\(b.title) · \($0.name)" : nil } }
                if !source.isSetUp {
                    Text("未连接").font(YYUI.font(10.5)).foregroundStyle(YYColor.t2)
                } else {
                    ForEach(reading, id: \.self) {
                        Label($0, systemImage: "pin.fill").labelStyle(PinLabelStyle(size: 9))
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                }
            }
            Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
        }
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }

    private func detail(_ source: VoiceSource) -> String {
        switch source {
        case .neural:
            if KokoroModel.isInstalled {
                return String(localized: "\(source.voiceCount) 个音色 · 跨端轻量离线，无需联网")
            }
            switch KokoroDownload.shared.phase {
            case .downloading(let frac):
                return String(localized: "正在下载 \(Int((frac * 100).rounded()))% · 下载期间可先听在线自然语音")
            case .failed:
                return String(localized: "下载中断 · 点进去继续下载（支持断点续传）")
            default:
                return String(localized: "一键下载（\(KokoroModel.totalMegabytes) MB）· 跨端离线自然语音")
            }
        case .mac:
            return String(localized: "\(source.voiceCount) 个音色 · 机械音明显，仅作断网兜底")
        case .cloud(let p):
            return p.isSetUp ? String(localized: "\(p.voiceCount) 个音色 · \(p.blurb)") : p.blurb
        }
    }
}

/// One source's page, as the Mac's middle column: 中文书 / 英文书, the one reading pinned, your
/// line and every voice saying it as a voice message, the box to send them another line. ⚙ in
/// the bar: its key and connection (a cloud one), or what this device has. Not connected yet: the
/// key, typed straight in.
struct PhoneVoiceSourcePage: View {
    let source: VoiceSource
    @State private var tick = 0

    var body: some View {
        let _ = tick
        Group {
            if source.isSetUp {
                VoiceSourcePage(source: source)
                    .padding(.horizontal, 16)
                    .padding(.top, 6)
                    .padding(.bottom, 8)
                    .toolbar {
                        ToolbarItem(placement: .topBarTrailing) {
                            NavigationLink { PhoneSourceSettings(source: source) { tick &+= 1 } } label: {
                                Image(systemName: "gearshape")
                            }
                            .accessibilityLabel(Text("\(source.title) 的设置"))
                        }
                    }
            } else if case .cloud(let p) = source {
                ScrollView {
                    PhoneCloudKey(provider: p) { tick &+= 1 }.padding(.top, YYUI.s(10))
                }
                .scrollBounceBehavior(.basedOnSize)
                .transition(.opacity)
            }
        }
        // A Key that works: the voices take the key page's place, not in a blink.
        .animation(YYMotion.standard, value: source.isSetUp)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(YYColor.bg)
        .navigationTitle(source.title)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { ListeningFocus.shared.reload() }
    }
}

/// ⚙ on a source's page: what the Mac shows for it in the right column — this device's voices and
/// where to get better ones; a cloud one's key, region, 检查连接, and 断开 at the foot.
private struct PhoneSourceSettings: View {
    let source: VoiceSource
    let changed: () -> Void
    @State private var removing = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        let _ = KokoroDownload.shared.phase
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                switch source {
                case .neural:
                    PanelHeader(title: source.title, detail: String(localized: "跨端轻量离线语音 · 推荐")) { ServiceTile(mark: source.mark, size: 30) }
                    PanelCard {
                        PanelRow { Text("Kokoro 引擎") } trailing: {
                            SettingsPill(KokoroModel.isInstalled ? String(localized: "已就绪 · 16 音色") : String(localized: "待下载 · \(KokoroModel.totalMegabytes) MB"),
                                         tone: KokoroModel.isInstalled ? .ok : .warn)
                        }
                        PanelRow { Text("费用") } trailing: {
                            Text("免费 · 不联网").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                        }
                    }
                    if !KokoroModel.isInstalled {
                        PanelCard {
                            PanelButton(systemImage: "arrow.down.circle", tint: YYColor.ok) {
                                Text("下载 Kokoro 离线语音（\(KokoroModel.totalMegabytes) MB）").foregroundStyle(YYColor.ok)
                                Spacer(minLength: 0)
                            } action: { KokoroDownload.shared.start() }
                        }
                    }
                    PanelHint(String(localized: "Kokoro-82M v1.1 跨端统一离线合成，下载一次即可在断网环境下自然朗读中英文书籍。"))
                case .mac:
                    PanelHeader(title: source.title, detail: String(localized: "系统自带 · 不推荐")) { ServiceTile(mark: source.mark, size: 30) }
                    PanelCard {
                        PanelRow { Text("评级") } trailing: {
                            SettingsPill(String(localized: "不推荐"), tone: .warn)
                        }
                        PanelRow { Text("声音") } trailing: {
                            Text("\(source.voiceCount) 个").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                        }
                        PanelRow { Text("费用") } trailing: {
                            Text("免费 · 仅兜底").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                        }
                    }
                    PanelHint(String(localized: "系统自带合成音机械感较重，建议改用「语阅离线语音（Kokoro）」或「在线自然语音（免 Key）」。"))
                case .cloud(let p):
                    PhoneCloudConnected(provider: p, changed: changed).padding(.horizontal, -16)
                    PanelDestructive(p.isFreeOnline ? String(localized: "隐藏 \(p.title)") : String(localized: "断开 \(p.title)")) { removing = true }
                        .confirmationDialog(p.isFreeOnline ? String(localized: "隐藏 \(p.title)？") : String(localized: "断开 \(p.title)？"), isPresented: $removing) {
                            Button(p.isFreeOnline ? "隐藏" : "断开", role: .destructive) {
                                p.remove()
                                changed()
                                dismiss()
                            }
                        } message: {
                            Text(p.isFreeOnline ? "隐藏后可在「添加声音来源」重新开启。" : "将同时移除 API Key；使用该服务音色的书籍将切回本地音色。")
                        }
                }
            }
            .padding(.horizontal, 16)
            .padding(.top, YYUI.s(10))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .navigationBarTitleDisplayMode(.inline)
    }
}

/// A cloud or LAN service not connected yet: Key (with 1-tap 书伴 Key reuse) or LAN URL.
private struct PhoneCloudKey: View {
    let provider: CloudVoice.Provider
    let connected: () -> Void
    @State private var key = ""
    @State private var appID = UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName) ?? ""
    @State private var url = ""
    @State private var model = ""
    @State private var customName = ""
    @State private var region = CloudVoice.Provider.region
    @State private var connect = AutoConnect()
    @Environment(\.openURL) private var openURL

    var body: some View {
        let step = provider.keyStep
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.blurb) { ServiceTile(mark: .cloud(provider), size: 30) }
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
                        } done: { connected() }
                    }
                }
            } else if provider.isLocal {
                PanelCard {
                    if provider == .local {
                        PanelField(label: String(localized: "名称"), text: $customName, prompt: String(localized: "比如 家里电脑"), mono: false)
                    }
                    PanelField(label: String(localized: "地址"), text: $url, prompt: provider.urlHint, bad: connect.failed, submit: { attemptLocal(now: true) })
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
                                Text("直接用「书伴」已填的 Key").font(.yy(size: PanelScale.k * 12, weight: .medium)).foregroundStyle(YYColor.ok)
                                Text(CloudVoice.Provider.mask(shared)).font(.yy(size: PanelScale.k * 10, design: .monospaced)).foregroundStyle(YYColor.t3)
                            }
                            Spacer(minLength: 6)
                            if connect.busy { ProgressView().controlSize(.mini) }
                        } action: {
                            key = shared
                            attempt(now: true)
                        }
                    }
                }
                PanelCard {
                    if provider == .azure { PhoneRegionRow(region: $region) }
                    if provider == .volcengine {
                        PanelField(label: "AppID", text: $appID, prompt: String(localized: "应用 AppID"), submit: { attempt(now: true) })
                    }
                    PanelField(label: provider == .volcengine ? "Token" : "Key",
                               text: $key,
                               prompt: provider == .azure ? String(localized: "密钥 1") : (provider == .volcengine ? String(localized: "Access Token") : provider.keyHint),
                               secure: true, bad: connect.failed, submit: { attempt(now: true) })
                    PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                        Text("检查连接").foregroundStyle(YYColor.ok)
                        Spacer(minLength: 8)
                        if connect.busy { ProgressView().controlSize(.mini) }
                        else if key.isEmpty { Text("先填 Key").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3) }
                    } action: { attempt(now: true) }
                    .opacity(key.isEmpty && !connect.busy ? 0.6 : 1)
                }
            }
            AutoConnectStatus(connect: connect)
            PanelCard {
                PanelButton(systemImage: "arrow.up.forward", tint: YYColor.t1) {
                    Text(step.title)
                    Spacer(minLength: 0)
                } action: { openURL(step.url) }
            }
            PanelHint(step.detail + (provider.isLocal || provider.isFreeOnline ? "" : String(localized: "。API Key 仅保存在本机钥匙串，调用费用由服务商直接结算。")))
        }
        .padding(.horizontal, 16)
        .onAppear {
            if provider.isLocal {
                url = provider.savedURL
                model = provider.savedModel
                if provider == .local { customName = UserDefaults.standard.string(forKey: "tts.local.title") ?? "" }
            }
        }
        .onChange(of: key) { if !provider.isLocal && !provider.isFreeOnline { attempt(now: false) } }
        .onChange(of: region) { if !provider.isLocal && !provider.isFreeOnline && !key.isEmpty { attempt(now: true) } }
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
        connect.start(String(localized: "正在连 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r, appID: a)
        } done: { connected() }
    }

    private func attemptLocal(now: Bool) {
        let u = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !u.isEmpty : AutoConnect.looksLikeAddress(u) else { connect.reset(); return }
        let p = provider, m = model, k = key, n = customName
        connect.start(String(localized: "正在探测 \(p.title)…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryLocal(url: u, model: m, key: k, customTitle: n)
        } done: { connected() }
    }
}

/// A connected cloud or LAN service on iPhone.
private struct PhoneCloudConnected: View {
    let provider: CloudVoice.Provider
    let changed: () -> Void
    @State private var editing = false
    @State private var key = ""
    @State private var url = ""
    @State private var model = ""
    @State private var region = CloudVoice.Provider.region
    @State private var connect = AutoConnect()
    @State private var check = AutoConnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: provider.title, detail: provider.inUse ? String(localized: "当前使用") : provider.account) {
                ServiceTile(mark: .cloud(provider), size: 30)
            }
            PanelCard {
                if provider.isFreeOnline {
                    PanelRow { Text("模式") } trailing: {
                        SettingsPill(String(localized: "免 Key · 零配置"), tone: .ok)
                    }
                    PanelRow { Text("线路") } trailing: {
                        Text(provider.endpointHost).font(.yy(size: PanelScale.k * 11.5, design: .monospaced)).foregroundStyle(YYColor.t2)
                    }
                } else if provider.isLocal {
                    if editing {
                        PanelField(label: String(localized: "地址"), text: $url, prompt: provider.urlHint, autofocus: true, submit: saveLocal, cancel: stop)
                        PanelField(label: String(localized: "模型"), text: $model, prompt: provider.defaultModel, submit: saveLocal, cancel: stop)
                        PanelButton(systemImage: "checkmark", tint: YYColor.ok) {
                            Text("保存并重连").foregroundStyle(YYColor.ok)
                            Spacer(minLength: 0)
                        } action: { saveLocal() }
                    } else {
                        PanelButton {
                            Text("地址")
                            Spacer(minLength: 8)
                            Text(provider.savedURL).font(.yy(size: PanelScale.k * 11.5, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1).truncationMode(.middle)
                            Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: {
                            url = provider.savedURL
                            model = provider.savedModel
                            editing = true
                        }
                        PanelRow { Text("模型") } trailing: {
                            Text(provider.savedModel).font(.yy(size: PanelScale.k * 12, design: .monospaced)).foregroundStyle(YYColor.t2)
                        }
                    }
                } else {
                    if editing {
                        PanelField(label: "Key", text: $key, prompt: String(localized: "贴上新的 Key"), secure: true, bad: connect.failed,
                                   autofocus: true, submit: { attempt(now: true) }, cancel: stop)
                    } else {
                        PanelButton {
                            Text(verbatim: "Key")
                            Spacer(minLength: 8)
                            Text(provider.maskedKey).font(.yy(size: PanelScale.k * 12, design: .monospaced)).foregroundStyle(YYColor.t2).lineLimit(1)
                            Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                        } action: { editing = true }
                    }
                    if provider == .azure { PhoneRegionRow(region: $region) }
                }
                PanelButton(systemImage: "waveform.path.ecg", tint: YYColor.ok) {
                    Text(provider.isLocal ? "刷新音色与状态" : "检查连接").foregroundStyle(YYColor.ok)
                    Spacer(minLength: 8)
                    switch check.phase {
                    case .idle: EmptyView()
                    case .checking: ProgressView().controlSize(.mini)
                    case .ok: SettingsPill(String(localized: "正常 · \(provider.voiceCount) 个音色"), tone: .ok)
                    case .failed(let why):
                        Text(why).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.danger).lineLimit(2).multilineTextAlignment(.trailing)
                    }
                } action: {
                    guard !check.busy else { return }
                    let p = provider
                    if p.isFreeOnline {
                        check.start("", delay: .zero) {
                            try await p.tryFree()
                        } done: { changed() }
                    } else if p.isLocal {
                        check.start("", delay: .zero) {
                            try await p.tryLocal(url: p.savedURL, model: p.savedModel, key: p.key ?? "")
                        } done: { changed() }
                    } else if let k = p.key {
                        let r: String? = p == .azure ? region : nil
                        let a: String? = p == .volcengine ? UserDefaults.standard.string(forKey: CloudVoice.Provider.volcAppIDName) : nil
                        check.start("", delay: .zero) { try await p.tryKey(k, region: r, appID: a) } done: { changed() }
                    }
                }
            }
            if editing {
                AutoConnectStatus(connect: connect)
                if case .idle = connect.phase, !provider.isLocal, !provider.isFreeOnline { PanelHint(String(localized: "粘贴后自动验证，有效才会替换。")) }
            }
        }
        .animation(YYMotion.standard, value: editing)
        .padding(.horizontal, 16)
        .onChange(of: key) { if !provider.isLocal && !provider.isFreeOnline { attempt(now: false) } }
        .onChange(of: region) {
            guard !provider.isLocal, !provider.isFreeOnline, let k = provider.key else { return }
            let p = provider, r = region
            check.start("", delay: .zero) { try await p.tryKey(k, region: r) } done: { changed() }
        }
    }

    private func stop() {
        editing = false
        key = ""
        connect.reset()
    }

    private func saveLocal() {
        let p = provider, u = url, m = model, k = provider.key ?? ""
        connect.start(String(localized: "正在重连 \(p.title)…"), delay: .zero) {
            try await p.tryLocal(url: u, model: m, key: k)
        } done: {
            stop()
            changed()
        }
    }

    private func attempt(now: Bool) {
        let k = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard now ? !k.isEmpty : AutoConnect.looksLikeKey(k) else { connect.reset(); return }
        let p = provider, r: String? = provider == .azure ? region : nil
        connect.start(String(localized: "正在试新的 Key…"), delay: now ? .zero : .milliseconds(600)) {
            try await p.tryKey(k, region: r)
        } done: {
            stop()
            changed()
        }
    }
}

/// Azure's region, from a menu at the end of the row.
private struct PhoneRegionRow: View {
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
                        .font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2)
                    Image(systemName: "chevron.up.chevron.down").font(.yy(size: PanelScale.k * 8.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                }
            }
        }
    }
}

// MARK: - 书伴

/// 书伴, as the Mac's: one list — this device's own (Apple 智能), the services added, 添加服务商.
/// A service opens its page: model, 书伴用它, its key typed in the row, 检查连接, 移除.
/// 添加服务商 is the picker; a service picked is typed into and tried on its own.
struct PhoneCompanionSettings: View {
    @State private var sheet: CompanionProviderSheet.Start?
    private var focus: CompanionFocus { .shared }

    var body: some View {
        let choice = focus.choice
        let _ = focus.tick
        let apple = Companion.appleAvailability == nil
        YYPage {
            PhoneSettingsLead(String(localized: "阅读问答与播客撰稿所用的大模型"))
            PhoneSettingsHeading(String(localized: "模型服务"))
            YYGroup {
                NavigationLink { PhoneCompanionService(provider: .apple) } label: {
                    row(.thisMac, dim: !apple) {
                        Text(SettingsDevice.name)
                        SettingsPill(String(localized: "不推荐"), tone: .warn)
                        if apple && choice.current.provider.kind == .apple { SettingsPill(String(localized: "当前使用"), tone: .ok) }
                    } detail: {
                        Text("Apple 智能 · 能力与上下文受限，仅本地兜底")
                    } trailing: {
                        switch Companion.appleState {
                        case .available: EmptyView()
                        case .notEnabled: SettingsPill(String(localized: "未开启"), tone: .warn)
                        case .downloading: SettingsPill(String(localized: "准备中"), tone: .warn)
                        case .unsupported: SettingsPill(String(localized: "当前系统不支持"), tone: .warn)
                        }
                    }
                }
                .buttonStyle(.plain)
                ForEach(choice.added) { p in
                    let config = CompanionConfig.saved(p)
                    NavigationLink { PhoneCompanionService(provider: p) } label: {
                        row(.provider(p)) {
                            Text(p.title)
                            if choice.current.provider == p { SettingsPill(String(localized: "当前使用"), tone: .ok) }
                        } detail: {
                            Text(([config.model.isEmpty ? String(localized: "未选择模型") : ModelNames.name(config.model)]
                                  + (config.key.isEmpty ? [] : ["Key " + config.maskedKey])).joined(separator: " · "))
                        } trailing: {
                            EmptyView()
                        }
                    }
                    .buttonStyle(.plain)
                }
                Button { sheet = .add } label: {
                    row(.add) {
                        Text("添加服务商").foregroundStyle(YYColor.t2)
                    } detail: {
                        Text("Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、本机 Ollama…")
                    } trailing: {
                        EmptyView()
                    }
                }
                .buttonStyle(.plain)
            }
            YYNote(String(localized: "API Key 仅保存在本机钥匙串，不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"))
            CompanionQuickPromptsSection()
            CompanionUsageAndCostSection()
        }
        .navigationTitle("书伴")
        .navigationBarTitleDisplayMode(.inline)
        .sheet(item: $sheet, onDismiss: { focus.reload() }) { CompanionProviderSheet(start: $0) }
        .onAppear {
            focus.reload()
            #if DEBUG
            // `-debugCompanion add` opens 添加服务商, `edit:<id>` that service's form.
            if let d = UserDefaults.standard.string(forKey: "debugCompanion"), sheet == nil {
                if d == "add" { sheet = .add }
                else if d.hasPrefix("edit:"), let p = CompanionProvider.find(String(d.dropFirst(5))) { sheet = .edit(p) }
            }
            #endif
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
        }
    }

    /// A service: its tile, its name, a grey line; the chevron at the end.
    private func row<Title: View, Detail: View, Trailing: View>(
        _ mark: ServiceTile.Mark, dim: Bool = false,
        @ViewBuilder title: () -> Title, @ViewBuilder detail: () -> Detail, @ViewBuilder trailing: () -> Trailing
    ) -> some View {
        HStack(spacing: YYUI.s(10)) {
            ServiceTile(mark: mark, size: YYUI.s(26)).opacity(dim ? 0.5 : 1)
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                HStack(spacing: YYUI.s(6)) { title() }
                    .font(YYUI.font(11.5, .medium)).foregroundStyle(dim ? YYColor.t3 : YYColor.t1)
                detail().font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: YYUI.s(8))
            trailing()
            Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
        }
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }
}

/// One service, as the Mac's right column shows it; its models one page further.
struct PhoneCompanionService: View {
    let provider: CompanionProvider
    @State private var models = false
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        ScrollView {
            ServicePanel(provider: provider, openModels: { models = true }, removed: { dismiss() })
                .padding(.top, YYUI.s(10))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { CompanionFocus.shared.reload() }
        .navigationDestination(isPresented: $models) {
            ModelList(provider: provider, back: { models = false }, showsHeader: false)
                .padding(.top, YYUI.s(8))
                .background(YYColor.bg)
                .navigationTitle("选择模型")
                .navigationBarTitleDisplayMode(.inline)
        }
    }
}
#endif
