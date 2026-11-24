package app.yuyue.speech

import android.content.Context
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.Alignment
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.SavedEmissions
import uniffi.yuyue_core.ListenSentence
import uniffi.yuyue_core.Listening
import uniffi.yuyue_core.ExtraPlace
import uniffi.yuyue_core.ExtraSpan
import uniffi.yuyue_core.SkipSettings
import uniffi.yuyue_core.SyncBy
import uniffi.yuyue_core.SyncMap
import java.io.File
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 听读书 on Android, as on Apple: an 有声书 on the shelf laid against its e-book
 * (强制对齐), so the e-book can be listened to in the narrator's voice with the ink line
 * following along. What's kept, in `audiobooks/<e-book>/`: which 有声书 it is, the model's output
 * for each of its files (so a stopped job picks up where it was), and `timings.json`, where each
 * sentence is in the recording.
 */
class Recordings(private val context: Context, private val library: Library) {
    private val dir = File(library.dataDir, "audiobooks").apply { mkdirs() }

    /** 转成文字 under way, by recording. */
    val textJobs = androidx.compose.runtime.mutableStateMapOf<String, TextJob>()

    class TextJob(val files: Int, val total: Double) {
        var file by mutableStateOf(0)
        var heard by mutableStateOf(0.0)
        /** 0–1 while the transcriber is fetched. */
        var downloaded by mutableStateOf<Double?>(null)
        var problem by mutableStateOf<String?>(null)
        val started = System.currentTimeMillis()
        val fraction: Double get() = if (total > 0) (heard / total).coerceIn(0.0, 1.0) else 0.0
        /** Seconds still to go, once it's clear. */
        val left: Double? get() {
            val spent = (System.currentTimeMillis() - started) / 1000.0
            return if (fraction > 0.03 && spent > 5) spent / fraction * (1 - fraction) else null
        }
    }

