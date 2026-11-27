import SwiftUI
import YuyueCore

// MARK: - 排版

/// The card under Aa: paper, typeface, size, line spacing and columns, whether this book keeps
/// its own, and the rest under 更多. The page changes as it is set.
struct TypesetCard: View {
    @State private var confirming: YYConfirm?
    let model: ReaderModel
    @AppStorage("reader.twoColumns") private var twoColumns = true
    @AppStorage(EInk.key) private var einkOn = false
    @AppStorage(PageTurnPrefs.key) private var pageTurnRaw = PageTurn.slide.rawValue
    @AppStorage(PageTurnPrefs.soundKey) private var pageTurnSound = false
    @AppStorage(PageTurnPrefs.soundStyleKey) private var pageTurnSoundStyleRaw = PageTurnSoundStyle.softPaper.rawValue
    @AppStorage(PageTurnPrefs.hapticKey) private var pageTurnHaptic = false
    @State private var more = false
    /// 全部纸张, when 我的纸 are more than two rows.
    @State private var all = false
    @State private var hoverNew = false
    /// 调纸 open on this paper (a new one has no id yet).
    @State private var editing: CustomPaper?
    /// 重命名 asked for this paper.
    @State private var renaming: CustomPaper?
    @State private var newName = ""
    #if os(macOS)
    @State private var fontRow = FontRow.ids()
    #endif
    private let tint = YYTint.standard

    static let sizes = 12.0 ... 34.0
    /// 紧 · 适中 · 松; nil is the paper's own.
    private static let spacings: [Double?] = [1.6, nil, 2.2]

