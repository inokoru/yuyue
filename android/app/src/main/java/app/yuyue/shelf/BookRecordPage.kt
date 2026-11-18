package app.yuyue.shelf

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons as MIcons
import androidx.compose.material.icons.outlined.Description
import androidx.compose.material.icons.outlined.Schedule
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.rememberPref
import app.yuyue.library.ChapterRow
import app.yuyue.library.Library
import app.yuyue.library.agoText
import app.yuyue.library.dayText
import app.yuyue.library.hoursText
import app.yuyue.library.recordLine
import app.yuyue.library.remaining
import app.yuyue.library.stepTitle
import app.yuyue.library.title
import app.yuyue.t
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.Mark
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.ProgressUnit
import uniffi.yuyue_core.ReadingStatus

private enum class RecordTab { Contents, About, Notes, Log }

/** 书的详情, as on iPhone: the cover and your score at the top, when and the status to change, the
 *  one thing to do next; a paper book's 读到; then 目录 (a list or squares), 简介, 笔记 (your line on
 *  it, tags, what you marked) and 记录. */
@Composable
fun BookRecordPage(library: Library, bookID: String, back: () -> Unit) {
    val book = library.book(bookID)
    var picking by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    app.yuyue.me.SubPage("", back) {
        if (book == null) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Text(t("这本书不在了"), fontSize = 13.sp, color = YY.c.t3) }
            return@SubPage
        }
        Record(library, book, pick = { picking = true }, delete = { deleting = true })
    }
    if (book != null && picking) CollectionPickerSheet(library, book.id) { picking = false }
    if (book != null && deleting) DeleteRecordDialog(library, book) { deleting = false; if (library.book(bookID) == null) back() }
}

@Composable
private fun Record(library: Library, book: BookSummary, pick: () -> Unit, delete: () -> Unit) {
    var tab by remember(book.id) { mutableStateOf(if (book.record) RecordTab.Notes else RecordTab.Contents) }
    val rows = remember(book.id, book.progress) { library.chapters(book) }
    val current = rows.firstOrNull { it.state is ChapterRow.State.Current }
    val marks = remember(book.id, book.progress) { runCatching { library.core.marks(book.id) }.getOrDefault(emptyList()).sortedByDescending { it.at } }
    val about = remember(book.id) { if (book.record) null else runCatching { library.core.bookDescription(book.id) }.getOrNull() }
    val open = { intent: Library.OpenIntent? -> library.open(book, intent) }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 40.dp + app.yuyue.LocalTabBarHeight.current)) {
        // The cover; the title in 宋体, who, what it is; your score.
        Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            BookCover(book, Modifier.width(104.dp))
            Column(Modifier.weight(1f)) {
                Text(book.title, fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = 20.sp, lineHeight = 27.sp, color = YY.c.t1, maxLines = 3, overflow = TextOverflow.Ellipsis)
                if (book.authors.isNotEmpty()) Text(book.authors.joinToString(" · "), fontSize = 13.sp, color = YY.c.t2, modifier = Modifier.padding(top = 4.dp))
                Text(meta(book, rows), fontSize = 11.5.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.padding(top = 3.dp))
                Row(Modifier.padding(top = 12.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(t("你的评分"), fontSize = 11.sp, color = YY.c.t3)
                    StarRow(book.rating?.toInt(), 17f) { library.setRating(book, it) }
                }
            }
        }
        // When, and the status to change.
        Row(Modifier.padding(top = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(standing(library, book).ifEmpty { book.recordLine }, fontSize = 12.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.weight(1f))
            var menu by remember { mutableStateOf(false) }
            Box {
                RecordButton(book.status.title, false, big = true, icon = book.status.icon) { menu = true }
                StatusMenu(menu, { menu = false }, book, library, pick, delete)
            }
        }
        // The one thing to do next: 接着读 · 第 15 章, 开始读, 重读, +20 页.
        var noting by remember(book.id) { mutableStateOf(false) }
        Row(Modifier.padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (book.record) {
                RecordButton("", false, big = true, icon = Icons.edit) { noting = !noting }
                val m = book.manual
                val step = book.stepTitle
                if (step != null && m != null) {
                    val to = minOf(if (m.total == 0u) Int.MAX_VALUE else m.total.toInt(), m.value.toInt() + maxOf(1, m.step.toInt()))
                    RecordButton(t("+%@ · 读到 %lld", step, to), true, big = true, modifier = Modifier.weight(1f)) { library.stepManualProgress(book) }
                } else RecordButton(t("记读到哪"), true, big = true, modifier = Modifier.weight(1f)) { noting = true }
            } else {
                val next = when {
                    book.status == ReadingStatus.FINISHED -> t("重读")
                    book.status == ReadingStatus.WANT && book.progress == 0.0 -> t("开始读")
                    current != null -> t("接着读 · 第 %lld 章", current.number)
                    else -> t("接着读")
                }
                RecordButton(next, true, big = true, modifier = Modifier.weight(1f)) { open(null) }
            }
        }
        if (book.record) PaperProgress(library, book, noting) { noting = it }
        // 目录 · 简介 · 笔记 · 记录
        val log = remember(book, marks, current) { log(library, book, marks, current) }
        Row(Modifier.padding(top = 20.dp).fillMaxWidth().horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(2.dp)) {
            if (!book.record) TabChip(Icons.book, t("目录"), rows.size.takeIf { it > 0 }, tab == RecordTab.Contents) { tab = RecordTab.Contents }
            TabChip(MIcons.Outlined.Description, t("简介"), null, tab == RecordTab.About) { tab = RecordTab.About }
            TabChip(Icons.highlighter, t("笔记"), marks.size + (if (book.comment == null) 0 else 1), tab == RecordTab.Notes) { tab = RecordTab.Notes }
            TabChip(MIcons.Outlined.Schedule, t("记录"), log.size, tab == RecordTab.Log) { tab = RecordTab.Log }
        }
        androidx.compose.animation.AnimatedContent(tab, Modifier.padding(top = 14.dp), transitionSpec = { app.yuyue.design.YYMotion.swap() }, label = "recordTab") { tab ->
            when (tab) {
                RecordTab.Contents -> Contents(rows) { row ->
                    // The chapter being read opens where it was left; any other at its start.
                    if (row.state is ChapterRow.State.Current) open(null) else open(Library.OpenIntent.At(Position(row.section, 0u)))
                }
                RecordTab.About -> AboutPage(library, book, rows, about)
                RecordTab.Notes -> Notes(library, book, marks) { m -> open(Library.OpenIntent.At(Position(m.section, m.start.sentence))) }
                RecordTab.Log -> Column {
                    for ((at, text, bold) in log) {
                        Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                            Text(java.text.SimpleDateFormat("MM-dd", java.util.Locale.getDefault()).format(java.util.Date(at)), fontSize = 11.5.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.widthIn(min = 44.dp))
                            Text(buildAnnotatedString {
                                withStyle(SpanStyle(color = YY.c.t2)) { append(text) }
                                withStyle(SpanStyle(color = YY.c.t1, fontWeight = FontWeight.Medium)) { append(bold) }
                            }, fontSize = 12.5.sp, style = YY.tnum)
                        }
                        HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
                    }
                }
            }
        }
    }
}

