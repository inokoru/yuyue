package app.yuyue.notes

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import app.yuyue.design.YYField
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.rememberPref
import app.yuyue.me.Secret
import app.yuyue.shelf.Field
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.HorizontalDivider
import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalContext
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.shelf.Item
import uniffi.yuyue_core.BookFormat
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.documentfile.provider.DocumentFile
import app.yuyue.design.Prefs
import app.yuyue.library.Keys
import app.yuyue.library.Library
import app.yuyue.library.author
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.Mark
import java.io.File
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * 笔记导出 beyond Markdown, as on Apple: an Obsidian vault (one note per book), Notion (one page
 * per book under a page the reader picks), flomo or 新枝 (one memo per highlight), and the book
 * itself as an EPUB with its highlights drawn in. Nothing goes through a server of ours: Notion,
 * flomo, and 新枝 get the reader's own token, kept encrypted on this device.
 *
 * Notion, flomo, and 新枝 only get what they haven't had yet, so sending again after reading on
 * just adds the new highlights. Obsidian notes are written whole each time.
 */
object NoteExport {
    enum class Target(val key: String, private val rawTitle: String) {
        Obsidian("obsidian", "Obsidian"), Notion("notion", "Notion"), Flomo("flomo", "flomo"), Xinzhi("xinzhi", "新枝");

        val title: String get() = if (this == Xinzhi) t("新枝") else rawTitle
    }

    class Failure(message: String) : Exception(message)

    const val FLOMO = "export.flomo.url"
    const val XINZHI = "export.xinzhi.token"
    const val NOTION = "export.notion.token"
    const val NOTION_PAGE = "export.notion.page"
    const val OBSIDIAN = "export.obsidian.tree"

    fun isSetUp(t: Target): Boolean = when (t) {
        Target.Obsidian -> Prefs.string(OBSIDIAN).isNotEmpty()
        Target.Notion -> !Keys.read(NOTION).isNullOrEmpty() && notionPage != null
        Target.Flomo -> !Keys.read(FLOMO).isNullOrEmpty()
        Target.Xinzhi -> !Keys.read(XINZHI).isNullOrEmpty()
    }

    const val NOTION_TITLE = "export.notion.title"
    const val XINZHI_NAME = "export.xinzhi.name"

    /** On: it shows in 发到. A target that's connected is on until switched off, as on Apple. */
    fun isOn(t: Target): Boolean = Prefs.bool("export.${t.key}.on", true)
    fun setOn(t: Target, on: Boolean) = Prefs.set("export.${t.key}.on", on)
    /** What 发到 offers: connected and on. */
    val offered: List<Target> get() = Target.entries.filter { isSetUp(it) && isOn(it) }

    /** 断开: forgets where it writes and its token; what was sent stays there. */
    fun disconnect(t: Target) {
        when (t) {
            Target.Obsidian -> Prefs.set(OBSIDIAN, null)
            Target.Notion -> { Keys.delete(NOTION); Prefs.set(NOTION_PAGE, null); Prefs.set(NOTION_TITLE, null) }
            Target.Flomo -> Keys.delete(FLOMO)
            Target.Xinzhi -> { Keys.delete(XINZHI); Prefs.set(XINZHI_NAME, null) }
        }
    }

    fun isNotionToken(text: String): Boolean = text.trim().let { (it.startsWith("ntn_") || it.startsWith("secret_")) && it.length > 20 }
    fun isFlomoLink(text: String): Boolean = text.trim().startsWith("https://flomoapp.com/iwh/") && text.trim().length > 30

    /** Asks Notion who the token is and whether it can see the page: (the integration, the page's title). */
    suspend fun checkNotion(token: String, page: String): Pair<String, String> = withContext(Dispatchers.IO) {
        fun get(path: String): JSONObject {
            val r = Request.Builder().url("https://api.notion.com/v1/$path").get()
                .header("Authorization", "Bearer $token").header("Notion-Version", "2022-06-28").build()
            http.newCall(r).execute().use { response ->
                val out = runCatching { JSONObject(response.body?.string() ?: "") }.getOrDefault(JSONObject())
                when (response.code) {
                    in 200..299 -> return out
                    401 -> throw Failure(t("令牌不对：拷集成页上“内部集成令牌”那一串"))
                    404 -> throw Failure(t("Notion 页面还没共享给语阅：在那个页面右上角 ⋯ › 连接，选你建的集成"))
                    else -> throw Failure(out.optString("message").ifEmpty { null } ?: t("%@ 回了 %lld", "Notion", response.code))
                }
            }
        }
        val bot = get("users/me").optString("name").ifEmpty { t("语阅") }
        val props = get("pages/$page").optJSONObject("properties")
        var title = ""
        props?.keys()?.forEach { k ->
            val p = props.optJSONObject(k)
            if (p?.optString("type") == "title") {
                val a = p.optJSONArray("title")
                if (a != null) for (i in 0 until a.length()) title += a.getJSONObject(i).optString("plain_text")
            }
        }
        bot to title.ifEmpty { t("那一页") }
    }

