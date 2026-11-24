@preconcurrency import PostHog
import SwiftUI
import YuyueCore
#if os(iOS)
import UIKit
#endif

/// Injectable sink for unit testing `AirTraffic` without hitting the network or PostHog singleton.
@MainActor
protocol AirTrafficSink: AnyObject {
    func optIn()
    func optOut()
    func screen(_ name: String, properties: [String: Any])
    func capture(_ event: String, properties: [String: Any])
    func flush()
}

@MainActor
private final class PostHogAirTrafficSink: AirTrafficSink {
    func optIn() {
        PostHogSDK.shared.optIn()
    }

    func optOut() {
        PostHogSDK.shared.optOut()
    }

    func screen(_ name: String, properties: [String: Any]) {
        PostHogSDK.shared.screen(name, properties: properties)
    }

    func capture(_ event: String, properties: [String: Any]) {
        PostHogSDK.shared.capture(event, properties: properties)
    }

    func flush() {
        PostHogSDK.shared.flush()
    }
}

/// Anonymous usage analytics (AirTraffic / PostHog).
///
/// Strictly records usage shapes only (closed enum properties + semantic screen names).
/// Never calls `identify` or `alias`, and never transmits free text (book titles, authors,
/// filenames, search queries, URLs, emails, or error messages).
@MainActor
enum AirTraffic {
    static let prefKey = "analytics.enabled"

    private static let appKey = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg"
    private static let trafficHost = "https://apps.air.build/traffic"

    private static let postHogSink = PostHogAirTrafficSink()
    private static var ready = false
    private static var enabled = true
    private static var lastScreen: String?
    private static var sink: any AirTrafficSink = postHogSink
    private static var persistEnabled: (Bool) -> Void = { value in
        UserDefaults.standard.set(value, forKey: prefKey)
    }

    static var platform: String {
        #if os(macOS)
        "macos"
        #else
        UIDevice.current.userInterfaceIdiom == .pad ? "ipados" : "ios"
        #endif
    }

    static var isEnabled: Bool { enabled }

    static func start() {
        if ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] != nil { return }
        if ready && sink === postHogSink { return }
        let current = UserDefaults.standard.object(forKey: prefKey) as? Bool ?? true
        enabled = current
        lastScreen = nil
        sink = postHogSink

        let config = PostHogConfig(projectToken: appKey, host: trafficHost)
        config.captureApplicationLifecycleEvents = true
        config.captureScreenViews = false
        config.personProfiles = .never
        config.preloadFeatureFlags = false
        config.sendFeatureFlagEvent = false
        config.errorTrackingConfig.autoCapture = false
        #if os(iOS)
        config.sessionReplay = false
        config.surveys = false
        #endif
        config.optOut = !current

