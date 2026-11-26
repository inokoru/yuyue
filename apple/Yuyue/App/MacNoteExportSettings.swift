import SwiftUI
#if os(macOS)
import AppKit
#else
import UIKit
#endif

#if os(macOS)

/// 设置 · 笔记导出. The middle is one list, Obsidian,
/// Notion, flomo, 新枝, each with a switch: on, it shows in 笔记 › 导出 › 发到. Choosing one puts it in
/// the right column: where it writes, when it last got something, 断开; or, not yet connected,
/// the steps and the fields, checked as soon as what's typed looks right. Under it, a sample of
/// what a book looks like there.
struct NoteExportSettings: View {
    private var focus: NotesFocus { .shared }

    var body: some View {
        let _ = focus.tick
        VStack(alignment: .leading, spacing: 12) {
            SettingsHeading(String(localized: "导出目标"))
            SettingsCard {
                ForEach(NoteExport.Target.allCases) { t in
                    ExportRow(target: t, selected: focus.target == t)
                        .settingsAnchor(anchor(for: t))
                }
            }
            SettingsNote(String(localized: "开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅保存在本机，不同步、不上传。"))
            Spacer(minLength: 0)
        }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
        }
    }

    private func anchor(for target: NoteExport.Target) -> SettingsAnchor {
        switch target {
        case .obsidian: .notesObsidian
        case .notion: .notesNotion
        case .flomo: .notesFlomo
        case .xinzhi: .notesXinzhi
        }
    }
}

#endif

/// Which target the right column shows, and the state both columns read.
@MainActor @Observable
final class NotesFocus {
    static let shared = NotesFocus()
    var target: NoteExport.Target = NoteExport.offered.first ?? .obsidian
    /// Bumped when the defaults change (a switch, an undo), so both columns read them again.
    var tick = 0
    /// iPhone: 选一个文件夹… asks the page to show the files picker.
    var pickingFolder = false

    static func open(_ url: URL) {
        #if os(macOS)
        NSWorkspace.shared.open(url)
        #else
        UIApplication.shared.open(url)
        #endif
    }

    init() {
        #if DEBUG
        // `-debugNotes notion` opens 笔记导出 on that target.
        if let t = UserDefaults.standard.string(forKey: "debugNotes").flatMap(NoteExport.Target.init(rawValue:)) { target = t }
        #endif
    }
}

#if os(macOS)
/// One target in the middle: its logo, name and where it writes; the switch at the end. The
/// row is the button; the switch is its own control beside it.
private struct ExportRow: View {
    let target: NoteExport.Target
    let selected: Bool
    @State private var hovering = false

    var body: some View {
        let ready = NoteExport.isSetUp(target)
        HStack(spacing: 0) {
            Button { NotesFocus.shared.target = target } label: {
                HStack(spacing: 12) {
                    ExportTile(target: target)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(target.title).font(.yy(size: 13, weight: .medium)).foregroundStyle(YYColor.t1)
                        Text(ExportWords.where_(target))
                            .font(.yy(size: 11, design: target == .obsidian && ready ? .monospaced : .default))
                            .foregroundStyle(YYColor.t3).lineLimit(1).truncationMode(.middle)
                    }
                    Spacer(minLength: 8)
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            Toggle(target.title, isOn: Binding(get: { ready && NoteExport.isOn(target) }, set: { on in
                // Not connected yet: the switch opens it instead.
                guard ready else { NotesFocus.shared.target = target; return }
                NoteExport.setOn(target, on)
            }))
            .labelsHidden().toggleStyle(.switch).controlSize(.small).tint(YYTint.standard.fill)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 9)
        .frame(minHeight: 52)
        .background(selected ? YYColor.t1.opacity(0.06) : hovering ? YYColor.t1.opacity(0.04) : .clear, in: .rect(cornerRadius: 9))
        .padding(4)
        .onHover { hovering = $0 }
    }
}

#endif

/// A target's logo on a white tile, like the services in 书伴.
struct ExportTile: View {
    let target: NoteExport.Target
    var size: CGFloat = 30

