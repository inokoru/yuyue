package app.yuyue.shelf

import androidx.activity.compose.BackHandler
import androidx.compose.animation.core.Animatable
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.RectangleShape
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.media3.common.util.UnstableApi
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYSegments
import app.yuyue.design.yyCard
import app.yuyue.library.Library
import app.yuyue.speech.AudiobookPlayer
import app.yuyue.speech.Player
import app.yuyue.t
import app.yuyue.design.ButtonSize
import app.yuyue.design.YYButton
import app.yuyue.design.pressable
import kotlinx.coroutines.launch
import androidx.compose.runtime.rememberCoroutineScope

/** 有声书 open: the cover, the chapter, the scrubber with the 书签 on
 *  it, and the controls; 章节 and 书签 come up from the bottom. */
@UnstableApi
@Composable
fun AudiobookScreen(library: Library, bookID: String, close: () -> Unit) {
    val context = LocalContext.current
    val player = remember(bookID) {
        // Still playing with the page closed: picked up as it is.
        (library.nowPlaying as? AudiobookPlayer)?.takeIf { it.bookID == bookID }?.also { library.nowPlaying = null } ?: run {
            (library.nowPlaying as? AudiobookPlayer)?.close()
            (library.nowPlaying as? app.yuyue.reader.ReaderModel)?.close()
            library.nowPlaying = null
            val book = library.books.first { it.id == bookID }
            AudiobookPlayer(context, library, book).also { p ->
                when (library.takeIntent(bookID)) {
                    Library.OpenIntent.Listen -> p.play()
                    Library.OpenIntent.ListenFromStart -> { p.seekTo(0.0); p.play() }
                    else -> {}
                }
            }
        }
    }
    DisposableEffect(player) {
        onDispose {
            // Closed while it plays: it goes on, with the 播放条 over the tabs.
            if (player.isPlaying || player.waiting) library.nowPlaying = player else player.close()
        }
    }
    BackHandler(onBack = close)
    var list by remember { mutableStateOf<Int?>(null) }
    var settingsShown by remember { mutableStateOf(false) }
    // 合成, 微调 or another device changed the map or the settings.
    androidx.compose.runtime.LaunchedEffect(player) {
        app.yuyue.speech.Recordings.changed.collect { if (it == player.bookID) player.loadMap() }
    }

    val scope = rememberCoroutineScope()
    val pullOffsetY = remember { Animatable(0f) }
    var rawPullY by remember { mutableFloatStateOf(0f) }
    Column(
        Modifier
            .fillMaxSize()
            .graphicsLayer {
                val dy = pullOffsetY.value
                translationY = dy
                shape = if (dy > 1f) RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp) else RectangleShape
                clip = dy > 1f
            }
            .background(YY.c.bg)
            .windowInsetsPadding(WindowInsets.safeDrawing)
            .padding(horizontal = 16.dp),
    ) {
        Row(
            Modifier
                .fillMaxWidth()
                .padding(top = YY.s(4))
                .pointerInput(Unit) {
                    val velocityTracker = VelocityTracker()
                    detectVerticalDragGestures(
                        onDragStart = {
                            rawPullY = pullOffsetY.value
                            velocityTracker.resetTracking()
                        },
                        onDragEnd = {
                            val vy = velocityTracker.calculateVelocity().y
                            if (pullOffsetY.value > 72.dp.toPx() || vy > 500.dp.toPx()) {
                                close()
                            } else {
                                scope.launch { pullOffsetY.animateTo(0f, app.yuyue.design.YYMotion.layout()) }
                            }
                        },
                        onDragCancel = {
                            scope.launch { pullOffsetY.animateTo(0f, app.yuyue.design.YYMotion.layout()) }
                        },
                    ) { change, dragAmount ->
                        change.consume()
                        velocityTracker.addPosition(change.uptimeMillis, change.position)
                        rawPullY += dragAmount
                        val visualY = if (rawPullY < 0f) rawPullY * 0.12f else rawPullY
                        scope.launch { pullOffsetY.snapTo(visualY) }
                    }
                },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(maxOf(44.dp, YY.s(36))).clip(CircleShape).pressable(label = t("收起"), scale = 0.92f, onClick = close), contentAlignment = Alignment.Center) {
                Icon(Icons.chevronDown, t("收起"), tint = YY.c.t1, modifier = Modifier.size(YY.s(22)))
            }
        }
        SquareCover(player.summary, Modifier.align(Alignment.CenterHorizontally).padding(top = YY.s(14), bottom = YY.s(18)).width(YY.s(188)))
        // 进度冲突: another device listened further on.
        player.elsewhere?.let { e -> ElsewhereCard(player, e) }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
            Column(Modifier.weight(1f)) {
                Text(player.title(player.chapter), style = YY.font(17f, FontWeight.Bold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(listOfNotNull(player.summary.title, player.summary.authors.firstOrNull()).joinToString(" · "), style = YY.font(11f), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            // Long press: the sentence just heard, highlighted in the e-book (the lock screen's 书签 on Apple).
            Box(Modifier.offset(x = 4.dp).size(44.dp).clip(CircleShape).combinedClickable(onClickLabel = t("加书签"), onLongClickLabel = t("划下刚听到的这句"), onLongClick = { player.highlightHeard() }) { player.addBookmark() }, contentAlignment = Alignment.Center) {
                Icon(Icons.bookmarkOutline, t("加书签"), tint = YY.c.t1, modifier = Modifier.size(YY.s(18)))
            }
        }
        Scrubber(player, Modifier.padding(top = YY.s(6)))
        Times(player)
        NoticeLine(player)
        Controls(player, Modifier.padding(vertical = YY.s(14)))
        player.waitingFor?.let { w ->
            Text(player.stream.problem[player.bookID] ?: t("第 %lld 集还在下，下好就接着放", (w + 1).toLong()),
                style = YY.font(11f), color = YY.c.t3, modifier = Modifier.padding(top = 0.dp, bottom = YY.s(8)))
        }
        Tiles(player, { settingsShown = true }) { list = it }
        PairCard(library, player.summary)
        if (library.recordings.ebookFor(player.summary) == null && library.recordings.making?.audio != player.bookID && !player.hasMap) TextVersionCard(library, player.summary)
        if (player.waiting) Text(t("已暂停 · 通话结束后继续"), style = YY.font(11f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(10)))
        player.failure?.let { Text(it, style = YY.font(11f), color = YY.c.danger, modifier = Modifier.padding(top = YY.s(10))) }
    }
    list?.let { ChapterSheet(player, it, { list = it }) { list = null } }
    if (settingsShown) RecordingSheet(player) { settingsShown = false }
    Box(Modifier.fillMaxSize().windowInsetsPadding(WindowInsets.safeDrawing)) {
        player.toast?.let { toast ->
            androidx.compose.runtime.LaunchedEffect(toast.id) {
                kotlinx.coroutines.delay(if (toast.undo != null) 6200 else 3000)
                if (player.toast?.id == toast.id) player.toast = null
            }
            UndoToast(toast, { toast.undo?.invoke(); player.toast = null }, Modifier.align(Alignment.TopCenter).padding(top = YY.s(48)))
        }
        player.sleepReturn?.takeIf { player.elsewhere == null }?.let { (seconds, ms) ->
            val i = player.chapter(seconds)
            val place = app.yuyue.reader.SleepReturn.short(player.title(i)) + " " + AudiobookPlayer.clock(seconds - (player.chapters.getOrNull(i)?.start ?: 0.0))
            val whenText = app.yuyue.reader.SleepReturn(Player.Spot(0, 0), "", "", ms).whenText
            Row(
                Modifier.align(Alignment.BottomCenter).padding(horizontal = 16.dp, vertical = YY.s(12)).fillMaxWidth().yyCard(YY.s(16)).padding(start = YY.s(14), end = YY.s(8), top = YY.s(8), bottom = YY.s(8)),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
            ) {
                Text(t("%@定时从 %@ 开始，之后睡着了？", whenText, place), style = YY.font(12.5f), color = YY.c.t1, modifier = Modifier.weight(1f))
                app.yuyue.design.YYButton(t("回到那里"), kind = app.yuyue.design.ButtonKind.Main, size = app.yuyue.design.ButtonSize.Small) { player.answerSleepReturn(true) }
                app.yuyue.design.YYButton(t("不用"), size = app.yuyue.design.ButtonSize.Small) { player.answerSleepReturn(false) }
            }
        }
    }
}

/** 跳过了片头 · 点这里听, 不在书里的话 · 跳过, 朗读补上: under the times. */
@UnstableApi
@Composable
private fun NoticeLine(player: AudiobookPlayer) {
    val n = player.notice ?: return
    if (n.kind == AudiobookPlayer.Notice.Kind.Head) {
        androidx.compose.runtime.LaunchedEffect(n.extra) { kotlinx.coroutines.delay(5000); player.dismissNotice() }
    }
    Row(
        Modifier.fillMaxWidth().padding(top = YY.s(6)).clickable(enabled = n.action != null) { player.noticeAction() },
        horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(n.text, style = YY.font(11f, FontWeight.SemiBold), color = if (n.kind == AudiobookPlayer.Notice.Kind.Filling) YY.c.t2 else YY.tint.accent)
        if (n.kind == AudiobookPlayer.Notice.Kind.Extra && n.action != null) {
            Text("  |  ", style = YY.font(11f), color = YY.c.t3)
            Text(n.action, style = YY.font(11f, FontWeight.SemiBold), color = YY.tint.accent)
        }
    }
}

/** 这本录音: this recording's own settings, each taking effect at once. */
@OptIn(ExperimentalMaterial3Api::class)
@UnstableApi
@Composable
private fun RecordingSheet(player: AudiobookPlayer, dismiss: () -> Unit) {
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = YY.s(24)).fillMaxWidth().yyCard(YY.s(14)).padding(horizontal = YY.s(12))) {
            Text(t("这本录音"), style = YY.font(12f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(10), bottom = YY.s(4)))
            val s = player.settings
            SettingRow(t("片头片尾"), null) {
                YYSegments(listOf(true to t("跳过"), false to t("照放")), s.headTail, { player.set(s.copy(headTail = it)) }, small = true, modifier = Modifier.widthIn(min = YY.s(128)))
            }
            SettingRow(t("章间音乐"), t("压到 1.5 秒")) { Toggle(s.shortenChapterGaps) { player.set(s.copy(shortenChapterGaps = it)) } }
            SettingRow(t("人声清楚一点"), t("压低底噪，人声提一点")) { Toggle(s.clearerVoice) { player.set(s.copy(clearerVoice = it)) } }
            SettingRow(t("缩短停顿"), if (player.saved >= 60) t("这本已经省了 %@", length(player.saved)) else t("长停顿压短一些")) { Toggle(s.shortenPauses) { player.set(s.copy(shortenPauses = it)) } }
            SettingRow(t("没念的用朗读补上"), if (player.unreadCount > 0) t("这本有 %lld 句没念", player.unreadCount) else t("录音每句都念了")) { Toggle(s.fillUnread) { player.set(s.copy(fillUnread = it)) } }
            SettingRow(t("逐词跟读"), t("念到哪个词，哪个词下面一道线")) { Toggle(s.wordFollow) { player.set(s.copy(wordFollow = it)) } }
            if (player.streaming) {
                val context = androidx.compose.ui.platform.LocalContext.current
                val bytes = remember(player.clearsListened, player.recording) { player.stream.bytesHere(player.bookID) }
                SettingRow(t("听过的自动清掉"), t("网盘上的不动 · 这台设备上占 %@", android.text.format.Formatter.formatShortFileSize(context, bytes))) {
                    Toggle(player.clearsListened) { player.changeClearsListened(it) }
                }
            }
            if (player.hasMap) {
                var exporting by remember { mutableStateOf<String?>(null) }
                val scope = rememberCoroutineScope()
                SettingRow(t("导出带朗读时间的电子书"), exporting ?: t("一个 EPUB，别的阅读器也能跟读")) {
                    YYButton(t("导出…"), size = ButtonSize.Small, enabled = exporting == null) {
                        exporting = t("正在打包，录音大的要一会儿")
                        scope.launch {
                            val file = player.exportEpub()
                            exporting = if (file == null) t("没导出来，再试一次") else null
                            file?.let { player.share(it) }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SettingRow(name: String, detail: String?, control: @Composable () -> Unit) {
    Column {
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.fillMaxWidth().padding(vertical = YY.s(7)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            Column(Modifier.weight(1f)) {
                Text(name, style = YY.font(12f), color = YY.c.t1)
                if (detail != null) Text(detail, style = YY.font(10f), color = YY.c.t3, maxLines = 1)
            }
            control()
        }
    }
}

@Composable
private fun Toggle(on: Boolean, set: (Boolean) -> Unit) {
    androidx.compose.material3.Switch(
        on, set,
        colors = androidx.compose.material3.SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
    )
}

/** 书架上有同一本电子书: 合成 lays the recording against it; while it runs, how far. */
@UnstableApi
@Composable
private fun PairCard(library: Library, audio: uniffi.yuyue_core.BookSummary) {
    val r = library.recordings
    val making = r.making?.takeIf { it.audio == audio.id }
    val ebook = making?.let { m -> library.books.firstOrNull { it.id == m.ebook } } ?: r.ebookFor(audio) ?: return
    Row(
        Modifier.padding(top = YY.s(14)).fillMaxWidth().yyCard(YY.s(14)).padding(horizontal = YY.s(12), vertical = YY.s(10)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        BookCover(ebook, Modifier.width(YY.s(24)))
        Column(Modifier.weight(1f)) {
            Text(t("书架上有《%@》电子书", ebook.title), style = YY.font(11f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val line = when {
                making == null -> t("合成以后听到哪句，书页就走到哪句")
                making.problem != null -> making.problem!!
                making.downloaded != null -> t("下载对齐模型 %lld%%", ((making.downloaded ?: 0.0) * 100).toInt())
                else -> t("合成中 %lld%%", (making.fraction * 100).toInt())
            }
            Text(line, style = YY.font(10f, tnum = true), color = if (making?.problem != null) YY.c.danger else YY.c.t3, maxLines = 2)
        }
        if (making == null || making.problem != null) {
            Box(Modifier.height(YY.s(26)).clip(CircleShape).background(YY.c.t1).clickable { r.make(audio, ebook) }.padding(horizontal = YY.s(12)), contentAlignment = Alignment.Center) {
                Text(if (making?.problem != null) t("再试一次") else t("合成"), style = YY.font(11f, FontWeight.SemiBold), color = YY.c.bg)
            }
        }
    }
}

/** 没有电子书: the recording made into a book of its own from what's heard, on this phone —
 *  or paired by hand with an e-book on the shelf when the file name didn't match its title. */
@OptIn(ExperimentalMaterial3Api::class)
@UnstableApi
@Composable
private fun TextVersionCard(library: Library, audio: uniffi.yuyue_core.BookSummary) {
    val job = library.recordings.textJobs[audio.id]
    val ebooks = remember(library.books) {
        library.books.filter {
            it.available &&
                it.format != uniffi.yuyue_core.BookFormat.PDF &&
                it.format != uniffi.yuyue_core.BookFormat.AUDIO &&
                it.format != uniffi.yuyue_core.BookFormat.PODCAST
        }
    }
    var pickingBook by remember { mutableStateOf(false) }
    Column(Modifier.padding(top = YY.s(14)).fillMaxWidth().yyCard(YY.s(14)).padding(horizontal = YY.s(12), vertical = YY.s(10))) {
        Text(t("这本录音"), style = YY.font(12f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(bottom = YY.s(4)))
        if (job == null && ebooks.isNotEmpty()) {
            Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
            Row(Modifier.padding(vertical = YY.s(7)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                Column(Modifier.weight(1f)) {
                    Text(t("配上书架上的电子书"), style = YY.font(12f), color = YY.c.t1)
                    Text(t("合成以后听到哪句，书页就走到哪句"), style = YY.font(10f), color = YY.c.t3, maxLines = 2)
                }
                YYButton(t("选书…"), size = ButtonSize.Small) { pickingBook = true }
            }
        }
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.padding(top = YY.s(7)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            Column(Modifier.weight(1f)) {
                Text(if (job == null || job.problem != null) t("没有电子书") else t("正在转成文字"), style = YY.font(12f), color = YY.c.t1)
                val detail = when {
                    job == null -> t("把录音转成文字，能翻能搜能划线")
                    job.problem != null -> job.problem!!
                    job.downloaded != null -> t("下载听写模型 %lld%%", ((job.downloaded ?: 0.0) * 100).toInt())
                    else -> t("第 %lld / %lld 集", (job.file + 1).toLong(), job.files.toLong()) + (job.left?.let { t(" · 还要约 %@", length(it)) } ?: "")
                }
                Text(detail, style = YY.font(10f, tnum = true), color = if (job?.problem != null) YY.c.danger else YY.c.t3, maxLines = 2)
                if (job != null && job.problem == null) {
                    androidx.compose.material3.LinearProgressIndicator(
                        progress = { (job.downloaded ?: job.fraction).toFloat() },
                        modifier = Modifier.padding(top = YY.s(5)).fillMaxWidth(),
                        color = YY.tint.fill, trackColor = YY.c.inset,
                    )
                }
            }
            if (job == null || job.problem != null) {
                Box(Modifier.height(YY.s(26)).clip(CircleShape).background(YY.c.t1).clickable { library.recordings.makeTextVersion(audio) }.padding(horizontal = YY.s(12)), contentAlignment = Alignment.Center) {
                    Text(if (job?.problem != null) t("再试一次") else t("转成文字"), style = YY.font(11f, FontWeight.SemiBold), color = YY.c.bg)
                }
            }
        }
    }
    if (pickingBook) {
        EbookPickerSheet(ebooks, dismiss = { pickingBook = false }) { ebook ->
            pickingBook = false
            library.recordings.make(audio, ebook)
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EbookPickerSheet(
    books: List<uniffi.yuyue_core.BookSummary>,
    dismiss: () -> Unit,
    pick: (uniffi.yuyue_core.BookSummary) -> Unit,
) {
    var query by remember { mutableStateOf("") }
    val filtered = remember(books, query) {
        val q = query.trim()
        if (q.isEmpty()) books else books.filter { b ->
            b.title.contains(q, ignoreCase = true) || b.authors.any { it.contains(q, ignoreCase = true) }
        }
    }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(), containerColor = YY.c.bg) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            Text(t("选一本电子书"), style = YY.font(15f, FontWeight.Bold), color = YY.c.t1)
            Text(t("听的时候书页跟着走"), style = YY.font(11f), color = YY.c.t3, modifier = Modifier.padding(top = 2.dp, bottom = 10.dp))
            Row(
                Modifier.fillMaxWidth().height(YY.s(36)).background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(horizontal = YY.s(10)),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
            ) {
                Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
                androidx.compose.foundation.text.BasicTextField(
                    value = query,
                    onValueChange = { query = it },
                    singleLine = true,
                    textStyle = YY.font(12f).copy(color = YY.c.t1),
                    decorationBox = { inner ->
                        if (query.isEmpty()) Text(t("书名或作者"), style = YY.font(12f), color = YY.c.t3)
                        inner()
                    },
                    modifier = Modifier.weight(1f),
                )
            }
            Spacer(Modifier.height(10.dp))
            LazyColumn(Modifier.fillMaxWidth().yyCard()) {
                itemsIndexed(filtered, key = { _, b -> b.id }) { i, b ->
                    if (i > 0) Box(Modifier.fillMaxWidth().padding(horizontal = YY.s(12)).height(0.5.dp).background(YY.c.line))
                    Row(
                        Modifier.fillMaxWidth().clickable { pick(b) }.padding(horizontal = YY.s(12), vertical = YY.s(8)),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                    ) {
                        BookCover(b, Modifier.width(YY.s(24)))
                        Column(Modifier.weight(1f)) {
                            Text(b.title, style = YY.font(12f, FontWeight.Medium), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            if (b.authors.isNotEmpty()) {
                                Text(b.authors.joinToString(t("、")), style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                }
            }
        }
    }
}

/** The chapter: what's been heard, the 书签 in it as small marks, and a knob to drag. */
@UnstableApi
@Composable
private fun Scrubber(player: AudiobookPlayer, modifier: Modifier) {
    var dragging by remember { mutableStateOf<Double?>(null) }
    var settledSeek by remember(player.chapter) { mutableStateOf<Pair<Double, Long>?>(null) }
    val (at, of) = player.inChapter
    val start = player.chapters.getOrNull(player.chapter)?.start ?: 0.0
    val pending = settledSeek?.takeIf { (target, ts) ->
        kotlin.math.abs(at - target) > 0.8 && System.currentTimeMillis() - ts < 600L
    }
    if (settledSeek != null && pending == null) settledSeek = null
    val shown = dragging ?: pending?.first ?: at
    BoxWithConstraints(modifier.fillMaxWidth().height(44.dp), contentAlignment = Alignment.Center) {
        val w = constraints.maxWidth.toFloat()
        val full = maxWidth
        fun frac(t: Double) = if (of > 0) (t / of).coerceIn(0.0, 1.0).toFloat() else 0f
        Box(
            Modifier.fillMaxSize()
                .pointerInput(of, start) {
                    detectTapGestures { o ->
                        val target = of * (o.x / w).coerceIn(0f, 1f)
                        settledSeek = target to System.currentTimeMillis()
                        player.seekTo(start + target)
                    }
                }
                .pointerInput(of, start) {
                    detectHorizontalDragGestures(
                        onDragStart = { o -> dragging = of * (o.x / w).coerceIn(0f, 1f) },
                        onDragEnd = {
                            dragging?.let { target ->
                                settledSeek = target to System.currentTimeMillis()
                                player.seekTo(start + target)
                            }
                            dragging = null
                        },
                        onDragCancel = { dragging = null },
                    ) { change, _ ->
                        change.consume()
                        dragging = of * (change.position.x / w).coerceIn(0f, 1f)
                    }
                },
            contentAlignment = Alignment.CenterStart,
        ) {
            if (player.streaming) {
                // 边下边听: past what's here, still on the share (dashed); here, pale; heard, ink.
                val (down, ticks) = player.hereInChapter
                val line = YY.c.line
                androidx.compose.foundation.Canvas(Modifier.fillMaxWidth().height(4.dp)) {
                    drawLine(line, androidx.compose.ui.geometry.Offset(0f, size.height / 2), androidx.compose.ui.geometry.Offset(size.width, size.height / 2), 2.dp.toPx(),
                        cap = androidx.compose.ui.graphics.StrokeCap.Round, pathEffect = androidx.compose.ui.graphics.PathEffect.dashPathEffect(floatArrayOf(3.dp.toPx(), 3.dp.toPx())))
                }
                Box(Modifier.width(full * frac(down)).height(4.dp).clip(CircleShape).background(YY.c.inset))
                Box(Modifier.width(full * frac(shown)).height(4.dp).clip(CircleShape).background(YY.c.t1))
                for (t in ticks) Box(Modifier.offset(x = full * frac(t)).size(1.dp, 10.dp).background(YY.c.line))
            } else {
                Box(Modifier.fillMaxWidth().height(4.dp).clip(CircleShape).background(YY.c.inset))
                Box(Modifier.width(full * frac(shown)).height(4.dp).clip(CircleShape).background(YY.c.t1))
            }
            for (b in player.bookmarks) {
                val m = player.seconds(b) - start
                if (m in 0.0..of) Box(Modifier.offset(x = full * frac(m) - 1.dp).size(2.dp, 6.dp).background(YY.c.card))
            }
            Box(Modifier.offset(x = full * frac(shown) - 6.dp).size(12.dp).clip(CircleShape).background(YY.c.t1))
        }
    }
}

@UnstableApi
@Composable
private fun Times(player: AudiobookPlayer) {
    val (at, of) = player.inChapter
    val left = player.remaining
    val tnum = YY.font(10f).copy(fontFeatureSettings = "tnum")
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(AudiobookPlayer.clock(at), style = tnum.copy(fontWeight = FontWeight.Medium), color = YY.c.t2)
        Text(
            t("本集还剩 %@ · 全书还剩 %@", AudiobookPlayer.clock(of - at), length(left)) + downloaded(player),
            style = tnum, color = YY.c.t3, maxLines = 1, modifier = Modifier.weight(1f), textAlign = androidx.compose.ui.text.style.TextAlign.Center,
        )
        Text(AudiobookPlayer.clock(of), style = tnum.copy(fontWeight = FontWeight.Medium), color = YY.c.t2)
    }
}

/** 边下边听: how many files are here, while some aren't. */
@UnstableApi
private fun downloaded(player: AudiobookPlayer): String {
    if (!player.streaming) return ""
    val (have, total) = player.filesHere
    return if (have < total) t(" · 已下 %lld / %lld 集", have.toLong(), total.toLong()) else ""
}

/** `6 小时 12 分`, `38 分钟`. */
fun length(seconds: Double): String {
    val m = (seconds / 60).toInt()
    return if (m >= 60) (if (m % 60 > 0) t("%lld 小时 %lld 分", m / 60, m % 60) else t("%lld 小时", m / 60)) else t("%lld 分钟", maxOf(1, m))
}

private val rates = listOf(0.75f, 1f, 1.1f, 1.2f, 1.25f, 1.5f, 1.75f, 2f, 2.5f, 3f)

private fun rateName(r: Float) = (if (r == r.toInt().toFloat()) r.toInt().toString() else r.toString().trimEnd('0')) + "×"

@UnstableApi
@Composable
private fun Controls(player: AudiobookPlayer, modifier: Modifier) {
    var rateOpen by remember { mutableStateOf(false) }
    Row(modifier.fillMaxWidth().padding(horizontal = YY.s(4)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
        Box {
            Box(Modifier.size(maxOf(44.dp, YY.s(40))).clip(CircleShape).clickable(onClickLabel = t("倍速")) { rateOpen = true }, contentAlignment = Alignment.Center) {
                Text(rateName(player.rate), style = YY.font(12.5f, FontWeight.SemiBold, tnum = true), color = YY.c.t1)
            }
            DropdownMenu(rateOpen, { rateOpen = false }) {
                for (r in rates) DropdownMenuItem(
                    text = { Text(rateName(r)) },
                    trailingIcon = { if (kotlin.math.abs(player.rate - r) < 0.01f) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(14))) },
                    onClick = { player.changeRate(r); rateOpen = false },
                )
            }
        }
        RoundIcon(Icons.back15, t("后退 15 秒")) { player.skip(-AudiobookPlayer.BACK) }
        val label = if (player.isPlaying) t("暂停") else t("播放")
        Box(contentAlignment = Alignment.Center) {
            Box(Modifier.size(YY.s(60)).clip(CircleShape).background(YY.tint.black).pressable(label = label, scale = 0.94f) { player.toggle() }, contentAlignment = Alignment.Center) {
                androidx.compose.animation.Crossfade(player.isPlaying, animationSpec = app.yuyue.design.YYMotion.quick(), label = "play") { on -> Icon(if (on) Icons.pause else Icons.play, label, tint = YY.tint.onBlack, modifier = Modifier.size(YY.s(24))) }
            }
            // 边下边听: waiting for a file, a thin ring round the button fills as it comes.
            player.waitingFor?.let { w ->
                val coming = player.stream.fetching[player.bookID]
                val fraction = if (coming?.first == w) coming.second else 0f
                val line = YY.c.line
                val ink = YY.c.t1
                androidx.compose.foundation.Canvas(Modifier.size(YY.s(68))) {
                    val sw = 1.6.dp.toPx()
                    drawCircle(line, radius = size.width / 2 - sw / 2, style = androidx.compose.ui.graphics.drawscope.Stroke(sw))
                    drawArc(ink, -90f, 360f * fraction.coerceIn(0.02f, 1f), false, androidx.compose.ui.geometry.Offset(sw / 2, sw / 2),
                        androidx.compose.ui.geometry.Size(size.width - sw, size.height - sw), style = androidx.compose.ui.graphics.drawscope.Stroke(sw, cap = androidx.compose.ui.graphics.StrokeCap.Round))
                }
            }
        }
        RoundIcon(Icons.ahead30, t("前进 30 秒")) { player.skip(AudiobookPlayer.AHEAD) }
        SleepButton(player)
    }
}

@Composable
private fun RoundIcon(icon: androidx.compose.ui.graphics.vector.ImageVector, label: String, onClick: () -> Unit) {
    Box(Modifier.size(maxOf(44.dp, YY.s(40))).clip(CircleShape).clickable(onClickLabel = label, onClick = onClick), contentAlignment = Alignment.Center) {
        Icon(icon, label, tint = YY.c.t1, modifier = Modifier.size(YY.s(24)))
    }
}

@UnstableApi
@Composable
private fun SleepButton(player: AudiobookPlayer, tile: Boolean = false) {
    var open by remember { mutableStateOf(false) }
    val sleepIcon = if (player.sleep == Player.Sleep.Off) Icons.sleep else Icons.sleepFill
    Box {
        if (tile) {
            TileFace(sleepIcon, t("定时"), sleepName(player.sleep)) { open = true }
        } else {
            Box(Modifier.size(maxOf(44.dp, YY.s(40))).clip(CircleShape).clickable(onClickLabel = t("定时")) { open = true }, contentAlignment = Alignment.Center) {
                Icon(sleepIcon, t("定时"), tint = if (player.sleep == Player.Sleep.Off) YY.c.t1 else YY.tint.accent, modifier = Modifier.size(YY.s(22)))
            }
        }
        DropdownMenu(open, { open = false }) {
            if (player.hasMap) DropdownMenuItem(
                text = { Text(t("念完这句")) },
                trailingIcon = { if (player.sleep == Player.Sleep.Sentence) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(14))) },
                onClick = { player.sleepAtSentenceEnd(); open = false },
            )
            DropdownMenuItem(
                text = { Text(t("本集完")) },
                trailingIcon = { if (player.sleep == Player.Sleep.Chapter) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(14))) },
                onClick = { player.changeSleep(Player.Sleep.Chapter); open = false },
            )
            for (m in listOf(15, 30, 45, 60, 90)) {
                val active = (player.sleep as? Player.Sleep.Until)?.minutes == m
                DropdownMenuItem(
                    text = { Text(t("%lld 分钟", m)) },
                    trailingIcon = { if (active) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(14))) },
                    onClick = { player.changeSleep(Player.Sleep.Until(System.currentTimeMillis() + m * 60_000L, m)); open = false },
                )
            }
            if (player.sleep != Player.Sleep.Off) DropdownMenuItem({ Text(t("关闭定时"), color = YY.c.danger) }, { player.changeSleep(Player.Sleep.Off); open = false })
        }
    }
}

private fun sleepName(s: Player.Sleep) = when (s) {
    Player.Sleep.Off -> t("关")
    Player.Sleep.Chapter -> t("本集完")
    Player.Sleep.Sentence -> t("念完这句")
    is Player.Sleep.Until -> t("%lld 分钟", s.minutes)
}

@UnstableApi
@Composable
private fun Tiles(player: AudiobookPlayer, settings: () -> Unit, open: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
        Box(Modifier.weight(1f)) { TileFace(Icons.list, t("章节"), t("%lld 集", player.chapters.size)) { open(0) } }
        Box(Modifier.weight(1f)) { TileFace(Icons.bookmarkOutline, t("书签"), t("%lld 个书签", player.bookmarks.size)) { open(1) } }
        Box(Modifier.weight(1f)) { SleepButton(player, tile = true) }
        if (player.hasMap) Box(Modifier.weight(1f)) {
            TileFace(Icons.general, t("这本录音"), if (player.settings.headTail) t("跳过片头") else t("照放片头"), settings)
        }
    }
}

@Composable
private fun TileFace(icon: androidx.compose.ui.graphics.vector.ImageVector, name: String, detail: String, onClick: () -> Unit) {
    Column(
        Modifier.fillMaxWidth().yyCard(YY.s(14)).clickable(onClick = onClick).padding(vertical = YY.s(9), horizontal = YY.s(4)),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(YY.s(3)),
    ) {
        Icon(icon, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(16)))
        Text(name, style = YY.font(10f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(detail, style = YY.font(10f, tnum = true), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** 边下边听: a chapter still on the share — a small ring in place of its length: an arrow to
 *  fetch it next, filling while it comes (tap to pause), paused (tap to go on). */
@UnstableApi
@Composable
private fun ChapterShareRing(player: AudiobookPlayer, chapter: Int) {
    val stream = player.stream
    val id = player.bookID
    val file = player.fileOf(chapter)
    val coming = stream.fetching[id]
    val label = t("第 %lld 集还在网盘上", (file + 1).toLong())
    when {
        coming != null && coming.first == file -> app.yuyue.design.ShareRing(app.yuyue.design.ShareRingState.Coming(coming.second), 16.dp, label) { stream.pause(id) }
        stream.paused[id] == true -> app.yuyue.design.ShareRing(app.yuyue.design.ShareRingState.Paused(0f), 16.dp, label) { stream.carryOn(id) }
        else -> app.yuyue.design.ShareRing(app.yuyue.design.ShareRingState.Get, 16.dp, label) { stream.want(id, file) }
    }
}

/** 章节 and 书签: the one being heard has the moving bars, the ones heard are grey. */
@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.ExperimentalFoundationApi::class)
@UnstableApi
@Composable
private fun ChapterSheet(player: AudiobookPlayer, page: Int, select: (Int) -> Unit, dismiss: () -> Unit) {
    // 长按改名: the chapter being named, and the name typed.
    var renaming by remember { mutableStateOf<Int?>(null) }
    var newName by remember { mutableStateOf("") }
    renaming?.let { i ->
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { renaming = null },
            containerColor = YY.c.card,
            title = { Text(t("给这一集起个名字"), color = YY.c.t1) },
            text = {
                Column {
                    androidx.compose.material3.OutlinedTextField(newName, { newName = it }, singleLine = true, placeholder = { Text(t("第 %lld 集", i + 1)) })
                    Text(t("留空就叫“第几集”"), style = YY.font(10f), color = YY.c.t3, modifier = Modifier.padding(top = 6.dp))
                }
            },
            confirmButton = { androidx.compose.material3.TextButton({ player.renameChapter(i, newName); renaming = null }) { Text(t("好"), color = YY.tint.accent) } },
            dismissButton = { androidx.compose.material3.TextButton({ renaming = null }) { Text(t("取消"), color = YY.c.t2) } },
        )
    }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(), containerColor = YY.c.bg) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
            YYSegments(listOf(0 to t("章节 %lld", player.chapters.size), 1 to t("书签 %lld", player.bookmarks.size)), page, select)
            Spacer(Modifier.height(10.dp))
            val now = player.chapter
            LazyColumn(Modifier.fillMaxWidth().yyCard()) {
                if (page == 0) {
                    itemsIndexed(player.chapters) { i, c ->
                        Row(
                            Modifier.fillMaxWidth()
                                .combinedClickable(onLongClickLabel = t("改名"), onLongClick = { renaming = i; newName = c.title ?: "" }) { player.goToChapter(i); dismiss() }
                                .padding(horizontal = YY.s(14)).height(YY.s(40)),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            if (i == now) {
                                Icon(Icons.waveform, null, tint = YY.c.t1, modifier = Modifier.width(YY.s(20)).size(YY.s(13)))
                            } else {
                                Text("${i + 1}", style = YY.font(10f).copy(fontFeatureSettings = "tnum"), color = YY.c.t3, modifier = Modifier.width(YY.s(20)))
                            }
                            val off = player.offDevice(i) && i != now
                            Text(
                                player.title(i), style = YY.font(13f, if (i == now) FontWeight.SemiBold else FontWeight.Normal),
                                color = if (i < now || off) YY.c.t3 else YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f),
                            )
                            if (off) ChapterShareRing(player, i)
                            else Text(
                                if (i == now) t("还剩 %@", AudiobookPlayer.clock(player.inChapter.second - player.inChapter.first)) else AudiobookPlayer.clock(c.seconds),
                                style = YY.font(11f).copy(fontFeatureSettings = "tnum"), color = YY.c.t3,
                            )
                        }
                    }
                } else {
                    if (player.bookmarks.isEmpty()) item {
                        Text(t("听到想记住的地方，点书签按钮记一下。"), style = YY.font(11f), color = YY.c.t3, modifier = Modifier.padding(YY.s(14)))
                    }
                    items(player.bookmarks, key = { it.id }) { b ->
                        Row(
                            Modifier.animateItem(fadeInSpec = app.yuyue.design.YYMotion.standard(), placementSpec = app.yuyue.design.YYMotion.place(), fadeOutSpec = app.yuyue.design.YYMotion.exit()).fillMaxWidth().clickable { player.seekTo(player.seconds(b)); dismiss() }.padding(start = YY.s(14), end = YY.s(6)).height(maxOf(44.dp, YY.s(40))),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(player.title(b.section.toInt()), style = YY.font(13f), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                            Text(AudiobookPlayer.clock(b.sentence.toDouble() / 1000), style = YY.font(11f, tnum = true), color = YY.c.t3)
                            Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("删除")) { player.deleteBookmark(b) }, contentAlignment = Alignment.Center) {
                                Icon(Icons.close, t("删除"), tint = YY.c.t3, modifier = Modifier.size(YY.s(12)))
                            }
                        }
                    }
                }
            }
        }
    }
}

/** 进度冲突 on a recording: which device listened further on, how long ago, where — as the
 *  book page's card on Apple, 听到 for 读到. */
@Composable
private fun ElsewhereCard(player: AudiobookPlayer, e: uniffi.yuyue_core.Progress) {
    val at = player.secondsOf(e)
    val i = player.chapter(at)
    val place = app.yuyue.reader.SleepReturn.short(player.title(i)) + " · " + AudiobookPlayer.clock(at - (player.chapters.getOrNull(i)?.start ?: 0.0))
    val ago = app.yuyue.reader.SleepReturn(Player.Spot(0, 0), "", "", e.at).whenText
    Column(Modifier.padding(bottom = YY.s(16)).fillMaxWidth().yyCard(YY.s(16)).padding(horizontal = YY.s(16), vertical = YY.s(12))) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(t("%@ 上听到了更后面", e.deviceName ?: t("另一台设备")), style = YY.font(12f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(ago, style = YY.font(10.5f), color = YY.c.t3)
        }
        Row(Modifier.padding(top = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
            Text(place, style = YY.font(10.5f), color = YY.c.t3, maxLines = 1, modifier = Modifier.weight(1f))
            app.yuyue.design.YYButton(t("留在这里"), size = app.yuyue.design.ButtonSize.Small) { player.elsewhere = null }
            app.yuyue.design.YYButton(t("跳过去"), kind = app.yuyue.design.ButtonKind.Main, size = app.yuyue.design.ButtonSize.Small) { player.jumpElsewhere() }
        }
    }
}
