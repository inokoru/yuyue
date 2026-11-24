package app.yuyue.speech

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.concurrent.atomic.AtomicInteger

class CloudVoiceTest {

    @Test
    fun providerCatalogGroupsAndPaidFlagsMatchCurrentProviders() {
        val gridTiles = CloudVoice.Provider.entries.filter { it != CloudVoice.Provider.Local }
        assertEquals(16, gridTiles.size)
        assertEquals(6, gridTiles.count { it.group == CloudVoice.Group.Domestic })
        assertEquals(6, gridTiles.count { it.group == CloudVoice.Group.Global })
        assertEquals(4, gridTiles.count { it.group == CloudVoice.Group.Local })
        assertTrue(CloudVoice.Provider.Local.isLocal)
        assertTrue(!CloudVoice.Provider.Local.isPaidCloud)
        assertTrue(CloudVoice.Provider.Edge.isFreeOnline)
        assertTrue(!CloudVoice.Provider.Edge.isPaidCloud)
        assertTrue(CloudVoice.Provider.Free.isFreeOnline)
        assertTrue(!CloudVoice.Provider.Free.isPaidCloud)
        assertTrue(CloudVoice.Provider.Xiaomi.isPaidCloud)
    }

    @Test
    fun speechEndpointNormalizesHostAndPreservesExplicitPaths() {
        assertNull(CloudVoice.speechEndpoint("   "))
        assertEquals(
            "http://127.0.0.1:3900/v1/audio/speech",
            CloudVoice.speechEndpoint("http://127.0.0.1:3900"),
        )
        assertEquals(
            "http://192.168.1.5:17493/v1/audio/speech",
            CloudVoice.speechEndpoint(" http://192.168.1.5:17493/v1/ "),
        )
        assertEquals(
            "http://192.168.1.5:8000/v1/audio/speech",
            CloudVoice.speechEndpoint("http://192.168.1.5:8000/v1/audio/speech/"),
        )
        assertEquals(
            "http://192.168.1.5:9000/custom/audio/speech",
            CloudVoice.speechEndpoint("http://192.168.1.5:9000/custom/audio/speech"),
        )
        assertEquals(
            "http://192.168.1.5:8000/v1",
            CloudVoice.normalizedBaseV1("http://192.168.1.5:8000/v1/audio/speech"),
        )
    }

    @Test
    fun sniffExtDetectsAudioHeadersAccurately() {
        assertEquals("wav", CloudVoice.sniffExt("RIFF\u0000\u0000".toByteArray(), "mp3"))
        assertEquals("mp3", CloudVoice.sniffExt("ID3\u0004\u0000".toByteArray(), "wav"))
        assertEquals(
            "mp3",
            CloudVoice.sniffExt(byteArrayOf(0xFF.toByte(), 0xFB.toByte(), 0x90.toByte(), 0x64.toByte()), "wav"),
        )
        assertEquals("flac", CloudVoice.sniffExt("fLaC\u0000".toByteArray(), "wav"))
        assertEquals("ogg", CloudVoice.sniffExt("OggS\u0000".toByteArray(), "wav"))
        assertEquals("mp3", CloudVoice.sniffExt(byteArrayOf(0x01, 0x02), "mp3"))
    }

    @Test
    fun builtinProviderVoiceTablesHaveExpectedCoverage() {
        assertEquals(8, CloudVoice.xiaomiVoices.size)
        assertTrue(CloudVoice.xiaomiVoices.any { it.id == "冰糖" && it.lang == "zh-CN" })
        assertTrue(CloudVoice.xiaomiVoices.any { it.id == "Mia" && it.lang == "en-US" })
        assertTrue(CloudVoice.siliconFlowPresets.isNotEmpty())
        assertTrue(CloudVoice.dashScopeVoices.isNotEmpty())
        assertTrue(CloudVoice.miniMaxPresets.isNotEmpty())
        assertTrue(CloudVoice.deepgramVoices.all { it.lang == "en-US" })
    }