    var body: some View {
        let r = size * 0.25
        Group {
            if target == .flomo || target == .xinzhi {
                // flomo's and 新枝's own icons are their tiles.
                Image("brand-\(target.rawValue)").resizable().scaledToFill()
                    .clipShape(.rect(cornerRadius: r))
            } else {
                Image("brand-\(target.rawValue)").resizable().scaledToFit()
                    .frame(width: size * 0.6, height: size * 0.6)
                    .frame(width: size, height: size)
                    .background(RoundedRectangle(cornerRadius: r).fill(.white))
            }
        }
        .frame(width: size, height: size)
        .overlay(RoundedRectangle(cornerRadius: r).strokeBorder(.black.opacity(0.1), lineWidth: 0.5))
        .shadow(color: .black.opacity(0.05), radius: 1, y: 1)
    }
}

/// The words both columns use.
enum ExportWords {
    /// Under a target's name: where it writes, or that it isn't connected.
    static func where_(_ t: NoteExport.Target) -> String {
        guard NoteExport.isSetUp(t) else { return String(localized: "未连接") }
        switch t {
        case .obsidian: return NoteExport.obsidianFolder.map(path) ?? String(localized: "选好的文件夹")
        case .notion:
            let title = UserDefaults.standard.string(forKey: NoteExport.notionTitleKey) ?? ""
            return title.isEmpty ? String(localized: "每本书一页") : String(localized: "放在“\(title)”下面，每本书一页")
        case .flomo: return String(localized: "每条划线一条笔记")
        case .xinzhi:
            let name = UserDefaults.standard.string(forKey: NoteExport.xinzhiNameKey) ?? ""
            return name.isEmpty ? String(localized: "每条划线一条笔记") : String(localized: "发到“\(name)”")
        }
    }

    /// Where a token stays.
    static var kept: String {
        #if os(macOS)
        String(localized: "只存在这台 Mac 上")
        #else
        String(localized: "只存在这台设备的钥匙串里")
        #endif
    }

    static func path(_ url: URL) -> String {
        #if os(macOS)
        url.path(percentEncoded: false).replacingOccurrences(of: NSHomeDirectory(), with: "~")
        #else
        url.lastPathComponent
        #endif
    }

    /// 上次发送: "昨天 21:40 · 《故乡》12 条".
    static func last(_ t: NoteExport.Target) -> String {
        guard let l = NoteExport.lastSent(t) else { return String(localized: "尚未发送") }
        let when: String
        if Calendar.current.isDateInToday(l.date) {
            when = l.date.formatted(date: .omitted, time: .shortened)
        } else if Calendar.current.isDateInYesterday(l.date) {
            when = String(localized: "昨天 \(l.date.formatted(date: .omitted, time: .shortened))")
        } else {
            when = l.date.formatted(.dateTime.month().day())
        }
        let what = l.books > 1 ? String(localized: "《\(l.book)》等 \(l.books) 本") : String(localized: "《\(l.book)》")
        return "\(when) · \(what)" + (l.count > 0 ? String(localized: " \(l.count) 条") : String(localized: " 没有新的"))
    }
}

// MARK: - The right column

/// 笔记导出 in the right column: the target chosen in the middle, then a sample of a book there.
struct NotesInspector: View {
    /// iPhone: the target its page is about; nil: the one chosen in the middle.
    var target: NoteExport.Target?
    private var focus: NotesFocus { .shared }

