package app.yuyue.design

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.drag
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.ProgressBarRangeInfo
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.progressBarRangeInfo
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.setProgress
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import kotlin.math.abs

/** A thin slider: a 4-point track, the part up to the value in the theme's fill, and a round
 *  white knob — as the system draws it on iOS, not Material 3's pill knob. */
@Composable
fun YYSlider(
    value: Float,
    onChange: (Float) -> Unit,
    range: ClosedFloatingPointRange<Float>,
    modifier: Modifier = Modifier,
    description: String? = null,
    state: String? = null,
) {
    val change by rememberUpdatedState(onChange)
    val currentValue by rememberUpdatedState(value)
    val span = (range.endInclusive - range.start).coerceAtLeast(1e-6f)
    val fraction = ((value - range.start) / span).coerceIn(0f, 1f)
    val knob = 22.dp
    val trackColor = YY.c.line
    val fillColor = YY.tint.fill
    val knobBorder = if (YY.dark) Color.White.copy(alpha = 0.22f) else Color(0x33332E29)
    BoxWithConstraints(
        modifier
            .fillMaxWidth()
            .heightIn(min = 44.dp)
            .semantics {
                description?.let { contentDescription = it }
                state?.let { stateDescription = it }
                progressBarRangeInfo = ProgressBarRangeInfo(value, range)
                setProgress { change(it.coerceIn(range)); true }
            }
            .pointerInput(range) {
                val k = knob.toPx()
                val localSpan = (range.endInclusive - range.start).coerceAtLeast(1e-6f)
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val usable = (size.width - k).coerceAtLeast(1f)
                    val currentFraction = ((currentValue - range.start) / localSpan).coerceIn(0f, 1f)
                    val knobCenterPx = k / 2f + currentFraction * usable
                    val grabOffset = if (abs(down.position.x - knobCenterPx) <= k * 0.85f) {
                        down.position.x - knobCenterPx
                    } else {
                        change(range.start + ((down.position.x - k / 2f) / usable).coerceIn(0f, 1f) * localSpan)
                        0f
                    }
                    drag(down.id) { c ->
                        c.consume()
                        change(range.start + ((c.position.x - grabOffset - k / 2f) / usable).coerceIn(0f, 1f) * localSpan)
                    }
                }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        val travelPx = with(LocalDensity.current) { (maxWidth - knob).coerceAtLeast(0.dp).toPx() }
        Spacer(
            Modifier
                .padding(horizontal = knob / 2)
                .fillMaxWidth()
                .height(4.dp)
                .drawBehind {
                    val r = CornerRadius(size.height / 2f)
                    drawRoundRect(trackColor, cornerRadius = r)
                    val fillWidth = size.width * fraction
                    if (fillWidth > 0f) {
                        drawRoundRect(fillColor, size = Size(fillWidth, size.height), cornerRadius = r)
                    }
                }
        )
        Box(
            Modifier
                .graphicsLayer { translationX = travelPx * fraction }
                .size(knob)
                .shadow(3.dp, CircleShape, ambientColor = Color(0x26332E29), spotColor = Color(0x38332E29))
                .background(Color.White, CircleShape)
                .border(0.75.dp, knobBorder, CircleShape)
        )
    }
}

/** The speed written as 1.0× with a ▾; tapped, the usual speeds drop down to pick from. */
@Composable
fun RateMenu(rate: Float, rates: List<Float>, pick: (Float) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(
            Modifier.heightIn(min = 44.dp).clip(RoundedCornerShape(YY.s(7))).pressable { open = true }.padding(horizontal = YY.s(6), vertical = YY.s(3)),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(YY.s(3)),
        ) {
            Text(rateLabel(rate), style = YY.font(11.5f, FontWeight.Medium, tnum = true), color = YY.c.t1)
            Icon(Icons.chevronDown, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(13)))
        }
        DropdownMenu(open, { open = false }, containerColor = YY.c.card) {
            for (r in rates) {
                val on = abs(rate - r) < 0.05f
                DropdownMenuItem(
                    text = { Text(rateLabel(r), style = YY.font(11.5f, if (on) FontWeight.SemiBold else FontWeight.Normal, tnum = true), color = if (on) YY.tint.accent else YY.c.t1) },
                    trailingIcon = { if (on) Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(13))) },
                    onClick = { open = false; pick(r) },
                )
            }
        }
    }
}

fun rateLabel(r: Float): String = "%.1f×".format(r)
