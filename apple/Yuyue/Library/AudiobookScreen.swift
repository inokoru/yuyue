import SwiftUI
import YuyueCore

/// 有声书 open: the cover, the chapter, the scrubber with the
/// 书签 on it, and the controls. On a Mac the chapters and 书签 sit in a column on the right; on
/// the phone they come up from the bottom.
struct AudiobookScreen: View {
    let library: Library
    let player: AudiobookPlayer
    var close: (() -> Void)?
    @State private var list: ListKind?
    @State private var scrubbing: Double?
    @State private var timer = false
    @State private var settingsShown = false
    #if os(macOS)
    @Environment(\.openWindow) private var openWindow
    @AppStorage(LiquidGlass.key) private var liquidChosen = false
    private var liquid: Bool { liquidChosen && LiquidGlass.supported }
    #endif
    private let tint = YYTint.standard

    enum ListKind: String, Identifiable, CaseIterable {
        case chapters, bookmarks
        var id: String { rawValue }
    }

    var body: some View {
        #if os(macOS)
        HStack(spacing: 0) {
            controls(cover: 240, width: 420)
                .frame(maxWidth: .infinity, maxHeight: .infinity)
                .overlay(alignment: .bottom) { keys.padding(.bottom, 18) }
            // As the book window's side: the chapters in a card, 这本录音 under them.
            VStack(spacing: 10) {
                ChapterColumn(player: player)
                if player.hasMap { RecordingSettings(player: player) }
                pairCard
            }
                .frame(width: 300)
                .padding(.vertical, 12)
                .padding(.trailing, 12)
        }
        .background(YYColor.bg)
        // ⌘D, as in a book's window.
        .background { Button("加书签") { player.addBookmark() }.keyboardShortcut("d").hidden() }
        .onKeyPress(.space) { player.togglePlay(); return .handled }
        // A sentence once laid against the e-book (⇧ for 15 seconds); 15 seconds either way otherwise.
        .onKeyPress(keys: [.leftArrow, .rightArrow]) { press in
            let forward = press.key == .rightArrow
            if player.hasMap, !press.modifiers.contains(.shift) {
                player.step(forward: forward)
            } else {
                player.skip(forward ? AudiobookPlayer.back : -AudiobookPlayer.back)
            }
            return .handled
        }
        .focusable()
        .focusEffectDisabled()
        .overlay(alignment: .top) { toast.padding(.top, 12).yyToastMotion(value: player.toast?.id) }
        .overlay(alignment: .bottom) { sleepReturn.padding(.bottom, 52).animation(YYMotion.standard, value: player.sleepReturn != nil && player.elsewhere == nil) }
        .navigationTitle(player.summary.title)
        // The book window's own top: ‹ 书架 on the left, no system title or bar.
        .toolbar {
            ShelfButtonToolbar(liquid: liquid, ink: YYColor.t1, glass: YYColor.glass, line: YYColor.line) {
                openWindow(id: "library")
            }
        }
        .yyHideWindowToolbarChrome()
        // 合成 here shows how it's going in this window too.
        .audiobookSheets(library)
        #else
        VStack(spacing: 0) {
            HStack {
                Button("收起", systemImage: "chevron.down") { close?() }
                    .labelStyle(.iconOnly)
                    .font(.body.weight(.semibold))
                    .frame(width: 40, height: 40)
                Spacer()
            }
            .padding(.horizontal, 12)
            controls(cover: 188, width: nil)
                .padding(.horizontal, 22)
            tiles
                .padding(.horizontal, 22)
            pairCard
                .padding(.horizontal, 22)
                .padding(.top, 14)
            Spacer(minLength: 0)
        }
        .overlay(alignment: .top) { toast.padding(.top, 50).yyToastMotion(value: player.toast?.id) }
        .overlay(alignment: .bottom) { sleepReturn.padding(.horizontal, 16).padding(.bottom, 12).animation(YYMotion.standard, value: player.sleepReturn != nil && player.elsewhere == nil) }
        .sheet(isPresented: $settingsShown) {
            ScrollView {
                RecordingSettings(player: player)
                    .padding(.horizontal, 16)
                    .padding(.top, YYUI.sheetCardTop)
                    .padding(.bottom, 16)
            }
            .presentationDetents([.medium])
            .presentationDragIndicator(.visible)
            .presentationBackground(YYColor.bg)
        }
        .buttonStyle(.plain)
        .foregroundStyle(YYColor.t1)
        .background(YYColor.bg)
        .sheet(item: $list) { kind in
            ChapterList(player: player, kind: Binding(get: { list ?? kind }, set: { list = $0 }))
                .presentationDetents([.medium, .large])
                .presentationDragIndicator(.visible)
        }
        // 合成 here shows how it's going on the phone too.
        .audiobookSheets(library)
        #endif
    }

    // MARK: Parts