    @Test
    fun freeOnlineVoicesRouteAllLanguagesToSentenceCapableEndpoints() {
        assertEquals(listOf("zh_baidu", "en_us", "en_uk"), CloudVoice.freeOnlineVoices.map { it.id })
        assertEquals("baidu_zh", CloudVoice.freeOnlineRoute("zh_baidu", "你好，世界。", "zh-CN"))
        // Legacy saved zh_youdao voice IDs must still resolve to working Chinese sentence TTS.
        assertEquals("baidu_zh", CloudVoice.freeOnlineRoute("zh_youdao", "你好，世界。", "zh-CN"))
        assertEquals("baidu_en", CloudVoice.freeOnlineRoute("en_us", "Hello world.", "en-US"))
        assertEquals("baidu_uk", CloudVoice.freeOnlineRoute("en_uk", "Hello world.", "en-GB"))
        // Chosen speaker stays locked even when a sentence mixes CJK and English.
        assertEquals("baidu_en", CloudVoice.freeOnlineRoute("en_us", "Hello，孔子说。", "en-US"))
        assertEquals("baidu_uk", CloudVoice.freeOnlineRoute("en_uk", "Chapter 1 · 序章", "en-GB"))
        assertEquals("baidu_zh", CloudVoice.freeOnlineRoute("zh_baidu", "Chapter 3", "en-US"))

        for (route in listOf("baidu_zh", "baidu_en", "baidu_uk")) {
            val url = CloudVoice.freeOnlineUrl(route, "hello")
            assertTrue(url.startsWith("https://fanyi.baidu.com/gettts?"))
            assertTrue(!url.contains("dict.youdao.com"))
        }
    }

    @Test
    fun edgeTtsGeneratesValidSecMsGecV2TokensAndFormatsVoiceNames() {
        assertEquals(
            "2CD23A2CA0B2595A-F3C846C370E05F9CC83ACC5F995C5F5D87A4D0F14CE8936A0F902E64099E38A2",
            EdgeTTS.generateSecMsGecV2(1700000000.0),
        )
        assertEquals(
            "6D50DDDB91812C33-DD68EFB01898B0750C9797D51A5FF93471DFDAE6888A1039D532CBDCCE54545D",
            EdgeTTS.generateSecMsGecV2(1760000000.0),
        )
        assertEquals(
            "Microsoft Server Speech Text to Speech Voice (zh-CN, XiaoxiaoNeural)",
            EdgeTTS.formatVoiceName("zh-CN-XiaoxiaoNeural"),
        )
        assertEquals(
            "Microsoft Server Speech Text to Speech Voice (zh-CN-liaoning, XiaobeiNeural)",
            EdgeTTS.formatVoiceName("zh-CN-liaoning-XiaobeiNeural"),
        )
        assertEquals(22, CloudVoice.edgeVoices.size)
        assertTrue(CloudVoice.edgeVoices.any { it.id == "zh-CN-XiaoxiaoNeural" && it.lang == "zh-CN" })
        assertTrue(CloudVoice.edgeVoices.any { it.id == "en-US-AvaMultilingualNeural" && it.lang == "en-US" })
    }

    @Test
    fun inFlightDeduplicatorSharesSingleFetchAndRespectsIndependentCancellation() = runBlocking {
        val dedup = CloudVoice.InFlightDeduplicator<String>()
        val fetchCount = AtomicInteger(0)
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<String>()

        val w1 = async(Dispatchers.IO) {
            dedup.awaitOrLaunch("cloud.openai.alloy\u0001hello") {
                fetchCount.incrementAndGet()
                started.complete(Unit)
                release.await()
            }
        }
        started.await()
        val w2 = async(Dispatchers.IO) {
            dedup.awaitOrLaunch("cloud.openai.alloy\u0001hello") {
                fetchCount.incrementAndGet()
                "should-not-run"
            }
        }

        // Give w2 a moment to join the in-flight entry, then cancel w1 only.
        kotlinx.coroutines.delay(30)
        w1.cancel()
        try {
            w1.await()
            fail("Expected w1 to cancel")
        } catch (_: CancellationException) {
            // expected
        }

        // Underlying fetch must stay alive for w2.
        assertEquals(1, fetchCount.get())
        release.complete("audio-ok")
        assertEquals("audio-ok", w2.await())
        assertEquals(1, fetchCount.get())
        assertEquals(0, dedup.activeCount())
    }

