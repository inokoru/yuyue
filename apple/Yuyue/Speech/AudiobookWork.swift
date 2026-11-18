import AVFoundation
import Foundation
import Speech
import YuyueCore
#if os(iOS)
import UIKit
#endif

/// 有声书's background work: the core hands out what's to be done for a recording (find the
/// speech, transcribe, 精修 a window), this runs it on the device and hands the answer back, until
/// the core has made the recording's map.
///
/// Speech is found by loudness (the core decides from each 30 ms frame's level); the words come
/// from the system's own transcriber (nothing to download beyond the language's pack, which the
/// system fetches); 精修 uses the alignment model already used for 合成, reading what it saved
/// for the whole file when it's there.
@MainActor
final class AudiobookWork {
    private let core: YuyueCore
    private let dataDir: URL
    private var running: [String: Task<Void, Never>] = [:]
    private var focus: [String: Double] = [:]
    /// What was asked for this run of the app, to take up again when the whole book may go on.
    private var asked: [String: (book: String?, folder: URL)] = [:]

    init(core: YuyueCore, dataDir: URL) {
        self.core = core
        self.dataDir = dataDir
        #if os(iOS)
        // Plugged in, or the app out of sight: the whole book may go on now.
        UIDevice.current.isBatteryMonitoringEnabled = true
        for name in [UIDevice.batteryStateDidChangeNotification, UIApplication.didEnterBackgroundNotification] {
            NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.resume() }
            }
        }
        #endif
    }

    private func resume() {
        for (recording, a) in asked where running[recording] == nil {
            start(recording, book: a.book, at: nil, folder: a.folder)
        }
    }

    /// Work on `recording` (an 有声书 on the shelf), nearest `at` seconds first. Asking again
    /// while it runs only moves where it starts from next.
    func start(_ recording: String, book: String?, at: Double?, folder: URL) {
        if let at { focus[recording] = at }
        asked[recording] = (book, folder)
        guard running[recording] == nil else { return }
        // The pairing made here is the one the core works to.
        if let book, (try? core.audiobookProgress(bookId: recording))?.pairedWith != book {
            try? core.audiobookPair(bookId: recording, with: book)
        }
        running[recording] = Task { [weak self] in
            await self?.run(recording, folder: folder)
            self?.running[recording] = nil
            // Made now or before (合成 may have written its own times since): taken in again.
            if (try? self?.core.audiobookProgress(bookId: recording))?.done == true { Audiobooks.shared?.adoptMap(recording) }
        }
    }

    func stop(_ recording: String) {
        asked[recording] = nil
        running[recording]?.cancel()
    }

    private func run(_ id: String, folder: URL) async {
        guard let rec = try? core.audioRecording(bookId: id) else { return }
        let (core, dataDir) = (core, dataDir)
        while !Task.isCancelled {
            // Asked each time: the model may have come down meanwhile, the phone plugged in.
            // With the alignment model here it does the hearing too, by its letters: measured on a
            // reading with known times, its rough times are closer than the system transcriber's
            // and its edges of talk outside the book exact. The transcriber (macOS 26 / iOS 26 on)
            // starts the work while the model isn't here yet.
            let model = Aligner.installed(dataDir)
            var letters = model || !Transcriber.available
            #if DEBUG
            // `-debugAudiobookLetters 1`: hear by the model's letters whatever's here.
            if UserDefaults.standard.bool(forKey: "debugAudiobookLetters") { letters = true }
            #endif
            core.audiobookSetCaps(caps: AudiobookCaps(vad: true, transcribe: (Transcriber.available && !letters) || model, letters: letters, align: model, wholeBookNow: Self.mayDoAll))
            guard let task = try? core.audiobookNextTask(bookId: id, focus: focus[id]) else { break }
            let taskID: String
            let result: AudiobookResult
            switch task {
            case let .vad(tid, file, from, to, offset):
                taskID = tid
                result = await Self.vad(core, url: Self.url(rec, file), from: from, to: to, offset: offset)
            case let .transcribe(tid, file, spans, lang, _, offset):
                taskID = tid
                if letters {
                    let cache = folder.appending(path: "emissions-\(file).f32")
                    result = await Self.letters(core, dataDir, url: Self.url(rec, file), cache: cache, fileSeconds: rec.files[Int(file)].seconds, spans: spans, offset: offset)
                } else {
                    result = await Transcriber.words(url: Self.url(rec, file), spans: spans, lang: lang, offset: offset)
                }
            case let .align(tid, file, from, to, sentences, lang, offset):
                taskID = tid
                let cache = folder.appending(path: "emissions-\(file).f32")
                result = await Self.align(core, dataDir, url: Self.url(rec, file), cache: cache, fileSeconds: rec.files[Int(file)].seconds, from: from, to: to, sentences: sentences, lang: lang, offset: offset)
            }
            let back = Task.isCancelled ? AudiobookResult.returned : result
            guard let progress = try? core.audiobookSubmit(bookId: id, taskId: taskID, result: back) else { break }
            #if DEBUG
            NSLog("audiobook work: \(taskID) → \(Self.brief(back)); paired \(progress.pairedWith ?? "-") \(progress.matchKind.map { "\($0)" } ?? "-"), \(progress.sentences) sentences, exact \(progress.exact) spread \(progress.spread) unread \(progress.unread), done \(progress.done)")
            #endif
            if progress.done {
                #if DEBUG
                let extras = (try? core.audiobookExtras(bookId: id)) ?? []
                if UserDefaults.standard.bool(forKey: "debugAudiobookRows") {
                    for n in [3, 30] {
                        let u = (try? core.audiobookUnits(bookId: id, section: 0, sentence: UInt32(n))) ?? []
                        NSLog("audiobook units \(n): \(u.map { "\($0.at)@\(String(format: "%.2f", $0.seconds))" }.joined(separator: " "))")
                    }
                    for r in (try? core.audiobookMap(bookId: id))?.rows ?? [] { NSLog("audiobook row \(r.sentence) \(r.t0Ms) \(r.t1Ms) \(r.by)") }
                }
                NSLog("audiobook work: done; extras \(extras.map { "\($0.place) \(String(format: "%.1f–%.1f", $0.t0, $0.t1))" })")
                #endif
                Audiobooks.shared?.adoptMap(id)
                NotificationCenter.default.post(name: Audiobooks.timingsChanged, object: id)
                break
            }
        }
    }

    #if DEBUG
    nonisolated private static func brief(_ r: AudiobookResult) -> String {
        switch r {
        case let .vad(speech, _): "\(speech.count) speech"
        case let .transcribe(words): "\(words.count) words: \(words.prefix(24).map(\.text).joined())"
        case let .align(sentences): "\(sentences.count) refined"
        case let .failed(reason): "failed \(reason)"
        case .returned: "returned"
        }
    }
    #endif

    nonisolated private static func url(_ r: AudioRecording, _ file: UInt32) -> URL {
        URL(filePath: r.files[Int(min(file, UInt32(max(0, r.files.count - 1))))].path)
    }

    /// A Mac, or a phone plugged in and not being looked at (locked, or another app in front —
    /// iOS doesn't say whether the screen is on): the whole recording now, not only near where
    /// it's being listened to, so the phone isn't warm and slow in the hand.
    private static var mayDoAll: Bool {
        #if os(iOS)
        UIDevice.current.isBatteryMonitoringEnabled = true
        return [.charging, .full].contains(UIDevice.current.batteryState) && UIApplication.shared.applicationState != .active
        #else
        return true
        #endif
    }

    // MARK: 人声检测

    /// Each 30 ms frame's level over [from, to) of the whole recording; the core finds the speech.
    @concurrent nonisolated private static func vad(_ core: YuyueCore, url: URL, from: Double, to: Double, offset: Double) async -> AudiobookResult {
        do {
            let frame = 480
            let wave = try Aligner.Wave(url, from: from - offset)
            let first = wave.end
            let stop = Int((to - offset) * Aligner.rate)
            var levels: [Float] = []
            var at = first
            while at < stop {
                try Task.checkCancellation()
                try wave.fill(to: min(stop, at + frame * 2000))
                let end = min(wave.end, stop)
                guard end - at >= frame else { break }
                let x = wave.copy(at, end - (end - at) % frame)
                for i in stride(from: 0, to: x.count, by: frame) {
                    var sum: Float = 0
                    for j in i..<(i + frame) { sum += x[j] * x[j] }
                    levels.append(10 * log10(sum / Float(frame) + 1e-10))
                }
                at = end - (end - at) % frame
                wave.drop(before: at)
            }
            let speech = core.audiobookSpeech(levels: levels, frameSeconds: Double(frame) / Aligner.rate, from: from)
            return .vad(speech: speech, music: [])
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    // MARK: 精修

    /// The model's letters over the window, from what 合成 saved for the whole file when it's
    /// there, else run now; the core lays the sentences along them.
    @concurrent nonisolated private static func align(_ core: YuyueCore, _ dataDir: URL, url: URL, cache: URL, fileSeconds: Double, from: Double, to: Double, sentences: [String], lang: String, offset: Double) async -> AudiobookResult {
        do {
            if let done = core.audiobookAlignWindowSaved(sentences: sentences, lang: lang, path: cache.path(percentEncoded: false), fileSeconds: fileSeconds, offset: offset, from: from, to: to) {
                return .align(sentences: done)
            }
            let (a, b) = (max(0, from - offset), min(fileSeconds, to - offset))
            guard b > a else { return .align(sentences: []) }
            let out = try await Aligner.emissions(dataDir, file: url, from: a, to: b) { _ in }
            return .align(sentences: core.audiobookAlignWindow(sentences: sentences, lang: lang, emissions: out.em, frames: UInt32(out.frames), seconds: out.seconds, from: a + offset))
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    /// 听写 by the alignment model's letters (no system transcriber here).
    @concurrent nonisolated private static func letters(_ core: YuyueCore, _ dataDir: URL, url: URL, cache: URL, fileSeconds: Double, spans: [TimeSpan], offset: Double) async -> AudiobookResult {
        do {
            var words: [HeardWord] = []
            let saved = FileManager.default.fileExists(atPath: cache.path(percentEncoded: false))
            for span in spans {
                try Task.checkCancellation()
                if saved {
                    words += core.audiobookHeardLettersSaved(path: cache.path(percentEncoded: false), fileSeconds: fileSeconds, offset: offset, from: span.t0, to: span.t1)
                } else {
                    let (a, b) = (max(0, span.t0 - offset), min(fileSeconds, span.t1 - offset))
                    guard b > a else { continue }
                    let out = try await Aligner.emissions(dataDir, file: url, from: a, to: b) { _ in }
                    words += core.audiobookHeardLetters(emissions: out.em, frames: UInt32(out.frames), seconds: out.seconds, from: a + offset)
                }
            }
            return .transcribe(words: words)
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }
}

/// 听写 with the system's own transcriber (`SpeechTranscriber` on macOS 26 / iOS 26+, falling back
/// to `SFSpeechRecognizer` on macOS 15 / iOS 18): the words with their times.
nonisolated enum Transcriber {
    static var available: Bool {
        if #available(macOS 26, iOS 26, *), SpeechTranscriber.isAvailable { return true }
        let status = SFSpeechRecognizer.authorizationStatus()
        guard status == .authorized || status == .notDetermined else { return false }
        return ["zh-CN", "en-US", Locale.current.identifier].contains { SFSpeechRecognizer(locale: Locale(identifier: $0))?.isAvailable == true }
    }

    /// `punctuated`: the transcriber's commas and stops kept, on the word before them (文字版);
    /// for lining up, words only.
    static func words(url: URL, spans: [TimeSpan], lang: String, offset: Double, punctuated: Bool = false) async -> AudiobookResult {
        do {
            if #available(macOS 26, iOS 26, *), SpeechTranscriber.isAvailable {
                return .transcribe(words: try await run(url: url, spans: spans, lang: lang, offset: offset, punctuated: punctuated))
            }
            return .transcribe(words: try await runLegacy(url: url, spans: spans, lang: lang, offset: offset, punctuated: punctuated))
        } catch is CancellationError {
            return .returned
        } catch {
            return .failed(reason: error.localizedDescription)
        }
    }

    @concurrent private static func runLegacy(url: URL, spans: [TimeSpan], lang: String, offset: Double, punctuated: Bool) async throws -> [HeardWord] {
        if SFSpeechRecognizer.authorizationStatus() == .notDetermined {
            let status = await withCheckedContinuation { cont in
                SFSpeechRecognizer.requestAuthorization { cont.resume(returning: $0) }
            }
            guard status == .authorized else { throw AudiobookError.model }
        } else if SFSpeechRecognizer.authorizationStatus() != .authorized {
            throw AudiobookError.model
        }
        let want: [String] = switch lang {
        case "zh": ["zh-CN", "zh-TW"]
        case "en": ["en-US", "en-GB"]
        default: [Locale.current.identifier, "zh-CN", "en-US"]
        }
        guard let recognizer = want.lazy.compactMap({ SFSpeechRecognizer(locale: Locale(identifier: $0)) }).first(where: \.isAvailable) else {
            throw AudiobookError.model
        }
        let chinese = lang != "en"
        let file = try AVAudioFile(forReading: url)
        let srcRate = file.processingFormat.sampleRate
        guard let pcm16k = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false),
              let converter = AVAudioConverter(from: file.processingFormat, to: pcm16k) else {
            throw AudiobookError.unreadable(url.lastPathComponent)
        }
        var outWords: [HeardWord] = []
        for span in spans.sorted(by: { $0.t0 < $1.t0 }) {
            let spanStart = max(0, span.t0 - offset)
            let spanEnd = min(Double(file.length) / srcRate, span.t1 - offset)
            var chunkStart = spanStart
            // Recognize in <= 50 s slices so SFSpeechRecognizer never hits its 1-minute limit.
            while chunkStart < spanEnd - 0.05 {
                try Task.checkCancellation()
                let chunkEnd = min(spanEnd, chunkStart + 50)
                let tmp = FileManager.default.temporaryDirectory.appending(path: "yy-asr-\(UUID().uuidString).caf")
                defer { try? FileManager.default.removeItem(at: tmp) }
                do {
                    let writer = try AVAudioFile(forWriting: tmp, settings: pcm16k.settings)
                    file.framePosition = min(file.length, AVAudioFramePosition(chunkStart * srcRate))
                    let stopFrame = min(file.length, AVAudioFramePosition(chunkEnd * srcRate))
                    converter.reset()
                    while file.framePosition < stopFrame {
                        let wantFrames = AVAudioFrameCount(min(32_768, stopFrame - file.framePosition))
                        guard let buf = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: wantFrames) else { break }
                        try file.read(into: buf, frameCount: wantFrames)
                        if buf.frameLength == 0 { break }
                        guard let dst = AVAudioPCMBuffer(pcmFormat: pcm16k, frameCapacity: AVAudioFrameCount(Double(buf.frameLength) * 16_000 / srcRate) + 512) else { break }
                        var fed = false
                        var convErr: NSError?
                        converter.convert(to: dst, error: &convErr) { _, state in
                            if fed { state.pointee = .noDataNow; return nil }
                            fed = true
                            state.pointee = .haveData
                            return buf
                        }
                        if let convErr { throw convErr }
                        if dst.frameLength > 0 { try writer.write(from: dst) }
                    }
                }
                let segments: [(String, Double, Double)] = try await withCheckedThrowingContinuation { cont in
                    let req = SFSpeechURLRecognitionRequest(url: tmp)
                    req.shouldReportPartialResults = false
                    req.addsPunctuation = punctuated
                    if recognizer.supportsOnDeviceRecognition { req.requiresOnDeviceRecognition = true }
                    final class Once: @unchecked Sendable {
                        var done = false
                        var task: SFSpeechRecognitionTask?
                    }
                    let once = Once()
                    once.task = recognizer.recognitionTask(with: req) { result, error in
                        guard !once.done else { return }
                        if let result, result.isFinal {
                            once.done = true
                            let segs = result.bestTranscription.segments.map { ($0.substring, $0.timestamp, max(0.05, $0.duration)) }
                            cont.resume(returning: segs)
                        } else if let error {
                            once.done = true
                            // Silence in a chunk often yields "No speech detected"; treat as empty rather than failing the file.
                            let ns = error as NSError
                            if ns.domain == "kAFAssistantErrorDomain" && (ns.code == 1110 || ns.code == 203) {
                                cont.resume(returning: [])
                            } else {
                                cont.resume(throwing: error)
                            }
                        }
                    }
                }
                let base = offset + chunkStart
                for (rawSub, ts, dur) in segments {
                    let raw = rawSub.trimmingCharacters(in: .whitespacesAndNewlines)
                    let text = raw.trimmingCharacters(in: .punctuationCharacters)
                    if punctuated, text.isEmpty, !raw.isEmpty, let last = outWords.popLast() {
                        outWords.append(HeardWord(text: last.text + raw, t0: last.t0, t1: last.t1))
                        continue
                    }
                    guard !text.isEmpty else { continue }
                    let tail = punctuated ? String(raw.drop(while: { !$0.isPunctuation }).reversed().prefix(while: { $0.isPunctuation }).reversed()) : ""
                    let chars = chinese ? text.map(String.init) : [text]
                    let step = dur / Double(chars.count)
                    for (i, c) in chars.enumerated() {
                        outWords.append(HeardWord(text: c, t0: base + ts + step * Double(i), t1: base + ts + step * Double(i + 1)))
                    }
                    if !tail.isEmpty, let last = outWords.popLast() {
                        outWords.append(HeardWord(text: last.text + tail, t0: last.t0, t1: last.t1))
                    }
                }
                chunkStart = chunkEnd
            }
        }
        return outWords
    }

    @available(macOS 26, iOS 26, *)
    private static func locale(_ lang: String) async throws -> Locale {
        let want: [String] = switch lang {
        case "zh": ["zh-CN"]
        case "en": ["en-US"]
        default: [Locale.current.identifier, "zh-CN"]
        }
        for w in want {
            if let l = await SpeechTranscriber.supportedLocale(equivalentTo: Locale(identifier: w)) { return l }
        }
        throw AudiobookError.model
    }

    @available(macOS 26, iOS 26, *)
    @concurrent private static func run(url: URL, spans: [TimeSpan], lang: String, offset: Double, punctuated: Bool) async throws -> [HeardWord] {
        let transcriber = SpeechTranscriber(locale: try await locale(lang), transcriptionOptions: [], reportingOptions: [], attributeOptions: [.audioTimeRange])
        // The language's pack, fetched by the system the first time.
        if let request = try await AssetInventory.assetInstallationRequest(supporting: [transcriber]) {
            try await request.downloadAndInstall()
        }
        guard let format = await SpeechAnalyzer.bestAvailableAudioFormat(compatibleWith: [transcriber]) else { throw AudiobookError.model }
        let analyzer = SpeechAnalyzer(modules: [transcriber])
        let (stream, input) = AsyncStream<AnalyzerInput>.makeStream()
        let chinese = lang != "en"
        let reader = Task {
            var words: [HeardWord] = []
            for try await r in transcriber.results {
                for run in r.text.runs {
                    let raw = String(r.text[run.range].characters).trimmingCharacters(in: .whitespacesAndNewlines)
                    let text = raw.trimmingCharacters(in: .punctuationCharacters)
                    // A stop or comma on its own goes onto the word before it.
                    if punctuated, text.isEmpty, !raw.isEmpty, let last = words.popLast() {
                        words.append(HeardWord(text: last.text + raw, t0: last.t0, t1: last.t1))
                        continue
                    }
                    guard let t = run.audioTimeRange, !text.isEmpty else { continue }
                    let tail = punctuated ? String(raw.drop(while: { !$0.isPunctuation }).reversed().prefix(while: { $0.isPunctuation }).reversed()) : ""
                    // A run can be a few characters in Chinese: one word for each, time shared.
                    let chars = chinese ? text.map(String.init) : [text]
                    let step = (t.end.seconds - t.start.seconds) / Double(chars.count)
                    for (i, c) in chars.enumerated() {
                        words.append(HeardWord(text: c, t0: t.start.seconds + step * Double(i), t1: t.start.seconds + step * Double(i + 1)))
                    }
                    if !tail.isEmpty, let last = words.popLast() { words.append(HeardWord(text: last.text + tail, t0: last.t0, t1: last.t1)) }
                }
            }
            return words
        }
        try await analyzer.start(inputSequence: stream)
        let file = try AVAudioFile(forReading: url)
        guard let converter = AVAudioConverter(from: file.processingFormat, to: format) else { throw AudiobookError.unreadable(url.lastPathComponent) }
        let rate = file.processingFormat.sampleRate
        let scale = CMTimeScale(format.sampleRate)
        // Stamped in the whole recording's samples, so the words come back in its seconds.
        var next: Int64 = 0
        for span in spans.sorted(by: { $0.t0 < $1.t0 }) {
            try Task.checkCancellation()
            let start = max(0, span.t0 - offset), end = span.t1 - offset
            file.framePosition = min(file.length, AVAudioFramePosition(start * rate))
            var at = max(next, Int64((start + offset) * format.sampleRate))
            converter.reset()
            while file.framePosition < min(file.length, AVAudioFramePosition(end * rate)) {
                let want = AVAudioFrameCount(min(32_768, AVAudioFramePosition(end * rate) - file.framePosition))
                guard let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: want) else { break }
                try file.read(into: buffer, frameCount: want)
                if buffer.frameLength == 0 { break }
                guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(Double(buffer.frameLength) * format.sampleRate / rate) + 1024) else { break }
                var fed = false
                var error: NSError?
                converter.convert(to: out, error: &error) { _, state in
                    if fed { state.pointee = .noDataNow; return nil }
                    fed = true
                    state.pointee = .haveData
                    return buffer
                }
                if let error { throw error }
                guard out.frameLength > 0 else { continue }
                input.yield(AnalyzerInput(buffer: out, bufferStartTime: CMTime(value: at, timescale: scale)))
                at += Int64(out.frameLength)
            }
            next = at
        }
        input.finish()
        try await analyzer.finalizeAndFinishThroughEndOfInput()
        return try await reader.value
    }
}