/** EPUB · 34 章 · 约 9 小时 — 纸书 · 320 页 */
private fun meta(book: BookSummary, rows: List<ChapterRow>): String {
    if (book.record) {
        val m = book.manual
        return listOfNotNull(t("纸书"), if (m != null && m.unit == ProgressUnit.PAGE && m.total > 0u) t("%lld 页", m.total.toInt()) else null).joinToString(" · ")
    }
    val label = when (book.format) {
        BookFormat.EPUB -> "EPUB"; BookFormat.TXT -> "TXT"; BookFormat.PDF -> "PDF"; BookFormat.MOBI -> "Kindle"
        BookFormat.AUDIO -> t("有声书"); BookFormat.PODCAST -> t("播客")
    }
    return listOfNotNull(label, if (rows.isNotEmpty()) t("%lld 章", rows.size) else null, if (book.readMinutes > 0u) t("约 %@", hoursText(book.readMinutes.toInt())) else null).joinToString(" · ")
}

/** 10月28日开始 · 读了 6 小时 · 还剩 5 小时 */
private fun standing(library: Library, book: BookSummary): String = buildList {
    book.startedAt?.let { add(t("%@开始", dayText(it))) }
    if (book.status == ReadingStatus.FINISHED) book.finishedAt?.let { add(t("%@读完", dayText(it))) }
    if (!book.record) {
        val (read, listen) = library.timeSpent(book)
        if (read > 0) add(t("读了 %@", duration(read)))
        if (listen > 0) add(t("听了 %@", duration(listen)))
        if (book.status != ReadingStatus.FINISHED && book.progress > 0) add(book.remaining)
    }
}.joinToString(" · ")

/** 6 小时 · 40 分钟 */
private fun duration(minutes: Int): String {
    if (minutes < 60) return t("%lld 分钟", maxOf(1, minutes))
    val h = minutes / 60; val m = minutes % 60
    return if (m == 0 || h >= 10) t("%lld 小时", h) else t("%lld 小时 %lld 分", h, m)
}

