package app.yuyue.sync

import com.sun.net.httpserver.HttpServer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetSocketAddress
import java.util.concurrent.CopyOnWriteArrayList

class WebDAVFolderTest {

    private data class RecordedRequest(
        val method: String,
        val path: String,
        val contentType: String?,
        val auth: String?,
    )

    private fun withServer(
        handler: (RecordedRequest) -> Int,
        block: (baseUrl: String, requests: List<RecordedRequest>) -> Unit,
    ) {
        val requests = CopyOnWriteArrayList<RecordedRequest>()
        val server = HttpServer.create(InetSocketAddress("127.0.0.1", 0), 0)
        server.createContext("/") { exchange ->
            val rec = RecordedRequest(
                method = exchange.requestMethod,
                path = exchange.requestURI.rawPath,
                contentType = exchange.requestHeaders.getFirst("Content-Type"),
                auth = exchange.requestHeaders.getFirst("Authorization"),
            )
            requests.add(rec)
            exchange.requestBody.use { it.readBytes() }
            val code = handler(rec)
            exchange.sendResponseHeaders(code, -1)
            exchange.close()
        }
        server.start()
        try {
            block("http://127.0.0.1:${server.address.port}/dav", requests)
        } finally {
            server.stop(0)
        }
    }

    @Test
    fun prepareCreatesNestedFoldersSegmentBySegment() {
        withServer({ 201 }) { base, requests ->
            val folder = WebDAVFolder(Sync.WebDAVAccount(base, "alice", "secret", "Books/语阅"))
            folder.prepare()
            assertEquals(listOf("MKCOL", "MKCOL"), requests.map { it.method })
            assertEquals(listOf("/dav/Books/", "/dav/Books/%E8%AF%AD%E9%98%85/"), requests.map { it.path })
            assertTrue(requests.all { it.auth?.startsWith("Basic ") == true })
        }
    }

    @Test
    fun prepareSkipsMkcolWhenFolderIsEmpty() {
        withServer({ 403 }) { base, requests ->
            val folder = WebDAVFolder(Sync.WebDAVAccount(base, "alice", "secret", ""))
            folder.prepare()
            assertTrue(requests.isEmpty())
        }
    }

    @Test
    fun prepareToleratesReadOnlyParentMountWhenFinalFolderSucceeds() {
        withServer({ req ->
            if (req.path == "/dav/mnt/") 403 else 201
        }) { base, requests ->
            val folder = WebDAVFolder(Sync.WebDAVAccount(base, "alice", "secret", "mnt/Yuyue"))
            folder.prepare()
            assertEquals(listOf("/dav/mnt/", "/dav/mnt/Yuyue/"), requests.map { it.path })
        }
    }

    @Test
    fun writeUsesAppropriateContentTypeForJsonAndProbeFiles() {
        withServer({ 201 }) { base, requests ->
            val folder = WebDAVFolder(Sync.WebDAVAccount(base, "alice", "secret", "Yuyue"))
            folder.write("dev.yuyue-sync.json", "{}".toByteArray())
            folder.write(".yuyue-check-1234", "ok".toByteArray())
            assertEquals(2, requests.size)
            assertTrue(requests[0].contentType!!.startsWith("application/json"))
            assertTrue(requests[1].contentType!!.startsWith("application/octet-stream"))
        }
    }
}
