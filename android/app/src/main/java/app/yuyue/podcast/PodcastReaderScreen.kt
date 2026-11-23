package app.yuyue.podcast

import android.Manifest
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBars
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBars
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.design.pressable
import app.yuyue.library.Library
import app.yuyue.reader.Dictionary
import app.yuyue.reader.WordDefinition
import app.yuyue.t
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import uniffi.yuyue_core.MagazineSentence
import uniffi.yuyue_core.PodcastEdition
import uniffi.yuyue_core.PodcastInterrupt
import uniffi.yuyue_core.PodcastIssue
import uniffi.yuyue_core.PodcastTrack
import uniffi.yuyue_core.Position
import kotlin.math.max
import kotlin.math.roundToInt

private sealed interface TranscriptBlock {
    data class SkipFold(val index: Int, val reason: String?, val count: Int, val sentences: List<MagazineSentence>) : TranscriptBlock
    data class Turn(val sentence: MagazineSentence, val showSpeaker: Boolean) : TranscriptBlock
}

private data class ActiveVocabCard(
    val word: String,
    val contextSentence: String,
    val definition: WordDefinition?,
    val saved: Boolean,
)

/**
 * Full-screen Audio-Magazine Episode Reader ("访谈书页"):
 * - Top bar (`播客`, `全文 / 精华`, `盲听`, `原声划线`, Clips/Voices drawer) + track bar (`英文原声 / 原声线中文 / 双语助教` when supported).
 * - Performance-isolated transcript `LazyColumn` (never recomposes on 4Hz `hub.currentTime` ticks!).
 * - Word lookup via offline ECDICT (`Dictionary.define`) + `+ 收进生词本` (`core.addWord`).
 * - Book citation anchors (`打开原书 ›`) jumping directly into the e-book section & sentence.
 * - Live microphone cut-in interjection (`语音插话`) with runtime permission, live RMS waveform, and spoken Companion Q&A.
 */
