import SwiftUI
import YuyueCore

/// 人物声音: who has spoken so far in the book and
/// the voice each reads with. Everyone reads with the narrator's until given one; the narrator's
/// is the book's voice from the listen card. Spoilers stay out: only those up to this chapter.
struct CastSheet: View {
    let core: YuyueCore
    let bookID: String
    let section: UInt32
    let player: Player?
    let language: String
    /// Opened straight on someone (the Mac's cast panel).
    var start: [Route] = []
    let close: () -> Void

    enum Route: Hashable {
        case role(String)
        /// Choosing a voice for someone, or (nil) the narrator.
        case voice(String?)
        case clone(String?)
        case rerecord(id: String, name: String)
        case cloneDetail(String)
        /// 没把握的句子.
        case unsure
    }

    @State private var path: [Route] = []
    @State private var cast: [CastMember] = []
    @State private var roles: [Role] = []
    @State private var autoVoices: [String: CastRoleVoice] = [:]
    @State private var dramaEnabled = false
    @State private var loaded = false
    @State private var suggestions: [SameSuggestion] = []
    @State private var unsureCount = 0
    @State private var mergingFrom: String?

    private var rootRoute: Route? { start.first }
    private var activeRoute: Route? { path.last ?? rootRoute }

    var body: some View {
        NavigationStack(path: $path) {
            rootContent
                .navigationDestination(for: Route.self) { route in
                    destinationView(route, isRoot: false)
                }
        }
        .task {
            if start.count > 1 { path = Array(start.dropFirst()) }
            #if DEBUG
            // `-debugCastRoute narrator`: straight to 旁白的声音.
            if UserDefaults.standard.string(forKey: "debugCastRoute") == "narrator" { path = [.voice(nil)] }
            #endif
            await load()
        }
        .onReceive(NotificationCenter.default.publisher(for: CastContext.changed)) { _ in Task { await load() } }
        .onReceive(NotificationCenter.default.publisher(for: DramaWork.updated)) { n in
            if (n.object as? String) == bookID { Task { await load() } }
        }
        .onReceive(NotificationCenter.default.publisher(for: DramaSynth.settingsChanged)) { n in
            if n.object == nil || n.object as? String == bookID { Task { await load() } }
        }
        #if os(macOS)
        .frame(width: macSize.width, height: macSize.height)
        .fixedSize(horizontal: false, vertical: macSize.height == nil)
        #endif
    }

    @ViewBuilder private var rootContent: some View {
        if let rootRoute {
            destinationView(rootRoute, isRoot: true)
        } else {
            list
        }
    }

    @ViewBuilder private func destinationView(_ route: Route, isRoot: Bool) -> some View {
        switch route {
        case .role(let name): RoleView(context: context, name: name, path: $path, isRoot: isRoot, close: close)
        case .voice(let name): VoiceChoiceView(context: context, name: name, path: $path, isRoot: isRoot, close: close)
        case .clone(let name): CloneVoiceView(context: context, forName: name, path: $path)
        case let .rerecord(id, initialName):
            CloneVoiceView(
                core: core,
                dataDir: LiveVoiceModel.context?.dataDir ?? ClonedVoices.dir.deletingLastPathComponent(),
                language: language,
                existingID: id,
                initialName: initialName,
                previewSentence: CloneVoiceView.nextBookSentence(core: core, bookID: bookID, section: section, player: player, language: language),
                onSaved: { _ in if !path.isEmpty { path.removeLast() } },
                onClose: { if !path.isEmpty { path.removeLast() } }
            )
        case .cloneDetail(let id):
            ClonedCardDetailView(
                entryID: id,
                core: core,
                dataDir: LiveVoiceModel.context?.dataDir ?? ClonedVoices.dir.deletingLastPathComponent(),
                language: language
            )
        case .unsure:
            ScrollView { UnsureView(core: core, bookID: bookID, section: section, compact: true).padding(16) }
                .background(YYColor.bg)
                .yySheetPage(String(localized: "待确认的台词"), back: isRoot ? nil : { if !path.isEmpty { path.removeLast() } }, close: close)
        }
    }

    #if os(macOS)
    /// The cast list is 420 × 600; choosing a voice is the 486 × 476 panel; a clone page is a card
    /// as tall as what it holds.
    private var macSize: (width: CGFloat, height: CGFloat?) {
        switch activeRoute {
        case .voice?: (486, 476)
        case .clone?, .rerecord?, .cloneDetail?: (CloneCardPage<EmptyView>.macWidth, nil)
        // The cast list is as tall as it is, up to a scroll.
        case nil: (420, nil)
        default: (420, 600)
        }
    }
    #endif

    private var context: CastContext {
        CastContext(core: core, bookID: bookID, section: section, player: player, language: language, cast: cast, roles: roles, dramaEnabled: dramaEnabled, autoVoices: autoVoices)
    }

