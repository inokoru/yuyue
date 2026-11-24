package app.yuyue.reader

import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import app.yuyue.design.pressable
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import app.yuyue.design.Icons
import app.yuyue.design.LocalTint
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.design.YYPaper
import app.yuyue.design.YYPapers
import app.yuyue.library.Library
import app.yuyue.shelf.UndoToast
import app.yuyue.t
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.rememberHazeState
import kotlinx.coroutines.launch
import uniffi.yuyue_core.Mark
import uniffi.yuyue_core.MarkStyle
import uniffi.yuyue_core.Paper
import kotlin.math.roundToInt

val Paper.tokens: YYPaper
    get() = when (this) {
        Paper.PLAIN -> YYPapers.Plain
        Paper.DARK -> YYPapers.Dark
        Paper.KAMI -> YYPapers.Kami
        Paper.KAMI_NIGHT -> YYPapers.Kaminight
        Paper.SAGE -> YYPapers.Sage
        Paper.EINK -> YYPapers.Eink
    }

/** A book over the tabs, full screen, as on iPhone: only the words and one capsule at the
 *  bottom (目录, where the reader is, ‹ 朗读 ›, Aa); a tap in the middle floats 返回, the
 *  book's name and page, 搜索, 书签 and 更多 along the top. */
@Composable
fun ReaderScreen(library: Library, bookID: String, close: () -> Unit) {
    val context = LocalContext.current
    val density = LocalDensity.current
    val top = WindowInsets.statusBars.getTop(density)
    val bottom = WindowInsets.navigationBars.getBottom(density)
    val model = remember(bookID) {
        // Still being read aloud with its pages closed: picked up as it is.
        (library.nowPlaying as? ReaderModel)?.takeIf { it.bookID == bookID }?.also { library.nowPlaying = null } ?: run {
            library.nowPlaying?.let { (it as? ReaderModel)?.close(); (it as? app.yuyue.speech.AudiobookPlayer)?.close() }
            library.nowPlaying = null
            val pager = ReaderPager(context, library.core, bookID)
            pager.insetTop = top
            pager.insetBottom = bottom
            ReaderModel(library, bookID, pager, context)
        }
    }
    val paper = model.paperTokens
    model.pager.background = paper.bg.toArgb()
    LaunchedEffect(model, Papers.version, PaperAppearance.version) { model.papersChanged() }
    LaunchedEffect(model, bookID) { model.consumeIntent() }
    DisposableEffect(model) {
        // 音量键翻页: on unless reading aloud, when the keys are for the volume.
        VolumeKeys.handler = { forward ->
            if (Prefs.bool("reader.volumeKeys", true) && !model.isPlaying && model.settings.mode == uniffi.yuyue_core.LayoutMode.PAGINATED) {
                model.turn(forward); true
            } else false
        }
        onDispose {
            VolumeKeys.handler = null
            // Pages closed while the voice reads on: it goes on, with the 播放条 over the tabs.
            if (model.isPlaying) library.nowPlaying = model else model.close()
        }
    }
    // A downloaded or added font arrived: the open sections load again, now with its @font-face.
    val fonts = WenKai.version + FontShelf.version
    val initialFonts = remember(model) { fonts }
    LaunchedEffect(fonts) { if (fonts != initialFonts) model.pager.reload() }
    BackHandler {
        when {
            model.selection != null || model.openMark != null -> model.dismissCards()
            model.searchShown -> model.endSearch()
            else -> close()
        }
    }

    // The status bar only with the controls, as on iPhone; its icons follow the paper.
    val activity = context as? android.app.Activity
    LaunchedEffect(model.chromeShown, model.searchShown, model.immersive, paper) {
        val window = activity?.window ?: return@LaunchedEffect
        val controller = WindowCompat.getInsetsController(window, window.decorView)
        controller.isAppearanceLightStatusBars = !paper.isDark
        controller.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        val show = model.chromeShown || model.searchShown || (model.immersive && !ImmersivePrefs.hidesStatusBar)
        if (show) controller.show(WindowInsetsCompat.Type.statusBars()) else controller.hide(WindowInsetsCompat.Type.statusBars())
        if (!model.searchShown) controller.hide(WindowInsetsCompat.Type.ime())
        // 沉浸: the navigation bar goes too, until a swipe from the edge.
        if (model.immersive && !model.chromeShown) controller.hide(WindowInsetsCompat.Type.navigationBars()) else controller.show(WindowInsetsCompat.Type.navigationBars())
    }
    // 读书时通知不打扰, while 沉浸阅读 is on.
    LaunchedEffect(model.immersive) { if (ImmersivePrefs.quiet) DoNotDisturb.set(context, model.immersive) }
    // 沉浸: called up by a tap, gone again by itself.
    LaunchedEffect(model.immersive, model.chromeShown) {
        if (model.immersive && model.chromeShown) { kotlinx.coroutines.delay(4000); if (!model.listenShown && !model.settingsShown) model.chromeShown = false }
    }
    DisposableEffect(Unit) {
        onDispose {
            val window = activity?.window ?: return@onDispose
            WindowCompat.getInsetsController(window, window.decorView).show(WindowInsetsCompat.Type.systemBars())
        }
    }

    var thought by remember { mutableStateOf<Mark?>(null) }
    var translating by remember { mutableStateOf<String?>(null) }
    val haze = rememberHazeState()
    val popupOpen = model.selection != null || model.openMark != null || model.lookup != null || model.speakerPick != null
    val topBarVisible = (model.chromeShown || model.searchShown) && !popupOpen

    Box(Modifier.fillMaxSize().background(paper.bg)) {
        // AndroidView(WebView) inside hazeSource triggers a continuous 60fps functor invalidation
        // loop between Chromium's hardware layer and Haze's GraphicsLayer recording.
        Box(Modifier.fillMaxSize()) {
            AndroidView({ model.pager.also { (it.parent as? android.view.ViewGroup)?.removeView(it) } }, Modifier.fillMaxSize())
            PageTapHint(model, paper)
        }
        Chrome(model, paper, haze, popupOpen, close)
        Cards(model, paper, thought = { thought = it }, translate = { translating = it })
        SpeakerLayer(model)
        WordCardHost(model)
        model.toast?.let {
            UndoToast(
                it, undo = { model.undoToast() },
                modifier = Modifier.align(Alignment.TopCenter).windowInsetsPadding(WindowInsets.statusBars)
                    .padding(start = 16.dp, end = 16.dp, top = if (topBarVisible) 52.dp else 6.dp, bottom = 6.dp),
            )
        }
        if (model.detail == null) app.yuyue.shelf.BookFailure(model.failure, close)
        else model.failure?.let { app.yuyue.shelf.FailureDialog(it) { model.failure = null } }
    }

    if (model.tocShown) TocSheet(model) { model.tocShown = false }
    if (model.settingsShown) TypesetSheet(model) { model.settingsShown = false }
    if (model.listenShown) ListenSheet(model) { model.listenShown = false }
    if (model.castShown) CastSheet(model) { model.castShown = false }
    if (model.immersiveShown) ImmersiveSheet(model) { model.immersiveShown = false }
    if (model.rulesShown) ReplaceRulesSheet(library.core, bookID, changed = { model.player?.textsChanged(); model.pager.reload() }) { model.rulesShown = false }
    if (model.txtShown) TxtLayoutSheet(library, bookID, model.detail?.summary?.title ?: "", rebuilt = {
        // The book was built again: closed and opened afresh, at the place the core carried over.
        model.txtShown = false
        close()
        library.scope.launch { kotlinx.coroutines.delay(150); library.presented = bookID }
    }) { model.txtShown = false }
    if (model.infoShown) library.books.firstOrNull { it.id == bookID }?.let { app.yuyue.shelf.BookInfoSheet(library, it) { model.infoShown = false } }
    var companionSettings by remember { mutableStateOf(false) }
    var companionVersion by remember { mutableStateOf(0) }
    LaunchedEffect(companionSettings) { if (!companionSettings) companionVersion++ }
    if (model.companionShown) app.yuyue.companion.CompanionSheet(model, settings = { companionSettings = true }) { model.companionShown = false }
    translating?.let { text -> TranslateSheet(model, text, companionVersion, openSettings = { companionSettings = true }) { translating = null } }
    if (companionSettings) app.yuyue.companion.CompanionSettingsSheet { companionSettings = false }
    model.adjustingSentence?.let { (sec, sen) ->
        TimingAdjustSheet(model, sec, sen) { model.adjustingSentence = null }
    }
    model.player?.let { PlayerFailure(it) }
    val thermalNotice = model.player?.thermalNotice
    LaunchedEffect(thermalNotice) {
        if (!thermalNotice.isNullOrEmpty()) {
            model.show(Library.Toast(Library.Toast.Icon.Error, thermalNotice, ""))
        }
    }
    thought?.let { mark ->
        ThoughtDialog(mark.note ?: "", mark.text) { text ->
            if (text != null) model.setNote(text, mark)
            thought = null
        }
    }
}

