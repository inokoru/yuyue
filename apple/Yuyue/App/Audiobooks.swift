import AVFoundation
import Foundation
import Observation
import UserNotifications
import YuyueCore
#if canImport(UIKit)
import UIKit
#endif

/// 有声书: a recording of a book laid against the e-book on the shelf, so it
/// can be listened to in the narrator's voice with the ink line following along.
///
/// The recording is kept on this device beside the library, in `audiobooks/<id>/`: its files,
/// `info.json`, what the transcriber heard of each file (so a stopped job picks up where it was),
/// and `timings.json`, where each sentence is in the recording.
struct Audiobook: Codable, Identifiable, Equatable {
    let id: String
    var title: String
    var author: String?
    /// The recording's files, in order, and how long each is in seconds.
    var files: [String]
    var seconds: [Double]
    /// The e-book it's laid against.
    var book: String?
    /// When it was made, and how long that took in seconds.
    var made: Date?
    var took: Double?
    /// 片头 looked at and skipped.
    var introSkipped = false

    var total: Double { seconds.reduce(0, +) }

    /// `6 小时 12 分`, `38 分钟`.
    static func length(_ seconds: Double) -> String {
        let m = Int(seconds / 60)
        return m >= 60 ? String(localized: "\(m / 60) 小时\(m % 60 > 0 ? String(localized: " \(m % 60) 分") : "")") : String(localized: "\(max(1, m)) 分钟")
    }
}

/// Where each sentence of the book is in the recording, from `alignRecording`, plus any 微调.
nonisolated struct Timings: Codable, Equatable, Sendable {
    struct Sentence: Codable, Equatable {
        var section: UInt32
        var sentence: UInt32
        /// Seconds from the start of the whole recording; negative when it isn't there.
        var start: Double
        var end: Double
        var heard: Float
        /// Moved by hand.
        var adjusted = false
    }

    struct Section: Codable, Equatable, Sendable {
        var section: UInt32
        var sentences: UInt32
        var heard: Float
        var missing: UInt32
        var unsure: UInt32
    }

    var sentences: [Sentence]
    var sections: [Section]
    var intro: Double
    /// Up to where the recording has been heard, in seconds; the whole of it once made.
    var through: Double

    init(_ a: Alignment, through: Double) {
        sentences = a.sentences.map { Sentence(section: $0.section, sentence: $0.sentence, start: $0.start, end: $0.end, heard: $0.heard) }
        sections = a.sections.map { Section(section: $0.section, sentences: $0.sentences, heard: $0.heard, missing: $0.missing, unsure: $0.unsure) }
        intro = a.intro
        self.through = through
        // A sentence ends by the time the next one starts.
        var next: Double?
        for i in sentences.indices.reversed() where sentences[i].start >= 0 {
            if let n = next, sentences[i].end > n, n > sentences[i].start { sentences[i].end = n }
            next = sentences[i].start
        }
    }

    func index(_ section: UInt32, _ sentence: Int) -> Int? {
        sentences.firstIndex { $0.section == section && $0.sentence == UInt32(sentence) }
    }

    /// The new lay-out's times taken in: a sentence it's sure of (精确, or pinned) gets its
    /// times; one it only spreads, or says isn't read, keeps what the alignment model alone gave,
    /// when it gave anything; one moved by hand (微调) stays where it was put.
    init(map: SyncMap, keeping old: Timings?, through: Double) {
        var byKey: [String: Sentence] = [:]
        for s in old?.sentences ?? [] { byKey["\(s.section).\(s.sentence)"] = s }
        sentences = map.rows.map { r in
            let key = "\(r.section).\(r.sentence)"
            if let o = byKey[key], o.adjusted { return o }
            let sure = r.by == .auto || r.by == .user
            if !sure, let o = byKey[key] { return o }
            switch r.by {
            case .unread: return Sentence(section: r.section, sentence: r.sentence, start: -1, end: -1, heard: 0)
            default: return Sentence(section: r.section, sentence: r.sentence, start: Double(r.t0Ms) / 1000, end: Double(r.t1Ms) / 1000, heard: Float(r.conf) / 255)
            }
        }
        sections = old?.sections ?? []
        intro = map.extras.first { $0.place == .head }?.t1 ?? old?.intro ?? 0
        self.through = through
        var next: Double?
        for i in sentences.indices.reversed() where sentences[i].start >= 0 {
            if let n = next, sentences[i].end > n, n > sentences[i].start { sentences[i].end = n }
            next = sentences[i].start
        }
    }

    /// From the sync log, as another device (or this one) kept it.
    init(_ l: Listening) {
        sentences = l.sentences.map { Sentence(section: $0.section, sentence: $0.sentence, start: $0.start, end: $0.end, heard: $0.heard, adjusted: $0.adjusted) }
        sections = l.sections.map { Section(section: $0.section, sentences: $0.sentences, heard: $0.heard, missing: $0.missing, unsure: $0.unsure) }
        intro = l.intro
        through = l.through
    }

    /// For the sync log.
    func listening(recording: String, book: String, audiobook a: Audiobook) -> Listening {
        Listening(
            recording: recording, book: book,
            sentences: sentences.map { ListenSentence(section: $0.section, sentence: $0.sentence, start: $0.start, end: $0.end, heard: $0.heard, adjusted: $0.adjusted) },
            sections: sections.map { SectionFit(section: $0.section, sentences: $0.sentences, heard: $0.heard, missing: $0.missing, unsure: $0.unsure) },
            intro: intro, through: through, introSkipped: a.introSkipped,
            madeMs: a.made.map { Int64($0.timeIntervalSince1970 * 1000) }, took: a.took)
    }

    /// Whether the sentence was heard well enough to trust where it is.
    static func unsure(_ s: Sentence) -> Bool { s.start >= 0 && s.heard < 0.5 && !s.adjusted }
}

