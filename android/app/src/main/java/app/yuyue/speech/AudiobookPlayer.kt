package app.yuyue.speech

import android.content.Context
import android.content.Intent
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import app.yuyue.design.Prefs
import app.yuyue.library.Library
import app.yuyue.t
import uniffi.yuyue_core.AudioChapter
import uniffi.yuyue_core.AudioRecording
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.Bookmark
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.ExtraPlace
import uniffi.yuyue_core.ExtraSpan
import uniffi.yuyue_core.SkipSettings
import uniffi.yuyue_core.SyncBy
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File
import java.time.LocalDate
import kotlin.math.roundToLong

/**
 * 有声书 listened to on its own, as on Apple: the recording's files one
 * after another in ExoPlayer, by time. A position is a chapter and the milliseconds into it, saved
 * like any book's, so progress, 书签 and 听了多久 follow the book to other devices.
 *
 * Speed keeps the pitch; −15 s and +30 s; after a pause of a while it goes back a few seconds;
 * 定时 stops after some minutes or at the end of the chapter. The notification, lock screen,
 * headphones and car take the same controls: their 上一曲 / 下一曲 are −15 s and +30 s.
 */
@UnstableApi
class AudiobookPlayer(private val context: Context, private val library: Library, book: BookSummary) {
    val bookID = book.id
    var summary by mutableStateOf(book)
        private set
    var recording by mutableStateOf<AudioRecording?>(runCatching { library.core.audioRecording(book.id) }.getOrNull())
        private set
    var isPlaying by mutableStateOf(false)
        private set
    /** Seconds from the start of the whole recording. */
    var time by mutableDoubleStateOf(0.0)
        private set
    var rate by mutableStateOf(Prefs.float(RATE_KEY, 1f).takeIf { it > 0 } ?: 1f)
        private set
    var sleep by mutableStateOf<Player.Sleep>(Player.Sleep.Off)
        private set
    var bookmarks by mutableStateOf(runCatching { library.core.bookmarks(book.id) }.getOrDefault(emptyList()))
        private set
    var failure by mutableStateOf<String?>(if (recording == null) t("这本有声书的录音不在这台设备上") else null)
    /** A call (or the navigation's voice) has the sound for a moment: shown as paused, and it
     *  goes on by itself when they're done. */
    var waiting by mutableStateOf(false)
        private set
    /** 这本录音. */
    var settings by mutableStateOf(runCatching { library.core.audiobookSkipSettings(book.id) }.getOrNull() ?: SkipSettings(true, false, true, false, false, false, false))
        private set
    /** Laid against its e-book: sentences to step by, extras to pass. */
    var hasMap by mutableStateOf(false)
        private set
    var unreadCount by mutableStateOf(0)
        private set
    /** Seconds 缩短停顿 has saved on this recording. */
    var saved by mutableDoubleStateOf(0.0)
        private set
    /** What's happening that isn't the book: 片头 passed, talk not in the book, 朗读补上. */
    var notice by mutableStateOf<Notice?>(null)
        private set
    /** 划了一句, with 撤销. */
    var toast by mutableStateOf<Library.Toast?>(null)
    /** The timer's last 20 seconds. */
    var fading by mutableStateOf(false)
        private set
    /** 回到昨晚: the timer stopped it, set at these seconds (and when). */
    var sleepReturn by mutableStateOf<Pair<Double, Long>?>(null)
        private set
    /** 进度冲突: another device is further on (跳过去 / 留在这里). */
    var elsewhere by mutableStateOf<uniffi.yuyue_core.Progress?>(null)

    /** 跳过去: on to where the other device is. */
    fun jumpElsewhere() {
        val e = elsewhere ?: return
        elsewhere = null
        seekTo(seconds(e.position))
    }

    /** Where another device's place is in the recording, for the card. */
    fun secondsOf(p: uniffi.yuyue_core.Progress): Double = seconds(p.position)

    /** Laid against its e-book: each chapter named as the book names it (第八回, 孔乙己), not as the
     *  files' tags do (`01 - kong yi ji`). */
    var bookTitles by mutableStateOf<Map<Int, String>>(emptyMap())
        private set

    data class Notice(val kind: Kind, val text: String, val action: String?, val extra: ExtraSpan? = null) {
        enum class Kind { Head, Extra, Filling, Fetching }
    }

    /** Sentences the recording doesn't read, between two it does. */
    private class UnreadRun(val at: Double, val resume: Double, val texts: List<String>)