    /** 没有电子书: the whole recording heard on this phone (SenseVoice), made into a book of its
     *  own, and laid against it like any e-book. */
    fun makeTextVersion(audio: BookSummary) {
        if (textJobs[audio.id]?.problem == null && textJobs.containsKey(audio.id)) return
        val rec = runCatching { library.core.audioRecording(audio.id) }.getOrElse { return }
        val job = TextJob(rec.files.size, rec.seconds)
        textJobs[audio.id] = job
        val lang = if (audio.language.startsWith("en")) "en" else "zh"
        library.scope.launch {
            try {
                if (!Listener.installed(library.dataDir)) {
                    job.downloaded = 0.0
                    Listener.ensure(library.dataDir) { p -> library.scope.launch { job.downloaded = p } }
                    job.downloaded = null
                }
                val words = mutableListOf<uniffi.yuyue_core.HeardWord>()
                var offset = 0.0
                for ((i, f) in rec.files.withIndex()) {
                    job.file = i
                    val base = offset
                    val got = Listener.file(library.dataDir, File(f.path), lang, offset) { s -> library.scope.launch { job.heard = base + s } }
                    if (got == null) {
                        job.problem = app.yuyue.t("转写没做完，再点一次接着转")
                        return@launch
                    }
                    words += got
                    offset += f.seconds
                    job.heard = offset
                }
                val made = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                    runCatching { library.core.audiobookMakeTextVersion(audio.id, words, audio.title) }.getOrNull()
                }
                textJobs.remove(audio.id)
                if (made == null) return@launch
                library.reload()
                make(audio, made)
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                job.downloaded = null
                job.problem = app.yuyue.t("转写没做完，再点一次接着转")
            }
        }
    }

    /** The one being made, while it is. */
    var making by mutableStateOf<Making?>(null)
        private set

    class Making(val ebook: String, val audio: String, val total: Double) {
        /** 0–1 while the model is fetched; then seconds of the recording run so far. */
        var downloaded by mutableStateOf<Double?>(null)
        var heard by mutableStateOf(0.0)
        var problem by mutableStateOf<String?>(null)
        var done by mutableStateOf(false)
        internal var job: Job? = null
        val fraction: Double get() = if (total > 0) minOf(1.0, heard / total) else 0.0
    }

    private fun folder(ebook: String) = File(dir, ebook)

    private val cache = mutableMapOf<String, Map<Pair<Int, Int>, Timing>>()
    /** 听写、精修 in the background, toward each recording's map. */
    val work by lazy { AudiobookWork(context, library) }

    init {
        adopt()
        // Recordings 合成 here before: their new lay-out goes on a little after opening.
        library.scope.launch {
            kotlinx.coroutines.delay(20_000)
            for (ebook in dir.list().orEmpty()) audioFor(ebook)?.let { workOn(it, null) }
        }
    }
    private val initCompleted = true

    /** The 有声书 laid against `ebook`, when there is one. */
    fun audioFor(ebook: String): String? = File(folder(ebook), "audio").takeIf { it.isFile }?.readText()?.trim()

    /** The e-book on the shelf that is the same book as this 有声书, not yet laid against it —
     *  or the one it was being laid against when that stopped (failed, cancelled, the app
     *  closed), so 合成 can go on. */
    fun ebookFor(audio: BookSummary): BookSummary? {
        if (audio.format != uniffi.yuyue_core.BookFormat.AUDIO) return null
        library.books.firstOrNull { audioFor(it.id) == audio.id }?.let { paired ->
            val made = runCatching { library.core.listening(audio.id)?.madeMs }.getOrNull() != null
            return if (made || making?.audio == audio.id || !paired.available) null else paired
        }
        val want = plain(audio.title)
        if (want.length < 2) return null
        return library.books.filter { it.available && it.format != uniffi.yuyue_core.BookFormat.AUDIO && it.format != uniffi.yuyue_core.BookFormat.PDF }
            .firstOrNull { val p = plain(it.title); p == want || p.length >= 2 && (p.contains(want) || want.contains(p)) }
    }

    /** 合成: lays the 有声书 against the e-book on this device. */
    fun make(audio: BookSummary, ebook: BookSummary) {
        making?.job?.cancel()
        val recording = runCatching { library.core.audioRecording(audio.id) }.getOrElse { return }
        val m = Making(ebook.id, audio.id, recording.seconds)
        making = m
        val f = folder(ebook.id).apply { mkdirs() }
        File(f, "audio").writeText(audio.id)
        val started = System.currentTimeMillis()
        m.job = library.scope.launch {
            try {
                if (!Aligner.installed(library.dataDir)) {
                    m.downloaded = 0.0
                    Aligner.ensure(library.dataDir) { p -> library.scope.launch { m.downloaded = p } }
                    m.downloaded = null
                }
                val files = mutableListOf<SavedEmissions>()
                var offset = 0.0
                for ((i, file) in recording.files.withIndex()) {
                    val cache = File(f, "emissions-$i.f32")
                    if (!(cache.isFile && cache.length() > 0 && cache.length() % (4 * Aligner.LETTERS) == 0L)) {
                        val base = offset
                        val (values, _, _) = Aligner.emissions(library.dataDir, File(file.path)) { s -> library.scope.launch { m.heard = base + s } }
                        saveEmissions(cache, values)
                    }
                    // The core reads the model's output from the file itself.
                    val em = SavedEmissions(cache.path, file.seconds)
                    files += em
                    offset += file.seconds
                    m.heard = offset
                    save(ebook.id, audio.id, library.core.alignRecordingSaved(ebook.id, files), offset, null)
                }
                // Finished: kept as made, so other devices know it's whole.
                library.core.listening(audio.id)?.let { library.core.saveListening(it.copy(madeMs = System.currentTimeMillis(), took = (System.currentTimeMillis() - started) / 1000.0)) }
                m.done = true
                making = null
                // The new lay-out (听写 by the model's letters → 粗对齐 → 精修), from what was just saved.
                work.start(audio.id, ebook.id, null)
                library.note(Library.Toast.Icon.Check, t("《%@》的有声书合成好了", ebook.title), t("打开书，点朗读就是主播的声音"))
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                m.problem = if (e.message == "ENOSPC") t("存储空间不够：对齐用的模型要约 800 MB 空间才装得下")
                    else if (e is java.io.IOException) t("对齐用的模型没下载下来，连上网再试一次")
                    else t("合成没做完（%@）", e.message ?: e.toString())
            }
        }
    }

    fun cancel() {
        making?.job?.cancel()
        making = null
    }

    /** Toward the map of 有声书 `audio`, nearest `at` seconds first, once it's been 合成 here. */
    fun workOn(audio: String, at: Double?) {
        val ebook = library.books.firstOrNull { audioFor(it.id) == audio }?.id ?: return
        if (making?.audio == audio || !File(folder(ebook), "emissions-0.f32").isFile) return
        work.start(audio, ebook, at)
    }

    /** Where `ebook`'s recording keeps its work. */
    fun folderOf(ebook: String) = folder(ebook).apply { mkdirs() }

    /** The model's output for one file, written a piece at a time: hours of it are a lot of
     *  memory twice over. */
    fun saveEmissions(to: File, values: FloatArray) {
        val part = File(to.parentFile, to.name + ".part")
        java.io.FileOutputStream(part).channel.use { ch ->
            val piece = ByteBuffer.allocateDirect(1 shl 20).order(ByteOrder.LITTLE_ENDIAN)
            var k = 0
            while (k < values.size) {
                piece.clear()
                val n = minOf(values.size - k, piece.capacity() / 4)
                piece.asFloatBuffer().put(values, k, n)
                piece.limit(n * 4)
                while (piece.hasRemaining()) ch.write(piece)
                k += n
            }
        }
        part.renameTo(to)
    }

    /** The new lay-out was made: its times go into the timings everything else reads (朗读 in
     *  the narrator's voice, the sync log), and the pages showing it look again. */
    fun mapChanged(ebook: String) {
        adoptMap(ebook)
        audioFor(ebook)?.let { changed.tryEmit(it) }
    }

    /** A sentence the new lay-out is sure of (精确, or pinned) gets its times; one it only
     *  spreads, or says isn't read, keeps what the alignment model alone gave, when it gave
     *  anything; one moved by hand stays where it was put. */
    private fun adoptMap(ebook: String) {
        val audio = audioFor(ebook) ?: return
        if (making?.audio == audio) return
        val map = runCatching { library.core.audiobookMap(audio) }.getOrNull() ?: return
        if (map.rows.isEmpty()) return
        val old = timings(ebook).orEmpty()
        val listening = runCatching { library.core.listening(audio) }.getOrNull()
        val byHand = listening?.sentences.orEmpty().filter { it.adjusted }.associateBy { it.section.toInt() to it.sentence.toInt() }
        val rows = map.rows.map { r ->
            val key = r.section.toInt() to r.sentence.toInt()
            val hand = byHand[key]
            val o = old[key]
            val sure = r.by == SyncBy.AUTO || r.by == SyncBy.USER
            when {
                hand != null -> Triple(key, hand.start, hand.end)
                !sure && o != null -> Triple(key, o.start, o.end)
                r.by == SyncBy.UNREAD -> Triple(key, -1.0, -1.0)
                else -> Triple(key, r.t0Ms.toDouble() / 1000, r.t1Ms.toDouble() / 1000)
            }
        }.toMutableList()
        // A sentence ends by the time the next one starts.
        var next: Double? = null
        for (i in rows.indices.reversed()) {
            val (k, a, b) = rows[i]
            if (a < 0) continue
            val n = next
            if (n != null && b > n && n > a) rows[i] = Triple(k, a, n)
            next = a
        }
        val same = old.size == rows.count { it.second >= 0 } && rows.all { (k, a, b) -> a < 0 || old[k]?.let { kotlin.math.abs(it.start - a) < 0.002 && kotlin.math.abs(it.end - b) < 0.002 } == true }
        if (same) return
        write(ebook, rows)
        val conf = map.rows.associate { (it.section.toInt() to it.sentence.toInt()) to it.conf.toFloat() / 255f }
        listening?.let { l ->
            runCatching {
                library.core.saveListening(l.copy(sentences = rows.map { (k, a, b) -> ListenSentence(k.first.toUInt(), k.second.toUInt(), a, b, byHand[k]?.heard ?: conf[k] ?: 1f, byHand.containsKey(k)) }))
            }
        }
    }

    // MARK: Where each sentence is

    class Timing(val section: Int, val sentence: Int, val start: Double, val end: Double)

    private fun save(ebook: String, audio: String, a: Alignment, through: Double, madeMs: Long?) {
        write(ebook, a.sentences.map { Triple(it.section.toInt() to it.sentence.toInt(), it.start, it.end) })
        // Into the sync log, so other devices use it without running the model.
        runCatching {
            library.core.saveListening(
                Listening(
                    recording = audio, book = ebook,
                    sentences = a.sentences.map { ListenSentence(it.section, it.sentence, it.start, it.end, it.heard, false) },
                    sections = a.sections, intro = a.intro, through = through, introSkipped = false, madeMs = madeMs, took = null,
                ),
            )
        }
    }

    private fun write(ebook: String, sentences: List<Triple<Pair<Int, Int>, Double, Double>>) {
        val arr = JSONArray()
        for ((at, start, end) in sentences) {
            if (start < 0) continue
            arr.put(JSONArray().put(at.first).put(at.second).put(start).put(end))
        }
        val tmp = File(folder(ebook).apply { mkdirs() }, "timings.json.part")
        tmp.writeText(JSONObject().put("sentences", arr).toString())
        tmp.renameTo(File(folder(ebook), "timings.json"))
        cache.remove(ebook)
    }

    /**
     * 同步: what another device laid out comes in through the sync log. For
     * every recording on this shelf (each device brings in its own copy; the id says it's the same
     * one), its e-book and where each sentence is are taken in; what was made here before the log
     * kept it goes out. Called when the app starts and after each sync.
     */
    fun adopt() {
        val pairs = runCatching { library.core.listeningPairs() }.getOrNull() ?: return
        val onShelf = library.books.filter { it.available }.map { it.id }.toSet()
        for (pair in pairs) {
            if (making?.audio == pair.recording) continue
            if (pair.recording !in onShelf || pair.book !in onShelf) continue
            val l = runCatching { library.core.listening(pair.recording) }.getOrNull() ?: continue
            // Another recording laid against this e-book here gives way to the one in the log.
            val f = folder(pair.book).apply { mkdirs() }
            val audio = File(f, "audio")
            if (audio.takeIf { it.isFile }?.readText()?.trim() != pair.recording) audio.writeText(pair.recording)
            val want = l.sentences.map { Triple(it.section.toInt() to it.sentence.toInt(), it.start, it.end) }
            val have = timings(pair.book)
            val same = have != null && have.size == want.count { it.second >= 0 } &&
                want.all { (at, start, end) -> start < 0 || have[at]?.let { kotlin.math.abs(it.start - start) < 0.002 && kotlin.math.abs(it.end - end) < 0.002 } == true }
            if (!same) write(pair.book, want)
        }
        // Made here before the log kept them.
        val known = pairs.map { it.recording }.toSet()
        for (ebook in dir.list().orEmpty()) {
            val audio = audioFor(ebook) ?: continue
            if (audio in known || making?.ebook == ebook) continue
            val times = timings(ebook) ?: continue
            runCatching {
                library.core.saveListening(
                    Listening(
                        recording = audio, book = ebook,
                        sentences = times.values.sortedWith(compareBy({ it.section }, { it.sentence })).map { ListenSentence(it.section.toUInt(), it.sentence.toUInt(), it.start, it.end, 1f, false) },
                        sections = emptyList(), intro = 0.0, through = 0.0, introSkipped = false, madeMs = System.currentTimeMillis(), took = null,
                    ),
                )
            }
        }
    }

    /** Where each sentence of `ebook` is in its recording, when it's been laid out. */
    fun timings(ebook: String): Map<Pair<Int, Int>, Timing>? = cache[ebook] ?: run {
        val f = File(folder(ebook), "timings.json").takeIf { it.isFile } ?: return null
        val arr = runCatching { JSONObject(f.readText()).getJSONArray("sentences") }.getOrNull() ?: return null
        val map = HashMap<Pair<Int, Int>, Timing>(arr.length())
        for (i in 0 until arr.length()) {
            val r = arr.getJSONArray(i)
            map[r.getInt(0) to r.getInt(1)] = Timing(r.getInt(0), r.getInt(1), r.getDouble(2), r.getDouble(3))
        }
        cache[ebook] = map
        map
    }

    /** The narrator's voice for `ebook`, when it has a recording laid against it. */
    fun voice(ebook: String): NarratorVoice? {
        val audio = audioFor(ebook) ?: return null
        val times = timings(ebook) ?: return null
        val rec = runCatching { library.core.audioRecording(audio) }.getOrNull() ?: return null
        return NarratorVoice(rec.files.map { File(it.path) to it.seconds }, times)
    }

    /** Moves `(section, sentence)`'s start to `toStart` (and subsequent sentences in `section`
     *  when `follow` is true), updates `timings.json` + the sync log, and pins it in the core. */
    fun moveStart(ebook: String, section: Int, sentence: Int, toStart: Double, follow: Boolean) {
        val audio = audioFor(ebook) ?: return
        val old = timings(ebook) ?: return
        val cur = old[section to sentence] ?: return
        val target = (maxOf(0.0, toStart) * 10.0).let { kotlin.math.round(it) / 10.0 }
        val delta = target - cur.start
        if (kotlin.math.abs(delta) < 0.005) return
        val touched = HashSet<Pair<Int, Int>>()
        val ordered = old.values.sortedWith(compareBy({ it.section }, { it.sentence })).map { t ->
            val move = t.section == section && (t.sentence == sentence || (follow && t.sentence > sentence))
            if (move) {
                touched += t.section to t.sentence
                Triple(t.section to t.sentence, maxOf(0.0, t.start + delta), maxOf(0.05, t.end + if (follow) delta else 0.0))
            } else {
                Triple(t.section to t.sentence, t.start, t.end)
            }
        }.toMutableList()
        var next: Double? = null
        for (i in ordered.indices.reversed()) {
            val (k, a, b) = ordered[i]
            val n = next
            if (n != null && n > a) ordered[i] = Triple(k, a, minOf(b, n))
            next = a
        }
        write(ebook, ordered)
        runCatching { library.core.audiobookPin(audio, section.toUInt(), sentence.toUInt(), target) }
        runCatching { library.core.listening(audio) }.getOrNull()?.let { l ->
            val byKey = ordered.associate { it.first to (it.second to it.third) }
            val updated = l.sentences.map { ls ->
                val k = ls.section.toInt() to ls.sentence.toInt()
                val nw = byKey[k]
                if (nw != null) ls.copy(start = nw.first, end = nw.second, adjusted = ls.adjusted || k in touched) else ls
            }
            runCatching { library.core.saveListening(l.copy(sentences = updated)) }
        }
        changed.tryEmit(audio)
    }

    // MARK: 这本录音

    /** What the map says about `ebook` for reading along with its recording. */
    fun guide(ebook: String): RecordingGuide? {
        val audio = audioFor(ebook) ?: return null
        val map = runCatching { library.core.audiobookMap(audio) }.getOrNull()
        return RecordingGuide(audio, map, settings(audio), timings(ebook).orEmpty())
    }

    /** A recording's own settings (片头片尾, 章间音乐, 人声, 停顿, 朗读补上), synced. */
    fun settings(audio: String): SkipSettings =
        runCatching { library.core.audiobookSkipSettings(audio) }.getOrNull() ?: SkipSettings(true, false, true, false, false, false, false)

    fun set(s: SkipSettings, audio: String) {
        runCatching { library.core.audiobookSetSkipSettings(audio, s) }
        changed.tryEmit(audio)
    }

    companion object {
        /** A recording's settings or lay-out changed (its id). */
        val changed = kotlinx.coroutines.flow.MutableSharedFlow<String>(extraBufferCapacity = 8)

        fun plain(s: String): String {
            var t = s.lowercase()
            for (p in listOf(Regex("[（(【\\[《].*?[)）】\\]》]"), Regex("有声(书|小说|版)?"), Regex("(全集|完整版|精品|播讲|朗读|演播)"), Regex("^\\d+[\\s._-]*"))) t = t.replace(p, "")
            return t.filter { it.isLetterOrDigit() }
        }
    }
}

