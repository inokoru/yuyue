package app.yuyue.podcast

import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.companion.CompanionConfig
import app.yuyue.companion.CompanionSettingsSheet
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.design.pressable
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.shelf.BookCover
import app.yuyue.speech.Listener
import app.yuyue.speech.LiveVoiceModel
import app.yuyue.t
import kotlinx.coroutines.launch
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.PodcastClip
import uniffi.yuyue_core.PodcastIssue
import uniffi.yuyue_core.PodcastPlacement
import uniffi.yuyue_core.PodcastTrack
import uniffi.yuyue_core.ReadingStatus
import uniffi.yuyue_core.StudioKind
import kotlin.math.max
import kotlin.math.roundToInt

enum class PodcastDrawerTab { Clips, Queue, Voices }

// MARK: - Stationery Building Blocks

@Composable
fun PodcastStationeryCard(
    modifier: Modifier = Modifier,
    cornerRadius: Dp = 15.dp,
    content: @Composable ColumnScope.() -> Unit,
) {
    val eink = if (Prefs.isReady) rememberPref("eink.on", false).value else false
    val shape = RoundedCornerShape(cornerRadius)
    Column(
        modifier = modifier
            .fillMaxWidth()
            .shadow(
                if (YY.dark || eink) 0.dp else 2.5.dp,
                shape,
                ambientColor = Color(0x14332E29),
                spotColor = Color(0x14332E29),
            )
            .clip(shape)
            .background(YY.c.card)
            .border(0.5.dp, if (YY.dark) Color.White.copy(alpha = 0.08f) else YY.c.line, shape),
        content = content,
    )
}

