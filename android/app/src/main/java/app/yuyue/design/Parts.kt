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

private val LocalFieldLabelWidth = compositionLocalOf<MutableIntState?> { null }

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
fun YYLabel(title: String, trailing: String? = null) {
    Row(
        Modifier.fillMaxWidth().padding(top = YY.s(18), bottom = YY.s(8), start = YY.s(2), end = YY.s(2)),
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
    CompositionLocalProvider(LocalFieldLabelWidth provides labelWidthPx) {
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
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = YY.s(38))
            .then(if (onClick != null) Modifier.clickable(role = androidx.compose.ui.semantics.Role.Button, onClick = onClick) else Modifier),
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

/** Choices on an inset track, the chosen one lifted on a white chip. */
@Composable
fun <T> YYSegments(items: List<Pair<T, String>>, selection: T, onSelect: (T) -> Unit, small: Boolean = false, modifier: Modifier = Modifier) {
    Row(
        modifier
            .width(IntrinsicSize.Max)
            .clip(RoundedCornerShape(YY.s(if (small) 10 else 12)))
            .background(YY.c.inset)
            .padding(3.dp),
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        for ((value, title) in items) {
            val on = value == selection
            // The chosen chip fades across rather than jumping.
            val chip by androidx.compose.animation.animateColorAsState(if (on) YY.c.card else YY.c.card.copy(alpha = 0f), YYMotion.quick(), label = "segment")
            val lift by androidx.compose.animation.core.animateDpAsState(if (on) 1.dp else 0.dp, YYMotion.quick(), label = "segmentShadow")
            val ink by androidx.compose.animation.animateColorAsState(if (on) YY.c.t1 else YY.c.t2, YYMotion.quick(), label = "segmentInk")
            Box(
                Modifier
                    .weight(1f, fill = true)
                    .heightIn(min = YY.s(if (small) 22 else 28))
                    .shadow(lift, RoundedCornerShape(YY.s(if (small) 7 else 9)))
                    .clip(RoundedCornerShape(YY.s(if (small) 7 else 9)))
                    .background(chip)
                    .selectable(selected = on, role = androidx.compose.ui.semantics.Role.Tab) { onSelect(value) }
                    .padding(horizontal = if (small) YY.s(8) else YY.s(6), vertical = YY.s(2)),
                contentAlignment = Alignment.Center,
            ) {
                // A longer English title (Medium line spacing) gets smaller rather than cut off.
                val size = if (small) 10f else 11f
                androidx.compose.foundation.text.BasicText(
                    title,
                    style = YY.font(size, if (on) FontWeight.SemiBold else FontWeight.Normal, tnum = true).copy(color = ink),
                    maxLines = 1,
                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                    autoSize = androidx.compose.foundation.text.TextAutoSize.StepBased(minFontSize = YY.sp(size * 0.78f), maxFontSize = YY.sp(size), stepSize = 0.5.sp),
                )
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

/** A sheet's own title with × on the right. */
@Composable
fun YYSheetHeader(title: String, subtitle: String? = null, close: () -> Unit) {
    val gap = YY.s(8)
    Row(
        // No side padding of its own: the sheet's edge is the edge, so × sits right against it.
        Modifier.fillMaxWidth().padding(top = YY.s(10), bottom = YY.s(6)),
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

