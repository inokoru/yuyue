import AVFoundation
import Foundation
import Network
import YuyueCore

/// 局域网传录音: the map of a recording syncs, its files don't. A Mac with the recording offers
/// it on the local network (Bonjour `_yuyue-rec._tcp`); a phone that has the map but not the
/// files finds it and takes them over, a piece at a time, then brings them in as the same
/// recording (its id is the files' own), so the map and the progress are there already.
///
/// Only this person's devices: every request carries the secret their devices share through
/// 同步 (`lan_token`), and anything else is refused.
nonisolated enum RecordingShare {
    static let type = "_yuyue-rec._tcp"
    /// Each piece asked for.
    static let piece = 8 << 20
}

#if os(macOS)
/// The Mac's side: answers who has which recording, and hands out pieces of its files.
@MainActor final class RecordingServer {
    static let shared = RecordingServer()
    private var listener: NWListener?
    private var core: YuyueCore?
    private var dataDir: URL?

    func start(core: YuyueCore, dataDir: URL) {
        guard listener == nil, let token = try? core.lanToken() else { return }
        self.core = core
        self.dataDir = dataDir
        let parameters = NWParameters.tcp
        guard let l = try? NWListener(using: parameters) else { return }
        l.service = NWListener.Service(name: Library.deviceName, type: RecordingShare.type)
        l.newConnectionHandler = { [weak self] c in
            MainActor.assumeIsolated { self.map { ShareExchange(connection: c, token: token, server: $0).start() } }
        }
        l.start(queue: .main)
        listener = l
    }

    /// The answer to one request.
    fileprivate func respond(path: String, query: [String: String]) -> (Int, String, Data) {
        guard let core, let id = query["id"] else { return (404, "application/json", Data()) }
        // 局域网传片段与声音工坊: the play made of a book, its designed cards, and /want from a phone.
        if path == "/drama" || path == "/clip" || path == "/card" || path == "/want" {
            return drama(path: path, id: id, query: query, core: core)
        }
        guard let rec = try? core.audioRecording(bookId: id) else { return (404, "application/json", Data()) }
        switch path {
        case "/has":
            let files = rec.files.map { f -> [String: Any] in
                let size = (try? FileManager.default.attributesOfItem(atPath: f.path)[.size] as? Int) ?? 0
                return ["name": URL(filePath: f.path).lastPathComponent, "size": size, "seconds": f.seconds]
            }
            let title = (try? core.book(id: id).summary.title) ?? ""
            let body = (try? JSONSerialization.data(withJSONObject: ["title": title, "files": files])) ?? Data()
            return (200, "application/json", body)
        case "/piece":
            guard let n = query["n"].flatMap(Int.init), rec.files.indices.contains(n),
                  let from = query["from"].flatMap(UInt64.init),
                  let h = try? FileHandle(forReadingFrom: URL(filePath: rec.files[n].path)) else { return (404, "application/json", Data()) }
            defer { try? h.close() }
            try? h.seek(toOffset: from)
            let data = (try? h.read(upToCount: RecordingShare.piece)) ?? Data()
            return (200, "application/octet-stream", data)
        default:
            return (404, "application/json", Data())
        }
    }
}

extension RecordingServer {
    /// `/drama`: the plan and the clips made (only those whose files are here); `/clip`: one
    /// clip's samples, mono 48 kHz float, little-endian.
    fileprivate func drama(path: String, id: String, query: [String: String], core: YuyueCore) -> (Int, String, Data) {
        guard let dataDir else { return (404, "application/json", Data()) }
        switch path {
        case "/drama":
            guard let manifest = (try? core.dramaClipManifest(bookId: id)) ?? nil else { return (404, "application/json", Data()) }
            let keys = core.dramaManifestKeys(manifest: manifest).filter { DramaSynth.clipMade(dataDir, book: id, key: $0) }
            let body = (try? JSONSerialization.data(withJSONObject: ["manifest": manifest, "keys": keys])) ?? Data()
            return (200, "application/json", body)
        case "/card":
            let json = (try? core.cardsPackBookJson(bookId: id)) ?? "[]"
            return (200, "application/json", Data(json.utf8))
        case "/want":
            return (200, "application/json", Data("{\"ok\":true}".utf8))
        default:
            // Sent as float whatever it's kept as, so every version takes it.
            guard let key = query["key"], key.allSatisfy({ $0.isLetter || $0.isNumber }),
                  let samples = DramaSynth.readClip(dataDir, book: id, key: key) else { return (404, "application/json", Data()) }
            let data = samples.withUnsafeBufferPointer { Data(buffer: $0) }
            return (200, "application/octet-stream", data)
        }
    }
}

