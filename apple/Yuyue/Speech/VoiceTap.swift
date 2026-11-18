import AVFoundation
import MediaToolbox

/// 有声书's sound on its way out: one gain that brings every book to the same loudness (measured
/// once after import, always on), and 人声清楚一点 — the low hum cut, the voice's presence lifted
/// a little, evened out — with a ceiling so nothing clips.
///
/// Runs inside the player's audio tap, on the audio thread: no allocation, no locks; the two
/// settings are plain values read as they are.
nonisolated final class VoiceTap: @unchecked Sendable {
    /// Linear gain for loudness.
    var gain: Float = 1
    var clearer = false

    private var rate: Double = 44_100
    private var channels = 0
    private var float = false
    /// Two biquads a channel: high-pass 80 Hz, presence +3 dB at 3 kHz.
    private var hp = [Biquad](repeating: Biquad(), count: 2)
    private var presence = [Biquad](repeating: Biquad(), count: 2)
    private var envelope: Float = 0

    init(gain: Float, clearer: Bool) {
        self.gain = gain
        self.clearer = clearer
    }

    fileprivate func prepare(_ format: AudioStreamBasicDescription) {
        rate = format.mSampleRate
        channels = Int(format.mChannelsPerFrame)
        float = format.mFormatFlags & kAudioFormatFlagIsFloat != 0 && format.mBitsPerChannel == 32
        let n = max(2, channels)
        hp = (0 ..< n).map { _ in Biquad.highPass(80, rate: rate) }
        presence = (0 ..< n).map { _ in Biquad.peak(3_000, gainDB: 3, q: 1, rate: rate) }
        envelope = 0
    }

    fileprivate func process(_ list: UnsafeMutableAudioBufferListPointer, frames: Int) {
        guard float, frames > 0 else { return }
        let g = gain
        let clear = clearer
        // Compressor (3:1 over −24 dBFS, 10 ms attack, 150 ms release) on the louder channel.
        let threshold: Float = 0.063
        let attack = Float(exp(-1 / (0.010 * rate)))
        let release = Float(exp(-1 / (0.150 * rate)))
        let ceiling: Float = 0.89
        let interleaved = list.count == 1 && channels > 1
        for (b, buffer) in list.enumerated() {
            guard let data = buffer.mData?.assumingMemoryBound(to: Float.self) else { continue }
            let count = Int(buffer.mDataByteSize) / MemoryLayout<Float>.size
            let stride = interleaved ? channels : 1
            for i in 0 ..< count {
                let ch = interleaved ? i % stride : min(b, hp.count - 1)
                var x = data[i] * g
                if clear {
                    x = hp[ch].run(x)
                    x = presence[ch].run(x)
                    if ch == 0 {
                        let level = abs(x)
                        envelope = level > envelope ? attack * envelope + (1 - attack) * level : release * envelope + (1 - release) * level
                    }
                    if envelope > threshold {
                        // Above the threshold the level rises a third as fast.
                        let over = envelope / threshold
                        x *= pow(over, 1.0 / 3.0 - 1)
                    }
                }
                // A soft ceiling at −1 dBFS.
                if abs(x) > ceiling {
                    let s: Float = x < 0 ? -1 : 1
                    x = s * (ceiling + (1 - ceiling) * tanh((abs(x) - ceiling) / (1 - ceiling)))
                }
                data[i] = x
            }
        }
    }

    /// An audio mix that runs the item's sound through `tap`.
    static func mix(_ track: AVAssetTrack, _ tap: VoiceTap) -> AVAudioMix? {
        var callbacks = MTAudioProcessingTapCallbacks(
            version: kMTAudioProcessingTapCallbacksVersion_0,
            clientInfo: UnsafeMutableRawPointer(Unmanaged.passRetained(tap).toOpaque()),
            init: { _, clientInfo, storage in storage.pointee = clientInfo },
            finalize: { t in Unmanaged<VoiceTap>.fromOpaque(MTAudioProcessingTapGetStorage(t)).release() },
            prepare: { t, _, format in
                Unmanaged<VoiceTap>.fromOpaque(MTAudioProcessingTapGetStorage(t)).takeUnretainedValue().prepare(format.pointee)
            },
            unprepare: nil,
            process: { t, frames, _, list, framesOut, flagsOut in
                guard MTAudioProcessingTapGetSourceAudio(t, frames, list, flagsOut, nil, framesOut) == noErr else { return }
                let me = Unmanaged<VoiceTap>.fromOpaque(MTAudioProcessingTapGetStorage(t)).takeUnretainedValue()
                me.process(UnsafeMutableAudioBufferListPointer(list), frames: Int(framesOut.pointee))
            })
        var out: MTAudioProcessingTap?
        guard MTAudioProcessingTapCreate(kCFAllocatorDefault, &callbacks, kMTAudioProcessingTapCreationFlag_PostEffects, &out) == noErr, let out else {
            Unmanaged<VoiceTap>.fromOpaque(callbacks.clientInfo!).release()
            return nil
        }
        let params = AVMutableAudioMixInputParameters(track: track)
        params.audioTapProcessor = out
        let mix = AVMutableAudioMix()
        mix.inputParameters = [params]
        return mix
    }

    /// 响度统一: the gain that brings a recording to about −20 dB of speech, from ten short
    /// samples across it. Between −6 and +12 dB.
    @concurrent static func measure(_ files: [URL]) async -> Float {
        var sum = 0.0, n = 0
        let opened = files.compactMap { try? AVAudioFile(forReading: $0) }
        let total = opened.reduce(0) { $0 + $1.length }
        guard total > 0 else { return 1 }
        for k in 0 ..< 10 {
            if Task.isCancelled { return 1 }
            var at = AVAudioFramePosition(Double(total) * (Double(k) + 0.5) / 10)
            guard let file = opened.first(where: { f in
                if at < f.length { return true }
                at -= f.length
                return false
            }) else { continue }
            let fmt = file.processingFormat
            let want = AVAudioFrameCount(min(Double(file.length - at), fmt.sampleRate * 5))
            guard want > 0, let buf = AVAudioPCMBuffer(pcmFormat: fmt, frameCapacity: want) else { continue }
            file.framePosition = at
            guard (try? file.read(into: buf, frameCount: want)) != nil, let data = buf.floatChannelData?[0] else { continue }
            // 50 ms blocks; the quiet ones (pauses) left out.
            let block = Int(fmt.sampleRate * 0.05)
            var i = 0
            while i + block <= Int(buf.frameLength) {
                var ms = 0.0
                for j in i ..< i + block { ms += Double(data[j] * data[j]) }
                ms /= Double(block)
                if ms > 1e-5 { sum += ms; n += 1 }
                i += block
            }
        }
        guard n > 0 else { return 1 }
        let db = 10 * log10(sum / Double(n))
        let want = min(12, max(-6, -20 - db))
        return Float(pow(10, want / 20))
    }
}

