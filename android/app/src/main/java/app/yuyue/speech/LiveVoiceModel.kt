package app.yuyue.speech

import android.app.ActivityManager
import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.io.IOException

/**
 * 自带声音包 (`models/voices-1/voices.json` + Ogg reference clips).
 * Downloaded alongside [LiveVoiceModel] as one "朗读声音" bundle in the UI.
 */
object VoicePack {
    const val NAME = "voices-1"
    const val URL = "https://github.com/inokoru/yuyue/releases/download/models-1/voices-1.zip"
    const val DIGEST = LiveVoiceModel.PACK_DIGEST
    const val BYTES = LiveVoiceModel.PACK_BYTES

    fun folder(dataDir: File) = File(dataDir, "models/$NAME")
    fun installed(dataDir: File) = File(folder(dataDir), "voices.json").isFile

    /** Registers the built-in voice pack with the core when present on disk. */
    fun register(core: YuyueCore, dataDir: File): Int {
        if (!installed(dataDir)) return 0
        return runCatching { core.cardsSetPack(folder(dataDir).path).toInt() }.getOrDefault(0)
    }

    /** Debug helper (`-e voicePackPath <zip-or-dir>`). */
    fun installDebug(core: YuyueCore, dataDir: File, from: File) {
        installDebugBundle(dataDir, NAME, folder(dataDir), from)
        register(core, dataDir)
    }
}

internal fun installDebugBundle(dataDir: File, name: String, dst: File, from: File) {
    if (!from.exists()) return
    if (from.isDirectory) {
        dst.deleteRecursively()
        from.copyRecursively(dst, overwrite = true)
    } else if (from.isFile) {
        val unpack = File(dataDir, "models/unpack-$name")
        try {
            unpack.deleteRecursively()
            uniffi.yuyue_core.unpackModel(from.path, unpack.path)
            val inner = File(unpack, name).takeIf { it.isDirectory } ?: unpack
            dst.deleteRecursively()
            dst.parentFile?.mkdirs()
            if (!inner.renameTo(dst)) inner.copyRecursively(dst, overwrite = true)
        } finally {
            unpack.deleteRecursively()
        }
    }
}

/**
 * 即时层语音合成模型 (`models/live-zipvoice-1/`, `ZipVoice-Distill-Int8` + `Vocos 24 kHz`).
 *
 * Manages low-RAM device gating (< 6 GB phones stay on system voices), network-aware background
 * preparation on first read-aloud (Wi‑Fi auto-downloads; cellular waits for tap; failures show inline
 * retry without modal alerts), BLAKE3 verification, unpacking, and core registration.
 */
object LiveVoiceModel {
    const val NAME = "live-zipvoice-1"
    const val MODEL_URL = "https://github.com/inokoru/yuyue/releases/download/models-1/live-zipvoice-1.zip"
    const val MODEL_DIGEST = "7a2542b6301088152211d6edf4bd66ac3527fd29ec85ecf9b35fd4cc059b6bf9"
    const val MODEL_BYTES = 149_776_622L

    const val PACK_URL = VoicePack.URL
    const val PACK_DIGEST = "2d35635523d08b1b2d9d5cbfb49695a1eb424bbe7d4eafe51e1c255414d44f2c"
    const val PACK_BYTES = 14_002_684L
    const val TOTAL_MB = 160L

    /** Phones below ~6 GB RAM (`totalMem < 5.5 GB`) skip the on-device neural synthesizer. */
    private const val MIN_RAM_BYTES = 5_500_000_000L

    sealed interface State {
        data object Idle : State
        /** Cellular / metered network: offer `更好的声音 · 160 MB · 下载`, wait for user tap. */
        data object Metered : State
        /** Downloading model + voice pack (`0.0..1.0`). */
        data class Downloading(val progress: Double) : State
        /** Both model and voice pack are installed and registered. */
        data object Ready : State
        /** Download failed (`没下下来 · 再试`), never shown as a modal dialog. */
        data object Failed : State
        /** Device has < 6 GB RAM (`这台手机带不动`). */
        data object Unsupported : State
    }

    var deviceUnsupported by mutableStateOf(false)
        private set

    var state by mutableStateOf<State>(State.Idle)
        private set

