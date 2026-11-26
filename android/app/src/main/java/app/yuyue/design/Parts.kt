package app.yuyue.design

import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
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
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.MutableIntState
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Constraints
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.L10n
import app.yuyue.t
import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.roundToInt

private val LocalFieldLabelWidth = compositionLocalOf<MutableIntState?> { null }
private val LocalInYYGroup = compositionLocalOf { false }

/** A page of these pieces on its grey ground. */
@Composable
fun YYPage(modifier: Modifier = Modifier, contentPadding: PaddingValues = PaddingValues(0.dp), scroll: androidx.compose.foundation.ScrollState = rememberScrollState(), content: @Composable ColumnScope.() -> Unit) {
    Column(
        modifier
            .fillMaxSize()
            .background(YY.c.bg)
            .verticalScroll(scroll)
            .padding(contentPadding)
            .padding(horizontal = 16.dp)
            .padding(bottom = YY.s(20) + app.yuyue.LocalTabBarHeight.current),
        content = content,
    )
}

/** A heading over a card, a grey note on its right. */
@Composable
fun YYLabel(title: String, trailing: String? = null, top: Dp = YY.s(18)) {
    Row(
        Modifier.fillMaxWidth().padding(top = top, bottom = YY.s(8), start = YY.s(2), end = YY.s(2)),
    ) {
        Text(title, style = YY.font(12.5f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.alignByBaseline())
        Spacer(Modifier.weight(1f))
        if (trailing != null) Text(trailing, style = YY.font(10.5f, tnum = true), color = YY.c.t3, modifier = Modifier.alignByBaseline())
    }
}

/** A grey line under a card. */
@Composable
fun YYNote(text: String, color: Color = YY.c.t3) {
    Text(
        text,
        style = YY.font(10f).copy(lineHeight = YY.sp(16f)),
        color = color,
        modifier = Modifier.padding(top = YY.s(6), start = YY.s(4), end = YY.s(4)),
    )
}

/** `.grp`: a white card of rows, a hairline between them. Pass rows as separate children. */
@Composable
fun YYGroup(modifier: Modifier = Modifier, rows: List<@Composable () -> Unit>) {
    val labelWidthPx = remember(L10n.chinese, rows.size) { mutableIntStateOf(0) }
    CompositionLocalProvider(
        LocalFieldLabelWidth provides labelWidthPx,
        LocalInYYGroup provides true,
    ) {
        Column(
            modifier
                .fillMaxWidth()
                // A row that comes or goes (a switch revealing more, an error line) opens the card smoothly.
                .animateContentSize(YYMotion.layout())
                .clip(RoundedCornerShape(YY.s(14)))
                .background(YY.c.card)
                .border(0.5.dp, if (YY.dark) Color.White.copy(alpha = 0.06f) else YY.c.line, RoundedCornerShape(YY.s(14)))
                .padding(horizontal = YY.s(12)),
        ) {
            rows.forEachIndexed { i, row ->
                if (i > 0) Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                row()
            }
        }
    }
}

/** A group from a builder: `YYGroup { row { … }; row { … } }`. */
@Composable
fun YYGroup(modifier: Modifier = Modifier, build: GroupScope.() -> Unit) {
    val scope = GroupScope().apply(build)
    YYGroup(modifier, scope.rows)
}

class GroupScope {
    val rows = mutableListOf<@Composable () -> Unit>()
    fun row(content: @Composable () -> Unit) {
        rows += content
    }
}

/** A white card on its own. */
fun Modifier.yyCard(dark: Boolean, card: Color, line: Color, radius: Dp = YY.s(16), eink: Boolean = false): Modifier =
    this
        .shadow(if (dark || eink) 0.dp else 2.dp, RoundedCornerShape(radius), ambientColor = Color(0x14332E29), spotColor = Color(0x14332E29))
        .clip(RoundedCornerShape(radius))
        .background(card)
        .border(0.5.dp, if (dark) Color.White.copy(alpha = 0.06f) else line, RoundedCornerShape(radius))

@Composable
fun Modifier.yyCard(radius: Dp = YY.s(16)): Modifier {
    val eink = if (Prefs.isReady) rememberPref("eink.on", false).value else false
    return yyCard(YY.dark, YY.c.card, YY.c.line, radius, eink)
}

/** `.grp .row`: an icon, what it is, and a grey value with a chevron. */
@Composable
fun YYRow(
    title: String,
    icon: ImageVector? = null,
    value: String? = null,
    chevron: Boolean = false,
    titleColor: Color = YY.c.t1,
    trailing: @Composable RowScope.() -> Unit = {},
    onClick: (() -> Unit)? = null,
) {
    val inGroup = LocalInYYGroup.current
    val press = remember { MutableInteractionSource() }
    val pressed by press.collectIsPressedAsState()
    val pressBg by androidx.compose.animation.animateColorAsState(
        targetValue = if (onClick != null && pressed) YY.c.inset else Color.Transparent,
        animationSpec = YYMotion.quick(),
        label = "yyRowPress",
    )
    val bleed = YY.s(12)
    val standaloneRadius = YY.s(10)
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = YY.s(38))
            .drawBehind {
                if (pressBg.alpha > 0.005f) {
                    if (inGroup) {
                        val bleedPx = bleed.toPx()
                        drawRect(
                            color = pressBg,
                            topLeft = androidx.compose.ui.geometry.Offset(-bleedPx, 0f),
                            size = Size(size.width + bleedPx * 2f, size.height),
                        )
                    } else {
                        val r = standaloneRadius.toPx()
                        drawRoundRect(color = pressBg, cornerRadius = CornerRadius(r, r))
                    }
                }
            }
            .then(
                if (onClick != null) {
                    Modifier.clickable(
                        interactionSource = press,
                        indication = null,
                        role = androidx.compose.ui.semantics.Role.Button,
                        onClick = onClick,
                    )
                } else {
                    Modifier
                },
            ),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        if (icon != null) Icon(icon, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(15)))
        if (value.isNullOrEmpty()) {
            Text(title, style = YY.font(11.5f), color = titleColor, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        } else {
            // The name stays whole when it fits; the grey value gives way, without crushing trailing controls.
            val gap = YY.s(8)
            val middle = value.contains("://") || value.contains("/") || value.contains("\\")
            Layout(
                content = {
                    Text(title, style = YY.font(11.5f), color = titleColor, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(value, style = YY.font(10.5f, tnum = true), color = YY.c.t3, maxLines = 1, overflow = if (middle) TextOverflow.MiddleEllipsis else TextOverflow.Ellipsis, textAlign = androidx.compose.ui.text.style.TextAlign.End)
                },
                modifier = Modifier.weight(1f),
            ) { measurables, constraints ->
                val maxW = constraints.maxWidth
                val gapPx = gap.roundToPx()
                val titleMax = (maxW * 0.72f).toInt().coerceAtLeast(0)
                val tPlace = measurables[0].measure(Constraints(0, titleMax, 0, constraints.maxHeight))
                val vWidth = (maxW - tPlace.width - gapPx).coerceAtLeast(0)
                val vPlace = measurables[1].measure(Constraints(vWidth, vWidth, 0, constraints.maxHeight))
                val h = max(tPlace.height, vPlace.height)
                layout(maxW, h) {
                    tPlace.placeRelative(0, (h - tPlace.height) / 2)
                    vPlace.placeRelative(maxW - vPlace.width, (h - vPlace.height) / 2)
                }
            }
        }
        trailing()
        if (chevron) Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
    }
}

