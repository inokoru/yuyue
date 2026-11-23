package app.yuyue.design

import androidx.compose.ui.graphics.vector.ImageVector
import app.yuyue.library.CollectionIcon
import app.yuyue.shelf.PlaceIcons
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.lang.reflect.Modifier

class IconsTest {

    @Test
    fun allSfAlignedIconsParseValidVectorPaths() {
        val getters = Icons::class.java.declaredMethods.filter {
            Modifier.isPublic(it.modifiers) && it.parameterCount == 0 && it.returnType == ImageVector::class.java
        }
        assertTrue("Expected >60 icon getters on Icons, got ${getters.size}", getters.size > 60)
        for (method in getters) {
            val vector = method.invoke(Icons) as? ImageVector
            assertNotNull("Icon ${method.name} returned null", vector)
            assertEquals("Icon ${method.name} viewportWidth", 24f, vector!!.viewportWidth)
            assertEquals("Icon ${method.name} viewportHeight", 24f, vector.viewportHeight)
            assertTrue("Icon ${method.name} has no path nodes", vector.root.size > 0)
        }
    }

    @Test
    fun placeIconsAndCollectionIconsMatchIosMappings() {
        assertEquals(Icons.microphone, PlaceIcons.podcasts)
        assertEquals(Icons.chevronUpDown, PlaceIcons.switch)
        assertEquals(Icons.bookFill, PlaceIcons.filled(PlaceIcons.books))
        assertEquals(Icons.bookmark, PlaceIcons.filled(PlaceIcons.progress))
        assertEquals(Icons.microphoneFill, PlaceIcons.filled(PlaceIcons.podcasts))

        for ((symbol, _) in CollectionIcon.choices) {
            val v = CollectionIcon.vector(symbol)
            assertTrue("Collection symbol $symbol has empty vector", v.root.size > 0)
        }
    }
}
