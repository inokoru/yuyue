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
        // -debugEpisodes 1: two shows and a few written episodes, no model needed — for looking
        // at 播客's screens in a scratch library.
        if d.bool(forKey: "debugEpisodes"), podcasts.shows.isEmpty {
            seedEpisodes()
        }
        if d.bool(forKey: "debugPodcast"), podcasts.shows.isEmpty, let draft = Podcasts.templates().last {
            do {
                let show = try podcasts.add(draft)
                NSLog("debugSeed: show \(show.id), writing")
                podcasts.write(show)
            } catch {
                NSLog("debugSeed: show failed: \(error)")
            }
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

    private func seedEpisodes() {
        let drafts = Podcasts.templates()
        guard let tech = try? podcasts.add(drafts[0]), let oss = try? podcasts.add(drafts[1]) else { return }
        let a = [
            ScriptArticle(title: "Show HN: A tiny SQLite-backed queue", url: "https://news.ycombinator.com/item?id=1", source: "Hacker News"),
            ScriptArticle(title: "Rust 1.91 发布：更快的增量编译", url: "https://blog.rust-lang.org/2026/10/30/Rust-1.91.0.html", source: "Rust Blog"),
            ScriptArticle(title: "我的 macOS 效率工具 2026 版", url: "https://sspai.com/post/1", source: "少数派"),
        ]
        func seg(_ topic: String, _ refs: [UInt32], _ lines: [(String, String)]) -> ScriptSegment {
            ScriptSegment(topic: topic, articles: refs, lines: lines.map { ScriptLine(who: $0.0, text: $0.1) })
        }
        let duo = EpisodeScript(title: "一个文件就是一条队列，Rust 编译又快了", summary: "SQLite 做消息队列靠不靠谱；Rust 1.91 的增量编译；少数派作者的效率工具清单。", articles: a, segments: [
            seg("SQLite 当队列", [0], [("小林", "今天第一条挺有意思，有人用一个 SQLite 文件做了个消息队列。"),
                                     ("老周", "听着像玩具，其实小项目里很够用。一个文件，备份就是复制。"),
                                     ("小林", "评论区也吵起来了，有人说并发一上来就不行。"),
                                     ("老周", "对，写锁是整库的。每秒几百条以内，它比 Redis 还省心。")]),
            seg("Rust 1.91", [1], [("老周", "第二条，Rust 1.91 发布了，增量编译又快了一截。"),
                                   ("小林", "快多少？我每天等编译的时间够泡三杯茶。"),
                                   ("老周", "官方说大项目改一行，重新编译平均少了两成。")]),
            seg("效率工具", [2], [("小林", "最后是少数派的一篇效率工具清单，我挑两个说。"),
                                ("老周", "你先说，我听听有没有我还没装的。")]),
        ])
        let solo = EpisodeScript(title: "rust 1.91.0 发布", summary: "这一版的三处变化，和升级前要注意的一件事。", articles: [a[1]], segments: [
            seg("1.91.0", [0], [("老周", "rust 发布了 1.91.0。"), ("老周", "这一版主要有三处变化。"), ("老周", "第一，增量编译更快。")]),
        ])
        let cal = Calendar.current
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        func day(_ ago: Int) -> String { f.string(from: cal.date(byAdding: .day, value: -ago, to: .now)!) }
        for ago in [0, 1, 2, 5] {
            _ = try? core.saveEpisode(show: tech.id, date: day(ago), script: duo)
        }
        _ = try? core.saveEpisode(show: oss.id, date: day(1), script: solo)
        podcasts.reload()
        if let old = podcasts.episodes(of: tech.id).last { try? core.setEpisodeHeard(book: old.book, heard: true) }
        podcasts.reload()
        reload()
        NSLog("debugSeed: episodes made")
    }
}
#endif

/// A debug argument's address: `~/…` is a file inside the app's own folder (copied onto a phone
/// with `devicectl device copy to`), anything else a URL.
nonisolated func debugURL(_ s: String) -> URL? {
    s.hasPrefix("~/") ? URL(filePath: NSHomeDirectory()).appending(path: String(s.dropFirst(2))) : URL(string: s)
}