    private var list: some View {
        CastListPage {
            DramaStatusBlock(core: core, bookID: bookID, section: section) { path.append(.unsure) }
                .padding(.top, YYUI.s(12))
            if !suggestions.isEmpty {
                YYGroup {
                    ForEach(suggestions.prefix(2), id: \.b) { s in
                        SameSuggestionRow(core: core, bookID: bookID, suggestion: s) { n in (context.avatar(n), context.color(n)) }
                    }
                }
                .padding(.top, YYUI.s(10))
            }
            YYGroup {
                Button { path.append(.voice(nil)) } label: {
                    CastRow(avatar: nil, color: YYColor.t3, name: String(localized: "旁白"),
                            detail: String(localized: "没单独设声音的人物也用这个"),
                            voice: player?.currentVoice(for: language)?.name ?? String(localized: "没有声音"), own: true)
                }
                .buttonStyle(.plain)
            }
            .padding(.top, YYUI.s(10))
            let here = cast.filter { $0.linesHere > 0 }
            let before = cast.filter { $0.linesHere == 0 }
            if !here.isEmpty {
                YYLabel(String(localized: "这一章"), String(localized: "\(here.count) 人"))
                YYGroup { ForEach(here, id: \.name) { row($0) } }
            }
            if !before.isEmpty {
                YYLabel(String(localized: "前面出场过"), String(localized: "读到这里共 \(cast.count) 人"))
                YYGroup { ForEach(before, id: \.name) { row($0) } }
            }
            DramaListenGroup(core: core, bookID: bookID, section: section, language: language, narrator: player?.currentVoice(for: language)?.id)
                .padding(.top, YYUI.s(10))
            if loaded, cast.isEmpty {
                YYNote(String(localized: "读到这里还没认出谁在说话。对话多的书读几章后，这里会列出说过话的人。"))
            } else {
                YYNote(String(localized: "点一个人：声音、快慢高低、气泡放左边还是右边。改了从下一段起生效，跟着同步到你的其他设备。"))
            }
        }
        .yySheetPage(String(localized: "人物"), close: close)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .sheet(item: Binding(get: { mergingFrom.map(Named.init) }, set: { mergingFrom = $0?.name })) { m in
            PeoplePicker(core: core, bookID: bookID, section: section, title: String(localized: "\(m.name)合并到"), except: m.name, pick: { into in
                mergingFrom = nil
                CastRowActions(core: core, bookID: bookID, member: cast.first { $0.name == m.name }!).edit(.merge(into: into, from: m.name))
            }, close: { mergingFrom = nil })
            .presentationBackground(YYColor.bg)
        }
        #if os(iOS)
        .toolbar {
            ToolbarItem(placement: .cancellationAction) {
                Button(String(localized: "完成"), action: close)
            }
        }
        #endif
    }

    private func row(_ m: CastMember) -> some View {
        let role = context.role(m.name)
        let display = context.voiceLabel(for: m.name)
        var detail = ([m.aliases.prefix(2).joined(separator: String(localized: "、"))].filter { !$0.isEmpty } + [String(localized: "\(m.linesSoFar) 句")]).joined(separator: " · ")
        if role?.right == true { detail += " · " + String(localized: "右边") }
        return Button { path.append(.role(m.name)) } label: {
            CastRow(avatar: context.avatar(m.name), color: context.color(m.name), name: m.name, detail: detail,
                    voice: display.text, own: display.emphasized)
        }
        .buttonStyle(.plain)
        .contextMenu { castMenu(m) }
    }

    /// 合并、男女、不是一个人物: long-press (right-click) someone.
    @ViewBuilder private func castMenu(_ m: CastMember) -> some View {
        let act = CastRowActions(core: core, bookID: bookID, member: m)
        Button { path.append(.role(m.name)) } label: { Label(String(localized: "\(m.name)的人物页"), systemImage: "person.crop.circle") }
        if let other = m.maybeSame {
            Button { act.edit(.merge(into: other, from: m.name)) } label: { Label(String(localized: "合并到 \(other)"), systemImage: "arrow.triangle.merge") }
        }
        Button { mergingFrom = m.name } label: { Label(String(localized: "合并到别人…"), systemImage: "magnifyingglass") }
        Divider()
        Button { act.edit(.setGender(name: m.name, gender: .f)) } label: { Label(String(localized: "设为女性"), systemImage: m.gender == .f ? "checkmark" : "person") }
        Button { act.edit(.setGender(name: m.name, gender: .m)) } label: { Label(String(localized: "设为男性"), systemImage: m.gender == .m ? "checkmark" : "person") }
        Divider()
        Button(role: .destructive) { act.edit(.remove(name: m.name)) } label: { Label(String(localized: "并非人物（移除）"), systemImage: "nosign") }
    }

    private struct Named: Identifiable {
        let name: String
        var id: String { name }
    }

    private func load() async {
        let active = DramaSynth.isDramaActive(core, bookID)
        var (c, r, s, auto, unsureTotal) = await CastCore.loadSummary(core, bookID: bookID, section: section)
        if active, auto.isEmpty, let dir = Audiobooks.shared?.dataDir {
            _ = await LiveCastCache.shared.plan(
                core: core,
                dataDir: dir,
                book: bookID,
                language: language,
                narrator: player?.currentVoice(for: language)?.id
            )
            auto = await CastCore.loadAutoVoices(core, bookID: bookID)
        }
        dramaEnabled = active
        cast = c
        roles = r
        suggestions = s
        unsureCount = unsureTotal
        autoVoices = Dictionary(uniqueKeysWithValues: auto.map { ($0.name, $0) })
        loaded = true
    }
}

/// What the cast pages share.
struct CastContext {
    static let changed = Notification.Name("yuyue.castChanged")

    let core: YuyueCore
    let bookID: String
    let section: UInt32
    let player: Player?
    let language: String
    let cast: [CastMember]
    let roles: [Role]
    let dramaEnabled: Bool
    let autoVoices: [String: CastRoleVoice]

    func role(_ name: String) -> RoleVoice? { roles.first { $0.name == name }?.voice }

    func autoVoice(for name: String) -> (voice: any Voice, isFallback: Bool)? {
        guard dramaEnabled, let item = autoVoices[name], let v = Voices.find(item.voice, bookID: bookID) else { return nil }
        return (v, item.isDialogueFallback)
    }

    func voiceLabel(for name: String) -> (text: String, emphasized: Bool) {
        if let id = role(name)?.voice, let v = Voices.find(id, bookID: bookID) {
            return (v.name, true)
        }
        if let auto = autoVoice(for: name) {
            if !auto.isFallback {
                return (String(localized: "\(auto.voice.name) · 自动"), false)
            }
            return (String(localized: "自动 · 跟旁白"), false)
        }
        return (String(localized: "跟旁白"), false)
    }

    func set(_ name: String, _ voice: RoleVoice) {
        try? core.setBookRole(bookId: bookID, name: name, voice: voice)
        player?.rolesChanged()
        NotificationCenter.default.post(name: Self.changed, object: nil)
    }

    /// Their circle's character and colour, as core gives them (the bubbles use the same).
    func avatar(_ name: String) -> String {
        cast.first { $0.name == name }?.avatar ?? String(name.last ?? "?")
    }

    func color(_ name: String) -> Color {
        Color(hex: cast.first { $0.name == name }?.color ?? "#888888")
    }

