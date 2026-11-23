package app.yuyue.me

import android.content.Intent
import android.net.Uri
import android.text.format.Formatter
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
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
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import app.yuyue.YuyueApp
import app.yuyue.design.ButtonKind
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYField
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYRow
import app.yuyue.design.YYSegments
import app.yuyue.library.Library
import app.yuyue.reader.SwitchRow
import app.yuyue.shelf.Field
import app.yuyue.sync.Sync
import app.yuyue.sync.DriveAuth
import androidx.activity.result.IntentSenderRequest
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.SnapshotAbout
import uniffi.yuyue_core.SnapshotCounts
import uniffi.yuyue_core.SnapshotInfo
import uniffi.yuyue_core.SnapshotParts
import uniffi.yuyue_core.SyncPart
import java.io.File
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Date
import java.util.Locale

// MARK: 同步

/** 同步, as on iPhone: how it went, every device, where to meet (a WebDAV share
 *  on Android; iCloud is Apple's), and what goes. */
@Composable
fun SyncPage(sync: Sync) {
    var editing by remember { mutableStateOf(false) }
    var driving by remember { mutableStateOf(false) }
    var driveProblem by remember { mutableStateOf<String?>(null) }
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    fun connect() {
        scope.launch {
            try {
                withContext(Dispatchers.IO) { sync.connectDrive() }
            } catch (e: Exception) {
                driveProblem = Sync.message(e)
            }
            driving = false
        }
    }
    val allow = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { result ->
        if (runCatching { DriveAuth.token(context, result.data) }.isSuccess) connect() else driving = false
    }
    LaunchedEffect(Unit) { sync.loadDevices() }
    YYPage {
        SettingsLead(t("通过个人云盘在多台设备间同步"))
        Spacer(Modifier.height(YY.s(10)))
        StatusCard(sync)
        YYLabel(t("同步方式"))
        YYGroup {
            row {
                MethodRow(Icons.globe, "Google Drive", if (sync.method == "drive" && sync.on) sync.driveEmail ?: t("已连接") else t("用这台手机上的 Google 账号"),
                    on = sync.method == "drive" && sync.on, chevron = false, busy = driving) {
                    if (sync.method == "drive") return@MethodRow
                    driving = true
                    driveProblem = null
                    DriveAuth.authorize(context) { r ->
                        r.onSuccess { e ->
                            when (e) {
                                is DriveAuth.Either.Ask -> allow.launch(IntentSenderRequest.Builder(e.sender).build())
                                is DriveAuth.Either.Token -> connect()
                            }
                        }.onFailure { driving = false; driveProblem = t("Google 账号没连上（%@）", it.message ?: "") }
                    }
                }
            }
            row {
                MethodRow(Icons.server, "WebDAV", if (sync.method == "webdav" && sync.on) sync.webdav?.url?.let { Uri.parse(it).host } ?: "" else t("坚果云、群晖、Nextcloud…"),
                    on = sync.method == "webdav" && sync.on, chevron = true) { editing = true }
            }
        }
        YYNote(driveProblem ?: t("在其他设备上选择同一种方式即可跨设备同步；Google Drive 数据存放于应用专属目录。"), if (driveProblem != null) YY.c.danger else YY.c.t3)
        YYLabel(t("同步内容"))
        YYGroup {
            row { DetailSwitch(t("进度和书签"), null, sync.includes(SyncPart.READING)) { sync.include(SyncPart.READING, it) } }
            row { DetailSwitch(t("笔记和生词"), null, sync.includes(SyncPart.NOTES)) { sync.include(SyncPart.NOTES, it) } }
            row { DetailSwitch(t("设置和替换规则"), null, sync.includes(SyncPart.SETTINGS)) { sync.include(SyncPart.SETTINGS, it) } }
            row { DetailSwitch(t("书文件"), t("只在 Wi‑Fi 下传，占你自己网盘的空间"), sync.syncsFiles) { sync.syncFiles(it) } }
        }
        var stopping by remember { mutableStateOf(false) }
        if (sync.on) DestructiveCard(t("停止同步")) { stopping = true }
        if (stopping) ConfirmDialog(t("停止同步？"), t("网盘里已经同步过去的留着不动；以后这台手机上的改动不再同步。"), t("停止同步"), { sync.stop() }) { stopping = false }
        Row(Modifier.padding(start = YY.s(4), top = YY.s(10)), horizontalArrangement = Arrangement.spacedBy(YY.s(5)), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.lock, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
            Text(t("无需注册账号，数据仅在你的设备与个人网盘之间传输。"), style = YY.font(10f), color = YY.c.t3)
        }
    }
    if (editing) WebDAVSheet(sync) { editing = false }
}