    /** The Notion page id from what the reader pasted: a link or the id itself. */
    fun pageID(text: String): String? {
        val hex = text.lowercase().substringBefore('?')
        return Regex("""[0-9a-f]{8}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{4}-?[0-9a-f]{12}|[0-9a-f]{32}""").findAll(hex).lastOrNull()?.value?.replace("-", "")
    }

    val notionPage: String? get() = pageID(Prefs.string(NOTION_PAGE))

    private val http = OkHttpClient.Builder().connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build()
    private val json = "application/json".toMediaType()

    /** Sends the books' highlights; the count that went this time. */
    suspend fun send(context: Context, books: List<NotedBook>, target: Target): Int = withContext(Dispatchers.IO) {
        when (target) {
            Target.Obsidian -> obsidian(context, books)
            Target.Notion -> notion(books)
            Target.Flomo -> flomo(books)
            Target.Xinzhi -> xinzhi(books)
        }
    }

    // MARK: Obsidian

    /** One Markdown file per book, `<title>.md`, with properties Obsidian reads. */
    private fun obsidian(context: Context, books: List<NotedBook>): Int {
        val tree = Prefs.string(OBSIDIAN).ifEmpty { throw Failure(t("还没设置 %@：在设置 · 数据 · 笔记导出里填好", "Obsidian")) }
        val folder = DocumentFile.fromTreeUri(context, Uri.parse(tree)) ?: throw Failure(t("还没设置 %@：在设置 · 数据 · 笔记导出里填好", "Obsidian"))
        for (b in books) {
            val name = b.book.title.replace(Regex("""[/\\:*?"<>|#^\[\]]"""), "-") + ".md"
            val file = folder.findFile(name) ?: folder.createFile("text/markdown", name) ?: continue
            context.contentResolver.openOutputStream(file.uri, "wt")?.use { it.write(obsidianNote(b).toByteArray()) }
        }
        return books.sumOf { it.marks.size }
    }

    fun obsidianNote(b: NotedBook): String = buildString {
        fun yaml(s: String) = "\"" + s.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
        append("---\ntitle: ${yaml(b.book.title)}\n")
        b.book.author?.let { append("author: ${yaml(it)}\n") }
        append("source: 语阅\ntags:\n  - 语阅\nupdated: ${Instant.now()}\n---\n\n# ${b.book.title}\n")
        for ((chapter, marks) in b.chapters()) {
            append("\n## $chapter\n")
            for (m in marks) {
                // Each highlight keeps a block id, so links to it survive the next export.
                append("\n> " + m.text.replace("\n", "\n> ") + " ^yy-${m.id.take(8)}\n")
                m.note?.let { append("\n$it\n") }
            }
        }
    }

    // MARK: flomo

    /** One memo per highlight, tagged #语阅 and the book. */
    private fun flomo(books: List<NotedBook>): Int {
        val url = Keys.read(FLOMO)?.trim()?.takeIf { it.startsWith("https://") } ?: throw Failure(t("还没设置 %@：在设置 · 数据 · 笔记导出里填好", "flomo"))
        val sent = Sent(Target.Flomo)
        var n = 0
        try {
            for (b in books) {
                val tag = "#语阅/" + b.book.title.replace(Regex("""[\s#/]+"""), "_")
                for (m in b.marks) {
                    if (sent.has(m)) continue
                    var content = m.text
                    m.note?.let { content += "\n\n$it" }
                    content += "\n\n" + t("——《%@》", b.book.title) + "${b.book.author?.let { " $it" } ?: ""}\n$tag"
                    http.newCall(Request.Builder().url(url).post(JSONObject().put("content", content).toString().toRequestBody(json)).build()).execute().use { r ->
                        val body = runCatching { JSONObject(r.body?.string() ?: "") }.getOrNull()
                        // flomo answers 200 with {"code": 0} when it took the memo.
                        if (body?.optInt("code", -1) != 0) {
                            val message = body?.optString("message")?.ifEmpty { null }
                            throw Failure(when {
                                r.code == 401 || r.code == 403 || message?.contains("token") == true -> t("%@ 的令牌不对，去设置 · 数据 · 笔记导出里重新填", "flomo")
                                r.code == 429 || message?.contains("次") == true -> t("%@ 说今天发得太多了，明天再发剩下的", "flomo")
                                message != null -> t("%@ 回了 %lld：%@", "flomo", r.code, message)
                                else -> t("%@ 回了 %lld", "flomo", r.code)
                            })
                        }
                    }
                    sent.add(m)
                    n++
                }
            }
        } finally {
            sent.save()
        }
        return n
    }

    // MARK: 新枝

