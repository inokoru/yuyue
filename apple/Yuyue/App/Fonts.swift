import CoreText
import Foundation
import Observation
import SwiftUI
import YuyueCore

/// 字体: every face a book can be read in, by one id each.
///
/// - `system`, `serif`, `kai`: built in;
/// - an id from the core's catalog (`wenkai`, `notoserif`, `notosans`): open source, downloaded
///   the first time it is chosen into the core's fonts folder, where the page loads it from;
/// - `file:<name>`: a TTF / OTF the reader added, copied into the same folder;
/// - `local:<family>`: a face installed on this Mac.
///
/// The app registers the files too, so a face's name and 永 can be drawn in it.
@MainActor @Observable
final class FontShelf {
    static let shared = FontShelf()

    static let builtIn = ["system", "serif", "kai"]
    let catalog: [FontPack] = fontCatalog()

    /// 0–1 while a face downloads.
    private(set) var progress: [String: Double] = [:]
    private(set) var failure: [String: String] = [:]
    /// Catalog faces whose files are all here.
    private(set) var installed: Set<String> = []
    /// Faces added from files, by file name.
    private(set) var files: [String] = []
    @ObservationIgnored private var dir: URL?
    @ObservationIgnored private var tasks: [String: Task<Void, Never>] = [:]
    @ObservationIgnored private var families: [String: String] = [:]

    /// Called once the library is open: knows where fonts go, and registers any already there off the main thread.
    func attach(fontsDir: String) {
        let dir = URL(fileURLWithPath: fontsDir, isDirectory: true)
        self.dir = dir
        installed = Set(catalog.filter { info in info.files.allSatisfy { exists($0.file) } }.map(\.id))
        let known = Set(catalog.flatMap { $0.files.map(\.file) })
        files = ((try? FileManager.default.contentsOfDirectory(atPath: dir.path(percentEncoded: false))) ?? [])
            .filter { Self.isFontFile($0) && !known.contains($0) }
            .sorted()
        let toRegister = catalog.filter { installed.contains($0.id) }.flatMap { $0.files.map(\.file) } + files
        if !toRegister.isEmpty {
            Task { await Self.registerInBackground(dir: dir, names: toRegister) }
        }
    }

    // MARK: What a face is

    func info(_ id: String) -> FontPack? { catalog.first { $0.id == id } }

    /// Its name as the tiles, lists and 样张 say it.
    func name(_ id: String) -> String {
        switch id {
        case "system": return String(localized: "系统")
        case "serif": return String(localized: "宋体")
        case "kai": return String(localized: "楷体")
        default: break
        }
        if let info = info(id) { return info.name }
        if let file = id.fileName { return family(ofFile: file).map { Self.localized($0) } ?? (file as NSString).deletingPathExtension }
        if let family = id.localFamily { return Self.localized(family) }
        return id
    }

    func kind(_ id: String) -> FontKind {
        switch id {
        case "system": return .hei
        case "serif": return .song
        case "kai": return .kai
        default: break
        }
        if let info = info(id) { return info.kind }
        let family = id.localFamily ?? id.fileName.flatMap { self.family(ofFile: $0) } ?? id
        return Self.guessKind(family)
    }

    /// Here and usable: built in, downloaded, added or installed.
    func isReady(_ id: String) -> Bool {
        if Self.builtIn.contains(id) || id.localFamily != nil { return true }
        if let file = id.fileName { return files.contains(file) }
        return installed.contains(id)
    }

    /// The family to draw it in, or the built-in face of its kind until it is here.
    func family(_ id: String) -> String? {
        if !isReady(id) { return Self.fallbackFamily(kind(id)) }
        switch id {
        case "system": return nil
        case "serif": return "Songti SC"
        case "kai": return "Kaiti SC"
        case "wenkai": return "LXGW WenKai Lite"
        case "notoserif": return "Noto Serif SC"
        case "notosans": return "Noto Sans SC"
        default: break
        }
        if let file = id.fileName { return family(ofFile: file) }
        return id.localFamily
    }

    func font(_ id: String, size: CGFloat) -> SwiftUI.Font {
        family(id).map { .custom($0, size: size) } ?? .system(size: size)
    }

