import XCTest
import YuyueCore
@testable import Yuyue

@MainActor
private final class MockAirTrafficSink: AirTrafficSink {
    struct RecordedEvent {
        let name: String
        let properties: [String: Any]
    }

    var optInCount = 0
    var optOutCount = 0
    var flushCount = 0
    var screens: [RecordedEvent] = []
    var events: [RecordedEvent] = []

    func optIn() { optInCount += 1 }
    func optOut() { optOutCount += 1 }
    func screen(_ name: String, properties: [String: Any]) {
        screens.append(RecordedEvent(name: name, properties: properties))
    }
    func capture(_ event: String, properties: [String: Any]) {
        events.append(RecordedEvent(name: event, properties: properties))
    }
    func flush() { flushCount += 1 }
}

final class AirTrafficTests: XCTestCase {
    @MainActor
    override func tearDown() async throws {
        AirTraffic.resetForTest()
    }

    // MARK: - Normalizers

    func testNormalizeBookFormat() {
        XCTAssertEqual(AirTraffic.normalizeBookFormat("epub"), "epub")
        XCTAssertEqual(AirTraffic.normalizeBookFormat(" .EPUB "), "epub")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("txt"), "txt")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("text"), "txt")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("PDF"), "pdf")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("mobi"), "mobi")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("azw"), "mobi")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("prc"), "mobi")
        XCTAssertEqual(AirTraffic.normalizeBookFormat("azw3"), "azw3")
        XCTAssertEqual(AirTraffic.normalizeBookFormat(".KF8"), "azw3")
        for audioExt in ["audio", "audiobook", "m4b", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav"] {
            XCTAssertEqual(AirTraffic.normalizeBookFormat(audioExt), "audio", "Expected audio for \(audioExt)")
        }
        XCTAssertEqual(AirTraffic.normalizeBookFormat("fb2"), "other")
        XCTAssertEqual(AirTraffic.normalizeBookFormat(""), "other")
        XCTAssertEqual(AirTraffic.normalizeBookFormat(nil), "other")
    }

    func testNormalizeReadingFormatExcludesAudio() {
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("epub"), "epub")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("txt"), "txt")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("pdf"), "pdf")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("mobi"), "mobi")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("azw3"), "azw3")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("audio"), "other")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat("m4b"), "other")
        XCTAssertEqual(AirTraffic.normalizeReadingFormat(nil), "other")
    }

    func testResolveBookFormatDistinguishesAzw3() {
        XCTAssertEqual(AirTraffic.resolveBookFormat(.mobi, fileNameOrExt: "secret-book.azw3"), "azw3")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.mobi, fileNameOrExt: "book.KF8"), "azw3")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.mobi, fileNameOrExt: "book.mobi"), "mobi")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.epub, fileNameOrExt: nil), "epub")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.txt, fileNameOrExt: nil), "txt")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.pdf, fileNameOrExt: nil), "pdf")
        XCTAssertEqual(AirTraffic.resolveBookFormat(.audio, fileNameOrExt: nil), "audio")
        XCTAssertEqual(AirTraffic.resolveBookFormat(nil, fileNameOrExt: "track.m4b"), "audio")
        XCTAssertEqual(AirTraffic.resolveBookFormat(nil, fileNameOrExt: "unknown.docx"), "other")
    }

    func testNormalizeImportSource() {
        XCTAssertEqual(AirTraffic.normalizeImportSource("file_picker"), "file_picker")
        XCTAssertEqual(AirTraffic.normalizeImportSource("document-picker"), "file_picker")
        XCTAssertEqual(AirTraffic.normalizeImportSource("wi-fi"), "wifi")
        XCTAssertEqual(AirTraffic.normalizeImportSource("wifi_transfer"), "wifi")
        XCTAssertEqual(AirTraffic.normalizeImportSource("OPDS"), "opds")
        XCTAssertEqual(AirTraffic.normalizeImportSource("webdav"), "webdav")
        XCTAssertEqual(AirTraffic.normalizeImportSource("dav"), "webdav")
        XCTAssertEqual(AirTraffic.normalizeImportSource("share_in"), "share_in")
        XCTAssertEqual(AirTraffic.normalizeImportSource("open with"), "share_in")
        XCTAssertEqual(AirTraffic.normalizeImportSource("https://private.example.com"), "other")
        XCTAssertEqual(AirTraffic.normalizeImportSource(nil), "other")
    }

    func testNormalizePaperTheme() {
        XCTAssertEqual(AirTraffic.normalizePaperTheme("plain"), "plain")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("White"), "plain")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("kami"), "kami")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("sepia"), "kami")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("dark"), "dark")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("night"), "dark")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("kami_night"), "kaminight")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("kami-night"), "kaminight")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("sage"), "sage")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("e-ink"), "eink")
        XCTAssertEqual(AirTraffic.normalizePaperTheme("my-custom-paper-id"), "other")
        XCTAssertEqual(AirTraffic.normalizePaperTheme(nil), "other")
    }

    func testNormalizeTtsEngineAndVoiceID() {
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("kokoro"), "local_kokoro")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("local_kokoro"), "local_kokoro")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("system"), "system")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("avspeech"), "system")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("eleven_labs"), "elevenlabs")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("open_ai"), "openai")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("edge_tts"), "edge")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("ms_azure"), "azure")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("mlx_audio"), "custom")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("speaches"), "custom")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("voicestudio"), "custom")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine("volcengine"), "other")
        XCTAssertEqual(AirTraffic.normalizeTtsEngine(nil), "other")

        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "kokoro.zf_001"), "local_kokoro")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "system.com.apple.voice.compact.zh-CN.Tingting"), "system")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "cloud.openai.alloy"), "openai")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "cloud.elevenlabs.rachel"), "elevenlabs")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "cloud.edge.zh-CN-XiaoxiaoNeural"), "edge")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "cloud.azure.zh-CN-YunxiNeural"), "azure")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "cloud.local.myvoice"), "custom")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "actor.vivian"), "custom")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: "clone.my-secret-voice"), "other")
        XCTAssertEqual(AirTraffic.ttsEngine(forVoiceID: nil), "other")
    }

    func testNormalizeTtsSurfaceAndAudioFeatureKind() {
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("reader"), "reader")
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("pdf"), "reader")
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("podcast"), "podcast")
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("studio"), "podcast")
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("audiobook"), "audiobook")
        XCTAssertEqual(AirTraffic.normalizeTtsSurface("something-else"), "other")

        let validKinds = [
            "audiobook_play",
            "audiobook_align",
            "podcast_added",
            "podcast_play",
            "podcast_transcribe",
        ]
        for kind in validKinds {
            XCTAssertEqual(AirTraffic.normalizeAudioFeatureKind(kind), kind)
        }
        XCTAssertNil(AirTraffic.normalizeAudioFeatureKind("unknown_feature"))
        XCTAssertNil(AirTraffic.normalizeAudioFeatureKind(""))
        XCTAssertNil(AirTraffic.normalizeAudioFeatureKind(nil))
    }

    // MARK: - Event Payloads, Clamping, Dedupe, Opt-Out & Privacy Audit

    @MainActor
    func testFeedbackSubmittedClamping() {
        let sink = MockAirTrafficSink()
        AirTraffic.resetForTest(enabled: true, sink: sink)

        AirTraffic.trackFeedbackSubmitted(imageCount: -5, includeDeviceInfo: false)
        AirTraffic.trackFeedbackSubmitted(imageCount: 0, includeDeviceInfo: true)
        AirTraffic.trackFeedbackSubmitted(imageCount: 2, includeDeviceInfo: true)
        AirTraffic.trackFeedbackSubmitted(imageCount: 4, includeDeviceInfo: false)
        AirTraffic.trackFeedbackSubmitted(imageCount: 9, includeDeviceInfo: true)

        XCTAssertEqual(sink.events.count, 5)
        let expected: [(Bool, Int, Bool)] = [
            (false, 0, false),
            (false, 0, true),
            (true, 2, true),
            (true, 4, false),
            (true, 4, true),
        ]
        for (index, exp) in expected.enumerated() {
            let e = sink.events[index]
            XCTAssertEqual(e.name, "feedback_submitted")
            XCTAssertEqual(e.properties["has_images"] as? Bool, exp.0)
            XCTAssertEqual(e.properties["image_count"] as? Int, exp.1)
            XCTAssertEqual(e.properties["include_device_info"] as? Bool, exp.2)
            XCTAssertEqual(e.properties["platform"] as? String, AirTraffic.platform)
        }
    }

    @MainActor
    func testScreenDeduplication() {
        let sink = MockAirTrafficSink()
        AirTraffic.resetForTest(enabled: true, sink: sink)

        AirTraffic.screen("Shelf")
        AirTraffic.screen("Shelf")
        AirTraffic.screen("  Shelf  ")
        AirTraffic.screen("")
        AirTraffic.screen("   ")
        AirTraffic.screen("Reader")
        AirTraffic.screen("Reader")
        AirTraffic.screen("Shelf")

        XCTAssertEqual(sink.screens.map(\.name), ["Shelf", "Reader", "Shelf"])
        for recorded in sink.screens {
            XCTAssertEqual(Set(recorded.properties.keys), ["platform"])
            XCTAssertEqual(recorded.properties["platform"] as? String, AirTraffic.platform)
        }
    }

    @MainActor
    func testOptOutAndReEnable() {
        let sink = MockAirTrafficSink()
        var persisted: [Bool] = []
        AirTraffic.resetForTest(enabled: true, sink: sink) { persisted.append($0) }

        AirTraffic.screen("Shelf")
        XCTAssertEqual(sink.screens.count, 1)

        AirTraffic.setEnabled(false)
        XCTAssertFalse(AirTraffic.isEnabled)
        XCTAssertEqual(sink.optOutCount, 1)
        XCTAssertEqual(persisted, [false])

        // Suppressed while opted out.
        AirTraffic.screen("Reader")
        AirTraffic.trackBookImported(format: "epub", source: "file_picker")
        AirTraffic.trackReadingOpened(format: "epub", paperTheme: "kami")
        AirTraffic.trackTtsStarted(engine: "kokoro", surface: "reader")
        AirTraffic.trackAudioFeatureUsed(kind: "audiobook_play")
        AirTraffic.trackFeedbackSubmitted(imageCount: 1, includeDeviceInfo: true)
        XCTAssertEqual(sink.screens.count, 1)
        XCTAssertTrue(sink.events.isEmpty)

        // Re-enabling calls optIn and resets lastScreen so the same screen can be recorded again.
        AirTraffic.setEnabled(true)
        XCTAssertTrue(AirTraffic.isEnabled)
        XCTAssertEqual(sink.optInCount, 1)
        XCTAssertEqual(persisted, [false, true])

        AirTraffic.screen("Shelf")
        AirTraffic.trackReadingOpened(format: "pdf", paperTheme: "dark")
        XCTAssertEqual(sink.screens.map(\.name), ["Shelf", "Shelf"])
        XCTAssertEqual(sink.events.map(\.name), ["reading_opened"])
    }

    @MainActor
    func testAllEventsContainOnlyClosedEnumKeysAndZeroFreeText() {
        let sink = MockAirTrafficSink()
        AirTraffic.resetForTest(enabled: true, sink: sink)

        AirTraffic.trackBookImported(format: "My Secret Book.epub", source: "https://private-opds.local/feed")
        AirTraffic.trackReadingOpened(format: "azw3", paperTheme: "Custom Private Theme")
        AirTraffic.trackTtsStarted(engine: "elevenlabs", surface: "reader")
        AirTraffic.trackAudioFeatureUsed(kind: "podcast_transcribe")
        AirTraffic.trackAudioFeatureUsed(kind: "invalid_free_text_kind")
        AirTraffic.trackFeedbackSubmitted(imageCount: 2, includeDeviceInfo: true)
        AirTraffic.screen("Notes")

        let allowedKeys: [String: Set<String>] = [
            "book_imported": ["format", "source", "platform"],
            "reading_opened": ["format", "paper_theme", "platform"],
            "tts_started": ["engine", "surface", "platform"],
            "audio_feature_used": ["kind", "platform"],
            "feedback_submitted": ["has_images", "image_count", "include_device_info", "platform"],
        ]

        XCTAssertEqual(sink.events.count, 5)
        for event in sink.events {
            XCTAssertEqual(Set(event.properties.keys), allowedKeys[event.name], "Unexpected keys for \(event.name)")
            for (k, v) in event.properties {
                let s = String(describing: v)
                XCTAssertFalse(s.contains("Secret"), "Free text leaked in \(event.name).\(k)")
                XCTAssertFalse(s.contains("http"), "URL leaked in \(event.name).\(k)")
                XCTAssertFalse(s.contains("Private"), "Custom name leaked in \(event.name).\(k)")
            }
        }
        XCTAssertEqual(sink.screens.map(\.name), ["Notes"])
        XCTAssertEqual(Set(sink.screens[0].properties.keys), ["platform"])
    }
}
