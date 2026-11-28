import SwiftUI
#if os(macOS)
import AppKit
#else
import UIKit
#endif

/// The app's own pieces, for the screens drawn
/// with them instead of a system form. Sizes are design pixels, scaled up from a
/// phone mock to points.
enum YYUI {
    #if os(macOS)
    static let k: CGFloat = 1.13
    #else
    static let k: CGFloat = 1.2
    #endif

    /// A design size in points.
    static func s(_ px: CGFloat) -> CGFloat { px * k }

    /// Top inset for custom sheets (without a navigation bar).
    /// iOS clears both the rounded sheet top corners and, when a grabber pill is shown, the
    /// overlaid drag indicator (~10pt bottom); macOS keeps compact popover/sheet rhythm.
    #if os(iOS)
    static let sheetTop: CGFloat = s(18)           // 21.6pt — single-detent sheet without grabber
    static let sheetGrabberTop: CGFloat = s(22)    // 26.4pt — sheet with drag indicator / multi-detent
    static let sheetCardTop: CGFloat = 28          // card-led half sheet (ListenCard / TypesetCard)
    #else
    static let sheetTop: CGFloat = s(16)
    static let sheetGrabberTop: CGFloat = s(16)
    static let sheetCardTop: CGFloat = 16
    #endif

    static func font(_ px: CGFloat, _ weight: Font.Weight = .regular, design: Font.Design = .default) -> Font {
        design == .serif ? serif(s(px), weight) : .yy(size: s(px), weight: weight, design: design)
    }

    /// `--serif`: Songti. The system serif (New York) borrows its Chinese
    /// from a fallback that sets every ，and 。 nearly two characters wide.
    static func serif(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        let size = size * TextScale.shared.factor
        return hasSongti ? .custom("Songti SC", fixedSize: size).weight(weight) : .system(size: size, weight: weight, design: .serif)
    }

    #if os(macOS)
    private static let hasSongti = NSFont(name: "Songti SC", size: 12) != nil
    #else
    private static let hasSongti = UIFont(name: "Songti SC", size: 12) != nil
    #endif
}

/// 大字体: the system text size as a factor on the fixed sizes, on iPhone and iPad (the
/// Mac has no such setting). Between 0.85 and 1.5, so the drawn layouts still hold. Views read it
/// through `Font.yy`, so they draw again when the setting changes.
@Observable final class TextScale {
    static let shared = TextScale()
    private(set) var factor: CGFloat = 1

    private init() {
        #if os(iOS)
        update()
        NotificationCenter.default.addObserver(forName: UIContentSizeCategory.didChangeNotification, object: nil, queue: .main) { _ in
            MainActor.assumeIsolated { TextScale.shared.update() }
        }
        #endif
    }

    #if os(iOS)
    private func update() {
        factor = min(1.5, max(0.85, UIFontMetrics(forTextStyle: .body).scaledValue(for: 17) / 17))
    }
    #endif
}

extension Font {
    /// `.system(size:)` that follows 大字体.
    static func yy(size: CGFloat, weight: Font.Weight = .regular, design: Font.Design = .default) -> Font {
        .system(size: size * TextScale.shared.factor, weight: weight, design: design)
    }
}

/// A page of these pieces on its grey ground.
struct YYPage<Content: View>: View {
    var background: Color = YYColor.bg
    @ViewBuilder let content: Content

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) { content }
                .padding(.horizontal, YYUI.s(16))
                .padding(.bottom, YYUI.s(20))
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(alignment: .top) { YYScrollOffsetAnchor() }
        }
        .yyScrollOffsetContainer()
        .scrollBounceBehavior(.basedOnSize)
        .background(background)
    }
}

/// A sheet's middle: as tall as what it holds, and only then a scroll, so the
/// footer sits under the rows instead of at the bottom of a tall empty frame.
struct YYFitScroll<Content: View>: View {
    var maxHeight: CGFloat
    /// The height the rows actually take, so a Mac sheet can size itself to them.
    var measured: Binding<CGFloat>? = nil
    @ViewBuilder var content: () -> Content
    @State private var contentHeight: CGFloat = 0

    var body: some View {
        // A ScrollView's ideal height is only the rows. A fitted sheet then clips
        // the header and the footer that sit outside it. Short content is just the
        // rows; a long list is a scroll of `maxHeight`.
        let fits = contentHeight == 0 || contentHeight <= maxHeight
        Group {
            if fits {
                content()
                    .fixedSize(horizontal: false, vertical: true)
                    .background {
                        GeometryReader { proxy in
                            Color.clear.preference(key: YYFitHeight.self, value: proxy.size.height)
                        }
                    }
                    .yySheetBlock()
            } else {
                ScrollView { content() }
                    .frame(height: maxHeight)
                    .yySheetBlock()
            }
        }
        .onPreferenceChange(YYFitHeight.self) { new in
            guard new > 0, abs(new - contentHeight) > 0.5 else { return }
            contentHeight = new
            measured?.wrappedValue = new
        }
    }
}

private struct YYFitHeight: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}

/// Heights of the header, the rows and the footer, added together.
struct YYSheetMeasure: PreferenceKey {
    static var defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value += nextValue() }
}

extension View {
    /// Reports this block's height so a Mac sheet can be exactly the paper.
    func yySheetBlock() -> some View {
        background {
            GeometryReader { proxy in
                Color.clear.preference(key: YYSheetMeasure.self, value: proxy.size.height)
            }
        }
    }
}

/// A heading over a card, a grey note on its right.
struct YYLabel: View {
    let title: String
    var trailing: String?

    init(_ title: String, _ trailing: String? = nil) {
        self.title = title
        self.trailing = trailing
    }

    var body: some View {
        HStack(alignment: .firstTextBaseline) {
            Text(title).font(YYUI.font(12.5, .semibold)).foregroundStyle(YYColor.t1)
            Spacer()
            if let trailing { Text(trailing).font(YYUI.font(10.5)).foregroundStyle(YYColor.t3) }
        }
        .padding(.top, YYUI.s(18))
        .padding(.bottom, YYUI.s(8))
        .padding(.horizontal, YYUI.s(2))
    }
}

/// A grey line under a card.
struct YYNote: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(YYUI.font(10))
            .lineSpacing(YYUI.s(10) * 0.6)
            .foregroundStyle(YYColor.t3)
            .fixedSize(horizontal: false, vertical: true)
            .padding(.top, YYUI.s(6))
            .padding(.horizontal, YYUI.s(4))
    }
}

/// `.grp`: a white card of rows, a hairline between them.
struct YYGroup<Content: View>: View {
    @ViewBuilder let content: Content
    @Environment(\.colorScheme) private var scheme
    @State private var fieldLabelWidth: CGFloat = 0

    var body: some View {
        YYDividedVStack(inset: 0) { content }
            .environment(\.yyFieldLabelWidth, fieldLabelWidth)
        .onPreferenceChange(YYFieldLabelWidthKey.self) { new in
            if abs(new - fieldLabelWidth) > 0.5 { fieldLabelWidth = new }
        }
        // Consume within this group so nested or outer groups measure only their own rows.
        .preference(key: YYFieldLabelWidthKey.self, value: 0)
        .padding(.horizontal, YYUI.s(12))
        .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(14)))
        .clipShape(.rect(cornerRadius: YYUI.s(14)))
        .overlay(RoundedRectangle(cornerRadius: YYUI.s(14)).strokeBorder(scheme == .dark ? Color.white.opacity(0.06) : YYColor.line, lineWidth: 0.5))
    }
}

private struct YYFieldLabelWidthKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) {
        value = max(value, nextValue())
    }
}

private struct YYFieldLabelWidthEnvKey: EnvironmentKey {
    static let defaultValue: CGFloat = 0
}

private extension EnvironmentValues {
    var yyFieldLabelWidth: CGFloat {
        get { self[YYFieldLabelWidthEnvKey.self] }
        set { self[YYFieldLabelWidthEnvKey.self] = newValue }
    }
}

