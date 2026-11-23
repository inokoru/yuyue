package app.yuyue.feedback

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.net.Uri
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.layout.width
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.yuyue.BuildConfig
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYSegments
import app.yuyue.t
import androidx.compose.runtime.mutableStateListOf
import androidx.exifinterface.media.ExifInterface
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.FeedbackDraft
import uniffi.yuyue_core.feedbackMessage
import uniffi.yuyue_core.sealFeedbackFile
import java.io.ByteArrayOutputStream
import java.util.concurrent.TimeUnit

/**
 * 反馈: the core turns what the form holds into one ntfy message (`feedbackMessage`) and it is
 * POSTed to ntfy.sh (docs/dev/feedback.md). 语阅 is open source: the words become a public GitHub
 * Issue, and so do the screenshots unless the person turns that off. The email, logs and other
 * files are sealed so only the developer reads them. Without a network the message waits on this
 * phone and goes on the next try. Same rules as the Apple app.
 */
object Feedback {
    enum class Category(val id: String) {
        Bug("bug"), Idea("idea"), Other("other");

        val title: String get() = when (this) { Bug -> t("问题"); Idea -> t("建议"); Other -> t("其他") }
        val placeholder: String get() = when (this) {
            Bug -> t("哪本书、哪一页，做了什么，结果怎样")
            Idea -> t("想要什么功能，或者哪里可以更顺手")
            Other -> t("想说的都可以")
        }
    }

    /** [QueuedBusy]: kept and sent later because ntfy said too many (HTTP 429), not because the network is gone. */
    enum class Outcome { Sent, Queued, QueuedBusy }

    class Problem(message: String) : Exception(message)

    /** HTTP 429: ntfy.sh's limit for this IP (shared by everyone behind the same carrier address). */
    class Busy : Exception("busy")

    /** The core's caps, counted in code points as Rust counts chars. */
    const val TEXT_MAX = 800
    const val CONTACT_MAX = 80
    const val FILES_MAX = 3
    const val FILE_BYTES_MAX = 1_500_000
    /** 早期版本: the entries wear the theme colour. Set to false once 语阅 settles. */
    const val EARLY = true

    private const val QUEUE_KEY = "feedback.queue"
    private val topic = BuildConfig.FEEDBACK_TOPIC.trim()
    private val publicKey = BuildConfig.FEEDBACK_PUBLIC_KEY.trim()
    private val http by lazy { OkHttpClient.Builder().callTimeout(15, TimeUnit.SECONDS).build() }
    private val slow by lazy { http.newBuilder().callTimeout(40, TimeUnit.SECONDS).build() }
    private val flushing = Mutex()

    /** Public builds carry neither; the form says so and does not send. */
    val isConfigured: Boolean get() = topic.isNotEmpty() && publicKey.isNotEmpty()

    val queued: List<String> get() = readList(QUEUE_KEY)

    /**
     * One attachment: a screenshot, a log, a book that won't open. A picture is shrunk first (at
     * most 1600 px on the long side, re-encoded as JPEG from the pixels, so what the original
     * carried — place, camera, time — is gone); anything else goes as it is, up to 1.5 MB.
     */
    class Attachment(val name: String, val data: ByteArray, val preview: ImageBitmap?) {
        /** A picture (shrunk to a JPEG here); only these may go public. */
        val isShot: Boolean get() = preview != null
    }

