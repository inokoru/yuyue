package app.yuyue.reader

import android.animation.ObjectAnimator
import android.animation.ValueAnimator
import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.text.TextUtils
import android.util.TypedValue
import android.view.Gravity
import android.view.HapticFeedbackConstants
import android.view.MotionEvent
import android.view.VelocityTracker
import android.view.View
import android.view.ViewConfiguration
import android.view.ViewGroup
import android.view.animation.DecelerateInterpolator
import android.view.animation.LinearInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import app.yuyue.BuildConfig
import app.yuyue.t
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.PageGeometry
import uniffi.yuyue_core.Paging
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.YuyueCore
import kotlin.math.abs
import kotlin.math.exp
import kotlin.math.floor
import kotlin.math.roundToInt

/**
 * Shows the book one section at a time and turns its pages (reader contract: native owns
 * everything that moves). The current section's neighbours are kept loaded on both sides, so
 * turning into the next chapter is as quick as turning a page — as on iPhone.
 *
 * Each section is a WebView laid out in pages side by side (`data-yy-paging="native"`). The
 * WebViews never scroll themselves: a drag moves one place along the whole strip of pages,
 * the current section's and its neighbours', and on letting go it settles on a page. Past a
 * section's last page the next section slides in under the same finger.
 */
@SuppressLint("ViewConstructor")
class ReaderPager(context: Context, private val core: YuyueCore, private val bookID: String) : FrameLayout(context) {
    interface Listener {
        /** The visible page changed, or the section was laid out again. */
        fun moved(pager: ReaderPager)
        /** The reader started turning pages themselves. */
        fun turnedByHand(pager: ReaderPager)
        fun tapped(m: ReaderMessage)
        fun inkReached(page: Int)
        fun selected(m: ReaderMessage)
        fun doubleTapped(m: ReaderMessage)
        /** A section was laid out for the first time: what is drawn in it goes in now. */
        fun sectionReady(page: SectionPage)
        /** Scrolling: the reader moved on down the page (controls away), back up it, or reached an end of it. */
        fun barsAway(away: Boolean)
        /** 气泡: a bubble's avatar or name was tapped (`choice` null), or a name it offers ('' for 别人…). */
        fun bubble(line: uniffi.yuyue_core.BubbleLine, section: Int, choice: String?, x: Double, y: Double) {}
    }

    var listener: Listener? = null
    var mode: LayoutMode = LayoutMode.PAGINATED
    /** Used for the line length, which decides the side margins. */
    var fontSize = 18.0
    /** Top and bottom space the page keeps clear of the system bars, in px. */
    var insetTop = 0
    var insetBottom = 0
    /** 墨水屏 and 沉浸 with still pages: pages switch at once, never slide. */
    var calm = false
    var background: Int = 0
        set(v) {
            field = v
            setBackgroundColor(v)
            (slots + spare).forEach { it.setBackground(v) }
            updatePullCueColors()
        }
    /** Ink & accent colours from the active paper, used for the scrolled chapter-pull cue. */
    var ink: Int = Color.DKGRAY
        set(v) {
            if (field != v) { field = v; updatePullCueColors() }
        }
    var accent: Int = Color.BLACK
        set(v) {
            if (field != v) { field = v; updatePullCueColors() }
        }
    /** Optional chapter title lookup for the pull-to-cross cue. */
    var chapterTitle: ((Int) -> String?)? = null
    /** Whether the floating bottom capsule is visible (keeps the bottom pull cue clear of it). */
    var bottomCapsuleVisible: Boolean = true

    private val scope: CoroutineScope = MainScope()
    private val slots = mutableListOf<SectionPage>()
    private val spare = mutableListOf<SectionPage>()
    var current: SectionPage? = null
        private set(v) {
            val old = field
            field = v
            if (translating && v != null && v !== old && v.section != null) translate(v)
        }
    private var pendingOpen: Pair<Int, Target>? = null
    private var appliedGeometry: PageGeometry? = null

    /** The place along the strip, in pages of the current section: 0 is its first page,
     *  -1 the previous section's last, pageCount the next section's first. */
    private var position = 0f
    private var animator: ValueAnimator? = null
    private var scrollAnimator: ValueAnimator? = null
    private var verticalAnimator: ValueAnimator? = null

    private val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
    private val minFling = ViewConfiguration.get(context).scaledMinimumFlingVelocity
    private var downX = 0f
    private var downY = 0f
    private var startPosition = 0f
    private var dragging = false
    private var velocity: VelocityTracker? = null

    // Scrolled vertical pull-to-cross state.
    private var downAtTop = false
    private var downAtBottom = false
    private var pulling = false
    private var pullDir = 0 // +1 = forward (pull up at bottom), -1 = backward (pull down at top)
    private var pullStartY = 0f
    private var pullArmed = false
    private var verticalTransitioning = false

