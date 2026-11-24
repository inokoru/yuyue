package app.yuyue.speech

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class CloudVoiceTest {

    @Test
    fun providerCatalogGroupsAndPaidFlagsMatchCurrentProviders() {
        val gridTiles = CloudVoice.Provider.entries.filter { it != CloudVoice.Provider.Local }
        assertEquals(15, gridTiles.size)
        assertEquals(6, gridTiles.count { it.group == CloudVoice.Group.Domestic })
        assertEquals(5, gridTiles.count { it.group == CloudVoice.Group.Global })
        assertEquals(4, gridTiles.count { it.group == CloudVoice.Group.Local })
        assertTrue(CloudVoice.Provider.Local.isLocal)
        assertTrue(!CloudVoice.Provider.Local.isPaidCloud)
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

        for (route in listOf("baidu_zh", "baidu_en", "baidu_uk")) {
            val url = CloudVoice.freeOnlineUrl(route, "hello")
            assertTrue(url.startsWith("https://fanyi.baidu.com/gettts?"))
            assertTrue(!url.contains("dict.youdao.com"))
        }
    }
}