@Composable
fun PodcastPaperPill(
    title: String,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    accent: Boolean = false,
    enabled: Boolean = true,
    height: Dp = 26.dp,
    horizontalPadding: Dp = 11.dp,
    onClick: () -> Unit,
) {
    val tint = YY.tint
    val bg = if (accent) tint.accentSoft else YY.c.card
    val border = if (accent) tint.accent.copy(alpha = 0.35f) else YY.c.pillLine
    val fg = if (accent) tint.accent else YY.c.t1
    Row(
        modifier = modifier
            .heightIn(min = height)
            .clip(CircleShape)
            .background(bg.copy(alpha = if (enabled) 1f else 0.5f))
            .border(0.5.dp, border, CircleShape)
            .pressable(label = title, enabled = enabled, scale = 0.96f, onClick = onClick)
            .padding(horizontal = horizontalPadding, vertical = 3.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        if (icon != null) {
            Icon(icon, null, tint = fg.copy(alpha = if (enabled) 1f else 0.45f), modifier = Modifier.size(13.dp))
        }
        Text(
            title,
            fontSize = 11.5.sp,
            fontWeight = if (accent) FontWeight.SemiBold else FontWeight.Medium,
            color = fg.copy(alpha = if (enabled) 1f else 0.45f),
            maxLines = 1,
        )
    }
}

@Composable
fun PodcastCircleIconButton(
    icon: ImageVector,
    label: String,
    size: Dp = 38.dp,
    iconSize: Dp = 17.dp,
    accent: Boolean = false,
    showDot: Boolean = false,
    onClick: () -> Unit,
) {
    val tint = YY.tint
    val bg = if (accent) tint.accentSoft else YY.c.card
    val ring = if (accent) tint.accent.copy(alpha = 0.35f) else YY.c.pillLine
    val fg = if (accent) tint.accent else YY.c.t1
    Box(
        modifier = Modifier
            .defaultMinSize(minWidth = 44.dp, minHeight = 44.dp)
            .clip(CircleShape)
            .pressable(label = label, scale = 0.93f, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            modifier = Modifier
                .size(size)
                .clip(CircleShape)
                .background(bg)
                .border(0.5.dp, ring, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, label, tint = fg, modifier = Modifier.size(iconSize))
            if (showDot) {
                Box(
                    Modifier
                        .align(Alignment.TopEnd)
                        .padding(top = 6.dp, end = 6.dp)
                        .size(6.dp)
                        .clip(CircleShape)
                        .background(tint.accent)
                )
            }
        }
    }
}

@Composable
fun PodcastTrackSegmentedControl(
    selection: PodcastTrack,
    language: String = "en",
    onSelect: (PodcastTrack) -> Unit,
) {
    val options = listOf(
        PodcastTrack.ORIGINAL to PodcastFormat.trackLabel(PodcastTrack.ORIGINAL, language),
        PodcastTrack.DUB to PodcastFormat.trackLabel(PodcastTrack.DUB, language),
        PodcastTrack.COACH to PodcastFormat.trackLabel(PodcastTrack.COACH, language),
    )
    Row(
        modifier = Modifier
            .clip(CircleShape)
            .background(YY.c.inset)
            .border(0.5.dp, YY.c.line, CircleShape)
            .padding(2.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        for ((track, label) in options) {
            val on = selection == track
            Box(
                modifier = Modifier
                    .height(22.dp)
                    .clip(CircleShape)
                    .background(if (on) YY.c.card else Color.Transparent)
                    .then(if (on) Modifier.border(0.5.dp, YY.c.line, CircleShape) else Modifier)
                    .clickable { onSelect(track) }
                    .padding(horizontal = 9.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    label,
                    fontSize = 11.sp,
                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium,
                    color = if (on) YY.tint.accent else YY.c.t3,
                    maxLines = 1,
                )
            }
        }
    }
}

@Composable
fun PodcastEqualizerBars(active: Boolean, color: Color = YY.tint.accent) {
    if (!active) {
        Icon(Icons.waveform, null, tint = color, modifier = Modifier.size(14.dp))
        return
    }
    if (YYMotion.isReduceMotion()) {
        Row(
            modifier = Modifier.size(14.dp),
            horizontalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.Bottom,
        ) {
            for (h in listOf(0.45f, 0.9f, 0.6f)) {
                Box(
                    Modifier
                        .width(2.3.dp)
                        .height((13f * h).dp)
                        .clip(CircleShape)
                        .background(color)
                )
            }
        }
        return
    }
    val infinite = rememberInfiniteTransition(label = "podEq")
    val b1 by infinite.animateFloat(0.32f, 0.95f, infiniteRepeatable(tween(420, easing = YYMotion.EaseOut), RepeatMode.Reverse), label = "b1")
    val b2 by infinite.animateFloat(0.85f, 0.28f, infiniteRepeatable(tween(340, easing = YYMotion.EaseOut), RepeatMode.Reverse), label = "b2")
    val b3 by infinite.animateFloat(0.45f, 1.0f, infiniteRepeatable(tween(510, easing = YYMotion.EaseOut), RepeatMode.Reverse), label = "b3")
    Row(
        modifier = Modifier.size(14.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        for (h in listOf(b1, b2, b3)) {
            Box(
                Modifier
                    .width(2.3.dp)
                    .height(13.dp)
                    .graphicsLayer {
                        scaleY = h
                        transformOrigin = TransformOrigin(0.5f, 1f)
                    }
                    .clip(CircleShape)
                    .background(color)
            )
        }
    }
}

// MARK: - Podcast Shelf Home ("播客" Place inside ShelfScreen)

private sealed interface ShowFilter {
    data object All : ShowFilter
    data object Studio : ShowFilter
    data class Show(val key: String) : ShowFilter
}

private data class ExternalShowItem(val key: String, val title: String, val artworkURL: String?)

@Composable
fun PodcastShelfContent(
    library: Library,
    onOpenIssue: (String) -> Unit,
) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint

    var showFilter by remember { mutableStateOf<ShowFilter>(ShowFilter.All) }
    var searchQuery by remember { mutableStateOf("") }
    var adding by remember { mutableStateOf(false) }
    var addInitialQuery by remember { mutableStateOf("") }
    var studioKind by remember { mutableStateOf<StudioKind?>(null) }
    var drawerTab by remember { mutableStateOf<PodcastDrawerTab?>(null) }
    var hasNewClipDot by remember { mutableStateOf(false) }
    var plusMenu by remember { mutableStateOf(false) }

    LaunchedEffect(Unit) {
        hub.loadDiscoveryIfNeeded()
    }

    val looksLikeURL = remember(searchQuery) {
        val q = searchQuery.trim().lowercase()
        q.startsWith("http://") || q.startsWith("https://")
            || q.contains("xiaoyuzhou") || q.contains("podcasts.apple.com")
            || q.contains(".xml") || q.contains("/feed")
    }

    val externalShows = remember(hub.channels, hub.deskIssues, hub.newsstandIssues, hub.archiveIssues) {
        val out = mutableListOf<ExternalShowItem>()
        val seen = HashSet<String>()
        for (ch in hub.channels) {
            val k = ch.title.ifBlank { ch.id }
            if (seen.add(k)) out += ExternalShowItem(k, k, ch.artworkUrl)
        }
        for (ep in hub.allIssues) {
            if (ep.studioKind != null) continue
            val k = ep.showTitle.ifBlank { t("单集导入") }
            if (seen.add(k)) out += ExternalShowItem(k, k, ep.artworkUrl)
        }
        out
    }

    fun submitSearch() {
        val q = searchQuery.trim()
        if (q.isEmpty()) return
        if (looksLikeURL) {
            scope.launch {
                if (hub.subscribeRSS(q, putLatestOnDesk = true)) {
                    searchQuery = ""
                }
            }
        } else {
            addInitialQuery = q
            adding = true
        }
    }

    Column(
        modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
        verticalArrangement = Arrangement.spacedBy(13.dp),
    ) {
        // 1. Top Search / Paste Command Bar + Clips & Plus Buttons
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(9.dp),
        ) {
            Row(
                modifier = Modifier
                    .weight(1f)
                    .heightIn(min = 42.dp)
                    .clip(RoundedCornerShape(11.dp))
                    .background(YY.c.inset)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(11.dp))
                    .padding(start = 12.dp, end = if (searchQuery.isNotEmpty()) 0.dp else 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    if (looksLikeURL) Icons.link else Icons.search,
                    contentDescription = null,
                    tint = YY.c.t3,
                    modifier = Modifier.size(15.dp),
                )
                Spacer(Modifier.width(8.dp))
                BasicTextField(
                    value = searchQuery,
                    onValueChange = { searchQuery = it },
                    singleLine = true,
                    textStyle = TextStyle(fontSize = 13.sp, color = YY.c.t1),
                    cursorBrush = SolidColor(tint.accent),
                    keyboardOptions = KeyboardOptions(imeAction = if (looksLikeURL) ImeAction.Go else ImeAction.Search),
                    keyboardActions = KeyboardActions(onAny = { submitSearch() }),
                    modifier = Modifier.weight(1f).padding(vertical = 6.dp),
                    decorationBox = { inner ->
                        if (searchQuery.isEmpty()) {
                            Text(
                                t("搜节目、听过的句子，或粘贴链接…"),
                                fontSize = 13.sp,
                                color = YY.c.t3,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        inner()
                    },
                )
                if (searchQuery.isNotEmpty()) {
                    Box(
                        Modifier
                            .defaultMinSize(minWidth = 44.dp, minHeight = 44.dp)
                            .clip(CircleShape)
                            .pressable(label = t("清空"), scale = 0.9f) { searchQuery = "" },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.xmarkCircleFill, t("清空"), tint = YY.c.t3, modifier = Modifier.size(15.dp))
                    }
                }
            }

            PodcastCircleIconButton(
                icon = Icons.bookmarkOutline,
                label = t("原声书摘与声音卡"),
                showDot = hasNewClipDot,
            ) {
                hasNewClipDot = false
                drawerTab = PodcastDrawerTab.Clips
            }

            Box {
                PodcastCircleIconButton(
                    icon = Icons.add,
                    label = t("新建或添加播客"),
                    accent = true,
                ) {
                    plusMenu = true
                }
                DropdownMenu(plusMenu, { plusMenu = false }, containerColor = YY.c.card, shape = RoundedCornerShape(14.dp)) {
                    DropdownMenuItem(
                        text = { Text(t("酿一期书房电台"), color = YY.c.t1) },
                        leadingIcon = { Icon(Icons.sparkles, null, tint = tint.accent, modifier = Modifier.size(18.dp)) },
                        onClick = { plusMenu = false; studioKind = StudioKind.WEEKLY_DIGEST },
                    )
                    DropdownMenuItem(
                        text = { Text(t("订阅播客或粘贴单集链接"), color = YY.c.t1) },
                        leadingIcon = { Icon(Icons.link, null, tint = YY.c.t2, modifier = Modifier.size(18.dp)) },
                        onClick = { plusMenu = false; addInitialQuery = ""; adding = true },
                    )
                    DropdownMenuItem(
                        text = { Text(t("声音卡与本机状态"), color = YY.c.t1) },
                        leadingIcon = { Icon(Icons.waveform, null, tint = YY.c.t2, modifier = Modifier.size(18.dp)) },
                        onClick = { plusMenu = false; drawerTab = PodcastDrawerTab.Voices },
                    )
                    if (hub.channels.isNotEmpty()) {
                        HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
                        DropdownMenuItem(
                            text = { Text(t("刷新订阅节目"), color = YY.c.t1) },
                            leadingIcon = { Icon(Icons.clockwise, null, tint = YY.c.t2, modifier = Modifier.size(18.dp)) },
                            onClick = {
                                plusMenu = false
                                scope.launch { hub.refreshAllChannels() }
                            },
                        )
                    }
                }
            }
        }

        // Inline transient notice if any
        hub.lastNotice?.let { note ->
            LaunchedEffect(note) {
                kotlinx.coroutines.delay(3200)
                if (hub.lastNotice == note) hub.lastNotice = null
            }
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .background(tint.accentSoft)
                    .padding(horizontal = 12.dp, vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(note, fontSize = 12.sp, color = tint.accent, modifier = Modifier.weight(1f))
            }
        }

        // 2. Horizontal Show Filter Rail
        Row(
            modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
            horizontalArrangement = Arrangement.spacedBy(7.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            FilterRailPill(t("全部"), showFilter == ShowFilter.All, null, false, false) {
                showFilter = ShowFilter.All
            }
            FilterRailPill(t("书房电台"), showFilter == ShowFilter.Studio, null, true, true) {
                showFilter = if (showFilter == ShowFilter.Studio) ShowFilter.All else ShowFilter.Studio
            }
            for (item in externalShows.take(8)) {
                val target = ShowFilter.Show(item.key)
                FilterRailPill(item.title, showFilter == target, item.artworkURL, false, true) {
                    showFilter = if (showFilter == target) ShowFilter.All else target
                }
            }
        }

        // 3. Active or Ready 3-Zone Tabletop Listening Card
        val active = hub.activeIssue
        if (active != null) {
            ActiveTabletopCard(
                library = library,
                issue = active,
                onOpen = { onOpenIssue(active.id) },
                onClipMarked = { hasNewClipDot = true },
            )
        } else {
            ReadyTabletopCard(library = library) { kind ->
                studioKind = kind
            }
        }

        // 4. Grouped Stationery List ("收听台")
        val q = searchQuery.trim()
        val matches: (PodcastIssue) -> Boolean = { ep ->
            q.isEmpty() || ep.title.contains(q, ignoreCase = true) || ep.showTitle.contains(q, ignoreCase = true)
        }
        val studioList = hub.studioIssues.filter(matches)
        val showsToRender = when (val f = showFilter) {
            ShowFilter.All -> externalShows
            ShowFilter.Studio -> emptyList()
            is ShowFilter.Show -> externalShows.filter { it.key == f.key }
        }

        Row(
            modifier = Modifier.fillMaxWidth().padding(horizontal = 2.dp, vertical = 2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(t("收听台"), fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 15.sp, color = YY.c.t1)
            Spacer(Modifier.weight(1f))
            Text(t("%lld 期", hub.allIssues.size), fontSize = 11.5.sp, color = YY.c.t3, style = YY.tnum)
        }

        if (showFilter == ShowFilter.All || showFilter == ShowFilter.Studio) {
            PodcastStationeryCard(cornerRadius = 14.dp) {
                Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 4.dp)) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        PodcastShowMark(title = t("书房电台"), isStudio = true, size = 20.dp)
                        Text(t("书房电台"), fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 14.5.sp, color = YY.c.t1)
                        Text(
                            if (studioList.isEmpty()) t("防剧透") else t("%lld 期", studioList.size),
                            fontSize = 11.sp,
                            color = YY.c.t3,
                            style = YY.tnum,
                        )
                        Spacer(Modifier.weight(1f))
                        PodcastPaperPill(t("+ 酿一期"), accent = true, height = 24.dp, horizontalPadding = 10.dp) {
                            studioKind = StudioKind.WEEKLY_DIGEST
                        }
                    }

                    if (studioList.isEmpty()) {
                        StudioStarterRows(library) { picked -> studioKind = picked }
                    } else {
                        studioList.forEachIndexed { idx, ep ->
                            if (idx > 0) HorizontalDivider(color = YY.c.line.copy(alpha = 0.65f), thickness = 0.5.dp)
                            EpisodeRow(library, ep, onOpen = { onOpenIssue(ep.id) })
                        }
                    }
                }
            }
        }

        for (show in showsToRender) {
            val eps = hub.allIssues.filter {
                it.studioKind == null && (it.showTitle.ifBlank { t("单集导入") }) == show.key && matches(it)
            }
            if (eps.isNotEmpty()) {
                PodcastStationeryCard(cornerRadius = 14.dp) {
                    Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 4.dp)) {
                        Row(
                            modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            PodcastShowMark(title = show.title, artworkURL = show.artworkURL, size = 20.dp)
                            Text(
                                show.title,
                                fontFamily = YY.serif,
                                fontWeight = FontWeight.Medium,
                                fontSize = 14.5.sp,
                                color = YY.c.t1,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f, fill = false),
                            )
                            Text(t("%lld 期", eps.size), fontSize = 11.sp, color = YY.c.t3, style = YY.tnum)
                            Spacer(Modifier.weight(1f))
                        }
                        eps.take(5).forEachIndexed { idx, ep ->
                            if (idx > 0) HorizontalDivider(color = YY.c.line.copy(alpha = 0.65f), thickness = 0.5.dp)
                            EpisodeRow(library, ep, onOpen = { onOpenIssue(ep.id) })
                        }
                    }
                }
            }
        }

        if (showFilter == ShowFilter.All && externalShows.isEmpty() && hub.discoveredShows.isNotEmpty()) {
            DiscoverCard(library, onMore = { addInitialQuery = ""; adding = true })
        }
    }

    if (adding) {
        PodcastAddSheet(
            library = library,
            initialQuery = addInitialQuery,
            onDismiss = { adding = false; addInitialQuery = "" },
        )
    }

    studioKind?.let { initial ->
        PodcastStudioSheet(
            library = library,
            initialKind = initial,
            onCreated = { created ->
                studioKind = null
                onOpenIssue(created.id)
            },
            onDismiss = { studioKind = null },
        )
    }

    drawerTab?.let { initialTab ->
        PodcastDrawerSheet(
            library = library,
            initialTab = initialTab,
            onOpenIssue = { id ->
                drawerTab = null
                onOpenIssue(id)
            },
            onDismiss = { drawerTab = null },
        )
    }
}

