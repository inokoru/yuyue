package app.yuyue.speech

import app.yuyue.reader.SilenceAutoStopper
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import uniffi.yuyue_core.CardOrigin
import uniffi.yuyue_core.CardStyle
import uniffi.yuyue_core.CharacterAge
import uniffi.yuyue_core.CharacterGender
import uniffi.yuyue_core.VoiceCard
import java.nio.file.Files

class VoiceExperienceTest {

    private fun sampleCard(
        id: String,
        lang: String,
        narrator: Boolean,
        version: UInt = 1u,
    ): VoiceCard = VoiceCard(
        id = id,
        name = id,
        gender = CharacterGender.F,
        age = CharacterAge.YOUNG,
        tags = emptyList(),
        lang = lang,
        origin = CardOrigin.Builtin,
        clips = emptyList(),
        description = "",
        version = version,
        narrator = narrator,
        rate = 4.2f,
        embedding = emptyList(),
        sync = false,
    )

    @Test
    fun lowRamGateFollowsSixGigabyteThreshold() {
        assertTrue(LiveVoiceModel.isLowRam(4_000_000_000L))
        assertTrue(LiveVoiceModel.isLowRam(5_499_999_999L))
        assertFalse(LiveVoiceModel.isLowRam(5_500_000_000L))
        assertFalse(LiveVoiceModel.isLowRam(8_000_000_000L))
        assertFalse(LiveVoiceModel.isLowRam(0L))
    }

    @Test
    fun liveModelAndVoicePackConstantsArePopulated() {
        assertEquals(64, LiveVoiceModel.MODEL_DIGEST.length)
        assertTrue(LiveVoiceModel.MODEL_BYTES > 100_000_000L)
        assertEquals(64, LiveVoiceModel.PACK_DIGEST.length)
        assertTrue(LiveVoiceModel.PACK_BYTES > 10_000_000L)
    }

    @Test
    fun cardVoiceLanguageGateAcceptsMandarinAndEnglishOnly() {
        assertTrue(CardVoice.speaksLanguage("zh"))
        assertTrue(CardVoice.speaksLanguage("zh-Hans"))
        assertTrue(CardVoice.speaksLanguage("zh_CN"))
        assertTrue(CardVoice.speaksLanguage("en"))
        assertTrue(CardVoice.speaksLanguage("en-US"))
        assertTrue(CardVoice.speaksLanguage("en-GB"))

        assertFalse(CardVoice.speaksLanguage("ja"))
        assertFalse(CardVoice.speaksLanguage("yue"))
        assertFalse(CardVoice.speaksLanguage("zh-HK"))
        assertFalse(CardVoice.speaksLanguage("fr"))
    }

    @Test
    fun plainSpokenTextStripsHeteronymAnnotations() {
        assertEquals(
            "甄士隐听说“假作真时真亦假”。",
            CardVoice.plainSpokenText("{甄|zhēn}士隐听说“{假|jiǎ}作真时真亦{假|jiǎ}”。"),
        )
        assertEquals("普通文本不变", CardVoice.plainSpokenText("普通文本不变"))
    }

    @Test
    fun promptLruCacheKeepsAtMostSixteenEntriesAndInvalidatesByCard() {
        val cache = CardVoice.PromptLruCache<CardVoice.PromptKey, Int>(maxEntries = 16)
        for (i in 1..16) {
            cache.put(CardVoice.PromptKey("card.b.%02d".format(i), 1u, CardStyle.NEUTRAL), i)
        }
        assertEquals(16, cache.size())
        // Touch card.b.01 so card.b.02 becomes least-recently-used.
        assertEquals(1, cache.get(CardVoice.PromptKey("card.b.01", 1u, CardStyle.NEUTRAL)))

        cache.put(CardVoice.PromptKey("card.b.17", 1u, CardStyle.NEUTRAL), 17)
        assertEquals(16, cache.size())
        assertEquals(1, cache.get(CardVoice.PromptKey("card.b.01", 1u, CardStyle.NEUTRAL)))
        assertNull(cache.get(CardVoice.PromptKey("card.b.02", 1u, CardStyle.NEUTRAL)))

        cache.removeIf { it.cardId == "card.b.01" }
        assertNull(cache.get(CardVoice.PromptKey("card.b.01", 1u, CardStyle.NEUTRAL)))
    }

