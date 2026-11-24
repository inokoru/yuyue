#if os(macOS)
import AppKit
import AuthenticationServices
import SwiftUI
import YuyueCore

/// Which sync method the right inspector column shows, and transient OAuth state shared across columns.
@MainActor @Observable
final class SyncFocus {
    static let shared = SyncFocus()
    var selected: Sync.Method?
    var signingInGoogle = false
    var googleProblem: String?

    func active(for sync: Sync) -> Sync.Method {
        if let selected, selected != .none { return selected }
        if sync.method != .none { return sync.method }
        return sync.webdav != nil ? .webdav : .icloud
    }
}

/// Common WebDAV services offered as one-click presets in the right inspector.
enum WebDAVProvider: String, CaseIterable, Identifiable {
    case jianguoyun, synology, nextcloud, custom

    var id: String { rawValue }

    var assetName: String {
        switch self {
        case .jianguoyun: "brand-jianguoyun"
        case .synology: "brand-synology"
        case .nextcloud: "brand-nextcloud"
        case .custom: "brand-webdav"
        }
    }

    var short: String {
        switch self {
        case .jianguoyun: String(localized: "坚果云")
        case .synology: String(localized: "群晖")
        case .nextcloud: "Nextcloud"
        case .custom: String(localized: "自定义")
        }
    }

    var title: String {
        switch self {
        case .jianguoyun: String(localized: "坚果云")
        case .synology: String(localized: "群晖 NAS")
        case .nextcloud: "Nextcloud"
        case .custom: String(localized: "自定义 WebDAV")
        }
    }

    var subtitle: String {
        switch self {
        case .jianguoyun: "dav.jianguoyun.com"
        case .synology: "NAS / DSM"
        case .nextcloud: String(localized: "自建云盘")
        case .custom: String(localized: "Alist / 其他")
        }
    }

    var defaultURL: String {
        switch self {
        case .jianguoyun: "https://dav.jianguoyun.com/dav/"
        case .synology, .nextcloud, .custom: ""
        }
    }

    var urlPlaceholder: String {
        switch self {
        case .jianguoyun: "https://dav.jianguoyun.com/dav/"
        case .synology: "https://nas.example.com:5006/"
        case .nextcloud: "https://cloud.example.com/remote.php/dav/files/…"
        case .custom: "https://dav.example.com/"
        }
    }

    var userPlaceholder: String {
        switch self {
        case .jianguoyun: String(localized: "注册邮箱")
        case .synology: String(localized: "DSM 账号")
        case .nextcloud: String(localized: "用户名")
        case .custom: String(localized: "账号（可不填）")
        }
    }

    var passwordPlaceholder: String {
        switch self {
        case .jianguoyun: String(localized: "应用密码")
        case .synology: String(localized: "密码或应用密码")
        case .nextcloud: String(localized: "应用密码")
        case .custom: String(localized: "密码（可不填）")
        }
    }

    var hint: String {
        switch self {
        case .jianguoyun: String(localized: "坚果云请使用第三方「应用密码」，而非登录密码。")
        case .synology: String(localized: "请先在群晖套件中心启用 WebDAV Server，默认 HTTPS 端口 5006。")
        case .nextcloud: String(localized: "路径需以 /remote.php/dav/files/用户名/ 结尾；开启两步验证请用应用密码。")
        case .custom: String(localized: "支持任意标准 WebDAV 服务（Alist、InfiniCLOUD 等）。")
        }
    }

    var guideTitle: String? {
        self == .jianguoyun ? String(localized: "获取应用密码") : nil
    }

    var guideURL: URL? {
        self == .jianguoyun ? URL(string: "https://www.jianguoyun.com/d/account#safe") : nil
    }