    /** One memo per highlight, tagged 语阅/书名. No container, so it lands in the account's default place. */
    private fun xinzhi(books: List<NotedBook>): Int {
        val token = Keys.read(XINZHI)?.trim()?.takeIf { it.isNotEmpty() } ?: throw Failure(t("还没设置 %@：在设置 · 数据 · 笔记导出里填好", "新枝"))
        val sent = Sent(Target.Xinzhi)
        var n = 0
        try {
            for (b in books) {
                val book = b.book.title.replace(Regex("""[\s#/]+"""), "_")
                val tag = if (book.isEmpty()) "语阅" else "语阅/$book"
                for (m in b.marks) {
                    if (sent.has(m)) continue
                    var content = m.text
                    m.note?.let { content += "\n\n$it" }
                    content += "\n\n" + t("——《%@》", b.book.title) + "${b.book.author?.let { " $it" } ?: ""}"
                    val body = JSONObject().put("content", content).put("tagNames", JSONArray().put(tag))
                    xinzhiCall("/cli/note/create-memo", token, body)
                    sent.add(m)
                    n++
                }
            }
        } finally {
            sent.save()
        }
        return n
    }

    /** A 新枝 CLI AccessToken: no published shape, so anything long that isn't a link or another service's token. */
    fun isXinzhiToken(text: String): Boolean {
        val t = text.trim()
        return t.length >= 8 && t.none { it.isWhitespace() } && !t.lowercase().startsWith("http") &&
            !t.startsWith("ntn_") && !t.startsWith("secret_")
    }

    /** Asks 新枝 who this token is, as the Mac does before keeping it. Writes no memo. */
    suspend fun checkXinzhi(token: String): String = withContext(Dispatchers.IO) {
        val me = xinzhiCall("/cli/user/me", token.trim(), null)
        me?.optString("name")?.trim()?.ifEmpty { null } ?: me?.optString("nickname")?.trim()?.ifEmpty { null } ?: t("你的新枝")
    }

    /** `https://api.xinzhi.zone/api`: `{success, data, msg}`; HTTP not-ok or `success: false` is a failure. GET without a body. */
    private fun xinzhiCall(path: String, token: String, body: JSONObject?): JSONObject? {
        val builder = Request.Builder().url("https://api.xinzhi.zone/api$path")
            .header("X-CLI-Token", token).header("x-client", "CLI").header("x-request-source", "xinzhi-cli")
        val request = (if (body != null) builder.post(body.toString().toRequestBody(json)) else builder.get()).build()
        http.newCall(request).execute().use { r ->
            val parsed = runCatching { JSONObject(r.body?.string() ?: "") }.getOrNull()
            if (r.isSuccessful && parsed?.optBoolean("success") == true) return parsed.optJSONObject("data")
            val message = parsed?.optString("msg")?.trim()?.ifEmpty { null }
            throw Failure(when {
                r.code == 401 || r.code == 403 || message?.contains("token", ignoreCase = true) == true || message?.contains("令牌") == true || message?.contains("登录") == true ->
                    t("%@ 的令牌不对，去设置 · 数据 · 笔记导出里重新填", "新枝")
                r.code == 429 || message?.contains("太多") == true || message?.contains("频繁") == true ->
                    t("%@ 说今天发得太多了，明天再发剩下的", "新枝")
                message != null -> t("%@ 回了 %lld：%@", "新枝", r.code, message)
                else -> t("%@ 回了 %lld", "新枝", r.code)
            })
        }
    }

    // MARK: Notion

    /** A page per book under the page the reader chose; later sends append the new highlights. */
    private fun notion(books: List<NotedBook>): Int {
        val token = Keys.read(NOTION)?.ifEmpty { null }
        val parent = notionPage
        if (token == null || parent == null) throw Failure(t("还没设置 %@：在设置 · 数据 · 笔记导出里填好", "Notion"))
        val sent = Sent(Target.Notion)
        val pages = JSONObject(Prefs.string("export.notion.pages", "{}"))
        var n = 0
        try {
            for (b in books) {
                val fresh = b.chapters().map { (c, ms) -> c to ms.filter { !sent.has(it) } }.filter { it.second.isNotEmpty() }
                if (fresh.isEmpty()) continue
                val blocks = mutableListOf<JSONObject>()
                for ((chapter, marks) in fresh) {
                    blocks += block("heading_2", chapter)
                    for (m in marks) {
                        blocks += block("quote", m.text)
                        m.note?.let { blocks += block("paragraph", it) }
                    }
                }
                var page = pages.optString(b.id).ifEmpty { null }
                if (page == null) {
                    val props = JSONObject().put("title", JSONObject().put("title", JSONArray().put(JSONObject().put("text", JSONObject().put("content", b.book.title.take(2000))))))
                    val made = call("pages", "POST", token, JSONObject().put("parent", JSONObject().put("page_id", parent)).put("properties", props).put("children", JSONArray(blocks.take(100))), first = true)
                    page = made.optString("id")
                    pages.put(b.id, page)
                    repeat(minOf(100, blocks.size)) { blocks.removeAt(0) }
                }
                // Notion takes at most 100 blocks a call.
                while (blocks.isNotEmpty()) {
                    val chunk = blocks.take(100)
                    try {
                        call("blocks/$page/children", "PATCH", token, JSONObject().put("children", JSONArray(chunk)))
                    } catch (e: Failure) {
                        if (e.message?.contains("404") == true) {
                            // The page was deleted in Notion: start a new one next time.
                            pages.remove(b.id)
                            throw Failure(t("《%@》的页面在 Notion 里删掉了，再发一次会新建", b.book.title))
                        }
                        throw e
                    }
                    repeat(chunk.size) { blocks.removeAt(0) }
                }
                for ((_, ms) in fresh) { ms.forEach(sent::add); n += ms.size }
            }
        } finally {
            sent.save()
            Prefs.set("export.notion.pages", pages.toString())
        }
        return n
    }

