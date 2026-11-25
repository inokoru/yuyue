package app.yuyue.sync

import app.yuyue.me.ConflictDiff
import app.yuyue.me.DiffSpan
import app.yuyue.reader.ReplaceRuleOrder
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Pure-JVM tests for Android's sync-conflict diffing and replace-rule reordering helpers. */
class ConflictAndRuleLogicTest {

    @Test
    fun conflictDiffHighlightsOnlyMiddleChange() {
        val (a, b) = ConflictDiff.compute(
            "这一段每次读都会停下来。",
            "这一段每次读都会停下来。他探身下去的样子，和我父亲一模一样。",
        )
        assertEquals(DiffSpan("这一段每次读都会停下来。", "", ""), a)
        assertEquals(DiffSpan("这一段每次读都会停下来。", "他探身下去的样子，和我父亲一模一样。", ""), b)
    }

    @Test
    fun conflictDiffPreservesSharedPrefixAndSuffix() {
        val (a, b) = ConflictDiff.compute("今天读了第一章，很有感触。", "今天读了第二章，很有感触。")
        assertEquals(DiffSpan("今天读了第", "一", "章，很有感触。"), a)
        assertEquals(DiffSpan("今天读了第", "二", "章，很有感触。"), b)
    }

    @Test
    fun conflictDiffHandlesIdenticalAndEmptyStrings() {
        val (a, b) = ConflictDiff.compute("相同", "相同")
        assertEquals(DiffSpan("相同", "", ""), a)
        assertEquals(DiffSpan("相同", "", ""), b)

        val (c, d) = ConflictDiff.compute("", "新增想法")
        assertEquals(DiffSpan("", "", ""), c)
        assertEquals(DiffSpan("", "新增想法", ""), d)
    }

    @Test
    fun replaceRuleOrderComputesMidpointAndEdges() {
        val orders = listOf(1.0, 2.0, 4.0)
        // Move last rule to middle (index 1) -> between 1.0 and 2.0 = 1.5
        assertEquals(1.5, ReplaceRuleOrder.orderForMove(orders, from = 2, to = 1), 1e-9)
        // Move middle rule to top (index 0) -> before 1.0 = 0.0
        assertEquals(0.0, ReplaceRuleOrder.orderForMove(orders, from = 1, to = 0), 1e-9)
        // Move top rule to bottom (index 2) -> after 4.0 = 5.0
        assertEquals(5.0, ReplaceRuleOrder.orderForMove(orders, from = 0, to = 2), 1e-9)
        // Moving to same index is a no-op
        assertEquals(2.0, ReplaceRuleOrder.orderForMove(orders, from = 1, to = 1), 1e-9)
        // Moving down places strictly between neighbours
        val mid = ReplaceRuleOrder.orderForMove(orders, from = 0, to = 1)
        assertTrue(mid > 2.0 && mid < 4.0)
    }
}
