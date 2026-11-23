package app.yuyue.me

import android.os.StatFs
import android.text.format.Formatter
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import app.yuyue.companion.CompanionConfig
import app.yuyue.companion.CompanionProvider
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.YYSegments
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.reader.ForeignPrefs
import app.yuyue.shelf.BookCover
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.BookFormat
import java.io.File
import kotlinx.coroutines.launch
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.clickable
import androidx.compose.foundation.ExperimentalFoundationApi

@Composable
private fun Toggle(on: Boolean, change: (Boolean) -> Unit, enabled: Boolean = true) {
    Switch(on, change, enabled = enabled, colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line))
}

@Composable
private fun TwoLines(title: String, detail: String?, modifier: Modifier = Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
        Text(title, style = YY.font(11.5f), color = YY.c.t1)
        detail?.let { Text(it, style = YY.font(10f), color = YY.c.t3, maxLines = 1) }
    }
}

// MARK: 外文

/** 外文, as the Mac's: saying a word aloud, 难词注释, the 生词 marks,
 *  and translation. */
@Composable
fun ForeignPage(core: uniffi.yuyue_core.YuyueCore) {
    var speak by rememberPref(ForeignPrefs.SPEAK, true)
    var accent by rememberPref(ForeignPrefs.ACCENT, "en-GB")
    var mark by rememberPref(ForeignPrefs.MARK, true)
    var gloss by rememberPref(ForeignPrefs.GLOSS, true)
    var level by rememberPref(ForeignPrefs.LEVEL, 3)
    var glossVocab by rememberPref(ForeignPrefs.GLOSS_VOCAB, false)
    YYPage {
        SettingsLead(t("查词、难词注释、生词与离线翻译"))
        YYLabel(t("查词"))
        YYGroup {
            row { YYRow(t("查词时自动发音"), trailing = { Toggle(speak, { speak = it }) }) }
            row {
                YYRow(t("英文发音"), trailing = {
                    YYSegments(listOf("en-GB" to t("英音"), "en-US" to t("美音")), accent, { accent = it }, small = true, modifier = Modifier.widthIn(min = YY.s(110)))
                })
            }
        }
        YYLabel(t("MDict 词典"))
        MDictList(core)
        YYNote(t("按列表顺序优先查词，未命中时使用内置离线英汉词典（支持词形还原）；长按可移除。"))
        YYLabel(t("难词注释"))
        YYGroup {
            row { DetailSwitch(t("难词行间释义"), t("阅读英文书时，在较难词汇上方标注简短中文词义"), gloss) { gloss = it } }
            row {
                // The five levels get the row's whole width: their English names are long.
                Column(Modifier.fillMaxWidth().padding(vertical = YY.s(8)), verticalArrangement = Arrangement.spacedBy(YY.s(6))) {
                    Row {
                        Text(t("词汇难度"), style = YY.font(11.5f), color = YY.c.t1, modifier = Modifier.weight(1f).alignByBaseline())
                        Text(t("等级越高，仅标注更生僻的词"), style = YY.font(10f), color = YY.c.t3, modifier = Modifier.alignByBaseline())
                    }
                    YYSegments(GLOSS_LEVELS.map { it.first to t(it.second) }, level, { if (gloss && !glossVocab) level = it }, small = true,
                        modifier = Modifier.fillMaxWidth().alpha(if (gloss && !glossVocab) 1f else 0.5f))
                }
            }
            row { YYRow(t("仅标注生词本中的词"), trailing = { Toggle(glossVocab, { glossVocab = it }, enabled = gloss) }) }
            row {
                Column(Modifier.padding(vertical = YY.s(6))) {
                    Text(t("预览"), style = YY.font(10f), color = YY.c.t3)
                    Row(verticalAlignment = Alignment.Bottom, modifier = Modifier.padding(top = YY.s(4))) {
                        val serif = FontFamily.Serif
                        Text("It is a truth universally ", style = YY.font(12f, family = serif), color = YY.c.t1)
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Text("公认", style = YY.font(9f), color = if (gloss && !glossVocab && level <= 4) YY.c.t2 else YY.c.card.copy(alpha = 0f))
                            Text("acknowledged", style = YY.font(12f, family = serif), color = YY.c.t1)
                        }
                        Text(", that a…", style = YY.font(12f, family = serif), color = YY.c.t1, maxLines = 1)
                    }
                }
            }
        }
        YYLabel(t("生词"))
        YYGroup {
            row {
                Row(Modifier.fillMaxWidth().padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(3))) {
                        Text(t("在书中标记生词"), style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1)
                        Text(buildAnnotatedString {
                            append("…this truth is so well ")
                            withStyle(SpanStyle(color = YY.c.t1, textDecoration = if (mark) TextDecoration.Underline else null)) { append("fixed") }
                            append(" in the minds…")
                        }, style = YY.font(11f, family = FontFamily.Serif), color = YY.c.t2)
                    }
                    Toggle(mark, { mark = it })
                }
            }
        }
        YYNote(t("「在学」生词在所有书中以虚线标出，标为「已掌握」后不再标记。"))
        YYLabel(t("翻译"))
        YYGroup {
            val companion = CompanionConfig.current?.takeIf { it.isReady }
            row { YYRow(t("翻译"), value = companion?.provider?.title ?: t("书伴或翻译 App")) }
        }
        YYNote(t("选中一句点“翻译”，用书伴模型、系统翻译或手机上的翻译 App 来翻。"))
    }
}

