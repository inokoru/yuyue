package app.yuyue.speech

import android.content.Context
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import uniffi.yuyue_core.Role
import uniffi.yuyue_core.RoleVoice
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.util.UUID
import kotlin.math.abs
import kotlin.math.log10
import kotlin.math.pow
import kotlin.math.sqrt

/**
 * 分角色朗读: each sentence read by the voice of whoever says it. Nobody has
 * one of their own until the reader gives it: by default everything is the narrator's, so a book
 * sounds as it always did. Lines the script isn't sure of stay the narrator's too.
 */
class BookRoles(private val core: YuyueCore, private val bookID: String) {
    private var roles: List<Role> = emptyList()
    private val speakers = HashMap<Int, List<String?>>()

    init { reload() }

    fun reload() {
        roles = runCatching { core.bookRoles(bookID) }.getOrDefault(emptyList())
        speakers.clear()
    }

    val any: Boolean get() = roles.isNotEmpty()

    fun role(name: String): RoleVoice? = roles.firstOrNull { it.name == name }?.voice

    suspend fun voiceForRole(name: String, language: String, narrator: suspend () -> BookVoice?): BookVoice? {
        val role = role(name) ?: return null
        val plain = abs(role.rate - 1) < 0.01 && abs(role.pitch) < 0.01
        var base: BookVoice? = role.voice?.let { Voices.find(it, bookID) }?.takeIf { it.speaks(language) && (it as? ClonedVoice)?.isReady != false }
        if (base == null && plain) return null
        if (base == null) base = narrator() ?: return null
        return if (plain) base else TunedVoice(base, role.rate.toFloat(), role.pitch.toFloat())
    }

    /** The voice for one sentence, or null for the narrator's. */
    suspend fun voice(section: Int, sentence: Int, language: String, narrator: suspend () -> BookVoice?): BookVoice? {
        if (!any) return null
        val list = speakers.getOrPut(section) { runCatching { core.sectionRoles(bookID, section.toUInt()) }.getOrDefault(emptyList()) }
        val who = list.getOrNull(sentence) ?: return null
        return voiceForRole(who, language, narrator)
    }

    /**
     * Segment-level plan for manual role reading (used when 广播剧模式 is off): splits
     * `"宝玉笑道：「你好。」"` into narrator + character speech so narration tags stay in the
     * narrator's voice and two speakers in one sentence don't overwrite each other.
     * Returns null when the whole sentence uses a single narrator voice.
     */
    suspend fun segmentPlan(section: Int, sentence: Int, language: String, narrator: suspend () -> BookVoice?): List<Pair<BookVoice, String>>? {
        if (!any) return null
        val defaultVoice = narrator() ?: return null
        val segs = runCatching { core.roleSentenceSegments(bookID, section.toUInt(), sentence.toUInt()) }.getOrNull().orEmpty()
        if (segs.isEmpty()) return null
        var hasCustom = false
        val out = ArrayList<Pair<BookVoice, String>>(segs.size)
        for (seg in segs) {
            val custom = seg.role?.let { voiceForRole(it, language) { defaultVoice } }
            if (custom != null) {
                hasCustom = true
                out += custom to seg.text
            } else {
                out += defaultVoice to seg.text
            }
        }
        return if (hasCustom) out else null
    }