/// What the map says about the book, for reading along with the recording: which sentences it
/// doesn't read, which it reads in a stretch not sure sentence by sentence, which chapters are
/// only followed as a whole, and what it has that the book doesn't (片头, talk in the middle).
nonisolated struct RecordingGuide: @unchecked Sendable {
    var settings: SkipSettings
    /// By section, each sentence's grade.
    var grades: [UInt32: [UInt32: SyncBy]] = [:]
    var extras: [ExtraSpan] = []
    /// Sentences the reader asked the voice to read (用朗读补上这段 in 微调).
    var voiced: Set<String> = []
    /// The recording's first sentence of the book, and its last.
    var first: (UInt32, UInt32)?
    var last: (UInt32, UInt32)?
    /// Each sentence's start and end in the recording.
    var times: [String: (Double, Double)] = [:]

    static func key(_ section: UInt32, _ sentence: UInt32) -> String { "\(section).\(sentence)" }

    init(map: SyncMap?, settings: SkipSettings, timings: Timings?) {
        self.settings = settings
        guard let map else { return }
        for r in map.rows {
            grades[r.section, default: [:]][r.sentence] = r.by
        }
        extras = map.extras
        for s in timings?.sentences ?? [] {
            if s.adjusted, s.start < 0 { voiced.insert(Self.key(s.section, s.sentence)) }
            if s.start >= 0, s.end > s.start { times[Self.key(s.section, s.sentence)] = (s.start, s.end) }
        }
        let timed = (timings?.sentences ?? []).filter { $0.start >= 0 && $0.end > $0.start }
        if let f = timed.min(by: { $0.start < $1.start }) { first = (f.section, f.sentence) }
        if let l = timed.max(by: { $0.end < $1.end }) { last = (l.section, l.sentence) }
    }

    func grade(_ section: UInt32, _ sentence: Int) -> SyncBy? { grades[section]?[UInt32(max(0, sentence))] }

    /// Not in the recording: skipped, unless the voice reads it (用朗读补上, or asked for in 微调).
    func skips(_ section: UInt32, _ sentence: Int) -> Bool {
        grade(section, sentence) == .unread && !settings.fillUnread && !voiced.contains(Self.key(section, UInt32(max(0, sentence))))
    }

    /// The whole chapter only followed as a whole.
    func chapterOnly(_ section: UInt32) -> Bool {
        guard let g = grades[section], !g.isEmpty else { return false }
        return g.values.contains(.chapter) && !g.values.contains(.auto)
    }

    /// The chapter's sentences the recording doesn't read.
    func unread(_ section: UInt32) -> [Int] {
        (grades[section] ?? [:]).filter { $0.value == .unread }.map { Int($0.key) }.sorted()
    }

    /// A sentence in a stretch not sure sentence by sentence starts from the stretch's first.
    func stretchStart(_ section: UInt32, _ sentence: Int) -> Int {
        guard grade(section, sentence) == .spread else { return sentence }
        var s = sentence
        while s > 0, grade(section, s - 1) == .spread { s -= 1 }
        return s
    }

    /// 片头: before the recording's first sentence.
    var head: ExtraSpan? { extras.first { $0.place == .head } }

    func isFirst(_ section: UInt32, _ sentence: Int) -> Bool {
        first.map { $0.0 == section && $0.1 == UInt32(max(0, sentence)) } ?? false
    }

    /// What the recording has right after this sentence and plays (not skipped by the settings):
    /// talk in the middle, or 片尾 after the last.
    func extra(after section: UInt32, _ sentence: Int) -> ExtraSpan? {
        guard let end = times[Self.key(section, UInt32(max(0, sentence)))]?.1 else { return nil }
        return extras.first { e in
            abs(e.t0 - end) < 0.5 && e.t1 - e.t0 >= 1 && (e.place == .middle && !settings.middle || e.place == .tail && !settings.headTail)
        }
    }
}

