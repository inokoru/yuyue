package app.yuyue.speech

import android.content.Context
import androidx.compose.runtime.mutableStateMapOf
import app.yuyue.library.Library
import app.yuyue.library.RemoteClient
import app.yuyue.library.RemoteSources
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.AudioFile
import uniffi.yuyue_core.AudioIdPart
import uniffi.yuyue_core.AudioImport
import uniffi.yuyue_core.BookSummary
import java.io.File

/**
 * 边下边听 on Android, as on Apple: a recording kept on a share (WebDAV), listened to while it
 * comes down. Its id is worked out from each file's ends, so a map and progress synced from
 * another device fit it at once; it goes on the shelf straight away; its files come in the order
 * they play, the one being listened to first.
 */
class ShareStream(private val context: Context, private val library: Library) {
    class FileRef(val name: String, val url: String, val size: Long)

    private val prefs get() = context.getSharedPreferences("shareStream", Context.MODE_PRIVATE)
    /** Coming down now: recording id → (file, 0–1). */
    val fetching = mutableStateMapOf<String, Pair<Int, Float>>()
    val problem = mutableStateMapOf<String, String>()
    /** A file has come: (recording id, file). */
    val arrived = MutableSharedFlow<Pair<String, Int>>(extraBufferCapacity = 8)
    private val loops = HashMap<String, Job>()
    private val wanted = HashMap<String, Int>()

    private fun job(id: String): Pair<String, List<FileRef>>? {
        val j = prefs.getString(id, null)?.let { JSONObject(it) } ?: return null
        val arr = j.getJSONArray("files")
        return j.getString("source") to (0 until arr.length()).map { arr.getJSONObject(it).let { f -> FileRef(f.getString("name"), f.getString("url"), f.getLong("size")) } }
    }

    fun streams(id: String) = prefs.contains(id)

    /** The recording already taken from these files, if any. */
    fun recording(files: List<FileRef>): String? {
        val urls = files.map { it.url }
        return prefs.all.keys.firstOrNull { id -> job(id)?.second?.map { it.url } == urls }
    }

    /** Paused by hand (the ring's square). */
    val paused = mutableStateMapOf<String, Boolean>()

    fun pause(id: String) {
        paused[id] = true
        loops.remove(id)?.cancel()
        fetching.remove(id)
    }

    fun carryOn(id: String) {
        paused.remove(id)
        run(id)
    }

    /** Files here, of all; and how far the whole recording is (bytes), for the rings. */
    fun progress(id: String): Triple<Int, Int, Float> {
        val files = job(id)?.second ?: return Triple(0, 0, 0f)
        val rec = runCatching { library.core.audioRecording(id) }.getOrNull() ?: return Triple(0, 0, 0f)
        val here = rec.files.indices.filter { File(rec.files[it].path).isFile }
        val total = files.sumOf { it.size }.coerceAtLeast(1)
        var done = here.sumOf { files.getOrNull(it)?.size ?: 0L }.toDouble()
        fetching[id]?.let { (n, f) -> done += (files.getOrNull(n)?.size ?: 0L) * f }
        return Triple(here.size, rec.files.size, (done / total).toFloat())
    }

    /** Puts a share's recording (its files, in play order) on the shelf and starts fetching it. */
    suspend fun take(files: List<FileRef>, title: String, source: app.yuyue.library.RemoteSource): BookSummary {
        val client = RemoteClient(source)
        val edge = 1L shl 20
        val parts = files.map { f ->
            val head = client.range(f.url, 0, minOf(f.size, edge))
            val tailLength = if (f.size > edge) minOf(edge, f.size - edge) else 0
            AudioIdPart(f.size.toULong(), head, client.range(f.url, f.size - tailLength, tailLength))
        }
        val core = library.core
        val id = core.audioIdParts(parts)
        val known = runCatching { core.audiobookMap(id)?.durations }.getOrNull().orEmpty()
        val import = AudioImport(files.mapIndexed { i, f -> AudioFile(f.name, known.getOrElse(i) { 0.0 }) }, title, emptyList(), emptyList(), null)
        val had = library.books.filter { it.available }.map { it.id }.toSet()
        val book = withContext(Dispatchers.IO) { core.audiobookReserve(id, import) }
        prefs.edit().putString(id, JSONObject().put("source", source.id).put("files", JSONArray(files.map { JSONObject().put("name", it.name).put("url", it.url).put("size", it.size) })).toString()).apply()
        library.reload()
        if (book.id !in had) {
            val sourceTag = if (source.kind == app.yuyue.library.RemoteSource.Kind.OPDS) "opds" else "webdav"
            app.yuyue.analytics.AirTraffic.trackBookImported(
                app.yuyue.analytics.AirTraffic.resolveBookFormat(book.format),
                sourceTag,
            )
        }
        run(id)
        return book
    }

