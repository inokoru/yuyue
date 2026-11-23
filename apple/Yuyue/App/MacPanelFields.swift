import SwiftUI

/// The panels' sizes: the Mac's points, a little larger on iPhone so they sit with its rows.
enum PanelScale {
    #if os(macOS)
    static let k: CGFloat = 1
    #else
    static let k: CGFloat = 1.1
    #endif
}

/// "这台 Mac", "这台 iPhone": the device's own, as the settings call it.
@MainActor enum SettingsDevice {
    static var name: String {
        #if os(macOS)
        String(localized: "这台 Mac")
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? String(localized: "这台 iPad") : String(localized: "这台 iPhone")
        #endif
    }
}

// The right column's typing pieces: a key is typed
// straight into its row, no box, no sheet; once it looks whole it's tried on its own, and the
// row says how that went. No 连接, no 取消, no 粘贴 or 👁 buttons: ⌘V pastes, Esc puts it back.

/// A row of a `PanelCard` that is typed into: its name on the left, the words on the right.
/// Lit softly while typing in it; red when what's in it didn't work.
struct PanelField: View {
    let label: String
    @Binding var text: String
    var prompt = ""
    var secure = false
    var mono = true
    var bad = false
    var autofocus = false
    var submit: () -> Void = {}
    var cancel: (() -> Void)?
    var blur: (() -> Void)?
    @FocusState private var focused: Bool
    #if os(macOS)
    @State private var secretFocused = false
    @Environment(\.colorScheme) private var scheme
    @AppStorage("appearance.tint") private var tintID = YYTint.byDefault.id
    private var tint: YYTint { YYTint.all.first { $0.id == tintID } ?? .byDefault }
    #endif

    private var isFocused: Bool {
        #if os(macOS)
        secure ? secretFocused : focused
        #else
        focused
        #endif
    }

    var body: some View {
        HStack(spacing: 10) {
            Text(label).font(.yy(size: PanelScale.k * 12.5)).foregroundStyle(bad ? YYColor.danger : YYColor.t1).fixedSize()
            // Not being typed in, a long address shows its two ends, not its start cut off.
            let shown = !isFocused && !secure && !text.isEmpty
            ZStack(alignment: .trailing) {
                #if os(macOS)
                if secure {
                    if text.isEmpty {
                        Text(prompt)
                            .foregroundStyle(YYColor.t3)
                            .lineLimit(1)
                            .allowsHitTesting(false)
                    }
                    MacSecretField(
                        text: $text,
                        focused: $secretFocused,
                        mono: mono,
                        accentColor: tint.accent,
                        scheme: scheme,
                        onSubmit: submit,
                        onCancel: cancel
                    )
                    .frame(height: ceil(YYUI.s(12) * 1.45) + 4)
                } else {
                    TextField("", text: $text, prompt: Text(prompt).foregroundStyle(YYColor.t3))
                        .textFieldStyle(.plain)
                        .multilineTextAlignment(.trailing)
                        .autocorrectionDisabled()
                        .focused($focused)
                        .onSubmit(submit)
                        .onKeyPress(.escape) {
                            guard let cancel else { return .ignored }
                            cancel()
                            return .handled
                        }
                        .opacity(shown ? 0 : 1)
                }
                #else
                Group {
                    if secure {
                        SecureField("", text: $text, prompt: Text(prompt).foregroundStyle(YYColor.t3))
                    } else {
                        TextField("", text: $text, prompt: Text(prompt).foregroundStyle(YYColor.t3))
                    }
                }
                .textFieldStyle(.plain)
                .multilineTextAlignment(.trailing)
                .autocorrectionDisabled()
                .textInputAutocapitalization(.never)
                .focused($focused)
                .onSubmit(submit)
                .onKeyPress(.escape) {
                    guard let cancel else { return .ignored }
                    cancel()
                    return .handled
                }
                .opacity(shown ? 0 : 1)
                #endif
                if shown {
                    Text(text).lineLimit(1).truncationMode(.middle).allowsHitTesting(false)
                }
            }
            .font(mono ? .yy(size: PanelScale.k * 12, design: .monospaced) : .yy(size: PanelScale.k * 12.5))
            .foregroundStyle(YYColor.t1)
        }
        .padding(.horizontal, 10)
        .frame(minHeight: 38 * PanelScale.k)
        // Wrong: only its name turns red, so two wrong rows never stack into one red block.
        .background(isFocused ? YYColor.t1.opacity(0.05) : .clear, in: .rect(cornerRadius: 8))
        .contentShape(.rect)
        .onTapGesture {
            #if os(macOS)
            if secure { secretFocused = true } else { focused = true }
            #else
            focused = true
            #endif
        }
        .onChange(of: isFocused) { was, now in
            if was, !now { blur?() }
        }
        // A moment late: a sheet hands the first field the focus once it's up, after this.
        .task {
            guard autofocus else { return }
            try? await Task.sleep(for: .milliseconds(250))
            #if os(macOS)
            if secure { secretFocused = true } else { focused = true }
            #else
            focused = true
            #endif
        }
    }
}