    var body: some View {
        let _ = focus.tick
        let t = target ?? focus.target
        VStack(alignment: .leading, spacing: 0) {
            Group {
                if NoteExport.isSetUp(t) {
                    ConnectedPanel(target: t)
                } else {
                    switch t {
                    case .obsidian: ObsidianSetup()
                    case .notion: NotionSetup()
                    case .flomo: FlomoSetup()
                    case .xinzhi: XinzhiSetup()
                    }
                }
            }
            .id("\(t.rawValue)-\(NoteExport.isSetUp(t))")
            .transition(.opacity.combined(with: .offset(y: 3)))
            // The right column's rule between its parts; a page on iPhone just leaves a gap.
            #if os(macOS)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            #endif
            ExportSample(target: t)
                .padding(.horizontal, 16)
                .padding(.vertical, 14)
        }
        .animation(YYMotion.standard, value: t)
    }
}

/// A connected target: where it writes, when it last got something, and 断开 on its own.
private struct ConnectedPanel: View {
    let target: NoteExport.Target
    @State private var confirming: YYConfirm?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: target.title, detail: String(localized: "已连上")) { ExportTile(target: target, size: 28) }
            PanelCard {
                switch target {
                case .obsidian:
                    PanelButton {
                        Text("写入目录")
                        Spacer(minLength: 8)
                        Text(NoteExport.obsidianFolder.map(ExportWords.path) ?? "")
                            .font(.yy(size: PanelScale.k * 11, design: .monospaced)).foregroundStyle(YYColor.t2)
                            .lineLimit(1).truncationMode(.head)
                        Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold)).foregroundStyle(YYColor.t3)
                    } action: { ObsidianSetup.pick() }
                case .notion:
                    PanelRow { Text("目标页面") } trailing: {
                        Text(UserDefaults.standard.string(forKey: NoteExport.notionTitleKey).flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "选好的那一页"))
                            .font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                case .flomo:
                    PanelRow { Text("发到") } trailing: { Text("你的 flomo").font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2) }
                case .xinzhi:
                    PanelRow { Text("发到") } trailing: {
                        Text(UserDefaults.standard.string(forKey: NoteExport.xinzhiNameKey).flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "你的新枝"))
                            .font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                }
                PanelRow { Text("上次发送") } trailing: {
                    Text(ExportWords.last(target)).font(.yy(size: PanelScale.k * 11.5)).foregroundStyle(YYColor.t2).lineLimit(1)
                }
                #if os(iOS)
                // iPhone: opening it is one more row here, not a card of its own.
                openRow
                #endif
            }
            PanelHint(hint)
            #if os(macOS)
            if target != .flomo {
                PanelCard { openRow }
            }
            #endif
            PanelDestructive(String(localized: "断开 \(target.title)")) {
                confirming = YYConfirm(title: String(localized: "断开 \(target.title)？"),
                                       message: target == .obsidian ? String(localized: "已经写进去的笔记留着不动。") : String(localized: "令牌也一起删掉；已经发过去的留着不动。"),
                                       button: String(localized: "断开")) {
                    NoteExport.disconnect(target)
                    NotesFocus.shared.tick &+= 1
                }
            }
            .yyConfirm($confirming)
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }

    /// 在访达中显示, 在 Notion 里打开那一页, 在新枝里打开; flomo has nothing to open.
    @ViewBuilder private var openRow: some View {
        if target == .obsidian {
            #if os(macOS)
            PanelButton(systemImage: "folder", tint: YYColor.t1) { Text("在访达中显示") } action: {
                guard let url = NoteExport.obsidianFolder else { return }
                let on = url.startAccessingSecurityScopedResource()
                NSWorkspace.shared.activateFileViewerSelecting([url])
                if on { url.stopAccessingSecurityScopedResource() }
            }
            #else
            if let url = URL(string: "obsidian://") {
                PanelButton(systemImage: "arrow.up.forward.square", tint: YYColor.t1) { Text("打开 Obsidian") } action: { NotesFocus.open(url) }
            }
            #endif
        } else if target == .xinzhi, let url = URL(string: "https://www.xinzhi.zone") {
            PanelButton(systemImage: "arrow.up.forward.square", tint: YYColor.t1) { Text("在新枝里打开") } action: { NotesFocus.open(url) }
        } else if target == .notion, let id = NoteExport.notionPage, let url = URL(string: "https://www.notion.so/\(id)") {
            PanelButton(systemImage: "arrow.up.forward.square", tint: YYColor.t1) { Text("在 Notion 里打开那一页") } action: { NotesFocus.open(url) }
        }
    }

    private var hint: String {
        switch target {
        case .obsidian: String(localized: "每本书一篇，文件名就是书名。再发一次整篇重写成最新的，所以自己的想法记在另一篇笔记里。")
        case .notion: String(localized: "每本书一页，放在这一页下面；再发一次只补上新的划线。")
        case .flomo: String(localized: "每条划线一条 memo，带 #语阅 和书名；再发一次只发新的。")
        case .xinzhi: String(localized: "每条划线一条笔记，带标签 语阅 和书名；再发一次只发新的。")
        }
    }
}

