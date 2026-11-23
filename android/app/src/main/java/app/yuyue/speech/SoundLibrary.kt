package app.yuyue.speech

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.YuyueCore
import java.io.File

/**
 * 声音素材库, as on Apple: the room tones and spot sounds 广播剧 lays its scenes with, richer than
 * the ones the core makes from noise. Fetched once from 语阅's own release when a play is first
 * made with 环境音 or 音效 on, checked against its BLAKE3, kept beside the library, and told to the
 * core. Until it's here, or offline, the built-in sounds play as before.
 */
object SoundLibrary {
    internal const val NAME = "sounds-1"
    private const val URL = "https://github.com/inokoru/yuyue/releases/download/models-1/sounds-1.zip"
    /** BLAKE3 of the zip (`yuyue digest`); empty until the library is published. */
    internal const val DIGEST = ""

    fun folder(dataDir: File) = File(dataDir, "models/$NAME")
    fun installed(dataDir: File) = File(folder(dataDir), "library.json").isFile

    /** Tells the core where the library is, when it's here (cheap to ask again). */
    fun register(core: YuyueCore, dataDir: File) {
        if (installed(dataDir)) runCatching { core.dramaSetSoundLibrary(folder(dataDir).path) }
    }

    /** Fetched, checked and unpacked unless it's here; quietly not, offline. Debug builds take
     *  a zip built here with `-e soundsPath <file>`, unchecked. */
    suspend fun ensure(core: YuyueCore, dataDir: File, debugZip: File? = null) = withContext(Dispatchers.IO) {
        try {
            if (installed(dataDir)) return@withContext
            if (debugZip == null && DIGEST.isEmpty()) return@withContext
            val models = File(dataDir, "models").apply { mkdirs() }
            val temp = File(models, ".$NAME.zip.part")
            val unpack = File(models, "unpack-$NAME")
            try {
                if (debugZip != null) {
                    debugZip.copyTo(temp, overwrite = true)
                } else {
                    val urls = ModelDownload.candidateUrls(asset = "$NAME.zip", officialUrl = URL)
                    ModelDownload.downloadWithResume(
                        urls = urls,
                        partFile = temp,
                        expectedBlake3 = DIGEST,
                    )
                }
                unpack.deleteRecursively()
                uniffi.yuyue_core.unpackModel(temp.path, unpack.path)
                folder(dataDir).deleteRecursively()
                File(unpack, NAME).renameTo(folder(dataDir))
                temp.delete()
            } catch (_: Exception) {
            } finally {
                unpack.deleteRecursively()
            }
        } finally {
            register(core, dataDir)
        }
    }
}
