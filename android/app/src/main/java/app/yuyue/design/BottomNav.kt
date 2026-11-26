package app.yuyue.design

import android.os.Build
import android.view.HapticFeedbackConstants
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.animateContentSize
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.snap
import androidx.compose.animation.core.spring
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.blur
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.PointerEventPass
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChange
import androidx.compose.ui.layout.boundsInRoot
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.zIndex
import app.yuyue.LocalTabBarHeight
import app.yuyue.t
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.hypot
import kotlin.math.min

/**
 * Every destination that can appear in the phone's bottom navigation bar.
 * Shares canonical IDs and normalization rules with iOS (`PhoneBottomNav.swift`).
 */
enum class BottomTab(val id: String) {
    Shelf("shelf"),
    Notes("notes"),
    Podcasts("podcasts"),
    Audio("audio"),
    Progress("progress"),
    Search("search"),
    Me("me");

    val title: String
        get() = when (this) {
            Shelf -> t("书架")
            Notes -> t("笔记")
            Podcasts -> t("播客")
            Audio -> t("有声")
            Progress -> t("进度")
            Search -> t("搜索")
            Me -> t("我的")
        }

    val subtitle: String
        get() = when (this) {
            Shelf -> t("书、进度、播客、有声与合集")
            Notes -> t("划线、想法、回顾与生词")
            Podcasts -> t("订阅频道与单集收听")
            Audio -> t("有声书与听读同步")
            Progress -> t("在读、想读与读过记录")
            Search -> t("搜书、正文、笔记与问书伴")
            Me -> t("阅读统计与全部设置")
        }

    val icon: ImageVector
        get() = when (this) {
            Shelf -> TabIcons.shelf
            Notes -> TabIcons.notes
            Podcasts -> Icons.radio
            Audio -> Icons.headphones
            Progress -> Icons.bookmarkOutline
            Search -> TabIcons.search
            Me -> TabIcons.me
        }

    val iconOn: ImageVector
        get() = when (this) {
            Shelf -> TabIcons.shelfOn
            Notes -> TabIcons.notesOn
            Podcasts -> Icons.radioFill
            Audio -> Icons.headphones
            Progress -> Icons.bookmark
            Search -> TabIcons.searchOn
            Me -> TabIcons.meOn
        }

    /** True for destinations that can fold under 书架 ⌃⌄ when not placed directly on the bottom bar. */
    val isShelfSub: Boolean get() = this == Progress || this == Podcasts || this == Audio || this == Notes

    val pinned: Boolean get() = this == Me

    companion object {
        fun of(id: String): BottomTab? = entries.firstOrNull { it.id == id }
    }
}

/**
 * Immutable snapshot of both bottom bar slots and shelf-folded preferences.
 * Mutual exclusion holds automatically via [BottomNav.foldedInShelf]: any shelf sub-entry promoted
 * to [tabs] disappears from 书架 ⌃⌄; removing it returns it to 书架 ⌃⌄ unless listed in [hiddenShelfSubs].
 */
@Immutable
data class BottomNavConfig(
    val tabs: List<BottomTab>,
    val hiddenShelfSubs: Set<BottomTab> = emptySet(),
)

/** Shared persistence, validation, shelf-fold coordination, mutation, and quick presets. */
object BottomNav {
    const val KEY = "appearance.bottomTabs"
    const val SHELF_HIDDEN_KEY = "appearance.shelfHiddenPlaces"
    const val MIN_TABS = 2
    const val MAX_TABS = 5

    val DEFAULT: List<BottomTab> = listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Me, BottomTab.Search)
    val DEFAULT_RAW: String = serialize(DEFAULT)
    const val DEFAULT_HIDDEN_SHELF_RAW: String = ""

    /** Shelf sub-places in PlacePanel's canonical order after 书. */
    val SHELF_SUB_TABS: List<BottomTab> = listOf(BottomTab.Progress, BottomTab.Podcasts, BottomTab.Audio, BottomTab.Notes)

    data class Preset(
        val id: String,
        val name: String,
        val tabs: List<BottomTab>,
        val hiddenShelfSubs: Set<BottomTab> = emptySet(),
    )

    val presets: List<Preset>
        get() = listOf(
            Preset("default", t("标准"), DEFAULT, emptySet()),
            Preset("minimal", t("极简"), listOf(BottomTab.Shelf, BottomTab.Me), SHELF_SUB_TABS.toSet()),
            Preset("podcast", t("播客"), listOf(BottomTab.Shelf, BottomTab.Podcasts, BottomTab.Me, BottomTab.Search), emptySet()),
            Preset("audio", t("听书"), listOf(BottomTab.Shelf, BottomTab.Audio, BottomTab.Podcasts, BottomTab.Me), emptySet()),
        )

    fun serialize(tabs: List<BottomTab>): String = tabs.joinToString(",") { it.id }

    fun parseHiddenShelfSubs(raw: String?): Set<BottomTab> =
        raw.orEmpty()
            .split(',')
            .mapNotNull { BottomTab.of(it.trim()) }
            .filter { it.isShelfSub }
            .toSet()

    fun serializeHiddenShelfSubs(hidden: Set<BottomTab>): String =
        SHELF_SUB_TABS.filter { it in hidden }.joinToString(",") { it.id }

    fun config(tabsRaw: String?, hiddenShelfRaw: String?): BottomNavConfig =
        BottomNavConfig(parse(tabsRaw), parseHiddenShelfSubs(hiddenShelfRaw))

    /**
     * Parses and normalizes any stored string so invariants always hold:
     * - Known unique entries only
     * - [BottomTab.Me] is always present (settings stay reachable)
     * - Count clamped to [MIN_TABS]..[MAX_TABS]
     */
    fun parse(raw: String?): List<BottomTab> {
        val parsed = raw
            .orEmpty()
            .split(',')
            .mapNotNull { BottomTab.of(it.trim()) }
            .distinct()
            .toMutableList()
        if (parsed.isEmpty()) return DEFAULT
        if (BottomTab.Me !in parsed) parsed.add(BottomTab.Me)
        if (parsed.size > MAX_TABS) {
            val trimmed = parsed.take(MAX_TABS).toMutableList()
            if (BottomTab.Me !in trimmed) {
                trimmed[MAX_TABS - 1] = BottomTab.Me
            }
            parsed.clear()
            parsed.addAll(trimmed)
        }
        if (parsed.size < MIN_TABS) {
            val fallback = BottomTab.entries.first { it !in parsed }
            parsed.add(0, fallback)
        }
        return parsed
    }

    /** Entries currently folded inside 书架 ⌃⌄ (strictly mutually exclusive with [BottomNavConfig.tabs]). */
    fun foldedInShelf(config: BottomNavConfig): List<BottomTab> {
        if (BottomTab.Shelf !in config.tabs) return emptyList()
        return SHELF_SUB_TABS.filter { it !in config.tabs && it !in config.hiddenShelfSubs }
    }

    /** Entries currently hidden from both the bottom bar and 书架 ⌃⌄. */
    fun hiddenEntries(config: BottomNavConfig): List<BottomTab> {
        val folded = foldedInShelf(config).toSet()
        return BottomTab.entries.filter { !it.pinned && it !in config.tabs && it !in folded }
    }

    /** Whether 书架 has anything folded inside it (sub-places or collections) to warrant ⌃⌄ / ⌄. */
    fun shelfHasSwitcher(config: BottomNavConfig, hasCollections: Boolean): Boolean =
        BottomTab.Shelf in config.tabs && (foldedInShelf(config).isNotEmpty() || hasCollections)

    fun activePresetId(config: BottomNavConfig): String? {
        for (preset in presets) {
            if (preset.tabs != config.tabs) continue
            val subsMatch = SHELF_SUB_TABS.all { sub ->
                sub in config.tabs || ((sub in config.hiddenShelfSubs) == (sub in preset.hiddenShelfSubs))
            }
            if (subsMatch) return preset.id
        }
        return null
    }

    fun isDefault(config: BottomNavConfig): Boolean = activePresetId(config) == "default"

    fun hidden(active: List<BottomTab>): List<BottomTab> =
        BottomTab.entries.filter { it !in active }

    fun canRemove(active: List<BottomTab>, tab: BottomTab): Boolean =
        !tab.pinned && active.size > MIN_TABS && tab in active

    fun remove(active: List<BottomTab>, tab: BottomTab): List<BottomTab> {
        if (!canRemove(active, tab)) return active
        return parse(serialize(active.filterNot { it == tab }))
    }

    /** Adds [tab] if space remains; inserts right before trailing Search so the search orb stays trailing. */
    fun add(active: List<BottomTab>, tab: BottomTab, atIndex: Int? = null): List<BottomTab> {
        if (tab in active || active.size >= MAX_TABS) return active
        val out = active.toMutableList()
        if (atIndex != null) {
            out.add(atIndex.coerceIn(0, out.size), tab)
        } else if (tab != BottomTab.Search && out.lastOrNull() == BottomTab.Search) {
            out.add(out.lastIndex, tab)
        } else {
            out.add(tab)
        }
        return parse(serialize(out))
    }

    /** Replaces [target] in-place with [replacement] (or swaps positions if [replacement] is already active). */
    fun replace(active: List<BottomTab>, target: BottomTab, replacement: BottomTab): List<BottomTab> {
        if (target == replacement) return active
        val targetIdx = active.indexOf(target)
        if (targetIdx < 0) return active
        val existingIdx = active.indexOf(replacement)
        val out = active.toMutableList()
        if (existingIdx >= 0) {
            out[targetIdx] = replacement
            out[existingIdx] = target
        } else {
            if (target.pinned) return active
            out[targetIdx] = replacement
        }
        return parse(serialize(out))
    }

    fun move(active: List<BottomTab>, from: Int, to: Int): List<BottomTab> {
        if (from == to || from !in active.indices || to !in active.indices) return active
        val out = active.toMutableList()
        val item = out.removeAt(from)
        out.add(to, item)
        return out
    }

    // ── Config-level mutations (keep bottom bar and 书架 ⌃⌄ fold state strictly coordinated) ──

    fun insert(config: BottomNavConfig, tab: BottomTab, atIndex: Int? = null): BottomNavConfig {
        if (tab in config.tabs || config.tabs.size >= MAX_TABS) return config
        return config.copy(tabs = add(config.tabs, tab, atIndex))
    }

    fun replace(config: BottomNavConfig, target: BottomTab, replacement: BottomTab): BottomNavConfig {
        if (target == replacement) return config
        val targetIdx = config.tabs.indexOf(target)
        if (targetIdx < 0) return config
        if (replacement in config.tabs) {
            return config.copy(tabs = replace(config.tabs, target, replacement))
        }
        if (target.pinned) return config
        val nextTabs = replace(config.tabs, target, replacement)
        val nextHidden = config.hiddenShelfSubs.toMutableSet()
        if (target.isShelfSub) {
            if (BottomTab.Shelf in nextTabs) nextHidden.remove(target) else nextHidden.add(target)
        }
        return BottomNavConfig(nextTabs, nextHidden)
    }

    fun move(config: BottomNavConfig, from: Int, to: Int): BottomNavConfig =
        config.copy(tabs = move(config.tabs, from, to))

    fun foldIntoShelf(config: BottomNavConfig, tab: BottomTab): BottomNavConfig {
        if (!tab.isShelfSub || BottomTab.Shelf !in config.tabs) return config
        if (tab in config.tabs && !canRemove(config.tabs, tab)) return config
        val nextTabs = if (tab in config.tabs) remove(config.tabs, tab) else config.tabs
        return BottomNavConfig(nextTabs, config.hiddenShelfSubs - tab)
    }

    fun hide(config: BottomNavConfig, tab: BottomTab): BottomNavConfig {
        if (tab.pinned) return config
        if (tab in config.tabs && !canRemove(config.tabs, tab)) return config
        val nextTabs = if (tab in config.tabs) remove(config.tabs, tab) else config.tabs
        val nextHidden = if (tab.isShelfSub) config.hiddenShelfSubs + tab else config.hiddenShelfSubs
        return BottomNavConfig(nextTabs, nextHidden)
    }

    fun applyPreset(preset: Preset): BottomNavConfig =
        BottomNavConfig(preset.tabs, preset.hiddenShelfSubs)

    fun usesDetachedSearchOrb(tabs: List<BottomTab>): Boolean =
        tabs.size >= 3 && tabs.lastOrNull() == BottomTab.Search

    fun summary(raw: String?): String = parse(raw).joinToString(" · ") { it.title }
}

