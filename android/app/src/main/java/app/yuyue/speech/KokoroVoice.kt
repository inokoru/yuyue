package app.yuyue.speech

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import android.app.ActivityManager
import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.YuyueApp
import app.yuyue.t
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.KokoroSpeaker
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.nio.LongBuffer
import java.nio.channels.FileChannel
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicLong
import kotlin.coroutines.coroutineContext

/**
 * 语阅跨端轻量离线语音 (`models/kokoro-v1_1-1/`, `Kokoro-82M-v1.1-zh Int8`, Apache-2.0).
 *
 * 125 MB 打包体积，统一走 ONNX Runtime 单次前向合成 24 kHz 音频，内含 16 个精选的中英自然音色。
 * 支持多源自动回退（魔搭 / GitHub Releases / HuggingFace 镜像）与 BLAKE3 强校验。
 */
object KokoroModel {
    const val NAME = "kokoro-v1_1-1"
    const val ZIP_DIGEST = "0fb9a0af735001f293549bfcf096c0a2e834472b419ecdbbd1d83531c3b791a3"
    const val ZIP_BYTES = 131_005_940L
    const val TOTAL_MB = 125L

    private const val HF_REPO = "csukuangfj/kokoro-int8-multi-lang-v1_1"
    private const val MIN_RAM_BYTES = 2_800_000_000L

    private data class DirectFile(
        val localName: String,
        val remoteName: String,
        val bytes: Long,
        val blake3: String,
    )

    private val directFiles = listOf(
        DirectFile("model.int8.onnx", "model.int8.onnx", 114_203_914L, "9c85d5920c66ca9ff88844b0de859df9badc29a3cada0e3696b993f111cf58b6"),
        DirectFile("voices.bin", "voices.bin", 53_790_720L, "07d6924043f9d4b7fb13149f74ee702321c9a02baadf743bc9c307499471bcd6"),
        DirectFile("tokens.txt", "tokens.txt", 1_111L, "6f6916d8b098628f18a2d4c66cf9a38af6de85e78ba8343a8787ff4bd9630885"),
        DirectFile("lexicon-zh.txt", "lexicon-zh.txt", 2_120_131L, "d1c4160a6ffcee2fdba5b1a42c3c358f0eec8063935cac22db341ebfee782950"),
        DirectFile("lexicon-us-en.txt", "lexicon-us-en.txt", 5_956_885L, "6c7e13b534ce7bf20884295e1ae3687010ed444ad01ef9a101d23a9dbef3a01e"),
    )

    sealed interface State {
        data object Idle : State
        data object Metered : State
        data class Downloading(val progress: Double) : State
        data object Ready : State
        data object Failed : State
        data object Unsupported : State
    }

    var deviceUnsupported by mutableStateOf(false)
        private set

    var state by mutableStateOf<State>(State.Idle)
        private set

    val readyEvents = MutableSharedFlow<Unit>(extraBufferCapacity = 4)

    private val scope: CoroutineScope = MainScope()
    private var downloadJob: Job? = null
    private val downloadRunId = AtomicLong(0L)

    internal fun applyDownloadProgress(runId: Long, frac: Double): Boolean {
        if (downloadRunId.get() != runId) return false
        if (state !is State.Downloading) return false
        if (frac >= 1.0) return false
        state = State.Downloading(frac.coerceIn(0.0, 0.99))
        return true
    }

    internal fun beginDownloadRun(): Long {
        val runId = downloadRunId.incrementAndGet()
        state = State.Downloading(0.0)
        return runId
    }

    internal fun finishDownloadRun(runId: Long, terminalState: State) {
        downloadRunId.compareAndSet(runId, runId + 1)
        state = terminalState
    }

    fun folder(dataDir: File) = File(dataDir, "models/$NAME")

    fun installed(dataDir: File): Boolean {
        val dir = folder(dataDir)
        return directFiles.all { File(dir, it.localName).isFile }
    }

