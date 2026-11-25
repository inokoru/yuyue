package app.yuyue.companion

import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.vector.ImageVector
import app.yuyue.design.Icons
import app.yuyue.library.Library
import app.yuyue.reader.ReaderModel
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.BookDetail
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.TextPoint
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.text.BreakIterator
import java.util.Locale
import java.util.UUID
import kotlin.math.abs
import kotlin.math.roundToInt

/** Selection or existing highlight carried along when opening 书伴 via 问, so an answer can be
 *  saved straight back onto that exact span as a thought. */
data class CompanionAnchor(
    val markID: String?,
    val section: UInt,
    val start: TextPoint,
    val end: TextPoint,
    val text: String,
)

/** 与书对话, as on Apple: by default only the part already read is handed to the
 *  model, so it can't give away what comes later; 问全书 lifts that. The sentences it gets are
 *  numbered, and the answer points back at them: 出处. */
class BookCompanion(private val model: ReaderModel) {
    enum class Scope { Read, Whole }

    enum class QueryKind {
        /** 总结本章: feed the current chapter's text in full reading order. */
        Chapter,
        /** 前情回顾 / 人物关系 / 梳理大纲: broader book coverage and structured length. */
        Broad,
        /** Specific question on a line, character or detail. */
        Focused,
    }

    data class Source(val section: Int, val sentence: Int, val page: Int?, val later: Boolean = false)

    data class Answer(val text: String, val sources: Map<Int, Source>, val label: String, val notYet: Boolean, val next: List<String>?)

    data class Turn(
        val question: String,
        val quote: String?,
        val scope: Scope,
        val anchor: CompanionAnchor? = null,
        val askedAt: Position = Position(0u, 0u),
        val streaming: String? = null,
        val thinking: Boolean = false,
        val answer: Answer? = null,
        val failed: String? = null,
        val savedMarkID: String? = null,
        val id: String = UUID.randomUUID().toString(),
    )

    var scope by mutableStateOf(Scope.Read)
    val turns = mutableStateListOf<Turn>()
    /** The source being shown on the page. */
    var shown by mutableStateOf<Source?>(null)
        private set
    private var asking: Job? = null

    val busy: Boolean get() = turns.lastOrNull()?.let { it.answer == null && it.failed == null } ?: false

    fun clear() {
        asking?.cancel()
        turns.clear()
        shown = null
        model.pager.setSource(null)
    }

