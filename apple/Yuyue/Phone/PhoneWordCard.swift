#if os(iOS)
import SwiftUI

/// 点词 on the phone: a card along the bottom with the word, its sound and short meaning, the
/// phrase it was met in, where the meaning came from, 完整释义 and 加入生词本. A tap on the
/// page puts it away (ReaderModel.closeLookup).
struct PhoneWordCard: View {
    let model: ReaderModel
    let lookup: WordLookup
    @State private var full = false
    private let tint = YYTint.standard

    var body: some View {
        VStack(alignment: .leading, spacing: YYUI.s(6)) {
            HStack(alignment: .center, spacing: YYUI.s(8)) {
                HStack(alignment: .firstTextBaseline, spacing: YYUI.s(8)) {
                    Text(lookup.definition?.headword ?? lookup.word)
                        .font(.custom("Georgia", size: YYUI.s(20)).weight(.bold))
                        .foregroundStyle(YYColor.t1)
                    if let phonetic = lookup.definition?.phonetic {
                        Text(phonetic).font(YYUI.font(11)).foregroundStyle(YYColor.t2)
                    }
                }
                Spacer(minLength: YYUI.s(8))
                Button { model.say(lookup.word) } label: {
                    Image(systemName: "headphones").font(YYUI.font(12, .medium)).foregroundStyle(YYColor.t1)
                        .frame(width: YYUI.s(32), height: YYUI.s(32)).background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("再读一遍"))
                Button { model.closeLookup() } label: {
                    Image(systemName: "xmark").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(32), height: YYUI.s(32)).background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("关闭"))
            }
            meaning
            if full, let html = lookup.html {
                DictEntryView(html: html).frame(height: YYUI.s(260))
                    .transition(.opacity)
            }
            Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.top, YYUI.s(4))
            HStack(spacing: YYUI.s(8)) {
                Text(source).font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
                Spacer(minLength: YYUI.s(4))
                if lookup.html != nil {
                    Button(full ? String(localized: "收起释义") : String(localized: "完整释义")) {
                        withAnimation(YYMotion.layout) { full.toggle() }
                    }
                    .buttonStyle(YYButtonStyle(kind: .soft, size: .small))
                }
                Button { model.keep(!lookup.kept) } label: {
                    Label(lookup.kept ? String(localized: "在生词本里") : String(localized: "加入生词本"), systemImage: lookup.kept ? "checkmark" : "plus")
                }
                .buttonStyle(YYButtonStyle(kind: lookup.kept ? .soft : .main, size: .small))
            }
        }
        .padding(YYUI.s(16))
        .yyFloatCard(.rect(cornerRadius: YYUI.s(18)),
                     shadow: [YYShadowLayer(x: 0, y: 8, blur: 36, spread: 0, color: YYRGBA(0, 0, 0, 0.16))])
        .onChange(of: lookup.word) { full = false }
    }

    @ViewBuilder private var meaning: some View {
        if let d = lookup.definition {
            (Text(d.partOfSpeech.map { $0 + " " } ?? "").font(.custom("Georgia", size: YYUI.s(13))).italic().foregroundStyle(YYColor.t3)
                + Text(d.meaning).font(YYUI.font(13)).foregroundStyle(YYColor.t1))
                .fixedSize(horizontal: false, vertical: true)
        } else if let gloss = lookup.gloss {
            Text(gloss).font(YYUI.font(13)).foregroundStyle(YYColor.t1)
        } else {
            Text("未查到释义，可在「我的 › 外文」中导入 MDict 词典。")
                .font(YYUI.font(12)).foregroundStyle(YYColor.t2)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    private var source: String {
        let from = lookup.dictionary ?? (lookup.gloss != nil ? String(localized: "系统翻译") : String(localized: "词典"))
        guard let n = lookup.count, n > 0 else { return from }
        return n == 1
            ? String(localized: "\(from) · 本书只出现这一次")
            : String(localized: "\(from) · 本书出现 \(n) 次").replacingOccurrences(of: String(n), with: n.formatted())
    }
}

/// 双击一句 on the phone: the sentence and its translation, along the bottom.
struct PhoneSentenceCard: View {
    let model: ReaderModel
    let item: SentenceTranslation

    var body: some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack(alignment: .top, spacing: YYUI.s(8)) {
                Text(item.text)
                    .font(.custom("Georgia", size: YYUI.s(13))).foregroundStyle(YYColor.t2)
                    .lineSpacing(3).lineLimit(4)
                    .frame(maxWidth: .infinity, alignment: .leading)
                Button { model.sentenceTranslation = nil } label: {
                    Image(systemName: "xmark").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(30), height: YYUI.s(30)).background(YYColor.inset, in: .circle)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("关闭"))
            }
            if let t = item.translated {
                Text(t).font(YYUI.font(14)).foregroundStyle(YYColor.t1).lineSpacing(4)
                    .textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
            } else if let failed = item.failed {
                Label(failed, systemImage: "exclamationmark.circle")
                    .font(YYUI.font(12)).foregroundStyle(YYColor.danger)
            } else {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("正在翻译…").font(YYUI.font(12)).foregroundStyle(YYColor.t3)
                }
            }
            Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.top, 2)
            HStack {
                Text("系统翻译 · 在这台设备上").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                Spacer()
                if let t = item.translated {
                    Button(String(localized: "复制")) {
                        UIPasteboard.general.string = t
                    }
                    .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                }
            }
        }
        .padding(YYUI.s(16))
        .yyFloatCard(.rect(cornerRadius: YYUI.s(18)),
                     shadow: [YYShadowLayer(x: 0, y: 8, blur: 36, spread: 0, color: YYRGBA(0, 0, 0, 0.16))])
    }
}
#endif

