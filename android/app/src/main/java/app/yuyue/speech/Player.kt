package app.yuyue.speech

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.PlaybackParams
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.design.Prefs
import app.yuyue.t
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.launch
import uniffi.yuyue_core.BookDetail
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.YuyueCore

/**
 * Reads a book aloud one sentence at a time, for every voice alike, as on
 * Apple. A sentence is rendered to audio before it plays, so its length is known exactly and the
 * ink line keeps pace with the voice. Two sentences are kept rendered ahead, so sentences and
 * chapters follow one another without a gap. Speed is applied while playing (pitch kept). The
 * notification and lock screen show the book and take play, pause, next and previous sentence.
 */
class Player(private val context: Context, private val core: YuyueCore, val detail: BookDetail, script: Script? = null) {
    /** What is read, one section after another: a book's chapters from the core, or a PDF's
     *  pages, each a section of its own. */
    class Script(
        val sentences: (Int) -> List<String>,
        /** The next section with something to read, and the one before. */
        val next: (Int) -> Int?,
        val previous: (Int) -> Int?,
        val language: (Int) -> String,
        /** What the notification calls the section. */
        val title: (Int) -> String?,
    ) {
        companion object {
            fun book(core: YuyueCore, d: BookDetail): Script {
                val id = d.summary.id
                return Script(
                    sentences = { runCatching { core.spokenSentences(id, it.toUInt()) }.getOrDefault(emptyList()) },
                    next = { runCatching { core.nextSection(id, it.toUInt(), true) }.getOrNull()?.toInt() },
                    previous = { runCatching { core.previousSection(id, it.toUInt(), true) }.getOrNull()?.toInt() },
                    language = { d.sections.getOrNull(it)?.lang ?: d.summary.language },
                    title = { s -> d.toc.lastOrNull { it.section.toInt() <= s }?.title ?: d.sections.getOrNull(s)?.title },
                )
            }
        }
    }

    private val script: Script = script ?: Script.book(core, detail)
    /** 分角色: the characters' own voices, where the reader has given any (a book's, not a PDF's). */
    private val roles: BookRoles? = if (script == null) BookRoles(core, detail.summary.id) else null

    enum class State { Stopped, Loading, Playing, Paused }

    /** A sentence of the book. */
    data class Spot(val section: Int, val sentence: Int) {
        val position: Position get() = Position(section.toUInt(), maxOf(0, sentence).toUInt())
    }

    sealed interface Sleep {
        data object Off : Sleep
        /** 本句读完 */
        data object Sentence : Sleep
        /** 本章结束 */
        data object Chapter : Sleep
        /** After some minutes: the moment to stop. */
        data class Until(val at: Long, val minutes: Int) : Sleep
    }

    var state by mutableStateOf(State.Stopped)
        private set
    /** The sentence being read or paused on. */
    var spot by mutableStateOf<Spot?>(null)
        private set
    var failure by mutableStateOf<String?>(null)
    /** 离线默认旁白在播放/暂停期间下好后提供给用户确认切换的候选音色（不自动静默切声）。 */
    var readyOfflineVoice by mutableStateOf<BookVoice?>(null)
        private set
    /** Incremented whenever the active narrator voice changes, so UI refreshes immediately. */
    var voiceVersion by mutableIntStateOf(0)
        private set
    /** The failure is that no voice reads this language, named as people say it (中文): the
     *  system's voice data can be downloaded. */
    var missingVoice by mutableStateOf<String?>(null)
    private var sleepState = mutableStateOf<Sleep>(Sleep.Off)
    var sleep: Sleep
        get() = sleepState.value
        set(v) {
            val old = sleepState.value
            if (v == old) return
            sleepState.value = v
            sleepChanged(old)
        }
    /** 单句循环 — or, with [loopRange], a few sentences over and over. */
    private var loopState = mutableStateOf(false)
    var loop: Boolean
        get() = loopState.value
        set(v) {
            loopState.value = v
            if (!v) loopRange = null
        }
    /** 几句一起循环: (section, first, last). */
    var loopRange by mutableStateOf<Triple<Int, Int, Int>?>(null)
        private set

    /** 念完停一下: quiet after each sentence (seconds; [PAUSE_AS_LONG] for as long as the sentence). */
    var pauseAfter by mutableStateOf(0.0)

    /** From the sentence being read to [sentence] (either way round), over and over; same section only. */
    fun loopTo(section: Int, sentence: Int) {
        val here = spot ?: return
        if (here.section != section) return
        loopRange = Triple(section, minOf(here.sentence, sentence), maxOf(here.sentence, sentence))
        loop = true
    }

    /** The sentence with the quiet 念完停一下 asks for after it. */
    private fun paced(sp: Speech): Speech {
        val quiet = if (pauseAfter == PAUSE_AS_LONG) minOf(sp.samples.size.toDouble() / sp.rate, 20.0) else pauseAfter
        if (quiet <= 0) return sp
        return Speech(sp.samples + ShortArray((quiet * sp.rate).toInt()), sp.rate)
    }
    /** Speed, 0.5–3. */
    var rate by mutableStateOf(Prefs.float(RATE_KEY, 1f).takeIf { it > 0 } ?: 1f)
        private set

    /** The ink line: the sentence, its length and how much has been read (ms at the current speed), and whether it moves. */
    var onSentence: ((Spot, Double, Double, Boolean) -> Unit)? = null
    /** 听读书: the narrator's recording of the book, read instead of a voice where it has the
     *  sentence; the voice reads the rest. */
    var narrator: NarratorVoice? = null
    /** 有声书: what the map says — sentences to pass, talk not in the book to play or pass. */
    var guide: RecordingGuide? = null
    /** Talk in the recording that isn't in the book, being played now. */
    var extraPlaying by mutableStateOf<uniffi.yuyue_core.ExtraSpan?>(null)
        private set
    /** 片头 passed as listening began, for the capsule to offer for 5 seconds. */
    var skippedHead by mutableStateOf<uniffi.yuyue_core.ExtraSpan?>(null)
        private set
    private var wantHead = false
    /** Where the timer was set: offered next time the book opens (回到昨晚). */
    var sleepFrom: Spot? = null
        private set
    /** The timer's last 20 seconds: the voice fades; a key or a shake gives 10 minutes more. */
    var fading by mutableStateOf(false)
        private set
    private var fadeJob: Job? = null
    private var shake: ShakeWatch? = null
    var onStop: (() -> Unit)? = null
    var onSleep: ((Spot) -> Unit)? = null