    /** Asks, in `scope` (the current one when null). Words chosen on the page go along as `quote`. */
    fun ask(
        question: String,
        quote: String? = null,
        scope: Scope? = null,
        anchor: CompanionAnchor? = null,
        action: CompanionCommand.Action? = null,
    ) {
        val q = question.trim()
        if (q.isEmpty() || busy) return
        val scope = scope ?: this.scope
        val config = CompanionConfig.current
        val detail = model.detail
        val here = model.pager.here() ?: Position((detail?.start ?: 0u), 0u)
        val resolvedAnchor = if (quote != null) (anchor ?: model.companionAnchor) else null
        turns += Turn(q, quote, scope, anchor = resolvedAnchor, askedAt = here)
        val index = turns.lastIndex
        if (config == null || !config.isReady) {
            turns[index] = turns[index].copy(failed = t("书伴还没接上：在设置 · 书伴里添加一个服务商"))
            return
        }
        val core = model.core
        val book = model.bookID
        val kind = classify(q, action)
        val structured = kind != QueryKind.Focused
        val earlier = turns.take(index).takeLast(2).mapNotNull { turn -> turn.answer?.let { turn.question to it.text } }
        val placeTag = if (kind == QueryKind.Chapter) "${here.section}:${here.sentence / 15u}" else if (scope == Scope.Read) "${here.section}" else ""
        val cacheKey = listOf(scope.name.lowercase(), placeTag, quote ?: "", q).joinToString("|")
        cache()[cacheKey]?.let { turns[index] = turns[index].copy(answer = it); return }
        val chapRange = chapterSections(detail, here.section.toInt())
        val bookStart = (detail?.start ?: 0u).toInt()
        asking = model.library.scope.launch {
            val excerpts = withContext(Dispatchers.IO) {
                excerpts(
                    core = core,
                    book = book,
                    question = q + " " + (quote ?: ""),
                    here = here,
                    upTo = if (scope == Scope.Read) here else null,
                    kind = kind,
                    chapterSections = chapRange,
                    bookStart = bookStart,
                )
            }
            val title = detail?.summary?.title ?: ""
            val percent = (model.fraction(here.section.toInt(), here.sentence.toInt()) * 100).roundToInt()
            val styleRule = if (structured) {
                "用${app.yuyue.companion.Companion.replyLanguage}，条理清晰，可用简短分段或要点梳理关键脉络，紧扣原文，不说空话。"
            } else {
                "用${app.yuyue.companion.Companion.replyLanguage}，口语，简短，一般三到五句话。"
            }
            var system = "你是「语阅」里的书伴，陪读者读《$title》${detail?.summary?.authors?.firstOrNull()?.let { "（$it）" } ?: ""}。\n"
            system += when (scope) {
                Scope.Read -> """
                    读者现在读到全书的 $percent%。
                    规则：
                    1. 只根据下面给出的原文片段回答。不要用你知道的、这本书后面的情节，不要剧透。
                    2. $styleRule
                    3. 说到原文里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                    4. 如果这些片段不够回答，或者答案在读者还没读到的地方，只回答三个字：没读到
                    5. 能回答的话，最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                    """.trimIndent()
                Scope.Whole -> """
                    读者想用整本书来回答，不介意提前知道后面的情节。
                    规则：
                    1. 可以用整本书的内容，也可以结合你对这本书的了解。
                    2. $styleRule
                    3. 说到下面原文片段里的内容时，在那句话后面写上片段编号，比如 [2]。只用给出的编号。
                    4. 最后另起一行，写"接着问："，后面是读者可能想接着问的两个短问题，用"｜"隔开。
                    """.trimIndent()
            }
            val prompt = buildString {
                if (earlier.isNotEmpty()) append("之前的对话：\n" + earlier.joinToString("\n") { "读者：${it.first}\n书伴：${it.second}" } + "\n\n")
                append(if (kind == QueryKind.Chapter) "当前章节原文片段（按阅读顺序）：\n" else "原文片段：\n")
                excerpts.forEachIndexed { n, e ->
                    val chapter = detail?.let { ReaderModel.chapterTitle(it, e.section) }?.let { "（$it）" } ?: ""
                    append("[${n + 1}]$chapter${e.text}\n")
                }
                quote?.let { append("\n读者选中的句子：「$it」\n") }
                append("\n读者问：$q")
            }
            val turnId = turns.getOrNull(index)?.id
            try {
                val rawReply = app.yuyue.companion.Companion.answer(
                    config = config,
                    system = system,
                    prompt = prompt,
                    maxTokens = if (structured) 2048 else 1024,
                    source = CompanionUsage.Source.Chat,
                    onStream = { raw: String ->
                        val (preview, isThinking) = streamingPreview(raw, hideNotYet = scope == Scope.Read)
                        model.library.scope.launch {
                            val idx = turns.indexOfFirst { it.id == turnId }
                            if (idx >= 0 && turns[idx].answer == null) {
                                val cur = turns[idx]
                                val nextStreaming = if (preview.isNotEmpty()) preview else cur.streaming
                                if (cur.thinking != isThinking || cur.streaming != nextStreaming) {
                                    turns[idx] = cur.copy(thinking = isThinking, streaming = nextStreaming)
                                }
                            }
                        }
                    },
                )
                val (text, next) = split(rawReply)
                val notYet = scope == Scope.Read && text.startsWith("没读到")
                val used = citations(text).toSet()
                val sources = excerpts.withIndex().filter { (it.index + 1) in used }.associate { (n, e) ->
                    val later = scope == Scope.Whole && (e.section > here.section.toInt() || (e.section == here.section.toInt() && e.sentence > here.sentence.toInt() + 8))
                    (n + 1) to Source(e.section, e.sentence, model.estimatedPage(e.section, e.sentence), later)
                }
                val answer = Answer(text, sources, config.label, notYet, if (notYet || next.isEmpty()) null else next)
                val idx = turns.indexOfFirst { it.id == turnId }
                if (idx >= 0) turns[idx] = turns[idx].copy(streaming = null, thinking = false, answer = answer)
                store(cacheKey, answer)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                val idx = turns.indexOfFirst { it.id == turnId }
                if (idx >= 0) turns[idx] = turns[idx].copy(streaming = null, thinking = false, failed = app.yuyue.companion.Companion.message(e))
            }
        }
    }

    /** 出处: the page goes there and the sentence is washed in the accent. */
    fun show(s: Source) {
        shown = s
        model.jump(s.section, s.sentence)
        model.pager.setSource(Position(s.section.toUInt(), s.sentence.toUInt()))
    }

