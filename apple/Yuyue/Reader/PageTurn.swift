import AVFoundation
import Foundation
import Observation
import SwiftUI
import UniformTypeIdentifiers
#if os(iOS)
import UIKit
#endif

/// Horizontal pagination animation style, mirrored on Android (`reader.pageTurn`).
nonisolated enum PageTurn: String, CaseIterable, Hashable, Sendable {
    case slide
    case cover
    case none

    static func of(_ raw: String?) -> PageTurn {
        raw.flatMap(PageTurn.init(rawValue:)) ?? .slide
    }
}

/// Built-in selectable page-turn sound presets, mirrored on Android (`reader.pageTurnSoundStyle`).
/// Covers four distinct paper textures plus two quiet tactile desk/ink alternatives.
nonisolated enum PageTurnSoundStyle: String, CaseIterable, Hashable, Sendable, Identifiable {
    case softPaper
    case crispBook
    case ricePaper
    case hardcover
    case woodClick
    case inkDrop

    var id: String { rawValue }

    static func of(_ raw: String?) -> PageTurnSoundStyle {
        raw.flatMap(PageTurnSoundStyle.init(rawValue:)) ?? .softPaper
    }

    var title: String {
        switch self {
        case .softPaper: String(localized: "轻纸")
        case .crispBook: String(localized: "脆页")
        case .ricePaper: String(localized: "宣纸")
        case .hardcover: String(localized: "精装")
        case .woodClick: String(localized: "轻叩")
        case .inkDrop: String(localized: "滴墨")
        }
    }

    var subtitle: String {
        switch self {
        case .softPaper: String(localized: "柔和薄页")
        case .crispBook: String(localized: "清爽新书")
        case .ricePaper: String(localized: "绵厚柔润")
        case .hardcover: String(localized: "沉稳厚页")
        case .woodClick: String(localized: "指尖木案")
        case .inkDrop: String(localized: "水墨落砚")
        }
    }

    var symbol: String {
        switch self {
        case .softPaper: "doc"
        case .crispBook: "book.pages"
        case .ricePaper: "wind"
        case .hardcover: "book.closed"
        case .woodClick: "hand.tap"
        case .inkDrop: "drop"
        }
    }
}

/// User-added custom page-turn sound stored locally as a trimmed, normalized 44.1 kHz mono WAV.
nonisolated struct CustomPageTurnSound: Codable, Hashable, Identifiable, Sendable {
    let id: String
    var name: String
    let durationMs: Int
    let createdAt: TimeInterval

    var selectionKey: String { CustomPageTurnSounds.prefix + id }
}

/// Result of decoding, trimming leading/trailing silence, capping duration, and normalizing audio,
/// along with a focused source window & waveform envelope for interactive micro-trimming.
nonisolated struct ProcessedCustomSound: Sendable {
    let wavData: Data
    let durationMs: Int
    let sourceSamples: [Float]
    let sampleRate: Int
    let trimStart: Double
    let trimEnd: Double
    let autoTrimStart: Double
    let autoTrimEnd: Double
    let waveform: [Float]
}

nonisolated enum CustomSoundError: Error {
    case unreadable
    case silentOrEmpty
    case tooShort
    case saveFailed

    var message: String {
        switch self {
        case .unreadable: String(localized: "这段音频读不出来，换一个试试")
        case .silentOrEmpty: String(localized: "没有检测到明显声音，再试一次")
        case .tooShort: String(localized: "声音太短了，换一段试试")
        case .saveFailed: String(localized: "保存自定义音效失败，请重试")
        }
    }
}

enum PageTurnPrefs {
    static let key = "reader.pageTurn"
    static let soundKey = "reader.pageTurnSound"
    static let soundStyleKey = "reader.pageTurnSoundStyle"
    static let soundVolumeKey = "reader.pageTurnSoundVolume"
    static let hapticKey = "reader.pageTurnHaptic"

    static let defaultVolume: Double = 0.60
    static let volumeRange: ClosedRange<Double> = 0.15 ... 1.0

    static var current: PageTurn {
        PageTurn.of(UserDefaults.standard.string(forKey: key))
    }

    static var sound: Bool {
        UserDefaults.standard.bool(forKey: soundKey)
    }

    static var soundSelection: String {
        UserDefaults.standard.string(forKey: soundStyleKey) ?? PageTurnSoundStyle.softPaper.rawValue
    }

    static var soundStyle: PageTurnSoundStyle {
        PageTurnSoundStyle.of(UserDefaults.standard.string(forKey: soundStyleKey))
    }

    static var soundVolume: Double {
        guard let raw = UserDefaults.standard.object(forKey: soundVolumeKey) as? Double else {
            return defaultVolume
        }
        return min(volumeRange.upperBound, max(volumeRange.lowerBound, raw))
    }

    static var haptic: Bool {
        UserDefaults.standard.bool(forKey: hapticKey)
    }

    @MainActor
    static func displayName(for raw: String) -> String {
        if let custom = CustomPageTurnSounds.shared.item(for: raw) {
            return custom.name
        }
        return PageTurnSoundStyle.of(raw).title
    }
}

// MARK: - Haptic Feedback (iOS)

/// Subtle soft-impact tactile feedback on manual page turns, independent of audio mute/sound state.
@MainActor
final class PageTurnHaptics {
    static let shared = PageTurnHaptics()
    #if os(iOS)
    private let impact = UIImpactFeedbackGenerator(style: .soft)
    #endif

    private init() {}

    func trigger() {
        guard PageTurnPrefs.haptic else { return }
        fire()
    }

    func preview() {
        fire()
    }

    private func fire() {
        #if os(iOS)
        impact.impactOccurred(intensity: 0.72)
        impact.prepare()
        #endif
    }
}

// MARK: - Custom Sound Store & Acoustic Processor

@MainActor @Observable
final class CustomPageTurnSounds {
    static let shared = CustomPageTurnSounds()
    nonisolated static let prefix = "custom:"
    nonisolated static let maxDurationSeconds: Double = 0.65
    nonisolated static let minDurationSeconds: Double = 0.025

    private(set) var items: [CustomPageTurnSound] = []
    @ObservationIgnored private var wavCache: [String: Data] = [:]

    private init() {
        loadIndex()
    }

