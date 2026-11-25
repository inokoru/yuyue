package app.yuyue.companion

import app.yuyue.L10n
import app.yuyue.R
import app.yuyue.design.Prefs
import app.yuyue.library.Keys
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.util.concurrent.TimeUnit

/** Where 书伴's answers come from, as on Apple, less Apple's on-device model:
 *  a service the reader added with their own key, or a model of their own (Ollama). No service
 *  of ours sits in between; keys stay on this device, encrypted, and are not synced.
 *  添加服务商 picks from [catalog], grouped by how it's reached
 *  ([Group]), or adds one of the reader's own (any OpenAI-shaped address). The logos are in
 *  shared/provider-icons (scripts/provider-icons.py). */
data class CompanionProvider(
    val id: String,
    val title: String,
    val anthropic: Boolean = false,
    val group: Group = Group.Key,
    /** Who the bill goes to, for the key form. */
    val account: String = "",
    val defaultModel: String = "",
    /** Lighter/cheaper model for bulk fast-tier tasks (e.g. scene attribution, homophone polish). */
    val defaultFastModel: String = "",
    val defaultURL: String? = null,
    val keyHint: String = "sk-…",
    /** What else it's searched by: its company, its Chinese name. */
    val aliases: String = "",
    /** A line under its name in 添加服务商. */
    val blurb: String? = null,
    /** Sent when no key is filled in: magpie's gateway takes any. */
    val defaultKey: String = "",
) {
    /** Its group in 添加服务商: how it's reached. */
    enum class Group {
        /** Signed in to on its own page (OpenRouter's sign-in for apps); a key can still be pasted. */
        SignIn,
        Key,
        /** A model or gateway on a computer on the network: Ollama, LM Studio, magpie. */
        Local;

        val title: String get() = when (this) { SignIn -> t("登录账号"); Key -> "API Key"; Local -> t("本机和局域网") }
    }

    val local: Boolean get() = group == Group.Local
    val isPaidCloud: Boolean get() = group != Group.Local
    val isCustom: Boolean get() = id.startsWith("custom.")

    /** Local models and the reader's own addresses may go without. */
    val needsKey: Boolean get() = !local && !isCustom
    val modelHint: String get() = defaultModel.ifEmpty { t("比如 qwen3:8b") }
    val urlHint: String get() = defaultURL ?: when {
        id == "magpie" -> t("比如 http://192.168.1.5:3425/v1")
        local -> t("比如 http://192.168.1.5:11434/v1")
        else -> "https://…/v1"
    }

    /** Its logo (res/drawable/provider_<id>.xml), and whether it takes the text colour. */
    val logo: Int? get() = LOGOS[id]
    val logoTinted: Boolean get() = id in TINTED

    /** The first letter, on its little square. */
    val mark: String get() = title.take(1).uppercase()

    companion object {
        /** What 添加服务商 offers, in its order. The OpenAI-shaped ones differ only in address. On a
         *  phone "localhost" is the phone, so Ollama's address is left to fill. */
        val catalog: List<CompanionProvider> get() = listOf(
            CompanionProvider("openrouter", "OpenRouter", group = Group.SignIn, account = "OpenRouter", defaultModel = "openrouter/auto",
                defaultURL = "https://openrouter.ai/api/v1/", keyHint = "sk-or-…", blurb = t("登录就能用，几百个模型一个账号")),
            CompanionProvider("claude", "Claude", anthropic = true, account = "Anthropic", defaultModel = "claude-sonnet-4-5", defaultFastModel = "claude-haiku-4-5",
                defaultURL = "https://api.anthropic.com/v1/", keyHint = "sk-ant-…", aliases = "anthropic 克劳德"),
            CompanionProvider("openai", "OpenAI", account = "OpenAI", defaultModel = "gpt-5-mini", defaultFastModel = "gpt-5-mini",
                defaultURL = "https://api.openai.com/v1/", aliases = "chatgpt gpt"),
            CompanionProvider("gemini", "Gemini", account = "Google", defaultModel = "gemini-2.5-flash", defaultFastModel = "gemini-2.5-flash",
                defaultURL = "https://generativelanguage.googleapis.com/v1beta/openai/", keyHint = "AIza…", aliases = "google 谷歌"),
            CompanionProvider("deepseek", "DeepSeek", account = "DeepSeek", defaultModel = "deepseek-chat", defaultFastModel = "deepseek-chat",
                defaultURL = "https://api.deepseek.com/v1/", aliases = "深度求索"),
            CompanionProvider("dashscope", t("通义千问"), account = t("阿里云百炼"), defaultModel = "qwen-plus", defaultFastModel = "qwen-turbo",
                defaultURL = "https://dashscope.aliyuncs.com/compatible-mode/v1/", aliases = "qwen 阿里 百炼 dashscope"),
            CompanionProvider("moonshot", "Kimi", account = t("月之暗面"), defaultModel = "kimi-k2-turbo-preview", defaultFastModel = "kimi-k2-turbo-preview",
                defaultURL = "https://api.moonshot.cn/v1/", aliases = "moonshot 月之暗面"),
            CompanionProvider("zhipu", t("智谱 GLM"), account = t("智谱"), defaultModel = "glm-4.6", defaultFastModel = "glm-4-flash",
                defaultURL = "https://open.bigmodel.cn/api/paas/v4/", aliases = "zhipu glm z.ai 清言"),
            CompanionProvider("volcengine", t("豆包"), account = t("火山引擎"), defaultModel = "doubao-seed-1-6-250615", defaultFastModel = "doubao-seed-1-6-flash-250615",
                defaultURL = "https://ark.cn-beijing.volces.com/api/v3/", aliases = "doubao 火山引擎 volcengine 字节"),
            CompanionProvider("siliconflow", t("硅基流动"), account = t("硅基流动"), defaultModel = "deepseek-ai/DeepSeek-V3", defaultFastModel = "deepseek-ai/DeepSeek-V3",
                defaultURL = "https://api.siliconflow.cn/v1/", aliases = "siliconflow"),
            CompanionProvider("minimax", "MiniMax", account = "MiniMax", defaultModel = "MiniMax-M2", defaultFastModel = "MiniMax-M2",
                defaultURL = "https://api.minimaxi.com/v1/", aliases = "海螺 稀宇"),
            CompanionProvider("xai", "Grok", account = "xAI", defaultModel = "grok-4", defaultFastModel = "grok-3-mini", defaultURL = "https://api.x.ai/v1/", keyHint = "xai-…", aliases = "xai"),
            CompanionProvider("mistral", "Mistral", account = "Mistral", defaultModel = "mistral-small-latest", defaultFastModel = "mistral-small-latest", defaultURL = "https://api.mistral.ai/v1/", keyHint = ""),
            CompanionProvider("groq", "Groq", account = "Groq", defaultModel = "llama-3.3-70b-versatile", defaultFastModel = "llama-3.1-8b-instant",
                defaultURL = "https://api.groq.com/openai/v1/", keyHint = "gsk_…"),
            CompanionProvider("magpie", "magpie", group = Group.Local, keyHint = t("局域网里用才要填网关 Key"), aliases = "喜鹊 网关 gateway",
                blurb = t("连接本机或局域网中的 magpie 模型网关"), defaultKey = "magpie"),
            CompanionProvider("ollama", "Ollama", group = Group.Local, keyHint = t("不用填")),
            CompanionProvider("lmstudio", "LM Studio", group = Group.Local, keyHint = t("不用填")),
        )

        private val LOGOS = mapOf(
            "claude" to R.drawable.provider_claude, "openai" to R.drawable.provider_openai, "gemini" to R.drawable.provider_gemini,
            "deepseek" to R.drawable.provider_deepseek, "dashscope" to R.drawable.provider_dashscope, "moonshot" to R.drawable.provider_moonshot,
            "zhipu" to R.drawable.provider_zhipu, "volcengine" to R.drawable.provider_volcengine, "siliconflow" to R.drawable.provider_siliconflow,
            "minimax" to R.drawable.provider_minimax, "openrouter" to R.drawable.provider_openrouter, "xai" to R.drawable.provider_xai,
            "mistral" to R.drawable.provider_mistral, "groq" to R.drawable.provider_groq, "ollama" to R.drawable.provider_ollama,
            "lmstudio" to R.drawable.provider_lmstudio, "magpie" to R.drawable.provider_magpie,
        )
        private val TINTED = setOf("openai", "moonshot", "openrouter", "xai", "groq", "ollama", "lmstudio", "magpie")

        /** One of the reader's own: any OpenAI-shaped address, under a name they give it. */
        fun custom(title: String, id: String = "custom." + java.util.UUID.randomUUID().toString().take(8)) =
            CompanionProvider(id, title, keyHint = t("可不填"))

        /** A catalog entry or one of the reader's own, by its id. */
        fun find(id: String): CompanionProvider? =
            catalog.firstOrNull { it.id == id }
                ?: Prefs.string("companion.name.$id").takeIf { id.startsWith("custom.") && it.isNotEmpty() }?.let { custom(it, id) }

        private val addedIds: List<String> get() = Prefs.string("companion.added").split(',').filter { it.isNotEmpty() }

        /** The services the reader added, in the order they were added. */
        val added: List<CompanionProvider> get() = addedIds.mapNotNull(::find)

        internal fun setAdded(ids: List<String>) = Prefs.set("companion.added", ids.joinToString(","))

        /** [catalog] less what's already added, matching what's typed in its name, id or aliases. */
        fun available(query: String): List<CompanionProvider> {
            val taken = addedIds.toSet()
            val q = query.lowercase().filterNot { it in " -_." }
            return catalog.filter { p -> p.id !in taken && (q.isEmpty() || "${p.id}${p.title}${p.aliases}".lowercase().filterNot { it == ' ' }.contains(q)) }
        }
    }
}

