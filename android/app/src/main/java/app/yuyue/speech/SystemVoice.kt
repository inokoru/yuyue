package app.yuyue.speech

import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.speech.tts.TextToSpeech
import android.speech.tts.UtteranceProgressListener
import android.speech.tts.Voice
import app.yuyue.t
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import java.io.ByteArrayOutputStream
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Locale
import java.util.UUID

/** One rendered sentence: 16-bit mono samples at `rate`. Every voice renders to this, so they
 *  all play through the same `Player`. */
class Speech(val samples: ShortArray, val rate: Int) {
    val seconds: Double get() = samples.size.toDouble() / rate
}

/** Something that reads a sentence aloud: the system's voices now, the cloud ones too. */
interface BookVoice {
    val id: String
    val name: String
    /** "高级", or the cloud it comes from: said after the name. */
    val qualityName: String?
    val isOffline: Boolean
    fun speaks(tag: String): Boolean
    /** The sentence as audio; null when there is nothing to say. */
    suspend fun render(text: String, language: String): Speech?
}

/** Book languages are BCP 47 tags (zh-Hans, en, …); voices use locales (zh-CN, zh-Hans-CN, cmn-CN, zho-CHN, en-US, …). */
object SpokenLanguage {
    internal fun normalizeTag(tag: String): String {
        val raw = tag.trim().replace('_', '-').lowercase()
        if (raw.isEmpty() || raw == "und") return ""
        val parts = raw.split('-').filter { it.isNotEmpty() }.toMutableList()
        if (parts.isEmpty()) return ""
        parts[0] = when (parts[0]) {
            "zho", "chi", "cmn" -> "zh"
            "eng" -> "en"
            "jpn" -> "ja"
            "kor" -> "ko"
            "fra", "fre" -> "fr"
            "deu", "ger" -> "de"
            "spa" -> "es"
            "rus" -> "ru"
            "por" -> "pt"
            "ita" -> "it"
            "ara" -> "ar"
            else -> parts[0]
        }
        for (i in 1 until parts.size) {
            parts[i] = when (parts[i]) {
                "chn" -> "cn"
                "twn" -> "tw"
                "hkg" -> "hk"
                "mac" -> "mo"
                "sgp" -> "sg"
                "usa" -> "us"
                "gbr" -> "gb"
                "jpn" -> "jp"
                "kor" -> "kr"
                else -> parts[i]
            }
        }
        return parts.joinToString("-")
    }

    internal fun localeTag(locale: Locale): String {
        val bcp = locale.toLanguageTag()
        if (bcp.isNotEmpty() && !bcp.equals("und", ignoreCase = true)) return normalizeTag(bcp)
        return normalizeTag(locale.toString())
    }

    fun localeForTag(tag: String): Locale {
        val t = normalizeTag(tag)
        return when {
            t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo") || t.startsWith("zh-yue") -> Locale.forLanguageTag("zh-HK")
            t.startsWith("zh-hant") || t.startsWith("zh-tw") -> Locale.TRADITIONAL_CHINESE
            t == "zh" || t.startsWith("zh-") -> Locale.SIMPLIFIED_CHINESE
            t == "en-gb" || t.startsWith("en-gb-") -> Locale.UK
            t == "en" || t.startsWith("en-") -> Locale.US
            t.isEmpty() -> Locale.getDefault()
            else -> Locale.forLanguageTag(t)
        }
    }

    fun voiceLanguages(tag: String): List<String> {
        val t = normalizeTag(tag)
        return when {
            t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo") || t.startsWith("zh-yue") -> listOf("zh-HK", "zh-TW", "zh-CN")
            t.startsWith("zh-hant") || t.startsWith("zh-tw") -> listOf("zh-TW", "zh-HK", "zh-CN")
            t == "zh" || t.startsWith("zh-") -> listOf("zh-CN", "zh-TW")
            t == "en" || t.startsWith("en-") -> listOf("en-US", "en-GB", "en")
            else -> listOf(t.substringBefore('-'))
        }
    }

    fun matches(voiceLanguage: String, tag: String): Boolean {
        val v = normalizeTag(voiceLanguage)
        val t = normalizeTag(tag)
        if (v.isEmpty() || t.isEmpty()) return false
        return when {
            t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo") || t.startsWith("zh-yue") ->
                v == "yue" || v.startsWith("yue-") || v == "zh" || v.startsWith("zh-")
            t.startsWith("zh-hant") || t.startsWith("zh-tw") ->
                v == "zh" || v.startsWith("zh-tw") || v.startsWith("zh-hant") ||
                    v.startsWith("zh-hk") || v.startsWith("zh-mo") ||
                    v.startsWith("zh-cn") || v.startsWith("zh-hans") || v.startsWith("zh-sg")
            t == "zh" || t.startsWith("zh-") ->
                v == "zh" || v.startsWith("zh-cn") || v.startsWith("zh-hans") || v.startsWith("zh-sg") ||
                    v.startsWith("zh-tw") || v.startsWith("zh-hant")
            t == "en" || t.startsWith("en-") ->
                v == "en" || v.startsWith("en-")
            else -> {
                val base = t.substringBefore('-')
                v == base || v.startsWith("$base-")
            }
        }
    }
}