    /** Saves a completed answer onto the anchor highlight (or the cited / current sentence) as a thought. */
    fun saveThought(turnID: String) {
        val index = turns.indexOfFirst { it.id == turnID }
        if (index < 0) return
        val turn = turns[index]
        val answer = turn.answer ?: return
        if (answer.notYet) return
        turn.savedMarkID?.let { savedId ->
            model.marks.firstOrNull { it.id == savedId }?.let { existing ->
                model.jump(existing.section.toInt(), existing.start.sentence.toInt())
                return
            }
        }
        val clean = noteText(answer.text)
        if (clean.isEmpty()) return

        // 1. Existing highlight tapped via 问: append/update its thought.
        val existingMark = turn.anchor?.markID?.let { id -> model.marks.firstOrNull { it.id == id } }
        if (existingMark != null) {
            val previousNote = existingMark.note
            val combined = mergeNote(previousNote, clean)
            runCatching {
                model.core.setMarkNote(model.bookID, existingMark.id, combined)
                model.reloadNotes()
                turns[index] = turns[index].copy(savedMarkID = existingMark.id)
                model.show(Library.Toast(Library.Toast.Icon.Highlight, t("已存进想法"), "“${existingMark.text.take(16)}”") {
                    runCatching {
                        model.core.setMarkNote(model.bookID, existingMark.id, previousNote)
                        model.reloadNotes()
                        val i = turns.indexOfFirst { it.id == turnID }
                        if (i >= 0) turns[i] = turns[i].copy(savedMarkID = null)
                    }
                })
            }.onFailure { model.failure = Library.message(it) }
            return
        }

        // 2. Fresh text selection handed via 问: highlight that exact span with the thought.
        val anchor = turn.anchor
        if (anchor != null && anchor.text.isNotEmpty()) {
            runCatching {
                val created = model.core.addMark(model.bookID, anchor.section, anchor.start, anchor.end, anchor.text, model.lastStyle, clean)
                model.pager.keepShown(created.id)
                model.reloadNotes()
                turns[index] = turns[index].copy(savedMarkID = created.id)
                model.show(Library.Toast(Library.Toast.Icon.Highlight, t("已存进想法"), "“${created.text.take(16)}”") {
                    runCatching {
                        model.core.deleteMark(model.bookID, created.id)
                        model.reloadNotes()
                        val i = turns.indexOfFirst { it.id == turnID }
                        if (i >= 0) turns[i] = turns[i].copy(savedMarkID = null)
                    }
                })
            }.onFailure { model.failure = Library.message(it) }
            return
        }

        // 3. No selection quote: anchor to the first cited sentence, or where the question was asked.
        val firstCited = answer.sources.keys.minOrNull()?.let { answer.sources[it] }
        val targetSection = firstCited?.section?.toUInt() ?: turn.askedAt.section
        val targetSentence = firstCited?.sentence ?: turn.askedAt.sentence.toInt()
        val all = runCatching { model.core.sentences(model.bookID, targetSection) }.getOrDefault(emptyList())
        if (all.isEmpty()) return
        val clamped = targetSentence.coerceIn(0, all.lastIndex)
        val chosenIndex = (clamped..all.lastIndex).firstOrNull { all[it].trim().isNotEmpty() }
            ?: (clamped - 1 downTo 0).firstOrNull { all[it].trim().isNotEmpty() }
            ?: clamped
        val raw = all[chosenIndex]
        val trimmed = raw.trim()
        if (trimmed.isEmpty()) return
        val charStart = raw.indexOf(trimmed).coerceAtLeast(0)
        val utf16Start = raw.substring(0, charStart).length
        val utf16End = utf16Start + trimmed.length
        val start = TextPoint(chosenIndex.toUInt(), utf16Start.toUInt())
        val end = TextPoint(chosenIndex.toUInt(), utf16End.toUInt())

        val sameSpan = model.marks.firstOrNull { it.section == targetSection && it.start == start && it.end == end }
        if (sameSpan != null) {
            val previousNote = sameSpan.note
            val combined = mergeNote(previousNote, clean)
            runCatching {
                model.core.setMarkNote(model.bookID, sameSpan.id, combined)
                model.reloadNotes()
                turns[index] = turns[index].copy(savedMarkID = sameSpan.id)
                model.show(Library.Toast(Library.Toast.Icon.Highlight, t("已存进想法"), "“${sameSpan.text.take(16)}”") {
                    runCatching {
                        model.core.setMarkNote(model.bookID, sameSpan.id, previousNote)
                        model.reloadNotes()
                        val i = turns.indexOfFirst { it.id == turnID }
                        if (i >= 0) turns[i] = turns[i].copy(savedMarkID = null)
                    }
                })
            }.onFailure { model.failure = Library.message(it) }
            return
        }

        runCatching {
            val created = model.core.addMark(model.bookID, targetSection, start, end, trimmed, model.lastStyle, clean)
            model.pager.keepShown(created.id)
            model.reloadNotes()
            turns[index] = turns[index].copy(savedMarkID = created.id)
            model.show(Library.Toast(Library.Toast.Icon.Highlight, t("已存进想法"), "“${created.text.take(16)}”") {
                runCatching {
                    model.core.deleteMark(model.bookID, created.id)
                    model.reloadNotes()
                    val i = turns.indexOfFirst { it.id == turnID }
                    if (i >= 0) turns[i] = turns[i].copy(savedMarkID = null)
                }
            })
        }.onFailure { model.failure = Library.message(it) }
    }

    // MARK: Finding the sentences

    class Excerpt(val section: Int, val sentence: Int, val text: String)

