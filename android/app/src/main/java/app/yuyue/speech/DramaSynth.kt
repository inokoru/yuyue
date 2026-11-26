package app.yuyue.speech

import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.CharacterAge
import uniffi.yuyue_core.CharacterGender
import uniffi.yuyue_core.ClipAsk
import uniffi.yuyue_core.ClipCheck
import uniffi.yuyue_core.DramaMixer
import uniffi.yuyue_core.DramaResult
import uniffi.yuyue_core.DramaTask
import uniffi.yuyue_core.MadeClip
import uniffi.yuyue_core.MixSource
import uniffi.yuyue_core.TimelineItem
import uniffi.yuyue_core.VoiceInfo
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 广播剧's voices on Android, as on Apple: the core casts the book with the voices this phone
 * has, then hands out each clip to make (a stretch in one voice); this renders it, keeps it at
 * the mixer's rate beside the book, and hands back how it measured. There's no transcriber that
 * takes a file here, so 回听 checks only what the sound itself shows (quiet ends, clipping,
 * length); the system voices say a text the same way every time. A scene is laid out and mixed
 * by the core's mixer.
 */
class DramaSynth(private val context: android.content.Context, private val library: Library) {
    private val loops = mutableMapOf<String, Job>()

    /** Synthesis is wanted (整本提前做好, or a scene being played). */
    var on = false
        private set

    /** A section's clips are all made (the book's id). */
    val sectionMade = kotlinx.coroutines.flow.MutableSharedFlow<String>(extraBufferCapacity = 8)
    /** 广播剧开关 / 旁白提示词 / 情感语气 / 环境音 / 音效 / 后台预合成 changed (the book's id). */
    val settingsChanged = kotlinx.coroutines.flow.MutableSharedFlow<String>(extraBufferCapacity = 8)

    private val planCacheLock = Any()
    private val cachedPlans = HashMap<String, uniffi.yuyue_core.CastingPlan>()

    private val prefs get() = context.getSharedPreferences("drama", android.content.Context.MODE_PRIVATE)
    /** 整本提前做好, by book: kept on this phone (the clips are). */
    fun preparing(book: String) = prefs.getBoolean("prepare.$book", false)
    fun setPreparing(book: String, on: Boolean) = prefs.edit().putBoolean("prepare.$book", on).apply()

    /** 广播剧是否对该书启用：以同步存储的 `dramaEnabled` 为唯一真源；若已关掉广播剧但本机残留预合成标记，自动清理停止。 */
    fun isDramaActive(book: String): Boolean {
        val enabled = runCatching { library.core.dramaEnabled(book) }.getOrDefault(false)
        if (!enabled && preparing(book)) {
            setPreparing(book, false)
            stop(book)
        }
        return enabled
    }

    fun invalidateCast(book: String? = null) {
        synchronized(planCacheLock) {
            if (book == null) cachedPlans.clear() else cachedPlans.remove(book)
        }
    }

    fun notifySettingsChanged(book: String) {
        invalidateCast(book)
        settingsChanged.tryEmit(book)
    }

    fun liveClipsFolder(book: String) = File(library.dataDir, "drama/$book/live-clips")

    /** 旁白回退升级：与普通朗读 `Voices.best(language)` 保持一致（手选 > Kokoro > LiveVoice > 免 Key 在线 > 系统自带）。 */
    suspend fun effectiveNarrator(language: String, chosen: String?): String? {
        val explicit = chosen?.takeIf { it.isNotEmpty() } ?: Player.savedVoiceId(language).takeIf { it.isNotEmpty() }
        if (explicit != null) return explicit
        if (KokoroModel.canRun(library.dataDir) && CardVoice.speaksLanguage(language)) {
            KokoroModel.defaultNarrator(library.core, library.dataDir, language)?.let { return it.id }
        }
        if (LiveVoiceModel.canRun(library.dataDir) && CardVoice.speaksLanguage(language)) {
            LiveVoiceModel.register(library.core, library.dataDir)
            Voices.pickDefaultNarrator(library.core.cardsBuiltin(), language)?.let { return it.id }
        }
        if (CloudVoice.Provider.Free.isSetUp) {
            val freeList = CloudVoice.all().filter { it.provider == CloudVoice.Provider.Free && it.speaks(language) }
            if (freeList.isNotEmpty()) {
                val preferred = if (language.lowercase().startsWith("en")) "cloud.free.en_us" else "cloud.free.zh_baidu"
                return (freeList.firstOrNull { it.id == preferred } ?: freeList.first()).id
            }
        }
        return Voices.systemBest(language)?.id
    }

