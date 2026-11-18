package app.yuyue.speech

import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import app.yuyue.library.Library
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import uniffi.yuyue_core.AudiobookCaps
import uniffi.yuyue_core.AudiobookResult
import uniffi.yuyue_core.AudiobookTask
import uniffi.yuyue_core.AudioRecording
import uniffi.yuyue_core.HeardWord
import java.io.File
import java.io.RandomAccessFile
import java.nio.ByteBuffer
import java.nio.ByteOrder

/**
 * 有声书's background work on Android, as on Apple: the core hands out what's to be done for a
 * recording (find the speech, transcribe, 精修 a window), this runs it and hands the answer back
 * until the core has made the recording's map.
 *
 * There's no system transcriber that takes a file here, so the alignment model does the
 * hearing: its likeliest letter each frame is the transcript (pinyin or English letters), which
 * the core lays against the book's letters. The model's output for each file is the one 合成
 * saved; speech is found from each 30 ms frame's loudness, measured once a file and kept.
 */
class AudiobookWork(private val context: Context, private val library: Library) {
    private val running = mutableMapOf<String, Job>()
    private val focus = mutableMapOf<String, Double>()
    /** What was asked for this run of the app, to take up again when the whole book may go on. */
    private val asked = mutableMapOf<String, String>()

    init {
        // Plugged in, or the screen gone dark: the whole book may go on now.
        val filter = IntentFilter().apply {
            addAction(Intent.ACTION_SCREEN_OFF)
            addAction(Intent.ACTION_POWER_CONNECTED)
        }
        context.applicationContext.registerReceiver(object : android.content.BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                library.scope.launch { asked.toMap().forEach { (audio, ebook) -> start(audio, ebook, null) } }
            }
        }, filter)
    }

    /** Work on 有声书 `audio` laid against `ebook`, nearest `at` seconds first. Asking again while
     *  it runs only moves where it starts from next. */
    fun start(audio: String, ebook: String, at: Double?) {
        if (at != null) focus[audio] = at
        asked[audio] = ebook
        if (running[audio]?.isActive == true) return
        val core = library.core
        if (runCatching { core.audiobookProgress(audio).pairedWith }.getOrNull() != ebook) runCatching { core.audiobookPair(audio, ebook) }
        running[audio] = library.scope.launch(Dispatchers.Default) {
            runCatching { run(audio, ebook) }
            running.remove(audio)
            // Made now or before (合成 may have written its own times since): taken in again.
            if (runCatching { library.core.audiobookProgress(audio).done }.getOrDefault(false)) {
                library.scope.launch { library.recordings.mapChanged(ebook) }
            }
        }
    }

    fun stop(audio: String) {
        asked.remove(audio)
        running.remove(audio)?.cancel()
    }

    private suspend fun kotlinx.coroutines.CoroutineScope.run(audio: String, ebook: String) {
        val core = library.core
        val rec = core.audioRecording(audio)
        val folder = library.recordings.folderOf(ebook)
        while (isActive) {
            // Asked each time: the model may have come down meanwhile, the phone plugged in.
            val model = Aligner.installed(library.dataDir)
            core.audiobookSetCaps(AudiobookCaps(vad = true, transcribe = model, letters = true, align = model, wholeBookNow = charging() && !screenOn()))
            val task = core.audiobookNextTask(audio, focus[audio]) ?: break
            val (id, result) = when (task) {
                is AudiobookTask.Vad -> task.id to vad(rec, folder, task.file.toInt(), task.from, task.to, task.fileOffset)
                is AudiobookTask.Transcribe -> task.id to transcribe(rec, folder, task.file.toInt(), task.spans.map { it.t0 to it.t1 }, task.fileOffset)
                is AudiobookTask.Align -> task.id to align(rec, folder, task.file.toInt(), task.from, task.to, task.sentences, task.lang, task.fileOffset)
            }
            val progress = core.audiobookSubmit(audio, id, if (isActive) result else AudiobookResult.Returned)
            if (app.yuyue.BuildConfig.DEBUG) android.util.Log.d("AudiobookWork", "$id → ${brief(result)}; paired ${progress.pairedWith} ${progress.matchKind}, ${progress.sentences} sentences, exact ${progress.exact} spread ${progress.spread} unread ${progress.unread}, done ${progress.done}")
            if (progress.done) {
                if (app.yuyue.BuildConfig.DEBUG) {
                    for (r in runCatching { core.audiobookMap(audio)?.rows }.getOrNull().orEmpty()) android.util.Log.d("AudiobookWork", "row ${r.sentence} ${r.t0Ms} ${r.t1Ms} ${r.by}")
                    android.util.Log.d("AudiobookWork", "done; extras " + runCatching { core.audiobookExtras(audio) }.getOrNull().orEmpty().joinToString { "${it.place} ${"%.1f".format(it.t0)}–${"%.1f".format(it.t1)}" })
                }
                break
            }
        }
    }

    private fun brief(r: AudiobookResult): String = when (r) {
        is AudiobookResult.Vad -> "${r.speech.size} speech"
        is AudiobookResult.Transcribe -> "${r.words.size} letters: " + r.words.take(40).joinToString("") { it.text }
        is AudiobookResult.Align -> "${r.sentences.size} refined"
        is AudiobookResult.Failed -> "failed ${r.reason}"
        else -> "returned"
    }

    /** The screen is on (someone may be using the phone): the whole book waits, so it isn't
     *  warm and slow in the hand. */
    private fun screenOn(): Boolean = (context.getSystemService(android.content.Context.POWER_SERVICE) as android.os.PowerManager).isInteractive

    private fun charging(): Boolean {
        val status = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        return status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
    }

    // MARK: 人声检测

    private suspend fun vad(rec: AudioRecording, folder: File, file: Int, from: Double, to: Double, offset: Double): AudiobookResult = try {
        val levels = levelsFile(rec, folder, file)
        val frame = 0.03
        val count = (levels.length() / 4).toInt()
        val lo = minOf(count, maxOf(0, ((from - offset) / frame).toInt()))
        val hi = minOf(count, maxOf(lo, ((to - offset) / frame).toInt()))
        val values = readFloats(levels, lo.toLong(), hi - lo)
        AudiobookResult.Vad(library.core.audiobookSpeech(values.toList(), frame, offset + lo * frame), emptyList())
    } catch (e: kotlinx.coroutines.CancellationException) {
        throw e
    } catch (e: Exception) {
        AudiobookResult.Failed(e.message ?: e.toString())
    }

    /** Each file's levels, measured the first time they're wanted. */
    private suspend fun levelsFile(rec: AudioRecording, folder: File, file: Int): File {
        val f = File(folder, "levels-$file.f32")
        if (f.isFile && f.length() > 0) return f
        val values = Aligner.levels(File(rec.files[file].path))
        val part = File(folder, "levels-$file.f32.part")
        part.outputStream().use { out ->
            val b = ByteBuffer.allocate(values.size * 4).order(ByteOrder.LITTLE_ENDIAN)
            b.asFloatBuffer().put(values)
            out.write(b.array())
        }
        part.renameTo(f)
        return f
    }

    private fun readFloats(f: File, at: Long, n: Int): FloatArray {
        val out = FloatArray(n)
        RandomAccessFile(f, "r").use { r ->
            val bytes = ByteArray(n * 4)
            r.seek(at * 4)
            r.readFully(bytes)
            ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).asFloatBuffer().get(out)
        }
        return out
    }

    // MARK: 听写 by the model's letters, and 精修

    private suspend fun emissionsFile(rec: AudioRecording, folder: File, file: Int): File {
        val f = File(folder, "emissions-$file.f32")
        if (f.isFile && f.length() > 0 && f.length() % (4 * Aligner.LETTERS) == 0L) return f
        library.recordings.saveEmissions(f, Aligner.emissions(library.dataDir, File(rec.files[file].path)) { }.first)
        return f
    }

    private suspend fun transcribe(rec: AudioRecording, folder: File, file: Int, spans: List<Pair<Double, Double>>, offset: Double): AudiobookResult = try {
        val em = emissionsFile(rec, folder, file)
        val seconds = rec.files[file].seconds
        val words = mutableListOf<HeardWord>()
        for ((a, b) in spans) words += library.core.audiobookHeardLettersSaved(em.path, seconds, offset, a, b)
        AudiobookResult.Transcribe(words)
    } catch (e: kotlinx.coroutines.CancellationException) {
        throw e
    } catch (e: Exception) {
        AudiobookResult.Failed(e.message ?: e.toString())
    }

    private suspend fun align(rec: AudioRecording, folder: File, file: Int, from: Double, to: Double, sentences: List<String>, lang: String, offset: Double): AudiobookResult = try {
        val em = emissionsFile(rec, folder, file)
        library.core.audiobookAlignWindowSaved(sentences, lang, em.path, rec.files[file].seconds, offset, from, to)
            ?.let { AudiobookResult.Align(it) } ?: AudiobookResult.Failed("no model output")
    } catch (e: kotlinx.coroutines.CancellationException) {
        throw e
    } catch (e: Exception) {
        AudiobookResult.Failed(e.message ?: e.toString())
    }
}