/// One request on one connection.
private final class ShareExchange {
    let connection: NWConnection
    let token: String
    weak var server: RecordingServer?
    private var buffer = Data()

    init(connection: NWConnection, token: String, server: RecordingServer) {
        self.connection = connection
        self.token = token
        self.server = server
    }

    func start() {
        connection.start(queue: .main)
        read()
    }

    private func read() {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 << 10) { data, _, complete, error in
            MainActor.assumeIsolated {
                if let data { self.buffer.append(data) }
                if error != nil || self.buffer.count > 64 << 10 { return self.connection.cancel() }
                guard let end = self.buffer.firstRange(of: Data("\r\n\r\n".utf8)) else { return complete ? self.connection.cancel() : self.read() }
                let head = String(decoding: self.buffer[..<end.lowerBound], as: UTF8.self).components(separatedBy: "\r\n")
                let parts = head.first?.split(separator: " ") ?? []
                var headers: [String: String] = [:]
                for line in head.dropFirst() {
                    guard let colon = line.firstIndex(of: ":") else { continue }
                    headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
                }
                guard parts.count >= 2, headers["x-yuyue-token"] == self.token, let target = URLComponents(string: String(parts[1])) else {
                    return self.send(403, "text/plain", Data())
                }
                var query: [String: String] = [:]
                for item in target.queryItems ?? [] { query[item.name] = item.value }
                guard let (status, type, body) = self.server?.respond(path: target.path, query: query) else { return self.connection.cancel() }
                self.send(status, type, body)
            }
        }
    }

    private func send(_ status: Int, _ type: String, _ body: Data) {
        let head = "HTTP/1.1 \(status) \(status == 200 ? "OK" : "No")\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\nConnection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in
            MainActor.assumeIsolated { self.connection.cancel() }
        })
    }
}
#endif

/// The phone's side: Macs offering recordings nearby, and taking one over.
@MainActor @Observable final class RecordingFetch {
    static let shared = RecordingFetch()

    struct Offer: Equatable {
        let device: String
        let url: URL
        let files: [(name: String, size: Int, seconds: Double)]
        static func == (a: Offer, b: Offer) -> Bool { a.url == b.url && a.files.map(\.name) == b.files.map(\.name) }
    }

    /// The devices found, by name, and where to ask them.
    private var hosts: [String: URL] = [:]
    private var browser: NWBrowser?
    /// Taking one over: recording id → 0–1.
    private(set) var taking: [String: Double] = [:]
    private(set) var problem: [String: String] = [:]

    func browse() {
        guard browser == nil else { return }
        let b = NWBrowser(for: .bonjour(type: RecordingShare.type, domain: nil), using: .tcp)
        b.browseResultsChangedHandler = { [weak self] results, _ in
            MainActor.assumeIsolated {
                for r in results {
                    guard case let .service(name, _, _, _) = r.endpoint, name != Library.deviceName else { continue }
                    self?.resolve(r.endpoint, name: name)
                }
            }
        }
        b.start(queue: .main)
        browser = b
    }

    /// The service's address, by connecting to it once.
    private func resolve(_ endpoint: NWEndpoint, name: String) {
        let c = NWConnection(to: endpoint, using: .tcp)
        c.stateUpdateHandler = { [weak self] state in
            MainActor.assumeIsolated {
                guard case .ready = state, case let .hostPort(host, port) = c.currentPath?.remoteEndpoint else { return }
                var h = "\(host)"
                if let pct = h.firstIndex(of: "%") { h = String(h[..<pct]) }
                if h.contains(":") { h = "[\(h)]" }
                self?.hosts[name] = URL(string: "http://\(h):\(port)")
                c.cancel()
            }
        }
        c.start(queue: .main)
    }

    private func request(_ base: URL, _ path: String, _ query: [String: String], token: String) -> URLRequest {
        var u = URLComponents(url: base.appending(path: path), resolvingAgainstBaseURL: false)!
        u.queryItems = query.map { URLQueryItem(name: $0.key, value: $0.value) }
        var r = URLRequest(url: u.url!, timeoutInterval: 30)
        r.setValue(token, forHTTPHeaderField: "x-yuyue-token")
        return r
    }

