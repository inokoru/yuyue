package app.yuyue.reader

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.math.PI
import kotlin.math.abs
import kotlin.math.sin

class PageTurnSoundTest {

    @Test
    fun allSoundStylesSynthesizeClean44kHzWavsWithDistinctVariants() {
        assertEquals(6, PageTurnSoundStyle.entries.size)
        assertEquals(PageTurnSoundStyle.SOFT_PAPER, PageTurnSoundStyle.of("unknown"))
        assertEquals(PageTurnSoundStyle.INK_DROP, PageTurnSoundStyle.of("inkDrop"))

        for (style in PageTurnSoundStyle.entries) {
            val variants = mutableListOf<ByteArray>()
            for (v in 0 until PageTurnSound.VARIANT_COUNT) {
                val wav = PageTurnSound.synthesizeWav(style, v)
                assertTrue("WAV too short for $style", wav.size > 44 + 4000)
                assertEquals("RIFF", String(wav, 0, 4))
                assertEquals("WAVE", String(wav, 8, 4))
                assertEquals("data", String(wav, 36, 4))

                val bb = ByteBuffer.wrap(wav).order(ByteOrder.LITTLE_ENDIAN)
                assertEquals(PageTurnSound.SAMPLE_RATE, bb.getInt(24))

                val sampleCount = (wav.size - 44) / 2
                var sum = 0.0
                var peak = 0
                for (i in 0 until sampleCount) {
                    val s = bb.getShort(44 + i * 2).toInt()
                    sum += s
                    val a = abs(s)
                    if (a > peak) peak = a
                }
                val first = abs(bb.getShort(44).toInt())
                val last = abs(bb.getShort(44 + (sampleCount - 1) * 2).toInt())
                assertTrue("Head click on $style: $first", first < 64)
                assertTrue("Tail click on $style: $last", last < 64)
                val mean = abs(sum / sampleCount) / Short.MAX_VALUE
                assertTrue("DC offset too large on $style: $mean", mean < 0.01)
                assertTrue("Peak out of comfortable range on $style: $peak", peak in 10001..24999)
                variants.add(wav)
            }
            assertNotEquals(variants[0].contentHashCode(), variants[1].contentHashCode())
            assertNotEquals(variants[1].contentHashCode(), variants[2].contentHashCode())
        }
    }

    @Test
    fun customSoundProcessorTrimsSilenceCapsDurationAndRejectsSilent() {
        val sr = PageTurnSound.SAMPLE_RATE

        // 1. 300 ms leading silence + 120 ms 700 Hz burst + 400 ms trailing silence → trimmed & normalized.
        val lead = (sr * 0.30).toInt()
        val burst = (sr * 0.12).toInt()
        val trail = (sr * 0.40).toInt()
        val padded = FloatArray(lead + burst + trail)
        for (i in 0 until burst) {
            val env = sin(PI * i / burst)
            padded[lead + i] = (0.18 * env * sin(2.0 * PI * 700.0 * i / sr)).toFloat()
        }
        val trimmed = CustomPageTurnSounds.trimAndNormalize(padded, sr)
        assertTrue("Unexpected trimmed duration: ${trimmed.durationMs}", trimmed.durationMs in 100..185)
        assertEquals(CustomPageTurnSounds.WAVEFORM_BAR_COUNT, trimmed.waveform.size)
        assertTrue(trimmed.trimStart >= 0.0 && trimmed.trimEnd <= 1.0 && trimmed.trimEnd > trimmed.trimStart)
        assertEquals("RIFF", String(trimmed.wavData, 0, 4))
        assertEquals("WAVE", String(trimmed.wavData, 8, 4))
        val bb = ByteBuffer.wrap(trimmed.wavData).order(ByteOrder.LITTLE_ENDIAN)
        val count = (trimmed.wavData.size - 44) / 2
        var peak = 0
        for (i in 0 until count) {
            peak = maxOf(peak, abs(bb.getShort(44 + i * 2).toInt()))
        }
        assertTrue("Head click: ${abs(bb.getShort(44).toInt())}", abs(bb.getShort(44).toInt()) < 64)
        assertTrue("Tail click: ${abs(bb.getShort(44 + (count - 1) * 2).toInt())}", abs(bb.getShort(44 + (count - 1) * 2).toInt()) < 64)
        assertTrue("Normalized peak out of range: $peak", peak in 17_000..22_000)

        // 1b. Interactive micro-trim re-slices cleanly inside the context window.
        val midEnd = (trimmed.trimStart + trimmed.trimEnd) * 0.5
        val sub = CustomPageTurnSounds.renderTrimmed(trimmed, trimmed.trimStart, midEnd)
        assertTrue("Expected sub-trim to succeed", sub != null)
        assertTrue(sub!!.durationMs < trimmed.durationMs)
        assertTrue(sub.durationMs >= 25)
        assertEquals(trimmed.autoTrimStart, sub.autoTrimStart, 1e-6)

        // 2. 2.0 s continuous tone → capped at <= 0.65 s with smooth fade-out.
        val longTone = FloatArray(sr * 2) { i ->
            (0.30 * sin(2.0 * PI * 520.0 * i / sr)).toFloat()
        }
        val capped = CustomPageTurnSounds.trimAndNormalize(longTone, sr)
        assertTrue("Unexpected capped duration: ${capped.durationMs}", capped.durationMs in 640..660)
        val cappedBb = ByteBuffer.wrap(capped.wavData).order(ByteOrder.LITTLE_ENDIAN)
        val cappedCount = (capped.wavData.size - 44) / 2
        assertTrue(abs(cappedBb.getShort(44 + (cappedCount - 1) * 2).toInt()) < 64)

        // 3. Silent / near-silent buffer → rejected with SILENT_OR_EMPTY.
        val silent = FloatArray(sr / 4) { 0.0005f }
        try {
            CustomPageTurnSounds.trimAndNormalize(silent, sr)
            fail("Expected CustomSoundException for silent input")
        } catch (e: CustomSoundException) {
            assertEquals(CustomSoundException.Kind.SILENT_OR_EMPTY, e.kind)
        }
    }
}
