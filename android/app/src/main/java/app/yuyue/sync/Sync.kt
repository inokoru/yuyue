package app.yuyue.sync

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.util.Xml
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.design.Prefs
import app.yuyue.library.Keys
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONObject
import org.xmlpull.v1.XmlPullParser
import uniffi.yuyue_core.DeviceInfo
import uniffi.yuyue_core.SyncPart
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.io.IOException
import java.net.ConnectException
import java.net.SocketTimeoutException
import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.net.UnknownHostException
import kotlin.coroutines.coroutineContext
import okhttp3.Credentials
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.util.concurrent.TimeUnit

/**
 * 同步, with no server of ours, as on Apple: every device keeps one file in a folder the user
 * owns (a WebDAV share) holding every change it knows of, and reads the others'. The operation
 * log merges in any order, so nothing here has to be careful about who writes first. The
 * folder's layout is the same as Apple's, so phones, Macs and iPhones meet in the same one.
 */
class Sync(private val context: Context, private val core: YuyueCore, private val library: Library) {
    sealed interface State {
        data object Idle : State
        data object Syncing : State
        data class Failed(val message: String) : State
    }

    data class WebDAVAccount(val url: String, val user: String, val password: String, val folder: String = t("语阅"))

    var state by mutableStateOf<State>(State.Idle)
        private set
    var lastSynced by mutableStateOf(Prefs.shared.getLong(LAST, 0L).takeIf { it > 0 })
        private set
    var devices by mutableStateOf<List<DeviceInfo>>(emptyList())
        private set
    /** "webdav", "drive", or "none". */
    var method by mutableStateOf(Prefs.string(METHOD, "none"))
        private set
    val on get() = method == "webdav" || method == "drive"
    /** The Google account's address, to show. */
    var driveEmail by mutableStateOf(Prefs.string(DriveAuth.EMAIL).ifEmpty { null })
        private set
    /** 书文件: each book's own file goes into the folder too. On the phone only over Wi‑Fi. */
    var syncsFiles by mutableStateOf(Prefs.bool(FILES, true))
        private set
    /** 同步这些: what this device neither sends nor takes. */
    var leftOut by mutableStateOf(Prefs.string(LEFT_OUT).split(',').filter { it.isNotEmpty() }.toSet())
        private set
    private var running: Job? = null

    init {
        loadDevices()
    }

    fun loadDevices() {
        devices = runCatching { core.devices() }.getOrDefault(emptyList())
    }

    val webdav: WebDAVAccount?
        get() {
            val url = Prefs.string(URL_KEY).takeIf { it.isNotEmpty() } ?: return null
            return WebDAVAccount(url, Prefs.string(USER), Keys.read(PASSWORD) ?: "", Prefs.string(FOLDER, t("语阅")))
        }

    fun includes(part: SyncPart) = part.name !in leftOut

    fun include(part: SyncPart, yes: Boolean) {
        leftOut = if (yes) leftOut - part.name else leftOut + part.name
        Prefs.set(LEFT_OUT, leftOut.sorted().joinToString(","))
        now()
    }

    fun syncFiles(yes: Boolean) {
        syncsFiles = yes
        Prefs.set(FILES, yes)
        now()
    }

    /** Checks the account can be reached and written, then keeps it and syncs. */
    suspend fun connect(account: WebDAVAccount) {
        val normalizedUrl = account.url.trim().trimEnd('/') + "/"
        val normalized = account.copy(
            url = normalizedUrl,
            user = account.user.trim(),
            folder = account.folder.trim('/', ' ', '\t', '\n', '\r'),
        )
        WebDAVFolder(normalized).prepare()
        val previous = webdav
        val targetChanged = method != "webdav" ||
            previous == null ||
            previous.url.trimEnd('/') != normalized.url.trimEnd('/') ||
            previous.user != normalized.user ||
            previous.folder != normalized.folder
        Prefs.set(URL_KEY, normalized.url)
        Prefs.set(USER, normalized.user)
        Prefs.set(FOLDER, normalized.folder)
        Keys.write(PASSWORD, normalized.password)
        if (targetChanged) {
            running?.cancel()
            running = null
            resetRemoteCache()
        }
        Prefs.set(METHOD, "webdav")
        method = "webdav"
        now()
    }

    /** Google Drive, once the phone's account has allowed it: checks it can be listed, then
     *  keeps it and syncs. */
    suspend fun connectDrive() {
        val folder = DriveFolder(context)
        folder.prepare()
        folder.list()
        driveEmail = folder.email()
        Prefs.set(DriveAuth.EMAIL, driveEmail ?: "")
        running?.cancel()
        running = null
        resetRemoteCache()
        Prefs.set(METHOD, "drive")
        method = "drive"
        now()
    }

