import SwiftUI
#if os(macOS)
import AppKit
#endif

/// What 设置 · 书伴 shows, the same on Mac and iPhone (the provider is
/// a menu and the model its own row): the service answering, its model with the ones it lists,
/// and 测试一下.
@MainActor @Observable
final class CompanionChoice {
    private(set) var current = CompanionConfig.current
    private(set) var added = CompanionProvider.added
    private(set) var models: [String] = []
    private(set) var listing = false
    private(set) var testing = false
    var result: Result<TimeInterval, Error>?

    /// What the 服务商 menu offers: this device's own where it can answer, then those added.
    var options: [CompanionProvider] { (Companion.appleAvailability == nil ? [.apple] : []) + added }

    func reload() {
        current = CompanionConfig.current
        added = CompanionProvider.added
        result = nil
        Task { await list() }
    }

    func choose(_ p: CompanionProvider) {
        CompanionConfig.current = CompanionConfig.saved(p)
        reload()
    }

    var model: String {
        get { current.model }
        set {
            current.model = newValue.trimmingCharacters(in: .whitespaces)
            current.save()
            result = nil
        }
    }

    /// The models the service lists; the first is taken when none was chosen yet.
    func list() async {
        let config = current
        guard config.provider.kind != .apple else { models = []; return }
        let cached = ModelNames.cached(config.provider.id)
        if !cached.isEmpty { models = cached }
        guard config.isConnected else { return }
        listing = true
        let found = (try? await Companion.models(config)) ?? []
        guard config.provider == current.provider else { return }
        if !found.isEmpty { models = found.sorted() }
        listing = false
        if current.model.isEmpty, let first = models.first { model = first }
    }

    func test() {
        testing = true
        result = nil
        let config = current
        Task {
            result = await CompanionTest.run(config)
            testing = false
        }
    }
}

/// 添加服务商, 管理服务商 and one service's form, in one sheet. 添加 is a picker: what's on
/// offer, searchable, grouped by how it's reached, with one of your own at the bottom; picking
/// one opens its form, ‹ goes back. A service added answers from then on.
struct CompanionProviderSheet: View {
    enum Start: Identifiable {
        case add, manage, edit(CompanionProvider)
        var id: String {
            switch self {
            case .add: "add"
            case .manage: "manage"
            case .edit(let p): p.id
            }
        }
    }

    let start: Start
    @State private var picked: CompanionProvider?
    @State private var adding: Bool
    @State private var query = ""
    @Environment(\.dismiss) private var dismiss

    #if os(macOS)
    private static let columns = 3
    #else
    private static let columns = 2
    #endif

    init(start: Start) {
        self.start = start
        switch start {
        case .add:
            _adding = State(initialValue: true)
            _picked = State(initialValue: nil)
        case .manage:
            _adding = State(initialValue: false)
            _picked = State(initialValue: nil)
        case .edit(let p):
            _adding = State(initialValue: false)
            _picked = State(initialValue: p)
        }
    }

    var body: some View {
        Group {
            if let picked {
                MacProviderForm(provider: picked, adding: adding, back: back, close: { dismiss() }, done: done)
                    .id(picked.id)
                    .transition(YYMotion.swap)
            } else if adding {
                picker.transition(YYMotion.swap)
            } else {
                manage.transition(YYMotion.swap)
            }
        }
        .animation(YYMotion.standard, value: picked?.id)
        .animation(YYMotion.standard, value: adding)
        .padding(.horizontal, YYUI.s(16))
        .padding(.bottom, YYUI.s(16))
        .padding(.top, YYUI.sheetGrabberTop)
        #if os(macOS)
        .frame(width: 480, height: 468)
        #else
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .presentationDetents([.large])
        .presentationDragIndicator(.visible)
        .presentationBackground(YYColor.bg)
        #endif
        .background(YYColor.bg)
    }

    /// After 添加, 保存 or 移除: back to 管理服务商 when that's where it started.
    private func done() {
        if case .manage = start {
            picked = nil
            adding = false
        } else {
            dismiss()
        }
    }

    /// ‹ from a form: to the picker it came from, or to 管理服务商.
    private var back: (() -> Void)? {
        switch start {
        case .edit: nil
        case .add: { picked = nil }
        case .manage: { picked = nil }
        }
    }

    // MARK: 添加服务商

