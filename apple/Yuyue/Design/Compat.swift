import Observation
import os
import SwiftUI

/// Back-deployed `Mutex` matching `Synchronization.Mutex` on macOS 14 / iOS 17.
nonisolated struct Mutex<Value>: @unchecked Sendable {
    private let lock: OSAllocatedUnfairLock<Value>

    init(_ initialValue: Value) {
        lock = OSAllocatedUnfairLock(uncheckedState: initialValue)
    }

    func withLock<Result, E: Error>(_ body: (inout Value) throws(E) -> Result) throws(E) -> Result {
        do {
            return try lock.withLockUnchecked { state in
                try body(&state)
            }
        } catch let error as E {
            throw error
        } catch {
            fatalError("unreachable")
        }
    }
}

extension Color {
    func yyMix(with rhs: Color, by fraction: Double) -> Color {
        if #available(macOS 15.0, iOS 18.0, *) {
            return mix(with: rhs, by: fraction)
        }
        return opacity(1.0 - fraction * 0.75)
    }
}

extension View {
    @ViewBuilder func yyPresentationSizingFitted() -> some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            presentationSizing(.fitted)
        } else {
            #if os(macOS)
            fixedSize(horizontal: false, vertical: true)
            #else
            self
            #endif
        }
    }

    @ViewBuilder func yySymbolRotate(isActive: Bool) -> some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            symbolEffect(.rotate, isActive: isActive)
        } else {
            symbolEffect(.pulse, isActive: isActive)
        }
    }

    @ViewBuilder func yyScrollOffsetContainer() -> some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            self
        } else {
            coordinateSpace(name: YYScrollOffset.space)
        }
    }

    @ViewBuilder func yyOnScrollOffsetPast(_ threshold: CGFloat, includeTopInset: Bool = false, action: @escaping (Bool) -> Void) -> some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            onScrollGeometryChange(for: Bool.self) {
                ($0.contentOffset.y + (includeTopInset ? $0.contentInsets.top : 0)) > threshold
            } action: { _, past in
                action(past)
            }
        } else {
            modifier(YYLegacyScrollOffsetPastModifier(threshold: threshold, action: action))
        }
    }

    #if os(macOS)
    @ViewBuilder func yyHideWindowToolbarChrome(removeSidebarToggle: Bool = false) -> some View {
        if #available(macOS 15.0, *) {
            if removeSidebarToggle {
                toolbar(removing: .title)
                    .toolbar(removing: .sidebarToggle)
                    .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
            } else {
                toolbar(removing: .title)
                    .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
            }
        } else {
            toolbarBackground(.hidden, for: .windowToolbar)
                .background(MacLegacyTitlebarHider())
        }
    }
    #endif
}

#if os(macOS)
import AppKit

private struct MacLegacyTitlebarHider: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let v = NSView(frame: .zero)
        DispatchQueue.main.async {
            v.window?.titleVisibility = .hidden
            v.window?.titlebarAppearsTransparent = true
        }
        return v
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        nsView.window?.titleVisibility = .hidden
        nsView.window?.titlebarAppearsTransparent = true
    }
}
#endif

// MARK: - macOS 14 / iOS 17 scroll offset fallback

enum YYScrollOffset {
    static let space = "yy.scrollOffsetSpace"
}

/// Zero-height top anchor placed inside a `.yyScrollOffsetContainer()` scroll view's content
/// to report scroll offset on macOS 14 / iOS 17 where `onScrollGeometryChange` is unavailable.
struct YYScrollOffsetAnchor: View {
    var body: some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            EmptyView()
        } else {
            GeometryReader { proxy in
                Color.clear.preference(
                    key: YYScrollOffsetKey.self,
                    value: -proxy.frame(in: .named(YYScrollOffset.space)).minY
                )
            }
            .frame(height: 0)
        }
    }
}

private struct YYScrollOffsetKey: PreferenceKey {
    static let defaultValue: CGFloat? = nil
    static func reduce(value: inout CGFloat?, nextValue: () -> CGFloat?) {
        if let next = nextValue() {
            value = value.map { max($0, next) } ?? next
        }
    }
}

private struct YYLegacyScrollOffsetPastModifier: ViewModifier {
    let threshold: CGFloat
    let action: (Bool) -> Void
    @State private var lastPast: Bool?

