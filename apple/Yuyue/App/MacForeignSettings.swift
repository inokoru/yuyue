#if os(macOS)
import AppKit
import SwiftUI
import Translation
import UniformTypeIdentifiers
import YuyueCore

/// 设置 · 外文: looking a word up, the 生词 marks, and the system's
/// offline translation, which can be downloaded from here before the first 译.
struct ForeignSettings: View {
    let library: Library
    @AppStorage(ForeignPrefs.speakKey) private var speak = true
    @AppStorage(ForeignPrefs.accentKey) private var accent = ForeignPrefs.Accent.british
    @AppStorage(ForeignPrefs.markKey) private var mark = true
    @AppStorage(ForeignPrefs.glossKey) private var gloss = true
    @AppStorage(ForeignPrefs.levelKey) private var level = 3
    @AppStorage(ForeignPrefs.glossVocabKey) private var glossVocab = false
    @State private var status: LanguageAvailability.Status?
    @State private var download: TranslationSession.Configuration?
    @State private var failed: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            SettingsHeading(String(localized: "查词"))
            SettingsCard {
                SettingsRow(String(localized: "系统词典"), detail: String(localized: "导入词典未命中时使用；启用项与顺序可在系统「词典」中设置")) {
                    Button("打开词典") { openDictionary() }
                }
                SettingsRow(String(localized: "查词时自动发音")) { SettingsSwitch(isOn: $speak) }
                SettingsRow(String(localized: "英文发音")) {
                    YYSegments(items: [(ForeignPrefs.Accent.british, String(localized: "英音")), (.american, String(localized: "美音"))], selection: $accent, small: true)
                        .frame(width: 120)
                }
            }
            SettingsHeading(String(localized: "MDict 词典"))
            MDictList(core: library.core)
            SettingsHeading(String(localized: "难词注释"))
            SettingsCard {
                SettingsRow(String(localized: "难词行间释义"), detail: String(localized: "阅读英文书时，在较难词汇上方标注简短中文词义")) {
                    SettingsSwitch(isOn: $gloss)
                }
                SettingsRow(String(localized: "词汇难度"), detail: String(localized: "等级越高，仅标注更生僻的词")) {
                    YYSegments(items: ForeignPrefs.levels.map { (Int($0.level), $0.name) }, selection: $level, small: true)
                        .frame(width: 260)
                }
                .disabled(!gloss || glossVocab)
                SettingsRow(String(localized: "仅标注生词本中的词")) { SettingsSwitch(isOn: $glossVocab) }
                    .disabled(!gloss)
            }
            .onChange(of: "\(gloss)\(level)\(glossVocab)") { NotificationCenter.default.post(name: .vocabChanged, object: nil) }
            SettingsHeading(String(localized: "生词"))
            SettingsCard {
                SettingsRow(String(localized: "在书中标记生词"), detail: String(localized: "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记")) {
                    SettingsSwitch(isOn: $mark)
                }
            }
            .onChange(of: mark) { NotificationCenter.default.post(name: .vocabChanged, object: nil) }
            SettingsHeading(String(localized: "翻译"))
            SettingsCard {
                SettingsRow(String(localized: "系统离线翻译"), detail: String(localized: "本地离线运行，原文不上传；供整页译文与双击整句翻译使用")) {
                    translationState
                }
            }
            if let failed {
                Label(failed, systemImage: "exclamationmark.circle")
                    .font(.yy(size: 11)).foregroundStyle(YYColor.danger)
                    .padding(.leading, 16)
            }
            Spacer(minLength: 0)
        }
        .task { await check() }
        .translationTask(download) { session in
            // Only ever used from here.
            nonisolated(unsafe) let session = session
            do {
                try await session.prepareTranslation()
                failed = nil
            } catch {
                failed = Translator.message(for: error)
            }
            download = nil
            await check()
        }
    }

    @ViewBuilder private var translationState: some View {
        switch status {
        case .installed?:
            Label("英文 → 中文 已下载", systemImage: "checkmark")
                .font(.yy(size: 12)).foregroundStyle(YYColor.t2)
        case .supported?:
            Button("下载英文 → 中文") {
                download = .init(source: Locale.Language(identifier: "en"), target: Translator.target)
            }
            .disabled(download != nil)
        case .unsupported?:
            Text("当前系统不支持").font(.yy(size: 12)).foregroundStyle(YYColor.t3)
        default:
            ProgressView().controlSize(.small)
        }
    }

    private func check() async {
        status = await LanguageAvailability().status(from: Locale.Language(identifier: "en"), to: Translator.target)
    }

    private func openDictionary() {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.apple.Dictionary") {
            NSWorkspace.shared.openApplication(at: url, configuration: .init())
        }
    }
}
/// 导入 MDict 词典: the ones imported, looked up first and in this order; 导入 reads a `.mdx`
/// once into the library (a big one takes a minute).
private struct MDictList: View {
    @State private var confirming: YYConfirm?
    let core: YuyueCore
    @State private var list: [DictionaryInfo] = []
    @State private var picking = false
    @State private var importing: String?
    @State private var failed: String?

