import Foundation
import YuyueCore

/// 整理文字版: a 文字版's homophones and names put right by the reader's own model (书伴), a
/// couple of thousand characters a request. The core keeps only fixes that read the same as
/// what they replace, so the recording still lines up.
enum TextPolish {
    struct Outcome: Sendable {
        var asked = 0
        var fixed = 0
        var failed = 0
    }

    /// Runs every request of `book` (a 文字版), `progress` told after each: (done, of).
    static func run(core: YuyueCore, book: String, progress: @escaping @MainActor (Int, Int) -> Void, fixedSoFar: (@MainActor (Int) -> Void)? = nil) async throws -> Outcome {
        guard let config = await MainActor.run(body: { DramaWork.config }) else { throw CompanionError.notReady }
        let asks = try await polishAsks(core, book: book)
        var out = Outcome()
        for (i, ask) in asks.enumerated() {
            try Task.checkCancellation()
            do {
                let answer = try await Companion.answer(config, system: ask.system, prompt: ask.user, maxTokens: 2048)
                out.fixed += Int(try await polishApply(core, book: book, askID: ask.id, answer: answer))
            } catch CompanionError.badKey {
                throw CompanionError.badKey
            } catch CompanionError.noCredit {
                throw CompanionError.noCredit
            } catch is CancellationError {
                throw CancellationError()
            } catch {
                out.failed += 1
            }
            out.asked += 1
            fixedSoFar?(out.fixed)
            progress(i + 1, asks.count)
        }
        return out
    }

    @concurrent nonisolated private static func polishAsks(_ core: YuyueCore, book: String) async throws -> [PolishAsk] {
        try core.audiobookPolishAsks(book: book)
    }

    @concurrent nonisolated private static func polishApply(_ core: YuyueCore, book: String, askID: String, answer: String) async throws -> UInt32 {
        try core.audiobookPolishApply(book: book, askId: askID, answer: answer)
    }
}

extension TextPolish {
    /// 整理错字 as the banner shows it: one run per book at a time; what it fixed is remembered
    /// (by book, on this device), and a page that's open is reloaded when it's done.
    @MainActor @Observable final class Run {
        static let shared = Run()
        static let finished = Notification.Name("yuyue.textPolished")

        enum State: Equatable {
            case idle
            case running(done: Int, of: Int, fixed: Int)
            case failed(String)
        }

        private var states: [String: State] = [:]
        @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]

        func state(_ book: String) -> State { states[book] ?? .idle }
        func fixed(_ book: String) -> Int? {
            UserDefaults.standard.object(forKey: "textPolish.\(book)") as? Int
        }

        func start(core: YuyueCore, book: String) {
            guard tasks[book] == nil else { return }
            states[book] = .running(done: 0, of: 0, fixed: 0)
            tasks[book] = Task {
                do {
                    let out = try await TextPolish.run(core: core, book: book, progress: { [weak self] done, of in
                        guard let self, case let .running(_, _, f) = self.states[book] else { return }
                        self.states[book] = .running(done: done, of: of, fixed: f)
                    }, fixedSoFar: { [weak self] f in
                        guard let self, case let .running(d, o, _) = self.states[book] else { return }
                        self.states[book] = .running(done: d, of: o, fixed: f)
                    })
                    UserDefaults.standard.set(out.fixed, forKey: "textPolish.\(book)")
                    states[book] = .idle
                } catch is CancellationError {
                    states[book] = .idle
                } catch {
                    states[book] = .failed(error.localizedDescription)
                }
                tasks[book] = nil
                NotificationCenter.default.post(name: Self.finished, object: book)
            }
        }

        func stop(_ book: String) {
            tasks[book]?.cancel()
        }
    }
}
