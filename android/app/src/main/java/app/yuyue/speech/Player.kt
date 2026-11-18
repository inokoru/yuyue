package app.yuyue.speech

import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.media.PlaybackParams
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.design.Prefs
import app.yuyue.t
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.cancel
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
    private class Line(val spot: Spot, val speech: Speech, var startFrame: Long = 0, var endFrame: Long = 0, val extra: uniffi.yuyue_core.ExtraSpan? = null)
    /** Sentences handed to the track, in order; the first is the one playing. */
    private val queued = ArrayDeque<Line>()
    private var nextToRender: Spot? = null
    private var rendering: Job? = null
    private var writer: Thread? = null
    private var track: AudioTrack? = null
    private var trackRate = 0
    /** Frames handed to the track so far (in its own rate). */
    private var scheduled = 0L
    private val pending = java.util.concurrent.LinkedBlockingQueue<Pair<Int, Speech>>()
    private var texts = mutableMapOf<Int, List<String>>()
    private val voices = mutableMapOf<String, BookVoice?>()
    private var fetching: Pair<Int, Job>? = null
    private var focus: AudioFocusRequest? = null
    private val audio = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    // MARK: Controls

    /** Where the first sentence starts when it isn't its own start (搜索 found a word in it). */
    private var startWord: Pair<Spot, Double>? = null
    /** Shown when critical phone heat temporarily falls back to system voice for the rest of a chapter (§3.4). */
    var thermalNotice by mutableStateOf<String?>(null)
    private var thermalFallbackSection: Int? = null
    private var pendingDefaultUpgrade = false
    private var chapterLayer: Pair<Int, uniffi.yuyue_core.DramaLayer>? = null
    private var chapterLiveAllMade: Pair<Int, Boolean>? = null
    private var lastRenderedSection: Int? = null
    private var lastUsingCardVoice = false

    /** Starts reading at `position`, or at the first sentence after it with something to read.
     *  `word`: in the narrator's voice, the first sentence starts at this moment of the
     *  recording (a word found by 搜索), not at its own start. */
    fun play(from: Position, word: Double? = null) {
        failure = null
        missingVoice = null
        thermalNotice = null
        thermalFallbackSection = null
        reset()
        startWord = word?.let { Spot(from.section.toInt(), from.sentence.toInt()) to it }
        val start = first(Spot(from.section.toInt(), from.sentence.toInt())) ?: return stop()
        spot = start
        nextToRender = start
        state = State.Loading
        // §5.1: speak immediately with SystemVoice while preparing the better voices in the background.
        val langTag = lang(start.section)
        if (Prefs.string("player.voice.$langTag").isEmpty() && CardVoice.speaksLanguage(langTag)) {
            runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull()?.let { lib ->
                if (!LiveVoiceModel.ready(lib.dataDir) && !LiveVoiceModel.deviceUnsupported) {
                    LiveVoiceModel.onFirstListen(context, core, lib.dataDir)
                }
            }
        }
        Session.claim(this)
        render()
    }

    fun pause() {
        if (state != State.Playing && state != State.Loading) return
        // §3.4: stop pre-synthesizing ahead immediately on pause.
        rendering?.cancel()
        rendering = null
        track?.pause()
        state = State.Paused
        report()
        Session.update()
    }

    fun resume() {
        if (state != State.Paused) return
        // §5.1: if the better voice finished downloading while playing or paused, switch on manual resume.
        val cur = spot
        val langTag = cur?.let { lang(it.section) } ?: language
        val lib = runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull()
        val canUpgradeNow = pendingDefaultUpgrade ||
            (lib != null && LiveVoiceModel.ready(lib.dataDir) &&
                Prefs.string("player.voice.$langTag").isEmpty() &&
                voices[langTag] is SystemVoice)
        if (canUpgradeNow && cur != null) {
            pendingDefaultUpgrade = false
            voices.clear()
            play(cur.position)
            return
        }
        if (queued.isEmpty()) {
            state = State.Loading
            return render()
        }
        if (!takeFocus()) return
        track?.play()
        state = State.Playing
        report()
        Session.update()
        render()
    }

    fun toggle() = if (state == State.Playing || state == State.Loading) pause() else resume()

    fun stop() {
        val was = state
        fetching?.second?.cancel()
        fetching = null
        thermalNotice = null
        thermalFallbackSection = null
        reset()
        state = State.Stopped
        spot = null
        releaseFocus()
        Session.update()
        if (was != State.Stopped) onStop?.invoke()
    }

    /** 跳过: past the recording's own talk being played, on to the next sentence. */
    fun skipExtra() {
        val l = queued.firstOrNull() ?: return
        val e = l.extra ?: return
        val next = (if (e.place == uniffi.yuyue_core.ExtraPlace.HEAD) l.spot else after(l.spot)) ?: return stop()
        val paused = state == State.Paused
        play(next.position)
        if (paused) pause()
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
        val paused = state == State.Paused
        play(target.position)
        if (paused) pause()
    }

    fun changeRate(r: Float) {
        rate = r
        Prefs.set(RATE_KEY, r)
        track?.let { runCatching { it.playbackParams = PlaybackParams().setSpeed(r).setPitch(1f) } }
        report()
    }

    /** Reads `language` with another voice from now on, starting the sentence again in it. */
    fun choose(voice: BookVoice, language: String) {
        Prefs.set("player.voice.$language", voice.id)
        voices[language] = voice
        restartHere()
    }

    suspend fun currentVoice(language: String): BookVoice? = voice(language)

    /** The sentence being read or paused on, for the 播放条 and the notification. */
    val line: String? get() = spot?.let { sentences(it.section).getOrNull(it.sentence) }

    /** Sentences in the section being read, for the 播放条's `第 4 / 24 句`. */
    val sentenceCount: Int get() = spot?.let { sentences(it.section).size } ?: 0

    val sectionProgress: Double get() = spot?.let { s -> sentences(s.section).size.takeIf { it > 0 }?.let { (s.sentence + 1.0) / it } } ?: 0.0

    val language: String get() = spot?.let { lang(it.section) } ?: detail.summary.language

    fun textsChanged() = texts.clear()

    /** Stops reading and lets go of the notification. The player can't be used afterwards. */
    fun close() {
        stop()
        Session.release(this)
        scope.cancel()
        track?.release()
        track = null
    }

    // MARK: The queue

    private fun reset() {
        generation++
        extraPlaying = null
        rendering?.cancel()
        rendering = null
        writer?.interrupt()
        writer = null
        pending.clear()
        queued.clear()
        nextToRender = null
        chapterLayer = null
        chapterLiveAllMade = null
        lastRenderedSection = null
        lastUsingCardVoice = false
        runCatching { app.yuyue.YuyueApp.instance.library.recordingFetch.applyPendingCards(detail.summary.id) }
            .getOrNull()?.let { if (it) roles?.reload() }
        track?.let {
            runCatching { it.pause(); it.flush() }
            it.release()
        }
        track = null
        scheduled = 0
        main.removeCallbacks(watch)
    }

    private fun currentThermalStatus(): Int {
        if (android.os.Build.VERSION.SDK_INT < 29) return 0
        val pm = context.getSystemService(Context.POWER_SERVICE) as? android.os.PowerManager ?: return 0
        return runCatching { pm.currentThermalStatus }.getOrDefault(0)
    }

    private fun queuedAheadSeconds(): Double {
        if (queued.isEmpty()) return 0.0
        val r = maxOf(0.5f, rate).toDouble()
        val t = track
        if (t != null && trackRate > 0) {
            val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0L)
            return ((scheduled - head).coerceAtLeast(0L)).toDouble() / trackRate / r
        }
        return queued.sumOf { it.speech.seconds / r }
    }

    private fun shouldKeepRendering(g: Int): Boolean {
        if (generation != g || state == State.Paused || state == State.Stopped) return false
        if (queued.isEmpty()) return true
        if (!lastUsingCardVoice) return queued.size <= AHEAD
        val target = targetAheadSeconds(currentThermalStatus())
        return if (target <= 0.0) queued.size <= AHEAD else queuedAheadSeconds() < target
    }

    /** Renders sentences until enough are queued. */
    private fun render() {
        if (rendering != null || state == State.Paused || state == State.Stopped) return
        val g = generation
        rendering = scope.launch {
            while (shouldKeepRendering(g)) {
                val s = nextToRender ?: break
                val lib = runCatching { app.yuyue.YuyueApp.instance.library }.getOrNull()
                if (lastRenderedSection != s.section) {
                    val enteringNextChapter = lastRenderedSection != null
                    lastRenderedSection = s.section
                    if (lib?.recordingFetch?.applyPendingCards(detail.summary.id) == true) {
                        roles?.reload()
                    }
                    if (enteringNextChapter) {
                        // §3.4: retry CardVoice on the new chapter after thermal fallback.
                        if (thermalFallbackSection != null && thermalFallbackSection != s.section) {
                            thermalFallbackSection = null
                            thermalNotice = null
                        }
                        // §5.1: switch to the newly downloaded default narrator card at chapter boundary.
                        if (pendingDefaultUpgrade && Prefs.string("player.voice.${lang(s.section)}").isEmpty()) {
                            pendingDefaultUpgrade = false
                            voices.clear()
                        }
                        if (lib != null && (lib.dramaSynth.preparing(detail.summary.id) ||
                                runCatching { core.dramaMode(detail.summary.id) }.getOrDefault(false) ||
                                core.dramaBorrowedFrom(detail.summary.id) != null)) {
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
                    if (head != null && gd.isFirst(s.section, s.sentence) && queued.isEmpty()) {
                        if (wantHead || !gd.settings.headTail) {
                            wantHead = false
                            val clip = kotlinx.coroutines.withContext(Dispatchers.IO) { n0.range(head.t0, head.t1) }
                            if (generation != g) return@launch
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
                    if (generation != g) return@launch
                    if (speech != null) {
                        nextToRender = after(s)
                        schedule(Line(s, speech))
                        // The recording's own talk after it, when it's played.
                        gd?.extraAfter(s.section, s.sentence)?.let { e ->
                            val clip = kotlinx.coroutines.withContext(Dispatchers.IO) { n.range(e.t0, e.t1) }
                            if (generation != g) return@launch
                            if (clip != null) schedule(Line(s, clip, extra = e))
                        }
                        continue
                    }
                }
                // §3.1 听成一部剧: check dramaSectionLayer once per chapter so we never switch mid-chapter.
                if (lib != null) {
                    val synth = lib.dramaSynth
                    val layer = chapterLayer?.takeIf { it.first == s.section }?.second ?: run {
                        val l = runCatching { core.dramaSectionLayer(detail.summary.id, s.section.toUInt(), DramaSynth.STUDIO_ENGINE) }
                            .getOrDefault(uniffi.yuyue_core.DramaLayer.LIVE)
                        chapterLayer = s.section to l
                        l
                    }
                    val useMixedDrama = when {
                        layer == uniffi.yuyue_core.DramaLayer.STUDIO -> true
                        core.dramaBorrowedFrom(detail.summary.id) != null -> false
                        synth.preparing(detail.summary.id) -> {
                            chapterLiveAllMade?.takeIf { it.first == s.section }?.second ?: run {
                                val allMade = kotlinx.coroutines.withContext(Dispatchers.IO) { synth.sectionAllMade(detail.summary.id, s.section) }
                                chapterLiveAllMade = s.section to allMade
                                allMade
                            }
                        }
                        else -> false
                    }
                    if (useMixedDrama) {
                        lastUsingCardVoice = false
                        val played = kotlinx.coroutines.withContext(Dispatchers.IO) { synth.sentence(detail.summary.id, s.section, s.sentence) }
                        if (generation != g) return@launch
                        if (played != null) {
                            nextToRender = after(s)
                            schedule(Line(s, played))
                            continue
                        }
                    }
                }
                val language = lang(s.section)
                var voice = roles?.voice(s.section, s.sentence, language) { voice(language) } ?: voice(language)
                if (voice == null) {
                    val name = java.util.Locale.forLanguageTag(language).getDisplayLanguage(java.util.Locale.getDefault()).ifEmpty { language }
                    failed(t("下载一个%@声音就能朗读这本书，系统会带你去它的下载页。", name))
                    missingVoice = name
                    return@launch
                }
                val isCard = voice is CardVoice || (voice is TunedVoice && voice.base is CardVoice)
                if (isCard) {
                    val thermal = currentThermalStatus()
                    if (thermal >= THERMAL_CRITICAL || thermalFallbackSection == s.section) {
                        thermalFallbackSection = s.section
                        if (thermalNotice == null) thermalNotice = t("手机有点热，这一章先用系统声音")
                        voice = Voices.systemBest(language) ?: voice
                        lastUsingCardVoice = false
                    } else {
                        lastUsingCardVoice = true
                    }
                } else {
                    lastUsingCardVoice = false
                }
                if (voice is CloudVoice && roles?.any != true) fetchAhead(voice, s, language)
                val speech = try {
                    voice.render(text(s), language)
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    failed(t("朗读出错了（%@）", e.message ?: e.toString()))
                    return@launch
                }
                if (generation != g) return@launch
                nextToRender = after(s)
                // A sentence with nothing to say (a lone dash, say) is skipped.
                if (speech != null) schedule(Line(s, speech))
            }
            if (generation != g) return@launch
            rendering = null
            // The book is over once the last sentence has played.
            if (queued.isEmpty() && nextToRender == null && state == State.Loading) stop()
        }
    }

    /** 整章缓存: a cloud voice fetches the rest of the chapter in the background. */
    private fun fetchAhead(voice: CloudVoice, from: Spot, language: String) {
        if (fetching?.first == from.section) return
        fetching?.second?.cancel()
        val rest = sentences(from.section).drop(from.sentence + 1)
        fetching = from.section to scope.launch(Dispatchers.IO) { voice.prefetch(rest, language) }
    }

    private fun schedule(line: Line) {
        val l = if (line.extra == null && pauseAfter != 0.0) Line(line.spot, paced(line.speech)) else line
        val t = ensureTrack(l.speech.rate) ?: return
        l.startFrame = scheduled
        l.endFrame = scheduled + l.speech.samples.size
        scheduled = l.endFrame
        queued.addLast(l)
        pending.put(generation to l.speech)
        if (writer == null) startWriter(t)
        if (queued.size == 1 && (state == State.Loading || state == State.Playing)) {
            if (!takeFocus()) return
            t.play()
            began()
        }
    }

    /** The sentences go to the track from a thread of their own: writing blocks while it plays. */
    private fun startWriter(t: AudioTrack) {
        val g = generation
        writer = Thread {
            try {
                while (!Thread.currentThread().isInterrupted) {
                    val (gen, speech) = pending.take()
                    if (gen != g) continue
                    var off = 0
                    while (off < speech.samples.size && !Thread.currentThread().isInterrupted) {
                        val n = t.write(speech.samples, off, speech.samples.size - off)
                        if (n < 0) return@Thread
                        off += n
                    }
                }
            } catch (_: InterruptedException) {
            } catch (_: IllegalStateException) {
            }
        }.apply { isDaemon = true; start() }
    }

    private fun ensureTrack(rate: Int): AudioTrack? {
        track?.let { if (trackRate == rate) return it }
        // Another voice at another sample rate: a track of its own.
        track?.release()
        val min = AudioTrack.getMinBufferSize(rate, AudioFormat.CHANNEL_OUT_MONO, AudioFormat.ENCODING_PCM_16BIT)
        val t = AudioTrack.Builder()
            .setAudioAttributes(attributes)
            .setAudioFormat(AudioFormat.Builder().setSampleRate(rate).setChannelMask(AudioFormat.CHANNEL_OUT_MONO).setEncoding(AudioFormat.ENCODING_PCM_16BIT).build())
            .setBufferSizeInBytes(maxOf(min, rate)) // half a second
            .setTransferMode(AudioTrack.MODE_STREAM)
            .build()
        runCatching { t.playbackParams = PlaybackParams().setSpeed(this.rate).setPitch(1f) }
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
        val range = loopRange
        if (loop && range != null) {
            // A few sentences: at the last (or once outside them), back to the first.
            if (done.section != range.first || done.sentence >= range.third || done.sentence < range.second) return play(Spot(range.first, range.second).position)
        } else if (loop) return play(done.position)
        val next = queued.getOrNull(1)?.spot ?: nextToRender
        if (shouldSleep(done, next)) return fallAsleep(next ?: done)
        queued.removeFirst()
        when {
            queued.isNotEmpty() -> began()
            nextToRender == null && rendering == null -> stop()
            else -> {
                // Rendering fell behind; the next sentence starts as soon as it's ready.
                state = State.Loading
                main.postDelayed(watch, 40)
            }
        }
        render()
    }

    private fun shouldSleep(done: Spot, next: Spot?): Boolean = when (val s = sleep) {
        Sleep.Off -> false
        Sleep.Sentence -> true
        Sleep.Chapter -> next == null || next.section != done.section
        is Sleep.Until -> System.currentTimeMillis() >= s.at
    }

    /** Stops between sentences for the sleep timer, paused where it would have gone on. */
    private fun fallAsleep(at: Spot) {
        reset()
        val from = sleepFrom
        sleep = Sleep.Off
        sleepFrom = from
        spot = at
        nextToRender = at
        state = State.Paused
        Session.update()
        onSleep?.invoke(at)
    }

    private fun sleepChanged(old: Sleep) {
        if (old == Sleep.Off) sleepFrom = spot
        fadeJob?.cancel()
        fading = false
        shake?.stop()
        shake = null
        runCatching { track?.setVolume(1f) }
        val s = sleep as? Sleep.Until ?: return
        fadeJob = scope.launch {
            val lead = s.at - System.currentTimeMillis() - FADE_MS
            if (lead > 0) kotlinx.coroutines.delay(lead)
            // The last 20 seconds fade out; a key or a shake in them gives 10 minutes more.
            while (sleep == s) {
                val left = s.at - System.currentTimeMillis()
                if (state != State.Playing && state != State.Loading) {
                    // Paused: nothing fades, and no key is taken for 续 10 分钟; the time passing
                    // meanwhile ends the timer, so playing again doesn't stop a moment later.
                    if (left <= 0) { sleep = Sleep.Off; return@launch }
                    if (fading) {
                        fading = false
                        shake?.stop()
                        shake = null
                        runCatching { track?.setVolume(1f) }
                    }
                    kotlinx.coroutines.delay(500)
                    continue
                }
                if (!fading) {
                    fading = true
                    shake = ShakeWatch(context) { extendSleep() }.also { it.start() }
                }
                runCatching { track?.setVolume((left.toFloat() / FADE_MS).coerceIn(0f, 1f)) }
                if (left <= 0) break
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
        stop()
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
        val l = queued.firstOrNull()?.takeIf { it.extra == null } ?: return
        val t = track ?: return
        val head = runCatching { t.playbackHeadPosition.toLong() and 0xffffffffL }.getOrDefault(0)
        val heard = ((head - l.startFrame).coerceIn(0, l.speech.samples.size.toLong())).toDouble() / l.speech.rate
        onSentence?.invoke(l.spot, l.speech.seconds / rate * 1000, heard / rate * 1000, state == State.Playing)
    }

    private fun restartHere() {
        val s = spot ?: return
        if (state == State.Stopped) return
        val paused = state == State.Paused
        play(s.position)
        if (paused) pause()
    }

    // MARK: Audio focus

    private val attributes = AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_SPEECH).build()

    private val focusChange = AudioManager.OnAudioFocusChangeListener { change ->
        when (change) {
            AudioManager.AUDIOFOCUS_LOSS, AudioManager.AUDIOFOCUS_LOSS_TRANSIENT -> pause()
            AudioManager.AUDIOFOCUS_GAIN -> if (state == State.Paused && pausedForFocus) resume()
        }
        pausedForFocus = change == AudioManager.AUDIOFOCUS_LOSS_TRANSIENT
    }
    private var pausedForFocus = false

    private fun takeFocus(): Boolean {
        val req = focus ?: AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN).setAudioAttributes(attributes).setOnAudioFocusChangeListener(focusChange).build().also { focus = it }
        return audio.requestAudioFocus(req) == AudioManager.AUDIOFOCUS_REQUEST_GRANTED
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
        if (voices.containsKey(language)) return voices[language]
        val chosen = Prefs.string("player.voice.$language").takeIf { it.isNotEmpty() }?.let { Voices.find(it, detail.summary.id) }
        val v = chosen ?: Voices.defaultNarrator(language) ?: Voices.systemBest(language)
        voices[language] = v
        return v
    }

    companion object {
        /** [pauseAfter]: as long as the sentence took. */
        const val PAUSE_AS_LONG = -1.0
        const val RATE_KEY = "player.rate"
        /** The timer's fade, in ms. */
        const val FADE_MS = 20_000L
        private const val AHEAD = 2
        internal const val THERMAL_SEVERE = 3
        internal const val THERMAL_CRITICAL = 4

        /** §3.4 pre-synthesis duration budget (seconds) by `PowerManager` thermal status. */
        internal fun targetAheadSeconds(thermalStatus: Int): Double = when {
            thermalStatus >= THERMAL_CRITICAL -> 0.0
            thermalStatus == THERMAL_SEVERE -> 15.0
            else -> 60.0
        }
    }

    init {
        SystemVoice.Engine.init(context)
        ClonedVoices.init(context)
        scope.launch {
            LiveVoiceModel.readyEvents.collect {
                pendingDefaultUpgrade = true
                if (state == State.Paused) voices.clear()
            }
        }
    }

    /** 分角色: who says a line changed; the next sentences are read by whoever says them now. */
    fun speakersChanged() {
        roles?.reload()
    }

    /** 分角色: someone's voice, pace or pitch changed; read on from this sentence with it. */
    fun rolesChanged() {
        roles?.reload()
        restartHere()
    }

    fun startService() {
        context.startService(Intent(context, PlaybackService::class.java))
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
