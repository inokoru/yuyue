package app.yuyue.feedback

import kotlinx.coroutines.runBlocking
import okhttp3.OkHttpClient
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.BufferedInputStream
import java.net.InetAddress
import java.net.ServerSocket
import java.util.Base64
import java.util.concurrent.TimeUnit

class FeedbackTest {

    @Test
    fun canSendAllowsTextOrImagesAndBlocksBlankOrSending() {
        assertFalse(Feedback.canSend("", 0, false))
        assertFalse(Feedback.canSend("   \n\t  ", 0, false))
        assertTrue(Feedback.canSend("", 1, false))
        assertTrue(Feedback.canSend("   ", 2, false))
        assertTrue(Feedback.canSend("书页打不开", 0, false))
        assertTrue(Feedback.canSend("附一张截图", 1, false))
        assertFalse(Feedback.canSend("正在发送中", 0, true))
        assertFalse(Feedback.canSend("", 1, true))
        assertFalse(Feedback.canSend("文本与图", 2, true))
    }

    @Test
    fun buildPayloadJsonWithInfoPopulatesFieldsClipsMessageAndCapsImages() {
        val longMessage = "  " + "😀".repeat(4005) + "  "
        val images = (1..6).map { i -> byteArrayOf(0xFF.toByte(), 0xD8.toByte(), i.toByte(), 0xD9.toByte()) }

        val json = Feedback.buildPayloadJson(
            message = longMessage,
            withInfo = true,
            appVersion = "0.1.3",
            appBuild = "5",
            deviceModel = "Google Pixel 9",
            osVersion = "Android 15",
            locale = "zh-CN",
            images = images,
        )

        @Suppress("UNCHECKED_CAST")
        val obj = parseJson(json) as Map<String, Any?>
        val message = obj["message"] as String
        assertEquals(Feedback.TEXT_MAX, message.codePointCount(0, message.length))
        assertEquals("😀".repeat(Feedback.TEXT_MAX), message)
        assertEquals("0.1.3", obj["appVersion"])
        assertEquals("5", obj["appBuild"])
        assertEquals("Google Pixel 9", obj["deviceModel"])
        assertEquals("Android 15", obj["osVersion"])
        assertEquals("zh-CN", obj["locale"])

        @Suppress("UNCHECKED_CAST")
        val encodedImages = obj["images"] as List<Map<String, Any?>>
        assertEquals(Feedback.FILES_MAX, encodedImages.size)
        encodedImages.forEachIndexed { index, item ->
            assertEquals("image/jpeg", item["mediaType"])
            val decoded = Base64.getDecoder().decode(item["data"] as String)
            assertArrayEquals(images[index], decoded)
        }
    }

    @Test
    fun buildPayloadJsonWithoutInfoEmptiesDeviceFieldsAndEscapesText() {
        val sampleImage = byteArrayOf(1, 2, 3, 4, 5)
        val rawMessage = "  \"引号\" \\ 反斜杠\n第二行\t制表  "

        val json = Feedback.buildPayloadJson(
            message = rawMessage,
            withInfo = false,
            appVersion = "0.1.3",
            appBuild = "5",
            deviceModel = "Xiaomi 15",
            osVersion = "Android 15",
            locale = "en-US",
            images = listOf(sampleImage),
        )

        @Suppress("UNCHECKED_CAST")
        val obj = parseJson(json) as Map<String, Any?>
        assertEquals("\"引号\" \\ 反斜杠\n第二行\t制表", obj["message"])
        assertEquals("", obj["appVersion"])
        assertEquals("", obj["appBuild"])
        assertEquals("", obj["deviceModel"])
        assertEquals("", obj["osVersion"])
        assertEquals("", obj["locale"])

        @Suppress("UNCHECKED_CAST")
        val encodedImages = obj["images"] as List<Map<String, Any?>>
        assertEquals(1, encodedImages.size)
        assertEquals("image/jpeg", encodedImages[0]["mediaType"])
        assertArrayEquals(sampleImage, Base64.getDecoder().decode(encodedImages[0]["data"] as String))
    }