    /** Renders a multi-segment manual-role sentence into one `Speech` at 48 kHz with a short breath between speaker changes. */
    suspend fun renderSegments(plan: List<Pair<BookVoice, String>>, language: String): Speech? {
        if (plan.size == 1) return plan[0].first.render(plan[0].second, language)
        val parts = ArrayList<FloatArray>(plan.size)
        for ((voice, text) in plan) {
            val sp = voice.render(text, language) ?: continue
            if (sp.samples.isEmpty()) continue
            val floats = sp.samples.map { it / 32768f }
            val at48 = core.dramaResample(floats, sp.rate.toUInt()).toFloatArray()
            if (at48.isNotEmpty()) parts += at48
        }
        if (parts.isEmpty()) return null
        val gap = 3_840 // 0.08 s at 48 kHz
        val total = parts.sumOf { it.size } + gap * (parts.size - 1).coerceAtLeast(0)
        val joined = ShortArray(total)
        var at = 0
        parts.forEachIndexed { idx, part ->
            if (idx > 0) at += gap
            for (k in part.indices) {
                joined[at + k] = (part[k].coerceIn(-1f, 1f) * 32767).toInt().toShort()
            }
            at += part.size
        }
        return Speech(joined, 48_000)
    }
}

/** A voice a little faster or slower, higher or lower: one character's own 快慢 and 高低 on top
 *  of whichever voice reads them. */
class TunedVoice(val base: BookVoice, val rate: Float, val pitch: Float) : BookVoice by base {
    override val id: String = "${base.id}#$rate#$pitch"
    override suspend fun render(text: String, language: String): Speech? {
        if (base is SystemVoice) return base.render(text, language, rate, 2f.pow(pitch / 12f))
        val s = base.render(text, language) ?: return null
        val tuned = uniffi.yuyue_core.tuneSpeechPcm(s.samples.toList(), s.rate.toUInt(), rate, pitch)
        return if (tuned.isEmpty()) s else Speech(tuned.toShortArray(), s.rate)
    }
}

/**
 * 克隆声音: a voice made from a short recording.
 * The recording stays on this device (files/clones/<id>); nothing about it syncs. What makes
 * speech of it is the reader's own ElevenLabs for now, to be swapped for a model on the device.
 */
class ClonedVoice(val meta: JSONObject) : BookVoice {
    override val id: String = meta.getString("id")
    override val name: String = meta.getString("name")
    override val qualityName: String? = t("克隆")
    override val isOffline: Boolean = false
    val remote: String? = meta.optString("remote").ifEmpty { null }
    val useElevenLabs: Boolean = if (meta.has("alsoElevenLabs")) meta.optBoolean("alsoElevenLabs", false) else remote != null
    val needsRerecord: Boolean = meta.optBoolean("needsRerecord", false)
    val isReady: Boolean get() = remote != null && useElevenLabs && CloudVoice.Provider.ElevenLabs.apiKey != null
    override fun speaks(tag: String) = true

    override suspend fun render(text: String, language: String): Speech? {
        val r = remote?.takeIf { useElevenLabs } ?: throw CloudVoice.Failure(t("这个克隆的声音还不能念：要先在设置里接上 ElevenLabs"))
        return CloudVoice(CloudVoice.Provider.ElevenLabs, r, name, null).render(text, language)
    }
}

object ClonedVoices {
    private lateinit var dir: File

    fun init(context: Context) {
        if (!::dir.isInitialized) dir = File(context.filesDir, "clones").apply { mkdirs() }
    }

    fun all(): List<ClonedVoice> {
        if (!::dir.isInitialized) return emptyList()
        return (dir.listFiles() ?: emptyArray()).mapNotNull { f ->
            runCatching { ClonedVoice(JSONObject(File(f, "meta.json").readText())) }.getOrNull()
        }.sortedByDescending { it.meta.optLong("created") }
    }

    fun find(id: String): ClonedVoice? = all().firstOrNull { it.id == id }

    val engineAvailable: Boolean get() = CloudVoice.Provider.ElevenLabs.apiKey != null

