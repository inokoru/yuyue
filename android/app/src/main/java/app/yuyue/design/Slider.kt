package app.yuyue.design

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
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
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
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
    val span = range.endInclusive - range.start
    val fraction = ((value - range.start) / span).coerceIn(0f, 1f)
    val knob = 22.dp
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
                fun at(x: Float) = range.start + ((x - k / 2) / (size.width - k)).coerceIn(0f, 1f) * span
                detectTapGestures { change(at(it.x)) }
            }
            .pointerInput(range) {
                val k = knob.toPx()
                fun at(x: Float) = range.start + ((x - k / 2) / (size.width - k)).coerceIn(0f, 1f) * span
                detectHorizontalDragGestures { c, _ -> c.consume(); change(at(c.position.x)) }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        val travel = maxWidth - knob
        Box(Modifier.padding(horizontal = knob / 2).fillMaxWidth().height(4.dp).clip(CircleShape).background(YY.c.line))
        Box(Modifier.padding(start = knob / 2).width(travel * fraction).height(4.dp).clip(CircleShape).background(YY.tint.fill))
        Box(
            Modifier.offset(x = travel * fraction).size(knob)
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
            Modifier.heightIn(min = 36.dp).clip(RoundedCornerShape(YY.s(7))).clickable { open = true }.padding(horizontal = YY.s(6), vertical = YY.s(3)),
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
