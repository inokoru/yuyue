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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.foundation.text.BasicTextField
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
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
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
    var currentVoice by remember { mutableStateOf<BookVoice?>(null) }
    var page by remember { mutableStateOf(ListenPage.Main) }
    var moreOpen by remember { mutableStateOf(false) }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.card, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            when (page) {
                ListenPage.Sleep -> SleepPage(model, player) { page = ListenPage.Main }
                ListenPage.Pronounce -> PronouncePage(model, player, dismiss) { page = ListenPage.Main }
                ListenPage.Main -> Unit
            }
            if (page == ListenPage.Main) {
            ListenHeader(player, moreOpen, onMore = { moreOpen = it }, onSleep = { page = ListenPage.Sleep }, onMark = {
                model.markCurrentHeard()
                dismiss()
            }, onFix = { page = ListenPage.Pronounce })
            VoiceRow(model, player, currentVoice, onCurrentChanged = { currentVoice = it }) { pickingVoice = true }
            PrepareBetterVoiceRow(model.core, model.library.dataDir, player.language)
            val liveState = app.yuyue.speech.LiveVoiceModel.state
            val needsLiveModel = (currentVoice is app.yuyue.speech.CardVoice || currentVoice?.id?.startsWith("cloned.") == true) &&
                !app.yuyue.speech.LiveVoiceModel.canRun(model.library.dataDir)
            if (needsLiveModel || liveState is app.yuyue.speech.LiveVoiceModel.State.Downloading) {
                PrepareLiveVoiceRow(model.core, model.library.dataDir)
            }
            player.thermalNotice?.let { notice ->
                Text(notice, fontSize = 11.sp, color = YY.c.warn, modifier = Modifier.padding(horizontal = 4.dp))
            }
            Speed(model, player)
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
            selectedId = currentVoice?.id,
            nextBookSentence = nextLine,
            readyVoiceId = player.readyOfflineVoice?.id,
            onSelect = { v ->
                player.choose(v, player.language)
                currentVoice = v
            },
            dismiss = {
                pickingVoice = false
                player.dismissReadyOfflineVoice()
            },
        )
    }
}