    /**
     * [withInfo] false: the person turned 附加信息 off; version and system stay here.
     * [publicShots]: screenshots go up as they are, for the public Issue; otherwise sealed like
     * every other file.
     *
     * Attachments go first, each its own ntfy attachment; the report then names them.
     * They need a network now: if one fails nothing is counted and the form keeps everything.
     * Only the words wait in the queue (ntfy keeps an attachment 3 hours, too short to hold).
     */
    suspend fun send(
        category: Category, text: String, contact: String, withInfo: Boolean = true, publicShots: Boolean = true,
        files: List<Attachment> = emptyList(),
    ): Outcome {
        if (!isConfigured) throw Problem(t("这个版本没有配置反馈地址，发不出去。"))
        val ids = files.take(FILES_MAX).mapIndexed { i, file ->
            val shown = publicShots && file.isShot
            val data = if (shown) file.data else sealFeedbackFile(publicKey, file.name, file.data)
            try { upload(data, shown, i + 1) } catch (_: Busy) {
                throw Problem(t("反馈服务现在太忙，附件没传上去。写的都还在，过一会儿再点发送。"))
            } catch (_: Exception) {
                throw Problem(t("附件没传上去，可能是没网。写的都还在，联网后再点发送。"))
            }
        }
        val body = feedbackMessage(publicKey, FeedbackDraft(
            category = category.id, text = text, contact = contact,
            appVersion = if (withInfo) appVersion else "",
            platform = if (withInfo) "android" else "",
            osVersion = if (withInfo) osVersion else "",
            files = ids,
        ))
        return try {
            post(body)
            flush()
            Outcome.Sent
        } catch (e: Exception) {
            // At most 10, the newest kept. The email in it is sealed.
            writeList(QUEUE_KEY, (queued + body).takeLast(10))
            if (e is Busy) Outcome.QueuedBusy else Outcome.Queued
        }
    }

    /** Sends what waited for a network, oldest first; stops at the first one that fails. */
    suspend fun flush() {
        if (!isConfigured || !flushing.tryLock()) return
        try {
            while (true) {
                val body = queued.firstOrNull() ?: return
                try { post(body) } catch (_: Exception) { return }
                writeList(QUEUE_KEY, queued.drop(1))
            }
        } finally {
            flushing.unlock()
        }
    }