@OptIn(ExperimentalLayoutApi::class, ExperimentalMaterial3Api::class)
@Composable
fun PodcastReaderScreen(
    library: Library,
    issueID: String,
    onClose: () -> Unit,
) {
    val context = LocalContext.current
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val tint = YY.tint

    val issue = hub.issue(issueID)
    if (issue == null) {
        BackHandler(onBack = onClose)
        Box(Modifier.fillMaxSize().background(YY.c.bg), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Icon(Icons.headphones, null, tint = YY.c.t3, modifier = Modifier.size(36.dp))
                Text(t("这期节目不在了"), fontSize = 14.sp, color = YY.c.t2)
                PodcastPaperPill(t("返回收听台"), onClick = onClose)
            }
        }
        return
    }

    var edition by remember(issueID) { mutableStateOf(issue.edition) }
    var track by remember(issueID) { mutableStateOf(issue.track) }
    var listenOnly by remember { mutableStateOf(false) }
    var peekedSentenceID by remember { mutableStateOf<String?>(null) }
    var expandedSkipIndices by remember { mutableStateOf(setOf<Int>()) }
    var clipFlash by remember { mutableStateOf(false) }
    var drawerTab by remember { mutableStateOf<PodcastDrawerTab?>(null) }

    var interrupting by remember { mutableStateOf(false) }
    var interruptQuestion by remember { mutableStateOf("") }
    var sendingInterrupt by remember { mutableStateOf(false) }
    var latestInterruptReply by remember { mutableStateOf<PodcastInterrupt?>(null) }

    var vocabCard by remember { mutableStateOf<ActiveVocabCard?>(null) }
    var toastText by remember { mutableStateOf<String?>(null) }

    fun showToast(msg: String) {
        toastText = msg
    }

    LaunchedEffect(toastText) {
        val current = toastText ?: return@LaunchedEffect
        delay(2200)
        if (toastText == current) toastText = null
    }

    DisposableEffect(issueID) {
        onDispose {
            hub.stopVoiceInterrupt()
            hub.stopSpeakingInterruptAnswer()
        }
    }

    fun closeInterrupt(rewind: Boolean = true) {
        interrupting = false
        latestInterruptReply = null
        hub.endInterruptSession(rewindOnResume = rewind)
    }

    BackHandler {
        when {
            vocabCard != null -> vocabCard = null
            drawerTab != null -> drawerTab = null
            interrupting -> closeInterrupt()
            else -> onClose()
        }
    }

    val allSentences = hub.sentences(issueID)
    val highlightIDs = hub.highlightSet(issueID)
    val interrupts = hub.interrupts(issueID)
    val supportsTrack = hub.supportsTrackSwitch(issue)
    val isActiveIssue = hub.activeIssueID == issueID

    val shownSentences = remember(allSentences, edition, highlightIDs) {
        if (edition == PodcastEdition.HIGHLIGHTS && highlightIDs.isNotEmpty()) {
            allSentences.filter { it.id in highlightIDs }
        } else {
            allSentences
        }
    }

    val blocks = remember(shownSentences, expandedSkipIndices) {
        buildList {
            var i = 0
            var foldIdx = 0
            var prevSpeaker: String? = null
            while (i < shownSentences.size) {
                val s = shownSentences[i]
                if (s.skipReason != null) {
                    val start = i
                    val reason = s.skipReason
                    while (i < shownSentences.size && shownSentences[i].skipReason != null) i++
                    val group = shownSentences.subList(start, i)
                    val currentFold = foldIdx++
                    if (currentFold in expandedSkipIndices) {
                        for (item in group) {
                            val sp = item.speaker?.trim()?.takeIf { it.isNotEmpty() }
                            add(TranscriptBlock.Turn(item, sp != null && sp != prevSpeaker))
                            prevSpeaker = sp
                        }
                    } else {
                        add(TranscriptBlock.SkipFold(currentFold, reason, group.size, group))
                    }
                } else {
                    val sp = s.speaker?.trim()?.takeIf { it.isNotEmpty() }
                    add(TranscriptBlock.Turn(s, sp != null && sp != prevSpeaker))
                    prevSpeaker = sp
                    i++
                }
            }
        }
    }

    // Active sentence ID WITHOUT reading 4Hz `hub.currentTime`!
    val currentActiveSentenceID = remember(isActiveIssue, hub.activeSentenceID, shownSentences, issue.positionSec) {
        val spoken = shownSentences.filter { it.skipReason == null }
        if (isActiveIssue && hub.activeSentenceID != null) {
            hub.activeSentenceID
        } else {
            val pos = issue.positionSec
            spoken.firstOrNull { pos >= it.t0 && pos <= it.t1 }?.id
                ?: spoken.lastOrNull { pos >= it.t0 }?.id
                ?: spoken.firstOrNull()?.id
        }
    }

    val listState = rememberLazyListState()
    LaunchedEffect(currentActiveSentenceID, hub.isPlaying) {
        if (!isActiveIssue || !hub.isPlaying || listState.isScrollInProgress) return@LaunchedEffect
        val targetID = currentActiveSentenceID ?: return@LaunchedEffect
        val idx = blocks.indexOfFirst { (it as? TranscriptBlock.Turn)?.sentence?.id == targetID }
        if (idx >= 0) {
            // +1 accounts for the editorial header item at index 0
            val itemIndex = idx + 1
            val visible = listState.layoutInfo.visibleItemsInfo.map { it.index }
            if (itemIndex !in visible) {
                runCatching { listState.animateScrollToItem(max(0, itemIndex - 1)) }
            }
        }
    }

    val micPermissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) {
            hub.startVoiceInterrupt { partial -> interruptQuestion = partial }
        } else {
            showToast(t("需要麦克风权限才能语音插话，你也可以直接打字提问"))
        }
    }

    Box(Modifier.fillMaxSize().background(YY.c.bg)) {
        Column(Modifier.fillMaxSize()) {
            // 1. Top Bar + Track Bar
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(YY.c.bg)
                    .windowInsetsPadding(WindowInsets.statusBars),
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 14.dp, vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Row(
                        modifier = Modifier
                            .clip(RoundedCornerShape(8.dp))
                            .clickable(onClick = onClose)
                            .padding(horizontal = 4.dp, vertical = 5.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        Icon(Icons.chevronLeft, t("返回"), tint = YY.c.t2, modifier = Modifier.size(18.dp))
                        Text(t("播客"), fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t2)
                    }

                    Spacer(Modifier.weight(1f))

                    if (highlightIDs.isNotEmpty()) {
                        PodcastPaperPill(
                            title = if (edition == PodcastEdition.HIGHLIGHTS) t("精华") else t("全文"),
                            accent = edition == PodcastEdition.HIGHLIGHTS,
                            height = 26.dp,
                            horizontalPadding = 10.dp,
                        ) {
                            val next = if (edition == PodcastEdition.FULL) PodcastEdition.HIGHLIGHTS else PodcastEdition.FULL
                            edition = next
                            hub.setViewMode(issue.id, next, track)
                        }
                    }

                    PodcastPaperPill(
                        title = if (listenOnly) t("退出盲听") else t("盲听"),
                        accent = listenOnly,
                        height = 26.dp,
                        horizontalPadding = 10.dp,
                    ) {
                        listenOnly = !listenOnly
                        peekedSentenceID = null
                        showToast(if (listenOnly) t("已开启盲听 · 轻点任意句子可偷看") else t("已退出盲听"))
                    }

                    PodcastPaperPill(
                        title = if (clipFlash) t("已记原声") else t("原声划线"),
                        icon = if (clipFlash) Icons.bookmark else Icons.bookmarkOutline,
                        accent = true,
                        height = 26.dp,
                        horizontalPadding = 10.dp,
                    ) {
                        scope.launch {
                            if (!isActiveIssue) {
                                hub.start(issue, from = issue.positionSec)
                                hub.pause()
                            }
                            if (hub.markCurrentMoment() != null) {
                                clipFlash = true
                                showToast(t("已记下原声书摘"))
                                delay(1400)
                                clipFlash = false
                            }
                        }
                    }

                    PodcastCircleIconButton(
                        icon = Icons.list,
                        label = t("原声书摘与声音卡"),
                        size = 28.dp,
                        iconSize = 15.dp,
                    ) {
                        drawerTab = PodcastDrawerTab.Clips
                    }
                }

                HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

                // Track / Fidelity Sub-Bar
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(YY.c.card)
                        .padding(horizontal = 14.dp, vertical = 7.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (supportsTrack) {
                        PodcastTrackSegmentedControl(track, issue.language) { newTrack ->
                            track = newTrack
                            vocabCard = null
                            hub.setViewMode(issue.id, edition, newTrack)
                            hub.issue(issue.id)?.let { hub.setTrack(newTrack, it) }
                            showToast(t("同句无缝切换：%@", PodcastFormat.trackLabel(newTrack, issue.language)))
                        }
                        Spacer(Modifier.weight(1f))
                        Text(t("同句无缝切轨"), fontSize = 11.sp, color = YY.c.t3)
                    } else {
                        val accentNote = when {
                            issue.studioKind != null -> "${issue.studioKind!!.title} · ${t("防剧透锁")}"
                            shownSentences.isEmpty() -> t("原声播放中")
                            else -> t("原声 · 逐字稿对齐")
                        }
                        Text(accentNote, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = tint.accent)
                        Spacer(Modifier.weight(1f))
                        if (shownSentences.isEmpty() && !issue.localAudioPath.isNullOrBlank()) {
                            val transcribing = hub.isTranscribingIssueID == issue.id
                            PodcastPaperPill(
                                title = if (transcribing) t("转写中…") else t("本机转文字"),
                                icon = Icons.waveform,
                                accent = true,
                                enabled = !transcribing,
                                height = 24.dp,
                                horizontalPadding = 9.dp,
                            ) {
                                scope.launch { hub.transcribeOnDevice(issue.id) }
                            }
                        } else {
                            Text(
                                if (issue.studioKind != null) t("防剧透锁生效") else t("点任意句子即刻跳播"),
                                fontSize = 11.sp,
                                color = YY.c.t3,
                            )
                        }
                    }
                }
                HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)
            }

            // 2. Editorial Magazine Sheet (`LazyColumn`)
            LazyColumn(
                state = listState,
                modifier = Modifier.weight(1f).fillMaxWidth(),
                contentPadding = PaddingValues(start = 18.dp, end = 18.dp, top = 20.dp, bottom = 36.dp),
            ) {
                // Header item
                item(key = "header") {
                    Column(Modifier.fillMaxWidth().widthIn(max = 650.dp)) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                            modifier = Modifier.padding(bottom = 10.dp),
                        ) {
                            PodcastShowMark(
                                title = if (issue.studioKind != null) t("书房电台") else issue.showTitle,
                                artworkURL = issue.artworkUrl,
                                isStudio = issue.studioKind != null,
                                size = 20.dp,
                            )
                            Text(
                                issue.showTitle,
                                fontSize = 12.sp,
                                color = YY.c.t3,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f, fill = false),
                            )
                            Text("·", fontSize = 12.sp, color = YY.c.t3, maxLines = 1)
                            Text(PodcastFormat.compactDuration(issue.durationSec), fontSize = 12.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
                        }

                        Text(
                            issue.title,
                            fontFamily = YY.serif,
                            fontWeight = FontWeight.Medium,
                            fontSize = 23.sp,
                            lineHeight = 31.sp,
                            color = YY.c.t1,
                        )

                        if (issue.chapters.isNotEmpty()) {
                            Row(
                                modifier = Modifier
                                    .padding(top = 12.dp)
                                    .fillMaxWidth()
                                    .horizontalScroll(rememberScrollState()),
                                horizontalArrangement = Arrangement.spacedBy(6.dp),
                            ) {
                                for (ch in issue.chapters) {
                                    PodcastPaperPill(
                                        title = "${PodcastFormat.clock(ch.startSec)} ${ch.title}",
                                        height = 25.dp,
                                        horizontalPadding = 10.dp,
                                    ) {
                                        hub.start(issue, from = ch.startSec)
                                    }
                                }
                            }
                        }

                        HorizontalDivider(
                            color = YY.c.line,
                            thickness = 0.5.dp,
                            modifier = Modifier.padding(top = 16.dp, bottom = 14.dp),
                        )

                        if (shownSentences.isEmpty()) {
                            L0ShownotesView(library, issue)
                        }
                    }
                }

                // Transcript blocks
                itemsIndexed(
                    items = blocks,
                    key = { _, b ->
                        when (b) {
                            is TranscriptBlock.SkipFold -> "fold-${b.index}"
                            is TranscriptBlock.Turn -> b.sentence.id
                        }
                    }
                ) { _, block ->
                    when (block) {
                        is TranscriptBlock.SkipFold -> {
                            Row(
                                modifier = Modifier
                                    .padding(vertical = 8.dp)
                                    .fillMaxWidth()
                                    .clip(RoundedCornerShape(10.dp))
                                    .background(YY.c.inset)
                                    .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp))
                                    .clickable { expandedSkipIndices = expandedSkipIndices + block.index }
                                    .padding(horizontal = 12.dp, vertical = 8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(
                                    t("已静默折叠 %lld 段%@ · 展开", block.count, PodcastFormat.skipReasonLabel(block.reason)),
                                    fontSize = 12.sp,
                                    color = YY.c.t3,
                                )
                            }
                        }

                        is TranscriptBlock.Turn -> {
                            val s = block.sentence
                            val isCurrent = currentActiveSentenceID == s.id
                            val blurred = listenOnly && !isCurrent && peekedSentenceID != s.id
                            val turnInterrupts = remember(interrupts, s.t0, s.t1) {
                                interrupts.filter { it.atSec >= s.t0 - 0.5 && it.atSec <= s.t1 + 0.5 }
                            }

                            TranscriptTurnItem(
                                sentence = s,
                                track = track,
                                language = issue.language,
                                showSpeaker = block.showSpeaker,
                                isActive = isCurrent,
                                isBlurred = blurred,
                                interrupts = turnInterrupts,
                                onTapSentence = {
                                    if (blurred) {
                                        peekedSentenceID = s.id
                                    } else {
                                        hub.start(issue, from = s.t0)
                                    }
                                },
                                onOpenBook = { bookId, sec, sen ->
                                    val book = library.book(bookId)
                                    if (book != null && book.available) {
                                        onClose()
                                        library.open(bookId, Position(sec ?: 0u, sen ?: 0u))
                                    } else {
                                        showToast(t("原书《%@》不在当前设备书架上", s.bookTitle ?: ""))
                                    }
                                },
                                onLookupWord = { rawWord ->
                                    scope.launch {
                                        val def = Dictionary.define(context, rawWord)
                                        val head = def?.headword ?: rawWord.lowercase().trim('\'', '’', '.', ',', '?', '!')
                                        val already = runCatching {
                                            library.core.words().any { it.word.equals(head, ignoreCase = true) && !it.known }
                                        }.getOrDefault(false)
                                        vocabCard = ActiveVocabCard(head, s.textOrig, def, already)
                                    }
                                },
                            )
                        }
                    }
                }
            }

            // 3. Performance-Isolated Bottom Slim Player Dock
            PodcastReaderPlayerDock(
                library = library,
                issue = issue,
                onStartInterrupt = {
                    interrupting = true
                    hub.beginInterruptSession()
                },
            )
        }

        // Inline Vocabulary Lookup Card
        val reduceMotion = YYMotion.isReduceMotion()
        AnimatedVisibility(
            visible = vocabCard != null,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(horizontal = 16.dp, vertical = 92.dp),
            enter = if (reduceMotion) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + slideInVertically(YYMotion.quick()) { it / 3 },
            exit = if (reduceMotion) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + slideOutVertically(YYMotion.exit()) { it / 3 },
        ) {
            vocabCard?.let { card ->
                PodcastStationeryCard(
                    modifier = Modifier.widthIn(max = 400.dp),
                    cornerRadius = 15.dp,
                ) {
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Text(card.word, fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = 18.sp, color = YY.c.t1)
                            card.definition?.phonetic?.let { ph ->
                                Text(ph, fontSize = 12.5.sp, color = YY.c.t3)
                            }
                            Spacer(Modifier.weight(1f))
                            PodcastCircleIconButton(Icons.close, t("关闭"), size = 26.dp, iconSize = 13.dp) {
                                vocabCard = null
                            }
                        }
                        Text(
                            card.definition?.lines?.joinToString("\n") ?: t("词典中暂无离线释义，仍可收进生词本"),
                            fontSize = 13.sp,
                            lineHeight = 19.sp,
                            color = YY.c.t2,
                        )
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(t("播客语境点词"), fontSize = 11.sp, color = YY.c.t3)
                            Spacer(Modifier.weight(1f))
                            PodcastPaperPill(
                                title = if (card.saved) t("已在生词本") else t("+ 收进生词本"),
                                accent = !card.saved,
                                enabled = !card.saved,
                                height = 26.dp,
                            ) {
                                runCatching {
                                    library.core.addWord(
                                        card.word,
                                        card.definition?.phonetic,
                                        card.definition?.line,
                                        issue.bookIds.firstOrNull() ?: "podcast:${issue.id}",
                                        0u,
                                        0u,
                                        card.contextSentence.take(180),
                                    )
                                }
                                library.notesChanged()
                                vocabCard = card.copy(saved = true)
                                showToast(t("已把「%@」收进生词本", card.word))
                            }
                        }
                    }
                }
            }
        }

        // Floating Toast
        AnimatedVisibility(
            visible = toastText != null,
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = 96.dp),
            enter = fadeIn(YYMotion.quick()),
            exit = fadeOut(YYMotion.exit()),
        ) {
            toastText?.let { msg ->
                Box(
                    Modifier
                        .clip(CircleShape)
                        .background(YY.c.t1)
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                ) {
                    Text(msg, fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.card)
                }
            }
        }
    }

    if (interrupting) {
        ModalBottomSheet(
            onDismissRequest = { closeInterrupt(rewind = true) },
            sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
            containerColor = YY.c.card,
            shape = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp),
            dragHandle = null,
        ) {
            PodcastInterruptSheetCard(
                library = library,
                issue = issue,
                question = interruptQuestion,
                onQuestionChange = { interruptQuestion = it },
                sending = sendingInterrupt,
                latestReply = latestInterruptReply,
                onMicTap = {
                    if (hub.isListeningInterrupt) {
                        hub.stopVoiceInterrupt()
                    } else if (hub.hasMicPermission()) {
                        hub.startVoiceInterrupt { partial -> interruptQuestion = partial }
                    } else {
                        micPermissionLauncher.launch(Manifest.permission.RECORD_AUDIO)
                    }
                },
                onSend = {
                    val q = interruptQuestion.trim()
                    if (q.isNotEmpty() && !sendingInterrupt) {
                        sendingInterrupt = true
                        scope.launch {
                            val reply = hub.submitInterrupt(issue, q, speakAnswer = true)
                            latestInterruptReply = reply
                            sendingInterrupt = false
                            interruptQuestion = ""
                        }
                    }
                },
                onResume = { closeInterrupt(rewind = true) },
            )
        }
    }

    drawerTab?.let { initialTab ->
        PodcastDrawerSheet(
            library = library,
            initialTab = initialTab,
            onOpenIssue = { targetId ->
                drawerTab = null
                if (targetId != issue.id) {
                    hub.requestedOpenIssueID = targetId
                }
            },
            onDismiss = { drawerTab = null },
        )
    }
}

