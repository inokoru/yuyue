package app.yuyue.update

import android.Manifest
import android.app.ActivityManager
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Process
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.BuildConfig
import app.yuyue.R
import app.yuyue.design.Prefs
import app.yuyue.speech.ModelDownload
import app.yuyue.t
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import uniffi.yuyue_core.Release
import uniffi.yuyue_core.UpdateInfo
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference

/**
 * 更新渠道. Switching back to 正式版 never goes back a version: the next release, whose
 * `versionCode` is higher than every beta before it, is the update.
 */
enum class UpdateChannel(val key: String) {
    Stable("stable"), Beta("beta");
    val title get() = if (this == Stable) t("正式版") else t("测试版")
    companion object {
        val detail get() = t("测试版先拿到新功能，可能不稳定。换回正式版不会退回旧版本，下一个正式版出来时自动换过去")
    }
}

/** A newer version and the notes of every version since this one, newest first. */
data class UpdateOffer(val info: UpdateInfo, val releases: List<Release>, val edition: Int? = null) {
    val display get() = info.version
    val size get() = info.size.toLong()
}

sealed interface UpdatePhase {
    data object Idle : UpdatePhase
    data object Checking : UpdatePhase
    data object Current : UpdatePhase
    data object CheckFailed : UpdatePhase
    data object Release : UpdatePhase
    data object Skipped : UpdatePhase
    data object Later : UpdatePhase
    data class Downloading(val done: Float) : UpdatePhase
    data class DownloadFailed(val why: String) : UpdatePhase
    data object Ready : UpdatePhase
    /** Downloaded; the system doesn't let 语阅 install apps yet (设置 → 安装未知应用). */
    data object NeedPermission : UpdatePhase
}

/**
 * 软件更新 for the APK from GitHub (not Google Play's: there `BuildConfig.SELF_UPDATE` is off and
 * nothing here runs). `update.json` from the newest release says the version (on 测试版, from the
 * newest release that has one, pre-releases included); the notes of every
 * version since this one come from that release's `CHANGELOG.md`. The APK is downloaded, its
 * SHA-256 and its signing certificate checked against this app's, and handed to the system's
 * installer, which asks the user. Skip, remind and the last check stay on this phone.
 *
 * Debug builds: `-e updateBase http://10.0.2.2:8000/` reads a folder of one's own (a draft release
 * served from `dist/`) instead.
 */
object Updates {
    const val LATEST = "https://github.com/inokoru/yuyue/releases/latest/download/"
    const val MIRROR_LATEST = "https://cnb.cool/inokoru/yuyue/-/releases/latest/download/"
    const val BASE_KEY = "update.base"
    private const val AUTO_KEY = "update.auto"
    private const val CHANNEL_KEY = "update.channel"
    private const val CHECKED_KEY = "update.checked"
    private const val CHECKED_BUILD_KEY = "update.checkedBuild"
    private const val SKIPPED_KEY = "update.skipped"
    private const val REMIND_VERSION_KEY = "update.remindVersion"
    private const val REMIND_UNTIL_KEY = "update.remindUntil"
    private const val LAST_SUCCESS_KEY = "update.lastSuccess"
    private const val LAST_AUTO_KEY = "update.lastSuccessAuto"
    private const val LAST_CURRENT_KEY = "update.lastWasCurrent"
    private const val DAY = 24 * 3600 * 1000L
    private const val REMIND = 3 * DAY
    private const val INSTALL_CHANNEL_ID = "yuyue.update.install"
    private const val INSTALL_NOTIFY_ID = 4201

    private val sameBuild = Prefs.long(CHECKED_BUILD_KEY) == BuildConfig.VERSION_CODE.toLong()

