package app.yuyue.speech

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.design.Prefs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.time.LocalDate

/**
 * 每日一言 for voice auditioning:
 * - Chinese books pull once per day from Hitokoto (文学 / 诗词 / 哲学), cached in [Prefs],
 *   with curated literary lines ready immediately offline.
 * - English books rotate curated literary classics by date, with manual refresh cycling cleanly.
 */
object DailyQuote {
    data class Entry(
        val text: String,
        val source: String = "",
    )

    private const val HITOKOTO_URL = "https://v1.hitokoto.cn/?c=d&c=i&c=k&max_length=46"
    private val genericSources = setOf("原创", "网络", "一言", "未知", "佚名", "网友", "其他", "自创")

    private val builtinZh = listOf(
        Entry("江上之清风，与山间之明月，耳得之而为声，目遇之而成色。", "苏轼《赤壁赋》"),
        Entry("庭下如积水空明，水中藻、荇交横，盖竹柏影也。", "苏轼《记承天寺夜游》"),
        Entry("行到水穷处，坐看云起时。偶然值林叟，谈笑无还期。", "王维《终南别业》"),
        Entry("醉后不知天在水，满船清梦压星河。", "唐温如《题龙阳县青草湖》"),
        Entry("落霞与孤鹜齐飞，秋水共长天一色。", "王勃《滕王阁序》"),
        Entry("赌书消得泼茶香，当时只道是寻常。", "纳兰性德《浣溪沙》"),
        Entry("我见青山多妩媚，料青山见我应如是。", "辛弃疾《贺新郎》"),
        Entry("一个人只拥有此生此世是不够的，他还应该拥有诗意的世界。", "王小波《万寿寺》"),
        Entry("太阳，它每时每刻都是夕阳也都是旭日。", "史铁生《我与地坛》"),
        Entry("溪流如弓背，山路如弓弦，故远近有了小小差异。", "沈从文《边城》"),
        Entry("花开了，就像睡醒了似的。鸟飞了，就像在天上逛似的。", "萧红《呼兰河传》"),
        Entry("月光如流水一般，静静地泻在这一片叶子和花上。", "朱自清《荷塘月色》"),
        Entry("最妙的是下点小雪呀。看吧，山上的矮松越发的青黑，树尖上顶着一髻儿白花。", "老舍《济南的冬天》"),
        Entry("雾凇沆砀，天与云与山与水，上下一白。", "张岱《湖心亭看雪》"),
    )

    private val builtinEn = listOf(
        Entry("I declare after all there is no enjoyment like reading!", "Jane Austen · Pride and Prejudice"),
        Entry("Whatever our souls are made of, his and mine are the same.", "Emily Brontë · Wuthering Heights"),
        Entry("I am no bird; and no net ensnares me: I am a free human being with an independent will.", "Charlotte Brontë · Jane Eyre"),
        Entry("The world is full of obvious things which nobody by any chance ever observes.", "Arthur Conan Doyle"),
        Entry("We are all in the gutter, but some of us are looking at the stars.", "Oscar Wilde"),
        Entry("To live is the rarest thing in the world. Most people exist, that is all.", "Oscar Wilde"),
        Entry("Only that day dawns to which we are awake. There is more day to dawn.", "Henry David Thoreau · Walden"),
        Entry("I went to the woods because I wished to live deliberately.", "Henry David Thoreau · Walden"),
        Entry("So we beat on, boats against the current, borne back ceaselessly into the past.", "F. Scott Fitzgerald · The Great Gatsby"),
        Entry("Mrs. Dalloway said she would buy the flowers herself.", "Virginia Woolf · Mrs Dalloway"),
        Entry("A room without books is like a body without a soul.", "Cicero"),
        Entry("Hope is the thing with feathers that perches in the soul, and sings the tune without the words.", "Emily Dickinson"),
        Entry("You have power over your mind — not outside events. Realize this, and you will find strength.", "Marcus Aurelius · Meditations"),
        Entry("It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife.", "Jane Austen · Pride and Prejudice"),
    )

    private val entries = mutableStateMapOf<String, Entry>()
    private val offsets = mutableMapOf<String, Int>()
    var refreshing by mutableStateOf(false)
        private set

    private fun norm(tag: String): String = if (tag.lowercase().startsWith("en")) "en" else "zh"

    private fun pool(lang: String): List<Entry> = if (lang == "en") builtinEn else builtinZh

    private fun builtinForToday(lang: String, offset: Int = 0): Entry {
        val list = pool(lang)
        val day = LocalDate.now().toEpochDay()
        val idx = Math.floorMod(day + offset.toLong(), list.size.toLong()).toInt()
        return list[idx]
    }

