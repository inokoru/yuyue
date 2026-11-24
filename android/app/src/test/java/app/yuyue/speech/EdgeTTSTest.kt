package app.yuyue.speech

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.runBlocking
import okhttp3.Request
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

class EdgeTTSTest {

    private class FakeWebSocket(private val onCancel: () -> Unit = {}) : WebSocket {
        val cancelled = AtomicBoolean(false)
        val closed = AtomicBoolean(false)
        val sentTexts = mutableListOf<String>()

        override fun request(): Request = Request.Builder().url("https://example.invalid").build()
        override fun queueSize(): Long = 0L
        override fun send(text: String): Boolean {
            sentTexts += text
            return true
        }
        override fun send(bytes: ByteString): Boolean = true
        override fun close(code: Int, reason: String?): Boolean {
            closed.set(true)
            return true
        }
        override fun cancel() {
            cancelled.set(true)
            onCancel()
        }
    }

    private fun audioFrame(payload: ByteArray): ByteString {
        val header = "X-RequestId:1234\r\nContent-Type:audio/mpeg\r\nPath:audio\r\n".toByteArray(Charsets.UTF_8)
        val out = ByteArrayOutputStream()
        out.write((header.size ushr 8) and 0xFF)
        out.write(header.size and 0xFF)
        out.write(header)
        out.write(payload)
        return out.toByteArray().toByteString()
    }

    @Test
    fun synthesizeReturnsAudioOnlyAfterTurnEnd() = runBlocking {
        val chunk1 = byteArrayOf(0xFF.toByte(), 0xFB.toByte(), 1, 2, 3)
        val chunk2 = byteArrayOf(4, 5, 6, 7)

        val result = EdgeTTS.synthesize("hello world", "zh-CN-XiaoxiaoNeural", "zh-CN") { _, listener ->
            val ws = FakeWebSocket()
            Thread {
                listener.onMessage(ws, audioFrame(chunk1))
                listener.onMessage(ws, audioFrame(chunk2))
                listener.onMessage(ws, "X-RequestId:1234\r\nPath:turn.end\r\n\r\n")
            }.start()
            ws
        }

        assertArrayEquals(chunk1 + chunk2, result)
    }

    @Test
    fun synthesizeRejectsPartialAudioWhenClosedBeforeTurnEnd() = runBlocking {
        val attempts = AtomicInteger(0)
        val partial = byteArrayOf(0xFF.toByte(), 0xFB.toByte(), 9, 8, 7)

        try {
            EdgeTTS.synthesize("partial close", "zh-CN-XiaoxiaoNeural", "zh-CN") { _, listener ->
                attempts.incrementAndGet()
                val ws = FakeWebSocket()
                Thread {
                    listener.onMessage(ws, audioFrame(partial))
                    // Server closes cleanly without sending Path:turn.end
                    listener.onClosing(ws, 1000, "premature")
                }.start()
                ws
            }
            fail("Expected synthesize to throw when closed before Path:turn.end")
        } catch (e: IllegalStateException) {
            assertTrue(e.message?.contains("turn.end") == true)
            assertEquals(2, attempts.get())
        }
    }

    @Test
    fun synthesizeRejectsPartialAudioWhenFailureOccursBeforeTurnEnd() = runBlocking {
        val partial = byteArrayOf(0xFF.toByte(), 0xFB.toByte(), 1, 2, 3)

        try {
            EdgeTTS.synthesize("partial failure", "zh-CN-XiaoxiaoNeural", "zh-CN") { _, listener ->
                val ws = FakeWebSocket()
                Thread {
                    listener.onMessage(ws, audioFrame(partial))
                    listener.onFailure(ws, IOException("connection reset"), null)
                }.start()
                ws
            }
            fail("Expected synthesize to throw on failure before Path:turn.end")
        } catch (e: IOException) {
            assertEquals("connection reset", e.message)
        }
    }

    @Test
    fun coroutineCancellationCancelsWebSocketWithoutRetry() = runBlocking {
        val attempts = AtomicInteger(0)
        val wsCreated = CompletableDeferred<FakeWebSocket>()
        val cancelObserved = CompletableDeferred<Unit>()

        val job = async {
            EdgeTTS.synthesize("cancel me", "zh-CN-XiaoxiaoNeural", "zh-CN") { _, _ ->
                attempts.incrementAndGet()
                FakeWebSocket(onCancel = { cancelObserved.complete(Unit) }).also {
                    wsCreated.complete(it)
                }
            }
        }

        val ws = wsCreated.await()
        job.cancel()
        cancelObserved.await()

        try {
            job.await()
            fail("Expected CancellationException")
        } catch (_: CancellationException) {
            // expected
        }
        assertTrue(ws.cancelled.get())
        assertEquals(1, attempts.get())
    }

    @Test
    fun timeoutCancelsWebSocketAndThrowsIllegalStateException() = runBlocking {
        val cancelledCount = AtomicInteger(0)
        val attempts = AtomicInteger(0)

        try {
            EdgeTTS.synthesize("slow chunk", "zh-CN-XiaoxiaoNeural", "zh-CN", timeoutMs = 80L) { _, _ ->
                attempts.incrementAndGet()
                FakeWebSocket(onCancel = { cancelledCount.incrementAndGet() })
            }
            fail("Expected timeout exception")
        } catch (e: IllegalStateException) {
            assertTrue(e.message?.contains("timed out") == true)
            assertEquals(2, attempts.get())
            assertEquals(2, cancelledCount.get())
        }
    }
}
