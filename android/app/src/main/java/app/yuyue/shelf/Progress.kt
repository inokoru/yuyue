package app.yuyue.shelf

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridScope
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.pressScale
import app.yuyue.design.rememberPref
import app.yuyue.library.BookSort
import app.yuyue.library.ChapterRow
import app.yuyue.library.Library
import app.yuyue.library.agoText
import app.yuyue.library.dayText
import app.yuyue.library.hasRecord
import app.yuyue.library.hoursText
import app.yuyue.library.percent
import app.yuyue.library.recordLine
import app.yuyue.library.remaining
import app.yuyue.library.restLine
import app.yuyue.library.shelfOrder
import app.yuyue.library.stepTitle
import app.yuyue.library.title
import app.yuyue.t
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.ProgressUnit
import uniffi.yuyue_core.ReadingStatus
import java.util.Calendar

/** What each status looks like: 在读's book, 想读's bookmark… */
val ReadingStatus.icon: ImageVector
    get() = when (this) {
        ReadingStatus.READING -> Icons.book
        ReadingStatus.WANT -> Icons.bookmarkOutline
        ReadingStatus.FINISHED -> Icons.checkCircle
        ReadingStatus.PAUSED -> Icons.pauseCircle
        ReadingStatus.DROPPED -> Icons.xmarkCircle
    }

/** The faint ink under the thing chosen; the stars not given. */
private val chosenInk @Composable get() = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color(0x122A2B2E)
internal val starOff @Composable get() = if (YY.dark) Color(0xFF4E4C49) else Color(0xFFD8D4CE)

/**
 * 进度: 在读 · 想读 · 读过 · 搁置 · 弃读, then a card for each book — the files on this phone and
 * the ones kept only as a record. 读过 stands as strips of their covers' colour, a row a year.
 */
fun LazyGridScope.progressItems(
    library: Library, status: ReadingStatus, setStatus: (ReadingStatus) -> Unit,
    show: (String) -> Unit, pick: (BookSummary) -> Unit,
) {
    item(span = { GridItemSpan(maxLineSpan) }) {
        var sortKey by rememberPref("books.sort", "recent")
        val sort = BookSort.of(sortKey)
        val counts = library.keptBooks.groupingBy { it.status }.eachCount()
        val these = library.keptBooks.filter { it.status == status }
        // 颜色 is 读过's own; elsewhere a stored 颜色 reads as 最近读过.
        val books = (if (sort == BookSort.Color && status != ReadingStatus.FINISHED) BookSort.Recent else sort).sorted(these)
        var deleting by remember { mutableStateOf<BookSummary?>(null) }
        val delete = { b: BookSummary -> if (b.hasRecord || b.record) deleting = b else library.delete(b) }
        Column(Modifier.padding(top = 18.dp)) {
            StatusTabs(status, counts, setStatus)
            if (books.isNotEmpty()) {
                // How many in this one, and their order: the shelf's 全部 row.
                Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(t("%lld 本", books.size), fontSize = 13.sp, color = YY.c.t3)
                    Spacer(Modifier.weight(1f))
                    SortMenu(sort.title, BookSort.offered(status).map { it.title to (it == sort) }) { i -> sortKey = BookSort.offered(status)[i].key }
                }
            }
            // Another status: its list fades in where the last one was.
            androidx.compose.animation.AnimatedContent(status, transitionSpec = { app.yuyue.design.YYMotion.swap() }, label = "status") { shownStatus ->
            // The list of the status being shown, so the one going keeps its own books as it fades.
            val status = shownStatus
            val books = (if (sort == BookSort.Color && status != ReadingStatus.FINISHED) BookSort.Recent else sort).sorted(library.keptBooks.filter { it.status == status })
            when {
                books.isEmpty() -> Text(
                    when (status) {
                        ReadingStatus.READING -> t("没有在读的书")
                        ReadingStatus.WANT -> t("还没有想读的书，右上角 ＋ 加一本")
                        ReadingStatus.FINISHED -> t("读完的书会在这里")
                        ReadingStatus.PAUSED -> t("没有搁置的书")
                        ReadingStatus.DROPPED -> t("没有弃读的书")
                    },
                    fontSize = 13.sp, color = YY.c.t3, modifier = Modifier.fillMaxWidth().padding(top = 60.dp), textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                )
                status == ReadingStatus.FINISHED -> FinishedStrips(books, library, show, pick, delete)
                else -> Column(Modifier.padding(top = 12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    for (b in books) BookRecordCard(b, library, show = { show(b.id) }, read = { library.open(b) }, pick = { pick(b) }, delete = { delete(b) })
                }
            }
            }
        }
        deleting?.let { b -> DeleteRecordDialog(library, b) { deleting = null } }
    }
}