/// Reads a sentence's stretch of the recording as audio the player can play. Off the main actor:
/// the player asks for the next sentences while one plays.
nonisolated final class Recording: @unchecked Sendable {
    private let dir: URL
    private let files: [String]
    private let starts: [Double]
    private let lock = NSLock()
    private var timings: Timings
    private var open: [Int: AVAudioFile] = [:]

    init(dir: URL, audiobook: Audiobook, timings: Timings) {
        self.dir = dir
        files = audiobook.files
        var at = 0.0
        starts = audiobook.seconds.map { s in defer { at += s }; return at }
        self.timings = timings
    }

    func update(_ timings: Timings) {
        lock.withLock { self.timings = timings }
    }

    /// The sentence in the narrator's voice, or nil when the recording doesn't have it; from
    /// `from` seconds when that's inside it (a word found by 搜索).
    func speech(section: UInt32, sentence: Int, from: Double? = nil) -> Speech? {
        let t: Timings.Sentence? = lock.withLock {
            timings.index(section, sentence).map { timings.sentences[$0] }
        }
        guard let t, t.start >= 0, t.end > t.start else { return nil }
        let start = from.map { min(max($0, t.start), t.end - 0.05) } ?? t.start
        return clip(from: start, to: t.end)
    }

    /// Where the sentence starts in the recording.
    func start(section: UInt32, sentence: Int) -> Double? {
        lock.withLock { timings.index(section, sentence).map { timings.sentences[$0].start }.flatMap { $0 >= 0 ? $0 : nil } }
    }

    /// Seconds of the recording from this sentence to the end of its chapter, for 本章还剩.
    func secondsLeft(section: UInt32, sentence: Int) -> Double? {
        lock.withLock {
            let here = timings.sentences.filter { $0.section == section && $0.start >= 0 }
            guard let end = here.last?.end, let from = here.first(where: { $0.sentence >= UInt32(sentence) })?.start else { return nil }
            return max(0, end - from)
        }
    }

    /// Any stretch of the recording, for 试听.
    func clip(from start: Double, to end: Double) -> Speech? {
        lock.lock()
        defer { lock.unlock() }
        guard let i = starts.lastIndex(where: { $0 <= start }), let file = file(i) else { return nil }
        let rate = file.processingFormat.sampleRate
        let from = AVAudioFramePosition((start - starts[i]) * rate)
        let count = AVAudioFrameCount(max(0, min((end - start) * rate, Double(file.length - from))))
        guard count > 0, from < file.length,
              let buffer = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: count) else { return nil }
        file.framePosition = max(0, from)
        do { try file.read(into: buffer, frameCount: count) } catch { return nil }
        return Speech.make(from: [buffer])
    }

    /// Loudness around a moment, for the 微调 waveform: `bars` values 0–1 from `start` to `end`.
    func levels(from start: Double, to end: Double, bars: Int) -> [Float] {
        guard let speech = clip(from: max(0, start), to: end), let data = speech.buffer.floatChannelData?[0] else { return [] }
        let n = Int(speech.buffer.frameLength)
        let per = max(1, n / bars)
        var out: [Float] = []
        for b in 0 ..< bars {
            let lo = b * per, hi = min(n, lo + per)
            guard lo < hi else { out.append(0); continue }
            var peak: Float = 0
            for k in stride(from: lo, to: hi, by: 8) { peak = max(peak, abs(data[k])) }
            out.append(peak)
        }
        let top = max(out.max() ?? 1, 0.01)
        return out.map { $0 / top }
    }

    private func file(_ i: Int) -> AVAudioFile? {
        if let f = open[i] { return f }
        guard files.indices.contains(i), let f = try? AVAudioFile(forReading: dir.appending(path: files[i])) else { return nil }
        open[i] = f
        return f
    }
}

@Observable
final class Audiobooks {
    /// The one the app uses; set when the library opens.
    static var shared: Audiobooks?

    private(set) var all: [Audiobook] = []
    /// The one being made, while it is.
    private(set) var job: Job?
    /// Just imported, waiting to be paired with an e-book.
    var pairing: Audiobook?
    /// The 合成 page to show: the one being made, or how one came out.
    var showing: String?

    @ObservationIgnored private let core: YuyueCore
    @ObservationIgnored let dir: URL
    /// The library's folder, where the alignment model is kept.
    @ObservationIgnored let dataDir: URL
    @ObservationIgnored private var recordings: [String: Recording] = [:]
    /// 听写、精修 in the background, toward the recording's map.
    @ObservationIgnored private lazy var work = AudiobookWork(core: core, dataDir: dataDir)

    nonisolated static let extensions: Set<String> = ["m4b", "m4a", "mp3", "aac", "wav", "aiff", "flac"]
    /// Formats that are always locked (Audible's, the old iTunes Store's).
    nonisolated static let locked: Set<String> = ["aa", "aax", "aaxc", "m4p"]
    /// 以后同名的书都这样配.
    static let autoPairKey = "audiobooks.autoPair"

    /// Removed on this device: what the log says of them isn't taken in again.
    private var removedHere: Set<String> {
        get { Set(UserDefaults.standard.stringArray(forKey: "audiobooks.removedHere") ?? []) }
        set { UserDefaults.standard.set(newValue.sorted(), forKey: "audiobooks.removedHere") }
    }