// MARK: Connecting

/// Obsidian: a folder, the vault or one in it. Picking it is connecting.
private struct ObsidianSetup: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: String(localized: "连接 Obsidian"), detail: String(localized: "选择笔记库中的文件夹")) { ExportTile(target: .obsidian, size: 28) }
            PanelCard {
                // Outlined on the Mac: what return takes. iPhone has no return to take it.
                PanelButton(systemImage: "folder.badge.plus", tint: YYColor.t1, outlined: PanelScale.k == 1) {
                    Text("选一个文件夹…")
                } action: { Self.pick() }
            }
            PanelHint(String(localized: "选 Obsidian 库，或库里的一个文件夹。每本书写成一篇 Markdown，文件名就是书名，带作者和标签。"))
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
    }

    static func pick() {
        #if os(iOS)
        NotesFocus.shared.pickingFolder = true
        #else
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.prompt = String(localized: "用这个文件夹")
        panel.message = String(localized: "选 Obsidian 库，或库里的一个文件夹")
        guard panel.runModal() == .OK, let url = panel.url else { return }
        NoteExport.setObsidianFolder(url)
        NotesFocus.shared.tick &+= 1
        #endif
    }
}

/// Notion: an integration's token and the page the books go under. Both look right → ask Notion.
private struct NotionSetup: View {
    @State private var token = ""
    @State private var page = ""
    @State private var opened = false
    @State private var checks = ConnectChecks()
    @State private var tokenBad: String?
    @State private var pageBad: String?

    var body: some View {
        let clip = Clip.text
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: String(localized: "连接 Notion"), detail: String(localized: "填好后自动验证连接")) { ExportTile(target: .notion, size: 28) }
            PanelCard {
                StepRow(n: 1, done: opened || !token.isEmpty, title: String(localized: "建一个集成，拷它的令牌"),
                        detail: String(localized: "notion.so/my-integrations › 新建，名字叫“语阅”"), link: URL(string: "https://www.notion.so/my-integrations")) { opened = true }
                StepRow(n: 2, done: checks.done, title: String(localized: "把放书的那一页连上它"),
                        detail: String(localized: "那一页右上角 ··· › 连接 › 选“语阅”，再拷这一页的链接"))
            }
            if token.isEmpty, let clip, NoteExport.isNotionToken(clip) {
                ClipRow(text: String(localized: "用剪贴板里的 ntn_••••\(clip.suffix(4))")) { token = clip }
            } else if page.isEmpty, let clip, NoteExport.pageID(from: clip) != nil, clip.contains("notion") {
                ClipRow(text: String(localized: "用剪贴板里的页面链接")) { page = clip }
            }
            PanelCard {
                PanelField(label: String(localized: "令牌"), text: $token, prompt: "ntn_…", secure: true, bad: tokenBad != nil)
                PanelField(label: String(localized: "放书的页"), text: $page, prompt: "notion.so/…", bad: pageBad != nil)
            }
            if let why = tokenBad ?? pageBad { FieldError(text: why) }
            checks.view
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .task(id: token + "\u{1}" + page) { await tryConnect() }
    }

    private func tryConnect() async {
        let tok = token.trimmingCharacters(in: .whitespacesAndNewlines)
        tokenBad = !tok.isEmpty && !NoteExport.isNotionToken(tok) && tok.count > 6 ? String(localized: "令牌应该以 ntn_ 开头") : nil
        pageBad = page.count > 8 && NoteExport.pageID(from: page) == nil ? String(localized: "这不像一个 Notion 页面链接") : nil
        guard NoteExport.isNotionToken(tok), let id = NoteExport.pageID(from: page) else { checks = ConnectChecks(); return }
        // Typing still: wait for a pause.
        try? await Task.sleep(for: .milliseconds(600))
        guard !Task.isCancelled else { return }
        checks = ConnectChecks(next: String(localized: "正在验证令牌…"))
        do {
            let found = try await NoteExport.checkNotion(token: tok, page: id)
            guard !Task.isCancelled else { return }
            checks.items = [String(localized: "令牌有效 · 集成“\(found.bot)”"), String(localized: "找到那一页：\(found.title)")]
            checks.next = nil
            checks.done = true
            // The green line stays a moment before the panel turns into the connected one.
            try? await Task.sleep(for: .milliseconds(900))
            Keychain.write(NoteExport.notionKey, tok)
            UserDefaults.standard.set(found.title, forKey: NoteExport.notionTitleKey)
            UserDefaults.standard.set(page.trimmingCharacters(in: .whitespacesAndNewlines), forKey: NoteExport.notionPageKey)
            NotesFocus.shared.tick &+= 1
        } catch {
            checks = ConnectChecks(failure: NoteSender.message(error))
        }
    }
}

