package app.yuyue.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ScrolledPullMathTest {

    @Test
    fun rubberBandIsMonotonicAndBounded() {
        assertEquals(0f, ScrolledPullMath.rubberBand(0f, 160f), 1e-4f)
        assertEquals(0f, ScrolledPullMath.rubberBand(-20f, 160f), 1e-4f)

        val small = ScrolledPullMath.rubberBand(50f, 160f)
        val medium = ScrolledPullMath.rubberBand(150f, 160f)
        val large = ScrolledPullMath.rubberBand(1000f, 160f)
        assertTrue(small > 0f)
        assertTrue(medium > small)
        assertTrue(large > medium)
        assertTrue(large < 160f)
    }

    @Test
    fun armingUsesHysteresisAroundThreshold() {
        val threshold = 56f
        val hysteresis = 8f
        assertFalse(ScrolledPullMath.isArmed(55f, wasArmed = false, threshold, hysteresis))
        assertTrue(ScrolledPullMath.isArmed(56f, wasArmed = false, threshold, hysteresis))
        // Small pull-back stays armed inside hysteresis band.
        assertTrue(ScrolledPullMath.isArmed(49f, wasArmed = true, threshold, hysteresis))
        assertFalse(ScrolledPullMath.isArmed(47f, wasArmed = true, threshold, hysteresis))
    }

    @Test
    fun edgeDirectionHandlesNormalAndShortChapters() {
        // Mid-chapter: neither direction crosses.
        assertEquals(0, ScrolledPullMath.edgeDirection(dy = -40f, canScrollUp = true, canScrollDown = true))
        assertEquals(0, ScrolledPullMath.edgeDirection(dy = 40f, canScrollUp = true, canScrollDown = true))

        // At bottom: dragging up crosses forward; dragging down scrolls normally.
        assertEquals(1, ScrolledPullMath.edgeDirection(dy = -40f, canScrollUp = true, canScrollDown = false))
        assertEquals(0, ScrolledPullMath.edgeDirection(dy = 40f, canScrollUp = true, canScrollDown = false))

        // At top: dragging down crosses backward; dragging up scrolls normally.
        assertEquals(-1, ScrolledPullMath.edgeDirection(dy = 40f, canScrollUp = false, canScrollDown = true))
        assertEquals(0, ScrolledPullMath.edgeDirection(dy = -40f, canScrollUp = false, canScrollDown = true))

        // Short chapter (maxScrollY == 0): both edges coincide; direction decides.
        assertEquals(1, ScrolledPullMath.edgeDirection(dy = -40f, canScrollUp = false, canScrollDown = false))
        assertEquals(-1, ScrolledPullMath.edgeDirection(dy = 40f, canScrollUp = false, canScrollDown = false))
        assertEquals(0, ScrolledPullMath.edgeDirection(dy = 0f, canScrollUp = false, canScrollDown = false))
    }
}
