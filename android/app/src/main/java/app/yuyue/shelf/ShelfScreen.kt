package app.yuyue.shelf

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.add
import androidx.compose.foundation.layout.windowInsetsTopHeight
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.platform.LocalDensity
import kotlinx.coroutines.launch
import androidx.activity.compose.BackHandler
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.ButtonKind
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.pressScale
import app.yuyue.design.Icons
import app.yuyue.design.ProgressLine
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYMotion
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.library.ShelfFilter
import app.yuyue.library.ShelfSort
import app.yuyue.library.author
import app.yuyue.library.percent
import app.yuyue.library.remaining
import app.yuyue.library.shelfNote
import app.yuyue.library.title
import app.yuyue.t
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.ReadingStatus

/** What 导入 can pick: anything; the core says what it can't read. */
val importTypes = arrayOf("*/*")

/** 书架 on the phone, as on iPhone: the books being read across the top, to read or listen on at
 *  a tap; all the books below, in an order. The title names where the shelf is — 书, 进度, 有声
 *  or one of your 合集 — and a tap on it (or, scrolled, on the chip at the top left) opens the
 *  panel that goes to another. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShelfScreen(library: Library, chrome: ShelfChrome, openWifi: () -> Unit, openRemote: () -> Unit, openWatched: () -> Unit, openFeedback: () -> Unit = {}, openRecord: (String) -> Unit = {}) {
    var place by remember { mutableStateOf<ShelfPlace>(ShelfPlace.Books) }
    var asking by remember { mutableStateOf<app.yuyue.me.Confirm?>(null) }
    app.yuyue.me.ConfirmHost(asking) { asking = null }
    var sortKey by rememberPref("shelf.sort", "recent")
    val showProgress by rememberPref("shelf.showProgress", true)
    val coverSize by rememberPref("shelf.coverSize", 100f)
    var importShown by remember { mutableStateOf(false) }
    var panel by remember { mutableStateOf<PanelFrom?>(null) }
    var naming by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf<uniffi.yuyue_core.BookCollection?>(null) }
    var iconFor by remember { mutableStateOf<uniffi.yuyue_core.BookCollection?>(null) }
    var selecting by remember { mutableStateOf(false) }
    var selected by remember { mutableStateOf(setOf<String>()) }
    var editing by remember { mutableStateOf<BookSummary?>(null) }
    var picking by remember { mutableStateOf<BookSummary?>(null) }
    var addingBook by remember { mutableStateOf(false) }
    var addingPodcast by remember { mutableStateOf(false) }
    var progressStatus by rememberSaveable { mutableStateOf(ReadingStatus.READING) }
    val sort = ShelfSort.of(sortKey)
    val collection = (place as? ShelfPlace.Collection)?.id
    val grid = rememberLazyGridState()
    val density = LocalDensity.current
    val scrolled by remember { derivedStateOf { grid.firstVisibleItemIndex > 0 || grid.firstVisibleItemScrollOffset > with(density) { 72.dp.toPx() } } }
    val scope = rememberCoroutineScope()

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris: List<Uri> ->
        library.importUris(uris, collection)
    }

    val all = library.shelfBooks
    fun info(p: ShelfPlace): PlaceInfo = when (p) {
        ShelfPlace.Books -> PlaceInfo(p, t("书"), PlaceIcons.books, t("%lld 本", all.size), listOf(t("%lld 本", all.size), t("在读 %lld", all.count { it.status == ReadingStatus.READING })))
        ShelfPlace.Progress -> {
            val kept = library.keptBooks
            val reading = kept.count { it.status == ReadingStatus.READING }
            PlaceInfo(p, t("进度"), PlaceIcons.progress, t("在读 %lld", reading), listOf(t("在读 %lld", reading), t("想读 %lld", kept.count { it.status == ReadingStatus.WANT })))
        }
        ShelfPlace.Podcasts -> {
            val desk = if (library.hasPodcastHub) library.podcastHub.deskIssues else runCatching { library.core.podcastIssues(uniffi.yuyue_core.PodcastPlacement.DESK) }.getOrDefault(emptyList())
            val news = if (library.hasPodcastHub) library.podcastHub.newsstandIssues else runCatching { library.core.podcastIssues(uniffi.yuyue_core.PodcastPlacement.NEWSSTAND) }.getOrDefault(emptyList())
            val total = desk.size + news.size
            PlaceInfo(
                p, t("播客"), PlaceIcons.podcasts,
                if (desk.isNotEmpty()) t("桌上 %lld", desk.size) else t("%lld 期", total),
                listOf(t("桌上 %lld 期", desk.size), t("报刊栏 %lld", news.size)),
                fresh = news.isNotEmpty(),
            )
        }
        ShelfPlace.Audio -> {
            val audio = all.filter { it.format == BookFormat.AUDIO }
            val listening = audio.count { it.status == ReadingStatus.READING }
            PlaceInfo(p, t("有声"), PlaceIcons.audio, if (listening > 0) t("在听 %lld", listening) else t("%lld 本", audio.size), listOf(t("%lld 本", audio.size), t("在听 %lld", listening)))
        }
        is ShelfPlace.Collection -> {
            val c = library.collections.firstOrNull { it.id == p.id }
            val n = c?.count?.toInt() ?: 0
            PlaceInfo(p, c?.name ?: t("合集"), c?.let { app.yuyue.library.CollectionIcon.of(it) } ?: Icons.collection, t("%lld 本", n), listOf(t("合集"), t("%lld 本", n)), c)
        }
    }
    // A 合集 deleted elsewhere: back to 书.
    if (place is ShelfPlace.Collection && library.collections.none { it.id == (place as ShelfPlace.Collection).id }) place = ShelfPlace.Books
    val now = info(place)
    // The first tab wears the place: 进度, 播客, 有声 or the 合集, with its mark.
    LaunchedEffect(place, now.name, now.icon) {
        chrome.place = place
        chrome.name = if (place == ShelfPlace.Books) null else now.name
        chrome.icon = if (place == ShelfPlace.Books) null else now.icon
    }
    fun go(to: ShelfPlace) {
        place = to
        scope.launch { grid.scrollToItem(0) }
    }
    val kinds = ShelfPlace.kinds.map(::info)
    val collections = library.collections.map { info(ShelfPlace.Collection(it.id)) }

    val shown = when (val p = place) {
        ShelfPlace.Progress, ShelfPlace.Podcasts -> emptyList()
        ShelfPlace.Audio -> sort.sorted(all.filter { it.format == BookFormat.AUDIO })
        is ShelfPlace.Collection -> sort.sorted(all.filter { p.id in it.collections })
        ShelfPlace.Books -> sort.sorted(all)
    }
    val reading = if (!selecting && (place == ShelfPlace.Books || place == ShelfPlace.Audio))
        shown.filter { it.status == ReadingStatus.READING }.let { r -> if (sort == ShelfSort.Recent) r else all.filter { it.status == ReadingStatus.READING && it in shown } } else emptyList()

    Box(Modifier.fillMaxSize().background(YY.c.bg)) {
        LazyVerticalGrid(
            columns = GridCells.Adaptive((96 * coverSize / 100f).dp),
            state = grid,
            modifier = Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.statusBars),
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, bottom = app.yuyue.LocalTabBarHeight.current + 16.dp),
            horizontalArrangement = Arrangement.spacedBy(18.dp),
            verticalArrangement = Arrangement.spacedBy(22.dp),
        ) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                if (selecting) {
                    TopBar(
                        selectedCount = selected.size, allCount = shown.size,
                        onDone = { selecting = false; selected = emptySet() },
                        onToggleAll = { selected = if (selected.size == shown.size) emptySet() else shown.map { it.id }.toSet() },
                    )
                } else {
                    // Room at the right for the buttons, which stay at the top as the shelf scrolls.
                    PlaceTitle(now, Modifier.padding(top = 14.dp, end = 150.dp).alpha(if (panel == PanelFrom.Title) 0f else 1f)) { panel = PanelFrom.Title }
                }
            }
            // 在读 — shown only when there is at least one book being read.
            if (reading.isNotEmpty()) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    ReadingRow(library, reading) { book, intent -> library.open(book, intent) }
                }
            }
            if (place == ShelfPlace.Podcasts) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    app.yuyue.podcast.PodcastShelfContent(library) { issueId ->
                        library.podcastHub.requestedOpenIssueID = issueId
                    }
                }
            } else if (place == ShelfPlace.Progress) {
                progressItems(library, progressStatus, { progressStatus = it }, show = openRecord, pick = { picking = it })
            } else if (all.isEmpty() && place == ShelfPlace.Books) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Welcome(onFiles = { picker.launch(importTypes) }, onWifi = openWifi, onRemote = openRemote)
                }
            } else if (shown.isEmpty()) {
                item(span = { GridItemSpan(maxLineSpan) }) { Empty(library, collection != null) { picker.launch(importTypes) } }
            } else {
                // 全部, how many, and their order — as 在读's own row above.
                item(span = { GridItemSpan(maxLineSpan) }) {
                    AllRow(shown.size, sort) { sortKey = it.key }
                }
                items(shown, key = { it.id }) { book ->
                    // Sorted, filtered, brought in or taken out, each book moves to its place.
                    Box(Modifier.animateItem(fadeInSpec = app.yuyue.design.YYMotion.standard(), placementSpec = app.yuyue.design.YYMotion.place(), fadeOutSpec = app.yuyue.design.YYMotion.exit())) {
                    Cell(
                        library, book, showProgress, selecting, book.id in selected,
                        onTap = {
                            if (selecting) selected = if (book.id in selected) selected - book.id else selected + book.id
                            else library.open(book)
                        },
                        onEdit = { editing = book },
                        onPick = { picking = book },
                    )
                    }
                }
            }
        }

        val reduce = YYMotion.isReduceMotion()
        if (!selecting) {
            // Scrolled: the shelf fades out under the chip and the buttons.
            val fade by animateFloatAsState(if (scrolled) 1f else 0f, YYMotion.standard(), label = "fade")
            Box(Modifier.fillMaxWidth().windowInsetsTopHeight(WindowInsets.statusBars.add(WindowInsets(top = 74.dp))).alpha(fade)
                .background(Brush.verticalGradient(listOf(YY.c.bg, YY.c.bg.copy(alpha = 0.85f), YY.c.bg.copy(alpha = 0f)))))
            Box(Modifier.align(Alignment.TopEnd).windowInsetsPadding(WindowInsets.statusBars).padding(top = 14.dp, end = 16.dp)) {
                TopButtons {
                    TopButton(Icons.feedback, t("反馈"), action = openFeedback)
                    if (library.importing > 0) Box(Modifier.size(40.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = YY.c.t2)
                    } else TopButton(
                        Icons.add,
                        when (place) {
                            ShelfPlace.Progress -> t("加书")
                            ShelfPlace.Podcasts -> t("添加节目")
                            else -> t("导入")
                        }
                    ) {
                        when (place) {
                            ShelfPlace.Progress -> addingBook = true
                            ShelfPlace.Podcasts -> addingPodcast = true
                            else -> importShown = true
                        }
                    }
                    if (place != ShelfPlace.Podcasts) {
                        TopButton(Icons.checkCircle, t("选择"), enabled = shown.isNotEmpty()) { selecting = true }
                    }
                }
            }
            AnimatedVisibility(
                scrolled, Modifier.windowInsetsPadding(WindowInsets.statusBars).padding(top = 11.5.dp, start = 16.dp),
                enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = 0.92f, transformOrigin = TransformOrigin(0f, 0.5f)),
                exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.92f, transformOrigin = TransformOrigin(0f, 0.5f)),
            ) {
                Box(Modifier.alpha(if (panel == PanelFrom.Chip) 0f else 1f)) { PlaceChip(now) { chrome.switching = false; panel = PanelFrom.Chip } }
            }
        }

        // 书架 tapped again: the places stand up over the tab.
        AnimatedVisibility(chrome.switching, enter = fadeIn(YYMotion.quick()), exit = fadeOut(YYMotion.exit())) {
            Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.06f)).clickable(remember { MutableInteractionSource() }, null) { chrome.switching = false })
        }
        AnimatedVisibility(
            chrome.switching, Modifier.align(Alignment.BottomStart).padding(start = 12.dp, bottom = app.yuyue.LocalTabBarHeight.current + 10.dp),
            enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = 0.92f, transformOrigin = TransformOrigin(0f, 1f)),
            exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.92f, transformOrigin = TransformOrigin(0f, 1f)),
        ) {
            PlaceStack(place, kinds, collections) { to -> chrome.switching = false; if (to != place) go(to) }
        }

        // The panel grows out of the title (or the chip), its head where the title was.
        if (panel != null) {
            Box(Modifier.fillMaxSize().clickable(remember { MutableInteractionSource() }, null) { panel = null })
        }
        val from = panel
        AnimatedVisibility(
            from != null, Modifier.fillMaxWidth().padding(horizontal = 16.dp),
            enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = 0.92f, transformOrigin = TransformOrigin(0.12f, 0.06f)),
            exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.92f, transformOrigin = TransformOrigin(0.12f, 0.06f)),
        ) {
            // Under the buttons at the top right, never over them.
            val top = with(density) { WindowInsets.statusBars.getTop(this).toDp() } + 58.dp
            Box(Modifier.padding(top = top)) {
                PlacePanel(
                    now, kinds, collections,
                    go = { to -> panel = null; if (to != place) go(to) },
                    newCollection = { panel = null; naming = true },
                    rename = { panel = null; renaming = it },
                    icon = { panel = null; iconFor = it },
                    delete = { c ->
                        panel = null
                        asking = app.yuyue.me.Confirm(t("删除合集“%@”？", c.name), t("这个合集删掉，里面的书还在书架上。"), t("删除")) {
                            if (place == ShelfPlace.Collection(c.id)) go(ShelfPlace.Books)
                            library.deleteCollection(c)
                        }
                    },
                )
            }
        }
        BackHandler(enabled = panel != null || chrome.switching) { panel = null; chrome.switching = false }

        Column(Modifier.align(Alignment.BottomCenter).padding(bottom = app.yuyue.LocalTabBarHeight.current).padding(horizontal = 8.dp, vertical = 8.dp)) {
            AnimatedVisibility(selecting, enter = YYMotion.barIn(false, YYMotion.isReduceMotion()), exit = YYMotion.barOut(false, YYMotion.isReduceMotion())) {
                SelectionBar(
                    enabled = selected.isNotEmpty(),
                    finish = {
                        library.books.filter { it.id in selected }.forEach { library.setFinished(it, true) }
                        selecting = false; selected = emptySet()
                    },
                    delete = {
                        val picked = library.books.filter { it.id in selected }
                        val title = if (picked.size == 1) t("删除《%@》？", picked[0].title) else t("删除这 %lld 本书？", picked.size)
                        asking = app.yuyue.me.Confirm(title, t("书会移到已删除，30 天内可以在存储里撤销。"), t("删除")) {
                            library.deleteAll(picked)
                            selecting = false; selected = emptySet()
                        }
                    },
                )
            }
            val podIssue = if (place == ShelfPlace.Podcasts && library.hasPodcastHub) library.podcastHub.activeIssue else null
            AnimatedVisibility(
                visible = !selecting && place == ShelfPlace.Podcasts && scrolled && podIssue != null && library.nowPlaying == null,
                enter = YYMotion.barIn(false, YYMotion.isReduceMotion()),
                exit = YYMotion.barOut(false, YYMotion.isReduceMotion()),
            ) {
                podIssue?.let { ep ->
                    app.yuyue.podcast.PodcastMiniPlayerBar(library, ep) {
                        library.podcastHub.requestedOpenIssueID = ep.id
                    }
                }
            }
        }
    }

    if (importShown) {
        ImportSheet(
            watching = library.watched.name,
            dismiss = { importShown = false },
            files = { importShown = false; picker.launch(importTypes) },
            wifi = { importShown = false; openWifi() },
            remote = { importShown = false; openRemote() },
            watch = { importShown = false; openWatched() },
        )
    }
    if (naming) {
        NameDialog(t("新建合集"), "") { name ->
            naming = false
            if (name != null) library.createCollection(name)?.let { go(ShelfPlace.Collection(it)) }
        }
    }
    renaming?.let { c ->
        NameDialog(t("重命名合集"), c.name) { name ->
            renaming = null
            if (name != null && name != c.name) library.renameCollection(c.id, name)
        }
    }
    iconFor?.let { c -> IconPicker(c, { iconFor = null }) { symbol -> iconFor = null; library.setCollectionIcon(c.id, symbol) } }
    editing?.let { BookInfoSheet(library, it) { editing = null } }
    picking?.let { CollectionPickerSheet(library, it.id) { picking = null } }
    if (addingBook) AddBookSheet(library, progressStatus) { addingBook = false }
    if (addingPodcast) app.yuyue.podcast.PodcastAddSheet(library) { addingPodcast = false }
}

private enum class PanelFrom { Title, Chip }

/** 全部, how many, and their order. */
@Composable
internal fun AllRow(count: Int, sort: ShelfSort, onSort: (ShelfSort) -> Unit) {
    var sorting by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(t("全部"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
        Text(t("%lld 本", count), fontSize = 13.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.padding(start = 8.dp))
        Spacer(Modifier.weight(1f))
        Box {
            Row(Modifier.clickable { sorting = true }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(sort.title, fontSize = 13.sp, color = YY.c.t2)
                Icon(PlaceIcons.switch, null, tint = YY.c.t2, modifier = Modifier.size(14.dp))
            }
            DropdownMenu(sorting, { sorting = false }, containerColor = YY.c.card) {
                for (s in ShelfSort.entries) {
                    DropdownMenuItem(
                        text = { Text(s.title, color = YY.c.t1) },
                        trailingIcon = { if (s == sort) Icon(Icons.check, null, tint = YY.tint.accent) },
                        onClick = { onSort(s); sorting = false },
                    )
                }
            }
        }
    }
}

/** Picking books: 全选 · 已选 3 本 · 完成. */
@Composable
private fun TopBar(selectedCount: Int, allCount: Int, onDone: () -> Unit, onToggleAll: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(48.dp).padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(if (selectedCount == allCount) t("全不选") else t("全选"), fontSize = 15.sp, color = YY.tint.accent, modifier = Modifier.clickable(onClick = onToggleAll))
        Spacer(Modifier.weight(1f))
        Text(if (selectedCount == 0) t("选择书") else t("已选 %lld 本", selectedCount), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, style = YY.tnum)
        Spacer(Modifier.weight(1f))
        Text(t("完成"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent, modifier = Modifier.clickable(onClick = onDone))
    }
}

@Composable
private fun ReadingRow(library: Library, reading: List<BookSummary>, open: (BookSummary, Library.OpenIntent?) -> Unit) {
    Column(Modifier.padding(top = 18.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(t("在读"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
            Spacer(Modifier.weight(1f))
            Text(t("%lld 本", reading.size), fontSize = 13.sp, color = YY.c.t3, style = YY.tnum)
        }
        Box(Modifier.padding(top = 14.dp)) { ReadingShelf(library, reading, open) }
    }
}

/** What this is, then the three ways to put books in. */
@Composable
internal fun Welcome(onFiles: () -> Unit, onWifi: () -> Unit, onRemote: () -> Unit) {
    Column(Modifier.padding(top = YY.s(14))) {
        Text(t("把书放进来，\n读或者听都行。"), fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = YY.sp(21f), lineHeight = YY.sp(31f), color = YY.c.t1)
        Text(t("不用注册。书、进度和笔记都存在你自己的设备里。"), style = YY.font(11f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(6)))
        androidx.compose.foundation.layout.BoxWithConstraints(Modifier.padding(top = YY.s(16))) {
            if (maxWidth < 520.dp) {
                Column(verticalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    WayRow(Icons.folder, t("从文件导入"), t("本机或云盘里的"), onFiles)
                    WayRow(Icons.wifi, t("Wi‑Fi 传书"), t("用电脑浏览器传"), onWifi)
                    WayRow(Icons.server, t("远程书库"), t("Calibre、NAS"), onRemote)
                }
            } else {
                Row(horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                    Way(Icons.folder, t("从文件导入"), t("本机或云盘里的"), Modifier.weight(1f), onFiles)
                    Way(Icons.wifi, t("Wi‑Fi 传书"), t("用电脑浏览器传"), Modifier.weight(1f), onWifi)
                    Way(Icons.server, t("远程书库"), t("Calibre、NAS"), Modifier.weight(1f), onRemote)
                }
            }
        }
    }
}

@Composable
private fun WayRow(icon: ImageVector, title: String, detail: String, action: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(YY.s(16)))
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16)))
            .clickable(onClick = action)
            .padding(start = YY.s(14), end = YY.s(16), top = YY.s(12), bottom = YY.s(12)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(12)),
    ) {
        Box(Modifier.size(YY.s(36)).clip(CircleShape).background(YY.c.inset), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(18)))
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
            Text(title, style = YY.font(14f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(detail, style = YY.font(12f), color = YY.c.t2, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
        Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
    }
}

@Composable
private fun Way(icon: ImageVector, title: String, detail: String, modifier: Modifier, action: () -> Unit) {
    Column(
        modifier
            .clip(RoundedCornerShape(YY.s(16)))
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16)))
            .clickable(onClick = action)
            .padding(start = YY.s(10), end = YY.s(10), top = YY.s(12), bottom = YY.s(11)),
        verticalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Box(Modifier.size(YY.s(28)).clip(RoundedCornerShape(YY.s(8))).background(YY.c.inset), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(14)))
        }
        Column {
            Text(title, style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(detail, style = YY.font(10f), color = YY.c.t3, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun Empty(library: Library, inCollection: Boolean, import: () -> Unit) {
    val (icon, text) = when {
        library.books.isEmpty() -> Icons.books to t("书架是空的。点右上角的加号，导入 EPUB、PDF、MOBI 或 TXT")
        inCollection -> Icons.inbox to t("这个合集里还没有书。长按一本书，点「加入合集」")
        else -> Icons.book to t("这里还没有书")
    }
    Column(Modifier.fillMaxWidth().heightIn(min = 300.dp).padding(horizontal = 40.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp, Alignment.CenterVertically)) {
        Icon(icon, null, tint = YY.c.t3, modifier = Modifier.size(40.dp))
        Text(text, fontSize = 15.sp, color = YY.c.t2, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
        if (library.books.isEmpty()) YYButton(t("导入书"), ButtonKind.Main, onClick = import)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun Cell(
    library: Library, book: BookSummary, showProgress: Boolean, selecting: Boolean, chosen: Boolean,
    onTap: () -> Unit, onEdit: () -> Unit, onPick: () -> Unit,
) {
    var menu by remember { mutableStateOf(false) }
    val haptics = LocalHapticFeedback.current
    val tint = YY.tint
    // The cell gives a little while pressed.
    val press = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }
    Box {
        Column(
            Modifier.pressScale(press).combinedClickable(
                interactionSource = press,
                indication = androidx.compose.material3.ripple(),
                onClick = onTap,
                onLongClick = if (selecting) null else ({ haptics.performHapticFeedback(HapticFeedbackType.LongPress); menu = true }),
            ),
        ) {
            Box(Modifier.padding(bottom = 8.dp)) {
                BookCover(book, Modifier.fillMaxWidth().then(if (book.available) Modifier else Modifier.background(Color.Transparent)))
                when {
                    selecting -> Box(Modifier.align(Alignment.BottomEnd).padding(6.dp)) {
                        Icon(
                            if (chosen) Icons.checkCircleFill else Icons.circle, if (chosen) t("已选") else t("未选"),
                            tint = if (chosen) tint.accent else Color.White,
                            modifier = Modifier.size(24.dp).clip(CircleShape).background(if (chosen) Color.White else Color.Black.copy(alpha = 0.25f)),
                        )
                    }
                    !book.available -> Box(Modifier.align(Alignment.BottomEnd).padding(6.dp).size(24.dp).clip(CircleShape).background(YY.c.card), contentAlignment = Alignment.Center) {
                        Icon(Icons.cloudDownload, null, tint = YY.c.t2, modifier = Modifier.size(13.dp))
                    }
                    book.format == BookFormat.AUDIO -> AudioBadge(Modifier.align(Alignment.BottomEnd))
                }
            }
            Text(book.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(bottom = 2.dp))
            if (showProgress) {
                if (library.listening == book.id) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                        Icon(Icons.headphones, null, tint = tint.accent, modifier = Modifier.size(11.dp))
                        Text(t("听中 · %@", book.percent), fontSize = 11.sp, color = tint.accent, style = YY.tnum)
                    }
                } else {
                    Text(book.shelfNote, fontSize = 11.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        BookMenu(library, book, menu, { menu = false }, onEdit, onPick)
    }
}

/** 长按: the book a size smaller with how far it is, then 接着读 · 从头听 · 标为读完, and
 *  编辑信息 · 加入合集 · 删除, as confirmed in the design. */
@Composable
private fun BookMenu(library: Library, book: BookSummary, shown: Boolean, dismiss: () -> Unit, onEdit: () -> Unit, onPick: () -> Unit) {
    var asking by remember { mutableStateOf<app.yuyue.me.Confirm?>(null) }
    app.yuyue.me.ConfirmHost(asking) { asking = null }
    DropdownMenu(shown, dismiss, containerColor = YY.c.card, shape = RoundedCornerShape(14.dp)) {
        BookPreview(book)
        HorizontalDivider(color = YY.c.line)
        val audio = book.format == BookFormat.AUDIO
        val resume = when {
            audio -> if (book.progress > 0) t("接着听") else t("开始听")
            book.status == ReadingStatus.FINISHED -> t("重读")
            else -> if (book.progress > 0) t("接着读") else t("开始读")
        }
        Item(resume, if (audio) Icons.headphones else Icons.book) { dismiss(); library.open(book, if (audio) Library.OpenIntent.Listen else null) }
        Item(t("从头听"), Icons.headphones) { dismiss(); library.open(book, Library.OpenIntent.ListenFromStart) }
        val finished = book.status == ReadingStatus.FINISHED
        Item(
            if (audio) (if (finished) t("标为没听完") else t("标为听完")) else if (finished) t("标为没读完") else t("标为读完"),
            if (finished) Icons.undo else Icons.check,
        ) { dismiss(); library.setFinished(book, !finished) }
        HorizontalDivider(color = YY.c.line)
        Item(t("编辑信息"), Icons.edit) { dismiss(); onEdit() }
        Item(t("加入合集"), Icons.inbox) { dismiss(); onPick() }
        Item(t("删除"), Icons.delete, danger = true) {
            dismiss()
            asking = app.yuyue.me.Confirm(t("删除《%@》？", book.title), t("书会移到已删除，30 天内可以在存储里撤销。"), t("删除")) { library.delete(book) }
        }
    }
}

/** Menu rows as the Mac's white card: words on the left, the icon on the right. */
@Composable
fun Item(title: String, icon: ImageVector, danger: Boolean = false, onClick: () -> Unit) {
    val color = if (danger) YY.c.danger else YY.c.t1
    DropdownMenuItem(
        text = { Text(title, color = color, fontSize = 15.sp) },
        trailingIcon = { Icon(icon, null, tint = color, modifier = Modifier.size(20.dp)) },
        onClick = onClick,
    )
}

/** What floats up when a book is pressed: the cover a size smaller, the title, and how far —
 *  a thin line and how long is left, not a percentage. */
@Composable
fun BookPreview(book: BookSummary) {
    Row(Modifier.widthIn(max = 300.dp).padding(14.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
        BookCover(book, Modifier.width(44.dp))
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(book.title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false).alignByBaseline())
                book.author?.let { Text(it, fontSize = 12.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.widthIn(max = 112.dp).alignByBaseline()) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalAlignment = Alignment.CenterVertically) {
                if (book.progress > 0 && book.status != ReadingStatus.FINISHED) {
                    ProgressLine(book.progress, 2.5.dp, Modifier.weight(1f))
                }
                Text(book.remaining, fontSize = 12.sp, color = YY.c.t2, style = YY.tnum, maxLines = 1)
            }
        }
    }
}

@Composable
private fun SelectionBar(enabled: Boolean, finish: () -> Unit, delete: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 44.dp).padding(bottom = 8.dp)
            .clip(CircleShape).background(YY.c.card).border(0.5.dp, YY.c.line, CircleShape).padding(horizontal = 8.dp),
    ) {
        BarButton(t("标为读完"), Icons.check, enabled, Modifier.weight(1f), onClick = finish)
        BarButton(t("删除"), Icons.delete, enabled, Modifier.weight(1f), danger = true, onClick = delete)
    }
}

@Composable
private fun BarButton(title: String, icon: ImageVector, enabled: Boolean, modifier: Modifier, danger: Boolean = false, onClick: () -> Unit) {
    val color = (if (danger) YY.c.danger else YY.c.t1).copy(alpha = if (enabled) 1f else 0.4f)
    Column(modifier.height(54.dp).clickable(enabled = enabled, onClick = onClick), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
        Icon(icon, null, tint = color, modifier = Modifier.size(20.dp))
        Text(title, fontSize = 11.sp, color = color)
    }
}

/** 导入: from 文件, over Wi‑Fi, a 远程书库, or a folder to keep watching. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ImportSheet(watching: String?, dismiss: () -> Unit, files: () -> Unit, wifi: () -> Unit, remote: () -> Unit, watch: () -> Unit) {
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
        ImportChoices(watching, files, wifi, remote, watch)
    }
}

/** The rows inside [ImportSheet]. The snapshot draws these on a sheet-shaped plate; the system
 *  sheet chrome (the drag handle) is not part of it. */
@Composable
internal fun ImportChoices(watching: String?, files: () -> Unit, wifi: () -> Unit, remote: () -> Unit, watch: () -> Unit) {
    Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
        Text(t("导入"), style = YY.font(14f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(start = 4.dp, bottom = 12.dp))
        app.yuyue.design.YYGroup {
            row { app.yuyue.design.YYRow(t("从文件导入"), Icons.folder, t("本机或云盘里的"), chevron = true, onClick = files) }
            row { app.yuyue.design.YYRow(t("Wi‑Fi 传书"), Icons.wifi, t("用电脑浏览器传"), chevron = true, onClick = wifi) }
            row { app.yuyue.design.YYRow(t("远程书库"), Icons.server, "OPDS · WebDAV", chevron = true, onClick = remote) }
            row { app.yuyue.design.YYRow(t("监听一个文件夹"), Icons.folderGear, watching ?: t("还没选"), chevron = true, onClick = watch) }
        }
        app.yuyue.design.YYNote(t("同一个文件只导入一次：按内容认，改过名字也认得出来"))
    }
}