    #if os(macOS)
    func nsFont(_ id: String, size: CGFloat) -> NSFont {
        family(id).flatMap { NSFont(name: $0, size: size) ?? NSFontManager.shared.font(withFamily: $0, traits: [], weight: 5, size: size) } ?? .systemFont(ofSize: size)
    }
    #endif

    /// Its size to download, `28 MB`; nil when there is nothing to fetch.
    func size(_ id: String) -> String? {
        guard let info = info(id), !installed.contains(id) else { return nil }
        return "\(Int((Double(info.totalBytes) / 1_000_000).rounded())) MB"
    }

    // MARK: Choosing

    /// The face the settings are in.
    static func id(of s: ReaderSettings) -> String {
        if let face = s.fontFace, !face.isEmpty { return face }
        switch s.font {
        case .serif?: return "serif"
        case .kai?: return "kai"
        case .wenKai?: return "wenkai"
        case .sans?: return "system"
        case nil:
            let preset = PaperAppearance.shared.resolve(s).preset
            return (preset == .plain || preset == .dark) ? "system" : "serif"
        }
    }

    /// Puts a face in the settings: `font` is the built-in face it reads like, so a device without
    /// it shows something close. 霞鹜文楷 is `.wenKai`, as before.
    func apply(_ id: String, to s: inout ReaderSettings) {
        s.fontFace = nil
        switch id {
        case "system": s.font = .sans
        case "serif": s.font = .serif
        case "kai": s.font = .kai
        case "wenkai": s.font = .wenKai
        default:
            s.fontFace = id
            s.font = switch kind(id) {
            case .song, .fang: .serif
            case .kai, .hand: .kai
            case .hei: .sans
            }
        }
    }

    // MARK: Downloading

    /// Chosen: fetched unless it is here or on its way.
    func ensure(_ id: String) {
        guard let info = info(id), !installed.contains(id), tasks[id] == nil, let dir else { return }
        failure[id] = nil
        progress[id] = progress[id] ?? 0
        let total = Double(max(1, info.totalBytes))
        let relay = ModelDownload.ProgressRelay { [weak self] frac in
            guard let self, self.tasks[id] != nil else { return }
            self.progress[id] = min(0.99, frac)
        }
        tasks[id] = Task { [weak self] in
            defer { self?.tasks[id] = nil }
            guard let self else { return }
            do {
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                // Said before the first byte, not after 20 MB of it.
                let free = (try? dir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage
                if let free, free < Int64(info.totalBytes) + 10_000_000 { throw CocoaError(.fileWriteOutOfSpace) }
                var done = 0.0
                for f in info.files {
                    let target = dir.appending(path: f.file)
                    if exists(f.file) { done += Double(f.bytes); continue }
                    let before = done
                    let partFile = dir.appending(path: ".\(f.file).part")
                    guard let officialURL = URL(string: f.url) else { throw URLError(.badURL) }
                    let urls = ModelDownload.candidateURLs(
                        for: f.file,
                        officialURL: officialURL,
                        mirrorPath: "fonts/\(f.file)"
                    )
                    try await ModelDownload.downloadWithResume(
                        urls: urls,
                        to: partFile,
                        expectedBytes: Int64(f.bytes)
                    ) { got, _ in
                        relay.send((before + Double(got)) / total)
                    }
                    try Task.checkCancellation()
                    try? FileManager.default.removeItem(at: target)
                    try FileManager.default.moveItem(at: partFile, to: target)
                    done += Double(f.bytes)
                }
                progress[id] = nil
                installed.insert(id)
                register(info.files.map(\.file))
                NotificationCenter.default.post(name: .fontsChanged, object: nil)
            } catch is CancellationError {
                progress[id] = nil
            } catch {
                progress[id] = nil
                failure[id] = Library.isOutOfSpace(error) ? String(localized: "存储空间不够")
                    : (error as? URLError)?.code == .notConnectedToInternet ? String(localized: "没有联网")
                    : Library.offline(error) != nil ? String(localized: "网断了，没下完")
                    : String(localized: "没下载下来")
            }
        }
    }

    /// Stops a download; what came in whole stays.
    func cancel(_ id: String) {
        tasks[id]?.cancel()
        tasks[id] = nil
        progress[id] = nil
    }

    // MARK: Files

    /// Copies a TTF / OTF into the fonts folder and registers it. Its id, or nil if it isn't a font.
    func add(file url: URL) -> String? {
        guard let dir, Self.isFontFile(url.lastPathComponent) else { return nil }
        let reading = url.startAccessingSecurityScopedResource()
        defer { if reading { url.stopAccessingSecurityScopedResource() } }
        guard let descriptors = CTFontManagerCreateFontDescriptorsFromURL(url as CFURL) as? [CTFontDescriptor], !descriptors.isEmpty else { return nil }
        let rawBase = (url.lastPathComponent as NSString).deletingPathExtension
        let base = Self.sanitizedBaseName(rawBase)
        let ext = url.pathExtension.lowercased()
        var name = "\(base).\(ext)"
        var n = 2
        while FileManager.default.fileExists(atPath: dir.appending(path: name).path(percentEncoded: false)) {
            name = "\(base) \(n).\(ext)"
            n += 1
        }
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try FileManager.default.copyItem(at: url, to: dir.appending(path: name))
        } catch {
            return nil
        }
        files.append(name)
        files.sort()
        register([name])
        NotificationCenter.default.post(name: .fontsChanged, object: nil)
        return "file:\(name)"
    }

