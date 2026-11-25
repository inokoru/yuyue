import SwiftUI
import YuyueCore

/// Puts a card centred over `rect`, above it when there is room, below it when there is room
/// there, and otherwise clamped inside the viewport so it never clips past the window edges.
struct Above: ViewModifier {
    nonisolated enum Pointer: Equatable {
        /// Card sits above the anchor; pointer at its bottom edge points down.
        case bottom
        /// Card sits below the anchor; pointer at its top edge points up.
        case top
        /// Card overlaps the anchor because neither above nor below had room; no pointer.
        case none
    }

    nonisolated struct Placement: Equatable {
        let origin: CGPoint
        let pointer: Pointer
        let tipX: CGFloat
        let scaleAnchor: UnitPoint
    }

    let rect: CGRect
    let size: CGSize
    let insets: EdgeInsets
    @State private var card = CGSize(width: 332, height: 120)
    @State private var shown = false
    @AppStorage(LiquidGlass.key) private var liquidChosen = false
    @AppStorage(EInk.key) private var eink = false

    init(rect: CGRect, in size: CGSize, insets: EdgeInsets = .init()) {
        self.rect = rect
        self.size = size
        self.insets = insets
    }

    nonisolated static func place(rect: CGRect, card: CGSize, in size: CGSize, insets: EdgeInsets = .init()) -> Placement {
        let gap: CGFloat = 14
        let margin: CGFloat = 12
        let pointerClearance: CGFloat = 8

        let minX = insets.leading + margin
        let maxX = size.width - insets.trailing - card.width - margin
        let x: CGFloat
        if maxX >= minX {
            x = min(max(rect.midX - card.width / 2, minX), maxX)
        } else {
            let available = size.width - insets.leading - insets.trailing
            let centered = insets.leading + (available - card.width) / 2
            x = min(max(0, centered), max(0, size.width - card.width))
        }

        let minY = insets.top + margin
        let bottomEdge = size.height - insets.bottom - margin
        let maxY = bottomEdge - card.height

        let aboveY = rect.minY - card.height - gap
        let belowY = rect.maxY + gap

        let y: CGFloat
        let pointer: Pointer
        if maxY < minY {
            let available = size.height - insets.top - insets.bottom
            let centered = insets.top + (available - card.height) / 2
            y = min(max(0, centered), max(0, size.height - card.height))
            pointer = .none
        } else if aboveY >= minY && aboveY <= maxY {
            y = aboveY
            pointer = .bottom
        } else if belowY >= minY && belowY <= maxY {
            y = belowY
            pointer = .top
        } else if belowY < minY {
            y = minY
            pointer = .none
        } else if aboveY > maxY {
            y = maxY
            pointer = .none
        } else {
            // Neither side fits with the full gap (e.g. a full-column or cross-column selection):
            // pin inside the viewport on whichever side leaves more room outside the selection.
            let spaceAbove = max(0, rect.minY - minY)
            let spaceBelow = max(0, bottomEdge - rect.maxY)
            y = spaceAbove > spaceBelow ? minY : maxY
            if y + card.height + pointerClearance <= rect.minY {
                pointer = .bottom
            } else if y >= rect.maxY + pointerClearance {
                pointer = .top
            } else {
                pointer = .none
            }
        }

        // The little point at the words, kept off the rounded corners.
        let cornerInset = min(22, card.width / 2)
        let tipX = min(max(rect.midX - x, cornerInset), card.width - cornerInset)
        let anchorX = card.width > 0 ? min(max(tipX / card.width, 0), 1) : 0.5
        let anchorY: CGFloat = switch pointer {
        case .bottom: 1
        case .top: 0
        case .none: 0.5
        }
        return Placement(
            origin: CGPoint(x: x, y: y),
            pointer: pointer,
            tipX: tipX,
            scaleAnchor: UnitPoint(x: anchorX, y: anchorY)
        )
    }

    func body(content: Content) -> some View {
        let p = Self.place(rect: rect, card: card, in: size, insets: insets)
        content
            .background(alignment: p.pointer == .bottom ? .bottomLeading : .topLeading) {
                // Glass cards have no point; overlapping cards drop it too.
                if p.pointer != .none, !(liquidChosen && LiquidGlass.supported && !eink) {
                    let down = p.pointer == .bottom
                    RoundedRectangle(cornerRadius: 2).fill(YYColor.card)
                        .frame(width: 11, height: 11)
                        .rotationEffect(.degrees(45))
                        // Behind the card, so only the part sticking out shows its edge on white paper.
                        .shadow(color: .black.opacity(eink ? 0 : 0.12), radius: 2, y: down ? 1.5 : -1.5)
                        .offset(x: p.tipX - 5.5, y: down ? 5 : -5)
                }
            }
            .onGeometryChange(for: CGSize.self) { $0.size } action: { card = $0 }
            .scaleEffect(shown || YYMotion.reduce ? 1 : 0.92, anchor: p.scaleAnchor)
            .opacity(shown ? 1 : 0)
            .onAppear { withAnimation(YYMotion.standard) { shown = true } }
            .offset(x: p.origin.x, y: p.origin.y)
    }
}