        PostHogSDK.shared.setup(config)
        if current {
            PostHogSDK.shared.optIn()
        } else {
            PostHogSDK.shared.optOut()
        }
        ready = true
    }

    static func setEnabled(_ enabled: Bool) {
        self.enabled = enabled
        if !enabled { lastScreen = nil }
        persistEnabled(enabled)
        guard ready else { return }
        if enabled {
            sink.optIn()
        } else {
            sink.optOut()
        }
    }

    static func screen(_ name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty, ready, enabled, lastScreen != trimmed else { return }
        lastScreen = trimmed
        sink.screen(trimmed, properties: ["platform": platform])
    }

    static func flush() {
        guard ready else { return }
        sink.flush()
    }

    private static func capture(_ event: String, properties: [String: Any]) {
        guard ready, enabled else { return }
        sink.capture(event, properties: properties)
    }

    // MARK: - Typed Event Tracking

    static func trackBookImported(format formatRaw: String?, source sourceRaw: String?) {
        capture(
            "book_imported",
            properties: [
                "format": normalizeBookFormat(formatRaw),
                "source": normalizeImportSource(sourceRaw),
                "platform": platform,
            ]
        )
    }

    static func trackReadingOpened(format formatRaw: String?, paperTheme paperThemeRaw: String?) {
        capture(
            "reading_opened",
            properties: [
                "format": normalizeReadingFormat(formatRaw),
                "paper_theme": normalizePaperTheme(paperThemeRaw),
                "platform": platform,
            ]
        )
    }

    static func trackTtsStarted(engine engineRaw: String?, surface surfaceRaw: String?) {
        capture(
            "tts_started",
            properties: [
                "engine": normalizeTtsEngine(engineRaw),
                "surface": normalizeTtsSurface(surfaceRaw),
                "platform": platform,
            ]
        )
    }

    static func trackAudioFeatureUsed(kind: String) {
        guard let normalized = normalizeAudioFeatureKind(kind) else { return }
        capture(
            "audio_feature_used",
            properties: [
                "kind": normalized,
                "platform": platform,
            ]
        )
    }

    static func trackFeedbackSubmitted(imageCount: Int, includeDeviceInfo: Bool) {
        let clamped = min(max(imageCount, 0), 4)
        capture(
            "feedback_submitted",
            properties: [
                "has_images": clamped > 0,
                "image_count": clamped,
                "include_device_info": includeDeviceInfo,
                "platform": platform,
            ]
        )
    }

    // MARK: - Pure Normalizers

    nonisolated static func normalizeBookFormat(_ raw: String?) -> String {
        guard var key = raw?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(), !key.isEmpty else {
            return "other"
        }
        if key.hasPrefix(".") { key.removeFirst() }
        switch key {
        case "epub": return "epub"
        case "txt", "text": return "txt"
        case "pdf": return "pdf"
        case "mobi", "azw", "prc": return "mobi"
        case "azw3", "kf8": return "azw3"
        case "audio", "audiobook", "m4b", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav": return "audio"
        default: return "other"
        }
    }

    nonisolated static func normalizeReadingFormat(_ raw: String?) -> String {
        guard var key = raw?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(), !key.isEmpty else {
            return "other"
        }
        if key.hasPrefix(".") { key.removeFirst() }
        switch key {
        case "epub": return "epub"
        case "txt", "text": return "txt"
        case "pdf": return "pdf"
        case "mobi", "azw", "prc": return "mobi"
        case "azw3", "kf8": return "azw3"
        default: return "other"
        }
    }

    nonisolated static func resolveBookFormat(_ format: BookFormat?, fileNameOrExt: String? = nil) -> String {
        let trimmed = fileNameOrExt?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let ext: String
        if let dot = trimmed.lastIndex(of: ".") {
            ext = String(trimmed[trimmed.index(after: dot)...]).lowercased()
        } else {
            ext = trimmed.lowercased()
        }
        if ext == "azw3" || ext == "kf8" { return "azw3" }
        switch format {
        case .epub: return "epub"
        case .txt: return "txt"
        case .pdf: return "pdf"
        case .mobi: return "mobi"
        case .audio: return "audio"
        default: return normalizeBookFormat(ext)
        }
    }

    nonisolated static func normalizeImportSource(_ raw: String?) -> String {
        guard let raw else { return "other" }
        let key = raw
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "-", with: "_")
            .replacingOccurrences(of: " ", with: "_")
        switch key {
        case "file_picker", "picker", "files", "document_picker": return "file_picker"
        case "wifi", "wi_fi", "wifi_transfer": return "wifi"
        case "opds": return "opds"
        case "webdav", "dav": return "webdav"
        case "share_in", "share", "intent", "open_with": return "share_in"
        default: return "other"
        }
    }

    nonisolated static func normalizePaperTheme(_ raw: String?) -> String {
        guard let raw else { return "other" }
        let key = raw
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "_", with: "")
            .replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: " ", with: "")
        switch key {
        case "plain", "white", "light": return "plain"
        case "kami", "warm", "sepia": return "kami"
        case "dark", "night": return "dark"
        case "kaminight": return "kaminight"
        case "sage", "green": return "sage"
        case "eink": return "eink"
        default: return "other"
        }
    }

    nonisolated static func normalizeTtsEngine(_ raw: String?) -> String {
        guard let raw else { return "other" }
        let key = raw
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "-", with: "_")
            .replacingOccurrences(of: " ", with: "_")
        switch key {
        case "local_kokoro", "kokoro": return "local_kokoro"
        case "system", "apple", "avspeech", "ios", "macos", "android", "android_tts", "platform": return "system"
        case "elevenlabs", "eleven_labs": return "elevenlabs"
        case "openai", "open_ai": return "openai"
        case "edge", "ms_edge", "edge_tts": return "edge"
        case "azure", "ms_azure": return "azure"
        case "custom", "local", "actor", "voicestudio", "voicebox", "mlxaudio", "mlx_audio", "speaches": return "custom"
        default: return "other"
        }
    }

    nonisolated static func normalizeTtsSurface(_ raw: String?) -> String {
        guard let raw else { return "other" }
        let key = raw
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "-", with: "_")
            .replacingOccurrences(of: " ", with: "_")
        switch key {
        case "reader", "book", "pdf": return "reader"
        case "podcast", "studio": return "podcast"
        case "audiobook": return "audiobook"
        default: return "other"
        }
    }

    nonisolated static func normalizeAudioFeatureKind(_ raw: String?) -> String? {
        guard let raw else { return nil }
        let key = raw
            .trimmingCharacters(in: .whitespacesAndNewlines)
            .lowercased()
            .replacingOccurrences(of: "-", with: "_")
            .replacingOccurrences(of: " ", with: "_")
        switch key {
        case "audiobook_play",
             "audiobook_align",
             "podcast_added",
             "podcast_play",
             "podcast_transcribe":
            return key
        default:
            return nil
        }
    }

    nonisolated static func ttsEngine(for voice: (any Voice)?) -> String {
        let base: (any Voice)? = (voice as? TunedVoice)?.base ?? voice
        switch base {
        case is KokoroVoice:
            return "local_kokoro"
        case is SystemVoice:
            return "system"
        case let cloud as CloudVoice:
            return normalizeTtsEngine(cloud.provider.rawValue)
        case is ActorVoice:
            return "custom"
        case is CardVoice, is ClonedVoice:
            return "other"
        default:
            return "other"
        }
    }

    nonisolated static func ttsEngine(forVoiceID voiceID: String?) -> String {
        guard let id = voiceID?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(), !id.isEmpty else {
            return "other"
        }
        if id.hasPrefix("kokoro.") { return "local_kokoro" }
        if id.hasPrefix("system.") { return "system" }
        if id.hasPrefix("actor.") { return "custom" }
        if id.hasPrefix("cloud.") {
            let rest = id.dropFirst("cloud.".count)
            let provider = rest.split(separator: ".", maxSplits: 1).first.map(String.init)
            return normalizeTtsEngine(provider)
        }
        return "other"
    }

    // MARK: - Testing

    static func resetForTest(
        enabled: Bool = true,
        sink: (any AirTrafficSink)? = nil,
        persist: @escaping (Bool) -> Void = { _ in }
    ) {
        self.enabled = enabled
        self.lastScreen = nil
        self.persistEnabled = persist
        if let sink {
            self.sink = sink
            self.ready = true
        } else {
            self.sink = postHogSink
            self.ready = false
        }
    }
}

extension View {
    /// Reports a semantic `$screen` visit when this view appears or when its screen name changes.
    func airTrafficScreen(_ name: String) -> some View {
        onAppear { AirTraffic.screen(name) }
            .onChange(of: name) { _, new in AirTraffic.screen(new) }
    }
}