    private fun block(type: String, text: String): JSONObject {
        // A rich text piece holds at most 2,000 characters.
        val pieces = JSONArray()
        text.chunked(2000).forEach { pieces.put(JSONObject().put("type", "text").put("text", JSONObject().put("content", it))) }
        return JSONObject().put("object", "block").put("type", type).put(type, JSONObject().put("rich_text", pieces))
    }

    private fun call(path: String, method: String, token: String, body: JSONObject, first: Boolean = false): JSONObject {
        val r = Request.Builder().url("https://api.notion.com/v1/$path").method(method, body.toString().toRequestBody(json))
            .header("Authorization", "Bearer $token").header("Notion-Version", "2022-06-28").build()
        http.newCall(r).execute().use { response ->
            val out = runCatching { JSONObject(response.body?.string() ?: "") }.getOrDefault(JSONObject())
            return when (response.code) {
                in 200..299 -> out
                401 -> throw Failure(t("%@ 的令牌不对，去设置 · 数据 · 笔记导出里重新填", "Notion"))
                404 -> if (first) throw Failure(t("Notion 页面还没共享给语阅：在那个页面右上角 ⋯ › 连接，选你建的集成")) else throw Failure(t("%@ 回了 %lld", "Notion", 404))
                429 -> throw Failure(t("%@ 说今天发得太多了，明天再发剩下的", "Notion"))
                else -> throw Failure(out.optString("message").ifEmpty { null }?.let { t("%@ 回了 %lld：%@", "Notion", response.code, it) } ?: t("%@ 回了 %lld", "Notion", response.code))
            }
        }
    }

    /** Which highlights a target already has, by id; one whose thought changed counts as new. */
    private class Sent(private val target: Target) {
        private val ids = JSONObject(Prefs.string("export.${target.key}.sent", "{}"))
        /** FNV-1a of the words and the thought, the same as Apple's. */
        private fun stamp(m: Mark): Int {
            var h = 2166136261L.toInt()
            for (b in (m.text + "\u0001" + (m.note ?: "")).toByteArray()) h = (h xor (b.toInt() and 0xff)) * 16777619
            return h and 0x7fffffff
        }
        fun has(m: Mark) = ids.optInt(m.id, -1) == stamp(m)
        fun add(m: Mark) { ids.put(m.id, stamp(m)) }
        fun save() = Prefs.set("export.${target.key}.sent", ids.toString())
    }

    // MARK: Sharing

    /** Markdown, handed to the share sheet as a file. */
    fun shareMarkdown(context: Context, markdown: String, name: String) {
        val dir = File(context.cacheDir, "export").apply { mkdirs() }
        val file = File(dir, name.replace('/', '-'))
        file.writeText(markdown)
        share(context, file, "text/markdown")
    }

    /** The book as an EPUB with its highlights and thoughts, to share. */
    suspend fun epub(context: Context, library: Library, book: BookSummary): File = withContext(Dispatchers.IO) {
        val dir = File(context.cacheDir, "export/${UUID.randomUUID()}").apply { mkdirs() }
        val file = File(dir, t("%@（带笔记）.epub", book.title.replace(Regex("""[/\\:]"""), "-")))
        library.core.exportEpub(book.id, true, file.path)
        file
    }

    fun share(context: Context, file: File, type: String) {
        val uri = androidx.core.content.FileProvider.getUriForFile(context, context.packageName + ".files", file)
        context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType(type).putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), null).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}

/** 导出's work: what is being sent, told in the library's notes at the bottom. */
class NoteSender(private val context: Context, private val library: Library) {
    var sending by mutableStateOf<NoteExport.Target?>(null)
        private set
    var making by mutableStateOf(false)
        private set

    fun send(books: List<NotedBook>, target: NoteExport.Target) {
        if (!NoteExport.isSetUp(target)) {
            library.note(Library.Toast.Icon.Error, t("还没设置 %@", target.title), t("在设置 · 数据 · 笔记导出里填好"))
            return
        }
        sending = target
        library.scope.launch {
            try {
                val n = NoteExport.send(context, books, target)
                Prefs.set("export.${target.key}.last", System.currentTimeMillis())
                library.note(Library.Toast.Icon.Check, t("发到了 %@", target.title), if (n == 0) t("没有新的划线") else t("%lld 条划线", n))
            } catch (e: Exception) {
                library.note(Library.Toast.Icon.Error, t("没发出去"), e.message ?: "")
            }
            sending = null
        }
    }