    private val core get() = library.core
    private val main = Handler(Looper.getMainLooper())
    /** Where each file starts in the whole recording. */
    // Worked out each time: a file that came from the share may know its length now.
    private val starts: List<Double> get() = recording?.files?.runningFold(0.0) { at, f -> at + f.seconds }?.dropLast(1) ?: emptyList()
    private var pausedAt = 0L
    private var listenSince = 0L
    private var savedAt = 0L
    private var closed = false
    private var sleepFrom: Double? = null
    /** 念完这句: where the sentence ends. */
    private var sentenceEnd = 0.0
    /** An extra listened to on purpose (点这里听): not passed again until it's over. */
    private var passing: ExtraSpan? = null
    private var unsavedSaving = 0.0
    private var runs: List<UnreadRun> = emptyList()
    private var language = "zh"
    private var tts: android.speech.tts.TextToSpeech? = null
    /** The sentences being read in place of the recording, while they are. */
    private var fillingRun: UnreadRun? = null
    private var shake: ShakeWatch? = null
    private var gain = Prefs.float(gainKey(book.id), 0f)
    private var eq: android.media.audiofx.Equalizer? = null
    private var boost: android.media.audiofx.LoudnessEnhancer? = null
    private var dynamics: android.media.audiofx.DynamicsProcessing? = null

    private val exo: ExoPlayer = ExoPlayer.Builder(context)
        .setAudioAttributes(AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(C.AUDIO_CONTENT_TYPE_SPEECH).build(), true)
        .setHandleAudioBecomingNoisy(true)
        .setSeekBackIncrementMs((BACK * 1000).toLong())
        .setSeekForwardIncrementMs((AHEAD * 1000).toLong())
        .build()

    /** What the session hands the notification: 上一曲 / 下一曲 move by seconds, not files. */
    val sessionPlayer: androidx.media3.common.Player = object : ForwardingPlayer(exo) {
        override fun getAvailableCommands() = super.getAvailableCommands().buildUpon()
            .add(COMMAND_SEEK_TO_NEXT).add(COMMAND_SEEK_TO_PREVIOUS).add(COMMAND_SEEK_BACK).add(COMMAND_SEEK_FORWARD).build()
        override fun isCommandAvailable(command: Int) = command in listOf(COMMAND_SEEK_TO_NEXT, COMMAND_SEEK_TO_PREVIOUS, COMMAND_SEEK_BACK, COMMAND_SEEK_FORWARD) || super.isCommandAvailable(command)
        // Headphones and cars send 上一曲 / 下一曲: a sentence, or 15 and 30 seconds. Any key
        // while the timer fades out: 10 minutes more instead.
        override fun seekToNext() = key { step(true) }
        override fun seekToNextMediaItem() = key { step(true) }
        override fun seekToPrevious() = key { step(false) }
        override fun seekToPreviousMediaItem() = key { step(false) }
        override fun seekBack() = key { skip(-BACK) }
        override fun seekForward() = key { skip(AHEAD) }
        override fun play() = key { this@AudiobookPlayer.play() }
        override fun pause() = key { this@AudiobookPlayer.pause() }
        override fun setPlayWhenReady(playWhenReady: Boolean) = key { if (playWhenReady) this@AudiobookPlayer.play() else this@AudiobookPlayer.pause() }
    }

    private val listener = object : androidx.media3.common.Player.Listener {
        override fun onIsPlayingChanged(playing: Boolean) {
            if (closed) return
            readTime()
            // Held for the voice reading what the recording leaves out: still playing.
            if (playing != isPlaying && !exo.playWhenReady && tts == null) {
                // Paused from outside (headphones out, a call): as if 暂停 were pressed.
                stopped()
            }
        }

        override fun onPlaybackStateChanged(state: Int) {
            if (state == androidx.media3.common.Player.STATE_ENDED) finished()
        }

        // 边下边听: into a file that hasn't come from the share yet — wait for it.
        override fun onMediaItemTransition(item: androidx.media3.common.MediaItem?, reason: Int) {
            if (!closed) checkHere()
        }

        override fun onPlayerError(error: androidx.media3.common.PlaybackException) {
            if (!closed) checkHere()
        }

        override fun onPlaybackSuppressionReasonChanged(reason: Int) {
            if (closed) return
            val held = reason == androidx.media3.common.Player.PLAYBACK_SUPPRESSION_REASON_TRANSIENT_AUDIO_FOCUS_LOSS
            if (held && isPlaying) {
                // The player still means to play; to the reader it's paused until the call ends.
                readTime()
                isPlaying = false
                waiting = true
                listened()
                save(now = true)
                main.removeCallbacks(tick)
            } else if (!held && waiting) {
                waiting = false
                if (exo.playWhenReady) {
                    isPlaying = true
                    listened()
                    main.removeCallbacks(tick)
                    main.post(tick)
                }
            }
        }
    }

    private fun key(action: () -> Unit) = if (fading) extendSleep() else action()

    private val tick = object : Runnable {
        override fun run() {
            if (closed) return
            onTick()
            main.postDelayed(this, 150)
        }
    }