/** The five, each with how many: the name, a grey count, a faint ink under the one shown. */
@Composable
private fun StatusTabs(status: ReadingStatus, counts: Map<ReadingStatus, Int>, set: (ReadingStatus) -> Unit) {
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
        for (s in shelfOrder) {
            val on = s == status
            val ground by androidx.compose.animation.animateColorAsState(if (on) chosenInk else chosenInk.copy(alpha = 0f), app.yuyue.design.YYMotion.quick(), label = "statusTab")
            Row(
                Modifier.heightIn(min = 28.dp).clip(RoundedCornerShape(8.dp)).background(ground).clickable { set(s) }.padding(horizontal = 10.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(s.title, fontSize = 13.5.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = YY.c.t1)
                Text("${counts[s] ?: 0}", fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
            }
        }
    }
}

/** A grey name and ⌃⌄ that opens the orders to pick from. */
@Composable
internal fun SortMenu(name: String, options: List<Pair<String, Boolean>>, pick: (Int) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(Modifier.clickable { open = true }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(name, fontSize = 13.sp, color = YY.c.t2)
            Icon(PlaceIcons.switch, null, tint = YY.c.t2, modifier = Modifier.size(14.dp))
        }
        DropdownMenu(open, { open = false }, containerColor = YY.c.card) {
            options.forEachIndexed { i, (title, on) ->
                DropdownMenuItem(text = { Text(title, color = YY.c.t1) }, trailingIcon = { if (on) Icon(Icons.check, null, tint = YY.c.t1) }, onClick = { open = false; pick(i) })
            }
        }
    }
}

/** A book on 进度: its cover down the left; the title in 宋体, who and when; where it's got to, a
 *  chapter at a time; and at the bottom how long is left, 目录 and the one thing to do next. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun BookRecordCard(book: BookSummary, library: Library, show: () -> Unit, read: () -> Unit, pick: () -> Unit, delete: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val rows = remember(book.id, book.progress) { library.chapters(book) }
    val current = rows.firstOrNull { it.state is ChapterRow.State.Current }
    val shape = RoundedCornerShape(14.dp)
    val press = remember { MutableInteractionSource() }
    Box(Modifier.fillMaxWidth().pressScale(press, 0.98f).clip(shape).background(YY.c.card).border(0.5.dp, YY.c.line, shape)
        .combinedClickable(press, null, onLongClick = { menu = true }, onClick = show)) {
        Row(Modifier.fillMaxWidth().heightIn(min = 136.dp).height(androidx.compose.foundation.layout.IntrinsicSize.Min)) {
            // The cover fills the card's left side, edge to edge, with a hairline after it.
            Box(Modifier.width(100.dp).fillMaxHeight()) { CoverFill(book) }
            Box(Modifier.width(0.5.dp).fillMaxHeight().background(YY.c.line))
            Column(Modifier.weight(1f).padding(start = 14.dp, end = 12.dp, top = 12.dp, bottom = 12.dp)) {
                Text(book.title, fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = 16.5.sp, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(end = 34.dp))
                val byline = listOfNotNull(book.authors.firstOrNull(), when {
                    book.record -> t("纸书")
                    book.openedAt != null && (book.status == ReadingStatus.READING || book.status == ReadingStatus.PAUSED) -> agoText(book.openedAt!!)
                    else -> null
                }).filter { it.isNotEmpty() }.joinToString(" · ")
                Text(byline, fontSize = 11.5.sp, color = YY.c.t3, maxLines = 1, modifier = Modifier.padding(top = 2.dp))
                Column(Modifier.padding(top = 9.dp)) { Standing(book, rows, current) }
                Spacer(Modifier.weight(1f).heightIn(min = 10.dp))
                Bottom(book, library, show, read)
            }
        }
        // The corner: what the book is now, and a small ⌄; it opens the menu to change it.
        Box(Modifier.align(Alignment.TopEnd).padding(8.dp)) {
            Row(Modifier.size(34.dp, 24.dp).clip(RoundedCornerShape(7.dp)).clickable(onClickLabel = t("改状态")) { menu = true },
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(3.dp, Alignment.CenterHorizontally)) {
                Icon(book.status.icon, null, tint = YY.c.t3, modifier = Modifier.size(14.dp))
                Icon(Icons.chevronDown, null, tint = YY.c.t3, modifier = Modifier.size(10.dp))
            }
            StatusMenu(menu, { menu = false }, book, library, pick, delete)
        }
    }
}

@Composable
private fun Standing(book: BookSummary, rows: List<ChapterRow>, current: ChapterRow?) {
    when (book.status) {
        ReadingStatus.FINISHED -> {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                book.rating?.let { StarLine(it.toInt(), 10.5f) }
                Text(book.restLine, fontSize = 11.5.sp, color = YY.c.t2)
            }
            book.comment?.let { Text(it, fontFamily = YY.serif, fontSize = 12.5.sp, lineHeight = 18.sp, color = YY.c.t2, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 5.dp)) }
        }
        ReadingStatus.WANT -> Text(wantLine(book, rows), fontSize = 11.5.sp, color = YY.c.t3)
        else -> if (book.record && book.manual == null) Text(t("还没记读到哪"), fontSize = 11.5.sp, color = YY.c.t3) else {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(buildAnnotatedString {
                    withStyle(SpanStyle(color = YY.c.t3)) { append(t("读到 ")) }
                    withStyle(SpanStyle(color = YY.c.t1, fontWeight = FontWeight.Medium)) { append(whereLine(book, current)) }
                }, fontSize = 11.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                Text(book.percent, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent, style = YY.tnum, modifier = Modifier.padding(start = 4.dp))
            }
            ChapterBar(if (book.record) emptyList() else rows, book.progress, Modifier.padding(top = 6.dp))
        }
    }
}

/** 第 8 章 · Chapter 8 — 148 / 320 页 — 13% */
private fun whereLine(book: BookSummary, current: ChapterRow?): String {
    book.manual?.let { m ->
        val v = m.value.toInt(); val n = m.total.toInt()
        return when (m.unit) {
            ProgressUnit.PAGE -> if (n > 0) t("%lld / %lld 页", v, n) else t("第 %lld 页", v)
            ProgressUnit.PERCENT -> "$v%"
            ProgressUnit.CHAPTER -> if (n > 0) t("第 %lld / %lld 章", v, n) else t("第 %lld 章", v)
        }
    }
    current ?: return book.percent
    val number = t("第 %lld 章", current.number)
    return if (current.title.isEmpty()) number else "$number · ${current.title}"
}