    fun epub(book: BookSummary) {
        making = true
        library.scope.launch {
            try {
                NoteExport.share(context, NoteExport.epub(context, library, book), "application/epub+zip")
            } catch (e: Exception) {
                library.note(Library.Toast.Icon.Error, t("没导出来"), Library.message(e))
            }
            making = false
        }
    }
}

/** 导出 as a menu, as on iPhone: Markdown to share, then 发到 Obsidian / Notion / flomo / 新枝, and
 *  for one book EPUB（书和笔记一起）. */
@Composable
fun NoteExportMenu(library: Library, open: Boolean, dismiss: () -> Unit, markdown: () -> String, name: String, books: () -> List<NotedBook>, book: BookSummary? = null) {
    val context = LocalContext.current
    val sender = library.noteSender
    DropdownMenu(open, dismiss, containerColor = YY.c.card) {
        Item("Markdown", Icons.share) { dismiss(); NoteExport.shareMarkdown(context, markdown(), name) }
        HorizontalDivider(color = YY.c.line)
        // As on Apple: only the ones connected and on; none yet, all of them, each saying where to set it up.
        for (target in NoteExport.offered.ifEmpty { NoteExport.Target.entries }) {
            Item(t("发到 %@", target.title) + if (sender.sending == target) " …" else "", Icons.upload) {
                dismiss()
                if (sender.sending == null) sender.send(books(), target)
            }
        }
        if (book != null && book.format != BookFormat.PDF) {
            HorizontalDivider(color = YY.c.line)
            Item(t("EPUB（书和笔记一起）") + if (sender.making) " …" else "", Icons.book) {
                dismiss()
                if (!sender.making) sender.epub(book)
            }
        }
    }
}

/** The words under a target's name: where it writes, or that it isn't connected. */
fun exportWhere(context: Context, t: NoteExport.Target): String {
    if (!NoteExport.isSetUp(t)) return t("未连接")
    return when (t) {
        NoteExport.Target.Obsidian -> Prefs.string(NoteExport.OBSIDIAN).takeIf { it.isNotEmpty() }?.let { DocumentFile.fromTreeUri(context, Uri.parse(it))?.name } ?: t("选好的文件夹")
        NoteExport.Target.Notion -> Prefs.string(NoteExport.NOTION_TITLE).let { if (it.isEmpty()) t("每本书一页") else t("放在“%@”下面，每本书一页", it) }
        NoteExport.Target.Flomo -> t("每条划线一条笔记")
        NoteExport.Target.Xinzhi -> Prefs.string(NoteExport.XINZHI_NAME).let { if (it.isEmpty()) t("每条划线一条笔记") else t("发到“%@”", it) }
    }
}

/** A target's logo on its tile. */
@Composable
fun ExportTile(t: NoteExport.Target, size: Float = 26f) {
    app.yuyue.me.ServiceTile(size) {
        val res = when (t) {
            NoteExport.Target.Obsidian -> app.yuyue.R.drawable.brand_obsidian
            NoteExport.Target.Notion -> app.yuyue.R.drawable.brand_notion
            NoteExport.Target.Flomo -> app.yuyue.R.drawable.brand_flomo
            NoteExport.Target.Xinzhi -> app.yuyue.R.drawable.brand_xinzhi
        }
        androidx.compose.foundation.Image(androidx.compose.ui.res.painterResource(res), null, modifier = androidx.compose.ui.Modifier.size(YY.s(size * 0.62f)),
            colorFilter = if (t == NoteExport.Target.Notion) androidx.compose.ui.graphics.ColorFilter.tint(YY.c.t1) else null)
    }
}

/** 我的 › 笔记导出, as the Mac's: one list, Obsidian, Notion, flomo, 新枝, each with its logo,
 *  where it writes and a switch: on, it shows in 笔记 › 导出 › 发到. A target opens its page. */
