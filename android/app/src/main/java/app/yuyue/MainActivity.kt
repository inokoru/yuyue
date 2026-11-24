package app.yuyue

import androidx.compose.foundation.layout.statusBars
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import kotlin.math.roundToInt
import androidx.compose.foundation.clickable
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.asPaddingValues
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.animation.togetherWith
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.IntentCompat
import app.yuyue.design.pressScale
import app.yuyue.design.pressable
import app.yuyue.design.Icons
import app.yuyue.design.TabIcons
import app.yuyue.design.YY
import app.yuyue.design.YuyueTheme
import app.yuyue.design.Prefs
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.reader.ReaderScreen
import app.yuyue.shelf.ShelfScreen
import dev.chrisbanes.haze.HazeInput
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.blur.HazeBlurStyle
import dev.chrisbanes.haze.blur.HazeColorEffect
import dev.chrisbanes.haze.blur.hazeBlur
import dev.chrisbanes.haze.hazeSource
import dev.chrisbanes.haze.rememberHazeState
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val library: Library get() = (application as YuyueApp).library

    override fun onCreate(savedInstanceState: Bundle?) {
        AppLanguage.apply(this)
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.auto(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.auto(android.graphics.Color.TRANSPARENT, android.graphics.Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        // Made again for a new language (通用 › 语言): the words, and the core's, in it.
        if (AppLanguage.chinese(this) != L10n.chinese) {
            L10n.load(this)
            uniffi.yuyue_core.setUiLanguage(if (L10n.chinese) "zh-Hans" else "en")
        }
        if (savedInstanceState == null) handle(intent)
        setContent {
            YuyueTheme {
                androidx.compose.foundation.layout.Box {
                    Root(library)
                    app.yuyue.design.InkSweepOnce()
                }
            }
        }
    }

    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        AppLanguage.apply(this)
    }

    /** 音量键翻页: volume down turns on, up turns back; held down, the pages keep turning. */
    override fun onKeyDown(keyCode: Int, event: android.view.KeyEvent): Boolean {
        val forward = when (keyCode) {
            android.view.KeyEvent.KEYCODE_VOLUME_DOWN -> true
            android.view.KeyEvent.KEYCODE_VOLUME_UP -> false
            else -> return super.onKeyDown(keyCode, event)
        }
        return app.yuyue.reader.VolumeKeys.handler?.invoke(forward) == true || super.onKeyDown(keyCode, event)
    }

    override fun onKeyUp(keyCode: Int, event: android.view.KeyEvent): Boolean {
        if ((keyCode == android.view.KeyEvent.KEYCODE_VOLUME_DOWN || keyCode == android.view.KeyEvent.KEYCODE_VOLUME_UP) && app.yuyue.reader.VolumeKeys.handler != null &&
            Prefs.bool("reader.volumeKeys", true)) return true
        return super.onKeyUp(keyCode, event)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    /** 用语阅打开: books opened or shared from other apps go into the library. */
    private fun handle(intent: Intent?) {
        intent ?: return
        val uris = mutableListOf<Uri>()
        when (intent.action) {
            Intent.ACTION_VIEW -> intent.data?.let { uris += it }
            Intent.ACTION_SEND -> IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let { uris += it }
            Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java)?.let { uris += it }
        }
        if (uris.isNotEmpty()) library.importUris(uris)
        // Debug builds: `-e listen <书 id>,<章>,<句>` reads the book aloud from there.
        if (BuildConfig.DEBUG) intent.getStringExtra("listen")?.split(',')?.takeIf { it.size == 3 }?.let { (b, sec, n) ->
            runCatching { library.core.saveProgress(b, uniffi.yuyue_core.Position(sec.toUInt(), n.toUInt())) }
            // `-e bubbles 1`: in 气泡模式.
            intent.getStringExtra("bubbles")?.let { on -> runCatching { library.core.setBookBubbles(b, on == "1") } }
            library.books.firstOrNull { it.id == b }?.let { library.open(it, Library.OpenIntent.Listen) }
        }
        // Debug builds: `-e importPath <file in the app's own folder>` brings it in as 导入 would.
        // Debug builds: `-e updateBase http://10.0.2.2:8000/` checks for updates in a folder of one's own.
        if (BuildConfig.DEBUG) intent.getStringExtra("updateBase")?.let { app.yuyue.design.Prefs.set(app.yuyue.update.Updates.BASE_KEY, it) }
        if (BuildConfig.DEBUG) intent.getStringExtra("sfxURL")?.let { app.yuyue.design.Prefs.set(app.yuyue.speech.SfxMaker.KEY, it) }
        if (BuildConfig.DEBUG) intent.getStringExtra("asrDir")?.let { app.yuyue.speech.Listener.install(library.dataDir, java.io.File(it)) }
        if (BuildConfig.DEBUG) intent.getStringExtra("soundsPath")?.let { library.dramaSynth.debugSounds = java.io.File(it) }
        if (BuildConfig.DEBUG) intent.getStringExtra("speakerPath")?.let { app.yuyue.speech.SpeakerPrint.install(library.dataDir, java.io.File(it)) }
        if (BuildConfig.DEBUG) intent.getStringExtra("liveVoicePath")?.let { app.yuyue.speech.LiveVoiceModel.installDebug(library.core, library.dataDir, java.io.File(it)) }
        if (BuildConfig.DEBUG) intent.getStringExtra("voicePackPath")?.let { app.yuyue.speech.VoicePack.installDebug(library.core, library.dataDir, java.io.File(it)) }
        if (BuildConfig.DEBUG) intent.getStringExtra("importPath")?.let { library.importUris(listOf(Uri.fromFile(java.io.File(it)))) }
        // Debug builds: `-e dramaSynth <book id>,<section>` makes that section's 广播剧 clips with the
        // system voices, then writes it mixed to the app's cache as drama-<section>.wav.
        if (BuildConfig.DEBUG) intent.getStringExtra("dramaSynth")?.split(',')?.takeIf { it.size == 2 }?.let { (b, n) ->
            val lang = runCatching { library.core.book(b).summary.language }.getOrDefault("zh-Hans")
            library.dramaSynth.start(b, lang, n.toUInt(), null) {
                library.scope.launch {
                    val wav = library.dramaSynth.mixSection(b, n.toUInt())
                    val out = java.io.File(cacheDir, "drama-$n.wav")
                    wav?.let { out.writeBytes(it) }
                    android.util.Log.d("DramaSynth", "section $n mixed → ${out.path} (${wav?.size ?: 0} bytes)")
                }
            }
        }
        // Debug builds: `-e relayout <有声书 id>,<电子书 id>` lays out again what was heard (the
        // transcript kept), then the new lay-out's work goes on.
        if (BuildConfig.DEBUG) intent.getStringExtra("relayout")?.split(',')?.takeIf { it.size == 2 }?.let { (a, e) ->
            runCatching { library.core.audiobookPair(a, e) }
            library.recordings.workOn(a, null)
        }
        // Debug builds: `-e pair <有声书 id>,<电子书 id>` lays the one against the other (合成).
        if (BuildConfig.DEBUG) intent.getStringExtra("pair")?.split(',')?.takeIf { it.size == 2 }?.let { (a, e) ->
            val audio = library.books.firstOrNull { it.id == a }
            val ebook = library.books.firstOrNull { it.id == e }
            if (audio != null && ebook != null) {
                library.recordings.make(audio, ebook)
                library.presented = audio.id
            }
        }
    }
}

