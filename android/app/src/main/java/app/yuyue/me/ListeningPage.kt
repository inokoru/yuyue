package app.yuyue.me

import android.content.Intent
import android.speech.tts.TextToSpeech
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableStateSetOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.yuyue.R
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.RateMenu
import app.yuyue.design.YY
import app.yuyue.design.YYField
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.YYSlider
import app.yuyue.design.rateLabel
import app.yuyue.design.rememberPref
import app.yuyue.library.Keys
import app.yuyue.shelf.Field
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.Player
import app.yuyue.speech.Speech
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.PI
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin

// 设置 · 朗读, as the Mac's and the iPhone's: first where the voices come from, a row for this phone
// and each cloud; one opened, its voices as voice messages that each say the line you sent. Tapping
// one plays it; 选择 beside it makes it read that kind of book. The one reading is pinned on top.

/** This phone, or one of the clouds. */
private const val SYSTEM = "system"

private fun cloud(id: String) = CloudVoice.Provider.entries.firstOrNull { it.key == id }

internal fun voiceSourceTitle(id: String) = cloud(id)?.title ?: t("这台手机")

private val CloudVoice.Provider.blurb: String
    get() = when (this) {
        CloudVoice.Provider.OpenAI -> t("10 个音色，支持中英双语")
        CloudVoice.Provider.Azure -> t("中文音色丰富，含免费额度")
        CloudVoice.Provider.ElevenLabs -> t("自然度高，适合英文朗读")
    }

private val CloudVoice.Provider.keyPage: String
    get() = when (this) {
        CloudVoice.Provider.OpenAI -> "https://platform.openai.com/api-keys"
        CloudVoice.Provider.Azure -> "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices"
        CloudVoice.Provider.ElevenLabs -> "https://elevenlabs.io/app/settings/api-keys"
    }

private fun sourceOf(v: BookVoice): String = (v as? CloudVoice)?.provider?.key ?: SYSTEM

/** The two kinds of book a voice is chosen for. */
private enum class VoiceBook(val tag: String) {
    ZH("zh"), EN("en");

    val title: String get() = if (this == ZH) t("中文书") else t("英文书")
    val key: String get() = "player.voice.$tag"
    val line: String get() = if (this == ZH) "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。" else "It was the best of times, it was the worst of times."

    /** Lines to try a voice on, under “ at the start of the box. */
    val quickLines: List<Pair<String, String>>
        get() = if (this == ZH) listOf(t("叙述") to line, t("对话") to "“这是谁？”我问。“这是闰土呀，你忘了么？”",
            t("数字") to "第 12 章，1986 年 3 月 4 日，下午 3:40。", t("中英夹杂") to "Hello，今天读到 Chapter 3 了。")
        else listOf("Narration" to line, "Dialogue" to "\"Is it you?\" she asked. \"Of course it is,\" he said.", "Numbers" to "Chapter 12, page 304: the year was 1859.")
}

/** What 朗读's pages share while it's open: the line each kind of book is tried on, the voices
 *  heard, the one playing. */
private object ListeningFocus {
    val lines = mutableStateMapOf<VoiceBook, String>()
    val heard = mutableStateSetOf<String>()
    var playing by mutableStateOf<String?>(null)
    /** When the playing one started and how long it should take, for its bubble to fill. */
    var startedAt by mutableStateOf(0L)
    var lasts by mutableStateOf(1L)
    /** The one tapped last: 选择 shows beside it. */
    var listening by mutableStateOf<String?>(null)
    var book by mutableStateOf(VoiceBook.ZH)
    private var job: Job? = null

    fun line(b: VoiceBook) = lines[b] ?: b.line

    fun stop() {
        job?.cancel()
        SystemVoice.Engine.stopPreview()
        playing = null
    }

    fun listen(v: BookVoice, scope: kotlinx.coroutines.CoroutineScope) {
        if (playing == v.id) { stop(); return }
        stop()
        listening = v.id
        heard += v.id
        playing = v.id
        val text = line(book)
        val secs = seconds(text)
        job = scope.launch {
            if (v is SystemVoice) {
                if (v.preview(text)) {
                    startedAt = System.currentTimeMillis(); lasts = secs * 1000L
                    delay(secs * 1000L)
                }
            } else {
                startedAt = 0L
                val s = runCatching { withContext(Dispatchers.IO) { v.render(text, book.tag) } }.getOrNull()
                if (s != null) {
                    startedAt = System.currentTimeMillis(); lasts = s.samples.size * 1000L / s.rate
                    SystemVoice.Engine.preview(s)
                    delay(lasts)
                }
            }
            playing = null
        }
    }

    fun pick(v: BookVoice) {
        Prefs.set(VoiceBook.entries.first { it == book }.key, v.id)
        SettingsLog.voiceNames[v.id] = v.name
    }