    /** Ensures `drama/{book}/plan` is cast and cached in memory for Live playback & CastSheet UI. */
    suspend fun ensurePlan(book: String, language: String, narrator: String?): uniffi.yuyue_core.CastingPlan? {
        return withContext(Dispatchers.Default) {
            SystemVoice.Engine.init(context)
            LiveVoiceModel.register(library.core, library.dataDir)
            val resolvedNarrator = effectiveNarrator(language, narrator)
            synchronized(planCacheLock) { cachedPlans[book] }?.let { cached ->
                if (resolvedNarrator == null || cached.narrator == resolvedNarrator) return@withContext cached
            }
            val voices = voices(language, resolvedNarrator, book)
            if (voices.isEmpty()) return@withContext null
            // Respect the user's chosen narrator voice verbatim (never silently replace cloud/system/cloned/kokoro narrators).
            val narratorID = resolvedNarrator ?: voices.first().id
            val castVoices = SpeakerPrint.printVoices(library.core, library.dataDir, voices, language)
            val plan = runCatching { library.core.dramaCastVoices(book, castVoices, narratorID) }.getOrNull() ?: return@withContext null
            synchronized(planCacheLock) { cachedPlans[book] = plan }
            plan
        }
    }

    /** Debug builds: a sound library zip built here (`-e soundsPath <file>`), taken unchecked. */
    var debugSounds: File? = null

    fun clipsFolder(book: String) = File(library.dataDir, "drama/$book/clips")
    /** A clip's file: AAC, one channel at 48 kHz (`ClipAudio`). */
    fun clipFile(book: String, key: String) = File(clipsFolder(book), "$key.m4a")
    /** Where clips made before they were compressed were kept (raw float); still read. */
    private fun oldClipFile(book: String, key: String) = File(clipsFolder(book), "$key.f32")
    fun clipMade(book: String, key: String) = clipFile(book, key).isFile || oldClipFile(book, key).isFile
    /** Clip `key`'s samples, mono 48 kHz; null when it isn't made. */
    fun readClip(book: String, key: String): FloatArray? =
        ClipAudio.read(clipFile(book, key).takeIf { it.isFile } ?: oldClipFile(book, key))
    /** Keeps `samples` (mono 48 kHz) as clip `key`, capping each book's clip cache at 500 MB. */
    private var clipsWrittenSinceTrim = 0
    fun writeClip(book: String, key: String, samples: FloatArray) {
        ClipAudio.write(samples, clipFile(book, key))
        oldClipFile(book, key).delete()
        if (++clipsWrittenSinceTrim >= 12) {
            clipsWrittenSinceTrim = 0
            CardVoice.trimFolderToBytes(clipsFolder(book), CardVoice.MAX_BOOK_CACHE_BYTES)
        }
    }

    /** True while whole-book synthesis is paused waiting for the phone to be plugged in. */
    @Volatile var waitingForPower = false
        private set

    fun isCharging(): Boolean {
        val bm = context.getSystemService(android.content.Context.BATTERY_SERVICE) as? android.os.BatteryManager
        if (bm != null && bm.isCharging) return true
        val sticky = context.registerReceiver(null, android.content.IntentFilter(android.content.Intent.ACTION_BATTERY_CHANGED))
        val status = sticky?.getIntExtra(android.os.BatteryManager.EXTRA_STATUS, -1) ?: -1
        return status == android.os.BatteryManager.BATTERY_STATUS_CHARGING || status == android.os.BatteryManager.BATTERY_STATUS_FULL
    }

