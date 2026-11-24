package app.yuyue.speech

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.io.ByteArrayOutputStream
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.floor

/**
 * Microsoft Edge Read Aloud WebSocket TTS client (keyless, v2 `Sec-MS-GEC` token).
 *
 * Matches `rany2/edge-tts` v7.3.x:
 * - Handshake carries `TrustedClientToken`, `ConnectionId`, `Sec-MS-GEC` (`<16-hex CS64>-<64-hex SHA256>`),
 *   `Sec-MS-GEC-Version`, Edge `User-Agent`, `Origin`, and random `muid` cookie.
 * - On HTTP 403 handshake failure with a `Date` response header, adjusts clock skew and retries once.
 * - Streams 24 kHz / 48 kbps mono MP3 chunks until `Path:turn.end`.
 */
object EdgeTTS {
    const val TRUSTED_CLIENT_TOKEN = "6A5AA1D4EAFF4E9FB37E23D68491D6F4"
    const val CHROMIUM_FULL_VERSION = "143.0.3650.75"
    const val CHROMIUM_MAJOR_VERSION = "143"
    const val SEC_MS_GEC_VERSION = "2-$CHROMIUM_FULL_VERSION"
    const val WSS_BASE_URL = "wss://speech.platform.bing.com/consumer/speech/synthesize/readaloud/edge/v1"
    const val ORIGIN = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold"
    const val USER_AGENT =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/$CHROMIUM_MAJOR_VERSION.0.0.0 Safari/537.36 Edg/$CHROMIUM_MAJOR_VERSION.0.0.0"

    private const val WIN_EPOCH = 11_644_473_600L
    private const val TICKS_PER_SECOND = 10_000_000L

    @Volatile
    private var clockSkewSeconds: Double = 0.0

    private val client: OkHttpClient by lazy {
        OkHttpClient.Builder()
            .connectTimeout(15, TimeUnit.SECONDS)
            .readTimeout(25, TimeUnit.SECONDS)
            .writeTimeout(15, TimeUnit.SECONDS)
            .build()
    }

    class EdgeHttpException(val code: Int, val serverDateHeader: String?, message: String) : Exception(message)

    fun synthesize(text: String, voiceID: String, language: String): ByteArray {
        val cleaned = sanitizeText(text)
        if (cleaned.isEmpty()) throw IllegalStateException("Empty text")

        val fullVoice = formatVoiceName(voiceID)
        val xmlLang = resolvedXMLLang(voiceID, language)
        val chunks = splitChunks(cleaned, maxUtf8Bytes = 2800)

        val combined = ByteArrayOutputStream()
        for (chunk in chunks) {
            val mp3 = synthesizeChunkWithRetry(chunk, fullVoice, xmlLang)
            combined.write(mp3)
        }
        val out = combined.toByteArray()
        if (out.isEmpty()) throw IllegalStateException("Empty audio from Edge TTS")
        return out
    }

    private fun synthesizeChunkWithRetry(text: String, fullVoice: String, xmlLang: String): ByteArray {
        var lastError: Throwable? = null
        for (attempt in 0 until 2) {
            try {
                return synthesizeSingleChunk(text, fullVoice, xmlLang)
            } catch (e: EdgeHttpException) {
                lastError = e
                if (e.code == 403 && !e.serverDateHeader.isNullOrBlank() && attempt == 0) {
                    parseHttpDate(e.serverDateHeader)?.let { adjustClockSkew(it) }
                    continue
                }
                if (attempt == 0) {
                    Thread.sleep(250)
                    continue
                }
            } catch (e: Throwable) {
                lastError = e
                if (attempt == 0) {
                    Thread.sleep(250)
                    continue
                }
            }
        }
        throw lastError ?: IllegalStateException("Edge TTS synthesis failed")
    }