// ── v4 Direct-Manipulation Settings UI (我的 › 外观 › 底部导航) ──

private enum class DragSource { Bar, Shelf, Hidden }
private enum class DragMode { Rail, Ghost }

private sealed interface DropIntent {
    data object Reorder : DropIntent
    data class Insert(val index: Int) : DropIntent
    data class Replace(val target: BottomTab) : DropIntent
    data class MoveTo(val index: Int) : DropIntent
    data object Fold : DropIntent
    data object Hide : DropIntent
}

private data class PopoverAnchor(
    val tab: BottomTab,
    val source: DragSource,
    val boundsInRoot: Rect,
)

/**
 * 我的 › 外观 › 底部导航 (v4):
 * - Top atmospheric warm-paper stage with directly draggable Liquid Glass / Classic bottom bar
 *   (1:1 horizontal rail reorder, pinned 我的 rail-lock, fixed-point zero-jitter magnetic insertion,
 *   full-bar direct replacement, tap slot for in-page frosted replace/fold/hide popover).
 * - Minimal preset strip (标准 · 极简 · 播客 · 听书).
 * - 书架内收起 card mirroring 书架 ⌃⌄ with 1-tap promote, × quick-hide, and drag-to-insert.
 * - Quiet dashed 已隐藏 recovery zone.
 */
