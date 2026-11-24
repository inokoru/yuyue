package app.yuyue.speech

import android.util.Base64
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
 * 外部语音来源：支持国内语音云（小米 MiMo、硅基流动、阿里云百炼、MiniMax）、国际语音云（微软 Azure、
 * OpenAI、ElevenLabs），以及局域网/本机开源语音服务（VoiceStudio、Voicebox、Speaches / mlx-audio / unspeech）。
 */
class CloudVoice(
    val provider: Provider,
    val voiceID: String,
    override val name: String,
    val language: String?,
    gender: String? = null,
    val age: String? = null,
) : BookVoice {
    val gender: String? = gender ?: if (provider == Provider.OpenAI) openAIGender[voiceID] else null

    data class Acting(val instruction: String, val head: String, val tail: String, val style: String?, val degree: Double) {
        val isFlat get() = instruction.isEmpty() && head.isEmpty() && tail.isEmpty() && style == null
        val key get() = "$instruction|$head|$tail|${style ?: ""}|$degree"
    }

    enum class Group {
        Domestic, Global, Local;

        val title: String
            get() = when (this) {
                Domestic -> t("国内服务")
                Global -> t("国际服务")
                Local -> t("本机与局域网")
            }
    }

    enum class Provider(val key: String, val group: Group) {
        Free("free", Group.Domestic),
        Volcengine("volcengine", Group.Domestic),
        Xiaomi("xiaomi", Group.Domestic),
        SiliconFlow("siliconflow", Group.Domestic),
        DashScope("dashscope", Group.Domestic),
        MiniMax("minimax", Group.Domestic),
        Fish("fish", Group.Global),
        Azure("azure", Group.Global),
        OpenAI("openai", Group.Global),
        ElevenLabs("elevenlabs", Group.Global),
        Deepgram("deepgram", Group.Global),
        VoiceStudio("voicestudio", Group.Local),
        Voicebox("voicebox", Group.Local),
        MlxAudio("mlxaudio", Group.Local),
        Speaches("speaches", Group.Local),
        Local("local", Group.Local);

        val isLocal: Boolean get() = group == Group.Local
        val isPaidCloud: Boolean get() = !isLocal && this != Free

        val title: String
            get() = when (this) {
                Free -> t("在线自然语音")
                Volcengine -> t("火山引擎（豆包）")
                Fish -> "Fish Audio"
                Xiaomi -> t("小米 MiMo")
                SiliconFlow -> t("硅基流动")
                DashScope -> t("阿里云百炼")
                MiniMax -> "MiniMax"
                OpenAI -> "OpenAI"
                Azure -> t("微软 Azure")
                ElevenLabs -> "ElevenLabs"
                Deepgram -> "Deepgram"
                VoiceStudio -> "VoiceStudio"
                Voicebox -> "Voicebox"
                MlxAudio -> "mlx-audio"
                Speaches -> "Speaches"
                Local -> Prefs.string("tts.local.title").ifEmpty { t("自定义语音接口") }
            }

        val aliases: String
            get() = when (this) {
                Free -> "free online 免key 免费 在线 自然 语音 kokoro melotts"
                Volcengine -> "volcengine 火山 豆包 字节 doubao seedtts"
                Fish -> "fish audio fishspeech openaudio s1 克隆"
                Xiaomi -> "xiaomi mimo 小米 小爱 冰糖 茉莉"
                SiliconFlow -> "siliconflow 硅基 cosyvoice indextts fish"
                DashScope -> "dashscope aliyun qwen 阿里 百炼 通义 千问"
                MiniMax -> "minimax 海螺 稀宇 speech"
                Azure -> "microsoft azure 微软 晓晓 云希"
                OpenAI -> "openai gpt chatgpt alloy nova"
                ElevenLabs -> "elevenlabs 11labs"
                Deepgram -> "deepgram aura 英文"
                VoiceStudio -> "voicestudio omnivoice 本地 克隆 3900"
                Voicebox -> "voicebox qwen3 kokoro chatterbox 17493"
                MlxAudio -> "mlx-audio mlx apple silicon qwen3 kokoro 8000"
                Speaches -> "speaches docker kokoro piper whisper 8000"
                Local -> "local openai unspeech gptsovits localai kokoro 局域网 8880"
            }

        val badge: String?
            get() = when (this) {
                Free -> t("免 Key")
                Volcengine -> t("豆包语音")
                Fish -> "OpenAudio S1"
                Xiaomi -> t("限时免费")
                Azure, Deepgram -> t("含免费额度")
                VoiceStudio, Voicebox, MlxAudio, Speaches -> t("局域网开源")
                else -> null
            }

        val keyName: String get() = "tts.$key.key"
        val voicesName: String get() = "tts.$key.voices"
        val urlName: String get() = "tts.$key.url"
        val modelName: String get() = "tts.$key.model"
        val enabledName: String get() = "tts.$key.enabled"
        val dismissedName: String get() = "tts.$key.dismissed"

        val urlHint: String
            get() = when (this) {
                VoiceStudio -> "http://192.168.1.5:3900/v1"
                Voicebox -> "http://192.168.1.5:17493/v1"
                else -> "http://192.168.1.5:8000/v1"
            }

        val defaultModel: String
            get() = when (this) {
                VoiceStudio -> "omnivoice"
                Voicebox -> "tts-1"
                MlxAudio -> "mlx-community/Kokoro-82M-bf16"
                Speaches -> "speaches-ai/Kokoro-82M-v1.0-ONNX"
                Local -> "kokoro"
                else -> ""
            }

        val savedURL: String get() = Prefs.string(urlName).trim()
        val savedModel: String get() = Prefs.string(modelName).trim().ifEmpty { defaultModel }
        val apiKey: String? get() = Keys.read(keyName)?.ifEmpty { null }

        /** 若用户在「书伴」里已配置同名服务商，可一键复用其 API Key。 */
        val companionSharedKey: String?
            get() {
                val id = when (this) {
                    SiliconFlow -> "siliconflow"
                    DashScope -> "dashscope"
                    MiniMax -> "minimax"
                    OpenAI -> "openai"
                    else -> null
                } ?: return null
                return Keys.read("companion.key.$id")?.ifEmpty { null }
            }

        val isSetUp: Boolean
            get() = when {
                this == Free -> !Prefs.bool(dismissedName, false)
                isLocal -> Prefs.bool(enabledName, false) && savedURL.isNotEmpty()
                this == Volcengine -> {
                    val k = apiKey
                    k != null && ('|' in k || Prefs.string(VOLC_APP_ID).isNotEmpty())
                }
                else -> apiKey != null && (this != Azure || Prefs.string(REGION).isNotEmpty())
            }

        val keyHint: String
            get() = when (this) {
                Free -> t("无需填写 Key")
                Volcengine -> "Access Token…"
                Fish -> "API Key…"
                Xiaomi, SiliconFlow, DashScope, OpenAI -> "sk-…"
                MiniMax -> "eyJ…"
                Azure -> t("资源的密钥 1")
                ElevenLabs -> "sk_…"
                Deepgram -> "Token…"
                VoiceStudio, Voicebox, MlxAudio, Speaches, Local -> t("一般不用填")
            }

        val help: String
            get() = when (this) {
                Free -> t("0 MB 免下载、免注册免填 Key，直连公共语音接口，已朗读句子自动缓存在本机。")
                Volcengine -> t("火山引擎控制台 › 豆包语音 › 语音合成大模型，获取 AppID 与 Access Token。")
                Fish -> t("fish.audio › API Keys 新建密钥，支持 OpenAudio S1 高表现力中英音色与社区声音库。")
                Xiaomi -> t("platform.xiaomimimo.com 控制台新建 API Key，当前限时免费，支持自然语言语气控制。")
                SiliconFlow -> t("cloud.siliconflow.cn › API 密钥新建。内置 CosyVoice2 音色并支持你在控制台克隆的声音。")
                DashScope -> t("阿里云百炼控制台获取 API-KEY，千问3-TTS 按字计费（约 0.1 元/千字），新开通含免费体验包。")
                MiniMax -> t("platform.minimaxi.com › 接口密钥新建。支持高拟真呼吸感与情绪朗读。")
                OpenAI -> t("platform.openai.com › API keys 里建一个。费用按字数算，走你的账号。")
                Azure -> t("Azure 门户里建一个“语音”资源，拷它的密钥和区域（比如 eastasia）。有免费额度。")
                ElevenLabs -> t("elevenlabs.io › 个人资料 › API Keys。额度按字数算。")
                Deepgram -> t("console.deepgram.com 新建 API Key，Aura-2 英文极速低延迟，注册含免费体验金。")
                VoiceStudio -> t("在电脑上启动 VoiceStudio 并开启局域网访问（端口 3900），手机即可直接用电脑克隆的声音听书。")
                Voicebox -> t("在电脑上启动 Voicebox（端口 17493），手机在同 Wi‑Fi 下即可调用。")
                MlxAudio -> t("在局域网 Mac 上运行 mlx_audio.server（默认端口 8000），手机在同 Wi‑Fi 下即可调用。")
                Speaches -> t("通过 Docker 部署 Speaches（默认端口 8000），适合电脑或家里 NAS 局域网共享。")
                Local -> t("支持 GPT-SoVITS、Kokoro-FastAPI、LocalAI、unspeech 等任何兼容 OpenAI /v1/audio/speech 的服务。")
            }

        companion object {
            const val REGION = "tts.azure.region"
            const val VOLC_APP_ID = "tts.volcengine.appid"
        }
    }

    override val id = "cloud.${provider.key}.$voiceID"
    override val qualityName: String get() = if (provider == Provider.Free) t("免 Key 在线") else provider.title
    override val isOffline: Boolean get() = provider.isLocal
    override fun speaks(tag: String) = language?.let { SpokenLanguage.matches(it, tag) } ?: true

    suspend fun render(parts: List<uniffi.yuyue_core.SpokenPart>, language: String, acting: Acting?): Speech? = withContext(Dispatchers.IO) {
        val text = parts.joinToString("") { it.text }
        if (text.none { it.isLetterOrDigit() }) return@withContext null
        val readings = if (provider == Provider.Azure) parts.mapNotNull { p -> p.reading?.let { "${p.text}=$it" } }.joinToString(",") else ""
        val acted = acting?.takeIf { !it.isFlat }
        val cacheKey = if (acted == null && readings.isEmpty()) text else text + "\u0002" + (acted?.key ?: "") + "\u0002" + readings
        readCached(cacheKey)?.let {
            if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.recordTts(text.length, cached = true)
            return@withContext it
        }
        if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.checkTtsBudget(text.length)
        val (data, ext) = fetch(text, language, parts, acted)
        if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.recordTts(text.length, cached = false)
        val file = writeCached(data, cacheKey, ext)
        decodeFile(file, ext) ?: throw Failure(t("声音数据读不出来"))
    }

    override suspend fun render(text: String, language: String): Speech? = withContext(Dispatchers.IO) {
        if (text.none { it.isLetterOrDigit() }) return@withContext null
        readCached(text)?.let {
            if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.recordTts(text.length, cached = true)
            return@withContext it
        }
        if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.checkTtsBudget(text.length)
        val (data, ext) = fetch(text, language)
        if (provider.isPaidCloud) app.yuyue.companion.CompanionUsage.recordTts(text.length, cached = false)
        val file = writeCached(data, text, ext)
        decodeFile(file, ext) ?: throw Failure(t("声音数据读不出来"))
    }

    suspend fun prefetch(texts: List<String>, language: String) = coroutineScope {
        val missing = texts.filter { t -> t.any { it.isLetterOrDigit() } && !hasCached(t) }
        for (pair in missing.chunked(2)) {
            if (!isActive) return@coroutineScope
            val ok = pair.map { async { runCatching { render(it, language) }.isSuccess } }.all { it.await() }
            if (!ok) return@coroutineScope
        }
    }

    private fun hashKey(text: String): String =
        MessageDigest.getInstance("SHA-256").digest((id + "\u0001" + text).toByteArray()).take(16).joinToString("") { "%02x".format(it) }

    private fun cacheFile(text: String, ext: String = "mp3"): File =
        File(persistentCacheDir, "${provider.key}/${hashKey(text)}.$ext")

    private fun legacyCacheFile(text: String): File =
        File(cacheDir, "${provider.key}/${hashKey(text)}.wav")

    private fun hasCached(text: String): Boolean =
        cacheFile(text, "mp3").exists() || cacheFile(text, "wav").exists() || legacyCacheFile(text).exists()

    private fun readCached(text: String): Speech? {
        for ((file, ext) in listOf(
            cacheFile(text, "mp3") to "mp3",
            cacheFile(text, "wav") to "wav",
            legacyCacheFile(text) to "wav",
        )) {
            if (file.exists()) {
                val decoded = decodeFile(file, ext)
                if (decoded != null) {
                    runCatching { file.setLastModified(System.currentTimeMillis()) }
                    return decoded
                }
            }
        }
        return null
    }

    private fun writeCached(data: ByteArray, key: String, ext: String): File {
        val file = cacheFile(key, ext)
        file.parentFile?.mkdirs()
        val tmp = File(file.parentFile, ".${file.name}.tmp")
        tmp.writeBytes(data)
        if (!tmp.renameTo(file)) {
            file.writeBytes(data)
            tmp.delete()
        }
        trimPersistentCacheIfNeeded()
        return file
    }

    class Failure(message: String) : Exception(message)

    private fun fetch(text: String, language: String, parts: List<uniffi.yuyue_core.SpokenPart>? = null, acting: Acting? = null): Pair<ByteArray, String> {
        return when (provider) {
            Provider.Free -> fetchFreeOnline(voiceID, text, language)

            Provider.Volcengine -> {
                val (appId, token) = volcengineCredentials()
                val reqObj = JSONObject()
                    .put("reqid", java.util.UUID.randomUUID().toString())
                    .put("text", text)
                    .put("text_type", "plain")
                    .put("operation", "query")
                val payload = JSONObject()
                    .put("app", JSONObject().put("appid", appId).put("token", token).put("cluster", "volcano_tts"))
                    .put("user", JSONObject().put("uid", "yuyue-reader"))
                    .put("audio", JSONObject().put("voice_type", voiceID).put("encoding", "mp3").put("speed_ratio", 1.0))
                    .put("request", reqObj)
                val resp = postJSON(provider, "https://openspeech.bytedance.com/api/v1/tts", payload.toString().toByteArray(), "Authorization" to "Bearer;$token")
                val json = JSONObject(String(resp))
                val code = json.optInt("code", 3000)
                if (code != 3000) {
                    val msg = json.optString("message")
                    throw Failure("${provider.title} ($code)" + (if (msg.isNotEmpty()) ": $msg" else ""))
                }
                val b64 = json.optString("data")
                if (b64.isEmpty()) throw Failure(t("声音数据读不出来"))
                val audio = Base64.decode(b64, Base64.DEFAULT)
                audio to sniffExt(audio, "mp3")
            }

            Provider.Fish -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val payload = JSONObject()
                    .put("text", text)
                    .put("format", "mp3")
                    .put("mp3_bitrate", 128)
                    .put("latency", "normal")
                if (voiceID != "default") payload.put("reference_id", voiceID)
                val audio = postJSON(provider, "https://api.fish.audio/v1/tts", payload.toString().toByteArray(), "Authorization" to "Bearer $key", "model" to "s1")
                audio to sniffExt(audio, "mp3")
            }

            Provider.Xiaomi -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val msgs = JSONArray()
                acting?.instruction?.takeIf { it.isNotEmpty() }?.let {
                    msgs.put(JSONObject().put("role", "user").put("content", it))
                }
                msgs.put(JSONObject().put("role", "assistant").put("content", text))
                val payload = JSONObject()
                    .put("model", "mimo-v2.5-tts")
                    .put("messages", msgs)
                    .put("audio", JSONObject().put("format", "mp3").put("voice", voiceID))
                val resp = postJSON(provider, "https://api.xiaomimimo.com/v1/chat/completions", payload.toString().toByteArray(), "Authorization" to "Bearer $key")
                val b64 = JSONObject(String(resp))
                    .optJSONArray("choices")?.optJSONObject(0)
                    ?.optJSONObject("message")?.optJSONObject("audio")?.optString("data").orEmpty()
                if (b64.isEmpty()) throw Failure(t("声音数据读不出来"))
                val audio = Base64.decode(b64, Base64.DEFAULT)
                audio to sniffExt(audio, "mp3")
            }

            Provider.SiliconFlow -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val (modelName, actualVoice) = splitSiliconFlowVoice(voiceID)
                val payload = JSONObject()
                    .put("model", modelName)
                    .put("voice", actualVoice)
                    .put("input", text)
                    .put("response_format", "mp3")
                val audio = postJSON(provider, "https://api.siliconflow.cn/v1/audio/speech", payload.toString().toByteArray(), "Authorization" to "Bearer $key")
                audio to sniffExt(audio, "mp3")
            }

            Provider.DashScope -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val payload = JSONObject()
                    .put("model", "qwen3-tts-flash")
                    .put("input", JSONObject().put("text", text).put("voice", voiceID))
                val resp = postJSON(provider, "https://dashscope.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation", payload.toString().toByteArray(), "Authorization" to "Bearer $key")
                val audioObj = JSONObject(String(resp)).optJSONObject("output")?.optJSONObject("audio") ?: throw Failure(t("声音数据读不出来"))
                val b64 = audioObj.optString("data")
                if (b64.isNotEmpty()) {
                    val audio = Base64.decode(b64, Base64.DEFAULT)
                    return audio to sniffExt(audio, "wav")
                }
                val urlStr = audioObj.optString("url").replace("http://", "https://")
                if (urlStr.isEmpty()) throw Failure(t("声音数据读不出来"))
                val audio = getBytes(provider, urlStr)
                audio to sniffExt(audio, "wav")
            }

            Provider.MiniMax -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val voiceSetting = JSONObject().put("voice_id", voiceID).put("speed", 1.0).put("vol", 1.0).put("pitch", 0)
                miniMaxEmotion(acting)?.let { voiceSetting.put("emotion", it) }
                val payload = JSONObject()
                    .put("model", "speech-02-hd")
                    .put("text", text)
                    .put("stream", false)
                    .put("voice_setting", voiceSetting)
                    .put("audio_setting", JSONObject().put("sample_rate", 24000).put("bitrate", 64000).put("format", "mp3").put("channel", 1))
                    .put("output_format", "hex")
                val resp = postJSON(provider, "https://api.minimaxi.com/v1/t2a_v2", payload.toString().toByteArray(), "Authorization" to "Bearer $key")
                val json = JSONObject(String(resp))
                val baseResp = json.optJSONObject("base_resp")
                val status = baseResp?.optInt("status_code", 0) ?: 0
                if (status != 0) {
                    val msg = baseResp?.optString("status_msg")
                    if (status == 1004 || status == 2049) throw Failure(t("%@ 的 Key 不对", provider.title))
                    if (status == 1008 || status == 1026) throw Failure(t("%@ 的额度用完了", provider.title))
                    throw Failure("${provider.title} $status" + (msg?.let { ": $it" } ?: ""))
                }
                val audioStr = json.optJSONObject("data")?.optString("audio").orEmpty()
                if (audioStr.isEmpty()) throw Failure(t("声音数据读不出来"))
                val audio = if (audioStr.startsWith("http")) getBytes(provider, audioStr) else decodeHex(audioStr) ?: throw Failure(t("声音数据读不出来"))
                audio to sniffExt(audio, "mp3")
            }

            Provider.OpenAI -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val o = JSONObject().put("model", "gpt-4o-mini-tts").put("voice", voiceID).put("input", text).put("response_format", "mp3")
                acting?.instruction?.takeIf { it.isNotEmpty() }?.let { o.put("instructions", it) }
                val audio = postJSON(provider, "https://api.openai.com/v1/audio/speech", o.toString().toByteArray(), "Authorization" to "Bearer $key")
                audio to sniffExt(audio, "mp3")
            }

            Provider.Azure -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val region = Prefs.string(Provider.REGION)
                val c = URL("https://$region.tts.speech.microsoft.com/cognitiveservices/v1").openConnection() as HttpURLConnection
                c.setRequestProperty("Ocp-Apim-Subscription-Key", key)
                c.setRequestProperty("Content-Type", "application/ssml+xml")
                c.setRequestProperty("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3")
                c.setRequestProperty("User-Agent", "Yuyue")
                val lang = this.language ?: SpokenLanguage.voiceLanguages(language).first()
                fun escape(t: String) = t.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
                var inner = (parts ?: listOf(uniffi.yuyue_core.SpokenPart(text, null))).joinToString("") { p ->
                    val r = p.reading
                    if (r == null || !lang.startsWith("zh")) escape(p.text) else "<phoneme alphabet='sapi' ph='${sapi(r)}'>${escape(p.text)}</phoneme>"
                }
                acting?.style?.let { inner = "<mstts:express-as style='$it' styledegree='${"%.1f".format(java.util.Locale.ROOT, acting.degree)}'>$inner</mstts:express-as>" }
                val body = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xmlns:mstts='https://www.w3.org/2001/mstts' xml:lang='$lang'><voice name='$voiceID'>$inner</voice></speak>".toByteArray()
                c.requestMethod = "POST"
                c.doOutput = true
                c.connectTimeout = 15_000
                c.readTimeout = 30_000
                c.outputStream.use { it.write(body) }
                val code = c.responseCode
                if (code !in 200..299) throw failure(provider, code, c.errorStream?.readBytes())
                c.inputStream.use { it.readBytes() } to "mp3"
            }

            Provider.ElevenLabs -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val payload = if (acting != null && (acting.head.isNotEmpty() || acting.tail.isNotEmpty())) {
                    JSONObject().put("text", acting.head + text + acting.tail).put("model_id", "eleven_v3")
                } else {
                    JSONObject().put("text", text).put("model_id", "eleven_flash_v2_5")
                }
                val audio = postJSON(provider, "https://api.elevenlabs.io/v1/text-to-speech/$voiceID?output_format=mp3_24000_48", payload.toString().toByteArray(), "xi-api-key" to key)
                audio to "mp3"
            }

            Provider.Deepgram -> {
                val key = provider.apiKey ?: throw Failure(t("还没填 %@ 的 Key", provider.title))
                val q = java.net.URLEncoder.encode(voiceID, "UTF-8")
                val payload = JSONObject().put("text", text)
                val audio = postJSON(provider, "https://api.deepgram.com/v1/speak?model=$q&encoding=mp3", payload.toString().toByteArray(), "Authorization" to "Token $key")
                audio to "mp3"
            }

            Provider.VoiceStudio, Provider.Voicebox, Provider.MlxAudio, Provider.Speaches, Provider.Local -> {
                val endpoint = speechEndpoint(provider.savedURL) ?: throw Failure(t("还没填 %@ 的服务地址", provider.title))
                val o = JSONObject()
                    .put("model", provider.savedModel)
                    .put("voice", voiceID)
                    .put("input", text)
                    .put("response_format", "wav")
                acting?.instruction?.takeIf { it.isNotEmpty() }?.let { o.put("instructions", it) }
                val headers = mutableListOf<Pair<String, String>>()
                provider.apiKey?.let { headers += "Authorization" to "Bearer $it" }
                val audio = try {
                    postJSON(provider, endpoint, o.toString().toByteArray(), *headers.toTypedArray(), readTimeoutMs = 90_000)
                } catch (e: Failure) {
                    throw e
                } catch (_: Exception) {
                    throw Failure(t("连不上 %@，请确认服务已启动且在同一局域网", provider.title))
                }
                audio to sniffExt(audio, "wav")
            }
        }
    }

    companion object {
        private val cacheDir: File get() = File(YuyueApp.instance.cacheDir, "yuyue-voices")
        private val persistentCacheDir: File get() = File(YuyueApp.instance.filesDir, "cloud-voices")
        private const val MAX_PERSISTENT_CACHE_BYTES = 300_000_000L
        private val trimCount = java.util.concurrent.atomic.AtomicInteger(0)

        private fun postJSON(p: Provider, url: String, body: ByteArray, vararg headers: Pair<String, String>, readTimeoutMs: Int = 35_000): ByteArray {
            val c = URL(url).openConnection() as HttpURLConnection
            c.requestMethod = "POST"
            c.doOutput = true
            c.connectTimeout = 15_000
            c.readTimeout = readTimeoutMs
            c.setRequestProperty("Content-Type", "application/json")
            headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
            c.outputStream.use { it.write(body) }
            val code = c.responseCode
            if (code !in 200..299) throw failure(p, code, c.errorStream?.readBytes())
            return c.inputStream.use { it.readBytes() }
        }

        private fun getBytes(p: Provider, url: String): ByteArray {
            val c = URL(url).openConnection() as HttpURLConnection
            c.connectTimeout = 15_000
            c.readTimeout = 35_000
            val code = c.responseCode
            if (code !in 200..299) throw failure(p, code, c.errorStream?.readBytes())
            return c.inputStream.use { it.readBytes() }
        }

        fun normalizedBaseV1(rawBase: String): String {
            var s = rawBase.trim().trimEnd('/')
            if (s.endsWith("/audio/speech")) s = s.removeSuffix("/audio/speech")
            if (s.isNotEmpty() && !s.endsWith("/v1")) s += "/v1"
            return s
        }

        fun speechEndpoint(rawBase: String): String? {
            var s = rawBase.trim().trimEnd('/')
            if (s.isEmpty()) return null
            if (s.endsWith("/audio/speech")) return s
            if (!s.endsWith("/v1") && !s.contains("/v1/")) s += "/v1"
            return "$s/audio/speech"
        }

        private fun splitSiliconFlowVoice(id: String): Pair<String, String> {
            val bar = id.indexOf('|')
            if (bar > 0) return id.substring(0, bar) to id.substring(bar + 1)
            val colon = id.indexOf(':')
            if (colon > 0 && (id.startsWith("FunAudioLLM/") || id.startsWith("fishaudio/") || id.startsWith("IndexTeam/"))) {
                return id.substring(0, colon) to id
            }
            return "FunAudioLLM/CosyVoice2-0.5B" to id
        }

        private fun miniMaxEmotion(acting: Acting?): String? {
            if (acting == null) return null
            val s = (acting.instruction + " " + (acting.style ?: "")).lowercase()
            return when {
                listOf("happy", "cheer", "欢", "喜", "笑").any { it in s } -> "happy"
                listOf("sad", "悲", "伤", "哭", "哽咽").any { it in s } -> "sad"
                listOf("angry", "怒", "气", "吼").any { it in s } -> "angry"
                listOf("fear", "恐", "怕", "颤").any { it in s } -> "fearful"
                listOf("calm", "gentle", "平", "柔").any { it in s } -> "calm"
                else -> null
            }
        }

        private fun decodeHex(hex: String): ByteArray? {
            if (hex.length < 4 || hex.length % 2 != 0) return null
            val out = ByteArray(hex.length / 2)
            for (i in out.indices) {
                val hi = Character.digit(hex[i * 2], 16)
                val lo = Character.digit(hex[i * 2 + 1], 16)
                if (hi < 0 || lo < 0) return null
                out[i] = ((hi shl 4) or lo).toByte()
            }
            return out
        }

        fun sniffExt(data: ByteArray, fallback: String): String {
            if (data.size >= 4) {
                if (data[0] == 0x52.toByte() && data[1] == 0x49.toByte() && data[2] == 0x46.toByte() && data[3] == 0x46.toByte()) return "wav"
                if (data[0] == 0x66.toByte() && data[1] == 0x4C.toByte() && data[2] == 0x61.toByte() && data[3] == 0x43.toByte()) return "flac"
                if (data[0] == 0x4F.toByte() && data[1] == 0x67.toByte() && data[2] == 0x67.toByte() && data[3] == 0x53.toByte()) return "ogg"
                if (data[0] == 0x49.toByte() && data[1] == 0x44.toByte() && data[2] == 0x33.toByte()) return "mp3"
                if (data[0] == 0xFF.toByte() && (data[1].toInt() and 0xE0) == 0xE0) return "mp3"
            }
            return fallback
        }

        private fun trimPersistentCacheIfNeeded() {
            if (trimCount.incrementAndGet() % 32 != 0) return
            val root = persistentCacheDir
            if (!root.isDirectory) return
            val files = root.walkTopDown().filter { it.isFile && !it.name.startsWith(".") }.toList()
            var total = files.sumOf { it.length() }
            if (total <= MAX_PERSISTENT_CACHE_BYTES) return
            val target = (MAX_PERSISTENT_CACHE_BYTES * 0.8).toLong()
            for (f in files.sortedBy { it.lastModified() }) {
                if (total <= target) break
                val sz = f.length()
                if (f.delete()) total -= sz
            }
        }

        private fun decodeFile(file: File, ext: String): Speech? {
            val head = runCatching { file.inputStream().use { val b = ByteArray(4); if (it.read(b) == 4) b else null } }.getOrNull()
            val realExt = if (head != null) sniffExt(head, ext) else ext
            return if (realExt == "wav") SystemVoice.Engine.wav(file) else decodeCompressed(file)
        }

        private fun decodeCompressed(file: File): Speech? {
            val ex = android.media.MediaExtractor()
            try {
                ex.setDataSource(file.path)
                if (ex.trackCount == 0) return null
                ex.selectTrack(0)
                val format = ex.getTrackFormat(0)
                val mime = format.getString(android.media.MediaFormat.KEY_MIME) ?: return null
                var rate = if (format.containsKey(android.media.MediaFormat.KEY_SAMPLE_RATE)) {
                    format.getInteger(android.media.MediaFormat.KEY_SAMPLE_RATE)
                } else 24_000
                var channels = if (format.containsKey(android.media.MediaFormat.KEY_CHANNEL_COUNT)) {
                    maxOf(1, format.getInteger(android.media.MediaFormat.KEY_CHANNEL_COUNT))
                } else 1
                val codec = android.media.MediaCodec.createDecoderByType(mime)
                codec.configure(format, null, null, 0)
                codec.start()
                var out = ShortArray(maxOf(16_000, rate * 4))
                var n = 0
                val info = android.media.MediaCodec.BufferInfo()
                var inputDone = false
                var idleAfterEos = 0
                try {
                    while (true) {
                        if (!inputDone) {
                            val i = codec.dequeueInputBuffer(10_000L)
                            if (i >= 0) {
                                val inBuf = codec.getInputBuffer(i)
                                val size = if (inBuf != null) ex.readSampleData(inBuf, 0) else -1
                                if (size < 0) {
                                    codec.queueInputBuffer(i, 0, 0, 0, android.media.MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                    inputDone = true
                                } else {
                                    codec.queueInputBuffer(i, 0, size, ex.sampleTime, 0)
                                    ex.advance()
                                }
                            }
                        }
                        val o = codec.dequeueOutputBuffer(info, 10_000L)
                        if (o == android.media.MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                            val f = codec.outputFormat
                            if (f.containsKey(android.media.MediaFormat.KEY_SAMPLE_RATE)) rate = f.getInteger(android.media.MediaFormat.KEY_SAMPLE_RATE)
                            if (f.containsKey(android.media.MediaFormat.KEY_CHANNEL_COUNT)) channels = maxOf(1, f.getInteger(android.media.MediaFormat.KEY_CHANNEL_COUNT))
                            idleAfterEos = 0
                        } else if (o >= 0) {
                            idleAfterEos = 0
                            if (info.size > 0) {
                                val raw = codec.getOutputBuffer(o)
                                if (raw != null) {
                                    val start = info.offset.coerceIn(0, raw.capacity())
                                    val end = (info.offset + info.size).coerceIn(start, raw.capacity())
                                    raw.position(start)
                                    raw.limit(end)
                                    val pcm = raw.slice().order(java.nio.ByteOrder.nativeOrder()).asShortBuffer()
                                    val ch = maxOf(1, channels)
                                    val frames = pcm.remaining() / ch
                                    if (frames > 0) {
                                        if (n + frames > out.size) out = out.copyOf(maxOf(out.size * 2, n + frames))
                                        if (ch == 1) {
                                            pcm.get(out, n, frames)
                                            n += frames
                                        } else {
                                            for (f in 0 until frames) {
                                                var sum = 0
                                                for (c in 0 until ch) sum += pcm.get()
                                                out[n++] = (sum / ch).toShort()
                                            }
                                        }
                                    }
                                }
                            }
                            codec.releaseOutputBuffer(o, false)
                            if (info.flags and android.media.MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                        } else if (inputDone && ++idleAfterEos >= 100) {
                            break
                        }
                    }
                } finally {
                    runCatching { codec.stop() }
                    runCatching { codec.release() }
                }
                return if (n > 0 && rate > 0) Speech(out.copyOf(n), rate) else null
            } catch (_: Throwable) {
                return null
            } finally {
                runCatching { ex.release() }
            }
        }

        val openAIVoices = listOf("alloy", "ash", "ballad", "coral", "echo", "fable", "nova", "onyx", "sage", "shimmer")
        val openAIGender = mapOf(
            "alloy" to "female", "ash" to "male", "ballad" to "male", "coral" to "female", "echo" to "male",
            "fable" to "male", "nova" to "female", "onyx" to "male", "sage" to "female", "shimmer" to "female",
        )

        data class PresetVoice(val id: String, val name: String, val lang: String?, val gender: String?, val age: String?)

        val freeOnlineVoices = listOf(
            PresetVoice("zh_baidu", "知声 · 清晰播报", "zh-CN", "female", "middle_aged"),
            PresetVoice("en_us", "Emma · American", "en-US", "female", "young"),
            PresetVoice("en_uk", "Clara · British", "en-GB", "female", "young"),
        )

        val volcengineVoices = listOf(
            PresetVoice("zh_female_cancan_mars_bigtts", "灿灿 · 温暖女声", "zh-CN", "female", "young"),
            PresetVoice("zh_female_shuangkuaisisi_moon_bigtts", "思思 · 爽快自然", "zh-CN", "female", "young"),
            PresetVoice("zh_male_wennuanahu_moon_bigtts", "阿虎 · 温暖男声", "zh-CN", "male", "young"),
            PresetVoice("zh_male_shaonianzixin_moon_bigtts", "梓新 · 少年男声", "zh-CN", "male", "young"),
            PresetVoice("zh_female_wanwanxiaohe_moon_bigtts", "小荷 · 温婉旁白", "zh-CN", "female", "young"),
            PresetVoice("en_female_anna_mars_bigtts", "Anna · English Narrator", "en-US", "female", "young"),
        )

        val fishPresets = listOf(
            PresetVoice("default", "OpenAudio S1 · 默认自然旁白", null, "female", "young"),
            PresetVoice("7f92f8afb8ec43bf81429cc1c9199cb1", "丁真 · 社区热榜示例", "zh-CN", "male", "young"),
            PresetVoice("54a5170264694bfc8e9ad98df7bd89c3", "温柔电台女声", "zh-CN", "female", "young"),
        )

        fun volcengineCredentials(): Pair<String, String> {
            val raw = Provider.Volcengine.apiKey?.trim().orEmpty()
            if (raw.isEmpty()) throw Failure(t("还没填 %@ 的 Key", Provider.Volcengine.title))
            if ('|' in raw) {
                val idx = raw.indexOf('|')
                val a = raw.substring(0, idx).trim()
                val k = raw.substring(idx + 1).trim()
                if (a.isNotEmpty() && k.isNotEmpty()) return a to k
            }
            val appId = Prefs.string(Provider.VOLC_APP_ID).trim()
            if (appId.isEmpty()) throw Failure(t("请先填写火山引擎 AppID"))
            return appId to raw
        }

        internal fun freeOnlineRoute(voiceID: String, text: String, language: String): String {
            val hasCjk = text.any { it.code in 0x4E00..0x9FFF }
            val isEn = !hasCjk && (
                language.lowercase().startsWith("en")
                    || voiceID.startsWith("en_")
                    || voiceID.startsWith("af_") || voiceID.startsWith("am_") || voiceID.startsWith("bf_")
            )
            return if (isEn) {
                if (voiceID == "en_uk" || voiceID.startsWith("bf_")) "baidu_uk" else "baidu_en"
            } else {
                // dict.youdao.com/dictvoice only serves dictionary headwords and returns HTTP 500 on full sentences;
                // route all Chinese free voices (including legacy zh_youdao) to Baidu sentence TTS.
                "baidu_zh"
            }
        }

        internal fun freeOnlineUrl(route: String, encodedChunk: String): String = when (route) {
            "baidu_zh" -> "https://fanyi.baidu.com/gettts?lan=zh&spd=5&source=web&text=$encodedChunk"
            "baidu_uk" -> "https://fanyi.baidu.com/gettts?lan=uk&spd=3&source=web&text=$encodedChunk"
            else -> "https://fanyi.baidu.com/gettts?lan=en&spd=3&source=web&text=$encodedChunk"
        }

        private fun fetchFreeOnline(voiceID: String, text: String, language: String): Pair<ByteArray, String> {
            val customBase = Provider.Free.savedURL
            if (customBase.isNotEmpty()) {
                val ep = speechEndpoint(customBase)
                if (ep != null) {
                    val payload = JSONObject()
                        .put("model", Provider.Free.savedModel.ifEmpty { "kokoro" })
                        .put("voice", voiceID)
                        .put("input", text)
                        .put("response_format", "mp3")
                    val audio = postJSON(Provider.Free, ep, payload.toString().toByteArray(), readTimeoutMs = 30_000)
                    return audio to sniffExt(audio, "mp3")
                }
            }

            val clean = text.trim()
            if (clean.isEmpty()) throw Failure(t("声音数据读不出来"))
            val chunks = splitFreeChunks(clean, 160)
            // 每个免费音色严格绑定单一线路，避免单句超时后静默切到另一家的不同声线。
            val route = freeOnlineRoute(voiceID, clean, language)

            repeat(2) { attempt ->
                if (attempt > 0) Thread.sleep(250)
                val combined = runCatching {
                    val out = java.io.ByteArrayOutputStream()
                    for (chunk in chunks) {
                        val enc = java.net.URLEncoder.encode(chunk, "UTF-8").replace("+", "%20")
                        val url = freeOnlineUrl(route, enc)
                        val c = URL(url).openConnection() as HttpURLConnection
                        try {
                            c.requestMethod = "GET"
                            c.connectTimeout = 10_000
                            c.readTimeout = 12_000
                            c.setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android 14) AppleWebKit/537.36")
                            if (c.responseCode !in 200..299) throw IllegalStateException("HTTP ${c.responseCode}")
                            val bytes = c.inputStream.use { it.readBytes() }
                            if (!looksLikeAudio(bytes)) throw IllegalStateException("Non-audio response")
                            out.write(bytes)
                        } finally {
                            c.disconnect()
                        }
                    }
                    out.toByteArray()
                }.getOrNull()
                if (combined != null && combined.size > 256) {
                    return combined to sniffExt(combined, "mp3")
                }
            }
            throw Failure(t("在线自然语音当前连不上，可重试，或改用本机离线/系统声音"))
        }

        private fun splitFreeChunks(text: String, maxChars: Int): List<String> {
            if (text.length <= maxChars) return listOf(text)
            val breaks = setOf('。', '！', '？', '；', '，', '.', '!', '?', ';', ',')
            val chunks = mutableListOf<String>()
            val sb = StringBuilder()
            for (ch in text) {
                sb.append(ch)
                if (sb.length >= maxChars || (sb.length >= maxChars * 2 / 3 && ch in breaks)) {
                    val s = sb.toString().trim()
                    if (s.isNotEmpty()) chunks += s
                    sb.clear()
                }
            }
            val rem = sb.toString().trim()
            if (rem.isNotEmpty()) chunks += rem
            return chunks.ifEmpty { listOf(text) }
        }

        private fun looksLikeAudio(bytes: ByteArray): Boolean {
            if (bytes.size <= 256) return false
            val b0 = bytes[0].toInt() and 0xFF
            val b1 = bytes[1].toInt() and 0xFF
            val b2 = bytes[2].toInt() and 0xFF
            val b3 = bytes[3].toInt() and 0xFF
            if (b0 == 0xFF && (b1 and 0xE0) == 0xE0) return true
            if (b0 == 0x49 && b1 == 0x44 && b2 == 0x33) return true
            if (b0 == 0x52 && b1 == 0x49 && b2 == 0x46 && b3 == 0x46) return true
            if (b0 == 0x4F && b1 == 0x67 && b2 == 0x67 && b3 == 0x53) return true
            return false
        }

        val xiaomiVoices = listOf(
            PresetVoice("冰糖", "冰糖 · 清甜活泼", "zh-CN", "female", "young"),
            PresetVoice("茉莉", "茉莉 · 温柔细腻", "zh-CN", "female", "young"),
            PresetVoice("苏打", "苏打 · 清爽阳光", "zh-CN", "male", "young"),
            PresetVoice("白桦", "白桦 · 沉稳厚重", "zh-CN", "male", "middle_aged"),
            PresetVoice("Mia", "Mia · 自然女声", "en-US", "female", "young"),
            PresetVoice("Chloe", "Chloe · 知性女声", "en-US", "female", "middle_aged"),
            PresetVoice("Milo", "Milo · 明亮男声", "en-US", "male", "young"),
            PresetVoice("Dean", "Dean · 浑厚男声", "en-US", "male", "middle_aged"),
        )

        val siliconFlowPresets = listOf(
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:anna", "安娜 · 沉稳女声", null, "female", "middle_aged"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:claire", "克莱尔 · 温柔女声", null, "female", "young"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:bella", "贝拉 · 热情女声", null, "female", "young"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:diana", "黛安娜 · 欢快女声", null, "female", "young"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:alex", "亚历克斯 · 沉稳男声", null, "male", "middle_aged"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:charles", "查尔斯 · 磁性男声", null, "male", "middle_aged"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:david", "大卫 · 明亮男声", null, "male", "young"),
            PresetVoice("FunAudioLLM/CosyVoice2-0.5B:benjamin", "本杰明 · 低沉男声", null, "male", "old"),
        )

        val dashScopeVoices = listOf(
            PresetVoice("Cherry", "芊悦 · 阳光女声", null, "female", "young"),
            PresetVoice("Serena", "苏瑶 · 温婉女声", null, "female", "young"),
            PresetVoice("Ethan", "晨煦 · 温暖男声", null, "male", "young"),
            PresetVoice("Chelsie", "千雪 · 灵动女声", null, "female", "young"),
        )

        val miniMaxPresets = listOf(
            PresetVoice("female-tianmei", "温柔甜美 · 女声", null, "female", "young"),
            PresetVoice("female-yujie", "知性御姐 · 女声", null, "female", "middle_aged"),
            PresetVoice("female-shaonv", "元气少女 · 女声", null, "female", "young"),
            PresetVoice("male-qn-qingse", "青涩青年 · 男声", null, "male", "young"),
            PresetVoice("male-qn-jingying", "精英沉稳 · 男声", null, "male", "middle_aged"),
        )

        val deepgramVoices = listOf(
            PresetVoice("aura-2-thalia-en", "Thalia · 温暖女声", "en-US", "female", "young"),
            PresetVoice("aura-2-andromeda-en", "Andromeda · 知性女声", "en-US", "female", "middle_aged"),
            PresetVoice("aura-2-helena-en", "Helena · 柔和女声", "en-US", "female", "young"),
            PresetVoice("aura-2-apollo-en", "Apollo · 沉稳男声", "en-US", "male", "middle_aged"),
            PresetVoice("aura-2-aries-en", "Aries · 明亮男声", "en-US", "male", "young"),
        )

        val defaultLocalVoices = listOf(
            PresetVoice("default", "默认音色", null, null, null),
            PresetVoice("alloy", "Alloy", null, "female", null),
            PresetVoice("nova", "Nova", null, "female", null),
            PresetVoice("onyx", "Onyx", null, "male", null),
        )

        fun sapi(pinyin: String): String {
            val marks = mapOf(
                'ā' to ('a' to 1), 'á' to ('a' to 2), 'ǎ' to ('a' to 3), 'à' to ('a' to 4), 'ē' to ('e' to 1), 'é' to ('e' to 2), 'ě' to ('e' to 3), 'è' to ('e' to 4),
                'ī' to ('i' to 1), 'í' to ('i' to 2), 'ǐ' to ('i' to 3), 'ì' to ('i' to 4), 'ō' to ('o' to 1), 'ó' to ('o' to 2), 'ǒ' to ('o' to 3), 'ò' to ('o' to 4),
                'ū' to ('u' to 1), 'ú' to ('u' to 2), 'ǔ' to ('u' to 3), 'ù' to ('u' to 4), 'ǖ' to ('v' to 1), 'ǘ' to ('v' to 2), 'ǚ' to ('v' to 3), 'ǜ' to ('v' to 4), 'ü' to ('v' to 0),
            )
            return pinyin.lowercase().split(' ', '\'', '-').filter { it.isNotEmpty() }.joinToString(" ") { syl ->
                var tone = 5
                val plain = syl.map { c -> marks[c]?.let { (base, t) -> if (t > 0) tone = t; base } ?: c }.joinToString("")
                val last = plain.last()
                if (last.isDigit()) "${plain.dropLast(1)} $last" else "$plain $tone"
            }
        }

        fun failure(p: Provider, code: Int, data: ByteArray?): Failure {
            val json = data?.let { runCatching { JSONObject(String(it)) }.getOrNull() }
            val message = json?.optJSONObject("error")?.optString("message")?.ifEmpty { null }
                ?: json?.optJSONObject("detail")?.optString("message")?.ifEmpty { null }
                ?: json?.optString("message")?.ifEmpty { null }
            return Failure(when (code) {
                401, 403 -> t("%@ 的 Key 不对", p.title)
                402 -> t("%@ 的额度用完了", p.title)
                429 -> if (message?.contains("quota") == true) t("%@ 的额度用完了", p.title) else t("%@ 说太忙，等一会儿再试", p.title)
                else -> "${p.title} $code" + (message?.let { ": $it" } ?: "")
            })
        }

        fun wav(pcm: ByteArray, rate: Int): ByteArray {
            val b = java.nio.ByteBuffer.allocate(44 + pcm.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            b.put("RIFF".toByteArray()).putInt(36 + pcm.size).put("WAVEfmt ".toByteArray()).putInt(16).putShort(1).putShort(1)
                .putInt(rate).putInt(rate * 2).putShort(2).putShort(16).put("data".toByteArray()).putInt(pcm.size).put(pcm)
            return b.array()
        }

        private fun savedTuples(p: Provider): List<CloudVoice> =
            runCatching { JSONArray(Prefs.string(p.voicesName, "[]")) }.getOrDefault(JSONArray()).let { a ->
                (0 until a.length()).map { a.getJSONArray(it) }.map {
                    CloudVoice(p, it.getString(0), it.getString(1), it.optString(2).ifEmpty { null }, it.optString(3).ifEmpty { null }, it.optString(4).ifEmpty { null })
                }
            }

        fun all(): List<CloudVoice> = Provider.entries.filter { it.isSetUp }.flatMap { p ->
            when (p) {
                Provider.Free -> freeOnlineVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.Volcengine -> volcengineVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.Fish -> savedTuples(p).ifEmpty { fishPresets.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) } }
                Provider.OpenAI -> openAIVoices.map { CloudVoice(p, it, it.replaceFirstChar(Char::uppercase), null) }
                Provider.Xiaomi -> xiaomiVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.DashScope -> dashScopeVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.SiliconFlow -> savedTuples(p) + siliconFlowPresets.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.MiniMax -> savedTuples(p) + miniMaxPresets.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.Azure, Provider.ElevenLabs -> savedTuples(p)
                Provider.Deepgram -> deepgramVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                Provider.VoiceStudio, Provider.Voicebox, Provider.MlxAudio, Provider.Speaches, Provider.Local -> {
                    val saved = savedTuples(p)
                    if (saved.isNotEmpty()) saved else defaultLocalVoices.map { CloudVoice(p, it.id, it.name, it.lang, it.gender, it.age) }
                }
            }
        }

        suspend fun refresh(p: Provider): Int = withContext(Dispatchers.IO) {
            fun get(url: String, vararg headers: Pair<String, String>): String {
                val c = URL(url).openConnection() as HttpURLConnection
                headers.forEach { (k, v) -> c.setRequestProperty(k, v) }
                c.connectTimeout = 15_000
                c.readTimeout = 15_000
                val code = c.responseCode
                if (code !in 200..299) throw failure(p, code, c.errorStream?.readBytes())
                return c.inputStream.use { String(it.readBytes()) }
            }
            when (p) {
                Provider.Free -> {
                    fetchFreeOnline("zh_baidu", "你好，我来给你读这本书。", "zh-CN")
                    freeOnlineVoices.size
                }
                Provider.Volcengine -> {
                    val (appId, token) = volcengineCredentials()
                    val probe = JSONObject()
                        .put("app", JSONObject().put("appid", appId).put("token", token).put("cluster", "volcano_tts"))
                        .put("user", JSONObject().put("uid", "yuyue-probe"))
                        .put("audio", JSONObject().put("voice_type", "zh_female_cancan_mars_bigtts").put("encoding", "mp3"))
                        .put("request", JSONObject().put("reqid", java.util.UUID.randomUUID().toString()).put("text", "好").put("text_type", "plain").put("operation", "query"))
                    val resp = postJSON(p, "https://openspeech.bytedance.com/api/v1/tts", probe.toString().toByteArray(), "Authorization" to "Bearer;$token")
                    val code = JSONObject(String(resp)).optInt("code", 0)
                    if (code != 3000) throw Failure(t("%@ 的 AppID 或 Token 不对", p.title))
                    volcengineVoices.size
                }
                Provider.Fish -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    val raw = get("https://api.fish.audio/model?page_size=20&page_number=1&sort_by=score", "Authorization" to "Bearer $key")
                    val items = JSONObject(raw).optJSONArray("items") ?: JSONArray()
                    val keep = JSONArray()
                    keep.put(JSONArray().put("default").put("OpenAudio S1 · 默认自然旁白").put("").put("female").put("young"))
                    for (i in 0 until items.length()) {
                        val item = items.optJSONObject(i) ?: continue
                        val id = item.optString("_id")
                        val title = item.optString("title")
                        if (id.isNotEmpty() && title.isNotEmpty()) {
                            keep.put(JSONArray().put(id).put(title).put("").put("").put(""))
                        }
                    }
                    Prefs.set(p.voicesName, keep.toString())
                    keep.length()
                }
                Provider.OpenAI -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    get("https://api.openai.com/v1/models/gpt-4o-mini-tts", "Authorization" to "Bearer $key")
                    openAIVoices.size
                }
                Provider.Xiaomi -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    get("https://api.xiaomimimo.com/v1/models", "Authorization" to "Bearer $key")
                    xiaomiVoices.size
                }
                Provider.SiliconFlow -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    val raw = get("https://api.siliconflow.cn/v1/audio/voice/list", "Authorization" to "Bearer $key")
                    val arr = JSONObject(raw).optJSONArray("results") ?: JSONArray()
                    val keep = JSONArray()
                    for (i in 0 until arr.length()) {
                        val item = arr.getJSONObject(i)
                        val uri = item.optString("uri")
                        if (uri.isEmpty()) continue
                        val model = item.optString("model").ifEmpty { "FunAudioLLM/CosyVoice2-0.5B" }
                        val customName = item.optString("customName").ifEmpty { uri }
                        keep.put(JSONArray().put("$model|$uri").put("$customName · 我的克隆").put("").put("").put(""))
                    }
                    Prefs.set(p.voicesName, keep.toString())
                    siliconFlowPresets.size + keep.length()
                }
                Provider.DashScope -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    get("https://dashscope.aliyuncs.com/compatible-mode/v1/models", "Authorization" to "Bearer $key")
                    dashScopeVoices.size
                }
                Provider.MiniMax -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    val probe = JSONObject()
                        .put("model", "speech-02-turbo")
                        .put("text", "好")
                        .put("stream", false)
                        .put("voice_setting", JSONObject().put("voice_id", "female-tianmei"))
                        .put("audio_setting", JSONObject().put("sample_rate", 24000).put("bitrate", 32000).put("format", "mp3").put("channel", 1))
                        .put("output_format", "hex")
                    val resp = postJSON(p, "https://api.minimaxi.com/v1/t2a_v2", probe.toString().toByteArray(), "Authorization" to "Bearer $key")
                    val json = JSONObject(String(resp))
                    val status = json.optJSONObject("base_resp")?.optInt("status_code", 0) ?: 0
                    if (status != 0) throw Failure(t("%@ 的 Key 不对", p.title))
                    miniMaxPresets.size
                }
                Provider.Azure -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    val list = JSONArray(get("https://${Prefs.string(Provider.REGION)}.tts.speech.microsoft.com/cognitiveservices/voices/list", "Ocp-Apim-Subscription-Key" to key))
                    val keep = JSONArray()
                    for (i in 0 until list.length()) {
                        val v = list.getJSONObject(i)
                        val locale = v.optString("Locale")
                        if (locale.startsWith("zh") || locale.startsWith("en") || locale.startsWith("ja")) {
                            keep.put(JSONArray().put(v.optString("ShortName")).put(v.optString("LocalName")).put(locale).put(v.optString("Gender").lowercase()))
                        }
                    }
                    Prefs.set(p.voicesName, keep.toString())
                    keep.length()
                }
                Provider.ElevenLabs -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
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
                Provider.Deepgram -> {
                    val key = p.apiKey ?: throw Failure(t("还没填 %@ 的 Key", p.title))
                    get("https://api.deepgram.com/v1/projects", "Authorization" to "Token $key")
                    deepgramVoices.size
                }
                Provider.VoiceStudio, Provider.Voicebox, Provider.MlxAudio, Provider.Speaches, Provider.Local -> {
                    val base = normalizedBaseV1(p.savedURL)
                    if (base.isEmpty()) throw Failure(t("还没填 %@ 的服务地址", p.title))
                    val root = base.removeSuffix("/v1")
                    val headers = p.apiKey?.let { arrayOf("Authorization" to "Bearer $it") } ?: emptyArray()
                    var reachable = false
                    val keep = JSONArray()
                    for (path in listOf("$base/audio/voices", "$base/voices", "$root/profiles")) {
                        val raw = runCatching { get(path, *headers) }.getOrNull() ?: continue
                        reachable = true
                        val arr = runCatching { JSONArray(raw) }.getOrNull()
                            ?: runCatching { JSONObject(raw) }.getOrNull()?.let { obj ->
                                obj.optJSONArray("voices") ?: obj.optJSONArray("data") ?: obj.optJSONArray("profiles")
                            }
                        if (arr != null && arr.length() > 0) {
                            for (i in 0 until arr.length()) {
                                val el = arr.get(i)
                                if (el is String && el.isNotEmpty()) {
                                    keep.put(JSONArray().put(el).put(el).put("").put("").put(""))
                                } else if (el is JSONObject) {
                                    val id = el.optString("voice_id").ifEmpty { el.optString("id").ifEmpty { el.optString("name") } }
                                    if (id.isEmpty()) continue
                                    val name = el.optString("name").ifEmpty { el.optString("title").ifEmpty { id } }
                                    keep.put(JSONArray().put(id).put(name).put("").put(el.optString("gender").lowercase()).put(""))
                                }
                            }
                            if (keep.length() > 0) break
                        }
                    }
                    val modelsRaw = runCatching { get("$base/models", *headers) }.getOrNull()
                    if (modelsRaw != null) {
                        reachable = true
                        if (Prefs.string(p.modelName).isEmpty()) {
                            val firstModel = runCatching { JSONObject(modelsRaw).optJSONArray("data")?.optJSONObject(0)?.optString("id") }.getOrNull().orEmpty()
                            if (firstModel.isNotEmpty()) Prefs.set(p.modelName, firstModel)
                        }
                    }
                    if (!reachable) throw Failure(t("连不上 %@，请确认服务已启动", p.title))
                    Prefs.set(p.voicesName, keep.toString())
                    maxOf(keep.length(), defaultLocalVoices.size)
                }
            }
        }
    }
}
