package app.yuyue.speech

import app.yuyue.design.Prefs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

/**
 * 生成音效, as on Apple: a sound-making model (Stable Audio Open and its kind) for the sounds a
 * book writes that no library has, asked over HTTP — `POST generate` with `{"prompt", "seconds"}`,
 * answered with a WAV. It runs on a computer nearby; not offered in settings yet, it's set up in
 * debug builds by `-e sfxURL http://…`. Without it the core asks for nothing.
 */
object SfxMaker {
    const val KEY = "debug.sfxURL"
    val url: String? get() = Prefs.string(KEY).ifEmpty { null }
    val available: Boolean get() = url != null

    private val http = OkHttpClient.Builder().readTimeout(10, TimeUnit.MINUTES).build()

    /** The sound as mono 48 kHz samples; kept in `cache`, so it's made once. */
    suspend fun make(core: YuyueCore, cache: File, description: String, seconds: Float): FloatArray? = withContext(Dispatchers.IO) {
        val base = url ?: return@withContext null
        val hash = MessageDigest.getInstance("SHA-256").digest("$description\u0001$seconds".toByteArray()).take(16).joinToString("") { "%02x".format(it) }
        val file = File(cache, "sfx/$hash.wav")
        val bytes = if (file.isFile) file.readBytes() else {
            val body = JSONObject().put("prompt", description).put("seconds", seconds.toDouble()).toString().toRequestBody("application/json".toMediaType())
            val data = http.newCall(Request.Builder().url(base.trimEnd('/') + "/generate").post(body).build()).execute().use { r ->
                if (r.code != 200) error("sfx ${r.code}")
                r.body!!.bytes()
            }
            file.parentFile?.mkdirs()
            file.writeBytes(data)
            data
        }
        val (samples, rate) = wav(bytes) ?: return@withContext null
        core.dramaResample(samples.toList(), rate.toUInt()).toFloatArray()
    }

    /** A WAV's samples (16-bit or float, channels averaged) and rate. */
    fun wav(b: ByteArray): Pair<FloatArray, Int>? {
        if (b.size < 44 || String(b, 0, 4) != "RIFF" || String(b, 8, 4) != "WAVE") return null
        val buf = ByteBuffer.wrap(b).order(ByteOrder.LITTLE_ENDIAN)
        var at = 12
        var format = 1
        var channels = 1
        var rate = 48_000
        var bits = 16
        while (at + 8 <= b.size) {
            val id = String(b, at, 4)
            val size = buf.getInt(at + 4)
            val body = at + 8
            if (id == "fmt ") {
                format = buf.getShort(body).toInt()
                channels = buf.getShort(body + 2).toInt().coerceAtLeast(1)
                rate = buf.getInt(body + 4)
                bits = buf.getShort(body + 14).toInt()
            } else if (id == "data") {
                val end = minOf(b.size, body + size.coerceAtLeast(0))
                val step = bits / 8 * channels
                val n = (end - body) / step
                val out = FloatArray(n) { i ->
                    var sum = 0f
                    for (c in 0 until channels) {
                        val p = body + i * step + c * bits / 8
                        sum += if (format == 3 && bits == 32) buf.getFloat(p) else buf.getShort(p) / 32768f
                    }
                    sum / channels
                }
                return out to rate
            }
            at = body + size + (size and 1)
        }
        return null
    }
}