/** 共 3 章 · 约 7 小时 — 共 168 页 */
private fun wantLine(book: BookSummary, rows: List<ChapterRow>): String {
    if (book.record) {
        val m = book.manual
        return if (m != null && m.unit == ProgressUnit.PAGE && m.total > 0u) t("共 %lld 页", m.total.toInt()) else t("纸书")
    }
    val parts = buildList {
        if (rows.isNotEmpty()) add(t("共 %lld 章", rows.size))
        if (book.readMinutes > 0u) add(t("约 %@", hoursText(book.readMinutes.toInt())))
    }
    return if (parts.isEmpty()) t("还没读") else parts.joinToString(" · ")
}

/** The bottom: how long is left, 目录, the next thing. */
@Composable
private fun Bottom(book: BookSummary, library: Library, show: () -> Unit, read: () -> Unit) {
    val left = when (book.status) {
        ReadingStatus.READING, ReadingStatus.PAUSED, ReadingStatus.DROPPED ->
            if (book.record) book.manual?.let { agoText(it.at) + t("记的") } ?: "" else if (book.progress > 0) book.remaining else ""
        ReadingStatus.WANT -> agoText(book.addedAt) + t("加的")
        ReadingStatus.FINISHED -> ""
    }
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(left, fontSize = 11.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        when (book.status) {
            ReadingStatus.FINISHED -> if (book.rating == null) RecordButton(t("打分"), false, compact = true, action = show)
            ReadingStatus.WANT -> {
                RecordButton(t("目录"), false, compact = true, action = show)
                RecordButton(t("开始读"), true, compact = true) { if (book.record) library.setStatus(book, ReadingStatus.READING) else read() }
            }
            else -> if (book.record) {
                RecordButton(t("记到…"), false, compact = true, action = show)
                book.stepTitle?.let { RecordButton("+$it", true, compact = true) { library.stepManualProgress(book) } }
            } else {
                RecordButton(t("目录"), false, compact = true, action = show)
                RecordButton(t("接着读"), true, compact = true, action = read)
            }
        }
    }
}