    private static var folder: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? FileManager.default.temporaryDirectory
        return base.appending(path: "Yuyue/PageTurnSounds", directoryHint: .isDirectory)
    }

    private static var indexURL: URL {
        folder.appending(path: "index.json")
    }

    private static func wavURL(for id: String) -> URL {
        folder.appending(path: "\(id).wav")
    }

    nonisolated static func customID(from raw: String) -> String? {
        guard raw.hasPrefix(prefix) else { return nil }
        let id = String(raw.dropFirst(prefix.count))
        return id.isEmpty ? nil : id
    }

    func item(for raw: String) -> CustomPageTurnSound? {
        guard let id = Self.customID(from: raw) else { return nil }
        return items.first { $0.id == id }
    }

    func wavData(for id: String) -> Data? {
        if let cached = wavCache[id] { return cached }
        guard let data = try? Data(contentsOf: Self.wavURL(for: id)), data.count > 44 else { return nil }
        wavCache[id] = data
        return data
    }

    @discardableResult
    func save(processed: ProcessedCustomSound, name: String) throws -> CustomPageTurnSound {
        let trimmedName = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(16))
        let finalName = trimmedName.isEmpty ? String(localized: "自定义音效") : trimmedName
        let id = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(12)).lowercased()
        let dir = Self.folder
        do {
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
            try processed.wavData.write(to: Self.wavURL(for: id), options: .atomic)
        } catch {
            throw CustomSoundError.saveFailed
        }
        let item = CustomPageTurnSound(
            id: id,
            name: finalName,
            durationMs: processed.durationMs,
            createdAt: Date().timeIntervalSince1970
        )
        wavCache[id] = processed.wavData
        items.append(item)
        persistIndex()
        return item
    }

    func rename(id: String, to newName: String) {
        let trimmed = String(newName.trimmingCharacters(in: .whitespacesAndNewlines).prefix(16))
        guard !trimmed.isEmpty, let idx = items.firstIndex(where: { $0.id == id }) else { return }
        items[idx].name = trimmed
        persistIndex()
    }

    func delete(id: String) {
        items.removeAll { $0.id == id }
        wavCache.removeValue(forKey: id)
        try? FileManager.default.removeItem(at: Self.wavURL(for: id))
        persistIndex()
        if UserDefaults.standard.string(forKey: PageTurnPrefs.soundStyleKey) == Self.prefix + id {
            UserDefaults.standard.set(PageTurnSoundStyle.softPaper.rawValue, forKey: PageTurnPrefs.soundStyleKey)
        }
    }

    private func loadIndex() {
        guard let data = try? Data(contentsOf: Self.indexURL),
              let decoded = try? JSONDecoder().decode([CustomPageTurnSound].self, from: data)
        else {
            items = []
            return
        }
        items = decoded.filter { FileManager.default.fileExists(atPath: Self.wavURL(for: $0.id).path(percentEncoded: false)) }
    }

    private func persistIndex() {
        let dir = Self.folder
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if let data = try? JSONEncoder().encode(items) {
            try? data.write(to: Self.indexURL, options: .atomic)
        }
    }

    /// Decodes any AVAudioFile-readable audio file (WAV, M4A, MP3, CAF, AIFF, FLAC) into 44.1 kHz
    /// mono Float PCM, removes low-frequency rumble, trims leading/trailing silence, caps at 0.65 s,
    /// applies smooth edge fades, and normalizes loudness to match built-in presets.
    @concurrent nonisolated static func processAudioFile(at url: URL) async throws -> ProcessedCustomSound {
        let scoped = url.startAccessingSecurityScopedResource()
        defer { if scoped { url.stopAccessingSecurityScopedResource() } }

        let file: AVAudioFile
        do {
            file = try AVAudioFile(forReading: url)
        } catch {
            throw CustomSoundError.unreadable
        }
        let targetRate = Double(PageTurnSound.sampleRate)
        guard let targetFormat = AVAudioFormat(
            commonFormat: .pcmFormatFloat32,
            sampleRate: targetRate,
            channels: 1,
            interleaved: false
        ) else {
            throw CustomSoundError.unreadable
        }

        // Cap decoded read to 8 seconds so accidentally selecting a long song stays fast & light.
        let srcFormat = file.processingFormat
        let maxSrcFrames = AVAudioFramePosition(srcFormat.sampleRate * 8.0)
        let framesToRead = AVAudioFrameCount(min(file.length, maxSrcFrames))
        guard framesToRead > 0 else { throw CustomSoundError.silentOrEmpty }

        guard let srcBuffer = AVAudioPCMBuffer(pcmFormat: srcFormat, frameCapacity: framesToRead) else {
            throw CustomSoundError.unreadable
        }
        do {
            try file.read(into: srcBuffer, frameCount: framesToRead)
        } catch {
            throw CustomSoundError.unreadable
        }

        let monoSamples: [Float]
        if srcFormat.sampleRate == targetRate, srcFormat.channelCount == 1,
           srcFormat.commonFormat == .pcmFormatFloat32, let ch = srcBuffer.floatChannelData?[0] {
            monoSamples = Array(UnsafeBufferPointer(start: ch, count: Int(srcBuffer.frameLength)))
        } else {
            guard let converter = AVAudioConverter(from: srcFormat, to: targetFormat) else {
                throw CustomSoundError.unreadable
            }
            let ratio = targetRate / max(1.0, srcFormat.sampleRate)
            let outCap = AVAudioFrameCount(Double(srcBuffer.frameLength) * ratio + 4096)
            guard let outBuffer = AVAudioPCMBuffer(pcmFormat: targetFormat, frameCapacity: outCap) else {
                throw CustomSoundError.unreadable
            }
            nonisolated(unsafe) var provided = false
            nonisolated(unsafe) let inputRef = srcBuffer
            var convError: NSError?
            let status = converter.convert(to: outBuffer, error: &convError) { _, outStatus in
                if provided {
                    outStatus.pointee = .endOfStream
                    return nil
                }
                provided = true
                outStatus.pointee = .haveData
                return inputRef
            }
            guard status != .error, convError == nil,
                  let ch = outBuffer.floatChannelData?[0], outBuffer.frameLength > 0 else {
                throw CustomSoundError.unreadable
            }
            monoSamples = Array(UnsafeBufferPointer(start: ch, count: Int(outBuffer.frameLength)))
        }
        return try trimAndNormalize(samples: monoSamples, sampleRate: PageTurnSound.sampleRate)
    }

    nonisolated static let waveformBarCount = 40

    /// Pure DSP post-processor: DC + 60 Hz rumble HP filter → smart leading/trailing silence trim →
    /// 0.65 s max cap → smooth raised-cosine edge fades → peak normalization → 16-bit WAV + waveform.
    nonisolated static func trimAndNormalize(samples: [Float], sampleRate: Int) throws -> ProcessedCustomSound {
        guard !samples.isEmpty, sampleRate > 0 else { throw CustomSoundError.silentOrEmpty }
        let n = samples.count

        // Remove DC offset and gentle 60 Hz 1-pole high-pass to eliminate mic-handling thump.
        var mean = 0.0
        for s in samples { mean += Double(s) }
        mean /= Double(n)
        let rc = 1.0 / (2.0 * Double.pi * 60.0)
        let dt = 1.0 / Double(sampleRate)
        let alpha = rc / (rc + dt)
        var cleaned = [Double](repeating: 0.0, count: n)
        var prevX = 0.0
        var prevY = 0.0
        var peak = 0.0
        for i in 0 ..< n {
            let x = Double(samples[i]) - mean
            let y = alpha * (prevY + x - prevX)
            prevX = x
            prevY = y
            cleaned[i] = y
            peak = max(peak, abs(y))
        }
        guard peak >= 0.004 else { throw CustomSoundError.silentOrEmpty }

        // Trim leading silence (keep 8 ms pre-roll so natural attack transient stays intact).
        let startThresh = max(peak * 0.045, 0.006)
        let firstActive = cleaned.firstIndex { abs($0) >= startThresh } ?? 0
        let preRoll = Int(Double(sampleRate) * 0.008)
        let trimStart = max(0, firstActive - preRoll)

        // Trim trailing silence inside a max 0.65 s window from trimStart.
        let maxFrames = Int(Double(sampleRate) * maxDurationSeconds)
        let searchEnd = min(n, trimStart + maxFrames)
        let endThresh = max(peak * 0.022, 0.003)
        var lastActive = trimStart
        for i in stride(from: searchEnd - 1, through: trimStart, by: -1) {
            if abs(cleaned[i]) >= endThresh {
                lastActive = i
                break
            }
        }
        let postRoll = Int(Double(sampleRate) * 0.024)
        let trimEnd = min(searchEnd, lastActive + postRoll)
        let length = trimEnd - trimStart
        let minFrames = Int(Double(sampleRate) * minDurationSeconds)
        guard length >= minFrames else { throw CustomSoundError.tooShort }

        // Keep a focused ~0.7–1.4 s context window around the active sound so waveform trimming
        // fills the screen generously instead of squashing a 0.3 s flip into a 2.5 s timeline.
        let padBefore = Int(Double(sampleRate) * 0.16)
        let padAfter = Int(Double(sampleRate) * 0.22)
        let minWindow = Int(Double(sampleRate) * 0.72)
        let maxWindow = Int(Double(sampleRate) * 1.40)
        let winStart = max(0, trimStart - padBefore)
        var winEnd = min(n, max(trimEnd + padAfter, min(n, winStart + minWindow)))
        if winEnd - winStart > maxWindow {
            winEnd = min(n, max(trimEnd, winStart + maxWindow))
        }
        let winLen = max(1, winEnd - winStart)
        var windowSamples = [Float](repeating: 0, count: winLen)
        for i in 0 ..< winLen {
            windowSamples[i] = Float(cleaned[winStart + i])
        }

        let relStart = Double(trimStart - winStart) / Double(winLen)
        let relEnd = Double(trimEnd - winStart) / Double(winLen)
        let waveform = computeWaveform(samples: windowSamples, barCount: waveformBarCount)
        let truncated = trimEnd == trimStart + maxFrames && trimEnd < n
        let (wav, durationMs) = try encodeNormalizedSlice(
            Array(cleaned[trimStart ..< trimEnd]),
            sampleRate: sampleRate,
            truncated: truncated
        )
        return ProcessedCustomSound(
            wavData: wav,
            durationMs: durationMs,
            sourceSamples: windowSamples,
            sampleRate: sampleRate,
            trimStart: relStart,
            trimEnd: relEnd,
            autoTrimStart: relStart,
            autoTrimEnd: relEnd,
            waveform: waveform
        )
    }

    /// Re-trims an existing `ProcessedCustomSound` to a user-adjusted `[startFraction, endFraction]`
    /// range inside its context window, applying smooth fades and loudness normalization.
    nonisolated static func renderTrimmed(
        from base: ProcessedCustomSound,
        startFraction: Double,
        endFraction: Double
    ) -> ProcessedCustomSound? {
        let total = base.sourceSamples.count
        guard total > 0, base.sampleRate > 0 else { return nil }
        let clampedStart = min(1.0, max(0.0, startFraction))
        let clampedEnd = min(1.0, max(clampedStart, endFraction))
        let startIdx = max(0, min(total - 1, Int((clampedStart * Double(total)).rounded())))
        let maxFrames = Int(Double(base.sampleRate) * maxDurationSeconds)
        let minFrames = Int(Double(base.sampleRate) * minDurationSeconds)
        var endIdx = max(startIdx + minFrames, min(total, Int((clampedEnd * Double(total)).rounded())))
        if endIdx - startIdx > maxFrames {
            endIdx = startIdx + maxFrames
        }
        guard endIdx <= total, endIdx - startIdx >= minFrames else { return nil }

        let slice = (startIdx ..< endIdx).map { Double(base.sourceSamples[$0]) }
        let truncated = endIdx < total
        guard let (wav, durationMs) = try? encodeNormalizedSlice(slice, sampleRate: base.sampleRate, truncated: truncated) else {
            return nil
        }
        return ProcessedCustomSound(
            wavData: wav,
            durationMs: durationMs,
            sourceSamples: base.sourceSamples,
            sampleRate: base.sampleRate,
            trimStart: Double(startIdx) / Double(total),
            trimEnd: Double(endIdx) / Double(total),
            autoTrimStart: base.autoTrimStart,
            autoTrimEnd: base.autoTrimEnd,
            waveform: base.waveform
        )
    }

    nonisolated static func computeWaveform(samples: [Float], barCount: Int) -> [Float] {
        guard !samples.isEmpty, barCount > 0 else { return Array(repeating: 0.1, count: max(0, barCount)) }
        var peaks = [Float](repeating: 0, count: barCount)
        var maxPeak: Float = 0.0001
        let n = samples.count
        for b in 0 ..< barCount {
            let s0 = (b * n) / barCount
            let s1 = max(s0 + 1, ((b + 1) * n) / barCount)
            var p: Float = 0
            var sq: Float = 0
            for i in s0 ..< min(n, s1) {
                let a = abs(samples[i])
                if a > p { p = a }
                sq += a * a
            }
            let rms = sqrt(sq / Float(max(1, min(n, s1) - s0)))
            let v = p * 0.65 + rms * 0.35
            peaks[b] = v
            if v > maxPeak { maxPeak = v }
        }
        return peaks.map { raw in
            let norm = min(1.0, max(0.0, raw / maxPeak))
            return max(0.08, pow(norm, 0.72))
        }
    }

    private nonisolated static func encodeNormalizedSlice(
        _ rawSlice: [Double],
        sampleRate: Int,
        truncated: Bool
    ) throws -> (Data, Int) {
        var slice = rawSlice
        let sliceMean = slice.reduce(0.0, +) / Double(slice.count)
        for i in 0 ..< slice.count { slice[i] -= sliceMean }
        let fadeIn = min(slice.count / 3, max(1, Int(Double(sampleRate) * 0.004)))
        let fadeOut = min(slice.count / 2, max(1, Int(Double(sampleRate) * (truncated ? 0.065 : 0.022))))
        for i in 0 ..< fadeIn {
            let u = Double(i) / Double(fadeIn)
            slice[i] *= 0.5 - 0.5 * cos(Double.pi * u)
        }
        for i in 0 ..< fadeOut {
            let u = Double(i) / Double(fadeOut)
            slice[slice.count - 1 - i] *= 0.5 - 0.5 * cos(Double.pi * u)
        }

        let slicePeak = slice.reduce(0.0) { max($0, abs($1)) }
        guard slicePeak > 1e-5 else { throw CustomSoundError.silentOrEmpty }
        let targetPeak = 0.60
        let gain = targetPeak / slicePeak
        var pcm = [Int16](repeating: 0, count: slice.count)
        for i in 0 ..< slice.count {
            let v = min(1.0, max(-1.0, slice[i] * gain))
            pcm[i] = Int16(v * Double(Int16.max))
        }
        let wav = PageTurnSound.encodeWav(samples: pcm, sampleRate: sampleRate)
        let durationMs = Int((Double(slice.count) / Double(sampleRate) * 1000.0).rounded())
        return (wav, durationMs)
    }
}