@Composable
private fun StatusCard(sync: Sync) {
    val failed = sync.state is Sync.State.Failed
    val tint = YY.tint
    Column(
        Modifier.padding(top = YY.s(12)).fillMaxWidth().background(YY.c.card, RoundedCornerShape(YY.s(16))).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16)))
            .padding(start = YY.s(14), end = YY.s(14), top = YY.s(14), bottom = YY.s(if (sync.devices.isEmpty()) 14 else 2)),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
            // A tick on green, a turning arrow, ! on red; off, the cloud crossed out.
            val color = when {
                sync.state == Sync.State.Syncing -> tint.accent
                failed -> YY.c.danger
                sync.on -> YY.c.ok
                else -> YY.c.t3
            }
            // Syncing → ✓ or !: the colour and the mark cross over rather than flip.
            val shown by androidx.compose.animation.animateColorAsState(color, app.yuyue.design.YYMotion.standard(), label = "syncColor")
            Box(Modifier.size(YY.s(32)).background(shown.copy(alpha = 0.14f), CircleShape), contentAlignment = Alignment.Center) {
                val mark = when { sync.state == Sync.State.Syncing -> 0; failed -> 1; sync.on -> 2; else -> 3 }
                androidx.compose.animation.Crossfade(mark, animationSpec = app.yuyue.design.YYMotion.quick(), label = "syncMark") { m ->
                    if (m == 0) CircularProgressIndicator(Modifier.size(YY.s(14)), strokeWidth = 2.dp, color = shown)
                    else Icon(if (m == 1) Icons.bang else if (m == 2) Icons.done else Icons.cloudOff, null, tint = shown, modifier = Modifier.size(YY.s(15)))
                }
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                Text(
                    when {
                        sync.state == Sync.State.Syncing -> t("正在同步…")
                        failed -> t("同步未成功")
                        !sync.on -> t("尚未开启同步")
                        sync.lastSynced == null -> t("已连上")
                        else -> t("已同步")
                    },
                    style = YY.font(14f, FontWeight.Bold), color = if (failed) YY.c.danger else YY.c.t1,
                )
                Text(
                    (sync.state as? Sync.State.Failed)?.message
                        ?: if (!sync.on) t("选择下方任一方式，跨设备同步进度、笔记与设置")
                        else listOfNotNull(sync.lastSynced?.let { ago(it) }, if (sync.method == "drive") "Google Drive" else "WebDAV").joinToString(" · "),
                    style = YY.font(10f), color = YY.c.t3, maxLines = 2,
                )
            }
            if (sync.on) YYButton(t("立即同步"), ButtonKind.Plain, ButtonSize.Small, enabled = sync.state != Sync.State.Syncing) { sync.now() }
        }
        if (sync.devices.isNotEmpty()) Column(Modifier.padding(top = YY.s(12))) {
            for (d in sync.devices) {
                Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                Row(Modifier.fillMaxWidth().height(YY.s(34)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(9))) {
                    Icon(when (d.platform.lowercase()) { "mac", "macos" -> Icons.laptop; "ios", "iphone", "ipad" -> Icons.iphone; else -> Icons.phone }, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(14)))
                    val label = if (d.thisDevice) {
                        if (d.name.isEmpty() || d.name.equals("Android", ignoreCase = true) || d.name.startsWith("sdk_", ignoreCase = true)) t("这台 %@", t("手机"))
                        else t("%@ · 本机", d.name)
                    } else d.name.ifEmpty { t("设备") }
                    Text(label, style = YY.font(11f), color = YY.c.t1, maxLines = 1, modifier = Modifier.weight(1f))
                    Text(if (d.thisDevice) t("刚刚") else d.lastAt?.let { ago(it) } ?: t("还没同步过"), style = YY.font(10f), color = YY.c.t3)
                }
            }
        }
    }
}

