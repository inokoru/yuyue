package app.yuyue.design

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import app.yuyue.AppLanguage

/** The ink line: a soft edge in the theme's colour passing left to right, once, after the screen
 *  was made again in a new language (通用 › 语言). As the Mac and iPhone draw it. */
@Composable
fun InkSweepOnce() {
    var shown by remember { mutableStateOf(AppLanguage.sweepPending) }
    if (!shown) return
    val fill = YY.tint.fill
    val x = remember { Animatable(0f) }
    val alpha = remember { Animatable(1f) }
    LaunchedEffect(Unit) {
        AppLanguage.sweepPending = false
        x.animateTo(1f, tween(650, easing = CubicBezierEasing(0.4f, 0f, 0.3f, 1f)))
        alpha.animateTo(0f, tween(200))
        shown = false
    }
    Canvas(Modifier.fillMaxSize()) {
        val band = 140.dp.toPx()
        val left = x.value * (size.width + band + 20.dp.toPx()) - band
        drawRect(
            Brush.horizontalGradient(
                0f to Color.Transparent, 0.7f to fill.copy(alpha = 0.07f), 0.98f to fill.copy(alpha = 0.3f), 1f to Color.Transparent,
                startX = left, endX = left + band,
            ),
            topLeft = Offset(left, 0f), size = Size(band, size.height), alpha = alpha.value,
        )
    }
}
