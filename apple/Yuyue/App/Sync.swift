import AuthenticationServices
import Foundation
import Network
import Security
import YuyueCore

/// 同步, with no server of ours: every device keeps one file in a folder the user owns (a WebDAV
/// share, a folder in iCloud Drive, or 语阅's hidden folder on their Google Drive) holding every change it knows of, and reads the others'.
/// The operation log merges in any order (store.rs), so nothing here has to be careful about who
/// writes first.
@Observable
final class Sync {
    enum Method: String, CaseIterable {
        case none, icloud, webdav, googleDrive
    }

    static func ago(_ date: Date) -> String {
        let s = Date().timeIntervalSince(date)
        if s < 60 { return String(localized: "刚刚") }
        if s < 3600 { return String(localized: "\(Int(s / 60)) 分钟前") }
        if Calendar.current.isDateInToday(date) { return String(localized: "今天 ") + date.formatted(.dateTime.hour().minute()) }
        if Calendar.current.isDateInYesterday(date) { return String(localized: "昨天 ") + date.formatted(.dateTime.hour().minute()) }
        return date.formatted(.dateTime.month().day())
    }

    static func deviceSymbol(_ platform: String) -> String {
        switch platform {
        case "ios": "iphone"
        case "ipados": "ipad"
        case "android": "smartphone"
        default: "laptopcomputer"
        }
    }

    enum State: Equatable {
        case idle
        case syncing
        case failed(String)
    }

    private(set) var state = State.idle
    private(set) var lastSynced: Date?
    private(set) var devices: [DeviceInfo] = []
    /// Bumped whenever the sync target changes so views observing computed settings (`webdav`, `chosenFolder`) refresh.
    private(set) var targetRevision = 0
    var method: Method {
        didSet { UserDefaults.standard.set(method.rawValue, forKey: Keys.method) }
    }

    @ObservationIgnored private let core: YuyueCore
    /// Changes from other devices arrived.
    @ObservationIgnored var changed: (() -> Void)?
    @ObservationIgnored private var running: Task<Void, Never>?
    #if os(iOS)
    /// 书文件 only go over Wi-Fi on the phone.
    @ObservationIgnored private let network = NWPathMonitor()
    #endif

    init(core: YuyueCore) {
        self.core = core
        method = UserDefaults.standard.string(forKey: Keys.method).flatMap(Method.init) ?? .none
        lastSynced = UserDefaults.standard.object(forKey: Keys.last) as? Date
        loadDevices()
        #if os(iOS)
        network.start(queue: .global(qos: .utility))
        #endif
    }

    var isOn: Bool { folder != nil }

    /// 同步这些: what this device neither sends nor takes. Books, collections and the device
    /// list always go.
    private(set) var leftOut: Set<String> = Set(UserDefaults.standard.stringArray(forKey: Keys.leftOut) ?? [])

    func includes(_ part: SyncPart) -> Bool { !leftOut.contains(Self.name(part)) }

    /// Turning a part back on reads every device's file again from the start (`exchange`).
    func include(_ part: SyncPart, _ on: Bool) {
        if on { leftOut.remove(Self.name(part)) } else { leftOut.insert(Self.name(part)) }
        UserDefaults.standard.set(leftOut.sorted(), forKey: Keys.leftOut)
        now()
    }

    /// 书文件: each book's own file goes into the folder too, so a device that has only heard of a
    /// book gets it. On by default.
    private(set) var syncsFiles = UserDefaults.standard.object(forKey: Keys.files) as? Bool ?? true

    func syncFiles(_ on: Bool) {
        syncsFiles = on
        UserDefaults.standard.set(on, forKey: Keys.files)
        now()
    }

    /// 有声书录音: the recordings made into 听读书 here, whole (hundreds of MB each). Off by
    /// default: where each sentence is goes with 进度和书签 anyway, so a device that brings in
    /// its own copy of the recording listens straight away.
    private(set) var syncsRecordings = UserDefaults.standard.bool(forKey: Keys.recordings)

    func syncRecordings(_ on: Bool) {
        syncsRecordings = on
        UserDefaults.standard.set(on, forKey: Keys.recordings)
        now()
    }

    /// Big files go on the phone only over Wi-Fi.
    private var bigFilesNow: Bool {
        #if os(iOS)
        let path = network.currentPath
        return path.status == .satisfied && !path.isExpensive && !path.usesInterfaceType(.cellular)
        #else
        return true
        #endif
    }

    /// What the book files would take in the folder.
    func filesSize() -> Int64 {
        Self.computeFilesSize(core: core)
    }

    func filesSizeAsync() async -> Int64 {
        await Self.computeFilesSizeAsync(core: core)
    }

    @concurrent nonisolated private static func computeFilesSizeAsync(core: YuyueCore) async -> Int64 {
        computeFilesSize(core: core)
    }

    nonisolated private static func computeFilesSize(core: YuyueCore) -> Int64 {
        let books = ((try? core.books()) ?? []).filter(\.available).compactMap { try? core.originalFile(bookId: $0.id) }
        let fm = FileManager.default
        return books.reduce(0) { $0 + ((try? fm.attributesOfItem(atPath: $1)[.size] as? Int64) ?? 0) }
    }

    /// What the 听读书 made here would take in the folder.
    func recordingsSize() -> Int64 {
        let audio = Audiobooks.shared.map { a in a.all.filter { $0.book != nil && $0.made != nil }.map { a.folder($0.id) } } ?? []
        return Self.computeRecordingsSize(dirs: audio)
    }