    var body: some View {
        Group {
            if let editing {
                PaperEditor(model: model, draft: editing) { saved in
                    self.editing = nil
                    if let saved { choose(saved) }
                }
                .id(editing.id)
            } else if more { moreSettings } else if all { allPapers } else { main }
        }
        // 304 wide in all.
        #if os(macOS)
        .frame(width: 272)
        #endif
        .padding(.horizontal, 16)
        .padding(.top, YYUI.sheetCardTop)
        .padding(.bottom, more || all || editing != nil ? 16 : 6)
        .animation(YYMotion.layout, value: more)
        .animation(YYMotion.layout, value: all)
        .animation(YYMotion.layout, value: editing == nil)
        .yyPrompt(String(localized: "重命名"), text: $newName, prompt: String(localized: "名字"),
                  isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } }),
                  confirm: String(localized: "保存")) {
            if let renaming { Papers.shared.rename(renaming, to: String(newName.prefix(20))) }
            renaming = nil
        }
        .yyConfirm($confirming)
    }

    private var main: some View {
        VStack(alignment: .leading, spacing: 12) {
            papers
            fonts
            HStack(spacing: 12) {
                Text("A").font(YYUI.serif(12)).foregroundStyle(YYColor.t1)
                Slider(value: binding(\.fontSize), in: Self.sizes, step: 1)
                    .controlSize(.small)
                    .accessibilityLabel(Text("字号"))
                    .accessibilityValue(Text("\(Int(model.settings.fontSize))"))
                    // A popover doesn't take the window's tint: without it the slider is system blue.
                    .tint(YYTint.standard.fill)
                Text("A").font(YYUI.serif(18)).foregroundStyle(YYColor.t1)
                Text("\(Int(model.settings.fontSize))")
                    .font(.yy(size: 12, weight: .bold))
                    .monospacedDigit()
                    .frame(minWidth: 26).frame(height: 26)
                    .background(YYColor.card, in: .rect(cornerRadius: 8))
                    .shadow(color: .black.opacity(0.06), radius: 1, y: 1)
            }
            .padding(.leading, 6).padding(.trailing, 4)
            .frame(height: 34)
            .background(YYColor.inset, in: .rect(cornerRadius: 12))
            // 1 : 1.25.
            HStack(spacing: 8) {
                Segments(items: Self.spacings.enumerated().map { ($0.offset, [String(localized: "行距紧"), String(localized: "行距适中"), String(localized: "行距松")][$0.offset]) },
                         selection: Binding(get: { Self.spacings.firstIndex(of: model.settings.lineHeight) ?? 1 },
                                            set: { i in model.update { $0.lineHeight = Self.spacings[i] } }), small: true) { i, symbol in
                    Image(systemName: i == 1 ? "text.justify" : "line.3.horizontal")
                        .font(.yy(size: 12, weight: .medium))
                        .scaleEffect(y: i == 0 ? 0.75 : i == 2 ? 1.25 : 1)
                        .help([String(localized: "行距紧"), String(localized: "行距适中"), String(localized: "行距松")][i])
                }
                #if os(macOS)
                .frame(width: (272 - 8) / 2.25)
                #endif
                Segments(items: [(false, String(localized: "单栏")), (true, String(localized: "双栏"))], selection: Binding(get: { twoColumns }, set: {
                    twoColumns = $0
                    model.pager.prefersTwoColumns = $0
                }), small: true) { two, title in
                    HStack(spacing: 4) {
                        Image(systemName: two ? "book.pages" : "doc.plaintext")
                        Text(title).lineLimit(1).minimumScaleFactor(0.5)
                    }
                }
                .disabled(model.settings.mode == .scrolled)
            }
            VStack(spacing: 0) {
                switchRow(String(localized: "只用于这本书"), Binding(get: { model.ownSettings }, set: { model.setOwnSettings($0) }))
                    .frame(height: 38)
                    .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                Button { more = true } label: {
                    HStack(spacing: 8) {
                        Text("更多").font(.yy(size: 12)).foregroundStyle(YYColor.t1)
                        Spacer()
                        HStack(spacing: 3) {
                            Text("段距、对齐、简繁与规则")
                            Image(systemName: "chevron.right").font(.yy(size: 9, weight: .semibold))
                        }
                        .font(.yy(size: 11)).foregroundStyle(YYColor.t3)
                    }
                    .frame(height: 38)
                    .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            .padding(.horizontal, -2)
        }
    }

    private var moreSettings: some View {
        VStack(alignment: .leading, spacing: 14) {
            Button { more = false } label: {
                Label("排版", systemImage: "chevron.left")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain)
            row(String(localized: "翻页方式")) {
                Segments(items: [(LayoutMode.paginated, String(localized: "左右翻页")), (.scrolled, String(localized: "上下滚动"))], selection: binding(\.mode)) { _, t in Text(t) }
                    .frame(width: 170)
            }
            if model.settings.mode == .paginated {
                row(String(localized: "翻页动画")) {
                    Segments(
                        items: [(PageTurn.slide, String(localized: "滑动")), (.cover, String(localized: "覆盖")), (.none, String(localized: "无动画"))],
                        selection: Binding(get: { PageTurn.of(pageTurnRaw) }, set: { pageTurnRaw = $0.rawValue; model.pager.pageTurn = $0 }),
                        small: true
                    ) { _, t in Text(t) }
                        .frame(width: 170)
                }
                switchRow(
                    String(localized: "翻页音效"),
                    Binding(
                        get: { pageTurnSound },
                        set: { next in
                            pageTurnSound = next
                            if next { PageTurnSound.shared.previewSelection(pageTurnSoundStyleRaw) }
                        }
                    )
                )
                if pageTurnSound {
                    PageTurnSoundStyleGrid(
                        selectedRaw: $pageTurnSoundStyleRaw,
                        showSubtitle: false,
                        compact: true,
                        eink: einkOn
                    )
                    .transition(.opacity)
                }
                #if os(iOS)
                switchRow(
                    String(localized: "翻页轻震"),
                    Binding(
                        get: { pageTurnHaptic },
                        set: { next in
                            pageTurnHaptic = next
                            if next { PageTurnHaptics.shared.preview() }
                        }
                    )
                )
                #endif
            }
            row(String(localized: "段距")) {
                Segments(items: [(Double?.some(0), String(localized: "无")), (nil, String(localized: "适中")), (.some(1), String(localized: "宽"))], selection: binding(\.paragraphSpacing)) { _, t in Text(t) }
                    .frame(width: 170)
            }
            row(String(localized: "首行缩进")) {
                Segments(items: [(Bool?.none, String(localized: "跟书")), (.some(true), String(localized: "缩进")), (.some(false), String(localized: "不缩"))], selection: binding(\.indent)) { _, t in Text(t) }
                    .frame(width: 170)
            }
            row(String(localized: "两端对齐")) {
                Segments(items: [(Bool?.none, String(localized: "跟书")), (.some(true), String(localized: "对齐")), (.some(false), String(localized: "靠左"))], selection: binding(\.justify)) { _, t in Text(t) }
                    .frame(width: 170)
            }
            switchRow(String(localized: "中文标点挤压"), binding(\.compress))
            if model.isChinese {
                row(String(localized: "简繁")) {
                    Segments(items: [(Script?.none, String(localized: "原样")), (.some(.simplified), String(localized: "简体")), (.some(.traditional), String(localized: "繁體"))],
                             selection: Binding(get: { model.script }, set: { model.script = $0 })) { _, t in Text(t) }
                        .frame(width: 170)
                }
            }
            if model.detail?.summary.format != .pdf {
                Button {
                    model.settingsShown = false
                    model.rulesFromSettings = true
                    model.rulesShown = true
                } label: {
                    HStack {
                        Text("替换规则").font(.yy(size: 12.5)).foregroundStyle(YYColor.t1)
                        Spacer()
                        Text("去广告、改读音").font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
                        Image(systemName: "chevron.right").font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYColor.t3)
                    }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            if model.detail?.summary.format == .txt {
                Button {
                    model.settingsShown = false
                    model.txtShown = true
                } label: {
                    HStack {
                        Text("编码和目录").font(.yy(size: 12.5)).foregroundStyle(YYColor.t1)
                        Spacer()
                        Text("乱码、章节不对").font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
                        Image(systemName: "chevron.right").font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYColor.t3)
                    }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            Text("页边距随窗口宽度自动调节，保持舒适行宽。")
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
        }
    }

    private func switchRow(_ title: String, _ on: Binding<Bool>) -> some View {
        row(title) {
            Toggle(title, isOn: on)
                .labelsHidden()
                .toggleStyle(.switch)
                .controlSize(.small)
                .tint(YYTint.standard.fill)
        }
    }

    private func row(_ title: String, @ViewBuilder _ content: () -> some View) -> some View {
        HStack {
            Text(title).font(.yy(size: 12.5)).foregroundStyle(YYColor.t1)
            Spacer()
            content()
        }
    }

    /// 字体: the same 永 tiles and download feel as 设置 › 阅读, with 更多字体 at the end.
    @ViewBuilder private var fonts: some View {
        #if os(iOS)
        PhoneFontTiles(selected: FontShelf.id(of: model.settings), titled: false) { id in
            useFace(id)
        }
        #else
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 5), count: 4), spacing: 5) {
            ForEach(macFontIDs, id: \.self) { id in
                FontTile(id: id, selected: FontShelf.id(of: model.settings) == id, pick: { useFace(id) },
                         remove: FontShelf.builtIn.contains(id) ? nil : { removeFontFromRow(id) },
                         compact: true)
            }
            MoreFontsTile(open: { model.fontBookShown = true }, compact: true)
        }
        .animation(YYMotion.layout, value: fontRow)
        .onAppear { fontRow = FontRow.ids() }
        .onReceive(NotificationCenter.default.publisher(for: .fontsChanged)) { _ in fontRow = FontRow.ids() }
        .onReceive(NotificationCenter.default.publisher(for: FontRow.changed)) { _ in fontRow = FontRow.ids() }
        #endif
    }

    private func useFace(_ id: String) {
        model.update { FontShelf.shared.apply(id, to: &$0) }
        FontShelf.shared.ensure(id)
    }

    #if os(macOS)
    /// Built-in three, the faces in the row, and this book's own face when it is another.
    private var macFontIDs: [String] {
        var ids = FontShelf.builtIn + fontRow.filter { !FontShelf.builtIn.contains($0) }
        let now = FontShelf.id(of: model.settings)
        if !ids.contains(now) { ids.append(now) }
        return ids
    }

    private func removeFontFromRow(_ id: String) {
        FontRow.remove(id)
        fontRow = FontRow.ids()
        if FontShelf.id(of: model.settings) == id {
            let fallback = switch FontShelf.shared.kind(id) {
            case .song, .fang: "serif"
            case .kai, .hand: "kai"
            case .hei: "system"
            }
            useFace(fallback)
        }
    }
    #endif

    private var resolvedPaper: PaperAppearance.Resolved { PaperAppearance.shared.resolve(model.settings) }

    private var followAppearance: Binding<Bool> {
        Binding(
            get: { PaperAppearance.shared.follow },
            set: { PaperAppearance.shared.setFollow($0, in: model.settings) { model.update($0) } }
        )
    }

    private var paperHeader: some View {
        HStack(spacing: 8) {
            Text("纸色").font(.yy(size: 11, weight: .semibold)).foregroundStyle(YYColor.t2)
            Spacer()
            Text("跟随外观").font(.yy(size: 11)).foregroundStyle(YYColor.t2)
            YYSwitch(isOn: followAppearance, label: String(localized: "跟随外观"))
        }
        .opacity(einkOn ? 0.45 : 1)
        .disabled(einkOn)
    }

    #if os(iOS)
    /// The phone keeps the strip: the presets, then 我的纸, a finger swipes along them; ＋ stays
    /// pinned at the right so making one is always in sight.
    private var papers: some View {
        let choice = resolvedPaper.choiceID
        return VStack(alignment: .leading, spacing: 8) {
            if einkOn {
                HStack(spacing: 6) {
                    Image(systemName: "rectangle.portrait.on.rectangle.portrait")
                        .font(.yy(size: 11))
                    Text("墨水屏模式已接管纸色，关闭后恢复原纸色")
                        .font(.yy(size: 11))
                }
                .foregroundStyle(YYColor.t2)
            }
            paperHeader
            HStack(alignment: .top, spacing: 0) {
                ScrollViewReader { proxy in
                    ScrollView(.horizontal) {
                        HStack(spacing: 4) {
                            ForEach(YYPaper.all) { paper in
                                PaperTile(paper: paper, texture: paper.hasNoiseTexture ? .grain : .none, name: paper.name, selected: choice == paper.id) {
                                    if let id = Paper(id: paper.id) {
                                        PaperAppearance.shared.pick(preset: id, in: model.settings) { model.update($0) }
                                    }
                                }
                                .id(paper.id)
                            }
                            ForEach(Papers.shared.list, id: \.id) { mine in
                                PaperTile(paper: YYPaper(custom: mine), texture: mine.texture, name: mine.name, selected: choice == mine.id) { choose(mine) }
                                    .id(mine.id)
                                    // 长按 / 右键: a menu card, 删除 in red in a group of its own.
                                    .contextMenu {
                                        Button(String(localized: "调整颜色"), systemImage: "paintpalette") { editing = mine }
                                        Button(String(localized: "重命名"), systemImage: "pencil") { newName = mine.name; renaming = mine }
                                        Button(String(localized: "复制一张"), systemImage: "plus.square.on.square") { duplicate(mine) }
                                        Divider()
                                        Button(String(localized: "删除…"), systemImage: "trash", role: .destructive) { confirming = YYConfirm(title: String(localized: "删除“\(mine.name)”？"), message: String(localized: "正在使用这张纸的书将恢复为相近的预设纸色。"), button: String(localized: "删除")) { delete(mine) } }
                                    }
                            }
                        }
                        .padding(.leading, 16).padding(.trailing, 12)
                    }
                    .scrollIndicators(.never)
                    // The paper the page is on stays in sight, a new one too.
                    .onAppear { proxy.scrollTo(choice, anchor: .center) }
                    .onChange(of: choice) { _, id in withAnimation(YYMotion.standard) { proxy.scrollTo(id, anchor: .center) } }
                }
                .mask {
                    HStack(spacing: 0) {
                        Rectangle()
                        LinearGradient(colors: [.black, .black.opacity(0)], startPoint: .leading, endPoint: .trailing).frame(width: 20)
                    }
                }
                Button { editing = newPaper() } label: {
                    VStack(spacing: 6) {
                        Image(systemName: "plus")
                            .font(.yy(size: 15, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .frame(width: 46, height: 38)
                            .background(YYColor.inset, in: .rect(cornerRadius: 10))
                            .padding(2)
                        Text("新建").font(.yy(size: 10.5)).foregroundStyle(YYColor.t2).lineLimit(1)
                    }
                    .padding(.leading, 6)
                    .overlay(alignment: .leading) { Rectangle().fill(YYColor.line).frame(width: 0.5).padding(.vertical, 4) }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .help(Text("自己调一张纸"))
                .accessibilityLabel(Text("自己调一张纸"))
            }
            .padding(.leading, -16)
            .opacity(einkOn ? 0.45 : 1)
            .disabled(einkOn)
        }
    }
    #else
    /// 纸张选择: the five presets fill a row; 我的纸 go under them five to
    /// a row, ＋ after the last. Two rows at most, so the card keeps its height: past that the last
    /// cell is 全部, a page of every paper. The one on the page always shows, standing in for the
    /// last of the two rows if it's further on.
    private var papers: some View {
        let mine = Papers.shared.list
        return VStack(alignment: .leading, spacing: 10) {
            if einkOn {
                HStack(spacing: 6) {
                    Image(systemName: "rectangle.portrait.on.rectangle.portrait")
                        .font(.yy(size: 10.5))
                    Text("墨水屏模式已接管纸色，关闭后恢复原纸色")
                        .font(.yy(size: 10.5))
                }
                .foregroundStyle(YYColor.t2)
            }
            paperHeader
            Group {
                paperGrid { ForEach(YYPaper.all) { presetTile($0) } }
                if mine.isEmpty {
                    // The whole row is the button: the words alone were 13 high and easy to miss.
                    Button { editing = newPaper() } label: {
                        HStack {
                            Text("我的纸").font(.yy(size: 10.5, weight: .semibold)).foregroundStyle(YYColor.t2)
                            Spacer()
                            Label("自己调一张", systemImage: "plus").labelStyle(TightLabel())
                                .font(.yy(size: 10.5)).foregroundStyle(YYColor.t2)
                        }
                        .padding(.horizontal, 8)
                        .frame(height: 30)
                        .background(hoverNew ? YYColor.inset : .clear, in: .rect(cornerRadius: 8))
                        .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .onHover { hoverNew = $0 }
                    .help(Text("自己调一张纸"))
                    .padding(.horizontal, -6)
                    .padding(.vertical, -4)
                } else {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text("我的纸").font(.yy(size: 10.5, weight: .semibold)).foregroundStyle(YYColor.t2)
                        Text("\(mine.count) 张").font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3)
                        Spacer()
                        if mine.count > Self.shown {
                            Button { all = true } label: {
                                HStack(spacing: 2) {
                                    Text("全部")
                                    Image(systemName: "chevron.right").font(.yy(size: 8.5, weight: .semibold))
                                }
                                .font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    .padding(.horizontal, 2)
                    .padding(.bottom, -4)
                    paperGrid {
                        ForEach(shownMine(mine), id: \.id) { mineTile($0) }
                        addTile
                        if mine.count > Self.shown { allTile(mine.count) }
                    }
                }
            }
            .opacity(einkOn ? 0.45 : 1)
            .disabled(einkOn)
        }
    }

    #endif

    /// Two rows of five: eight papers, ＋ and 全部. Nine papers and ＋ fit without 全部.
    private static let shown = 9

    private func shownMine(_ mine: [CustomPaper]) -> [CustomPaper] {
        guard mine.count > Self.shown else { return mine }
        var head = Array(mine.prefix(Self.shown - 1))
        let choice = resolvedPaper.choiceID
        if let on = mine.firstIndex(where: { $0.id == choice }), on >= head.count { head[head.count - 1] = mine[on] }
        return head
    }

    /// 全部纸张: the same kind of page as 更多, every paper, scrolling when they're many.
    private var allPapers: some View {
        let mine = Papers.shared.list
        return VStack(alignment: .leading, spacing: 12) {
            ZStack {
                Text("全部纸张").font(.yy(size: 12.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                HStack {
                    Button { all = false } label: {
                        Label("排版", systemImage: "chevron.left")
                            .font(.yy(size: 12.5, weight: .semibold))
                            .foregroundStyle(tint.accent)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    Spacer()
                }
            }
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 10) {
                        groupTitle(String(localized: "自带"), nil)
                        paperGrid { ForEach(YYPaper.all) { presetTile($0) } }
                        groupTitle(String(localized: "我的纸"), String(localized: "\(mine.count) 张"))
                        paperGrid {
                            ForEach(mine, id: \.id) { mineTile($0) }
                            addTile
                        }
                    }
                    .padding(.vertical, 4)
                }
                .scrollIndicators(.automatic)
                .frame(maxHeight: 320)
                .fixedSize(horizontal: false, vertical: true)
                .onAppear { proxy.scrollTo(resolvedPaper.choiceID, anchor: .center) }
            }
            Text("右键一张自己调的纸：调整颜色、重命名、复制、删除。")
                .font(.yy(size: 10.5)).foregroundStyle(YYColor.t3)
        }
    }

    private func groupTitle(_ title: String, _ count: String?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(title).font(.yy(size: 10.5, weight: .semibold)).foregroundStyle(YYColor.t2)
            if let count { Text(count).font(.yy(size: 10.5)).monospacedDigit().foregroundStyle(YYColor.t3) }
        }
        .padding(.horizontal, 2)
        .padding(.bottom, -4)
    }

    /// Five columns.
    private func paperGrid(@ViewBuilder _ content: () -> some View) -> some View {
        LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: 4), count: 5), alignment: .center, spacing: 10, content: content)
    }

    private func presetTile(_ paper: YYPaper) -> some View {
        PaperTile(paper: paper, texture: paper.hasNoiseTexture ? .grain : .none, name: paper.name, selected: resolvedPaper.choiceID == paper.id) {
            if let id = Paper(id: paper.id) {
                PaperAppearance.shared.pick(preset: id, in: model.settings) { model.update($0) }
            }
        }
        .id(paper.id)
    }

    private func mineTile(_ mine: CustomPaper) -> some View {
        PaperTile(paper: YYPaper(custom: mine), texture: mine.texture, name: mine.name, selected: resolvedPaper.choiceID == mine.id) { choose(mine) }
            .id(mine.id)
            // 长按 / 右键: a menu card, 删除 in red in a group of its own.
            .contextMenu {
                Button(String(localized: "调整颜色"), systemImage: "paintpalette") { editing = mine }
                Button(String(localized: "重命名"), systemImage: "pencil") { newName = mine.name; renaming = mine }
                Button(String(localized: "复制一张"), systemImage: "plus.square.on.square") { duplicate(mine) }
                Divider()
                Button(String(localized: "删除…"), systemImage: "trash", role: .destructive) { confirming = YYConfirm(title: String(localized: "删除“\(mine.name)”？"), message: String(localized: "正在使用这张纸的书将恢复为相近的预设纸色。"), button: String(localized: "删除")) { delete(mine) } }
            }
    }

    private var addTile: some View {
        cellButton(String(localized: "新建"), help: String(localized: "自己调一张纸")) { editing = newPaper() } face: {
            Image(systemName: "plus").font(.yy(size: 15, weight: .medium)).foregroundStyle(YYColor.t2)
        }
    }

    private func allTile(_ count: Int) -> some View {
        cellButton(String(localized: "全部"), help: String(localized: "全部纸张")) { all = true } face: {
            Text("\(count)").font(.yy(size: 11.5, weight: .semibold)).monospacedDigit().foregroundStyle(YYColor.t1)
        }
    }

    /// A cell the size of a paper, on the inset grey: ＋ 新建 and 全部.
    private func cellButton(_ title: String, help: String, action: @escaping () -> Void, @ViewBuilder face: () -> some View) -> some View {
        Button(action: action) {
            VStack(spacing: 6) {
                face()
                    .frame(width: 46, height: 38)
                    .background(YYColor.inset, in: .rect(cornerRadius: 10))
                    .padding(2)
                Text(title).font(.yy(size: 10.5)).foregroundStyle(YYColor.t2).lineLimit(1)
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help(Text(help))
        .accessibilityLabel(Text(help))
    }

    private func choose(_ mine: CustomPaper) {
        PaperAppearance.shared.pick(custom: mine, in: model.settings) { model.update($0) }
    }

    private func newPaper() -> CustomPaper { Papers.shared.newDraft(from: model.settings) }

    /// 复制一张: straight onto the shelf of papers, named 豆沙绿 2 and so on, and on the page.
    private func duplicate(_ mine: CustomPaper) {
        if let saved = Papers.shared.duplicate(mine) { choose(saved) }
    }

    /// 删除, with 撤销 for a few seconds.
    private func delete(_ mine: CustomPaper) {
        let wasOn = resolvedPaper.custom?.id == mine.id || model.settings.customPaper == mine.id
        if model.settings.customPaper == mine.id { model.update { $0.customPaper = nil } }
        PaperAppearance.shared.forget(mine.id)
        Papers.shared.delete(mine.id)
        model.show(Library.Toast(icon: "trash", danger: true, title: String(localized: "已删除“\(mine.name)”"), detail: "", undo: { [model] in
            Papers.shared.restore(mine.id)
            if wasOn {
                PaperAppearance.shared.pick(custom: mine, in: model.settings) { model.update($0) }
            }
        }))
    }

    private func binding<T>(_ key: WritableKeyPath<ReaderSettings, T>) -> Binding<T> {
        Binding(get: { model.settings[keyPath: key] }, set: { value in model.update { $0[keyPath: key] = value } })
    }
}

/// A row of choices on an inset track, the chosen one lifted on a white chip. `small` is
/// The one beside 导出 in a shelf header.
struct Segments<T: Hashable, Label: View>: View {
    let items: [(T, String)]
    @Binding var selection: T
    var small = false
    @ViewBuilder let label: (T, String) -> Label

    var body: some View {
        HStack(spacing: 2) {
            ForEach(items, id: \.0) { value, title in
                let on = selection == value
                let ink: Color = on ? YYColor.t1 : YYColor.t2
                let chip: Color = on ? YYColor.card : .clear
                let shade: Double = on ? 0.07 : 0
                let size: CGFloat = small ? 11 : 12
                let height: CGFloat = small ? 24 : 30
                let corner: CGFloat = small ? 7 : 9
                Button { selection = value } label: {
                    label(value, title)
                        .font(.yy(size: size, weight: on ? .semibold : .regular))
                        .foregroundStyle(ink)
                        .lineLimit(1)
                        .minimumScaleFactor(small ? 0.5 : 0.75)
                        .padding(.horizontal, 4)
                        .frame(maxWidth: .infinity)
                        .frame(minHeight: height)
                        .background(chip, in: .rect(cornerRadius: corner))
                        .shadow(color: .black.opacity(shade), radius: 1.5, y: 0.5)
                        .animation(YYMotion.quick, value: on)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(title)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(3)
        .background(YYColor.inset, in: .rect(cornerRadius: small ? 10 : 12))
    }
}

// MARK: - 听书

/// The card over the reading capsule: the voice and 试听, speed (the pitch stays), the sleep
/// timer, and 逐句翻译 · 单句循环 · 只听.
struct ListenCard: View {
    let player: Player?
    @Binding var rate: Float
    /// Minutes of listening left in the chapter (or the PDF's page) at this speed.
    let minutesLeft: Int
    /// 只听, for a book; a PDF has no text of its own to hide.
    var listenOnly: Binding<Bool>?
    /// What a section is here: 章, or 页 in a PDF.
    var unit = String(localized: "章")
    /// 人物声音: the book and the chapter, for the cast page (a PDF has none).
    var castBook: (core: YuyueCore, bookID: String, section: UInt32)?
    /// The open book, when this card is the reader's. A PDF player has none, so 记下这句 / 纠正读音 stay hidden.
    private let reader: ReaderModel?
    /// Closes the sheet or popover after 记下这句 / 纠正读音, so the playback capsule can morph.
    private let dismissListen: () -> Void
    @State private var castShown = false
    @State private var voicePickerShown = false
    @State private var sleepOpen = false
    @State private var mainHeight: CGFloat = 0
    @State private var ownVoices = 0
    @State private var dramaOn = false
    private let tint = YYTint.standard

    init(model: ReaderModel, dismiss: @escaping () -> Void = {}) {
        reader = model
        dismissListen = dismiss
        player = model.player
        _rate = Binding(get: { model.rate }, set: { model.rate = $0 })
        minutesLeft = Self.minutesLeft(model)
        listenOnly = Binding(get: { model.listenOnly }, set: { model.listenOnly = $0 })
        if let section = model.player?.spot?.section ?? model.section { castBook = (model.core, model.bookID, section) }
    }

    init(player: Player?, rate: Binding<Float>, minutesLeft: Int, unit: String) {
        reader = nil
        dismissListen = {}
        self.player = player
        _rate = rate
        self.minutesLeft = minutesLeft
        self.unit = unit
    }

    /// The speed track's marks.
    private static let rates: [Float] = [0.8, 1.0, 1.2, 1.5, 2.0]

    var body: some View {
        Group {
            if let player, sleepOpen {
                sleepPage(player)
                    .frame(minHeight: mainHeight, alignment: .top)
                    .transition(
                        YYMotion.reduce
                            ? .opacity
                            : .asymmetric(
                                insertion: .move(edge: .trailing).combined(with: .opacity),
                                removal: .move(edge: .trailing).combined(with: .opacity)
                            )
                    )
            } else {
                mainContent
                    .onGeometryChange(for: CGFloat.self, of: \.size.height) { h in
                        if h > 0, !sleepOpen { mainHeight = ceil(h) }
                    }
                    .transition(
                        YYMotion.reduce
                            ? .opacity
                            : .asymmetric(
                                insertion: .move(edge: .leading).combined(with: .opacity),
                                removal: .move(edge: .leading).combined(with: .opacity)
                            )
                    )
            }
        }
        .clipped()
        .animation(YYMotion.standard, value: sleepOpen)
        .padding(.horizontal, 16)
        #if os(iOS)
        .padding(.top, 20)
        .padding(.bottom, 18)
        #else
        .padding(.vertical, 14)
        .frame(width: 460)
        #endif
        .onDisappear { Voices.stopPreview() }
        .task(id: castShown) { countOwnVoices() }
        .task {
            #if DEBUG
            // `-debugCast 1`: the cast page straight away (with `-debugListen 1`).
            if UserDefaults.standard.bool(forKey: "debugCast") { try? await Task.sleep(for: .seconds(0.8)); castShown = true }
            // `-debugLivePhase …`: the 准备更好的声音 row; `-debugVoicePicker 1`: the 换声音 sheet.
            if let p = UserDefaults.standard.string(forKey: "debugLivePhase") { LiveVoiceDownload.shared.debugShow(p) }
            if UserDefaults.standard.bool(forKey: "debugVoicePicker") { try? await Task.sleep(for: .seconds(0.8)); voicePickerShown = true }
            #endif
        }
        .sheet(isPresented: $castShown) {
            if let castBook {
                CastSheet(core: castBook.core, bookID: castBook.bookID, section: castBook.section, player: player,
                          language: player?.language ?? "zh") { castShown = false }
                    .presentationBackground(YYColor.bg)
            }
        }
        .sheet(isPresented: $voicePickerShown) {
            if let player {
                let lang = player.language
                let bookLine = castBook.map { CloneVoiceView.nextBookSentence(core: $0.core, bookID: $0.bookID, section: $0.section, player: player, language: lang) }
                VoicePickerSheet(
                    title: String(localized: "声音"),
                    language: lang,
                    bookID: castBook?.bookID,
                    selectedID: player.currentVoice(for: lang)?.id,
                    sampleText: sample(lang),
                    previewSentence: bookLine,
                    sampleSource: castBook == nil ? nil : player.bookTitle,
                    readyVoiceID: player.readyOfflineVoice?.id,
                    // 指着试听: the sentence the book stopped on, in the voice pointed at.
                    onPreview: { v in player.preview(v, text: bookLine.flatMap { $0.isEmpty ? nil : $0 } ?? sample(lang)) },
                    onSelect: { v in player.choose(v, for: lang) },
                    onClose: {
                        voicePickerShown = false
                        player.dismissReadyOfflineVoice()
                    }
                )
                .presentationBackground(YYColor.bg)
            }
        }
    }

    private var mainContent: some View {
        VStack(alignment: .leading, spacing: 12) {
            #if os(macOS)
            HStack(spacing: 6) {
                Text(String(localized: "听书")).font(.yy(size: 13.5, weight: .bold)).foregroundStyle(YYColor.t1)
                Spacer()
                HStack(spacing: 4) {
                    Key(String(localized: "空格"))
                    Text("暂停").padding(.trailing, 4)
                    Key("⌘[")
                    Key("⌘]")
                    Text("上一句 / 下一句")
                }
                .font(.yy(size: 10.5))
                .foregroundStyle(YYColor.t3)
                if let player {
                    sleepToggleButton(player)
                }
            }
            #else
            if let player {
                HStack(alignment: .center) {
                    Text(String(localized: "听书"))
                        .font(.yy(size: 15, weight: .bold))
                        .foregroundStyle(YYColor.t1)
                    Spacer()
                    sleepToggleButton(player)
                }
                .padding(.horizontal, 2)
            }
            #endif
            if let player {
                if player.recording != nil { narrator(player) }
                if let hotNotice = player.thermalNotice {
                    HStack(spacing: 8) {
                        Image(systemName: "thermometer.medium")
                            .font(.yy(size: 12))
                            .foregroundStyle(YYColor.warn)
                        Text(hotNotice)
                            .font(.yy(size: 11, weight: .medium))
                            .foregroundStyle(YYColor.t1)
                        Spacer()
                    }
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
                    .background(YYColor.warn.opacity(0.10), in: .rect(cornerRadius: 11))
                }
                voice(player)
                if !player.narrated {
                    if case .downloading = KokoroDownload.shared.phase, CardVoice.supportsLanguage(player.language) {
                        KokoroPrepBanner(language: player.language, alwaysShowIdle: false)
                    } else {
                        LiveVoicePrepBanner(language: player.language)
                    }
                }
                speed(player)
                if reader != nil { sentenceActions() }
                tiles(player)
            }
        }
    }

    @ViewBuilder private func tiles(_ player: Player) -> some View {
        let items = Group {
            if castBook != nil {
                // 分角色 / 广播剧
                let detailText: String = if dramaOn {
                    String(localized: "广播剧开")
                } else if ownVoices == 0 {
                    String(localized: "都跟旁白")
                } else {
                    String(localized: "\(ownVoices) 人单独设")
                }
                Tile(icon: "person.2", title: String(localized: "人物声音"),
                     detail: detailText, on: dramaOn || ownVoices > 0) {
                    castShown = true
                }
                .help("开启广播剧、给书里的人物换声音、调快慢高低")
            }
            Tile(icon: "repeat.1", title: String(localized: "单句循环"), detail: loopDetail(player), on: player.loop) {
                player.loop.toggle()
            }
            Tile(icon: "pause.circle", title: String(localized: "念完停一下"), detail: pauseDetail(player.pauseAfter), on: player.pauseAfter != .off) {
                // 关 → 2 秒 → 按句长 → 关
                player.pauseAfter = switch player.pauseAfter {
                case .off: .seconds(2)
                case .seconds: .asLong
                case .asLong: .off
                }
            }
            .help("每念完一句停一会儿，跟着说一遍")
            if let listenOnly {
                Tile(icon: "eye.slash", title: String(localized: "只听"), detail: String(localized: "隐藏原文"), on: listenOnly.wrappedValue) {
                    listenOnly.wrappedValue.toggle()
                }
            }
        }
        #if os(iOS)
        // Phone width can't fit four icon+two-line tiles in one row without truncating labels; 2×2 stays legible.
        LazyVGrid(columns: [GridItem(.flexible(), spacing: 8), GridItem(.flexible(), spacing: 8)], spacing: 8) {
            items
        }
        #else
        HStack(spacing: 6) {
            items
        }
        #endif
    }

    private func countOwnVoices() {
        guard let castBook else { return }
        ownVoices = ((try? castBook.core.bookRoles(bookId: castBook.bookID)) ?? []).filter { $0.voice.voice != nil }.count
        dramaOn = DramaSynth.isDramaActive(castBook.core, castBook.bookID)
    }

    private func voice(_ player: Player) -> some View {
        let language = player.language
        let current = player.currentVoice(for: language)
        let preview = PreviewState.shared
        let previewLoading = current.map { preview.voiceID == $0.id && preview.started == nil } ?? false
        let previewPlaying = current.map { preview.voiceID == $0.id && preview.started != nil } ?? false
        let previewActive = previewLoading || previewPlaying
        let previewFailed = current.map { preview.failedID == $0.id } ?? false
        return VStack(alignment: .leading, spacing: 6) {
            HStack(spacing: 10) {
                // Animated speaking bars: they move while the voice reads or previews.
                SpeakingBars(paused: player.state != .playing && !previewPlaying)
                    .frame(width: 32, height: 32)
                    .background(YYColor.card, in: .rect(cornerRadius: 9))
                    .overlay(RoundedRectangle(cornerRadius: 9).strokeBorder(YYColor.line, lineWidth: 0.5))
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 5) {
                        Text(current.map(Voices.title) ?? String(localized: "没有能读的声音"))
                            .font(.yy(size: 12.5, weight: .semibold))
                            .foregroundStyle(YYColor.t1)
                        if current is SystemVoice {
                            Text("系统自带")
                                .font(.yy(size: 9.5, weight: .medium))
                                .foregroundStyle(YYColor.t2)
                                .padding(.horizontal, 5).padding(.vertical, 1)
                                .background(YYColor.t1.opacity(0.06), in: .rect(cornerRadius: 4))
                                .fixedSize()
                        }
                    }
                    Text(voiceLine(player, current))
                        .font(.yy(size: 10.5))
                        .foregroundStyle(YYColor.t2)
                }
                .lineLimit(1)
                Spacer(minLength: 4)
                if let current {
                    Button {
                        if previewActive {
                            Voices.stopPreview()
                        } else {
                            player.preview(current, text: sample(language))
                        }
                    } label: {
                        HStack(spacing: 4) {
                            Group {
                                if previewLoading {
                                    ProgressView()
                                        .controlSize(.mini)
                                        .scaleEffect(0.72)
                                        .tint(YYColor.t2)
                                } else if previewPlaying {
                                    Image(systemName: "stop.fill")
                                        .font(.yy(size: 8))
                                        .foregroundStyle(tint.accent)
                                } else {
                                    Image(systemName: "play.fill")
                                        .font(.yy(size: 8.5))
                                        .foregroundStyle(YYColor.t1)
                                }
                            }
                            .frame(width: 11, height: 11)
                            Text(previewPlaying ? String(localized: "停止") : String(localized: "试听"))
                                .foregroundStyle(previewLoading ? YYColor.t2 : YYColor.t1)
                        }
                        .font(.yy(size: 11, weight: .semibold))
                        .padding(.leading, 8).padding(.trailing, 10)
                        .frame(height: 26)
                        .background(YYColor.card, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                        .shadow(color: .black.opacity(0.05), radius: 1, y: 1)
                        .animation(YYMotion.quick, value: previewLoading)
                        .animation(YYMotion.quick, value: previewPlaying)
                    }
                    .buttonStyle(YYPressStyle(scale: 0.94))
                    .fixedSize()
                    .accessibilityLabel(previewLoading ? String(localized: "准备中") : previewPlaying ? String(localized: "停止试听") : String(localized: "试听"))
                }
                Button { voicePickerShown = true } label: {
                    HStack(spacing: 3) {
                        if player.readyOfflineVoice != nil {
                            Circle()
                                .fill(YYColor.ok)
                                .frame(width: 6, height: 6)
                        }
                        Text("换声音")
                        Image(systemName: "chevron.right").font(.yy(size: 9, weight: .semibold))
                    }
                    .font(.yy(size: 11))
                    .foregroundStyle(player.readyOfflineVoice != nil ? YYColor.t1 : YYColor.t2)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .fixedSize()
                .padding(.trailing, 2)
            }
            if previewFailed {
                Text(preview.failedMessage ?? String(localized: "试听暂不可用，请检查网络或稍后再试"))
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.danger)
                    .padding(.leading, 42)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
                    .transition(.opacity)
            }
        }
        .padding(.leading, 9).padding(.trailing, 8).padding(.vertical, 9)
        .background(YYColor.inset, in: .rect(cornerRadius: 14))
        .animation(YYMotion.quick, value: previewFailed)
    }

    /// Under the voice's name: while a better offline voice downloads and a fallback voice stands in,
    /// tell the user it will prompt when ready; otherwise 正在朗读 and where it reads from.
    private func voiceLine(_ player: Player, _ current: (any Voice)?) -> String {
        if let ready = player.readyOfflineVoice {
            return String(localized: "离线声音「\(Voices.title(ready))」已下好 · 点右侧换声音")
        }
        let isOfflineDownloading: Bool = switch (KokoroDownload.shared.phase, LiveVoiceDownload.shared.phase) {
        case (.downloading, _), (_, .downloading): true
        default: false
        }
        let isFallbackVoice = current is SystemVoice || (current as? CloudVoice)?.provider == .free
        if isFallbackVoice, !player.hasSavedVoice(for: player.language),
           CardVoice.supportsLanguage(player.language),
           isOfflineDownloading {
            return String(localized: "离线旁白下载中，下好后会提示你切换")
        }
        let parts = [player.state == .playing ? String(localized: "正在朗读") : nil,
                     languageName(player.language),
                     current?.isOffline == false ? String(localized: "云端（已读内容本地缓存）") : String(localized: "本地离线")]
        return parts.compactMap { $0 }.joined(separator: " · ")
    }

    /// 原声: a recording is laid against this book; off reads it in the voice below instead.
    private func narrator(_ player: Player) -> some View {
        HStack(spacing: 12) {
            Image(systemName: "person.wave.2")
                .font(.yy(size: 15, weight: .medium))
                .foregroundStyle(player.usesRecording ? tint.accent : YYColor.t1)
                .frame(width: 36, height: 36)
                .background(player.usesRecording ? tint.accentSoft : YYColor.card, in: .rect(cornerRadius: 10))
            VStack(alignment: .leading, spacing: 2) {
                Text("原声").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text(player.usesRecording ? String(localized: "播放有声书原声，缺失处自动补读；若高亮错位，选中正在念的句子点「对准」") : String(localized: "已关闭，使用下方朗读音色"))
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(2)
            }
            Spacer(minLength: 4)
            YYSwitch(isOn: Binding(get: { player.usesRecording }, set: { player.usesRecording = $0 }), label: String(localized: "原声"))
        }
        .padding(10)
        .background(YYColor.inset, in: .rect(cornerRadius: 14))
    }

    private func speed(_ player: Player) -> some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text("速度").font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text(rateText(rate) + "×").font(.yy(size: 17, weight: .bold)).foregroundStyle(YYColor.t1)
                Text("不变调")
                    .font(.yy(size: 10, weight: .medium))
                    .foregroundStyle(YYColor.t2)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 3)
                    .background(YYColor.inset, in: .rect(cornerRadius: 5))
                Spacer()
                Text("本\(unit)约剩 \(minutesLeft) 分钟")
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
            }
            .monospacedDigit()
            .padding(.horizontal, 2)
            VStack(spacing: 2) {
            Slider(value: Binding(get: { Double(rate) }, set: { rate = Float(($0 * 10).rounded() / 10) }), in: 0.8 ... 2.0)
                .controlSize(.small)
                .accessibilityLabel(Text("速度"))
                .accessibilityValue(Text(String(format: "%.1f×", rate)))
                .tint(YYTint.standard.fill)
            GeometryReader { g in
                ForEach(Self.rates, id: \.self) { mark in
                    Text(rateText(mark))
                        .font(.yy(size: 10, weight: abs(rate - mark) < 0.05 ? .bold : .regular))
                        .foregroundStyle(abs(rate - mark) < 0.05 ? YYColor.t1 : YYColor.t3)
                        .fixedSize()
                        .position(x: 8 + (g.size.width - 16) * CGFloat((mark - 0.8) / 1.2), y: 7)
                }
            }
            .frame(height: 14)
            }
            .padding(.horizontal, 9).padding(.top, 2)
        }
    }

    /// Where the sleep-timer chip row used to sit, between speed and the other tiles.
    /// The moon toggle now opens `sleepPage`; these keep the bookmark and waveform icons.
    @ViewBuilder private func sentenceActions() -> some View {
        HStack(spacing: 6) {
            Tile(icon: "bookmark", title: String(localized: "记下这句"), detail: String(localized: "划下了刚听到的这句"), on: false) {
                reader?.markCurrentHeard()
                dismissListen()
            }
            Tile(icon: "waveform", title: String(localized: "纠正读音"), detail: String(localized: "划选读错的字"), on: false) {
                reader?.promptPronunciation()
                dismissListen()
            }
        }
    }

    private func sleepToggleButton(_ player: Player) -> some View {
        let on = player.sleep != .off
        let highlighted = on || sleepOpen
        return Button {
            withAnimation(YYMotion.standard) { sleepOpen.toggle() }
        } label: {
            HStack(spacing: 4) {
                Image(systemName: on ? "moon.zzz.fill" : "moon.zzz")
                    .font(.yy(size: 13, weight: .medium))
                if let badge = sleepBadge(player) {
                    Text(badge)
                        .font(.yy(size: 11, weight: .semibold))
                        .monospacedDigit()
                }
            }
            .foregroundStyle(highlighted ? tint.accent : YYColor.t2)
            .padding(.horizontal, on ? 10 : 0)
            .frame(minWidth: 32, minHeight: 32)
            .background(highlighted ? tint.accentSoft : YYColor.inset, in: .capsule)
            .overlay(Capsule().strokeBorder(highlighted ? tint.accent.opacity(0.26) : YYColor.line, lineWidth: 0.5))
            .contentShape(Capsule().inset(by: -4))
            .animation(YYMotion.quick, value: highlighted)
            .animation(YYMotion.quick, value: on)
        }
        .buttonStyle(YYPressStyle(scale: 0.92))
        .fixedSize()
        .help(String(localized: "睡眠定时"))
        .accessibilityLabel(String(localized: "睡眠定时"))
        .accessibilityValue(sleepBadge(player) ?? String(localized: "关"))
    }

    private func sleepPage(_ player: Player) -> some View {
        let chapterTitle = unit == String(localized: "章")
            ? String(localized: "本章结束")
            : unit == String(localized: "集")
                ? String(localized: "本集结束")
                : String(localized: "本\(unit)读完")
        return VStack(alignment: .leading, spacing: 12) {
            ZStack {
                Text("睡眠定时")
                    .font(.yy(size: 14, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                HStack {
                    Button {
                        withAnimation(YYMotion.standard) { sleepOpen = false }
                    } label: {
                        HStack(spacing: 3) {
                            Image(systemName: "chevron.left")
                                .font(.yy(size: 12, weight: .semibold))
                            Text("听书")
                                .font(.yy(size: 13, weight: .medium))
                        }
                        .foregroundStyle(tint.accent)
                        .frame(minHeight: 32)
                        .contentShape(Rectangle().inset(by: -6))
                    }
                    .buttonStyle(YYPressStyle(scale: 0.94))
                    Spacer()
                    if player.sleep != .off {
                        Button {
                            #if os(iOS)
                            UISelectionFeedbackGenerator().selectionChanged()
                            #endif
                            withAnimation(YYMotion.standard) {
                                player.sleep = .off
                                sleepOpen = false
                            }
                        } label: {
                            Text("关闭")
                                .font(.yy(size: 13, weight: .medium))
                                .foregroundStyle(tint.accent)
                                .frame(minHeight: 32)
                                .contentShape(Rectangle().inset(by: -6))
                        }
                        .buttonStyle(YYPressStyle(scale: 0.94))
                    }
                }
            }
            .padding(.horizontal, 2)

            VStack(spacing: 0) {
                sleepOptionRow(player, title: String(localized: "本句读完"), detail: nil, value: .sentence)
                sleepDivider
                sleepOptionRow(player, title: chapterTitle, detail: String(localized: "约 \(minutesLeft) 分钟"), value: .chapter)
                ForEach([15, 30, 60], id: \.self) { m in
                    let left = minutesLeft(player, of: m)
                    sleepDivider
                    sleepOptionRow(
                        player,
                        title: String(localized: "\(m) 分钟"),
                        detail: left.map { String(localized: "还剩 \($0) 分") },
                        value: .until(Date().addingTimeInterval(Double(m) * 60), minutes: m)
                    )
                }
            }
            .background(YYColor.inset)
            .clipShape(.rect(cornerRadius: 14))
            .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line.opacity(0.6), lineWidth: 0.5))

            Text("停止时自动添加书签")
                .font(.yy(size: 11))
                .foregroundStyle(YYColor.t3)
                .padding(.horizontal, 4)
        }
    }

    private var sleepDivider: some View {
        Rectangle()
            .fill(YYColor.line)
            .frame(height: 0.5)
            .padding(.horizontal, 14)
    }

    private func sleepOptionRow(_ player: Player, title: String, detail: String?, value: Player.Sleep) -> some View {
        let on: Bool = switch (player.sleep, value) {
        case (.until(_, let a), .until(_, let b)): a == b
        default: player.sleep == value
        }
        return Button {
            #if os(iOS)
            UISelectionFeedbackGenerator().selectionChanged()
            #endif
            withAnimation(YYMotion.standard) {
                player.sleep = on ? .off : value
                sleepOpen = false
            }
        } label: {
            HStack(spacing: 8) {
                Text(title)
                    .font(.yy(size: 13.5, weight: on ? .semibold : .regular))
                    .foregroundStyle(on ? tint.accent : YYColor.t1)
                if let detail {
                    Text(detail)
                        .font(.yy(size: 11.5))
                        .foregroundStyle(on ? tint.accent.opacity(0.8) : YYColor.t3)
                        .monospacedDigit()
                }
                Spacer(minLength: 8)
                if on {
                    Image(systemName: "checkmark")
                        .font(.yy(size: 12.5, weight: .bold))
                        .foregroundStyle(tint.accent)
                }
            }
            .padding(.horizontal, 14)
            .frame(height: 44)
            .background(on ? tint.accentSoft : .clear)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    private func sleepBadge(_ player: Player) -> String? {
        switch player.sleep {
        case .off: nil
        case .sentence: String(localized: "本句")
        case .chapter:
            unit == String(localized: "章")
                ? String(localized: "本章")
                : unit == String(localized: "集")
                    ? String(localized: "本集")
                    : String(localized: "本\(unit)")
        case .until(let date, _):
            String(localized: "\(max(1, Int((date.timeIntervalSinceNow / 60).rounded(.up)))) 分")
        }
    }

    private func minutesLeft(_ player: Player, of minutes: Int) -> Int? {
        guard case .until(let date, let m) = player.sleep, m == minutes else { return nil }
        return max(1, Int((date.timeIntervalSinceNow / 60).rounded(.up)))
    }

    /// Minutes of listening left in the chapter at this speed (about 220 characters a minute at 1×).
    private static func minutesLeft(_ model: ReaderModel) -> Int {
        guard let section = model.section, let s = model.detail?.sections[safe: Int(section)] else { return 0 }
        let n = max(1, Int(s.sentences))
        let left = Double(s.units) * Double(n - model.sentence) / Double(n)
        return max(1, Int((left / 220 / Double(max(0.5, model.rate))).rounded()))
    }

    private func rateText(_ rate: Float) -> String {
        rate.formatted(.number.precision(.fractionLength(1)))
    }

    private func languageName(_ tag: String) -> String {
        let t = tag.lowercased()
        if t.hasPrefix("zh") { return t.contains("hant") || t.contains("tw") || t.contains("hk") ? String(localized: "繁体中文") : String(localized: "中文") }
        return Locale(identifier: "zh-Hans").localizedString(forLanguageCode: tag) ?? tag
    }

    private func sample(_ tag: String) -> String {
        if tag.lowercased().hasPrefix("zh") { return "你好，我来给你读这本书。" }
        return "Hello, I'll read this book to you."
    }
}

/// A key cap: 空格 · ⌘[.
private struct Key: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.yy(size: 10, weight: .semibold))
            .foregroundStyle(YYColor.t1)
            .padding(.horizontal, 4)
            .frame(minWidth: 18).frame(height: 17)
            .background(YYColor.inset, in: .rect(cornerRadius: 4))
            .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(YYColor.line, lineWidth: 0.5))
    }
}

