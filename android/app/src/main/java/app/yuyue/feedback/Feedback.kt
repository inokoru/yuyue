package app.yuyue.feedback

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.ime
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
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
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.exifinterface.media.ExifInterface
import app.yuyue.BuildConfig
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYMotion
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
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.util.Base64
import java.util.Locale
import java.util.concurrent.TimeUnit

/**
 * 反馈: single-channel AirFeedback submission.
 *
 * The user's message, optional screenshots (EXIF-stripped JPEG), and optional device info are
 * posted privately to the AirFeedback ingest endpoint. Nothing is published publicly and no email
 * address is collected. Drafts stay local until sent or explicitly cleared.
 */
object Feedback {
    const val ENDPOINT = "https://apps.air.build/v1/ingest/feedback"
    private const val APP_KEY = "ak_production_Qs0hTAYx4hLMr8ZINF6vA5UkGx06nSplVhloydvhOyg"
    const val TEXT_MAX = 4000
    const val FILES_MAX = 4
    const val FILE_BYTES_MAX = 8_000_000
    const val IMAGE_MAX_SIDE = 2048
    const val SUPPORT_EMAIL = "yuyue.app@icloud.com"
    /** 早期版本: the entries wear the theme colour. Set to false once 语阅 settles. */
    const val EARLY = true

    class Problem(message: String) : Exception(message)

    /** One image attachment: downscaled, EXIF-stripped JPEG bytes plus its decoded preview. */
    class Attachment(val name: String, val data: ByteArray, val preview: ImageBitmap)

    private val http by lazy {
        OkHttpClient.Builder()
            .readTimeout(60, TimeUnit.SECONDS)
            .writeTimeout(60, TimeUnit.SECONDS)
            .callTimeout(60, TimeUnit.SECONDS)
            .build()
    }

    internal fun canSend(text: String, imageCount: Int, sending: Boolean): Boolean =
        !sending && (text.isNotBlank() || imageCount > 0)

    internal fun buildPayloadJson(
        message: String,
        withInfo: Boolean,
        appVersion: String,
        appBuild: String,
        deviceModel: String,
        osVersion: String,
        locale: String,
        images: List<ByteArray>,
    ): String {
        val encoder = Base64.getEncoder()
        val clipped = clip(message.trim(), TEXT_MAX)
        val cappedImages = images.take(FILES_MAX)
        return buildString(256 + clipped.length) {
            append('{')
            append("\"message\":")
            appendJsonString(clipped)
            append(",\"appVersion\":")
            appendJsonString(if (withInfo) appVersion else "")
            append(",\"appBuild\":")
            appendJsonString(if (withInfo) appBuild else "")
            append(",\"deviceModel\":")
            appendJsonString(if (withInfo) deviceModel else "")
            append(",\"osVersion\":")
            appendJsonString(if (withInfo) osVersion else "")
            append(",\"locale\":")
            appendJsonString(if (withInfo) locale else "")
            append(",\"images\":[")
            cappedImages.forEachIndexed { index, bytes ->
                if (index > 0) append(',')
                append("{\"mediaType\":\"image/jpeg\",\"data\":")
                appendJsonString(encoder.encodeToString(bytes))
                append('}')
            }
            append("]}")
        }
    }

