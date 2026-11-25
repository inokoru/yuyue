package app.yuyue.reader

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.pm.ResolveInfo
import android.speech.tts.TextToSpeech
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.companion.Companion
import app.yuyue.companion.CompanionConfig
import app.yuyue.design.ButtonKind
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYSheetHeader
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.util.Locale
import kotlin.coroutines.resume
import androidx.compose.runtime.remember
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.text.style.TextOverflow
import app.yuyue.design.YYMotion

/** 设置 · 外文, as on Apple. */
object ForeignPrefs {
    const val SPEAK = "foreign.speak"
    const val ACCENT = "foreign.accent"
    const val MARK = "foreign.mark"
    const val GLOSS = "foreign.gloss"
    const val LEVEL = "foreign.glossLevel"
    const val GLOSS_VOCAB = "foreign.glossVocab"
    const val AI_TRANSLATE = "foreign.aiTranslate"

    /** 点词时读出来. */
    val speaks get() = Prefs.bool(SPEAK, true)
    /** 生词在所有书里标出来. */
    val marks get() = Prefs.bool(MARK, true)
    /** 在难词上方写释义. */
    val glosses get() = Prefs.bool(GLOSS, true)
    val level: Int get() = Prefs.int(LEVEL, 3)
    val glossVocabOnly get() = Prefs.bool(GLOSS_VOCAB, false)
    /** 优先用书伴模型翻译整页与整句. */
    val aiTranslate get() = Prefs.bool(AI_TRANSLATE, true)
    /** en-GB or en-US. */
    val accent get() = Prefs.string(ACCENT, "en-GB")
}

/** 书伴逐段译文对照（含外文译中、文言译现代白话）与按章本地缓存. */
object ParagraphTranslation {
    enum class Target(val htmlLang: String, val cacheTag: String, val systemPrompt: String) {
        ZhModern(
            htmlLang = "zh-Hans",
            cacheTag = "zh-baihua",
            systemPrompt = "把下面按编号排列的原文逐段译为通顺自然的现代简体白话文（若某段已是明白晓畅的现代汉语，则简明释义或保持通顺直译）。严格保留每段开头的 [编号] 标记，每段译文对应一个编号，不要遗漏、合并或输出额外说明。",
        ),
        ZhHans(
            htmlLang = "zh-Hans",
            cacheTag = "zh-Hans",
            systemPrompt = "把下面按编号排列的原文逐段准确、自然地译为简体中文。严格保留每段开头的 [编号] 标记，每段译文对应一个编号，不要遗漏、合并或输出额外说明。",
        ),
        En(
            htmlLang = "en",
            cacheTag = "en",
            systemPrompt = "Translate each numbered paragraph below naturally and accurately into English. Strictly keep the leading [number] marker for each paragraph, one translation per number, without merging, skipping, or adding extra commentary.",
        ),
    }

    fun target(texts: List<String>, bookLanguage: String?, uiChinese: Boolean): Target {
        val prefix = bookLanguage?.lowercase()?.substringBefore('-')?.substringBefore('_').orEmpty()
        val sample = texts.take(8).joinToString("")
        var hasKana = false
        var hanCount = 0
        var letterCount = 0
        var i = 0
        while (i < sample.length) {
            val cp = sample.codePointAt(i)
            if (cp in 0x3040..0x30FF) hasKana = true
            if (Character.UnicodeScript.of(cp) == Character.UnicodeScript.HAN) hanCount++
            if (Character.isLetter(cp)) letterCount++
            i += Character.charCount(cp)
        }
        val isChineseSource = !hasKana && prefix != "ja" && prefix != "ko" &&
            (prefix == "zh" || (letterCount > 0 && hanCount.toDouble() / letterCount.toDouble() >= 0.45))
        return if (uiChinese) {
            if (isChineseSource) Target.ZhModern else Target.ZhHans
        } else {
            if (!isChineseSource && prefix == "en") Target.ZhHans else Target.En
        }
    }

    fun parseNumbered(raw: String, count: Int): List<String?> {
        if (count <= 0) return emptyList()
        val cleaned = app.yuyue.companion.BookCompanion.stripThinking(raw).first.trim()
        if (cleaned.isEmpty()) return List(count) { null }
        val out = MutableList<String?>(count) { null }
        var currentIndex: Int? = null
        val buffer = mutableListOf<String>()
        fun flush() {
            val idx = currentIndex ?: return
            if (idx in out.indices) {
                val joined = buffer.joinToString("\n").trim()
                if (joined.isNotEmpty()) out[idx] = joined
            }
        }
        for (line in cleaned.lines()) {
            val hit = matchBracketTag(line)
            if (hit != null && hit.first in 1..count) {
                flush()
                currentIndex = hit.first - 1
                buffer.clear()
                if (hit.second.isNotEmpty()) buffer += hit.second
            } else if (currentIndex != null) {
                buffer += line
            }
        }
        flush()
        if (out.any { it != null }) return out
        if (count == 1) {
            val single = matchBracketTag(cleaned)?.second?.trim()?.ifEmpty { null } ?: cleaned
            return listOf(single.ifEmpty { null })
        }
        return out
    }