/// 单句循环's line under it: 开 / 关, or the sentences a few-sentence loop goes over.
@MainActor private func loopDetail(_ player: Player) -> String {
    if let r = player.loopRange, player.loop { return String(localized: "第 \(r.first + 1)–\(r.last + 1) 句") }
    return player.loop ? String(localized: "开") : String(localized: "关")
}

private func pauseDetail(_ p: Player.PauseAfter) -> String {
    switch p {
    case .off: String(localized: "关")
    case .seconds(let s): String(localized: "\(Int(s)) 秒")
    case .asLong: String(localized: "按句长")
    }
}

private struct Tile: View {
    let icon: String
    let title: String
    let detail: String
    let on: Bool
    let action: () -> Void
    @Environment(\.isEnabled) private var enabled
    private let tint = YYTint.standard

    var body: some View {
        Button(action: action) {
            HStack(spacing: 9) {
                Image(systemName: icon).font(.yy(size: 14, weight: .medium)).frame(width: 16)
                VStack(alignment: .leading, spacing: 1) {
                    Text(title).font(.yy(size: 11.5, weight: .semibold))
                    Text(detail).font(.yy(size: 10)).foregroundStyle(on ? tint.accent : YYColor.t2)
                }
                .lineLimit(1)
                Spacer(minLength: 0)
            }
            .foregroundStyle(on ? tint.accent : YYColor.t1)
            .opacity(enabled ? 1 : 0.45)
            .padding(.horizontal, 10)
            .frame(height: 44)
            .frame(maxWidth: .infinity)
            .background(on ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: 12))
            .animation(YYMotion.quick, value: on)
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }
}