    private fun resetRemoteCache() {
        Prefs.set(SEEN, "{}")
        Prefs.set(WRITTEN, "")
        Prefs.set(LEFT_OUT_DONE, "\u0000")
        Prefs.set("sync.companion.seen", "{}")
        Prefs.set("sync.companion.sent", "{}")
    }

    private fun folder(): SyncFolder? = when (method) {
        "webdav" -> webdav?.let(::WebDAVFolder)
        "drive" -> DriveFolder(context)
        else -> null
    }

    fun stop() {
        running?.cancel()
        running = null
        Prefs.set(METHOD, "none")
        method = "none"
        state = State.Idle
    }

    /** Exchanges changes now, unless an exchange is already under way. */
    fun now() {
        if (running != null) return
        val folder = folder() ?: return
        state = State.Syncing
        running = library.scope.launch {
            try {
                var applied = withContext(Dispatchers.IO) {
                    var n = exchange(folder)
                    n += exchangePodcastFiles(folder, includeClips = syncsFiles && onWifi())
                    n += exchangeCompanionCaches(folder)
                    n
                }
                var missed = 0
                if (syncsFiles && onWifi()) withContext(Dispatchers.IO) { exchangeFiles(folder) }.let { (took, notGot) -> applied += took; missed = notGot }
                lastSynced = System.currentTimeMillis()
                Prefs.set(LAST, lastSynced!!)
                // Notes and progress went across; a book file that didn't come down isn't 已同步.
                state = if (missed > 0) State.Failed(t("有 %lld 本书的文件没下载下来，下次同步再试", missed)) else State.Idle
                if (applied > 0) {
                    library.reload()
                    library.recordings.adopt()
                }
            } catch (e: CancellationException) {
                state = State.Idle
            } catch (e: Exception) {
                state = State.Failed(message(e))
            }
            loadDevices()
            running = null
        }
    }

    /** Syncs every few minutes while the app is open. */
    suspend fun keepUp() {
        while (true) {
            now()
            delay(5 * 60 * 1000L)
        }
    }

