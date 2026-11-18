import SwiftUI

/// The note after something happened. A plain one is a small white capsule that flashes at the
/// top; one that can be undone is the wide glass banner instead,
/// so the two never look alike.
struct UndoToast: View {
    let toast: Library.Toast
    let undo: () -> Void
    /// Pauses countdown while the pointer hovers over the banner on Mac.
    static var hoveredID: UUID?

    /// A green tick for a plain note; a deletion is red.
    static let done = Color(red: 0x2D / 255, green: 0xBE / 255, blue: 0x60 / 255)

    /// Sizes are drawn at the phone's own pixels (a point is 1.2 of them) and the Mac
    /// window at its real size, so the Mac's banner and capsule are a fifth smaller.
    #if os(macOS)
    static let k: CGFloat = 1
    static let bannerWidth: CGFloat = 360
    #else
    static let k: CGFloat = 1.2
    static let bannerWidth: CGFloat = 460
    #endif

    /// Every screen puts it centred at the top, coming down from above.
    static var transition: AnyTransition {
        YYMotion.reduce
            ? .opacity
            : .offset(y: -12).combined(with: .opacity).combined(with: .scale(scale: 0.95, anchor: .top))
    }

    var body: some View {
        if toast.undo != nil {
            UndoBanner(toast: toast, undo: undo)
        } else {
            NoteCapsule(toast: toast)
        }
    }
}

/// White capsule, as wide as its one line: a round red or green dot with a tick, then the words.
private struct NoteCapsule: View {
    let toast: Library.Toast
    private let k = UndoToast.k
    /// 墨水屏 has no glass.
    @AppStorage(EInk.key) private var eink = false

    var body: some View {
        HStack(spacing: 7 * k) {
            ZStack {
                Circle().fill(toast.danger ? YYColor.danger : UndoToast.done)
                Image(systemName: "checkmark")
                    .font(.yy(size: 7 * k, weight: .heavy))
                    .foregroundStyle(.white)
            }
            .frame(width: 15 * k, height: 15 * k)
            Text(toast.detail.isEmpty ? toast.title : "\(toast.title) · \(toast.detail)")
                .font(.yy(size: 11.5 * k))
                .foregroundStyle(YYColor.t1)
                .lineLimit(1)
        }
        .padding(.leading, 11 * k)
        .padding(.trailing, 14 * k)
        .frame(height: 34 * k)
        .yyGlass(Capsule(), fill: YYColor.card, line: YYColor.line, shadow: 0.09, radius: 12, y: 4, liquid: !eink)
        .frame(maxWidth: 420)
        .accessibilityElement(children: .combine)
        .accessibilityAddTraits(.updatesFrequently)
    }
}

/// The glass banner across the top: a red ring counting the seconds down, the two lines, and a
/// round glass button with the undo arrow. At 0 the ring closes into a red tick and the button
/// goes; Library takes the toast off a moment later, which lifts the banner away.
private struct UndoBanner: View {
    let toast: Library.Toast
    let undo: () -> Void
    private let k = UndoToast.k
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    @AppStorage(EInk.key) private var eink = false
    @State private var remaining = Library.undoSeconds
    @State private var hovered = false
    @State private var done = false

    var body: some View {
        HStack(spacing: 12 * k) {
            CountdownRing(left: max(0, remaining / Library.undoSeconds), done: done, danger: toast.danger)
            .frame(width: 26 * k, height: 26 * k)
            VStack(alignment: .leading, spacing: 2 * k) {
                Text(toast.title)
                    .font(.yy(size: 13 * k, weight: .semibold))
                    .foregroundStyle(YYColor.t1)
                if !toast.detail.isEmpty {
                    Text(toast.detail)
                        .font(.yy(size: 10.5 * k))
                        .foregroundStyle(YYColor.t2)
                }
            }
            .lineLimit(1)
            .frame(maxWidth: .infinity, alignment: .leading)
            // A round glass button with only the arrow; it fades as the tick comes.
            Button(action: undo) {
                Image(systemName: "arrow.uturn.backward")
                    .font(.yy(size: 12.5 * k, weight: .semibold))
                    .foregroundStyle(tint.accent)
                    .frame(width: 36 * k, height: 36 * k)
                    .background(YYColor.t1.opacity(0.06), in: .circle)
                    .overlay(Circle().strokeBorder(YYColor.line, lineWidth: 0.5))
                    .contentShape(.circle)
            }
            .buttonStyle(.plain)
            .keyboardShortcut("z")
            .accessibilityLabel("撤销")
            .opacity(done ? 0 : 1)
            .scaleEffect(done ? 0.9 : 1)
            .disabled(done)
        }
        .padding(.leading, 13 * k)
        .padding(.trailing, 10 * k)
        .padding(.vertical, 11 * k)
        // Liquid Glass, or our own glass: near-white over a blur of the page.
        .yyGlass(RoundedRectangle(cornerRadius: 20 * k), fill: YYColor.glass, line: YYColor.line, material: .ultraThinMaterial,
                 shadow: 0.16, radius: 18, y: 8, liquid: !eink)
        .frame(maxWidth: UndoToast.bannerWidth)
        #if os(macOS)
        .onHover { over in
            hovered = over
            if over { UndoToast.hoveredID = toast.id } else if UndoToast.hoveredID == toast.id { UndoToast.hoveredID = nil }
        }
        .onDisappear {
            if UndoToast.hoveredID == toast.id { UndoToast.hoveredID = nil }
        }
        #endif
        .accessibilityElement(children: .combine)
        .accessibilityAction(named: "撤销") { if !done { undo() } }
        .task(id: toast.id) {
            remaining = Library.undoSeconds
            done = false
            while remaining > 0, !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(50))
                if !hovered { remaining = max(0, remaining - 0.05) }
            }
            guard !Task.isCancelled else { return }
            withAnimation(YYMotion.reduce ? nil : .spring(duration: 0.24, bounce: 0)) { done = true }
        }
    }
}

/// A thin ring with the seconds left inside, draining as they go; then a disc with a tick.
private struct CountdownRing: View {
    let left: Double
    let done: Bool
    let danger: Bool
    private let k = UndoToast.k
    private var color: Color { danger ? YYColor.danger : UndoToast.done }

    var body: some View {
        ZStack {
            if done {
                Circle().fill(color)
                    .overlay {
                        Image(systemName: "checkmark")
                            .font(.yy(size: 11 * k, weight: .heavy))
                            .foregroundStyle(.white)
                    }
                    .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.95).combined(with: .opacity))
            } else {
                Circle().stroke(color.opacity(0.18), lineWidth: 2.2 * k)
                // Drains anticlockwise from the top.
                Circle().trim(from: 1 - left, to: 1)
                    .stroke(color, style: StrokeStyle(lineWidth: 2.2 * k, lineCap: .round))
                    .rotationEffect(.degrees(-90))
                Text("\(max(1, Int((left * Library.undoSeconds).rounded(.up))))")
                    .font(.yy(size: 11.5 * k, weight: .bold).monospacedDigit())
                    .foregroundStyle(color)
                    .contentTransition(.numericText(countsDown: true))
                    .animation(YYMotion.reduce ? nil : .spring(duration: 0.24, bounce: 0), value: Int((left * Library.undoSeconds).rounded(.up)))
            }
        }
        .padding(k)
    }
}
