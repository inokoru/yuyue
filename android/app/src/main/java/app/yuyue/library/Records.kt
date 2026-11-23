package app.yuyue.library

import android.text.format.DateUtils
import app.yuyue.t
import uniffi.yuyue_core.BookDetail
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.ProgressUnit
import uniffi.yuyue_core.ReadingStatus
import uniffi.yuyue_core.SectionKind
import java.text.Collator

// 书: the record the reader keeps of each book — 在读 · 想读 · 读过 · 搁置 · 弃读, a score, a line
// of their own, tags, and how far — as on iPhone.

/** The order 进度 lists them in. */
val shelfOrder = listOf(ReadingStatus.READING, ReadingStatus.WANT, ReadingStatus.FINISHED, ReadingStatus.PAUSED, ReadingStatus.DROPPED)

/** 约 7 小时 · 约 40 分钟 */
fun hoursText(minutes: Int): String = if (minutes < 60) t("%lld 分钟", maxOf(1, minutes)) else t("%lld 小时", Math.round(minutes / 60.0).toInt())

/** 刚刚 · 14 小时前 · 3 天前 */
fun agoText(ms: Long): String =
    if (System.currentTimeMillis() - ms < 60_000) t("刚刚") else DateUtils.getRelativeTimeSpanString(ms, System.currentTimeMillis(), DateUtils.MINUTE_IN_MILLIS).toString()

/** 11月3日 */
fun dayText(ms: Long): String {
    val locale = java.util.Locale.getDefault()
    val pattern = android.text.format.DateFormat.getBestDateTimePattern(locale, "MMMd")
    return java.text.SimpleDateFormat(pattern, locale).format(java.util.Date(ms))
}

/** 读到 43% · 还剩 5 小时 — 11月3日开始 */
val BookSummary.recordLine: String
    get() {
        if (record) {
            if (status == ReadingStatus.READING) startedAt?.let { return t("%@开始", dayText(it)) }
            return status.title
        }
        if (progress <= 0) return if (readMinutes > 0u) t("约 %@", hoursText(readMinutes.toInt())) else t("还没读")
        return t("读到 %@", percent) + " · " + remaining
    }

/** The line under a book's title when it isn't being read: when, and how it went. */
val BookSummary.restLine: String
    get() = when (status) {
        ReadingStatus.FINISHED -> finishedAt?.let { t("%@读完", dayText(it)) } ?: t("读过")
        ReadingStatus.PAUSED, ReadingStatus.DROPPED -> {
            val at = if (record) (startedAt ?: addedAt) else (openedAt ?: addedAt)
            val place = if (!record && progress > 0) t("停在 %@", percent) else ""
            listOf(place, agoText(at)).filter { it.isNotEmpty() }.joinToString(" · ")
        }
        else -> recordLine
    }

/** Anything kept about it beyond the file: what 删除 has to ask about. */
val BookSummary.hasRecord: Boolean
    get() = rating != null || comment != null || tags.isNotEmpty() || status == ReadingStatus.FINISHED || status == ReadingStatus.PAUSED || status == ReadingStatus.DROPPED || manual != null

/** How 进度 lists the books in one status. */
enum class BookSort(val key: String) {
    Recent("recent"), Added("added"), Title("title"), Rating("rating"), Color("color");

    val title: String
        get() = when (this) {
            Recent -> t("最近读过"); Added -> t("加入时间"); Title -> t("书名"); Rating -> t("评分"); Color -> t("颜色")
        }

    fun sorted(books: List<BookSummary>): List<BookSummary> {
        fun touched(b: BookSummary) = maxOf(b.openedAt ?: 0, b.manual?.at ?: 0, b.finishedAt ?: 0, b.addedAt)
        val collator = Collator.getInstance()
        return when (this) {
            Recent -> books.sortedByDescending(::touched)
            Added -> books.sortedByDescending { it.addedAt }
            Title -> books.sortedWith { a, b -> collator.compare(a.title, b.title) }
            Rating -> books.sortedWith(compareByDescending<BookSummary> { it.rating?.toInt() ?: 0 }.thenByDescending(::touched))
            Color -> books.sortedWith(compareBy<BookSummary> { CoverColors.key(it.id).first }.thenBy { CoverColors.key(it.id).second }.thenBy { CoverColors.key(it.id).third })
        }
    }

