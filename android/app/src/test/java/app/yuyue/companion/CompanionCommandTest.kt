package app.yuyue.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CompanionCommandTest {

    @Test
    fun slashQueryDetectsLeadingSlashAndStopsOnWhitespace() {
        assertNull(CompanionCommand.slashQuery(""))
        assertNull(CompanionCommand.slashQuery("普通问题"))
        assertNull(CompanionCommand.slashQuery("什么是 /recap"))
        assertEquals("", CompanionCommand.slashQuery("/"))
        assertEquals("", CompanionCommand.slashQuery("／"))
        assertEquals("re", CompanionCommand.slashQuery("/re"))
        assertEquals("人物", CompanionCommand.slashQuery("/人物"))
        assertNull(CompanionCommand.slashQuery("/characters 闰土"))
        assertNull(CompanionCommand.slashQuery("/recap\n第二行"))
    }

    @Test
    fun slashMatchingSupportsEnglishNamesAndChineseKeywords() {
        assertEquals(CompanionCommand.all.size, CompanionCommand.matching("").size)

        val byPrefix = CompanionCommand.matching("re")
        assertEquals(listOf("recap", "read"), byPrefix.take(2).map { it.name })

        val byChineseTitle = CompanionCommand.matching("人物")
        assertEquals("characters", byChineseTitle.firstOrNull()?.name)

        val byKeyword = CompanionCommand.matching("剧透")
        assertTrue(byKeyword.any { it.name == "whole" })

        assertTrue(CompanionCommand.matching("zzzz-no-match").isEmpty())
    }

    @Test
    fun slashParsingExtractsCommandAndMultilineArgument() {
        val clear = CompanionCommand.parse("/clear")
        assertNotNull(clear)
        assertEquals(CompanionCommand.Action.Clear, clear!!.command.action)
        assertEquals("", clear.argument)

        val fullWidth = CompanionCommand.parse("／人物关系 闰土")
        assertNotNull(fullWidth)
        assertEquals(CompanionCommand.Action.Characters, fullWidth!!.command.action)
        assertEquals("闰土", fullWidth.argument)
        val prompt = fullWidth.command.question(fullWidth.argument, hasQuote = false, chapterTitle = "故乡")
        assertTrue(prompt?.contains("闰土") == true)

        val multiline = CompanionCommand.parse("/whole 结局是什么\n还有伏笔吗")
        assertNotNull(multiline)
        assertEquals(CompanionCommand.Action.Scope(BookCompanion.Scope.Whole), multiline!!.command.action)
        assertEquals("结局是什么\n还有伏笔吗", multiline.argument)

        assertNull(CompanionCommand.parse("/unknown something"))
        assertNull(CompanionCommand.parse("第一行\n第二行"))
    }

    @Test
    fun queryClassificationAndStreamingCleanersMatchAppleBehavior() {
        assertEquals(BookCompanion.QueryKind.Chapter, BookCompanion.classify("这一章讲了什么？"))
        assertEquals(BookCompanion.QueryKind.Chapter, BookCompanion.classify("随便", CompanionCommand.Action.Chapter))
        assertEquals(BookCompanion.QueryKind.Broad, BookCompanion.classify("前面发生了什么？"))
        assertEquals(BookCompanion.QueryKind.Broad, BookCompanion.classify("梳理人物关系"))
        assertEquals(BookCompanion.QueryKind.Focused, BookCompanion.classify("这句是什么意思？"))

        val thinking = BookCompanion.streamingPreview("<think>先核对上下文", hideNotYet = true)
        assertEquals("", thinking.first)
        assertTrue(thinking.second)

        val mid = BookCompanion.streamingPreview("<think>ok</think>\n这里用了白描手法 [1]，对比很深 [2", hideNotYet = true)
        assertFalse(mid.second)
        assertEquals("这里用了白描手法 [1]，对比很深", mid.first)

        val notYet = BookCompanion.streamingPreview("没读到", hideNotYet = true)
        assertEquals("", notYet.first)

        val (body, next) = BookCompanion.split("<think>reasoning</think>\n第一点 [1]。\n第二点 [2]。\n接着问：问题甲｜问题乙")
        assertEquals("第一点 [1]。\n第二点 [2]。", body)
        assertEquals(listOf("问题甲", "问题乙"), next)
        assertEquals("第一点。\n第二点。", BookCompanion.noteText(body))
        assertEquals("已有想法\n\n第一点。", BookCompanion.mergeNote("已有想法", "第一点。"))
        assertEquals("已有想法\n\n第一点。", BookCompanion.mergeNote("已有想法\n\n第一点。", "第一点。"))
    }

    @Test
    fun paragraphTranslationDetectsScriptsAndParsesNumberedBatches() {
        assertEquals(
            app.yuyue.reader.ParagraphTranslation.Target.ZhModern,
            app.yuyue.reader.ParagraphTranslation.target(listOf("北冥有鱼，其名为鲲。"), "zh", uiChinese = true),
        )
        assertEquals(
            app.yuyue.reader.ParagraphTranslation.Target.ZhHans,
            app.yuyue.reader.ParagraphTranslation.target(listOf("国境の長いトンネルを抜けると雪国であった。"), "ja", uiChinese = true),
        )
        assertEquals(
            app.yuyue.reader.ParagraphTranslation.Target.ZhHans,
            app.yuyue.reader.ParagraphTranslation.target(listOf("It is a truth universally acknowledged."), "en", uiChinese = true),
        )
        assertEquals(
            app.yuyue.reader.ParagraphTranslation.Target.En,
            app.yuyue.reader.ParagraphTranslation.target(listOf("北冥有鱼，其名为鲲。"), "zh", uiChinese = false),
        )

        val raw = """
            <think>逐段翻译</think>
            [1] 北海里有一条鱼，它的名字叫鲲。
            鲲的体型非常巨大。

            【2】：它变化成为鸟，名字叫鹏。
        """.trimIndent()
        val parsed = app.yuyue.reader.ParagraphTranslation.parseNumbered(raw, count = 3)
        assertEquals("北海里有一条鱼，它的名字叫鲲。\n鲲的体型非常巨大。", parsed[0])
        assertEquals("它变化成为鸟，名字叫鹏。", parsed[1])
        assertNull(parsed[2])
        assertEquals(listOf("单段译文"), app.yuyue.reader.ParagraphTranslation.parseNumbered("单段译文", count = 1))
    }

    @Test
    fun customPromptsDeduplicateCapAndWireIntoSlashCommands() {
        var list = emptyList<String>()
        list = CompanionPrompts.adding("  拆解这句长难句  ", list)
        list = CompanionPrompts.adding("拆解这句长难句", list)
        list = CompanionPrompts.adding("文言译白话", list)
        assertEquals(listOf("拆解这句长难句", "文言译白话"), list)
        for (i in 1..10) list = CompanionPrompts.adding("问题 $i", list)
        assertEquals(CompanionPrompts.MAX_COUNT, list.size)
        list = CompanionPrompts.removing("文言译白话", list)
        assertEquals(CompanionPrompts.MAX_COUNT - 1, list.size)

        val cmds = CompanionCommand.customCommands(listOf("拆解这句长难句", "文言译白话"))
        assertEquals(listOf("p1", "p2"), cmds.map { it.name })
        val matched = CompanionCommand.matching("文言", cmds)
        assertEquals(CompanionCommand.Action.Custom("文言译白话"), matched.firstOrNull()?.action)
        val parsed = CompanionCommand.parse("/p1 注意倒装", cmds)
        assertEquals("拆解这句长难句\n注意倒装", parsed?.command?.question(parsed.argument, hasQuote = true, chapterTitle = null))
    }

    @Test
    fun readingHeatmapAlignsMondayAndComputesStreaks() {
        val jan1 = java.time.LocalDate.of(2026, 1, 1) // Thursday -> 3 leading pads
        val yearDays = (0L until 365L).map { jan1.plusDays(it) }
        val today = java.time.LocalDate.of(2026, 6, 1)
        val times = listOf(
            uniffi.yuyue_core.DayTime("2026-01-02", "b1", 600u, 0u),
            uniffi.yuyue_core.DayTime("2026-01-03", "b1", 1200u, 700u),
            uniffi.yuyue_core.DayTime("2026-01-04", "b2", 3600u, 0u),
            uniffi.yuyue_core.DayTime("2026-02-10", "b1", 300u, 0u),
        )
        val map = app.yuyue.me.ReadingHeatmap.build(yearDays, times, today)
        assertEquals(53, map.weeks.size)
        assertTrue(map.weeks.all { it.size == 7 })
        assertNull(map.weeks[0][0].date)
        assertNull(map.weeks[0][2].date)
        assertEquals("2026-01-01", map.weeks[0][3].dayKey)
        assertEquals(4, map.activeDays)
        assertEquals(3, map.longestStreak)
        assertEquals(0, app.yuyue.me.ReadingHeatmap.levelFor(0))
        assertEquals(1, app.yuyue.me.ReadingHeatmap.levelFor(600))
        assertEquals(2, app.yuyue.me.ReadingHeatmap.levelFor(1500))
        assertEquals(3, app.yuyue.me.ReadingHeatmap.levelFor(2400))
        assertEquals(4, app.yuyue.me.ReadingHeatmap.levelFor(4000))
    }
}