    static func detect(url: URL) -> WebDAVProvider {
        let host = (url.host() ?? "").lowercased()
        let path = url.path(percentEncoded: false).lowercased()
        if host.contains("jianguoyun") { return .jianguoyun }
        if path.contains("remote.php/dav") || host.contains("nextcloud") { return .nextcloud }
        if url.port == 5005 || url.port == 5006 || host.contains("synology") || host.contains("quickconnect") {
            return .synology
        }
        return .custom
    }
}

/// 设置 · 同步: the state and each device's last sync on top, the three ways in
/// the middle (whose configuration lives in the right inspector), and what goes along below.
struct SyncSettings: View {
    let library: Library
    /// What 书文件 and 有声书录音 take, worked out when the page opens.
    @State private var filesSize: Int64?
    @State private var recordingsSize: Int64?
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id

    private var sync: Sync { library.sync }
    private var focus: SyncFocus { .shared }
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            // The banner only takes room when there's something to choose.
            VStack(spacing: 0) {
                status
                ConflictsBanner(library: library, above: 12)
            }
            SettingsHeading(String(localized: "同步方式"))
            HStack(spacing: 10) {
                methodCard(.icloud, symbol: "cloud", title: "iCloud", detail: icloudDetail)
                methodCard(.webdav, symbol: "externaldrive.connected.to.line.below", title: "WebDAV", detail: webdavDetail)
                methodCard(.googleDrive, symbol: "globe", title: "Google Drive",
                           detail: GoogleAuth.clientID == nil ? String(localized: "适合跨 Android · 即将支持") : (sync.googleEmail ?? String(localized: "用你自己的 Google 账号")))
            }
            .fixedSize(horizontal: false, vertical: true)
            SettingsHeading(String(localized: "同步内容"))
            SettingsCard {
                SettingsRow(String(localized: "进度和书签"), compact: true) { included(.reading) }
                SettingsRow(String(localized: "笔记和生词"), compact: true) { included(.notes) }
                SettingsRow(String(localized: "设置和替换规则"), compact: true) { included(.settings) }
                SettingsRow(String(localized: "书文件"), detail: filesDetail, compact: true) {
                    SettingsSwitch(isOn: Binding(get: { sync.syncsFiles }, set: { sync.syncFiles($0) }))
                }
                SettingsRow(String(localized: "有声书录音"), detail: recordingsDetail, compact: true) {
                    SettingsSwitch(isOn: Binding(get: { sync.syncsRecordings }, set: { sync.syncRecordings($0) }))
                }
            }
            Label("无需注册账号，数据仅在你的设备与个人网盘之间传输", systemImage: "lock")
                .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                .padding(.leading, 16)
            Spacer(minLength: 0)
        }
        .task {
            sync.loadDevices()
            async let f = sync.filesSizeAsync()
            async let r = sync.recordingsSizeAsync()
            filesSize = await f
            recordingsSize = await r
        }
    }

    private var icloudDetail: String {
        _ = sync.targetRevision
        if sync.method == .icloud, let folder = Sync.chosenFolder {
            return folder.lastPathComponent
        }
        return String(localized: "使用 iCloud 云盘中的文件夹")
    }

    private var webdavDetail: String {
        if sync.method == .webdav, let account = sync.webdav {
            let provider = WebDAVProvider.detect(url: account.url)
            let name = provider == .custom ? (account.url.host() ?? "WebDAV") : provider.short
            let cleanFolder = account.folder.split(separator: "/").joined(separator: "/")
            return cleanFolder.isEmpty ? name : "\(name) · /\(cleanFolder)"
        }
        return String(localized: "坚果云、群晖、Nextcloud…")
    }

    private var place: String {
        switch sync.method {
        case .webdav: String(localized: "网盘")
        case .googleDrive: "Google Drive "
        default: "iCloud "
        }
    }

    /// 3.2 GB，占你自己的 iCloud 空间；播客每一集不管开没开都同步
    private var filesDetail: String {
        let size = filesSize.map { ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) + " · " } ?? ""
        return String(localized: "\(size)占用你的 \(place)空间；播客单集体积很小，始终同步")
    }

    /// The recordings are big; where each sentence is goes with 进度和书签 anyway.
    private var recordingsDetail: String {
        let size = recordingsSize.flatMap { $0 > 0 ? ByteCountFormatter.string(fromByteCount: $0, countStyle: .file) + " · " : nil } ?? ""
        return String(localized: "\(size)同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读")
    }

    private func included(_ part: SyncPart) -> some View {
        SettingsSwitch(isOn: Binding(get: { sync.includes(part) }, set: { sync.include(part, $0) }))
    }

    // MARK: Status

    private var status: some View {
        SettingsCard {
            VStack(alignment: .leading, spacing: 14) {
                HStack(spacing: 12) {
                    statusIcon
                    VStack(alignment: .leading, spacing: 3) {
                        Text(headline).font(.yy(size: 13.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                        Text(subline).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
                    }
                    Spacer()
                    if sync.isOn {
                        Button { sync.now() } label: {
                            Label("立即同步", systemImage: "arrow.triangle.2.circlepath")
                                .font(.yy(size: 12, weight: .semibold))
                        }
                        .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                        .disabled(sync.state == .syncing)
                    }
                }
                if !sync.devices.isEmpty {
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 136), spacing: 8)], spacing: 8) {
                        ForEach(sync.devices, id: \.id) { device($0) }
                    }
                }
            }
            .padding(16)
        }
    }

    @ViewBuilder private var statusIcon: some View {
        let (symbol, color): (String, Color) = switch sync.state {
        case .syncing: ("arrow.triangle.2.circlepath", tint.accent)
        case .failed: ("exclamationmark", YYColor.danger)
        case .idle: sync.isOn ? ("checkmark", YYColor.ok) : ("icloud.slash", YYColor.t3)
        }
        Image(systemName: symbol)
            .font(.yy(size: 14, weight: .bold))
            .foregroundStyle(color)
            .yySymbolRotate(isActive: sync.state == .syncing)
            .frame(width: 32, height: 32)
            .background(color.opacity(0.13), in: .circle)
    }

    private var headline: String {
        switch sync.state {
        case .syncing: return String(localized: "正在同步…")
        case .failed: return String(localized: "同步未成功")
        case .idle:
            guard sync.isOn else { return String(localized: "尚未开启同步") }
            guard let last = sync.lastSynced else { return String(localized: "已连上") }
            return String(localized: "已同步 · ") + Sync.ago(last)
        }
    }

    private var subline: String {
        if case .failed(let why) = sync.state { return why }
        guard sync.isOn else { return String(localized: "选择下方任一方式，跨设备同步进度、笔记与设置") }
        let place: String = switch sync.method {
        case .webdav:
            if let account = sync.webdav {
                switch WebDAVProvider.detect(url: account.url) {
                case .custom: "WebDAV"
                case let provider: String(localized: "WebDAV（\(provider.short)）")
                }
            } else {
                "WebDAV"
            }
        case .googleDrive: "Google Drive"
        default: String(localized: "iCloud 云盘")
        }
        return String(localized: "\(place) · 进度、笔记和设置在 \(max(1, sync.devices.count)) 台设备之间同步")
    }

    private func device(_ d: DeviceInfo) -> some View {
        HStack(spacing: 8) {
            Image(systemName: Sync.deviceSymbol(d.platform))
                .font(.yy(size: 14))
                .foregroundStyle(YYColor.t2)
                .frame(width: 16)
            VStack(alignment: .leading, spacing: 1) {
                Text(d.name.isEmpty ? String(localized: "设备") : d.name)
                    .font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                Text(detail(of: d)).font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: 6)
            if d.thisDevice {
                Text("这台").font(.yy(size: 10.5)).foregroundStyle(YYColor.t2).lineLimit(1).fixedSize()
            }
        }
        .padding(.horizontal, 10).padding(.vertical, 8)
        .background(YYColor.inset, in: .rect(cornerRadius: 10))
    }

    private func detail(of d: DeviceInfo) -> String {
        if d.thisDevice { return String(localized: "刚刚") }
        guard let at = d.lastAt else { return String(localized: "暂无阅读记录") }
        let when = Sync.ago(Date(timeIntervalSince1970: Double(at) / 1000))
        guard let book = library.books.first(where: { $0.id == d.lastBook }) else { return when }
        let percent = d.lastFraction.map { " \(Int(($0 * 100).rounded()))%" } ?? ""
        return "\(when) · \(book.title)\(percent)"
    }

    // MARK: Ways

    private func methodCard(_ m: Sync.Method, symbol: String, title: String, detail: String) -> some View {
        let on = sync.method == m
        let focused = focus.active(for: sync) == m
        let later = m == .googleDrive && GoogleAuth.clientID == nil
        return Button {
            withAnimation(YYMotion.standard) {
                focus.selected = m
            }
        } label: {
            VStack(alignment: .leading, spacing: 2) {
                HStack(alignment: .top) {
                    Image(systemName: symbol)
                        .font(.yy(size: 12.5, weight: .medium))
                        .foregroundStyle(on || focused ? tint.accent : YYColor.t1)
                        .frame(width: 28, height: 28)
                        .background(on || focused ? tint.soft : YYColor.inset, in: .rect(cornerRadius: 8))
                    Spacer()
                    if on {
                        Image(systemName: "checkmark.circle").font(.yy(size: 14, weight: .semibold)).foregroundStyle(tint.accent)
                            .transition(.opacity)
                    } else if focused {
                        Text("配置中")
                            .font(.yy(size: 10, weight: .medium))
                            .foregroundStyle(tint.accent)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 2)
                            .background(tint.soft, in: .capsule)
                            .transition(.opacity)
                    }
                }
                .padding(.bottom, 6)
                Text(title).font(.yy(size: 12.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text(detail).font(.yy(size: 10.5)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            .padding(.horizontal, 12).padding(.vertical, 11)
            .frame(maxWidth: .infinity, alignment: .topLeading)
            .background(YYColor.card, in: .rect(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(focused ? tint.accent : YYColor.line, lineWidth: focused ? 1.5 : 0.5))
            .opacity(later ? 0.55 : 1)
            .animation(YYMotion.quick, value: on)
            .animation(YYMotion.quick, value: focused)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .disabled(later)
        .contextMenu {
            if on { Button("停止同步") { sync.stop() } }
        }
    }
}

// MARK: - Right Inspector

/// 同步 in the right column: configures or inspects whichever sync method is focused in the middle.
struct SyncInspector: View {
    let library: Library
    private var focus: SyncFocus { .shared }

    var body: some View {
        let active = focus.active(for: library.sync)
        Group {
            switch active {
            case .webdav:
                WebDAVInspector(sync: library.sync)
            case .googleDrive:
                GoogleDriveInspector(sync: library.sync)
            case .icloud, .none:
                ICloudInspector(library: library)
            }
        }
        .id(active)
        .transition(.opacity.combined(with: .offset(y: 3)))
        .animation(YYMotion.standard, value: active)
    }
}

/// A WebDAV provider's brand logo on a crisp white tile, matching `ExportTile` in 笔记导出.
private struct WebDAVBrandTile: View {
    let provider: WebDAVProvider
    var size: CGFloat = 28

    var body: some View {
        let r = size * 0.25
        Image(provider.assetName)
            .resizable()
            .scaledToFit()
            .frame(width: size * 0.66, height: size * 0.66)
            .frame(width: size, height: size)
            .background(RoundedRectangle(cornerRadius: r).fill(.white))
            .overlay(RoundedRectangle(cornerRadius: r).strokeBorder(.black.opacity(0.1), lineWidth: 0.5))
            .shadow(color: .black.opacity(0.05), radius: 1, y: 1)
    }
}

/// Standard SF Symbol tile for non-WebDAV methods in the right column header.
private struct SyncMethodTile: View {
    let symbol: String
    let active: Bool
    var size: CGFloat = 30
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        let r = size * 0.25
        Image(systemName: symbol)
            .font(.yy(size: size * 0.45, weight: .medium))
            .foregroundStyle(active ? tint.accent : YYColor.t1)
            .frame(width: size, height: size)
            .background(active ? tint.soft : YYColor.inset, in: .rect(cornerRadius: r))
    }
}

/// WebDAV configuration & live 4-step connection check in the right column.
private struct WebDAVInspector: View {
    let sync: Sync
    @State private var provider: WebDAVProvider = .jianguoyun
    @State private var url = WebDAVProvider.jianguoyun.defaultURL
    @State private var user = ""
    @State private var password = ""
    @State private var folder = String(localized: "语阅")
    @State private var check: WebDAVCheck?
    @State private var checking = false
    @State private var checkTask: Task<Void, Never>?
    @State private var loaded = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var account: Sync.WebDAVAccount? {
        let trimmed = url.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let u = URL(string: trimmed),
              let scheme = u.scheme?.lowercased(),
              (scheme == "http" || scheme == "https"),
              let host = u.host(), !host.isEmpty else { return nil }
        return Sync.WebDAVAccount(
            url: WebDAVFolder.normalizeBase(u),
            user: user.trimmingCharacters(in: .whitespacesAndNewlines),
            password: password,
            folder: folder.trimmingCharacters(in: CharacterSet(charactersIn: "/ \t\n\r"))
        )
    }

    private var isConnectedCurrent: Bool {
        guard sync.method == .webdav, let saved = sync.webdav, let draft = account else { return false }
        return WebDAVFolder.normalizeBase(saved.url) == draft.url
            && saved.user == draft.user
            && saved.password == draft.password
            && saved.folder == draft.folder
    }

    private var addressAtFault: Bool {
        guard let check, check.failed else { return false }
        if check.fixAfter == 0 || check.fixAfter == 1 { return true }
        if check.fixAfter == 2 {
            let note = check.steps[2].note
            return note == "HTTP 404" || note == "HTTP 405"
        }
        return false
    }

    private var authAtFault: Bool {
        guard let check, check.failed, check.fixAfter == 2 else { return false }
        let note = check.steps[2].note
        return note == "HTTP 401" || note == "HTTP 403"
    }

    private var folderAtFault: Bool {
        check?.failed == true && check?.fixAfter == 3
    }

    private var actionTitle: String {
        if checking { return String(localized: "正在检查连接…") }
        if check?.failed == true { return String(localized: "重试连接") }
        if isConnectedCurrent { return String(localized: "已连接 · 重新检查") }
        return String(localized: "检查并连接")
    }

    var body: some View {
        let _ = sync.targetRevision
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(
                title: isConnectedCurrent ? provider.title : String(localized: "连接 \(provider.short)"),
                detail: isConnectedCurrent ? String(localized: "WebDAV · 已连上") : String(localized: "WebDAV · 填好后逐项检查连通")
            ) {
                WebDAVBrandTile(provider: provider, size: 30)
            }

            // 2×2 brand preset grid (坚果云 / 群晖 / Nextcloud / 自定义)
            LazyVGrid(columns: [GridItem(.flexible(), spacing: 6), GridItem(.flexible(), spacing: 6)], spacing: 6) {
                ForEach(WebDAVProvider.allCases) { pr in
                    WebDAVProviderTile(provider: pr, selected: provider == pr, tint: tint) {
                        selectProvider(pr)
                    }
                }
            }

            // Connection fields in a native PanelCard
            PanelCard {
                PanelField(label: String(localized: "地址"), text: $url, prompt: provider.urlPlaceholder, mono: true, bad: addressAtFault, submit: run)
                PanelField(label: String(localized: "账号"), text: $user, prompt: provider.userPlaceholder, mono: false, bad: authAtFault, submit: run)
                PanelField(label: String(localized: "密码"), text: $password, prompt: provider.passwordPlaceholder, secure: true, revealable: true, mono: true, bad: authAtFault, submit: run)
                PanelField(label: String(localized: "文件夹"), text: $folder, prompt: String(localized: "语阅"), mono: false, bad: folderAtFault, submit: run)
            }

            // Primary action card
            let canRun = account != nil && !checking
            PanelCard {
                PanelButton(tint: canRun ? tint.accent : YYColor.t3) {
                    if checking {
                        ProgressView().controlSize(.mini).frame(width: 16)
                    } else {
                        Image(systemName: "waveform.path.ecg")
                            .font(.yy(size: PanelScale.k * 12.5, weight: .medium))
                            .frame(width: 16)
                    }
                    Text(actionTitle)
                        .fontWeight(.medium)
                    Spacer(minLength: 6)
                    if !checking {
                        if account == nil {
                            Text("先填地址")
                                .font(.yy(size: PanelScale.k * 11))
                                .foregroundStyle(YYColor.t3)
                        } else {
                            Image(systemName: "chevron.right")
                                .font(.yy(size: PanelScale.k * 9.5, weight: .semibold))
                                .foregroundStyle(YYColor.t3)
                        }
                    }
                } action: {
                    run()
                }
                .disabled(!canRun)
            }

            if let check {
                WebDAVCheckCard(check: check)
                if check.passed {
                    HStack(spacing: 7) {
                        Image(systemName: "checkmark")
                            .font(.yy(size: PanelScale.k * 10.5, weight: .bold))
                        Text("已连上并开始同步")
                            .font(.yy(size: PanelScale.k * 12, weight: .medium))
                    }
                    .foregroundStyle(YYColor.ok)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYColor.ok.opacity(0.12), in: .rect(cornerRadius: 8))
                    .transition(.opacity)
                }
            } else {
                VStack(alignment: .leading, spacing: 6) {
                    PanelHint(provider.hint)
                    if let guideTitle = provider.guideTitle, let guideURL = provider.guideURL {
                        Button {
                            NSWorkspace.shared.open(guideURL)
                        } label: {
                            HStack(spacing: 3) {
                                Text(guideTitle)
                                Image(systemName: "arrow.up.forward")
                                    .font(.yy(size: 9.5, weight: .semibold))
                            }
                            .font(.yy(size: PanelScale.k * 11, weight: .medium))
                            .foregroundStyle(tint.accent)
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .padding(.horizontal, 12)
                    }
                    PanelHint(String(localized: "密码仅保存在这台 Mac 上，不同步、不上传。"))
                }
            }

            if sync.method == .webdav {
                PanelDestructive(String(localized: "停止同步")) {
                    checkTask?.cancel()
                    checkTask = nil
                    checking = false
                    check = nil
                    sync.stop()
                }
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .onAppear {
            guard !loaded else { return }
            loaded = true
            if let saved = sync.webdav {
                provider = WebDAVProvider.detect(url: saved.url)
                url = saved.url.absoluteString
                user = saved.user
                password = saved.password
                folder = saved.folder
            } else {
                provider = .jianguoyun
                url = WebDAVProvider.jianguoyun.defaultURL
                user = ""
                password = ""
                folder = String(localized: "语阅")
            }
        }
        .onChange(of: [url, user, password, folder]) {
            if !checking, check?.passed == true {
                withAnimation(YYMotion.exit) { check = nil }
            }
        }
        .onDisappear {
            checkTask?.cancel()
            checkTask = nil
        }
    }

    private func selectProvider(_ next: WebDAVProvider) {
        let previous = provider
        withAnimation(YYMotion.quick) {
            provider = next
            let trimmed = url.trimmingCharacters(in: .whitespacesAndNewlines)
            if !next.defaultURL.isEmpty {
                url = next.defaultURL
            } else if trimmed == previous.defaultURL {
                url = ""
            }
            checkTask?.cancel()
            checkTask = nil
            checking = false
            check = nil
        }
    }

    private func run() {
        guard let account, !checking else { return }
        checkTask?.cancel()
        checking = true
        checkTask = Task {
            let result = await WebDAVCheck.run(account) { progress in
                guard !Task.isCancelled else { return }
                withAnimation(YYMotion.standard) { check = progress }
            }
            guard !Task.isCancelled else { return }
            withAnimation(YYMotion.standard) {
                check = result
                checking = false
            }
            if result.passed {
                sync.keep(account)
            }
        }
    }
}

/// One tile in the 2×2 WebDAV brand selector.
private struct WebDAVProviderTile: View {
    let provider: WebDAVProvider
    let selected: Bool
    let tint: YYTint
    let action: () -> Void
    @State private var hovering = false
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        Button(action: action) {
            HStack(spacing: 8) {
                WebDAVBrandTile(provider: provider, size: 24)
                VStack(alignment: .leading, spacing: 1) {
                    Text(provider.short)
                        .font(.yy(size: 12, weight: selected ? .semibold : .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    Text(provider.subtitle)
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, 9)
            .padding(.vertical, 8)
            .background(
                RoundedRectangle(cornerRadius: 10)
                    .fill(selected ? YYColor.card : hovering ? YYColor.card.opacity(0.95) : YYColor.card.opacity(0.75))
            )
            .overlay(
                RoundedRectangle(cornerRadius: 10)
                    .strokeBorder(borderColor, lineWidth: selected ? 1.5 : 0.5)
            )
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .animation(YYMotion.quick, value: selected)
    }

    private var borderColor: Color {
        if selected { return tint.accent }
        if hovering { return YYColor.t3.opacity(0.4) }
        return scheme == .dark ? Color(yy: YYRGBA(255, 255, 255, 0.06)) : YYColor.line
    }
}

/// iCloud Drive folder setup & status in the right column.
private struct ICloudInspector: View {
    let library: Library
    private var sync: Sync { library.sync }

    var body: some View {
        let _ = sync.targetRevision
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(
                title: String(localized: "iCloud 云盘"),
                detail: sync.method == .icloud ? String(localized: "当前使用中") : String(localized: "使用 iCloud 云盘同步")
            ) {
                SyncMethodTile(symbol: "cloud", active: sync.method == .icloud, size: 30)
            }

            if sync.method == .icloud, let folder = Sync.chosenFolder {
                PanelCard {
                    VStack(alignment: .leading, spacing: 3) {
                        Text(folder.lastPathComponent)
                            .font(.yy(size: 12.5, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                        Text(verbatim: folder.path(percentEncoded: false).replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                            .font(.yy(size: 10.5, design: .monospaced))
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(3)
                            .truncationMode(.middle)
                    }
                    .padding(.horizontal, 10)
                    .padding(.vertical, 9)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    PanelButton(systemImage: "folder", tint: YYColor.t1) {
                        Text("在访达中显示")
                    } action: {
                        let on = folder.startAccessingSecurityScopedResource()
                        NSWorkspace.shared.activateFileViewerSelecting([folder])
                        if on { folder.stopAccessingSecurityScopedResource() }
                    }
                    PanelButton(systemImage: "folder.badge.gearshape", tint: YYColor.t1) {
                        Text("更换文件夹…")
                    } action: {
                        pickFolder()
                    }
                }
                PanelHint(String(localized: "每台 Apple 设备选同一个 iCloud 云盘文件夹即可自动同步。点左侧「WebDAV」可切换到坚果云、群晖或 Nextcloud。"))
                PanelDestructive(String(localized: "停止同步")) {
                    sync.stop()
                }
            } else {
                PanelCard {
                    PanelButton(systemImage: "folder.badge.plus", tint: YYColor.t1, outlined: true) {
                        Text("选一个文件夹…")
                    } action: {
                        pickFolder()
                    }
                }
                PanelHint(String(localized: "在 iCloud 云盘里选一个文件夹（比如新建一个“语阅”），每台 Apple 设备选同一个即可自动同步。"))
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }

    private func pickFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = String(localized: "用这个文件夹同步")
        panel.message = String(localized: "在 iCloud 云盘里选一个文件夹（比如新建一个“语阅”），每台设备选同一个")
        panel.directoryURL = FileManager.default.homeDirectoryForCurrentUser
            .appending(path: "Library/Mobile Documents/com~apple~CloudDocs", directoryHint: .isDirectory)
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do { try sync.choose(folder: url) } catch { library.alert = Library.message(for: error) }
    }
}

/// Google Drive sign-in & status in the right column.
private struct GoogleDriveInspector: View {
    let sync: Sync
    private var focus: SyncFocus { .shared }
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession

    var body: some View {
        let _ = sync.targetRevision
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(
                title: "Google Drive",
                detail: sync.method == .googleDrive ? String(localized: "当前使用中") : String(localized: "适合跨 Android 同步")
            ) {
                SyncMethodTile(symbol: "globe", active: sync.method == .googleDrive, size: 30)
            }

            if sync.method == .googleDrive {
                PanelCard {
                    if let email = sync.googleEmail, !email.isEmpty {
                        PanelRow { Text("账号") } trailing: {
                            Text(verbatim: email)
                                .font(.yy(size: PanelScale.k * 12))
                                .foregroundStyle(YYColor.t2)
                                .lineLimit(1)
                                .truncationMode(.middle)
                        }
                    }
                    PanelRow { Text("位置") } trailing: {
                        Text("应用专属目录")
                            .font(.yy(size: PanelScale.k * 12))
                            .foregroundStyle(YYColor.t2)
                    }
                }
                PanelHint(String(localized: "数据存放在你自己的 Google Drive 隐藏应用目录里，语阅看不到其他文件。"))
                PanelDestructive(String(localized: "退出并停止同步")) {
                    sync.stop()
                }
            } else if GoogleAuth.clientID == nil {
                PanelHint(String(localized: "这个版本还没开通 Google Drive，敬请期待。"))
            } else {
                PanelCard {
                    PanelButton(systemImage: "person.crop.circle", tint: YYColor.t1) {
                        Text(focus.signingInGoogle ? String(localized: "在弹出的窗口里登录 Google…") : String(localized: "登录 Google 账号"))
                        Spacer(minLength: 6)
                        if focus.signingInGoogle {
                            ProgressView().controlSize(.mini)
                        } else {
                            Image(systemName: "arrow.up.forward")
                                .font(.yy(size: PanelScale.k * 9.5, weight: .semibold))
                                .foregroundStyle(YYColor.t3)
                        }
                    } action: {
                        signIn()
                    }
                    .disabled(focus.signingInGoogle)
                }
                if let problem = focus.googleProblem {
                    Text(problem)
                        .font(.yy(size: PanelScale.k * 11))
                        .foregroundStyle(YYColor.danger)
                        .padding(.horizontal, 12)
                }
                PanelHint(String(localized: "数据存放在你自己的 Google Drive 应用专属目录，方便在 Mac 与 Android 间同步。"))
            }
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }

    private func signIn() {
        guard !focus.signingInGoogle else { return }
        focus.signingInGoogle = true
        focus.googleProblem = nil
        Task {
            do {
                try await sync.connectGoogle { url, scheme in
                    try await webAuthenticationSession.authenticate(using: url, callbackURLScheme: scheme)
                }
            } catch {
                focus.googleProblem = Sync.message(for: error)
            }
            focus.signingInGoogle = false
        }
    }
}
#endif