    private val pullCueBg = GradientDrawable()
    private val pullCueArrow = TextView(context).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 12f)
        typeface = Typeface.DEFAULT_BOLD
        includeFontPadding = false
    }
    private val pullCueLabel = TextView(context).apply {
        setTextSize(TypedValue.COMPLEX_UNIT_SP, 12.5f)
        typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
        maxLines = 1
        ellipsize = TextUtils.TruncateAt.END
        includeFontPadding = false
    }
    private val pullCue = LinearLayout(context).apply {
        orientation = LinearLayout.HORIZONTAL
        gravity = Gravity.CENTER_VERTICAL
        val d = resources.displayMetrics.density
        val padH = (14f * d).roundToInt()
        val padV = (7f * d).roundToInt()
        setPadding(padH, padV, padH, padV)
        pullCueBg.cornerRadius = 999f * d
        background = pullCueBg
        addView(pullCueArrow, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT).apply {
            marginEnd = (6f * d).roundToInt()
        })
        addView(pullCueLabel, LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT))
        alpha = 0f
        visibility = View.GONE
        isClickable = false
        isFocusable = false
    }

    init {
        addView(pullCue, LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER_HORIZONTAL or Gravity.BOTTOM))
        updatePullCueColors()
    }

    // MARK: Geometry

    fun geometry(): PageGeometry {
        val density = resources.displayMetrics.density
        val width = width / density
        // About 34 characters to a line at most; wider pages get wider margins instead.
        val measure = fontSize * 34
        val margin = maxOf(if (width < 500) 24.0 else 40.0, (width - measure) / 2).roundToInt().toDouble()
        val top = (insetTop / density + 28).roundToInt().toDouble()
        val bottom = (insetBottom / density + 40).roundToInt().toDouble()
        return PageGeometry(Paging.NATIVE, margin, top, bottom, 1u, calm)
    }

    // MARK: Opening

    fun open(section: Int, target: Target) {
        if (width == 0) {
            pendingOpen = section to target
            return
        }
        pendingOpen = null
        resetVerticalPull()
        val cur = current
        if (cur != null && cur.section == section && cur.isReady) {
            scope.launch {
                cur.go(target)
                position = cur.page.toFloat()
                render()
                listener?.moved(this@ReaderPager)
            }
            return
        }
        slots.toList().forEach(::recycle)
        val page = makePage()
        page.load(bookID, section, target)
        slots.clear()
        slots += page
        current = page
        position = 0f
        render()
    }

    /** Moves to a section and place, e.g. from 目录 or a link. */
    fun show(section: Int, target: Target) = open(section, target)

    /** The words changed underneath (替换规则): loads the sections again, at the same place. */
    fun reload() {
        val here = here() ?: return
        current = null
        open(here.section.toInt(), Target.Sentence(here.sentence.toInt()))
    }

    private fun previous(of: Int): Int? = runCatching { core.previousSection(bookID, of.toUInt(), false) }.getOrNull()?.toInt()
    private fun next(of: Int): Int? = runCatching { core.nextSection(bookID, of.toUInt(), false) }.getOrNull()?.toInt()

    /** Keeps the sections on either side of the current one loaded, and lets the others go. */
    private fun loadNeighbours() {
        val cur = current ?: return
        val section = cur.section ?: return
        val wanted = mutableListOf<SectionPage>()
        previous(section)?.let { wanted += pageFor(it, Target.End) }
        wanted += cur
        next(section)?.let { wanted += pageFor(it, Target.Start) }
        slots.filter { it !in wanted }.forEach(::recycle)
        slots.clear()
        slots += wanted
        if (mode == LayoutMode.SCROLLED) {
            previousSlot?.takeIf { it.isReady }?.let { it.webView.scrollTo(0, it.webView.maxScrollY) }
            nextSlot?.takeIf { it.isReady }?.let { it.webView.scrollTo(0, 0) }
        }
        render()
    }

    private fun pageFor(section: Int, target: Target): SectionPage {
        slots.firstOrNull { it.section == section }?.let { return it }
        val p = makePage()
        p.load(bookID, section, target)
        return p
    }

    private fun makePage(): SectionPage {
        val p = spare.removeLastOrNull() ?: SectionPage(context, core, bookID, ::geometry, ::received).also {
            it.setBackground(background)
            addView(it.webView, LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            bringChildToFront(pullCue)
            it.webView.scrolled = { dy -> trackBars(it, dy) }
        }
        p.webView.translationY = 0f
        p.webView.visibility = View.VISIBLE
        return p
    }

    /** Scrolling: how far the hand has moved the page one way. */
    private var barsTravel = 0

    /** The controls follow the hand only: the ink line moving the page doesn't send them away.
     *  At either end of a section they come back, for 下一章 is on them. */
    private fun trackBars(page: SectionPage, dy: Int) {
        if (mode != LayoutMode.SCROLLED || page !== current || verticalTransitioning) return
        val view = page.webView
        if (!view.canScrollVertically(-1) || !view.canScrollVertically(1)) {
            barsTravel = 0
            listener?.barsAway(false)
            return
        }
        if (!view.touched) {
            barsTravel = 0
            return
        }
        barsTravel = if (dy * barsTravel < 0) dy else barsTravel + dy
        if (abs(barsTravel) >= 24 * resources.displayMetrics.density) {
            listener?.barsAway(barsTravel > 0)
            barsTravel = 0
        }
    }

    private fun recycle(p: SectionPage) {
        if (p === current) current = null
        p.scrolledTo = null
        p.clear()
        p.webView.translationY = 0f
        p.webView.visibility = View.INVISIBLE
        spare += p
    }

    private val previousSlot: SectionPage? get() = current?.let { c -> slots.getOrNull(slots.indexOf(c) - 1) }
    private val nextSlot: SectionPage? get() = current?.let { c -> slots.getOrNull(slots.indexOf(c) + 1) }

    // MARK: Drawing the strip

    private fun pageWidthPx(p: SectionPage): Float {
        val css = p.layout?.pageWidth ?: 0.0
        return if (css > 0) (css * resources.displayMetrics.density).toFloat() else width.toFloat()
    }

    /** Puts every slot where `position` says. */
    private fun render() {
        val cur = current ?: return
        val w = width.toFloat()
        val n = (cur.layout?.pageCount ?: 1)
        val inside = position.coerceIn(0f, (n - 1).toFloat())
        // Down a scrolled section the page's own place stands: nothing here may move it.
        val paged = mode == LayoutMode.PAGINATED
        if (paged) cur.webView.scrollTo((inside * pageWidthPx(cur)).roundToInt(), 0)
        cur.webView.translationX = (inside - position) * w
        if (!pulling && !verticalTransitioning) cur.webView.translationY = 0f
        previousSlot?.let { prev ->
            val last = (prev.layout?.pageCount ?: 1) - 1
            if (paged) prev.webView.scrollTo((last * pageWidthPx(prev)).roundToInt(), 0)
            prev.webView.translationX = (-1 - position) * w
            if (!verticalTransitioning) prev.webView.translationY = 0f
        }
        nextSlot?.let { nx ->
            if (paged) nx.webView.scrollTo(0, 0)
            nx.webView.translationX = (n - position) * w
            if (!verticalTransitioning) nx.webView.translationY = 0f
        }
        for (s in slots) if (s !== cur && s !== previousSlot && s !== nextSlot) {
            s.webView.translationX = 10f * w
            s.webView.translationY = 0f
        }
    }

    // MARK: Turning

    /** One page on or back, as a tap on the side or a volume key does. */
    fun turn(forward: Boolean) {
        val cur = current ?: return
        val n = cur.layout?.pageCount ?: return
        val scrolled = mode == LayoutMode.SCROLLED
        if (scrolled && cur.webView.canScrollVertically(if (forward) 1 else -1)) {
            listener?.turnedByHand(this)
            val view = cur.webView
            val step = (view.height * 0.85f).roundToInt()
            val to = (view.scrollY + if (forward) step else -step).coerceIn(0, view.maxScrollY)
            scrollAnimator?.cancel()
            if (calm) view.scrollTo(0, to)
            else scrollAnimator = ObjectAnimator.ofInt(view, "scrollY", to).setDuration(240).apply {
                interpolator = DecelerateInterpolator(1.6f)
                start()
            }
            return
        }
        if (scrolled) {
            val neighbour = if (forward) nextSlot else previousSlot
            if (neighbour?.isReady != true) return
            listener?.turnedByHand(this)
            crossVertically(forward = forward, initialOffset = 0f, animated = !calm)
            return
        }
        val target = cur.page + if (forward) 1 else -1
        if (target >= n && nextSlot?.isReady != true) return
        if (target < 0 && previousSlot?.isReady != true) return
        listener?.turnedByHand(this)
        settle(target.toFloat(), animated = !calm)
    }

    /** Goes to a page of the current section, as the voice moves on. */
    fun showPage(page: Int, animated: Boolean) {
        val cur = current ?: return
        val n = cur.layout?.pageCount ?: return
        if (page !in 0 until n || page == cur.page) return
        settle(page.toFloat(), animated && !calm)
    }

    /** The ink line under `sentence` of `section` (contract §7); off when null. */
    fun setInk(section: Int, sentence: Int, durationMs: Double, elapsedMs: Double, playing: Boolean, words: List<Double>? = null) {
        // 逐词跟读: [character, ms] pairs, laid end to end.
        val w = words?.joinToString(",", "[", "]") ?: "null"
        for (s in slots) {
            if (s.section == section) s.call("yy.setActive($sentence, {durationMs: $durationMs, elapsedMs: $elapsedMs, playing: $playing, words: $w})")
            else s.call("yy.setActive(null)")
        }
    }

    fun clearInk() = callAll("yy.setActive(null)")

    /** Goes to page `p` of the current section (`-1` and `pageCount` cross into the neighbours). */
    private fun settle(to: Float, animated: Boolean, initialVelocity: Float = 0f) {
        animator?.cancel()
        val d0 = position - to
        if (!animated || abs(d0) < 0.0005f) {
            position = to
            landed()
            return
        }
        // Critically damped spring x(t) = to + (d0 + (v0 + omega * d0) * t) * exp(-omega * t).
        // Raise omega >= -v0/d0 on fast toward-target flings so x(t) never overshoots into a neighbour slot.
        val v0 = if (d0 < 0f) initialVelocity.coerceIn(0f, 25f) else initialVelocity.coerceIn(-25f, 0f)
        val omega = maxOf(18f, if (v0 * d0 < 0f) -v0 / d0 else 18f)
        val c2 = v0 + omega * d0
        var totalSec = 0.32f
        for (ms in 120..360 step 10) {
            val t = ms / 1000f
            if (abs((d0 + c2 * t) * exp(-omega * t)) < 0.001f) {
                totalSec = t
                break
            }
        }
        animator = ValueAnimator.ofFloat(0f, totalSec).apply {
            duration = (totalSec * 1000f).roundToInt().toLong()
            interpolator = LinearInterpolator()
            addUpdateListener {
                val t = it.animatedValue as Float
                position = to + (d0 + c2 * t) * exp(-omega * t)
                render()
            }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    if (animator === animation) landed()
                }
            })
            start()
        }
    }

    /** The strip has stopped on a page: cross into a neighbour when it went past an end. */
    private fun landed() {
        animator = null
        val cur = current ?: return
        val n = cur.layout?.pageCount ?: 1
        when {
            position >= n -> nextSlot?.let { nx ->
                current = nx
                nx.page = 0
                if (mode == LayoutMode.SCROLLED) nx.webView.scrollTo(0, 0)
                position = 0f
                loadNeighbours()
            }
            position < 0 -> previousSlot?.let { prev ->
                current = prev
                val last = (prev.layout?.pageCount ?: 1) - 1
                prev.page = last
                if (mode == LayoutMode.SCROLLED) prev.webView.scrollTo(0, prev.webView.maxScrollY)
                position = last.toFloat()
                loadNeighbours()
            }
            else -> {
                cur.page = position.roundToInt()
                position = cur.page.toFloat()
            }
        }
        render()
        listener?.moved(this)
    }

    // MARK: Touch

    override fun onInterceptTouchEvent(e: MotionEvent): Boolean {
        if (mode == LayoutMode.SCROLLED) return interceptVerticalPull(e)
        if (mode != LayoutMode.PAGINATED) return false
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x
                downY = e.y
                dragging = false
                velocity?.recycle()
                velocity = VelocityTracker.obtain().apply { addMovement(e) }
                // A page still settling is caught by the finger, as a scroll view would.
                if (animator != null) {
                    animator?.cancel()
                    animator = null
                    startDrag()
                    return true
                }
            }
            MotionEvent.ACTION_MOVE -> {
                velocity?.addMovement(e)
                val dx = e.x - downX
                val dy = e.y - downY
                if (current?.webView?.selecting == true) return false
                if (abs(dx) > touchSlop && abs(dx) > abs(dy) * 1.2f) {
                    startDrag()
                    downX = e.x
                    return true
                }
            }
        }
        return false
    }

    private fun interceptVerticalPull(e: MotionEvent): Boolean {
        if (verticalTransitioning) return true
        when (e.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = e.x
                downY = e.y
                pulling = false
                pullDir = 0
                pullArmed = false
                if (verticalAnimator != null) {
                    verticalAnimator?.cancel()
                    verticalAnimator = null
                    current?.webView?.translationY = 0f
                }
                val view = current?.webView
                downAtTop = view != null && !view.canScrollVertically(-1)
                downAtBottom = view != null && !view.canScrollVertically(1)
            }
            MotionEvent.ACTION_MOVE -> {
                val view = current?.webView ?: return false
                if (view.selecting) return false
                val dx = e.x - downX
                val dy = e.y - downY
                if (abs(dy) > touchSlop && abs(dy) > abs(dx) * 1.2f) {
                    val dir = ScrolledPullMath.edgeDirection(dy, canScrollUp = !downAtTop, canScrollDown = !downAtBottom)
                    if (dir != 0 && !view.canScrollVertically(dir)) {
                        if (dir < 0 && previousSlot == null) return false
                        pulling = true
                        pullDir = dir
                        pullStartY = e.y
                        pullArmed = false
                        parent?.requestDisallowInterceptTouchEvent(true)
                        return true
                    }
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                downAtTop = false
                downAtBottom = false
            }
        }
        return false
    }

    private fun startDrag() {
        dragging = true
        startPosition = position
        parent?.requestDisallowInterceptTouchEvent(true)
        listener?.turnedByHand(this)
    }

    private fun rubberBand(d: Float): Float = (1f - 1f / (d * 1.6f + 1f)) * 0.28f

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(e: MotionEvent): Boolean {
        if (mode == LayoutMode.SCROLLED) return handleVerticalPullTouch(e)
        velocity?.addMovement(e)
        when (e.actionMasked) {
            MotionEvent.ACTION_MOVE -> {
                if (dragging) {
                    val cur = current ?: return true
                    val n = cur.layout?.pageCount ?: 1
                    var p = startPosition - (e.x - downX) / width
                    // Past the book's first or last page the strip gives asymptotically.
                    val low = if (previousSlot?.isReady == true) -1f else 0f
                    val high = if (nextSlot?.isReady == true) n.toFloat() else (n - 1).toFloat()
                    if (p < low) p = low - rubberBand(low - p)
                    if (p > high) p = high + rubberBand(p - high)
                    position = p
                    render()
                }
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                if (dragging) {
                    dragging = false
                    velocity?.computeCurrentVelocity(1000)
                    val v = velocity?.xVelocity ?: 0f
                    val cur = current
                    val n = cur?.layout?.pageCount ?: 1
                    val base = floor(startPosition + 0.0001f)
                    val movedEnough = abs(position - startPosition) > 0.02f
                    var target = when {
                        movedEnough && v < -minFling * 2.5f -> floor(position) + 1
                        movedEnough && v > minFling * 2.5f -> floor(position + 0.9999f) - 1
                        else -> position.roundToInt().toFloat()
                    }
                    // One page per swipe, like a book.
                    target = target.coerceIn(base - 1, base + 1)
                    val low = if (previousSlot?.isReady == true) -1f else 0f
                    val high = if (nextSlot?.isReady == true) n.toFloat() else (n - 1).toFloat()
                    val v0 = if (width > 0) -v / width else 0f
                    settle(target.coerceIn(low, high), animated = !calm, initialVelocity = v0)
                }
                velocity?.recycle()
                velocity = null
            }
        }
        return true
    }

    private fun handleVerticalPullTouch(e: MotionEvent): Boolean {
        if (verticalTransitioning) return true
        if (!pulling) return false
        val cur = current ?: return true
        val density = resources.displayMetrics.density
        val maxPull = 160f * density
        val threshold = 56f * density
        val hysteresis = 8f * density
        when (e.actionMasked) {
            MotionEvent.ACTION_MOVE -> {
                val raw = maxOf(0f, -(e.y - pullStartY) * pullDir)
                val visual = ScrolledPullMath.rubberBand(raw, maxPull)
                if (!calm) cur.webView.translationY = -pullDir * visual
                val target = if (pullDir > 0) nextSlot else previousSlot
                val hasTarget = target != null
                val wasArmed = pullArmed
                val armed = ScrolledPullMath.isArmed(visual, wasArmed, threshold, hysteresis)
                if (armed && !wasArmed) {
                    performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
                }
                pullArmed = armed
                updatePullCue(
                    forward = pullDir > 0,
                    armed = armed && hasTarget,
                    hasTarget = hasTarget,
                    targetSection = target?.section,
                    progress = ((visual - 6f * density) / (threshold - 6f * density)).coerceIn(0f, 1f),
                )
            }
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                val shouldCross = e.actionMasked == MotionEvent.ACTION_UP && pullArmed
                val dir = pullDir
                val offset = cur.webView.translationY
                pulling = false
                pullDir = 0
                pullArmed = false
                hidePullCue(animated = !shouldCross)
                if (shouldCross) {
                    val target = if (dir > 0) nextSlot else previousSlot
                    if (target?.isReady == true) {
                        listener?.turnedByHand(this)
                        crossVertically(forward = dir > 0, initialOffset = offset, animated = !calm)
                        return true
                    }
                }
                snapBackVertical()
            }
        }
        return true
    }

    private fun withAlpha(color: Int, alpha: Float): Int =
        Color.argb((alpha.coerceIn(0f, 1f) * 255f).roundToInt(), Color.red(color), Color.green(color), Color.blue(color))

    private fun updatePullCueColors(armed: Boolean = false) {
        val density = resources.displayMetrics.density
        val fg = if (armed) accent else withAlpha(ink, 0.72f)
        pullCueArrow.setTextColor(fg)
        pullCueLabel.setTextColor(fg)
        pullCueBg.setColor(withAlpha(background, 0.94f))
        val stroke = if (armed) withAlpha(accent, 0.42f) else withAlpha(ink, 0.16f)
        pullCueBg.setStroke(maxOf(1, (0.75f * density).roundToInt()), stroke)
    }

    private fun updatePullCue(forward: Boolean, armed: Boolean, hasTarget: Boolean, targetSection: Int?, progress: Float) {
        val density = resources.displayMetrics.density
        val text = if (!hasTarget) {
            t("已是最后一章")
        } else {
            val title = targetSection?.let { chapterTitle?.invoke(it) }?.trim()?.takeIf { it.isNotEmpty() }
            val fallback = if (forward) t("下一章") else t("上一章")
            val dest = title ?: fallback
            val verb = when {
                forward && armed -> t("松手进入")
                forward -> t("继续上拉")
                armed -> t("松手回到")
                else -> t("继续下拉")
            }
            "$verb · $dest"
        }
        if (pullCueLabel.text != text) pullCueLabel.text = text
        val maxW = maxOf((120f * density).roundToInt(), minOf(width - (72f * density).roundToInt(), (260f * density).roundToInt()))
        if (pullCueLabel.maxWidth != maxW) pullCueLabel.maxWidth = maxW
        val arrowVis = if (hasTarget) View.VISIBLE else View.GONE
        if (pullCueArrow.visibility != arrowVis) pullCueArrow.visibility = arrowVis
        if (hasTarget) {
            val glyph = if (forward) (if (armed) "↓" else "↑") else (if (armed) "↑" else "↓")
            if (pullCueArrow.text != glyph) pullCueArrow.text = glyph
        }
        updatePullCueColors(armed)
        val lp = pullCue.layoutParams as LayoutParams
        val wantGravity = Gravity.CENTER_HORIZONTAL or if (forward) Gravity.BOTTOM else Gravity.TOP
        val wantBottom = if (forward) insetBottom + (if (bottomCapsuleVisible) (68f * density).roundToInt() else (16f * density).roundToInt()) else 0
        val wantTop = if (forward) 0 else insetTop + (14f * density).roundToInt()
        if (lp.gravity != wantGravity || lp.bottomMargin != wantBottom || lp.topMargin != wantTop) {
            lp.gravity = wantGravity
            lp.bottomMargin = wantBottom
            lp.topMargin = wantTop
            pullCue.layoutParams = lp
        }
        val wantVis = if (progress > 0.01f) View.VISIBLE else View.GONE
        if (pullCue.visibility != wantVis) {
            pullCue.visibility = wantVis
            if (wantVis == View.VISIBLE) bringChildToFront(pullCue)
        }
        pullCue.alpha = progress
    }

    private fun hidePullCue(animated: Boolean) {
        if (pullCue.visibility != View.VISIBLE) return
        pullCue.animate().cancel()
        if (!animated || calm) {
            pullCue.alpha = 0f
            pullCue.visibility = View.GONE
            return
        }
        pullCue.animate().alpha(0f).setDuration(150).withEndAction {
            if (pullCue.alpha <= 0.01f) pullCue.visibility = View.GONE
        }.start()
    }

    private fun snapBackVertical() {
        val cur = current ?: return
        val start = cur.webView.translationY
        verticalAnimator?.cancel()
        if (calm || abs(start) < 0.5f) {
            cur.webView.translationY = 0f
            return
        }
        verticalAnimator = ValueAnimator.ofFloat(start, 0f).apply {
            duration = 180
            interpolator = DecelerateInterpolator(1.6f)
            addUpdateListener { cur.webView.translationY = it.animatedValue as Float }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    if (verticalAnimator === animation) verticalAnimator = null
                }
            })
            start()
        }
    }

    private fun crossVertically(forward: Boolean, initialOffset: Float, animated: Boolean) {
        if (verticalTransitioning) return
        val cur = current ?: return
        val target = (if (forward) nextSlot else previousSlot) ?: return
        if (!target.isReady) return
        hidePullCue(animated = false)
        target.webView.scrollTo(0, if (forward) 0 else target.webView.maxScrollY)
        val finish = {
            verticalTransitioning = false
            cur.webView.translationY = 0f
            target.webView.translationY = 0f
            if (current === cur) {
                current = target
                val last = (target.layout?.pageCount ?: 1) - 1
                target.page = if (forward) 0 else last
                position = target.page.toFloat()
                loadNeighbours()
                render()
                listener?.moved(this)
            }
        }
        if (!animated || calm || height <= 0) {
            finish()
            return
        }
        verticalTransitioning = true
        val h = height.toFloat()
        val startCurY = initialOffset.coerceIn(-h * 0.45f, h * 0.45f)
        val endCurY = if (forward) -h else h
        val startTargetY = if (forward) h + startCurY else -h + startCurY
        target.webView.visibility = View.VISIBLE
        target.webView.translationX = 0f
        target.webView.translationY = startTargetY
        cur.webView.translationY = startCurY
        verticalAnimator?.let { verticalAnimator = null; it.cancel() }
        verticalAnimator = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 260
            interpolator = DecelerateInterpolator(1.8f)
            addUpdateListener {
                val f = it.animatedValue as Float
                cur.webView.translationY = startCurY + (endCurY - startCurY) * f
                target.webView.translationY = startTargetY * (1f - f)
            }
            addListener(object : android.animation.AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: android.animation.Animator) {
                    if (verticalAnimator !== animation) return
                    verticalAnimator = null
                    finish()
                }
            })
            start()
        }
    }

    private fun resetVerticalPull() {
        verticalAnimator?.let { verticalAnimator = null; it.cancel() }
        verticalTransitioning = false
        pulling = false
        pullDir = 0
        pullArmed = false
        downAtTop = false
        downAtBottom = false
        (slots + spare).forEach { it.webView.translationY = 0f }
        hidePullCue(animated = false)
    }

    // MARK: Messages

    private fun received(page: SectionPage, m: ReaderMessage) {
        if (BuildConfig.DEBUG && m.type != "log") android.util.Log.d("yuyue-reader", "${m.type} section=${m.section} page.section=${page.section} pages=${m.pageCount}")
        if (m.section != null && m.section != page.section) return
        when (m.type) {
            "log" -> if (BuildConfig.DEBUG) android.util.Log.d("yuyue-reader", m.json.optString("level") + " " + m.json.optString("message"))
            "ready", "layout" -> {
                if (m.type == "ready") {
                    page.call("yy.setUi(${if (app.yuyue.L10n.chinese) "'zh'" else "'en'"})")
                    // First: it rebuilds the section's boxes; the rest are drawn on them.
                    if (bubbles) sendBubbles(page)
                    sendMarks(page)
                    sendHits(page)
                    sendSource(page)
                    sendForeign(page)
                    listener?.sectionReady(page)
                }
                page.take(m)
                scope.launch {
                    val shown = page.settle()
                    if (page === current) {
                        position = page.page.toFloat()
                        if (shown || m.type == "layout") listener?.moved(this@ReaderPager)
                        if (slots.size == 1) loadNeighbours()
                    } else if (mode == LayoutMode.SCROLLED) {
                        if (page === previousSlot) page.webView.scrollTo(0, page.webView.maxScrollY)
                        else if (page === nextSlot) page.webView.scrollTo(0, 0)
                    }
                    render()
                }
            }
            "tap" -> if (page === current && !dragging && !pulling && !verticalTransitioning) listener?.tapped(m)
            "doubleTap" -> if (page === current) listener?.doubleTapped(m)
            "bubble" -> if (page === current) {
                val section = page.section
                val l = section?.let { bubbleLines[it] }?.getOrNull(m.line ?: -1)
                if (section != null && l != null) listener?.bubble(l, section, m.choice, m.x ?: 0.0, m.y ?: 0.0)
            }
            "selection" -> if (page === current) {
                page.webView.selecting = m.text != null
                listener?.selected(m)
            }
            "activePage" -> if (page === current) m.page?.let { listener?.inkReached(it) }
            "scroll" -> {
                // Kept for a neighbour too: it may be moved to its end before it comes in.
                page.scrolledTo = m.sentence
                if (page === current) listener?.moved(this)
            }
        }
    }

    // MARK: Where the reader is

    /** The section and sentence standing for what is on screen. */
    fun here(): Position? {
        val cur = current ?: return null
        val section = cur.section ?: return null
        val layout = cur.layout ?: return null
        val sentence = if (mode == LayoutMode.SCROLLED) cur.scrolledTo ?: 0 else layout.firstSentence(cur.page)
        return Position(section.toUInt(), sentence.toUInt())
    }

    // MARK: 气泡模式

    /** The book's dialogue as bubbles. */
    var bubbles = false
        set(v) { if (v != field) { field = v; slots.forEach(::sendBubbles) } }

    fun bubblesChanged() {
        if (bubbles) slots.forEach(::sendBubbles)
    }

    /** The lines last drawn per section: a tap on a bubble says which by its place in the list. */
    private val bubbleLines = HashMap<Int, List<uniffi.yuyue_core.BubbleLine>>()

    private fun sendBubbles(page: SectionPage) {
        val section = page.section ?: return
        if (!bubbles) return page.call("yy.setBubbles(null)")
        val list = org.json.JSONArray()
        val got = runCatching { core.sectionBubbles(bookID, section.toUInt()) }.getOrDefault(emptyList())
        bubbleLines[section] = got
        for (l in got) {
            list.put(org.json.JSONObject().put("first", l.first.toInt()).put("last", l.last.toInt()).put("start", l.start.toInt()).put("end", l.end.toInt())
                .put("who", l.who ?: org.json.JSONObject.NULL).put("avatar", l.avatar).put("color", l.color).put("right", l.right)
                .put("choices", org.json.JSONArray(l.choices)))
        }
        page.call("yy.setBubbles($list)")
    }

    // MARK: Drawn in the text

    private var marks: List<uniffi.yuyue_core.Mark> = emptyList()
    private var hits: Pair<String, uniffi.yuyue_core.SearchHit?>? = null
    /** 沉浸阅读 can hide the highlights. */
    var marksHidden = false
        set(v) { field = v; slots.forEach(::sendMarks) }
    /** Marks made since the book was opened: shown even then, or a colour picked would leave nothing on the page. */
    private val madeHere = mutableSetOf<String>()
    fun keepShown(id: String) { madeHere.add(id) }

    fun setMarks(list: List<uniffi.yuyue_core.Mark>) {
        marks = list
        slots.forEach(::sendMarks)
    }

    /** Marks where `query` turns up; `current` in the solid colour. null clears. */
    fun setHits(query: String?, current: uniffi.yuyue_core.SearchHit?) {
        hits = query?.let { it to current }
        slots.forEach(::sendHits)
    }

    private fun sendMarks(page: SectionPage) {
        val section = page.section ?: return
        val list = org.json.JSONArray()
        for (m in marks) if (m.section.toInt() == section && (!marksHidden || m.id in madeHere)) {
            list.put(org.json.JSONObject().put("id", m.id)
                .put("start", org.json.JSONObject().put("s", m.start.sentence.toInt()).put("o", m.start.offset.toInt()))
                .put("end", org.json.JSONObject().put("s", m.end.sentence.toInt()).put("o", m.end.offset.toInt()))
                .put("style", m.style.css).put("note", m.note != null))
        }
        page.call("yy.setMarks($list)")
    }

    /** 书伴's 出处: one sentence washed in the accent until something else is pointed at. */
    private var source: Position? = null

    fun setSource(p: Position?) {
        source = p
        slots.forEach(::sendSource)
    }

    private fun sendSource(page: SectionPage) {
        val s = source
        if (s == null || page.section != s.section.toInt()) return page.call("yy.setSource(null)")
        page.call("yy.setSource(${s.sentence})")
    }

    private fun sendHits(page: SectionPage) {
        val h = hits
        if (h == null || page.section == null) return page.call("yy.setHits(null, null)")
        val c = h.second?.takeIf { it.section.toInt() == page.section }?.let { "{s: ${it.sentence}, nth: ${it.nth}}" } ?: "null"
        page.call("yy.setHits(${org.json.JSONObject.quote(h.first)}, $c)")
    }

    // MARK: 外文原著

    /** Taps on words look them up (点词). */
    var lookup = false
        set(v) { if (v != field) { field = v; slots.forEach(::sendForeign) } }
    /** 生词: dotted wherever they turn up. */
    private var vocab: List<String> = emptyList()
    /** The word being looked up. */
    private var looking: Triple<Int, TextPoint, TextPoint>? = null
    /** 译: translates whole paragraphs; the answers are kept per section. */
    var translator: (suspend (Int, List<String>) -> Pair<List<String?>, String>)? = null
    var onTranslateFailed: ((String?) -> Unit)? = null
    var translating: Boolean = false
        private set
    private val translated = mutableMapOf<Int, org.json.JSONArray>()
    private val translatingInFlight = mutableSetOf<Int>()
    /** 难词注释: the harder words of a section and their meanings, from the core. */
    var glosser: ((Int) -> List<Pair<String, String>>)? = null
    private val glossed = mutableMapOf<Int, List<Pair<String, String>>>()
    /** 沉浸阅读 can hide 难词注释 too. */
    var glossesHidden = false
        set(value) {
            if (field == value) return
            field = value
            slots.forEach(::sendGlosses)
        }

    fun refreshGlosses() {
        if (glosser == null) return
        glossed.clear()
        slots.forEach(::sendGlosses)
    }

    fun setVocab(words: List<String>) {
        if (words == vocab) return
        vocab = words
        for (p in slots) if (p.section != null) p.call("yy.setVocab(${org.json.JSONArray(words)})")
    }

    fun setLooking(section: Int?, start: TextPoint?, end: TextPoint?) {
        looking = if (section != null && start != null && end != null) Triple(section, start, end) else null
        slots.forEach(::sendLooking)
    }

    /** 译 on or off. */
    fun setTranslating(on: Boolean) {
        if (on == translating) return
        translating = on
        if (on) {
            val cur = current
            if (cur != null && cur.section != null) translate(cur)
        } else {
            for (p in slots) if (p.section != null) p.call("yy.setTranslations(null)")
        }
    }

    fun clearTranslatedCache() {
        translated.clear()
    }

    private fun translate(page: SectionPage) {
        val section = page.section ?: return
        translated[section]?.let { done ->
            page.call("yy.setTranslations($done)")
            return
        }
        if (!translatingInFlight.add(section)) return
        scope.launch {
            try {
                val tr = translator ?: return@launch
                val raw = page.ask("yy.paragraphs()") as? org.json.JSONArray ?: return@launch
                if (raw.length() == 0) return@launch
                val paragraphs = (0 until raw.length()).mapNotNull { raw.optJSONObject(it) }
                if (paragraphs.isEmpty()) return@launch
                val texts = paragraphs.map { it.optString("text", "") }
                val (out, lang) = tr(section, texts)
                val items = org.json.JSONArray()
                for ((p, t) in paragraphs.zip(out)) {
                    if (!t.isNullOrEmpty() && p.has("last")) {
                        items.put(
                            org.json.JSONObject()
                                .put("last", p.optInt("last"))
                                .put("text", t)
                                .put("lang", lang),
                        )
                    }
                }
                if (items.length() == 0) {
                    if (translating && page === current) onTranslateFailed?.invoke(null)
                    return@launch
                }
                translated[section] = items
                if (translating && page.section == section) {
                    page.call("yy.setTranslations($items)")
                }
            } finally {
                translatingInFlight.remove(section)
            }
        }
    }

    private fun sendForeign(page: SectionPage) {
        if (page.section == null) return
        page.call("yy.setLookup($lookup)")
        if (vocab.isNotEmpty()) page.call("yy.setVocab(${org.json.JSONArray(vocab)})")
        sendGlosses(page)
        sendLooking(page)
        sendUnread(page)
        sendFlag(page)
        if (translating && page === current) translate(page)
    }

    /** 有声书: an amber line beside the paragraphs of a stretch the recording doesn't line up with. */
    private var flag: Triple<Int, Int, Int>? = null

    fun setFlag(section: Int, first: Int, last: Int) {
        val nw = Triple(section, first, last)
        if (nw == flag) return
        flag = nw
        slots.forEach(::sendFlag)
    }

    fun clearFlag() {
        if (flag == null) return
        flag = null
        slots.forEach(::sendFlag)
    }

    private fun sendFlag(page: SectionPage) {
        val f = flag
        if (f == null || page.section != f.first) return page.call("yy.setFlag(null)")
        page.call("yy.setFlag(${f.second}, ${f.third})")
    }

    /** 有声书: by section, the sentences the recording doesn't read, a shade lighter. */
    private var unread: Map<Int, List<Int>> = emptyMap()

    fun setUnread(all: Map<Int, List<Int>>) {
        if (all == unread) return
        unread = all
        slots.forEach(::sendUnread)
    }

    private fun sendUnread(page: SectionPage) {
        val section = page.section ?: return
        val list = unread[section]
        if (list.isNullOrEmpty()) return page.call("yy.setUnread(null)")
        page.call("yy.setUnread(${org.json.JSONArray(list)}, ${org.json.JSONObject.quote(app.yuyue.t("录音里没念"))})")
    }

    private fun sendGlosses(page: SectionPage) {
        val section = page.section ?: return
        val g = glosser ?: return
        if (glossesHidden) return page.call("yy.setGlosses(null)")
        val list = glossed.getOrPut(section) { g(section) }
        page.call("yy.setGlosses(${org.json.JSONArray(list.map { org.json.JSONObject().put("form", it.first).put("gloss", it.second) })})")
    }

    private fun sendLooking(page: SectionPage) {
        val l = looking
        if (l == null || page.section != l.first) return page.call("yy.setLooking(null, null)")
        page.call("yy.setLooking(${l.second.json()}, ${l.third.json()})")
    }

    /** Hands JavaScript to the current section's reader. */
    fun call(expression: String) = current?.call(expression)

    /** Hands JavaScript to every loaded section (settings, marks hidden). */
    fun callAll(expression: String) = slots.forEach { it.call(expression) }

    /** Reader settings changed: every loaded section lays out again. */
    fun apply(settings: org.json.JSONObject) = callAll("yy.applySettings(${settings})")

    override fun onSizeChanged(w: Int, h: Int, oldw: Int, oldh: Int) {
        super.onSizeChanged(w, h, oldw, oldh)
        pendingOpen?.let { (s, t) -> post { open(s, t) } }
        val g = geometry()
        if (appliedGeometry != null && g != appliedGeometry) {
            apply(org.json.JSONObject().put("margin", g.margin).put("top", g.top).put("bottom", g.bottom))
        }
        appliedGeometry = g
        render()
    }

    fun close() {
        animator?.cancel()
        scrollAnimator?.cancel()
        verticalAnimator?.cancel()
        scope.cancel()
        (slots + spare).forEach { it.webView.destroy() }
        slots.clear()
        spare.clear()
    }
}

/** Pure math for scrolled-mode vertical pull-to-cross gestures (unit-tested on JVM). */
internal object ScrolledPullMath {
    fun rubberBand(rawPull: Float, maxPull: Float): Float {
        if (rawPull <= 0f || maxPull <= 0f) return 0f
        return maxPull * (1f - 1f / (rawPull * 0.6f / maxPull + 1f))
    }

    fun isArmed(visualPull: Float, wasArmed: Boolean, threshold: Float, hysteresis: Float): Boolean =
        if (wasArmed) visualPull >= threshold - hysteresis else visualPull >= threshold

    /** Returns +1 when dragging up at bottom (next chapter), -1 when dragging down at top (prev), or 0. */
    fun edgeDirection(dy: Float, canScrollUp: Boolean, canScrollDown: Boolean): Int = when {
        dy < 0f && !canScrollDown -> 1
        dy > 0f && !canScrollUp -> -1
        else -> 0
    }
}