/** 在读 ✓ · 想读 · 读过 · 搁置 · 弃读 — 放进合集… · 删除… */
@Composable
internal fun StatusMenu(open: Boolean, dismiss: () -> Unit, book: BookSummary, library: Library, pick: (() -> Unit)?, delete: (() -> Unit)?) {
    DropdownMenu(open, dismiss, containerColor = YY.c.card) {
        for (s in shelfOrder) {
            DropdownMenuItem(
                text = { Text(s.title, color = YY.c.t1, fontWeight = if (s == book.status) FontWeight.SemiBold else FontWeight.Normal) },
                leadingIcon = { Icon(if (s == book.status) Icons.check else s.icon, null, tint = YY.c.t2) },
                onClick = { dismiss(); library.setStatus(book, s) },
            )
        }
        if (pick != null || delete != null) HorizontalDivider(color = YY.c.line)
        pick?.let { DropdownMenuItem(text = { Text(t("放进合集…"), color = YY.c.t1) }, leadingIcon = { Icon(Icons.inbox, null, tint = YY.c.t2) }, onClick = { dismiss(); it() }) }
        delete?.let { DropdownMenuItem(text = { Text(t("删除…"), color = YY.c.danger) }, leadingIcon = { Icon(Icons.delete, null, tint = YY.c.danger) }, onClick = { dismiss(); it() }) }
    }
}

/** 删除 a book with a record: keep the record, or let it all go. */
@Composable
internal fun DeleteRecordDialog(library: Library, book: BookSummary, dismiss: () -> Unit) {
    AlertDialog(
        onDismissRequest = dismiss,
        containerColor = YY.c.card,
        title = { Text(t("删除《%@》", book.title), color = YY.c.t1) },
        text = { Text(if (book.record) t("评分、短评和读书记录都会一起删掉。") else t("留下记录的话，评分、短评和读到哪都还在“书”里。"), color = YY.c.t2) },
        confirmButton = {
            Column(horizontalAlignment = Alignment.End) {
                if (!book.record) TextButton({ dismiss(); library.keepRecordOnly(book) }) { Text(t("只删文件，留下记录"), color = YY.c.t1) }
                TextButton({ dismiss(); library.delete(book) }) { Text(t("连记录一起删"), color = YY.c.danger) }
                TextButton(dismiss) { Text(t("取消"), color = YY.c.t2) }
            }
        },
    )
}

/** How far a book has got, a chapter at a time: read chapters in the theme's colour, the one being
 *  read filled as far as it's got, the rest pale. One chapter, or very many, is one line. */
@Composable
internal fun ChapterBar(rows: List<ChapterRow>, progress: Double, modifier: Modifier = Modifier) {
    val empty = if (YY.dark) Color(0xFF36373A) else Color(0xFFEBE9E5)
    val fill = YY.tint.fill
    if (rows.size in 2..40) {
        Row(modifier.fillMaxWidth().height(5.dp), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
            for (r in rows) BoxWithConstraints(Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(2.dp)).background(empty)) {
                when (val s = r.state) {
                    ChapterRow.State.Read -> Box(Modifier.fillMaxSize().background(fill))
                    is ChapterRow.State.Current -> Box(Modifier.width(maxWidth * maxOf(0.15, s.fraction).toFloat()).fillMaxHeight().background(fill))
                    ChapterRow.State.Unread -> {}
                }
            }
        }
    } else {
        BoxWithConstraints(modifier.fillMaxWidth().height(5.dp).clip(CircleShape).background(empty)) {
            Box(Modifier.width(maxOf(5.dp, maxWidth * minOf(1.0, progress).toFloat())).fillMaxHeight().clip(CircleShape).background(fill))
        }
    }
}

/** The buttons on 书: the main one the theme's dark with its words light, the other warm white.
 *  Rounded squares, not pills. */
@Composable
internal fun RecordButton(title: String, main: Boolean, compact: Boolean = false, big: Boolean = false, modifier: Modifier = Modifier, icon: ImageVector? = null, action: () -> Unit) {
    val tint = YY.tint
    val height = if (big) 44.dp else if (compact) 28.dp else 32.dp
    val radius = if (big) 12.dp else if (compact) 9.dp else 10.dp
    val shape = RoundedCornerShape(radius)
    val press = remember { MutableInteractionSource() }
    Row(
        modifier.pressScale(press, 0.97f).heightIn(min = height).clip(shape).background(if (main) tint.black else YY.c.pill)
            .border(0.5.dp, if (main) tint.edge else YY.c.pillLine, shape).clickable(press, null, onClick = action)
            .padding(horizontal = if (big) 18.dp else if (compact) 10.dp else 12.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp, Alignment.CenterHorizontally),
    ) {
        val color = if (main) tint.onBlack else YY.c.t1
        if (icon != null) Icon(icon, null, tint = color, modifier = Modifier.size(15.dp))
        if (title.isNotEmpty()) Text(title, fontSize = if (big) 14.5.sp else if (compact) 12.sp else 13.sp, fontWeight = if (main) FontWeight.SemiBold else FontWeight.Normal, color = color, style = YY.tnum, maxLines = 1)
    }
}