    private fun onWifi(): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val caps = cm.getNetworkCapabilities(cm.activeNetwork) ?: return false
        return caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
    }

    /** Reads every other device's file that changed since last time, then writes this device's
     *  own when it knows something new. Returns how many values changed here. */
    private suspend fun exchange(folder: SyncFolder): Int {
        folder.prepare()
        val mine = core.deviceId() + SUFFIX
        val files = folder.list()
        val parts = SyncPart.entries.filter { it.name in leftOut }
        val before = Prefs.string(LEFT_OUT_DONE, "\u0000").takeIf { it != "\u0000" }?.split(',')?.filter { it.isNotEmpty() }?.toSet()
        // A part on again: what was passed over in files already read is only in them, so read them all.
        val reread = before != null && !leftOut.containsAll(before)
        val seen = if (reread) mutableMapOf() else JSONObject(Prefs.string(SEEN, "{}")).let { j -> j.keys().asSequence().associateWith { j.getString(it) }.toMutableMap() }
        var applied = 0
        for ((name, tag) in files) {
            if (!name.endsWith(SUFFIX) || name == mine || seen[name] == tag) continue
            coroutineContext.ensureActive()
            applied += core.importChangesExcept(String(folder.read(name)), parts).toInt()
            seen[name] = tag
        }
        Prefs.set(SEEN, JSONObject(seen as Map<*, *>).toString())
        val version = core.syncVersion() + "|" + leftOut.sorted().joinToString(",")
        if (files[mine] == null || Prefs.string(WRITTEN) != version) {
            folder.write(mine, core.exportChangesExcept("{}", parts).toByteArray())
            Prefs.set(WRITTEN, version)
        }
        Prefs.set(LEFT_OUT_DONE, leftOut.sorted().joinToString(","))
        return applied
    }

    /** 书文件: a book's file is `<id>.book.<ext>`; ids are the file's hash, so the same file
     *  imported anywhere is the same book. One failing doesn't stop the rest. */
    private suspend fun exchangeFiles(folder: SyncFolder): Pair<Int, Int> {
        val files = runCatching { folder.list() }.getOrNull() ?: return 0 to 0
        val books = runCatching { core.books() }.getOrDefault(emptyList())
        var took = 0
        var missed = 0
        for (b in books.filter { it.available }) {
            if (files.keys.any { it.startsWith(b.id + ".book.") }) continue
            coroutineContext.ensureActive()
            val path = runCatching { core.originalFile(b.id) }.getOrNull() ?: continue
            runCatching { folder.upload("${b.id}.book.${File(path).extension}", File(path)) }
        }
        val temp = File(context.cacheDir, "sync-${System.nanoTime()}").apply { mkdirs() }
        try {
            for (b in books.filter { !it.available }) {
                val name = files.keys.firstOrNull { it.startsWith(b.id + ".book.") } ?: continue
                coroutineContext.ensureActive()
                // Named as the book is, so a TXT's title (taken from its file name) comes out the same.
                val file = File(temp, b.title.replace(Regex("""[/\\:?*"<>|]"""), "-") + "." + name.substringAfterLast('.'))
                runCatching {
                    folder.download(name, file)
                    library.importPath(file.path)
                    took++
                }.onFailure { if (it is CancellationException) throw it; missed++ }
                file.delete()
            }
        } finally {
            temp.deleteRecursively()
        }
        return took to missed
    }

    /** 播客逐字稿 / 书房电台剧本 (`*.pod-issue.json`) 与原声书摘切片 (`*.pod-clip`)，与 Apple 端目录协议一致. */
    private suspend fun exchangePodcastFiles(folder: SyncFolder, includeClips: Boolean): Int {
        val files = runCatching { folder.list() }.getOrNull() ?: return 0
        val clipsPath = runCatching { core.podcastClipsDir() }.getOrNull() ?: return 0
        val hubDir = File(clipsPath).parentFile ?: return 0
        var took = 0

        if (SyncPart.READING.name !in leftOut) {
            val issuesDir = File(hubDir, "issues").apply { mkdirs() }
            val localIssues = issuesDir.listFiles()?.filter { it.isFile && it.name.endsWith(".json") && !it.name.startsWith(".") }.orEmpty()
            for (f in localIssues) {
                coroutineContext.ensureActive()
                val id = f.name.removeSuffix(".json")
                val remote = "$id.pod-issue.json"
                if (remote in files) continue
                runCatching { folder.write(remote, f.readBytes()) }
            }
            for (name in files.keys) {
                if (!name.endsWith(".pod-issue.json")) continue
                coroutineContext.ensureActive()
                val id = name.removeSuffix(".pod-issue.json")
                if (id.isEmpty() || "/" in id || ".." in id) continue
                val dest = File(issuesDir, "$id.json")
                if (dest.isFile) continue
                runCatching {
                    val data = folder.read(name)
                    dest.writeBytes(data)
                    took++
                }
            }
        }

        if (includeClips && SyncPart.NOTES.name !in leftOut) {
            val clipsDir = File(hubDir, "clips").apply { mkdirs() }
            val localClips = clipsDir.listFiles()?.filter { it.isFile && !it.name.startsWith(".") }.orEmpty()
            for (f in localClips) {
                coroutineContext.ensureActive()
                val remote = "${f.name}.pod-clip"
                if (remote in files) continue
                runCatching { folder.upload(remote, f) }
            }
            for (name in files.keys) {
                if (!name.endsWith(".pod-clip")) continue
                coroutineContext.ensureActive()
                val file = name.removeSuffix(".pod-clip")
                if (file.isEmpty() || file.startsWith(".") || "/" in file || ".." in file) continue
                val dest = File(clipsDir, file)
                if (dest.isFile) continue
                val partial = File(clipsDir, ".$file.download")
                runCatching {
                    folder.download(name, partial)
                    if (partial.renameTo(dest)) took++ else partial.delete()
                }.onFailure { partial.delete() }
            }
        }
        return took
    }

    /** 书伴问答与前情提要纯文本缓存 (`companion/<id>.json` / `<id>.recap.json`)，与 Apple 端协议一致，一处付费多端复用. */
    private suspend fun exchangeCompanionCaches(folder: SyncFolder): Int {
        if (SyncPart.READING.name in leftOut) return 0
        val files = runCatching { folder.list() }.getOrNull() ?: return 0
        val compDir = File(library.dataDir, "companion").apply { mkdirs() }
        val seen = runCatching { org.json.JSONObject(Prefs.string("sync.companion.seen", "{}")) }.getOrDefault(org.json.JSONObject())
        val sent = runCatching { org.json.JSONObject(Prefs.string("sync.companion.sent", "{}")) }.getOrDefault(org.json.JSONObject())

        fun stamp(f: File) = "${f.lastModified()}-${f.length()}"
        fun remoteName(localFile: String): String? = when {
            localFile.endsWith(".recap.json") -> localFile.removeSuffix(".recap.json").takeIf { it.isNotEmpty() }?.let { "$it.recap.json" }
            localFile.endsWith(".json") -> localFile.removeSuffix(".json").takeIf { it.isNotEmpty() }?.let { "$it.companion.json" }
            else -> null
        }
        fun localName(remote: String): String? = when {
            remote.endsWith(".recap.json") -> remote.removeSuffix(".recap.json")
                .takeIf { it.isNotEmpty() && "/" !in it && ".." !in it }?.let { "$it.recap.json" }
            remote.endsWith(".companion.json") -> remote.removeSuffix(".companion.json")
                .takeIf { it.isNotEmpty() && "/" !in it && ".." !in it }?.let { "$it.json" }
            else -> null
        }

        var took = 0
        for ((remote, tag) in files) {
            if (seen.optString(remote) == tag) continue
            val local = localName(remote) ?: continue
            coroutineContext.ensureActive()
            runCatching {
                val data = folder.read(remote)
                val remoteObj = org.json.JSONObject(String(data, Charsets.UTF_8))
                val dest = File(compDir, local)
                val merged = if (dest.isFile) runCatching { org.json.JSONObject(dest.readText()) }.getOrDefault(org.json.JSONObject()) else org.json.JSONObject()
                var added = false
                val keys = remoteObj.keys()
                while (keys.hasNext()) {
                    val k = keys.next()
                    if (!merged.has(k)) {
                        merged.put(k, remoteObj.get(k))
                        added = true
                    }
                }
                if (added || !dest.isFile) {
                    dest.writeText(merged.toString())
                    took++
                }
                seen.put(remote, tag)
            }
        }

        val locals = compDir.listFiles()?.filter { it.isFile && it.name.endsWith(".json") && !it.name.startsWith(".") }.orEmpty()
        for (f in locals) {
            val remote = remoteName(f.name) ?: continue
            coroutineContext.ensureActive()
            val s = stamp(f)
            if (sent.optString(remote) == s && remote in files) continue
            val bytes = runCatching { f.readBytes() }.getOrNull() ?: continue
            if (bytes.isEmpty()) continue
            runCatching {
                folder.write(remote, bytes)
                sent.put(remote, s)
            }
        }

        Prefs.set("sync.companion.seen", seen.toString())
        Prefs.set("sync.companion.sent", sent.toString())
        return took
    }

    class Status(val code: Int) : IOException("HTTP $code")

    /** The Google account has to allow 语阅 again. */
    class SignedOut : IOException("signed out")

    companion object {
        private const val SUFFIX = ".yuyue-sync.json"
        private const val METHOD = "sync.method"
        private const val LAST = "sync.last.ms"
        private const val URL_KEY = "sync.webdav.url"
        private const val USER = "sync.webdav.user"
        private const val PASSWORD = "sync.webdav.password"
        private const val FOLDER = "sync.webdav.folder"
        private const val FILES = "sync.files"
        private const val LEFT_OUT = "sync.leftOut"
        private const val LEFT_OUT_DONE = "sync.leftOut.done"
        private const val SEEN = "sync.seen"
        private const val WRITTEN = "sync.written"

        fun message(e: Throwable): String = when {
            e is SignedOut -> t("Google 账号要重新登录")
            e is Status && (e.code == 401 || e.code == 403) -> t("账号或密码不对")
            e is Status && e.code == 404 -> t("服务器上没有这个地址")
            e is Status -> t("服务器回了 %lld", e.code)
            e is UnknownHostException || e is ConnectException -> t("连不上服务器")
            e is SocketTimeoutException -> t("连接超时")
            else -> Library.message(e)
        }
    }
}