    var body: some View {
        SettingsCard {
            ForEach(list, id: \.id) { d in
                SettingsRow(d.title, detail: String(localized: "\(d.entries) 个词条").replacingOccurrences(of: String(d.entries), with: d.entries.formatted())) {
                    SettingsSwitch(isOn: Binding(get: { d.enabled }, set: { on in
                        try? core.setDictionary(id: d.id, enabled: on)
                        reload()
                    }))
                }
                .monospacedDigit()
                .contextMenu {
                    if list.count > 1 {
                        Button("往上挪") { move(d, by: -1) }.disabled(list.first?.id == d.id)
                        Button("往下挪") { move(d, by: 1) }.disabled(list.last?.id == d.id)
                        Divider()
                    }
                    Button("移除…", role: .destructive) {
                        confirming = YYConfirm(title: String(localized: "移除“\(d.title)”？"), message: String(localized: "词典文件会从语阅里删掉，要用得再导入一次。"), button: String(localized: "移除")) { try? core.removeDictionary(id: d.id); reload() }
                    }
                }
            }
            SettingsRow(importing.map { String(localized: "正在导入 \($0)…") } ?? String(localized: "导入词典（.mdx）"),
                        detail: failed ?? String(localized: "按列表顺序优先查词，右键可调整先后或移除")) {
                if importing != nil {
                    ProgressView().controlSize(.small)
                } else {
                    Button("选择文件…") { picking = true }
                }
            }
        }
        .onAppear(perform: reload)
        .fileImporter(isPresented: $picking, allowedContentTypes: [UTType(filenameExtension: "mdx") ?? .data], allowsMultipleSelection: true) { result in
            guard case .success(let urls) = result else { return }
            Task { await add(urls) }
        }
        .yyConfirm($confirming)
    }

    private func reload() { list = (try? core.dictionaries()) ?? [] }

    /// 往上挪 / 往下挪 — a word is looked up in this order.
    private func move(_ d: DictionaryInfo, by step: Int) {
        var ids = list.map(\.id)
        guard let i = ids.firstIndex(of: d.id), ids.indices.contains(i + step) else { return }
        ids.swapAt(i, i + step)
        try? core.orderDictionaries(ids: ids)
        withAnimation(YYMotion.layout) { reload() }
    }


    private func add(_ urls: [URL]) async {
        failed = nil
        for url in urls {
            importing = url.deletingPathExtension().lastPathComponent
            let scoped = url.startAccessingSecurityScopedResource()
            defer { if scoped { url.stopAccessingSecurityScopedResource() } }
            do {
                try await Self.importDictionary(core, path: url.path(percentEncoded: false))
            } catch {
                failed = Library.message(for: error)
            }
            reload()
        }
        importing = nil
    }

    @concurrent nonisolated private static func importDictionary(_ core: YuyueCore, path: String) async throws {
        _ = try core.importDictionary(path: path)
    }
}
#endif
