package app.yuyue.speech

import ai.onnxruntime.OnnxTensor
import ai.onnxruntime.OrtEnvironment
import ai.onnxruntime.OrtSession
import app.yuyue.YuyueApp
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.CardOrigin
import uniffi.yuyue_core.CardStyle
import uniffi.yuyue_core.VoiceCard
import uniffi.yuyue_core.YuyueCore
import uniffi.yuyue_core.ZipVoicePrompt
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.FloatBuffer
import java.nio.LongBuffer
import java.security.MessageDigest
import kotlin.coroutines.coroutineContext

/**
 * 声音卡语音 (`CardVoice`): runs `ZipVoice-Distill-Int8` + `Vocos 24 kHz` on-device via
 * pure ONNX Runtime (arena and memory pattern disabled to keep peak RSS ≤ 600 MB), conditioning on
 * up to seven emotional reference clips per `VoiceCard`.
 */
class CardVoice(
    val card: VoiceCard,
    val bookID: String? = (card.origin as? CardOrigin.Designed)?.book,
) : BookVoice {
    override val id: String = card.id
    override val name: String = card.name
    override val qualityName: String?
        get() = if (card.origin is CardOrigin.Cloned) t("克隆") else null
    override val isOffline: Boolean = true

    override fun speaks(tag: String): Boolean = speaksLanguage(tag)

    override suspend fun render(text: String, language: String): Speech? =
        render(text, language, CardStyle.NEUTRAL)

    suspend fun render(text: String, language: String, style: CardStyle): Speech? = withContext(Dispatchers.Default) {
        val plain = plainSpokenText(text)
        if (plain.none { it.isLetterOrDigit() }) return@withContext null
        val app = runCatching { YuyueApp.instance }.getOrNull() ?: return@withContext null
        val core = app.library.core
        val dataDir = app.library.dataDir

        val cached = Engine.readSentenceCache(app.cacheDir, bookID, card.id, card.version, style, language, text)
        if (cached != null) return@withContext cached

        val speech = Engine.synthesizeWithGate(
            core = core,
            dataDir = dataDir,
            card = card,
            bookID = bookID,
            style = style,
            text = text,
            plainText = plain,
            language = language,
        )
        if (speech != null) {
            Engine.writeSentenceCache(app.cacheDir, bookID, card.id, card.version, style, language, text, speech)
            return@withContext speech
        }
        // 不静默切换系统声音，避免读到一半突然换人；明确报错交由播放器告知用户。
        throw IllegalStateException(t("本机声音卡这一句合成失败，请重试或换一个声音"))
    }

    companion object {
        const val SAMPLE_RATE = 24_000
        private const val CLAUSE_PAUSE_SAMPLES = 2_880 // 0.12 s at 24 kHz
        const val MAX_BOOK_CACHE_BYTES = 500L * 1024L * 1024L // 500 MB

        /** ZipVoice speaks Mandarin Chinese and English; Japanese/Cantonese/other tags stay on system voices. */
        fun speaksLanguage(tag: String): Boolean {
            val t = tag.replace('_', '-').lowercase().trim()
            if (t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo")) return false
            return t == "zh" || t.startsWith("zh-") || t == "cmn" || t == "en" || t.startsWith("en-")
        }

        /** Strips `{词|jiǎ zhēn}` reading annotations down to plain display text for duration/fallback checks. */
        internal fun plainSpokenText(text: String): String {
            if ('{' !in text) return text
            val sb = StringBuilder(text.length)
            var rest = text
            while (true) {
                val a = rest.indexOf('{')
                if (a < 0) break
                val b = rest.indexOf('}', startIndex = a + 1)
                if (b < 0) break
                sb.append(rest, 0, a)
                val inner = rest.substring(a + 1, b)
                val pipe = inner.indexOf('|')
                if (pipe >= 0) sb.append(inner, 0, pipe) else sb.append(rest, a, b + 1)
                rest = rest.substring(b + 1)
            }
            sb.append(rest)
            return sb.toString()
        }

        /** Evicts oldest files in `folder` when total size exceeds `maxBytes`. */
        internal fun trimFolderToBytes(folder: File, maxBytes: Long = MAX_BOOK_CACHE_BYTES) {
            val files = folder.listFiles()?.filter { it.isFile } ?: return
            var total = files.sumOf { it.length() }
            if (total <= maxBytes) return
            for (f in files.sortedBy { it.lastModified() }) {
                val len = f.length()
                if (f.delete()) {
                    total -= len
                    if (total <= maxBytes * 9 / 10) break
                }
            }
        }
    }

    /** Bounded LRU cache used for reference `ZipVoicePrompt`s (max 16 entries). */
    internal class PromptLruCache<K, V>(private val maxEntries: Int = 16) {
        private val map = object : LinkedHashMap<K, V>(maxEntries, 0.75f, true) {
            override fun removeEldestEntry(eldest: MutableMap.MutableEntry<K, V>?): Boolean = size > maxEntries
        }

        @Synchronized
        fun get(key: K): V? = map[key]

        @Synchronized
        fun put(key: K, value: V) {
            map[key] = value
        }

        @Synchronized
        fun removeIf(predicate: (K) -> Boolean) {
            val it = map.entries.iterator()
            while (it.hasNext()) {
                if (predicate(it.next().key)) it.remove()
            }
        }

        @Synchronized
        fun size(): Int = map.size
    }

    internal data class PromptKey(val cardId: String, val version: UInt, val style: CardStyle)

    object Engine {
        private class Sessions(
            val dirPath: String,
            val encoder: OrtSession,
            val decoder: OrtSession,
            val vocoder: OrtSession,
        ) {
            fun close() {
                runCatching { encoder.close() }
                runCatching { decoder.close() }
                runCatching { vocoder.close() }
            }
        }

        private val inferLock = Mutex()
        private var loaded: Sessions? = null
        private val prompts = PromptLruCache<PromptKey, ZipVoicePrompt>(16)
        private var writesSinceTrim = 0

        /** Clears cached prompts for `cardId` after re-recording or deletion. */
        fun invalidateCard(cardId: String) {
            prompts.removeIf { it.cardId == cardId }
        }

        @Synchronized
        private fun sessions(core: YuyueCore, dataDir: File): Sessions? {
            if (!LiveVoiceModel.canRun(dataDir)) return null
            LiveVoiceModel.register(core, dataDir)
            val dir = LiveVoiceModel.folder(dataDir)
            loaded?.let { if (it.dirPath == dir.path) return it }
            loaded?.close()
            loaded = null
            return runCatching {
                val env = OrtEnvironment.getEnvironment()
                val threads = minOf(4, maxOf(2, Runtime.getRuntime().availableProcessors() - 1))
                fun makeOptions() = OrtSession.SessionOptions().apply {
                    setCPUArenaAllocator(false)
                    setMemoryPatternOptimization(false)
                    runCatching { addConfigEntry("session.use_env_allocators", "0") }
                    setIntraOpNumThreads(threads)
                }
                val enc = makeOptions().use { env.createSession(File(dir, "encoder.int8.onnx").path, it) }
                val dec = makeOptions().use { env.createSession(File(dir, "decoder.int8.onnx").path, it) }
                val voc = makeOptions().use { env.createSession(File(dir, "vocos_24khz.onnx").path, it) }
                Sessions(dir.path, enc, dec, voc)
            }.getOrNull()?.also { loaded = it }
        }

        private fun resolvePrompt(
            core: YuyueCore,
            card: VoiceCard,
            bookID: String?,
            style: CardStyle,
        ): ZipVoicePrompt? {
            val clip = core.cardClipResolved(bookID, card.id, style) ?: return null
            val key = PromptKey(card.id, card.version, clip.style)
            prompts.get(key)?.let { return it }
            val built = runCatching { core.liveVoicePrompt(clip.file, clip.text, card.lang) }.getOrNull() ?: return null
            prompts.put(key, built)
            return built
        }

        /**
         * Synthesizes `text` with lightweight quality gating (retries once with `seed + 1u` if
         * duration, silence, or clipping check fails; returns null if both attempts fail).
         */
        suspend fun synthesizeWithGate(
            core: YuyueCore,
            dataDir: File,
            card: VoiceCard,
            bookID: String?,
            style: CardStyle,
            text: String,
            plainText: String,
            language: String,
        ): Speech? = inferLock.withLock {
            val sess = sessions(core, dataDir) ?: return@withLock null
            val prompt = resolvePrompt(core, card, bookID, style) ?: return@withLock null
            val clauses = core.liveVoiceClauses(text, language)
            if (clauses.isEmpty()) return@withLock null

            val baseSeed = ((card.id.hashCode().toLong() and 0xFFFF_FFFFL) xor
                ((text.hashCode().toLong() and 0xFFFF_FFFFL) shl 13)).toULong() or 1uL

            var bestFloats: FloatArray? = null
            for (attempt in 0..2) {
                coroutineContext.ensureActive()
                val seed = baseSeed + attempt.toULong() * 0x9E37_79B9_7F4A_7C15uL
                val floats = runCatching {
                    synthesizeClauses(core, sess, prompt, clauses, language, seed)
                }.getOrNull()
                if (floats != null && floats.isNotEmpty()) {
                    val ok = core.liveVoiceCheck(
                        floats.asList(),
                        SAMPLE_RATE.toUInt(),
                        plainText,
                        language,
                        card.rate,
                    )
                    if (ok) {
                        val shorts = ShortArray(floats.size) {
                            (floats[it].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                        }
                        return@withLock Speech(shorts, SAMPLE_RATE)
                    }
                    if (bestFloats == null && isAudible(floats)) {
                        bestFloats = floats
                    }
                }
            }
            // 若三次均未过严格时长/静音门槛但产出了有效人声，保留同一声卡结果，避免中途切回系统音。
            bestFloats?.let { floats ->
                val shorts = ShortArray(floats.size) {
                    (floats[it].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                }
                return@withLock Speech(shorts, SAMPLE_RATE)
            }
            null
        }

        private fun isAudible(samples: FloatArray): Boolean {
            if (samples.isEmpty()) return false
            var sumSq = 0.0
            for (s in samples) {
                if (!s.isFinite()) return false
                sumSq += s.toDouble() * s.toDouble()
            }
            return kotlin.math.sqrt(sumSq / samples.size) >= 1e-4
        }

        /**
         * Immediate preview helper for newly recorded cloning samples before saving `VoiceCard`.
         */
        suspend fun renderPreviewWithClip(
            core: YuyueCore,
            dataDir: File,
            clipPath: String,
            clipText: String,
            clipLang: String,
            text: String,
            language: String,
        ): Speech? = withContext(Dispatchers.Default) {
            inferLock.withLock {
                val sess = sessions(core, dataDir) ?: return@withLock null
                val prompt = runCatching { core.liveVoicePrompt(clipPath, clipText, clipLang) }.getOrNull() ?: return@withLock null
                val clauses = core.liveVoiceClauses(text, language)
                if (clauses.isEmpty()) return@withLock null
                val floats = runCatching {
                    synthesizeClauses(core, sess, prompt, clauses, language, 42uL)
                }.getOrNull() ?: return@withLock null
                if (floats.isEmpty()) return@withLock null
                val shorts = ShortArray(floats.size) {
                    (floats[it].coerceIn(-1f, 1f) * 32767f).toInt().toShort()
                }
                Speech(shorts, SAMPLE_RATE)
            }
        }

        private suspend fun synthesizeClauses(
            core: YuyueCore,
            sess: Sessions,
            prompt: ZipVoicePrompt,
            clauses: List<String>,
            language: String,
            seed: ULong,
        ): FloatArray {
            val parts = ArrayList<FloatArray>(clauses.size)
            var totalLen = 0
            for ((idx, clause) in clauses.withIndex()) {
                coroutineContext.ensureActive()
                val pcm = synthesizeOneClause(core, sess, prompt, clause, language, seed + idx.toULong() * 97uL)
                if (pcm.isNotEmpty()) {
                    if (parts.isNotEmpty()) totalLen += CLAUSE_PAUSE_SAMPLES
                    parts.add(pcm)
                    totalLen += pcm.size
                }
            }
            if (parts.isEmpty()) return FloatArray(0)
            if (parts.size == 1) return parts[0]
            val out = FloatArray(totalLen)
            var pos = 0
            for ((idx, p) in parts.withIndex()) {
                if (idx > 0) pos += CLAUSE_PAUSE_SAMPLES // zero-filled 0.12 s gap
                p.copyInto(out, pos)
                pos += p.size
            }
            return out
        }

        private fun synthesizeOneClause(
            core: YuyueCore,
            sess: Sessions,
            prompt: ZipVoicePrompt,
            clause: String,
            language: String,
            seed: ULong,
        ): FloatArray {
            val tokList = core.liveVoiceTokens(clause, language)
            if (tokList.isEmpty() || prompt.promptTokens.isEmpty()) return FloatArray(0)
            val env = OrtEnvironment.getEnvironment()
            val tokens = LongArray(tokList.size) { tokList[it] }
            val promptTokens = LongArray(prompt.promptTokens.size) { prompt.promptTokens[it] }
            val promptFrames = prompt.promptFrames.toInt()

            // 1. Encoder -> text_condition [1, T_tot, 100]
            val (tTot, tcData) = OnnxTensor.createTensor(env, LongBuffer.wrap(tokens), longArrayOf(1L, tokens.size.toLong())).use { tokT ->
                OnnxTensor.createTensor(env, LongBuffer.wrap(promptTokens), longArrayOf(1L, promptTokens.size.toLong())).use { pTokT ->
                    OnnxTensor.createTensor(env, LongBuffer.wrap(longArrayOf(promptFrames.toLong())), longArrayOf()).use { pLenT ->
                        OnnxTensor.createTensor(env, FloatBuffer.wrap(floatArrayOf(1.0f)), longArrayOf()).use { speedT ->
                            sess.encoder.run(
                                mapOf(
                                    "tokens" to tokT,
                                    "prompt_tokens" to pTokT,
                                    "prompt_features_len" to pLenT,
                                    "speed" to speedT,
                                )
                            ).use { encRes ->
                                val outT = encRes[0] as OnnxTensor
                                val frames = outT.info.shape[1].toInt()
                                val fb = outT.floatBuffer
                                val arr = FloatArray(fb.remaining()).also { fb.get(it) }
                                frames to arr
                            }
                        }
                    }
                }
            }
            val kept = tTot - promptFrames
            if (kept <= 0 || tcData.size < tTot * 100) return FloatArray(0)

            // 2. Flow-matching Euler decoder (3 steps, guidance_scale = 3.0)
            val totalElems = tTot * 100
            val noise = core.liveVoiceNoise(totalElems.toUInt(), seed)
            val x = FloatArray(totalElems) { noise[it] }
            val scData = FloatArray(totalElems)
            val copyCount = minOf(promptFrames * 100, prompt.promptFeatures.size, scData.size)
            for (i in 0 until copyCount) scData[i] = prompt.promptFeatures[i]

            val timesteps = core.liveVoiceTimesteps(3u, 0.5f)
            val shape3d = longArrayOf(1L, tTot.toLong(), 100L)
            OnnxTensor.createTensor(env, FloatBuffer.wrap(tcData), shape3d).use { tcT ->
                OnnxTensor.createTensor(env, FloatBuffer.wrap(scData), shape3d).use { scT ->
                    OnnxTensor.createTensor(env, FloatBuffer.wrap(floatArrayOf(3.0f)), longArrayOf()).use { gsT ->
                        for (step in 0 until (timesteps.size - 1)) {
                            val tCur = timesteps[step]
                            val dt = timesteps[step + 1] - tCur
                            OnnxTensor.createTensor(env, FloatBuffer.wrap(floatArrayOf(tCur)), longArrayOf()).use { tT ->
                                OnnxTensor.createTensor(env, FloatBuffer.wrap(x), shape3d).use { xT ->
                                    sess.decoder.run(
                                        mapOf(
                                            "t" to tT,
                                            "x" to xT,
                                            "text_condition" to tcT,
                                            "speech_condition" to scT,
                                            "guidance_scale" to gsT,
                                        )
                                    ).use { decRes ->
                                        val vT = decRes[0] as OnnxTensor
                                        val vb = vT.floatBuffer
                                        for (i in 0 until totalElems) {
                                            x[i] += vb.get(i) * dt
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            // 3. Transpose generated suffix [kept, 100] * 10f -> [1, 100, kept] for Vocos 24 kHz
            val mels = FloatArray(100 * kept)
            val baseOff = promptFrames * 100
            for (f in 0 until kept) {
                val row = baseOff + f * 100
                for (b in 0 until 100) {
                    mels[b * kept + f] = x[row + b] * 10f
                }
            }
            val (mag, xCos, ySin) = OnnxTensor.createTensor(env, FloatBuffer.wrap(mels), longArrayOf(1L, 100L, kept.toLong())).use { melT ->
                sess.vocoder.run(mapOf("mels" to melT)).use { vocRes ->
                    val magT = (vocRes.get("mag").orElse(null) as? OnnxTensor) ?: (vocRes[0] as OnnxTensor)
                    val xT = (vocRes.get("x").orElse(null) as? OnnxTensor) ?: (vocRes[1] as OnnxTensor)
                    val yT = (vocRes.get("y").orElse(null) as? OnnxTensor) ?: (vocRes[2] as OnnxTensor)
                    fun takeFloats(t: OnnxTensor): FloatArray {
                        val fb = t.floatBuffer
                        return FloatArray(fb.remaining()).also { fb.get(it) }
                    }
                    Triple(takeFloats(magT), takeFloats(xT), takeFloats(yT))
                }
            }

            // 4. Vocos ISTFT + −20 LUFS loudness normalization in Rust core
            return core.liveVoiceIstft(mag.asList(), xCos.asList(), ySin.asList(), kept.toUInt()).toFloatArray()
        }

        private fun sentenceCacheFile(
            cacheRoot: File,
            bookID: String?,
            cardId: String,
            version: UInt,
            style: CardStyle,
            language: String,
            text: String,
        ): File {
            val scope = (bookID ?: "shared").replace('/', '_')
            val rawKey = "$cardId@$version/${style.name}\u0001$language\u0001$text"
            val hash = MessageDigest.getInstance("SHA-256")
                .digest(rawKey.toByteArray())
                .take(16)
                .joinToString("") { "%02x".format(it) }
            return File(cacheRoot, "live-voices/$scope/$hash.wav")
        }

        fun readSentenceCache(
            cacheRoot: File,
            bookID: String?,
            cardId: String,
            version: UInt,
            style: CardStyle,
            language: String,
            text: String,
        ): Speech? {
            val f = sentenceCacheFile(cacheRoot, bookID, cardId, version, style, language, text)
            if (!f.isFile) return null
            f.setLastModified(System.currentTimeMillis())
            return SystemVoice.Engine.wav(f)
        }

        fun writeSentenceCache(
            cacheRoot: File,
            bookID: String?,
            cardId: String,
            version: UInt,
            style: CardStyle,
            language: String,
            text: String,
            speech: Speech,
        ) {
            runCatching {
                val f = sentenceCacheFile(cacheRoot, bookID, cardId, version, style, language, text)
                val folder = f.parentFile?.apply { mkdirs() } ?: return
                val pcmBytes = ByteArray(speech.samples.size * 2)
                ByteBuffer.wrap(pcmBytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer().put(speech.samples)
                f.writeBytes(CloudVoice.wav(pcmBytes, speech.rate))
                if (++writesSinceTrim >= 16) {
                    writesSinceTrim = 0
                    trimFolderToBytes(folder, MAX_BOOK_CACHE_BYTES)
                }
            }
        }
    }
}