@Composable
private fun L0ShownotesView(library: Library, issue: PodcastIssue) {
    val hub = library.podcastHub
    val scope = rememberCoroutineScope()
    val body = issue.shownotes.ifBlank { issue.summary }
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        if (hub.lastNotice != null) {
            Text(hub.lastNotice!!, fontSize = 12.sp, color = YY.tint.accent)
        }
        PodcastStationeryCard(cornerRadius = 14.dp) {
            Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(
                    if (!issue.localAudioPath.isNullOrBlank()) t("本机音频 · 可离线生成逐字稿")
                    else t("节目单集导览"),
                    fontSize = 13.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = YY.c.t1,
                )
                Text(
                    body.ifBlank { t("这期节目暂未附带公开逐字稿。你依然可以直接收听，随时点右上角「原声划线」或下方「插话」记下灵感。") },
                    fontFamily = YY.serif,
                    fontSize = 14.5.sp,
                    lineHeight = 23.sp,
                    color = YY.c.t2,
                )
                if (!issue.localAudioPath.isNullOrBlank()) {
                    val busy = hub.isTranscribingIssueID == issue.id
                    PodcastPaperPill(
                        title = if (busy) t("正在本机识别逐字稿…") else t("用 SenseVoice 本机转出逐字稿"),
                        icon = Icons.waveform,
                        accent = true,
                        enabled = !busy,
                        height = 28.dp,
                    ) {
                        scope.launch { hub.transcribeOnDevice(issue.id) }
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TranscriptTurnItem(
    sentence: MagazineSentence,
    track: PodcastTrack,
    language: String,
    showSpeaker: Boolean,
    isActive: Boolean,
    isBlurred: Boolean,
    interrupts: List<PodcastInterrupt>,
    onTapSentence: () -> Unit,
    onOpenBook: (String, UInt?, UInt?) -> Unit,
    onLookupWord: (String) -> Unit,
) {
    val tint = YY.tint
    val bg = if (isActive) tint.accentSoft.copy(alpha = if (YY.dark) 0.55f else 0.72f) else Color.Transparent

    // Extract candidate English vocabulary words (length >= 6) for 1-tap lookup pills when active
    val englishWords = remember(sentence.textOrig, language, isActive) {
        if (!isActive || !language.lowercase().startsWith("en")) emptyList()
        else Regex("[A-Za-z][A-Za-z'-]{5,}")
            .findAll(sentence.textOrig)
            .map { it.value }
            .distinctBy { it.lowercase() }
            .take(5)
            .toList()
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .widthIn(max = 650.dp)
            .clip(RoundedCornerShape(11.dp))
            .background(bg)
            .clickable(onClick = onTapSentence)
            .padding(horizontal = 10.dp, vertical = 9.dp),
    ) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.Top) {
            Text(
                PodcastFormat.clock(sentence.t0),
                fontSize = 11.sp,
                fontWeight = if (isActive) FontWeight.SemiBold else FontWeight.Normal,
                color = if (isActive) tint.accent else YY.c.t3,
                style = YY.tnum,
                modifier = Modifier.width(40.dp).padding(top = 3.dp),
            )

            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                if (showSpeaker && !sentence.speaker.isNullOrBlank()) {
                    Text(
                        sentence.speaker!!,
                        fontSize = 11.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (isActive) tint.accent else YY.c.t2,
                    )
                }

                val blurMod = if (isBlurred) Modifier.blur(7.dp).alpha(0.35f) else Modifier
                Column(blurMod, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    when (track) {
                        PodcastTrack.ORIGINAL -> {
                            Text(
                                sentence.textOrig,
                                fontFamily = YY.serif,
                                fontSize = 16.sp,
                                lineHeight = 25.sp,
                                color = YY.c.t1,
                            )
                        }
                        PodcastTrack.DUB -> {
                            Text(
                                sentence.textTrans?.takeIf { it.isNotBlank() } ?: sentence.textOrig,
                                fontFamily = YY.serif,
                                fontSize = 16.sp,
                                lineHeight = 25.sp,
                                color = YY.c.t1,
                            )
                        }
                        PodcastTrack.COACH -> {
                            Text(
                                sentence.textOrig,
                                fontFamily = YY.serif,
                                fontSize = 15.5.sp,
                                lineHeight = 24.sp,
                                color = YY.c.t1,
                            )
                            sentence.textTrans?.takeIf { it.isNotBlank() && it != sentence.textOrig }?.let { zh ->
                                Text(zh, fontSize = 13.5.sp, lineHeight = 20.sp, color = YY.c.t2)
                            }
                            sentence.coachNote?.takeIf { it.isNotBlank() }?.let { note ->
                                Text(
                                    note,
                                    fontSize = 12.sp,
                                    lineHeight = 18.sp,
                                    color = tint.accent,
                                    modifier = Modifier
                                        .padding(top = 2.dp)
                                        .clip(RoundedCornerShape(8.dp))
                                        .background(tint.accentSoft)
                                        .padding(horizontal = 9.dp, vertical = 5.dp),
                                )
                            }
                        }
                    }
                }

                // Quick English vocabulary lookup chips on the active sentence
                if (englishWords.isNotEmpty() && !isBlurred) {
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp),
                        modifier = Modifier.padding(top = 2.dp),
                    ) {
                        for (w in englishWords) {
                            Text(
                                w,
                                fontSize = 11.5.sp,
                                color = YY.c.t2,
                                textDecoration = TextDecoration.Underline,
                                modifier = Modifier
                                    .clip(RoundedCornerShape(6.dp))
                                    .background(YY.c.card.copy(alpha = 0.8f))
                                    .border(0.5.dp, YY.c.line, RoundedCornerShape(6.dp))
                                    .clickable { onLookupWord(w) }
                                    .padding(horizontal = 7.dp, vertical = 2.dp),
                            )
                        }
                    }
                }

                // Book citation anchor pill (`打开原书 ›`)
                if (!sentence.bookTitle.isNullOrBlank()) {
                    Row(
                        modifier = Modifier
                            .padding(top = 3.dp)
                            .clip(CircleShape)
                            .background(YY.c.card)
                            .border(0.5.dp, YY.c.pillLine, CircleShape)
                            .clickable {
                                val bid = sentence.bookId
                                if (!bid.isNullOrBlank()) onOpenBook(bid, sentence.bookSection, sentence.bookSentence)
                            }
                            .padding(horizontal = 10.dp, vertical = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(5.dp),
                    ) {
                        Icon(Icons.book, null, tint = tint.accent, modifier = Modifier.size(12.dp))
                        Text(
                            t("《%@》 · 打开原书 ›", sentence.bookTitle!!),
                            fontSize = 11.5.sp,
                            fontWeight = FontWeight.Medium,
                            color = tint.accent,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }

                // Saved cut-in Q&A notes attached to this timestamp
                for (intr in interrupts) {
                    Column(
                        modifier = Modifier
                            .padding(top = 6.dp)
                            .fillMaxWidth()
                            .clip(RoundedCornerShape(10.dp))
                            .background(YY.c.card)
                            .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp))
                            .padding(horizontal = 11.dp, vertical = 8.dp),
                        verticalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        Text("${t("插话")} · ${intr.question}", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tint.accent)
                        Text(intr.answer, fontSize = 12.5.sp, lineHeight = 18.sp, color = YY.c.t2)
                    }
                }
            }
        }
    }
}

