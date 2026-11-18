package app.yuyue.reader

import android.Manifest
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYSegments
import app.yuyue.speech.BookVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.ClonedVoices
import app.yuyue.speech.SampleCheck
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.TunedVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.CastMember
import uniffi.yuyue_core.Role
import uniffi.yuyue_core.RoleVoice
import java.io.File
import java.io.RandomAccessFile
import kotlin.math.log10
import kotlin.math.sqrt

/**
 * 人物声音: who has spoken so far and the
 * voice each reads with; a character's own page; choosing a voice; cloning one. Everyone reads
 * with the narrator's until given one. Only those up to this chapter: no spoilers.
 */
private sealed interface CastRoute {
    data object List : CastRoute
    data class Person(val name: String) : CastRoute
    /** Choosing for someone, or (null) the narrator. */
    data class Voice(val name: String?) : CastRoute
    data class Clone(val name: String?, val replacingId: String? = null, val initialName: String? = null) : CastRoute
    data class CloneDetail(val cardId: String) : CastRoute
    /** 没把握的句子. */
    data object Unsure : CastRoute
}

private fun color(hex: String): Color = runCatching { Color(android.graphics.Color.parseColor(hex)) }.getOrDefault(Color.Gray)

private val plain = RoleVoice(null, 1.0, 0.0, false)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CastSheet(model: ReaderModel, dismiss: () -> Unit) {
    val core = model.core
    val section = model.player?.spot?.section ?: model.section
    val language = model.player?.language ?: model.detail?.summary?.language ?: "zh"
    val stack = remember { mutableStateListOf<CastRoute>(CastRoute.List) }
    var cast by remember { mutableStateOf<List<CastMember>>(emptyList()) }
    var roles by remember { mutableStateOf<List<Role>>(emptyList()) }
    var version by remember { mutableIntStateOf(0) }
    var suggestions by remember { mutableStateOf<List<uniffi.yuyue_core.SameSuggestion>>(emptyList()) }
    var menuFor by remember { mutableStateOf<CastMember?>(null) }
    var mergingFrom by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { DramaWork.updated.collect { if (it == model.bookID) version++ } }
    LaunchedEffect(version) {
        val (c, r) = withContext(Dispatchers.IO) {
            runCatching { core.bookCast(model.bookID, section.toUInt()) }.getOrDefault(emptyList()) to runCatching { core.bookRoles(model.bookID) }.getOrDefault(emptyList())
        }
        cast = c
        roles = r
        suggestions = withContext(Dispatchers.IO) { runCatching { core.dramaSameSuggestions(model.bookID, section.toUInt()) }.getOrDefault(emptyList()) }
    }
    /** Who says what changed: the bubbles, the voices and this list follow. */
    fun changed() {
        model.player?.speakersChanged()
        model.castChanged()
        version++
    }
    fun roleOf(name: String) = roles.firstOrNull { it.name == name }?.voice
    fun set(name: String, v: RoleVoice) {
        runCatching { core.setBookRole(model.bookID, name, v) }
        model.player?.rolesChanged()
        model.castChanged()
        version++
    }
    fun sample(name: String?): String {
        if (name == null) return if (language.startsWith("zh")) "你好，这是我的声音。" else "Hello, this is how I sound."
        val script = runCatching { core.sectionScript(model.bookID, section.toUInt()) }.getOrNull()
        val sentences = runCatching { core.sentences(model.bookID, section.toUInt()) }.getOrDefault(emptyList())
        val l = script?.lines?.firstOrNull { (it.who?.let { w -> w == name || w.endsWith(name) || name.endsWith(w) } ?: false) && it.first == it.last }
        if (l != null) {
            val cps = sentences.getOrNull(l.first.toInt())?.codePoints()?.toArray()
            if (cps != null) {
                val a = minOf(l.start.toInt(), cps.size)
                val b = minOf(maxOf(l.end.toInt(), a), cps.size)
                val q = String(cps, a, b - a).trim()
                if (q.length >= 2) return q
            }
        }
        return if (language.startsWith("zh")) "我是$name。" else "I am $name."
    }
    val previewScope = rememberCoroutineScope()
    fun preview(v: BookVoice, text: String) {
        model.player?.pause()
        val base = (v as? TunedVoice)?.base ?: v
        if (base is SystemVoice) {
            base.preview(text)
        } else {
            previewScope.launch {
                val sp = withContext(Dispatchers.IO) { runCatching { v.render(text, language) }.getOrNull() }
                if (sp != null) SystemVoice.Engine.preview(sp)
            }
        }
    }

    val route = stack.last()
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp).verticalScroll(rememberScrollState())) {
            if (route !is CastRoute.Clone && route !is CastRoute.CloneDetail) {
                Row(Modifier.fillMaxWidth().heightIn(min = 44.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (stack.size > 1) {
                        Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("返回")) { stack.removeAt(stack.lastIndex) }, contentAlignment = Alignment.CenterStart) {
                            Icon(Icons.chevronLeft, t("返回"), tint = YY.c.t1, modifier = Modifier.size(22.dp))
                        }
                    }
                    Text(
                        when (route) {
                            CastRoute.List -> t("人物")
                            is CastRoute.Person -> route.name
                            is CastRoute.Voice -> route.name?.let { t("%@的声音", it) } ?: t("旁白的声音")
                            is CastRoute.Clone -> t("克隆一个声音")
                            is CastRoute.CloneDetail -> t("卡的详情")
                            CastRoute.Unsure -> t("待确认的台词")
                        },
                        fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, modifier = Modifier.weight(1f),
                    )
                }
            }
            when (route) {
                CastRoute.List -> {
                    DramaStatus(core, model.bookID, section.toUInt(), version) { stack.add(CastRoute.Unsure) }
                    SameSuggestions(core, model.bookID, suggestions, { n -> cast.firstOrNull { it.name == n }.let { (it?.avatar ?: n.takeLast(1)) to color(it?.color ?: "#888888") } }, ::changed)
                    CastList(cast, roles, model, language, longPress = { menuFor = it }) { stack.add(it) }
                    DramaListenGroup(model.library, model.bookID, section.toUInt(), language, null)
                }
                CastRoute.Unsure -> UnsureList(core, model.bookID, section.toUInt(), { s -> model.detail?.let { d -> ReaderModel.chapterTitle(d, s.toInt()) }?.substringBefore(' ') }, ::changed)
                is CastRoute.Person -> {
                    NamesSection(core, model.bookID, route.name, ::changed)
                    PersonPage(route.name, cast.firstOrNull { it.name == route.name }, roleOf(route.name) ?: plain, sample(route.name),
                    voiceName = { id -> id?.let { i -> roleVoiceName(core, model.bookID, i) } }, open = { stack.add(it) }, set = { set(route.name, it) },
                    preview = { v ->
                        scopePreview(model, language, v, sample(route.name), ::preview)
                    })
                }
                is CastRoute.Voice -> VoicePage(
                    name = route.name,
                    model = model,
                    language = language,
                    roles = roles,
                    chosenID = if (route.name != null) roleOf(route.name)?.voice else null,
                    sample = sample(route.name),
                    version = version,
                    open = { stack.add(it) },
                ) { id, voice ->
                    if (route.name != null) set(route.name, (roleOf(route.name) ?: plain).copy(voice = id))
                    else if (voice != null) { model.player?.choose(voice, language); version++ }
                }
                is CastRoute.Clone -> {
                    val curSentence = model.player?.spot?.sentence ?: model.sentence
                    val nextLine = remember(section, curSentence) {
                        val all = runCatching { core.spokenSentences(model.bookID, section.toUInt()) }.getOrDefault(emptyList())
                        all.drop(curSentence + 1).firstOrNull { s -> s.any { it.isLetterOrDigit() } }
                            ?: sample(route.name)
                    }
                    VoiceClonePanel(
                        core = core,
                        dataDir = model.library.dataDir,
                        language = language,
                        previewSentence = nextLine,
                        bookTitle = model.detail?.summary?.title,
                        replacingId = route.replacingId,
                        initialName = route.initialName ?: route.name,
                        onCancel = { stack.removeAt(stack.lastIndex) },
                        onSaved = { card ->
                            names[card.id] = card.name
                            val voice: BookVoice = if (app.yuyue.speech.LiveVoiceModel.canRun(model.library.dataDir)) {
                                app.yuyue.speech.CardVoice(card)
                            } else {
                                ClonedVoices.find(card.id) ?: app.yuyue.speech.CardVoice(card)
                            }
                            if (route.name != null) set(route.name, (roleOf(route.name) ?: plain).copy(voice = card.id))
                            else { model.player?.choose(voice, language); version++ }
                            stack.removeAt(stack.lastIndex)
                        },
                    )
                }
                is CastRoute.CloneDetail -> {
                    ClonedCardDetailPanel(
                        core = core,
                        dataDir = model.library.dataDir,
                        cardId = route.cardId,
                        language = language,
                        sampleLine = sample(null),
                        onBack = {
                            version++
                            stack.removeAt(stack.lastIndex)
                        },
                        onDeleted = { deletedId ->
                            names.remove(deletedId)
                            for (r in roles) {
                                if (r.voice.voice == deletedId) {
                                    set(r.name, r.voice.copy(voice = null))
                                }
                            }
                            version++
                            stack.removeAt(stack.lastIndex)
                        },
                    )
                }
            }
        }
    }
    menuFor?.let { m ->
        CastPersonMenu(core, model.bookID, m, open = { stack.add(CastRoute.Person(m.name)) }, mergeOther = { mergingFrom = m.name }, close = { menuFor = null }, changed = ::changed)
    }
    mergingFrom?.let { from ->
        PeoplePicker(core, model.bookID, section.toUInt(), t("%@合并到", from), from, { mergingFrom = null }) { into ->
            mergingFrom = null
            kotlinx.coroutines.MainScope().launch {
                withContext(Dispatchers.IO) { runCatching { core.dramaEditCast(model.bookID, uniffi.yuyue_core.CastEdit.Merge(into, from)) } }
                changed()
            }
        }
    }
}

