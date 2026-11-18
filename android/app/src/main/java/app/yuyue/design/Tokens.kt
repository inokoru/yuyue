// generated from design/tokens.json — do not edit (run `node design/generate.mjs`)
package app.yuyue.design

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

/** UI colours (界面) for one appearance. Tint-dependent colours live on [YYTintColors]. */
data class YYPalette(
    val bg: Color,
    val card: Color,
    val inset: Color,
    val line: Color,
    val t1: Color,
    val t2: Color,
    val t3: Color,
    val tint: Color,
    val player: Color,
    val hlY: Color,
    val hlR: Color,
    val hlB: Color,
    val mkY: Color,
    val mkB: Color,
    val mkP: Color,
    val ok: Color,
    val danger: Color,
    val warn: Color,
    val glass: Color,
    val fade0: Color,
    val pill: Color,
    val pillLine: Color
)

object YYColors {
    val Light = YYPalette(
        bg = Color(0xFFF8F5F0L),
        card = Color(0xFFFFFFFFL),
        inset = Color(0xFFEFEEEBL),
        line = Color(0xFFE7E5E1L),
        t1 = Color(0xFF2A2B2EL),
        t2 = Color(0xFF6E6B68L),
        t3 = Color(0xFF8A8682L),
        tint = Color(0xFFC9C4BDL),
        player = Color(0xFFECEBE8L),
        hlY = Color(0xFFFBE7A0L),
        hlR = Color(0xFFEF7F71L),
        hlB = Color(0xFFC4E3F4L),
        mkY = Color(0xFFFBE7A0L),
        mkB = Color(0xFFCFE6F4L),
        mkP = Color(0xFFF6C7C0L),
        ok = Color(0xFF3E9B6AL),
        danger = Color(0xFFC2473AL),
        warn = Color(0xFFB7791FL),
        glass = Color(0xDBFFFFFFL),
        fade0 = Color(0x00F8F5F0L),
        pill = Color(0xFFFBFAF7L),
        pillLine = Color(0x2950412DL)
    )
    val Dark = YYPalette(
        bg = Color(0xFF1A1613L),
        card = Color(0xFF1B1C1EL),
        inset = Color(0xFF26272AL),
        line = Color(0xFF2C2D30L),
        t1 = Color(0xFFEDEDEEL),
        t2 = Color(0xFF9A9895L),
        t3 = Color(0xFF8E8C89L),
        tint = Color(0xFF4E4C49L),
        player = Color(0xFF18191BL),
        hlY = Color(0xFFFBE7A0L),
        hlR = Color(0xFFEF7F71L),
        hlB = Color(0xFFC4E3F4L),
        mkY = Color(0x42FBE7A0L),
        mkB = Color(0x38C4E3F4L),
        mkP = Color(0x3DF6C7C0L),
        ok = Color(0xFF5DBB88L),
        danger = Color(0xFFE5806FL),
        warn = Color(0xFFD9A54AL),
        glass = Color(0xDB1E1F22L),
        fade0 = Color(0x001A1613L),
        pill = Color(0xFF2C2D30L),
        pillLine = Color(0x1AFFFFFFL)
    )
}

/** Font families: `css` is the reader stack, `androidNames` are tried first, then [system]. */
enum class YYFont(val css: String, val androidNames: List<String>, val system: FontFamily) {
    Sans("-apple-system,BlinkMacSystemFont,\"PingFang SC\",\"Hiragino Sans GB\",\"Microsoft YaHei\",sans-serif", listOf(), FontFamily.SansSerif),
    Serif("\"Noto Serif SC\",\"Source Han Serif SC\",\"Songti SC\",\"STSong\",serif", listOf("Noto Serif CJK SC", "Source Han Serif SC"), FontFamily.Serif),
    Kai("\"LXGW WenKai\",\"Kaiti SC\",\"STKaiti\",\"KaiTi\",serif", listOf("LXGW WenKai"), FontFamily.Serif),
    Kaiti("\"Kaiti SC\",\"STKaiti\",\"KaiTi\",serif", listOf(), FontFamily.Serif),
    Wenkai("\"LXGW WenKai\",\"Kaiti SC\",\"STKaiti\",serif", listOf(), FontFamily.Serif),
    Mono("ui-monospace,\"SF Mono\",Menlo,Consolas,monospace", listOf(), FontFamily.Monospace),
    LatinSerif("Georgia,\"Times New Roman\",serif", listOf(), FontFamily.Serif);
}

