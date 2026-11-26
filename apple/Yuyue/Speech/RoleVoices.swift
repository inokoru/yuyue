import AVFoundation
import YuyueCore

/// 分角色朗读: each sentence read by the voice of whoever says it. Nobody has
/// one of their own until the reader gives it: by default everything is the narrator's, so a
/// book sounds as it always did. Lines the script isn't sure of stay the narrator's too.
@MainActor final class BookRoles {
    private let core: YuyueCore
    private let bookID: String
    private var roles: [Role] = []
    private var speakers: [UInt32: [String?]] = [:]

    init(core: YuyueCore, bookID: String) {
        self.core = core
        self.bookID = bookID
        reload()
    }

    func reload() {
        roles = (try? core.bookRoles(bookId: bookID)) ?? []
        speakers = [:]
    }

    /// Whether anyone has a voice, pace or pitch of their own: when not, the script isn't even
    /// read, and the book plays exactly as before.
    var any: Bool { !roles.isEmpty }

    func role(_ name: String) -> RoleVoice? { roles.first { $0.name == name }?.voice }

    nonisolated enum RoleVoiceError: LocalizedError {
        case voiceUnavailable(String)

        var errorDescription: String? {
            switch self {
            case .voiceUnavailable(let name):
                String(localized: "角色「\(name)」设定的声音当前不可用，请在人物声音中重选")
            }
        }
    }

    /// Resolves a character's voice (and rate/pitch tuning) if the reader configured anything for them.
    func voiceForRole(_ name: String, language: String, narrator: () -> (any Voice)?) throws -> (any Voice)? {
        guard let role = role(name) else { return nil }
        var base: (any Voice)?
        if let id = role.voice, !id.isEmpty {
            guard let v = Voices.find(id, bookID: bookID),
                  v.speaks(language),
                  (v as? ClonedVoice)?.isReady ?? true else {
                throw RoleVoiceError.voiceUnavailable(name)
            }
            base = v
        }
        if base == nil, abs(role.rate - 1) < 0.01, abs(role.pitch) < 0.01 { return nil }
        guard let voice = base ?? narrator() else { return nil }
        if abs(role.rate - 1) < 0.01, abs(role.pitch) < 0.01 { return voice }
        return TunedVoice(base: voice, rate: Float(role.rate), pitch: Float(role.pitch))
    }

    /// The voice for one sentence, or nil for the narrator's.
    func voice(section: UInt32, sentence: Int, language: String, narrator: () -> (any Voice)?) -> (any Voice)? {
        guard any else { return nil }
        if speakers[section] == nil { speakers[section] = (try? core.sectionRoles(bookId: bookID, section: section)) ?? [] }
        guard let list = speakers[section], list.indices.contains(sentence), let who = list[sentence] else { return nil }
        return try? voiceForRole(who, language: language, narrator: narrator)
    }

    /// Segment-level plan for manual role reading (used when 广播剧模式 is off): splits
    /// `"宝玉笑道：「你好。」"` into narrator + character speech so narration tags stay in the
    /// narrator's voice and two speakers in one sentence don't overwrite each other.
    /// Returns nil when the whole sentence uses a single narrator voice.
    func segmentPlan(section: UInt32, sentence: Int, language: String, narrator: () -> (any Voice)?) throws -> [(voice: any Voice, text: String)]? {
        guard any, let defaultVoice = narrator() else { return nil }
        guard let segs = try? core.roleSentenceSegments(bookId: bookID, section: section, sentence: UInt32(sentence)),
              !segs.isEmpty else {
            return nil
        }
        var hasCustom = false
        var out: [(voice: any Voice, text: String)] = []
        for seg in segs {
            if let who = seg.role, let custom = try voiceForRole(who, language: language, narrator: { defaultVoice }) {
                hasCustom = true
                out.append((custom, seg.text))
            } else {
                out.append((defaultVoice, seg.text))
            }
        }
        return hasCustom ? out : nil
    }

    /// Renders a multi-segment manual-role sentence into one `Speech` buffer with a short breath
    /// between speaker changes.
    @concurrent nonisolated static func renderSegments(_ plan: [(voice: any Voice, text: String)], language: String) async throws -> Speech? {
        if plan.count == 1 {
            return try await plan[0].voice.render(plan[0].text, language: language)
        }
        var buffers: [AVAudioPCMBuffer] = []
        let gapFrames = AVAudioFrameCount(Speech.format.sampleRate * 0.08)
        for (idx, item) in plan.enumerated() {
            try Task.checkCancellation()
            guard let rendered = try await item.voice.render(item.text, language: language),
                  rendered.buffer.frameLength > 0 else { continue }
            if !buffers.isEmpty, idx > 0, let gap = AVAudioPCMBuffer(pcmFormat: Speech.format, frameCapacity: gapFrames) {
                gap.frameLength = gapFrames
                if let ch = gap.floatChannelData { ch[0].update(repeating: 0, count: Int(gapFrames)) }
                buffers.append(gap)
            }
            buffers.append(rendered.buffer)
        }
        return Speech.make(from: buffers)
    }
}

/// A voice a little faster or slower, higher or lower: one character's own 快慢 and 高低 on top
/// of whichever voice reads them.
nonisolated struct TunedVoice: Voice {
    let base: any Voice
    let rate: Float
    /// Semitones.
    let pitch: Float

    var id: String { base.id + "#\(rate)#\(pitch)" }
    var name: String { base.name }
    var qualityName: String? { base.qualityName }
    var isOffline: Bool { base.isOffline }
    func speaks(_ language: String) -> Bool { base.speaks(language) }

    @concurrent func render(_ text: String, language: String) async throws -> Speech? {
        guard let speech = try await base.render(text, language: language) else { return nil }
        return Self.tune(speech, rate: rate, pitch: pitch) ?? speech
    }

    /// The sentence through a time-pitch unit, offline.
    static func tune(_ speech: Speech, rate: Float, pitch: Float) -> Speech? {
        let format = Speech.format
        let engine = AVAudioEngine()
        let node = AVAudioPlayerNode()
        let unit = AVAudioUnitTimePitch()
        unit.rate = rate
        unit.pitch = pitch * 100
        engine.attach(node)
        engine.attach(unit)
        engine.connect(node, to: unit, format: format)
        engine.connect(unit, to: engine.mainMixerNode, format: format)
        do {
            try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
            try engine.start()
        } catch { return nil }
        node.scheduleBuffer(speech.buffer)
        node.play()
        let expected = AVAudioFramePosition(Double(speech.buffer.frameLength) / Double(rate)) + 4096
        guard let out = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(expected) + 4096),
              let chunk = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096) else { return nil }
        while engine.manualRenderingSampleTime < expected {
            let frames = AVAudioFrameCount(min(4096, expected - engine.manualRenderingSampleTime))
            guard (try? engine.renderOffline(frames, to: chunk)) == .success else { break }
            let room = out.frameCapacity - out.frameLength
            let n = min(chunk.frameLength, room)
            if n == 0 { break }
            if let src = chunk.floatChannelData?[0], let dst = out.floatChannelData?[0] {
                (dst + Int(out.frameLength)).update(from: src, count: Int(n))
            }
            out.frameLength += n
        }
        engine.stop()
        return out.frameLength > 0 ? Speech(buffer: out) : nil
    }
}

extension Player.Script {
    /// A book read by its characters' voices, where the reader has given them any.
    static func book(_ core: YuyueCore, _ detail: BookDetail, roles: BookRoles) -> Player.Script {
        var script = Player.Script.book(core, detail)
        script.roles = roles
        return script
    }
}
