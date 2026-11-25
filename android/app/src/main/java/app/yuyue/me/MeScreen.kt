package app.yuyue.me

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import android.text.format.Formatter
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.BuildConfig
import app.yuyue.design.Appearance
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.YYTints
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.t
import uniffi.yuyue_core.ReadingStatus
import java.time.DayOfWeek
import kotlin.math.roundToInt
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.temporal.TemporalAdjusters

/** A page under 我的: the Mac's settings pages, then what only the phone has. */
enum class MePage {
    General, Appearance, Reading, Foreign, Listening, VoiceSource, VoiceSourceSettings, Companion, CompanionService, CompanionModels,
    Sync, NoteExport, NoteTarget, Storage, Snapshot, History,
    Feedback, Update, EInk, Sources, Stats, About, Privacy, Licences, Wifi,
}

/** A page and what it is about (a voice source, a service). */
data class MeRoute(val page: MePage, val arg: String? = null)

/** 我的, as on iPhone: this week's reading and listening on top, the year's
 *  totals under it, then the Mac's settings sidebar, in its order and its groups. */
@Composable
fun MeScreen(library: Library, pop: Int = 0, isForeground: Boolean = true) {
    val stack = remember {
        mutableStateListOf<MeRoute>().apply {
            // Made again for a new language: back on 通用, where it was chosen.
            if (app.yuyue.AppLanguage.reopenGeneral) { add(MeRoute(MePage.General)); app.yuyue.AppLanguage.reopenGeneral = false }
        }
    }
    LaunchedEffect(pop) { if (pop > 0) stack.clear() }
    val meScreenName = when (stack.lastOrNull()?.page) {
        null -> "Me"
        MePage.About, MePage.Privacy, MePage.Licences -> "About"
        MePage.Feedback -> "Feedback"
        else -> "Settings"
    }
    LaunchedEffect(isForeground, meScreenName) {
        if (isForeground) app.yuyue.analytics.AirTraffic.screen(meScreenName)
    }
    val push: (MePage) -> Unit = { stack += MeRoute(it) }
    val open: (MePage, String) -> Unit = { page, arg -> stack += MeRoute(page, arg) }
    val back: () -> Unit = { stack.removeAt(stack.lastIndex) }
    BackHandler(enabled = stack.isNotEmpty()) { back() }
    // Deeper slides in a short way from the right, back from the left.
    val reduce = app.yuyue.design.YYMotion.isReduceMotion()
    androidx.compose.animation.AnimatedContent(
        stack.lastOrNull() to stack.size,
        transitionSpec = { app.yuyue.design.YYMotion.push(targetState.second > initialState.second, reduce) },
        label = "me",
    ) { (route, _) ->
    when (route?.page) {
        null -> Main(library, push)
        MePage.Sources -> app.yuyue.shelf.SourcesHome(library, back)
        else -> SubPage(route.title, back, action = gear(route) { open(MePage.VoiceSourceSettings, route.arg ?: "") }) {
            when (route.page) {
                MePage.General -> GeneralPage(library, push)
                MePage.Appearance -> AppearancePage()
                MePage.Reading -> ReadingPage(library, push)
                MePage.Foreign -> ForeignPage(library.core)
                MePage.Listening -> ListeningPage { open(MePage.VoiceSource, it) }
                MePage.VoiceSource -> VoiceSourcePage(route.arg ?: "") { open(MePage.VoiceSourceSettings, route.arg ?: "") }
                MePage.Companion -> app.yuyue.companion.CompanionSettings { open(MePage.CompanionService, it) }
                MePage.CompanionService -> app.yuyue.companion.ServicePage(route.arg ?: "", { open(MePage.CompanionModels, route.arg ?: "") }, back)
                MePage.CompanionModels -> app.yuyue.companion.ModelsPage(route.arg ?: "", back)
                MePage.Sync -> SyncPage(library.sync)
                MePage.NoteExport -> app.yuyue.notes.NoteExportPage { open(MePage.NoteTarget, it.key) }
                MePage.NoteTarget -> app.yuyue.notes.NoteExport.Target.entries.firstOrNull { it.key == route.arg }?.let { app.yuyue.notes.NoteTargetPage(it) }
                MePage.VoiceSourceSettings -> VoiceSourceSettings(route.arg ?: "", back)
                MePage.Storage -> StoragePage(library)
                MePage.Snapshot -> SnapshotPage(library)
                MePage.History -> HistoryPage(push)
                MePage.Stats -> StatsPage(library)
                MePage.About -> About(push)
                MePage.Feedback -> app.yuyue.feedback.FeedbackPage(back)
                MePage.Privacy -> TextPage(PRIVACY)
                MePage.Licences -> TextPage(LICENCES)
                MePage.Wifi -> app.yuyue.shelf.WifiPage(library)
                MePage.EInk -> app.yuyue.reader.EInkPage(library.core)
                MePage.Sources -> {}
                MePage.Update -> app.yuyue.update.UpdateScreen()
            }
        }
    }
    }
}