/** A paper-tinted glass shape over the page, opaque in 墨水屏模式. */
@Composable
private fun Modifier.glass(paper: YYPaper, @Suppress("UNUSED_PARAMETER") haze: HazeState, shape: androidx.compose.ui.graphics.Shape = CircleShape): Modifier {
    val eink = EInk.on
    if (eink) {
        return this.clip(shape).background(paper.bg).border(1.dp, paper.ink, shape)
    }
    return this.shadow(if (paper.isDark) 6.dp else 10.dp, shape, ambientColor = Color.Black.copy(alpha = 0.08f), spotColor = Color.Black.copy(alpha = 0.14f))
        .clip(shape)
        .background(paper.glass.copy(alpha = 0.92f))
        .border(0.5.dp, if (paper.isDark) Color.White.copy(alpha = 0.10f) else paper.line, shape)
}

@Composable
private fun Chrome(model: ReaderModel, paper: YYPaper, haze: HazeState, popupOpen: Boolean, close: () -> Unit) {
    // 跳过了片头: offered for 5 seconds.
    val head = model.player?.skippedHead
    LaunchedEffect(head) {
        if (head != null) { kotlinx.coroutines.delay(5000); model.player.dismissHead() }
    }
    val topShown = (model.chromeShown || model.searchShown) && !popupOpen
    val reduce = YYMotion.isReduceMotion()
    // The banners move down and up with the bar rather than jumping under it.
    val bannerTop by androidx.compose.animation.core.animateDpAsState(if (topShown) 52.dp else 6.dp, YYMotion.standard(), label = "bannerTop")
    Box(Modifier.fillMaxSize()) {
        val chapterBanner = model.chapterOnlyHere && !model.searchShown && !popupOpen
        AnimatedVisibility(chapterBanner, Modifier.align(Alignment.TopCenter), enter = androidx.compose.animation.fadeIn(YYMotion.standard()), exit = androidx.compose.animation.fadeOut(YYMotion.exit())) {
            // 只按章跟着走: over a chapter the recording doesn't line up with sentence by sentence.
            Row(
                Modifier.windowInsetsPadding(WindowInsets.statusBars)
                    .padding(top = bannerTop).padding(horizontal = 16.dp).widthIn(max = 520.dp)
                    .clip(RoundedCornerShape(10.dp)).background(paper.line.copy(alpha = 0.55f)).heightIn(min = 44.dp).padding(start = 12.dp, end = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(t("这一章录音和书对不上逐句，只按章跟着走"), fontSize = 12.sp, color = paper.ink2, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                app.yuyue.design.YYButton(t("校准一句"), size = app.yuyue.design.ButtonSize.Small, modifier = Modifier.padding(start = 8.dp)) { model.calibrateChapter() }
            }
        }
        AnimatedVisibility(!chapterBanner && model.textVersion && topShown && !model.searchShown, Modifier.align(Alignment.TopCenter), enter = YYMotion.barIn(true, reduce), exit = YYMotion.barOut(true, reduce)) {
            // 文字版: made from a recording's words, so the page says so.
            Row(
                Modifier.windowInsetsPadding(WindowInsets.statusBars)
                    .padding(top = 52.dp).padding(horizontal = 16.dp)
                    .clip(RoundedCornerShape(10.dp)).background(paper.line.copy(alpha = 0.55f)).heightIn(min = 36.dp).padding(horizontal = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                val polishing = app.yuyue.speech.TextPolishRuns.states[model.bookID]
                val fixed = app.yuyue.speech.TextPolishRuns.fixed(model.bookID)
                val label = when {
                    polishing is app.yuyue.speech.TextPolishRuns.State.Running -> t("正在整理 · %lld / %lld 段 · 改了 %lld 处", polishing.done.toLong(), polishing.of.toLong(), polishing.fixed.toLong())
                    polishing is app.yuyue.speech.TextPolishRuns.State.Failed -> polishing.why
                    fixed != null -> t("整理过了，改了 %lld 处同音字和人名，可能还有错字", fixed.toLong())
                    else -> t("这是录音转出来的文字，可能有错字")
                }
                Text(label, fontSize = 12.sp, color = paper.ink2, style = YY.tnum, maxLines = 1, modifier = Modifier.weight(1f, fill = false))
                // 整理错字: only with a model connected (书伴).
                if (polishing is app.yuyue.speech.TextPolishRuns.State.Running) {
                    app.yuyue.design.YYButton(t("停"), size = app.yuyue.design.ButtonSize.Small, modifier = Modifier.padding(start = 8.dp)) { app.yuyue.speech.TextPolishRuns.stop(model.bookID) }
                } else if (fixed == null && DramaWork.config != null) {
                    app.yuyue.design.YYButton(t("整理错字"), size = app.yuyue.design.ButtonSize.Small, modifier = Modifier.padding(start = 8.dp)) { app.yuyue.speech.TextPolishRuns.start(model.library, model.bookID) }
                }
            }
        }
        AnimatedVisibility(topShown, enter = YYMotion.barIn(fromTop = true, reduce), exit = YYMotion.barOut(toTop = true, reduce), modifier = Modifier.align(Alignment.TopCenter)) {
            // The page fades out under the bar, so a chapter title never shows through the title pill.
            val edge = Brush.verticalGradient(0f to paper.bg, 0.72f to paper.bg, 1f to paper.bg.copy(alpha = 0f))
            Box(Modifier.fillMaxWidth().background(edge).padding(bottom = 20.dp), contentAlignment = Alignment.TopCenter) {
                Box(Modifier.windowInsetsPadding(WindowInsets.statusBars).padding(horizontal = 12.dp).padding(top = 4.dp).widthIn(max = 560.dp)) {
                    if (model.searchShown) SearchField(model, paper, haze) else TopBar(model, paper, haze, close)
                }
            }
        }
        Column(
            Modifier.align(Alignment.BottomCenter).windowInsetsPadding(WindowInsets.navigationBars).padding(horizontal = 12.dp).padding(bottom = 4.dp).widthIn(max = 560.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            // The pill and the cards rise in over the capsule and sink away, the last one drawn as it goes.
            val back = model.returnTo?.takeIf { !model.barsAway && !popupOpen }
            var lastBack by remember { mutableStateOf(back) }
            if (back != null) lastBack = back
            AnimatedVisibility(back != null, enter = YYMotion.barIn(false, reduce), exit = YYMotion.barOut(false, reduce)) { lastBack?.let { (_, page) ->
                Row(
                    Modifier.padding(bottom = 8.dp).glass(paper, haze).pressable(scale = 0.97f, role = null) { model.goBack() }.padding(horizontal = 14.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    Icon(Icons.undo, null, tint = paper.ink, modifier = Modifier.size(15.dp))
                    Text(page?.let { t("回到第 %lld 页", it) } ?: t("回到原处"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = paper.ink, style = YY.tnum)
                }
            } }
            val flag = model.narrationFlag?.takeIf { !popupOpen }
            var lastFlag by remember { mutableStateOf(flag) }
            if (flag != null) lastFlag = flag
            AnimatedVisibility(flag != null, enter = YYMotion.barIn(false, reduce), exit = YYMotion.barOut(false, reduce)) {
                lastFlag?.let { f -> NarrationFlagCard(f, letBe = { model.letFlagBe() }, adjust = { model.adjustFlag() }) }
            }
            val sleep = model.sleepReturn?.takeIf { !popupOpen }
            var lastSleep by remember { mutableStateOf(sleep) }
            if (sleep != null) lastSleep = sleep
            AnimatedVisibility(sleep != null, enter = YYMotion.barIn(false, reduce), exit = YYMotion.barOut(false, reduce)) {
                lastSleep?.let { r -> SleepReturnCard(r.whenText, "${r.chapter} ${r.at}", paper, haze) { model.answerSleepReturn(it) } }
            }
            // 沉浸阅读 hides the capsule until the middle is tapped; scrolling down the page, until back up it.
            val capsule = ((!model.immersive && !model.barsAway) || model.chromeShown || model.searchShown) && !popupOpen
            AnimatedVisibility(capsule, enter = YYMotion.barIn(fromTop = false, reduce), exit = YYMotion.barOut(toTop = false, reduce)) {
                if (model.searchShown) SearchCapsule(model, paper, haze) else Capsule(model, paper, haze)
            }
            AnimatedVisibility(!capsule && model.immersive && !popupOpen, enter = androidx.compose.animation.fadeIn(YYMotion.standard()), exit = androidx.compose.animation.fadeOut(YYMotion.exit())) {
                if (!ImmersivePrefs.hidesProgress) {
                    Text(model.capsuleNote, fontSize = 12.sp, color = paper.ink2, style = YY.tnum, modifier = Modifier.padding(bottom = 2.dp))
                } else if (ImmersivePrefs.keepsLine) {
                    val frac = model.bookFraction.toFloat().coerceIn(0f, 1f)
                    val lineColor = paper.ink.copy(alpha = 0.25f)
                    Box(Modifier.fillMaxWidth().height(1.dp).drawBehind {
                        if (frac > 0f) drawRect(lineColor, size = Size(size.width * frac, size.height))
                    })
                }
            }
        }
    }
}

@Composable
private fun TopBar(model: ReaderModel, paper: YYPaper, haze: HazeState, close: () -> Unit) {
    var more by remember { mutableStateOf(false) }
    val line = listOfNotNull(model.chapterTitle?.takeIf { it != model.detail?.summary?.title }, model.bookPage?.let { t("第 %lld 页", it) }).joinToString(" · ")
    ReaderTopBar(
        paper, haze,
        title = model.detail?.summary?.title ?: "",
        subtitle = line,
        bookmarked = model.bookmarkHere != null,
        onClose = close,
        onSearch = { model.chromeShown = false; model.searchShown = true },
        onBookmark = { model.toggleBookmark() },
        more = {
            Box {
                Glyph(Icons.more, t("更多"), paper.ink) { more = true }
                DropdownMenu(more, { more = false }, containerColor = YY.c.card) {
                    app.yuyue.shelf.Item(t("沉浸阅读"), Icons.viewfinder) { more = false; model.immersiveShown = true }
                    app.yuyue.shelf.Item(t("书伴"), Icons.textBubble) { more = false; model.chromeShown = false; model.companionShown = true }
                    app.yuyue.shelf.Item(t("书的信息"), Icons.info) { more = false; model.infoShown = true }
                    if (model.detail?.summary?.format != uniffi.yuyue_core.BookFormat.PDF) app.yuyue.shelf.Item(t("替换规则"), Icons.swapHoriz) { more = false; model.rulesShown = true }
                    if (model.detail?.summary?.format == uniffi.yuyue_core.BookFormat.TXT) app.yuyue.shelf.Item(t("编码和目录"), Icons.docSearch) { more = false; model.txtShown = true }
                }
            }
        },
    )
}

/** The glass row over a page: 返回, the book's name, 搜索 · 书签 · 更多. The snapshot uses this
 *  with fixture words; the WebView under it is not part of the picture. */
@Composable
internal fun ReaderTopBar(
    paper: YYPaper,
    haze: HazeState,
    title: String,
    subtitle: String,
    bookmarked: Boolean,
    onClose: () -> Unit,
    onSearch: () -> Unit,
    onBookmark: () -> Unit,
    more: @Composable () -> Unit,
) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Box(Modifier.defaultMinSize(minWidth = 44.dp, minHeight = 44.dp).glass(paper, haze).pressable(label = t("返回书架"), scale = 0.92f, onClick = onClose), contentAlignment = Alignment.Center) {
            Icon(Icons.back, t("返回书架"), tint = paper.ink, modifier = Modifier.size(20.dp))
        }
        Column(Modifier.weight(1f).heightIn(min = 40.dp).glass(paper, haze).padding(horizontal = 12.dp, vertical = 4.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            Text(title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = paper.ink, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
            if (subtitle.isNotEmpty()) Text(subtitle, fontSize = 11.sp, color = paper.ink2, style = YY.tnum, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
        }
        Row(Modifier.heightIn(min = 40.dp).glass(paper, haze).padding(horizontal = 3.dp), verticalAlignment = Alignment.CenterVertically) {
            Glyph(Icons.search, t("搜索这本书"), paper.ink, onSearch)
            Glyph(if (bookmarked) Icons.bookmark else Icons.bookmarkOutline, if (bookmarked) t("拿掉书签") else t("加书签"), if (bookmarked) YY.tint.accent else paper.ink, onBookmark)
            more()
        }
    }
}

@Composable
private fun Glyph(icon: ImageVector, label: String, tint: Color, onClick: () -> Unit) {
    Box(Modifier.size(width = 38.dp, height = 44.dp).clip(CircleShape).pressable(label = label, scale = 0.9f, onClick = onClick), contentAlignment = Alignment.Center) {
        // 书签 ⇄ 已加书签: the glyph and its colour cross over rather than flip.
        val ink by androidx.compose.animation.animateColorAsState(tint, YYMotion.quick(), label = "glyph")
        androidx.compose.animation.Crossfade(icon, animationSpec = YYMotion.quick(), label = "glyphIcon") { Icon(it, label, tint = ink, modifier = Modifier.size(19.dp)) }
    }
}

/** 平时只有一条胶囊: 目录, where the reader is, ‹ 朗读 ›, Aa, and how far into the chapter. */
@Composable
private fun Capsule(model: ReaderModel, paper: YYPaper, haze: HazeState) {
    val alert = model.capsuleAlert
    val reading = model.player?.spot != null
    val n = model.sentenceCount
    ReaderCapsuleBar(
        paper, haze,
        title = alert?.title ?: if (reading && n > 0) t("第 %lld / %lld 句", minOf(model.sentence + 1, n), n) else model.chapterTitle ?: model.detail?.summary?.title ?: "",
        note = if (alert != null) alert.note else model.capsuleNote,
        noteAccent = alert != null,
        playing = model.isPlaying,
        progress = model.chapterProgress.toFloat().coerceIn(0f, 1f),
        playEnabled = model.player != null,
        sentenceStep = reading,
        onToc = { model.tocShown = true },
        onCenter = { if (alert != null) alert.action() else model.listenShown = true },
        onPrevious = { model.step(false) },
        onPlay = { model.togglePlay() },
        onNext = { model.step(true) },
        onTypeset = { model.settingsShown = true },
    )
}

/** The capsule under the page. [Capsule] fills it from the reader; the snapshot fills it from a fixture. */
@Composable
internal fun ReaderCapsuleBar(
    paper: YYPaper,
    haze: HazeState,
    title: String,
    note: String,
    noteAccent: Boolean,
    playing: Boolean,
    progress: Float,
    playEnabled: Boolean,
    sentenceStep: Boolean,
    onToc: () -> Unit,
    onCenter: () -> Unit,
    onPrevious: () -> Unit,
    onNext: () -> Unit,
    onPlay: () -> Unit,
    onTypeset: () -> Unit,
) {
    Box(Modifier.fillMaxWidth().heightIn(min = 50.dp).glass(paper, haze)) {
        Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 5.dp, vertical = 3.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(2.dp)) {
            Glyph(Icons.list, t("目录、书签和笔记"), paper.ink, onToc)
            Column(Modifier.weight(1f).padding(start = 2.dp).clickable(enabled = playEnabled, onClick = onCenter)) {
                Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = paper.ink, style = YY.tnum, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                Text(
                    note,
                    fontSize = 12.sp, fontWeight = if (noteAccent) FontWeight.SemiBold else FontWeight.Normal,
                    color = if (noteAccent) YY.tint.accent else paper.ink2, style = YY.tnum, maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                )
            }
            Glyph(Icons.chevronLeft, if (sentenceStep) t("上一句") else t("上一页"), paper.ink, onPrevious)
            // The play button takes the theme colour on every paper but e-ink (YYTint.play).
            Box(Modifier.size(40.dp).clip(CircleShape).background(LocalTint.current.play(paper)).pressable(label = if (playing) t("暂停") else t("朗读"), enabled = playEnabled, scale = 0.92f, onClick = onPlay), contentAlignment = Alignment.Center) {
                androidx.compose.animation.Crossfade(playing, animationSpec = YYMotion.quick(), label = "play") { on -> Icon(if (on) Icons.pause else Icons.play, if (on) t("暂停") else t("朗读"), tint = paper.onMain, modifier = Modifier.size(20.dp)) }
            }
            Glyph(Icons.chevronRight, if (sentenceStep) t("下一句") else t("下一页"), paper.ink, onNext)
            Box(Modifier.size(width = 40.dp, height = 44.dp).clip(CircleShape).pressable(label = t("排版"), scale = 0.9f, onClick = onTypeset), contentAlignment = Alignment.Center) {
                Text("Aa", fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = paper.ink)
            }
        }
        val trackColor = paper.line
        val fillColor = YY.tint.accent
        val share = progress.coerceIn(0f, 1f)
        Box(Modifier.align(Alignment.BottomCenter).padding(horizontal = 24.dp).fillMaxWidth().height(2.dp).drawBehind {
            val r = CornerRadius(size.height / 2f)
            drawRoundRect(trackColor, cornerRadius = r)
            if (share > 0f) drawRoundRect(fillColor, size = Size(size.width * share, size.height), cornerRadius = r)
        })
    }
}

@Composable
private fun SearchField(model: ReaderModel, paper: YYPaper, haze: HazeState) {
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            Modifier.weight(1f).heightIn(min = 44.dp).glass(paper, haze).padding(start = 12.dp, end = if (model.query.isNotEmpty()) 2.dp else 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            Icon(Icons.search, null, tint = paper.ink2, modifier = Modifier.size(17.dp))
            BasicTextField(
                model.query, { model.query = it; model.search() }, singleLine = true,
                textStyle = androidx.compose.ui.text.TextStyle(fontSize = 15.sp, color = paper.ink),
                cursorBrush = SolidColor(YY.tint.accent),
                modifier = Modifier.weight(1f).padding(vertical = 6.dp).focusRequester(focus).semantics { contentDescription = t("搜这本书") },
                decorationBox = { inner ->
                    if (model.query.isEmpty()) Text(t("搜这本书"), fontSize = 15.sp, color = paper.ink2)
                    inner()
                },
            )
            if (model.query.isNotEmpty()) {
                Box(
                    Modifier
                        .size(width = 40.dp, height = 40.dp)
                        .clip(CircleShape)
                        .pressable(label = t("清空"), scale = 0.9f) { model.query = ""; model.search() },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        Icons.xmarkCircleFill,
                        t("清空"),
                        tint = paper.ink2,
                        modifier = Modifier.size(17.dp),
                    )
                }
            }
        }
        Box(
            Modifier.defaultMinSize(minWidth = 44.dp, minHeight = 44.dp).glass(paper, haze).pressable(label = t("结束搜索"), scale = 0.92f) { model.endSearch() },
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.close, t("结束搜索"), tint = paper.ink, modifier = Modifier.size(18.dp))
        }
    }
}

@Composable
private fun SearchCapsule(model: ReaderModel, paper: YYPaper, haze: HazeState) {
    val q = model.query.trim()
    Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).glass(paper, haze).padding(horizontal = 12.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            Text(
                when {
                    q.isEmpty() -> t("输入要找的字")
                    model.hits.isEmpty() -> t("没找到“%@”", q)
                    model.hits.size >= app.yuyue.search.SEARCH_HIT_LIMIT -> t("第 %lld / %lld+ 处", (model.hitIndex ?: 0) + 1, model.hits.size)
                    else -> t("第 %lld / %lld 处", (model.hitIndex ?: 0) + 1, model.hits.size)
                },
                fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = paper.ink, style = YY.tnum, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
            )
            val hit = model.hitIndex?.let { model.hits.getOrNull(it) }
            Text(hit?.let { model.place(it.section.toInt(), it.sentence.toInt()) } ?: if (q.isEmpty()) t("输完直接跳到第一处") else "", fontSize = 10.5.sp, color = paper.ink2, style = YY.tnum, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
        }
        Glyph(Icons.chevronLeft, t("上一处"), paper.ink.copy(alpha = if (model.hits.isEmpty()) 0.3f else 1f)) { model.nextHit(false) }
        Glyph(Icons.chevronRight, t("下一处"), paper.ink.copy(alpha = if (model.hits.isEmpty()) 0.3f else 1f)) { model.nextHit(true) }
    }
}

private data class CardSnapshot(
    val sel: TextSelection?,
    val open: OpenMark?,
    val text: String,
    val top: Float,
    val bottom: Float,
    val midX: Float?,
)

/** 选中文字, or a highlight tapped: the same card as on iPhone, over the words — three colours
 *  and the wave, the two underlines (and 删除 for a highlight), then 想法 · 从这听 · 翻译 · 复制 · 分享. */
@Composable
private fun Cards(model: ReaderModel, paper: YYPaper, thought: (Mark) -> Unit, translate: (String) -> Unit) {
    val context = LocalContext.current
    val density = LocalDensity.current
    val reduce = YYMotion.isReduceMotion()
    val sel = model.selection
    val open = model.openMark
    val active = if (sel != null || open != null) {
        val txt = open?.mark?.text ?: sel?.text
        if (txt != null) CardSnapshot(
            sel = sel,
            open = open,
            text = txt,
            top = (sel?.rect?.y ?: open?.y ?: 200.0).toFloat(),
            bottom = (sel?.rect?.let { it.y + it.height } ?: ((open?.y ?: 200.0) + 24)).toFloat(),
            midX = (sel?.rect?.let { it.x + it.width / 2.0 } ?: open?.x)?.toFloat(),
        ) else null
    } else null
    var last by remember { mutableStateOf<CardSnapshot?>(null) }
    if (active != null) last = active
    val snap = active ?: last ?: return
    val current = snap.open?.mark
    val text = snap.text
    val tint = YY.tint
    fun ringed(style: MarkStyle) = (current?.style ?: model.lastStyle) == style
    fun pick(style: MarkStyle) {
        if (current != null) { model.setStyle(style, current); model.dismissCards() } else model.mark(style)
    }
    val hasNote = !current?.note.isNullOrBlank()
    var cardHeightPx by remember { mutableIntStateOf(0) }
    var cardWidthPx by remember { mutableIntStateOf(0) }
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val cardHeight = if (cardHeightPx > 0) with(density) { cardHeightPx.toDp() } else if (hasNote) 168.dp else 116.dp
        val cardWidth = if (cardWidthPx > 0) with(density) { cardWidthPx.toDp() } else minOf(maxWidth - 32.dp, 340.dp)
        // Above the words when there is room, under them otherwise.
        val aboveCandidate = snap.top.dp - cardHeight - 12.dp
        val isAbove = aboveCandidate > 56.dp
        val maxY = (maxHeight - cardHeight - 12.dp).coerceAtLeast(56.dp)
        val y = (if (isAbove) aboveCandidate else (snap.bottom.dp + 12.dp)).coerceIn(56.dp, maxY)
        val cardLeft = (maxWidth - cardWidth) / 2f
        val selMidX = snap.midX?.dp
        val originX = if (selMidX != null && cardWidth > 1.dp) ((selMidX - cardLeft) / cardWidth).coerceIn(0.15f, 0.85f) else 0.5f
        val origin = TransformOrigin(originX, if (isAbove) 1f else 0f)
        AnimatedVisibility(
            visible = active != null,
            modifier = Modifier.align(Alignment.TopCenter).offset(y = y),
            enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = 0.92f, transformOrigin = origin),
            exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.92f, transformOrigin = origin),
        ) {
            val eink = Prefs.isReady && Prefs.bool("eink.on", false)
            Column(
                Modifier.padding(horizontal = 16.dp).widthIn(max = 340.dp).fillMaxWidth()
                    .onSizeChanged {
                        cardHeightPx = it.height
                        cardWidthPx = it.width
                    }
                    .shadow(if (eink) 0.dp else 16.dp, RoundedCornerShape(14.dp), ambientColor = Color.Black.copy(alpha = 0.14f), spotColor = Color.Black.copy(alpha = 0.2f))
                    .clip(RoundedCornerShape(14.dp)).background(YY.c.card)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)),
            ) {
                current?.note?.takeIf { it.isNotBlank() }?.let { note ->
                    Row(
                        Modifier.fillMaxWidth().clickable { model.dismissCards(); thought(current) }.padding(horizontal = 12.dp, vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Row(Modifier.weight(1f).height(androidx.compose.foundation.layout.IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Box(Modifier.width(2.dp).fillMaxHeight().background(tint.accent))
                            Text(note, fontSize = 13.sp, lineHeight = 18.sp, color = YY.c.t1, maxLines = 3, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        }
                        Icon(Icons.edit, t("改想法"), tint = tint.accent, modifier = Modifier.size(15.dp))
                    }
                    Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                }
                Row(Modifier.padding(start = 6.dp, end = 6.dp, top = 4.dp, bottom = 2.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(2.dp)) {
                    for (style in listOf(MarkStyle.YELLOW, MarkStyle.BLUE, MarkStyle.PINK, MarkStyle.WAVE)) {
                        Box(
                            Modifier.size(width = 38.dp, height = 44.dp).clip(CircleShape).clickable { pick(style) },
                            contentAlignment = Alignment.Center,
                        ) {
                            Box(
                                Modifier.size(28.dp).border(1.5.dp, if (ringed(style)) tint.accent else Color.Transparent, CircleShape).padding(3.dp)
                                    .clip(CircleShape).background(if (style == MarkStyle.WAVE) YY.c.hlR.copy(alpha = if (YY.dark) 0.24f else 0.14f) else style.swatch(paper))
                                    .border(0.5.dp, if (YY.dark) Color.White.copy(alpha = 0.16f) else Color.Black.copy(alpha = 0.06f), CircleShape),
                                contentAlignment = Alignment.Center,
                            ) { if (style == MarkStyle.WAVE) Text("~", fontSize = 17.sp, fontWeight = FontWeight.Medium, color = YY.c.hlR, modifier = Modifier.offset(y = (-2).dp)) }
                        }
                    }
                    Box(Modifier.weight(1f))
                    Row(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                        for (style in listOf(MarkStyle.UNDERLINE, MarkStyle.DOTTED)) {
                            val selected = ringed(style)
                            val ink = if (selected) tint.accent else YY.c.t1
                            val shape = RoundedCornerShape(8.5.dp)
                            val desc = if (style == MarkStyle.DOTTED) t("点线") else t("下划线")
                            Box(
                                Modifier.size(width = 38.dp, height = 44.dp).clip(shape)
                                    .semantics { contentDescription = desc }
                                    .clickable { pick(style) },
                                contentAlignment = Alignment.Center,
                            ) {
                                Column(
                                    Modifier.size(width = 36.dp, height = 34.dp).clip(shape)
                                        .background(if (selected) tint.soft else YY.c.inset)
                                        .border(if (selected) 1.5.dp else 0.75.dp, if (selected) tint.accent else YY.c.pillLine, shape),
                                    horizontalAlignment = Alignment.CenterHorizontally,
                                    verticalArrangement = Arrangement.spacedBy(3.5.dp, Alignment.CenterVertically),
                                ) {
                                    Text(
                                        "A",
                                        fontFamily = YY.serif,
                                        fontSize = 14.5.sp,
                                        lineHeight = 14.5.sp,
                                        fontWeight = FontWeight.Medium,
                                        color = ink,
                                    )
                                    if (style == MarkStyle.DOTTED) {
                                        Row(horizontalArrangement = Arrangement.spacedBy(2.8.dp), verticalAlignment = Alignment.CenterVertically) {
                                            repeat(4) {
                                                Box(Modifier.size(2.8.dp).clip(CircleShape).background(ink))
                                            }
                                        }
                                    } else {
                                        Box(Modifier.size(width = 20.dp, height = 2.2.dp).clip(CircleShape).background(ink))
                                    }
                                }
                            }
                        }
                    }
                    if (current != null) {
                        Box(Modifier.size(width = 38.dp, height = 44.dp).clip(CircleShape).clickable { model.deleteMark(current) }, contentAlignment = Alignment.Center) {
                            Icon(Icons.delete, t("删除划线"), tint = YY.c.danger, modifier = Modifier.size(18.dp))
                        }
                    }
                }
                Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 6.dp)) {
                    CardAction(Icons.noteEdit, if (hasNote) t("改想法") else t("想法"), Modifier.weight(1f), color = if (hasNote) tint.accent else YY.c.t1) {
                        val m = current ?: model.mark()
                        model.dismissCards()
                        if (m != null) thought(m)
                    }
                    if (model.player != null) CardAction(Icons.speakerWave, t("从这听"), Modifier.weight(1f)) { model.readFrom(current) }
                    if (current == null && model.contextLine != null) CardAction(Icons.personCircle, t("谁说的"), Modifier.weight(1f)) { model.pickSpeakerFromSelection() }
                    snap.sel?.takeIf { current == null }?.let { s ->
                        if (model.canPin) CardAction(Icons.target, t("对准"), Modifier.weight(1f)) { model.pinHere(s.section, s.start.s) }
                    }
                    CardAction(Icons.sparkle, t("问"), Modifier.weight(1f), color = YY.tint.accent) {
                        model.companionQuote = text
                        model.dismissCards()
                        model.chromeShown = false
                        model.companionShown = true
                    }
                    CardAction(Icons.translate, t("翻译"), Modifier.weight(1f)) {
                        val s = text
                        model.dismissCards()
                        translate(s)
                    }
                    CardAction(Icons.copy, t("复制"), Modifier.weight(1f)) { model.copySelection() }
                    CardAction(Icons.share, t("分享"), Modifier.weight(1f)) {
                        model.dismissCards()
                        val book = model.detail?.summary
                        val quote = t("“%@”", text) + (current?.note?.let { "\n$it" } ?: "") + (book?.let { "\n" + t("——《%@》", it.title) } ?: "")
                        context.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).setType("text/plain").putExtra(android.content.Intent.EXTRA_TEXT, quote), null))
                    }
                }
            }
        }
    }
}

