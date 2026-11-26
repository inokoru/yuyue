package app.yuyue.design

import android.os.Build
import android.view.HapticFeedbackConstants
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.snap
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.runtime.withFrameNanos
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.drawscope.clipRect
import androidx.compose.ui.graphics.drawscope.scale
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.util.VelocityTracker
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.DpOffset
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.dp
import androidx.compose.ui.util.lerp
import com.kyant.backdrop.Backdrop
import com.kyant.backdrop.backdrops.layerBackdrop
import com.kyant.backdrop.backdrops.rememberCanvasBackdrop
import com.kyant.backdrop.backdrops.rememberCombinedBackdrop
import com.kyant.backdrop.backdrops.rememberLayerBackdrop
import com.kyant.backdrop.drawBackdrop
import com.kyant.backdrop.effects.blur
import com.kyant.backdrop.effects.lens
import com.kyant.backdrop.effects.vibrancy
import com.kyant.backdrop.highlight.Highlight
import com.kyant.backdrop.isRuntimeShaderSupported
import com.kyant.backdrop.shadow.InnerShadow
import com.kyant.backdrop.shadow.Shadow
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlin.math.tanh

/**
 * 液态玻璃效果 on Android, mirroring Apple's `LiquidGlass.swift` (`appearance.liquidGlass`).
 * Powered by Kyant0's AndroidLiquidGlass (`io.github.kyant0:backdrop`, Apache-2.0) on Android 13+
 * (API 33+) with dual-layer lens refraction, chromatic dispersion, and interactive drag physics
 * for the floating bottom navigation bar. Never used over WebView.
 */
object LiquidGlass {
    const val KEY = "appearance.liquidGlass"

    val supported: Boolean
        get() = isRuntimeShaderSupported()
}

/**
 * Floating draggable Liquid Glass bottom tab capsule powered by Kyant0 Backdrop:
 * - Outer capsule refracts & softly blurs [backdrop] (the page content behind the bar).
 * - Touching & dragging slides a raised convex glass lens over the tabs, magnifying and
 *   chromatically refracting both the underlying page and the accent-tinted tab icons/labels.
 * - Tapping a different tab glides the lens with a brief refractive swell; tapping the current
 *   tab preserves secondary actions (e.g. opening 书架's shelf switcher).
 */