/** 目录 · 简介 · 笔记 · 记录: an icon, the name, a grey count; a faint ink under the one shown. */
@Composable
private fun TabChip(icon: ImageVector, title: String, count: Int?, on: Boolean, action: () -> Unit) {
    val chosen = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color(0x122A2B2E)
    val ground by androidx.compose.animation.animateColorAsState(if (on) chosen else chosen.copy(alpha = 0f), app.yuyue.design.YYMotion.quick(), label = "recordChip")
    Row(
        Modifier.heightIn(min = 28.dp).clip(RoundedCornerShape(8.dp)).background(ground)
            .clickable(onClick = action).padding(horizontal = 8.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(icon, null, tint = if (on) YY.c.t1 else YY.c.t2, modifier = Modifier.size(15.dp))
        Text(title, fontSize = 12.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = YY.c.t1)
        if (count != null) Text("$count", fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
    }
}

/** 纸书: how far, by hand. */
@Composable
private fun PaperProgress(library: Library, book: BookSummary, noting: Boolean, setNoting: (Boolean) -> Unit) {
    Column(Modifier.padding(top = 22.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(t("读到"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.weight(1f))
            book.manual?.let { Text(agoText(it.at), fontSize = 12.sp, color = YY.c.t3, style = YY.tnum) }
        }
        val m = book.manual
        if (m != null && !noting) {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text("${m.value}", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, style = YY.tnum, modifier = Modifier.alignByBaseline())
                Text(when (m.unit) {
                    ProgressUnit.PAGE -> if (m.total > 0u) t("/ %lld 页", m.total.toInt()) else t("页")
                    ProgressUnit.PERCENT -> "%"
                    ProgressUnit.CHAPTER -> if (m.total > 0u) t("/ %lld 章", m.total.toInt()) else t("章")
                }, fontSize = 14.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline())
            }
            if (m.total > 0u) ChapterBar(emptyList(), book.progress)
        } else {
            var unit by remember(book.id) { mutableStateOf(m?.unit ?: ProgressUnit.PAGE) }
            var value by remember(book.id) { mutableStateOf(m?.value?.toString() ?: "") }
            var total by remember(book.id) { mutableStateOf(m?.takeIf { it.total > 0u && it.unit != ProgressUnit.PERCENT }?.total?.toString() ?: "") }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                NumberField(value, if (unit == ProgressUnit.PERCENT) "0–100" else t("读到")) { value = it }
                if (unit != ProgressUnit.PERCENT) {
                    Text("/", color = YY.c.t3)
                    NumberField(total, t("共")) { total = it }
                }
                Row(Modifier.clip(RoundedCornerShape(8.dp)).background(YY.c.inset).padding(2.dp)) {
                    for ((u, name) in listOf(ProgressUnit.PAGE to t("页"), ProgressUnit.PERCENT to "%", ProgressUnit.CHAPTER to t("章"))) {
                        Box(Modifier.clip(RoundedCornerShape(6.dp)).background(if (u == unit) YY.c.card else Color.Transparent).clickable { unit = u }.padding(horizontal = 10.dp, vertical = 5.dp)) {
                            Text(name, fontSize = 12.sp, color = YY.c.t1)
                        }
                    }
                }
                Spacer(Modifier.weight(1f))
                RecordButton(t("记下"), true) {
                    library.setManualProgress(book, unit, value.filter(Char::isDigit).toIntOrNull() ?: 0, total.filter(Char::isDigit).toIntOrNull() ?: 0)
                    setNoting(false)
                }
            }
        }
    }
}

@Composable
private fun NumberField(text: String, hint: String, change: (String) -> Unit) {
    Box(Modifier.width(64.dp).height(32.dp).clip(RoundedCornerShape(8.dp)).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(8.dp)).padding(horizontal = 8.dp), contentAlignment = Alignment.CenterStart) {
        if (text.isEmpty()) Text(hint, fontSize = 14.sp, color = YY.c.t3)
        BasicTextField(text, change, singleLine = true, textStyle = TextStyle(fontSize = 14.sp, color = YY.c.t1), cursorBrush = SolidColor(YY.c.t1),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
    }
}

/** 目录 as the page at the front of a book — the title, a run of dots, how long — or as squares. */
@Composable
private fun Contents(rows: List<ChapterRow>, open: (ChapterRow) -> Unit) {
    if (rows.isEmpty()) { Text(t("这本书没有目录"), fontSize = 12.sp, color = YY.c.t3); return }
    var grid by rememberPref("book.contentsAsGrid", false)
    var expanded by remember { mutableStateOf(false) }
    Column {
        Row(Modifier.fillMaxWidth().padding(bottom = 6.dp), horizontalArrangement = Arrangement.End) {
            for ((asGrid, icon) in listOf(false to Icons.list, true to Icons.grid)) {
                Box(Modifier.size(28.dp).clip(RoundedCornerShape(7.dp)).background(if (grid == asGrid) YY.c.inset else Color.Transparent).clickable { grid = asGrid }, contentAlignment = Alignment.Center) {
                    Icon(icon, null, tint = if (grid == asGrid) YY.c.t1 else YY.c.t2, modifier = Modifier.size(16.dp))
                }
            }
        }
        if (grid) {
            FlowRow(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                for (r in rows) {
                    val fill = YY.tint.fill
                    Box(Modifier.size(30.dp).clip(RoundedCornerShape(5.dp)).background(when (r.state) { ChapterRow.State.Read -> fill; else -> YY.c.inset })
                        .border(if (r.state is ChapterRow.State.Current) 1.5.dp else 0.dp, fill, RoundedCornerShape(5.dp)).clickable { open(r) }, contentAlignment = Alignment.Center) {
                        Text("${r.number}", fontSize = 10.5.sp, color = if (r.state == ChapterRow.State.Read) YY.tint.onBlack else YY.c.t2, style = YY.tnum)
                    }
                }
            }
        } else {
            val shown = if (!expanded && rows.size > 9) {
                val at = rows.indexOfFirst { it.state is ChapterRow.State.Current }.coerceAtLeast(0)
                val from = (at - 3).coerceIn(0, rows.size - 9)
                rows.subList(from, from + 9)
            } else rows
            for (r in shown) {
                Row(Modifier.fillMaxWidth().clickable { open(r) }.padding(vertical = 9.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("${r.number}", fontSize = 12.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.widthIn(min = 26.dp))
                    Text(r.title, fontFamily = YY.serif, fontSize = 14.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
                        color = if (r.state == ChapterRow.State.Read) YY.c.t3 else YY.c.t1, fontWeight = if (r.state is ChapterRow.State.Current) FontWeight.SemiBold else FontWeight.Normal, modifier = Modifier.weight(1f))
                    Text(when (val s = r.state) {
                        is ChapterRow.State.Current -> "${Math.round(s.fraction * 100)}%"
                        ChapterRow.State.Read -> "✓"
                        ChapterRow.State.Unread -> t("%lld 分钟", r.minutes)
                    }, fontSize = 11.5.sp, color = if (r.state is ChapterRow.State.Current) YY.tint.accent else YY.c.t3, style = YY.tnum)
                }
            }
            if (shown.size < rows.size) Text(t("全部 %lld 章", rows.size), fontSize = 12.5.sp, color = YY.c.t2, style = YY.tnum, modifier = Modifier.padding(top = 6.dp).clickable { expanded = true })
        }
    }
}

@Composable
private fun AboutPage(library: Library, book: BookSummary, rows: List<ChapterRow>, about: String?) {
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        if (about != null) Text(about, fontFamily = YY.serif, fontSize = 13.5.sp, lineHeight = 22.sp, color = YY.c.t1)
        else Text(if (book.record) t("纸书没有简介，可以在笔记里写几句。") else t("这本书没有附简介。"), fontSize = 12.5.sp, color = YY.c.t3)
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            InfoLine(t("作者"), book.authors.joinToString(" · "))
            if (!book.record) InfoLine(t("格式"), meta(book, rows))
            InfoLine(t("加入"), dayText(book.addedAt))
        }
        Tags(library, book)
    }
}