    /// A device nearby that has recording `id`, when one does.
    func offer(for id: String, core: YuyueCore) async -> Offer? {
        guard let token = try? core.lanToken() else { return nil }
        for (device, base) in hosts {
            guard let (data, resp) = try? await URLSession.shared.data(for: request(base, "/has", ["id": id], token: token)),
                  (resp as? HTTPURLResponse)?.statusCode == 200,
                  let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let files = o["files"] as? [[String: Any]] else { continue }
            return Offer(device: device, url: base, files: files.map { ($0["name"] as? String ?? "", $0["size"] as? Int ?? 0, $0["seconds"] as? Double ?? 0) })
        }
        return nil
    }

    /// 局域网传片段: what a Mac nearby has made of a book's play — the device, its manifest and
    /// the clips it has — when one has more than this device.
    struct DramaOffer {
        let device: String
        let url: URL
        let manifest: String
        let keys: [String]
    }

    /// Taking clips over: book → (got, of).
    private(set) var takingClips: [String: (got: Int, of: Int)] = [:]
    /// 从 Mac `/card` 拉到的专属角色声音卡 JSON，若该书正在播放中则暂存到下一章开头再换上。
    @ObservationIgnored private var pendingCardJSON: [String: String] = [:]
    @ObservationIgnored private var activeDramaState: (bookID: String, section: UInt32)?
    @ObservationIgnored private var wantTask: Task<Void, Never>?

    /// 在章节边界调用：把暂存的新角色声音卡正式装进本书，绝不在句中变声。
    func applyPendingCards(for bookID: String, core: YuyueCore) {
        guard let json = pendingCardJSON.removeValue(forKey: bookID) else { return }
        if ((try? core.cardsAdoptBookJson(bookId: bookID, json: json)) ?? 0) > 0 {
            LiveCastCache.shared.invalidate(book: bookID)
        }
    }

    /// 手机正在听某本书的广播剧时，翻章立即发一次 `/want` 并每 60 秒轮询一次附近 Mac。
    func readingDrama(bookID: String, section: UInt32, core: YuyueCore) {
        let changed = activeDramaState?.bookID != bookID || activeDramaState?.section != section
        activeDramaState = (bookID, section)
        #if os(iOS)
        browse()
        if changed || wantTask == nil {
            wantTask?.cancel()
            wantTask = Task { [weak self] in
                while !Task.isCancelled {
                    guard let self, let current = self.activeDramaState, current.bookID == bookID else { return }
                    await self.pingWantAndPull(bookID: current.bookID, section: current.section, core: core)
                    try? await Task.sleep(for: .seconds(60))
                }
            }
        }
        #endif
    }

    func stopReadingDrama(bookID: String? = nil) {
        if let bookID, activeDramaState?.bookID != bookID { return }
        activeDramaState = nil
        wantTask?.cancel()
        wantTask = nil
    }

    private func pingWantAndPull(bookID: String, section: UInt32, core: YuyueCore) async {
        guard let token = try? core.lanToken() else { return }
        for (_, base) in hosts {
            _ = try? await URLSession.shared.data(
                for: request(base, "/want", ["id": bookID, "section": "\(section)"], token: token)
            )
        }
        if let dir = Audiobooks.shared?.dataDir,
           let offer = await dramaOffer(for: bookID, core: core) {
            takeClips(bookID, from: offer, core: core, dataDir: dir) {}
        }
    }

    private func fetchCards(bookID: String, from base: URL, token: String, core: YuyueCore) async {
        guard let (data, resp) = try? await URLSession.shared.data(for: request(base, "/card", ["id": bookID], token: token)),
              (resp as? HTTPURLResponse)?.statusCode == 200,
              let json = String(data: data, encoding: .utf8),
              !json.isEmpty, json != "[]" else { return }
        if activeDramaState?.bookID == bookID {
            pendingCardJSON[bookID] = json
        } else if ((try? core.cardsAdoptBookJson(bookId: bookID, json: json)) ?? 0) > 0 {
            LiveCastCache.shared.invalidate(book: bookID)
        }
    }

    func dramaOffer(for id: String, core: YuyueCore) async -> DramaOffer? {
        guard let token = try? core.lanToken() else { return nil }
        for (device, base) in hosts {
            guard let (data, resp) = try? await URLSession.shared.data(for: request(base, "/drama", ["id": id], token: token)),
                  (resp as? HTTPURLResponse)?.statusCode == 200,
                  let o = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let manifest = o["manifest"] as? String, let keys = o["keys"] as? [String], !keys.isEmpty else { continue }
            return DramaOffer(device: device, url: base, manifest: manifest, keys: keys)
        }
        return nil
    }

