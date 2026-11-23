package app.yuyue.shelf

import androidx.compose.animation.core.Animatable
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** The note after something happened: one white capsule at the top, a coloured disc with a
 *  tick and one line of words. One that can be undone has a round undo button at its end; when
 *  the seconds are up the button folds away and it is a plain note for a moment before it goes. */
@Composable
fun UndoToast(toast: Library.Toast, undo: () -> Unit, modifier: Modifier = Modifier) {
    var dismissed by remember(toast.id) { mutableStateOf(false) }
    if (dismissed) return
    val reduce = YYMotion.isReduceMotion()
    val scope = rememberCoroutineScope()
    val canUndo = toast.undo != null
    val shown = remember(toast.id) { Animatable(0f) }
    val dragOffsetY = remember(toast.id) { Animatable(0f) }
    var rawDragY by remember(toast.id) { mutableFloatStateOf(0f) }
    // 1 while the undo button is there; folds to 0 when the seconds are up.
    val button = remember(toast.id) { Animatable(if (canUndo) 1f else 0f) }
    var done by remember(toast.id) { mutableStateOf(!canUndo) }
    LaunchedEffect(toast.id) {
        shown.animateTo(1f, YYMotion.standard())
        if (canUndo) {
            delay(Library.UNDO_MS - app.yuyue.design.YYDuration.Standard)
            done = true
            button.animateTo(0f, YYMotion.quick())
            delay(Library.UNDO_SETTLE_MS)
        } else {
            // Goes as it came, just before Library takes it off, rather than vanishing mid-frame.
            delay(Library.TOAST_MS - app.yuyue.design.YYDuration.Standard - app.yuyue.design.YYDuration.Exit - 60)
        }
        shown.animateTo(0f, YYMotion.exit())
    }
    val b = button.value.coerceIn(0f, 1f)
    UndoToastPlate(
        title = toast.title,
        danger = toast.danger,
        error = toast.icon == Library.Toast.Icon.Error,
        button = b,
        undoShown = !done && b > 0f,
        undo = undo,
        modifier = modifier
            .pointerInput(toast.id) {
                val velocityTracker = VelocityTracker()
                detectVerticalDragGestures(
                    onDragStart = {
                        rawDragY = dragOffsetY.value
                        velocityTracker.resetTracking()
                    },
                    onDragEnd = {
                        val vy = velocityTracker.calculateVelocity().y
                        if (dragOffsetY.value < -24.dp.toPx() || vy < -350.dp.toPx()) {
                            done = true
                            scope.launch {
                                launch { dragOffsetY.animateTo(-72.dp.toPx(), YYMotion.exit()) }
                                shown.animateTo(0f, YYMotion.exit())
                                dismissed = true
                            }
                        } else {
                            scope.launch { dragOffsetY.animateTo(0f, YYMotion.layout()) }
                        }
                    },
                    onDragCancel = {
                        scope.launch { dragOffsetY.animateTo(0f, YYMotion.layout()) }
                    },
                ) { change, dragAmount ->
                    change.consume()
                    velocityTracker.addPosition(change.uptimeMillis, change.position)
                    rawDragY += dragAmount
                    val visualY = if (rawDragY > 0f) rawDragY * 0.18f else rawDragY
                    scope.launch { dragOffsetY.snapTo(visualY) }
                }
            }
            .graphicsLayer {
                val v = shown.value
                val dy = dragOffsetY.value
                val dragFade = 1f - (-dy / 96.dp.toPx()).coerceIn(0f, 0.6f)
                alpha = (v * dragFade).coerceIn(0f, 1f)
                translationY = (if (reduce) 0f else (1 - v) * -10.dp.toPx()) + dy
                scaleX = if (reduce) 1f else 0.97f + 0.03f * v
                scaleY = if (reduce) 1f else 0.97f + 0.03f * v
                transformOrigin = TransformOrigin(0.5f, 0f)
            },
    )
}

/** The capsule itself, settled. [UndoToast] animates into this; the snapshot records this, not the motion. */
@Composable
internal fun UndoToastPlate(
    title: String,
    danger: Boolean,
    error: Boolean,
    button: Float,
    undoShown: Boolean,
    undo: () -> Unit,
    modifier: Modifier = Modifier,
) {
    val eink = if (app.yuyue.design.Prefs.isReady) app.yuyue.design.rememberPref("eink.on", false).value else false
    val b = button.coerceIn(0f, 1f)
    Row(
        modifier
            .widthIn(max = 420.dp)
            .shadow(if (eink) 0.dp else 11.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.04f), spotColor = Color.Black.copy(alpha = if (YY.dark) 0.4f else 0.08f))
            .background(if (YY.dark) Color(0xFF2A2A2C) else Color(0xFFFCFCFC), CircleShape)
            .border(0.5.dp, if (YY.dark) Color.White.copy(alpha = 0.12f) else Color.Black.copy(alpha = 0.14f), CircleShape)
            .heightIn(min = YY.s(40))
            .padding(start = YY.s(14), end = YY.s(18f - 16.5f * b))
            .semantics(mergeDescendants = true) {
                liveRegion = LiveRegionMode.Polite
                if (undoShown) customActions = listOf(CustomAccessibilityAction(t("撤销")) { undo(); true })
            },
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(
            Modifier.size(YY.s(17)).background(if (danger || error) YY.c.danger else Done, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(if (error) Icons.bang else Icons.check, null, tint = Color.White, modifier = Modifier.size(YY.s(12)))
        }
        Text(
            title, style = YY.font(13f, FontWeight.Medium), color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = YY.s(10), top = 4.dp, bottom = 4.dp).weight(1f, fill = false),
        )
        if (b > 0f) {
            Box(
                Modifier
                    .padding(start = YY.s(6.5f * b))
                    .size((44f * b).dp)
                    .clip(CircleShape)
                    .clickable(enabled = undoShown, onClickLabel = t("撤销"), onClick = undo),
                contentAlignment = Alignment.Center,
            ) {
                Box(
                    Modifier
                        .size(YY.s(24f * b))
                        .graphicsLayer { alpha = b }
                        .clip(CircleShape)
                        .background(if (YY.dark) Color(0xFF3A3A3C) else Color(0xFFEBEBEB)),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.undo, t("撤销"), tint = YY.c.t1, modifier = Modifier.size(YY.s(14f * b)))
                }
            }
        }
    }
}

/** A green tick for a plain note; a deletion is red. */
private val Done = Color(0xFF2DBE60)

val Library.Toast.Icon.vector: ImageVector
    get() = when (this) {
        Library.Toast.Icon.Delete -> Icons.delete
        Library.Toast.Icon.Highlight -> Icons.highlighter
        Library.Toast.Icon.Add -> Icons.add
        Library.Toast.Icon.Folder -> Icons.folder
        Library.Toast.Icon.Download -> Icons.cloudDownload
        Library.Toast.Icon.Check -> Icons.checkCircle
        Library.Toast.Icon.Share -> Icons.upload
        Library.Toast.Icon.Error -> Icons.error
    }
