package app.yuyue.me

import android.content.SharedPreferences
import androidx.compose.foundation.BorderStroke
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
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Appearance
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYPage
import app.yuyue.design.YYPaper
import app.yuyue.design.YYRow
import app.yuyue.design.YYSegments
import app.yuyue.design.YYSlider
import app.yuyue.design.YYTints
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.reader.DoNotDisturb
import app.yuyue.reader.EInk
import app.yuyue.reader.ForeignPrefs
import app.yuyue.reader.ImmersivePrefs
import app.yuyue.reader.PaperEditor
import app.yuyue.reader.Papers
import app.yuyue.reader.TextureLayer
import app.yuyue.reader.WenKai
import app.yuyue.reader.WenKaiStatus
import app.yuyue.reader.customTokens
import app.yuyue.reader.paperChoice
import app.yuyue.reader.paperPresets
import app.yuyue.reader.paperTokens
import app.yuyue.reader.tokens
import app.yuyue.speech.Player
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.CustomPaper
import uniffi.yuyue_core.Font
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.Paper
import uniffi.yuyue_core.PaperTexture
import uniffi.yuyue_core.ReaderSettings
import java.text.DateFormat
import java.util.Calendar
import java.util.Date
import java.util.UUID
import kotlin.math.roundToInt

// The pieces the Mac's settings pages are made of, at phone size.

/** What a page holds, in a grey line under its title — the Mac's line under each page's name. */
@Composable
internal fun SettingsLead(text: String) {
    Text(text, style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.padding(start = YY.s(4), end = YY.s(4), top = YY.s(6), bottom = YY.s(2)))
}

@Composable
internal fun SettingSwitch(on: Boolean, change: ((Boolean) -> Unit)?, enabled: Boolean = true) {
    Switch(on, change, enabled = enabled, colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line))
}

/** A setting's name with a grey line under it and its control at the end — the Mac's SettingsRow. */
@Composable
internal fun DetailRow(title: String, detail: String?, modifier: Modifier = Modifier, titleWeight: FontWeight = FontWeight.Normal, control: @Composable () -> Unit = {}) {
    Row(
        modifier.fillMaxWidth().heightIn(min = YY.s(38)).padding(vertical = YY.s(if (detail == null) 4 else 8)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
            Text(title, style = YY.font(11.5f, titleWeight), color = YY.c.t1)
            detail?.let { Text(it, style = YY.font(10f).copy(lineHeight = YY.sp(14f)), color = YY.c.t3) }
        }
        control()
    }
}

/** A switch with its name and line; the whole row turns it. */
@Composable
internal fun DetailSwitch(title: String, detail: String?, on: Boolean, enabled: Boolean = true, set: (Boolean) -> Unit) {
    DetailRow(title, detail, Modifier.toggleable(on, enabled = enabled, role = Role.Switch, onValueChange = set).alpha(if (enabled) 1f else 0.5f)) {
        SettingSwitch(on, null, enabled)
    }
}

/** Tiles in a grid of [columns], the last row left short. */
@Composable
private fun <T> TileGrid(items: List<T>, columns: Int, tile: @Composable (T, Modifier) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(YY.s(6))) {
        for (line in items.chunked(columns)) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
            for (item in line) tile(item, Modifier.weight(1f))
            repeat(columns - line.size) { Spacer(Modifier.weight(1f)) }
        }
    }
}

/** `.tile`: a hairline box, the chosen one ringed in the theme colour (the ring moves over quickly). */
@Composable
private fun Modifier.settingsTile(on: Boolean, line: Color, accent: Color): Modifier {
    val width by androidx.compose.animation.core.animateDpAsState(if (on) 1.5.dp else 0.5.dp, app.yuyue.design.YYMotion.quick(), label = "tileRing")
    val ring by androidx.compose.animation.animateColorAsState(if (on) accent else line, app.yuyue.design.YYMotion.quick(), label = "tileInk")
    return clip(RoundedCornerShape(9.dp)).border(width, ring, RoundedCornerShape(9.dp))
}

// MARK: 通用

object General {
    const val REOPEN_LAST = "general.reopenLast"
}

/** 通用, as the Mac's: what opening 语阅 does, and how books come in — 书库来源 and Wi‑Fi 传书
 *  being the phone's ways in, where the Mac takes them dragged onto its window. */
@Composable
fun GeneralPage(library: Library, push: (MePage) -> Unit) {
    var reopenLast by rememberPref(General.REOPEN_LAST, false)
    val activity = LocalContext.current as android.app.Activity
    var language by remember { mutableStateOf(app.yuyue.AppLanguage.chosen(activity)) }
    val systemChinese = remember(activity) { app.yuyue.AppLanguage.systemChinese(activity) }
    YYPage {
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                LanguageSettingRow(language, systemChinese) { picked ->
                    if (language != picked) {
                        language = picked
                        app.yuyue.AppLanguage.choose(activity, picked)
                    }
                }
            }
            row { DetailSwitch(t("启动时继续上次阅读"), t("关闭则进入书架"), reopenLast) { reopenLast = it } }
        }
        YYLabel(t("导入"))
        YYGroup {
            row {
                Column(Modifier.padding(vertical = YY.s(10)), verticalArrangement = Arrangement.spacedBy(YY.s(3))) {
                    Text(t("在文件管理中打开或分享至语阅即可导入"), style = YY.font(11.5f), color = YY.c.t1)
                    Text(t("文件会复制到书库，移动或删除原文件不受影响"), style = YY.font(10f), color = YY.c.t3)
                    Text("EPUB  PDF  MOBI  AZW3  TXT", style = YY.font(9.5f).copy(fontFamily = FontFamily.Monospace), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(2)))
                }
            }
            row { YYRow(t("书库来源"), Icons.server, if (library.sources.all.isEmpty()) null else t("%lld 个来源", library.sources.all.size), chevron = true) { push(MePage.Sources) } }
            row { YYRow(t("Wi‑Fi 传书"), Icons.wifi, null, chevron = true) { push(MePage.Wifi) } }
        }
    }
}