    var phase by mutableStateOf<UpdatePhase>(UpdatePhase.Idle)
        private set
    var offer by mutableStateOf<UpdateOffer?>(null)
        private set
    var autoOn by mutableStateOf(Prefs.bool(AUTO_KEY, true))
        private set
    var channel by mutableStateOf(UpdateChannel.entries.firstOrNull { it.key == Prefs.string(CHANNEL_KEY) } ?: UpdateChannel.Stable)
        private set
    private var skipped by mutableStateOf(Prefs.string(SKIPPED_KEY).takeIf { it.isNotEmpty() && it != BuildConfig.VERSION_NAME })
    private var remindVersion by mutableStateOf(Prefs.string(REMIND_VERSION_KEY).takeIf { it.isNotEmpty() && it != BuildConfig.VERSION_NAME })
    /** Milliseconds. */
    var remindUntil by mutableStateOf(if (remindVersion != null) Prefs.long(REMIND_UNTIL_KEY) else null)
        private set
    var lastSuccess by mutableStateOf(if (sameBuild) Prefs.long(LAST_SUCCESS_KEY) else null)
        private set
    var lastSuccessAuto by mutableStateOf(sameBuild && Prefs.bool(LAST_AUTO_KEY, false))
        private set
    var lastWasCurrent by mutableStateOf(sameBuild && Prefs.bool(LAST_CURRENT_KEY, false))
        private set

    init {
        if (!sameBuild) {
            listOf(CHECKED_KEY, LAST_SUCCESS_KEY, LAST_AUTO_KEY, LAST_CURRENT_KEY).forEach { Prefs.set(it, null) }
        }
        if (skipped == null && Prefs.string(SKIPPED_KEY).isNotEmpty()) Prefs.set(SKIPPED_KEY, null)
        if (remindVersion == null && Prefs.string(REMIND_VERSION_KEY).isNotEmpty()) {
            Prefs.set(REMIND_VERSION_KEY, null)
            Prefs.set(REMIND_UNTIL_KEY, null)
        }
    }
    private var apk: File? = null
    private val installing = AtomicBoolean(false)
    private val pendingConfirmRef = AtomicReference<Intent?>(null)

    var pendingConfirmIntent: Intent?
        get() = pendingConfirmRef.get()
        internal set(value) {
            pendingConfirmRef.set(value)
        }

    val enabled: Boolean get() = BuildConfig.SELF_UPDATE
    val currentLabel get() = "${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})"

    val showsBadge: Boolean
        get() {
            val o = offer ?: return false
            if (skipped == o.display) return false
            val until = remindUntil
            if (remindVersion == o.display && until != null && System.currentTimeMillis() < until) return false
            return true
        }

