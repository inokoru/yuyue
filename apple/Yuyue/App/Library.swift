import Foundation
import ImageIO
import Observation
import UniformTypeIdentifiers
import YuyueCore
#if canImport(UIKit)
import UIKit
#endif

/// A book the reader asked for that cannot be opened. The card stays on the current window.
struct BlockedBook: Equatable {
    var bookID: String
    var title: String
    var message: String
    /// The file may just have moved: offer 重新导入 as well as dismissing.
    var reimport: Bool
}

extension BlockedBook {
    /// A failure reported by a window that already opened, worded as the core said it.
    init(message: String) {
        let missing = message.contains("找不到这本书的文件")
        let permission = message.contains("没有权限")
        let title = missing ? String(localized: "找不到这本书的文件")
            : (permission ? String(localized: "读不到这个文件") : String(localized: "这本书打不开"))
        self.init(bookID: "", title: title, message: Self.rest(message, after: title), reimport: missing || permission)
    }

    init(bookID: String, error: Error) {
        let full = Library.message(for: error)
        let title: String
        let reimport: Bool
        if let error = error as? CoreError {
            switch error {
            case .NotFound:
                title = String(localized: "找不到这本书的文件")
                reimport = true
            case .Io(let detail) where detail.contains("os error 1)") || detail.contains("os error 13)"):
                title = String(localized: "读不到这个文件")
                reimport = true
            case .BadBook(let detail) where detail.contains("too large"):
                title = String(localized: "这本书太大了")
                reimport = false
            case .Unsupported:
                title = String(localized: "这种文件打不开")
                reimport = false
            default:
                title = String(localized: "这本书打不开")
                reimport = false
            }
        } else {
            title = String(localized: "这本书打不开")
            reimport = false
        }
        self.init(bookID: bookID, title: title, message: Self.rest(full, after: title), reimport: reimport)
    }

    /// The sentence after a title that already begins the core's message.
    private static func rest(_ message: String, after title: String) -> String {
        guard message.hasPrefix(title) else { return message }
        let rest = message.dropFirst(title.count).trimmingCharacters(in: CharacterSet(charactersIn: "，,。 "))
        return rest.isEmpty ? message : String(rest)
    }
}

/// The app's handle on the core (one per process) and the shelf it shows.
@Observable
final class Library {
    let core: YuyueCore
    private(set) var books: [BookSummary] = []
    /// What the shelf shows: books and 有声书. 播客's episodes live on its own page; books kept
    /// only as a record (纸书, 只删了文件) live in 书.
    var shelfBooks: [BookSummary] { books.filter { $0.format != .podcast && !$0.record } }
    /// 书: every book the reader keeps a record of, files or not.
    var keptBooks: [BookSummary] { books.filter { $0.format != .podcast } }
    private(set) var collections: [BookCollection] = []
    private(set) var importing = 0
    /// 正在导入: on the Mac, each file being brought in, one row each, until
    /// they've settled and the card folds away.
    private(set) var imports: [ImportFile] = []
    @ObservationIgnored private var importsStarted = Date()
    /// When a file on the card last changed: files opened together from 访达 come one by one,
    /// a moment apart, and belong on one card.
    @ObservationIgnored private var importsTouched = Date.distantPast
    var alert: String?
    /// A book that could not be opened. Shown on the window the reader is already in,
    /// rather than a reader window whose only job is to say so.
    var blocked: BlockedBook?
    /// `core.book` already run for an open that is about to happen, so the reader does not build it again.
    /// Only good for a moment: a reader built later (after 编码和目录, say) must build the book afresh.
    @ObservationIgnored private var primed: (id: String, detail: BookDetail, at: Date)?

    /// The detail `open` just built for this book, taken once. Nil when stale or for another book.
    func takePrimedDetail(_ bookID: String) -> BookDetail? {
        defer { primed = nil }
        guard let primed, primed.id == bookID, Date().timeIntervalSince(primed.at) < 3 else { return nil }
        return primed.detail
    }
    /// A 数据快照 opened from 文件 or dropped in, waiting to be looked at before it's brought back.
    var openedSnapshot: URL?
    /// The note at the bottom of the window after deleting or importing, with its undo.
    private(set) var toast: Toast?
    /// The book being read aloud, in whichever window.
    var listening: String? {
        didSet { keepAwake(listening != nil) }
    }
    @ObservationIgnored private var awake: NSObjectProtocol?

    @ObservationIgnored private let booksDir: URL
    /// Where the library lives: the database and the books' files.
    @ObservationIgnored let dataDir: URL
    let sync: Sync
    /// 远程书库 filled in under 书库来源.
    let sources = RemoteSources()
    /// 监听一个文件夹.
    let watched = WatchedFolder()
    /// 有声书.
    let audiobooks: Audiobooks
    /// 有声杂志 + 私人书房电台.
    let podcastHub: PodcastHub
    #if os(macOS)
    /// 语阅 in the menu bar while listening or running 声音工坊.
    @ObservationIgnored private var menuBar: MacMenuBar?
    #endif
    @ObservationIgnored private var madeNoteSender: NoteSender?
    /// 笔记 › 导出's sends, told in the library's notes.
    var noteSender: NoteSender {
        if let s = madeNoteSender { return s }
        let s = NoteSender(core: core) { [weak self] t in self?.note(icon: t.icon, t.title, t.detail) }
        madeNoteSender = s
        return s
    }
    /// What to do when a book's window opens: from the shelf's 听 / 从头听.
    @ObservationIgnored private var intents: [String: OpenIntent] = [:]

