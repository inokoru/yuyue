import AVFoundation
import CoreML
import Foundation
import Synchronization
import YuyueCore

/// 声纹: a speaker-embedding model (CAM++ and its kind, as Core ML) that turns a stretch of voice
/// into a print. 选角 keeps the voices in one scene apart by them; 回听 notices a clip that
/// doesn't sound like its voice. The core makes the model's input and keeps each voice's print.
///
/// The model (3D-Speaker's CAM++, Chinese common, Apache-2.0; about 13 MB) is fetched from 语阅's
/// own release the first time a play is made, checked against its BLAKE3, and kept beside the
/// library. Until it's here, or if it can't be fetched, everything answers nil and both work as
/// before.
nonisolated enum SpeakerPrint {
    static let file = "SpeakerPrint.mlmodelc"
    /// The model's name, so prints from another model aren't compared with its own.
    static let model = "campplus-1"
    static let url = URL(string: "https://github.com/inokoru/yuyue/releases/download/models-1/SpeakerPrint.mlmodelc.zip")!
    /// BLAKE3 of the zip (`yuyue digest`).
    static let digest = "802f366a78860dd826ee34ec7861ad0e45100782ff43ebb2fc2ca3f1555d5fb5"

    static func url(_ dataDir: URL) -> URL { Aligner.folder(dataDir).appending(path: file) }

    static func installed(_ dataDir: URL) -> Bool {
        FileManager.default.fileExists(atPath: url(dataDir).path(percentEncoded: false))
    }

    /// Fetched, checked and unpacked unless it's here; quietly not, offline.
    @concurrent
    static func ensure(_ dataDir: URL) async {
        #if DEBUG
        if UserDefaults.standard.string(forKey: "debugSpeakerURL") != nil { return await debugInstall(dataDir) }
        #endif
        guard !installed(dataDir) else { return }
        let dir = Aligner.folder(dataDir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let partFile = dir.appending(path: ".\(file).zip.part")
        let urls = ModelDownload.candidateURLs(for: url.lastPathComponent, officialURL: url)
        guard (try? await ModelDownload.downloadWithResume(
            urls: urls,
            to: partFile,
            expectedBlake3: digest
        ) { _, _ in }) != nil else { return }
        let unpack = dir.appending(path: "unpack-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: unpack) }
        guard (try? unpackModel(zip: partFile.path(percentEncoded: false), to: unpack.path(percentEncoded: false))) != nil else { return }
        try? FileManager.default.removeItem(at: url(dataDir))
        if (try? FileManager.default.moveItem(at: unpack.appending(path: file), to: url(dataDir))) != nil {
            try? FileManager.default.removeItem(at: partFile)
        }
    }

    #if DEBUG
    /// `-debugSpeakerURL <zip>` (a file or `~/…` in the app's folder): unpacked beside the library.
    @concurrent
    static func debugInstall(_ dataDir: URL) async {
        guard !installed(dataDir), let from = UserDefaults.standard.string(forKey: "debugSpeakerURL").flatMap(debugURL) else { return }
        let dir = Aligner.folder(dataDir)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let temp: URL
        if from.isFileURL {
            temp = FileManager.default.temporaryDirectory.appending(path: UUID().uuidString)
            guard (try? FileManager.default.copyItem(at: from, to: temp)) != nil else { return }
        } else {
            guard let (downloaded, _) = try? await URLSession.shared.download(from: from) else { return }
            temp = downloaded
        }
        defer { try? FileManager.default.removeItem(at: temp) }
        let unpack = dir.appending(path: "unpack-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer { try? FileManager.default.removeItem(at: unpack) }
        guard (try? unpackModel(zip: temp.path(percentEncoded: false), to: unpack.path(percentEncoded: false))) != nil else { return }
        try? FileManager.default.moveItem(at: unpack.appending(path: file), to: url(dataDir))
    }
    #endif

    private static let loaded = Mutex<(URL, MLModel)?>(nil)

    private static func load(_ dataDir: URL) -> MLModel? {
        let u = url(dataDir)
        guard installed(dataDir) else { return nil }
        return loaded.withLock { held in
            if let held, held.0 == u { return held.1 }
            let config = MLModelConfiguration()
            config.computeUnits = .all
            guard let m = try? MLModel(contentsOf: u, configuration: config) else { return nil }
            held = (u, m)
            return m
        }
    }

    /// The print of `samples` at `rate` (mono); nil without the model, or for under half a second.
    static func print(_ core: YuyueCore, _ dataDir: URL, samples: [Float], rate: Double) async -> [Float]? {
        guard let m = load(dataDir) else { return nil }
        let f = core.speakerFeatures(samples: samples, rate: UInt32(rate))
        guard f.frames >= 50,
              let input = try? MLMultiArray(shape: [1, NSNumber(value: f.frames), 80], dataType: .float32) else { return nil }
        input.withUnsafeMutableBufferPointer(ofType: Float.self) { p, _ in
            for i in f.data.indices { p[i] = f.data[i] }
        }
        guard let out = try? await m.prediction(from: MLDictionaryFeatureProvider(dictionary: ["fbank": input])),
              let e = out.featureValue(for: "embedding")?.multiArrayValue else { return nil }
        var v: [Float] = []
        let n = e.count
        if e.dataType == .float16 {
            e.withUnsafeBufferPointer(ofType: Float16.self) { p in v = (0..<n).map { Float(p[$0]) } }
        } else {
            e.withUnsafeBufferPointer(ofType: Float.self) { p in v = Array(p.prefix(n)) }
        }
        return v
    }

    /// Each of `voices` that has no print yet reads the sample sentence once, and its print is
    /// kept (by the core) for 选角 and 回听.
    static func printVoices(_ core: YuyueCore, _ dataDir: URL, voices: [VoiceInfo], language: String) async {
        guard installed(dataDir) else { return }
        core.speakerModel(model: model)
        // 声音卡自带或造好时已有 embedding，直接写入避免重复试念量取。
        for v in voices where !v.embedding.isEmpty {
            core.speakerSetPrint(voice: v.id, embedding: v.embedding)
        }
        // Cloud voices cost money to read: they go without.
        let ids = core.speakerUnprinted(voices: voices.map(\.id).filter { !$0.hasPrefix("cloud.") })
        let text = core.speakerSampleText(language: language)
        for id in ids {
            guard !Task.isCancelled else { return }
            guard let voice = await MainActor.run(body: { Voices.find(id) }),
                  let speech = try? await voice.render(text, language: language) else { continue }
            let n = Int(speech.buffer.frameLength)
            let samples = speech.buffer.floatChannelData.map { Array(UnsafeBufferPointer(start: $0[0], count: n)) } ?? []
            if let p = await print(core, dataDir, samples: samples, rate: speech.buffer.format.sampleRate) {
                core.speakerSetPrint(voice: id, embedding: p)
            }
        }
    }
}