    /** About how long the line takes at this speed. */
    fun seconds(text: String): Int {
        val latin = text.count { it.code < 128 } > text.length / 2
        val r = Prefs.float(Player.RATE_KEY, 1f).takeIf { it > 0 } ?: 1f
        return max(2, ((text.length * (if (latin) 0.07 else 0.2)) / r).roundToInt())
    }
}

/** A source's mark on its white tile: this phone, a cloud's logo, or ElevenLabs' E. */
@Composable
private fun SourceTile(id: String, size: Float = 26f) {
    ServiceTile(size) {
        when (cloud(id)) {
            null -> Icon(Icons.phone, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(size * 0.58f)))
            CloudVoice.Provider.OpenAI -> Icon(painterResource(R.drawable.provider_openai), null, tint = YY.c.t1, modifier = Modifier.size(YY.s(size * 0.62f)))
            CloudVoice.Provider.Azure -> Image(painterResource(R.drawable.brand_azure), null, modifier = Modifier.size(YY.s(size * 0.6f)))
            CloudVoice.Provider.ElevenLabs -> Text("E", style = YY.font(size * 0.45f, FontWeight.SemiBold), color = YY.c.t2)
        }
    }
}

/** 朗读: 声音来源, then 语速. */
@Composable
fun ListeningPage(open: (String) -> Unit) {
    val context = LocalContext.current
    val lib = app.yuyue.YuyueApp.instance.library
    val liveState = app.yuyue.speech.LiveVoiceModel.state
    var rate by rememberPref(Player.RATE_KEY, 1f)
    val zh by rememberPref(VoiceBook.ZH.key, "")
    val en by rememberPref(VoiceBook.EN.key, "")
    var localCount by remember { mutableIntStateOf(0) }
    /** Who reads each kind of book now: the one chosen, or the best this phone has. */
    var reading by remember { mutableStateOf<Map<VoiceBook, BookVoice>>(emptyMap()) }
    LaunchedEffect(zh, en, liveState) {
        SystemVoice.Engine.init(context)
        val system = SystemVoice.Engine.voices()
        val cards = if (app.yuyue.speech.LiveVoiceModel.ready(lib.dataDir)) {
            runCatching { lib.core.cardsBuiltin() }.getOrDefault(emptyList())
        } else emptyList()
        val cloned = runCatching { lib.core.cardsCloned() }.getOrDefault(emptyList())
        localCount = system.size + cards.size + cloned.size
        SettingsLog.voiceNames += system.associate { it.id to it.name }
        SettingsLog.voiceNames += cards.associate { it.id to it.name }
        SettingsLog.voiceNames += cloned.associate { it.id to it.name }
        reading = VoiceBook.entries.mapNotNull { b ->
            val id = Prefs.string(b.key)
            val v = (if (id.isNotEmpty()) Voices.find(id)?.takeIf { it.speaks(b.tag) } else null) ?: Voices.best(b.tag)
            v?.let { b to it }
        }.toMap()
    }
    YYPage {
        SettingsLead(t("朗读、查词发音与播客音色"))
        YYLabel(t("声音来源"))
        YYGroup {
            for (id in listOf(SYSTEM) + CloudVoice.Provider.entries.map { it.key }) row {
                val p = cloud(id)
                val on = p == null || p.isSetUp
                Row(Modifier.fillMaxWidth().clickable { open(id) }.padding(vertical = YY.s(9)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    SourceTile(id, 30f)
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                        Text(voiceSourceTitle(id), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                        Text(
                            when {
                                p == null -> t("%lld 个音色 · 本地免费，无需联网", localCount)
                                on -> t("%lld 个音色 · 已连接", CloudVoice.all().count { it.provider == p })
                                else -> p.blurb
                            },
                            style = YY.font(10f), color = YY.c.t3, maxLines = 1,
                        )
                        // 📌 中文书 · 婷婷: the books it reads, under its name on a phone's narrow row.
                        for ((b, v) in reading.entries.sortedBy { it.key.ordinal }) if (sourceOf(v) == id) {
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(3)), modifier = Modifier.padding(top = YY.s(1))) {
                                Icon(Icons.pin, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(9)))
                                Text("${b.title} · ${v.name}", style = YY.font(10f), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                    if (!on) Text(t("未连接"), style = YY.font(10.5f), color = YY.c.t3)
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                }
            }
        }
        YYNote(t("云端语音使用你自己的 API Key，费用由服务商直接结算；已合成的句子缓存在本机，重听不重复计费。"))
        YYLabel(t("语速"))
        YYGroup {
            row {
                val shown = if (rate == 0f) 1f else rate
                Row(Modifier.fillMaxWidth().heightIn(min = YY.s(40)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                    Text(t("慢"), style = YY.font(10f), color = YY.c.t3)
                    YYSlider(shown, { rate = (it * 10).roundToInt() / 10f }, 0.8f..2f, Modifier.weight(1f), description = t("语速"), state = rateLabel(shown))
                    Text(t("快"), style = YY.font(10f), color = YY.c.t3)
                    RateMenu(shown, listOf(0.8f, 1f, 1.2f, 1.5f, 2f)) { rate = it }
                }
            }
        }
        YYNote(t("与阅读页听书面板同步。"))
    }
}

// MARK: One source's voices

/** 中文书 / 英文书 and a hint; the one reading, pinned; your line and every other voice as voice
 *  messages, which alone scroll; the box to send them another line. A cloud not connected yet is
 *  its key instead. */
@Composable
fun VoiceSourcePage(id: String, openSettings: () -> Unit) {
    val context = LocalContext.current
    val lib = app.yuyue.YuyueApp.instance.library
    val liveState = app.yuyue.speech.LiveVoiceModel.state
    val p = cloud(id)
    var version by remember { mutableIntStateOf(0) }
    val setUp = remember(version) { p == null || p.isSetUp }
    if (!setUp && p != null) {
        YYPage { CloudKeyPanel(p) { version++ } }
        return
    }
    val scope = rememberCoroutineScope()
    val focus = ListeningFocus
    val book = focus.book
    val chosenID by rememberPref(book.key, "")
    var voices by remember { mutableStateOf<List<BookVoice>?>(null) }
    var current by remember { mutableStateOf<BookVoice?>(null) }
    var draft by remember { mutableStateOf("") }
    var linesOpen by remember { mutableStateOf(false) }
    var pickerOpen by remember { mutableStateOf(false) }
    LaunchedEffect(book, chosenID, version, liveState) {
        SystemVoice.Engine.init(context)
        val all = Voices.all(book.tag)
        voices = all.filter { sourceOf(it) == id }
        current = (if (chosenID.isNotEmpty()) Voices.find(chosenID)?.takeIf { it.speaks(book.tag) } else null) ?: Voices.best(book.tag)
        SettingsLog.voiceNames += all.associate { it.id to it.name }
    }
    androidx.compose.runtime.DisposableEffect(Unit) { onDispose { focus.stop() } }
    fun send(text: String) {
        if (text.isBlank()) return
        focus.lines[book] = text.trim()
        draft = ""
        linesOpen = false
        current?.let { focus.listen(it, scope) }
    }
    Box(Modifier.fillMaxSize().background(YY.c.bg)) {
        Column(Modifier.fillMaxSize().padding(horizontal = YY.s(16))) {
            Row(Modifier.padding(top = YY.s(4), bottom = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
                BookSegments(book) { focus.book = it; focus.listening = null; focus.stop() }
                Spacer(Modifier.weight(1f))
                if (p == null) {
                    Text(
                        t("克隆声音"),
                        style = YY.font(10.5f, FontWeight.Medium),
                        color = YY.tint.accent,
                        modifier = Modifier
                            .clip(RoundedCornerShape(6.dp))
                            .clickable { focus.stop(); pickerOpen = true }
                            .padding(horizontal = YY.s(6), vertical = YY.s(2)),
                    )
                } else {
                    Text(t("点一下听，再点“选择”"), style = YY.font(10f), color = YY.c.t3, maxLines = 1)
                }
            }
            if (p == null) {
                if (app.yuyue.speech.LiveVoiceModel.deviceUnsupported) {
                    Text(t("这台手机带不动"), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.padding(bottom = YY.s(6)))
                } else if (!app.yuyue.speech.LiveVoiceModel.ready(lib.dataDir)) {
                    Box(Modifier.padding(bottom = YY.s(8))) {
                        app.yuyue.reader.PrepareBetterVoiceRow(lib.core, lib.dataDir, book.tag)
                    }
                }
            }
            val others = voices.orEmpty().filter { it.id != current?.id }
            current?.let { PinnedVoice(it, book, elsewhere = sourceOf(it) != id, scope) }
            YourLine(focus.line(book), Modifier.padding(top = YY.s(10), bottom = YY.s(4)))
            LazyColumn(Modifier.weight(1f).fillMaxWidth()) {
                val list = voices
                if (list == null) item { Box(Modifier.fillMaxWidth().padding(YY.s(20)), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(YY.s(16)), color = YY.c.t3, strokeWidth = 1.5.dp) } }
                else if (others.isEmpty()) item {
                    Text(t("%@ 没有别的%@声音了", voiceSourceTitle(id), book.title), style = YY.font(10.5f), color = YY.c.t3, textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(top = YY.s(14)))
                } else {
                    item { DayLine(t("%lld 位", others.size)) }
                    items(others, key = { it.id }) { v -> VoiceMessage(v, book, scope) }
                }
                if (p == null) item {
                    Text(t("想要更自然的音色：点击右上角 ⚙ 下载语音数据，完成后即可使用。"), style = YY.font(10f), color = YY.c.t3, textAlign = TextAlign.Center,
                        modifier = Modifier.fillMaxWidth().padding(vertical = YY.s(14)))
                }
            }
            Composer(book, draft, { draft = it.take(120) }, linesOpen, { linesOpen = it }, ::send, Modifier.padding(top = YY.s(8), bottom = YY.s(10) + app.yuyue.LocalTabBarHeight.current))
        }
        if (linesOpen) Box(Modifier.fillMaxSize().clickable(indication = null, interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }) { linesOpen = false })
        // The ready lines grow out of the composer's corner, as the shelf's popups do.
        androidx.compose.animation.AnimatedVisibility(
            linesOpen, Modifier.align(Alignment.BottomStart).padding(start = YY.s(16), end = YY.s(16), bottom = YY.s(64) + app.yuyue.LocalTabBarHeight.current),
            enter = androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.quick()) + androidx.compose.animation.scaleIn(app.yuyue.design.YYMotion.quick(), initialScale = 0.92f, transformOrigin = androidx.compose.ui.graphics.TransformOrigin(0f, 1f)),
            exit = androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) + androidx.compose.animation.scaleOut(app.yuyue.design.YYMotion.exit(), targetScale = 0.92f, transformOrigin = androidx.compose.ui.graphics.TransformOrigin(0f, 1f)),
        ) {
            QuickLines(book, Modifier, send = ::send, fill = { draft = it; linesOpen = false })
        }
        if (pickerOpen) {
            app.yuyue.reader.VoicePickerSheet(
                core = lib.core,
                dataDir = lib.dataDir,
                bookID = null,
                bookTitle = null,
                language = book.tag,
                selectedId = current?.id,
                nextBookSentence = focus.line(book),
                onSelect = { v -> focus.pick(v); version++ },
                dismiss = { pickerOpen = false; version++ },
            )
        }
    }
}