    init() throws {
        var dir = try FileManager.default
            .url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
            .appending(path: "Yuyue", directoryHint: .isDirectory)
        #if DEBUG
        // `-debugLibrary <dir>` opens a scratch library instead of the real one.
        // `~/…` is inside the app's own folder (an iPhone's moves with each install).
        if let other = UserDefaults.standard.string(forKey: "debugLibrary") {
            dir = other.hasPrefix("~/") ? URL(filePath: NSHomeDirectory()).appending(path: String(other.dropFirst(2)), directoryHint: .isDirectory) : URL(filePath: other, directoryHint: .isDirectory)
        }
        #endif
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        // The core's own words (errors, 常用规则, a chapter's 读/听 line) in the interface's language.
        setUiLanguage(language: Bundle.main.preferredLocalizations.first ?? "zh-Hans")
        core = try YuyueCore.open(dataDir: dir.path(percentEncoded: false), deviceName: Self.deviceName, platform: Self.platform)
        booksDir = dir.appending(path: "books", directoryHint: .isDirectory)
        dataDir = dir
        LiveVoiceModel.attach(core: core, dataDir: dir)
        sync = Sync(core: core)
        FontShelf.shared.attach(fontsDir: core.fontsDir())
        audiobooks = Audiobooks(core: core, dataDir: dir)
        Audiobooks.shared = audiobooks
        podcastHub = PodcastHub(core: core, dataDir: dir)
        #if os(macOS)
        #if DEBUG
        // `-debugSynth 1`: say one line with the default narrator card and log what came back.
        if UserDefaults.standard.bool(forKey: "debugSynth") {
            Task { @MainActor in
                try? await Task.sleep(for: .seconds(2))
                guard let v = VoicePack.defaultNarrator(for: "zh") else { return NSLog("debugSynth: 没有默认旁白") }
                do {
                    let sp = try await v.render("我买几个橘子去。你就在此地，不要走动。", language: "zh")
                    NSLog("debugSynth: \(v.name) → \(sp == nil ? "nil" : "ok")")
                } catch {
                    NSLog("debugSynth: \(v.name) 出错 \(String(reflecting: error))")
                }
            }
        }
        #endif
        menuBar = MacMenuBar(library: self)
        #endif
        reload()
        // Deferred post-launch maintenance & Bonjour discovery so cold-start Frame 1 renders immediately.
        let coreRef = core
        let dataDirRef = dir
        Task { @MainActor in
            try? await Task.sleep(for: .milliseconds(350))
            ShareStream.shared.resume(core: coreRef)
            #if os(macOS)
            RecordingServer.shared.start(core: coreRef, dataDir: dataDirRef)
            #else
            RecordingFetch.shared.browse()
            #endif
            try? await Task.sleep(for: .seconds(3))
            await Self.purgeDeletedInBackground(coreRef)
        }
        sync.changed = { [weak self] in
            if let self { LiveVoiceModel.register(self.core, self.dataDir) }
            NotificationCenter.default.post(name: ClonedVoices.changed, object: nil)
            self?.reload()
            self?.audiobooks.reload()
            self?.podcastHub.reload(clearCache: true)
        }
        #if DEBUG
        debugSeed()
        #endif
    }

    /// 听书时 Mac 不睡着 (设置 · 通用).
    private func keepAwake(_ on: Bool) {
        if on, awake == nil, UserDefaults.standard.object(forKey: "general.keepAwake") as? Bool ?? true {
            awake = ProcessInfo.processInfo.beginActivity(options: [.idleSystemSleepDisabled], reason: String(localized: "听书"))
        } else if !on, let awake {
            ProcessInfo.processInfo.endActivity(awake)
            self.awake = nil
        }
    }

    func reload() {
        do {
            books = try core.books()
            collections = try core.collections()
            Papers.shared.load(core)
        } catch {
            alert = Self.message(for: error)
        }
    }

    @concurrent nonisolated private static func purgeDeletedInBackground(_ core: YuyueCore) async {
        _ = try? core.purgeDeleted(olderThanMs: 30 * 24 * 3600 * 1000)
    }

    // MARK: Opening

    enum OpenIntent: Equatable {
        case listen, listenFromStart
        /// From 笔记 or 搜索: open at this place, optionally highlighting a search hit.
        case at(Position, query: String? = nil, hit: SearchHit? = nil)
        /// From 搜索 · 问: open with 书伴 and this question.
        case ask(String)
    }

    /// False when the book cannot be opened: `blocked` is set and nothing is presented.
    @discardableResult
    func open(_ book: BookSummary, _ intent: OpenIntent?) -> Bool {
        if let blocked = refusal(of: book) {
            self.blocked = blocked
            return false
        }
        intents[book.id] = intent
        #if os(iOS)
        presented = book.id
        #endif
        return true
    }

    /// Opens a book at a place: a window opening takes it as its intent, one already open
    /// hears it (`readerJump`). False when the book cannot be opened.
    @discardableResult
    func open(_ bookID: String, at position: Position, query: String? = nil, hit: SearchHit? = nil) -> Bool {
        guard let book = books.first(where: { $0.id == bookID }) else { return false }
        guard open(book, .at(position, query: query, hit: hit)) else { return false }
        NotificationCenter.default.post(name: .readerJump, object: bookID)
        return true
    }

    /// Hands a question to the book's 书伴, in a window open already or one about to open.
    /// False when that book cannot be opened.
    @discardableResult
    func ask(_ question: String, in bookID: String) -> Bool {
        guard let book = books.first(where: { $0.id == bookID }) else { return false }
        guard open(book, .ask(question)) else { return false }
        NotificationCenter.default.post(name: .readerJump, object: bookID)
        return true
    }

