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
            self
        }
    }

    @ViewBuilder func yySymbolRotate(isActive: Bool) -> some View {
        if #available(macOS 15.0, iOS 18.0, *) {
            symbolEffect(.rotate, isActive: isActive)
        } else {
            symbolEffect(.pulse, isActive: isActive)
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
            self
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