/**
 * Isolated recomposition scope for 4Hz (`250ms`) `hub.currentTime` updates so the transcript
 * `LazyColumn` above stays completely untouched while audio plays.
 */
@Composable
private fun PodcastReaderPlayerDock(
    library: Library,
    issue: PodcastIssue,
    onStartInterrupt: () -> Unit,
) {
    val hub = library.podcastHub
    val tint = YY.tint
    val isActive = hub.activeIssueID == issue.id
    val isPlaying = isActive && hub.isPlaying

    var scrubPos by remember { mutableStateOf<Float?>(null) }
    var rateMenu by remember { mutableStateOf(false) }

    val liveTime = if (isActive) hub.currentTime else issue.positionSec
    val totalDur = max(
        if (isActive && hub.duration > 0) hub.duration else issue.durationSec.toDouble(),
        liveTime,
    )
    val shownTime = scrubPos?.toDouble() ?: liveTime

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(YY.c.card)
            .windowInsetsPadding(WindowInsets.navigationBars),
    ) {
        HorizontalDivider(color = YY.c.line, thickness = 0.5.dp)

        Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(PodcastFormat.clock(shownTime), fontSize = 11.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.width(42.dp))
                Slider(
                    value = if (totalDur > 0) (shownTime / totalDur).toFloat().coerceIn(0f, 1f) else 0f,
                    onValueChange = { frac -> scrubPos = (frac * totalDur).toFloat() },
                    onValueChangeFinished = {
                        scrubPos?.let { target ->
                            if (!isActive) hub.start(issue, from = target.toDouble())
                            else hub.seek(target.toDouble())
                        }
                        scrubPos = null
                    },
                    colors = SliderDefaults.colors(
                        thumbColor = YY.c.t1,
                        activeTrackColor = tint.accent,
                        inactiveTrackColor = YY.c.inset,
                    ),
                    modifier = Modifier.weight(1f).height(24.dp),
                )
                Text(
                    PodcastFormat.clock(totalDur),
                    fontSize = 11.sp,
                    color = YY.c.t3,
                    style = YY.tnum,
                    modifier = Modifier.width(42.dp),
                )
            }

            Row(
                modifier = Modifier.fillMaxWidth().padding(top = 2.dp, bottom = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween,
            ) {
                // Speed selector
                Box {
                    PodcastPaperPill(
                        title = String.format(java.util.Locale.US, "%.2g×", hub.rate),
                        height = 28.dp,
                        horizontalPadding = 10.dp,
                    ) { rateMenu = true }
                    DropdownMenu(rateMenu, { rateMenu = false }, containerColor = YY.c.card, shape = RoundedCornerShape(14.dp)) {
                        for (r in listOf(0.8f, 0.9f, 1.0f, 1.25f, 1.5f, 1.75f, 2.0f)) {
                            val label = String.format(java.util.Locale.US, "%.2g×", r)
                            DropdownMenuItem(
                                text = { Text(label, color = YY.c.t1, style = YY.tnum) },
                                trailingIcon = { if (kotlin.math.abs(hub.rate - r) < 0.03f) Icon(Icons.check, null, tint = tint.accent, modifier = Modifier.size(16.dp)) },
                                onClick = { hub.setPlaybackRate(r); rateMenu = false },
                            )
                        }
                    }
                }

                // Transport controls
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    Box(
                        Modifier.size(38.dp).clip(CircleShape).clickable { hub.skip(-15.0) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.back15, t("后退 15 秒"), tint = YY.c.t1, modifier = Modifier.size(21.dp))
                    }
                    Box(
                        modifier = Modifier
                            .size(44.dp)
                            .clip(CircleShape)
                            .background(YY.c.t1)
                            .pressable(label = if (isPlaying) t("暂停") else t("播放"), scale = 0.93f) {
                                hub.selectAndPlay(issue)
                            },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(
                            if (isPlaying) Icons.pause else Icons.play,
                            contentDescription = null,
                            tint = YY.c.card,
                            modifier = Modifier.size(22.dp),
                        )
                    }
                    Box(
                        Modifier.size(38.dp).clip(CircleShape).clickable { hub.skip(15.0) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text("+15s", fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, style = YY.tnum)
                    }
                }

                // Cut-in Q&A ("插话") trigger
                PodcastPaperPill(
                    title = t("插话"),
                    icon = Icons.microphoneFill,
                    accent = true,
                    height = 30.dp,
                    horizontalPadding = 12.dp,
                    onClick = onStartInterrupt,
                )
            }
        }
    }
}