/** A field with its name in a shared column on the left so every field in a [YYGroup] lines up. */
@Composable
fun YYField(label: String, labelWidth: Float = 58f, field: @Composable () -> Unit) {
    val density = LocalDensity.current
    val groupWidth = LocalFieldLabelWidth.current
    val minDp = maxOf(YY.s(labelWidth), with(density) { (groupWidth?.intValue ?: 0).toDp() }).coerceAtMost(148.dp)
    Row(Modifier.fillMaxWidth().heightIn(min = YY.s(38)), verticalAlignment = Alignment.CenterVertically) {
        Text(
            label, style = YY.font(11f), color = YY.c.t2, maxLines = 1, softWrap = false, overflow = TextOverflow.Ellipsis,
            onTextLayout = { r ->
                if (groupWidth != null && r.lineCount > 0) {
                    val w = ceil(r.getLineRight(0) - r.getLineLeft(0)).toInt()
                    if (w > groupWidth.intValue) groupWidth.intValue = w
                }
            },
            modifier = Modifier.widthIn(min = minDp, max = 148.dp),
        )
        Spacer(Modifier.width(YY.s(8)))
        Box(Modifier.weight(1f)) { field() }
    }
}

/** Choices on a capsule inset track, with a smoothly sliding white pill behind the chosen one. */
@Composable
fun <T> YYSegments(items: List<Pair<T, String>>, selection: T, onSelect: (T) -> Unit, small: Boolean = false, modifier: Modifier = Modifier) {
    if (items.isEmpty()) return
    val selectedIndex = items.indexOfFirst { it.first == selection }
    val thumbPos by androidx.compose.animation.core.animateFloatAsState(
        targetValue = selectedIndex.coerceAtLeast(0).toFloat(),
        animationSpec = YYMotion.quick(),
        label = "segmentThumb",
    )
    val pad = 3.dp
    val minSegHeight = YY.s(if (small) 22 else 26)
    val hPad = if (small) YY.s(8) else YY.s(6)
    val vPad = YY.s(2)
    val fontSize = if (small) 10f else 11f
    val borderColor = YY.c.line

    Layout(
        modifier = modifier
            .clip(CircleShape)
            .background(YY.c.inset, CircleShape)
            .border(0.5.dp, borderColor, CircleShape),
        content = {
            // Child 0: sliding active capsule thumb
            Box(
                Modifier
                    .background(YY.c.card, CircleShape)
                    .border(0.5.dp, borderColor.copy(alpha = 0.7f), CircleShape),
            )
            // Children 1..N: equal-width segment tabs
            for ((value, title) in items) {
                val on = value == selection
                val ink by androidx.compose.animation.animateColorAsState(if (on) YY.c.t1 else YY.c.t2, YYMotion.quick(), label = "segmentInk")
                val interaction = remember { MutableInteractionSource() }
                Box(
                    Modifier
                        .clip(CircleShape)
                        .selectable(
                            selected = on,
                            interactionSource = interaction,
                            indication = null,
                            role = androidx.compose.ui.semantics.Role.Tab,
                        ) { onSelect(value) }
                        .padding(horizontal = hPad, vertical = vPad),
                    contentAlignment = Alignment.Center,
                ) {
                    androidx.compose.foundation.text.BasicText(
                        title,
                        style = YY.font(fontSize, if (on) FontWeight.SemiBold else FontWeight.Normal, tnum = true).copy(color = ink),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        autoSize = androidx.compose.foundation.text.TextAutoSize.StepBased(minFontSize = YY.sp(fontSize * 0.78f), maxFontSize = YY.sp(fontSize), stepSize = 0.5.sp),
                    )
                }
            }
        },
    ) { measurables, constraints ->
        val count = items.size
        val padPx = pad.roundToPx()
        val minSegHPx = minSegHeight.roundToPx()
        val tabMeasurables = measurables.drop(1)

        val totalW = if (constraints.hasFixedWidth) {
            constraints.maxWidth
        } else {
            val maxIntrinsic = tabMeasurables.maxOfOrNull { it.maxIntrinsicWidth(Constraints.Infinity) } ?: 0
            val naturalW = maxIntrinsic * count + padPx * 2
            naturalW.coerceIn(constraints.minWidth, constraints.maxWidth)
        }
        val innerW = (totalW - padPx * 2).coerceAtLeast(0)
        val segW = if (count > 0) innerW / count else 0
        val tabConstraints = Constraints(
            minWidth = segW,
            maxWidth = segW,
            minHeight = minSegHPx,
            maxHeight = if (constraints.hasBoundedHeight) (constraints.maxHeight - padPx * 2).coerceAtLeast(minSegHPx) else Constraints.Infinity,
        )
        val tabPlaceables = tabMeasurables.map { it.measure(tabConstraints) }
        val segH = max(minSegHPx, tabPlaceables.maxOfOrNull { it.height } ?: minSegHPx)
        val thumbPlaceable = measurables[0].measure(Constraints.fixed(segW, segH))
        val totalH = segH + padPx * 2

        layout(totalW, totalH) {
            if (selectedIndex >= 0 && segW > 0) {
                val thumbX = padPx + (thumbPos * segW).roundToInt()
                thumbPlaceable.placeRelative(thumbX, padPx)
            }
            tabPlaceables.forEachIndexed { i, p ->
                p.placeRelative(padPx + i * segW, padPx + (segH - p.height) / 2)
            }
        }
    }
}