    init {
        recording?.let { r ->
            val mine = runCatching { core.progress(bookID) }.getOrNull()
            val latest = runCatching { core.latestProgress(bookID) }.getOrNull()
            val p = mine ?: latest
            if (p != null) time = seconds(p.position)
            // 进度冲突: another device listened on later, half a minute or more from here.
            if (mine != null && latest != null && !latest.thisDevice && latest.at > mine.at && kotlin.math.abs(seconds(latest.position) - time) >= 30) elsewhere = latest
            exo.setMediaItems(r.files.mapIndexed { i, f ->
                MediaItem.Builder().setUri(android.net.Uri.fromFile(File(f.path))).setMediaId("$bookID/$i").setMediaMetadata(meta(i)).build()
            })
            exo.playbackParameters = PlaybackParameters(rate, 1f)
            exo.addListener(listener)
            seekExo(time)
            exo.prepare()
            effects()
        }
        loadMap()
        offerSleepReturn()
        measureLoudness()
        if (streaming) {
            library.shareStream.tidy(bookID, currentFile)
            recording = runCatching { core.audioRecording(bookID) }.getOrNull() ?: recording
        }
    }

    // MARK: 边下边听

    /** Waiting for this file to come from the share: shown on the play button (a ring filling)
     *  and a line under it. */
    var waitingFor by mutableStateOf<Int?>(null)
        private set

    /** 边下边听's fetching, for the screen. */
    val stream get() = library.shareStream

    /** Whether this recording comes from a share. */
    val streaming: Boolean get() = library.shareStream.streams(bookID)

    /** Whether file `i` is on this phone. */
    fun isHere(file: Int): Boolean {
        val f = recording?.files?.getOrNull(file) ?: return true
        return File(f.path).isFile
    }

    /** The file chapter `i` starts in. */
    fun fileOf(chapter: Int): Int {
        val c = chapters.getOrNull(chapter) ?: return 0
        return maxOf(0, starts.indexOfLast { it <= c.start + 0.01 })
    }

    fun offDevice(chapter: Int) = streaming && !isHere(fileOf(chapter))

    /** Files here, of all. */
    val filesHere: Pair<Int, Int> get() {
        val r = recording ?: return 0 to 0
        return r.files.indices.count { isHere(it) } to r.files.size
    }

    /** In the chapter now: how far into it is here to play on from its start (the file coming
     *  counted as far as it has come), and where each file after its first starts in it. */
    val hereInChapter: Pair<Double, List<Double>> get() {
        val r = recording ?: return 0.0 to emptyList()
        val c = chapters.getOrNull(chapter) ?: return 0.0 to emptyList()
        val end = c.start + c.seconds
        val coming = library.shareStream.fetching[bookID]
        var down = 0.0
        val ticks = mutableListOf<Double>()
        var open = true
        for ((i, s) in starts.withIndex()) {
            val e = s + r.files[i].seconds
            if (e <= c.start + 0.01 || s >= end - 0.01) continue
            if (s > c.start + 0.01) ticks += s - c.start
            if (!open) continue
            if (isHere(i)) down = minOf(end, e) - c.start
            else {
                if (coming?.first == i) down = maxOf(down, minOf(end, s + r.files[i].seconds * coming.second) - c.start)
                open = false
            }
        }
        return maxOf(0.0, down) to ticks
    }

    /** The file being listened to. */
    val currentFile: Int get() = maxOf(0, starts.indexOfLast { it <= time })

    /** 听过的自动清掉, for this recording. */
    var clearsListened by mutableStateOf(library.shareStream.clears(bookID))
        private set

    fun changeClearsListened(on: Boolean) {
        clearsListened = on
        library.shareStream.setClears(bookID, on, currentFile)
        recording = runCatching { core.audioRecording(bookID) }.getOrNull() ?: recording
    }

    /** The file playing isn't here yet: it's fetched first, and playing goes on when it comes. */
    private fun checkHere() {
        val r = recording ?: return
        val i = exo.currentMediaItemIndex
        if (i !in r.files.indices || File(r.files[i].path).isFile) return
        waitingFor = i
        exo.pause()
        library.shareStream.want(bookID, i)
    }

    private val arrivals = library.scope.launch {
        library.shareStream.arrived.collect { (id, n) ->
            if (id != bookID || closed) return@collect
            recording = runCatching { core.audioRecording(bookID) }.getOrNull() ?: recording
            if (waitingFor == n) {
                waitingFor = null
                exo.prepare()
                seekExo(time)
                if (isPlaying) exo.play()
            }
        }
    }

    // MARK: 这本录音