/** 中文书 / 英文书: a small track, the chosen one lifted on white. */
@Composable
private fun BookSegments(selection: VoiceBook, choose: (VoiceBook) -> Unit) {
    Row(Modifier.background(YY.c.inset, RoundedCornerShape(8.dp)).padding(2.dp)) {
        for (b in VoiceBook.entries) {
            val on = b == selection
            Text(b.title, style = YY.font(11f, if (on) FontWeight.Medium else FontWeight.Normal), color = if (on) YY.c.t1 else YY.c.t2,
                modifier = Modifier.then(if (on) Modifier.shadow(1.dp, RoundedCornerShape(6.dp)).background(YY.c.card, RoundedCornerShape(6.dp)) else Modifier)
                    .clickable { choose(b) }.padding(horizontal = YY.s(10), vertical = YY.s(4)))
        }
    }
}

/** A line across, words in the middle: as a chat's date. */
@Composable
private fun DayLine(title: String) {
    Row(Modifier.fillMaxWidth().padding(top = YY.s(12), bottom = YY.s(4)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        Box(Modifier.weight(1f).height(0.5.dp).background(YY.c.line))
        Text(title, style = YY.font(10f), color = YY.c.t3)
        Box(Modifier.weight(1f).height(0.5.dp).background(YY.c.line))
    }
}

/** What every voice says back: your line, as your own dark bubble on the right, 我 beside it. */
@Composable
private fun YourLine(text: String, modifier: Modifier) {
    Row(modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(YY.s(8), Alignment.End), verticalAlignment = Alignment.Top) {
        Text(text, fontFamily = FontFamily.Serif, fontSize = YY.sp(12.5f), lineHeight = YY.sp(19f), color = YY.c.card,
            modifier = Modifier.padding(start = YY.s(20)).widthIn(max = YY.s(240)).background(YY.c.t1, RoundedCornerShape(YY.s(14))).padding(horizontal = YY.s(13), vertical = YY.s(9)))
        Box(Modifier.size(YY.s(32)).background(YY.c.inset, CircleShape), contentAlignment = Alignment.Center) {
            Text(t("我"), style = YY.font(12f, FontWeight.SemiBold), color = YY.c.t2)
        }
    }
}

/** A voice's own colours: a pale ground and a dark letter, picked from its id so it keeps them. */
private val voiceColors = listOf(
    Color(0xFFEBDDC8) to Color(0xFF6B4E33), Color(0xFFDCE5DB) to Color(0xFF34503F), Color(0xFFF2E6BF) to Color(0xFF6E5A1E),
    Color(0xFFDEDAEA) to Color(0xFF3E3A5A), Color(0xFFEDD2C3) to Color(0xFF7A3A24), Color(0xFFD6DCE4) to Color(0xFF2F3A4A),
    Color(0xFFDDD9D2) to Color(0xFF3C3A36), Color(0xFFF6DDD3) to Color(0xFF8B3B2A),
)

private fun colorsOf(id: String): Pair<Color, Color> {
    var n = 7u
    for (c in id) n = n * 31u + c.code.toUInt()
    return voiceColors[(n % voiceColors.size.toUInt()).toInt()]
}

/** A system voice's letter (声音 A → A); others, their name's first. */
private fun initial(v: BookVoice) = (if (v is SystemVoice) v.name.substringAfterLast(' ') else v.name).take(1).ifEmpty { "?" }

/** Its letter on its own colour; pinned, a little pin at its foot. */
@Composable
private fun VoiceAvatar(v: BookVoice, size: Float = 32f, pinned: Boolean = false) {
    val (ground, ink) = colorsOf(v.id)
    val latin = initial(v).first().code < 128
    Box {
        val avatarBorder = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.06f)
        Box(Modifier.size(YY.s(size)).background(ground, CircleShape).border(0.5.dp, avatarBorder, CircleShape), contentAlignment = Alignment.Center) {
            Text(initial(v), fontFamily = FontFamily.Serif, fontWeight = FontWeight.SemiBold, fontSize = YY.sp(size * (if (latin) 0.44f else 0.42f)), color = ink)
        }
        if (pinned) Box(
            Modifier.align(Alignment.BottomEnd).offset(x = 4.dp, y = 4.dp).size(YY.s(14)).shadow(1.dp, CircleShape).background(YY.c.card, CircleShape),
            contentAlignment = Alignment.Center,
        ) { Icon(Icons.pin, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(8))) }
    }
}