/** 主题色 for one appearance. */
data class YYTintColors(
    val black: Color,
    val onBlack: Color,
    val accent: Color,
    val accentSoft: Color,
    val edge: Color,
    val fill: Color,
    val soft: Color,
    val onSoft: Color
)

data class YYTint(
    val id: String,
    val name: String,
    val light: YYTintColors,
    val dark: YYTintColors,
    /** Accent drawn on light papers (素白、纸本) and dark papers (暗黑、纸本 · 夜). */
    val paperAccentLight: Color,
    val paperAccentDark: Color,
    val playPlain: Color,
    val playKami: Color,
    val playDark: Color,
    val playKaminight: Color,
    val playSage: Color,
) {
    /** The play button colour on a paper. The e-ink paper keeps its own. */
    fun play(paper: YYPaper): Color = when (paper.id) {
        "plain" -> playPlain
        "kami" -> playKami
        "dark" -> playDark
        "kaminight" -> playKaminight
        "sage" -> playSage
        else -> paper.main
    }
}

object YYTints {
    val Camel = YYTint(
        id = "camel",
        name = "暖纸",
        light = YYTintColors(black = Color(0xFFF2E7DBL), onBlack = Color(0xFF8A653FL), accent = Color(0xFF8A653FL), accentSoft = Color(0xFFF2E7DBL), edge = Color(0xFFD4B396L), fill = Color(0xFF8A653FL), soft = Color(0xFFF2E7DBL), onSoft = Color(0xFF8A653FL)),
        dark = YYTintColors(black = Color(0xFF2D2722L), onBlack = Color(0xFFA0754FL), accent = Color(0xFFA0754FL), accentSoft = Color(0xFF2D2722L), edge = Color(0xFF5A4A3AL), fill = Color(0xFFA0754FL), soft = Color(0xFF2D2722L), onSoft = Color(0xFFA0754FL)),
        paperAccentLight = Color(0xFF8A653FL),
        paperAccentDark = Color(0xFFA0754FL),
        playPlain = Color(0xFF8A653FL),
        playKami = Color(0xFF8A653FL),
        playDark = Color(0xFFA0754FL),
        playKaminight = Color(0xFFA0754FL),
        playSage = Color(0xFF8A653FL),
    )
    val Sage = YYTint(
        id = "sage",
        name = "草木",
        light = YYTintColors(black = Color(0xFFDEEBE1L), onBlack = Color(0xFF4F7657L), accent = Color(0xFF4F7657L), accentSoft = Color(0xFFDEEBE1L), edge = Color(0xFF9EBDA6L), fill = Color(0xFF4F7657L), soft = Color(0xFFDEEBE1L), onSoft = Color(0xFF4F7657L)),
        dark = YYTintColors(black = Color(0xFF252D25L), onBlack = Color(0xFF5A8A6AL), accent = Color(0xFF5A8A6AL), accentSoft = Color(0xFF252D25L), edge = Color(0xFF3A4A3AL), fill = Color(0xFF5A8A6AL), soft = Color(0xFF252D25L), onSoft = Color(0xFF5A8A6AL)),
        paperAccentLight = Color(0xFF4F7657L),
        paperAccentDark = Color(0xFF5A8A6AL),
        playPlain = Color(0xFF4F7657L),
        playKami = Color(0xFF4F7657L),
        playDark = Color(0xFF5A8A6AL),
        playKaminight = Color(0xFF5A8A6AL),
        playSage = Color(0xFF4F7657L),
    )
    val Mist = YYTint(
        id = "mist",
        name = "烟雨",
        light = YYTintColors(black = Color(0xFFDDE7F0L), onBlack = Color(0xFF4F6D88L), accent = Color(0xFF4F6D88L), accentSoft = Color(0xFFDDE7F0L), edge = Color(0xFF9FB2C5L), fill = Color(0xFF4F6D88L), soft = Color(0xFFDDE7F0L), onSoft = Color(0xFF4F6D88L)),
        dark = YYTintColors(black = Color(0xFF252D35L), onBlack = Color(0xFF6A8A9AL), accent = Color(0xFF6A8A9AL), accentSoft = Color(0xFF252D35L), edge = Color(0xFF3A4A5AL), fill = Color(0xFF6A8A9AL), soft = Color(0xFF252D35L), onSoft = Color(0xFF6A8A9AL)),
        paperAccentLight = Color(0xFF4F6D88L),
        paperAccentDark = Color(0xFF6A8A9AL),
        playPlain = Color(0xFF4F6D88L),
        playKami = Color(0xFF4F6D88L),
        playDark = Color(0xFF6A8A9AL),
        playKaminight = Color(0xFF6A8A9AL),
        playSage = Color(0xFF4F6D88L),
    )
    val Blush = YYTint(
        id = "blush",
        name = "海棠",
        light = YYTintColors(black = Color(0xFFF2DFE1L), onBlack = Color(0xFFA5656FL), accent = Color(0xFFA5656FL), accentSoft = Color(0xFFF2DFE1L), edge = Color(0xFFD2A1A7L), fill = Color(0xFFA5656FL), soft = Color(0xFFF2DFE1L), onSoft = Color(0xFFA5656FL)),
        dark = YYTintColors(black = Color(0xFF332A2AL), onBlack = Color(0xFFA5656FL), accent = Color(0xFFA5656FL), accentSoft = Color(0xFF332A2AL), edge = Color(0xFF5A4A4AL), fill = Color(0xFFA5656FL), soft = Color(0xFF332A2AL), onSoft = Color(0xFFA5656FL)),
        paperAccentLight = Color(0xFFA5656FL),
        paperAccentDark = Color(0xFFA5656FL),
        playPlain = Color(0xFFA5656FL),
        playKami = Color(0xFFA5656FL),
        playDark = Color(0xFFA5656FL),
        playKaminight = Color(0xFFA5656FL),
        playSage = Color(0xFFA5656FL),
    )
    val Default = Camel
    val All = listOf(Camel, Sage, Mist, Blush)
}