/** The chosen engine and how to reach it. */
data class CompanionConfig(val provider: CompanionProvider, val key: String, val model: String, val url: String, val fastModel: String = "") {
    /** Its key and address are in: enough to add it, and to list its models. */
    val isConnected: Boolean
        get() = (if (url.isEmpty()) provider.defaultURL != null else url.startsWith("http")) &&
            (!provider.needsKey || key.isNotEmpty()) && provider.title.isNotEmpty()

    /** Filled in enough to ask: connected, and a model chosen. */
    val isReady: Boolean get() = isConnected && model.isNotEmpty()

    /** What goes in the request: the key typed in, or the one the service takes without one. */
    val sentKey: String get() = key.ifEmpty { provider.defaultKey }

    /** "DeepSeek · deepseek-chat": under each answer. */
    val label: String get() = "${provider.title} · $model"

    /** "sk-ant-…3f9a": the saved key, shown without giving it away. */
    val maskedKey: String get() = if (key.length <= 8) "•".repeat(key.length) else (if (key.startsWith("sk-")) "sk-" else "") + "…" + key.takeLast(4)

    /** Lighter model for bulk fast-tier tasks (scene attribution, homophone polish), or null. */
    val resolvedFastModel: String?
        get() {
            val manual = fastModel.trim()
            if (manual.isNotEmpty()) return manual.takeIf { it != model }
            val lower = model.lowercase()
            if (listOf("haiku", "mini", "nano", "flash", "turbo", "small", "instant", "lite").any { it in lower }) return null
            if (provider.id == "openrouter") {
                return when {
                    lower.startsWith("anthropic/claude-") -> "anthropic/claude-haiku-4-5"
                    lower.startsWith("openai/gpt-5") -> "openai/gpt-5-mini"
                    lower.startsWith("openai/gpt-4o") -> "openai/gpt-4o-mini"
                    lower.startsWith("google/gemini-2.5-pro") || lower.startsWith("google/gemini-pro") -> "google/gemini-2.5-flash"
                    lower.startsWith("deepseek/deepseek-r1") -> "deepseek/deepseek-chat"
                    else -> null
                }
            }
            return provider.defaultFastModel.takeIf { it.isNotEmpty() && it != model }
        }