/// A white card on its own.
struct YYCardBackground: ViewModifier {
    @Environment(\.colorScheme) private var scheme
    @AppStorage(EInk.key) private var eink = false

    func body(content: Content) -> some View {
        content
            .background(YYColor.card, in: .rect(cornerRadius: YYUI.s(16)))
            .overlay(RoundedRectangle(cornerRadius: YYUI.s(16)).strokeBorder(scheme == .dark ? Color.white.opacity(0.06) : YYColor.line, lineWidth: 0.5))
            .shadow(color: Color(yy: YYRGBA(51, 46, 41, scheme == .dark || eink ? 0 : 0.04)), radius: 1, y: 1)
    }
}

extension View {
    func yyCard() -> some View { modifier(YYCardBackground()) }
}

/// 动效: every animation in the app picks one of these.
/// Short, eased out, no bounce. With 减弱动态效果 or 墨水屏 on, things that move get there at once
/// and the rest only fades, quickly.
enum YYMotion {
    static var reduce: Bool {
        if EInk.on { return true }
        #if os(macOS)
        return NSWorkspace.shared.accessibilityDisplayShouldReduceMotion
        #else
        return UIAccessibility.isReduceMotionEnabled
        #endif
    }

    /// Hover, press, an icon changing, a tick.
    static var quick: Animation { EInk.on ? .linear(duration: 0) : .easeOut(duration: reduce ? 0.1 : YYDuration.quick) }
    /// A card or panel coming and going, a colour, a progress line, a number.
    static var standard: Animation { EInk.on ? .linear(duration: 0) : (reduce ? .easeOut(duration: 0.15) : .spring(duration: YYDuration.standard, bounce: 0)) }
    /// Things moving to a new place: re-sorting, the sidebar's selection, opening and closing.
    /// Nil with 减弱动态效果 or 墨水屏: they're simply there.
    static var layout: Animation? { reduce ? nil : .spring(duration: YYDuration.layout, bounce: 0) }
    /// Going away, a little quicker than coming.
    static var exit: Animation {
        EInk.on ? .linear(duration: 0) : (reduce ? .easeOut(duration: 0.08) : .timingCurve(0.23, 1, 0.32, 1, duration: YYDuration.exit))
    }

    /// Content that changes in place (another 侧栏 page): fades in a few points lower.
    static var swap: AnyTransition {
        reduce ? .opacity : .asymmetric(insertion: .opacity.combined(with: .offset(y: 6)), removal: .opacity)
    }
}

/// A card grows out of what it points at as it appears — 0.94 → 1 from `anchor` with a fade —
/// so it reads as coming from the words chosen, not from nowhere. Its going is the container's
/// transition (a quick fade).
private struct YYPopOnAppear: ViewModifier {
    let anchor: UnitPoint
    @State private var shown = false

    func body(content: Content) -> some View {
        content
            .scaleEffect(shown || YYMotion.reduce ? 1 : 0.94, anchor: anchor)
            .opacity(shown ? 1 : 0)
            .onAppear { withAnimation(YYMotion.standard) { shown = true } }
    }
}

extension View {
    func yyPopOnAppear(from anchor: UnitPoint) -> some View { modifier(YYPopOnAppear(anchor: anchor)) }
}

/// How a note (删除 · 撤销 and the rest) comes and goes.
struct YYToastMotion<Value: Equatable>: ViewModifier {
    let value: Value

    func body(content: Content) -> some View {
        content.animation(YYMotion.reduce ? .easeOut(duration: 0.2) : .spring(duration: 0.4, bounce: 0), value: value)
    }
}

extension View {
    func yyToastMotion<Value: Equatable>(value: Value) -> some View { modifier(YYToastMotion(value: value)) }

    /// Lifted a little under the pointer (Mac). For things that open something: a book, a
    /// collection, a note. A press is the button's: `YYPressStyle`.
    func yyLift() -> some View { modifier(YYLift()) }
}

private struct YYLift: ViewModifier {
    @State private var hover = false
    @AppStorage(EInk.key) private var eink = false

    func body(content: Content) -> some View {
        let lift = hover && !YYMotion.reduce && !eink
        content
            .offset(y: lift ? -1.5 : 0)
            .shadow(color: .black.opacity(lift ? 0.10 : 0), radius: lift ? 10 : 0, y: lift ? 6 : 0)
            .animation(YYMotion.quick, value: lift)
            #if os(macOS)
            .onHover { hover = $0 }
            #endif
    }
}

/// A plain button that gives a little when pressed: down to 0.98, back as it's let go.
struct YYPressStyle: ButtonStyle {
    var scale: CGFloat = 0.98
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .contentShape(.rect)
            .scaleEffect(configuration.isPressed && !YYMotion.reduce ? scale : 1)
            .animation(.easeOut(duration: configuration.isPressed ? 0.08 : YYDuration.quick), value: configuration.isPressed)
    }
}

/// `.grp .row`: an icon, what it is, and a grey value with a chevron.
struct YYRow<Trailing: View>: View {
    let title: String
    var icon: String?
    var value: String?
    var chevron = false
    @ViewBuilder let trailing: Trailing

    var body: some View {
        HStack(spacing: YYUI.s(10)) {
            if let icon {
                Image(systemName: icon).font(YYUI.font(13)).foregroundStyle(YYColor.t2).frame(width: YYUI.s(15))
            }
            if let value, !value.isEmpty {
                let middle = value.contains("://") || value.contains("/") || value.contains("\\")
                YYRowTextLayout(gap: YYUI.s(8)) {
                    Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(1)
                    Text(value).font(YYUI.font(10.5).monospacedDigit()).foregroundStyle(YYColor.t3).lineLimit(1)
                        .truncationMode(middle ? .middle : .tail)
                }
            } else {
                Text(title).font(YYUI.font(11.5)).foregroundStyle(YYColor.t1).lineLimit(1)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            trailing
            if chevron {
                Image(systemName: "chevron.right").font(YYUI.font(9, .semibold)).foregroundStyle(YYColor.t3)
            }
        }
        .frame(minHeight: YYUI.s(38))
        .contentShape(.rect)
    }
}

private struct YYRowTextLayout: Layout {
    var gap: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard subviews.count == 2 else { return subviews.first?.sizeThatFits(proposal) ?? .zero }
        let tIdeal = subviews[0].sizeThatFits(.unspecified)
        let vIdeal = subviews[1].sizeThatFits(.unspecified)
        let h = max(tIdeal.height, vIdeal.height)
        guard let w = proposal.width else {
            return CGSize(width: tIdeal.width + gap + vIdeal.width, height: h)
        }
        if !w.isFinite { return CGSize(width: 10_000, height: h) }
        return CGSize(width: max(0, w), height: h)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard subviews.count == 2 else {
            subviews.first?.place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading, proposal: proposal)
            return
        }
        let maxW = bounds.width
        let tIdeal = subviews[0].sizeThatFits(.unspecified)
        let vIdeal = subviews[1].sizeThatFits(.unspecified)
        let tW: CGFloat
        let vW: CGFloat
        if tIdeal.width + gap + vIdeal.width <= maxW {
            tW = tIdeal.width
            vW = vIdeal.width
        } else {
            tW = min(tIdeal.width, max(maxW * 0.68, maxW - vIdeal.width - gap))
            vW = max(0, maxW - tW - gap)
        }
        subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading,
                          proposal: .init(width: max(0, tW), height: bounds.height))
        subviews[1].place(at: CGPoint(x: bounds.maxX, y: bounds.midY), anchor: .trailing,
                          proposal: .init(width: max(0, vW), height: bounds.height))
    }
}

extension YYRow where Trailing == EmptyView {
    init(_ title: String, icon: String? = nil, value: String? = nil, chevron: Bool = false) {
        self.init(title: title, icon: icon, value: value, chevron: chevron) { EmptyView() }
    }
}

/// A field with its name in a shared column on the left; grows cleanly for longer English labels.
struct YYField<Field: View>: View {
    let label: String
    var labelWidth: CGFloat = 58
    @ViewBuilder let field: Field
    @Environment(\.yyFieldLabelWidth) private var groupLabelWidth

