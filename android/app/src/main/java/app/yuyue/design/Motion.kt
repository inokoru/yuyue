package app.yuyue.design

import android.content.Context
import android.provider.Settings
import androidx.compose.animation.ContentTransform
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.FiniteAnimationSpec
import androidx.compose.animation.core.Spring
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.clickable
import androidx.compose.runtime.remember
import androidx.compose.ui.semantics.Role
import androidx.compose.runtime.getValue
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.IntOffset

/**
 * 动效, as on Apple: every animation picks one of these.
 * Short, strong ease-out, no bounce.
 */
object YYMotion {
    /** Strong ease-out curve for crisp UI entrances, state changes and exits (never ease-in). */
    val EaseOut = CubicBezierEasing(0.23f, 1f, 0.32f, 1f)

    fun reduceMotion(context: Context): Boolean =
        (Prefs.isReady && Prefs.bool("eink.on", false)) ||
            Settings.Global.getFloat(context.contentResolver, Settings.Global.ANIMATOR_DURATION_SCALE, 1f) == 0f

    @Composable
    fun isReduceMotion(): Boolean {
        val eink = if (Prefs.isReady) rememberPref("eink.on", false).value else false
        return eink || reduceMotion(LocalContext.current)
    }

    private fun isEink(): Boolean = Prefs.isReady && Prefs.bool("eink.on", false)

    /** Hover, press, an icon changing, a tick. */
    fun <T> quick(): FiniteAnimationSpec<T> = if (isEink()) tween(0) else tween(YYDuration.Quick, easing = EaseOut)

    /** A card or panel coming and going, a colour, a progress line, a number. */
    fun <T> standard(): FiniteAnimationSpec<T> = if (isEink()) tween(0) else tween(YYDuration.Standard, easing = EaseOut)

    /** Things moving to a new place: re-sorting, a selection, opening and closing. No bounce. */
    fun <T> layout(): FiniteAnimationSpec<T> = if (isEink()) tween(0) else spring(dampingRatio = Spring.DampingRatioNoBouncy, stiffness = 400f)

    /** Going away, a little quicker than coming — eased out so dismissal starts immediately. */
    fun <T> exit(): FiniteAnimationSpec<T> = if (isEink()) tween(0) else tween(YYDuration.Exit, easing = EaseOut)

    /** A layout spring for positions (LazyGrid / LazyColumn `animateItem`). */
    val place: FiniteAnimationSpec<IntOffset> = spring(dampingRatio = Spring.DampingRatioNoBouncy, stiffness = 400f)

    /** A bar or panel showing: fades in and comes a little way from its edge (fade-only under reduce motion). */
    fun barIn(fromTop: Boolean, reduce: Boolean = false): EnterTransition =
        if (reduce) fadeIn(quick()) else fadeIn(standard()) + slideInVertically(standard()) { if (fromTop) -it / 3 else it / 3 }

    fun barOut(toTop: Boolean, reduce: Boolean = false): ExitTransition =
        if (reduce) fadeOut(exit()) else fadeOut(exit()) + slideOutVertically(exit()) { if (toTop) -it / 3 else it / 3 }

    /** Content changing in place (another tab's list, loading → loaded): a quick crossfade, the new one
     *  a few dp lower (fade-only under reduce motion). */
    fun swap(reduce: Boolean = false): ContentTransform =
        if (reduce) fadeIn(quick()) togetherWith fadeOut(exit())
        else (fadeIn(standard()) + slideInVertically(standard()) { 6 }) togetherWith fadeOut(exit())

    /** A page going deeper (forward) or back: the new one comes a short way from its side as the old
     *  one fades, never the whole width; fade-only under reduce motion. */
    fun push(forward: Boolean, reduce: Boolean = false): ContentTransform =
        if (reduce) swap(true)
        else if (forward) (fadeIn(standard()) + slideInHorizontally(standard()) { it / 6 }) togetherWith (fadeOut(exit()) + slideOutHorizontally(exit()) { -it / 12 })
        else (fadeIn(standard()) + slideInHorizontally(standard()) { -it / 12 }) togetherWith (fadeOut(exit()) + slideOutHorizontally(exit()) { it / 6 })

}

/** Gives a little while pressed (0.98), back as it's let go. Pass the same interaction
 *  source as the clickable. */
@Composable
fun Modifier.pressScale(source: MutableInteractionSource, scale: Float = 0.98f): Modifier {
    val pressed by source.collectIsPressedAsState()
    val s by animateFloatAsState(if (pressed) scale else 1f, if (pressed) tween(80, easing = YYMotion.EaseOut) else YYMotion.quick(), label = "press")
    return graphicsLayer { scaleX = s; scaleY = s }
}

/** A tappable thing with no ripple of its own: clickable, giving a little while pressed. */
@Composable
fun Modifier.pressable(
    label: String? = null,
    enabled: Boolean = true,
    scale: Float = 0.96f,
    role: Role? = Role.Button,
    onClick: () -> Unit,
): Modifier {
    val source = remember { MutableInteractionSource() }
    return pressScale(source, scale).clickable(source, null, enabled, label, role, onClick)
}