/** 难词注释's levels, as on Apple (`ForeignPrefs.levels`). */
internal val GLOSS_LEVELS = listOf(2 to "四级", 3 to "六级", 4 to "考研", 5 to "雅思", 6 to "GRE")


// MARK: 存储


private class Usage(
    val total: Long, val free: Long, val books: List<Pair<String, Long>>, val deleted: Int, val deletedBytes: Long,
    val audio: Long, val model: Long, val companion: Long,
)

private fun bytes(f: File): Long = if (f.isFile) f.length() else f.listFiles()?.sumOf(::bytes) ?: 0

private fun measure(library: Library): Usage {
    val active = library.books.map { it.id }.toSet()
    val dirs = File(library.dataDir, "books").listFiles()?.filter { !it.name.startsWith(".") } ?: emptyList()
    val sizes = dirs.map { it.name to bytes(it) }
    val (kept, gone) = sizes.partition { it.first in active }
    return Usage(
        bytes(library.dataDir), StatFs(library.dataDir.path).availableBytes, kept.sortedByDescending { it.second }, gone.size, gone.sumOf { it.second },
        audio = bytes(File(library.dataDir, "audiobooks")), model = bytes(app.yuyue.speech.Aligner.file(library.dataDir)),
        companion = bytes(File(library.dataDir, "companion")),
    )
}

/** 存储, as the Mac's: what 语阅 takes, the books deleted (kept 30 days), and each book from
 *  the largest. */