    func recordingsSizeAsync() async -> Int64 {
        let audio = Audiobooks.shared.map { a in a.all.filter { $0.book != nil && $0.made != nil }.map { a.folder($0.id) } } ?? []
        return await Self.computeRecordingsSizeAsync(dirs: audio)
    }

    @concurrent nonisolated private static func computeRecordingsSizeAsync(dirs: [URL]) async -> Int64 {
        computeRecordingsSize(dirs: dirs)
    }

    nonisolated private static func computeRecordingsSize(dirs: [URL]) -> Int64 {
        let fm = FileManager.default
        var total: Int64 = 0
        for dir in dirs {
            for name in (try? fm.contentsOfDirectory(atPath: dir.path(percentEncoded: false))) ?? [] where !name.hasPrefix("emissions-") {
                total += (try? fm.attributesOfItem(atPath: dir.appending(path: name).path(percentEncoded: false))[.size] as? Int64) ?? 0
            }
        }
        return total
    }

    nonisolated private static func name(_ part: SyncPart) -> String {
        switch part {
        case .reading: "reading"
        case .notes: "notes"
        case .settings: "settings"
        }
    }

    func loadDevices() {
        devices = (try? core.devices()) ?? []
    }

    /// Exchanges changes now, unless an exchange is already under way.
    func now() {
        guard running == nil, let folder else { return }
        state = .syncing
        let core = core
        let audio = syncsRecordings && bigFilesNow ? Audiobooks.shared?.dir : nil
        let books = syncsFiles && bigFilesNow
        running = Task {
            do {
                var applied = try await Self.exchange(core: core, folder: folder)
                // Book files when they're on, and 播客's episodes always.
                let (gotBooks, notGot, gotAudio) = await Self.exchangeFiles(core: core, folder: folder, books: books, audiobooks: audio)
                applied += gotBooks
                let missed = notGot
                if gotAudio { Audiobooks.shared?.reload() }
                lastSynced = Date()
                UserDefaults.standard.set(lastSynced, forKey: Keys.last)
                // Notes and progress went across; a book file that didn't come down isn't 已同步.
                state = missed > 0 ? .failed(String(localized: "有 \(missed) 本书的文件没下载下来，下次同步再试")) : .idle
                if applied > 0 { changed?() }
            } catch {
                state = .failed(Self.message(for: error))
            }
            loadDevices()
            running = nil
        }
    }

    /// Syncs every few minutes while the app is open.
    func keepUp() async {
        while !Task.isCancelled {
            now()
            try? await Task.sleep(for: .seconds(5 * 60))
        }
    }

    // MARK: Where

    private var folder: (any SyncFolder)? {
        switch method {
        case .webdav: webdav.map(WebDAVFolder.init)
        case .icloud: Self.chosenFolder.map(LocalFolder.init)
        case .googleDrive: Keychain.read(Self.googleRefreshKey) != nil && GoogleAuth.clientID != nil ? GoogleDriveFolder() : nil
        case .none: nil
        }
    }

    /// The Google account signed in for 同步, to show.
    var googleEmail: String? { UserDefaults.standard.string(forKey: Keys.googleEmail) }

    /// Signs in to Google in the system's sheet, checks the hidden folder can be reached, then
    /// syncs with it.
    func connectGoogle(authenticate: (URL, String) async throws -> URL) async throws {
        let refresh = try await GoogleAuth.signIn(authenticate: authenticate)
        Keychain.write(Self.googleRefreshKey, refresh)
        let folder = GoogleDriveFolder()
        do {
            _ = try await folder.list()
        } catch {
            Keychain.delete(Self.googleRefreshKey)
            DriveSession.shared.forget()
            throw error
        }
        UserDefaults.standard.set(await folder.email(), forKey: Keys.googleEmail)
        running?.cancel()
        running = nil
        resetRemoteCache()
        method = .googleDrive
        targetRevision &+= 1
        now()
    }

    nonisolated static let googleRefreshKey = "sync.google.refresh"

    nonisolated struct WebDAVAccount: Equatable, Sendable {
        var url: URL
        var user: String
        var password: String
        /// The folder on the share the devices meet in.
        var folder = String(localized: "语阅")
    }

    var webdav: WebDAVAccount? {
        _ = targetRevision
        let d = UserDefaults.standard
        guard let s = d.string(forKey: Keys.webdavURL), let url = URL(string: s) else { return nil }
        return WebDAVAccount(url: url, user: d.string(forKey: Keys.webdavUser) ?? "", password: Keychain.read(Keys.webdavPassword) ?? "",
                             folder: d.string(forKey: Keys.webdavFolder) ?? String(localized: "语阅"))
    }

    /// Checks the account can be reached and written, then keeps it.
    func connect(_ account: WebDAVAccount) async throws {
        try await WebDAVFolder(account).prepare()
        keep(account)
    }

    /// Keeps an account 连接检查 has passed, and syncs with it.
    func keep(_ account: WebDAVAccount) {
        let normalized = WebDAVAccount(
            url: WebDAVFolder.normalizeBase(account.url),
            user: account.user,
            password: account.password,
            folder: account.folder
        )
        let previous = webdav
        let targetChanged = method != .webdav
            || previous.map { WebDAVFolder.normalizeBase($0.url) != normalized.url || $0.user != normalized.user || $0.folder != normalized.folder } ?? true
        UserDefaults.standard.set(normalized.url.absoluteString, forKey: Keys.webdavURL)
        UserDefaults.standard.set(normalized.user, forKey: Keys.webdavUser)
        UserDefaults.standard.set(normalized.folder, forKey: Keys.webdavFolder)
        Keychain.write(Keys.webdavPassword, normalized.password)
        if targetChanged {
            running?.cancel()
            running = nil
            resetRemoteCache()
        }
        method = .webdav
        targetRevision &+= 1
        now()
    }