@Composable
private fun CardAction(icon: ImageVector, title: String, modifier: Modifier, color: Color = YY.c.t1, onClick: () -> Unit) {
    Column(modifier.clip(RoundedCornerShape(8.dp)).clickable(onClick = onClick).padding(vertical = 5.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Icon(icon, title, tint = color, modifier = Modifier.size(19.dp))
        // Up to seven to a row: a longer English title (Read from here) gets smaller rather than cut off.
        androidx.compose.foundation.text.BasicText(
            title, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
            style = androidx.compose.ui.text.TextStyle(fontSize = 11.sp, color = color),
            autoSize = androidx.compose.foundation.text.TextAutoSize.StepBased(minFontSize = 7.5.sp, maxFontSize = 11.sp, stepSize = 0.5.sp),
        )
    }
}

/** The colours 笔记 and the card show for a highlight, as on Apple (`YYColor.mkY` …), tuned so
 *  standalone swatch dots stay crisp on dark card surfaces. */
@Composable
private fun MarkStyle.swatch(paper: YYPaper): Color = when (this) {
    MarkStyle.YELLOW -> if (YY.dark) YY.c.hlY.copy(alpha = 0.72f) else YY.c.mkY
    MarkStyle.BLUE -> if (YY.dark) YY.c.hlB.copy(alpha = 0.68f) else YY.c.mkB
    MarkStyle.PINK -> if (YY.dark) Color(0xFFF6C7C0).copy(alpha = 0.68f) else YY.c.mkP
    else -> paper.wave
}

/** 写想法: a few lines about the highlight. Null when cancelled; empty clears the thought. */
@Composable
fun ThoughtDialog(initial: String, quote: String? = null, done: (String?) -> Unit) {
    var text by remember(initial) { mutableStateOf(initial) }
    val focus = remember { FocusRequester() }
    LaunchedEffect(initial) { focus.requestFocus() }
    AlertDialog(
        onDismissRequest = { done(null) },
        containerColor = YY.c.card,
        title = { Text(if (initial.isEmpty()) t("写想法") else t("改想法"), color = YY.c.t1) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                quote?.takeIf { it.isNotBlank() }?.let { q ->
                    Row(Modifier.height(androidx.compose.foundation.layout.IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Box(Modifier.width(2.dp).fillMaxHeight().background(YY.c.line))
                        Text("“$q”", fontFamily = YY.serif, fontSize = 13.sp, color = YY.c.t2, maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    }
                }
                OutlinedTextField(
                    text, { text = it }, minLines = 3,
                    placeholder = { Text(t("想法…")) },
                    modifier = Modifier.fillMaxWidth().focusRequester(focus),
                )
                if (initial.isNotEmpty()) {
                    TextButton({ done("") }, modifier = Modifier.align(Alignment.Start)) {
                        Text(t("删掉想法"), color = YY.c.danger)
                    }
                }
            }
        },
        confirmButton = { TextButton({ done(text) }) { Text(t("完成"), color = YY.tint.accent) } },
        dismissButton = { TextButton({ done(null) }) { Text(t("取消"), color = YY.c.t2) } },
    )
}

/** The volume keys while a book is open: the reader takes them to turn pages, or lets them be. */
object VolumeKeys {
    var handler: ((forward: Boolean) -> Boolean)? = null
}

/** 第一次进阅读器: three faint zones — 上一页, 菜单, 下一页 — over the page the first time
 *  a book is opened, gone after three seconds and never shown again. */
@Composable
private fun PageTapHint(model: ReaderModel, paper: YYPaper) {
    var shown by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        if (Prefs.bool("reader.tapHintShown", false) || model.settings.mode != uniffi.yuyue_core.LayoutMode.PAGINATED) return@LaunchedEffect
        Prefs.set("reader.tapHintShown", true)
        shown = true
        kotlinx.coroutines.delay(3000)
        shown = false
    }
    androidx.compose.animation.AnimatedVisibility(
        shown,
        enter = androidx.compose.animation.fadeIn(YYMotion.standard()),
        exit = androidx.compose.animation.fadeOut(YYMotion.exit()),
    ) {
        Row(Modifier.fillMaxSize()) {
            listOf(Triple(t("上一页"), Icons.chevronLeft, 0.3f), Triple(t("菜单"), Icons.handTap, 0.4f), Triple(t("下一页"), Icons.chevronRight, 0.3f))
                .forEachIndexed { i, (title, icon, share) ->
                    Column(
                        Modifier.weight(share).fillMaxHeight().background(paper.ink.copy(alpha = if (i == 1) 0.07f else 0.035f)),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterVertically),
                    ) {
                        Icon(icon, null, tint = paper.ink.copy(alpha = 0.7f), modifier = Modifier.size(24.dp))
                        Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = paper.ink.copy(alpha = 0.7f))
                    }
                    if (i < 2) Box(Modifier.width(0.5.dp).fillMaxHeight().background(paper.ink.copy(alpha = 0.12f)))
                }
        }
    }
}