    /// One of their own lines in this chapter, to try voices with; or a few plain words.
    func sample(_ name: String?) -> String {
        guard let name else { return Voices.sampleText(language) }
        if let bubbles = try? core.sectionBubbles(bookId: bookID, section: section),
           let sentences = try? core.sentences(bookId: bookID, section: section) {
            for line in bubbles {
                guard let who = line.who,
                      who == name || who.hasSuffix(name) || name.hasSuffix(who),
                      line.first == line.last,
                      sentences.indices.contains(Int(line.first)) else { continue }
                let s = Array(sentences[Int(line.first)])
                let a = min(Int(line.start), s.count), b = min(max(Int(line.end), a), s.count)
                var quote = String(s[a ..< b]).trimmingCharacters(in: .whitespacesAndNewlines)
                while quote.hasPrefix("—") || quote.hasPrefix("―") || quote.hasPrefix("–") {
                    quote = String(quote.dropFirst()).trimmingCharacters(in: .whitespacesAndNewlines)
                }
                if quote.count >= 2 { return quote }
            }
        }
        return language.hasPrefix("zh") ? "我是\(name)。" : "I am \(name)."
    }
}

private struct CastRow: View {
    let avatar: String?
    let color: Color
    let name: String
    let detail: String
    let voice: String
    let own: Bool

    var body: some View {
        HStack(spacing: YYUI.s(10)) {
            Group {
                if let avatar {
                    Text(avatar).font(YYUI.font(11.5, .semibold)).foregroundStyle(color)
                } else {
                    Image(systemName: "book").font(YYUI.font(11)).foregroundStyle(YYColor.t3)
                }
            }
            .frame(width: YYUI.s(26), height: YYUI.s(26))
            .background(avatar == nil ? YYColor.inset : color.opacity(0.18), in: .circle)
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: YYUI.s(8))
            HStack(spacing: 3) {
                Text(voice).font(YYUI.font(10, own ? .semibold : .regular)).foregroundStyle(own ? YYColor.t1 : YYColor.t3)
                Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
            }
        }
        .padding(.vertical, YYUI.s(9))
        .contentShape(.rect)
    }
}

/// One of the cast — voice, which side their bubbles go, pace, pitch — and one of their own
/// lines to try every change on.
private struct RoleView: View {
    let context: CastContext
    let name: String
    @Binding var path: [CastSheet.Route]
    var isRoot: Bool = false
    let close: () -> Void
    @State private var voice = RoleVoice(voice: nil, rate: 1, pitch: 0, right: false)

    var body: some View {
        YYPage {
            HStack(spacing: YYUI.s(10)) {
                Text(context.avatar(name)).font(YYUI.font(14, .semibold)).foregroundStyle(context.color(name))
                    .frame(width: YYUI.s(34), height: YYUI.s(34)).background(context.color(name).opacity(0.18), in: .circle)
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).font(YYUI.font(14, .semibold))
                    if let m = context.cast.first(where: { $0.name == name }) {
                        Text(String(localized: "\(m.linesSoFar) 句"))
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(1)
                    }
                }
            }
            .padding(.vertical, YYUI.s(12))
            RoleNamesSection(core: context.core, bookID: context.bookID, name: name)
                .padding(.bottom, YYUI.s(10))
            YYGroup {
                Button { path.append(.voice(name)) } label: {
                    YYRow(String(localized: "声音"), icon: "waveform", value: voiceName, chevron: true)
                }
                .buttonStyle(.plain)
                YYRow(title: String(localized: "气泡放在"), icon: "rectangle.split.2x1") {
                    YYSegments(items: [(false, String(localized: "左边")), (true, String(localized: "右边"))], selection: binding(\.right), small: true)
                        .fixedSize(horizontal: true, vertical: false)
                }
            }
            VStack(spacing: YYUI.s(20)) {
                slider(String(localized: "快慢"), value: "\(String(format: "%.1f", voice.rate))×", note: String(localized: "在全书速度上再快或慢一点"), range: 0.7 ... 1.4, key: \.rate)
                slider(String(localized: "高低"), value: voice.pitch == 0 ? "0" : String(format: "%+.0f", voice.pitch), note: String(localized: "声音高一点或低一点"), range: -6 ... 6, key: \.pitch, step: 1)
            }
            .padding(YYUI.s(14))
            .yyCard()
            .padding(.top, YYUI.s(12))
            Button(action: preview) {
                HStack(spacing: YYUI.s(10)) {
                    Image(systemName: "play.fill").font(YYUI.font(10)).frame(width: YYUI.s(28), height: YYUI.s(28)).background(YYColor.inset, in: .circle)
                    VStack(alignment: .leading, spacing: 1) {
                        Text("“\(context.sample(name))”").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(2)
                        Text("\(name)的一句 · 点一下试听").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 0)
                }
                .padding(.horizontal, YYUI.s(12)).padding(.vertical, YYUI.s(10))
                .yyCard()
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .padding(.top, YYUI.s(12))
            if voice != RoleVoice(voice: nil, rate: 1, pitch: 0, right: false) {
                Button(context.dramaEnabled ? String(localized: "恢复自动选角") : String(localized: "改回跟旁白")) {
                    voice = RoleVoice(voice: nil, rate: 1, pitch: 0, right: false)
                    context.set(name, voice)
                }
                .buttonStyle(YYButtonStyle(kind: .plain))
                .padding(.top, YYUI.s(12))
            }
        }
        .yySheetPage(name, back: isRoot ? nil : { if !path.isEmpty { path.removeLast() } }, close: close)
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear { voice = context.role(name) ?? RoleVoice(voice: nil, rate: 1, pitch: 0, right: false) }
        .onReceive(NotificationCenter.default.publisher(for: CastContext.changed)) { _ in
            if let v = try? context.core.bookRoles(bookId: context.bookID).first(where: { $0.name == name })?.voice { voice = v }
        }
    }

    private var voiceName: String {
        if let id = voice.voice, let v = Voices.find(id, bookID: context.bookID) {
            return v.name
        }
        return context.voiceLabel(for: name).text
    }

    private func binding<T>(_ key: WritableKeyPath<RoleVoice, T>) -> Binding<T> {
        Binding(get: { voice[keyPath: key] }, set: { voice[keyPath: key] = $0; context.set(name, voice) })
    }

    private func slider(_ title: String, value: String, note: String, range: ClosedRange<Double>, key: WritableKeyPath<RoleVoice, Double>, step: Double = 0.05) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack(alignment: .firstTextBaseline, spacing: YYUI.s(8)) {
                Text("\(title) \(value)").font(YYUI.font(15, .bold)).monospacedDigit()
                Spacer()
                Text(note).font(YYUI.font(10)).foregroundStyle(YYColor.t3)
            }
            Slider(value: Binding(get: { voice[keyPath: key] }, set: { voice[keyPath: key] = $0 }), in: range, step: step) { editing in
                if !editing { context.set(name, voice) }
            }
            .tint(YYTint.standard.fill)
        }
    }

    private func preview() {
        guard let player = context.player else { return }
        let base = voice.voice.flatMap { Voices.find($0, bookID: context.bookID) }
            ?? context.autoVoice(for: name)?.voice
            ?? player.currentVoice(for: context.language)
        guard let base else { return }
        let v: any Voice = (abs(voice.rate - 1) < 0.01 && voice.pitch == 0) ? base : TunedVoice(base: base, rate: Float(voice.rate), pitch: Float(voice.pitch))
        player.preview(v, text: context.sample(name))
    }
}