    fun routed(fast: Boolean): CompanionConfig {
        if (!fast || !CompanionUsage.tierRoutingEnabled) return this
        val lighter = resolvedFastModel ?: return this
        return copy(model = lighter)
    }

    /** Adds the service, or keeps the changes to one added before. */
    fun save() {
        val id = provider.id
        if (provider.isCustom) Prefs.set("companion.name.$id", provider.title)
        Prefs.set("companion.model.$id", model)
        Prefs.set("companion.fastModel.$id", fastModel.ifEmpty { null })
        Prefs.set("companion.url.$id", url)
        if (key.isEmpty()) Keys.delete("companion.key.$id") else Keys.write("companion.key.$id", key)
        val ids = CompanionProvider.added.map { it.id }
        if (id !in ids) CompanionProvider.setAdded(ids + id)
    }

    /** Saves it and makes it the one that answers: a service just added answers from now on. */
    fun choose() {
        save()
        Prefs.set("companion.provider", provider.id)
    }

    companion object {
        /** The one answering: the chosen one, else the first added. Null before any is added. */
        val current: CompanionConfig?
            get() {
                val added = CompanionProvider.added
                return (added.firstOrNull { it.id == Prefs.string("companion.provider") } ?: added.firstOrNull())?.let(::saved)
            }

        /** What was filled in for a provider before, or its defaults. */
        fun saved(p: CompanionProvider) = CompanionConfig(
            p, Keys.read("companion.key.${p.id}") ?: "",
            Prefs.string("companion.model.${p.id}").ifEmpty { p.defaultModel },
            Prefs.string("companion.url.${p.id}"),
            Prefs.string("companion.fastModel.${p.id}"),
        )

        /** Forgets the service and its key. */
        fun remove(p: CompanionProvider) {
            CompanionProvider.setAdded(CompanionProvider.added.map { it.id }.filter { it != p.id })
            for (k in listOf("model", "fastModel", "url", "name")) Prefs.set("companion.$k.${p.id}", null)
            Keys.delete("companion.key.${p.id}")
            if (Prefs.string("companion.provider") == p.id) Prefs.set("companion.provider", null)
        }
    }
}

class CompanionError(message: String, val httpCode: Int = 0) : Exception(message)

/** Tracks real 30-day LLM token & Cloud TTS character usage, cache hit breakdown, source
 *  attribution, and daily budget limits so BYOK costs stay transparent and bounded. */
object CompanionUsage {
    enum class Source(val key: String) {
        Drama("drama"),
        Chat("companion"),
        Polish("polish"),
        Recap("recap"),
        Podcast("podcast"),
        Other("other");

        val title: String get() = when (this) {
            Drama -> t("广播剧认人")
            Chat -> t("书伴问答")
            Polish -> t("错字整理")
            Recap -> t("前情提要")
            Podcast -> t("播客")
            Other -> t("其他")
        }

        companion object {
            fun fromKey(k: String): Source = entries.firstOrNull { it.key == k } ?: Other
        }
    }