/**
 * A voice of the phone's own TextToSpeech engine. The engine writes each sentence to a file
 * rather than speaking it, so its length is known before it plays and the ink line keeps pace;
 * the samples are collected as they arrive.
 *
 * When [isSystemDefault] is true (`id == DEFAULT_ID`), synthesis delegates directly to the phone's
 * configured system TTS engine and default voice (e.g. Samsung TTS, OnePlus/ColorOS TTS, or a
 * third-party engine like MultiTTS) without overriding the user's chosen voice inside OEM settings.
 */
class SystemVoice private constructor(
    private val voice: Voice?,
    val isSystemDefault: Boolean,
    override val id: String,
    override val name: String,
    override val qualityName: String?,
    override val isOffline: Boolean,
    private val defaultSupports: ((String) -> Boolean)? = null,
) : BookVoice {

    constructor(voice: Voice, number: Int = 0) : this(
        voice = voice,
        isSystemDefault = false,
        id = "system.${voice.name}",
        // The engine's own names (`en-us-x-iol-local`) say nothing to a person, so a language's voices
        // are lettered in the order they're listed: 声音 A, 声音 B…
        name = voice.locale.getDisplayName(Locale.getDefault()) + " · " +
            app.yuyue.t("声音 %@", if (number < 26) ('A' + number).toString() else "${number + 1}"),
        qualityName = if (voice.quality >= Voice.QUALITY_HIGH) app.yuyue.t("高级") else null,
        isOffline = !voice.isNetworkConnectionRequired,
    )

    companion object {
        const val DEFAULT_ID = "system.default"

        internal fun systemDefault(
            engineLabel: String?,
            isOffline: Boolean = true,
            supports: (String) -> Boolean = { true },
        ): SystemVoice = SystemVoice(
            voice = null,
            isSystemDefault = true,
            id = DEFAULT_ID,
            name = app.yuyue.t("系统默认语音"),
            qualityName = engineLabel?.takeIf { it.isNotBlank() } ?: app.yuyue.t("系统默认"),
            isOffline = isOffline,
            defaultSupports = supports,
        )
    }

    /** Gender hinted by the engine's voice id or features, before acoustic measurement. */
    val inferredGender: uniffi.yuyue_core.CharacterGender
        get() {
            val v = voice ?: return uniffi.yuyue_core.CharacterGender.UNKNOWN
            val tokens = (listOf(v.name) + (v.features ?: emptySet())).joinToString(" ").lowercase()
            return when {
                Regex("(^|[^a-z])(female|woman|girl)([^a-z]|$)").containsMatchIn(tokens) -> uniffi.yuyue_core.CharacterGender.F
                Regex("(^|[^a-z])(male|man|boy)([^a-z]|$)").containsMatchIn(tokens) -> uniffi.yuyue_core.CharacterGender.M
                else -> uniffi.yuyue_core.CharacterGender.UNKNOWN
            }
        }

    override fun speaks(tag: String): Boolean {
        val v = voice
        if (v != null) return SpokenLanguage.matches(SpokenLanguage.localeTag(v.locale), tag)
        return defaultSupports?.invoke(tag) ?: true
    }

    override suspend fun render(text: String, language: String): Speech? = render(text, language, 1f, 1f)

    /** 分角色: one character a little faster or slower, higher or lower. */
    suspend fun render(text: String, language: String, rate: Float, pitch: Float): Speech? {
        val plain = CardVoice.plainSpokenText(text)
        if (plain.none { it.isLetterOrDigit() }) return null
        return Engine.render(plain, voice, language, rate, pitch)
    }

    /** The one TextToSpeech the app keeps, and its voices. */
    object Engine {
        private var tts: TextToSpeech? = null
        private var ready = CompletableDeferred<Boolean>()
        @Volatile private var readyOk: Boolean? = null
        @Volatile private var boundEnginePkg: String? = null
        private var appContext: Context? = null
        /** Dedicated TTS client for 试听 so previewing never cancels or clears active synthesis on [tts]. */
        private var previewTts: TextToSpeech? = null
        @Volatile private var previewReadyOk: Boolean? = null
        @Volatile private var previewEnginePkg: String? = null
        private val previewLock = Any()
        private val previewMain by lazy { Handler(Looper.getMainLooper()) }
        private var previewToken = 0L
        private var previewOnDone: (() -> Unit)? = null
        private var pendingTtsPreview: PendingPreview? = null
        private var previewTrack: AudioTrack? = null
        private var previewWriter: Thread? = null
        private var previewEndWatch: Runnable? = null
        private val lock = Mutex()
        private lateinit var cache: File

        private data class PendingPreview(val token: Long, val voice: Voice?, val language: String, val text: String)

        private fun configuredDefaultSynth(context: Context): String? =
            runCatching { Settings.Secure.getString(context.contentResolver, "tts_default_synth") }
                .getOrNull()
                ?.takeIf { it.isNotBlank() }

        internal fun hasEngineChanged(wantedPkg: String?, boundPkg: String?): Boolean =
            !wantedPkg.isNullOrBlank() && !boundPkg.isNullOrBlank() && wantedPkg != boundPkg

        @Synchronized
        fun init(context: Context) {
            val ctx = context.applicationContext
            appContext = ctx
            val wantedPkg = configuredDefaultSynth(ctx)
                ?: runCatching { tts?.defaultEngine }.getOrNull()?.takeIf { it.isNotBlank() }
            if (tts != null) {
                val engineChanged = hasEngineChanged(wantedPkg, boundEnginePkg)
                if (readyOk != false && !engineChanged) return
                runCatching { tts?.shutdown() }
                tts = null
                ready = CompletableDeferred()
                readyOk = null
                synchronized(previewLock) {
                    runCatching { previewTts?.shutdown() }
                    previewTts = null
                    previewReadyOk = null
                    previewEnginePkg = null
                }
            }
            cache = File(ctx.cacheDir, "tts").apply { mkdirs() }
            boundEnginePkg = wantedPkg
            var created: TextToSpeech? = null
            created = TextToSpeech(ctx) { status ->
                val ok = status == TextToSpeech.SUCCESS
                if (ok) {
                    boundEnginePkg = runCatching { created?.defaultEngine ?: tts?.defaultEngine }.getOrNull() ?: wantedPkg
                }
                readyOk = ok
                ready.complete(ok)
            }
            tts = created
        }

        private suspend fun awaitReady(): Boolean =
            withTimeoutOrNull(5_000L) { ready.await() } == true

        /** Human-readable name of the phone's currently selected Android TTS engine (e.g. Samsung TTS, ColorOS/OnePlus TTS, MultiTTS). */
        fun defaultEngineLabel(): String? {
            val engine = tts ?: return null
            val pkg = runCatching { engine.defaultEngine }.getOrNull()?.takeIf { it.isNotBlank() }
                ?: boundEnginePkg
            val engines = runCatching { engine.engines }.getOrNull().orEmpty()
            return engines.firstOrNull { it.name == pkg }?.label?.takeIf { it.isNotBlank() }
                ?: pkg?.substringAfterLast('.')?.takeIf { it.isNotBlank() }
        }

        private fun buildSystemDefaultVoice(engine: TextToSpeech, enumerated: List<SystemVoice>): SystemVoice {
            val defVoice = runCatching { engine.defaultVoice }.getOrNull()
            val label = defaultEngineLabel()
            val offline = !(defVoice?.isNetworkConnectionRequired ?: false)
            val defLocaleTag = defVoice?.locale?.let { SpokenLanguage.localeTag(it) }.orEmpty()
            return SystemVoice.systemDefault(engineLabel = label, isOffline = offline) { tag ->
                if (enumerated.any { it.speaks(tag) }) return@systemDefault true
                if (defLocaleTag.isNotEmpty() && SpokenLanguage.matches(defLocaleTag, tag)) return@systemDefault true
                val avail = runCatching { engine.isLanguageAvailable(SpokenLanguage.localeForTag(tag)) }
                    .getOrDefault(TextToSpeech.LANG_NOT_SUPPORTED)
                if (avail >= TextToSpeech.LANG_AVAILABLE) return@systemDefault true
                // Some OEM / third-party TTS engines (e.g. custom ROM wrappers) omit getVoices() and
                // return LANG_NOT_SUPPORTED even though default synthesis works for the phone's locale.
                enumerated.isEmpty() && SpokenLanguage.matches(SpokenLanguage.localeTag(Locale.getDefault()), tag)
            }
        }

        suspend fun defaultSystemVoice(): SystemVoice? {
            if (!awaitReady()) return null
            val engine = tts ?: return null
            val named = enumeratedVoices(engine)
            return buildSystemDefaultVoice(engine, named)
        }

        private fun enumeratedVoices(engine: TextToSpeech): List<SystemVoice> {
            val defVoice = runCatching { engine.defaultVoice }.getOrNull()
            val raw = LinkedHashMap<String, Voice>()
            runCatching { engine.voices }.getOrNull().orEmpty().forEach { v ->
                if (v != null &&
                    v.locale != null &&
                    !v.name.isNullOrEmpty() &&
                    (v.features?.contains(TextToSpeech.Engine.KEY_FEATURE_NOT_INSTALLED) != true || v.name == defVoice?.name)
                ) {
                    raw[v.name] = v
                }
            }
            if (defVoice != null && defVoice.locale != null && !defVoice.name.isNullOrEmpty() && defVoice.name !in raw) {
                raw[defVoice.name] = defVoice
            }
            return raw.values
                .sortedWith(
                    compareByDescending<Voice> { it.name == defVoice?.name }
                        .thenBy { SpokenLanguage.localeTag(it.locale) }
                        .thenBy { it.isNetworkConnectionRequired }
                        .thenBy { -it.quality }
                )
                .let { all -> all.mapIndexed { i, v -> SystemVoice(v, all.take(i).count { it.locale == v.locale }) } }
        }

        suspend fun voices(): List<SystemVoice> {
            if (!awaitReady()) return emptyList()
            val engine = tts ?: return emptyList()
            val named = enumeratedVoices(engine)
            val def = buildSystemDefaultVoice(engine, named)
            return listOf(def) + named
        }

        private fun applyVoiceOrLanguage(engine: TextToSpeech, voice: Voice?, language: String) {
            if (voice != null) {
                runCatching { engine.voice = voice }
                return
            }
            val def = runCatching { engine.defaultVoice }.getOrNull()
            if (def != null && def.locale != null &&
                (language.isBlank() || SpokenLanguage.matches(SpokenLanguage.localeTag(def.locale), language))
            ) {
                runCatching { engine.voice = def }
            } else if (language.isNotBlank()) {
                runCatching { engine.setLanguage(SpokenLanguage.localeForTag(language)) }
            }
        }

        suspend fun render(
            text: String,
            voice: Voice?,
            language: String = "",
            speechRate: Float = 1f,
            speechPitch: Float = 1f,
        ): Speech? = lock.withLock {
            if (!awaitReady()) throw IllegalStateException(t("这台手机的文字转语音没准备好"))
            val engine = tts ?: throw IllegalStateException(t("这台手机的文字转语音没准备好"))
            val id = UUID.randomUUID().toString()
            val done = CompletableDeferred<Speech?>()
            val outLock = Any()
            val out = ByteArrayOutputStream()
            val file = File(cache, "$id.wav")
            var rate = 0
            var format = 0
            var channels = 1
            engine.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
                override fun onStart(utteranceId: String?) {}
                override fun onBeginSynthesis(utteranceId: String?, sampleRateInHz: Int, audioFormat: Int, channelCount: Int) {
                    if (utteranceId != id) return
                    synchronized(outLock) {
                        rate = sampleRateInHz
                        format = audioFormat
                        channels = maxOf(1, channelCount)
                    }
                }
                override fun onAudioAvailable(utteranceId: String?, audio: ByteArray) {
                    if (utteranceId != id) return
                    synchronized(outLock) {
                        out.write(audio)
                    }
                }
                override fun onDone(utteranceId: String?) {
                    if (utteranceId != id) return
                    // Engines that don't hand the audio over as it's made (or omit onBeginSynthesis) still wrote the file.
                    val fromStream = synchronized(outLock) {
                        if (out.size() > 0) pcm(out.toByteArray(), rate, format, channels) else null
                    }
                    done.complete(fromStream ?: wav(file))
                }
                @Deprecated("Deprecated in Java")
                override fun onError(utteranceId: String?) {
                    if (utteranceId != id) return
                    done.completeExceptionally(IllegalStateException(t("系统朗读合成失败")))
                }
                override fun onError(utteranceId: String?, errorCode: Int) {
                    if (utteranceId != id) return
                    done.completeExceptionally(IllegalStateException(t("系统朗读合成失败（%lld）", errorCode.toLong())))
                }
                override fun onStop(utteranceId: String?, interrupted: Boolean) {
                    if (utteranceId == id && !done.isCompleted) {
                        done.completeExceptionally(IllegalStateException(t("系统朗读合成失败")))
                    }
                }
            })
            var timedOut = true
            val speech = try {
                applyVoiceOrLanguage(engine, voice, language)
                engine.setSpeechRate(speechRate)
                engine.setPitch(speechPitch)
                val params = Bundle().apply {
                    putString(TextToSpeech.Engine.KEY_PARAM_UTTERANCE_ID, id)
                }
                if (engine.synthesizeToFile(text, params, file, id) == TextToSpeech.ERROR) {
                    throw IllegalStateException(t("系统朗读启动失败"))
                }
                // A sentence that never finishes must not stall the book.
                withTimeoutOrNull(20_000L + text.length * 300L) {
                    done.await().also { timedOut = false }
                }
            } finally {
                if (!done.isCompleted) runCatching { engine.stop() }
                file.delete()
                runCatching { engine.setOnUtteranceProgressListener(null) }
            }
            if (timedOut) throw IllegalStateException(t("系统朗读超时了"))
            speech
        }

        /** A PCM / IEEE-float WAV file's samples (downmixed to mono if multi-channel). */
        fun wav(file: File): Speech? {
            val b = runCatching { file.readBytes() }.getOrNull() ?: return null
            if (b.size < 44 || String(b, 0, 4) != "RIFF") return null
            val buf = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)
            var i = 12
            var audioFmt = 1
            var rate = 0
            var bits = 16
            var channels = 1
            while (i + 8 <= b.size) {
                val tag = String(b, i, 4)
                val size = buf.getInt(i + 4)
                if (size < 0) return null
                if (tag == "fmt " && i + 24 <= b.size) {
                    audioFmt = buf.getShort(i + 8).toInt() and 0xffff
                    channels = maxOf(1, buf.getShort(i + 10).toInt())
                    rate = buf.getInt(i + 12)
                    bits = buf.getShort(i + 22).toInt()
                } else if (tag == "data" && rate > 0) {
                    val bytesAvailable = minOf(size, b.size - i - 8)
                    if (bytesAvailable <= 0) return null
                    val dataStart = i + 8
                    val s = when {
                        bits == 16 && (audioFmt == 1 || audioFmt == 0xfffe) -> {
                            val frames = (bytesAvailable / 2) / channels
                            if (frames <= 0) return null
                            if (channels == 1) {
                                ShortArray(frames) { buf.getShort(dataStart + it * 2) }
                            } else {
                                ShortArray(frames) { f ->
                                    var sum = 0
                                    val base = dataStart + f * channels * 2
                                    for (c in 0 until channels) sum += buf.getShort(base + c * 2)
                                    (sum / channels).toShort()
                                }
                            }
                        }
                        bits == 8 && audioFmt == 1 -> {
                            val frames = bytesAvailable / channels
                            if (frames <= 0) return null
                            ShortArray(frames) { f ->
                                var sum = 0
                                val base = dataStart + f * channels
                                for (c in 0 until channels) sum += ((b[base + c].toInt() and 0xff) - 128) shl 8
                                (sum / channels).toShort()
                            }
                        }
                        bits == 32 && audioFmt == 3 -> {
                            val frames = (bytesAvailable / 4) / channels
                            if (frames <= 0) return null
                            ShortArray(frames) { f ->
                                var sum = 0f
                                val base = dataStart + f * channels * 4
                                for (c in 0 until channels) {
                                    val v = buf.getFloat(base + c * 4)
                                    sum += if (v.isFinite()) v.coerceIn(-1f, 1f) else 0f
                                }
                                ((sum / channels).coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                            }
                        }
                        else -> return null
                    }
                    return Speech(s, rate)
                }
                val step = 8 + size + (size and 1)
                if (step <= 0) return null
                i += step
            }
            return null
        }

        /** 16-bit mono samples; 8-bit, float, and multi-channel streams are converted. */
        internal fun pcm(bytes: ByteArray, rate: Int, format: Int, channels: Int = 1): Speech? {
            if (rate <= 0 || bytes.isEmpty()) return null
            val ch = maxOf(1, channels)
            val interleaved = when (format) {
                android.media.AudioFormat.ENCODING_PCM_8BIT -> ShortArray(bytes.size) { ((bytes[it].toInt() and 0xff) - 128 shl 8).toShort() }
                android.media.AudioFormat.ENCODING_PCM_FLOAT -> {
                    val f = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
                    ShortArray(f.remaining()) { (f.get(it).coerceIn(-1f, 1f) * 32767).toInt().toShort() }
                }
                else -> {
                    val s = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
                    ShortArray(s.remaining()).also { s.get(it) }
                }
            }
            if (interleaved.isEmpty()) return null
            val samples = if (ch == 1) {
                interleaved
            } else {
                val frames = interleaved.size / ch
                if (frames <= 0) return null
                ShortArray(frames) { f ->
                    var sum = 0
                    val base = f * ch
                    for (c in 0 until ch) sum += interleaved[base + c]
                    (sum / ch).toShort()
                }
            }
            return if (samples.isEmpty()) null else Speech(samples, rate)
        }

        private fun finishPreview(token: Long) {
            val done: (() -> Unit)?
            val trackToRelease: AudioTrack?
            val writerToStop: Thread?
            synchronized(previewLock) {
                if (previewToken != token) return
                previewToken++
                pendingTtsPreview = null
                previewEndWatch?.let { previewMain.removeCallbacks(it) }
                previewEndWatch = null
                done = previewOnDone
                previewOnDone = null
                writerToStop = previewWriter
                previewWriter = null
                trackToRelease = previewTrack
                previewTrack = null
            }
            writerToStop?.interrupt()
            trackToRelease?.runCatching {
                pause()
                flush()
                stop()
                release()
            }
            done?.let { cb ->
                if (Looper.myLooper() == Looper.getMainLooper()) cb()
                else previewMain.post(cb)
            }
        }

        /** Stops what 试听 is saying without touching the synthesis TTS engine used by playback. */
        fun stopPreview() {
            val done: (() -> Unit)?
            val trackToRelease: AudioTrack?
            val writerToStop: Thread?
            synchronized(previewLock) {
                previewToken++
                pendingTtsPreview = null
                previewEndWatch?.let { previewMain.removeCallbacks(it) }
                previewEndWatch = null
                done = previewOnDone
                previewOnDone = null
                runCatching { previewTts?.stop() }
                writerToStop = previewWriter
                previewWriter = null
                trackToRelease = previewTrack
                previewTrack = null
            }
            writerToStop?.interrupt()
            trackToRelease?.runCatching {
                pause()
                flush()
                stop()
                release()
            }
            done?.let { cb ->
                if (Looper.myLooper() == Looper.getMainLooper()) cb()
                else previewMain.post(cb)
            }
        }

        /** Plays a cloud or rendered clip once for 试听 via `MODE_STREAM` (avoids OEM `MODE_STATIC` buffer failures). */
        fun preview(s: Speech, onComplete: (() -> Unit)? = null): Boolean {
            stopPreview()
            if (s.samples.isEmpty() || s.rate <= 0) return false
            val minBuf = AudioTrack.getMinBufferSize(s.rate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
            if (minBuf <= 0) return false
            val halfSecEven = (s.rate + 1) and 1.inv() // 16-bit mono = 2 bytes/frame; frame-aligned
            val track = runCatching {
                AudioTrack.Builder()
                    .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(s.rate).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setTransferMode(AudioTrack.MODE_STREAM)
                    .setBufferSizeInBytes(maxOf(minBuf, halfSecEven))
                    .build()
            }.getOrNull() ?: return false
            if (track.state != AudioTrack.STATE_INITIALIZED) {
                runCatching { track.release() }
                return false
            }
            val durationMs = maxOf(150L, (s.samples.size.toLong() * 1000L) / s.rate)
            val token: Long
            synchronized(previewLock) {
                token = previewToken
                val endWatch = Runnable { finishPreview(token) }
                previewOnDone = onComplete
                previewTrack = track
                previewEndWatch = endWatch
                previewMain.postDelayed(endWatch, durationMs + 1_500L)
            }
            val started = runCatching {
                track.play()
                val writer = Thread({
                    try {
                        val samples = s.samples
                        var off = 0
                        while (off < samples.size && !Thread.currentThread().isInterrupted) {
                            val chunk = minOf(4096, samples.size - off)
                            val n = track.write(samples, off, chunk)
                            if (n < 0) break
                            off += n
                        }
                        if (!Thread.currentThread().isInterrupted) {
                            val deadline = System.currentTimeMillis() + durationMs + 400L
                            while (!Thread.currentThread().isInterrupted && System.currentTimeMillis() < deadline) {
                                val head = runCatching { track.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(samples.size.toLong())
                                if (head >= samples.size) break
                                Thread.sleep(25)
                            }
                        }
                    } catch (_: InterruptedException) {
                    } catch (_: Throwable) {
                    } finally {
                        finishPreview(token)
                    }
                }, "yuyue-preview-audio").apply { isDaemon = true }
                synchronized(previewLock) {
                    if (previewToken == token) {
                        previewWriter = writer
                        writer.start()
                    }
                }
                true
            }.getOrDefault(false)
            if (!started) {
                finishPreview(token)
                return false
            }
            return true
        }

        private fun speakPreview(engine: TextToSpeech, token: Long, voice: Voice?, language: String, text: String): Boolean {
            synchronized(previewLock) {
                if (previewToken != token) return false
            }
            val utteranceId = "preview-$token"
            return runCatching {
                engine.setOnUtteranceProgressListener(object : UtteranceProgressListener() {
                    override fun onStart(id: String?) {}
                    override fun onDone(id: String?) {
                        if (id == utteranceId) finishPreview(token)
                    }
                    @Deprecated("Deprecated in Java")
                    override fun onError(id: String?) {
                        if (id == utteranceId) finishPreview(token)
                    }
                    override fun onError(id: String?, errorCode: Int) {
                        if (id == utteranceId) finishPreview(token)
                    }
                    override fun onStop(id: String?, interrupted: Boolean) {
                        if (id == utteranceId) finishPreview(token)
                    }
                })
                applyVoiceOrLanguage(engine, voice, language)
                val params = Bundle().apply {
                    putString(TextToSpeech.Engine.KEY_PARAM_UTTERANCE_ID, utteranceId)
                }
                engine.speak(text, TextToSpeech.QUEUE_FLUSH, params, utteranceId) == TextToSpeech.SUCCESS
            }.getOrDefault(false)
        }

        /** Says a few words straight away, for 试听, on a dedicated TTS engine so active playback is never interrupted. */
        fun preview(voice: Voice?, text: String, language: String = "", onComplete: (() -> Unit)? = null): Boolean {
            stopPreview()
            val ctx = appContext ?: return false
            if (readyOk == false) return false
            val wantedPkg = configuredDefaultSynth(ctx)
                ?: runCatching { previewTts?.defaultEngine ?: tts?.defaultEngine }.getOrNull()?.takeIf { it.isNotBlank() }
            val token: Long
            val readyEngine: TextToSpeech?
            synchronized(previewLock) {
                token = previewToken
                previewOnDone = onComplete
                val existing = previewTts
                val engineChanged = hasEngineChanged(wantedPkg, previewEnginePkg)
                if (existing != null && previewReadyOk == true && !engineChanged) {
                    readyEngine = existing
                } else {
                    readyEngine = null
                    pendingTtsPreview = PendingPreview(token, voice, language, text)
                    if (existing == null || previewReadyOk == false || engineChanged) {
                        runCatching { existing?.shutdown() }
                        previewTts = null
                        previewReadyOk = null
                        previewEnginePkg = wantedPkg
                        var created: TextToSpeech? = null
                        created = TextToSpeech(ctx) { status ->
                            val ok = status == TextToSpeech.SUCCESS
                            var pending: PendingPreview? = null
                            var engineToUse: TextToSpeech? = null
                            synchronized(previewLock) {
                                if (previewTts != null && previewTts !== created) {
                                    runCatching { created?.shutdown() }
                                    return@TextToSpeech
                                }
                                previewReadyOk = ok
                                if (ok) previewEnginePkg = runCatching { created?.defaultEngine }.getOrNull() ?: wantedPkg
                                pending = pendingTtsPreview
                                pendingTtsPreview = null
                                engineToUse = created
                            }
                            val req = pending ?: return@TextToSpeech
                            if (ok && engineToUse != null) {
                                if (!speakPreview(engineToUse, req.token, req.voice, req.language, req.text)) {
                                    val fallback = if (readyOk == true) tts else null
                                    if (fallback == null || !speakPreview(fallback, req.token, req.voice, req.language, req.text)) {
                                        finishPreview(req.token)
                                    }
                                }
                            } else {
                                val fallback = if (readyOk == true) tts else null
                                if (fallback == null || !speakPreview(fallback, req.token, req.voice, req.language, req.text)) {
                                    finishPreview(req.token)
                                }
                            }
                        }
                        previewTts = created
                    }
                }
            }
            if (readyEngine != null) {
                val ok = speakPreview(readyEngine, token, voice, language, text)
                if (!ok) {
                    finishPreview(token)
                    return false
                }
            }
            return true
        }

        /** Opens Android's system TTS engine settings (choose Samsung / OnePlus / MultiTTS / Google TTS). */
        fun openTtsSettings(context: Context): Boolean {
            val flags = Intent.FLAG_ACTIVITY_NEW_TASK
            return listOf(
                Intent("com.android.settings.TTS_SETTINGS").addFlags(flags),
                Intent(TextToSpeech.Engine.ACTION_INSTALL_TTS_DATA).addFlags(flags),
                Intent(Settings.ACTION_SETTINGS).addFlags(flags),
            ).any { runCatching { context.startActivity(it) }.isSuccess }
        }

        /** Opens system TTS data download, or falls back to system TTS / general settings. */
        fun openSettings(context: Context): Boolean {
            val flags = Intent.FLAG_ACTIVITY_NEW_TASK
            return listOf(
                Intent(TextToSpeech.Engine.ACTION_INSTALL_TTS_DATA).addFlags(flags),
                Intent("com.android.settings.TTS_SETTINGS").addFlags(flags),
                Intent(Settings.ACTION_SETTINGS).addFlags(flags),
            ).any { runCatching { context.startActivity(it) }.isSuccess }
        }
    }

    fun preview(text: String, language: String = "", onComplete: (() -> Unit)? = null): Boolean =
        Engine.preview(voice, text, language, onComplete)
}

