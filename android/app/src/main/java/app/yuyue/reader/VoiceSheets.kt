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
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
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
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.ClonedVoices
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.Listener
import app.yuyue.speech.LiveVoiceModel
import app.yuyue.speech.SpeakerPrint
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.CardCheck
import uniffi.yuyue_core.CardClip
import uniffi.yuyue_core.CardOrigin
import uniffi.yuyue_core.CardStyle
import uniffi.yuyue_core.CharacterAge
import uniffi.yuyue_core.CharacterGender
import uniffi.yuyue_core.Role
import uniffi.yuyue_core.VoiceCard
import uniffi.yuyue_core.YuyueCore
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID
import kotlin.math.log10
import kotlin.math.roundToInt
import kotlin.math.sqrt

/**
 * §5.1 & §7.6-2: Three-state inline preparation bar inside the read-aloud sheet:
 * - Wi‑Fi auto-download: `正在准备更好的声音 43%` + progress bar
 * - Cellular metered: `更好的声音 · 160 MB` + `下载`
 * - Download failed: `没下下来` + `再试` (no modal popup)
 */
@Composable
fun PrepareBetterVoiceRow(core: YuyueCore, dataDir: File, language: String) {
    if (!CardVoice.speaksLanguage(language) || LiveVoiceModel.deviceUnsupported) return
    val st = LiveVoiceModel.state
    if (st is LiveVoiceModel.State.Ready || LiveVoiceModel.ready(dataDir)) return
    if (st is LiveVoiceModel.State.Unsupported) return

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
            is LiveVoiceModel.State.Downloading -> {
                val pct = (st.progress * 100).roundToInt().coerceIn(0, 99)
                Text(
                    t("正在准备更好的声音 %lld%%", pct.toLong()),
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
            LiveVoiceModel.State.Failed -> {
                Text(t("没下下来"), fontSize = 11.5.sp, color = YY.c.t2, modifier = Modifier.weight(1f))
                Text(
                    t("再试"),
                    fontSize = 11.5.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = YY.c.danger,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { LiveVoiceModel.startDownload(core, dataDir) }
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
            else -> {
                Text(t("更好的声音 · 160 MB"), fontSize = 11.5.sp, color = YY.c.t2, modifier = Modifier.weight(1f))
                Text(
                    t("下载"),
                    fontSize = 11.5.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = YY.tint.accent,
                    modifier = Modifier
                        .clip(RoundedCornerShape(6.dp))
                        .clickable { LiveVoiceModel.startDownload(core, dataDir) }
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
                        Modifier.fillMaxWidth().padding(bottom = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Text(t("声音"), fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, modifier = Modifier.weight(1f))
                        Box(
                            Modifier
                                .size(26.dp)
                                .clip(CircleShape)
                                .background(YY.c.inset)
                                .clickable { dismiss() },
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(Icons.close, t("关闭"), tint = YY.c.t2, modifier = Modifier.size(14.dp))
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
 * §5.1 & §7.6-1: Four-group voice list:
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
    onPickFollowNarrator: (() -> Unit)? = null,
    onPickVoice: (BookVoice) -> Unit,
    onOpenClone: (replacingId: String?, initialName: String?) -> Unit,
    onOpenDetail: (cardId: String) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val liveState = LiveVoiceModel.state
    var recommended by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var clonedCards by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var legacyClones by remember { mutableStateOf<List<ClonedVoice>>(emptyList()) }
    var designedCards by remember { mutableStateOf<List<VoiceCard>>(emptyList()) }
    var cloudVoices by remember { mutableStateOf<List<CloudVoice>>(emptyList()) }
    var systemVoices by remember { mutableStateOf<List<SystemVoice>>(emptyList()) }
    var previewingId by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(language, bookID, refreshKey, liveState) {
        SystemVoice.Engine.init(context)
        ClonedVoices.init(context)
        withContext(Dispatchers.IO) {
            LiveVoiceModel.register(core, dataDir)
            ClonedVoices.migrateLegacyIfNeeded(core, dataDir)
            val rec = if (LiveVoiceModel.canRun(dataDir)) Voices.recommendedCards(core, dataDir, language) else emptyList()
            val cCards = core.cardsCloned()
            val cardIds = cCards.map { it.id }.toSet()
            val leg = ClonedVoices.all().filter { it.id !in cardIds }
            val des = if (bookID != null) {
                runCatching { core.cardsOfBook(bookID) }.getOrDefault(emptyList())
                    .filter { Voices.cardMatchesTag(it.lang, language) }
            } else emptyList()
            val cld = CloudVoice.all().filter { it.speaks(language) }
            val sys = SystemVoice.Engine.voices().filter { it.speaks(language) }.sortedByDescending { it.isOffline }
            recommended = rec
            clonedCards = cCards
            legacyClones = leg
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
                    title = t("跟旁白"),
                    badge = null,
                    badgeAccent = false,
                    subtitle = t("现在旁白是%@，旁白换了%@也跟着换", narratorVoice?.name ?: "", roleName),
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
        val primaryNarratorId = if (language.lowercase().startsWith("en")) "card.b.33" else "card.b.01"
        YYLabel(t("推荐"), if (recommended.isNotEmpty()) t("自带声音 · 七档语气") else null)
        YYGroup {
            when {
                LiveVoiceModel.deviceUnsupported -> {
                    row {
                        Row(
                            Modifier.fillMaxWidth().padding(vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            Text(t("这台手机带不动"), fontSize = 12.sp, color = YY.c.t3)
                        }
                    }
                }
                recommended.isEmpty() -> {
                    row {
                        Row(
                            Modifier.fillMaxWidth().padding(vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(10.dp),
                        ) {
                            when (liveState) {
                                is LiveVoiceModel.State.Downloading -> {
                                    val pct = (liveState.progress * 100).roundToInt().coerceIn(0, 99)
                                    Column(Modifier.weight(1f)) {
                                        Text(t("正在准备更好的声音 %lld%%", pct.toLong()), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, style = YY.tnum)
                                        Text(t("不联网 · 分角色带语气"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.tint.accent)
                                }
                                LiveVoiceModel.State.Failed -> {
                                    Column(Modifier.weight(1f)) {
                                        Text(t("没下下来"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                                        Text(t("连上网再试一次"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    YYButton(t("再试"), kind = ButtonKind.Main, size = ButtonSize.Small) {
                                        LiveVoiceModel.startDownload(core, dataDir)
                                    }
                                }
                                else -> {
                                    Column(Modifier.weight(1f)) {
                                        Text(t("更好的声音 · 160 MB"), fontSize = 12.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1)
                                        Text(t("不联网 · 分角色带语气"), fontSize = 10.5.sp, color = YY.c.t3)
                                    }
                                    YYButton(t("下载"), kind = ButtonKind.Main, size = ButtonSize.Small) {
                                        LiveVoiceModel.startDownload(core, dataDir)
                                    }
                                }
                            }
                        }
                    }
                }
                else -> {
                    for (card in recommended) {
                        val v = CardVoice(card)
                        val used = roles.filter { it.voice.voice == card.id && it.name != roleName }.map { it.name }
                        val badge = when {
                            card.id == primaryNarratorId -> t("默认旁白")
                            card.narrator -> t("推荐旁白")
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
        for (legacy in legacyClones) {
            row {
                VoiceChoiceRow(
                    avatar = legacy.name.take(1).ifEmpty { "我" },
                    title = legacy.name,
                    badge = if (legacy.needsRerecord) t("要重录") else t("克隆"),
                    badgeAccent = false,
                    subtitle = when {
                        legacy.needsRerecord -> t("要重录 · 点此补录一句即可本机离线念")
                        legacy.isReady -> "ElevenLabs"
                        else -> t("克隆 · 还不能念，先用旁白的")
                    },
                    selected = selectedId == legacy.id,
                    previewing = previewingId == legacy.id,
                    onPreview = if (legacy.isReady) ({ playPreview(legacy) }) else null,
                    onMore = null,
                    onClick = {
                        if (legacy.needsRerecord) onOpenClone(legacy.id, legacy.name)
                        else onPickVoice(legacy)
                    },
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

    // 4. 系统自带
    YYLabel(t("系统自带"))
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
                    Text(t("下载更自然的声音"), fontSize = 12.sp, color = YY.tint.accent, modifier = Modifier.weight(1f))
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
                        badge = null,
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

// ── §6.1 & §6.2 & §7.6-3: 本机离线克隆面板 ──────────────────────────────────

private sealed interface CloneStep {
    data object Record : CloneStep
    data class ConfirmImport(val samples: FloatArray, val rate: Int, val draftText: String) : CloneStep
    data class Fault(val check: CardCheck) : CloneStep
    data class Ready(val samples: FloatArray, val rate: Int, val text: String, val stageFile: File, val seconds: Float) : CloneStep
}

@Composable
fun VoiceClonePanel(
    core: YuyueCore,
    dataDir: File,
    language: String,
    previewSentence: String,
    bookTitle: String?,
    replacingId: String? = null,
    initialName: String? = null,
    onCancel: () -> Unit,
    onSaved: (VoiceCard) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val cardLang = if (language.lowercase().startsWith("en")) "en" else "zh"
    val script = remember(cardLang) { core.cardScript(cardLang, CardStyle.NEUTRAL) }

    var consent by remember { mutableStateOf(false) }
    var step by remember { mutableStateOf<CloneStep>(CloneStep.Record) }
    var recording by remember { mutableStateOf(false) }
    var recSeconds by remember { mutableDoubleStateOf(0.0) }
    val meterBars = remember { mutableStateListOf(0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f) }
    var recorder by remember { mutableStateOf<VoiceSampleRecorder?>(null) }
    var name by remember { mutableStateOf(initialName ?: t("我的声音")) }
    var saving by remember { mutableStateOf(false) }
    var previewing by remember { mutableStateOf(false) }
    var errorMsg by remember { mutableStateOf<String?>(null) }

    DisposableEffect(Unit) {
        onDispose {
            recorder?.stop()
            SystemVoice.Engine.stopPreview()
        }
    }

    suspend fun evaluateSample(samples: FloatArray, rate: Int, expectedText: String, checkTranscript: Boolean) {
        val heard = if (checkTranscript && Listener.installed(dataDir)) {
            withContext(Dispatchers.Default) { Listener.text(dataDir, samples, rate, cardLang) }
        } else null
        val chk = withContext(Dispatchers.Default) {
            core.cardCheck(samples.asList(), rate.toUInt(), expectedText, heard)
        }
        if (!chk.ok) {
            step = CloneStep.Fault(chk)
            return
        }
        val stageDir = File(context.cacheDir, "clone-stage-${UUID.randomUUID()}").apply { mkdirs() }
        val wavFile = File(stageDir, "neutral.wav")
        val secs = withContext(Dispatchers.IO) {
            core.cardWriteWav(wavFile.path, samples.asList(), rate.toUInt())
        }
        step = CloneStep.Ready(samples, rate, expectedText, wavFile, secs)
        // Automatically synthesize & play current book's next sentence (§6.1 step 4).
        previewing = true
        try {
            val speech = if (LiveVoiceModel.canRun(dataDir)) {
                CardVoice.Engine.renderPreviewWithClip(core, dataDir, wavFile.path, expectedText, cardLang, previewSentence, language)
            } else {
                withContext(Dispatchers.IO) { SystemVoice.Engine.wav(wavFile) }
            }
            if (speech != null) SystemVoice.Engine.preview(speech)
        } catch (_: Exception) {
        } finally {
            previewing = false
        }
    }

    fun finishRecording() {
        val r = recorder ?: return
        recording = false
        recorder = null
        val captured = r.stop()
        if (captured == null || captured.first.isEmpty()) return
        scope.launch {
            evaluateSample(captured.first, captured.second, script, checkTranscript = true)
        }
    }

    LaunchedEffect(recording) {
        while (recording && isActive) {
            delay(100)
            val r = recorder ?: break
            recSeconds = r.seconds
            meterBars.removeAt(0)
            meterBars.add(r.level)
            if (r.autoStopped) {
                finishRecording()
                break
            }
        }
    }

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (!ok) errorMsg = t("语阅没有用麦克风的权限。在系统设置里给语阅打开麦克风。")
    }

    val importLauncher = rememberLauncherForActivityResult(ActivityResultContracts.GetContent()) { uri: Uri? ->
        if (uri == null) return@rememberLauncherForActivityResult
        errorMsg = null
        scope.launch {
            val tmp = File(context.cacheDir, "clone-import-${System.currentTimeMillis()}.audio")
            try {
                withContext(Dispatchers.IO) {
                    context.contentResolver.openInputStream(uri)?.use { inp -> tmp.outputStream().use { inp.copyTo(it) } }
                }
                val decoded = ClonedVoices.decodeAudioFile(core, tmp)
                if (decoded == null || decoded.first.isEmpty()) {
                    errorMsg = t("这段音频读不出来，换一个试试")
                    return@launch
                }
                val (samples, rate) = decoded
                val heard = if (Listener.installed(dataDir)) {
                    Listener.text(dataDir, samples, rate, cardLang).orEmpty()
                } else ""
                step = CloneStep.ConfirmImport(samples, rate, heard.ifEmpty { script })
            } finally {
                tmp.delete()
            }
        }
    }

    // Header
    Row(
        Modifier.fillMaxWidth().padding(bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        val s = step
        Text(
            if (s is CloneStep.Ready) t("听听像不像") else t("克隆一个声音"),
            fontSize = 15.sp,
            fontWeight = FontWeight.Bold,
            color = YY.c.t1,
            modifier = Modifier.weight(1f),
        )
        if (s is CloneStep.Ready) {
            Text(
                t("✓ 录音合格 · %@ 秒", "%.1f".format(s.seconds)),
                fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold,
                color = YY.tint.accent,
                style = YY.tnum,
            )
        } else {
            Text(
                t("取消"),
                fontSize = 12.sp,
                color = YY.c.t3,
                modifier = Modifier.clickable {
                    recorder?.stop()
                    recording = false
                    onCancel()
                }.padding(4.dp),
            )
        }
    }

    when (val s = step) {
        CloneStep.Record -> {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(16.dp))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                // Mandatory consent
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clip(RoundedCornerShape(11.dp))
                        .background(YY.c.bg)
                        .clickable { consent = !consent }
                        .padding(horizontal = 11.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(
                        if (consent) Icons.checkCircle else Icons.circle,
                        null,
                        tint = if (consent) YY.tint.accent else YY.c.t3,
                        modifier = Modifier.size(18.dp),
                    )
                    Text(
                        t("这是我的声音，或者我得到了本人同意"),
                        fontSize = 11.5.sp,
                        fontWeight = FontWeight.Medium,
                        color = YY.c.t1,
                        modifier = Modifier.weight(1f),
                    )
                }

                // Script box
                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.bg, RoundedCornerShape(13.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(13.dp))
                        .padding(horizontal = 14.dp, vertical = 12.dp),
                ) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(t("请用平常自然的语气念这一句"), fontSize = 10.5.sp, color = YY.c.t3)
                        Text(t("约 8 秒"), fontSize = 10.5.sp, color = YY.c.t3)
                    }
                    Spacer(Modifier.height(6.dp))
                    Text(script, fontFamily = YY.serif, fontSize = 15.sp, lineHeight = 27.sp, color = YY.c.t1)
                }

                // Record zone
                Column(
                    Modifier.fillMaxWidth().padding(vertical = 6.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Box(
                        Modifier
                            .size(54.dp)
                            .alpha(if (consent) 1f else 0.38f)
                            .clip(CircleShape)
                            .background(if (recording) YY.c.danger.copy(alpha = 0.12f) else YY.tint.accentSoft)
                            .border(1.dp, if (recording) YY.c.danger.copy(alpha = 0.4f) else YY.tint.edge, CircleShape)
                            .clickable(enabled = consent) {
                                errorMsg = null
                                if (recording) {
                                    finishRecording()
                                } else if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                                    permissionLauncher.launch(Manifest.permission.RECORD_AUDIO)
                                } else {
                                    recSeconds = 0.0
                                    for (i in meterBars.indices) meterBars[i] = 0.15f
                                    val r = VoiceSampleRecorder()
                                    if (r.start()) {
                                        recorder = r
                                        recording = true
                                    } else {
                                        errorMsg = t("麦克风启动失败，请重试")
                                    }
                                }
                            },
                        contentAlignment = Alignment.Center,
                    ) {
                        if (recording) {
                            Box(Modifier.size(16.dp).background(YY.c.danger, RoundedCornerShape(3.dp)))
                        } else {
                            Icon(Icons.waveform, t("点一下开始录"), tint = YY.tint.accent, modifier = Modifier.size(22.dp))
                        }
                    }

                    Row(
                        Modifier.height(18.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        for (lvl in meterBars) {
                            Box(
                                Modifier
                                    .width(3.dp)
                                    .height((lvl * 18f).coerceIn(4f, 18f).dp)
                                    .background(if (recording) YY.tint.accent else YY.c.t3.copy(alpha = 0.4f), RoundedCornerShape(1.5.dp)),
                            )
                        }
                    }

                    Text(
                        if (recording) {
                            t("正在录音 %@ · 念完停顿 1.2 秒自动停", "%02d:%02d".format(recSeconds.toInt() / 60, recSeconds.toInt() % 60))
                        } else if (!consent) {
                            t("先勾上面那一行同意声明就能开始录")
                        } else {
                            t("点一下开始录，念完停顿一秒自动结束")
                        },
                        fontSize = 11.5.sp,
                        color = YY.c.t2,
                        style = YY.tnum,
                    )
                }

                Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                Row(
                    Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    Text(t("不联网，录音只留在这台设备"), fontSize = 11.sp, color = YY.c.t3)
                    Text(
                        t("导入一段录音…"),
                        fontSize = 11.5.sp,
                        fontWeight = FontWeight.Medium,
                        color = if (consent) YY.tint.accent else YY.c.t3,
                        modifier = Modifier
                            .clickable(enabled = consent) { importLauncher.launch("audio/*") }
                            .padding(vertical = 2.dp),
                    )
                }
            }
        }

        is CloneStep.ConfirmImport -> {
            var editedText by remember(s) { mutableStateOf(s.draftText) }
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(16.dp))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Text(t("核对这段录音念的文字"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                Text(t("一字不差地对应录音里的内容，合成声音才准。"), fontSize = 11.sp, color = YY.c.t2)
                OutlinedTextField(
                    value = editedText,
                    onValueChange = { editedText = it },
                    minLines = 2,
                    modifier = Modifier.fillMaxWidth(),
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End)) {
                    YYButton(t("再录一次"), size = ButtonSize.Small) { step = CloneStep.Record }
                    YYButton(
                        t("下一步"),
                        kind = ButtonKind.Main,
                        size = ButtonSize.Small,
                        enabled = editedText.isNotBlank(),
                    ) {
                        scope.launch {
                            evaluateSample(s.samples, s.rate, editedText.trim(), checkTranscript = false)
                        }
                    }
                }
            }
        }

        is CloneStep.Fault -> {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(16.dp))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.bg, RoundedCornerShape(13.dp))
                        .padding(horizontal = 14.dp, vertical = 12.dp),
                ) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(t("刚才念的话"), fontSize = 10.5.sp, color = YY.c.t3)
                        Text(t("重录即可"), fontSize = 10.5.sp, color = YY.c.t3)
                    }
                    Spacer(Modifier.height(5.dp))
                    Text(script, fontFamily = YY.serif, fontSize = 14.5.sp, lineHeight = 26.sp, color = YY.c.t1)
                }

                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.danger.copy(alpha = 0.08f), RoundedCornerShape(12.dp))
                        .border(0.5.dp, YY.c.danger.copy(alpha = 0.25f), RoundedCornerShape(12.dp))
                        .padding(horizontal = 13.dp, vertical = 11.dp),
                    verticalArrangement = Arrangement.spacedBy(3.dp),
                ) {
                    val msg = s.check.messages.joinToString("；").ifEmpty { t("录音还没达标，再试一次") }
                    Text(msg, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.danger)
                    Text(
                        t("时长 %@ 秒 · 音量 %@ dBFS · 信噪比 %@ dB", "%.1f".format(s.check.seconds), "%.0f".format(s.check.speechDb), "%.0f".format(s.check.snrDb)),
                        fontSize = 11.sp,
                        color = YY.c.t2,
                        style = YY.tnum,
                    )
                }

                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                    YYButton(t("再录一次"), kind = ButtonKind.Main, size = ButtonSize.Small) {
                        recSeconds = 0.0
                        step = CloneStep.Record
                    }
                }
            }
        }

        is CloneStep.Ready -> {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(16.dp))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                // Preview card
                Column(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.bg, RoundedCornerShape(12.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(12.dp))
                        .clickable {
                            previewing = true
                            scope.launch {
                                try {
                                    val sp = if (LiveVoiceModel.canRun(dataDir)) {
                                        CardVoice.Engine.renderPreviewWithClip(core, dataDir, s.stageFile.path, s.text, cardLang, previewSentence, language)
                                    } else {
                                        withContext(Dispatchers.IO) { SystemVoice.Engine.wav(s.stageFile) }
                                    }
                                    if (sp != null) SystemVoice.Engine.preview(sp)
                                } finally {
                                    previewing = false
                                }
                            }
                        }
                        .padding(horizontal = 13.dp, vertical = 11.dp),
                    verticalArrangement = Arrangement.spacedBy(5.dp),
                ) {
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            if (!bookTitle.isNullOrEmpty() && LiveVoiceModel.canRun(dataDir)) {
                                t("正在用你的声音念《%@》下一句", bookTitle)
                            } else {
                                t("点一下再听一遍")
                            },
                            fontSize = 10.5.sp,
                            color = YY.c.t3,
                        )
                        if (previewing) {
                            CircularProgressIndicator(Modifier.size(12.dp), strokeWidth = 1.5.dp, color = YY.tint.accent)
                        } else {
                            Icon(Icons.play, t("试听"), tint = YY.tint.accent, modifier = Modifier.size(13.dp))
                        }
                    }
                    Text(
                        if (LiveVoiceModel.canRun(dataDir)) previewSentence else s.text,
                        fontFamily = YY.serif,
                        fontSize = 14.sp,
                        lineHeight = 24.sp,
                        color = YY.c.t1,
                    )
                }

                // Name input
                Row(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.bg, RoundedCornerShape(10.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp))
                        .padding(horizontal = 11.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(t("名字"), fontSize = 11.5.sp, color = YY.c.t3)
                    BasicTextField(
                        value = name,
                        onValueChange = { name = it },
                        singleLine = true,
                        textStyle = TextStyle(fontSize = 13.sp, fontWeight = FontWeight.Medium, color = YY.c.t1),
                        cursorBrush = SolidColor(YY.tint.accent),
                        modifier = Modifier.weight(1f),
                    )
                }

                Row(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.End),
                ) {
                    YYButton(t("再录一次"), size = ButtonSize.Small, enabled = !saving) {
                        SystemVoice.Engine.stopPreview()
                        s.stageFile.parentFile?.deleteRecursively()
                        recSeconds = 0.0
                        step = CloneStep.Record
                    }
                    YYButton(
                        if (saving) t("存下…") else t("用这个声音"),
                        kind = ButtonKind.Main,
                        size = ButtonSize.Small,
                        enabled = !saving && name.isNotBlank(),
                    ) {
                        saving = true
                        errorMsg = null
                        scope.launch {
                            try {
                                val saved = withContext(Dispatchers.IO) {
                                    val id = replacingId ?: ("cloned." + UUID.randomUUID().toString().lowercase())
                                    val existing = core.cardFind(null, id)
                                    val gender = core.speakerVoiceGender(s.samples.asList(), s.rate.toUInt())
                                    val emb = SpeakerPrint.print(core, dataDir, s.samples, s.rate)?.asList().orEmpty()
                                    if (emb.isNotEmpty()) core.speakerSetPrint(id, emb)
                                    val card = VoiceCard(
                                        id = id,
                                        name = name.trim(),
                                        gender = gender,
                                        age = CharacterAge.UNKNOWN,
                                        tags = emptyList(),
                                        lang = cardLang,
                                        origin = CardOrigin.Cloned,
                                        clips = listOf(CardClip(CardStyle.NEUTRAL, "neutral.wav", s.text, s.seconds)),
                                        description = "",
                                        version = (existing?.version ?: 0u) + 1u,
                                        narrator = false,
                                        rate = 0f,
                                        embedding = emb,
                                        sync = existing?.sync ?: false,
                                    )
                                    val stageDir = s.stageFile.parentFile!!
                                    val out = core.cardSave(null, card, stageDir.path)
                                    CardVoice.Engine.invalidateCard(id)
                                    ClonedVoices.recordLocalCardMeta(out, s.text, s.seconds.toDouble())
                                    stageDir.deleteRecursively()
                                    out
                                }
                                onSaved(saved)
                            } catch (e: Exception) {
                                errorMsg = e.message ?: t("保存声音卡失败")
                                saving = false
                            }
                        }
                    }
                }
            }
        }
    }

    errorMsg?.let {
        Text(it, fontSize = 11.5.sp, color = YY.c.danger, modifier = Modifier.padding(top = 8.dp))
    }
}

// ── §6.1 step 6 & §6.3 & §7.6-3: 克隆卡详情（加上语气六档 · 跟着同步 · ElevenLabs）──

private val ACTING_STYLES: List<Pair<CardStyle, String>> = listOf(
    CardStyle.JOY to "高兴",
    CardStyle.ANGER to "生气",
    CardStyle.SAD to "难过",
    CardStyle.TENSE to "紧张",
    CardStyle.SOFT to "低声",
    CardStyle.SHOUT to "喊",
)

private val CardStyle.fileStem: String
    get() = when (this) {
        CardStyle.NEUTRAL -> "neutral"
        CardStyle.JOY -> "joy"
        CardStyle.ANGER -> "anger"
        CardStyle.SAD -> "sad"
        CardStyle.TENSE -> "tense"
        CardStyle.SOFT -> "soft"
        CardStyle.SHOUT -> "shout"
    }

@Composable
fun ClonedCardDetailPanel(
    core: YuyueCore,
    dataDir: File,
    cardId: String,
    language: String,
    sampleLine: String,
    onBack: () -> Unit,
    onDeleted: (String) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var card by remember { mutableStateOf<VoiceCard?>(null) }
    var alsoEleven by remember { mutableStateOf(false) }
    var busyEleven by remember { mutableStateOf(false) }
    var recordingStyle by remember { mutableStateOf<CardStyle?>(null) }
    var renaming by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    var errorMsg by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableIntStateOf(0) }

    LaunchedEffect(cardId, tick) {
        card = withContext(Dispatchers.IO) { core.cardFind(null, cardId) }
        alsoEleven = ClonedVoices.alsoElevenLabs(cardId)
    }

    val currentCard = card
    if (currentCard == null) {
        Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
            CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = YY.c.t3)
        }
        return
    }

    val activeStyle = recordingStyle
    if (activeStyle != null) {
        StyleClipRecorderPanel(
            core = core,
            dataDir = dataDir,
            card = currentCard,
            style = activeStyle,
            onCancel = { recordingStyle = null },
            onSaved = { updated ->
                card = updated
                recordingStyle = null
                tick++
            },
        )
        return
    }

    fun playStyle(style: CardStyle) {
        SystemVoice.Engine.stopPreview()
        scope.launch {
            val sp = withContext(Dispatchers.IO) {
                if (LiveVoiceModel.canRun(dataDir)) {
                    CardVoice(currentCard).render(sampleLine, language, style)
                } else {
                    core.cardClipPath(null, currentCard.id, style)?.let { SystemVoice.Engine.wav(File(it)) }
                }
            }
            if (sp != null) SystemVoice.Engine.preview(sp)
        }
    }

    // Header
    Row(
        Modifier.fillMaxWidth().padding(bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Box(
            Modifier.size(32.dp).clip(CircleShape).clickable { onBack() },
            contentAlignment = Alignment.CenterStart,
        ) {
            Icon(Icons.chevronLeft, t("返回"), tint = YY.c.t1, modifier = Modifier.size(22.dp))
        }
        Text(currentCard.name, fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1)
        Text(
            t("克隆"),
            fontSize = 9.5.sp,
            fontWeight = FontWeight.Medium,
            color = YY.c.t2,
            modifier = Modifier
                .background(YY.c.inset, RoundedCornerShape(4.dp))
                .padding(horizontal = 5.dp, vertical = 1.dp),
        )
        Spacer(Modifier.weight(1f))
        YYButton("▶ " + t("试听"), size = ButtonSize.Small) {
            playStyle(CardStyle.NEUTRAL)
        }
    }

    // Six acting tones
    YYLabel(t("加上语气"), t("可只录几档，没录的用平常语气"))
    YYGroup {
        for ((style, labelKey) in ACTING_STYLES) {
            val existingClip = currentCard.clips.firstOrNull { it.style == style }
            row {
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(t(labelKey), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.widthIn(min = 42.dp))
                    if (existingClip != null) {
                        Text(
                            t("已录 · %@ 秒", "%.1f".format(existingClip.seconds)),
                            fontSize = 11.sp,
                            color = YY.tint.accent,
                            style = YY.tnum,
                            modifier = Modifier.weight(1f),
                        )
                        YYButton("▶ " + t("试听"), size = ButtonSize.Small) {
                            playStyle(style)
                        }
                        Text(
                            t("重录"),
                            fontSize = 11.sp,
                            color = YY.c.t3,
                            modifier = Modifier
                                .clickable { recordingStyle = style }
                                .padding(horizontal = 6.dp, vertical = 4.dp),
                        )
                    } else {
                        Text(t("未录"), fontSize = 11.sp, color = YY.c.t3, modifier = Modifier.weight(1f))
                        YYButton(t("录一句"), size = ButtonSize.Small) {
                            recordingStyle = style
                        }
                    }
                }
            }
        }
    }

    // Sync & optional ElevenLabs switches
    Spacer(Modifier.height(12.dp))
    YYGroup {
        row {
            Row(
                Modifier.fillMaxWidth().padding(vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Column(Modifier.weight(1f)) {
                    Text(t("跟着同步"), fontSize = 12.sp, color = YY.c.t1)
                    Text(t("同步到你的其他设备（默认关）"), fontSize = 10.5.sp, color = YY.c.t3)
                }
                Switch(
                    checked = currentCard.sync,
                    onCheckedChange = { newSync ->
                        scope.launch {
                            val cloneDir = File(dataDir, "clones/${currentCard.id}")
                            val updated = withContext(Dispatchers.IO) {
                                core.cardSave(null, currentCard.copy(sync = newSync), cloneDir.path)
                            }
                            card = updated
                        }
                    },
                    colors = SwitchDefaults.colors(
                        checkedTrackColor = YY.tint.fill,
                        checkedThumbColor = YY.c.card,
                        uncheckedTrackColor = YY.c.inset,
                        uncheckedBorderColor = YY.c.line,
                    ),
                )
            }
        }
        if (CloudVoice.Provider.ElevenLabs.apiKey != null) {
            row {
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(10.dp),
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(t("也用 ElevenLabs 念"), fontSize = 12.sp, color = YY.c.t1)
                        Text(t("已填 ElevenLabs Key 时可用（默认关）"), fontSize = 10.5.sp, color = YY.c.t3)
                    }
                    if (busyEleven) {
                        CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 1.5.dp, color = YY.tint.accent)
                    } else {
                        Switch(
                            checked = alsoEleven,
                            onCheckedChange = { on ->
                                busyEleven = true
                                errorMsg = null
                                scope.launch {
                                    try {
                                        val neutralPath = core.cardClipPath(null, currentCard.id, CardStyle.NEUTRAL)
                                        if (neutralPath != null) {
                                            ClonedVoices.setAlsoElevenLabs(currentCard, File(neutralPath), on)
                                            alsoEleven = on
                                        }
                                    } catch (e: Exception) {
                                        errorMsg = e.message
                                    } finally {
                                        busyEleven = false
                                    }
                                }
                            },
                            colors = SwitchDefaults.colors(
                                checkedTrackColor = YY.tint.fill,
                                checkedThumbColor = YY.c.card,
                                uncheckedTrackColor = YY.c.inset,
                                uncheckedBorderColor = YY.c.line,
                            ),
                        )
                    }
                }
            }
        }
    }

    errorMsg?.let {
        YYNote(it, YY.c.danger)
    }

    // Rename & Delete
    Row(
        Modifier.fillMaxWidth().padding(top = 14.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        YYButton(t("重命名"), size = ButtonSize.Small) { renaming = true }
        YYButton(t("删除"), kind = ButtonKind.Danger, size = ButtonSize.Small) { deleting = true }
    }

    if (renaming) {
        var newName by remember { mutableStateOf(currentCard.name) }
        AlertDialog(
            onDismissRequest = { renaming = false },
            containerColor = YY.c.card,
            title = { Text(t("重命名"), color = YY.c.t1) },
            text = {
                OutlinedTextField(
                    value = newName,
                    onValueChange = { newName = it },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                )
            },
            confirmButton = {
                TextButton(
                    enabled = newName.isNotBlank(),
                    onClick = {
                        val trimmed = newName.trim()
                        renaming = false
                        scope.launch {
                            val cloneDir = File(dataDir, "clones/${currentCard.id}")
                            val updated = withContext(Dispatchers.IO) {
                                val saved = core.cardSave(null, currentCard.copy(name = trimmed), cloneDir.path)
                                ClonedVoices.renameMeta(currentCard.id, trimmed)
                                saved
                            }
                            card = updated
                        }
                    },
                ) { Text(t("好"), color = YY.tint.accent) }
            },
            dismissButton = {
                TextButton(onClick = { renaming = false }) { Text(t("取消"), color = YY.c.t2) }
            },
        )
    }

    if (deleting) {
        AlertDialog(
            onDismissRequest = { deleting = false },
            containerColor = YY.c.card,
            title = { Text(t("删除“%@”？", currentCard.name), color = YY.c.t1) },
            text = { Text(t("录好的参考音和语气会从这台设备上删掉。"), color = YY.c.t2) },
            confirmButton = {
                TextButton(
                    onClick = {
                        deleting = false
                        scope.launch {
                            withContext(Dispatchers.IO) {
                                runCatching { core.cardDelete(null, currentCard.id) }
                                ClonedVoices.deleteById(currentCard.id)
                            }
                            onDeleted(currentCard.id)
                        }
                    },
                ) { Text(t("删除"), color = YY.c.danger) }
            },
            dismissButton = {
                TextButton(onClick = { deleting = false }) { Text(t("取消"), color = YY.c.t2) }
            },
        )
    }
}

@Composable
private fun StyleClipRecorderPanel(
    core: YuyueCore,
    dataDir: File,
    card: VoiceCard,
    style: CardStyle,
    onCancel: () -> Unit,
    onSaved: (VoiceCard) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val chinese = !card.lang.lowercase().startsWith("en")
    val instruction = remember(style, chinese) { core.cardStyleInstruction(style, chinese) }
    val script = remember(card.lang, style) { core.cardScript(card.lang, style) }

    var recording by remember { mutableStateOf(false) }
    var recSeconds by remember { mutableDoubleStateOf(0.0) }
    val meterBars = remember { mutableStateListOf(0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f, 0.15f) }
    var recorder by remember { mutableStateOf<VoiceSampleRecorder?>(null) }
    var checkFault by remember { mutableStateOf<CardCheck?>(null) }
    var saving by remember { mutableStateOf(false) }
    var errorMsg by remember { mutableStateOf<String?>(null) }

    DisposableEffect(Unit) { onDispose { recorder?.stop() } }

    fun finishStyleRecording() {
        val r = recorder ?: return
        recording = false
        recorder = null
        val captured = r.stop() ?: return
        val (samples, rate) = captured
        if (samples.isEmpty()) return
        saving = true
        scope.launch {
            val heard = if (Listener.installed(dataDir)) {
                withContext(Dispatchers.Default) { Listener.text(dataDir, samples, rate, card.lang) }
            } else null
            val chk = withContext(Dispatchers.Default) {
                core.cardCheck(samples.asList(), rate.toUInt(), script, heard)
            }
            if (!chk.ok) {
                checkFault = chk
                saving = false
                return@launch
            }
            try {
                val updated = withContext(Dispatchers.IO) {
                    val cloneDir = File(dataDir, "clones/${card.id}").apply { mkdirs() }
                    val fileName = "${style.fileStem}.wav"
                    val secs = core.cardWriteWav(File(cloneDir, fileName).path, samples.asList(), rate.toUInt())
                    val newClips = card.clips.filterNot { it.style == style } + CardClip(style, fileName, script, secs)
                    val nextCard = card.copy(clips = newClips, version = card.version + 1u)
                    val out = core.cardSave(null, nextCard, cloneDir.path)
                    CardVoice.Engine.invalidateCard(card.id)
                    out
                }
                saving = false
                onSaved(updated)
            } catch (e: Exception) {
                errorMsg = e.message
                saving = false
            }
        }
    }

    LaunchedEffect(recording) {
        while (recording && isActive) {
            delay(100)
            val r = recorder ?: break
            recSeconds = r.seconds
            meterBars.removeAt(0)
            meterBars.add(r.level)
            if (r.autoStopped) {
                finishStyleRecording()
                break
            }
        }
    }

    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (!ok) errorMsg = t("语阅没有用麦克风的权限。在系统设置里给语阅打开麦克风。")
    }

    val styleTitle = ACTING_STYLES.firstOrNull { it.first == style }?.second?.let { t(it) } ?: style.name
    Row(
        Modifier.fillMaxWidth().padding(bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(
            t("录一句 · %@", styleTitle),
            fontSize = 15.sp,
            fontWeight = FontWeight.Bold,
            color = YY.c.t1,
            modifier = Modifier.weight(1f),
        )
        Text(
            t("取消"),
            fontSize = 12.sp,
            color = YY.c.t3,
            modifier = Modifier.clickable {
                recorder?.stop()
                recording = false
                onCancel()
            }.padding(4.dp),
        )
    }

    Column(
        Modifier
            .fillMaxWidth()
            .background(YY.c.card, RoundedCornerShape(16.dp))
            .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(
            Modifier
                .fillMaxWidth()
                .background(YY.c.bg, RoundedCornerShape(13.dp))
                .border(0.5.dp, YY.c.line, RoundedCornerShape(13.dp))
                .padding(horizontal = 14.dp, vertical = 12.dp),
        ) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(instruction, fontSize = 10.5.sp, fontWeight = FontWeight.Medium, color = YY.tint.accent)
                Text(t("约 8 秒"), fontSize = 10.5.sp, color = YY.c.t3)
            }
            Spacer(Modifier.height(6.dp))
            Text(script, fontFamily = YY.serif, fontSize = 15.sp, lineHeight = 27.sp, color = YY.c.t1)
        }

        val fault = checkFault
        if (fault != null) {
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.danger.copy(alpha = 0.08f), RoundedCornerShape(12.dp))
                    .border(0.5.dp, YY.c.danger.copy(alpha = 0.25f), RoundedCornerShape(12.dp))
                    .padding(horizontal = 13.dp, vertical = 11.dp),
                verticalArrangement = Arrangement.spacedBy(3.dp),
            ) {
                Text(fault.messages.joinToString("；"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = YY.c.danger)
                Text(t("时长 %@ 秒 · 再录一次即可", "%.1f".format(fault.seconds)), fontSize = 11.sp, color = YY.c.t2, style = YY.tnum)
            }
        }

        Column(
            Modifier.fillMaxWidth().padding(vertical = 6.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                Modifier
                    .size(54.dp)
                    .clip(CircleShape)
                    .background(if (recording) YY.c.danger.copy(alpha = 0.12f) else YY.tint.accentSoft)
                    .border(1.dp, if (recording) YY.c.danger.copy(alpha = 0.4f) else YY.tint.edge, CircleShape)
                    .clickable(enabled = !saving) {
                        checkFault = null
                        errorMsg = null
                        if (recording) {
                            finishStyleRecording()
                        } else if (ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                            permissionLauncher.launch(Manifest.permission.RECORD_AUDIO)
                        } else {
                            recSeconds = 0.0
                            val r = VoiceSampleRecorder()
                            if (r.start()) {
                                recorder = r
                                recording = true
                            }
                        }
                    },
                contentAlignment = Alignment.Center,
            ) {
                if (saving) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp, color = YY.tint.accent)
                } else if (recording) {
                    Box(Modifier.size(16.dp).background(YY.c.danger, RoundedCornerShape(3.dp)))
                } else {
                    Icon(Icons.waveform, t("点一下开始录"), tint = YY.tint.accent, modifier = Modifier.size(22.dp))
                }
            }
            Row(
                Modifier.height(18.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(3.dp),
            ) {
                for (lvl in meterBars) {
                    Box(
                        Modifier
                            .width(3.dp)
                            .height((lvl * 18f).coerceIn(4f, 18f).dp)
                            .background(if (recording) YY.tint.accent else YY.c.t3.copy(alpha = 0.4f), RoundedCornerShape(1.5.dp)),
                    )
                }
            }
            Text(
                if (recording) t("正在录音 %@ · 念完停顿 1.2 秒自动停", "%02d:%02d".format(recSeconds.toInt() / 60, recSeconds.toInt() % 60))
                else t("点一下开始录，念完停顿一秒自动结束"),
                fontSize = 11.5.sp,
                color = YY.c.t2,
                style = YY.tnum,
            )
        }
        errorMsg?.let { Text(it, fontSize = 11.sp, color = YY.c.danger) }
    }
}

// ── 录音器与 1.2s 静音自动停止状态机 ────────────────────────────────────────────

/**
 * Pure state machine for §6.1's "念完自动停（静音 1.2 秒）":
 * waits for at least 0.4 s of speech before starting the 1.2 s trailing-silence timer.
 */
internal class SilenceAutoStopper(
    private val silenceStopSec: Double = 1.2,
    private val maxTotalSec: Double = 16.0,
) {
    var totalSec = 0.0
        private set
    var spokenSec = 0.0
        private set
    var trailingSilenceSec = 0.0
        private set
    private var peakSpeechDb = -35f

    /** Feeds one frame's RMS level in dBFS (`frameSec` seconds) and returns true when recording should auto-stop. */
    fun pushFrame(frameDb: Float, frameSec: Double): Boolean {
        totalSec += frameSec
        if (frameDb >= -35f) {
            spokenSec += frameSec
            if (frameDb > peakSpeechDb) peakSpeechDb = frameDb
            trailingSilenceSec = 0.0
        } else if (spokenSec >= 0.4) {
            val quietGate = maxOf(-42f, peakSpeechDb - 22f)
            if (frameDb < quietGate) {
                trailingSilenceSec += frameSec
            } else {
                trailingSilenceSec = 0.0
            }
        }
        return (spokenSec >= 0.4 && trailingSilenceSec >= silenceStopSec) || totalSec >= maxTotalSec
    }
}

/** Mono 16-bit `AudioRecord` at 24 kHz (or 16 kHz fallback) with live meter & 1.2 s silence auto-stop. */
internal class VoiceSampleRecorder {
    var rate: Int = 24_000
        private set
    private var record: AudioRecord? = null
    private var thread: Thread? = null
    private val pcm = ByteArrayOutputStream()

    @Volatile var seconds: Double = 0.0
        private set
    @Volatile var level: Float = 0.12f
        private set
    @Volatile var autoStopped: Boolean = false
        private set

    @android.annotation.SuppressLint("MissingPermission")
    fun start(): Boolean {
        for (candidate in intArrayOf(24_000, 16_000, 44_100)) {
            val minBuf = AudioRecord.getMinBufferSize(candidate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT)
            if (minBuf <= 0) continue
            val r = runCatching {
                AudioRecord(
                    MediaRecorder.AudioSource.MIC,
                    candidate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    maxOf(minBuf * 2, candidate / 2),
                )
            }.getOrNull() ?: continue
            if (r.state == AudioRecord.STATE_INITIALIZED) {
                rate = candidate
                record = r
                break
            } else {
                r.release()
            }
        }
        val r = record ?: return false
        r.startRecording()
        val stopper = SilenceAutoStopper()
        thread = Thread {
            val chunk = maxOf(1, rate / 10) // 100 ms frames
            val buf = ShortArray(chunk)
            while (record === r) {
                val n = r.read(buf, 0, buf.size)
                if (n <= 0) continue
                var sumSq = 0.0
                val bytes = ByteArray(n * 2)
                for (i in 0 until n) {
                    val s = buf[i] / 32768.0
                    sumSq += s * s
                    bytes[i * 2] = (buf[i].toInt() and 0xFF).toByte()
                    bytes[i * 2 + 1] = ((buf[i].toInt() shr 8) and 0xFF).toByte()
                }
                synchronized(pcm) { pcm.write(bytes) }
                val rms = sqrt(sumSq / n).coerceAtLeast(1e-5)
                val db = (20.0 * log10(rms)).toFloat()
                level = ((db + 50f) / 45f).coerceIn(0.10f, 1f)
                seconds = synchronized(pcm) { pcm.size() } / 2.0 / rate
                if (stopper.pushFrame(db, n.toDouble() / rate)) {
                    autoStopped = true
                    break
                }
            }
        }.apply { isDaemon = true; start() }
        return true
    }

    fun stop(): Pair<FloatArray, Int>? {
        val r = record
        record = null
        thread?.join(400)
        thread = null
        if (r != null) {
            runCatching { r.stop() }
            r.release()
        }
        val raw = synchronized(pcm) { pcm.toByteArray() }
        if (raw.size < 4) return null
        val samples = FloatArray(raw.size / 2) { i ->
            val s = ((raw[i * 2 + 1].toInt() shl 8) or (raw[i * 2].toInt() and 0xFF)).toShort()
            s / 32768f
        }
        return samples to rate
    }
}