    data class DayRecord(
        val date: String,
        val cachedInput: Int = 0,
        val missInput: Int = 0,
        val output: Int = 0,
        val requests: Int = 0,
        val sources: Map<String, Int> = emptyMap(),
        val ttsChars: Int = 0,
        val ttsSavedChars: Int = 0,
    ) {
        val totalTokens: Int get() = cachedInput + missInput + output
        val totalInput: Int get() = cachedInput + missInput
        val shortLabel: String get() {
            val parts = date.split('-')
            if (parts.size != 3) return date
            return "${parts[1].toIntOrNull() ?: parts[1]}/${parts[2].toIntOrNull() ?: parts[2]}"
        }
    }

    data class SourceShare(val source: Source, val tokens: Int, val share: Double)

    data class Snapshot30Days(
        val days: List<DayRecord>,
        val totalTokens: Int,
        val cachedInputTokens: Int,
        val missInputTokens: Int,
        val outputTokens: Int,
        val peakTokens: Int,
        val peakIndex: Int,
        val averageTokens: Double,
        val sources: List<SourceShare>,
        val ttsChars: Int,
        val ttsSavedChars: Int,
    ) {
        val cacheHitRate: Double? get() {
            val totalIn = cachedInputTokens + missInputTokens
            return if (totalIn > 0) cachedInputTokens.toDouble() / totalIn else null
        }
    }

    private val lock = Any()
    private const val K_LEDGER = "usage.days30.v1"
    private const val K_TIER_ROUTING = "companion.cost.tierRouting"
    private const val K_DAILY_TOKEN_LIMIT = "companion.cost.dailyTokenLimit"
    private const val K_DAILY_TTS_LIMIT = "companion.cost.dailyTtsCharLimit"
    private const val K_CLOUD_SAVE_NARRATOR = "drama.cost.cloudSaveNarrator"

    var tierRoutingEnabled: Boolean
        get() = Prefs.bool(K_TIER_ROUTING, true)
        set(v) = Prefs.set(K_TIER_ROUTING, v)

    var dailyTokenLimit: Int
        get() = Prefs.shared.getInt(K_DAILY_TOKEN_LIMIT, 0)
        set(v) { Prefs.shared.edit().putInt(K_DAILY_TOKEN_LIMIT, v.coerceAtLeast(0)).apply() }

    var dailyTtsCharLimit: Int
        get() = Prefs.shared.getInt(K_DAILY_TTS_LIMIT, 0)
        set(v) { Prefs.shared.edit().putInt(K_DAILY_TTS_LIMIT, v.coerceAtLeast(0)).apply() }

    var cloudSaveNarrator: Boolean
        get() = Prefs.bool(K_CLOUD_SAVE_NARRATOR, true)
        set(v) = Prefs.set(K_CLOUD_SAVE_NARRATOR, v)

    private fun dateKey(cal: java.util.Calendar = java.util.Calendar.getInstance()): String =
        "%04d-%02d-%02d".format(java.util.Locale.ROOT, cal.get(java.util.Calendar.YEAR), cal.get(java.util.Calendar.MONTH) + 1, cal.get(java.util.Calendar.DAY_OF_MONTH))

    private fun last30DateKeys(): List<String> {
        val out = ArrayList<String>(30)
        for (offset in -29..0) {
            val c = java.util.Calendar.getInstance()
            c.add(java.util.Calendar.DAY_OF_MONTH, offset)
            out.add(dateKey(c))
        }
        return out
    }