    /** Builds numbered passages `[1]…[K]` grounded in the book:
     *  - `Chapter`: chunks the entire current chapter (within `upTo` when spoiler-free) sequentially.
     *  - `Broad`: combines keyword hits with milestone passages across read sections and recent prose.
     *  - `Focused`: top keyword matches plus the sentences around the current reading spot. */
    private fun excerpts(
        core: YuyueCore,
        book: String,
        question: String,
        here: Position,
        upTo: Position?,
        kind: QueryKind,
        chapterSections: IntRange,
        bookStart: Int,
    ): List<Excerpt> {
        val words = mutableSetOf<String>()
        val it = BreakIterator.getWordInstance(Locale.getDefault()).apply { setText(question) }
        var start = it.first()
        var end = it.next()
        while (end != BreakIterator.DONE) {
            val w = question.substring(start, end).lowercase()
            val latin = w.all { c -> c.code < 128 }
            if ((if (latin) w.length >= 3 else w.length >= 2) && w.any { c -> c.isLetterOrDigit() } && w !in stopWords) words += w
            start = end
            end = it.next()
        }
        val cache = mutableMapOf<Int, List<String>>()
        fun text(section: Int) = cache.getOrPut(section) { runCatching { core.sentences(book, section.toUInt()) }.getOrDefault(emptyList()) }
        fun allowed(section: Int, sentence: Int): Boolean {
            val u = upTo ?: return true
            return section < u.section.toInt() || (section == u.section.toInt() && sentence <= u.sentence.toInt() + 8)
        }

        // 1. Chapter-level context: sequential ~480-char passages across the current chapter.
        if (kind == QueryKind.Chapter) {
            val chapterChunks = ArrayList<Excerpt>()
            for (sec in chapterSections) {
                if (sec < 0) continue
                if (upTo != null && sec > upTo.section.toInt()) break
                val all = text(sec)
                if (all.isEmpty()) continue
                val limit = if (upTo != null && sec == upTo.section.toInt()) minOf(all.size, upTo.sentence.toInt() + 9) else all.size
                var idx = 0
                while (idx < limit) {
                    val startSent = idx
                    val parts = ArrayList<String>()
                    var chars = 0
                    while (idx < limit && (chars < 480 || parts.isEmpty())) {
                        val s = all[idx].trim().replace("\n", " ")
                        if (s.isNotEmpty()) {
                            parts += s
                            chars += s.length
                        }
                        idx++
                    }
                    if (parts.isNotEmpty()) {
                        chapterChunks += Excerpt(sec, startSent, parts.joinToString(" ").take(540))
                    }
                }
            }
            if (chapterChunks.isNotEmpty()) {
                val maxChunks = 24
                if (chapterChunks.size <= maxChunks) return chapterChunks
                val chosen = sortedSetOf(0, chapterChunks.lastIndex)
                val hereSec = here.section.toInt()
                val hereSent = here.sentence.toInt()
                val nearIdx = chapterChunks.indexOfLast { it.section < hereSec || (it.section == hereSec && it.sentence <= hereSent) }
                if (nearIdx >= 0) chosen += nearIdx
                if (words.isNotEmpty()) {
                    for ((ci, chunk) in chapterChunks.withIndex()) {
                        if (chosen.size >= maxChunks / 2) break
                        val lower = chunk.text.lowercase()
                        if (words.any { lower.contains(it) }) chosen += ci
                    }
                }
                val step = (chapterChunks.size - 1).toDouble() / (maxChunks - 1).toDouble()
                for (k in 0 until maxChunks) {
                    if (chosen.size >= maxChunks) break
                    chosen += minOf(chapterChunks.lastIndex, (k * step).roundToInt())
                }
                return chosen.take(maxChunks).map { chapterChunks[it] }
            }
        }

        // 2. Keyword search + broad/focused context.
        val score = mutableMapOf<String, Triple<Int, Int, Int>>()
        for (w in words) {
            for (h in runCatching { core.searchBook(book, w) }.getOrDefault(emptyList())) {
                val s = h.section.toInt()
                val n = h.sentence.toInt()
                if (!allowed(s, n)) continue
                val key = "$s/$n"
                score[key] = Triple((score[key]?.first ?: 0) + 1, s, n)
            }
        }
        val picked = mutableListOf<Pair<Int, Int>>()
        for ((_, s, n) in score.values.sortedWith(compareByDescending<Triple<Int, Int, Int>> { it.first }.thenByDescending { it.second }.thenByDescending { it.third })) {
            if (picked.size >= 10) break
            if (picked.any { it.first == s && abs(it.second - n) <= 2 }) continue
            picked += s to n
        }

        val anchorPos = upTo ?: here
        val anchorSec = anchorPos.section.toInt()
        val recentSpan = if (kind == QueryKind.Broad) 20 else 12
        val last = anchorPos.sentence.toInt() + 3
        var s = last
        while (s >= maxOf(0, last - recentSpan)) {
            if (s < text(anchorSec).size && picked.none { it.first == anchorSec && abs(it.second - s) <= 2 }) picked += anchorSec to s
            s -= 4
        }

        if (kind == QueryKind.Broad && picked.size < 16) {
            val maxSec = anchorSec
            val minSec = maxOf(0, minOf(bookStart, maxSec))
            if (maxSec >= minSec) {
                val totalSecs = maxSec - minSec + 1
                val strideSec = maxOf(1, totalSecs / 8)
                var sec = minSec
                while (sec <= maxSec && picked.size < 16) {
                    val all = text(sec)
                    if (all.isNotEmpty()) {
                        val cap = if (upTo != null && sec == anchorSec) minOf(all.size, anchorPos.sentence.toInt() + 1) else all.size
                        for (probe in listOf(0, cap / 2)) {
                            if (probe in 0 until cap && picked.size < 16 && picked.none { it.first == sec && abs(it.second - probe) <= 3 }) {
                                picked += sec to probe
                            }
                        }
                    }
                    sec += strideSec
                }
            }
        }

        val charLimit = if (kind == QueryKind.Broad) 360 else 280
        val tailOffset = if (kind == QueryKind.Broad) 3 else 2
        return picked.sortedWith(compareBy({ it.first }, { it.second })).mapNotNull { (section, sentence) ->
            val all = text(section)
            if (sentence !in all.indices) return@mapNotNull null
            val joined = all.subList(maxOf(0, sentence - 1), minOf(all.size, sentence + tailOffset)).joinToString(" ") { it.trim() }.replace("\n", " ")
            Excerpt(section, sentence, joined.take(charLimit))
        }
    }