/** 设置 › 通用 › 语言: warm-paper seal trigger + floating warm-paper dropdown matching macOS. */
@Composable
private fun LanguageSettingRow(choice: String, systemChinese: Boolean, pick: (String) -> Unit) {
    var menuOpen by remember { mutableStateOf(false) }
    val dark = YY.dark
    val systemName = if (systemChinese) "中文" else "English"
    val choiceName = when (choice) {
        app.yuyue.AppLanguage.ZH -> "中文"
        app.yuyue.AppLanguage.EN -> "English"
        else -> t("跟随系统")
    }
    val subtitle = if (choice == app.yuyue.AppLanguage.SYSTEM) {
        if (systemChinese) t("当前：中文") else t("当前：English")
    } else {
        t("仅用于语阅界面")
    }
    val triggerFill = when {
        dark && menuOpen -> Color(0xFF303237)
        dark -> YY.c.inset
        menuOpen -> YY.c.card
        else -> YY.c.bg
    }
    val triggerBorder = when {
        menuOpen -> YY.tint.edge
        dark -> Color.White.copy(alpha = 0.10f)
        else -> Color(0x2450412D)
    }
    val systemTagFill = if (dark) Color.White.copy(alpha = 0.09f) else Color(0x0F2A2B2E)

    Row(
        Modifier.fillMaxWidth().heightIn(min = YY.s(42)).padding(vertical = YY.s(8)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1.5f))) {
            Text(t("语言"), style = YY.font(11.5f), color = YY.c.t1)
            Text(subtitle, style = YY.font(10f), color = YY.c.t3, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
        Box {
            Row(
                Modifier
                    .width(176.dp)
                    .height(34.dp)
                    .shadow(if (dark) 0.dp else 1.dp, RoundedCornerShape(9.dp), ambientColor = Color.Black.copy(alpha = 0.06f), spotColor = Color.Black.copy(alpha = 0.06f))
                    .clip(RoundedCornerShape(9.dp))
                    .background(triggerFill)
                    .border(if (menuOpen) 1.dp else 0.5.dp, triggerBorder, RoundedCornerShape(9.dp))
                    .clickable { menuOpen = !menuOpen }
                    .padding(start = 6.dp, end = 10.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                LanguageSealBadge(choice, onTrigger = true)
                Text(choiceName, fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1)
                if (choice == app.yuyue.AppLanguage.SYSTEM) {
                    Text(
                        systemName,
                        fontSize = 10.5.sp,
                        fontWeight = FontWeight.Medium,
                        color = YY.c.t2,
                        maxLines = 1,
                        modifier = Modifier
                            .clip(CircleShape)
                            .background(systemTagFill)
                            .padding(horizontal = 6.dp, vertical = 1.5.dp),
                    )
                }
                Spacer(Modifier.weight(1f))
                UpDownChevrons(YY.c.t3)
            }

            val menuBg = if (dark) Color(0xFF26272B) else Color(0xFFFCFBF8)
            val menuBorder = if (dark) Color.White.copy(alpha = 0.12f) else Color(0x1F332E29)
            DropdownMenu(
                expanded = menuOpen,
                onDismissRequest = { menuOpen = false },
                offset = DpOffset((-42).dp, 6.dp),
                shape = RoundedCornerShape(12.dp),
                containerColor = menuBg,
                tonalElevation = 0.dp,
                shadowElevation = 12.dp,
                border = BorderStroke(0.5.dp, menuBorder),
                modifier = Modifier.width(218.dp),
            ) {
                Column(Modifier.padding(horizontal = 5.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    LanguagePaperDropdownRow(
                        language = app.yuyue.AppLanguage.ZH,
                        title = "中文",
                        detail = t("简体中文界面"),
                        isSelected = choice == app.yuyue.AppLanguage.ZH,
                    ) {
                        menuOpen = false
                        pick(app.yuyue.AppLanguage.ZH)
                    }
                    LanguagePaperDropdownRow(
                        language = app.yuyue.AppLanguage.EN,
                        title = "English",
                        detail = "English interface",
                        isSelected = choice == app.yuyue.AppLanguage.EN,
                    ) {
                        menuOpen = false
                        pick(app.yuyue.AppLanguage.EN)
                    }
                    Box(
                        Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 8.dp, vertical = 4.dp)
                            .height(0.5.dp)
                            .background(YY.c.line),
                    )
                    LanguagePaperDropdownRow(
                        language = app.yuyue.AppLanguage.SYSTEM,
                        title = t("跟随系统"),
                        detail = if (systemChinese) t("当前：中文") else t("当前：English"),
                        isSelected = choice == app.yuyue.AppLanguage.SYSTEM,
                    ) {
                        menuOpen = false
                        pick(app.yuyue.AppLanguage.SYSTEM)
                    }
                }
            }
        }
    }
}

@Composable
private fun LanguagePaperDropdownRow(
    language: String,
    title: String,
    detail: String,
    isSelected: Boolean,
    onClick: () -> Unit,
) {
    val dark = YY.dark
    val rowFill = if (isSelected) {
        if (dark) YY.tint.accent.copy(alpha = 0.18f) else YY.tint.soft
    } else {
        Color.Transparent
    }
    Row(
        Modifier
            .fillMaxWidth()
            .height(42.dp)
            .clip(RoundedCornerShape(8.dp))
            .background(rowFill)
            .clickable(onClick = onClick)
            .semantics { selected = isSelected }
            .padding(horizontal = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        LanguageSealBadge(language, highlighted = isSelected)
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(
                title,
                fontSize = 13.sp,
                fontWeight = if (isSelected) FontWeight.SemiBold else FontWeight.Medium,
                color = if (isSelected) YY.tint.accent else YY.c.t1,
                maxLines = 1,
            )
            Text(
                detail,
                fontSize = 10.5.sp,
                color = if (isSelected) YY.tint.accent.copy(alpha = 0.78f) else YY.c.t3,
                maxLines = 1,
            )
        }
        if (isSelected) {
            Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(14.dp))
        }
    }
}

/** The 20 × 20 warm-paper seal emblem holding 文, Aa, or the phone glyph. */
@Composable
private fun LanguageSealBadge(language: String, highlighted: Boolean = false, onTrigger: Boolean = false) {
    val dark = YY.dark
    val fill = when {
        highlighted -> if (dark) YY.tint.accent.copy(alpha = 0.20f) else YY.c.card
        onTrigger -> if (dark) Color.White.copy(alpha = 0.08f) else YY.c.card
        else -> if (dark) Color.White.copy(alpha = 0.07f) else YY.c.bg
    }
    val border = when {
        highlighted -> YY.tint.edge
        onTrigger -> if (dark) Color.White.copy(alpha = 0.11f) else YY.c.line
        else -> if (dark) Color.White.copy(alpha = 0.10f) else YY.c.line
    }
    val glyphColor = when {
        highlighted -> YY.tint.accent
        else -> if (dark) YY.c.t1.copy(alpha = 0.82f) else YY.c.t2
    }
    Box(
        Modifier
            .size(20.dp)
            .clip(RoundedCornerShape(5.5.dp))
            .background(fill)
            .border(0.5.dp, border, RoundedCornerShape(5.5.dp)),
        contentAlignment = Alignment.Center,
    ) {
        when (language) {
            app.yuyue.AppLanguage.ZH -> Text(
                "文",
                fontFamily = YY.serif,
                fontSize = 11.5.sp,
                fontWeight = FontWeight.SemiBold,
                color = glyphColor,
            )
            app.yuyue.AppLanguage.EN -> Text(
                "Aa",
                fontFamily = FontFamily.Serif,
                fontStyle = FontStyle.Italic,
                fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold,
                color = glyphColor,
            )
            else -> androidx.compose.foundation.Canvas(Modifier.size(12.5.dp)) {
                val k = size.minDimension / 34f
                val dx = (size.width - 34f * k) / 2f
                val dy = (size.height - 34f * k) / 2f
                val p = androidx.compose.ui.graphics.Path().apply {
                    addRoundRect(androidx.compose.ui.geometry.RoundRect(dx + 10.5f * k, dy + 5f * k, dx + 23.5f * k, dy + 29f * k, 3.5f * k, 3.5f * k))
                    moveTo(dx + 15.5f * k, dy + 25.5f * k)
                    lineTo(dx + 18.5f * k, dy + 25.5f * k)
                }
                drawPath(
                    p,
                    glyphColor,
                    style = androidx.compose.ui.graphics.drawscope.Stroke(
                        width = 1.35.dp.toPx(),
                        cap = androidx.compose.ui.graphics.StrokeCap.Round,
                        join = androidx.compose.ui.graphics.StrokeJoin.Round,
                    ),
                )
            }
        }
    }
}

@Composable
private fun UpDownChevrons(tint: Color) {
    androidx.compose.foundation.Canvas(Modifier.size(10.dp, 12.dp)) {
        val stroke = androidx.compose.ui.graphics.drawscope.Stroke(
            width = 1.3.dp.toPx(),
            cap = androidx.compose.ui.graphics.StrokeCap.Round,
            join = androidx.compose.ui.graphics.StrokeJoin.Round,
        )
        val cx = size.width / 2f
        val dx = size.width * 0.28f
        val up = androidx.compose.ui.graphics.Path().apply {
            moveTo(cx - dx, size.height * 0.36f)
            lineTo(cx, size.height * 0.16f)
            lineTo(cx + dx, size.height * 0.36f)
        }
        val down = androidx.compose.ui.graphics.Path().apply {
            moveTo(cx - dx, size.height * 0.64f)
            lineTo(cx, size.height * 0.84f)
            lineTo(cx + dx, size.height * 0.64f)
        }
        drawPath(up, tint, style = stroke)
        drawPath(down, tint, style = stroke)
    }
}

// MARK: 阅读

/** 紧 · 适中 · 松; null is the paper's own. */
private val spacings = listOf<Double?>(1.6, null, 2.2)

private fun fontName(f: Font?) = when (f) {
    Font.SERIF -> t("宋体")
    Font.KAI, Font.WEN_KAI -> t("霞鹜文楷")
    else -> t("系统")
}


/** 阅读, as the Mac's: what a book opens with — paper, font, size, spacing, how pages turn and how
 *  the text sits — under a sample of 《故乡》; a book's own Aa changes only that book. 沉浸阅读 and
 *  墨水屏 sit here too, as the Mac keeps them under 阅读. */
