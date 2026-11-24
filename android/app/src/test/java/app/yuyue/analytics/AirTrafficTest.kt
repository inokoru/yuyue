package app.yuyue.analytics

import app.yuyue.speech.CloudVoice
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.Paper

class AirTrafficTest {

    private data class RecordedScreen(val name: String, val properties: Map<String, Any>)
    private data class RecordedEvent(val name: String, val properties: Map<String, Any>)

    private class FakeSink : AirTraffic.EventSink {
        val screens = mutableListOf<RecordedScreen>()
        val events = mutableListOf<RecordedEvent>()
        var optInCalls = 0
        var optOutCalls = 0
        var flushCalls = 0

        override fun optIn() {
            optInCalls++
        }

        override fun optOut() {
            optOutCalls++
        }

        override fun screen(name: String, properties: Map<String, Any>) {
            screens += RecordedScreen(name, properties)
        }

        override fun capture(event: String, properties: Map<String, Any>) {
            events += RecordedEvent(event, properties)
        }

        override fun flush() {
            flushCalls++
        }
    }

    private lateinit var sink: FakeSink
    private val persisted = mutableListOf<Boolean>()

    @Before
    fun setUp() {
        sink = FakeSink()
        persisted.clear()
        AirTraffic.resetForTest(enabled = true, sink = sink) { persisted += it }
    }

    @After
    fun tearDown() {
        AirTraffic.resetForTest(enabled = true, sink = null)
    }

    @Test
    fun bookAndReadingFormatNormalizersHandleKnownExtensionsAndFallbacks() {
        assertEquals("epub", AirTraffic.normalizeBookFormat("EPUB"))
        assertEquals("epub", AirTraffic.normalizeBookFormat(" .epub "))
        assertEquals("txt", AirTraffic.normalizeBookFormat("TXT"))
        assertEquals("txt", AirTraffic.normalizeBookFormat("text"))
        assertEquals("pdf", AirTraffic.normalizeBookFormat(".PDF"))
        assertEquals("mobi", AirTraffic.normalizeBookFormat("mobi"))
        assertEquals("mobi", AirTraffic.normalizeBookFormat("azw"))
        assertEquals("azw3", AirTraffic.normalizeBookFormat("AZW3"))
        assertEquals("azw3", AirTraffic.normalizeBookFormat(".kf8"))
        assertEquals("audio", AirTraffic.normalizeBookFormat("AUDIO"))
        assertEquals("audio", AirTraffic.normalizeBookFormat("m4b"))
        assertEquals("audio", AirTraffic.normalizeBookFormat(".mp3"))
        assertEquals("other", AirTraffic.normalizeBookFormat("docx"))
        assertEquals("other", AirTraffic.normalizeBookFormat(null))
        assertEquals("other", AirTraffic.normalizeBookFormat("   "))

        // Reading format excludes audio and falls back to other.
        assertEquals("epub", AirTraffic.normalizeReadingFormat("EPUB"))
        assertEquals("txt", AirTraffic.normalizeReadingFormat("txt"))
        assertEquals("pdf", AirTraffic.normalizeReadingFormat("pdf"))
        assertEquals("mobi", AirTraffic.normalizeReadingFormat("mobi"))
        assertEquals("azw3", AirTraffic.normalizeReadingFormat("azw3"))
        assertEquals("other", AirTraffic.normalizeReadingFormat("audio"))
        assertEquals("other", AirTraffic.normalizeReadingFormat("fb2"))
        assertEquals("other", AirTraffic.normalizeReadingFormat(null))

        // Core maps .azw3 files to BookFormat.MOBI; filename extension must win.
        assertEquals("azw3", AirTraffic.resolveBookFormat(BookFormat.MOBI, "SecretBook.AZW3"))
        assertEquals("mobi", AirTraffic.resolveBookFormat(BookFormat.MOBI, "SecretBook.mobi"))
        assertEquals("epub", AirTraffic.resolveBookFormat(BookFormat.EPUB, "book.epub"))
        assertEquals("txt", AirTraffic.resolveBookFormat(BookFormat.TXT, null))
        assertEquals("pdf", AirTraffic.resolveBookFormat(BookFormat.PDF, null))
        assertEquals("audio", AirTraffic.resolveBookFormat(BookFormat.AUDIO, "chapter1.m4b"))
        assertEquals("other", AirTraffic.resolveBookFormat(BookFormat.PODCAST, null))
    }

