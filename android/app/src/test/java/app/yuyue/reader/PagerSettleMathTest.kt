package app.yuyue.reader

import app.yuyue.me.General
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PagerSettleMathTest {

    @Test
    fun forwardChapterBoundaryResolvesNextSectionEvenWhenSpringUndershoots() {
        // Critically damped spring from page 4 -> 5 stops at ~4.9787f at t=0.32s.
        assertEquals(
            PagerSettleMath.Landing.NextSection,
            PagerSettleMath.resolveLanding(position = 4.9787f, pageCount = 5, hasPrev = true, hasNext = true),
        )
        assertEquals(
            PagerSettleMath.Landing.NextSection,
            PagerSettleMath.resolveLanding(position = 5.0f, pageCount = 5, hasPrev = true, hasNext = true),
        )
    }

    @Test
    fun backwardChapterBoundaryResolvesPreviousSectionEvenWhenSpringUndershoots() {
        assertEquals(
            PagerSettleMath.Landing.PreviousSection,
            PagerSettleMath.resolveLanding(position = -0.9787f, pageCount = 5, hasPrev = true, hasNext = true),
        )
        assertEquals(
            PagerSettleMath.Landing.PreviousSection,
            PagerSettleMath.resolveLanding(position = -1.0f, pageCount = 5, hasPrev = true, hasNext = true),
        )
    }

    @Test
    fun missingNeighboursClampInsideValidPageRange() {
        assertEquals(
            PagerSettleMath.Landing.Page(4),
            PagerSettleMath.resolveLanding(position = 5.0f, pageCount = 5, hasPrev = false, hasNext = false),
        )
        assertEquals(
            PagerSettleMath.Landing.Page(0),
            PagerSettleMath.resolveLanding(position = -1.0f, pageCount = 5, hasPrev = false, hasNext = false),
        )
    }

    @Test
    fun normalPageSettlesStayInsideCurrentSection() {
        assertEquals(
            PagerSettleMath.Landing.Page(2),
            PagerSettleMath.resolveLanding(position = 1.98f, pageCount = 5, hasPrev = true, hasNext = true),
        )
        assertEquals(
            PagerSettleMath.Landing.Page(0),
            PagerSettleMath.resolveLanding(position = 0.02f, pageCount = 1, hasPrev = true, hasNext = true),
        )
    }

    @Test
    fun reopenLastBookDefaultsOnAndOnlyReopensWhenExitedInAvailableBook() {
        assertTrue(General.REOPEN_LAST_DEFAULT)
        assertEquals(
            "book-1",
            General.bookToReopen(reopenLast = General.REOPEN_LAST_DEFAULT, lastBook = "book-1", availableIds = setOf("book-1", "book-2")),
        )
        assertNull(
            General.bookToReopen(reopenLast = true, lastBook = "", availableIds = setOf("book-1")),
        )
        assertNull(
            General.bookToReopen(reopenLast = false, lastBook = "book-1", availableIds = setOf("book-1")),
        )
        assertNull(
            General.bookToReopen(reopenLast = true, lastBook = "deleted-book", availableIds = setOf("book-1")),
        )
    }
}