/** A sentence in the narrator's voice: its stretch of the recording, decoded to 16-bit mono. */
class NarratorVoice(private val files: List<Pair<File, Double>>, private val times: Map<Pair<Int, Int>, Recordings.Timing>) {
    private val starts = files.runningFold(0.0) { at, f -> at + f.second }.dropLast(1)

    fun has(section: Int, sentence: Int) = times.containsKey(section to sentence)

    /** Where the sentence starts in the whole recording. */
    fun startOf(section: Int, sentence: Int): Double? = times[section to sentence]?.start

    /** Any stretch of the recording, in its whole-recording seconds (片头, talk not in the book). */
    fun range(from: Double, to: Double): Speech? {
        if (to <= from) return null
        val i = starts.indexOfLast { it <= from }.coerceAtLeast(0)
        return clip(files[i].first, from - starts[i], to - starts[i])
    }

    /** The sentence's stretch, or null when the recording doesn't have it. */
    /** The sentence in the narrator's voice; from `from` seconds when that's inside it (a word
     *  found by 搜索). */
    fun speech(section: Int, sentence: Int, from: Double? = null): Speech? {
        val t = times[section to sentence] ?: return null
        if (t.end <= t.start) return null
        val start = from?.coerceIn(t.start, t.end - 0.05) ?: t.start
        val i = starts.indexOfLast { it <= start }.coerceAtLeast(0)
        return clip(files[i].first, start - starts[i], t.end - starts[i])
    }

