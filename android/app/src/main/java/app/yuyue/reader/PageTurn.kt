package app.yuyue.reader

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaRecorder
import android.media.SoundPool
import android.net.Uri
import android.provider.OpenableColumns
import android.view.HapticFeedbackConstants
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.pressable
import app.yuyue.design.rememberPref
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.cos
import kotlin.math.exp
import kotlin.math.max
import kotlin.math.min
import kotlin.math.pow
import kotlin.math.roundToInt
import kotlin.math.sin
import kotlin.math.sqrt

/** Horizontal pagination animation style, mirrored on Apple (`reader.pageTurn`). */
enum class PageTurn(val key: String) {
    SLIDE("slide"),
    COVER("cover"),
    NONE("none");

    companion object {
        fun of(raw: String?): PageTurn = entries.firstOrNull { it.key == raw } ?: SLIDE
    }
}

/** Built-in selectable page-turn sound preset, mirrored on Apple (`reader.pageTurnSoundStyle`). */
enum class PageTurnSoundStyle(val key: String) {
    SOFT_PAPER("softPaper"),
    CRISP_BOOK("crispBook"),
    RICE_PAPER("ricePaper"),
    HARDCOVER("hardcover"),
    WOOD_CLICK("woodClick"),
    INK_DROP("inkDrop");

    fun title(): String = when (this) {
        SOFT_PAPER -> t("轻纸")
        CRISP_BOOK -> t("脆页")
        RICE_PAPER -> t("宣纸")
        HARDCOVER -> t("精装")
        WOOD_CLICK -> t("轻叩")
        INK_DROP -> t("滴墨")
    }

    fun subtitle(): String = when (this) {
        SOFT_PAPER -> t("柔和薄页")
        CRISP_BOOK -> t("清爽新书")
        RICE_PAPER -> t("绵厚柔润")
        HARDCOVER -> t("沉稳厚页")
        WOOD_CLICK -> t("指尖木案")
        INK_DROP -> t("水墨落砚")
    }

    fun icon(): ImageVector = when (this) {
        SOFT_PAPER -> Icons.docText
        CRISP_BOOK -> Icons.book
        RICE_PAPER -> Icons.sparkles
        HARDCOVER -> Icons.bookClosed
        WOOD_CLICK -> Icons.handTap
        INK_DROP -> Icons.waveform
    }

    companion object {
        fun of(raw: String?): PageTurnSoundStyle = entries.firstOrNull { it.key == raw } ?: SOFT_PAPER
    }
}

data class CustomPageTurnSound(
    val id: String,
    val name: String,
    val durationMs: Int,
    val createdAt: Long,
) {
    val selectionKey: String get() = CustomPageTurnSounds.PREFIX + id
}

class ProcessedCustomSound(
    val wavData: ByteArray,
    val durationMs: Int,
    val sourceSamples: FloatArray = FloatArray(0),
    val sampleRate: Int = PageTurnSound.SAMPLE_RATE,
    val trimStart: Double = 0.0,
    val trimEnd: Double = 1.0,
    val autoTrimStart: Double = 0.0,
    val autoTrimEnd: Double = 1.0,
    val waveform: FloatArray = FloatArray(0),
)

class CustomSoundException(val kind: Kind) : Exception(kind.name) {
    enum class Kind { UNREADABLE, SILENT_OR_EMPTY, TOO_SHORT, SAVE_FAILED }

    fun localizedMessageText(): String = when (kind) {
        Kind.UNREADABLE -> t("这段音频读不出来，换一个试试")
        Kind.SILENT_OR_EMPTY -> t("没有检测到明显声音，再试一次")
        Kind.TOO_SHORT -> t("声音太短了，换一段试试")
        Kind.SAVE_FAILED -> t("保存自定义音效失败，请重试")
    }
}

object PageTurnPrefs {
    const val KEY = "reader.pageTurn"
    const val SOUND_KEY = "reader.pageTurnSound"
    const val SOUND_STYLE_KEY = "reader.pageTurnSoundStyle"
    const val SOUND_VOLUME_KEY = "reader.pageTurnSoundVolume"
    const val HAPTIC_KEY = "reader.pageTurnHaptic"

    const val DEFAULT_VOLUME = 0.60f
    const val MIN_VOLUME = 0.15f
    const val MAX_VOLUME = 1.0f

    val current: PageTurn
        get() = if (Prefs.isReady) PageTurn.of(Prefs.string(KEY, PageTurn.SLIDE.key)) else PageTurn.SLIDE

    val sound: Boolean
        get() = Prefs.isReady && Prefs.bool(SOUND_KEY, false)

    val soundSelection: String
        get() = if (Prefs.isReady) Prefs.string(SOUND_STYLE_KEY, PageTurnSoundStyle.SOFT_PAPER.key) else PageTurnSoundStyle.SOFT_PAPER.key

    val soundStyle: PageTurnSoundStyle
        get() = PageTurnSoundStyle.of(soundSelection)

    val soundVolume: Float
        get() = if (Prefs.isReady) Prefs.float(SOUND_VOLUME_KEY, DEFAULT_VOLUME).coerceIn(MIN_VOLUME, MAX_VOLUME) else DEFAULT_VOLUME

    val haptic: Boolean
        get() = Prefs.isReady && Prefs.bool(HAPTIC_KEY, false)
}

/**
 * Local store & DSP post-processor for user-imported or recorded page-turn sound clips.
 * Mirror of Apple's `CustomPageTurnSounds`.
 */
object CustomPageTurnSounds {
    const val PREFIX = "custom:"
    const val MAX_DURATION_SECONDS = 0.65
    const val MIN_DURATION_SECONDS = 0.025

    var version by mutableIntStateOf(0)
        private set

    fun customId(raw: String?): String? {
        if (raw == null || !raw.startsWith(PREFIX)) return null
        val id = raw.removePrefix(PREFIX)
        return id.ifEmpty { null }
    }

    private fun folder(context: Context): File = File(context.applicationContext.filesDir, "page_turn_sounds")
    private fun indexFile(context: Context): File = File(folder(context), "index.json")
    fun wavFile(context: Context, id: String): File = File(folder(context), "$id.wav")

    fun list(context: Context): List<CustomPageTurnSound> {
        val file = indexFile(context)
        if (!file.isFile) return emptyList()
        return runCatching {
            val arr = JSONArray(file.readText())
            buildList {
                for (i in 0 until arr.length()) {
                    val o = arr.optJSONObject(i) ?: continue
                    val id = o.optString("id")
                    if (id.isNotEmpty() && wavFile(context, id).isFile) {
                        add(
                            CustomPageTurnSound(
                                id = id,
                                name = o.optString("name").ifEmpty { t("自定义音效") },
                                durationMs = o.optInt("durationMs", 200),
                                createdAt = o.optLong("createdAt", 0L),
                            )
                        )
                    }
                }
            }
        }.getOrDefault(emptyList())
    }

    private fun writeIndex(context: Context, items: List<CustomPageTurnSound>) {
        val dir = folder(context)
        dir.mkdirs()
        val arr = JSONArray()
        for (item in items) {
            arr.put(
                JSONObject().apply {
                    put("id", item.id)
                    put("name", item.name)
                    put("durationMs", item.durationMs)
                    put("createdAt", item.createdAt)
                }
            )
        }
        indexFile(context).writeText(arr.toString())
    }

    fun save(context: Context, processed: ProcessedCustomSound, rawName: String): CustomPageTurnSound {
        val trimmed = rawName.trim().take(16)
        val finalName = trimmed.ifEmpty { t("自定义音效") }
        val id = UUID.randomUUID().toString().replace("-", "").take(12).lowercase()
        val dir = folder(context)
        try {
            dir.mkdirs()
            FileOutputStream(wavFile(context, id)).use { it.write(processed.wavData) }
        } catch (_: Throwable) {
            throw CustomSoundException(CustomSoundException.Kind.SAVE_FAILED)
        }
        val item = CustomPageTurnSound(
            id = id,
            name = finalName,
            durationMs = processed.durationMs,
            createdAt = System.currentTimeMillis() / 1000L,
        )
        val updated = list(context) + item
        runCatching { writeIndex(context, updated) }
        version++
        return item
    }

    fun delete(context: Context, id: String) {
        val updated = list(context).filterNot { it.id == id }
        runCatching { wavFile(context, id).delete() }
        runCatching { writeIndex(context, updated) }
        PageTurnSound.evictCustom(id)
        if (Prefs.isReady && Prefs.string(PageTurnPrefs.SOUND_STYLE_KEY, "") == PREFIX + id) {
            Prefs.set(PageTurnPrefs.SOUND_STYLE_KEY, PageTurnSoundStyle.SOFT_PAPER.key)
        }
        version++
    }