    /// Deletes a downloaded or added face's files. Built-in and installed faces have none.
    func delete(_ id: String) {
        guard let dir else { return }
        let names = info(id)?.files.map(\.file) ?? id.fileName.map { [$0] } ?? []
        for name in names {
            let url = dir.appending(path: name)
            CTFontManagerUnregisterFontsForURL(url as CFURL, .process, nil)
            try? FileManager.default.removeItem(at: url)
        }
        installed.remove(id)
        if let file = id.fileName { files.removeAll { $0 == file } }
        NotificationCenter.default.post(name: .fontsChanged, object: nil)
    }

    #if os(macOS)
    /// Faces installed on this Mac that set Chinese, besides the ones above. `local:<family>`.
    func macFaces() -> [String] {
        let skip = ["Songti SC", "Kaiti SC", "PingFang SC", "LXGW WenKai Lite", "Noto Serif SC", "Noto Sans SC", "STSong", "STKaiti", "Heiti SC", "Hiragino Sans GB"]
        let ours = Set(files.compactMap { family(ofFile: $0) })
        return NSFontManager.shared.availableFontFamilies
            .filter { !$0.hasPrefix(".") && !skip.contains($0) && !ours.contains($0) }
            // The 繁体 twins (Heiti TC, Baoli TC…) of faces listed already.
            .filter { f in ![" TC", " HK", " TW"].contains { f.hasSuffix($0) } }
            .filter { family in
                let font = CTFontCreateWithName(family as CFString, 12, nil)
                let langs = CTFontCopySupportedLanguages(font) as? [String] ?? []
                return langs.contains { $0.hasPrefix("zh-Hans") || $0 == "zh" }
            }
            .sorted { Self.localized($0).localizedStandardCompare(Self.localized($1)) == .orderedAscending }
            .map { "local:\($0)" }
    }
    #endif

    // MARK: -

    private func exists(_ file: String) -> Bool {
        guard let dir else { return false }
        return FileManager.default.fileExists(atPath: dir.appending(path: file).path(percentEncoded: false))
    }

    private func register(_ names: [String]) {
        guard let dir else { return }
        for name in names {
            CTFontManagerRegisterFontsForURL(dir.appending(path: name) as CFURL, .process, nil)
        }
    }

    @concurrent nonisolated private static func registerInBackground(dir: URL, names: [String]) async {
        for name in names {
            CTFontManagerRegisterFontsForURL(dir.appending(path: name) as CFURL, .process, nil)
        }
    }

    private func family(ofFile name: String) -> String? {
        if let known = families[name] { return known }
        guard let dir,
              let d = (CTFontManagerCreateFontDescriptorsFromURL(dir.appending(path: name) as CFURL) as? [CTFontDescriptor])?.first,
              let family = CTFontDescriptorCopyAttribute(d, kCTFontFamilyNameAttribute) as? String else { return nil }
        families[name] = family
        return family
    }

    /// The name a family goes by in the system's language: 圆体 for Yuanti SC.
    static func localized(_ family: String) -> String {
        let font = CTFontCreateWithName(family as CFString, 12, nil)
        return CTFontCopyLocalizedName(font, kCTFontFamilyNameKey, nil) as String? ?? family
    }

