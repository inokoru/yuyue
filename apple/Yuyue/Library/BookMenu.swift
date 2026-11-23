import SwiftUI
import YuyueCore
#if os(macOS)
import AppKit
#endif

/// What right-clicking a book offers. Not the system menu: the app's own white card, the same
/// as the one over selected text, with the book's progress on top.
enum BookAction: CaseIterable {
    case resume, listenFromStart, audiobook, toggleFinished
    case editInfo, addToCollection, revealInFinder, delete

    static let groups: [[BookAction]] = [
        [.resume, .listenFromStart, .audiobook, .toggleFinished],
        [.editInfo, .addToCollection, .revealInFinder, .delete],
    ]

    /// 有声书… only with a recording laid against it; a book kept only as a record (纸书) has
    /// no file to open, listen to or show.
    static func groups(for book: BookSummary) -> [[BookAction]] {
        let recorded = Audiobooks.shared?.audiobook(for: book.id) != nil
        let fileless: Set<BookAction> = book.record ? [.resume, .listenFromStart, .audiobook, .revealInFinder] : []
        return groups.map { $0.filter { ($0 != .audiobook || recorded) && !fileless.contains($0) } }
    }

    func title(for book: BookSummary) -> String {
        return switch self {
        case .resume where book.format == .audio: book.progress > 0 ? String(localized: "接着听") : String(localized: "开始听")
        case .resume: book.progress > 0 ? String(localized: "接着读") : String(localized: "开始读")
        case .listenFromStart: String(localized: "从头听")
        case .audiobook: String(localized: "有声书…")
        case .toggleFinished where book.format == .audio: book.status == .finished ? String(localized: "标为没听完") : String(localized: "标为听完")
        case .toggleFinished: book.status == .finished ? String(localized: "标为没读完") : String(localized: "标为读完")
        case .editInfo: String(localized: "编辑信息…")
        case .addToCollection: String(localized: "加入合集…")
        case .revealInFinder: String(localized: "在访达中显示")
        case .delete: String(localized: "删除")
        }
    }

    func symbol(for book: BookSummary) -> String {
        switch self {
        case .resume: book.format == .audio ? "headphones" : "book"
        case .listenFromStart: "headphones"
        case .audiobook: "waveform"
        case .toggleFinished: book.status == .finished ? "arrow.uturn.backward" : "checkmark"
        case .editInfo: "pencil"
        case .addToCollection: "rectangle.stack"
        case .revealInFinder: "folder"
        case .delete: "trash"
        }
    }
}

#if os(macOS)
struct BookMenuCard: View {
    let book: BookSummary
    let perform: (BookAction) -> Void
    let close: () -> Void
    /// 在读 · 想读 · 读过 · 搁置 · 弃读, first under the header; 标为读完 gives way to them.
    var setStatus: ((ReadingStatus) -> Void)?

    private enum Item: Hashable {
        case status(ReadingStatus)
        case action(BookAction)
    }

    @State private var cursor: Item?
    @State private var hoverPoint: CGPoint?
    @FocusState private var focused: Bool
    @Environment(\.colorScheme) private var scheme
    private let tint = YYTint.standard

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            header
            if let setStatus {
                VStack(spacing: 0) {
                    ForEach(ReadingStatus.shelfOrder) { s in
                        itemRow(
                            item: .status(s),
                            title: s.name,
                            symbol: s == book.status ? "checkmark" : s.symbol,
                            color: s == book.status ? tint.accent : YYColor.t1,
                            bold: s == book.status
                        ) {
                            setStatus(s)
                            close()
                        }
                    }
                }
                .padding(.vertical, 4)
                .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 14) }
            }
            // A line only between groups, at the top of each.
            ForEach(Array(groups.enumerated()), id: \.offset) { _, group in
                VStack(spacing: 0) {
                    ForEach(group, id: \.self) { action in row(action) }
                }
                .padding(.vertical, 4)
                .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 14) }
            }
        }
        .frame(width: 214)
        .yyFloatCard(.rect(cornerRadius: YYRadius.block), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onKeyPress(phases: .down) { press in
            let all = items
            switch press.key {
            case .escape:
                close()
            case .downArrow, .upArrow:
                guard !all.isEmpty else { return .handled }
                let i = cursor.flatMap { all.firstIndex(of: $0) } ?? (press.key == .downArrow ? -1 : all.count)
                cursor = all[(i + (press.key == .downArrow ? 1 : -1) + all.count) % all.count]
            case .return, .space:
                switch cursor {
                case .status(let s)?:
                    setStatus?(s)
                    close()
                case .action(let a)?:
                    perform(a)
                case nil:
                    break
                }
            default:
                return .ignored
            }
            return .handled
        }
        .accessibilityElement(children: .contain)
        .accessibilityLabel("《\(book.title)》")
    }

    private var groups: [[BookAction]] {
        BookAction.groups(for: book).map { $0.filter { setStatus == nil || $0 != .toggleFinished } }.filter { !$0.isEmpty }
    }

    private var items: [Item] {
        let statuses = setStatus != nil ? ReadingStatus.shelfOrder.map(Item.status) : []
        return statuses + groups.flatMap { $0 }.map(Item.action)
    }

    private var header: some View {
        VStack(alignment: .leading, spacing: 7) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(book.title)
                    .font(.yy(size: 13, weight: .bold))
                    .foregroundStyle(YYColor.t1)
                    .lineLimit(1)
                    .layoutPriority(1)
                if let author = book.author {
                    Text(author)
                        .font(.yy(size: 10.5))
                        .foregroundStyle(YYColor.t3)
                        .lineLimit(1)
                }
            }
            HStack(spacing: 8) {
                // Not started: no line, only 还没读.
                if book.progress > 0 { ProgressLine(value: book.progress, height: 2.5) }
                Text(book.remaining)
                    .font(.yy(size: 10.5))
                    .foregroundStyle(YYColor.t2)
                    .fixedSize()
            }
        }
        .padding(.horizontal, 14)
        .padding(.top, 11)
        .padding(.bottom, 10)
        .accessibilityElement(children: .combine)
    }

    private func row(_ action: BookAction) -> some View {
        let color: Color = switch action {
        case .resume: tint.accent
        case .delete: YYColor.danger
        default: YYColor.t1
        }
        return itemRow(
            item: .action(action),
            title: action.title(for: book),
            symbol: action.symbol(for: book),
            color: color,
            bold: action == .resume
        ) {
            perform(action)
        }
    }

    private func itemRow(item: Item, title: String, symbol: String, color: Color, bold: Bool, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            HStack {
                Text(title)
                    .font(.yy(size: 12.5, weight: bold ? .semibold : .regular))
                Spacer()
                Image(systemName: symbol)
                    .font(.yy(size: 13))
                    .frame(width: 15)
            }
            .foregroundStyle(color)
            .padding(.horizontal, 9)
            .frame(height: 28)
            .background(cursor == item ? YYColor.inset : .clear, in: .rect(cornerRadius: 7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 5)
        .onContinuousHover { phase in
            switch phase {
            case .active(let pt):
                if hoverPoint != pt {
                    hoverPoint = pt
                    cursor = item
                }
            case .ended:
                hoverPoint = nil
                if cursor == item { cursor = nil }
            }
        }
    }
}