    // MARK: Kept answers

    /** 按"书 + 章节"缓存: the same question at the same place costs nothing the second time. */
    private val file: File get() = File(model.library.dataDir, "companion/${model.bookID}.json")

    private fun cache(): Map<String, Answer> = runCatching {
        val j = JSONObject(file.readText())
        j.keys().asSequence().associateWith { k ->
            val a = j.getJSONObject(k)
            val src = a.optJSONObject("sources") ?: JSONObject()
            Answer(
                a.getString("text"),
                src.keys().asSequence().associate { n ->
                    val s = src.getJSONObject(n)
                    n.toInt() to Source(s.getInt("section"), s.getInt("sentence"), if (s.has("page")) s.getInt("page") else null, s.optBoolean("later"))
                },
                a.optString("label"), a.optBoolean("notYet"),
                a.optJSONArray("next")?.let { arr -> (0 until arr.length()).map { arr.getString(it) } },
            )
        }
    }.getOrDefault(emptyMap())

    private fun store(key: String, a: Answer) {
        val all = runCatching { JSONObject(file.readText()) }.getOrDefault(JSONObject())
        val src = JSONObject()
        for ((n, s) in a.sources) src.put("$n", JSONObject().put("section", s.section).put("sentence", s.sentence).put("later", s.later).apply { s.page?.let { put("page", it) } })
        all.put(key, JSONObject().put("text", a.text).put("sources", src).put("label", a.label).put("notYet", a.notYet).apply { a.next?.let { put("next", JSONArray(it)) } })
        file.parentFile?.mkdirs()
        file.writeText(all.toString())
    }

