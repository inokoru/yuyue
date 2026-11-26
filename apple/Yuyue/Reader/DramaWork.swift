import Foundation
import YuyueCore

/// 认人: the core hands out the work (read the whole book for its cast; who says each line of a
/// scene; a second look), this asks the model the user added for 书伴 and hands the answers
/// back. Started by the user for one book at a time ("用大模型认全书"), the chapter being read
/// first; it goes on while the app is open, the book closed or not, until done or paused.
@MainActor @Observable final class DramaWork {
    static let shared = DramaWork()
    /// Posted after an answer is taken: the bubbles, the cast page and who reads what follow.
    static let updated = Notification.Name("yuyue.dramaUpdated")

    enum State: Equatable {
        case idle
        case running
        case paused
        /// The model can't be asked (no key, no credit): why, in the user's words.
        case stopped(String)
        case done
    }

    struct Book {
        var state = State.idle
        var progress: DramaProgress?
        /// Characters read and written so far, the model's own counting where it gives it.
        var sentChars = 0
        var startedAt: Date?
    }

    enum Scope: Equatable {
        /// Run the whole book (cast + every chapter).
        case wholeBook
        /// Only attribute/verify/direct the single section currently being read.
        case sectionOnly(UInt32)
    }

    private(set) var books: [String: Book] = [:]
    private var loops: [String: Task<Void, Never>] = [:]
    private var focus: [String: UInt32] = [:]
    private var scopes: [String: Scope] = [:]
    /// Questions out per book: a worker with nothing to do waits while others may still bring work.
    private var asking: [String: Int] = [:]

    /// Two at a time: a scene's question takes ten to thirty seconds.
    private let parallel = 2

    /// The model 书伴 uses, when it's one that can do this: a service with a key. The model on
    /// the device has too little room for a scene and its cast.
    static var config: CompanionConfig? {
        let c = CompanionConfig.current
        return c.provider.kind != .apple && c.isReady ? c : nil
    }

    /// The play being made is cast with cloud voices: a clip made again comes out different, so
    /// 回听 holds them to the full test.
    static var cloudCast = false

    /// Tells the core what this device can do. Called at launch and when 书伴's model or voice models change.
    static func tellCore(_ core: YuyueCore, forceEngine: String? = nil) {
        let c = config
        let engine: String
        let ttsOn: Bool
        let canDesign: Bool
        let sameEachTime: Bool

        if let forceEngine {
            engine = forceEngine
            ttsOn = true
            canDesign = false
            sameEachTime = false
        } else if cloudCast {
            engine = "apple-cloud-voices-1"
            ttsOn = DramaSynth.shared.on
            canDesign = false
            sameEachTime = false
        } else if ActorVoice.canDesign || !ActorVoice.all().isEmpty {
            engine = "apple-actor-voices-1"
            ttsOn = DramaSynth.shared.on
            canDesign = DramaSynth.shared.on && ActorVoice.canDesign
            sameEachTime = false
        } else if LiveVoiceModel.isInstalled {
            engine = LiveVoiceModel.engine
            ttsOn = DramaSynth.shared.on
            canDesign = false
            sameEachTime = false
        } else if KokoroModel.isInstalled {
            engine = KokoroModel.engine
            ttsOn = DramaSynth.shared.on
            canDesign = false
            sameEachTime = true
        } else {
            engine = "apple-system-voices-1"
            ttsOn = DramaSynth.shared.on
            canDesign = false
            sameEachTime = true
        }
        core.dramaSetCaps(caps: DramaCaps(
            llm: c != nil && shared.hasActiveWork,
            verifyDistinct: false,
            contextTokens: 120_000,
            tts: ttsOn,
            designVoice: canDesign,
            sfx: (DramaSynth.shared.on || ttsOn) && SfxMaker.available,
            combo: c.map { "\($0.provider.id)/\($0.model)" } ?? "",
            engine: engine,
            ttsSameEachTime: sameEachTime
        ))
    }

    var hasActiveWork: Bool {
        books.values.contains { $0.state == .running }
    }