/** ⚙ on a voice source's page that is connected: its key and connection, or this phone's voices. */
private fun gear(route: MeRoute, open: () -> Unit): (@Composable () -> Unit)? {
    if (route.page != MePage.VoiceSource) return null
    val p = app.yuyue.speech.CloudVoice.Provider.entries.firstOrNull { it.key == route.arg }
    if (p != null && !p.isSetUp) return null
    return {
        val label = t("%@ 的设置", voiceSourceTitle(route.arg ?: ""))
        Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = label, onClick = open), contentAlignment = Alignment.Center) {
            Icon(Icons.settings, label, tint = YY.c.t2, modifier = Modifier.size(22.dp))
        }
    }
}

private val MeRoute.title: String
    get() = when (page) {
        MePage.VoiceSource -> voiceSourceTitle(arg ?: "")
        MePage.CompanionService -> app.yuyue.companion.CompanionProvider.find(arg ?: "")?.title ?: t("书伴")
        MePage.CompanionModels -> t("选择模型")
        MePage.NoteTarget -> app.yuyue.notes.NoteExport.Target.entries.firstOrNull { it.key == arg }?.title ?: t("笔记导出")
        MePage.VoiceSourceSettings -> voiceSourceTitle(arg ?: "")
        else -> page.title
    }

internal val MePage.title: String
    get() = when (this) {
        MePage.General -> t("通用")
        MePage.Appearance -> t("外观")
        MePage.Reading -> t("阅读")
        MePage.Foreign -> t("外文")
        MePage.Listening, MePage.VoiceSource, MePage.VoiceSourceSettings -> t("朗读")
        MePage.Companion, MePage.CompanionService, MePage.CompanionModels -> t("书伴")
        MePage.Sync -> t("同步")
        MePage.NoteExport, MePage.NoteTarget -> t("笔记导出")
        MePage.Storage -> t("存储")
        MePage.Snapshot -> t("数据")
        MePage.History -> t("最近改过")
        MePage.Stats -> t("阅读统计")
        MePage.About -> t("关于")
        MePage.Feedback -> t("反馈")
        MePage.Privacy -> t("隐私说明")
        MePage.Licences -> t("开源许可")
        MePage.Sources -> t("书库来源")
        MePage.EInk -> t("墨水屏")
        MePage.Wifi -> t("Wi‑Fi 传书")
        MePage.Update -> t("检查更新")
    }

/** Each page's icon, as the Mac's sidebar has it. */
internal val MePage.icon: ImageVector
    get() = when (this) {
        MePage.General -> Icons.general
        MePage.Appearance -> Icons.appearance
        MePage.Reading -> Icons.book
        MePage.EInk -> Icons.eink
        MePage.Foreign -> Icons.globe
        MePage.Listening, MePage.VoiceSource, MePage.VoiceSourceSettings -> Icons.headphones
        MePage.Companion, MePage.CompanionService, MePage.CompanionModels -> Icons.textBubble
        MePage.Sync -> Icons.icloud
        MePage.NoteExport, MePage.NoteTarget -> Icons.share
        MePage.Storage -> Icons.storage
        MePage.Snapshot -> Icons.snapshot
        MePage.History -> Icons.history
        MePage.Sources -> Icons.server
        MePage.Wifi -> Icons.wifi
        MePage.Feedback -> Icons.feedback
        MePage.Update -> Icons.arrowDownCircle
        else -> Icons.info
    }