/** The tabs: 书架 · 笔记 · 我的, and 搜索. */
enum class Tab { Shelf, Notes, Me, Search }

/** A page pushed over a tab. */
sealed interface Page {
    data object Wifi : Page
    data object Remote : Page
    data object Watched : Page
    data object Feedback : Page
    data class Record(val book: String) : Page
}

/** The phone, as on iPhone: four tabs along the bottom (on Android flush with the bottom edge),
 *  and a book over all of them, full screen. */
@Composable
fun Root(library: Library) {
    var tab by rememberSaveable { mutableStateOf(Tab.Shelf) }
    var notesPop by remember { androidx.compose.runtime.mutableIntStateOf(0) }
    var mePop by remember { androidx.compose.runtime.mutableIntStateOf(0) }
    val pages = remember { mutableStateListOf<Page>() }
    // 书架 tapped again, and where the shelf is, for the first tab to wear.
    val chrome = remember { app.yuyue.shelf.ShelfChrome() }
    var lastBook by rememberPref("phone.lastBook", "")
    var reopened by rememberSaveable { mutableStateOf(false) }

    // 没网时留下的反馈，打开 App 时再试一次.
    LaunchedEffect(Unit) { app.yuyue.feedback.Feedback.flush() }
    // 检查更新: once a day at most (the APK from GitHub only).
    val context = androidx.compose.ui.platform.LocalContext.current
    LaunchedEffect(Unit) { app.yuyue.update.Updates.checkNow(context) }
    // 通用 › 打开语阅时接着读上次那本: 上次停在书页上，打开 App 就直接回到那一页.
    LaunchedEffect(Unit) {
        if (!reopened) {
            reopened = true
            if (Prefs.bool(app.yuyue.me.General.REOPEN_LAST, false) && lastBook.isNotEmpty() && library.books.any { it.id == lastBook && it.available }) library.presented = lastBook
        }
    }

    val haze = rememberHazeState()
    val density = LocalDensity.current
    var barHeight by remember { mutableStateOf(0.dp) }
    Box(Modifier.fillMaxSize().background(YY.c.bg)) {
        // The page runs under the tab bar (and 播放条), which blurs what scrolls beneath it.
        CompositionLocalProvider(LocalTabBarHeight provides barHeight) {
            Box(Modifier.fillMaxSize().hazeSource(haze)) {
                // A page going deeper comes a short way from the right; back, from the left. Switching
                // tabs snaps immediately with zero transition even when clearing pushed sub-pages.
                val reducePages = app.yuyue.design.YYMotion.isReduceMotion()
                androidx.compose.animation.AnimatedContent(
                    Triple(tab, pages.lastOrNull(), pages.size),
                    transitionSpec = {
                        if (targetState.first != initialState.first) {
                            androidx.compose.animation.EnterTransition.None togetherWith androidx.compose.animation.ExitTransition.None
                        } else {
                            app.yuyue.design.YYMotion.push(targetState.third > initialState.third, reducePages)
                        }
                    },
                    label = "page",
                ) { (currentTab, page, _) ->
                when (page) {
                    // Instant switch on core high-frequency bottom navigation.
                    null -> when (currentTab) {
                        Tab.Shelf -> ShelfScreen(library, chrome, openWifi = { pages += Page.Wifi }, openRemote = { pages += Page.Remote }, openWatched = { pages += Page.Watched }, openFeedback = { pages += Page.Feedback }, openRecord = { pages += Page.Record(it) })
                        Tab.Notes -> app.yuyue.notes.NotesScreen(library, notesPop)
                        Tab.Me -> app.yuyue.me.MeScreen(library, mePop)
                        Tab.Search -> app.yuyue.search.SearchScreen(library)
                    }
                    Page.Wifi -> app.yuyue.me.SubPage(t("Wi‑Fi 传书"), { pages.removeAt(pages.lastIndex) }) { app.yuyue.shelf.WifiPage(library) }
                    Page.Remote -> app.yuyue.shelf.RemotePage(library) { pages.removeAt(pages.lastIndex) }
                    Page.Watched -> app.yuyue.me.SubPage(t("监听一个文件夹"), { pages.removeAt(pages.lastIndex) }) { app.yuyue.shelf.WatchedPage(library) }
                    Page.Feedback -> app.yuyue.me.SubPage(t("反馈"), { pages.removeAt(pages.lastIndex) }) { app.yuyue.feedback.FeedbackPage { pages.removeAt(pages.lastIndex) } }
                    is Page.Record -> app.yuyue.shelf.BookRecordPage(library, page.book) { pages.removeAt(pages.lastIndex) }
                }
                }
            }
        }
        val onPodcastShelf = tab == Tab.Shelf && pages.isEmpty() && chrome.place == app.yuyue.shelf.ShelfPlace.Podcasts
        Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth().onSizeChanged { barHeight = with(density) { it.height.toDp() } }) {
            TabBar(tab, chrome, haze, { NowPlayingBar(library, onPodcastShelf) }) {
                // 书架 tapped while already there: the shelf's places stand up over it.
                chrome.switching = it == Tab.Shelf && tab == Tab.Shelf && pages.isEmpty() && !chrome.switching
                if (it == Tab.Notes && tab == Tab.Notes && pages.isEmpty()) notesPop++
                if (it == Tab.Me && tab == Tab.Me && pages.isEmpty()) mePop++
                tab = it; pages.clear()
            }
        }
        library.toast?.let {
            app.yuyue.shelf.UndoToast(it, { library.undo() }, Modifier.align(Alignment.TopCenter).windowInsetsPadding(WindowInsets.statusBars).padding(horizontal = 16.dp, vertical = 6.dp))
        }
        BackHandler(enabled = pages.isNotEmpty()) { pages.removeAt(pages.lastIndex) }

        // A book opens with a quick fade up from a touch smaller (or slides up like a sheet for
        // 有声书 matching its 收起 chevron), and closes the same way back; the last one stays drawn while it goes.
        var shownBook by remember { mutableStateOf<String?>(null) }
        var shownFormat by remember { mutableStateOf<uniffi.yuyue_core.BookFormat?>(null) }
        val presentedBook = library.presented?.let { id -> library.books.firstOrNull { it.id == id } }
        if (presentedBook != null) {
            shownBook = presentedBook.id
            shownFormat = presentedBook.format
        }
        LaunchedEffect(library.presented, presentedBook) {
            if (library.presented != null && presentedBook == null) {
                library.presented = null
                lastBook = ""
            }
        }
        val reduceBook = app.yuyue.design.YYMotion.isReduceMotion()
        val isAudioBook = shownFormat == uniffi.yuyue_core.BookFormat.AUDIO
        val bookEnter = when {
            reduceBook -> androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard())
            isAudioBook -> androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) +
                androidx.compose.animation.slideInVertically(app.yuyue.design.YYMotion.standard()) { it / 5 }
            else -> androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) +
                androidx.compose.animation.scaleIn(app.yuyue.design.YYMotion.standard(), initialScale = 0.97f)
        }
        val bookExit = when {
            reduceBook -> androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit())
            isAudioBook -> androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) +
                androidx.compose.animation.slideOutVertically(app.yuyue.design.YYMotion.exit()) { it / 4 }
            else -> androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) +
                androidx.compose.animation.scaleOut(app.yuyue.design.YYMotion.exit(), targetScale = 0.98f)
        }
        androidx.compose.animation.AnimatedVisibility(
            presentedBook != null,
            enter = bookEnter,
            exit = bookExit,
        ) {
            shownBook?.let { id ->
                LaunchedEffect(id) { lastBook = id }
                val closeBook = {
                    library.presented = null
                    lastBook = ""
                }
                when (shownFormat) {
                    uniffi.yuyue_core.BookFormat.PDF -> app.yuyue.pdf.PdfScreen(library, id, closeBook)
                    uniffi.yuyue_core.BookFormat.AUDIO -> app.yuyue.shelf.AudiobookScreen(library, id, closeBook)
                    null -> Unit
                    else -> ReaderScreen(library, id, closeBook)
                }
            }
        }

        val openPodcastID = if (library.hasPodcastHub) library.podcastHub.requestedOpenIssueID else null
        var shownPodcast by remember { mutableStateOf<String?>(null) }
        if (openPodcastID != null) shownPodcast = openPodcastID
        androidx.compose.animation.AnimatedVisibility(
            openPodcastID != null,
            enter = if (reduceBook) androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) else androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) + androidx.compose.animation.scaleIn(app.yuyue.design.YYMotion.standard(), initialScale = 0.97f),
            exit = if (reduceBook) androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) else androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) + androidx.compose.animation.scaleOut(app.yuyue.design.YYMotion.exit(), targetScale = 0.98f),
        ) {
            shownPodcast?.let { issueId ->
                app.yuyue.podcast.PodcastReaderScreen(library, issueId) {
                    library.podcastHub.requestedOpenIssueID = null
                }
            }
        }
    }

    LaunchedEffect(Unit) { library.sync.keepUp() }
    // Books put in the watched folder come in whenever 语阅 comes to the front.
    val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current.lifecycle
    androidx.compose.runtime.DisposableEffect(lifecycle) {
        val observer = androidx.lifecycle.LifecycleEventObserver { _, e ->
            if (e == androidx.lifecycle.Lifecycle.Event.ON_START) library.scope.launch { library.watched.scan(library) }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer) }
    }
    library.openedSnapshot?.let { (file, info) -> app.yuyue.me.SnapshotImportSheet(library, file, info) { library.openedSnapshot = null } }
    library.pendingRecording?.let { app.yuyue.shelf.AudiobookImportSheet(library, it) }

    library.alert?.let { message ->
        AlertDialog(
            onDismissRequest = { library.alert = null },
            containerColor = YY.c.card,
            title = { Text(t("出了点问题"), color = YY.c.t1) },
            text = { Text(message, color = YY.c.t2) },
            confirmButton = { TextButton({ library.alert = null }) { Text(t("好"), color = YY.tint.accent) } },
        )
    }
}