    init(core: YuyueCore, dataDir: URL) {
        self.core = core
        self.dataDir = dataDir
        dir = dataDir.appending(path: "audiobooks", directoryHint: .isDirectory)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        load()
        // Defer multi-MB `timings.json` sync comparison until after cold-start Frame 1 renders.
        Task { @MainActor [weak self] in
            try? await Task.sleep(for: .milliseconds(450))
            self?.adopt()
        }
        #if os(macOS)
        // A Mac does the whole of each laid-out recording, a little after opening.
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(20))
            guard let self else { return }
            for a in all where a.book != nil { work(on: a.id, at: nil) }
        }
        #endif
    }

    /// The new lay-out of recording `id` is made: its times go into the timings everything else
    /// reads (朗读 in the narrator's voice, 微调, the sync log).
    func adoptMap(_ id: String) {
        guard let a = all.first(where: { $0.id == id }), a.book != nil, job?.audiobook != id,
              let map = try? core.audiobookMap(bookId: id), !map.rows.isEmpty else { return }
        let existing = timings(a)
        let t = Timings(map: map, keeping: existing, through: a.total)
        guard t != existing else { return }
        save(t, for: a)
    }

    // MARK: 文字版

    /// 转成文字 under way, by recording.
    @Observable final class TextJob {
        let files: Int
        let total: Double
        var file = 0
        var heard = 0.0
        var problem: String?
        let started = Date()
        init(files: Int, total: Double) { self.files = files; self.total = total }
        var fraction: Double { total > 0 ? min(1, heard / total) : 0 }
        var left: Double? {
            let spent = Date().timeIntervalSince(started)
            guard fraction > 0.03, spent > 5 else { return nil }
            return spent / fraction * (1 - fraction)
        }
    }

    private(set) var textJobs: [String: TextJob] = [:]

    /// 没有电子书: the whole recording transcribed with care on this device, made into a book, and
    /// laid against it like any e-book.
    func makeTextVersion(_ audio: BookSummary, library: Library) {
        guard textJobs[audio.id] == nil, let rec = try? core.audioRecording(bookId: audio.id) else { return }
        let job = TextJob(files: rec.files.count, total: rec.seconds)
        textJobs[audio.id] = job
        let core = core
        let lang = audio.language.hasPrefix("en") ? "en" : "zh"
        Task {
            var words: [HeardWord] = []
            var offset = 0.0
            for (i, f) in rec.files.enumerated() {
                job.file = i
                // Ten minutes at a time, so a long file shows how far it's got.
                var from = 0.0
                while from < f.seconds {
                    let to = min(f.seconds, from + 600)
                    let r = await Transcriber.words(url: URL(filePath: f.path), spans: [TimeSpan(t0: offset + from, t1: offset + to)], lang: lang, offset: offset, punctuated: true)
                    guard case let .transcribe(got) = r else {
                        job.problem = String(localized: "转写没做完，再点一次接着转")
                        textJobs[audio.id] = nil
                        return
                    }
                    words += got
                    from = to
                    job.heard = offset + from
                }
                offset += f.seconds
            }
            let made = await Self.makeTextVersion(core, bookID: audio.id, words: words, title: audio.title)
            textJobs[audio.id] = nil
            guard let made else { return }
            library.reload()
            library.pair(audio, with: made)
        }
    }

    @concurrent nonisolated private static func makeTextVersion(_ core: YuyueCore, bookID: String, words: [HeardWord], title: String) async -> BookSummary? {
        try? core.audiobookMakeTextVersion(bookId: bookID, words: words, title: title)
    }

    /// Toward the map of recording `id` (an 有声书 on the shelf), nearest `at` seconds first.
    func work(on id: String, at: Double?) {
        guard let a = all.first(where: { $0.id == id }), a.book != nil, (try? core.audioRecording(bookId: id)) != nil else { return }
        work.start(id, book: a.book, at: at, folder: folder(id))
    }

    private func load() {
        let ids = (try? FileManager.default.contentsOfDirectory(atPath: dir.path(percentEncoded: false))) ?? []
        all = ids.compactMap { id in
            (try? Data(contentsOf: folder(id).appending(path: "info.json"))).flatMap { try? JSONDecoder().decode(Audiobook.self, from: $0) }
        }
        .sorted { $0.title < $1.title }
    }

    /// After 同步 brought one in, or changed one's timings.
    func reload() {
        recordings = [:]
        timingsCache = [:]
        load()
        adopt()
    }

    // MARK: 同步

    /// What another device laid out comes in through the sync log: where each sentence is and
    /// which e-book the recording goes with. Here it's taken in for every recording this device
    /// has (its own copy, same id), and what this device made before the log kept it goes out.
    private func adopt() {
        guard let pairs = try? core.listeningPairs() else { return }
        var changed = false
        // One being made here is this device's to write.
        let removed = removedHere
        for pair in pairs where job?.audiobook != pair.recording && !removed.contains(pair.recording) {
            guard let l = try? core.listening(recording: pair.recording) else { continue }
            var a: Audiobook
            if let mine = all.first(where: { $0.id == pair.recording }) {
                a = mine
            } else {
                // An 有声书 on this shelf that another device laid out: link its files in.
                guard let shelf = try? core.book(id: pair.recording).summary, shelf.available, shelf.format == .audio,
                      let added = try? add(shelf: shelf) else { continue }
                a = added
            }
            let t = Timings(l)
            if timings(a) != t {
                timingsCache[a.id] = t
                if let data = try? JSONEncoder().encode(t) { try? data.write(to: folder(a.id).appending(path: "timings.json"), options: .atomic) }
                recordings[a.id]?.update(t)
                NotificationCenter.default.post(name: Self.timingsChanged, object: a.id)
                changed = true
            }
            let made = l.madeMs.map { Date(timeIntervalSince1970: Double($0) / 1000) }
            if a.book != l.book || a.made != made || a.took != l.took || a.introSkipped != l.introSkipped {
                a.book = l.book
                a.made = made
                a.took = l.took
                a.introSkipped = l.introSkipped
                write(a)
                changed = true
            }
        }
        // Made here before the log kept them: out they go.
        let known = Set(pairs.map(\.recording))
        for a in all where !known.contains(a.id) && a.book != nil && a.made != nil {
            if let t = timings(a) { push(a, t) }
        }
        if changed {
            recordings = [:]
            timingsCache = [:]
        }
    }

    /// This device's lay-out into the sync log.
    private func push(_ a: Audiobook, _ t: Timings) {
        guard let book = a.book else { return }
        try? core.saveListening(listening: t.listening(recording: a.id, book: book, audiobook: a))
    }

    func folder(_ id: String) -> URL { dir.appending(path: id, directoryHint: .isDirectory) }

    func save(_ a: Audiobook) {
        write(a)
        if a.book != nil, let t = timings(a) { push(a, t) }
    }

    private func write(_ a: Audiobook) {
        if let data = try? JSONEncoder().encode(a) { try? data.write(to: folder(a.id).appending(path: "info.json"), options: .atomic) }
        if let i = all.firstIndex(where: { $0.id == a.id }) { all[i] = a } else { all.append(a) }
    }

    /// The recording laid against `book`.
    func audiobook(for book: String) -> Audiobook? {
        all.first { $0.book == book }
    }

    @ObservationIgnored private var timingsCache: [String: Timings] = [:]

    func timings(_ a: Audiobook) -> Timings? {
        if let hit = timingsCache[a.id] { return hit }
        guard let loaded = (try? Data(contentsOf: folder(a.id).appending(path: "timings.json"))).flatMap({ try? JSONDecoder().decode(Timings.self, from: $0) }) else {
            return nil
        }
        timingsCache[a.id] = loaded
        return loaded
    }

    func save(_ t: Timings, for a: Audiobook) {
        timingsCache[a.id] = t
        if let data = try? JSONEncoder().encode(t) { try? data.write(to: folder(a.id).appending(path: "timings.json"), options: .atomic) }
        recordings[a.id]?.update(t)
        NotificationCenter.default.post(name: Self.timingsChanged, object: a.id)
        push(all.first { $0.id == a.id } ?? a, t)
    }

    /// Posted with the audiobook's id after 微调 saves its timings, for the pages showing them.
    static let timingsChanged = Notification.Name("yuyue.audiobook.timings")

    /// For the player: the narrator's voice for `book`, once some of it has been laid out.
    func recording(for book: String) -> Recording? {
        guard let a = audiobook(for: book) else { return nil }
        if let r = recordings[a.id] { return r }
        guard let t = timings(a) else { return nil }
        let r = Recording(dir: folder(a.id), audiobook: a, timings: t)
        recordings[a.id] = r
        return r
    }

    // MARK: 这本录音

    /// What the map says about the book `book` (the e-book) for reading along with its recording.
    func guide(for book: String) -> RecordingGuide? {
        guard let a = audiobook(for: book) else { return nil }
        return RecordingGuide(map: try? core.audiobookMap(bookId: a.id), settings: settings(a.id), timings: timings(a))
    }

    /// A recording's own settings (片头片尾, 章间音乐, 人声, 停顿, 朗读补上), synced.
    func settings(_ recording: String) -> SkipSettings {
        (try? core.audiobookSkipSettings(bookId: recording)) ?? SkipSettings(headTail: true, middle: false, shortenChapterGaps: true, clearerVoice: false, shortenPauses: false, fillUnread: false, wordFollow: false)
    }

    func set(_ s: SkipSettings, for recording: String) {
        try? core.audiobookSetSkipSettings(bookId: recording, s: s)
        NotificationCenter.default.post(name: Self.timingsChanged, object: recording)
    }

    // MARK: Bringing one in

    /// A recording's files (one book in one or more files) into the library, waiting to be paired.
    func add(_ urls: [URL]) async throws -> Audiobook {
        let files = urls.sorted { $0.lastPathComponent.localizedStandardCompare($1.lastPathComponent) == .orderedAscending }
        guard let first = files.first else { throw CocoaError(.fileNoSuchFile) }
        let id = UUID().uuidString.lowercased()
        let folder = folder(id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var names: [String] = []
        var seconds: [Double] = []
        do {
            for (i, url) in files.enumerated() {
                let name = String(format: "%03d.", i) + url.pathExtension.lowercased()
                try FileManager.default.copyItem(at: url, to: folder.appending(path: name))
                guard let f = try? AVAudioFile(forReading: folder.appending(path: name)), f.length > 0 else {
                    throw AudiobookError.unreadable(url.lastPathComponent)
                }
                names.append(name)
                seconds.append(Double(f.length) / f.processingFormat.sampleRate)
            }
        } catch {
            try? FileManager.default.removeItem(at: folder)
            throw error
        }
        let (title, author) = await AudioImporter.metadata(first, folderName: urls.count > 1 ? first.deletingLastPathComponent().lastPathComponent : nil)
        let a = Audiobook(id: id, title: title, author: author, files: names, seconds: seconds)
        save(a)
        if let cover = await AudioImporter.cover(first) { try? cover.write(to: folder.appending(path: "cover.jpg")) }
        return a
    }

    /// An 有声书 on the shelf, to lay against its e-book: its files linked in (the same disk, so
    /// no second copy), under the shelf book's id.
    func add(shelf book: BookSummary) throws -> Audiobook {
        if let a = all.first(where: { $0.id == book.id }) { return a }
        removedHere.remove(book.id)
        let r = try core.audioRecording(bookId: book.id)
        let folder = folder(book.id)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        var names: [String] = []
        for f in r.files {
            let source = URL(filePath: f.path)
            let name = source.lastPathComponent
            let target = folder.appending(path: name)
            if !FileManager.default.fileExists(atPath: target.path(percentEncoded: false)) {
                do { try FileManager.default.linkItem(at: source, to: target) } catch { try FileManager.default.copyItem(at: source, to: target) }
            }
            names.append(name)
        }
        if let cover = book.coverPath { try? FileManager.default.copyItem(at: URL(filePath: cover), to: folder.appending(path: "cover.jpg")) }
        let a = Audiobook(id: book.id, title: book.title, author: book.authors.first, files: names, seconds: r.files.map(\.seconds))
        save(a)
        return a
    }

    func coverURL(_ a: Audiobook) -> URL? {
        let url = folder(a.id).appending(path: "cover.jpg")
        return FileManager.default.fileExists(atPath: url.path(percentEncoded: false)) ? url : nil
    }

    /// Off this device. Where its sentences are stays in the sync log: the other devices keep
    /// their own copies of the recording. It isn't taken in again here from the log, until it's
    /// laid out again by hand.
    func remove(_ a: Audiobook) {
        if job?.audiobook == a.id { job?.cancel() }
        work.stop(a.id)
        try? FileManager.default.removeItem(at: folder(a.id))
        removedHere.insert(a.id)
        recordings[a.id] = nil
        timingsCache[a.id] = nil
        all.removeAll { $0.id == a.id }
    }

    /// The e-book on the shelf that is the same book: same title once the extras are taken off
    /// (有声书, 播讲, brackets), and the same author when both say one.
    func match(_ a: Audiobook, in books: [BookSummary]) -> BookSummary? {
        let want = Self.plain(a.title)
        if let id = (UserDefaults.standard.dictionary(forKey: Self.pairsKey) as? [String: String])?[want],
           let book = books.first(where: { $0.id == id && $0.available }) {
            return book
        }
        let candidates = books.filter { $0.available && $0.format != .pdf && $0.format != .audio }
        let same = candidates.filter { Self.plain($0.title) == want }
        let near = same.isEmpty ? candidates.filter { want.count >= 2 && (Self.plain($0.title).contains(want) || want.contains(Self.plain($0.title))) && Self.plain($0.title).count >= 2 } : same
        guard !near.isEmpty else { return nil }
        if let author = a.author.map(Self.plain), !author.isEmpty,
           let both = near.first(where: { $0.authors.contains { author.contains(Self.plain($0)) || Self.plain($0).contains(author) } }) {
            return both
        }
        return near.first
    }

    private static let plainBracketsRegex = /[（(【\[《].*?[)）】\]》]/
    private static let plainAudioRegex = /有声(?:书|小说|版)?/
    private static let plainEditionRegex = /(?:全集|完整版|精品|播讲|朗读|演播)/
    private static let plainLeadingNumRegex = /^\d+[\s._-]*/

    static func plain(_ s: String) -> String {
        var t = s.lowercased()
        t.replace(Self.plainBracketsRegex, with: "")
        t.replace(Self.plainAudioRegex, with: "")
        t.replace(Self.plainEditionRegex, with: "")
        t.replace(Self.plainLeadingNumRegex, with: "")
        return t.filter { $0.isLetter || $0.isNumber }
    }

    // MARK: Making it

    /// Lays the recording against `book` on this device (强制对齐): a file at a time through the
    /// alignment model, the sentences laid out again after each, so chapters can be listened to
    /// as they come.
    func make(_ a: Audiobook, with book: BookSummary) {
        var a = a
        a.book = book.id
        a.made = nil
        // One job at a time: one already running (合成 again, another recording) stops first, or
        // the two would run the model side by side and write each other's timings.
        job?.cancel()
        // Another recording of the same book gives way.
        for other in all where other.book == book.id && other.id != a.id { remove(other) }
        save(a)
        pairing = nil
        showing = a.id
        let job = Job(audiobook: a.id, total: a.total, sections: (try? core.book(id: book.id).sections.filter { $0.sentences > 0 }.count) ?? 0)
        self.job = job
        AirTraffic.trackAudioFeatureUsed(kind: "audiobook_align")
        job.task = Task { [weak self] in
            await self?.run(job, a, book)
        }
    }

    private func run(_ job: Job, _ a: Audiobook, _ book: BookSummary) async {
        let started = Date()
        #if os(iOS)
        // A little longer when the app is left; past that it waits until it's back.
        let background = BackgroundTime()
        defer { background.end() }
        UIApplication.shared.isIdleTimerDisabled = true
        defer { UIApplication.shared.isIdleTimerDisabled = false }
        #endif
        Self.askToNotify()
        do {
            // The alignment model, fetched the first time (about 270 MB).
            if !Aligner.installed(dataDir) {
                job.step = .reading
                try await Aligner.ensure(dataDir) { f in Task { @MainActor in job.downloaded = f } }
                job.downloaded = nil
            }
            try Task.checkCancellation()
            // 找出每集念的是哪一段, then 逐句对齐: each file through the model (kept, so a stopped
            // job picks up where it was), the whole book laid out again after each.
            job.step = .transcribing
            var files: [SavedEmissions] = []
            var offset = 0.0
            for (i, name) in a.files.enumerated() {
                let cache = folder(a.id).appending(path: "emissions-\(i).f32")
                let size = (try? FileManager.default.attributesOfItem(atPath: cache.path(percentEncoded: false))[.size] as? Int) ?? 0
                if size == 0 || size % (4 * Aligner.letters) != 0 {
                    let base = offset
                    let out = try await Aligner.emissions(dataDir, file: folder(a.id).appending(path: name)) { done in
                        Task { @MainActor in job.heard = base + done }
                    }
                    try Task.checkCancellation()
                    await Self.writeEmissions(out.em, to: cache)
                }
                // The core reads the model's output from the file itself.
                let em = SavedEmissions(path: cache.path(percentEncoded: false), seconds: a.seconds[i])
                files.append(em)
                offset += a.seconds[i]
                job.heard = offset
                job.transcribed = Date().timeIntervalSince(started)
                job.step = .aligning
                await layOut(a, files, through: offset, job: job)
            }
            job.step = .checking
            await layOut(a, files, through: a.total, job: job)
            Aligner.unload()
            var done = a
            done.made = .now
            done.took = Date().timeIntervalSince(started)
            save(done)
            job.step = .done
            if self.job === job { self.job = nil }
            work(on: done.id, at: nil)
            Self.notify(String(localized: "《\(book.title)》的有声书合成好了"), Timings.summary(timings(done)))
        } catch is CancellationError {
            Aligner.unload()
        } catch {
            Aligner.unload()
            job.problem = Library.isOutOfSpace(error) ? String(localized: "存储空间不够：对齐用的模型要约 800 MB 空间才装得下")
                : (error as? AudiobookError)?.message ?? (Library.offline(error) != nil ? String(localized: "对齐用的模型没下载下来，连上网再试一次")
                : String(localized: "合成没做完（\(error.localizedDescription)）"))
        }
    }

    @concurrent nonisolated private static func writeEmissions(_ em: [Float], to url: URL) async {
        em.withUnsafeBufferPointer { try? Data(buffer: $0).write(to: url, options: .atomic) }
    }

    @concurrent nonisolated private static func computeLayout(
        core: YuyueCore,
        book: String,
        files: [SavedEmissions],
        through: Double,
        old: Timings?,
        timingsURL: URL
    ) async -> Timings? {
        guard let alignment = try? core.alignRecordingSaved(bookId: book, files: files) else { return nil }
        var t = Timings(alignment, through: through)
        // Moves made by hand stay (O(N) index lookup instead of O(N²)).
        if let old {
            var idxByKey: [UInt64: Int] = Dictionary(minimumCapacity: t.sentences.count)
            for (i, s) in t.sentences.enumerated() {
                idxByKey[(UInt64(s.section) << 32) | UInt64(s.sentence)] = i
            }
            for s in old.sentences where s.adjusted {
                let key = (UInt64(s.section) << 32) | UInt64(s.sentence)
                if let i = idxByKey[key] { t.sentences[i] = s }
            }
        }
        if let data = try? JSONEncoder().encode(t) {
            try? data.write(to: timingsURL, options: .atomic)
        }
        return t
    }

    private func layOut(_ a: Audiobook, _ files: [SavedEmissions], through: Double, job: Job) async {
        guard let book = a.book else { return }
        let old = timings(a)
        let url = folder(a.id).appending(path: "timings.json")
        guard let t = await Self.computeLayout(core: core, book: book, files: files, through: through, old: old, timingsURL: url) else { return }
        timingsCache[a.id] = t
        recordings[a.id]?.update(t)
        NotificationCenter.default.post(name: Self.timingsChanged, object: a.id)
        push(all.first { $0.id == a.id } ?? a, t)
        job.timings = t
    }

    func cancel() {
        job?.cancel()
        job = nil
    }

    /// The sheet put away. A recording not paired stays: it's on the shelf as an 有声书 of its own.
    func dismiss() {
        pairing = nil
        showing = nil
        stopListening()
    }

    /// 换一本电子书: remembered, so the next recording of the same name goes to the same book.
    func remember(_ a: Audiobook, _ book: BookSummary) {
        var pairs = UserDefaults.standard.dictionary(forKey: Self.pairsKey) as? [String: String] ?? [:]
        pairs[Self.plain(a.title)] = book.id
        UserDefaults.standard.set(pairs, forKey: Self.pairsKey)
    }

    private static let pairsKey = "audiobooks.pairs"

    /// 以后同名的书都这样配, once it's been seen on the 配对 sheet.
    static var autoPair: Bool { UserDefaults.standard.object(forKey: autoPairKey) as? Bool == true }

    /// 片头 looked at: it's left out when listening anyway, so it only stops being asked about.
    func skipIntro(_ a: Audiobook) {
        var a = a
        a.introSkipped = true
        save(a)
    }

    // MARK: 试听

    @ObservationIgnored private lazy var clips = ClipPlayer()

    /// A stretch of the recording on its own, for 试听 on the result and 微调 pages.
    func listen(_ a: Audiobook, from start: Double, to end: Double) {
        guard let book = a.book, let r = recording(for: book) else { return }
        Task {
            guard let speech = await Self.clip(r, max(0, start), end) else { return }
            clips.play(speech)
        }
    }

    func stopListening() { clips.stop() }

    @concurrent nonisolated private static func clip(_ r: Recording, _ start: Double, _ end: Double) async -> Speech? {
        r.clip(from: start, to: end)
    }

    private static func askToNotify() {
        UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in }
    }

    private static func notify(_ title: String, _ body: String) {
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil))
    }
}