    /** Keeps a recording as a new voice and asks the engine to make it; kept even when it can't yet. */
    suspend fun save(recording: File, name: String, text: String?, seconds: Double): ClonedVoice = withContext(Dispatchers.IO) {
        val id = "cloned." + UUID.randomUUID().toString().lowercase()
        val folder = File(dir, id).apply { mkdirs() }
        val sample = File(folder, "sample." + recording.extension.ifEmpty { "audio" })
        recording.copyTo(sample, overwrite = true)
        val meta = JSONObject().put("id", id).put("name", name).put("created", System.currentTimeMillis()).put("seconds", seconds)
        if (text != null) meta.put("text", text)
        if (engineAvailable) {
            meta.put("remote", elevenLabs(name, sample)).put("engine", "ElevenLabs").put("alsoElevenLabs", true)
        }
        File(folder, "meta.json").writeText(meta.toString(2))
        ClonedVoice(meta)
    }

    fun delete(v: ClonedVoice) = deleteById(v.id)

    fun deleteById(id: String) {
        if (::dir.isInitialized) File(dir, id).deleteRecursively()
        CardVoice.Engine.invalidateCard(id)
    }

    fun alsoElevenLabs(id: String): Boolean = find(id)?.useElevenLabs ?: false

    fun recordLocalCardMeta(card: uniffi.yuyue_core.VoiceCard, neutralText: String, seconds: Double) {
        if (!::dir.isInitialized) return
        val folder = File(dir, card.id).apply { mkdirs() }
        val metaFile = File(folder, "meta.json")
        val meta = runCatching { JSONObject(metaFile.readText()) }.getOrDefault(JSONObject())
        meta.put("id", card.id)
        meta.put("name", card.name)
        if (!meta.has("created")) meta.put("created", System.currentTimeMillis())
        meta.put("seconds", seconds)
        meta.put("text", neutralText)
        meta.put("migrated", true)
        meta.put("needsRerecord", false)
        if (!meta.has("alsoElevenLabs")) meta.put("alsoElevenLabs", false)
        metaFile.writeText(meta.toString(2))
    }

    fun renameMeta(id: String, newName: String) {
        if (!::dir.isInitialized) return
        val metaFile = File(dir, "$id/meta.json")
        if (!metaFile.isFile) return
        val meta = runCatching { JSONObject(metaFile.readText()) }.getOrNull() ?: return
        meta.put("name", newName)
        metaFile.writeText(meta.toString(2))
    }

    /** Toggles "也用 ElevenLabs 念" on a cloned card. */
    suspend fun setAlsoElevenLabs(card: uniffi.yuyue_core.VoiceCard, clipFile: File, enabled: Boolean) = withContext(Dispatchers.IO) {
        if (!::dir.isInitialized) return@withContext
        val folder = File(dir, card.id).apply { mkdirs() }
        val metaFile = File(folder, "meta.json")
        val meta = runCatching { JSONObject(metaFile.readText()) }.getOrDefault(JSONObject().put("id", card.id).put("name", card.name).put("created", System.currentTimeMillis()))
        if (enabled && meta.optString("remote").isEmpty()) {
            val remoteId = elevenLabs(card.name, clipFile)
            meta.put("remote", remoteId).put("engine", "ElevenLabs")
        }
        meta.put("alsoElevenLabs", enabled)
        metaFile.writeText(meta.toString(2))
    }

    /** Decodes `.ogg`, `.wav`, or platform `.m4a`/`.mp3`/`.aac` into mono float samples `[-1, 1]` and sample rate. */
    suspend fun decodeAudioFile(core: YuyueCore, file: File): Pair<FloatArray, Int>? = withContext(Dispatchers.IO) {
        if (!file.isFile) return@withContext null
        val direct = runCatching { core.cardDecodeClip(file.path, CardVoice.SAMPLE_RATE.toUInt()) }.getOrNull()
        if (direct != null && direct.isNotEmpty()) {
            return@withContext direct.toFloatArray() to CardVoice.SAMPLE_RATE
        }
        runCatching {
            Aligner.Wave(file).use { wave ->
                wave.fillTo(Aligner.RATE * 30)
                if (wave.end <= 0) null else wave.copy(0, wave.end) to Aligner.RATE
            }
        }.getOrNull()
    }