    /** The map, the settings and what follows from them, again. */
    fun loadMap() {
        settings = runCatching { core.audiobookSkipSettings(bookID) }.getOrNull() ?: settings
        saved = runCatching { core.audiobookSaved(bookID) }.getOrDefault(0.0)
        effects()
        val map = runCatching { core.audiobookMap(bookID) }.getOrNull()?.takeIf { m -> m.rows.any { it.by != SyncBy.UNREAD } }
        if (map == null) {
            hasMap = false
            unreadCount = 0
            runs = emptyList()
            bookTitles = emptyMap()
            return
        }
        hasMap = true
        unreadCount = map.rows.count { it.by == SyncBy.UNREAD }
        runCatching { core.book(map.bookId) }.getOrNull()?.let { detail ->
            val titles = HashMap<Int, String>()
            var last: UInt? = null
            map.chapters.forEachIndexed { i, c ->
                val s = c.bookSection ?: return@forEachIndexed
                val name = app.yuyue.reader.ReaderModel.chapterTitle(detail, s.toInt()) ?: return@forEachIndexed
                // A chapter of the book read over two of the recording's.
                titles[i] = if (s == last) t("%@（续）", name) else name
                last = s
            }
            bookTitles = titles
        }
        language = runCatching { core.book(map.bookId).summary.language }.getOrDefault("zh")
        val texts = HashMap<UInt, List<String>>()
        val out = ArrayList<UnreadRun>()
        var lastEnd: Double? = null
        val pending = ArrayList<String>()
        for (r in map.rows) {
            if (r.by == SyncBy.UNREAD) {
                val t = texts.getOrPut(r.section) { runCatching { core.sentences(map.bookId, r.section) }.getOrDefault(emptyList()) }
                t.getOrNull(r.sentence.toInt())?.let { pending.add(it) }
                continue
            }
            if (pending.isNotEmpty() && lastEnd != null) out.add(UnreadRun(lastEnd, r.t0Ms.toDouble() / 1000, pending.toList()))
            pending.clear()
            lastEnd = r.t1Ms.toDouble() / 1000
        }
        runs = out
    }

    fun set(s: SkipSettings) {
        settings = s
        library.recordings.set(s, bookID)
        effects()
    }

    /** 全书还剩: what's left, less what 缩短停顿 will save of it. */
    val remaining: Double
        get() {
            val left = maxOf(0.0, total - time)
            if (!settings.shortenPauses || !hasMap) return left
            return maxOf(0.0, left - runCatching { core.audiobookPauseSavings(bookID, time) }.getOrDefault(0.0))
        }

    /** 响度统一 (always) and 人声清楚一点: on the player's own audio session. */
    private fun effects() {
        val session = exo.audioSessionId
        if (session == 0) return
        val mb = if (gain > 0f) (2000 * kotlin.math.log10(gain.toDouble())).toInt() else 0
        runCatching {
            if (boost == null) boost = android.media.audiofx.LoudnessEnhancer(session)
            boost?.setTargetGain(maxOf(0, mb))
            boost?.enabled = mb > 0
        }
        exo.volume = if (mb < 0) gain else 1f
        var usedDynamics = false
        if (android.os.Build.VERSION.SDK_INT >= 28) {
            runCatching {
                if (settings.clearerVoice) {
                    val dp = dynamics ?: run {
                        val cfg = android.media.audiofx.DynamicsProcessing.Config.Builder(
                            android.media.audiofx.DynamicsProcessing.VARIANT_FAVOR_FREQUENCY_RESOLUTION,
                            1, true, 3, true, 1, false, 0, true,
                        ).build()
                        // 80 Hz rumble cut, 3 kHz presence +3 dB, 3:1 over -24 dBFS, -1 dBFS ceiling (as in VoiceTap).
                        val pre = cfg.getChannelByChannelIndex(0).preEq
                        pre.getBand(0).apply { isEnabled = true; cutoffFrequency = 100f; gain = -12f }
                        pre.getBand(1).apply { isEnabled = true; cutoffFrequency = 1800f; gain = 0f }
                        pre.getBand(2).apply { isEnabled = true; cutoffFrequency = 4800f; gain = 3f }
                        cfg.getChannelByChannelIndex(0).mbc.getBand(0).apply {
                            isEnabled = true; cutoffFrequency = 20_000f
                            attackTime = 10f; releaseTime = 150f; ratio = 3f
                            threshold = -24f; kneeWidth = 6f; noiseGateThreshold = -75f; expanderRatio = 1f
                            preGain = 0f; postGain = 1.5f
                        }
                        cfg.getChannelByChannelIndex(0).limiter.apply {
                            isEnabled = true; linkGroup = 0; attackTime = 2f; releaseTime = 60f
                            ratio = 10f; threshold = -1f; postGain = 0f
                        }
                        android.media.audiofx.DynamicsProcessing(0, session, cfg).also { dynamics = it }
                    }
                    dp.enabled = true
                    eq?.enabled = false
                    usedDynamics = true
                } else {
                    dynamics?.enabled = false
                }
            }
        }
        if (!usedDynamics) {
            runCatching {
                if (settings.clearerVoice) {
                    val e = eq ?: android.media.audiofx.Equalizer(0, session).also { eq = it }
                    val (lo, hi) = e.bandLevelRange.let { it[0] to it[1] }
                    for (b in 0 until e.numberOfBands) {
                        val hz = e.getCenterFreq(b.toShort()) / 1000
                        val level = when {
                            hz < 120 -> -900
                            hz in 2000..4500 -> 300
                            else -> 0
                        }
                        e.setBandLevel(b.toShort(), level.coerceIn(lo.toInt(), hi.toInt()).toShort())
                    }
                    e.enabled = true
                } else {
                    eq?.enabled = false
                }
            }
        }
    }