@Composable
fun LiquidBottomTabBar(
    backdrop: Backdrop,
    selectedIndex: Int,
    tabCount: Int,
    tabLabels: List<String>,
    onSelect: (Int) -> Unit,
    modifier: Modifier = Modifier,
    tabContent: @Composable RowScope.(index: Int, active: Boolean) -> Unit,
) {
    val dark = YY.dark
    val tintFill = YY.tint.fill
    val reduce = YYMotion.isReduceMotion()
    val density = LocalDensity.current
    val layoutDirection = LocalLayoutDirection.current
    val viewConfiguration = LocalViewConfiguration.current
    val view = LocalView.current
    val scope = rememberCoroutineScope()

    val currentSelected by rememberUpdatedState(selectedIndex)
    val currentOnSelect by rememberUpdatedState(onSelect)
    val currentReduce by rememberUpdatedState(reduce)

    var lastActiveIndex by remember { mutableIntStateOf(selectedIndex.coerceAtLeast(0)) }
    if (selectedIndex >= 0) lastActiveIndex = selectedIndex

    val maxIndex = (tabCount - 1).coerceAtLeast(0).toFloat()
    val pillPos = remember { Animatable(lastActiveIndex.toFloat()) }
    val pressAnim = remember { Animatable(0f) }
    val stretchAnim = remember { Animatable(0f) }

    // Synchronous zero-allocation drag state read directly in draw/layerBlock at 120fps.
    var isGestureActive by remember { mutableStateOf(false) }
    var isActivelyDragging by remember { mutableStateOf(false) }
    var liveDragPos by remember { mutableFloatStateOf(lastActiveIndex.toFloat()) }
    var liveDragStretch by remember { mutableFloatStateOf(0f) }
    var targetDragStretch by remember { mutableFloatStateOf(0f) }
    var lastMoveNanos by remember { mutableLongStateOf(0L) }
    var overscrollSlots by remember { mutableFloatStateOf(0f) }
    var lastHandledIndex by remember { mutableIntStateOf(selectedIndex) }

    var settleJob by remember { mutableStateOf<Job?>(null) }
    var pressJob by remember { mutableStateOf<Job?>(null) }
    var wasInactive by remember { mutableStateOf(selectedIndex < 0) }

    // Smoothly relax velocity stretch when finger pauses mid-drag.
    LaunchedEffect(isActivelyDragging) {
        if (!isActivelyDragging) return@LaunchedEffect
        while (true) {
            withFrameNanos { now ->
                val target = if (now - lastMoveNanos > 48_000_000L) 0f else targetDragStretch
                liveDragStretch += (target - liveDragStretch) * 0.28f
                if (abs(liveDragStretch) < 0.001f && target == 0f) liveDragStretch = 0f
            }
        }
    }

    // Keep the pill in sync when selection or tab count changes externally (e.g. Back button,
    // Search <-> Tab, or live Bottom Navigation customization).
    LaunchedEffect(selectedIndex, tabCount) {
        if (selectedIndex >= 0) {
            val clampedTarget = selectedIndex.coerceIn(0, (tabCount - 1).coerceAtLeast(0)).toFloat()
            if ((selectedIndex != lastHandledIndex || pillPos.value > maxIndex) && !isGestureActive && !isActivelyDragging) {
                lastHandledIndex = selectedIndex
                if (wasInactive || currentReduce || pillPos.value > maxIndex) {
                    pillPos.snapTo(clampedTarget)
                } else {
                    pillPos.animateTo(
                        targetValue = clampedTarget,
                        animationSpec = spring(dampingRatio = 0.74f, stiffness = 360f),
                    )
                }
            } else if (pillPos.value != clampedTarget && !isGestureActive && !isActivelyDragging) {
                pillPos.snapTo(clampedTarget)
            }
            wasInactive = false
        } else {
            lastHandledIndex = -1
            wasInactive = true
        }
    }

    val pillVisible = selectedIndex >= 0 || isGestureActive
    val pillAlpha by animateFloatAsState(
        targetValue = if (pillVisible) 1f else 0f,
        animationSpec = YYMotion.quick(),
        label = "liquidTabAlpha",
    )

    // Capsule-clipped copy of the outer glass bar surface so the enlarged floating lens
    // seamlessly refracts crisp pageBackdrop where it protrudes above/below the bar.
    val barClipPath = remember { Path() }
    val barBackdrop = rememberLayerBackdrop {
        val r = size.height / 2f
        barClipPath.rewind()
        barClipPath.addRoundRect(RoundRect(0f, 0f, size.width, size.height, CornerRadius(r, r)))
        clipPath(barClipPath) {
            this@rememberLayerBackdrop.drawContent()
        }
    }
    val activeTabsBackdrop = rememberLayerBackdrop()

    val restingFillColor = remember(dark, tintFill) {
        if (dark) Color.White.copy(alpha = 0.14f) else tintFill.copy(alpha = 0.11f)
    }
    val currentRestingFill by rememberUpdatedState(restingFillColor)
    val restingTintBackdrop = rememberCanvasBackdrop {
        val restAlpha = (1f - pressAnim.value).coerceIn(0f, 1f)
        if (restAlpha > 0.005f) {
            drawRect(currentRestingFill.copy(alpha = currentRestingFill.alpha * restAlpha))
        }
    }
    val indicatorBackdrop = rememberCombinedBackdrop(
        backdrop,
        barBackdrop,
        restingTintBackdrop,
        activeTabsBackdrop,
    )

    val outerSurfaceColor = remember(dark) {
        if (dark) Color(0xFF1F1E1D).copy(alpha = 0.56f)
        else Color(0xFFFAF9F7).copy(alpha = 0.48f)
    }
    val outerShadowColor = remember(dark) {
        Color.Black.copy(alpha = if (dark) 0.40f else 0.12f)
    }
    val lensShadowColor = remember(dark) {
        Color.Black.copy(alpha = if (dark) 0.42f else 0.16f)
    }
    val lensSurfaceTint = remember(dark) {
        Color.White.copy(alpha = if (dark) 0.06f else 0.14f)
    }

    fun triggerTick() {
        val haptic = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            HapticFeedbackConstants.SEGMENT_FREQUENT_TICK
        } else {
            HapticFeedbackConstants.CLOCK_TICK
        }
        view.performHapticFeedback(haptic)
    }

    fun rubberBand(overshoot: Float): Float = 0.24f * tanh(overshoot * 2.2f)

    val animatedOverscroll by animateFloatAsState(
        targetValue = if (isActivelyDragging) rubberBand(overscrollSlots) else 0f,
        animationSpec = if (isActivelyDragging || reduce) snap()
        else spring(dampingRatio = 0.62f, stiffness = 380f),
        label = "liquidBarOverscroll",
    )
    val overscrollPx = with(density) { (animatedOverscroll * 24.dp.toPx()).roundToInt() }

    BoxWithConstraints(
        modifier = modifier
            .offset { IntOffset(if (layoutDirection == LayoutDirection.Ltr) overscrollPx else -overscrollPx, 0) }
            .pointerInput(tabCount, layoutDirection) {
                if (tabCount <= 0) return@pointerInput
                val touchSlop = viewConfiguration.touchSlop
                val padPx = 4.dp.toPx()
                val isLtr = layoutDirection == LayoutDirection.Ltr

                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val innerW = (size.width - padPx * 2f).coerceAtLeast(1f)
                    val slotW = innerW / tabCount.toFloat()

                    fun xToSlotFloat(px: Float): Float {
                        val localX = (px - padPx).coerceIn(0f, innerW)
                        val directedX = if (isLtr) localX else (innerW - localX)
                        return directedX / slotW - 0.5f
                    }

                    fun xToSlotIndex(px: Float): Int {
                        val localX = (px - padPx).coerceIn(0f, innerW - 0.01f)
                        val directedX = if (isLtr) localX else (innerW - localX)
                        return (directedX / slotW).toInt().coerceIn(0, tabCount - 1)
                    }

                    val pointerId = down.id
                    val downPos = down.position
                    val downSlot = xToSlotIndex(downPos.x)
                    val startedOnSelected = downSlot == currentSelected && currentSelected >= 0
                    val startedFromInactive = currentSelected < 0

                    // Interrupt any in-flight spring cleanly for immediate gesture response.
                    settleJob?.cancel()
                    pressJob?.cancel()
                    isGestureActive = true
                    overscrollSlots = 0f

                    val dragStartValue = if (startedFromInactive) downSlot.toFloat() else pillPos.value
                    liveDragPos = dragStartValue
                    liveDragStretch = stretchAnim.value
                    targetDragStretch = 0f

                    if (startedFromInactive) {
                        scope.launch(start = CoroutineStart.UNDISPATCHED) {
                            pillPos.snapTo(downSlot.toFloat())
                        }
                    }
                    if (startedOnSelected || startedFromInactive) {
                        pressJob = scope.launch {
                            pressAnim.animateTo(
                                targetValue = if (currentReduce) 0.35f else 1f,
                                animationSpec = spring(dampingRatio = 0.72f, stiffness = 460f),
                            )
                        }
                    }

                    val velocityTracker = VelocityTracker()
                    velocityTracker.addPosition(down.uptimeMillis, downPos)

                    var isDragging = false
                    var abortedToVertical = false
                    var cancelled = false
                    var lastHapticSlot = if (startedFromInactive) downSlot else currentSelected.coerceAtLeast(0)
                    var lastEventX = downPos.x
                    var lastEventTime = down.uptimeMillis

                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val change = event.changes.firstOrNull { it.id == pointerId } ?: break
                        if (!change.pressed) {
                            if (isDragging) change.consume()
                            break
                        }

                        val dx = change.position.x - downPos.x
                        val dy = change.position.y - downPos.y

                        if (!isDragging) {
                            if (abs(dy) > touchSlop && abs(dy) > abs(dx) * 1.25f) {
                                abortedToVertical = true
                                break
                            }
                            if (abs(dx) > touchSlop) {
                                isDragging = true
                                isActivelyDragging = true
                                pressJob?.cancel()
                                pressJob = scope.launch {
                                    pressAnim.animateTo(
                                        targetValue = if (currentReduce) 0.35f else 1f,
                                        animationSpec = spring(dampingRatio = 0.72f, stiffness = 460f),
                                    )
                                }
                            }
                        }

                        if (isDragging) {
                            change.consume()
                            velocityTracker.addPosition(change.uptimeMillis, change.position)

                            val directedDx = if (isLtr) dx else -dx
                            val rawVal = if (startedOnSelected || startedFromInactive) {
                                dragStartValue + directedDx / slotW
                            } else {
                                xToSlotFloat(change.position.x)
                            }

                            val dampedVal = when {
                                rawVal < 0f -> {
                                    overscrollSlots = rawVal
                                    -rubberBand(-rawVal)
                                }
                                rawVal > maxIndex -> {
                                    overscrollSlots = rawVal - maxIndex
                                    maxIndex + rubberBand(rawVal - maxIndex)
                                }
                                else -> {
                                    overscrollSlots = 0f
                                    rawVal
                                }
                            }

                            // Catch up smoothly if drag started away from the active pill, else 1:1.
                            val gap = dampedVal - liveDragPos
                            liveDragPos = if (!startedOnSelected && !startedFromInactive && abs(gap) > 0.18f) {
                                liveDragPos + gap * 0.48f
                            } else {
                                dampedVal
                            }

                            val dtMs = (change.uptimeMillis - lastEventTime).coerceAtLeast(1L)
                            val instVx = ((change.position.x - lastEventX) / slotW) / (dtMs / 1000f)
                            lastEventX = change.position.x
                            lastEventTime = change.uptimeMillis
                            lastMoveNanos = System.nanoTime()
                            targetDragStretch = if (currentReduce) 0f else (abs(instVx) * 0.022f).coerceIn(0f, 0.11f)

                            val nearest = liveDragPos.roundToInt().coerceIn(0, tabCount - 1)
                            if (nearest != lastHapticSlot) {
                                lastHapticSlot = nearest
                                triggerTick()
                            }
                        }

                        if (change.isConsumed && !isDragging) {
                            cancelled = true
                            break
                        }
                    }

                    overscrollSlots = 0f
                    isGestureActive = false
                    pressJob?.cancel()

                    when {
                        abortedToVertical || cancelled -> {
                            val releasePos = liveDragPos
                            val releaseStretch = liveDragStretch
                            settleJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                                if (isActivelyDragging) {
                                    pillPos.snapTo(releasePos)
                                    stretchAnim.snapTo(releaseStretch)
                                    isActivelyDragging = false
                                }
                                launch {
                                    if (currentSelected >= 0) {
                                        pillPos.animateTo(
                                            currentSelected.toFloat(),
                                            spring(dampingRatio = 0.78f, stiffness = 380f),
                                        )
                                    }
                                }
                                launch {
                                    stretchAnim.animateTo(0f, spring(dampingRatio = 0.72f, stiffness = 360f))
                                }
                                launch {
                                    pressAnim.animateTo(0f, spring(dampingRatio = 0.82f, stiffness = 360f))
                                }
                            }
                        }

                        isDragging -> {
                            val vxPx = velocityTracker.calculateVelocity().x
                            val vxSlots = (if (isLtr) vxPx else -vxPx) / slotW
                            val releasePos = liveDragPos
                            val releaseStretch = liveDragStretch
                            val projected = (releasePos + vxSlots * 0.11f)
                                .coerceIn(releasePos - 0.85f, releasePos + 0.85f)
                            val target = projected.roundToInt().coerceIn(0, tabCount - 1)

                            if (target != lastHapticSlot) triggerTick()
                            lastHandledIndex = target
                            if (target != currentSelected || startedFromInactive) {
                                currentOnSelect(target)
                            }

                            settleJob = scope.launch(start = CoroutineStart.UNDISPATCHED) {
                                pillPos.snapTo(releasePos)
                                stretchAnim.snapTo(releaseStretch)
                                isActivelyDragging = false
                                launch {
                                    pillPos.animateTo(
                                        targetValue = target.toFloat(),
                                        animationSpec = if (currentReduce) snap()
                                        else spring(dampingRatio = 0.70f, stiffness = 340f),
                                        initialVelocity = if (currentReduce) 0f else vxSlots.coerceIn(-6f, 6f),
                                    )
                                }
                                launch {
                                    stretchAnim.animateTo(0f, spring(dampingRatio = 0.65f, stiffness = 340f))
                                }
                                launch {
                                    pressAnim.animateTo(0f, spring(dampingRatio = 0.82f, stiffness = 320f))
                                }
                            }
                        }

                        else -> {
                            // Clean tap without horizontal drag.
                            isActivelyDragging = false
                            val tappedDifferent = downSlot != currentSelected
                            if (tappedDifferent) triggerTick()
                            lastHandledIndex = downSlot
                            currentOnSelect(downSlot)

                            settleJob = scope.launch {
                                launch { stretchAnim.animateTo(0f, tween(120)) }
                                if (tappedDifferent && !startedFromInactive && !currentReduce) {
                                    // Brief refractive lens swell while gliding across to the tapped tab.
                                    val glide = launch {
                                        pillPos.animateTo(
                                            targetValue = downSlot.toFloat(),
                                            animationSpec = spring(dampingRatio = 0.72f, stiffness = 340f),
                                        )
                                    }
                                    pressAnim.animateTo(
                                        targetValue = 0.85f,
                                        animationSpec = tween(durationMillis = 95, easing = YYMotion.EaseOut),
                                    )
                                    pressAnim.animateTo(
                                        targetValue = 0f,
                                        animationSpec = spring(dampingRatio = 0.82f, stiffness = 300f),
                                    )
                                    glide.join()
                                } else {
                                    if (currentReduce || startedFromInactive) {
                                        pillPos.snapTo(downSlot.toFloat())
                                    } else {
                                        launch {
                                            pillPos.animateTo(
                                                targetValue = downSlot.toFloat(),
                                                animationSpec = spring(dampingRatio = 0.76f, stiffness = 380f),
                                            )
                                        }
                                    }
                                    pressAnim.animateTo(
                                        targetValue = 0f,
                                        animationSpec = spring(dampingRatio = 0.82f, stiffness = 340f),
                                    )
                                }
                            }
                        }
                    }
                }
            },
        contentAlignment = Alignment.CenterStart,
    ) {
        val innerPad = 4.dp
        val slotWidth = ((maxWidth - innerPad * 2) / tabCount.coerceAtLeast(1)).coerceAtLeast(1.dp)
        val slotWidthPx = with(density) { slotWidth.toPx() }
        val isLtr = layoutDirection == LayoutDirection.Ltr

        // Layer 1: Outer Liquid Glass capsule, recorded into `barBackdrop` for the sliding lens.
        Box(
            Modifier
                .matchParentSize()
                .layerBackdrop(barBackdrop)
                .drawBackdrop(
                    backdrop = backdrop,
                    shape = { CircleShape },
                    effects = {
                        vibrancy()
                        blur(8.dp.toPx())
                        lens(
                            refractionHeight = 20.dp.toPx(),
                            refractionAmount = 24.dp.toPx(),
                        )
                    },
                    highlight = {
                        if (dark) Highlight.Default.copy(alpha = 0.65f) else Highlight.Default
                    },
                    shadow = {
                        Shadow(
                            radius = 18.dp,
                            offset = DpOffset(0.dp, 6.dp),
                            color = outerShadowColor,
                        )
                    },
                    onDrawSurface = { drawRect(outerSurfaceColor) },
                ),
        )

        // Layer 2: Unselected base tabs (visible everywhere outside the floating lens).
        Row(
            Modifier
                .fillMaxSize()
                .padding(innerPad),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            for (i in 0 until tabCount) {
                Box(
                    Modifier
                        .weight(1f)
                        .fillMaxHeight()
                        .semantics(mergeDescendants = true) {
                            role = Role.Tab
                            selected = (i == selectedIndex)
                            tabLabels.getOrNull(i)?.let { contentDescription = it }
                            onClick {
                                onSelect(i)
                                true
                            }
                        },
                    contentAlignment = Alignment.Center,
                ) {
                    Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                        tabContent(i, false)
                    }
                }
            }
        }

        // Layer 3: Accent-tinted active tabs recorded offscreen into `activeTabsBackdrop`
        // (matching Layer 2 slot geometry 1:1) so the floating lens reveals & refracts active icons/labels.
        Row(
            Modifier
                .fillMaxSize()
                .padding(innerPad)
                .drawWithContent {
                    clipRect(0f, 0f, 0f, 0f) {
                        this@drawWithContent.drawContent()
                    }
                }
                .layerBackdrop(activeTabsBackdrop),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            for (i in 0 until tabCount) {
                Box(
                    Modifier
                        .weight(1f)
                        .fillMaxHeight(),
                    contentAlignment = Alignment.Center,
                ) {
                    Row(Modifier.fillMaxSize(), verticalAlignment = Alignment.CenterVertically) {
                        tabContent(i, true)
                    }
                }
            }
        }

        // Layer 4: Floating draggable refractive Liquid Glass lens indicator.
        if (pillAlpha > 0.005f) {
            Box(
                Modifier
                    .padding(innerPad)
                    .width(slotWidth)
                    .fillMaxHeight()
                    .drawBackdrop(
                        backdrop = indicatorBackdrop,
                        shape = { CircleShape },
                        effects = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            if (p > 0.005f) {
                                lens(
                                    refractionHeight = 14.dp.toPx() * p,
                                    refractionAmount = 22.dp.toPx() * p,
                                    depthEffect = true,
                                    chromaticAberration = true,
                                )
                            }
                        },
                        highlight = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            val baseAlpha = if (dark) 0.22f else 0.42f
                            Highlight.Default.copy(alpha = lerp(baseAlpha, 1f, p) * pillAlpha)
                        },
                        shadow = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            if (p > 0.01f) {
                                Shadow(
                                    radius = 18.dp,
                                    offset = DpOffset(0.dp, 6.dp),
                                    color = lensShadowColor.copy(alpha = lensShadowColor.alpha * p * pillAlpha),
                                )
                            } else {
                                null
                            }
                        },
                        innerShadow = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            if (p > 0.01f) {
                                InnerShadow(
                                    radius = 8.dp * p,
                                    offset = DpOffset(0.dp, 2.dp * p),
                                    color = Color.Black.copy(alpha = 0.10f * p * pillAlpha),
                                )
                            } else {
                                null
                            }
                        },
                        layerBlock = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            val pos = if (isActivelyDragging) liveDragPos else pillPos.value
                            val stretch = if (isActivelyDragging) liveDragStretch else stretchAnim.value
                            val x = pos * slotWidthPx
                            translationX = if (isLtr) x else -x
                            val baseScaleX = lerp(1f, 1.15f, p)
                            val baseScaleY = lerp(1f, 1.34f, p)
                            scaleX = baseScaleX * (1f + stretch)
                            scaleY = baseScaleY / (1f + stretch * 0.55f)
                            alpha = pillAlpha
                        },
                        onDrawBackdrop = { drawBackdrop ->
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            val zoom = 1f + 0.07f * p
                            if (zoom > 1.001f) {
                                scale(
                                    scaleX = zoom,
                                    scaleY = zoom,
                                    pivot = Offset(size.width * 0.5f, size.height * 0.5f),
                                ) {
                                    drawBackdrop()
                                }
                            } else {
                                drawBackdrop()
                            }
                        },
                        onDrawSurface = {
                            val p = pressAnim.value.coerceIn(0f, 1f)
                            if (p > 0.01f) {
                                drawRect(lensSurfaceTint.copy(alpha = lensSurfaceTint.alpha * p))
                            }
                        },
                    ),
            )
        }
    }
}

