package app.yuyue.me

import android.graphics.Bitmap
import android.os.Build
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.blur
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.graphics.BlendMode
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.CompositingStrategy
import androidx.compose.ui.graphics.ImageShader
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathMeasure
import androidx.compose.ui.graphics.ShaderBrush
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.TileMode
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import app.yuyue.design.YY
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.random.Random

/** The three strokes, in the order they were written, on a 60 × 56 sheet. */
private fun strokes(): List<Path> {
    fun p(x: Float, y: Float) = Offset(x - 8, y - 6)
    fun Path.c(c1: Offset, c2: Offset, to: Offset) = cubicTo(c1.x, c1.y, c2.x, c2.y, to.x, to.y)
    return listOf(
        Path().apply {
            moveTo(p(24f, 9f).x, p(24f, 9f).y)
            c(p(19f, 15f), p(13.5f, 25f), p(11.5f, 33f)); c(p(10f, 39f), p(13f, 41.5f), p(20f, 41.5f))
            c(p(28f, 41.5f), p(36f, 40.5f), p(44f, 39.5f)); c(p(51f, 38.5f), p(58f, 37.5f), p(64f, 37f))
        },
        Path().apply {
            moveTo(p(21.5f, 17f).x, p(21.5f, 17f).y)
            c(p(27f, 18.5f), p(31.5f, 18.8f), p(35f, 18.2f)); c(p(31f, 21f), p(27.5f, 26f), p(26.3f, 33f))
            c(p(25.3f, 40f), p(24.6f, 47f), p(25.2f, 53f))
        },
        Path().apply {
            moveTo(p(43f, 17.5f).x, p(43f, 17.5f).y)
            c(p(48f, 18.8f), p(52.5f, 17.8f), p(56.5f, 15.5f)); c(p(51f, 19f), p(45.5f, 24f), p(43.3f, 31f))
            c(p(41.5f, 39f), p(40.3f, 49f), p(41f, 59.5f))
        },
        // Where the pencil pressed hardest: the right half of the long stroke.
        Path().apply {
            moveTo(p(40f, 40.2f).x, p(40f, 40.2f).y)
            c(p(48f, 39f), p(56f, 37.8f), p(63.5f, 37.2f))
        },
    )
}

/** Paper grain the graphite didn't reach. */
private val grain by lazy {
    val n = 96
    val px = IntArray(n * n) { val a = ((1.3f - 0.9f * Random.nextFloat()).coerceIn(0f, 1f) * 255).toInt(); a shl 24 }
    Bitmap.createBitmap(px, n, n, Bitmap.Config.ARGB_8888).asImageBitmap()
}

/**
 * The foot of 关于: the pencil signature from the corner of a drawing — a curve running into the
 * long stroke under it, then two T's. Not there when the page opens; tapped, it writes itself out.
 */
@Composable
fun PencilSignature(modifier: Modifier = Modifier) {
    val paths = remember { strokes() }
    val drawn = remember { List(3) { Animatable(0f) } }
    var run by remember { mutableIntStateOf(0) }
    val lead = if (YY.dark) Color(0xFFD9D5CD) else Color(0xFF2F2E2D)
    LaunchedEffect(run) {
        drawn.forEach { it.snapTo(0f) }
        if (run == 0) return@LaunchedEffect
        delay(100)
        val ease = CubicBezierEasing(0.45f, 0.05f, 0.3f, 1f)
        listOf(620, 500, 560).forEachIndexed { i, d ->
            launch { drawn[i].animateTo(1f, tween(d, easing = ease)) }
            delay((d * 0.82f).toLong())
        }
    }
    Canvas(
        modifier.size(84.dp, 78.4.dp)
            .graphicsLayer { compositingStrategy = CompositingStrategy.Offscreen }
            .then(if (Build.VERSION.SDK_INT >= 31) Modifier.blur(0.45.dp) else Modifier)
            .pointerInput(Unit) { detectTapGestures(onTap = { run++ }) }
            .semantics { contentDescription = "CTT" },
    ) {
        val k = size.width / 60f
        for (i in 0..2) {
            pencil(paths[i], drawn[i].value, k, lead.copy(alpha = 0.9f), 1.9f)
            translate(0.28f * k, 0.22f * k) { pencil(paths[i], drawn[i].value, k, lead.copy(alpha = 0.6f), 1f) }
        }
        pencil(paths[3], (drawn[0].value * 2.1f - 1.1f).coerceAtLeast(0f), k, lead.copy(alpha = 0.5f), 2.6f)
        // The grain, taken out of what's drawn.
        drawRect(ShaderBrush(ImageShader(grain, TileMode.Repeated, TileMode.Repeated)), blendMode = BlendMode.DstIn)
    }
}

/** The first `part` of a stroke, `width` on the sheet. */
private fun DrawScope.pencil(path: Path, part: Float, k: Float, color: Color, width: Float) {
    if (part <= 0f) return
    val measure = PathMeasure().apply { setPath(path, false) }
    val piece = Path()
    measure.getSegment(0f, measure.length * part, piece, true)
    val scaled = Path().apply { addPath(piece) }
    scaled.transform(androidx.compose.ui.graphics.Matrix().apply { scale(k, k) })
    drawPath(scaled, color, style = Stroke(width * k, cap = StrokeCap.Round, join = StrokeJoin.Round))
}