/// flomo: its API link. Only its shape is checked: flomo has no way to try a memo and take it
/// back, and a test memo would land in the reader's flomo. The first send is the real test.
private struct FlomoSetup: View {
    @State private var link = ""
    @State private var opened = false
    @State private var checks = ConnectChecks()
    @State private var bad: String?

    var body: some View {
        let clip = Clip.text
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: String(localized: "连接 flomo"), detail: String(localized: "贴一条链接")) { ExportTile(target: .flomo, size: 28) }
            PanelCard {
                StepRow(n: 1, done: opened || !link.isEmpty, title: String(localized: "打开 flomo 的 API 页"),
                        detail: String(localized: "flomo › 设置 › API（要 PRO），拷那条链接"),
                        link: URL(string: "https://v.flomoapp.com/mine?source=incoming_webhook")) { opened = true }
                StepRow(n: 2, done: checks.done, title: String(localized: "贴到下面"), detail: ExportWords.kept)
            }
            if link.isEmpty, let clip, NoteExport.isFlomoLink(clip) {
                ClipRow(text: String(localized: "用剪贴板里的 flomoapp.com/iwh/••••")) { link = clip }
            }
            PanelCard {
                PanelField(label: String(localized: "API 链接"), text: $link, prompt: "https://flomoapp.com/iwh/…", secure: true, bad: bad != nil)
            }
            if let bad { FieldError(text: bad) }
            checks.view
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .task(id: link) {
            let l = link.trimmingCharacters(in: .whitespacesAndNewlines)
            bad = l.count > 12 && !NoteExport.isFlomoLink(l) ? String(localized: "链接要以 https://flomoapp.com/iwh/ 开头") : nil
            guard NoteExport.isFlomoLink(l) else { checks = ConnectChecks(); return }
            try? await Task.sleep(for: .milliseconds(500))
            guard !Task.isCancelled else { return }
            checks = ConnectChecks(items: [String(localized: "链接的样子对")], done: true,
                                   banner: String(localized: "存好了 · 第一次发送时才真正试"))
            Keychain.write(NoteExport.flomoKey, l)
            try? await Task.sleep(for: .milliseconds(1100))
            NotesFocus.shared.tick &+= 1
        }
    }
}

/// 新枝: a CLI AccessToken. GET `/cli/user/me` checks it without writing a memo.
private struct XinzhiSetup: View {
    @State private var token = ""
    @State private var opened = false
    @State private var checks = ConnectChecks()
    @State private var bad: String?

