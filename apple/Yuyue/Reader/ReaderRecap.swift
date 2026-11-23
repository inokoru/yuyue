import SwiftUI
import YuyueCore

/// 前情提要: back to a book after a few days, a compact pill appears in the top toolbar without
/// covering any prose; tapping it opens a clean popover card with three spoiler-free lines.
struct Recap: Codable, Equatable {
    let lines: [String]
    /// Days since the book was last read, on any device.
    var days: Int
    /// Paid cloud model: waiting for the reader to tap the top-bar pill before spending tokens.
    var pending: Bool? = nil
    /// Generating after the reader tapped the top-bar pill.
    var loading: Bool? = nil
    /// Whether the popover card is currently open (always false on book open so text stays clear).
    var expanded: Bool? = nil
    /// Position for on-demand generation (not persisted in cache).
    var section: UInt32? = nil
    var sentence: UInt32? = nil
}

extension ReaderModel {
    /// Offers the recap pill in the top bar when the book was last read a day or more ago and 书伴 is set up.
    /// Never auto-covers the reading text: free/local models pre-warm silently in the background,
    /// while paid cloud models wait for the reader to tap the pill.
    func startRecap(lastRead at: Int64, here: Position) {
        let days = Int(Date().timeIntervalSince1970 - Double(at) / 1000) / 86400
        let config = CompanionConfig.current
        guard days >= 1, config.isReady, let detail, here.section > detail.start || here.sentence > 20 else { return }
        let key = "recap|\(here.section)|\(here.sentence / 20)"
        if var kept = recapCache[key] {
            kept.days = days
            kept.pending = nil
            kept.loading = nil
            kept.expanded = false
            recap = kept
            return
        }
        recap = Recap(lines: [], days: days, pending: true, loading: false, expanded: false, section: here.section, sentence: here.sentence)
        if !config.provider.isPaidCloud {
            fetchRecap(days: days, here: here, detail: detail, config: config, key: key, openWhenDone: false)
        }
    }

    func toggleRecap() {
        guard var current = recap else { return }
        if !current.lines.isEmpty {
            current.expanded = !(current.expanded ?? false)
            recap = current
            return
        }
        guard current.loading != true else { return }
        generateRecap()
    }

    func setRecapExpanded(_ open: Bool) {
        guard var current = recap else { return }
        if (current.expanded ?? false) != open {
            current.expanded = open
            recap = current
        }
    }

    func generateRecap() {
        guard let current = recap, current.lines.isEmpty, let detail else { return }
        let config = CompanionConfig.current
        guard config.isReady else { recap = nil; return }
        let here = Position(section: current.section ?? detail.start, sentence: current.sentence ?? 0)
        let key = "recap|\(here.section)|\(here.sentence / 20)"
        recap = Recap(lines: [], days: current.days, pending: false, loading: true, expanded: false, section: here.section, sentence: here.sentence)
        fetchRecap(days: current.days, here: here, detail: detail, config: config, key: key, openWhenDone: true)
    }

    private func fetchRecap(days: Int, here: Position, detail: BookDetail, config: CompanionConfig, key: String, openWhenDone: Bool) {
        let core = core, book = bookID
        Task { [weak self] in
            let text = await Self.readBefore(core: core, book: book, detail: detail, here: here)
            guard text.count > 200 else {
                await MainActor.run { if self?.recap?.lines.isEmpty == true { self?.recap = nil } }
                return
            }
            let system = """
            你是「语阅」里的书伴。读者隔了 \(days) 天又打开《\(detail.summary.title)》，帮他想起之前读到哪、发生了什么。
            规则：
            1. 只根据下面的原文，不要提后面的情节，不要剧透。
            2. 写三句话，每句一行，每句不超过 30 个字（英文不超过 20 个词），用\(Companion.replyLanguage)，按发生的先后。
            3. 只写这三行，不要标题，不要编号，不要别的话。
            """
            guard let reply = try? await Companion.answer(config, system: system, prompt: "原文（读者读过的最后一部分）：\n" + text, maxTokens: 512, fast: true, source: .recap),
                  let self else {
                await MainActor.run { if self?.recap?.loading == true { self?.recap = nil } }
                return
            }
            let lines = reply.components(separatedBy: .newlines)
                .map { $0.trimmingCharacters(in: CharacterSet(charactersIn: " \t-•*·0123456789.、）)")) }
                .filter { !$0.isEmpty }
            guard lines.count >= 2 else {
                if self.recap?.loading == true { self.recap = nil }
                return
            }
            let stored = Recap(lines: Array(lines.prefix(3)), days: days)
            self.recapCache[key] = stored
            // Not if the reader has already moved on or dismissed the pill.
            guard self.recap != nil else { return }
            if self.pager.position.map({ $0.section == here.section && abs(Int($0.sentence) - Int(here.sentence)) < 30 }) ?? true {
                let shouldOpen = openWhenDone || (self.recap?.loading == true)
                self.recap = Recap(lines: stored.lines, days: days, pending: nil, loading: nil, expanded: shouldOpen)
            }
        }
    }