#if os(iOS)
import UniformTypeIdentifiers
import YuyueCore

/// 我的 › 外文阅读 › MDict 词典: the ones imported, on or off, 导入 from Files.
struct PhoneMDictList: View {
    @State private var confirming: YYConfirm?
    let core: YuyueCore
    @State private var list: [DictionaryInfo] = []
    @State private var picking = false
    @State private var importing: String?
    @State private var failed: String?
    private let tint = YYTint.standard

    var body: some View {
        YYGroup {
            ForEach(list, id: \.id) { d in
                YYRow(title: d.title) {
                    Text(String(localized: "\(d.entries) 个词条").replacingOccurrences(of: String(d.entries), with: d.entries.formatted()))
                        .font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                    YYSwitch(isOn: Binding(get: { d.enabled }, set: { on in
                        try? core.setDictionary(id: d.id, enabled: on)
                        reload()
                    }), label: d.title)
                }
                .swipeActions { Button("移除", role: .destructive) { confirming = YYConfirm(title: String(localized: "移除“\(d.title)”？"), message: String(localized: "词典文件会从语阅里删掉，要用得再导入一次。"), button: String(localized: "移除")) { try? core.removeDictionary(id: d.id); reload() } } }
                .contextMenu {
                    if list.count > 1 {
                        Button("往上挪", systemImage: "arrow.up") { move(d, by: -1) }.disabled(list.first?.id == d.id)
                        Button("往下挪", systemImage: "arrow.down") { move(d, by: 1) }.disabled(list.last?.id == d.id)
                    }
                    Button("移除…", systemImage: "trash", role: .destructive) { confirming = YYConfirm(title: String(localized: "移除“\(d.title)”？"), message: String(localized: "词典文件会从语阅里删掉，要用得再导入一次。"), button: String(localized: "移除")) { try? core.removeDictionary(id: d.id); reload() } }
                }
            }
            Button { picking = true } label: {
                HStack(spacing: YYUI.s(6)) {
                    if importing != nil { ProgressView() } else { Image(systemName: "plus").font(YYUI.font(10.5, .semibold)) }
                    Text(importing.map { String(localized: "正在导入 \($0)…") } ?? String(localized: "导入词典（.mdx）"))
                        .font(YYUI.font(11.5, .semibold))
                    Spacer(minLength: 0)
                }
                .foregroundStyle(tint.accent)
                .padding(.vertical, YYUI.s(9))
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .disabled(importing != nil)
            if let failed {
                Text(failed).font(YYUI.font(10.5)).foregroundStyle(YYColor.danger).padding(.vertical, YYUI.s(6))
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
        let core = core
        for url in urls {
            guard !Task.isCancelled else { break }
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
