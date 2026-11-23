#if os(iOS)
import AuthenticationServices
import SwiftUI
import UniformTypeIdentifiers
import YuyueCore

// MARK: - 同步

/// 同步: how it is going and each device's last book on top, the three ways in the
/// middle, what goes along below.
struct PhoneSync: View {
    let library: Library
    @State private var picking = false
    @State private var signingIn = false
    @State private var problem: String?
    @Environment(\.webAuthenticationSession) private var webAuthenticationSession
    private let tint = YYTint.standard

    private var sync: Sync { library.sync }

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "通过个人云盘在多台设备间同步"))
            statusCard.padding(.top, YYUI.s(12)).padding(.bottom, YYUI.s(10))
            ConflictsBanner(library: library, spacing: YYUI.s(10))
            groupLabel(String(localized: "同步方式")).padding(.top, YYUI.s(8))
            YYGroup {
                method(.icloud, "icloud", "iCloud", sync.method == .icloud && sync.isOn ? String(localized: "iCloud 云盘中你选择的文件夹") : String(localized: "使用 iCloud 云盘中的文件夹"))
                NavigationLink { PhoneWebDAV(sync: sync) } label: {
                    methodLabel(.webdav, "server.rack", "WebDAV",
                                sync.method == .webdav ? (sync.webdav.map { $0.url.host() ?? $0.url.absoluteString } ?? "") : String(localized: "坚果云、群晖、Nextcloud…"))
                }
                .buttonStyle(.plain)
                method(.googleDrive, "globe", "Google Drive",
                       GoogleAuth.clientID == nil ? String(localized: "适合跨 Android 同步 · 即将支持") : sync.method == .googleDrive ? (sync.googleEmail ?? String(localized: "已登录")) : String(localized: "用你自己的 Google 账号"))
                    .disabled(GoogleAuth.clientID == nil || signingIn)
                if signingIn {
                    HStack(spacing: YYUI.s(8)) {
                        ProgressView().controlSize(.small)
                        Text("在弹出的页面里登录 Google…").font(YYUI.font(11)).foregroundStyle(YYColor.t2)
                    }
                    .frame(minHeight: YYUI.s(38))
                }
                if let problem {
                    Label(problem, systemImage: "exclamationmark.circle").font(YYUI.font(11)).foregroundStyle(YYColor.danger)
                        .frame(minHeight: YYUI.s(38))
                }
            }
            .animation(YYMotion.standard, value: signingIn)
            .animation(YYMotion.standard, value: problem)
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "同步内容"))
            YYGroup {
                included(String(localized: "进度和书签"), .reading)
                included(String(localized: "笔记和生词"), .notes)
                included(String(localized: "设置和替换规则"), .settings)
                described(String(localized: "书文件"), String(localized: "仅在 Wi‑Fi 下传输，占用你的网盘空间；播客单集体积很小，始终同步")) {
                    YYSwitch(isOn: Binding(get: { sync.syncsFiles }, set: { sync.syncFiles($0) }), label: String(localized: "书文件"))
                }
                described(String(localized: "有声书录音"), String(localized: "仅在 Wi‑Fi 下同步完整音频文件；关闭时仅同步句级对齐信息，其他设备导入同名音频即可直接跟读")) {
                    YYSwitch(isOn: Binding(get: { sync.syncsRecordings }, set: { sync.syncRecordings($0) }), label: String(localized: "有声书录音"))
                }
            }
            .padding(.bottom, YYUI.s(14))
            if sync.isOn {
                YYGroup {
                    Button { sync.stop() } label: {
                        Text("停止同步").font(YYUI.font(11.5)).foregroundStyle(YYColor.danger)
                            .frame(maxWidth: .infinity, minHeight: YYUI.s(38))
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.bottom, YYUI.s(14))
                .transition(.opacity)
            }
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(5)) {
                Image(systemName: "lock").font(YYUI.font(9.5))
                Text("无需注册账号，数据仅在你的设备与个人网盘之间传输。")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .font(YYUI.font(10))
            .foregroundStyle(YYColor.t3)
            .padding(.horizontal, YYUI.s(4))
        }
        // 停止同步 and the devices come and go with the sync, not in a blink.
        .animation(YYMotion.standard, value: sync.isOn)
        .animation(YYMotion.layout, value: sync.devices.map(\.id))
        .navigationTitle("同步")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { sync.loadDevices() }
        .fileImporter(isPresented: $picking, allowedContentTypes: [.folder]) { result in
            guard case .success(let url) = result else { return }
            do { try sync.choose(folder: url) } catch { library.alert = Library.message(for: error) }
        }
    }

    /// How it went and when, 立即同步, then every device and how long since it synced.
    /// Red when it failed; for WebDAV a tap goes to the connection check.
    private var statusCard: some View {
        VStack(spacing: 0) {
            HStack(spacing: YYUI.s(10)) {
                statusIcon
                VStack(alignment: .leading, spacing: YYUI.s(2)) {
                    Text(headline).font(YYUI.font(14, .bold)).foregroundStyle(failed ? YYColor.danger : YYColor.t1)
                    Text(subline).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(2)
                }
                Spacer(minLength: 0)
                if sync.isOn {
                    if failed, sync.method == .webdav {
                        NavigationLink { PhoneWebDAV(sync: sync, checkNow: true) } label: { Text("查一查") }
                            .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                    } else {
                        Button("立即同步") { sync.now() }
                            .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                            .disabled(sync.state == .syncing)
                    }
                }
            }
            if !sync.devices.isEmpty {
                VStack(spacing: 0) {
                    ForEach(sync.devices, id: \.id) { d in
                        Rectangle().fill(YYColor.line).frame(height: 0.5)
                        HStack(spacing: YYUI.s(9)) {
                            Image(systemName: Self.symbol(d.platform)).font(YYUI.font(12)).foregroundStyle(YYColor.t2).frame(width: YYUI.s(14))
                            let name = d.name.isEmpty ? String(localized: "设备") : d.name
                            let label = d.thisDevice ? (d.name.isEmpty || d.name == Library.platformWord ? String(localized: "这台 \(Library.platformWord)") : String(localized: "\(d.name) · 本机")) : name
                            Text(label)
                                .font(YYUI.font(11)).foregroundStyle(YYColor.t1).lineLimit(1)
                            Spacer(minLength: YYUI.s(8))
                            Text(detail(of: d)).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
                        }
                        .frame(height: YYUI.s(34))
                    }
                }
                .padding(.top, YYUI.s(12))
            }
        }
        .padding(.top, YYUI.s(14))
        .padding(.bottom, YYUI.s(sync.devices.isEmpty ? 14 : 2))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
    }

    private var failed: Bool {
        if case .failed = sync.state { return true }
        return false
    }

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A switch with a grey line under its name, as the Mac's rows have.
    private func described<C: View>(_ title: String, _ detail: String, @ViewBuilder control: () -> C) -> some View {
        HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: YYUI.s(8))
            control()
        }
        .padding(.vertical, YYUI.s(8))
    }

    private func included(_ title: String, _ part: SyncPart) -> some View {
        YYRow(title: title) {
            YYSwitch(isOn: Binding(get: { sync.includes(part) }, set: { sync.include(part, $0) }), label: title)
        }
    }

    private func method(_ m: Sync.Method, _ symbol: String, _ title: String, _ detail: String) -> some View {
        Button { pick(m) } label: { methodLabel(m, symbol, title, detail) }
            .buttonStyle(.plain)
    }

    /// A small icon tile, the name and a grey line, a tick on the one in use.
    private func methodLabel(_ m: Sync.Method, _ symbol: String, _ title: String, _ detail: String) -> some View {
        let on = sync.method == m && sync.isOn
        return HStack(spacing: YYUI.s(10)) {
            Image(systemName: symbol)
                .font(YYUI.font(12, .medium))
                .foregroundStyle(on ? tint.accent : YYColor.t1)
                .frame(width: YYUI.s(26), height: YYUI.s(26))
                .background(on ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: YYUI.s(7)))
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: 0)
            if on { Image(systemName: "checkmark").font(YYUI.font(11, .bold)).foregroundStyle(tint.accent).transition(.opacity) }
        }
        .animation(YYMotion.quick, value: on)
        .padding(.vertical, YYUI.s(8))
        .contentShape(.rect)
    }

    private func pick(_ m: Sync.Method) {
        switch m {
        case .icloud:
            picking = true
        case .googleDrive:
            guard !signingIn else { return }
            signingIn = true
            problem = nil
            Task {
                do {
                    try await sync.connectGoogle { url, scheme in
                        try await webAuthenticationSession.authenticate(using: url, callback: .customScheme(scheme), additionalHeaderFields: [:])
                    }
                } catch {
                    problem = Sync.message(for: error)
                }
                signingIn = false
            }
        default:
            break
        }
    }

    /// A tick on green, a turning arrow, or ! on red.
    @ViewBuilder private var statusIcon: some View {
        let (symbol, color): (String, Color) = switch sync.state {
        case .syncing: ("arrow.triangle.2.circlepath", tint.accent)
        case .failed: ("exclamationmark", YYColor.danger)
        case .idle: sync.isOn ? ("checkmark", YYColor.ok) : ("icloud.slash", YYColor.t3)
        }
        Image(systemName: symbol)
            .font(YYUI.font(12, .bold))
            .foregroundStyle(color)
            .symbolEffect(.rotate, isActive: sync.state == .syncing)
            .contentTransition(.symbolEffect(.replace))
            .frame(width: YYUI.s(32), height: YYUI.s(32))
            .background(color.opacity(0.14), in: .circle)
            .animation(YYMotion.standard, value: sync.state)
            .animation(YYMotion.standard, value: sync.isOn)
    }

    private var headline: String {
        switch sync.state {
        case .syncing: return String(localized: "正在同步…")
        case .failed: return String(localized: "同步未成功")
        case .idle: return sync.isOn ? (sync.lastSynced == nil ? String(localized: "已连上") : String(localized: "已同步")) : String(localized: "尚未开启同步")
        }
    }

    /// "2 分钟前 · iCloud", or why it failed.
    private var subline: String {
        if case .failed(let why) = sync.state { return why }
        guard sync.isOn else { return String(localized: "选择下方任一方式，跨设备同步进度、笔记与设置") }
        let place = switch sync.method {
        case .webdav: "WebDAV"
        case .googleDrive: "Google Drive"
        default: "iCloud"
        }
        return [sync.lastSynced.map(Self.ago), place].compactMap { $0 }.joined(separator: " · ")
    }

    private func detail(of d: DeviceInfo) -> String {
        if d.thisDevice { return String(localized: "刚刚") }
        guard let at = d.lastAt else { return String(localized: "还没同步过") }
        return Self.ago(Date(timeIntervalSince1970: Double(at) / 1000))
    }

    static func ago(_ date: Date) -> String {
        let s = Date().timeIntervalSince(date)
        if s < 60 { return String(localized: "刚刚") }
        if s < 3600 { return String(localized: "\(Int(s / 60)) 分钟前") }
        if Calendar.current.isDateInToday(date) { return String(localized: "今天 ") + date.formatted(.dateTime.hour().minute()) }
        if Calendar.current.isDateInYesterday(date) { return String(localized: "昨天 ") + date.formatted(.dateTime.hour().minute()) }
        return date.formatted(.dateTime.month().day())
    }

    private static func symbol(_ platform: String) -> String {
        switch platform {
        case "ios": "iphone"
        case "ipados": "ipad"
        case "android": "smartphone"
        default: "laptopcomputer"
        }
    }
}