    internal suspend fun postPayload(
        payloadJson: String,
        client: OkHttpClient = http,
        endpoint: String = ENDPOINT,
        appKey: String = APP_KEY,
    ) = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url(endpoint)
            .header("Authorization", "Bearer $appKey")
            .post(payloadJson.toRequestBody("application/json; charset=utf-8".toMediaType()))
            .build()
        try {
            client.newCall(request).execute().use { response ->
                if (response.isSuccessful) return@withContext
                val body = response.body?.string().orEmpty()
                val reason = parseErrorReason(body)
                throw Problem(reason ?: t("当前没有网络或服务暂时不可用，内容和图片已保存在草稿里，稍后再点发送即可。"))
            }
        } catch (_: IOException) {
            throw Problem(t("当前没有网络或服务暂时不可用，内容和图片已保存在草稿里，稍后再点发送即可。"))
        }
    }

    suspend fun send(text: String, withInfo: Boolean, files: List<Attachment>) {
        if (!canSend(text, files.size, false)) {
            throw Problem(t("请写点内容或附一张截图再发送。"))
        }
        val images = files.take(FILES_MAX)
        val payloadJson = buildPayloadJson(
            message = text,
            withInfo = withInfo,
            appVersion = BuildConfig.VERSION_NAME,
            appBuild = BuildConfig.VERSION_CODE.toString(),
            deviceModel = device,
            osVersion = osVersion,
            locale = locale,
            images = images.map { it.data },
        )
        postPayload(payloadJson)
        app.yuyue.analytics.AirTraffic.trackFeedbackSubmitted(images.size, withInfo)
    }

    /** A picked or pasted image URI -> an EXIF-stripped JPEG [Attachment]. Rejects non-images. */
    suspend fun attachment(context: Context, uri: Uri): Attachment = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        val name = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        } ?: uri.lastPathSegment?.substringAfterLast('/') ?: "image.jpg"
        val mime = resolver.getType(uri)
        if (mime != null && !mime.startsWith("image/")) {
            throw Problem(t("只支持图片或截图，不支持上传普通文件。"))
        }
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        runCatching { resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) } }
        val longSide = maxOf(bounds.outWidth, bounds.outHeight)
        if (longSide <= 0) {
            throw Problem(t("只支持图片或截图，不支持上传普通文件。"))
        }
        val (jpeg, bitmap) = shrink(context, uri, longSide)
            ?: throw Problem(t("图片太大或无法读取，请换一张再试。"))
        val stem = name.substringBeforeLast('.').ifBlank { "image" }
        Attachment("$stem.jpg", jpeg, bitmap.asImageBitmap())
    }

    /** Downscales long side to <= [IMAGE_MAX_SIDE], applies EXIF orientation, and re-encodes as JPEG (EXIF stripped). */
    private fun shrink(context: Context, uri: Uri, longSide: Int): Pair<ByteArray, Bitmap>? = runCatching {
        val resolver = context.contentResolver
        var sample = 1
        while (longSide / (sample * 2) >= IMAGE_MAX_SIDE) sample *= 2
        val decoded = resolver.openInputStream(uri)?.use {
            BitmapFactory.decodeStream(it, null, BitmapFactory.Options().apply { inSampleSize = sample })
        } ?: return@runCatching null
        val exif = runCatching { resolver.openInputStream(uri)?.use { ExifInterface(it) } }.getOrNull()
        val rotation = exif?.rotationDegrees ?: 0
        val flipped = exif?.isFlipped == true
        val upright = if (rotation == 0 && !flipped) {
            decoded
        } else {
            val matrix = Matrix().apply {
                if (rotation != 0) postRotate(rotation.toFloat())
                if (flipped) postScale(-1f, 1f)
            }
            Bitmap.createBitmap(decoded, 0, 0, decoded.width, decoded.height, matrix, true)
        }
        val steps = listOf(IMAGE_MAX_SIDE to 82, IMAGE_MAX_SIDE to 68, 1600 to 68, 1200 to 58)
        for ((side, quality) in steps) {
            val scale = minOf(1f, side.toFloat() / maxOf(upright.width, upright.height))
            val fitted = if (scale >= 1f) {
                upright
            } else {
                Bitmap.createScaledBitmap(
                    upright,
                    (upright.width * scale).toInt().coerceAtLeast(1),
                    (upright.height * scale).toInt().coerceAtLeast(1),
                    true,
                )
            }
            val out = ByteArrayOutputStream()
            fitted.compress(Bitmap.CompressFormat.JPEG, quality, out)
            if (out.size() <= FILE_BYTES_MAX) return@runCatching out.toByteArray() to fitted
        }
        null
    }.getOrNull()

    // Draft persistence

    private const val DRAFT_KEY = "feedback.draft"
    private val draftGeneration = java.util.concurrent.atomic.AtomicLong(0)
    private val draftWriteLock = Any()

    class Draft(val text: String, val withInfo: Boolean, val files: List<Attachment>)

    private fun draftDir(context: Context) = File(context.filesDir, "feedback-draft")

    private fun draftJson(): JSONObject = runCatching { JSONObject(Prefs.string(DRAFT_KEY, "{}")) }.getOrDefault(JSONObject())

    private fun writeDraft(json: JSONObject) {
        val empty = json.optString("text").isEmpty() &&
            (json.optJSONArray("files")?.length() ?: 0) == 0 &&
            json.optBoolean("withInfo", true)
        Prefs.set(DRAFT_KEY, if (empty) null else json.toString())
    }

    fun saveDraft(text: String, withInfo: Boolean) =
        writeDraft(draftJson().put("text", text).put("withInfo", withInfo))

    suspend fun saveDraftFiles(context: Context, files: List<Attachment>) = withContext(Dispatchers.IO) {
        val gen = draftGeneration.get()
        val names = JSONArray()
        val capped = files.take(FILES_MAX)
        synchronized(draftWriteLock) {
            if (draftGeneration.get() != gen) return@withContext
            val dir = draftDir(context)
            dir.deleteRecursively()
            if (capped.isNotEmpty()) {
                dir.mkdirs()
                capped.forEachIndexed { i, file ->
                    if (runCatching { File(dir, "$i").writeBytes(file.data) }.isSuccess) names.put(file.name)
                }
            }
        }
        withContext(Dispatchers.Main) {
            if (draftGeneration.get() == gen) writeDraft(draftJson().put("files", names))
        }
    }

    suspend fun loadDraft(context: Context): Draft? = withContext(Dispatchers.IO) {
        val json = draftJson()
        if (json.length() == 0) return@withContext null
        val names = json.optJSONArray("files") ?: JSONArray()
        val files = (0 until minOf(names.length(), FILES_MAX)).mapNotNull { i ->
            val data = runCatching { File(draftDir(context), "$i").readBytes() }.getOrNull() ?: return@mapNotNull null
            val preview = BitmapFactory.decodeByteArray(data, 0, data.size)?.asImageBitmap() ?: return@mapNotNull null
            val name = names.optString(i).ifBlank { "image-${i + 1}.jpg" }
            Attachment(name, data, preview)
        }
        Draft(
            text = json.optString("text"),
            withInfo = json.optBoolean("withInfo", true),
            files = files,
        )
    }

    fun clearDraft(context: Context) {
        draftGeneration.incrementAndGet()
        Prefs.set(DRAFT_KEY, null)
        Prefs.set("feedback.queue", null)
        Prefs.set("feedback.email", null)
        synchronized(draftWriteLock) { draftDir(context).deleteRecursively() }
    }

    // Device info

    val appVersion: String get() = "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})"
    val osVersion: String get() = "Android ${Build.VERSION.RELEASE}"
    val locale: String get() = Locale.getDefault().toLanguageTag()

    val device: String
        get() {
            val mfr = Build.MANUFACTURER.trim().replaceFirstChar { it.uppercase() }
            val model = Build.MODEL.trim()
            return when {
                mfr.isEmpty() -> model
                model.isEmpty() -> mfr
                model.startsWith(mfr, ignoreCase = true) -> model
                else -> "$mfr $model"
            }
        }

    val shortInfo: String get() = "${BuildConfig.VERSION_NAME} · $osVersion · $device"

    val info: List<Pair<String, String>>
        get() = listOf(
            t("版本") to appVersion,
            t("系统") to osVersion,
            t("设备") to device,
            t("语言") to locale,
        )

    /** Opens the user's mail app to write to the developer without the app collecting their email. */
    fun openMail(context: Context): Boolean {
        val intent = Intent(Intent.ACTION_SENDTO, Uri.parse("mailto:$SUPPORT_EMAIL"))
        return runCatching { context.startActivity(intent) }.isSuccess
    }

    fun clip(text: String, max: Int): String =
        if (text.codePointCount(0, text.length) <= max) text else text.substring(0, text.offsetByCodePoints(0, max))

    fun length(text: String): Int = text.codePointCount(0, text.length)

    // Minimal pure-JVM RFC 8259 JSON helpers (keeps JVM unit tests free of Android stubbed org.json).

    private fun StringBuilder.appendJsonString(value: String) {
        append('"')
        for (ch in value) {
            when (ch) {
                '"' -> append("\\\"")
                '\\' -> append("\\\\")
                '\b' -> append("\\b")
                '\u000C' -> append("\\f")
                '\n' -> append("\\n")
                '\r' -> append("\\r")
                '\t' -> append("\\t")
                else -> if (ch.code < 0x20) {
                    append("\\u")
                    append(ch.code.toString(16).padStart(4, '0'))
                } else {
                    append(ch)
                }
            }
        }
        append('"')
    }

    internal fun parseErrorReason(body: String): String? {
        if (body.isBlank()) return null
        val parser = JsonScanner(body)
        return parser.readTopLevelStringField("reason")?.trim()?.takeIf { it.isNotEmpty() }
    }

    private class JsonScanner(private val src: String) {
        private var i = 0

        fun readTopLevelStringField(target: String): String? {
            skipWs()
            if (peek() != '{') return null
            i++
            skipWs()
            if (peek() == '}') return null
            while (i < src.length) {
                skipWs()
                val key = readString() ?: return null
                skipWs()
                if (peek() != ':') return null
                i++
                skipWs()
                if (key == target) {
                    return if (peek() == '"') readString() else null
                }
                if (!skipValue()) return null
                skipWs()
                when (peek()) {
                    ',' -> i++
                    '}' -> return null
                    else -> return null
                }
            }
            return null
        }

        private fun peek(): Char? = if (i < src.length) src[i] else null

        private fun skipWs() {
            while (i < src.length && src[i] in charArrayOf(' ', '\t', '\r', '\n')) i++
        }

        private fun readString(): String? {
            if (peek() != '"') return null
            i++
            val sb = StringBuilder()
            while (i < src.length) {
                val c = src[i++]
                when (c) {
                    '"' -> return sb.toString()
                    '\\' -> {
                        if (i >= src.length) return null
                        when (val esc = src[i++]) {
                            '"', '\\', '/' -> sb.append(esc)
                            'b' -> sb.append('\b')
                            'f' -> sb.append('\u000C')
                            'n' -> sb.append('\n')
                            'r' -> sb.append('\r')
                            't' -> sb.append('\t')
                            'u' -> {
                                if (i + 4 > src.length) return null
                                val code = src.substring(i, i + 4).toIntOrNull(16) ?: return null
                                sb.append(code.toChar())
                                i += 4
                            }
                            else -> return null
                        }
                    }
                    else -> sb.append(c)
                }
            }
            return null
        }

        private fun skipValue(): Boolean {
            skipWs()
            return when (peek()) {
                '"' -> readString() != null
                '{' -> skipObject()
                '[' -> skipArray()
                't' -> consume("true")
                'f' -> consume("false")
                'n' -> consume("null")
                '-', in '0'..'9' -> skipNumber()
                else -> false
            }
        }

        private fun skipObject(): Boolean {
            i++
            skipWs()
            if (peek() == '}') {
                i++
                return true
            }
            while (i < src.length) {
                skipWs()
                if (readString() == null) return false
                skipWs()
                if (peek() != ':') return false
                i++
                if (!skipValue()) return false
                skipWs()
                when (peek()) {
                    ',' -> i++
                    '}' -> {
                        i++
                        return true
                    }
                    else -> return false
                }
            }
            return false
        }

        private fun skipArray(): Boolean {
            i++
            skipWs()
            if (peek() == ']') {
                i++
                return true
            }
            while (i < src.length) {
                if (!skipValue()) return false
                skipWs()
                when (peek()) {
                    ',' -> i++
                    ']' -> {
                        i++
                        return true
                    }
                    else -> return false
                }
            }
            return false
        }

        private fun consume(literal: String): Boolean {
            if (!src.startsWith(literal, i)) return false
            i += literal.length
            return true
        }

        private fun skipNumber(): Boolean {
            val start = i
            if (peek() == '-') i++
            while (i < src.length && src[i] in '0'..'9') i++
            if (peek() == '.') {
                i++
                while (i < src.length && src[i] in '0'..'9') i++
            }
            if (peek() == 'e' || peek() == 'E') {
                i++
                if (peek() == '+' || peek() == '-') i++
                while (i < src.length && src[i] in '0'..'9') i++
            }
            return i > start
        }
    }
}