// MARK: - Synthesizer & Polyphonic Audio Player

/// Synthesizes 44.1 kHz multi-layer acoustic page-turn WAVs in memory (identical math to Android's
/// `PageTurnSound`) with 3 subtle micro-variations per built-in style, custom sound playback,
/// user-adjustable volume, and a 4-voice polyphonic player pool.
@MainActor
final class PageTurnSound {
    static let shared = PageTurnSound()

    nonisolated static let sampleRate = 44_100
    nonisolated static let variantCount = 3

    private struct CacheKey: Hashable {
        let style: PageTurnSoundStyle
        let variant: Int
    }

    private var wavCache: [CacheKey: Data] = [:]
    private var players: [AVAudioPlayer] = []
    /// The sheet's try-flip clip. Replaced on each tap so rapid taps restart one sound
    /// instead of stacking on the page-turn voice pool.
    private var auditionPlayer: AVAudioPlayer?
    private var variantCursor = 0

    private init() {}

    func play() {
        guard PageTurnPrefs.sound else { return }
        playSelection(PageTurnPrefs.soundSelection, volume: PageTurnPrefs.soundVolume)
    }

    /// Auditions a built-in preset immediately.
    func preview(_ style: PageTurnSoundStyle, volume: Double? = nil) {
        playBuiltIn(style: style, volume: volume ?? PageTurnPrefs.soundVolume)
    }

    /// Auditions any selection key (built-in preset or `custom:<id>`).
    func previewSelection(_ raw: String, volume: Double? = nil) {
        playSelection(raw, volume: volume ?? PageTurnPrefs.soundVolume)
    }

