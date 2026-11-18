package app.yuyue.design

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** 边下边听's small ring, as on Apple: an arrow to fetch; filling in ink while it comes, a small
 *  square in it to pause; paused, the arrow again over what has come. */
sealed interface ShareRingState {
    data object Get : ShareRingState
    data class Coming(val fraction: Float) : ShareRingState
    data class Paused(val fraction: Float) : ShareRingState
}

@Composable
fun ShareRing(state: ShareRingState, size: Dp = 18.dp, label: String? = null, action: () -> Unit) {
    val ink = YY.c.t1
    val line = YY.c.line
    val pale = YY.c.t3
    Box(Modifier.size(size + 12.dp).clickable(onClickLabel = label, onClick = action), contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(size)) {
            val w = 1.4.dp.toPx()
            val inset = w / 2
            val arc = Size(this.size.width - w, this.size.height - w)
            drawCircle(line, radius = arc.width / 2, style = Stroke(w))
            val fraction = when (state) {
                is ShareRingState.Coming -> state.fraction
                is ShareRingState.Paused -> state.fraction
                ShareRingState.Get -> 0f
            }
            if (fraction > 0f || state is ShareRingState.Coming) {
                drawArc(if (state is ShareRingState.Paused) pale else ink, -90f, 360f * fraction.coerceIn(0.02f, 1f), false,
                    Offset(inset, inset), arc, style = Stroke(w, cap = StrokeCap.Round))
            }
            if (state is ShareRingState.Coming) {
                val s = this.size.width * 0.27f
                drawRoundRect(ink, Offset(center.x - s / 2, center.y - s / 2), Size(s, s), CornerRadius(1.dp.toPx()))
            }
        }
        if (state !is ShareRingState.Coming) {
            Icon(Icons.arrowDown, null, tint = if (state is ShareRingState.Paused) pale else YY.c.t2, modifier = Modifier.size(size * 0.55f))
        }
    }
}