    private fun loadLedgerLocked(): MutableMap<String, DayRecord> {
        val raw = Prefs.string(K_LEDGER)
        if (raw.isEmpty()) return HashMap()
        val map = HashMap<String, DayRecord>()
        runCatching {
            val arr = JSONArray(raw)
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                val date = o.optString("date")
                if (date.isEmpty()) continue
                val srcObj = o.optJSONObject("sources")
                val src = HashMap<String, Int>()
                if (srcObj != null) {
                    val keys = srcObj.keys()
                    while (keys.hasNext()) {
                        val k = keys.next()
                        src[k] = srcObj.optInt(k)
                    }
                }
                map[date] = DayRecord(
                    date = date,
                    cachedInput = o.optInt("cachedInput"),
                    missInput = o.optInt("missInput"),
                    output = o.optInt("output"),
                    requests = o.optInt("requests"),
                    sources = src,
                    ttsChars = o.optInt("ttsChars"),
                    ttsSavedChars = o.optInt("ttsSavedChars"),
                )
            }
        }
        return map
    }

    private fun saveLedgerLocked(map: Map<String, DayRecord>) {
        val keep = last30DateKeys().toHashSet()
        val arr = JSONArray()
        for (rec in map.values.filter { it.date in keep }.sortedBy { it.date }) {
            val src = JSONObject()
            for ((k, v) in rec.sources) if (v > 0) src.put(k, v)
            arr.put(JSONObject()
                .put("date", rec.date)
                .put("cachedInput", rec.cachedInput)
                .put("missInput", rec.missInput)
                .put("output", rec.output)
                .put("requests", rec.requests)
                .put("sources", src)
                .put("ttsChars", rec.ttsChars)
                .put("ttsSavedChars", rec.ttsSavedChars))
        }
        Prefs.set(K_LEDGER, arr.toString())
    }

    fun checkLlmBudget(config: CompanionConfig) {
        if (!config.provider.isPaidCloud) return
        val limit = dailyTokenLimit
        if (limit <= 0) return
        val today = dateKey()
        val used = synchronized(lock) { loadLedgerLocked()[today]?.totalTokens ?: 0 }
        if (used >= limit) {
            throw CompanionError(t("已达到今日用量上限（%@ token），已自动暂停。可在「设置 · 书伴」中调整。", formatCount(limit)))
        }
    }

    fun recordLlm(input: Int, output: Int, cached: Int, paid: Boolean, source: Source = Source.Other) {
        if (!paid || (input <= 0 && output <= 0 && cached <= 0)) return
        val today = dateKey()
        synchronized(lock) {
            val map = loadLedgerLocked()
            val cur = map[today] ?: DayRecord(today)
            val c = cached.coerceAtLeast(0)
            val m = input.coerceAtLeast(0)
            val o = output.coerceAtLeast(0)
            val nextSources = cur.sources.toMutableMap()
            nextSources[source.key] = (nextSources[source.key] ?: 0) + c + m + o
            map[today] = cur.copy(
                cachedInput = cur.cachedInput + c,
                missInput = cur.missInput + m,
                output = cur.output + o,
                requests = cur.requests + 1,
                sources = nextSources,
            )
            saveLedgerLocked(map)
        }
    }

    fun checkTtsBudget(chars: Int) {
        val limit = dailyTtsCharLimit
        if (limit <= 0) return
        val today = dateKey()
        val used = synchronized(lock) { loadLedgerLocked()[today]?.ttsChars ?: 0 }
        if (used + chars > limit) {
            throw app.yuyue.speech.CloudVoice.Failure(t("已达到今日云端朗读上限（%@字），已自动暂停。可在「设置 · 书伴」中调整。", formatCount(limit)))
        }
    }

    fun recordTts(chars: Int, cached: Boolean) {
        if (chars <= 0) return
        val today = dateKey()
        synchronized(lock) {
            val map = loadLedgerLocked()
            val cur = map[today] ?: DayRecord(today)
            map[today] = if (cached) cur.copy(ttsSavedChars = cur.ttsSavedChars + chars) else cur.copy(ttsChars = cur.ttsChars + chars)
            saveLedgerLocked(map)
        }
    }

    fun snapshot30Days(): Snapshot30Days {
        val keys = last30DateKeys()
        val map = synchronized(lock) { loadLedgerLocked() }
        val days = keys.map { map[it] ?: DayRecord(it) }
        var total = 0; var cached = 0; var miss = 0; var out = 0; var tts = 0; var ttsSaved = 0
        var peak = 0; var peakIndex = (days.size - 1).coerceAtLeast(0)
        val sourceTotals = HashMap<Source, Int>()
        days.forEachIndexed { idx, d ->
            val t = d.totalTokens
            total += t; cached += d.cachedInput; miss += d.missInput; out += d.output
            tts += d.ttsChars; ttsSaved += d.ttsSavedChars
            if (t > peak) { peak = t; peakIndex = idx }
            for ((k, v) in d.sources) if (v > 0) {
                val s = Source.fromKey(k)
                sourceTotals[s] = (sourceTotals[s] ?: 0) + v
            }
        }
        val shares = if (total > 0) sourceTotals.entries.map { SourceShare(it.key, it.value, it.value.toDouble() / total) }.sortedByDescending { it.tokens } else emptyList()
        return Snapshot30Days(days, total, cached, miss, out, peak, peakIndex, total.toDouble() / days.size.coerceAtLeast(1), shares, tts, ttsSaved)
    }

    fun formatCount(n: Int): String = when {
        n >= 10_000 -> {
            val wan = n / 10_000.0
            if (wan >= 100) "%.0f 万".format(java.util.Locale.ROOT, wan)
            else "%.1f".format(java.util.Locale.ROOT, wan).removeSuffix(".0") + " 万"
        }
        n >= 1_000 -> "%.1f".format(java.util.Locale.ROOT, n / 1_000.0).removeSuffix(".0") + "K"
        else -> "$n"
    }
}

object Companion {
    /** The language the companion answers in: the interface's. The prompts stay Chinese, and so
     *  do the markers in them (没读到, 接着问) that the replies are read back by. */
    val replyLanguage: String get() = if (L10n.chinese) "简体中文" else "英文（English）"

    private val http = OkHttpClient.Builder().connectTimeout(20, TimeUnit.SECONDS).readTimeout(120, TimeUnit.SECONDS).build()
    private val json = "application/json".toMediaType()

