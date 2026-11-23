package app.yuyue.speech

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMuxer
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 广播剧's clips as kept on the device: AAC in MP4, one channel at 48 kHz, 64 kbps — a twentieth of
 * the float samples they're made as. Clips made before were kept as raw float (`.f32`) and are
 * still read.
 */
object ClipAudio {
    private const val RATE = 48_000
    private const val TIMEOUT = 10_000L

    /** Writes `samples` (mono, 48 kHz, −1…1) to `out`, beside first and moved in whole. */
    fun write(samples: FloatArray, out: File) {
        val part = File(out.parentFile, out.name + ".part")
        part.delete()
        val format = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_AAC, RATE, 1).apply {
            setInteger(MediaFormat.KEY_AAC_PROFILE, MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            // 40 kbps mono AAC-LC is transparent for speech and cuts clip storage/sync size by ~37%.
            setInteger(MediaFormat.KEY_BIT_RATE, 40_000)
            setInteger(MediaFormat.KEY_MAX_INPUT_SIZE, 1 shl 14)
        }
        val codec = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_AAC)
        codec.configure(format, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
        val muxer = MediaMuxer(part.path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        var track = -1
        val info = MediaCodec.BufferInfo()
        fun drain(end: Boolean) {
            while (true) {
                val i = codec.dequeueOutputBuffer(info, if (end) TIMEOUT else 0)
                when {
                    i == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> { track = muxer.addTrack(codec.outputFormat); muxer.start() }
                    i >= 0 -> {
                        val buf = codec.getOutputBuffer(i)!!
                        if (info.size > 0 && track >= 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) muxer.writeSampleData(track, buf, info)
                        codec.releaseOutputBuffer(i, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                    }
                    else -> if (!end) return
                }
            }
        }
        try {
            // 16-bit PCM in, a buffer at a time.
            var at = 0
            while (at < samples.size) {
                val i = codec.dequeueInputBuffer(TIMEOUT)
                if (i < 0) { drain(false); continue }
                val buf = codec.getInputBuffer(i)!!.order(ByteOrder.nativeOrder())
                buf.clear()
                val n = minOf(buf.capacity() / 2, samples.size - at)
                for (k in 0 until n) buf.putShort((samples[at + k].coerceIn(-1f, 1f) * 32767).toInt().toShort())
                codec.queueInputBuffer(i, 0, n * 2, at.toLong() * 1_000_000 / RATE, 0)
                at += n
                drain(false)
            }
            while (true) {
                val i = codec.dequeueInputBuffer(TIMEOUT)
                if (i >= 0) { codec.queueInputBuffer(i, 0, 0, at.toLong() * 1_000_000 / RATE, MediaCodec.BUFFER_FLAG_END_OF_STREAM); break }
                drain(false)
            }
            drain(true)
        } finally {
            runCatching { codec.stop() }
            codec.release()
            runCatching { if (track >= 0) muxer.stop() }
            muxer.release()
        }
        if (track < 0 || !part.renameTo(out)) {
            part.delete()
            throw java.io.IOException("clip not written")
        }
    }

    /** A clip's samples (mono, 48 kHz): AAC decoded with the encoder's lead-in taken off; or an
     *  old raw float clip. */
    fun read(file: File): FloatArray? {
        if (!file.isFile) return null
        if (file.name.endsWith(".f32")) {
            val fb = ByteBuffer.wrap(file.readBytes()).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
            return FloatArray(fb.remaining()).also { fb.get(it) }
        }
        val (samples, said) = decode(file) ?: return null
        val from = (said ?: lead).coerceIn(0, samples.size)
        return samples.copyOfRange(from, samples.size)
    }

    /** How many samples this device's AAC encoder puts before the sound (silence the decoder
     *  gives back). MP4s written by the system don't say, so it's measured once: a click encoded
     *  and decoded, and where it comes back. */
    private val lead: Int by lazy {
        runCatching {
            val f = File.createTempFile("lead", ".m4a")
            try {
                val click = FloatArray(8192).also { it[3000] = 0.9f }
                write(click, f)
                val back = decode(f)?.first ?: return@runCatching 0
                val at = back.indices.maxByOrNull { kotlin.math.abs(back[it]) } ?: 3000
                (at - 3000).coerceIn(0, 4096)
            } finally {
                f.delete()
            }
        }.getOrDefault(0)
    }

    /** Every sample the decoder gives back, and the lead-in when the file says it. */
    private fun decode(file: File): Pair<FloatArray, Int?>? {
        val ex = MediaExtractor()
        try {
            ex.setDataSource(file.path)
            if (ex.trackCount == 0) return FloatArray(0) to null
            ex.selectTrack(0)
            val format = ex.getTrackFormat(0)
            val said = if (format.containsKey(MediaFormat.KEY_ENCODER_DELAY)) format.getInteger(MediaFormat.KEY_ENCODER_DELAY) else null
            val length = if (format.containsKey(MediaFormat.KEY_DURATION)) (format.getLong(MediaFormat.KEY_DURATION) * RATE / 1_000_000).toInt() else RATE * 10
            val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            codec.configure(format, null, null, 0)
            codec.start()
            var out = FloatArray(maxOf(RATE, minOf(length, RATE * 120)))
            var n = 0
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            val info = MediaCodec.BufferInfo()
            var inputDone = false
            try {
                while (true) {
                    if (!inputDone) {
                        val i = codec.dequeueInputBuffer(TIMEOUT)
                        if (i >= 0) {
                            val size = ex.readSampleData(codec.getInputBuffer(i)!!, 0)
                            if (size < 0) {
                                codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                inputDone = true
                            } else {
                                codec.queueInputBuffer(i, 0, size, ex.sampleTime, 0)
                                ex.advance()
                            }
                        }
                    }
                    val o = codec.dequeueOutputBuffer(info, TIMEOUT)
                    if (o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        channels = codec.outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    } else if (o >= 0) {
                        val pcm = codec.getOutputBuffer(o)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                        pcm.position(info.offset / 2)
                        val frames = (info.size / 2) / channels
                        if (n + frames > out.size) out = out.copyOf(maxOf(out.size * 2, n + frames))
                        for (f in 0 until frames) {
                            var sum = 0f
                            for (c in 0 until channels) sum += pcm.get()
                            out[n++] = sum / channels / 32768f
                        }
                        codec.releaseOutputBuffer(o, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                    }
                }
            } finally {
                runCatching { codec.stop() }
                codec.release()
            }
            return out.copyOf(n) to said
        } catch (e: Exception) {
            return null
        } finally {
            ex.release()
        }
    }
}