    /// 划了一句 (撤销), up top for a few seconds.
    @ViewBuilder private var toast: some View {
        if let t = player.toast {
            UndoToast(toast: t) { t.undo?(); player.toast = nil }
                .transition(UndoToast.transition)
                .task(id: t.id) {
                    await Library.waitToast(t)
                    if player.toast?.id == t.id { player.toast = nil }
                }
        }
    }

    /// 回到昨晚.
    @ViewBuilder private var sleepReturn: some View {
        if player.elsewhere == nil, let r = player.sleepReturn {
            let i = player.chapter(at: r.seconds)
            SleepReturnCard(when: SleepReturn.when(r.date), place: "\(ReaderModel.shortTitle(player.title(i))) \(AudiobookPlayer.clock(r.seconds - (player.chapters.indices.contains(i) ? player.chapters[i].start : 0)))") {
                player.answerSleepReturn(go: $0)
            }
            .transition(YYMotion.reduce ? .opacity : .move(edge: .bottom).combined(with: .opacity))
        }
    }

    /// 跳过了片头 · 点这里听, 不在书里的话 · 跳过, 朗读补上: under the times.
    @ViewBuilder private var noticeLine: some View {
        if let n = player.notice {
            Button { player.noticeAction() } label: {
                HStack(spacing: 6) {
                    Text(n.kind == .extra ? n.text : n.text).foregroundStyle(n.kind == .filling ? YYColor.t2 : tint.accent)
                    if n.kind == .extra, let a = n.action {
                        Text("|").foregroundStyle(YYColor.t3)
                        Text(a).foregroundStyle(tint.accent)
                    }
                }
                .font(.system(size: 11, weight: .semibold))
                .padding(.top, 6)
            }
            .buttonStyle(.plain)
            .disabled(n.action == nil)
            .transition(.opacity)
            .task(id: n.kind == .head ? n.extra : nil) {
                guard n.kind == .head else { return }
                try? await Task.sleep(for: .seconds(5))
                player.dismissNotice()
            }
        }
    }