    private fun matchBracketTag(line: String): Pair<Int, String>? {
        val trimmed = line.trimStart(' ', '\t')
        val close = when (trimmed.firstOrNull()) {
            '[' -> ']'
            '【' -> '】'
            else -> return null
        }
        val afterOpen = trimmed.drop(1)
        val digits = afterOpen.takeWhile { it.isDigit() }
        val num = digits.toIntOrNull() ?: return null
        val afterDigits = afterOpen.drop(digits.length)
        if (afterDigits.firstOrNull() != close) return null
        val rest = afterDigits.drop(1).trimStart(' ', '\t', ':', '：')
        return num to rest
    }

    fun batches(texts: List<String>, maxItems: Int = 10, maxChars: Int = 2600): List<List<String>> {
        val result = mutableListOf<List<String>>()
        var current = mutableListOf<String>()
        var chars = 0
        for (t in texts) {
            if (t.isEmpty()) continue
            if (current.isNotEmpty() && (current.size >= maxItems || chars + t.length > maxChars)) {
                result += current
                current = mutableListOf()
                chars = 0
            }
            current += t
            chars += t.length
        }
        if (current.isNotEmpty()) result += current
        return result
    }

    suspend fun loadCache(file: java.io.File): MutableMap<String, String> = withContext(Dispatchers.IO) {
        val out = mutableMapOf<String, String>()
        if (!file.isFile) return@withContext out
        runCatching {
            val obj = org.json.JSONObject(file.readText())
            val keys = obj.keys()
            while (keys.hasNext()) {
                val k = keys.next()
                val v = obj.optString(k, "")
                if (v.isNotEmpty()) out[k] = v
            }
        }
        out
    }

    suspend fun saveCache(file: java.io.File, map: Map<String, String>) = withContext(Dispatchers.IO) {
        if (map.isEmpty()) return@withContext
        runCatching {
            file.parentFile?.mkdirs()
            val obj = org.json.JSONObject()
            for ((k, v) in map) obj.put(k, v)
            val tmp = java.io.File(file.parentFile, "${file.name}.tmp")
            tmp.writeText(obj.toString())
            tmp.renameTo(file)
        }
    }
}

/** The short meaning, as the card shows it: "fortune /ˈfɔːtʃuːn/ n. 财富，大笔的钱；运气". */
data class WordDefinition(val headword: String, val phonetic: String?, val lines: List<String>) {
    /** "n. 财富，大笔的钱" for the 生词本. */
    val line: String get() = lines.firstOrNull() ?: ""
}

/** 点词's dictionary on Android: ECDICT, trimmed by scripts/ecdict-android.py. Android has no system
 *  dictionary like Apple's; this one is English–Chinese, as the reader most often needs. */
object Dictionary {
    @Volatile private var lines: Array<String>? = null

    private fun load(context: Context): Array<String> = lines ?: synchronized(this) {
        lines ?: context.assets.open("ecdict.tsv").bufferedReader().useLines { s -> s.filter { !it.startsWith("#") }.toList().toTypedArray() }.also { lines = it }
    }

    private fun find(all: Array<String>, word: String): String? {
        var lo = 0
        var hi = all.size - 1
        while (lo <= hi) {
            val mid = (lo + hi) ushr 1
            val key = all[mid].substringBefore('\t')
            val c = key.compareTo(word)
            when {
                c < 0 -> lo = mid + 1
                c > 0 -> hi = mid - 1
                else -> return all[mid]
            }
        }
        return null
    }

    /** The word, or the word it is a form of (went → go, happier → happy). */
    suspend fun define(context: Context, word: String): WordDefinition? = withContext(Dispatchers.IO) {
        val all = load(context)
        var w = word.lowercase().trim('\'', '’', '-', '.')
        var row = find(all, w)
        // A plural or a tense the dictionary has under its word.
        row?.split('\t')?.let { if (it.size == 2 && it[1].startsWith("=")) { w = it[1].drop(1); row = find(all, w) } }
        if (row == null && w.endsWith("'s")) { w = w.dropLast(2); row = find(all, w) }
        val parts = row?.split('\t') ?: return@withContext null
        if (parts.size < 3) return@withContext null
        WordDefinition(parts[0], parts[1].ifEmpty { null }?.let { "/$it/" }, parts[2].split("\\n"))
    }
}

/** The word tapped, and what its card shows. */
data class WordLookup(
    val word: String,
    val section: Int,
    val start: TextPoint,
    val end: TextPoint,
    /** The sentence it was met in. */
    val context: String,
    val rect: TextRect?,
    val definition: WordDefinition? = null,
    /** 本书出现 12 次. */
    val count: Int? = null,
    /** In the 生词本 already. */
    val kept: Boolean = false,
    /** The imported MDict dictionary it came from, and its whole entry (完整释义). */
    val dictionary: String? = null,
    val html: String? = null,
) {
    /** What goes into the 生词本: the dictionary's headword, or the word as tapped. */
    val headword: String
        get() = definition?.headword ?: if (word.drop(1) == word.drop(1).lowercase()) word.lowercase() else word
}

/** The book's words are read out in the book's language, English in the accent chosen. */
object WordVoice {
    private var tts: TextToSpeech? = null