@Composable
private fun InfoLine(name: String, value: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Text(name, fontSize = 11.5.sp, color = YY.c.t3, modifier = Modifier.widthIn(min = 48.dp))
        Text(value.ifEmpty { "—" }, fontSize = 12.5.sp, color = YY.c.t1, style = YY.tnum)
    }
}

/** Your tags: tap one to take it off; ＋ to add. */
@Composable
private fun Tags(library: Library, book: BookSummary) {
    var newTag by remember { mutableStateOf("") }
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (tag in book.tags) {
            Box(Modifier.height(26.dp).clip(RoundedCornerShape(7.dp)).background(YY.c.pill).border(0.5.dp, YY.c.pillLine, RoundedCornerShape(7.dp))
                .clickable(onClickLabel = t("点一下拿掉")) { library.setTag(book, tag, false) }.padding(horizontal = 9.dp), contentAlignment = Alignment.Center) {
                Text(tag, fontSize = 12.sp, color = YY.c.t1)
            }
        }
        Row(Modifier.height(26.dp).border(0.5.dp, YY.c.pillLine, RoundedCornerShape(7.dp)).padding(horizontal = 9.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(3.dp)) {
            Icon(Icons.add, null, tint = YY.c.t3, modifier = Modifier.size(12.dp))
            Box(Modifier.width(64.dp)) {
                if (newTag.isEmpty()) Text(t("标签"), fontSize = 12.sp, color = YY.c.t3)
                BasicTextField(newTag, { newTag = it }, singleLine = true, textStyle = TextStyle(fontSize = 12.sp, color = YY.c.t1), cursorBrush = SolidColor(YY.c.t1),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                    keyboardActions = KeyboardActions(onDone = { newTag.trim().takeIf { it.isNotEmpty() }?.let { library.setTag(book, it, true) }; newTag = "" }))
            }
        }
    }
}