#if os(macOS)
// MARK: - 书伴

/// ✦ 书伴: ask about the book without leaving the page. Answers point back at the
/// sentences they lean on; a click on one turns the page there and marks it 出处.
struct CompanionPanel: View {
    @Bindable var model: ReaderModel
    @State private var draft = ""
    @State private var linesOpen = false
    @State private var modelPickerOpen = false
    @State private var slashManualOpen = false
    @State private var slashDismissedForDraft: String?
    @State private var slashIndex = 0
    @State private var composerHeight: CGFloat = 20
    @State private var focusRequest = 0
    /// The quote and the box: the ready lines and slash menu float just above them.
    @State private var askHeight: CGFloat = 76
    @State private var typing = false
    @State private var pickingThought = false
    @State private var pickedMessages: Set<BookCompanion.MessagePick> = []
    @State private var previewMerged = false
    @Environment(\.openSettings) private var openSettings
    @Environment(\.colorScheme) private var scheme
    @Environment(\.yyButtonOnPaper) private var onPaper
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var companion: BookCompanion { model.companion }
    private var focus: CompanionFocus { .shared }

    private var slashQuery: String? {
        if let q = CompanionCommand.slashQuery(in: draft) {
            return slashDismissedForDraft == draft ? nil : q
        }
        return slashManualOpen ? "" : nil
    }

