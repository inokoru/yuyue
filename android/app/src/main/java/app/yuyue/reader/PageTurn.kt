package app.yuyue.reader

import android.content.Context
import android.media.AudioAttributes
import android.media.SoundPool
import app.yuyue.design.Prefs
import java.io.File
import java.io.FileOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.exp
import kotlin.math.sin

/** Horizontal pagination animation style, mirrored on Apple (`reader.pageTurn`). */
enum class PageTurn(val key: String) {
    SLIDE("slide"),
    COVER("cover"),
    NONE("none");

    companion object {
        fun of(raw: String?): PageTurn = entries.firstOrNull { it.key == raw } ?: SLIDE
    }
}

object PageTurnPrefs {
    const val KEY = "reader.pageTurn"
    const val SOUND_KEY = "reader.pageTurnSound"

    val current: PageTurn
        get() = if (Prefs.isReady) PageTurn.of(Prefs.string(KEY, PageTurn.SLIDE.key)) else PageTurn.SLIDE

    val sound: Boolean
        get() = Prefs.isReady && Prefs.bool(SOUND_KEY, false)
}

/**
 * Lightweight synthesized dry-paper turn sound (~68 ms mono WAV cached once on disk, played via
 * SoundPool so it never steals audio focus and respects device volume / silent mode).
 */
object PageTurnSound {
    private var pool: SoundPool? = null
    private var soundId: Int = 0
    @Volatile private var loaded = false

    fun prepare(context: Context) {
        if (pool != null) return
        val app = context.applicationContext
        val file = File(app.cacheDir, "yy_page_turn_v1.wav")
        if (!file.exists() || file.length() < 1024L) {
            runCatching { writePaperWav(file) }
        }
        if (!file.exists()) return
        val attrs = AudioAttributes.Builder()
            .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
            .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
            .build()
        val sp = SoundPool.Builder()
            .setMaxStreams(2)
            .setAudioAttributes(attrs)
            .build()
        sp.setOnLoadCompleteListener { _, id, status ->
            if (status == 0 && id == soundId) loaded = true
        }
        pool = sp
        soundId = sp.load(file.absolutePath, 1)
    }

    fun play(context: Context) {
        if (!PageTurnPrefs.sound) return
        if (pool == null) prepare(context)
        val sp = pool ?: return
        if (loaded && soundId != 0) {
            sp.play(soundId, 0.34f, 0.34f, 1, 0, 1.0f)
        }
    }

    /** Synthesizes a subtle 68 ms dry-paper rustle (band-shaped filtered noise + gentle decay). */
    private fun writePaperWav(out: File) {
        val sampleRate = 22050
        val count = (sampleRate * 0.068).toInt()
        val pcm = ShortArray(count)
        // Deterministic LCG noise so every build sounds identical and clean.
        var seed = 0x5A17E21CL
        var lp1 = 0.0
        var lp2 = 0.0
        var prev = 0.0
        for (i in 0 until count) {
            val t = i.toDouble() / sampleRate
            seed = seed * 6364136223846793005L + 1442695040888963407L
            val white = ((seed ushr 33).toInt().toDouble() / Int.MAX_VALUE.toDouble()) * 2.0 - 1.0
            // Soft bandpass: remove harsh hiss above ~3.5kHz and rumble below ~450Hz.
            lp1 += 0.36 * (white - lp1)
            lp2 += 0.14 * (lp1 - lp2)
            val band = lp1 - lp2
            val hp = band - 0.55 * prev
            prev = band
            // Asymmetric paper-turn envelope: smooth attack (14ms), soft whisper swell, gentle tail.
            val attack = if (t < 0.014) {
                val u = t / 0.014
                u * u * (3.0 - 2.0 * u)
            } else 1.0
            val decay = exp(-(t - 0.014).coerceAtLeast(0.0) * 44.0)
            val flutter = 0.85 + 0.15 * sin(t * 210.0)
            val sample = (hp * attack * decay * flutter * 0.42).coerceIn(-1.0, 1.0)
            pcm[i] = (sample * Short.MAX_VALUE).toInt().toShort()
        }
        val dataBytes = count * 2
        val header = ByteBuffer.allocate(44).order(ByteOrder.LITTLE_ENDIAN).apply {
            put("RIFF".toByteArray())
            putInt(36 + dataBytes)
            put("WAVE".toByteArray())
            put("fmt ".toByteArray())
            putInt(16)
            putShort(1) // PCM
            putShort(1) // mono
            putInt(sampleRate)
            putInt(sampleRate * 2)
            putShort(2)
            putShort(16)
            put("data".toByteArray())
            putInt(dataBytes)
        }.array()
        val body = ByteBuffer.allocate(dataBytes).order(ByteOrder.LITTLE_ENDIAN).apply {
            for (s in pcm) putShort(s)
        }.array()
        FileOutputStream(out).use { fos ->
            fos.write(header)
            fos.write(body)
        }
    }
}