/** 你的评分: five stars, tap one to give that many, the same again to take it back. */
@Composable
internal fun StarRow(rating: Int?, size: Float = 17f, set: (Int?) -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(3.dp)) {
        for (n in 1..5) {
            val press = remember { MutableInteractionSource() }
            Icon(Icons.starFill, t("%lld 星", n), tint = if ((rating ?: 0) >= n) YY.c.t1 else starOff,
                modifier = Modifier.pressScale(press, 0.9f).size((size + 3).dp).clickable(press, null) { set(if (rating == n) null else n) })
        }
    }
}

/** ★★★★☆, not to be tapped. */
@Composable
internal fun StarLine(rating: Int, size: Float = 11f) {
    Row(horizontalArrangement = Arrangement.spacedBy(1.dp)) {
        for (n in 1..5) Icon(Icons.starFill, null, tint = if (rating >= n) YY.c.t1 else starOff, modifier = Modifier.size((size + 2).dp))
    }
}

/** 读过: a row a year, a book a tall narrow strip of its cover's colour, the title down it, the day
 *  it was finished on top. A tap opens its page; holding it, the same menu as a card's. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FinishedStrips(books: List<BookSummary>, library: Library, show: (String) -> Unit, pick: (BookSummary) -> Unit, delete: (BookSummary) -> Unit) {
    fun year(b: BookSummary) = Calendar.getInstance().apply { timeInMillis = b.finishedAt ?: b.openedAt ?: b.addedAt }.get(Calendar.YEAR)
    val years = books.groupBy(::year).toSortedMap(compareByDescending { it })
    val borderColor = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.06f)
    Column {
        for ((y, list) in years) {
            Row(Modifier.fillMaxWidth().padding(top = 22.dp, bottom = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("$y", fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 19.sp, color = YY.c.t1, style = YY.tnum, modifier = Modifier.alignByBaseline())
                Text(t("%lld 本", list.size), fontSize = 13.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline())
                Spacer(Modifier.weight(1f))
                val minutes = list.sumOf { (it.readMinutes + it.listenMinutes).toInt() }
                if (minutes > 0) Text(t("约 %@", hoursText(minutes)), fontSize = 13.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline())
            }
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                for (b in list) {
                    var menu by remember { mutableStateOf(false) }
                    val press = remember { MutableInteractionSource() }
                    val shape = RoundedCornerShape(9.dp)
                    Box {
                        Box(Modifier.pressScale(press, 0.97f).size(44.dp, 232.dp).clip(shape).border(0.5.dp, borderColor, shape)
                            .combinedClickable(press, null, onLongClick = { menu = true }) { show(b.id) }) {
                            CoverStrip(b, b.finishedAt?.let(::dayText), 44.dp, 232.dp, Modifier.fillMaxSize())
                        }
                        StatusMenu(menu, { menu = false }, b, library, { pick(b) }, { delete(b) })
                    }
                }
            }
        }
    }
}

/** 加一本书: a title, and it's in. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddBookSheet(library: Library, status: ReadingStatus, done: () -> Unit) {
    var title by remember { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    fun add() {
        val name = title.trim()
        if (name.isEmpty()) return
        library.addRecordBook(name, status)
        done()
    }
    ModalBottomSheet(done, containerColor = YY.c.bg) {
        Column(Modifier.padding(start = 20.dp, end = 20.dp, bottom = 28.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Box(Modifier.size(36.dp, 52.dp).clip(RoundedCornerShape(3.dp)).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(3.dp)), contentAlignment = Alignment.Center) {
                    Text(t("书"), fontFamily = YY.serif, fontSize = 15.sp, color = YY.c.t3)
                }
                Box(Modifier.weight(1f)) {
                    if (title.isEmpty()) Text(t("书名"), fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = 19.sp, color = YY.c.t3)
                    BasicTextField(
                        title, { title = it }, singleLine = true,
                        textStyle = TextStyle(fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = 19.sp, color = YY.c.t1),
                        cursorBrush = SolidColor(YY.c.t1),
                        keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done), keyboardActions = KeyboardActions(onDone = { add() }),
                        modifier = Modifier.fillMaxWidth().focusRequester(focus),
                    )
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(t("放进 %@", status.title), fontSize = 12.sp, color = YY.c.t3, modifier = Modifier.weight(1f))
                RecordButton(t("加入"), true) { add() }
            }
        }
    }
    LaunchedEffect(Unit) { focus.requestFocus() }
}