@Composable
private fun PodcastInterruptSheetCard(
    library: Library,
    issue: PodcastIssue,
    question: String,
    onQuestionChange: (String) -> Unit,
    sending: Boolean,
    latestReply: PodcastInterrupt?,
    onMicTap: () -> Unit,
    onSend: () -> Unit,
    onResume: () -> Unit,
) {
    val hub = library.podcastHub
    val tint = YY.tint
    val shape = RoundedCornerShape(topStart = 20.dp, topEnd = 20.dp)

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .background(YY.c.card)
            .imePadding()
            .padding(start = 18.dp, end = 18.dp, top = 10.dp, bottom = 18.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Box(
            Modifier
                .align(Alignment.CenterHorizontally)
                .width(36.dp)
                .height(4.dp)
                .clip(CircleShape)
                .background(YY.c.line)
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(t("边听边插话"), fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = 16.sp, color = YY.c.t1)
                Text(
                    hub.interruptVoiceHint ?: t("节目已压低音量 · 说完或问完后自动回退 3 秒接着听"),
                    fontSize = 11.5.sp,
                    color = if (hub.isListeningInterrupt) tint.accent else YY.c.t3,
                )
            }
            PodcastPaperPill(t("继续听"), height = 26.dp, onClick = onResume)
        }

        // Live 5-bar voice level indicator when microphone is capturing
        if (hub.isListeningInterrupt) {
            val lvl = hub.interruptVoiceLevel.coerceIn(0.08f, 1f)
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .height(28.dp)
                    .clip(RoundedCornerShape(9.dp))
                    .background(tint.accentSoft)
                    .padding(horizontal = 14.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                for (mult in listOf(0.55f, 0.85f, 1.0f, 0.75f, 0.5f)) {
                    Box(
                        Modifier
                            .width(3.dp)
                            .height((20f * (lvl * mult).coerceIn(0.18f, 1f)).dp)
                            .clip(CircleShape)
                            .background(tint.accent)
                    )
                }
                Text(t("正在听写你的问题…"), fontSize = 12.sp, color = tint.accent, modifier = Modifier.padding(start = 6.dp))
            }
        }

        // Answer card when a reply has arrived
        latestReply?.let { reply ->
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(12.dp))
                    .background(YY.c.bg)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(12.dp))
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp),
            ) {
                Text(reply.question, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = tint.accent)
                Text(reply.answer, fontFamily = YY.serif, fontSize = 14.sp, lineHeight = 21.sp, color = YY.c.t1)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    PodcastPaperPill(
                        title = if (hub.isSpeakingInterruptReply) t("停止播报") else t("重听回答"),
                        icon = if (hub.isSpeakingInterruptReply) Icons.pause else Icons.play,
                        height = 25.dp,
                    ) {
                        if (hub.isSpeakingInterruptReply) hub.stopSpeakingInterruptAnswer()
                        else hub.speakInterruptAnswer(reply.answer)
                    }
                    Spacer(Modifier.weight(1f))
                    PodcastPaperPill(t("回退 3 秒继续听"), accent = true, height = 25.dp, onClick = onResume)
                }
            }
        }

        // Input row + Mic button + Send button
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(38.dp)
                    .clip(CircleShape)
                    .background(if (hub.isListeningInterrupt) tint.accent else tint.accentSoft)
                    .pressable(label = t("语音插话"), scale = 0.93f, onClick = onMicTap),
                contentAlignment = Alignment.Center,
            ) {
                Icon(
                    if (hub.isListeningInterrupt) Icons.stop else Icons.microphoneFill,
                    contentDescription = t("语音插话"),
                    tint = if (hub.isListeningInterrupt) YY.c.card else tint.accent,
                    modifier = Modifier.size(18.dp),
                )
            }

            Box(
                modifier = Modifier
                    .weight(1f)
                    .heightIn(min = 38.dp)
                    .clip(RoundedCornerShape(11.dp))
                    .background(YY.c.inset)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(11.dp))
                    .padding(horizontal = 12.dp, vertical = 9.dp),
                contentAlignment = Alignment.CenterStart,
            ) {
                BasicTextField(
                    value = question,
                    onValueChange = onQuestionChange,
                    textStyle = TextStyle(fontSize = 13.5.sp, color = YY.c.t1),
                    cursorBrush = SolidColor(tint.accent),
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                    keyboardActions = KeyboardActions(onSend = { onSend() }),
                    modifier = Modifier.fillMaxWidth(),
                    decorationBox = { inner ->
                        if (question.isEmpty()) {
                            Text(t("点左侧麦克风开口问，或直接输入问题…"), fontSize = 13.sp, color = YY.c.t3)
                        }
                        inner()
                    },
                )
            }

            Box(
                modifier = Modifier
                    .size(38.dp)
                    .clip(CircleShape)
                    .background(if (question.isNotBlank() && !sending) YY.c.t1 else YY.c.inset)
                    .pressable(label = t("提问"), enabled = question.isNotBlank() && !sending, scale = 0.93f, onClick = onSend),
                contentAlignment = Alignment.Center,
            ) {
                if (sending) {
                    CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.c.t2)
                } else {
                    Icon(
                        Icons.send,
                        contentDescription = t("提问"),
                        tint = if (question.isNotBlank()) YY.c.card else YY.c.t3,
                        modifier = Modifier.size(17.dp),
                    )
                }
            }
        }
    }
}