    fun canRun(dataDir: File): Boolean = !deviceUnsupported && installed(dataDir)

    fun init(context: Context, core: YuyueCore, dataDir: File) {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager
        val info = ActivityManager.MemoryInfo()
        am?.getMemoryInfo(info)
        deviceUnsupported = info.totalMem in 1 until MIN_RAM_BYTES
        if (deviceUnsupported) {
            state = State.Unsupported
            return
        }
        scope.launch {
            val isInstalled = withContext(Dispatchers.IO) {
                if (installed(dataDir)) {
                    runCatching { core.kokoroSetDir(folder(dataDir).path) }
                    true
                } else false
            }
            if (isInstalled && state !is State.Ready) state = State.Ready
        }
    }

    fun register(core: YuyueCore, dataDir: File) {
        if (deviceUnsupported) return
        if (installed(dataDir)) {
            runCatching { core.kokoroSetDir(folder(dataDir).path) }
            if (state !is State.Ready) {
                if (android.os.Looper.myLooper() == android.os.Looper.getMainLooper()) {
                    state = State.Ready
                } else {
                    scope.launch { if (state !is State.Ready) state = State.Ready }
                }
            }
        }
    }

    fun refresh(dataDir: File) {
        if (deviceUnsupported) {
            downloadRunId.incrementAndGet()
            state = State.Unsupported
            return
        }
        if (state is State.Downloading) return
        downloadRunId.incrementAndGet()
        state = if (installed(dataDir)) State.Ready else State.Idle
    }

    fun voices(core: YuyueCore, dataDir: File, tag: String? = null): List<KokoroVoice> {
        if (!canRun(dataDir)) return emptyList()
        register(core, dataDir)
        val all = runCatching { core.kokoroVoices() }.getOrDefault(emptyList()).map { KokoroVoice(it) }
        if (tag.isNullOrEmpty()) return all
        return all.filter { it.speaks(tag) }
    }

    fun defaultNarrator(tag: String): KokoroVoice? {
        if (!CardVoice.speaksLanguage(tag)) return null
        val app = runCatching { YuyueApp.instance }.getOrNull() ?: return null
        val dataDir = app.library.dataDir
        val core = app.library.core
        val list = voices(core, dataDir, tag)
        if (list.isEmpty()) return null
        val primary = if (tag.lowercase().startsWith("en")) "kokoro.af_maple" else "kokoro.zf_001"
        return list.firstOrNull { it.id == primary } ?: list.firstOrNull { it.speaker.narrator } ?: list.first()
    }

    fun onFirstListen(context: Context, core: YuyueCore, dataDir: File) {
        if (deviceUnsupported || installed(dataDir) || downloadJob != null || state is State.Failed) return
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        val caps = cm?.getNetworkCapabilities(cm.activeNetwork)
        val wifi = caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == true
        if (wifi) {
            startDownload(core, dataDir)
        } else {
            state = State.Metered
        }
    }