/** Body typography defaults of a paper. Sizes are design pixels at 14px text: scale by fontSize / 14. */
data class YYPaperText(
    val font: YYFont,
    val lineHeight: Float,
    val paragraphSpacingEm: Float,
    val indentEm: Float,
    val justify: Boolean,
    val letterSpacingEm: Float,
    val sizePx: Float,
)

data class YYPaperTitle(
    val font: YYFont,
    val sizePx: Float,
    val weight: Int,
    val lineHeight: Float,
    val letterSpacingEm: Float,
    /** A short accent rule under the title (纸本 papers). */
    val rule: Boolean,
)

data class YYPaperMeta(
    val font: YYFont,
    val sizePx: Float,
    val border: Boolean,
    val letterSpacingEm: Float,
    val marginTopPx: Float,
    val paddingBottomPx: Float,
)

/** 纸页. Paper colours do not follow the system appearance. */
data class YYPaper(
    val id: String,
    val name: String,
    val isDark: Boolean,
    val hasNoiseTexture: Boolean,
    val bg: Color,
    val ink: Color,
    val ink2: Color,
    val line: Color,
    val accent: Color,
    val hl: Color,
    val wave: Color,
    val glass: Color,
    val main: Color,
    val onMain: Color,
    /** Accent for marks on the paper (search hits, footnote references, bookmarks). */
    val paperAccent: Color,
    val text: YYPaperText,
    val title: YYPaperTitle,
    val meta: YYPaperMeta,
)