/** A folder on a WebDAV share: file name → a tag that changes when the file does. */
class WebDAVFolder(private val account: Sync.WebDAVAccount) : SyncFolder {
    private val baseUrl: String
        get() = account.url.trim().trimEnd('/')

    private val segments: List<String>
        get() = account.folder.split('/').filter { it.isNotEmpty() }

    private val dir: String
        get() {
            val path = segments.joinToString("/") { enc(it) }
            return if (path.isEmpty()) "$baseUrl/" else "$baseUrl/$path/"
        }

    private fun enc(s: String) = URLEncoder.encode(s, "UTF-8").replace("+", "%20")

    private fun request(url: String, method: String, body: RequestBody? = null): Request {
        val b = Request.Builder().url(url).method(method, body).header("Cache-Control", "no-cache")
        if (account.user.isNotEmpty()) b.header("Authorization", Credentials.basic(account.user, account.password))
        return b.build()
    }

    private fun send(r: Request, client: OkHttpClient = http, ok: (Int) -> Boolean = { it in 200..299 }): Response {
        val response = client.newCall(r).execute()
        if (!ok(response.code)) {
            response.close()
            throw Sync.Status(response.code)
        }
        return response
    }

    override fun prepare() {
        val segs = segments
        if (segs.isEmpty()) return
        var current = baseUrl
        for ((index, seg) in segs.withIndex()) {
            current = "$current/${enc(seg)}"
            val isLast = index == segs.lastIndex
            // 405: collection already exists; non-final parent mounts may answer 301/403.
            send(request("$current/", "MKCOL")) { code ->
                code in 200..299 || code == 405 || (!isLast && (code == 301 || code == 403))
            }.close()
        }
    }