    private static var cachedFolderBookmark: (data: Data, url: URL?)?

    /// The iCloud Drive folder picked for 同步 (a security-scoped bookmark: the app is sandboxed).
    static var chosenFolder: URL? {
        #if DEBUG
        // `-debugSyncDir <dir>`: a folder in the app's own container, to try a round between two scratch libraries.
        if let dir = UserDefaults.standard.string(forKey: "debugSyncDir") { return URL(filePath: dir, directoryHint: .isDirectory) }
        #endif
        guard let data = UserDefaults.standard.data(forKey: Keys.folder) else {
            cachedFolderBookmark = nil
            return nil
        }
        if let cached = cachedFolderBookmark, cached.data == data {
            return cached.url
        }
        var stale = false
        #if os(macOS)
        let url = try? URL(resolvingBookmarkData: data, options: .withSecurityScope, bookmarkDataIsStale: &stale)
        #else
        let url = try? URL(resolvingBookmarkData: data, bookmarkDataIsStale: &stale)
        #endif
        cachedFolderBookmark = (data, url)
        return url
    }

    func choose(folder url: URL) throws {
        let previousFolder = Self.chosenFolder?.standardizedFileURL
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }
        #if os(macOS)
        let data = try url.bookmarkData(options: .withSecurityScope)
        #else
        let data = try url.bookmarkData()
        #endif
        let targetChanged = method != .icloud || previousFolder != url.standardizedFileURL
        UserDefaults.standard.set(data, forKey: Keys.folder)
        if targetChanged {
            running?.cancel()
            running = nil
            resetRemoteCache()
        }
        method = .icloud
        targetRevision &+= 1
        now()
    }

    func stop() {
        running?.cancel()
        running = nil
        if method == .googleDrive {
            // 停止同步 signs out too; the files stay in the Drive for the other devices.
            Keychain.delete(Self.googleRefreshKey)
            UserDefaults.standard.removeObject(forKey: Keys.googleEmail)
            DriveSession.shared.forget()
        }
        method = .none
        state = .idle
        targetRevision &+= 1
    }

    /// Clears cached remote file tags and write stamps when switching sync backend or folder.
    private func resetRemoteCache() {
        let d = UserDefaults.standard
        for key in ["sync.seen", "sync.written", "sync.leftOut.done",
                    "sync.companion.seen", "sync.companion.sent",
                    "sync.audio.seen", "sync.audio.sent"] {
            d.removeObject(forKey: key)
        }
    }

    private enum Keys {
        static let method = "sync.method"
        static let last = "sync.last"
        static let webdavURL = "sync.webdav.url"
        static let webdavUser = "sync.webdav.user"
        static let webdavPassword = "sync.webdav.password"
        static let webdavFolder = "sync.webdav.folder"
        static let folder = "sync.folder"
        static let googleEmail = "sync.google.email"
        static let leftOut = "sync.leftOut"
        static let files = "sync.files"
        static let recordings = "sync.recordings"
    }

    // MARK: Exchanging

    nonisolated private static let suffix = ".yuyue-sync.json"

    /// Reads every other device's file that changed since last time, then writes this device's
    /// own when it knows something new. Returns how many values changed here.
    @concurrent nonisolated private static func exchange(core: YuyueCore, folder: any SyncFolder) async throws -> UInt32 {
        try await folder.prepare()
        let mine = core.deviceId() + suffix
        let files = try await folder.list()
        // 同步这些 as it is now, and as it was the last time round.
        let names = Set(UserDefaults.standard.stringArray(forKey: "sync.leftOut") ?? [])
        let leftOut = [SyncPart.reading, .notes, .settings].filter { names.contains(name($0)) }
        let before = UserDefaults.standard.stringArray(forKey: "sync.leftOut.done").map(Set.init)
        // A part on again: what was passed over in files already read is only in them, so read them all.
        let reread = before.map { !$0.isSubset(of: names) } ?? false
        var seen = reread ? [:] : UserDefaults.standard.dictionary(forKey: "sync.seen") as? [String: String] ?? [:]
        var applied: UInt32 = 0
        for (name, tag) in files where name.hasSuffix(suffix) && name != mine && seen[name] != tag {
            try Task.checkCancellation()
            let data = try await folder.read(name)
            applied += try core.importChangesExcept(batch: String(decoding: data, as: UTF8.self), leftOut: leftOut)
            seen[name] = tag
        }
        UserDefaults.standard.set(seen, forKey: "sync.seen")
        let version = try core.syncVersion() + "|" + names.sorted().joined(separator: ",")
        if files[mine] == nil || UserDefaults.standard.string(forKey: "sync.written") != version {
            let everything = try core.exportChangesExcept(version: "{}", leftOut: leftOut)
            try await folder.write(mine, Data(everything.utf8))
            UserDefaults.standard.set(version, forKey: "sync.written")
        }
        UserDefaults.standard.set(names.sorted(), forKey: "sync.leftOut.done")
        return applied
    }

    /// 书文件和有声书. A book's file is `<id>.book.<ext>`: ids are the file's hash, so the same file
    /// imported anywhere is the same book. A 听读书 is `<id>.audio.<file>` for each of its files,
    /// with `info.json` written last, so a device takes it only once all of it is there. One file
    /// failing doesn't stop the rest; it's tried again next time. Returns the books taken, and
    /// whether any 听读书 came.
    @concurrent nonisolated private static func exchangeFiles(core: YuyueCore, folder: any SyncFolder, books all: Bool, audiobooks: URL?) async -> (UInt32, Int, Bool) {
        guard let files = try? await folder.list() else { return (0, 0, false) }
        let fm = FileManager.default
        // 书文件 off (or the phone not on Wi-Fi): only 播客's episodes, a few KB each.
        let books = ((try? core.books()) ?? []).filter { all || $0.format == .podcast }
        var took: UInt32 = 0
        var missed = 0
        // Up: each book here the folder doesn't have yet.
        for book in books where book.available && !files.keys.contains(where: { $0.hasPrefix(book.id + ".book.") }) {
            guard !Task.isCancelled, let path = try? core.originalFile(bookId: book.id) else { continue }
            let url = URL(filePath: path)
            try? await folder.upload("\(book.id).book.\(url.pathExtension)", from: url)
        }
        // Down: each book known here only from another device.
        let temp = fm.temporaryDirectory.appending(path: "yuyue-sync-\(UUID().uuidString)", directoryHint: .isDirectory)
        try? fm.createDirectory(at: temp, withIntermediateDirectories: true)
        defer { try? fm.removeItem(at: temp) }
        for book in books where !book.available {
            guard !Task.isCancelled, let name = files.keys.first(where: { $0.hasPrefix(book.id + ".book.") }) else { continue }
            // Named as the book is, so a TXT's title (taken from its file name) comes out the same.
            let ext = (name as NSString).pathExtension
            let url = temp.appending(path: fileName(book.title) + "." + ext)
            do {
                try await folder.download(name, to: url)
                _ = try await Library.importBook(core, path: url.path(percentEncoded: false))
                took += 1
            } catch { missed += 1 }
            try? fm.removeItem(at: url)
        }
        took += await exchangePodcastFiles(core: core, folder: folder, files: files, includeClips: all)
        took += await exchangeCompanionCaches(core: core, folder: folder, files: files)
        guard let audiobooks else { return (took, missed, false) }
        return (took, missed, await exchangeAudiobooks(folder: folder, files: files, dir: audiobooks))
    }

    /// 书伴问答与前情提要纯文本缓存 (`companion/<id>.json` / `<id>.recap.json`)，一处付费多端复用.
    @concurrent nonisolated private static func exchangeCompanionCaches(
        core: YuyueCore,
        folder: any SyncFolder,
        files: [String: String]
    ) async -> UInt32 {
        let leftOut = Set(UserDefaults.standard.stringArray(forKey: "sync.leftOut") ?? [])
        guard !leftOut.contains("reading") else { return 0 }
        let fm = FileManager.default
        let dataDir = URL(filePath: core.podcastClipsDir()).deletingLastPathComponent().deletingLastPathComponent()
        let compDir = dataDir.appending(path: "companion", directoryHint: .isDirectory)
        try? fm.createDirectory(at: compDir, withIntermediateDirectories: true)

        var seen = UserDefaults.standard.dictionary(forKey: "sync.companion.seen") as? [String: String] ?? [:]
        var sent = UserDefaults.standard.dictionary(forKey: "sync.companion.sent") as? [String: String] ?? [:]
        defer {
            UserDefaults.standard.set(seen, forKey: "sync.companion.seen")
            UserDefaults.standard.set(sent, forKey: "sync.companion.sent")
        }

        func stamp(_ url: URL) -> String? {
            guard let v = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else { return nil }
            return "\(v.contentModificationDate?.timeIntervalSince1970 ?? 0)-\(v.fileSize ?? 0)"
        }

        func remoteName(for localFile: String) -> String? {
            if localFile.hasSuffix(".recap.json") {
                let id = String(localFile.dropLast(".recap.json".count))
                return id.isEmpty ? nil : "\(id).recap.json"
            }
            if localFile.hasSuffix(".json") {
                let id = String(localFile.dropLast(".json".count))
                return id.isEmpty ? nil : "\(id).companion.json"
            }
            return nil
        }

        func localName(for remote: String) -> String? {
            if remote.hasSuffix(".recap.json") {
                let id = String(remote.dropLast(".recap.json".count))
                guard !id.isEmpty, !id.contains("/"), !id.contains("..") else { return nil }
                return "\(id).recap.json"
            }
            if remote.hasSuffix(".companion.json") {
                let id = String(remote.dropLast(".companion.json".count))
                guard !id.isEmpty, !id.contains("/"), !id.contains("..") else { return nil }
                return "\(id).json"
            }
            return nil
        }

        var took: UInt32 = 0
        // Down: merge remote JSON keys into local cache so neither device clobbers the other.
        for (remote, tag) in files where seen[remote] != tag {
            guard !Task.isCancelled, let local = localName(for: remote) else { continue }
            guard let data = try? await folder.read(remote),
                  let remoteObj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { continue }
            let dest = compDir.appending(path: local)
            var merged = ((try? Data(contentsOf: dest)).flatMap { try? JSONSerialization.jsonObject(with: $0) } as? [String: Any]) ?? [:]
            var added = false
            for (k, v) in remoteObj where merged[k] == nil {
                merged[k] = v
                added = true
            }
            if added || !fm.fileExists(atPath: dest.path(percentEncoded: false)),
               let encoded = try? JSONSerialization.data(withJSONObject: merged) {
                try? encoded.write(to: dest, options: .atomic)
                took += 1
            }
            seen[remote] = tag
        }

        // Up: upload local caches created or updated since last sync.
        let locals = (try? fm.contentsOfDirectory(atPath: compDir.path(percentEncoded: false))) ?? []
        for file in locals where file.hasSuffix(".json") && !file.hasPrefix(".") {
            guard !Task.isCancelled, let remote = remoteName(for: file) else { continue }
            let url = compDir.appending(path: file)
            guard let s = stamp(url), sent[remote] != s || files[remote] == nil else { continue }
            if let data = try? Data(contentsOf: url), !data.isEmpty {
                try? await folder.write(remote, data)
                sent[remote] = s
            }
        }
        return took
    }

    /// 播客的逐字稿 / 书房电台剧本 (`issues/<id>.json`) 与原声摘录切片 (`clips/<file>`).
    @concurrent nonisolated private static func exchangePodcastFiles(
        core: YuyueCore,
        folder: any SyncFolder,
        files: [String: String],
        includeClips: Bool
    ) async -> UInt32 {
        let fm = FileManager.default
        let leftOut = Set(UserDefaults.standard.stringArray(forKey: "sync.leftOut") ?? [])
        let hubDir = URL(filePath: core.podcastClipsDir()).deletingLastPathComponent()
        var took: UInt32 = 0

        if !leftOut.contains("reading") {
            let issuesDir = hubDir.appending(path: "issues", directoryHint: .isDirectory)
            try? fm.createDirectory(at: issuesDir, withIntermediateDirectories: true)
            let localIssues = (try? fm.contentsOfDirectory(atPath: issuesDir.path(percentEncoded: false))) ?? []
            for file in localIssues where file.hasSuffix(".json") && !file.hasPrefix(".") {
                guard !Task.isCancelled else { break }
                let id = String(file.dropLast(".json".count))
                let remote = "\(id).pod-issue.json"
                guard files[remote] == nil else { continue }
                if let data = try? Data(contentsOf: issuesDir.appending(path: file)) {
                    try? await folder.write(remote, data)
                }
            }
            for name in files.keys where name.hasSuffix(".pod-issue.json") {
                guard !Task.isCancelled else { break }
                let id = String(name.dropLast(".pod-issue.json".count))
                guard !id.isEmpty, !id.contains("/"), !id.contains("..") else { continue }
                let dest = issuesDir.appending(path: "\(id).json")
                guard !fm.fileExists(atPath: dest.path(percentEncoded: false)) else { continue }
                if let data = try? await folder.read(name) {
                    try? data.write(to: dest, options: .atomic)
                    took += 1
                }
            }
        }

        if includeClips && !leftOut.contains("notes") {
            let clipsDir = hubDir.appending(path: "clips", directoryHint: .isDirectory)
            try? fm.createDirectory(at: clipsDir, withIntermediateDirectories: true)
            let localClips = (try? fm.contentsOfDirectory(atPath: clipsDir.path(percentEncoded: false))) ?? []
            for file in localClips where !file.hasPrefix(".") {
                guard !Task.isCancelled else { break }
                let remote = "\(file).pod-clip"
                guard files[remote] == nil else { continue }
                try? await folder.upload(remote, from: clipsDir.appending(path: file))
            }
            for name in files.keys where name.hasSuffix(".pod-clip") {
                guard !Task.isCancelled else { break }
                let file = String(name.dropLast(".pod-clip".count))
                guard !file.isEmpty, !file.hasPrefix("."), !file.contains("/"), !file.contains("..") else { continue }
                let dest = clipsDir.appending(path: file)
                guard !fm.fileExists(atPath: dest.path(percentEncoded: false)) else { continue }
                let partial = clipsDir.appending(path: ".\(file).download")
                if (try? await folder.download(name, to: partial)) != nil {
                    _ = try? fm.replaceItemAt(dest, withItemAt: partial)
                    took += 1
                } else {
                    try? fm.removeItem(at: partial)
                }
            }
        }
        return took
    }

    @concurrent nonisolated private static func exchangeAudiobooks(folder: any SyncFolder, files: [String: String], dir: URL) async -> Bool {
        let fm = FileManager.default
        func info(_ data: Data) -> [String: Any]? { (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] }
        func parts(_ info: [String: Any]) -> [String] { (info["files"] as? [String] ?? []) + ["cover.jpg", "timings.json"] }
        func stamp(_ url: URL) -> String? {
            guard let v = try? url.resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey]) else { return nil }
            return "\(v.contentModificationDate?.timeIntervalSince1970 ?? 0)-\(v.fileSize ?? 0)"
        }
        // What was last put up from here, by file, so 微调 goes up again.
        var sent = UserDefaults.standard.dictionary(forKey: "sync.audio.sent") as? [String: String] ?? [:]
        var seen = UserDefaults.standard.dictionary(forKey: "sync.audio.seen") as? [String: String] ?? [:]
        defer {
            UserDefaults.standard.set(sent, forKey: "sync.audio.sent")
            UserDefaults.standard.set(seen, forKey: "sync.audio.seen")
        }
        let ids = (try? fm.contentsOfDirectory(atPath: dir.path(percentEncoded: false))) ?? []
        // Up: the 听读书 made here, laid against a book.
        for id in ids {
            let here = dir.appending(path: id, directoryHint: .isDirectory)
            guard let data = try? Data(contentsOf: here.appending(path: "info.json")), let i = info(data),
                  i["book"] is String, i["made"] != nil else { continue }
            var whole = true
            for part in parts(i) + ["info.json"] {
                let url = here.appending(path: part)
                guard !Task.isCancelled, let now = stamp(url) else { continue }
                let name = "\(id).audio.\(part)"
                // info.json last, and only once the rest is up.
                if part == "info.json", !whole { break }
                // New to the folder, or changed since it went up (微调). What came from another
                // device was marked sent when it came.
                guard files[name] == nil || sent[name].map({ $0 != now }) == true else { continue }
                do {
                    try await folder.upload(name, from: url)
                    sent[name] = now
                } catch { whole = false }
            }
        }
        // Down: one from another device, once its info.json is there.
        var got = false
        for (name, tag) in files where name.hasSuffix(".audio.info.json") && seen[name] != tag {
            guard !Task.isCancelled, let data = try? await folder.read(name), let i = info(data) else { continue }
            let id = String(name.dropLast(".audio.info.json".count))
            guard !id.isEmpty, !id.contains("/") else { continue }
            let here = dir.appending(path: id, directoryHint: .isDirectory)
            try? fm.createDirectory(at: here, withIntermediateDirectories: true)
            var whole = true
            for part in parts(i) {
                let remote = "\(id).audio.\(part)"
                let url = here.appending(path: part)
                guard let remoteTag = files[remote] else { continue }
                let present = fm.fileExists(atPath: url.path(percentEncoded: false))
                // The recording doesn't change; the timings do (微调).
                if present, part != "timings.json" || seen[remote] == remoteTag {
                    if sent[remote] == nil { sent[remote] = stamp(url) }
                    continue
                }
                do {
                    let partial = here.appending(path: part + ".download")
                    try await folder.download(remote, to: partial)
                    if present, (try? Data(contentsOf: partial)) == (try? Data(contentsOf: url)) {
                        try? fm.removeItem(at: partial)
                    } else {
                        _ = try fm.replaceItemAt(url, withItemAt: partial)
                        got = true
                    }
                    sent[remote] = stamp(url)
                    seen[remote] = remoteTag
                } catch { whole = false }
            }
            guard whole else { continue }
            let url = here.appending(path: "info.json")
            if (try? Data(contentsOf: url)) != data {
                try? data.write(to: url, options: .atomic)
                got = true
            }
            sent[name] = stamp(url)
            seen[name] = tag
        }
        return got
    }

    /// A book's title as a file name.
    nonisolated private static func fileName(_ title: String) -> String {
        let bad = CharacterSet(charactersIn: "/\\:?*\"<>|").union(.controlCharacters)
        let clean = title.components(separatedBy: bad).joined(separator: " ").trimmingCharacters(in: .whitespaces)
        let cut = String(clean.prefix(80)).trimmingCharacters(in: CharacterSet(charactersIn: ". "))
        return cut.isEmpty ? "book" : cut
    }

    static func message(for error: Error) -> String {
        switch error {
        case SyncError.status(401), SyncError.status(403): String(localized: "账号或密码不对")
        case SyncError.status(404): String(localized: "服务器上没有这个地址")
        case SyncError.status(let code): String(localized: "服务器回了 \(code)")
        case SyncError.noFolder: String(localized: "找不到同步用的文件夹，重新选一下")
        case SyncError.signedOut: String(localized: "Google 账号要重新登录一下")
        case SyncError.signInFailed: String(localized: "Google 没让登录，再试一次")
        case SyncError.notRegistered: String(localized: "这个版本还没开通 Google Drive")
        case let e as ASWebAuthenticationSessionError where e.code == .canceledLogin: String(localized: "没有登录")
        case is CancellationError: String(localized: "没有登录")
        case let e as URLError where e.code == .notConnectedToInternet: String(localized: "没有联网")
        case let e as URLError where e.code == .timedOut: String(localized: "连接超时")
        case let e as URLError where e.code == .cannotFindHost || e.code == .cannotConnectToHost: String(localized: "连不上服务器")
        default: Library.message(for: error)
        }
    }
}