    /// The book is already in a reader: a window on the Mac, the presented one on the phone.
    private func isShowing(_ bookID: String) -> Bool {
        #if os(macOS)
        OneWindowPerBook.hasWindow(bookID)
        #else
        presented == bookID
        #endif
    }

    /// A reader window that found out, only after it existed, that the book will not open.
    func noteUnopened(_ message: String) {
        blocked = BlockedBook(message: message)
    }

    /// Why this book should not get a window. Audio always opens; a PDF is checked on disk,
    /// everything else by asking the core to build it.
    private func refusal(of book: BookSummary) -> BlockedBook? {
        // Already on screen: it opened, and a jump in it needs no second build.
        if isShowing(book.id) { return nil }
        switch book.format {
        case .audio: return nil
        case .pdf: return PdfModel.refusal(book.id, url: originalFile(of: book))
        default:
            do {
                primed = (book.id, try core.book(id: book.id), Date())
                return nil
            } catch {
                return BlockedBook(bookID: book.id, error: error)
            }
        }
    }

    #if os(iOS)
    /// The book over the tabs on the phone; opening one puts it there.
    var presented: String?
    #endif
    /// A book still being read aloud after its pages were closed: the 播放条 over the tabs
    /// plays and pauses it, and opening the book again picks it up as it is. On the Mac, the
    /// episode the 播客 播放条 plays, its window open or not. One voice at a time: a new one
    /// stops the old.
    var nowPlaying: (any NowPlaying)? {
        didSet {
            if nowPlaying != nil, podcastHub.isPlaying {
                podcastHub.pause()
            }
            guard let oldValue, nowPlaying != nil, oldValue !== nowPlaying else { return }
            #if os(macOS)
            // Still in its window: it only goes quiet there; the window keeps its player.
            if let m = oldValue as? ReaderModel, openModels.contains(m) {
                m.player?.pause()
                return
            }
            #endif
            oldValue.stopAndClose()
        }
    }
    #if os(macOS)
    /// Each open book window's model.
    @ObservationIgnored let openModels = NSHashTable<ReaderModel>.weakObjects()
    #endif

    func takeIntent(for bookID: String) -> OpenIntent? {
        intents.removeValue(forKey: bookID)
    }

    /// The imported file, for 在访达中显示.
    func originalFile(of book: BookSummary) -> URL? {
        let dir = booksDir.appending(path: book.id, directoryHint: .isDirectory)
        let files = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        return files.first { $0.lastPathComponent.hasPrefix("original.") }
    }

    // MARK: Organising

    func setFinished(_ book: BookSummary, _ finished: Bool) {
        run { try core.setFinished(book: book.id, finished: finished) }
    }

    // MARK: 书的记录

    func setStatus(_ book: BookSummary, _ status: ReadingStatus) {
        run { try core.setStatus(book: book.id, status: status) }
    }

    func setRating(_ book: BookSummary, _ rating: UInt8?) {
        run { try core.setRating(book: book.id, rating: rating) }
    }

    func setComment(_ book: BookSummary, _ comment: String) {
        run { try core.setComment(book: book.id, comment: comment) }
    }

    func setTag(_ book: BookSummary, _ tag: String, _ on: Bool) {
        run { try core.setTag(book: book.id, tag: tag, on: on) }
    }

    var allTags: [String] { (try? core.allTags()) ?? [] }

    /// 加一本书: only the title is needed. Returns the new book.
    @discardableResult
    func addRecordBook(_ title: String, status: ReadingStatus) -> BookSummary? {
        var made: BookSummary?
        run { made = try core.addRecordBook(title: title, authors: [], status: status) }
        return made
    }

    func setManualProgress(_ book: BookSummary, _ unit: ProgressUnit, value: UInt32, total: UInt32) {
        run { _ = try core.setManualProgress(book: book.id, unit: unit, value: value, total: total) }
    }

    /// “+20 页”.
    func stepManualProgress(_ book: BookSummary) {
        run { _ = try core.stepManualProgress(book: book.id) }
    }

    /// 只删文件，留下记录.
    func keepRecordOnly(_ book: BookSummary) {
        closeOpenBook(book.id)
        run { _ = try core.keepRecordOnly(book: book.id) }
    }

    private func closeOpenBook(_ id: String) {
        if nowPlaying?.bookID == id {
            nowPlaying?.abandon()
            nowPlaying = nil
        }
        if listening == id { listening = nil }
        NotificationCenter.default.post(name: .bookRemoved, object: id)
    }

    func book(_ id: String) -> BookSummary? { books.first { $0.id == id } }

    /// Where a book stands chapter by chapter, for its card on 进度: worked out once per
    /// position, since it reads the book's contents.
    @ObservationIgnored private var chapterCache: [String: (Double, [ChapterRow])] = [:]

    func chapters(of book: BookSummary) -> [ChapterRow] {
        guard book.available, !book.record, book.format != .audio, book.format != .podcast else { return [] }
        if let (at, rows) = chapterCache[book.id], at == book.progress { return rows }
        guard let detail = try? core.book(id: book.id) else { return [] }
        let position = (try? core.latestProgress(bookId: book.id))?.position
        let rows = Chapters.rows(detail, at: position)
        chapterCache[book.id] = (book.progress, rows)
        return rows
    }

    /// How long it has been read and listened to, in minutes.
    func timeSpent(on book: BookSummary) -> (read: Int, listen: Int) {
        let times = (try? core.sectionTimes(book: book.id)) ?? []
        let read = times.reduce(0) { $0 + Int($1.readSeconds) }
        let listen = times.reduce(0) { $0 + Int($1.listenSeconds) }
        return (read / 60, listen / 60)
    }