    private fun synthesizeSingleChunk(text: String, fullVoice: String, xmlLang: String): ByteArray {
        val connId = randomHex32()
        val gec = generateSecMsGecV2()
        val url =
            "$WSS_BASE_URL?TrustedClientToken=$TRUSTED_CLIENT_TOKEN" +
                "&ConnectionId=$connId&Sec-MS-GEC=$gec&Sec-MS-GEC-Version=$SEC_MS_GEC_VERSION"

        val request = Request.Builder()
            .url(url)
            .header("Pragma", "no-cache")
            .header("Cache-Control", "no-cache")
            .header("Origin", ORIGIN)
            .header("User-Agent", USER_AGENT)
            .header("Accept-Language", "en-US,en;q=0.9")
            .header("Cookie", "muid=${randomHex32().uppercase(Locale.US)};")
            .build()

        val audio = ByteArrayOutputStream()
        val failure = AtomicReference<Throwable?>(null)
        val latch = CountDownLatch(1)

        val timestamp = jsDateString()
        val configPayload =
            "X-Timestamp:$timestamp\r\n" +
                "Content-Type:application/json; charset=utf-8\r\n" +
                "Path:speech.config\r\n\r\n" +
                """{"context":{"synthesis":{"audio":{"metadataoptions":{"sentenceBoundaryEnabled":"false","wordBoundaryEnabled":"false"},"outputFormat":"audio-24khz-48kbitrate-mono-mp3"}}}}""" +
                "\r\n"

        val escaped = escapeXml(text)
        val ssml =
            "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='$xmlLang'>" +
                "<voice name='$fullVoice'>" +
                "<prosody pitch='+0Hz' rate='+0%' volume='+0%'>$escaped</prosody>" +
                "</voice></speak>"
        val ssmlPayload =
            "X-RequestId:${randomHex32()}\r\n" +
                "Content-Type:application/ssml+xml\r\n" +
                "X-Timestamp:${timestamp}Z\r\n" +
                "Path:ssml\r\n\r\n" +
                ssml

        val ws = client.newWebSocket(
            request,
            object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    webSocket.send(configPayload)
                    webSocket.send(ssmlPayload)
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    if (text.contains("Path:turn.end")) {
                        webSocket.close(1000, "done")
                        latch.countDown()
                    }
                }

                override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                    val raw = bytes.toByteArray()
                    if (raw.size < 2) return
                    val headerLen = ((raw[0].toInt() and 0xFF) shl 8) or (raw[1].toInt() and 0xFF)
                    val bodyStart = 2 + headerLen
                    if (bodyStart > raw.size) return
                    val header = String(raw, 2, headerLen, Charsets.UTF_8)
                    if (header.contains("Path:audio") && bodyStart < raw.size) {
                        synchronized(audio) {
                            audio.write(raw, bodyStart, raw.size - bodyStart)
                        }
                    }
                }