nonisolated enum SyncError: Error {
    case status(Int)
    case noFolder
    /// Google took the sign-in back, or it ran out.
    case signedOut
    case signInFailed
    /// No Google client id in this build.
    case notRegistered
}

/// A folder the devices meet in: file name → a tag that changes when the file does.
nonisolated protocol SyncFolder: Sendable {
    func prepare() async throws
    func list() async throws -> [String: String]
    func read(_ name: String) async throws -> Data
    func write(_ name: String, _ data: Data) async throws
    /// 书文件 and recordings, without holding them in memory.
    func upload(_ name: String, from file: URL) async throws
    func download(_ name: String, to file: URL) async throws
}

nonisolated extension SyncFolder {
    /// The sync files are JSON; everything else goes as it is.
    static func type(of name: String) -> String {
        name.hasSuffix(".json") ? "application/json" : "application/octet-stream"
    }

    /// Puts a file downloaded to `temp` at `file`.
    static func place(_ temp: URL, at file: URL) throws {
        let fm = FileManager.default
        if fm.fileExists(atPath: file.path(percentEncoded: false)) { try fm.removeItem(at: file) }
        try fm.moveItem(at: temp, to: file)
    }
}

// MARK: - WebDAV

/// 坚果云、群晖、Nextcloud…: files under `<base>/语阅/`.
nonisolated struct WebDAVFolder: SyncFolder {
    let base: URL
    let user: String
    let password: String
    var folder = String(localized: "语阅")

    init(_ account: Sync.WebDAVAccount) {
        base = account.url
        user = account.user
        password = account.password
        folder = account.folder
    }

    /// Guarantees a trailing slash on a WebDAV base URL so collection requests don't hit 301 redirects that drop auth.
    static func normalizeBase(_ url: URL) -> URL {
        guard !url.absoluteString.hasSuffix("/") else { return url }
        guard var comps = URLComponents(url: url, resolvingAgainstBaseURL: true) else { return url }
        let path = comps.percentEncodedPath
        comps.percentEncodedPath = path.isEmpty ? "/" : path + "/"
        return comps.url ?? url
    }

    var normalizedBase: URL { Self.normalizeBase(base) }

    var folderSegments: [String] {
        folder.split(separator: "/").map(String.init).filter { !$0.isEmpty }
    }

    var dir: URL {
        folderSegments.reduce(normalizedBase) { current, seg in
            current.appending(path: seg, directoryHint: .isDirectory)
        }
    }

    func prepare() async throws {
        let segments = folderSegments
        guard !segments.isEmpty else { return }
        var current = normalizedBase
        for (index, seg) in segments.enumerated() {
            current = current.appending(path: seg, directoryHint: .isDirectory)
            var r = request(current, "MKCOL")
            r.timeoutInterval = 20
            let (_, response) = try await URLSession.shared.data(for: r)
            let code = (response as? HTTPURLResponse)?.statusCode ?? 0
            // 405: collection already exists.
            if (200 ..< 300).contains(code) || code == 405 { continue }
            // Read-only parent mounts may answer 301/403 even when the parent already exists; let the final segment decide.
            let isLast = index == segments.count - 1
            if !isLast && (code == 301 || code == 403) { continue }
            throw SyncError.status(code)
        }
    }

    func list() async throws -> [String: String] {
        var r = request(dir, "PROPFIND")
        r.setValue("1", forHTTPHeaderField: "Depth")
        r.setValue("application/xml", forHTTPHeaderField: "Content-Type")
        r.httpBody = Data("""
        <?xml version="1.0" encoding="utf-8"?>
        <d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getetag/><d:getlastmodified/><d:getcontentlength/></d:prop></d:propfind>
        """.utf8)
        let data = try await send(r)
        return PropfindParser.parse(data, baseDir: dir)
    }

    func read(_ name: String) async throws -> Data {
        try await send(request(dir.appending(path: name, directoryHint: .notDirectory), "GET"))
    }

    func write(_ name: String, _ data: Data) async throws {
        var r = request(dir.appending(path: name, directoryHint: .notDirectory), "PUT")
        r.setValue(Self.type(of: name), forHTTPHeaderField: "Content-Type")
        r.httpBody = data
        _ = try await send(r)
    }

    func upload(_ name: String, from file: URL) async throws {
        var r = request(dir.appending(path: name, directoryHint: .notDirectory), "PUT")
        r.setValue(Self.type(of: name), forHTTPHeaderField: "Content-Type")
        r.timeoutInterval = 600
        let (_, response) = try await URLSession.shared.upload(for: r, fromFile: file)
        try Self.check(response)
    }

    func download(_ name: String, to file: URL) async throws {
        var r = request(dir.appending(path: name, directoryHint: .notDirectory), "GET")
        r.timeoutInterval = 600
        let (temp, response) = try await URLSession.shared.download(for: r)
        try Self.check(response)
        try Self.place(temp, at: file)
    }

    private static func check(_ response: URLResponse) throws {
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200 ..< 300).contains(code) else { throw SyncError.status(code) }
    }

    func request(_ url: URL, _ method: String) -> URLRequest {
        var r = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 60)
        r.httpMethod = method
        if !user.isEmpty {
            let token = Data("\(user):\(password)".utf8).base64EncodedString()
            r.setValue("Basic \(token)", forHTTPHeaderField: "Authorization")
        }
        return r
    }

    private func send(_ r: URLRequest) async throws -> Data {
        let (data, response) = try await URLSession.shared.data(for: r)
        let code = (response as? HTTPURLResponse)?.statusCode ?? 0
        guard (200 ..< 300).contains(code) else { throw SyncError.status(code) }
        return data
    }
}