enum class ButtonKind { Main, Raised, Soft, Plain, Danger }
enum class ButtonSize { Small, Regular, Large }

/** The app's pill buttons: `Main`, the page's one action, is a faint wash of the theme colour with
 *  a hairline and words in the colour; `Raised` and `Plain` the warm white pill with a hairline.
 *  No shadows. */
@Composable
fun YYButton(
    title: String,
    kind: ButtonKind = ButtonKind.Plain,
    size: ButtonSize = ButtonSize.Regular,
    wide: Boolean = false,
    enabled: Boolean = true,
    icon: ImageVector? = null,
    modifier: Modifier = Modifier,
    onClick: () -> Unit,
) {
    // Low on purpose: a tall pill looks heavy next to the rows around it.
    val height = when (size) { ButtonSize.Small -> 26f; ButtonSize.Regular -> 30f; ButtonSize.Large -> 38f }
    val font = when (size) { ButtonSize.Small -> 11f; ButtonSize.Regular -> 12f; ButtonSize.Large -> 13.5f }
    val pad = when (size) { ButtonSize.Small -> 11f; ButtonSize.Regular -> 14f; ButtonSize.Large -> 20f }
    val tint = YY.tint
    val fg = when (kind) {
        ButtonKind.Main -> tint.onBlack
        ButtonKind.Soft -> tint.accent
        ButtonKind.Danger -> YY.c.danger
        else -> YY.c.t1
    }
    val bg = when (kind) {
        ButtonKind.Main -> tint.black
        ButtonKind.Raised, ButtonKind.Plain -> YY.c.pill
        ButtonKind.Soft -> tint.accentSoft
        ButtonKind.Danger -> YY.c.danger.copy(alpha = 0.11f)
    }
    val line = when (kind) {
        ButtonKind.Main -> tint.edge
        ButtonKind.Raised, ButtonKind.Plain -> YY.c.pillLine
        else -> Color.Transparent
    }
    val source = remember { MutableInteractionSource() }
    val pressed by source.collectIsPressedAsState()
    Box(
        modifier
            .then(if (wide) Modifier.fillMaxWidth() else Modifier)
            .widthIn(min = 44.dp)
            .heightIn(min = 44.dp)
            .clickable(interactionSource = source, indication = null, enabled = enabled, role = androidx.compose.ui.semantics.Role.Button, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Row(
            Modifier
                .then(if (wide) Modifier.fillMaxWidth() else Modifier)
                .heightIn(min = YY.s(height))
                .then(if (enabled) Modifier.pressScale(source, 0.97f) else Modifier)
                .alpha(if (!enabled) 0.4f else if (pressed) 0.75f else 1f)
                .clip(CircleShape)
                .background(bg)
                .border(0.5.dp, line, CircleShape)
                .padding(horizontal = YY.s(pad), vertical = YY.s(4)),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(YY.s(5), Alignment.CenterHorizontally),
        ) {
            if (icon != null) Icon(icon, null, tint = fg, modifier = Modifier.size(YY.s(font + 2)))
            val weight = if (kind == ButtonKind.Main || kind == ButtonKind.Danger) FontWeight.SemiBold else FontWeight.Normal
            Text(title, style = YY.font(font, weight, tnum = true), color = fg, maxLines = 1, softWrap = false, overflow = TextOverflow.Ellipsis)
        }
    }
}

/** A small grey label. */
@Composable
fun YYTag(text: String, icon: ImageVector? = null) {
    Row(
        Modifier.clip(RoundedCornerShape(YY.s(5))).background(YY.c.inset).padding(horizontal = YY.s(5), vertical = YY.s(1.5f)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(3)),
    ) {
        if (icon != null) Icon(icon, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(10)))
        Text(text, style = YY.font(10f, FontWeight.Medium), color = YY.c.t2)
    }
}

