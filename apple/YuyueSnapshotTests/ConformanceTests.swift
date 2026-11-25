import Foundation
import Testing
import YuyueCore

/// Layer 2: Shared UniFFI conformance runner (Apple / Swift side).
/// Executes the exact same `fixtures/conformance/*.json` scenarios as Rust, C#, and Kotlin.
struct ConformanceTests {
    private static func repoRoot() -> URL {
        URL(filePath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    @Test func `shared conformance scenarios pass through Swift UniFFI bindings`() throws {
        let dir = Self.repoRoot().appending(path: "fixtures/conformance")
        let files = try FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" && $0.lastPathComponent != "reader-messages.json" }
            .sorted { $0.lastPathComponent < $1.lastPathComponent }
        #expect(!files.isEmpty)
        for file in files {
            try runScenario(file)
        }
    }

    private func runScenario(_ file: URL) throws {
        let data = try Data(contentsOf: file)
        let scenario = try #require(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let id = try #require(scenario["id"] as? String)
        let steps = try #require(scenario["steps"] as? [[String: Any]])

        let work = FileManager.default.temporaryDirectory
            .appending(path: "yuyue-swift-conformance-\(id)-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: work, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: work) }

        let fixtures = Self.repoRoot().appending(path: "fixtures")
        var cores: [String: YuyueCore] = [:]
        var vars: [String: String] = [:]
        let geom = PageGeometry(paging: .native, margin: 20, top: 50, bottom: 80, columns: 1, reducedMotion: false)

        for (idx, step) in steps.enumerated() {
            let op = try #require(step["op"] as? String)
            switch op {
            case "open":
                let dev = try #require(step["device"] as? String)
                let name = try #require(step["name"] as? String)
                let platform = try #require(step["platform"] as? String)
                let dir = work.appending(path: "dev-\(dev)", directoryHint: .isDirectory)
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                cores[dev] = try YuyueCore.open(dataDir: dir.path(percentEncoded: false), deviceName: name, platform: platform)
            case "close":
                let dev = try #require(step["device"] as? String)
                cores.removeValue(forKey: dev)
            case "import_book":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let rel = try #require(step["fixture"] as? String)
                let full = fixtures.appending(path: rel).path(percentEncoded: false)
                if (step["expect_error"] as? Bool) == true {
                    #expect(throws: Error.self) { _ = try core.importBook(path: full) }
                    continue
                }
                let book = try core.importBook(path: full)
                if let save = step["save_as"] as? String { vars[save] = book.id }
                if let exp = step["expect"] as? [String: Any] {
                    if let fmt = exp["format"] as? String { #expect(book.format == parseFormat(fmt)) }
                    if let avail = exp["available"] as? Bool { #expect(book.available == avail) }
                    if let same = exp["same_id_as"] as? String { #expect(book.id == vars[same]) }
                    if let sub = exp["title_contains"] as? String { #expect(book.title.contains(sub)) }
                }
            case "assert_books":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let books = try core.books()
                if let count = step["count"] as? Int { #expect(books.count == count) }
                if let firstRef = step["first_id_ref"] as? String { #expect(books.first?.id == vars[firstRef]) }
            case "assert_sentences":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                let section = UInt32(try #require(step["section"] as? Int))
                let sents = try core.sentences(bookId: bookId, section: section)
                if let minC = step["min_count"] as? Int { #expect(sents.count >= minC) }
                if let sub = step["any_contains"] as? String { #expect(sents.contains { $0.contains(sub) }) }
            case "save_progress":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                let pos = Position(
                    section: UInt32(try #require(step["section"] as? Int)),
                    sentence: UInt32(try #require(step["sentence"] as? Int))
                )
                try core.saveProgress(bookId: bookId, position: pos)
            case "assert_progress":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                let kind = try #require(step["kind"] as? String)
                let got = kind == "own" ? try core.progress(bookId: bookId) : try core.latestProgress(bookId: bookId)
                let present = try #require(step["present"] as? Bool)
                #expect((got != nil) == present)
                if let p = got {
                    if let sec = step["section"] as? Int { #expect(p.position.section == UInt32(sec)) }
                    if let sen = step["sentence"] as? Int { #expect(p.position.sentence == UInt32(sen)) }
                    if let td = step["this_device"] as? Bool { #expect(p.thisDevice == td) }
                    if let mf = step["min_fraction"] as? Double { #expect(p.fraction >= mf) }
                }
            case "set_reader_settings":
                let core = try #require(cores[step["device"] as? String ?? ""])
                var s = try core.readerSettings()
                if let p = step["paper"] as? String { s.paper = parsePaper(p) }
                if let m = step["mode"] as? String { s.mode = parseMode(m) }
                if let f = step["font"] as? String { s.font = parseFont(f) }
                if let fs = step["font_size"] as? Double { s.fontSize = fs }
                if let c = step["compress"] as? Bool { s.compress = c }
                try core.setReaderSettings(settings: s)
            case "assert_reader_settings":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let s = try core.readerSettings()
                if let p = step["paper"] as? String { #expect(s.paper == parsePaper(p)) }
                if let m = step["mode"] as? String { #expect(s.mode == parseMode(m)) }
                if let f = step["font"] as? String { #expect(s.font == parseFont(f)) }
                if let fs = step["font_size"] as? Double { #expect(abs(s.fontSize - fs) < 1e-6) }
                if let c = step["compress"] as? Bool { #expect(s.compress == c) }
            case "set_book_reader_settings":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                if (step["clear"] as? Bool) == true {
                    try core.setBookReaderSettings(bookId: bookId, settings: nil)
                } else {
                    var s = try core.readerSettings()
                    if let p = step["paper"] as? String { s.paper = parsePaper(p) }
                    if let fs = step["font_size"] as? Double { s.fontSize = fs }
                    try core.setBookReaderSettings(bookId: bookId, settings: s)
                }
            case "assert_section_document":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                let section = UInt32(try #require(step["section"] as? Int))
                let html = try core.sectionDocument(bookId: bookId, section: section, geometry: geom)
                for needle in (step["contains"] as? [String]) ?? [] {
                    #expect(html.contains(needle))
                }
            case "add_mark":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let bookId = try #require(vars[step["book"] as? String ?? ""])
                let mark = try core.addMark(
                    book: bookId,
                    section: UInt32(try #require(step["section"] as? Int)),
                    start: TextPoint(
                        sentence: UInt32(try #require(step["start_sentence"] as? Int)),
                        offset: UInt32(try #require(step["start_offset"] as? Int))
                    ),
                    end: TextPoint(
                        sentence: UInt32(try #require(step["end_sentence"] as? Int)),
                        offset: UInt32(try #require(step["end_offset"] as? Int))
                    ),
                    text: try #require(step["text"] as? String),
                    style: parseStyle(try #require(step["style"] as? String)),
                    note: step["note"] as? String
                )
                if let save = step["save_as"] as? String { vars[save] = mark.id }
            case "set_mark_note":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.setMarkNote(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    id: try #require(vars[step["mark"] as? String ?? ""]),
                    note: step["note"] as? String
                )
            case "set_mark_style":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.setMarkStyle(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    id: try #require(vars[step["mark"] as? String ?? ""]),
                    style: parseStyle(try #require(step["style"] as? String))
                )
            case "assert_marks":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let marks = try core.marks(book: try #require(vars[step["book"] as? String ?? ""]))
                let count = try #require(step["count"] as? Int)
                #expect(marks.count == count)
                if count > 0 {
                    if let fs = step["first_style"] as? String { #expect(marks[0].style == parseStyle(fs)) }
                    if let fn = step["first_note"] as? String { #expect(marks[0].note == fn) }
                }
            case "search_book":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let hits = try core.searchBook(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    query: try #require(step["query"] as? String)
                )
                #expect(hits.count >= (step["min_hits"] as? Int ?? 0))
            case "delete_book":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.deleteBook(id: try #require(vars[step["book"] as? String ?? ""]))
            case "restore_book":
                let core = try #require(cores[step["device"] as? String ?? ""])
                _ = try core.restoreBook(id: try #require(vars[step["book"] as? String ?? ""]))
            case "sleep_ms":
                let ms = try #require(step["ms"] as? Int)
                Thread.sleep(forTimeInterval: Double(ms) / 1000.0)
            case "sync":
                let from = try #require(cores[step["from"] as? String ?? ""])
                let to = try #require(cores[step["to"] as? String ?? ""])
                let full = (step["full"] as? Bool) ?? false
                let ver = full ? "{}" : try to.syncVersion()
                let leftOut = ((step["left_out"] as? [String]) ?? []).map(parseSyncPart)
                let batch = leftOut.isEmpty
                    ? try from.exportChanges(version: ver)
                    : try from.exportChangesExcept(version: ver, leftOut: leftOut)
                let changed = leftOut.isEmpty
                    ? try to.importChanges(batch: batch)
                    : try to.importChangesExcept(batch: batch, leftOut: leftOut)
                if let mc = step["min_changed"] as? Int { #expect(changed >= UInt32(mc)) }
                if let ec = step["exact_changed"] as? Int { #expect(changed == UInt32(ec)) }
            case "assert_conflicts":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let conflicts = try core.noteConflicts()
                let count = try #require(step["count"] as? Int)
                #expect(conflicts.count == count)
                if count > 0 {
                    if let o = step["first_older_note"] as? String { #expect(conflicts[0].older.note == o) }
                    if let n = step["first_newer_note"] as? String { #expect(conflicts[0].newer.note == n) }
                }
            case "resolve_conflict":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.resolveNoteConflict(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    mark: try #require(vars[step["mark"] as? String ?? ""]),
                    choice: parseChoice(try #require(step["choice"] as? String))
                )
            case "make_snapshot":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let snap = work.appending(path: try #require(step["snapshot_name"] as? String)).path(percentEncoded: false)
                let parts = SnapshotParts(
                    progress: try #require(step["progress"] as? Bool),
                    notes: try #require(step["notes"] as? Bool),
                    words: try #require(step["words"] as? Bool),
                    settings: try #require(step["settings"] as? Bool),
                    files: try #require(step["files"] as? Bool)
                )
                let bytes = try core.makeSnapshot(path: snap, parts: parts)
                #expect(bytes > 0)
            case "restore_snapshot":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let snap = work.appending(path: try #require(step["snapshot_name"] as? String)).path(percentEncoded: false)
                _ = try core.restoreSnapshot(path: snap, replace: try #require(step["replace"] as? Bool))
            case "assert_snapshot_counts":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let counts = try core.snapshotCounts()
                if let mb = step["min_books"] as? Int { #expect(counts.books >= UInt32(mb)) }
                if let mn = step["min_notes"] as? Int { #expect(counts.notes >= UInt32(mn)) }
            case "assert_page_sentences":
                let text = try #require(step["text"] as? String)
                let expected = try #require(step["expected_texts"] as? [String])
                let got = pageSentences(text: text).map(\.text)
                #expect(got == expected)
            case "assert_compare_versions":
                for c in (step["cases"] as? [[String: Any]]) ?? [] {
                    let a = try #require(c["a"] as? String)
                    let b = try #require(c["b"] as? String)
                    let sign = try #require(c["sign"] as? Int)
                    let raw = compareVersions(a: a, b: b)
                    let got = raw < 0 ? -1 : (raw > 0 ? 1 : 0)
                    #expect(got == sign)
                }
            case "assert_review_today":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = (step["book"] as? String).flatMap { vars[$0] }
                let dayStart = Int64(try #require(step["day_start"] as? Int))
                let count = UInt32(try #require(step["count"] as? Int))
                let deck = try core.reviewToday(book: book, dayStart: dayStart, count: count)
                if let ei = step["exact_items"] as? Int { #expect(deck.items.count == ei) }
                if let ed = step["exact_done_today"] as? Int { #expect(deck.doneToday == UInt32(ed)) }
            case "set_mark_review":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.setMarkReview(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    id: try #require(vars[step["mark"] as? String ?? ""]),
                    keep: try #require(step["keep"] as? Bool)
                )
            case "add_time":
                let core = try #require(cores[step["device"] as? String ?? ""])
                try core.addTime(
                    book: try #require(vars[step["book"] as? String ?? ""]),
                    section: UInt32(try #require(step["section"] as? Int)),
                    readSeconds: UInt32(try #require(step["read_seconds"] as? Int)),
                    listenSeconds: UInt32(try #require(step["listen_seconds"] as? Int)),
                    day: try #require(step["day"] as? String)
                )
            case "assert_day_times":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let rows = try core.dayTimes(from: try #require(step["from"] as? String), to: try #require(step["to"] as? String))
                let totalR = rows.reduce(0) { $0 + Int($1.readSeconds) }
                let totalL = rows.reduce(0) { $0 + Int($1.listenSeconds) }
                if let me = step["min_entries"] as? Int { #expect(rows.count >= me) }
                if let mr = step["min_read_seconds"] as? Int { #expect(totalR >= mr) }
                if let ml = step["min_listen_seconds"] as? Int { #expect(totalL >= ml) }
            case "bookmark_undo_roundtrip":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let bm = try core.addBookmark(book: book, section: UInt32(try #require(step["section"] as? Int)), sentence: UInt32(try #require(step["sentence"] as? Int)))
                try core.deleteBookmark(book: book, id: bm.id)
                #expect(try core.bookmarks(book: book).isEmpty)
                try core.restoreBookmark(book: book, id: bm.id)
                #expect(try core.bookmarks(book: book).count == 1)
            case "collection_undo_roundtrip":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let col = try core.createCollection(name: try #require(step["name"] as? String), icon: nil, book: book)
                try core.deleteCollection(id: col.id)
                #expect(try !core.collections().contains { $0.id == col.id })
                try core.restoreCollection(id: col.id)
                #expect(try core.collections().contains { $0.id == col.id })
            case "custom_paper_undo_roundtrip":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let paper = try core.saveCustomPaper(paper: CustomPaper(id: "", name: try #require(step["name"] as? String), bg: try #require(step["bg"] as? String), ink: nil, layout: .kami, texture: .none))
                try core.deleteCustomPaper(id: paper.id)
                #expect(try !core.customPapers().contains { $0.id == paper.id })
                try core.restoreCustomPaper(id: paper.id)
                #expect(try core.customPapers().contains { $0.id == paper.id })
            case "word_undo_and_count":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let word = try #require(step["word"] as? String)
                _ = try core.addWord(word: word, phonetic: nil, meaning: "family name", book: book, section: 0, sentence: 0, context: "Mr. Bennet")
                let cnt = try core.bookWordCount(book: book, word: word)
                #expect(Int(cnt) >= (step["min_book_count"] as? Int ?? 0))
                try core.deleteWord(word: word)
                #expect(try !core.words().contains { $0.word.caseInsensitiveCompare(word) == .orderedSame })
                try core.restoreWord(word: word)
                #expect(try core.words().contains { $0.word.caseInsensitiveCompare(word) == .orderedSame })
            case "replace_rule_reorder":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let r1 = try core.saveReplaceRule(rule: ReplaceRule(id: "", book: book, find: "甲", replace: "乙", regex: false, speechOnly: false, enabled: true, order: 1.0))
                let r2 = try core.saveReplaceRule(rule: ReplaceRule(id: "", book: book, find: "丙", replace: "丁", regex: false, speechOnly: false, enabled: true, order: 2.0))
                try core.moveReplaceRule(id: r2.id, order: 0.5)
                let ordered = try core.replaceRules(book: book).sorted { $0.order < $1.order }
                let p2 = try #require(ordered.firstIndex { $0.id == r2.id })
                let p1 = try #require(ordered.firstIndex { $0.id == r1.id })
                #expect(p2 < p1)
            case "set_finished":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let finished = try #require(step["finished"] as? Bool)
                try core.setFinished(book: book, finished: finished)
                let b = try #require(try core.books().first { $0.id == book })
                #expect(b.status == (finished ? .finished : .reading))
            case "keep_record_only":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let book = try #require(vars[step["book"] as? String ?? ""])
                let updated = try core.keepRecordOnly(book: book)
                #expect(updated.record)
                #expect(!updated.available)
            case "purge_deleted":
                let core = try #require(cores[step["device"] as? String ?? ""])
                let purged = try core.purgeDeleted(olderThanMs: Int64(try #require(step["older_than_ms"] as? Int)))
                if let mp = step["min_purged"] as? Int { #expect(Int(purged) >= mp) }
            default:
                Issue.record("\(id)[\(idx)]: unsupported op \(op)")
            }
        }
    }

    private func parsePaper(_ s: String) -> Paper {
        switch s {
        case "plain": .plain
        case "dark": .dark
        case "kami": .kami
        case "kaminight": .kamiNight
        case "sage": .sage
        case "eink": .eink
        default: fatalError("unknown paper \(s)")
        }
    }

    private func parseMode(_ s: String) -> LayoutMode {
        s == "scrolled" ? .scrolled : .paginated
    }

    private func parseFont(_ s: String) -> Font {
        switch s {
        case "sans": .sans
        case "serif": .serif
        case "kai": .kai
        case "wenkai": .wenKai
        default: fatalError("unknown font \(s)")
        }
    }

    private func parseStyle(_ s: String) -> MarkStyle {
        switch s {
        case "yellow": .yellow
        case "blue": .blue
        case "pink": .pink
        case "wave": .wave
        case "underline": .underline
        case "dotted": .dotted
        default: fatalError("unknown style \(s)")
        }
    }

    private func parseFormat(_ s: String) -> BookFormat {
        switch s {
        case "epub": .epub
        case "txt": .txt
        case "pdf": .pdf
        case "mobi": .mobi
        case "audio": .audio
        case "podcast": .podcast
        default: fatalError("unknown format \(s)")
        }
    }

    private func parseSyncPart(_ s: String) -> SyncPart {
        switch s {
        case "reading": .reading
        case "notes": .notes
        case "settings": .settings
        default: fatalError("unknown sync part \(s)")
        }
    }

    private func parseChoice(_ s: String) -> ConflictChoice {
        switch s {
        case "older": .older
        case "newer": .newer
        case "both": .both
        default: fatalError("unknown choice \(s)")
        }
    }
}
