package app.yuyue.speech

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.ensureActive
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.Locale
import java.util.TimeZone
import java.util.concurrent.TimeUnit
import kotlin.coroutines.coroutineContext

/**
 * 本机模型与字体的统一多源自动下载与断点续传。
 *
 * 1. 自动选路：中国大陆地区 / 中国时区 / 简体中文环境优先走魔搭（ModelScope）国内 CDN，
 *    超时或报错时自动无感回退 GitHub Releases；海外环境优先 GitHub Releases，失败回退魔搭。
 * 2. 断点续传：保留 `.part` 临时文件，重连或切源时带 `Range` 头续传；下载完成后用 BLAKE3 强校验。
 */
object ModelDownload {
    const val GITHUB_RELEASE_BASE = "https://github.com/inokoru/yuyue/releases/download/models-1/"
    const val MODELSCOPE_REPO = "nowtin/yuyue-models"
    const val MODELSCOPE_BASE = "https://modelscope.cn/models/$MODELSCOPE_REPO/resolve/master/"

    private val http = OkHttpClient.Builder()
        .connectTimeout(20, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .followRedirects(true)
        .followSslRedirects(true)
        .build()

    fun prefersChinaMirror(): Boolean {
        val locale = Locale.getDefault()
        if (locale.country.equals("CN", ignoreCase = true)) return true
        val tz = TimeZone.getDefault().id
        if (tz == "Asia/Shanghai" || tz == "Asia/Chongqing" || tz == "Asia/Harbin" || tz == "Asia/Urumqi") {
            return true
        }
        if (locale.language.equals("zh", ignoreCase = true) &&
            !locale.script.equals("Hant", ignoreCase = true) &&
            locale.country.uppercase(Locale.ROOT) !in setOf("TW", "HK", "MO")
        ) {
            return true
        }
        return false
    }

    /** 构造按优先级排序的候选下载地址（自动去重）。 */
    fun candidateUrls(
        asset: String,
        officialUrl: String? = null,
        mirrorPath: String = asset,
    ): List<String> {
        val rel = mirrorPath.trimStart('/')
        val official = officialUrl ?: (GITHUB_RELEASE_BASE + asset)
        val mirror = MODELSCOPE_BASE + rel
        return if (prefersChinaMirror()) listOf(mirror, official).distinct() else listOf(official, mirror).distinct()
    }

    /**
     * 多源回退 + HTTP Range 断点续传下载。
     * 中断或取消时保留 [partFile] 以便下次继续；仅当 BLAKE3 校验不一致时清空重下。
     */
    suspend fun downloadWithResume(
        urls: List<String>,
        partFile: File,
        expectedBytes: Long = 0L,
        expectedBlake3: String? = null,
        onBytes: (Long, Long) -> Unit = { _, _ -> },
    ) {
        if (urls.isEmpty()) throw IOException("no download urls")
        partFile.parentFile?.mkdirs()

        // 本地已有完整且校验通过的 .part 时直接复用。
        if (partFile.isFile && partFile.length() > 0 && expectedBytes > 0 && partFile.length() >= expectedBytes) {
            val hasDigest = !expectedBlake3.isNullOrEmpty()
            val valid = if (hasDigest) matchesDigest(partFile, expectedBlake3) else (partFile.length() == expectedBytes)
            if (valid) {
                onBytes(partFile.length(), partFile.length())
                return
            } else {
                partFile.delete()
            }
        }

        var lastError: Exception = IOException("download failed")
        for (url in urls) {
            coroutineContext.ensureActive()
            try {
                streamOneSource(url, partFile, expectedBytes, expectedBlake3, onBytes)
                if (matchesDigest(partFile, expectedBlake3)) return
                partFile.delete()
                lastError = IOException("digest")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                coroutineContext.ensureActive()
                // 网络或 HTTP 错误：保留已下载字节，尝试下一个源继续续传。
                lastError = e
            }
        }
        throw lastError
    }

    private suspend fun streamOneSource(
        url: String,
        partFile: File,
        expectedBytes: Long,
        expectedBlake3: String?,
        onBytes: (Long, Long) -> Unit,
    ) {
        val offset = if (partFile.isFile) partFile.length() else 0L
        val res = fetchRange(url, partFile, offset, expectedBytes, onBytes)
        if (res == 416) {
            val hasDigest = !expectedBlake3.isNullOrEmpty()
            val looksComplete = if (hasDigest) matchesDigest(partFile, expectedBlake3) else (expectedBytes > 0 && offset == expectedBytes)
            if (offset > 0 && looksComplete) {
                onBytes(offset, offset)
                return
            }
            partFile.delete()
            val retry = fetchRange(url, partFile, 0L, expectedBytes, onBytes)
            if (retry !in 200..299) throw IOException("HTTP $retry")
        }
    }

    private suspend fun fetchRange(
        url: String,
        partFile: File,
        offset: Long,
        expectedBytes: Long,
        onBytes: (Long, Long) -> Unit,
    ): Int {
        val reqBuilder = Request.Builder().url(url)
        if (offset > 0) {
            reqBuilder.header("Range", "bytes=$offset-")
        }
        val call = http.newCall(reqBuilder.build())
        val cancelHook = coroutineContext[Job]?.invokeOnCompletion { cause ->
            if (cause is CancellationException) call.cancel()
        }
        try {
            call.execute().use { r ->
                if (r.code == 416) return 416
                if (!r.isSuccessful) throw IOException("HTTP ${r.code}")
                val body = r.body ?: throw IOException("empty")
                val isPartial = r.code == 206 && offset > 0
                val contentLen = body.contentLength().takeIf { it > 0 } ?: 0L
                val total = when {
                    isPartial -> parseContentRangeTotal(r.header("Content-Range"))
                        ?: if (contentLen > 0) offset + contentLen else maxOf(expectedBytes, offset)
                    contentLen > 0 -> contentLen
                    else -> expectedBytes
                }

                var done = if (isPartial) offset else 0L
                var last = done
                if (done > 0) onBytes(done, maxOf(total, done))

                body.byteStream().use { input ->
                    FileOutputStream(partFile, isPartial).use { out ->
                        val buf = ByteArray(1 shl 16)
                        while (true) {
                            coroutineContext.ensureActive()
                            val n = input.read(buf)
                            if (n < 0) break
                            out.write(buf, 0, n)
                            done += n
                            if (done - last >= (1 shl 18)) {
                                last = done
                                onBytes(done, maxOf(total, done))
                            }
                        }
                        onBytes(done, maxOf(total, done))
                    }
                }
                return r.code
            }
        } finally {
            cancelHook?.dispose()
        }
    }

    private fun parseContentRangeTotal(header: String?): Long? {
        val slash = header?.lastIndexOf('/') ?: return null
        if (slash < 0 || slash + 1 >= header.length) return null
        return header.substring(slash + 1).trim().toLongOrNull()?.takeIf { it > 0 }
    }

    private fun matchesDigest(file: File, expectedBlake3: String?): Boolean {
        if (expectedBlake3.isNullOrEmpty()) return true
        if (!file.isFile) return false
        return runCatching {
            uniffi.yuyue_core.fileDigest(file.path).equals(expectedBlake3, ignoreCase = true)
        }.getOrDefault(false)
    }
}
