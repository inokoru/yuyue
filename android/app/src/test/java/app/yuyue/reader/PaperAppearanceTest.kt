package app.yuyue.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.Paper
import uniffi.yuyue_core.ReaderSettings

class PaperAppearanceTest {

    private fun settings(paper: Paper) = ReaderSettings(
        paper = paper,
        mode = LayoutMode.PAGINATED,
        font = null,
        fontSize = 18.0,
        lineHeight = null,
        paragraphSpacing = null,
        justify = null,
        indent = null,
        compress = true,
        customPaper = null,
        fontFace = null,
    )

    @Before
    fun setUp() {
        PaperAppearance.resetForTest(follow = true, dark = false)
    }

    @Test
    fun autoPairsDayAndNightKeepingTypography() {
        assertEquals(Paper.DARK, PaperAppearance.resolve(settings(Paper.PLAIN), dark = true).preset)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(settings(Paper.KAMI), dark = true).preset)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(settings(Paper.SAGE), dark = true).preset)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(settings(Paper.EINK), dark = true).preset)
        assertEquals(Paper.PLAIN, PaperAppearance.resolve(settings(Paper.DARK), dark = false).preset)
        assertEquals(Paper.KAMI, PaperAppearance.resolve(settings(Paper.KAMI_NIGHT), dark = false).preset)
    }

    @Test
    fun pickingDarkAtNightKeepsFollowAndPreservesDayBase() {
        var current = settings(Paper.KAMI)
        PaperAppearance.syncAppearance(true)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(current).preset)

        // Pick Dark while at night: stays following, doesn't overwrite synced Kami day paper.
        PaperAppearance.pick(Paper.DARK, current) { current = it(current) }
        assertTrue(PaperAppearance.follow)
        assertEquals(Paper.KAMI, current.paper)
        assertEquals(Paper.DARK, PaperAppearance.resolve(current).preset)

        // Sunrise: returns to Kami.
        PaperAppearance.syncAppearance(false)
        assertEquals(Paper.KAMI, PaperAppearance.resolve(current).preset)

        // Next night: remembers Dark instead of auto-paired KamiNight.
        PaperAppearance.syncAppearance(true)
        assertEquals(Paper.DARK, PaperAppearance.resolve(current).preset)
    }

    @Test
    fun pickingOppositePolaritySwitchesImmediatelyAndDisablesFollow() {
        var current = settings(Paper.KAMI)
        PaperAppearance.syncAppearance(true)

        // At night, explicitly tap Sage (light): switches to Sage and disables follow.
        PaperAppearance.pick(Paper.SAGE, current) { current = it(current) }
        assertFalse(PaperAppearance.follow)
        assertEquals(Paper.SAGE, current.paper)
        assertEquals(Paper.SAGE, PaperAppearance.resolve(current).preset)
    }

    @Test
    fun turningFollowOffFreezesVisiblePaperWithoutFlashing() {
        var current = settings(Paper.KAMI)
        PaperAppearance.syncAppearance(true)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(current).preset)

        // Turn follow off at night: freezes visible KamiNight into settings, remembers Kami for day.
        PaperAppearance.setFollow(false, current) { current = it(current) }
        assertFalse(PaperAppearance.follow)
        assertEquals(Paper.KAMI_NIGHT, current.paper)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(current).preset)

        // Sunrise: still stays KamiNight while follow is off.
        PaperAppearance.syncAppearance(false)
        assertEquals(Paper.KAMI_NIGHT, PaperAppearance.resolve(current).preset)

        // Re-enable follow during day: restores remembered Kami.
        PaperAppearance.setFollow(true, current) { current = it(current) }
        assertTrue(PaperAppearance.follow)
        assertEquals(Paper.KAMI, PaperAppearance.resolve(current).preset)
    }

    @Test
    fun applyToHtmlRewritesPaperTextureAndCustomStyle() {
        val raw = """<!DOCTYPE html>
<html lang="zh-CN" data-yy-paper="kami" data-yy-mode="paginated" data-yy-texture="xuan" style="--yy-fs:18px;--yy-p-bg:#F5F4ED;color-scheme:light">
<body><p>Hello</p></body></html>"""

        val nightPreset = PaperAppearance.applyToHTML(raw, "kaminight", null, null)
        assertTrue(nightPreset.contains("""data-yy-paper="kaminight""""))
        assertFalse(nightPreset.contains("data-yy-texture="))
        assertTrue(nightPreset.contains("""style="--yy-fs:18px""""))
        assertFalse(nightPreset.contains("--yy-p-bg:"))

        val nightCustom = PaperAppearance.applyToHTML(
            raw,
            "dark",
            "--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark",
            "linen",
        )
        assertTrue(nightCustom.contains("""data-yy-paper="dark""""))
        assertTrue(nightCustom.contains("""data-yy-texture="linen""""))
        assertTrue(nightCustom.contains("""style="--yy-fs:18px;--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark""""))
    }

    @Test
    fun fontSettingsForwardFontFaceAndSystemSansToReaderScript() {
        var s = settings(Paper.KAMI)
        s = FontShelf.apply("system", s)
        assertEquals(uniffi.yuyue_core.Font.SANS, s.font)
        assertEquals("sans", s.cssFont())

        s = FontShelf.apply("notoserif", s)
        assertEquals("notoserif", s.fontFace)
        assertEquals("\"yy-notoserif\", var(--yy-serif)", s.cssFont())

        s = s.copy(fontFace = "file:我的字.ttf", font = uniffi.yuyue_core.Font.SERIF)
        assertEquals("\"yy-file:我的字.ttf\", var(--yy-serif)", s.cssFont())
    }

    @Test
    fun readerAndAudiobookModelsDeclareAllBackingFieldsBeforeInit() {
        val guarded = listOf(
            ReaderModel::class.java,
            app.yuyue.speech.AudiobookPlayer::class.java,
            app.yuyue.speech.Recordings::class.java,
            app.yuyue.pdf.PdfModel::class.java,
            app.yuyue.library.Library::class.java,
        )
        for (cls in guarded) {
            val instanceFields = cls.declaredFields.filter { !java.lang.reflect.Modifier.isStatic(it.modifiers) }
            val sentinelIndex = instanceFields.indexOfFirst { it.name == "initCompleted" }
            assertTrue("Missing initCompleted sentinel on ${cls.simpleName}", sentinelIndex >= 0)
            val afterInit = instanceFields.drop(sentinelIndex + 1).map { it.name }
            assertEquals(
                "Instance backing fields declared after init {} in ${cls.simpleName} risk NPE during init (#52): $afterInit",
                emptyList<String>(),
                afterInit,
            )
        }
    }
}
