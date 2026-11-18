package app.yuyue.speech

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONObject
import uniffi.yuyue_core.AudioFile
import uniffi.yuyue_core.AudioImport
import java.io.File
import java.io.RandomAccessFile
import java.util.concurrent.TimeUnit

/**
 * 局域网传录音 on Android, as on the iPhone: a Mac with the recording offers it on the local
 * network (`_yuyue-rec._tcp`); this finds it and takes the files over a piece at a time, then
 * brings them in as the same recording, so the synced map and progress are there already. Every
 * request carries the secret this person's devices share through 同步.
 */
class RecordingFetch(private val context: Context, private val library: Library) {
    class Offer(val device: String, val base: String, val files: List<Triple<String, Long, Double>>)

    private val hosts = HashMap<String, String>()
    private val http = OkHttpClient.Builder().readTimeout(60, TimeUnit.SECONDS).build()
    /** Taking one over: recording id → 0–1. */
    val taking = mutableStateMapOf<String, Double>()
    val problem = mutableStateMapOf<String, String>()
    private var started = false

    fun browse() {
        if (started) return
        started = true
        val nsd = context.getSystemService(Context.NSD_SERVICE) as NsdManager
        nsd.discoverServices("_yuyue-rec._tcp", NsdManager.PROTOCOL_DNS_SD, object : NsdManager.DiscoveryListener {
            override fun onServiceFound(info: NsdServiceInfo) {
                @Suppress("DEPRECATION")
                nsd.resolveService(info, object : NsdManager.ResolveListener {
                    override fun onServiceResolved(r: NsdServiceInfo) {
                        @Suppress("DEPRECATION")
                        val host = r.host?.hostAddress ?: return
                        val h = if (host.contains(':')) "[${host.substringBefore('%')}]" else host
                        synchronized(hosts) { hosts[r.serviceName] = "http://$h:${r.port}" }
                    }
                    override fun onResolveFailed(r: NsdServiceInfo, code: Int) {}
                })
            }
            override fun onServiceLost(info: NsdServiceInfo) { synchronized(hosts) { hosts.remove(info.serviceName) } }
            override fun onDiscoveryStarted(type: String) {}
            override fun onDiscoveryStopped(type: String) {}
            override fun onStartDiscoveryFailed(type: String, code: Int) {}
            override fun onStopDiscoveryFailed(type: String, code: Int) {}
        })
    }

    private fun get(url: String, token: String) = http.newCall(Request.Builder().url(url).header("x-yuyue-token", token).build()).execute()

    /** A device nearby that has recording `id`, when one does. */
    suspend fun offer(id: String): Offer? = withContext(Dispatchers.IO) {
        val token = runCatching { library.core.lanToken() }.getOrNull() ?: return@withContext null
        for ((device, base) in synchronized(hosts) { hosts.toMap() }) {
            val o = runCatching {
                get("$base/has?id=$id", token).use { r -> if (r.code == 200) JSONObject(r.body!!.string()) else null }
            }.getOrNull() ?: continue
            val arr = o.getJSONArray("files")
            return@withContext Offer(device, base, (0 until arr.length()).map { i -> arr.getJSONObject(i).let { Triple(it.getString("name"), it.getLong("size"), it.getDouble("seconds")) } })
        }
        null
    }

    /** 局域网传片段: what a Mac nearby has made of a book's play — its manifest and the clips it has. */
    class DramaOffer(val device: String, val base: String, val manifest: String, val keys: List<String>)

    /** Taking clips over: book → (got, of). */
    val takingClips = mutableStateMapOf<String, Pair<Int, Int>>()
    private val pendingBookCards = HashMap<String, String>()

    /** Adopts any book-designed cards fetched from Mac while a chapter was playing (§5.3 & §8). */
    fun applyPendingCards(id: String): Boolean {
        val json = synchronized(pendingBookCards) { pendingBookCards.remove(id) } ?: return false
        return runCatching { library.core.cardsAdoptBookJson(id, json) > 0u }.getOrDefault(false)
    }

    /** Tells nearby paired Macs which book and chapter this phone is listening to as a play (§5.4). */
    fun want(id: String, section: UInt) {
        browse()
        library.scope.launch(Dispatchers.IO) {
            val token = runCatching { library.core.lanToken() }.getOrNull() ?: return@launch
            val bases = synchronized(hosts) { hosts.values.toList() }
            for (base in bases) {
                runCatching { get("$base/want?id=$id&section=$section", token).close() }
            }
        }
    }