    /// 书架上有同一本电子书: 合成 lays the recording against it.
    @ViewBuilder private var pairCard: some View {
        if let ebook = library.ebook(for: player.summary) {
            HStack(spacing: 10) {
                BookCover(book: ebook).frame(width: 24)
                VStack(alignment: .leading, spacing: 2) {
                    Text("书架上有《\(ebook.title)》电子书").font(.system(size: 11, weight: .semibold)).lineLimit(1)
                    Text("合成以后听到哪句，书页就走到哪句").font(.system(size: 10)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                Button("合成") { library.pair(player.summary, with: ebook) }
                    .buttonStyle(.plain)
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundStyle(YYColor.bg)
                    .padding(.horizontal, 12)
                    .frame(height: 26)
                    .background(YYColor.t1, in: .capsule)
            }
            .padding(.horizontal, 12)
            .padding(.vertical, 10)
            .yyCard()
        } else if !player.hasMap {
            TextVersionCard(library: library, summary: player.summary)
        }
    }

    private func controls(cover: CGFloat, width: CGFloat?) -> some View {
        VStack(spacing: 0) {
            SquareCover(book: player.summary)
                .frame(width: cover, height: cover)
                .padding(.top, 14)
                .padding(.bottom, 18)
            // 进度冲突: another device listened further on.
            if let e = player.elsewhere {
                ElsewhereCard(elsewhere: e, tint: tint, listened: true, stay: { player.elsewhere = nil }, jump: { player.jumpElsewhere() })
                    .frame(maxWidth: 420)
                    .padding(.bottom, 16)
                    .transition(.opacity)
            }
            HStack(alignment: .bottom, spacing: 8) {
                VStack(alignment: width == nil ? .leading : .center, spacing: 2) {
                    Text(player.title(player.chapter))
                        .font(.system(size: width == nil ? 17 : 19, weight: .bold))
                        .lineLimit(1)
                    Text([player.summary.title, player.summary.authors.first].compactMap { $0 }.joined(separator: " · "))
                        .font(.system(size: 11))
                        .foregroundStyle(YYColor.t2)
                        .lineLimit(1)
                }
                .frame(maxWidth: .infinity, alignment: width == nil ? .leading : .center)
                #if os(iOS)
                Button("加书签", systemImage: "bookmark") { player.addBookmark() }
                    .labelStyle(.iconOnly)
                    .frame(width: 30, height: 30)
                #endif
            }
            scrubber
                .padding(.top, 4)
                .padding(.bottom, -8)
            times
            noticeLine
                .animation(YYMotion.standard, value: player.notice?.text)
            buttons
                .padding(.vertical, 14)
                .padding(.horizontal, width == nil ? 4 : 40)
            waitingLine
                .padding(.top, -10)
                .animation(YYMotion.quick, value: player.waiting)
            if let failure = player.failure {
                Text(failure).font(.footnote).foregroundStyle(YYColor.danger)
                    .transition(.opacity)
            }
        }
        // 进度冲突 and a failure come and go with a fade; the rest makes room smoothly.
        .animation(YYMotion.standard, value: player.elsewhere != nil)
        .animation(YYMotion.standard, value: player.failure)
        .frame(width: width)
    }

    /// The chapter: what's been heard, the 书签 in it as small marks, and a knob to drag.
    private var scrubber: some View {
        let (at, of) = player.inChapter
        let start = player.chapters.indices.contains(player.chapter) ? player.chapters[player.chapter].start : 0
        let shown = scrubbing ?? at
        let marks = player.bookmarks.map { player.seconds(of: $0) - start }.filter { $0 >= 0 && $0 <= of }
        return GeometryReader { g in
            let x = { (t: Double) in of > 0 ? g.size.width * min(1, max(0, t / of)) : 0 }
            ZStack(alignment: .leading) {
                if player.streaming {
                    // 边下边听: past what's here, still on the share (dashed); here, pale; heard, ink.
                    let here = player.hereInChapter
                    Path { p in p.move(to: CGPoint(x: 0, y: 2)); p.addLine(to: CGPoint(x: g.size.width, y: 2)) }
                        .stroke(YYColor.line, style: StrokeStyle(lineWidth: 2, lineCap: .round, dash: [3, 3]))
                        .frame(height: 4)
                    Capsule().fill(YYColor.inset).frame(width: x(here.down), height: 4)
                    Capsule().fill(YYColor.t1).frame(width: x(shown), height: 4)
                    ForEach(here.ticks, id: \.self) { t in
                        Rectangle().fill(YYColor.line).frame(width: 1, height: 10).offset(x: x(t))
                    }
                } else {
                    Capsule().fill(YYColor.inset).frame(height: 4)
                    Capsule().fill(YYColor.t1).frame(width: x(shown), height: 4)
                }
                ForEach(marks, id: \.self) { m in
                    RoundedRectangle(cornerRadius: 1).fill(YYColor.card).frame(width: 2, height: 6).offset(x: x(m) - 1)
                }
                Circle().fill(YYColor.t1)
                    .frame(width: 12, height: 12)
                    .overlay(Circle().strokeBorder(YYColor.bg, lineWidth: 3).padding(-3))
                    .offset(x: x(shown) - 6)
            }
            .frame(maxHeight: .infinity)
            .contentShape(Rectangle())
            .gesture(DragGesture(minimumDistance: 0)
                .onChanged { v in scrubbing = of * min(1, max(0, v.location.x / max(1, g.size.width))) }
                .onEnded { _ in
                    if let s = scrubbing { player.seek(to: start + s) }
                    scrubbing = nil
                })
        }
        .frame(height: 44)
        .accessibilityElement()
        .accessibilityLabel("本集进度")
        .accessibilityValue(AudiobookPlayer.clock(at))
        .accessibilityAdjustableAction { d in player.skip(d == .increment ? AudiobookPlayer.ahead : -AudiobookPlayer.back) }
    }

    private var times: some View {
        let (at, of) = player.inChapter
        let left = player.remaining
        return HStack {
            Text(AudiobookPlayer.clock(scrubbing ?? at)).fontWeight(.medium).foregroundStyle(YYColor.t2)
            Spacer()
            #if os(iOS)
            Text("本集还剩 \(AudiobookPlayer.clock(of - at)) · 全书还剩 \(Audiobook.length(left))") + Text(downloaded)
            #else
            Text("全书还剩 \(Audiobook.length(left))") + Text(downloaded)
            #endif
            Spacer()
            Text(AudiobookPlayer.clock(of)).fontWeight(.medium).foregroundStyle(YYColor.t2)
        }
        .font(.system(size: 10).monospacedDigit())
        .foregroundStyle(YYColor.t3)
    }

    /// 边下边听: how many files are here, while some aren't.
    private var downloaded: String {
        guard player.streaming else { return "" }
        let (have, total) = player.filesHere
        return have < total ? String(localized: " · 已下 \(have) / \(total) 集") : ""
    }

    /// 边下边听: waiting for a file, the ring round the play button fills as it comes.
    @ViewBuilder private var playRing: some View {
        if let w = player.waiting {
            let f = ShareStream.shared.fetching[player.bookID]
            let fraction = f?.file == w ? f?.fraction ?? 0 : 0
            ZStack {
                Circle().stroke(YYColor.line, lineWidth: 1.6)
                Circle().trim(from: 0, to: max(0.02, fraction))
                    .stroke(YYColor.t1, style: StrokeStyle(lineWidth: 1.6, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                    .animation(YYMotion.quick, value: fraction)
            }
            .frame(width: 68, height: 68)
            .allowsHitTesting(false)
            .transition(.opacity)
        }
    }

    /// 边下边听: under the buttons while waiting.
    @ViewBuilder private var waitingLine: some View {
        if let w = player.waiting {
            Text(ShareStream.shared.problem[player.bookID] ?? String(localized: "第 \(w + 1) 集还在下，下好就接着放"))
                .font(.system(size: 11))
                .foregroundStyle(YYColor.t3)
                .frame(maxWidth: .infinity)
                .padding(.top, 4)
                .transition(.opacity)
        }
    }

    private var buttons: some View {
        HStack {
            // One level, as 定时 beside it: the rates, the one in use ticked.
            Menu {
                ForEach([0.75, 1, 1.1, 1.2, 1.25, 1.5, 1.75, 2, 2.5, 3] as [Float], id: \.self) { r in
                    Button { player.rate = r } label: {
                        if player.rate == r { Label(Self.rate(r), systemImage: "checkmark") } else { Text(Self.rate(r)) }
                    }
                }
            } label: {
                Text(Self.rate(player.rate))
                    .font(.system(size: 12.5, weight: .semibold).monospacedDigit())
                    .frame(width: 40, height: 40)
            }
            .menuIndicator(.hidden)
            .fixedSize()
            Spacer()
            Button("后退 15 秒", systemImage: "gobackward.15") { player.skip(-AudiobookPlayer.back) }
                .font(.system(size: 22))
                .frame(width: 40, height: 40)
            Spacer()
            Button(player.isPlaying ? String(localized: "暂停") : String(localized: "播放"), systemImage: player.isPlaying ? "pause.fill" : "play.fill") {
                player.togglePlay()
            }
            .contentTransition(.symbolEffect(.replace))
            .animation(YYMotion.quick, value: player.isPlaying)
            .font(.system(size: 20))
            .foregroundStyle(YYColor.bg)
            .frame(width: 60, height: 60)
            .background(YYColor.t1, in: .circle)
            .overlay { playRing }
            Spacer()
            Button("前进 30 秒", systemImage: "goforward.30") { player.skip(AudiobookPlayer.ahead) }
                .font(.system(size: 22))
                .frame(width: 40, height: 40)
            Spacer()
            sleepMenu
        }
        .labelStyle(.iconOnly)
        .buttonStyle(YYPressStyle(scale: 0.92))
        .foregroundStyle(YYColor.t1)
    }

    private var sleepMenu: some View {
        Menu {
            Section {
                if player.canStopAtSentence {
                    sleepItem("念完这句", on: { if case .sentence = $0 { true } else { false } }) { player.sleepAtSentenceEnd() }
                }
                sleepItem("本集完", on: { $0 == .chapter }) { player.sleep = .chapter }
            }
            Section {
                ForEach([15, 30, 45, 60, 90], id: \.self) { m in
                    sleepItem("\(m) 分钟", on: { if case .until(_, let n) = $0 { n == m } else { false } }) {
                        player.sleep = .until(Date().addingTimeInterval(Double(m) * 60), minutes: m)
                    }
                }
            }
            Section {
                Button("关闭") { player.sleep = .off }
            }
        } label: {
            Image(systemName: player.sleep == .off ? "moon.zzz" : "moon.zzz.fill")
                .font(.system(size: 20))
                .frame(width: 40, height: 40)
        }
        .menuIndicator(.hidden)
        .fixedSize()
        .accessibilityLabel("定时")
    }

    private func sleepItem(_ name: LocalizedStringKey, on: @escaping (AudiobookPlayer.Sleep) -> Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            if on(player.sleep) { Label(name, systemImage: "checkmark") } else { Text(name) }
        }
    }

    #if os(iOS)
    private var tiles: some View {
        HStack(spacing: 6) {
            tile("list.bullet", "章节", String(localized: "\(player.chapters.count) 集")) { list = .chapters }
            tile("bookmark", "书签", String(localized: "\(player.bookmarks.count) 个书签")) { list = .bookmarks }
            tile("moon.zzz", "定时", sleepName) { timer = true }
            if player.hasMap {
                tile("slider.horizontal.3", "这本录音", player.settings.headTail ? String(localized: "跳过片头") : String(localized: "照放片头")) { settingsShown = true }
            }
            OutputTile()
        }
        .confirmationDialog("定时", isPresented: $timer) {
            if player.canStopAtSentence { Button("念完这句") { player.sleepAtSentenceEnd() } }
            Button("本集完") { player.sleep = .chapter }
            ForEach([15, 30, 45, 60, 90], id: \.self) { m in
                Button("\(m) 分钟") { player.sleep = .until(Date().addingTimeInterval(Double(m) * 60), minutes: m) }
            }
            if player.sleep != .off { Button("关闭定时", role: .destructive) { player.sleep = .off } }
        }
    }

    private func tile(_ icon: String, _ name: LocalizedStringKey, _ detail: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            VStack(spacing: 3) {
                Image(systemName: icon).font(.system(size: 16))
                Text(name).font(.system(size: 10, weight: .semibold))
                Text(detail).font(.system(size: 10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1).minimumScaleFactor(0.85)
            }
            .frame(maxWidth: .infinity, minHeight: 56)
            .yyCard()
        }
    }
    #endif

    private var sleepName: String {
        switch player.sleep {
        case .off: String(localized: "关")
        case .sentence: String(localized: "念完这句")
        case .chapter: String(localized: "本集完")
        case .until(_, let m): String(localized: "\(m) 分钟")
        }
    }

    #if os(macOS)
    private var keys: some View {
        HStack(spacing: 16) {
            key(["空格"], "暂停")
            if player.hasMap {
                key(["←", "→"], "上一句 / 下一句")
                key(["⇧←", "⇧→"], "15 秒")
            } else {
                key(["←", "→"], "后退 15 秒 / 前进 30 秒")
            }
            key(["⌘D"], "加书签")
        }
        .font(.system(size: 10.5))
        .foregroundStyle(YYColor.t3)
    }

    private func key(_ keys: [String], _ what: LocalizedStringKey) -> some View {
        HStack(spacing: 3) {
            ForEach(keys, id: \.self) { k in
                Text(k).font(.system(size: 10)).padding(.horizontal, 5).padding(.vertical, 1)
                    .background(YYColor.inset, in: .rect(cornerRadius: 4))
                    .foregroundStyle(YYColor.t2)
            }
            Text(what)
        }
    }
    #endif

    static func rate(_ r: Float) -> String {
        (r == r.rounded() ? String(format: "%.0f", r) : String(format: "%g", r)) + "×"
    }
}

/// 边下边听: a chapter still on the share — a small ring in place of its length: an arrow to
/// fetch it next, filling while it comes (tap to pause), paused (tap to go on).
struct ChapterShareRing: View {
    let player: AudiobookPlayer
    let chapter: Int

    var body: some View {
        let stream = ShareStream.shared
        let id = player.bookID
        let file = player.file(ofChapter: chapter)
        let f = stream.fetching[id]
        Group {
            if let f, f.file == file {
                ShareRing(state: .coming(f.fraction), size: 16) { stream.pause(id) }
            } else if stream.paused.contains(id) {
                ShareRing(state: .paused(0), size: 16) { stream.carryOn(id, core: player.core) }
            } else {
                ShareRing(state: .get, size: 16) { stream.want(id, file: file, core: player.core) }
            }
        }
        .accessibilityLabel(String(localized: "第 \(file + 1) 集还在网盘上"))
    }
}

extension AudiobookPlayer {
    /// 边下边听: whether chapter `i`'s file is still on the share.
    func offDevice(chapter i: Int) -> Bool { streaming && !isHere(file: file(ofChapter: i)) }
}

/// 章节 and 书签: the one being heard has the moving bars, the ones heard are grey.
struct ChapterList: View {
    let player: AudiobookPlayer
    @Binding var kind: AudiobookScreen.ListKind
    /// 长按改名: the chapter being named, and the name typed.
    @State private var renaming: Int?
    @State private var newName = ""

    var body: some View {
        VStack(spacing: 10) {
            YYSegments(items: [(AudiobookScreen.ListKind.chapters, String(localized: "章节")), (.bookmarks, String(localized: "书签"))],
                       selection: $kind, counts: [.chapters: player.chapters.count, .bookmarks: player.bookmarks.count], small: true)
                .frame(width: 180)
            .padding(.horizontal, 12)
            #if os(iOS)
            .padding(.top, YYUI.sheetGrabberTop)
            #else
            .padding(.top, 12)
            #endif
            List {
                switch kind {
                case .chapters: chapters
                case .bookmarks: bookmarks
                }
            }
            .listStyle(.plain)
            .scrollContentBackground(.hidden)
        }
        .yyPrompt(String(localized: "给这一集起个名字"), message: String(localized: "留空就叫「第几集」。"),
                  text: $newName, prompt: renaming.map { String(localized: "第 \($0 + 1) 集") } ?? "",
                  isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } }),
                  confirm: String(localized: "保存")) {
            if let i = renaming { player.renameChapter(i, newName) }
            renaming = nil
        }
        .background(YYColor.card)
        #if os(macOS)
        .clipShape(.rect(cornerRadius: 14))
        #endif
    }