@Composable
private fun FilterRailPill(
    title: String,
    selected: Boolean,
    artworkURL: String?,
    isStudio: Boolean,
    showMark: Boolean,
    onClick: () -> Unit,
) {
    val tint = YY.tint
    val bg = if (selected) tint.accentSoft else YY.c.card
    val border = if (selected) tint.accent.copy(alpha = 0.35f) else YY.c.pillLine
    Row(
        modifier = Modifier
            .height(32.dp)
            .clip(CircleShape)
            .background(bg)
            .border(0.5.dp, border, CircleShape)
            .pressable(label = title, scale = 0.97f, onClick = onClick)
            .padding(start = if (showMark) 6.dp else 12.dp, end = 11.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        if (showMark) {
            PodcastShowMark(title = title, artworkURL = artworkURL, isStudio = isStudio, size = 19.dp)
        }
        Text(
            title,
            fontSize = 12.sp,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
            color = if (selected) tint.accent else YY.c.t2,
            maxLines = 1,
        )
    }
}

@Composable
private fun ActiveTabletopCard(
    library: Library,
    issue: PodcastIssue,
    onOpen: () -> Unit,
    onClipMarked: () -> Unit,
) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint
    val isCurrent = hub.activeIssueID == issue.id
    val pos = if (isCurrent) hub.currentTime else issue.positionSec
    val dur = if (isCurrent && hub.duration > 0) hub.duration else issue.durationSec.toDouble()
    val frac = if (dur > 0) (pos / dur).toFloat().coerceIn(0.03f, 1f) else 0f
    val sentence = hub.activeSentence(issue.id)
    val excerpt = when {
        sentence != null -> PodcastFormat.sentenceText(sentence, issue.track)
        issue.summary.isNotBlank() -> issue.summary
        else -> t("轻点进入访谈书页，查看逐字稿与原声摘录。")
    }
    val supportsTrack = hub.supportsTrackSwitch(issue)
    val leafBg = if (YY.dark) Color.White.copy(alpha = 0.035f) else Color(0xFFFAF8F4)

    PodcastStationeryCard(cornerRadius = 15.dp) {
        Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 13.dp, bottom = 11.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                PodcastShowMark(
                    title = if (issue.studioKind != null) t("书房电台") else issue.showTitle,
                    artworkURL = issue.artworkUrl,
                    isStudio = issue.studioKind != null,
                    size = 34.dp,
                )
                Column(
                    modifier = Modifier.weight(1f).clickable(onClick = onOpen),
                    verticalArrangement = Arrangement.spacedBy(1.5.dp),
                ) {
                    Text(
                        issue.title,
                        fontSize = 13.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = YY.c.t1,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    val rem = PodcastFormat.remaining(pos, dur)
                    Text(
                        listOf(issue.showTitle, rem).filter { it.isNotEmpty() }.joinToString(" · "),
                        fontSize = 11.5.sp,
                        color = YY.c.t3,
                        style = YY.tnum,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                }
                Box(
                    modifier = Modifier
                        .size(32.dp)
                        .clip(CircleShape)
                        .background(YY.c.t1)
                        .pressable(label = if (isCurrent && hub.isPlaying) t("暂停") else t("播放"), scale = 0.93f) {
                            hub.selectAndPlay(issue)
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        if (isCurrent && hub.isPlaying) Icons.pause else Icons.play,
                        contentDescription = null,
                        tint = YY.c.card,
                        modifier = Modifier.size(16.dp),
                    )
                }
            }

            Box(
                modifier = Modifier
                    .padding(top = 9.dp)
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(9.dp))
                    .background(leafBg)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(9.dp))
                    .clickable(onClick = onOpen)
                    .padding(horizontal = 11.dp, vertical = 9.dp),
            ) {
                Text(
                    excerpt,
                    fontFamily = YY.serif,
                    fontSize = 13.sp,
                    lineHeight = 19.sp,
                    color = YY.c.t1,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            val trackBg = YY.c.inset
            val fillBg = YY.c.t1
            Box(
                modifier = Modifier
                    .padding(top = 10.dp)
                    .fillMaxWidth()
                    .height(2.dp)
                    .drawBehind {
                        val r = CornerRadius(size.height / 2f)
                        drawRoundRect(trackBg, cornerRadius = r)
                        if (frac > 0f) drawRoundRect(fillBg, size = Size(max(6f, size.width * frac), size.height), cornerRadius = r)
                    }
            )
        }

        HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(leafBg)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            when {
                supportsTrack -> PodcastTrackSegmentedControl(issue.track, issue.language) { next ->
                    hub.setTrack(next, issue)
                }
                issue.studioKind != null -> Text(
                    "${issue.studioKind!!.title} · ${t("不剧透锁")}",
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = tint.accent,
                )
                else -> Text(
                    t("原声 · 书摘与插话就绪"),
                    fontSize = 11.sp,
                    fontWeight = FontWeight.Medium,
                    color = YY.c.t3,
                )
            }
            Spacer(Modifier.weight(1f))
            PodcastPaperPill(t("划线"), icon = Icons.bookmarkOutline, height = 24.dp, horizontalPadding = 10.dp) {
                scope.launch {
                    if (hub.markCurrentMoment() != null) onClipMarked()
                }
            }
        }
    }
}

