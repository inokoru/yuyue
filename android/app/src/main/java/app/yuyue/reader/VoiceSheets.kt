package app.yuyue.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.pressable
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.ClonedVoices
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.KokoroModel
import app.yuyue.speech.KokoroVoice
import app.yuyue.speech.LiveVoiceModel
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.CharacterAge
import uniffi.yuyue_core.CharacterGender
import uniffi.yuyue_core.Role
import uniffi.yuyue_core.VoiceCard
import uniffi.yuyue_core.YuyueCore
import java.io.File
import kotlin.math.roundToInt

/**
 * Three-state inline preparation bar inside the read-aloud sheet:
 * - Wi‑Fi auto-download: `正在准备更好的声音 43%` + progress bar
 * - Cellular metered: `更好的声音 · 160 MB` + `下载`
 * - Download failed: `没下下来` + `再试` (no modal popup)
 */
@Composable
fun PrepareBetterVoiceRow(core: YuyueCore, dataDir: File, language: String) {
    if (!CardVoice.speaksLanguage(language) || KokoroModel.deviceUnsupported) return
    val st = KokoroModel.state
    if (st is KokoroModel.State.Ready || KokoroModel.installed(dataDir)) return
    if (st is KokoroModel.State.Unsupported) return

    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(10.dp))
            .background(YY.c.bg)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp))
            .padding(horizontal = 11.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        when (st) {
            is KokoroModel.State.Downloading -> {
                val pct = (st.progress * 100).roundToInt().coerceIn(0, 99)
                Text(
                    t("正在准备离线语音 %lld%%", pct.toLong()),
                    fontSize = 11.5.sp,
                    color = YY.c.t2,
                    style = YY.tnum,
                    modifier = Modifier.weight(1f),
                )
                Box(
                    Modifier
                        .width(88.dp)
                        .height(3.dp)
                        .clip(CircleShape)
                        .background(YY.c.inset),
                ) {
                    Box(
                        Modifier
                            .fillMaxWidth(st.progress.toFloat().coerceIn(0.03f, 1f))
                            .height(3.dp)
                            .background(YY.tint.accent),
                    )
                }
            }
            KokoroModel.State.Failed -> {
                Text(t("离线语音没下下来"), fontSize = 11.5.sp, color = YY.c.t2, modifier = Modifier.weight(1f))
                Text(
                    t("再试"),
                    fontSize = 11.5.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = YY.c.danger,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { KokoroModel.startDownload(core, dataDir) }
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
            else -> {
                Text(t("语阅离线语音（Kokoro） · 125 MB"), fontSize = 11.5.sp, color = YY.c.t2, modifier = Modifier.weight(1f))
                Text(
                    t("下载"),
                    fontSize = 11.5.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = YY.tint.accent,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { KokoroModel.startDownload(core, dataDir) }
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
        }
    }
}

private sealed interface PickerSubRoute {
    data object List : PickerSubRoute
    data class Clone(val replacingId: String? = null, val initialName: String? = null) : PickerSubRoute
    data class Detail(val cardId: String) : PickerSubRoute
}

/**
 * Full Material bottom-sheet wrapper around [VoiceGroupsContent] + [VoiceClonePanel] + [ClonedCardDetailPanel].
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun VoicePickerSheet(
    core: YuyueCore,
    dataDir: File,
    bookID: String?,
    bookTitle: String?,
    language: String,
    selectedId: String?,
    nextBookSentence: String?,
    onSelect: (BookVoice) -> Unit,
    dismiss: () -> Unit,
) {
    var route by remember { mutableStateOf<PickerSubRoute>(PickerSubRoute.List) }
    var refreshTick by remember { mutableIntStateOf(0) }
    val scope = rememberCoroutineScope()

    DisposableEffect(Unit) { onDispose { SystemVoice.Engine.stopPreview() } }

    ModalBottomSheet(
        onDismissRequest = dismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
    ) {
        Column(
            Modifier
                .padding(horizontal = 16.dp)
                .padding(bottom = 24.dp)
                .verticalScroll(rememberScrollState()),
        ) {
            when (val r = route) {
                PickerSubRoute.List -> {
                    Row(
                        Modifier.fillMaxWidth().padding(bottom = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(t("声音"), fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, modifier = Modifier.weight(1f))
                        Box(
                            Modifier
                                .offset(x = 9.dp)
                                .size(44.dp)
                                .clip(CircleShape)
                                .pressable(label = t("关闭"), scale = 0.92f) { dismiss() },
                            contentAlignment = Alignment.Center,
                        ) {
                            Box(
                                Modifier
                                    .size(26.dp)
                                    .clip(CircleShape)
                                    .background(YY.c.inset),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.close, t("关闭"), tint = YY.c.t2, modifier = Modifier.size(14.dp))
                            }
                        }
                    }
                    VoiceGroupsContent(
                        core = core,
                        dataDir = dataDir,
                        bookID = bookID,
                        bookTitle = bookTitle,
                        language = language,
                        roleName = null,
                        narratorVoice = null,
                        roles = emptyList(),
                        selectedId = selectedId,
                        sampleText = nextBookSentence ?: defaultSampleLine(language),
                        refreshKey = refreshTick,
                        onPickFollowNarrator = null,
                        onPickVoice = { v ->
                            onSelect(v)
                            dismiss()
                        },
                        onOpenClone = { replacingId, initName ->
                            route = PickerSubRoute.Clone(replacingId, initName)
                        },
                        onOpenDetail = { cardId ->
                            route = PickerSubRoute.Detail(cardId)
                        },
                    )
                }
                is PickerSubRoute.Clone -> {
                    VoiceClonePanel(
                        core = core,
                        dataDir = dataDir,
                        language = language,
                        previewSentence = nextBookSentence ?: defaultSampleLine(language),
                        bookTitle = bookTitle,
                        replacingId = r.replacingId,
                        initialName = r.initialName,
                        onCancel = { route = PickerSubRoute.List },
                        onSaved = { card ->
                            refreshTick++
                            val voice: BookVoice = if (LiveVoiceModel.canRun(dataDir)) {
                                CardVoice(card)
                            } else {
                                ClonedVoices.find(card.id) ?: CardVoice(card)
                            }
                            scope.launch {
                                onSelect(voice)
                                dismiss()
                            }
                        },
                    )
                }
                is PickerSubRoute.Detail -> {
                    ClonedCardDetailPanel(
                        core = core,
                        dataDir = dataDir,
                        cardId = r.cardId,
                        language = language,
                        sampleLine = nextBookSentence ?: defaultSampleLine(language),
                        onBack = {
                            refreshTick++
                            route = PickerSubRoute.List
                        },
                        onDeleted = { deletedId ->
                            for (lang in listOf("zh", "en", language)) {
                                if (Prefs.string("player.voice.$lang") == deletedId) {
                                    Prefs.set("player.voice.$lang", "")
                                }
                            }
                            refreshTick++
                            route = PickerSubRoute.List
                        },
                    )
                }
            }
        }
    }
}

/**
 * Four-group voice list:
 * 1. 推荐 (built-in cards, `narrator == true` first; download row when uninstalled; `这台手机带不动` on < 6 GB RAM; hidden for unsupported languages like Japanese)
 * 2. 我的 (cloned cards + book-designed cards + `克隆一个声音`)
 * 3. 云端 (configured cloud voices)
 * 4. 系统自带 (system TTS voices)
 */
@Composable
fun VoiceGroupsContent(
    core: YuyueCore,
    dataDir: File,
    bookID: String?,
    bookTitle: String?,
    language: String,
    roleName: String?,
    narratorVoice: BookVoice?,
    roles: List<Role>,
    selectedId: String?,
    sampleText: String,
    refreshKey: Int = 0,
    followTitle: String? = null,
    followSubtitle: String? = null,
    onPickFollowNarrator: (() -> Unit)? = null,
    onPickVoice: (BookVoice) -> Unit,
    onOpenClone: (replacingId: String?, initialName: String?) -> Unit,
    onOpenDetail: (cardId: String) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val kokoroState = KokoroModel.state
    val liveState = LiveVoiceModel.state
    var kokoroVoices by remember { mutableStateOf<List<KokoroVoice>>(emptyList()) }
    var recommended by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var clonedCards by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var designedCards by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var cloudVoices by remember { mutableStateOf<List<CloudVoice>>(emptyList()) }
    var systemVoices by remember { mutableStateOf<List<SystemVoice>>(emptyList()) }
    var previewingId by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(language, bookID, refreshKey, kokoroState, liveState) {
        SystemVoice.Engine.init(context)
        ClonedVoices.init(context)
        withContext(Dispatchers.IO) {
            KokoroModel.register(core, dataDir)
            LiveVoiceModel.register(core, dataDir)
            val kok = KokoroModel.voices(core, dataDir, language)
            val rec = if (LiveVoiceModel.canRun(dataDir)) Voices.recommendedCards(core, dataDir, language) else emptyList()
            val cCards = core.cardsCloned()
            val des = if (bookID != null) {
                runCatching { core.cardsOfBook(bookID) }.getOrDefault(emptyList())
                    .filter { Voices.cardMatchesTag(it.lang, language) }
            } else emptyList()
            val cld = CloudVoice.all().filter { it.speaks(language) }
            val sys = SystemVoice.Engine.voices().filter { it.speaks(language) }.sortedByDescending { it.isOffline }
            kokoroVoices = kok
            recommended = rec
            clonedCards = cCards
            designedCards = des
            cloudVoices = cld
            systemVoices = sys
        }
    }

    fun playPreview(v: BookVoice) {
        if (previewingId == v.id) {
            SystemVoice.Engine.stopPreview()
            previewingId = null
            return
        }
        SystemVoice.Engine.stopPreview()
        if (v is SystemVoice) {
            v.preview(sampleText)
            return
        }
        previewingId = v.id
        scope.launch {
            try {
                val s = withContext(Dispatchers.IO) { v.render(sampleText, language) }
                if (s != null) SystemVoice.Engine.preview(s)
            } catch (_: Exception) {
            } finally {
                if (previewingId == v.id) previewingId = null
            }
        }
    }

    if (roleName != null && onPickFollowNarrator != null) {
        YYGroup(Modifier.padding(top = 4.dp)) {
            row {
                VoiceChoiceRow(
                    avatar = narratorVoice?.name?.take(1) ?: "旁",
                    title = followTitle ?: t("跟旁白"),
                    badge = null,
                    badgeAccent = false,
                    subtitle = followSubtitle ?: t("现在旁白是%@，旁白换了%@也跟着换", narratorVoice?.name ?: "", roleName),
                    selected = selectedId == null,
                    previewing = false,
                    onPreview = narratorVoice?.let { nv -> { playPreview(nv) } },
                    onMore = null,
                    onClick = onPickFollowNarrator,
                )
            }
        }
    }

    // 1. 推荐
    if (CardVoice.speaksLanguage(language)) {
        val defaultKokoroId = if (language.lowercase().startsWith("en")) "kokoro.af_heart" else "kokoro.zf_001"
        val primaryNarratorId = if (kokoroVoices.isNotEmpty()) defaultKokoroId else if (language.lowercase().startsWith("en")) "card.b.33" else "card.b.01"
        YYLabel(t("推荐"), if (kokoroVoices.isNotEmpty() || recommended.isNotEmpty()) t("跨端离线好声音") else null)
        YYGroup {
            when {
                KokoroModel.deviceUnsupported && LiveVoiceModel.deviceUnsupported -> {
                    row {
                        Row(
                            Modifier.fillMaxWidth().padding(vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(t("这台设备带不动离线语音，可使用云端「在线自然语音（免 Key）」"), fontSize = 12.sp, color = YY.c.t3)
                        }
                    }
                }
                kokoroVoices.isEmpty() && recommended.isEmpty() -> {
                    row {
                        Row(
                            Modifier.fillMaxWidth().padding(vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            when (kokoroState) {
                                is KokoroModel.State.Downloading -> {
                                    val pct = (kokoroState.progress * 100).roundToInt().coerceIn(0, 99)
                                    Column(Modifier.weight(1f)) {
                                        Text(t("正在准备离线语音 %lld%%", pct.toLong()), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, style = YY.tnum)
                                        Text(t("跨端自然朗读 · 断网可用"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.tint.accent)
                                }
                                KokoroModel.State.Failed -> {
                                    Column(Modifier.weight(1f)) {
                                        Text(t("离线语音没下下来"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                                        Text(t("连上网再试一次"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    YYButton(t("再试"), kind = ButtonKind.Main, size = ButtonSize.Small) {
                                        KokoroModel.startDownload(core, dataDir)
                                    }
                                }
                                else -> {
                                    Column(Modifier.weight(1f)) {
                                        Text(t("语阅离线语音（Kokoro） · 125 MB"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                                        Text(t("跨端自然朗读 · 断网可用"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    YYButton(t("下载"), kind = ButtonKind.Main, size = ButtonSize.Small) {
                                        KokoroModel.startDownload(core, dataDir)
                                    }
                                }
                            }
                        }
                    }
                }
                else -> {
                    for (kv in kokoroVoices) {
                        val used = roles.filter { it.voice.voice == kv.id && it.name != roleName }.map { it.name }
                        val isDef = kv.id == primaryNarratorId
                        val badge = when {
                            isDef -> t("默认旁白")
                            kv.speaker.narrator -> t("推荐旁白")
                            else -> null
                        }
                        val sub = if (used.isNotEmpty()) {
                            t("%@在用", used.joinToString(t("、"))) + " · " + kv.summaryLine
                        } else {
                            kv.summaryLine
                        }
                        row {
                            VoiceChoiceRow(
                                avatar = kv.name.take(1).ifEmpty { "语" },
                                title = kv.name,
                                badge = badge,
                                badgeAccent = isDef,
                                subtitle = sub,
                                selected = selectedId == kv.id,
                                previewing = previewingId == kv.id,
                                onPreview = { playPreview(kv) },
                                onMore = null,
                                onClick = { onPickVoice(kv) },
                            )
                        }
                    }
                    for (card in recommended) {
                        val v = CardVoice(card)
                        val used = roles.filter { it.voice.voice == card.id && it.name != roleName }.map { it.name }
                        val badge = when {
                            card.id == primaryNarratorId -> t("默认旁白")
                            card.narrator -> t("角色旁白")
                            else -> null
                        }
                        val sub = if (used.isNotEmpty()) {
                            t("%@在用", used.joinToString(t("、"))) + " · " + cardSummaryLine(card)
                        } else {
                            cardSummaryLine(card)
                        }
                        row {
                            VoiceChoiceRow(
                                avatar = card.name.take(1).ifEmpty { "声" },
                                title = card.name,
                                badge = badge,
                                badgeAccent = card.id == primaryNarratorId,
                                subtitle = sub,
                                selected = selectedId == card.id,
                                previewing = previewingId == card.id,
                                onPreview = { playPreview(v) },
                                onMore = null,
                                onClick = { onPickVoice(v) },
                            )
                        }
                    }
                }
            }
        }
    }

    // 2. 我的
    YYLabel(t("我的"), t("克隆与本书角色"))
    YYGroup {
        for (card in clonedCards) {
            val v: BookVoice = if (LiveVoiceModel.canRun(dataDir)) {
                CardVoice(card)
            } else {
                ClonedVoices.find(card.id) ?: CardVoice(card)
            }
            val toneCount = card.clips.size
            val sub = if (LiveVoiceModel.canRun(dataDir)) {
                t("本机 · 已录 %lld 档语气", toneCount.toLong())
            } else if ((v as? ClonedVoice)?.isReady == true) {
                "ElevenLabs"
            } else {
                t("已存本机 · 下载朗读声音后可念")
            }
            row {
                VoiceChoiceRow(
                    avatar = card.name.take(1).ifEmpty { "我" },
                    title = card.name,
                    badge = t("克隆"),
                    badgeAccent = false,
                    subtitle = sub,
                    selected = selectedId == card.id,
                    previewing = previewingId == card.id,
                    onPreview = if (LiveVoiceModel.canRun(dataDir) || (v as? ClonedVoice)?.isReady == true) ({ playPreview(v) }) else null,
                    onMore = { onOpenDetail(card.id) },
                    onClick = { onPickVoice(v) },
                )
            }
        }
        for (card in designedCards) {
            val v = CardVoice(card, bookID)
            val sub = if (!bookTitle.isNullOrEmpty()) {
                t("为《%@》生成", bookTitle) + " · " + cardSummaryLine(card)
            } else {
                cardSummaryLine(card)
            }
            row {
                VoiceChoiceRow(
                    avatar = card.name.take(1).ifEmpty { "角" },
                    title = card.name,
                    badge = t("本书角色"),
                    badgeAccent = false,
                    subtitle = sub,
                    selected = selectedId == card.id,
                    previewing = previewingId == card.id,
                    onPreview = if (LiveVoiceModel.canRun(dataDir)) ({ playPreview(v) }) else null,
                    onMore = null,
                    onClick = { onPickVoice(v) },
                )
            }
        }
        row {
            Row(
                Modifier
                    .fillMaxWidth()
                    .clickable { onOpenClone(null, null) }
                    .padding(vertical = 9.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Box(
                    Modifier.size(28.dp).background(YY.tint.accentSoft, CircleShape),
                    contentAlignment = Alignment.Center,
                ) {
                    Text("＋", fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent)
                }
                Column(Modifier.weight(1f)) {
                    Text(t("克隆一个声音"), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent)
                    Text(t("录 10 秒自己的声音 · 不联网、不花钱"), fontSize = 10.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(12.dp))
            }
        }
    }

    // 3. 云端
    if (cloudVoices.isNotEmpty()) {
        YYLabel(t("云端"))
        YYGroup {
            for (v in cloudVoices) {
                val used = roles.filter { it.voice.voice == v.id && it.name != roleName }.map { it.name }
                row {
                    VoiceChoiceRow(
                        avatar = v.name.take(1).ifEmpty { "☁" },
                        title = v.name,
                        badge = v.provider.title,
                        badgeAccent = false,
                        subtitle = if (used.isNotEmpty()) t("%@在用", used.joinToString(t("、"))) else t("云端"),
                        selected = selectedId == v.id,
                        previewing = previewingId == v.id,
                        onPreview = { playPreview(v) },
                        onMore = null,
                        onClick = { onPickVoice(v) },
                    )
                }
            }
        }
    }

    // 4. 系统自带（不推荐）
    YYLabel(t("系统自带 · 不推荐"), t("机械音仅断网兜底"))
    YYGroup {
        if (systemVoices.isEmpty()) {
            row {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable { SystemVoice.Engine.openSettings(context) }
                        .padding(vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(t("下载系统文字转语音数据"), fontSize = 12.sp, color = YY.tint.accent, modifier = Modifier.weight(1f))
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(12.dp))
                }
            }
        } else {
            for (v in systemVoices) {
                val used = roles.filter { it.voice.voice == v.id && it.name != roleName }.map { it.name }
                val sub = listOfNotNull(
                    if (used.isNotEmpty()) t("%@在用", used.joinToString(t("、"))) else null,
                    t("系统自带"),
                    v.qualityName,
                    if (!v.isOffline) t("联网") else null,
                ).joinToString(" · ")
                row {
                    VoiceChoiceRow(
                        avatar = v.name.substringAfterLast(' ').take(1).ifEmpty { "A" },
                        title = v.name,
                        badge = v.qualityName ?: t("不推荐"),
                        badgeAccent = false,
                        subtitle = sub,
                        selected = selectedId == v.id,
                        previewing = false,
                        onPreview = { playPreview(v) },
                        onMore = null,
                        onClick = { onPickVoice(v) },
                    )
                }
            }
        }
    }
}

@Composable
private fun VoiceChoiceRow(
    avatar: String,
    title: String,
    badge: String?,
    badgeAccent: Boolean,
    subtitle: String,
    selected: Boolean,
    previewing: Boolean,
    onPreview: (() -> Unit)?,
    onMore: (() -> Unit)?,
    onClick: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .padding(vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        Box(
            Modifier
                .size(28.dp)
                .clip(CircleShape)
                .background(if (selected) YY.tint.accentSoft else YY.c.inset)
                .then(if (onPreview != null) Modifier.clickable(onClick = onPreview) else Modifier),
            contentAlignment = Alignment.Center,
        ) {
            if (previewing) {
                CircularProgressIndicator(Modifier.size(12.dp), strokeWidth = 1.5.dp, color = YY.tint.accent)
            } else if (onPreview != null) {
                Icon(Icons.play, t("试听"), tint = if (selected) YY.tint.accent else YY.c.t2, modifier = Modifier.size(12.dp))
            } else {
                Text(avatar, fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = if (selected) YY.tint.accent else YY.c.t2)
            }
        }
        Column(
            Modifier
                .weight(1f)
                .clickable(onClick = onClick),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                Text(
                    title,
                    fontSize = 12.5.sp,
                    fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium,
                    color = YY.c.t1,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                if (badge != null) {
                    Text(
                        badge,
                        fontSize = 9.5.sp,
                        fontWeight = if (badgeAccent) FontWeight.SemiBold else FontWeight.Medium,
                        color = if (badgeAccent) YY.tint.accent else YY.c.t2,
                        modifier = Modifier
                            .background(if (badgeAccent) YY.tint.accentSoft else YY.c.inset, RoundedCornerShape(4.dp))
                            .padding(horizontal = 5.dp, vertical = 1.dp),
                    )
                }
            }
            Text(subtitle, fontSize = 10.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        if (selected) {
            Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(15.dp))
        }
        if (onMore != null) {
            Box(
                Modifier
                    .size(28.dp)
                    .clip(RoundedCornerShape(7.dp))
                    .clickable(onClick = onMore),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.more, t("卡的详情"), tint = YY.c.t3, modifier = Modifier.size(16.dp))
            }
        }
    }
}

private fun cardSummaryLine(card: VoiceCard): String {
    val g = when (card.gender) {
        CharacterGender.M -> t("男")
        CharacterGender.F -> t("女")
        CharacterGender.UNKNOWN -> null
    }
    val a = when (card.age) {
        CharacterAge.CHILD -> t("儿童")
        CharacterAge.TEEN -> t("少年")
        CharacterAge.YOUNG -> t("青年")
        CharacterAge.MIDDLE -> t("中年")
        CharacterAge.OLD -> t("老年")
        CharacterAge.UNKNOWN -> null
    }
    val tags = card.tags.takeIf { it.isNotEmpty() }?.joinToString(t("、"))
    return listOfNotNull(g, a, tags).joinToString(" · ").ifEmpty { card.lang }
}

private fun defaultSampleLine(language: String): String =
    if (language.lowercase().startsWith("en")) {
        "It was a quiet morning, and the sunlight fell softly across the page."
    } else {
        "“我买几个橘子去。你就在此地，不要走动。”"
    }