    fun startDownload(core: YuyueCore, dataDir: File) {
        if (deviceUnsupported || downloadJob != null) return
        if (installed(dataDir)) {
            downloadRunId.incrementAndGet()
            register(core, dataDir)
            state = State.Ready
            return
        }
        val runId = beginDownloadRun()
        downloadJob = scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    ensureInstalled(dataDir) { frac ->
                        if (frac < 1.0 && downloadRunId.get() == runId) {
                            scope.launch { applyDownloadProgress(runId, frac) }
                        }
                    }
                    register(core, dataDir)
                }
                finishDownloadRun(runId, State.Ready)
                readyEvents.tryEmit(Unit)
            } catch (e: CancellationException) {
                downloadRunId.incrementAndGet()
                if (!installed(dataDir)) state = State.Idle
                throw e
            } catch (_: Throwable) {
                finishDownloadRun(runId, State.Failed)
            } finally {
                downloadJob = null
            }
        }
    }

    private suspend fun ensureInstalled(dataDir: File, onProgress: (Double) -> Unit) {
        if (installed(dataDir)) {
            onProgress(1.0)
            return
        }
        val modelsDir = File(dataDir, "models").apply { mkdirs() }
        val dst = folder(dataDir)
        val zipPart = File(modelsDir, ".$NAME.zip.part")
        val zipUrls = ModelDownload.candidateUrls("$NAME.zip")

        // 1. 优先尝试整包下载（魔搭 / GitHub Releases）
        val zipOk = runCatching {
            ModelDownload.downloadWithResume(
                urls = zipUrls,
                partFile = zipPart,
                expectedBytes = ZIP_BYTES,
                expectedBlake3 = ZIP_DIGEST,
            ) { done, total ->
                if (total > 0) onProgress(done.toDouble() / total.toDouble())
            }
            val unpack = File(modelsDir, ".unpack-$NAME")
            try {
                unpack.deleteRecursively()
                uniffi.yuyue_core.unpackModel(zipPart.path, unpack.path)
                val inner = File(unpack, NAME).takeIf { it.isDirectory } ?: unpack
                dst.deleteRecursively()
                if (!inner.renameTo(dst)) inner.copyRecursively(dst, overwrite = true)
            } finally {
                unpack.deleteRecursively()
                zipPart.delete()
            }
            installed(dataDir)
        }.getOrDefault(false)

        if (zipOk) {
            onProgress(1.0)
            return
        }

        // 2. 回退至 HuggingFace 镜像（hf-mirror.com / huggingface.co）逐文件校验下载
        val stageDir = File(modelsDir, ".kokoro-stage").apply { mkdirs() }
        val totalBytes = directFiles.sumOf { it.bytes }.coerceAtLeast(1L)
        var finishedBytes = 0L
        val cnFirst = ModelDownload.prefersChinaMirror()

        for (f in directFiles) {
            coroutineContext.ensureActive()
            val target = File(stageDir, f.localName)
            val part = File(stageDir, ".${f.localName}.part")
            val verifiedDigest = f.blake3
            if (target.isFile && runCatching { uniffi.yuyue_core.fileDigest(target.path).equals(verifiedDigest, ignoreCase = true) }.getOrDefault(false)) {
                finishedBytes += f.bytes
                onProgress(finishedBytes.toDouble() / totalBytes.toDouble())
                continue
            }
            val mirror = "https://hf-mirror.com/$HF_REPO/resolve/main/${f.remoteName}"
            val official = "https://huggingface.co/$HF_REPO/resolve/main/${f.remoteName}"
            val urls = if (cnFirst) listOf(mirror, official) else listOf(official, mirror)
            ModelDownload.downloadWithResume(
                urls = urls,
                partFile = part,
                expectedBytes = f.bytes,
                expectedBlake3 = verifiedDigest,
            ) { done, _ ->
                onProgress((finishedBytes + done).toDouble() / totalBytes.toDouble())
            }
            target.delete()
            if (!part.renameTo(target)) {
                part.copyTo(target, overwrite = true)
                part.delete()
            }
            finishedBytes += f.bytes
            onProgress(finishedBytes.toDouble() / totalBytes.toDouble())
        }

        dst.deleteRecursively()
        if (!stageDir.renameTo(dst)) {
            stageDir.copyRecursively(dst, overwrite = true)
            stageDir.deleteRecursively()
        }
        onProgress(1.0)
    }
}

class KokoroVoice(val speaker: KokoroSpeaker) : BookVoice {
    override val id: String = speaker.id
    override val name: String = speaker.name
    override val qualityName: String get() = t("Kokoro 离线")
    override val isOffline: Boolean = true
    val summaryLine: String get() = speaker.summary

    override fun speaks(tag: String): Boolean = Voices.cardMatchesTag(speaker.lang, tag)