    fun displayNameForUri(context: Context, uri: Uri): String {
        val name = runCatching {
            context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
                if (c.moveToFirst()) c.getString(0) else null
            }
        }.getOrNull()
        val base = (name ?: uri.lastPathSegment ?: "").substringAfterLast('/').substringBeforeLast('.')
        return base.trim().take(16).ifEmpty { t("自定义音效") }
    }

    /** Decodes any MediaExtractor/MediaCodec-supported audio URI into 44.1 kHz mono and post-processes it. */
    fun decodeAndProcessUri(context: Context, uri: Uri): ProcessedCustomSound {
        val tmp = File.createTempFile("yy_custom_turn_", ".audio", context.cacheDir)
        try {
            context.contentResolver.openInputStream(uri)?.use { input ->
                FileOutputStream(tmp).use { output -> input.copyTo(output) }
            } ?: throw CustomSoundException(CustomSoundException.Kind.UNREADABLE)
            val (mono, srcRate) = decodeToMonoFloat(tmp)
            val resampled = if (srcRate == PageTurnSound.SAMPLE_RATE) {
                mono
            } else {
                resampleLinear(mono, srcRate, PageTurnSound.SAMPLE_RATE)
            }
            return trimAndNormalize(resampled, PageTurnSound.SAMPLE_RATE)
        } finally {
            tmp.delete()
        }
    }

    private fun decodeToMonoFloat(file: File): Pair<FloatArray, Int> {
        val ex = MediaExtractor()
        try {
            ex.setDataSource(file.path)
            var trackIndex = -1
            var format: MediaFormat? = null
            for (i in 0 until ex.trackCount) {
                val f = ex.getTrackFormat(i)
                val mime = f.getString(MediaFormat.KEY_MIME) ?: ""
                if (mime.startsWith("audio/")) {
                    trackIndex = i
                    format = f
                    break
                }
            }
            if (trackIndex < 0 || format == null) {
                throw CustomSoundException(CustomSoundException.Kind.UNREADABLE)
            }
            ex.selectTrack(trackIndex)
            val mime = format.getString(MediaFormat.KEY_MIME) ?: throw CustomSoundException(CustomSoundException.Kind.UNREADABLE)
            var sampleRate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
            val maxFrames = sampleRate * 8 // Cap decode to 8 seconds for instant responsiveness
            val codec = MediaCodec.createDecoderByType(mime)
            codec.configure(format, null, null, 0)
            codec.start()
            var out = FloatArray(min(maxFrames, sampleRate * 2).coerceAtLeast(4096))
            var n = 0
            val info = MediaCodec.BufferInfo()
            var inputDone = false
            try {
                while (n < maxFrames) {
                    if (!inputDone) {
                        val inIdx = codec.dequeueInputBuffer(10_000L)
                        if (inIdx >= 0) {
                            val inBuf = codec.getInputBuffer(inIdx)!!
                            val size = ex.readSampleData(inBuf, 0)
                            if (size < 0) {
                                codec.queueInputBuffer(inIdx, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                inputDone = true
                            } else {
                                codec.queueInputBuffer(inIdx, 0, size, ex.sampleTime, 0)
                                ex.advance()
                            }
                        }
                    }
                    val outIdx = codec.dequeueOutputBuffer(info, 10_000L)
                    if (outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        val of = codec.outputFormat
                        if (of.containsKey(MediaFormat.KEY_SAMPLE_RATE)) sampleRate = of.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                        if (of.containsKey(MediaFormat.KEY_CHANNEL_COUNT)) channels = of.getInteger(MediaFormat.KEY_CHANNEL_COUNT).coerceAtLeast(1)
                    } else if (outIdx >= 0) {
                        val pcm = codec.getOutputBuffer(outIdx)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                        pcm.position(info.offset / 2)
                        val frames = (info.size / 2) / channels
                        if (n + frames > out.size) {
                            out = out.copyOf(min(maxFrames, max(out.size * 2, n + frames)))
                        }
                        val take = min(frames, out.size - n)
                        for (f in 0 until take) {
                            var sum = 0f
                            for (c in 0 until channels) sum += pcm.get()
                            out[n++] = (sum / channels) / 32768f
                        }
                        codec.releaseOutputBuffer(outIdx, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
                    } else if (inputDone) {
                        break
                    }
                }
            } finally {
                runCatching { codec.stop() }
                codec.release()
            }
            if (n == 0) throw CustomSoundException(CustomSoundException.Kind.SILENT_OR_EMPTY)
            return out.copyOf(n) to sampleRate
        } catch (e: CustomSoundException) {
            throw e
        } catch (_: Throwable) {
            throw CustomSoundException(CustomSoundException.Kind.UNREADABLE)
        } finally {
            runCatching { ex.release() }
        }
    }

    private fun resampleLinear(input: FloatArray, fromRate: Int, toRate: Int): FloatArray {
        if (input.isEmpty() || fromRate <= 0 || fromRate == toRate) return input
        val ratio = fromRate.toDouble() / toRate.toDouble()
        val outLen = max(1, (input.size / ratio).toInt())
        val out = FloatArray(outLen)
        for (i in 0 until outLen) {
            val pos = i * ratio
            val idx = pos.toInt().coerceIn(0, input.size - 1)
            val next = (idx + 1).coerceAtMost(input.size - 1)
            val frac = (pos - idx).toFloat()
            out[i] = input[idx] * (1f - frac) + input[next] * frac
        }
        return out
    }

    const val WAVEFORM_BAR_COUNT = 40

    /**
     * Pure DSP post-processor (identical to Apple `CustomPageTurnSounds.trimAndNormalize`):
     * DC + 60 Hz rumble high-pass → smart leading/trailing silence trim → 0.65 s max cap →
     * smooth raised-cosine fades → peak normalization → 16-bit mono WAV + waveform envelope.
     */
    fun trimAndNormalize(samples: FloatArray, sampleRate: Int): ProcessedCustomSound {
        if (samples.isEmpty() || sampleRate <= 0) {
            throw CustomSoundException(CustomSoundException.Kind.SILENT_OR_EMPTY)
        }
        val n = samples.size
        var sum = 0.0
        for (s in samples) sum += s.toDouble()
        val mean = sum / n

        val rc = 1.0 / (2.0 * PI * 60.0)
        val dt = 1.0 / sampleRate
        val alpha = rc / (rc + dt)
        val cleaned = DoubleArray(n)
        var prevX = 0.0
        var prevY = 0.0
        var peak = 0.0
        for (i in 0 until n) {
            val x = samples[i].toDouble() - mean
            val y = alpha * (prevY + x - prevX)
            prevX = x
            prevY = y
            cleaned[i] = y
            val a = abs(y)
            if (a > peak) peak = a
        }
        if (peak < 0.004) throw CustomSoundException(CustomSoundException.Kind.SILENT_OR_EMPTY)

        val startThresh = max(peak * 0.045, 0.006)
        var firstActive = 0
        for (i in 0 until n) {
            if (abs(cleaned[i]) >= startThresh) {
                firstActive = i
                break
            }
        }
        val preRoll = (sampleRate * 0.008).toInt()
        val trimStart = max(0, firstActive - preRoll)

        val maxFrames = (sampleRate * MAX_DURATION_SECONDS).toInt()
        val searchEnd = min(n, trimStart + maxFrames)
        val endThresh = max(peak * 0.022, 0.003)
        var lastActive = trimStart
        for (i in (searchEnd - 1) downTo trimStart) {
            if (abs(cleaned[i]) >= endThresh) {
                lastActive = i
                break
            }
        }
        val postRoll = (sampleRate * 0.024).toInt()
        val trimEnd = min(searchEnd, lastActive + postRoll)
        val length = trimEnd - trimStart
        val minFrames = (sampleRate * MIN_DURATION_SECONDS).toInt()
        if (length < minFrames) throw CustomSoundException(CustomSoundException.Kind.TOO_SHORT)

        val padBefore = (sampleRate * 0.16).toInt()
        val padAfter = (sampleRate * 0.22).toInt()
        val minWindow = (sampleRate * 0.72).toInt()
        val maxWindow = (sampleRate * 1.40).toInt()
        val winStart = max(0, trimStart - padBefore)
        var winEnd = min(n, max(trimEnd + padAfter, min(n, winStart + minWindow)))
        if (winEnd - winStart > maxWindow) {
            winEnd = min(n, max(trimEnd, winStart + maxWindow))
        }
        val winLen = max(1, winEnd - winStart)
        val windowSamples = FloatArray(winLen) { cleaned[winStart + it].toFloat() }
        val relStart = (trimStart - winStart).toDouble() / winLen
        val relEnd = (trimEnd - winStart).toDouble() / winLen
        val waveform = computeWaveform(windowSamples, WAVEFORM_BAR_COUNT)

        val truncated = trimEnd == trimStart + maxFrames && trimEnd < n
        val slice = DoubleArray(length) { cleaned[trimStart + it] }
        val (wav, durationMs) = encodeNormalizedSlice(slice, sampleRate, truncated)
        return ProcessedCustomSound(
            wavData = wav,
            durationMs = durationMs,
            sourceSamples = windowSamples,
            sampleRate = sampleRate,
            trimStart = relStart,
            trimEnd = relEnd,
            autoTrimStart = relStart,
            autoTrimEnd = relEnd,
            waveform = waveform,
        )
    }

    fun renderTrimmed(
        base: ProcessedCustomSound,
        startFraction: Double,
        endFraction: Double,
    ): ProcessedCustomSound? {
        val total = base.sourceSamples.size
        if (total <= 0 || base.sampleRate <= 0) return null
        val clampedStart = startFraction.coerceIn(0.0, 1.0)
        val clampedEnd = endFraction.coerceIn(clampedStart, 1.0)
        val startIdx = (clampedStart * total).roundToInt().coerceIn(0, total - 1)
        val maxFrames = (base.sampleRate * MAX_DURATION_SECONDS).toInt()
        val minFrames = (base.sampleRate * MIN_DURATION_SECONDS).toInt()
        var endIdx = (clampedEnd * total).roundToInt().coerceIn(startIdx + minFrames, total)
        if (endIdx - startIdx > maxFrames) endIdx = startIdx + maxFrames
        if (endIdx > total || endIdx - startIdx < minFrames) return null

        val slice = DoubleArray(endIdx - startIdx) { base.sourceSamples[startIdx + it].toDouble() }
        val truncated = endIdx < total
        val encoded = runCatching { encodeNormalizedSlice(slice, base.sampleRate, truncated) }.getOrNull() ?: return null
        return ProcessedCustomSound(
            wavData = encoded.first,
            durationMs = encoded.second,
            sourceSamples = base.sourceSamples,
            sampleRate = base.sampleRate,
            trimStart = startIdx.toDouble() / total,
            trimEnd = endIdx.toDouble() / total,
            autoTrimStart = base.autoTrimStart,
            autoTrimEnd = base.autoTrimEnd,
            waveform = base.waveform,
        )
    }

    fun computeWaveform(samples: FloatArray, barCount: Int): FloatArray {
        if (samples.isEmpty() || barCount <= 0) return FloatArray(max(0, barCount)) { 0.1f }
        val peaks = FloatArray(barCount)
        var maxPeak = 0.0001f
        val n = samples.size
        for (b in 0 until barCount) {
            val s0 = (b * n) / barCount
            val s1 = max(s0 + 1, ((b + 1) * n) / barCount).coerceAtMost(n)
            var p = 0f
            var sq = 0f
            for (i in s0 until s1) {
                val a = abs(samples[i])
                if (a > p) p = a
                sq += a * a
            }
            val rms = sqrt(sq / max(1, s1 - s0).toDouble()).toFloat()
            val v = p * 0.65f + rms * 0.35f
            peaks[b] = v
            if (v > maxPeak) maxPeak = v
        }
        return FloatArray(barCount) { i ->
            val norm = (peaks[i] / maxPeak).coerceIn(0f, 1f)
            max(0.08f, norm.toDouble().pow(0.72).toFloat())
        }
    }

    private fun encodeNormalizedSlice(
        slice: DoubleArray,
        sampleRate: Int,
        truncated: Boolean,
    ): Pair<ByteArray, Int> {
        val length = slice.size
        var sliceSum = 0.0
        for (x in slice) sliceSum += x
        val sliceMean = sliceSum / length
        for (i in 0 until length) slice[i] -= sliceMean

        val fadeIn = min(length / 3, max(1, (sampleRate * 0.004).toInt()))
        val fadeOut = min(length / 2, max(1, (sampleRate * if (truncated) 0.065 else 0.022).toInt()))
        for (i in 0 until fadeIn) {
            val u = i.toDouble() / fadeIn
            slice[i] *= 0.5 - 0.5 * cos(PI * u)
        }
        for (i in 0 until fadeOut) {
            val u = i.toDouble() / fadeOut
            slice[length - 1 - i] *= 0.5 - 0.5 * cos(PI * u)
        }

        var slicePeak = 0.0
        for (x in slice) {
            val a = abs(x)
            if (a > slicePeak) slicePeak = a
        }
        if (slicePeak <= 1e-5) throw CustomSoundException(CustomSoundException.Kind.SILENT_OR_EMPTY)

        val gain = 0.60 / slicePeak
        val pcm = ShortArray(length)
        for (i in 0 until length) {
            val v = (slice[i] * gain).coerceIn(-1.0, 1.0)
            pcm[i] = (v * Short.MAX_VALUE).toInt().toShort()
        }
        val wav = PageTurnSound.encodeWav(pcm, sampleRate)
        val durationMs = ((length.toDouble() / sampleRate) * 1000.0).roundToInt()
        return wav to durationMs
    }
}

/**
 * Multi-layer 44.1 kHz synthesized page-turn sound engine (identical DSP math to Apple's
 * `PageTurnSound`), supporting built-in presets, custom user WAVs, and adjustable volume.
 */
object PageTurnSound {
    const val SAMPLE_RATE = 44100
    const val VARIANT_COUNT = 3

    private var pool: SoundPool? = null
    private val soundIds = ConcurrentHashMap<String, Int>()
    private val loadedIds = ConcurrentHashMap.newKeySet<Int>()
    private val pendingPlay = AtomicReference<Pair<Int, Float>?>(null)
    private val variantCursor = AtomicInteger(0)

    private fun gainFor(volume: Float): Float = volume.coerceIn(PageTurnPrefs.MIN_VOLUME, PageTurnPrefs.MAX_VOLUME) * 0.80f

    private fun ensurePool(): SoundPool {
        pool?.let { return it }
        synchronized(this) {
            pool?.let { return it }
            val attrs = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build()
            val sp = SoundPool.Builder()
                .setMaxStreams(4)
                .setAudioAttributes(attrs)
                .build()
            sp.setOnLoadCompleteListener { loadedPool, sampleId, status ->
                if (status == 0) {
                    loadedIds.add(sampleId)
                    val pending = pendingPlay.get()
                    if (pending != null && pending.first == sampleId && pendingPlay.compareAndSet(pending, null)) {
                        loadedPool.play(sampleId, pending.second, pending.second, 1, 0, 1.0f)
                    }
                }
            }
            pool = sp
            return sp
        }
    }

    private fun loadBuiltInVariant(context: Context, style: PageTurnSoundStyle, variant: Int): Int {
        val k = "builtin_${style.key}_$variant"
        soundIds[k]?.let { return it }
        val sp = ensurePool()
        val app = context.applicationContext
        val file = File(app.cacheDir, "yy_page_turn_v2_${style.key}_$variant.wav")
        if (!file.exists() || file.length() < 1024L) {
            runCatching {
                val wav = synthesizeWav(style, variant)
                FileOutputStream(file).use { it.write(wav) }
            }
        }
        if (!file.exists()) return 0
        val id = sp.load(file.absolutePath, 1)
        if (id != 0) soundIds[k] = id
        return id
    }

    private fun loadCustom(context: Context, customId: String): Int {
        val k = "custom_$customId"
        soundIds[k]?.let { return it }
        val file = CustomPageTurnSounds.wavFile(context, customId)
        if (!file.isFile) return 0
        val sp = ensurePool()
        val id = sp.load(file.absolutePath, 1)
        if (id != 0) soundIds[k] = id
        return id
    }

    fun evictCustom(customId: String) {
        val k = "custom_$customId"
        val id = soundIds.remove(k) ?: return
        loadedIds.remove(id)
        pool?.unload(id)
    }

    fun prepare(context: Context, style: PageTurnSoundStyle = PageTurnPrefs.soundStyle) {
        for (v in 0 until VARIANT_COUNT) {
            loadBuiltInVariant(context, style, v)
        }
    }

    fun prepareSelection(context: Context, raw: String = PageTurnPrefs.soundSelection) {
        val customId = CustomPageTurnSounds.customId(raw)
        if (customId != null) {
            if (loadCustom(context, customId) != 0) return
        }
        prepare(context, PageTurnSoundStyle.of(raw))
    }

    fun play(context: Context) {
        if (!PageTurnPrefs.sound) return
        playSelection(context, PageTurnPrefs.soundSelection, PageTurnPrefs.soundVolume)
    }

    fun preview(context: Context, style: PageTurnSoundStyle, volume: Float = PageTurnPrefs.soundVolume) {
        playBuiltIn(context, style, volume)
    }

    fun previewSelection(context: Context, raw: String, volume: Float = PageTurnPrefs.soundVolume) {
        playSelection(context, raw, volume)
    }

    /** Plays an unsaved 44.1 kHz 16-bit mono WAV buffer directly via static AudioTrack (zero temp files). */
    fun previewWavBytes(wav: ByteArray, volume: Float = PageTurnPrefs.soundVolume) {
        if (wav.size <= 44) return
        val pcmBytes = wav.size - 44
        val gain = gainFor(volume)
        runCatching {
            val attrs = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ASSISTANCE_SONIFICATION)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build()
            val fmt = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(SAMPLE_RATE)
                .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                .build()
            val track = AudioTrack.Builder()
                .setAudioAttributes(attrs)
                .setAudioFormat(fmt)
                .setBufferSizeInBytes(pcmBytes)
                .setTransferMode(AudioTrack.MODE_STATIC)
                .build()
            track.write(wav, 44, pcmBytes)
            track.setVolume(gain)
            val totalFrames = pcmBytes / 2
            track.notificationMarkerPosition = totalFrames
            track.setPlaybackPositionUpdateListener(object : AudioTrack.OnPlaybackPositionUpdateListener {
                override fun onMarkerReached(t: AudioTrack) {
                    runCatching { t.stop(); t.release() }
                }
                override fun onPeriodicNotification(t: AudioTrack) {}
            })
            track.play()
        }
    }

    private fun playSelection(context: Context, raw: String, volume: Float) {
        val customId = CustomPageTurnSounds.customId(raw)
        if (customId != null) {
            val id = loadCustom(context, customId)
            if (id != 0) {
                playLoaded(id, gainFor(volume))
                return
            }
        }
        playBuiltIn(context, PageTurnSoundStyle.of(raw), volume)
    }

    private fun playBuiltIn(context: Context, style: PageTurnSoundStyle, volume: Float) {
        val v = (variantCursor.getAndIncrement() and 0x7FFF_FFFF) % VARIANT_COUNT
        val id = loadBuiltInVariant(context, style, v)
        if (id != 0) playLoaded(id, gainFor(volume))
    }

    private fun playLoaded(id: Int, gain: Float) {
        val sp = ensurePool()
        if (loadedIds.contains(id)) {
            sp.play(id, gain, gain, 1, 0, 1.0f)
        } else {
            pendingPlay.set(id to gain)
        }
    }

    // MARK: Acoustic Synthesis (mirrored in Apple PageTurn.swift)

    fun synthesizeWav(style: PageTurnSoundStyle, variant: Int = 0): ByteArray {
        val v = ((variant % VARIANT_COUNT) + VARIANT_COUNT) % VARIANT_COUNT
        val pcm = renderSamples(style, v)
        return encodeWav(pcm, SAMPLE_RATE)
    }

    private class Rng(seed: Long) {
        private var state: Long = (seed * -7046029254386353131L).let { if (it == 0L) 1L else it }

        fun nextSigned(): Double {
            state = state * 6364136223846793005L + 1442695040888963407L
            val v = ((state ushr 33) and 0x7FFF_FFFFL).toDouble()
            return (v / 2147483647.0) * 2.0 - 1.0
        }

        fun nextUnit(): Double = 0.5 * (nextSigned() + 1.0)
    }

    private class Pink {
        private var b0 = 0.0
        private var b1 = 0.0
        private var b2 = 0.0

        fun next(w: Double): Double {
            b0 = 0.99765 * b0 + w * 0.0990460
            b1 = 0.96300 * b1 + w * 0.2965164
            b2 = 0.57000 * b2 + w * 1.0526913
            return (b0 + b1 + b2 + w * 0.1848) * 0.25
        }
    }

    private class Biquad(private val lp: Boolean, freq: Double, q: Double) {
        private var b0 = 0.0
        private var b1 = 0.0
        private var b2 = 0.0
        private var a1 = 0.0
        private var a2 = 0.0
        private var z1 = 0.0
        private var z2 = 0.0

        init {
            set(freq, q)
        }

        fun set(freq: Double, q: Double) {
            val sr = SAMPLE_RATE.toDouble()
            val f = freq.coerceIn(20.0, sr * 0.45)
            val w = 2.0 * PI * f / sr
            val s = sin(w)
            val c = cos(w)
            val alpha = s / (2.0 * max(0.1, q))
            val rawB0: Double
            val rawB1: Double
            val rawB2: Double
            if (lp) {
                rawB0 = (1.0 - c) * 0.5
                rawB1 = 1.0 - c
                rawB2 = (1.0 - c) * 0.5
            } else {
                rawB0 = alpha
                rawB1 = 0.0
                rawB2 = -alpha
            }
            val a0 = 1.0 + alpha
            b0 = rawB0 / a0
            b1 = rawB1 / a0
            b2 = rawB2 / a0
            a1 = -2.0 * c / a0
            a2 = (1.0 - alpha) / a0
        }

        fun run(x: Double): Double {
            val y = b0 * x + z1
            z1 = b1 * x - a1 * y + z2
            z2 = b2 * x - a2 * y
            return y
        }
    }

    private fun smoothstep(u: Double): Double {
        val c = u.coerceIn(0.0, 1.0)
        return c * c * (3.0 - 2.0 * c)
    }

    private fun gauss(x: Double, mu: Double, sigma: Double): Double {
        val d = (x - mu) / sigma
        return exp(-0.5 * d * d)
    }

    private fun arch(u: Double, peak: Double): Double {
        if (u <= 0.0 || u >= 1.0) return 0.0
        return if (u < peak) {
            sin((u / peak) * PI * 0.5).pow(1.6)
        } else {
            cos(((u - peak) / (1.0 - peak)) * PI * 0.5).pow(2.1)
        }
    }

    private fun finalize(
        buf: DoubleArray,
        targetPeak: Double,
        fadeInMs: Double,
        fadeOutMs: Double,
    ): ShortArray {
        val n = buf.size
        if (n == 0) return ShortArray(0)
        var sum = 0.0
        for (x in buf) sum += x
        val mean = sum / n
        for (i in 0 until n) buf[i] -= mean

        val sr = SAMPLE_RATE.toDouble()
        val fi = min(n, max(1, (sr * fadeInMs / 1000.0).toInt()))
        val fo = min(n, max(1, (sr * fadeOutMs / 1000.0).toInt()))
        for (i in 0 until fi) {
            val u = i.toDouble() / fi
            buf[i] *= 0.5 - 0.5 * cos(PI * u)
        }
        for (i in 0 until fo) {
            val u = i.toDouble() / fo
            buf[n - 1 - i] *= 0.5 - 0.5 * cos(PI * u)
        }

        var peak = 0.0
        for (x in buf) {
            val a = abs(x)
            if (a > peak) peak = a
        }
        val gain = if (peak > 1e-6) targetPeak / peak else 1.0
        val pcm = ShortArray(n)
        for (i in 0 until n) {
            val clamped = (buf[i] * gain).coerceIn(-1.0, 1.0)
            pcm[i] = (clamped * Short.MAX_VALUE).toInt().toShort()
        }
        return pcm
    }

    private fun renderSamples(style: PageTurnSoundStyle, variant: Int): ShortArray {
        val sr = SAMPLE_RATE.toDouble()
        val pitchScale = doubleArrayOf(1.0, 0.965, 1.04)[variant % 3]
        val durScale = doubleArrayOf(1.0, 1.04, 0.96)[variant % 3]
        val styleIndex = style.ordinal.toLong()
        val rng = Rng(0x51A7E21CL + styleIndex * 7919L + variant.toLong() * 104729L)

        return when (style) {
            PageTurnSoundStyle.SOFT_PAPER -> {
                val dur = 0.225 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val p3 = Pink()
                val bpArc = Biquad(lp = false, freq = 600.0, q = 1.15)
                val lpArc = Biquad(lp = true, freq = 3900.0 * pitchScale, q = 0.707)
                val bpAir = Biquad(lp = false, freq = 280.0, q = 0.85)
                val bpSet = Biquad(lp = false, freq = 480.0, q = 1.05)
                val lpSet = Biquad(lp = true, freq = 2100.0 * pitchScale, q = 0.707)
                var brown = 0.0
                val buf = DoubleArray(n)
                for (i in 0 until n) {
                    val u = i.toDouble() / n
                    if ((i and 15) == 0) {
                        val sweep = 520.0 + 1040.0 * sin(PI * min(1.0, u * 1.12)).pow(1.3)
                        bpArc.set(sweep * pitchScale, 1.15)
                        val airF = (220.0 + 260.0 * sin(PI * u)) * pitchScale
                        bpAir.set(airF, 0.85)
                        val setF = (380.0 + 260.0 * max(0.0, (u - 0.5) * 2.0)) * pitchScale
                        bpSet.set(setF, 1.05)
                    }
                    val w1 = rng.nextSigned()
                    val w2 = rng.nextSigned()
                    val w3 = rng.nextSigned()
                    val pk1 = p1.next(w1)
                    brown = 0.96 * brown + 0.08 * w2
                    val pk3 = p3.next(w3)

                    val envArc = arch(u, 0.38)
                    val envAir = arch(u, 0.32)
                    val envSet = gauss(u, 0.72, 0.13)

                    val l1 = lpArc.run(bpArc.run(pk1)) * envArc * 1.15
                    val l2 = bpAir.run(brown) * envAir * 0.85
                    val l3 = lpSet.run(bpSet.run(pk3)) * envSet * 0.42
                    buf[i] = l1 + l2 + l3
                }
                finalize(buf, targetPeak = 0.52, fadeInMs = 6.0, fadeOutMs = 18.0)
            }

            PageTurnSoundStyle.CRISP_BOOK -> {
                val dur = 0.185 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val p2 = Pink()
                val p3 = Pink()
                val bpFlick = Biquad(lp = false, freq = 2100.0 * pitchScale, q = 1.6)
                val bpArc = Biquad(lp = false, freq = 1200.0, q = 1.25)
                val lpArc = Biquad(lp = true, freq = 5000.0 * pitchScale, q = 0.707)
                val bpCrackle = Biquad(lp = false, freq = 2400.0 * pitchScale, q = 1.9)
                val bpSet = Biquad(lp = false, freq = 640.0 * pitchScale, q = 1.35)
                val lpSet = Biquad(lp = true, freq = 2800.0 * pitchScale, q = 0.707)
                val buf = DoubleArray(n)
                for (i in 0 until n) {
                    val u = i.toDouble() / n
                    val t = i.toDouble() / sr
                    if ((i and 15) == 0) {
                        val sweep = 880.0 + 1750.0 * sin(PI * min(1.0, u * 1.15)).pow(1.15)
                        bpArc.set(sweep * pitchScale, 1.25)
                    }
                    val w1 = rng.nextSigned()
                    val w2 = rng.nextSigned()
                    val w3 = rng.nextSigned()
                    val pk1 = p1.next(w1)
                    val pk2 = p2.next(w2)
                    val pk3 = p3.next(w3)

                    val envFlick = gauss(t, 0.015, 0.0075)
                    val envArc = arch(u, 0.40)
                    val envSet = gauss(u, 0.76, 0.095)

                    val imp = if (rng.nextUnit() < 0.004) w2 * 0.7 else 0.0
                    val crackle = bpCrackle.run(imp) * envArc * 0.35

                    val l1 = bpFlick.run(pk2) * envFlick * 0.42
                    val l2 = lpArc.run(bpArc.run(pk1)) * envArc * 1.10
                    val l3 = lpSet.run(bpSet.run(pk3)) * envSet * 0.50
                    buf[i] = l1 + l2 + l3 + crackle
                }
                finalize(buf, targetPeak = 0.54, fadeInMs = 3.0, fadeOutMs = 14.0)
            }

            PageTurnSoundStyle.RICE_PAPER -> {
                val dur = 0.275 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val p2 = Pink()
                val bpBody = Biquad(lp = false, freq = 420.0, q = 0.95)
                val lpBody = Biquad(lp = true, freq = 2000.0 * pitchScale, q = 0.65)
                val lpAir = Biquad(lp = true, freq = 460.0 * pitchScale, q = 0.707)
                val bpFiber = Biquad(lp = false, freq = 1050.0, q = 1.15)
                val lpFiber = Biquad(lp = true, freq = 2100.0 * pitchScale, q = 0.707)
                var brown1 = 0.0
                var brown2 = 0.0
                val buf = DoubleArray(n)
                for (i in 0 until n) {
                    val u = i.toDouble() / n
                    if ((i and 15) == 0) {
                        val sweep = 310.0 + 530.0 * sin(PI * u).pow(1.1)
                        bpBody.set(sweep * pitchScale, 0.95)
                        val fibF = (1120.0 - 420.0 * u) * pitchScale
                        bpFiber.set(fibF, 1.15)
                    }
                    val w1 = rng.nextSigned()
                    val w2 = rng.nextSigned()
                    val pk1 = p1.next(w1)
                    val pk2 = p2.next(w2)
                    brown1 = 0.94 * brown1 + 0.12 * w1
                    brown2 = 0.975 * brown2 + 0.06 * w2

                    val envBody = 0.72 * arch(u, 0.35) + 0.48 * gauss(u, 0.68, 0.16)
                    val envAir = arch(u, 0.45)
                    val envFib = gauss(u, 0.52, 0.22)

                    val l1 = lpBody.run(bpBody.run(0.55 * pk1 + 0.45 * brown1)) * envBody * 1.2
                    val l2 = lpAir.run(brown2) * envAir * 0.9
                    val l3 = lpFiber.run(bpFiber.run(pk2)) * envFib * 0.24
                    buf[i] = l1 + l2 + l3
                }
                finalize(buf, targetPeak = 0.48, fadeInMs = 10.0, fadeOutMs = 26.0)
            }

            PageTurnSoundStyle.HARDCOVER -> {
                val dur = 0.260 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val p2 = Pink()
                val bpArc = Biquad(lp = false, freq = 650.0, q = 1.1)
                val lpArc = Biquad(lp = true, freq = 3700.0 * pitchScale, q = 0.707)
                val bpFlex = Biquad(lp = false, freq = 220.0, q = 1.05)
                val bpLand = Biquad(lp = false, freq = 420.0 * pitchScale, q = 1.2)
                val lpLand = Biquad(lp = true, freq = 1600.0 * pitchScale, q = 0.707)
                var brown = 0.0
                val buf = DoubleArray(n)
                val thumpT0 = dur * 0.73
                for (i in 0 until n) {
                    val u = i.toDouble() / n
                    val t = i.toDouble() / sr
                    if ((i and 15) == 0) {
                        val sweep = 420.0 + 880.0 * sin(PI * min(1.0, u * 1.1)).pow(1.25)
                        bpArc.set(sweep * pitchScale, 1.1)
                        val flexF = (165.0 + 185.0 * sin(PI * u)) * pitchScale
                        bpFlex.set(flexF, 1.05)
                    }
                    val w1 = rng.nextSigned()
                    val w2 = rng.nextSigned()
                    val pk1 = p1.next(w1)
                    val pk2 = p2.next(w2)
                    brown = 0.965 * brown + 0.08 * w2

                    val envArc = arch(u, 0.38)
                    val envFlex = arch(u, 0.34)
                    val envLand = gauss(u, 0.75, 0.095)

                    var thump = 0.0
                    val dt = t - thumpT0
                    if (dt > 0.0) {
                        val thEnv = smoothstep(dt / 0.007) * exp(-dt / 0.026)
                        val f1 = 132.0 * pitchScale
                        val f2 = 258.0 * pitchScale
                        thump = (0.72 * sin(2.0 * PI * f1 * dt) + 0.28 * sin(2.0 * PI * f2 * dt)) * thEnv * 0.16
                    }

                    val l1 = lpArc.run(bpArc.run(pk1)) * envArc * 1.05
                    val l2 = bpFlex.run(brown) * envFlex * 1.15
                    val l3 = lpLand.run(bpLand.run(pk2)) * envLand * 0.55
                    buf[i] = l1 + l2 + l3 + thump
                }
                finalize(buf, targetPeak = 0.58, fadeInMs = 5.0, fadeOutMs = 20.0)
            }

            PageTurnSoundStyle.WOOD_CLICK -> {
                val dur = 0.110 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val bpHit = Biquad(lp = false, freq = 780.0 * pitchScale, q = 1.1)
                val lpHit = Biquad(lp = true, freq = 1450.0 * pitchScale, q = 0.707)
                val buf = DoubleArray(n)
                var ph1 = 0.0
                var ph2 = 0.0
                var ph3 = 0.0
                for (i in 0 until n) {
                    val t = i.toDouble() / sr
                    val bend = 1.0 + 0.06 * exp(-t / 0.004)
                    val f1 = 460.0 * pitchScale * bend
                    val f2 = 1035.0 * pitchScale * bend
                    val f3 = 1920.0 * pitchScale * bend
                    ph1 += 2.0 * PI * f1 / sr
                    ph2 += 2.0 * PI * f2 / sr
                    ph3 += 2.0 * PI * f3 / sr

                    val atk = smoothstep(t / 0.0025)
                    val m1 = 0.65 * sin(ph1) * exp(-t / 0.017)
                    val m2 = 0.28 * sin(ph2) * exp(-t / 0.009)
                    val m3 = 0.09 * sin(ph3) * exp(-t / 0.0045)

                    val w = rng.nextSigned()
                    val hit = lpHit.run(bpHit.run(p1.next(w))) * gauss(t, 0.003, 0.0025) * 0.55
                    buf[i] = (m1 + m2 + m3) * atk + hit
                }
                finalize(buf, targetPeak = 0.46, fadeInMs = 2.0, fadeOutMs = 12.0)
            }

            PageTurnSoundStyle.INK_DROP -> {
                val dur = 0.145 * durScale
                val n = (sr * dur).toInt()
                val p1 = Pink()
                val bpPlip = Biquad(lp = false, freq = 540.0 * pitchScale, q = 1.4)
                val buf = DoubleArray(n)
                var ph = 0.0
                var ph2 = 0.0
                val t0 = 0.008
                for (i in 0 until n) {
                    val t = i.toDouble() / sr
                    val w = rng.nextSigned()
                    val plip = bpPlip.run(p1.next(w)) * gauss(t, 0.006, 0.0035) * 0.18
                    var drop = 0.0
                    val dt = t - t0
                    if (dt > 0.0) {
                        val f = (620.0 + 440.0 * (1.0 - exp(-dt / 0.030))) * pitchScale
                        ph += 2.0 * PI * f / sr
                        ph2 += 2.0 * PI * (f * 2.0) / sr
                        val env = smoothstep(dt / 0.006) * exp(-dt / 0.030)
                        val env2 = smoothstep(dt / 0.005) * exp(-dt / 0.014)
                        drop = sin(ph) * env + 0.11 * sin(ph2) * env2
                    }
                    buf[i] = plip + drop
                }
                finalize(buf, targetPeak = 0.44, fadeInMs = 2.0, fadeOutMs = 16.0)
            }
        }
    }

    fun encodeWav(pcm: ShortArray, sampleRate: Int): ByteArray {
        val dataBytes = pcm.size * 2
        val buf = ByteBuffer.allocate(44 + dataBytes).order(ByteOrder.LITTLE_ENDIAN)
        buf.put("RIFF".toByteArray())
        buf.putInt(36 + dataBytes)
        buf.put("WAVE".toByteArray())
        buf.put("fmt ".toByteArray())
        buf.putInt(16)
        buf.putShort(1) // PCM
        buf.putShort(1) // mono
        buf.putInt(sampleRate)
        buf.putInt(sampleRate * 2)
        buf.putShort(2)
        buf.putShort(16)
        buf.put("data".toByteArray())
        buf.putInt(dataBytes)
        for (s in pcm) buf.putShort(s)
        return buf.array()
    }
}

