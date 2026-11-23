package app.yuyue.speech

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.VoiceInfo
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.nio.FloatBuffer
import kotlin.coroutines.coroutineContext

/**
 * 声纹, as on Apple: a speaker-embedding model (CAM++ and its kind, as ONNX) that turns a stretch of
 * voice into a print. 选角 keeps the voices in one scene apart by them; 回听 notices a clip that
 * doesn't sound like its voice. The core makes the model's input and keeps each voice's print.
 *
 * The model (3D-Speaker's CAM++, Chinese common, Apache-2.0; about 28 MB) is fetched from 语阅's own
 * release the first time a play is made, checked against its BLAKE3, and kept beside the library
 * (debug builds can copy one in with `-e speakerPath <file>`). Until it's here, everything answers
 * null and both work as before.
 */
object SpeakerPrint {
    const val FILE = "speaker_print.onnx"
    /** The model's name, so prints from another model aren't compared with its own. */
    const val MODEL = "campplus-1"
    private const val URL = "https://github.com/inokoru/yuyue/releases/download/models-1/speaker_print.onnx"
    /** BLAKE3 of the file (`yuyue digest`). */
    internal const val DIGEST = "d44308bac657ccf2031b437d453a5b869ffd0da5521b299aabe96e665b840c7e"

    fun file(dataDir: File) = File(dataDir, "models/$FILE")
    fun installed(dataDir: File) = file(dataDir).isFile

    /** Debug builds: a model file copied in by hand. */
    fun install(dataDir: File, from: File) {
        file(dataDir).parentFile?.mkdirs()
        from.copyTo(file(dataDir), overwrite = true)
    }

    /** Fetched and checked unless it's here; quietly not, offline. */
    suspend fun ensure(dataDir: File) = withContext(Dispatchers.IO) {
        if (installed(dataDir)) return@withContext
        val target = file(dataDir).apply { parentFile?.mkdirs() }
        val temp = File(target.parentFile, ".$FILE.part")
        val urls = ModelDownload.candidateUrls(asset = FILE, officialUrl = URL)
        try {
            ModelDownload.downloadWithResume(
                urls = urls,
                partFile = temp,
                expectedBlake3 = DIGEST,
            )
            if (!temp.renameTo(target)) {
                temp.copyTo(target, overwrite = true)
                temp.delete()
            }
        } catch (_: Exception) {
        }
    }

    private var held: Pair<String, OrtSession>? = null

    @Synchronized
    private fun session(dataDir: File): OrtSession? {
        val f = file(dataDir)
        if (!f.isFile) return null
        held?.let { if (it.first == f.path) return it.second }
        val s = runCatching { OrtEnvironment.getEnvironment().createSession(f.path, OrtSession.SessionOptions()) }.getOrNull() ?: return null
        held = f.path to s
        return s
    }

    /** The print of `samples` at `rate` (mono, −1…1); null without the model, or for under half a second. */
    suspend fun print(core: YuyueCore, dataDir: File, samples: FloatArray, rate: Int): FloatArray? = withContext(Dispatchers.Default) {
        val s = session(dataDir) ?: return@withContext null
        val f = core.speakerFeatures(samples.toList(), rate.toUInt())
        if (f.frames < 50u) return@withContext null
        runCatching {
            val env = OrtEnvironment.getEnvironment()
            OnnxTensor.createTensor(env, FloatBuffer.wrap(f.data.toFloatArray()), longArrayOf(1, f.frames.toLong(), 80)).use { input ->
                s.run(mapOf((s.inputNames.firstOrNull() ?: "fbank") to input)).use { out ->
                    @Suppress("UNCHECKED_CAST")
                    when (val v = out[0].value) {
                        is Array<*> -> (v[0] as FloatArray)
                        is FloatArray -> v
                        else -> null
                    }
                }
            }
        }.getOrNull()
    }

    /** Each offline voice in `voices` that lacks a reference print or a known gender reads the
     *  sample sentence once; its pitch gives its gender (kept even without the ONNX model) and,
     *  when the model is installed, its print is kept for 选角 and 回听. */
    suspend fun printVoices(core: YuyueCore, dataDir: File, voices: List<VoiceInfo>, language: String): List<VoiceInfo> {
        val haveModel = installed(dataDir)
        if (haveModel) core.speakerModel(MODEL)
        val localIds = voices.filter { !it.id.startsWith("cloud.") }.map { it.id }
        val unprinted = if (haveModel) core.speakerUnprinted(localIds).toSet() else emptySet()
        val text = core.speakerSampleText(language)
        val updated = HashMap<String, uniffi.yuyue_core.CharacterGender>()
        for (v in voices) {
            if (v.id.startsWith("cloud.")) continue
            if (v.id in unprinted && v.embedding.isNotEmpty()) {
                core.speakerSetPrint(v.id, v.embedding)
            }
            val needGender = v.gender == uniffi.yuyue_core.CharacterGender.UNKNOWN
            val needPrint = v.id in unprinted && v.embedding.isEmpty()
            if (!needGender && !needPrint) continue
            coroutineContext.ensureActive()
            val voice = Voices.find(v.id) ?: continue
            val speech = runCatching { voice.render(text, language) }.getOrNull() ?: continue
            val samples = FloatArray(speech.samples.size) { speech.samples[it] / 32768f }
            if (needGender) {
                val g = core.speakerVoiceGender(samples.toList(), speech.rate.toUInt())
                if (g != uniffi.yuyue_core.CharacterGender.UNKNOWN) {
                    core.speakerSetGender(v.id, g)
                    updated[v.id] = g
                }
            }
            if (needPrint) {
                print(core, dataDir, samples, speech.rate)?.let { core.speakerSetPrint(v.id, it.toList()) }
            }
        }
        return if (updated.isEmpty()) voices else voices.map { v -> updated[v.id]?.let { v.copy(gender = it) } ?: v }
    }
}