    var body: some View {
        let clip = Clip.text
        VStack(alignment: .leading, spacing: 10) {
            PanelHeader(title: String(localized: "连接新枝"), detail: String(localized: "贴一条令牌")) { ExportTile(target: .xinzhi, size: 28) }
            PanelCard {
                StepRow(n: 1, done: opened || !token.isEmpty, title: String(localized: "拷 CLI AccessToken"),
                        detail: String(localized: "新枝网页 › 设置 › AI Agent › CLI AccessToken"),
                        link: URL(string: "https://www.xinzhi.zone")) { opened = true }
                StepRow(n: 2, done: checks.done, title: String(localized: "贴到下面"), detail: ExportWords.kept)
            }
            if token.isEmpty, let clip, NoteExport.isXinzhiToken(clip) {
                ClipRow(text: String(localized: "用剪贴板里的令牌 ••••\(clip.suffix(4))")) { token = clip }
            }
            PanelCard {
                PanelField(label: String(localized: "令牌"), text: $token, prompt: "CLI AccessToken", secure: true, bad: bad != nil)
            }
            if let bad { FieldError(text: bad) }
            checks.view
        }
        .padding(.horizontal, 16)
        .padding(.bottom, 14)
        .task(id: token) { await tryConnect() }
    }

    private func tryConnect() async {
        let tok = token.trimmingCharacters(in: .whitespacesAndNewlines)
        bad = !tok.isEmpty && !NoteExport.isXinzhiToken(tok) && tok.count > 4 ? String(localized: "这不像一个新枝令牌") : nil
        guard NoteExport.isXinzhiToken(tok) else { checks = ConnectChecks(); return }
        try? await Task.sleep(for: .milliseconds(600))
        guard !Task.isCancelled else { return }
        checks = ConnectChecks(next: String(localized: "正在验证令牌…"))
        do {
            let name = try await NoteExport.checkXinzhi(token: tok)
            guard !Task.isCancelled else { return }
            checks.items = [String(localized: "令牌有效 · \(name)")]
            checks.next = nil
            checks.done = true
            try? await Task.sleep(for: .milliseconds(900))
            Keychain.write(NoteExport.xinzhiKey, tok)
            UserDefaults.standard.set(name, forKey: NoteExport.xinzhiNameKey)
            NotesFocus.shared.tick &+= 1
        } catch {
            checks = ConnectChecks(failure: NoteSender.message(error))
        }
    }
}

// MARK: Pieces

/// What the connecting found so far: a tick for each thing that worked, a spinner for the one
/// being asked, then a green line; or a red line saying what's wrong.
private struct ConnectChecks {
    var items: [String] = []
    var next: String?
    var done = false
    var banner: String?
    var failure: String?

    @ViewBuilder var view: some View {
        if !items.isEmpty || next != nil || failure != nil {
            VStack(alignment: .leading, spacing: 7) {
                ForEach(items, id: \.self) { item in
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: "checkmark.circle.fill").foregroundStyle(YYColor.ok)
                        Text(item).foregroundStyle(YYColor.t1)
                    }
                    .transition(.opacity.combined(with: .offset(y: -3)))
                }
                if let next {
                    HStack(spacing: 8) {
                        ProgressView().controlSize(.mini)
                        Text(next).foregroundStyle(YYColor.t3)
                    }
                    .transition(.opacity)
                }
                if let failure {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Image(systemName: "exclamationmark.circle.fill").foregroundStyle(YYColor.danger)
                        Text(failure).foregroundStyle(YYColor.danger).fixedSize(horizontal: false, vertical: true)
                    }
                    .transition(.opacity)
                }
                if done {
                    HStack(spacing: 7) {
                        Image(systemName: "checkmark")
                        Text(banner ?? String(localized: "已连上"))
                    }
                    .font(.yy(size: PanelScale.k * 12, weight: .medium))
                    .foregroundStyle(YYColor.ok)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .background(YYColor.ok.opacity(0.12), in: .rect(cornerRadius: 8))
                    .transition(.opacity)
                }
            }
            .font(.yy(size: PanelScale.k * 12))
            .padding(12)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.card, in: .rect(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(YYColor.line, lineWidth: 0.5))
            .animation(YYMotion.standard, value: items)
            .animation(YYMotion.standard, value: next)
            .animation(YYMotion.standard, value: failure)
            .animation(YYMotion.standard, value: done)
        }
    }
}