// MARK: - The card over selected text

/// 选中文字: the four highlight colours (1–4) with the last one used ringed, the two lines, then
/// 想法 · 从这听 · 问 · 翻译 · 复制. Over a highlight already made, it changes its colour or
/// takes it off.
struct SelectionCard: View {
    let model: ReaderModel
    /// The highlight clicked, or nil for a fresh selection.
    let current: Mark?
    let thought: () -> Void
    var translate: (() -> Void)? = nil
    /// 问 and 分享, where the device has them.
    var ask: (() -> Void)?
    var share: (() -> Void)?
    @Environment(\.colorScheme) private var scheme
    private let tint = YYTint.standard
    #if os(iOS)
    private static let touch = true
    #else
    private static let touch = false
    #endif

    var body: some View {
        let hasNote = current?.note?.isEmpty == false
        VStack(spacing: 0) {
            if let note = current?.note, !note.isEmpty {
                Button(action: thought) {
                    HStack(spacing: Self.touch ? 8 : 6) {
                        HStack(spacing: 8) {
                            Rectangle().fill(tint.accent).frame(width: 1.5)
                            Text(note)
                                .font(.yy(size: Self.touch ? 13 : 12))
                                .foregroundStyle(YYColor.t1)
                                .lineLimit(3)
                                .multilineTextAlignment(.leading)
                        }
                        .fixedSize(horizontal: false, vertical: true)
                        Spacer(minLength: 4)
                        Image(systemName: "pencil")
                            .font(.yy(size: 12))
                            .foregroundStyle(tint.accent)
                    }
                    .padding(.horizontal, Self.touch ? 12 : 16)
                    .padding(.vertical, Self.touch ? 9 : 8)
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                Rectangle().fill(YYColor.line).frame(height: 0.5)
            }
            HStack(alignment: .center, spacing: 0) {
                HStack(alignment: .top, spacing: Self.touch ? 10 : 14) {
                    ForEach(Array(MarkStyle.colours.enumerated()), id: \.offset) { i, style in
                        Button { pick(style) } label: {
                            VStack(spacing: Self.touch ? 5 : 4) {
                                swatch(style)
                                #if os(macOS)
                                // The key that makes it, with the text still selected.
                                Text("\(i + 1)")
                                    .font(.yy(size: 10, weight: .semibold))
                                    .foregroundStyle(ringed(style) ? YYColor.t1 : YYColor.t2)
                                    .frame(width: 16, height: 15)
                                    .background(YYColor.inset, in: .rect(cornerRadius: 4))
                                #endif
                            }
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .help("\(style.name) · 按 \(i + 1)")
                        .accessibilityLabel(style.name)
                        .accessibilityAddTraits(ringed(style) ? .isSelected : [])
                    }
                }
                Spacer(minLength: 12)
                HStack(spacing: Self.touch ? 8 : 7) {
                    ForEach(MarkStyle.lines, id: \.self) { style in
                        lineButton(style)
                    }
                    if let current {
                        Button { model.deleteMark(current) } label: {
                            Image(systemName: "trash")
                                .font(.yy(size: 12.5))
                                .foregroundStyle(YYColor.danger)
                                .frame(width: Self.touch ? 30 : 26, height: Self.touch ? 30 : 26)
                                .contentShape(.circle)
                        }
                        .accessibilityLabel(Text("删除划线"))
                        .buttonStyle(.plain)
                        .help("删除划线")
                    }
                }
            }
            .padding(.horizontal, Self.touch ? 12 : 16)
            .padding(.top, Self.touch ? 10 : 9)
            .padding(.bottom, Self.touch ? 8 : 7)
            Rectangle().fill(YYColor.line).frame(height: 0.5)
            HStack(spacing: 0) {
                action(hasNote ? String(localized: "改想法") : String(localized: "想法"), "square.and.pencil", thought, accent: hasNote)
                action(String(localized: "从这听"), "speaker.wave.2") {
                    if let current {
                        model.selection = TextSelection(section: current.section, start: .init(s: Int(current.start.sentence), o: 0), end: .init(s: Int(current.end.sentence), o: 0), text: current.text, rect: .zero)
                    }
                    model.readFromSelection()
                }
                if Self.touch, current == nil, model.contextLine != nil {
                    action(String(localized: "谁说的"), "person.crop.circle") { model.pickSpeakerFromMenu() }
                }
                if current == nil, let sel = model.selection, model.canPin {
                    action(String(localized: "对准"), "scope") { model.pinHere(section: sel.section, sentence: sel.start.s) }
                }
                if let ask { action(String(localized: "问"), "sparkle", ask, accent: true) }
                if let translate { action(String(localized: "翻译"), "translate", translate) }
                action(String(localized: "复制"), "doc.on.doc") { model.copySelection() }
                if let share { action(String(localized: "分享"), "square.and.arrow.up", share) }
            }
            .padding(.horizontal, Self.touch ? 4 : 6)
            .padding(.vertical, Self.touch ? 6 : 9)
        }
        .frame(width: Self.touch ? 340 : 332)
        .yyFloatCard(.rect(cornerRadius: YYRadius.block), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
    }

    private func ringed(_ style: MarkStyle) -> Bool {
        (current?.style ?? model.lastStyle) == style
    }

    private func swatch(_ style: MarkStyle) -> some View {
        ZStack {
            if style == .wave {
                Circle().fill(Self.touch ? YYColor.hlR.opacity(0.14) : YYColor.inset)
                Text("~")
                    .font(.yy(size: 17, weight: .medium))
                    .foregroundStyle(YYColor.hlR)
                    .offset(y: -1.5)
            } else {
                Circle().fill(style.fill)
            }
        }
        .frame(width: Self.touch ? 22 : 20, height: Self.touch ? 22 : 20)
        .padding(Self.touch ? 3 : 2.5)
        .overlay(Circle().strokeBorder(ringed(style) ? tint.accent : .clear, lineWidth: 1.5))
    }

    private func lineButton(_ style: MarkStyle) -> some View {
        let selected = ringed(style)
        let ink = selected ? tint.accent : YYColor.t1
        let radius: CGFloat = Self.touch ? 8.5 : 8
        return Button { pick(style) } label: {
            VStack(spacing: Self.touch ? 3.5 : 3) {
                Text("A")
                    .font(YYUI.serif(Self.touch ? 14.5 : 13.5, .medium))
                    .foregroundStyle(ink)
                    .frame(height: Self.touch ? 14 : 13)
                if style == .dotted {
                    HStack(spacing: Self.touch ? 2.9 : 2.5) {
                        ForEach(0 ..< 4, id: \.self) { _ in
                            Circle()
                                .fill(ink)
                                .frame(width: Self.touch ? 2.8 : 2.6, height: Self.touch ? 2.8 : 2.6)
                        }
                    }
                    .frame(height: 3.5)
                } else {
                    Capsule()
                        .fill(ink)
                        .frame(width: Self.touch ? 20 : 18, height: Self.touch ? 2.2 : 2)
                        .frame(height: 3.5)
                }
            }
            .frame(width: Self.touch ? 36 : 32, height: Self.touch ? 34 : 32)
            .background(selected ? tint.soft : YYColor.inset, in: .rect(cornerRadius: radius))
            .overlay(
                RoundedRectangle(cornerRadius: radius)
                    .strokeBorder(selected ? tint.accent : YYColor.pillLine, lineWidth: selected ? 1.5 : 0.75)
            )
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.94))
        .help(style.name)
        .accessibilityLabel(style.name)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }

    private func pick(_ style: MarkStyle) {
        if let current {
            model.setStyle(style, of: current)
            model.dismissCards()
        } else {
            model.mark(style)
        }
    }

    private func action(_ title: String, _ symbol: String, _ run: @escaping () -> Void, accent: Bool = false) -> some View {
        Button(action: run) {
            VStack(spacing: 4) {
                Image(systemName: symbol).font(.yy(size: Self.touch ? 13.5 : 14.5)).frame(height: 16)
                Text(title).font(.yy(size: Self.touch ? 10.5 : 11, weight: accent ? .semibold : .regular))
            }
            .foregroundStyle(accent ? tint.accent : YYColor.t1)
            .frame(maxWidth: .infinity)
            .frame(height: Self.touch ? 44 : 41)
            .contentShape(.rect)
        }
        .buttonStyle(YYPressStyle(scale: 0.96))
    }
}

/// The words of a highlight drawn as in the book: a colour behind, or a line under (the red
/// one wavy, as on the page).
struct MarkText: View {
    let mark: Mark

    var body: some View {
        if mark.style == .wave {
            if #available(macOS 15.0, iOS 18.0, *) {
                Text(mark.text).customAttribute(WavyLine()).textRenderer(WavyLineRenderer())
            } else {
                Text(mark.text).underline(true, pattern: .dot, color: YYColor.danger)
            }
        } else {
            Text(attributed)
        }
    }

    private var attributed: AttributedString {
        var text = AttributedString(mark.text)
        switch mark.style {
        case .yellow, .blue, .pink: text.backgroundColor = mark.style.fill
        case .underline: text.underlineStyle = .single
        default: text.underlineStyle = Text.LineStyle(pattern: .dot)
        }
        return text
    }
}

@available(macOS 15.0, iOS 18.0, *)
struct WavyLine: TextAttribute {}

/// Draws a red wave under every line of text marked `WavyLine`.
@available(macOS 15.0, iOS 18.0, *)
struct WavyLineRenderer: TextRenderer {
    func draw(layout: Text.Layout, in ctx: inout GraphicsContext) {
        for line in layout {
            ctx.draw(line)
            for run in line where run[WavyLine.self] != nil {
                let r = run.typographicBounds.rect
                var path = Path()
                let y = r.maxY + 1.5, step = 3.0
                path.move(to: CGPoint(x: r.minX, y: y))
                var x = r.minX, up = true
                while x < r.maxX {
                    let next = min(x + step, r.maxX)
                    path.addQuadCurve(to: CGPoint(x: next, y: y), control: CGPoint(x: (x + next) / 2, y: y + (up ? -2.2 : 2.2)))
                    x = next
                    up.toggle()
                }
                ctx.stroke(path, with: .color(YYColor.hlR), lineWidth: 1)
            }
        }
    }
}

extension Int64 {
    /// "今天" · "10月3日"
    var dayText: String {
        let date = Date(timeIntervalSince1970: Double(self) / 1000)
        if Calendar.current.isDateInToday(date) { return String(localized: "今天") }
        if Calendar.current.isDateInYesterday(date) { return String(localized: "昨天") }
        return date.formatted(.dateTime.month(.defaultDigits).day())
    }
}

#if os(iOS)
/// Liquid Glass when it's on (液态玻璃效果), frosted otherwise.
struct CapsuleGlass: ViewModifier {
    func body(content: Content) -> some View {
        content.yyGlass(Capsule(), fill: .clear, line: YYColor.line, shadow: 0.08, radius: 10, y: 4)
    }
}

/// 想法 on the phone.
struct PhoneThoughtSheet: View {
    @State private var confirming: YYConfirm?
    let mark: Mark
    var subtitle: String = ""
    let save: (String?) -> Void
    let cancel: () -> Void
    @State private var text = ""
    @FocusState private var focused: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            YYSheetHeader(title: String(localized: "想法"), subtitle: subtitle.isEmpty ? nil : subtitle, close: cancel)
                .padding(.horizontal, YYUI.s(20))
                .padding(.top, YYUI.sheetGrabberTop)
                .padding(.bottom, YYUI.s(4))
            VStack(alignment: .leading, spacing: YYUI.s(10)) {
                Text("“\(mark.text)”")
                    .font(YYUI.font(13, design: .serif))
                    .foregroundStyle(YYColor.t2)
                    .lineLimit(4)
                    .padding(.leading, 10)
                    .overlay(alignment: .leading) { Rectangle().fill(YYColor.line).frame(width: 2) }
                TextEditor(text: $text)
                    .focused($focused)
                    .scrollContentBackground(.hidden)
                    .padding(YYUI.s(12))
                    .frame(minHeight: 90)
                    .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(12)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(12)).strokeBorder(YYColor.line, lineWidth: 0.5))
            }
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(12))
            HStack {
                if mark.note != nil {
                    Button("删掉想法", role: .destructive) { confirming = YYConfirm(title: String(localized: "删掉这条想法？"), message: String(localized: "划线还在，只删写下的想法。"), button: String(localized: "删掉")) { save(nil) } }
                        .font(YYUI.font(12, .medium))
                        .foregroundStyle(YYColor.danger)
                        .buttonStyle(.plain)
                }
                Spacer(minLength: 0)
            }
            .padding(.horizontal, YYUI.s(20))
            .padding(.top, YYUI.s(8))
            YYSheetFooter(
                secondaryTitle: String(localized: "取消"),
                onSecondary: cancel,
                primaryTitle: String(localized: "保存"),
                onPrimary: { save(text) },
                spread: true
            )
        }
        .background(YYColor.bg)
        .presentationDetents([.medium])
        .presentationBackground(YYColor.bg)
        .presentationDragIndicator(.visible)
        .onAppear {
            text = mark.note ?? ""
            focused = true
        }
        .yyConfirm($confirming)
    }
}
#endif