    func body(content: Content) -> some View {
        content.onPreferenceChange(YYScrollOffsetKey.self) { offset in
            guard let offset else { return }
            let past = offset > threshold
            if lastPast != past {
                lastPast = past
                action(past)
            }
        }
    }
}

// MARK: - Divided card stack (macOS 14 / iOS 17 compatible)

/// Vertical stack with 0.5pt `YYColor.line` hairline dividers between adjacent subviews.
/// Uses `Group(subviews:)` on macOS 15 / iOS 18+ and a `Layout` + divider overlay on macOS 14 / iOS 17.
struct YYDividedVStack<Content: View>: View {
    let inset: CGFloat
    let content: Content
    @State private var dividerModel = YYDividerModel()
    @State private var reportBox = YYDividerReportBox()

    init(inset: CGFloat = 0, @ViewBuilder content: () -> Content) {
        self.inset = inset
        self.content = content()
    }

    var body: some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            VStack(alignment: .leading, spacing: 0) {
                Group(subviews: content) { rows in
                    ForEach(Array(rows.enumerated()), id: \.offset) { i, row in
                        if i > 0 {
                            Rectangle()
                                .fill(YYColor.line)
                                .frame(height: 0.5)
                                .padding(.horizontal, inset)
                        }
                        row
                    }
                }
            }
        } else {
            let box = reportBox
            let model = dividerModel
            YYDividedVStackLayout(onDividers: { nextYs in
                guard box.shouldUpdate(nextYs) else { return }
                Task { @MainActor in
                    model.ys = nextYs
                }
            }) {
                content
            }
            .overlay(alignment: .top) {
                if !dividerModel.ys.isEmpty {
                    ZStack(alignment: .top) {
                        ForEach(Array(dividerModel.ys.enumerated()), id: \.offset) { _, y in
                            Rectangle()
                                .fill(YYColor.line)
                                .frame(height: 0.5)
                                .padding(.horizontal, inset)
                                .offset(y: y)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .allowsHitTesting(false)
                }
            }
        }
    }
}

@Observable
private final class YYDividerModel {
    var ys: [CGFloat] = []
}

private nonisolated final class YYDividerReportBox: Sendable {
    private let last = Mutex<[CGFloat]>([])

    func shouldUpdate(_ next: [CGFloat]) -> Bool {
        last.withLock { current in
            guard current != next else { return false }
            current = next
            return true
        }
    }
}

private nonisolated struct YYDividedVStackLayout: Layout {
    let onDividers: @Sendable ([CGFloat]) -> Void

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        guard !subviews.isEmpty else { return .zero }
        let dividerHeight: CGFloat = 0.5
        let childProposal = ProposedViewSize(width: proposal.width, height: nil)
        var totalHeight: CGFloat = 0
        var maxWidth: CGFloat = 0
        for (i, subview) in subviews.enumerated() {
            if i > 0 { totalHeight += dividerHeight }
            let size = subview.sizeThatFits(childProposal)
            totalHeight += size.height
            maxWidth = max(maxWidth, size.width)
        }
        let width: CGFloat = if subviews.count > 1, let w = proposal.width, w.isFinite {
            max(maxWidth, w)
        } else {
            maxWidth
        }
        return CGSize(width: width, height: totalHeight)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        guard !subviews.isEmpty else {
            onDividers([])
            return
        }
        let dividerHeight: CGFloat = 0.5
        let childProposal = ProposedViewSize(width: bounds.width, height: nil)
        var y = bounds.minY
        var ys: [CGFloat] = []
        if subviews.count > 1 {
            ys.reserveCapacity(subviews.count - 1)
        }
        for (i, subview) in subviews.enumerated() {
            if i > 0 {
                let localY = ((y - bounds.minY) * 2).rounded() / 2
                ys.append(localY)
                y += dividerHeight
            }
            let size = subview.sizeThatFits(childProposal)
            subview.place(
                at: CGPoint(x: bounds.minX, y: y),
                anchor: .topLeading,
                proposal: ProposedViewSize(width: bounds.width, height: size.height)
            )
            y += size.height
        }
        onDividers(ys)
    }
}