/** How much of the bottom the tab bar (and 播放条) covers: a tab's list pads its end by this so
 *  the last row scrolls clear of it. */
val LocalTabBarHeight = staticCompositionLocalOf { 0.dp }

/** The tab bar flush with the bottom edge: paper colour
 *  at 88% over a blur of the page, a hairline on top; four tabs with icon and word, the one that
 *  is on filled and in the theme's fill colour (墨蓝 by default, ink under 墨黑). While a book is read aloud the 播放条 sits inside it, on
 *  the same blurred paper, above the tabs. */
@Composable
private fun TabBar(tab: Tab, chrome: app.yuyue.shelf.ShelfChrome, haze: HazeState, playing: @Composable () -> Unit, select: (Tab) -> Unit) {
    val eink by rememberPref("eink.on", false)
    val paper = YY.c.bg
    val style = remember(paper) {
        HazeBlurStyle {
            blurRadius(20.dp)
            backgroundColor(paper)
            colorEffects(listOf(HazeColorEffect.tint(paper.copy(alpha = 0.88f))))
        }
    }
    val barBg = if (eink) Modifier.background(paper) else Modifier.hazeBlur(HazeInput.Sources(haze), style)
    Column(Modifier.fillMaxWidth().then(barBg)) {
        Box(Modifier.fillMaxWidth().height(YY.s(0.5f)).background(YY.c.line))
        playing()
        // 35px under the tabs, the system's handle included.
        val inset = WindowInsets.navigationBars.asPaddingValues().calculateBottomPadding()
        Row(
            Modifier.fillMaxWidth().padding(start = YY.s(4), end = YY.s(4), top = YY.s(5), bottom = maxOf(YY.s(35), inset + 7.dp)),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            // 书架, or where the shelf is now (进度, 有声, a 合集) with its mark; chosen, a small ⌃⌄ beside it.
            val placeIcon = chrome.icon
            TabItem(placeIcon ?: TabIcons.shelf, placeIcon?.let(app.yuyue.shelf.PlaceIcons::filled) ?: TabIcons.shelfOn, chrome.name ?: t("书架"), tab == Tab.Shelf, switch = true) { select(Tab.Shelf) }
            TabItem(TabIcons.notes, TabIcons.notesOn, t("笔记"), tab == Tab.Notes) { select(Tab.Notes) }
            TabItem(TabIcons.me, TabIcons.meOn, t("我的"), tab == Tab.Me, badge = app.yuyue.update.Updates.showsBadge) { select(Tab.Me) }
            TabItem(TabIcons.search, TabIcons.searchOn, t("搜索"), tab == Tab.Search) { select(Tab.Search) }
        }
    }
}