/** A way to sync: a small icon tile, the name and a grey line, a tick on the one in use. */
@Composable
private fun MethodRow(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, detail: String, on: Boolean, chevron: Boolean, busy: Boolean = false, onClick: () -> Unit) {
    val tint = YY.tint
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        Box(Modifier.size(YY.s(26)).background(if (on) tint.accentSoft else YY.c.inset, RoundedCornerShape(YY.s(7))), contentAlignment = Alignment.Center) {
            Icon(icon, null, tint = if (on) tint.accent else YY.c.t1, modifier = Modifier.size(YY.s(14)))
        }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
            Text(title, style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1)
            Text(detail, style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
        }
        when {
            busy -> CircularProgressIndicator(Modifier.size(YY.s(14)), strokeWidth = 2.dp, color = tint.accent)
            on -> Icon(Icons.check, null, tint = tint.accent, modifier = Modifier.size(YY.s(13)))
            chevron -> Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
        }
    }
}

/** "刚刚" · "5 分钟前" · "3 小时前" · "10 月 2 日". */
fun ago(ms: Long): String {
    val s = (System.currentTimeMillis() - ms) / 1000
    return when {
        s < 60 -> t("刚刚")
        s < 3600 -> t("%lld 分钟前", s / 60)
        s < 86400 -> t("%lld 小时前", s / 3600)
        else -> day(ms)
    }
}

fun day(ms: Long): String {
    val c = Calendar.getInstance().apply { timeInMillis = ms }
    return t("%lld 月 %lld 日", c.get(Calendar.MONTH) + 1, c.get(Calendar.DAY_OF_MONTH))
}

/** WebDAV: 地址, 账号, 密码, 文件夹; 连接 checks it can be reached and written. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun WebDAVSheet(sync: Sync, dismiss: () -> Unit) {
    val a = sync.webdav
    var url by remember { mutableStateOf(a?.url ?: "") }
    var user by remember { mutableStateOf(a?.user ?: "") }
    var password by remember { mutableStateOf(a?.password ?: "") }
    var folder by remember { mutableStateOf(a?.folder ?: t("语阅")) }
    var checking by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
            Text("WebDAV", style = YY.font(14f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(start = 4.dp, bottom = 10.dp))
            YYGroup {
                row { YYField(t("地址"), 64f) { Field(url, "https://dav.jianguoyun.com/dav/") { url = it } } }
                row { YYField(t("账号"), 64f) { Field(user, t("邮箱或用户名")) { user = it } } }
                row { YYField(t("密码"), 64f) { Secret(password, t("坚果云填应用密码")) { password = it } } }
                row { YYField(t("文件夹"), 64f) { Field(folder, t("语阅")) { folder = it } } }
            }
            YYNote(problem ?: t("密码存在这台设备上，加密保存。"), if (problem != null) YY.c.danger else YY.c.t3)
            Spacer(Modifier.height(18.dp))
            YYButton(if (checking) t("正在连接…") else t("连接"), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = !checking && url.startsWith("http")) {
                checking = true
                problem = null
                scope.launch {
                    try {
                        withContext(Dispatchers.IO) { sync.connect(Sync.WebDAVAccount(url.trim(), user.trim(), password, folder.trim('/', ' '))) }
                        dismiss()
                    } catch (e: Exception) {
                        problem = Sync.message(e)
                    }
                    checking = false
                }
            }
        }
    }
}

@Composable
fun Secret(value: String, placeholder: String, onChange: (String) -> Unit) {
    androidx.compose.foundation.text.BasicTextField(
        value, onChange, singleLine = true,
        textStyle = YY.font(11.5f).copy(color = YY.c.t1),
        visualTransformation = androidx.compose.ui.text.input.PasswordVisualTransformation(),
        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Password),
        cursorBrush = androidx.compose.ui.graphics.SolidColor(YY.tint.accent),
        decorationBox = { inner ->
            if (value.isEmpty()) Text(placeholder, style = YY.font(11.5f), color = YY.c.t3)
            inner()
        },
        modifier = Modifier.fillMaxWidth(),
    )
}

// MARK: 数据快照

private class Saved(val file: File, val about: SnapshotAbout)

private fun snapshotsDir() = File(YuyueApp.instance.filesDir, "snapshots").apply { mkdirs() }

private fun size(bytes: Long) = Formatter.formatShortFileSize(YuyueApp.instance, bytes)

private fun when_(ms: Long): String = SimpleDateFormat(if (L10nChinese()) "M 月 d 日 HH:mm" else "MMM d, HH:mm", Locale.getDefault()).format(Date(ms))

private fun L10nChinese() = app.yuyue.L10n.chinese

private fun snapshotName(note: String? = null) =
    t("语阅快照-%@%@.yuyue", note?.let { "$it-" } ?: "", SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US).format(Date()))

/** 数据, as the Mac's: what goes in and how big, 生成快照, 导入快照, and the ones
 *  made before, to send on. The same .yuyue file moves between iPhone, Mac and Android. */
