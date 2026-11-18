package app.yuyue.speech

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import uniffi.yuyue_core.HeardWord
import uniffi.yuyue_core.SenseVoice
import java.io.File
import java.nio.FloatBuffer
import java.nio.IntBuffer
import kotlin.coroutines.coroutineContext

/**
 * 听写 on Android, which has no system transcriber that takes a file: SenseVoice-Small
 * (FunAudioLLM; Chinese, English, Japanese, Korean, Cantonese; about 240 MB, int8), run here with
 * ONNX Runtime. The core makes its input and reads its output (`SenseVoice`).
 *
 * 回听 asks it what a 广播剧 clip said; 转成文字 asks it for a whole recording, punctuated. Fetched
 * from 语阅's own release the first time either needs it, checked against its BLAKE3, kept beside
 * the library. Debug builds can copy one in with `-e asrDir <folder with both files>`.
 */
object Listener {
    private const val MODEL = "sense_voice.int8.onnx"
    private const val TOKENS = "sense_voice_tokens.txt"
    private const val BASE = "https://github.com/inokoru/yuyue/releases/download/models-1/"
    /** BLAKE3 of each file (`yuyue digest`). */
    private val DIGESTS = mapOf(
        MODEL to "3171a61e94132c6270584b5a0f2f100289d5958c28795695a4d43ef23660ab8e",
        TOKENS to "dc65b6ac2b8bab079278769054812b6972114f2f515983491dfd7750e20987e1",
    )
    const val BYTES = 239_233_841L
    /** Up to this much is heard at once; longer is cut at its quietest moment near the end. */
    private const val CHUNK = 28.0

    private fun path(dataDir: File, name: String) = File(dataDir, "models/$name")
    fun installed(dataDir: File) = path(dataDir, MODEL).isFile && path(dataDir, TOKENS).isFile

    /** Debug builds: the two files copied in from a folder. */
    fun install(dataDir: File, from: File) {
        for (n in listOf(MODEL, TOKENS)) File(from, n).takeIf { it.isFile }?.copyTo(path(dataDir, n).apply { parentFile?.mkdirs() }, overwrite = true)
    }

    /** Fetched and checked unless it's here; `progress` hears 0–1. Throws when it can't. */
    suspend fun ensure(dataDir: File, progress: (Double) -> Unit) = withContext(Dispatchers.IO) {
        if (installed(dataDir)) return@withContext
        val dir = File(dataDir, "models").apply { mkdirs() }
        if (dir.usableSpace < BYTES * 2) throw java.io.IOException("ENOSPC")
        for (name in listOf(TOKENS, MODEL)) {
            val target = path(dataDir, name)
            if (target.isFile) continue
            val temp = File(dir, "$name.part")
            try {
                OkHttpClient().newCall(Request.Builder().url(BASE + name).build()).execute().use { r ->
                    if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                    val body = r.body ?: throw java.io.IOException("empty")
                    val total = body.contentLength().takeIf { it > 0 } ?: BYTES
                    body.byteStream().use { input ->
                        temp.outputStream().use { out ->
                            val buf = ByteArray(1 shl 16)
                            var done = 0L
                            var last = 0L
                            while (true) {
                                coroutineContext.ensureActive()
                                val n = input.read(buf)
                                if (n < 0) break
                                out.write(buf, 0, n)
                                done += n
                                if (name == MODEL && done - last > (1 shl 20)) {
                                    last = done
                                    progress(done.toDouble() / total)
                                }
                            }
                        }
                    }
                }
                if (uniffi.yuyue_core.fileDigest(temp.path) != DIGESTS[name]) throw java.io.IOException("digest")
                if (!temp.renameTo(target)) throw java.io.IOException("rename")
            } finally {
                temp.delete()
            }
        }
    }

    private class Loaded(val path: String, val session: OrtSession, val model: SenseVoice)
    private var held: Loaded? = null

    @Synchronized
    private fun load(dataDir: File): Loaded? {
        val f = path(dataDir, MODEL)
        if (!installed(dataDir)) return null
        held?.let { if (it.path == f.path) return it }
        return runCatching {
            val options = OrtSession.SessionOptions().apply { setIntraOpNumThreads(4) }
            val s = OrtEnvironment.getEnvironment().createSession(f.path, options)
            val model = SenseVoice(s.metadata.customMetadata, path(dataDir, TOKENS).readText())
            Loaded(f.path, s, model)
        }.getOrNull()?.also { held = it }
    }