/** A page pushed over 我的: its name in the middle, ‹ on the left. */
@Composable
fun SubPage(title: String, back: () -> Unit, action: (@Composable () -> Unit)? = null, content: @Composable () -> Unit) {
    Column(Modifier.fillMaxSize().background(YY.c.bg).windowInsetsPadding(WindowInsets.statusBars)) {
        Box(Modifier.fillMaxWidth().height(52.dp)) {
            Box(
                Modifier.align(Alignment.CenterStart).padding(start = 2.dp).size(44.dp).clip(CircleShape).clickable(onClickLabel = t("返回"), onClick = back),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.back, t("返回"), tint = YY.c.t1, modifier = Modifier.size(24.dp))
            }
            Text(title, fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, modifier = Modifier.align(Alignment.Center).padding(horizontal = 52.dp))
            if (action != null) Box(Modifier.align(Alignment.CenterEnd).padding(end = 2.dp)) { action() }
        }
        Box(Modifier.weight(1f)) { content() }
    }
}

@Composable
private fun Main(library: Library, push: (MePage) -> Unit) {
    val mode by rememberPref(Appearance.KEY, Appearance.SYSTEM)
    val tintID by rememberPref("appearance.tint", YYTints.Default.id)
    // Read again whenever a setting changes, as the rows' grey values say what's set.
    val provider by rememberPref("companion.provider", "")
    val added by rememberPref("companion.added", "")
    val current = remember(provider, added) { app.yuyue.companion.CompanionConfig.current }
    var used by remember { mutableStateOf<Long?>(null) }
    var lastSnapshot by remember { mutableStateOf<Long?>(null) }
    LaunchedEffect(Unit) {
        withContext(Dispatchers.IO) {
            used = usedBytes(library)
            lastSnapshot = lastSnapshotAt()
        }
    }
    val context = LocalContext.current
    val scroll = androidx.compose.foundation.rememberScrollState()
    val density = androidx.compose.ui.platform.LocalDensity.current
    // Scrolled past 我的: the small title pinned at the top.
    val pinned = scroll.value > with(density) { 50.dp.toPx() }
    Box(Modifier.fillMaxSize().background(YY.c.bg).windowInsetsPadding(WindowInsets.statusBars)) {
        YYPage(scroll = scroll) {
            // The title is the page's own, as on the shelf.
            app.yuyue.shelf.PageTitle(app.yuyue.design.TabIcons.me, t("我的"), Modifier.padding(top = 14.dp, bottom = 4.dp))
            Box(Modifier.padding(top = YY.s(14), bottom = YY.s(10)).clickable { push(MePage.Stats) }) { WeekCard(library) }
            Group(null) {
                link(MePage.General, null, push)
                link(MePage.Appearance, "${modeTitle(mode)} · ${YYTints.All.firstOrNull { it.id == tintID }?.name?.let { t(it) } ?: ""}", push)
            }
            Group(t("阅读与朗读")) {
                link(MePage.Reading, null, push)
                link(MePage.Foreign, null, push)
                link(MePage.Listening, null, push)
            }
            Group(t("连接")) {
                link(MePage.Companion, current?.provider?.title ?: t("未连接"), push)
                val sync = library.sync
                val place = if (sync.method == "drive") "Google Drive" else "WebDAV"
                link(MePage.Sync, when {
                    !sync.on -> t("未开启")
                    sync.state is app.yuyue.sync.Sync.State.Failed -> t("%@ · 同步未成功", place)
                    else -> listOfNotNull(place, sync.lastSynced?.let { ago(it) }).joinToString(" · ")
                }, push)
                val on = app.yuyue.notes.NoteExport.Target.entries.filter { app.yuyue.notes.NoteExport.isSetUp(it) }.map { it.title }
                link(MePage.NoteExport, if (on.isEmpty()) t("未连接") else on.joinToString(" · "), push)
            }
            Group(t("这台手机")) {
                link(MePage.Storage, used?.let { Formatter.formatShortFileSize(context, it) }, push)
                link(MePage.Snapshot, lastSnapshot?.let { day(it) } ?: t("换设备、备份"), push)
                val n = SettingsLog.changedToday
                link(MePage.History, if (n > 0) t("今天 %lld 处", n) else null, push)
            }
            Group(null) {
                link(MePage.Feedback, if (app.yuyue.feedback.Feedback.EARLY) t("早期版本") else null, push)
                // A new version lights its dot here; 检查更新 is inside 关于.
                row {
                    val update = app.yuyue.update.Updates
                    val badge = update.enabled && update.showsBadge
                    YYRow(t("关于"), MePage.About.icon, if (badge) update.offer?.display else BuildConfig.VERSION_NAME, chevron = true, trailing = {
                        if (badge) Box(Modifier.size(8.dp).clip(CircleShape).background(YY.c.danger))
                    }) { push(MePage.About) }
                }
            }
        }
        androidx.compose.animation.AnimatedVisibility(pinned, enter = androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.quick()), exit = androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit())) {
            Column(Modifier.fillMaxWidth().background(YY.c.bg)) {
                Box(Modifier.fillMaxWidth().height(44.dp), contentAlignment = Alignment.Center) { Text(t("我的"), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1) }
                Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
            }
        }
    }
}

