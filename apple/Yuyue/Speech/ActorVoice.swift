import AVFoundation
import CryptoKit
import Foundation
import YuyueCore

/// 本机会演的声音: a voice model on this Mac that acts as it reads (Qwen3-TTS and its kind),
/// served on the Mac itself with the speech endpoint OpenAI's voices use — the line, the voice,
/// and how to say it as an instruction. 广播剧 casts it like a cloud voice that acts, without
/// the network or the cost.
///
/// Not offered in the voice lists yet: while the model is tried it's set up by debug arguments —
/// `-debugActorURL http://127.0.0.1:8880/v1 -debugActorModel <name>
/// -debugActorVoices "Vivian:female:young,Ryan:male:middle,…"`.
nonisolated struct ActorVoice: Voice {
    let voiceID: String
    let name: String
    let gender: String?
    let age: String?
    let id: String

    init(voiceID: String, gender: String?, age: String?) {
        self.voiceID = voiceID
        name = voiceID
        self.gender = gender
        self.age = age
        id = "actor.\(voiceID)"
    }

    var qualityName: String? { String(localized: "本机") }
    var isOffline: Bool { true }
    func speaks(_ language: String) -> Bool { true }

    struct Server: Sendable {
        let url: URL
        let model: String
    }

    static var server: Server? {
        #if DEBUG
        let d = UserDefaults.standard
        guard let u = d.string(forKey: "debugActorURL").flatMap(URL.init(string:)) else { return nil }
        return Server(url: u, model: d.string(forKey: "debugActorModel") ?? "qwen3-tts")
        #else
        return nil
        #endif
    }

    /// The voices the server has, as the debug arguments list them, and the ones made for books.
    static func all() -> [ActorVoice] {
        guard server != nil else { return [] }
        let list = (UserDefaults.standard.string(forKey: "debugActorVoices") ?? "") + "," + (UserDefaults.standard.string(forKey: designedKey) ?? "")
        return list.split(separator: ",").compactMap { item in
            let p = item.split(separator: ":").map { $0.trimmingCharacters(in: .whitespaces) }
            guard let v = p.first, !v.isEmpty else { return nil }
            return ActorVoice(voiceID: v, gender: p.count > 1 ? p[1] : nil, age: p.count > 2 ? p[2] : nil)
        }
    }

    private static let designedKey = "actor.designed"

    /// 造声音: whether the server makes voices from a description (`-debugActorDesign 1`).
    static var canDesign: Bool { server != nil && UserDefaults.standard.bool(forKey: "debugActorDesign") }

    /// 造声音: a voice made from a description (the cast's: sex, age, how they speak), tried on
    /// `sample`; kept with the server's voices. The server answers `POST voices/design` with
    /// `{"voice": id}`. nil when it can't.
    static func design(description: String, sample: String, language: String) async throws -> ActorVoice? {
        guard let server, canDesign else { return nil }
        var request = URLRequest(url: server.url.appending(path: "voices/design"), timeoutInterval: 600)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        request.httpBody = try JSONSerialization.data(withJSONObject: ["model": server.model, "description": description, "sample_text": sample, "language": language])
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200,
              let o = try JSONSerialization.jsonObject(with: data) as? [String: Any], let id = o["voice"] as? String, !id.contains(",") else { return nil }
        let lower = description.lowercased()
        let gender: String? = lower.contains("female") || description.contains("女") ? "female" : lower.contains("male") || description.contains("男") ? "male" : nil
        let entry = [id, gender ?? ""].joined(separator: ":")
        let d = UserDefaults.standard
        let kept = d.string(forKey: designedKey) ?? ""
        if !kept.split(separator: ",").contains(where: { $0.hasPrefix(id + ":") || $0 == id }) {
            d.set(kept.isEmpty ? entry : kept + "," + entry, forKey: designedKey)
        }
        return ActorVoice(voiceID: id, gender: gender, age: nil)
    }

    func render(_ text: String, language: String) async throws -> Speech? {
        try await render(text, instruction: "")
    }

    /// A line of a play, said as `instruction` asks (empty: plainly). Kept, as the cloud voices'
    /// are: the same line asked again isn't made again.
    func render(_ text: String, instruction: String) async throws -> Speech? {
        guard text.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) else { return nil }
        guard let server = Self.server else { throw CloudVoice.Failure.unreadable }
        let hash = SHA256.hash(data: Data((id + "\u{1}" + server.model + "\u{1}" + text + "\u{2}" + instruction).utf8)).prefix(16).map { String(format: "%02x", $0) }.joined()
        let file = CloudVoice.cacheDir.appending(path: "actor").appending(path: "\(hash).wav")
        if let data = try? Data(contentsOf: file) { return try CloudVoice.decode(data) }
        var request = URLRequest(url: server.url.appending(path: "audio/speech"), timeoutInterval: 300)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        var body: [String: Any] = ["model": server.model, "voice": voiceID, "input": text, "response_format": "wav"]
        if !instruction.isEmpty { body["instructions"] = instruction }
        request.httpBody = try JSONSerialization.data(withJSONObject: body)
        let (data, response) = try await URLSession.shared.data(for: request)
        guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw CloudVoice.Failure.unreadable }
        try? FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? data.write(to: file, options: .atomic)
        return try CloudVoice.decode(data)
    }
}