    /** One answer: `system` says how to behave, `prompt` carries the excerpts and the question.
     *  `cachePrefixLen` is the UTF-8 byte length of the stable leading prefix in `prompt`.
     *  `fast` routes to the provider's lighter model when tier routing is enabled (with fallback). */
    suspend fun answer(
        config: CompanionConfig,
        system: String,
        prompt: String,
        maxTokens: Int = 1024,
        cachePrefixLen: Int = 0,
        fast: Boolean = false,
        source: CompanionUsage.Source = CompanionUsage.Source.Other,
        onStream: ((String) -> Unit)? = null,
    ): String = withContext(Dispatchers.IO) {
        if (!config.isReady) throw CompanionError(t("书伴还没接上：在设置 · 书伴里添加一个服务商"))
        CompanionUsage.checkLlmBudget(config)
        val effective = config.routed(fast)
        val cancelHook = kotlinx.coroutines.currentCoroutineContext()[kotlinx.coroutines.Job]
        try {
            perform(effective, system, prompt, maxTokens, cachePrefixLen, source, onStream, cancelHook)
        } catch (e: CompanionError) {
            if (effective.model != config.model && (e.httpCode == 400 || e.httpCode == 404)) {
                perform(config, system, prompt, maxTokens, cachePrefixLen, source, onStream, cancelHook)
            } else {
                throw e
            }
        }
    }

    /** Splits `text` at UTF-8 byte offset `byteLen`, snapping cleanly to a UTF-8 character boundary. */
    private fun splitUTF8Prefix(text: String, byteLen: Int): Pair<String, String>? {
        if (byteLen <= 0) return null
        val bytes = text.toByteArray(Charsets.UTF_8)
        if (byteLen >= bytes.size) return null
        var cut = byteLen
        // Step back if landing on a UTF-8 continuation byte (10xxxxxx).
        while (cut > 0 && (bytes[cut].toInt() and 0xC0) == 0x80) cut--
        if (cut <= 0 || cut >= bytes.size) return null
        return String(bytes, 0, cut, Charsets.UTF_8) to String(bytes, cut, bytes.size - cut, Charsets.UTF_8)
    }

    private fun estimateTokens(text: String): Int {
        var cjk = 0
        var other = 0
        for (ch in text) if (ch.code >= 0x2E80) cjk++ else other++
        return maxOf(1, (cjk * 0.72 + other * 0.28).toInt())
    }

