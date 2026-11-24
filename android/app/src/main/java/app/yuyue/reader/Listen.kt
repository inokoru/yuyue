package app.yuyue.reader

import app.yuyue.design.YYSlider
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.RateMenu
import app.yuyue.design.rateLabel
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.selection.selectable
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.speech.BookVoice
import app.yuyue.speech.Player
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.abs
import kotlin.math.roundToInt

/** 听书, over the capsule (as on iPhone): the voice and 试听, speed (the pitch stays), the sleep
 *  timer, and 单句循环. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ListenSheet(model: ReaderModel, dismiss: () -> Unit) {
    val player = model.player ?: return dismiss()
    var pickingVoice by remember { mutableStateOf(false) }
    var currentVoiceId by remember { mutableStateOf<String?>(null) }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.card, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            VoiceRow(model, player, onCurrentChanged = { currentVoiceId = it?.id }) { pickingVoice = true }
            val readyVoice = player.readyOfflineVoice
            if (readyVoice != null) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.bg, RoundedCornerShape(10.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp))
                        .padding(horizontal = 11.dp, vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                        Text(
                            t("离线旁白「%@」已下好", Voices.title(readyVoice)),
                            fontSize = 11.5.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = YY.c.t1,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                        Text(
                            t("确认后从当前句切换，不自动打断"),
                            fontSize = 10.5.sp,
                            color = YY.c.t2,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    Text(
                        t("暂不"),
                        fontSize = 11.sp,
                        color = YY.c.t2,
                        modifier = Modifier
                            .clip(RoundedCornerShape(50))
                            .clickable { player.dismissReadyOfflineVoice() }
                            .padding(horizontal = 8.dp, vertical = 5.dp),
                    )
                    Text(
                        t("切换"),
                        fontSize = 11.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = YY.tint.accent,
                        modifier = Modifier
                            .clip(RoundedCornerShape(50))
                            .background(YY.tint.accentSoft)
                            .clickable {
                                player.acceptReadyOfflineVoice()
                                currentVoiceId = readyVoice.id
                            }
                            .padding(horizontal = 10.dp, vertical = 5.dp),
                    )
                }
            } else {
                PrepareBetterVoiceRow(model.core, model.library.dataDir, player.language)
            }
            player.thermalNotice?.let { notice ->
                Text(notice, fontSize = 11.sp, color = YY.c.warn, modifier = Modifier.padding(horizontal = 4.dp))
            }
            Speed(model, player)
            Sleep(model, player)
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                if (model.hasDialogue) {
                    // 分角色 / 广播剧: 人物声音.
                    var own by remember { mutableStateOf(0) }
                    var dramaOn by remember { mutableStateOf(false) }
                    LaunchedEffect(model.castShown) {
                        own = runCatching { model.core.bookRoles(model.bookID) }.getOrDefault(emptyList()).count { it.voice.voice != null }
                        dramaOn = model.library.dramaSynth.isDramaActive(model.bookID)
                    }
                    val detail = when {
                        dramaOn -> t("广播剧开")
                        own == 0 -> t("都跟旁白")
                        else -> t("%lld 人单独设", own)
                    }
                    Tile(Icons.personTwo, t("人物声音"), detail, dramaOn || own > 0, Modifier.weight(1f)) {
                        dismiss()
                        model.castShown = true
                    }
                }
                val range = player.loopRange
                val loopDetail = if (player.loop && range != null) t("第 %lld–%lld 句", (range.second + 1).toLong(), (range.third + 1).toLong()) else if (player.loop) t("开") else t("关")
                Tile(Icons.repeatOne, t("单句循环"), loopDetail, player.loop, Modifier.weight(1f)) { player.loop = !player.loop }
                // 念完停一下: 关 → 2 秒 → 按句长 → 关
                val p = player.pauseAfter
                Tile(Icons.pauseCircle, t("念完停一下"), when { p == 0.0 -> t("关"); p == Player.PAUSE_AS_LONG -> t("按句长"); else -> t("%lld 秒", p.toLong()) }, p != 0.0, Modifier.weight(1f)) {
                    player.pauseAfter = when (p) { 0.0 -> 2.0; Player.PAUSE_AS_LONG -> 0.0; else -> Player.PAUSE_AS_LONG }
                }
                if (!model.hasDialogue) {
                    if (player.spot != null) Tile(Icons.stop, t("停止朗读"), t("回到翻页"), false, Modifier.weight(1f)) { player.stop(); dismiss() }
                    else Spacer(Modifier.weight(1f))
                }
            }
            if (model.hasDialogue && player.spot != null) {
                Row(Modifier.fillMaxWidth()) {
                    Tile(Icons.stop, t("停止朗读"), t("回到翻页"), false, Modifier.fillMaxWidth()) { player.stop(); dismiss() }
                }
            }
        }
    }
    if (pickingVoice) {
        val sec = player.spot?.section ?: model.section
        val curSentence = player.spot?.sentence ?: model.sentence
        val nextLine = remember(sec, curSentence) {
            val all = runCatching { model.core.spokenSentences(model.bookID, sec.toUInt()) }.getOrDefault(emptyList())
            all.drop(curSentence + 1).firstOrNull { s -> s.any { it.isLetterOrDigit() } }
                ?: all.getOrNull(curSentence)?.takeIf { s -> s.any { it.isLetterOrDigit() } }
        }
        VoicePickerSheet(
            core = model.core,
            dataDir = model.library.dataDir,
            bookID = model.bookID,
            bookTitle = model.detail?.summary?.title,
            language = player.language,
            selectedId = currentVoiceId,
            nextBookSentence = nextLine,
            onSelect = { v ->
                player.choose(v, player.language)
                currentVoiceId = v.id
            },
            dismiss = { pickingVoice = false },
        )
    }
}

@Composable
private fun VoiceRow(
    model: ReaderModel,
    player: Player,
    onCurrentChanged: (BookVoice?) -> Unit,
    openPicker: () -> Unit,
) {
    val language = player.language
    val kokoroState = app.yuyue.speech.KokoroModel.state
    val liveState = app.yuyue.speech.LiveVoiceModel.state
    var current by remember { mutableStateOf<BookVoice?>(null) }
    var previewing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    DisposableEffect(Unit) { onDispose { SystemVoice.Engine.stopPreview() } }
    LaunchedEffect(language, kokoroState, liveState, player.spot?.section) {
        current = player.currentVoice(language)
        onCurrentChanged(current)
    }
    Row(
        Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(14.dp)).padding(start = 9.dp, end = 8.dp, top = 9.dp, bottom = 9.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Box(Modifier.size(32.dp).background(YY.c.card, RoundedCornerShape(9.dp)), contentAlignment = Alignment.Center) {
            Icon(Icons.waveform, null, tint = YY.c.t1, modifier = Modifier.size(17.dp))
        }
        Column(Modifier.weight(1f).clickable { openPicker() }) {
            val v = current
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                Text(v?.let(Voices::title) ?: t("没有能读的声音"), fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                if (v is SystemVoice) {
                    Text(t("系统自带 · 不推荐"), fontSize = 9.5.sp, color = Color(0xFFD97706), modifier = Modifier.background(YY.c.card, RoundedCornerShape(4.dp)).padding(horizontal = 5.dp, vertical = 1.dp))
                }
            }
            val subText = when {
                player.readyOfflineVoice != null -> t("离线默认旁白已下好，可在下方确认切换")
                (v is SystemVoice || (v as? app.yuyue.speech.CloudVoice)?.provider == app.yuyue.speech.CloudVoice.Provider.Free) &&
                    (kokoroState is app.yuyue.speech.KokoroModel.State.Downloading || liveState is app.yuyue.speech.LiveVoiceModel.State.Downloading) ->
                    t("离线旁白下载中，下好后会提示你切换")
                else ->
                    listOf(language, if (v?.isOffline == false) t("云端（已读内容本地缓存）") else t("本地离线")).filter { it.isNotEmpty() }.joinToString(" · ")
            }
            Text(subText, fontSize = 10.5.sp, color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        current?.let { v ->
            Text(t("试听"), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.clip(RoundedCornerShape(50)).background(YY.c.card).clickable {
                if (model.isPlaying) player.pause()
                if (previewing) {
                    SystemVoice.Engine.stopPreview()
                    previewing = false
                    return@clickable
                }
                if (v is SystemVoice) {
                    if (!v.preview(sample(language))) {
                        player.failure = t("系统朗读还没准备好，请稍后再试或检查文字转语音设置")
                    }
                } else {
                    previewing = true
                    scope.launch {
                        try {
                            val speech = withContext(Dispatchers.IO) { v.render(sample(language), language) }
                            if (speech != null) SystemVoice.Engine.preview(speech)
                        } catch (e: kotlinx.coroutines.CancellationException) {
                            throw e
                        } catch (e: Throwable) {
                            player.failure = t("试听失败（%@）", e.message ?: e.toString())
                        } finally {
                            previewing = false
                        }
                    }
                }
            }.padding(horizontal = 10.dp, vertical = 5.dp))
        }
        Row(Modifier.clickable { openPicker() }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(t("换声音"), fontSize = 11.sp, color = YY.c.t2)
            Icon(Icons.chevronRight, null, tint = YY.c.t2, modifier = Modifier.size(14.dp))
        }
    }
}

private fun sample(language: String) = if (language.lowercase().startsWith("zh")) "你好，我来给你读这本书。" else "Hello, I'll read this book to you."

private val rates = listOf(0.8f, 1.0f, 1.2f, 1.5f, 2.0f)

private fun rateText(r: Float) = if (r == r.roundToInt().toFloat()) "${r.roundToInt()}" else "%.1f".format(r)

@Composable
private fun Speed(model: ReaderModel, player: Player) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(t("速度"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.alignByBaseline())
            Text(rateText(player.rate) + "×", fontSize = 17.sp, fontWeight = FontWeight.Bold, color = YY.c.t1, style = YY.tnum, modifier = Modifier.alignByBaseline())
            Text(t("不变调"), fontSize = 10.sp, fontWeight = FontWeight.Medium, color = YY.c.t2, modifier = Modifier.alignByBaseline().background(YY.c.inset, RoundedCornerShape(5.dp)).padding(horizontal = 6.dp, vertical = 2.dp))
            Spacer(Modifier.weight(1f))
            Text(t("本%@约剩 %lld 分钟", t("章"), minutesLeft(model, player)), fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.alignByBaseline())
        }
        YYSlider(player.rate, { player.changeRate((it * 10).roundToInt() / 10f) }, 0.8f..2.0f, description = t("语速"), state = rateText(player.rate))
        Row(Modifier.fillMaxWidth().padding(horizontal = 2.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            for (r in rates) {
                val on = abs(player.rate - r) < 0.05f
                Text(
                    rateText(r), fontSize = 11.sp, fontWeight = if (on) FontWeight.Bold else FontWeight.Normal,
                    color = if (on) YY.c.t1 else YY.c.t3, style = YY.tnum,
                    modifier = Modifier.clip(RoundedCornerShape(6.dp)).selectable(on, role = androidx.compose.ui.semantics.Role.RadioButton) { player.changeRate(r) }.padding(horizontal = 6.dp, vertical = 4.dp),
                )
            }
        }
    }
}

/** Minutes of listening left in the chapter at this speed (about 220 characters a minute at 1×). */
private fun minutesLeft(model: ReaderModel, player: Player): Int {
    val s = model.detail?.sections?.getOrNull(model.section) ?: return 0
    val n = maxOf(1, s.sentences.toInt())
    val left = s.units.toDouble() * (n - model.sentence) / n
    return maxOf(1, (left / 220 / maxOf(0.5f, player.rate)).roundToInt())
}