    override suspend fun render(text: String, language: String): Speech? = withContext(Dispatchers.Default) {
        val plain = CardVoice.plainSpokenText(text)
        if (plain.none { it.isLetterOrDigit() }) return@withContext null
        val app = runCatching { YuyueApp.instance }.getOrNull() ?: return@withContext null
        val core = app.library.core
        val dataDir = app.library.dataDir
        if (!KokoroModel.canRun(dataDir)) return@withContext null

        Engine.readCache(app.cacheDir, speaker.id, language, text)?.let { return@withContext it }
        val speech = Engine.synthesize(core, dataDir, speaker, text, language)
        if (speech != null) {
            Engine.writeCache(app.cacheDir, speaker.id, language, text, speech)
            return@withContext speech
        }
        throw IllegalStateException(t("离线语音合成失败，请重试或换一个声音"))
    }

    object Engine {
        private const val SAMPLE_RATE = 24_000
        private const val STYLE_DIM = 256
        private const val STYLE_MAX_LEN = 510
        private const val CLAUSE_GAP_SAMPLES = 2_400 // 0.10 s at 24 kHz
        private const val MAX_CACHE_BYTES = 180L * 1024L * 1024L

        private class Loaded(
            val dirPath: String,
            val session: OrtSession,
            val voicesBuf: FloatBuffer,
            val voiceCount: Int,
        ) {
            fun close() {
                runCatching { session.close() }
            }
        }

        private val lock = Mutex()
        private var loaded: Loaded? = null
        private var writes = 0

        @Synchronized
        fun close() {
            loaded?.close()
            loaded = null
        }

        @Synchronized
        private fun openIfNeeded(core: YuyueCore, dataDir: File): Loaded? {
            if (!KokoroModel.canRun(dataDir)) return null
            KokoroModel.register(core, dataDir)
            val dir = KokoroModel.folder(dataDir)
            loaded?.let { if (it.dirPath == dir.path) return it }
            loaded?.close()
            loaded = null
            return runCatching {
                val env = OrtEnvironment.getEnvironment()
                val threads = minOf(4, maxOf(2, Runtime.getRuntime().availableProcessors() - 1))
                val session = OrtSession.SessionOptions().apply {
                    setCPUArenaAllocator(false)
                    setMemoryPatternOptimization(false)
                    runCatching { addConfigEntry("session.use_env_allocators", "0") }
                    setIntraOpNumThreads(threads)
                }.use { env.createSession(File(dir, "model.int8.onnx").path, it) }

                val voicesFile = File(dir, "voices.bin")
                val mapped = RandomAccessFile(voicesFile, "r").use { raf ->
                    raf.channel.map(FileChannel.MapMode.READ_ONLY, 0L, voicesFile.length())
                }
                mapped.order(ByteOrder.LITTLE_ENDIAN)
                val floats = mapped.asFloatBuffer()
                val stride = STYLE_MAX_LEN * STYLE_DIM
                val count = floats.capacity() / stride
                if (count <= 0) {
                    session.close()
                    return null
                }
                Loaded(dir.path, session, floats, count)
            }.getOrNull()?.also { loaded = it }
        }