    func book(_ id: String) -> Book { books[id] ?? Book() }

    func isSectionOnly(_ id: String) -> Bool {
        if case .sectionOnly = scopes[id] { return true }
        return false
    }

    /// The section being read moved: its scenes go first.
    func reading(_ id: String, section: UInt32) { focus[id] = section }

    /// Reads the counts afresh, without starting anything.
    func refresh(core: YuyueCore, bookID: String) async {
        let p = await Self.loadProgress(core, bookID: bookID)
        var b = book(bookID)
        b.progress = p
        if b.state == .idle, let p, p.castReady, p.sectionsDone == p.sections { b.state = .done }
        books[bookID] = b
    }

    func start(core: YuyueCore, bookID: String, section: UInt32, scope: Scope = .wholeBook) {
        guard loops[bookID] == nil else { return }
        focus[bookID] = section
        scopes[bookID] = scope
        var b = book(bookID)
        b.state = .running
        b.startedAt = b.startedAt ?? Date()
        books[bookID] = b
        Self.tellCore(core)
        loops[bookID] = Task { [weak self] in
            await withTaskGroup(of: Void.self) { group in
                for _ in 0 ..< (self?.parallel ?? 1) {
                    group.addTask { await self?.worker(core: core, bookID: bookID) }
                }
            }
            self?.finished(core: core, bookID: bookID)
        }
    }

    func pause(_ bookID: String) {
        loops[bookID]?.cancel()
        loops[bookID] = nil
        books[bookID]?.state = .paused
        if let ctx = LiveVoiceModel.context { Self.tellCore(ctx.core) }
    }

    private func finished(core: YuyueCore, bookID: String) {
        loops[bookID] = nil
        let scope = scopes.removeValue(forKey: bookID)
        if books[bookID]?.state == .running {
            // Section-only run returns to idle so the reader can still trigger whole-book later.
            if case .sectionOnly = scope {
                books[bookID]?.state = .idle
            } else {
                books[bookID]?.state = .done
            }
        }
        Self.tellCore(core)
        Task { await refresh(core: core, bookID: bookID) }
    }

    /// Whether task `id` belongs to section `target` (`unquoted:<book>:<sec>`, `attr:<book>:<sec>:<first>`, etc.).
    private static func taskMatchesSection(_ taskID: String, bookID: String, section target: UInt32) -> Bool {
        let prefix = ":\(bookID):\(target)"
        guard let range = taskID.range(of: prefix) else { return false }
        return range.upperBound == taskID.endIndex || taskID[range.upperBound] == ":"
    }

