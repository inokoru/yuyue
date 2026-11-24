package app.yuyue.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Test

class FontShelfTest {

    @Test
    fun sanitizeFontBaseStripsForbiddenCharsAndLeadingDots() {
        assertEquals("Noto Serif SC", FontShelf.sanitizeFontBase("Noto Serif SC"))
        assertEquals("霞鹜文楷", FontShelf.sanitizeFontBase("霞鹜文楷"))

        val nasty = FontShelf.sanitizeFontBase("..\"'a/b\\c<d>e;f{g}h")
        assertFalse(nasty.startsWith("."))
        for (forbidden in listOf('/', '\\', '"', '\'', '<', '>', ';', '{', '}')) {
            assertFalse("Should not contain '$forbidden'", nasty.contains(forbidden))
        }
        assertEquals("a-b-cdefgh", nasty)

        // Leading dot exposed after stripping quotes/whitespace must also be trimmed.
        assertEquals("hidden", FontShelf.sanitizeFontBase("  \"..hidden\"  "))
        assertEquals("font", FontShelf.sanitizeFontBase("..."))
        assertEquals("font", FontShelf.sanitizeFontBase("\"'<>;{}"))
        assertEquals("font", FontShelf.sanitizeFontBase("   "))
    }
}