    fun current(tag: String): Entry {
        val lang = norm(tag)
        val today = LocalDate.now().toString()
        entries[lang]?.let { cached ->
            if (!Prefs.isReady || Prefs.string("voice.daily.$lang.date") == today || offsets.containsKey(lang)) {
                return cached
            }
        }
        if (Prefs.isReady && Prefs.string("voice.daily.$lang.date") == today) {
            val savedText = Prefs.string("voice.daily.$lang.text").trim()
            if (savedText.isNotEmpty()) {
                val saved = Entry(savedText, Prefs.string("voice.daily.$lang.source"))
                entries[lang] = saved
                return saved
            }
        }
        val fallback = builtinForToday(lang, offsets[lang] ?: 0)
        entries[lang] = fallback
        return fallback
    }

    /** Pulls today's online Hitokoto quote once per calendar day if not yet cached. */
    suspend fun ensureToday(tag: String, canUpdateUi: () -> Boolean = { true }) {
        val lang = norm(tag)
        current(lang)
        if (lang != "zh" || !Prefs.isReady) return
        val today = LocalDate.now().toString()
        if (Prefs.string("voice.daily.zh.date") == today && Prefs.bool("voice.daily.zh.online", false)) return
        val fetched = withContext(Dispatchers.IO) { fetchHitokoto(timeoutMs = 3000) } ?: return
        save("zh", today, fetched, online = true)
        if (canUpdateUi()) {
            entries["zh"] = fetched
        }
    }

    /** Picks a fresh quote on user tap ("换一句"): online Hitokoto first for Chinese, offline literary pool fallback. */
    suspend fun refresh(tag: String): Entry {
        val lang = norm(tag)
        val previous = current(lang).text
        refreshing = true
        return try {
            val today = LocalDate.now().toString()
            var next: Entry? = null
            var online = false
            if (lang == "zh") {
                next = withContext(Dispatchers.IO) {
                    fetchHitokoto(timeoutMs = 1800, avoid = previous)
                        ?: fetchHitokoto(timeoutMs = 1500, avoid = previous)
                }
                online = next != null
            }
            val chosen = next ?: nextBuiltin(lang, avoid = previous)
            save(lang, today, chosen, online = online)
            entries[lang] = chosen
            chosen
        } finally {
            refreshing = false
        }
    }

    private fun nextBuiltin(lang: String, avoid: String): Entry {
        val list = pool(lang)
        var step = (offsets[lang] ?: 0) + 1
        var candidate = builtinForToday(lang, step)
        if (candidate.text == avoid && list.size > 1) {
            step += 1
            candidate = builtinForToday(lang, step)
        }
        offsets[lang] = step
        return candidate
    }

    private fun save(lang: String, today: String, entry: Entry, online: Boolean) {
        if (!Prefs.isReady) return
        Prefs.set("voice.daily.$lang.date", today)
        Prefs.set("voice.daily.$lang.text", entry.text)
        Prefs.set("voice.daily.$lang.source", entry.source)
        Prefs.set("voice.daily.$lang.online", online)
    }

    private fun fetchHitokoto(timeoutMs: Int, avoid: String? = null): Entry? = runCatching {
        val conn = (URL(HITOKOTO_URL).openConnection() as HttpURLConnection).apply {
            requestMethod = "GET"
            connectTimeout = timeoutMs
            readTimeout = timeoutMs
            setRequestProperty("Accept", "application/json")
            setRequestProperty("User-Agent", "Yuyue/0.1")
        }
        try {
            if (conn.responseCode != 200) return null
            val body = conn.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
            val json = JSONObject(body)
            val text = json.optString("hitokoto").trim()
            if (text.length !in 4..80 || text.contains("我冒了严寒")) return null
            if (avoid != null && text == avoid) return null
            val who = if (json.isNull("from_who")) null else json.optString("from_who")
            val from = if (json.isNull("from")) null else json.optString("from")
            Entry(text = text, source = formatSource(who, from))
        } finally {
            conn.disconnect()
        }
    }.getOrNull()

    internal fun formatSource(rawWho: String?, rawFrom: String?): String {
        val who = rawWho?.trim().orEmpty().takeUnless { it.isEmpty() || it == "null" || it in genericSources }.orEmpty()
        val from = rawFrom?.trim().orEmpty().takeUnless { it.isEmpty() || it == "null" || it in genericSources }.orEmpty()
        val wrappedFrom = when {
            from.isEmpty() -> ""
            from.startsWith("《") || from.contains("·") || from.all { it.code < 128 } -> from
            else -> "《$from》"
        }
        return when {
            who.isNotEmpty() && wrappedFrom.isNotEmpty() -> if (who == from) who else "$who$wrappedFrom"
            who.isNotEmpty() -> who
            else -> wrappedFrom
        }
    }
}
