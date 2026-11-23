package app.yuyue.library

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test

class RemoteLibraryTest {

    @Test
    fun opdsAcceptsAudioMp4AcquisitionEntries() {
        val entry = OPDS.make(
            id0 = "urn:book:beiying-audio",
            title = "背影（朗读）",
            authors = listOf("朱自清"),
            links = listOf(
                mapOf(
                    "rel" to "http://opds-spec.org/acquisition",
                    "href" to "/media/beiying-short.m4a",
                    "type" to "audio/mp4",
                    "length" to "1468006",
                ),
            ),
            authorSep = "、",
        ) { href -> RemoteClient.resolve("https://books.example.com/opds/feed.xml", href) }

        assertNotNull(entry)
        assertEquals("背影（朗读）", entry!!.title)
        assertEquals("朱自清", entry.author)
        assertEquals("https://books.example.com/media/beiying-short.m4a", entry.book)
        assertEquals("audio/mp4", entry.type)
        assertEquals("M4A", entry.format)
        assertEquals(1468006L, entry.size)
    }

    @Test
    fun opdsPreservesM4bFormatWhenServedAsAudioMp4() {
        val entry = OPDS.make(
            id0 = "urn:book:hongloumeng-m4b",
            title = "红楼梦",
            authors = listOf("曹雪芹"),
            links = listOf(
                mapOf(
                    "rel" to "http://opds-spec.org/acquisition/open-access",
                    "href" to "/audio/hongloumeng.m4b?token=1",
                    "type" to "audio/mp4",
                ),
            ),
            authorSep = "、",
        ) { href -> RemoteClient.resolve("https://books.example.com/opds", href) }

        assertNotNull(entry)
        assertEquals("M4B", entry!!.format)
    }

    @Test
    fun opdsPrefersEpubOverAudioWhenEntryOffersBoth() {
        val entry = OPDS.make(
            id0 = "urn:book:dual",
            title = "背影",
            authors = listOf("朱自清"),
            links = listOf(
                mapOf(
                    "rel" to "http://opds-spec.org/acquisition",
                    "href" to "/media/beiying.m4a",
                    "type" to "audio/mp4",
                ),
                mapOf(
                    "rel" to "http://opds-spec.org/acquisition",
                    "href" to "/books/beiying.epub",
                    "type" to "application/epub+zip",
                ),
            ),
            authorSep = "、",
        ) { href -> RemoteClient.resolve("https://books.example.com/opds", href) }

        assertNotNull(entry)
        assertEquals("EPUB", entry!!.format)
        assertEquals("https://books.example.com/books/beiying.epub", entry.book)
    }

    @Test
    fun opdsIgnoresUnsupportedAcquisitionMimeTypes() {
        val entry = OPDS.make(
            id0 = "urn:book:unknown",
            title = "未知格式",
            authors = emptyList(),
            links = listOf(
                mapOf(
                    "rel" to "http://opds-spec.org/acquisition",
                    "href" to "/files/archive.rar",
                    "type" to "application/vnd.rar",
                ),
            ),
            authorSep = "、",
        ) { href -> href }

        assertNull(entry)
    }

    @Test
    fun remoteClientNameKeepsAudioExtensionsAndDerivesFromMime() {
        val audioEntry = RemoteEntry(
            id = "1",
            title = "背影",
            book = "https://books.example.com/download/123",
            type = "audio/mp4",
            format = "M4A",
        )
        assertEquals(
            "背影.m4a",
            RemoteClient.name(audioEntry, "https://books.example.com/download/123", "audio/mp4", null),
        )
        assertEquals(
            "beiying-short.m4a",
            RemoteClient.name(audioEntry, "https://books.example.com/media/beiying-short.m4a?v=2", "audio/mp4", null),
        )
    }
}
