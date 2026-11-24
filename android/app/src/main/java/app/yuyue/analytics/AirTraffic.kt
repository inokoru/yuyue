package app.yuyue.analytics

import android.content.Context
import app.yuyue.design.Prefs
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.KokoroVoice
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.TunedVoice
import com.posthog.PersonProfiles
import com.posthog.PostHog
import com.posthog.android.PostHogAndroid
import com.posthog.android.PostHogAndroidConfig
import uniffi.yuyue_core.BookFormat

/**
 * Anonymous usage analytics (AirTraffic / PostHog).
 *
 * Strictly records usage shapes only (closed enum properties + semantic screen names).
 * Never calls `identify` or `alias`, and never transmits free text (book titles, authors,
 * filenames, search queries, URLs, emails, or error messages).
 */
object AirTraffic {
    const val PREF_KEY = "analytics.enabled"

    private const val APP_KEY = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg"
    private const val TRAFFIC_HOST = "https://apps.air.build/traffic"
    private const val PLATFORM = "android"

    internal interface EventSink {
        fun optIn()
        fun optOut()
        fun screen(name: String, properties: Map<String, Any>)
        fun capture(event: String, properties: Map<String, Any>)
        fun flush()
    }

    private val postHogSink = object : EventSink {
        override fun optIn() {
            PostHog.optIn()
        }

        override fun optOut() {
            PostHog.optOut()
        }

        override fun screen(name: String, properties: Map<String, Any>) {
            PostHog.screen(name, properties = properties)
        }

        override fun capture(event: String, properties: Map<String, Any>) {
            PostHog.capture(event, properties = properties)
        }

        override fun flush() {
            PostHog.flush()
        }
    }

    private val lock = Any()
    @Volatile private var ready = false
    @Volatile private var enabled = true
    @Volatile private var lastScreen: String? = null
    @Volatile private var sink: EventSink = postHogSink
    @Volatile private var persistEnabled: (Boolean) -> Unit = { value ->
        if (Prefs.isReady) Prefs.set(PREF_KEY, value)
    }

    fun init(context: Context) {
        synchronized(lock) {
            if (ready && sink === postHogSink) return
            val current = if (Prefs.isReady) Prefs.bool(PREF_KEY, true) else true
            enabled = current
            lastScreen = null
            sink = postHogSink
            val config = PostHogAndroidConfig(
                projectToken = APP_KEY,
                host = TRAFFIC_HOST,
            ).apply {
                captureApplicationLifecycleEvents = true
                captureScreenViews = false
                captureDeepLinks = false
                capturePushNotificationSubscriptions = false
                capturePushNotificationOpened = false
                sessionReplay = false
                surveys = false
                preloadFeatureFlags = false
                sendFeatureFlagEvent = false
                personProfiles = PersonProfiles.NEVER
                errorTrackingConfig.autoCapture = false
                optOut = !current
            }
            PostHogAndroid.setup(context, config)
            if (current) PostHog.optIn() else PostHog.optOut()
            ready = true
        }
    }

    fun isEnabled(): Boolean = enabled

    fun setEnabled(enabled: Boolean) {
        val currentSink: EventSink?
        synchronized(lock) {
            this.enabled = enabled
            if (!enabled) lastScreen = null
            persistEnabled(enabled)
            currentSink = if (ready) sink else null
        }
        currentSink?.let { if (enabled) it.optIn() else it.optOut() }
    }

    fun screen(name: String) {
        val trimmed = name.trim()
        if (trimmed.isEmpty()) return
        val currentSink: EventSink
        synchronized(lock) {
            if (!ready || !enabled) return
            if (lastScreen == trimmed) return
            lastScreen = trimmed
            currentSink = sink
        }
        currentSink.screen(trimmed, mapOf("platform" to PLATFORM))
    }

    fun flush() {
        val currentSink = synchronized(lock) { if (ready) sink else null }
        currentSink?.flush()
    }

    private fun capture(event: String, properties: Map<String, Any>) {
        val currentSink = synchronized(lock) {
            if (!ready || !enabled) return
            sink
        }
        currentSink.capture(event, properties)
    }

    // MARK: - Typed Event Tracking

    fun trackBookImported(formatRaw: String?, sourceRaw: String?) {
        capture(
            "book_imported",
            mapOf(
                "format" to normalizeBookFormat(formatRaw),
                "source" to normalizeImportSource(sourceRaw),
                "platform" to PLATFORM,
            ),
        )
    }

    fun trackReadingOpened(formatRaw: String?, paperThemeRaw: String?) {
        capture(
            "reading_opened",
            mapOf(
                "format" to normalizeReadingFormat(formatRaw),
                "paper_theme" to normalizePaperTheme(paperThemeRaw),
                "platform" to PLATFORM,
            ),
        )
    }

    fun trackTtsStarted(engineRaw: String?, surfaceRaw: String?) {
        capture(
            "tts_started",
            mapOf(
                "engine" to normalizeTtsEngine(engineRaw),
                "surface" to normalizeTtsSurface(surfaceRaw),
                "platform" to PLATFORM,
            ),
        )
    }

    fun trackAudioFeatureUsed(kind: String) {
        val normalized = normalizeAudioFeatureKind(kind) ?: return
        capture(
            "audio_feature_used",
            mapOf(
                "kind" to normalized,
                "platform" to PLATFORM,
            ),
        )
    }

    fun trackFeedbackSubmitted(imageCount: Int, includeDeviceInfo: Boolean) {
        val clamped = imageCount.coerceIn(0, 4)
        capture(
            "feedback_submitted",
            mapOf(
                "has_images" to (clamped > 0),
                "image_count" to clamped,
                "include_device_info" to includeDeviceInfo,
                "platform" to PLATFORM,
            ),
        )
    }

