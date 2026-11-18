package app.yuyue.speech

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.nio.ByteOrder
import java.nio.FloatBuffer
import kotlin.coroutines.coroutineContext
import kotlin.math.sqrt

/**
 * 强制对齐, as on Apple: Meta's MMS alignment model, 8-bit, as ONNX. It turns a
 * recording into how likely each letter is every 20 ms; the core lays the book's words along that.
 *
 * The model (about 300 MB) isn't in the app: it's fetched from 语阅's own GitHub release the first
 * time an audiobook is made, checked against its BLAKE3, and kept beside the library. Its licence
 * is CC-BY-NC 4.0 — 语阅 is free and takes no money, and 开源许可 names it.
 */
object Aligner {
    const val FILE = "mms_fa.int8.onnx"
    private const val URL = "https://github.com/inokoru/yuyue/releases/download/models-1/mms_fa.int8.onnx"
    /** BLAKE3 of the file (`yuyue digest`). */
    private const val DIGEST = "fad66a4545c60898c382c001aefd26dafb550e3baf58b915480b70dd89d48861"
    private const val BYTES = 317_327_887L
    /** The model's letters a frame (`forced::LETTERS`). */
    const val LETTERS = 28
    const val RATE = 16_000
    /** 30 s a window, with 2 s either side. */
    private const val HOP = 30 * RATE
    private const val CONTEXT = 2 * RATE

    fun file(dataDir: File) = File(dataDir, "models/$FILE")
    fun installed(dataDir: File) = file(dataDir).isFile

    /** Fetched and checked unless it's here. `progress` hears 0–1. */
    suspend fun ensure(dataDir: File, progress: (Double) -> Unit) = withContext(Dispatchers.IO) {
        if (installed(dataDir)) return@withContext
        val dir = File(dataDir, "models").apply { mkdirs() }
        if (dir.usableSpace < BYTES * 2) throw java.io.IOException("ENOSPC")
        val temp = File(dir, "$FILE.part")
        try {
            OkHttpClient().newCall(Request.Builder().url(URL).build()).execute().use { r ->
                if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                val body = r.body ?: throw java.io.IOException("empty")
                val total = body.contentLength().takeIf { it > 0 } ?: BYTES
                body.byteStream().use { input ->
                    temp.outputStream().use { out ->
                        val buf = ByteArray(1 shl 16)
                        var done = 0L
                        var last = 0L
                        while (true) {
                            coroutineContext.ensureActive()
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            if (done - last > (1 shl 20)) {
                                last = done
                                progress(done.toDouble() / total)
                            }
                        }
                    }
                }
            }
            if (uniffi.yuyue_core.fileDigest(temp.path) != DIGEST) throw java.io.IOException("digest")
            if (!temp.renameTo(file(dataDir))) throw java.io.IOException("rename")
        } finally {
            temp.delete()
        }
    }

    /** The model's output for one file: frames × `LETTERS` log probabilities, row by row, and
     *  how long the file is. `progress` hears seconds done. */
    suspend fun emissions(dataDir: File, audio: File, progress: (Double) -> Unit): Triple<FloatArray, Int, Double> = withContext(Dispatchers.Default) {
        val env = OrtEnvironment.getEnvironment()
        val options = OrtSession.SessionOptions().apply {
            setIntraOpNumThreads(maxOf(1, Runtime.getRuntime().availableProcessors() - 1))
        }
        env.createSession(file(dataDir).path, options).use { session ->
            // Decoded as it goes, a window at a time: a whole book in one file is hours of sound,
            // far more than a phone gives an app.
            Wave(audio).use { wave ->
                val out = FloatArrayBuilder()
                var start = 0
                while (true) {
                    coroutineContext.ensureActive()
                    wave.fillTo(start + HOP + CONTEXT)
                    if (start >= wave.end) break
                    val a = maxOf(0, start - CONTEXT)
                    val b = minOf(wave.end, start + HOP + CONTEXT)
                    val x = wave.copy(a, b)
                    // The layer norm the model was trained with, over the window.
                    var mean = 0.0
                    for (v in x) mean += v
                    mean /= x.size
                    var variance = 0.0
                    for (v in x) variance += (v - mean) * (v - mean)
                    variance /= x.size
                    val scale = (1 / sqrt(variance + 1e-5)).toFloat()
                    for (i in x.indices) x[i] = ((x[i] - mean) * scale).toFloat()
                    OnnxTensor.createTensor(env, FloatBuffer.wrap(x), longArrayOf(1, x.size.toLong())).use { input ->
                        session.run(mapOf("wav" to input)).use { result ->
                            val t = result[0] as OnnxTensor
                            val frames = t.info.shape[1].toInt()
                            val values = t.floatBuffer
                            val fps = frames / ((b - a).toDouble() / RATE)
                            val lo = Math.round((start - a).toDouble() / RATE * fps).toInt()
                            val hi = minOf(frames, lo + Math.round(minOf(HOP, wave.end - start).toDouble() / RATE * fps).toInt())
                            val chunk = FloatArray(maxOf(0, hi - lo) * LETTERS)
                            values.position(lo * LETTERS)
                            values.get(chunk)
                            out.addAll(chunk)
                        }
                    }
                    start += HOP
                    // What no later window looks at again goes.
                    wave.dropBefore(start - CONTEXT)
                    progress(minOf(start, wave.end).toDouble() / RATE)
                }
                val all = out.toArray()
                Triple(all, all.size / LETTERS, wave.end.toDouble() / RATE)
            }
        }
    }