    /** 响度统一: measured once, kept. */
    private fun measureLoudness() {
        if (gain > 0f) return
        val r = recording ?: return
        val files = r.files.map { File(it.path) to it.seconds }
        library.scope.launch(Dispatchers.IO) {
            val voice = NarratorVoice(files, emptyMap())
            var sum = 0.0
            var n = 0
            for (k in 0 until 10) {
                val at = total * (k + 0.5) / 10
                val clip = voice.range(at, minOf(total, at + 5)) ?: continue
                val block = clip.rate / 20
                var i = 0
                while (i + block <= clip.samples.size) {
                    var ms = 0.0
                    for (j in i until i + block) { val v = clip.samples[j] / 32768.0; ms += v * v }
                    ms /= block
                    if (ms > 1e-5) { sum += ms; n++ }
                    i += block
                }
            }
            val g = if (n == 0) 1f else Math.pow(10.0, (-20 - 10 * kotlin.math.log10(sum / n)).coerceIn(-6.0, 12.0) / 20).toFloat()
            Prefs.set(gainKey(bookID), g)
            main.post { gain = g; effects() }
        }
    }

    /** ← →, 上一首 / 下一首: a sentence, once laid against the e-book; 15 seconds either way otherwise. */
    fun step(forward: Boolean) {
        val t = if (hasMap) runCatching { core.audiobookStep(bookID, time, forward) }.getOrNull() else null
        if (t != null) seekTo(t) else skip(if (forward) BACK else -BACK)
    }

    /** 点这里听 / 跳过 on the notice. */
    fun noticeAction() {
        val n = notice ?: return
        notice = null
        when (n.kind) {
            Notice.Kind.Head -> n.extra?.let { passing = it; seekTo(it.t0); if (!isPlaying) play() }
            Notice.Kind.Extra -> n.extra?.let { passing = null; seekTo(it.t1) }
            Notice.Kind.Filling -> {}
            Notice.Kind.Fetching -> notice = n
        }
    }

    fun dismissNotice() {
        if (notice?.kind == Notice.Kind.Head) notice = null
    }

    /** What the map says about the moment: pass it, cut it short, say what it is. True when
     *  playing went somewhere else. */
    private fun follow(was: Double): Boolean {
        if (!hasMap) return false
        val m = runCatching { core.audiobookMoment(bookID, time) }.getOrNull() ?: return false
        passing?.let { if (m.extra == it) return false; passing = null }
        val to = m.skipTo
        if (to != null) {
            val e = m.extra
            if (e != null) {
                if (e.place == ExtraPlace.TAIL && to >= total - 0.5) {
                    pause()
                    return true
                }
                if (e.place == ExtraPlace.HEAD) notice = Notice(Notice.Kind.Head, t("跳过了片头 %@ · 点这里听", clock(e.t1 - e.t0)), t("点这里听"), e)
            } else {
                unsavedSaving += to - time
                saved += to - time
            }
            time = to
            seekExo(to)
            return true
        }
        val e = m.extra
        if (e != null && e.place == ExtraPlace.MIDDLE) {
            val n = Notice(Notice.Kind.Extra, t("不在书里的话 · %@", clock(e.t1 - e.t0)), t("跳过"), e)
            if (notice != n) notice = n
        } else if (notice?.kind == Notice.Kind.Extra) notice = null
        // 没念的用朗读补上: at the end of the sentence before them.
        if (settings.fillUnread && time > was) {
            runs.firstOrNull { was < it.at && time >= it.at - 0.05 }?.let { fill(it); return true }
        }
        if (sleep == Player.Sleep.Sentence && time >= sentenceEnd - 0.05) {
            fallAsleep()
            return true
        }
        return false
    }

    /** Reads what the recording leaves out with a voice, then on from where it picks up again. */
    private fun fill(run: UnreadRun) {
        fillingRun = run
        notice = Notice(Notice.Kind.Filling, t("录音里没念这里，朗读补上"), null)
        val last = "fill-${run.at}"
        var engine: android.speech.tts.TextToSpeech? = null
        engine = android.speech.tts.TextToSpeech(context) { status ->
            val e = engine ?: return@TextToSpeech
            if (status != android.speech.tts.TextToSpeech.SUCCESS || tts !== e) return@TextToSpeech
            e.language = java.util.Locale.forLanguageTag(language)
            e.setSpeechRate(rate.coerceIn(0.8f, 1.6f))
            e.setOnUtteranceProgressListener(object : android.speech.tts.UtteranceProgressListener() {
                override fun onStart(id: String?) {}
                @Deprecated("old") override fun onError(id: String?) { if (id == last) main.post { filled(e, run) } }
                override fun onDone(id: String?) { if (id == last) main.post { filled(e, run) } }
            })
            run.texts.forEachIndexed { k, text ->
                e.speak(text, android.speech.tts.TextToSpeech.QUEUE_ADD, null, if (k == run.texts.lastIndex) last else "fill-$k")
            }
        }
        tts = engine
        // After `tts` is set, so the player's listener sees a hold, not a pause.
        exo.pause()
    }