private fun app.yuyue.design.GroupScope.link(page: MePage, value: String?, push: (MePage) -> Unit) {
    row { YYRow(page.title, page.icon, value, chevron = true) { push(page) } }
}

private fun usedBytes(library: Library): Long {
    fun size(f: java.io.File): Long = if (f.isFile) f.length() else f.listFiles()?.sumOf(::size) ?: 0
    return size(library.dataDir)
}

private fun lastSnapshotAt(): Long? =
    java.io.File(app.yuyue.YuyueApp.instance.filesDir, "snapshots").listFiles()?.filter { it.extension == "yuyue" }?.maxOfOrNull { it.lastModified() }

/** One group, under its small grey name as in the Mac's sidebar. */
@Composable
private fun Group(title: String?, build: app.yuyue.design.GroupScope.() -> Unit) {
    if (title != null) Text(title, style = YY.font(10f, FontWeight.Medium), color = YY.c.t3,
        modifier = Modifier.padding(start = YY.s(4), top = YY.s(4), bottom = YY.s(6)))
    // A group without a name sits as far from the one above as a named group's name does.
    YYGroup(Modifier.padding(top = if (title == null) YY.s(6) else 0.dp, bottom = YY.s(10)), build = build)
}


private fun modeTitle(mode: String) = when (mode) {
    Appearance.LIGHT -> t("浅色")
    Appearance.DARK -> t("深色")
    else -> t("跟随系统")
}

// MARK: 这周

private data class DaySums(val day: LocalDate, val read: Int, val listen: Int)

private fun weekDays(of: LocalDate = LocalDate.now()): List<LocalDate> {
    val monday = of.with(TemporalAdjusters.previousOrSame(DayOfWeek.MONDAY))
    return (0 until 7).map { monday.plusDays(it.toLong()) }
}

private fun sums(library: Library, days: List<LocalDate>): List<DaySums> {
    val times = runCatching { library.core.dayTimes(days.first().toString(), days.last().toString()) }.getOrDefault(emptyList())
    return days.map { d ->
        val k = d.toString()
        val ts = times.filter { it.day == k }
        DaySums(d, ts.sumOf { it.readSeconds.toInt() }, ts.sumOf { it.listenSeconds.toInt() })
    }
}

/** This week's reading and listening, a column a day stacked, and under them the
 *  books finished this year, the lines marked and the words kept. */
@Composable
private fun WeekCard(library: Library) {
    val days = remember(library.notesVersion) { sums(library, weekDays()) }
    val marks = remember(library.notesVersion, library.books) { runCatching { library.core.markCounts() }.getOrDefault(emptyMap()).let { counts -> library.books.filter { it.available }.sumOf { (counts[it.id] ?: 0u).toInt() } } }
    val words = remember(library.notesVersion) { runCatching { library.core.words().count { !it.known } }.getOrDefault(0) }
    val year = LocalDate.now().year
    val finished = library.books.count { b ->
        b.status == ReadingStatus.FINISHED && Instant.ofEpochMilli(b.finishedAt ?: b.openedAt ?: b.addedAt).atZone(ZoneId.systemDefault()).year == year
    }
    Column(
        Modifier.fillMaxWidth().shadow(2.dp, RoundedCornerShape(YY.s(16))).background(YY.c.card, RoundedCornerShape(YY.s(16)))
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16))).padding(horizontal = YY.s(14), vertical = YY.s(12)),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(t("这周"), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.weight(1f))
            Text(t("阅读统计"), style = YY.font(10.5f), color = YY.c.t2)
            Icon(Icons.chevronRight, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(12)))
        }
        Row(Modifier.padding(top = YY.s(6)), horizontalArrangement = Arrangement.spacedBy(YY.s(20))) {
            PeriodTotal(t("读"), days.sumOf { it.read }, YY.tint.fill)
            PeriodTotal(t("听"), days.sumOf { it.listen }, YY.c.tint)
        }
        Bars(days, Modifier.padding(top = YY.s(10)).height(YY.s(70)))
        Box(Modifier.padding(top = YY.s(12)).fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.padding(top = YY.s(11)).fillMaxWidth()) {
            Tile("$finished", t("本"), t("今年读完"), Modifier.weight(1f))
            Box(Modifier.width(0.5.dp).height(YY.s(30)).background(YY.c.line))
            Tile("$marks", t("句"), t("划线"), Modifier.weight(1f))
            Box(Modifier.width(0.5.dp).height(YY.s(30)).background(YY.c.line))
            Tile("$words", t("个"), t("生词"), Modifier.weight(1f))
        }
    }
}

