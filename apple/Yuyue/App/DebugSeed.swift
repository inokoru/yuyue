import Foundation
#if DEBUG
import AVFoundation
import YuyueCore

/// Test data made on launch, so a round between two scratch libraries (`-debugLibrary`,
/// `-debugSyncDir`) needs no clicking: the things one device makes for the other to receive.
///
///   -debugAudiobook <audio file> -debugAudiobookBook <part of a shelf book's title>
///       adds the recording and lays it against that book (合成), once.
///   -debugPodcast 1
///       makes the 开源动态 show from its template, if there's no show, and writes an episode.
extension Library {
    func debugSeed() {
        let d = UserDefaults.standard
        // -debugImportDir <folder in Documents>: imports every file in it (test books copied onto
        // a phone), then empties the folder so the next launch doesn't add them again.
        if let dir = d.string(forKey: "debugImportDir") {
            let folder = URL.documentsDirectory.appending(path: dir, directoryHint: .isDirectory)
            let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
            if !files.isEmpty {
                Task {
                    await importFiles(files)
                    for f in files { try? FileManager.default.removeItem(at: f) }
                    if d.bool(forKey: "debugRecords"), !books.contains(where: \.record) { seedRecords() }
                }
            }
        }
        // -debugFetchBook <http url>: downloads a book into a scratch library (the sandbox reads
        // no files outside it) and adds it, once. -debugBubbles 1: every book in 气泡模式.
        if let s = d.string(forKey: "debugFetchBook"), let url = URL(string: s), books.isEmpty {
            Task {
                guard let (data, _) = try? await URLSession.shared.data(from: url) else { return NSLog("debugSeed: fetch failed") }
                let file = URL.temporaryDirectory.appending(path: url.lastPathComponent)
                try? data.write(to: file)
                do {
                    _ = try await Self.importBook(core, path: file.path(percentEncoded: false))
                } catch {
                    return NSLog("prepare: e-book import failed: \(error)")
                }
                reload()
                if d.bool(forKey: "debugBubbles") { for b in books { try? core.setBookBubbles(bookId: b.id, on: true) } }
            }
        }
        if d.bool(forKey: "debugBubbles") {
            for b in books { try? core.setBookBubbles(bookId: b.id, on: true) }
        }
        // -debugRecords 1: 书 filled in — statuses, scores, a line, tags, and two paper books.
        if d.bool(forKey: "debugRecords"), !books.contains(where: \.record) {
            seedRecords()
        }
        // -debugProgress 1: each book being read gets a place a third of the way in.
        if d.bool(forKey: "debugProgress") {
            for book in books where book.status == .reading && book.available && !book.record && book.progress == 0 {
                guard let detail = try? core.book(id: book.id), !detail.sections.isEmpty else { continue }
                let section = UInt32(detail.sections.count / 3)
                let sentences = detail.sections[Int(section)].sentences
                try? core.saveProgress(bookId: book.id, position: Position(section: section, sentence: sentences / 2))
            }
            reload()
        }
        if let path = d.string(forKey: "debugAudiobook"), let title = d.string(forKey: "debugAudiobookBook"),
           let book = books.first(where: { $0.title.contains(title) }),
           audiobooks.audiobook(for: book.id) == nil {
            Task {
                do {
                    let a = try await audiobooks.add([URL(filePath: path)])
                    NSLog("debugSeed: audiobook \(a.id) → \(book.title)")
                    audiobooks.make(a, with: book)
                } catch {
                    NSLog("debugSeed: audiobook failed: \(error)")
                }
            }
        }
        // -debugFakeAudiobook <http wav> -debugFakeTimings <http json> -debugAudiobookBook <title>:
        // a recording already laid against the book with the given timings, no model needed —
        // for looking at 有声书's screens.
        if let wav = d.string(forKey: "debugFakeAudiobook").flatMap(URL.init(string:)),
           let json = d.string(forKey: "debugFakeTimings").flatMap(URL.init(string:)),
           let title = d.string(forKey: "debugAudiobookBook"),
           let book = books.first(where: { $0.title.contains(title) }), audiobooks.audiobook(for: book.id) == nil {
            Task {
                guard let (audio, _) = try? await URLSession.shared.data(from: wav), let (t, _) = try? await URLSession.shared.data(from: json),
                      let timings = try? JSONDecoder().decode(Timings.self, from: t) else { return NSLog("debugSeed: fake audiobook fetch failed") }
                let file = URL.temporaryDirectory.appending(path: wav.lastPathComponent)
                try? audio.write(to: file)
                do {
                    var a = try await audiobooks.add([file])
                    a.book = book.id
                    a.made = .now
                    a.took = 1
                    audiobooks.save(a)
                    audiobooks.save(timings, for: a)
                    NSLog("debugSeed: fake audiobook \(a.id) → \(book.title)")
                } catch {
                    NSLog("debugSeed: fake audiobook failed: \(error)")
                }
            }
        }
        // -debugPrepareBook <http epub> -debugPrepareRecording <http m4b> -debugPrepareTitle <name>
        // -debugPrepareAuthor <name> -debugPrepareEbook <part of the e-book's title>: a real 有声书 and its e-book brought in as 导入 would, the
        // recording named for the shelf, then 合成 — each step once, skipped when already done.
        if let bookURL = d.string(forKey: "debugPrepareBook").flatMap(debugURL),
           let recURL = d.string(forKey: "debugPrepareRecording").flatMap(debugURL),
           let name = d.string(forKey: "debugPrepareTitle") {
            let author = d.string(forKey: "debugPrepareAuthor")
            Task { await prepareAudiobook(book: bookURL, recording: recURL, title: name, author: author) }
        }
        // -debugDramaSynth <part of a shelf book's title> [-debugDramaSection n]: makes that
        // section's clips with the system voices, then writes it mixed to tmp/drama-<n>.wav.
        if let title = d.string(forKey: "debugDramaSynth"), let book = books.first(where: { $0.title.contains(title) && $0.format != .audio }) {
            let section = UInt32(d.integer(forKey: "debugDramaSection"))
            let core = core, dir = dataDir
            let language = (try? core.book(id: book.id).summary.language) ?? "zh-Hans"
            let bookID = book.id
            Task {
                for await note in NotificationCenter.default.notifications(named: DramaSynth.sectionMade) where note.object as? String == bookID {
                    let voices = DramaSynth.voices(for: language, narrator: nil)
                    await Self.verifyDramaSection(core: core, dir: dir, bookID: bookID, section: section, voices: voices)
                    break
                }
            }
            NSLog("debugSeed: drama synth \(book.title) section \(section)")
            DramaSynth.shared.start(core: core, dataDir: dir, book: book.id, language: language, section: section, narrator: nil)
        }
        // -debugTextVersion <recording url>: that recording on the shelf on its own, then 转成文字.
        if let recURL = d.string(forKey: "debugTextVersion").flatMap(debugURL) {
            Task {
                if !books.contains(where: { $0.format == .audio }) {
                    let fetched: URL? = recURL.isFileURL ? recURL : try? await URLSession.shared.download(from: recURL).0
                    guard let tmp = fetched else { return NSLog("textversion: fetch failed") }
                    let dir = URL.temporaryDirectory.appending(path: "tv-\(UUID().uuidString)", directoryHint: .isDirectory)
                    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                    let file = dir.appending(path: recURL.lastPathComponent)
                    try? FileManager.default.copyItem(at: tmp, to: file)
                    if var audio = try? await AudioImporter.read(core, files: [file], folderName: nil) {
                        audio.title = "背影（录音）"
                        _ = try? core.importAudiobook(import: audio)
                    }
                    reload()
                }
                guard let audio = books.first(where: { $0.format == .audio }) else { return NSLog("textversion: no recording") }
                NSLog("textversion: transcribing \(audio.title)")
                audiobooks.makeTextVersion(audio, library: self)
                while audiobooks.textJobs[audio.id] != nil { try? await Task.sleep(for: .seconds(2)) }
                let made = books.first { core.audiobookIsTextVersion(book: $0.id) }
                NSLog("textversion: made \(made.map { "\($0.title) (\($0.id))" } ?? "nothing")")
                if let made, let first = try? core.sentences(bookId: made.id, section: 0) {
                    NSLog("textversion: first sentences: \(first.prefix(6).joined(separator: " | "))")
                }
            }
        }
        // -debugRemoveAudiobooks 1: removes every 有声书 here, as 删除 would.
        if d.bool(forKey: "debugRemoveAudiobooks") {
            for a in audiobooks.all {
                NSLog("debugSeed: removing audiobook \(a.id)")
                audiobooks.remove(a)
            }
        }
        // -debugPodcastDemo 1: populates PodcastHub (正在听 + 书桌待听 + 本周书房周刊 + 报刊栏 + 文稿/边注/原声摘)
        // in a scratch library so every Podcast screen can be visually verified end-to-end.
        if d.bool(forKey: "debugPodcastDemo"), podcastHub.deskIssues.isEmpty {
            seedPodcastHubDemo()
        }
    }