@Composable
private fun VoiceRow(
    model: ReaderModel,
    player: Player,
    current: BookVoice?,
    onCurrentChanged: (BookVoice?) -> Unit,
    openPicker: () -> Unit,
) {
    val language = player.language
    val kokoroState = app.yuyue.speech.KokoroModel.state
    val liveState = app.yuyue.speech.LiveVoiceModel.state
    var previewing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    DisposableEffect(Unit) { onDispose { SystemVoice.Engine.stopPreview() } }
    LaunchedEffect(language, kokoroState, liveState, player.spot?.section, player.voiceVersion, current?.id) {
        onCurrentChanged(player.currentVoice(language))
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
                    Text(t("系统语音"), fontSize = 9.5.sp, color = YY.c.t2, modifier = Modifier.background(YY.c.card, RoundedCornerShape(4.dp)).padding(horizontal = 5.dp, vertical = 1.dp))
                }
            }
            val subText = when {
                player.readyOfflineVoice != null -> t("离线声音「%@」已下好 · 点右侧换声音", Voices.title(player.readyOfflineVoice!!))
                (v is SystemVoice || (v as? app.yuyue.speech.CloudVoice)?.provider == app.yuyue.speech.CloudVoice.Provider.Free) &&
                    (kokoroState is app.yuyue.speech.KokoroModel.State.Downloading || liveState is app.yuyue.speech.LiveVoiceModel.State.Downloading) ->
                    t("离线旁白下载中，下好后会提示你切换")
                else ->
                    listOf(language, if (v?.isOffline == false) t("云端（已读内容本地缓存）") else t("本地离线")).filter { it.isNotEmpty() }.joinToString(" · ")
            }
            Text(subText, fontSize = 10.5.sp, color = YY.c.t2, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        current?.let { v ->
            var previewJob by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }
            androidx.compose.runtime.DisposableEffect(Unit) {
                onDispose {
                    previewJob?.cancel()
                    SystemVoice.Engine.stopPreview()
                }
            }
            Text(
                if (previewing) t("合成中…") else t("试听"),
                fontSize = 11.sp,
                fontWeight = FontWeight.SemiBold,
                color = YY.c.t1,
                modifier = Modifier.clip(RoundedCornerShape(50)).background(YY.c.card).clickable {
                    if (model.isPlaying) player.pause()
                    if (previewing) {
                        previewJob?.cancel()
                        previewJob = null
                        SystemVoice.Engine.stopPreview()
                        previewing = false
                        return@clickable
                    }
                    if (v is SystemVoice) {
                        if (!v.preview(sample(language), language)) {
                            player.failure = t("系统朗读还没准备好，请稍后再试或检查文字转语音设置")
                        }
                    } else {
                        previewing = true
                        previewJob?.cancel()
                        previewJob = scope.launch {
                            try {
                                val speech = withContext(Dispatchers.IO) { v.render(sample(language), language) }
                                if (speech != null) {
                                    if (!SystemVoice.Engine.preview(speech)) {
                                        player.failure = t("试听播放失败，请重试")
                                    }
                                } else {
                                    player.failure = t("试听失败，请重试")
                                }
                            } catch (e: kotlinx.coroutines.CancellationException) {
                                throw e
                            } catch (e: Throwable) {
                                player.failure = t("试听失败（%@）", e.message ?: e.toString())
                            } finally {
                                previewing = false
                            }
                        }
                    }
                }.padding(horizontal = 10.dp, vertical = 5.dp),
            )
        }
        Row(Modifier.clickable { openPicker() }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(3.dp)) {
            if (player.readyOfflineVoice != null) {
                Box(Modifier.size(6.dp).background(YY.c.ok, CircleShape))
            }
            Text(t("换声音"), fontSize = 11.sp, color = if (player.readyOfflineVoice != null) YY.c.t1 else YY.c.t2)
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

private enum class ListenPage { Main, Sleep, Pronounce }

private fun minutesRemaining(at: Long): Int = maxOf(1, kotlin.math.ceil((at - System.currentTimeMillis()) / 60000.0).toInt())

private fun sleepBadge(player: Player): String? = when (val s = player.sleep) {
    Player.Sleep.Off -> null
    Player.Sleep.Sentence -> t("本句")
    Player.Sleep.Chapter -> t("本章")
    is Player.Sleep.Until -> t("%lld 分", minutesRemaining(s.at))
}

@Composable
private fun ListenHeader(
    player: Player,
    moreOpen: Boolean,
    onMore: (Boolean) -> Unit,
    onSleep: () -> Unit,
    onMark: () -> Unit,
    onFix: () -> Unit,
) {
    val sleepOn = player.sleep != Player.Sleep.Off
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(t("听书"), fontSize = 15.sp, fontWeight = FontWeight.Bold, color = YY.c.t1)
        Spacer(Modifier.weight(1f))
        Box {
            HeaderCapsule(t("更多"), highlighted = moreOpen) { onMore(true) }
            DropdownMenu(moreOpen, { onMore(false) }) {
                DropdownMenuItem(
                    text = { Text(t("记下这句")) },
                    leadingIcon = { Icon(Icons.bookmarkOutline, null, modifier = Modifier.size(16.dp)) },
                    onClick = { onMore(false); onMark() },
                )
                DropdownMenuItem(
                    text = { Text(t("纠正读音")) },
                    leadingIcon = { Icon(Icons.waveform, null, modifier = Modifier.size(16.dp)) },
                    onClick = { onMore(false); onFix() },
                )
            }
        }
        Spacer(Modifier.size(6.dp))
        HeaderCapsule(sleepBadge(player) ?: t("睡眠定时"), highlighted = sleepOn, icon = if (sleepOn) Icons.sleepFill else Icons.sleep, showLabel = sleepOn, onClick = onSleep)
    }
}

@Composable
private fun HeaderCapsule(label: String, highlighted: Boolean, icon: androidx.compose.ui.graphics.vector.ImageVector? = null, showLabel: Boolean = false, onClick: () -> Unit) {
    val ink = if (highlighted) YY.tint.accent else YY.c.t2
    Row(
        Modifier
            .heightIn(min = 32.dp)
            .clip(CircleShape)
            .background(if (highlighted) YY.tint.accentSoft else YY.c.inset)
            .clickable(onClickLabel = label, onClick = onClick)
            .padding(horizontal = if (showLabel) 10.dp else 8.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        if (icon != null) Icon(icon, null, tint = ink, modifier = Modifier.size(15.dp))
        else Icon(Icons.more, null, tint = ink, modifier = Modifier.size(16.dp))
        if (showLabel) Text(label, fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = ink, style = YY.tnum, maxLines = 1)
    }
}

/** Same subpage as iOS/macOS: the chip row moved off the main listen panel. */
@Composable
private fun SleepPage(model: ReaderModel, player: Player, back: () -> Unit) {
    val s = player.sleep
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        SubpageHeader(t("睡眠定时"), back, trailing = if (s != Player.Sleep.Off) t("关闭") else null) {
            player.sleep = Player.Sleep.Off
            back()
        }
        Column(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(YY.c.inset),
        ) {
            SleepRow(t("本句读完"), null, s == Player.Sleep.Sentence) {
                player.sleep = if (s == Player.Sleep.Sentence) Player.Sleep.Off else Player.Sleep.Sentence
                back()
            }
            SleepDivider()
            SleepRow(t("本章结束"), t("约 %lld 分钟", minutesLeft(model, player)), s == Player.Sleep.Chapter) {
                player.sleep = if (s == Player.Sleep.Chapter) Player.Sleep.Off else Player.Sleep.Chapter
                back()
            }
            for (m in listOf(15, 30, 60)) {
                val on = s is Player.Sleep.Until && s.minutes == m
                val left = (s as? Player.Sleep.Until)?.takeIf { it.minutes == m }?.let { minutesRemaining(it.at) }
                SleepDivider()
                SleepRow(t("%lld 分钟", m), left?.let { t("还剩 %lld 分", it) }, on) {
                    player.sleep = if (on) Player.Sleep.Off else Player.Sleep.Until(System.currentTimeMillis() + m * 60_000L, m)
                    back()
                }
            }
        }
        Text(t("停止时自动添加书签"), fontSize = 11.sp, color = YY.c.t3, modifier = Modifier.padding(horizontal = 4.dp))
    }
}