@Composable
private fun Tile(number: String, unit: String, label: String, modifier: Modifier) {
    Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
        Row(horizontalArrangement = Arrangement.spacedBy(YY.s(2))) {
            Text(number, style = YY.font(15f, FontWeight.Bold, tnum = true), color = YY.c.t1, modifier = Modifier.alignByBaseline())
            if (unit.isNotEmpty()) Text(unit, style = YY.font(10f, FontWeight.Medium), color = YY.c.t2, modifier = Modifier.alignByBaseline())
        }
        Text(label, style = YY.font(10f), color = YY.c.t3)
    }
}

/** "■ 读 5 小时 12 分", the numbers big. */
@Composable
internal fun PeriodTotal(label: String, seconds: Int, color: Color) {
    val m = seconds / 60
    val parts = when {
        m < 60 -> listOf(m to t("分"))
        m % 60 == 0 -> listOf(m / 60 to t("小时"))
        else -> listOf(m / 60 to t("小时"), m % 60 to t("分"))
    }
    Row {
        Box(Modifier.align(Alignment.CenterVertically).padding(end = YY.s(3)).size(YY.s(7)).background(color, RoundedCornerShape(2.dp)))
        Text(label, style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.alignByBaseline())
        for ((n, unit) in parts) {
            Text("$n", style = YY.font(19f, FontWeight.Bold, tnum = true), color = YY.c.t1, modifier = Modifier.padding(start = YY.s(4), end = YY.s(1)).alignByBaseline())
            Text(unit, style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.alignByBaseline())
        }
    }
}

/** Seven columns, listening stacked on reading; today's column wider and its label dark. */
@Composable
private fun Bars(days: List<DaySums>, modifier: Modifier) {
    val most = maxOf(1, days.maxOf { it.read + it.listen })
    val today = LocalDate.now()
    val names = if (app.yuyue.L10n.chinese) listOf("一", "二", "三", "四", "五", "六", "日")
    else DayOfWeek.entries.map { it.getDisplayName(java.time.format.TextStyle.SHORT, java.util.Locale.getDefault()) }
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.Bottom) {
        days.forEachIndexed { i, d ->
            val on = d.day == today
            Column(Modifier.weight(1f).fillMaxHeight(), horizontalAlignment = Alignment.CenterHorizontally) {
                Column(Modifier.weight(1f).width(YY.s(if (on) 12 else 8)), verticalArrangement = Arrangement.Bottom) {
                    if (d.listen > 0) Box(Modifier.fillMaxWidth().fillMaxHeight(d.listen.toFloat() / most).background(YY.c.tint, RoundedCornerShape(topStart = 2.5.dp, topEnd = 2.5.dp)))
                    if (d.read > 0) Box(Modifier.fillMaxWidth().fillMaxHeight(d.read.toFloat() / (most - d.listen).coerceAtLeast(1)).background(YY.tint.fill, RoundedCornerShape(if (d.listen > 0) 0.dp else 2.5.dp)))
                }
                Text(if (on) t("今天") else names[i], style = YY.font(10f, if (on) FontWeight.SemiBold else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t3, modifier = Modifier.padding(top = YY.s(5)))
            }
        }
    }
}

// MARK: 阅读统计

// MARK: 外观