    static func guessKind(_ family: String) -> FontKind {
        let f = family.lowercased()
        if f.contains("kai") || f.contains("楷") || f.contains("wenkai") { return .kai }
        if f.contains("fang") || f.contains("仿") { return .fang }
        if f.contains("pen") || f.contains("xingkai") || f.contains("hand") || f.contains("手") || f.contains("行") { return .hand }
        if f.contains("sans") || f.contains("hei") || f.contains("黑") || f.contains("yuan") || f.contains("圆") || f.contains("ping") { return .hei }
        return .song
    }

    static func fallbackFamily(_ kind: FontKind) -> String? {
        switch kind {
        case .song, .fang: "Songti SC"
        case .kai, .hand: "Kaiti SC"
        case .hei: nil
        }
    }

    nonisolated static func sanitizedBaseName(_ raw: String) -> String {
        let stripped: Set<Character> = ["\"", "'", "<", ">", ";", "{", "}"]
        let replaced = String(raw.compactMap { ch -> Character? in
            if ch == "/" || ch == "\\" { return "-" }
            if stripped.contains(ch) { return nil }
            return ch
        })
        var cleaned = replaced.trimmingCharacters(in: .whitespacesAndNewlines)
        while cleaned.hasPrefix(".") {
            cleaned.removeFirst()
            cleaned = cleaned.trimmingCharacters(in: .whitespacesAndNewlines)
        }
        return cleaned.isEmpty ? "font" : cleaned
    }

    nonisolated static func isFontFile(_ name: String) -> Bool {
        let ext = (name as NSString).pathExtension.lowercased()
        return ["ttf", "otf", "ttc"].contains(ext) && !name.hasPrefix(".")
    }
}

extension String {
    /// `file:<name>` → the name.
    var fileName: String? { hasPrefix("file:") ? String(dropFirst(5)) : nil }
    /// `local:<family>` → the family.
    var localFamily: String? { hasPrefix("local:") ? String(dropFirst(6)) : nil }
}

extension Notification.Name {
    /// A face arrived or went: pages already open load again to use it.
    static let fontsChanged = Notification.Name("yuyue.fontsChanged")
}

// MARK: - Before a face is here

/// A few characters of a face not yet downloaded, from Google Fonts' subsetting (`text=`), so
/// 字样册 shows what it looks like. Cached per face and text; nothing when it can't be fetched.
@MainActor @Observable
final class FontPreview {
    static let shared = FontPreview()
    private(set) var faces: [String: CTFont] = [:]
    @ObservationIgnored private var asked: Set<String> = []

    func font(_ id: String, text: String, size: CGFloat) -> SwiftUI.Font? {
        guard let f = faces["\(id)|\(text)"] else { return nil }
        return SwiftUI.Font(CTFontCreateCopyWithAttributes(f, size, nil, nil))
    }

    func load(_ id: String, text: String) {
        let key = "\(id)|\(text)"
        guard !asked.contains(key), let family = FontShelf.shared.info(id)?.previewFamily else { return }
        asked.insert(key)
        Task {
            guard let data = await Self.fetch(family: family, text: text),
                  let d = CTFontManagerCreateFontDescriptorFromData(data as CFData) else { return }
            faces[key] = CTFontCreateWithFontDescriptor(d, 16, nil)
        }
    }

    nonisolated private static func fetch(family: String, text: String) async -> Data? {
        var q = URLComponents(string: "https://fonts.googleapis.com/css2")!
        q.queryItems = [URLQueryItem(name: "family", value: family), URLQueryItem(name: "text", value: String(Set(text)))]
        var css = URLRequest(url: q.url!)
        // An old browser's name gets TrueType back, which CoreText reads.
        css.setValue("Mozilla/4.0", forHTTPHeaderField: "User-Agent")
        guard let (body, _) = try? await URLSession.shared.data(for: css),
              let text = String(data: body, encoding: .utf8),
              let start = text.range(of: "url(")?.upperBound,
              let end = text[start...].firstIndex(of: ")"),
              let url = URL(string: String(text[start..<end])),
              let (data, response) = try? await URLSession.shared.data(from: url),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return data
    }
}

extension FontPack {
    var totalBytes: UInt64 { files.reduce(0) { $0 + $1.bytes } }
}