#if os(macOS)
/// Single-line masked input for API keys & tokens on macOS.
/// Uses a regular `NSTextView` with glyph masking instead of `NSSecureTextField`, so macOS
/// Passwords AutoFill never attaches to API key fields (nor shows broken AutoFill popovers).
struct MacSecretField: NSViewRepresentable {
    @Binding var text: String
    @Binding var focused: Bool
    var mono = true
    var accentColor: Color = YYTint.standard.accent
    var scheme: ColorScheme = .light
    var onSubmit: () -> Void = {}
    var onCancel: (() -> Void)?

    func makeCoordinator() -> Coordinator {
        Coordinator(text: $text, focused: $focused, onSubmit: onSubmit, onCancel: onCancel)
    }

    func makeNSView(context: Context) -> SecretFieldScrollView {
        let scroll = SecretFieldScrollView()
        scroll.hasVerticalScroller = false
        scroll.hasHorizontalScroller = false
        scroll.drawsBackground = false
        scroll.borderType = .noBorder
        scroll.automaticallyAdjustsContentInsets = false
        scroll.contentInsets = NSEdgeInsets()

        let storage = NSTextStorage()
        let layoutManager = NSLayoutManager()
        layoutManager.delegate = context.coordinator
        storage.addLayoutManager(layoutManager)
        let container = NSTextContainer(containerSize: NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = false
        container.heightTracksTextView = false
        container.lineFragmentPadding = 2
        layoutManager.addTextContainer(container)

        let tv = SecretNSTextView(frame: .zero, textContainer: container)
        tv.isHorizontallyResizable = true
        tv.isVerticallyResizable = false
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.allowsUndo = true
        tv.isRichText = false
        tv.importsGraphics = false
        tv.drawsBackground = false
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticTextReplacementEnabled = false
        tv.isAutomaticSpellingCorrectionEnabled = false
        tv.isContinuousSpellCheckingEnabled = false
        tv.isGrammarCheckingEnabled = false
        tv.isAutomaticTextCompletionEnabled = false
        tv.allowsCharacterPickerTouchBarItem = false
        tv.textContainerInset = NSSize(width: 0, height: 2)
        tv.delegate = context.coordinator

        applyStyling(to: tv)
        tv.string = text

        tv.onFocusChange = { [weak coordinator = context.coordinator] isFocused in
            DispatchQueue.main.async {
                guard let coordinator, coordinator.focused.wrappedValue != isFocused else { return }
                coordinator.focused.wrappedValue = isFocused
            }
        }

        scroll.documentView = tv
        return scroll
    }

    func updateNSView(_ scroll: SecretFieldScrollView, context: Context) {
        let coordinator = context.coordinator
        coordinator.text = $text
        coordinator.focused = $focused
        coordinator.onSubmit = onSubmit
        coordinator.onCancel = onCancel

        guard let tv = scroll.documentView as? SecretNSTextView else { return }
        applyStyling(to: tv)

        if tv.string != text, !tv.hasMarkedText() {
            tv.string = text
            let end = (text as NSString).length
            tv.setSelectedRange(NSRange(location: end, length: 0))
            tv.scrollRangeToVisible(NSRange(location: end, length: 0))
        }

        if focused, scroll.window?.firstResponder !== tv {
            DispatchQueue.main.async {
                guard let window = scroll.window, window.firstResponder !== tv else { return }
                window.makeFirstResponder(tv)
                let end = (tv.string as NSString).length
                tv.setSelectedRange(NSRange(location: end, length: 0))
            }
        }
    }

    private func applyStyling(to tv: NSTextView) {
        let font: NSFont = mono
            ? .monospacedSystemFont(ofSize: YYUI.s(12), weight: .regular)
            : .systemFont(ofSize: YYUI.s(12.5))
        let ink = NSColor(YYColor.t1)
        let para = NSMutableParagraphStyle()
        para.alignment = .right
        para.lineBreakMode = .byClipping

        if tv.font != font { tv.font = font }
        tv.textColor = ink
        tv.insertionPointColor = NSColor(accentColor)
        tv.alignment = .right
        tv.defaultParagraphStyle = para
        tv.typingAttributes = [
            .font: font,
            .foregroundColor: ink,
            .paragraphStyle: para,
        ]
    }

    final class Coordinator: NSObject, NSTextViewDelegate, NSLayoutManagerDelegate {
        var text: Binding<String>
        var focused: Binding<Bool>
        var onSubmit: () -> Void
        var onCancel: (() -> Void)?

        init(text: Binding<String>, focused: Binding<Bool>, onSubmit: @escaping () -> Void, onCancel: (() -> Void)?) {
            self.text = text
            self.focused = focused
            self.onSubmit = onSubmit
            self.onCancel = onCancel
        }

        func textDidChange(_ notification: Notification) {
            guard let tv = notification.object as? NSTextView else { return }
            let raw = tv.string
            // API keys are single-line: strip accidental newlines on paste.
            if raw.contains(where: \.isNewline) {
                let clean = raw.filter { !$0.isNewline }
                tv.string = clean
                let end = (clean as NSString).length
                tv.setSelectedRange(NSRange(location: end, length: 0))
                if text.wrappedValue != clean { text.wrappedValue = clean }
            } else if text.wrappedValue != raw {
                text.wrappedValue = raw
            }
            tv.scrollRangeToVisible(tv.selectedRange())
        }

        func textView(_ textView: NSTextView, doCommandBy commandSelector: Selector) -> Bool {
            if textView.hasMarkedText() { return false }
            switch commandSelector {
            case #selector(NSResponder.insertNewline(_:)),
                 #selector(NSResponder.insertNewlineIgnoringFieldEditor(_:)):
                onSubmit()
                return true
            case #selector(NSResponder.cancelOperation(_:)):
                if let onCancel {
                    onCancel()
                    return true
                }
                return false
            case #selector(NSResponder.insertTab(_:)):
                textView.window?.selectNextKeyView(textView)
                return true
            case #selector(NSResponder.insertBacktab(_:)):
                textView.window?.selectPreviousKeyView(textView)
                return true
            default:
                return false
            }
        }

        func layoutManager(
            _ layoutManager: NSLayoutManager,
            shouldGenerateGlyphs glyphs: UnsafePointer<CGGlyph>,
            properties props: UnsafePointer<NSLayoutManager.GlyphProperty>,
            characterIndexes charIndexes: UnsafePointer<Int>,
            font aFont: NSFont,
            forGlyphRange glyphRange: NSRange
        ) -> Int {
            let count = glyphRange.length
            guard count > 0 else { return 0 }
            var bulletChar: UniChar = 0x2022
            var bulletGlyph: CGGlyph = 0
            CTFontGetGlyphsForCharacters(aFont as CTFont, &bulletChar, &bulletGlyph, 1)
            let masked = [CGGlyph](repeating: bulletGlyph, count: count)
            masked.withUnsafeBufferPointer { buf in
                guard let base = buf.baseAddress else { return }
                layoutManager.setGlyphs(base, properties: props, characterIndexes: charIndexes, font: aFont, forGlyphRange: glyphRange)
            }
            return count
        }
    }
}

final class SecretFieldScrollView: NSScrollView {
    override func layout() {
        super.layout()
        guard let tv = documentView as? NSTextView else { return }
        let width = contentSize.width
        let height = contentSize.height
        if width > 0, abs(tv.minSize.width - width) > 0.5 || abs(tv.minSize.height - height) > 0.5 {
            tv.minSize = NSSize(width: width, height: height)
            if tv.frame.width < width || abs(tv.frame.height - height) > 0.5 {
                tv.frame.size = NSSize(width: max(tv.frame.width, width), height: height)
            }
        }
    }
}

private final class SecretNSTextView: NSTextView {
    var onFocusChange: ((Bool) -> Void)?