    @Test
    fun importSourceNormalizerCoversAllAllowedSourcesAndFallback() {
        assertEquals("file_picker", AirTraffic.normalizeImportSource("file_picker"))
        assertEquals("file_picker", AirTraffic.normalizeImportSource("file-picker"))
        assertEquals("wifi", AirTraffic.normalizeImportSource("WiFi"))
        assertEquals("wifi", AirTraffic.normalizeImportSource("wi-fi"))
        assertEquals("opds", AirTraffic.normalizeImportSource("OPDS"))
        assertEquals("webdav", AirTraffic.normalizeImportSource("WebDAV"))
        assertEquals("share_in", AirTraffic.normalizeImportSource("share_in"))
        assertEquals("share_in", AirTraffic.normalizeImportSource("share-in"))
        assertEquals("other", AirTraffic.normalizeImportSource("watched_folder"))
        assertEquals("other", AirTraffic.normalizeImportSource("https://private.example.com/book.epub"))
        assertEquals("other", AirTraffic.normalizeImportSource(null))
    }

    @Test
    fun paperThemeNormalizerMapsAllCorePresetsAndCustomFallback() {
        assertEquals("plain", AirTraffic.normalizePaperTheme(Paper.PLAIN.name))
        assertEquals("kami", AirTraffic.normalizePaperTheme(Paper.KAMI.name))
        assertEquals("dark", AirTraffic.normalizePaperTheme(Paper.DARK.name))
        assertEquals("kaminight", AirTraffic.normalizePaperTheme(Paper.KAMI_NIGHT.name))
        assertEquals("kaminight", AirTraffic.normalizePaperTheme("kami-night"))
        assertEquals("kaminight", AirTraffic.normalizePaperTheme("kaminight"))
        assertEquals("sage", AirTraffic.normalizePaperTheme(Paper.SAGE.name))
        assertEquals("eink", AirTraffic.normalizePaperTheme(Paper.EINK.name))
        assertEquals("eink", AirTraffic.normalizePaperTheme("e-ink"))
        assertEquals("other", AirTraffic.normalizePaperTheme("my-custom-paper-uuid"))
        assertEquals("other", AirTraffic.normalizePaperTheme(null))
    }