@OptIn(ExperimentalFoundationApi::class, ExperimentalMaterial3Api::class)
@Composable
fun ReadingPage(library: Library, push: (MePage) -> Unit) {
    val context = LocalContext.current
    var settings by remember { mutableStateOf(runCatching { library.core.readerSettings() }.getOrNull()) }
    var always by rememberPref(ImmersivePrefs.ALWAYS, false)
    var line by rememberPref(ImmersivePrefs.LINE, true)
    var marks by rememberPref(ImmersivePrefs.MARKS, false)
    var hints by rememberPref(ImmersivePrefs.HINTS, true)
    var still by rememberPref(ImmersivePrefs.STILL, true)
    var status by rememberPref(ImmersivePrefs.STATUS, true)
    var progress by rememberPref(ImmersivePrefs.PROGRESS, true)
    var quiet by rememberPref(ImmersivePrefs.QUIET, true)
    val eink by rememberPref(EInk.KEY, false)
    var editingPaper by remember { mutableStateOf<CustomPaper?>(null) }
    var previewPaper by remember { mutableStateOf<CustomPaper?>(null) }
    var renamingPaper by remember { mutableStateOf<CustomPaper?>(null) }
    var asking by remember { mutableStateOf<Confirm?>(null) }
    val s = settings ?: return
    fun change(edit: (ReaderSettings) -> ReaderSettings) {
        val new = edit(s)
        if (new == s) return
        SettingsLog.reader(s, new)
        settings = new
        runCatching { library.core.setReaderSettings(new) }
    }
    fun choosePaper(mine: CustomPaper) {
        app.yuyue.reader.PaperAppearance.pick(mine, s) { change(it) }
    }
    fun deletePaper(mine: CustomPaper) {
        if (s.customPaper == mine.id) change { it.copy(customPaper = null) }
        app.yuyue.reader.PaperAppearance.forget(mine.id)
        Papers.delete(mine.id)
    }
    val resolvedPaper = app.yuyue.reader.PaperAppearance.resolve(s)
    val followAppearance = app.yuyue.reader.PaperAppearance.follow
    ConfirmHost(asking) { asking = null }
    YYPage {
        SettingsLead(t("新书的默认排版；单本书可在阅读页 Aa 中单独调整"))
        Sample(s, previewPaper, eink, Modifier.padding(top = YY.s(12)))
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                Column(Modifier.padding(vertical = YY.s(12)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
                    Row(
                        Modifier.fillMaxWidth().alpha(if (eink) 0.45f else 1f),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                    ) {
                        Text(t("纸色"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                        Spacer(Modifier.weight(1f))
                        Text(t("跟随外观"), style = YY.font(10.5f), color = YY.c.t2)
                        SettingSwitch(followAppearance, {
                            app.yuyue.reader.PaperAppearance.setFollow(it, s) { editFn -> change(editFn) }
                        }, enabled = !eink)
                    }
                    if (eink) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                            Icon(Icons.eink, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(12)))
                            Text(t("墨水屏模式已接管纸色，关闭后恢复原纸色"), style = YY.font(10.5f), color = YY.c.t2)
                        }
                    }
                    val choices: List<Pair<String, YYPaper>?> =
                        paperPresets.map { it.tokens.id to it.tokens } +
                        Papers.list.map { it.id to customTokens(it) } +
                        listOf(null)
                    Box(Modifier.alpha(if (eink) 0.45f else 1f)) {
                        TileGrid(choices, 3) { entry, m ->
                            if (entry == null) {
                                NewPaperChoice(m) { if (!eink) editingPaper = Papers.newDraft(s) }
                            } else {
                                val (id, paper) = entry
                                val custom = Papers.paper(id)
                                if (custom != null) {
                                    var menu by remember(custom.id) { mutableStateOf(false) }
                                    Box(m) {
                                        PaperChoice(
                                            paper,
                                            custom.texture,
                                            custom.name,
                                            resolvedPaper.choiceID == id,
                                            Modifier.fillMaxWidth(),
                                            onLongClick = if (eink) null else ({ menu = true }),
                                        ) { if (!eink) choosePaper(custom) }
                                        DropdownMenu(menu, { menu = false }, containerColor = YY.c.card) {
                                            DropdownMenuItem({ Text(t("调整颜色")) }, { menu = false; editingPaper = custom })
                                            DropdownMenuItem({ Text(t("重命名")) }, { menu = false; renamingPaper = custom })
                                            DropdownMenuItem({ Text(t("复制一张")) }, {
                                                menu = false
                                                Papers.duplicate(custom)?.let { choosePaper(it) }
                                            })
                                            DropdownMenuItem({ Text(t("删除"), color = YY.c.danger) }, {
                                                menu = false
                                                asking = Confirm(
                                                    t("删除“%@”？", custom.name),
                                                    t("正在使用这张纸的书将恢复为相近的预设纸色。"),
                                                    t("删除"),
                                                ) { deletePaper(custom) }
                                            })
                                        }
                                    }
                                } else {
                                    PaperChoice(
                                        paper,
                                        if (id == Paper.EINK.tokens.id) PaperTexture.GRAIN else PaperTexture.NONE,
                                        t(paper.name),
                                        resolvedPaper.choiceID == id,
                                        m,
                                    ) {
                                        if (!eink) {
                                            val preset = paperPresets.first { p -> p.tokens.id == id }
                                            app.yuyue.reader.PaperAppearance.pick(preset, s) { change(it) }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            row {
                FontTiles(app.yuyue.reader.FontShelf.id(s)) { id ->
                    change { app.yuyue.reader.FontShelf.apply(id, it) }
                    app.yuyue.reader.FontShelf.ensure(id)
                }
            }
            row {
                Row(Modifier.fillMaxWidth().heightIn(min = YY.s(40)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    Text(t("字号"), style = YY.font(11.5f), color = YY.c.t1)
                    YYSlider(s.fontSize.toFloat(), { v -> change { it.copy(fontSize = v.roundToInt().toDouble()) } }, 12f..34f,
                        Modifier.weight(1f), description = t("字号"), state = "${s.fontSize.roundToInt()}")
                    Text("${s.fontSize.roundToInt()}", style = YY.font(11f, tnum = true), color = YY.c.t2, textAlign = TextAlign.End, modifier = Modifier.width(YY.s(22)))
                }
            }
            row {
                YYRow(t("行距"), trailing = {
                    YYSegments(spacings.mapIndexed { i, v -> v to listOf(t("紧"), t("适中"), t("松"))[i] }, s.lineHeight.takeIf { it in spacings },
                        { v -> change { it.copy(lineHeight = v) } }, small = true)
                })
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                YYRow(t("翻页方式"), trailing = {
                    YYSegments(listOf(LayoutMode.PAGINATED to t("左右翻页"), LayoutMode.SCROLLED to t("上下滚动")), s.mode, { m -> change { it.copy(mode = m) } }, small = true)
                })
            }
            row { DetailSwitch(t("两端对齐"), null, s.justify ?: true) { v -> change { it.copy(justify = v) } } }
            row { DetailSwitch(t("首行缩进两字"), null, s.indent ?: true) { v -> change { it.copy(indent = v) } } }
            row { DetailSwitch(t("中文标点挤压"), t("收紧连续标点的留白"), s.compress) { v -> change { it.copy(compress = v) } } }
        }
        YYLabel(t("沉浸阅读"))
        YYGroup {
            row { DetailRow(t("进入与退出"), t("双击屏幕边缘进出，轻点中央临时唤出控制栏")) }
            row { DetailSwitch(t("打开书籍时自动进入"), null, always) { always = it } }
            row { DetailSwitch(t("底部保留进度细线"), null, line) { line = it } }
            row { DetailSwitch(t("隐藏划线与想法"), t("退出沉浸后恢复显示"), marks) { marks = it } }
            row { DetailSwitch(t("隐藏书伴提示与难词注释"), null, hints) { hints = it } }
            row { DetailSwitch(t("关闭翻页动画"), t("直接切换整页，无滑动过渡"), still) { still = it } }
            row { DetailSwitch(t("隐藏状态栏、时间和电量"), null, status) { status = it } }
            row { DetailSwitch(t("隐藏进度、页码和剩余时间"), null, progress) { progress = it } }
            val allowed = DoNotDisturb.allowed(context)
            row { DetailSwitch(t("专注模式联动"), t("沉浸阅读时自动开启系统勿扰，退出后恢复；不影响手动开启的勿扰"), quiet) { quiet = it } }
            // Not let in yet: the way to the system page, as a row of its own under the switch.
            if (quiet && !allowed) row { YYRow(t("在系统设置中允许语阅管理勿扰"), titleColor = YY.c.warn, chevron = true) { DoNotDisturb.ask(context) } }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup { row { YYRow(t("墨水屏"), Icons.eink, if (eink) t("开") else t("关"), chevron = true) { push(MePage.EInk) } } }
    }
    editingPaper?.let { draft ->
        ModalBottomSheet(
            onDismissRequest = { editingPaper = null; previewPaper = null },
            sheetState = rememberModalBottomSheetState(),
            containerColor = YY.c.card,
            scrimColor = Color.Transparent,
            dragHandle = { YYSheetDragHandle() },
        ) {
            Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                PaperEditor(
                    start = draft,
                    backTitle = t("取消"),
                    preview = { previewPaper = it },
                ) { saved ->
                    editingPaper = null
                    previewPaper = null
                    saved?.let { choosePaper(it) }
                }
            }
        }
    }
    renamingPaper?.let { paper ->
        var name by remember(paper) { mutableStateOf(paper.name) }
        AlertDialog(
            onDismissRequest = { renamingPaper = null },
            containerColor = YY.c.card,
            title = { Text(t("重命名"), color = YY.c.t1) },
            text = { OutlinedTextField(name, { name = it.take(20) }, singleLine = true) },
            confirmButton = { TextButton({ Papers.rename(paper, name); renamingPaper = null }) { Text(t("好"), color = YY.tint.accent) } },
            dismissButton = { TextButton({ renamingPaper = null }) { Text(t("取消"), color = YY.c.t2) } },
        )
    }
}

/** The opening of 《故乡》 on the chosen paper, in the chosen font, size and spacing. */
@Composable
private fun Sample(s: ReaderSettings, preview: CustomPaper?, eink: Boolean, modifier: Modifier) {
    val resolved = app.yuyue.reader.PaperAppearance.resolve(s)
    val paper = if (eink) EInk.paper else (preview?.let { customTokens(it) } ?: resolved.tokens)
    val size = (s.fontSize * 0.8).roundToInt().toFloat()
    val lineHeight = (s.lineHeight?.toFloat() ?: paper.text.lineHeight).coerceAtLeast(1.3f)
    val indent = if (s.indent ?: true) "　　" else ""
    val texture = if (eink) PaperTexture.NONE else (preview?.texture ?: resolved.texture)
    Box(
        modifier.fillMaxWidth().clip(RoundedCornerShape(YY.s(12))).background(paper.bg)
            .border(0.5.dp, if (paper.isDark) Color.Transparent else YY.c.line, RoundedCornerShape(YY.s(12))),
    ) {
        TextureLayer(texture, paper.isDark, Modifier.matchParentSize())
        Column(Modifier.padding(horizontal = YY.s(14), vertical = YY.s(12)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
            Row {
                Text(t("故乡"), style = YY.font(8.5f), color = paper.ink.copy(alpha = 0.45f), modifier = Modifier.weight(1f))
                Text("12 / 31", style = YY.font(8.5f), color = paper.ink.copy(alpha = 0.45f))
            }
            Text(
                indent + t("我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"),
                fontFamily = app.yuyue.reader.FontShelf.family(app.yuyue.reader.FontShelf.id(s)), fontSize = size.sp, lineHeight = (size * lineHeight * 0.75f).sp, color = paper.ink,
                textAlign = if (s.justify ?: true) TextAlign.Justify else TextAlign.Start,
            )
        }
    }
}

/** A paper to choose: two lines of ink on it, its name under it. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PaperChoice(
    paper: YYPaper,
    texture: PaperTexture,
    name: String,
    on: Boolean,
    modifier: Modifier,
    onLongClick: (() -> Unit)? = null,
    pick: () -> Unit,
) {
    val tap = if (onLongClick != null) Modifier.combinedClickable(onLongClick = onLongClick, onClick = pick) else Modifier.clickable(onClick = pick)
    Column(
        modifier.settingsTile(on, YY.c.line, YY.tint.accent).then(tap).padding(start = YY.s(6), end = YY.s(6), top = YY.s(9), bottom = YY.s(7)),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(6)),
    ) {
        Box(
            Modifier.fillMaxWidth().height(YY.s(30)).clip(RoundedCornerShape(5.dp)).background(paper.bg)
                .border(0.5.dp, if (paper.isDark) Color.Transparent else Color.Black.copy(alpha = 0.12f), RoundedCornerShape(5.dp)),
        ) {
            TextureLayer(texture, paper.isDark, Modifier.matchParentSize())
            Column(Modifier.align(Alignment.CenterStart).padding(start = YY.s(9)), verticalArrangement = Arrangement.spacedBy(YY.s(4))) {
                Box(Modifier.width(YY.s(28)).height(2.dp).background(paper.ink.copy(alpha = 0.5f), RoundedCornerShape(1.dp)))
                Box(Modifier.width(YY.s(20)).height(2.dp).background(paper.ink.copy(alpha = 0.5f), RoundedCornerShape(1.dp)))
            }
        }
        Text(name, style = YY.font(11f, if (on) FontWeight.SemiBold else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** The ＋ tile at the end of the 纸色 grid in 设置 › 阅读: opens 调纸. */
@Composable
private fun NewPaperChoice(modifier: Modifier, onClick: () -> Unit) {
    Column(
        modifier
            .settingsTile(false, YY.c.line, YY.tint.accent)
            .clickable(onClick = onClick)
            .semantics { contentDescription = t("自己调一张纸") }
            .padding(start = YY.s(6), end = YY.s(6), top = YY.s(9), bottom = YY.s(7)),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(YY.s(6)),
    ) {
        Box(
            Modifier.fillMaxWidth().height(YY.s(30)).clip(RoundedCornerShape(5.dp)).background(YY.c.inset),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(14)))
        }
        Text(t("新建"), style = YY.font(11f), color = YY.c.t2, maxLines = 1)
    }
}

// MARK: 最近改过

/** Every change made in 设置, kept across launches (the last 40), each one undoable: a preference
 *  goes back to what it was; a reading setting is written back to the core. Preferences are
 *  noticed as they change, so a page needs no code of its own — as on the Mac. */
object SettingsLog {
    sealed interface Undo {
        data class Pref(val key: String, val old: Any?) : Undo
        data class Reader(val field: String, val old: String) : Undo
    }

    data class Entry(
        val id: String = UUID.randomUUID().toString(),
        val date: Long,
        val title: String,
        val from: String,
        val to: String,
        val page: MePage?,
        val undo: Undo?,
        val undone: Boolean = false,
        val redo: Undo? = null,
        val key: String = "",
        val look: String? = null,
        val fromRaw: String? = null,
        val toRaw: String? = null,
    ) {
        val settingKey: String
            get() = when {
                key.isNotEmpty() -> key
                undo is Undo.Pref -> "p:${undo.key}"
                undo is Undo.Reader -> "r:${undo.field}"
                else -> "${page?.name ?: ""}:$title"
            }
    }

    val entries = mutableStateListOf<Entry>()

    private class Watched(
        val title: () -> String,
        val page: MePage,
        val standard: Any,
        val look: String? = if (standard is Boolean) "toggle" else null,
        val show: (Any?) -> String = ::onOff,
    ) {
        fun raw(v: Any?): String = when (val x = v ?: standard) {
            is Boolean -> if (x) "1" else "0"
            else -> "$x"
        }
    }

    private fun onOff(v: Any?) = if (v == true) t("开") else t("关")

    private val watched: Map<String, Watched> = mapOf(
        General.REOPEN_LAST to Watched({ t("启动时继续上次阅读") }, MePage.General, false),
        Appearance.KEY to Watched({ t("界面") }, MePage.Appearance, Appearance.SYSTEM) {
            when (it) { Appearance.LIGHT -> t("浅色"); Appearance.DARK -> t("深色"); else -> t("跟随系统") }
        },
        "appearance.tint" to Watched({ t("主题色") }, MePage.Appearance, YYTints.Default.id) { v -> YYTints.All.firstOrNull { it.id == v }?.name?.let { t(it) } ?: "$v" },
        Shelf.COVER_SIZE to Watched({ t("封面大小") }, MePage.Appearance, 100f, look = "mono") { "${(it as? Float ?: 100f).roundToInt()}" },
        Shelf.SHOW_PROGRESS to Watched({ t("封面下显示进度") }, MePage.Appearance, true),
        app.yuyue.reader.PaperAppearance.FOLLOW_KEY to Watched({ t("纸色跟随外观") }, MePage.Reading, true),
        ImmersivePrefs.ALWAYS to Watched({ t("打开书籍时自动进入") }, MePage.Reading, false),
        ImmersivePrefs.LINE to Watched({ t("进度细线") }, MePage.Reading, true),
        ImmersivePrefs.MARKS to Watched({ t("沉浸时隐藏划线") }, MePage.Reading, false),
        ImmersivePrefs.HINTS to Watched({ t("沉浸时隐藏提示") }, MePage.Reading, true),
        ImmersivePrefs.STILL to Watched({ t("关闭翻页动画") }, MePage.Reading, true),
        ImmersivePrefs.STATUS to Watched({ t("隐藏状态栏") }, MePage.Reading, true),
        ImmersivePrefs.PROGRESS to Watched({ t("隐藏进度和页码") }, MePage.Reading, true),
        ImmersivePrefs.QUIET to Watched({ t("专注模式联动") }, MePage.Reading, true),
        "companion.provider" to Watched({ t("书伴使用") }, MePage.Companion, "") { v ->
            app.yuyue.companion.CompanionProvider.find("$v")?.title ?: t("未选择")
        },
        Player.RATE_KEY to Watched({ t("语速") }, MePage.Listening, 1f, look = "mono") { "%.1f×".format((it as? Float)?.takeIf { r -> r > 0 } ?: 1f) },
        "player.voice.zh" to Watched({ t("中文声音") }, MePage.Listening, "", look = "voice") { voiceName(it) },
        "player.voice.en" to Watched({ t("英文声音") }, MePage.Listening, "", look = "voice") { voiceName(it) },
        ForeignPrefs.SPEAK to Watched({ t("查词时自动发音") }, MePage.Foreign, true),
        ForeignPrefs.ACCENT to Watched({ t("英文发音") }, MePage.Foreign, "en-GB") { if (it == "en-US") t("美音") else t("英音") },
        ForeignPrefs.MARK to Watched({ t("标出生词") }, MePage.Foreign, true),
        ForeignPrefs.GLOSS to Watched({ t("难词释义") }, MePage.Foreign, true),
        ForeignPrefs.LEVEL to Watched({ t("词汇难度") }, MePage.Foreign, 3) { v -> GLOSS_LEVELS.firstOrNull { it.first == v }?.second?.let { t(it) } ?: "$v" },
        ForeignPrefs.GLOSS_VOCAB to Watched({ t("只标生词本里的") }, MePage.Foreign, false),
        ForeignPrefs.AI_TRANSLATE to Watched({ t("优先用书伴模型翻译") }, MePage.Foreign, true),
    )

    private fun voiceName(v: Any?): String {
        val id = v as? String ?: return t("自动")
        if (id.isEmpty()) return t("自动")
        return voiceNames[id]
            ?: runCatching { core?.cardFind(null, id)?.name }.getOrNull()
            ?: app.yuyue.speech.ClonedVoices.find(id)?.name
            ?: app.yuyue.speech.CloudVoice.all().firstOrNull { it.id == id }?.name
            ?: t("另一个声音")
    }

    /** Voices by id, filled where they are listed, so a change reads as a name. */
    val voiceNames = mutableMapOf<String, String>()

    private const val STORE = "settings.log"
    private var snapshot: Map<String, Any?> = emptyMap()
    private var restoring = false
    private var core: uniffi.yuyue_core.YuyueCore? = null
    // Held here: SharedPreferences keeps its listeners only weakly.
    private val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key -> if (key != null && key in watched) changed() }

    fun init(core: uniffi.yuyue_core.YuyueCore) {
        this.core = core
        entries.clear()
        entries += load()
        snapshot = read()
        Prefs.shared.registerOnSharedPreferenceChangeListener(listener)
    }

    private fun read(): Map<String, Any?> = watched.keys.associateWith { Prefs.shared.all[it] }

    private fun changed() {
        if (restoring) return
        val now = read()
        val before = snapshot
        snapshot = now
        for ((key, w) in watched) {
            val new = now[key]
            val old = before[key]
            if (new == old || w.show(new ?: w.standard) == w.show(old ?: w.standard)) continue
            record(
                Entry(
                    date = System.currentTimeMillis(),
                    title = w.title(),
                    from = w.show(old ?: w.standard),
                    to = w.show(new ?: w.standard),
                    page = w.page,
                    undo = Undo.Pref(key, old),
                    key = "p:$key",
                    look = w.look,
                    fromRaw = w.raw(old),
                    toRaw = w.raw(new),
                )
            )
        }
    }

    // Reading settings, written to the core rather than to preferences.

    private enum class Field(val title: () -> String) {
        PAPER({ t("纸色") }), FONT({ t("字体") }), SIZE({ t("字号") }), LINE_HEIGHT({ t("行距") }),
        MODE({ t("翻页方式") }), JUSTIFY({ t("两端对齐") }), INDENT({ t("首行缩进") }), COMPRESS({ t("标点挤压") });

        fun value(s: ReaderSettings): String = when (this) {
            PAPER -> s.paperChoice
            FONT -> s.font?.name ?: ""
            SIZE -> "${s.fontSize.roundToInt()}"
            LINE_HEIGHT -> s.lineHeight?.toString() ?: ""
            MODE -> s.mode.name
            JUSTIFY -> if (s.justify ?: true) "1" else "0"
            INDENT -> if (s.indent ?: true) "1" else "0"
            COMPRESS -> if (s.compress) "1" else "0"
        }

        fun show(v: String): String = when (this) {
            PAPER -> paperPresets.firstOrNull { it.tokens.id == v }?.let { t(it.tokens.name) } ?: Papers.paper(v)?.name ?: v
            FONT -> fontName(Font.entries.firstOrNull { it.name == v })
            SIZE -> v
            LINE_HEIGHT -> when (v) { "1.6" -> t("紧"); "2.2" -> t("松"); "" -> t("适中"); else -> v }
            MODE -> if (v == LayoutMode.SCROLLED.name) t("上下滚动") else t("左右翻页")
            JUSTIFY, INDENT, COMPRESS -> if (v == "1") t("开") else t("关")
        }

        fun apply(v: String, s: ReaderSettings): ReaderSettings = when (this) {
            PAPER -> paperPresets.firstOrNull { it.tokens.id == v }?.let { s.copy(paper = it, customPaper = null) }
                ?: Papers.paper(v)?.let { s.copy(customPaper = it.id, paper = uniffi.yuyue_core.paperColors(it).preset) } ?: s
            FONT -> s.copy(font = Font.entries.firstOrNull { it.name == v })
            SIZE -> s.copy(fontSize = v.toDoubleOrNull() ?: s.fontSize)
            LINE_HEIGHT -> s.copy(lineHeight = v.toDoubleOrNull())
            MODE -> s.copy(mode = LayoutMode.entries.firstOrNull { it.name == v } ?: s.mode)
            JUSTIFY -> s.copy(justify = v == "1")
            INDENT -> s.copy(indent = v == "1")
            COMPRESS -> s.copy(compress = v == "1")
        }
    }

    /** A reading setting changed on 阅读: each field that moved is a line of its own. */
    fun reader(from: ReaderSettings, to: ReaderSettings) {
        for (f in Field.entries) {
            val old = f.value(from)
            val new = f.value(to)
            if (old == new) continue
            val look = when (f) {
                Field.PAPER -> "paper"
                Field.FONT -> "font"
                Field.SIZE -> "mono"
                Field.JUSTIFY, Field.INDENT, Field.COMPRESS -> "toggle"
                else -> null
            }
            record(
                Entry(
                    date = System.currentTimeMillis(),
                    title = f.title(),
                    from = f.show(old),
                    to = f.show(new),
                    page = MePage.Reading,
                    undo = Undo.Reader(f.name, old),
                    key = "r:${f.name}",
                    look = look,
                    fromRaw = old,
                    toRaw = new,
                )
            )
        }
    }

    private fun record(e: Entry) {
        // A slider dragged is one change: the line just made for the same thing is moved on, not added to.
        val last = entries.firstOrNull()
        if (last != null && !last.undone && last.settingKey == e.settingKey && e.date - last.date < 4000) {
            if (last.from == e.to) entries.removeAt(0) else entries[0] = last.copy(to = e.to, toRaw = e.toRaw, date = e.date)
        } else {
            entries.add(0, e)
            while (entries.size > 40) entries.removeAt(entries.lastIndex)
        }
        save()
    }

    /** Puts a value back; returns what it was before, for the other way. */
    private fun put(u: Undo): Undo? {
        restoring = true
        try {
            return when (u) {
                is Undo.Pref -> {
                    val now = Prefs.shared.all[u.key]
                    Prefs.shared.edit().apply {
                        when (val v = u.old) {
                            null -> remove(u.key)
                            is Boolean -> putBoolean(u.key, v)
                            is Float -> putFloat(u.key, v)
                            is Int -> putInt(u.key, v)
                            is Long -> putLong(u.key, v)
                            else -> putString(u.key, "$v")
                        }
                    }.commit()
                    if (u.key == app.yuyue.reader.PaperAppearance.FOLLOW_KEY) app.yuyue.reader.PaperAppearance.reloadFromPrefs()
                    snapshot = read()
                    Undo.Pref(u.key, now)
                }
                is Undo.Reader -> {
                    val core = core ?: return null
                    val f = Field.entries.firstOrNull { it.name == u.field } ?: return null
                    val s = runCatching { core.readerSettings() }.getOrNull() ?: return null
                    val now = f.value(s)
                    runCatching { core.setReaderSettings(f.apply(u.old, s)) }
                    Undo.Reader(u.field, now)
                }
            }
        } finally {
            restoring = false
        }
    }

    fun undo(e: Entry) {
        val u = e.undo ?: return
        val back = put(u)
        replace(e.copy(undone = true, redo = back))
    }

    fun redo(e: Entry) {
        val r = e.redo ?: return
        put(r)
        replace(e.copy(undone = false, redo = null))
    }

    fun clear() {
        entries.clear()
        save()
    }

    private fun replace(e: Entry) {
        val i = entries.indexOfFirst { it.id == e.id }
        if (i >= 0) entries[i] = e
        save()
    }

    val changedToday: Int get() = entries.count { !it.undone && isToday(it.date) }

    private fun isToday(ms: Long) = sameDay(ms, System.currentTimeMillis())

    internal fun sameDay(a: Long, b: Long): Boolean {
        val x = Calendar.getInstance().apply { timeInMillis = a }
        val y = Calendar.getInstance().apply { timeInMillis = b }
        return x.get(Calendar.YEAR) == y.get(Calendar.YEAR) && x.get(Calendar.DAY_OF_YEAR) == y.get(Calendar.DAY_OF_YEAR)
    }

    // Kept as JSON in the preferences.

    private fun undoJSON(u: Undo?): JSONObject? = when (u) {
        null -> null
        is Undo.Pref -> JSONObject().put("key", u.key).put("old", u.old ?: JSONObject.NULL)
            .put("type", when (u.old) { is Boolean -> "b"; is Float -> "f"; is Int -> "i"; is Long -> "l"; null -> "n"; else -> "s" })
        is Undo.Reader -> JSONObject().put("field", u.field).put("old", u.old)
    }

    private fun undoFrom(j: JSONObject?): Undo? {
        j ?: return null
        if (j.has("field")) return Undo.Reader(j.getString("field"), j.getString("old"))
        val old: Any? = when (j.optString("type")) {
            "b" -> j.getBoolean("old")
            "f" -> j.getDouble("old").toFloat()
            "i" -> j.getInt("old")
            "l" -> j.getLong("old")
            "n" -> null
            else -> j.getString("old")
        }
        return Undo.Pref(j.getString("key"), old)
    }

    private fun save() {
        val a = JSONArray()
        for (e in entries) a.put(
            JSONObject().put("id", e.id).put("date", e.date).put("title", e.title).put("from", e.from).put("to", e.to)
                .put("page", e.page?.name ?: JSONObject.NULL).put("undo", undoJSON(e.undo) ?: JSONObject.NULL)
                .put("undone", e.undone).put("redo", undoJSON(e.redo) ?: JSONObject.NULL)
                .put("key", e.key).put("look", e.look ?: JSONObject.NULL)
                .put("fromRaw", e.fromRaw ?: JSONObject.NULL).put("toRaw", e.toRaw ?: JSONObject.NULL)
        )
        Prefs.set(STORE, a.toString())
    }

    private fun load(): List<Entry> = runCatching {
        val a = JSONArray(Prefs.string(STORE, "[]"))
        (0 until a.length()).map { i ->
            val j = a.getJSONObject(i)
            Entry(
                id = j.getString("id"), date = j.getLong("date"), title = j.getString("title"), from = j.getString("from"), to = j.getString("to"),
                page = j.optString("page").let { p -> MePage.entries.firstOrNull { it.name == p } },
                undo = undoFrom(j.optJSONObject("undo")), undone = j.optBoolean("undone"), redo = undoFrom(j.optJSONObject("redo")),
                key = j.optString("key", ""),
                look = j.optString("look").takeIf { it.isNotEmpty() && !j.isNull("look") },
                fromRaw = j.optString("fromRaw").takeIf { !j.isNull("fromRaw") },
                toRaw = j.optString("toRaw").takeIf { !j.isNull("toRaw") },
            )
        }
    }.getOrDefault(emptyList())
}

/** The shelf's look, kept with 外观. */
object Shelf {
    const val COVER_SIZE = "shelf.coverSize"
    const val SHOW_PROGRESS = "shelf.showProgress"
}

private data class HistoryRun(val head: SettingsLog.Entry, val older: List<SettingsLog.Entry>)

/** 最近改过: grouped by day with consecutive edits to the same setting folded, rich value chips
 *  (paper dot, font face, switch, 1-tap voice preview), locked-width 改回 / 还原 actions, and filters. */
@Composable
fun HistoryPage(push: (MePage) -> Unit) {
    val entries = SettingsLog.entries
    var filterPage by remember { mutableStateOf<MePage?>(null) }
    val expanded = remember { mutableStateSetOf<String>() }
    var feedback by remember { mutableStateOf<Pair<Long, String>?>(null) }
    var playingChip by remember { mutableStateOf<String?>(null) }
    var playJob by remember { mutableStateOf<Job?>(null) }
    val scope = rememberCoroutineScope()

    DisposableEffect(Unit) {
        onDispose {
            playJob?.cancel()
            SystemVoice.Engine.stopPreview()
        }
    }

    LaunchedEffect(feedback?.first) {
        if (feedback != null) {
            delay(1800)
            feedback = null
        }
    }

    fun toggleVoice(chipId: String, voiceId: String?, label: String) {
        if (voiceId.isNullOrEmpty()) return
        if (playingChip == chipId) {
            playJob?.cancel()
            SystemVoice.Engine.stopPreview()
            playingChip = null
            return
        }
        playJob?.cancel()
        SystemVoice.Engine.stopPreview()
        playingChip = chipId
        playJob = scope.launch {
            val v = Voices.find(voiceId)
            if (v != null) {
                val latin = label.count { it.code < 128 } > label.length / 2
                val sample = app.yuyue.speech.DailyQuote.current(if (latin && v.speaks("en") && !v.speaks("zh")) "en" else "zh").text
                if (v is SystemVoice) {
                    if (v.preview(sample)) delay(2400)
                } else {
                    val s = runCatching { withContext(Dispatchers.IO) { v.render(sample, if (latin) "en" else "zh") } }.getOrNull()
                    if (s != null) {
                        val ms = (s.samples.size * 1000L / maxOf(1, s.rate)).coerceAtLeast(800L)
                        SystemVoice.Engine.preview(s)
                        delay(ms)
                    }
                }
            }
            if (playingChip == chipId) playingChip = null
        }
    }

    fun doUndo(e: SettingsLog.Entry) {
        SettingsLog.undo(e)
        feedback = System.currentTimeMillis() to t("已改回「%@」", e.from)
    }

    fun doRedo(e: SettingsLog.Entry) {
        SettingsLog.redo(e)
        feedback = System.currentTimeMillis() to t("已还原「%@」", e.to)
    }

    // Compute superseded entries & per-page counts
    val seenActive = HashSet<String>()
    val superseded = HashSet<String>()
    val pageCounts = LinkedHashMap<MePage, Int>()
    for (e in entries) {
        e.page?.let { p -> pageCounts[p] = (pageCounts[p] ?: 0) + 1 }
        if (!e.undone) {
            if (!seenActive.add(e.settingKey)) superseded.add(e.id)
        }
    }
    val effectiveFilter = filterPage?.takeIf { pageCounts.containsKey(it) }
    val latestUndoable = entries.firstOrNull { !it.undone && it.undo != null }

    YYPage {
        if (entries.isEmpty()) {
            Column(
                Modifier.fillMaxWidth().padding(vertical = YY.s(44)),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(YY.s(8)),
            ) {
                Box(
                    Modifier
                        .size(YY.s(36))
                        .clip(RoundedCornerShape(YY.s(10)))
                        .background(YY.c.card)
                        .border(0.5.dp, if (YY.dark) Color.White.copy(alpha = 0.07f) else YY.c.line, RoundedCornerShape(YY.s(10))),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.history, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(16)))
                }
                Text(t("还没有修改记录"), style = YY.font(12f, FontWeight.SemiBold), color = YY.c.t1)
                Text(
                    t("调整过的设置会记在这里，\n每一项都能随时改回去。"),
                    style = YY.font(10.5f),
                    color = YY.c.t3,
                    textAlign = TextAlign.Center,
                )
            }
            return@YYPage
        }

        // Top quick-undo & inline status bar
        Column(Modifier.fillMaxWidth().padding(top = YY.s(10), bottom = YY.s(2)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                val canUndo = latestUndoable != null
                Row(
                    Modifier
                        .height(YY.s(26))
                        .clip(CircleShape)
                        .background(YY.c.card)
                        .border(0.5.dp, YY.c.pillLine, CircleShape)
                        .alpha(if (canUndo) 1f else 0.5f)
                        .clickable(enabled = canUndo) { latestUndoable?.let(::doUndo) }
                        .padding(horizontal = YY.s(10)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(5)),
                ) {
                    Icon(Icons.undo, null, tint = if (canUndo) YY.c.t1 else YY.c.t3, modifier = Modifier.size(YY.s(11)))
                    Text(
                        if (latestUndoable != null) t("改回「%@」", latestUndoable.title) else t("无可改回项"),
                        style = YY.font(10.5f, FontWeight.Medium),
                        color = if (canUndo) YY.c.t1 else YY.c.t3,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }

                Spacer(Modifier.weight(1f))

                val fb = feedback
                if (fb != null) {
                    Row(
                        Modifier
                            .clip(CircleShape)
                            .background(YY.tint.accentSoft)
                            .border(0.5.dp, YY.tint.edge, CircleShape)
                            .padding(horizontal = YY.s(8), vertical = YY.s(3)),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
                    ) {
                        Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(10)))
                        Text(fb.second, style = YY.font(10f, FontWeight.Medium), color = YY.tint.accent, maxLines = 1)
                    }
                } else {
                    Text(t("%lld 项记录", entries.size), style = YY.font(10f, tnum = true), color = YY.c.t3)
                }
            }

            if (pageCounts.size > 1) {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(YY.s(5)),
                ) {
                    HistoryFilterPill(t("全部"), entries.size, effectiveFilter == null) { filterPage = null }
                    for ((p, cnt) in pageCounts) {
                        HistoryFilterPill(p.title, cnt, effectiveFilter == p) { filterPage = p }
                    }
                }
            }
        }

        val filtered = if (effectiveFilter != null) entries.filter { it.page == effectiveFilter } else entries
        val days = filtered.groupBy { e -> filtered.first { SettingsLog.sameDay(it.date, e.date) }.date }

        for ((dayMs, list) in days) {
            val runs = ArrayList<HistoryRun>()
            for (item in list) {
                val prev = runs.lastOrNull()
                if (prev != null && prev.head.settingKey == item.settingKey) {
                    runs[runs.lastIndex] = prev.copy(older = prev.older + item)
                } else {
                    runs.add(HistoryRun(item, emptyList()))
                }
            }

            YYLabel(dayName(dayMs))
            YYGroup {
                for (run in runs) {
                    val open = run.head.id in expanded
                    row {
                        HistoryRow(
                            e = run.head,
                            superseded = run.head.id in superseded,
                            olderCount = run.older.size,
                            isExpanded = open,
                            isSubStep = false,
                            playingChip = playingChip,
                            onToggleExpand = {
                                if (open) expanded.remove(run.head.id) else expanded.add(run.head.id)
                            },
                            onToggleVoice = ::toggleVoice,
                            onUndo = { doUndo(run.head) },
                            onRedo = { doRedo(run.head) },
                            push = push,
                        )
                    }
                    if (run.older.isNotEmpty() && open) {
                        for (sub in run.older) {
                            row {
                                HistoryRow(
                                    e = sub,
                                    superseded = sub.id in superseded,
                                    olderCount = 0,
                                    isExpanded = false,
                                    isSubStep = true,
                                    playingChip = playingChip,
                                    onToggleExpand = {},
                                    onToggleVoice = ::toggleVoice,
                                    onUndo = { doUndo(sub) },
                                    onRedo = { doRedo(sub) },
                                    push = push,
                                )
                            }
                        }
                    }
                }
            }
        }

        var clearing by remember { mutableStateOf(false) }
        DestructiveCard(t("清空记录")) { clearing = true }
        if (clearing) {
            ConfirmDialog(
                t("清空最近改过？"),
                t("只清掉这些记录，设置本身不变；清掉后就不能一条条改回了。"),
                t("清空"),
                {
                    playJob?.cancel()
                    SystemVoice.Engine.stopPreview()
                    playingChip = null
                    SettingsLog.clear()
                }
            ) { clearing = false }
        }
    }
}

@Composable
private fun HistoryFilterPill(title: String, count: Int, selected: Boolean, onClick: () -> Unit) {
    Row(
        Modifier
            .height(YY.s(24))
            .clip(CircleShape)
            .background(if (selected) YY.c.card else Color.Transparent)
            .border(0.5.dp, if (selected) YY.c.pillLine else YY.c.line, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = YY.s(10)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
    ) {
        Text(title, style = YY.font(10.5f, if (selected) FontWeight.SemiBold else FontWeight.Normal), color = if (selected) YY.c.t1 else YY.c.t2)
        Text("$count", style = YY.font(9.5f, tnum = true), color = if (selected) YY.tint.accent else YY.c.t3)
    }
}

@Composable
private fun HistoryRow(
    e: SettingsLog.Entry,
    superseded: Boolean,
    olderCount: Int,
    isExpanded: Boolean,
    isSubStep: Boolean,
    playingChip: String?,
    onToggleExpand: () -> Unit,
    onToggleVoice: (String, String?, String) -> Unit,
    onUndo: () -> Unit,
    onRedo: () -> Unit,
    push: (MePage) -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clickable(enabled = e.page != null) { e.page?.let(push) }
            .padding(start = if (isSubStep) YY.s(4) else 0.dp, top = YY.s(8.5f), bottom = YY.s(8.5f)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        val icoBox = if (isSubStep) 22f else 25f
        Box(
            Modifier
                .size(YY.s(icoBox))
                .alpha(if (isSubStep) 0.72f else 1f)
                .background(YY.c.inset, RoundedCornerShape(YY.s(6.5f))),
            contentAlignment = Alignment.Center,
        ) {
            Icon(e.page?.icon ?: Icons.general, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(if (isSubStep) 11.5f else 13f)))
        }

        Column(
            Modifier.weight(1f).alpha(if (e.undone) 0.68f else if (superseded) 0.84f else 1f),
            verticalArrangement = Arrangement.spacedBy(YY.s(3.5f)),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(5))) {
                Text(
                    e.title,
                    style = YY.font(11.5f, FontWeight.SemiBold),
                    color = YY.c.t1,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                e.page?.let { p ->
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(1.dp)) {
                        Text(p.title, style = YY.font(10f), color = YY.c.t3, maxLines = 1)
                        Icon(Icons.chevronRight, null, tint = YY.c.t3.copy(alpha = 0.65f), modifier = Modifier.size(YY.s(10)))
                    }
                }
                Spacer(Modifier.weight(1f))
                Text(
                    DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(e.date)),
                    style = YY.font(9.5f, tnum = true),
                    color = YY.c.t3,
                )
            }

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(5))) {
                Box(Modifier.weight(1f, fill = false)) {
                    HistoryValueChip(
                        look = e.look,
                        raw = e.fromRaw,
                        text = e.from,
                        color = if (e.undone) YY.c.t1 else YY.c.t2,
                        weight = if (e.undone) FontWeight.Medium else FontWeight.Normal,
                        strikethrough = false,
                        chipId = "${e.id}-from",
                        playing = playingChip == "${e.id}-from",
                        onToggleVoice = onToggleVoice,
                    )
                }
                Icon(Icons.forward, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(10)))
                Box(Modifier.weight(1f, fill = false)) {
                    HistoryValueChip(
                        look = e.look,
                        raw = e.toRaw,
                        text = e.to,
                        color = if (e.undone) YY.c.t3 else if (superseded) YY.c.t2 else YY.c.t1,
                        weight = if (e.undone || superseded) FontWeight.Normal else FontWeight.SemiBold,
                        strikethrough = e.undone,
                        chipId = "${e.id}-to",
                        playing = playingChip == "${e.id}-to",
                        onToggleVoice = onToggleVoice,
                    )
                }
            }

            if (olderCount > 0) {
                Row(
                    Modifier
                        .padding(top = 1.dp)
                        .height(YY.s(19))
                        .clip(CircleShape)
                        .background(YY.c.inset)
                        .clickable(onClick = onToggleExpand)
                        .padding(horizontal = YY.s(7)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(3.dp),
                ) {
                    Text(
                        if (isExpanded) t("收起早先 %lld 次", olderCount) else t("早先 %lld 次调整", olderCount),
                        style = YY.font(9.5f),
                        color = YY.c.t3,
                    )
                    val turn by androidx.compose.animation.core.animateFloatAsState(if (isExpanded) 180f else 0f, app.yuyue.design.YYMotion.quick(), label = "chevron")
                    Icon(Icons.chevronDown, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(10)).graphicsLayer { rotationZ = turn })
                }
            }
        }

        // Fixed-width 56 design-px slot so timestamps never shift horizontally
        Box(Modifier.width(YY.s(56)), contentAlignment = Alignment.CenterEnd) {
            when {
                e.undone && e.redo != null -> {
                    Row(
                        Modifier
                            .size(width = YY.s(54), height = YY.s(23))
                            .clip(CircleShape)
                            .border(0.5.dp, YY.c.line, CircleShape)
                            .clickable(onClick = onRedo),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(3.dp, Alignment.CenterHorizontally),
                    ) {
                        Icon(Icons.redo, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(10)))
                        Text(t("还原"), style = YY.font(10f, FontWeight.Medium), color = YY.c.t2, maxLines = 1)
                    }
                }
                !e.undone && e.undo != null -> {
                    Row(
                        Modifier
                            .size(width = YY.s(54), height = YY.s(23))
                            .clip(CircleShape)
                            .background(YY.c.pill)
                            .border(0.5.dp, YY.c.pillLine, CircleShape)
                            .clickable(onClick = onUndo),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(3.dp, Alignment.CenterHorizontally),
                    ) {
                        Icon(Icons.undo, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(10)))
                        Text(t("改回"), style = YY.font(10f, FontWeight.Medium), color = YY.c.t1, maxLines = 1)
                    }
                }
            }
        }
    }
}

