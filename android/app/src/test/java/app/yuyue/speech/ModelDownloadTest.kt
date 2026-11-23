package app.yuyue.speech

import com.sun.net.httpserver.HttpServer
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.net.InetSocketAddress
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger

class ModelDownloadTest {

    @Test
    fun candidateUrlsIncludeModelScopeAndGitHubWithoutDuplicates() {
        val urls = ModelDownload.candidateUrls("live-zipvoice-1.zip")
        assertEquals(2, urls.size)
        assertTrue(urls.any { it == "${ModelDownload.MODELSCOPE_BASE}live-zipvoice-1.zip" })
        assertTrue(urls.any { it == "${ModelDownload.GITHUB_RELEASE_BASE}live-zipvoice-1.zip" })

        val fontUrls = ModelDownload.candidateUrls(
            asset = "LXGWWenKaiLite-Regular.ttf",
            officialUrl = "https://github.com/lxgw/LxgwWenKai-Lite/releases/download/v1.530/LXGWWenKaiLite-Regular.ttf",
            mirrorPath = "fonts/LXGWWenKaiLite-Regular.ttf",
        )
        assertEquals(2, fontUrls.size)
        assertTrue(fontUrls.any { it == "${ModelDownload.MODELSCOPE_BASE}fonts/LXGWWenKaiLite-Regular.ttf" })
        assertTrue(fontUrls.any { it.contains("LxgwWenKai-Lite") })
    }

    @Test
    fun downloadWithResumeFallsBackToSecondSourceAndHonorsHttpRange206() = runBlocking {
        val payload = ByteArray(4096) { (it * 31 + 7).toByte() }
        val rangeRequests = AtomicInteger(0)
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        // First source always returns 503 to simulate an unreachable mirror/origin.
        server.createContext("/broken") { ex ->
            ex.sendResponseHeaders(503, -1)
            ex.close()
        }
        // Second source supports standard HTTP Range 206 resume.
        server.createContext("/good") { ex ->
            val range = ex.requestHeaders.getFirst("Range")
            if (range != null && range.startsWith("bytes=")) {
                val start = range.removePrefix("bytes=").substringBefore("-").toInt()
                if (start >= payload.size) {
                    ex.sendResponseHeaders(416, -1)
                    ex.close()
                    return@createContext
                }
                rangeRequests.incrementAndGet()
                val slice = payload.copyOfRange(start, payload.size)
                ex.responseHeaders.add("Content-Range", "bytes $start-${payload.size - 1}/${payload.size}")
                ex.sendResponseHeaders(206, slice.size.toLong())
                ex.responseBody.use { it.write(slice) }
            } else {
                ex.sendResponseHeaders(200, payload.size.toLong())
                ex.responseBody.use { it.write(payload) }
            }
        }
        server.start()
        val tempDir = Files.createTempDirectory("yuyue-dl-test").toFile()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val part = File(tempDir, ".sample.bin.part")
            // Seed the first 1024 bytes as if a previous attempt was interrupted.
            part.writeBytes(payload.copyOfRange(0, 1024))

            ModelDownload.downloadWithResume(
                urls = listOf("$base/broken", "$base/good"),
                partFile = part,
                expectedBytes = payload.size.toLong(),
                expectedBlake3 = null,
            )

            assertEquals(1, rangeRequests.get())
            assertArrayEquals(payload, part.readBytes())
        } finally {
            server.stop(0)
            tempDir.deleteRecursively()
        }
    }

    @Test
    fun downloadWithResumeRecoversFrom416OversizedPart() = runBlocking {
        val payload = ByteArray(2048) { (it and 0xFF).toByte() }
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/file") { ex ->
            val range = ex.requestHeaders.getFirst("Range")
            if (range != null) {
                ex.sendResponseHeaders(416, -1)
                ex.close()
            } else {
                ex.sendResponseHeaders(200, payload.size.toLong())
                ex.responseBody.use { it.write(payload) }
            }
        }
        server.start()
        val tempDir = Files.createTempDirectory("yuyue-dl-416").toFile()
        try {
            val base = "http://127.0.0.1:${server.address.port}"
            val part = File(tempDir, ".sample.bin.part")
            // Oversized stale part file triggers 416, then restarts cleanly from byte 0.
            part.writeBytes(ByteArray(3000) { 0x55 })

            ModelDownload.downloadWithResume(
                urls = listOf("$base/file"),
                partFile = part,
                expectedBytes = payload.size.toLong(),
                expectedBlake3 = null,
            )
            assertArrayEquals(payload, part.readBytes())
        } finally {
            server.stop(0)
            tempDir.deleteRecursively()
        }
    }
}
