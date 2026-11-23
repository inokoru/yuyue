package app.yuyue.update

import android.content.Intent
import android.net.Uri
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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import app.yuyue.BuildConfig
import app.yuyue.L10n
import app.yuyue.design.ButtonKind
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.t
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.ZoneId

/** 关于 › 检查更新. The shelf stays clear; the red dot on 我的 and 关于 leads here. */
@Composable
fun UpdateScreen() {
    val u = Updates
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val life = LocalLifecycleOwner.current.lifecycle
    DisposableEffect(life) {
        val observer = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_RESUME) u.onResume(context) }
        life.addObserver(observer)
        onDispose { life.removeObserver(observer) }
    }
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = YY.s(16)).padding(bottom = YY.s(28)),
    ) {
        // checking → a release → downloading → ready: each fades in where the last one was.
        androidx.compose.animation.AnimatedContent(u.phase, transitionSpec = { app.yuyue.design.YYMotion.swap() }, contentKey = { it::class }, label = "update") { phase ->
        when (phase) {
            UpdatePhase.Checking -> Status(checking = true)
            UpdatePhase.CheckFailed -> FailedCheck()
            UpdatePhase.Skipped -> Skipped()
            UpdatePhase.Later -> Later()
            UpdatePhase.Release -> u.offer?.let { Release(it) { scope.launch { u.download(context) } } } ?: Status(checking = false)
            is UpdatePhase.Downloading -> Progress(phase.done)
            is UpdatePhase.DownloadFailed -> FailedDownload(phase.why) { scope.launch { u.download(context) } }
            UpdatePhase.Ready -> Ready { u.install(context) }
            UpdatePhase.NeedPermission -> Permission({ u.openInstallSettings(context) }, { u.installLater() })
            else -> Status(checking = false)
        }
        }
    }
}

