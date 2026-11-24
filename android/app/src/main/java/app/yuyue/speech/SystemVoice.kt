package app.yuyue.speech

import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Bundle
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

/** Book languages are BCP 47 tags (zh-Hans, en, …); voices use locales (zh-CN, en-US). */
object SpokenLanguage {
    fun voiceLanguages(tag: String): List<String> {
        val t = tag.replace('_', '-').lowercase()
        return when {
            t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo") -> listOf("zh-HK", "zh-TW", "zh-CN")
            t.startsWith("zh-hant") || t.startsWith("zh-tw") -> listOf("zh-TW", "zh-HK", "zh-CN")
            t == "zh" || t.startsWith("zh-") || t == "cmn" -> listOf("zh-CN", "zh-TW")
            t == "en" || t.startsWith("en-") -> listOf("en-US", "en-GB", "en")
            else -> listOf(t.substringBefore('-'))
        }
    }

    fun matches(voiceLanguage: String, tag: String): Boolean =
        voiceLanguages(tag).any { voiceLanguage.lowercase().startsWith(it.lowercase()) }
}

/**
 * A voice of the phone's own TextToSpeech engine. The engine writes each sentence to a file
 * rather than speaking it, so its length is known before it plays and the ink line keeps pace;
 * the samples are collected as they arrive.
 */
class SystemVoice(private val voice: Voice, number: Int = 0) : BookVoice {
    override val id: String = "system.${voice.name}"
    // The engine's own names (`en-us-x-iol-local`) say nothing to a person, so a language's voices
    // are lettered in the order they're listed: 声音 A, 声音 B…
    override val name: String = voice.locale.getDisplayName(Locale.getDefault()) + " · " +
        app.yuyue.t("声音 %@", if (number < 26) ('A' + number).toString() else "${number + 1}")
    override val qualityName: String? = if (voice.quality >= Voice.QUALITY_HIGH) app.yuyue.t("高级") else null
    override val isOffline: Boolean = !voice.isNetworkConnectionRequired

    /** Gender hinted by the engine's voice id or features, before acoustic measurement. */
    val inferredGender: uniffi.yuyue_core.CharacterGender
        get() {
            val tokens = (listOf(voice.name) + (voice.features ?: emptySet())).joinToString(" ").lowercase()
            return when {
                Regex("(^|[^a-z])(female|woman|girl)([^a-z]|$)").containsMatchIn(tokens) -> uniffi.yuyue_core.CharacterGender.F
                Regex("(^|[^a-z])(male|man|boy)([^a-z]|$)").containsMatchIn(tokens) -> uniffi.yuyue_core.CharacterGender.M
                else -> uniffi.yuyue_core.CharacterGender.UNKNOWN
            }
        }

    override fun speaks(tag: String) = SpokenLanguage.matches(voice.locale.toLanguageTag(), tag)

    override suspend fun render(text: String, language: String): Speech? = render(text, language, 1f, 1f)

    /** 分角色: one character a little faster or slower, higher or lower. */
    suspend fun render(text: String, language: String, rate: Float, pitch: Float): Speech? {
        if (text.none { it.isLetterOrDigit() }) return null
        return Engine.render(text, voice, rate, pitch)
    }

    /** The one TextToSpeech the app keeps, and its voices. */
    object Engine {
        private var tts: TextToSpeech? = null
        private var ready = CompletableDeferred<Boolean>()
        @Volatile private var readyOk: Boolean? = null
        private var previewTrack: AudioTrack? = null
        private val lock = Mutex()
        private lateinit var cache: File

        @Synchronized
        fun init(context: Context) {
            if (tts != null) {
                if (readyOk != false) return
                runCatching { tts?.shutdown() }
                tts = null
                ready = CompletableDeferred()
                readyOk = null
            }
            cache = File(context.cacheDir, "tts").apply { mkdirs() }
            tts = TextToSpeech(context.applicationContext) { status ->
                val ok = status == TextToSpeech.SUCCESS
                readyOk = ok
                ready.complete(ok)
            }
        }

        private suspend fun awaitReady(): Boolean =
            withTimeoutOrNull(5_000L) { ready.await() } == true

        suspend fun voices(): List<SystemVoice> {
            if (!awaitReady()) return emptyList()
            val engine = tts ?: return emptyList()
            val raw = runCatching { engine.voices }.getOrNull() ?: return emptyList()
            return raw
                .filter { v ->
                    v != null &&
                        v.locale != null &&
                        !v.name.isNullOrEmpty() &&
                        v.features?.contains(TextToSpeech.Engine.KEY_FEATURE_NOT_INSTALLED) != true
                }
                .sortedWith(compareBy({ it.locale.toLanguageTag() }, { it.isNetworkConnectionRequired }, { -it.quality }))
                .let { all -> all.mapIndexed { i, v -> SystemVoice(v, all.take(i).count { it.locale == v.locale }) } }
        }