/** Every voice to choose from: built-in & book cards, cloned voices, cloud voices, and system voices. */
object Voices {
    internal fun cardMatchesTag(cardLang: String, tag: String): Boolean {
        if (!CardVoice.speaksLanguage(tag)) return false
        val c = cardLang.lowercase()
        val t = tag.replace('_', '-').lowercase()
        return when {
            c.startsWith("en") -> t == "en" || t.startsWith("en-")
            c.startsWith("zh") -> t == "zh" || t.startsWith("zh-") || t == "cmn"
            else -> SpokenLanguage.matches(cardLang, tag)
        }
    }

    internal fun sortBuiltinCards(cards: List<uniffi.yuyue_core.VoiceCard>, tag: String): List<uniffi.yuyue_core.VoiceCard> {
        val primaryId = if (tag.lowercase().startsWith("en")) "card.b.33" else "card.b.01"
        return cards
            .filter { cardMatchesTag(it.lang, tag) }
            .sortedWith(
                compareByDescending<uniffi.yuyue_core.VoiceCard> { it.id == primaryId }
                    .thenByDescending { it.narrator }
                    .thenBy { it.id }
            )
    }

    internal fun pickDefaultNarrator(cards: List<uniffi.yuyue_core.VoiceCard>, tag: String): uniffi.yuyue_core.VoiceCard? =
        sortBuiltinCards(cards, tag).firstOrNull()

