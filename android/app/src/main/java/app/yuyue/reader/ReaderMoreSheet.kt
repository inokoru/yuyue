package app.yuyue.reader

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.SyncDialogSystemBars
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.pressable
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.notes.NoteExportMenu
import app.yuyue.notes.NotedBook
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.Paper
import kotlin.math.roundToInt

/**
 * The reader's top-right `···` quick control bottom sheet:
 * 1. Screen brightness light-bar with a slim "Follow System" micro-switch header + 3 quick paper cards
 * 2. 2×2 core stage (left: compact charcoal toggle switches for Focus & Bilingual; right: AI Companion & Book Notes)
 * 3. Page turn animation strip + sound toggle
 * 4. Bottom 3-card (4-card for TXT) bookstore tool row (Replace Rules, Export Notes, Book Info, Encoding)
 */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun ReaderMoreSheet(
    model: ReaderModel,
    openNotes: () -> Unit,
    dismiss: () -> Unit,
) {
    val dark = YY.dark
    val eink = EInk.on
    val resolved = PaperAppearance.resolve(model.settings)
    val format = model.detail?.summary?.format
    val isPdf = format == BookFormat.PDF
    val isTxt = format == BookFormat.TXT

    var followSystem by rememberPref(ReaderBrightness.FOLLOW_KEY, true)
    var readerBrightness by rememberPref(ReaderBrightness.VALUE_KEY, 0.6f)
    var eyeCare by rememberPref(ReaderBrightness.EYE_CARE_KEY, false)
    var pageTurnRaw by rememberPref(PageTurnPrefs.KEY, PageTurn.SLIDE.key)
    var pageTurnSound by rememberPref(PageTurnPrefs.SOUND_KEY, false)
    var activeRuleCount by remember(model.bookID) { mutableIntStateOf(0) }
    var exportMenuOpen by remember { mutableStateOf(false) }

    LaunchedEffect(model.bookID, isPdf) {
        if (!isPdf) {
            activeRuleCount = withContext(Dispatchers.IO) {
                runCatching {
                    val bookRules = model.core.replaceRules(model.bookID).count { it.enabled }
                    val sharedRules = model.core.replaceRules(null).count { it.enabled }
                    bookRules + sharedRules
                }.getOrDefault(0)
            }
        }
    }

    // Palette tuned to warm paper-and-ink clarity matching the finalized prototype.
    val sheetBg = if (eink) YY.c.bg else if (dark) Color(0xFF1C1D20) else Color(0xFFF4F1EA)
    val cardBg = if (eink) YY.c.bg else if (dark) Color(0xFF272930) else Color(0xFFFFFFFF)
    val insetBg = if (eink) YY.c.bg else if (dark) Color(0xFF2F3137) else Color(0xFFECE8DF)
    val recessedBg = if (eink) YY.c.bg else if (dark) Color(0xFF212227) else Color(0xFFE9E4DB)
    val lightTrackBg = if (eink) YY.c.inset else if (dark) Color(0xFF2C2E34) else Color(0xFFE8E3D8)
    val lightFillBg = if (eink) YY.c.card else if (dark) Color(0xFF3A3D45) else Color(0xFFFFFFFF)
    val borderColor = if (eink) YY.c.t1 else if (dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.075f)
    val ink = if (eink) YY.c.t1 else if (dark) Color(0xFFEDEDEE) else Color(0xFF262523)
    val ink2 = if (eink) YY.c.t2 else if (dark) Color(0xFF9A9895) else Color(0xFF736E67)
    val switchOnTrack = if (eink) YY.c.t1 else if (dark) Color(0xFFE6E4DF) else Color(0xFF3B3936)
    val switchOnKnob = if (eink) YY.c.bg else if (dark) Color(0xFF1C1D20) else Color(0xFFFFFFFF)
    val switchOffTrack = if (eink) YY.c.line else if (dark) Color(0xFF363840) else Color(0xFFDDD8CE)
    val iconActiveBg = if (eink) YY.c.inset else if (dark) Color(0xFF343740) else Color(0xFFF0ECE4)
    val companionIconBg = if (eink) YY.c.inset else if (dark) Color(0xFF303137) else Color(0xFFF2EEE8)
    val companionIconFg = if (eink) YY.c.t1 else if (dark) Color(0xFFD1C4B2) else Color(0xFF6B5A46)
    val notesNumFg = if (eink) YY.c.t1 else if (dark) Color(0xFFE2E0DA) else Color(0xFF2F3339)

    ModalBottomSheet(
        onDismissRequest = dismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = sheetBg,
        scrimColor = if (eink) Color.Black.copy(alpha = 0.15f) else Color.Black.copy(alpha = 0.08f),
        shape = RoundedCornerShape(topStart = 26.dp, topEnd = 26.dp),
        dragHandle = { YYSheetDragHandle() },
    ) {
        SyncDialogSystemBars()
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .navigationBarsPadding()
                .padding(horizontal = 16.dp)
                .padding(bottom = 18.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            // 1. Brightness micro-header + full-width luminous light-bar + 3 paper cards
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    Row(
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Text(
                            t("屏幕亮度"),
                            fontSize = 11.sp,
                            fontWeight = FontWeight.Medium,
                            color = ink2,
                        )
                        Row(
                            modifier = Modifier
                                .clip(RoundedCornerShape(10.dp))
                                .clickable(role = Role.Switch) { followSystem = !followSystem }
                                .padding(vertical = 2.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                        ) {
                            Text(
                                t("跟随系统"),
                                fontSize = 11.sp,
                                fontWeight = FontWeight.Medium,
                                color = if (followSystem) ink else ink2,
                            )
                            MoreMiniSwitch(
                                checked = followSystem,
                                onTrack = switchOnTrack,
                                onKnob = switchOnKnob,
                                offTrack = switchOffTrack,
                            )
                        }
                    }

                    // Full-width 48dp horizontal touch-bar (drag or tap anywhere; zero conflict with sheet swipe)
                    val displayFraction = (if (followSystem) 0.78f else readerBrightness).coerceIn(0.05f, 1f)
                    val pct = (readerBrightness.coerceIn(0.05f, 1f) * 100f).roundToInt()
                    BoxWithConstraints(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(48.dp)
                            .clip(RoundedCornerShape(16.dp))
                            .background(lightTrackBg)
                            .border(if (eink) 1.dp else 0.5.dp, borderColor, RoundedCornerShape(16.dp))
                            .semantics {
                                contentDescription = t("屏幕亮度")
                                stateDescription = if (followSystem) t("跟随系统") else "$pct%"
                            }
                            .pointerInput(Unit) {
                                detectTapGestures { offset ->
                                    val ratio = (offset.x / size.width.coerceAtLeast(1)).coerceIn(0f, 1f)
                                    followSystem = false
                                    readerBrightness = (0.05f + ratio * 0.95f).coerceIn(0.05f, 1f)
                                }
                            }
                            .pointerInput(Unit) {
                                detectHorizontalDragGestures { change, _ ->
                                    change.consume()
                                    val ratio = (change.position.x / size.width.coerceAtLeast(1)).coerceIn(0f, 1f)
                                    followSystem = false
                                    readerBrightness = (0.05f + ratio * 0.95f).coerceIn(0.05f, 1f)
                                }
                            },
                    ) {
                        val innerWidth = (maxWidth - 4.dp).coerceAtLeast(0.dp)
                        Box(
                            modifier = Modifier
                                .padding(2.dp)
                                .fillMaxHeight()
                                .width((innerWidth * displayFraction).coerceAtLeast(20.dp))
                                .alpha(if (followSystem) 0.58f else 0.96f)
                                .shadow(if (eink) 0.dp else 1.dp, RoundedCornerShape(13.dp))
                                .clip(RoundedCornerShape(13.dp))
                                .background(lightFillBg),
                        )
                        Row(
                            modifier = Modifier.align(Alignment.CenterStart).padding(horizontal = 14.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            Icon(Icons.appearance, null, tint = ink2, modifier = Modifier.size(16.dp))
                            Text(
                                if (followSystem) t("跟随系统") else "$pct%",
                                fontSize = 12.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = ink,
                                style = YY.tnum,
                            )
                        }
                    }
                }

                // Day / Night Polarity Toggle (preserves the user's chosen day/night paper in Aa, hidden in E-Ink)
                if (!eink) {
                    val isDark = resolved.isDark
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        val items = listOf(
                            Triple(t("日间"), Icons.appearance, false),
                            Triple(t("夜间"), Icons.moon, true),
                        )
                        items.forEach { (label, icon, wantDark) ->
                            val selected = isDark == wantDark
                            val shape = RoundedCornerShape(14.dp)
                            Row(
                                modifier = Modifier
                                    .weight(1f)
                                    .height(42.dp)
                                    .then(
                                        if (selected) Modifier.shadow(4.dp, shape, ambientColor = Color.Black.copy(alpha = 0.08f), spotColor = Color.Black.copy(alpha = 0.10f))
                                        else Modifier
                                    )
                                    .clip(shape)
                                    .background(if (selected) cardBg else recessedBg)
                                    .border(0.5.dp, if (selected) borderColor else Color.Transparent, shape)
                                    .semantics { this.selected = selected }
                                    .pressable(scale = 0.96f, label = label) {
                                        if (isDark != wantDark) {
                                            PaperAppearance.togglePolarity(model.settings) { model.update(it) }
                                        }
                                    },
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(7.dp, Alignment.CenterHorizontally),
                            ) {
                                Icon(
                                    icon,
                                    contentDescription = null,
                                    tint = if (selected) ink else ink2,
                                    modifier = Modifier.size(16.dp),
                                )
                                Text(
                                    label,
                                    fontSize = 12.5.sp,
                                    fontWeight = FontWeight.SemiBold,
                                    color = if (selected) ink else ink2,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }

                    // Independent Eye-Care Warmth toggle (overlays a subtle warm blue-light filter on any paper)
                    val eyeShape = RoundedCornerShape(14.dp)
                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(44.dp)
                            .clip(eyeShape)
                            .background(cardBg)
                            .border(if (eink) 1.dp else 0.5.dp, borderColor, eyeShape)
                            .pressable(scale = 0.98f, role = Role.Switch, label = t("护眼柔光")) {
                                eyeCare = !eyeCare
                            }
                            .padding(horizontal = 12.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                    ) {
                        Icon(
                            Icons.eye,
                            contentDescription = null,
                            tint = if (eyeCare) ink else ink2,
                            modifier = Modifier.size(16.dp),
                        )
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                            Text(
                                t("护眼柔光"),
                                fontSize = 12.5.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = ink,
                                maxLines = 1,
                            )
                            Text(
                                t("暖色滤蓝光"),
                                fontSize = 10.5.sp,
                                color = ink2,
                                maxLines = 1,
                            )
                        }
                        MoreMiniSwitch(
                            checked = eyeCare,
                            onTrack = switchOnTrack,
                            onKnob = switchOnKnob,
                            offTrack = switchOffTrack,
                        )
                    }
                }
            }

            // 2. Core Switches (Focus Reading + Dialogue Bubbles) & Action Entries (AI Companion + Book Notes)
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                // Row 1: Reading Mode Switches
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    val focusShape = RoundedCornerShape(16.dp)
                    Row(
                        modifier = Modifier
                            .weight(1f)
                            .height(54.dp)
                            .clip(focusShape)
                            .background(cardBg)
                            .border(if (eink) 1.dp else 0.5.dp, borderColor, focusShape)
                            .combinedClickable(
                                onClick = {
                                    model.chromeShown = false
                                    model.immersive = !model.immersive
                                },
                                onLongClick = {
                                    dismiss()
                                    model.immersiveShown = true
                                },
                            )
                            .padding(horizontal = 11.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        Box(
                            Modifier
                                .size(28.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (model.immersive) iconActiveBg else insetBg),
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(Icons.viewfinder, null, tint = if (model.immersive) ink else ink2, modifier = Modifier.size(15.dp))
                        }
                        Column(Modifier.weight(1f)) {
                            Text(t("沉浸阅读"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                if (model.immersive) t("已开启") else t("无打扰模式"),
                                fontSize = 10.5.sp,
                                color = ink2,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                        MoreMiniSwitch(
                            checked = model.immersive,
                            onTrack = switchOnTrack,
                            onKnob = switchOnKnob,
                            offTrack = switchOffTrack,
                        )
                    }

                    if (model.hasDialogue) {
                        val bubbleShape = RoundedCornerShape(16.dp)
                        Row(
                            modifier = Modifier
                                .weight(1f)
                                .height(54.dp)
                                .clip(bubbleShape)
                                .background(cardBg)
                                .border(if (eink) 1.dp else 0.5.dp, borderColor, bubbleShape)
                                .clickable(role = Role.Switch) {
                                    model.bubbles = !model.bubbles
                                }
                                .padding(horizontal = 11.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            Box(
                                Modifier
                                    .size(28.dp)
                                    .clip(RoundedCornerShape(8.dp))
                                    .background(if (model.bubbles) iconActiveBg else insetBg),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.bubbles, null, tint = if (model.bubbles) ink else ink2, modifier = Modifier.size(15.dp))
                            }
                            Column(Modifier.weight(1f)) {
                                Text(t("对话气泡"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(
                                    if (model.bubbles) t("已开启") else t("聊天气泡"),
                                    fontSize = 10.5.sp,
                                    color = ink2,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                            MoreMiniSwitch(
                                checked = model.bubbles,
                                onTrack = switchOnTrack,
                                onKnob = switchOnKnob,
                                offTrack = switchOffTrack,
                            )
                        }
                    }
                }

                // Row 2: Book Entries (AI Companion + Book Notes)
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    if (!isPdf) {
                        val compShape = RoundedCornerShape(16.dp)
                        Row(
                            modifier = Modifier
                                .weight(1f)
                                .height(54.dp)
                                .clip(compShape)
                                .background(cardBg)
                                .border(if (eink) 1.dp else 0.5.dp, borderColor, compShape)
                                .pressable(scale = 0.97f, label = t("AI 书伴")) {
                                    dismiss()
                                    model.chromeShown = false
                                    model.companionShown = true
                                }
                                .padding(horizontal = 11.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(9.dp),
                        ) {
                            Box(
                                Modifier
                                    .size(32.dp)
                                    .clip(RoundedCornerShape(11.dp))
                                    .background(companionIconBg),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.sparkle, null, tint = companionIconFg, modifier = Modifier.size(16.dp))
                            }
                            Column(Modifier.weight(1f)) {
                                Text(t("AI 书伴"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(t("总结 · 追问"), fontSize = 10.5.sp, color = ink2, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                        }
                    }

                    val notesShape = RoundedCornerShape(16.dp)
                    Row(
                        modifier = Modifier
                            .weight(1f)
                            .height(54.dp)
                            .clip(notesShape)
                            .background(cardBg)
                            .border(if (eink) 1.dp else 0.5.dp, borderColor, notesShape)
                            .pressable(scale = 0.97f, label = t("本书笔记")) {
                                dismiss()
                                openNotes()
                            }
                            .padding(horizontal = 13.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.SpaceBetween,
                    ) {
                        Column(Modifier.weight(1f).padding(end = 6.dp)) {
                            Text(t("本书笔记"), fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(t("划线与想法"), fontSize = 10.5.sp, color = ink2, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                        Text(
                            "${model.marks.size}",
                            fontFamily = YY.serif,
                            fontSize = 19.sp,
                            fontWeight = FontWeight.Bold,
                            color = notesNumFg,
                            style = YY.tnum,
                        )
                    }
                }
            }

            // 3. Translation 3-Mode Strip (原文 / 对照 / 仅译文)
            if (!isPdf && format != BookFormat.PODCAST) {
                val trStripShape = RoundedCornerShape(15.dp)
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(44.dp)
                        .clip(trStripShape)
                        .background(cardBg)
                        .border(if (eink) 1.dp else 0.5.dp, borderColor, trStripShape)
                        .padding(horizontal = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        t("译文"),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = ink2,
                        modifier = Modifier.padding(start = 4.dp),
                    )
                    Row(
                        modifier = Modifier
                            .weight(1f)
                            .height(34.dp)
                            .clip(RoundedCornerShape(11.dp))
                            .background(insetBg)
                            .padding(3.dp),
                        horizontalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        val modes = listOf(
                            TranslateMode.OFF to t("原文"),
                            TranslateMode.BILINGUAL to t("对照"),
                            TranslateMode.ONLY to t("仅译文"),
                        )
                        for ((mode, label) in modes) {
                            val on = model.translateMode == mode
                            val segShape = RoundedCornerShape(8.dp)
                            Box(
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxHeight()
                                    .then(if (on && !eink) Modifier.shadow(1.5.dp, segShape) else Modifier)
                                    .clip(segShape)
                                    .background(if (on) cardBg else Color.Transparent)
                                    .clickable { model.setTranslateMode(mode) },
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    label,
                                    fontSize = 12.sp,
                                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium,
                                    color = if (on) ink else ink2,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                }
            }

            // 4. Page Turn Strip + Sound Toggle (paginated & non-E-Ink)
            if (model.settings.mode == LayoutMode.PAGINATED && !eink) {
                val currentTurn = PageTurn.of(pageTurnRaw)
                val stripShape = RoundedCornerShape(15.dp)
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(46.dp)
                        .clip(stripShape)
                        .background(cardBg)
                        .border(0.5.dp, borderColor, stripShape)
                        .padding(horizontal = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(
                        t("翻页"),
                        fontSize = 12.sp,
                        fontWeight = FontWeight.Medium,
                        color = ink2,
                        modifier = Modifier.padding(start = 4.dp),
                    )
                    Row(
                        modifier = Modifier
                            .weight(1f)
                            .height(34.dp)
                            .clip(RoundedCornerShape(11.dp))
                            .background(insetBg)
                            .padding(3.dp),
                        horizontalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        val turns = listOf(
                            PageTurn.SLIDE to t("滑动"),
                            PageTurn.COVER to t("覆盖"),
                            PageTurn.NONE to t("无动画"),
                        )
                        for ((pt, label) in turns) {
                            val on = currentTurn == pt
                            val segShape = RoundedCornerShape(8.dp)
                            Box(
                                modifier = Modifier
                                    .weight(1f)
                                    .fillMaxHeight()
                                    .then(if (on) Modifier.shadow(1.5.dp, segShape) else Modifier)
                                    .clip(segShape)
                                    .background(if (on) cardBg else Color.Transparent)
                                    .clickable { pageTurnRaw = pt.key },
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(
                                    label,
                                    fontSize = 12.sp,
                                    fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium,
                                    color = if (on) ink else ink2,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                        }
                    }
                    val soundShape = RoundedCornerShape(11.dp)
                    Box(
                        modifier = Modifier
                            .size(width = 40.dp, height = 32.dp)
                            .clip(soundShape)
                            .background(if (pageTurnSound) YY.tint.accentSoft else insetBg)
                            .border(
                                0.5.dp,
                                if (pageTurnSound) YY.tint.accent.copy(alpha = 0.35f) else Color.Transparent,
                                soundShape,
                            )
                            .pressable(scale = 0.92f, label = t("翻页音效")) {
                                pageTurnSound = !pageTurnSound
                            },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(
                            if (pageTurnSound) Icons.speakerWave else Icons.speakerSlash,
                            t("翻页音效"),
                            tint = if (pageTurnSound) YY.tint.accent else ink2,
                            modifier = Modifier.size(16.dp),
                        )
                    }
                }
            }

            // 4. Bottom 3-Card (4-Card for TXT) Bookstore Tool Footer
            val pctRead = (model.bookFraction * 100.0).roundToInt().coerceIn(0, 100)
            val formatName = when (format) {
                BookFormat.EPUB -> "EPUB"
                BookFormat.TXT -> "TXT"
                BookFormat.PDF -> "PDF"
                else -> "BOOK"
            }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                if (!isPdf) {
                    FooterToolCard(
                        icon = Icons.swapHoriz,
                        title = t("替换规则"),
                        subtitle = if (activeRuleCount > 0) t("%lld 条生效", activeRuleCount.toLong()) else t("去广告 · 读音"),
                        accent = false,
                        cardBg = cardBg,
                        insetBg = insetBg,
                        borderColor = borderColor,
                        ink = ink,
                        ink2 = ink2,
                        eink = eink,
                        modifier = Modifier.weight(1f),
                    ) {
                        dismiss()
                        model.rulesShown = true
                    }
                }

                // Export Notes card with anchored NoteExportMenu
                Box(Modifier.weight(1f)) {
                    FooterToolCard(
                        icon = Icons.share,
                        title = t("导出笔记"),
                        subtitle = t("MD · 分享"),
                        accent = true,
                        cardBg = cardBg,
                        insetBg = insetBg,
                        borderColor = borderColor,
                        ink = ink,
                        ink2 = ink2,
                        eink = eink,
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        if (model.marks.isEmpty()) {
                            dismiss()
                            model.show(Library.Toast(Library.Toast.Icon.Highlight, t("这本书还没有划线和想法"), t("选中文字点“划线”，就会出现在这里")))
                        } else {
                            exportMenuOpen = true
                        }
                    }
                    val summary = model.detail?.summary
                    val title = summary?.title ?: t("笔记")
                    val currentNoted: () -> List<NotedBook> = {
                        NotedBook.load(model.library, only = setOf(model.bookID))
                    }
                    NoteExportMenu(
                        library = model.library,
                        open = exportMenuOpen,
                        dismiss = { exportMenuOpen = false },
                        markdown = { NotedBook.markdown(currentNoted(), t("《%@》的笔记", title)) },
                        name = t("%@ 笔记.md", title),
                        books = currentNoted,
                        book = summary,
                    )
                }

                FooterToolCard(
                    icon = Icons.info,
                    title = t("书的信息"),
                    subtitle = "$formatName · $pctRead%",
                    accent = false,
                    cardBg = cardBg,
                    insetBg = insetBg,
                    borderColor = borderColor,
                    ink = ink,
                    ink2 = ink2,
                    eink = eink,
                    modifier = Modifier.weight(1f),
                ) {
                    dismiss()
                    model.infoShown = true
                }

                if (isTxt) {
                    FooterToolCard(
                        icon = Icons.docSearch,
                        title = t("编码目录"),
                        subtitle = t("乱码 · 章节"),
                        accent = false,
                        cardBg = cardBg,
                        insetBg = insetBg,
                        borderColor = borderColor,
                        ink = ink,
                        ink2 = ink2,
                        eink = eink,
                        modifier = Modifier.weight(1f),
                    ) {
                        dismiss()
                        model.txtShown = true
                    }
                }
            }
        }
    }
}

@Composable
private fun MoreMiniSwitch(
    checked: Boolean,
    onTrack: Color,
    onKnob: Color,
    offTrack: Color,
) {
    val track by animateColorAsState(if (checked) onTrack else offTrack, YYMotion.quick(), label = "miniTrack")
    val knobOffset by animateDpAsState(if (checked) 14.dp else 0.dp, YYMotion.quick(), label = "miniKnob")
    Box(
        modifier = Modifier
            .size(width = 34.dp, height = 20.dp)
            .clip(CircleShape)
            .background(track)
            .padding(2.dp),
        contentAlignment = Alignment.CenterStart,
    ) {
        Box(
            modifier = Modifier
                .offset(x = knobOffset)
                .size(16.dp)
                .shadow(1.dp, CircleShape)
                .clip(CircleShape)
                .background(if (checked) onKnob else Color.White),
        )
    }
}

@Composable
private fun FooterToolCard(
    icon: ImageVector,
    title: String,
    subtitle: String,
    accent: Boolean,
    cardBg: Color,
    insetBg: Color,
    borderColor: Color,
    ink: Color,
    ink2: Color,
    eink: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(16.dp)
    Column(
        modifier = modifier
            .heightIn(min = 68.dp)
            .clip(shape)
            .background(cardBg)
            .border(if (eink) 1.dp else 0.5.dp, borderColor, shape)
            .pressable(scale = 0.96f, label = title, onClick = onClick)
            .padding(horizontal = 6.dp, vertical = 10.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(4.dp, Alignment.CenterVertically),
    ) {
        Box(
            modifier = Modifier
                .size(28.dp)
                .clip(CircleShape)
                .background(if (accent && !eink) YY.tint.accentSoft else insetBg),
            contentAlignment = Alignment.Center,
        ) {
            Icon(
                icon,
                null,
                tint = if (accent && !eink) YY.tint.accent else ink,
                modifier = Modifier.size(14.dp),
            )
        }
        Text(
            title,
            fontSize = 11.sp,
            fontWeight = FontWeight.SemiBold,
            color = ink,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
        Text(
            subtitle,
            fontSize = 10.sp,
            color = ink2,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}