    override func becomeFirstResponder() -> Bool {
        let ok = super.becomeFirstResponder()
        if ok { onFocusChange?(true) }
        return ok
    }

    override func resignFirstResponder() -> Bool {
        let ok = super.resignFirstResponder()
        if ok { onFocusChange?(false) }
        return ok
    }

    override func copy(_ sender: Any?) {}
    override func cut(_ sender: Any?) {}
}
#endif

/// Tries what was typed once it looks whole, a moment after the typing stops; a new keystroke
/// starts over. What it found goes up as ticks, then a green line, then `done`.
@MainActor @Observable
final class AutoConnect {
    struct Tick: Identifiable {
        let id = UUID()
        let title: String
        var detail: String?
    }
    struct Outcome {
        var ticks: [Tick]
        var banner: String
    }
    enum Phase {
        case idle
        case checking(String)
        case ok(Outcome)
        case failed(String)
    }

    private(set) var phase = Phase.idle
    /// What the last try threw, to tell which row is at fault.
    private(set) var error: Error?
    private var task: Task<Void, Never>?

    var failed: Bool { if case .failed = phase { true } else { false } }
    var busy: Bool { if case .checking = phase { true } else { false } }

    func start(_ checking: String, delay: Duration = .milliseconds(600),
               describe: @escaping (Error) -> String = { $0.localizedDescription },
               work: @escaping @MainActor () async throws -> Outcome,
               done: @escaping @MainActor () -> Void) {
        task?.cancel()
        // Typing again: the last answer goes quickly, not in a blink.
        if case .idle = phase {} else { withAnimation(YYMotion.exit) { phase = .idle } }
        task = Task {
            try? await Task.sleep(for: delay)
            guard !Task.isCancelled else { return }
            withAnimation(YYMotion.standard) { phase = .checking(checking) }
            do {
                let o = try await work()
                guard !Task.isCancelled else { return }
                withAnimation(YYMotion.standard) { phase = .ok(o) }
                try? await Task.sleep(for: .milliseconds(900))
                guard !Task.isCancelled else { return }
                done()
            } catch {
                guard !Task.isCancelled, !(error is CancellationError) else { return }
                self.error = error
                withAnimation(YYMotion.standard) { phase = .failed(describe(error)) }
            }
        }
    }

