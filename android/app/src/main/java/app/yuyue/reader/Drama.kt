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
import androidx.compose.material.icons.Icons as M
import androidx.compose.material.icons.rounded.Block
import androidx.compose.material.icons.rounded.Group
import androidx.compose.material.icons.rounded.Notes
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
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

    data class Book(val state: State = State.Idle, val progress: DramaProgress? = null, val sentChars: Int = 0)

    val books = mutableStateMapOf<String, Book>()
    /** A book whose bubbles, cast and voices should follow a new answer. */
    val updated = MutableSharedFlow<String>(extraBufferCapacity = 16)
    private val jobs = HashMap<String, Job>()
    private val focus = HashMap<String, UInt>()
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

    fun reading(id: String, section: UInt) { focus[id] = section }

    suspend fun refresh(core: YuyueCore, id: String) {
        val p = withContext(Dispatchers.IO) { runCatching { core.dramaProgress(id) }.getOrNull() }
        var b = book(id).copy(progress = p)
        if (b.state == State.Idle && p != null && p.castReady && p.sectionsDone == p.sections) b = b.copy(state = State.Done)
        books[id] = b
    }

    fun start(core: YuyueCore, id: String, section: UInt) {
        if (jobs[id]?.isActive == true) return
        tellCore(core)
        focus[id] = section
        books[id] = book(id).copy(state = State.Running)
        jobs[id] = scope.launch {
            val workers = List(2) { launch { worker(core, id) } }
            workers.forEach { it.join() }
            jobs.remove(id)
            if (book(id).state == State.Running) books[id] = book(id).copy(state = State.Done)
            refresh(core, id)
        }
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
            val c = config
            if (c == null) {
                stop(id, t("书伴还没接上大模型：在设置 · 书伴里添加一个服务商"))
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, DramaResult.Returned) } }
                return
            }
            asking[id] = (asking[id] ?: 0) + 1
            val prompt = llm.user + "\n\n<schema>\n" + llm.schema + "\n</schema>"
            val result: DramaResult = try {
                val text = ask(c, llm.system, prompt, maxOf(llm.maxOutputTokens.toInt(), 1024))
                books[id] = book(id).let { it.copy(sentChars = it.sentChars + prompt.length + text.length) }
                DramaResult.Llm(text)
            } catch (e: kotlinx.coroutines.CancellationException) {
                // Paused: handed back (whatever the cancelling), to be asked again on 继续.
                withContext(kotlinx.coroutines.NonCancellable + Dispatchers.IO) { runCatching { core.dramaSubmit(id, llm.id, DramaResult.Returned) } }
                throw e
            } catch (e: CompanionError) {
                val m = e.message ?: ""
                // The key or the account, not the scene: given out again once that's fixed.
                if ("Key" in m || "余额" in m) { stop(id, m); DramaResult.Returned } else DramaResult.Failed(m)
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
    private suspend fun ask(c: CompanionConfig, system: String, prompt: String, maxTokens: Int): String {
        var wait = 4000L
        repeat(3) {
            try {
                return Companion.answer(c, system, prompt, maxTokens)
            } catch (e: CompanionError) {
                if (e.message?.contains("太快") != true) throw e
            } catch (e: java.io.IOException) {
                // the line dropped: try again
            }
            delay(wait)
            wait *= 3
        }
        return Companion.answer(c, system, prompt, maxTokens)
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

/** What's known about who says what, on top of the cast; and the way into the unsure lines. */
@Composable
fun DramaStatus(core: YuyueCore, bookID: String, section: UInt, version: Int, openUnsure: () -> Unit) {
    var lines by remember { mutableIntStateOf(0) }
    var known by remember { mutableIntStateOf(0) }
    var unsureHere by remember { mutableIntStateOf(0) }
    var unsureSoFar by remember { mutableIntStateOf(0) }
    var characters by remember { mutableIntStateOf(0) }
    var chinese by remember { mutableStateOf(true) }
    var loaded by remember { mutableStateOf(false) }
    var tick by remember { mutableIntStateOf(0) }
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
            var chars = 0; var han = 0
            for (i in 0 until n) for (s in runCatching { core.sentences(bookID, i.toUInt()) }.getOrDefault(emptyList())) {
                chars += s.length
                if (i < 3) han += s.count { it in '一'..'鿿' }
            }
            characters = chars; chinese = han > 50
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
    @Composable
    fun Note(s: String) = Text(s, fontSize = 10.sp, lineHeight = 15.sp, color = YY.c.t3, modifier = Modifier.padding(top = 3.dp))
    @Composable
    fun Bar(a: Float, u: Float) = Row(Modifier.padding(top = 8.dp, bottom = 2.dp).fillMaxWidth().height(3.dp).clip(CircleShape).background(YY.c.inset)) {
        if (a > 0f) Box(Modifier.weight(a).height(3.dp).background(YY.tint.fill))
        if (u > 0f) Box(Modifier.weight(u).height(3.dp).background(YY.c.warn.copy(alpha = 0.75f)))
        if (1f - a - u > 0.001f) Spacer(Modifier.weight(1f - a - u))
    }
    YYGroup(Modifier.padding(top = 8.dp)) {
        row {
            Column(Modifier.padding(vertical = 10.dp)) {
                when (val s = b.state) {
                    DramaWork.State.Running -> {
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(t("正在认"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.alignByBaseline())
                            b.progress?.let { Text(t("第 %lld / %lld 章", it.sectionsDone.toInt(), it.sections.toInt()), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline()) }
                        }
                        Bar(b.progress?.let { if (it.sections > 0u) it.sectionsDone.toFloat() / it.sections.toFloat() else 0f } ?: 0f, 0f)
                        Note(t("先认你正在读的这一章和后面几章，其余的在后台慢慢来。合上书也会接着认，退出语阅就停。"))
                        Row(Modifier.padding(top = 9.dp), verticalAlignment = Alignment.CenterVertically) {
                            YYButton(t("暂停"), size = ButtonSize.Small) { DramaWork.pause(bookID) }
                            Spacer(Modifier.weight(1f))
                            if (b.sentChars > 0) Text(t("已发出约 %lld 万字", maxOf(1, b.sentChars / 10000)), fontSize = 10.sp, color = YY.c.t3, style = YY.tnum)
                        }
                    }
                    is DramaWork.State.Stopped -> {
                        Text(t("停下了"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                        Note(s.why)
                        YYButton(t("接着认"), kind = ButtonKind.Main, size = ButtonSize.Small, modifier = Modifier.padding(top = 9.dp)) { DramaWork.start(core, bookID, section) }
                    }
                    else -> {
                        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(t("认出 %lld 句", known), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, style = YY.tnum, modifier = Modifier.alignByBaseline())
                            Text(t("这一章 %lld 句台词", lines), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, modifier = Modifier.alignByBaseline())
                        }
                        Bar(if (lines > 0) known.toFloat() / lines else 0f, if (lines > 0) (lines - known).toFloat() / lines else 0f)
                        if (b.progress?.castReady == true) {
                            if (unsureSoFar > 0) Note(t("截至当前共有 %lld 句待确认，逐句确认约需 %lld 分钟。", unsureSoFar, maxOf(1, unsureSoFar / 12)))
                        } else if (DramaWork.config != null) {
                            Note(t("当前仅识别文中写明说话人的台词。使用大模型通读全书可识别绝大多数对话，不确定处保持留空。"))
                            Row(Modifier.padding(top = 9.dp), verticalAlignment = Alignment.CenterVertically) {
                                YYButton(t("识别全书角色"), kind = ButtonKind.Main, size = ButtonSize.Small) { DramaWork.start(core, bookID, section) }
                                Spacer(Modifier.weight(1f))
                                Text(t("约 %lld 万字 · 约 %lld 万 token", maxOf(1, characters / 10000), maxOf(1, DramaWork.estimate(characters, chinese) / 10000)),
                                    fontSize = 10.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
                            }
                        } else {
                            Note(t("当前仅识别文中写明说话人的台词。在「设置 › 书伴」中配置大模型后，即可通读全书识别角色。"))
                        }
                    }
                }
            }
        }
        if (unsureSoFar > 0) row {
            Row(Modifier.fillMaxWidth().clickable(onClick = openUnsure).padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Box(Modifier.size(6.dp).background(YY.c.warn, CircleShape))
                Text(t("%lld 句待确认", unsureSoFar), fontSize = 11.5.sp, color = YY.c.t1, style = YY.tnum, modifier = Modifier.weight(1f))
                Text(if (unsureHere > 0) t("这一章 %lld 句", unsureHere) else t("先用旁白念"), fontSize = 10.sp, color = YY.c.t3, style = YY.tnum)
                Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(11.dp))
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
                Icon(M.Rounded.Search, null, tint = YY.c.t3, modifier = Modifier.size(15.dp))
                Spacer(Modifier.width(6.dp))
                BasicTextField(query, { query = it }, singleLine = true, textStyle = TextStyle(fontSize = 13.sp, color = YY.c.t1), modifier = Modifier.weight(1f))
            }
            val q = query.trim()
            val shown = everyone.filter { it.name != except && (q.isEmpty() || q in it.name) }
            Column(Modifier.background(YY.c.card, RoundedCornerShape(14.dp)).padding(horizontal = 12.dp).verticalScroll(rememberScrollState())) {
                if (q.isNotEmpty() && everyone.none { it.name == q }) {
                    Text(t("新的人物“%@”", q), fontSize = 13.sp, color = YY.c.t1, modifier = Modifier.fillMaxWidth().clickable { pick(q) }.padding(vertical = 11.dp))
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
                MenuRow(p.name, if (on) Icons.check else M.Rounded.Person, if (on) YY.tint.accent else YY.c.t1, on) { choose(UserChoice.Person(p.name)) }
            }
        }
        MenuGroup {
            MenuRow(t("别的人物…"), M.Rounded.Search, onClick = others)
            MenuRow(t("众人"), M.Rounded.Group) { choose(UserChoice.Group(t("众人"))) }
            MenuRow(t("不是台词"), M.Rounded.Notes) { choose(UserChoice.NotSpeech) }
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
            MenuGroup { MenuRow(t("%@的人物页…", member.name), M.Rounded.Person) { close(); open() } }
            MenuGroup {
                member.maybeSame?.let { other -> MenuRow(t("合并到 %@", other), Icons.chevronRight) { edit(CastEdit.Merge(other, member.name)) } }
                MenuRow(t("合并到别人…"), M.Rounded.Search) { close(); mergeOther() }
            }
            MenuGroup {
                val f = member.gender == CharacterGender.F
                val m = member.gender == CharacterGender.M
                MenuRow(t("设为女性"), if (f) Icons.check else M.Rounded.Person, if (f) YY.tint.accent else YY.c.t1, f) { edit(CastEdit.SetGender(member.name, CharacterGender.F)) }
                MenuRow(t("设为男性"), if (m) Icons.check else M.Rounded.Person, if (m) YY.tint.accent else YY.c.t1, m) { edit(CastEdit.SetGender(member.name, CharacterGender.M)) }
            }
            MenuGroup { MenuRow(t("并非人物（移除）"), M.Rounded.Block, YY.c.danger) { edit(CastEdit.Remove(member.name)) } }
        }
    }
}