    fun recommendedCards(core: uniffi.yuyue_core.YuyueCore, dataDir: File, tag: String): List<uniffi.yuyue_core.VoiceCard> {
        if (!CardVoice.speaksLanguage(tag)) return emptyList()
        LiveVoiceModel.register(core, dataDir)
        return sortBuiltinCards(core.cardsBuiltin(), tag)
    }

    /** Default narrator voice when Kokoro or LiveVoice bundle is ready. */
    fun defaultNarrator(tag: String): BookVoice? {
        if (!CardVoice.speaksLanguage(tag)) return null
        KokoroModel.defaultNarrator(tag)?.let { return it }
        val app = runCatching { app.yuyue.YuyueApp.instance }.getOrNull() ?: return null
        val dataDir = app.library.dataDir
        val core = app.library.core
        if (!LiveVoiceModel.canRun(dataDir)) return null
        LiveVoiceModel.register(core, dataDir)
        return pickDefaultNarrator(core.cardsBuiltin(), tag)?.let { CardVoice(it) }
    }

    suspend fun all(tag: String, bookID: String? = null): List<BookVoice> {
        val app = runCatching { app.yuyue.YuyueApp.instance }.getOrNull()
        val kokoro = if (app != null && KokoroModel.canRun(app.library.dataDir)) {
            KokoroModel.voices(app.library.core, app.library.dataDir, tag)
        } else emptyList()
        val cards = if (app != null && LiveVoiceModel.canRun(app.library.dataDir)) {
            val core = app.library.core
            val dataDir = app.library.dataDir
            LiveVoiceModel.register(core, dataDir)
            val rec = recommendedCards(core, dataDir, tag).map { CardVoice(it) }
            val designed = if (bookID != null && CardVoice.speaksLanguage(tag)) {
                runCatching { core.cardsOfBook(bookID) }.getOrDefault(emptyList())
                    .filter { cardMatchesTag(it.lang, tag) }
                    .map { CardVoice(it, bookID) }
            } else emptyList()
            val cloned = if (CardVoice.speaksLanguage(tag)) {
                core.cardsCloned().map { CardVoice(it) }
            } else emptyList()
            rec + cloned + designed
        } else emptyList()
        val sys = SystemVoice.Engine.voices()
            .filter { it.speaks(tag) }
            .sortedWith(compareByDescending<SystemVoice> { it.isSystemDefault }.thenByDescending { it.isOffline })
        val cloud = CloudVoice.all().filter { it.speaks(tag) }
        return kokoro + cards + cloud + sys
    }