    private val scope: CoroutineScope = MainScope()
    private val main = Handler(Looper.getMainLooper())
    private var generation = 0
    private var renderToken = 0
    private class Line(val spot: Spot, val speech: Speech, var startFrame: Long = 0, var endFrame: Long = 0, val extra: uniffi.yuyue_core.ExtraSpan? = null)
    /** Sentences handed to the track, in order; the first is the one playing. */
    private val queued = ArrayDeque<Line>()
    /** Sentences rendered ahead in memory, waiting to be handed to the track. */
    private val staged = ArrayDeque<Line>()
    private var nextToRender: Spot? = null
    private var rendering: Job? = null
    private var writer: Thread? = null
    private var track: AudioTrack? = null
    private var trackRate = 0
    /** Frames handed to the track so far (in its own rate). */
    private var scheduled = 0L
    private var pending = java.util.concurrent.LinkedBlockingQueue<Pair<Int, Speech>>()
    private var texts = mutableMapOf<Int, List<String>>()
    private val voices = mutableMapOf<String, BookVoice?>()
    private var fetching: Triple<Int, Int, Job>? = null
    /** Sliding prefetch window for paid cloud voices: keeps only the next few sentences warm. */
    private val cloudPrefetchWindow = 4
    private var focus: AudioFocusRequest? = null
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    // MARK: Controls

    /** Where the first sentence starts when it isn't its own start (搜索 found a word in it). */
    private var startWord: Pair<Spot, Double>? = null
    /** Shown when critical phone heat temporarily falls back to system voice for the rest of a chapter. */
    var thermalNotice by mutableStateOf<String?>(null)
    private var thermalFallbackSection: Int? = null
    /** Remember where playback failed so 重试 / 换成系统声音 resumes from the same sentence. */
    private var failedSpot: Spot? = null
    private var chapterLayer: Pair<Int, uniffi.yuyue_core.DramaLayer>? = null
    private var chapterLiveAllMade: Pair<Int, Boolean>? = null
    private var lastRenderedSection: Int? = null
    private var lastUsingCardVoice = false
    private var ttsTrackedForSession = false

    /** Starts reading at `position`, or at the first sentence after it with something to read.
     *  `word`: in the narrator's voice, the first sentence starts at this moment of the
     *  recording (a word found by 搜索), not at its own start. */
    fun play(from: Position, word: Double? = null) {
        failure = null
        failedSpot = null
        missingVoice = null
        thermalNotice = null
        thermalFallbackSection = null
        pausedForFocus = false
        (sleep as? Sleep.Until)?.let { if (System.currentTimeMillis() >= it.at) sleep = Sleep.Off }
        reset()
        startWord = word?.let { Spot(from.section.toInt(), from.sentence.toInt()) to it }
        val start = first(Spot(from.section.toInt(), from.sentence.toInt())) ?: return stop()
        spot = start
        nextToRender = start
        state = State.Loading
        // speak immediately with SystemVoice while preparing the better voices in the background.
        val langTag = lang(start.section)
        if (savedVoiceId(langTag).isEmpty() && CardVoice.speaksLanguage(langTag)) {
            runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull()?.let { lib ->
                if (!KokoroModel.installed(lib.dataDir) && !KokoroModel.deviceUnsupported) {
                    KokoroModel.onFirstListen(context, core, lib.dataDir)
                }
            }
        }
        Session.claim(this)
        render()
    }

    /** Moves the paused cursor to `from` without starting playback, notifying the reader immediately. */
    private fun seekPaused(from: Position) {
        failure = null
        failedSpot = null
        missingVoice = null
        thermalNotice = null
        thermalFallbackSection = null
        reset()
        startWord = null
        val start = first(Spot(from.section.toInt(), from.sentence.toInt())) ?: return stop()
        spot = start
        nextToRender = start
        state = State.Paused
        report()
        Session.update()
    }

    fun pause() = pauseInternal(keepFocus = false)

    private fun pauseInternal(keepFocus: Boolean) {
        if (state != State.Playing && state != State.Loading) return
        // stop pre-synthesizing ahead immediately on pause.
        rendering?.cancel()
        rendering = null
        main.removeCallbacks(watch)
        runCatching { track?.pause() }
        state = State.Paused
        if (!keepFocus) {
            pausedForFocus = false
            releaseFocus()
        }
        stopFade(restoreVolume = true)
        (sleep as? Sleep.Until)?.let { if (System.currentTimeMillis() >= it.at) sleep = Sleep.Off }
        report()
        Session.update()
    }

    fun resume() {
        if (state != State.Paused) return
        pausedForFocus = false
        (sleep as? Sleep.Until)?.let { if (System.currentTimeMillis() >= it.at) sleep = Sleep.Off }
        val wasEmpty = queued.isEmpty()
        if (wasEmpty) feedTrack()
        if (queued.isEmpty()) {
            state = State.Loading
            if (Session.current !== this) Session.claim(this) else {
                Session.update()
                startService()
            }
            return render()
        }
        if (!acquireFocusOrPause()) return
        if (!fading) runCatching { track?.setVolume(1f) }
        runCatching { track?.play() }
        if (wasEmpty) {
            began()
            if (Session.current !== this) Session.claim(this) else startService()
        } else {
            state = State.Playing
            report()
            if (Session.current !== this) Session.claim(this) else {
                Session.update()
                startService()
            }
            main.removeCallbacks(watch)
            main.postDelayed(watch, 40)
        }
        render()
    }

    fun toggle() = if (state == State.Playing || state == State.Loading) pause() else resume()

    fun stop() {
        val was = state
        fetching?.third?.cancel()
        fetching = null
        thermalNotice = null
        thermalFallbackSection = null
        readyOfflineVoice = null
        voices.clear()
        voiceVersion++
        ttsTrackedForSession = false
        pausedForFocus = false
        stopFade(restoreVolume = false)
        reset()
        state = State.Stopped
        spot = null
        releaseFocus()
        Session.release(this)
        if (was != State.Stopped) onStop?.invoke()
    }

    /** 跳过: past the recording's own talk being played, on to the next sentence. */
    fun skipExtra() {
        val l = queued.firstOrNull() ?: return
        val e = l.extra ?: return
        val next = (if (e.place == uniffi.yuyue_core.ExtraPlace.HEAD) l.spot else after(l.spot)) ?: return stop()
        if (state == State.Paused) seekPaused(next.position) else play(next.position)
    }