    @Test
    fun postPayloadSendsHeadersAndHandlesSuccessAndJsonErrorReason() = runBlocking {
        val client = OkHttpClient.Builder()
            .readTimeout(5, TimeUnit.SECONDS)
            .writeTimeout(5, TimeUnit.SECONDS)
            .callTimeout(5, TimeUnit.SECONDS)
            .build()
        val payload = Feedback.buildPayloadJson(
            message = "测试反馈",
            withInfo = true,
            appVersion = "0.1.3",
            appBuild = "5",
            deviceModel = "Pixel",
            osVersion = "Android 15",
            locale = "zh-CN",
            images = emptyList(),
        )

        // 200 OK path
        val capturedOk = withLocalHttpServer(
            statusLine = "HTTP/1.1 200 OK",
            responseBody = """{"ok":true}""",
        ) { url ->
            Feedback.postPayload(
                payloadJson = payload,
                client = client,
                endpoint = url,
                appKey = "test-app-key-123",
            )
        }
        assertEquals("POST /v1/ingest/feedback HTTP/1.1", capturedOk.requestLine)
        assertEquals("Bearer test-app-key-123", capturedOk.headers["authorization"])
        assertNotNull(capturedOk.headers["content-type"])
        assertTrue(
            "Expected application/json content-type, got: ${capturedOk.headers["content-type"]}",
            capturedOk.headers["content-type"]!!.lowercase().startsWith("application/json"),
        )
        assertEquals(payload, capturedOk.body)

        // 400 JSON {"reason": "..."} error propagation
        var capturedError: Feedback.Problem? = null
        val capturedBad = withLocalHttpServer(
            statusLine = "HTTP/1.1 400 Bad Request",
            responseBody = """{"error":"bad_request","reason":"图片格式不支持"}""",
        ) { url ->
            try {
                Feedback.postPayload(
                    payloadJson = payload,
                    client = client,
                    endpoint = url,
                    appKey = "test-app-key-456",
                )
                fail("Expected Feedback.Problem on HTTP 400")
            } catch (e: Feedback.Problem) {
                capturedError = e
            }
        }
        assertEquals("Bearer test-app-key-456", capturedBad.headers["authorization"])
        assertEquals("图片格式不支持", capturedError?.message)
    }

    private data class CapturedRequest(
        val requestLine: String,
        val headers: Map<String, String>,
        val body: String,
    )

    private suspend fun withLocalHttpServer(
        statusLine: String,
        responseBody: String,
        block: suspend (String) -> Unit,
    ): CapturedRequest {
        ServerSocket(0, 1, InetAddress.getLoopbackAddress()).use { server ->
            var captured: CapturedRequest? = null
            var serverError: Throwable? = null
            val worker = Thread {
                try {
                    server.accept().use { socket ->
                        socket.soTimeout = 5_000
                        val input = BufferedInputStream(socket.getInputStream())
                        val headerBytes = readUntilHeaderEnd(input)
                        val headerText = String(headerBytes, Charsets.ISO_8859_1)
                        val lines = headerText.split("\r\n").filter { it.isNotEmpty() }
                        val requestLine = lines.firstOrNull().orEmpty()
                        val headers = buildMap {
                            for (line in lines.drop(1)) {
                                val idx = line.indexOf(':')
                                if (idx > 0) {
                                    put(line.substring(0, idx).trim().lowercase(), line.substring(idx + 1).trim())
                                }
                            }
                        }
                        val contentLength = headers["content-length"]?.toIntOrNull() ?: 0
                        val bodyBytes = ByteArray(contentLength)
                        var offset = 0
                        while (offset < contentLength) {
                            val read = input.read(bodyBytes, offset, contentLength - offset)
                            if (read < 0) break
                            offset += read
                        }
                        captured = CapturedRequest(requestLine, headers, String(bodyBytes, 0, offset, Charsets.UTF_8))

                        val respBytes = responseBody.toByteArray(Charsets.UTF_8)
                        val response = buildString {
                            append(statusLine).append("\r\n")
                            append("Content-Type: application/json; charset=utf-8\r\n")
                            append("Content-Length: ").append(respBytes.size).append("\r\n")
                            append("Connection: close\r\n\r\n")
                        }
                        socket.getOutputStream().apply {
                            write(response.toByteArray(Charsets.ISO_8859_1))
                            write(respBytes)
                            flush()
                        }
                    }
                } catch (t: Throwable) {
                    serverError = t
                }
            }
            worker.start()

            block("http://127.0.0.1:${server.localPort}/v1/ingest/feedback")
            worker.join(5_000)
            assertNull("Local test server failed: $serverError", serverError)
            return checkNotNull(captured) { "Server did not receive a request" }
        }
    }