    suspend fun find(id: String, bookID: String? = null): BookVoice? {
        val app = runCatching { app.yuyue.YuyueApp.instance }.getOrNull()
        return when {
            id.startsWith("kokoro.") -> {
                if (app == null || !KokoroModel.canRun(app.library.dataDir)) null
                else KokoroModel.voices(app.library.core, app.library.dataDir).firstOrNull { it.id == id }
            }
            id.startsWith("card.") -> {
                if (app == null || !LiveVoiceModel.canRun(app.library.dataDir)) null
                else {
                    LiveVoiceModel.register(app.library.core, app.library.dataDir)
                    app.library.core.cardFind(bookID, id)?.let { CardVoice(it, bookID) }
                }
            }
            id.startsWith("cloned.") -> {
                if (app != null && LiveVoiceModel.canRun(app.library.dataDir)) {
                    LiveVoiceModel.register(app.library.core, app.library.dataDir)
                    app.library.core.cardFind(null, id)?.let { return CardVoice(it) }
                }
                ClonedVoices.find(id)?.takeIf { it.isReady }?.let { return it }
                if (app != null) {
                    app.library.core.cardFind(null, id)?.let { return CardVoice(it) }
                }
                ClonedVoices.find(id)
            }
            id.startsWith("cloud.") -> {
                CloudVoice.all().firstOrNull { it.id == id } ?: if (id.startsWith("cloud.free.")) {
                    val raw = id.removePrefix("cloud.free.")
                    val isEn = raw.startsWith("en_") || raw.startsWith("af_") || raw.startsWith("am_") || raw.startsWith("bf_")
                    CloudVoice.all().firstOrNull { it.provider == CloudVoice.Provider.Free && it.speaks(if (isEn) "en" else "zh") }
                } else null
            }
            id == SystemVoice.DEFAULT_ID -> SystemVoice.Engine.defaultSystemVoice()
            else -> SystemVoice.Engine.voices().firstOrNull { it.id == id }
        }
    }