@Composable
private fun RowScope.TabItem(icon: ImageVector, iconOn: ImageVector, title: String, on: Boolean, switch: Boolean = false, badge: Boolean = false, onClick: () -> Unit) {
    val color = if (on) YY.tint.fill else YY.c.t2
    val press = remember { MutableInteractionSource() }
    Column(
        Modifier.weight(1f).heightIn(min = YY.s(44)).padding(vertical = 2.dp)
            .selectable(on, interactionSource = press, indication = null, role = androidx.compose.ui.semantics.Role.Tab, onClick = onClick),
        horizontalAlignment = Alignment.CenterHorizontally,
        // 2px wanted; Android's line box for the word already adds about 1.6 of it.
        verticalArrangement = Arrangement.spacedBy(YY.s(0.4f), Alignment.CenterVertically),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.pressScale(press, 0.9f)) {
            // As much nothing on the left as the ⌃⌄ on the right keeps the icon over its name.
            if (switch) androidx.compose.foundation.layout.Spacer(Modifier.width(7.dp))
            Box {
                Icon(if (on) iconOn else icon, null, tint = color, modifier = Modifier.size(24.dp))
                if (badge) Box(Modifier.align(Alignment.TopEnd).size(7.dp).offset(2.dp, (-2).dp).clip(CircleShape).background(YY.c.danger))
            }
            if (switch) Icon(TabIcons.switchChevron, null, tint = color.copy(alpha = if (on) 1f else 0f), modifier = Modifier.size(width = 7.dp, height = 24.dp))
        }
        Text(title, style = YY.font(10f, if (on) FontWeight.SemiBold else FontWeight.Medium).copy(lineHeight = YY.sp(12f)), color = color)
    }
}

