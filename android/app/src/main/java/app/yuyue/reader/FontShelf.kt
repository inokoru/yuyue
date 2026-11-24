package app.yuyue.reader

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.text.font.Font as ComposeFont
import androidx.compose.ui.text.font.FontFamily
import app.yuyue.YuyueApp
import app.yuyue.design.Prefs
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import uniffi.yuyue_core.Font
import uniffi.yuyue_core.FontKind
import uniffi.yuyue_core.FontPack
import uniffi.yuyue_core.ReaderSettings
import uniffi.yuyue_core.fontCatalog
import java.io.File
import java.util.concurrent.TimeUnit

/**
 * 字体: every face a book can be read in, by one id each, as on Apple.
 *
 * - `system`, `serif`: built in; Android has no 楷体 of its own, so 霞鹜文楷 (`wenkai`) takes that place;
 * - an id from the core's catalog (`wenkai`, `notoserif`, `notosans`): downloaded the first time it
 *   is chosen into the core's fonts folder, where the page loads it from;
 * - `file:<name>`: a TTF / OTF the reader added, copied into the same folder.
 */
object FontShelf {
    val builtIn = listOf("system", "serif", "wenkai")
    private var catalogLang: Boolean? = null
    private var cachedCatalog: List<FontPack> = emptyList()
    val catalog: List<FontPack>
        get() {
            val zh = runCatching { app.yuyue.L10n.chinese }.getOrDefault(true)
            if (catalogLang != zh) {
                runCatching { fontCatalog() }.onSuccess {
                    cachedCatalog = it
                    catalogLang = zh
                }
            }
            return cachedCatalog
        }

    /** 0–1 while a face downloads. */
    val progress = mutableStateMapOf<String, Float>()
    val failure = mutableStateMapOf<String, String>()
    /** Faces added from files, by file name. */
    val files = mutableStateListOf<String>()
    /** Bumped when a face arrives or goes, for whatever draws in it to look again. */
    var version by mutableStateOf(0)
        private set
    private val jobs = mutableMapOf<String, Job>()
    private val families = mutableMapOf<String, FontFamily>()

    private val dir: File get() = File(YuyueApp.instance.library.core.fontsDir())

    fun load() {
        val known = catalog.flatMap { p -> p.files.map { it.file } }.toSet()
        files.clear()
        files += (dir.list() ?: emptyArray()).filter { isFontFile(it) && it !in known }.sorted()
    }

    fun info(id: String): FontPack? = catalog.firstOrNull { it.id == id }

    fun name(id: String): String = when (id) {
        "system" -> t("系统")
        "serif" -> t("宋体")
        else -> info(id)?.name ?: id.removePrefix("file:").substringBeforeLast('.')
    }

    fun kind(id: String): FontKind = when (id) {
        "system" -> FontKind.HEI
        "serif" -> FontKind.SONG
        else -> info(id)?.kind ?: guessKind(id)
    }

    fun isReady(id: String): Boolean {
        if (id == "system" || id == "serif") return true
        if (id.startsWith("file:")) return id.removePrefix("file:") in files
        val pack = info(id) ?: return false
        return pack.files.all { File(dir, it.file).isFile }
    }

    /** The family to draw it in; the built-in face of its kind until it is here. */
    fun family(id: String): FontFamily {
        @Suppress("UNUSED_EXPRESSION") version
        if (!isReady(id)) return when (kind(id)) { FontKind.HEI -> FontFamily.SansSerif; else -> FontFamily.Serif }
        return when (id) {
            "system" -> FontFamily.SansSerif
            "serif" -> FontFamily.Serif
            else -> families.getOrPut(id) {
                val file = id.removePrefix("file:").takeIf { id.startsWith("file:") } ?: info(id)!!.files.first().file
                runCatching { FontFamily(ComposeFont(File(dir, file))) }.getOrDefault(FontFamily.Serif)
            }
        }
    }

    fun size(id: String): String? = info(id)?.let { "${(it.files.sumOf { f -> f.bytes.toLong() } / 1_000_000.0).let { mb -> Math.round(mb) }} MB" }

    // MARK: Choosing

    fun id(of: ReaderSettings): String = of.fontFace?.takeIf { it.isNotEmpty() } ?: when (of.font) {
        Font.SERIF -> "serif"
        Font.KAI, Font.WEN_KAI -> "wenkai"
        Font.SANS -> "system"
        null -> {
            val preset = PaperAppearance.resolve(of).preset
            if (preset == uniffi.yuyue_core.Paper.PLAIN || preset == uniffi.yuyue_core.Paper.DARK) "system" else "serif"
        }
    }

    /** The settings with this face: `font` is the built-in face it reads like, so a device without it
     *  shows something close. 霞鹜文楷 is `WEN_KAI`, as before. */
    fun apply(id: String, to: ReaderSettings): ReaderSettings = when (id) {
        "system" -> to.copy(font = Font.SANS, fontFace = null)
        "serif" -> to.copy(font = Font.SERIF, fontFace = null)
        "wenkai" -> to.copy(font = Font.WEN_KAI, fontFace = null)
        else -> to.copy(fontFace = id, font = when (kind(id)) { FontKind.HEI -> Font.SANS; FontKind.KAI, FontKind.HAND -> Font.WEN_KAI; else -> Font.SERIF })
    }