    private suspend fun post(body: String) = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("https://ntfy.sh/$topic")
            .post(body.toRequestBody("text/plain".toMediaType()))
            // Not pushed on through Google's FCM: the relay polls, nobody needs the push.
            .header("X-Firebase", "no")
            .build()
        http.newCall(request).execute().use {
            if (it.code == 429) throw Busy()
            if (!it.isSuccessful) throw Problem("HTTP ${it.code}")
        }
    }

    /**
     * One attachment. [shown]: a screenshot for the public Issue, the plain JPEG, marked `v2shot`.
     * Otherwise a sealed box marked `v1file`, its name inside the box. Returns its ntfy id.
     */
    private suspend fun upload(body: ByteArray, shown: Boolean, n: Int): String = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("https://ntfy.sh/$topic")
            .put(body.toRequestBody((if (shown) "image/jpeg" else "application/octet-stream").toMediaType()))
            .header("X-Firebase", "no")
            .header("Filename", if (shown) "shot-$n.jpg" else "f.bin")
            .header("X-Message", if (shown) "v2shot" else "v1file")
            .build()
        slow.newCall(request).execute().use { reply ->
            if (reply.code == 429) throw Busy()
            if (!reply.isSuccessful) throw Problem("HTTP ${reply.code}")
            JSONObject(reply.body?.string().orEmpty()).optString("id").ifEmpty { throw Problem("no id") }
        }
    }

    /** A picked file → an [Attachment]. Pictures are shrunk; anything else must already fit. */
    suspend fun attachment(context: Context, uri: Uri): Attachment = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        val name = resolver.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: uri.lastPathSegment?.substringAfterLast('/') ?: "file"
        val type = resolver.getType(uri).orEmpty()
        if (type.startsWith("image/")) {
            shrink(context, uri)?.let { (jpeg, bitmap) ->
                return@withContext Attachment(name.substringBeforeLast('.') + ".jpg", jpeg, bitmap.asImageBitmap())
            }
        }
        // Read a little past the cap, so a big file says so instead of loading whole.
        val data = resolver.openInputStream(uri)?.use { it.readNBytesCompat(FILE_BYTES_MAX + 1) }
            ?: throw Problem(t("「%@」读不出来。", name))
        if (data.isEmpty()) throw Problem(t("「%@」读不出来。", name))
        if (data.size > FILE_BYTES_MAX) throw Problem(t("「%@」太大了，一个附件最多 1.5 MB。", name))
        Attachment(name, data, null)
    }

    private fun java.io.InputStream.readNBytesCompat(max: Int): ByteArray {
        val out = ByteArrayOutputStream()
        val buffer = ByteArray(64 * 1024)
        while (out.size() < max) {
            val n = read(buffer, 0, minOf(buffer.size, max - out.size()))
            if (n < 0) break
            out.write(buffer, 0, n)
        }
        return out.toByteArray()
    }

    /** An image → a JPEG under 1 MB and its bitmap, or null. Tries smaller and softer until it fits. */
    private fun shrink(context: Context, uri: Uri): Pair<ByteArray, Bitmap>? = runCatching {
        val resolver = context.contentResolver
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
        val long = maxOf(bounds.outWidth, bounds.outHeight)
        if (long <= 0) return@runCatching null
        var sample = 1
        while (long / (sample * 2) >= 1600) sample *= 2
        val decoded = resolver.openInputStream(uri)?.use {
            BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
        } ?: return@runCatching null
        val rotation = resolver.openInputStream(uri)?.use { ExifInterface(it).rotationDegrees } ?: 0
        val upright = if (rotation == 0) decoded
            else Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, Matrix().apply { postRotate(rotation.toFloat()) }, true)
        for ((side, quality) in listOf(1600 to 78, 1600 to 60, 1200 to 60, 900 to 50)) {
            val scale = minOf(1f, side.toFloat() / maxOf(upright.width, upright.height))
            val fitted = if (scale >= 1f) upright
                else Bitmap.createScaledBitmap(upright, (upright.width * scale).toInt().coerceAtLeast(1), (upright.height * scale).toInt().coerceAtLeast(1), true)
            val out = ByteArrayOutputStream()
            fitted.compress(Bitmap.CompressFormat.JPEG, quality, out)
            if (out.size() <= 1_000_000) return@runCatching out.toByteArray() to fitted
        }
        null
    }.getOrNull()

    // Draft and email

    private const val DRAFT_KEY = "feedback.draft"
    private const val EMAIL_KEY = "feedback.email"

    /** What the form held when it closed, so nothing written is lost. Cleared once sent (or queued). */
    class Draft(val category: Category, val text: String, val withInfo: Boolean, val publicShots: Boolean, val files: List<Attachment>)

    /** The email last typed, offered next time. Kept after sending. */
    var savedEmail: String
        get() = Prefs.string(EMAIL_KEY)
        set(value) = Prefs.set(EMAIL_KEY, value.trim().ifEmpty { null })

    private fun draftDir(context: Context) = java.io.File(context.filesDir, "feedback-draft")

    private fun draftJson(): JSONObject = runCatching { JSONObject(Prefs.string(DRAFT_KEY, "{}")) }.getOrDefault(JSONObject())

    private fun writeDraft(json: JSONObject) {
        val empty = json.optString("text").isEmpty() && (json.optJSONArray("files")?.length() ?: 0) == 0 &&
            json.optBoolean("withInfo", true) && json.optBoolean("publicShots", true) &&
            json.optString("category", Category.Bug.id) == Category.Bug.id
        Prefs.set(DRAFT_KEY, if (empty) null else json.toString())
    }

    fun saveDraft(category: Category, text: String, withInfo: Boolean, publicShots: Boolean) =
        writeDraft(draftJson().put("category", category.id).put("text", text).put("withInfo", withInfo).put("publicShots", publicShots))

    suspend fun saveDraftFiles(context: Context, files: List<Attachment>) = withContext(Dispatchers.IO) {
        val dir = draftDir(context)
        dir.deleteRecursively()
        val names = JSONArray()
        if (files.isNotEmpty()) {
            dir.mkdirs()
            files.forEachIndexed { i, file ->
                if (runCatching { java.io.File(dir, "$i").writeBytes(file.data) }.isSuccess) names.put(file.name)
            }
        }
        withContext(Dispatchers.Main) { writeDraft(draftJson().put("files", names)) }
    }

    suspend fun loadDraft(context: Context): Draft? = withContext(Dispatchers.IO) {
        val json = draftJson()
        if (json.length() == 0) return@withContext null
        val names = json.optJSONArray("files") ?: JSONArray()
        val files = (0 until names.length()).mapNotNull { i ->
            val data = runCatching { java.io.File(draftDir(context), "$i").readBytes() }.getOrNull() ?: return@mapNotNull null
            val name = names.optString(i)
            val preview = if (name.lowercase().endsWith(".jpg")) BitmapFactory.decodeByteArray(data, 0, data.size)?.asImageBitmap() else null
            Attachment(name, data, preview)
        }
        Draft(
            Category.entries.firstOrNull { it.id == json.optString("category") } ?: Category.Bug,
            json.optString("text"), json.optBoolean("withInfo", true), json.optBoolean("publicShots", true), files,
        )
    }

    fun clearDraft(context: Context) {
        Prefs.set(DRAFT_KEY, null)
        draftDir(context).deleteRecursively()
    }

    /** 附加信息 as the form lists it; the same values go in the report. More may join later. */
    val info: List<Pair<String, String>> get() = listOf(
        t("版本") to t("语阅 %@", appVersion),
        t("系统") to osVersion,
        t("设备") to listOf(Build.MANUFACTURER.replaceFirstChar { it.uppercase() }, Build.MODEL).distinct().joinToString(" "),
    )

    /** Empty is fine (no reply wanted); otherwise something@something.something. */
    fun isEmail(text: String): Boolean = text.isBlank() || Regex("[^@\\s]+@[^@\\s]+\\.[^@\\s]+").matches(text.trim())

    val appVersion: String get() = "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})"
    val osVersion: String get() = "Android ${Build.VERSION.RELEASE}"

    fun clip(text: String, max: Int): String =
        if (text.codePointCount(0, text.length) <= max) text else text.substring(0, text.offsetByCodePoints(0, max))

    fun length(text: String): Int = text.codePointCount(0, text.length)

    private const val DAY = 86_400_000L

    private fun readList(key: String): List<String> = runCatching {
        val array = JSONArray(Prefs.string(key, "[]"))
        List(array.length()) { array.getString(it) }
    }.getOrDefault(emptyList())

    private fun writeList(key: String, list: List<String>) = Prefs.set(key, JSONArray(list).toString())
}