/** Bar heights for a voice, the same every time. */
private fun waveHeights(seed: String, bars: Int, tallest: Float): List<Float> {
    var x = seed.fold(17L) { a, c -> (a * 31 + c.code) % 233_280 }
    return (0 until bars).map { i ->
        x = (x * 9301 + 49297) % 233_280
        val k = sin(PI * (i + 0.5) / bars).pow(0.5) * (0.3 + 0.7 * x / 233_280.0)
        max(3f, (tallest * k).toFloat())
    }
}

/** A voice message: ▶, a little wave, the seconds; its bars darken as it plays. Dark for the one reading. */
@Composable
private fun VoiceBubble(v: BookVoice, book: VoiceBook, scope: kotlinx.coroutines.CoroutineScope, dark: Boolean = false, height: Float = 34f, widest: Float = 180f, lifted: Boolean = false) {
    val focus = ListeningFocus
    val text = focus.line(book)
    val secs = focus.seconds(text)
    val playing = focus.playing == v.id
    val bars = min(20, min(8 + secs * 2, max(4, ((widest - 94) / 4).toInt())))
    var now by remember { mutableStateOf(0L) }
    LaunchedEffect(playing, focus.startedAt) { while (playing) { now = System.currentTimeMillis(); delay(50) } }
    val heard = if (playing && focus.startedAt > 0) ((now - focus.startedAt).toFloat() / focus.lasts).coerceIn(0f, 1f) else 0f
    val ink = if (dark) YY.c.card.copy(alpha = 0.5f) else YY.c.t3.copy(alpha = 0.6f)
    val playedInk = if (dark) YY.c.card else YY.c.t1
    Row(
        Modifier.width(YY.s(min(widest, 66f + bars * 4 + 28))).heightIn(min = YY.s(height))
            .then(if (dark) Modifier else Modifier.shadow(0.5.dp, RoundedCornerShape(YY.s(14))))
            .clip(RoundedCornerShape(YY.s(14))).background(if (dark) YY.c.t1 else YY.c.card)
            .border(0.5.dp, if (dark) Color.Transparent else if (lifted) YY.c.t3 else YY.c.line, RoundedCornerShape(YY.s(14)))
            .clickable { focus.listen(v, scope) }.padding(start = YY.s(5), end = YY.s(11), top = YY.s(4), bottom = YY.s(4)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(7)),
    ) {
        Box(Modifier.size(YY.s(22)).background(if (dark) Color.White.copy(alpha = 0.16f) else YY.c.inset, CircleShape), contentAlignment = Alignment.Center) {
            if (playing && focus.startedAt == 0L) CircularProgressIndicator(Modifier.size(YY.s(10)), color = if (dark) YY.c.card else YY.c.t1, strokeWidth = 1.2.dp)
            else Icon(if (playing) Icons.pause else Icons.play, null, tint = if (dark) YY.c.card else YY.c.t1, modifier = Modifier.size(YY.s(11)))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(2.dp), verticalAlignment = Alignment.CenterVertically, modifier = Modifier.weight(1f)) {
            waveHeights(v.id, bars, 13f).forEachIndexed { i, h ->
                Box(Modifier.width(2.dp).height(h.dp).background(if (i < (heard * bars).toInt()) playedInk else ink, RoundedCornerShape(1.dp)))
            }
        }
        Text("$secs″", style = YY.font(10.5f, tnum = true), color = if (dark) YY.c.card.copy(alpha = 0.6f) else YY.c.t3)
    }
}