    /** The voices the core casts `language` with:
     *  - LiveVoice cards (+ book-designed + chosen cloned narrator) when LiveVoice is ready.
     *  - Kokoro speakers when Kokoro is ready and either LiveVoice isn't installed or Kokoro is the narrator.
     *  - Cloud voices of the chosen cloud provider when narrator is cloud.
     *  - System voices ONLY when none of the above exist (never pollutes a cloud or Kokoro cast). */
    suspend fun voices(language: String, narrator: String?, book: String? = null): List<VoiceInfo> {
        val out = mutableListOf<VoiceInfo>()
        if (LiveVoiceModel.canRun(library.dataDir) && CardVoice.speaksLanguage(language)) {
            LiveVoiceModel.register(library.core, library.dataDir)
            val builtin = Voices.recommendedCards(library.core, library.dataDir, language)
            val designed = if (book != null) {
                runCatching { library.core.cardsOfBook(book) }.getOrDefault(emptyList())
                    .filter { Voices.cardMatchesTag(it.lang, language) }
            } else emptyList()
            val clonedNarrator = if (narrator?.startsWith("cloned.") == true) {
                listOfNotNull(runCatching { library.core.cardFind(null, narrator) }.getOrNull())
            } else emptyList()
            for (card in builtin + designed + clonedNarrator) {
                out += library.core.cardVoiceInfo(card, card.embedding, true)
            }
        }
        if (KokoroModel.canRun(library.dataDir) && CardVoice.speaksLanguage(language) &&
            (out.isEmpty() || narrator?.startsWith("kokoro.") == true)) {
            out += kokoroVoiceInfos(language)
        }
        if (narrator?.startsWith("cloud.") == true) {
            val provider = narrator.split('.').getOrNull(1)
            out += CloudVoice.all().filter { it.provider.key == provider && it.speaks(language) }.map { v ->
                val gender = when (v.gender) { "male" -> CharacterGender.M; "female" -> CharacterGender.F; else -> CharacterGender.UNKNOWN }
                val age = when (v.age) { "young" -> CharacterAge.YOUNG; "middle_aged", "middle aged" -> CharacterAge.MIDDLE; "old" -> CharacterAge.OLD; else -> CharacterAge.UNKNOWN }
                VoiceInfo(v.id, listOf(v.language ?: language), gender, age, emptyList(), emptyList(), true, true, -20f)
            }
        }
        if (out.isEmpty()) {
            out += systemVoiceInfos(language)
        }
        return out
    }

    private fun kokoroVoiceInfos(language: String): List<VoiceInfo> {
        return KokoroModel.voices(library.core, library.dataDir, language).map { v ->
            val s = v.speaker
            val age = when (s.voiceId) {
                "zm_025", "zm_056", "zm_033", "zm_096", "zf_006", "zf_094", "zf_019" -> CharacterAge.YOUNG
                "zm_010", "zm_012", "zm_035", "zm_041", "zm_080", "zm_095", "zf_004", "zf_079", "zf_090" -> CharacterAge.MIDDLE
                "zm_098", "zf_076" -> CharacterAge.OLD
                else -> CharacterAge.UNKNOWN
            }
            val emb = KokoroModel.styleEmbedding(s.sid, library.dataDir)
            VoiceInfo(
                id = s.id,
                langs = listOf(s.lang),
                gender = s.gender,
                age = age,
                tags = emptyList(),
                embedding = emb,
                acts = false,
                narratorOk = s.narrator,
                loudnessLufs = -20f,
            )
        }
    }

    private suspend fun systemVoiceInfos(language: String): List<VoiceInfo> {
        val allSys = SystemVoice.Engine.voices().filter { it.speaks(language) }
        val named = allSys.filter { !it.isSystemDefault }
        val pool = named.ifEmpty { allSys }
        return (pool.filter { it.isOffline }.ifEmpty { pool }).map { v ->
            val hinted = v.inferredGender
            val gender = if (hinted != CharacterGender.UNKNOWN) hinted else library.core.speakerKnownGender(v.id)
            VoiceInfo(v.id, listOf(language), gender, CharacterAge.UNKNOWN, emptyList(), emptyList(), false, true, -20f)
        }
    }