/// Choosing a voice for someone (or the narrator). 跟旁白 first, then 推荐 · 我的 · 云端 · 系统自带.
private struct VoiceChoiceView: View {
    let context: CastContext
    let name: String?
    @Binding var path: [CastSheet.Route]
    var isRoot: Bool = false
    let close: () -> Void
    @State private var chosen: String?

    var body: some View {
        #if os(macOS)
        let narratorVoice = context.player?.currentVoice(for: context.language)
        MacVoicePickerPanel(
            language: context.language,
            bookID: context.bookID,
            selectedID: chosen,
            sampleSource: (try? context.core.book(id: context.bookID))?.summary.title,
            onPreview: { preview($0) },
            onSelect: { voice in pick(id: voice.id, voice: voice) },
            onOpenClone: { existingID, initName in
                if let existingID {
                    path.append(.rerecord(id: existingID, name: initName))
                } else {
                    path.append(.clone(name))
                }
            },
            onOpenCloneDetail: { path.append(.cloneDetail($0)) },
            onClose: close,
            title: name.map { String(localized: "\($0)的声音") } ?? String(localized: "旁白的声音"),
            onBack: isRoot ? nil : { if !path.isEmpty { path.removeLast() } },
            usedByLabel: { usedBy($0) },
            follow: name.map { n in
                let auto = context.autoVoice(for: n)
                let fallbackVoice = auto?.voice ?? narratorVoice
                let title = context.dramaEnabled ? String(localized: "自动选角") : String(localized: "跟旁白")
                let detail: String = if context.dramaEnabled, let auto, !auto.isFallback {
                    String(localized: "广播剧自动分配为「\(auto.voice.name)」，也可手动改选")
                } else if context.dramaEnabled {
                    String(localized: "性别未定或暂无可用音色时先跟旁白，设为男/女后自动分声")
                } else {
                    String(localized: "现在旁白是\(narratorVoice?.name ?? "")，旁白换了\(n)也跟着换")
                }
                return MacVoicePickerPanel.Follow(
                    title: title,
                    narrator: fallbackVoice,
                    detail: detail,
                    selected: chosen == nil,
                    pick: { pick(id: nil, voice: fallbackVoice) }
                )
            }
        )
        .toolbar(.hidden)
        .navigationBarBackButtonHidden(true)
        .onAppear {
            chosen = name.map { context.role($0)?.voice } ?? context.player?.currentVoice(for: context.language)?.id
        }
        #else
        phoneBody
        #endif
    }

    private func preview(_ voice: any Voice) {
        if let player = context.player {
            player.preview(voice, text: context.sample(name))
        } else {
            Voices.preview(voice, text: context.sample(name))
        }
    }

    private var phoneBody: some View {
        YYPage {
            if let name {
                YYGroup {
                    followNarratorRow(for: name)
                }
                .padding(.top, YYUI.s(12))
            }
            VoiceCatalogSections(
                language: context.language,
                bookID: context.bookID,
                selectedID: chosen,
                sampleText: context.sample(name),
                usedByLabel: { usedBy($0) },
                onPreview: { voice in
                    if let player = context.player {
                        player.preview(voice, text: context.sample(name))
                    } else {
                        Voices.preview(voice, text: context.sample(name))
                    }
                },
                onSelect: { voice in
                    pick(id: voice.id, voice: voice)
                },
                onOpenClone: { existingID, initName in
                    if let existingID {
                        path.append(.rerecord(id: existingID, name: initName))
                    } else {
                        path.append(.clone(name))
                    }
                },
                onOpenCloneDetail: { entryID in
                    path.append(.cloneDetail(entryID))
                }
            )
        }
        .navigationTitle(name.map { String(localized: "\($0)的声音") } ?? String(localized: "旁白的声音"))
        #if os(iOS)
        .navigationBarTitleDisplayMode(.inline)
        #endif
        .onAppear {
            chosen = name.map { context.role($0)?.voice } ?? context.player?.currentVoice(for: context.language)?.id
        }
    }

