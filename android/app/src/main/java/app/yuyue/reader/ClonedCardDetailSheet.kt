package app.yuyue.reader

import android.Manifest
import android.content.pm.PackageManager
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.ClonedVoices
import app.yuyue.speech.CloudVoice
import app.yuyue.speech.Listener
import app.yuyue.speech.LiveVoiceModel
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
import uniffi.yuyue_core.CardStyle
import uniffi.yuyue_core.VoiceCard
import uniffi.yuyue_core.YuyueCore
import java.io.File

// ── 克隆卡详情（加上语气六档 · 跟着同步 · ElevenLabs）──

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
    val liveState = LiveVoiceModel.state
    var card by remember { mutableStateOf<VoiceCard?>(null) }
    var alsoEleven by remember { mutableStateOf(false) }
    var busyEleven by remember { mutableStateOf(false) }
    var recordingStyle by remember { mutableStateOf<CardStyle?>(null) }
    var renaming by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    var errorMsg by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableIntStateOf(0) }

    LaunchedEffect(Unit) {
        if (!LiveVoiceModel.canRun(dataDir) && !LiveVoiceModel.deviceUnsupported) {
            LiveVoiceModel.onFirstListen(context, core, dataDir)
        }
    }

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
        errorMsg = null
        scope.launch {
            try {
                val sp = withContext(Dispatchers.IO) {
                    if (LiveVoiceModel.canRun(dataDir)) {
                        CardVoice(currentCard).render(sampleLine, language, style)
                    } else {
                        core.cardClipPath(null, currentCard.id, style)?.let { SystemVoice.Engine.wav(File(it)) }
                    }
                }
                if (sp != null) SystemVoice.Engine.preview(sp)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Throwable) {
                errorMsg = e.message ?: e.toString()
            }
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

    if (LiveVoiceModel.deviceUnsupported && !alsoEleven) {
        YYNote(t("这台设备内存不足 6 GB，暂不支持用本机克隆声音朗读"), YY.c.t3)
    } else if (!LiveVoiceModel.canRun(dataDir) && !alsoEleven && liveState !is LiveVoiceModel.State.Unsupported) {
        PrepareLiveVoiceRow(core, dataDir, Modifier.padding(bottom = 8.dp))
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
                            } else {
                                errorMsg = t("麦克风启动失败，请重试")
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
                    Icon(Icons.microphone, t("点一下开始录"), tint = YY.tint.accent, modifier = Modifier.size(22.dp))
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