    @concurrent nonisolated private static func verifyDramaSection(core: YuyueCore, dir: URL, bookID: String, section: UInt32, voices: [VoiceInfo]) async {
        let wav = await DramaSynth.mixSection(core, dir, book: bookID, section: section)
        let out = URL.temporaryDirectory.appending(path: "drama-\(section).wav")
        try? wav?.write(to: out)
        NSLog("debugSeed: drama section \(section) mixed → \(out.path(percentEncoded: false)) (\(wav?.count ?? 0) bytes)")
        let n = UInt32((try? core.sentences(bookId: bookID, section: section).count) ?? 0)
        var played: [AVAudioPCMBuffer] = []
        var missing = 0
        for s in 0..<n {
            if let sp = await DramaSynth.sentence(core, dir, book: bookID, section: section, sentence: s) { played.append(sp.buffer) } else { missing += 1 }
        }
        if let joined = Speech.make(from: played) {
            let url = URL.temporaryDirectory.appending(path: "drama-\(section)-player.caf")
            if let f = try? AVAudioFile(forWriting: url, settings: Speech.format.settings, commonFormat: .pcmFormatFloat32, interleaved: false) { try? f.write(from: joined.buffer) }
            NSLog("debugSeed: drama player \(played.count) sentences (\(missing) without), \(Int(joined.seconds)) s → \(url.path(percentEncoded: false))")
        }
        if case let .transcribe(words) = await Transcriber.words(url: out, spans: [TimeSpan(t0: 0, t1: 60)], lang: "zh", offset: 0) {
            NSLog("debugSeed: drama heard: \(words.map { $0.text }.joined())")
        }
        let ds = try? core.dramaSection(bookId: bookID, section: section)
        let plan = try? core.dramaCastVoices(bookId: bookID, voices: voices, narrator: voices.first?.id ?? "")
        let names = Dictionary(((try? core.dramaCast(bookId: bookID, uptoSection: section)) ?? []).map { ($0.id, $0.names.first ?? "") }, uniquingKeysWith: { a, _ in a })
        var byVoice: [String: Set<String>] = [:]
        for seg in ds?.segments ?? [] {
            let who: String = switch seg.speaker {
            case .narrator: "旁白"
            case let .char(id): names[id] ?? "#\(id)"
            default: "对话声"
            }
            let v: String = switch seg.speaker {
            case .narrator: plan?.narrator ?? ""
            case let .char(id): plan?.voices.first { $0.character == id }?.voice ?? plan?.dialogue ?? ""
            default: plan?.dialogue ?? ""
            }
            byVoice[String(v.split(separator: ".").last ?? ""), default: []].insert(who)
        }
        for (v, who) in byVoice.sorted(by: { $0.key < $1.key }) { NSLog("debugSeed: drama voice \(v): \(who.sorted().prefix(8).joined(separator: "、"))") }
        let m4b = URL.temporaryDirectory.appending(path: "drama.m4b")
        let ok = await DramaSynth.exportM4B(core, dir, book: bookID, to: m4b)
        let asset = AVURLAsset(url: m4b)
        let seconds = (try? await asset.load(.duration))?.seconds ?? 0
        let locales = (try? await asset.load(.availableChapterLocales)) ?? []
        let groups = (try? await asset.loadChapterMetadataGroups(withTitleLocale: locales.first ?? .current, containingItemsWithCommonKeys: [.commonKeyTitle])) ?? []
        NSLog("debugSeed: drama m4b \(ok) \(Int(seconds)) s, system chapters \(groups.map { "\(Int($0.timeRange.start.seconds))" }), core marks \(core.audioChapterMarks(path: m4b.path(percentEncoded: false)).map { "\(Int($0.start)) \($0.title ?? "")" })")
    }
}
extension Library {
    private func prepareAudiobook(book bookURL: URL, recording recURL: URL, title: String, author: String?) async {
        let same = { (t: String) in t == title }
        if !books.contains(where: { $0.format != .audio && $0.title.contains(UserDefaults.standard.string(forKey: "debugPrepareEbook") ?? title) }) {
            guard let (data, _) = try? await URLSession.shared.data(from: bookURL) else { return NSLog("prepare: book fetch failed") }
            let file = URL.temporaryDirectory.appending(path: bookURL.lastPathComponent)
            try? data.write(to: file)
            _ = try? await Self.importBook(core, path: file.path)
            reload()
            NSLog("prepare: e-book imported")
        }
        let ebookTitle = UserDefaults.standard.string(forKey: "debugPrepareEbook") ?? title
        guard let ebook = books.first(where: { $0.format != .audio && $0.title.contains(ebookTitle) }) else { return NSLog("prepare: no e-book among \(books.map { "\($0.title)/\($0.format)" })") }
        if !books.contains(where: { $0.format == .audio && same($0.title) }) {
            NSLog("prepare: fetching the recording")
            let fetched: URL? = recURL.isFileURL
                ? { let t = URL.temporaryDirectory.appending(path: UUID().uuidString); return (try? FileManager.default.copyItem(at: recURL, to: t)).map { t } }()
                : try? await URLSession.shared.download(from: recURL).0
            guard let tmp = fetched else { return NSLog("prepare: recording fetch failed") }
            let dir = URL.temporaryDirectory.appending(path: "prepare-\(UUID().uuidString)", directoryHint: .isDirectory)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            let file = dir.appending(path: recURL.lastPathComponent)
            try? FileManager.default.moveItem(at: tmp, to: file)
            do {
                var audio = try await AudioImporter.read(core, files: [file], folderName: nil)
                audio.title = title
                if let author { audio.authors = [author] }
                _ = try core.importAudiobook(import: audio)
                reload()
                NSLog("prepare: recording on the shelf")
            } catch {
                return NSLog("prepare: recording failed: \(error)")
            }
            try? FileManager.default.removeItem(at: dir)
        }
        guard let audio = books.first(where: { $0.format == .audio && same($0.title) }) else { return NSLog("prepare: no recording") }
        if audiobooks.all.first(where: { $0.id == audio.id })?.made == nil, audiobooks.job == nil {
            NSLog("prepare: 合成 \(audio.title) → \(ebook.title)")
            pair(audio, with: ebook)
        }
        while let job = audiobooks.job {
            NSLog("prepare: \(job.step) \(Int(job.fraction * 100))%% \(job.problem ?? "")")
            if job.problem != nil { return }
            try? await Task.sleep(for: .seconds(20))
        }
        NSLog("prepare: done, made \(audiobooks.all.first(where: { $0.id == audio.id })?.made.map { "\($0)" } ?? "no")")
    }