    var body: some View {
        HStack(spacing: YYUI.s(8)) {
            Text(label)
                .font(YYUI.font(11))
                .foregroundStyle(YYColor.t2)
                .lineLimit(1)
                .fixedSize(horizontal: true, vertical: false)
                .background {
                    GeometryReader { proxy in
                        Color.clear.preference(key: YYFieldLabelWidthKey.self, value: proxy.size.width)
                    }
                }
                .frame(minWidth: max(YYUI.s(labelWidth), groupLabelWidth), alignment: .leading)
            field.font(YYUI.font(11.5)).frame(maxWidth: .infinity, alignment: .leading)
        }
        .frame(minHeight: YYUI.s(38))
    }
}

/// Set on a view drawn straight on 芦苇. Secondary pills there are the translucent warm white;
/// a card on that page turns it back off.
struct YYButtonOnPaperKey: EnvironmentKey {
    static let defaultValue = false
}

extension EnvironmentValues {
    var yyButtonOnPaper: Bool {
        get { self[YYButtonOnPaperKey.self] }
        set { self[YYButtonOnPaperKey.self] = newValue }
    }
}

/// The warm pill: a hairline and two light shadows. The far one is `0 3px 8px -4px`: shrunk
/// 4pt before the blur, so it stays behind the fill and only a faint edge shows underneath.
struct YYPillLift {
    var fill: Color
    var ring: Color
    var near: Color
    var far: Color

    static func secondary(scheme: ColorScheme, onPaper: Bool, hover: Bool) -> YYPillLift {
        let paper = onPaper && scheme == .light
        let fill: Color = {
            if scheme == .dark { return hover ? Color(yy: YYRGBA(58, 59, 62)) : YYColor.pill }
            // Pointing at it: a warm grey step down from the near-white pill, solid, so it shows.
            if hover { return Color(yy: YYRGBA(240, 237, 231)) }
            if paper { return Color(yy: YYRGBA(255, 255, 253, 0.72)) }
            return YYColor.pill
        }()
        let ring: Color = paper ? Color(yy: YYRGBA(80, 75, 55, 0.14)) : YYColor.pillLine
        let near: Color
        let far: Color
        if scheme == .dark {
            near = .black.opacity(0.18)
            far = .black.opacity(0.14)
        } else if paper {
            near = Color(yy: YYRGBA(80, 75, 55, 0.06))
            far = Color(yy: YYRGBA(80, 75, 55, 0.12))
        } else {
            near = Color(yy: YYRGBA(80, 65, 45, 0.07))
            far = Color(yy: YYRGBA(80, 65, 45, 0.10))
        }
        return YYPillLift(fill: fill, ring: ring, near: near, far: far)
    }

    static func primary(tint: YYTint, scheme: ColorScheme, hover: Bool) -> YYPillLift {
        let fill: Color = {
            if scheme == .dark { return hover ? tint.accent.opacity(0.28) : tint.black }
            return hover ? hoverWash(tint) : tint.black
        }()
        let ring = tint.edge
        let sh = tint.accent.opacity(scheme == .dark ? 0.22 : 0.12)
        return YYPillLift(fill: fill, ring: ring, near: sh, far: sh)
    }

    /// The chosen tag: the soft wash, no shadow of its own. The track carries the shadow.
    static func selected(tint: YYTint) -> YYPillLift {
        YYPillLift(fill: tint.soft, ring: .clear, near: .clear, far: .clear)
    }

    /// Hover: the wash, solid, a step (6%) toward the colour.
    private static func hoverWash(_ tint: YYTint) -> Color {
        switch tint.id {
        case "sage": Color(yy: YYRGBA(213, 228, 217))
        case "mist": Color(yy: YYRGBA(212, 224, 234))
        case "blush": Color(yy: YYRGBA(237, 216, 218))
        default: Color(yy: YYRGBA(236, 223, 210))
        }
    }
}

struct YYShapeLift<S: Shape>: View {
    var shape: S
    var lift: YYPillLift

    var body: some View {
        ZStack {
            // `0 3px 8px -4px`. Sigma is half the 8pt blur. Inset first, or the blur
            // blooms around the whole pill instead of tucking under the bottom edge.
            shape.fill(lift.far)
                .padding(4)
                .offset(y: 3)
                .blur(radius: 4)
            // `0 1px 2px`.
            shape.fill(lift.near)
                .offset(y: 1)
                .blur(radius: 1)
            shape.fill(lift.fill)
                .overlay(shape.stroke(lift.ring, lineWidth: 0.5))
        }
    }
}

struct YYCapsuleLift: View {
    var lift: YYPillLift
    var body: some View { YYShapeLift(shape: Capsule(), lift: lift) }
}

/// Chrome for a pill whose label already has its own padding. Hover, the light shadow, press 0.97.
struct YYPillChrome: ButtonStyle {
    enum Role { case secondary, primary, selected }
    var role = Role.secondary

    func makeBody(configuration: Configuration) -> some View {
        YYPillChromeBody(configuration: configuration, role: role)
    }
}

private struct YYPillChromeBody: View {
    let configuration: ButtonStyleConfiguration
    var role: YYPillChrome.Role
    @Environment(\.isEnabled) private var enabled
    @Environment(\.colorScheme) private var scheme
    @Environment(\.yyButtonOnPaper) private var onPaper
    @State private var hover = false

    var body: some View {
        configuration.label
            .background { YYCapsuleLift(lift: lift) }
            .contentShape(.capsule)
            .scaleEffect(enabled && configuration.isPressed && !YYMotion.reduce && !EInk.on ? 0.97 : 1)
            .animation(EInk.on || YYMotion.reduce ? nil : .easeOut(duration: 0.1), value: configuration.isPressed)
            .opacity(enabled ? 1 : 0.4)
            #if os(macOS)
            .animation(EInk.on || YYMotion.reduce ? nil : .easeOut(duration: 0.2), value: hover)
            .onHover { hover = $0 && enabled }
            #endif
    }

    private var lift: YYPillLift {
        let tint = YYTint.standard
        switch role {
        case .primary: return .primary(tint: tint, scheme: scheme, hover: hover)
        case .selected: return .selected(tint: tint)
        case .secondary: return .secondary(scheme: scheme, onPaper: onPaper, hover: hover)
        }
    }
}

/// Choices on a warm capsule track. The chosen one is the theme colour on a 10% wash; `small` is
/// the one at the end of a row. Heights stay as they are.
struct YYSegments<T: Hashable>: View {
    let items: [(T, String)]
    @Binding var selection: T
    var counts: [T: Int] = [:]
    var small = false
    /// The chosen pill slides to its new place instead of jumping there.
    @Namespace private var chosen

    var body: some View {
        HStack(spacing: 2) {
            ForEach(items, id: \.0) { value, title in
                let on = selection == value
                Button {
                    withAnimation(YYMotion.layout) { selection = value }
                } label: {
                    HStack(spacing: YYUI.s(4)) {
                        // A longer English label scales down smoothly instead of overflowing the track.
                        Text(title).lineLimit(1).minimumScaleFactor(small ? 0.5 : 0.75)
                        if let n = counts[value] {
                            Text("\(n)").font(YYUI.font(10).monospacedDigit()).foregroundStyle(YYColor.t3)
                        }
                    }
                    .font(YYUI.font(small ? 10 : 11, on ? .semibold : .regular))
                    .foregroundStyle(on ? YYColor.t1 : YYColor.t2)
                    .lineLimit(1)
                    .padding(.horizontal, small ? YYUI.s(8) : YYUI.s(5))
                    .frame(maxWidth: .infinity)
                    .frame(height: YYUI.s(small ? 22 : 28))
                    .background {
                        if on {
                            RoundedRectangle(cornerRadius: YYUI.s(small ? 7 : 9)).fill(YYColor.card)
                                .shadow(color: .black.opacity(0.07), radius: 1.5, y: 1)
                                .matchedGeometryEffect(id: "chosen", in: chosen)
                        }
                    }
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)
                .accessibilityLabel(title)
                .accessibilityAddTraits(on ? .isSelected : [])
            }
        }
        .padding(3)
        .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(small ? 10 : 12)))
        .animation(YYMotion.layout, value: selection)
    }
}