    fun say(context: Context, text: String, language: String) {
        val existing = tts
        if (existing == null) {
            lateinit var created: TextToSpeech
            created = TextToSpeech(context.applicationContext) { status ->
                if (status == TextToSpeech.SUCCESS) {
                    tts = created
                    speak(text, language)
                } else {
                    runCatching { created.shutdown() }
                    if (tts === created) tts = null
                }
            }
            tts = created
            return
        }
        existing.stop()
        speak(text, language)
    }

    private fun speak(text: String, language: String) {
        val engine = tts ?: return
        val tag = if (language.lowercase().startsWith("en")) ForeignPrefs.accent else language
        engine.language = Locale.forLanguageTag(tag)
        engine.setSpeechRate(0.9f)
        engine.speak(text, TextToSpeech.QUEUE_FLUSH, null, "word")
    }
}

/** Host that animates [WordCard] entrance and exit from the tapped word's direction. */
@Composable
fun WordCardHost(model: ReaderModel) {
    val active = model.lookup
    var last by remember { mutableStateOf<WordLookup?>(null) }
    if (active != null) last = active
    val lookup = active ?: last ?: return
    val reduce = YYMotion.isReduceMotion()
    val density = LocalDensity.current
    var cardHeightPx by remember { mutableIntStateOf(0) }
    var cardWidthPx by remember { mutableIntStateOf(0) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val top = (lookup.rect?.y ?: 200.0).toFloat().dp
        val bottom = (lookup.rect?.let { it.y + it.height } ?: 220.0).toFloat().dp
        val measuredDp = if (cardHeightPx > 0) with(density) { cardHeightPx.toDp() } else 210.dp
        val minY = 56.dp
        val maxY = (maxHeight - measuredDp - 12.dp).coerceAtLeast(minY)
        val belowCandidate = bottom + 10.dp
        val aboveCandidate = top - measuredDp - 10.dp
        val placeBelow = belowCandidate <= maxY || (aboveCandidate < minY && (maxHeight - bottom) >= (top - minY))
        val y = (if (placeBelow) belowCandidate else aboveCandidate).coerceIn(minY, maxY)
        val wordMidX = lookup.rect?.let { (it.x + it.width / 2.0).toFloat().dp }
        val cardW = if (cardWidthPx > 0) with(density) { cardWidthPx.toDp() } else minOf(maxWidth - 32.dp, 340.dp)
        val cardLeft = (maxWidth - cardW) / 2f
        val originX = if (wordMidX != null && cardW > 1.dp) ((wordMidX - cardLeft) / cardW).coerceIn(0.15f, 0.85f) else 0.5f
        val maxCardHeight = maxHeight * 0.68f
        val origin = TransformOrigin(originX, if (placeBelow) 0f else 1f)
        AnimatedVisibility(
            visible = active != null,
            modifier = Modifier.align(Alignment.TopCenter).offset(y = y),
            enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = 0.92f, transformOrigin = origin),
            exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.92f, transformOrigin = origin),
        ) {
            WordCardBody(
                model,
                lookup,
                modifier = Modifier
                    .heightIn(max = maxCardHeight)
                    .onSizeChanged {
                        cardHeightPx = it.height
                        cardWidthPx = it.width
                    },
            )
        }
    }
}

/** 点词: over or under the word, its headword and sound, the short meaning,
 *  the phrase it was met in, then where it is from · 本书出现 N 次, and 加入生词本. */
@Composable
fun WordCard(model: ReaderModel, lookup: WordLookup) {
    val density = LocalDensity.current
    var cardHeightPx by remember { mutableIntStateOf(0) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val maxCardHeight = maxHeight * 0.68f
        val top = (lookup.rect?.y ?: 200.0).toFloat().dp
        val bottom = (lookup.rect?.let { it.y + it.height } ?: 220.0).toFloat().dp
        val measuredDp = if (cardHeightPx > 0) with(density) { cardHeightPx.toDp() } else 210.dp
        val minY = 56.dp
        val maxY = (maxHeight - measuredDp - 12.dp).coerceAtLeast(minY)
        val belowCandidate = bottom + 10.dp
        val aboveCandidate = top - measuredDp - 10.dp
        val placeBelow = belowCandidate <= maxY || (aboveCandidate < minY && (maxHeight - bottom) >= (top - minY))
        val y = (if (placeBelow) belowCandidate else aboveCandidate).coerceIn(minY, maxY)
        Box(Modifier.align(Alignment.TopCenter).offset(y = y)) {
            WordCardBody(
                model,
                lookup,
                modifier = Modifier
                    .heightIn(max = maxCardHeight)
                    .onSizeChanged { cardHeightPx = it.height },
            )
        }
    }
}