/** Why reading aloud stopped. With no voice for the language, the way to get one comes first. */
@Composable
internal fun PlayerFailure(player: app.yuyue.speech.Player) {
    val message = player.failure ?: return
    val context = LocalContext.current
    val missing = player.missingVoice
    AlertDialog(
        onDismissRequest = { player.failure = null },
        containerColor = YY.c.card,
        title = { Text(if (missing != null) t("这台手机还没有%@声音", missing) else t("朗读停下了"), color = YY.c.t1) },
        text = { Text(message, color = YY.c.t2) },
        confirmButton = if (missing != null) {
            {
                TextButton({
                    player.failure = null
                    // The engine's own page for voice data: Chinese, English and the rest.
                    app.yuyue.speech.SystemVoice.Engine.openSettings(context)
                }) { Text(t("下载%@声音", missing), color = YY.tint.accent) }
            }
        } else {
            { TextButton({ player.useSystemVoice() }) { Text(t("换成系统声音"), color = YY.tint.accent) } }
        },
        dismissButton = if (missing != null) {
            { TextButton({ player.failure = null }) { Text(t("先不听"), color = YY.c.t2) } }
        } else {
            { TextButton({ player.retry() }) { Text(t("重试"), color = YY.c.t2) } }
        },
    )
}

/** 气泡: the card for who says a line, where its avatar was tapped; 别的人物… searches everyone. */
@Composable
private fun SpeakerLayer(model: ReaderModel) {
    val active = model.speakerPick
    var lastPick by remember { mutableStateOf<SpeakerPick?>(null) }
    if (active != null) lastPick = active
    val reduce = YYMotion.isReduceMotion()
    if (active != null || lastPick != null) {
        BoxWithConstraints(Modifier.fillMaxSize()) {
            if (active != null) {
                Box(Modifier.fillMaxSize().clickable(interactionSource = null, indication = null) { model.speakerPick = null })
            }
            val pick = active ?: lastPick!!
            val w = maxWidth
            val h = maxHeight
            val x = (pick.x.toFloat().dp + 4.dp).coerceAtMost(w - 238.dp).coerceAtLeast(8.dp)
            val y = (pick.y.toFloat().dp + 4.dp).coerceAtMost(h - 380.dp).coerceAtLeast(8.dp)
            AnimatedVisibility(
                visible = active != null,
                modifier = Modifier.offset(x = x, y = y),
                enter = fadeIn(YYMotion.quick()) + scaleIn(YYMotion.quick(), initialScale = if (reduce) 1f else 0.92f, transformOrigin = TransformOrigin(0f, 0f)),
                exit = fadeOut(YYMotion.exit()),
            ) {
                SpeakerCard(
                    pick,
                    Modifier,
                    choose = { model.setSpeaker(it, pick.section.toInt(), pick.line) },
                    others = { model.speakerOthers = pick; model.speakerPick = null },
                )
            }
        }
    }
    model.speakerOthers?.let { pick ->
        PeoplePicker(model.core, model.bookID, pick.section, t("是谁说的"), null, { model.speakerOthers = null }) { name ->
            model.setSpeaker(uniffi.yuyue_core.UserChoice.Person(name), pick.section.toInt(), pick.line)
        }
    }
}