    /// Takes the clips this device lacks from `offer`, then has the core play them.
    func takeClips(_ id: String, from offer: DramaOffer, core: YuyueCore, dataDir: URL, done: @escaping () -> Void) {
        guard takingClips[id] == nil, let token = try? core.lanToken() else { return }
        let folder = DramaSynth.clipsFolder(dataDir, book: id)
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let want = offer.keys.filter { !DramaSynth.clipMade(dataDir, book: id, key: $0) }
        // Nothing new (and already taken): still check `/card` lightly, but avoid clip churn.
        if want.isEmpty, core.dramaBorrowedFrom(bookId: id) != nil {
            Task { await fetchCards(bookID: id, from: offer.url, token: token, core: core) }
            return
        }
        takingClips[id] = (0, want.count)
        Task {
            await fetchCards(bookID: id, from: offer.url, token: token, core: core)
            var got = 0
            for k in want {
                guard let (data, resp) = try? await URLSession.shared.data(for: request(offer.url, "/clip", ["id": id, "key": k], token: token)),
                      (resp as? HTTPURLResponse)?.statusCode == 200 else { break }
                let n = data.count / 4
                if let b = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: AVAudioFrameCount(max(1, n))) {
                    b.frameLength = AVAudioFrameCount(n)
                    data.withUnsafeBytes { raw in
                        if n > 0, let src = raw.baseAddress { b.floatChannelData![0].update(from: src.assumingMemoryBound(to: Float.self), count: n) }
                    }
                    try? DramaSynth.writeClip(b, dataDir, book: id, key: k)
                }
                got += 1
                takingClips[id] = (got, want.count)
            }
            let have = offer.keys.filter { DramaSynth.clipMade(dataDir, book: id, key: $0) }
            try? core.dramaAdoptClips(bookId: id, device: offer.device, manifest: offer.manifest, have: have)
            if !have.isEmpty {
                try? core.dramaSetEnabled(bookId: id, enabled: true)
            }
            takingClips[id] = nil
            NotificationCenter.default.post(name: DramaSynth.sectionMade, object: id)
            NotificationCenter.default.post(name: DramaSynth.settingsChanged, object: id)
            done()
        }
    }

    /// Takes recording `id` over from `offer` and brings it onto the shelf.
    func take(_ id: String, from offer: Offer, core: YuyueCore, library: Library) {
        guard taking[id] == nil, let token = try? core.lanToken() else { return }
        taking[id] = 0
        problem[id] = nil
        let total = max(1, offer.files.reduce(0) { $0 + $1.size })
        Task {
            let dir = FileManager.default.temporaryDirectory.appending(path: "take-\(id)", directoryHint: .isDirectory)
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            var got = 0
            var paths: [AudioFile] = []
            do {
                for (n, f) in offer.files.enumerated() {
                    let out = dir.appending(path: f.name)
                    // Going on from what came before, if a take was cut off.
                    var have = (try? FileManager.default.attributesOfItem(atPath: out.path(percentEncoded: false))[.size] as? Int) ?? 0
                    if have == 0 { FileManager.default.createFile(atPath: out.path(percentEncoded: false), contents: nil) }
                    let h = try FileHandle(forWritingTo: out)
                    try h.seekToEnd()
                    while have < f.size {
                        let (data, resp) = try await URLSession.shared.data(for: request(offer.url, "/piece", ["id": id, "n": "\(n)", "from": "\(have)"], token: token))
                        guard (resp as? HTTPURLResponse)?.statusCode == 200, !data.isEmpty else { throw URLError(.badServerResponse) }
                        try h.write(contentsOf: data)
                        have += data.count
                        taking[id] = Double(got + have) / Double(total)
                    }
                    try h.close()
                    got += f.size
                    paths.append(AudioFile(path: out.path(percentEncoded: false), seconds: f.seconds))
                }
                let title = (try? core.book(id: id).summary.title) ?? offer.device
                try await Self.importAudiobook(core, files: paths, title: title)
                AirTraffic.trackBookImported(format: "audio", source: "share_in")
                try? FileManager.default.removeItem(at: dir)
                taking[id] = nil
                library.reload()
                Audiobooks.shared?.reload()
            } catch {
                taking[id] = nil
                problem[id] = String(localized: "没传完，回到同一个 Wi‑Fi 再点一次接着传")
            }
        }
    }

    @concurrent nonisolated private static func importAudiobook(_ core: YuyueCore, files: [AudioFile], title: String) async throws {
        _ = try core.importAudiobook(import: AudioImport(files: files, title: title, authors: [], marks: [], cover: nil))
    }
}
