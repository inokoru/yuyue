import SwiftUI
#if os(macOS)
import AppKit
#endif

/// Press feedback for history pills and chips: immediate subtle scale-down on press.
struct HistoryPressStyle: ButtonStyle {
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .scaleEffect(configuration.isPressed && !YYMotion.reduce ? 0.96 : 1)
            .animation(.easeOut(duration: configuration.isPressed ? 0.08 : YYDuration.quick), value: configuration.isPressed)
    }
}

/// A value drawn as what it is: a paper swatch dot, the font in its own face, a mini switch,
/// or a unified voice-preview capsule (`[▶ 名字]` with live waveform while speaking).
/// Shared between the Mac popover and the iPhone 最近改过 page.
struct SettingsHistoryValue: View {
    let look: SettingsLog.Look?
    let raw: String?
    let text: String
    var color: Color = YYColor.t1
    var fontSize: CGFloat = 12
    var strikethrough = false
    var highlighted = false
    var tint: YYTint = .standard

    var body: some View {
        switch look {
        case .voice?:
            if let raw, !raw.isEmpty, let voice = Voices.find(raw) {
                voiceChip(voice)
            } else {
                styledValue { Text(text) }
            }
        case .paper?:
            styledValue {
                HStack(spacing: 5) {
                    paperDot(raw.flatMap { r in
                        YYPaper.all.first { $0.id == r }?.bg ?? Papers.shared.paper(r).map { YYPaper(custom: $0).bg }
                    })
                    Text(text)
                }
            }
        case .font?:
            styledValue {
                Text(text).font(faceFont)
            }
        case .toggle?:
            styledValue {
                HStack(spacing: 5) {
                    let on = raw == "1"
                    Capsule().fill(on ? YYColor.ok : YYColor.tint)
                        .frame(width: 19, height: 11.5)
                        .overlay(alignment: on ? .trailing : .leading) {
                            Circle().fill(.white)
                                .frame(width: 8, height: 8)
                                .padding(1.75)
                                .shadow(color: .black.opacity(0.2), radius: 0.5, y: 0.5)
                        }
                    Text(text)
                }
            }
        case .tint?, nil:
            styledValue { Text(text) }
        }
    }