@Composable
private fun SubpageHeader(title: String, back: () -> Unit, trailing: String? = null, onTrailing: () -> Unit = {}) {
    Box(Modifier.fillMaxWidth().heightIn(min = 32.dp), contentAlignment = Alignment.Center) {
        Text(title, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Row(Modifier.clickable(onClickLabel = t("听书"), onClick = back).padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.chevronLeft, null, tint = YY.tint.accent, modifier = Modifier.size(16.dp))
                Text(t("听书"), fontSize = 13.sp, color = YY.tint.accent)
            }
            Spacer(Modifier.weight(1f))
            if (trailing != null) {
                Text(trailing, fontSize = 13.sp, color = YY.tint.accent, modifier = Modifier.clickable(onClick = onTrailing).padding(vertical = 6.dp))
            }
        }
    }
}

@Composable
private fun SleepDivider() {
    Box(Modifier.fillMaxWidth().padding(horizontal = 14.dp).height(0.5.dp).background(YY.c.line))
}

@Composable
private fun SleepRow(title: String, detail: String?, on: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 44.dp).background(if (on) YY.tint.accentSoft else Color.Transparent).clickable(onClick = onClick).padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Text(title, fontSize = 13.5.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Normal, color = if (on) YY.tint.accent else YY.c.t1)
        if (detail != null) Text(detail, fontSize = 11.5.sp, color = if (on) YY.tint.accent.copy(alpha = 0.8f) else YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Spacer(Modifier.weight(1f))
        if (on) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(14.dp))
    }
}

/** 纠正读音, inside the listen panel: a speech-only rule, then the current sentence is reread. */
@Composable
private fun PronouncePage(model: ReaderModel, player: Player, dismiss: () -> Unit, back: () -> Unit) {
    var find by remember { mutableStateOf("") }
    var replace by remember { mutableStateOf("") }
    var whole by remember { mutableStateOf(false) }
    val sentence = remember(player.spot, model.sentence) { model.currentSpokenLine() }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        SubpageHeader(t("纠正读音"), back)
        if (sentence.isNotEmpty()) {
            Text(sentence, fontSize = 14.sp, color = YY.c.t1, maxLines = 3, overflow = TextOverflow.Ellipsis, modifier = Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(12.dp)).padding(12.dp))
        }
        PronounceField(find, { find = it }, t("错词"))
        PronounceField(replace, { replace = it }, t("替换为"))
        Text(
            if (whole) t("全部书") else t("只用于这本书"),
            fontSize = 12.sp,
            color = if (whole) YY.tint.accent else YY.c.t2,
            modifier = Modifier.clickable { whole = !whole }.padding(horizontal = 4.dp, vertical = 2.dp),
        )
        YYButton(t("保存并重念"), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = find.isNotBlank() && replace.isNotBlank()) {
            if (model.applyPronunciationFix(find, replace, whole)) dismiss()
        }
    }
}

@Composable
private fun PronounceField(value: String, onValue: (String) -> Unit, hint: String) {
    BasicTextField(
        value,
        onValue,
        singleLine = true,
        textStyle = androidx.compose.ui.text.TextStyle(fontSize = 15.sp, color = YY.c.t1),
        cursorBrush = SolidColor(YY.tint.accent),
        modifier = Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(12.dp)).padding(horizontal = 12.dp, vertical = 12.dp),
        decorationBox = { inner ->
            Box {
                if (value.isEmpty()) Text(hint, fontSize = 15.sp, color = YY.c.t3)
                inner()
            }
        },
    )
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
