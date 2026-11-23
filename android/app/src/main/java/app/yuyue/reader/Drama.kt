package app.yuyue.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.window.Dialog
import app.yuyue.companion.Companion
import app.yuyue.companion.CompanionConfig
import app.yuyue.companion.CompanionError
import app.yuyue.companion.CompanionUsage
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYSegments
import app.yuyue.t
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.BubbleLine
import uniffi.yuyue_core.CastEdit
import uniffi.yuyue_core.CastPick
import uniffi.yuyue_core.CharacterGender
import uniffi.yuyue_core.DramaCaps
import uniffi.yuyue_core.DramaProgress
import uniffi.yuyue_core.DramaResult
import uniffi.yuyue_core.DramaSpeaker
import uniffi.yuyue_core.DramaTask
import uniffi.yuyue_core.SameSuggestion
import uniffi.yuyue_core.SegKind
import uniffi.yuyue_core.UnsureLine
import uniffi.yuyue_core.UserChoice
import uniffi.yuyue_core.YuyueCore

// 分角色 · 认人和改人 (as on iPhone): reading the book with the model the user added for 书伴,
// what's known on top of the cast, the lines nobody is sure of, the card for who says a line, and
// someone's names.

private fun hex(s: String): Color = runCatching { Color(android.graphics.Color.parseColor(s)) }.getOrDefault(Color.Gray)

/** 认人: the core hands out the work, this asks the model and hands the answers back; the
 *  chapter being read first, two questions at a time, until done or paused. */
object DramaWork {
    sealed interface State {
        data object Idle : State
        data object Running : State
        data object Paused : State
        data class Stopped(val why: String) : State
        data object Done : State
    }

    sealed interface Scope {
        data object WholeBook : Scope
        data class SectionOnly(val section: UInt) : Scope
    }

    data class Book(val state: State = State.Idle, val progress: DramaProgress? = null, val sentChars: Int = 0)

    val books = mutableStateMapOf<String, Book>()
    /** A book whose bubbles, cast and voices should follow a new answer. */
    val updated = MutableSharedFlow<String>(extraBufferCapacity = 16)
    private val jobs = HashMap<String, Job>()
    private val focus = HashMap<String, UInt>()
    private val scopes = HashMap<String, Scope>()
    private val asking = HashMap<String, Int>()
    private val scope: CoroutineScope = MainScope()

    /** The model 书伴 uses, when it can do this. */
    val config: CompanionConfig? get() = CompanionConfig.current?.takeIf { it.isReady }

    /** Clips are being made (广播剧): the core may hand out voice work too. */
    var synthesizing = false

    /** The play being made is cast with cloud voices: a clip made again comes out different, so
     *  回听 holds them to the full test. */
    @Volatile var cloudCast = false

    fun tellCore(core: YuyueCore) {
        val c = config
        val appInst = runCatching { app.yuyue.YuyueApp.instance }.getOrNull()
        val liveReady = appInst != null && app.yuyue.speech.LiveVoiceModel.canRun(appInst.library.dataDir)
        val engine = when {
            cloudCast -> "android-cloud-voices-1"
            liveReady -> app.yuyue.speech.DramaSynth.LIVE_ENGINE
            else -> "android-system-voices-1"
        }
        val tts = synthesizing || liveReady
        val sameEachTime = !cloudCast && !liveReady
        core.dramaSetCaps(DramaCaps(c != null, false, 120_000u, tts, false, synthesizing && app.yuyue.speech.SfxMaker.available, c?.let { "${it.provider.id}/${it.model}" } ?: "", engine, sameEachTime))
    }

    fun book(id: String) = books[id] ?: Book()

    fun isSectionOnly(id: String): Boolean = scopes[id] is Scope.SectionOnly

    fun reading(id: String, section: UInt) { focus[id] = section }

    suspend fun refresh(core: YuyueCore, id: String) {
        val p = withContext(Dispatchers.IO) { runCatching { core.dramaProgress(id) }.getOrNull() }
        var b = book(id).copy(progress = p)
        if (b.state == State.Idle && p != null && p.castReady && p.sectionsDone == p.sections) b = b.copy(state = State.Done)
        books[id] = b
    }

    fun start(core: YuyueCore, id: String, section: UInt, runScope: Scope = Scope.WholeBook) {
        if (jobs[id]?.isActive == true) return
        tellCore(core)
        focus[id] = section
        scopes[id] = runScope
        books[id] = book(id).copy(state = State.Running)
        jobs[id] = scope.launch {
            val workers = List(2) { launch { worker(core, id) } }
            workers.forEach { it.join() }
            jobs.remove(id)
            val finishedScope = scopes.remove(id)
            if (book(id).state == State.Running) {
                val nextState = if (finishedScope is Scope.SectionOnly) State.Idle else State.Done
                books[id] = book(id).copy(state = nextState)
            }
            refresh(core, id)
        }
    }