private fun durationLabel(ms: Int): String = t("%@ 秒", "%.2f".format(max(10, ms) / 1000.0))

private data class SoundGridEntry(
    val key: String,
    val title: String,
    val subtitle: String?,
    val icon: ImageVector,
    val isAdd: Boolean = false,
)

/** Shared 3-column sound preset + custom sound grid with inline volume slider & custom sheet. */
@Composable
fun PageTurnSoundStyleGrid(
    selectedRaw: String,
    onSelectKey: (String) -> Unit,
    showSubtitle: Boolean = false,
    compact: Boolean = false,
    showVolume: Boolean = true,
    insetBg: Color = YY.c.inset,
    ink: Color = YY.c.t1,
    ink2: Color = YY.c.t2,
    borderColor: Color = YY.c.line,
    eink: Boolean = EInk.on,
) {
    val context = LocalContext.current
    val customVersion = CustomPageTurnSounds.version
    val customs = remember(customVersion) { CustomPageTurnSounds.list(context) }
    var volume by rememberPref(PageTurnPrefs.SOUND_VOLUME_KEY, PageTurnPrefs.DEFAULT_VOLUME)
    var customSheetShown by remember { mutableStateOf(false) }

    val activeKey = if (customs.any { it.selectionKey == selectedRaw }) selectedRaw else PageTurnSoundStyle.of(selectedRaw).key
    val accent = if (eink) ink else YY.tint.accent
    val accentBg = if (eink) insetBg else YY.tint.accentSoft
    val shape = RoundedCornerShape(if (compact) 8.dp else 10.dp)
    val tileHeight = if (showSubtitle) 44.dp else if (compact) 30.dp else 34.dp

    val entries = remember(customs, showSubtitle) {
        buildList {
            for (st in PageTurnSoundStyle.entries) {
                add(SoundGridEntry(st.key, st.title(), if (showSubtitle) st.subtitle() else null, st.icon()))
            }
            for (c in customs) {
                add(SoundGridEntry(c.selectionKey, c.name, if (showSubtitle) durationLabel(c.durationMs) else null, Icons.waveform))
            }
            add(SoundGridEntry("__add__", t("自定义"), if (showSubtitle) t("导入或实录") else null, Icons.add, isAdd = true))
        }
    }

    Column(verticalArrangement = Arrangement.spacedBy(if (compact) 8.dp else 10.dp)) {
        Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
            for (row in entries.chunked(3)) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    for (entry in row) {
                        val on = !entry.isAdd && activeKey == entry.key
                        Box(
                            modifier = Modifier
                                .weight(1f)
                                .height(tileHeight)
                                .clip(shape)
                                .background(
                                    when {
                                        entry.isAdd -> insetBg.copy(alpha = 0.6f)
                                        on -> accentBg
                                        else -> insetBg
                                    }
                                )
                                .border(
                                    width = if (eink && on) 1.dp else 0.5.dp,
                                    color = when {
                                        entry.isAdd -> ink2.copy(alpha = if (eink) 0.8f else 0.35f)
                                        on -> if (eink) ink else accent.copy(alpha = 0.4f)
                                        else -> if (eink) borderColor else Color.Transparent
                                    },
                                    shape = shape,
                                )
                                .pressable(scale = 0.96f, label = entry.title) {
                                    if (entry.isAdd) {
                                        customSheetShown = true
                                    } else {
                                        onSelectKey(entry.key)
                                        PageTurnSound.previewSelection(context, entry.key)
                                    }
                                }
                                .padding(horizontal = 6.dp),
                            contentAlignment = Alignment.Center,
                        ) {
                            Column(
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.spacedBy(if (showSubtitle) 1.dp else 0.dp),
                            ) {
                                Row(
                                    verticalAlignment = Alignment.CenterVertically,
                                    horizontalArrangement = Arrangement.spacedBy(4.dp),
                                ) {
                                    Icon(
                                        entry.icon,
                                        contentDescription = null,
                                        tint = if (on) accent else ink2,
                                        modifier = Modifier.size(if (compact) 11.dp else 12.dp),
                                    )
                                    Text(
                                        entry.title,
                                        fontSize = if (compact) 11.sp else 12.sp,
                                        fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium,
                                        color = if (entry.isAdd) ink2 else if (on) (if (eink) ink else accent) else ink,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                }
                                if (entry.subtitle != null) {
                                    Text(
                                        entry.subtitle,
                                        fontSize = 9.5.sp,
                                        color = if (on) (if (eink) ink2 else accent.copy(alpha = 0.85f)) else ink2,
                                        maxLines = 1,
                                        overflow = TextOverflow.Ellipsis,
                                    )
                                }
                            }
                        }
                    }
                    repeat(3 - row.size) {
                        Box(Modifier.weight(1f))
                    }
                }
            }
        }

        if (showVolume) {
            val clamped = volume.coerceIn(PageTurnPrefs.MIN_VOLUME, PageTurnPrefs.MAX_VOLUME)
            val pct = (clamped * 100f).roundToInt()
            Row(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 2.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Icon(Icons.speakerWave, t("音效音量"), tint = ink2, modifier = Modifier.size(14.dp))
                Slider(
                    value = clamped,
                    onValueChange = { volume = it },
                    onValueChangeFinished = {
                        PageTurnSound.previewSelection(context, activeKey, volume)
                    },
                    valueRange = PageTurnPrefs.MIN_VOLUME..PageTurnPrefs.MAX_VOLUME,
                    modifier = Modifier.weight(1f).height(26.dp),
                    colors = SliderDefaults.colors(
                        thumbColor = if (eink) ink else YY.tint.fill,
                        activeTrackColor = if (eink) ink else YY.tint.fill,
                        inactiveTrackColor = insetBg,
                    ),
                )
                Text(
                    "$pct%",
                    fontSize = 10.5.sp,
                    fontWeight = FontWeight.Medium,
                    color = ink2,
                    style = YY.tnum,
                    textAlign = TextAlign.End,
                    modifier = Modifier.width(34.dp),
                )
            }
        }
    }

    if (customSheetShown) {
        CustomPageTurnSoundSheet(
            selectedRaw = activeKey,
            onSelectKey = onSelectKey,
            dismiss = { customSheetShown = false },
        )
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CustomPageTurnSoundSheet(
    selectedRaw: String,
    onSelectKey: (String) -> Unit,
    dismiss: () -> Unit,
) {
    val context = LocalContext.current
    val focusManager = LocalFocusManager.current
    val view = LocalView.current
    val scope = rememberCoroutineScope()
    val customVersion = CustomPageTurnSounds.version
    val customs = remember(customVersion) { CustomPageTurnSounds.list(context) }

    var recording by remember { mutableStateOf(false) }
    var recordSeconds by remember { mutableFloatStateOf(0f) }
    var recordLevel by remember { mutableFloatStateOf(0f) }
    val recordLevels = remember { mutableStateListOf<Float>() }
    var recordJob by remember { mutableStateOf<Job?>(null) }
    var processing by remember { mutableStateOf(false) }
    var draft by remember { mutableStateOf<ProcessedCustomSound?>(null) }
    var draftFromFile by remember { mutableStateOf(false) }
    var draftName by remember { mutableStateOf("") }
    var flipQuoteIndex by remember { mutableIntStateOf(0) }
    var pageFlipping by remember { mutableStateOf(false) }
    var playingDraft by remember { mutableStateOf(false) }
    var playJob by remember { mutableStateOf<Job?>(null) }
    val playProgress = remember { Animatable(0f) }
    var errorMsg by remember { mutableStateOf<String?>(null) }

    val idleWaveHeights = remember {
        floatArrayOf(
            0.16f, 0.22f, 0.30f, 0.24f, 0.38f, 0.52f, 0.44f, 0.64f,
            0.78f, 0.58f, 0.88f, 0.96f, 0.84f, 0.66f, 0.74f, 0.54f,
            0.46f, 0.36f, 0.42f, 0.28f, 0.32f, 0.22f, 0.20f, 0.16f,
        )
    }
    val previewQuotes = remember {
        listOf(
            t("轻点或轻扫这一页，听听你刚录下的翻书声。"),
            t("纸页轻轻掠过指尖，声音恰到好处，不惊扰夜读。"),
            t("再翻一页试试节奏，满意就可以直接保存使用。"),
        )
    }

    fun flipAndAudition(item: ProcessedCustomSound, animatePage: Boolean = true) {
        playJob?.cancel()
        PageTurnSound.previewWavBytes(item.wavData)
        if (animatePage) {
            view.performHapticFeedback(HapticFeedbackConstants.CLOCK_TICK)
        }
        playJob = scope.launch {
            playingDraft = true
            playProgress.snapTo(0f)
            if (animatePage) {
                pageFlipping = true
                launch {
                    delay(190)
                    flipQuoteIndex++
                    pageFlipping = false
                }
            }
            playProgress.animateTo(
                targetValue = 1f,
                animationSpec = tween(durationMillis = max(120, item.durationMs), easing = LinearEasing),
            )
            delay(50)
            playingDraft = false
            playProgress.snapTo(0f)
        }
    }

    fun saveCurrentDraft(item: ProcessedCustomSound) {
        focusManager.clearFocus()
        runCatching { CustomPageTurnSounds.save(context, item, draftName) }
            .onSuccess { saved ->
                onSelectKey(saved.selectionKey)
                Prefs.set(PageTurnPrefs.SOUND_KEY, true)
                PageTurnSound.previewSelection(context, saved.selectionKey)
                dismiss()
            }
            .onFailure { e ->
                errorMsg = (e as? CustomSoundException)?.localizedMessageText() ?: t("保存自定义音效失败，请重试")
            }
    }

    fun finishRecordedSamples(samples: FloatArray) {
        processing = true
        scope.launch {
            val res = withContext(Dispatchers.Default) {
                runCatching { CustomPageTurnSounds.trimAndNormalize(samples, PageTurnSound.SAMPLE_RATE) }
            }
            processing = false
            res.onSuccess { processed ->
                draft = processed
                draftFromFile = false
                draftName = t("手边实录")
                flipQuoteIndex = 0
                pageFlipping = false
                flipAndAudition(processed, animatePage = false)
            }.onFailure { e ->
                errorMsg = (e as? CustomSoundException)?.localizedMessageText() ?: t("没有检测到明显声音，再试一次")
            }
        }
    }

    fun startRecording() {
        errorMsg = null
        val minBuf = AudioRecord.getMinBufferSize(
            PageTurnSound.SAMPLE_RATE,
            AudioFormat.CHANNEL_IN_MONO,
            AudioFormat.ENCODING_PCM_16BIT,
        ).coerceAtLeast(4096)
        val rec = try {
            AudioRecord(
                MediaRecorder.AudioSource.MIC,
                PageTurnSound.SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT,
                minBuf * 2,
            )
        } catch (_: SecurityException) {
            errorMsg = t("请在系统设置中允许语阅使用麦克风，或直接从文件导入音频")
            return
        }
        if (rec.state != AudioRecord.STATE_INITIALIZED) {
            rec.release()
            errorMsg = t("无法启动录音，请重试")
            return
        }
        recording = true
        recordSeconds = 0f
        recordLevel = 0.08f
        recordLevels.clear()
        val maxSamples = (PageTurnSound.SAMPLE_RATE * 2.5).toInt()
        val captured = FloatArray(maxSamples)
        var written = 0
        var heardPeak = false
        var quietAfterPeak = 0.0
        recordJob = scope.launch(Dispatchers.IO) {
            val chunk = ShortArray(1024)
            try {
                rec.startRecording()
                while (isActive && recording && written < maxSamples) {
                    val read = rec.read(chunk, 0, min(chunk.size, maxSamples - written))
                    if (read > 0) {
                        var energy = 0.0
                        for (i in 0 until read) {
                            val f = chunk[i] / 32768f
                            captured[written++] = f
                            energy += f * f
                        }
                        val rms = sqrt(energy / read).toFloat()
                        val norm = (rms * 5.5f).coerceIn(0.06f, 1f)
                        val chunkSec = read.toDouble() / PageTurnSound.SAMPLE_RATE
                        val sec = written.toFloat() / PageTurnSound.SAMPLE_RATE
                        if (sec >= 0.10f && norm >= 0.28f) {
                            heardPeak = true
                            quietAfterPeak = 0.0
                        } else if (heardPeak) {
                            if (norm < 0.14f) quietAfterPeak += chunkSec
                            else if (norm >= 0.18f) quietAfterPeak = 0.0
                        }
                        withContext(Dispatchers.Main) {
                            recordSeconds = sec
                            recordLevel = norm
                            recordLevels.add(norm)
                            if (recordLevels.size > 24) recordLevels.removeAt(0)
                        }
                        if (heardPeak && sec >= 0.45f && quietAfterPeak >= 0.55) {
                            break
                        }
                    }
                }
            } finally {
                runCatching { rec.stop() }
                rec.release()
                val slice = captured.copyOf(written)
                withContext(Dispatchers.Main) {
                    recording = false
                    recordLevel = 0f
                    if (slice.size > 400) finishRecordedSamples(slice)
                }
            }
        }
    }

    val micPermLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        if (granted) startRecording()
        else errorMsg = t("请在系统设置中允许语阅使用麦克风，或直接从文件导入音频")
    }

    val audioPicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        errorMsg = null
        processing = true
        val fallbackName = CustomPageTurnSounds.displayNameForUri(context, uri)
        scope.launch {
            val res = withContext(Dispatchers.IO) {
                runCatching { CustomPageTurnSounds.decodeAndProcessUri(context, uri) }
            }
            processing = false
            res.onSuccess { processed ->
                draft = processed
                draftFromFile = true
                draftName = fallbackName
                flipQuoteIndex = 0
                pageFlipping = false
                flipAndAudition(processed, animatePage = false)
            }.onFailure { e ->
                errorMsg = (e as? CustomSoundException)?.localizedMessageText() ?: t("这段音频读不出来，换一个试试")
            }
        }
    }

    DisposableEffect(Unit) {
        onDispose {
            recording = false
            recordJob?.cancel()
            playJob?.cancel()
        }
    }

    ModalBottomSheet(
        onDismissRequest = {
            recording = false
            recordJob?.cancel()
            playJob?.cancel()
            dismiss()
        },
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
        dragHandle = { YYSheetDragHandle() },
    ) {
        Column(modifier = Modifier.fillMaxWidth()) {
            val currentDraft = draft
            if (currentDraft != null) {
                CustomPageTurnDraftHeader(
                    draftFromFile = draftFromFile,
                    onReset = {
                        focusManager.clearFocus()
                        playJob?.cancel()
                        playingDraft = false
                        pageFlipping = false
                        draft = null
                        errorMsg = null
                    },
                    onSave = { saveCurrentDraft(currentDraft) },
                )
            } else {
                CustomPageTurnCaptureHeader(
                    onClose = {
                        recording = false
                        recordJob?.cancel()
                        playJob?.cancel()
                        dismiss()
                    },
                )
            }

            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f, fill = false)
                    .verticalScroll(rememberScrollState())
                    .padding(horizontal = 18.dp)
                    .padding(bottom = 26.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                errorMsg?.let { msg -> CustomPageTurnErrorBanner(msg) }

                if (currentDraft != null) {
                    CustomPageTurnDraftContent(
                        currentDraft = currentDraft,
                        draftFromFile = draftFromFile,
                        draftName = draftName,
                        onDraftNameChange = { draftName = it },
                        previewQuotes = previewQuotes,
                        flipQuoteIndex = flipQuoteIndex,
                        pageFlipping = pageFlipping,
                        playingDraft = playingDraft,
                        playProgress = playProgress.value,
                        onClearFocus = { focusManager.clearFocus() },
                        onFlipAndAudition = { flipAndAudition(currentDraft) },
                    )
                } else {
                    CustomPageTurnCaptureStudio(
                        recording = recording,
                        processing = processing,
                        recordSeconds = recordSeconds,
                        recordLevel = recordLevel,
                        recordLevels = recordLevels,
                        idleWaveHeights = idleWaveHeights,
                        onToggleRecord = {
                            if (recording) {
                                recording = false
                            } else {
                                val granted = ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED
                                if (granted) startRecording() else micPermLauncher.launch(Manifest.permission.RECORD_AUDIO)
                            }
                        },
                        onImportFile = {
                            errorMsg = null
                            audioPicker.launch(arrayOf("audio/*"))
                        },
                    )

                    if (customs.isNotEmpty()) {
                        CustomPageTurnSavedSoundsSection(
                            customs = customs,
                            selectedRaw = selectedRaw,
                            onSelect = { item ->
                                onSelectKey(item.selectionKey)
                                PageTurnSound.previewSelection(context, item.selectionKey)
                            },
                            onDelete = { item ->
                                CustomPageTurnSounds.delete(context, item.id)
                                if (selectedRaw == item.selectionKey) {
                                    onSelectKey(PageTurnSoundStyle.SOFT_PAPER.key)
                                }
                            },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun CustomPageTurnDraftHeader(
    draftFromFile: Boolean,
    onReset: () -> Unit,
    onSave: () -> Unit,
) {
    // Clean 3-part header: ‹ 重录 · 自定义翻页音效 · 保存
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 18.dp)
            .padding(bottom = 14.dp),
    ) {
        Row(
            modifier = Modifier
                .align(Alignment.CenterStart)
                .heightIn(min = 32.dp)
                .clip(RoundedCornerShape(8.dp))
                .clickable(onClick = onReset)
                .padding(end = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(3.dp),
        ) {
            Icon(Icons.back, null, tint = YY.c.t2, modifier = Modifier.size(13.dp))
            Text(
                if (draftFromFile) t("重选") else t("重录"),
                fontSize = 13.5.sp,
                fontWeight = FontWeight.Medium,
                color = YY.c.t2,
            )
        }

        Text(
            t("自定义翻页音效"),
            fontSize = 15.5.sp,
            fontWeight = FontWeight.SemiBold,
            color = YY.c.t1,
            modifier = Modifier.align(Alignment.Center),
        )

        Text(
            t("保存"),
            fontSize = 14.sp,
            fontWeight = FontWeight.SemiBold,
            color = YY.tint.accent,
            modifier = Modifier
                .align(Alignment.CenterEnd)
                .clip(RoundedCornerShape(8.dp))
                .pressable(scale = 0.94f, onClick = onSave)
                .padding(horizontal = 6.dp, vertical = 5.dp),
        )
    }
}

@Composable
private fun CustomPageTurnCaptureHeader(onClose: () -> Unit) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 18.dp)
            .padding(bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(t("自定义翻页音效"), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
        Box(
            Modifier
                .size(30.dp)
                .clip(CircleShape)
                .background(YY.c.inset)
                .pressable(scale = 0.92f, label = t("关闭"), onClick = onClose),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.close, t("关闭"), tint = YY.c.t2, modifier = Modifier.size(13.dp))
        }
    }
}

@Composable
private fun CustomPageTurnErrorBanner(message: String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(YY.c.danger.copy(alpha = 0.09f))
            .border(0.5.dp, YY.c.danger.copy(alpha = 0.22f), RoundedCornerShape(12.dp))
            .padding(horizontal = 12.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        Icon(Icons.warning, null, tint = YY.c.danger, modifier = Modifier.size(15.dp))
        Text(message, fontSize = 12.sp, color = YY.c.t1)
    }
}

@Composable
private fun CustomPageTurnDraftContent(
    currentDraft: ProcessedCustomSound,
    draftFromFile: Boolean,
    draftName: String,
    onDraftNameChange: (String) -> Unit,
    previewQuotes: List<String>,
    flipQuoteIndex: Int,
    pageFlipping: Boolean,
    playingDraft: Boolean,
    playProgress: Float,
    onClearFocus: () -> Unit,
    onFlipAndAudition: () -> Unit,
) {
    val isDark = YY.dark
    val paperBg = if (isDark) Color(0xFF26231F) else Color(0xFFF4EFE6)
    val paperEdge = if (isDark) Color(0xFF38332C) else Color(0xFFE5DEC9)
    val paperInk = if (isDark) Color(0xFFEAE4DA) else Color(0xFF2C2925)
    val currentQuote = previewQuotes[flipQuoteIndex % previewQuotes.size]
    val nextQuote = previewQuotes[(flipQuoteIndex + 1) % previewQuotes.size]
    val rotY by animateFloatAsState(
        targetValue = if (pageFlipping) -22f else 0f,
        animationSpec = spring(dampingRatio = 0.85f, stiffness = 420f),
        label = "pageFlipRotY",
    )
    val shiftX by animateFloatAsState(
        targetValue = if (pageFlipping) -10f else 0f,
        animationSpec = spring(dampingRatio = 0.85f, stiffness = 420f),
        label = "pageFlipShiftX",
    )

    // Style 3: Miniature stacked flippable book page
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .pointerInput(currentDraft) {
                detectTapGestures {
                    onClearFocus()
                    onFlipAndAudition()
                }
            }
            .pointerInput(currentDraft) {
                var dragTotal = 0f
                detectHorizontalDragGestures(
                    onDragStart = { dragTotal = 0f },
                    onHorizontalDrag = { _, dragAmount -> dragTotal += dragAmount },
                    onDragEnd = {
                        if (dragTotal < -36f) {
                            onClearFocus()
                            onFlipAndAudition()
                        }
                    },
                )
            },
    ) {
        // Underlying next page peek
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(top = 5.dp, start = 7.dp)
                .heightIn(min = 172.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(paperBg.copy(alpha = 0.92f))
                .border(0.8.dp, paperEdge, RoundedCornerShape(16.dp))
                .padding(horizontal = 18.dp, vertical = 16.dp),
            contentAlignment = Alignment.CenterStart,
        ) {
            Text(
                nextQuote,
                fontSize = 14.sp,
                lineHeight = 24.sp,
                fontFamily = FontFamily.Serif,
                color = paperInk.copy(alpha = 0.32f),
                maxLines = 2,
            )
        }

        // Front flippable book page
        Box(
            modifier = Modifier
                .fillMaxWidth()
                .padding(end = 7.dp, bottom = 5.dp)
                .graphicsLayer {
                    rotationY = rotY
                    translationX = shiftX
                    transformOrigin = TransformOrigin(0f, 0.5f)
                    cameraDistance = 14f * density
                }
                .heightIn(min = 172.dp)
                .clip(RoundedCornerShape(16.dp))
                .background(paperBg)
                .border(0.8.dp, paperEdge, RoundedCornerShape(16.dp)),
        ) {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 18.dp, vertical = 16.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
            ) {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Box(Modifier.size(6.dp).clip(CircleShape).background(YY.tint.fill))
                    Spacer(Modifier.width(6.dp))
                    Text(
                        if (draftFromFile) t("导入音频") else t("手边实录"),
                        fontSize = 11.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = YY.tint.accent,
                    )
                    Spacer(Modifier.weight(1f))
                    Text(
                        durationLabel(currentDraft.durationMs),
                        fontSize = 11.5.sp,
                        color = YY.c.t3,
                        style = YY.tnum,
                    )
                }

                Text(
                    currentQuote,
                    fontSize = 15.sp,
                    lineHeight = 25.sp,
                    fontFamily = FontFamily.Serif,
                    color = paperInk,
                    modifier = Modifier.fillMaxWidth().heightIn(min = 52.dp),
                )

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.SpaceBetween,
                ) {
                    MiniPageWaveform(
                        waveform = currentDraft.waveform,
                        trimStart = currentDraft.trimStart,
                        trimEnd = currentDraft.trimEnd,
                        playProgress = if (playingDraft) playProgress else null,
                        modifier = Modifier.width(118.dp).height(18.dp),
                    )

                    Row(
                        modifier = Modifier.padding(end = 14.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(3.dp),
                    ) {
                        Text(t("点此试翻"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent)
                        Icon(Icons.arrowRight, null, tint = YY.tint.accent, modifier = Modifier.size(11.dp))
                    }
                }
            }

            // Curled bottom-right paper corner
            val cornerAccent = YY.tint.accent
            Canvas(
                modifier = Modifier
                    .align(Alignment.BottomEnd)
                    .size(28.dp),
            ) {
                val path = Path().apply {
                    moveTo(0f, size.height)
                    lineTo(size.width, 0f)
                    lineTo(size.width, size.height)
                    close()
                }
                drawPath(
                    path = path,
                    brush = Brush.linearGradient(
                        colors = listOf(
                            cornerAccent.copy(alpha = if (isDark) 0.24f else 0.14f),
                            if (isDark) Color.White.copy(alpha = 0.08f) else Color.White.copy(alpha = 0.72f),
                        ),
                        start = Offset(0f, 0f),
                        end = Offset(size.width, size.height),
                    ),
                )
            }
        }
    }

    // Clean single-row editorial name field
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .heightIn(min = 46.dp)
            .clip(RoundedCornerShape(14.dp))
            .background(YY.c.card)
            .border(0.6.dp, YY.c.line, RoundedCornerShape(14.dp))
            .padding(horizontal = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(t("音效名"), fontSize = 12.5.sp, color = YY.c.t3)
        Box(modifier = Modifier.weight(1f), contentAlignment = Alignment.CenterStart) {
            if (draftName.isEmpty()) {
                Text(t("给音效起个名字"), fontSize = 14.sp, color = YY.c.t3)
            }
            BasicTextField(
                value = draftName,
                onValueChange = { onDraftNameChange(it.take(16)) },
                singleLine = true,
                textStyle = TextStyle(color = YY.c.t1, fontSize = 14.sp, fontWeight = FontWeight.SemiBold),
                cursorBrush = SolidColor(YY.tint.accent),
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
                keyboardActions = KeyboardActions(onDone = { onClearFocus() }),
                modifier = Modifier.fillMaxWidth(),
            )
        }
        if (draftName.isNotEmpty()) {
            Box(
                Modifier
                    .size(22.dp)
                    .clip(CircleShape)
                    .background(YY.c.inset)
                    .clickable { onDraftNameChange("") },
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.close, t("清空"), tint = YY.c.t3, modifier = Modifier.size(11.dp))
            }
        }
    }
}

