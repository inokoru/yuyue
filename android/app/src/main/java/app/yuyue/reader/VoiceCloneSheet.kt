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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
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
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoice
import app.yuyue.speech.ClonedVoices
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
import uniffi.yuyue_core.VoiceCard
import uniffi.yuyue_core.YuyueCore
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.UUID
import kotlin.math.log10
import kotlin.math.sqrt

// ── 本机离线克隆面板 ──────────────────────────────────

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
        // Automatically synthesize & play current book's next sentence.
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
                        if (consent) Icons.checkCircleFill else Icons.circle,
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

// ── 录音器与 1.2s 静音自动停止状态机 ────────────────────────────────────────────

/**
 * Pure state machine for "念完自动停（静音 1.2 秒）":
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