    /** Emits whenever the live voice bundle finishes installing and becomes ready. */
    val readyEvents = MutableSharedFlow<Unit>(extraBufferCapacity = 4)

    private val scope: CoroutineScope = MainScope()
    private var downloadJob: Job? = null

    fun folder(dataDir: File) = File(dataDir, "models/$NAME")

    /** True when the ZipVoice ONNX weights and lexicon files are on disk. */
    fun modelInstalled(dataDir: File): Boolean {
        val dir = folder(dataDir)
        return File(dir, "encoder.int8.onnx").isFile &&
            File(dir, "decoder.int8.onnx").isFile &&
            File(dir, "vocos_24khz.onnx").isFile &&
            File(dir, "tokens.txt").isFile
    }

    /** True when this device can run on-device `CardVoice` synthesis (cloned, designed, or built-in cards). */
    fun canRun(dataDir: File): Boolean = !deviceUnsupported && modelInstalled(dataDir)

    /** True when both the synthesizer model and built-in `voices-1` pack are installed. */
    fun ready(dataDir: File): Boolean = canRun(dataDir) && VoicePack.installed(dataDir)

    /** Pure helper for unit tests: whether `totalMemBytes` falls below the 6 GB tier. */
    internal fun isLowRam(totalMemBytes: Long): Boolean = totalMemBytes in 1 until MIN_RAM_BYTES