    /// About the last 2,400 characters read, the chapter before included when this one has
    /// only just started.
    @concurrent nonisolated private static func readBefore(core: YuyueCore, book: String, detail: BookDetail, here: Position) async -> String {
        var parts: [String] = []
        var count = 0
        var section = here.section
        var upTo = Int(here.sentence)
        while count < 2400 {
            let all = (try? core.sentences(bookId: book, section: section)) ?? []
            let end = min(all.count, upTo + 1)
            if end > 0 {
                var taken: [String] = []
                for s in all[..<end].reversed() where count < 2400 {
                    taken.append(s)
                    count += s.count
                }
                parts.insert(taken.reversed().joined(), at: 0)
            }
            guard count < 2400, section > detail.start,
                  let previous = try? core.previousSection(bookId: book, section: section, speech: false) else { break }
            section = previous
            upTo = Int.max - 1
        }
        return parts.joined(separator: "\n").replacingOccurrences(of: "\n\n", with: "\n")
    }

    /// Kept recaps, in the 书伴 answers' file for this book.
    var recapCache: [String: Recap] {
        get {
            guard let url = recapFile, let data = try? Data(contentsOf: url) else { return [:] }
            return (try? JSONDecoder().decode([String: Recap].self, from: data)) ?? [:]
        }
        set {
            guard let url = recapFile else { return }
            try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try? JSONEncoder().encode(newValue).write(to: url, options: .atomic)
        }
    }

    private var recapFile: URL? {
        library.map { $0.dataDir.appending(path: "companion", directoryHint: .isDirectory).appending(path: "\(bookID).recap.json") }
    }
}

/// Compact pill tucked into the reader's top bar (`↺ 前情提要 · 3天前`), leaving prose 100% uncovered.
struct RecapTopPill: View {
    let recap: Recap
    let tint: YYTint
    let paper: YYPaper
    let action: () -> Void

    private var isOpen: Bool { recap.expanded == true && !recap.lines.isEmpty }
    private var isLoading: Bool { recap.loading == true }

    var body: some View {
        Button(action: action) {
            HStack(spacing: 5) {
                if isLoading {
                    ProgressView()
                        .controlSize(.mini)
                        .tint(tint.accent)
                } else {
                    Image(systemName: "clock.arrow.circlepath")
                        .font(.yy(size: 11.5, weight: .semibold))
                        .foregroundStyle(tint.accent)
                }
                Text(isLoading ? String(localized: "整理中…") : String(localized: "前情提要"))
                    .font(.yy(size: 12, weight: .medium))
                    .foregroundStyle(paper.ink)
                if !isLoading {
                    Text("\(recap.days)天前")
                        .font(.yy(size: 11))
                        .monospacedDigit()
                        .foregroundStyle(paper.ink2)
                }
            }
            .lineLimit(1)
            .padding(.horizontal, 11)
            .frame(height: 32)
            .yyGlass(
                Capsule(),
                fill: paper.glass,
                line: isOpen ? tint.accent.opacity(0.6) : paper.line,
                shadow: paper.isDark ? 0.3 : 0.06,
                radius: 7,
                interactive: true,
                liquid: !paper.isEink
            )
            .contentShape(.capsule)
        }
        .buttonStyle(YYPressStyle(scale: 0.97))
    }
}

/// 前情提要 popover card: crisp white surface with preserved clock icon, direct typography,
/// ultra-soft divider, and borderless theme-accent `接着读` action.
struct RecapCard: View {
    let recap: Recap
    let tint: YYTint
    var embedded = false
    let done: () -> Void
    @Environment(\.colorScheme) private var scheme

    private var softDivider: Color {
        scheme == .dark ? Color.white.opacity(0.05) : Color(yy: YYRGBA(45, 40, 34, 0.06))
    }

    private var hairline: Color {
        scheme == .dark ? Color.white.opacity(0.085) : Color(yy: YYRGBA(45, 40, 34, 0.095))
    }

    var body: some View {
        let inner = VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 6) {
                Image(systemName: "clock.arrow.circlepath")
                    .font(.yy(size: 12, weight: .semibold))
                    .foregroundStyle(tint.accent)
                Text("前情提要")
                    .font(.yy(size: 12.5, weight: .semibold))
                    .foregroundStyle(tint.accent)
                Spacer(minLength: 8)
                Text("\(recap.days) 天没读了")
                    .font(.yy(size: 11))
                    .monospacedDigit()
                    .foregroundStyle(YYColor.t3)
            }
            .padding(.bottom, 8)

            VStack(alignment: .leading, spacing: 5) {
                ForEach(Array(recap.lines.enumerated()), id: \.offset) { i, line in
                    HStack(alignment: .firstTextBaseline, spacing: 7) {
                        Text("\(i + 1).")
                            .font(.yy(size: 11.5))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                        Text(line)
                            .font(.yy(size: 13.5))
                            .lineSpacing(4)
                            .foregroundStyle(YYColor.t1)
                            .fixedSize(horizontal: false, vertical: true)
                    }
                }
            }

            Rectangle()
                .fill(softDivider)
                .frame(height: 0.5)
                .padding(.top, 10)
                .padding(.bottom, 9)

            HStack {
                Text("只提要你读过的部分")
                    .font(.yy(size: 11.5))
                    .foregroundStyle(YYColor.t3)
                Spacer()
                Button(action: done) {
                    Text("接着读")
                        .font(.yy(size: 12, weight: .semibold))
                        .foregroundStyle(tint.accent)
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 14)

        if embedded {
            inner
        } else {
            inner
                .background(YYColor.card, in: RoundedRectangle(cornerRadius: 14, style: .continuous))
                .overlay(
                    RoundedRectangle(cornerRadius: 14, style: .continuous)
                        .strokeBorder(hairline, lineWidth: 0.5)
                )
                .shadow(
                    color: scheme == .dark ? Color.black.opacity(0.55) : Color(yy: YYRGBA(25, 22, 18, 0.14)),
                    radius: 20,
                    y: 8
                )
        }
    }
}