    private func followNarratorRow(for characterName: String) -> some View {
        let narratorVoice = context.player?.currentVoice(for: context.language)
        let auto = context.autoVoice(for: characterName)
        let previewVoice = auto?.voice ?? narratorVoice
        let title = context.dramaEnabled ? String(localized: "自动选角") : String(localized: "跟旁白")
        let detail: String = if context.dramaEnabled, let auto, !auto.isFallback {
            String(localized: "广播剧自动分配为「\(auto.voice.name)」，也可手动改选")
        } else if context.dramaEnabled {
            String(localized: "性别未定或暂无可用音色时先跟旁白，设为男/女后自动分声")
        } else {
            String(localized: "现在旁白是\(narratorVoice?.name ?? "")，旁白换了\(characterName)也跟着换")
        }
        return HStack(spacing: YYUI.s(10)) {
            Button {
                if let previewVoice, let player = context.player {
                    player.preview(previewVoice, text: context.sample(characterName))
                }
            } label: {
                Image(systemName: "play.fill").font(YYUI.font(9)).foregroundStyle(YYColor.t1)
                    .frame(width: YYUI.s(28), height: YYUI.s(28)).background(YYColor.inset, in: .circle)
            }
            .buttonStyle(.plain)
            .accessibilityLabel("试听")
            Button { pick(id: nil, voice: previewVoice) } label: {
                HStack {
                    VStack(alignment: .leading, spacing: 1) {
                        Text(title).font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text(detail)
                            .font(YYUI.font(10)).foregroundStyle(YYColor.t3).lineLimit(2)
                    }
                    Spacer()
                    if chosen == nil {
                        Image(systemName: "checkmark").font(YYUI.font(12, .semibold)).foregroundStyle(YYTint.standard.accent)
                    }
                }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        }
        .padding(.vertical, YYUI.s(8))
    }

    /// 宝玉在用: someone else already reads with it.
    private func usedBy(_ id: String) -> String? {
        let who = context.roles.filter { $0.voice.voice == id && $0.name != name }.map(\.name)
        return who.isEmpty ? nil : String(localized: "\(who.joined(separator: String(localized: "、")))在用")
    }

    private func pick(id: String?, voice: (any Voice)?) {
        chosen = id
        if let name {
            var v = context.role(name) ?? RoleVoice(voice: nil, rate: 1, pitch: 0, right: false)
            v.voice = id
            context.set(name, v)
        } else if let voice {
            context.player?.choose(voice, for: context.language)
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }
}

extension Voices {
    /// A few words to try a voice with.
    static func sampleText(_ language: String) -> String {
        language.hasPrefix("zh") ? "你好，这是我的声音。" : "Hello, this is how I sound."
    }
}


/// 听成一部剧: the play made ahead, how it's said, and taken away as an audiobook.
struct DramaListenGroup: View {
    let core: YuyueCore
    let bookID: String
    let section: UInt32
    let language: String
    let narrator: String?
    @State private var dramaEnabled = false
    @State private var tagsOut = false
    @State private var sound = DramaSound(acting: true, beds: true, spots: true)
    @State private var canAct = false
    @State private var preparing = false
    @State private var made: DramaMade?
    @State private var exporting: String?
    /// 局域网传片段: the device the clips were taken from, while they are.
    @State private var borrowedFrom: String?
    #if os(iOS)
    @State private var shared: URL?
    #endif

    var body: some View {
        YYGroup { rows }
            .task { await refresh() }
            .onReceive(NotificationCenter.default.publisher(for: DramaSynth.sectionMade)) { n in
                if n.object as? String == bookID { Task { await refresh() } }
            }
            .onReceive(NotificationCenter.default.publisher(for: DramaSynth.settingsChanged)) { n in
                if n.object == nil || n.object as? String == bookID { Task { await refresh() } }
            }
            #if os(iOS)
            .sheet(item: Binding(get: { shared.map(DramaFile.init) }, set: { shared = $0?.url })) { ActivitySheet(items: [$0.url]) }
            #endif
    }

    private var rows: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 1) {
                    Text("广播剧模式").font(.yy(size: 12, weight: .bold))
                    Text(dramaEnabled ? String(localized: "按人物自动分声，带语气与场景音效，点了就听") : String(localized: "开启后自动为人物配声并加入场景音效"))
                        .font(.yy(size: 10)).foregroundStyle(YYColor.t3).lineLimit(2)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                YYSwitch(isOn: Binding(get: { dramaEnabled }, set: { setDramaEnabled($0) }), label: String(localized: "广播剧模式"))
            }
            .padding(.vertical, 8)
            if dramaEnabled {
                line("旁白提示词", String(localized: "如「宝玉笑道」等引述语")) { tags }
                if canAct {
                    line("情感语气", String(localized: "按原文语境表现笑、叹、低语等语气")) { soundSwitch(\.acting, String(localized: "情感语气")) }
                } else {
                    line("情感语气", String(localized: "需使用支持情感的云端音色或增强本地音色")) { off(String(localized: "情感语气")) }
                }
                line("环境音", String(localized: "随室内、雨夜、水边等场景铺垫氛围音")) { soundSwitch(\.beds, String(localized: "环境音")) }
                line("音效", String(localized: "随正文触发敲门、雷声、钟声等音效")) { soundSwitch(\.spots, String(localized: "音效")) }
                line("后台预合成全书", progress) { prepare }
            }
            if (made?.made ?? 0) > 0 {
                line("导出成有声书", exporting ?? String(localized: "做好的章，一个 m4b，带章节 · 导出的音频仅供个人收听；自带声音含公有领域录音与合成声音，详见关于页")) {
                    Button("导出…", action: export).buttonStyle(YYButtonStyle(kind: .plain, size: .small)).disabled(exporting != nil)
                }
            }
        }
    }

    private func notifySettingsChanged() {
        LiveCastCache.shared.invalidate(book: bookID)
        NotificationCenter.default.post(name: DramaSynth.settingsChanged, object: bookID)
    }

    private func setDramaEnabled(_ on: Bool) {
        dramaEnabled = on
        try? core.dramaSetEnabled(bookId: bookID, enabled: on)
        if !on {
            if preparing {
                preparing = false
                DramaSynth.setPreparing(bookID, false)
            }
            DramaSynth.shared.stop(bookID)
        }
        notifySettingsChanged()
    }

    private var tags: some View {
        let b = Binding<Bool>(get: { tagsOut }, set: { v in
            tagsOut = v
            try? core.dramaSetMode(bookId: bookID, dramaMode: v)
            notifySettingsChanged()
        })
        return YYSegments(items: [(false, String(localized: "保留")), (true, String(localized: "略过"))], selection: b, small: true)
    }

    private func soundSwitch(_ key: WritableKeyPath<DramaSound, Bool>, _ label: String) -> some View {
        let b = Binding<Bool>(get: { sound[keyPath: key] }, set: { v in
            sound[keyPath: key] = v
            try? core.dramaSetSound(bookId: bookID, sound: sound)
            notifySettingsChanged()
        })
        return YYSwitch(isOn: b, label: label)
    }

    private func off(_ label: String) -> some View {
        YYSwitch(isOn: .constant(false), label: label).disabled(true)
    }

    private var prepare: some View {
        let b = Binding<Bool>(get: { preparing }, set: { on in setPreparing(on) })
        return YYSwitch(isOn: b, label: String(localized: "后台预合成全书"))
    }

    private func setPreparing(_ on: Bool) {
        preparing = on
        DramaSynth.setPreparing(bookID, on)
        if on, !dramaEnabled {
            dramaEnabled = true
            try? core.dramaSetEnabled(bookId: bookID, enabled: true)
        }
        notifySettingsChanged()
        guard on, let dir = Audiobooks.shared?.dataDir else { return DramaSynth.shared.stop(bookID) }
        #if os(iOS)
        // 局域网传片段: a Mac nearby that has made the play hands its clips over; else made here.
        let core = core, id = bookID, language = language, section = section, narrator = narrator
        Task {
            if let offer = await RecordingFetch.shared.dramaOffer(for: id, core: core) {
                RecordingFetch.shared.takeClips(id, from: offer, core: core, dataDir: dir) {}
            } else if core.dramaBorrowedFrom(bookId: id) == nil {
                DramaSynth.shared.start(core: core, dataDir: dir, book: id, language: language, section: section, narrator: narrator)
            }
        }
        #else
        DramaSynth.shared.start(core: core, dataDir: dir, book: bookID, language: language, section: section, narrator: narrator)
        #endif
    }

    private var progress: String {
        #if os(iOS)
        if let t = RecordingFetch.shared.takingClips[bookID] {
            return String(localized: "从 Mac 拿做好的片段 \(t.got) / \(t.of)")
        }
        if preparing, !DramaSynth.isPluggedIn {
            return String(localized: "即时可听 · 连接电源时自动预合成全书")
        }
        #endif
        guard let made else { return String(localized: "不开启也可即时听；开启后提前做好全书") }
        if let from = borrowedFrom {
            return String(localized: "已完成 \(made.made) / \(made.sections) 章 · 来自「\(from)」")
        }
        let more = preparing ? String(localized: " · 应用运行期间自动继续") : ""
        return String(localized: "已完成 \(made.made) / \(made.sections) 章") + more
    }

    private func refresh() async {
        let core = core, id = bookID
        dramaEnabled = DramaSynth.isDramaActive(core, id)
        tagsOut = core.dramaMode(bookId: id)
        sound = (try? core.dramaSound(bookId: id)) ?? sound
        canAct = core.dramaCanAct(bookId: id) || (LiveVoiceModel.isInstalled && CardVoice.supportsLanguage(language))
        preparing = DramaSynth.preparing(id)
        borrowedFrom = core.dramaBorrowedFrom(bookId: id)
        made = await CastCore.loadMade(core, bookID: id)
        #if os(iOS)
        // Clips taken from a Mac: the Mac may have made more since.
        if preparing, borrowedFrom != nil, let dir = Audiobooks.shared?.dataDir,
           let offer = await RecordingFetch.shared.dramaOffer(for: id, core: core) {
            RecordingFetch.shared.takeClips(id, from: offer, core: core, dataDir: dir) {}
        }
        #endif
    }

    private func export() {
        let name = ((try? core.book(id: bookID).summary.title) ?? String(localized: "广播剧")) + ".m4b"
        #if os(macOS)
        let panel = NSSavePanel()
        panel.nameFieldStringValue = name
        guard panel.runModal() == .OK, let url = panel.url else { return }
        #else
        let url = FileManager.default.temporaryDirectory.appending(path: name)
        #endif
        guard let dir = Audiobooks.shared?.dataDir else { return }
        exporting = String(localized: "正在混音和压缩，一章要几十秒")
        let core = core, id = bookID
        Task {
            let ok = await DramaSynth.exportM4B(core, dir, book: id, to: url)
            exporting = ok ? nil : String(localized: "没导出来，再试一次")
            #if os(iOS)
            if ok { shared = url }
            #endif
        }
    }

    private func line(_ name: LocalizedStringKey, _ detail: String, @ViewBuilder control: () -> some View) -> some View {
        let ctl = control()
        return ViewThatFits(in: .horizontal) {
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).font(.yy(size: 12)).fixedSize(horizontal: true, vertical: false)
                    Text(detail).font(.yy(size: 10)).foregroundStyle(YYColor.t3).lineLimit(2)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                ctl.fixedSize(horizontal: true, vertical: false)
            }
            VStack(alignment: .leading, spacing: 6) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(name).font(.yy(size: 12))
                    Text(detail).font(.yy(size: 10)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
                }
                ctl
            }
        }
        .padding(.vertical, 7)
        .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
    }
}