/** 回到昨晚: the timer stopped the voice; 回到那里 / 不用. */
@Composable
private fun SleepReturnCard(whenText: String, place: String, paper: YYPaper, haze: HazeState, answer: (Boolean) -> Unit) {
    Row(
        Modifier.padding(bottom = 8.dp).fillMaxWidth().glass(paper, haze, RoundedCornerShape(16.dp)).padding(start = 14.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Text(t("%@定时从 %@ 开始，之后睡着了？", whenText, place), fontSize = 12.5.sp, color = paper.ink, style = YY.tnum, modifier = Modifier.weight(1f))
        app.yuyue.design.YYButton(t("回到那里"), kind = app.yuyue.design.ButtonKind.Main, size = app.yuyue.design.ButtonSize.Small) { answer(true) }
        app.yuyue.design.YYButton(t("不用"), size = app.yuyue.design.ButtonSize.Small) { answer(false) }
    }
}

/** 这一段对得不太准: card above the reading capsule, matching Apple `NarrationFlagCard`. */
@Composable
private fun NarrationFlagCard(flag: ReaderModel.Flag, letBe: () -> Unit, adjust: () -> Unit) {
    val eink = Prefs.isReady && Prefs.bool("eink.on", false)
    Column(
        Modifier.padding(bottom = 8.dp).widthIn(max = 360.dp).fillMaxWidth()
            .shadow(if (eink) 0.dp else 14.dp, RoundedCornerShape(16.dp), ambientColor = Color.Black.copy(alpha = 0.12f), spotColor = Color.Black.copy(alpha = 0.18f))
            .clip(RoundedCornerShape(16.dp)).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
            .padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Box(Modifier.size(6.dp).background(YY.c.warn, CircleShape))
            Text(t("这一段对得不太准"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(t("第 %lld / %lld 处", flag.number.toLong(), flag.total.toLong()), fontSize = 10.sp, color = YY.c.t3, style = YY.tnum)
        }
        val note = if (flag.count > 1) t("这 %lld 句录音只听清一部分，墨线可能跟声音错开。可以先听着，回头再调。", flag.count.toLong())
        else t("这句录音只听清一部分，墨线可能跟声音错开。可以先听着，回头再调。")
        Text(note, fontSize = 11.sp, lineHeight = 16.sp, color = YY.c.t2, modifier = Modifier.padding(top = 6.dp))
        Box(Modifier.padding(top = 10.dp).fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.padding(top = 9.dp).fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
            app.yuyue.design.YYButton(t("先不管"), size = app.yuyue.design.ButtonSize.Small, onClick = letBe)
            app.yuyue.design.YYButton(t("微调"), size = app.yuyue.design.ButtonSize.Small, onClick = adjust)
        }
    }
}

/** 微调: nudges one sentence's start against the recording (`−0.1 秒`, `试听这句`, `+0.1 秒`,
 *  `这一段后面的句子跟着挪`, `<` / `>`), matching Apple `TimingAdjust`. */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
private fun TimingAdjustSheet(model: ReaderModel, initialSection: Int, initialSentence: Int, dismiss: () -> Unit) {
    var sec by remember { mutableStateOf(initialSection) }
    var sen by remember { mutableStateOf(initialSentence) }
    var follow by remember { mutableStateOf(true) }
    var tick by remember { mutableStateOf(0) }
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var previewTrack by remember { mutableStateOf<android.media.AudioTrack?>(null) }
    fun stopPreview() {
        runCatching { previewTrack?.stop(); previewTrack?.release() }
        previewTrack = null
    }
    DisposableEffect(Unit) { onDispose { stopPreview() } }
    val allOrdered = remember(model.bookID, tick) {
        model.library.recordings.timings(model.bookID)?.values?.sortedWith(compareBy({ it.section }, { it.sentence })).orEmpty()
    }
    val cur = allOrdered.firstOrNull { it.section == sec && it.sentence == sen } ?: allOrdered.firstOrNull()
    if (cur == null) {
        LaunchedEffect(Unit) { dismiss() }
        return
    }
    val curIdx = allOrdered.indexOf(cur)
    val sentences = remember(model.bookID, cur.section) {
        runCatching { model.core.sentences(model.bookID, cur.section.toUInt()) }.getOrDefault(emptyList())
    }
    fun stamp(seconds: Double): String {
        val tenths = (maxOf(0.0, seconds) * 10.0).roundToInt()
        val s = tenths / 10
        val head = if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
        return "$head.${tenths % 10}"
    }
    fun shift(delta: Double) {
        stopPreview()
        model.library.recordings.moveStart(model.bookID, cur.section, cur.sentence, cur.start + delta, follow)
        tick++
    }
    fun preview() {
        stopPreview()
        val start = cur.start
        val end = maxOf(cur.end, start + 1.0)
        scope.launch(kotlinx.coroutines.Dispatchers.IO) {
            val clip = model.library.recordings.voice(model.bookID)?.range(start, minOf(end, start + 15.0)) ?: return@launch
            if (clip.samples.isEmpty()) return@launch
            val track = android.media.AudioTrack.Builder()
                .setAudioAttributes(android.media.AudioAttributes.Builder().setUsage(android.media.AudioAttributes.USAGE_MEDIA).setContentType(android.media.AudioAttributes.CONTENT_TYPE_SPEECH).build())
                .setAudioFormat(android.media.AudioFormat.Builder().setEncoding(android.media.AudioFormat.ENCODING_PCM_16BIT).setSampleRate(clip.rate).setChannelMask(android.media.AudioFormat.CHANNEL_OUT_MONO).build())
                .setBufferSizeInBytes(maxOf(clip.samples.size * 2, android.media.AudioTrack.getMinBufferSize(clip.rate, android.media.AudioFormat.CHANNEL_OUT_MONO, android.media.AudioFormat.ENCODING_PCM_16BIT)))
                .setTransferMode(android.media.AudioTrack.MODE_STATIC)
                .build()
            track.write(clip.samples, 0, clip.samples.size)
            kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.Main) {
                previewTrack = track
                runCatching { track.play() }
            }
        }
    }
    androidx.compose.material3.ModalBottomSheet(onDismissRequest = { stopPreview(); dismiss() }, sheetState = androidx.compose.material3.rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { app.yuyue.design.YYSheetDragHandle(bottom = 2.dp) }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            app.yuyue.design.YYSheetHeader(t("微调"), t("第 %lld 章 · 第 %lld 句", (cur.section + 1).toLong(), (cur.sentence + 1).toLong())) { stopPreview(); dismiss() }
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)).padding(12.dp)) {
                val contextText = androidx.compose.ui.text.buildAnnotatedString {
                    sentences.getOrNull(cur.sentence - 1)?.let { pushStyle(androidx.compose.ui.text.SpanStyle(color = YY.c.t3)); append(it); pop() }
                    sentences.getOrNull(cur.sentence)?.let { pushStyle(androidx.compose.ui.text.SpanStyle(color = YY.c.t1, background = YY.tint.accentSoft, fontWeight = FontWeight.Medium)); append(it); pop() }
                    sentences.getOrNull(cur.sentence + 1)?.let { pushStyle(androidx.compose.ui.text.SpanStyle(color = YY.c.t3)); append(it); pop() }
                }
                Text(contextText, fontSize = 13.sp, lineHeight = 20.sp, maxLines = 4, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                Text(t("这句从 %@ 开始", stamp(cur.start)), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent, style = YY.tnum, modifier = Modifier.padding(top = 8.dp))
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                app.yuyue.design.YYButton(t("−0.1 秒"), wide = true, modifier = Modifier.weight(1f)) { shift(-0.1) }
                app.yuyue.design.YYButton(t("试听这句"), kind = app.yuyue.design.ButtonKind.Raised, icon = Icons.play, wide = true, modifier = Modifier.weight(1.4f)) { preview() }
                app.yuyue.design.YYButton(t("+0.1 秒"), wide = true, modifier = Modifier.weight(1f)) { shift(0.1) }
            }
            app.yuyue.design.YYGroup {
                row {
                    Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(t("这一段后面的句子跟着挪"), fontSize = 13.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
                        androidx.compose.material3.Switch(
                            follow, { follow = it },
                            colors = androidx.compose.material3.SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
                        )
                    }
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                app.yuyue.design.YYButton("", icon = Icons.chevronLeft, wide = true, enabled = curIdx > 0, modifier = Modifier.weight(1f).semantics { contentDescription = t("上一句") }) {
                    stopPreview()
                    allOrdered.getOrNull(curIdx - 1)?.let { sec = it.section; sen = it.sentence }
                }
                app.yuyue.design.YYButton("", icon = Icons.chevronRight, wide = true, enabled = curIdx in 0 until allOrdered.lastIndex, modifier = Modifier.weight(1f).semantics { contentDescription = t("下一句") }) {
                    stopPreview()
                    allOrdered.getOrNull(curIdx + 1)?.let { sec = it.section; sen = it.sentence }
                }
                app.yuyue.design.YYButton(t("完成"), kind = app.yuyue.design.ButtonKind.Main, wide = true, modifier = Modifier.weight(1.2f)) { stopPreview(); dismiss() }
            }
        }
    }
}
