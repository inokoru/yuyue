package app.yuyue.me

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.reader.FontShelf
import app.yuyue.t
import uniffi.yuyue_core.FontKind

// 字体 in 设置 › 阅读, as on iPhone and Mac: 永 in each face in a row of tiles — 系统、宋体、
// 霞鹜文楷, the faces you've picked, then 更多. A face coming down fills with ink from the bottom,
// its name turned to the per cent; tapped then, it stops. 更多 opens a sheet over half the page,
// the sample still showing above it: a face tapped there is chosen, fetched, and joins the row.

@Composable
internal fun FontTiles(selected: String, choose: (String) -> Unit) {
    var more by remember { mutableStateOf(false) }
    var asking by remember { mutableStateOf<Confirm?>(null) }
    var row by remember { mutableStateOf(FontShelf.row()) }
    @Suppress("UNUSED_EXPRESSION") FontShelf.version
    val ids = FontShelf.builtIn + row.filter { it !in FontShelf.builtIn }
    Column(Modifier.padding(top = YY.s(12), bottom = YY.s(8)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
        Text(t("字体"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
        val cells: List<String?> = ids + listOf(null)
        Column(verticalArrangement = Arrangement.spacedBy(YY.s(6))) {
            for (line in cells.chunked(5)) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                for (id in line) {
                    if (id == null) MoreTile(Modifier.weight(1f)) { more = true }
                    else FontTile(id, id == selected, Modifier.weight(1f), choose, remove = {
                        FontShelf.removeFromRow(id); row = FontShelf.row()
                        if (id == selected) choose(fallback(id))
                    }, delete = {
                        asking = Confirm(t("删除%@？", FontShelf.name(id)),
                            FontShelf.size(id)?.let { t("这台手机上的 %@ 字体文件会删掉；以后要用再下载。", it) } ?: t("拷进语阅的字体文件会删掉。"), t("删除")) {
                            val was = id == selected
                            FontShelf.delete(id); row = FontShelf.row()
                            if (was) choose(fallback(id))
                        }
                    })
                }
                repeat(5 - line.size) { Spacer(Modifier.weight(1f)) }
            }
        }
        FontLine(selected)
    }
    if (more) MoreFonts(selected, { more = false }) { id ->
        FontShelf.addToRow(id); row = FontShelf.row()
        choose(id)
    }
    ConfirmHost(asking) { asking = null }
}

private fun fallback(id: String) = when (FontShelf.kind(id)) {
    FontKind.HEI -> "system"
    FontKind.KAI, FontKind.HAND -> "wenkai"
    else -> "serif"
}

/** 永 in a face; not here yet, a pale 永 with the ink rising in it as the face comes down. */
@Composable
internal fun InkGlyph(id: String, size: Float, text: String = "永") {
    val ready = FontShelf.isReady(id)
    val family = FontShelf.family(id)
    val p = FontShelf.progress[id] ?: 0f
    Box {
        if (ready) Text(text, fontFamily = family, fontSize = size.sp, color = YY.c.t1)
        else {
            Text(text, fontFamily = family, fontSize = size.sp, color = YY.c.t1.copy(alpha = 0.18f))
            Text(text, fontFamily = family, fontSize = size.sp, color = YY.c.t1, modifier = Modifier.drawWithContent {
                clipRect(top = this.size.height * (1 - p)) { this@drawWithContent.drawContent() }
            })
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun FontTile(id: String, on: Boolean, modifier: Modifier, choose: (String) -> Unit, remove: () -> Unit, delete: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    val p = FontShelf.progress[id]
    Box(modifier) {
        Column(
            Modifier.fillMaxWidth().heightIn(min = YY.s(58)).clip(RoundedCornerShape(YY.s(10))).background(YY.c.card)
                .border(if (on) 1.5.dp else 0.5.dp, if (on) YY.tint.accent else YY.c.line, RoundedCornerShape(YY.s(10)))
                .combinedClickable(onLongClick = { if (id !in FontShelf.builtIn) menu = true }) {
                    if (p != null) FontShelf.cancel(id) else choose(id)
                }
                .padding(vertical = YY.s(8)),
            horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(4)),
        ) {
            InkGlyph(id, 20f)
            Text(p?.let { "${(it * 100).toInt()}%" } ?: FontShelf.name(id), style = YY.font(9f, if (on) FontWeight.SemiBold else FontWeight.Normal),
                color = if (on) YY.c.t1 else YY.c.t2, maxLines = 1)
        }
        DropdownMenu(menu, { menu = false }, containerColor = YY.c.card) {
            DropdownMenuItem({ Text(t("从格子里拿掉"), color = YY.c.t1) }, { menu = false; remove() })
            if (FontShelf.isReady(id)) DropdownMenuItem({ Text(t("删除字体"), color = YY.c.danger) }, { menu = false; delete() })
        }
    }
}

@Composable
private fun MoreTile(modifier: Modifier, open: () -> Unit) {
    Column(
        modifier.heightIn(min = YY.s(58)).clip(RoundedCornerShape(YY.s(10))).border(0.8.dp, YY.c.line, RoundedCornerShape(YY.s(10)))
            .clickable(onClick = open).padding(vertical = YY.s(8)),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(4)),
    ) {
        Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(20)))
        Text(t("更多"), style = YY.font(9f), color = YY.c.t2)
    }
}

/** Under the tiles while the face in use is on its way, or when it didn't come. */
@Composable
private fun FontLine(id: String) {
    val p = FontShelf.progress[id]
    val f = FontShelf.failure[id]
    if (p != null) Text(t("%@下载中 %lld%%，下好就换上", FontShelf.name(id), (p * 100).toInt()), style = YY.font(10f), color = YY.c.t2)
    else if (f != null) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(FontShelf.name(id) + f, style = YY.font(10f), color = YY.c.danger)
        Text(t("重试"), style = YY.font(10f), color = YY.tint.accent, modifier = Modifier.clickable { FontShelf.ensure(id) })
    }
}