    private var slashMatches: [CompanionCommand] {
        guard let q = slashQuery else { return [] }
        return CompanionCommand.matching(q)
    }

    private var slashMenuOpen: Bool { !slashMatches.isEmpty }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            mode.padding(.top, 12)
            Rectangle().fill(YYColor.line).frame(height: 1).padding(.top, 14)
            chat
            if pickingThought {
                pickBar.padding(.top, 10)
            } else {
                VStack(alignment: .leading, spacing: 0) {
                    if let quote = model.companionQuote { quoted(quote) }
                    input
                }
                .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { askHeight = $0 }
                .padding(.top, 10)
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 16)
        .padding(.bottom, 14)
        .overlay {
            if slashMenuOpen {
                Color.clear.contentShape(.rect).onTapGesture { dismissSlashMenu() }
                CompanionSlashCard(
                    commands: slashMatches,
                    selectedIndex: min(slashIndex, max(0, slashMatches.count - 1)),
                    currentScope: companion.scope,
                    busy: companion.busy,
                    accent: tint.accent,
                    accentSoft: tint.accentSoft,
                    onHover: { slashIndex = $0 },
                    onSelect: { executeCommand($0, argument: "") }
                )
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                .padding(.horizontal, 14)
                .padding(.bottom, 14 + askHeight + 8)
            } else if linesOpen, !readyQuestions.isEmpty {
                Color.clear.contentShape(.rect).onTapGesture { linesOpen = false }
                ReadyLinesCard {
                    ForEach(readyQuestions, id: \.self) { q in
                        QuickLine(line: q) { linesOpen = false; send(q) }
                    }
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .bottomLeading)
                .padding(.horizontal, 14)
                .padding(.bottom, 14 + askHeight + 8)
            }
        }
        .onChange(of: modelPickerOpen) { _, on in
            if on {
                linesOpen = false
                dismissSlashMenu()
            }
        }
        .onChange(of: linesOpen) { _, on in
            if on {
                modelPickerOpen = false
                dismissSlashMenu()
            }
        }
        .onChange(of: typing) { _, on in if on { linesOpen = false } }
        .onChange(of: draft) { _, new in
            if slashDismissedForDraft != new { slashDismissedForDraft = nil }
            if CompanionCommand.slashQuery(in: new) != nil {
                slashManualOpen = false
                linesOpen = false
                modelPickerOpen = false
            } else if !new.isEmpty {
                slashManualOpen = false
            }
        }
        .onChange(of: slashMatches.map(\.id)) { _, _ in
            slashIndex = 0
        }
        .onDisappear { model.pager.setSource(nil) }
        #if DEBUG
        .onAppear { if UserDefaults.standard.bool(forKey: "debugCompanionLines") { linesOpen = true } }
        #endif
    }

    private var allSelectablePicks: Set<BookCompanion.MessagePick> {
        var out = Set<BookCompanion.MessagePick>()
        for turn in companion.turns {
            if !turn.question.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                out.insert(.init(turnID: turn.id, role: .question))
            }
            if let a = turn.answer, !a.notYet {
                out.insert(.init(turnID: turn.id, role: .answer))
            }
        }
        return out
    }

    private func enterPicking(initial: BookCompanion.MessagePick? = nil) {
        linesOpen = false
        modelPickerOpen = false
        dismissSlashMenu()
        pickedMessages = initial.map { [$0] } ?? []
        previewMerged = false
        withAnimation(YYMotion.quick) { pickingThought = true }
    }

    private func exitPicking() {
        withAnimation(YYMotion.quick) {
            pickingThought = false
            previewMerged = false
            pickedMessages.removeAll()
        }
    }

    private func togglePick(_ pick: BookCompanion.MessagePick) {
        withAnimation(YYMotion.quick) {
            if pickedMessages.contains(pick) {
                pickedMessages.remove(pick)
                if pickedMessages.isEmpty { previewMerged = false }
            } else {
                pickedMessages.insert(pick)
            }
        }
    }

    private var header: some View {
        HStack(spacing: 8) {
            if pickingThought {
                let all = allSelectablePicks
                let allChosen = !all.isEmpty && pickedMessages == all
                Text(pickedMessages.isEmpty ? String(localized: "勾选要存的消息") : String(localized: "已选 \(pickedMessages.count) 条"))
                    .font(.yy(size: 13.5, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                    .monospacedDigit()
                Spacer()
                Button(allChosen ? String(localized: "取消全选") : String(localized: "全选")) {
                    withAnimation(YYMotion.quick) {
                        pickedMessages = allChosen ? [] : all
                        if allChosen { previewMerged = false }
                    }
                }
                .font(.yy(size: 11.5, weight: .medium))
                .foregroundStyle(tint.accent)
                .buttonStyle(.plain)
                Button(String(localized: "取消")) { exitPicking() }
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t2)
                    .buttonStyle(.plain)
            } else {
                Image(systemName: "sparkle").font(.yy(size: 12, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .frame(width: 24, height: 24)
                    .background(tint.accentSoft, in: .rect(cornerRadius: 7))
                Text("书伴").font(.yy(size: 14, weight: .bold)).foregroundStyle(YYColor.t1)
                Text(model.detail?.summary.title ?? "").font(.yy(size: 10.5)).foregroundStyle(YYColor.t3).lineLimit(1)
                Spacer()
                Menu {
                    Button("多选存为想法") { enterPicking() }.disabled(companion.turns.isEmpty)
                    Divider()
                    Button("清空对话") { companion.clear() }.disabled(companion.turns.isEmpty)
                    Button("书伴设置…") { openCompanionSettings() }
                } label: {
                    Image(systemName: "ellipsis").font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(YYColor.t2)
                        .frame(width: 26, height: 26)
                        .background(YYColor.inset, in: .circle)
                }
                .menuStyle(.button)
                .buttonStyle(.plain)
                .menuIndicator(.hidden)
                .fixedSize()
            }
        }
    }

    private var pickBar: some View {
        let previewText = previewMerged && !pickedMessages.isEmpty
            ? BookCompanion.composedThought(from: companion.turns, picks: pickedMessages)
            : ""
        return VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Text(String(localized: "可同时勾选你的提问与多条回答，合并存为 1 条想法"))
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
                Spacer(minLength: 4)
                if !pickedMessages.isEmpty {
                    Button(previewMerged ? String(localized: "收起预览") : String(localized: "预览合并")) {
                        withAnimation(YYMotion.quick) { previewMerged.toggle() }
                    }
                    .font(.yy(size: 11, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .buttonStyle(.plain)
                    .fixedSize()
                }
            }
            if !previewText.isEmpty {
                ScrollView {
                    Text(previewText)
                        .font(.yy(size: 11.5))
                        .lineSpacing(4)
                        .foregroundStyle(YYColor.t2)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .textSelection(.enabled)
                }
                .frame(maxHeight: 116)
                .padding(.horizontal, 10)
                .padding(.vertical, 8)
                .background(YYColor.inset, in: .rect(cornerRadius: 10))
                .transition(.opacity.combined(with: .move(edge: .bottom)))
            }
            HStack(spacing: 8) {
                Button(String(localized: "取消")) { exitPicking() }
                    .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                Spacer(minLength: 4)
                Button(pickedMessages.count > 1 ? String(localized: "合并存为想法 (\(pickedMessages.count))") : String(localized: "存为想法 (\(pickedMessages.count))")) {
                    if companion.saveThought(picks: pickedMessages) {
                        exitPicking()
                    }
                }
                .buttonStyle(YYButtonStyle(kind: .main, size: .small))
                .disabled(pickedMessages.isEmpty)
            }
        }
        .padding(10)
        .background(model.paperTokens.bg, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private var mode: some View {
        VStack(alignment: .leading, spacing: 6) {
            YYSegments(items: [(BookCompanion.Scope.read, String(localized: "仅限已读")), (.whole, String(localized: "全书范围"))],
                       selection: Bindable(companion).scope, small: true)
            Text(companion.scope == .read ? readUpTo + String(localized: "，不涉及后续情节") : String(localized: "检索整本书，可能包含后续剧透"))
                .font(.yy(size: 10.5)).foregroundStyle(YYColor.t3).padding(.horizontal, 2)
        }
    }

    private var readUpTo: String {
        if let page = model.bookPage { return String(localized: "截至第 \(page) 页") }
        return String(localized: "截至当前位置")
    }

    // MARK: Turns

    private var chat: some View {
        ScrollViewReader { proxy in
            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    if companion.turns.isEmpty { welcome.transition(.opacity) }
                    ForEach(companion.turns) { turn in
                        VStack(alignment: .leading, spacing: 10) {
                            selectableMessage(pick: .init(turnID: turn.id, role: .question), enabled: pickingThought) {
                                bubble(turn.question)
                            }
                            selectableMessage(
                                pick: .init(turnID: turn.id, role: .answer),
                                enabled: pickingThought && (turn.answer.map { !$0.notYet } ?? false)
                            ) {
                                reply(turn).transition(.opacity)
                            }
                        }
                        // 正在翻阅原文… gives way to the answer (or the failure) with a fade.
                        .animation(YYMotion.standard, value: turn.answer != nil || turn.failed != nil)
                        .transition(.opacity.combined(with: .offset(y: 6)))
                        .id(turn.id)
                    }
                }
                .animation(YYMotion.standard, value: companion.turns.map(\.id))
                .padding(.vertical, 14)
                .frame(maxWidth: .infinity, alignment: .leading)
            }
            .scrollIndicators(.never)
            .onChange(of: companion.turns.map(\.id)) {
                if let last = companion.turns.last { withAnimation(YYMotion.standard) { proxy.scrollTo(last.id, anchor: .bottom) } }
            }
            .onChange(of: companion.busy) {
                if let last = companion.turns.last { withAnimation(YYMotion.standard) { proxy.scrollTo(last.id, anchor: .bottom) } }
            }
            .onChange(of: companion.turns.last?.streaming) {
                if let last = companion.turns.last { proxy.scrollTo(last.id, anchor: .bottom) }
            }
        }
    }

    @ViewBuilder private var welcome: some View {
        let _ = focus.tick
        let config = CompanionConfig.current
        VStack(alignment: .leading, spacing: 10) {
            VStack(alignment: .leading, spacing: 6) {
                Text("读到哪儿问到哪儿").font(.yy(size: 13, weight: .semibold)).foregroundStyle(YYColor.t1)
                Text("可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，点一下就翻到那句；也可点回答下方的「存为想法」收进笔记。")
                    .font(.yy(size: 12)).foregroundStyle(YYColor.t2).lineSpacing(3)
            }
            .padding(14)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.inset, in: .rect(cornerRadius: 14))

            if !config.isReady {
                CompanionSetupCard(config: config, tint: tint, action: openCompanionSettings)
            }
        }
    }

    @ViewBuilder
    private func selectableMessage<Content: View>(
        pick: BookCompanion.MessagePick,
        enabled: Bool,
        @ViewBuilder content: () -> Content
    ) -> some View {
        if enabled {
            let selected = pickedMessages.contains(pick)
            Button { togglePick(pick) } label: {
                HStack(alignment: .top, spacing: 8) {
                    Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                        .font(.yy(size: 15, weight: .medium))
                        .foregroundStyle(selected ? tint.accent : YYColor.t3)
                        .padding(.top, 6)
                    content()
                        .allowsHitTesting(false)
                        .frame(maxWidth: .infinity, alignment: .leading)
                }
                .padding(.horizontal, 7)
                .padding(.vertical, 5)
                .background(selected ? tint.accentSoft.opacity(0.45) : .clear, in: .rect(cornerRadius: 12))
                .overlay(
                    RoundedRectangle(cornerRadius: 12)
                        .strokeBorder(selected ? tint.accent.opacity(0.32) : .clear, lineWidth: 1)
                )
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
        } else {
            content()
        }
    }

    private func bubble(_ text: String) -> some View {
        HStack {
            Spacer(minLength: 40)
            Text(text)
                .font(.yy(size: 12))
                .lineSpacing(3)
                .multilineTextAlignment(.leading)
                .foregroundStyle(YYColor.t1)
                .padding(.horizontal, 12).padding(.vertical, 8)
                .background {
                    YYShapeLift(shape: UnevenRoundedRectangle(topLeadingRadius: 14, bottomLeadingRadius: 14, bottomTrailingRadius: 4, topTrailingRadius: 14),
                                lift: .secondary(scheme: scheme, onPaper: onPaper, hover: false))
                }
                .textSelection(.enabled)
        }
    }

    @ViewBuilder private func reply(_ turn: BookCompanion.Turn) -> some View {
        if let answer = turn.answer {
            VStack(alignment: .leading, spacing: 6) {
                if answer.notYet {
                    Text("\(readUpTo)，这件事可能要到后面才写。切换至「全书范围」即可作答，但可能包含后续剧透。")
                        .font(.yy(size: 12.5)).lineSpacing(9).foregroundStyle(YYColor.t1)
                    Button { companion.ask(turn.question, quote: turn.quote, scope: .whole) } label: {
                        HStack(spacing: 8) {
                            Text("用全书回答").font(.yy(size: 11.5, weight: .semibold)).foregroundStyle(YYColor.t1)
                            Label("可能剧透", systemImage: "exclamationmark.triangle")
                                .font(.yy(size: 10, weight: .semibold)).foregroundStyle(YYColor.warn)
                        }
                        .padding(.horizontal, 12).frame(height: 28)
                        .background(YYColor.card, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line))
                    }
                    .buttonStyle(.plain)
                    .disabled(companion.busy)
                    .padding(.top, 3)
                } else {
                    Text(cited(answer.text, sources: answer.sources))
                        .font(.yy(size: 12.5))
                        .lineSpacing(9)
                        .foregroundStyle(YYColor.t1)
                        .textSelection(.enabled)
                        .environment(\.openURL, OpenURLAction { url in
                            if let n = Int(url.host() ?? ""), let source = answer.sources[n] { companion.show(source) }
                            return .handled
                        })
                    if !pickingThought {
                        HStack(spacing: 6) {
                            saveThoughtButton(turn)
                            copyAnswerButton(answer)
                            multiPickButton(turn)
                            Spacer(minLength: 0)
                        }
                        .padding(.top, 2)
                    }
                    HStack(spacing: 4) {
                        Image(systemName: turn.scope == .whole ? "book" : "sparkle").font(.yy(size: 9))
                        Text(turn.scope == .whole ? answer.label + String(localized: " · 用了全书") : answer.label)
                            .truncationMode(.middle)
                    }
                    .font(.yy(size: 10)).foregroundStyle(YYColor.t3)
                    .lineLimit(1)
                }
            }
        } else if let failed = turn.failed {
            VStack(alignment: .leading, spacing: 8) {
                Label(failed, systemImage: "exclamationmark.circle")
                    .font(.yy(size: 11.5)).foregroundStyle(YYColor.danger)
                if turn.id == companion.turns.last?.id, !CompanionConfig.current.isReady {
                    Button(action: openCompanionSettings) {
                        HStack(spacing: 5) {
                            Text("去配置模型")
                            Image(systemName: "chevron.right").font(.yy(size: 9.5, weight: .semibold))
                        }
                        .font(.yy(size: 11.5, weight: .medium)).foregroundStyle(YYColor.t1)
                        .padding(.horizontal, 11).frame(height: 27)
                        .background(YYColor.card, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                    }
                    .buttonStyle(.plain)
                }
            }
        } else if let streaming = turn.streaming, !streaming.isEmpty {
            Text(cited(streaming, sources: [:], streaming: true))
                .font(.yy(size: 12.5))
                .lineSpacing(9)
                .foregroundStyle(YYColor.t1)
                .textSelection(.enabled)
        } else {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text(turn.thinking ? String(localized: "正在思考…") : String(localized: "正在翻阅原文…"))
                    .font(.yy(size: 11.5)).foregroundStyle(YYColor.t3)
            }
        }
    }

    private func saveThoughtButton(_ turn: BookCompanion.Turn) -> some View {
        let saved = turn.savedMarkID != nil
        return Button { companion.saveThought(for: turn.id) } label: {
            HStack(spacing: 4) {
                Image(systemName: saved ? "checkmark" : "square.and.pencil")
                    .font(.yy(size: 10, weight: .semibold))
                Text(saved ? String(localized: "已存想法") : String(localized: "存为想法"))
                    .font(.yy(size: 11, weight: saved ? .semibold : .medium))
            }
            .foregroundStyle(saved ? YYColor.ok : YYColor.t1)
            .padding(.horizontal, 10)
            .frame(height: 25)
            .background(saved ? YYColor.ok.opacity(0.12) : YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(saved ? YYColor.ok.opacity(0.32) : YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
        .help(saved ? String(localized: "跳到这条想法") : String(localized: "把这段回答存进划线想法"))
    }

    private func copyAnswerButton(_ answer: BookCompanion.Answer) -> some View {
        Button {
            let clean = BookCompanion.noteText(from: answer.text)
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(clean, forType: .string)
            model.show(Library.Toast(icon: "checkmark", title: String(localized: "复制好了"), detail: ""))
        } label: {
            HStack(spacing: 4) {
                Image(systemName: "doc.on.doc")
                    .font(.yy(size: 9.5, weight: .medium))
                Text(String(localized: "复制"))
                    .font(.yy(size: 11, weight: .medium))
            }
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 9)
            .frame(height: 25)
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
        .help(String(localized: "复制回答内容"))
    }

    private func multiPickButton(_ turn: BookCompanion.Turn) -> some View {
        Button {
            enterPicking(initial: .init(turnID: turn.id, role: .answer))
        } label: {
            HStack(spacing: 3.5) {
                Image(systemName: "checklist")
                    .font(.yy(size: 9.5, weight: .medium))
                Text(String(localized: "多选"))
                    .font(.yy(size: 11, weight: .medium))
            }
            .foregroundStyle(YYColor.t2)
            .padding(.horizontal, 9)
            .frame(height: 25)
            .background(YYColor.card, in: .capsule)
            .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
        .fixedSize()
        .help(String(localized: "勾选你的提问和多条回答，合并存为一条想法"))
    }

    /// Renders Markdown (bold, lists, headings, inline code) and turns each `[n]` into a clickable `第 N 页` tag.
    private func cited(_ text: String, sources: [Int: BookCompanion.Source], streaming: Bool = false) -> AttributedString {
        var out = AttributedString()
        for segment in BookCompanion.richSegments(from: text, streaming: streaming) {
            switch segment {
            case .text(let s):
                out += AttributedString(s)
            case .bold(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 12.5, weight: .semibold)
                out += part
            case .italic(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 12.5).italic()
                out += part
            case .code(let s):
                var part = AttributedString(" \(s) ")
                part.font = .yy(size: 11.5, design: .monospaced)
                part.backgroundColor = YYColor.inset
                out += part
            case .strike(let s):
                var part = AttributedString(s)
                part.strikethroughStyle = .single
                part.foregroundColor = YYColor.t3
                out += part
            case .bullet:
                var dot = AttributedString("•\u{00A0}")
                dot.font = .yy(size: 12.5, weight: .bold)
                dot.foregroundColor = tint.accent
                out += dot
            case .quoteBar:
                var bar = AttributedString("▎\u{00A0}")
                bar.foregroundColor = YYColor.t3
                out += bar
            case .heading(let s):
                var part = AttributedString(s)
                part.font = .yy(size: 13, weight: .semibold)
                out += part
            case .citation(let n):
                if let source = sources[n] {
                    let place = source.page.map { String(localized: "第 \($0) 页") } ?? String(localized: "出处")
                    let label = " " + place + (source.later == true ? String(localized: " · 未读") : "") + " "
                    var tag = AttributedString(label.replacingOccurrences(of: " ", with: "\u{00A0}"))
                    tag.font = .yy(size: 10, weight: .semibold)
                    if source.later == true, companion.shown != source {
                        tag.foregroundColor = tint.accent
                        tag.underlineStyle = Text.LineStyle(pattern: .dash, color: tint.accent)
                    } else {
                        tag.foregroundColor = companion.shown == source ? YYColor.card : tint.accent
                        tag.backgroundColor = companion.shown == source ? tint.accent : tint.accentSoft
                    }
                    tag.link = URL(string: "yuyue-cite://\(n)")
                    out += AttributedString(" ") + tag
                }
            case .newline:
                out += AttributedString("\n")
            }
        }
        return out
    }

    // MARK: Asking

    private func quoted(_ quote: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Text(quote)
                .font(YYUI.serif(11.5))
                .foregroundStyle(YYColor.t2)
                .lineLimit(2)
                .padding(.leading, 8)
                .overlay(alignment: .leading) { Rectangle().fill(tint.accent).frame(width: 1.5) }
            Spacer(minLength: 0)
            Button { model.clearCompanionQuote() } label: {
                Image(systemName: "xmark").font(.yy(size: 9, weight: .bold)).foregroundStyle(YYColor.t3)
            }
            .buttonStyle(.plain)
            .help("不带这句")
        }
        .padding(.bottom, 8)
    }

    private var readyQuestions: [String] {
        let _ = focus.tick
        return CompanionPrompts.suggestions(
            hasQuote: model.companionQuote != nil,
            followUps: companion.turns.last?.answer?.next,
            quoteDefaults: [String(localized: "这句是什么意思？"), String(localized: "为什么这么写？")],
            startDefaults: [String(localized: "前面发生了什么？"), String(localized: "这一章讲了什么？")]
        )
    }

    private var parsedDraft: CompanionCommand.Parsed? { CompanionCommand.parse(draft) }

    private var canSubmitDraft: Bool {
        let trimmed = draft.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return false }
        if let parsed = parsedDraft, parsed.command.category == .control, parsed.argument.isEmpty {
            return true
        }
        return !companion.busy
    }

    private var input: some View {
        let placeholder = companion.turns.isEmpty
            ? String(localized: "问这本书，或输入 / …")
            : String(localized: "继续问，或输入 / …")
        return VStack(alignment: .leading, spacing: 6) {
            ZStack(alignment: .topLeading) {
                if draft.isEmpty {
                    Text(placeholder)
                        .font(.yy(size: 12))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 4)
                        .padding(.top, 2)
                        .allowsHitTesting(false)
                }
                CompanionComposerField(
                    text: $draft,
                    height: $composerHeight,
                    typing: $typing,
                    focusRequest: focusRequest,
                    accentColor: tint.accent,
                    scheme: scheme,
                    onReturn: { handleReturnKey() },
                    onArrow: { delta in handleSlashArrow(delta) },
                    onTab: { handleSlashTab() },
                    onEscape: { handleSlashEscape() }
                )
                .frame(height: composerHeight)
            }

            HStack(spacing: 4) {
                if !readyQuestions.isEmpty {
                    ReadyLinesToggle(open: $linesOpen, help: Text("挑一句现成的"))
                        .disabled(companion.busy)
                }
                SlashCommandToggle(open: slashMenuOpen, action: toggleSlashMenu)
                CompanionModelPill(open: $modelPickerOpen, openSettings: openCompanionSettings)
                    .layoutPriority(1)
                Spacer(minLength: 4)
                ViewThatFits(in: .horizontal) {
                    Text("⇧↩ 换行 · ↩ 发送")
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                    Text("⇧↩ 换行")
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                    Text("⇧↩")
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }
                .help(String(localized: "Shift+Return 换行 · Return 发送"))
                Button { submitDraft() } label: {
                    Image(systemName: "arrow.up").font(.yy(size: 11.5, weight: .bold))
                        .foregroundStyle(tint.onBlack)
                        .frame(width: 28, height: 28)
                        .background(tint.black, in: .rect(cornerRadius: 9))
                }
                .buttonStyle(.plain)
                .opacity(canSubmitDraft ? 1 : 0.32)
                .disabled(!canSubmitDraft)
                .help(String(localized: "发送 ↩"))
                .accessibilityLabel(Text("发送"))
            }
        }
        .padding(.horizontal, 8)
        .padding(.top, 9)
        .padding(.bottom, 6)
        .background(model.paperTokens.bg, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
    }

    private func toggleSlashMenu() {
        linesOpen = false
        modelPickerOpen = false
        if slashMenuOpen {
            if draft == "/" || draft == "／" { draft = "" }
            dismissSlashMenu()
            return
        }
        slashDismissedForDraft = nil
        if draft.isEmpty {
            draft = "/"
            focusRequest += 1
        } else {
            slashManualOpen = true
            focusRequest += 1
        }
    }

    private func dismissSlashMenu() {
        slashManualOpen = false
        if CompanionCommand.slashQuery(in: draft) != nil {
            slashDismissedForDraft = draft
        }
    }

    private func handleReturnKey() {
        if slashMenuOpen {
            let index = min(max(0, slashIndex), slashMatches.count - 1)
            executeCommand(slashMatches[index], argument: "")
            return
        }
        submitDraft()
    }

    private func handleSlashArrow(_ delta: Int) -> Bool {
        guard slashMenuOpen else { return false }
        let count = slashMatches.count
        slashIndex = (slashIndex + delta + count) % count
        return true
    }

    private func handleSlashTab() -> Bool {
        guard slashMenuOpen else { return false }
        let index = min(max(0, slashIndex), slashMatches.count - 1)
        let cmd = slashMatches[index]
        slashManualOpen = false
        slashDismissedForDraft = nil
        draft = cmd.takesArgument ? cmd.slashName + " " : cmd.slashName
        focusRequest += 1
        return true
    }

    private func handleSlashEscape() -> Bool {
        guard slashMenuOpen else { return false }
        dismissSlashMenu()
        return true
    }

    private func submitDraft() {
        if let parsed = CompanionCommand.parse(draft) {
            executeCommand(parsed.command, argument: parsed.argument)
            return
        }
        send(draft)
    }

    private func executeCommand(_ command: CompanionCommand, argument: String) {
        let arg = argument.trimmingCharacters(in: .whitespacesAndNewlines)
        switch command.action {
        case .recap, .chapter, .characters, .explain, .quote, .quiz, .custom:
            guard !companion.busy else { return }
            if let q = command.question(argument: arg, hasQuote: model.companionQuote != nil, chapterTitle: model.chapterTitle) {
                slashManualOpen = false
                slashDismissedForDraft = nil
                send(q, action: command.action)
            }
        case .scope(let target):
            slashManualOpen = false
            slashDismissedForDraft = nil
            companion.scope = target
            draft = ""
            if !arg.isEmpty, !companion.busy {
                companion.ask(arg, quote: model.companionQuote, scope: target, anchor: model.companionAnchor)
                model.clearCompanionQuote()
            }
        case .model:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            if CompanionConfig.current.isUnconfiguredFallback {
                openCompanionSettings()
            } else {
                modelPickerOpen = true
            }
        case .clear:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            companion.clear()
        case .settings:
            slashManualOpen = false
            slashDismissedForDraft = nil
            draft = ""
            openCompanionSettings()
        }
    }

    private func send(_ text: String, action: CompanionCommand.Action? = nil) {
        let q = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, !companion.busy else { return }
        companion.ask(q, quote: model.companionQuote, anchor: model.companionAnchor, action: action)
        draft = ""
        slashManualOpen = false
        slashDismissedForDraft = nil
        model.clearCompanionQuote()
    }

    private func openCompanionSettings() {
        UserDefaults.standard.set(SettingsPage.companion.rawValue, forKey: "settings.page")
        openSettings()
    }
}

/// Clean elevated setup call-to-action card shown below the intro block when no AI model is ready.
private struct CompanionSetupCard: View {
    let config: CompanionConfig
    let tint: YYTint
    let action: () -> Void
    @Environment(\.colorScheme) private var scheme
    @State private var hover = false

    private var title: String {
        if config.isUnconfiguredFallback {
            String(localized: "添加 AI 服务商即可开始")
        } else if config.provider == .apple {
            String(localized: "开启或添加 AI 服务商")
        } else {
            String(localized: "完成 \(config.provider.title) 配置")
        }
    }

    private var subtitle: String {
        if config.isUnconfiguredFallback {
            String(localized: "支持 DeepSeek、通义千问、Claude、OpenAI、Ollama 等")
        } else if config.provider == .apple {
            Companion.appleAvailability ?? String(localized: "开启 Apple 智能，或添加其他服务商")
        } else {
            String(localized: "填入 API Key 后即可开始问答")
        }
    }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 11) {
                if config.provider == .apple {
                    Image(systemName: "sparkles")
                        .font(.yy(size: 13, weight: .semibold))
                        .foregroundStyle(tint.accent)
                        .frame(width: 30, height: 30)
                        .background(tint.accentSoft, in: .rect(cornerRadius: 9))
                } else {
                    ServiceTile(mark: .provider(config.provider), size: 30)
                }

                VStack(alignment: .leading, spacing: 2.5) {
                    Text(title)
                        .font(.yy(size: 12.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    Text(subtitle)
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t3)
                        .lineSpacing(2)
                        .multilineTextAlignment(.leading)
                        .fixedSize(horizontal: false, vertical: true)
                }
                .frame(maxWidth: .infinity, alignment: .leading)

                HStack(spacing: 3) {
                    Text("去配置")
                        .font(.yy(size: 11.5, weight: .semibold))
                    Image(systemName: "chevron.right")
                        .font(.yy(size: 9.5, weight: .semibold))
                }
                .foregroundStyle(tint.accent)
                .fixedSize()
            }
            .padding(.horizontal, 13)
            .padding(.vertical, 12)
            .background(hover ? YYColor.inset : YYColor.card, in: .rect(cornerRadius: 14))
            .overlay(
                RoundedRectangle(cornerRadius: 14)
                    .strokeBorder(hover ? tint.accent.opacity(0.32) : YYColor.line, lineWidth: 0.5)
            )
            .shadow(color: .black.opacity(scheme == .dark ? 0.18 : 0.04), radius: 4, y: 1.5)
            .contentShape(.rect(cornerRadius: 14))
        }
        .buttonStyle(YYPressStyle(scale: 0.985))
        .onHover { hover = $0 }
        .animation(YYMotion.quick, value: hover)
    }
}

/// `/` trigger button beside `“` in 书伴's input bar.
private struct SlashCommandToggle: View {
    let open: Bool
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            Text("/")
                .font(.system(size: YYUI.s(12.5), weight: .semibold, design: .monospaced))
                .foregroundStyle(open ? YYColor.t1 : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(open || hover ? YYColor.inset : .clear, in: .circle)
                .contentShape(.circle)
        }
        .buttonStyle(.plain)
        .onHover { hover = $0 }
        .help(String(localized: "快捷指令 (/)"))
        .accessibilityLabel(Text("快捷指令"))
    }
}