@Composable
fun Placeholder(title: String) {
    Box(Modifier.fillMaxSize().background(YY.c.bg), contentAlignment = Alignment.Center) {
        Text(title, color = YY.c.t3)
    }
}

/** The 播放条 while a book is read aloud with its pages closed: a small card
 *  over the tab bar with the cover, `背影 · 第一章` and `第 4 / 24 句 · 本章还剩 7 分钟`, ‹ ▶ ›,
 *  and a thin line for the chapter along its bottom. A tap anywhere else goes back to the page. */
@Composable
private fun NowPlayingBar(library: Library, onPodcastShelf: Boolean = false) {
    (library.nowPlaying as? app.yuyue.speech.AudiobookPlayer)?.let { return AudioPlayingBar(library, it) }
    val model = library.nowPlaying as? app.yuyue.reader.ReaderModel
    if (model == null) {
        if (!onPodcastShelf && library.hasPodcastHub) {
            val hub = library.podcastHub
            val issue = hub.activeIssue
            if (issue != null && hub.isPlaying && hub.requestedOpenIssueID == null) {
                app.yuyue.podcast.PodcastMiniPlayerBar(
                    library = library,
                    issue = issue,
                    modifier = Modifier.padding(start = YY.s(8), end = YY.s(8), top = YY.s(8), bottom = YY.s(6)),
                ) {
                    hub.requestedOpenIssueID = issue.id
                }
            }
        }
        return
    }
    val player = model.player ?: return
    val book = library.books.firstOrNull { it.id == model.bookID } ?: return
    if (player.state == app.yuyue.speech.Player.State.Stopped) {
        LaunchedEffect(Unit) { model.close(); library.nowPlaying = null }
        return
    }
    val tint = YY.tint
    val spot = player.spot
    val chapter = spot?.let { s -> model.detail?.let { app.yuyue.reader.ReaderModel.chapterTitle(it, s.section) } }
    val n = player.sentenceCount
    val note = if (spot != null && n > 0) {
        val left = maxOf(1, (model.minutes(spot.section).toDouble() * (n - spot.sentence) / n).roundToInt())
        t("第 %lld / %lld 句 · 本章还剩 %lld 分钟", minOf(spot.sentence + 1, n), n, left)
    } else null
    val shape = RoundedCornerShape(YY.s(14))
    val reduce = app.yuyue.design.YYMotion.isReduceMotion()
    Box(
        Modifier.fillMaxWidth().padding(start = YY.s(8), end = YY.s(8), top = YY.s(8), bottom = YY.s(6)).heightIn(min = YY.s(54))
            .shadow(if (YY.dark) 0.dp else 6.dp, shape, ambientColor = Color(0x14000000), spotColor = Color(0x14000000))
            .clip(shape).background(YY.c.card.copy(alpha = 0.92f)).border(0.5.dp, YY.c.line, shape)
            .clickable(onClickLabel = t("回到正在读的页"), role = androidx.compose.ui.semantics.Role.Button) { library.presented = book.id },
    ) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = YY.s(54)).padding(start = YY.s(9), end = YY.s(6), top = 4.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(2)),
        ) {
            Box(Modifier.padding(end = YY.s(6)).size(YY.s(36)), contentAlignment = Alignment.Center) {
                app.yuyue.shelf.BookCover(book, Modifier.size(width = YY.s(24), height = YY.s(36)))
            }
            Column(Modifier.weight(1f)) {
                Text(if (chapter != null) t("%@ · %@", book.title, chapter) else book.title, style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                // The line rolls up as the voice moves to the next sentence.
                androidx.compose.animation.AnimatedContent(note ?: player.line ?: "", transitionSpec = {
                    if (reduce) {
                        androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.quick()) togetherWith
                            androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit())
                    } else {
                        (androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) + androidx.compose.animation.slideInVertically(app.yuyue.design.YYMotion.standard()) { it / 2 }) togetherWith
                            (androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) + androidx.compose.animation.slideOutVertically(app.yuyue.design.YYMotion.exit()) { -it / 2 })
                    }
                }, label = "line") { text ->
                    Text(text, style = YY.font(10f, tnum = true), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("上一句")) { player.skip(false) }, contentAlignment = Alignment.Center) {
                    Icon(Icons.previous, t("上一句"), tint = YY.c.t1, modifier = Modifier.size(YY.s(18)))
                }
                val playing = player.state == app.yuyue.speech.Player.State.Playing || player.state == app.yuyue.speech.Player.State.Loading
                val label = if (playing) t("暂停") else t("接着听")
                Box(Modifier.size(44.dp).clip(CircleShape).pressable(label = label, scale = 0.9f) { player.toggle() }, contentAlignment = Alignment.Center) {
                    Box(Modifier.size(YY.s(36)).clip(CircleShape).background(tint.black), contentAlignment = Alignment.Center) {
                        Icon(if (playing) Icons.pause else Icons.play, label, tint = tint.onBlack, modifier = Modifier.size(YY.s(17)))
                    }
                }
                Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("下一句")) { player.skip(true) }, contentAlignment = Alignment.Center) {
                    Icon(Icons.next, t("下一句"), tint = YY.c.t1, modifier = Modifier.size(YY.s(18)))
                }
            }
        }
        // How far into this chapter.
        val trackColor = YY.c.line
        val fillColor = tint.fill
        val progress = player.sectionProgress.toFloat().coerceIn(0f, 1f)
        Box(Modifier.align(Alignment.BottomStart).padding(horizontal = YY.s(12)).fillMaxWidth().height(2.dp).drawBehind {
            val r = CornerRadius(size.height / 2f)
            drawRoundRect(trackColor, cornerRadius = r)
            if (progress > 0f) drawRoundRect(fillColor, size = Size(size.width * progress, size.height), cornerRadius = r)
        })
    }
}