/// The file names and tags in a PROPFIND answer.
private nonisolated final class PropfindParser: NSObject, XMLParserDelegate {
    private let baseDir: URL
    private let basePath: String
    private var out: [String: String] = [:]
    private var href = ""
    private var tag = ""
    private var modified = ""
    private var length = ""
    private var isCollection = false
    private var text = ""

    private init(baseDir: URL) {
        self.baseDir = baseDir
        self.basePath = Self.normalizedPath(baseDir.path(percentEncoded: false))
    }

    static func parse(_ data: Data, baseDir: URL) -> [String: String] {
        let p = PropfindParser(baseDir: baseDir)
        let parser = XMLParser(data: data)
        parser.shouldProcessNamespaces = true
        parser.delegate = p
        parser.parse()
        return p.out
    }

    private static func normalizedPath(_ raw: String) -> String {
        "/" + raw.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
    }

    private func resolvedPath(for rawHref: String) -> String {
        let url = URL(string: rawHref, relativeTo: baseDir)
            ?? rawHref.addingPercentEncoding(withAllowedCharacters: .urlFragmentAllowed.union(CharacterSet(charactersIn: "%")))
                .flatMap { URL(string: $0, relativeTo: baseDir) }
        let decoded = url?.path(percentEncoded: false) ?? (rawHref.removingPercentEncoding ?? rawHref)
        return Self.normalizedPath(decoded)
    }

    func parser(_ parser: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
        text = ""
        if name == "response" {
            href = ""
            tag = ""
            modified = ""
            length = ""
            isCollection = false
        } else if name == "collection" {
            isCollection = true
        }
    }

    func parser(_ parser: XMLParser, foundCharacters string: String) {
        text += string
    }

    func parser(_ parser: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
        let value = text.trimmingCharacters(in: .whitespacesAndNewlines)
        switch name {
        case "href": href = value
        case "getetag": tag = value
        case "getlastmodified": modified = value
        case "getcontentlength": length = value
        case "response":
            guard !isCollection, !href.isEmpty, !href.hasSuffix("/") else { return }
            let itemPath = resolvedPath(for: href)
            guard itemPath != basePath else { return }
            let file = itemPath.split(separator: "/").last.map(String.init) ?? ""
            if !file.isEmpty {
                out[file] = "\(tag)|\(modified)|\(length)"
            }
        default: break
        }
    }
}