/// A numbered step; with a link, the whole row opens it.
private struct StepRow: View {
    let n: Int
    let done: Bool
    let title: String
    var detail: String?
    var link: URL?
    var opened: () -> Void = {}
    @State private var hovering = false

    var body: some View {
        let row = HStack(alignment: .top, spacing: 10) {
            ZStack {
                Circle().fill(done ? YYColor.ok : YYColor.inset)
                if done {
                    Image(systemName: "checkmark").font(.yy(size: PanelScale.k * 8.5, weight: .bold)).foregroundStyle(.white)
                } else {
                    Text("\(n)").font(.yy(size: PanelScale.k * 10.5, weight: .semibold)).foregroundStyle(YYColor.t2)
                }
            }
            .frame(width: 18, height: 18)
            .animation(YYMotion.quick, value: done)
            VStack(alignment: .leading, spacing: 2) {
                Text(title).font(.yy(size: PanelScale.k * 12.5)).foregroundStyle(YYColor.t1)
                if let detail { Text(detail).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true) }
            }
            Spacer(minLength: 4)
            if link != nil {
                Image(systemName: "arrow.up.forward").font(.yy(size: PanelScale.k * 10, weight: .semibold)).foregroundStyle(YYColor.t3).padding(.top, 2)
            }
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 9)
        .frame(maxWidth: .infinity, alignment: .leading)
        if let link {
            Button {
                NotesFocus.open(link)
                opened()
            } label: {
                row.background(hovering ? YYColor.t1.opacity(0.045) : .clear, in: .rect(cornerRadius: 8)).contentShape(.rect)
            }
            .buttonStyle(.plain)
            .onHover { hovering = $0 }
        } else {
            row
        }
    }
}

private struct FieldError: View {
    let text: String
    var body: some View {
        Text(text).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.danger).padding(.horizontal, 12).padding(.top, -4)
    }
}

/// 用剪贴板里的 …: one click fills it in.
private struct ClipRow: View {
    let text: String
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: "doc.on.clipboard").font(.yy(size: PanelScale.k * 12, weight: .medium))
                Text(text).font(.yy(size: PanelScale.k * 12)).lineLimit(1)
                Spacer(minLength: 4)
                Image(systemName: "chevron.right").font(.yy(size: PanelScale.k * 9.5, weight: .semibold))
            }
            .foregroundStyle(YYColor.t1)
            .padding(.horizontal, 12)
            .padding(.vertical, 9)
            .background(YYColor.t1.opacity(0.06), in: .rect(cornerRadius: 11))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }
}

/// What's on the clipboard, if it's words.
private enum Clip {
    /// iPhone: nil. Reading the clipboard there asks the reader first, every time.
    static var text: String? {
        #if os(macOS)
        NSPasteboard.general.string(forType: .string)?.trimmingCharacters(in: .whitespacesAndNewlines)
        #else
        nil
        #endif
    }
}

// MARK: - 样张