@Composable
private fun HistoryValueChip(
    look: String?,
    raw: String?,
    text: String,
    color: Color,
    weight: FontWeight,
    strikethrough: Boolean,
    chipId: String,
    playing: Boolean,
    onToggleVoice: (String, String?, String) -> Unit,
) {
    val dec = if (strikethrough) TextDecoration.LineThrough else null
    when (look) {
        "voice" -> {
            if (!raw.isNullOrEmpty()) {
                Row(
                    Modifier
                        .height(YY.s(22))
                        .clip(CircleShape)
                        .background(if (playing) YY.tint.accentSoft else YY.c.inset)
                        .border(if (playing) 0.8.dp else 0.5.dp, if (playing) YY.tint.edge else YY.c.line, CircleShape)
                        .clickable { onToggleVoice(chipId, raw, text) }
                        .padding(start = YY.s(5), end = YY.s(8)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
                ) {
                    Box(
                        Modifier
                            .size(YY.s(13))
                            .clip(CircleShape)
                            .background(if (playing) YY.tint.accent else YY.c.card)
                            .border(if (playing) 0.dp else 0.5.dp, if (playing) Color.Transparent else YY.c.line, CircleShape),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(
                            if (playing) Icons.waveform else Icons.play,
                            null,
                            tint = if (playing) Color.White else YY.c.t1,
                            modifier = Modifier.size(YY.s(if (playing) 8.5f else 9f)),
                        )
                    }
                    Text(
                        text,
                        style = YY.font(10f, if (playing) FontWeight.SemiBold else weight).copy(textDecoration = dec),
                        color = if (playing) YY.tint.accent else color,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
            } else {
                Text(text, style = YY.font(10.5f, weight).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        "paper" -> {
            val bg = raw?.let { id ->
                paperPresets.firstOrNull { it.tokens.id == id }?.tokens?.bg
                    ?: Papers.paper(id)?.let { customTokens(it).bg }
            } ?: YY.c.inset
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(4.5f))) {
                Box(
                    Modifier
                        .size(YY.s(10.5f))
                        .clip(CircleShape)
                        .background(bg)
                        .border(0.6.dp, YY.c.t1.copy(alpha = 0.2f), CircleShape)
                )
                Text(text, style = YY.font(10.5f, weight).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        "toggle" -> {
            val on = raw == "1"
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(4.5f))) {
                Box(
                    Modifier
                        .size(width = YY.s(18), height = YY.s(11))
                        .clip(CircleShape)
                        .background(if (on) YY.c.ok else YY.c.tint)
                        .padding(horizontal = YY.s(1.5f)),
                    contentAlignment = if (on) Alignment.CenterEnd else Alignment.CenterStart,
                ) {
                    Box(Modifier.size(YY.s(7.8f)).clip(CircleShape).background(Color.White))
                }
                Text(text, style = YY.font(10.5f, weight).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        "font" -> {
            val fam = if (raw != null && raw != "" && raw != "SANS") YY.serif else FontFamily.Default
            Text(text, style = YY.font(10.5f, weight, family = fam).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        "mono" -> {
            Text(text, style = YY.font(10.5f, weight, tnum = true).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        else -> {
            Text(text, style = YY.font(10.5f, weight).copy(textDecoration = dec), color = color, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

private fun dayName(ms: Long): String {
    val now = System.currentTimeMillis()
    if (SettingsLog.sameDay(ms, now)) return t("今天")
    if (SettingsLog.sameDay(ms, now - 86_400_000L)) return t("昨天")
    return day(ms)
}

// MARK: Panels — the Mac's right column, as a phone page

/** A service's mark on its white square with a hairline: the Mac's ServiceTile. */
@Composable
internal fun ServiceTile(size: Float = 26f, content: @Composable () -> Unit) {
    Box(
        Modifier.size(YY.s(size)).clip(RoundedCornerShape(YY.s(size * 0.27f))).background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(size * 0.27f))),
        contentAlignment = Alignment.Center,
    ) { content() }
}

/** A panel's head: its tile, its name, a grey line (green when it is the one in use). */
@Composable
internal fun PanelHeader(title: String, detail: String?, detailColor: Color = YY.c.t3, tile: @Composable () -> Unit) {
    Row(Modifier.padding(start = YY.s(4), top = YY.s(14), bottom = YY.s(12)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        tile()
        Column {
            Text(title, style = YY.font(14f, FontWeight.SemiBold), color = YY.c.t1)
            detail?.let { Text(it, style = YY.font(10.5f), color = detailColor) }
        }
    }
}

/** 移除 / 断开 / 清空: red words in the middle of a card of their own, at the foot of the page. */
@Composable
internal fun DestructiveCard(text: String, onClick: () -> Unit) {
    YYGroup(Modifier.padding(top = YY.s(20))) {
        row {
            Text(text, style = YY.font(11.5f), color = YY.c.danger, textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = YY.s(11)))
        }
    }
}

/** A row that leaves 语阅 for a page (拿一个 API Key, 下载更自然的声音): ↗ and its words. */
@Composable
internal fun LinkRow(title: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = YY.s(40)).clickable(onClick = onClick), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        Icon(Icons.external, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(14)))
        Text(title, style = YY.font(11.5f), color = YY.c.t1)
    }
}

/** One step of connecting: its number in a circle (a tick once done), what to do, where; tapped, it opens the page. */
@Composable
internal fun StepRow(n: Int, done: Boolean, title: String, detail: String, onClick: (() -> Unit)? = null) {
    Row(
        Modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(vertical = YY.s(9)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Box(Modifier.size(YY.s(20)).background(if (done) YY.c.ok.copy(alpha = 0.14f) else YY.c.inset, RoundedCornerShape(50)), contentAlignment = Alignment.Center) {
            if (done) Icon(Icons.check, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(12)))
            else Text("$n", style = YY.font(10.5f, FontWeight.SemiBold), color = YY.c.t2)
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
            Text(title, style = YY.font(11.5f), color = YY.c.t1)
            Text(detail, style = YY.font(10f), color = YY.c.t3)
        }
        if (onClick != null) Icon(Icons.external, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(12)))
    }
}

/** What connecting found so far: a tick for each thing that worked, a line for what's being asked,
 *  then a green band; or a red line saying what's wrong. */
@Composable
internal fun ChecksCard(items: List<String>, next: String?, failure: String?, done: Boolean, banner: String? = null) {
    if (items.isEmpty() && next == null && failure == null) return
    Column(
        Modifier.padding(top = YY.s(10)).fillMaxWidth().clip(RoundedCornerShape(YY.s(11))).background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(11))).padding(YY.s(12)),
        verticalArrangement = Arrangement.spacedBy(YY.s(7)),
    ) {
        for (item in items) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.checkCircle, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(13)))
            Text(item, style = YY.font(11f), color = YY.c.t1)
        }
        next?.let {
            Row(horizontalArrangement = Arrangement.spacedBy(YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
                androidx.compose.material3.CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                Text(it, style = YY.font(11f), color = YY.c.t3)
            }
        }
        failure?.let {
            Row(horizontalArrangement = Arrangement.spacedBy(YY.s(8)), verticalAlignment = Alignment.Top) {
                Icon(Icons.error, null, tint = YY.c.danger, modifier = Modifier.size(YY.s(13)))
                Text(it, style = YY.font(11f), color = YY.c.danger)
            }
        }
        if (done) Row(
            Modifier.fillMaxWidth().background(YY.c.ok.copy(alpha = 0.12f), RoundedCornerShape(YY.s(8))).padding(horizontal = YY.s(10), vertical = YY.s(7)),
            horizontalArrangement = Arrangement.spacedBy(YY.s(7)), verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(Icons.check, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(12)))
            Text(banner ?: t("已连上"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.ok)
        }
    }
}

/** Opens a link outside 语阅. */
internal fun openLink(context: android.content.Context, url: String) {
    runCatching { context.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url)).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)) }
}

/** The red words in the middle of a row, as 移除 has them; faded while there's nothing to do. */
@Composable
internal fun DestructiveRow(text: String, enabled: Boolean = true, onClick: () -> Unit) {
    Text(text, style = YY.font(11.5f), color = YY.c.danger.copy(alpha = if (enabled) 1f else 0.35f), textAlign = TextAlign.Center,
        modifier = Modifier.fillMaxWidth().clickable(enabled = enabled, onClick = onClick).padding(vertical = YY.s(11)))
}

/** A question waiting to be asked by [ConfirmDialog]: set it from a menu, show it with [ConfirmHost]. */
internal class Confirm(val title: String, val text: String, val action: String, val run: () -> Unit)

@Composable
internal fun ConfirmHost(asking: Confirm?, dismiss: () -> Unit) {
    if (asking != null) ConfirmDialog(asking.title, asking.text, asking.action, asking.run, dismiss)
}

/** Asks before anything is thrown away: what goes, what's kept; the red word does it. */
@Composable
internal fun ConfirmDialog(title: String, text: String, action: String, confirm: () -> Unit, dismiss: () -> Unit) {
    androidx.compose.material3.AlertDialog(
        onDismissRequest = dismiss,
        containerColor = YY.c.card,
        title = { Text(title, color = YY.c.t1) },
        text = { Text(text, color = YY.c.t2) },
        confirmButton = { androidx.compose.material3.TextButton({ dismiss(); confirm() }) { Text(action, color = YY.c.danger) } },
        dismissButton = { androidx.compose.material3.TextButton(dismiss) { Text(t("取消"), color = YY.c.t2) } },
    )
}