@Composable
private fun CustomPageTurnCaptureStudio(
    recording: Boolean,
    processing: Boolean,
    recordSeconds: Float,
    recordLevel: Float,
    recordLevels: List<Float>,
    idleWaveHeights: FloatArray,
    onToggleRecord: () -> Unit,
    onImportFile: () -> Unit,
) {
    // State 1: Capture Studio
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(18.dp))
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(18.dp))
            .padding(vertical = 20.dp, horizontal = 16.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        // 24-bar live acoustic meter
        Row(
            modifier = Modifier.height(44.dp),
            horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            val count = 24
            for (i in 0 until count) {
                val h = if (recording) {
                    val offset = count - recordLevels.size
                    if (i < offset) 0.10f else recordLevels[i - offset]
                } else {
                    idleWaveHeights[i % idleWaveHeights.size]
                }
                val barColor = if (recording) {
                    val recency = (i + 1f) / count
                    YY.c.danger.copy(alpha = 0.32f + 0.68f * recency)
                } else {
                    YY.tint.accent.copy(alpha = 0.22f)
                }
                Box(
                    Modifier
                        .width(3.5.dp)
                        .height((h * 38f).coerceAtLeast(5f).dp)
                        .clip(CircleShape)
                        .background(barColor),
                )
            }
        }

        // Sculpted record button with circular progress sweep
        val progress = (recordSeconds / 2.5f).coerceIn(0f, 1f)
        val trackRingColor = if (recording) YY.c.danger.copy(alpha = 0.16f) else YY.tint.accent.copy(alpha = 0.16f)
        val dangerColor = YY.c.danger
        Box(
            modifier = Modifier
                .size(76.dp)
                .pressable(scale = 0.94f, enabled = !processing, label = if (recording) t("停止录音") else t("录一段翻书声"), onClick = onToggleRecord),
            contentAlignment = Alignment.Center,
        ) {
            Canvas(Modifier.fillMaxSize()) {
                val stroke = Stroke(width = 2.5.dp.toPx(), cap = StrokeCap.Round)
                val pad = 2.dp.toPx()
                val arcSize = Size(size.width - pad * 2, size.height - pad * 2)
                drawArc(
                    color = trackRingColor,
                    startAngle = -90f,
                    sweepAngle = 360f,
                    useCenter = false,
                    topLeft = Offset(pad, pad),
                    size = arcSize,
                    style = stroke,
                )
                if (recording) {
                    drawArc(
                        color = dangerColor,
                        startAngle = -90f,
                        sweepAngle = 360f * progress,
                        useCenter = false,
                        topLeft = Offset(pad, pad),
                        size = arcSize,
                        style = stroke,
                    )
                }
            }
            Box(
                modifier = Modifier
                    .size(60.dp)
                    .scale(if (recording) 1f + recordLevel * 0.07f else 1f)
                    .clip(CircleShape)
                    .background(if (recording) YY.c.danger else YY.tint.fill),
                contentAlignment = Alignment.Center,
            ) {
                when {
                    recording -> Box(
                        Modifier
                            .size(17.dp)
                            .clip(RoundedCornerShape(4.5.dp))
                            .background(Color.White),
                    )
                    processing -> CircularProgressIndicator(
                        modifier = Modifier.size(20.dp),
                        strokeWidth = 2.dp,
                        color = Color.White,
                    )
                    else -> Icon(Icons.microphoneFill, null, tint = Color.White, modifier = Modifier.size(23.dp))
                }
            }
        }

        Column(
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            when {
                recording -> {
                    Text(
                        t("正在收音 · %@ 秒", "%.1f".format(recordSeconds)),
                        fontSize = 13.5.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = YY.c.danger,
                        style = YY.tnum,
                    )
                    Text(t("翻完停顿片刻自动完成，或轻点停止"), fontSize = 11.sp, color = YY.c.t3)
                }
                processing -> Text(t("正在裁剪与调平音量…"), fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                else -> {
                    Text(t("翻一页真书，或轻敲桌面"), fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                    Text(
                        t("录完稍停自动截取 · 自动去静音并调平音量"),
                        fontSize = 11.sp,
                        color = YY.c.t3,
                        textAlign = TextAlign.Center,
                    )
                }
            }
        }
    }

    // Quiet local-only + file import bar
    Row(
        modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(t("只保存在本地，不上传"), fontSize = 11.sp, color = YY.c.t3)
        Row(
            modifier = Modifier
                .height(32.dp)
                .clip(CircleShape)
                .background(YY.tint.accentSoft)
                .pressable(scale = 0.96f, enabled = !recording && !processing, onClick = onImportFile)
                .padding(horizontal = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(5.dp),
        ) {
            Icon(Icons.folder, null, tint = YY.tint.accent, modifier = Modifier.size(13.dp))
            Text(t("从文件导入…"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent)
        }
    }
}

@Composable
private fun CustomPageTurnSavedSoundsSection(
    customs: List<CustomPageTurnSound>,
    selectedRaw: String,
    onSelect: (CustomPageTurnSound) -> Unit,
    onDelete: (CustomPageTurnSound) -> Unit,
) {
    Text(
        t("已保存的自定义音效"),
        fontSize = 11.5.sp,
        fontWeight = FontWeight.SemiBold,
        color = YY.c.t3,
        modifier = Modifier.padding(horizontal = 4.dp, vertical = 2.dp),
    )
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(14.dp))
            .background(YY.c.card)
            .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp))
            .padding(horizontal = 12.dp),
    ) {
        customs.forEachIndexed { idx, c ->
            if (idx > 0) Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
            val active = selectedRaw == c.selectionKey
            Row(
                modifier = Modifier.fillMaxWidth().heightIn(min = 44.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Row(
                    modifier = Modifier
                        .weight(1f)
                        .clickable { onSelect(c) }
                        .padding(vertical = 8.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Icon(
                        if (active) Icons.checkCircleFill else Icons.waveform,
                        null,
                        tint = if (active) YY.tint.accent else YY.c.t2,
                        modifier = Modifier.size(15.dp),
                    )
                    Text(
                        c.name,
                        fontSize = 12.5.sp,
                        fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                        color = YY.c.t1,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Text(durationLabel(c.durationMs), fontSize = 11.sp, color = YY.c.t3, style = YY.tnum)
                }
                Box(
                    Modifier
                        .size(32.dp)
                        .clip(CircleShape)
                        .clickable { onDelete(c) },
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.delete, t("删除"), tint = YY.c.t3, modifier = Modifier.size(15.dp))
                }
            }
        }
    }
}

@Composable
private fun MiniPageWaveform(
    waveform: FloatArray,
    trimStart: Double,
    trimEnd: Double,
    playProgress: Float?,
    modifier: Modifier = Modifier,
) {
    val accent = YY.tint.accent
    val bars = remember(waveform, trimStart, trimEnd) {
        val outCount = 28
        if (waveform.isEmpty()) {
            FloatArray(outCount) { 0.25f }
        } else {
            val n = waveform.size
            val s = (trimStart * n).toInt().coerceIn(0, n - 1)
            val e = (trimEnd * n).toInt().coerceIn(s + 1, n)
            val sliceLen = (e - s).coerceAtLeast(1)
            FloatArray(outCount) { i ->
                val idx = (s + (i * sliceLen) / outCount).coerceIn(0, n - 1)
                waveform[idx]
            }
        }
    }
    Canvas(modifier = modifier) {
        val count = bars.size
        val step = size.width / count
        val barW = (step * 0.48f).coerceIn(1.8.dp.toPx(), 2.4.dp.toPx())
        for (i in 0 until count) {
            val pos = (i + 0.5) / count
            val edge = sin(PI * pos)
            val amp = bars[i] * (0.42 + 0.58 * edge).toFloat()
            val bh = (amp * (size.height - 2.dp.toPx())).coerceAtLeast(3.dp.toPx())
            val lit = playProgress != null && pos <= playProgress.toDouble()
            val cx = ((i + 0.5f) * step)
            drawRoundRect(
                color = accent.copy(alpha = if (lit) 1f else 0.34f),
                topLeft = Offset(cx - barW * 0.5f, (size.height - bh) * 0.5f),
                size = Size(barW, bh),
                cornerRadius = CornerRadius(barW * 0.5f, barW * 0.5f),
            )
        }
    }
}