/** 短评 (only you see it), your tags, then what you marked. */
@Composable
private fun Notes(library: Library, book: BookSummary, marks: List<Mark>, open: (Mark) -> Unit) {
    var comment by remember(book.id) { mutableStateOf(book.comment ?: "") }
    fun save() { val c = comment.trim(); if (c != (book.comment ?: "")) library.setComment(book, c) }
    Column {
        Row {
            Text(t("短评"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(t("只有你看得到"), fontSize = 12.sp, color = YY.c.t3)
        }
        Box(Modifier.padding(top = 8.dp).fillMaxWidth().heightIn(min = 60.dp).clip(RoundedCornerShape(12.dp)).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(12.dp)).padding(horizontal = 12.dp, vertical = 10.dp)) {
            if (comment.isEmpty()) Text(t("写一句（可以不写）"), fontFamily = YY.serif, fontSize = 13.5.sp, color = YY.c.t3)
            BasicTextField(comment, { comment = it }, textStyle = TextStyle(fontFamily = YY.serif, fontSize = 13.5.sp, lineHeight = 21.sp, color = YY.c.t1), cursorBrush = SolidColor(YY.c.t1),
                modifier = Modifier.fillMaxWidth().onFocusChanged { if (!it.isFocused) save() })
        }
        Box(Modifier.padding(top = 12.dp)) { Tags(library, book) }
        if (marks.isNotEmpty()) {
            Row(Modifier.padding(top = 22.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(t("划线"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.weight(1f))
                Text(t("%lld 条", marks.size), fontSize = 12.sp, color = YY.c.t3, style = YY.tnum)
            }
            for ((i, m) in marks.take(30).withIndex()) {
                Column(Modifier.fillMaxWidth().clickable { open(m) }.padding(vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(m.text, fontFamily = YY.serif, fontSize = 13.sp, lineHeight = 20.sp, color = YY.c.t1, maxLines = 4, overflow = TextOverflow.Ellipsis)
                    m.note?.let { Text(it, fontSize = 12.sp, color = YY.c.t2, maxLines = 3) }
                    Text(dayText(m.at), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
                }
                if (i < minOf(30, marks.size) - 1) HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
            }
        }
    }
}

/** 记录: what happened to this book, newest first. */
private fun log(library: Library, book: BookSummary, marks: List<Mark>, current: ChapterRow?): List<Triple<Long, String, String>> {
    val out = mutableListOf(Triple(book.addedAt, t("加进来"), ""))
    book.startedAt?.let { out += Triple(it, t("开始读"), "") }
    book.manual?.let { out += Triple(it.at, t("记到 "), book.recordLine.replace(t("读到 "), "")) }
    val p = runCatching { library.core.latestProgress(book.id) }.getOrNull()
    if (p != null && current != null) out += Triple(p.at, t("读到 "), t("第 %lld 章", current.number) + " · " + current.title)
    marks.groupBy { java.util.Calendar.getInstance().apply { timeInMillis = it.at; set(java.util.Calendar.HOUR_OF_DAY, 0); set(java.util.Calendar.MINUTE, 0); set(java.util.Calendar.SECOND, 0); set(java.util.Calendar.MILLISECOND, 0) }.timeInMillis }
        .forEach { (day, list) -> out += Triple(day + 1, t("划了 %lld 句", list.size), "") }
    book.finishedAt?.let { out += Triple(it, t("读完"), book.rating?.let { r -> "★".repeat(r.toInt()) } ?: "") }
    if (book.status == ReadingStatus.PAUSED || book.status == ReadingStatus.DROPPED) (book.openedAt ?: book.manual?.at)?.let { out += Triple(it + 1, book.status.title, "") }
    return out.sortedByDescending { it.first }
}