    private fun taskMatchesSection(taskID: String, bookID: String, target: UInt): Boolean {
        val needle = ":$bookID:$target"
        val idx = taskID.indexOf(needle)
        if (idx < 0) return false
        val end = idx + needle.length
        return end == taskID.length || taskID[end] == ':'
    }

    fun pause(id: String) {
        jobs.remove(id)?.cancel()
        books[id] = book(id).copy(state = State.Paused)
    }

    private fun stop(id: String, why: String) {
        books[id] = book(id).copy(state = State.Stopped(why))
        jobs.remove(id)?.cancel()
    }

    private suspend fun worker(core: YuyueCore, id: String) {
        while (scope.isActive && book(id).state == State.Running) {
            val section = focus[id] ?: 0u
            val task = withContext(Dispatchers.IO) { runCatching { core.dramaNextTask(id, section) }.getOrNull() }
            val llm = task as? DramaTask.Llm
            // A voice or a sound to make is 整本提前做好's (DramaSynth): handed back, not held.
            if (task != null && llm == null) {
                val other = when (task) {
                    is DramaTask.Tts -> task.id
                    is DramaTask.DesignVoice -> task.id
                    is DramaTask.Sfx -> task.id
                    is DramaTask.Llm -> task.id
                }
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, other, DramaResult.Returned) } }
                return
            }
            if (llm == null) {
                if ((asking[id] ?: 0) > 0) { delay(2000); continue }
                return
            }
            val activeScope = scopes[id]
            if (activeScope is Scope.SectionOnly && !taskMatchesSection(llm.id, id, activeScope.section)) {
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, DramaResult.Returned) } }
                if ((asking[id] ?: 0) > 0) { delay(2000); continue }
                return
            }
            val c = config
            if (c == null) {
                stop(id, t("书伴还没接上大模型：在设置 · 书伴里添加一个服务商"))
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, DramaResult.Returned) } }
                return
            }
            asking[id] = (asking[id] ?: 0) + 1
            // Keep schema in `system` so `system` stays constant across scenes and `user[0..<cachePrefixLen]`
            // (cast block) stays at the exact start of `user` for prompt caching.
            val fullSystem = if (llm.schema.isEmpty()) llm.system else llm.system + "\n\n<schema>\n" + llm.schema + "\n</schema>"
            val useFast = llm.tier == uniffi.yuyue_core.LlmTier.FAST
            val result: DramaResult = try {
                val text = ask(c, fullSystem, llm.user, maxOf(llm.maxOutputTokens.toInt(), 1024), llm.cachePrefixLen.toInt(), useFast)
                books[id] = book(id).let { it.copy(sentChars = it.sentChars + llm.user.length + text.length) }
                DramaResult.Llm(text)
            } catch (e: kotlinx.coroutines.CancellationException) {
                // Paused: handed back (whatever the cancelling), to be asked again on 继续.
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, DramaResult.Returned) } }
                throw e
            } catch (e: CompanionError) {
                val m = e.message ?: ""
                // The key, account credit, or user budget cap: given out again once resolved.
                if ("Key" in m || "余额" in m || "上限" in m) { stop(id, m); DramaResult.Returned } else DramaResult.Failed(m)
            } catch (e: Exception) {
                DramaResult.Failed(e.message ?: "")
            } finally {
                asking[id] = (asking[id] ?: 1) - 1
            }
            // Handed in even when `stop` has just cancelled this worker, or the task stays taken.
            val p = withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, result) }.getOrNull() }
            if (p != null) books[id] = book(id).copy(progress = p)
            if (result is DramaResult.Llm) updated.tryEmit(id)
        }
    }

    /** Asked again after a pause when the service is busy. */
    private suspend fun ask(c: CompanionConfig, system: String, prompt: String, maxTokens: Int, cachePrefixLen: Int, fast: Boolean): String {
        var wait = 4000L
        repeat(3) {
            try {
                return Companion.answer(c, system, prompt, maxTokens, cachePrefixLen, fast, app.yuyue.companion.CompanionUsage.Source.Drama)
            } catch (e: CompanionError) {
                if (e.message?.contains("太快") != true) throw e
            } catch (e: java.io.IOException) {
                // the line dropped: try again
            }
            delay(wait)
            wait *= 3
        }
        return Companion.answer(c, system, prompt, maxTokens, cachePrefixLen, fast, app.yuyue.companion.CompanionUsage.Source.Drama)
    }

    /** 约 230 万字 · 约 600 万 token. */
    fun estimate(characters: Int, chinese: Boolean): Int {
        val tokensIn = (characters * 3.5 * (if (chinese) 0.75 else 0.27)).toInt()
        return tokensIn + tokensIn / 12
    }
}