/// WebDAV: 地址, 账号, 密码, 文件夹; 连接 runs the check step by step and
/// keeps the account once every step passes.
private struct PhoneWebDAV: View {
    let sync: Sync
    /// Opened from a failed sync: check straight away.
    var checkNow = false
    @Environment(\.dismiss) private var dismiss
    @State private var url = ""
    @State private var user = ""
    @State private var password = ""
    @State private var folder = String(localized: "语阅")
    @State private var showPassword = false
    @State private var check: WebDAVCheck?
    @State private var checking = false

    private var account: Sync.WebDAVAccount? {
        guard let u = URL(string: url.trimmingCharacters(in: .whitespaces)), u.scheme?.hasPrefix("http") == true, u.host() != nil else { return nil }
        return Sync.WebDAVAccount(url: u, user: user.trimmingCharacters(in: .whitespaces), password: password,
                                  folder: folder.trimmingCharacters(in: CharacterSet(charactersIn: "/ ")))
    }

    var body: some View {
        YYPage {
            YYGroup {
                YYField(label: String(localized: "地址"), labelWidth: 44) {
                    TextField("", text: $url, prompt: Text("https://dav.jianguoyun.com/dav/"))
                        .font(YYUI.font(11, design: .monospaced))
                        .keyboardType(.URL).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                YYField(label: String(localized: "账号"), labelWidth: 44) {
                    TextField("", text: $user, prompt: Text("邮箱或用户名"))
                        .keyboardType(.emailAddress).textInputAutocapitalization(.never).autocorrectionDisabled()
                }
                YYField(label: String(localized: "密码"), labelWidth: 44) {
                    HStack(spacing: YYUI.s(8)) {
                        Group {
                            if showPassword {
                                TextField("", text: $password).textInputAutocapitalization(.never).autocorrectionDisabled()
                            } else {
                                SecureField("", text: $password, prompt: Text("坚果云填应用密码"))
                            }
                        }
                        .font(YYUI.font(11.5, design: .monospaced))
                        Button { showPassword.toggle() } label: {
                            Image(systemName: showPassword ? "eye.slash" : "eye").font(YYUI.font(12)).foregroundStyle(YYColor.t3)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(showPassword ? String(localized: "藏起密码") : String(localized: "显示密码"))
                    }
                }
                YYField(label: String(localized: "文件夹"), labelWidth: 44) {
                    HStack(spacing: 0) {
                        Text("/").foregroundStyle(YYColor.t3)
                        TextField("", text: $folder).textInputAutocapitalization(.never).autocorrectionDisabled()
                    }
                }
            }
            .foregroundStyle(YYColor.t1)
            .padding(.top, YYUI.s(10))
            Group {
                if let check {
                    YYLabel(String(localized: "连接检查"), check.summary).padding(.top, -YYUI.s(4))
                    WebDAVCheckCard(check: check)
                } else {
                    YYNote(String(localized: "密码存在这台设备的钥匙串里。点连接会一项一项查：服务器、证书、账号密码、文件夹读写。"))
                }
            }
            .transition(.opacity)
            .animation(YYMotion.standard, value: check == nil)
            Button(buttonTitle) {
                if check?.passed == true { dismiss() } else { run() }
            }
            .buttonStyle(YYButtonStyle(kind: .main, size: .large, wide: true))
            .disabled(account == nil || checking)
            .padding(.top, YYUI.s(18))
        }
        .navigationTitle("WebDAV")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear {
            if let a = sync.webdav {
                url = a.url.absoluteString
                user = a.user
                password = a.password
                folder = a.folder
                if checkNow, check == nil { run() }
            }
        }
        .onChange(of: [url, user, password, folder]) { if !checking, check?.passed == true { check = nil } }
    }

    private var buttonTitle: String {
        if checking { return String(localized: "正在检查…") }
        guard let check else { return sync.method == .webdav ? String(localized: "重新连接") : String(localized: "连接") }
        return check.passed ? String(localized: "好了") : String(localized: "重试")
    }

    private func run() {
        guard let account else { return }
        checking = true
        Task {
            let result = await WebDAVCheck.run(account) { check = $0 }
            check = result
            checking = false
            if result.passed { sync.keep(account) }
        }
    }
}

// MARK: - 存储空间

/// 存储空间: what 语阅 takes, the books deleted (kept 30 days), and each book from the largest.
struct PhoneStorage: View {
    let library: Library
    @State private var usage: StorageUsage?
    @State private var companionCache: Int64 = 0
    @State private var cleaning = false
    @State private var confirming: YYConfirm?
    private let tint = YYTint.standard

    @State private var info: BookSummary?

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "书库、有声书与本地缓存占用的空间"))
            useCard.padding(.top, YYUI.s(12)).padding(.bottom, YYUI.s(14))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("最近删除 · \(usage?.deleted.count ?? 0) 本（\(Self.size(usage?.deletedBytes ?? 0))）")
                            .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                        Text("保留 30 天以便恢复，到期自动清理").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                    Button("立即清空") {
                        let n = usage?.deleted.count ?? 0
                        confirming = YYConfirm(title: String(localized: "清空最近删除的 \(n) 本书？"),
                                               message: String(localized: "清空后文件将彻底删除，无法恢复。"),
                                               button: String(localized: "立即清空")) { purge() }
                    }
                        .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                        .disabled((usage?.deleted.isEmpty ?? true) || cleaning)
                }
                .padding(.vertical, YYUI.s(9))
                if let usage, usage.voiceModelBytes > 0 {
                    HStack(spacing: YYUI.s(10)) {
                        VStack(alignment: .leading, spacing: YYUI.s(1)) {
                            Text("朗读模型与声音包 · \(Self.size(usage.voiceModelBytes))")
                                .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                            Text("离线朗读模型、自带声音与音效包；移除后可在听书设置里重新下载").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                        Button("移除") {
                            confirming = YYConfirm(title: String(localized: "移除朗读模型与声音包？"),
                                                   message: String(localized: "移除后将改用系统声音朗读，随时可以在听书设置里重新下载。"),
                                                   button: String(localized: "移除")) {
                                StorageUsage.removeVoiceModels(in: library.dataDir)
                                LiveVoiceDownload.shared.refresh()
                                Task { await measure() }
                            }
                        }
                        .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                        .disabled({ if case .downloading = LiveVoiceDownload.shared.phase { true } else { false } }())
                    }
                    .padding(.vertical, YYUI.s(9))
                }
                if let usage, usage.alignerBytes > 0 {
                    HStack(spacing: YYUI.s(10)) {
                        VStack(alignment: .leading, spacing: YYUI.s(1)) {
                            Text("语音对齐模型 · \(Self.size(usage.alignerBytes))")
                                .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                            Text("用于有声书句级对齐；移除后下次合成时重新下载（约 270 MB）").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: 0)
                        Button("移除") {
                            confirming = YYConfirm(title: String(localized: "移除语音对齐模型？"),
                                                   message: String(localized: "已合成的有声书不受影响；下次合成时将重新下载（约 270 MB）。"),
                                                   button: String(localized: "移除")) {
                                StorageUsage.removeAligner(in: library.dataDir)
                                Task { await measure() }
                            }
                        }
                        .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                        .disabled(library.audiobooks.job != nil)
                    }
                    .padding(.vertical, YYUI.s(9))
                }
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("书伴问答缓存 · \(Self.size(companionCache))")
                            .font(YYUI.font(11.5, .medium).monospacedDigit()).foregroundStyle(YYColor.t1)
                        Text("同章节重复提问直接读取本地缓存，不重复调用模型").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    Button("清理") {
                        confirming = YYConfirm(title: String(localized: "清理书伴问答缓存？"),
                                               message: String(localized: "清理后，相同问题或前情回顾将重新请求模型。"),
                                               button: String(localized: "清理")) {
                            try? FileManager.default.removeItem(at: companionDir)
                            measureCompanion()
                            Task { await measure() }
                        }
                    }
                    .buttonStyle(YYButtonStyle(kind: .danger, size: .small))
                    .disabled(companionCache == 0)
                }
                .padding(.vertical, YYUI.s(9))
            }
            if let usage, !usage.books.isEmpty {
                YYLabel(String(localized: "各书占用空间"), "")
                YYGroup {
                    ForEach(usage.books.prefix(30), id: \.id) { b in bookRow(b) }
                }
            }
        }
        .navigationTitle("存储")
        .navigationBarTitleDisplayMode(.inline)
        .task { await measure() }
        .onAppear(perform: measureCompanion)
        .sheet(item: $info) { PhoneBookInfo(library: library, book: $0) }
        .yyConfirm($confirming)
    }

    private var companionDir: URL { library.dataDir.appending(path: "companion", directoryHint: .isDirectory) }

    private func measureCompanion() {
        let files = (try? FileManager.default.contentsOfDirectory(at: companionDir, includingPropertiesForKeys: [.fileSizeKey])) ?? []
        companionCache = files.reduce(0) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
    }

    /// The total, what is left on the device, one bar split by kind and its legend.
    private var useCard: some View {
        let parts = parts
        let visible = parts.filter { $0.1 > 0 }
        let total = max(1, parts.reduce(0) { $0 + $1.1 })
        return VStack(alignment: .leading, spacing: 0) {
            Text("已用空间").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(3)) {
                let (number, unit) = Self.split(usage?.total ?? 0)
                Text(usage == nil ? "…" : number).font(YYUI.font(26, .bold).monospacedDigit()).foregroundStyle(YYColor.t1)
                    .contentTransition(.numericText())
                Text(usage == nil ? "" : unit).font(YYUI.font(12, .semibold)).foregroundStyle(YYColor.t1)
                Spacer(minLength: YYUI.s(8))
                if let free = usage?.free {
                    Text("本机可用 \(Self.size(free))").font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                }
            }
            .padding(.top, 1)
            GeometryReader { g in
                let gap = CGFloat(max(0, visible.count - 1)) * 2
                HStack(spacing: 2) {
                    ForEach(Array(visible.enumerated()), id: \.element.0) { _, p in
                        Rectangle().fill(p.2).frame(width: max(3, max(0, g.size.width - gap) * Double(p.1) / Double(total)))
                    }
                    Spacer(minLength: 0)
                }
            }
            .frame(height: YYUI.s(10))
            .background(YYColor.inset)
            .clipShape(.rect(cornerRadius: YYUI.s(5)))
            .padding(.top, YYUI.s(10))
            LazyVGrid(columns: [GridItem(.flexible(), spacing: YYUI.s(16)), GridItem(.flexible())], alignment: .leading, spacing: YYUI.s(7)) {
                ForEach(parts, id: \.0) { p in
                    HStack(spacing: YYUI.s(6)) {
                        RoundedRectangle(cornerRadius: 2).fill(p.2).frame(width: YYUI.s(8), height: YYUI.s(8))
                        Text(p.0).foregroundStyle(YYColor.t2).lineLimit(1)
                        Spacer(minLength: 0)
                        Text(Self.size(p.1)).fontWeight(.medium).monospacedDigit().foregroundStyle(YYColor.t1)
                    }
                    .font(YYUI.font(10.5))
                }
            }
            .padding(.top, YYUI.s(12))
        }
        .padding(.vertical, YYUI.s(13))
        .padding(.horizontal, YYUI.s(14))
        .yyCard()
    }

    /// 书文件, 有声书录音, 朗读模型, 对齐模型, 字体, 已删除, and everything else (the library db, caches).
    private var parts: [(String, Int64, Color)] {
        guard let u = usage else {
            return [(String(localized: "书文件"), 0, tint.fill),
                    (String(localized: "已删除"), 0, YYColor.danger.opacity(0.7)),
                    (String(localized: "其他"), 0, YYColor.tint)]
        }
        return [(String(localized: "书文件"), u.bookBytes, tint.fill),
                (String(localized: "有声书录音"), u.audioBytes, tint.fill.opacity(0.6)),
                (String(localized: "朗读模型"), u.voiceModelBytes, YYColor.t2),
                (String(localized: "对齐模型"), u.alignerBytes, YYColor.t3),
                (String(localized: "字体"), u.fontBytes, tint.fill.opacity(0.35)),
                (String(localized: "已删除"), u.deletedBytes, YYColor.danger.opacity(0.7)),
                (String(localized: "其他"), u.otherBytes, YYColor.tint)]
            .filter { $0.1 > 0 || $0.0 == String(localized: "书文件") }
    }

    /// Cover, title and kind, the size in ink.
    private func bookRow(_ b: StorageUsage.Book) -> some View {
        let book = library.books.first { $0.id == b.id }
        return Button { info = book } label: {
            HStack(spacing: YYUI.s(10)) {
                if let book { BookCover(book: book).frame(width: YYUI.s(24)) }
                VStack(alignment: .leading, spacing: YYUI.s(1)) {
                    Text(b.title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Text(book.map(Self.kind) ?? "").font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                Spacer(minLength: YYUI.s(8))
                Text(Self.size(b.bytes)).font(YYUI.font(11, .semibold).monospacedDigit()).foregroundStyle(YYColor.t1)
            }
            .padding(.vertical, YYUI.s(7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private static func kind(_ b: BookSummary) -> String {
        switch b.format {
        case .pdf: "PDF"
        case .txt: "TXT"
        case .mobi: "MOBI"
        case .epub: "EPUB"
        case .audio: String(localized: "有声书")
        case .podcast: String(localized: "播客")
        }
    }

    /// "6.8" and "GB".
    private static func split(_ bytes: Int64) -> (String, String) {
        let s = size(bytes)
        guard let space = s.lastIndex(of: " ") else { return (s, "") }
        return (String(s[..<space]), String(s[s.index(after: space)...]))
    }

    private func measure() async {
        let active = Dictionary(library.books.map { ($0.id, $0.title) }, uniquingKeysWith: { a, _ in a })
        let measured = await StorageUsage.measure(dir: library.dataDir, active: active)
        // The total rolls to its number and the bar grows into its parts.
        withAnimation(YYMotion.standard) { usage = measured }
    }

    private func purge() {
        cleaning = true
        _ = try? library.core.purgeDeleted(olderThanMs: 0)
        Task {
            await measure()
            cleaning = false
        }
    }

    static func size(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }
}

// MARK: - 关于

/// 关于: free, no server, nothing collected; the licences, privacy, and feedback
/// by mail, as there is no server to send it to.
struct PhoneAbout: View {
    private var version: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "1.0"
        let b = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String
        return b.map { "\(v) (\($0))" } ?? v
    }

    var body: some View {
        YYPage {
            VStack(spacing: 0) {
                // 阅 on paper, the blue rule under it.
                Text("阅")
                    .font(YYUI.font(30, .bold, design: .serif))
                    .foregroundStyle(Color(yy: YYRGBA(28, 29, 32)))
                    .padding(.bottom, YYUI.s(6))
                    .frame(width: YYUI.s(64), height: YYUI.s(64))
                    .overlay(alignment: .bottom) {
                        Capsule().fill(Color(yy: YYRGBA(43, 76, 126))).frame(height: YYUI.s(2.5))
                            .padding(.horizontal, YYUI.s(20)).padding(.bottom, YYUI.s(13))
                    }
                    .background(Color(yy: YYRGBA(251, 250, 247)), in: .rect(cornerRadius: YYUI.s(15)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(15)).strokeBorder(.black.opacity(0.1), lineWidth: 0.5))
                    .shadow(color: Color(yy: YYRGBA(30, 28, 24, 0.14)), radius: YYUI.s(10), y: YYUI.s(8))
                Text("语阅").font(YYUI.font(17, .bold)).foregroundStyle(YYColor.t1).padding(.top, YYUI.s(10))
                Text("版本 \(version)").font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).padding(.top, 2)
                Text("免费，没有服务器，不收集任何数据。")
                    .font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
                    .padding(.top, YYUI.s(22)).padding(.bottom, YYUI.s(26))
            }
            .frame(maxWidth: .infinity)
            .padding(.top, YYUI.s(24))
            YYGroup {
                if UpdateCenter.enabled {
                    NavigationLink { PhoneUpdate() } label: {
                        YYRow(title: String(localized: "检查更新"), icon: "arrow.down.circle",
                              value: UpdateCenter.shared.showsBadge ? UpdateCenter.shared.offer?.display : nil, chevron: true) {
                            if UpdateCenter.shared.showsBadge {
                                Circle().fill(YYColor.danger).frame(width: 8, height: 8)
                            }
                        }
                    }
                    .buttonStyle(.plain)
                }
                NavigationLink { PhoneTextPage(title: String(localized: "开源许可"), text: Self.licences) } label: {
                    YYRow(String(localized: "开源许可"), icon: "chevron.left.forwardslash.chevron.right", chevron: true)
                }
                .buttonStyle(.plain)
                NavigationLink { PhoneTextPage(title: String(localized: "隐私说明"), text: Self.privacy) } label: {
                    YYRow(String(localized: "隐私说明"), icon: "checkmark.shield", chevron: true)
                }
                .buttonStyle(.plain)
            }
            YYGroup {
                Link(destination: URL(string: "https://github.com/nowtin")!) {
                    YYRow(String(localized: "作者"), icon: "person", value: "nowtin", chevron: true)
                }
                .buttonStyle(.plain)
                Link(destination: URL(string: "https://github.com/inokoru/yuyue")!) {
                    YYRow(String(localized: "源代码"), icon: "link", value: String(localized: "GitHub"), chevron: true)
                }
                .buttonStyle(.plain)
                Link(destination: URL(string: "https://github.com/inokoru/yuyue/issues")!) {
                    YYRow(String(localized: "问题与建议"), icon: "exclamationmark.bubble", value: String(localized: "前往 GitHub 反馈"), chevron: true)
                }
                .buttonStyle(.plain)
            }
            .padding(.top, YYUI.s(12))
            PencilSignature()
                .frame(maxWidth: .infinity)
                .padding(.top, YYUI.s(96))
                .padding(.bottom, YYUI.s(40))
        }
        .navigationTitle("关于")
        .navigationBarTitleDisplayMode(.inline)
    }

    static var licences: String {
        String(localized: "语阅用到了这些开源项目，感谢它们的作者：") + "\n\n"
            + "UniFFI (MPL-2.0) · rusqlite / SQLite (MIT / Public Domain) · serde, serde_json, regex, encoding_rs, chardetng, zip, html5ever, roxmltree, ICU4X, blake3, percent-encoding, thiserror, aho-corasick, base64, crypto_box, flate2, getrandom, lewton, markup5ever_rcdom, ripemd (MIT / Apache-2.0) · rars, rust-lzo1x (MIT) · foliate-js (MIT) · zhconv (GPL-2.0-or-later) · OpenCC dictionaries (Apache-2.0) · ECDICT (MIT) · pinyin, pypinyin (MIT) · ONNX Runtime (MIT) · OkHttp, JNA (Apache-2.0) · AndroidX, Jetpack Compose, Media3, kotlinx.coroutines, Haze (Apache-2.0) · Sparkle (MIT) · PdfPig (Apache-2.0) · Simple Icons (CC0 1.0) · ZipVoice weights (CC-BY-NC 4.0) · Vocos (MIT) · voices-1, sounds-1 (CC0 1.0) · Meta MMS (CC-BY-NC 4.0) · 3D-Speaker CAM++ (Apache-2.0) · SenseVoice (FunASR Model License)"
            + "\n\n" + String(localized: "完整的许可文本随源代码一起发布。")
    }

    static var privacy: String {
        [String(localized: "语阅没有服务器，也不收集任何数据：没有账号、没有统计、没有广告。"),
         String(localized: "书、进度、笔记和生词都存在这台设备上。打开同步时，它们只在你的设备和你自己选的网盘（iCloud 云盘、WebDAV，Google Drive 即将支持）之间传，语阅看不到。"),
         String(localized: "书伴默认用设备自带的模型，不联网。你填了自己的 Key 时，提问和书里相关的几段原文会直接发给你选的服务商，费用和隐私条款按你和它的约定；Key 只存在这台设备的钥匙串里。"),
         String(localized: "云端声音和笔记导出（Notion、flomo、新枝）也只在你填了 Key 或链接时联网：要读的句子、要发的笔记直接发给你选的服务。读过的声音存在这台设备上。"),
         String(localized: "离线翻译与本地模型：翻译用系统的离线翻译；按需从 GitHub Releases 下载的对齐模型、声音包与离线语音模型都在本机运行，原文与录音不出这台设备。"),
         String(localized: "问题反馈：只有在你主动提交反馈时才会联网，通过 ntfy.sh 中转为公开的 GitHub Issue；你选择附带的邮箱、日志或私密文件会在发送前用开发者公钥在本机加密。"),
         String(localized: "检查更新：从 GitHub 下载安装的版本每天从 GitHub 取一个小文件，看有没有新版本，不带任何关于你和设备的信息；在设置 › 关于 › 检查更新里能关掉。App Store 和 Google Play 的版本不查，由商店更新。")].joined(separator: "\n\n")
    }
}

private struct PhoneTextPage: View {
    let title: String
    let text: String

    var body: some View {
        YYPage {
            Text(text)
                .font(YYUI.font(11.5))
                .lineSpacing(YYUI.s(11.5) * 0.7)
                .foregroundStyle(YYColor.t1)
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.vertical, YYUI.s(14))
                .padding(.horizontal, YYUI.s(14))
                .yyCard()
                .padding(.top, YYUI.s(10))
        }
        .navigationTitle(title)
        .navigationBarTitleDisplayMode(.inline)
    }
}
// MARK: - 最近改过

/// 最近改过: the settings changed on this device, grouped by day with consecutive edits to the
/// same setting folded, rich value chips (paper dot, font face, switch, 1-tap voice preview),
/// locked-width 改回 / 还原 actions, category filters, and tap-to-jump to each setting's page.
struct PhoneSettingsHistory: View {
    let library: Library
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @State private var filterPage: String?
    @State private var expanded: Set<UUID> = []
    @State private var feedback: FeedbackNote?
    @State private var destinationPage: SettingsPage?
    @State private var confirming: YYConfirm?
    private var log: SettingsLog { .shared }
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private struct FeedbackNote: Equatable {
        let id = UUID()
        let text: String
    }

    var body: some View {
        let pres = log.presentation(filterPage: filterPage)
        YYPage {
            if log.entries.isEmpty {
                emptyState
            } else {
                topControls(pres: pres)
                ForEach(pres.days) { dayGroup in
                    PhoneSettingsHeading(SettingsLog.dayName(dayGroup.day))
                    YYGroup {
                        ForEach(dayGroup.runs) { run in
                            let open = expanded.contains(run.id)
                            row(
                                run.head,
                                superseded: pres.superseded.contains(run.head.id),
                                olderCount: run.older.count,
                                isExpanded: open,
                                toggleExpand: {
                                    withAnimation(.snappy(duration: 0.2)) {
                                        if open { expanded.remove(run.id) } else { expanded.insert(run.id) }
                                    }
                                }
                            )
                            if !run.older.isEmpty, open {
                                ForEach(run.older) { sub in
                                    row(
                                        sub,
                                        superseded: pres.superseded.contains(sub.id),
                                        olderCount: 0,
                                        isExpanded: false,
                                        isSubStep: true,
                                        toggleExpand: {}
                                    )
                                }
                            }
                        }
                    }
                }
                YYGroup {
                    Button(role: .destructive) {
                        confirming = YYConfirm(
                            title: String(localized: "清空最近改过的记录？"),
                            message: String(localized: "设置本身不变，只是不能再从这里一条条撤销。"),
                            button: String(localized: "清空")
                        ) {
                            Voices.stopPreview()
                            withAnimation(.snappy(duration: 0.2)) { log.clear() }
                        }
                    } label: {
                        Text("清空记录")
                            .font(YYUI.font(11.5))
                            .foregroundStyle(YYColor.danger)
                            .frame(maxWidth: .infinity, minHeight: YYUI.s(38))
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
                .padding(.top, YYUI.s(20))
            }
        }
        .navigationTitle("最近改过")
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $destinationPage) { page in
            destination(for: page)
        }
        .yyConfirm($confirming)
        .task(id: feedback?.id) {
            guard feedback != nil else { return }
            try? await Task.sleep(for: .seconds(1.8))
            withAnimation(.easeOut(duration: 0.2)) { feedback = nil }
        }
        .onDisappear {
            Voices.stopPreview()
        }
    }

    private var emptyState: some View {
        VStack(spacing: YYUI.s(8)) {
            Image(systemName: "clock.arrow.circlepath")
                .font(.system(size: YYUI.s(15), weight: .regular))
                .foregroundStyle(YYColor.t3)
                .frame(width: YYUI.s(36), height: YYUI.s(36))
                .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(10)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(scheme == .dark ? .white.opacity(0.07) : YYColor.line, lineWidth: 0.5))
                .padding(.bottom, YYUI.s(2))
            Text("还没有修改记录")
                .font(YYUI.font(12, .semibold))
                .foregroundStyle(YYColor.t1)
            Text("调整过的设置会记在这里，\n每一项都能随时改回去。")
                .font(YYUI.font(10.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, YYUI.s(44))
    }

    private func topControls(pres: SettingsLog.Presentation) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack(spacing: YYUI.s(8)) {
                let latest = pres.latestUndoable
                Button {
                    if let latest { performUndo(latest) }
                } label: {
                    HStack(spacing: YYUI.s(5)) {
                        Image(systemName: "arrow.uturn.backward")
                            .font(.system(size: YYUI.s(9.5), weight: .semibold))
                        Text(latest.map { String(localized: "改回「\($0.title)」") } ?? String(localized: "无可改回项"))
                            .font(YYUI.font(10.5, .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(latest != nil ? YYColor.t1 : YYColor.t3)
                    .padding(.horizontal, YYUI.s(10))
                    .frame(height: YYUI.s(26))
                    .background(YYColor.card, in: .capsule)
                    .overlay(Capsule().strokeBorder(YYColor.pillLine, lineWidth: 0.5))
                    .contentShape(.capsule)
                }
                .buttonStyle(HistoryPressStyle())
                .disabled(latest == nil)
                .opacity(latest == nil ? 0.5 : 1)

                Spacer(minLength: YYUI.s(4))

                if let feedback {
                    HStack(spacing: YYUI.s(4)) {
                        Image(systemName: "checkmark")
                            .font(.system(size: YYUI.s(8.5), weight: .bold))
                        Text(feedback.text)
                            .font(YYUI.font(10, .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, YYUI.s(8))
                    .padding(.vertical, YYUI.s(3))
                    .background(tint.accentSoft, in: .capsule)
                    .overlay(Capsule().strokeBorder(tint.edge, lineWidth: 0.5))
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
                } else {
                    Text(String(localized: "\(log.entries.count) 项记录"))
                        .font(YYUI.font(10).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
            }

            if pres.pageCounts.count > 1 {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: YYUI.s(5)) {
                        filterPill(title: String(localized: "全部"), count: log.entries.count, selected: pres.activeFilter == nil) {
                            withAnimation(.snappy(duration: 0.18)) { filterPage = nil }
                        }
                        ForEach(pres.pageCounts, id: \.page.id) { item in
                            filterPill(title: item.page.title, count: item.count, selected: pres.activeFilter == item.page.rawValue) {
                                withAnimation(.snappy(duration: 0.18)) { filterPage = item.page.rawValue }
                            }
                        }
                    }
                    .padding(.vertical, 1)
                }
            }
        }
        .padding(.top, YYUI.s(10))
        .padding(.bottom, YYUI.s(2))
    }

    private func filterPill(title: String, count: Int, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: YYUI.s(4)) {
                Text(title)
                    .font(YYUI.font(10.5, selected ? .semibold : .regular))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                Text("\(count)")
                    .font(YYUI.font(9.5).monospacedDigit())
                    .foregroundStyle(selected ? tint.accent : YYColor.t3)
            }
            .padding(.horizontal, YYUI.s(10))
            .frame(height: YYUI.s(24))
            .background(selected ? YYColor.card : .clear, in: .capsule)
            .overlay(Capsule().strokeBorder(selected ? YYColor.pillLine : YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(HistoryPressStyle())
    }

    private func row(
        _ entry: SettingsLog.Entry,
        superseded: Bool,
        olderCount: Int,
        isExpanded: Bool,
        isSubStep: Bool = false,
        toggleExpand: @escaping () -> Void
    ) -> some View {
        let page = entry.page.flatMap(SettingsPage.init(rawValue:))
        let undone = entry.undone == true
        return HStack(alignment: .center, spacing: YYUI.s(10)) {
            Image(systemName: page?.symbol ?? "slider.horizontal.3")
                .font(YYUI.font(isSubStep ? 10 : 11))
                .foregroundStyle(YYColor.t2)
                .frame(width: YYUI.s(isSubStep ? 22 : 25), height: YYUI.s(isSubStep ? 22 : 25))
                .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(6.5)))
                .opacity(isSubStep ? 0.72 : 1)

            VStack(alignment: .leading, spacing: YYUI.s(3.5)) {
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(5)) {
                    Text(entry.title)
                        .font(YYUI.font(11.5, .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .layoutPriority(1)
                    if let page {
                        HStack(spacing: 2) {
                            Text(page.title).lineLimit(1)
                            Image(systemName: "chevron.right")
                                .font(.system(size: YYUI.s(7.5), weight: .semibold))
                                .opacity(0.6)
                        }
                        .font(YYUI.font(10))
                        .foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(4))
                    Text(entry.date.formatted(date: .omitted, time: .shortened))
                        .font(YYUI.font(9.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }

                HStack(spacing: YYUI.s(5)) {
                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.fromRaw,
                        text: entry.from,
                        color: undone ? YYColor.t1 : YYColor.t2,
                        fontSize: YYUI.s(10.5),
                        tint: tint
                    )
                    Image(systemName: "arrow.right")
                        .font(YYUI.font(8, .medium))
                        .foregroundStyle(YYColor.t3)
                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.toRaw,
                        text: entry.to,
                        color: undone ? YYColor.t3 : (superseded ? YYColor.t2 : YYColor.t1),
                        fontSize: YYUI.s(10.5),
                        strikethrough: undone,
                        tint: tint
                    )
                    .fontWeight(undone || superseded ? .regular : .semibold)
                }
                .font(YYUI.font(10.5))
                .lineLimit(1)

                if olderCount > 0 {
                    Button(action: toggleExpand) {
                        HStack(spacing: 3) {
                            Text(isExpanded
                                 ? String(localized: "收起早先 \(olderCount) 次")
                                 : String(localized: "早先 \(olderCount) 次调整"))
                            Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                                .font(.system(size: YYUI.s(7.5), weight: .semibold))
                        }
                        .font(YYUI.font(9.5))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, YYUI.s(7))
                        .frame(height: YYUI.s(19))
                        .background(YYColor.inset, in: .capsule)
                        .contentShape(.capsule)
                    }
                    .buttonStyle(HistoryPressStyle())
                    .padding(.top, 1)
                }
            }
            .opacity(undone ? 0.68 : (superseded ? 0.84 : 1))

            actionSlot(entry: entry, undone: undone)
                .frame(width: YYUI.s(56), alignment: .trailing)
        }
        .padding(.leading, isSubStep ? YYUI.s(4) : 0)
        .padding(.vertical, YYUI.s(8.5))
        .contentShape(.rect)
        .onTapGesture {
            if let page { destinationPage = page }
        }
    }

    @ViewBuilder
    private func actionSlot(entry: SettingsLog.Entry, undone: Bool) -> some View {
        if undone, entry.redo != nil {
            Button { performRedo(entry) } label: {
                HStack(spacing: 3) {
                    Image(systemName: "arrow.uturn.forward")
                        .font(.system(size: YYUI.s(8.5), weight: .semibold))
                    Text("还原")
                        .font(YYUI.font(10, .medium))
                }
                .foregroundStyle(YYColor.t2)
                .frame(width: YYUI.s(54), height: YYUI.s(23))
                .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
        } else if !undone, entry.undo != nil {
            Button { performUndo(entry) } label: {
                HStack(spacing: 3) {
                    Image(systemName: "arrow.uturn.backward")
                        .font(.system(size: YYUI.s(8.5), weight: .semibold))
                    Text("改回")
                        .font(YYUI.font(10, .medium))
                }
                .foregroundStyle(YYColor.t1)
                .frame(width: YYUI.s(54), height: YYUI.s(23))
                .background(YYColor.pill, in: .capsule)
                .overlay(Capsule().strokeBorder(YYColor.pillLine, lineWidth: 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
            .accessibilityLabel(Text("改回 \(entry.from)"))
        }
    }

    private func performUndo(_ entry: SettingsLog.Entry) {
        withAnimation(.snappy(duration: 0.2)) {
            log.undo(entry)
            feedback = FeedbackNote(text: String(localized: "已改回「\(entry.from)」"))
        }
    }

    private func performRedo(_ entry: SettingsLog.Entry) {
        withAnimation(.snappy(duration: 0.2)) {
            log.redo(entry)
            feedback = FeedbackNote(text: String(localized: "已还原「\(entry.to)」"))
        }
    }

    @ViewBuilder
    private func destination(for page: SettingsPage) -> some View {
        switch page {
        case .general: PhoneGeneral(library: library)
        case .appearance: PhoneAppearance()
        case .reading: PhoneReading(library: library)
        case .foreign: PhoneForeign(library: library)
        case .listening: PhoneListening()
        case .companion: PhoneCompanionSettings()
        case .sync: PhoneSync(library: library)
        case .notes: PhoneNoteExport()
        case .storage: PhoneStorage(library: library)
        case .data: PhoneSnapshot(library: library)
        case .update: PhoneAbout()
        }
    }
}
#endif