/**
 * Floating circular Liquid Glass orb (used for the independent 搜索 tab orb on the right)
 * backed by Kyant0 Backdrop with press-to-refract swell and chromatic rim dispersion.
 */
@Composable
fun LiquidGlassOrb(
    backdrop: Backdrop,
    selected: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    content: @Composable BoxScope.() -> Unit,
) {
    val dark = YY.dark
    val tintFill = YY.tint.fill
    val reduce = YYMotion.isReduceMotion()
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val pressProgress by animateFloatAsState(
        targetValue = if (pressed && !reduce) 1f else 0f,
        animationSpec = if (reduce) snap() else spring(dampingRatio = 0.72f, stiffness = 420f),
        label = "liquidOrbPress",
    )

    val surfaceColor = remember(dark, selected, tintFill) {
        when {
            selected && dark -> Color(0xFF2B2A29).copy(alpha = 0.62f)
            selected -> Color(0xFFFAF9F7).copy(alpha = 0.54f)
            dark -> Color(0xFF1F1E1D).copy(alpha = 0.56f)
            else -> Color(0xFFFAF9F7).copy(alpha = 0.48f)
        }
    }
    val selectedTint = remember(dark, tintFill) {
        if (dark) Color.White.copy(alpha = 0.12f) else tintFill.copy(alpha = 0.12f)
    }
    val shadowColor = remember(dark) {
        Color.Black.copy(alpha = if (dark) 0.40f else 0.12f)
    }

    Box(
        modifier = modifier
            .drawBackdrop(
                backdrop = backdrop,
                shape = { CircleShape },
                effects = {
                    val p = pressProgress
                    vibrancy()
                    blur(lerp(8.dp.toPx(), 4.dp.toPx(), p))
                    lens(
                        refractionHeight = lerp(16.dp.toPx(), 20.dp.toPx(), p),
                        refractionAmount = lerp(18.dp.toPx(), 26.dp.toPx(), p),
                        depthEffect = p > 0.05f,
                        chromaticAberration = p > 0.05f,
                    )
                },
                highlight = {
                    val baseAlpha = if (dark) 0.65f else 1f
                    Highlight.Default.copy(alpha = lerp(baseAlpha, 1f, pressProgress))
                },
                shadow = {
                    Shadow(
                        radius = lerp(18f, 22f, pressProgress).dp,
                        offset = DpOffset(0.dp, 6.dp),
                        color = shadowColor,
                    )
                },
                layerBlock = {
                    val s = lerp(1f, 1.12f, pressProgress)
                    scaleX = s
                    scaleY = s
                },
                onDrawSurface = {
                    drawRect(surfaceColor)
                    if (selected) {
                        val restAlpha = (1f - pressProgress * 0.6f).coerceIn(0f, 1f)
                        drawRect(selectedTint.copy(alpha = selectedTint.alpha * restAlpha))
                    }
                },
            )
            .selectable(
                selected = selected,
                interactionSource = interaction,
                indication = null,
                role = Role.Tab,
                onClick = onClick,
            ),
        contentAlignment = Alignment.Center,
        content = content,
    )
}
