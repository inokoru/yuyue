import Foundation
import UniformTypeIdentifiers
import YuyueCore

/// 数据快照: making one, the ones made before, and bringing one back. The files live where the
/// person can see them: on iPhone in 文件 › 语阅, on Mac in the app's 文稿 folder.
@Observable
final class Snapshots {
    nonisolated struct Saved: Identifiable, Sendable {
        let url: URL
        let about: SnapshotAbout
        var id: URL { url }
    }

    let library: Library
    private(set) var counts: SnapshotCounts?
    private(set) var saved: [Saved] = []
    private(set) var making = false
    var parts = SnapshotParts(progress: true, notes: true, words: true, settings: true, files: false)
    var problem: String?

    init(library: Library) {
        self.library = library
    }

    nonisolated static let fileExtension = "yuyue"

    var folder: URL {
        #if DEBUG
        // A scratch library keeps its snapshots to itself.
        if UserDefaults.standard.string(forKey: "debugLibrary") != nil {
            return library.dataDir.appending(path: "snapshots", directoryHint: .isDirectory)
        }
        #endif
        return URL.documentsDirectory
    }

    func load() async {
        let (counts, saved) = await Self.loadSnapshots(library.core, folder: folder)
        self.counts = counts
        self.saved = saved
    }

    @concurrent nonisolated private static func loadSnapshots(_ core: YuyueCore, folder: URL) async -> (SnapshotCounts?, [Saved]) {
        let counts = try? core.snapshotCounts()
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil)) ?? []
        let saved = files.filter { $0.pathExtension == fileExtension }
            .compactMap { url in (try? core.peekSnapshot(path: url.path(percentEncoded: false))).map { Saved(url: url, about: $0) } }
            .sorted { $0.about.madeAt > $1.about.madeAt }
        return (counts, saved)
    }

    /// What a snapshot of the chosen parts comes to, roughly: the registers compress well.
    var estimate: Int64 {
        guard let counts else { return 0 }
        let data = Int64(Double(counts.dataBytes) * 0.15)
        return data + (parts.files ? Int64(counts.fileBytes) : 0)
    }

    func make() async {
        making = true
        problem = nil
        defer { making = false }
        let url = folder.appending(path: Self.name(Date()))
        let core = library.core
        let parts = parts
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try await Self.writeSnapshot(core, path: url.path(percentEncoded: false), parts: parts)
            await load()
            NotificationCenter.default.post(name: .snapshotsChanged, object: self)
            library.note(icon: "checkmark", String(localized: "已生成快照"), "")
        } catch {
            problem = Library.message(for: error)
        }
    }

    func delete(_ s: Saved) {
        try? FileManager.default.removeItem(at: s.url)
        saved.removeAll { $0.url == s.url }
        NotificationCenter.default.post(name: .snapshotsChanged, object: self)
    }

    /// Reads one picked from 文件 or dropped in, copying it out of where it was first.
    func preview(_ picked: URL) throws -> (URL, SnapshotInfo) {
        let scoped = picked.startAccessingSecurityScopedResource()
        defer { if scoped { picked.stopAccessingSecurityScopedResource() } }
        let local: URL
        if picked.deletingLastPathComponent().standardizedFileURL == folder.standardizedFileURL {
            local = picked
        } else {
            local = FileManager.default.temporaryDirectory.appending(path: picked.lastPathComponent)
            try? FileManager.default.removeItem(at: local)
            try FileManager.default.copyItem(at: picked, to: local)
        }
        return (local, try library.core.readSnapshot(path: local.path(percentEncoded: false)))
    }

    /// 合并 or 覆盖. Before 覆盖 the library as it is goes into a snapshot of its own, so a
    /// wrong choice can be taken back.
    func restore(_ url: URL, replace: Bool) async throws {
        let core = library.core
        if replace {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            let backup = folder.appending(path: Self.name(Date(), note: String(localized: "覆盖前")))
            let all = SnapshotParts(progress: true, notes: true, words: true, settings: true, files: false)
            try await Self.writeSnapshot(core, path: backup.path(percentEncoded: false), parts: all)
        }
        try await Self.applySnapshot(core, path: url.path(percentEncoded: false), replace: replace)
        library.reload()
        NotificationCenter.default.post(name: .notesChanged, object: nil)
        NotificationCenter.default.post(name: .vocabChanged, object: nil)
        await load()
        NotificationCenter.default.post(name: .snapshotsChanged, object: self)
        library.note(icon: "checkmark", String(localized: "快照导入好了"), "")
    }

    @concurrent nonisolated private static func writeSnapshot(_ core: YuyueCore, path: String, parts: SnapshotParts) async throws {
        _ = try core.makeSnapshot(path: path, parts: parts)
    }

    @concurrent nonisolated private static func applySnapshot(_ core: YuyueCore, path: String, replace: Bool) async throws {
        _ = try core.restoreSnapshot(path: path, replace: replace)
    }

    static func name(_ date: Date, note: String? = nil) -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd-HHmmss"
        return String(localized: "语阅快照-\(note.map { "\($0)-" } ?? "")\(f.string(from: date)).\(fileExtension)")
    }

    // MARK: Words for the pages

    /// `10 月 2 日 21:40`, with the year when it isn't this one.
    static func when(_ ms: Int64) -> String {
        let date = Date(timeIntervalSince1970: Double(ms) / 1000)
        let cal = Calendar.current
        let c = cal.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        let year = c.year == cal.component(.year, from: Date()) ? "" : String(localized: "\(c.year ?? 0) 年 ")
        // The time as the system writes it: 18:32, or 6:32 PM.
        return String(localized: "\(year)\(c.month ?? 0) 月 \(c.day ?? 0) 日 ") + date.formatted(date: .omitted, time: .shortened)
    }

    /// `10 月 2 日`, for a row's value.
    static func day(_ ms: Int64) -> String {
        let c = Calendar.current.dateComponents([.month, .day], from: Date(timeIntervalSince1970: Double(ms) / 1000))
        return String(localized: "\(c.month ?? 0) 月 \(c.day ?? 0) 日")
    }

    static func size(_ bytes: UInt64) -> String {
        ByteCountFormatter.string(fromByteCount: Int64(bytes), countStyle: .file)
    }

    static func number(_ n: UInt32) -> String {
        n.formatted(.number.locale(Locale(identifier: "en_US")))
    }

    static func books(_ n: UInt32) -> String {
        n == 1 ? String(localized: "1 本") : String(localized: "\(number(n)) 本")
    }

    static func items(_ n: UInt32) -> String {
        n == 1 ? String(localized: "1 条") : String(localized: "\(number(n)) 条")
    }

    static func words(_ n: UInt32) -> String {
        n == 1 ? String(localized: "1 个词") : String(localized: "\(number(n)) 个词")
    }

    /// `这台 iPhone` for this device's own, the device's name otherwise.
    static func from(_ about: SnapshotAbout) -> String { from(about.deviceName) }

    static func from(_ device: String) -> String {
        device == Library.deviceName ? String(localized: "这台 \(Library.platformWord)") : device
    }

    static func nothingToImport(_ info: SnapshotInfo, replace: Bool) -> Bool {
        !replace && info.merged == info.now && !info.parts.settings && !info.parts.files
    }

    /// What 合并 or 覆盖 would do to the counts that change, in a line.
    static func change(_ info: SnapshotInfo, replace: Bool) -> String {
        let after = replace ? info.replaced : info.merged
        if after == info.now, !replace { return String(localized: "这份快照里的，这台设备上都已经有了") }
        var parts: [String] = []
        if info.parts.notes { parts.append(String(localized: "笔记 \(number(info.now.notes)) → \(number(after.notes)) 条")) }
        if info.parts.words { parts.append(String(localized: "生词 \(number(info.now.words)) → \(number(after.words)) 个")) }
        if parts.isEmpty || after.books != info.now.books { parts.insert(String(localized: "书 \(number(info.now.books)) → \(number(after.books)) 本"), at: 0) }
        return (replace ? String(localized: "覆盖后：") : String(localized: "合并后：")) + parts.joined(separator: String(localized: "，"))
    }
}

extension UTType {
    /// A 语阅 snapshot: `.yuyue`, a zip inside.
    static let yuyueSnapshot = UTType(exportedAs: "app.yuyue.snapshot", conformingTo: .data)
}

extension Notification.Name {
    /// A snapshot made, deleted or restored from: every list of them reads the folder again.
    static let snapshotsChanged = Notification.Name("yuyue.snapshotsChanged")
}
