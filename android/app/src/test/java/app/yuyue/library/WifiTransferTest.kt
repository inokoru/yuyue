package app.yuyue.library

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket

class WifiTransferTest {
    private fun exchange(
        rawRequest: String,
        handler: (WifiTransfer.Request) -> Triple<Int, String, ByteArray> = { Triple(200, "text/plain", "ok".toByteArray()) },
    ): String {
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { server ->
            var serverThreadError: Throwable? = null
            val worker = Thread {
                try {
                    server.accept().use { accepted ->
                        WifiTransfer.serveWith(accepted, handler)
                    }
                } catch (t: Throwable) {
                    serverThreadError = t
                }
            }
            worker.start()

            val response = Socket(InetAddress.getLoopbackAddress(), server.localPort).use { client ->
                client.soTimeout = 5_000
                client.getOutputStream().apply {
                    write(rawRequest.toByteArray(Charsets.ISO_8859_1))
                    flush()
                }
                client.getInputStream().readBytes().toString(Charsets.UTF_8)
            }
            worker.join(5_000)
            assertNull("serveWith must isolate all exceptions on connection thread", serverThreadError)
            return response
        }
    }

    @Test
    fun parseQueryRejectsMalformedPercentEncodingAndDecodesValidPairs() {
        assertNull(WifiTransfer.parseQuery("name=%ZZ"))
        assertNull(WifiTransfer.parseQuery("name=book%"))
        assertNull(WifiTransfer.parseQuery("%ZZ=1"))
        assertEquals(mapOf("id" to "abc", "name" to "hello world"), WifiTransfer.parseQuery("id=abc&name=hello%20world"))
        assertEquals(emptyMap<String, String>(), WifiTransfer.parseQuery(""))
    }

    @Test
    fun serveWithReturns400OnMalformedQueryPercentEncodingWithoutCrashing() {
        val resp1 = exchange("POST /upload?name=%ZZ HTTP/1.1\r\nHost: localhost\r\n\r\n")
        assertTrue("Expected 400 Bad Request, got: $resp1", resp1.startsWith("HTTP/1.1 400 Bad Request\r\n"))

        val resp2 = exchange("POST /upload?name=trailing% HTTP/1.1\r\nHost: localhost\r\n\r\n")
        assertTrue("Expected 400 Bad Request, got: $resp2", resp2.startsWith("HTTP/1.1 400 Bad Request\r\n"))
    }

    @Test
    fun serveWithGuardsNegativeNonNumericAndOversizedContentLengthAndHandlerErrors() {
        val neg = exchange("POST /queue HTTP/1.1\r\nContent-Length: -5\r\n\r\n")
        assertTrue("Expected 400 for negative Content-Length, got: $neg", neg.startsWith("HTTP/1.1 400 Bad Request\r\n"))

        val badNum = exchange("POST /queue HTTP/1.1\r\nContent-Length: abc\r\n\r\n")
        assertTrue("Expected 400 for non-numeric Content-Length, got: $badNum", badNum.startsWith("HTTP/1.1 400 Bad Request\r\n"))

        val huge = exchange("POST /queue HTTP/1.1\r\nContent-Length: 99999999\r\n\r\n")
        assertTrue("Expected 413 for oversized Content-Length, got: $huge", huge.startsWith("HTTP/1.1 413 Payload Too Large\r\n"))

        val thrown = exchange("GET / HTTP/1.1\r\n\r\n") { throw IllegalStateException("boom") }
        assertTrue("Expected 400 when handler throws, got: $thrown", thrown.startsWith("HTTP/1.1 400 Bad Request\r\n"))
    }
}