/// A second-order filter, direct form I.
nonisolated struct Biquad {
    var b0: Float = 1, b1: Float = 0, b2: Float = 0, a1: Float = 0, a2: Float = 0
    var x1: Float = 0, x2: Float = 0, y1: Float = 0, y2: Float = 0

    mutating func run(_ x: Float) -> Float {
        let y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
        x2 = x1; x1 = x; y2 = y1; y1 = y
        return y
    }

    static func highPass(_ f: Double, rate: Double) -> Biquad {
        let w = 2 * Double.pi * f / rate, alpha = sin(w) / (2 * 0.707), c = cos(w), a0 = 1 + alpha
        return Biquad(b0: Float((1 + c) / 2 / a0), b1: Float(-(1 + c) / a0), b2: Float((1 + c) / 2 / a0), a1: Float(-2 * c / a0), a2: Float((1 - alpha) / a0))
    }

    static func peak(_ f: Double, gainDB: Double, q: Double, rate: Double) -> Biquad {
        let a = pow(10, gainDB / 40), w = 2 * Double.pi * f / rate, alpha = sin(w) / (2 * q), c = cos(w), a0 = 1 + alpha / a
        return Biquad(b0: Float((1 + alpha * a) / a0), b1: Float(-2 * c / a0), b2: Float((1 - alpha * a) / a0), a1: Float(-2 * c / a0), a2: Float((1 - alpha / a) / a0))
    }
}