@Composable
fun DramaAvatar(text: String, c: Color, size: Int = 26) {
    Box(Modifier.size(size.dp).background(c.copy(alpha = 0.18f), CircleShape), contentAlignment = Alignment.Center) {
        Text(text, fontSize = (size * 0.44).sp, fontWeight = FontWeight.SemiBold, color = c)
    }
}

/** Compact header capsule (`待确认 24 ›`) placed on the right of the cast sheet header. */
@Composable
fun DramaUnsurePill(count: Int, onClick: () -> Unit) {
    val pillLineColor = if (YY.dark) Color(0xFF3C3833) else Color(0xFFDDD9D1)
    Row(
        Modifier
            .clip(CircleShape)
            .background(YY.c.card, CircleShape)
            .border(0.5.dp, pillLineColor, CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 9.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Text(t("待确认"), fontSize = 11.sp, fontWeight = FontWeight.Medium, color = YY.c.t2, maxLines = 1)
        Text("$count", fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent, style = YY.tnum, maxLines = 1)
        Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(10.dp))
    }
}

/** What's known about who says what, on top of the cast. */
@Composable
fun DramaStatus(core: YuyueCore, bookID: String, section: UInt, version: Int, openUnsure: () -> Unit = {}) {
    var lines by remember { mutableIntStateOf(0) }
    var known by remember { mutableIntStateOf(0) }
    var unsureHere by remember { mutableIntStateOf(0) }
    var unsureSoFar by remember { mutableIntStateOf(0) }
    var characters by remember { mutableIntStateOf(0) }
    var sectionChars by remember { mutableIntStateOf(0) }
    var chinese by remember { mutableStateOf(true) }
    var loaded by remember { mutableStateOf(false) }
    var tick by remember { mutableIntStateOf(0) }
    var chapterOnly by remember { mutableStateOf(true) }
    LaunchedEffect(Unit) { DramaWork.updated.collect { if (it == bookID) tick++ } }
    LaunchedEffect(section, version, tick) {
        withContext(Dispatchers.IO) {
            runCatching { core.dramaSection(bookID, section) }.getOrNull()?.let { play ->
                val seen = HashSet<UInt>()
                var l = 0; var k = 0; var u = 0
                for (s in play.segments) {
                    val utt = s.utterance ?: continue
                    if (s.kind == SegKind.NARRATION || !seen.add(utt)) continue
                    l++
                    if (s.speaker is DramaSpeaker.Unsure) u++ else k++
                }
                lines = l; known = k; unsureHere = u
            }
            unsureSoFar = runCatching { core.dramaReview(bookID, section) }.getOrDefault(emptyList()).size
            val n = runCatching { core.dramaProgress(bookID).sections.toInt() }.getOrDefault(0)
            var chars = 0; var secChars = 0; var han = 0
            for (i in 0 until n) for (s in runCatching { core.sentences(bookID, i.toUInt()) }.getOrDefault(emptyList())) {
                chars += s.length
                if (i.toUInt() == section) secChars += s.length
                if (i < 3) han += s.count { it in '一'..'鿿' }
            }
            characters = chars; sectionChars = secChars; chinese = han > 50
        }
        loaded = true
        DramaWork.refresh(core, bookID)
    }
    val b = DramaWork.book(bookID)
    if (!loaded || (lines == 0 && b.state != DramaWork.State.Running)) return
    if (unsureSoFar == 0 && b.progress?.castReady == true && b.state != DramaWork.State.Running) {
        Text(t("读到这里的台词都认好了"), fontSize = 10.5.sp, color = YY.c.t3, modifier = Modifier.padding(top = 8.dp, start = 4.dp))
        return
    }

    val dark = YY.dark
    val trayColor = if (dark) Color(0xFF1D1B18) else Color(0xFFF9F7F3)
    val pillLineColor = if (dark) Color(0xFF3C3833) else Color(0xFFDDD9D1)
    val cardBorderColor = if (dark) Color.White.copy(alpha = 0.08f) else YY.c.line

    val chapterCostLabel = t("约 %@ token", CompanionUsage.formatCount(DramaWork.estimate(maxOf(sectionChars, 600), chinese)))
    val bookCostLabel = t("%lld 万字 · 约 %lld 万", maxOf(1, characters / 10000), maxOf(1, DramaWork.estimate(characters, chinese) / 10000))

    @Composable
    fun HairlinePillButton(label: String, onClick: () -> Unit) {
        Box(
            Modifier
                .clip(CircleShape)
                .background(YY.c.card, CircleShape)
                .border(0.5.dp, pillLineColor, CircleShape)
                .clickable(onClick = onClick)
                .padding(horizontal = 12.dp, vertical = 6.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(label, fontSize = 11.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1)
        }
    }

    @Composable
    fun InkProgress(fraction: Float) {
        val f = fraction.coerceIn(0f, 1f)
        Row(Modifier.fillMaxWidth().height(2.dp).clip(CircleShape).background(YY.c.inset)) {
            if (f > 0f) Box(Modifier.weight(f).height(2.dp).background(YY.c.t1))
            if (1f - f > 0.001f) Spacer(Modifier.weight(1f - f))
        }
    }

    @Composable
    fun ScopeToggle() {
        Row(
            Modifier
                .clip(CircleShape)
                .background(YY.c.inset, CircleShape)
                .padding(2.5.dp),
            horizontalArrangement = Arrangement.spacedBy(2.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            listOf(true to t("仅本章"), false to t("全书")).forEach { (isChapter, label) ->
                val active = chapterOnly == isChapter
                Box(
                    Modifier
                        .clip(CircleShape)
                        .then(if (active) Modifier.background(YY.c.card, CircleShape) else Modifier)
                        .clickable { chapterOnly = isChapter }
                        .padding(horizontal = 10.dp, vertical = 3.dp),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        label,
                        fontSize = 11.sp,
                        fontWeight = if (active) FontWeight.Medium else FontWeight.Normal,
                        color = if (active) YY.c.t1 else YY.c.t2,
                        maxLines = 1,
                    )
                }
            }
        }
    }

    Column(Modifier.padding(top = 8.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        val cardShape = RoundedCornerShape(15.dp)
        Column(
            Modifier
                .fillMaxWidth()
                .clip(cardShape)
                .background(YY.c.card, cardShape)
                .border(0.5.dp, cardBorderColor, cardShape),
        ) {
            when (val s = b.state) {
                DramaWork.State.Running -> {
                    val onlySection = DramaWork.isSectionOnly(bookID)
                    val frac = if (onlySection) {
                        if (lines > 0) maxOf(0.12f, known.toFloat() / lines) else 0.25f
                    } else {
                        b.progress?.let { if (it.sections > 0u) maxOf(0.05f, it.sectionsDone.toFloat() / it.sections.toFloat()) else 0f } ?: 0.1f
                    }
                    Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            Box(
                                Modifier.size(30.dp).background(YY.c.t1, RoundedCornerShape(9.dp)),
                                contentAlignment = Alignment.Center,
                            ) {
                                CircularProgressIndicator(
                                    modifier = Modifier.size(14.dp),
                                    strokeWidth = 1.6.dp,
                                    color = YY.c.card,
                                )
                            }
                            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                Text(
                                    if (onlySection) t("正在认本章") else t("正在认全书"),
                                    fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1,
                                    maxLines = 1, overflow = TextOverflow.Ellipsis,
                                )
                                if (onlySection) {
                                    Text(
                                        if (CompanionUsage.tierRoutingEnabled) t("已认出 %lld / %lld 句 · 轻量模型", known, lines)
                                        else t("已认出 %lld / %lld 句", known, lines),
                                        fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1,
                                    )
                                } else {
                                    b.progress?.let {
                                        Text(t("第 %lld / %lld 章", it.sectionsDone.toInt(), it.sections.toInt()), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
                                    }
                                }
                            }
                            HairlinePillButton(t("暂停")) { DramaWork.pause(bookID) }
                        }
                        InkProgress(frac)
                    }
                    Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                    Row(
                        Modifier.fillMaxWidth().background(trayColor).padding(horizontal = 14.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Text(t("合上书也会继续，退出语阅即停"), fontSize = 10.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                        if (b.sentChars > 0) {
                            Text(t("已发出约 %@ 字", CompanionUsage.formatCount(b.sentChars)), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
                        }
                    }
                }
                is DramaWork.State.Stopped -> {
                    Row(
                        Modifier.fillMaxWidth().padding(14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(12.dp),
                    ) {
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text(t("停下了"), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                            Text(s.why, fontSize = 10.5.sp, lineHeight = 15.sp, color = YY.c.t3)
                        }
                        HairlinePillButton(t("接着认")) {
                            DramaWork.start(core, bookID, section, if (chapterOnly) DramaWork.Scope.SectionOnly(section) else DramaWork.Scope.WholeBook)
                        }
                    }
                }
                else -> {
                    val missing = maxOf(0, lines - known)
                    val chapterDone = lines > 0 && missing == 0 && b.progress?.castReady != true && DramaWork.config != null
                    if (chapterDone) {
                        Row(
                            Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 13.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                Text(t("这一章 %lld 句台词已认全", lines), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, style = YY.tnum)
                                Text(bookCostLabel, fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
                            }
                            HairlinePillButton(t("+ 认全书")) {
                                DramaWork.start(core, bookID, section, DramaWork.Scope.WholeBook)
                            }
                        }
                    } else {
                        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(11.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                                Box(
                                    Modifier.size(30.dp).background(YY.c.inset, RoundedCornerShape(9.dp)),
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Icon(Icons.sparkle, null, tint = YY.c.t1, modifier = Modifier.size(14.dp))
                                }
                                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                    Text(t("本章台词 · %lld / %lld", known, lines), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, style = YY.tnum, maxLines = 1)
                                    val sub = when {
                                        b.progress?.castReady == true -> if (unsureSoFar > 0) t("截至当前共有 %lld 句待确认", unsureSoFar) else t("读到这里的台词都认好了")
                                        DramaWork.config == null -> t("在「设置 › 书伴」中配置大模型后可自动补全")
                                        missing > 0 -> t("余 %lld 句未写明说话人", missing)
                                        else -> t("本章台词已全部识别")
                                    }
                                    Text(sub, fontSize = 10.5.sp, lineHeight = 14.5.sp, color = YY.c.t3, maxLines = 2, overflow = TextOverflow.Ellipsis)
                                }
                                if (b.progress?.castReady != true && DramaWork.config != null) {
                                    HairlinePillButton(if (chapterOnly) t("开始认本章") else t("开始认全书")) {
                                        DramaWork.start(core, bookID, section, if (chapterOnly) DramaWork.Scope.SectionOnly(section) else DramaWork.Scope.WholeBook)
                                    }
                                }
                            }
                            InkProgress(if (lines > 0) known.toFloat() / lines else 0f)
                        }
                        if (b.progress?.castReady != true && DramaWork.config != null) {
                            Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                            Row(
                                Modifier.fillMaxWidth().background(trayColor).padding(horizontal = 14.dp, vertical = 9.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                ScopeToggle()
                                Spacer(Modifier.weight(1f))
                                Text(
                                    if (chapterOnly) chapterCostLabel else bookCostLabel,
                                    fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

/** “可能是同一个人”: 合成一个 or 不是. */
@Composable
fun SameSuggestions(core: YuyueCore, bookID: String, list: List<SameSuggestion>, look: (String) -> Pair<String, Color>, changed: () -> Unit) {
    if (list.isEmpty()) return
    val scope = rememberCoroutineScope()
    fun edit(e: CastEdit) = scope.launch { withContext(Dispatchers.IO) { runCatching { core.dramaEditCast(bookID, e) } }; changed() }
    YYGroup(Modifier.padding(top = 10.dp)) {
        for (s in list.take(2)) row {
            Column(Modifier.padding(vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for ((i, n) in listOf(s.a, s.b).withIndex()) {
                        if (i == 1) Text(t("和"), fontSize = 10.sp, color = YY.c.t3)
                        val (a, c) = look(n)
                        DramaAvatar(a, c, 22)
                        Text(n, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                    }
                    Text(t("可能是同一个人"), fontSize = 10.sp, color = YY.c.t3, maxLines = 1)
                }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    YYButton(t("合成一个"), kind = ButtonKind.Main, size = ButtonSize.Small) { edit(CastEdit.Merge(s.a, s.b)) }
                    YYButton(t("不是"), size = ButtonSize.Small) { edit(CastEdit.NotSame(s.a, s.b)) }
                }
            }
        }
    }
}

/** 没把握的句子: one line at a time, the scene around it, the likeliest two. */
@Composable
fun UnsureList(core: YuyueCore, bookID: String, section: UInt, chapter: (UInt) -> String?, changed: () -> Unit) {
    var items by remember { mutableStateOf<List<UnsureLine>>(emptyList()) }
    var looks by remember { mutableStateOf<Map<String, CastPick>>(emptyMap()) }
    var loaded by remember { mutableStateOf(false) }
    var picks by remember { mutableIntStateOf(0) }
    var settled by remember { mutableIntStateOf(0) }
    var undo by remember { mutableStateOf<Triple<UnsureLine, String, Int>?>(null) }
    var picking by remember { mutableStateOf<UnsureLine?>(null) }
    var version by remember { mutableIntStateOf(0) }
    val scope = rememberCoroutineScope()
    LaunchedEffect(section, version) {
        val (list, everyone) = withContext(Dispatchers.IO) {
            runCatching { core.dramaReview(bookID, section) }.getOrDefault(emptyList()) to runCatching { core.dramaEveryone(bookID, section) }.getOrDefault(emptyList())
        }
        items = list.filter { it.section == section } + list.filter { it.section != section }
        looks = everyone.associateBy { it.name }
        loaded = true
    }
    fun choose(line: UnsureLine, choice: UserChoice, name: String) {
        val before = items.size
        scope.launch {
            withContext(Dispatchers.IO) { runCatching { core.dramaSetSpeaker(bookID, line.section, line.sentence, line.start, choice) } }
            val after = withContext(Dispatchers.IO) { runCatching { core.dramaReview(bookID, section) }.getOrDefault(emptyList()) }
            val freed = maxOf(0, before - after.size - 1)
            picks++; settled += freed + 1
            undo = Triple(line, name, freed)
            version++
            changed()
        }
    }
    undo?.let { (line, name, freed) ->
        Row(Modifier.padding(top = 8.dp).fillMaxWidth().background(YY.c.card, RoundedCornerShape(12.dp)).padding(horizontal = 12.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("✓ $name", fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent)
            if (freed > 0) Text(t("也定了 %lld 句", freed), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
            Spacer(Modifier.weight(1f))
            YYButton(t("撤销"), size = ButtonSize.Small) {
                undo = null
                scope.launch {
                    withContext(Dispatchers.IO) { runCatching { core.dramaClearSpeaker(bookID, line.section, line.sentence, line.start) } }
                    picks = maxOf(0, picks - 1); settled = maxOf(0, settled - freed - 1)
                    version++
                    changed()
                }
            }
        }
    }
    val line = items.firstOrNull()
    if (line == null) {
        if (loaded) Column(Modifier.padding(top = 10.dp).fillMaxWidth().background(YY.c.card, RoundedCornerShape(16.dp)).padding(vertical = 22.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(22.dp))
            Text(t("读到这里的台词都认好了"), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
            if (picks > 0) Text(t("点了 %lld 下，带走 %lld 句", picks, settled), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
        }
    } else {
        Column(Modifier.padding(top = 10.dp).fillMaxWidth().background(YY.c.card, RoundedCornerShape(16.dp))) {
            val where = listOfNotNull(chapter(line.section), line.place.ifEmpty { null }, line.present.take(5).joinToString(" ").ifEmpty { null }?.let { t("在场 ") + it }).joinToString(" · ")
            Text(where, fontSize = 10.sp, color = YY.c.t3, maxLines = 1, modifier = Modifier.padding(start = 12.dp, end = 12.dp, top = 10.dp))
            val at = line.sentence.toInt() - line.contextFirst.toInt()
            Text(buildAnnotatedString {
                for ((i, s) in line.context.withIndex()) {
                    val k = if (i == at) s.indexOf(line.text) else -1
                    if (k >= 0) {
                        withStyle(SpanStyle(color = YY.c.t3)) { append(s.substring(0, k)) }
                        withStyle(SpanStyle(color = YY.c.t1, background = YY.c.mkY)) { append(line.text) }
                        withStyle(SpanStyle(color = YY.c.t3)) { append(s.substring(k + line.text.length)) }
                    } else withStyle(SpanStyle(color = YY.c.t3.copy(alpha = if (i < at) 1f else 0.7f))) { append(s) }
                }
            }, fontFamily = YY.serif, fontSize = 14.5.sp, lineHeight = 25.sp, modifier = Modifier.padding(start = 12.dp, end = 12.dp, top = 6.dp, bottom = 10.dp))
            Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
            Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(t("这句是谁说的？"), fontSize = 11.sp, color = YY.c.t3)
                for ((i, name) in line.names.take(2).withIndex()) {
                    val look = looks[name]
                    Row(
                        Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(if (i == 0) YY.tint.accentSoft else YY.c.bg)
                            .border(0.5.dp, if (i == 0) YY.tint.edge else YY.c.line, RoundedCornerShape(10.dp))
                            .clickable { choose(line, UserChoice.Person(name), name) }.padding(horizontal = 10.dp, vertical = 10.dp),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(9.dp),
                    ) {
                        DramaAvatar(look?.avatar ?: name.takeLast(1), hex(look?.color ?: "#888888"), 24)
                        Text(name, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                        Text(if (i == 0) t("最可能") else if (name in line.present) t("在场") else t("也可能"), fontSize = 10.sp, color = YY.c.t3)
                    }
                }
                Row(Modifier.padding(top = 3.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    YYButton(t("别人…"), size = ButtonSize.Small) { picking = line }
                    YYButton(t("不是台词"), size = ButtonSize.Small) { choose(line, UserChoice.NotSpeech, t("不是台词")) }
                    Spacer(Modifier.weight(1f))
                    Text(t("跳过 →"), fontSize = 10.5.sp, color = YY.c.t3, modifier = Modifier.clickable { undo = null; items = items.drop(1) + items.take(1) }.padding(vertical = 6.dp, horizontal = 6.dp))
                }
            }
        }
        if (line.frees > 0u) Text(t("定了这句，后面 %u 句也跟着定。", line.frees.toInt()), fontSize = 10.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.padding(top = 6.dp, start = 2.dp))
    }
    picking?.let { p ->
        PeoplePicker(core, bookID, section, t("是谁说的"), null, { picking = null }) { name -> picking = null; choose(p, UserChoice.Person(name), name) }
    }
}

/** 别的人物…: everyone met so far, searched; a name nobody has becomes someone new. */
@Composable
fun PeoplePicker(core: YuyueCore, bookID: String, section: UInt, title: String, except: String?, close: () -> Unit, pick: (String) -> Unit) {
    var everyone by remember { mutableStateOf<List<CastPick>>(emptyList()) }
    var query by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { everyone = withContext(Dispatchers.IO) { runCatching { core.dramaEveryone(bookID, section) }.getOrDefault(emptyList()) } }
    Dialog(onDismissRequest = close) {
        Column(Modifier.fillMaxWidth().heightIn(max = 520.dp).background(YY.c.bg, RoundedCornerShape(20.dp)).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
            Row(Modifier.fillMaxWidth().height(36.dp).background(YY.c.inset, RoundedCornerShape(9.dp)).padding(horizontal = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(15.dp))
                Spacer(Modifier.width(6.dp))
                BasicTextField(query, { query = it }, singleLine = true, textStyle = TextStyle(fontSize = 13.sp, color = YY.c.t1), modifier = Modifier.weight(1f))
            }
            val q = query.trim()
            val shown = everyone.filter { it.name != except && (q.isEmpty() || q in it.name) }
            Column(Modifier.background(YY.c.card, RoundedCornerShape(14.dp)).padding(horizontal = 12.dp).verticalScroll(rememberScrollState())) {
                if (q.isNotEmpty() && everyone.none { it.name == q }) {
                    Row(Modifier.fillMaxWidth().clickable { pick(q) }.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(15.dp))
                        Text(t("新的人物“%@”", q), fontSize = 13.sp, color = YY.c.t1)
                    }
                }
                for (p in shown) Row(Modifier.fillMaxWidth().clickable { pick(p.name) }.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    DramaAvatar(p.avatar, hex(p.color))
                    Text(p.name, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                }
            }
        }
    }
}

/** 人物页: how the book calls someone (× makes a name someone of its own, ＋ adds one), and
 *  whether they're a man or a woman. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun NamesSection(core: YuyueCore, bookID: String, name: String, changed: () -> Unit) {
    var names by remember(name) { mutableStateOf(listOf(name)) }
    var gender by remember(name) { mutableStateOf(CharacterGender.UNKNOWN) }
    var version by remember { mutableIntStateOf(0) }
    var adding by remember { mutableStateOf(false) }
    var newName by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    LaunchedEffect(name, version) {
        withContext(Dispatchers.IO) {
            names = runCatching { core.dramaNames(bookID, name) }.getOrDefault(emptyList()).ifEmpty { listOf(name) }
            gender = runCatching { core.dramaGender(bookID, name) }.getOrDefault(CharacterGender.UNKNOWN)
        }
    }
    fun edit(e: CastEdit) = scope.launch { withContext(Dispatchers.IO) { runCatching { core.dramaEditCast(bookID, e) } }; version++; changed() }
    YYGroup(Modifier.padding(bottom = 10.dp)) {
        row {
            Column(Modifier.padding(vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(t("书里怎么叫"), fontSize = 11.sp, color = YY.c.t3)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(5.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
                    for ((i, n) in names.withIndex()) {
                        Row(Modifier.heightIn(min = 26.dp).background(YY.c.bg, RoundedCornerShape(7.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(7.dp))
                            .padding(start = 9.dp, end = if (i == 0) 9.dp else 3.dp, top = 3.dp, bottom = 3.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(n, fontSize = 11.sp, fontWeight = if (i == 0) FontWeight.SemiBold else FontWeight.Normal, color = YY.c.t1,
                                modifier = Modifier.clickable(enabled = i > 0) { edit(CastEdit.Show(n)) })
                            if (i > 0) Icon(Icons.close, t("拆出去成另一个人"), tint = YY.c.t3, modifier = Modifier.size(20.dp).clickable { edit(CastEdit.Split(n)) }.padding(4.dp))
                        }
                    }
                    Box(Modifier.heightIn(min = 26.dp).border(0.5.dp, YY.c.line, RoundedCornerShape(7.dp)).clickable { adding = true }.padding(horizontal = 9.dp, vertical = 3.dp), contentAlignment = Alignment.Center) {
                        Text("＋", fontSize = 11.sp, color = YY.c.t3)
                    }
                }
                if (adding) Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    BasicTextField(newName, { newName = it }, singleLine = true, textStyle = TextStyle(fontSize = 12.sp, color = YY.c.t1),
                        modifier = Modifier.weight(1f).background(YY.c.inset, RoundedCornerShape(8.dp)).padding(horizontal = 8.dp, vertical = 6.dp))
                    YYButton(t("加上"), size = ButtonSize.Small) {
                        val n = newName.trim()
                        newName = ""; adding = false
                        if (n.isNotEmpty()) edit(CastEdit.AddName(name, n))
                    }
                }
            }
        }
        row {
            Row(Modifier.fillMaxWidth().heightIn(min = 40.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(t("是"), fontSize = 11.5.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
                YYSegments(listOf(CharacterGender.M to t("男"), CharacterGender.F to t("女"), CharacterGender.UNKNOWN to t("不知道")), gender,
                    { gender = it; edit(CastEdit.SetGender(name, it)) }, small = true, modifier = Modifier.widthIn(min = 148.dp))
            }
        }
    }
}

/** A line on the page whose speaker is being picked. */
data class SpeakerPick(val section: UInt, val line: BubbleLine, val x: Double, val y: Double, val people: List<CastPick> = emptyList())

@Composable
private fun MenuRow(title: String, icon: ImageVector, color: Color = YY.c.t1, bold: Boolean = false, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().padding(horizontal = 5.dp).clip(RoundedCornerShape(7.dp)).clickable(onClick = onClick).height(36.dp).padding(horizontal = 9.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Text(title, fontSize = 13.sp, fontWeight = if (bold) FontWeight.SemiBold else FontWeight.Normal, color = color, modifier = Modifier.weight(1f), maxLines = 1)
        Icon(icon, null, tint = color, modifier = Modifier.size(16.dp))
    }
}

@Composable
private fun MenuGroup(content: @Composable () -> Unit) {
    Box(Modifier.fillMaxWidth().padding(horizontal = 14.dp).height(0.5.dp).background(YY.c.line))
    Column(Modifier.padding(vertical = 4.dp)) { content() }
}

/** The card a bubble's avatar opens: the scene's people (the one now ticked), anyone else, 众人,
 *  不是台词 — the same white card as long-pressing a book. */
@Composable
fun SpeakerCard(pick: SpeakerPick, modifier: Modifier, choose: (UserChoice) -> Unit, others: () -> Unit) {
    Column(modifier.width(230.dp).shadow(16.dp, RoundedCornerShape(14.dp), ambientColor = Color.Black.copy(alpha = 0.14f), spotColor = Color.Black.copy(alpha = 0.2f))
        .clip(RoundedCornerShape(14.dp)).background(YY.c.card).padding(bottom = 4.dp)) {
        Text(t("这句是谁说的"), fontSize = 13.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, modifier = Modifier.padding(start = 14.dp, end = 14.dp, top = 11.dp, bottom = 6.dp))
        MenuGroup {
            for (p in pick.people.take(6)) {
                val on = p.name == pick.line.who
                MenuRow(p.name, if (on) Icons.check else Icons.person, if (on) YY.tint.accent else YY.c.t1, on) { choose(UserChoice.Person(p.name)) }
            }
        }
        MenuGroup {
            MenuRow(t("别的人物…"), Icons.search, onClick = others)
            MenuRow(t("众人"), Icons.personTwo) { choose(UserChoice.Group(t("众人"))) }
            MenuRow(t("不是台词"), Icons.list) { choose(UserChoice.NotSpeech) }
        }
    }
}

/** 合并、男女、不是一个人物: long-pressing someone in the cast. */
@Composable
fun CastPersonMenu(core: YuyueCore, bookID: String, member: uniffi.yuyue_core.CastMember, open: () -> Unit, mergeOther: () -> Unit, close: () -> Unit, changed: () -> Unit) {
    val scope = rememberCoroutineScope()
    fun edit(e: CastEdit) { close(); scope.launch { withContext(Dispatchers.IO) { runCatching { core.dramaEditCast(bookID, e) } }; changed() } }
    Dialog(onDismissRequest = close) {
        Column(Modifier.width(250.dp).clip(RoundedCornerShape(14.dp)).background(YY.c.card).padding(bottom = 4.dp)) {
            Row(Modifier.padding(start = 14.dp, end = 14.dp, top = 11.dp, bottom = 6.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(member.name, fontSize = 13.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, modifier = Modifier.alignByBaseline())
                Text(t("%u 句", member.linesSoFar.toInt()), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline())
            }
            MenuGroup { MenuRow(t("%@的人物页…", member.name), Icons.personCircle) { close(); open() } }
            MenuGroup {
                member.maybeSame?.let { other -> MenuRow(t("合并到 %@", other), Icons.merge) { edit(CastEdit.Merge(other, member.name)) } }
                MenuRow(t("合并到别人…"), Icons.search) { close(); mergeOther() }
            }
            MenuGroup {
                val f = member.gender == CharacterGender.F
                val m = member.gender == CharacterGender.M
                MenuRow(t("设为女性"), if (f) Icons.check else Icons.person, if (f) YY.tint.accent else YY.c.t1, f) { edit(CastEdit.SetGender(member.name, CharacterGender.F)) }
                MenuRow(t("设为男性"), if (m) Icons.check else Icons.person, if (m) YY.tint.accent else YY.c.t1, m) { edit(CastEdit.SetGender(member.name, CharacterGender.M)) }
            }
            MenuGroup { MenuRow(t("并非人物（移除）"), Icons.nosign, YY.c.danger) { edit(CastEdit.Remove(member.name)) } }
        }
    }
}