private val names = HashMap<String, String>()
private fun roleVoiceName(core: uniffi.yuyue_core.YuyueCore, bookID: String, id: String): String? =
    names[id] ?: core.cardFind(bookID, id)?.name?.also { names[id] = it } ?: ClonedVoices.find(id)?.name?.also { names[id] = it }

private fun scopePreview(model: ReaderModel, language: String, v: RoleVoice, text: String, play: (BookVoice, String) -> Unit) {
    kotlinx.coroutines.MainScope().launch {
        val base = v.voice?.let { Voices.find(it, model.bookID) } ?: model.player?.currentVoice(language) ?: return@launch
        play(if (v.rate == 1.0 && v.pitch == 0.0) base else TunedVoice(base, v.rate.toFloat(), v.pitch.toFloat()), text)
    }
}

@Composable
private fun Avatar(text: String?, c: Color) {
    Box(Modifier.size(26.dp).background(if (text == null) YY.c.inset else c.copy(alpha = 0.18f), CircleShape), contentAlignment = Alignment.Center) {
        if (text == null) Icon(Icons.book, null, tint = YY.c.t3, modifier = Modifier.size(13.dp))
        else Text(text, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = c)
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PersonRow(avatar: String?, c: Color, name: String, detail: String, voice: String, own: Boolean, onLongClick: (() -> Unit)? = null, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().combinedClickable(onClick = onClick, onLongClick = onLongClick).padding(vertical = 9.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Avatar(avatar, c)
        Column(Modifier.weight(1f)) {
            Text(name, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
            Text(detail, fontSize = 10.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
        }
        Text(voice, fontSize = 10.sp, fontWeight = if (own) FontWeight.SemiBold else FontWeight.Normal, color = if (own) YY.c.t1 else YY.c.t3, maxLines = 1)
        Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(11.dp))
    }
}

@Composable
private fun CastList(cast: List<CastMember>, roles: List<Role>, model: ReaderModel, language: String, longPress: (CastMember) -> Unit, open: (CastRoute) -> Unit) {
    var narrator by remember { mutableStateOf("") }
    var all by remember { mutableStateOf<List<BookVoice>>(emptyList()) }
    LaunchedEffect(Unit) {
        narrator = model.player?.currentVoice(language)?.name ?: ""
        all = Voices.all(language, model.bookID) + ClonedVoices.all()
        for (v in all) names[v.id] = v.name
    }
    YYGroup(Modifier.padding(top = 8.dp)) {
        row { PersonRow(null, YY.c.t3, t("旁白"), t("没单独设声音的人物也用这个"), narrator, true) { open(CastRoute.Voice(null)) } }
    }
    val here = cast.filter { it.linesHere > 0u }
    val before = cast.filter { it.linesHere == 0u }
    for ((title, list) in listOf(t("这一章") to here, t("前面出场过") to before)) {
        if (list.isEmpty()) continue
        YYLabel(title, if (list === here) t("%lld 人", list.size) else t("读到这里共 %lld 人", cast.size))
        YYGroup {
            for (m in list) row {
                val role = roles.firstOrNull { it.name == m.name }?.voice
                val v = role?.voice?.let { all.firstOrNull { a -> a.id == it }?.name }
                val detail = (listOfNotNull(m.aliases.take(2).joinToString(t("、")).ifEmpty { null }, t("%u 句", m.linesSoFar.toInt())) +
                    listOfNotNull(if (role?.right == true) t("右边") else null)).joinToString(" · ")
                PersonRow(m.avatar, color(m.color), m.name, detail, v ?: t("跟旁白"), v != null, onLongClick = { longPress(m) }) { open(CastRoute.Person(m.name)) }
            }
        }
    }
    YYNote(if (cast.isEmpty()) t("读到这里还没认出谁在说话。对话多的书读几章后，这里会列出说过话的人。") else t("点一个人：声音、快慢高低、气泡放左边还是右边。改了从下一段起生效，跟着同步到你的其他设备。"))
}

@Composable
private fun PersonPage(
    name: String, member: CastMember?, role: RoleVoice, sample: String, voiceName: (String?) -> String?,
    open: (CastRoute) -> Unit, set: (RoleVoice) -> Unit, preview: (RoleVoice) -> Unit,
) {
    var v by remember(role) { mutableStateOf(role) }
    Row(Modifier.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Box(Modifier.size(34.dp).background(color(member?.color ?: "#888888").copy(alpha = 0.18f), CircleShape), contentAlignment = Alignment.Center) {
            Text(member?.avatar ?: name.takeLast(1), fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = color(member?.color ?: "#888888"))
        }
        Text(listOfNotNull(member?.aliases?.joinToString(t("、"))?.ifEmpty { null }, member?.let { t("%u 句", it.linesSoFar.toInt()) }).joinToString(" · "), fontSize = 10.sp, color = YY.c.t3, style = YY.tnum)
    }
    YYGroup {
        row {
            Row(Modifier.fillMaxWidth().heightIn(min = 40.dp).clickable { open(CastRoute.Voice(name)) }, verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.waveform, null, tint = YY.c.t2, modifier = Modifier.size(15.dp))
                Spacer(Modifier.width(10.dp))
                Text(t("声音"), fontSize = 11.5.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
                Text(voiceName(v.voice) ?: t("跟旁白"), fontSize = 10.5.sp, color = YY.c.t3)
                Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(11.dp))
            }
        }
        row {
            Row(Modifier.fillMaxWidth().heightIn(min = 40.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(t("气泡放在"), fontSize = 11.5.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
                YYSegments(listOf(false to t("左边"), true to t("右边")), v.right, { v = v.copy(right = it); set(v) }, small = true, modifier = Modifier.widthIn(min = 120.dp))
            }
        }
    }
    Column(Modifier.padding(top = 12.dp).fillMaxWidth().background(YY.c.card, RoundedCornerShape(16.dp)).padding(14.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Tune(t("快慢 %@×", "%.1f".format(v.rate)), t("在全书速度上再快或慢一点"), v.rate.toFloat(), 0.7f..1.4f) { v = v.copy(rate = it.toDouble()) ; set(v) }
        Tune(t("高低 %@", if (v.pitch == 0.0) "0" else "%+.0f".format(v.pitch)), t("声音高一点或低一点"), v.pitch.toFloat(), -6f..6f, steps = 11) { v = v.copy(pitch = it.toDouble()); set(v) }
    }
    Row(
        Modifier.padding(top = 12.dp).fillMaxWidth().background(YY.c.card, RoundedCornerShape(16.dp)).clickable { preview(v) }.padding(12.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Box(Modifier.size(28.dp).background(YY.c.inset, CircleShape), contentAlignment = Alignment.Center) { Icon(Icons.play, null, tint = YY.c.t1, modifier = Modifier.size(12.dp)) }
        Column(Modifier.weight(1f)) {
            Text("“$sample”", fontSize = 11.5.sp, color = YY.c.t1, maxLines = 2)
            Text(t("%@的一句 · 点一下试听", name), fontSize = 10.sp, color = YY.c.t3)
        }
    }
    if (v != plain) {
        Text(t("改回跟旁白"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1,
            modifier = Modifier.padding(top = 12.dp).background(YY.c.inset, CircleShape).clickable { v = plain; set(plain) }.padding(horizontal = 14.dp, vertical = 8.dp))
    }
}

@Composable
private fun Tune(title: String, note: String, value: Float, range: ClosedFloatingPointRange<Float>, steps: Int = 0, change: (Float) -> Unit) {
    var local by remember(value) { mutableStateOf(value) }
    Column {
        Row {
            Text(title, fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, style = YY.tnum, modifier = Modifier.weight(1f).alignByBaseline())
            Text(note, fontSize = 10.sp, color = YY.c.t3, modifier = Modifier.alignByBaseline())
        }
        Slider(local, { local = it }, valueRange = range, steps = steps, onValueChangeFinished = { change(local) },
            colors = SliderDefaults.colors(thumbColor = YY.tint.fill, activeTrackColor = YY.tint.fill))
    }
}

@Composable
private fun VoicePage(
    name: String?,
    model: ReaderModel,
    language: String,
    roles: List<Role>,
    chosenID: String?,
    sample: String,
    version: Int,
    open: (CastRoute) -> Unit,
    pick: (String?, BookVoice?) -> Unit,
) {
    var narrator by remember { mutableStateOf<BookVoice?>(null) }
    var chosen by remember(chosenID) { mutableStateOf(chosenID) }
    LaunchedEffect(version) {
        narrator = model.player?.currentVoice(language)
        if (name == null) chosen = narrator?.id
        for (v in Voices.all(language, model.bookID) + ClonedVoices.all()) names[v.id] = v.name
    }
    VoiceGroupsContent(
        core = model.core,
        dataDir = model.library.dataDir,
        bookID = model.bookID,
        bookTitle = model.detail?.summary?.title,
        language = language,
        roleName = name,
        narratorVoice = narrator,
        roles = roles,
        selectedId = chosen,
        sampleText = sample,
        refreshKey = version,
        onPickFollowNarrator = if (name != null) {
            {
                chosen = null
                pick(null, narrator)
            }
        } else null,
        onPickVoice = { v ->
            names[v.id] = v.name
            chosen = v.id
            pick(v.id, v)
        },
        onOpenClone = { replacingId, initName ->
            open(CastRoute.Clone(name, replacingId, initName))
        },
        onOpenDetail = { cardId ->
            open(CastRoute.CloneDetail(cardId))
        },
    )
}
