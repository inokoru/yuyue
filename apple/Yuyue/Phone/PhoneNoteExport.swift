#if os(iOS)
import SwiftUI

/// The grey line under a settings page's title, as the Mac's page header has it.
struct PhoneSettingsLead: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(YYUI.font(10.5)).foregroundStyle(YYColor.t2)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.horizontal, YYUI.s(4))
            .padding(.top, YYUI.s(10))
    }
}

/// The small name over a card, as the Mac's settings have it.
struct PhoneSettingsHeading: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(YYUI.font(10.5, .semibold)).foregroundStyle(YYColor.t2)
            .padding(.horizontal, YYUI.s(4))
            .padding(.top, YYUI.s(16))
            .padding(.bottom, YYUI.s(6))
    }
}

/// 笔记导出 under 我的, as the Mac's: one list, Obsidian, Notion, flomo, 新枝, each with its
/// logo, where it writes and a switch: on, it shows in 笔记 › 导出 › 发到. A target opens its
/// page: the steps and the fields (tried as soon as they look right), or, connected, where it
/// writes and 断开; then a sample of a book there.
struct PhoneNoteExport: View {
    @State private var opening: NoteExport.Target?
    /// DEBUG: opens that target's page on appearing.
    var debugTarget: NoteExport.Target?

    init(debugTarget: NoteExport.Target? = nil) { self.debugTarget = debugTarget }
    private var focus: NotesFocus { .shared }

    var body: some View {
        let _ = focus.tick
        YYPage {
            PhoneSettingsLead(String(localized: "将划线与想法发送到常用笔记软件"))
            PhoneSettingsHeading(String(localized: "导出目标"))
            YYGroup {
                ForEach(NoteExport.Target.allCases) { row($0) }
            }
            YYNote(String(localized: "开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅保存在本机钥匙串，不同步、不上传。"))
        }
        .navigationTitle("笔记导出")
        .navigationBarTitleDisplayMode(.inline)
        .navigationDestination(item: $opening) { PhoneExportTarget(target: $0) }
        .onAppear { if let debugTarget, opening == nil { opening = debugTarget } }
        .onReceive(NotificationCenter.default.publisher(for: UserDefaults.didChangeNotification).receive(on: RunLoop.main)) { _ in
            focus.tick &+= 1
        }
    }

    /// The row opens the target; the switch beside it is its own control. Not connected yet,
    /// the switch opens it instead.
    private func row(_ t: NoteExport.Target) -> some View {
        let ready = NoteExport.isSetUp(t)
        return HStack(spacing: YYUI.s(10)) {
            Button { opening = t } label: {
                HStack(spacing: YYUI.s(10)) {
                    ExportTile(target: t, size: YYUI.s(26))
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text(t.title).font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text(ExportWords.where_(t)).font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .lineLimit(1).truncationMode(.middle)
                    }
                    Spacer(minLength: YYUI.s(8))
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            YYSwitch(isOn: Binding(get: { ready && NoteExport.isOn(t) }, set: { on in
                guard ready else { opening = t; return }
                NoteExport.setOn(t, on)
                focus.tick &+= 1
            }), label: t.title)
        }
        .padding(.vertical, YYUI.s(8))
    }
}

/// One target's page: what the Mac shows in the right column for it.
private struct PhoneExportTarget: View {
    let target: NoteExport.Target

    var body: some View {
        @Bindable var focus = NotesFocus.shared
        ScrollView {
            NotesInspector(target: target)
                .padding(.top, YYUI.s(10))
        }
        .scrollBounceBehavior(.basedOnSize)
        .background(YYColor.bg)
        .navigationBarTitleDisplayMode(.inline)
        .fileImporter(isPresented: $focus.pickingFolder, allowedContentTypes: [.folder]) { result in
            guard case .success(let url) = result else { return }
            NoteExport.setObsidianFolder(url)
            focus.tick &+= 1
        }
    }
}
#endif