    private fun clip(file: File, from: Double, to: Double): Speech? {
        val ex = MediaExtractor()
        try {
            ex.setDataSource(file.path)
            val track = (0 until ex.trackCount).firstOrNull { ex.getTrackFormat(it).getString(MediaFormat.KEY_MIME)?.startsWith("audio/") == true } ?: return null
            ex.selectTrack(track)
            val format = ex.getTrackFormat(track)
            ex.seekTo((from * 1_000_000).toLong(), MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
            val codec = MediaCodec.createDecoderByType(format.getString(MediaFormat.KEY_MIME)!!)
            codec.configure(format, null, null, 0)
            codec.start()
            var rate = format.getInteger(MediaFormat.KEY_SAMPLE_RATE)
            var channels = format.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
            val out = java.io.ByteArrayOutputStream()
            val info = MediaCodec.BufferInfo()
            var inputDone = false
            var done = false
            try {
                while (!done) {
                    if (!inputDone) {
                        val i = codec.dequeueInputBuffer(10_000)
                        if (i >= 0) {
                            val n = ex.readSampleData(codec.getInputBuffer(i)!!, 0)
                            if (n < 0 || ex.sampleTime / 1e6 > to + 0.5) {
                                codec.queueInputBuffer(i, 0, 0, 0, MediaCodec.BUFFER_FLAG_END_OF_STREAM)
                                inputDone = true
                            } else {
                                codec.queueInputBuffer(i, 0, n, ex.sampleTime, 0)
                                ex.advance()
                            }
                        }
                    }
                    val o = codec.dequeueOutputBuffer(info, 10_000)
                    if (o == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                        rate = codec.outputFormat.getInteger(MediaFormat.KEY_SAMPLE_RATE)
                        channels = codec.outputFormat.getInteger(MediaFormat.KEY_CHANNEL_COUNT)
                    } else if (o >= 0) {
                        val at = info.presentationTimeUs / 1e6
                        val buf = codec.getOutputBuffer(o)!!.order(ByteOrder.nativeOrder()).asShortBuffer()
                        val frames = buf.remaining() / channels
                        for (k in 0 until frames) {
                            val time = at + k.toDouble() / rate
                            if (time < from) continue
                            if (time >= to) { done = true; break }
                            var sum = 0
                            for (c in 0 until channels) sum += buf.get(k * channels + c)
                            val v = (sum / channels).toShort()
                            out.write(v.toInt() and 0xff)
                            out.write(v.toInt() shr 8 and 0xff)
                        }
                        codec.releaseOutputBuffer(o, false)
                        if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) done = true
                    }
                }
            } finally {
                codec.stop()
                codec.release()
            }
            val bytes = out.toByteArray()
            if (bytes.isEmpty()) return null
            val samples = ShortArray(bytes.size / 2)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asShortBuffer().get(samples)
            return Speech(samples, rate)
        } catch (_: Exception) {
            return null
        } finally {
            ex.release()
        }
    }
}