                override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                    webSocket.close(1000, null)
                    latch.countDown()
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    latch.countDown()
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (response != null && response.code != 101) {
                        failure.compareAndSet(
                            null,
                            EdgeHttpException(response.code, response.header("Date"), "HTTP ${response.code}"),
                        )
                    } else {
                        failure.compareAndSet(null, t)
                    }
                    latch.countDown()
                }
            },
        )

        val completed = latch.await(25, TimeUnit.SECONDS)
        if (!completed) {
            ws.cancel()
            throw IllegalStateException("Edge TTS timed out")
        }
        val bytes = synchronized(audio) { audio.toByteArray() }
        if (bytes.isNotEmpty()) return bytes
        failure.get()?.let { throw it }
        throw IllegalStateException("No audio returned from Edge TTS")
    }

    // MARK: - Formatting & Sanitization

    fun formatVoiceName(voiceID: String): String {
        val trimmed = voiceID.trim()
        if (trimmed.startsWith("Microsoft Server Speech Text to Speech Voice (")) return trimmed
        val parts = trimmed.split("-")
        if (parts.size < 3) return trimmed
        val name = parts.last()
        val locale = parts.dropLast(1).joinToString("-")
        return "Microsoft Server Speech Text to Speech Voice ($locale, $name)"
    }

    private fun resolvedXMLLang(voiceID: String, fallback: String): String {
        val parts = voiceID.split("-")
        if (parts.size >= 2 && parts[0].length >= 2 && parts[1].length >= 2) {
            return "${parts[0]}-${parts[1]}"
        }
        return fallback.ifEmpty { "zh-CN" }
    }

    fun sanitizeText(text: String): String {
        val sb = StringBuilder(text.length)
        for (ch in text) {
            val code = ch.code
            if ((code in 0..8) || code == 11 || code == 12 || (code in 14..31)) {
                sb.append(' ')
            } else {
                sb.append(ch)
            }
        }
        return sb.toString().trim()
    }

    fun escapeXml(text: String): String =
        text.replace("&", "&amp;")
            .replace("<", "&lt;")
            .replace(">", "&gt;")
            .replace("\"", "&quot;")
            .replace("'", "&apos;")

    private fun splitChunks(text: String, maxUtf8Bytes: Int): List<String> {
        if (text.toByteArray(Charsets.UTF_8).size <= maxUtf8Bytes) return listOf(text)
        val breaks = setOf('。', '！', '？', '；', '，', '.', '!', '?', ';', ',', '\n', ' ')
        val out = mutableListOf<String>()
        val cur = StringBuilder()
        var curBytes = 0
        for (ch in text) {
            val chBytes = ch.toString().toByteArray(Charsets.UTF_8).size
            if (curBytes + chBytes > maxUtf8Bytes && cur.isNotEmpty()) {
                val t = cur.toString().trim()
                if (t.isNotEmpty()) out.add(t)
                cur.clear()
                curBytes = 0
            }
            cur.append(ch)
            curBytes += chBytes
            if (curBytes >= maxUtf8Bytes * 2 / 3 && ch in breaks) {
                val t = cur.toString().trim()
                if (t.isNotEmpty()) out.add(t)
                cur.clear()
                curBytes = 0
            }
        }
        val rem = cur.toString().trim()
        if (rem.isNotEmpty()) out.add(rem)
        return out.ifEmpty { listOf(text) }
    }

    private fun randomHex32(): String = UUID.randomUUID().toString().replace("-", "").lowercase(Locale.US)

    private fun jsDateString(): String {
        val fmt = SimpleDateFormat("EEE MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'", Locale.US)
        fmt.timeZone = TimeZone.getTimeZone("UTC")
        return fmt.format(Date())
    }

    private fun parseHttpDate(raw: String): Date? {
        val patterns = listOf(
            "EEE, dd MMM yyyy HH:mm:ss zzz",
            "EEEE, dd-MMM-yy HH:mm:ss zzz",
            "EEE MMM d HH:mm:ss yyyy",
        )
        for (pattern in patterns) {
            val fmt = SimpleDateFormat(pattern, Locale.US)
            fmt.timeZone = TimeZone.getTimeZone("UTC")
            runCatching { fmt.parse(raw) }.getOrNull()?.let { return it }
        }
        return null
    }

    @Synchronized
    private fun adjustClockSkew(serverDate: Date) {
        val clientSeconds = System.currentTimeMillis() / 1000.0 + clockSkewSeconds
        clockSkewSeconds += serverDate.time / 1000.0 - clientSeconds
    }

    // MARK: - Sec-MS-GEC v2 Token (CS64 + Salted SHA-256)

    /** Generates a v2 `Sec-MS-GEC` token (`"<16-HEX>-<64-HEX>"`), matching `rany2/edge-tts` `drm.py`. */
    fun generateSecMsGecV2(unixTimestamp: Double? = null): String {
        val ts = unixTimestamp ?: (System.currentTimeMillis() / 1000.0 + clockSkewSeconds)
        val secs = floor(ts).toLong() + WIN_EPOCH
        val rounded = secs - ((secs % 300L + 300L) % 300L)
        val ticks = rounded * TICKS_PER_SECOND
        val payload = "$ticks$TRUSTED_CLIENT_TOKEN".toByteArray(Charsets.US_ASCII)
        val digest = sha256Digest(payload)
        val (c0, c1) = cs64Digest(payload, digest)
        val cs64Bytes = ByteArray(8)
        writeLE32(c0, cs64Bytes, 0)
        writeLE32(c1, cs64Bytes, 4)
        return "${hexUpper(cs64Bytes)}-${hexUpper(digest)}"
    }

    private val SHA256_INITIAL = intArrayOf(
        0x6A09E667, 0xBB67AE85.toInt(), 0x3C6EF372, 0xA54FF53A.toInt(),
        0x510E527F, 0x9B05688C.toInt(), 0x1F83D9AB, 0x5BE0CD19,
    )
    private val SHA256_STATE = intArrayOf(
        0xFD9FBEA2.toInt(), 0x91243CE7.toInt(), 0xBE499094.toInt(), 0xCD2E1E0A.toInt(),
        0x6C006F49, 0x04BD35AB, 0xFEB0A7E4.toInt(), 0x03EFFBD2,
    )
    private val SHA256_SCHEDULE = intArrayOf(
        0xEB24BF9E.toInt(), 0x1558691F, 0xA0C09EE2.toInt(), 0x0CFA32BA,
        0xA622EE8C.toInt(), 0xD1DD9491.toInt(), 0xC0DD25A0.toInt(), 0xF9E70AB6.toInt(),
        0xF0C31573.toInt(), 0x7058CE4E, 0x7188E606, 0x00E5AB33,
        0xD258B060.toInt(), 0x72766564,
    )
    private val SHA256_ROUND_CONSTANTS = intArrayOf(
        0x428A2F98, 0x71374491, 0xB5C0FBCF.toInt(), 0xE9B5DBA5.toInt(),
        0x3956C25B, 0x59F111F1, 0x923F82A4.toInt(), 0xAB1C5ED5.toInt(),
        0xD807AA98.toInt(), 0x12835B01, 0x243185BE, 0x550C7DC3,
        0x72BE5D74, 0x80DEB1FE.toInt(), 0x9BDC06A7.toInt(), 0xC19BF174.toInt(),
        0xE49B69C1.toInt(), 0xEFBE4786.toInt(), 0x0FC19DC6, 0x240CA1CC,
        0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152.toInt(), 0xA831C66D.toInt(), 0xB00327C8.toInt(), 0xBF597FC7.toInt(),
        0xC6E00BF3.toInt(), 0xD5A79147.toInt(), 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13,
        0x650A7354, 0x766A0ABB, 0x81C2C92E.toInt(), 0x92722C85.toInt(),
        0xA2BFE8A1.toInt(), 0xA81A664B.toInt(), 0xC24B8B70.toInt(), 0xC76C51A3.toInt(),
        0xD192E819.toInt(), 0xD6990624.toInt(), 0xF40E3585.toInt(), 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5,
        0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814.toInt(), 0x8CC70208.toInt(),
        0x90BEFFFA.toInt(), 0xA4506CEB.toInt(), 0xBEF9A3F7.toInt(), 0xC67178F2.toInt(),
    )
    private const val SHA256_ADJUSTMENT = 0xC9DC06A7.toInt()
    private const val SHA256_SIGMA0_ADJUSTMENT = 0x059C0B80
    private const val SHA256_SIGMA1_ADJUSTMENT = 0x000B92C0

    private val CS64_COEFFS = intArrayOf(
        0xE82E91B1.toInt(), 0x44DE24C6, 0x3BEB65BC, 0xC89C5933.toInt(),
        0x8D1EC211.toInt(), 0x314EF5D5, 0xE92491B1.toInt(), 0xF0A5B9D5.toInt(),
        0x06064CF3, 0xDA79FA60.toInt(), 0xBED20560.toInt(), 0x89F9C9F8.toInt(),
        0x35FA38C6, 0xCB49E3C6.toInt(),
    )
    private val CS64_OFFSETS = intArrayOf(
        0xB32700D0.toInt(), 0x88DFF51D.toInt(), 0x29514038, 0xB328CBDF.toInt(),
        0xD405B4D5.toInt(), 0x26E55F3C, 0x607801C6, 0x82B2C1FB.toInt(),
        0xFA51A3DA.toInt(), 0x207CC464, 0x3C3C74F3, 0xD2D14B61.toInt(),
        0xBB04A2C1.toInt(), 0xC5626167.toInt(),
    )
    private const val CS64_FACTOR = 0x82893933.toInt()
    private const val CS64_ADJUSTMENT0 = 0xF2F90B1A.toInt()
    private const val CS64_ADJUSTMENT1 = 0x0B1A0000

    private fun rol16(v: Int): Int = (v shl 16) or (v ushr 16)
    private fun rotr32(v: Int, n: Int): Int = (v ushr n) or (v shl (32 - n))
    private fun sigma0(v: Int): Int = rotr32(v, 7) xor rotr32(v, 18) xor (v ushr 3)
    private fun sigma1(v: Int): Int = rotr32(v, 17) xor rotr32(v, 19) xor (v ushr 10)

    private fun sha256Rounds(state: IntArray, words: IntArray, firstRound: Int = 0): IntArray {
        var a = state[0]
        var b = state[1]
        var c = state[2]
        var d = state[3]
        var e = state[4]
        var f = state[5]
        var g = state[6]
        var h = state[7]
        for (i in firstRound until 64) {
            val k = if (firstRound == 14 && i == 14) SHA256_ADJUSTMENT else SHA256_ROUND_CONSTANTS[i]
            val s1 = rotr32(e, 6) xor rotr32(e, 11) xor rotr32(e, 25)
            val ch = g xor (e and (f xor g))
            val t1 = h + s1 + ch + k + words[i]
            val s0 = rotr32(a, 2) xor rotr32(a, 13) xor rotr32(a, 22)
            val maj = (a and b) or (c and (a or b))
            val t2 = s0 + maj
            h = g; g = f; f = e; e = d + t1
            d = c; c = b; b = a; a = t1 + t2
        }
        return intArrayOf(a, b, c, d, e, f, g, h)
    }

    private fun expandSchedule(words: IntArray, from: Int = 16) {
        for (i in from until 64) {
            words[i] = sigma1(words[i - 2]) + words[i - 7] + sigma0(words[i - 15]) + words[i - 16]
        }
    }

    private fun sha256Init(data: ByteArray): IntArray {
        val words = IntArray(64)
        words[14] = ((data[0].toInt() and 0xFF) shl 16) or
            ((data[1].toInt() and 0xFF) shl 8) or
            (data[2].toInt() and 0xFF)
        words[15] = ((data[3].toInt() and 0xFF) shl 24) or
            ((data[4].toInt() and 0xFF) shl 16) or
            ((data[5].toInt() and 0xFF) shl 8) or
            (data[6].toInt() and 0xFF)
        for (i in 16 until 30) {
            var value = SHA256_SCHEDULE[i - 16]
            if (i - 2 >= 14) {
                val s1 = sigma1(words[i - 2])
                value += if (i == 16) (s1 xor SHA256_SIGMA1_ADJUSTMENT) else s1
            }
            if (i - 7 >= 14) {
                value += words[i - 7]
            }
            if (i - 15 >= 14) {
                val s0 = sigma0(words[i - 15])
                value += if (i == 29) (s0 xor SHA256_SIGMA0_ADJUSTMENT) else s0
            }
            words[i] = value
        }
        words[30] = sigma1(words[28]) + words[23] + sigma0(words[15]) + words[14] + SHA256_ADJUSTMENT - SHA256_ROUND_CONSTANTS[14]
        expandSchedule(words, 31)
        val working = sha256Rounds(SHA256_STATE, words, firstRound = 14)
        return IntArray(8) { SHA256_INITIAL[it] + working[it] }
    }

    private fun sha256Digest(data: ByteArray): ByteArray {
        require(data.size >= 7)
        val totalLen = 57 + data.size
        val padZeros = ((55 - totalLen) % 64 + 64) % 64
        val tailLen = data.size - 7
        val padded = ByteArray(tailLen + 1 + padZeros + 8)
        System.arraycopy(data, 7, padded, 0, tailLen)
        padded[tailLen] = 0x80.toByte()
        val bitLen = totalLen.toLong() * 8L
        val lenPos = padded.size - 8
        for (i in 0 until 8) {
            padded[lenPos + i] = ((bitLen ushr (56 - i * 8)) and 0xFFL).toByte()
        }

        val state = sha256Init(data)
        var offset = 0
        while (offset < padded.size) {
            val words = IntArray(64)
            for (j in 0 until 16) {
                val p = offset + j * 4
                words[j] = ((padded[p].toInt() and 0xFF) shl 24) or
                    ((padded[p + 1].toInt() and 0xFF) shl 16) or
                    ((padded[p + 2].toInt() and 0xFF) shl 8) or
                    (padded[p + 3].toInt() and 0xFF)
            }
            expandSchedule(words, 16)
            val working = sha256Rounds(state, words)
            for (j in 0 until 8) state[j] += working[j]
            offset += 64
        }

        val out = ByteArray(32)
        for (j in 0 until 8) {
            val v = state[j]
            out[j * 4] = (v ushr 24).toByte()
            out[j * 4 + 1] = (v ushr 16).toByte()
            out[j * 4 + 2] = (v ushr 8).toByte()
            out[j * 4 + 3] = v.toByte()
        }
        return out
    }

    private fun wordsLE4Even(data: ByteArray): IntArray {
        if (data.isEmpty()) return IntArray(0)
        val paddedLen = (data.size + 3) and 3.inv()
        var wordCount = paddedLen / 4
        if (wordCount % 2 != 0) wordCount += 1
        val words = IntArray(wordCount)
        var i = 0
        var wIdx = 0
        while (i < data.size) {
            val b0 = data[i].toInt() and 0xFF
            val b1 = if (i + 1 < data.size) data[i + 1].toInt() and 0xFF else 0
            val b2 = if (i + 2 < data.size) data[i + 2].toInt() and 0xFF else 0
            val b3 = if (i + 3 < data.size) data[i + 3].toInt() and 0xFF else 0
            words[wIdx++] = b0 or (b1 shl 8) or (b2 shl 16) or (b3 shl 24)
            i += 4
        }
        return words
    }

    private fun cs64Digest(data: ByteArray, digest: ByteArray): Pair<Int, Int> {
        val keyA = ((digest[0].toInt() and 0xFF) or
            ((digest[1].toInt() and 0xFF) shl 8) or
            ((digest[2].toInt() and 0xFF) shl 16) or
            ((digest[3].toInt() and 0xFF) shl 24)) or 1
        val keyB = ((digest[4].toInt() and 0xFF) or
            ((digest[5].toInt() and 0xFF) shl 8) or
            ((digest[6].toInt() and 0xFF) shl 16) or
            ((digest[7].toInt() and 0xFF) shl 24)) or 1
        val factor0 = keyA * CS64_FACTOR
        val factor1 = keyB * CS64_FACTOR

        val prefixed = ByteArray(data.size + 1)
        System.arraycopy(data, 0, prefixed, 1, data.size)
        val dataWords = wordsLE4Even(prefixed)

        val total = CS64_COEFFS.size + dataWords.size
        val terms = IntArray(total)
        val offsets = IntArray(total)

        for (i in CS64_COEFFS.indices) {
            terms[i] = CS64_COEFFS[i] * if (i % 2 == 0) factor0 else factor1
            offsets[i] = CS64_OFFSETS[i]
        }
        for (i in dataWords.indices) {
            val word = dataWords[i]
            var term = word * if (i % 2 == 0) keyA else keyB
            var offset = rol16(word) * if (i % 2 == 0) 0xEF0569FB.toInt() else 0xC31713DB.toInt()
            if (i == 0) {
                term += CS64_ADJUSTMENT0 * factor0
                offset += CS64_ADJUSTMENT1
            }
            terms[CS64_COEFFS.size + i] = term
            offsets[CS64_COEFFS.size + i] = offset
        }

        var swapAcc = 0
        var swapLast = 0
        var state = 0
        var acc = 0
        var i = 0
        while (i < total) {
            val x = terms[i]
            val y = terms[i + 1]

            var t = rol16(offsets[i] + x)
            var q = t * 0x76F50F34
            swapAcc += rol16(q) * 0xC3EFEA97.toInt() + q

            t = rol16(offsets[i + 1] + y)
            q = t * 0x1881CE3C
            swapLast = rol16(q) * 0x35BD1EC9 + q
            swapAcc += swapLast

            state = state * keyA + x
            state = rol16(state) * 0xCF98B111.toInt()
            state = rol16(state) * 0x87085B9F.toInt()
            state = rol16(state) * 0x12CEB96D
            state = rol16(state) * 0x257E1D83
            acc += state

            t = state * keyB + y
            t = rol16(t) * 0xA27416F5.toInt()
            t = rol16(t) * 0xD38396FF.toInt()
            state = rol16(rol16(t) * 0x7C932B89) * 0xBFA49F69.toInt()
            acc += state

            i += 2
        }
        return (swapLast xor state) to (swapAcc xor acc)
    }

    private fun writeLE32(v: Int, buf: ByteArray, idx: Int) {
        buf[idx] = (v and 0xFF).toByte()
        buf[idx + 1] = ((v ushr 8) and 0xFF).toByte()
        buf[idx + 2] = ((v ushr 16) and 0xFF).toByte()
        buf[idx + 3] = ((v ushr 24) and 0xFF).toByte()
    }

    private val HEX = "0123456789ABCDEF".toCharArray()
    private fun hexUpper(bytes: ByteArray): String {
        val out = CharArray(bytes.size * 2)
        for (i in bytes.indices) {
            val b = bytes[i].toInt() and 0xFF
            out[i * 2] = HEX[b ushr 4]
            out[i * 2 + 1] = HEX[b and 0x0F]
        }
        return String(out)
    }
}