    private func worker(core: YuyueCore, bookID: String) async {
        while !Task.isCancelled, books[bookID]?.state == .running {
            let section = focus[bookID] ?? 0
            let task = await Self.nextTask(core, bookID: bookID, section: section)
            // A voice or a sound to make is 整本提前做好's (DramaSynth): handed back, not held.
            if let task, let other = DramaSynth.notModel(task) {
                _ = await Self.submit(core, bookID: bookID, taskID: other, result: .returned)
                return
            }
            guard let task, case let .llm(id, tier, system, user, schema, cachePrefixLen, maxOutputTokens) = task else {
                // An answer still out may open more (a scene answered, then its second look).
                if asking[bookID, default: 0] > 0 {
                    try? await Task.sleep(for: .seconds(2))
                    continue
                }
                return
            }
            // Section-only mode: once the focus chapter has no remaining tasks, hand back out-of-scope work and stop.
            if case .sectionOnly(let target) = scopes[bookID], !Self.taskMatchesSection(id, bookID: bookID, section: target) {
                _ = await Self.submit(core, bookID: bookID, taskID: id, result: .returned)
                if asking[bookID, default: 0] > 0 {
                    try? await Task.sleep(for: .seconds(2))
                    continue
                }
                return
            }
            asking[bookID, default: 0] += 1
            defer { asking[bookID, default: 1] -= 1 }
            guard let config = Self.config else {
                stop(bookID, String(localized: "书伴还没接上大模型：在设置 · 书伴里添加一个服务商"))
                _ = await Self.submit(core, bookID: bookID, taskID: id, result: .returned)
                return
            }
            // Keep schema in `system` so `system` stays constant across every scene and `user[..<cachePrefixLen]`
            // (the book's cast block) stays at the exact start of `user` for prompt caching.
            let fullSystem = schema.isEmpty ? system : system + "\n\n<schema>\n" + schema + "\n</schema>"
            let result: DramaResult
            do {
                let text = try await Self.ask(config, system: fullSystem, prompt: user, maxTokens: Int(maxOutputTokens), tier: tier, cachePrefixLen: Int(cachePrefixLen))
                books[bookID]?.sentChars += user.count + text.count
                result = .llm(text: text)
            } catch CompanionError.badKey {
                stop(bookID, String(localized: "Key 不对，去设置 · 书伴里重新填"))
                // Not the scene's fault: it's given out again once the key is fixed.
                result = .returned
            } catch CompanionError.noCredit {
                stop(bookID, String(localized: "账号余额不够了"))
                result = .returned
            } catch CompanionError.budgetExceeded(let detail) {
                stop(bookID, CompanionError.budgetExceeded(detail).errorDescription ?? String(localized: "已达到今日用量上限"))
                result = .returned
            } catch {
                // Paused: handed back, to be asked again on 继续.
                if Task.isCancelled { result = .returned } else { result = .failed(reason: error.localizedDescription) }
            }
            let progress = await Self.submit(core, bookID: bookID, taskID: id, result: result)
            if let progress { books[bookID]?.progress = progress }
            if case .llm = result { NotificationCenter.default.post(name: Self.updated, object: bookID) }
        }
    }

    @concurrent nonisolated private static func loadProgress(_ core: YuyueCore, bookID: String) async -> DramaProgress? {
        try? core.dramaProgress(bookId: bookID)
    }

    @concurrent nonisolated private static func nextTask(_ core: YuyueCore, bookID: String, section: UInt32) async -> DramaTask? {
        try? core.dramaNextTask(bookId: bookID, focusSection: section)
    }

    @concurrent nonisolated private static func submit(_ core: YuyueCore, bookID: String, taskID: String, result: DramaResult) async -> DramaProgress? {
        try? core.dramaSubmit(bookId: bookID, taskId: taskID, result: result)
    }

    private func stop(_ bookID: String, _ why: String) {
        books[bookID]?.state = .stopped(why)
        loops[bookID]?.cancel()
        loops[bookID] = nil
    }

    /// Asked again after a pause when the service is busy, as 播客 does.
    private static func ask(_ config: CompanionConfig, system: String, prompt: String, maxTokens: Int, tier: LlmTier, cachePrefixLen: Int) async throws -> String {
        var wait: UInt64 = 4
        let useFast = tier == .fast
        for attempt in 0 ..< 4 {
            do {
                return try await Companion.answer(config, system: system, prompt: prompt, maxTokens: max(maxTokens, 1024), cachePrefixLen: cachePrefixLen, fast: useFast, source: .drama)
            } catch where attempt < 3 && worthRetrying(error) {
                try await Task.sleep(nanoseconds: wait * 1_000_000_000)
                wait *= 3
            }
        }
        throw CompanionError.busy
    }

    private static func worthRetrying(_ error: Error) -> Bool {
        if case CompanionError.busy = error { return true }
        guard let e = error as? URLError else { return false }
        return [.networkConnectionLost, .timedOut, .cannotConnectToHost, .notConnectedToInternet, .dnsLookupFailed].contains(e.code)
    }

    /// Roughly what reading the whole book costs: its characters about three and a half times
    /// over going in (the cast once, every scene twice, the acting), little coming out.
    static func estimate(characters: Int, chinese: Bool) -> (tokensIn: Int, tokensOut: Int) {
        let perChar = chinese ? 0.75 : 0.27
        let tokensIn = Int(Double(characters) * 3.5 * perChar)
        return (tokensIn, tokensIn / 12)
    }
}