@Composable
fun BottomNavPage(collectionCount: Int = 0) {
    var rawTabs by rememberPref(BottomNav.KEY, BottomNav.DEFAULT_RAW)
    var rawHiddenSubs by rememberPref(BottomNav.SHELF_HIDDEN_KEY, BottomNav.DEFAULT_HIDDEN_SHELF_RAW)
    val liquidChosen by rememberPref(LiquidGlass.KEY, false)
    val eink by rememberPref("eink.on", false)
    val previewLiquid = LiquidGlass.supported && liquidChosen && !eink
    val reduce = YYMotion.isReduceMotion()

    val config = remember(rawTabs, rawHiddenSubs) { BottomNav.config(rawTabs, rawHiddenSubs) }
    val folded = remember(config) { BottomNav.foldedInShelf(config) }
    val hidden = remember(config) { BottomNav.hiddenEntries(config) }
    val shelfOn = BottomTab.Shelf in config.tabs
    val shelfHasSwitcher = remember(config, collectionCount) {
        BottomNav.shelfHasSwitcher(config, hasCollections = collectionCount > 0)
    }
    val curPresetId = remember(config) { BottomNav.activePresetId(config) }
    val detachedSearch = previewLiquid && BottomNav.usesDetachedSearchOrb(config.tabs)
    val trackTabs = if (detachedSearch) config.tabs.dropLast(1) else config.tabs

    val view = LocalView.current
    val density = LocalDensity.current
    val scope = rememberCoroutineScope()

    // Coordinates in root space for zero-recomposition hit testing during drag.
    var rootBounds by remember { mutableStateOf(Rect.Zero) }
    var heroBounds by remember { mutableStateOf(Rect.Zero) }
    var trackBounds by remember { mutableStateOf(Rect.Zero) }
    var orbBounds by remember { mutableStateOf(Rect.Zero) }
    var shelfZoneBounds by remember { mutableStateOf(Rect.Zero) }
    var hiddenZoneBounds by remember { mutableStateOf(Rect.Zero) }

    // Live drag state (kept lightweight so only graphicsLayer / badges update at 60-120fps).
    var draggingTab by remember { mutableStateOf<BottomTab?>(null) }
    var dragSource by remember { mutableStateOf<DragSource?>(null) }
    var dragMode by remember { mutableStateOf(DragMode.Rail) }
    var ghostPosInRoot by remember { mutableStateOf(Offset.Zero) }
    var railDragX by remember { mutableFloatStateOf(0f) }
    var railDragY by remember { mutableFloatStateOf(0f) }
    val railSettleX = remember { Animatable(0f) }
    var lastSettledTab by remember { mutableStateOf<BottomTab?>(null) }

    var dropIntent by remember { mutableStateOf<DropIntent?>(null) }
    var placeholderIndex by remember { mutableIntStateOf(-1) }
    var replaceHoverTab by remember { mutableStateOf<BottomTab?>(null) }
    var replaceBadgeLabel by remember { mutableStateOf<String?>(null) }
    var heroDropActive by remember { mutableStateOf(false) }
    var shelfDropHover by remember { mutableStateOf(false) }
    var hiddenDropHover by remember { mutableStateOf(false) }
    var liveHint by remember { mutableStateOf<String?>(null) }

    var popover by remember { mutableStateOf<PopoverAnchor?>(null) }
    var toastMsg by remember { mutableStateOf<String?>(null) }
    var toastNonce by remember { mutableIntStateOf(0) }

    LaunchedEffect(toastNonce) {
        if (toastMsg != null) {
            delay(2100)
            toastMsg = null
        }
    }

    val currentConfig by rememberUpdatedState(config)
    val currentTrackTabs by rememberUpdatedState(trackTabs)
    val currentDetached by rememberUpdatedState(detachedSearch)

    fun tick() {
        val haptic = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            HapticFeedbackConstants.SEGMENT_FREQUENT_TICK
        } else {
            HapticFeedbackConstants.CLOCK_TICK
        }
        view.performHapticFeedback(haptic)
    }

    fun showToast(msg: String) {
        toastMsg = msg
        toastNonce++
    }

    fun commit(next: BottomNavConfig, feedback: String? = null) {
        val nextTabsRaw = BottomNav.serialize(next.tabs)
        val nextHiddenRaw = BottomNav.serializeHiddenShelfSubs(next.hiddenShelfSubs)
        if (nextTabsRaw != rawTabs || nextHiddenRaw != rawHiddenSubs) {
            rawTabs = nextTabsRaw
            rawHiddenSubs = nextHiddenRaw
            tick()
        }
        if (feedback != null) showToast(feedback)
    }

    fun clearDragVisuals() {
        draggingTab = null
        dragSource = null
        dragMode = DragMode.Rail
        railDragX = 0f
        railDragY = 0f
        dropIntent = null
        placeholderIndex = -1
        replaceHoverTab = null
        replaceBadgeLabel = null
        heroDropActive = false
        shelfDropHover = false
        hiddenDropHover = false
        liveHint = null
    }

    fun padRect(r: Rect, padPx: Float): Rect =
        if (r.isEmpty) r else Rect(r.left - padPx, r.top - padPx, r.right + padPx, r.bottom + padPx)

    /** Evaluate ghost drop zone in root coordinates — mathematically jitter-free fixed-point buckets. */
    fun updateGhostTarget(tab: BottomTab, src: DragSource, pos: Offset) {
        val cfg = currentConfig
        val slots = currentTrackTabs
        val pad10 = with(density) { 10.dp.toPx() }
        val pad6 = with(density) { 6.dp.toPx() }
        val pad12 = with(density) { 12.dp.toPx() }
        val pullBelowPx = with(density) { 24.dp.toPx() }

        heroDropActive = false
        shelfDropHover = false
        hiddenDropHover = false
        replaceHoverTab = null
        replaceBadgeLabel = null
        dropIntent = null

        // 1. Over Hero Bottom Bar?
        if (!heroBounds.isEmpty && padRect(heroBounds, pad10).contains(pos)) {
            heroDropActive = true

            // Check detached Search orb first
            if (currentDetached && !orbBounds.isEmpty && padRect(orbBounds, pad6).contains(pos)) {
                placeholderIndex = -1
                if (tab == BottomTab.Search) {
                    dropIntent = DropIntent.Reorder
                    liveHint = t("松手保持「搜索」独立圆球")
                    return
                }
                replaceHoverTab = BottomTab.Search
                replaceBadgeLabel = if (src == DragSource.Bar) t("互换位置") else t("替换 %@", BottomTab.Search.title)
                dropIntent = DropIntent.Replace(BottomTab.Search)
                liveHint = if (src == DragSource.Bar) {
                    t("松手与「%@」互换位置", BottomTab.Search.title)
                } else {
                    t("松手用「%@」替换「%@」", tab.title, BottomTab.Search.title)
                }
                return
            }

            // Re-entering bar while dragging from bar (e.g. detached Search orb dragged left or changed mind)
            if (src == DragSource.Bar) {
                placeholderIndex = -1
                if (!trackBounds.isEmpty && slots.isNotEmpty()) {
                    val rel = ((pos.x - trackBounds.left) / trackBounds.width.coerceAtLeast(1f)).coerceIn(0f, 0.9999f)
                    val targetIdx = (rel * slots.size).toInt().coerceIn(0, slots.lastIndex)
                    val targetTab = slots[targetIdx]
                    if (targetTab == tab) {
                        dropIntent = DropIntent.Reorder
                        liveHint = t("松手将「%@」放回原位", tab.title)
                    } else {
                        replaceHoverTab = targetTab
                        replaceBadgeLabel = t("移到此处")
                        dropIntent = DropIntent.MoveTo(targetIdx)
                        liveHint = t("松手将「%@」移到第 %lld 格", tab.title, targetIdx + 1)
                    }
                }
                return
            }

            // Dragging FROM shelf/hidden INTO bottom bar:
            // Full (5/5) -> direct Replace; Not full (<5) -> fixed-point magnetic Placeholder (zero jitter!).
            if (!trackBounds.isEmpty && slots.isNotEmpty()) {
                val rel = ((pos.x - trackBounds.left) / trackBounds.width.coerceAtLeast(1f)).coerceIn(0f, 0.9999f)
                if (cfg.tabs.size >= BottomNav.MAX_TABS) {
                    placeholderIndex = -1
                    val targetIdx = (rel * slots.size).toInt().coerceIn(0, slots.lastIndex)
                    val targetTab = slots[targetIdx]
                    if (!targetTab.pinned) {
                        replaceHoverTab = targetTab
                        replaceBadgeLabel = t("替换 %@", targetTab.title)
                        dropIntent = DropIntent.Replace(targetTab)
                        liveHint = t("底栏已满 · 松手替换「%@」", targetTab.title)
                    } else {
                        liveHint = t("「我的」常驻底栏，请移到其他槽位替换")
                    }
                } else {
                    val bucket = (rel * (slots.size + 1)).toInt().coerceIn(0, slots.size)
                    if (placeholderIndex != bucket) {
                        placeholderIndex = bucket
                        tick()
                    }
                    dropIntent = DropIntent.Insert(bucket)
                    liveHint = t("松手插入第 %lld 格（轻点已有槽位可直接替换）", bucket + 1)
                }
            }
            return
        }

        // Left hero bar -> smoothly collapse placeholder
        placeholderIndex = -1

        // 2. Over 书架内收起 card (eligible for shelfSub only)
        if (!shelfZoneBounds.isEmpty &&
            padRect(shelfZoneBounds, with(density) { 4.dp.toPx() }).contains(pos) &&
            tab.isShelfSub &&
            BottomTab.Shelf in cfg.tabs &&
            src != DragSource.Shelf
        ) {
            shelfDropHover = true
            dropIntent = DropIntent.Fold
            liveHint = t("松手收起「%@」到书架 ⌃⌄", tab.title)
            return
        }

        // 3. Over 已隐藏 zone (or pulled clearly below hero stage for non-shelfSub bar items)
        val pulledDownToHide = src == DragSource.Bar && !tab.isShelfSub && !heroBounds.isEmpty && pos.y > heroBounds.bottom + pullBelowPx
        if ((!hiddenZoneBounds.isEmpty && padRect(hiddenZoneBounds, pad12).contains(pos) || pulledDownToHide) &&
            !tab.pinned &&
            src != DragSource.Hidden
        ) {
            hiddenDropHover = true
            dropIntent = DropIntent.Hide
            liveHint = t("松手隐藏「%@」", tab.title)
            return
        }

        liveHint = null
    }

    fun executeDrop(tab: BottomTab, src: DragSource, intent: DropIntent?) {
        val cfg = currentConfig
        when (intent) {
            is DropIntent.Insert -> {
                val msg = if (tab.isShelfSub) {
                    t("「%@」已放到底栏（自动移出书架收起）", tab.title)
                } else {
                    t("「%@」已放到底栏", tab.title)
                }
                commit(BottomNav.insert(cfg, tab, intent.index), msg)
            }
            is DropIntent.Replace -> {
                val msg = if (tab in cfg.tabs) {
                    t("已互换「%@」与「%@」", intent.target.title, tab.title)
                } else {
                    t("已将「%@」替换为「%@」", intent.target.title, tab.title)
                }
                commit(BottomNav.replace(cfg, intent.target, tab), msg)
            }
            is DropIntent.MoveTo -> {
                val curIdx = cfg.tabs.indexOf(tab)
                if (curIdx >= 0) commit(BottomNav.move(cfg, curIdx, intent.index))
            }
            DropIntent.Fold -> {
                if (tab in cfg.tabs && !BottomNav.canRemove(cfg.tabs, tab)) {
                    showToast(t("底栏至少保留 2 个入口"))
                } else {
                    commit(BottomNav.foldIntoShelf(cfg, tab), t("「%@」已收起进书架 ⌃⌄", tab.title))
                }
            }
            DropIntent.Hide -> {
                if (tab in cfg.tabs && !BottomNav.canRemove(cfg.tabs, tab)) {
                    showToast(t("底栏至少保留 2 个入口，可直接拖新入口覆盖替换"))
                } else {
                    commit(BottomNav.hide(cfg, tab), t("已隐藏「%@」", tab.title))
                }
            }
            DropIntent.Reorder, null -> Unit
        }
    }

    fun handleTap(tab: BottomTab, src: DragSource, anchorBounds: Rect) {
        val cfg = currentConfig
        if (src == DragSource.Bar) {
            popover = PopoverAnchor(tab, src, anchorBounds)
        } else if (cfg.tabs.size < BottomNav.MAX_TABS) {
            val msg = if (tab.isShelfSub) {
                t("「%@」已放到底栏（自动移出书架收起）", tab.title)
            } else {
                t("「%@」已放到底栏", tab.title)
            }
            commit(BottomNav.insert(cfg, tab), msg)
        } else {
            popover = PopoverAnchor(tab, src, anchorBounds)
        }
    }

    // Drop-readiness state while dragging a non-pinned entry
    val activeDragTab = draggingTab
    val activeDragSrc = dragSource
    val shelfZoneDropReady = activeDragTab != null && !activeDragTab.pinned && activeDragTab.isShelfSub && shelfOn && activeDragSrc != DragSource.Shelf
    val shelfZoneIneligible = activeDragTab != null && !activeDragTab.pinned && !activeDragTab.isShelfSub && activeDragSrc == DragSource.Bar
    val hiddenZoneDropReady = activeDragTab != null && !activeDragTab.pinned && activeDragSrc != DragSource.Hidden

    val defaultHint = t("左右拖动排序 · 向下拖出移走 · 轻点槽位替换")

    Box(
        Modifier
            .fillMaxSize()
            .onGloballyPositioned { rootBounds = it.boundsInRoot() },
    ) {
        val scrollState = rememberScrollState()
        Column(
            Modifier
                .fillMaxSize()
                .verticalScroll(scrollState, enabled = draggingTab == null)
                .padding(horizontal = YY.s(14))
                .padding(top = YY.s(6), bottom = LocalTabBarHeight.current + YY.s(28)),
            verticalArrangement = Arrangement.spacedBy(YY.s(14)),
        ) {
            // ── Zone 1: Atmospheric Warm-Paper Live Hero Stage ──
            HeroBottomBarStage(
                trackTabs = trackTabs,
                detachedSearch = detachedSearch,
                liquid = previewLiquid,
                shelfHasSwitcher = shelfHasSwitcher,
                eink = eink,
                dropActive = heroDropActive,
                draggingTab = draggingTab,
                dragMode = dragMode,
                railDragX = railDragX,
                railDragY = railDragY,
                railSettleX = railSettleX.value,
                lastSettledTab = lastSettledTab,
                placeholderIndex = placeholderIndex,
                placeholderTitle = draggingTab?.title,
                replaceHoverTab = replaceHoverTab,
                replaceBadgeLabel = replaceBadgeLabel,
                menuOpenTab = if (popover?.source == DragSource.Bar) popover?.tab else null,
                hintText = liveHint ?: defaultHint,
                hintEmphasized = liveHint != null,
                onHeroBounds = { heroBounds = it },
                onTrackBounds = { trackBounds = it },
                onOrbBounds = { orbBounds = it },
                onSlotTap = { tab, bounds -> handleTap(tab, DragSource.Bar, bounds) },
                onDragStartRail = { tab ->
                    popover = null
                    draggingTab = tab
                    dragSource = DragSource.Bar
                    dragMode = DragMode.Rail
                    railDragX = 0f
                    railDragY = 0f
                    view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                },
                onRailDelta = { tab, totalDx, totalDy, slotWidthPx, isDetachedOrb, pointerRoot ->
                    val pullThresholdPx = with(density) { 42.dp.toPx() }
                    val orbDetachLeftPx = with(density) { -24.dp.toPx() }
                    if (dragMode == DragMode.Rail) {
                        if (totalDy > pullThresholdPx) {
                            if (tab.pinned) {
                                val extra = totalDy - pullThresholdPx
                                val rubberY = min(with(density) { 18.dp.toPx() }, extra * 0.18f + with(density) { 8.dp.toPx() })
                                railDragY = rubberY
                                liveHint = t("「我的」常驻底栏，可左右拖动调整位置")
                                return@HeroBottomBarStage 0f
                            }
                            dragMode = DragMode.Ghost
                            ghostPosInRoot = pointerRoot
                            updateGhostTarget(tab, DragSource.Bar, pointerRoot)
                            return@HeroBottomBarStage 0f
                        }
                        if (isDetachedOrb) {
                            if (totalDx < orbDetachLeftPx) {
                                dragMode = DragMode.Ghost
                                ghostPosInRoot = pointerRoot
                                updateGhostTarget(tab, DragSource.Bar, pointerRoot)
                                return@HeroBottomBarStage 0f
                            }
                            railDragX = totalDx * 0.4f
                            railDragY = (totalDy * 0.4f).coerceAtLeast(with(density) { -10.dp.toPx() })
                            liveHint = t("向左拖入胶囊调顺序 · 向下拖出隐藏")
                            return@HeroBottomBarStage 0f
                        }
                        val slots = currentTrackTabs
                        val maxTrackIdx = (slots.size - 1).coerceAtLeast(0)
                        var curIdx = currentConfig.tabs.indexOf(tab)
                        var baseShift = 0f
                        var localX = totalDx
                        val threshold = slotWidthPx * 0.52f
                        if (localX > threshold && curIdx in 0 until maxTrackIdx) {
                            commit(BottomNav.move(currentConfig, curIdx, curIdx + 1))
                            baseShift = slotWidthPx
                            localX -= slotWidthPx
                            curIdx += 1
                        } else if (localX < -threshold && curIdx > 0) {
                            commit(BottomNav.move(currentConfig, curIdx, curIdx - 1))
                            baseShift = -slotWidthPx
                            localX += slotWidthPx
                            curIdx -= 1
                        }
                        if (curIdx == 0 && localX < 0f) localX *= 0.25f
                        if (curIdx == maxTrackIdx && localX > 0f) {
                            if (currentDetached && localX > slotWidthPx * 0.65f) {
                                dragMode = DragMode.Ghost
                                ghostPosInRoot = pointerRoot
                                updateGhostTarget(tab, DragSource.Bar, pointerRoot)
                                return@HeroBottomBarStage baseShift
                            }
                            localX *= 0.28f
                        }
                        railDragX = localX
                        val maxUp = with(density) { -10.dp.toPx() }
                        val maxDownPinned = with(density) { 10.dp.toPx() }
                        val minUpPinned = with(density) { -6.dp.toPx() }
                        railDragY = if (tab.pinned) (totalDy * 0.16f).coerceIn(minUpPinned, maxDownPinned)
                        else (totalDy * 0.35f).coerceAtLeast(maxUp)
                        dropIntent = DropIntent.Reorder
                        liveHint = if (tab.pinned) {
                            t("「我的」固定在第 %lld 格", curIdx + 1)
                        } else {
                            t("松手固定在第 %lld 格 · 向下拖出移走", curIdx + 1)
                        }
                        return@HeroBottomBarStage baseShift
                    } else {
                        ghostPosInRoot = pointerRoot
                        updateGhostTarget(tab, DragSource.Bar, pointerRoot)
                        return@HeroBottomBarStage 0f
                    }
                },
                onDragFinish = { tab ->
                    val finalMode = dragMode
                    val finalIntent = dropIntent
                    val releaseRailX = railDragX
                    clearDragVisuals()
                    if (finalMode == DragMode.Rail && !reduce && kotlin.math.abs(releaseRailX) > 1f) {
                        lastSettledTab = tab
                        scope.launch {
                            railSettleX.snapTo(releaseRailX)
                            railSettleX.animateTo(0f, spring(dampingRatio = 0.72f, stiffness = 420f))
                            if (lastSettledTab == tab) lastSettledTab = null
                        }
                    } else if (finalMode == DragMode.Ghost) {
                        executeDrop(tab, DragSource.Bar, finalIntent)
                    }
                },
                onA11yMove = { from, to -> commit(BottomNav.move(currentConfig, from, to)) },
            )

            // ── Zone 2: Native Segmented Presets (matching Yuyue's YYSegments language) ──
            YYSegments<String?>(
                items = BottomNav.presets.map { it.id to it.name },
                selection = curPresetId,
                onSelect = { pickedId ->
                    val preset = BottomNav.presets.firstOrNull { it.id == pickedId } ?: return@YYSegments
                    popover = null
                    commit(BottomNav.applyPreset(preset), t("已切换「%@」", preset.name))
                },
                modifier = Modifier.fillMaxWidth(),
            )

            // ── Zone 3: 书架内收起 Card (Mirrors PlaceStack) ──
            ShelfFoldZoneCard(
                shelfOn = shelfOn,
                folded = folded,
                collectionCount = collectionCount,
                dropReady = shelfZoneDropReady,
                dropHover = shelfDropHover,
                ineligible = shelfZoneIneligible,
                draggingTab = draggingTab,
                onZoneBounds = { shelfZoneBounds = it },
                onTapTile = { tab, bounds -> handleTap(tab, DragSource.Shelf, bounds) },
                onQuickHide = { tab ->
                    popover = null
                    commit(BottomNav.hide(currentConfig, tab), t("已隐藏「%@」", tab.title))
                },
                onGhostDragStart = { tab, pos ->
                    popover = null
                    draggingTab = tab
                    dragSource = DragSource.Shelf
                    dragMode = DragMode.Ghost
                    ghostPosInRoot = pos
                    view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                    updateGhostTarget(tab, DragSource.Shelf, pos)
                },
                onGhostDragMove = { tab, pos ->
                    ghostPosInRoot = pos
                    updateGhostTarget(tab, DragSource.Shelf, pos)
                },
                onGhostDragEnd = { tab ->
                    val intent = dropIntent
                    clearDragVisuals()
                    executeDrop(tab, DragSource.Shelf, intent)
                },
            )

            // ── Zone 4: Quiet Dashed 已隐藏 Recovery Zone ──
            HiddenRecoveryZone(
                hidden = hidden,
                dropReady = hiddenZoneDropReady,
                dropHover = hiddenDropHover,
                draggingTab = draggingTab,
                onZoneBounds = { hiddenZoneBounds = it },
                onTapChip = { tab, bounds -> handleTap(tab, DragSource.Hidden, bounds) },
                onGhostDragStart = { tab, pos ->
                    popover = null
                    draggingTab = tab
                    dragSource = DragSource.Hidden
                    dragMode = DragMode.Ghost
                    ghostPosInRoot = pos
                    view.performHapticFeedback(HapticFeedbackConstants.LONG_PRESS)
                    updateGhostTarget(tab, DragSource.Hidden, pos)
                },
                onGhostDragMove = { tab, pos ->
                    ghostPosInRoot = pos
                    updateGhostTarget(tab, DragSource.Hidden, pos)
                },
                onGhostDragEnd = { tab ->
                    val intent = dropIntent
                    clearDragVisuals()
                    executeDrop(tab, DragSource.Hidden, intent)
                },
            )
        }

        // ── Floating Frosted Ghost Pill during Cross-Zone Drag (GPU-only graphicsLayer!) ──
        val ghostTab = draggingTab
        if (ghostTab != null && dragMode == DragMode.Ghost) {
            val localX = ghostPosInRoot.x - rootBounds.left
            val localY = ghostPosInRoot.y - rootBounds.top
            var ghostW by remember { mutableFloatStateOf(0f) }
            var ghostH by remember { mutableFloatStateOf(0f) }
            val menuBg = if (YY.dark) Color(0xF0242427) else Color(0xF0FCFBF9)
            Row(
                Modifier
                    .zIndex(50f)
                    .onGloballyPositioned {
                        ghostW = it.size.width.toFloat()
                        ghostH = it.size.height.toFloat()
                    }
                    .graphicsLayer {
                        translationX = localX - ghostW / 2f
                        translationY = localY - ghostH / 2f
                        scaleX = 1.06f
                        scaleY = 1.06f
                    }
                    .shadow(16.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.22f), spotColor = Color.Black.copy(alpha = 0.22f))
                    .clip(CircleShape)
                    .background(menuBg)
                    .border(0.75.dp, YY.tint.edge, CircleShape)
                    .padding(start = 11.dp, end = 15.dp, top = 9.dp, bottom = 9.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(7.dp),
            ) {
                Icon(ghostTab.iconOn, null, tint = YY.tint.accent, modifier = Modifier.size(18.dp))
                Text(ghostTab.title, style = YY.font(11.5f, FontWeight.SemiBold), color = YY.tint.accent, maxLines = 1)
            }
        }

        // ── In-Page Telegram Frosted Context Popover (Never escapes viewport!) ──
        AnimatedVisibility(
            visible = popover != null,
            enter = fadeIn(YYMotion.quick()),
            exit = fadeOut(YYMotion.exit()),
            modifier = Modifier.fillMaxSize().zIndex(40f),
        ) {
            Box(
                Modifier
                    .fillMaxSize()
                    .background(Color.Black.copy(alpha = 0.14f))
                    .clickable(remember { MutableInteractionSource() }, null) { popover = null },
            )
        }
        val activePopover = popover
        AnimatedVisibility(
            visible = activePopover != null,
            enter = if (reduce) fadeIn(YYMotion.quick()) else fadeIn(YYMotion.quick()) + scaleIn(
                spring(dampingRatio = 0.72f, stiffness = 420f),
                initialScale = 0.88f,
                transformOrigin = TransformOrigin(0.5f, 0f),
            ),
            exit = if (reduce) fadeOut(YYMotion.exit()) else fadeOut(YYMotion.exit()) + scaleOut(YYMotion.exit(), targetScale = 0.9f),
            modifier = Modifier.fillMaxSize().zIndex(45f),
        ) {
            if (activePopover != null) {
                InPageTabPopover(
                    anchor = activePopover,
                    rootBounds = rootBounds,
                    config = config,
                    onDismiss = { popover = null },
                    onReplace = { slot, incoming ->
                        popover = null
                        val msg = if (incoming in config.tabs) {
                            t("已互换「%@」与「%@」", slot.title, incoming.title)
                        } else {
                            t("已将「%@」替换为「%@」", slot.title, incoming.title)
                        }
                        commit(BottomNav.replace(config, slot, incoming), msg)
                    },
                    onNudge = { tab, delta ->
                        popover = null
                        val idx = config.tabs.indexOf(tab)
                        val next = idx + delta
                        if (idx >= 0 && next in config.tabs.indices) {
                            commit(BottomNav.move(config, idx, next))
                        }
                    },
                    onFold = { tab ->
                        popover = null
                        commit(BottomNav.foldIntoShelf(config, tab), t("「%@」已收起进书架 ⌃⌄", tab.title))
                    },
                    onHide = { tab ->
                        popover = null
                        commit(BottomNav.hide(config, tab), t("已隐藏「%@」", tab.title))
                    },
                )
            }
        }

        // ── Bottom Pill Toast Feedback ──
        AnimatedVisibility(
            visible = toastMsg != null,
            enter = fadeIn(YYMotion.quick()) + slideInVertically(YYMotion.quick()) { it / 2 },
            exit = fadeOut(YYMotion.exit()) + slideOutVertically(YYMotion.exit()) { it / 2 },
            modifier = Modifier
                .align(Alignment.BottomCenter)
                .padding(bottom = LocalTabBarHeight.current + 12.dp, start = 20.dp, end = 20.dp)
                .zIndex(60f),
        ) {
            toastMsg?.let { msg ->
                Box(
                    Modifier
                        .shadow(10.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.24f), spotColor = Color.Black.copy(alpha = 0.24f))
                        .clip(CircleShape)
                        .background(Color(0xEB1C1B19))
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                ) {
                    Text(msg, style = YY.font(10.5f, FontWeight.Medium), color = Color(0xFFF7F4EE), maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
    }
}

/** Top atmospheric warm-paper stage with directly editable Liquid Glass / Classic bar. */
@Composable
private fun HeroBottomBarStage(
    trackTabs: List<BottomTab>,
    detachedSearch: Boolean,
    liquid: Boolean,
    shelfHasSwitcher: Boolean,
    eink: Boolean,
    dropActive: Boolean,
    draggingTab: BottomTab?,
    dragMode: DragMode,
    railDragX: Float,
    railDragY: Float,
    railSettleX: Float,
    lastSettledTab: BottomTab?,
    placeholderIndex: Int,
    placeholderTitle: String?,
    replaceHoverTab: BottomTab?,
    replaceBadgeLabel: String?,
    menuOpenTab: BottomTab?,
    hintText: String,
    hintEmphasized: Boolean,
    onHeroBounds: (Rect) -> Unit,
    onTrackBounds: (Rect) -> Unit,
    onOrbBounds: (Rect) -> Unit,
    onSlotTap: (BottomTab, Rect) -> Unit,
    onDragStartRail: (BottomTab) -> Unit,
    onRailDelta: (tab: BottomTab, totalDx: Float, totalDy: Float, slotWidthPx: Float, isDetachedOrb: Boolean, pointerRoot: Offset) -> Float,
    onDragFinish: (BottomTab) -> Unit,
    onA11yMove: (Int, Int) -> Unit,
) {
    val dark = YY.dark
    val stageShape = RoundedCornerShape(YY.s(20))
    val stageBrush = remember(dark) {
        if (dark) {
            Brush.verticalGradient(listOf(Color(0xFF24211E), Color(0xFF191715)))
        } else {
            Brush.verticalGradient(listOf(Color(0xFFEFEAE0), Color(0xFFE7E1D5)))
        }
    }
    val stageBorder by animateColorAsState(
        if (dropActive) YY.tint.accent else if (dark) Color.White.copy(alpha = 0.08f) else Color(0xFF5A4A36).copy(alpha = 0.14f),
        YYMotion.quick(),
        label = "heroBorder",
    )
    val glassBg = if (dark) Color(0xC8262523) else Color(0xC8FCFBF8)
    val glassBorder = if (dark) Color.White.copy(alpha = 0.16f) else Color.White.copy(alpha = 0.92f)

    Column(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { onHeroBounds(it.boundsInRoot()) }
            .clip(stageShape)
            .background(stageBrush)
            .border(if (dropActive) 1.5.dp else 0.5.dp, stageBorder, stageShape)
            .padding(start = 12.dp, end = 12.dp, top = 16.dp, bottom = 12.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        // Subtle blurred warm book spines peeking above the translucent bar
        Row(
            Modifier
                .height(48.dp)
                .padding(horizontal = 12.dp)
                .alpha(if (eink) 0.25f else 0.50f)
                .then(if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && !eink) Modifier.blur(0.8.dp) else Modifier),
            horizontalArrangement = Arrangement.spacedBy(8.dp, Alignment.CenterHorizontally),
            verticalAlignment = Alignment.Bottom,
        ) {
            HeroSpine(30.dp, 46.dp, Color(0xFF1F5368), Color(0xFF122933))
            HeroSpine(26.dp, 38.dp, Color(0xFF7B6A3A), Color(0xFF43381B))
            HeroSpine(32.dp, 50.dp, Color(0xFFDBD6CA), Color(0xFFBFB9AC))
            HeroSpine(28.dp, 42.dp, Color(0xFFB9B7EE), Color(0xFF6FD4AA))
            HeroSpine(24.dp, 36.dp, Color(0xFFC88A7C), Color(0xFF8D5247))
        }

        // Overlap the bar gently over the bottom edge of the spines
        Row(
            Modifier
                .fillMaxWidth()
                .graphicsLayer { translationY = (-10).dp.toPx() }
                .zIndex(3f),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(9.dp),
        ) {
            val trackShape = if (liquid) CircleShape else RoundedCornerShape(16.dp)
            Row(
                Modifier
                    .weight(1f)
                    .height(if (liquid) 60.dp else 58.dp)
                    .onGloballyPositioned { onTrackBounds(it.boundsInRoot()) }
                    .shadow(
                        elevation = if (eink) 0.dp else if (dark) 8.dp else 10.dp,
                        shape = trackShape,
                        ambientColor = Color.Black.copy(alpha = if (dark) 0.42f else 0.14f),
                        spotColor = Color.Black.copy(alpha = if (dark) 0.42f else 0.14f),
                    )
                    .clip(trackShape)
                    .background(if (liquid) glassBg else YY.c.card)
                    .border(
                        width = if (liquid) 0.75.dp else 0.5.dp,
                        color = if (liquid) glassBorder else YY.c.line,
                        shape = trackShape,
                    )
                    .padding(4.dp)
                    .animateContentSize(spring(dampingRatio = 0.78f, stiffness = 420f)),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                trackTabs.forEachIndexed { idx, tab ->
                    if (placeholderIndex == idx && placeholderTitle != null) {
                        InsertSlotPlaceholder(placeholderTitle, Modifier.weight(1f))
                    }
                    key(tab) {
                        EditableDockSlot(
                            tab = tab,
                            index = idx,
                            totalTrackCount = trackTabs.size,
                            hasSwitch = tab == BottomTab.Shelf && shelfHasSwitcher,
                            isRailDragging = draggingTab == tab && dragMode == DragMode.Rail,
                            isGhosted = draggingTab == tab && dragMode == DragMode.Ghost,
                            isReplaceHover = replaceHoverTab == tab,
                            replaceBadge = if (replaceHoverTab == tab) replaceBadgeLabel else null,
                            isMenuOpen = menuOpenTab == tab,
                            railOffsetX = when {
                                draggingTab == tab && dragMode == DragMode.Rail -> railDragX
                                lastSettledTab == tab -> railSettleX
                                else -> 0f
                            },
                            railOffsetY = if (draggingTab == tab && dragMode == DragMode.Rail) railDragY else 0f,
                            modifier = Modifier.weight(1f),
                            onTap = { bounds -> onSlotTap(tab, bounds) },
                            onDragStart = { onDragStartRail(tab) },
                            onDragDelta = { dx, dy, w, rootPos -> onRailDelta(tab, dx, dy, w, false, rootPos) },
                            onDragEnd = { onDragFinish(tab) },
                            onA11yMove = onA11yMove,
                        )
                    }
                }
                if (placeholderIndex >= trackTabs.size && placeholderTitle != null) {
                    InsertSlotPlaceholder(placeholderTitle, Modifier.weight(1f))
                }
            }

            if (detachedSearch) {
                EditableSearchOrb(
                    glassBg = glassBg,
                    glassBorder = glassBorder,
                    eink = eink,
                    dark = dark,
                    isRailDragging = draggingTab == BottomTab.Search && dragMode == DragMode.Rail,
                    isGhosted = draggingTab == BottomTab.Search && dragMode == DragMode.Ghost,
                    isReplaceHover = replaceHoverTab == BottomTab.Search,
                    replaceBadge = if (replaceHoverTab == BottomTab.Search) replaceBadgeLabel else null,
                    isMenuOpen = menuOpenTab == BottomTab.Search,
                    railOffsetX = if (draggingTab == BottomTab.Search && dragMode == DragMode.Rail) railDragX else 0f,
                    railOffsetY = if (draggingTab == BottomTab.Search && dragMode == DragMode.Rail) railDragY else 0f,
                    onBounds = onOrbBounds,
                    onTap = { bounds -> onSlotTap(BottomTab.Search, bounds) },
                    onDragStart = { onDragStartRail(BottomTab.Search) },
                    onDragDelta = { dx, dy, w, rootPos -> onRailDelta(BottomTab.Search, dx, dy, w, true, rootPos) },
                    onDragEnd = { onDragFinish(BottomTab.Search) },
                )
            }
        }

        Text(
            text = hintText,
            style = YY.font(9.5f, if (hintEmphasized) FontWeight.Medium else FontWeight.Normal),
            color = if (hintEmphasized) YY.tint.accent else YY.c.t2,
            textAlign = TextAlign.Center,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(top = 2.dp),
        )
    }
}

@Composable
private fun HeroSpine(width: androidx.compose.ui.unit.Dp, height: androidx.compose.ui.unit.Dp, top: Color, bottom: Color) {
    Box(
        Modifier
            .width(width)
            .height(height)
            .clip(RoundedCornerShape(topStart = 6.dp, topEnd = 6.dp, bottomStart = 2.dp, bottomEnd = 2.dp))
            .background(Brush.verticalGradient(listOf(top, bottom))),
    )
}

@Composable
private fun InsertSlotPlaceholder(title: String, modifier: Modifier = Modifier) {
    val accent = YY.tint.accent
    Box(
        modifier
            .height(46.dp)
            .padding(horizontal = 2.dp)
            .clip(CircleShape)
            .background(YY.tint.accentSoft),
        contentAlignment = Alignment.Center,
    ) {
        Canvas(Modifier.fillMaxSize()) {
            drawRoundRect(
                color = accent,
                cornerRadius = CornerRadius(size.height / 2f, size.height / 2f),
                style = Stroke(
                    width = 1.5.dp.toPx(),
                    pathEffect = PathEffect.dashPathEffect(floatArrayOf(10f, 8f)),
                ),
            )
        }
        Text(
            title,
            fontSize = 10.sp,
            fontWeight = FontWeight.SemiBold,
            color = accent,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
private fun EditableDockSlot(
    tab: BottomTab,
    index: Int,
    totalTrackCount: Int,
    hasSwitch: Boolean,
    isRailDragging: Boolean,
    isGhosted: Boolean,
    isReplaceHover: Boolean,
    replaceBadge: String?,
    isMenuOpen: Boolean,
    railOffsetX: Float,
    railOffsetY: Float,
    modifier: Modifier = Modifier,
    onTap: (Rect) -> Unit,
    onDragStart: () -> Unit,
    onDragDelta: (totalDx: Float, totalDy: Float, slotWidthPx: Float, pointerRoot: Offset) -> Float,
    onDragEnd: () -> Unit,
    onA11yMove: (Int, Int) -> Unit,
) {
    val viewConfig = LocalViewConfiguration.current
    var boundsInRoot by remember { mutableStateOf(Rect.Zero) }
    val curOnTap by rememberUpdatedState(onTap)
    val curOnDragStart by rememberUpdatedState(onDragStart)
    val curOnDragDelta by rememberUpdatedState(onDragDelta)
    val curOnDragEnd by rememberUpdatedState(onDragEnd)

    val scale by animateFloatAsState(
        targetValue = when {
            isRailDragging -> 1.06f
            isReplaceHover -> 0.95f
            isGhosted -> 0.9f
            else -> 1f
        },
        animationSpec = if (YYMotion.isReduceMotion()) snap() else spring(dampingRatio = 0.72f, stiffness = 420f),
        label = "slotScale",
    )
    val alpha by animateFloatAsState(
        targetValue = if (isGhosted) 0.16f else 1f,
        animationSpec = YYMotion.quick(),
        label = "slotAlpha",
    )

    val slotBg = when {
        isReplaceHover || isMenuOpen -> YY.tint.accentSoft
        isRailDragging -> YY.c.card
        else -> Color.Transparent
    }
    val ink = when {
        isReplaceHover || isMenuOpen || isRailDragging -> YY.tint.accent
        else -> YY.c.t1
    }
    val borderStroke = when {
        isReplaceHover -> 1.8.dp to YY.tint.accent
        isMenuOpen || isRailDragging -> 1.dp to YY.tint.edge
        else -> null
    }

    val moveLeftLabel = t("左移")
    val moveRightLabel = t("右移")
    val a11yActions = buildList {
        if (index > 0) add(CustomAccessibilityAction(moveLeftLabel) { onA11yMove(index, index - 1); true })
        if (index < totalTrackCount - 1) add(CustomAccessibilityAction(moveRightLabel) { onA11yMove(index, index + 1); true })
    }

    Box(
        modifier
            .height(52.dp)
            .zIndex(if (isRailDragging) 25f else if (isReplaceHover) 10f else 0f)
            .onGloballyPositioned { boundsInRoot = it.boundsInRoot() }
            .graphicsLayer {
                translationX = railOffsetX
                translationY = railOffsetY
                scaleX = scale
                scaleY = scale
                this.alpha = alpha
            }
            .semantics { customActions = a11yActions }
            .pointerInput(tab) {
                val slop = viewConfig.touchSlop
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val downPos = down.position
                    val startRootPos = boundsInRoot.topLeft + downPos
                    var dragging = false
                    var railBaseX = 0f
                    var cumDx = 0f
                    var cumDy = 0f
                    var lastRootPos = startRootPos
                    val slotW = boundsInRoot.width.coerceAtLeast(1f)

                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            if (dragging) change.consume()
                            break
                        }
                        val delta = change.positionChange()
                        cumDx += delta.x
                        cumDy += delta.y
                        lastRootPos += delta
                        if (!dragging && hypot(cumDx, cumDy) > slop) {
                            dragging = true
                            curOnDragStart()
                        }
                        if (dragging) {
                            change.consume()
                            val shift = curOnDragDelta(cumDx - railBaseX, cumDy, slotW, lastRootPos)
                            railBaseX += shift
                        }
                    }

                    if (dragging) {
                        curOnDragEnd()
                    } else {
                        curOnTap(boundsInRoot)
                    }
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Column(
            Modifier
                .fillMaxSize()
                .shadow(
                    elevation = if (isRailDragging) 10.dp else 0.dp,
                    shape = CircleShape,
                    ambientColor = Color.Black.copy(alpha = 0.20f),
                    spotColor = Color.Black.copy(alpha = 0.20f),
                )
                .clip(CircleShape)
                .background(slotBg)
                .then(if (borderStroke != null) Modifier.border(borderStroke.first, borderStroke.second, CircleShape) else Modifier),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(2.dp, Alignment.CenterVertically),
        ) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (hasSwitch) Spacer(Modifier.width(7.dp))
                Icon(if (isRailDragging || isReplaceHover) tab.iconOn else tab.icon, null, tint = ink, modifier = Modifier.size(20.dp))
                if (hasSwitch) Icon(TabIcons.switchChevron, null, tint = ink.copy(alpha = 0.65f), modifier = Modifier.size(width = 7.dp, height = 20.dp))
            }
            Text(
                tab.title,
                fontSize = 10.sp,
                fontWeight = if (isRailDragging || isReplaceHover || isMenuOpen) FontWeight.SemiBold else FontWeight.Medium,
                color = ink,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        if (replaceBadge != null) {
            Box(
                Modifier
                    .align(Alignment.TopCenter)
                    .graphicsLayer { translationY = (-22).dp.toPx() }
                    .shadow(6.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.20f), spotColor = Color.Black.copy(alpha = 0.20f))
                    .clip(CircleShape)
                    .background(YY.tint.accent)
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            ) {
                Text(replaceBadge, fontSize = 10.sp, fontWeight = FontWeight.SemiBold, color = Color.White, maxLines = 1)
            }
        }
    }
}

@Composable
private fun EditableSearchOrb(
    glassBg: Color,
    glassBorder: Color,
    eink: Boolean,
    dark: Boolean,
    isRailDragging: Boolean,
    isGhosted: Boolean,
    isReplaceHover: Boolean,
    replaceBadge: String?,
    isMenuOpen: Boolean,
    railOffsetX: Float,
    railOffsetY: Float,
    onBounds: (Rect) -> Unit,
    onTap: (Rect) -> Unit,
    onDragStart: () -> Unit,
    onDragDelta: (totalDx: Float, totalDy: Float, slotWidthPx: Float, pointerRoot: Offset) -> Float,
    onDragEnd: () -> Unit,
) {
    val viewConfig = LocalViewConfiguration.current
    var boundsInRoot by remember { mutableStateOf(Rect.Zero) }
    val curOnTap by rememberUpdatedState(onTap)
    val curOnDragStart by rememberUpdatedState(onDragStart)
    val curOnDragDelta by rememberUpdatedState(onDragDelta)
    val curOnDragEnd by rememberUpdatedState(onDragEnd)

    val scale by animateFloatAsState(
        targetValue = when {
            isRailDragging -> 1.06f
            isReplaceHover -> 0.95f
            isGhosted -> 0.9f
            else -> 1f
        },
        animationSpec = if (YYMotion.isReduceMotion()) snap() else spring(dampingRatio = 0.72f, stiffness = 420f),
        label = "orbScale",
    )
    val alpha by animateFloatAsState(if (isGhosted) 0.16f else 1f, YYMotion.quick(), label = "orbAlpha")

    val bg = when {
        isReplaceHover || isMenuOpen -> YY.tint.accentSoft
        isRailDragging -> YY.c.card
        else -> glassBg
    }
    val borderWidth = if (isReplaceHover) 1.8.dp else if (isMenuOpen || isRailDragging) 1.dp else 0.75.dp
    val borderColor = if (isReplaceHover) YY.tint.accent else if (isMenuOpen || isRailDragging) YY.tint.edge else glassBorder
    val ink = if (isReplaceHover || isMenuOpen || isRailDragging) YY.tint.accent else YY.c.t1

    Box(
        Modifier
            .size(60.dp)
            .zIndex(if (isRailDragging) 25f else if (isReplaceHover) 10f else 0f)
            .onGloballyPositioned {
                boundsInRoot = it.boundsInRoot()
                onBounds(boundsInRoot)
            }
            .graphicsLayer {
                translationX = railOffsetX
                translationY = railOffsetY
                scaleX = scale
                scaleY = scale
                this.alpha = alpha
            }
            .pointerInput(BottomTab.Search) {
                val slop = viewConfig.touchSlop
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    val startRootPos = boundsInRoot.topLeft + down.position
                    var dragging = false
                    var cumDx = 0f
                    var cumDy = 0f
                    var lastRootPos = startRootPos
                    val orbW = boundsInRoot.width.coerceAtLeast(1f)
                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            if (dragging) change.consume()
                            break
                        }
                        val delta = change.positionChange()
                        cumDx += delta.x
                        cumDy += delta.y
                        lastRootPos += delta
                        if (!dragging && hypot(cumDx, cumDy) > slop) {
                            dragging = true
                            curOnDragStart()
                        }
                        if (dragging) {
                            change.consume()
                            curOnDragDelta(cumDx, cumDy, orbW, lastRootPos)
                        }
                    }
                    if (dragging) curOnDragEnd() else curOnTap(boundsInRoot)
                }
            },
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier
                .fillMaxSize()
                .shadow(
                    elevation = if (eink) 0.dp else if (dark) 8.dp else 10.dp,
                    shape = CircleShape,
                    ambientColor = Color.Black.copy(alpha = if (dark) 0.42f else 0.14f),
                    spotColor = Color.Black.copy(alpha = if (dark) 0.42f else 0.14f),
                )
                .clip(CircleShape)
                .background(bg)
                .border(borderWidth, borderColor, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(TabIcons.search, t("搜索"), tint = ink, modifier = Modifier.size(21.dp))
        }
        if (replaceBadge != null) {
            Box(
                Modifier
                    .align(Alignment.TopCenter)
                    .graphicsLayer { translationY = (-22).dp.toPx() }
                    .shadow(6.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.20f), spotColor = Color.Black.copy(alpha = 0.20f))
                    .clip(CircleShape)
                    .background(YY.tint.accent)
                    .padding(horizontal = 8.dp, vertical = 2.dp),
            ) {
                Text(replaceBadge, fontSize = 10.sp, fontWeight = FontWeight.SemiBold, color = Color.White, maxLines = 1)
            }
        }
    }
}