private enum class Kind { SONG, KAI, HEI, MINE }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun MoreFonts(selected: String, close: () -> Unit, choose: (String) -> Unit) {
    val context = LocalContext.current
    var kind by remember {
        mutableStateOf(when {
            selected.startsWith("file:") -> Kind.MINE
            else -> when (FontShelf.kind(selected)) { FontKind.HEI -> Kind.HEI; FontKind.KAI, FontKind.HAND -> Kind.KAI; else -> Kind.SONG }
        })
    }
    var failed by remember { mutableStateOf(false) }
    val pick = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        val id = FontShelf.add(context, uri)
        failed = id == null
        if (id != null) { kind = Kind.MINE; choose(id) }
    }
    @Suppress("UNUSED_EXPRESSION") FontShelf.version
    val all = FontShelf.builtIn + FontShelf.catalog.map { it.id }.filter { it !in FontShelf.builtIn }
    val ids = when (kind) {
        Kind.MINE -> FontShelf.files.map { "file:$it" }
        Kind.SONG -> all.filter { FontShelf.kind(it) == FontKind.SONG || FontShelf.kind(it) == FontKind.FANG }
        Kind.KAI -> all.filter { FontShelf.kind(it) == FontKind.KAI || FontShelf.kind(it) == FontKind.HAND }
        Kind.HEI -> all.filter { FontShelf.kind(it) == FontKind.HEI }
    }
    ModalBottomSheet(close, sheetState = rememberModalBottomSheetState(), containerColor = YY.c.bg, scrimColor = Color.Black.copy(alpha = 0.12f)) {
        Column(Modifier.padding(horizontal = YY.s(16)).navigationBarsPadding()) {
            Text(t("更多字体"), style = YY.font(14f, FontWeight.Medium), color = YY.c.t1, modifier = Modifier.padding(bottom = YY.s(10)))
            Row(Modifier.horizontalScroll(rememberScrollState()).padding(bottom = YY.s(8)), horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                for (k in Kind.entries) {
                    val on = k == kind
                    val (mark, family) = when (k) {
                        Kind.SONG -> "宋" to FontFamily.Serif
                        Kind.KAI -> "楷" to FontShelf.family("wenkai")
                        Kind.HEI -> "黑" to FontFamily.SansSerif
                        Kind.MINE -> "" to FontFamily.Default
                    }
                    Row(
                        Modifier.clip(CircleShape).background(YY.c.card).border(if (on) 1.dp else 0.5.dp, if (on) YY.c.t1 else YY.c.line, CircleShape)
                            .clickable { kind = k }.padding(horizontal = YY.s(10), vertical = YY.s(4)),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
                    ) {
                        if (mark.isNotEmpty()) Text(mark, fontFamily = family, fontSize = 14.sp, color = YY.c.t1)
                        Text(when (k) { Kind.SONG -> t("宋体"); Kind.KAI -> t("楷体"); Kind.HEI -> t("黑体"); Kind.MINE -> t("你自己的") },
                            style = YY.font(10.5f, if (on) FontWeight.SemiBold else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t2)
                    }
                }
            }
            Column(Modifier.verticalScroll(rememberScrollState()).padding(bottom = YY.s(16))) {
                for (id in ids) FontLineRow(id, id == selected) { if (FontShelf.progress[id] == null) choose(id) }
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(40)).clip(RoundedCornerShape(YY.s(10)))
                        .clickable { pick.launch(arrayOf("font/ttf", "font/otf", "application/x-font-ttf", "application/x-font-otf", "application/octet-stream", "font/*")) }
                        .padding(horizontal = YY.s(10)),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(16)))
                    Text(t("从文件添加…"), style = YY.font(11.5f), color = YY.c.t2)
                }
                if (failed) Text(t("这个文件不是能用的字体"), style = YY.font(10f), color = YY.c.danger, modifier = Modifier.padding(horizontal = YY.s(10)))
            }
        }
    }
}

@Composable
private fun FontLineRow(id: String, on: Boolean, pick: () -> Unit) {
    val p = FontShelf.progress[id]
    val ready = FontShelf.isReady(id)
    Row(
        Modifier.fillMaxWidth().heightIn(min = YY.s(48)).clip(RoundedCornerShape(YY.s(10)))
            .background(if (on) YY.c.inset.copy(alpha = 0.8f) else Color.Transparent).clickable(onClick = pick).padding(horizontal = YY.s(10)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Column(Modifier.weight(1f)) {
            Text(FontShelf.name(id), fontFamily = FontShelf.family(id), fontSize = 18.sp, color = YY.c.t1)
            val size = FontShelf.size(id) ?: ""
            Text(when {
                id in listOf("system", "serif") -> t("语阅自带")
                id.startsWith("file:") -> t("从文件添加")
                p != null -> t("开源 · %@ · 下载中 %lld%%", size, (p * 100).toInt())
                ready -> t("开源 · %@ · 已下载", size)
                else -> t("开源 · %@", size)
            }, style = YY.font(9.5f), color = YY.c.t3, maxLines = 1)
        }
        if (p != null) CircularProgressIndicator(progress = { p }, color = YY.c.t1, trackColor = YY.c.line, strokeWidth = 2.dp, modifier = Modifier.size(YY.s(15)))
        else if (!ready) Icon(Icons.download, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(15)))
    }
}