@Composable
private fun WordCardBody(model: ReaderModel, lookup: WordLookup, modifier: Modifier = Modifier) {
    val tint = YY.tint
    val shape = RoundedCornerShape(14.dp)
    var full by remember(lookup.word) { mutableStateOf(false) }
    Column(
        Modifier.padding(horizontal = 16.dp).widthIn(max = 340.dp).fillMaxWidth()
            .then(modifier)
            .shadow(16.dp, shape).clip(shape).background(YY.c.card).border(0.5.dp, YY.c.line, shape)
            .padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(Modifier.weight(1f), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    lookup.definition?.headword ?: lookup.word,
                    fontFamily = FontFamily.Serif, fontWeight = FontWeight.Bold, fontSize = 21.sp, color = YY.c.t1,
                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false).alignByBaseline(),
                )
                lookup.definition?.phonetic?.let {
                    Text(it, fontSize = 11.5.sp, color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.alignByBaseline())
                }
            }
            Box(Modifier.size(40.dp).clip(CircleShape).clickable(onClickLabel = t("再读一遍")) { model.say(lookup.word) }, contentAlignment = Alignment.Center) {
                Box(Modifier.size(28.dp).background(YY.c.inset, CircleShape), contentAlignment = Alignment.Center) {
                    Icon(Icons.speakerWave, t("再读一遍"), tint = YY.c.t1, modifier = Modifier.size(14.dp))
                }
            }
        }
        Column(Modifier.weight(1f, fill = false).verticalScroll(rememberScrollState())) {
            val d = lookup.definition
            if (d != null) {
                Column(Modifier.padding(top = 6.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    for (line in d.lines) {
                        val pos = Regex("""^([a-z]+\.)\s*""").find(line)
                        Text(
                            buildAnnotatedString {
                                if (pos != null) withStyle(SpanStyle(fontFamily = FontFamily.Serif, fontStyle = FontStyle.Italic, color = YY.c.t3)) { append(pos.groupValues[1] + " ") }
                                append(if (pos != null) line.substring(pos.range.last + 1) else line)
                            },
                            fontSize = 13.sp, color = YY.c.t1,
                        )
                    }
                }
            } else {
                Text(t("词典里没有这个词。"), fontSize = 12.sp, color = YY.c.t2, modifier = Modifier.padding(top = 6.dp))
            }
            if (full && lookup.html != null) DictEntryWeb(lookup.html, Modifier.padding(top = 8.dp).fillMaxWidth().height(240.dp))
            phrase(lookup)?.let { Text(it, fontFamily = FontFamily.Serif, fontStyle = FontStyle.Italic, fontSize = 11.5.sp, color = YY.c.t1, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 4.dp)) }
        }
        Box(Modifier.padding(top = 12.dp).fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            val from = lookup.dictionary ?: t("英汉词典（内置）")
            val n = lookup.count ?: 0
            Text(
                if (n == 1) t("%@ · 本书只出现这一次", from) else if (n > 1) t("%@ · 本书出现 %lld 次", from, n) else from,
                fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
            )
            if (lookup.html != null) {
                Box(
                    Modifier.heightIn(min = 32.dp).background(YY.c.inset, CircleShape).clip(CircleShape).clickable { full = !full }.padding(horizontal = 12.dp, vertical = 5.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(if (full) t("收起释义") else t("完整释义"), fontSize = 11.5.sp, color = YY.c.t1, maxLines = 1)
                }
            }
            Row(
                Modifier.heightIn(min = 32.dp).background(if (lookup.kept) YY.c.inset else tint.black, CircleShape).clip(CircleShape).clickable { model.keep(!lookup.kept) }.padding(horizontal = 12.dp, vertical = 5.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Icon(if (lookup.kept) Icons.check else Icons.add, null, tint = if (lookup.kept) YY.c.t1 else tint.onBlack, modifier = Modifier.size(13.dp))
                Text(if (lookup.kept) t("在生词本里") else t("加入生词本"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = if (lookup.kept) YY.c.t1 else tint.onBlack, maxLines = 1)
            }
        }
    }
}

/** A few words either side of it in the sentence: "in possession of a good fortune". */
private fun phrase(l: WordLookup): String? {
    val words = l.context.split(' ').filter { it.isNotEmpty() }
    val i = words.indexOfFirst { it.lowercase().contains(l.word.lowercase()) }
    if (i < 0) return null
    val from = maxOf(0, i - 4)
    val to = minOf(words.size - 1, i + 1)
    val core = words.subList(from, to + 1).joinToString(" ").trim(',', ';', ':', '“', '”', '"', '\'', '‘', '’')
    return (if (from > 0) "…" else "") + core + (if (to < words.size - 1) "…" else "")
}

// MARK: The model's side

/** Read in a language other than Chinese, Japanese or Korean: words can be looked up. */
val ReaderModel.isForeign: Boolean
    get() {
        val language = detail?.summary?.language?.lowercase() ?: return false
        return language.isNotEmpty() && listOf("zh", "ja", "ko", "und").none { language.startsWith(it) }
    }

/** Shows the word's card and says it. */
suspend fun ReaderModel.look(word: org.json.JSONObject) {
    val start = word.optJSONObject("start")?.let { TextPoint(it.optInt("s"), it.optInt("o")) } ?: return
    val end = word.optJSONObject("end")?.let { TextPoint(it.optInt("s"), it.optInt("o")) } ?: return
    val text = word.optString("text").ifEmpty { return }
    val rect = word.optJSONObject("rect")?.let { TextRect(it.optDouble("x"), it.optDouble("y"), it.optDouble("width"), it.optDouble("height")) }
    dismissCards()
    // The dictionaries the reader imported first, then the one built in.
    val entry = withContext(Dispatchers.IO) { runCatching { core.lookUp(text) }.getOrNull() }
    val definition = entry?.let { WordDefinition(it.headword, null, listOf(it.summary)) } ?: Dictionary.define(context, text)
    var l = WordLookup(text, section, start, end, word.optString("sentence"), rect, definition, dictionary = entry?.dictionary, html = entry?.html)
    val words = runCatching { core.words() }.getOrDefault(emptyList())
    l = l.copy(
        kept = words.any { it.word.equals(l.headword, true) && !it.known },
        count = runCatching { core.bookWordCount(bookID, l.headword).toInt() }.getOrNull(),
    )
    lookup = l
    pager.setLooking(section, start, end)
    if (ForeignPrefs.speaks) say(text)
}

fun ReaderModel.closeLookup() {
    if (lookup == null) return
    lookup = null
    pager.setLooking(null, null, null)
}

fun ReaderModel.say(text: String) = WordVoice.say(context, text, detail?.summary?.language ?: "en")

/** 加入生词本, or out of it again. */
fun ReaderModel.keep(on: Boolean) {
    val l = lookup ?: return
    try {
        if (on) {
            core.addWord(l.headword, l.definition?.phonetic, l.definition?.line, bookID, l.section.toUInt(), maxOf(0, l.start.s).toUInt(), l.context)
            show(Library.Toast(Library.Toast.Icon.Check, t("加入了生词本"), l.headword))
        } else {
            core.deleteWord(l.headword)
        }
        lookup = l.copy(kept = on)
        loadVocab()
        library.notesChanged()
    } catch (e: Exception) {
        failure = Library.message(e)
    }
}

/** 生词本 words still being learned, for the dotted line; and 难词注释 again. */
fun ReaderModel.loadVocab() {
    if (!isForeign) return
    val words = runCatching { core.words() }.getOrDefault(emptyList())
    pager.setVocab(if (ForeignPrefs.marks) words.filter { !it.known }.map { it.word } else emptyList())
    pager.refreshGlosses()
}

/** 译 in the tools. */
/** Reading translation display mode: original only, side-by-side bilingual, or translation only. */
enum class TranslateMode(val key: String) {
    OFF("off"),
    BILINGUAL("bilingual"),
    ONLY("only");

    companion object {
        fun of(raw: String): TranslateMode = entries.firstOrNull { it.key == raw } ?: OFF
    }
}

fun ReaderModel.setTranslateMode(mode: TranslateMode) {
    if (mode != TranslateMode.OFF && !isForeign && CompanionConfig.current?.isReady != true) {
        show(Library.Toast(Library.Toast.Icon.Error, t("先在「设置 · 书伴」里配好模型，才能译文对照"), ""))
        return
    }
    translateMode = mode
    if (mode != TranslateMode.OFF) lastActiveTranslateMode = mode
    Prefs.set("translate.$bookID", mode != TranslateMode.OFF)
    Prefs.set("translateMode.$bookID", mode.key)
    pager.setTranslating(mode != TranslateMode.OFF, only = mode == TranslateMode.ONLY)
}

fun ReaderModel.setTranslateShown(on: Boolean) {
    setTranslateMode(if (on) (if (lastActiveTranslateMode == TranslateMode.OFF) TranslateMode.BILINGUAL else lastActiveTranslateMode) else TranslateMode.OFF)
}

private suspend fun ReaderModel.translateParagraphs(section: Int, texts: List<String>): Pair<List<String?>, String> {
    if (texts.isEmpty()) return emptyList<String?>() to "zh-Hans"
    val target = ParagraphTranslation.target(texts, detail?.summary?.language, app.yuyue.L10n.chinese)
    val cfg = CompanionConfig.current
    if ((ForeignPrefs.aiTranslate || !isForeign) && cfg != null && cfg.isReady) {
        val cacheFile = java.io.File(library.dataDir, "translations/$bookID/s$section-${target.cacheTag}.json")
        val cache = ParagraphTranslation.loadCache(cacheFile)
        val missing = texts.map { it.trim() }.filter { it.isNotEmpty() && cache[it] == null }.distinct()
        var aiError: String? = null
        for (batch in ParagraphTranslation.batches(missing)) {
            val prompt = batch.mapIndexed { idx, line -> "[${idx + 1}] $line" }.joinToString("\n\n")
            try {
                val raw = Companion.answer(cfg, target.systemPrompt, prompt, maxTokens = 2048, fast = true)
                val parsed = ParagraphTranslation.parseNumbered(raw, batch.size)
                var wrote = false
                for ((src, dst) in batch.zip(parsed)) {
                    if (!dst.isNullOrEmpty()) {
                        cache[src] = dst
                        wrote = true
                    }
                }
                if (wrote) ParagraphTranslation.saveCache(cacheFile, cache)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                aiError = Companion.message(e)
                break
            }
        }
        val mapped = texts.map { cache[it.trim()] }
        if (mapped.any { it != null }) {
            translateFailure = null
            return mapped to target.htmlLang
        }
        translateFailure = aiError
    }
    if (isForeign && android.os.Build.VERSION.SDK_INT >= 31) {
        val local = texts.map { src ->
            if (src.isBlank()) null else SystemTranslator.translate(context, src, detail?.summary?.language)
        }
        if (local.any { it != null }) {
            translateFailure = null
            return local to target.htmlLang
        }
    }
    if (translateFailure == null) {
        translateFailure = t("先在「设置 · 书伴」里配好模型，才能译文对照")
    }
    return List(texts.size) { null } to target.htmlLang
}

/** Turns on what a book's translation & foreign tools need: 译文对照, 点词, 生词, and 难词注释. */
fun ReaderModel.setUpForeign() {
    if (detail?.summary?.format == uniffi.yuyue_core.BookFormat.PDF) return
    pager.translator = { section, texts -> translateParagraphs(section, texts) }
    pager.onTranslateFailed = {
        setTranslateShown(false)
        val msg = translateFailure ?: t("先在「设置 · 书伴」里配好模型，才能译文对照")
        show(Library.Toast(Library.Toast.Icon.Error, msg, ""))
    }
    val savedRaw = Prefs.string("translateMode.$bookID", "")
    val savedMode = when {
        savedRaw.isNotEmpty() -> TranslateMode.of(savedRaw).takeIf { it != TranslateMode.OFF }
        Prefs.bool("translate.$bookID", false) -> TranslateMode.BILINGUAL
        else -> null
    }
    if (savedMode != null) {
        val aiReady = (ForeignPrefs.aiTranslate || !isForeign) && CompanionConfig.current?.isReady == true
        if (aiReady) setTranslateMode(savedMode)
    }
    if (!isForeign) return
    pager.lookup = true
    if (detail?.summary?.language?.lowercase()?.startsWith("en") == true) {
        pager.glosser = { section ->
            if (!ForeignPrefs.glosses) emptyList()
            else runCatching { core.glosses(bookID, section.toUInt(), ForeignPrefs.level.toUByte(), ForeignPrefs.glossVocabOnly) }.getOrDefault(emptyList()).map { it.form to it.gloss }
        }
    }
    loadVocab()
}

/** 完整释义: an MDict entry as the dictionary wrote it (HTML), scripts off, links inert. */
@Composable
fun DictEntryWeb(html: String, modifier: Modifier = Modifier) {
    val dark = YY.c.t1.luminance() > 0.5f
    val page = """<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <style>body{margin:0;padding:2px 2px 12px;font:14px/1.6 sans-serif;color:${if (dark) "#E8E6E1" else "#2A2B2E"};background:transparent;overflow-wrap:anywhere}
        img{max-width:100%;height:auto}a{color:${if (dark) "#8FB0E0" else "#2B4C7E"};pointer-events:none;text-decoration:none}</style></head><body>$html</body></html>"""
    androidx.compose.ui.viewinterop.AndroidView(
        factory = { c ->
            android.webkit.WebView(c).apply {
                settings.javaScriptEnabled = false
                setBackgroundColor(android.graphics.Color.TRANSPARENT)
                loadDataWithBaseURL(null, page, "text/html", "utf-8", null)
            }
        },
        modifier = modifier,
    )
}

private data class WordMatch(
    val headword: String,
    val definition: WordDefinition,
    val dictionary: String?,
    val kept: Boolean,
)

private sealed interface TranslationResult {
    data object Idle : TranslationResult
    data object Loading : TranslationResult
    data class Done(val text: String, val source: String) : TranslationResult
    data class Failed(val message: String) : TranslationResult
    data object Unconfigured : TranslationResult
}

/** 翻译: translates selected words or passages inside the reader using the built-in dictionary,
 *  Companion AI, or Android's on-device translation engine, with clear guidance to Companion
 *  settings when neither is configured. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TranslateSheet(model: ReaderModel, text: String, recheck: Int, openSettings: () -> Unit, dismiss: () -> Unit) {
    val context = LocalContext.current
    val trimmed = remember(text) { text.trim() }
    val shortPhrase = remember(trimmed) { trimmed.isNotEmpty() && trimmed.length <= 48 && trimmed.split(Regex("""\s+""")).size <= 2 }
    var wordMatch by remember(trimmed) { mutableStateOf<WordMatch?>(null) }
    var result by remember(trimmed) { mutableStateOf<TranslationResult>(TranslationResult.Idle) }
    var attempt by remember(trimmed) { mutableIntStateOf(0) }
    val external = remember(context) { externalTranslationHandlers(context) }

    LaunchedEffect(trimmed) {
        if (!shortPhrase) return@LaunchedEffect
        val cleaned = trimmed.trim('“', '”', '"', '\'', '‘', '’', ',', '.', ';', ':', '!', '?', '(', ')')
        if (cleaned.isEmpty()) return@LaunchedEffect
        val mdict = withContext(Dispatchers.IO) { runCatching { model.core.lookUp(cleaned) }.getOrNull() }
        val def = mdict?.let { WordDefinition(it.headword, null, listOf(it.summary)) } ?: Dictionary.define(context, cleaned)
        if (def != null) {
            val head = def.headword.ifEmpty { cleaned }
            val kept = withContext(Dispatchers.IO) {
                runCatching { model.core.words().any { it.word.equals(head, true) && !it.known } }.getOrDefault(false)
            }
            wordMatch = WordMatch(head, def, mdict?.dictionary, kept)
        }
    }

    LaunchedEffect(trimmed, recheck, attempt) {
        val cfg = CompanionConfig.current
        if (cfg?.isReady == true) {
            result = TranslationResult.Loading
            result = try {
                val hasHan = trimmed.any { Character.UnicodeScript.of(it.code) == Character.UnicodeScript.HAN }
                val target = if (hasHan) "English" else if (app.yuyue.L10n.chinese) "简体中文" else if (model.detail?.summary?.language?.lowercase()?.startsWith("en") == true) "简体中文" else "English"
                val sys = "Translate the following excerpt naturally and accurately into $target. Output only the translation."
                TranslationResult.Done(Companion.answer(cfg, sys, trimmed), cfg.provider.title)
            } catch (e: Exception) {
                TranslationResult.Failed(Companion.message(e))
            }
            return@LaunchedEffect
        }
        if (android.os.Build.VERSION.SDK_INT >= 31) {
            result = TranslationResult.Loading
            val local = SystemTranslator.translate(context, trimmed, model.detail?.summary?.language)
            if (local != null) {
                result = TranslationResult.Done(local, t("系统翻译"))
                return@LaunchedEffect
            }
        }
        result = TranslationResult.Unconfigured
    }

    fun toggleKeepWord(m: WordMatch) {
        val next = !m.kept
        runCatching {
            if (next) {
                val pos = model.pager.here()
                model.core.addWord(m.headword, m.definition.phonetic, m.definition.line, model.bookID, pos?.section ?: 0u, pos?.sentence ?: 0u, trimmed)
                model.show(Library.Toast(Library.Toast.Icon.Check, t("加入了生词本"), m.headword))
            } else {
                model.core.deleteWord(m.headword)
            }
            wordMatch = m.copy(kept = next)
            model.loadVocab()
            model.library.notesChanged()
        }.onFailure { model.failure = Library.message(it) }
    }

    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle(bottom = 2.dp) }) {
        Column(
            Modifier.fillMaxWidth().animateContentSize(app.yuyue.design.YYMotion.layout()).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            YYSheetHeader(t("翻译")) { dismiss() }
            Row(
                Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(14.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(14.dp),
                verticalAlignment = Alignment.Top, horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Text(trimmed, fontFamily = YY.serif, fontSize = 13.5.sp, lineHeight = 21.sp, color = YY.c.t2, maxLines = 5, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                if (model.isForeign && trimmed.length <= 120) {
                    Box(Modifier.size(34.dp).clip(CircleShape).background(YY.c.inset).clickable(onClickLabel = t("再读一遍")) { model.say(trimmed) }, contentAlignment = Alignment.Center) {
                        Icon(Icons.speakerWave, t("再读一遍"), tint = YY.c.t1, modifier = Modifier.size(16.dp))
                    }
                }
            }

            wordMatch?.let { m ->
                Column(
                    Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(14.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(m.headword, fontFamily = FontFamily.Serif, fontWeight = FontWeight.Bold, fontSize = 18.sp, color = YY.c.t1, modifier = Modifier.alignByBaseline())
                        m.definition.phonetic?.let { Text(it, fontSize = 12.sp, color = YY.c.t2, modifier = Modifier.alignByBaseline()) }
                        Spacer(Modifier.weight(1f))
                        YYButton(if (m.kept) t("在生词本里") else t("加入生词本"), if (m.kept) ButtonKind.Soft else ButtonKind.Main, ButtonSize.Small) { toggleKeepWord(m) }
                    }
                    for (line in m.definition.lines) {
                        Text(line, fontSize = 13.5.sp, lineHeight = 20.sp, color = YY.c.t1)
                    }
                    Text(m.dictionary ?: t("英汉词典（内置）"), fontSize = 11.sp, color = YY.c.t3)
                }
            }

            // 正在翻译… gives way to the translation (or why not) with a fade, the sheet growing to it.
            val reduce = app.yuyue.design.YYMotion.isReduceMotion()
            androidx.compose.animation.AnimatedContent(result, transitionSpec = { app.yuyue.design.YYMotion.swap(reduce) }, contentKey = { if (it == TranslationResult.Idle) TranslationResult.Loading::class else it::class }, label = "translation") { r ->
            when (r) {
                TranslationResult.Idle, TranslationResult.Loading -> {
                    Row(
                        Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(14.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.c.t3)
                        Text(t("正在翻译…"), fontSize = 13.sp, color = YY.c.t3)
                    }
                }
                is TranslationResult.Done -> {
                    Column(
                        Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(14.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        SelectionContainer { Text(r.text, fontSize = 15.sp, lineHeight = 23.sp, color = YY.c.t1) }
                        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                            Text(r.source, fontSize = 11.sp, color = YY.c.t3, modifier = Modifier.weight(1f))
                            YYButton(t("复制"), ButtonKind.Raised, ButtonSize.Small) {
                                (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("", r.text))
                                model.show(Library.Toast(Library.Toast.Icon.Check, t("复制好了"), ""))
                            }
                        }
                    }
                }
                is TranslationResult.Failed -> {
                    Column(
                        Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(14.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Text(r.message, fontSize = 13.sp, color = YY.c.danger)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            YYButton(t("重试"), ButtonKind.Raised, ButtonSize.Small) { attempt++ }
                            YYButton(t("去设置 · 书伴"), ButtonKind.Plain, ButtonSize.Small, onClick = openSettings)
                        }
                    }
                }
                TranslationResult.Unconfigured -> {
                    val cfg = CompanionConfig.current
                    Column(
                        Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(14.dp)).padding(14.dp),
                        verticalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(
                            if (cfg == null) t("还没添加翻译用的模型") else t("还没填好 %@ 的 Key", cfg.provider.title),
                            fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1,
                        )
                        Text(t("在“书伴”里填好模型就能直接翻译整句和段落。"), fontSize = 13.sp, lineHeight = 19.sp, color = YY.c.t2)
                        YYButton(t("去设置 · 书伴"), ButtonKind.Raised, ButtonSize.Small, onClick = openSettings)
                    }
                }
            }
            }

            if (external.isNotEmpty()) {
                YYButton(t("用翻译 App 打开"), ButtonKind.Plain, ButtonSize.Small) {
                    val info = external.first()
                    val intent = Intent(Intent.ACTION_PROCESS_TEXT)
                        .setType("text/plain")
                        .putExtra(Intent.EXTRA_PROCESS_TEXT, trimmed)
                        .putExtra(Intent.EXTRA_PROCESS_TEXT_READONLY, true)
                        .setClassName(info.activityInfo.packageName, info.activityInfo.name)
                    runCatching { context.startActivity(intent) }
                        .onFailure { model.show(Library.Toast(Library.Toast.Icon.Error, t("翻译没成功"), "", danger = true)) }
                }
            }
        }
    }
}

private fun externalTranslationHandlers(context: Context): List<ResolveInfo> {
    val intent = Intent(Intent.ACTION_PROCESS_TEXT).setType("text/plain")
    return context.packageManager.queryIntentActivities(intent, 0).filter { info ->
        val pkg = info.activityInfo.packageName.lowercase()
        val cls = info.activityInfo.name.lowercase()
        pkg != context.packageName.lowercase() && !pkg.contains("tts") && !pkg.contains("speech") && !cls.contains("tts") && !cls.contains("readaloud")
    }.sortedByDescending { info ->
        val pkg = info.activityInfo.packageName.lowercase()
        if (pkg.contains("translate") || pkg.contains("dict") || pkg.contains("deepl")) 1 else 0
    }
}

@androidx.annotation.RequiresApi(31)
private object SystemTranslator {
    suspend fun translate(context: Context, text: String, bookLanguage: String?): String? = withContext(Dispatchers.IO) {
        val manager = context.getSystemService(android.view.translation.TranslationManager::class.java) ?: return@withContext null
        val hasHan = text.any { Character.UnicodeScript.of(it.code) == Character.UnicodeScript.HAN }
        val srcLang = bookLanguage?.substringBefore('-')?.substringBefore('_')?.lowercase()?.ifEmpty { null }
            ?: if (hasHan) "zh" else "en"
        val dstLang = if (hasHan) "en" else if (app.yuyue.L10n.chinese || srcLang == "en") "zh" else "en"
        val cap = runCatching {
            manager.getOnDeviceTranslationCapabilities(
                android.view.translation.TranslationSpec.DATA_FORMAT_TEXT,
                android.view.translation.TranslationSpec.DATA_FORMAT_TEXT,
            )
        }.getOrNull()?.firstOrNull {
            it.state == android.view.translation.TranslationCapability.STATE_ON_DEVICE &&
                it.sourceSpec.locale.language == srcLang &&
                it.targetSpec.locale.language == dstLang
        } ?: return@withContext null

        val exec = java.util.concurrent.Executors.newSingleThreadExecutor()
        var translator: android.view.translation.Translator? = null
        try {
            translator = suspendCancellableCoroutine { cont ->
                val ctx = android.view.translation.TranslationContext.Builder(cap.sourceSpec, cap.targetSpec).build()
                manager.createOnDeviceTranslator(ctx, exec) { t ->
                    if (cont.isActive) cont.resume(t) else t.destroy()
                }
            }
            val t = translator ?: return@withContext null
            val req = android.view.translation.TranslationRequest.Builder()
                .setTranslationRequestValues(listOf(android.view.translation.TranslationRequestValue.forText(text)))
                .build()
            suspendCancellableCoroutine<String?> { cont ->
                val signal = android.os.CancellationSignal()
                cont.invokeOnCancellation { signal.cancel() }
                t.translate(req, signal, exec) { res ->
                    if (!cont.isActive) return@translate
                    if (res.translationStatus == android.view.translation.TranslationResponse.TRANSLATION_STATUS_SUCCESS) {
                        val values = res.translationResponseValues
                        val out = (0 until values.size()).mapNotNull { i ->
                            val v = values.valueAt(i)
                            if (v.statusCode == android.view.translation.TranslationResponseValue.STATUS_SUCCESS) v.text?.toString() else null
                        }.joinToString("\n").trim().ifEmpty { null }
                        cont.resume(out)
                    } else {
                        cont.resume(null)
                    }
                }
            }
        } catch (_: Exception) {
            null
        } finally {
            runCatching { translator?.destroy() }
            exec.shutdown()
        }
    }
}