    private fun filled(e: android.speech.tts.TextToSpeech, run: UnreadRun) {
        if (tts !== e) return
        tts = null
        fillingRun = null
        e.shutdown()
        notice = null
        time = run.resume
        seekExo(run.resume)
        if (isPlaying) exo.play()
    }

    private fun stopFilling() {
        val e = tts ?: return
        tts = null
        fillingRun = null
        e.stop()
        e.shutdown()
        if (notice?.kind == Notice.Kind.Filling) notice = null
    }

    /** The bookmark key isn't sent by Android's media controls; 划线 is the same from the page. */
    fun highlightHeard() {
        val s = if (hasMap) runCatching { core.audiobookHeardSentence(bookID, time) }.getOrNull() else null
        val all = s?.let { runCatching { core.sentences(it.book, it.section) }.getOrNull() }
        val raw = s?.let { all?.getOrNull(it.sentence.toInt()) }
        if (s == null || raw == null) {
            addBookmark()
            toast = Library.Toast(Library.Toast.Icon.Check, t("记了一个书签"), "${title(chapter)} ${clock(inChapter.first)}")
            return
        }
        val text = raw.trim()
        if (text.isEmpty()) return
        val from = raw.indexOf(text)
        val point = { i: Int -> uniffi.yuyue_core.TextPoint(s.sentence, raw.substring(0, i).length.toUInt()) }
        val known = runCatching { core.marks(s.book) }.getOrDefault(emptyList()).map { it.id }.toSet()
        val style = uniffi.yuyue_core.MarkStyle.entries.firstOrNull { it.name == Prefs.string("reader.markStyle") } ?: uniffi.yuyue_core.MarkStyle.YELLOW
        val mark = runCatching { core.markHeard(s.book, s.section, point(from), point(from + text.length), text, style) }.getOrNull() ?: return
        val new = mark.id !in known
        toast = Library.Toast(
            Library.Toast.Icon.Highlight, if (new) t("划了一句") else t("这句已经划过了"), "“${text.take(24)}”",
            undo = if (new) ({ runCatching { core.deleteMark(s.book, mark.id) } }) else null,
        )
    }

    // MARK: Where it is

    val total: Double get() = recording?.seconds ?: 0.0
    val chapters: List<AudioChapter> get() = recording?.chapters ?: emptyList()
    val chapter: Int get() = chapter(time)

    fun chapter(at: Double): Int = maxOf(0, chapters.indexOfLast { it.start <= at + 0.05 })

    /** `第 3 集`, or the recording's own name for it. */
    fun title(i: Int): String = bookTitles[i] ?: chapters.getOrNull(i)?.let { it.title ?: t("第 %lld 集", i + 1) } ?: summary.title

    /** Seconds into the chapter being heard, and how long it is. */
    val inChapter: Pair<Double, Double>
        get() = chapters.getOrNull(chapter)?.let { (time - it.start) to it.seconds } ?: (0.0 to 0.0)

    private fun seconds(p: Position): Double {
        val c = chapters.getOrNull(p.section.toInt()) ?: return 0.0
        return minOf(total, c.start + p.sentence.toDouble() / 1000)
    }

    private fun position(at: Double): Position {
        val i = chapter(at)
        val into = chapters.getOrNull(i)?.let { at - it.start } ?: 0.0
        return Position(i.toUInt(), (maxOf(0.0, into) * 1000).roundToLong().toUInt())
    }

    fun seconds(b: Bookmark) = seconds(Position(b.section, b.sentence))

    // MARK: Controls

    fun toggle() = if (isPlaying) pause() else play()

    fun play() {
        if (recording == null || isPlaying || closed) return
        waiting = false
        // 听写、精修 near here first, while it's being listened to.
        library.recordings.workOn(bookID, time)
        // Picked up after a while: back to the sentence's start, the one before or the
        // paragraph's, by how long it's been; a few seconds when it isn't laid out.
        if (pausedAt > 0) {
            val gap = (System.currentTimeMillis() - pausedAt) / 1000.0
            // Without a map the core goes back to a pause in the speech (≥ 0.4 s) instead.
            val floor = if (hasMap) 0.0 else chapters.getOrNull(chapter)?.start ?: 0.0
            runCatching { core.audiobookResumeAt(bookID, time, gap) }.getOrNull()?.takeIf { it < time - 0.05 }?.let { seekTo(maxOf(floor, it)) }
        }
        // Listened on since the timer stopped it: nothing to ask next time.
        Prefs.set(sleepKey, "")
        sleepReturn = null
        if (time >= total - 0.5) seekTo(0.0)
        pausedAt = 0
        Session.claimAudio(this)
        exo.play()
        isPlaying = true
        library.listening = bookID
        listened()
        main.removeCallbacks(tick)
        main.post(tick)
        armSleep()
    }

    fun pause() {
        if (waiting) {
            // Paused during a call: it stays paused after it.
            waiting = false
            exo.pause()
            pausedAt = System.currentTimeMillis()
            return
        }
        if (!isPlaying) return
        // Paused while the voice reads what the recording leaves out: back to just before them,
        // so playing on reads them again rather than passing them by.
        val rewind = fillingRun?.let { maxOf(0.0, it.at - 0.3) }
        stopFilling()
        exo.pause()
        stopped()
        rewind?.let { time = it; seekExo(it); save() }
    }