    /** 点这里听: the 片头 just passed, then on from the first sentence. */
    fun playHead() {
        val s = spot ?: nextToRender ?: return
        if (skippedHead == null) return
        skippedHead = null
        wantHead = true
        play(s.position)
    }

    fun dismissHead() { skippedHead = null }

    /** Moves on, or back, by one sentence. */
    fun skip(forward: Boolean) {
        val s = spot ?: return
        var target = (if (forward) after(s) else before(s)) ?: return
        // Past the sentences the recording doesn't read, or going back would land here again.
        val g = guide
        if (narrator != null && g != null) {
            while (g.skips(target.section, target.sentence)) target = (if (forward) after(target) else before(target)) ?: break
        }
        if (state == State.Paused) seekPaused(target.position) else play(target.position)
    }

    fun changeRate(r: Float) {
        rate = r
        Prefs.set(RATE_KEY, r)
        track?.let { runCatching { it.playbackParams = PlaybackParams().setSpeed(r).setPitch(1f) } }
        report()
    }

    /** Reads `language` with another voice from now on, starting the sentence again in it. */
    fun choose(voice: BookVoice, language: String) {
        saveVoicePreference(language, voice.id, notify = false)
        readyOfflineVoice = null
        val cacheKey = canonicalLanguage(language).ifEmpty { language }
        voices[cacheKey] = voice
        voices[language] = voice
        voiceVersion++
        ttsTrackedForSession = false
        restartHere()
    }

    /** 用户确认切换到刚下载完成的离线默认旁白。 */
    fun acceptReadyOfflineVoice() {
        val next = readyOfflineVoice ?: return
        readyOfflineVoice = null
        choose(next, language)
    }

    /** 用户暂不切换，收起就绪提示并继续使用当前声音。 */
    fun dismissReadyOfflineVoice() {
        readyOfflineVoice = null
    }

    /** Switches to a local system voice and resumes from where playback failed. */
    fun useSystemVoice() {
        val langTag = language
        val resumeAt = failedSpot ?: spot
        failure = null
        missingVoice = null
        scope.launch {
            val sys = try {
                Voices.systemBest(langTag)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Throwable) {
                failedSpot = resumeAt
                failure = t("朗读出错了（%@）", e.message ?: e.toString())
                return@launch
            }
            if (sys == null) {
                val name = java.util.Locale.forLanguageTag(langTag).getDisplayLanguage(java.util.Locale.getDefault()).ifEmpty { langTag }
                failedSpot = resumeAt
                missingVoice = name
                failure = t("下载一个%@声音就能朗读这本书，系统会带你去它的下载页。", name)
                return@launch
            }
            saveVoicePreference(langTag, sys.id)
            val cacheKey = canonicalLanguage(langTag).ifEmpty { langTag }
            voices[cacheKey] = sys
            voices[langTag] = sys
            voiceVersion++
            ttsTrackedForSession = false
            if (resumeAt != null) play(resumeAt.position) else restartHere()
        }
    }

    /** Retries playback from the sentence where the last error occurred. */
    fun retry() {
        val resumeAt = failedSpot ?: spot
        failure = null
        missingVoice = null
        if (resumeAt != null) play(resumeAt.position)
    }

    suspend fun currentVoice(language: String): BookVoice? = voice(language)

    /** The sentence being read or paused on, for the 播放条 and the notification. */
    val line: String? get() = spot?.let { sentences(it.section).getOrNull(it.sentence) }

    /** Sentences in the section being read, for the 播放条's `第 4 / 24 句`. */
    val sentenceCount: Int get() = spot?.let { sentences(it.section).size } ?: 0

    val sectionProgress: Double get() = spot?.let { s -> sentences(s.section).size.takeIf { it > 0 }?.let { (s.sentence + 1.0) / it } } ?: 0.0

    val language: String get() = (spot ?: failedSpot)?.let { lang(it.section) } ?: detail.summary.language

    fun textsChanged() = texts.clear()

    /** Stops reading and lets go of the notification. The player can't be used afterwards. */
    fun close() {
        stop()
        Session.release(this)
        scope.cancel()
        runCatching { track?.release() }
        track = null
    }

    // MARK: The queue

    private fun reset() {
        generation++
        renderToken++
        extraPlaying = null
        rendering?.cancel()
        rendering = null
        writer?.interrupt()
        writer = null
        pending = java.util.concurrent.LinkedBlockingQueue()
        queued.clear()
        staged.clear()
        nextToRender = null
        chapterLayer = null
        chapterLiveAllMade = null
        lastRenderedSection = null
        lastUsingCardVoice = false
        runCatching { app.yuyue.YuyueApp.instance.library.recordingFetch.applyPendingCards(detail.summary.id) }
            .getOrNull()?.let { if (it) roles?.reload() }
        track?.let {
            runCatching { it.pause(); it.flush() }
            runCatching { it.release() }
        }
        track = null
        scheduled = 0
        main.removeCallbacks(watch)
    }

    /** Drops pre-rendered sentences not yet handed to AudioTrack and cancels in-flight synthesis,
     *  without interrupting the sentence currently playing. */
    private fun dropStaged() {
        renderToken++
        rendering?.cancel()
        rendering = null
        staged.clear()
        val last = queued.lastOrNull()
        nextToRender = if (last != null) {
            if (last.extra?.place == uniffi.yuyue_core.ExtraPlace.HEAD) last.spot else after(last.spot)
        } else {
            spot
        }
    }

    private fun currentThermalStatus(): Int {
        if (android.os.Build.VERSION.SDK_INT < 29) return 0
        val pm = context.getSystemService(Context.POWER_SERVICE) as? android.os.PowerManager ?: return 0
        return runCatching { pm.currentThermalStatus }.getOrDefault(0)
    }

