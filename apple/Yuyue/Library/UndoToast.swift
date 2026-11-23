import SwiftUI

/// The note after something happened: one white capsule at the top, a coloured disc with a
/// tick and one line of words. One that can be undone has a round undo button at its end; when
/// the seconds are up the button folds away and it is a plain note for a moment before it goes.
struct UndoToast: View {
    let toast: Library.Toast
    let undo: () -> Void
    var dismiss: (() -> Void)? = nil
    /// Pauses countdown while the pointer hovers over the note on Mac.
    static var hoveredID: UUID?

    /// A green tick for a plain note; a deletion is red.
    static let done = Color(red: 0x2D / 255, green: 0xBE / 255, blue: 0x60 / 255)

    /// Sizes are drawn at the phone's own pixels (a point is 1.2 of them) and the Mac
    /// window at its real size, so the Mac's capsule is a fifth smaller.
    #if os(macOS)
    static let k: CGFloat = 1
    #else
    static let k: CGFloat = 1.2
    #endif

    /// Every screen puts it centred at the top, coming down from above.
    static var transition: AnyTransition {
        YYMotion.reduce
            ? .opacity
            : .offset(y: -10).combined(with: .opacity).combined(with: .scale(scale: 0.97, anchor: .top))
    }

    private static let fill = Color.yyDynamic(light: YYRGBA(252, 252, 252), dark: YYRGBA(42, 42, 44))
    private static let edge = Color.yyDynamic(light: YYRGBA(0, 0, 0, 0.14), dark: YYRGBA(255, 255, 255, 0.12))
    private static let button = Color.yyDynamic(light: YYRGBA(235, 235, 235), dark: YYRGBA(58, 58, 60))
    private static let buttonHover = Color.yyDynamic(light: YYRGBA(226, 226, 226), dark: YYRGBA(68, 68, 70))

    private let k = UndoToast.k
    @State private var hovered = false
    @State private var overButton = false
    @State private var done = false
    #if os(iOS)
    @State private var dragY: CGFloat = 0
    @State private var dismissed = false
    #endif
    /// 墨水屏 has no shadow.
    @AppStorage(EInk.key) private var eink = false

    private var canUndo: Bool { toast.undo != nil && !done }
    private var isError: Bool { toast.icon.hasPrefix("exclamationmark") || toast.icon == "thermometer.high" }
    private var isCloud: Bool { !toast.danger && !isError && toast.icon == "icloud.and.arrow.down" }
    private var hasDetail: Bool { !toast.detail.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }

    var body: some View {
        let shape = RoundedRectangle(cornerRadius: 20 * k)
        HStack(spacing: 0) {
            if isCloud {
                ZStack {
                    Circle().fill(YYColor.inset)
                    Image(systemName: "icloud.and.arrow.down")
                        .font(.yy(size: 10.5 * k, weight: .semibold))
                        .foregroundStyle(YYColor.t2)
                }
                .frame(width: 22 * k, height: 22 * k)
                .padding(.trailing, 10 * k)
            } else {
                ZStack {
                    Circle().fill((toast.danger || isError) ? YYColor.danger : UndoToast.done)
                    Image(systemName: isError ? "exclamationmark" : "checkmark")
                        .font(.yy(size: 8 * k, weight: .bold))
                        .foregroundStyle(.white)
                }
                .frame(width: 17 * k, height: 17 * k)
                .padding(.trailing, 10 * k)
            }
            VStack(alignment: .leading, spacing: 1.5 * k) {
                Text(toast.title)
                    .font(.yy(size: 13 * k, weight: .medium))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                if hasDetail {
                    Text(toast.detail)
                        .font(.yy(size: 11.5 * k))
                        .foregroundStyle(YYColor.t2)
                        .lineLimit(2)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            .padding(.vertical, hasDetail ? 8 * k : 4 * k)
            if canUndo {
                Button(action: undo) {
                    Image(systemName: "arrow.uturn.backward")
                        .font(.yy(size: 10.5 * k, weight: .semibold))
                        .foregroundStyle(YYColor.t1)
                        .frame(width: 24 * k, height: 24 * k)
                        .background(overButton ? UndoToast.buttonHover : UndoToast.button, in: .circle)
                        #if os(iOS)
                        .contentShape(Circle().inset(by: -6))
                        #else
                        .contentShape(.circle)
                        #endif
                }
                .buttonStyle(YYPressStyle())
                .keyboardShortcut("z")
                .help(String(localized: "撤销"))
                .accessibilityLabel(String(localized: "撤销"))
                #if os(macOS)
                .onHover { overButton = $0 }
                #endif
                .padding(.leading, 13 * k)
                .transition(YYMotion.reduce ? .opacity : .scale(scale: 0.92).combined(with: .opacity))
            }
        }
        .padding(.leading, 14 * k)
        .padding(.trailing, (canUndo ? 8 : 18) * k)
        .frame(minHeight: 40 * k)
        .background(UndoToast.fill, in: shape)
        .overlay(shape.strokeBorder(UndoToast.edge, lineWidth: 0.5))
        .shadow(color: .black.opacity(eink ? 0 : 0.03), radius: 1, y: 1)
        .shadow(color: .black.opacity(eink ? 0 : 0.07), radius: 11, y: 6)
        .frame(maxWidth: 420)
        #if os(iOS)
        .offset(y: dismissed ? -60 : (YYMotion.reduce ? 0 : (dragY <= 0 ? dragY : dragY * 0.18)))
        .opacity(dismissed ? 0 : 1)
        .allowsHitTesting(!dismissed)
        .accessibilityHidden(dismissed)
        .simultaneousGesture(
            DragGesture(minimumDistance: 8)
                .onChanged { value in
                    guard !dismissed else { return }
                    if !YYMotion.reduce {
                        dragY = value.translation.height
                    }
                }
                .onEnded { value in
                    guard !dismissed else { return }
                    let shouldDismiss = value.translation.height < -24
                        || (!YYMotion.reduce && value.predictedEndTranslation.height < -80)
                    if shouldDismiss {
                        if let dismiss {
                            withAnimation(YYMotion.exit) { dismiss() }
                        } else {
                            withAnimation(YYMotion.exit) { dismissed = true }
                        }
                    } else if dragY != 0 {
                        withAnimation(YYMotion.standard) { dragY = 0 }
                    }
                }
        )
        #endif
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
        .accessibilityLabel(toast.detail.isEmpty ? toast.title : "\(toast.title) \(toast.detail)")
        .accessibilityAddTraits(.updatesFrequently)
        .accessibilityAction(named: "撤销") { if canUndo { undo() } }
        .task(id: toast.id) {
            guard toast.undo != nil else { return }
            var remaining = Library.undoSeconds
            done = false
            while remaining > 0, !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(50))
                if !hovered { remaining = max(0, remaining - 0.05) }
            }
            guard !Task.isCancelled else { return }
            withAnimation(YYMotion.reduce ? nil : .spring(duration: 0.35, bounce: 0)) { done = true }
        }
    }
}