@Composable
fun StoragePage(library: Library) {
    val context = LocalContext.current
    var version by remember { mutableIntStateOf(0) }
    var usage by remember { mutableStateOf<Usage?>(null) }
    LaunchedEffect(version, library.books) { usage = withContext(Dispatchers.IO) { measure(library) } }
    fun size(b: Long) = Formatter.formatShortFileSize(context, b)
    val u = usage
    val bookBytes = u?.books?.sumOf { it.second } ?: 0
    val audio = u?.audio ?: 0
    val model = u?.model ?: 0
    // As the Mac's bar: books, recordings, the model, what's deleted, the rest; the empty ones left out.
    val parts = listOf(t("书文件") to bookBytes, t("有声书录音") to audio, t("对齐模型") to model, t("已删除") to (u?.deletedBytes ?: 0),
        t("其他") to maxOf(0, (u?.total ?: 0) - bookBytes - audio - model - (u?.deletedBytes ?: 0)))
        .filterIndexed { i, p -> i == 0 || p.second > 0 }
    val fill = YY.tint.fill
    val colors = mapOf(t("书文件") to fill, t("有声书录音") to fill.copy(alpha = 0.55f), t("对齐模型") to YY.c.t3,
        t("已删除") to YY.c.danger.copy(alpha = 0.7f), t("其他") to YY.c.tint)
    YYPage {
        SettingsLead(t("书库、有声书与本地缓存占用的空间"))
        Column(Modifier.padding(top = YY.s(10), bottom = YY.s(14)).fillMaxWidth().background(YY.c.card, RoundedCornerShape(YY.s(16))).padding(horizontal = YY.s(14), vertical = YY.s(13))) {
            Text(t("已用空间"), style = YY.font(10.5f), color = YY.c.t3)
            Row {
                val (number, unit) = (u?.let { size(it.total) } ?: "…").let { s -> s.substringBeforeLast(' ', s) to s.substringAfterLast(' ', "") }
                Text(number, style = YY.font(26f, FontWeight.Bold, tnum = true), color = YY.c.t1, modifier = Modifier.alignByBaseline())
                if (unit.isNotEmpty() && unit != number) {
                    Text(unit, style = YY.font(12f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.padding(start = YY.s(3)).alignByBaseline())
                }
                Spacer(Modifier.weight(1f))
                u?.let { Text(t("本机可用 %@", size(it.free)), style = YY.font(10f, tnum = true), color = YY.c.t3, modifier = Modifier.alignByBaseline()) }
            }
            val total = maxOf(1L, parts.sumOf { it.second })
            Row(Modifier.padding(top = YY.s(10)).fillMaxWidth().height(YY.s(10)).background(YY.c.inset, RoundedCornerShape(YY.s(5))), horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                parts.forEachIndexed { i, (_, b) ->
                    if (b > 0) Box(Modifier.weight(maxOf(0.01f, b.toFloat() / total)).height(YY.s(10)).background(colors[parts[i].first] ?: YY.c.tint))
                }
            }
            Column(Modifier.padding(top = YY.s(12)), verticalArrangement = Arrangement.spacedBy(YY.s(7))) {
                for (pair in parts.chunked(2)) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(16))) {
                    for ((label, b) in pair) Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                        Box(Modifier.size(YY.s(8)).background(colors[label] ?: YY.c.tint, CircleShape))
                        Text(label, style = YY.font(10.5f), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                        Text(size(b), style = YY.font(10.5f, FontWeight.Medium, tnum = true), color = YY.c.t1)
                    }
                }
            }
        }
        // Each thing that can be thrown away is a card: what it is, then the red words that do it, after asking.
        var asking by remember { mutableStateOf<String?>(null) }
        YYGroup {
            row { DetailRow(t("最近删除 · %lld 本（%@）", u?.deleted ?: 0, size(u?.deletedBytes ?: 0)), t("保留 30 天以便恢复，到期自动清理")) }
            row { DestructiveRow(t("立即清空"), enabled = (u?.deleted ?: 0) > 0) { asking = "deleted" } }
        }
        if (model > 0) {
            Spacer(Modifier.height(YY.s(12)))
            YYGroup {
                row { DetailRow(t("语音对齐模型 · %@", size(model)), t("用于有声书句级对齐；移除后下次合成时重新下载（约 300 MB）")) }
                row { DestructiveRow(t("移除")) { asking = "model" } }
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row { DetailRow(t("书伴问答缓存 · %@", size(u?.companion ?: 0)), t("同章节重复提问直接读取本地缓存，不重复调用模型")) }
            row { DestructiveRow(t("清理"), enabled = (u?.companion ?: 0) > 0) { asking = "companion" } }
        }
        when (asking) {
            "deleted" -> ConfirmDialog(t("清空最近删除的 %lld 本书？", u?.deleted ?: 0), t("清空后文件将彻底删除，无法恢复。"), t("立即清空"),
                { runCatching { library.core.purgeDeleted(0) }; version++ }) { asking = null }
            "model" -> ConfirmDialog(t("移除语音对齐模型？"), t("已合成的有声书不受影响；下次合成时将重新下载（约 300 MB）。"), t("移除"),
                { app.yuyue.speech.Aligner.file(library.dataDir).delete(); version++ }) { asking = null }
            "companion" -> ConfirmDialog(t("清理书伴问答缓存？"), t("清理后，相同问题或前情回顾将重新请求模型。"), t("清理"),
                { File(library.dataDir, "companion").deleteRecursively(); version++ }) { asking = null }
        }
        if (u != null && u.books.isNotEmpty()) {
            YYLabel(t("各书占用空间"))
            YYGroup {
                for ((id, b) in u.books.take(30)) {
                    val book = library.books.firstOrNull { it.id == id } ?: continue
                    row {
                        Row(Modifier.fillMaxWidth().padding(vertical = YY.s(7)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                            BookCover(book, Modifier.width(YY.s(24)))
                            TwoLines(book.title, when (book.format) { BookFormat.PDF -> "PDF"; BookFormat.TXT -> "TXT"; BookFormat.MOBI -> "MOBI"; BookFormat.EPUB -> "EPUB"; BookFormat.AUDIO -> t("有声书"); BookFormat.PODCAST -> t("播客") }, Modifier.weight(1f))
                            Text(size(b), style = YY.font(11f, FontWeight.SemiBold, tnum = true), color = YY.c.t1)
                        }
                    }
                }
            }
        }
    }
}

/** 导入 MDict 词典: the ones imported, on or off, 导入 from the files on the phone. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun MDictList(core: uniffi.yuyue_core.YuyueCore) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var list by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf(runCatching { core.dictionaries() }.getOrDefault(emptyList())) }
    var importing by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf<String?>(null) }
    var failed by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf<String?>(null) }
    fun reload() { list = runCatching { core.dictionaries() }.getOrDefault(emptyList()) }
    val picker = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        scope.launch {
            failed = null
            for (uri in uris) {
                val name = uri.lastPathSegment?.substringAfterLast('/')?.substringBeforeLast('.') ?: "MDict"
                importing = name
                val result = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                    runCatching {
                        // A copy the core can read by path; gone once it has been read in.
                        val file = java.io.File(context.cacheDir, "import-$name.mdx")
                        context.contentResolver.openInputStream(uri)?.use { input -> file.outputStream().use { input.copyTo(it) } }
                        try { core.importDictionary(file.path) } finally { file.delete() }
                    }
                }
                result.exceptionOrNull()?.let { failed = it.message }
                reload()
            }
            importing = null
        }
    }
    var menuFor by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf<String?>(null) }
    var removing by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf<uniffi.yuyue_core.DictionaryInfo?>(null) }
    /** 往上挪 / 往下挪 — a word is looked up in this order. */
    fun move(id: String, step: Int) {
        val ids = list.map { it.id }.toMutableList()
        val i = ids.indexOf(id)
        if (i < 0 || i + step !in ids.indices) return
        java.util.Collections.swap(ids, i, i + step)
        runCatching { core.orderDictionaries(ids) }
        reload()
    }
    YYGroup {
        for ((n, d) in list.withIndex()) {
            row {
                // A long press opens a menu: removing one isn't a single slip of the finger away.
                Box(Modifier.combinedClickable(onClick = {}, onLongClick = { menuFor = d.id })) {
                    YYRow(d.title, value = t("%lld 个词条", d.entries.toLong()), trailing = {
                        Toggle(d.enabled, { on -> runCatching { core.setDictionary(d.id, on) }; reload() })
                    })
                    androidx.compose.material3.DropdownMenu(menuFor == d.id, { menuFor = null }, containerColor = YY.c.card) {
                        if (n > 0) app.yuyue.shelf.Item(t("往上挪"), Icons.arrowUp) { menuFor = null; move(d.id, -1) }
                        if (n < list.size - 1) app.yuyue.shelf.Item(t("往下挪"), Icons.arrowDown) { menuFor = null; move(d.id, 1) }
                        app.yuyue.shelf.Item(t("移除"), Icons.delete, danger = true) { menuFor = null; removing = d }
                    }
                }
            }
        }
        row {
            Row(Modifier.fillMaxWidth().clickable(enabled = importing == null) { picker.launch(arrayOf("*/*")) }.padding(vertical = YY.s(10)),
                verticalAlignment = Alignment.CenterVertically) {
                Text(importing?.let { t("正在导入 %@…", it) } ?: t("导入词典（.mdx）"), style = YY.font(11.5f), color = YY.tint.accent)
            }
        }
        failed?.let { f -> row { Text(f, style = YY.font(10.5f), color = YY.c.danger, modifier = Modifier.padding(vertical = YY.s(6))) } }
    }
    removing?.let { d ->
        ConfirmDialog(t("移除“%@”？", d.title), t("查词时不再检索；词典文件将从语阅中删除，需要时可重新导入。"), t("移除"),
            { runCatching { core.removeDictionary(d.id) }; reload() }) { removing = null }
    }
}
