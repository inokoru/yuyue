package app.yuyue.speech

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CloudVoiceTest {

    @Test
    fun providerCatalogFormsSymmetricFourByThreeGridPlusCustomEndpoint() {
        val gridTiles = CloudVoice.Provider.entries.filter { it != CloudVoice.Provider.Local }
        assertEquals(12, gridTiles.size)
        assertEquals(4, gridTiles.count { it.group == CloudVoice.Group.Domestic })
        assertEquals(4, gridTiles.count { it.group == CloudVoice.Group.Global })
        assertEquals(4, gridTiles.count { it.group == CloudVoice.Group.Local })
        assertTrue(CloudVoice.Provider.Local.isLocal)
        assertTrue(!CloudVoice.Provider.Local.isPaidCloud)
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
}