#if os(iOS)
private struct DramaFile: Identifiable {
    let url: URL
    var id: URL { url }
}
#endif

#if os(macOS)
/// 人物 on the Mac: a float panel beside the page, the narrator at the top and each of the
/// cast with their voice; a click opens their page, a right-click the app's menu card. What's
/// known about who says what sits on top; 没把握的句子 open in the panel itself.
struct CastPanel: View {
    let model: ReaderModel
    @State private var cast: [CastMember] = []
    @State private var roles: [Role] = []
    @State private var autoVoices: [String: CastRoleVoice] = [:]
    @State private var dramaEnabled = false
    @State private var suggestions: [SameSuggestion] = []
    @State private var unsureCount = 0
    @State private var open: CastSheet.Route?
    @State private var unsure = false
    @State private var menu: (member: CastMember, at: CGPoint)?
    @State private var merging: String?
    @State private var frame: CGRect = .zero
    @State private var hoverClose = false
    private let tint = YYTint.standard

    private var section: UInt32 { model.player?.spot?.section ?? model.section ?? 0 }
    private var language: String { model.player?.language ?? model.detail?.summary.language ?? "zh" }

    private var closeButton: some View {
        Button { model.panel = nil } label: {
            Image(systemName: "xmark")
                .font(.yy(size: 10, weight: .bold))
                .foregroundStyle(hoverClose ? YYColor.t1 : YYColor.t2)
                .frame(width: 24, height: 24)
                .background(hoverClose ? YYColor.line : YYColor.inset, in: .circle)
                .contentShape(Circle().inset(by: -4))
        }
        .buttonStyle(.plain)
        .onHover { hoverClose = $0 }
        .animation(YYMotion.quick, value: hoverClose)
        .help(Text("关闭"))
        .accessibilityLabel(Text("关闭"))
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if unsure {
                HStack(alignment: .center, spacing: 8) {
                    Button { unsure = false } label: {
                        HStack(alignment: .firstTextBaseline, spacing: 6) {
                            Image(systemName: "chevron.left").font(.yy(size: 12, weight: .semibold)).foregroundStyle(YYColor.t2)
                            Text("待确认的台词").font(.yy(size: 15, weight: .bold)).foregroundStyle(YYColor.t1)
                        }
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    Spacer(minLength: 6)
                    closeButton
                }
                ScrollView { UnsureView(core: model.core, bookID: model.bookID, section: section, chapter: { model.detail?.chapterTitle(for: $0)?.split(separator: " ").first.map(String.init) }) }
                    .scrollIndicators(.never)
            } else {
                HStack(alignment: .center, spacing: 8) {
                    HStack(alignment: .firstTextBaseline, spacing: 8) {
                        Text("人物").font(.yy(size: 15, weight: .bold))
                        Text("读到这里 · \(cast.count) 人").font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: 6)
                    closeButton
                }
                ScrollView {
                    VStack(spacing: 10) {
                        DramaStatusBlock(core: model.core, bookID: model.bookID, section: section) { unsure = true }
                        if !suggestions.isEmpty {
                            YYGroup {
                                ForEach(suggestions.prefix(2), id: \.b) { s in
                                    SameSuggestionRow(core: model.core, bookID: model.bookID, suggestion: s) { n in
                                        let m = cast.first { $0.name == n }
                                        return (m?.avatar ?? String(n.last ?? "?"), Color(hex: m?.color ?? "#888888"))
                                    }
                                }
                            }
                        }
                        YYGroup {
                            Button { open = .voice(nil) } label: {
                                row(avatar: nil, color: YYColor.t3, name: String(localized: "旁白"), detail: String(localized: "全书默认"),
                                    voice: model.player?.currentVoice(for: language)?.name ?? "", own: true)
                            }
                            .buttonStyle(.plain)
                        }
                        YYGroup {
                            ForEach(cast, id: \.name) { m in
                                let display = voiceLabel(for: m.name)
                                Button { open = .role(m.name) } label: {
                                    row(avatar: m.avatar, color: Color(hex: m.color), name: m.name,
                                        detail: [m.aliases.prefix(1).joined(), String(localized: "\(m.linesSoFar) 句")].filter { !$0.isEmpty }.joined(separator: " · "),
                                        voice: display.text, own: display.emphasized)
                                        .background(menu?.member.name == m.name ? YYColor.inset : .clear)
                                }
                                .buttonStyle(.plain)
                                .overlay(RightClickCatcher { p in menu = (m, CGPoint(x: p.x - frame.minX, y: p.y - frame.minY)) })
                            }
                        }
                        DramaListenGroup(core: model.core, bookID: model.bookID, section: section, language: language, narrator: model.player?.currentVoice(for: language)?.id)
                    }
                }
                .scrollIndicators(.never)
            }
        }
        .padding(14)
        .onGeometryChange(for: CGRect.self) { $0.frame(in: .global) } action: { frame = $0 }
        .overlay { menuLayer.animation(YYMotion.quick, value: menu?.member.name) }
        .task(id: section) { await load() }
        .task {
            #if DEBUG
            // `-debugUnsure 1`: 没把握的句子 open in the panel.
            if UserDefaults.standard.bool(forKey: "debugUnsure") { unsure = true }
            #endif
        }
        .onChange(of: section) { DramaWork.shared.reading(model.bookID, section: section) }
        .onReceive(NotificationCenter.default.publisher(for: CastContext.changed)) { _ in Task { await load() } }
        .onReceive(NotificationCenter.default.publisher(for: DramaSynth.settingsChanged)) { n in
            if n.object == nil || n.object as? String == model.bookID { Task { await load() } }
        }
        .onReceive(NotificationCenter.default.publisher(for: DramaWork.updated)) { n in
            if n.object == nil || n.object as? String == model.bookID { Task { await load() } }
        }
        .sheet(item: Binding(get: { open.map(Opened.init) }, set: { open = $0?.route })) { o in
            CastSheet(core: model.core, bookID: model.bookID, section: section, player: model.player, language: language, start: [o.route]) { open = nil }
                .presentationBackground(YYColor.bg)
        }
        .sheet(item: Binding(get: { merging.map(Named.init) }, set: { merging = $0?.name })) { m in
            PeoplePicker(core: model.core, bookID: model.bookID, section: section, title: String(localized: "\(m.name)合并到"), except: m.name, pick: { into in
                merging = nil
                edit(.merge(into: into, from: m.name))
            }, close: { merging = nil })
        }
    }