/** 选择: white, a hairline, a pin; once chosen, green ✓ 已选择. */
@Composable
private fun ChooseButton(chosen: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.heightIn(min = YY.s(26)).clip(RoundedCornerShape(50)).background(if (chosen) YY.c.ok.copy(alpha = 0.11f) else YY.c.card)
            .border(0.5.dp, if (chosen) Color.Transparent else YY.c.line, RoundedCornerShape(50))
            .clickable(enabled = !chosen, onClick = onClick).padding(start = YY.s(9), end = YY.s(11), top = YY.s(4), bottom = YY.s(4)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
    ) {
        Icon(if (chosen) Icons.check else Icons.pin, null, tint = if (chosen) YY.c.ok else YY.c.t1, modifier = Modifier.size(YY.s(11)))
        Text(if (chosen) t("已选择") else t("选择"), style = YY.font(11f, if (chosen) FontWeight.SemiBold else FontWeight.Medium), color = if (chosen) YY.c.ok else YY.c.t1)
    }
}

/** One voice in the list: its letter and bubble on one line, its name above; a red dot until heard;
 *  选择 beside the bubble once it has been tapped. */
@Composable
private fun VoiceMessage(v: BookVoice, book: VoiceBook, scope: kotlinx.coroutines.CoroutineScope) {
    val focus = ListeningFocus
    val open = focus.listening == v.id
    Row(
        Modifier.fillMaxWidth().clickable(indication = null, interactionSource = remember { androidx.compose.foundation.interaction.MutableInteractionSource() }) { focus.listen(v, scope) }
            .padding(vertical = YY.s(7)),
        verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        VoiceAvatar(v)
        Column(verticalArrangement = Arrangement.spacedBy(YY.s(4))) {
            Row(horizontalArrangement = Arrangement.spacedBy(YY.s(6)), verticalAlignment = Alignment.CenterVertically) {
                Text(v.name, style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                if (v is SystemVoice) listOfNotNull(v.qualityName, if (v.isOffline) null else t("联网")).takeIf { it.isNotEmpty() }?.let {
                    Text(it.joinToString(" · "), style = YY.font(10f), color = YY.c.t3, maxLines = 1)
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                VoiceBubble(v, book, scope, lifted = open)
                if (v.id !in focus.heard) Box(Modifier.size(6.dp).background(Color(0xFFE5483B), CircleShape))
                if (open) ChooseButton(false) { focus.pick(v) }
            }
        }
    }
}

/** The one reading this kind of book, pinned on top: a card of its own colour. */
@Composable
private fun PinnedVoice(v: BookVoice, book: VoiceBook, elsewhere: Boolean, scope: kotlinx.coroutines.CoroutineScope) {
    val (ground, ink) = colorsOf(v.id)
    Row(
        Modifier.fillMaxWidth().height(YY.s(44)).shadow(6.dp, RoundedCornerShape(YY.s(14)), ambientColor = Color(0x22332E29), spotColor = Color(0x33332E29))
            .clip(RoundedCornerShape(YY.s(14))).background(YY.c.card).background(if (YY.dark) ground.copy(alpha = 0.12f) else ground.copy(alpha = 0.34f))
            .border(0.5.dp, if (YY.dark) YY.c.line else ink.copy(alpha = 0.16f), RoundedCornerShape(YY.s(14))).padding(start = YY.s(8), end = YY.s(10)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
    ) {
        VoiceAvatar(v, 28f, pinned = true)
        // A system voice's name leads with its language; pinned on a narrow row, only 声音 A.
        Text(if (v is SystemVoice) v.name.substringAfterLast(" · ") else v.name, style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
        VoiceBubble(v, book, scope, dark = true, height = 30f, widest = 118f)
        Spacer(Modifier.weight(1f))
        Icon(Icons.pin, t("念你的%@", book.title), tint = if (YY.dark) YY.c.t2 else ink, modifier = Modifier.size(YY.s(14)))
        if (elsewhere) Text(voiceSourceTitle(sourceOf(v)), style = YY.font(10f, FontWeight.Medium), color = if (YY.dark) YY.c.t2 else ink, maxLines = 1)
    }
}

/** The box under the voices: “ for a ready line, what you type, how much of it, ↑ to send. */
@Composable
private fun Composer(book: VoiceBook, draft: String, change: (String) -> Unit, linesOpen: Boolean, openLines: (Boolean) -> Unit, send: (String) -> Unit, modifier: Modifier) {
    val ready = draft.isNotBlank()
    Row(
        modifier.fillMaxWidth().heightIn(min = maxOf(44.dp, YY.s(40))).shadow(1.dp, RoundedCornerShape(50)).clip(RoundedCornerShape(50)).background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(50)).padding(horizontal = YY.s(4)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
    ) {
        Box(Modifier.size(38.dp).clip(CircleShape).clickable(onClickLabel = t("换一句现成的")) { openLines(!linesOpen) }, contentAlignment = Alignment.Center) {
            Box(Modifier.size(YY.s(28)).background(if (linesOpen) YY.c.inset else Color.Transparent, CircleShape), contentAlignment = Alignment.Center) {
                Icon(Icons.quote, t("换一句现成的"), tint = if (linesOpen) YY.c.t1 else YY.c.t2, modifier = Modifier.size(YY.s(16)))
            }
        }
        Box(Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            if (draft.isEmpty()) Text(if (book == VoiceBook.ZH) t("想听他们念什么，写一句") else "Type a line for them to read", style = YY.font(11f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
            BasicTextField(draft, change, singleLine = true, textStyle = YY.font(12f).copy(color = YY.c.t1, fontFamily = FontFamily.Serif),
                cursorBrush = SolidColor(YY.tint.accent), keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
                keyboardActions = KeyboardActions(onSend = { send(draft) }), modifier = Modifier.fillMaxWidth())
        }
        if (draft.isNotEmpty()) Text("${draft.length} / 120", style = YY.font(10f, tnum = true), color = YY.c.t3)
        Box(Modifier.size(38.dp).clip(CircleShape).clickable(enabled = ready, onClickLabel = t("发给大家")) { send(draft) }, contentAlignment = Alignment.Center) {
            Box(Modifier.size(YY.s(28)).alpha(if (ready) 1f else 0.25f).background(YY.c.t1, CircleShape), contentAlignment = Alignment.Center) {
                Icon(Icons.send, t("发给大家"), tint = YY.c.card, modifier = Modifier.size(YY.s(14)))
            }
        }
    }
}

/** The ready lines, over the box: one sends it; 书里的一句 puts one in the box to change. */
@Composable
private fun QuickLines(book: VoiceBook, modifier: Modifier, send: (String) -> Unit, fill: (String) -> Unit) {
    Column(
        modifier.fillMaxWidth().shadow(16.dp, RoundedCornerShape(YY.s(12)), ambientColor = Color(0x29282420), spotColor = Color(0x29282420))
            .clip(RoundedCornerShape(YY.s(12))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12))).padding(YY.s(5)),
    ) {
        for ((name, line) in book.quickLines) {
            val using = ListeningFocus.line(book) == line
            QuickLine(if (using) t("%@ · 正在用", name) else name, line) { send(line) }
        }
        QuickLine(if (book == VoiceBook.ZH) t("书里的一句") else "From a book", if (book == VoiceBook.ZH) t("从《故乡》里随手取一句") else "A line from Pride and Prejudice") {
            fill(if (book == VoiceBook.ZH) "时候既然是深冬；渐近故乡时，天气又阴晦了。" else "It is a truth universally acknowledged.")
        }
    }
}

@Composable
private fun QuickLine(name: String, line: String, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(YY.s(8))).clickable(onClick = onClick).padding(horizontal = YY.s(10), vertical = YY.s(7)), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
        Text(name, style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t3)
        Text(line, fontFamily = FontFamily.Serif, fontSize = YY.sp(12f), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

// MARK: Keys and connections

/** A cloud not connected yet: its name and what it's good at; its Key typed straight in (Azure's
 *  region above it), tried once it looks whole; where to get one, as a row that opens the page. */
@Composable
private fun CloudKeyPanel(p: CloudVoice.Provider, connected: () -> Unit) {
    val context = LocalContext.current
    PanelHeader(p.title, p.blurb) { SourceTile(p.key, 34f) }
    KeyCard(p, connected = false, changed = connected)
    Spacer(Modifier.height(YY.s(10)))
    YYGroup { row { LinkRow(t("获取 API Key")) { openLink(context, p.keyPage) } } }
    YYNote(p.help + t("API Key 仅在本机加密保存，调用费用由服务商直接结算。"))
}

/** ⚙ on a source's page: this phone's voices and where to get better ones; a cloud's key, region,
 *  检查连接, and 断开 at the foot. */
@Composable
fun VoiceSourceSettings(id: String, back: () -> Unit) {
    val context = LocalContext.current
    val p = cloud(id)
    var count by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { SystemVoice.Engine.init(context); count = SystemVoice.Engine.voices().size }
    YYPage {
        if (p == null) {
            PanelHeader(t("这台手机"), t("系统自带")) { SourceTile(SYSTEM, 34f) }
            YYGroup {
                row { YYRow(t("声音"), value = t("%lld 个声音", count)) }
                row { YYRow(t("费用"), value = t("免费 · 不联网")) }
            }
            Spacer(Modifier.height(YY.s(10)))
            YYGroup {
                row {
                    LinkRow(t("下载更自然的声音")) {
                        SystemVoice.Engine.openSettings(context)
                    }
                }
            }
            YYNote(t("系统设置 › 无障碍 › 文字转语音，下载语音数据，回来就在这里。"))
        } else {
            var removing by remember { mutableStateOf(false) }
            val inUse = listOf("zh", "en").any { Prefs.string("player.voice.$it").startsWith("cloud.${p.key}.") }
            PanelHeader(p.title, if (inUse) t("当前使用") else p.blurb, if (inUse) YY.c.ok else YY.c.t3) { SourceTile(p.key, 34f) }
            KeyCard(p, connected = true) {}
            YYNote(t("粘贴后自动验证，有效才会替换。"))
            DestructiveCard(t("断开 %@", p.title)) { removing = true }
            if (removing) AlertDialog(
                onDismissRequest = { removing = false },
                containerColor = YY.c.card,
                title = { Text(t("断开 %@？", p.title), color = YY.c.t1) },
                text = { Text(t("将同时移除 API Key；使用该服务音色的书籍将切回本地音色。"), color = YY.c.t2) },
                confirmButton = {
                    TextButton({
                        Keys.delete(p.keyName)
                        Prefs.set(p.voicesName, null)
                        for (lang in listOf("zh", "en")) if (Prefs.string("player.voice.$lang").startsWith("cloud.${p.key}.")) Prefs.set("player.voice.$lang", "")
                        removing = false
                        back()
                        back()
                    }) { Text(t("断开"), color = YY.c.danger) }
                },
                dismissButton = { TextButton({ removing = false }) { Text(t("取消"), color = YY.c.t2) } },
            )
        }
    }
}

/** A cloud's key, in its card: Azure's region above it; tried as soon as it looks whole, so there
 *  is no 连接 to press. Connected, the key shows masked with 更换, and 检查连接 asks it again. */
@Composable
private fun KeyCard(p: CloudVoice.Provider, connected: Boolean, changed: () -> Unit) {
    var key by remember { mutableStateOf("") }
    var region by remember { mutableStateOf(Prefs.string(CloudVoice.Provider.REGION)) }
    var editing by remember { mutableStateOf(!connected) }
    var checking by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<Pair<Boolean, String>?>(null) }
    val scope = rememberCoroutineScope()
    fun check(newKey: String?) {
        checking = true
        outcome = null
        scope.launch {
            val old = Keys.read(p.keyName)
            val oldRegion = Prefs.string(CloudVoice.Provider.REGION)
            if (newKey != null) Keys.write(p.keyName, newKey)
            if (p == CloudVoice.Provider.Azure) Prefs.set(CloudVoice.Provider.REGION, region.trim().lowercase())
            outcome = try {
                val n = withContext(Dispatchers.IO) { CloudVoice.refresh(p) }
                if (newKey != null && connected) { editing = false; key = "" }
                true to t("正常 · %lld 个音色", n)
            } catch (e: Exception) {
                // A new key that doesn't work never replaces the one that did.
                if (newKey != null) { if (old != null) Keys.write(p.keyName, old) else Keys.delete(p.keyName) }
                Prefs.set(CloudVoice.Provider.REGION, oldRegion)
                false to (e.message ?: t("没连上"))
            }
            checking = false
            if (outcome?.first == true) { delay(700); changed() }
        }
    }
    LaunchedEffect(key, region) {
        val k = key.trim()
        if (!editing || k.length < 20 || (p == CloudVoice.Provider.Azure && region.isBlank())) return@LaunchedEffect
        delay(600)
        check(k)
    }
    YYGroup {
        if (p == CloudVoice.Provider.Azure) row { YYField(t("区域"), 44f) { Field(region, "eastasia") { region = it } } }
        row {
            YYField("Key", 44f) {
                if (editing) Secret(key, if (connected) t("贴上新的 Key") else p.keyHint) { key = it }
                else Row(verticalAlignment = Alignment.CenterVertically) {
                    val saved = Keys.read(p.keyName).orEmpty()
                    Text(if (saved.length <= 8) "•".repeat(saved.length) else "…" + saved.takeLast(4), style = YY.font(11.5f), color = YY.c.t2, modifier = Modifier.weight(1f))
                    Text(t("更换"), style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.clickable { editing = true }.padding(start = YY.s(8), top = YY.s(6), bottom = YY.s(6)))
                }
            }
        }
        if (connected || checking || outcome != null) row {
            Row(
                Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !checking) { check(if (editing) key.trim().ifEmpty { null } else null) },
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
            ) {
                Icon(Icons.waveform, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
                Text(t("检查连接"), style = YY.font(11.5f), color = YY.c.ok, modifier = Modifier.weight(1f))
                when {
                    checking -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                    outcome != null -> Text(outcome!!.second, style = YY.font(10.5f), color = if (outcome!!.first) YY.c.ok else YY.c.danger, maxLines = 2, textAlign = TextAlign.End)
                }
            }
        }
    }
}