/// Floating `/` command palette above 书伴's composer.
private struct CompanionSlashCard: View {
    let commands: [CompanionCommand]
    let selectedIndex: Int
    let currentScope: BookCompanion.Scope
    let busy: Bool
    let accent: Color
    let accentSoft: Color
    let onHover: (Int) -> Void
    let onSelect: (CompanionCommand) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Text("指令")
                    .font(.yy(size: 10.5, weight: .semibold))
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 4)
                ViewThatFits(in: .horizontal) {
                    Text("↑↓ 选择 · Tab 补全 · ↩ 执行")
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                    Text("↑↓ · ↩ 执行")
                        .font(.yy(size: 10))
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }
            }
            .padding(.horizontal, 10)
            .padding(.top, 7)
            .padding(.bottom, 5)

            Rectangle().fill(YYColor.line).frame(height: 0.5)

            ScrollViewReader { proxy in
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 2) {
                        ForEach(Array(commands.enumerated()), id: \.element.id) { index, cmd in
                            let selected = index == selectedIndex
                            let disabled = busy && cmd.category == .ask
                            let isCurrentScope: Bool = {
                                if case .scope(let s) = cmd.action { return s == currentScope }
                                return false
                            }()
                            Button {
                                if !disabled { onSelect(cmd) }
                            } label: {
                                HStack(spacing: 8) {
                                    Image(systemName: cmd.icon)
                                        .font(.yy(size: 11.5, weight: .medium))
                                        .foregroundStyle(selected ? accent : YYColor.t2)
                                        .frame(width: 24, height: 24)
                                        .background(selected ? accentSoft : YYColor.inset, in: .rect(cornerRadius: 6))

                                    VStack(alignment: .leading, spacing: 1) {
                                        HStack(spacing: 5) {
                                            Text(cmd.slashName)
                                                .font(.system(size: YYUI.s(11.5), weight: .semibold, design: .monospaced))
                                                .foregroundStyle(selected ? accent : YYColor.t1)
                                                .fixedSize()
                                            Text(cmd.title)
                                                .font(.yy(size: 11.5, weight: .medium))
                                                .foregroundStyle(YYColor.t1)
                                                .lineLimit(1)
                                        }
                                        Text(cmd.subtitle)
                                            .font(.yy(size: 10.5))
                                            .foregroundStyle(YYColor.t3)
                                            .lineLimit(1)
                                    }

                                    Spacer(minLength: 4)

                                    if isCurrentScope {
                                        Text("当前")
                                            .font(.yy(size: 9.5, weight: .medium))
                                            .foregroundStyle(accent)
                                            .padding(.horizontal, 5)
                                            .padding(.vertical, 2)
                                            .background(accentSoft, in: .capsule)
                                    } else if selected && !disabled {
                                        Text("↩")
                                            .font(.yy(size: 10.5, weight: .medium))
                                            .foregroundStyle(YYColor.t3)
                                    }
                                }
                                .padding(.horizontal, 8)
                                .padding(.vertical, 6)
                                .background(selected ? YYColor.t1.opacity(0.06) : .clear, in: .rect(cornerRadius: 8))
                                .opacity(disabled ? 0.42 : 1)
                                .contentShape(.rect)
                            }
                            .buttonStyle(.plain)
                            .onHover { hovering in if hovering { onHover(index) } }
                            .id(cmd.id)
                        }
                    }
                    .padding(5)
                }
                .scrollIndicators(.never)
                .frame(maxHeight: 248)
                .onChange(of: selectedIndex) { _, new in
                    if commands.indices.contains(new) {
                        proxy.scrollTo(commands[new].id)
                    }
                }
            }
        }
        .background(YYColor.card, in: .rect(cornerRadius: 12))
        .ring(YYColor.line, radius: 12)
        .shadow(color: Color(yy: YYRGBA(40, 36, 32, 0.16)), radius: 16, y: 12)
    }
}