    @ViewBuilder
    private func styledValue<Content: View>(@ViewBuilder _ content: () -> Content) -> some View {
        content()
            .strikethrough(strikethrough && !highlighted, color: YYColor.t3)
            .foregroundStyle(highlighted ? tint.accent : color)
            .fontWeight(highlighted ? .semibold : nil)
            .padding(.horizontal, highlighted ? 5 : 0)
            .padding(.vertical, highlighted ? 1.5 : 0)
            .background {
                if highlighted {
                    RoundedRectangle(cornerRadius: 5)
                        .fill(tint.accentSoft)
                        .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(tint.edge, lineWidth: 0.6))
                }
            }
            .animation(.easeOut(duration: 0.14), value: highlighted)
    }

    private func voiceChip(_ voice: any Voice) -> some View {
        let playing = PreviewState.shared.voiceID == voice.id
        let active = playing || highlighted
        return Button {
            if playing {
                Voices.stopPreview()
            } else {
                let sample = voice.speaks("en") && !voice.speaks("zh")
                    ? "It was the best of times."
                    : "我冒了严寒，回到相隔二千余里的故乡去。"
                Voices.preview(voice, text: sample)
            }
        } label: {
            HStack(spacing: 4.5) {
                ZStack {
                    Circle()
                        .fill(playing ? tint.accent : YYColor.card)
                        .frame(width: 13.5, height: 13.5)
                        .overlay {
                            if !playing {
                                Circle().strokeBorder(YYColor.line, lineWidth: 0.5)
                            }
                        }
                    if playing {
                        Image(systemName: "waveform")
                            .font(.system(size: 7.5, weight: .bold))
                            .foregroundStyle(.white)
                            .symbolEffect(.variableColor.iterative, isActive: true)
                    } else {
                        Image(systemName: "play.fill")
                            .font(.system(size: 6, weight: .bold))
                            .foregroundStyle(YYColor.t1)
                            .offset(x: 0.4)
                    }
                }
                Text(text)
                    .font(.yy(size: fontSize - 0.5, weight: active ? .semibold : .regular))
                    .strikethrough(strikethrough && !highlighted, color: YYColor.t3)
                    .lineLimit(1)
            }
            .foregroundStyle(active ? tint.accent : YYColor.t1)
            .padding(.leading, 5.5)
            .padding(.trailing, 8)
            .frame(height: 23)
            .background(active ? tint.accentSoft : YYColor.inset, in: .capsule)
            .overlay(Capsule().strokeBorder(active ? tint.edge : YYColor.line, lineWidth: active ? 0.8 : 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(HistoryPressStyle())
        #if os(macOS)
        .help(Text(playing ? String(localized: "停止试听") : String(localized: "试听「\(text)」")))
        #endif
    }

    private func paperDot(_ color: Color?) -> some View {
        Circle()
            .fill(color ?? YYColor.inset)
            .frame(width: 11, height: 11)
            .overlay(Circle().strokeBorder(.primary.opacity(0.18), lineWidth: 0.6))
    }

    private var faceFont: Font {
        let id = raw.map { SettingsLog.faceID($0) } ?? "system"
        return id == "system" ? .yy(size: fontSize) : FontShelf.shared.font(id, size: fontSize + 0.5)
    }
}

#if os(macOS)

/// 最近改过: a clock in the top-right corner of 设置, with a badge of today's active changes.
/// Opens a warm-paper popover grouped by day, folding consecutive edits to the same setting,
/// with hover-to-preview on 改回/还原, 1-tap voice chips, clickable ⌘Z, and inline clear confirmation.
struct SettingsHistoryButton: View {
    @State private var open = false
    @State private var hovering = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    private var log: SettingsLog { .shared }

    var body: some View {
        let today = log.entries.filter { $0.undone != true && Calendar.current.isDateInToday($0.date) }.count
        Button { open.toggle() } label: {
            Image(systemName: "clock.arrow.circlepath")
                .font(.yy(size: 13.5, weight: .regular))
                .foregroundStyle(open || hovering ? YYColor.t1 : YYColor.t2)
                .frame(width: 28, height: 28)
                .background(YYColor.t1.opacity(open ? 0.07 : hovering ? 0.05 : 0), in: .rect(cornerRadius: 8))
                .overlay(alignment: .topTrailing) {
                    if today > 0 {
                        Text("\(today)")
                            .font(.yy(size: 9.5, weight: .semibold).monospacedDigit())
                            .foregroundStyle(.white)
                            .padding(.horizontal, 4)
                            .frame(minWidth: 15, minHeight: 15)
                            .background(tint.accent, in: .capsule)
                            .overlay(Capsule().strokeBorder(YYColor.bg, lineWidth: 1.2))
                            .offset(x: 3, y: -2)
                    }
                }
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .onHover { hovering = $0 }
        .help(Text("最近改过（⌘Z 改回最近一条）"))
        .popover(isPresented: $open, arrowEdge: .bottom) {
            SettingsHistoryCard { open = false }
                .presentationBackground(YYColor.bg)
        }
        #if DEBUG
        .task { if UserDefaults.standard.bool(forKey: "debugHistory") { try? await Task.sleep(for: .seconds(1)); open = true } }
        #endif
    }
}

private struct SettingsHistoryCard: View {
    let close: () -> Void
    private var log: SettingsLog { .shared }
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    @State private var filterPage: String?
    @State private var expanded: Set<UUID> = []
    @State private var feedback: FeedbackNote?
    @State private var confirmingClear = false
    @State private var hoveringCmdZ = false
    @State private var hoveringClear = false

    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private struct FeedbackNote: Equatable {
        let id = UUID()
        let text: String
    }

    var body: some View {
        let pres = log.presentation(filterPage: filterPage)
        VStack(alignment: .leading, spacing: 0) {
            header(pres: pres)
            if log.entries.isEmpty {
                emptyState
            } else {
                listBody(pres: pres)
            }
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            footer(pres: pres)
        }
        .frame(width: 380)
        .background(YYColor.bg)
        .task(id: feedback?.id) {
            guard feedback != nil else { return }
            try? await Task.sleep(for: .seconds(1.8))
            withAnimation(.easeOut(duration: 0.2)) { feedback = nil }
        }
        .task(id: confirmingClear) {
            guard confirmingClear else { return }
            try? await Task.sleep(for: .seconds(4.5))
            withAnimation(.snappy(duration: 0.18)) { confirmingClear = false }
        }
        .onDisappear {
            Voices.stopPreview()
        }
    }

    private func header(pres: SettingsLog.Presentation) -> some View {
        VStack(alignment: .leading, spacing: 9) {
            HStack(spacing: 7) {
                HStack(alignment: .firstTextBaseline, spacing: 7) {
                    Text("最近改过")
                        .font(.yy(size: 14, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                    Text(log.entries.isEmpty ? String(localized: "暂无记录") : String(localized: "\(log.entries.count) 项记录"))
                        .font(.yy(size: 11).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                }
                Spacer(minLength: 4)
                if let feedback {
                    HStack(spacing: 4) {
                        Image(systemName: "checkmark")
                            .font(.system(size: 9, weight: .bold))
                        Text(feedback.text)
                            .font(.yy(size: 11, weight: .medium))
                            .lineLimit(1)
                    }
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, 8)
                    .padding(.vertical, 2.5)
                    .background(tint.accentSoft, in: .capsule)
                    .overlay(Capsule().strokeBorder(tint.edge, lineWidth: 0.5))
                    .transition(.opacity.combined(with: .scale(scale: 0.96)))
                }
            }

            if pres.pageCounts.count > 1 {
                ScrollView(.horizontal, showsIndicators: false) {
                    HStack(spacing: 5) {
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
        .padding(.horizontal, 14)
        .padding(.top, 13)
        .padding(.bottom, 8)
    }

    private func filterPill(title: String, count: Int, selected: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack(spacing: 4) {
                Text(title)
                    .font(.yy(size: 11, weight: selected ? .semibold : .regular))
                    .foregroundStyle(selected ? YYColor.t1 : YYColor.t2)
                Text("\(count)")
                    .font(.yy(size: 10).monospacedDigit())
                    .foregroundStyle(selected ? tint.accent : YYColor.t3)
            }
            .padding(.horizontal, 10)
            .frame(height: 23)
            .background(selected ? YYColor.card : .clear, in: .capsule)
            .overlay(Capsule().strokeBorder(selected ? YYColor.pillLine : YYColor.line, lineWidth: 0.5))
            .contentShape(.capsule)
        }
        .buttonStyle(HistoryPressStyle())
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "clock.arrow.circlepath")
                .font(.system(size: 16, weight: .regular))
                .foregroundStyle(YYColor.t3)
                .frame(width: 38, height: 38)
                .background(YYColor.card, in: .rect(cornerRadius: 11))
                .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(scheme == .dark ? .white.opacity(0.07) : YYColor.line, lineWidth: 0.5))
                .padding(.bottom, 2)
            Text("还没有修改记录")
                .font(.yy(size: 13, weight: .semibold))
                .foregroundStyle(YYColor.t1)
            Text("调整过的设置会记在这里，\n每一项都能随时改回去。")
                .font(.yy(size: 11.5))
                .foregroundStyle(YYColor.t3)
                .multilineTextAlignment(.center)
                .lineSpacing(3)
        }
        .frame(maxWidth: .infinity)
        .padding(.vertical, 36)
        .padding(.horizontal, 24)
    }

    private func listBody(pres: SettingsLog.Presentation) -> some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 10) {
                ForEach(pres.days) { dayGroup in
                    VStack(alignment: .leading, spacing: 5) {
                        Text(SettingsLog.dayName(dayGroup.day))
                            .font(.yy(size: 11, weight: .semibold))
                            .foregroundStyle(YYColor.t3)
                            .padding(.horizontal, 6)

                        VStack(alignment: .leading, spacing: 0) {
                            ForEach(Array(dayGroup.runs.enumerated()), id: \.element.id) { idx, run in
                                if idx > 0 {
                                    rowDivider
                                }
                                let open = expanded.contains(run.id)
                                HistoryRow(
                                    entry: run.head,
                                    superseded: pres.superseded.contains(run.head.id),
                                    olderCount: run.older.count,
                                    isExpanded: open,
                                    tint: tint,
                                    toggleExpand: {
                                        withAnimation(.snappy(duration: 0.2)) {
                                            if open { expanded.remove(run.id) } else { expanded.insert(run.id) }
                                        }
                                    },
                                    onUndo: { performUndo(run.head) },
                                    onRedo: { performRedo(run.head) },
                                    close: close
                                )
                                if !run.older.isEmpty, open {
                                    VStack(alignment: .leading, spacing: 0) {
                                        ForEach(run.older) { sub in
                                            rowDivider
                                            HistoryRow(
                                                entry: sub,
                                                superseded: pres.superseded.contains(sub.id),
                                                olderCount: 0,
                                                isExpanded: false,
                                                isSubStep: true,
                                                tint: tint,
                                                toggleExpand: {},
                                                onUndo: { performUndo(sub) },
                                                onRedo: { performRedo(sub) },
                                                close: close
                                            )
                                        }
                                    }
                                    .background(YYColor.t1.opacity(0.025))
                                }
                            }
                        }
                        .background(YYColor.card, in: .rect(cornerRadius: 12))
                        .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(scheme == .dark ? .white.opacity(0.07) : YYColor.line, lineWidth: 0.5))
                    }
                }
            }
            .padding(.horizontal, 10)
            .padding(.top, 4)
            .padding(.bottom, 10)
        }
        .frame(maxHeight: 416)
        .fixedSize(horizontal: false, vertical: true)
        .mask {
            VStack(spacing: 0) {
                LinearGradient(colors: [.black.opacity(0), .black], startPoint: .top, endPoint: .bottom).frame(height: 6)
                Rectangle()
                LinearGradient(colors: [.black, .black.opacity(0)], startPoint: .top, endPoint: .bottom).frame(height: 8)
            }
        }
    }

    private var rowDivider: some View {
        Rectangle()
            .fill(YYColor.line)
            .frame(height: 0.5)
            .padding(.leading, 49)
            .padding(.trailing, 10)
    }

    @ViewBuilder
    private func footer(pres: SettingsLog.Presentation) -> some View {
        if confirmingClear {
            HStack(spacing: 8) {
                Text("清空记录？当前设置会保留")
                    .font(.yy(size: 11.5, weight: .medium))
                    .foregroundStyle(YYColor.t2)
                Spacer(minLength: 4)
                Button {
                    withAnimation(.snappy(duration: 0.18)) { confirmingClear = false }
                } label: {
                    Text("取消")
                        .font(.yy(size: 11))
                        .foregroundStyle(YYColor.t2)
                        .padding(.horizontal, 10)
                        .frame(height: 24)
                        .background(YYColor.card, in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.line, lineWidth: 0.5))
                        .contentShape(.capsule)
                }
                .buttonStyle(HistoryPressStyle())

                Button {
                    Voices.stopPreview()
                    withAnimation(.snappy(duration: 0.2)) {
                        log.clear()
                        confirmingClear = false
                    }
                } label: {
                    Text("清空")
                        .font(.yy(size: 11, weight: .semibold))
                        .foregroundStyle(YYColor.danger)
                        .padding(.horizontal, 11)
                        .frame(height: 24)
                        .background(YYColor.danger.opacity(0.1), in: .capsule)
                        .overlay(Capsule().strokeBorder(YYColor.danger.opacity(0.3), lineWidth: 0.5))
                        .contentShape(.capsule)
                }
                .buttonStyle(HistoryPressStyle())
            }
            .padding(.horizontal, 12)
            .frame(minHeight: 41)
        } else {
            HStack(spacing: 8) {
                let latest = pres.latestUndoable
                Button {
                    if let latest { performUndo(latest) }
                } label: {
                    HStack(spacing: 6) {
                        Text(verbatim: "⌘Z")
                            .font(.yy(size: 10.5, weight: .semibold))
                            .foregroundStyle(YYColor.t2)
                            .padding(.horizontal, 5.5)
                            .padding(.vertical, 2)
                            .background(YYColor.card, in: .rect(cornerRadius: 4.5))
                            .overlay(RoundedRectangle(cornerRadius: 4.5).strokeBorder(YYColor.line, lineWidth: 0.5))
                        Text(latest.map { String(localized: "改回「\($0.title)」") } ?? String(localized: "无可改回项"))
                            .font(.yy(size: 11.5))
                            .foregroundStyle(hoveringCmdZ && latest != nil ? YYColor.t1 : YYColor.t2)
                            .lineLimit(1)
                    }
                    .padding(.leading, 5)
                    .padding(.trailing, 8)
                    .frame(height: 26)
                    .background(hoveringCmdZ && latest != nil ? YYColor.card : .clear, in: .rect(cornerRadius: 7))
                    .overlay {
                        if hoveringCmdZ && latest != nil {
                            RoundedRectangle(cornerRadius: 7).strokeBorder(YYColor.line, lineWidth: 0.5)
                        }
                    }
                    .contentShape(.rect(cornerRadius: 7))
                }
                .buttonStyle(HistoryPressStyle())
                .disabled(latest == nil)
                .opacity(latest == nil ? 0.45 : 1)
                .onHover { hoveringCmdZ = $0 }

                Spacer(minLength: 6)

                if !log.entries.isEmpty {
                    Button {
                        withAnimation(.snappy(duration: 0.18)) { confirmingClear = true }
                    } label: {
                        Text("清空记录")
                            .font(.yy(size: 11.5))
                            .foregroundStyle(hoveringClear ? YYColor.danger : YYColor.t3)
                            .padding(.horizontal, 8)
                            .frame(height: 25)
                            .background(hoveringClear ? YYColor.danger.opacity(0.09) : .clear, in: .rect(cornerRadius: 7))
                            .contentShape(.rect(cornerRadius: 7))
                    }
                    .buttonStyle(HistoryPressStyle())
                    .onHover { hoveringClear = $0 }
                }
            }
            .padding(.horizontal, 12)
            .frame(minHeight: 41)
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
}

/// One change row inside a day card: page icon, title + page jump + aligned timestamp,
/// old → new with hover-to-preview, optional fold toggle, and a locked-width 64pt action slot.
private struct HistoryRow: View {
    let entry: SettingsLog.Entry
    let superseded: Bool
    let olderCount: Int
    let isExpanded: Bool
    var isSubStep = false
    let tint: YYTint
    let toggleExpand: () -> Void
    let onUndo: () -> Void
    let onRedo: () -> Void
    let close: () -> Void

    @State private var hoveringRow = false
    @State private var previewingUndo = false
    @State private var previewingRedo = false

    var body: some View {
        let page = entry.page.flatMap(SettingsPage.init(rawValue:))
        let undone = entry.undone == true
        HStack(alignment: .center, spacing: 10) {
            Image(systemName: page?.symbol ?? "slider.horizontal.3")
                .font(.yy(size: isSubStep ? 11 : 12, weight: .regular))
                .foregroundStyle(hoveringRow ? tint.accent : YYColor.t2)
                .frame(width: isSubStep ? 24 : 28, height: isSubStep ? 24 : 28)
                .background(hoveringRow ? tint.accentSoft : YYColor.inset, in: .rect(cornerRadius: isSubStep ? 7 : 8))
                .opacity(isSubStep ? 0.72 : 1)

            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(entry.title)
                        .font(.yy(size: 12.5, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .lineLimit(1)
                        .layoutPriority(1)
                    if let page {
                        HStack(spacing: 2) {
                            Text(page.title)
                                .lineLimit(1)
                            Image(systemName: "chevron.right")
                                .font(.system(size: 8.5, weight: .semibold))
                                .opacity(hoveringRow ? 1 : 0.55)
                                .offset(x: hoveringRow ? 1.5 : 0)
                        }
                        .font(.yy(size: 11))
                        .foregroundStyle(hoveringRow ? tint.accent : YYColor.t3)
                        .animation(.easeOut(duration: 0.14), value: hoveringRow)
                    }
                    Spacer(minLength: 6)
                    Text(entry.date.formatted(date: .omitted, time: .shortened))
                        .font(.yy(size: 10.5).monospacedDigit())
                        .foregroundStyle(YYColor.t3)
                        .fixedSize()
                }

                HStack(spacing: 6) {
                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.fromRaw,
                        text: entry.from,
                        color: undone ? YYColor.t1 : YYColor.t2,
                        fontSize: 12,
                        highlighted: previewingUndo,
                        tint: tint
                    )

                    Image(systemName: "arrow.right")
                        .font(.yy(size: 9, weight: .medium))
                        .foregroundStyle(previewingUndo ? tint.accent : YYColor.t3)
                        .scaleEffect(x: previewingUndo ? -1 : 1)
                        .animation(.easeOut(duration: 0.14), value: previewingUndo)

                    SettingsHistoryValue(
                        look: entry.look,
                        raw: entry.toRaw,
                        text: entry.to,
                        color: undone ? YYColor.t3 : (superseded ? YYColor.t2 : YYColor.t1),
                        fontSize: 12,
                        strikethrough: undone,
                        highlighted: previewingRedo,
                        tint: tint
                    )
                    .fontWeight(undone || superseded ? .regular : .semibold)
                }
                .font(.yy(size: 12))
                .lineLimit(1)

                if olderCount > 0 {
                    Button(action: toggleExpand) {
                        HStack(spacing: 3) {
                            Text(isExpanded
                                 ? String(localized: "收起早先 \(olderCount) 次")
                                 : String(localized: "早先 \(olderCount) 次调整"))
                            Image(systemName: isExpanded ? "chevron.up" : "chevron.down")
                                .font(.system(size: 8, weight: .semibold))
                        }
                        .font(.yy(size: 10.5))
                        .foregroundStyle(YYColor.t3)
                        .padding(.horizontal, 7)
                        .frame(height: 19)
                        .background(YYColor.inset, in: .capsule)
                        .contentShape(.capsule)
                    }
                    .buttonStyle(HistoryPressStyle())
                    .padding(.top, 1)
                }
            }
            .opacity(undone ? 0.68 : (superseded ? 0.84 : 1))

            actionSlot(undone: undone)
                .frame(width: 64, alignment: .trailing)
        }
        .padding(.leading, isSubStep ? 15 : 11)
        .padding(.trailing, 10)
        .padding(.vertical, 9.5)
        .background(hoveringRow ? YYColor.t1.opacity(0.032) : .clear)
        .contentShape(.rect)
        .onHover { hoveringRow = $0 }
        .onTapGesture {
            guard let page = entry.page else { return }
            UserDefaults.standard.set(page, forKey: "settings.page")
            close()
        }
        .help(Text(page.map { String(localized: "去“\($0.title)”页") } ?? ""))
    }

    @ViewBuilder
    private func actionSlot(undone: Bool) -> some View {
        if undone, entry.redo != nil {
            Button(action: onRedo) {
                HStack(spacing: 3.5) {
                    Image(systemName: "arrow.uturn.forward")
                        .font(.system(size: 9.5, weight: .semibold))
                    Text("还原")
                        .font(.yy(size: 11.5, weight: previewingRedo ? .semibold : .medium))
                }
                .foregroundStyle(previewingRedo ? tint.accent : YYColor.t2)
                .frame(width: 62, height: 25)
                .background(previewingRedo ? tint.accentSoft : .clear, in: .capsule)
                .overlay(Capsule().strokeBorder(previewingRedo ? tint.edge : YYColor.line, lineWidth: previewingRedo ? 0.8 : 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
            .onHover { previewingRedo = $0 }
            .help(Text("还原为 \(entry.to)"))
        } else if !undone, entry.undo != nil {
            Button(action: onUndo) {
                HStack(spacing: 3.5) {
                    Image(systemName: "arrow.uturn.backward")
                        .font(.system(size: 9.5, weight: .semibold))
                    Text("改回")
                        .font(.yy(size: 11.5, weight: previewingUndo ? .semibold : .medium))
                }
                .foregroundStyle(previewingUndo ? tint.accent : (hoveringRow ? YYColor.t1 : YYColor.t2))
                .frame(width: 62, height: 25)
                .background(previewingUndo ? tint.accentSoft : YYColor.pill, in: .capsule)
                .overlay(Capsule().strokeBorder(previewingUndo ? tint.edge : YYColor.pillLine, lineWidth: previewingUndo ? 0.8 : 0.5))
                .contentShape(.capsule)
            }
            .buttonStyle(HistoryPressStyle())
            .onHover { previewingUndo = $0 }
            .help(Text("改回 \(entry.from)"))
        }
    }
}
#endif