    /** System voice for `tag`: prefers the phone's configured system default TTS when it speaks `tag`. */
    suspend fun systemBest(tag: String): SystemVoice? {
        val voices = SystemVoice.Engine.voices()
        voices.firstOrNull { it.isSystemDefault && it.speaks(tag) }?.let { return it }
        for (lang in SpokenLanguage.voiceLanguages(tag)) {
            voices.filter { !it.isSystemDefault && it.speaks(lang) && it.isOffline }.firstOrNull()?.let { return it }
        }
        return voices.firstOrNull { it.speaks(tag) }
    }

    /** The best voice for text in `tag`: Kokoro / LiveVoice narrator -> keyless Free online -> system fallback. */
    suspend fun best(tag: String): BookVoice? {
        defaultNarrator(tag)?.let { return it }
        val freeList = CloudVoice.all().filter { it.provider == CloudVoice.Provider.Free && it.speaks(tag) }
        if (freeList.isNotEmpty()) {
            val preferred = if (tag.lowercase().startsWith("en")) "cloud.free.en_us" else "cloud.free.zh_baidu"
            return freeList.firstOrNull { it.id == preferred } ?: freeList.first()
        }
        return systemBest(tag)
    }

    fun title(v: BookVoice) = listOfNotNull(v.name, v.qualityName).joinToString(" · ")
}
