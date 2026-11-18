import AVFoundation
import CryptoKit
import Foundation

/// 生成音效: a sound-making model (Stable Audio Open and its kind) for the sounds a book writes
/// that neither the built-in library nor the downloaded one has — a goose at the gate, a bead
/// curtain. It runs on this Mac and is asked over HTTP: `POST generate` with `{"prompt",
/// "seconds"}`, answered with a WAV.
///
/// Not offered in settings yet: while the model is tried it's set up by `-debugSfxURL
/// http://127.0.0.1:8890`. Without it the core asks for nothing and those sounds stay unheard.
nonisolated enum SfxMaker {
    static var url: URL? {
        #if DEBUG
        UserDefaults.standard.string(forKey: "debugSfxURL").flatMap(URL.init(string:))
        #else
        nil
        #endif
    }
    static var available: Bool { url != nil }

    /// The sound, at the voices' format (48 kHz mono); kept, so it's made once.
    static func make(description: String, seconds: Double) async throws -> Speech? {
        guard let url else { return nil }
        let hash = SHA256.hash(data: Data((description + "\u{1}\(seconds)").utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
        let file = CloudVoice.cacheDir.appending(path: "sfx").appending(path: "\(hash).wav")
        if let data = try? Data(contentsOf: file) { return try CloudVoice.decode(data) }
        var request = URLRequest(url: url.appending(path: "generate"), timeoutInterval: 600)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["prompt": description, "seconds": seconds])
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw CloudVoice.Failure.unreadable }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        return try CloudVoice.decode(data)
    }
}
