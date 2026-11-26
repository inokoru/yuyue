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
import androidx.compose.foundation.layout.FlowRow
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.yuyue.L10n
import app.yuyue.R
import app.yuyue.design.Icons
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.Prefs
import app.yuyue.design.RateMenu
import app.yuyue.design.YY
import app.yuyue.design.YYField
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.YYSheetHeader
import app.yuyue.design.YYSlider
import app.yuyue.design.pressable
import app.yuyue.design.rateLabel
import app.yuyue.design.rememberPref
import app.yuyue.library.Keys
import app.yuyue.shelf.Field
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.DailyQuote
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

/** Neural local Kokoro/Cloned voices, system voices, or one of the clouds. */
private const val NEURAL = "neural"
private const val SYSTEM = "system"

private fun cloud(id: String) = CloudVoice.Provider.entries.firstOrNull { it.key == id }

internal fun voiceSourceTitle(id: String) = when (id) {
    NEURAL -> t("语阅离线语音（Kokoro）")
    SYSTEM -> t("这台手机 · 系统语音")
    else -> cloud(id)?.title ?: t("这台手机 · 系统语音")
}

@Composable
private fun SourceBadgePill(text: String, tone: Int) {
    // tone: 0 = ok, 1 = warn, 2 = neutral
    val fg = when (tone) {
        0 -> YY.c.ok
        1 -> Color(0xFFD97706)
        else -> YY.c.t2
    }
    val bg = when (tone) {
        0 -> YY.c.ok.copy(alpha = 0.12f)
        1 -> Color(0xFFD97706).copy(alpha = 0.14f)
        else -> YY.c.inset
    }
    Text(
        text,
        style = YY.font(9.5f, FontWeight.SemiBold),
        color = fg,
        maxLines = 1,
        softWrap = false,
        modifier = Modifier
            .background(bg, RoundedCornerShape(YY.s(4)))
            .padding(horizontal = YY.s(5), vertical = YY.s(1)),
    )
}

private val CloudVoice.Provider.blurb: String
    get() = when (this) {
        CloudVoice.Provider.Edge -> t("免 Key 直连微软 Edge 自然神经语音，含晓晓、云希、Ava 等多语种音色")
        CloudVoice.Provider.Free -> t("0 MB 免下载、免注册免 Key，已读句子缓存在本机")
        CloudVoice.Provider.Volcengine -> t("豆包大模型语音，中文播客与小说自然度高")
        CloudVoice.Provider.Fish -> t("OpenAudio S1 高表现力中英音色与社区声音库")
        CloudVoice.Provider.Xiaomi -> t("中英 8 个精品音色，支持语气指令")
        CloudVoice.Provider.SiliconFlow -> t("CosyVoice2 音色与克隆，可复用书伴 Key")
        CloudVoice.Provider.DashScope -> t("千问3-TTS，约 0.1 元/千字，可复用书伴 Key")
        CloudVoice.Provider.MiniMax -> t("Speech-2.8 高拟真情绪与呼吸感")
        CloudVoice.Provider.OpenAI -> t("10 个音色，支持中英双语")
        CloudVoice.Provider.Azure -> t("中文音色丰富，每月 50 万字免费")
        CloudVoice.Provider.ElevenLabs -> t("自然度高，适合外文朗读")
        CloudVoice.Provider.Deepgram -> t("Aura-2 英文自然极速语音，注册含体验金")
        CloudVoice.Provider.VoiceStudio -> t("连接局域网电脑 3900 端口与克隆音色")
        CloudVoice.Provider.Voicebox -> t("连接局域网电脑 17493 端口多引擎语音")
        CloudVoice.Provider.MlxAudio -> t("连接局域网 Mac 8000 端口原生加速语音")
        CloudVoice.Provider.Speaches -> t("连接局域网电脑或 NAS Docker 语音服务")
        CloudVoice.Provider.Local -> t("GPT-SoVITS、Kokoro、unspeech 或局域网地址")
    }

private val CloudVoice.Provider.keyPage: String
    get() = when (this) {
        CloudVoice.Provider.Edge -> "https://www.microsoft.com/edge"
        CloudVoice.Provider.Free -> "https://modelscope.cn/studios/k2-fsa/text-to-speech"
        CloudVoice.Provider.Volcengine -> "https://console.volcengine.com/speech/app"
        CloudVoice.Provider.Fish -> "https://fish.audio/app/api-keys/"
        CloudVoice.Provider.Xiaomi -> "https://platform.xiaomimimo.com"
        CloudVoice.Provider.SiliconFlow -> "https://cloud.siliconflow.cn/account/ak"
        CloudVoice.Provider.DashScope -> "https://bailian.console.aliyun.com/?apiKey=1"
        CloudVoice.Provider.MiniMax -> "https://platform.minimaxi.com/user-center/basic-information/interface-key"
        CloudVoice.Provider.OpenAI -> "https://platform.openai.com/api-keys"
        CloudVoice.Provider.Azure -> "https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices"
        CloudVoice.Provider.ElevenLabs -> "https://elevenlabs.io/app/settings/api-keys"
        CloudVoice.Provider.Deepgram -> "https://console.deepgram.com"
        CloudVoice.Provider.VoiceStudio -> "https://voicestudio.sh"
        CloudVoice.Provider.Voicebox -> "https://github.com/jamiepine/voicebox"
        CloudVoice.Provider.MlxAudio -> "https://github.com/Blaizzy/mlx-audio"
        CloudVoice.Provider.Speaches -> "https://speaches.ai"
        CloudVoice.Provider.Local -> "https://github.com/moeru-ai/unspeech"
    }

private fun sourceOf(v: BookVoice): String = when (v) {
    is CloudVoice -> v.provider.key
    is SystemVoice -> SYSTEM
    else -> NEURAL
}

/** The two kinds of book a voice is chosen for. */
private enum class VoiceBook(val tag: String) {
    ZH("zh"), EN("en");