    /// Auditions an unsaved WAV buffer (used inside the custom sound recorder/importer sheet).
    /// Stops the previous audition first, so a second tap restarts the clip instead of layering it.
    func previewData(_ wav: Data, volume: Double? = nil) {
        auditionPlayer?.stop()
        auditionPlayer = nil
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.ambient, options: [.mixWithOthers])
        #endif
        guard let player = try? AVAudioPlayer(data: wav) else { return }
        let requested = volume ?? PageTurnPrefs.soundVolume
        let clamped = min(PageTurnPrefs.volumeRange.upperBound, max(PageTurnPrefs.volumeRange.lowerBound, requested))
        player.volume = Float(clamped * 0.80)
        player.prepareToPlay()
        player.play()
        auditionPlayer = player
    }

    private func playSelection(_ raw: String, volume: Double) {
        if let customID = CustomPageTurnSounds.customID(from: raw),
           let data = CustomPageTurnSounds.shared.wavData(for: customID) {
            playWav(data, volume: volume)
            return
        }
        playBuiltIn(style: PageTurnSoundStyle.of(raw), volume: volume)
    }

    private func playBuiltIn(style: PageTurnSoundStyle, volume: Double) {
        let variant = variantCursor % Self.variantCount
        variantCursor &+= 1
        let key = CacheKey(style: style, variant: variant)
        let data: Data
        if let cached = wavCache[key] {
            data = cached
        } else {
            let made = Self.synthesizeWav(style: style, variant: variant)
            wavCache[key] = made
            data = made
        }
        playWav(data, volume: volume)
    }

    private func playWav(_ data: Data, volume: Double) {
        #if os(iOS)
        // Respect hardware mute switch and never interrupt other apps' audio.
        try? AVAudioSession.sharedInstance().setCategory(.ambient, options: [.mixWithOthers])
        #endif
        players.removeAll { !$0.isPlaying }
        if players.count >= 4 {
            players.removeFirst().stop()
        }
        guard let player = try? AVAudioPlayer(data: data) else { return }
        let clamped = min(PageTurnPrefs.volumeRange.upperBound, max(PageTurnPrefs.volumeRange.lowerBound, volume))
        player.volume = Float(clamped * 0.80)
        player.prepareToPlay()
        player.play()
        players.append(player)
    }

    // MARK: Acoustic Synthesis (mirrored in Android PageTurnSound)

    nonisolated static func synthesizeWav(style: PageTurnSoundStyle, variant: Int = 0) -> Data {
        let v = ((variant % variantCount) + variantCount) % variantCount
        let samples = renderSamples(style: style, variant: v)
        return encodeWav(samples: samples, sampleRate: sampleRate)
    }

    private nonisolated struct Rng {
        var state: UInt64

        init(seed: UInt64) {
            let mixed = seed &* 0x9E37_79B9_7F4A_7C15
            state = mixed == 0 ? 1 : mixed
        }

        mutating func nextSigned() -> Double {
            state = state &* 6_364_136_223_846_793_005 &+ 1_442_695_040_888_963_407
            let v = Double(UInt32(truncatingIfNeeded: state >> 33) & 0x7FFF_FFFF)
            return (v / 2_147_483_647.0) * 2.0 - 1.0
        }

        mutating func nextUnit() -> Double {
            0.5 * (nextSigned() + 1.0)
        }
    }

    /// Paul Kellet economy pink-noise filter (-3 dB/oct natural acoustic noise floor).
    private nonisolated struct Pink {
        var b0 = 0.0
        var b1 = 0.0
        var b2 = 0.0

        mutating func next(_ w: Double) -> Double {
            b0 = 0.99765 * b0 + w * 0.0990460
            b1 = 0.96300 * b1 + w * 0.2965164
            b2 = 0.57000 * b2 + w * 1.0526913
            return (b0 + b1 + b2 + w * 0.1848) * 0.25
        }
    }

    /// 2nd-order RBJ biquad (lowpass / bandpass) with zipper-free parameter updates.
    private nonisolated struct Biquad {
        enum Kind { case lp, bp }

        var b0 = 0.0
        var b1 = 0.0
        var b2 = 0.0
        var a1 = 0.0
        var a2 = 0.0
        var z1 = 0.0
        var z2 = 0.0

        init(_ kind: Kind, freq: Double, q: Double) {
            set(kind, freq: freq, q: q)
        }

        mutating func set(_ kind: Kind, freq: Double, q: Double) {
            let sr = Double(PageTurnSound.sampleRate)
            let f = min(sr * 0.45, max(20.0, freq))
            let w = 2.0 * Double.pi * f / sr
            let s = sin(w)
            let c = cos(w)
            let alpha = s / (2.0 * max(0.1, q))
            let rawB0: Double
            let rawB1: Double
            let rawB2: Double
            switch kind {
            case .lp:
                rawB0 = (1.0 - c) * 0.5
                rawB1 = 1.0 - c
                rawB2 = (1.0 - c) * 0.5
            case .bp:
                rawB0 = alpha
                rawB1 = 0.0
                rawB2 = -alpha
            }
            let a0 = 1.0 + alpha
            b0 = rawB0 / a0
            b1 = rawB1 / a0
            b2 = rawB2 / a0
            a1 = -2.0 * c / a0
            a2 = (1.0 - alpha) / a0
        }

        mutating func run(_ x: Double) -> Double {
            let y = b0 * x + z1
            z1 = b1 * x - a1 * y + z2
            z2 = b2 * x - a2 * y
            return y
        }
    }

    private nonisolated static func smoothstep(_ u: Double) -> Double {
        let clamped = min(1.0, max(0.0, u))
        return clamped * clamped * (3.0 - 2.0 * clamped)
    }

    private nonisolated static func gauss(_ x: Double, _ mu: Double, _ sigma: Double) -> Double {
        let d = (x - mu) / sigma
        return exp(-0.5 * d * d)
    }

    private nonisolated static func arch(_ u: Double, peak: Double) -> Double {
        guard u > 0.0, u < 1.0 else { return 0.0 }
        if u < peak {
            return pow(sin((u / peak) * Double.pi * 0.5), 1.6)
        }
        return pow(cos(((u - peak) / (1.0 - peak)) * Double.pi * 0.5), 2.1)
    }

    private nonisolated static func finalize(
        _ raw: [Double],
        targetPeak: Double,
        fadeInMs: Double,
        fadeOutMs: Double
    ) -> [Int16] {
        let n = raw.count
        guard n > 0 else { return [] }
        var buf = raw
        let mean = buf.reduce(0.0, +) / Double(n)
        for i in 0 ..< n { buf[i] -= mean }

        let sr = Double(sampleRate)
        let fi = min(n, max(1, Int(sr * fadeInMs / 1000.0)))
        let fo = min(n, max(1, Int(sr * fadeOutMs / 1000.0)))
        for i in 0 ..< fi {
            let u = Double(i) / Double(fi)
            buf[i] *= 0.5 - 0.5 * cos(Double.pi * u)
        }
        for i in 0 ..< fo {
            let u = Double(i) / Double(fo)
            buf[n - 1 - i] *= 0.5 - 0.5 * cos(Double.pi * u)
        }

        let peak = buf.reduce(0.0) { max($0, abs($1)) }
        let gain = peak > 1e-6 ? (targetPeak / peak) : 1.0
        var pcm = [Int16](repeating: 0, count: n)
        for i in 0 ..< n {
            let clamped = min(1.0, max(-1.0, buf[i] * gain))
            pcm[i] = Int16(clamped * Double(Int16.max))
        }
        return pcm
    }

    private nonisolated static func renderSamples(style: PageTurnSoundStyle, variant: Int) -> [Int16] {
        let sr = Double(sampleRate)
        let pitchScales = [1.0, 0.965, 1.04]
        let durScales = [1.0, 1.04, 0.96]
        let pitchScale = pitchScales[variant % 3]
        let durScale = durScales[variant % 3]
        let styleIndex = UInt64(PageTurnSoundStyle.allCases.firstIndex(of: style) ?? 0)
        var rng = Rng(seed: 0x51A7_E21C &+ styleIndex &* 7_919 &+ UInt64(variant) &* 104_729)

        switch style {
        case .softPaper:
            let dur = 0.225 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var p3 = Pink()
            var bpArc = Biquad(.bp, freq: 600.0, q: 1.15)
            var lpArc = Biquad(.lp, freq: 3900.0 * pitchScale, q: 0.707)
            var bpAir = Biquad(.bp, freq: 280.0, q: 0.85)
            var bpSet = Biquad(.bp, freq: 480.0, q: 1.05)
            var lpSet = Biquad(.lp, freq: 2100.0 * pitchScale, q: 0.707)
            var brown = 0.0
            var buf = [Double](repeating: 0.0, count: n)
            for i in 0 ..< n {
                let u = Double(i) / Double(n)
                if (i & 15) == 0 {
                    let sweep = 520.0 + 1040.0 * pow(sin(Double.pi * min(1.0, u * 1.12)), 1.3)
                    bpArc.set(.bp, freq: sweep * pitchScale, q: 1.15)
                    let airF = (220.0 + 260.0 * sin(Double.pi * u)) * pitchScale
                    bpAir.set(.bp, freq: airF, q: 0.85)
                    let setF = (380.0 + 260.0 * max(0.0, (u - 0.5) * 2.0)) * pitchScale
                    bpSet.set(.bp, freq: setF, q: 1.05)
                }
                let w1 = rng.nextSigned()
                let w2 = rng.nextSigned()
                let w3 = rng.nextSigned()
                let pk1 = p1.next(w1)
                brown = 0.96 * brown + 0.08 * w2
                let pk3 = p3.next(w3)

                let envArc = arch(u, peak: 0.38)
                let envAir = arch(u, peak: 0.32)
                let envSet = gauss(u, 0.72, 0.13)

                let l1 = lpArc.run(bpArc.run(pk1)) * envArc * 1.15
                let l2 = bpAir.run(brown) * envAir * 0.85
                let l3 = lpSet.run(bpSet.run(pk3)) * envSet * 0.42
                buf[i] = l1 + l2 + l3
            }
            return finalize(buf, targetPeak: 0.52, fadeInMs: 6.0, fadeOutMs: 18.0)

        case .crispBook:
            let dur = 0.185 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var p2 = Pink()
            var p3 = Pink()
            var bpFlick = Biquad(.bp, freq: 2100.0 * pitchScale, q: 1.6)
            var bpArc = Biquad(.bp, freq: 1200.0, q: 1.25)
            var lpArc = Biquad(.lp, freq: 5000.0 * pitchScale, q: 0.707)
            var bpCrackle = Biquad(.bp, freq: 2400.0 * pitchScale, q: 1.9)
            var bpSet = Biquad(.bp, freq: 640.0 * pitchScale, q: 1.35)
            var lpSet = Biquad(.lp, freq: 2800.0 * pitchScale, q: 0.707)
            var buf = [Double](repeating: 0.0, count: n)
            for i in 0 ..< n {
                let u = Double(i) / Double(n)
                let t = Double(i) / sr
                if (i & 15) == 0 {
                    let sweep = 880.0 + 1750.0 * pow(sin(Double.pi * min(1.0, u * 1.15)), 1.15)
                    bpArc.set(.bp, freq: sweep * pitchScale, q: 1.25)
                }
                let w1 = rng.nextSigned()
                let w2 = rng.nextSigned()
                let w3 = rng.nextSigned()
                let pk1 = p1.next(w1)
                let pk2 = p2.next(w2)
                let pk3 = p3.next(w3)

                let envFlick = gauss(t, 0.015, 0.0075)
                let envArc = arch(u, peak: 0.40)
                let envSet = gauss(u, 0.76, 0.095)

                let imp = rng.nextUnit() < 0.004 ? w2 * 0.7 : 0.0
                let crackle = bpCrackle.run(imp) * envArc * 0.35

                let l1 = bpFlick.run(pk2) * envFlick * 0.42
                let l2 = lpArc.run(bpArc.run(pk1)) * envArc * 1.10
                let l3 = lpSet.run(bpSet.run(pk3)) * envSet * 0.50
                buf[i] = l1 + l2 + l3 + crackle
            }
            return finalize(buf, targetPeak: 0.54, fadeInMs: 3.0, fadeOutMs: 14.0)

        case .ricePaper:
            let dur = 0.275 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var p2 = Pink()
            var bpBody = Biquad(.bp, freq: 420.0, q: 0.95)
            var lpBody = Biquad(.lp, freq: 2000.0 * pitchScale, q: 0.65)
            var lpAir = Biquad(.lp, freq: 460.0 * pitchScale, q: 0.707)
            var bpFiber = Biquad(.bp, freq: 1050.0, q: 1.15)
            var lpFiber = Biquad(.lp, freq: 2100.0 * pitchScale, q: 0.707)
            var brown1 = 0.0
            var brown2 = 0.0
            var buf = [Double](repeating: 0.0, count: n)
            for i in 0 ..< n {
                let u = Double(i) / Double(n)
                if (i & 15) == 0 {
                    let sweep = 310.0 + 530.0 * pow(sin(Double.pi * u), 1.1)
                    bpBody.set(.bp, freq: sweep * pitchScale, q: 0.95)
                    let fibF = (1120.0 - 420.0 * u) * pitchScale
                    bpFiber.set(.bp, freq: fibF, q: 1.15)
                }
                let w1 = rng.nextSigned()
                let w2 = rng.nextSigned()
                let pk1 = p1.next(w1)
                let pk2 = p2.next(w2)
                brown1 = 0.94 * brown1 + 0.12 * w1
                brown2 = 0.975 * brown2 + 0.06 * w2

                let envBody = 0.72 * arch(u, peak: 0.35) + 0.48 * gauss(u, 0.68, 0.16)
                let envAir = arch(u, peak: 0.45)
                let envFib = gauss(u, 0.52, 0.22)

                let l1 = lpBody.run(bpBody.run(0.55 * pk1 + 0.45 * brown1)) * envBody * 1.2
                let l2 = lpAir.run(brown2) * envAir * 0.9
                let l3 = lpFiber.run(bpFiber.run(pk2)) * envFib * 0.24
                buf[i] = l1 + l2 + l3
            }
            return finalize(buf, targetPeak: 0.48, fadeInMs: 10.0, fadeOutMs: 26.0)

        case .hardcover:
            let dur = 0.260 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var p2 = Pink()
            var bpArc = Biquad(.bp, freq: 650.0, q: 1.1)
            var lpArc = Biquad(.lp, freq: 3700.0 * pitchScale, q: 0.707)
            var bpFlex = Biquad(.bp, freq: 220.0, q: 1.05)
            var bpLand = Biquad(.bp, freq: 420.0 * pitchScale, q: 1.2)
            var lpLand = Biquad(.lp, freq: 1600.0 * pitchScale, q: 0.707)
            var brown = 0.0
            var buf = [Double](repeating: 0.0, count: n)
            let thumpT0 = dur * 0.73
            for i in 0 ..< n {
                let u = Double(i) / Double(n)
                let t = Double(i) / sr
                if (i & 15) == 0 {
                    let sweep = 420.0 + 880.0 * pow(sin(Double.pi * min(1.0, u * 1.1)), 1.25)
                    bpArc.set(.bp, freq: sweep * pitchScale, q: 1.1)
                    let flexF = (165.0 + 185.0 * sin(Double.pi * u)) * pitchScale
                    bpFlex.set(.bp, freq: flexF, q: 1.05)
                }
                let w1 = rng.nextSigned()
                let w2 = rng.nextSigned()
                let pk1 = p1.next(w1)
                let pk2 = p2.next(w2)
                brown = 0.965 * brown + 0.08 * w2

                let envArc = arch(u, peak: 0.38)
                let envFlex = arch(u, peak: 0.34)
                let envLand = gauss(u, 0.75, 0.095)

                var thump = 0.0
                let dt = t - thumpT0
                if dt > 0.0 {
                    let thEnv = smoothstep(dt / 0.007) * exp(-dt / 0.026)
                    let f1 = 132.0 * pitchScale
                    let f2 = 258.0 * pitchScale
                    thump = (0.72 * sin(2.0 * Double.pi * f1 * dt) + 0.28 * sin(2.0 * Double.pi * f2 * dt)) * thEnv * 0.16
                }

                let l1 = lpArc.run(bpArc.run(pk1)) * envArc * 1.05
                let l2 = bpFlex.run(brown) * envFlex * 1.15
                let l3 = lpLand.run(bpLand.run(pk2)) * envLand * 0.55
                buf[i] = l1 + l2 + l3 + thump
            }
            return finalize(buf, targetPeak: 0.58, fadeInMs: 5.0, fadeOutMs: 20.0)

        case .woodClick:
            let dur = 0.110 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var bpHit = Biquad(.bp, freq: 780.0 * pitchScale, q: 1.1)
            var lpHit = Biquad(.lp, freq: 1450.0 * pitchScale, q: 0.707)
            var buf = [Double](repeating: 0.0, count: n)
            var ph1 = 0.0
            var ph2 = 0.0
            var ph3 = 0.0
            for i in 0 ..< n {
                let t = Double(i) / sr
                let bend = 1.0 + 0.06 * exp(-t / 0.004)
                let f1 = 460.0 * pitchScale * bend
                let f2 = 1035.0 * pitchScale * bend
                let f3 = 1920.0 * pitchScale * bend
                ph1 += 2.0 * Double.pi * f1 / sr
                ph2 += 2.0 * Double.pi * f2 / sr
                ph3 += 2.0 * Double.pi * f3 / sr

                let atk = smoothstep(t / 0.0025)
                let m1 = 0.65 * sin(ph1) * exp(-t / 0.017)
                let m2 = 0.28 * sin(ph2) * exp(-t / 0.009)
                let m3 = 0.09 * sin(ph3) * exp(-t / 0.0045)

                let w = rng.nextSigned()
                let hit = lpHit.run(bpHit.run(p1.next(w))) * gauss(t, 0.003, 0.0025) * 0.55
                buf[i] = (m1 + m2 + m3) * atk + hit
            }
            return finalize(buf, targetPeak: 0.46, fadeInMs: 2.0, fadeOutMs: 12.0)

        case .inkDrop:
            let dur = 0.145 * durScale
            let n = Int(sr * dur)
            var p1 = Pink()
            var bpPlip = Biquad(.bp, freq: 540.0 * pitchScale, q: 1.4)
            var buf = [Double](repeating: 0.0, count: n)
            var ph = 0.0
            var ph2 = 0.0
            let t0 = 0.008
            for i in 0 ..< n {
                let t = Double(i) / sr
                let w = rng.nextSigned()
                let plip = bpPlip.run(p1.next(w)) * gauss(t, 0.006, 0.0035) * 0.18
                var drop = 0.0
                let dt = t - t0
                if dt > 0.0 {
                    let f = (620.0 + 440.0 * (1.0 - exp(-dt / 0.030))) * pitchScale
                    ph += 2.0 * Double.pi * f / sr
                    ph2 += 2.0 * Double.pi * (f * 2.0) / sr
                    let env = smoothstep(dt / 0.006) * exp(-dt / 0.030)
                    let env2 = smoothstep(dt / 0.005) * exp(-dt / 0.014)
                    drop = sin(ph) * env + 0.11 * sin(ph2) * env2
                }
                buf[i] = plip + drop
            }
            return finalize(buf, targetPeak: 0.44, fadeInMs: 2.0, fadeOutMs: 16.0)
        }
    }

    nonisolated static func encodeWav(samples: [Int16], sampleRate: Int) -> Data {
        let dataBytes = UInt32(samples.count * 2)
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
        appendLE(UInt16(1)) // PCM
        appendLE(UInt16(1)) // mono
        appendLE(UInt32(sampleRate))
        appendLE(UInt32(sampleRate * 2))
        appendLE(UInt16(2))
        appendLE(UInt16(16))
        data.append(contentsOf: [0x64, 0x61, 0x74, 0x61]) // data
        appendLE(dataBytes)
        for s in samples { appendLE(s) }
        return data
    }
}