/** Zone 3: 书架内收起 card mirroring PlaceStack with 1-tap promote, × quick-hide, and ghost drag. */
@Composable
private fun ShelfFoldZoneCard(
    shelfOn: Boolean,
    folded: List<BottomTab>,
    collectionCount: Int,
    dropReady: Boolean,
    dropHover: Boolean,
    ineligible: Boolean,
    draggingTab: BottomTab?,
    onZoneBounds: (Rect) -> Unit,
    onTapTile: (BottomTab, Rect) -> Unit,
    onQuickHide: (BottomTab) -> Unit,
    onGhostDragStart: (BottomTab, Offset) -> Unit,
    onGhostDragMove: (BottomTab, Offset) -> Unit,
    onGhostDragEnd: (BottomTab) -> Unit,
) {
    val cardShape = RoundedCornerShape(YY.s(17))
    val cardAlpha by animateFloatAsState(
        targetValue = when {
            ineligible -> 0.36f
            !shelfOn -> 0.55f
            else -> 1f
        },
        animationSpec = YYMotion.quick(),
        label = "shelfZoneAlpha",
    )
    val cardBg by animateColorAsState(
        if (dropHover) YY.tint.accentSoft else YY.c.card,
        YYMotion.quick(),
        label = "shelfZoneBg",
    )
    val cardBorder by animateColorAsState(
        when {
            dropHover -> YY.tint.accent
            dropReady -> YY.tint.edge
            else -> YY.c.line
        },
        YYMotion.quick(),
        label = "shelfZoneBorder",
    )

    Column(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { onZoneBounds(it.boundsInRoot()) }
            .alpha(cardAlpha)
            .clip(cardShape)
            .background(cardBg)
            .border(if (dropHover) 1.5.dp else 0.5.dp, cardBorder, cardShape)
            .padding(horizontal = 14.dp, vertical = 13.dp)
            .animateContentSize(spring(dampingRatio = 0.8f, stiffness = 420f)),
        verticalArrangement = Arrangement.spacedBy(11.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                Text(t("书架内收起"), style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1)
                Row(
                    Modifier
                        .clip(CircleShape)
                        .background(YY.tint.accentSoft)
                        .padding(horizontal = 7.dp, vertical = 2.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    Icon(TabIcons.switchChevron, null, tint = YY.tint.accent, modifier = Modifier.size(width = 7.dp, height = 13.dp))
                    Text(t("点书架切换"), fontSize = 10.sp, fontWeight = FontWeight.Medium, color = YY.tint.accent)
                }
            }
            Spacer(Modifier.weight(1f))
            Text(
                if (shelfOn) t("轻点放到底栏 · 拖入可插位") else t("需先开启书架"),
                style = YY.font(9.5f),
                color = YY.c.t3,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }

        if (folded.isNotEmpty() || (shelfOn && collectionCount > 0)) {
            FlowRow(
                Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                for (tab in folded) {
                    key(tab) {
                        ShelfEntryTile(
                            tab = tab,
                            isGhosted = draggingTab == tab,
                            onTap = { bounds -> onTapTile(tab, bounds) },
                            onQuickHide = { onQuickHide(tab) },
                            onDragStart = { pos -> onGhostDragStart(tab, pos) },
                            onDragMove = { pos -> onGhostDragMove(tab, pos) },
                            onDragEnd = { onGhostDragEnd(tab) },
                        )
                    }
                }
                if (shelfOn && collectionCount > 0) {
                    PassiveCollectionsTile(collectionCount)
                }
            }
        } else {
            Text(
                text = if (shelfOn) {
                    t("已全部移到底栏或隐藏，书架自动省去折叠箭头")
                } else {
                    t("「书架」当前已隐藏，点下方恢复即可折叠收纳")
                },
                style = YY.font(10f),
                color = YY.c.t3,
                modifier = Modifier.padding(vertical = 2.dp),
            )
        }
    }
}

@Composable
private fun ShelfEntryTile(
    tab: BottomTab,
    isGhosted: Boolean,
    onTap: (Rect) -> Unit,
    onQuickHide: () -> Unit,
    onDragStart: (Offset) -> Unit,
    onDragMove: (Offset) -> Unit,
    onDragEnd: () -> Unit,
) {
    val viewConfig = LocalViewConfiguration.current
    var boundsInRoot by remember { mutableStateOf(Rect.Zero) }
    val curTap by rememberUpdatedState(onTap)
    val curStart by rememberUpdatedState(onDragStart)
    val curMove by rememberUpdatedState(onDragMove)
    val curEnd by rememberUpdatedState(onDragEnd)

    val tileAlpha by animateFloatAsState(if (isGhosted) 0.22f else 1f, YYMotion.quick(), label = "tileAlpha")
    val shape = RoundedCornerShape(14.dp)

    Row(
        Modifier
            .onGloballyPositioned { boundsInRoot = it.boundsInRoot() }
            .alpha(tileAlpha)
            .clip(shape)
            .background(YY.c.bg)
            .border(0.5.dp, YY.c.line, shape)
            .padding(start = 7.dp, end = 5.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(7.dp),
    ) {
        // Draggable + 1-tap promote area (distinct from trailing × button so × never triggers drag!)
        Row(
            Modifier.pointerInput(tab) {
                val slop = viewConfig.touchSlop
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    var dragging = false
                    var cumDx = 0f
                    var cumDy = 0f
                    var lastRoot = boundsInRoot.topLeft + down.position
                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            if (dragging) change.consume()
                            break
                        }
                        val delta = change.positionChange()
                        cumDx += delta.x
                        cumDy += delta.y
                        lastRoot += delta
                        if (!dragging && hypot(cumDx, cumDy) > slop) {
                            dragging = true
                            curStart(lastRoot)
                        }
                        if (dragging) {
                            change.consume()
                            curMove(lastRoot)
                        }
                    }
                    if (dragging) curEnd() else curTap(boundsInRoot)
                }
            },
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(7.dp),
        ) {
            Box(
                Modifier
                    .size(28.dp)
                    .shadow(1.dp, RoundedCornerShape(9.dp), ambientColor = Color.Black.copy(alpha = 0.06f), spotColor = Color.Black.copy(alpha = 0.06f))
                    .clip(RoundedCornerShape(9.dp))
                    .background(YY.c.card),
                contentAlignment = Alignment.Center,
            ) {
                Icon(tab.icon, null, tint = YY.tint.accent, modifier = Modifier.size(16.dp))
            }
            Text(tab.title, style = YY.font(11f, FontWeight.Medium), color = YY.c.t1, maxLines = 1)
        }

        // Trailing 1-tap quick-hide ×
        Box(
            Modifier
                .size(24.dp)
                .clip(CircleShape)
                .clickable(onClickLabel = t("隐藏%@", tab.title), onClick = onQuickHide),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.close, t("隐藏%@", tab.title), tint = YY.c.t3, modifier = Modifier.size(12.dp))
        }
    }
}