// MARK: - A folder on disk (iCloud Drive)

nonisolated struct LocalFolder: SyncFolder {
    let url: URL

    @concurrent func prepare() async throws {
        guard FileManager.default.fileExists(atPath: url.path(percentEncoded: false)) else { throw SyncError.noFolder }
    }

    @concurrent func list() async throws -> [String: String] {
        try scoped {
            let names = try FileManager.default.contentsOfDirectory(atPath: url.path(percentEncoded: false))
            var out: [String: String] = [:]
            for name in names {
                // iCloud keeps files not yet downloaded as ".name.icloud".
                if name.hasPrefix("."), name.hasSuffix(".icloud") {
                    try? FileManager.default.startDownloadingUbiquitousItem(at: url.appending(path: name))
                    continue
                }
                let v = try? url.appending(path: name).resourceValues(forKeys: [.contentModificationDateKey, .fileSizeKey])
                out[name] = "\(v?.contentModificationDate?.timeIntervalSince1970 ?? 0)-\(v?.fileSize ?? 0)"
            }
            return out
        }
    }

    @concurrent func read(_ name: String) async throws -> Data {
        try scoped { try Data(contentsOf: url.appending(path: name)) }
    }

    @concurrent func write(_ name: String, _ data: Data) async throws {
        try scoped { try data.write(to: url.appending(path: name), options: .atomic) }
    }

    func upload(_ name: String, from file: URL) async throws {
        try await Self.copy(file, to: url.appending(path: name), scope: url)
    }

    func download(_ name: String, to file: URL) async throws {
        try await Self.copy(url.appending(path: name), to: file, scope: url)
    }

    /// Off the main thread: a book can be big.
    @concurrent nonisolated private static func copy(_ from: URL, to: URL, scope: URL) async throws {
        let on = scope.startAccessingSecurityScopedResource()
        defer { if on { scope.stopAccessingSecurityScopedResource() } }
        let fm = FileManager.default
        let temp = to.deletingLastPathComponent().appending(path: ".\(to.lastPathComponent).\(UUID().uuidString)")
        try fm.copyItem(at: from, to: temp)
        try place(temp, at: to)
    }

    private func scoped<T>(_ body: () throws -> T) throws -> T {
        let on = url.startAccessingSecurityScopedResource()
        defer { if on { url.stopAccessingSecurityScopedResource() } }
        return try body()
    }
}