@Composable
private fun Release(offer: UpdateOffer, download: () -> Unit) {
    val day = Updates.monthDay(offer.info.date)?.let { " · $it" } ?: ""
    Hero(kicker = t("新版本"), version = offer.display, meta = t("从 %@%@", BuildConfig.VERSION_NAME, day) + " · ${megabytes(offer.size)}")
    NotesCard(offer)
    YYButton(t("下载并安装"), ButtonKind.Main, wide = true, modifier = Modifier.padding(top = YY.s(14))) { download() }
    Row(Modifier.fillMaxWidth().padding(top = YY.s(8)), horizontalArrangement = Arrangement.Center) {
        Quiet(t("稍后提醒")) { Updates.remindLater() }
        Quiet(t("跳过此版本")) { Updates.skip() }
    }
    Text(t("下好后核对签名，再交给系统安装。若还不允许安装未知应用，会先去系统设置打开。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
}

@Composable
private fun Progress(done: Float) {
    val o = Updates.offer
    Head(t("正在下载"), o?.display ?: "")
    Bar(done, o?.size ?: 0)
    Text(t("下完核对签名，再交给系统安装。书和笔记不受影响。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
}

@Composable
private fun FailedDownload(why: String, retry: () -> Unit) {
    val o = Updates.offer
    Head(t("没下完"), o?.display ?: "", danger = true)
    Text(why.ifEmpty { t("网络断了。书和笔记不受影响。") }, style = YY.font(12.5f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(14)))
    YYButton(t("重试"), ButtonKind.Main, wide = true, modifier = Modifier.padding(top = YY.s(14))) { retry() }
    Text(t("GitHub 在这边有时打不开，或下到一半停住。语阅不内置镜像。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
}

@Composable
private fun Ready(install: () -> Unit) {
    val o = Updates.offer
    Head(t("已下载"), o?.display ?: "")
    Bar(1f, o?.size ?: 0)
    Text(t("签名核对过了。交给系统安装，安装那一屏是系统的。"), style = YY.font(12.5f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(8)))
    YYButton(t("安装"), ButtonKind.Main, wide = true, modifier = Modifier.padding(top = YY.s(14))) { install() }
}

@Composable
private fun Permission(openSettings: () -> Unit, later: () -> Unit) {
    Hero(kicker = t("先允许安装"), version = t("未知应用"), versionPx = 32f, meta = null)
    Text(t("这一版已经下好。系统还不允许语阅安装应用，所以先去打开「允许来自此来源」。装完可以再关掉。"), style = YY.font(14.5f), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(16)))
    Note(t("打开的是系统设置，不是语阅的页面。允许之后，回到这里，把安装交给系统。"))
    YYButton(t("去系统设置打开"), ButtonKind.Main, wide = true, modifier = Modifier.padding(top = YY.s(14))) { openSettings() }
    Ghost(t("稍后安装"), wide = true) { later() }
    Text(t("安装包留在这台手机上，不用重新下。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
}

@Composable
private fun Status(checking: Boolean) {
    val u = Updates
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        Mark(64)
        Text(t("语阅"), style = YY.font(17f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(8)))
        Text(u.currentLabel, style = YY.font(12.5f), color = YY.c.t3)
        if (checking) Text(t("正在检查更新…"), style = YY.font(12.5f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(8)))
        else if (u.lastWasCurrent && u.lastSuccess != null) Text(u.checkedLine(), style = YY.font(13f, FontWeight.SemiBold), color = YY.c.ok, modifier = Modifier.padding(top = YY.s(8)))
    }
    YYButton(if (checking) t("正在检查…") else t("检查更新"), ButtonKind.Main, wide = true, enabled = !checking, modifier = Modifier.padding(top = YY.s(14))) {
        scope.launch { u.checkNow(context, byHand = true) }
    }
    Toggles()
    Text(t("更新检查仅获取版本信息，不上传任何本地数据。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
}

@Composable
private fun FailedCheck() {
    val u = Updates
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally) {
        Mark(64)
        Text(t("语阅"), style = YY.font(17f, FontWeight.Bold), color = YY.c.t1, modifier = Modifier.padding(top = YY.s(8)))
        Text(u.currentLabel, style = YY.font(12.5f), color = YY.c.t3)
        Text(t("无法检查更新，请检查网络连接后重试。"), style = YY.font(13f, FontWeight.SemiBold), color = YY.c.danger, textAlign = TextAlign.Center, modifier = Modifier.padding(top = YY.s(8)))
    }
    YYButton(t("重试"), ButtonKind.Main, wide = true, modifier = Modifier.padding(top = YY.s(14))) { scope.launch { u.checkNow(context, byHand = true) } }
    Text(u.offlineFine(), style = YY.font(11.5f), color = YY.c.t3, textAlign = TextAlign.Center, modifier = Modifier.padding(top = YY.s(10)).fillMaxWidth())
    Toggles()
}

@Composable
private fun Skipped() {
    val u = Updates
    val context = LocalContext.current
    Card {
        Row {
            Text(u.offer?.display ?: "", fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = 22.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(t("已跳过"), style = YY.font(12f), color = YY.c.t3)
        }
        Text(t("红点不再为这一版本亮起，有新版本时仍会提醒（仅保存在本机）。"), style = YY.font(13f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(8)))
        YYButton(t("查看此版本"), ButtonKind.Main, modifier = Modifier.padding(top = YY.s(8))) { u.lookAnyway(context) }
    }
    Fact(t("当前版本"), u.currentLabel, first = true)
    Fact(t("上次检查"), u.lastSuccess?.let { Updates.phrase(it) } ?: t("尚未检查"))
    Toggles()
}

@Composable
private fun Later() {
    val u = Updates
    val context = LocalContext.current
    val day = u.remindUntil?.let { Instant.ofEpochMilli(it).atZone(ZoneId.systemDefault()).let { z -> t("%lld 月 %lld 日", z.monthValue, z.dayOfMonth) } } ?: ""
    Card {
        Row {
            Text(u.offer?.display ?: "", fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = 22.sp, color = YY.c.t1, modifier = Modifier.weight(1f))
            Text(day, style = YY.font(12f), color = YY.c.t3)
        }
        Text(t("你选了稍后。到那天之前，红点不出现。想现在看，从这里进。"), style = YY.font(13f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(8)))
        Row(Modifier.padding(top = YY.s(8))) {
            YYButton(t("现在查看"), ButtonKind.Main) { u.lookAnyway(context) }
            Spacer(Modifier.width(8.dp))
            Quiet(t("不再提醒")) { u.skip() }
        }
    }
    Text(t("「稍后」是 3 天。日子只记在这台设备上，不进同步。"), style = YY.font(11.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(10)))
    Toggles()
}

@Composable
private fun Toggles() {
    val u = Updates
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    Column(
        Modifier.padding(top = YY.s(12)).clip(RoundedCornerShape(YY.s(16))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16))).padding(horizontal = YY.s(14)),
    ) {
        Row(Modifier.fillMaxWidth().padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(t("自动检查"), style = YY.font(15f, FontWeight.Medium), color = YY.c.t1)
                Text(t("每天启动时自动检查；无新版本时不提醒"), style = YY.font(11.5f), color = YY.c.t3)
            }
            Switch(u.autoOn, { on -> u.setAuto(context, on) { work -> scope.launch { work() } } }, colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = Color.White))
        }
        HorizontalDivider(color = YY.c.line)
        var choosing by remember { mutableStateOf(false) }
        Row(Modifier.fillMaxWidth().clickable { choosing = true }.padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(t("更新渠道"), style = YY.font(15f, FontWeight.Medium), color = YY.c.t1)
                Text(UpdateChannel.detail, style = YY.font(11.5f), color = YY.c.t3)
            }
            Box(Modifier.padding(start = YY.s(10))) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(u.channel.title, style = YY.font(14f), color = YY.c.t2)
                    Icon(Icons.chevronDown, null, tint = YY.c.t3, modifier = Modifier.padding(start = 2.dp).size(18.dp))
                }
                DropdownMenu(choosing, { choosing = false }, containerColor = YY.c.card) {
                    for (c in UpdateChannel.entries) {
                        DropdownMenuItem(
                            text = { Text(c.title, color = YY.c.t1, fontSize = 15.sp) },
                            trailingIcon = { if (c == u.channel) Icon(Icons.check, null, tint = YY.c.t1, modifier = Modifier.size(18.dp)) },
                            onClick = { choosing = false; u.setChannel(context, c) { work -> scope.launch { work() } } },
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun Hero(kicker: String?, version: String?, meta: String?, versionPx: Float = 42f) {
    Column(Modifier.fillMaxWidth().padding(top = YY.s(8)), horizontalAlignment = Alignment.CenterHorizontally) {
        Mark(64)
        if (kicker != null) Text(kicker, style = YY.font(12f, FontWeight.SemiBold), color = YY.tint.accent, letterSpacing = 2.sp, modifier = Modifier.padding(top = YY.s(12)))
        if (version != null) Text(version, fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = versionPx.sp, color = YY.c.t1, modifier = Modifier.padding(top = YY.s(4)))
        if (meta != null) Text(meta, style = YY.font(12.5f), color = YY.c.t3, modifier = Modifier.padding(top = YY.s(6)))
    }
}

@Composable
private fun Head(kicker: String, version: String, danger: Boolean = false) {
    Row(Modifier.padding(top = YY.s(8)), verticalAlignment = Alignment.CenterVertically) {
        Mark(44)
        Column(Modifier.padding(start = YY.s(12))) {
            Text(kicker, style = YY.font(12f, FontWeight.SemiBold), color = if (danger) YY.c.danger else YY.tint.accent, letterSpacing = 1.5.sp)
            Text(version, fontFamily = YY.serif, fontWeight = FontWeight.SemiBold, fontSize = 32.sp, color = YY.c.t1)
        }
    }
}

/** Every version since this one, newest first: skipped a few, each under its number. */
@Composable
private fun NotesCard(offer: UpdateOffer) {
    val paper = if (YY.dark) Color(0xFF1B1A17) else Color(0xFFF5F4ED)
    val ink = if (YY.dark) Color(0xFFE3DED0) else Color(0xFF26251F)
    val ink2 = if (YY.dark) Color(0xFF8C8779) else Color(0xFF716E62)
    Column(
        Modifier.padding(top = YY.s(18)).fillMaxWidth().clip(RoundedCornerShape(YY.s(18))).background(paper).border(0.5.dp, ink.copy(alpha = 0.14f), RoundedCornerShape(YY.s(18))).padding(YY.s(18)),
    ) {
        val many = offer.releases.size > 1
        val notes = offer.releases.map { r -> r to (if (L10n.chinese) r.notesZh.ifEmpty { r.notesEn } else r.notesEn.ifEmpty { r.notesZh }) }
        if (notes.all { it.second.isEmpty() }) Text(t("这一版的说明写在发布页上。"), fontFamily = YY.serif, fontSize = 16.5.sp, lineHeight = 28.sp, color = ink)
        notes.forEachIndexed { i, (r, items) ->
            if (many) Text(Updates.monthDay(r.date)?.let { "${r.version} · $it" } ?: r.version, style = YY.font(11f, FontWeight.SemiBold, tnum = true), color = YY.tint.accent, letterSpacing = 2.sp, modifier = Modifier.padding(top = if (i == 0) 0.dp else YY.s(16), bottom = YY.s(6)))
            for (item in items) {
                Row(Modifier.padding(vertical = 3.dp)) {
                    Box(Modifier.padding(top = 7.dp).size(4.dp).clip(CircleShape).background(YY.tint.accent))
                    Text(item, style = YY.font(13.5f), color = ink, modifier = Modifier.padding(start = 8.dp))
                }
            }
        }
        val context = LocalContext.current
        if (offer.info.page.isNotEmpty()) Text(
            t("完整说明在发布页。"),
            style = YY.font(12f), color = YY.tint.accent,
            modifier = Modifier.padding(top = YY.s(12)).clickable { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(offer.info.page))) },
        )
        Row(Modifier.padding(top = YY.s(14)).fillMaxWidth()) {
            Text(t("语阅 · 第 %@ 版", offer.display), fontFamily = YY.serif, fontSize = 11.sp, color = ink2, style = YY.tnum, modifier = Modifier.weight(1f))
            Text(Updates.monthDay(offer.info.date) ?: "", fontFamily = YY.serif, fontSize = 11.sp, color = ink2, style = YY.tnum)
        }
    }
}

@Composable
private fun Bar(done: Float, total: Long) {
    val frac by androidx.compose.animation.core.animateFloatAsState(done.coerceIn(0f, 1f), app.yuyue.design.YYMotion.standard(), label = "download")
    val trackColor = YY.c.inset
    val fillColor = YY.tint.fill
    Column(Modifier.padding(top = YY.s(18)).fillMaxWidth()) {
        androidx.compose.foundation.Canvas(Modifier.fillMaxWidth().height(4.dp)) {
            val r = androidx.compose.ui.geometry.CornerRadius(size.height / 2f)
            drawRoundRect(trackColor, cornerRadius = r)
            if (frac > 0f) drawRoundRect(fillColor, size = androidx.compose.ui.geometry.Size(size.width * frac, size.height), cornerRadius = r)
        }
        Row(Modifier.padding(top = YY.s(8)).fillMaxWidth()) {
            Text("${(done * 100).toInt()}%", style = YY.font(13f, FontWeight.SemiBold, tnum = true), color = YY.c.t1)
            Spacer(Modifier.weight(1f))
            Text("${megabytes((total * done).toLong())} / ${megabytes(total)}", style = YY.font(13f, tnum = true), color = YY.c.t2)
        }
    }
}

private fun megabytes(bytes: Long) = "%.1f MB".format(bytes / 1024.0 / 1024.0)

@Composable
private fun Note(text: String) {
    Text(text, style = YY.font(12.5f), color = YY.c.t2, modifier = Modifier.padding(top = YY.s(14)).fillMaxWidth().clip(RoundedCornerShape(YY.s(16))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16))).padding(YY.s(12)))
}

@Composable
private fun Card(content: @Composable () -> Unit) {
    Column(Modifier.padding(top = YY.s(8)).fillMaxWidth().clip(RoundedCornerShape(YY.s(16))).background(YY.c.card).border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(16))).padding(YY.s(14))) { content() }
}

@Composable
private fun Fact(title: String, value: String, first: Boolean = false) {
    Row(Modifier.padding(top = if (first) YY.s(12) else 0.dp).fillMaxWidth().padding(horizontal = YY.s(14), vertical = YY.s(12))) {
        Text(title, style = YY.font(15f), color = YY.c.t1, modifier = Modifier.weight(1f))
        Text(value, style = YY.font(13f, tnum = true), color = YY.c.t3)
    }
}

@Composable
private fun Quiet(title: String, onClick: () -> Unit) {
    Text(title, style = YY.font(13f, FontWeight.Medium), color = YY.c.t2, modifier = Modifier.clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 8.dp))
}

@Composable
private fun Ghost(title: String, wide: Boolean = false, onClick: () -> Unit) {
    Text(title, style = YY.font(15f, FontWeight.SemiBold), color = YY.tint.accent, textAlign = TextAlign.Center, modifier = Modifier.then(if (wide) Modifier.fillMaxWidth() else Modifier).clickable(onClick = onClick).padding(vertical = 10.dp))
}

@Composable
private fun Mark(px: Int) {
    val s = YY.s(px)
    Box(Modifier.size(s).clip(RoundedCornerShape(s * 0.23f)).background(Color(0xFFFBFAF7)), contentAlignment = Alignment.Center) {
        Text("阅", fontFamily = YY.serif, fontWeight = FontWeight.Bold, fontSize = (px * 0.46f).sp, color = Color(0xFF1C1D20))
        Box(Modifier.align(Alignment.BottomCenter).padding(bottom = s * 0.18f).width(s * 0.38f).height(2.dp).clip(CircleShape).background(Color(0xFF2B4C7E)))
    }
}