    func reset() {
        task?.cancel()
        task = nil
        error = nil
        withAnimation(YYMotion.exit) { phase = .idle }
    }

    /// Long enough and in one piece: worth trying.
    static func looksLikeKey(_ s: String) -> Bool {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        return t.count >= 20 && !t.contains(where: \.isWhitespace)
    }

    /// http(s), with a host that could be reached: worth trying.
    static func looksLikeAddress(_ s: String) -> Bool {
        guard let u = URL(string: s.trimmingCharacters(in: .whitespaces)), let scheme = u.scheme, scheme.hasPrefix("http"),
              let host = u.host(), !host.isEmpty else { return false }
        return host.contains(".") || host == "localhost" || u.port != nil
    }
}

/// Under the fields: why it didn't work, in red; or the ticks and the green line.
struct AutoConnectStatus: View {
    let connect: AutoConnect

    var body: some View {
        switch connect.phase {
        case .idle:
            EmptyView()
        case .failed(let why):
            Text(why).font(.yy(size: PanelScale.k * 11)).lineSpacing(2).foregroundStyle(YYColor.danger)
                .fixedSize(horizontal: false, vertical: true)
                .padding(.horizontal, 12).padding(.top, -4)
        case .checking(let what):
            card {
                HStack(spacing: 9) {
                    ProgressView().controlSize(.mini).frame(width: 15)
                    Text(what).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t3)
                }
            }
        case .ok(let o):
            card {
                ForEach(o.ticks) { t in
                    HStack(alignment: .top, spacing: 9) {
                        Image(systemName: "checkmark").font(.yy(size: PanelScale.k * 8, weight: .bold)).foregroundStyle(.white)
                            .frame(width: 15, height: 15).background(YYColor.ok, in: .circle)
                            .padding(.top, 1)
                        VStack(alignment: .leading, spacing: 1) {
                            Text(t.title).font(.yy(size: PanelScale.k * 12)).foregroundStyle(YYColor.t1)
                            if let d = t.detail { Text(d).font(.yy(size: PanelScale.k * 11)).foregroundStyle(YYColor.t3) }
                        }
                    }
                }
                HStack(spacing: 7) {
                    Image(systemName: "checkmark").font(.yy(size: PanelScale.k * 10.5, weight: .bold))
                    Text(o.banner).font(.yy(size: PanelScale.k * 12, weight: .medium))
                }
                .foregroundStyle(YYColor.ok)
                .padding(.horizontal, 9).padding(.vertical, 7)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(YYColor.ok.opacity(0.12), in: .rect(cornerRadius: 8))
                .padding(.top, 2)
            }
        }
    }

    private func card<C: View>(@ViewBuilder _ c: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 7) { c() }
            .padding(.horizontal, 12).padding(.vertical, 10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(YYColor.card, in: .rect(cornerRadius: 11))
            .overlay(RoundedRectangle(cornerRadius: 11).strokeBorder(YYColor.line, lineWidth: 0.5))
            .transition(.opacity.combined(with: .offset(y: -3)))
    }
}