    private var picker: some View {
        let matches = CompanionProvider.available(matching: query)
        return VStack(alignment: .leading, spacing: 0) {
            SheetTitle(title: String(localized: "添加服务商"), back: start.id == "manage" ? { adding = false } : nil) { dismiss() }
            HStack(spacing: YYUI.s(6)) {
                Image(systemName: "magnifyingglass").font(YYUI.font(10.5, .medium)).foregroundStyle(YYColor.t3)
                TextField("", text: $query, prompt: Text("搜索服务商，比如 千问、Kimi、magpie"))
                    .textFieldStyle(.plain)
                    .font(YYUI.font(11.5))
                    .plainEntry()
                    .onSubmit { if let first = matches.first { open(first) } }
            }
            .padding(.horizontal, YYUI.s(9))
            .frame(height: YYUI.s(30))
            .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(8)))
            .padding(.top, YYUI.s(10))
            ScrollView {
                VStack(alignment: .leading, spacing: YYUI.s(10)) {
                    ForEach(CompanionProvider.Group.allCases, id: \.self) { group in
                        let list = matches.filter { $0.group == group }
                        if !list.isEmpty {
                            VStack(alignment: .leading, spacing: YYUI.s(2)) {
                                Text(group.title).font(YYUI.font(10, .semibold)).foregroundStyle(YYColor.t3)
                                    .padding(.leading, YYUI.s(8)).padding(.bottom, YYUI.s(2))
                                LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: YYUI.s(2)), count: Self.columns), spacing: YYUI.s(2)) {
                                    ForEach(list) { tile($0) }
                                }
                            }
                        }
                    }
                    if matches.isEmpty {
                        Text("没找到。在下面自己添加，填上接口地址就行")
                            .font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                            .frame(maxWidth: .infinity).padding(.vertical, YYUI.s(36))
                    }
                }
                .padding(.vertical, YYUI.s(12))
            }
            .scrollBounceBehavior(.basedOnSize)
            Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, -YYUI.s(16))
            Button { open(.custom("")) } label: {
                HStack(spacing: YYUI.s(8)) {
                    Image(systemName: "plus").font(YYUI.font(10.5, .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(16), height: YYUI.s(16))
                    Text("自己添加").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    Text("任何 OpenAI 兼容接口").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
                    Spacer(minLength: 0)
                }
            }
            .buttonStyle(PickRow())
            .padding(.top, YYUI.s(8))
        }
    }

    private func open(_ p: CompanionProvider) {
        adding = true
        picked = p
    }

    private func tile(_ p: CompanionProvider) -> some View {
        Button { open(p) } label: {
            HStack(spacing: YYUI.s(8)) {
                ProviderLogo(provider: p)
                Text(p.title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(1)
                Spacer(minLength: 0)
            }
        }
        .buttonStyle(PickRow())
        .help(p.blurb.map(Text.init) ?? Text(p.title))
    }

    // MARK: 管理服务商

    private var manage: some View {
        let added = CompanionProvider.added
        return VStack(alignment: .leading, spacing: YYUI.s(10)) {
            SheetTitle(title: String(localized: "管理服务商"), back: nil) { dismiss() }
            if added.isEmpty {
                Text("还没添加服务商").font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                    .frame(maxWidth: .infinity).padding(.vertical, YYUI.s(30))
            } else {
                ScrollView {
                    VStack(spacing: YYUI.s(2)) {
                        ForEach(added) { p in
                            let saved = CompanionConfig.saved(p)
                            Button { picked = p } label: {
                                HStack(spacing: YYUI.s(8)) {
                                    ProviderLogo(provider: p)
                                    Text(p.title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                                    Spacer(minLength: YYUI.s(8))
                                    Text(saved.key.isEmpty ? (saved.url.isEmpty ? p.urlHint : saved.url) : saved.maskedKey)
                                        .font(YYUI.font(10, design: .monospaced)).foregroundStyle(YYColor.t3).lineLimit(1)
                                    Image(systemName: "chevron.right").font(YYUI.font(8.5, .semibold)).foregroundStyle(YYColor.t3)
                                }
                            }
                            .buttonStyle(PickRow())
                        }
                    }
                    .padding(.top, YYUI.s(4))
                }
                .scrollBounceBehavior(.basedOnSize)
            }
            Spacer(minLength: 0)
            HStack {
                Spacer()
                Button { adding = true } label: { Label("添加服务商", systemImage: "plus") }
                    .buttonStyle(YYButtonStyle(kind: .raised))
            }
        }
    }
}

/// A row to pick: no box, the inset grey under it on hover and press.
struct PickRow: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View { Row(configuration: configuration) }

    private struct Row: View {
        let configuration: Configuration
        @State private var hovering = false

        var body: some View {
            configuration.label
                .padding(.horizontal, YYUI.s(8))
                .frame(height: YYUI.s(32))
                .background(hovering || configuration.isPressed ? YYColor.inset : .clear, in: .rect(cornerRadius: YYUI.s(7)))
                .contentShape(.rect)
                .onHover { hovering = $0 }
        }
    }
}

/// The sheet's title, with ‹ when there's somewhere to go back to, and the service's logo in a form.
struct SheetTitle: View {
    let title: String
    var logo: CompanionProvider?
    let back: (() -> Void)?
    let close: () -> Void

    var body: some View {
        HStack(spacing: YYUI.s(6)) {
            if let back {
                Button(action: back) {
                    Image(systemName: "chevron.left").font(YYUI.font(11, .semibold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(20), height: YYUI.s(26))
                        .contentShape(Rectangle().inset(by: -8))
                }
                .buttonStyle(.plain)
                .accessibilityLabel(Text("返回"))
            }
            if let logo { ProviderLogo(provider: logo, size: 18) }
            YYSheetHeader(title: title, subtitle: String(localized: "书伴"), close: close)
        }
    }
}

/// A service's logo, bare as the menu bar draws its icons; the user's own get their first letter
/// on a small grey disc, the device's own the Apple logo.
struct ProviderLogo: View {
    let provider: CompanionProvider
    var size: CGFloat = 16

    var body: some View {
        Group {
            if let logo = provider.logo {
                Image(logo).resizable().scaledToFit().foregroundStyle(YYColor.t1)
            } else if provider.kind == .apple {
                Image(systemName: "apple.logo").resizable().scaledToFit().foregroundStyle(YYColor.t1).padding(YYUI.s(size * 0.06))
            } else {
                Text(String(provider.title.prefix(1)).uppercased())
                    .font(YYUI.font(size * 0.55, .semibold)).foregroundStyle(YYColor.t2)
                    .frame(width: YYUI.s(size), height: YYUI.s(size))
                    .background(YYColor.inset, in: .circle)
            }
        }
        .frame(width: YYUI.s(size), height: YYUI.s(size))
    }
}

private extension View {
    /// Keys, model names and addresses: typed as they are.
    func plainEntry() -> some View {
        self.autocorrectionDisabled()
            #if os(iOS)
            .textInputAutocapitalization(.never)
            #endif
    }
}