/// A small grey label.
struct YYTag: View {
    let text: String
    var icon: String?

    var body: some View {
        HStack(spacing: YYUI.s(3)) {
            if let icon { Image(systemName: icon) }
            Text(text)
        }
        .font(YYUI.font(10, .medium))
        .foregroundStyle(YYColor.t2)
        .padding(.horizontal, YYUI.s(6))
        .padding(.vertical, YYUI.s(3))
        .background(YYColor.inset, in: .rect(cornerRadius: YYUI.s(5)))
    }
}

/// `.sw2`: a switch in the theme colour.
struct YYSwitch: View {
    @Binding var isOn: Bool
    var label = ""

    var body: some View {
        Toggle(label, isOn: $isOn)
            .labelsHidden()
            .toggleStyle(.switch)
            #if os(macOS)
            .controlSize(.small)
            #endif
            .tint(YYTint.standard.fill)
    }
}

/// A pill button. `main` is a wash of the theme colour, a theme hairline, and semibold words in
/// the colour. `card`, `raised`, and `plain` are the warm white pill in regular weight (`card`'s
/// words stay in the colour); `danger` the same pill in red; `soft` a tinted chip with a theme hairline. On 芦苇 the warm pills turn translucent. Hover lightens the fill;
/// the shadow stays. Pressing scales to 0.97.
struct YYButtonStyle: ButtonStyle {
    enum Kind { case main, card, raised, soft, plain, danger }
    enum Size { case small, regular, large }
    var kind = Kind.plain
    var size = Size.regular
    var wide = false
    /// A height from the dialog sheet, when it isn't one of the three sizes.
    var height: CGFloat? = nil
    /// Overrides the kind's ink. 加上了 is the warm pill in grey.
    var ink: Color? = nil
    /// A quiet shortcut beside the words, as 改好了 ↩ and 保存 ⌘↩.
    var hint: String? = nil

    func makeBody(configuration: Configuration) -> some View {
        YYPillButton(configuration: configuration, kind: kind, size: size, wide: wide, height: height, ink: ink, hint: hint)
    }
}

private struct YYPillButton: View {
    let configuration: ButtonStyleConfiguration
    var kind: YYButtonStyle.Kind
    var size: YYButtonStyle.Size
    var wide: Bool
    var height: CGFloat?
    var ink: Color?
    var hint: String?
    @Environment(\.isEnabled) private var enabled
    @Environment(\.colorScheme) private var scheme
    @Environment(\.yyButtonOnPaper) private var onPaper
    @State private var hover = false

    var body: some View {
        // Low on purpose: a tall pill looks heavy next to the rows around it.
        let box: CGFloat = height ?? { switch size { case .small: 26; case .regular: 30; case .large: 36 } }()
        let font: CGFloat = switch size { case .small: 11; case .regular: 12; case .large: 13 }
        let pad: CGFloat = switch size { case .small: 11; case .regular: 14; case .large: 20 }
        let tint = YYTint.standard
        #if os(iOS)
        let hitDy = max(0, (44 - YYUI.s(box)) / 2)
        #else
        let hitDy: CGFloat = 0
        #endif
        HStack(spacing: YYUI.s(5)) {
            configuration.label
                .font(YYUI.font(font, kind == .main ? .semibold : .regular))
                .lineLimit(1)
                .foregroundStyle(foreground(tint))
            if let hint {
                Text(hint).font(YYUI.font(10.5)).foregroundStyle(YYColor.t3)
            }
        }
        .padding(.horizontal, YYUI.s(pad))
        .frame(maxWidth: wide ? .infinity : nil)
        .frame(height: YYUI.s(box))
        .background { YYCapsuleLift(lift: lift(tint)) }
        #if os(iOS)
        .contentShape([.hoverEffect, .contextMenuPreview], .capsule)
        #else
        .contentShape(.focusEffect, .capsule)
        #endif
        .contentShape(.interaction, ExpandedCapsuleHit(dx: min(hitDy, 3), dy: hitDy))
        .scaleEffect(enabled && configuration.isPressed && !YYMotion.reduce && !EInk.on ? 0.97 : 1)
        .animation(EInk.on || YYMotion.reduce ? nil : .easeOut(duration: 0.1), value: configuration.isPressed)
        .opacity(enabled ? 1 : 0.4)
        #if os(macOS)
        .animation(EInk.on || YYMotion.reduce ? nil : .easeOut(duration: 0.2), value: hover)
        .onHover { hover = $0 && enabled }
        #endif
    }

    private func foreground(_ tint: YYTint) -> Color {
        if let ink { return ink }
        switch kind {
        case .main: return tint.onBlack
        case .card, .soft: return tint.accent
        case .danger: return YYColor.danger
        case .raised, .plain: return YYColor.t1
        }
    }

    private func lift(_ tint: YYTint) -> YYPillLift {
        switch kind {
        case .main:
            return .primary(tint: tint, scheme: scheme, hover: hover)
        case .soft:
            // The tinted chip keeps the set's hairline, in the theme colour.
            let base = YYPillLift.secondary(scheme: scheme, onPaper: onPaper, hover: false)
            return YYPillLift(fill: tint.accentSoft, ring: tint.edge, near: base.near, far: base.far)
        case .danger:
            // The warm pill with red words; pointing at it washes it faintly red, solid (a see-through
            // red let the shadow show through as a muddy grey).
            var lift = YYPillLift.secondary(scheme: scheme, onPaper: onPaper, hover: false)
            if hover {
                lift.fill = scheme == .dark ? Color(yy: YYRGBA(74, 46, 44)) : Color(yy: YYRGBA(250, 233, 229))
                lift.ring = YYColor.danger.opacity(0.28)
            }
            return lift
        case .card, .raised, .plain:
            return .secondary(scheme: scheme, onPaper: onPaper, hover: hover)
        }
    }
}

private nonisolated struct ExpandedCapsuleHit: Shape {
    var dx: CGFloat
    var dy: CGFloat
    func path(in rect: CGRect) -> Path {
        Capsule().path(in: rect.insetBy(dx: -dx, dy: -dy))
    }
}

/// A sheet's own title with × on the right. Optional ‹ on the left, as 替换规则's next page.
struct YYSheetHeader: View {
    let title: String
    var subtitle: String?
    var back: (() -> Void)? = nil
    /// nil: a page pushed inside a sheet, left with ‹ only.
    let close: (() -> Void)?