        suspend fun render(text: String, voice: Voice, speechRate: Float = 1f, speechPitch: Float = 1f): Speech? = lock.withLock {
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
                    synchronized(outLock) {
                        rate = sampleRateInHz
                        format = audioFormat
                        channels = maxOf(1, channelCount)
                    }
                }
                override fun onAudioAvailable(utteranceId: String?, audio: ByteArray) {
                    synchronized(outLock) {
                        out.write(audio)
                    }
                }
                override fun onDone(utteranceId: String?) {
                    // Engines that don't hand the audio over as it's made (or omit onBeginSynthesis) still wrote the file.
                    val fromStream = synchronized(outLock) {
                        if (out.size() > 0) pcm(out.toByteArray(), rate, format, channels) else null
                    }
                    done.complete(fromStream ?: wav(file))
                }
                @Deprecated("Deprecated in Java")
                override fun onError(utteranceId: String?) {
                    done.completeExceptionally(IllegalStateException(t("系统朗读合成失败")))
                }
                override fun onError(utteranceId: String?, errorCode: Int) {
                    done.completeExceptionally(IllegalStateException(t("系统朗读合成失败（%lld）", errorCode.toLong())))
                }
            })
            var timedOut = true
            val speech = try {
                engine.voice = voice
                engine.setSpeechRate(speechRate)
                engine.setPitch(speechPitch)
                if (engine.synthesizeToFile(text, Bundle(), file, id) == TextToSpeech.ERROR) {
                    throw IllegalStateException(t("系统朗读启动失败"))
                }
                // A sentence that never finishes must not stall the book.
                withTimeoutOrNull(20_000L + text.length * 300L) {
                    done.await().also { timedOut = false }
                }
            } finally {
                file.delete()
                runCatching { engine.setOnUtteranceProgressListener(null) }
            }
            if (timedOut) throw IllegalStateException(t("系统朗读超时了"))
            speech
        }

        /** A 16-bit PCM WAV file's samples (downmixed to mono if multi-channel). */
        fun wav(file: File): Speech? {
            val b = runCatching { file.readBytes() }.getOrNull() ?: return null
            if (b.size < 44 || String(b, 0, 4) != "RIFF") return null
            val buf = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)
            var i = 12
            var rate = 0
            var bits = 16
            var channels = 1
            while (i + 8 <= b.size) {
                val tag = String(b, i, 4)
                val size = buf.getInt(i + 4)
                if (size < 0) return null
                if (tag == "fmt " && i + 24 <= b.size) {
                    channels = maxOf(1, buf.getShort(i + 10).toInt())
                    rate = buf.getInt(i + 12)
                    bits = buf.getShort(i + 22).toInt()
                } else if (tag == "data" && bits == 16 && rate > 0) {
                    val bytesAvailable = minOf(size, b.size - i - 8)
                    val totalShorts = bytesAvailable / 2
                    val frames = totalShorts / channels
                    if (frames <= 0) return null
                    val s = if (channels == 1) {
                        ShortArray(frames) { buf.getShort(i + 8 + it * 2) }
                    } else {
                        ShortArray(frames) { f ->
                            var sum = 0
                            val base = i + 8 + f * channels * 2
                            for (c in 0 until channels) sum += buf.getShort(base + c * 2)
                            (sum / channels).toShort()
                        }
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

        /** Stops what 试听 is saying. */
        fun stopPreview() {
            runCatching { tts?.stop() }
            previewTrack?.runCatching { stop(); release() }
            previewTrack = null
        }

        /** Plays a cloud or rendered clip once for 试听. */
        fun preview(s: Speech): Boolean {
            stopPreview()
            if (s.samples.isEmpty() || s.rate <= 0) return false
            val bytes = s.samples.size * 2
            val track = runCatching {
                AudioTrack.Builder()
                    .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build())
                    .setAudioFormat(AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(s.rate).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).build())
                    .setTransferMode(AudioTrack.MODE_STATIC)
                    .setBufferSizeInBytes(maxOf(2, bytes))
                    .build()
            }.getOrNull() ?: return false
            val started = runCatching {
                track.write(s.samples, 0, s.samples.size)
                track.play()
                true
            }.getOrDefault(false)
            if (!started) {
                runCatching { track.release() }
                return false
            }
            previewTrack = track
            return true
        }

        /** Says a few words straight away, for 试听. */
        fun preview(voice: Voice, text: String): Boolean {
            val engine = tts?.takeIf { readyOk == true } ?: return false
            stopPreview()
            return runCatching {
                engine.setOnUtteranceProgressListener(null)
                engine.voice = voice
                engine.speak(text, TextToSpeech.QUEUE_FLUSH, null, "preview") == TextToSpeech.SUCCESS
            }.getOrDefault(false)
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

    fun preview(text: String): Boolean = Engine.preview(voice, text)
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
        val sys = SystemVoice.Engine.voices().filter { it.speaks(tag) }.sortedByDescending { it.isOffline }
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
                ClonedVoices.find(id)
            }
            id.startsWith("cloud.") -> {
                CloudVoice.all().firstOrNull { it.id == id } ?: if (id.startsWith("cloud.free.")) {
                    val raw = id.removePrefix("cloud.free.")
                    val isEn = raw.startsWith("en_") || raw.startsWith("af_") || raw.startsWith("am_") || raw.startsWith("bf_")
                    CloudVoice.all().firstOrNull { it.provider == CloudVoice.Provider.Free && it.speaks(if (isEn) "en" else "zh") }
                } else null
            }
            else -> SystemVoice.Engine.voices().firstOrNull { it.id == id }
        }
    }

    /** System fallback voice for `tag` (used before the live voice bundle is ready, or under critical heat). */
    suspend fun systemBest(tag: String): SystemVoice? {
        val voices = SystemVoice.Engine.voices()
        for (lang in SpokenLanguage.voiceLanguages(tag)) {
            voices.filter { it.speaks(lang) && it.isOffline }.firstOrNull()?.let { return it }
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