    /** Casts `book` and makes its clips, nearest `section` first, until the core has nothing
     *  more; `done` hears when it stops. Whole-book background runs (`done == null`) run only
     *  while charging, pausing on unplug and resuming when plugged back in. */
    fun start(book: String, language: String, section: UInt, narrator: String?, done: (() -> Unit)? = null) {
        if (loops[book]?.isActive == true) return
        on = true
        // The system's voices, ready even when nothing has played yet.
        SystemVoice.Engine.init(context)
        LiveVoiceModel.register(library.core, library.dataDir)
        val chosenNarrator = narrator?.takeIf { it.isNotEmpty() } ?: Player.savedVoiceId(language).ifEmpty { null }
        app.yuyue.reader.DramaWork.synthesizing = true
        app.yuyue.reader.DramaWork.cloudCast = chosenNarrator?.startsWith("cloud.") == true
        app.yuyue.reader.DramaWork.tellCore(library.core)
        val core = library.core
        loops[book] = library.scope.launch(Dispatchers.Default) {
            val resolvedNarrator = effectiveNarrator(language, chosenNarrator)
            app.yuyue.reader.DramaWork.cloudCast = resolvedNarrator?.startsWith("cloud.") == true
            app.yuyue.reader.DramaWork.tellCore(core)
            val voices = voices(language, resolvedNarrator, book)
            if (app.yuyue.BuildConfig.DEBUG) android.util.Log.d("DramaSynth", "${voices.size} voices for $language")
            if (voices.isEmpty()) {
                loops.remove(book)
                on = loops.isNotEmpty()
                app.yuyue.reader.DramaWork.synthesizing = on
                return@launch
            }
            // Respect the user's chosen narrator voice verbatim (never silently replace cloud/system/cloned/kokoro narrators).
            val narratorID = resolvedNarrator ?: voices.first().id
            // Core reads `roles/book/{id}` directly as single source of truth.
            // 声纹: each voice's print, once, so the voices in one scene are kept apart.
            SpeakerPrint.ensure(library.dataDir)
            // 回听's transcriber (about 240 MB): only needed for cloud/studio verification, not Live layer.
            if (app.yuyue.reader.DramaWork.cloudCast && !Listener.installed(library.dataDir) &&
                !(context.getSystemService(android.content.Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager).isActiveNetworkMetered) {
                try { Listener.ensure(library.dataDir) {} } catch (_: java.io.IOException) {}
            }
            // 声音素材库, when this book's play has room tones or sounds.
            if (runCatching { core.dramaSound(book) }.getOrNull()?.let { it.beds || it.spots } == true) SoundLibrary.ensure(core, library.dataDir, debugSounds)
            val castVoices = SpeakerPrint.printVoices(core, library.dataDir, voices, language)
            runCatching { core.dramaCastVoices(book, castVoices, narratorID) }.getOrNull()?.let { plan ->
                synchronized(planCacheLock) { cachedPlans[book] = plan }
            }
            clipsFolder(book).mkdirs()
            // The core hands out the focus section's clips and the next's; the focus moves on.
            val sections = runCatching { core.book(book).sections.size }.getOrDefault(1).coerceAtLeast(1).toUInt()
            var focus = section
            var passed = 0u
            while (isActive && passed < sections) {
                if (done == null && !isCharging()) {
                    waitingForPower = true
                    sectionMade.tryEmit(book)
                    while (isActive && preparing(book) && !isCharging()) {
                        kotlinx.coroutines.delay(3000)
                    }
                    waitingForPower = false
                    sectionMade.tryEmit(book)
                    if (!isActive || !preparing(book)) break
                }
                val task = runCatching { core.dramaNextTask(book, focus) }.getOrNull()
                if (task == null) {
                    sectionMade.tryEmit(book)
                    if (done != null) break
                    focus = (focus + 2u) % sections
                    passed += 2u
                    continue
                }
                val (id, result) = when (task) {
                    is DramaTask.Tts -> task.id to make(book, task.clip, language, narratorID)
                    // The model's questions are 认人's; not this loop's.
                    is DramaTask.Llm -> task.id to DramaResult.Returned
                    // 造声音 needs a model that acts, which a phone can't run.
                    is DramaTask.DesignVoice -> task.id to DramaResult.Returned
                    is DramaTask.Sfx -> task.id to sound(task.description, task.seconds)
                }
                if (result == DramaResult.Returned && !id.startsWith("tts:")) break
                runCatching { core.dramaSubmit(book, id, if (isActive) result else DramaResult.Returned) }
                if (app.yuyue.BuildConfig.DEBUG && result is DramaResult.Clip) android.util.Log.d("DramaSynth", "clip ${id.takeLast(8)} ${"%.1f".format(result.clip.seconds)}s")
            }
            waitingForPower = false
            loops.remove(book)
            on = loops.isNotEmpty()
            app.yuyue.reader.DramaWork.synthesizing = on
            app.yuyue.reader.DramaWork.tellCore(core)
            done?.let { library.scope.launch { it() } }
        }
    }

    /** 生成音效: a sound the book writes, made by the sound model. */
    private suspend fun sound(description: String, seconds: Float): DramaResult = try {
        SfxMaker.make(library.core, context.cacheDir, description, seconds)?.let { DramaResult.Sound(it.toList()) } ?: DramaResult.Returned
    } catch (e: kotlinx.coroutines.CancellationException) {
        throw e
    } catch (e: Exception) {
        DramaResult.Failed(e.message ?: e.toString())
    }

    fun stop(book: String) {
        loops.remove(book)?.cancel()
        waitingForPower = false
        on = loops.isNotEmpty()
        app.yuyue.reader.DramaWork.synthesizing = on
        app.yuyue.reader.DramaWork.tellCore(library.core)
    }

    private suspend fun make(book: String, clip: ClipAsk, language: String, fallback: String): DramaResult {
        val voice = Voices.find(clip.voice, book) ?: Voices.find(fallback, book) ?: return DramaResult.Failed("no voice")
        // The book's reading marks out of the text; a cloud voice is told them, and how to act.
        val parts = library.core.dramaSpokenParts(clip.text)
        val plain = parts.joinToString("") { it.text }
        val actingOn = runCatching { library.core.dramaSound(book).acting }.getOrDefault(true)
        return try {
            val speech = when (voice) {
                is CardVoice -> {
                    val style = if (actingOn) library.core.cardStyleFor(clip.act) else uniffi.yuyue_core.CardStyle.NEUTRAL
                    voice.render(clip.text, language, style)
                }
                is CloudVoice -> voice.render(parts, language, if (actingOn) acting(clip.act, language.startsWith("zh")) else null)
                else -> voice.render(plain, language)
            }
            if (speech == null) {
                // A voice that said nothing for words failed (the core makes it again, or has
                // another voice read it); only a lone dash is a clip of no length.
                if (plain.any { it.isLetterOrDigit() }) return DramaResult.Failed("the voice gave nothing")
                return DramaResult.Clip(MadeClip(clip.key, 0.0, emptyList()), ClipCheck(0f, 0u, 1f, false, 0f, 0f, 0f, 0u))
            }
            val tunedSpeech = applyClipTuning(book, clip, speech)
            val floats = tunedSpeech.samples.map { it / 32768f }
            val at48 = library.core.dramaResample(floats, tunedSpeech.rate.toUInt())
            withContext(Dispatchers.IO) { writeClip(book, clip.key, at48.toFloatArray()) }
            // Live-layer CardVoice clips already passed lightweight gating without ASR.
            val sim = if (voice is CardVoice) 1f else {
                SpeakerPrint.print(library.core, library.dataDir, at48.toFloatArray(), 48_000)?.let { library.core.speakerSimilarity(voice.id, it.toList()) } ?: 1f
            }
            val heard = if (voice is CardVoice) plain else (Listener.text(library.dataDir, at48.toFloatArray(), 48_000, language) ?: plain)
            val check = library.core.dramaMeasureClip(plain, heard, at48, 48_000u, sim, language.startsWith("zh"))
            DramaResult.Clip(MadeClip(clip.key, tunedSpeech.seconds, emptyList()), check)
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            DramaResult.Failed(e.message ?: e.toString())
        }
    }

    /** A section as one stretch of sound, scene after scene through the core's mixer, as WAV
     *  (导出). Clips not made yet are left out. */
    suspend fun mixSection(book: String, section: UInt): ByteArray? = withContext(Dispatchers.IO) {
        SoundLibrary.register(library.core, library.dataDir)
        val timelines = runCatching { library.core.dramaTimelines(book, section, emptyList()) }.getOrNull().orEmpty()
        if (timelines.isEmpty()) return@withContext null
        var header: ByteArray? = null
        val body = java.io.ByteArrayOutputStream()
        for (tl in timelines) {
            val keys = tl.items.filterIsInstance<TimelineItem.Voice>().map { it.clip }.distinct()
            val sources = keys.mapNotNull { k -> readClip(book, k)?.let { MixSource(k, it.toList(), false) } }
            val wav = DramaMixer(tl, sources).renderWav()
            if (header == null) header = wav.copyOfRange(0, 44)
            body.write(wav, 44, wav.size - 44)
        }
        val h = header ?: return@withContext null
        val bytes = body.size()
        ByteBuffer.wrap(h).order(ByteOrder.LITTLE_ENDIAN).apply { putInt(4, bytes + 36); putInt(40, bytes) }
        h + body.toByteArray()
    }

    /** 导出成有声书: the sections made so far, mixed, as one M4B (AAC) with a chapter each. */
    suspend fun exportM4B(book: String): File? = withContext(Dispatchers.IO) {
        val core = library.core
        val made = runCatching { core.dramaMade(book) }.getOrNull() ?: return@withContext null
        if (made.madeSections.isEmpty()) return@withContext null
        val detail = runCatching { core.book(book) }.getOrNull() ?: return@withContext null
        val dir = File(context.cacheDir, "export").apply { mkdirs() }
        val out = File(dir, detail.summary.title.replace('/', '-') + ".m4b")
        val rate = 48_000
        val format = android.media.MediaFormat.createAudioFormat(android.media.MediaFormat.MIMETYPE_AUDIO_AAC, rate, 2).apply {
            setInteger(android.media.MediaFormat.KEY_AAC_PROFILE, android.media.MediaCodecInfo.CodecProfileLevel.AACObjectLC)
            setInteger(android.media.MediaFormat.KEY_BIT_RATE, 96_000)
            setInteger(android.media.MediaFormat.KEY_MAX_INPUT_SIZE, 1 shl 16)
        }
        val codec = android.media.MediaCodec.createEncoderByType(android.media.MediaFormat.MIMETYPE_AUDIO_AAC)
        codec.configure(format, null, null, android.media.MediaCodec.CONFIGURE_FLAG_ENCODE)
        codec.start()
        val muxer = android.media.MediaMuxer(out.path, android.media.MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        var track = -1
        val info = android.media.MediaCodec.BufferInfo()
        val marks = mutableListOf<uniffi.yuyue_core.AudioMark>()
        var frames = 0L
        fun drain(end: Boolean) {
            while (true) {
                val i = codec.dequeueOutputBuffer(info, if (end) 10_000 else 0)
                when {
                    i == android.media.MediaCodec.INFO_OUTPUT_FORMAT_CHANGED -> { track = muxer.addTrack(codec.outputFormat); muxer.start() }
                    i >= 0 -> {
                        val buf = codec.getOutputBuffer(i)!!
                        if (info.size > 0 && track >= 0 && info.flags and android.media.MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) muxer.writeSampleData(track, buf, info)
                        codec.releaseOutputBuffer(i, false)
                        if (info.flags and android.media.MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) return
                    }
                    else -> if (!end) return
                }
            }
        }
        try {
            for (s in made.madeSections) {
                val wav = mixSection(book, s) ?: continue
                if (wav.size <= 44) continue
                marks += uniffi.yuyue_core.AudioMark(detail.sections.getOrNull(s.toInt())?.title, frames.toDouble() / rate)
                // 16-bit stereo PCM after the 44-byte header, fed a piece at a time.
                var at = 44
                while (at < wav.size) {
                    val i = codec.dequeueInputBuffer(10_000)
                    if (i < 0) { drain(false); continue }
                    val buf = codec.getInputBuffer(i)!!
                    val n = minOf(buf.capacity() / 4 * 4, wav.size - at)
                    buf.clear(); buf.put(wav, at, n)
                    codec.queueInputBuffer(i, 0, n, frames * 1_000_000 / rate, 0)
                    frames += n / 4
                    at += n
                    drain(false)
                }
            }
            val i = codec.dequeueInputBuffer(10_000)
            if (i >= 0) codec.queueInputBuffer(i, 0, 0, frames * 1_000_000 / rate, android.media.MediaCodec.BUFFER_FLAG_END_OF_STREAM)
            drain(true)
        } finally {
            runCatching { codec.stop(); codec.release() }
            runCatching { if (track >= 0) muxer.stop(); muxer.release() }
        }
        if (frames == 0L) return@withContext null
        // Chapters, book metadata and cover where the file allows; the sound either way.
        runCatching { core.dramaFinishM4b(book, out.path, marks) }
        out
    }

    fun share(file: File) {
        val uri = androidx.core.content.FileProvider.getUriForFile(context, context.packageName + ".files", file)
        context.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).setType("audio/mp4").putExtra(android.content.Intent.EXTRA_STREAM, uri).addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION), null).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun applyClipTuning(book: String, clip: ClipAsk, speech: Speech): Speech {
        val tuning = runCatching { library.core.dramaClipTuning(book, clip.section, clip.sentence, clip.start) }.getOrNull() ?: return speech
        if (kotlin.math.abs(tuning.rate - 1.0) < 0.01 && kotlin.math.abs(tuning.pitch) < 0.01) return speech
        val tuned = uniffi.yuyue_core.tuneSpeechPcm(speech.samples.toList(), speech.rate.toUInt(), tuning.rate.toFloat(), tuning.pitch.toFloat())
        return if (tuned.isEmpty()) speech else Speech(tuned.toShortArray(), speech.rate)
    }