    companion object {
        private val closedThinkRegex = Regex("""(?is)<think>.*?</think>""")
        private val openThinkRegex = Regex("""(?i)<think>""")
        private val trailingBracketRegex = Regex("""\s*\[\d*$""")
        private val citationStripRegex = Regex("""[ \t\u00A0]*\[\d+]""")
        private val citationFindRegex = Regex("""\[(\d+)]""")

        /** Removes `<think>…</think>` blocks from reasoning models, reporting whether an unclosed `<think>` is still streaming. */
        fun stripThinking(raw: String): Pair<String, Boolean> {
            val completed = closedThinkRegex.replace(raw, "")
            val openMatch = openThinkRegex.find(completed)
            if (openMatch != null) {
                return completed.substring(0, openMatch.range.first).trim() to true
            }
            return completed.trim() to false
        }

        /** Live preview string during SSE streaming: hides `<think>`, hides the trailing `接着问` line,
         *  and trims an incomplete `[1` bracket at the very tail. */
        fun streamingPreview(raw: String, hideNotYet: Boolean): Pair<String, Boolean> {
            val (cleaned, thinking) = stripThinking(raw)
            if (cleaned.isEmpty()) return "" to thinking
            if (hideNotYet && cleaned.startsWith("没读到")) return "" to thinking
            val lines = cleaned.split("\n").toMutableList()
            val askIdx = lines.indexOfLast { "接着问" in it }
            if (askIdx >= 0) {
                while (lines.size > askIdx) lines.removeAt(lines.lastIndex)
            }
            var body = trailingBracketRegex.replace(lines.joinToString("\n"), "").trim()
            if (hideNotYet && "没读到".startsWith(body)) body = ""
            return body to thinking
        }

        /** Strips `[n]` citation markers so an answer saved into 想法 reads cleanly in notes & exports. */
        fun noteText(answerText: String): String = citationStripRegex.replace(answerText, "").trim()

        internal fun mergeNote(existing: String?, addition: String): String {
            val old = existing?.trim().orEmpty()
            if (old.isEmpty()) return addition
            if (addition in old) return old
            return "$old\n\n$addition"
        }

        /** The answer (with `<think>` blocks removed), and the 接着问 line taken off its end. */
        fun split(reply: String): Pair<String, List<String>> {
            val cleaned = stripThinking(reply).first
            val lines = cleaned.split("\n").toMutableList()
            val i = lines.indexOfLast { "接着问" in it }
            if (i < 0) return cleaned to emptyList()
            val line = lines.removeAt(i)
            val asks = line.substringAfterLast("接着问").trim('：', ':', ' ', '*').split('｜', '|').map { it.trim() }.filter { it.isNotEmpty() }
            return lines.joinToString("\n").trim() to asks.take(2)
        }

        fun citations(text: String): List<Int> = citationFindRegex.findAll(text).mapNotNull { it.groupValues[1].toIntOrNull() }.toList()

        fun classify(question: String, action: CompanionCommand.Action? = null): QueryKind {
            when (action) {
                is CompanionCommand.Action.Chapter -> return QueryKind.Chapter
                is CompanionCommand.Action.Recap,
                is CompanionCommand.Action.Characters,
                is CompanionCommand.Action.Quote,
                is CompanionCommand.Action.Quiz -> return QueryKind.Broad
                else -> {}
            }
            val lower = question.lowercase()
            val chapterMarkers = listOf("这一章", "本章", "这章", "当前章", "这回", "本回", "这一回", "这一节", "本节", "this chapter", "current chapter")
            if (chapterMarkers.any { it in lower }) return QueryKind.Chapter
            val broadMarkers = listOf(
                "前面发生", "前情", "目前为止", "主要人物", "人物关系", "出场人物",
                "总结", "概括", "梳理", "大纲", "脉络", "主旨", "分析", "思考题", "考考我", "挑几句", "挑两三句",
                "what happened", "so far", "main characters", "summarize", "summary", "outline", "analyze",
            )
            if (broadMarkers.any { it in lower }) return QueryKind.Broad
            return QueryKind.Focused
        }

        fun chapterSections(detail: BookDetail?, section: Int): IntRange {
            if (detail == null) return section..section
            val total = maxOf(1, detail.sections.size)
            val secU = section.coerceAtLeast(0).toUInt()
            val toc = detail.toc
            val entry = toc.lastOrNull { it.section <= secU } ?: toc.firstOrNull { it.section == secU }
            if (entry != null) {
                val start = entry.section.toInt()
                val end = toc.firstOrNull { it.section > entry.section }?.section?.toInt() ?: total
                return start until maxOf(start + 1, end)
            }
            val cur = section.coerceIn(0, total - 1)
            return cur..cur
        }

        private val stopWords = setOf(
            "什么", "为什么", "怎么", "怎么样", "怎样", "如何", "哪里", "哪儿", "哪个", "谁", "是不是", "有没有",
            "这个", "那个", "这些", "那些", "这里", "那里", "这本书", "本书", "这本", "这章", "这一章", "一章",
            "意思", "后来", "之后", "以后", "现在", "时候", "为何", "是否", "可以", "能不能", "一个", "一下",
            "他们", "她们", "我们", "你们", "自己", "就是", "还是", "但是", "因为", "所以", "然后", "发生",
            "总结", "概括", "梳理", "讲了", "主要", "内容", "目前", "为止", "前面", "人物", "关系", "出场",
            "the", "and", "what", "why", "how", "who", "does", "did", "this", "that", "with", "about",
        )
    }
}

/** 用户保存在「设置 · 书伴」里的常用提问，同时出现在底部建议栏和 `/` 快捷指令里. */
object CompanionPrompts {
    const val KEY = "companion.customPrompts"
    const val MAX_COUNT = 8
    const val MAX_LENGTH = 120

    var version by androidx.compose.runtime.mutableIntStateOf(0)
        private set

    val presets: List<String>
        get() = listOf(
            t("拆解这句语法与长难句"),
            t("把这段古文译成现代白话"),
            t("提炼核心论点与逻辑"),
        )

    val list: List<String>
        get() {
            if (!app.yuyue.design.Prefs.isReady) return emptyList()
            val raw = app.yuyue.design.Prefs.string(KEY, "[]")
            val arr = runCatching { JSONArray(raw) }.getOrNull() ?: return emptyList()
            return normalized((0 until arr.length()).mapNotNull { arr.optString(it, "").ifEmpty { null } })
        }

    fun normalized(items: List<String>): List<String> {
        val seen = LinkedHashSet<String>()
        for (raw in items) {
            val trimmed = raw.trim().take(MAX_LENGTH)
            if (trimmed.isNotEmpty() && seen.add(trimmed) && seen.size >= MAX_COUNT) break
        }
        return seen.toList()
    }

    fun adding(prompt: String, current: List<String>): List<String> {
        val trimmed = prompt.trim().take(MAX_LENGTH)
        val base = normalized(current)
        if (trimmed.isEmpty() || trimmed in base || base.size >= MAX_COUNT) return base
        return base + trimmed
    }