@Composable
fun NoteExportPage(open: (NoteExport.Target) -> Unit) {
    val context = LocalContext.current
    // Read again when anything about the targets changes.
    var tick by remember { mutableStateOf(0) }
    androidx.compose.runtime.DisposableEffect(Unit) {
        val l = android.content.SharedPreferences.OnSharedPreferenceChangeListener { _, k -> if (k?.startsWith("export.") == true) tick++ }
        Prefs.shared.registerOnSharedPreferenceChangeListener(l)
        onDispose { Prefs.shared.unregisterOnSharedPreferenceChangeListener(l) }
    }
    YYPage {
        app.yuyue.me.SettingsLead(t("将划线与想法发送到常用笔记软件"))
        YYLabel(t("导出目标"))
        YYGroup {
            for (target in NoteExport.Target.entries) row {
                val ready = remember(tick) { NoteExport.isSetUp(target) }
                val on = remember(tick) { ready && NoteExport.isOn(target) }
                Row(Modifier.fillMaxWidth().padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    Row(Modifier.weight(1f).clickable { open(target) }, verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                        ExportTile(target)
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                            Text(target.title, style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                            Text(remember(tick) { exportWhere(context, target) }, style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                    // Not connected yet, the switch opens it instead.
                    app.yuyue.me.SettingSwitch(on, { v -> if (!ready) open(target) else { NoteExport.setOn(target, v); tick++ } })
                }
            }
        }
        YYNote(t("开启的项目会显示在笔记页「导出」菜单中。令牌与目录仅在本机加密保存，不同步、不上传。"))
    }
}

/** One target's page, as the Mac's right column: connecting it (the steps and the fields, tried as
 *  soon as they look right), or, connected, where it writes and 断开; then a sample of a book there. */
@Composable
fun NoteTargetPage(target: NoteExport.Target) {
    val context = LocalContext.current
    var tick by remember { mutableStateOf(0) }
    val ready = remember(tick) { NoteExport.isSetUp(target) }
    YYPage {
        if (ready) Connected(target) { tick++ } else when (target) {
            NoteExport.Target.Obsidian -> ObsidianSetup { tick++ }
            NoteExport.Target.Notion -> NotionSetup { tick++ }
            NoteExport.Target.Flomo -> FlomoSetup { tick++ }
            NoteExport.Target.Xinzhi -> XinzhiSetup { tick++ }
        }
        ExportSample(target)
    }
}

@Composable
private fun Connected(target: NoteExport.Target, changed: () -> Unit) {
    val context = LocalContext.current
    var leaving by remember { mutableStateOf(false) }
    val picker = obsidianPicker(changed)
    app.yuyue.me.PanelHeader(target.title, t("已连上"), YY.c.ok) { ExportTile(target, 34f) }
    YYGroup {
        row {
            when (target) {
                NoteExport.Target.Obsidian -> YYRow(t("写入目录"), value = exportWhere(context, target), chevron = true) { picker.launch(null) }
                NoteExport.Target.Notion -> YYRow(t("目标页面"), value = Prefs.string(NoteExport.NOTION_TITLE).ifEmpty { t("选好的那一页") })
                NoteExport.Target.Flomo -> YYRow(t("发到"), value = t("你的 flomo"))
                NoteExport.Target.Xinzhi -> YYRow(t("发到"), value = Prefs.string(NoteExport.XINZHI_NAME).ifEmpty { t("你的新枝") })
            }
        }
        row { YYRow(t("上次发送"), value = Prefs.long("export.${target.key}.last")?.let { app.yuyue.me.ago(it) } ?: t("尚未发送")) }
        when (target) {
            NoteExport.Target.Notion -> NoteExport.notionPage?.let { id -> row { app.yuyue.me.LinkRow(t("在 Notion 里打开那一页")) { app.yuyue.me.openLink(context, "https://www.notion.so/$id") } } }
            NoteExport.Target.Xinzhi -> row { app.yuyue.me.LinkRow(t("在新枝里打开")) { app.yuyue.me.openLink(context, "https://www.xinzhi.zone") } }
            NoteExport.Target.Obsidian -> row { app.yuyue.me.LinkRow(t("打开 Obsidian")) { app.yuyue.me.openLink(context, "obsidian://") } }
            NoteExport.Target.Flomo -> {}
        }
    }
    YYNote(when (target) {
        NoteExport.Target.Obsidian -> t("每本书一篇，文件名就是书名。再发一次整篇重写成最新的，所以自己的想法记在另一篇笔记里。")
        NoteExport.Target.Notion -> t("每本书一页，放在这一页下面；再发一次只补上新的划线。")
        NoteExport.Target.Flomo -> t("每条划线一条 memo，带 #语阅 和书名；再发一次只发新的。")
        NoteExport.Target.Xinzhi -> t("每条划线一条笔记，带标签 语阅 和书名；再发一次只发新的。")
    })
    app.yuyue.me.DestructiveCard(t("断开 %@", target.title)) { leaving = true }
    if (leaving) androidx.compose.material3.AlertDialog(
        onDismissRequest = { leaving = false },
        containerColor = YY.c.card,
        title = { Text(t("断开 %@？", target.title), color = YY.c.t1) },
        text = { Text(if (target == NoteExport.Target.Obsidian) t("已经写进去的笔记留着不动。") else t("令牌也一起删掉；已经发过去的留着不动。"), color = YY.c.t2) },
        confirmButton = { androidx.compose.material3.TextButton({ NoteExport.disconnect(target); leaving = false; changed() }) { Text(t("断开"), color = YY.c.danger) } },
        dismissButton = { androidx.compose.material3.TextButton({ leaving = false }) { Text(t("取消"), color = YY.c.t2) } },
    )
}

@Composable
private fun obsidianPicker(changed: () -> Unit) = run {
    val context = LocalContext.current
    rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        uri ?: return@rememberLauncherForActivityResult
        val flags = Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION
        runCatching { context.contentResolver.takePersistableUriPermission(uri, flags) }
        Prefs.set(NoteExport.OBSIDIAN, uri.toString())
        changed()
    }
}

/** Obsidian: a folder, the vault or one in it. Picking it is connecting. */
@Composable
private fun ObsidianSetup(changed: () -> Unit) {
    val picker = obsidianPicker(changed)
    app.yuyue.me.PanelHeader(t("连接 Obsidian"), t("选择笔记库中的文件夹")) { ExportTile(NoteExport.Target.Obsidian, 34f) }
    YYGroup { row { YYRow(t("选一个文件夹…"), Icons.folder, chevron = true) { picker.launch(null) } } }
    YYNote(t("选 Obsidian 库，或库里的一个文件夹。每本书写成一篇 Markdown，文件名就是书名，带作者和标签。"))
}

/** Notion: an integration's token and the page the books go under. Both look right → ask Notion. */
@Composable
private fun NotionSetup(changed: () -> Unit) {
    val context = LocalContext.current
    var token by remember { mutableStateOf("") }
    var page by remember { mutableStateOf("") }
    var opened by remember { mutableStateOf(false) }
    var items by remember { mutableStateOf(listOf<String>()) }
    var next by remember { mutableStateOf<String?>(null) }
    var failure by remember { mutableStateOf<String?>(null) }
    var done by remember { mutableStateOf(false) }
    LaunchedEffect(token, page) {
        items = emptyList(); next = null; failure = null; done = false
        val tok = token.trim()
        val id = NoteExport.pageID(page)
        if (tok.length > 6 && !NoteExport.isNotionToken(tok)) { failure = t("令牌应该以 ntn_ 开头"); return@LaunchedEffect }
        if (page.length > 8 && id == null) { failure = t("这不像一个 Notion 页面链接"); return@LaunchedEffect }
        if (!NoteExport.isNotionToken(tok) || id == null) return@LaunchedEffect
        delay(600)
        next = t("正在验证令牌…")
        try {
            val (bot, title) = NoteExport.checkNotion(tok, id)
            next = null
            items = listOf(t("令牌有效 · 集成“%@”", bot), t("找到那一页：%@", title))
            done = true
            delay(900)
            Keys.write(NoteExport.NOTION, tok)
            Prefs.set(NoteExport.NOTION_PAGE, page.trim())
            Prefs.set(NoteExport.NOTION_TITLE, title)
            changed()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            next = null
            failure = e.message ?: t("没有联网")
        }
    }
    app.yuyue.me.PanelHeader(t("连接 Notion"), t("填好后自动验证连接")) { ExportTile(NoteExport.Target.Notion, 34f) }
    YYGroup {
        row { app.yuyue.me.StepRow(1, opened || token.isNotEmpty(), t("建一个集成，拷它的令牌"), t("notion.so/my-integrations › 新建，名字叫“语阅”")) { opened = true; app.yuyue.me.openLink(context, "https://www.notion.so/my-integrations") } }
        row { app.yuyue.me.StepRow(2, done, t("把放书的那一页连上它"), t("那一页右上角 ··· › 连接 › 选“语阅”，再拷这一页的链接")) }
    }
    Spacer(Modifier.height(YY.s(10)))
    YYGroup {
        row { YYField(t("令牌"), 58f) { Secret(token, "ntn_…") { token = it } } }
        row { YYField(t("放书的页"), 58f) { Field(page, "notion.so/…") { page = it } } }
    }
    app.yuyue.me.ChecksCard(items, next, failure, done)
}

/** flomo: its API link. Only its shape is checked: a test memo would land in the reader's flomo. */
@Composable
private fun FlomoSetup(changed: () -> Unit) {
    val context = LocalContext.current
    var link by remember { mutableStateOf("") }
    var opened by remember { mutableStateOf(false) }
    var failure by remember { mutableStateOf<String?>(null) }
    var done by remember { mutableStateOf(false) }
    LaunchedEffect(link) {
        done = false
        val l = link.trim()
        failure = if (l.length > 12 && !NoteExport.isFlomoLink(l)) t("链接要以 https://flomoapp.com/iwh/ 开头") else null
        if (!NoteExport.isFlomoLink(l)) return@LaunchedEffect
        delay(500)
        done = true
        Keys.write(NoteExport.FLOMO, l)
        delay(1100)
        changed()
    }
    app.yuyue.me.PanelHeader(t("连接 flomo"), t("贴一条链接")) { ExportTile(NoteExport.Target.Flomo, 34f) }
    YYGroup {
        row { app.yuyue.me.StepRow(1, opened || link.isNotEmpty(), t("打开 flomo 的 API 页"), t("flomo › 设置 › API（要 PRO），拷那条链接")) { opened = true; app.yuyue.me.openLink(context, "https://v.flomoapp.com/mine?source=incoming_webhook") } }
        row { app.yuyue.me.StepRow(2, done, t("贴到下面"), t("只存在这台设备上，加密保存")) }
    }
    Spacer(Modifier.height(YY.s(10)))
    YYGroup { row { YYField(t("API 链接"), 58f) { Secret(link, "https://flomoapp.com/iwh/…") { link = it } } } }
    app.yuyue.me.ChecksCard(if (done) listOf(t("链接的样子对")) else emptyList(), null, failure, done, t("存好了 · 第一次发送时才真正试"))
}

/** 新枝: a CLI AccessToken, asked who it is without writing a memo. */
@Composable
private fun XinzhiSetup(changed: () -> Unit) {
    val context = LocalContext.current
    var token by remember { mutableStateOf("") }
    var opened by remember { mutableStateOf(false) }
    var items by remember { mutableStateOf(listOf<String>()) }
    var next by remember { mutableStateOf<String?>(null) }
    var failure by remember { mutableStateOf<String?>(null) }
    var done by remember { mutableStateOf(false) }
    LaunchedEffect(token) {
        items = emptyList(); next = null; failure = null; done = false
        val tok = token.trim()
        if (tok.length > 4 && !NoteExport.isXinzhiToken(tok)) { failure = t("这不像一个新枝令牌"); return@LaunchedEffect }
        if (!NoteExport.isXinzhiToken(tok)) return@LaunchedEffect
        delay(600)
        next = t("正在验证令牌…")
        try {
            val name = NoteExport.checkXinzhi(tok)
            next = null
            items = listOf(t("令牌有效 · %@", name))
            done = true
            delay(900)
            Keys.write(NoteExport.XINZHI, tok)
            Prefs.set(NoteExport.XINZHI_NAME, name)
            changed()
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            next = null
            failure = e.message ?: t("没有联网")
        }
    }
    app.yuyue.me.PanelHeader(t("连接新枝"), t("贴一条令牌")) { ExportTile(NoteExport.Target.Xinzhi, 34f) }
    YYGroup {
        row { app.yuyue.me.StepRow(1, opened || token.isNotEmpty(), t("拷 CLI AccessToken"), t("新枝网页 › 设置 › AI Agent › CLI AccessToken")) { opened = true; app.yuyue.me.openLink(context, "https://www.xinzhi.zone") } }
        row { app.yuyue.me.StepRow(2, done, t("贴到下面"), t("只存在这台设备上，加密保存")) }
    }
    Spacer(Modifier.height(YY.s(10)))
    YYGroup { row { YYField(t("令牌"), 58f) { Secret(token, "CLI AccessToken") { token = it } } } }
    app.yuyue.me.ChecksCard(items, next, failure, done)
}

/** 样张: how a book looks there, from two highlights and a thought in 《故乡》. */
@Composable
private fun ExportSample(target: NoteExport.Target) {
    Row(Modifier.fillMaxWidth().padding(start = YY.s(4), end = YY.s(4), top = YY.s(22), bottom = YY.s(8))) {
        Text(t("样张"), style = YY.font(12.5f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.weight(1f).alignByBaseline())
        Text(t("发到 %@ 是这样", target.title), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.alignByBaseline())
    }
    val quotes = listOf(t("我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"), t("希望本是无所谓有，无所谓无的。"))
    val serif = androidx.compose.ui.text.font.FontFamily.Serif
    Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(YY.s(12))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12))).padding(YY.s(14)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
        when (target) {
            NoteExport.Target.Obsidian, NoteExport.Target.Notion -> {
                val accent = if (target == NoteExport.Target.Obsidian) Color(0xFF8B5CF6) else YY.c.t3
                if (target == NoteExport.Target.Obsidian) Row {
                    Text(t("读书笔记 / 故乡.md"), style = YY.font(10f).copy(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace), color = YY.c.t3, modifier = Modifier.weight(1f))
                    Text("Markdown", style = YY.font(10f), color = YY.c.t3)
                }
                Text(t("故乡"), style = YY.font(15f, FontWeight.SemiBold), color = YY.c.t1)
                Text(t("鲁迅 · 《呐喊》"), style = YY.font(10.5f), color = YY.c.t3)
                for (q in quotes) Row(Modifier.height(androidx.compose.foundation.layout.IntrinsicSize.Min)) {
                    Box(Modifier.width(3.dp).fillMaxHeight().background(accent, RoundedCornerShape(2.dp)))
                    Text(q, fontFamily = serif, fontSize = YY.sp(12.5f), color = YY.c.t1, modifier = Modifier.padding(start = YY.s(10)))
                }
                Text(t("这句是全篇的落点"), style = YY.font(11f), color = YY.c.t2)
                if (target == NoteExport.Target.Obsidian) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                    for (tag in listOf(t("#语阅"), t("#鲁迅"))) Text(tag, style = YY.font(10.5f, FontWeight.Medium), color = accent,
                        modifier = Modifier.background(accent.copy(alpha = 0.1f), RoundedCornerShape(50)).padding(horizontal = YY.s(7), vertical = YY.s(2)))
                }
            }
            else -> {
                Text("2026-11-05 10:12", style = YY.font(10.5f), color = YY.c.t3)
                Text(quotes[1], fontFamily = serif, fontSize = YY.sp(12.5f), color = YY.c.t1)
                Text(t("这句是全篇的落点"), style = YY.font(11f), color = YY.c.t2)
                Text(if (target == NoteExport.Target.Flomo) t("#语阅 #故乡") else t("语阅 · 故乡"), style = YY.font(11f), color = Color(0xFF30CF79))
            }
        }
    }
    Text(t("用《故乡》的两条划线和一条想法排的"), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.padding(start = YY.s(4), top = YY.s(6)))
}