    // MARK: - Pure Normalizers (Host-JVM Testable)

    fun normalizeBookFormat(raw: String?): String {
        val key = raw?.trim()?.removePrefix(".")?.lowercase() ?: return "other"
        return when (key) {
            "epub" -> "epub"
            "txt", "text" -> "txt"
            "pdf" -> "pdf"
            "mobi", "azw", "prc" -> "mobi"
            "azw3", "kf8" -> "azw3"
            "audio", "audiobook", "m4b", "m4a", "mp3", "aac", "flac", "ogg", "opus", "wav" -> "audio"
            else -> "other"
        }
    }

    fun normalizeReadingFormat(raw: String?): String {
        val key = raw?.trim()?.removePrefix(".")?.lowercase() ?: return "other"
        return when (key) {
            "epub" -> "epub"
            "txt", "text" -> "txt"
            "pdf" -> "pdf"
            "mobi", "azw", "prc" -> "mobi"
            "azw3", "kf8" -> "azw3"
            else -> "other"
        }
    }

    fun resolveBookFormat(format: BookFormat?, fileNameOrExt: String? = null): String {
        val ext = fileNameOrExt?.trim()
            ?.substringAfterLast('.', missingDelimiterValue = fileNameOrExt.trim())
            ?.lowercase()
        if (ext == "azw3" || ext == "kf8") return "azw3"
        return when (format) {
            BookFormat.EPUB -> "epub"
            BookFormat.TXT -> "txt"
            BookFormat.PDF -> "pdf"
            BookFormat.MOBI -> "mobi"
            BookFormat.AUDIO -> "audio"
            else -> normalizeBookFormat(ext)
        }
    }

    fun normalizeImportSource(raw: String?): String {
        val key = raw?.trim()?.lowercase()?.replace('-', '_')?.replace(' ', '_') ?: return "other"
        return when (key) {
            "file_picker", "picker", "files", "document_picker" -> "file_picker"
            "wifi", "wi_fi", "wifi_transfer" -> "wifi"
            "opds" -> "opds"
            "webdav", "dav" -> "webdav"
            "share_in", "share", "intent", "open_with" -> "share_in"
            else -> "other"
        }
    }

    fun normalizePaperTheme(raw: String?): String {
        val key = raw?.trim()?.lowercase()?.replace("_", "")?.replace("-", "")?.replace(" ", "") ?: return "other"
        return when (key) {
            "plain", "white", "light" -> "plain"
            "kami", "warm", "sepia" -> "kami"
            "dark", "night" -> "dark"
            "kaminight" -> "kaminight"
            "sage", "green" -> "sage"
            "eink" -> "eink"
            else -> "other"
        }
    }

    fun normalizeTtsEngine(raw: String?): String {
        val key = raw?.trim()?.lowercase()?.replace('-', '_')?.replace(' ', '_') ?: return "other"
        return when (key) {
            "local_kokoro", "kokoro" -> "local_kokoro"
            "system", "android", "android_tts", "platform" -> "system"
            "elevenlabs", "eleven_labs" -> "elevenlabs"
            "openai", "open_ai" -> "openai"
            "edge", "ms_edge", "edge_tts" -> "edge"
            "azure", "ms_azure" -> "azure"
            "custom", "local", "voicestudio", "voicebox", "mlxaudio", "mlx_audio", "speaches" -> "custom"
            else -> "other"
        }
    }

    fun normalizeTtsSurface(raw: String?): String {
        val key = raw?.trim()?.lowercase()?.replace('-', '_')?.replace(' ', '_') ?: return "other"
        return when (key) {
            "reader", "book", "pdf" -> "reader"
            "podcast", "studio" -> "podcast"
            "audiobook" -> "audiobook"
            else -> "other"
        }
    }

    fun normalizeAudioFeatureKind(raw: String?): String? {
        val key = raw?.trim()?.lowercase()?.replace('-', '_')?.replace(' ', '_') ?: return null
        return when (key) {
            "audiobook_play",
            "audiobook_align",
            "podcast_added",
            "podcast_play",
            "podcast_transcribe" -> key
            else -> null
        }
    }

    fun ttsEngineForVoice(voice: BookVoice?): String {
        val raw = (voice as? TunedVoice)?.base ?: voice
        return when (raw) {
            is KokoroVoice -> "local_kokoro"
            is SystemVoice -> "system"
            is CloudVoice -> normalizeTtsEngine(raw.provider.key)
            is CardVoice, is ClonedVoice -> "other"
            else -> "other"
        }
    }

    fun ttsEngineForVoiceId(voiceId: String?): String {
        val id = voiceId?.trim()?.lowercase() ?: return "other"
        if (id.isEmpty()) return "other"
        return when {
            id.startsWith("kokoro.") -> "local_kokoro"
            id.startsWith("system.") -> "system"
            id.startsWith("cloud.") -> normalizeTtsEngine(id.removePrefix("cloud.").substringBefore('.'))
            else -> "other"
        }
    }

    // MARK: - Testing

    internal fun resetForTest(
        enabled: Boolean = true,
        sink: EventSink? = null,
        persist: (Boolean) -> Unit = {},
    ) {
        synchronized(lock) {
            this.enabled = enabled
            this.lastScreen = null
            this.persistEnabled = persist
            if (sink != null) {
                this.sink = sink
                this.ready = true
            } else {
                this.sink = postHogSink
                this.ready = false
            }
        }
    }
}