@Composable
private fun ReadyTabletopCard(
    library: Library,
    onStartBrew: (StudioKind) -> Unit,
) {
    val tint = YY.tint
    val readingBook = library.keptBooks.firstOrNull { it.status == ReadingStatus.READING } ?: library.keptBooks.firstOrNull()
    val title = if (readingBook != null) t("《%@》· 导读待酿制", readingBook.title) else t("书房电台 · 准备就绪")
    val meta = if (readingBook != null) t("书房电台 · 仅依已读进度 · 防剧透") else t("书房电台 · 本机整理")
    val preview = if (readingBook != null) {
        t("轻点右侧播放键或「酿一期」，把《%@》酿成一期 8 分钟读前导读，绝不剧透后文。", readingBook.title)
    } else {
        t("在上方搜公开播客、粘贴单集链接直听，或点「酿一期」把你的书架酿成私人电台。")
    }
    val targetKind = if (readingBook != null) StudioKind.BOOK_GUIDE else StudioKind.WEEKLY_DIGEST
    val leafBg = if (YY.dark) Color.White.copy(alpha = 0.035f) else Color(0xFFFAF8F4)

    PodcastStationeryCard(cornerRadius = 15.dp) {
        Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 13.dp, bottom = 11.dp)) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                PodcastShowMark(title = t("书房电台"), isStudio = true, size = 34.dp)
                Column(
                    modifier = Modifier.weight(1f).clickable { onStartBrew(targetKind) },
                    verticalArrangement = Arrangement.spacedBy(1.5.dp),
                ) {
                    Text(title, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(meta, fontSize = 11.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Box(
                    modifier = Modifier
                        .size(32.dp)
                        .clip(CircleShape)
                        .background(YY.c.t1)
                        .pressable(label = t("酿一期"), scale = 0.93f) { onStartBrew(targetKind) },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.play, null, tint = YY.c.card, modifier = Modifier.size(16.dp))
                }
            }

            Box(
                modifier = Modifier
                    .padding(top = 9.dp)
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(9.dp))
                    .background(leafBg)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(9.dp))
                    .clickable { onStartBrew(targetKind) }
                    .padding(horizontal = 11.dp, vertical = 9.dp),
            ) {
                Text(preview, fontFamily = YY.serif, fontSize = 13.sp, lineHeight = 19.sp, color = YY.c.t1, maxLines = 2, overflow = TextOverflow.Ellipsis)
            }

            Box(
                modifier = Modifier
                    .padding(top = 10.dp)
                    .fillMaxWidth()
                    .height(2.dp)
                    .clip(CircleShape)
                    .background(YY.c.inset),
            ) {
                Box(Modifier.width(24.dp).fillMaxHeight().clip(CircleShape).background(YY.c.t1.copy(alpha = 0.25f)))
            }
        }

        HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .background(leafBg)
                .padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(t("防剧透锁 · 仅基于已读进度"), fontSize = 11.sp, fontWeight = FontWeight.Medium, color = tint.accent)
            Spacer(Modifier.weight(1f))
            PodcastPaperPill(t("酿一期"), accent = true, height = 24.dp, horizontalPadding = 10.dp) {
                onStartBrew(targetKind)
            }
        }
    }
}

