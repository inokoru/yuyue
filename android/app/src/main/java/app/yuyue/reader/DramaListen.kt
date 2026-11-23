package app.yuyue.reader

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import app.yuyue.design.ButtonSize
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYSegments
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.DramaMade

/** 听成一部剧: the play made ahead, how it's said, and taken away as an audiobook. */
@Composable
fun DramaListenGroup(library: Library, bookID: String, section: UInt, language: String, narrator: String?) {
    val core = library.core
    val synth = library.dramaSynth
    var dramaEnabled by remember { mutableStateOf(synth.isDramaActive(bookID)) }
    var tagsOut by remember { mutableStateOf(false) }
    var sound by remember { mutableStateOf(uniffi.yuyue_core.DramaSound(true, true, true)) }
    var canAct by remember { mutableStateOf(false) }
    var preparing by remember { mutableStateOf(synth.preparing(bookID)) }
    var made by remember { mutableStateOf<DramaMade?>(null) }
    var exporting by remember { mutableStateOf<String?>(null) }
    var borrowedFrom by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableStateOf(0) }
    val scope = rememberCoroutineScope()
    LaunchedEffect(Unit) { synth.sectionMade.collect { if (it == bookID) tick++ } }
    LaunchedEffect(Unit) { synth.settingsChanged.collect { if (it == bookID) tick++ } }
    // Tell nearby paired Macs which book & chapter we are listening to every 60 s and on chapter turns.
    LaunchedEffect(bookID, section, dramaEnabled) {
        if (!dramaEnabled) return@LaunchedEffect
        while (kotlinx.coroutines.currentCoroutineContext()[kotlinx.coroutines.Job]?.isActive != false) {
            library.recordingFetch.want(bookID, section)
            library.recordingFetch.dramaOffer(bookID)?.let { library.recordingFetch.takeClips(bookID, it) }
            kotlinx.coroutines.delay(60_000)
        }
    }
    LaunchedEffect(tick) {
        dramaEnabled = synth.isDramaActive(bookID)
        preparing = synth.preparing(bookID)
        tagsOut = runCatching { core.dramaMode(bookID) }.getOrDefault(false)
        sound = runCatching { core.dramaSound(bookID) }.getOrDefault(sound)
        canAct = runCatching { core.dramaCanAct(bookID) }.getOrDefault(false)
        made = withContext(Dispatchers.IO) { runCatching { core.dramaMade(bookID) }.getOrNull() }
        borrowedFrom = core.dramaBorrowedFrom(bookID)
        // Clips taken from a Mac: the Mac may have made more since.
        if (preparing && borrowedFrom != null) library.recordingFetch.dramaOffer(bookID)?.let { library.recordingFetch.takeClips(bookID, it) }
    }
    val liveActing = app.yuyue.speech.LiveVoiceModel.canRun(library.dataDir) && app.yuyue.speech.CardVoice.speaksLanguage(language)
    Column(Modifier.padding(top = YY.s(10)).fillMaxWidth().background(YY.c.card, androidx.compose.foundation.shape.RoundedCornerShape(14.dp)).padding(horizontal = YY.s(12))) {
        Row(
            Modifier.fillMaxWidth().padding(vertical = YY.s(8)),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
        ) {
            Column(Modifier.weight(1f)) {
                Text(t("广播剧模式"), style = YY.font(12f, FontWeight.Bold), color = YY.c.t1)
                Text(
                    if (dramaEnabled) t("按人物自动分声，带语气与场景音效，点了就听")
                    else t("开启后自动为人物配声并加入场景音效"),
                    style = YY.font(10f),
                    color = YY.c.t3,
                    maxLines = 2,
                )
            }
            Toggle(dramaEnabled) { on ->
                dramaEnabled = on
                runCatching { core.dramaSetEnabled(bookID, on) }
                if (!on) {
                    if (preparing) {
                        preparing = false
                        synth.setPreparing(bookID, false)
                    }
                    synth.stop(bookID)
                }
                synth.notifySettingsChanged(bookID)
            }
        }
        if (dramaEnabled) {
            Line(t("旁白提示词"), t("如「宝玉笑道」等引述语")) {
                YYSegments(listOf(false to t("保留"), true to t("略过")), tagsOut, { v ->
                    tagsOut = v
                    runCatching { core.dramaSetMode(bookID, v) }
                    synth.notifySettingsChanged(bookID)
                }, small = true, modifier = Modifier.width(YY.s(110)))
            }
            fun set(s: uniffi.yuyue_core.DramaSound) {
                sound = s
                runCatching { core.dramaSetSound(bookID, s) }
                synth.notifySettingsChanged(bookID)
            }
            if (canAct || liveActing) Line(t("情感语气"), t("按原文语境表现笑、叹、低语等语气")) { Toggle(sound.acting) { set(sound.copy(acting = it)) } }
            else Line(t("情感语气"), t("需使用支持情感的云端音色或增强本地音色")) { Toggle(false, enabled = false) {} }
            Line(t("环境音"), t("随室内、雨夜、水边等场景铺垫氛围音")) { Toggle(sound.beds) { set(sound.copy(beds = it)) } }
            Line(t("音效"), t("随正文触发敲门、雷声、钟声等音效")) { Toggle(sound.spots) { set(sound.copy(spots = it)) } }
            val m = made
            val taking = library.recordingFetch.takingClips[bookID]
            val suffix = when {
                !preparing -> ""
                synth.waitingForPower -> t(" · 连接电源后自动继续")
                else -> t(" · 充电期间自动继续")
            }
            val progress = when {
                taking != null -> t("从 Mac 拿做好的片段 %lld / %lld", taking.first.toLong(), taking.second.toLong())
                m == null -> t("不开启也可即时听；开启后提前做好全书")
                borrowedFrom != null -> t("已完成 %lld / %lld 章 · 来自「%@」", m.made.toLong(), m.sections.toLong(), borrowedFrom!!)
                else -> t("已完成 %lld / %lld 章", m.made.toLong(), m.sections.toLong()) + suffix
            }
            Line(t("后台预合成全书"), progress) {
                Toggle(preparing) { on ->
                    preparing = on
                    synth.setPreparing(bookID, on)
                    if (on && !dramaEnabled) {
                        dramaEnabled = true
                        runCatching { core.dramaSetEnabled(bookID, true) }
                    }
                    synth.notifySettingsChanged(bookID)
                    if (!on) synth.stop(bookID)
                    // 局域网传片段: a Mac nearby that has made the play hands its clips over; else made here.
                    else scope.launch {
                        val offer = library.recordingFetch.dramaOffer(bookID)
                        if (offer != null) library.recordingFetch.takeClips(bookID, offer)
                        else if (core.dramaBorrowedFrom(bookID) == null) synth.start(bookID, language, section, narrator)
                    }
                }
            }
        }
        val m = made
        if ((m?.made ?: 0u) > 0u) {
            Line(t("导出成有声书"), exporting ?: t("做好的章，一个 m4b，带章节 · 合成的声音受各自服务的条款约束，导出的文件供自己听")) {
                YYButton(t("导出…"), size = ButtonSize.Small, enabled = exporting == null) {
                    exporting = t("正在混音和压缩，一章要几十秒")
                    scope.launch {
                        val file = synth.exportM4B(bookID)
                        exporting = if (file == null) t("没导出来，再试一次") else null
                        file?.let { synth.share(it) }
                    }
                }
            }
        }
    }
}

@Composable
private fun Line(name: String, detail: String, control: @Composable () -> Unit) {
    Column {
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Row(Modifier.fillMaxWidth().padding(vertical = YY.s(7)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            Column(Modifier.weight(1f)) {
                Text(name, style = YY.font(12f), color = YY.c.t1)
                Text(detail, style = YY.font(10f), color = YY.c.t3, maxLines = 2)
            }
            control()
        }
    }
}

@Composable
private fun Toggle(on: Boolean, enabled: Boolean = true, set: (Boolean) -> Unit) {
    Switch(on, set, enabled = enabled, colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line))
}