/** The form: 类别, 想说的 (Markdown, with attachments), an optional 邮箱, 附加信息, then 发送. Its own page. */
@Composable
fun FeedbackPage(close: () -> Unit) {
    var category by remember { mutableStateOf(Feedback.Category.Bug) }
    var text by remember { mutableStateOf("") }
    var contact by remember { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    var done by remember { mutableStateOf<Boolean?>(null) }
    var notice by remember { mutableStateOf<String?>(null) }
    var withInfo by remember { mutableStateOf(true) }
    // Screenshots shown in the public Issue. On until the person turns it off; the card says what it means.
    var publicShots by remember { mutableStateOf(true) }
    var focused by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var restored by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    val files = remember { mutableStateListOf<Feedback.Attachment>() }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val blank = text.isBlank()
    val emailOK = Feedback.isEmail(contact)
    val canSend = !blank && emailOK && !sending && Feedback.isConfigured

    fun take(uris: List<Uri>) {
        scope.launch {
            for (uri in uris) {
                if (files.size >= Feedback.FILES_MAX) break
                try { files += Feedback.attachment(context, uri) } catch (e: Exception) {
                    notice = (e as? Feedback.Problem)?.message ?: t("读不出来。")
                }
            }
        }
    }
    val photos = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(Feedback.FILES_MAX)) { take(it) }
    val documents = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { take(it) }

    LaunchedEffect(Unit) { Feedback.flush() }
    // Back to where the person left off, then straight to typing.
    LaunchedEffect(Unit) {
        Feedback.loadDraft(context)?.let { draft ->
            category = draft.category
            text = draft.text
            withInfo = draft.withInfo
            publicShots = draft.publicShots
            files.addAll(draft.files)
        }
        contact = Feedback.savedEmail
        restored = true
        runCatching { focus.requestFocus() }
    }
    LaunchedEffect(category, text, withInfo, publicShots) {
        if (restored && done == null) Feedback.saveDraft(category, text, withInfo, publicShots)
    }
    LaunchedEffect(files.toList()) { if (restored) Feedback.saveDraftFiles(context, files.toList()) }

    done?.let { replied ->
        Thanks(replied, again = { text = ""; category = Feedback.Category.Bug; withInfo = true; publicShots = true; files.clear(); done = null }, close = close)
        return
    }

    Column(Modifier.fillMaxSize().background(YY.c.bg).imePadding()) {
        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = YY.s(16)).padding(top = YY.s(4), bottom = YY.s(4)),
            verticalArrangement = Arrangement.spacedBy(YY.s(10)),
        ) {
            Text(t("语阅还在早期，哪里不对、想要什么，直接写。"), style = YY.font(11.5f), color = YY.c.t2)
            if (!Feedback.isConfigured) {
                // Builds without a topic and key (see docs/dev/feedback.md) can't send; mail still works.
                androidx.compose.foundation.text.selection.SelectionContainer {
                    Text(
                        t("这个版本发不出反馈。可以发邮件到 %@。", "yuyue.app@icloud.com"), style = YY.font(11f), color = YY.c.t2,
                        modifier = Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(YY.s(12))).padding(YY.s(11)),
                    )
                }
            }
            YYSegments(Feedback.Category.entries.map { it to it.title }, category, { category = it }, modifier = Modifier.fillMaxWidth())
            // 想说的, with the attachments and the count inside the box.
            Column(
                Modifier.fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(YY.s(14)))
                    .border(if (focused) 1.dp else 0.5.dp, if (focused) YY.tint.edge else YY.c.line, RoundedCornerShape(YY.s(14)))
                    .padding(start = YY.s(12), end = YY.s(12), top = YY.s(11), bottom = YY.s(6)),
                verticalArrangement = Arrangement.spacedBy(YY.s(8)),
            ) {
                BasicTextField(
                    text, { text = Feedback.clip(it, Feedback.TEXT_MAX) },
                    textStyle = YY.font(12.5f).copy(color = YY.c.t1, lineHeight = YY.sp(20f)),
                    cursorBrush = SolidColor(YY.tint.accent),
                    decorationBox = { inner ->
                        if (text.isEmpty()) Text(category.placeholder, style = YY.font(12.5f), color = YY.c.t3)
                        inner()
                    },
                    modifier = Modifier.fillMaxWidth().heightIn(min = YY.s(120)).focusRequester(focus).onFocusChanged { focused = it.isFocused },
                )
                if (files.isNotEmpty()) {
                    Row(Modifier.padding(top = YY.s(5)), horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                        files.forEach { file ->
                            Box {
                                val tile = Modifier.size(YY.s(56)).clip(RoundedCornerShape(YY.s(9))).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(9)))
                                val preview = file.preview
                                if (preview != null) {
                                    Image(preview, file.name, contentScale = ContentScale.Crop, modifier = tile)
                                } else {
                                    Column(tile.background(YY.c.inset), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
                                        Icon(Icons.doc, file.name, tint = YY.c.t2, modifier = Modifier.size(YY.s(18)))
                                        Text(file.name.substringAfterLast('.', "").uppercase().take(5), style = YY.font(8f, FontWeight.SemiBold), color = YY.c.t2, maxLines = 1)
                                    }
                                }
                                Box(
                                    Modifier.align(Alignment.TopEnd).offset(x = YY.s(5), y = YY.s(-5)).size(YY.s(18))
                                        .background(Color.Black.copy(alpha = 0.55f), CircleShape)
                                        .clickable(onClickLabel = t("去掉「%@」", file.name)) { files.remove(file) },
                                    contentAlignment = Alignment.Center,
                                ) { Icon(Icons.close, t("去掉「%@」", file.name), tint = Color.White, modifier = Modifier.size(YY.s(10))) }
                            }
                        }
                    }
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    if (files.size < Feedback.FILES_MAX) {
                        Box {
                            Row(
                                Modifier.height(YY.s(26)).clip(CircleShape).clickable(onClickLabel = t("加附件")) { menu = true }.padding(horizontal = YY.s(4)),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Icon(Icons.paperclip, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(13)))
                                Text(t("附件"), style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.padding(start = YY.s(3)))
                            }
                            DropdownMenu(menu, { menu = false }) {
                                DropdownMenuItem({ Text(t("照片")) }, onClick = {
                                    menu = false
                                    photos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                                }, leadingIcon = { Icon(Icons.photo, null) })
                                DropdownMenuItem({ Text(t("文件")) }, onClick = {
                                    menu = false
                                    documents.launch(arrayOf("*/*"))
                                }, leadingIcon = { Icon(Icons.folder, null) })
                                DropdownMenuItem({ Text(t("粘贴")) }, onClick = {
                                    menu = false
                                    val clip = (context.getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager).primaryClip
                                    val uris = (0 until (clip?.itemCount ?: 0)).mapNotNull { clip?.getItemAt(it)?.uri }
                                    if (uris.isEmpty()) notice = t("剪贴板里没有图片或文件。") else take(uris)
                                }, leadingIcon = { Icon(Icons.clipboard, null) })
                            }
                        }
                    }
                    Spacer(Modifier.weight(1f))
                    Text("Markdown", style = YY.font(9.5f), color = YY.c.t3)
                    // Only near the end: a count from the first word reads like a quota.
                    val n = Feedback.length(text)
                    if (n >= Feedback.TEXT_MAX - 200) {
                        Spacer(Modifier.size(YY.s(8)))
                        Text("$n/${Feedback.TEXT_MAX}", style = YY.font(9.5f), color = if (n > Feedback.TEXT_MAX - 60) YY.c.warn else YY.c.t3)
                    }
                }
            }
            // 截图公开: only with a screenshot attached. On by default; what it means is said right
            // there, and off still sends the screenshots, sealed for the developer alone.
            AnimatedVisibility(files.any { it.isShot }, enter = androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) + androidx.compose.animation.expandVertically(app.yuyue.design.YYMotion.layout()), exit = androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) + androidx.compose.animation.shrinkVertically(app.yuyue.design.YYMotion.layout())) {
                Row(
                    Modifier.fillMaxWidth()
                        .background(YY.c.card, RoundedCornerShape(YY.s(12)))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12)))
                        .padding(horizontal = YY.s(12), vertical = YY.s(8)),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Column(Modifier.weight(1f).padding(end = YY.s(10))) {
                        Text(t("截图公开在 GitHub 上"), style = YY.font(11.5f), color = YY.c.t1)
                        Text(
                            if (publicShots) t("语阅是开源项目，截图和文字一起公开，别人也能帮忙查。发之前看一眼截图里有没有隐私。")
                            else t("截图照样发出，只有开发者能看到。"),
                            style = YY.font(9.5f), color = YY.c.t3,
                        )
                    }
                    Switch(
                        publicShots, { publicShots = it },
                        modifier = Modifier.semantics { contentDescription = t("截图公开在 GitHub 上") },
                        colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
                    )
                }
            }
            // 邮箱
            Row(
                Modifier.fillMaxWidth().height(YY.s(40))
                    .background(YY.c.card, RoundedCornerShape(YY.s(12)))
                    .border(if (emailOK) 0.5.dp else 1.dp, if (emailOK) YY.c.line else YY.c.warn, RoundedCornerShape(YY.s(12)))
                    .padding(horizontal = YY.s(12)),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(t("邮箱"), style = YY.font(11.5f), color = YY.c.t1)
                Spacer(Modifier.size(YY.s(10)))
                BasicTextField(
                    contact, {
                        contact = Feedback.clip(it.trim(), Feedback.CONTACT_MAX)
                        if (Feedback.isEmail(contact)) Feedback.savedEmail = contact
                    }, singleLine = true,
                    textStyle = YY.font(11.5f).copy(color = YY.c.t1, textAlign = TextAlign.End),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email),
                    cursorBrush = SolidColor(YY.tint.accent),
                    decorationBox = { inner ->
                        Box(contentAlignment = Alignment.CenterEnd) {
                            if (contact.isEmpty()) Text(t("想收到回复就留一个，可不填"), style = YY.font(11.5f), color = YY.c.t3)
                            inner()
                        }
                    },
                    modifier = Modifier.weight(1f),
                )
            }
            // 附加信息: what goes along besides the words, shown as it is.
            Column(
                Modifier.fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(YY.s(12)))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(12)))
                    .padding(horizontal = YY.s(12)),
            ) {
                Row(Modifier.padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(t("附加信息"), style = YY.font(11.5f), color = YY.c.t1)
                        Text(if (withInfo) t("一起发出，方便查问题") else t("不发送，查问题会难一点"), style = YY.font(9.5f), color = YY.c.t3)
                    }
                    Switch(
                        withInfo, { withInfo = it },
                        modifier = Modifier.semantics { contentDescription = t("附加信息") },
                        colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
                    )
                }
                AnimatedVisibility(withInfo, enter = androidx.compose.animation.fadeIn(app.yuyue.design.YYMotion.standard()) + androidx.compose.animation.expandVertically(app.yuyue.design.YYMotion.layout()), exit = androidx.compose.animation.fadeOut(app.yuyue.design.YYMotion.exit()) + androidx.compose.animation.shrinkVertically(app.yuyue.design.YYMotion.layout())) {
                    Column {
                        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                        Column(Modifier.padding(vertical = YY.s(9)), verticalArrangement = Arrangement.spacedBy(YY.s(5))) {
                            Feedback.info.forEach { (label, value) ->
                                Row {
                                    Text(label, style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.width(YY.s(34)))
                                    Text(value, style = YY.font(10.5f), color = YY.c.t2)
                                }
                            }
                        }
                    }
                }
            }
            Row(Modifier.padding(horizontal = YY.s(2)), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.globe, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                Text(t("内容会公开在 GitHub 上。邮箱、日志和其他文件只有开发者能看到"), style = YY.font(10.5f), color = YY.c.t3, modifier = Modifier.padding(start = YY.s(5)))
            }
            notice?.let {
                Row(
                    Modifier.fillMaxWidth().background(YY.c.warn.copy(alpha = 0.1f), RoundedCornerShape(YY.s(12))).padding(YY.s(11)),
                    horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.error, null, tint = YY.c.warn, modifier = Modifier.size(YY.s(14)))
                    Text(it, style = YY.font(11f), color = YY.c.t1)
                }
            }
        }
        // 发送, where the thumb is.
        Column(
            Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = YY.s(16)).padding(top = YY.s(10), bottom = YY.s(12)),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(YY.s(6)),
        ) {
            YYButton(if (sending) t("正在发送…") else t("发送"), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = canSend) {
                sending = true
                notice = null
                scope.launch {
                    try {
                        val outcome = Feedback.send(category, text, contact, withInfo, publicShots, files.toList())
                        when (outcome) {
                            Feedback.Outcome.Sent -> {
                                Feedback.clearDraft(context)
                                done = contact.isNotBlank()
                            }
                            Feedback.Outcome.Queued, Feedback.Outcome.QueuedBusy -> {
                                val busy = outcome == Feedback.Outcome.QueuedBusy
                                Feedback.clearDraft(context)
                                text = ""
                                files.clear()
                                notice = if (busy) t("反馈服务现在太忙，这条先存在这台设备上，过一会儿自动发出，不用再点。")
                                    else t("没网，这条先存在这台设备上，联网后自动发出，不用再点。待发 %lld 条。", Feedback.queued.size)
                            }
                        }
                    } catch (e: Exception) {
                        notice = (e as? Feedback.Problem)?.message ?: t("没发出去，内容有问题。")
                    } finally {
                        sending = false
                    }
                }
            }
            // The daily cap only speaks once it is reached; a count before that reads like a quota.
            when {
                !Feedback.isConfigured -> t("这个版本发不出反馈")
                !emailOK -> t("邮箱好像不对")
                else -> null
            }?.let { Text(it, style = YY.font(10f), color = if (emailOK) YY.c.t3 else YY.c.warn) }
        }
    }
}

@Composable
private fun Thanks(replied: Boolean, again: () -> Unit, close: () -> Unit) {
    Column(
        Modifier.fillMaxSize().background(YY.c.bg).padding(horizontal = YY.s(24)),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box(
            Modifier.size(YY.s(56)).background(YY.tint.black, CircleShape).border(0.5.dp, YY.tint.edge, CircleShape),
            contentAlignment = Alignment.Center,
        ) { Icon(Icons.check, null, tint = YY.tint.accent, modifier = Modifier.size(YY.s(26))) }
        Text(t("收到了，谢谢"), style = YY.font(15f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(14)))
        Text(
            if (replied) t("稍后会出现在 GitHub 上，需要的话也会发邮件回复你。") else t("稍后会出现在 GitHub 上，没留邮箱，所以不会发邮件回复。"),
            style = YY.font(11.5f), color = YY.c.t2, textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = YY.s(8)).widthIn(max = YY.s(240)),
        )
        Row(Modifier.padding(top = YY.s(18)), horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            YYButton(t("再写一条"), ButtonKind.Plain, onClick = again)
            YYButton(t("完成"), ButtonKind.Main, onClick = close)
        }
        Spacer(Modifier.height(YY.s(60)))
    }
}