    private struct Opened: Identifiable {
        let route: CastSheet.Route
        var id: String { "\(route)" }
    }

    private struct Named: Identifiable {
        let name: String
        var id: String { name }
    }

    /// The app's white card, where the right-click was.
    @ViewBuilder private var menuLayer: some View {
        if let menu {
            let m = menu.member
            GeometryReader { g in
                ZStack(alignment: .topLeading) {
                    Color.clear.contentShape(.rect)
                        .onTapGesture { self.menu = nil }
                        .overlay(RightClickCatcher { _ in self.menu = nil })
                    DramaMenuCard(title: m.name, detail: String(localized: "\(m.linesSoFar) 句")) {
                        DramaMenuGroup {
                            DramaMenuRow(title: String(localized: "\(m.name)的人物页…"), symbol: "person.crop.circle") { close { open = .role(m.name) } }
                        }
                        DramaMenuGroup {
                            if let other = m.maybeSame {
                                DramaMenuRow(title: String(localized: "合并到 \(other)"), symbol: "arrow.triangle.merge") { close { edit(.merge(into: other, from: m.name)) } }
                            }
                            DramaMenuRow(title: String(localized: "合并到别人…"), symbol: "magnifyingglass") { close { merging = m.name } }
                        }
                        DramaMenuGroup {
                            DramaMenuRow(title: String(localized: "设为女性"), symbol: m.gender == .f ? "checkmark" : "person",
                                         color: m.gender == .f ? tint.accent : YYColor.t1, bold: m.gender == .f) { close { edit(.setGender(name: m.name, gender: .f)) } }
                            DramaMenuRow(title: String(localized: "设为男性"), symbol: m.gender == .m ? "checkmark" : "person",
                                         color: m.gender == .m ? tint.accent : YYColor.t1, bold: m.gender == .m) { close { edit(.setGender(name: m.name, gender: .m)) } }
                        }
                        DramaMenuGroup {
                            DramaMenuRow(title: String(localized: "并非人物（移除）"), symbol: "nosign", color: YYColor.danger) { close { edit(.remove(name: m.name)) } }
                        }
                    }
                    .modifier(AtPoint(point: menu.at, size: g.size))
                    .transition(.opacity)
                }
            }
            .onExitCommand { self.menu = nil }
        }
    }