    suspend fun dramaOffer(id: String): DramaOffer? = withContext(Dispatchers.IO) {
        // Just started looking: give the network a moment to answer.
        if (synchronized(hosts) { hosts.isEmpty() }) kotlinx.coroutines.delay(2000)
        val token = runCatching { library.core.lanToken() }.getOrNull() ?: return@withContext null
        for ((device, base) in synchronized(hosts) { hosts.toMap() }) {
            val o = runCatching {
                get("$base/drama?id=$id", token).use { r -> if (r.code == 200) JSONObject(r.body!!.string()) else null }
            }.getOrNull() ?: continue
            val arr = o.getJSONArray("keys")
            if (arr.length() == 0) continue
            return@withContext DramaOffer(device, base, o.getString("manifest"), (0 until arr.length()).map { arr.getString(it) })
        }
        null
    }

    /** Takes the clips this phone lacks from `offer` (they come as mono 48 kHz float, as the
     *  clips here are kept), pulls `/card` for book-designed cards, then has the core play them. */
    fun takeClips(id: String, offer: DramaOffer) {
        if (takingClips.containsKey(id)) return
        val token = runCatching { library.core.lanToken() }.getOrNull() ?: return
        val synth = library.dramaSynth
        val want = offer.keys.filter { !synth.clipMade(id, it) }
        // Nothing new (and already taken): no work, and no news to set the card refreshing again.
        if (want.isEmpty() && library.core.dramaBorrowedFrom(id) != null) return
        takingClips[id] = 0 to want.size
        library.scope.launch {
            withContext(Dispatchers.IO) {
                // Pull book-designed voice cards alongside clips (§2.4 & §8).
                val cardJson = runCatching {
                    get("${offer.base}/card?id=$id", token).use { r -> if (r.code == 200) r.body?.string() else null }
                }.getOrNull()
                if (!cardJson.isNullOrBlank()) {
                    if (library.listening == id) {
                        synchronized(pendingBookCards) { pendingBookCards[id] = cardJson }
                    } else {
                        runCatching { library.core.cardsAdoptBookJson(id, cardJson) }
                    }
                }
                synth.clipsFolder(id).mkdirs()
                var got = 0
                for (k in want) {
                    val bytes = runCatching {
                        get("${offer.base}/clip?id=$id&key=$k", token).use { r -> if (r.code == 200) r.body!!.bytes() else null }
                    }.getOrNull() ?: break
                    // Sent as float; kept compressed.
                    val fb = java.nio.ByteBuffer.wrap(bytes).order(java.nio.ByteOrder.LITTLE_ENDIAN).asFloatBuffer()
                    if (runCatching { synth.writeClip(id, k, FloatArray(fb.remaining()).also { fb.get(it) }) }.isFailure) break
                    got++
                    val g = got
                    library.scope.launch { takingClips[id] = g to want.size }
                }
                val have = offer.keys.filter { synth.clipMade(id, it) }
                runCatching { library.core.dramaAdoptClips(id, offer.device, offer.manifest, have) }
            }
            takingClips.remove(id)
            synth.sectionMade.tryEmit(id)
        }
    }

    /** Takes recording `id` over from `offer` and brings it onto the shelf. */
    fun take(id: String, offer: Offer) {
        if (taking.containsKey(id)) return
        val token = runCatching { library.core.lanToken() }.getOrNull() ?: return
        taking[id] = 0.0
        problem.remove(id)
        library.scope.launch {
            val ok = withContext(Dispatchers.IO) {
                runCatching {
                    val dir = File(context.cacheDir, "take-$id").apply { mkdirs() }
                    val total = offer.files.sumOf { it.second }.coerceAtLeast(1)
                    var got = 0L
                    val files = mutableListOf<AudioFile>()
                    for ((n, f) in offer.files.withIndex()) {
                        val out = File(dir, f.first)
                        RandomAccessFile(out, "rw").use { raf ->
                            // Going on from what came before, if a take was cut off.
                            var have = raf.length()
                            raf.seek(have)
                            while (have < f.second) {
                                val bytes = get("${offer.base}/piece?id=$id&n=$n&from=$have", token).use { r -> if (r.code == 200) r.body!!.bytes() else null }
                                if (bytes == null || bytes.isEmpty()) error("piece")
                                raf.write(bytes)
                                have += bytes.size
                                val p = (got + have).toDouble() / total
                                library.scope.launch { taking[id] = p }
                            }
                        }
                        got += f.second
                        files += AudioFile(out.path, f.third)
                    }
                    val title = runCatching { library.core.book(id).summary.title }.getOrDefault(offer.device)
                    library.core.importAudiobook(AudioImport(files, title, emptyList(), emptyList(), null))
                    dir.deleteRecursively()
                }.isSuccess
            }
            taking.remove(id)
            if (ok) {
                library.reload()
                library.recordings.adopt()
            } else {
                problem[id] = t("没传完，回到同一个 Wi‑Fi 再点一次接着传")
            }
        }
    }
}