    /** ElevenLabs' 即时克隆: the recording goes to the reader's own account. */
    private fun elevenLabs(name: String, sample: File): String {
        val key = CloudVoice.Provider.ElevenLabs.apiKey ?: throw CloudVoice.Failure(t("还没填 %@ 的 Key", "ElevenLabs"))
        val boundary = "yuyue-" + UUID.randomUUID()
        val c = URL("https://api.elevenlabs.io/v1/voices/add").openConnection() as HttpURLConnection
        c.requestMethod = "POST"
        c.doOutput = true
        c.connectTimeout = 30_000
        c.readTimeout = 60_000
        c.setRequestProperty("xi-api-key", key)
        c.setRequestProperty("Content-Type", "multipart/form-data; boundary=$boundary")
        c.outputStream.use { out ->
            fun field(k: String, v: String) = out.write("--$boundary\r\nContent-Disposition: form-data; name=\"$k\"\r\n\r\n$v\r\n".toByteArray())
            field("name", "语阅 · $name")
            field("remove_background_noise", "true")
            val type = when (sample.extension) { "wav" -> "audio/wav"; "m4a", "mp4" -> "audio/mp4"; else -> "audio/mpeg" }
            out.write("--$boundary\r\nContent-Disposition: form-data; name=\"files\"; filename=\"${sample.name}\"\r\nContent-Type: $type\r\n\r\n".toByteArray())
            out.write(sample.readBytes())
            out.write("\r\n--$boundary--\r\n".toByteArray())
        }
        val code = c.responseCode
        val body = (if (code in 200..299) c.inputStream else c.errorStream)?.readBytes()
        if (code !in 200..299) throw CloudVoice.failure(CloudVoice.Provider.ElevenLabs, code, body)
        return JSONObject(String(body ?: ByteArray(0))).optString("voice_id").ifEmpty { throw CloudVoice.Failure(t("ElevenLabs 没有给回声音")) }
    }
}

/** 检查录音: what can be told from the samples alone. It never refuses a recording. */
data class SampleCheck(val seconds: Double, val items: List<Item>) {
    data class Item(val ok: Boolean, val title: String, val detail: String? = null)

    companion object {
        /** `samples` 16-bit mono at `rate`. */
        fun of(samples: ShortArray, rate: Int): SampleCheck {
            val seconds = samples.size.toDouble() / rate
            val hop = maxOf(1, rate / 20)
            val frames = ArrayList<Double>()
            var peak = 0.0
            var i = 0
            while (i + hop <= samples.size) {
                var sum = 0.0
                for (k in i until i + hop) {
                    val s = samples[k] / 32768.0
                    sum += s * s
                    peak = maxOf(peak, abs(s))
                }
                frames.add(sqrt(sum / hop))
                i += hop
            }
            frames.sort()
            fun at(q: Double) = if (frames.isEmpty()) 0.0 else frames[minOf(frames.size - 1, (frames.size * q).toInt())]
            val room = at(0.1)
            val voice = at(0.9)
            val items = ArrayList<Item>()
            items += if (seconds < 8) Item(false, t("只有 %lld 秒", seconds.toInt()), t("多念几句，8 秒以上更像"))
            else Item(true, t("长度 %lld 秒", Math.round(seconds)))
            items += when {
                voice < 0.02 -> Item(false, t("声音有点小"), t("离手机近一点，或者大声一点"))
                peak > 0.99 -> Item(false, t("声音太大，有的地方破了"), t("离远一点再录"))
                else -> Item(true, t("音量合适"))
            }
            val snr = if (voice > 0 && room > 0) 20 * log10(voice / room) else 60.0
            // Without a voice to compare with, nothing can be said about the room.
            if (voice >= 0.02) items += if (snr < 20) Item(false, t("背景有点吵"), t("能用；换个安静的地方再录会更像")) else Item(true, t("背景很安静"))
            return SampleCheck(seconds, items)
        }
    }
}