    val title: String get() = if (this == ZH) t("中文书") else t("英文书")
    val key: String get() = "player.voice.$tag"
    val line: String get() = DailyQuote.current(tag).text

    /** Lines to try a voice on, under “ at the start of the box. */
    val quickLines: List<Pair<String, String>>
        get() = if (this == ZH) listOf(
            t("对话") to "“这是谁？”我问。“这是闰土呀，你忘了么？”",
            t("数字") to "第 12 章，1986 年 3 月 4 日，下午 3:40。",
            t("中英夹杂") to "Hello，今天读到 Chapter 3 了。",
        )
        else listOf(
            "Dialogue" to "\"Is it you?\" she asked. \"Of course it is,\" he said.",
            "Numbers" to "Chapter 12, page 304: the year was 1859.",
        )

    companion object {
        fun defaultForUi(): VoiceBook = if (L10n.chinese) ZH else EN
    }
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
    var failedId by mutableStateOf<String?>(null)
    var failedMessage by mutableStateOf<String?>(null)
    var book by mutableStateOf(VoiceBook.defaultForUi())
        private set
    private var syncedChinese = L10n.chinese
    private var userPickedBook = false
    private var job: Job? = null

    fun syncDefaultBook() {
        val chinese = L10n.chinese
        if (!userPickedBook || syncedChinese != chinese) {
            syncedChinese = chinese
            userPickedBook = false
            book = VoiceBook.defaultForUi()
        }
    }

    fun selectBook(target: VoiceBook) {
        syncedChinese = L10n.chinese
        userPickedBook = true
        book = target
    }

    fun hasCustomLine(b: VoiceBook) = lines.containsKey(b)
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
        failedId = null
        failedMessage = null
        heard += v.id
        playing = v.id
        val text = line(book)
        val secs = seconds(text)
        job = scope.launch {
            if (v is SystemVoice) {
                if (v.preview(text)) {
                    startedAt = System.currentTimeMillis(); lasts = secs * 1000L
                    delay(secs * 1000L)
                } else {
                    failedId = v.id
                    failedMessage = t("试听暂不可用，请检查系统语音设置或稍后再试")
                }
            } else {
                startedAt = 0L
                val result = runCatching { withContext(Dispatchers.IO) { v.render(text, book.tag) } }
                val s = result.getOrNull()
                if (s != null) {
                    startedAt = System.currentTimeMillis(); lasts = s.samples.size * 1000L / s.rate
                    if (SystemVoice.Engine.preview(s)) {
                        delay(lasts)
                    } else {
                        failedId = v.id
                        failedMessage = t("试听暂不可用，请检查系统语音设置或稍后再试")
                    }
                } else {
                    val err = result.exceptionOrNull()
                    if (err !is kotlinx.coroutines.CancellationException) {
                        failedId = v.id
                        failedMessage = err?.message?.ifBlank { null } ?: t("试听暂不可用，请检查网络或稍后再试")
                    }
                }
            }
            playing = null
        }
    }

    fun pick(v: BookVoice) {
        val target = VoiceBook.entries.first { it == book }
        Player.saveVoicePreference(target.tag, v.id)
        SettingsLog.voiceNames[v.id] = v.name
    }

    /** About how long the line takes at this speed. */
    fun seconds(text: String): Int {
        val latin = text.count { it.code < 128 } > text.length / 2
        val r = Prefs.float(Player.RATE_KEY, 1f).takeIf { it > 0 } ?: 1f
        return max(2, ((text.length * (if (latin) 0.07 else 0.2)) / r).roundToInt())
    }
}

/** A source's mark on its white tile. */
@Composable
private fun SourceTile(id: String, size: Float = 26f) {
    ServiceTile(size) {
        if (id == NEURAL) {
            Box(
                Modifier.size(YY.s(size * 0.64f)).background(Color(0xFF0F766E), RoundedCornerShape(YY.s(size * 0.16f))),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.waveform, null, tint = Color.White, modifier = Modifier.size(YY.s(size * 0.42f)))
            }
            return@ServiceTile
        }
        when (val p = cloud(id)) {
            null -> Icon(Icons.phone, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(size * 0.58f)))
            CloudVoice.Provider.Edge -> Image(painterResource(R.drawable.brand_edge), null, modifier = Modifier.size(YY.s(size * 0.64f)))
            CloudVoice.Provider.Free -> Box(
                Modifier.size(YY.s(size * 0.64f)).background(Color(0xFF16A34A), RoundedCornerShape(YY.s(size * 0.16f))),
                contentAlignment = Alignment.Center,
            ) {
                Text("免", style = YY.font(size * 0.34f, FontWeight.Bold), color = Color.White)
            }
            CloudVoice.Provider.Volcengine -> Box(
                Modifier.size(YY.s(size * 0.64f)).background(Color(0xFF1D4ED8), RoundedCornerShape(YY.s(size * 0.16f))),
                contentAlignment = Alignment.Center,
            ) {
                Text("豆", style = YY.font(size * 0.34f, FontWeight.Bold), color = Color.White)
            }
            CloudVoice.Provider.Fish -> Box(
                Modifier.size(YY.s(size * 0.64f)).background(Color(0xFF0891B2), RoundedCornerShape(YY.s(size * 0.16f))),
                contentAlignment = Alignment.Center,
            ) {
                Text("F", style = YY.font(size * 0.36f, FontWeight.Bold), color = Color.White)
            }
            CloudVoice.Provider.Xiaomi -> Box(
                Modifier.size(YY.s(size * 0.64f)).background(Color(0xFFFF6900), RoundedCornerShape(YY.s(size * 0.16f))),
                contentAlignment = Alignment.Center,
            ) {
                Text("mi", style = YY.font(size * 0.34f, FontWeight.Bold), color = Color.White)
            }
            CloudVoice.Provider.SiliconFlow -> Image(painterResource(R.drawable.provider_siliconflow), null, modifier = Modifier.size(YY.s(size * 0.6f)))
            CloudVoice.Provider.DashScope -> Image(painterResource(R.drawable.provider_dashscope), null, modifier = Modifier.size(YY.s(size * 0.6f)))
            CloudVoice.Provider.MiniMax -> Image(painterResource(R.drawable.provider_minimax), null, modifier = Modifier.size(YY.s(size * 0.6f)))
            CloudVoice.Provider.OpenAI -> Icon(painterResource(R.drawable.provider_openai), null, tint = YY.c.t1, modifier = Modifier.size(YY.s(size * 0.62f)))
            CloudVoice.Provider.Azure -> Image(painterResource(R.drawable.brand_azure), null, modifier = Modifier.size(YY.s(size * 0.6f)))
            CloudVoice.Provider.ElevenLabs -> Text("E", style = YY.font(size * 0.45f, FontWeight.SemiBold), color = YY.c.t2)
            CloudVoice.Provider.Deepgram -> Text("D", style = YY.font(size * 0.45f, FontWeight.SemiBold), color = YY.c.t2)
            CloudVoice.Provider.VoiceStudio, CloudVoice.Provider.Voicebox, CloudVoice.Provider.MlxAudio, CloudVoice.Provider.Speaches, CloudVoice.Provider.Local ->
                Icon(Icons.waveformEcg, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(size * 0.54f)))
        }
    }
}