/** Single-channel AirFeedback form: unified editor + screenshots + collapsible device info + sticky footer. */
@Composable
fun FeedbackPage(close: () -> Unit) {
    var text by remember { mutableStateOf("") }
    var sending by remember { mutableStateOf(false) }
    var done by remember { mutableStateOf(false) }
    var notice by remember { mutableStateOf<String?>(null) }
    var withInfo by remember { mutableStateOf(true) }
    var infoExpanded by remember { mutableStateOf(false) }
    var focused by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var restored by remember { mutableStateOf(false) }
    val focus = remember { FocusRequester() }
    val files = remember { mutableStateListOf<Feedback.Attachment>() }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val canSend = Feedback.canSend(text, files.size, sending)

    fun openMail() {
        if (!Feedback.openMail(context)) {
            (context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager)
                ?.setPrimaryClip(ClipData.newPlainText("email", Feedback.SUPPORT_EMAIL))
            notice = t("没有找到邮件应用，已复制邮箱：%@。", Feedback.SUPPORT_EMAIL)
        }
    }

    fun take(uris: List<Uri>) {
        if (uris.isEmpty()) return
        scope.launch {
            notice = null
            for (uri in uris) {
                if (files.size >= Feedback.FILES_MAX) break
                try {
                    files += Feedback.attachment(context, uri)
                } catch (e: Exception) {
                    notice = (e as? Feedback.Problem)?.message ?: t("图片太大或无法读取，请换一张再试。")
                }
            }
        }
    }

    fun pasteClipboardImage() {
        val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as? ClipboardManager
        val clip = clipboard?.primaryClip
        val resolver = context.contentResolver
        val imageUris = (0 until (clip?.itemCount ?: 0)).mapNotNull { index ->
            val uri = clip?.getItemAt(index)?.uri ?: return@mapNotNull null
            val mime = resolver.getType(uri)
            if (mime != null && mime.startsWith("image/")) uri else null
        }
        if (imageUris.isEmpty()) {
            notice = t("剪贴板里没有图片。")
        } else {
            take(imageUris)
        }
    }

    val photos = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(Feedback.FILES_MAX),
    ) { take(it) }

    LaunchedEffect(Unit) {
        Feedback.loadDraft(context)?.let { draft ->
            text = draft.text
            withInfo = draft.withInfo
            files.addAll(draft.files)
        }
        restored = true
        runCatching { focus.requestFocus() }
    }
    LaunchedEffect(text, withInfo) {
        if (restored && !done) Feedback.saveDraft(text, withInfo)
    }
    LaunchedEffect(files.toList()) {
        if (restored && !done) Feedback.saveDraftFiles(context, files.toList())
    }

    if (done) {
        Thanks(
            onMail = { openMail() },
            again = {
                text = ""
                withInfo = true
                infoExpanded = false
                files.clear()
                notice = null
                done = false
            },
            close = close,
        )
        return
    }

    val density = LocalDensity.current
    val imeBottom = with(density) { WindowInsets.ime.getBottom(this).toDp() }
    val bottomClearance = maxOf(imeBottom, app.yuyue.LocalTabBarHeight.current)

    Column(Modifier.fillMaxSize().background(YY.c.bg).padding(bottom = bottomClearance)) {
        Column(
            Modifier
                .weight(1f)
                .verticalScroll(rememberScrollState())
                .padding(horizontal = YY.s(16))
                .padding(top = YY.s(4), bottom = YY.s(12)),
            verticalArrangement = Arrangement.spacedBy(YY.s(10)),
        ) {
            Text(t("语阅还在早期，哪里不对、想要什么，直接写。"), style = YY.font(11.5f), color = YY.c.t2)

            // Unified editor card: multi-line input, screenshot strip, divider, bottom toolbar.
            Column(
                Modifier
                    .fillMaxWidth()
                    .background(YY.c.card, RoundedCornerShape(14.dp))
                    .border(
                        if (focused) 1.dp else 0.5.dp,
                        if (focused) YY.tint.edge else YY.c.line,
                        RoundedCornerShape(14.dp),
                    ),
            ) {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .padding(start = YY.s(12), end = YY.s(12), top = YY.s(11), bottom = YY.s(8)),
                    verticalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    BasicTextField(
                        value = text,
                        onValueChange = { text = Feedback.clip(it, Feedback.TEXT_MAX) },
                        textStyle = YY.font(12.5f).copy(color = YY.c.t1, lineHeight = YY.sp(20f)),
                        cursorBrush = SolidColor(YY.tint.accent),
                        decorationBox = { inner ->
                            if (text.isEmpty()) {
                                Text(
                                    t("遇到什么问题、想要什么功能，或哪本书打开不对，都可以直接写"),
                                    style = YY.font(12.5f).copy(lineHeight = YY.sp(20f)),
                                    color = YY.c.t3,
                                )
                            }
                            inner()
                        },
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(min = 128.dp)
                            .focusRequester(focus)
                            .onFocusChanged { focused = it.isFocused },
                    )
                    if (files.isNotEmpty()) {
                        Row(
                            Modifier
                                .fillMaxWidth()
                                .horizontalScroll(rememberScrollState())
                                .padding(top = YY.s(4), end = YY.s(6)),
                            horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                        ) {
                            files.forEach { file ->
                                Box(Modifier.padding(top = 5.dp, end = 5.dp)) {
                                    Image(
                                        bitmap = file.preview,
                                        contentDescription = file.name,
                                        contentScale = ContentScale.Crop,
                                        modifier = Modifier
                                            .size(58.dp)
                                            .clip(RoundedCornerShape(10.dp))
                                            .border(0.5.dp, YY.c.line, RoundedCornerShape(10.dp)),
                                    )
                                    Box(
                                        Modifier
                                            .align(Alignment.TopEnd)
                                            .offset(x = 5.dp, y = (-5).dp)
                                            .size(18.dp)
                                            .background(Color.Black.copy(alpha = 0.6f), CircleShape)
                                            .clickable(onClickLabel = t("去掉「%@」", file.name)) { files.remove(file) },
                                        contentAlignment = Alignment.Center,
                                    ) {
                                        Icon(Icons.close, t("去掉「%@」", file.name), tint = Color.White, modifier = Modifier.size(10.dp))
                                    }
                                }
                            }
                        }
                    }
                }
                Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                Row(
                    Modifier
                        .fillMaxWidth()
                        .padding(horizontal = YY.s(10), vertical = YY.s(7)),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (files.size < Feedback.FILES_MAX) {
                        Box {
                            Row(
                                Modifier
                                    .clip(CircleShape)
                                    .background(YY.c.inset)
                                    .clickable(onClickLabel = t("图片")) { menu = true }
                                    .padding(horizontal = YY.s(9), vertical = YY.s(4)),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
                            ) {
                                Icon(Icons.photo, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(12.5f)))
                                Text(t("图片"), style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t2)
                            }
                            DropdownMenu(menu, { menu = false }) {
                                DropdownMenuItem(
                                    text = { Text(t("从相册选图")) },
                                    onClick = {
                                        menu = false
                                        photos.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                                    },
                                    leadingIcon = { Icon(Icons.photo, null) },
                                )
                                DropdownMenuItem(
                                    text = { Text(t("粘贴剪贴板图片")) },
                                    onClick = {
                                        menu = false
                                        pasteClipboardImage()
                                    },
                                    leadingIcon = { Icon(Icons.clipboard, null) },
                                )
                            }
                        }
                    }
                    Spacer(Modifier.weight(1f))
                    Text(
                        if (files.isEmpty()) t("最多 4 张图") else t("图片 %lld/4", files.size),
                        style = YY.font(9.5f, tnum = true),
                        color = YY.c.t3,
                    )
                    val n = Feedback.length(text)
                    if (n >= 3600) {
                        Spacer(Modifier.width(YY.s(8)))
                        Text(
                            "$n/${Feedback.TEXT_MAX}",
                            style = YY.font(9.5f, tnum = true),
                            color = if (n >= 3900) YY.c.warn else YY.c.t3,
                        )
                    }
                }
            }

            // Collapsible 附带设备信息 card (default ON, collapsed by default).
            Column(
                Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(14.dp))
                    .background(YY.c.card)
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)),
            ) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .clickable(enabled = withInfo, onClickLabel = t("附带设备信息")) { infoExpanded = !infoExpanded }
                        .padding(horizontal = YY.s(12), vertical = YY.s(8)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                ) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(YY.s(4)),
                        ) {
                            Text(t("附带设备信息"), style = YY.font(11.5f), color = YY.c.t1)
                            if (withInfo) {
                                Icon(
                                    if (infoExpanded) Icons.chevronUp else Icons.chevronDown,
                                    contentDescription = null,
                                    tint = YY.c.t3,
                                    modifier = Modifier.size(YY.s(11)),
                                )
                            }
                        }
                        Text(
                            if (withInfo) Feedback.shortInfo else t("本次不附带"),
                            style = YY.font(9.5f),
                            color = YY.c.t3,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    Switch(
                        checked = withInfo,
                        onCheckedChange = {
                            withInfo = it
                            if (!it) infoExpanded = false
                        },
                        modifier = Modifier.semantics { contentDescription = t("附带设备信息") },
                        colors = SwitchDefaults.colors(
                            checkedTrackColor = YY.tint.fill,
                            checkedThumbColor = YY.c.card,
                            uncheckedTrackColor = YY.c.inset,
                            uncheckedBorderColor = YY.c.line,
                        ),
                    )
                }
                AnimatedVisibility(
                    visible = withInfo && infoExpanded,
                    enter = androidx.compose.animation.fadeIn(YYMotion.standard()) + androidx.compose.animation.expandVertically(YYMotion.layout()),
                    exit = androidx.compose.animation.fadeOut(YYMotion.exit()) + androidx.compose.animation.shrinkVertically(YYMotion.layout()),
                ) {
                    Column(Modifier.padding(horizontal = YY.s(12))) {
                        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                        val info = Feedback.info
                        val infoStyle = YY.font(10.5f)
                        val measurer = rememberTextMeasurer()
                        val density = LocalDensity.current
                        val labelWidth = remember(info, density, measurer) {
                            val maxPx = info.maxOfOrNull { (label, _) ->
                                measurer.measure(label, style = infoStyle, maxLines = 1, softWrap = false).size.width
                            } ?: 0
                            maxOf(YY.s(24), with(density) { maxPx.toDp() } + 2.dp)
                        }
                        Column(Modifier.padding(vertical = YY.s(9)), verticalArrangement = Arrangement.spacedBy(YY.s(5))) {
                            info.forEach { (label, value) ->
                                Row(horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                                    Text(label, style = infoStyle, color = YY.c.t3, maxLines = 1, softWrap = false, modifier = Modifier.width(labelWidth))
                                    Text(value, style = infoStyle, color = YY.c.t2, modifier = Modifier.weight(1f))
                                }
                            }
                        }
                    }
                }
            }

            // Mail hint row.
            Row(Modifier.padding(horizontal = YY.s(2)), verticalAlignment = Alignment.CenterVertically) {
                Text(t("需要回复？"), style = YY.font(10.5f), color = YY.c.t3)
                Text(
                    t("直接发邮件 ↗"),
                    style = YY.font(10.5f),
                    color = YY.tint.accent,
                    modifier = Modifier
                        .clickable(onClickLabel = t("直接发邮件 ↗")) { openMail() }
                        .padding(horizontal = YY.s(4), vertical = YY.s(2)),
                )
            }

            // Inline notice banner.
            notice?.let {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .background(YY.c.warn.copy(alpha = 0.1f), RoundedCornerShape(YY.s(12)))
                        .padding(YY.s(11)),
                    horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                    verticalAlignment = Alignment.Top,
                ) {
                    Icon(Icons.error, null, tint = YY.c.warn, modifier = Modifier.size(YY.s(14)))
                    Text(it, style = YY.font(11f), color = YY.c.t1)
                }
            }
        }

        // Sticky bottom footer.
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Column(
            Modifier
                .fillMaxWidth()
                .background(YY.c.bg)
                .padding(horizontal = YY.s(16))
                .padding(top = YY.s(10), bottom = YY.s(12)),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(YY.s(8)),
        ) {
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(YY.s(5)),
            ) {
                Icon(Icons.lockFill, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(11)))
                Text(t("仅发给开发者，不会公开"), style = YY.font(10f), color = YY.c.t3)
            }
            YYButton(
                title = if (sending) t("正在发送…") else t("发送给开发者"),
                kind = ButtonKind.Main,
                size = ButtonSize.Large,
                wide = true,
                enabled = canSend,
            ) {
                sending = true
                notice = null
                scope.launch {
                    try {
                        Feedback.send(text, withInfo, files.toList())
                        Feedback.clearDraft(context)
                        done = true
                    } catch (e: Exception) {
                        notice = (e as? Feedback.Problem)?.message
                            ?: t("当前没有网络或服务暂时不可用，内容和图片已保存在草稿里，稍后再点发送即可。")
                    } finally {
                        sending = false
                    }
                }
            }
            if ((text.isNotBlank() || files.isNotEmpty()) && !sending) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
                ) {
                    Text(t("草稿已自动保存"), style = YY.font(10f), color = YY.c.t3)
                    Text("·", style = YY.font(10f), color = YY.c.t3)
                    Text(
                        t("清空"),
                        style = YY.font(10f),
                        color = YY.c.t2,
                        modifier = Modifier
                            .clickable(onClickLabel = t("清空")) {
                                text = ""
                                files.clear()
                                notice = null
                                Feedback.clearDraft(context)
                            }
                            .padding(horizontal = YY.s(2), vertical = YY.s(2)),
                    )
                }
            }
        }
    }
}