    func noteCount(of book: BookSummary) -> Int {
        book.record ? 0 : ((try? core.marks(book: book.id).count) ?? 0)
    }

    func setBook(_ book: String, in collection: String, _ inside: Bool) {
        run { try core.setInCollection(collection: collection, book: book, inside: inside) }
    }

    /// Makes a collection, with the book in it when given. Returns its id.
    @discardableResult
    func createCollection(_ name: String, with book: String? = nil) -> String? {
        var id: String?
        run { id = try core.createCollection(name: name, icon: CollectionIcon.guess(for: name), book: book).id }
        return id
    }

    func renameCollection(_ id: String, to name: String) {
        run { try core.renameCollection(id: id, name: name) }
    }

    /// 换图标: nil goes back to the icon its name brings.
    func setCollectionIcon(_ id: String, _ symbol: String?) {
        run { try core.setCollectionIcon(id: id, icon: symbol) }
    }

    func deleteCollection(_ collection: BookCollection) {
        run { try core.deleteCollection(id: collection.id) }
        show(Toast(icon: "trash", danger: true, title: String(localized: "已删除合集「\(collection.name)」"), detail: String(localized: "里面的书都还在")) { [weak self] in
            self?.run { try self?.core.restoreCollection(id: collection.id) }
        })
    }

    /// 合集 dragged into a new order.
    func reorderCollections(_ ids: [String]) {
        run { try core.reorderCollections(ids: ids) }
    }

    func editInfo(_ book: BookSummary, title: String, authors: [String]) {
        run { _ = try core.editBookInfo(book: book.id, title: title, authors: authors) }
    }

    /// 换封面: the picked picture, as a JPEG no bigger than a cover needs.
    func setCover(_ book: BookSummary, image: Data) {
        run {
            guard let jpeg = Self.coverJPEG(image) else { throw CocoaError(.fileReadCorruptFile) }
            _ = try core.setCover(bookId: book.id, image: jpeg)
        }
    }

    nonisolated static func coverJPEG(_ data: Data, longest: Int = 1200) -> Data? {
        guard let source = CGImageSourceCreateWithData(data as CFData, nil),
              let image = CGImageSourceCreateThumbnailAtIndex(source, 0, [
                  kCGImageSourceCreateThumbnailFromImageAlways: true,
                  kCGImageSourceCreateThumbnailWithTransform: true,
                  kCGImageSourceThumbnailMaxPixelSize: longest,
              ] as CFDictionary) else { return nil }
        let out = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(out, UTType.jpeg.identifier as CFString, 1, nil) else { return nil }
        CGImageDestinationAddImage(dest, image, [kCGImageDestinationLossyCompressionQuality: 0.85] as CFDictionary)
        return CGImageDestinationFinalize(dest) ? out as Data : nil
    }

    private func run(_ change: () throws -> Void) {
        do {
            try change()
        } catch {
            alert = Self.message(for: error)
        }
        reload()
    }

    // MARK: The note at the bottom

    struct Toast: Identifiable {
        let id = UUID()
        var icon: String
        var danger = false
        var title: String
        var detail: String
        var undo: (() -> Void)?
    }

    /// How long a plain note stays.
    static let toastSeconds = 3.0
    /// The seconds a deletion can be undone, counted down on its banner.
    static let undoSeconds = 5.0
    /// Until a note goes: a banner counts its seconds, shows the red tick a moment, then rises away.
    static func lifetime(of toast: Toast) -> Double { toast.undo == nil ? toastSeconds : undoSeconds + 1.2 }

    /// Waits for `toast` to finish showing, pausing an undoable banner while hovered on Mac.
    static func waitToast(_ toast: Toast) async {
        if toast.undo == nil {
            try? await Task.sleep(for: .seconds(toastSeconds))
            return
        }
        var remaining = undoSeconds
        while remaining > 0, !Task.isCancelled {
            try? await Task.sleep(for: .milliseconds(50))
            if UndoToast.hoveredID != toast.id { remaining -= 0.05 }
        }
        if !Task.isCancelled {
            try? await Task.sleep(for: .seconds(1.2))
        }
    }

    /// A note without undo.
    func note(icon: String, _ title: String, _ detail: String) {
        show(Toast(icon: icon, title: title, detail: detail))
    }

    private func show(_ toast: Toast) {
        self.toast = toast
        Task { [weak self] in
            await Self.waitToast(toast)
            if self?.toast?.id == toast.id { self?.toast = nil }
        }
    }

    #if DEBUG
    /// A delete note that puts nothing back, to look at it; it stays until something replaces it.
    func debugToast() {
        toast = Toast(icon: "trash", danger: true, title: String(localized: "已删除《三体》"), detail: String(localized: "笔记和进度都留着")) {}
    }
    #endif

    /// Takes a word out of the 生词本, with 撤销.
    func deleteWord(_ word: String) {
        do {
            try core.deleteWord(word: word)
        } catch {
            alert = Self.message(for: error)
            return
        }
        NotificationCenter.default.post(name: .vocabChanged, object: nil)
        show(Toast(icon: "trash", danger: true, title: String(localized: "已删除“\(word)”"), detail: String(localized: "从生词本里拿掉了")) { [weak self] in
            try? self?.core.restoreWord(word: word)
            NotificationCenter.default.post(name: .vocabChanged, object: nil)
        })
    }