/// One row of the app's own white menu card: the name, an icon on the right.
struct MenuCardRow: View {
    let title: String
    let symbol: String
    var color: Color = YYColor.t1
    var bold = false
    let action: () -> Void
    @State private var hover = false

    var body: some View {
        Button(action: action) {
            HStack {
                Text(title).font(.yy(size: 12.5, weight: bold ? .semibold : .regular))
                Spacer()
                Image(systemName: symbol).font(.yy(size: 13)).frame(width: 15)
            }
            .foregroundStyle(color)
            .padding(.horizontal, 9)
            .frame(height: 28)
            .background(hover ? YYColor.inset : .clear, in: .rect(cornerRadius: 7))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .padding(.horizontal, 5)
        .onHover { hover = $0 }
    }
}

/// The status button's menu on the Mac: the same white card as right-clicking a book —
/// 在读 · 想读 · 读过 · 搁置 · 弃读, then 放进合集… and 删除….
struct StatusMenuCard: View {
    let book: BookSummary
    let setStatus: (ReadingStatus) -> Void
    let pickCollection: () -> Void
    let delete: () -> Void
    let close: () -> Void
    @Environment(\.colorScheme) private var scheme
    private let tint = YYTint.standard

    var body: some View {
        VStack(spacing: 0) {
            VStack(spacing: 0) {
                ForEach(ReadingStatus.shelfOrder) { s in
                    MenuCardRow(title: s.name, symbol: s == book.status ? "checkmark" : s.symbol,
                                color: s == book.status ? tint.accent : YYColor.t1, bold: s == book.status) {
                        setStatus(s)
                        close()
                    }
                }
            }
            .padding(.vertical, 5)
            VStack(spacing: 0) {
                MenuCardRow(title: String(localized: "放进合集…"), symbol: "rectangle.stack") { close(); pickCollection() }
                MenuCardRow(title: String(localized: "删除…"), symbol: "trash", color: YYColor.danger) { close(); delete() }
            }
            .padding(.vertical, 4)
            .overlay(alignment: .top) { Rectangle().fill(YYColor.line).frame(height: 0.5).padding(.horizontal, 14) }
        }
        .frame(width: 200)
        .yyFloatCard(.rect(cornerRadius: YYRadius.block), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
        .onExitCommand(perform: close)
    }
}

/// Catches right-clicks (and control-clicks) on the view under it, leaving every other click
/// to SwiftUI. Says where, in the window's coordinates (top left at zero, as SwiftUI's global).
struct RightClickCatcher: NSViewRepresentable {
    let action: (CGPoint) -> Void

    func makeNSView(context: Context) -> Catcher {
        let view = Catcher()
        view.action = action
        return view
    }

    func updateNSView(_ view: Catcher, context: Context) {
        view.action = action
    }

    final class Catcher: NSView {
        var action: ((CGPoint) -> Void)?

        override func hitTest(_ point: NSPoint) -> NSView? {
            guard let event = NSApp.currentEvent else { return nil }
            let secondary = event.type == .rightMouseDown
                || (event.type == .leftMouseDown && event.modifierFlags.contains(.control))
            return secondary ? super.hitTest(point) : nil
        }

        override func rightMouseDown(with event: NSEvent) { report(event) }
        override func mouseDown(with event: NSEvent) { report(event) }

        private func report(_ event: NSEvent) {
            guard let content = window?.contentView else { return }
            var p = content.convert(event.locationInWindow, from: nil)
            if !content.isFlipped { p.y = content.bounds.height - p.y }
            action?(p)
        }
    }
}
#endif