    companion object {
        fun of(key: String) = entries.firstOrNull { it.key == key } ?: Recent
        /** 颜色 only for 读过, whose books stand as strips of their covers' colour. */
        fun offered(status: ReadingStatus) = if (status == ReadingStatus.FINISHED) entries else entries.filter { it != Color }
    }
}

/** A cover's one colour, measured once it's loaded: what 按颜色 sorts by. */
object CoverColors {
    private val tones = mutableMapOf<String, FloatArray>()
    fun set(id: String, hsl: FloatArray) { tones[id] = hsl }
    /** The day's sky — blues, greens, yellows, reds — then the greys, light to dark. */
    fun key(id: String): Triple<Int, Float, Float> {
        val t = tones[id] ?: return Triple(1, -0.85f, 0f)
        if (t[1] <= 0.12f) return Triple(1, -t[2], 0f)
        return Triple(0, (600 - t[0]) % 360, t[2])
    }
}

/** One chapter of the book's contents, and where the reader stands in it. */
data class ChapterRow(val id: Int, val number: Int, val title: String, val section: UInt, val minutes: Int, val state: State) {
    sealed interface State {
        data object Read : State
        data class Current(val fraction: Double) : State
        data object Unread : State
    }
}

object Chapters {
    /** The chapters from the book's contents (top level), or its sections when it has none. */
    fun rows(detail: BookDetail, position: Position?): List<ChapterRow> {
        val sections = detail.sections
        val first = detail.start
        fun isText(i: UInt) = i.toInt() in sections.indices && sections[i.toInt()].kind == SectionKind.TEXT && sections[i.toInt()].linear
        val contentsPage = listOf("目录", "目次", "contents", "table of contents")
        var starts = detail.toc
            .filter { it.depth.toInt() == 0 && it.section >= first && isText(it.section) }
            .filter { it.title.trim().lowercase() !in contentsPage }
            .filter { !it.title.contains(detail.summary.title, ignoreCase = true) || it.title.length > detail.summary.title.length * 3 }
            .map { it.title to it.section }
        if (starts.size < 2) {
            starts = sections.mapIndexedNotNull { i, s ->
                if (i.toUInt() >= first && s.linear && s.kind == SectionKind.TEXT && !s.continued) (s.title ?: t("第 %lld 节", i + 1)) to i.toUInt() else null
            }
        }
        val seen = mutableSetOf<UInt>()
        starts = starts.sortedBy { it.second }.filter { seen.add(it.second) }
        val units = sections.map { maxOf(1, it.units.toInt()).toDouble() }
        val all = maxOf(1.0, units.sum())
        val perUnit = detail.summary.readMinutes.toDouble() / all
        val at = position?.section?.toInt() ?: -1
        return starts.mapIndexed { i, (title, section) ->
            val from = section.toInt()
            val to = if (i + 1 < starts.size) starts[i + 1].second.toInt() else sections.size
            val span = if (from < to) (from until to).toList() else listOf(from)
            val spanUnits = span.sumOf { units.getOrElse(it) { 0.0 } }
            val state = when {
                detail.summary.status == ReadingStatus.FINISHED || at >= to -> ChapterRow.State.Read
                at >= from && position != null -> {
                    val before = span.filter { it < at }.sumOf { units[it] }
                    val inSection = position.sentence.toDouble() / maxOf(1, sections[at].sentences.toInt())
                    ChapterRow.State.Current(minOf(0.99, (before + inSection * units[at]) / maxOf(1.0, spanUnits)))
                }
                else -> ChapterRow.State.Unread
            }
            ChapterRow(i, i + 1, title, section, maxOf(1, Math.round(spanUnits * perUnit).toInt()), state)
        }
    }
}
