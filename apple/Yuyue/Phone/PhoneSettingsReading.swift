#if os(iOS)
import SwiftUI
@preconcurrency import Translation
import YuyueCore

// MARK: - 外观

/// 外观: light, dark or the system's; 墨蓝 or 墨黑; and a look at both.
struct PhoneAppearance: View {
    @AppStorage(AppAppearance.key) private var mode = AppAppearance.system
    @AppStorage("shelf.showProgress") private var showProgress = true
    @AppStorage("shelf.coverSize") private var coverSize = 100.0
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @AppStorage(LiquidGlass.key) private var liquid = false
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    var body: some View {
        YYPage {
            PhoneSettingsLead(String(localized: "应用界面的外观与配色；书页纸色在「阅读」或 Aa 中设置"))
                .padding(.bottom, YYUI.s(12))
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(10)) {
                    Text("界面").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                    HStack(spacing: YYUI.s(10)) {
                        ForEach(AppAppearance.allCases) { m in modeTile(m) }
                    }
                }
                .padding(.vertical, YYUI.s(12))
            }
            .padding(.bottom, YYUI.s(12))
            YYGroup {
                HStack(alignment: .center) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("主题色").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("用于按钮、开关与进度标记").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: YYUI.s(6))
                    HStack(spacing: YYUI.s(6)) { ForEach(YYTint.all) { t in tintChoice(t) } }
                }
                .padding(.vertical, YYUI.s(12))
            }
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(8)) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("书架封面大小").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("随屏幕宽度自动排布").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    HStack(spacing: YYUI.s(10)) {
                        RoundedRectangle(cornerRadius: 1.5).fill(YYColor.tint).frame(width: 8, height: 11)
                        Slider(value: $coverSize, in: 80 ... 130, step: 10).tint(YYTint.standard.fill)
                            .accessibilityLabel(Text("书架封面大小"))
                        RoundedRectangle(cornerRadius: 2).fill(YYColor.tint).frame(width: 11, height: 16)
                    }
                }
                .padding(.vertical, YYUI.s(10))
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(2)) {
                        Text("封面下显示进度").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                        Text("关闭后仅显示书名").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $showProgress, label: String(localized: "封面下显示进度"))
                }
                .padding(.vertical, YYUI.s(10))
            }
            .padding(.top, YYUI.s(12))
            if LiquidGlass.supported {
                YYGroup {
                    HStack(spacing: YYUI.s(10)) {
                        VStack(alignment: .leading, spacing: YYUI.s(2)) {
                            Text("液态玻璃效果").font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                            Text("浮动工具栏与提示条使用系统液态玻璃，关闭则为毛玻璃").font(YYUI.font(10)).foregroundStyle(YYColor.t2)
                                .fixedSize(horizontal: false, vertical: true)
                        }
                        Spacer(minLength: YYUI.s(8))
                        YYSwitch(isOn: $liquid, label: String(localized: "液态玻璃效果"))
                    }
                    .padding(.vertical, YYUI.s(10))
                }
                .padding(.top, YYUI.s(12))
            }
        }
        .navigationTitle("外观")
        .navigationBarTitleDisplayMode(.inline)
        .animation(YYMotion.quick, value: tintID)
        .animation(YYMotion.standard, value: mode)
    }

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A little page in light, dark, or both split down a slant; the chosen one ringed.
    private func modeTile(_ m: AppAppearance) -> some View {
        let on = m == mode
        let light = (bg: Color(yy: YYRGBA(246, 245, 243)), bar: Color.white, title: Color(yy: YYRGBA(42, 43, 46)).opacity(0.75))
        let dark = (bg: Color(yy: YYRGBA(16, 17, 18)), bar: Color(yy: YYRGBA(27, 28, 30)), title: Color(yy: YYRGBA(237, 237, 238)).opacity(0.75))
        func page(_ c: (bg: Color, bar: Color, title: Color)) -> some View {
            ZStack(alignment: .topLeading) {
                c.bg
                GeometryReader { g in
                    VStack(alignment: .leading, spacing: YYUI.s(5)) {
                        RoundedRectangle(cornerRadius: 3).fill(c.title).frame(width: g.size.width * 0.55, height: YYUI.s(7)).padding(.bottom, YYUI.s(3))
                        RoundedRectangle(cornerRadius: 3).fill(c.bar).frame(height: YYUI.s(9))
                        RoundedRectangle(cornerRadius: 3).fill(c.bar).frame(height: YYUI.s(9))
                    }
                }
                .padding(.top, YYUI.s(13)).padding(.horizontal, YYUI.s(11))
            }
        }
        return Button { mode = m } label: {
            VStack(spacing: YYUI.s(7)) {
                ZStack {
                    switch m {
                    case .light: page(light)
                    case .dark: page(dark)
                    case .system:
                        page(light)
                        page(dark).mask(Slant())
                    }
                }
                .frame(height: YYUI.s(74))
                .clipShape(.rect(cornerRadius: YYUI.s(13)))
                .overlay(RoundedRectangle(cornerRadius: YYUI.s(13)).strokeBorder(YYColor.line, lineWidth: 0.5))
                .padding(on ? YYUI.s(4.5) : 0)
                .overlay {
                    if on {
                        RoundedRectangle(cornerRadius: YYUI.s(13) + YYUI.s(4.5)).strokeBorder(tint.accentSoft, lineWidth: YYUI.s(2.5))
                        RoundedRectangle(cornerRadius: YYUI.s(13) + YYUI.s(2)).strokeBorder(tint.accent, lineWidth: YYUI.s(2)).padding(YYUI.s(2.5))
                    }
                }
                .padding(on ? 0 : YYUI.s(4.5))
                Text(m.title).font(YYUI.font(11, on ? .semibold : .regular)).foregroundStyle(on ? YYColor.t1 : YYColor.t2)
            }
            .padding(.horizontal, -YYUI.s(4.5))
            .frame(maxWidth: .infinity)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }

    /// The dark half of 跟随系统: right of a line leaning 20° off upright.
    private nonisolated struct Slant: Shape {
        func path(in r: CGRect) -> Path {
            let lean = r.height * tan(.pi / 9) / 2
            var p = Path()
            p.move(to: CGPoint(x: r.midX + lean, y: r.minY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.minY))
            p.addLine(to: CGPoint(x: r.maxX, y: r.maxY))
            p.addLine(to: CGPoint(x: r.midX - lean, y: r.maxY))
            p.closeSubpath()
            return p
        }
    }

    /// A colour, ringed when chosen, its name under it — as the Mac's 外观 has them.
    private func tintChoice(_ t: YYTint) -> some View {
        let on = tintID == t.id
        return Button { tintID = t.id } label: {
            VStack(spacing: YYUI.s(5)) {
                Circle().fill(t.accent)
                    .frame(width: YYUI.s(22), height: YYUI.s(22)).padding(YYUI.s(2.5))
                    .overlay(Circle().strokeBorder(on ? t.accent : .clear, lineWidth: 2))
                Text(t.name).font(YYUI.font(10, on ? .semibold : .regular))
                    .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1).fixedSize()
            }
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}

// MARK: - 阅读

/// 阅读, as the Mac's: what a book opens with — paper, font, size, spacing, how pages turn and
/// how the text sits — with a sample of 《故乡》 on top; a book's own Aa changes only that book.
/// 沉浸阅读 and 墨水屏 sit here too, as the Mac keeps them under 阅读.
struct PhoneReading: View {
    let library: Library
    @State private var settings = ReaderSettings.standard
    @AppStorage(ImmersivePrefs.alwaysKey) private var immersiveAlways = false
    @AppStorage(ImmersivePrefs.lineKey) private var immersiveLine = true
    @AppStorage(ImmersivePrefs.marksKey) private var immersiveMarks = false
    @AppStorage(ImmersivePrefs.hintsKey) private var immersiveHints = true
    @AppStorage(ImmersivePrefs.stillKey) private var immersiveStill = true
    @AppStorage(ImmersivePrefs.statusKey) private var immersiveStatus = true
    @AppStorage(ImmersivePrefs.progressKey) private var immersiveProgress = true
    @AppStorage(EInk.key) private var einkOn = false
    @State private var editingPaper: PaperDraft?
    @State private var previewPaper: CustomPaper?
    @State private var renamingPaper: CustomPaper?
    @State private var newPaperName = ""
    @State private var confirming: YYConfirm?

    var body: some View {
        let resolved = PaperAppearance.shared.resolve(settings)
        YYPage {
            PhoneSettingsLead(String(localized: "新书的默认排版；单本书可在阅读页 Aa 中单独调整"))
            sample.padding(.top, YYUI.s(12))
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(6)) {
                    if einkOn {
                        HStack(spacing: YYUI.s(6)) {
                            Image(systemName: "rectangle.portrait.on.rectangle.portrait")
                                .font(YYUI.font(10.5))
                            Text("墨水屏模式已接管纸色，关闭后恢复原纸色")
                                .font(YYUI.font(10.5))
                        }
                        .foregroundStyle(YYColor.t2)
                        .padding(.top, YYUI.s(10))
                    }
                    tiles(String(localized: "纸色"), columns: 3, trailing: {
                        HStack(spacing: YYUI.s(6)) {
                            Text("跟随外观").font(YYUI.font(10.5)).foregroundStyle(YYColor.t2)
                            YYSwitch(isOn: Binding(
                                get: { PaperAppearance.shared.follow },
                                set: { PaperAppearance.shared.setFollow($0, in: settings) { change($0) } }
                            ), label: String(localized: "跟随外观"))
                        }
                    }) {
                        ForEach(YYPaper.all) { paper in
                            SettingsPaperTile(paper: paper, texture: paper.hasNoiseTexture ? .grain : .none, name: paper.name,
                                              selected: resolved.choiceID == paper.id) {
                                if let id = Paper(id: paper.id) {
                                    PaperAppearance.shared.pick(preset: id, in: settings) { change($0) }
                                }
                            }
                        }
                        ForEach(Papers.shared.list, id: \.id) { mine in
                            SettingsPaperTile(paper: YYPaper(custom: mine), texture: mine.texture, name: mine.name,
                                              selected: resolved.choiceID == mine.id) {
                                choosePaper(mine)
                            }
                            .contextMenu {
                                Button(String(localized: "调整颜色"), systemImage: "paintpalette") {
                                    editingPaper = PaperDraft(paper: mine)
                                }
                                Button(String(localized: "重命名"), systemImage: "pencil") {
                                    newPaperName = mine.name
                                    renamingPaper = mine
                                }
                                Button(String(localized: "复制一张"), systemImage: "plus.square.on.square") {
                                    if let saved = Papers.shared.duplicate(mine) { choosePaper(saved) }
                                }
                                Divider()
                                Button(String(localized: "删除…"), systemImage: "trash", role: .destructive) {
                                    confirming = YYConfirm(
                                        title: String(localized: "删除“\(mine.name)”？"),
                                        message: String(localized: "正在使用这张纸的书将恢复为相近的预设纸色。"),
                                        button: String(localized: "删除")
                                    ) { deletePaper(mine) }
                                }
                            }
                        }
                        SettingsNewPaperTile {
                            editingPaper = PaperDraft(paper: Papers.shared.newDraft(from: settings))
                        }
                    }
                    .animation(YYMotion.layout, value: Papers.shared.list.map(\.id))
                    .opacity(einkOn ? 0.45 : 1)
                    .disabled(einkOn)
                }
                PhoneFontTiles(selected: FontShelf.id(of: settings)) { id in
                    change { FontShelf.shared.apply(id, to: &$0) }
                    FontShelf.shared.ensure(id)
                }
                HStack(spacing: YYUI.s(10)) {
                    Text("字号").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    Slider(value: Binding(get: { settings.fontSize }, set: { v in change { $0.fontSize = v.rounded() } }),
                           in: TypesetCard.sizes)
                        .tint(YYTint.standard.fill)
                    Text("\(Int(settings.fontSize))").font(YYUI.font(11).monospacedDigit()).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(20), alignment: .trailing)
                }
                .frame(minHeight: YYUI.s(38))
                YYRow(title: String(localized: "行距")) {
                    YYSegments(items: [(Double?.some(1.6), String(localized: "紧")), (nil, String(localized: "适中")), (.some(2.2), String(localized: "松"))],
                               selection: Binding(get: { settings.lineHeight }, set: { v in change { $0.lineHeight = v } }), small: true)
                        .fixedSize()
                }
            }
            .padding(.top, YYUI.s(12))
            YYGroup {
                YYRow(title: String(localized: "翻页方式")) {
                    YYSegments(items: [(LayoutMode.paginated, String(localized: "左右翻页")), (.scrolled, String(localized: "上下滚动"))],
                               selection: Binding(get: { settings.mode }, set: { v in change { $0.mode = v } }), small: true)
                        .fixedSize()
                }
                YYRow(title: String(localized: "两端对齐")) {
                    YYSwitch(isOn: Binding(get: { settings.justify ?? true }, set: { v in change { $0.justify = v } }), label: String(localized: "两端对齐"))
                }
                YYRow(title: String(localized: "首行缩进两字")) {
                    YYSwitch(isOn: Binding(get: { settings.indent ?? true }, set: { v in change { $0.indent = v } }), label: String(localized: "首行缩进两字"))
                }
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("中文标点挤压").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("收紧连续标点的留白").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: Binding(get: { settings.compress }, set: { v in change { $0.compress = v } }), label: String(localized: "中文标点挤压"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            .padding(.top, YYUI.s(12))
            PhoneSettingsHeading(String(localized: "沉浸阅读"))
            YYGroup {
                detailRow(String(localized: "进入与退出"), String(localized: "双击屏幕边缘进出，轻点中央临时唤出控制栏")) { EmptyView() }
                detailRow(String(localized: "打开书籍时自动进入"), nil) {
                    YYSwitch(isOn: $immersiveAlways, label: String(localized: "打开书籍时自动进入"))
                }
                detailRow(String(localized: "底部保留进度细线"), nil) {
                    YYSwitch(isOn: $immersiveLine, label: String(localized: "底部保留进度细线"))
                }
                detailRow(String(localized: "隐藏划线与想法"), String(localized: "退出沉浸后恢复显示")) {
                    YYSwitch(isOn: $immersiveMarks, label: String(localized: "隐藏划线与想法"))
                }
                detailRow(String(localized: "隐藏书伴提示与难词注释"), nil) {
                    YYSwitch(isOn: $immersiveHints, label: String(localized: "隐藏书伴提示与难词注释"))
                }
                detailRow(String(localized: "关闭翻页动画"), String(localized: "直接切换整页，无滑动过渡")) {
                    YYSwitch(isOn: $immersiveStill, label: String(localized: "关闭翻页动画"))
                }
                // The phone's own: its status bar and the line of where you are.
                detailRow(String(localized: "隐藏状态栏、时间和电量"), nil) {
                    YYSwitch(isOn: $immersiveStatus, label: String(localized: "隐藏状态栏、时间和电量"))
                }
                detailRow(String(localized: "隐藏进度、页码和剩余时间"), nil) {
                    YYSwitch(isOn: $immersiveProgress, label: String(localized: "隐藏进度、页码和剩余时间"))
                }
                detailRow(String(localized: "专注模式联动"), String(localized: "iOS 暂不支持自动开启，请在系统「设置 › 专注模式」中添加「阅读」后从控制中心开启")) { EmptyView() }
            }
            YYGroup {
                NavigationLink { PhoneEInk(library: library) } label: {
                    YYRow(String(localized: "墨水屏"), icon: "rectangle.portrait.on.rectangle.portrait", value: einkOn ? String(localized: "开") : String(localized: "关"), chevron: true)
                }
                .buttonStyle(.plain)
            }
            .padding(.top, YYUI.s(12))
        }
        .navigationTitle("阅读")
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { settings = (try? library.core.readerSettings()) ?? .standard }
        .onReceive(NotificationCenter.default.publisher(for: .readerSettingsChanged)) { _ in
            settings = (try? library.core.readerSettings()) ?? settings
        }
        .sheet(item: $editingPaper, onDismiss: { previewPaper = nil }) { draft in
            ScrollView {
                PaperEditor(draft: draft.paper, backTitle: String(localized: "取消"), onPreview: { previewPaper = $0 }) { saved in
                    editingPaper = nil
                    previewPaper = nil
                    if let saved { choosePaper(saved) }
                }
                .padding(16)
            }
            .scrollBounceBehavior(.basedOnSize)
            .background(YYColor.bg)
            .presentationDetents([.medium, .large])
            .presentationDragIndicator(.visible)
            .presentationBackgroundInteraction(.enabled(upThrough: .medium))
        }
        .yyPrompt(String(localized: "重命名"), text: $newPaperName, prompt: String(localized: "名字"),
                  isPresented: Binding(get: { renamingPaper != nil }, set: { if !$0 { renamingPaper = nil } }),
                  confirm: String(localized: "好")) {
            if let mine = renamingPaper { Papers.shared.rename(mine, to: newPaperName) }
            renamingPaper = nil
        }
        .yyConfirm($confirming)
    }

    private func choosePaper(_ mine: CustomPaper) {
        PaperAppearance.shared.pick(custom: mine, in: settings) { change($0) }
    }

    private func deletePaper(_ mine: CustomPaper) {
        if settings.customPaper == mine.id { change { $0.customPaper = nil } }
        PaperAppearance.shared.forget(mine.id)
        Papers.shared.delete(mine.id)
    }

    /// A setting's name with a grey line under it, its control at the end — the Mac's SettingsRow.
    private func detailRow<C: View>(_ title: String, _ detail: String?, @ViewBuilder control: () -> C) -> some View {
        HStack(spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(1)) {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                if let detail {
                    Text(detail).font(YYUI.font(10)).foregroundStyle(YYColor.t3).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: YYUI.s(8))
            control()
        }
        .padding(.vertical, YYUI.s(detail == nil ? 4 : 8))
        .frame(minHeight: YYUI.s(38))
    }

    /// The opening of 《故乡》 on the chosen paper, in the chosen font, size and spacing.
    private var sample: some View {
        let paper = einkOn ? EInk.paper : (previewPaper.map { YYPaper(custom: $0) } ?? ReaderPaperOf.paper(settings))
        let texture: PaperTexture = einkOn ? .none : (previewPaper?.texture ?? ReaderPaperOf.texture(settings))
        let size = (settings.fontSize * 0.8).rounded()
        let id = FontShelf.id(of: settings)
        let name = FontShelf.shared.family(id)
        let line = "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"
        // A face still coming down: the few characters of the sample from it, until it's here.
        let preview = FontShelf.shared.isReady(id) ? nil : FontPreview.shared.font(id, text: line, size: size)
        let face: SwiftUI.Font = preview ?? name.map { .custom($0, fixedSize: size) } ?? .system(size: size)
        let lineHeight = settings.lineHeight ?? paper.text.lineHeight
        let indent = settings.indent ?? true ? "\u{3000}\u{3000}" : ""
        return VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack {
                Text("故乡")
                Spacer()
                Text(verbatim: "12 / 31").monospacedDigit()
            }
            .font(YYUI.font(8.5))
            .foregroundStyle(paper.ink.opacity(0.45))
            Text(indent + line)
                .font(face).lineSpacing(size * max(0, lineHeight - 1.25))
                .foregroundStyle(paper.ink)
                .multilineTextAlignment(.leading)
                .fixedSize(horizontal: false, vertical: true)
        }
        .padding(.horizontal, YYUI.s(14)).padding(.vertical, YYUI.s(12))
        .frame(maxWidth: .infinity, alignment: .topLeading)
        .background { paper.bg.overlay { PaperTextureView(texture: texture, dark: paper.isDark) } }
        .clipShape(.rect(cornerRadius: YYUI.s(12)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: paper.isDark ? 0 : 0.5))
        .onAppear { if !FontShelf.shared.isReady(id) { FontPreview.shared.load(id, text: line) } }
        .onChange(of: id) { if !FontShelf.shared.isReady(id) { FontPreview.shared.load(id, text: line) } }
        .animation(previewPaper == nil ? YYMotion.standard : nil, value: PaperAppearance.shared.resolve(settings).choiceID)
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("样张"))
    }

    private func tiles<Content: View>(_ title: String, columns: Int, @ViewBuilder content: () -> Content) -> some View {
        tiles(title, columns: columns, trailing: { EmptyView() }, content: content)
    }

    private func tiles<Trailing: View, Content: View>(_ title: String, columns: Int,
                                                      @ViewBuilder trailing: () -> Trailing,
                                                      @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: YYUI.s(8)) {
            HStack(alignment: .center, spacing: YYUI.s(8)) {
                Text(title).font(YYUI.font(11.5, .medium)).foregroundStyle(YYColor.t1)
                Spacer(minLength: YYUI.s(8))
                trailing()
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(), spacing: YYUI.s(6)), count: columns), spacing: YYUI.s(6)) { content() }
        }
        .padding(.vertical, YYUI.s(12))
    }

    private func change(_ edit: (inout ReaderSettings) -> Void) {
        var new = settings
        edit(&new)
        guard new != settings else { return }
        SettingsLog.shared.reader(from: settings, to: new)
        settings = new
        try? library.core.setReaderSettings(settings: new)
        NotificationCenter.default.post(name: .readerSettingsChanged, object: nil)
    }
}