/** Compact drag pill for modal sheets (Material 3's default DragHandle adds 48dp of vertical padding). */
@Composable
fun YYSheetDragHandle(top: Dp = 10.dp, bottom: Dp = 8.dp) {
    SyncDialogSystemBars()
    Box(
        Modifier.fillMaxWidth().padding(top = top, bottom = bottom),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier
                .size(width = 36.dp, height = 4.dp)
                .clip(CircleShape)
                .background(YY.c.t3.copy(alpha = 0.35f)),
        )
    }
}

/** A sheet's own title with × on the right. */
@Composable
fun YYSheetHeader(title: String, subtitle: String? = null, close: () -> Unit) {
    val gap = YY.s(8)
    Row(
        // No side padding of its own: the sheet's edge is the edge, so × sits right against it.
        Modifier.fillMaxWidth().padding(bottom = YY.s(4)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
    ) {
        Layout(
            content = {
                Text(title, style = YY.font(14f, FontWeight.SemiBold), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (subtitle != null) Text(subtitle, style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
            },
            modifier = Modifier.weight(1f),
        ) { measurables, constraints ->
            val maxW = constraints.maxWidth
            val gapPx = gap.roundToPx()
            if (measurables.size == 1) {
                val tPlace = measurables[0].measure(Constraints(0, maxW, 0, constraints.maxHeight))
                layout(maxW, tPlace.height) { tPlace.placeRelative(0, 0) }
            } else {
                val tIdeal = measurables[0].maxIntrinsicWidth(constraints.maxHeight)
                val sIdeal = measurables[1].maxIntrinsicWidth(constraints.maxHeight)
                val tMax: Int
                val sMax: Int
                if (tIdeal + gapPx + sIdeal <= maxW) {
                    tMax = tIdeal
                    sMax = sIdeal
                } else {
                    val avail = (maxW - gapPx).coerceAtLeast(0)
                    val cap = (avail * 0.38f).toInt()
                    when {
                        sIdeal <= cap -> { sMax = sIdeal; tMax = avail - sMax }
                        tIdeal <= cap -> { tMax = tIdeal; sMax = avail - tMax }
                        else -> { tMax = (avail * 0.65f).toInt(); sMax = avail - tMax }
                    }
                }
                val tPlace = measurables[0].measure(Constraints(0, tMax.coerceIn(0, maxW), 0, constraints.maxHeight))
                val sPlace = measurables[1].measure(Constraints(0, sMax.coerceIn(0, maxW), 0, constraints.maxHeight))
                val h = max(tPlace.height, sPlace.height)
                layout(maxW, h) {
                    tPlace.placeRelative(0, (h - tPlace.height) / 2)
                    sPlace.placeRelative(tPlace.width + gapPx, (h - sPlace.height) / 2)
                }
            }
        }
        Box(
            Modifier.size(44.dp).pressable(label = t("关上"), scale = 0.9f, onClick = close),
            contentAlignment = Alignment.CenterEnd,
        ) {
            Box(Modifier.size(YY.s(28)).clip(CircleShape).background(YY.c.inset), contentAlignment = Alignment.Center) {
                Icon(Icons.close, t("关上"), tint = YY.c.t2, modifier = Modifier.size(YY.s(11)))
            }
        }
    }
}

/** A thin progress bar in the tint, drawn without per-frame layout. */
@Composable
fun ProgressLine(value: Double, height: Dp = 3.dp, modifier: Modifier = Modifier) {
    // A new value grows to its length instead of jumping there.
    val shown = androidx.compose.animation.core.animateFloatAsState(value.coerceIn(0.0, 1.0).toFloat(), YYMotion.standard(), label = "progress")
    val track = YY.c.inset
    val fill = YY.tint.fill
    Box(
        modifier.fillMaxWidth().height(height).clip(CircleShape).drawBehind {
            val r = CornerRadius(size.height / 2f)
            drawRoundRect(track, cornerRadius = r)
            val w = size.width * shown.value
            if (w > 0f) drawRoundRect(fill, size = Size(w, size.height), cornerRadius = r)
        },
    )
}