object YYPapers {
    val Plain = YYPaper(
        id = "plain",
        name = "素白",
        isDark = false,
        hasNoiseTexture = false,
        bg = Color(0xFFFFFFFFL),
        ink = Color(0xFF2A2B2EL),
        ink2 = Color(0xFF777571L),
        line = Color(0xFFEFEEEBL),
        accent = Color(0xFF2A2B2EL),
        hl = Color(0xFFFBE7A0L),
        wave = Color(0xFFEF7F71L),
        glass = Color(0xD1FFFFFFL),
        main = Color(0xFF2B4C7EL),
        onMain = Color(0xFFFFFFFFL),
        paperAccent = Color(0xFF2B4C7EL),
        text = YYPaperText(YYFont.Sans, 2.15f, 1.1f, 0.0f, true, 0.02f, 14.0f),
        title = YYPaperTitle(YYFont.Sans, 22.0f, 700, 1.4f, 0.0f, false),
        meta = YYPaperMeta(YYFont.Sans, 10.5f, true, 0.0f, 10.0f, 14.0f),
    )
    val Dark = YYPaper(
        id = "dark",
        name = "暗黑",
        isDark = true,
        hasNoiseTexture = false,
        bg = Color(0xFF121314L),
        ink = Color(0xFFD6D6D8L),
        ink2 = Color(0xFF7E7E83L),
        line = Color(0xFF26272AL),
        accent = Color(0xFFD6D6D8L),
        hl = Color(0xFF51461FL),
        wave = Color(0xFFE57A6EL),
        glass = Color(0xD1222326L),
        main = Color(0xFF8FA8C8L),
        onMain = Color(0xFF121314L),
        paperAccent = Color(0xFF8FA8C8L),
        text = YYPaperText(YYFont.Sans, 2.15f, 1.1f, 0.0f, true, 0.02f, 14.0f),
        title = YYPaperTitle(YYFont.Sans, 22.0f, 700, 1.4f, 0.0f, false),
        meta = YYPaperMeta(YYFont.Sans, 10.5f, true, 0.0f, 10.0f, 14.0f),
    )
    val Kami = YYPaper(
        id = "kami",
        name = "纸本",
        isDark = false,
        hasNoiseTexture = false,
        bg = Color(0xFFF5F4EDL),
        ink = Color(0xFF26251FL),
        ink2 = Color(0xFF716E62L),
        line = Color(0xFFE2DFD3L),
        accent = Color(0xFF1B365DL),
        hl = Color(0xFFE9E0C2L),
        wave = Color(0xFF1B365DL),
        glass = Color(0xD6FAF9F4L),
        main = Color(0xFF1B365DL),
        onMain = Color(0xFFF5F4EDL),
        paperAccent = Color(0xFF2B4C7EL),
        text = YYPaperText(YYFont.Serif, 2.05f, 0.3f, 2.0f, true, 0.01f, 14.5f),
        title = YYPaperTitle(YYFont.Serif, 25.0f, 700, 1.4f, 0.05f, true),
        meta = YYPaperMeta(YYFont.Serif, 10.5f, false, 0.06f, 0.0f, 4.0f),
    )
    val Kaminight = YYPaper(
        id = "kaminight",
        name = "纸本 · 夜",
        isDark = true,
        hasNoiseTexture = false,
        bg = Color(0xFF1B1A17L),
        ink = Color(0xFFE3DED0L),
        ink2 = Color(0xFF8C8779L),
        line = Color(0xFF2E2C27L),
        accent = Color(0xFF8FA8C8L),
        hl = Color(0xFF463F2CL),
        wave = Color(0xFF8FA8C8L),
        glass = Color(0xD6282622L),
        main = Color(0xFF8FA8C8L),
        onMain = Color(0xFF1B1A17L),
        paperAccent = Color(0xFF8FA8C8L),
        text = YYPaperText(YYFont.Serif, 2.05f, 0.3f, 2.0f, true, 0.01f, 14.5f),
        title = YYPaperTitle(YYFont.Serif, 25.0f, 700, 1.4f, 0.05f, true),
        meta = YYPaperMeta(YYFont.Serif, 10.5f, false, 0.06f, 0.0f, 4.0f),
    )
    val Sage = YYPaper(
        id = "sage",
        name = "芦苇",
        isDark = false,
        hasNoiseTexture = false,
        bg = Color(0xFFE6E7DCL),
        ink = Color(0xFF2A2C24L),
        ink2 = Color(0xFF63665AL),
        line = Color(0xFFD3D5C6L),
        accent = Color(0xFF2A2C24L),
        hl = Color(0xFFF3E3A4L),
        wave = Color(0xFFC9604FL),
        glass = Color(0xD6EAEBE1L),
        main = Color(0xFF2B4C7EL),
        onMain = Color(0xFFFFFFFFL),
        paperAccent = Color(0xFF2B4C7EL),
        text = YYPaperText(YYFont.Serif, 2.05f, 0.3f, 2.0f, true, 0.01f, 14.5f),
        title = YYPaperTitle(YYFont.Serif, 25.0f, 700, 1.4f, 0.05f, true),
        meta = YYPaperMeta(YYFont.Serif, 10.5f, false, 0.06f, 0.0f, 4.0f),
    )
    val Eink = YYPaper(
        id = "eink",
        name = "铅纸",
        isDark = false,
        hasNoiseTexture = true,
        bg = Color(0xFFE9E8E3L),
        ink = Color(0xFF141414L),
        ink2 = Color(0xFF555555L),
        line = Color(0xFFC9C8C2L),
        accent = Color(0xFF141414L),
        hl = Color(0x00000000L),
        wave = Color(0xFF141414L),
        glass = Color(0xFFE9E8E3L),
        main = Color(0xFF141414L),
        onMain = Color(0xFFE9E8E3L),
        paperAccent = Color(0xFF141414L),
        text = YYPaperText(YYFont.Serif, 2.05f, 0.3f, 2.0f, true, 0.02f, 14.0f),
        title = YYPaperTitle(YYFont.Serif, 24.0f, 700, 1.4f, 0.05f, false),
        meta = YYPaperMeta(YYFont.Sans, 10.5f, true, 0.0f, 10.0f, 14.0f),
    )
    val All = listOf(Plain, Dark, Kami, Kaminight, Sage, Eink)
}