// MARK: - 外文阅读

/// 外文阅读: saying a word aloud, 难词注释, the 生词 marks, and the
/// system's offline translation.
struct PhoneForeign: View {
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
        YYPage {
            PhoneSettingsLead(String(localized: "查词、难词注释、生词与离线翻译"))
            groupLabel(String(localized: "查词")).padding(.top, YYUI.s(16))
            YYGroup {
                YYRow(title: String(localized: "查词时自动发音")) { YYSwitch(isOn: $speak, label: String(localized: "查词时自动发音")) }
                YYRow(title: String(localized: "英文发音")) {
                    YYSegments(items: [(ForeignPrefs.Accent.british, String(localized: "英音")), (ForeignPrefs.Accent.american, String(localized: "美音"))], selection: $accent, small: true)
                        .fixedSize(horizontal: true, vertical: false)
                }
            }
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "MDict 词典"))
            PhoneMDictList(core: library.core)
            YYNote(String(localized: "按列表顺序优先查词，点右侧菜单或长按可调整顺序与移除。"))
                .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "难词注释"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("难词行间释义").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("阅读英文书时，在较难词汇上方标注简短中文词义").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $gloss, label: String(localized: "难词行间释义"))
                }
                .padding(.vertical, YYUI.s(8))
                VStack(alignment: .leading, spacing: YYUI.s(6)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("词汇难度").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("等级越高，仅标注更生僻的词").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    let levelSegments = YYSegments(items: ForeignPrefs.levels.map { (Int($0.level), $0.name) }, selection: $level, small: true)
                    ViewThatFits(in: .horizontal) {
                        levelSegments
                        ScrollView(.horizontal, showsIndicators: false) {
                            levelSegments.fixedSize(horizontal: true, vertical: false)
                        }
                    }
                    .disabled(!gloss || glossVocab)
                    .opacity(!gloss || glossVocab ? 0.5 : 1)
                    .animation(YYMotion.quick, value: !gloss || glossVocab)
                }
                .padding(.vertical, YYUI.s(8))
                YYRow(title: String(localized: "仅标注生词本中的词")) { YYSwitch(isOn: $glossVocab, label: String(localized: "仅标注生词本中的词")).disabled(!gloss) }
                glossPreview
            }
            .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "生词"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(3)) {
                        Text("在书中标记生词").font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text("…this truth is so well \(Text("fixed").foregroundStyle(YYColor.t1).underline(mark, pattern: .dot, color: YYTint.standard.accent)) in the minds…")
                            .font(.custom("Georgia", size: YYUI.s(11)))
                            .foregroundStyle(YYColor.t2)
                    }
                    Spacer(minLength: 0)
                    YYSwitch(isOn: $mark, label: String(localized: "在书中标记生词"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            YYNote(String(localized: "「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记。"))
                .padding(.bottom, YYUI.s(14))
            groupLabel(String(localized: "翻译"))
            YYGroup {
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("系统离线翻译").font(YYUI.font(11.5, .semibold)).foregroundStyle(YYColor.t1)
                        Text("本地离线运行，原文不上传；供整页译文与双击整句翻译使用").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                    Spacer(minLength: 0)
                    translationState
                }
                .padding(.vertical, YYUI.s(8))
                if let failed {
                    Label(failed, systemImage: "exclamationmark.circle").font(YYUI.font(10.5)).foregroundStyle(YYColor.danger)
                        .frame(minHeight: YYUI.s(34))
                        .transition(.opacity)
                }
            }
            .animation(YYMotion.standard, value: failed)
            YYNote(String(localized: "原文不出这台设备。"))
        }
        .navigationTitle("外文")
        .navigationBarTitleDisplayMode(.inline)
        .onChange(of: "\(gloss)\(level)\(glossVocab)\(mark)") { NotificationCenter.default.post(name: .vocabChanged, object: nil) }
        .task { await check() }
        .translationTask(download) { session in
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

    /// `.gl`: the small grey name over a group.
    /// The Mac's small heading over a card.
    private func groupLabel(_ text: String) -> some View {
        PhoneSettingsHeading(text).padding(.top, -YYUI.s(10))
    }

    /// A line of Austen with the gloss over the hard word, changing as the settings do.
    private var glossPreview: some View {
        VStack(alignment: .leading, spacing: YYUI.s(4)) {
            Text("预览").font(YYUI.font(10)).foregroundStyle(YYColor.t3).padding(.top, YYUI.s(6))
            HStack(alignment: .lastTextBaseline, spacing: 0) {
                Text(verbatim: "It is a truth universally ")
                VStack(spacing: 0) {
                    Text(verbatim: "公认").font(YYUI.font(9)).foregroundStyle(YYColor.t2)
                        .opacity(gloss && !glossVocab && level <= 4 ? 1 : 0)
                    Text(verbatim: "acknowledged")
                }
                Text(verbatim: ", that a…")
            }
            .font(.custom("Georgia", size: YYUI.s(12)))
            .foregroundStyle(YYColor.t1)
            .lineLimit(1)
            .minimumScaleFactor(0.8)
            .padding(.bottom, YYUI.s(6))
        }
        .animation(YYMotion.standard, value: "\(gloss)\(level)\(glossVocab)")
    }

    @ViewBuilder private var translationState: some View {
        switch status {
        case .installed?:
            Image(systemName: "checkmark").font(YYUI.font(11, .bold)).foregroundStyle(YYTint.standard.accent)
                .accessibilityLabel("英文 → 中文 已下载")
        case .supported?:
            Button("下载英文 → 中文") {
                download = .init(source: Locale.Language(identifier: "en"), target: Translator.target)
            }
            .buttonStyle(YYButtonStyle(kind: .soft, size: .small))
            .disabled(download != nil)
        case .unsupported?:
            Text("当前系统不支持").font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
        default:
            ProgressView().controlSize(.small)
        }
    }

    private func check() async {
        let s = await LanguageAvailability().status(from: Locale.Language(identifier: "en"), to: Translator.target)
        withAnimation(YYMotion.standard) { status = s }
    }
}