    /** A sentence of the play: its stretch of the scene, mixed — the voices, the room's echo, the
     *  room tone and the sounds the book writes, and the pause after it. null while any of its
     *  clips isn't made (the voice reads it as usual). */
    fun sentence(book: String, section: Int, sentence: Int): Speech? {
        SoundLibrary.register(library.core, library.dataDir)
        val w = runCatching { library.core.dramaSentenceWindow(book, section.toUInt(), sentence.toUInt()) }.getOrNull() ?: return null
        val sources = w.clips.distinct().map { k -> MixSource(k, (readClip(book, k) ?: return null).toList(), false) }
        val mono = DramaMixer(w.timeline, sources).renderSpan(w.from, w.to)
        if (mono.isEmpty()) return null
        return Speech(ShortArray(mono.size) { (mono[it].coerceIn(-1f, 1f) * 32767).toInt().toShort() }, 48_000)
    }

    /**
     * 即时层广播剧: 若本机已做好该句精做/预合成混音则直接播，否则按分角选声、
     * 读音标注、情感语气、快慢高低与 `DramaMixer`（带环境音/音效/声场/混响）即时合成并落盘缓存。
     */
    suspend fun liveSentence(
        book: String,
        section: Int,
        sentence: Int,
        language: String,
        narratorFallback: String?,
    ): Speech? = withContext(Dispatchers.Default) {
        SystemVoice.Engine.init(context)
        val resolvedNarrator = effectiveNarrator(language, narratorFallback)
        val borrowed = runCatching { library.core.dramaBorrowedFrom(book) }.getOrNull() != null
        val borrowedMatchesNarrator = if (!borrowed) {
            false
        } else if (resolvedNarrator == null) {
            true
        } else {
            val ds = runCatching { library.core.dramaSection(book, section.toUInt()) }.getOrNull()
            val curAsks = runCatching { library.core.dramaLiveSentenceAsks(book, section.toUInt(), sentence.toUInt()) }.getOrNull()
            if (ds == null || curAsks == null) {
                true
            } else {
                val narrStarts = ds.segments
                    .filter { it.sentence == sentence.toUInt() && it.kind == uniffi.yuyue_core.SegKind.NARRATION }
                    .map { it.start }
                    .toSet()
                val narrAsks = curAsks.filter { it.start in narrStarts }
                narrAsks.isEmpty() || narrAsks.all { it.voice == resolvedNarrator }
            }
        }
        if (!borrowedMatchesNarrator) {
            ensurePlan(book, language, resolvedNarrator) ?: return@withContext null
        }
        withContext(Dispatchers.IO) { sentence(book, section, sentence) }?.let { return@withContext it }
        if (borrowedMatchesNarrator) {
            ensurePlan(book, language, resolvedNarrator) ?: return@withContext null
        }
        SoundLibrary.register(library.core, library.dataDir)
        val asks = runCatching {
            library.core.dramaLiveSentenceAsks(book, section.toUInt(), sentence.toUInt())
        }.getOrNull().orEmpty()
        if (asks.isEmpty()) return@withContext null

        val dramaMode = runCatching { library.core.dramaMode(book) }.getOrDefault(false)
        val sound = runCatching { library.core.dramaSound(book) }.getOrDefault(uniffi.yuyue_core.DramaSound(true, true, true))
        val chinese = language.startsWith("zh")

        val keyBuilder = StringBuilder("v2:$section:$sentence:$dramaMode:${sound.acting}:${sound.beds}:${sound.spots}")
        for (ask in asks) {
            val t = runCatching { library.core.dramaClipTuning(book, ask.section, ask.sentence, ask.start) }.getOrNull()
            val rk = t?.let { "%.2f".format(it.rate) } ?: "1.00"
            val pk = t?.let { "%.1f".format(it.pitch) } ?: "0.0"
            keyBuilder.append('|').append(ask.key).append('#').append(rk).append('#').append(pk)
        }
        val digest = java.security.MessageDigest.getInstance("SHA-256")
            .digest(keyBuilder.toString().toByteArray())
            .take(16)
            .joinToString("") { "%02x".format(it) }
        val secDir = File(liveClipsFolder(book), "s-%05d".format(section))
        val cachedFile = File(secDir, "$sentence-$digest.m4a")
        withContext(Dispatchers.IO) { ClipAudio.read(cachedFile) }?.takeIf { it.isNotEmpty() }?.let { cached ->
            return@withContext Speech(ShortArray(cached.size) { (cached[it].coerceIn(-1f, 1f) * 32767).toInt().toShort() }, 48_000)
        }

        val madeClips = ArrayList<MadeClip>(asks.size)
        val sources = ArrayList<MixSource>(asks.size)
        val fallbackParts = ArrayList<FloatArray>(asks.size)
        for (ask in asks) {
            if (!isActive) return@withContext null
            val voice = Voices.find(ask.voice, book)
                ?: throw IllegalStateException(t("广播剧里角色的声音当前不可用，请检查离线模型或在人物声音中重选"))
            val parts = library.core.dramaSpokenParts(ask.text)
            val plain = parts.joinToString("") { it.text }
            val rendered = when (voice) {
                is CardVoice -> {
                    val style = if (sound.acting) library.core.cardStyleFor(ask.act) else uniffi.yuyue_core.CardStyle.NEUTRAL
                    voice.render(ask.text, language, style)
                }
                is CloudVoice -> voice.render(parts, language, if (sound.acting) acting(ask.act, chinese) else null)
                else -> voice.render(plain, language)
            }
            if (rendered == null || rendered.samples.isEmpty()) {
                madeClips += MadeClip(ask.key, 0.0, emptyList())
                continue
            }
            val speech = applyClipTuning(book, ask, rendered)
            val floats = speech.samples.map { it / 32768f }
            val at48 = library.core.dramaResample(floats, speech.rate.toUInt())
            val arr48 = at48.toFloatArray()
            madeClips += MadeClip(ask.key, arr48.size.toDouble() / 48_000.0, emptyList())
            sources += MixSource(ask.key, at48, false)
            fallbackParts += arr48
        }
        if (sources.isEmpty()) return@withContext null

        val mixedMono: FloatArray = runCatching {
            library.core.dramaLiveSentenceWindow(book, section.toUInt(), sentence.toUInt(), madeClips)
        }.getOrNull()?.let { w ->
            DramaMixer(w.timeline, sources).renderSpan(w.from, w.to).toFloatArray()
        }?.takeIf { it.isNotEmpty() } ?: run {
            val gap = 3_840 // 0.08 s at 48 kHz
            val total = fallbackParts.sumOf { it.size } + gap * (fallbackParts.size - 1).coerceAtLeast(0)
            FloatArray(total).also { out ->
                var at = 0
                fallbackParts.forEachIndexed { idx, part ->
                    if (idx > 0) at += gap
                    part.copyInto(out, at)
                    at += part.size
                }
            }
        }
        if (mixedMono.isEmpty()) return@withContext null
        withContext(Dispatchers.IO) {
            runCatching {
                secDir.mkdirs()
                ClipAudio.write(mixedMono, cachedFile)
                pruneLiveCacheIfNeeded(book, section)
            }
        }
        Speech(ShortArray(mixedMono.size) { (mixedMono[it].coerceIn(-1f, 1f) * 32767).toInt().toShort() }, 48_000)
    }