    var body: some View {
        HStack(alignment: .center, spacing: YYUI.s(8)) {
            if let back {
                Button(action: back) {
                    Image(systemName: "chevron.left")
                        .font(YYUI.font(13, .semibold))
                        .foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(28), height: YYUI.s(28))
                        .contentShape(Rectangle().inset(by: -6))
                }
                .buttonStyle(.plain)
                .accessibilityLabel("返回")
            }
            YYHeaderTextLayout(gap: YYUI.s(8)) {
                Text(title).font(YYUI.font(15, .semibold)).foregroundStyle(YYColor.t1).lineLimit(1)
                if let subtitle {
                    Text(subtitle).font(YYUI.font(12)).foregroundStyle(YYColor.t3).lineLimit(1)
                }
            }
            if let close {
                Button(action: close) {
                    Image(systemName: "xmark").font(YYUI.font(10, .bold)).foregroundStyle(YYColor.t2)
                        .frame(width: YYUI.s(28), height: YYUI.s(28))
                        .background(YYColor.inset, in: .circle)
                        .contentShape(Circle().inset(by: -6))
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
                .keyboardShortcut(.cancelAction)
                .accessibilityLabel("关上")
            } else if let back {
                Button("", action: back).keyboardShortcut(.cancelAction).opacity(0).frame(width: 0, height: 0)
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }
}

extension View {
    /// A page in a sheet. On the Mac it heads itself — its title, ‹ when pushed, × to close —
    /// instead of the window's grey title strip and a 完成 bar along the bottom. On a phone it
    /// keeps the navigation bar's title.
    @ViewBuilder
    func yySheetPage(_ title: String, back: (() -> Void)? = nil, close: (() -> Void)? = nil) -> some View {
        #if os(macOS)
        self
            .safeAreaInset(edge: .top, spacing: 0) {
                YYSheetHeader(title: title, back: back, close: close)
                    .padding(.leading, YYUI.s(back == nil ? 20 : 12))
                    .padding(.trailing, YYUI.s(16))
                    .padding(.top, YYUI.s(14))
                    .padding(.bottom, YYUI.s(6))
                    .background(YYColor.bg)
            }
            .toolbar(.hidden)
            .navigationBarBackButtonHidden(true)
        #else
        self.navigationTitle(title)
        #endif
    }
}

private struct YYHeaderTextLayout: Layout {
    var gap: CGFloat

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard !subviews.isEmpty else { return .zero }
        let ideals = subviews.map { $0.sizeThatFits(.unspecified) }
        let height = ideals.map(\.height).max() ?? 0
        let totalIdeal = ideals.map(\.width).reduce(0, +) + gap * CGFloat(max(0, ideals.count - 1))
        guard let w = proposal.width else { return CGSize(width: totalIdeal, height: height) }
        if !w.isFinite { return CGSize(width: 10_000, height: height) }
        return CGSize(width: max(0, w), height: height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard !subviews.isEmpty else { return }
        if subviews.count == 1 {
            let tIdeal = subviews[0].sizeThatFits(.unspecified)
            let tW = min(tIdeal.width, bounds.width)
            subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading,
                              proposal: .init(width: max(0, tW), height: bounds.height))
            return
        }
        let maxW = bounds.width
        let tIdeal = subviews[0].sizeThatFits(.unspecified)
        let sIdeal = subviews[1].sizeThatFits(.unspecified)
        let tW: CGFloat
        let sW: CGFloat
        if tIdeal.width + gap + sIdeal.width <= maxW {
            tW = tIdeal.width
            sW = sIdeal.width
        } else {
            let avail = max(0, maxW - gap)
            let cap = avail * 0.38
            if sIdeal.width <= cap {
                sW = sIdeal.width
                tW = max(0, avail - sW)
            } else if tIdeal.width <= cap {
                tW = tIdeal.width
                sW = max(0, avail - tW)
            } else {
                tW = avail * 0.65
                sW = max(0, avail - tW)
            }
        }
        subviews[0].place(at: CGPoint(x: bounds.minX, y: bounds.midY), anchor: .leading,
                          proposal: .init(width: max(0, tW), height: bounds.height))
        subviews[1].place(at: CGPoint(x: bounds.minX + tW + gap, y: bounds.midY), anchor: .leading,
                          proposal: .init(width: max(0, sW), height: bounds.height))
    }
}

/// The sheet's bottom row: grey words on the left, 取消 then the action on the right.
struct YYSheetFooter: View {
    var extraTitle: String? = nil
    var extra: (() -> Void)? = nil
    var meta: String? = nil
    var secondaryTitle: String? = nil
    var onSecondary: (() -> Void)? = nil
    var secondaryDisabled = false
    var primaryTitle: String
    var primaryHint: String? = nil
    var onPrimary: () -> Void
    var primaryDisabled = false
    /// Phone sheets: the buttons share the row, the action on the right.
    var spread = false