    override fun list(): Map<String, String> {
        val body = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getetag/><d:getlastmodified/><d:getcontentlength/></d:prop></d:propfind>"""
            .toRequestBody("application/xml".toMediaType())
        val dirUrl = dir
        val r = request(dirUrl, "PROPFIND", body).newBuilder().header("Depth", "1").build()
        val basePath = normalizePath(runCatching { URI(dirUrl).path ?: "" }.getOrDefault(""))
        return send(r).use { parse(it.body!!.byteStream(), basePath) }
    }

    override fun read(name: String): ByteArray = send(request(dir + enc(name), "GET")).use { it.body!!.bytes() }

    override fun write(name: String, data: ByteArray) {
        val mediaType = if (name.endsWith(".json")) "application/json" else "application/octet-stream"
        send(request(dir + enc(name), "PUT", data.toRequestBody(mediaType.toMediaType()))).close()
    }

    override fun upload(name: String, file: File) {
        send(request(dir + enc(name), "PUT", file.asRequestBody("application/octet-stream".toMediaType())), slow).close()
    }

    override fun download(name: String, to: File) {
        val part = File(to.path + ".download")
        send(request(dir + enc(name), "GET"), slow).use { r -> r.body!!.byteStream().use { input -> part.outputStream().use { input.copyTo(it) } } }
        if (!part.renameTo(to)) throw IOException("rename")
    }

    private fun normalizePath(raw: String): String = "/" + raw.trim('/')

    private fun resolvedPath(rawHref: String): String {
        val safe = rawHref.replace(" ", "%20")
        val path = runCatching { URI(safe).rawPath ?: safe }.getOrDefault(safe)
        val decoded = runCatching { URLDecoder.decode(path.replace("+", "%2B"), "UTF-8") }.getOrDefault(path)
        return normalizePath(decoded)
    }

    /** The file names and tags in a PROPFIND answer. */
    private fun parse(input: java.io.InputStream, basePath: String): Map<String, String> {
        val out = mutableMapOf<String, String>()
        val p = Xml.newPullParser()
        p.setFeature(XmlPullParser.FEATURE_PROCESS_NAMESPACES, true)
        p.setInput(input, null)
        var href = ""
        var tag = ""
        var modified = ""
        var length = ""
        var isCollection = false
        var text = StringBuilder()
        while (p.next() != XmlPullParser.END_DOCUMENT) {
            when (p.eventType) {
                XmlPullParser.START_TAG -> {
                    text = StringBuilder()
                    when (p.name) {
                        "response" -> {
                            href = ""
                            tag = ""
                            modified = ""
                            length = ""
                            isCollection = false
                        }
                        "collection" -> isCollection = true
                    }
                }
                XmlPullParser.TEXT -> text.append(p.text)
                XmlPullParser.END_TAG -> {
                    val v = text.toString().trim()
                    when (p.name) {
                        "href" -> href = v
                        "getetag" -> tag = v
                        "getlastmodified" -> modified = v
                        "getcontentlength" -> length = v
                        "response" -> {
                            if (!isCollection && href.isNotEmpty() && !href.endsWith("/")) {
                                val itemPath = resolvedPath(href)
                                if (itemPath != basePath) {
                                    val file = itemPath.substringAfterLast('/')
                                    if (file.isNotEmpty()) out[file] = "$tag|$modified|$length"
                                }
                            }
                        }
                    }
                }
            }
        }
        return out
    }

    companion object {
        private val http = OkHttpClient.Builder().connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build()
        /** 书文件 take a while. */
        private val slow = http.newBuilder().readTimeout(10, TimeUnit.MINUTES).writeTimeout(10, TimeUnit.MINUTES).build()
    }
}