@Composable
internal fun AppearancePage() {
    var mode by rememberPref(Appearance.KEY, Appearance.SYSTEM)
    var tintID by rememberPref("appearance.tint", YYTints.Default.id)
    var coverSize by rememberPref(Shelf.COVER_SIZE, 100f)
    var showProgress by rememberPref(Shelf.SHOW_PROGRESS, true)
    var viewStyleRaw by rememberPref(app.yuyue.shelf.ShelfStylePrefs.VIEW_KEY, app.yuyue.shelf.ShelfViewStyle.GRID.key)
    var woodLedge by rememberPref(app.yuyue.shelf.ShelfStylePrefs.WOOD_KEY, false)
    var liquidGlass by rememberPref(app.yuyue.design.LiquidGlass.KEY, false)
    val eink by rememberPref(app.yuyue.reader.EInk.KEY, false)
    YYPage {
        SettingsLead(t("应用界面的外观与配色；书页纸色在「阅读」或 Aa 中设置"))
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                Column(Modifier.padding(vertical = YY.s(12)), verticalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    Text(t("界面"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                    Row(horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                        for (m in listOf(Appearance.LIGHT, Appearance.DARK, Appearance.SYSTEM)) {
                            val on = m == mode
                            Column(Modifier.weight(1f).clickable { mode = m }, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(7))) {
                                ModeTile(m, on)
                                Text(modeTitle(m), style = YY.font(11f, if (on) FontWeight.SemiBold else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t2)
                            }
                        }
                    }
                }
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                Row(Modifier.padding(vertical = YY.s(12)), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                        Text(t("主题色"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                        Text(t("用于按钮、开关与进度标记"), style = YY.font(10f), color = YY.c.t2)
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                        for (tint in YYTints.All) {
                            val on = tint.id == tintID
                            Column(Modifier.clickable { tintID = tint.id }, horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(5))) {
                                Box(Modifier.size(YY.s(27)).border(2.dp, if (on) tint.light.accent else Color.Transparent, CircleShape).padding(YY.s(2.5f)).background(tint.light.accent, CircleShape))
                                Text(t(tint.name), style = YY.font(10f, if (on) FontWeight.SemiBold else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t2, maxLines = 1)
                            }
                        }
                    }
                }
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                Column(Modifier.padding(vertical = YY.s(10)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
                    Column(verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                        Text(t("书架封面大小"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                        Text(t("随屏幕宽度自动排布"), style = YY.font(10f), color = YY.c.t2)
                    }
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                        Box(Modifier.width(8.dp).height(11.dp).background(YY.c.tint, RoundedCornerShape(1.5.dp)))
                        app.yuyue.design.YYSlider(coverSize, { coverSize = ((it / 10f).roundToInt() * 10f).coerceIn(80f, 130f) }, 80f..130f, Modifier.weight(1f), description = t("书架封面大小"), state = "${coverSize.roundToInt()}")
                        Box(Modifier.width(11.dp).height(16.dp).background(YY.c.tint, RoundedCornerShape(2.dp)))
                    }
                }
            }
            row { DetailSwitch(t("封面下显示进度"), t("关闭后仅显示书名"), showProgress) { showProgress = it } }
            row {
                YYRow(t("书架样式"), trailing = {
                    app.yuyue.design.YYSegments(
                        listOf(
                            app.yuyue.shelf.ShelfViewStyle.GRID to t("封面"),
                            app.yuyue.shelf.ShelfViewStyle.SPINE to t("书脊"),
                        ),
                        app.yuyue.shelf.ShelfViewStyle.of(viewStyleRaw),
                        { viewStyleRaw = it.key },
                        small = true,
                    )
                })
            }
            row { DetailSwitch(t("浅木层板"), t("为书架每排加上浅木层板与柔和投影"), woodLedge) { woodLedge = it } }
        }
        if (app.yuyue.design.LiquidGlass.supported) {
            Spacer(Modifier.height(YY.s(12)))
            YYGroup {
                row {
                    DetailSwitch(
                        t("液态玻璃效果"),
                        t("底部导航栏使用液态玻璃，关闭则为毛玻璃"),
                        liquidGlass && !eink,
                        enabled = !eink,
                    ) { liquidGlass = it }
                }
            }
        }
    }
}

/** A little page in light, dark, or both split down a slant; the chosen one ringed. */
@Composable
private fun ModeTile(m: String, on: Boolean) {
    val radius = YY.s(13)
    val ring = YY.s(4.5f)
    val tint = YY.tint
    Box(
        Modifier.fillMaxWidth()
            .then(if (on) Modifier.border(YY.s(2.5f), tint.accentSoft, RoundedCornerShape(radius + ring)).padding(YY.s(2.5f))
                .border(2.dp, tint.accent, RoundedCornerShape(radius + YY.s(2f))).padding(YY.s(2f)) else Modifier.padding(ring))
            .height(YY.s(74)).clip(RoundedCornerShape(radius)).border(0.5.dp, YY.c.line, RoundedCornerShape(radius)),
    ) {
        androidx.compose.foundation.Canvas(Modifier.matchParentSize()) {
            fun page(bg: Color, bar: Color, title: Color) {
                drawRect(bg)
                val left = 11.dp.toPx(); val top = 13.dp.toPx(); val w = size.width - left * 2
                drawRoundRect(title, androidx.compose.ui.geometry.Offset(left, top), androidx.compose.ui.geometry.Size(w * 0.55f, 7.dp.toPx()), androidx.compose.ui.geometry.CornerRadius(3.dp.toPx()))
                drawRoundRect(bar, androidx.compose.ui.geometry.Offset(left, top + 15.dp.toPx()), androidx.compose.ui.geometry.Size(w, 9.dp.toPx()), androidx.compose.ui.geometry.CornerRadius(3.dp.toPx()))
                drawRoundRect(bar, androidx.compose.ui.geometry.Offset(left, top + 29.dp.toPx()), androidx.compose.ui.geometry.Size(w, 9.dp.toPx()), androidx.compose.ui.geometry.CornerRadius(3.dp.toPx()))
            }
            val light = Triple(Color(0xFFF6F5F3), Color.White, Color(0xBF2A2B2E))
            val dark = Triple(Color(0xFF101112), Color(0xFF1B1C1E), Color(0xBFEDEDEE))
            when (m) {
                Appearance.LIGHT -> page(light.first, light.second, light.third)
                Appearance.DARK -> page(dark.first, dark.second, dark.third)
                else -> {
                    page(light.first, light.second, light.third)
                    // The dark half: right of a line leaning 20° off upright.
                    val lean = size.height * kotlin.math.tan(Math.PI / 9).toFloat() / 2
                    val path = androidx.compose.ui.graphics.Path().apply {
                        moveTo(size.width / 2 + lean, 0f); lineTo(size.width, 0f); lineTo(size.width, size.height); lineTo(size.width / 2 - lean, size.height); close()
                    }
                    clipPath(path) { page(dark.first, dark.second, dark.third) }
                }
            }
        }
    }
}

// MARK: 关于

@Composable
private fun About(push: (MePage) -> Unit) {
    YYPage {
        Column(Modifier.fillMaxWidth().padding(top = YY.s(24)), horizontalAlignment = Alignment.CenterHorizontally) {
            // 阅 on paper, the blue rule under it.
            Box(
                Modifier.size(YY.s(64)).shadow(8.dp, RoundedCornerShape(YY.s(15))).background(Color(0xFFFBFAF7), RoundedCornerShape(YY.s(15))),
                contentAlignment = Alignment.Center,
            ) {
                Text("阅", fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = YY.sp(30f), color = Color(0xFF1C1D20), modifier = Modifier.padding(bottom = YY.s(6)))
                Box(Modifier.align(Alignment.BottomCenter).padding(bottom = YY.s(13)).width(YY.s(24)).height(YY.s(2.5f)).background(Color(0xFF2B4C7E), CircleShape))
            }
            Text(t("语阅"), style = YY.font(17f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(10)))
            Text(t("版本 %@", "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})"), style = YY.font(10f, tnum = true), color = YY.c.t3)
            Text(t("免费，书库与笔记留在本地，没有账号与广告。"), style = YY.font(12.5f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(22), bottom = YY.s(26)))
        }
        val open = androidx.compose.ui.platform.LocalUriHandler.current
        YYGroup {
            if (app.yuyue.update.Updates.enabled) row {
                val update = app.yuyue.update.Updates
                YYRow(t("检查更新"), Icons.arrowDownCircle, if (update.showsBadge) update.offer?.display else null, chevron = true, trailing = {
                    if (update.showsBadge) Box(Modifier.size(8.dp).clip(CircleShape).background(YY.c.danger))
                }) { push(MePage.Update) }
            }
            row { YYRow(t("开源许可"), Icons.code, chevron = true) { push(MePage.Licences) } }
            row { YYRow(t("隐私说明"), Icons.shieldCheck, t("书库仅存本机 · 可关匿名统计"), chevron = true) { push(MePage.Privacy) } }
        }
        Spacer(Modifier.height(YY.s(12)))
        var analyticsEnabled by rememberPref(app.yuyue.analytics.AirTraffic.PREF_KEY, true)
        YYGroup {
            row {
                DetailSwitch(
                    t("匿名使用统计"),
                    t("仅记录功能使用形态，不关联身份，绝不收集书名、笔记或搜索词"),
                    analyticsEnabled,
                ) {
                    analyticsEnabled = it
                    app.yuyue.analytics.AirTraffic.setEnabled(it)
                }
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row { YYRow(t("作者"), Icons.person, "nowtin", chevron = true) { open.openUri("https://github.com/nowtin") } }
            row { YYRow(t("源代码"), Icons.link, "GitHub", chevron = true) { open.openUri("https://github.com/inokoru/yuyue") } }
            row { YYRow(t("问题与建议"), Icons.feedback, t("前往 GitHub 反馈"), chevron = true) { open.openUri("https://github.com/inokoru/yuyue/issues") } }
        }
        Box(Modifier.fillMaxWidth().padding(top = YY.s(96), bottom = YY.s(40)), contentAlignment = Alignment.Center) { PencilSignature() }
    }
}

@Composable
private fun TextPage(text: String) {
    YYPage {
        Text(
            text, style = YY.font(11.5f).copy(lineHeight = YY.sp(19.5f)), color = YY.c.t1,
            modifier = Modifier.padding(top = YY.s(10)).fillMaxWidth().background(YY.c.card, RoundedCornerShape(YY.s(16))).padding(YY.s(14)),
        )
        Spacer(Modifier.height(20.dp))
    }
}

private val PRIVACY: String
    get() = t("语阅没有账号、没有广告，也不做跨应用追踪。你的书、进度、笔记、划线和搜索词都只留在本地设备。") + "\n\n" +
        t("匿名使用统计：默认开启，仅记录功能使用形态（如导入格式、界面浏览、朗读与音频功能使用），不关联个人身份，绝不收集书名、作者、文件名、笔记、划线、搜索词、网址或邮箱；随时可以在「我的 › 关于」关闭。") + "\n\n" +
        t("书、进度、笔记和生词都存在这台设备上。打开同步时，它们只在你的设备和你自己选的网盘（WebDAV，Google Drive 即将支持）之间传，语阅看不到。") + "\n\n" +
        t("免费在线语音会把当前要读的句子发给在线语音服务；书伴、自行配置的云端声音和笔记导出只在你选用并填了自己的 Key 时联网，提问、要读的句子或笔记直接发给你选的服务商；Key 只存在这台设备上，加密保存。") + "\n\n" +
        t("本地模型与声音包：对齐模型、离线语音模型、开源字体和内置声音包会在你需要时从 GitHub Releases、魔搭（ModelScope）或 Hugging Face 下载，运行全在这台设备上完成，原文与录音不出本机。") + "\n\n" +
        t("问题反馈：只有在你主动提交反馈时才会联网，正文和截图仅发给开发者，不会公开，也不收集邮箱。") + "\n\n" +
        t("检查更新：从 GitHub 下载安装的版本每天从 GitHub 取一个小文件，看有没有新版本，不带任何关于你和设备的信息；在我的 › 关于 › 检查更新里能关掉。App Store 和 Google Play 的版本不查，由商店更新。")

private val LICENCES: String
    get() = t("语阅用到了这些开源项目，感谢它们的作者：") + "\n\n" +
        "UniFFI (MPL-2.0) · rusqlite / SQLite (MIT / Public Domain) · serde, serde_json, regex, encoding_rs, chardetng, zip, html5ever, roxmltree, ICU4X, blake3, percent-encoding, thiserror, aho-corasick, base64, flate2, getrandom, lewton, markup5ever_rcdom, ripemd (MIT / Apache-2.0) · rars, rust-lzo1x (MIT) · foliate-js (MIT) · zhconv (GPL-2.0-or-later) · OpenCC dictionaries (Apache-2.0) · ECDICT (MIT) · pinyin, pypinyin (MIT) · ONNX Runtime (MIT) · OkHttp, JNA (Apache-2.0) · AndroidX, Jetpack Compose, Media3, kotlinx.coroutines, Haze (Apache-2.0) · Sparkle (MIT) · PdfPig (Apache-2.0) · Simple Icons (CC0 1.0) · Kokoro-82M-v1.1-zh (Apache-2.0) · ZipVoice weights (CC-BY-NC 4.0) · Vocos (MIT) · voices-1, sounds-1 (CC0 1.0) · Meta MMS (CC-BY-NC 4.0) · 3D-Speaker CAM++ (Apache-2.0) · SenseVoice (FunASR Model License)" + "\n\n" +
        t("完整的许可文本随源代码一起发布。")