@Composable
private fun StudioStarterRows(library: Library, onPick: (StudioKind) -> Unit) {
    val tint = YY.tint
    val readingBook = library.keptBooks.firstOrNull { it.status == ReadingStatus.READING } ?: library.keptBooks.firstOrNull()
    val starters = listOf(
        Triple(
            StudioKind.WEEKLY_DIGEST,
            if (readingBook != null) t("本周书房周刊：重读《%@》与夜间划线", readingBook.title)
            else t("本周书房周刊：串联本周阅读与划线"),
            t("不剧透锁") to t("%lld 分", 10),
        ),
        Triple(
            StudioKind.ROUNDTABLE,
            t("跨书圆桌：挑两三本在读书同桌对谈"),
            t("跨书对谈") to t("15 分 · 防剧透"),
        ),
        Triple(
            StudioKind.BOOK_GUIDE,
            t("难书与论文导读：晦涩章节直觉精讲"),
            t("读前热身") to t("%lld 分", 8),
        ),
        Triple(
            StudioKind.VOCAB_RADIO,
            t("生词伴听 · 通勤外语台（融入本周生词）"),
            t("生词本联动") to t("12 分 · 0.9×"),
        ),
    )

    starters.forEachIndexed { idx, (kind, label, meta) ->
        if (idx > 0) HorizontalDivider(color = YY.c.line.copy(alpha = 0.65f), thickness = 0.5.dp)
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable { onPick(kind) }
                .padding(vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(11.dp),
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(label, fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(meta.first, fontSize = 11.5.sp, color = tint.accent)
                    Text("·", fontSize = 11.5.sp, color = YY.c.t3)
                    Text(meta.second, fontSize = 11.5.sp, color = YY.c.t3, style = YY.tnum)
                }
            }
            Box(
                modifier = Modifier
                    .size(29.dp)
                    .clip(CircleShape)
                    .background(YY.c.card)
                    .border(0.5.dp, YY.c.pillLine, CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.play, null, tint = YY.c.t2, modifier = Modifier.size(14.dp))
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun EpisodeRow(
    library: Library,
    item: PodcastIssue,
    onOpen: () -> Unit,
) {
    val hub = library.podcastHub
    val tint = YY.tint
    val isCur = hub.activeIssueID == item.id
    val isPlaying = isCur && hub.isPlaying
    var menu by remember { mutableStateOf(false) }

    val accentMeta = when {
        item.studioKind != null -> "${item.studioKind!!.title} · ${t("防剧透")}"
        item.language.lowercase().startsWith("en") -> PodcastFormat.trackLabel(item.track, item.language)
        else -> t("中文原声")
    }

    Box {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .combinedClickable(onClick = onOpen, onLongClick = { menu = true })
                .padding(vertical = 10.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(11.dp),
        ) {
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Text(
                    item.title,
                    fontSize = 13.5.sp,
                    fontWeight = if (isCur) FontWeight.SemiBold else FontWeight.Medium,
                    color = if (isCur) tint.accent else YY.c.t1,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Row(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(accentMeta, fontSize = 11.5.sp, color = tint.accent)
                    Text("·", fontSize = 11.5.sp, color = YY.c.t3)
                    Text(PodcastFormat.compactDuration(item.durationSec), fontSize = 11.5.sp, color = YY.c.t3, style = YY.tnum)
                }
            }

            Box(
                modifier = Modifier
                    .size(30.dp)
                    .clip(CircleShape)
                    .background(if (isCur) tint.accentSoft else YY.c.card)
                    .border(0.5.dp, if (isCur) tint.accent.copy(alpha = 0.35f) else YY.c.pillLine, CircleShape)
                    .pressable(label = if (isPlaying) t("暂停") else t("播放"), scale = 0.93f) {
                        hub.selectAndPlay(item)
                    },
                contentAlignment = Alignment.Center,
            ) {
                if (isPlaying) {
                    PodcastEqualizerBars(active = true, color = tint.accent)
                } else {
                    Icon(Icons.play, null, tint = if (isCur) tint.accent else YY.c.t2, modifier = Modifier.size(14.dp))
                }
            }
        }

        DropdownMenu(menu, { menu = false }, containerColor = YY.c.card, shape = RoundedCornerShape(14.dp)) {
            DropdownMenuItem(
                text = { Text(t("打开访谈书页"), color = YY.c.t1) },
                leadingIcon = { Icon(Icons.book, null, tint = YY.c.t2) },
                onClick = { menu = false; onOpen() },
            )
            if (item.placement != PodcastPlacement.DESK) {
                DropdownMenuItem(
                    text = { Text(t("放到收听台"), color = YY.c.t1) },
                    leadingIcon = { Icon(Icons.pin, null, tint = YY.c.t2) },
                    onClick = { menu = false; hub.setPlacement(item.id, PodcastPlacement.DESK) },
                )
            }
            if (item.placement != PodcastPlacement.NEWSSTAND) {
                DropdownMenuItem(
                    text = { Text(t("移到稍后听"), color = YY.c.t1) },
                    leadingIcon = { Icon(Icons.inbox, null, tint = YY.c.t2) },
                    onClick = { menu = false; hub.setPlacement(item.id, PodcastPlacement.NEWSSTAND) },
                )
            }
            if (item.placement != PodcastPlacement.ARCHIVE) {
                DropdownMenuItem(
                    text = { Text(t("归档"), color = YY.c.t1) },
                    leadingIcon = { Icon(Icons.check, null, tint = YY.c.t2) },
                    onClick = { menu = false; hub.setPlacement(item.id, PodcastPlacement.ARCHIVE) },
                )
            }
            HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
            DropdownMenuItem(
                text = { Text(t("删除"), color = YY.c.danger) },
                leadingIcon = { Icon(Icons.delete, null, tint = YY.c.danger) },
                onClick = { menu = false; hub.deleteIssue(item.id) },
            )
        }
    }
}

@Composable
private fun DiscoverCard(library: Library, onMore: () -> Unit) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val subscribedFeeds = remember(hub.channels) { hub.channels.map { it.feedUrl }.toSet() }

    PodcastStationeryCard(cornerRadius = 14.dp) {
        Column(Modifier.padding(start = 14.dp, end = 14.dp, top = 12.dp, bottom = 6.dp)) {
            Row(
                modifier = Modifier.fillMaxWidth().padding(bottom = 6.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(t("精选播客"), fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = 14.5.sp, color = YY.c.t1)
                Spacer(Modifier.weight(1f))
                Text(
                    t("搜索更多 ›"),
                    fontSize = 11.5.sp,
                    fontWeight = FontWeight.Medium,
                    color = YY.c.t2,
                    modifier = Modifier.clickable(onClick = onMore),
                )
            }

            hub.discoveredShows.take(4).forEachIndexed { idx, hit ->
                if (idx > 0) HorizontalDivider(color = YY.c.line.copy(alpha = 0.65f), thickness = 0.5.dp)
                val isSub = hit.feedURL in subscribedFeeds
                val busy = hit.feedURL in hub.subscribingFeedURLs
                Row(
                    modifier = Modifier.fillMaxWidth().padding(vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    PodcastShowMark(title = hit.title, artworkURL = hit.artworkURL, size = 30.dp)
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(hit.title, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            listOfNotNull(hit.author.takeIf { it.isNotBlank() }, hit.genre?.takeIf { it.isNotBlank() }).joinToString(" · "),
                            fontSize = 11.sp,
                            color = YY.c.t3,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    if (isSub) {
                        Text(t("已订阅"), fontSize = 11.sp, color = YY.c.t3)
                    } else {
                        PodcastPaperPill(
                            title = if (busy) t("订阅中") else t("订阅"),
                            accent = true,
                            enabled = !busy,
                            height = 24.dp,
                            horizontalPadding = 10.dp,
                        ) {
                            scope.launch { hub.subscribeFeaturedShow(hit) }
                        }
                    }
                }
            }
        }
    }
}

// MARK: - Floating Mini Podcast Player Bar (Cross-Tab & Scrolled Shelf)

@Composable
fun PodcastMiniPlayerBar(
    library: Library,
    issue: PodcastIssue,
    modifier: Modifier = Modifier,
    onOpenReader: () -> Unit,
) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint
    var clipFlash by remember { mutableStateOf(false) }

    val isCurrent = hub.activeIssueID == issue.id
    val isPlaying = isCurrent && hub.isPlaying
    val pos = if (isCurrent) hub.currentTime else issue.positionSec
    val dur = if (isCurrent && hub.duration > 0) hub.duration else issue.durationSec.toDouble()
    val frac = if (dur > 0) (pos / dur).toFloat().coerceIn(0f, 1f) else 0f
    val shape = RoundedCornerShape(14.dp)

    Column(
        modifier = modifier
            .fillMaxWidth()
            .shadow(if (YY.dark) 0.dp else 6.dp, shape, ambientColor = Color(0x18000000), spotColor = Color(0x18000000))
            .clip(shape)
            .background(YY.c.card.copy(alpha = 0.95f))
            .border(0.5.dp, YY.c.line, shape),
    ) {
        val railTrack = YY.c.inset
        val railFill = tint.accent
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .height(2.5.dp)
                .drawBehind {
                    drawRect(railTrack)
                    if (frac > 0f) drawRect(railFill, size = Size(max(4f, size.width * frac), size.height))
                }
        )

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .heightIn(min = 50.dp)
                .padding(start = 10.dp, end = 6.dp, top = 3.dp, bottom = 3.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Row(
                modifier = Modifier.weight(1f).heightIn(min = 44.dp).clickable(onClick = onOpenReader),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(9.dp),
            ) {
                PodcastShowMark(
                    title = if (issue.studioKind != null) t("书房电台") else issue.showTitle,
                    artworkURL = issue.artworkUrl,
                    isStudio = issue.studioKind != null,
                    size = 28.dp,
                )
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Text(
                        issue.title,
                        fontSize = 12.5.sp,
                        fontWeight = FontWeight.Medium,
                        color = YY.c.t1,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        "${PodcastFormat.clock(pos)} / ${PodcastFormat.clock(dur)}",
                        fontSize = 10.5.sp,
                        color = YY.c.t3,
                        style = YY.tnum,
                        maxLines = 1,
                    )
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.defaultMinSize(minWidth = 38.dp, minHeight = 44.dp).clip(CircleShape).clickable { hub.skip(-15.0) },
                    contentAlignment = Alignment.Center,
                ) {
                    Text("-15s", fontSize = 10.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t2, style = YY.tnum, maxLines = 1)
                }
                Box(
                    modifier = Modifier
                        .defaultMinSize(minWidth = 38.dp, minHeight = 44.dp)
                        .clip(CircleShape)
                        .pressable(label = if (isPlaying) t("暂停") else t("接着听"), scale = 0.93f) {
                            hub.selectAndPlay(issue)
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Box(
                        modifier = Modifier
                            .size(30.dp)
                            .clip(CircleShape)
                            .background(YY.c.t1),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(
                            if (isPlaying) Icons.pause else Icons.play,
                            contentDescription = null,
                            tint = YY.c.card,
                            modifier = Modifier.size(15.dp),
                        )
                    }
                }
                Box(
                    Modifier.defaultMinSize(minWidth = 38.dp, minHeight = 44.dp).clip(CircleShape).clickable { hub.skip(15.0) },
                    contentAlignment = Alignment.Center,
                ) {
                    Text("+15s", fontSize = 10.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t2, style = YY.tnum, maxLines = 1)
                }
                Box(
                    modifier = Modifier
                        .defaultMinSize(minWidth = 38.dp, minHeight = 44.dp)
                        .clip(CircleShape)
                        .clickable {
                            scope.launch {
                                if (!isCurrent) {
                                    hub.start(issue, from = pos)
                                    hub.pause()
                                }
                                if (hub.markCurrentMoment() != null) {
                                    clipFlash = true
                                    kotlinx.coroutines.delay(1400)
                                    clipFlash = false
                                }
                            }
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        if (clipFlash) Icons.bookmark else Icons.bookmarkOutline,
                        contentDescription = t("原声划线"),
                        tint = if (clipFlash) tint.accent else YY.c.t2,
                        modifier = Modifier.size(15.dp),
                    )
                }
            }
        }
    }
}

// MARK: - Add Podcast / URL / OPML / Local Audio Sheet

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PodcastAddSheet(
    library: Library,
    initialQuery: String = "",
    onDismiss: () -> Unit,
) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint

    var query by remember { mutableStateOf(initialQuery) }
    var hits by remember { mutableStateOf<List<PodcastSearchHit>>(emptyList()) }
    var searching by remember { mutableStateOf(false) }
    var statusLine by remember { mutableStateOf<String?>(null) }
    val subscribedURLs = remember(hub.channels) { hub.channels.map { it.feedUrl }.toSet() }

    val looksLikeURL = remember(query) {
        val q = query.trim().lowercase()
        q.startsWith("http://") || q.startsWith("https://") || q.contains(".xml") || q.contains("/feed")
            || q.contains("xiaoyuzhou") || q.contains("podcasts.apple.com")
    }

    val opmlLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                val n = hub.importOPML(uri)
                statusLine = t("已导入 %lld 档播客到报刊栏", n)
            }
        }
    }

    val audioLauncher = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri: Uri? ->
        if (uri != null) {
            scope.launch {
                val ep = hub.importLocalAudio(uri)
                if (ep != null) statusLine = t("已将《%@》放上书桌", ep.title)
            }
        }
    }

    fun submit() {
        val q = query.trim()
        if (q.isEmpty()) return
        hub.addError = null
        if (looksLikeURL) {
            scope.launch {
                if (hub.subscribeRSS(q, putLatestOnDesk = true)) {
                    statusLine = t("已添加至收听台")
                    query = ""
                }
            }
        } else {
            searching = true
            scope.launch {
                hits = hub.searchDirectory(q)
                searching = false
            }
        }
    }

    LaunchedEffect(initialQuery) {
        if (initialQuery.isNotBlank()) submit()
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp)
                .padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(t("添加节目"), fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 19.sp, color = YY.c.t1)
                Text("  " + t("搜播客目录、贴单集或导入本地音频"), fontSize = 11.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                PodcastCircleIconButton(Icons.close, t("关闭"), size = 28.dp, iconSize = 14.dp, onClick = onDismiss)
            }

            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .heightIn(min = 44.dp)
                    .clip(RoundedCornerShape(11.dp))
                    .background(YY.c.inset)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(11.dp))
                    .padding(start = 12.dp, end = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(if (looksLikeURL) Icons.link else Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(15.dp))
                Spacer(Modifier.width(8.dp))
                BasicTextField(
                    value = query,
                    onValueChange = { query = it },
                    singleLine = true,
                    textStyle = TextStyle(fontSize = 13.5.sp, color = YY.c.t1),
                    cursorBrush = SolidColor(tint.accent),
                    keyboardOptions = KeyboardOptions(imeAction = if (looksLikeURL) ImeAction.Go else ImeAction.Search),
                    keyboardActions = KeyboardActions(onAny = { submit() }),
                    modifier = Modifier.weight(1f).padding(vertical = 6.dp),
                    decorationBox = { inner ->
                        if (query.isEmpty()) {
                            Text(t("搜节目名，或贴 RSS / 单集网页 / 音频链接…"), fontSize = 13.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        inner()
                    },
                )
                if (query.isNotEmpty()) {
                    Box(
                        Modifier
                            .defaultMinSize(minWidth = 44.dp, minHeight = 44.dp)
                            .clip(CircleShape)
                            .pressable(label = t("清空"), scale = 0.9f) { query = ""; hits = emptyList() },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.xmarkCircleFill, t("清空"), tint = YY.c.t3, modifier = Modifier.size(15.dp))
                    }
                } else {
                    Spacer(Modifier.width(8.dp))
                }
                PodcastPaperPill(
                    title = if (hub.isAddingFeed) t("解析中…") else if (looksLikeURL) t("放入收听台") else t("搜索"),
                    accent = true,
                    enabled = !hub.isAddingFeed && query.isNotBlank(),
                    height = 26.dp,
                    horizontalPadding = 10.dp,
                    onClick = ::submit,
                )
            }

            hub.addError?.let { err ->
                Text(err, fontSize = 12.sp, color = YY.c.danger)
            } ?: statusLine?.let { ok ->
                Text(ok, fontSize = 12.sp, color = tint.accent)
            }

            PodcastStationeryCard(cornerRadius = 14.dp) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 180.dp, max = 340.dp)
                        .verticalScroll(rememberScrollState()),
                ) {
                    when {
                        searching -> {
                            Row(
                                modifier = Modifier.fillMaxWidth().padding(vertical = 48.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.c.t3)
                                Text(t("正在搜索播客目录…"), fontSize = 12.5.sp, color = YY.c.t3)
                            }
                        }
                        hits.isEmpty() -> {
                            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
                                Text(t("三种添加方式"), fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 14.sp, color = YY.c.t1)
                                WayGuideRow(Icons.grid, t("搜节目名"), t("直连公开播客目录找到 RSS，回车即可搜索并一键订阅。"))
                                WayGuideRow(Icons.link, t("贴单集链接"), t("支持小宇宙、Apple Podcasts、网页或音频直链，不强迫订阅整档节目。"))
                                WayGuideRow(Icons.waveform, t("导入 OPML / 音频"), t("一键迁入现有订阅或本地 .mp3 / .m4a 讲座录音，零未读红点焦虑。"))
                            }
                        }
                        else -> {
                            hits.forEachIndexed { idx, hit ->
                                if (idx > 0) HorizontalDivider(color = YY.c.line, thickness = 0.5.dp, modifier = Modifier.padding(horizontal = 14.dp))
                                val isSub = hit.feedURL in subscribedURLs
                                val busy = hit.feedURL in hub.subscribingFeedURLs
                                Row(
                                    modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 10.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                                ) {
                                    PodcastShowMark(title = hit.title, artworkURL = hit.artworkURL, size = 34.dp)
                                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                        Text(hit.title, fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                        Text(
                                            listOfNotNull(hit.author.takeIf { it.isNotBlank() }, hit.genre?.takeIf { it.isNotBlank() }).joinToString(" · "),
                                            fontSize = 11.5.sp,
                                            color = YY.c.t3,
                                            maxLines = 1,
                                            overflow = TextOverflow.Ellipsis,
                                        )
                                    }
                                    if (isSub) {
                                        Text(t("已订阅"), fontSize = 11.5.sp, color = YY.c.t3)
                                    } else {
                                        PodcastPaperPill(
                                            title = if (busy) t("订阅中") else t("订阅"),
                                            accent = true,
                                            enabled = !busy,
                                            height = 26.dp,
                                        ) {
                                            scope.launch {
                                                if (hub.subscribeFeaturedShow(hit)) {
                                                    statusLine = t("已订阅《%@》", hit.title)
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PodcastPaperPill(t("导入 OPML"), icon = Icons.folder, height = 28.dp) {
                    opmlLauncher.launch(arrayOf("text/xml", "application/xml", "text/x-opml", "*/*"))
                }
                PodcastPaperPill(t("本地音频"), icon = Icons.headphones, height = 28.dp) {
                    audioLauncher.launch(arrayOf("audio/*"))
                }
                Spacer(Modifier.weight(1f))
                Text(t("粘贴链接回车即可收单集"), fontSize = 11.sp, color = YY.c.t3)
            }
        }
    }
}

@Composable
private fun WayGuideRow(icon: ImageVector, lead: String, detail: String) {
    Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.Top) {
        Box(
            Modifier.size(26.dp).clip(RoundedCornerShape(7.dp)).background(YY.tint.accentSoft),
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, null, tint = YY.tint.accent, modifier = Modifier.size(14.dp))
        }
        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(lead, fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
            Text(detail, fontSize = 11.5.sp, lineHeight = 16.sp, color = YY.c.t3)
        }
    }
}

// MARK: - Private Studio Composer Sheet ("酿一期书房电台")

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PodcastStudioSheet(
    library: Library,
    initialKind: StudioKind = StudioKind.BOOK_GUIDE,
    preselectedBookIDs: List<String> = emptyList(),
    onCreated: (PodcastIssue) -> Unit,
    onDismiss: () -> Unit,
) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint

    val allBooks = library.keptBooks
    var kind by remember { mutableStateOf(initialKind) }
    var selectedBookIDs by remember {
        mutableStateOf(
            preselectedBookIDs.ifEmpty {
                val maxPick = if (initialKind == StudioKind.ROUNDTABLE) minOf(2, allBooks.size) else minOf(1, allBooks.size)
                allBooks.take(maxPick).map { it.id }
            }
        )
    }
    var bookFilter by remember {
        mutableIntStateOf(if (allBooks.any { it.status == ReadingStatus.READING }) 0 else 2)
    }
    var bookQuery by remember { mutableStateOf("") }
    var topic by remember { mutableStateOf("") }
    var minutes by remember { mutableIntStateOf(if (initialKind == StudioKind.ROUNDTABLE) 15 else 10) }
    var noSpoiler by remember { mutableStateOf(true) }
    var voiceStyle by remember { mutableStateOf(hub.studioVoiceStyle) }
    var voiceMenu by remember { mutableStateOf(false) }
    var showCompanionSettings by remember { mutableStateOf(false) }
    var companionRefresh by remember { mutableIntStateOf(0) }

    LaunchedEffect(Unit) { hub.refreshVoiceOptions() }

    val companionCfg = remember(companionRefresh, showCompanionSettings) { CompanionConfig.current }
    val companionReady = companionCfg?.isReady == true

    val filteredBooks = remember(allBooks, bookFilter, bookQuery) {
        val q = bookQuery.trim().lowercase()
        if (q.isNotEmpty()) {
            allBooks.filter { b -> b.title.lowercase().contains(q) || b.authors.any { it.lowercase().contains(q) } }
        } else {
            when (bookFilter) {
                0 -> allBooks.filter { it.status == ReadingStatus.READING }.ifEmpty { allBooks }
                1 -> allBooks.filter { it.status == ReadingStatus.FINISHED }
                else -> allBooks
            }
        }
    }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 18.dp)
                .padding(bottom = 26.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            // Header
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(t("酿一期书房电台"), fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 19.sp, color = YY.c.t1)
                    Text(kind.subtitle, fontSize = 12.sp, color = YY.c.t3)
                }
                PodcastCircleIconButton(Icons.close, t("关闭"), size = 28.dp, iconSize = 14.dp, onClick = onDismiss)
            }

            // 4 Studio Kinds
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(7.dp),
            ) {
                for (item in listOf(StudioKind.WEEKLY_DIGEST, StudioKind.BOOK_GUIDE, StudioKind.ROUNDTABLE, StudioKind.VOCAB_RADIO)) {
                    val on = kind == item
                    PodcastPaperPill(
                        title = item.title,
                        accent = on,
                        height = 30.dp,
                        horizontalPadding = 12.dp,
                    ) {
                        kind = item
                        minutes = if (item == StudioKind.ROUNDTABLE) 15 else 10
                    }
                }
            }

            // Companion connection status / offline fallback note
            if (!companionReady) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(12.dp))
                        .background(tint.accentSoft)
                        .border(0.5.dp, tint.accent.copy(alpha = 0.3f), RoundedCornerShape(12.dp))
                        .padding(horizontal = 12.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(Icons.companion, null, tint = tint.accent, modifier = Modifier.size(16.dp))
                    Text(
                        t("尚未连接书伴模型，将使用本机离线摘录编排；接上模型可生成深度双人对谈。"),
                        fontSize = 11.5.sp,
                        lineHeight = 16.sp,
                        color = YY.c.t1,
                        modifier = Modifier.weight(1f),
                    )
                    PodcastPaperPill(t("接上书伴"), accent = true, height = 24.dp, horizontalPadding = 9.dp) {
                        showCompanionSettings = true
                    }
                }
            }

            // Book selection card
            if (kind != StudioKind.VOCAB_RADIO) {
                PodcastStationeryCard(cornerRadius = 14.dp) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(t("挑选入台书籍"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                            Spacer(Modifier.weight(1f))
                            // Spoiler lock segmented toggle
                            Row(
                                modifier = Modifier
                                    .clip(CircleShape)
                                    .background(YY.c.inset)
                                    .border(0.5.dp, YY.c.line, CircleShape)
                                    .padding(2.dp),
                            ) {
                                for ((flag, label) in listOf(true to t("仅已读"), false to t("全书"))) {
                                    val on = noSpoiler == flag
                                    Box(
                                        Modifier
                                            .height(22.dp)
                                            .clip(CircleShape)
                                            .background(if (on) YY.c.card else Color.Transparent)
                                            .clickable { noSpoiler = flag }
                                            .padding(horizontal = 9.dp),
                                        contentAlignment = Alignment.Center,
                                    ) {
                                        Text(label, fontSize = 11.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = if (on) tint.accent else YY.c.t3)
                                    }
                                }
                            }
                        }

                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            for ((idx, label) in listOf(0 to t("在读"), 1 to t("读过"), 2 to t("全部"))) {
                                val on = bookFilter == idx
                                Box(
                                    Modifier
                                        .height(25.dp)
                                        .clip(CircleShape)
                                        .background(if (on) YY.c.inset else Color.Transparent)
                                        .clickable { bookFilter = idx }
                                        .padding(horizontal = 9.dp),
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Text(label, fontSize = 11.5.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = if (on) YY.c.t1 else YY.c.t3)
                                }
                            }
                            Spacer(Modifier.weight(1f))
                            Row(
                                modifier = Modifier
                                    .width(136.dp)
                                    .height(28.dp)
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(YY.c.inset)
                                    .padding(horizontal = 8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(4.dp),
                            ) {
                                Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(13.dp))
                                BasicTextField(
                                    value = bookQuery,
                                    onValueChange = { bookQuery = it },
                                    singleLine = true,
                                    textStyle = TextStyle(fontSize = 12.sp, color = YY.c.t1),
                                    cursorBrush = SolidColor(tint.accent),
                                    modifier = Modifier.weight(1f),
                                    decorationBox = { inner ->
                                        if (bookQuery.isEmpty()) Text(t("搜书架…"), fontSize = 11.5.sp, color = YY.c.t3)
                                        inner()
                                    },
                                )
                            }
                        }

                        if (filteredBooks.isEmpty()) {
                            Text(t("这里还没有可选的书，先回书架导入一本即可。"), fontSize = 12.sp, color = YY.c.t3, modifier = Modifier.padding(vertical = 12.dp))
                        } else {
                            Column(
                                modifier = Modifier.heightIn(max = 196.dp).verticalScroll(rememberScrollState()),
                                verticalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                for (book in filteredBooks.take(24)) {
                                    val chosen = book.id in selectedBookIDs
                                    StudioBookPickRow(book, chosen) {
                                        selectedBookIDs = if (chosen) {
                                            selectedBookIDs - book.id
                                        } else if (kind == StudioKind.BOOK_GUIDE) {
                                            listOf(book.id)
                                        } else {
                                            (selectedBookIDs + book.id).takeLast(4)
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // Topic + Duration + Host Voice Card
            PodcastStationeryCard(cornerRadius = 14.dp) {
                Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(t("本期想聊的方向（可选）"), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = YY.c.t2)
                        Box(
                            modifier = Modifier
                                .fillMaxWidth()
                                .height(36.dp)
                                .clip(RoundedCornerShape(9.dp))
                                .background(YY.c.inset)
                                .border(0.5.dp, YY.c.line, RoundedCornerShape(9.dp))
                                .padding(horizontal = 10.dp),
                            contentAlignment = Alignment.CenterStart,
                        ) {
                            BasicTextField(
                                value = topic,
                                onValueChange = { topic = it },
                                singleLine = true,
                                textStyle = TextStyle(fontSize = 13.sp, color = YY.c.t1),
                                cursorBrush = SolidColor(tint.accent),
                                modifier = Modifier.fillMaxWidth(),
                                decorationBox = { inner ->
                                    if (topic.isEmpty()) Text(t("比如：作者为什么这样安排叙事结构？"), fontSize = 12.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                    inner()
                                },
                            )
                        }
                    }

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(t("节目时长"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                        Spacer(Modifier.weight(1f))
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            for (m in listOf(5, 10, 15, 20)) {
                                val on = minutes == m
                                PodcastPaperPill(
                                    title = t("%lld 分", m),
                                    accent = on,
                                    height = 25.dp,
                                    horizontalPadding = 9.dp,
                                ) { minutes = m }
                            }
                        }
                    }

                    HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(t("主播声线"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                        Spacer(Modifier.weight(1f))
                        val currentOption = hub.voiceOptions.firstOrNull { it.id == voiceStyle } ?: hub.voiceOptions.firstOrNull()
                        Box {
                            PodcastPaperPill(
                                title = currentOption?.title ?: t("书房双人 · 温润对谈"),
                                icon = Icons.waveform,
                                height = 27.dp,
                            ) { voiceMenu = true }
                            DropdownMenu(voiceMenu, { voiceMenu = false }, containerColor = YY.c.card, shape = RoundedCornerShape(14.dp)) {
                                for (opt in hub.voiceOptions) {
                                    DropdownMenuItem(
                                        text = {
                                            Column {
                                                Text(opt.title, fontSize = 13.5.sp, color = YY.c.t1, fontWeight = if (opt.id == voiceStyle) FontWeight.SemiBold else FontWeight.Normal)
                                                Text(opt.subtitle, fontSize = 11.sp, color = YY.c.t3)
                                            }
                                        },
                                        trailingIcon = {
                                            if (opt.id == voiceStyle) Icon(Icons.check, null, tint = tint.accent, modifier = Modifier.size(16.dp))
                                        },
                                        onClick = {
                                            voiceStyle = opt.id
                                            hub.selectStudioVoiceStyle(opt.id)
                                            voiceMenu = false
                                        },
                                    )
                                }
                            }
                        }
                    }
                }
            }

            // Action Footer
            val canStart = !hub.isGeneratingStudio && (kind == StudioKind.VOCAB_RADIO || selectedBookIDs.isNotEmpty() || allBooks.isEmpty())
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(44.dp)
                    .clip(CircleShape)
                    .background(if (canStart) YY.c.t1 else YY.c.inset)
                    .pressable(label = t("开始酿制"), enabled = canStart, scale = 0.98f) {
                        scope.launch {
                            val created = hub.generateStudioEpisode(
                                kind = kind,
                                bookIDs = selectedBookIDs,
                                minutes = minutes.toUInt(),
                                topic = topic,
                                noSpoiler = noSpoiler,
                                voiceStyle = voiceStyle,
                            )
                            if (created != null) {
                                hub.start(created, from = 0.0)
                                onCreated(created)
                            }
                        }
                    },
                contentAlignment = Alignment.Center,
            ) {
                if (hub.isGeneratingStudio) {
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(Modifier.size(15.dp), strokeWidth = 2.dp, color = YY.c.t2)
                        Text(hub.studioStatus ?: t("正在酿制…"), fontSize = 13.sp, fontWeight = FontWeight.Medium, color = YY.c.t2)
                    }
                } else {
                    Text(t("开始酿制并收听"), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = if (canStart) YY.c.card else YY.c.t3)
                }
            }
        }
    }

    if (showCompanionSettings) {
        CompanionSettingsSheet {
            showCompanionSettings = false
            companionRefresh++
        }
    }
}

@Composable
private fun StudioBookPickRow(book: BookSummary, selected: Boolean, onToggle: () -> Unit) {
    val tint = YY.tint
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(if (selected) tint.accentSoft else YY.c.inset.copy(alpha = 0.55f))
            .border(0.5.dp, if (selected) tint.accent.copy(alpha = 0.4f) else Color.Transparent, RoundedCornerShape(10.dp))
            .clickable(onClick = onToggle)
            .padding(horizontal = 9.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        BookCover(book, Modifier.width(26.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(book.title, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(
                listOfNotNull(
                    book.authors.firstOrNull(),
                    t("已读 %lld%%", (book.progress * 100.0).roundToInt()),
                ).joinToString(" · "),
                fontSize = 11.sp,
                color = YY.c.t3,
                style = YY.tnum,
                maxLines = 1,
            )
        }
        Icon(
            if (selected) Icons.checkCircleFill else Icons.circle,
            contentDescription = null,
            tint = if (selected) tint.accent else YY.c.t3,
            modifier = Modifier.size(18.dp),
        )
    }
}

// MARK: - Clips, Queue & Voice Status Drawer Sheet

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PodcastDrawerSheet(
    library: Library,
    initialTab: PodcastDrawerTab = PodcastDrawerTab.Clips,
    onOpenIssue: (String) -> Unit,
    onDismiss: () -> Unit,
) {
    val hub = library.podcastHub
    val tint = YY.tint
    var tab by remember { mutableStateOf(initialTab) }

    LaunchedEffect(Unit) { hub.refreshVoiceOptions() }

    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 18.dp)
                .padding(bottom = 26.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for ((tItem, label) in listOf(
                    PodcastDrawerTab.Clips to t("原声书摘"),
                    PodcastDrawerTab.Queue to t("稍后听"),
                    PodcastDrawerTab.Voices to t("声音卡与本机"),
                )) {
                    PodcastPaperPill(title = label, accent = tab == tItem, height = 28.dp) {
                        tab = tItem
                    }
                }
                Spacer(Modifier.weight(1f))
                PodcastCircleIconButton(Icons.close, t("关闭"), size = 28.dp, iconSize = 14.dp, onClick = onDismiss)
            }

            PodcastStationeryCard(cornerRadius = 14.dp) {
                Column(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(min = 220.dp, max = 420.dp)
                        .verticalScroll(rememberScrollState())
                        .padding(14.dp),
                    verticalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    when (tab) {
                        PodcastDrawerTab.Clips -> {
                            if (hub.clips.isEmpty()) {
                                Text(
                                    t("还没有原声书摘。听播客时点「划线」，会把打动你的那几句连同时间戳一起留在这里。"),
                                    fontSize = 12.5.sp,
                                    lineHeight = 19.sp,
                                    color = YY.c.t3,
                                    modifier = Modifier.padding(vertical = 24.dp),
                                )
                            } else {
                                hub.clips.forEachIndexed { idx, clip ->
                                    if (idx > 0) HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
                                    ClipCardRow(
                                        clip = clip,
                                        isPlaying = hub.playingClipID == clip.id,
                                        onPlay = { hub.playClip(clip) },
                                        onJump = {
                                            hub.issue(clip.issueId)?.let { ep ->
                                                hub.start(ep, from = clip.t0)
                                                onOpenIssue(ep.id)
                                            }
                                        },
                                        onDelete = { hub.deleteClip(clip.id) },
                                    )
                                }
                            }
                        }

                        PodcastDrawerTab.Queue -> {
                            val queue = hub.newsstandIssues + hub.archiveIssues
                            if (queue.isEmpty()) {
                                Text(
                                    t("报刊栏与归档目前是空的。订阅节目新到的单集会安静停在这里，不弹红点催促。"),
                                    fontSize = 12.5.sp,
                                    lineHeight = 19.sp,
                                    color = YY.c.t3,
                                    modifier = Modifier.padding(vertical = 24.dp),
                                )
                            } else {
                                queue.forEachIndexed { idx, ep ->
                                    if (idx > 0) HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
                                    Row(
                                        modifier = Modifier.fillMaxWidth().padding(vertical = 6.dp),
                                        verticalAlignment = Alignment.CenterVertically,
                                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                                    ) {
                                        PodcastShowMark(ep.showTitle, ep.artworkUrl, ep.studioKind != null, 28.dp)
                                        Column(Modifier.weight(1f).clickable { onOpenIssue(ep.id) }) {
                                            Text(ep.title, fontSize = 13.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                            Text("${ep.showTitle} · ${PodcastFormat.compactDuration(ep.durationSec)}", fontSize = 11.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
                                        }
                                        PodcastPaperPill(t("放上桌"), accent = true, height = 24.dp) {
                                            hub.setPlacement(ep.id, PodcastPlacement.DESK)
                                        }
                                    }
                                }
                            }
                        }

                        PodcastDrawerTab.Voices -> {
                            Text(t("本机语音合成（ZipVoice 24kHz）"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                            val voiceState = LiveVoiceModel.state
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                val desc = when (voiceState) {
                                    is LiveVoiceModel.State.Ready -> t("已就绪 · 书房电台与原声线中文均在本机离线合成")
                                    is LiveVoiceModel.State.Downloading -> t("正在下载本机声音模型 · %lld%%", (voiceState.progress * 100).roundToInt())
                                    is LiveVoiceModel.State.Unsupported -> t("这台手机内存较小，自动使用系统离线语音")
                                    else -> t("下载 160 MB 声音包后，电台主播将使用自然真人质感声线")
                                }
                                Text(desc, fontSize = 12.sp, color = YY.c.t2, style = YY.tnum, modifier = Modifier.weight(1f))
                                if (voiceState !is LiveVoiceModel.State.Ready && voiceState !is LiveVoiceModel.State.Downloading && voiceState !is LiveVoiceModel.State.Unsupported) {
                                    PodcastPaperPill(t("下载"), accent = true, height = 25.dp) {
                                        LiveVoiceModel.startDownload(library.core, library.dataDir)
                                    }
                                }
                            }

                            HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

                            Text(t("本机离线听写（SenseVoice）"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                            val asrReady = Listener.installed(library.dataDir)
                            Text(
                                if (asrReady) t("已安装 · 导入的本机讲座或播客音频可一键转出逐字稿")
                                else t("未安装（约 230 MB）· 在无逐字稿的本地音频里点「本机转文字」时按需下载"),
                                fontSize = 12.sp,
                                color = YY.c.t2,
                            )

                            HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

                            Text(t("可用电台声线"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                            for (opt in hub.voiceOptions) {
                                val chosen = hub.studioVoiceStyle == opt.id
                                Row(
                                    modifier = Modifier
                                        .fillMaxWidth()
                                        .clip(RoundedCornerShape(9.dp))
                                        .background(if (chosen) tint.accentSoft else Color.Transparent)
                                        .clickable { hub.selectStudioVoiceStyle(opt.id) }
                                        .padding(horizontal = 8.dp, vertical = 7.dp),
                                    verticalAlignment = Alignment.CenterVertically,
                                ) {
                                    Column(Modifier.weight(1f)) {
                                        Text(opt.title, fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                                        Text(opt.subtitle, fontSize = 11.sp, color = YY.c.t3)
                                    }
                                    if (chosen) Icon(Icons.check, null, tint = tint.accent, modifier = Modifier.size(16.dp))
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ClipCardRow(
    clip: PodcastClip,
    isPlaying: Boolean,
    onPlay: () -> Unit,
    onJump: () -> Unit,
    onDelete: () -> Unit,
) {
    val tint = YY.tint
    Column(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(
            "“${clip.quoteText}”",
            fontFamily = YY.serif,
            fontSize = 13.5.sp,
            lineHeight = 20.sp,
            color = YY.c.t1,
            modifier = Modifier.clickable(onClick = onJump),
        )
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(
                "${clip.showTitle} · ${PodcastFormat.clock(clip.t0)}",
                fontSize = 11.sp,
                color = YY.c.t3,
                style = YY.tnum,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            PodcastPaperPill(
                title = if (isPlaying) t("暂停原声") else t("回放原声"),
                icon = if (isPlaying) Icons.pause else Icons.play,
                accent = isPlaying,
                height = 24.dp,
                horizontalPadding = 9.dp,
                onClick = onPlay,
            )
            Box(Modifier.size(26.dp).clip(CircleShape).clickable(onClick = onDelete), contentAlignment = Alignment.Center) {
                Icon(Icons.delete, t("删除"), tint = YY.c.t3, modifier = Modifier.size(14.dp))
            }
        }
    }
}