    /** The file being listened to: fetched next if it isn't here. */
    fun want(id: String, file: Int) {
        if (!streams(id)) return
        wanted[id] = file
        run(id)
    }

    /** Goes on with every recording that still has files to fetch (at launch). */
    fun resume() {
        for (id in prefs.all.keys) run(id)
    }

    private fun missing(id: String): List<Int> {
        val rec = runCatching { library.core.audioRecording(id) }.getOrNull() ?: return emptyList()
        return rec.files.indices.filter { !File(rec.files[it].path).isFile }
    }

    private fun run(id: String) {
        if (loops[id]?.isActive == true || paused[id] == true) return
        val (sourceId, files) = job(id) ?: return
        val source = RemoteSources().all.firstOrNull { it.id == sourceId } ?: return
        problem.remove(id)
        loops[id] = library.scope.launch {
            val client = RemoteClient(source)
            while (true) {
                val left = withContext(Dispatchers.IO) { missing(id) }
                if (left.isEmpty()) break
                val w = wanted[id] ?: 0
                val n = left.firstOrNull { it >= w } ?: left[0]
                val f = files[n]
                val temp = File(context.cacheDir, "stream-$id-$n." + f.name.substringAfterLast('.', "mp3"))
                fetching[id] = n to 0f
                val ok = runCatching {
                    client.save(f.url, temp) { p -> library.scope.launch { fetching[id] = n to p } }
                    val seconds = runCatching {
                        android.media.MediaMetadataRetriever().use { r ->
                            r.setDataSource(temp.path)
                            (r.extractMetadata(android.media.MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L) / 1000.0
                        }
                    }.getOrDefault(0.0)
                    withContext(Dispatchers.IO) { library.core.audiobookFileArrived(id, n.toUInt(), temp.path, seconds) }
                }.isSuccess
                if (!ok && paused[id] == true) break
                if (!ok) {
                    problem[id] = t("连不上网盘，回到网络里接着下")
                    break
                }
                arrived.tryEmit(id to n)
            }
            // Not one paused and started again since: that one goes on.
            if (loops[id] === coroutineContext[Job]) {
                fetching.remove(id)
                loops.remove(id)
            }
        }
    }

    /** 听过的自动清掉, by recording: off unless turned on. */
    fun clears(id: String) = prefs(id).getBoolean("clear", false)

    fun setClears(id: String, on: Boolean, file: Int) {
        prefs(id).edit().putBoolean("clear", on).apply()
        if (on) tidy(id, file)
    }

    private fun prefs(id: String) = context.getSharedPreferences("shareStream.$id", Context.MODE_PRIVATE)

    /** What this recording's files take on this phone. */
    fun bytesHere(id: String): Long {
        val rec = runCatching { library.core.audioRecording(id) }.getOrNull() ?: return 0
        return rec.files.sumOf { File(it.path).takeIf(File::isFile)?.length() ?: 0L }
    }

    /** 听过的自动清掉: notes when each file before `file` (the one being listened to) was first
     *  passed, and with clearing on, takes the ones passed a day ago or more off this phone (the
     *  share keeps them); listening back to one fetches it again. */
    fun tidy(id: String, file: Int) {
        if (!streams(id)) return
        val rec = runCatching { library.core.audioRecording(id) }.getOrNull() ?: return
        val p = prefs(id)
        val e = p.edit()
        val now = System.currentTimeMillis()
        for (i in rec.files.indices) {
            if (i < file && !p.contains("passed$i")) e.putLong("passed$i", now)
            // Gone back to: listened to again, not passed.
            if (i >= file) e.remove("passed$i")
        }
        e.apply()
        if (!clears(id)) return
        for (i in rec.files.indices) {
            val at = p.getLong("passed$i", 0L).takeIf { it > 0 } ?: if (i < file) now else continue
            if (i < file && now - at >= 86_400_000L) File(rec.files[i].path).delete()
        }
    }
}