    private fun queuedAheadSeconds(): Double {
        val r = maxOf(0.5f, rate).toDouble()
        val stagedSecs = staged.sumOf { it.speech.seconds / r }
        if (queued.isEmpty()) return stagedSecs
        val t = track
        val queuedSecs = if (t != null && trackRate > 0) {
            val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0L)
            ((scheduled - head).coerceAtLeast(0L)).toDouble() / trackRate / r
        } else {
            queued.sumOf { it.speech.seconds / r }
        }
        return queuedSecs + stagedSecs
    }

    private fun shouldKeepRendering(g: Int, token: Int): Boolean {
        if (generation != g || renderToken != token || state == State.Paused || state == State.Stopped) return false
        val buffered = queued.size + staged.size
        if (buffered == 0) return true
        if (!lastUsingCardVoice) return buffered <= AHEAD
        val target = targetAheadSeconds(currentThermalStatus())
        return if (target <= 0.0) buffered <= AHEAD else queuedAheadSeconds() < target
    }

    /** Renders sentences until enough are queued. */
    private fun render() {
        if (rendering != null || state == State.Paused || state == State.Stopped) return
        val g = generation
        val token = renderToken
        rendering = scope.launch {
            try {
            while (shouldKeepRendering(g, token)) {
                val s = nextToRender ?: break
                val lib = runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull()
                if (lastRenderedSection != s.section) {
                    val enteringNextChapter = lastRenderedSection != null
                    lastRenderedSection = s.section
                    if (lib?.recordingFetch?.applyPendingCards(detail.summary.id) == true) {
                        roles?.reload()
                    }
                    if (enteringNextChapter) {
                        // retry CardVoice on the new chapter after thermal fallback.
                        if (thermalFallbackSection != null && thermalFallbackSection != s.section) {
                            thermalFallbackSection = null
                            thermalNotice = null
                        }
                        if (lib != null && lib.dramaSynth.isDramaActive(detail.summary.id)) {
                            lib.recordingFetch.want(detail.summary.id, s.section.toUInt())
                        }
                    }
                }
                val gd = guide
                val n0 = narrator
                if (n0 != null && gd != null) {
                    // Not in the recording, and not to be read by the voice: on to the next.
                    if (gd.skips(s.section, s.sentence)) {
                        nextToRender = after(s)
                        continue
                    }
                    // 片头: played when it isn't skipped (or asked for), else offered on the capsule.
                    val head = gd.head
                    if (head != null && gd.isFirst(s.section, s.sentence) && queued.isEmpty() && staged.isEmpty()) {
                        if (wantHead || !gd.settings.headTail) {
                            wantHead = false
                            val clip = kotlinx.coroutines.withContext(Dispatchers.IO) { n0.range(head.t0, head.t1) }
                            if (generation != g || renderToken != token) return@launch
                            if (clip != null) schedule(Line(s, clip, extra = head))
                        } else skippedHead = head
                    }
                }
                // 听读书: the narrator's own voice where the recording has the sentence.
                narrator?.takeIf { it.has(s.section, s.sentence) }?.let { n ->
                    lastUsingCardVoice = false
                    val word = startWord?.takeIf { it.first == s }?.second
                    startWord = null
                    val speech = kotlinx.coroutines.withContext(Dispatchers.IO) { n.speech(s.section, s.sentence, word) }
                    if (generation != g || renderToken != token) return@launch
                    if (speech != null) {
                        nextToRender = after(s)
                        schedule(Line(s, speech))
                        // The recording's own talk after it, when it's played.
                        gd?.extraAfter(s.section, s.sentence)?.let { e ->
                            val clip = kotlinx.coroutines.withContext(Dispatchers.IO) { n.range(e.t0, e.t1) }
                            if (generation != g || renderToken != token) return@launch
                            if (clip != null) schedule(Line(s, clip, extra = e))
                        }
                        continue
                    }
                }
                val language = lang(s.section)
                val thermal = currentThermalStatus()
                val forceSystemForHeat = thermal >= THERMAL_CRITICAL || thermalFallbackSection == s.section
                if (thermal >= THERMAL_CRITICAL) {
                    thermalFallbackSection = s.section
                    if (thermalNotice == null) thermalNotice = t("手机有点热，这一章先用系统声音")
                }
                // 听成一部剧: whole-chapter Studio vs Live layer switching with instant Live synthesis.
                if (lib != null && !forceSystemForHeat && lib.dramaSynth.isDramaActive(detail.summary.id)) {
                    val synth = lib.dramaSynth
                    val layer = chapterLayer?.takeIf { it.first == s.section }?.second ?: run {
                        val l = runCatching { core.dramaSectionLayer(detail.summary.id, s.section.toUInt(), DramaSynth.STUDIO_ENGINE) }
                            .getOrDefault(uniffi.yuyue_core.DramaLayer.LIVE)
                        chapterLayer = s.section to l
                        l
                    }
                    val chosenNarrator = savedVoiceId(language).ifEmpty { null }
                    lastUsingCardVoice = (layer == uniffi.yuyue_core.DramaLayer.LIVE) && LiveVoiceModel.canRun(lib.dataDir)
                    val played: Speech? = when (layer) {
                        uniffi.yuyue_core.DramaLayer.STUDIO -> {
                            kotlinx.coroutines.withContext(Dispatchers.IO) { synth.sentence(detail.summary.id, s.section, s.sentence) }
                                ?: synth.liveSentence(detail.summary.id, s.section, s.sentence, language, chosenNarrator)
                        }
                        uniffi.yuyue_core.DramaLayer.LIVE -> {
                            synth.liveSentence(detail.summary.id, s.section, s.sentence, language, chosenNarrator)
                        }
                    }
                    if (generation != g || renderToken != token) return@launch
                    if (played != null) {
                        if (!ttsTrackedForSession) {
                            ttsTrackedForSession = true
                            val engine = if (lastUsingCardVoice) "other" else app.yuyue.analytics.AirTraffic.ttsEngineForVoiceId(chosenNarrator)
                            app.yuyue.analytics.AirTraffic.trackTtsStarted(engine, "reader")
                        }
                        nextToRender = after(s)
                        schedule(Line(s, played))
                        continue
                    }
                }
                val segPlan = if (!forceSystemForHeat) {
                    roles?.segmentPlan(s.section, s.sentence, language) { voice(language) }
                } else null
                if (segPlan != null) {
                    lastUsingCardVoice = segPlan.any { (v, _) -> v is CardVoice || (v is TunedVoice && v.base is CardVoice) }
                    val speech = try {
                        roles?.renderSegments(segPlan, language)
                    } catch (e: kotlinx.coroutines.CancellationException) {
                        throw e
                    } catch (e: Throwable) {
                        if (generation != g || renderToken != token) return@launch
                        failed(t("朗读出错了（%@）", e.message ?: e.toString()))
                        return@launch
                    }
                    if (generation != g || renderToken != token) return@launch
                    nextToRender = after(s)
                    if (speech != null) {
                        if (!ttsTrackedForSession) {
                            ttsTrackedForSession = true
                            app.yuyue.analytics.AirTraffic.trackTtsStarted(
                                app.yuyue.analytics.AirTraffic.ttsEngineForVoice(segPlan.firstOrNull()?.first),
                                "reader",
                            )
                        }
                        schedule(Line(s, speech))
                    }
                    continue
                }
                var voice = if (forceSystemForHeat) Voices.systemBest(language) ?: voice(language) else voice(language)
                if (voice == null) {
                    val name = java.util.Locale.forLanguageTag(language).getDisplayLanguage(java.util.Locale.getDefault()).ifEmpty { language }
                    failed(t("下载一个%@声音就能朗读这本书，系统会带你去它的下载页。", name))
                    missingVoice = name
                    return@launch
                }
                val isCard = voice is CardVoice || (voice is TunedVoice && voice.base is CardVoice)
                lastUsingCardVoice = isCard && !forceSystemForHeat
                if (voice is CloudVoice && roles?.any != true) fetchAhead(voice, s, language)
                val speech = try {
                    voice.render(text(s), language)
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Throwable) {
                    if (generation != g || renderToken != token) return@launch
                    failed(t("朗读出错了（%@）", e.message ?: e.toString()))
                    return@launch
                }
                if (generation != g || renderToken != token) return@launch
                nextToRender = after(s)
                // A sentence with nothing to say (a lone dash, say) is skipped.
                if (speech != null) {
                    if (!ttsTrackedForSession) {
                        ttsTrackedForSession = true
                        app.yuyue.analytics.AirTraffic.trackTtsStarted(
                            app.yuyue.analytics.AirTraffic.ttsEngineForVoice(voice),
                            "reader",
                        )
                    }
                    schedule(Line(s, speech))
                }
            }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Throwable) {
                if (generation != g || renderToken != token) return@launch
                failed(t("朗读出错了（%@）", e.message ?: e.toString()))
                return@launch
            }
            if (generation != g || renderToken != token) return@launch
            rendering = null
            // The book is over once the last sentence has played.
            if (queued.isEmpty() && staged.isEmpty() && nextToRender == null && state == State.Loading) stop()
        }
    }

    /** 滑动窗口预取: a cloud voice fetches only the next few sentences ahead of playback,
     *  avoiding paying for the rest of the chapter if the listener stops or switches voices early. */
    private fun fetchAhead(voice: CloudVoice, from: Spot, language: String) {
        val cur = fetching
        if (cur != null && cur.first == from.section && from.sentence <= cur.second + 1) return
        cur?.third?.cancel()
        val all = sentences(from.section)
        val start = from.sentence + 1
        if (start >= all.size) { fetching = null; return }
        val rest = all.subList(start, minOf(all.size, start + cloudPrefetchWindow))
        fetching = Triple(from.section, from.sentence, scope.launch(Dispatchers.IO) { voice.prefetch(rest, language) })
    }

    private fun schedule(line: Line) {
        val l = if (line.extra == null && pauseAfter != 0.0) Line(line.spot, paced(line.speech)) else line
        staged.addLast(l)
        val wasEmpty = queued.isEmpty()
        feedTrack()
        if (wasEmpty && queued.isNotEmpty() && (state == State.Loading || state == State.Playing)) {
            if (!acquireFocusOrPause()) return
            runCatching { track?.play() }
            began()
        }
    }

    private fun feedTrack() {
        while (queued.size < NODE_AHEAD && staged.isNotEmpty()) {
            val next = staged.first()
            if (queued.isNotEmpty() && track != null && trackRate != next.speech.rate) break
            val l = staged.removeFirst()
            val t = ensureTrack(l.speech.rate) ?: run {
                failed(t("音频播放初始化失败，请重试"))
                return
            }
            l.startFrame = scheduled
            l.endFrame = scheduled + l.speech.samples.size
            scheduled = l.endFrame
            queued.addLast(l)
            val queue = pending
            queue.put(generation to l.speech)
            if (writer == null) startWriter(t, queue)
        }
    }

    /** The sentences go to the track from a thread of their own: writing blocks while it plays. */
    private fun startWriter(t: AudioTrack, queue: java.util.concurrent.LinkedBlockingQueue<Pair<Int, Speech>>) {
        val g = generation
        writer = Thread {
            try {
                while (!Thread.currentThread().isInterrupted) {
                    val (gen, speech) = queue.take()
                    if (gen != g) continue
                    var off = 0
                    while (off < speech.samples.size && !Thread.currentThread().isInterrupted) {
                        val n = t.write(speech.samples, off, speech.samples.size - off)
                        if (n < 0) return@Thread
                        off += n
                    }
                }
            } catch (_: InterruptedException) {
            } catch (_: Throwable) {
            }
        }.apply { isDaemon = true; start() }
    }

    private fun ensureTrack(rate: Int): AudioTrack? {
        if (rate <= 0) return null
        track?.let { if (trackRate == rate) return it }
        // Another voice at another sample rate: stop the old writer and give the new track one of its own.
        writer?.interrupt()
        writer = null
        pending = java.util.concurrent.LinkedBlockingQueue()
        track?.let {
            runCatching { it.pause(); it.flush() }
            runCatching { it.release() }
        }
        track = null
        val min = AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        if (min <= 0) return null
        val halfSecEven = (rate + 1) and 1.inv() // 16-bit mono = 2 bytes/frame; keep buffer size frame-aligned
        val t = runCatching {
            AudioTrack.Builder()
                .setAudioAttributes(attributes)
                .setAudioFormat(AudioFormat.Builder().setSampleRate(rate).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
                .setBufferSizeInBytes(maxOf(min, halfSecEven)) // half a second
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()
        }.getOrNull() ?: return null
        runCatching { t.playbackParams = PlaybackParams().setSpeed(this.rate).setPitch(1f) }
        if (fading) runCatching { t.setVolume(fadeVolume(sleep, fading, System.currentTimeMillis())) }
        track = t
        trackRate = rate
        scheduled = 0
        return t
    }

    private fun began() {
        val l = queued.firstOrNull() ?: return
        val newSection = spot?.section != l.spot.section
        if (extraPlaying != l.extra) extraPlaying = l.extra
        if (l.extra == null && skippedHead != null && spot != null && spot != l.spot) skippedHead = null
        spot = l.spot
        state = State.Playing
        report()
        if (newSection) Session.update() else Session.updateLine()
        main.removeCallbacks(watch)
        main.postDelayed(watch, 40)
    }

    /** Watches the track's head: when it passes a sentence's end, the next one has begun. */
    private val watch: Runnable = object : Runnable {
        override fun run() {
            if (state != State.Playing && state != State.Loading) return
            val t = track ?: return
            val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0)
            val first = queued.firstOrNull()
            if (first != null && head >= first.endFrame) {
                played()
                return
            }
            if (state == State.Playing || state == State.Loading) main.postDelayed(this, 40)
        }
    }

    private fun played() {
        val done = queued.firstOrNull()?.spot ?: return
        val next = queued.getOrNull(1)?.spot ?: staged.firstOrNull()?.spot ?: nextToRender
        when (val action = nextActionAfterPlayed(done, next, sleep, loop, loopRange, System.currentTimeMillis())) {
            is PlayedAction.SleepAt -> return fallAsleep(action.spot)
            is PlayedAction.LoopTo -> return play(action.spot.position)
            PlayedAction.Advance -> Unit
        }
        queued.removeFirst()
        val wasEmpty = queued.isEmpty()
        feedTrack()
        when {
            queued.isNotEmpty() -> {
                if (wasEmpty && (state == State.Loading || state == State.Playing)) {
                    if (!acquireFocusOrPause()) return
                    runCatching { track?.play() }
                }
                began()
            }
            nextToRender == null && rendering == null -> stop()
            else -> {
                // Rendering fell behind; the next sentence starts as soon as it's ready.
                state = State.Loading
                main.postDelayed(watch, 40)
            }
        }
        render()
    }

    /** Stops between sentences for the sleep timer, paused where it would have gone on. */
    private fun fallAsleep(at: Spot) {
        reset()
        pausedForFocus = false
        releaseFocus()
        val from = sleepFrom
        sleep = Sleep.Off
        sleepFrom = from
        spot = at
        nextToRender = at
        state = State.Paused
        report()
        Session.update()
        onSleep?.invoke(at)
    }

    private fun stopFade(restoreVolume: Boolean = true) {
        if (fading) fading = false
        shake?.stop()
        shake = null
        if (restoreVolume) runCatching { track?.setVolume(1f) }
    }

    private fun sleepChanged(old: Sleep) {
        if (old == Sleep.Off) sleepFrom = spot
        fadeJob?.cancel()
        fadeJob = null
        stopFade(restoreVolume = true)
        val s = sleep as? Sleep.Until ?: return
        fadeJob = scope.launch {
            val lead = s.at - System.currentTimeMillis() - FADE_MS
            if (lead > 0) kotlinx.coroutines.delay(lead)
            // The last 20 seconds fade out; a key or a shake in them gives 10 minutes more.
            while (sleep == s) {
                val left = s.at - System.currentTimeMillis()
                if (state != State.Playing && state != State.Loading) {
                    // Paused/Stopped: nothing fades, and no key is taken for 续 10 分钟; the time passing
                    // meanwhile ends the timer, so playing again doesn't stop a moment later.
                    if (left <= 0) { sleep = Sleep.Off; return@launch }
                    stopFade(restoreVolume = true)
                    kotlinx.coroutines.delay(500)
                    continue
                }
                if (left <= 0 && queued.isEmpty()) {
                    // Expired while loading with no sentence playing: stop now instead of starting a muted sentence.
                    val at = spot ?: nextToRender
                    if (at != null) fallAsleep(at) else sleep = Sleep.Off
                    return@launch
                }
                if (!fading) {
                    fading = true
                    shake = ShakeWatch(context) { extendSleep() }.also { it.start() }
                }
                runCatching { track?.setVolume((left.toFloat() / FADE_MS).coerceIn(0f, 1f)) }
                kotlinx.coroutines.delay(200)
            }
        }
    }

    /** 续 10 分钟: while the timer fades out. */
    fun extendSleep() {
        val s = sleep as? Sleep.Until ?: return
        if (!fading) return
        sleep = Sleep.Until(System.currentTimeMillis() + 600_000, s.minutes)
    }

    private fun failed(message: String) {
        val resumeAt = spot ?: nextToRender
        stop()
        failedSpot = resumeAt
        failure = message
    }

    /** 听读书: the moment of the recording being heard now (whole-recording seconds), or null
     *  when the narrator isn't the one reading. */
    fun recordingNow(): Double? {
        val l = queued.firstOrNull()?.takeIf { it.extra == null } ?: return null
        val start = narrator?.startOf(l.spot.section, l.spot.sentence) ?: return null
        val t = track ?: return null
        val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0)
        return start + ((head - l.startFrame).coerceAtLeast(0)).toDouble() / l.speech.rate
    }

    private fun report() {
        val l = queued.firstOrNull()
        if (l != null) {
            if (l.extra != null) return
            val t = track
            if (t != null) {
                val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0)
                val heard = ((head - l.startFrame).coerceIn(0, l.speech.samples.size.toLong())).toDouble() / l.speech.rate
                onSentence?.invoke(l.spot, l.speech.seconds / rate * 1000, heard / rate * 1000, state == State.Playing)
                return
            }
        }
        val s = spot ?: return
        if (state == State.Paused) {
            onSentence?.invoke(s, 0.0, 0.0, false)
        }
    }

    private fun restartHere() {
        val s = spot ?: return
        if (state == State.Stopped) return
        if (state == State.Paused) seekPaused(s.position) else play(s.position)
    }

    // MARK: Audio focus

    private val attributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()

    private val focusChange = AudioManager.OnAudioFocusChangeListener { change ->
        val t = focusTransition(change, state, pausedForFocus)
        pausedForFocus = t.pausedForFocus
        when (t.action) {
            FocusAction.PauseReleaseFocus -> pauseInternal(keepFocus = false)
            FocusAction.PauseKeepFocus -> pauseInternal(keepFocus = true)
            FocusAction.Resume -> resume()
            FocusAction.None -> if (change == AudioManager.AUDIOFOCUS_LOSS) releaseFocus()
        }
    }
    private var pausedForFocus = false

    private fun takeFocus(): Int {
        val req = focus ?: AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
            .setAudioAttributes(attributes)
            .setAcceptsDelayedFocusGain(true)
            .setOnAudioFocusChangeListener(focusChange, main)
            .build().also { focus = it }
        return audio.requestAudioFocus(req)
    }

    private fun acquireFocusOrPause(): Boolean {
        val t = focusRequestTransition(takeFocus(), state)
        return when (t.action) {
            FocusRequestAction.Play -> {
                pausedForFocus = t.pausedForFocus
                true
            }
            FocusRequestAction.PauseKeepFocus -> {
                pauseInternal(keepFocus = true)
                pausedForFocus = t.pausedForFocus
                false
            }
            FocusRequestAction.PauseReleaseFocus -> {
                pauseInternal(keepFocus = false)
                pausedForFocus = t.pausedForFocus
                releaseFocus()
                false
            }
        }
    }

    private fun releaseFocus() {
        focus?.let { audio.abandonAudioFocusRequest(it) }
    }

    // MARK: The book's sentences

    private fun sentences(section: Int): List<String> = texts.getOrPut(section) { script.sentences(section) }

    private fun text(s: Spot) = sentences(s.section).getOrElse(s.sentence) { "" }

    private fun lang(section: Int): String = script.language(section)

    private fun next(section: Int) = script.next(section)
    private fun previous(section: Int) = script.previous(section)

    /** What the notification calls the section being read. */
    val sectionTitle: String? get() = spot?.let { script.title(it.section) }

    /** `s` itself when it can be read, or the first sentence after it that can. */
    private fun first(s: Spot): Spot? {
        if (sentences(s.section).isNotEmpty() && s.sentence < sentences(s.section).size) return s
        var n = next(s.section)
        while (n != null) {
            if (sentences(n).isNotEmpty()) return Spot(n, 0)
            n = next(n)
        }
        return null
    }

    private fun after(s: Spot): Spot? {
        if (s.sentence + 1 < sentences(s.section).size) return s.copy(sentence = s.sentence + 1)
        var n = next(s.section)
        while (n != null) {
            if (sentences(n).isNotEmpty()) return Spot(n, 0)
            n = next(n)
        }
        return null
    }

    private fun before(s: Spot): Spot? {
        if (s.sentence > 0) return s.copy(sentence = s.sentence - 1)
        var p = previous(s.section)
        while (p != null) {
            val n = sentences(p).size
            if (n > 0) return Spot(p, n - 1)
            p = previous(p)
        }
        return null
    }

    private suspend fun voice(language: String): BookVoice? {
        val cacheKey = canonicalLanguage(language).ifEmpty { language }
        if (voices.containsKey(cacheKey)) return voices[cacheKey]
        val chosen = savedVoiceId(language).takeIf { it.isNotEmpty() }
            ?.let { Voices.find(it, detail.summary.id) }
            ?.takeIf { it.speaks(language) }
        val v = chosen ?: Voices.best(language)
        voices[cacheKey] = v
        return v
    }

    internal sealed interface PlayedAction {
        data class SleepAt(val spot: Spot) : PlayedAction
        data class LoopTo(val spot: Spot) : PlayedAction
        data object Advance : PlayedAction
    }

    internal enum class FocusAction { None, PauseReleaseFocus, PauseKeepFocus, Resume }

    internal data class FocusTransition(val action: FocusAction, val pausedForFocus: Boolean)

    internal enum class FocusRequestAction { Play, PauseKeepFocus, PauseReleaseFocus }

    internal data class FocusRequestTransition(val action: FocusRequestAction, val pausedForFocus: Boolean)

    companion object {
        /** [pauseAfter]: as long as the sentence took. */
        const val PAUSE_AS_LONG = -1.0
        const val RATE_KEY = "player.rate"
        /** The timer's fade, in ms. */
        const val FADE_MS = 20_000L
        private const val AHEAD = 2
        private const val NODE_AHEAD = 2
        internal const val THERMAL_SEVERE = 3
        internal const val THERMAL_CRITICAL = 4
        private val voicePrefChanged = MutableSharedFlow<Unit>(extraBufferCapacity = 1)

        internal fun shouldSleepAt(done: Spot, next: Spot?, sleep: Sleep, nowMs: Long): Boolean = when (sleep) {
            Sleep.Off -> false
            Sleep.Sentence -> true
            Sleep.Chapter -> next == null || next.section != done.section
            is Sleep.Until -> nowMs >= sleep.at
        }

        internal fun nextActionAfterPlayed(
            done: Spot,
            next: Spot?,
            sleep: Sleep,
            loop: Boolean,
            loopRange: Triple<Int, Int, Int>?,
            nowMs: Long,
        ): PlayedAction {
            val loopTarget = when {
                loop && loopRange != null ->
                    if (done.section != loopRange.first || done.sentence >= loopRange.third || done.sentence < loopRange.second) {
                        Spot(loopRange.first, loopRange.second)
                    } else null
                loop -> done
                else -> null
            }
            if (shouldSleepAt(done, next, sleep, nowMs)) {
                return PlayedAction.SleepAt(loopTarget ?: next ?: done)
            }
            if (loopTarget != null) {
                return PlayedAction.LoopTo(loopTarget)
            }
            return PlayedAction.Advance
        }

        internal fun focusRequestTransition(result: Int, @Suppress("UNUSED_PARAMETER") state: State): FocusRequestTransition = when (result) {
            AudioManager.AUDIOFOCUS_REQUEST_GRANTED -> FocusRequestTransition(FocusRequestAction.Play, pausedForFocus = false)
            AudioManager.AUDIOFOCUS_REQUEST_DELAYED -> FocusRequestTransition(FocusRequestAction.PauseKeepFocus, pausedForFocus = true)
            else -> FocusRequestTransition(FocusRequestAction.PauseReleaseFocus, pausedForFocus = false)
        }

        internal fun focusTransition(change: Int, state: State, pausedForFocus: Boolean): FocusTransition = when (change) {
            AudioManager.AUDIOFOCUS_LOSS -> {
                val active = state == State.Playing || state == State.Loading
                FocusTransition(
                    action = if (active) FocusAction.PauseReleaseFocus else FocusAction.None,
                    pausedForFocus = false,
                )
            }
            AudioManager.AUDIOFOCUS_LOSS_TRANSIENT -> {
                val active = state == State.Playing || state == State.Loading
                if (active) FocusTransition(FocusAction.PauseKeepFocus, pausedForFocus = true)
                else FocusTransition(FocusAction.None, pausedForFocus = pausedForFocus)
            }
            AudioManager.AUDIOFOCUS_GAIN -> {
                if (state == State.Paused && pausedForFocus) FocusTransition(FocusAction.Resume, pausedForFocus = false)
                else FocusTransition(FocusAction.None, pausedForFocus = false)
            }
            else -> FocusTransition(FocusAction.None, pausedForFocus = pausedForFocus)
        }

        internal fun fadeVolume(sleep: Sleep, fading: Boolean, nowMs: Long): Float {
            if (!fading) return 1f
            val s = sleep as? Sleep.Until ?: return 1f
            return ((s.at - nowMs).toFloat() / FADE_MS).coerceIn(0f, 1f)
        }

        /** pre-synthesis duration budget (seconds) by `PowerManager` thermal status. */
        internal fun targetAheadSeconds(thermalStatus: Int): Double = when {
            thermalStatus >= THERMAL_CRITICAL -> 0.0
            thermalStatus == THERMAL_SEVERE -> 15.0
            else -> 60.0
        }

        internal fun canonicalLanguage(language: String): String {
            val t = language.replace('_', '-').trim().lowercase()
            return when {
                t == "yue" || t.startsWith("zh-hk") || t.startsWith("zh-mo") -> "yue"
                t == "zh" || t.startsWith("zh-") || t == "cmn" || t.startsWith("cmn-") -> "zh"
                t == "en" || t.startsWith("en-") -> "en"
                else -> t.substringBefore('-')
            }
        }

        internal fun savedVoiceId(language: String, lookup: (String) -> String = { Prefs.string(it) }): String {
            val exact = lookup("player.voice.$language")
            if (exact.isNotEmpty()) return exact
            val canon = canonicalLanguage(language)
            return if (canon.isNotEmpty() && canon != language) lookup("player.voice.$canon") else ""
        }

        internal fun saveVoicePreference(
            language: String,
            voiceId: String,
            notify: Boolean = true,
            write: (String, String?) -> Unit = { k, v -> Prefs.set(k, v) },
        ) {
            val canon = canonicalLanguage(language)
            if (canon == "zh" || canon == "en") {
                write("player.voice.$canon", voiceId)
                val variants = if (canon == "zh") {
                    listOf("zh-Hans", "zh-Hant", "zh-CN", "zh-TW", "zh-SG", "cmn")
                } else {
                    listOf("en-US", "en-GB", "en-AU", "en-CA")
                }
                for (v in variants) {
                    if (v != canon) write("player.voice.$v", null)
                }
                if (language != canon && language !in variants) {
                    write("player.voice.$language", null)
                }
            } else {
                write("player.voice.$language", voiceId)
                if (canon.isNotEmpty() && canon != language) {
                    write("player.voice.$canon", voiceId)
                }
            }
            if (notify) voicePrefChanged.tryEmit(Unit)
        }
    }

    init {
        SystemVoice.Engine.init(context)
        ClonedVoices.init(context)
        scope.launch {
            voicePrefChanged.collect {
                if (state == State.Stopped) {
                    readyOfflineVoice = null
                    voices.clear()
                    voiceVersion++
                }
            }
        }
        scope.launch {
            KokoroModel.readyEvents.collect {
                onOfflineVoiceReady()
            }
        }
        scope.launch {
            LiveVoiceModel.readyEvents.collect {
                onOfflineVoiceReady()
            }
        }
        scope.launch {
            val lib = runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull() ?: return@launch
            lib.dramaSynth.settingsChanged.collect { book ->
                if (book == detail.summary.id) rolesChanged()
            }
        }
    }

    /** 离线模型就绪：停下时清缓存供下次使用；播放或暂停中绝不自动切声，仅提示候选音色由用户确认。 */
    private fun onOfflineVoiceReady() {
        if (state == State.Stopped) {
            readyOfflineVoice = null
            voices.clear()
            voiceVersion++
            return
        }
        if (narrator != null) return
        val lang = language
        if (savedVoiceId(lang).isNotEmpty()) return
        val cacheKey = canonicalLanguage(lang).ifEmpty { lang }
        val cur = voices[cacheKey] ?: voices[lang]
        if (cur is KokoroVoice || cur is CardVoice) return
        val candidate = Voices.defaultNarrator(lang) ?: return
        readyOfflineVoice = candidate
    }

    /** 分角色: who says a line changed; committed sentences finish, later ones use whoever says them now. */
    fun speakersChanged() {
        runCatching { app.yuyue.YuyueApp.instance.library.dramaSynth.invalidateCast(detail.summary.id) }
        roles?.reload()
        chapterLayer = null
        chapterLiveAllMade = null
        dropStaged()
        render()
    }

    /** 分角色: committed sentences on the audio track finish with their current voice; later ones use the new role. */
    fun rolesChanged() {
        runCatching { app.yuyue.YuyueApp.instance.library.dramaSynth.invalidateCast(detail.summary.id) }
        roles?.reload()
        chapterLayer = null
        chapterLiveAllMade = null
        dropStaged()
        render()
    }

    fun startService() {
        PlaybackService.start(context)
    }
}

/** 晃一下: listens for a shake while the sleep timer fades out. */
class ShakeWatch(context: Context, private val shaken: () -> Unit) : android.hardware.SensorEventListener {
    private val sensors = context.getSystemService(Context.SENSOR_SERVICE) as android.hardware.SensorManager
    private var last = 0L

    fun start() {
        sensors.getDefaultSensor(android.hardware.Sensor.TYPE_ACCELEROMETER)?.let { sensors.registerListener(this, it, android.hardware.SensorManager.SENSOR_DELAY_UI) }
    }

    fun stop() = sensors.unregisterListener(this)

    override fun onSensorChanged(e: android.hardware.SensorEvent) {
        val (x, y, z) = Triple(e.values[0], e.values[1], e.values[2])
        val g = kotlin.math.sqrt(x * x + y * y + z * z) / android.hardware.SensorManager.GRAVITY_EARTH
        val now = System.currentTimeMillis()
        if (g > 2.3f && now - last > 1500) {
            last = now
            android.os.Handler(android.os.Looper.getMainLooper()).post(shaken)
        }
    }

    override fun onAccuracyChanged(s: android.hardware.Sensor?, accuracy: Int) {}
}