        suspend fun synthesize(
            core: YuyueCore,
            dataDir: File,
            speaker: KokoroSpeaker,
            text: String,
            language: String,
        ): Speech? = lock.withLock {
            val h = openIfNeeded(core, dataDir) ?: return@withLock null
            val sid = speaker.sid.toInt()
            if (sid !in 0 until h.voiceCount) return@withLock null
            val clauses = runCatching { core.kokoroClauses(text, language) }.getOrDefault(emptyList())
            if (clauses.isEmpty()) return@withLock null

            val env = OrtEnvironment.getEnvironment()
            val chunks = ArrayList<FloatArray>(clauses.size)
            var total = 0

            for (clause in clauses) {
                coroutineContext.ensureActive()
                val inner = runCatching { core.kokoroTokens(clause, language) }.getOrDefault(emptyList())
                if (inner.isEmpty()) continue
                val capped = if (inner.size > STYLE_MAX_LEN) inner.subList(0, STYLE_MAX_LEN) else inner
                val padded = LongArray(capped.size + 2)
                for (i in capped.indices) padded[i + 1] = capped[i]

                val lenIdx = minOf(capped.size, STYLE_MAX_LEN - 1)
                val styleOffset = (sid * STYLE_MAX_LEN + lenIdx) * STYLE_DIM
                val styleVec = FloatArray(STYLE_DIM)
                val dup = h.voicesBuf.duplicate()
                dup.position(styleOffset)
                dup.get(styleVec, 0, STYLE_DIM)

                val rawAudio = OnnxTensor.createTensor(env, LongBuffer.wrap(padded), longArrayOf(1L, padded.size.toLong())).use { tokT ->
                    OnnxTensor.createTensor(env, FloatBuffer.wrap(styleVec), longArrayOf(1L, STYLE_DIM.toLong())).use { styleT ->
                        OnnxTensor.createTensor(env, FloatBuffer.wrap(floatArrayOf(1.0f)), longArrayOf(1L)).use { speedT ->
                            h.session.run(
                                mapOf(
                                    "tokens" to tokT,
                                    "style" to styleT,
                                    "speed" to speedT,
                                )
                            ).use { res ->
                                val outT = res[0] as OnnxTensor
                                val fb = outT.floatBuffer
                                FloatArray(fb.remaining()).also { fb.get(it) }
                            }
                        }
                    }
                }
                if (rawAudio.isNotEmpty()) {
                    if (chunks.isNotEmpty()) total += CLAUSE_GAP_SAMPLES
                    chunks.add(rawAudio)
                    total += rawAudio.size
                }
            }

            if (chunks.isEmpty() || total == 0) return@withLock null
            val merged = if (chunks.size == 1) chunks[0] else FloatArray(total).also { dst ->
                var pos = 0
                for ((i, c) in chunks.withIndex()) {
                    if (i > 0) pos += CLAUSE_GAP_SAMPLES
                    c.copyInto(dst, pos)
                    pos += c.size
                }
            }
            val norm = runCatching { core.kokoroPostprocess(merged.asList(), SAMPLE_RATE.toUInt()) }
                .getOrNull() ?: return@withLock null
            if (norm.isEmpty()) return@withLock null
            val shorts = ShortArray(norm.size) {
                (norm[it].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
            }
            Speech(shorts, SAMPLE_RATE)
        }

        private fun cacheFile(cacheRoot: File, speakerId: String, language: String, text: String): File {
            val md = MessageDigest.getInstance("SHA-256")
            md.update("kokoro-v1_1|$speakerId|$language|$text".toByteArray())
            val hex = md.digest().take(16).joinToString("") { "%02x".format(it) }
            return File(cacheRoot, "kokoro/$hex.pcm")
        }

        fun readCache(cacheRoot: File, speakerId: String, language: String, text: String): Speech? {
            val f = cacheFile(cacheRoot, speakerId, language, text)
            if (!f.isFile) return null
            val bytes = runCatching { f.readBytes() }.getOrNull() ?: return null
            if (bytes.size < 4 || bytes.size % 2 != 0) return null
            runCatching { f.setLastModified(System.currentTimeMillis()) }
            val sb = java.nio.ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer()
            val shorts = ShortArray(sb.remaining()).also { sb.get(it) }
            return Speech(shorts, SAMPLE_RATE)
        }

        fun writeCache(cacheRoot: File, speakerId: String, language: String, text: String, speech: Speech) {
            runCatching {
                val f = cacheFile(cacheRoot, speakerId, language, text)
                f.parentFile?.mkdirs()
                val bb = java.nio.ByteBuffer.allocate(speech.samples.size * 2).order(ByteOrder.LITTLE_ENDIAN)
                bb.asShortBuffer().put(speech.samples)
                f.writeBytes(bb.array())
                if (++writes % 24 == 0) {
                    CardVoice.trimFolderToBytes(f.parentFile!!, MAX_CACHE_BYTES)
                }
            }
        }
    }
}