/** English body text replaces the paper's font, line height, letter spacing, alignment and size. */
object YYLatinText {
    val font = YYFont.LatinSerif
    const val LINE_HEIGHT = 2.25f
    const val LETTER_SPACING_EM = 0.0f
    const val JUSTIFY = false
    const val SIZE_PX = 13.5f
}

/** One shadow layer in CSS order. spread > 0 with blur 0 is a hairline ring (draw it as a border). */
data class YYShadowLayer(val x: Float, val y: Float, val blur: Float, val spread: Float, val color: Color)

object YYShadows {
    val CardLight = listOf(YYShadowLayer(0.0f, 1.0f, 2.0f, 0.0f, Color(0x0A332E29L)), YYShadowLayer(0.0f, 8.0f, 24.0f, 0.0f, Color(0x0D332E29L)))
    val CardDark = listOf(YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0x0DFFFFFFL)))
    val FloatLight = listOf(YYShadowLayer(0.0f, 10.0f, 30.0f, 0.0f, Color(0x24332E29L)), YYShadowLayer(0.0f, 1.0f, 3.0f, 0.0f, Color(0x14332E29L)))
    val FloatDark = listOf(YYShadowLayer(0.0f, 10.0f, 30.0f, 0.0f, Color(0x80000000L)), YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0x12FFFFFFL)))
    val RingLight = listOf(YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0xFFE7E5E1L)), YYShadowLayer(0.0f, 1.0f, 2.0f, 0.0f, Color(0x0A332E29L)))
    val RingDark = listOf(YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0x0FFFFFFFL)))
    val RingFloatLight = listOf(YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0xFFE7E5E1L)), YYShadowLayer(0.0f, 8.0f, 24.0f, 0.0f, Color(0x1A332E29L)))
    val RingFloatDark = listOf(YYShadowLayer(0.0f, 0.0f, 0.0f, 0.5f, Color(0x14FFFFFFL)), YYShadowLayer(0.0f, 16.0f, 40.0f, 0.0f, Color(0x80000000L)))
}

object YYRadius {
    val Card: Dp = 16.dp
    val Block: Dp = 14.dp
    val Sheet: Dp = 22.dp
    val Capsule: Dp = 24.dp
    val Control: Dp = 10.dp
    val Chip: Dp = 999.dp
}

/** 动效 durations in milliseconds; the animation specs themselves are YYMotion (Motion.kt). */
object YYDuration {
    const val Quick = 140
    const val Standard = 240
    const val Layout = 350
    const val Exit = 120
}

/** The reading line (墨线) and reader defaults. */
object YYReader {
    const val PROTOTYPE_TEXT_PX = 14.0f
    const val INK_WIDTH_PX = 1.5f
    const val INK_GAP_PX = 2.0f
    const val FALLBACK_SECONDS_PER_CHAR = 0.12f
    const val FALLBACK_MIN_SECONDS = 1.8f
    const val DEFAULT_FONT_SIZE = 18.0f
    const val DEFAULT_MARGIN = 24.0f
    const val DEFAULT_TOP = 56.0f
    const val DEFAULT_BOTTOM = 88.0f
    const val DEFAULT_PAPER = "plain"
}