    private fun stopped() {
        readTime()
        isPlaying = false
        pausedAt = System.currentTimeMillis()
        listened()
        save(now = true)
        if (library.listening == bookID) library.listening = null
        main.removeCallbacks(tick)
        // The timer waits while paused: no fading, no stopping, no key taken for 续 10 分钟.
        main.removeCallbacks(sleepAt)
        main.removeCallbacks(fade)
        if (fading) {
            fading = false
            shake?.stop()
            shake = null
            exo.volume = if (gain in 0.01f..0.999f) gain else 1f
        }
    }

    fun skip(by: Double) = seekTo(time + by)

    fun seekTo(t: Double) {
        // Elsewhere while the voice fills in: it stops, or the two would talk at once.
        val wasFilling = tts != null
        stopFilling()
        time = t.coerceIn(0.0, maxOf(0.0, total - 0.1))
        pausedAt = 0
        seekExo(time)
        if (wasFilling && isPlaying) exo.play()
        save(now = !isPlaying)
    }

    fun goToChapter(i: Int) {
        val c = chapters.getOrNull(i) ?: return
        seekTo(c.start)
        if (!isPlaying) play()
    }

    fun changeRate(r: Float) {
        rate = r
        Prefs.set(RATE_KEY, r)
        exo.playbackParameters = PlaybackParameters(r, 1f)
    }

    fun changeSleep(s: Player.Sleep) {
        if (sleep == Player.Sleep.Off && s != Player.Sleep.Off) sleepFrom = time
        sleep = s
        armSleep()
    }

    /** 念完这句: stops where the sentence being heard ends. */
    fun sleepAtSentenceEnd() {
        val b = runCatching { core.audiobookSentenceBounds(bookID, time) }.getOrNull() ?: return
        sentenceEnd = b.t1
        changeSleep(Player.Sleep.Sentence)
    }

    /** 续 10 分钟. */
    fun extendSleep() {
        val s = sleep as? Player.Sleep.Until ?: return
        if (!fading) return
        changeSleep(Player.Sleep.Until(System.currentTimeMillis() + 600_000, s.minutes))
    }

    private val sleepKey get() = "sleepFrom.$bookID"

    private fun offerSleepReturn() {
        val v = Prefs.string(sleepKey).split(",").mapNotNull { it.toDoubleOrNull() }
        if (v.size == 2 && System.currentTimeMillis() - v[1] < 7 * 86_400_000.0) sleepReturn = v[0] to v[1].toLong()
    }

    fun answerSleepReturn(go: Boolean) {
        sleepReturn?.takeIf { go }?.let { seekTo(it.first) }
        sleepReturn = null
        Prefs.set(sleepKey, "")
    }

    /** The timer stopped it: where it was set is offered next time (回到昨晚). */
    private fun fallAsleep() {
        val from = sleepFrom ?: time
        pause()
        changeSleep(Player.Sleep.Off)
        Prefs.set(sleepKey, "$from,${System.currentTimeMillis()}")
    }

    fun close() {
        if (closed) return
        if (isPlaying) {
            exo.pause()
            stopped()
        } else {
            save(now = true)
        }
        stopFilling()
        shake?.stop()
        runCatching { dynamics?.release(); eq?.release(); boost?.release() }
        closed = true
        main.removeCallbacksAndMessages(null)
        Session.releaseAudio(this)
        exo.removeListener(listener)
        exo.release()
    }

    /** 长按改名: a chapter's own name, or empty to have it called 第 n 集. */
    fun renameChapter(i: Int, name: String) {
        runCatching {
            summary = core.renameAudioChapter(bookID, i.toUInt(), name)
            recording = core.audioRecording(bookID)
        }.onFailure { failure = Library.message(it) }
    }

    // MARK: 书签

    fun addBookmark() {
        val p = position(time)
        runCatching { core.addBookmark(bookID, p.section, p.sentence) }
            .onFailure { failure = Library.message(it) }
        bookmarks = runCatching { core.bookmarks(bookID) }.getOrDefault(bookmarks)
    }

    fun deleteBookmark(b: Bookmark) {
        runCatching { core.deleteBookmark(bookID, b.id) }
        bookmarks = bookmarks.filter { it.id != b.id }
    }

    // MARK: The files

    private fun seekExo(t: Double) {
        if (starts.isEmpty()) return
        val i = maxOf(0, starts.indexOfLast { it <= t })
        exo.seekTo(i, ((t - starts[i]) * 1000).roundToLong())
    }

    private fun readTime() {
        val i = exo.currentMediaItemIndex
        if (i !in starts.indices) return
        time = minOf(total, starts[i] + exo.currentPosition / 1000.0)
    }

