package app.yuyue.reader

import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File

/**
 * Layer 3: Verifies Android reader bridge contract stays aligned with
 * `reader/schema/bridge-messages.schema.json` and `fixtures/conformance/reader-messages.json`.
 */
class ReaderBridgeContractTest {

    private fun repoRoot(): File {
        var dir: File? = File(System.getProperty("user.dir")).canonicalFile
        while (dir != null) {
            if (File(dir, "reader/schema/bridge-messages.schema.json").isFile) return dir
            dir = dir.parentFile
        }
        error("Could not locate repo root from ${System.getProperty("user.dir")}")
    }

    @Test
    fun androidReaderPagerHandlesAllRequiredContractMessageTypes() {
        val root = repoRoot()
        val schemaText = File(root, "reader/schema/bridge-messages.schema.json").readText(Charsets.UTF_8)
        val fixtureText = File(root, "fixtures/conformance/reader-messages.json").readText(Charsets.UTF_8)
        val pagerText = File(root, "android/app/src/main/java/app/yuyue/reader/ReaderPager.kt").readText(Charsets.UTF_8)

        val coreTypes = listOf("ready", "layout", "tap", "doubleTap", "selection", "activePage", "bubble", "scroll", "log")
        for (t in coreTypes) {
            assertTrue("Schema missing type $t", schemaText.contains("\"$t\""))
            assertTrue("Canonical fixture missing type $t", fixtureText.contains("\"type\": \"$t\""))
            assertTrue("Android ReaderPager.kt missing handler for $t", pagerText.contains("\"$t\""))
        }
    }
}