    @Test
    fun trimFolderToBytesEvictsOldestClipsFirst() {
        val dir = Files.createTempDirectory("yuyue-card-cache").toFile()
        try {
            val f1 = dir.resolve("old.pcm").apply {
                writeBytes(ByteArray(400))
                setLastModified(1_000L)
            }
            val f2 = dir.resolve("mid.pcm").apply {
                writeBytes(ByteArray(400))
                setLastModified(2_000L)
            }
            val f3 = dir.resolve("new.pcm").apply {
                writeBytes(ByteArray(400))
                setLastModified(3_000L)
            }
            CardVoice.trimFolderToBytes(dir, maxBytes = 900L)
            assertFalse(f1.exists())
            assertTrue(f2.exists())
            assertTrue(f3.exists())
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun builtinCardsSortNarratorsFirstAndPickDefaultNarratorByLanguage() {
        val cards = listOf(
            sampleCard("card.b.05", "zh", narrator = false),
            sampleCard("card.b.02", "zh", narrator = true),
            sampleCard("card.b.01", "zh", narrator = true),
            sampleCard("card.b.35", "en", narrator = false),
            sampleCard("card.b.33", "en", narrator = true),
        )
        val zhSorted = Voices.sortBuiltinCards(cards, "zh-Hans")
        assertEquals(listOf("card.b.01", "card.b.02", "card.b.05"), zhSorted.map { it.id })
        assertEquals("card.b.01", Voices.pickDefaultNarrator(cards, "zh")?.id)

        val enSorted = Voices.sortBuiltinCards(cards, "en-US")
        assertEquals(listOf("card.b.33", "card.b.35"), enSorted.map { it.id })
        assertEquals("card.b.33", Voices.pickDefaultNarrator(cards, "en")?.id)

        assertTrue(Voices.sortBuiltinCards(cards, "ja").isEmpty())
    }

    @Test
    fun playerThermalBudgetScalesDownGracefully() {
        assertEquals(60.0, Player.targetAheadSeconds(0), 0.001)
        assertEquals(60.0, Player.targetAheadSeconds(2), 0.001)
        assertEquals(15.0, Player.targetAheadSeconds(Player.THERMAL_SEVERE), 0.001)
        assertEquals(0.0, Player.targetAheadSeconds(Player.THERMAL_CRITICAL), 0.001)
    }

    @Test
    fun silenceAutoStopperWaitsForSpeechThenStopsAfterOnePointTwoSecondsQuiet() {
        val stopper = SilenceAutoStopper(silenceStopSec = 1.2, maxTotalSec = 16.0)
        // Initial room silence before user starts speaking should NOT stop early.
        repeat(15) {
            assertFalse(stopper.pushFrame(-50f, 0.1))
        }
        // User speaks for 0.6 s.
        repeat(6) {
            assertFalse(stopper.pushFrame(-18f, 0.1))
        }
        // 1.1 s of trailing silence: not stopped yet.
        repeat(11) {
            assertFalse(stopper.pushFrame(-48f, 0.1))
        }
        // Reaching 1.2 s of trailing silence triggers auto-stop.
        assertTrue(stopper.pushFrame(-48f, 0.1))
    }

    @Test
    fun dailyQuoteDefaultsAndSourceFormattingStayClean() {
        val zh = DailyQuote.current("zh")
        assertTrue(zh.text.isNotBlank())
        assertFalse(zh.text.contains("我冒了严寒"))

        val en = DailyQuote.current("en")
        assertTrue(en.text.isNotBlank())

        assertEquals("苏轼《赤壁赋》", DailyQuote.formatSource("苏轼", "赤壁赋"))
        assertEquals("《月亮与六便士》", DailyQuote.formatSource("null", "月亮与六便士"))
        assertEquals("鲁迅《野草》", DailyQuote.formatSource("鲁迅", "《野草》"))
        assertEquals("", DailyQuote.formatSource("佚名", "原创"))
    }

    @Test
    fun systemVoiceWavDownmixesStereoAndRejectsMalformedChunks() {
        val dir = Files.createTempDirectory("yuyue-wav-test").toFile()
        try {
            // Build a 2-channel 16-bit PCM WAV with 2 frames: (1000, 3000) -> 2000, (-4000, 0) -> -2000
            val pcm = java.nio.ByteBuffer.allocate(8).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .putShort(1000).putShort(3000)
                .putShort(-4000).putShort(0)
                .array()
            val header = java.nio.ByteBuffer.allocate(44 + pcm.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .put("RIFF".toByteArray()).putInt(36 + pcm.size)
                .put("WAVEfmt ".toByteArray()).putInt(16).putShort(1).putShort(2) // stereo
                .putInt(24_000).putInt(24_000 * 4).putShort(4).putShort(16)
                .put("data".toByteArray()).putInt(pcm.size).put(pcm)
                .array()
            val stereoFile = dir.resolve("stereo.wav").apply { writeBytes(header) }
            val decoded = SystemVoice.Engine.wav(stereoFile)
            assertEquals(24_000, decoded?.rate)
            assertEquals(2, decoded?.samples?.size)
            assertEquals(2000.toShort(), decoded?.samples?.get(0))
            assertEquals((-2000).toShort(), decoded?.samples?.get(1))

            // Malformed negative chunk size must return null instead of looping forever.
            val bad = java.nio.ByteBuffer.allocate(44).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .put("RIFF".toByteArray()).putInt(36)
                .put("WAVEJUNK".toByteArray()).putInt(-8)
                .array()
            val badFile = dir.resolve("bad.wav").apply { writeBytes(bad) }
            assertNull(SystemVoice.Engine.wav(badFile))
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun kokoroInstalledRequiresEnglishLexiconFile() {
        val dir = Files.createTempDirectory("yuyue-kokoro-check").toFile()
        try {
            val modelDir = KokoroModel.folder(dir).apply { mkdirs() }
            for (name in listOf("model.int8.onnx", "voices.bin", "tokens.txt", "lexicon-zh.txt")) {
                modelDir.resolve(name).writeBytes(byteArrayOf(1))
            }
            assertFalse(KokoroModel.installed(dir))

            modelDir.resolve("lexicon-us-en.txt").writeBytes(byteArrayOf(1))
            assertTrue(KokoroModel.installed(dir))
        } finally {
            dir.deleteRecursively()
        }
    }
}