/** The 播放条 for an 有声书: −15 s, ▶, +30 s, and the time. */
@androidx.annotation.OptIn(androidx.media3.common.util.UnstableApi::class)
@Composable
private fun AudioPlayingBar(library: Library, player: app.yuyue.speech.AudiobookPlayer) {
    val book = library.books.firstOrNull { it.id == player.bookID } ?: return
    val tint = YY.tint
    val (at, of) = player.inChapter
    val shape = RoundedCornerShape(YY.s(14))
    Box(
        Modifier.fillMaxWidth().padding(start = YY.s(8), end = YY.s(8), top = YY.s(8), bottom = YY.s(6)).heightIn(min = YY.s(54))
            .shadow(if (YY.dark) 0.dp else 6.dp, shape, ambientColor = Color(0x14000000), spotColor = Color(0x14000000))
            .clip(shape).background(YY.c.card.copy(alpha = 0.92f)).border(0.5.dp, YY.c.line, shape)
            .clickable(onClickLabel = t("回到播放页"), role = androidx.compose.ui.semantics.Role.Button) { library.presented = book.id },
    ) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = YY.s(54)).padding(start = YY.s(9), end = YY.s(6), top = 4.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(2)),
        ) {
            Box(Modifier.padding(end = YY.s(6)).size(YY.s(36)), contentAlignment = Alignment.Center) {
                app.yuyue.shelf.SquareCover(book, Modifier.size(YY.s(36)), radius = 5.dp, shadow = 0.dp)
            }
            Column(Modifier.weight(1f)) {
                Text(t("%@ · %@", book.title, player.title(player.chapter)), style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("${app.yuyue.speech.AudiobookPlayer.clock(at)} / ${app.yuyue.speech.AudiobookPlayer.clock(of)}", style = YY.font(10f, tnum = true), color = YY.c.t2, maxLines = 1)
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("后退 15 秒")) { player.skip(-app.yuyue.speech.AudiobookPlayer.BACK) }, contentAlignment = Alignment.Center) {
                    Icon(Icons.back15, t("后退 15 秒"), tint = YY.c.t1, modifier = Modifier.size(YY.s(20)))
                }
                val label = if (player.isPlaying) t("暂停") else t("接着听")
                Box(Modifier.size(44.dp).clip(CircleShape).pressable(label = label, scale = 0.9f) { player.toggle() }, contentAlignment = Alignment.Center) {
                    Box(Modifier.size(YY.s(36)).clip(CircleShape).background(tint.black), contentAlignment = Alignment.Center) {
                        Icon(if (player.isPlaying) Icons.pause else Icons.play, label, tint = tint.onBlack, modifier = Modifier.size(YY.s(17)))
                    }
                }
                Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("前进 30 秒")) { player.skip(app.yuyue.speech.AudiobookPlayer.AHEAD) }, contentAlignment = Alignment.Center) {
                    Icon(Icons.ahead30, t("前进 30 秒"), tint = YY.c.t1, modifier = Modifier.size(YY.s(20)))
                }
            }
        }
        val trackColor = YY.c.line
        val fillColor = tint.fill
        val progress = if (of > 0) (at / of).toFloat().coerceIn(0f, 1f) else 0f
        Box(Modifier.align(Alignment.BottomStart).padding(horizontal = YY.s(12)).fillMaxWidth().height(2.dp).drawBehind {
            val r = CornerRadius(size.height / 2f)
            drawRoundRect(trackColor, cornerRadius = r)
            if (progress > 0f) drawRoundRect(fillColor, size = Size(size.width * progress, size.height), cornerRadius = r)
        })
    }
}