    var body: some View {
        HStack(spacing: YYUI.s(8)) {
            if let extraTitle, let extra {
                Button(extraTitle, action: extra)
                    .buttonStyle(YYButtonStyle(kind: .plain, size: .small, wide: spread))
            }
            if let meta, !spread {
                Text(meta)
                    .font(YYUI.font(11.5))
                    .foregroundStyle(YYColor.t3)
                    .lineLimit(1)
            }
            if !spread { Spacer(minLength: YYUI.s(8)) }
            if let secondaryTitle, let onSecondary {
                Button(secondaryTitle, action: onSecondary)
                    .buttonStyle(YYButtonStyle(kind: .plain, wide: spread, height: spread ? 40 : nil))
                    .disabled(secondaryDisabled)
            }
            Button(primaryTitle, action: onPrimary)
                .buttonStyle(YYButtonStyle(kind: .main, wide: spread, height: spread ? 40 : nil, hint: primaryHint))
                .disabled(primaryDisabled)
                .keyboardShortcut(.defaultAction)
        }
        .padding(.horizontal, YYUI.s(20))
        .padding(.top, YYUI.s(14))
        .padding(.bottom, YYUI.s(16))
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// The tab bar's own icons (as Android draws them), for the places
/// that stand for the same thing: the Mac sidebar's 全部 is the 书架 tab.
enum PhoneTabIcon {
    /// An asset in the catalogue, drawn as a template.
    static let shelf = "tab-shelf"
}

/// 黄 = 提醒，红 = 出错，绿 = 一般。The paper card uses one of these.
enum YYAlertTone {
    case warn, error, note

    var ink: Color {
        switch self {
        case .warn: Color(yy: YYRGBA(183, 121, 31))
        case .error: YYColor.danger
        case .note: YYTint.standard.onBlack
        }
    }

    var wash: Color {
        let tint = YYTint.standard
        switch self {
        case .warn: return Color(yy: YYRGBA(183, 121, 31, 0.12))
        case .error: return YYColor.danger.opacity(0.1)
        case .note: return tint.black
        }
    }
}

/// The four marks drawn on the dialog sheet: a tray, a file with +, a file with ×, a circle with !.
enum YYAlertIcon {
    case tray, docPlus, docX, bang

    static func mark(for tone: YYAlertTone) -> YYAlertIcon {
        switch tone {
        case .note: .tray
        case .warn, .error: .bang
        }
    }
}

/// Stroke icon from the dialog sheet, 18 inside a 38 square.
private struct YYAlertGlyph: View {
    var kind: YYAlertIcon
    var ink: Color
    var wash: Color

    var body: some View {
        Glyph(kind: kind)
            .stroke(ink, style: StrokeStyle(lineWidth: 2, lineCap: .round, lineJoin: .round))
            .frame(width: 24, height: 24)
            .scaleEffect(YYUI.s(18) / 24)
            .frame(width: YYUI.s(38), height: YYUI.s(38))
            .background(wash, in: RoundedRectangle(cornerRadius: YYUI.s(12)))
    }

    private struct Glyph: Shape {
        var kind: YYAlertIcon

        nonisolated func path(in rect: CGRect) -> Path {
            let s = min(rect.width, rect.height) / 24
            func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint {
                CGPoint(x: rect.minX + x * s, y: rect.minY + y * s)
            }
            var path = Path()
            switch kind {
            case .tray:
                path.move(to: p(5, 7)); path.addLine(to: p(19, 7))
                path.move(to: p(5, 7)); path.addLine(to: p(5, 19)); path.addLine(to: p(19, 19)); path.addLine(to: p(19, 7))
                path.move(to: p(9, 3)); path.addLine(to: p(15, 3))
            case .docPlus:
                document(&path, p)
                path.move(to: p(9, 13)); path.addLine(to: p(13, 13))
                path.move(to: p(11, 11)); path.addLine(to: p(11, 15))
            case .docX:
                document(&path, p)
                path.move(to: p(9, 11)); path.addLine(to: p(13, 15))
                path.move(to: p(13, 11)); path.addLine(to: p(9, 15))
            case .bang:
                path.addEllipse(in: CGRect(x: p(3, 3).x, y: p(3, 3).y, width: 18 * s, height: 18 * s))
                path.move(to: p(12, 7)); path.addLine(to: p(12, 13))
                path.move(to: p(12, 17)); path.addLine(to: p(12, 17.5))
            }
            return path
        }

        private nonisolated func document(_ path: inout Path, _ p: (CGFloat, CGFloat) -> CGPoint) {
            path.move(to: p(4, 5))
            path.addLine(to: p(14, 5))
            path.addLine(to: p(18, 9))
            path.addLine(to: p(18, 19))
            path.addLine(to: p(4, 19))
            path.closeSubpath()
        }
    }
}

/// Return confirms a card that has no text field. A field uses its own submit.
private struct DefaultShortcut: ViewModifier {
    var on: Bool
    @ViewBuilder func body(content: Content) -> some View {
        if on { content.keyboardShortcut(.defaultAction) } else { content }
    }
}

/// A short paper card, as the dialog sheet draws it: 320 wide, a 38 icon, title, one sentence, pills at the lower right.
struct YYDialogCard: View {
    var tone: YYAlertTone
    var icon: YYAlertIcon?
    var title: String
    var message: String = ""
    var field: Binding<String>?
    var prompt: String = ""
    var secondaryTitle: String?
    var primaryTitle: String
    var onSecondary: () -> Void = {}
    var onPrimary: () -> Void
    /// One button filling the row, as 回到书架 does. Otherwise the pills sit at the lower right.
    var wide = false
    /// The card sits in its own window: the window is the paper, so no second rounding or shadow.
    var panel = false
    /// The action deletes or disconnects: the button is red and Return doesn't press it.
    var destructive = false
    @FocusState private var focused: Bool
    /// On the phone the two pills share the row. On the Mac they sit at the lower right.
    private var phoneWide: Bool {
        #if os(iOS)
        true
        #else
        false
        #endif
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            YYAlertGlyph(kind: icon ?? YYAlertIcon.mark(for: tone), ink: tone.ink, wash: tone.wash)
            Text(title)
                .font(YYUI.font(15, .semibold))
                .foregroundStyle(YYColor.t1)
                .padding(.top, YYUI.s(12))
            if !message.isEmpty {
                Text(message)
                    .font(YYUI.font(12.5))
                    .foregroundStyle(YYColor.t2)
                    .lineSpacing(YYUI.s(12.5) * 0.6)
                    .fixedSize(horizontal: false, vertical: true)
                    .padding(.top, YYUI.s(4))
            }
            if let field {
                TextField("", text: field, prompt: Text(prompt).foregroundStyle(YYColor.t3))
                    .textFieldStyle(.plain)
                    .font(YYUI.font(13))
                    .focused($focused)
                    .padding(.horizontal, YYUI.s(12))
                    .frame(height: YYUI.s(34))
                    .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(10)))
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(10)).strokeBorder(YYColor.line, lineWidth: 0.5))
                    // The sheet tucks the field 6 into the sentence's 16 below it.
                    .padding(.top, message.isEmpty ? YYUI.s(12) : YYUI.s(10))
                    .onAppear { focused = true }
                    .onSubmit(onPrimary)
            }
            if wide {
                Button(primaryTitle, action: onPrimary)
                    .buttonStyle(YYButtonStyle(kind: .main, wide: true, height: 34))
                    .minimumScaleFactor(0.85)
                    .keyboardShortcut(.defaultAction)
                    .frame(maxWidth: .infinity)
                    .padding(.top, YYUI.s(16))
            } else {
                YYDialogButtonLayout(gap: YYUI.s(8), spread: phoneWide) {
                    if let secondaryTitle {
                        // 取消 beside a name being typed is red, as the phone's own prompts have it.
                        Button(secondaryTitle, action: onSecondary)
                            .buttonStyle(YYButtonStyle(kind: field != nil ? .danger : .plain, wide: phoneWide))
                            .minimumScaleFactor(0.85)
                            .keyboardShortcut(.cancelAction)
                    }
                    // Return in the field submits it; a default shortcut would run the action twice.
                    Button(primaryTitle, action: onPrimary)
                        .buttonStyle(YYButtonStyle(kind: destructive ? .danger : .main, wide: phoneWide))
                        .minimumScaleFactor(0.85)
                        .modifier(DefaultShortcut(on: field == nil && !destructive))
                }
                .frame(maxWidth: .infinity)
                .padding(.top, YYUI.s(16))
            }
        }
        .padding(.horizontal, YYUI.s(20))
        .padding(.top, YYUI.s(22))
        .padding(.bottom, YYUI.s(16))
        // The card is drawn 320 wide at the Mac's scale; on a phone, no wider than 340.
        #if os(iOS)
        .frame(maxWidth: min(YYUI.s(320), 340))
        #else
        .frame(width: YYUI.s(320))
        #endif
        .background {
            if panel {
                YYColor.bg
            } else {
                RoundedRectangle(cornerRadius: YYUI.s(18))
                    .fill(YYColor.bg)
                    .overlay(RoundedRectangle(cornerRadius: YYUI.s(18)).strokeBorder(Color.black.opacity(0.08), lineWidth: 0.5))
                    .shadow(color: .black.opacity(0.28), radius: YYUI.s(30), y: YYUI.s(16))
            }
        }
    }
}

/// Dialog action row: splits 50/50 when both labels fit half the row (iOS), gives a longer
/// label the room it needs when both still fit on one line, and stacks vertically (primary on
/// top) when one row is too narrow (e.g. large Dynamic Type or two long localized labels).
private struct YYDialogButtonLayout: Layout {
    var gap: CGFloat
    var spread: Bool

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard !subviews.isEmpty else { return .zero }
        if subviews.count == 1 {
            let ideal = subviews[0].sizeThatFits(.unspecified)
            guard let w = proposal.width, w.isFinite else { return ideal }
            return CGSize(width: max(0, w), height: ideal.height)
        }
        let s0 = subviews[0].sizeThatFits(.unspecified)
        let s1 = subviews[1].sizeThatFits(.unspecified)
        let rowW = s0.width + gap + s1.width
        let rowH = max(s0.height, s1.height)
        guard let w = proposal.width, w.isFinite else {
            return CGSize(width: rowW, height: rowH)
        }
        if rowW <= w {
            return CGSize(width: max(0, w), height: rowH)
        }
        return CGSize(width: max(0, w), height: s1.height + gap + s0.height)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard !subviews.isEmpty else { return }
        if subviews.count == 1 {
            let ideal = subviews[0].sizeThatFits(.unspecified)
            if spread {
                subviews[0].place(
                    at: CGPoint(x: bounds.minX, y: bounds.midY),
                    anchor: .leading,
                    proposal: .init(width: bounds.width, height: bounds.height)
                )
            } else {
                subviews[0].place(
                    at: CGPoint(x: bounds.maxX, y: bounds.midY),
                    anchor: .trailing,
                    proposal: .init(width: min(ideal.width, bounds.width), height: bounds.height)
                )
            }
            return
        }
        let s0 = subviews[0].sizeThatFits(.unspecified)
        let s1 = subviews[1].sizeThatFits(.unspecified)
        let avail = bounds.width
        if s0.width + gap + s1.width <= avail {
            if spread {
                let usable = max(0, avail - gap)
                let half = usable / 2
                let w0: CGFloat
                let w1: CGFloat
                if s0.width <= half && s1.width <= half {
                    w0 = half
                    w1 = half
                } else {
                    let extra = max(0, usable - s0.width - s1.width)
                    w0 = s0.width + extra / 2
                    w1 = max(0, usable - w0)
                }
                subviews[0].place(
                    at: CGPoint(x: bounds.minX, y: bounds.midY),
                    anchor: .leading,
                    proposal: .init(width: w0, height: bounds.height)
                )
                subviews[1].place(
                    at: CGPoint(x: bounds.maxX, y: bounds.midY),
                    anchor: .trailing,
                    proposal: .init(width: w1, height: bounds.height)
                )
            } else {
                subviews[1].place(
                    at: CGPoint(x: bounds.maxX, y: bounds.midY),
                    anchor: .trailing,
                    proposal: .init(width: s1.width, height: bounds.height)
                )
                subviews[0].place(
                    at: CGPoint(x: bounds.maxX - s1.width - gap, y: bounds.midY),
                    anchor: .trailing,
                    proposal: .init(width: s0.width, height: bounds.height)
                )
            }
        } else {
            // Stack vertically with the primary action on top and secondary/cancel below.
            subviews[1].place(
                at: CGPoint(x: bounds.midX, y: bounds.minY),
                anchor: .top,
                proposal: .init(width: avail, height: s1.height)
            )
            subviews[0].place(
                at: CGPoint(x: bounds.midX, y: bounds.minY + s1.height + gap),
                anchor: .top,
                proposal: .init(width: avail, height: s0.height)
            )
        }
    }
}