    // MARK: The row of tiles

    private const val ROW = "fonts.row"
    /** Bumped when the row of tiles changes, so 设置 › 阅读 and Aa stay in sync. */
    var rowVersion by mutableStateOf(0)
        private set
    fun row(): List<String> = Prefs.string(ROW, "").split('\n').filter { it.isNotEmpty() }
    fun addToRow(id: String) {
        if (id in builtIn) return
        val all = row()
        if (id !in all) {
            Prefs.set(ROW, (all + id).joinToString("\n"))
            rowVersion++
        }
    }
    fun removeFromRow(id: String) {
        Prefs.set(ROW, row().filter { it != id }.joinToString("\n"))
        rowVersion++
    }

    // MARK: Downloading

    fun ensure(id: String) {
        val pack = info(id) ?: return
        if (isReady(id) || jobs[id] != null) return
        failure.remove(id)
        progress[id] = 0f
        val total = pack.files.sumOf { it.bytes.toLong() }.coerceAtLeast(1)
        jobs[id] = YuyueApp.instance.library.scope.launch {
            try {
                withContext(Dispatchers.IO) {
                    dir.mkdirs()
                    // Said before the first byte, not after 20 MB of it.
                    if (dir.usableSpace in 1 until total + 10_000_000L) throw java.io.IOException("ENOSPC")
                    var before = 0L
                    for (f in pack.files) {
                        val target = File(dir, f.file)
                        if (target.isFile) { before += f.bytes.toLong(); continue }
                        val temp = File(dir, ".${f.file}.part")
                        val urls = app.yuyue.speech.ModelDownload.candidateUrls(
                            asset = f.file,
                            officialUrl = f.url,
                            mirrorPath = "fonts/${f.file}",
                        )
                        app.yuyue.speech.ModelDownload.downloadWithResume(
                            urls = urls,
                            partFile = temp,
                            expectedBytes = f.bytes.toLong(),
                        ) { done, _ ->
                            if (jobs[id]?.isActive == false) throw kotlinx.coroutines.CancellationException()
                            progress[id] = ((before + done).toFloat() / total).coerceAtMost(0.99f)
                        }
                        if (!temp.renameTo(target)) {
                            temp.copyTo(target, overwrite = true)
                            temp.delete()
                        }
                        before += f.bytes.toLong()
                    }
                }
                version++
                if (id == "wenkai") WenKai.arrived()
            } catch (e: kotlinx.coroutines.CancellationException) {
                // 停下: what came in whole stays.
            } catch (e: Exception) {
                failure[id] = when {
                    Library.outOfSpace(e) -> t("存储空间不够")
                    e is java.net.UnknownHostException -> t("没有联网")
                    e is java.net.SocketTimeoutException || e is java.net.SocketException -> t("网断了，没下完")
                    else -> t("没下载下来")
                }
            }
            progress.remove(id)
            jobs.remove(id)
        }
    }

    fun cancel(id: String) {
        jobs.remove(id)?.cancel()
        progress.remove(id)
    }

    // MARK: Files

    /** Copies a TTF / OTF into the fonts folder. Its id, or null if it isn't one. */
    fun add(context: Context, uri: Uri): String? {
        val shown = context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        } ?: return null
        if (!isFontFile(shown)) return null
        val base = shown.substringBeforeLast('.').replace('/', '-')
        val ext = shown.substringAfterLast('.').lowercase()
        var name = "$base.$ext"
        var n = 2
        while (File(dir, name).exists()) { name = "$base $n.$ext"; n++ }
        val target = File(dir, name)
        return runCatching {
            dir.mkdirs()
            context.contentResolver.openInputStream(uri)!!.use { input -> target.outputStream().use { input.copyTo(it) } }
            // Not a font after all: gone again.
            runCatching { android.graphics.Typeface.createFromFile(target) }.getOrElse { target.delete(); return null }
            files += name
            files.sort()
            version++
            "file:$name"
        }.getOrNull()
    }

    /** Deletes a downloaded or added face's files. */
    fun delete(id: String) {
        val names = info(id)?.files?.map { it.file } ?: listOfNotNull(id.removePrefix("file:").takeIf { id.startsWith("file:") })
        for (n in names) File(dir, n).delete()
        files.remove(id.removePrefix("file:"))
        families.remove(id)
        removeFromRow(id)
        version++
        if (id == "wenkai") WenKai.arrived()
    }

    private fun isFontFile(name: String) = !name.startsWith(".") && name.substringAfterLast('.', "").lowercase() in setOf("ttf", "otf", "ttc")

    private fun guessKind(id: String): FontKind {
        val f = id.lowercase()
        return when {
            "kai" in f || "楷" in f -> FontKind.KAI
            "fang" in f || "仿" in f -> FontKind.FANG
            "sans" in f || "hei" in f || "黑" in f || "圆" in f -> FontKind.HEI
            else -> FontKind.SONG
        }
    }
}
