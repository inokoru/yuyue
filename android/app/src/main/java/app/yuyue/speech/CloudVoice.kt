package app.yuyue.speech

import app.yuyue.YuyueApp
import app.yuyue.design.Prefs
import app.yuyue.library.Keys
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

/**
 * 用户自带 Key 的云端语音, as on Apple: OpenAI, Microsoft (Azure Speech) and ElevenLabs, each
 * with the reader's own key. Every sentence is kept once rendered, by voice and text, so a
 * chapter heard (or fetched ahead) plays again offline and costs nothing the second time.
 */
class CloudVoice(
    val provider: Provider,
    val voiceID: String,
    override val name: String,
    val language: String?,
    /** "male" / "female" and "young" / "middle_aged" / "old", where the provider says (广播剧's casting goes by them). */
    gender: String? = null,
    val age: String? = null,
) : BookVoice {
    val gender: String? = gender ?: if (provider == Provider.OpenAI) openAIGender[voiceID] else null

    /**
     * 带语气: how a line of a play is said, in each provider's own terms — OpenAI by an
     * instruction, ElevenLabs by tags around the text (its v3 model), Azure by a speaking style
     * where the voice has one.
     */
    data class Acting(val instruction: String, val head: String, val tail: String, val style: String?, val degree: Double) {
        val isFlat get() = instruction.isEmpty() && head.isEmpty() && tail.isEmpty() && style == null
        val key get() = "$instruction|$head|$tail|${style ?: ""}|$degree"
    }

    /** A line of a play: in pieces (the words the book gave a reading for apart, which Azure is
     *  told how to say), and acted. */
    suspend fun render(parts: List<uniffi.yuyue_core.SpokenPart>, language: String, acting: Acting?): Speech? = withContext(Dispatchers.IO) {
        val text = parts.joinToString("") { it.text }
        if (text.none { it.isLetterOrDigit() }) return@withContext null
        val readings = if (provider == Provider.Azure) parts.mapNotNull { p -> p.reading?.let { "${p.text}=$it" } }.joinToString(",") else ""
        val acted = acting?.takeIf { !it.isFlat }
        // A line as plain speech is the same sentence the reader hears: one cache for both.
        val file = if (acted == null && readings.isEmpty()) cacheFile(text) else cacheFile(text + "\u0002" + (acted?.key ?: "") + "\u0002" + readings)
        if (file.exists()) return@withContext SystemVoice.Engine.wav(file)
        val data = fetch(text, language, parts, acted)
        file.parentFile?.mkdirs()
        file.writeBytes(data)
        SystemVoice.Engine.wav(file) ?: throw Failure(t("声音数据读不出来"))
    }

    enum class Provider(val key: String) {
        OpenAI("openai"), Azure("azure"), ElevenLabs("elevenlabs");

        val title: String get() = when (this) { OpenAI -> "OpenAI"; Azure -> t("微软 Azure"); ElevenLabs -> "ElevenLabs" }
        val keyName: String get() = "tts.$key.key"
        val voicesName: String get() = "tts.$key.voices"
        val apiKey: String? get() = Keys.read(keyName)?.ifEmpty { null }
        val isSetUp: Boolean get() = apiKey != null && (this != Azure || Prefs.string(REGION).isNotEmpty())

        val keyHint: String get() = when (this) { OpenAI -> "sk-…"; Azure -> t("资源的密钥 1"); ElevenLabs -> "sk_…" }
        val help: String
            get() = when (this) {
                OpenAI -> t("platform.openai.com › API keys 里建一个。费用按字数算，走你的账号。")
                Azure -> t("Azure 门户里建一个“语音”资源，拷它的密钥和区域（比如 eastasia）。有免费额度。")
                ElevenLabs -> t("elevenlabs.io › 个人资料 › API Keys。额度按字数算。")
            }

        companion object {
            const val REGION = "tts.azure.region"
        }
    }

    override val id = "cloud.${provider.key}.$voiceID"
    override val qualityName: String get() = provider.title
    override val isOffline = false
    override fun speaks(tag: String) = language?.let { SpokenLanguage.matches(it, tag) } ?: true

    override suspend fun render(text: String, language: String): Speech? = withContext(Dispatchers.IO) {
        if (text.none { it.isLetterOrDigit() }) return@withContext null
        val file = cacheFile(text)
        if (file.exists()) return@withContext SystemVoice.Engine.wav(file)
        val data = fetch(text, language)
        file.parentFile?.mkdirs()
        file.writeBytes(data)
        SystemVoice.Engine.wav(file) ?: throw Failure(t("声音数据读不出来"))
    }

    /** Renders the sentences not kept yet, two at a time, so the chapter plays on offline. */
    suspend fun prefetch(texts: List<String>, language: String) = coroutineScope {
        val missing = texts.filter { t -> t.any { it.isLetterOrDigit() } && !cacheFile(t).exists() }
        for (pair in missing.chunked(2)) {
            if (!isActive) return@coroutineScope
            val ok = pair.map { async { runCatching { render(it, language) }.isSuccess } }.all { it.await() }
            if (!ok) return@coroutineScope
        }
    }

    private fun cacheFile(text: String): File {
        val hash = MessageDigest.getInstance("SHA-256").digest((id + "\u0001" + text).toByteArray()).take(16).joinToString("") { "%02x".format(it) }
        return File(cacheDir, "${provider.key}/$hash.wav")
    }

    class Failure(message: String) : Exception(message)

    private fun fetch(text: String, language: String, parts: List<uniffi.yuyue_core.SpokenPart>? = null, acting: Acting? = null): ByteArray {
        val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
        val c: HttpURLConnection
        val body: ByteArray
        when (provider) {
            Provider.OpenAI -> {
                c = URL("https://api.openai.com/v1/audio/speech").openConnection() as HttpURLConnection
                c.setRequestProperty("Authorization", "Bearer $key")
                c.setRequestProperty("Content-Type", "application/json")
                val o = JSONObject().put("model", "gpt-4o-mini-tts").put("voice", voiceID).put("input", text).put("response_format", "wav")
                acting?.instruction?.takeIf { it.isNotEmpty() }?.let { o.put("instructions", it) }
                body = o.toString().toByteArray()
            }
            Provider.Azure -> {
                val region = Prefs.string(Provider.REGION)
                c = URL("https://$region.tts.speech.microsoft.com/cognitiveservices/v1").openConnection() as HttpURLConnection
                c.setRequestProperty("Ocp-Apim-Subscription-Key", key)
                c.setRequestProperty("Content-Type", "application/ssml+xml")
                c.setRequestProperty("X-Microsoft-OutputFormat", "riff-24khz-16bit-mono-pcm")
                c.setRequestProperty("User-Agent", "Yuyue")
                val lang = this.language ?: SpokenLanguage.voiceLanguages(language).first()
                fun escape(t: String) = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
                // Words the book gave a reading for, said that way (pinyin with tone numbers).
                var inner = (parts ?: listOf(uniffi.yuyue_core.SpokenPart(text, null))).joinToString("") { p ->
                    val r = p.reading
                    if (r == null || !lang.startsWith("zh")) escape(p.text) else "<phoneme alphabet='sapi' ph='${sapi(r)}'>${escape(p.text)}</phoneme>"
                }
                acting?.style?.let { inner = "<mstts:express-as style='$it' styledegree='${"%.1f".format(java.util.Locale.ROOT, acting.degree)}'>$inner</mstts:express-as>" }
                body = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='$lang'><voice name='$voiceID'>$inner</voice></speak>".toByteArray()
            }
            Provider.ElevenLabs -> {
                c = URL("https://api.elevenlabs.io/v1/text-to-speech/$voiceID?output_format=pcm_24000").openConnection() as HttpURLConnection
                c.setRequestProperty("xi-api-key", key)
                c.setRequestProperty("Content-Type", "application/json")
                // Tags are read by the v3 model only; plain speech stays on the one it always used.
                body = if (acting != null && (acting.head.isNotEmpty() || acting.tail.isNotEmpty()))
                    JSONObject().put("text", acting.head + text + acting.tail).put("model_id", "eleven_v3").toString().toByteArray()
                else JSONObject().put("text", text).put("model_id", "eleven_multilingual_v2").toString().toByteArray()
            }
        }
        c.requestMethod = "POST"
        c.doOutput = true
        c.connectTimeout = 15_000
        c.readTimeout = 30_000
        c.outputStream.use { it.write(body) }
        val code = c.responseCode
        if (code !in 200..299) throw failure(provider, code, c.errorStream?.readBytes())
        val data = c.inputStream.use { it.readBytes() }
        // ElevenLabs sends bare 16-bit samples; keep every voice's audio as WAV.
        return if (provider == Provider.ElevenLabs) wav(data, 24_000) else data
    }

    companion object {
        private val cacheDir: File get() = File(YuyueApp.instance.cacheDir, "yuyue-voices")

        val openAIVoices = listOf("alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer")
        /** How OpenAI's voices sound (it doesn't say). */
        val openAIGender = mapOf(
            "alloy" to "female", "ash" to "male", "ballad" to "male", "coral" to "female", "echo" to "male",
            "fable" to "male", "nova" to "female", "onyx" to "male", "sage" to "female", "shimmer" to "female",
        )

        /** Pinyin with tone marks (jiǎ zhēn) as Azure's Chinese phonemes want it (jia 3 zhen 1). */
        fun sapi(pinyin: String): String {
            val marks = mapOf(
                'ā' to ('a' to 1), 'á' to ('a' to 2), 'ǎ' to ('a' to 3), 'à' to ('a' to 4), 'ē' to ('e' to 1), 'é' to ('e' to 2), 'ě' to ('e' to 3), 'è' to ('e' to 4),
                'ī' to ('i' to 1), 'í' to ('i' to 2), 'ǐ' to ('i' to 3), 'ì' to ('i' to 4), 'ō' to ('o' to 1), 'ó' to ('o' to 2), 'ǒ' to ('o' to 3), 'ò' to ('o' to 4),
                'ū' to ('u' to 1), 'ú' to ('u' to 2), 'ǔ' to ('u' to 3), 'ù' to ('u' to 4), 'ǖ' to ('v' to 1), 'ǘ' to ('v' to 2), 'ǚ' to ('v' to 3), 'ǜ' to ('v' to 4), 'ü' to ('v' to 0),
            )
            return pinyin.lowercase().split(' ', '\'', '-').filter { it.isNotEmpty() }.joinToString(" ") { syl ->
                var tone = 5
                val plain = syl.map { c -> marks[c]?.let { (base, t) -> if (t > 0) tone = t; base } ?: c }.joinToString("")
                // Already numbered (jia3): keep its number.
                val last = plain.last()
                if (last.isDigit()) "${plain.dropLast(1)} $last" else "$plain $tone"
            }
        }

        fun failure(p: Provider, code: Int, data: ByteArray?): Failure {
            val json = data?.let { runCatching { JSONObject(String(it)) }.getOrNull() }
            val message = json?.optJSONObject("error")?.optString("message")?.ifEmpty { null } ?: json?.optJSONObject("detail")?.optString("message")?.ifEmpty { null } ?: json?.optString("message")?.ifEmpty { null }
            return Failure(when (code) {
                401, 403 -> t("%@ 的 Key 不对", p.title)
                402 -> t("%@ 的额度用完了", p.title)
                429 -> if (message?.contains("quota") == true) t("%@ 的额度用完了", p.title) else t("%@ 说太忙，等一会儿再试", p.title)
                else -> "${p.title} $code" + (message?.let { ": $it" } ?: "")
            })
        }

        /** 16-bit mono samples in a WAV header. */
        fun wav(pcm: ByteArray, rate: Int): ByteArray {
            val b = java.nio.ByteBuffer.allocate(44 + pcm.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            b.put("RIFF".toByteArray()).putInt(36 + pcm.size).put("WAVEfmt ".toByteArray()).putInt(16).putShort(1).putShort(1)
                .putInt(rate).putInt(rate * 2).putShort(2).putShort(16).put("data".toByteArray()).putInt(pcm.size).put(pcm)
            return b.array()
        }

        /** The voices of the providers that have a key, from the lists fetched when it was saved. */
        fun all(): List<CloudVoice> = Provider.entries.filter { it.isSetUp }.flatMap { p ->
            when (p) {
                Provider.OpenAI -> openAIVoices.map { CloudVoice(p, it, it.replaceFirstChar(Char::uppercase), null) }
                else -> runCatching { JSONArray(Prefs.string(p.voicesName, "[]")) }.getOrDefault(JSONArray()).let { a ->
                    (0 until a.length()).map { a.getJSONArray(it) }.map {
                        CloudVoice(p, it.getString(0), it.getString(1), it.optString(2).ifEmpty { null }, it.optString(3).ifEmpty { null }, it.optString(4).ifEmpty { null })
                    }
                }
            }
        }

        /** Checks the key by fetching the provider's voices, and keeps the list. */
        suspend fun refresh(p: Provider): Int = withContext(Dispatchers.IO) {
            val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
            fun get(url: String, vararg headers: Pair<String, String>): String {
                val c = URL(url).openConnection() as HttpURLConnection
                headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
                c.connectTimeout = 15_000
                val code = c.responseCode
                if (code !in 200..299) throw failure(p, code, c.errorStream?.readBytes())
                return c.inputStream.use { String(it.readBytes()) }
            }
            when (p) {
                Provider.OpenAI -> { get("https://api.openai.com/v1/models/gpt-4o-mini-tts", "Authorization" to "Bearer $key"); openAIVoices.size }
                Provider.Azure -> {
                    val list = JSONArray(get("https://${Prefs.string(Provider.REGION)}.tts.speech.microsoft.com/cognitiveservices/voices/list", "Ocp-Apim-Subscription-Key" to key))
                    // Only the voices for Chinese, English and Japanese: the rest are hundreds.
                    val keep = JSONArray()
                    for (i in 0 until list.length()) {
                        val v = list.getJSONObject(i)
                        val locale = v.optString("Locale")
                        if (locale.startsWith("zh") || locale.startsWith("en") || locale.startsWith("ja")) keep.put(JSONArray().put(v.optString("ShortName")).put(v.optString("LocalName")).put(locale).put(v.optString("Gender").lowercase()))
                    }
                    Prefs.set(p.voicesName, keep.toString())
                    keep.length()
                }
                Provider.ElevenLabs -> {
                    val list = JSONObject(get("https://api.elevenlabs.io/v1/voices", "xi-api-key" to key)).optJSONArray("voices") ?: JSONArray()
                    val keep = JSONArray()
                    for (i in 0 until list.length()) {
                        val v = list.getJSONObject(i)
                        val labels = v.optJSONObject("labels")
                        keep.put(JSONArray().put(v.optString("voice_id")).put(v.optString("name")).put("").put(labels?.optString("gender")?.lowercase() ?: "").put(labels?.optString("age")?.lowercase() ?: ""))
                    }
                    Prefs.set(p.voicesName, keep.toString())
                    keep.length()
                }
            }
        }
    }
}