    /// Deletes highlights outside the book (笔记), with 撤销. Each is (book, mark).
    func deleteMarks(_ marks: [(String, Mark)]) {
        guard !marks.isEmpty else { return }
        do {
            for (book, mark) in marks { try core.deleteMark(book: book, id: mark.id) }
        } catch {
            alert = Self.message(for: error)
        }
        NotificationCenter.default.post(name: .notesChanged, object: nil)
        let one = marks.count == 1 ? marks[0].1 : nil
        let detail = one.map { $0.note == nil ? "“\($0.text.prefix(16))”" : String(localized: "想法也一起删了") } ?? String(localized: "\(marks.count) 条划线和它们的想法")
        show(Toast(icon: "highlighter", danger: true, title: one == nil ? String(localized: "已删除 \(marks.count) 条") : String(localized: "已删除划线"), detail: detail) { [weak self] in
            for (book, mark) in marks { try? self?.core.restoreMark(book: book, id: mark.id) }
            NotificationCenter.default.post(name: .notesChanged, object: nil)
        })
    }

    /// Writes or clears a highlight's thought outside the book.
    func setNote(_ note: String?, book: String, mark: Mark) {
        do {
            try core.setMarkNote(book: book, id: mark.id, note: note)
        } catch {
            alert = Self.message(for: error)
        }
        NotificationCenter.default.post(name: .notesChanged, object: nil)
    }

    func undo() {
        guard let toast else { return }
        self.toast = nil
        toast.undo?()
    }

    /// What 导入 can pick: books, or a folder of them.
    static let importTypes: [UTType] = [.epub, .yuyueSnapshot, .pdf, .plainText, .folder, .audio, .zip] + ["mobi", "azw3", "azw", "prc", "aax", "aa"].compactMap { UTType(filenameExtension: $0) }

    /// Imports the files, into the collection when given; `folder` names the watched folder
    /// they came from.
    func importFiles(_ urls: [URL], into collection: String? = nil, from folder: String? = nil) async {
        importing += 1
        defer { importing -= 1 }
        // A folder of books brings in each of them.
        let folders = urls.filter { $0.hasDirectoryPath && $0.startAccessingSecurityScopedResource() }
        defer { folders.forEach { $0.stopAccessingSecurityScopedResource() } }
        // A zip of sound files is a recording: taken out, then brought in as a folder would be.
        var zipped: [[URL]] = []
        var incoming = urls
        for z in urls where z.pathExtension.lowercased() == "zip" {
            let scoped = z.startAccessingSecurityScopedResource()
            defer { if scoped { z.stopAccessingSecurityScopedResource() } }
            // Into a folder named for the zip: the recording takes its name from it.
            let into = FileManager.default.temporaryDirectory.appending(path: "unzip-\(UUID().uuidString)", directoryHint: .isDirectory)
                .appending(path: z.deletingPathExtension().lastPathComponent, directoryHint: .isDirectory)
            let files = await Self.unzipAudiobook(core, zip: z.path(percentEncoded: false), into: into.path(percentEncoded: false))
            if !files.isEmpty {
                zipped.append(files.map { URL(filePath: $0) })
                incoming.removeAll { $0 == z }
            }
        }
        // 有声书 go onto the shelf on their own.
        let (found, rest) = Self.recordings(in: incoming)
        let recordings = found + zipped
        defer {
            // After the books, so they come first on the shelf.
            if !recordings.isEmpty {
                Task { for files in recordings { await importRecording(files) } }
            }
        }
        let urls = rest.flatMap(Self.booksInside)
        var failures: [String] = []
        var imported: [BookSummary] = []
        let place = collection.flatMap { id in collections.first { $0.id == id }?.name } ?? String(localized: "全部")
        var rows: [UUID: URL] = [:]
        #if os(macOS)
        if imports.allSatisfy(\.settled), Date.now.timeIntervalSince(importsTouched) > 2 {
            imports = []
            importsStarted = .now
        }
        for url in urls where url.pathExtension.lowercased() != Snapshots.fileExtension {
            let file = ImportFile(name: url.lastPathComponent)
            rows[file.id] = url
            imports.append(file)
            importsTouched = .now
        }
        #endif
        for url in urls {
            if url.pathExtension.lowercased() == Snapshots.fileExtension {
                openedSnapshot = url
                continue
            }
            let row = rows.first { $0.value == url }?.key
            let had = Set(books.map(\.id))
            setImport(row, .reading)
            let scoped = url.startAccessingSecurityScopedResource()
            do {
                let book = try await Self.importBook(core, path: url.path(percentEncoded: false))
                if had.contains(book.id) {
                    setImport(row, .already(book.id))
                } else {
                    imported.append(book)
                    if let collection { try? core.setInCollection(collection: collection, book: book.id, inside: true) }
                    setImport(row, .added(book.id, line: Self.importedLine(book, place: place)))
                }
            } catch {
                failures.append("\(url.lastPathComponent)：\(Self.message(for: error))")
                setImport(row, .failed(Self.message(for: error)))
            }
            if scoped { url.stopAccessingSecurityScopedResource() }
            reload()
        }
        reload()
        #if os(macOS)
        foldImports()
        #else
        if !failures.isEmpty {
            alert = failures.joined(separator: "\n")
        } else if !imported.isEmpty {
            let first = imported[0]
            let title = folder != nil ? String(localized: "导入了 \(imported.count) 本") : imported.count == 1 ? String(localized: "已导入《\(first.title)》") : String(localized: "已导入 \(imported.count) 本书")
            show(Toast(icon: folder != nil ? "folder" : "plus", title: title, detail: folder.map { String(localized: "来自“\($0)”文件夹") } ?? String(localized: "在书架最前面")) { [weak self] in
                for book in imported { try? self?.core.deleteBook(id: book.id) }
                self?.reload()
            })
        }
        #endif
    }