@Composable
private fun Thanks(onMail: () -> Unit, again: () -> Unit, close: () -> Unit) {
    Column(
        Modifier
            .fillMaxSize()
            .background(YY.c.bg)
            .padding(bottom = app.yuyue.LocalTabBarHeight.current)
            .padding(horizontal = YY.s(24)),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Box(
            Modifier
                .size(YY.s(56))
                .background(YY.c.ok.copy(alpha = 0.14f), CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            Icon(Icons.checkCircleFill, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(30)))
        }
        Text(
            t("已发给开发者，谢谢"),
            style = YY.font(15f, FontWeight.SemiBold),
            color = YY.c.t1,
            modifier = Modifier.padding(top = YY.s(14)),
        )
        Text(
            t("这条反馈仅开发者可见，不会公开。"),
            style = YY.font(11.5f),
            color = YY.c.t2,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(top = YY.s(8)).widthIn(max = YY.s(260)),
        )
        Row(
            Modifier.padding(top = YY.s(8)),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(t("如需回复，可发邮件至"), style = YY.font(10.5f), color = YY.c.t3)
            Text(
                Feedback.SUPPORT_EMAIL,
                style = YY.font(10.5f),
                color = YY.tint.accent,
                modifier = Modifier
                    .clickable(onClickLabel = Feedback.SUPPORT_EMAIL, onClick = onMail)
                    .padding(horizontal = YY.s(4), vertical = YY.s(2)),
            )
        }
        Row(Modifier.padding(top = YY.s(18)), horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            YYButton(t("再写一条"), ButtonKind.Plain, onClick = again)
            YYButton(t("完成"), ButtonKind.Main, onClick = close)
        }
        Spacer(Modifier.height(YY.s(60)))
    }
}