/// 合成中: the four steps, how far, and each chapter as it's laid out.
@Observable
final class Job {
    enum Step: Int { case reading, transcribing, aligning, checking, done }

    let audiobook: String
    let total: Double
    let sections: Int
    var step = Step.reading
    /// Seconds of the recording heard so far.
    var heard = 0.0
    /// How long running the model took, once it's done.
    var transcribed: Double?
    /// 0–1 while the alignment model is being fetched.
    var downloaded: Double?
    var timings: Timings?
    var problem: String?
    let started = Date()
    @ObservationIgnored var task: Task<Void, Never>?

    init(audiobook: String, total: Double, sections: Int) {
        self.audiobook = audiobook
        self.total = total
        self.sections = sections
    }

    var fraction: Double { total > 0 ? min(1, heard / total) : 0 }

    /// Seconds left, from how fast it's gone so far.
    var left: Double? {
        let spent = Date().timeIntervalSince(started)
        guard fraction > 0.02, spent > 5 else { return nil }
        return spent / fraction * (1 - fraction)
    }

    func cancel() { task?.cancel() }
}

enum AudiobookError: Error {
    case unreadable(String)
    case locked(String)
    case language(String)
    case model

    var message: String {
        switch self {
        case .unreadable(let name): String(localized: "“\(name)”读不出声音，换一个 M4B、MP3 或 M4A 文件试试")
        case .locked(let name): String(localized: "“\(name)”有版权保护（DRM），打不开。换一份没加密的录音试试")
        case .language(let l): String(localized: "这台设备还不能转写这种语言（\(l)）")
        case .model: String(localized: "对齐用的模型没下载完整，连上网再试一次")
        }
    }
}

extension Timings {
    /// `21 章已准备好，可以开始听`.
    static func summary(_ t: Timings?) -> String {
        guard let t else { return "" }
        return String(localized: "\(t.sections.count) 章已准备好，可以开始听")
    }
}

#if os(iOS)
/// The extra time iOS gives when the app is left mid-合成, ended when it runs out.
private final class BackgroundTime {
    private var id = UIBackgroundTaskIdentifier.invalid

    init() {
        id = UIApplication.shared.beginBackgroundTask(withName: String(localized: "有声书合成")) { [weak self] in self?.end() }
    }

    func end() {
        guard id != .invalid else { return }
        UIApplication.shared.endBackgroundTask(id)
        id = .invalid
    }
}
#endif