@Composable
private fun PassiveCollectionsTile(count: Int) {
    val lineColor = YY.c.line
    Box(
        Modifier
            .alpha(0.72f)
            .clip(RoundedCornerShape(14.dp)),
    ) {
        Canvas(Modifier.matchParentSize()) {
            drawRoundRect(
                color = lineColor,
                cornerRadius = CornerRadius(14.dp.toPx(), 14.dp.toPx()),
                style = Stroke(width = 1.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(8f, 6f))),
            )
        }
        Row(
            Modifier.padding(start = 7.dp, end = 12.dp, top = 6.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(7.dp),
        ) {
            Box(
                Modifier
                    .size(28.dp)
                    .clip(RoundedCornerShape(9.dp))
                    .background(YY.c.inset),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.collection, null, tint = YY.c.t2, modifier = Modifier.size(15.dp))
            }
            Text(t("合集 (%lld)", count), style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t2, maxLines = 1)
        }
    }
}

/** Zone 4: Recessed matte tray for hidden entries (distinct from raised white 书架内收起 card). */
@Composable
private fun HiddenRecoveryZone(
    hidden: List<BottomTab>,
    dropReady: Boolean,
    dropHover: Boolean,
    draggingTab: BottomTab?,
    onZoneBounds: (Rect) -> Unit,
    onTapChip: (BottomTab, Rect) -> Unit,
    onGhostDragStart: (BottomTab, Offset) -> Unit,
    onGhostDragMove: (BottomTab, Offset) -> Unit,
    onGhostDragEnd: (BottomTab) -> Unit,
) {
    val dark = YY.dark
    val idleTrayBg = if (dark) Color(0xFF141210) else Color(0xFFEEEAE2)
    val idleTrayBorder = if (dark) Color.White.copy(alpha = 0.05f) else Color(0xFF332E29).copy(alpha = 0.07f)

    val bg by animateColorAsState(
        when {
            dropHover -> YY.c.danger.copy(alpha = 0.14f)
            dropReady -> YY.c.danger.copy(alpha = 0.06f)
            else -> idleTrayBg
        },
        YYMotion.quick(),
        label = "hiddenBg",
    )
    val dashColor by animateColorAsState(
        when {
            dropHover -> YY.c.danger
            dropReady -> YY.c.danger.copy(alpha = 0.52f)
            else -> Color.Transparent
        },
        YYMotion.quick(),
        label = "hiddenDash",
    )
    val shape = RoundedCornerShape(18.dp)

    Box(
        Modifier
            .fillMaxWidth()
            .onGloballyPositioned { onZoneBounds(it.boundsInRoot()) }
            .clip(shape)
            .background(bg)
            .then(
                if (!dropReady && !dropHover) Modifier.border(0.5.dp, idleTrayBorder, shape) else Modifier,
            )
            .animateContentSize(spring(dampingRatio = 0.8f, stiffness = 420f)),
    ) {
        if (dropReady || dropHover) {
            Canvas(Modifier.matchParentSize()) {
                drawRoundRect(
                    color = dashColor,
                    cornerRadius = CornerRadius(18.dp.toPx(), 18.dp.toPx()),
                    style = Stroke(width = 1.25.dp.toPx(), pathEffect = PathEffect.dashPathEffect(floatArrayOf(12f, 8f))),
                )
            }
        }

        if (hidden.isEmpty()) {
            Text(
                text = t("把不看的入口向下拖到这里隐藏"),
                style = YY.font(10f),
                color = if (dropHover) YY.c.danger else YY.c.t3,
                textAlign = TextAlign.Center,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 14.dp),
            )
        } else {
            Column(
                Modifier.fillMaxWidth().padding(horizontal = 14.dp, vertical = 12.dp),
                verticalArrangement = Arrangement.spacedBy(9.dp),
            ) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(t("已隐藏"), style = YY.font(10.5f, FontWeight.SemiBold), color = YY.c.t2)
                    Spacer(Modifier.weight(1f))
                    Text(t("轻点加回底栏 · 拖入任意区"), style = YY.font(9.5f), color = YY.c.t3)
                }
                FlowRow(
                    Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    for (tab in hidden) {
                        key(tab) {
                            HiddenRecoveryChip(
                                tab = tab,
                                isGhosted = draggingTab == tab,
                                onTap = { bounds -> onTapChip(tab, bounds) },
                                onDragStart = { pos -> onGhostDragStart(tab, pos) },
                                onDragMove = { pos -> onGhostDragMove(tab, pos) },
                                onDragEnd = { onGhostDragEnd(tab) },
                            )
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun HiddenRecoveryChip(
    tab: BottomTab,
    isGhosted: Boolean,
    onTap: (Rect) -> Unit,
    onDragStart: (Offset) -> Unit,
    onDragMove: (Offset) -> Unit,
    onDragEnd: () -> Unit,
) {
    val viewConfig = LocalViewConfiguration.current
    var boundsInRoot by remember { mutableStateOf(Rect.Zero) }
    val curTap by rememberUpdatedState(onTap)
    val curStart by rememberUpdatedState(onDragStart)
    val curMove by rememberUpdatedState(onDragMove)
    val curEnd by rememberUpdatedState(onDragEnd)
    val chipAlpha by animateFloatAsState(if (isGhosted) 0.22f else 1f, YYMotion.quick(), label = "hChipAlpha")

    Row(
        Modifier
            .onGloballyPositioned { boundsInRoot = it.boundsInRoot() }
            .alpha(chipAlpha)
            .shadow(if (YY.dark) 0.dp else 1.5.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.06f), spotColor = Color.Black.copy(alpha = 0.06f))
            .clip(CircleShape)
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, CircleShape)
            .pointerInput(tab) {
                val slop = viewConfig.touchSlop
                awaitEachGesture {
                    val down = awaitFirstDown(requireUnconsumed = false)
                    var dragging = false
                    var cumDx = 0f
                    var cumDy = 0f
                    var lastRoot = boundsInRoot.topLeft + down.position
                    while (true) {
                        val event = awaitPointerEvent(PointerEventPass.Main)
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            if (dragging) change.consume()
                            break
                        }
                        val delta = change.positionChange()
                        cumDx += delta.x
                        cumDy += delta.y
                        lastRoot += delta
                        if (!dragging && hypot(cumDx, cumDy) > slop) {
                            dragging = true
                            curStart(lastRoot)
                        }
                        if (dragging) {
                            change.consume()
                            curMove(lastRoot)
                        }
                    }
                    if (dragging) curEnd() else curTap(boundsInRoot)
                }
            }
            .padding(start = 8.dp, end = 12.dp, top = 7.dp, bottom = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        Box(
            Modifier.size(18.dp).clip(CircleShape).background(YY.c.inset),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(12.dp))
        }
        Icon(tab.icon, null, tint = YY.c.t2, modifier = Modifier.size(15.dp))
        Text(tab.title, style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t2, maxLines = 1)
    }
}

/** In-page Telegram-style frosted context popover anchored strictly within the settings viewport. */
@Composable
private fun InPageTabPopover(
    anchor: PopoverAnchor,
    rootBounds: Rect,
    config: BottomNavConfig,
    onDismiss: () -> Unit,
    onReplace: (BottomTab, BottomTab) -> Unit,
    onNudge: (BottomTab, Int) -> Unit,
    onFold: (BottomTab) -> Unit,
    onHide: (BottomTab) -> Unit,
) {
    val density = LocalDensity.current
    val dark = YY.dark
    val menuBg = if (dark) Color(0xF2242427) else Color(0xF5FCFBF9)
    val menuBorder = if (dark) Color.White.copy(alpha = 0.14f) else Color.White.copy(alpha = 0.92f)
    val menuWidthDp = 240.dp
    val menuWidthPx = with(density) { menuWidthDp.toPx() }
    val marginPx = with(density) { 12.dp.toPx() }
    val gapPx = with(density) { 8.dp.toPx() }
    var menuHeightPx by remember { mutableFloatStateOf(with(density) { 210.dp.toPx() }) }

    val relAnchorCenterX = (anchor.boundsInRoot.left + anchor.boundsInRoot.width / 2f) - rootBounds.left
    val maxLeft = (rootBounds.width - menuWidthPx - marginPx).coerceAtLeast(marginPx)
    val leftPx = (relAnchorCenterX - menuWidthPx / 2f).coerceIn(marginPx, maxLeft)
    val belowTopPx = (anchor.boundsInRoot.bottom - rootBounds.top) + gapPx
    val aboveTopPx = (anchor.boundsInRoot.top - rootBounds.top) - menuHeightPx - gapPx
    val topPx = if (belowTopPx + menuHeightPx <= rootBounds.height - marginPx || aboveTopPx < marginPx) {
        belowTopPx.coerceAtLeast(marginPx)
    } else {
        aboveTopPx.coerceAtLeast(marginPx)
    }

    val tab = anchor.tab
    val shelfOn = BottomTab.Shelf in config.tabs
    val removable = BottomNav.canRemove(config.tabs, tab)
    val shape = RoundedCornerShape(18.dp)

    Box(Modifier.fillMaxSize()) {
        Column(
            Modifier
                .width(menuWidthDp)
                .onGloballyPositioned { if (it.size.height > 0) menuHeightPx = it.size.height.toFloat() }
                .graphicsLayer {
                    translationX = leftPx
                    translationY = topPx
                }
                .shadow(20.dp, shape, ambientColor = Color.Black.copy(alpha = 0.24f), spotColor = Color.Black.copy(alpha = 0.24f))
                .clip(shape)
                .background(menuBg)
                .border(0.75.dp, menuBorder, shape)
                .clickable(remember { MutableInteractionSource() }, null) {} // consume taps inside menu
                .padding(8.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            if (anchor.source == DragSource.Bar) {
                val idx = config.tabs.indexOf(tab)
                if (tab.pinned) {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
                        Text(t("%@ · 常驻底栏", tab.title), style = YY.font(9.5f, FontWeight.SemiBold), color = YY.c.t3)
                        Spacer(Modifier.weight(1f))
                        Text(t("第 %lld 格", idx + 1), style = YY.font(9.5f, tnum = true), color = YY.c.t3)
                    }
                    if (idx > 0) {
                        PopoverActionRow(Icons.chevronLeft, t("向左移一格"), YY.c.t1) { onNudge(tab, -1) }
                    }
                    if (idx in 0 until config.tabs.lastIndex) {
                        PopoverActionRow(Icons.chevronRight, t("向右移一格"), YY.c.t1) { onNudge(tab, 1) }
                    }
                } else {
                    Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
                        Text(t("替换「%@」为", tab.title), style = YY.font(9.5f, FontWeight.SemiBold), color = YY.c.t3)
                        Spacer(Modifier.weight(1f))
                        Text(t("第 %lld 格", idx + 1), style = YY.font(9.5f, tnum = true), color = YY.c.t3)
                    }
                    val candidates = BottomTab.entries.filter { it != tab && !it.pinned }
                    PopoverTileGrid(candidates) { picked -> onReplace(tab, picked) }
                    if (removable) {
                        Box(Modifier.fillMaxWidth().padding(horizontal = 6.dp, vertical = 2.dp).height(0.5.dp).background(YY.c.line))
                        if (tab.isShelfSub && shelfOn) {
                            PopoverActionRow(Icons.arrowDown, t("收起进书架 ⌃⌄"), YY.tint.accent) { onFold(tab) }
                        }
                        PopoverActionRow(Icons.eyeSlash, t("隐藏「%@」", tab.title), YY.c.danger) { onHide(tab) }
                    }
                }
            } else {
                // Full (5/5) replacement chooser when tapping a chip in 书架内收起 / 已隐藏
                Text(
                    t("底栏已满，用「%@」替换…", tab.title),
                    style = YY.font(9.5f, FontWeight.SemiBold),
                    color = YY.c.t3,
                    modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
                )
                val replaceableSlots = config.tabs.filter { !it.pinned }
                PopoverTileGrid(replaceableSlots, prefix = t("换 ")) { slot -> onReplace(slot, tab) }
                if (anchor.source == DragSource.Hidden && tab.isShelfSub && shelfOn) {
                    Box(Modifier.fillMaxWidth().padding(horizontal = 6.dp, vertical = 2.dp).height(0.5.dp).background(YY.c.line))
                    PopoverActionRow(Icons.arrowDown, t("先收起进书架 ⌃⌄"), YY.tint.accent) { onFold(tab) }
                }
            }
        }
    }
}