/// Dims the window behind a paper card. A tap beside the card is cancel.
struct YYDialogScrim<Content: View>: View {
    var dismiss: () -> Void
    @ViewBuilder var content: () -> Content

    var body: some View {
        ZStack {
            Color.black.opacity(0.18).ignoresSafeArea().onTapGesture(perform: dismiss)
            content()
                #if os(iOS)
                .padding(.horizontal, 24)
                #endif
                // The scrim fades in; the card comes up from a touch smaller with it.
                .yyPopOnAppear(from: .center)
        }
    }
}

extension View {
    /// One sentence on the paper card instead of a system alert. 知道了 unless named otherwise.
    func yyNotice(_ title: String, message: String, tone: YYAlertTone = .error, isPresented: Binding<Bool>,
                  secondaryTitle: String? = nil, primaryTitle: String = String(localized: "知道了"),
                  onPrimary: (() -> Void)? = nil) -> some View {
        overlay {
            if isPresented.wrappedValue {
                YYDialogScrim(dismiss: { isPresented.wrappedValue = false }) {
                    YYDialogCard(
                        tone: tone, title: title, message: message,
                        secondaryTitle: secondaryTitle, primaryTitle: primaryTitle,
                        onSecondary: { isPresented.wrappedValue = false },
                        onPrimary: {
                            isPresented.wrappedValue = false
                            onPrimary?()
                        }
                    )
                }
            }
        }
        .animation(.easeOut(duration: 0.15), value: isPresented.wrappedValue)
    }

    /// Asks before something is deleted, cleared or disconnected. Set `pending` and it shows;
    /// the red button runs the action. The Mac draws the paper card, the phone its own alert.
    func yyConfirm(_ pending: Binding<YYConfirm?>) -> some View {
        modifier(YYConfirmModifier(pending: pending))
    }

    /// 新建合集, 重命名: still a dialog of its own (it was `.alert`), drawn as the small paper from the sheet.
    func yyPrompt(_ title: String, message: String = "", text: Binding<String>, prompt: String, isPresented: Binding<Bool>, confirm: String, onConfirm: @escaping () -> Void) -> some View {
        let card = YYDialogCard(
            tone: .note, icon: .tray, title: title, message: message, field: text, prompt: prompt,
            secondaryTitle: String(localized: "取消"), primaryTitle: confirm,
            onSecondary: { isPresented.wrappedValue = false },
            onPrimary: onConfirm,
            panel: {
                #if os(macOS)
                true
                #else
                false
                #endif
            }()
        )
        #if os(macOS)
        return sheet(isPresented: isPresented) {
            card
                .yyPresentationSizingFitted()
                .paperWindow()
        }
        #else
        return overlay {
            if isPresented.wrappedValue {
                YYDialogScrim(dismiss: { isPresented.wrappedValue = false }) { card }
            }
        }
        .animation(.easeOut(duration: 0.15), value: isPresented.wrappedValue)
        #endif
    }

    /// The book could not be opened: the card on this window, and no reader window.
    /// On the Mac only the key window draws it, so the shelf and a book don't both.
    func bookBlocked(_ library: Library, reimport: @escaping () -> Void) -> some View {
        modifier(BookBlockedModifier(library: library, reimport: reimport))
    }

    /// A paper sheet: no system title bar, the header and × are the window's own.
    @ViewBuilder func paperWindow() -> some View {
        #if os(macOS)
        toolbar(.hidden, for: .windowToolbar)
            .presentationBackground(YYColor.bg)
            .presentationCornerRadius(YYUI.s(18))
            .yyPresentationSizingFitted()
            .background(DialogChrome())
            .environment(\.yyButtonOnPaper, false)
        #else
        presentationBackground(YYColor.bg)
            .presentationCornerRadius(YYUI.s(18))
            .environment(\.yyButtonOnPaper, false)
        #endif
    }
}

/// What a red button is about to do: what goes, what stays, and the word on the button.
struct YYConfirm: Identifiable {
    let id = UUID()
    var title: String
    var message: String
    var button: String
    var action: () -> Void
}

private struct YYConfirmModifier: ViewModifier {
    @Binding var pending: YYConfirm?

    func body(content: Content) -> some View {
        #if os(macOS)
        content.sheet(item: $pending) { c in
            YYDialogCard(
                tone: .error, icon: .bang, title: c.title, message: c.message,
                secondaryTitle: String(localized: "取消"), primaryTitle: c.button,
                onSecondary: { pending = nil },
                onPrimary: {
                    pending = nil
                    c.action()
                },
                panel: true, destructive: true
            )
            .yyPresentationSizingFitted()
            .paperWindow()
        }
        #else
        content.alert(pending?.title ?? "", isPresented: Binding(get: { pending != nil }, set: { if !$0 { pending = nil } }), presenting: pending) { c in
            Button(c.button, role: .destructive) {
                pending = nil
                c.action()
            }
            Button(String(localized: "取消"), role: .cancel) { pending = nil }
        } message: { c in
            Text(c.message)
        }
        #endif
    }
}

private struct BookBlockedModifier: ViewModifier {
    @Bindable var library: Library
    var reimport: () -> Void
    #if os(macOS)
    @State private var key = false
    #endif

    func body(content: Content) -> some View {
        content
            #if os(macOS)
            .background(KeyWatch(key: $key))
            #endif
            .overlay {
                if shows, let blocked = library.blocked {
                    YYDialogScrim(dismiss: { library.blocked = nil }) {
                        YYDialogCard(
                            tone: .error,
                            icon: blocked.reimport ? .docPlus : .docX,
                            title: blocked.title,
                            message: blocked.message,
                            secondaryTitle: blocked.reimport ? String(localized: "先放着") : nil,
                            primaryTitle: blocked.reimport ? String(localized: "重新导入…") : String(localized: "回到书架"),
                            onSecondary: { library.blocked = nil },
                            onPrimary: {
                                let again = blocked.reimport
                                library.blocked = nil
                                if again { reimport() }
                            },
                            wide: !blocked.reimport
                        )
                    }
                }
            }
            .animation(.easeOut(duration: 0.15), value: library.blocked != nil)
    }

    private var shows: Bool {
        #if os(macOS)
        key
        #else
        true
        #endif
    }
}

#if os(macOS)
/// Takes the system title bar off a small dialog, so the paper is the window.
struct DialogChrome: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { Self.tidy(view.window) }
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        Self.tidy(view.window)
    }

    static func tidy(_ window: NSWindow?) {
        guard let window else { return }
        window.titleVisibility = .hidden
        window.titlebarAppearsTransparent = true
        window.titlebarSeparatorStyle = .none
        window.styleMask.insert(.fullSizeContentView)
        window.standardWindowButton(.closeButton)?.isHidden = true
        window.standardWindowButton(.miniaturizeButton)?.isHidden = true
        window.standardWindowButton(.zoomButton)?.isHidden = true
        window.standardWindowButton(.closeButton)?.superview?.isHidden = true
        // The paper colour, light or dark with the window.
        window.backgroundColor = NSColor(YYColor.bg)
        window.isMovableByWindowBackground = true
    }
}

/// Sizes a Mac sheet window to the paper. Fitted sizing follows a scroll view and
/// clips the header and footer; this sets the window to the measured paper.
struct MacPaperSize: NSViewRepresentable {
    var width: CGFloat
    var height: CGFloat

    func makeNSView(context: Context) -> NSView { NSView() }