    private fun perform(
        config: CompanionConfig,
        system: String,
        prompt: String,
        maxTokens: Int,
        cachePrefixLen: Int,
        source: CompanionUsage.Source,
        onStream: ((String) -> Unit)?,
        job: kotlinx.coroutines.Job?,
    ): String {
        val base = base(config)
        val shouldStream = onStream != null || maxTokens > 2048
        val text = if (config.provider.anthropic) {
            val useCache = cachePrefixLen > 0 || system.toByteArray(Charsets.UTF_8).size >= 1024
            val body = JSONObject().put("model", config.model).put("max_tokens", maxTokens).put("stream", shouldStream)
            if (useCache) {
                body.put("system", JSONArray().put(
                    JSONObject().put("type", "text").put("text", system)
                        .put("cache_control", JSONObject().put("type", "ephemeral"))
                ))
            } else {
                body.put("system", system)
            }
            val split = splitUTF8Prefix(prompt, cachePrefixLen)
            val userContent: Any = if (split != null) {
                JSONArray()
                    .put(JSONObject().put("type", "text").put("text", split.first).put("cache_control", JSONObject().put("type", "ephemeral")))
                    .put(JSONObject().put("type", "text").put("text", split.second))
            } else {
                prompt
            }
            body.put("messages", JSONArray().put(JSONObject().put("role", "user").put("content", userContent)))
            val r = Request.Builder().url(base + "messages").post(body.toString().toRequestBody(json))
                .header("x-api-key", config.sentKey).header("anthropic-version", "2023-06-01").build()
            if (shouldStream) {
                var inTok = 0
                var outTok = 0
                var cachedTok = 0
                val reply = stream(r, long = maxTokens > 1024, onStream = onStream, job = job) { event ->
                    val kind = event.optString("type")
                    if (kind == "message_start") {
                        event.optJSONObject("message")?.optJSONObject("usage")?.let { u ->
                            inTok = u.optInt("input_tokens") + u.optInt("cache_creation_input_tokens")
                            cachedTok = u.optInt("cache_read_input_tokens")
                        }
                    } else if (kind == "message_delta") {
                        event.optJSONObject("usage")?.let { u ->
                            val o = u.optInt("output_tokens")
                            if (o > 0) outTok = o
                        }
                    }
                    if (kind == "content_block_delta") event.optJSONObject("delta")?.optString("text")?.takeIf { it.isNotEmpty() } else null
                }
                if (inTok == 0 && outTok == 0) {
                    inTok = estimateTokens(system + prompt)
                    outTok = estimateTokens(reply)
                }
                CompanionUsage.recordLlm(inTok, outTok, cachedTok, config.provider.isPaidCloud, source)
                reply
            } else {
                val out = send(r, long = maxTokens > 1024)
                val blocks = out.optJSONArray("content") ?: JSONArray()
                val reply = (0 until blocks.length()).joinToString("") { blocks.getJSONObject(it).optString("text") }
                val u = out.optJSONObject("usage")
                if (u != null) {
                    val inTok = u.optInt("input_tokens") + u.optInt("cache_creation_input_tokens")
                    val outTok = u.optInt("output_tokens")
                    val cachedTok = u.optInt("cache_read_input_tokens")
                    CompanionUsage.recordLlm(inTok, outTok, cachedTok, config.provider.isPaidCloud, source)
                } else {
                    CompanionUsage.recordLlm(estimateTokens(system + prompt), estimateTokens(reply), 0, config.provider.isPaidCloud, source)
                }
                reply
            }
        } else {
            // OpenAI, DeepSeek, Gemini (its OpenAI-shaped endpoint), Ollama and the rest.
            val isOpenRouterClaude = config.provider.id == "openrouter" && config.model.lowercase().startsWith("anthropic/")
            val split = if (isOpenRouterClaude) splitUTF8Prefix(prompt, cachePrefixLen) else null
            val messages = if (split != null) {
                JSONArray()
                    .put(JSONObject().put("role", "system").put("content", JSONArray().put(
                        JSONObject().put("type", "text").put("text", system).put("cache_control", JSONObject().put("type", "ephemeral"))
                    )))
                    .put(JSONObject().put("role", "user").put("content", JSONArray()
                        .put(JSONObject().put("type", "text").put("text", split.first).put("cache_control", JSONObject().put("type", "ephemeral")))
                        .put(JSONObject().put("type", "text").put("text", split.second))
                    ))
            } else {
                JSONArray()
                    .put(JSONObject().put("role", "system").put("content", system))
                    .put(JSONObject().put("role", "user").put("content", prompt))
            }
            val body = JSONObject().put("model", config.model).put("temperature", 0.3)
                .apply {
                    if (maxTokens > 1024) put("max_tokens", maxTokens)
                    if (shouldStream) {
                        put("stream", true)
                        if (config.provider.id in setOf("openai", "deepseek", "openrouter", "dashscope", "moonshot")) {
                            put("stream_options", JSONObject().put("include_usage", true))
                        }
                    }
                }
                .put("messages", messages)
            val b = Request.Builder().url(base + "chat/completions").post(body.toString().toRequestBody(json))
            if (config.sentKey.isNotEmpty()) b.header("Authorization", "Bearer ${config.sentKey}")
            if (shouldStream) {
                var inTok = 0
                var outTok = 0
                var cachedTok = 0
                val reply = stream(b.build(), long = maxTokens > 1024, onStream = onStream, job = job) { event ->
                    event.optJSONObject("usage")?.let { u ->
                        val promptTok = u.optInt("prompt_tokens", u.optInt("input_tokens"))
                        val compTok = u.optInt("completion_tokens", u.optInt("output_tokens"))
                        val cTok = u.optJSONObject("prompt_tokens_details")?.optInt("cached_tokens")
                            ?: u.optInt("prompt_cache_hit_tokens", u.optInt("cache_read_input_tokens"))
                        if (promptTok > 0) inTok = (promptTok - cTok).coerceAtLeast(0)
                        if (compTok > 0) outTok = compTok
                        if (cTok > 0) cachedTok = cTok
                    }
                    event.optJSONArray("choices")?.optJSONObject(0)?.optJSONObject("delta")?.optString("content")?.takeIf { it.isNotEmpty() }
                }
                if (inTok == 0 && outTok == 0) {
                    inTok = estimateTokens(system + prompt)
                    outTok = estimateTokens(reply)
                }
                CompanionUsage.recordLlm(inTok, outTok, cachedTok, config.provider.isPaidCloud, source)
                reply
            } else {
                val out = send(b.build(), long = maxTokens > 1024)
                val reply = out.optJSONArray("choices")?.optJSONObject(0)?.optJSONObject("message")?.optString("content") ?: ""
                val u = out.optJSONObject("usage")
                if (u != null) {
                    val promptTok = u.optInt("prompt_tokens", u.optInt("input_tokens"))
                    val compTok = u.optInt("completion_tokens", u.optInt("output_tokens"))
                    val cachedTok = u.optJSONObject("prompt_tokens_details")?.optInt("cached_tokens")
                        ?: u.optInt("prompt_cache_hit_tokens", u.optInt("cache_read_input_tokens"))
                    CompanionUsage.recordLlm((promptTok - cachedTok).coerceAtLeast(0), compTok, cachedTok, config.provider.isPaidCloud, source)
                } else {
                    CompanionUsage.recordLlm(estimateTokens(system + prompt), estimateTokens(reply), 0, config.provider.isPaidCloud, source)
                }
                reply
            }
        }
        return text.trim().ifEmpty { throw CompanionError(t("没有回答，换个问法试试")) }
    }

    /** The models the service lists (`GET …/models`, the same shape from Anthropic, OpenAI and the
     *  rest), for the 模型 menu. Ollama, LM Studio and magpie list what's there to use. */
    suspend fun models(config: CompanionConfig): List<String> = withContext(Dispatchers.IO) {
        val b = Request.Builder().url(base(config) + "models")
        if (config.provider.anthropic) b.header("x-api-key", config.sentKey).header("anthropic-version", "2023-06-01")
        else if (config.sentKey.isNotEmpty()) b.header("Authorization", "Bearer ${config.sentKey}")
        val raw = sendRaw(b.build())
        val trimmed = raw.trim()
        val list = if (trimmed.startsWith("[")) {
            runCatching { JSONArray(trimmed) }.getOrDefault(JSONArray())
        } else {
            val out = runCatching { JSONObject(trimmed) }.getOrDefault(JSONObject())
            out.optJSONArray("data") ?: out.optJSONArray("models") ?: JSONArray()
        }
        val seen = LinkedHashSet<String>()
        for (i in 0 until list.length()) {
            val item = list.opt(i)
            val id = when (item) {
                is JSONObject -> item.optString("id").ifEmpty { item.optString("name") }
                is String -> item
                else -> ""
            }.removePrefix("models/")
            if (id.isNotEmpty()) seen.add(id)
        }
        seen.toList()
    }