@Composable
private fun PopoverTileGrid(
    items: List<BottomTab>,
    prefix: String = "",
    onPick: (BottomTab) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(5.dp), modifier = Modifier.padding(horizontal = 2.dp, vertical = 2.dp)) {
        items.chunked(3).forEach { rowItems ->
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                for (candidate in rowItems) {
                    Column(
                        Modifier
                            .weight(1f)
                            .clip(RoundedCornerShape(10.dp))
                            .background(YY.c.inset)
                            .clickable { onPick(candidate) }
                            .padding(vertical = 8.dp, horizontal = 4.dp),
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        Icon(candidate.icon, null, tint = YY.c.t1, modifier = Modifier.size(18.dp))
                        Text(
                            if (prefix.isEmpty()) candidate.title else "$prefix${candidate.title}",
                            fontSize = 10.5.sp,
                            fontWeight = FontWeight.Medium,
                            color = YY.c.t1,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                }
                repeat(3 - rowItems.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

@Composable
private fun PopoverActionRow(
    icon: ImageVector,
    label: String,
    tint: Color,
    onClick: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp, vertical = 9.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(9.dp),
    ) {
        Icon(icon, null, tint = tint, modifier = Modifier.size(16.dp))
        Text(label, style = YY.font(11f, FontWeight.Medium), color = tint, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}