    /** Checks device RAM and registers any already-present model or voice pack off the main thread. */
    fun init(context: Context, core: YuyueCore, dataDir: File) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
        val info = ActivityManager.MemoryInfo()
        am?.getMemoryInfo(info)
        deviceUnsupported = isLowRam(info.totalMem)
        if (deviceUnsupported) {
            state = State.Unsupported
            return
        }
        scope.launch {
            val isReady = withContext(Dispatchers.IO) {
                if (modelInstalled(dataDir)) {
                    runCatching { core.liveVoiceSetDir(folder(dataDir).path) }
                }
                if (VoicePack.installed(dataDir)) {
                    VoicePack.register(core, dataDir)
                }
                ready(dataDir)
            }
            if (isReady && state !is State.Ready) {
                state = State.Ready
            }
        }
    }

    /** Registers `live-zipvoice-1` and `voices-1` with `YuyueCore` whenever their files exist on disk. */
    fun register(core: YuyueCore, dataDir: File) {
        if (deviceUnsupported) return
        if (modelInstalled(dataDir)) {
            runCatching { core.liveVoiceSetDir(folder(dataDir).path) }
        }
        if (VoicePack.installed(dataDir)) {
            VoicePack.register(core, dataDir)
        }
        if (ready(dataDir) && state !is State.Ready) {
            if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
                state = State.Ready
            } else {
                scope.launch { if (state !is State.Ready) state = State.Ready }
            }
        }
    }

    /** Re-checks on-disk files after storage cleanup and updates state without triggering a download. */
    fun refresh(dataDir: File) {
        if (deviceUnsupported) {
            state = State.Unsupported
            return
        }
        if (state is State.Downloading) return
        state = if (ready(dataDir)) State.Ready else State.Idle
    }

    /** Debug helper (`-e liveVoicePath <zip-or-dir>`). */
    fun installDebug(core: YuyueCore, dataDir: File, from: File) {
        installDebugBundle(dataDir, NAME, folder(dataDir), from)
        register(core, dataDir)
    }

    /**
     * Triggered when the reader starts listening in a supported language and hasn't installed the
     * bundle yet: auto-downloads on Wi‑Fi, waits for user tap on cellular.
     */
    fun onFirstListen(context: Context, core: YuyueCore, dataDir: File) {
        if (deviceUnsupported) {
            state = State.Unsupported
            return
        }
        register(core, dataDir)
        if (ready(dataDir)) {
            state = State.Ready
            return
        }
        if (downloadJob?.isActive == true) return
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        val net = cm?.activeNetwork
        val caps = net?.let { cm.getNetworkCapabilities(it) }
        if (net == null || caps == null || !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) {
            // Offline: offer the manual download row without popping an error.
            if (state is State.Idle) state = State.Metered
            return
        }
        if (cm.isActiveNetworkMetered) {
            if (state !is State.Failed) state = State.Metered
        } else {
            startDownload(core, dataDir)
        }
    }

    /** Explicit user tap on `下载` or `再试` (runs on both Wi‑Fi and cellular). */
    fun startDownload(core: YuyueCore, dataDir: File) {
        if (deviceUnsupported) {
            state = State.Unsupported
            return
        }
        register(core, dataDir)
        if (ready(dataDir)) {
            state = State.Ready
            readyEvents.tryEmit(Unit)
            return
        }
        if (downloadJob?.isActive == true) return
        state = State.Downloading(0.0)
        downloadJob = scope.launch {
            try {
                ensure(core, dataDir) { p ->
                    state = State.Downloading(p.coerceIn(0.0, 1.0))
                }
                state = State.Ready
                app.yuyue.reader.DramaWork.tellCore(core)
                readyEvents.tryEmit(Unit)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (_: Exception) {
                state = State.Failed
            }
        }
    }

    /** Downloads, verifies, unpacks, and registers both `live-zipvoice-1` and `voices-1`. */
    suspend fun ensure(core: YuyueCore, dataDir: File, progress: (Double) -> Unit) = withContext(Dispatchers.IO) {
        if (deviceUnsupported) return@withContext
        register(core, dataDir)
        if (ready(dataDir)) {
            progress(1.0)
            return@withContext
        }
        val needModel = !modelInstalled(dataDir)
        val needPack = !VoicePack.installed(dataDir)
        val totalExpected = (if (needModel) MODEL_BYTES else 0L) + (if (needPack) VoicePack.BYTES else 0L)
        val modelsDir = File(dataDir, "models").apply { mkdirs() }
        if (modelsDir.usableSpace < totalExpected * 2) throw IOException("ENOSPC")

        var completedBytes = 0L
        if (needModel) {
            downloadAndUnpackZip(
                url = MODEL_URL,
                digest = MODEL_DIGEST,
                expectedBytes = MODEL_BYTES,
                name = NAME,
                modelsDir = modelsDir,
                targetDir = folder(dataDir),
            ) { done, _ ->
                progress((completedBytes + done).toDouble() / totalExpected.coerceAtLeast(1L))
            }
            completedBytes += MODEL_BYTES
            runCatching { core.liveVoiceSetDir(folder(dataDir).path) }
            // Cloned and designed cards can run as soon as live-zipvoice-1 is unpacked.
            readyEvents.tryEmit(Unit)
        }

        if (needPack) {
            downloadAndUnpackZip(
                url = PACK_URL,
                digest = PACK_DIGEST,
                expectedBytes = VoicePack.BYTES,
                name = VoicePack.NAME,
                modelsDir = modelsDir,
                targetDir = VoicePack.folder(dataDir),
            ) { done, packTotal ->
                val denom = (if (needModel) MODEL_BYTES else 0L) + packTotal
                progress((completedBytes + done).toDouble() / denom.coerceAtLeast(1L))
            }
            VoicePack.register(core, dataDir)
        }
        progress(1.0)
    }

    private suspend fun downloadAndUnpackZip(
        url: String,
        digest: String,
        expectedBytes: Long,
        name: String,
        modelsDir: File,
        targetDir: File,
        onBytes: (Long, Long) -> Unit,
    ) {
        val temp = File(modelsDir, ".$name.zip.part")
        val unpack = File(modelsDir, "unpack-$name")
        val asset = url.substringAfterLast('/')
        val urls = ModelDownload.candidateUrls(asset = asset, officialUrl = url)
        try {
            ModelDownload.downloadWithResume(
                urls = urls,
                partFile = temp,
                expectedBytes = expectedBytes,
                expectedBlake3 = digest.takeIf { it.isNotEmpty() },
                onBytes = onBytes,
            )
            unpack.deleteRecursively()
            uniffi.yuyue_core.unpackModel(temp.path, unpack.path)
            val inner = File(unpack, name).takeIf { it.isDirectory } ?: unpack
            targetDir.deleteRecursively()
            targetDir.parentFile?.mkdirs()
            if (!inner.renameTo(targetDir)) {
                inner.copyRecursively(targetDir, overwrite = true)
            }
            temp.delete()
        } finally {
            unpack.deleteRecursively()
        }
    }
}
