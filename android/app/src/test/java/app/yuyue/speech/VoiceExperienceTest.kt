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
        assertEquals(15.0, Player.targetAheadSeconds(0), 0.001)
        assertEquals(15.0, Player.targetAheadSeconds(2), 0.001)
        assertEquals(8.0, Player.targetAheadSeconds(Player.THERMAL_SEVERE), 0.001)
        assertEquals(0.0, Player.targetAheadSeconds(Player.THERMAL_CRITICAL), 0.001)
    }

    @Test
    fun previewSampleLineKeepsLongBookSentenceShort() {
        val longZh = "河见的心意，苍子自然很高兴，不过河见每次都买圆圆的一整个回来，而他自己是决不吃甜食的。"
        val sample = app.yuyue.reader.previewSampleLine(longZh, "zh-Hans")
        assertTrue(sample.length <= 24)
        assertEquals("河见的心意，苍子自然很高兴，", sample)
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

    @Test
    fun kokoroStyleEmbeddingExtractsNormalizedVectorAndHandlesMissingOrInvalidSid() {
        val dir = Files.createTempDirectory("yuyue-kokoro-emb").toFile()
        try {
            assertTrue(KokoroModel.styleEmbedding(0u, dir).isEmpty())
            assertTrue(KokoroModel.styleEmbedding(103u, dir).isEmpty())

            val modelDir = KokoroModel.folder(dir).apply { mkdirs() }
            val dim = 256
            val bytesPerVec = dim * 4
            // Allocate space covering sid = 1 through bucket 80
            val totalFloats = (1 * 510 + 81) * dim
            val buf = java.nio.ByteBuffer.allocate(totalFloats * 4).order(java.nio.ByteOrder.LITTLE_ENDIAN)
            for (b in intArrayOf(10, 20, 40, 80)) {
                val base = (1 * 510 + b) * dim
                buf.putFloat(base * 4, 3f)
                buf.putFloat((base + 1) * 4, 4f)
            }
            modelDir.resolve("voices.bin").writeBytes(buf.array())

            val emb = KokoroModel.styleEmbedding(1u, dir)
            assertEquals(256, emb.size)
            assertEquals(0.6f, emb[0], 1e-4f)
            assertEquals(0.8f, emb[1], 1e-4f)
            assertEquals(0f, emb[2], 1e-4f)
            // sid 2 exceeds file length -> returns empty safely
            assertTrue(KokoroModel.styleEmbedding(2u, dir).isEmpty())
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun kokoroDownloadProgressCannotOverwriteTerminalOrNewerState() {
        val run1 = KokoroModel.beginDownloadRun()
        assertEquals(KokoroModel.State.Downloading(0.0), KokoroModel.state)

        // Normal progress update for active run succeeds.
        assertTrue(KokoroModel.applyDownloadProgress(run1, 0.45))
        assertEquals(KokoroModel.State.Downloading(0.45), KokoroModel.state)

        // 1.0 progress is ignored so it never leaves Downloading(0.99).
        assertFalse(KokoroModel.applyDownloadProgress(run1, 1.0))
        assertEquals(KokoroModel.State.Downloading(0.45), KokoroModel.state)

        // Transitioning to Ready invalidates run1: stale progress cannot regress Ready -> Downloading(0.99).
        KokoroModel.finishDownloadRun(run1, KokoroModel.State.Ready)
        assertEquals(KokoroModel.State.Ready, KokoroModel.state)
        assertFalse(KokoroModel.applyDownloadProgress(run1, 0.99))
        assertEquals(KokoroModel.State.Ready, KokoroModel.state)

        // Same for Idle and Failed terminal states.
        val run2 = KokoroModel.beginDownloadRun()
        KokoroModel.finishDownloadRun(run2, KokoroModel.State.Failed)
        assertFalse(KokoroModel.applyDownloadProgress(run2, 0.8))
        assertEquals(KokoroModel.State.Failed, KokoroModel.state)

        val run3 = KokoroModel.beginDownloadRun()
        val run4 = KokoroModel.beginDownloadRun()
        assertFalse(KokoroModel.applyDownloadProgress(run3, 0.7))
        assertTrue(KokoroModel.applyDownloadProgress(run4, 0.25))
        assertEquals(KokoroModel.State.Downloading(0.25), KokoroModel.state)

        KokoroModel.finishDownloadRun(run4, KokoroModel.State.Idle)
        assertEquals(KokoroModel.State.Idle, KokoroModel.state)
    }

    @Test
    fun spokenLanguageMatchesOemChineseAndEnglishLocaleVariants() {
        for (oemTag in listOf("zh-CN", "zh-Hans-CN", "zh-Hans", "zh", "zh_CN", "cmn-CN", "cmn-Hans-CN", "zho-CHN", "zh-SG", "zh-TW", "zh-Hant-TW")) {
            assertTrue("expected $oemTag to match zh", SpokenLanguage.matches(oemTag, "zh"))
            assertTrue("expected $oemTag to match zh-Hans", SpokenLanguage.matches(oemTag, "zh-Hans"))
        }
        assertFalse(SpokenLanguage.matches("en-US", "zh"))
        assertFalse(SpokenLanguage.matches("ja-JP", "zh"))
        assertFalse(SpokenLanguage.matches("zh-HK", "zh-Hans"))
        assertTrue(SpokenLanguage.matches("zh-HK", "yue"))
        assertTrue(SpokenLanguage.matches("yue-HK", "zh-HK"))

        for (enTag in listOf("en", "en-US", "en-GB", "eng-USA", "en_AU")) {
            assertTrue("expected $enTag to match en", SpokenLanguage.matches(enTag, "en"))
            assertTrue("expected $enTag to match en-US", SpokenLanguage.matches(enTag, "en-US"))
        }
        assertEquals("zh-CN", SpokenLanguage.localeForTag("zh-Hans").toLanguageTag())
        assertEquals("zh-TW", SpokenLanguage.localeForTag("zh-Hant").toLanguageTag())
        assertEquals("en-US", SpokenLanguage.localeForTag("en").toLanguageTag())
    }

    @Test
    fun systemDefaultVoiceHasCanonicalIdAndRespectsSupportPredicate() {
        val voice = SystemVoice.systemDefault(
            engineLabel = "Samsung TTS",
            isOffline = true,
            supports = { it.lowercase().startsWith("zh") },
        )
        assertEquals(SystemVoice.DEFAULT_ID, voice.id)
        assertTrue(voice.isSystemDefault)
        assertEquals("Samsung TTS", voice.qualityName)
        assertTrue(voice.isOffline)
        assertTrue(voice.speaks("zh-Hans"))
        assertFalse(voice.speaks("en-US"))
        assertEquals("system", app.yuyue.analytics.AirTraffic.ttsEngineForVoiceId(voice.id))
    }

    @Test
    fun systemVoiceWavDecodesFloat32And8BitPcm() {
        val dir = Files.createTempDirectory("yuyue-wav-formats").toFile()
        try {
            // 32-bit IEEE float mono WAV: [0.5f, -0.5f]
            val f32Data = java.nio.ByteBuffer.allocate(8).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .putFloat(0.5f).putFloat(-0.5f).array()
            val f32Wav = java.nio.ByteBuffer.allocate(44 + f32Data.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .put("RIFF".toByteArray()).putInt(36 + f32Data.size)
                .put("WAVEfmt ".toByteArray()).putInt(16).putShort(3).putShort(1) // IEEE float, mono
                .putInt(24_000).putInt(24_000 * 4).putShort(4).putShort(32)
                .put("data".toByteArray()).putInt(f32Data.size).put(f32Data)
                .array()
            val f32File = dir.resolve("f32.wav").apply { writeBytes(f32Wav) }
            val decodedF32 = SystemVoice.Engine.wav(f32File)
            assertEquals(24_000, decodedF32?.rate)
            assertEquals(2, decodedF32?.samples?.size)
            assertTrue(decodedF32!!.samples[0] in 16_300..16_400)
            assertTrue(decodedF32.samples[1] in -16_400..-16_300)

            // 8-bit unsigned PCM mono WAV: [128 -> 0, 255 -> ~32512]
            val u8Data = byteArrayOf(128.toByte(), 255.toByte())
            val u8Wav = java.nio.ByteBuffer.allocate(44 + u8Data.size).order(java.nio.ByteOrder.LITTLE_ENDIAN)
                .put("RIFF".toByteArray()).putInt(36 + u8Data.size)
                .put("WAVEfmt ".toByteArray()).putInt(16).putShort(1).putShort(1)
                .putInt(16_000).putInt(16_000).putShort(1).putShort(8)
                .put("data".toByteArray()).putInt(u8Data.size).put(u8Data)
                .array()
            val u8File = dir.resolve("u8.wav").apply { writeBytes(u8Wav) }
            val decodedU8 = SystemVoice.Engine.wav(u8File)
            assertEquals(16_000, decodedU8?.rate)
            assertEquals(2, decodedU8?.samples?.size)
            assertEquals(0.toShort(), decodedU8?.samples?.get(0))
            assertTrue(decodedU8!!.samples[1] > 32_000)
        } finally {
            dir.deleteRecursively()
        }
    }
}