/// Multiline auto-growing NSTextView for Mac 书伴:
/// - `Return` (or `⌘↩`) sends or runs the highlighted `/` command.
/// - `Shift+Return` / `Option+Return` / `Control+Return` inserts a newline.
/// - `↑`/`↓`/`Tab`/`Esc` drive the `/` palette without interfering with Chinese IME composition.
private struct CompanionComposerField: NSViewRepresentable {
    @Binding var text: String
    @Binding var height: CGFloat
    @Binding var typing: Bool
    let focusRequest: Int
    let accentColor: Color
    let scheme: ColorScheme
    let onReturn: () -> Void
    let onArrow: (Int) -> Bool
    let onTab: () -> Bool
    let onEscape: () -> Bool

    func makeCoordinator() -> Coordinator {
        Coordinator(
            text: $text,
            height: $height,
            typing: $typing,
            onReturn: onReturn,
            onArrow: onArrow,
            onTab: onTab,
            onEscape: onEscape
        )
    }

    func makeNSView(context: Context) -> CompanionComposerScrollView {
        let scroll = CompanionComposerScrollView()
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.automaticallyAdjustsContentInsets = false
        scroll.contentInsets = NSEdgeInsets()

        let storage = NSTextStorage()
        let layoutManager = NSLayoutManager()
        storage.addLayoutManager(layoutManager)
        let container = NSTextContainer(containerSize: NSSize(width: 200, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        container.lineFragmentPadding = 4
        layoutManager.addTextContainer(container)

        let tv = CompanionNSTextView(frame: .zero, textContainer: container)
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.isHorizontallyResizable = false
        tv.isVerticallyResizable = true
        tv.autoresizingMask = [.width]
        tv.allowsUndo = true
        tv.isRichText = false
        tv.importsGraphics = false
        tv.drawsBackground = false
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.textContainerInset = NSSize(width: 0, height: 2)
        tv.delegate = context.coordinator

        let font = NSFont.systemFont(ofSize: YYUI.s(12))
        let ink = NSColor(YYColor.t1)
        tv.font = font
        tv.textColor = ink
        tv.typingAttributes = [.font: font, .foregroundColor: ink]
        tv.insertionPointColor = NSColor(accentColor)
        tv.string = text

        tv.onFocusChange = { [weak coordinator = context.coordinator] focused in
            DispatchQueue.main.async {
                guard let coordinator, coordinator.typing.wrappedValue != focused else { return }
                coordinator.typing.wrappedValue = focused
            }
        }
        tv.onCommandReturn = { [weak coordinator = context.coordinator] in
            coordinator?.onReturn()
        }

        scroll.documentView = tv
        scroll.onLayout = { [weak coordinator = context.coordinator, weak tv] in
            guard let tv else { return }
            coordinator?.recalculateHeight(tv)
        }
        context.coordinator.recalculateHeight(tv)
        return scroll
    }

    func updateNSView(_ scroll: CompanionComposerScrollView, context: Context) {
        let coordinator = context.coordinator
        coordinator.text = $text
        coordinator.height = $height
        coordinator.typing = $typing
        coordinator.onReturn = onReturn
        coordinator.onArrow = onArrow
        coordinator.onTab = onTab
        coordinator.onEscape = onEscape

        guard let tv = scroll.documentView as? CompanionNSTextView else { return }
        let font = NSFont.systemFont(ofSize: YYUI.s(12))
        let ink = NSColor(YYColor.t1)
        if tv.font != font { tv.font = font }
        tv.textColor = ink
        tv.typingAttributes = [.font: font, .foregroundColor: ink]
        tv.insertionPointColor = NSColor(accentColor)

        if tv.string != text, !tv.hasMarkedText() {
            tv.string = text
            let end = (text as NSString).length
            tv.setSelectedRange(NSRange(location: end, length: 0))
            coordinator.recalculateHeight(tv)
            tv.scrollRangeToVisible(NSRange(location: end, length: 0))
        }

        if focusRequest != coordinator.lastFocusRequest {
            coordinator.lastFocusRequest = focusRequest
            DispatchQueue.main.async {
                guard let window = scroll.window else { return }
                if window.firstResponder !== tv {
                    window.makeFirstResponder(tv)
                }
                let end = (tv.string as NSString).length
                tv.setSelectedRange(NSRange(location: end, length: 0))
            }
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var text: Binding<String>
        var height: Binding<CGFloat>
        var typing: Binding<Bool>
        var onReturn: () -> Void
        var onArrow: (Int) -> Bool
        var onTab: () -> Bool
        var onEscape: () -> Bool
        var lastFocusRequest = 0
        private var lastReportedHeight: CGFloat = 0

        init(
            text: Binding<String>,
            height: Binding<CGFloat>,
            typing: Binding<Bool>,
            onReturn: @escaping () -> Void,
            onArrow: @escaping (Int) -> Bool,
            onTab: @escaping () -> Bool,
            onEscape: @escaping () -> Bool
        ) {
            self.text = text
            self.height = height
            self.typing = typing
            self.onReturn = onReturn
            self.onArrow = onArrow
            self.onTab = onTab
            self.onEscape = onEscape
        }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            if text.wrappedValue != tv.string {
                text.wrappedValue = tv.string
            }
            recalculateHeight(tv)
            tv.scrollRangeToVisible(tv.selectedRange())
        }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if textView.hasMarkedText() { return false }

            switch commandSelector {
            case #selector(NSResponder.insertNewline(_:)):
                let flags = NSApp.currentEvent?.modifierFlags.intersection(.deviceIndependentFlagsMask) ?? []
                if flags.contains(.shift) || flags.contains(.option) || flags.contains(.control) {
                    textView.insertNewlineIgnoringFieldEditor(nil)
                    return true
                }
                onReturn()
                return true

            case #selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)):
                return false

            case #selector(NSResponder.moveUp(_:)):
                return onArrow(-1)

            case #selector(NSResponder.moveDown(_:)):
                return onArrow(1)

            case #selector(NSResponder.insertTab(_:)):
                return onTab()

            case #selector(NSResponder.cancelOperation(_:)):
                return onEscape()

            default:
                return false
            }
        }

        func recalculateHeight(_ tv: NSTextView) {
            guard let layoutManager = tv.layoutManager,
                  let textContainer = tv.textContainer
            else { return }
            let font = tv.font ?? NSFont.systemFont(ofSize: YYUI.s(12))
            let lineHeight = ceil(font.ascender - font.descender + font.leading)
            let insetY = tv.textContainerInset.height
            let minH = lineHeight + insetY * 2
            let maxH = lineHeight * 5 + insetY * 2

            layoutManager.ensureLayout(for: textContainer)
            let used = layoutManager.usedRect(for: textContainer)
            let extra = layoutManager.extraLineFragmentRect
            let contentH = max(used.maxY, extra.maxY)
            let clamped = min(max(ceil(contentH + insetY * 2), minH), maxH)

            guard abs(clamped - lastReportedHeight) > 0.5 else { return }
            lastReportedHeight = clamped
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                if abs(self.height.wrappedValue - clamped) > 0.5 {
                    self.height.wrappedValue = clamped
                }
            }
        }
    }
}

