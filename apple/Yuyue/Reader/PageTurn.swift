import AVFoundation
import Foundation

/// Horizontal pagination animation style, mirrored on Android (`reader.pageTurn`).
nonisolated enum PageTurn: String, CaseIterable, Hashable, Sendable {
    case slide
    case cover
    case none

    static func of(_ raw: String?) -> PageTurn {
        raw.flatMap(PageTurn.init(rawValue:)) ?? .slide
    }
}

enum PageTurnPrefs {
    static let key = "reader.pageTurn"
    static let soundKey = "reader.pageTurnSound"

    static var current: PageTurn {
        PageTurn.of(UserDefaults.standard.string(forKey: key))
    }

    static var sound: Bool {
        UserDefaults.standard.bool(forKey: soundKey)
    }
}

/// Synthesizes a subtle ~68 ms dry-paper rustle WAV in memory (identical math to Android's
/// `PageTurnSound`) and plays it without interrupting active music or speech.
@MainActor
final class PageTurnSound {
    static let shared = PageTurnSound()
    private var wavData: Data?
    private var player: AVAudioPlayer?

    private init() {}

    func play() {
        guard PageTurnPrefs.sound else { return }
        if wavData == nil { wavData = Self.makePaperWav() }
        guard let data = wavData else { return }
        #if os(iOS)
        // Respect hardware mute switch and never interrupt other apps' audio.
        try? AVAudioSession.sharedInstance().setCategory(.ambient, options: [.mixWithOthers])
        #endif
        if player == nil {
            player = try? AVAudioPlayer(data: data)
            player?.volume = 0.34
            player?.prepareToPlay()
        }
        guard let player else { return }
        player.currentTime = 0
        player.play()
    }

    private static func makePaperWav() -> Data {
        let sampleRate = 22050
        let count = Int(Double(sampleRate) * 0.068)
        var pcm = [Int16](repeating: 0, count: count)
        var seed: UInt64 = 0x5A17_E21C
        var lp1 = 0.0
        var lp2 = 0.0
        var prev = 0.0
        for i in 0 ..< count {
            let t = Double(i) / Double(sampleRate)
            seed = seed &* 6364136223846793005 &+ 1442695040888963407
            let white = (Double(Int32(bitPattern: UInt32(truncatingIfNeeded: seed >> 33))) / Double(Int32.max)) * 2.0 - 1.0
            lp1 += 0.36 * (white - lp1)
            lp2 += 0.14 * (lp1 - lp2)
            let band = lp1 - lp2
            let hp = band - 0.55 * prev
            prev = band
            let attack: Double
            if t < 0.014 {
                let u = t / 0.014
                attack = u * u * (3.0 - 2.0 * u)
            } else {
                attack = 1.0
            }
            let decay = exp(-max(0.0, t - 0.014) * 44.0)
            let flutter = 0.85 + 0.15 * sin(t * 210.0)
            let sample = min(1.0, max(-1.0, hp * attack * decay * flutter * 0.42))
            pcm[i] = Int16(sample * Double(Int16.max))
        }

        let dataBytes = UInt32(count * 2)
        var data = Data(capacity: 44 + Int(dataBytes))
        func appendLE<T: FixedWidthInteger>(_ v: T) {
            var le = v.littleEndian
            withUnsafeBytes(of: &le) { data.append(contentsOf: $0) }
        }
        data.append(contentsOf: [0x52, 0x49, 0x46, 0x46]) // RIFF
        appendLE(36 + dataBytes)
        data.append(contentsOf: [0x57, 0x41, 0x56, 0x45]) // WAVE
        data.append(contentsOf: [0x66, 0x6D, 0x74, 0x20]) // fmt 
        appendLE(UInt32(16))
        appendLE(UInt16(1))
        appendLE(UInt16(1))
        appendLE(UInt32(sampleRate))
        appendLE(UInt32(sampleRate * 2))
        appendLE(UInt16(2))
        appendLE(UInt16(16))
        data.append(contentsOf: [0x64, 0x61, 0x74, 0x61]) // data
        appendLE(dataBytes)
        for s in pcm { appendLE(s) }
        return data
    }
}