    func updateNSView(_ view: NSView, context: Context) {
        let width = width, height = height
        guard width > 80, height > 80, height < 920 else { return }
        DispatchQueue.main.async {
            guard let window = view.window, window.isVisible, !window.inLiveResize else { return }
            let now = window.contentView?.bounds.size ?? .zero
            guard abs(now.width - width) > 1 || abs(now.height - height) > 1 else { return }
            window.setContentSize(NSSize(width: width, height: height))
        }
    }
}

/// Whether this view's window is the key window. Reported again once the view
/// actually sits in a window — attaching can happen before that, and a missed
/// report would leave the card hidden.
private struct KeyWatch: NSViewRepresentable {
    @Binding var key: Bool

    func makeNSView(context: Context) -> NSView {
        let view = KeyView()
        let key = $key
        view.onKey = { key.wrappedValue = $0 }
        context.coordinator.watch(view)
        return view
    }

    func updateNSView(_ view: NSView, context: Context) {
        let key = $key
        (view as? KeyView)?.onKey = { key.wrappedValue = $0 }
        context.coordinator.watch(view, syncNow: false)
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class KeyView: NSView {
        var onKey: ((Bool) -> Void)?
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            onKey?(window?.isKeyWindow == true)
        }
    }

    static func dismantleNSView(_ view: NSView, coordinator: Coordinator) {
        coordinator.stop()
    }

    final class Coordinator {
        var tokens: [NSObjectProtocol] = []
        weak var view: NSView?
        private var lastKey: Bool?

        /// The window went away: stop hearing about key windows for it.
        func stop() {
            for token in tokens { NotificationCenter.default.removeObserver(token) }
            tokens = []
        }

        private func ping() {
            guard let view else { return }
            let next = view.window?.isKeyWindow == true
            guard lastKey != next else { return }
            lastKey = next
            (view as? KeyView)?.onKey?(next)
        }

        func watch(_ view: NSView, syncNow: Bool = true) {
            self.view = view
            if syncNow { ping() }
            guard tokens.isEmpty else { return }
            tokens = [
                NotificationCenter.default.addObserver(forName: NSWindow.didBecomeKeyNotification, object: nil, queue: .main) { [weak self] note in
                    let senderID = (note.object as AnyObject?).map(ObjectIdentifier.init)
                    MainActor.assumeIsolated {
                        guard let self, let w = self.view?.window, senderID == ObjectIdentifier(w) else { return }
                        self.ping()
                    }
                },
                NotificationCenter.default.addObserver(forName: NSWindow.didResignKeyNotification, object: nil, queue: .main) { [weak self] note in
                    let senderID = (note.object as AnyObject?).map(ObjectIdentifier.init)
                    MainActor.assumeIsolated {
                        guard let self, let w = self.view?.window, senderID == ObjectIdentifier(w) else { return }
                        self.ping()
                    }
                },
            ]
            Task { [weak self] in self?.ping() }
        }
    }
}

/// Interactive drag handle for resizing right-hand inspector and reader panels on macOS.
/// Uses global screen coordinates so resizing never oscillates with the moving panel origin,
/// locks the resize cursor during drag, and resets to `defaultWidth` on double-click.
struct YYResizeHandle: View {
    enum Chrome {
        /// Sits on a full-height 0.5pt column divider (e.g. Settings inspector).
        case divider
        /// Sits in the gutter beside a floating card (e.g. Reader / PDF side panel).
        case floatingCard
    }

    @Binding var width: Double
    var minWidth: Double
    var maxWidth: Double
    var defaultWidth: Double
    var chrome: Chrome = .floatingCard

    @State private var hovering = false
    @State private var dragging = false
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }

    private var active: Bool { hovering || dragging }

    var body: some View {
        let clampedMax = max(minWidth, maxWidth)
        ZStack {
            switch chrome {
            case .divider:
                Rectangle()
                    .fill(active ? tint.accent.opacity(dragging ? 0.65 : 0.4) : YYColor.line)
                    .frame(width: active ? 1.5 : 0.5)
                    .frame(maxHeight: .infinity)
            case .floatingCard:
                Capsule()
                    .fill(dragging ? tint.accent.opacity(0.75) : YYColor.t3.opacity(hovering ? 0.42 : 0))
                    .frame(width: 3.5, height: dragging ? 44 : 32)
            }
        }
        .frame(width: chrome == .divider ? 9 : 11)
        .frame(maxHeight: .infinity)
        .contentShape(.rect)
        .animation(YYMotion.quick, value: hovering)
        .animation(YYMotion.quick, value: dragging)
        .overlay {
            ResizeStripRepresentable(
                currentWidth: min(max(width, minWidth), clampedMax),
                minWidth: minWidth,
                maxWidth: clampedMax,
                onHover: { hovering = $0 },
                onDragState: { dragging = $0 },
                onResize: { width = $0 },
                onReset: {
                    withAnimation(YYMotion.layout) {
                        width = min(max(defaultWidth, minWidth), clampedMax)
                    }
                }
            )
        }
        .help(Text("拖动调整宽度，双击恢复默认"))
        .accessibilityLabel(Text("调整面板宽度"))
    }
}

private struct ResizeStripRepresentable: NSViewRepresentable {
    var currentWidth: Double
    var minWidth: Double
    var maxWidth: Double
    var onHover: (Bool) -> Void
    var onDragState: (Bool) -> Void
    var onResize: (Double) -> Void
    var onReset: () -> Void

    func makeNSView(context: Context) -> StripView {
        let v = StripView()
        update(v)
        return v
    }

    func updateNSView(_ nsView: StripView, context: Context) {
        update(nsView)
    }

    private func update(_ v: StripView) {
        v.currentWidth = currentWidth
        v.minWidth = minWidth
        v.maxWidth = maxWidth
        v.onHover = onHover
        v.onDragState = onDragState
        v.onResize = onResize
        v.onReset = onReset
    }

    final class StripView: NSView {
        var currentWidth: Double = 300
        var minWidth: Double = 250
        var maxWidth: Double = 520
        var onHover: ((Bool) -> Void)?
        var onDragState: ((Bool) -> Void)?
        var onResize: ((Double) -> Void)?
        var onReset: (() -> Void)?

        private var tracking: NSTrackingArea?
        private var startScreenX: CGFloat = 0
        private var startWidth: Double = 300
        private var isDragging = false

        override var mouseDownCanMoveWindow: Bool { false }
        override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

        override func updateTrackingAreas() {
            super.updateTrackingAreas()
            if let tracking { removeTrackingArea(tracking) }
            let area = NSTrackingArea(
                rect: bounds,
                options: [.mouseEnteredAndExited, .cursorUpdate, .activeInKeyWindow, .inVisibleRect],
                owner: self,
                userInfo: nil
            )
            addTrackingArea(area)
            tracking = area
        }

        override func resetCursorRects() {
            addCursorRect(bounds, cursor: .resizeLeftRight)
        }

        override func cursorUpdate(with event: NSEvent) {
            NSCursor.resizeLeftRight.set()
        }

        override func mouseEntered(with event: NSEvent) {
            onHover?(true)
            NSCursor.resizeLeftRight.set()
        }

        override func mouseExited(with event: NSEvent) {
            if !isDragging { onHover?(false) }
        }

        override func mouseDown(with event: NSEvent) {
            if event.clickCount == 2 {
                onReset?()
                return
            }
            isDragging = true
            startScreenX = NSEvent.mouseLocation.x
            startWidth = currentWidth
            onDragState?(true)
            NSCursor.resizeLeftRight.push()
        }

        override func mouseDragged(with event: NSEvent) {
            guard isDragging else { return }
            let delta = Double(NSEvent.mouseLocation.x - startScreenX)
            let clamped = min(max(startWidth - delta, minWidth), max(minWidth, maxWidth))
            onResize?(clamped.rounded())
        }

        override func mouseUp(with event: NSEvent) {
            guard isDragging else { return }
            isDragging = false
            NSCursor.pop()
            onDragState?(false)
            if let winPt = window?.mouseLocationOutsideOfEventStream {
                let pt = convert(winPt, from: nil)
                if !bounds.contains(pt) { onHover?(false) }
            }
        }
    }
}
#endif