// MARK: - Short Clip Recorder (up to 2.5 s, smart silence auto-stop)

@MainActor @Observable
private final class PageTurnClipRecorder {
    private(set) var recording = false
    private(set) var seconds: Double = 0
    private(set) var level: Double = 0
    private(set) var levels: [Double] = []
    @ObservationIgnored var onAutoStop: ((URL) -> Void)?

    @ObservationIgnored private var recorder: AVAudioRecorder?
    @ObservationIgnored private var timer: Timer?
    @ObservationIgnored private var heardPeak = false
    @ObservationIgnored private var quietAfterPeak: Double = 0
    @ObservationIgnored private let url = FileManager.default.temporaryDirectory
        .appending(path: "yuyue-turn-sfx-\(UUID().uuidString).caf")

    static let maxSeconds = 2.5
    static let barCount = 24

    static func requestPermission() async -> Bool {
        await AVAudioApplication.requestRecordPermission()
    }

    func start() -> Bool {
        discard()
        #if os(iOS)
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playAndRecord, mode: .default, options: [.defaultToSpeaker])
            try session.setActive(true)
        } catch {
            return false
        }
        #endif
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: PageTurnSound.sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsBigEndianKey: false,
            AVLinearPCMIsFloatKey: false,
        ]
        guard let r = try? AVAudioRecorder(url: url, settings: settings) else {
            restoreSession()
            return false
        }
        r.isMeteringEnabled = true
        guard r.record() else {
            r.stop()
            restoreSession()
            return false
        }
        recorder = r
        recording = true
        seconds = 0
        level = 0.08
        levels = []
        heardPeak = false
        quietAfterPeak = 0
        timer = Timer.scheduledTimer(withTimeInterval: 0.04, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        return true
    }

    private func tick() {
        guard let r = recorder, r.isRecording else { return }
        r.updateMeters()
        seconds = r.currentTime
        let db = r.averagePower(forChannel: 0)
        let norm = max(0.06, min(1.0, Double(db + 52) / 48.0))
        level = norm
        levels.append(norm)
        if levels.count > Self.barCount {
            levels.removeFirst(levels.count - Self.barCount)
        }

        // Smart auto-stop: once a crisp page flip / desk tap finishes and settles into quiet for ~0.55 s.
        if seconds >= 0.10, norm >= 0.28 {
            heardPeak = true
            quietAfterPeak = 0
        } else if heardPeak {
            if norm < 0.14 {
                quietAfterPeak += 0.04
            } else if norm >= 0.18 {
                quietAfterPeak = 0
            }
        }

        if seconds >= Self.maxSeconds || (heardPeak && seconds >= 0.45 && quietAfterPeak >= 0.55) {
            if let out = stop() {
                onAutoStop?(out)
            }
        }
    }

    @discardableResult
    func stop() -> URL? {
        timer?.invalidate()
        timer = nil
        guard let r = recorder else { return nil }
        seconds = max(seconds, r.currentTime)
        r.stop()
        recorder = nil
        recording = false
        level = 0
        heardPeak = false
        quietAfterPeak = 0
        restoreSession()
        return url
    }

    func discard() {
        _ = stop()
        try? FileManager.default.removeItem(at: url)
        seconds = 0
        level = 0
        levels = []
    }

    private func restoreSession() {
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.ambient, options: [.mixWithOthers])
        #endif
    }
}

// MARK: - Sound Preset Grid + Volume Slider + Custom Sheet

/// Shared 3-column preset & custom sound palette used across Settings, TypesetCard, and quick menus.
/// Tapping any preset selects it and immediately plays an audio preview; `+ 自定义` opens recording/import.
struct PageTurnSoundStyleGrid: View {
    @Binding var selectedRaw: String
    var showSubtitle: Bool = false
    var compact: Bool = false
    var showVolume: Bool = true
    var cardBg: Color = YYColor.card
    var insetBg: Color = YYColor.inset
    var ink: Color = YYColor.t1
    var ink2: Color = YYColor.t2
    var borderColor: Color = YYColor.line
    var eink: Bool = false

    @AppStorage(PageTurnPrefs.soundVolumeKey) private var volume = PageTurnPrefs.defaultVolume
    @State private var customSheetShown = false
    @State private var renamingItem: CustomPageTurnSound?
    @State private var renameText = ""

    private let store = CustomPageTurnSounds.shared
    private let columns = Array(repeating: GridItem(.flexible(), spacing: 6), count: 3)

    private var activeSelectionKey: String {
        if let custom = store.item(for: selectedRaw) { return custom.selectionKey }
        return PageTurnSoundStyle.of(selectedRaw).rawValue
    }