    private fun pruneLiveCacheIfNeeded(book: String, keepFromSection: Int) {
        val root = liveClipsFolder(book)
        val dirs = root.listFiles()?.filter { it.isDirectory } ?: return
        data class SecDir(val section: Int, val file: File, val bytes: Long)
        val items = dirs.map { d ->
            val sec = d.name.removePrefix("s-").toIntOrNull() ?: 0
            val sz = d.listFiles()?.filter { it.isFile }?.sumOf { it.length() } ?: 0L
            SecDir(sec, d, sz)
        }
        var total = items.sumOf { it.bytes }
        if (total <= CardVoice.MAX_BOOK_CACHE_BYTES) return
        val ordered = items.sortedWith(compareBy<SecDir>({ it.section >= keepFromSection }, { it.section }))
        for (item in ordered) {
            if (item.section == keepFromSection) continue
            if (total <= CardVoice.MAX_BOOK_CACHE_BYTES) break
            if (item.file.deleteRecursively()) total -= item.bytes
        }
    }

    /** The acting a clip asks for, in the cloud voices' terms; null for none. */
    private fun acting(act: uniffi.yuyue_core.DramaAct, chinese: Boolean): CloudVoice.Acting? {
        val core = library.core
        val tags = core.dramaActTags(act)
        // Azure's speaking styles, where the voice has them (it ignores one it hasn't).
        var style: String? = when (act.volume) {
            uniffi.yuyue_core.ActVolume.WHISPER -> "whispering"
            uniffi.yuyue_core.ActVolume.SHOUT -> "shouting"
            else -> null
        }
        if (style == null && act.level > 0u) style = when (act.emotion) {
            uniffi.yuyue_core.ActEmotion.JOY -> "cheerful"
            uniffi.yuyue_core.ActEmotion.ANGER -> "angry"
            uniffi.yuyue_core.ActEmotion.SADNESS -> "sad"
            uniffi.yuyue_core.ActEmotion.FEAR, uniffi.yuyue_core.ActEmotion.ANXIOUS -> "fearful"
            uniffi.yuyue_core.ActEmotion.DISGUST -> "disgruntled"
            uniffi.yuyue_core.ActEmotion.TENDER -> "gentle"
            uniffi.yuyue_core.ActEmotion.MOCKING -> "unfriendly"
            uniffi.yuyue_core.ActEmotion.WEARY -> "depressed"
            uniffi.yuyue_core.ActEmotion.SURPRISE -> "excited"
            else -> null
        }
        val a = CloudVoice.Acting(core.dramaActInstruction(act, chinese), tags.head, tags.tail, style, listOf(0.8, 0.8, 1.2, 1.6)[act.level.toInt().coerceAtMost(3)])
        return a.takeIf { !it.isFlat }
    }

    companion object {
        const val LIVE_ENGINE = "live-zipvoice-1"
        const val STUDIO_ENGINE = "studio-qwen3tts-1.7b-1"
    }
}