// MARK: - Keychain

#if os(macOS)
/// The Mac keeps keys in a file in the app's container. Its keychain ties an item to the app's code
/// signature, and without a Developer ID the signature changes with every build, so each update
/// would ask for the login password once per key. The file is plain text, readable only by this
/// user, and outside the library so it's never in a snapshot or a sync.
nonisolated enum Keychain {
    private static let store = Mutex<[String: String]?>(nil)

    private static var file: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "Yuyue/secrets.json")
    }

    /// `-debugNoKeychain 1`: a scratch run neither sees the real keys nor changes them.
    private static var off: Bool {
        #if DEBUG
        UserDefaults.standard.bool(forKey: "debugNoKeychain")
        #else
        false
        #endif
    }

    private static func loadFromDisk() -> [String: String] {
        guard let data = try? Data(contentsOf: file),
              let decoded = try? JSONDecoder().decode([String: String].self, from: data) else { return [:] }
        return decoded
    }

    private static func save(_ secrets: [String: String]) {
        guard let data = try? JSONEncoder().encode(secrets) else { return }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        guard (try? data.write(to: file, options: .atomic)) != nil else { return }
        try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
    }

    static func read(_ key: String) -> String? {
        if off { return nil }
        return store.withLock { cached in
            if let cached { return cached[key] }
            let loaded = loadFromDisk()
            cached = loaded
            return loaded[key]
        }
    }

    static func write(_ key: String, _ value: String) {
        if off { return }
        store.withLock { cached in
            var secrets = cached ?? loadFromDisk()
            secrets[key] = value
            cached = secrets
            save(secrets)
        }
    }

    static func delete(_ key: String) {
        if off { return }
        store.withLock { cached in
            var secrets = cached ?? loadFromDisk()
            guard secrets.removeValue(forKey: key) != nil else {
                cached = secrets
                return
            }
            cached = secrets
            save(secrets)
        }
    }
}
#else
nonisolated enum Keychain {
    private static let service = "app.yuyue.Yuyue"
    /// In-memory cache (storing both hits and `errSecItemNotFound` misses) so SwiftUI render
    /// passes never storm synchronous `SecItemCopyMatching` IPC on the main thread.
    private static let cache = Mutex<[String: String?]>([:])

    static func read(_ key: String) -> String? {
        if let hit = cache.withLock({ $0[key] }) { return hit }
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: key, kSecReturnData as String: true]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        let value: String? = (status == errSecSuccess) ? (item as? Data).map { String(decoding: $0, as: UTF8.self) } : nil
        if status == errSecSuccess || status == errSecItemNotFound {
            cache.withLock { $0[key] = value }
        }
        return value
    }

    static func write(_ key: String, _ value: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: key]
        SecItemDelete(query as CFDictionary)
        var add = query
        add[kSecValueData as String] = Data(value.utf8)
        let status = SecItemAdd(add as CFDictionary, nil)
        cache.withLock { store in
            if status == errSecSuccess {
                store[key] = value
            } else {
                store.removeValue(forKey: key)
            }
        }
    }

    static func delete(_ key: String) {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service,
                                    kSecAttrAccount as String: key]
        SecItemDelete(query as CFDictionary)
        cache.withLock { $0[key] = .some(nil) }
    }
}
#endif