    private fun base(config: CompanionConfig) = (config.url.trim().ifEmpty { config.provider.defaultURL ?: "" }).let { if (it.endsWith("/")) it else "$it/" }

    private val slow by lazy { http.newBuilder().readTimeout(600, TimeUnit.SECONDS).build() }

    private fun send(r: Request, long: Boolean = false): JSONObject =
        runCatching { JSONObject(sendRaw(r, long)) }.getOrDefault(JSONObject())

    private fun stream(
        r: Request,
        long: Boolean = false,
        onStream: ((String) -> Unit)? = null,
        job: kotlinx.coroutines.Job? = null,
        pieceOf: (JSONObject) -> String?,
    ): String {
        val call = (if (long) slow else http).newCall(r)
        val handle = job?.invokeOnCompletion { cause -> if (cause != null) call.cancel() }
        try {
            call.execute().use { response ->
                if (!response.isSuccessful) {
                    val raw = response.body?.string() ?: ""
                    throwHttpError(response.code, raw)
                }
                val out = StringBuilder()
                val rawLines = ArrayList<String>()
                var sawSSE = false
                var lastEmitMs = 0L
                var emittedLen = 0
                response.body?.charStream()?.buffered()?.useLines { lines ->
                    for (line in lines) {
                        if (job?.isCancelled == true) throw kotlinx.coroutines.CancellationException()
                        if (!line.startsWith("data:")) {
                            if (!sawSSE && rawLines.size < 256) rawLines.add(line)
                            continue
                        }
                        sawSSE = true
                        val payload = line.substring(5).trim()
                        if (payload == "[DONE]") break
                        val event = runCatching { JSONObject(payload) }.getOrNull() ?: continue
                        val piece = pieceOf(event)
                        if (!piece.isNullOrEmpty()) {
                            out.append(piece)
                            if (onStream != null) {
                                val now = android.os.SystemClock.uptimeMillis()
                                if (emittedLen == 0 || now - lastEmitMs >= 35L) {
                                    lastEmitMs = now
                                    emittedLen = out.length
                                    onStream(out.toString())
                                }
                            }
                        }
                    }
                }
                if (out.isEmpty() && !sawSSE && rawLines.isNotEmpty()) {
                    runCatching { JSONObject(rawLines.joinToString("\n")) }.getOrNull()?.let { obj ->
                        val fallback = obj.optJSONArray("choices")?.optJSONObject(0)?.optJSONObject("message")?.optString("content")
                            ?.takeIf { it.isNotEmpty() }
                            ?: obj.optJSONArray("content")?.let { arr ->
                                (0 until arr.length()).joinToString("") { arr.optJSONObject(it)?.optString("text") ?: "" }
                            }
                        if (!fallback.isNullOrEmpty()) out.append(fallback)
                    }
                }
                val result = out.toString()
                if (onStream != null && result.length > emittedLen) onStream(result)
                return result
            }
        } catch (e: java.io.IOException) {
            if (job?.isCancelled == true) throw kotlinx.coroutines.CancellationException()
            throw e
        } finally {
            handle?.dispose()
        }
    }

    private fun sendRaw(r: Request, long: Boolean = false): String {
        (if (long) slow else http).newCall(r).execute().use { response ->
            val raw = response.body?.string() ?: ""
            if (response.isSuccessful) return raw
            throwHttpError(response.code, raw)
        }
    }

    private fun throwHttpError(code: Int, raw: String): Nothing {
        val out = runCatching { JSONObject(raw) }.getOrDefault(JSONObject())
        val error = out.optJSONObject("error")
        val message = error?.optString("message")?.ifEmpty { null } ?: out.optString("message").ifEmpty { null }
        val kind = "${error?.optString("type")} ${error?.optString("code")} $message".lowercase()
        throw CompanionError(when {
            code == 401 || code == 403 -> t("Key 不对，去设置 · 书伴里重新填")
            code == 402 -> t("账号余额不够了")
            code == 429 && ("quota" in kind || "credit" in kind || "balance" in kind) -> t("账号余额不够了")
            code == 429 || code == 529 -> t("问得太快了，等一会儿再问")
            code == 400 && ("credit" in kind || "balance" in kind) -> t("账号余额不够了")
            message != null -> t("服务器回了 %lld：%@", code, message)
            else -> t("服务器回了 %lld", code)
        }, httpCode = code)
    }

    fun message(e: Throwable): String = when (e) {
        is CompanionError -> e.message ?: t("出错了")
        is UnknownHostException, is ConnectException -> t("连不上服务器，看看接口地址")
        is SocketTimeoutException -> t("等太久没回答，再问一次")
        else -> e.localizedMessage ?: e.toString()
    }
}