    /** Each 30 ms frame's level (dB) over a whole file, for 人声检测: decoded once, start to end. */
    suspend fun levels(audio: File): FloatArray = withContext(Dispatchers.Default) {
        val frame = 480
        val out = FloatArrayBuilder()
        Wave(audio).use { wave ->
            var at = 0
            while (true) {
                coroutineContext.ensureActive()
                wave.fillTo(at + frame * 2000)
                val end = at + (wave.end - at) / frame * frame
                if (end <= at) break
                val x = wave.copy(at, end)
                var i = 0
                while (i < x.size) {
                    var sum = 0.0
                    for (j in i until i + frame) sum += x[j] * x[j]
                    out.add((10 * Math.log10(sum / frame + 1e-10)).toFloat())
                    i += frame
                }
                at = end
                wave.dropBefore(at)
            }
        }
        out.toArray()
    }

    /** A file's sound, one channel at 16 kHz, decoded by the system's codecs as it's asked for.
     *  Holds only what hasn't been dropped. Sample `i` of the whole file is `copy(i, i + 1)`. */
    internal class Wave(audio: File) : java.io.Closeable {
        private val ex = MediaExtractor()
        private val codec: MediaCodec
        private var rate: Int
        private var channels: Int
        private val info = MediaCodec.BufferInfo()
        private var inputDone = false
        private var outputDone = false
        /** 16 kHz samples kept, from `base`. */
        private var buf = FloatArray(1 shl 20)
        private var base = 0
        private var n = 0
        val end: Int get() = base + n
        // Resampling state: the moving average (against aliasing) and where the next output falls.
        private var ring = FloatArray(1)
        private var ringAt = 0
        private var ringSum = 0f
        private var inCount = 0L
        private var prev = 0f
        private var outCount = 0L

        init {
            ex.setDataSource(audio.path)
            val track = (0 until ex.trackCount).firstOrNull { ex.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true }
                ?: throw java.io.IOException("no audio")
            ex.selectTrack(track)
            val format = ex.getTrackFormat(track)
            rate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            codec.configure(format, null, null, 0)
            codec.start()
            setRate(rate)
        }

        private fun setRate(r: Int) {
            rate = r
            ring = FloatArray(if (r > RATE * 2) r / RATE else 1)
            ringAt = 0
            ringSum = 0f
        }

        /** Decodes until sample `want` is there, or the file is over. */
        fun fillTo(want: Int) {
            while (end < want && !outputDone) step()
        }

        fun copy(a: Int, b: Int): FloatArray = buf.copyOfRange(a - base, b - base)

        fun dropBefore(at: Int) {
            val k = (at - base).coerceIn(0, n)
            if (k == 0) return
            System.arraycopy(buf, k, buf, 0, n - k)
            n -= k
            base += k
        }

        private fun push(v: Float) {
            if (n == buf.size) buf = buf.copyOf(buf.size * 2)
            buf[n++] = v
        }

        /** One native sample in: smoothed, then as many 16 kHz samples out as fall before it. */
        private fun input(x: Float) {
            ringSum += x - ring[ringAt]
            ring[ringAt] = x
            ringAt = (ringAt + 1) % ring.size
            val v = ringSum / minOf(inCount + 1, ring.size.toLong())
            // Output sample j sits at native position j * rate / 16000, between prev (inCount - 1) and v (inCount).
            while (true) {
                val p = outCount.toDouble() * rate / RATE
                if (p > inCount) break
                val f = (p - (inCount - 1)).toFloat().coerceIn(0f, 1f)
                push(if (inCount == 0L) v else prev + (v - prev) * f)
                outCount++
            }
            prev = v
            inCount++
        }

        private fun step() {
            if (!inputDone) {
                val i = codec.dequeueInputBuffer(10_000)
                if (i >= 0) {
                    val b = codec.getInputBuffer(i)!!
                    val k = ex.readSampleData(b, 0)
                    if (k < 0) {
                        codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                        inputDone = true
                    } else {
                        codec.queueInputBuffer(i, 0, k, ex.sampleTime, 0)
                        ex.advance()
                    }
                }
            }
            val o = codec.dequeueOutputBuffer(info, 10_000)
            when {
                o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> {
                    val f = codec.outputFormat
                    channels = f.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    val r = f.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                    if (r != rate) setRate(r)
                }
                o >= 0 -> {
                    val shorts = codec.getOutputBuffer(o)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                    val frames = shorts.remaining() / channels
                    for (k in 0 until frames) {
                        var sum = 0f
                        for (c in 0 until channels) sum += shorts.get(k * channels + c)
                        input(sum / channels / 32768f)
                    }
                    codec.releaseOutputBuffer(o, false)
                    if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) outputDone = true
                }
            }
        }

        override fun close() {
            runCatching { codec.stop() }
            codec.release()
            ex.release()
        }
    }

    private class FloatArrayBuilder {
        private var data = FloatArray(1 shl 20)
        private var n = 0
        fun add(v: Float) {
            if (n == data.size) data = data.copyOf(data.size * 2)
            data[n++] = v
        }
        fun addAll(v: FloatArray) {
            if (n + v.size > data.size) data = data.copyOf(maxOf(data.size * 2, n + v.size))
            v.copyInto(data, n)
            n += v.size
        }
        fun toArray() = data.copyOf(n)
    }
}
