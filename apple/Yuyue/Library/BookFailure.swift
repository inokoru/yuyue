import SwiftUI

/// A window that learned, only after opening, that the book will not open. It says so on the
/// window the reader was already using, and this one goes away — a system title bar with nothing
/// but “这本书打不开” under it is not a place to read.
struct UnopenedBook: View {
    let library: Library
    let message: String
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        Color.clear
            #if os(macOS)
            .toolbar(.hidden, for: .windowToolbar)
            .background(WindowCloser())
            #endif
            .onAppear {
                library.noteUnopened(message)
                dismiss()
            }
    }
}

#if os(macOS)
/// Closes the window this view landed in. `dismiss` does not always take a reader window down.
private struct WindowCloser: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        Task { view.window?.close() }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {}
}
#endif

extension View {
    /// A failure after the book is open (a mark not saved…): the paper card, gone on 知道了.
    /// A book that never opened is `UnopenedBook`, not this.
    func bookFailure(_ failure: Binding<String?>, opened: Bool) -> some View {
        yyNotice(
            String(localized: "出错了"),
            message: failure.wrappedValue ?? "",
            isPresented: Binding(get: { opened && failure.wrappedValue != nil }, set: { if !$0 { failure.wrappedValue = nil } })
        )
    }
}