    var body: some View {
        let activeKey = activeSelectionKey
        let accent = eink ? ink : YYTint.standard.accent
        let accentBg = eink ? insetBg : YYTint.standard.accentSoft
        let corner: CGFloat = compact ? 8 : 10
        let tileHeight: CGFloat = showSubtitle ? 42 : (compact ? 28 : 32)

        VStack(alignment: .leading, spacing: compact ? 8 : 10) {
            LazyVGrid(columns: columns, spacing: 6) {
                ForEach(PageTurnSoundStyle.allCases) { style in
                    let on = activeKey == style.rawValue
                    Button {
                        selectedRaw = style.rawValue
                        PageTurnSound.shared.preview(style)
                    } label: {
                        tileLabel(
                            symbol: style.symbol,
                            title: style.title,
                            subtitle: showSubtitle ? style.subtitle : nil,
                            on: on,
                            accent: accent,
                            accentBg: accentBg,
                            corner: corner,
                            height: tileHeight
                        )
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                    .accessibilityLabel(Text("\(style.title)，\(style.subtitle)"))
                    .accessibilityAddTraits(on ? .isSelected : [])
                }

                ForEach(store.items) { custom in
                    let on = activeKey == custom.selectionKey
                    Button {
                        selectedRaw = custom.selectionKey
                        PageTurnSound.shared.previewSelection(custom.selectionKey)
                    } label: {
                        tileLabel(
                            symbol: "waveform",
                            title: custom.name,
                            subtitle: showSubtitle ? Self.durationLabel(custom.durationMs) : nil,
                            on: on,
                            accent: accent,
                            accentBg: accentBg,
                            corner: corner,
                            height: tileHeight
                        )
                    }
                    .buttonStyle(YYPressStyle(scale: 0.96))
                    .contextMenu {
                        Button(String(localized: "重命名"), systemImage: "pencil") {
                            renameText = custom.name
                            renamingItem = custom
                        }
                        Button(String(localized: "删除"), systemImage: "trash", role: .destructive) {
                            store.delete(id: custom.id)
                            if selectedRaw == custom.selectionKey {
                                selectedRaw = PageTurnSoundStyle.softPaper.rawValue
                            }
                        }
                    }
                    .accessibilityLabel(Text(custom.name))
                    .accessibilityAddTraits(on ? .isSelected : [])
                }

                Button {
                    customSheetShown = true
                } label: {
                    VStack(spacing: showSubtitle ? 2 : 0) {
                        HStack(spacing: 4) {
                            Image(systemName: "plus")
                                .font(.yy(size: compact ? 10 : 11, weight: .semibold))
                                .foregroundStyle(ink2)
                            Text("自定义")
                                .font(.yy(size: compact ? 11 : 12, weight: .medium))
                                .foregroundStyle(ink2)
                                .lineLimit(1)
                                .minimumScaleFactor(0.75)
                        }
                        if showSubtitle {
                            Text("导入或实录")
                                .font(.yy(size: 9.5))
                                .foregroundStyle(ink2.opacity(0.8))
                                .lineLimit(1)
                                .minimumScaleFactor(0.75)
                        }
                    }
                    .frame(maxWidth: .infinity)
                    .frame(height: tileHeight)
                    .padding(.horizontal, 6)
                    .background(insetBg.opacity(0.55), in: RoundedRectangle(cornerRadius: corner, style: .continuous))
                    .overlay(
                        RoundedRectangle(cornerRadius: corner, style: .continuous)
                            .strokeBorder(
                                ink2.opacity(eink ? 0.8 : 0.35),
                                style: StrokeStyle(lineWidth: 0.8, dash: [3, 2.5])
                            )
                    )
                    .contentShape(RoundedRectangle(cornerRadius: corner, style: .continuous))
                }
                .buttonStyle(YYPressStyle(scale: 0.96))
                .accessibilityLabel(Text("自定义翻页音效"))
            }

            if showVolume {
                volumeSliderRow
            }
        }
        .sheet(isPresented: $customSheetShown) {
            CustomPageTurnSoundSheet(selectedRaw: $selectedRaw)
        }
        .yyPrompt(
            String(localized: "重命名"),
            text: $renameText,
            prompt: String(localized: "名字"),
            isPresented: Binding(get: { renamingItem != nil }, set: { if !$0 { renamingItem = nil } }),
            confirm: String(localized: "保存")
        ) {
            if let item = renamingItem {
                store.rename(id: item.id, to: renameText)
            }
            renamingItem = nil
        }
    }

    private func tileLabel(
        symbol: String,
        title: String,
        subtitle: String?,
        on: Bool,
        accent: Color,
        accentBg: Color,
        corner: CGFloat,
        height: CGFloat
    ) -> some View {
        VStack(spacing: subtitle != nil ? 2 : 0) {
            HStack(spacing: 4) {
                Image(systemName: symbol)
                    .font(.yy(size: compact ? 10 : 11, weight: .medium))
                    .foregroundStyle(on ? accent : ink2)
                Text(title)
                    .font(.yy(size: compact ? 11 : 12, weight: on ? .semibold : .medium))
                    .foregroundStyle(on ? (eink ? ink : accent) : ink)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
            if let subtitle {
                Text(subtitle)
                    .font(.yy(size: 9.5))
                    .foregroundStyle(on ? (eink ? ink2 : accent.opacity(0.85)) : ink2)
                    .lineLimit(1)
                    .minimumScaleFactor(0.75)
            }
        }
        .frame(maxWidth: .infinity)
        .frame(height: height)
        .padding(.horizontal, 6)
        .background(on ? accentBg : insetBg, in: RoundedRectangle(cornerRadius: corner, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: corner, style: .continuous)
                .strokeBorder(
                    on ? (eink ? ink : accent.opacity(0.4)) : (eink ? borderColor : .clear),
                    lineWidth: eink && on ? 1 : 0.5
                )
        )
        .contentShape(RoundedRectangle(cornerRadius: corner, style: .continuous))
    }

    private var volumeSliderRow: some View {
        let clamped = min(PageTurnPrefs.volumeRange.upperBound, max(PageTurnPrefs.volumeRange.lowerBound, volume))
        let pct = Int((clamped * 100).rounded())
        return HStack(spacing: 8) {
            Image(systemName: "speaker.wave.1")
                .font(.yy(size: compact ? 10.5 : 11.5, weight: .medium))
                .foregroundStyle(ink2)
                .frame(width: 14)
            Slider(
                value: Binding(
                    get: { clamped },
                    set: { volume = $0 }
                ),
                in: PageTurnPrefs.volumeRange,
                step: 0.05
            ) { editing in
                if !editing {
                    PageTurnSound.shared.previewSelection(selectedRaw, volume: volume)
                }
            }
            .controlSize(.small)
            .tint(eink ? ink : YYTint.standard.fill)
            .accessibilityLabel(Text("音效音量"))
            .accessibilityValue(Text("\(pct)%"))
            Text("\(pct)%")
                .font(.yy(size: compact ? 10 : 10.5, weight: .medium))
                .monospacedDigit()
                .foregroundStyle(ink2)
                .frame(width: 32, alignment: .trailing)
        }
        .padding(.horizontal, 2)
    }

    static func durationLabel(_ ms: Int) -> String {
        let sec = Double(max(10, ms)) / 1000.0
        return String(localized: "\(sec.formatted(.number.precision(.fractionLength(2)))) 秒")
    }
}

/// Focused sheet for recording a short desk/book flip or importing an audio clip,
/// reviewing it on an interactive miniature flippable book page with live mini-waveform sweep,
/// and saving cleanly via the native header bar.
struct CustomPageTurnSoundSheet: View {
    @Binding var selectedRaw: String
    @Environment(\.dismiss) private var dismiss
    @Environment(\.colorScheme) private var scheme

    @State private var recorder = PageTurnClipRecorder()
    @State private var importing = false
    @State private var processing = false
    @State private var draft: ProcessedCustomSound?
    @State private var draftFromFile = false
    @State private var draftName = ""
    @State private var flipQuoteIndex = 0
    @State private var pageFlipping = false
    /// Bumped on every try-flip so a cancelled tap cannot still swap the sentence.
    @State private var flipGeneration = 0
    @State private var playStartTime: Date?
    @State private var playDurationSec: Double = 0.28
    @State private var playTask: Task<Void, Never>?
    @State private var errorMessage: String?
    @FocusState private var nameFocused: Bool

    private let store = CustomPageTurnSounds.shared
    private var tint: YYTint { YYTint.standard }

    private static let idleWaveHeights: [CGFloat] = [
        0.16, 0.22, 0.30, 0.24, 0.38, 0.52, 0.44, 0.64,
        0.78, 0.58, 0.88, 0.96, 0.84, 0.66, 0.74, 0.54,
        0.46, 0.36, 0.42, 0.28, 0.32, 0.22, 0.20, 0.16,
    ]

    private var previewQuotes: [String] {
        [
            String(localized: "轻点或轻扫这一页，听听你刚录下的翻书声。"),
            String(localized: "纸页轻轻掠过指尖，声音恰到好处，不惊扰夜读。"),
            String(localized: "再翻一页试试节奏，满意就可以直接保存使用。"),
        ]
    }

    #if os(iOS)
    private var sheetDetents: Set<PresentationDetent> {
        if draft != nil {
            return [.height(344)]
        }
        if store.items.isEmpty {
            return [.height(372)]
        }
        let h = min(560, 410 + CGFloat(store.items.count) * 48)
        return store.items.count > 2 ? [.height(h), .large] : [.height(h)]
    }
    #endif

    var body: some View {
        VStack(spacing: 0) {
            if let draft {
                draftHeader(draft)
                    .padding(.horizontal, YYUI.s(18))
                    .padding(.top, YYUI.sheetGrabberTop)
                    .padding(.bottom, YYUI.s(14))
            } else {
                YYSheetHeader(title: String(localized: "自定义翻页音效")) {
                    cleanupAndDismiss()
                }
                .padding(.horizontal, YYUI.s(18))
                .padding(.top, YYUI.sheetGrabberTop)
                .padding(.bottom, YYUI.s(12))
            }

            ScrollView {
                VStack(alignment: .leading, spacing: YYUI.s(14)) {
                    if let errorMessage {
                        errorBanner(errorMessage)
                            .transition(.opacity.combined(with: .move(edge: .top)))
                    }

                    if let draft {
                        draftBookStudio(draft)
                            .transition(.asymmetric(
                                insertion: .opacity.combined(with: .scale(scale: 0.97)),
                                removal: .opacity
                            ))
                    } else {
                        captureStudio
                            .transition(.asymmetric(
                                insertion: .opacity.combined(with: .scale(scale: 0.97)),
                                removal: .opacity
                            ))
                        if !store.items.isEmpty {
                            savedList
                        }
                    }
                }
                .padding(.horizontal, YYUI.s(18))
                .padding(.bottom, YYUI.s(20))
            }
            .scrollBounceBehavior(.basedOnSize)
            .scrollDismissesKeyboard(.interactively)
            .onTapGesture { nameFocused = false }
        }
        .animation(YYMotion.standard, value: draft != nil)
        .animation(YYMotion.quick, value: errorMessage != nil)
        .background(YYColor.bg)
        #if os(iOS)
        .presentationDetents(sheetDetents)
        .presentationDragIndicator(.visible)
        .presentationBackground(YYColor.bg)
        #else
        .frame(width: 410, height: draft != nil ? 336 : (store.items.isEmpty ? 350 : 450))
        .paperWindow()
        #endif
        .fileImporter(isPresented: $importing, allowedContentTypes: [.audio]) { result in
            switch result {
            case .success(let url):
                let stem = url.deletingPathExtension().lastPathComponent
                importAudio(from: url, defaultName: stem, fromFile: true)
            case .failure:
                break
            }
        }
        .onDisappear {
            playTask?.cancel()
            recorder.discard()
        }
    }

    private func draftHeader(_ item: ProcessedCustomSound) -> some View {
        ZStack {
            Text("自定义翻页音效")
                .font(YYUI.font(14.5, .semibold))
                .foregroundStyle(YYColor.t1)
                .lineLimit(1)

            HStack {
                Button {
                    nameFocused = false
                    playTask?.cancel()
                    playStartTime = nil
                    pageFlipping = false
                    draft = nil
                    errorMessage = nil
                } label: {
                    HStack(spacing: 3) {
                        Image(systemName: "chevron.left")
                            .font(YYUI.font(11.5, .semibold))
                        Text(draftFromFile ? String(localized: "重选") : String(localized: "重录"))
                            .font(YYUI.font(13, .medium))
                    }
                    .foregroundStyle(YYColor.t2)
                    .frame(minHeight: YYUI.s(28))
                    .contentShape(.rect)
                }
                .buttonStyle(.plain)

                Spacer()

                Button {
                    saveDraft(item)
                } label: {
                    Text("保存")
                        .font(YYUI.font(13.5, .semibold))
                        .foregroundStyle(tint.accent)
                        .frame(minHeight: YYUI.s(28))
                        .padding(.horizontal, 4)
                        .contentShape(.rect)
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
                .keyboardShortcut(.defaultAction)
            }
        }
    }

    private func errorBanner(_ message: String) -> some View {
        HStack(spacing: YYUI.s(8)) {
            Image(systemName: "exclamationmark.circle.fill")
                .font(YYUI.font(13))
                .foregroundStyle(YYColor.danger)
            Text(message)
                .font(YYUI.font(11.5))
                .foregroundStyle(YYColor.t1)
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, YYUI.s(12))
        .padding(.vertical, YYUI.s(10))
        .background(YYColor.danger.opacity(0.09), in: RoundedRectangle(cornerRadius: YYUI.s(12), style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: YYUI.s(12), style: .continuous)
                .strokeBorder(YYColor.danger.opacity(0.22), lineWidth: 0.5)
        )
    }

    // MARK: State 1 — Capture Studio

    private var captureStudio: some View {
        VStack(spacing: YYUI.s(12)) {
            VStack(spacing: YYUI.s(16)) {
                liveMeterView
                    .frame(height: 44)
                    .padding(.top, YYUI.s(4))

                Button {
                    toggleRecord()
                } label: {
                    let progress = min(1.0, recorder.seconds / PageTurnClipRecorder.maxSeconds)
                    ZStack {
                        Circle()
                            .stroke(
                                recorder.recording ? YYColor.danger.opacity(0.16) : tint.accent.opacity(0.16),
                                lineWidth: 2.5
                            )
                            .frame(width: 74, height: 74)

                        if recorder.recording {
                            Circle()
                                .trim(from: 0, to: progress)
                                .stroke(YYColor.danger, style: StrokeStyle(lineWidth: 2.5, lineCap: .round))
                                .rotationEffect(.degrees(-90))
                                .frame(width: 74, height: 74)
                                .animation(.linear(duration: 0.05), value: progress)
                        }

                        Circle()
                            .fill(recorder.recording ? YYColor.danger : tint.fill)
                            .frame(width: 60, height: 60)
                            .scaleEffect(recorder.recording ? (1.0 + recorder.level * 0.07) : 1.0)
                            .shadow(
                                color: (recorder.recording ? YYColor.danger : tint.fill).opacity(0.26),
                                radius: 10,
                                y: 4
                            )
                            .animation(.easeOut(duration: 0.08), value: recorder.level)

                        if recorder.recording {
                            RoundedRectangle(cornerRadius: 4.5, style: .continuous)
                                .fill(.white)
                                .frame(width: 17, height: 17)
                        } else if processing {
                            ProgressView()
                                .tint(.white)
                                .controlSize(.small)
                        } else {
                            Image(systemName: "mic.fill")
                                .font(.yy(size: 22, weight: .semibold))
                                .foregroundStyle(.white)
                        }
                    }
                    .frame(width: 78, height: 78)
                    .contentShape(.circle)
                }
                .buttonStyle(YYPressStyle(scale: 0.94))
                .disabled(processing)
                .accessibilityLabel(recorder.recording ? Text("停止录音") : Text("录一段翻书声"))

                VStack(spacing: 4) {
                    if recorder.recording {
                        Text("正在收音 · \(recorder.seconds.formatted(.number.precision(.fractionLength(1)))) 秒")
                            .font(YYUI.font(13, .semibold))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.danger)
                        Text("翻完停顿片刻自动完成，或轻点停止")
                            .font(YYUI.font(10.5))
                            .foregroundStyle(YYColor.t3)
                    } else if processing {
                        Text("正在裁剪与调平音量…")
                            .font(YYUI.font(13, .semibold))
                            .foregroundStyle(YYColor.t1)
                    } else {
                        Text("翻一页真书，或轻敲桌面")
                            .font(YYUI.font(13, .semibold))
                            .foregroundStyle(YYColor.t1)
                        Text("录完稍停自动截取 · 自动去静音并调平音量")
                            .font(YYUI.font(10.5))
                            .foregroundStyle(YYColor.t3)
                            .multilineTextAlignment(.center)
                    }
                }
                .animation(YYMotion.quick, value: recorder.recording)
            }
            .frame(maxWidth: .infinity)
            .padding(.vertical, YYUI.s(18))
            .padding(.horizontal, YYUI.s(16))
            .yyCard()

            // Quiet local-only & import footer row
            HStack(spacing: YYUI.s(8)) {
                Text("只保存在本地，不上传")
                    .font(YYUI.font(10.5))
                    .foregroundStyle(YYColor.t3)
                Spacer(minLength: 6)
                Button {
                    errorMessage = nil
                    importing = true
                } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "folder")
                            .font(YYUI.font(11, .semibold))
                        Text("从文件导入…")
                            .font(YYUI.font(11, .semibold))
                    }
                    .foregroundStyle(tint.accent)
                    .padding(.horizontal, YYUI.s(11))
                    .frame(height: YYUI.s(30))
                    .background(tint.accentSoft, in: Capsule())
                }
                .buttonStyle(YYPressStyle(scale: 0.96))
                .disabled(recorder.recording || processing)
            }
            .padding(.horizontal, YYUI.s(4))
        }
    }

    private var liveMeterView: some View {
        let count = PageTurnClipRecorder.barCount
        let live = recorder.levels
        return HStack(spacing: 4) {
            ForEach(0 ..< count, id: \.self) { i in
                let h: CGFloat = {
                    if recorder.recording {
                        let offset = count - live.count
                        if i < offset { return 0.10 }
                        return CGFloat(live[i - offset])
                    }
                    return Self.idleWaveHeights[i % Self.idleWaveHeights.count]
                }()
                let barColor: Color = {
                    if recorder.recording {
                        let recency = Double(i + 1) / Double(count)
                        return YYColor.danger.opacity(0.32 + 0.68 * recency)
                    }
                    return tint.accent.opacity(0.22)
                }()
                Capsule()
                    .fill(barColor)
                    .frame(width: 3.5, height: max(5, h * 38))
            }
        }
        .animation(.easeOut(duration: 0.08), value: recorder.levels.count)
    }


    /// Style-3 prototype ease: cubic-bezier(0.22, 1, 0.36, 1) over 0.36s.
    private var pageFlipAnimation: Animation {
        YYMotion.reduce ? .linear(duration: 0) : .timingCurve(0.22, 1, 0.36, 1, duration: 0.36)
    }

    /// Short ease back to flat so a second tap can start a new lift. A snap to 0 reads as a jump.
    private var flipRelease: Animation {
        YYMotion.reduce ? .linear(duration: 0) : .timingCurve(0.22, 1, 0.36, 1, duration: 0.12)
    }

    private func draftBookStudio(_ item: ProcessedCustomSound) -> some View {
        let dark = scheme == .dark
        let paperBg = dark ? Color(yy: YYRGBA(38, 35, 31)) : Color(yy: YYRGBA(244, 239, 230))
        let paperEdge = dark ? Color(yy: YYRGBA(56, 51, 44)) : Color(yy: YYRGBA(229, 222, 201))
        let paperInk = dark ? Color(yy: YYRGBA(234, 228, 218)) : Color(yy: YYRGBA(44, 41, 37))
        let quote = previewQuotes[flipQuoteIndex % previewQuotes.count]
        let flipping = pageFlipping && !YYMotion.reduce
        let pageShape = RoundedRectangle(cornerRadius: 16, style: .continuous)

        return VStack(alignment: .leading, spacing: 14) {
            ZStack {
                Text("纸页落下，静气自生。下一段文字正在温柔等待。")
                    .font(YYUI.serif(13))
                    .lineSpacing(5)
                    .foregroundStyle(paperInk.opacity(0.45))
                    .padding(.horizontal, 20)
                    .padding(.vertical, 18)
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                    .background(paperBg, in: pageShape)
                    .overlay(pageShape.strokeBorder(paperEdge, lineWidth: 0.8))
                    .opacity(0.85)
                    .padding(.top, 4)
                    .padding(.leading, 8)
                    .padding(.trailing, 2)
                    .allowsHitTesting(false)

                VStack(alignment: .leading, spacing: 0) {
                    HStack(spacing: 6) {
                        Circle()
                            .fill(tint.fill)
                            .frame(width: 6, height: 6)
                        Text(draftFromFile ? String(localized: "导入音频") : String(localized: "手边实录"))
                            .font(YYUI.font(11, .semibold))
                            .foregroundStyle(tint.accent)
                        Spacer(minLength: 8)
                        Text(PageTurnSoundStyleGrid.durationLabel(item.durationMs))
                            .font(YYUI.font(11))
                            .monospacedDigit()
                            .foregroundStyle(YYColor.t3)
                    }

                    Text(quote)
                        .font(YYUI.serif(14.5))
                        .lineSpacing(6)
                        .foregroundStyle(paperInk)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.top, 8)

                    Spacer(minLength: 8)

                    HStack(spacing: 8) {
                        MiniPageWaveformView(
                            waveform: item.waveform,
                            trimStart: item.trimStart,
                            trimEnd: item.trimEnd,
                            playStartTime: playStartTime,
                            playDurationSec: playDurationSec,
                            tint: tint.accent
                        )
                        .frame(width: 120, height: 18)

                        Spacer(minLength: 6)

                        HStack(spacing: 3) {
                            Text("点此试翻")
                                .font(YYUI.font(11, .semibold))
                            Image(systemName: "arrow.right")
                                .font(YYUI.font(9.5, .semibold))
                        }
                        .foregroundStyle(tint.accent)
                        .padding(.trailing, 14)
                    }
                }
                .padding(.horizontal, 18)
                .padding(.top, 16)
                .padding(.bottom, 14)
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                .background(paperBg, in: pageShape)
                .overlay(alignment: .bottomTrailing) {
                    PageCornerFold(size: 30, accent: tint.accent, dark: dark)
                }
                .overlay(pageShape.strokeBorder(paperEdge, lineWidth: 0.8))
                .overlay(alignment: .trailing) {
                    LinearGradient(
                        colors: [tint.accent.opacity(0.05), .clear],
                        startPoint: .trailing,
                        endPoint: .leading
                    )
                    .frame(width: 16)
                    .clipShape(pageShape)
                    .allowsHitTesting(false)
                }
                .clipShape(pageShape)
                .drawingGroup(opaque: false)
                .shadow(color: Color(yy: YYRGBA(55, 42, 26, dark ? 0.40 : 0.10)), radius: 12, y: 8)
                .rotation3DEffect(
                    .degrees(flipping ? -28 : 0),
                    axis: (x: 0, y: 1, z: 0),
                    anchor: .leading,
                    perspective: 0.35
                )
                .scaleEffect(flipping ? 0.985 : 1, anchor: .leading)
                .offset(x: flipping ? -8 : 0)
                .padding(.trailing, 6)
                .padding(.bottom, 4)
            }
            .frame(height: 176)
            .contentShape(.rect)
            .onTapGesture {
                nameFocused = false
                flipAndAudition(item)
            }
            .simultaneousGesture(
                DragGesture(minimumDistance: 18)
                    .onEnded { value in
                        let dx = value.translation.width
                        if dx < -28, abs(dx) > abs(value.translation.height) {
                            nameFocused = false
                            flipAndAudition(item)
                        }
                    }
            )
            .accessibilityElement(children: .combine)
            .accessibilityLabel(Text("点此试翻"))
            .accessibilityAddTraits(.isButton)

            HStack(spacing: YYUI.s(10)) {
                Text("音效名")
                    .font(YYUI.font(12))
                    .foregroundStyle(YYColor.t3)
                TextField("", text: $draftName, prompt: Text("给音效起个名字").foregroundStyle(YYColor.t3))
                    .textFieldStyle(.plain)
                    .font(YYUI.font(13.5, .semibold))
                    .foregroundStyle(YYColor.t1)
                    .focused($nameFocused)
                    .submitLabel(.done)
                    .onSubmit { nameFocused = false }
                    .onChange(of: draftName) { _, newValue in
                        if newValue.count > 16 {
                            draftName = String(newValue.prefix(16))
                        }
                    }
                if !draftName.isEmpty {
                    Button {
                        draftName = ""
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                            .font(YYUI.font(13.5))
                            .foregroundStyle(YYColor.t3.opacity(0.6))
                            .frame(width: 24, height: 24)
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                    .accessibilityLabel(Text("清空"))
                }
            }
            .padding(.horizontal, YYUI.s(14))
            .frame(minHeight: YYUI.s(44))
            .background(YYColor.card, in: RoundedRectangle(cornerRadius: YYUI.s(14), style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: YYUI.s(14), style: .continuous)
                    .strokeBorder(scheme == .dark ? Color.white.opacity(0.07) : YYColor.line, lineWidth: 0.6)
            )
        }
    }

    // MARK: Saved List (Capture Mode)

    private var savedList: some View {
        VStack(alignment: .leading, spacing: YYUI.s(6)) {
            Text("已保存的自定义音效")
                .font(YYUI.font(11, .semibold))
                .foregroundStyle(YYColor.t3)
                .padding(.horizontal, YYUI.s(4))
                .padding(.top, YYUI.s(4))
            YYGroup {
                ForEach(store.items) { custom in
                    let active = selectedRaw == custom.selectionKey
                    HStack(spacing: YYUI.s(10)) {
                        Button {
                            selectedRaw = custom.selectionKey
                            PageTurnSound.shared.previewSelection(custom.selectionKey)
                        } label: {
                            HStack(spacing: YYUI.s(8)) {
                                Image(systemName: active ? "checkmark.circle.fill" : "waveform")
                                    .font(YYUI.font(13))
                                    .foregroundStyle(active ? tint.accent : YYColor.t2)
                                Text(custom.name)
                                    .font(YYUI.font(12, active ? .semibold : .regular))
                                    .foregroundStyle(YYColor.t1)
                                    .lineLimit(1)
                                Spacer()
                                Text(PageTurnSoundStyleGrid.durationLabel(custom.durationMs))
                                    .font(YYUI.font(10.5))
                                    .monospacedDigit()
                                    .foregroundStyle(YYColor.t3)
                            }
                            .frame(minHeight: YYUI.s(38))
                            .contentShape(.rect)
                        }
                        .buttonStyle(.plain)

                        Button {
                            store.delete(id: custom.id)
                            if selectedRaw == custom.selectionKey {
                                selectedRaw = PageTurnSoundStyle.softPaper.rawValue
                            }
                        } label: {
                            Image(systemName: "trash")
                                .font(YYUI.font(12))
                                .foregroundStyle(YYColor.t3)
                                .frame(width: YYUI.s(30), height: YYUI.s(30))
                                .contentShape(.rect)
                        }
                        .buttonStyle(.plain)
                        .accessibilityLabel(Text("删除"))
                    }
                }
            }
        }
    }

    // MARK: Actions

    private func cleanupAndDismiss() {
        nameFocused = false
        playTask?.cancel()
        recorder.discard()
        dismiss()
    }

    private func flipAndAudition(_ item: ProcessedCustomSound, animatePage: Bool = true) {
        // Cancel the previous apex. A new tap owns the only in-flight flip, so an old
        // sleep cannot swap the sentence after a newer tap has already started.
        playTask?.cancel()
        flipGeneration += 1
        let generation = flipGeneration
        PageTurnSound.shared.previewData(item.wavData)
        #if os(iOS)
        if animatePage {
            UIImpactFeedbackGenerator(style: .soft).impactOccurred(intensity: 0.65)
        }
        #endif
        let sec = max(0.12, Double(item.durationMs) / 1000.0)
        playDurationSec = sec
        playStartTime = Date()

        guard animatePage, !YYMotion.reduce else {
            if animatePage { flipQuoteIndex += 1 }
            playTask = Task { @MainActor in
                try? await Task.sleep(for: .milliseconds(max(40, item.durationMs)))
                guard !Task.isCancelled, generation == flipGeneration else { return }
                playStartTime = nil
            }
            return
        }

        playTask = Task { @MainActor in
            // `pageFlipping = true` does nothing while a lift is already running, which
            // left the page stuck tilted and the sentence swap waiting on a timer that
            // every new tap cancelled. Ease back to flat first so the next lift is a
            // real change, then run one apex: swap the line once, then settle.
            if pageFlipping {
                withAnimation(flipRelease) { pageFlipping = false }
                try? await Task.sleep(for: .milliseconds(120))
                guard !Task.isCancelled, generation == flipGeneration else { return }
            }
            withAnimation(pageFlipAnimation) { pageFlipping = true }
            try? await Task.sleep(for: .milliseconds(230))
            guard !Task.isCancelled, generation == flipGeneration else { return }
            var quiet = Transaction()
            quiet.disablesAnimations = true
            withTransaction(quiet) { flipQuoteIndex += 1 }
            withAnimation(pageFlipAnimation) { pageFlipping = false }
            let remain = max(40, item.durationMs - 230)
            try? await Task.sleep(for: .milliseconds(remain))
            guard !Task.isCancelled, generation == flipGeneration else { return }
            playStartTime = nil
        }
    }

    private func saveDraft(_ item: ProcessedCustomSound) {
        nameFocused = false
        do {
            let saved = try store.save(processed: item, name: draftName)
            selectedRaw = saved.selectionKey
            UserDefaults.standard.set(true, forKey: PageTurnPrefs.soundKey)
            PageTurnSound.shared.previewSelection(saved.selectionKey)
            #if os(iOS)
            UINotificationFeedbackGenerator().notificationOccurred(.success)
            #endif
            dismiss()
        } catch let err as CustomSoundError {
            errorMessage = err.message
        } catch {
            errorMessage = CustomSoundError.saveFailed.message
        }
    }

    private func toggleRecord() {
        errorMessage = nil
        if recorder.recording {
            if let url = recorder.stop() {
                importAudio(from: url, defaultName: String(localized: "手边实录"), fromFile: false, deleteAfter: true)
            }
            return
        }
        Task {
            let allowed = await PageTurnClipRecorder.requestPermission()
            guard allowed else {
                errorMessage = String(localized: "请在系统设置中允许语阅使用麦克风，或直接从文件导入音频")
                return
            }
            recorder.onAutoStop = { url in
                importAudio(from: url, defaultName: String(localized: "手边实录"), fromFile: false, deleteAfter: true)
            }
            if recorder.start() {
                #if os(iOS)
                UIImpactFeedbackGenerator(style: .light).impactOccurred()
                #endif
            } else {
                errorMessage = String(localized: "无法启动录音，请重试")
            }
        }
    }

    private func importAudio(from url: URL, defaultName: String, fromFile: Bool, deleteAfter: Bool = false) {
        processing = true
        errorMessage = nil
        Task {
            defer {
                processing = false
                if deleteAfter { try? FileManager.default.removeItem(at: url) }
            }
            do {
                let processed = try await CustomPageTurnSounds.processAudioFile(at: url)
                draft = processed
                draftFromFile = fromFile
                draftName = String(defaultName.prefix(16))
                flipQuoteIndex = 0
                pageFlipping = false
                #if os(iOS)
                UINotificationFeedbackGenerator().notificationOccurred(.success)
                #endif
                flipAndAudition(processed, animatePage: false)
            } catch let err as CustomSoundError {
                errorMessage = err.message
            } catch {
                errorMessage = CustomSoundError.unreadable.message
            }
        }
    }
}