    private val http by lazy { OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS).build() }

    /** Ordered candidate release folders: China mirror (CNB) first on mainland locales, with GitHub fallback. */
    private fun candidateBases(): List<String> {
        Prefs.string(BASE_KEY).takeIf { it.isNotEmpty() }?.let { return listOf(if (it.endsWith("/")) it else "$it/") }
        val preferCN = ModelDownload.prefersChinaMirror()
        if (channel != UpdateChannel.Beta) {
            return if (preferCN) listOf(MIRROR_LATEST, LATEST) else listOf(LATEST, MIRROR_LATEST)
        }
        val bases = mutableListOf<String>()
        if (preferCN) {
            runCatching { newestMirrorBase() }.getOrNull()?.let { bases += it }
            runCatching { newestBase() }.getOrNull()?.let { bases += it }
            bases += listOf(MIRROR_LATEST, LATEST)
        } else {
            runCatching { newestBase() }.getOrNull()?.let { bases += it }
            runCatching { newestMirrorBase() }.getOrNull()?.let { bases += it }
            bases += listOf(LATEST, MIRROR_LATEST)
        }
        return bases.distinct()
    }

    /** GitHub's list is newest first; drafts aren't shown to anyone else, `models-1` has no `update.json`. */
    private fun newestBase(): String? {
        val list = org.json.JSONArray(get("https://api.github.com/repos/inokoru/yuyue/releases?per_page=20"))
        for (i in 0 until list.length()) {
            val r = list.getJSONObject(i)
            if (r.optBoolean("draft")) continue
            val assets = r.optJSONArray("assets") ?: continue
            if ((0 until assets.length()).any { assets.getJSONObject(it).optString("name") == "update.json" }) {
                return "https://github.com/inokoru/yuyue/releases/download/${r.getString("tag_name")}/"
            }
        }
        return null
    }

    /** CNB public web JSON release list for beta channel discovery without token. */
    private fun newestMirrorBase(): String? {
        val req = Request.Builder()
            .url("https://cnb.cool/inokoru/yuyue/-/releases?page=1&page_size=20")
            .header("Accept", "application/vnd.cnb.web+json")
            .build()
        val body = http.newCall(req).execute().use { r ->
            if (!r.isSuccessful) return null
            r.body?.string() ?: return null
        }
        val list = org.json.JSONObject(body).optJSONArray("releases") ?: return null
        for (i in 0 until list.length()) {
            val r = list.getJSONObject(i)
            if (r.optBoolean("is_draft")) continue
            val assets = r.optJSONArray("assets") ?: continue
            if ((0 until assets.length()).any { assets.getJSONObject(it).optString("name") == "update.json" }) {
                val tag = r.optString("tag_ref").removePrefix("refs/tags/")
                if (tag.isNotEmpty()) return "https://cnb.cool/inokoru/yuyue/-/releases/download/$tag/"
            }
        }
        return null
    }

    /** On opening: once a day at most, when 自动检查 is on; `byHand` from 检查更新, always. */
    suspend fun checkNow(context: Context, byHand: Boolean = false) {
        if (!enabled) return
        if (!byHand) {
            if (!autoOn) return
            val last = Prefs.long(CHECKED_KEY) ?: 0L
            val staleOffer = lastSuccess != null && !lastWasCurrent
            if (!staleOffer && System.currentTimeMillis() - last < DAY) {
                if (phase == UpdatePhase.Idle && lastSuccess != null) phase = UpdatePhase.Current
                return
            }
        }
        if (phase is UpdatePhase.Downloading || phase == UpdatePhase.Checking) return
        val before = phase
        if (byHand) phase = UpdatePhase.Checking
        Prefs.set(CHECKED_KEY, System.currentTimeMillis())
        try {
            val found = withContext(Dispatchers.IO) { fetch(context) }
            offer = found?.first
            apk = found?.second
            val now = System.currentTimeMillis()
            lastSuccess = now
            lastSuccessAuto = !byHand
            lastWasCurrent = found == null
            Prefs.set(CHECKED_BUILD_KEY, BuildConfig.VERSION_CODE.toLong())
            Prefs.set(LAST_SUCCESS_KEY, now)
            Prefs.set(LAST_AUTO_KEY, !byHand)
            Prefs.set(LAST_CURRENT_KEY, found == null)
            reconcile(context)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            phase = if (byHand) UpdatePhase.CheckFailed else before
        }
    }

    fun setAuto(context: Context, on: Boolean, launch: (suspend () -> Unit) -> Unit) {
        autoOn = on
        Prefs.set(AUTO_KEY, on)
        if (on) launch { checkNow(context) }
    }

    fun setChannel(context: Context, to: UpdateChannel, launch: (suspend () -> Unit) -> Unit) {
        if (to == channel) return
        channel = to
        Prefs.set(CHANNEL_KEY, to.key)
        launch { checkNow(context, byHand = true) }
    }

    private fun reconcile(context: Context) {
        val o = offer ?: run { phase = if (lastSuccess == null) UpdatePhase.Idle else UpdatePhase.Current; return }
        val until = remindUntil
        phase = when {
            skipped == o.display -> UpdatePhase.Skipped
            remindVersion == o.display && until != null && System.currentTimeMillis() < until -> UpdatePhase.Later
            apk != null -> if (mayInstall(context)) UpdatePhase.Ready else UpdatePhase.NeedPermission
            else -> UpdatePhase.Release
        }
    }

    private fun get(url: String): String =
        getOrNull(url) ?: error("HTTP 404")

    private fun getOrNull(url: String): String? =
        http.newCall(Request.Builder().url(url).build()).execute().use { r ->
            if (r.code == 404) return null
            if (!r.isSuccessful) error("HTTP ${r.code}")
            r.body!!.string()
        }

    /** A newer version, and its APK when an earlier visit downloaded it already. */
    private fun fetch(context: Context): Pair<UpdateOffer, File?>? {
        var json: String? = null
        var anyReachable = false
        var lastErr: Exception? = null
        for (base in candidateBases()) {
            try {
                val got = getOrNull(base + "update.json")
                anyReachable = true
                if (got != null) {
                    json = got
                    break
                }
            } catch (e: Exception) {
                lastErr = e
            }
        }
        if (!anyReachable && lastErr != null) throw lastErr
        if (json == null) return null
        val info = uniffi.yuyue_core.parseUpdate(json)
        if (info.code.toLong() <= BuildConfig.VERSION_CODE.toLong()) return null
        val log = runCatching { if (info.changelog.isNotEmpty()) get(info.changelog) else "" }.getOrDefault("")
        val all = if (log.isNotEmpty()) uniffi.yuyue_core.parseChangelog(log) else emptyList()
        val between = uniffi.yuyue_core.releasesAfter(log, BuildConfig.VERSION_NAME)
        val releases = if (between.any { it.version == info.version }) between else listOf(uniffi.yuyue_core.updateRelease(info)) + between
        val edition = UpdateVerifier.editionOf(info.version, all.map { it.version }) { a, b -> uniffi.yuyue_core.compareVersions(a, b) }
        val file = apkFile(context, info)
        val ready = UpdateVerifier.validateCachedApk(file, info.size.toLong(), info.sha256) { sameSigner(context, it) }
        return UpdateOffer(info, releases, edition) to file.takeIf { ready }
    }

    fun skip() {
        val o = offer ?: return
        skipped = o.display
        Prefs.set(SKIPPED_KEY, o.display)
        phase = UpdatePhase.Skipped
    }

    fun remindLater() {
        val o = offer ?: return
        remindVersion = o.display
        remindUntil = System.currentTimeMillis() + REMIND
        Prefs.set(REMIND_VERSION_KEY, o.display)
        Prefs.set(REMIND_UNTIL_KEY, remindUntil)
        phase = UpdatePhase.Later
    }

    fun lookAnyway(context: Context) {
        skipped = null
        remindVersion = null
        remindUntil = null
        listOf(SKIPPED_KEY, REMIND_VERSION_KEY, REMIND_UNTIL_KEY).forEach { Prefs.set(it, null) }
        reconcile(context)
    }

    fun checkedLine(): String {
        val at = lastSuccess ?: return ""
        val whenText = phrase(at)
        if (lastWasCurrent) return t("已是最新 · %s 检查过", whenText)
        return if (lastSuccessAuto) t("上次检查：%@ · 自动", whenText) else t("上次检查：%@", whenText)
    }

    fun offlineFine(): String {
        val at = lastSuccess
        if (at == null || !lastWasCurrent) return t("书和笔记不受影响。")
        return t("上次查到的还是%s，当时已是最新。书和笔记不受影响。", phrase(at))
    }

    private fun apkFile(context: Context, info: UpdateInfo) = File(File(context.cacheDir, "updates").apply { mkdirs() }, "yuyue-${info.version}.apk")

    private fun apkCandidates(primaryUrl: String): List<String> {
        val ghPrefix = "https://github.com/inokoru/yuyue/releases/download/"
        val cnbPrefix = "https://cnb.cool/inokoru/yuyue/-/releases/download/"
        val alt = when {
            primaryUrl.startsWith(cnbPrefix) -> ghPrefix + primaryUrl.removePrefix(cnbPrefix)
            primaryUrl.startsWith(ghPrefix) -> cnbPrefix + primaryUrl.removePrefix(ghPrefix)
            else -> null
        }
        return listOfNotNull(primaryUrl, alt).distinct()
    }

    /** 下载并安装: downloaded here, checked, then handed to the system's installer. */
    suspend fun download(context: Context) {
        val o = offer ?: return
        if (phase is UpdatePhase.Downloading) return
        val info = o.info
        apk = null
        phase = UpdatePhase.Downloading(0f)
        phase = try {
            withContext(Dispatchers.IO) {
                val out = apkFile(context, info)
                out.parentFile?.listFiles()?.forEach { it.delete() }
                val part = File(out.path + ".part")
                try {
                    var lastErr: Throwable? = null
                    var downloaded = false
                    for (apkUrl in apkCandidates(info.apk)) {
                        try {
                            part.delete()
                            http.newCall(Request.Builder().url(apkUrl).build()).execute().use { r ->
                                if (!r.isSuccessful) error("HTTP ${r.code}")
                                val body = r.body!!
                                val total = body.contentLength().takeIf { it > 0 } ?: info.size.toLong()
                                body.byteStream().use { input ->
                                    part.outputStream().use { output ->
                                        val buf = ByteArray(64 * 1024)
                                        var got = 0L
                                        var shown = 0f
                                        while (true) {
                                            val n = input.read(buf)
                                            if (n < 0) break
                                            output.write(buf, 0, n)
                                            got += n
                                            val f = if (total > 0) (got.toFloat() / total).coerceIn(0f, 1f) else 0f
                                            if (f - shown >= 0.01f) {
                                                shown = f
                                                withContext(Dispatchers.Main) { phase = UpdatePhase.Downloading(f) }
                                            }
                                        }
                                    }
                                }
                            }
                            downloaded = true
                            break
                        } catch (e: CancellationException) {
                            throw e
                        } catch (e: Throwable) {
                            lastErr = e
                        }
                    }
                    if (!downloaded && lastErr != null) throw lastErr
                    check(part.length() == info.size.toLong() && sha256(part) == info.sha256.lowercase()) { t("安装包不完整，请重新下载") }
                    check(sameSigner(context, part)) { t("安装包签名不对，不能安装") }
                    check((!out.exists() || out.delete()) && part.renameTo(out)) { t("读写文件时出了问题，再试一次") }
                } catch (e: Throwable) {
                    part.delete()
                    throw e
                }
                apk = out
                if (mayInstall(context)) UpdatePhase.Ready else UpdatePhase.NeedPermission
            }
        } catch (e: kotlinx.coroutines.CancellationException) {
            phase = UpdatePhase.Release
            throw e
        } catch (e: Exception) {
            UpdatePhase.DownloadFailed(e.message ?: t("网络断了。书和笔记不受影响。"))
        }
        if (phase == UpdatePhase.Ready) install(context)
    }

    /** Android 8+: allowed to install apps (设置 → 安装未知应用)? */
    fun mayInstall(context: Context): Boolean = context.packageManager.canRequestPackageInstalls()

    fun openInstallSettings(context: Context) {
        context.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}")).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    /** 稍后安装: the APK stays; 安装 asks again. */
    fun installLater() {
        if (apk != null) phase = UpdatePhase.Ready
    }

    /** Back from the system's settings or background: launch pending confirm or install if allowed now. */
    suspend fun onResume(context: Context) {
        if (launchPendingConfirmIfAny(context)) return
        if (phase == UpdatePhase.NeedPermission && mayInstall(context)) install(context)
    }

    fun launchPendingConfirmIfAny(context: Context): Boolean {
        val pending = pendingConfirmRef.getAndSet(null) ?: return false
        cancelInstallConfirmNotification(context)
        val launched = runCatching { context.startActivity(pending) }.isSuccess
        if (!launched) {
            pendingConfirmRef.compareAndSet(null, pending)
        }
        return launched
    }

    internal fun postInstallConfirmNotification(context: Context, confirm: Intent) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            return
        }
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        val channel = NotificationChannel(
            INSTALL_CHANNEL_ID,
            t("软件更新"),
            NotificationManager.IMPORTANCE_HIGH,
        )
        nm.createNotificationChannel(channel)
        val piFlags = PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        val pendingIntent = PendingIntent.getActivity(context, INSTALL_NOTIFY_ID, confirm, piFlags)
        val notification = Notification.Builder(context, INSTALL_CHANNEL_ID)
            .setSmallIcon(R.mipmap.ic_launcher_monochrome)
            .setContentTitle(t("新版本已就绪"))
            .setContentText(t("轻点以确认安装语阅更新"))
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .setCategory(Notification.CATEGORY_STATUS)
            .build()
        runCatching { nm.notify(INSTALL_NOTIFY_ID, notification) }
    }

    internal fun cancelInstallConfirmNotification(context: Context) {
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager ?: return
        runCatching { nm.cancel(INSTALL_NOTIFY_ID) }
    }

    /** Hands the APK to the system's installer, which asks the user and then replaces the app. */
    suspend fun install(context: Context) {
        val file = apk ?: return
        if (!mayInstall(context)) { phase = UpdatePhase.NeedPermission; return }
        if (!installing.compareAndSet(false, true)) return
        val expectedInfo = offer?.info
        phase = UpdatePhase.Ready
        try {
            val check = withContext(Dispatchers.IO) {
                val verified = UpdateVerifier.validateForInstall(
                    file = file,
                    expectedSize = expectedInfo?.size?.toLong(),
                    expectedSha256 = expectedInfo?.sha256,
                    verifySigner = { sameSigner(context, it) },
                )
                if (verified != UpdateVerifier.InstallCheckResult.Ok) return@withContext verified
                val installer = context.packageManager.packageInstaller
                val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
                    setAppPackageName(context.packageName)
                    if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
                }
                var sessionId = -1
                var committed = false
                try {
                    sessionId = installer.createSession(params)
                    installer.openSession(sessionId).use { session ->
                        file.inputStream().use { input ->
                            session.openWrite("yuyue.apk", 0, file.length()).use { out ->
                                input.copyTo(out)
                                session.fsync(out)
                            }
                        }
                        val flags = PendingIntent.FLAG_UPDATE_CURRENT or (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
                        val pending = PendingIntent.getBroadcast(context, sessionId, Intent(context, InstallResult::class.java), flags)
                        session.commit(pending.intentSender)
                        committed = true
                    }
                } catch (e: Throwable) {
                    if (sessionId >= 0 && !committed) {
                        runCatching { installer.abandonSession(sessionId) }
                    }
                    throw e
                }
                UpdateVerifier.InstallCheckResult.Ok
            }
            when (check) {
                UpdateVerifier.InstallCheckResult.Ok -> Unit
                UpdateVerifier.InstallCheckResult.MissingOrCorrupt -> {
                    apk = null
                    phase = UpdatePhase.DownloadFailed(t("安装包不完整，请重新下载"))
                }
                UpdateVerifier.InstallCheckResult.WrongSigner -> {
                    apk = null
                    phase = UpdatePhase.DownloadFailed(t("安装包签名不对，不能安装"))
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            if (!file.isFile) apk = null
            phase = UpdatePhase.DownloadFailed(e.message ?: t("读写文件时出了问题，再试一次"))
        } finally {
            installing.set(false)
        }
    }

    private fun sha256(f: File): String = UpdateVerifier.sha256(f)

    /** The new APK is signed by the same key as this app: nobody else's build gets in. */
    @Suppress("DEPRECATION")
    private fun sameSigner(context: Context, apk: File): Boolean {
        val pm = context.packageManager
        fun certs(info: android.content.pm.PackageInfo?): Set<String> {
            if (info == null) return emptySet()
            val sigs = if (Build.VERSION.SDK_INT >= 28) info.signingInfo?.apkContentsSigners?.toList().orEmpty() else info.signatures?.toList().orEmpty()
            return sigs.map { it.toCharsString() }.toSet()
        }
        val flag = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else PackageManager.GET_SIGNATURES
        val mine = certs(pm.getPackageInfo(context.packageName, flag))
        val theirs = certs(pm.getPackageArchiveInfo(apk.path, flag))
        return mine.isNotEmpty() && mine == theirs
    }

    internal fun failed(why: String) {
        phase = UpdatePhase.DownloadFailed(why)
    }

    /** 今天 9:30, 昨天 9:30, 11月20日 9:30. */
    fun phrase(millis: Long): String {
        val now = java.time.LocalDate.now()
        val date = java.time.Instant.ofEpochMilli(millis).atZone(java.time.ZoneId.systemDefault())
        val clock = "%d:%02d".format(date.hour, date.minute)
        return when (date.toLocalDate()) {
            now -> t("今天 %s", clock)
            now.minusDays(1) -> t("昨天 %s", clock)
            else -> t("%lld 月 %lld 日", date.monthValue, date.dayOfMonth) + " $clock"
        }
    }

    /** `2026-11-20` → `11月20日`. */
    fun monthDay(iso: String): String? {
        if (iso.length < 10) return null
        val m = iso.substring(5, 7).toIntOrNull() ?: return null
        val d = iso.substring(8, 10).toIntOrNull() ?: return null
        return t("%lld 月 %lld 日", m, d)
    }
}

/** Pure APK integrity & signer verification helpers (kept free of Prefs/Android init for JVM tests). */
internal object UpdateVerifier {
    enum class InstallCheckResult { Ok, MissingOrCorrupt, WrongSigner }

    /** 1-based release ordinal (oldest = 1) for `version` in a newest-first changelog. */
    fun editionOf(
        version: String,
        changelogVersions: List<String>,
        compare: (String, String) -> Int,
    ): Int? {
        if (changelogVersions.isEmpty()) return null
        val idx = changelogVersions.indexOfFirst { it == version }
        if (idx >= 0) return changelogVersions.size - idx
        if (compare(version, changelogVersions.first()) > 0) return changelogVersions.size + 1
        return null
    }

    fun sha256(f: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        f.inputStream().use { input ->
            val buf = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                md.update(buf, 0, n)
            }
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    fun validateCachedApk(
        file: File,
        expectedSize: Long,
        expectedSha256: String,
        verifySigner: (File) -> Boolean,
    ): Boolean {
        if (!file.isFile) return false
        val valid = file.length() == expectedSize &&
            sha256(file) == expectedSha256.lowercase() &&
            verifySigner(file)
        if (!valid) {
            file.delete()
        }
        return valid
    }

    fun validateForInstall(
        file: File,
        expectedSize: Long?,
        expectedSha256: String?,
        verifySigner: (File) -> Boolean,
    ): InstallCheckResult {
        if (!file.isFile) return InstallCheckResult.MissingOrCorrupt
        if (expectedSize != null && file.length() != expectedSize) {
            file.delete()
            return InstallCheckResult.MissingOrCorrupt
        }
        if (!expectedSha256.isNullOrEmpty() && sha256(file) != expectedSha256.lowercase()) {
            file.delete()
            return InstallCheckResult.MissingOrCorrupt
        }
        if (!verifySigner(file)) {
            file.delete()
            return InstallCheckResult.WrongSigner
        }
        return InstallCheckResult.Ok
    }
}

/** The installer's answer: ask the user when it must, report a failure. */
class InstallResult : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = if (Build.VERSION.SDK_INT >= 33) {
                    intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                } else {
                    intent.getParcelableExtra(Intent.EXTRA_INTENT)
                } ?: return
                confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                val launched = isAppInForeground(context) && runCatching { context.startActivity(confirm) }.isSuccess
                if (!launched) {
                    Updates.pendingConfirmIntent = confirm
                    Updates.postInstallConfirmNotification(context, confirm)
                }
            }
            PackageInstaller.STATUS_SUCCESS -> {
                Updates.pendingConfirmIntent = null
                Updates.cancelInstallConfirmNotification(context)
            }
            // 取消 in the system's dialog: the page stays, ready.
            PackageInstaller.STATUS_FAILURE_ABORTED -> {
                Updates.pendingConfirmIntent = null
                Updates.cancelInstallConfirmNotification(context)
            }
            else -> {
                Updates.pendingConfirmIntent = null
                Updates.cancelInstallConfirmNotification(context)
                Updates.failed(intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "")
            }
        }
    }

    private fun isAppInForeground(context: Context): Boolean {
        val am = context.getSystemService(Context.ACTIVITY_SERVICE) as? ActivityManager ?: return false
        val pid = Process.myPid()
        val processes = runCatching { am.runningAppProcesses }.getOrNull() ?: return false
        return processes.any {
            it.pid == pid && it.importance == ActivityManager.RunningAppProcessInfo.IMPORTANCE_FOREGROUND
        }
    }
}