    @Test
    fun ttsEngineAndSurfaceNormalizersCoverAllEnginesProvidersAndSurfaces() {
        assertEquals("local_kokoro", AirTraffic.normalizeTtsEngine("local_kokoro"))
        assertEquals("local_kokoro", AirTraffic.normalizeTtsEngine("kokoro"))
        assertEquals("system", AirTraffic.normalizeTtsEngine("system"))
        assertEquals("elevenlabs", AirTraffic.normalizeTtsEngine("elevenlabs"))
        assertEquals("openai", AirTraffic.normalizeTtsEngine("OpenAI"))
        assertEquals("edge", AirTraffic.normalizeTtsEngine("edge"))
        assertEquals("azure", AirTraffic.normalizeTtsEngine("Azure"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("custom"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("local"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("voicestudio"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("voicebox"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("mlxaudio"))
        assertEquals("custom", AirTraffic.normalizeTtsEngine("speaches"))
        assertEquals("other", AirTraffic.normalizeTtsEngine("volcengine"))
        assertEquals("other", AirTraffic.normalizeTtsEngine("free"))
        assertEquals("other", AirTraffic.normalizeTtsEngine(null))

        // Verify voice ID inference & every CloudVoice.Provider entry produces an allowed value.
        val allowedEngines = setOf("local_kokoro", "system", "elevenlabs", "openai", "edge", "azure", "custom", "other")
        for (provider in CloudVoice.Provider.entries) {
            val mapped = AirTraffic.normalizeTtsEngine(provider.key)
            assertTrue("Unexpected engine '$mapped' for provider ${provider.key}", mapped in allowedEngines)
        }
        assertEquals("local_kokoro", AirTraffic.ttsEngineForVoiceId("kokoro.zf_001"))
        assertEquals("system", AirTraffic.ttsEngineForVoiceId("system.cmn-cn-x-ccc"))
        assertEquals("openai", AirTraffic.ttsEngineForVoiceId("cloud.openai.alloy"))
        assertEquals("elevenlabs", AirTraffic.ttsEngineForVoiceId("cloud.elevenlabs.rachel"))
        assertEquals("edge", AirTraffic.ttsEngineForVoiceId("cloud.edge.zh-CN-XiaoxiaoNeural"))
        assertEquals("azure", AirTraffic.ttsEngineForVoiceId("cloud.azure.en-US-JennyNeural"))
        assertEquals("custom", AirTraffic.ttsEngineForVoiceId("cloud.voicestudio.myvoice"))
        assertEquals("other", AirTraffic.ttsEngineForVoiceId("card.abc"))
        assertEquals("other", AirTraffic.ttsEngineForVoiceId(null))

        assertEquals("reader", AirTraffic.normalizeTtsSurface("reader"))
        assertEquals("podcast", AirTraffic.normalizeTtsSurface("podcast"))
        assertEquals("audiobook", AirTraffic.normalizeTtsSurface("audiobook"))
        assertEquals("other", AirTraffic.normalizeTtsSurface("unknown"))
        assertEquals("other", AirTraffic.normalizeTtsSurface(null))
    }

    @Test
    fun audioFeatureKindsAllowOnlyClosedEnumAndDropUnknowns() {
        val allowed = listOf(
            "audiobook_play",
            "audiobook_align",
            "podcast_added",
            "podcast_play",
            "podcast_transcribe",
        )
        for (kind in allowed) {
            assertEquals(kind, AirTraffic.normalizeAudioFeatureKind(kind))
            AirTraffic.trackAudioFeatureUsed(kind)
        }
        assertNull(AirTraffic.normalizeAudioFeatureKind("arbitrary_text"))
        assertNull(AirTraffic.normalizeAudioFeatureKind("https://example.com/feed.xml"))
        assertNull(AirTraffic.normalizeAudioFeatureKind(""))
        AirTraffic.trackAudioFeatureUsed("arbitrary_text")

        assertEquals(allowed.size, sink.events.size)
        for ((index, kind) in allowed.withIndex()) {
            assertEquals("audio_feature_used", sink.events[index].name)
            assertEquals(mapOf("kind" to kind, "platform" to "android"), sink.events[index].properties)
        }
    }

    @Test
    fun feedbackSubmittedClampsImageCountAndSetsHasImages() {
        AirTraffic.trackFeedbackSubmitted(imageCount = -2, includeDeviceInfo = false)
        AirTraffic.trackFeedbackSubmitted(imageCount = 0, includeDeviceInfo = true)
        AirTraffic.trackFeedbackSubmitted(imageCount = 2, includeDeviceInfo = true)
        AirTraffic.trackFeedbackSubmitted(imageCount = 9, includeDeviceInfo = false)

        assertEquals(4, sink.events.size)
        assertEquals(
            mapOf("has_images" to false, "image_count" to 0, "include_device_info" to false, "platform" to "android"),
            sink.events[0].properties,
        )
        assertEquals(
            mapOf("has_images" to false, "image_count" to 0, "include_device_info" to true, "platform" to "android"),
            sink.events[1].properties,
        )
        assertEquals(
            mapOf("has_images" to true, "image_count" to 2, "include_device_info" to true, "platform" to "android"),
            sink.events[2].properties,
        )
        assertEquals(
            mapOf("has_images" to true, "image_count" to 4, "include_device_info" to false, "platform" to "android"),
            sink.events[3].properties,
        )
    }

    @Test
    fun optOutSuppressesScreensAndEventsAndOptInResumes() {
        assertTrue(AirTraffic.isEnabled())
        AirTraffic.screen("Shelf")
        AirTraffic.trackBookImported("epub", "file_picker")
        assertEquals(1, sink.screens.size)
        assertEquals(1, sink.events.size)

        AirTraffic.setEnabled(false)
        assertFalse(AirTraffic.isEnabled())
        assertEquals(1, sink.optOutCalls)
        assertEquals(listOf(false), persisted)

        AirTraffic.screen("Reader")
        AirTraffic.trackBookImported("pdf", "wifi")
        AirTraffic.trackReadingOpened("epub", "kami")
        AirTraffic.trackTtsStarted("local_kokoro", "reader")
        AirTraffic.trackAudioFeatureUsed("podcast_play")
        AirTraffic.trackFeedbackSubmitted(1, true)
        assertEquals(1, sink.screens.size)
        assertEquals(1, sink.events.size)

        AirTraffic.setEnabled(true)
        assertTrue(AirTraffic.isEnabled())
        assertEquals(1, sink.optInCalls)
        assertEquals(listOf(false, true), persisted)

        AirTraffic.screen("Shelf")
        AirTraffic.trackReadingOpened("pdf", "dark")
        assertEquals(2, sink.screens.size)
        assertEquals(2, sink.events.size)
        assertEquals("Shelf", sink.screens[1].name)
        assertEquals("reading_opened", sink.events[1].name)

        AirTraffic.flush()
        assertEquals(1, sink.flushCalls)
    }

    @Test
    fun screenDeduplicatesConsecutiveIdenticalNamesAndReemitsOnChange() {
        AirTraffic.screen("Shelf")
        AirTraffic.screen("Shelf")
        AirTraffic.screen("  Shelf  ")
        AirTraffic.screen("")
        AirTraffic.screen("   ")
        assertEquals(1, sink.screens.size)
        assertEquals(RecordedScreen("Shelf", mapOf("platform" to "android")), sink.screens[0])

        AirTraffic.screen("Reader")
        AirTraffic.screen("Reader")
        AirTraffic.screen("Shelf")
        assertEquals(listOf("Shelf", "Reader", "Shelf"), sink.screens.map { it.name })
    }

    @Test
    fun allEventsEmitOnlyStrictSchemaKeysAndNeverLeakFreeTextOrPii() {
        val secretTitle = "Three-Body Problem by Liu Cixin"
        val secretUrl = "https://user:pass@nas.example.com/books/secret.epub"
        val secretEmail = "reader@example.com"

        AirTraffic.trackBookImported(secretTitle, secretUrl)
        AirTraffic.trackReadingOpened(secretEmail, secretTitle)
        AirTraffic.trackTtsStarted(secretUrl, secretEmail)
        AirTraffic.trackAudioFeatureUsed("audiobook_play")
        AirTraffic.trackFeedbackSubmitted(3, includeDeviceInfo = true)
        AirTraffic.screen("Notes")

        val expectedKeysByEvent = mapOf(
            "book_imported" to setOf("format", "source", "platform"),
            "reading_opened" to setOf("format", "paper_theme", "platform"),
            "tts_started" to setOf("engine", "surface", "platform"),
            "audio_feature_used" to setOf("kind", "platform"),
            "feedback_submitted" to setOf("has_images", "image_count", "include_device_info", "platform"),
        )
        val forbiddenKeys = setOf(
            "title", "book_title", "author", "filename", "file_name", "path",
            "query", "search_query", "url", "email", "error", "message", "text", "note",
        )

        assertEquals(5, sink.events.size)
        for (event in sink.events) {
            assertEquals(expectedKeysByEvent[event.name], event.properties.keys)
            for (k in event.properties.keys) {
                assertFalse("Forbidden key $k in ${event.name}", k in forbiddenKeys)
            }
            assertEquals("android", event.properties["platform"])
            for (v in event.properties.values) {
                val str = v.toString()
                assertFalse("Leaked title in $event", str.contains("Three-Body"))
                assertFalse("Leaked URL in $event", str.contains("nas.example.com"))
                assertFalse("Leaked email in $event", str.contains("reader@example.com"))
            }
        }

        // Verify free-text inputs collapsed cleanly to "other".
        assertEquals(mapOf("format" to "other", "source" to "other", "platform" to "android"), sink.events[0].properties)
        assertEquals(mapOf("format" to "other", "paper_theme" to "other", "platform" to "android"), sink.events[1].properties)
        assertEquals(mapOf("engine" to "other", "surface" to "other", "platform" to "android"), sink.events[2].properties)
        assertEquals(setOf("platform"), sink.screens.single().properties.keys)
    }
}