    private fun readUntilHeaderEnd(input: BufferedInputStream): ByteArray {
        val out = java.io.ByteArrayOutputStream()
        var matched = 0
        while (true) {
            val b = input.read()
            if (b < 0) break
            out.write(b)
            matched = when {
                (matched == 0 || matched == 2) && b == '\r'.code -> matched + 1
                (matched == 1 || matched == 3) && b == '\n'.code -> matched + 1
                b == '\r'.code -> 1
                else -> 0
            }
            if (matched == 4) break
        }
        return out.toByteArray()
    }

    // Pure-JVM JSON parser for test assertions (avoids Android stubbed org.json).
    private fun parseJson(src: String): Any? = JsonParser(src).parseRoot()

    private class JsonParser(private val src: String) {
        private var i = 0

        fun parseRoot(): Any? {
            val result = parseValue()
            skipWs()
            assertEquals(src.length, i)
            return result
        }

        private fun skipWs() {
            while (i < src.length && src[i] in charArrayOf(' ', '\t', '\r', '\n')) i++
        }

        private fun parseString(): String {
            assertEquals('"', src[i++])
            val sb = StringBuilder()
            while (i < src.length) {
                val c = src[i++]
                when (c) {
                    '"' -> return sb.toString()
                    '\\' -> when (val e = src[i++]) {
                        '"', '\\', '/' -> sb.append(e)
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000C')
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'u' -> {
                            sb.append(src.substring(i, i + 4).toInt(16).toChar())
                            i += 4
                        }
                        else -> fail("Invalid JSON escape: \\$e")
                    }
                    else -> sb.append(c)
                }
            }
            fail("Unterminated JSON string")
            return ""
        }

        private fun parseObject(): Map<String, Any?> {
            assertEquals('{', src[i++])
            val map = linkedMapOf<String, Any?>()
            skipWs()
            if (src[i] == '}') {
                i++
                return map
            }
            while (true) {
                skipWs()
                val k = parseString()
                skipWs()
                assertEquals(':', src[i++])
                map[k] = parseValue()
                skipWs()
                when (src[i++]) {
                    ',' -> continue
                    '}' -> return map
                    else -> fail("Expected ',' or '}' in JSON object at $i")
                }
            }
        }

        private fun parseArray(): List<Any?> {
            assertEquals('[', src[i++])
            val list = mutableListOf<Any?>()
            skipWs()
            if (src[i] == ']') {
                i++
                return list
            }
            while (true) {
                list += parseValue()
                skipWs()
                when (src[i++]) {
                    ',' -> continue
                    ']' -> return list
                    else -> fail("Expected ',' or ']' in JSON array at $i")
                }
            }
        }

        private fun parseValue(): Any? {
            skipWs()
            return when (src[i]) {
                '{' -> parseObject()
                '[' -> parseArray()
                '"' -> parseString()
                't' -> {
                    assertTrue(src.startsWith("true", i))
                    i += 4
                    true
                }
                'f' -> {
                    assertTrue(src.startsWith("false", i))
                    i += 5
                    false
                }
                'n' -> {
                    assertTrue(src.startsWith("null", i))
                    i += 4
                    null
                }
                else -> fail("Unexpected JSON token at $i: ${src.substring(i)}")
            }
        }
    }
}
