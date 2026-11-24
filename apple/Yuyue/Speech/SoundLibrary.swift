import Foundation
import YuyueCore

/// 声音素材库: the room tones and spot sounds 广播剧 lays its scenes with, richer than the ones
/// the core makes from noise. Fetched once from 语阅's own release when a play is first made
/// with 环境音 or 音效 on, checked against its BLAKE3, kept beside the library, and told to the
/// core. Until it's here, or offline, the built-in sounds play as before.
nonisolated enum SoundLibrary {
    static let folderName = "sounds-1"
    static let url = URL(string: "https://github.com/inokoru/yuyue/releases/download/models-1/sounds-1.zip")!
    /// BLAKE3 of the zip (`yuyue digest`); empty until the library is published.
    static let digest = ""

    static func folder(_ dataDir: URL) -> URL { Aligner.folder(dataDir).appending(path: folderName, directoryHint: .isDirectory) }

    static func installed(_ dataDir: URL) -> Bool {
        FileManager.default.fileExists(atPath: folder(dataDir).appending(path: "library.json").path(percentEncoded: false))
    }

    /// Tells the core where the library is, when it's here (cheap to ask again).
    static func register(_ core: YuyueCore, _ dataDir: URL) {
        guard installed(dataDir) else { return }
        _ = core.dramaSetSoundLibrary(dir: folder(dataDir).path(percentEncoded: false))
    }

    /// Fetched, checked and unpacked unless it's here; quietly not, offline.
    @concurrent
    static func ensure(_ core: YuyueCore, _ dataDir: URL) async {
        defer { register(core, dataDir) }
        guard !installed(dataDir) else { return }
        var urls = ModelDownload.candidateURLs(for: url.lastPathComponent, officialURL: url)
        var want: String? = digest
        #if DEBUG
        // `-debugSoundsURL <zip>` (a file or `~/…` in the app's folder): a library built here, taken unchecked.
        if let local = UserDefaults.standard.string(forKey: "debugSoundsURL").flatMap(debugURL) { urls = [local]; want = nil }
        #endif
        if want?.isEmpty == true { return }
        let dir = Aligner.folder(dataDir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let partFile = dir.appending(path: ".\(folderName).zip.part")
        guard (try? await ModelDownload.downloadWithResume(
            urls: urls,
            to: partFile,
            expectedBlake3: want
        ) { _, _ in }) != nil else { return }
        let unpack = dir.appending(path: "unpack-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: unpack) }
        guard (try? unpackModel(zip: partFile.path(percentEncoded: false), to: unpack.path(percentEncoded: false))) != nil else { return }
        try? FileManager.default.removeItem(at: folder(dataDir))
        if (try? FileManager.default.moveItem(at: unpack.appending(path: folderName), to: folder(dataDir))) != nil {
            try? FileManager.default.removeItem(at: partFile)
        }
    }
}