private final class CompanionComposerScrollView: NSScrollView {
    var onLayout: (() -> Void)?
    private var lastWidth: CGFloat = 0

    override func layout() {
        super.layout()
        let targetWidth = contentSize.width
        if let tv = documentView as? NSTextView, targetWidth > 0, abs(tv.frame.width - targetWidth) > 0.5 {
            tv.frame.size.width = targetWidth
            tv.textContainer?.containerSize.width = targetWidth
        }
        if abs(bounds.width - lastWidth) > 0.5 {
            lastWidth = bounds.width
            onLayout?()
        }
    }
}

private final class CompanionNSTextView: NSTextView {
    var onFocusChange: ((Bool) -> Void)?
    var onCommandReturn: (() -> Void)?

    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok { onFocusChange?(true) }
        return ok
    }

    override func resignFirstResponder() -> Bool {
        let ok = super.resignFirstResponder()
        if ok { onFocusChange?(false) }
        return ok
    }

    override func keyDown(with event: NSEvent) {
        if !hasMarkedText(),
           event.keyCode == 36 || event.keyCode == 76,
           event.modifierFlags.intersection(.deviceIndependentFlagsMask) == .command
        {
            onCommandReturn?()
            return
        }
        super.keyDown(with: event)
    }
}

#endif

// MARK: - 只听

/// The page covered while 只听 is on: 100% preserves the original quiet minimalist composition.
/// Tapping the empty paper softly crossfades to depth-of-field 3-line lyrics (tap again to return
/// to stillness); marking a sentence blooms a subtle golden ink ripple across the paper.
struct ListenOnlyCover: View {
    let model: ReaderModel
    @State private var showLyrics = false
    @State private var rippleScale: CGFloat = 0.25
    @State private var rippleOpacity: Double = 0

    var body: some View {
        let paper = model.paperTokens
        let lines = model.focusListeningLines()

        ZStack {
            paper.bg.ignoresSafeArea()

            // Subtle golden ink ripple when a sentence is marked via the morphing capsule.
            Circle()
                .fill(
                    RadialGradient(
                        colors: [paper.hl.opacity(paper.isDark ? 0.42 : 0.65), .clear],
                        center: .center,
                        startRadius: 0,
                        endRadius: 150
                    )
                )
                .frame(width: 280, height: 280)
                .scaleEffect(rippleScale)
                .opacity(rippleOpacity)
                .allowsHitTesting(false)

            // Default serene original composition (untouched!)
            VStack(spacing: 14) {
                Image(systemName: "headphones")
                    .font(.yy(size: 34, weight: .light))
                    .foregroundStyle(paper.ink2)
                Text(model.chapterTitle ?? model.detail?.summary.title ?? "")
                    .font(YYUI.serif(17, .semibold))
                    .foregroundStyle(paper.ink)
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 24)
                Text("纯听模式 · 已隐藏正文")
                    .font(.yy(size: 12))
                    .foregroundStyle(paper.ink2)
                Button("显示原文") { model.listenOnly = false }
                    .buttonStyle(YYButtonStyle(kind: .raised, size: .small))
                    .padding(.top, 6)
            }
            .opacity(showLyrics ? 0 : 1)
            .scaleEffect(showLyrics && !YYMotion.reduce ? 0.965 : 1)
            .blur(radius: showLyrics && !YYMotion.reduce ? 4 : 0)
            .allowsHitTesting(!showLyrics)

            // Tap-to-reveal depth-of-field 3-line literary lyrics
            VStack(spacing: 18) {
                if let ch = model.chapterTitle ?? model.detail?.summary.title, !ch.isEmpty {
                    Text(ch)
                        .font(.yy(size: 11))
                        .tracking(1.4)
                        .foregroundStyle(paper.ink2.opacity(0.65))
                        .lineLimit(1)
                }
                if let prev = lines.prev {
                    Text(prev)
                        .font(YYUI.serif(14.5))
                        .foregroundStyle(paper.ink2.opacity(0.38))
                        .blur(radius: YYMotion.reduce ? 0 : 0.8)
                        .lineLimit(2)
                        .multilineTextAlignment(.center)
                }
                Text(lines.current ?? String(localized: "轻按下方播放开始听书"))
                    .font(YYUI.serif(20, .medium))
                    .foregroundStyle(paper.ink)
                    .lineSpacing(7)
                    .lineLimit(4)
                    .multilineTextAlignment(.center)
                    .contentTransition(.opacity)
                    .animation(YYMotion.standard, value: lines.current)
                if let next = lines.next {
                    Text(next)
                        .font(YYUI.serif(14.5))
                        .foregroundStyle(paper.ink2.opacity(0.38))
                        .blur(radius: YYMotion.reduce ? 0 : 0.8)
                        .lineLimit(2)
                        .multilineTextAlignment(.center)
                }
                Text("再点一次纸面归于留白")
                    .font(.yy(size: 11))
                    .foregroundStyle(paper.ink2.opacity(0.5))
                    .padding(.top, 4)
            }
            .frame(maxWidth: 500)
            .padding(.horizontal, 32)
            .padding(.bottom, 48)
            .opacity(showLyrics ? 1 : 0)
            .scaleEffect(showLyrics || YYMotion.reduce ? 1 : 0.965)
            .blur(radius: showLyrics || YYMotion.reduce ? 0 : 4)
            .allowsHitTesting(showLyrics)
        }
        .contentShape(Rectangle())
        .onTapGesture {
            if model.capsuleMode != .idle {
                model.dismissCapsuleMode()
                return
            }
            withAnimation(YYMotion.reduce ? .easeOut(duration: 0.16) : .spring(duration: 0.34, bounce: 0)) {
                showLyrics.toggle()
            }
        }
        .onChange(of: model.listenRippleToken) { _, _ in
            guard !YYMotion.reduce else { return }
            rippleScale = 0.25
            rippleOpacity = 0.7
            withAnimation(.easeOut(duration: 0.76)) {
                rippleScale = 2.4
                rippleOpacity = 0
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .environment(\.yyButtonOnPaper, paper.id == "sage")
    }
}