    @Test
    fun inFlightDeduplicatorCancelsUnderlyingFetchWhenAllWaitersCancel() = runBlocking {
        val dedup = CloudVoice.InFlightDeduplicator<String>()
        val started = CompletableDeferred<Unit>()
        val underlyingCancelled = CompletableDeferred<Unit>()

        val w1 = async(Dispatchers.IO) {
            dedup.awaitOrLaunch("cloud.edge.v1\u0001cancel-all") {
                started.complete(Unit)
                try {
                    kotlinx.coroutines.awaitCancellation()
                } finally {
                    underlyingCancelled.complete(Unit)
                }
            }
        }
        started.await()
        w1.cancel()
        underlyingCancelled.await()
        assertEquals(0, dedup.activeCount())
    }

    @Test
    fun writeFileAtomicallyUsesUniqueTempAndCleansUpOnFailure() {
        val dir = Files.createTempDirectory("yuyue-atomic-cache").toFile()
        try {
            val target = File(dir, "clip.mp3")
            val first = byteArrayOf(1, 2, 3, 4)
            CloudVoice.writeFileAtomically(target, first)
            assertArrayEquals(first, target.readBytes())
            assertTrue(dir.listFiles().orEmpty().none { it.name.endsWith(".tmp") })

            val updated = byteArrayOf(5, 6, 7, 8, 9)
            CloudVoice.writeFileAtomically(target, updated)
            assertArrayEquals(updated, target.readBytes())
            assertTrue(dir.listFiles().orEmpty().none { it.name.endsWith(".tmp") })

            // Target is a directory -> renameTo fails; temp file must be removed and not leaked.
            val badTarget = File(dir, "not-a-file.mp3").apply { mkdirs() }
            try {
                CloudVoice.writeFileAtomically(badTarget, updated)
                fail("Expected IOException when rename target is a directory")
            } catch (_: IOException) {
                // expected
            }
            assertTrue(badTarget.isDirectory)
            assertTrue(dir.listFiles().orEmpty().none { it.name.endsWith(".tmp") })
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun cacheExtensionsAndLooksLikeAudioSupportFlacAndOgg() {
        assertEquals(listOf("mp3", "wav", "flac", "ogg"), CloudVoice.cacheExtensions)

        val flac = ByteArray(300).apply {
            this[0] = 0x66; this[1] = 0x4C; this[2] = 0x61; this[3] = 0x43 // fLaC
        }
        val ogg = ByteArray(300).apply {
            this[0] = 0x4F; this[1] = 0x67; this[2] = 0x67; this[3] = 0x53 // OggS
        }
        val garbage = ByteArray(300) { 0x20 }
        assertTrue(CloudVoice.looksLikeAudio(flac))
        assertTrue(CloudVoice.looksLikeAudio(ogg))
        assertFalse(CloudVoice.looksLikeAudio(garbage))
        assertFalse(CloudVoice.looksLikeAudio(byteArrayOf(0x66, 0x4C, 0x61, 0x43))) // too short

        val persistent = Files.createTempDirectory("yuyue-persist").toFile()
        val legacy = Files.createTempDirectory("yuyue-legacy").toFile()
        try {
            val flacFile = File(persistent, "local/abc123.flac").apply {
                parentFile?.mkdirs()
                writeBytes(flac)
            }
            val candidates = CloudVoice.cacheCandidates(persistent, legacy, "local", "abc123")
            val hit = candidates.firstOrNull { (file, _) -> file.exists() }
            assertEquals(flacFile to "flac", hit)

            flacFile.delete()
            val oggFile = File(persistent, "local/abc123.ogg").apply { writeBytes(ogg) }
            val hitOgg = CloudVoice.cacheCandidates(persistent, legacy, "local", "abc123")
                .firstOrNull { (file, _) -> file.exists() }
            assertEquals(oggFile to "ogg", hitOgg)
        } finally {
            persistent.deleteRecursively()
            legacy.deleteRecursively()
        }
    }
}