    private func seedRecords() {
        let shelf = books.filter { $0.format != .podcast }
        let plan: [(ReadingStatus, UInt8?, String?, [String])] = [
            (.reading, nil, nil, []),
            (.finished, 5, "父亲爬上月台的那一段，每次读都停在那里。", ["散文", "二刷"]),
            (.paused, nil, nil, []),
            (.want, nil, nil, []),
        ]
        for (book, p) in zip(shelf, plan) {
            if p.0 != book.status { try? core.setStatus(book: book.id, status: p.0) }
            if let r = p.1 { try? core.setRating(book: book.id, rating: r) }
            if let c = p.2 { try? core.setComment(book: book.id, comment: c) }
            for t in p.3 { try? core.setTag(book: book.id, tag: t, on: true) }
        }
        if let paper = try? core.addRecordBook(title: "我与地坛", authors: ["史铁生"], status: .reading) {
            _ = try? core.setManualProgress(book: paper.id, unit: .page, value: 128, total: 320)
            _ = try? core.setManualProgress(book: paper.id, unit: .page, value: 148, total: 320)
            try? core.setRating(book: paper.id, rating: 4)
            try? core.setComment(book: paper.id, comment: "死是一件不必急于求成的事。")
        }
        _ = try? core.addRecordBook(title: "边城", authors: ["沈从文"], status: .want)
        if let done = try? core.addRecordBook(title: "活着", authors: ["余华"], status: .finished) {
            try? core.setRating(book: done.id, rating: 5)
        }
        reload()
    }