// MARK: - 通用

/// 通用, as the Mac's: what opening 语阅 does, and how books come in — 书库来源 and Wi‑Fi 传书
/// being the phone's ways in, where the Mac takes them dragged onto its window.
struct PhoneGeneral: View {
    let library: Library
    @AppStorage("general.reopenLast") private var reopenLast = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @Environment(\.colorScheme) private var scheme

    @State private var language = AppLanguage.chosen
    @State private var langMenuOpen = false
    @State private var langTriggerRect: CGRect = .zero
    @State private var pendingLanguage: AppLanguage?

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var pendingRestart: Bool { language.effective != AppLanguage.running }

    var body: some View {
        YYPage {
            YYGroup {
                languageRow
                HStack(spacing: YYUI.s(10)) {
                    VStack(alignment: .leading, spacing: YYUI.s(1)) {
                        Text("启动时继续上次阅读").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                        Text("关闭则进入书架").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    }
                    Spacer(minLength: YYUI.s(8))
                    YYSwitch(isOn: $reopenLast, label: String(localized: "启动时继续上次阅读"))
                }
                .padding(.vertical, YYUI.s(8))
            }
            .padding(.top, YYUI.s(12))
            PhoneSettingsHeading(String(localized: "导入"))
            YYGroup {
                VStack(alignment: .leading, spacing: YYUI.s(4)) {
                    Text("在「文件」中选择书籍，用语阅打开即可导入").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                    Text("文件会复制到书库，移动或删除原文件不受影响").font(YYUI.font(10)).foregroundStyle(YYColor.t3)
                    Text(verbatim: "EPUB  PDF  MOBI  AZW3  TXT").font(YYUI.font(9.5, design: .monospaced)).foregroundStyle(YYColor.t2)
                        .padding(.top, YYUI.s(2))
                }
                .padding(.vertical, YYUI.s(10))
                NavigationLink { PhoneSources(library: library) } label: {
                    YYRow(String(localized: "书库来源"), icon: "server.rack",
                          value: library.sources.all.isEmpty ? nil : String(localized: "\(library.sources.all.count) 个"), chevron: true)
                }
                .buttonStyle(.plain)
                NavigationLink { PhoneWifi(library: library) } label: {
                    YYRow(String(localized: "Wi‑Fi 传书"), icon: "wifi", chevron: true)
                }
                .buttonStyle(.plain)
            }
        }
        .coordinateSpace(name: "phoneGeneral")
        .overlay {
            if langMenuOpen {
                Color.black.opacity(0.001)
                    .ignoresSafeArea()
                    .contentShape(.rect)
                    .onTapGesture {
                        withAnimation(YYMotion.quick) { langMenuOpen = false }
                    }
            }
        }
        .overlay(alignment: .topLeading) {
            if langMenuOpen {
                LanguagePaperDropdownMenu(selected: language) { picked in
                    withAnimation(YYMotion.quick) { langMenuOpen = false }
                    pickLanguage(picked)
                }
                .offset(
                    x: max(12, langTriggerRect.maxX - 218),
                    y: langTriggerRect.maxY + 6
                )
                .transition(.opacity.combined(with: .scale(scale: 0.95, anchor: .topTrailing)))
            }
        }
        .alert(
            pendingLanguage.map(AppLanguage.restartPromptTitle(for:)) ?? "",
            isPresented: Binding(
                get: { pendingLanguage != nil },
                set: { if !$0 { pendingLanguage = nil } }
            ),
            presenting: pendingLanguage
        ) { pending in
            Button("好的", role: .destructive) {
                language = pending
                AppLanguage.choose(pending)
                pendingLanguage = nil
            }
            Button("取消", role: .cancel) {
                pendingLanguage = nil
            }
        } message: { pending in
            Text(pending.effective == .en
                 ? String(localized: "下次打开语阅换成 English。想现在就换：从后台划掉语阅，再打开。")
                 : String(localized: "下次打开语阅换成简体中文。想现在就换：从后台划掉语阅，再打开。"))
        }
        .navigationTitle("通用")
        .navigationBarTitleDisplayMode(.inline)
        .sensoryFeedback(.selection, trigger: language)
        .onDisappear { langMenuOpen = false }
    }