    @ViewBuilder private var chapters: some View {
        let now = player.chapter
        ForEach(Array(player.chapters.enumerated()), id: \.offset) { i, c in
            Button { player.go(toChapter: i) } label: {
                HStack(spacing: 10) {
                    Group {
                        if i == now {
                            Image(systemName: "waveform").symbolEffect(.variableColor.iterative, isActive: player.isPlaying)
                        } else {
                            Text("\(i + 1)")
                        }
                    }
                    .font(.system(size: 10).monospacedDigit())
                    .foregroundStyle(i == now ? YYColor.t1 : YYColor.t3)
                    .frame(width: 20, alignment: .leading)
                    Text(player.title(i))
                        .fontWeight(i == now ? .semibold : .regular)
                        .lineLimit(1)
                    Spacer()
                    if player.offDevice(chapter: i) && i != now {
                        ChapterShareRing(player: player, chapter: i)
                    } else {
                        Text(i == now ? String(localized: "还剩 \(AudiobookPlayer.clock(player.inChapter.of - player.inChapter.at))") : AudiobookPlayer.clock(c.seconds))
                            .font(.system(size: 11).monospacedDigit())
                            .foregroundStyle(YYColor.t3)
                    }
                }
                .font(.system(size: 13))
                .foregroundStyle(i < now || player.offDevice(chapter: i) ? YYColor.t3 : YYColor.t1)
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .listRowBackground(Color.clear)
            .contextMenu {
                Button("改名", systemImage: "pencil") {
                    newName = c.title ?? ""
                    renaming = i
                }
            }
        }
    }

    @ViewBuilder private var bookmarks: some View {
        if player.bookmarks.isEmpty {
            Text("听到想记住的地方，点书签按钮记一下。")
                .font(.footnote)
                .foregroundStyle(YYColor.t3)
                .listRowBackground(Color.clear)
        }
        ForEach(player.bookmarks, id: \.id) { b in
            Button { player.seek(to: player.seconds(of: b)) } label: {
                HStack {
                    Text(player.title(Int(b.section))).lineLimit(1)
                    Spacer()
                    Text(AudiobookPlayer.clock(Double(b.sentence) / 1000))
                        .font(.system(size: 11).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
                .font(.system(size: 13))
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            .listRowBackground(Color.clear)
            .swipeActions { Button("删除", role: .destructive) { player.deleteBookmark(b) } }
            .contextMenu { Button("删除", role: .destructive) { player.deleteBookmark(b) } }
        }
    }
}

/// The cover square, as recordings' covers are; the book's colour when it has none.
struct SquareCover: View {
    let book: BookSummary
    var shadow = true
    @State private var image: CGImage?
    @AppStorage(EInk.key) private var eink = false
    @AppStorage(EInk.grayscaleKey) private var einkGray = true

    var body: some View {
        Color.clear
            .aspectRatio(1, contentMode: .fit)
            .overlay {
                if let image {
                    Image(decorative: image, scale: 1).resizable().scaledToFill()
                } else {
                    ZStack {
                        YYColor.inset
                        Image(systemName: "headphones").font(.system(size: 28)).foregroundStyle(YYColor.t3)
                    }
                }
            }
            .clipShape(.rect(cornerRadius: shadow ? 10 : 4))
            .overlay(RoundedRectangle(cornerRadius: shadow ? 10 : 4).strokeBorder(.black.opacity(0.12), lineWidth: 0.5))
            .grayscale(eink && einkGray ? 1 : 0)
            .shadow(color: .black.opacity(eink || !shadow ? 0 : 0.18), radius: 17, y: 14)
            .task(id: book.coverPath) { image = await Thumbnail.load(book.coverPath, maxPixels: 600) }
    }
}

#if os(iOS)
import AVKit

/// 播放到: a tile like its neighbours — the icon, the words, where it's playing now. The system's
/// AirPlay picker lies over it, clear, and takes the tap.
private struct OutputTile: View {
    @State private var route = OutputTile.current()

    var body: some View {
        VStack(spacing: 3) {
            Image(systemName: "airplayaudio").font(.system(size: 16))
            Text("播放到").font(.system(size: 10, weight: .semibold))
            Text(route).font(.system(size: 10)).foregroundStyle(YYColor.t3).lineLimit(1)
        }
        .frame(maxWidth: .infinity, minHeight: 56)
        .yyCard()
        .overlay { RoutePicker() }
        .fixedSize(horizontal: false, vertical: true)
        .onReceive(NotificationCenter.default.publisher(for: AVAudioSession.routeChangeNotification).receive(on: RunLoop.main)) { _ in
            route = Self.current()
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel(Text("播放到 \(route)"))
    }

    static func current() -> String {
        guard let out = AVAudioSession.sharedInstance().currentRoute.outputs.first else { return String(localized: "这台设备") }
        return out.portType == .builtInSpeaker || out.portType == .builtInReceiver ? String(localized: "这台设备") : out.portName
    }
}

/// The system's AirPlay picker, drawn clear: only its tap is wanted.
private struct RoutePicker: UIViewRepresentable {
    func makeUIView(context: Context) -> AVRoutePickerView {
        let v = AVRoutePickerView()
        v.prioritizesVideoDevices = false
        v.tintColor = .clear
        v.activeTintColor = .clear
        return v
    }

    func updateUIView(_ uiView: AVRoutePickerView, context: Context) {}
}
#endif

#if os(iOS)
private struct SharedFile: Identifiable {
    let url: URL
    var id: URL { url }
}
#endif

/// 没有电子书: the recording made into a book of its own from what's heard, on this device —
/// or paired by hand with an e-book on the shelf when the file name didn't match its title.
struct TextVersionCard: View {
    let library: Library
    let summary: BookSummary
    @State private var pickingBook = false

    private var hasEbooks: Bool {
        library.books.contains { $0.available && $0.format != .pdf && $0.format != .audio && $0.format != .podcast }
    }

    var body: some View {
        let job = Audiobooks.shared?.textJobs[summary.id]
        VStack(alignment: .leading, spacing: 0) {
            Text("这本录音").font(.system(size: 12, weight: .bold)).padding(.top, 10).padding(.bottom, 4)
            if job == nil, hasEbooks {
                HStack(spacing: 10) {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("配上书架上的电子书").font(.system(size: 12))
                        Text("合成以后听到哪句，书页就走到哪句").font(.system(size: 10)).foregroundStyle(YYColor.t3).lineLimit(2)
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    Button("选书…") { pickingBook = true }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                }
                .padding(.vertical, 7)
                .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
            }
            HStack(spacing: 10) {
                VStack(alignment: .leading, spacing: 1) {
                    Text(job == nil ? "没有电子书" : "正在转成文字").font(.system(size: 12))
                    Text(detail(job)).font(.system(size: 10)).foregroundStyle(YYColor.t3).lineLimit(2)
                    if let job, job.problem == nil {
                        ProgressView(value: job.fraction).progressViewStyle(.linear).tint(YYTint.standard.fill).padding(.top, 5)
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                if job == nil {
                    Button("转成文字") { Audiobooks.shared?.makeTextVersion(summary, library: library) }
                        .buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                        .disabled(!Transcriber.available)
                }
            }
            .padding(.vertical, 7)
            .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 2)
        .yyCard()
        .sheet(isPresented: $pickingBook) {
            AudiobookBookPicker(library: library, chosen: nil) { book in
                let probe = Audiobook(id: summary.id, title: summary.title, author: summary.authors.first, files: [], seconds: [])
                library.audiobooks.remember(probe, book)
                library.pair(summary, with: book)
            }
        }
    }

    private func detail(_ job: Audiobooks.TextJob?) -> String {
        guard let job else {
            return Transcriber.available ? String(localized: "把录音转成文字，能翻能搜能划线") : String(localized: "先在系统设置里开启语音识别，才能在这台设备上转写")
        }
        if let p = job.problem { return p }
        let left = job.left.map { String(localized: " · 还要约 \(Audiobook.length($0))") } ?? ""
        return String(localized: "第 \(job.file + 1) / \(job.files) 集") + left
    }
}

/// 这本录音: this recording's own settings, each taking effect at once.
struct RecordingSettings: View {
    let player: AudiobookPlayer

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text("这本录音")
                .font(.system(size: 12, weight: .bold))
                .padding(.top, 10)
                .padding(.bottom, 4)
            row("片头片尾", nil) {
                YYSegments(items: [(true, String(localized: "跳过")), (false, String(localized: "照放"))], selection: binding(\.headTail), small: true)
                    .frame(width: 116)
            }
            row("章间音乐", String(localized: "压到 1.5 秒")) { YYSwitch(isOn: binding(\.shortenChapterGaps), label: String(localized: "章间音乐")) }
            row("人声清楚一点", String(localized: "压低底噪，人声提一点")) { YYSwitch(isOn: binding(\.clearerVoice), label: String(localized: "人声清楚一点")) }
            row("缩短停顿", player.saved >= 60 ? String(localized: "这本已经省了 \(Audiobook.length(player.saved))") : String(localized: "长停顿压短一些")) {
                YYSwitch(isOn: binding(\.shortenPauses), label: String(localized: "缩短停顿"))
            }
            row("没念的用朗读补上", player.unreadCount > 0 ? String(localized: "这本有 \(player.unreadCount) 句没念") : String(localized: "录音每句都念了")) {
                YYSwitch(isOn: binding(\.fillUnread), label: String(localized: "没念的用朗读补上"))
            }
            row("逐词跟读", String(localized: "念到哪个词，哪个词下面一道线")) {
                YYSwitch(isOn: binding(\.wordFollow), label: String(localized: "逐词跟读"))
            }
            if player.streaming {
                row("听过的自动清掉", String(localized: "网盘上的不动 · 这台设备上占 \(ByteCountFormatter.string(fromByteCount: ShareStream.shared.bytesHere(player.bookID, core: player.core), countStyle: .file))")) {
                    YYSwitch(isOn: Binding(get: { player.clearsListened }, set: { player.clearsListened = $0 }), label: String(localized: "听过的自动清掉"))
                }
            }
            if player.hasMap {
                row("导出带朗读时间的电子书", exporting ?? String(localized: "一个 EPUB，别的阅读器也能跟读")) {
                    Button("导出…", action: export).buttonStyle(YYButtonStyle(kind: .plain, size: .small)).disabled(exporting != nil)
                }
            }
        }
        .padding(.horizontal, 12)
        .padding(.bottom, 2)
        .yyCard()
        #if os(iOS)
        .sheet(item: Binding(get: { shared.map(SharedFile.init) }, set: { shared = $0?.url })) { ActivitySheet(items: [$0.url]) }
        #endif
    }

    /// While the package is being written, what's happening.
    @State private var exporting: String?
    #if os(iOS)
    @State private var shared: URL?
    #endif

    /// 导出: the e-book with the recording and each sentence's time, as one EPUB.
    private func export() {
        let name = "\(player.summary.title).epub"
        #if os(macOS)
        let panel = NSSavePanel()
        panel.nameFieldStringValue = name
        panel.allowedContentTypes = [.epub]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        write(to: url, then: { _ in })
        #else
        let url = FileManager.default.temporaryDirectory.appending(path: name)
        write(to: url) { ok in if ok { shared = url } }
        #endif
    }

    private func write(to url: URL, then: @escaping (Bool) -> Void) {
        exporting = String(localized: "正在打包，录音大的要一会儿")
        let core = player.core, id = player.bookID, path = url.path(percentEncoded: false)
        Task {
            let ok = await Self.exportEpub(core, id: id, path: path)
            exporting = ok ? nil : String(localized: "没导出来，再试一次")
            then(ok)
        }
    }

    @concurrent nonisolated private static func exportEpub(_ core: YuyueCore, id: String, path: String) async -> Bool {
        (try? core.audiobookExportEpub(bookId: id, out: path)) != nil
    }

    private func binding(_ key: WritableKeyPath<SkipSettings, Bool>) -> Binding<Bool> {
        Binding(get: { player.settings[keyPath: key] }, set: {
            var s = player.settings
            s[keyPath: key] = $0
            player.set(s)
        })
    }

    private func row(_ name: LocalizedStringKey, _ detail: String?, @ViewBuilder control: () -> some View) -> some View {
        HStack(spacing: 10) {
            VStack(alignment: .leading, spacing: 1) {
                Text(name).font(.system(size: 12))
                if let detail {
                    Text(detail).font(.system(size: 10).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1).minimumScaleFactor(0.85)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            control()
        }
        .padding(.vertical, 7)
        .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5) }
    }
}

#if os(macOS)
/// 章节 on the Mac: one card, each chapter's name and length, the one
/// being heard in bold, those heard grey. 书签, when there are any, follow in the same card.
struct ChapterColumn: View {
    let player: AudiobookPlayer
    @State private var renaming: Int?
    @State private var newName = ""

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    let now = player.chapter
                    ForEach(Array(player.chapters.enumerated()), id: \.offset) { i, c in
                        row(player.title(i), i == now ? String(localized: "还剩 \(AudiobookPlayer.clock(player.inChapter.of - player.inChapter.at))") : AudiobookPlayer.clock(c.seconds),
                            bold: i == now, grey: i < now || player.offDevice(chapter: i), first: i == 0,
                            ring: player.offDevice(chapter: i) && i != now ? i : nil) { player.go(toChapter: i) }
                            .id(i)
                            .contextMenu {
                                Button("改名", systemImage: "pencil") {
                                    newName = c.title ?? ""
                                    renaming = i
                                }
                            }
                    }
                    if !player.bookmarks.isEmpty {
                        Text("书签")
                            .font(.yy(size: 10.5, weight: .semibold))
                            .foregroundStyle(YYColor.t3)
                            .padding(.top, 12)
                            .padding(.bottom, 2)
                        ForEach(player.bookmarks, id: \.id) { b in
                            row(player.title(Int(b.section)), AudiobookPlayer.clock(Double(b.sentence) / 1000), bold: false, grey: false, first: false) {
                                player.seek(to: player.seconds(of: b))
                            }
                            .contextMenu { Button("删除", role: .destructive) { player.deleteBookmark(b) } }
                        }
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 6)
            }
            .scrollIndicators(.never)
            .onAppear { proxy.scrollTo(player.chapter, anchor: .center) }
        }
        .background(YYColor.card, in: .rect(cornerRadius: 14))
        .overlay(RoundedRectangle(cornerRadius: 14).strokeBorder(YYColor.line, lineWidth: 0.5))
        .yyPrompt(String(localized: "给这一集起个名字"), message: String(localized: "留空就叫「第几集」。"),
                  text: $newName, prompt: renaming.map { String(localized: "第 \($0 + 1) 集") } ?? "",
                  isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } }),
                  confirm: String(localized: "保存")) {
            if let i = renaming { player.renameChapter(i, newName) }
            renaming = nil
        }
    }

    private func row(_ title: String, _ detail: String, bold: Bool, grey: Bool, first: Bool, ring: Int? = nil, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Text(title)
                    .font(.yy(size: 12, weight: bold ? .semibold : .regular))
                    .foregroundStyle(grey ? YYColor.t3 : YYColor.t1)
                    .lineLimit(1)
                Spacer(minLength: 8)
                if let ring {
                    ChapterShareRing(player: player, chapter: ring)
                } else {
                    Text(detail)
                        .font(.yy(size: 10.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
            }
            .padding(.vertical, 7)
            .contentShape(.rect)
            .overlay(alignment: .top) { if !first { Rectangle().fill(YYColor.line).frame(height: 0.5) } }
        }
        .buttonStyle(.plain)
    }
}
#endif