    private func close(then: () -> Void) {
        menu = nil
        then()
    }

    private func edit(_ e: CastEdit) {
        let core = model.core, id = model.bookID
        Task {
            await CastCore.editCast(core, bookID: id, edit: e)
            NotificationCenter.default.post(name: CastContext.changed, object: nil)
        }
    }

    private func row(avatar: String?, color: Color, name: String, detail: String, voice: String, own: Bool) -> some View {
        HStack(spacing: 10) {
            Group {
                if let avatar { Text(avatar).font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(color) } else { Image(systemName: "book").font(.yy(size: 11)).foregroundStyle(YYColor.t3) }
            }
            .frame(width: 26, height: 26)
            .background(avatar == nil ? YYColor.inset : color.opacity(0.18), in: .circle)
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text(detail).font(.yy(size: 10)).foregroundStyle(YYColor.t3).lineLimit(1)
            }
            Spacer(minLength: 6)
            Text(voice).font(.yy(size: 10, weight: own ? .semibold : .regular)).foregroundStyle(own ? YYColor.t1 : YYColor.t3).lineLimit(1)
        }
        .padding(.vertical, 8)
        .contentShape(.rect)
    }

    private func voiceLabel(for name: String) -> (text: String, emphasized: Bool) {
        if let id = roles.first(where: { $0.name == name })?.voice.voice,
           let v = Voices.find(id, bookID: model.bookID) {
            return (v.name, true)
        }
        if dramaEnabled, let auto = autoVoices[name], let v = Voices.find(auto.voice, bookID: model.bookID) {
            if !auto.isDialogueFallback {
                return (String(localized: "\(v.name) · 自动"), false)
            }
            return (String(localized: "自动 · 跟旁白"), false)
        }
        return (String(localized: "跟旁白"), false)
    }

    private func load() async {
        let active = DramaSynth.isDramaActive(model.core, model.bookID)
        var (c, r, sg, auto, unsureTotal) = await CastCore.loadSummary(model.core, bookID: model.bookID, section: section)
        if active, auto.isEmpty, let dir = Audiobooks.shared?.dataDir {
            _ = await LiveCastCache.shared.plan(
                core: model.core,
                dataDir: dir,
                book: model.bookID,
                language: language,
                narrator: model.player?.currentVoice(for: language)?.id
            )
            auto = await CastCore.loadAutoVoices(model.core, bookID: model.bookID)
        }
        dramaEnabled = active
        cast = c
        roles = r
        suggestions = sg
        unsureCount = unsureTotal
        autoVoices = Dictionary(uniqueKeysWithValues: auto.map { ($0.name, $0) })
    }
}
#endif

/// Off-main-actor FFI work shared by the cast and drama views.
nonisolated enum CastCore {
    @concurrent static func loadSummary(_ core: YuyueCore, bookID: String, section: UInt32) async -> ([CastMember], [Role], [SameSuggestion], [CastRoleVoice], Int) {
        ((try? core.bookCast(bookId: bookID, section: section)) ?? [],
         (try? core.bookRoles(bookId: bookID)) ?? [],
         (try? core.dramaSameSuggestions(bookId: bookID, uptoSection: section)) ?? [],
         (try? core.dramaRoleVoices(bookId: bookID)) ?? [],
         ((try? core.dramaReview(bookId: bookID, uptoSection: section)) ?? []).count)
    }

    @concurrent static func loadAutoVoices(_ core: YuyueCore, bookID: String) async -> [CastRoleVoice] {
        (try? core.dramaRoleVoices(bookId: bookID)) ?? []
    }

    @concurrent static func loadMade(_ core: YuyueCore, bookID: String) async -> DramaMade? {
        try? core.dramaMade(bookId: bookID)
    }

    @concurrent static func editCast(_ core: YuyueCore, bookID: String, edit: CastEdit) async {
        _ = try? core.dramaEditCast(bookId: bookID, edit: edit)
    }
}

/// 人物's list: on the Mac as tall as what it holds (up to 560, then it scrolls), so a short
/// cast leaves no empty paper below; on a phone a page.
private struct CastListPage<Content: View>: View {
    @ViewBuilder let content: Content

    var body: some View {
        #if os(macOS)
        YYFitScroll(maxHeight: 560) {
            VStack(alignment: .leading, spacing: 0) { content }
                .padding(.horizontal, YYUI.s(16))
                .padding(.bottom, YYUI.s(18))
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .background(YYColor.bg)
        #else
        YYPage { content }
        #endif
    }
}