    private fun onTick() {
        if (!isPlaying || tts != null) return
        val before = chapter
        val was = time
        readTime()
        if (follow(was)) return
        if (sleep == Player.Sleep.Chapter && chapter != before) {
            // 本集完: stop at the start of the next one.
            sleep = Player.Sleep.Off
            pause()
            seekTo(chapters[chapter].start)
            return
        }
        val now = System.currentTimeMillis()
        if (now - savedAt > 10_000) save()
        if (listenSince > 0 && now - listenSince > 60_000) listened()
    }

    private fun finished() {
        if (!isPlaying) return
        time = total
        isPlaying = false
        pausedAt = 0
        listened()
        save(now = true)
        if (library.listening == bookID) library.listening = null
        sleep = Player.Sleep.Off
        main.removeCallbacks(tick)
    }

    // MARK: Progress and time listened

    private fun save(now: Boolean = false) {
        if (recording == null) return
        savedAt = System.currentTimeMillis()
        if (unsavedSaving > 0) {
            runCatching { core.audiobookAddSaved(bookID, unsavedSaving) }
            unsavedSaving = 0.0
        }
        runCatching { core.saveProgress(bookID, position(time)) }.onFailure { failure = Library.message(it) }
        if (now) library.reload()
    }

    private fun listened() {
        val now = System.currentTimeMillis()
        if (listenSince > 0) {
            val seconds = (now - listenSince) / 1000
            if (seconds in 1..599) runCatching { core.addTime(bookID, chapter.toUInt(), 0u, seconds.toUInt(), LocalDate.now().toString()) }
        }
        listenSince = if (isPlaying) now else 0
    }

    // MARK: 定时

    private val sleepAt = Runnable { fallAsleep() }

    /** The last 20 seconds of a timer fade out; a key or a shake in them gives 10 minutes more. */
    private val fade = object : Runnable {
        override fun run() {
            val s = sleep as? Player.Sleep.Until ?: return
            val left = s.at - System.currentTimeMillis()
            if (!fading) {
                fading = true
                shake = ShakeWatch(context) { extendSleep() }.also { it.start() }
            }
            exo.volume = (left.toFloat() / Player.FADE_MS).coerceIn(0f, 1f) * (if (gain in 0.01f..0.999f) gain else 1f)
            if (left > 0) main.postDelayed(this, 200)
        }
    }

    private fun armSleep() {
        main.removeCallbacks(sleepAt)
        main.removeCallbacks(fade)
        fading = false
        shake?.stop()
        shake = null
        exo.volume = if (gain in 0.01f..0.999f) gain else 1f
        val s = sleep as? Player.Sleep.Until ?: return
        val left = s.at - System.currentTimeMillis()
        // Its time passed while paused: over, rather than stopping as soon as it plays.
        if (left <= 0) {
            sleep = Player.Sleep.Off
            return
        }
        if (!isPlaying) return
        main.postDelayed(sleepAt, maxOf(0, left))
        main.postDelayed(fade, maxOf(0, left - Player.FADE_MS))
    }

    // MARK: The notification

    /** Each file as the notification shows it: the book, the chapter it starts, the cover. */
    private fun meta(file: Int): MediaMetadata {
        val start = starts.getOrNull(file) ?: 0.0
        return MediaMetadata.Builder()
            .setTitle(t("%@ · %@", summary.title, title(chapter(start))))
            .setArtist(summary.authors.firstOrNull())
            .setAlbumTitle(summary.title)
            .setMediaType(MediaMetadata.MEDIA_TYPE_AUDIO_BOOK_CHAPTER)
            .apply { summary.coverPath?.let { runCatching { File(it).readBytes() }.getOrNull() }?.let { setArtworkData(it, MediaMetadata.PICTURE_TYPE_FRONT_COVER) } }
            .build()
    }

    fun startService() {
        context.startService(Intent(context, PlaybackService::class.java))
    }

    companion object {
        const val BACK = 15.0
        const val AHEAD = 30.0
        private fun gainKey(id: String) = "audiobook.gain.$id"
        private const val RATE_KEY = "audiobook.rate"

        /** `14:22`, `1:02:05`. */
        fun clock(seconds: Double): String {
            val s = maxOf(0.0, seconds).toLong()
            return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
        }
    }

    /** 导出带朗读时间的电子书: the e-book with the recording and each sentence's time, one EPUB. */
    suspend fun exportEpub(): java.io.File? = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
        val dir = java.io.File(context.cacheDir, "export").apply { mkdirs() }
        val file = java.io.File(dir, summary.title.replace('/', '-') + ".epub")
        runCatching { core.audiobookExportEpub(bookID, file.path); file }.getOrNull()
    }

    fun share(file: java.io.File) {
        val uri = androidx.core.content.FileProvider.getUriForFile(context, context.packageName + ".files", file)
        context.startActivity(android.content.Intent.createChooser(android.content.Intent(android.content.Intent.ACTION_SEND).setType("application/epub+zip").putExtra(android.content.Intent.EXTRA_STREAM, uri).addFlags(android.content.Intent.FLAG_GRANT_READ_URI_PERMISSION), null).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