    fun removing(prompt: String, current: List<String>): List<String> =
        normalized(current).filter { it != prompt }

    fun add(prompt: String) {
        val cur = list
        val next = adding(prompt, cur)
        if (next == cur) return
        save(next)
    }

    fun remove(prompt: String) {
        val cur = list
        val next = removing(prompt, cur)
        if (next == cur) return
        save(next)
    }

    private fun save(items: List<String>) {
        if (!app.yuyue.design.Prefs.isReady) return
        val arr = JSONArray()
        for (item in items) arr.put(item)
        app.yuyue.design.Prefs.set(KEY, arr.toString())
        version++
    }

    fun suggestions(
        hasQuote: Boolean,
        followUps: List<String>?,
        quoteDefaults: List<String>,
        startDefaults: List<String>,
        custom: List<String> = list,
    ): List<String> {
        val (primary, secondary) = when {
            hasQuote -> custom to quoteDefaults
            !followUps.isNullOrEmpty() -> followUps to custom
            else -> custom to startDefaults
        }
        val seen = LinkedHashSet<String>()
        for (s in primary + secondary) {
            if (s.isNotEmpty()) seen.add(s)
        }
        return seen.toList()
    }
}

/** Slash commands (`/recap`, `/chapter`, `/clear`, …) aligned 1:1 with Apple's `CompanionCommand`. */
data class CompanionCommand(
    val id: String,
    val name: String,
    val title: String,
    val subtitle: String,
    val icon: ImageVector,
    val category: Category,
    val action: Action,
    val takesArgument: Boolean,
    val keywords: List<String>,
) {
    enum class Category { Ask, Control }

    sealed interface Action {
        data object Recap : Action
        data object Chapter : Action
        data object Characters : Action
        data object Explain : Action
        data object Quote : Action
        data object Quiz : Action
        data class Custom(val prompt: String) : Action
        data class Scope(val scope: BookCompanion.Scope) : Action
        data object Model : Action
        data object Clear : Action
        data object Settings : Action
    }

    data class Parsed(val command: CompanionCommand, val argument: String)

    val slashName: String get() = "/$name"

    fun question(argument: String, hasQuote: Boolean, chapterTitle: String?): String? {
        val arg = argument.trim()
        return when (action) {
            Action.Recap -> if (arg.isNotEmpty()) t("帮我回顾一下目前为止关于「%@」的情节。", arg) else t("帮我回顾一下目前为止发生了什么？")
            Action.Chapter -> when {
                arg.isNotEmpty() -> t("结合「%@」，总结一下这一章讲了什么？", arg)
                !chapterTitle.isNullOrEmpty() -> t("总结一下「%@」这一章讲了什么？", chapterTitle)
                else -> t("总结一下这一章讲了什么？")
            }
            Action.Characters -> if (arg.isNotEmpty()) t("介绍一下「%@」是谁，以及和其他人物的关系。", arg) else t("梳理一下目前出场的主要人物和他们之间的关系。")
            Action.Explain -> when {
                arg.isNotEmpty() -> t("解释一下「%@」在书里的意思和作用。", arg)
                hasQuote -> t("这句是什么意思？结合上下文解释一下。")
                else -> t("结合上下文，解释一下当前这一段的意思和用意。")
            }
            Action.Quote -> if (arg.isNotEmpty()) t("从已读内容里挑几句和「%@」相关的精彩句子，并标出出处。", arg) else t("从目前读过的内容里，挑两三句值得回味的句子并简短点评。")
            Action.Quiz -> if (arg.isNotEmpty()) t("围绕「%@」，根据我已读的内容出两道思考题考考我。", arg) else t("根据我目前读过的内容，出两道有意思的思考题考考我。")
            is Action.Custom -> if (arg.isEmpty()) action.prompt else "${action.prompt}\n$arg"
            is Action.Scope, Action.Model, Action.Clear, Action.Settings -> null
        }
    }

    companion object {
        val all: List<CompanionCommand>
            get() = listOf(
                CompanionCommand(
                    id = "recap", name = "recap",
                    title = t("回顾前情"), subtitle = t("梳理目前为止发生了什么"),
                    icon = Icons.clockwise, category = Category.Ask, action = Action.Recap,
                    takesArgument = true, keywords = listOf("前情", "回顾", "剧情", "摘要", "summary", "previously"),
                ),
                CompanionCommand(
                    id = "chapter", name = "chapter",
                    title = t("总结本章"), subtitle = t("概括当前章节的主要内容"),
                    icon = Icons.docText, category = Category.Ask, action = Action.Chapter,
                    takesArgument = true, keywords = listOf("本章", "章节", "总结", "这一章", "summary"),
                ),
                CompanionCommand(
                    id = "characters", name = "characters",
                    title = t("人物关系"), subtitle = t("梳理已出场人物与关系"),
                    icon = Icons.personTwo, category = Category.Ask, action = Action.Characters,
                    takesArgument = true, keywords = listOf("人物", "角色", "关系", "谁", "who", "cast"),
                ),
                CompanionCommand(
                    id = "explain", name = "explain",
                    title = t("解释含义"), subtitle = t("解读选中句子或当前段落"),
                    icon = Icons.docSearch, category = Category.Ask, action = Action.Explain,
                    takesArgument = true, keywords = listOf("解释", "解析", "意思", "含义", "meaning"),
                ),
                CompanionCommand(
                    id = "quote", name = "quote",
                    title = t("挑出金句"), subtitle = t("从已读内容里找几句值得回味的话"),
                    icon = Icons.quote, category = Category.Ask, action = Action.Quote,
                    takesArgument = true, keywords = listOf("金句", "摘录", "名句", "句子", "highlight"),
                ),
                CompanionCommand(
                    id = "quiz", name = "quiz",
                    title = t("考考我"), subtitle = t("根据已读内容出两道思考题"),
                    icon = Icons.textBubble, category = Category.Ask, action = Action.Quiz,
                    takesArgument = true, keywords = listOf("考考我", "测验", "提问", "思考", "test"),
                ),
            ) + customCommands(CompanionPrompts.list) + listOf(
                CompanionCommand(
                    id = "read", name = "read",
                    title = t("仅限已读"), subtitle = t("切回不剧透模式"),
                    icon = Icons.eyeSlash, category = Category.Control, action = Action.Scope(BookCompanion.Scope.Read),
                    takesArgument = true, keywords = listOf("已读", "不剧透", "防剧透", "spoiler"),
                ),
                CompanionCommand(
                    id = "whole", name = "whole",
                    title = t("全书范围"), subtitle = t("检索整本书（可能剧透）"),
                    icon = Icons.book, category = Category.Control, action = Action.Scope(BookCompanion.Scope.Whole),
                    takesArgument = true, keywords = listOf("全书", "剧透", "整本", "all"),
                ),
                CompanionCommand(
                    id = "model", name = "model",
                    title = t("切换模型"), subtitle = t("打开模型与服务商选择"),
                    icon = Icons.sparkles, category = Category.Control, action = Action.Model,
                    takesArgument = false, keywords = listOf("模型", "服务商", "切换", "provider"),
                ),
                CompanionCommand(
                    id = "clear", name = "clear",
                    title = t("清空对话"), subtitle = t("清除当前这本书的对话记录"),
                    icon = Icons.delete, category = Category.Control, action = Action.Clear,
                    takesArgument = false, keywords = listOf("清空", "新对话", "清除", "重置", "reset", "new"),
                ),
                CompanionCommand(
                    id = "settings", name = "settings",
                    title = t("书伴设置"), subtitle = t("配置服务商与 API Key"),
                    icon = Icons.settings, category = Category.Control, action = Action.Settings,
                    takesArgument = false, keywords = listOf("设置", "配置", "key", "api"),
                ),
            )

        fun customCommands(prompts: List<String>): List<CompanionCommand> =
            prompts.mapIndexed { index, prompt ->
                CompanionCommand(
                    id = "custom-${index + 1}",
                    name = "p${index + 1}",
                    title = prompt,
                    subtitle = t("常用提问"),
                    icon = Icons.textBubble,
                    category = Category.Ask,
                    action = Action.Custom(prompt),
                    takesArgument = true,
                    keywords = listOf("常用", "自定义", "custom", "prompt"),
                )
            }

        fun slashQuery(draft: String): String? {
            val first = draft.firstOrNull() ?: return null
            if (first != '/' && first != '／') return null
            val rest = draft.substring(1)
            if (rest.any { it.isWhitespace() }) return null
            return rest
        }

        fun matching(query: String, commands: List<CompanionCommand> = all): List<CompanionCommand> {
            val q = query.trim().lowercase()
            if (q.isEmpty()) return commands
            return commands.mapNotNull { cmd ->
                val score = when {
                    cmd.name.startsWith(q) -> 0
                    cmd.title.lowercase().startsWith(q) -> 1
                    cmd.keywords.any { it.lowercase().startsWith(q) } -> 2
                    q in cmd.name || q in cmd.title.lowercase() || cmd.keywords.any { q in it.lowercase() } || q in cmd.subtitle.lowercase() -> 3
                    else -> return@mapNotNull null
                }
                score to cmd
            }.sortedBy { it.first }.map { it.second }
        }

        fun parse(draft: String, commands: List<CompanionCommand> = all): Parsed? {
            val trimmed = draft.trim()
            val first = trimmed.firstOrNull() ?: return null
            if (first != '/' && first != '／') return null
            val body = trimmed.substring(1)
            val splitIdx = body.indexOfFirst { it.isWhitespace() }.let { if (it < 0) body.length else it }
            val token = body.substring(0, splitIdx).lowercase()
            if (token.isEmpty()) return null
            val arg = body.substring(splitIdx).trim()
            val cmd = commands.firstOrNull { it.name == token || it.title.lowercase() == token } ?: return null
            return Parsed(cmd, arg)
        }
    }
}