/**
 * What the map says about the book, for reading along with the recording: which sentences it
 * doesn't read, which chapters are only followed as a whole, and what it has that the book doesn't
 * (片头, talk in the middle), as on Apple.
 */
class RecordingGuide(val audio: String, map: SyncMap?, val settings: SkipSettings, times: Map<Pair<Int, Int>, Recordings.Timing>) {
    private val grades = HashMap<Int, HashMap<Int, SyncBy>>()
    val extras: List<ExtraSpan> = map?.extras.orEmpty()
    private val ends = times.mapValues { it.value.end }
    private val first: Pair<Int, Int>? = times.values.minByOrNull { it.start }?.let { it.section to it.sentence }

    init {
        for (r in map?.rows.orEmpty()) grades.getOrPut(r.section.toInt()) { HashMap() }[r.sentence.toInt()] = r.by
    }

    val sections: Set<Int> get() = grades.keys

    fun grade(section: Int, sentence: Int): SyncBy? = grades[section]?.get(sentence)

    /** Not in the recording: passed, unless 用朗读补上 is on. */
    fun skips(section: Int, sentence: Int) = grade(section, sentence) == SyncBy.UNREAD && !settings.fillUnread

    fun chapterOnly(section: Int): Boolean {
        val g = grades[section]?.values ?: return false
        return SyncBy.CHAPTER in g && SyncBy.AUTO !in g
    }

    fun unread(section: Int): List<Int> = grades[section].orEmpty().filterValues { it == SyncBy.UNREAD }.keys.sorted()

    /** A sentence in a stretch not sure sentence by sentence starts from the stretch's first. */
    fun stretchStart(section: Int, sentence: Int): Int {
        if (grade(section, sentence) != SyncBy.SPREAD) return sentence
        var s = sentence
        while (s > 0 && grade(section, s - 1) == SyncBy.SPREAD) s--
        return s
    }

    val head: ExtraSpan? get() = extras.firstOrNull { it.place == ExtraPlace.HEAD }

    fun isFirst(section: Int, sentence: Int) = first == section to sentence

    /** Played right after this sentence (not skipped by the settings): talk in the middle, or 片尾. */
    fun extraAfter(section: Int, sentence: Int): ExtraSpan? {
        val end = ends[section to sentence] ?: return null
        return extras.firstOrNull { e ->
            kotlin.math.abs(e.t0 - end) < 0.5 && e.t1 - e.t0 >= 1 &&
                (e.place == ExtraPlace.MIDDLE && !settings.middle || e.place == ExtraPlace.TAIL && !settings.headTail)
        }
    }
}