/// 32-bar waveform drawn in one Canvas. The timeline pauses when nothing is playing,
/// so scrolling the sheet does not rebuild bars.
private struct MiniPageWaveformView: View {
    let waveform: [Float]
    let trimStart: Double
    let trimEnd: Double
    let playStartTime: Date?
    let playDurationSec: Double
    let tint: Color

    private var bars: [CGFloat] {
        let outCount = 32
        guard !waveform.isEmpty else { return Array(repeating: 0.25, count: outCount) }
        let n = waveform.count
        let s = max(0, min(n - 1, Int((trimStart * Double(n)).rounded(.down))))
        let e = max(s + 1, min(n, Int((trimEnd * Double(n)).rounded(.up))))
        let slice = waveform[s ..< e]
        let sliceCount = slice.count
        return (0 ..< outCount).map { i in
            let idx = slice.index(slice.startIndex, offsetBy: min(sliceCount - 1, (i * sliceCount) / outCount))
            return CGFloat(slice[idx])
        }
    }

    var body: some View {
        let samples = bars
        TimelineView(.animation(minimumInterval: 1.0 / 30.0, paused: playStartTime == nil)) { context in
            let progress: Double? = {
                guard let playStartTime else { return nil }
                let elapsed = context.date.timeIntervalSince(playStartTime)
                return min(1, max(0, elapsed / max(0.06, playDurationSec)))
            }()
            Canvas { ctx, size in
                let count = samples.count
                guard count > 0, size.width > 1, size.height > 1 else { return }
                let step = size.width / CGFloat(count)
                let barW = min(2.4, max(1.6, step * 0.42))
                for i in 0 ..< count {
                    let pos = (Double(i) + 0.5) / Double(count)
                    let edge = sin(Double.pi * pos)
                    let amp = samples[i] * CGFloat(0.45 + 0.55 * edge)
                    let bh = max(3, amp * (size.height - 4))
                    let cx = (CGFloat(i) + 0.5) * step
                    let lit = progress.map { pos <= $0 } ?? false
                    let rect = CGRect(x: cx - barW / 2, y: (size.height - bh) / 2, width: barW, height: bh)
                    ctx.fill(Path(roundedRect: rect, cornerRadius: barW / 2), with: .color(tint.opacity(lit ? 1 : 0.34)))
                }
            }
        }
        .allowsHitTesting(false)
    }
}

/// Bottom-right dog-ear from the style-3 prototype (30pt, 135° fold, 8pt crease radius).
private struct PageCornerFold: View {
    let size: CGFloat
    let accent: Color
    let dark: Bool

    var body: some View {
        LinearGradient(
            stops: [
                .init(color: .clear, location: 0.48),
                .init(color: accent.opacity(dark ? 0.28 : 0.14), location: 0.50),
                .init(color: Color.white.opacity(dark ? 0.22 : 0.65), location: 1),
            ],
            startPoint: .topLeading,
            endPoint: .bottomTrailing
        )
        .frame(width: size, height: size)
        .clipShape(
            UnevenRoundedRectangle(
                topLeadingRadius: 8,
                bottomLeadingRadius: 0,
                bottomTrailingRadius: 0,
                topTrailingRadius: 0
            )
        )
        .allowsHitTesting(false)
    }
}