@Composable
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
fun SnapshotPage(library: Library) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var counts by remember { mutableStateOf<SnapshotCounts?>(null) }
    var saved by remember { mutableStateOf<List<Saved>>(emptyList()) }
    var parts by remember { mutableStateOf(SnapshotParts(true, true, true, true, false)) }
    var making by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    var importing by remember { mutableStateOf<Pair<File, SnapshotInfo>?>(null) }
    var version by remember { mutableIntStateOf(0) }
    var saving by remember { mutableStateOf<File?>(null) }
    var exporting by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf<File?>(null) }

    LaunchedEffect(version) {
        withContext(Dispatchers.IO) {
            counts = runCatching { library.core.snapshotCounts() }.getOrNull()
            saved = (snapshotsDir().listFiles() ?: emptyArray()).filter { it.extension == "yuyue" }
                .mapNotNull { f -> runCatching { Saved(f, library.core.peekSnapshot(f.path)) }.getOrNull() }
                .sortedByDescending { it.about.madeAt }
        }
    }
    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        uri ?: return@rememberLauncherForActivityResult
        scope.launch {
            try {
                importing = withContext(Dispatchers.IO) { copyIn(uri)?.let { f -> f to library.core.readSnapshot(f.path) } }
            } catch (e: Exception) {
                library.alert = Library.message(e)
            }
        }
    }
    val saver = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/octet-stream")) { uri ->
        val f = saving
        saving = null
        if (uri == null || f == null) return@rememberLauncherForActivityResult
        scope.launch(Dispatchers.IO) {
            context.contentResolver.openOutputStream(uri)?.use { out -> f.inputStream().use { it.copyTo(out) } }
        }
    }

    YYPage {
        SettingsLead(t("本地备份快照与阅读记录导出"))
        YYLabel(t("生成快照"))
        val c = counts
        YYGroup {
            // As on Apple: each kind, how many there are in grey, its switch.
            row { YYRow(t("进度和书签"), value = c?.let { t("%lld 本", it.read.toInt()) }, trailing = { SettingSwitch(parts.progress, { parts = parts.copy(progress = it) }) }) }
            row { YYRow(t("笔记"), value = c?.let { t("%lld 条", it.notes.toInt()) }, trailing = { SettingSwitch(parts.notes, { parts = parts.copy(notes = it) }) }) }
            row { YYRow(t("生词"), value = c?.let { t("%lld 个词", it.words.toInt()) }, trailing = { SettingSwitch(parts.words, { parts = parts.copy(words = it) }) }) }
            row { YYRow(t("设置和替换规则"), trailing = { SettingSwitch(parts.settings, { parts = parts.copy(settings = it) }) }) }
            row { YYRow(t("书文件"), value = c?.let { size(it.fileBytes.toLong()) }, trailing = { SettingSwitch(parts.files, { parts = parts.copy(files = it) }) }) }
            row {
                Row(Modifier.fillMaxWidth().padding(vertical = YY.s(9)), verticalAlignment = Alignment.CenterVertically) {
                    val estimate = c?.let { (it.dataBytes.toDouble() * 0.15).toLong() + if (parts.files) it.fileBytes.toLong() else 0 }
                    Text(androidx.compose.ui.text.buildAnnotatedString {
                        append(if (parts.files) t("含书文件 · 约 ") else t("不含书文件 · 约 "))
                        pushStyle(androidx.compose.ui.text.SpanStyle(fontWeight = FontWeight.SemiBold, color = YY.c.t1)); append(estimate?.let(::size) ?: "…"); pop()
                    }, style = YY.font(10.5f, tnum = true), color = YY.c.t3, modifier = Modifier.weight(1f))
                    YYButton(t("导入…"), ButtonKind.Plain, modifier = Modifier.padding(end = YY.s(8))) { picker.launch(arrayOf("*/*")) }
                    YYButton(if (making) "…" else t("生成快照"), ButtonKind.Main, enabled = !making && (parts.progress || parts.notes || parts.words || parts.settings || parts.files)) {
                        making = true
                        problem = null
                        scope.launch {
                            try {
                                withContext(Dispatchers.IO) { library.core.makeSnapshot(File(snapshotsDir(), snapshotName()).path, parts) }
                                version++
                                library.note(Library.Toast.Icon.Check, t("已生成快照"), "")
                            } catch (e: Exception) {
                                problem = Library.message(e)
                            }
                            making = false
                        }
                    }
                }
            }
            problem?.let { p -> row { YYRow(p, Icons.error, titleColor = YY.c.danger) } }
        }
        Spacer(Modifier.height(10.dp))
        YYGroup {
            row {
                Row(Modifier.fillMaxWidth().clickable { exporting = true }, verticalAlignment = Alignment.CenterVertically) {
                    DetailRow(t("导出阅读记录"), t("导出想读、在读、读完状态与完成日期（CSV），可导入 Goodreads 或 StoryGraph"), Modifier.weight(1f))
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                }
            }
        }
        YYNote(t("快照为单个 .yuyue 文件，在 Mac、iPhone 与 Android 上打开即可恢复书库。"))
        if (saved.isNotEmpty()) {
            YYLabel(t("历史快照"))
            YYGroup {
                for (s in saved) row {
                    Row(Modifier.fillMaxWidth().padding(vertical = YY.s(9)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                        Column(Modifier.weight(1f).clickable { scope.launch { runCatching { importing = s.file to withContext(Dispatchers.IO) { library.core.readSnapshot(s.file.path) } } } }) {
                            Text(when_(s.about.madeAt), style = YY.font(11.5f, FontWeight.Medium, tnum = true), color = YY.c.t1)
                            Text(listOfNotNull(from(s.about.deviceName), if (s.about.parts.files) t("含书文件") else null, size(s.about.bytes.toLong())).joinToString(" · "), style = YY.font(10f, tnum = true), color = YY.c.t3, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                        }
                        // As on iPhone: send it on; long-press for the rest.
                        var menu by remember { mutableStateOf(false) }
                        Box {
                            Box(
                                Modifier.size(40.dp).clip(androidx.compose.foundation.shape.CircleShape)
                                    .combinedClickable(onClickLabel = t("分享"), onLongClick = { menu = true }) { share(s.file) },
                                contentAlignment = Alignment.CenterEnd,
                            ) {
                                Box(Modifier.size(YY.s(28)).background(YY.c.inset, androidx.compose.foundation.shape.CircleShape), contentAlignment = Alignment.Center) {
                                    Icon(Icons.share, t("分享"), tint = YY.c.t1, modifier = Modifier.size(YY.s(15)))
                                }
                            }
                            androidx.compose.material3.DropdownMenu(menu, { menu = false }, containerColor = YY.c.card) {
                                app.yuyue.shelf.Item(t("导入这份"), Icons.squareArrowDown) { menu = false; scope.launch { runCatching { importing = s.file to withContext(Dispatchers.IO) { library.core.readSnapshot(s.file.path) } } } }
                                app.yuyue.shelf.Item(t("存到…"), Icons.share) { menu = false; saving = s.file; saver.launch(s.file.name) }
                                app.yuyue.shelf.Item(t("删除"), Icons.delete, danger = true) { menu = false; deleting = s.file }
                            }
                        }
                    }
                }
            }
            YYNote(t("长按分享键可以导入、存到别处或删除。"))
        }
    }
    importing?.let { (file, info) -> SnapshotImportSheet(library, file, info, { importing = null; version++ }) }
    if (exporting) ReadingExportSheet(library) { exporting = false }
    deleting?.let { f -> ConfirmDialog(t("删除这份快照？"), t("删了就找不回来；已经发出去、存到别处的不受影响。"), t("删除"), { f.delete(); version++ }) { deleting = null } }
}

/** 导出阅读记录: the shelf as a CSV for Goodreads or StoryGraph — how many of each, the first lines
 *  of the file, and a word on the books without an ISBN. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ReadingExportSheet(library: Library, dismiss: () -> Unit) {
    var site by remember { mutableStateOf(uniffi.yuyue_core.ReadingSite.GOODREADS) }
    var export by remember { mutableStateOf<uniffi.yuyue_core.ReadingExport?>(null) }
    LaunchedEffect(site) {
        val offset = java.util.TimeZone.getDefault().getOffset(System.currentTimeMillis()) / 60_000
        export = withContext(Dispatchers.IO) { runCatching { library.core.readingExport(site, offset) }.getOrNull() }
    }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
            Text(t("导出阅读记录"), style = YY.font(14f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(start = 4.dp, bottom = 12.dp))
            YYSegments(listOf(uniffi.yuyue_core.ReadingSite.GOODREADS to "Goodreads", uniffi.yuyue_core.ReadingSite.STORY_GRAPH to "StoryGraph"), site, { site = it }, modifier = Modifier.fillMaxWidth())
            Spacer(Modifier.height(YY.s(10)))
            val e = export
            YYGroup {
                row { YYRow(t("想读"), Icons.bookmarkOutline, t("%lld 本", (e?.want ?: 0u).toInt())) }
                row { YYRow(t("在读"), Icons.book, t("%lld 本", (e?.reading ?: 0u).toInt())) }
                row { YYRow(t("读完"), Icons.checkCircle, t("%lld 本 · 带读完日期", (e?.finished ?: 0u).toInt())) }
            }
            Column(Modifier.padding(top = YY.s(14)).fillMaxWidth().background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(YY.s(12)), verticalArrangement = Arrangement.spacedBy(YY.s(3))) {
                (e?.csv ?: "").lineSequence().take(4).forEachIndexed { i, line ->
                    Text(line, style = YY.font(9.5f, if (i == 0) FontWeight.Bold else FontWeight.Normal).copy(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace),
                        color = if (i == 0) YY.c.t1 else YY.c.t2, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                }
            }
            if (e != null && e.withoutIsbn > 0u) YYNote(t("没有 ISBN 的 %lld 本按书名和作者对，导进去后可能要在网站上确认一下。", e.withoutIsbn.toInt()))
            Spacer(Modifier.height(YY.s(16)))
            val n = e?.let { (it.want + it.reading + it.finished).toInt() } ?: 0
            YYButton(if (e == null) t("导出 · CSV") else t("导出 %lld 本 · CSV", n), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = e != null && n > 0) {
                val made = export ?: return@YYButton
                val dir = File(YuyueApp.instance.cacheDir, "export").apply { mkdirs() }
                val file = File(dir, if (site == uniffi.yuyue_core.ReadingSite.GOODREADS) t("语阅-goodreads.csv") else t("语阅-storygraph.csv"))
                file.writeText(made.csv)
                val ctx = YuyueApp.instance
                val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", file)
                ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/csv").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), null).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }
        }
    }
}

private fun from(device: String) = if (device == Library.deviceName(YuyueApp.instance)) t("这台 %@", t("手机")) else device

private fun share(file: File) {
    val ctx = YuyueApp.instance
    val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", file)
    ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("application/octet-stream").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION), null).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
}

/** A picked snapshot copied next to the app, to read. */
fun copyIn(uri: Uri): File? {
    val ctx = YuyueApp.instance
    val f = File(ctx.cacheDir, "picked.yuyue")
    ctx.contentResolver.openInputStream(uri)?.use { input -> f.outputStream().use { input.copyTo(it) } } ?: return null
    return f
}

/** 导入快照: where it's from and what it holds, 合并 or 覆盖, and what that does. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SnapshotImportSheet(library: Library, file: File, info: SnapshotInfo, dismiss: () -> Unit) {
    var replace by remember { mutableStateOf(false) }
    var working by remember { mutableStateOf(false) }
    var problem by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
            Text(t("导入快照"), style = YY.font(14f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(start = 4.dp, bottom = 12.dp))
            Column(Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(16.dp)).padding(12.dp)) {
                Text(t("来自 %@ · %@ · %@", from(info.deviceName), when_(info.madeAt), size(info.bytes.toLong())), style = YY.font(10.5f, tnum = true), color = YY.c.t2)
                val c = info.counts
                val tags = listOfNotNull(
                    if (info.parts.progress) t("进度") + " " + t("%lld 本", c.read.toInt()) else null,
                    if (info.parts.notes) t("笔记") + " " + t("%lld 条", c.notes.toInt()) else null,
                    if (info.parts.words) t("生词") + " " + t("%lld 个词", c.words.toInt()) else null,
                    if (info.parts.settings) t("设置") else null,
                    if (info.parts.files) t("书文件") + " " + size(c.fileBytes.toLong()) else null,
                )
                Text(tags.joinToString(" · "), style = YY.font(11f, FontWeight.Medium, tnum = true), color = YY.c.t1, modifier = Modifier.padding(top = 8.dp))
            }
            Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Choice(t("合并"), t("这台设备上的都留着，加上快照里没有的；同一条取较新的。"), !replace, Modifier.weight(1f)) { replace = false }
                Choice(t("覆盖"), t("这台设备上的全部换成快照里的。换之前先自动备份一份。"), replace, Modifier.weight(1f)) { replace = true }
            }
            Text(problem ?: change(info, replace), style = YY.font(10.5f), color = if (problem == null) YY.c.t2 else YY.c.danger, modifier = Modifier.fillMaxWidth().padding(top = 12.dp))
            Spacer(Modifier.height(12.dp))
            val empty = !replace && info.merged == info.now && !info.parts.settings && !info.parts.files
            YYButton(if (working) "…" else t("导入"), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = !working && !empty) {
                working = true
                scope.launch {
                    try {
                        withContext(Dispatchers.IO) {
                            if (replace) library.core.makeSnapshot(File(snapshotsDir(), snapshotName(t("覆盖前"))).path, SnapshotParts(true, true, true, true, false))
                            library.core.restoreSnapshot(file.path, replace)
                        }
                        library.reload()
                        library.notesChanged()
                        library.note(Library.Toast.Icon.Check, t("快照导入好了"), "")
                        dismiss()
                    } catch (e: Exception) {
                        problem = Library.message(e)
                    }
                    working = false
                }
            }
        }
    }
}

@Composable
private fun Choice(title: String, detail: String, on: Boolean, modifier: Modifier, onClick: () -> Unit) {
    Column(
        modifier.background(YY.c.card, RoundedCornerShape(14.dp)).border(if (on) 1.5.dp else 0.5.dp, if (on) YY.tint.accent else YY.c.line, RoundedCornerShape(14.dp))
            .clickable(onClick = onClick).padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(title, style = YY.font(12f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.weight(1f))
            if (on) Icon(Icons.checkCircle, null, tint = YY.tint.accent, modifier = Modifier.size(16.dp))
        }
        Text(detail, style = YY.font(10f), color = YY.c.t2)
    }
}

/** What 合并 or 覆盖 would do to the counts that change, in a line (as on Apple). */
private fun change(info: SnapshotInfo, replace: Boolean): String {
    val after = if (replace) info.replaced else info.merged
    if (after == info.now && !replace) return t("这份快照里的，这台设备上都已经有了")
    val parts = mutableListOf<String>()
    if (info.parts.notes) parts += t("笔记 %@ → %@ 条", "${info.now.notes}", "${after.notes}")
    if (info.parts.words) parts += t("生词 %@ → %@ 个", "${info.now.words}", "${after.words}")
    if (parts.isEmpty() || after.books != info.now.books) parts.add(0, t("书 %@ → %@ 本", "${info.now.books}", "${after.books}"))
    return (if (replace) t("覆盖后：") else t("合并后：")) + parts.joinToString(t("，"))
}