    /** What `samples` (mono, −1…1, at `rate`) say, each word with its time from `offset`; null
     *  without the model. */
    suspend fun words(dataDir: File, samples: FloatArray, rate: Int, language: String, punctuated: Boolean, offset: Double = 0.0): List<HeardWord>? = withContext(Dispatchers.Default) {
        val m = load(dataDir) ?: return@withContext null
        val input = m.model.input(samples.toList(), rate.toUInt())
        if (input.frames == 0u) return@withContext emptyList()
        val env = OrtEnvironment.getEnvironment()
        val t = input.frames.toLong()
        val d = input.dim.toLong()
        runCatching {
            OnnxTensor.createTensor(env, FloatBuffer.wrap(input.data.toFloatArray()), longArrayOf(1, t, d)).use { x ->
                OnnxTensor.createTensor(env, IntBuffer.wrap(intArrayOf(t.toInt())), longArrayOf(1)).use { len ->
                    OnnxTensor.createTensor(env, IntBuffer.wrap(intArrayOf(m.model.languageId(language))), longArrayOf(1)).use { lang ->
                        OnnxTensor.createTensor(env, IntBuffer.wrap(intArrayOf(m.model.textNormId(punctuated))), longArrayOf(1)).use { norm ->
                            m.session.run(mapOf("x" to x, "x_length" to len, "language" to lang, "text_norm" to norm)).use { out ->
                                val logits = out[0] as OnnxTensor
                                val shape = logits.info.shape
                                val frames = shape[1].toInt()
                                val vocab = shape[2].toInt()
                                val fb = logits.floatBuffer
                                // The likeliest token each frame; the core reads them.
                                val best = IntArray(frames) { f ->
                                    var bi = 0
                                    var bv = Float.NEGATIVE_INFINITY
                                    val base = f * vocab
                                    for (k in 0 until vocab) {
                                        val v = fb.get(base + k)
                                        if (v > bv) { bv = v; bi = k }
                                    }
                                    bi
                                }
                                m.model.words(best.toList(), offset)
                            }
                        }
                    }
                }
            }
        }.getOrNull()
    }

    /** The words of `samples` run together: Chinese as written, other languages with spaces. */
    suspend fun text(dataDir: File, samples: FloatArray, rate: Int, language: String): String? {
        val w = words(dataDir, samples, rate, language, punctuated = false) ?: return null
        return w.joinToString(if (language.startsWith("zh")) "" else " ") { it.text }
    }

    /** A whole file heard, punctuated, a stretch at a time (cut where it's quietest), times from
     *  `offset`; `progress` hears seconds done in the file. null without the model or when it
     *  fails. */
    suspend fun file(dataDir: File, audio: File, language: String, offset: Double, progress: (Double) -> Unit): List<HeardWord>? = withContext(Dispatchers.Default) {
        if (load(dataDir) == null) return@withContext null
        val rate = Aligner.RATE
        val out = mutableListOf<HeardWord>()
        runCatching {
            Aligner.Wave(audio).use { wave ->
                var pos = 0
                while (true) {
                    coroutineContext.ensureActive()
                    val want = pos + (CHUNK * rate).toInt()
                    wave.fillTo(want)
                    val end = wave.end
                    if (end <= pos) break
                    val cut = if (end < want) end else quietest(wave.copy(pos, end), pos, (end - pos) * 2 / 3)
                    val got = words(dataDir, wave.copy(pos, cut), rate, language, punctuated = true, offset = offset + pos.toDouble() / rate) ?: return@runCatching null
                    out += got
                    wave.dropBefore(cut)
                    pos = cut
                    progress(pos.toDouble() / rate)
                    if (end < want) break
                }
                out
            }
        }.getOrNull()
    }

    /** Where in `x` (which starts at sample `base`) after `from` it's quietest, by 0.1 s. */
    private fun quietest(x: FloatArray, base: Int, from: Int): Int {
        val step = Aligner.RATE / 10
        var best = x.size
        var bestE = Double.MAX_VALUE
        var i = from
        while (i + step <= x.size) {
            var e = 0.0
            for (k in i until i + step) e += x[k] * x[k]
            if (e < bestE) { bestE = e; best = i + step / 2 }
            i += step
        }
        return base + best
    }
}