/** 朗读: 声音来源, then 语速. */
@Composable
fun ListeningPage(open: (String) -> Unit) {
    val chineseUi = L10n.chinese
    LaunchedEffect(chineseUi) { ListeningFocus.syncDefaultBook() }
    val context = LocalContext.current
    val lib = app.yuyue.YuyueApp.instance.library
    val kokoroState = app.yuyue.speech.KokoroModel.state
    val liveState = app.yuyue.speech.LiveVoiceModel.state
    var rate by rememberPref(Player.RATE_KEY, 1f)
    val zh by rememberPref(VoiceBook.ZH.key, "")
    val en by rememberPref(VoiceBook.EN.key, "")
    var adding by remember { mutableStateOf(false) }
    var neuralCount by remember { mutableIntStateOf(0) }
    var systemCount by remember { mutableIntStateOf(0) }
    var reading by remember { mutableStateOf<Map<VoiceBook, BookVoice>>(emptyMap()) }
    LaunchedEffect(zh, en, kokoroState, liveState) {
        SystemVoice.Engine.init(context)
        val system = SystemVoice.Engine.voices()
        val kokoro = app.yuyue.speech.KokoroModel.voices(lib.core, lib.dataDir)
        val cards = if (app.yuyue.speech.LiveVoiceModel.ready(lib.dataDir)) {
            runCatching { lib.core.cardsBuiltin() }.getOrDefault(emptyList())
        } else emptyList()
        val cloned = runCatching { lib.core.cardsCloned() }.getOrDefault(emptyList())
        neuralCount = kokoro.size + cards.size + cloned.size
        systemCount = system.size
        SettingsLog.voiceNames += kokoro.associate { it.id to it.name }
        SettingsLog.voiceNames += system.associate { it.id to it.name }
        SettingsLog.voiceNames += cards.associate { it.id to it.name }
        SettingsLog.voiceNames += cloned.associate { it.id to it.name }
        reading = VoiceBook.entries.mapNotNull { b ->
            val id = Prefs.string(b.key)
            val v = (if (id.isNotEmpty()) Voices.find(id)?.takeIf { it.speaks(b.tag) } else null) ?: Voices.best(b.tag)
            v?.let { b to it }
        }.toMap()
    }
    val activeIds = listOf(NEURAL) + CloudVoice.Provider.entries.filter { it.isSetUp }.map { it.key } + listOf(SYSTEM)
    YYPage {
        SettingsLead(t("朗读、查词发音与播客音色"))
        YYLabel(t("声音来源"))
        YYGroup {
            for (id in activeIds) row {
                val p = cloud(id)
                Row(Modifier.fillMaxWidth().clickable { open(id) }.padding(vertical = YY.s(9)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    SourceTile(id, 30f)
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                        FlowRow(
                            Modifier.fillMaxWidth(),
                            horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
                            verticalArrangement = Arrangement.spacedBy(YY.s(3)),
                            itemVerticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(voiceSourceTitle(id), style = YY.font(11.5f, FontWeight.Medium), color = if (id == SYSTEM) YY.c.t2 else YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            when {
                                id == NEURAL -> SourceBadgePill(t("推荐"), 0)
                                id == SYSTEM -> SourceBadgePill(t("不推荐"), 1)
                                p?.badge != null -> SourceBadgePill(p.badge!!, if (p.isFreeOnline || p == CloudVoice.Provider.Xiaomi) 0 else 2)
                            }
                        }
                        Text(
                            when {
                                id == NEURAL -> if (app.yuyue.speech.KokoroModel.installed(lib.dataDir)) {
                                    t("%lld 个音色 · 跨端轻量离线，断网可用", neuralCount)
                                } else {
                                    t("一键下载（125 MB）· 跨端离线自然语音")
                                }
                                id == SYSTEM -> t("%lld 个音色 · 机械音明显，仅作断网兜底", systemCount)
                                p != null && p == CloudVoice.Provider.Edge -> t("%lld 个微软神经音色 · 免 Key 直连，已读缓存在本机", CloudVoice.all().count { it.provider == p })
                                p != null && p == CloudVoice.Provider.Free -> t("%lld 个音色 · 零配置免 Key 即点即听", CloudVoice.all().count { it.provider == p })
                                p != null && p.isLocal -> t("%lld 个音色 · 局域网语音", CloudVoice.all().count { it.provider == p })
                                p != null -> t("%lld 个音色 · 已连接", CloudVoice.all().count { it.provider == p })
                                else -> ""
                            },
                            style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis,
                        )
                        for ((b, v) in reading.entries.sortedBy { it.key.ordinal }) if (sourceOf(v) == id) {
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(3)), modifier = Modifier.padding(top = YY.s(1))) {
                                Icon(Icons.pin, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(9)))
                                Text("${b.title} · ${v.name}", style = YY.font(10f), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                }
            }
            row {
                Row(Modifier.fillMaxWidth().clickable { adding = true }.padding(vertical = YY.s(9)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    ServiceTile(30f) { Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(16))) }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                        Text(t("添加声音来源"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t2)
                        Text(t("火山豆包、Fish Audio、小米 MiMo（限时免费）、硅基流动、VoiceStudio…"), style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                }
            }
        }
        YYNote(t("推荐下载「语阅离线语音（Kokoro）」断网畅听，或直接使用免 Key 的「在线自然语音」。系统自带语音机械感较重（不推荐），仅作断网兜底。"))
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
    if (adding) {
        VoiceProviderSheet(
            close = { adding = false },
            pick = { picked ->
                if (picked.isFreeOnline) Prefs.set(picked.dismissedName, false)
                adding = false
                open(picked.key)
            },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun VoiceProviderSheet(close: () -> Unit, pick: (CloudVoice.Provider) -> Unit) {
    var query by remember { mutableStateOf("") }
    val q = query.lowercase().replace(" ", "")
    val matches = CloudVoice.Provider.entries.filter { p ->
        p != CloudVoice.Provider.Local && !p.isSetUp && (q.isEmpty() || "${p.key}${p.title}${p.aliases}${p.blurb}".lowercase().replace(" ", "").contains(q))
    }
    ModalBottomSheet(close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle(bottom = 2.dp) }) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
            YYSheetHeader(t("添加声音来源"), t("朗读"), close)
            Spacer(Modifier.height(YY.s(10)))
            Row(
                Modifier.fillMaxWidth().height(YY.s(34)).background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(horizontal = YY.s(10)),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
            ) {
                Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
                Box(Modifier.weight(1f)) { Field(query, t("搜索语音来源，比如 小米、硅基流动、VoiceStudio")) { query = it } }
            }
            for (group in CloudVoice.Group.entries) {
                val list = matches.filter { it.group == group }
                if (list.isEmpty()) continue
                Text(group.title, style = YY.font(10f, FontWeight.SemiBold), color = YY.c.t3, modifier = Modifier.padding(start = YY.s(8), top = YY.s(12), bottom = YY.s(2)))
                Column(verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                    for (pair in list.chunked(2)) {
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(YY.s(2))) {
                            for (p in pair) {
                                Row(
                                    Modifier.weight(1f).height(YY.s(36))
                                        .clip(RoundedCornerShape(YY.s(8)))
                                        .clickable { pick(p) }
                                        .padding(horizontal = YY.s(8)),
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                                ) {
                                    Box {
                                        SourceTile(p.key, 18f)
                                        if (p.companionSharedKey != null || p.isFreeOnline || p == CloudVoice.Provider.Xiaomi || p == CloudVoice.Provider.Azure || p == CloudVoice.Provider.Deepgram) {
                                            Box(Modifier.align(Alignment.TopEnd).offset(x = 1.5.dp, y = (-1.5).dp).size(5.dp).background(YY.c.ok, CircleShape))
                                        }
                                    }
                                    Text(
                                        p.title,
                                        style = YY.font(11.5f),
                                        color = YY.c.t1,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                }
                            }
                            if (pair.size == 1) Spacer(Modifier.weight(1f))
                        }
                    }
                }
            }
            Spacer(Modifier.height(YY.s(12)))
            YYGroup {
                row {
                    Row(
                        Modifier.fillMaxWidth().clickable { pick(CloudVoice.Provider.Local) }.padding(vertical = YY.s(8)),
                        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                    ) {
                        SourceTile(CloudVoice.Provider.Local.key, 28f)
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                            Text(t("自定义语音接口"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                            Text(t("GPT-SoVITS、Kokoro、unspeech 或局域网 /v1/audio/speech"), style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(10)))
                    }
                }
            }
        }
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
    val kokoroState = app.yuyue.speech.KokoroModel.state
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
    val chineseUi = L10n.chinese
    LaunchedEffect(chineseUi) { focus.syncDefaultBook() }
    val book = focus.book
    val chosenID by rememberPref(book.key, "")
    var voices by remember { mutableStateOf<List<BookVoice>?>(null) }
    var current by remember { mutableStateOf<BookVoice?>(null) }
    var draft by remember { mutableStateOf("") }
    var linesOpen by remember { mutableStateOf(false) }
    var pickerOpen by remember { mutableStateOf(false) }
    LaunchedEffect(book, chosenID, version, kokoroState, liveState) {
        SystemVoice.Engine.init(context)
        val all = Voices.all(book.tag)
        voices = all.filter { sourceOf(it) == id }
        current = (if (chosenID.isNotEmpty()) Voices.find(chosenID)?.takeIf { it.speaks(book.tag) } else null) ?: Voices.best(book.tag)
        SettingsLog.voiceNames += all.associate { it.id to it.name }
    }
    LaunchedEffect(book) {
        DailyQuote.ensureToday(book.tag) {
            !focus.hasCustomLine(book) && focus.playing == null
        }
    }
    androidx.compose.runtime.DisposableEffect(Unit) { onDispose { focus.stop() } }
    fun send(text: String) {
        if (text.isBlank()) return
        focus.stop()
        focus.lines[book] = text.trim()
        focus.heard.clear()
        draft = ""
        linesOpen = false
        current?.let { focus.listen(it, scope) }
    }
    fun pickDailyQuote(refreshIfActive: Boolean) {
        val wasDaily = !focus.hasCustomLine(book)
        focus.stop()
        focus.lines.remove(book)
        focus.heard.clear()
        linesOpen = false
        scope.launch {
            if (wasDaily && refreshIfActive) {
                DailyQuote.refresh(book.tag)
            }
            current?.let { focus.listen(it, scope) }
        }
    }
    Box(Modifier.fillMaxSize().background(YY.c.bg)) {
        Column(Modifier.fillMaxSize().padding(horizontal = YY.s(16))) {
            Row(Modifier.padding(top = YY.s(4), bottom = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
                BookSegments(book) { focus.selectBook(it); focus.listening = null; focus.stop() }
                Spacer(Modifier.weight(1f))
                if (id == NEURAL) {
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
            if (id == NEURAL) {
                if (app.yuyue.speech.KokoroModel.deviceUnsupported) {
                    Text(t("这台设备内存不足，可使用「在线自然语音（免 Key）」"), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.padding(bottom = YY.s(6)))
                } else if (!app.yuyue.speech.KokoroModel.installed(lib.dataDir)) {
                    Box(Modifier.padding(bottom = YY.s(8))) {
                        app.yuyue.reader.PrepareBetterVoiceRow(lib.core, lib.dataDir, book.tag)
                    }
                }
            } else if (id == SYSTEM) {
                Row(
                    Modifier.fillMaxWidth().padding(bottom = YY.s(6)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
                ) {
                    SourceBadgePill(t("不推荐"), 1)
                    Text(t("系统自带合成音机械感较重，建议改用「语阅离线语音」或「在线自然语音」"), style = YY.font(10f), color = YY.c.t3, maxLines = 2)
                }
            }
            val others = voices.orEmpty().filter { it.id != current?.id }
            current?.let { PinnedVoice(it, book, elsewhere = sourceOf(it) != id, scope) }
            val customLine = focus.lines[book]
            if (customLine != null) {
                YourLine(customLine, Modifier.padding(top = YY.s(10), bottom = YY.s(4)))
            } else {
                DailyQuoteCard(
                    quote = DailyQuote.current(book.tag),
                    refreshing = DailyQuote.refreshing,
                    onRefresh = {
                        scope.launch {
                            focus.stop()
                            focus.heard.clear()
                            DailyQuote.refresh(book.tag)
                        }
                    },
                    modifier = Modifier.padding(top = YY.s(10), bottom = YY.s(4)),
                )
            }
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
                if (id == SYSTEM) item {
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
            QuickLines(book, Modifier, useDaily = { pickDailyQuote(refreshIfActive = true) }, send = ::send, fill = { draft = it; linesOpen = false })
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

/** Default trial line before you type your own: a quiet literary card (no fake "我" avatar), tappable to swap. */
@Composable
private fun DailyQuoteCard(
    quote: DailyQuote.Entry,
    refreshing: Boolean,
    onRefresh: () -> Unit,
    modifier: Modifier = Modifier,
) {
    var spinCount by remember { mutableIntStateOf(0) }
    val spinDeg by androidx.compose.animation.core.animateFloatAsState(
        targetValue = spinCount * 180f,
        animationSpec = app.yuyue.design.YYMotion.standard(),
        label = "quoteSpin",
    )
    Column(
        modifier
            .fillMaxWidth()
            .shadow(0.5.dp, RoundedCornerShape(YY.s(12)))
            .clip(RoundedCornerShape(YY.s(12)))
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12)))
            .pressable(label = t("换一句"), enabled = !refreshing, scale = 0.985f) {
                spinCount++
                onRefresh()
            }
            .padding(horizontal = YY.s(12), vertical = YY.s(9)),
        verticalArrangement = Arrangement.spacedBy(YY.s(5)),
    ) {
        Row(
            Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(YY.s(5)),
        ) {
            Icon(Icons.quote, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
            Text(t("每日一言"), style = YY.font(10f, FontWeight.SemiBold), color = YY.c.t2)
            if (quote.source.isNotEmpty()) {
                Text(
                    "· ${quote.source}",
                    style = YY.font(10f),
                    color = YY.c.t3,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f),
                )
            } else {
                Spacer(Modifier.weight(1f))
            }
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(YY.s(3)),
            ) {
                Icon(
                    Icons.clockwise,
                    null,
                    tint = YY.c.t3,
                    modifier = Modifier
                        .size(YY.s(10.5f))
                        .graphicsLayer { rotationZ = spinDeg },
                )
                Text(t("换一句"), style = YY.font(10f, FontWeight.Medium), color = YY.c.t3)
            }
        }
        val reduceMotion = app.yuyue.design.YYMotion.isReduceMotion()
        androidx.compose.animation.AnimatedContent(
            targetState = quote.text,
            transitionSpec = { app.yuyue.design.YYMotion.swap(reduceMotion) },
            label = "dailyQuoteText",
        ) { line ->
            Text(
                line,
                fontFamily = FontFamily.Serif,
                fontSize = YY.sp(12.5f),
                lineHeight = YY.sp(19f),
                color = YY.c.t1,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.fillMaxWidth(),
            )
        }
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
                if (v is SystemVoice) {
                    val sysTag = listOfNotNull(v.qualityName ?: t("不推荐"), if (v.isOffline) null else t("联网")).joinToString(" · ")
                    Text(sysTag, style = YY.font(10f), color = if (v.qualityName == null) Color(0xFFD97706) else YY.c.t3, maxLines = 1)
                } else if (v is app.yuyue.speech.KokoroVoice) {
                    Text(v.summaryLine, style = YY.font(10f), color = YY.c.t3, maxLines = 1)
                }
            }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                VoiceBubble(v, book, scope, lifted = open)
                if (v.id !in focus.heard) Box(Modifier.size(6.dp).background(Color(0xFFE5483B), CircleShape))
                if (open) ChooseButton(false) { focus.pick(v) }
            }
            if (focus.failedId == v.id) {
                Text(
                    focus.failedMessage ?: t("试听暂不可用，请检查网络或稍后再试"),
                    style = YY.font(10f),
                    color = YY.c.danger,
                    maxLines = 2,
                )
            }
        }
    }
}

/** The one reading this kind of book, pinned on top: a card of its own colour. */
@Composable
private fun PinnedVoice(v: BookVoice, book: VoiceBook, elsewhere: Boolean, scope: kotlinx.coroutines.CoroutineScope) {
    val focus = ListeningFocus
    val (ground, ink) = colorsOf(v.id)
    val pinTint = if (YY.dark) YY.c.t2 else ink
    Column(
        Modifier.fillMaxWidth().heightIn(min = YY.s(44)).shadow(6.dp, RoundedCornerShape(YY.s(14)), ambientColor = Color(0x22332E29), spotColor = Color(0x33332E29))
            .clip(RoundedCornerShape(YY.s(14))).background(YY.c.card).background(if (YY.dark) ground.copy(alpha = 0.12f) else ground.copy(alpha = 0.34f))
            .border(0.5.dp, if (YY.dark) YY.c.line else ink.copy(alpha = 0.16f), RoundedCornerShape(YY.s(14)))
            .padding(horizontal = YY.s(10), vertical = YY.s(7)),
        verticalArrangement = Arrangement.spacedBy(YY.s(5)),
    ) {
        Row(
            Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
        ) {
            VoiceAvatar(v, 28f, pinned = true)
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                // A system voice's name leads with its language; pinned on a narrow row, only 声音 A.
                Text(
                    if (v is SystemVoice) v.name.substringAfterLast(" · ") else v.name,
                    style = YY.font(11.5f, FontWeight.SemiBold),
                    color = YY.c.t1,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                if (elsewhere) {
                    Row(
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(YY.s(3)),
                    ) {
                        Icon(Icons.pin, t("念你的%@", book.title), tint = pinTint, modifier = Modifier.size(YY.s(9.5f)))
                        Text(
                            voiceSourceTitle(sourceOf(v)),
                            style = YY.font(9.5f, FontWeight.Medium),
                            color = pinTint,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
            }
            VoiceBubble(v, book, scope, dark = true, height = 30f, widest = 108f)
            if (!elsewhere) {
                Icon(Icons.pin, t("念你的%@", book.title), tint = pinTint, modifier = Modifier.size(YY.s(14)))
            }
        }
        if (focus.failedId == v.id) {
            Text(
                focus.failedMessage ?: t("试听暂不可用，请检查网络或稍后再试"),
                style = YY.font(10f),
                color = YY.c.danger,
                maxLines = 2,
            )
        }
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
private fun QuickLines(book: VoiceBook, modifier: Modifier, useDaily: () -> Unit, send: (String) -> Unit, fill: (String) -> Unit) {
    val dailyLabel = if (book == VoiceBook.ZH) t("每日一言") else "Daily quote"
    val usingDaily = !ListeningFocus.hasCustomLine(book)
    val daily = DailyQuote.current(book.tag)
    Column(
        modifier.fillMaxWidth().shadow(16.dp, RoundedCornerShape(YY.s(12)), ambientColor = Color(0x29282420), spotColor = Color(0x29282420))
            .clip(RoundedCornerShape(YY.s(12))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12))).padding(YY.s(5)),
    ) {
        QuickLine(if (usingDaily) t("%@ · 正在用", dailyLabel) else dailyLabel, daily.text, onClick = useDaily)
        for ((name, line) in book.quickLines) {
            val using = ListeningFocus.hasCustomLine(book) && ListeningFocus.line(book) == line
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

@Composable
private fun CloudKeyPanel(p: CloudVoice.Provider, connected: () -> Unit) {
    val context = LocalContext.current
    PanelHeader(p.title, p.blurb) { SourceTile(p.key, 34f) }
    KeyCard(p, connected = false, changed = connected)
    Spacer(Modifier.height(YY.s(10)))
    YYGroup { row { LinkRow(if (p.isLocal) t("了解项目说明") else t("获取 API Key")) { openLink(context, p.keyPage) } } }
    YYNote(p.help + (if (p.isLocal) "" else t("API Key 仅在本机加密保存，调用费用由服务商直接结算。")))
}

@Composable
fun VoiceSourceSettings(id: String, back: () -> Unit) {
    val context = LocalContext.current
    val lib = app.yuyue.YuyueApp.instance.library
    val p = cloud(id)
    var count by remember { mutableIntStateOf(0) }
    LaunchedEffect(id) {
        if (id == SYSTEM) {
            SystemVoice.Engine.init(context)
            count = SystemVoice.Engine.voices().size
        } else if (id == NEURAL) {
            count = app.yuyue.speech.KokoroModel.voices(lib.core, lib.dataDir).size
        }
    }
    YYPage {
        if (id == NEURAL) {
            val liveState = app.yuyue.speech.LiveVoiceModel.state
            val liveReady = app.yuyue.speech.LiveVoiceModel.canRun(lib.dataDir)
            PanelHeader(voiceSourceTitle(NEURAL), t("跨端轻量离线模型 · 推荐"), YY.c.ok) { SourceTile(NEURAL, 34f) }
            YYGroup {
                row { YYRow(t("旁白模型"), value = if (app.yuyue.speech.KokoroModel.installed(lib.dataDir)) t("已就绪 · 16 音色") else t("待下载 · 125 MB")) }
                if (!app.yuyue.speech.LiveVoiceModel.deviceUnsupported) {
                    val liveVal = when {
                        liveReady -> t("已就绪 · 支持声音克隆与角色")
                        liveState is app.yuyue.speech.LiveVoiceModel.State.Downloading -> t("下载中 %lld%%", (liveState.progress * 100).roundToInt().toLong())
                        else -> t("待下载 · %lld MB", app.yuyue.speech.LiveVoiceModel.TOTAL_MB)
                    }
                    row { YYRow(t("声音克隆模型"), value = liveVal) }
                }
                row { YYRow(t("费用"), value = t("免费 · 不联网")) }
            }
            if (!app.yuyue.speech.KokoroModel.installed(lib.dataDir)) {
                Spacer(Modifier.height(YY.s(10)))
                app.yuyue.reader.PrepareBetterVoiceRow(lib.core, lib.dataDir, "zh")
            }
            if (!liveReady && !app.yuyue.speech.LiveVoiceModel.deviceUnsupported) {
                Spacer(Modifier.height(YY.s(10)))
                app.yuyue.reader.PrepareLiveVoiceRow(lib.core, lib.dataDir)
            }
            YYNote(t("Kokoro-82M v1.1 Int8 用于离线自然旁白；声音克隆模型（ZipVoice）用于本机克隆声音与多语气角色演绎，均无需联网。"))
        } else if (p == null) {
            PanelHeader(voiceSourceTitle(SYSTEM), t("系统自带 · 不推荐"), Color(0xFFD97706)) { SourceTile(SYSTEM, 34f) }
            YYGroup {
                row { YYRow(t("评级"), value = t("不推荐 · 仅断网兜底")) }
                row { YYRow(t("声音"), value = t("%lld 个声音", count)) }
                row { YYRow(t("费用"), value = t("免费 · 不联网")) }
            }
            Spacer(Modifier.height(YY.s(10)))
            YYGroup {
                row {
                    LinkRow(t("下载系统文字转语音数据")) {
                        SystemVoice.Engine.openSettings(context)
                    }
                }
            }
            YYNote(t("系统自带合成音机械感明显，仅建议在未下载离线语音且完全断网时兜底使用。"))
        } else {
            var removing by remember { mutableStateOf(false) }
            val inUse = listOf("zh", "en").any { Prefs.string("player.voice.$it").startsWith("cloud.${p.key}.") }
            PanelHeader(p.title, if (inUse) t("当前使用") else p.blurb, if (inUse) YY.c.ok else YY.c.t3) { SourceTile(p.key, 34f) }
            KeyCard(p, connected = true) {}
            if (!p.isLocal && !p.isFreeOnline) YYNote(t("粘贴后自动验证，有效才会替换。"))
            DestructiveCard(if (p.isFreeOnline) t("隐藏 %@", p.title) else t("断开 %@", p.title)) { removing = true }
            if (removing) AlertDialog(
                onDismissRequest = { removing = false },
                containerColor = YY.c.card,
                title = { Text(if (p.isFreeOnline) t("隐藏 %@？", p.title) else t("断开 %@？", p.title), color = YY.c.t1) },
                text = { Text(if (p.isFreeOnline) t("隐藏后可在「添加声音来源」重新开启。") else t("断开后将移除配置；使用该服务音色的书籍将切回本地音色。"), color = YY.c.t2) },
                confirmButton = {
                    TextButton({
                        Keys.delete(p.keyName)
                        Prefs.set(p.voicesName, null)
                        Prefs.set(p.urlName, null)
                        Prefs.set(p.modelName, null)
                        Prefs.set(p.enabledName, false)
                        if (p.isFreeOnline) Prefs.set(p.dismissedName, true)
                        if (p == CloudVoice.Provider.Volcengine) Prefs.set(CloudVoice.Provider.VOLC_APP_ID, null)
                        if (p == CloudVoice.Provider.Local) Prefs.set("tts.local.title", null)
                        for (lang in listOf("zh", "zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "en", "en-US", "en-GB")) {
                            if (Prefs.string("player.voice.$lang").startsWith("cloud.${p.key}.")) Prefs.set("player.voice.$lang", "")
                        }
                        removing = false
                        back()
                        back()
                    }) { Text(if (p.isFreeOnline) t("隐藏") else t("断开"), color = YY.c.danger) }
                },
                dismissButton = { TextButton({ removing = false }) { Text(t("取消"), color = YY.c.t2) } },
            )
        }
    }
}

@Composable
private fun KeyCard(p: CloudVoice.Provider, connected: Boolean, changed: () -> Unit) {
    var key by remember { mutableStateOf("") }
    var appId by remember { mutableStateOf(Prefs.string(CloudVoice.Provider.VOLC_APP_ID)) }
    var url by remember { mutableStateOf(p.savedURL) }
    var model by remember { mutableStateOf(Prefs.string(p.modelName).ifEmpty { p.defaultModel }) }
    var customTitle by remember { mutableStateOf(Prefs.string("tts.local.title")) }
    var region by remember { mutableStateOf(Prefs.string(CloudVoice.Provider.REGION)) }
    var editing by remember { mutableStateOf(!connected) }
    var checking by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<Pair<Boolean, String>?>(null) }
    val scope = rememberCoroutineScope()

    if (p.isFreeOnline) {
        YYGroup {
            row { YYRow(t("模式"), value = t("免 Key · 零配置即用")) }
            row {
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !checking) {
                        checking = true
                        outcome = null
                        Prefs.set(p.dismissedName, false)
                        scope.launch {
                            outcome = try {
                                val n = withContext(Dispatchers.IO) { CloudVoice.refresh(p) }
                                true to t("正常 · %lld 个音色", n)
                            } catch (e: Exception) {
                                false to (e.message ?: t("当前繁忙"))
                            }
                            checking = false
                            if (outcome?.first == true) { delay(500); changed() }
                        }
                    },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.waveformEcg, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
                    Text(if (connected) t("检查在线线路") else t("开启 %@", p.title), style = YY.font(11.5f), color = YY.c.ok, modifier = Modifier.weight(1f))
                    when {
                        checking -> CircularProgressIndicator(Modifier.size(12.dp), color = YY.c.t3, strokeWidth = 1.5.dp)
                        outcome != null -> Text(outcome!!.second, style = YY.font(10.5f), color = if (outcome!!.first) YY.c.ok else YY.c.danger, maxLines = 2, textAlign = TextAlign.End)
                    }
                }
            }
        }
        return
    }

    fun checkLocal() {
        checking = true
        outcome = null
        scope.launch {
            val oldUrl = Prefs.string(p.urlName)
            val oldModel = Prefs.string(p.modelName)
            val oldEnabled = Prefs.bool(p.enabledName, false)
            val cleanUrl = CloudVoice.normalizedBaseV1(url)
            Prefs.set(p.urlName, cleanUrl)
            Prefs.set(p.modelName, model.trim().ifEmpty { p.defaultModel })
            Prefs.set(p.enabledName, true)
            if (p == CloudVoice.Provider.Local && customTitle.isNotBlank()) Prefs.set("tts.local.title", customTitle.trim())
            if (key.isNotBlank()) Keys.write(p.keyName, key.trim())
            outcome = try {
                val n = withContext(Dispatchers.IO) { CloudVoice.refresh(p) }
                if (connected) editing = false
                true to t("正常 · %lld 个音色", n)
            } catch (e: Exception) {
                Prefs.set(p.urlName, oldUrl)
                Prefs.set(p.modelName, oldModel)
                Prefs.set(p.enabledName, oldEnabled)
                false to (e.message ?: t("没连上"))
            }
            checking = false
            if (outcome?.first == true) { delay(600); changed() }
        }
    }

    fun check(newKey: String?) {
        checking = true
        outcome = null
        scope.launch {
            val old = Keys.read(p.keyName)
            val oldRegion = Prefs.string(CloudVoice.Provider.REGION)
            val oldAppId = Prefs.string(CloudVoice.Provider.VOLC_APP_ID)
            if (newKey != null) Keys.write(p.keyName, newKey)
            if (p == CloudVoice.Provider.Azure) Prefs.set(CloudVoice.Provider.REGION, region.trim().lowercase())
            if (p == CloudVoice.Provider.Volcengine) Prefs.set(CloudVoice.Provider.VOLC_APP_ID, appId.trim())
            outcome = try {
                val n = withContext(Dispatchers.IO) { CloudVoice.refresh(p) }
                if (newKey != null && connected) { editing = false; key = "" }
                true to t("正常 · %lld 个音色", n)
            } catch (e: Exception) {
                if (newKey != null) { if (old != null) Keys.write(p.keyName, old) else Keys.delete(p.keyName) }
                Prefs.set(CloudVoice.Provider.REGION, oldRegion)
                if (p == CloudVoice.Provider.Volcengine) Prefs.set(CloudVoice.Provider.VOLC_APP_ID, oldAppId)
                false to (e.message ?: t("没连上"))
            }
            checking = false
            if (outcome?.first == true) { delay(600); changed() }
        }
    }

    LaunchedEffect(key, region) {
        if (p.isLocal) return@LaunchedEffect
        val k = key.trim()
        if (!editing || k.length < 20 || (p == CloudVoice.Provider.Azure && region.isBlank())) return@LaunchedEffect
        delay(600)
        check(k)
    }

    val shared = if (!connected && !p.isLocal) p.companionSharedKey else null
    if (shared != null) {
        YYGroup {
            row {
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(40)).clickable(enabled = !checking) { check(shared) },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Text(t("直接用「书伴」已填的 Key"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.ok, modifier = Modifier.weight(1f))
                    Text("…" + shared.takeLast(4), style = YY.font(10.5f), color = YY.c.t3)
                }
            }
        }
        Spacer(Modifier.height(YY.s(10)))
    }

    YYGroup {
        if (p.isLocal) {
            if (p == CloudVoice.Provider.Local && editing) {
                row { YYField(t("名称"), 44f) { Field(customTitle, t("比如 家里电脑")) { customTitle = it } } }
            }
            row {
                YYField(t("地址"), 44f) {
                    if (editing) Field(url, p.urlHint) { url = it }
                    else Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(p.savedURL, style = YY.font(11f), color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                        Text(t("更换"), style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.clickable { editing = true }.padding(start = YY.s(8), top = YY.s(6), bottom = YY.s(6)))
                    }
                }
            }
            row {
                YYField(t("模型"), 44f) {
                    if (editing) Field(model, p.defaultModel) { model = it }
                    else Text(p.savedModel, style = YY.font(11f), color = YY.c.t2)
                }
            }
            if (editing) {
                row { YYField("Key", 44f) { Secret(key, p.keyHint) { key = it } } }
            }
            row {
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !checking && url.isNotBlank()) { checkLocal() },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.waveformEcg, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
                    Text(if (connected && !editing) t("刷新音色与状态") else t("连接并获取音色"), style = YY.font(11.5f), color = YY.c.ok, modifier = Modifier.weight(1f))
                    when {
                        checking -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                        outcome != null -> Text(outcome!!.second, style = YY.font(10.5f), color = if (outcome!!.first) YY.c.ok else YY.c.danger, maxLines = 2, textAlign = TextAlign.End)
                    }
                }
            }
        } else {
            if (p == CloudVoice.Provider.Azure) row { YYField(t("区域"), 44f) { Field(region, "eastasia") { region = it } } }
            if (p == CloudVoice.Provider.Volcengine) row { YYField("AppID", 44f) { Field(appId, t("应用 AppID")) { appId = it } } }
            row {
                YYField(if (p == CloudVoice.Provider.Volcengine) "Token" else "Key", 44f) {
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
                    Icon(Icons.waveformEcg, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
                    Text(t("检查连接"), style = YY.font(11.5f), color = YY.c.ok, modifier = Modifier.weight(1f))
                    when {
                        checking -> CircularProgressIndicator(Modifier.size(12.dp), color = YY.c.t3, strokeWidth = 1.5.dp)
                        outcome != null -> Text(outcome!!.second, style = YY.font(10.5f), color = if (outcome!!.first) YY.c.ok else YY.c.danger, maxLines = 2, textAlign = TextAlign.End)
                    }
                }
            }
        }
    }
}