@Composable
private fun Sleep(model: ReaderModel, player: Player) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(t("睡眠定时"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(t("停止时自动添加书签"), fontSize = 10.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        val s = player.sleep
        Column(
            Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(12.dp)).padding(3.dp),
            verticalArrangement = Arrangement.spacedBy(3.dp),
        ) {
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                Choice(t("关"), null, s == Player.Sleep.Off, Modifier.weight(1f)) { player.sleep = Player.Sleep.Off }
                Choice(t("本句读完"), null, s == Player.Sleep.Sentence, Modifier.weight(1f)) { player.sleep = if (s == Player.Sleep.Sentence) Player.Sleep.Off else Player.Sleep.Sentence }
                Choice(t("本章结束"), t("约 %lld 分钟", minutesLeft(model, player)), s == Player.Sleep.Chapter, Modifier.weight(1f)) { player.sleep = if (s == Player.Sleep.Chapter) Player.Sleep.Off else Player.Sleep.Chapter }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                for (m in listOf(15, 30, 60)) {
                    val on = s is Player.Sleep.Until && s.minutes == m
                    val left = (s as? Player.Sleep.Until)?.takeIf { it.minutes == m }?.let { maxOf(1, ((it.at - System.currentTimeMillis()) / 60000.0).toInt() + 1) }
                    Choice(t("%lld 分钟", m), left?.let { t("还剩 %lld 分", it) }, on, Modifier.weight(1f)) {
                        player.sleep = if (on) Player.Sleep.Off else Player.Sleep.Until(System.currentTimeMillis() + m * 60_000L, m)
                    }
                }
            }
        }
    }
}

@Composable
private fun Choice(title: String, detail: String?, on: Boolean, modifier: Modifier, onClick: () -> Unit) {
    Column(
        modifier.heightIn(min = 42.dp).clip(RoundedCornerShape(9.dp)).background(if (on) YY.c.card else Color.Transparent).clickable(onClick = onClick).padding(horizontal = 6.dp, vertical = 5.dp),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
    ) {
        Text(
            title, fontSize = 12.sp, fontWeight = if (on) FontWeight.Bold else FontWeight.Medium,
            color = YY.c.t1, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis,
        )
        if (on && detail != null) Text(detail, fontSize = 10.sp, color = YY.c.t2, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun Tile(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, detail: String, on: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val ink = if (on) YY.tint.accent else YY.c.t1
    Column(
        modifier.clip(RoundedCornerShape(12.dp)).background(if (on) YY.tint.accentSoft else YY.c.inset).clickable(onClick = onClick).padding(10.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Icon(icon, null, tint = ink, modifier = Modifier.size(15.dp).padding(bottom = 1.dp))
        Text(title, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(detail, fontSize = 10.5.sp, color = YY.c.t2, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