    /// The recordings among what was brought in: each M4B on its own, the other sound files
    /// together as one book, and a folder of sound files as one book.
    nonisolated private static func recordings(in urls: [URL]) -> ([[URL]], [URL]) {
        // Locked ones too, so 导入 can say why they won't go in.
        let isAudio = { (u: URL) in Audiobooks.extensions.contains(u.pathExtension.lowercased()) || Audiobooks.locked.contains(u.pathExtension.lowercased()) }
        var books: [[URL]] = []
        var loose: [URL] = []
        var rest: [URL] = []
        for url in urls {
            if url.hasDirectoryPath {
                let all = FileManager.default.enumerator(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles, .skipsPackageDescendants])?
                    .compactMap { $0 as? URL } ?? []
                let audio = all.filter(isAudio)
                if !audio.isEmpty, !all.contains(where: { ["epub", "azw3", "mobi", "pdf", "txt"].contains($0.pathExtension.lowercased()) }) {
                    books.append(audio)
                } else {
                    rest.append(url)
                }
            } else if url.pathExtension.lowercased() == "m4b" {
                books.append([url])
            } else if isAudio(url) {
                loose.append(url)
            } else {
                rest.append(url)
            }
        }
        if !loose.isEmpty { books.append(loose) }
        return (books, rest)
    }

    /// A recording read and waiting on 导入有声书: its name, author, cover and
    /// the order of its files can be changed before it goes onto the shelf.
    struct PendingRecording: Identifiable {
        let id = UUID()
        var audio: AudioImport
        /// A folder's files, or loose ones: copied to a scratch folder, as the picker's access
        /// ends when the import does.
        let scratch: URL
        /// Each file's name as it was, for the list.
        var names: [String]
        /// The e-book on the shelf with the same name, to make 听读书 with afterwards.
        let match: BookSummary?
    }

    var pendingRecording: PendingRecording?

    /// A recording read: copied aside, then onto 导入有声书 to look over.
    private func importRecording(_ files: [URL]) async {
        let scoped = files.filter { $0.startAccessingSecurityScopedResource() }
        defer { scoped.forEach { $0.stopAccessingSecurityScopedResource() } }
        let scratch = FileManager.default.temporaryDirectory.appending(path: "recording-\(UUID().uuidString)", directoryHint: .isDirectory)
        do {
            try FileManager.default.createDirectory(at: scratch, withIntermediateDirectories: true)
            var copies: [URL] = []
            for url in files {
                let copy = scratch.appending(path: url.lastPathComponent)
                try FileManager.default.copyItem(at: url, to: copy)
                copies.append(copy)
            }
            let folderName = files.count > 1 ? files[0].deletingLastPathComponent().lastPathComponent : nil
            let audio = try await AudioImporter.read(core, files: copies, folderName: folderName)
            let probe = Audiobook(id: "", title: audio.title, author: audio.authors.first, files: [], seconds: [])
            // Several brought in at once: one sheet after another.
            while pendingRecording != nil { try? await Task.sleep(for: .milliseconds(200)) }
            let pending = PendingRecording(
                audio: audio, scratch: scratch,
                names: audio.files.map { URL(filePath: $0.path).lastPathComponent },
                match: audiobooks.match(probe, in: books))
            #if DEBUG
            // `-debugImportDirect 1`: straight onto the shelf, as 完成 would.
            if UserDefaults.standard.bool(forKey: "debugImportDirect") {
                finishRecording(pending)
                return
            }
            #endif
            pendingRecording = pending
        } catch {
            try? FileManager.default.removeItem(at: scratch)
            alert = "\(files.first?.lastPathComponent ?? "")：\((error as? AudiobookError)?.message ?? Self.message(for: error))"
        }
    }

    /// 完成 on 导入有声书: onto the shelf as a book of its own.
    @discardableResult
    func finishRecording(_ pending: PendingRecording) -> BookSummary? {
        defer {
            try? FileManager.default.removeItem(at: pending.scratch)
            pendingRecording = nil
        }
        let had = Set(books.map(\.id))
        do {
            let book = try core.importAudiobook(import: pending.audio)
            reload()
            if !had.contains(book.id) {
                show(Toast(icon: "headphones", title: String(localized: "已导入《\(book.title)》"), detail: String(localized: "在书架最前面")) { [weak self] in
                    try? self?.core.deleteBook(id: book.id)
                    self?.reload()
                })
            }
            return book
        } catch {
            alert = Self.message(for: error)
            return nil
        }
    }

    /// 合成听读书 from an 有声书 on the shelf: laid against `ebook`, as a recording brought in to
    /// pair would be.
    func pair(_ audio: BookSummary, with ebook: BookSummary) {
        do {
            let a = try audiobooks.add(shelf: audio)
            audiobooks.make(a, with: ebook)
        } catch {
            alert = Self.message(for: error)
        }
    }

    /// The e-book on the shelf that is the same book as this 有声书, not yet laid against it —
    /// or the one it was being laid against when that stopped (failed, cancelled, the app
    /// closed), so 合成 can go on.
    func ebook(for audio: BookSummary) -> BookSummary? {
        guard audio.format == .audio else { return nil }
        if let mine = audiobooks.all.first(where: { $0.id == audio.id }), let paired = mine.book {
            guard mine.made == nil, audiobooks.job?.audiobook != audio.id else { return nil }
            return books.first { $0.id == paired && $0.available }
        }
        let probe = Audiobook(id: "", title: audio.title, author: audio.authors.first, files: [], seconds: [])
        return audiobooks.match(probe, in: books.filter { $0.format != .audio })
    }

    func cancelRecording() {
        if let p = pendingRecording { try? FileManager.default.removeItem(at: p.scratch) }
        pendingRecording = nil
    }

    /// The books in a folder, all the way down.
    nonisolated private static func booksInside(_ url: URL) -> [URL] {
        let fm = FileManager.default
        let books: Set<String> = ["epub", "azw3", "mobi", "azw", "prc", "pdf", "txt"]
        guard url.hasDirectoryPath,
              let top = try? fm.contentsOfDirectory(at: url, includingPropertiesForKeys: nil, options: .skipsHiddenFiles),
              !top.isEmpty,
              let all = fm.enumerator(at: url, includingPropertiesForKeys: nil, options: [.skipsHiddenFiles, .skipsPackageDescendants])
        else { return [url] }
        let found = all.compactMap { $0 as? URL }.filter { books.contains($0.pathExtension.lowercased()) }
        return found.isEmpty ? [url] : found.sorted { $0.path < $1.path }
    }

    private func setImport(_ id: UUID?, _ state: ImportFile.State) {
        guard let id, let i = imports.firstIndex(where: { $0.id == id }) else { return }
        imports[i].state = state
        importsTouched = .now
        if state.settled { imports[i].took = Date.now.timeIntervalSince(importsStarted) }
    }

    /// `萧红 · 已放进“全部”`.
    private static func importedLine(_ book: BookSummary, place: String) -> String {
        var parts: [String] = []
        if !book.authors.isEmpty { parts.append(book.authors.joined(separator: String(localized: "、"))) }
        parts.append(String(localized: "已放进“\(place)”"))
        return parts.joined(separator: " · ")
    }

    /// When every file went in, the card folds a few seconds later; anything that didn't stays
    /// until it's closed.
    private func foldImports() {
        guard imports.allSatisfy(\.settled), imports.allSatisfy(\.added) else { return }
        let shown = imports.map(\.id)
        Task { [weak self] in
            try? await Task.sleep(for: .seconds(4))
            guard let self, imports.map(\.id) == shown else { return }
            imports = []
        }
    }

    func closeImports() {
        imports.removeAll(where: \.settled)
    }

    /// Splits a catalogue author line ("曹雪芹、高鹗" / "A, B") into trimmed names.
    nonisolated static func splitAuthors(_ raw: String?) -> [String] {
        guard let raw = raw?.trimmingCharacters(in: .whitespacesAndNewlines), !raw.isEmpty else { return [] }
        return raw.split(whereSeparator: { "、,，;/".contains($0) })
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    /// One file that came over Wi‑Fi 传书 or 远程书库: into the library, or why not.
    func importReceived(_ url: URL, title: String? = nil, author: String? = nil, cover: Data? = nil) async throws -> BookSummary {
        importing += 1
        defer { importing -= 1 }
        let ext = url.pathExtension.lowercased()
        let cleanTitle = title?.trimmingCharacters(in: .whitespacesAndNewlines)
        let fallbackTitle = (cleanTitle?.isEmpty == false) ? cleanTitle : nil
        let fallbackAuthors = Self.splitAuthors(author)
        if RemoteFormats.audio.contains(ext) || Audiobooks.extensions.contains(ext) || Audiobooks.locked.contains(ext) {
            var audio = try await AudioImporter.read(core, files: [url], folderName: nil)
            let stem = url.deletingPathExtension().lastPathComponent
            if let fallbackTitle, audio.title.isEmpty || audio.title == stem {
                audio.title = fallbackTitle
            }
            if audio.authors.isEmpty, !fallbackAuthors.isEmpty {
                audio.authors = fallbackAuthors
            }
            if audio.cover == nil, let cover {
                audio.cover = Self.coverJPEG(cover) ?? cover
            }
            let book = try await Self.importAudiobook(core, audio: audio)
            reload()
            return book
        }
        let book = try await Self.importBook(
            core,
            path: url.path(percentEncoded: false),
            fallbackTitle: fallbackTitle,
            fallbackAuthors: fallbackAuthors,
            fallbackCover: cover
        )
        reload()
        return book
    }

    @concurrent nonisolated private static func importAudiobook(_ core: YuyueCore, audio: AudioImport) async throws -> BookSummary {
        try core.importAudiobook(import: audio)
    }

    /// Deletes straight away; the note at the bottom can put it back.
    func delete(_ book: BookSummary) {
        closeOpenBook(book.id)
        run { try core.deleteBook(id: book.id) }
        show(Toast(icon: "trash", danger: true, title: String(localized: "已删除《\(book.title)》"), detail: String(localized: "笔记和进度都留着")) { [weak self] in
            self?.run { _ = try self?.core.restoreBook(id: book.id) }
        })
    }

    /// Deletes a selection in one batch; Undo restores every deleted book.
    func delete(_ books: [BookSummary]) {
        guard !books.isEmpty else { return }
        if books.count == 1 {
            delete(books[0])
            return
        }
        for book in books { closeOpenBook(book.id) }
        let ids = books.map(\.id)
        run {
            for id in ids { try core.deleteBook(id: id) }
        }
        show(Toast(icon: "trash", danger: true, title: String(localized: "已删除 \(books.count) 本书"), detail: String(localized: "笔记和进度都留着")) { [weak self] in
            self?.run {
                for id in ids { _ = try self?.core.restoreBook(id: id) }
            }
        })
    }

    /// Big books take a moment to import; keep it off the main thread.
    @concurrent nonisolated static func importBook(
        _ core: YuyueCore,
        path: String,
        fallbackTitle: String? = nil,
        fallbackAuthors: [String] = [],
        fallbackCover: Data? = nil
    ) async throws -> BookSummary {
        if let size = (try? FileManager.default.attributesOfItem(atPath: path)[.size] as? NSNumber)?.int64Value, size == 0 {
            throw CoreError.BadBook(detail: "empty")
        }
        let url = URL(filePath: path)
        let stem = url.deletingPathExtension().lastPathComponent
        var book = try core.importBook(path: path)
        if book.format == .pdf {
            guard book.readMinutes == 0 else { return book }
            // The core keeps a PDF as it is; the system's viewer says what is in it.
            do {
                var info = try PdfInfoReader.read(url)
                if info.title == nil, let fallbackTitle {
                    info.title = fallbackTitle
                }
                if info.authors.isEmpty, !fallbackAuthors.isEmpty {
                    info.authors = fallbackAuthors
                }
                if info.cover == nil, let fallbackCover {
                    info.cover = Self.coverJPEG(fallbackCover) ?? fallbackCover
                }
                return try core.setPdfInfo(bookId: book.id, info: info)
            } catch {
                try? core.deleteBook(id: book.id)
                throw error
            }
        }
        let needsTitle = fallbackTitle != nil && (book.title.isEmpty || book.title == stem)
        let needsAuthors = !fallbackAuthors.isEmpty && book.authors.isEmpty
        if needsTitle || needsAuthors {
            let nextTitle = needsTitle ? (fallbackTitle ?? book.title) : book.title
            let nextAuthors = needsAuthors ? fallbackAuthors : book.authors
            if let edited = try? core.editBookInfo(book: book.id, title: nextTitle, authors: nextAuthors) {
                book = edited
            }
        }
        return book
    }

    @concurrent nonisolated private static func unzipAudiobook(_ core: YuyueCore, zip: String, into: String) async -> [String] {
        (try? core.audiobookUnzip(path: zip, into: into)) ?? []
    }

    static func message(for error: Error) -> String {
        if let error = error as? PdfInfoReader.Failure {
            return error == .locked ? String(localized: "这个 PDF 设了密码，打不开") : String(localized: "这个 PDF 读不出来")
        }
        if isOutOfSpace(error) { return String(localized: "存储空间不够了，腾出一些地方再试") }
        if let lost = offline(error) { return lost }
        guard let error = error as? CoreError else { return error.localizedDescription }
        switch error {
        case .Unsupported: return String(localized: "不支持这种文件，目前能打开 EPUB、PDF、MOBI、AZW3、TXT")
        case .Protected: return String(localized: "这本书有版权保护（DRM），打不开")
        case .BadBook(let message) where message == "empty": return String(localized: "这个文件是空的，里面没有内容")
        case .BadBook(let message) where message.lowercased().contains("password"): return String(localized: "这个压缩包设了密码，打不开")
        case .BadBook(let message) where message.contains("too large"): return String(localized: "这本书太大了，读不进来")
        case .BadBook: return String(localized: "这本书读不出来，文件可能坏了，或者不是它看起来的格式")
        case .NotFound: return String(localized: "找不到这本书的文件，可能被移走或删掉了。从书架上删掉，再导入一次就好")
        case .Io(let message) where message.contains("os error 1)") || message.contains("os error 13)"):
            return String(localized: "没有权限读这个文件，从访达拖进来或用“导入”选一次就行")
        case .Io(let message) where message.contains("os error 28)"): return String(localized: "磁盘满了，腾点地方再导入")
        case .Io(let message), .Db(let message), .Invalid(let message): return String(localized: "出错了（\(message)）")
        }
    }

    /// The disk is full, whichever layer said so.
    static func isOutOfSpace(_ error: Error) -> Bool {
        let e = error as NSError
        if e.domain == NSCocoaErrorDomain, e.code == NSFileWriteOutOfSpaceError { return true }
        if e.domain == NSPOSIXErrorDomain, e.code == Int(ENOSPC) { return true }
        if let under = e.userInfo[NSUnderlyingErrorKey] as? Error { return isOutOfSpace(under) }
        return false
    }

    /// The network went, or was never there: said plainly, with what happens next.
    static func offline(_ error: Error) -> String? {
        switch (error as? URLError)?.code {
        case .notConnectedToInternet?: String(localized: "没有联网，连上网再试")
        case .networkConnectionLost?, .timedOut?: String(localized: "网断了，下载停在半路。连好网再点一次，会重新下")
        default: nil
        }
    }

    static var deviceName: String {
        #if os(macOS)
        Host.current().localizedName ?? "Mac"
        #else
        UIDevice.current.name
        #endif
    }

    private static var platform: String {
        #if os(macOS)
        "macos"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "ipados" : "ios"
        #endif
    }

    /// `iPhone`, `iPad`, `Mac`: for 这台 iPhone.
    static var platformWord: String {
        #if os(macOS)
        "Mac"
        #else
        UIDevice.current.model
        #endif
    }
}

extension BookSummary: @retroactive Identifiable {}

extension Notification.Name {
    /// A book was deleted or stripped to record-only: open windows for it close without writing back.
    static let bookRemoved = Notification.Name("yuyue.bookRemoved")
}

/// One row of 正在导入.
struct ImportFile: Identifiable, Equatable {
    let id = UUID()
    let name: String
    var state = State.waiting
    /// Seconds from the start of the batch until it settled, for 还要几秒.
    var took: TimeInterval?

    enum State: Equatable {
        case waiting, reading
        case added(String, line: String)
        /// The same file, by its contents, was already on the shelf.
        case already(String)
        case failed(String)

        var settled: Bool { self != .waiting && self != .reading }
    }

    var settled: Bool { state.settled }
    var added: Bool { if case .added = state { true } else { false } }
}