    private func seedPodcastHubDemo() {
        // Seed 4 public-domain reading books + 11 episodes + 5 shows + 3 clips + 2 saved-later items
        // for debug preview & verification.
        var book1ID: String?
        var book2ID: String?
        if books.isEmpty {
            if let b1 = try? core.addRecordBook(title: "红楼梦", authors: ["曹雪芹"], status: .reading) {
                _ = try? core.setManualProgress(book: b1.id, unit: .page, value: 156, total: 340)
                book1ID = b1.id
            }
            if let b2 = try? core.addRecordBook(title: "背影", authors: ["朱自清"], status: .reading) {
                _ = try? core.setManualProgress(book: b2.id, unit: .page, value: 98, total: 350)
                book2ID = b2.id
            }
            if let b3 = try? core.addRecordBook(title: "Pride and Prejudice", authors: ["Jane Austen"], status: .reading) {
                _ = try? core.setManualProgress(book: b3.id, unit: .page, value: 48, total: 368)
            }
            if let b4 = try? core.addRecordBook(title: "The Great Gatsby", authors: ["F. Scott Fitzgerald"], status: .reading) {
                _ = try? core.setManualProgress(book: b4.id, unit: .page, value: 4, total: 15)
            }
            reload()
        } else {
            book1ID = books.first?.id
        }

        // 1. Open Source Craft (1 期 · 稍后听)
        if let acq = try? core.podcastImportSingleIssue(
            title: "Local-First Software — Designing SQLite Engines for Offline Reading",
            showTitle: "Open Source Craft",
            summary: "本地优先架构最核心的决定，不是把云端缓存搬到终端，而是让用户的本地数据库成为唯一真实数据源。",
            shownotes: "Deep dive into local-first architecture, embedded SQLite storage, and deterministic sync.",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 3820, language: "en", source: .rss
        ) {
            try? core.podcastSetViewMode(issueId: acq.id, edition: .full, track: .dub)
            try? core.podcastSetTranscript(issueId: acq.id, sentences: [
                MagazineSentence(
                    id: "osc-0", speaker: "Alex Mercer",
                    textOrig: "The defining choice of local-first software is making the database on the user's device the primary source of truth.",
                    textTrans: "本地优先架构最核心的决定，不是把云端缓存搬到终端，而是让用户的本地数据库成为唯一真实数据源。",
                    coachNote: "source of truth：唯一真实数据源；基准事实来源。", t0: 452, t1: 474, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l3CrossLingual)
            try? core.podcastSetPlacement(issueId: acq.id, placement: .archive)
        }

        // 2. 夜间书房 (1 期)
        if let shufang = try? core.podcastImportSingleIssue(
            title: "EP 18 重读朱自清《背影》：现代散文里的克制与白描",
            showTitle: "夜间书房",
            summary: "好的白描从来不需要堆砌形容词，只写买橘子时攀着月台的那几个动作，时间感就自己在纸上立起来了。",
            shownotes: "中文文学慢读，围绕现代散文的白描手法与叙事节奏展开。",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 2580, language: "zh-Hans", source: .rss
        ) {
            try? core.podcastSetTranscript(issueId: shufang.id, sentences: [
                MagazineSentence(
                    id: "sf-0", speaker: "林致",
                    textOrig: "好的白描从来不需要堆砌形容词，只写买橘子时攀着月台的那几个动作，时间感就自己在纸上立起来了。",
                    textTrans: nil, coachNote: nil, t0: 685, t1: 710, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l1LiveTranscript)
        }

        // 3. Classic Literature Desk (2 期: Stoicism + The Silk Road)
        var stoicID: String?
        if let cld2 = try? core.podcastImportSingleIssue(
            title: "Stoicism — Marcus Aurelius, Epictetus & Inner Calm",
            showTitle: "Classic Literature Desk",
            summary: "斯多葛派并不是要压抑情感，而是分辨哪些事在我们的掌控之内，哪些不在。",
            shownotes: "Reading notes on Marcus Aurelius's Meditations and Epictetus.",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 2690, language: "en", source: .rss
        ) {
            stoicID = cld2.id
            try? core.podcastSetTranscript(issueId: cld2.id, sentences: [
                MagazineSentence(
                    id: "cld2-0", speaker: "Clara Evans",
                    textOrig: "Stoicism is not about suppressing emotion; it is about distinguishing what lies within our agency from what does not.",
                    textTrans: "斯多葛派并不是要压抑情感，而是分辨哪些事在我们的掌控之内，哪些不在。",
                    coachNote: "agency：能动性；自主掌控力。", t0: 550, t1: 568, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l2Magazine)
        }

        if let cld1 = try? core.podcastImportSingleIssue(
            title: "The Silk Road — Trade, Manuscripts and Oasis Towns",
            showTitle: "Classic Literature Desk",
            summary: "丝绸之路从来不是一条单一的道路，丝绸也不是穿越中亚绿洲的唯一通货。",
            shownotes: "Historical overview of trade routes, paper manuscripts, and cultural exchange.",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 2775, language: "en", source: .rss
        ) {
            try? core.podcastSetViewMode(issueId: cld1.id, edition: .full, track: .coach)
            try? core.podcastSetTranscript(issueId: cld1.id, sentences: [
                MagazineSentence(
                    id: "cld1-0", speaker: "David Harper",
                    textOrig: "The Silk Road was never a single road, nor was silk the only currency moving across the oases of Central Asia.",
                    textTrans: "丝绸之路从来不是一条单一的道路，丝绸也不是穿越中亚绿洲的唯一通货。",
                    coachNote: "oases：绿洲（oasis 复数）。", t0: 400, t1: 422, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l3CrossLingual)
            try? core.podcastSetPlacement(issueId: cld1.id, placement: .archive)
        }

        // 4. Rust Audio Weekly (2 期未听)
        if let rw1 = try? core.podcastImportSingleIssue(
            title: "Zero-Copy Parsers — Streaming EPUB & ZIP Archives in Rust",
            showTitle: "Rust Audio Weekly",
            summary: "当我们把解析器改造成流式零拷贝架构后，打开几百兆的图文电子书也只需要几毫秒的索引开销。",
            shownotes: "Discussion on streaming archive readers, zero-copy slices, and memory budgets.",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 2500, language: "en", source: .rss
        ) {
            try? core.podcastSetTranscript(issueId: rw1.id, sentences: [
                MagazineSentence(
                    id: "rw1-0", speaker: "Evan Lin",
                    textOrig: "Once we move the parser to a streaming zero-copy pipeline, opening a multi-hundred-megabyte archive takes only milliseconds.",
                    textTrans: "当我们把解析器改造成流式零拷贝架构后，打开几百兆的图文电子书也只需要几毫秒的索引开销。",
                    coachNote: nil, t0: 690, t1: 708, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l2Magazine)
        }

        if let rw2 = try? core.podcastImportSingleIssue(
            title: "On-Device Speech Synthesis — Prosody, Pauses & Latency",
            showTitle: "Rust Audio Weekly",
            summary: "端侧语音合成真正的难点不只是字音准确，而是句与句之间的呼吸停顿与跨语言韵律衔接。",
            shownotes: "Exploring neural TTS pipelines, sentence boundary detection, and low-latency audio rings.",
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 3130, language: "en", source: .rss
        ) {
            try? core.podcastSetViewMode(issueId: rw2.id, edition: .full, track: .dub)
            try? core.podcastSetTranscript(issueId: rw2.id, sentences: [
                MagazineSentence(
                    id: "rw2-0", speaker: "Maya Chen",
                    textOrig: "The hardest part of on-device speech synthesis is not phonetic accuracy alone; it is natural breathing pauses and prosody across clauses.",
                    textTrans: "端侧语音合成真正的难点不只是字音准确，而是句与句之间的呼吸停顿与跨语言韵律衔接。",
                    coachNote: "prosody：韵律；语调与节奏。", t0: 492, t1: 514, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l3CrossLingual)
        }

        // 5. 书房电台 (其余 3 期: 生词伴听 / 难书导读 / 跨书圆桌)
        let vocDraft = ParsedStudioDraft(
            title: "生词伴听 · 凌晨通勤外语台（融入 14 个本周生词）",
            subtitle: "生词本联动 · 14 词",
            summary: "早安。今天的十二分钟通勤故事自然编进了你存下的三个词：fastidious、generalization 和 heuristics。",
            chapters: [],
            sentences: [
                MagazineSentence(
                    id: "sv-0", speaker: "Host · Claire",
                    textOrig: "Good morning. Today's short walk weaves in three words you saved: fastidious, generalization, and heuristics.",
                    textTrans: "早安。今天的十二分钟通勤故事自然编进了你存下的三个词：fastidious、generalization 和 heuristics。",
                    coachNote: "fastidious：挑剔的；对细节一丝不苟的（摘自 Pride and Prejudice）。",
                    t0: 15, t1: 720, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ],
            anchorCount: 0
        )
        _ = try? core.podcastSaveStudioIssue(kind: .vocabRadio, draft: vocDraft, bookIds: [], language: "en")

        let guideDraft = ParsedStudioDraft(
            title: "难书导读：《红楼梦》前五回的草蛇灰线与人物判词",
            subtitle: "结构拆解 · 读前热身",
            summary: "把前五回看作整部小说的总纲：冷子兴演说荣国府搭起了家族骨架，而太虚幻境的判词则悄悄埋下了伏线。",
            chapters: [],
            sentences: [
                MagazineSentence(
                    id: "sg-0", speaker: "林深 · 导读",
                    textOrig: "把前五回看作整部小说的总纲：冷子兴演说荣国府搭起了家族骨架，而太虚幻境的判词则悄悄埋下了伏线。",
                    textTrans: nil, coachNote: nil, t0: 18, t1: 840, skipReason: nil,
                    bookId: book1ID, bookTitle: "红楼梦 · 第二回", bookSection: 2, bookSentence: 2,
                    bookQuote: "满纸荒唐言，一把辛酸泪。"
                )
            ],
            anchorCount: 3
        )
        _ = try? core.podcastSaveStudioIssue(kind: .bookGuide, draft: guideDraft, bookIds: [book1ID].compactMap { $0 }, language: "zh-Hans")

        let rtDraft = ParsedStudioDraft(
            title: "跨书圆桌：古典长篇与现代散文里的日常细节",
            subtitle: "《红楼梦》×《背影》",
            summary: "今天这场圆桌只聊你书架上的两本书：《红楼梦》与朱自清的《背影》。隔着时代，却都在用极细微的日常动作写人。",
            chapters: [
                PodcastChapter(title: "两部作品里的器物与动作", startSec: 0, endSec: 480),
                PodcastChapter(title: "留白与克制：不直说的情绪", startSec: 480, endSec: 960),
            ],
            sentences: [
                MagazineSentence(
                    id: "sr-0", speaker: "林深 · 圆桌主持",
                    textOrig: "今天这场圆桌只聊你书架上的两本书：《红楼梦》与朱自清的《背影》。隔着时代，却都在用极细微的日常动作写人。",
                    textTrans: nil, coachNote: nil, t0: 24, t1: 480, skipReason: nil,
                    bookId: book1ID, bookTitle: "红楼梦", bookSection: 2, bookSentence: 8,
                    bookQuote: "世事洞明皆学问，人情练达即文章。"
                ),
                MagazineSentence(
                    id: "sr-1", speaker: "苏晚 · 书评人声卡",
                    textOrig: "无论是大观园里的一盏茶，还是浦口车站月台上的几个橘子，真正留住读者的往往是具体可感的细节。",
                    textTrans: nil, coachNote: nil, t0: 481, t1: 960, skipReason: nil,
                    bookId: book2ID, bookTitle: "背影", bookSection: 1, bookSentence: 3, bookQuote: nil
                )
            ],
            anchorCount: 2
        )
        _ = try? core.podcastSaveStudioIssue(kind: .roundtable, draft: rtDraft, bookIds: [book1ID, book2ID].compactMap { $0 }, language: "zh-Hans")

        // 6. 正在听 (2 期未完): #14 本周书房周刊 + Rust Audio Weekly (active on right tabletop card)
        var weeklyID: String?
        let wkDraft = ParsedStudioDraft(
            title: "#14 本周书房周刊：重读《红楼梦》与三段夜间划线",
            subtitle: "不剧透锁 · 含 6 条划线",
            summary: "欢迎回到第 14 期书房周刊。这周你一共翻了四本书，但在周三深夜停在《红楼梦》第三回很久。",
            chapters: [
                PodcastChapter(title: "周三深夜的三条划线", startSec: 0, endSec: 250),
                PodcastChapter(title: "从《红楼梦》翻到《背影》", startSec: 250, endSec: 460),
                PodcastChapter(title: "留白：严守已读边界", startSec: 460, endSec: 680),
            ],
            sentences: [
                MagazineSentence(
                    id: "wk-0", speaker: "林深 · 书房主播",
                    textOrig: "欢迎回到第 14 期书房周刊。这周你一共翻了四本书，但在周三深夜停在《红楼梦》第三回很久，划下了三条关于人物出场节奏的句子。",
                    textTrans: nil, coachNote: nil, t0: 12, t1: 250, skipReason: nil,
                    bookId: book1ID, bookTitle: "红楼梦", bookSection: 3, bookSentence: 12,
                    bookQuote: "两弯似蹙非蹙罥烟眉，一双似喜非喜含情目。"
                ),
                MagazineSentence(
                    id: "wk-1", speaker: "苏晚 · 对谈主持",
                    textOrig: "有意思的是，你第二天早上又翻开了《背影》，把月台送别那一段和前晚的笔记连在一起。真正的理解不是记住结论，而是能把同一件事放进另一本书的语境里重说一遍。",
                    textTrans: nil, coachNote: nil, t0: 251, t1: 459, skipReason: nil,
                    bookId: book2ID, bookTitle: "背影", bookSection: 1, bookSentence: 5,
                    bookQuote: "我看见他戴着黑布小帽，穿着黑布大马褂，深青布棉袍，蹒跚地走到铁道边，慢慢探身下去。"
                ),
                MagazineSentence(
                    id: "wk-2", speaker: "林深 · 书房主播",
                    textOrig: "我们严格停在你已经读完的页码之前，后面的章节留给你自己慢慢翻开。",
                    textTrans: nil, coachNote: nil, t0: 460, t1: 680, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ],
            anchorCount: 6
        )
        if let wk = try? core.podcastSaveStudioIssue(kind: .weeklyDigest, draft: wkDraft, bookIds: [book1ID, book2ID].compactMap { $0 }, language: "zh-Hans") {
            weeklyID = wk.id
            try? core.podcastSavePlayback(issueId: wk.id, positionSec: 254, durationSec: 680)
        }

        var activeIssueID: String?
        if let activeIssue = try? core.podcastImportSingleIssue(
            title: "Reasoning, Generalization & Fluid Intelligence in Open Models",
            showTitle: "Rust Audio Weekly",
            summary: "因为智能并不等于你已经背下来的技能，而是你在面对陌生约束时，习得一项新技能的效率。这才是流体智力真正衡量的东西。",
            shownotes: """
            Deep dive into reasoning benchmarks, generalization, and fluid intelligence.
            00:00 寒暄与开场介绍
            01:48 跑分为什么高估了真正的泛化能力
            13:45 流体智力与基准测试的设计哲学
            28:10 程序合成与深度直觉的融合
            41:20 人类学习效率与未来研究方向
            """,
            audioUrl: nil, localAudioPath: nil, artworkUrl: nil,
            durationSec: 3130, language: "en", source: .rss
        ) {
            activeIssueID = activeIssue.id
            try? core.podcastSetViewMode(issueId: activeIssue.id, edition: .full, track: .dub)
            try? core.podcastSetTranscript(issueId: activeIssue.id, sentences: [
                MagazineSentence(
                    id: "ch-ad", speaker: "HOST",
                    textOrig: "Intro note: community newsletter and open benchmark updates.",
                    textTrans: "已静默折叠 1 段片头口播（01:48）",
                    coachNote: nil, t0: 0, t1: 108, skipReason: "ad",
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-intro", speaker: "Evan Lin",
                    textOrig: "Before we dive into reasoning evaluations, what makes benchmark saturation such a recurring trap in model research?",
                    textTrans: "在深入聊推理评测之前，为什么“跑分饱和”会成为模型研究里反复出现的陷阱？",
                    coachNote: "saturation：饱和度；指基准测试分数很快刷满却不代表能力真正突破。",
                    t0: 112, t1: 148, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-0", speaker: "Evan Lin",
                    textOrig: "When people look at models solving competition math, why do we still argue that benchmark scores overstate true generalization?",
                    textTrans: "当大家看到模型已经能解竞赛数学题时，为什么我们依然认为跑分高估了真正的泛化能力？",
                    coachNote: "generalization：泛化能力，把已有经验迁移到全新未见过问题的能力。",
                    t0: 828, t1: 861, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-1", speaker: "Maya Chen",
                    textOrig: "Because intelligence is not the skill you already memorized — it is the efficiency with which you acquire a new skill under novel constraints. That is what fluid intelligence measures.",
                    textTrans: "因为智能并不等于你已经背下来的技能，而是你在面对陌生约束时，习得一项新技能的效率。这才是流体智力真正衡量的东西。",
                    coachNote: "fluid intelligence：流体智力，不依赖既有知识储备、现场推理解决陌生问题的能力。",
                    t0: 862, t1: 904, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-2", speaker: "Maya Chen",
                    textOrig: "If a system relies purely on memorized heuristics from pre-training, it looks brilliant inside the manifold and suddenly fragile one step outside it.",
                    textTrans: "如果一个系统纯粹依赖预训练记住的启发式套路，它在分布内会显得极其聪明，但只要跨出边界一步，就会突然变得非常脆弱。",
                    coachNote: "heuristics：启发式方法，凭经验快速判断的捷径规则。",
                    t0: 905, t1: 952, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-3", speaker: "Maya Chen",
                    textOrig: "Deep learning gives us intuition — a continuous geometry over past experience — while discrete program search lets you recombine primitives on the fly.",
                    textTrans: "深度学习赋予我们的是直觉——基于过往经验的连续几何流形；而离散程序搜索，才允许你在现场把基本原语重新组合。",
                    coachNote: "primitives：基本原语；可组合的最小操作单元。",
                    t0: 1695, t1: 1742, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                ),
                MagazineSentence(
                    id: "ch-4", speaker: "Evan Lin",
                    textOrig: "So the next leap isn't just bigger pre-training corpora, it's test-time adaptation guided by intuition.",
                    textTrans: "所以下一次跃迁不只是更大的预训练语料，而是在直觉引导下的测试时现场适应。",
                    coachNote: "test-time adaptation：测试时适应；面对新题实时搜索与修正推理路径。",
                    t0: 2490, t1: 2536, skipReason: nil,
                    bookId: nil, bookTitle: nil, bookSection: nil, bookSentence: nil, bookQuote: nil
                )
            ], fidelity: .l3CrossLingual)
            try? core.podcastSavePlayback(issueId: activeIssue.id, positionSec: 862, durationSec: 3130)
            _ = try? core.podcastAddInterrupt(
                issueId: activeIssue.id, atSec: 862,
                question: "这里说的流体智力和预训练记忆的区别，用一句话怎么概括？",
                answer: "预训练记住的是见过的招式，流体智力是在陌生规则下现场发明新招式的效率。"
            )
        }

        // 7. 3 原声书摘 Clips (oldest first so #14 书房周刊 clip appears first under the greeting)
        if let stoicID {
            _ = try? core.podcastAddClip(
                issueId: stoicID, t0: 550, t1: 556.2, speaker: "Clara Evans",
                quoteText: "Stoicism is not about suppressing emotion; it is about distinguishing what lies within our agency.",
                thought: nil, audioRelPath: nil
            )
        }
        if let activeIssueID {
            _ = try? core.podcastAddClip(
                issueId: activeIssueID, t0: 862, t1: 870.4, speaker: "Maya Chen",
                quoteText: "智能并不等于你已经背下来的技能，而是面对陌生约束时习得新技能的效率。",
                thought: "原声片段永久留存，整集音频可随时清理。", audioRelPath: nil
            )
        }
        if let weeklyID {
            _ = try? core.podcastAddClip(
                issueId: weeklyID, t0: 254, t1: 261.8, speaker: "苏晚 · 对谈主持",
                quoteText: "真正的理解不是记住结论，而是能把同一件事放进另一本书的语境里重说一遍。",
                thought: "和《背影》批注合在一起看。", audioRelPath: nil
            )
        }

        podcastHub.reload()
        NSLog("debugSeed: podcastHub demo seeded (11 issues, 3 clips)")
    }
}
#endif

/// A debug argument's address: `~/…` is a file inside the app's own folder (copied onto a phone
/// with `devicectl device copy to`), anything else a URL.
nonisolated func debugURL(_ s: String) -> URL? {
    s.hasPrefix("~/") ? URL(filePath: NSHomeDirectory()).appending(path: String(s.dropFirst(2))) : URL(string: s)
}