    private func pickLanguage(_ picked: AppLanguage) {
        guard language != picked else { return }
        if picked.effective != AppLanguage.running {
            pendingLanguage = picked
        } else {
            language = picked
            AppLanguage.choose(picked)
        }
    }

    /// 设置 › 通用 › 语言: warm-paper seal trigger matching macOS's `MacLanguageCard`.
    private var languageRow: some View {
        HStack(alignment: .center, spacing: YYUI.s(10)) {
            VStack(alignment: .leading, spacing: YYUI.s(1.5)) {
                Text("语言").font(YYUI.font(11.5)).foregroundStyle(YYColor.t1)
                Text(languageSubtitle)
                    .font(YYUI.font(10))
                    .foregroundStyle(pendingRestart ? tint.accent : YYColor.t3)
                    .lineLimit(2)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: YYUI.s(8))
            Button {
                withAnimation(YYMotion.quick) { langMenuOpen.toggle() }
            } label: {
                HStack(spacing: 8) {
                    LanguageSealBadge(language: language, onTrigger: true, tint: tint)
                    Text(language.displayName)
                        .font(.yy(size: 12.5, weight: .medium))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                    if language == .system {
                        Text(AppLanguage.systemDisplayName)
                            .font(.yy(size: 10.5, weight: .medium))
                            .foregroundStyle(YYColor.t2)
                            .lineLimit(1)
                            .padding(.horizontal, 6)
                            .padding(.vertical, 1.5)
                            .background(systemTagFill, in: .capsule)
                    }
                    Spacer(minLength: 4)
                    Image(systemName: "chevron.up.chevron.down")
                        .font(.yy(size: 9.5, weight: .semibold))
                        .foregroundStyle(YYColor.t3)
                }
                .padding(.leading, 6)
                .padding(.trailing, 10)
                .frame(width: 176, height: 34)
                .background(triggerFill, in: .rect(cornerRadius: 9, style: .continuous))
                .overlay {
                    RoundedRectangle(cornerRadius: 9, style: .continuous)
                        .strokeBorder(triggerBorder, lineWidth: langMenuOpen ? 1 : 0.5)
                }
                .shadow(color: .black.opacity(scheme == .dark ? 0.22 : 0.03), radius: 1.5, y: 1)
                .contentShape(.rect(cornerRadius: 9, style: .continuous))
            }
            .buttonStyle(YYPressStyle(scale: 0.98))
            .onGeometryChange(for: CGRect.self) { proxy in
                proxy.frame(in: .named("phoneGeneral"))
            } action: { rect in
                langTriggerRect = rect
            }
            .animation(YYMotion.quick, value: langMenuOpen)
        }
        .padding(.vertical, YYUI.s(8))
        .frame(minHeight: YYUI.s(42))
    }

    private var languageSubtitle: String {
        if pendingRestart {
            return String(localized: "待重开语阅后生效")
        }
        if language == .system {
            return AppLanguage.systemLanguage == .en
                ? String(localized: "当前：English · 切换需重新打开语阅")
                : String(localized: "当前：中文 · 切换需重新打开语阅")
        }
        return String(localized: "切换需重新打开语阅")
    }

    private var triggerFill: Color {
        if scheme == .dark {
            return langMenuOpen ? Color(yy: YYRGBA(48, 50, 55)) : YYColor.inset
        }
        return langMenuOpen ? YYColor.card : YYColor.bg
    }

    private var triggerBorder: Color {
        if langMenuOpen { return tint.edge }
        return scheme == .dark ? Color.white.opacity(0.10) : Color(yy: YYRGBA(80, 65, 45, 0.14))
    }

    private var systemTagFill: Color {
        scheme == .dark ? Color.white.opacity(0.09) : Color(yy: YYRGBA(42, 43, 46, 0.06))
    }
}
#endif