/// 发到 X 是这样: two highlights and a thought from 《故乡》, as that target would show them.
private struct ExportSample: View {
    let target: NoteExport.Target
    private static let lines = ["我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。", "希望本是无所谓有，无所谓无的。"]
    private static let thought = "这句是全篇的落点"

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline) {
                Text("样张").font(.yy(size: PanelScale.k * 12.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                Spacer(minLength: 8)
                Text("发到 \(target.title) 是这样").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
            }
            Group {
                switch target {
                case .obsidian: obsidian
                case .notion: notion
                case .flomo: flomo
                case .xinzhi: xinzhi
                }
            }
            Text("用《故乡》的两条划线和一条想法排的").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
        }
    }

    private func card<C: View>(file: String, kind: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack {
                Text(file).font(.yy(size: PanelScale.k * 10.5, design: .monospaced)).foregroundStyle(YYColor.t3).lineLimit(1).truncationMode(.head)
                Spacer(minLength: 6)
                Text(kind).font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(YYColor.t3)
            }
            .padding(.bottom, 2)
            content()
        }
        .padding(12)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(YYColor.card, in: .rect(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private var obsidian: some View {
        let folder = NoteExport.obsidianFolder?.lastPathComponent ?? String(localized: "读书笔记")
        return card(file: "\(folder)/故乡.md", kind: "Markdown") {
            Text("故乡").font(.yy(size: PanelScale.k * 15, weight: .semibold)).foregroundStyle(YYColor.t1)
            Text("鲁迅 · 《呐喊》").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
            ForEach(Self.lines, id: \.self) { line in
                HStack(spacing: 8) {
                    RoundedRectangle(cornerRadius: 1).fill(Color(yy: YYRGBA(124, 58, 237, 0.5))).frame(width: 2.5)
                    Text(line).font(.custom("Songti SC", size: 12.5 * PanelScale.k)).foregroundStyle(YYColor.t1).fixedSize(horizontal: false, vertical: true)
                }
            }
            Text(Self.thought).font(.yy(size: PanelScale.k * 11.5)).foregroundStyle(YYColor.t2)
            HStack(spacing: 5) {
                ForEach(["#语阅", "#鲁迅"], id: \.self) {
                    Text($0).font(.yy(size: PanelScale.k * 10.5)).foregroundStyle(Color(yy: YYRGBA(124, 58, 237, 1)))
                        .padding(.horizontal, 6).padding(.vertical, 1.5)
                        .background(Color(yy: YYRGBA(124, 58, 237, 0.1)), in: .capsule)
                }
            }
        }
    }

    private var notion: some View {
        let parent = UserDefaults.standard.string(forKey: NoteExport.notionTitleKey).flatMap { $0.isEmpty ? nil : $0 } ?? String(localized: "读书笔记")
        return card(file: "\(parent) › 故乡", kind: "Notion") {
            Text("故乡").font(.yy(size: PanelScale.k * 15, weight: .bold)).foregroundStyle(YYColor.t1)
            Text("第一章").font(.yy(size: PanelScale.k * 12, weight: .semibold)).foregroundStyle(YYColor.t1)
            ForEach(Self.lines, id: \.self) { line in
                HStack(spacing: 8) {
                    Rectangle().fill(YYColor.t1).frame(width: 2)
                    Text(line).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1).fixedSize(horizontal: false, vertical: true)
                }
            }
            Text(Self.thought).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1)
        }
    }

    private var flomo: some View {
        VStack(spacing: 8) {
            ForEach(Array(Self.lines.enumerated()), id: \.offset) { i, line in
                VStack(alignment: .leading, spacing: 5) {
                    Text("2026-11-05 10:12").font(.yy(size: PanelScale.k * 10.5).monospacedDigit()).foregroundStyle(YYColor.t3)
                    Text(line).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1).fixedSize(horizontal: false, vertical: true)
                    if i == 1 { Text(Self.thought).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1) }
                    Text("#语阅 #故乡").font(.yy(size: PanelScale.k * 11)).foregroundStyle(Color(yy: YYRGBA(48, 207, 121, 1)))
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYColor.card, in: .rect(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: 0.5))
            }
        }
    }

    private var xinzhi: some View {
        VStack(spacing: 8) {
            ForEach(Array(Self.lines.enumerated()), id: \.offset) { i, line in
                VStack(alignment: .leading, spacing: 5) {
                    Text(line).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1).fixedSize(horizontal: false, vertical: true)
                    if i == 1 { Text(Self.thought).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t2) }
                    Text(verbatim: "——《故乡》 鲁迅").font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3)
                    Text(verbatim: "语阅/故乡").font(.yy(size: PanelScale.k * 11)).foregroundStyle(Color(yy: YYRGBA(46, 125, 82, 1)))
                }
                .padding(12)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYColor.card, in: .rect(cornerRadius: 10))
                .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(YYColor.line, lineWidth: 0.5))
            }
        }
    }
}
