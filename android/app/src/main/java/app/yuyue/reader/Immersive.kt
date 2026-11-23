package app.yuyue.reader

import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import android.app.AutomaticZenRule
import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.service.notification.Condition
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import app.yuyue.MainActivity
import app.yuyue.design.ButtonKind
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYGroup
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.YYPaper
import app.yuyue.design.YYPapers
import app.yuyue.design.YYRow
import app.yuyue.design.rememberPref
import app.yuyue.t
import uniffi.yuyue_core.YuyueCore

/** 沉浸阅读's settings, as on Apple. */
object ImmersivePrefs {
    const val LINE = "immersive.line"
    const val MARKS = "immersive.hideMarks"
    const val STILL = "immersive.stillPages"
    const val STATUS = "immersive.hideStatus"
    const val PROGRESS = "immersive.hideProgress"
    const val QUIET = "immersive.quiet"
    const val HINTS = "immersive.hideHints"
    const val ALWAYS = "immersive.always"

    val keepsLine get() = Prefs.bool(LINE, true)
    /** Off at first: a highlight is the reader's own and stays unless asked. */
    val hidesMarks get() = Prefs.bool(MARKS, false)
    val stillPages get() = Prefs.bool(STILL, true)
    val hidesStatusBar get() = Prefs.bool(STATUS, true)
    val hidesProgress get() = Prefs.bool(PROGRESS, true)
    val quiet get() = Prefs.bool(QUIET, true)
    /** 关掉书伴提示和难词注释, on at first as on Apple. */
    val hidesHints get() = Prefs.bool(HINTS, true)
    val always get() = Prefs.bool(ALWAYS, false)
}

/**
 * 读书时通知不打扰 (打开沉浸阅读时，语阅顺手打开勿扰，快捷开关上写着"由语阅打开"；离开书页
 * 就关掉，你自己开的勿扰不会被动). An automatic rule of the app's own, so the system shows it as
 * turned on by 语阅, and turning it off never ends a Do Not Disturb the reader turned on.
 */
object DoNotDisturb {
    private val condition = Uri.parse("condition://app.yuyue/reading")
    private const val RULE = "dnd.rule"

    fun allowed(context: Context): Boolean = Build.VERSION.SDK_INT >= 29 && manager(context).isNotificationPolicyAccessGranted

    /** The system page where 语阅 is let in. */
    fun ask(context: Context) {
        runCatching { context.startActivity(Intent(Settings.ACTION_NOTIFICATION_POLICY_ACCESS_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
    }

    fun set(context: Context, on: Boolean) {
        if (Build.VERSION.SDK_INT < 29 || !allowed(context)) return
        val nm = manager(context)
        val id = Prefs.string(RULE).takeIf { it.isNotEmpty() && nm.getAutomaticZenRule(it) != null } ?: run {
            if (!on) return
            val rule = AutomaticZenRule(
                t("语阅 · 沉浸阅读"), null, ComponentName(context, MainActivity::class.java), condition, null,
                NotificationManager.INTERRUPTION_FILTER_PRIORITY, true,
            )
            nm.addAutomaticZenRule(rule).also { Prefs.set(RULE, it) }
        }
        runCatching { nm.setAutomaticZenRuleState(id, Condition(condition, t("读书时不打扰"), if (on) Condition.STATE_TRUE else Condition.STATE_FALSE)) }
    }

    private fun manager(context: Context) = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
}

/** 墨水屏模式, as on Apple: local device display adaptation (pure white/black high-contrast page,
 *  no grain, no animation, optional grayscale for monochrome vs. colour e-ink). Never mutates the
 *  synced paper settings. On a device with an e-ink screen it is turned on automatically once. */
object EInk {
    const val KEY = "eink.on"
    const val GRAYSCALE_KEY = "eink.grayscale"
    private const val CHECKED = "eink.checked"

    val on get() = Prefs.isReady && Prefs.bool(KEY, false)
    val grayscale get() = !Prefs.isReady || Prefs.bool(GRAYSCALE_KEY, true)
    val grayscaleImages get() = on && grayscale

    /** High-contrast pure white/black surface for real e-ink displays (no grey background or grain). */
    val paper = YYPaper(
        id = "eink-mode",
        name = "墨水屏",
        isDark = false,
        hasNoiseTexture = false,
        bg = Color(0xFFFFFFFFL),
        ink = Color(0xFF000000L),
        ink2 = Color(0xFF444444L),
        line = Color(0xFFCCCCCCL),
        accent = Color(0xFF000000L),
        hl = Color(0x00000000L),
        wave = Color(0xFF000000L),
        glass = Color(0xFFFFFFFFL),
        main = Color(0xFF000000L),
        onMain = Color(0xFFFFFFFFL),
        paperAccent = Color(0xFF000000L),
        text = YYPapers.Eink.text,
        title = YYPapers.Eink.title,
        meta = YYPapers.Eink.meta,
    )

    fun set(on: Boolean) {
        Prefs.set(KEY, on)
    }

    fun setGrayscale(on: Boolean) {
        Prefs.set(GRAYSCALE_KEY, on)
    }

    /** The makers whose phones and readers have e-ink screens. */
    private val makers = listOf("onyx", "boox", "boyue", "likebook", "meebook", "bigme", "pocketbook", "ireader", "hisense a", "kobo", "dasung", "moaan", "mooink", "inkpalm")

    fun checkDevice() {
        if (Prefs.bool(CHECKED, false)) return
        Prefs.set(CHECKED, true)
        val name = "${Build.MANUFACTURER} ${Build.BRAND} ${Build.MODEL}".lowercase()
        if (makers.any { it in name }) set(true)
    }
}

/** 沉浸阅读: one switch, and what goes with it, each to keep if wanted. From
 *  a book it turns it on there; from 我的 it is for every book. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ImmersiveSheet(model: ReaderModel?, dismiss: () -> Unit) {
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
        Column(Modifier.padding(horizontal = 16.dp).padding(bottom = 24.dp)) { ImmersiveSettings(model) }
    }
}

@Composable
fun ImmersiveSettings(model: ReaderModel?) {
    val context = LocalContext.current
    var always by rememberPref(ImmersivePrefs.ALWAYS, false)
    var status by rememberPref(ImmersivePrefs.STATUS, true)
    var progress by rememberPref(ImmersivePrefs.PROGRESS, true)
    var quiet by rememberPref(ImmersivePrefs.QUIET, true)
    var still by rememberPref(ImmersivePrefs.STILL, true)
    var marks by rememberPref(ImmersivePrefs.MARKS, false)
    var hints by rememberPref(ImmersivePrefs.HINTS, true)
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(t("沉浸阅读"), style = YY.font(13f, FontWeight.SemiBold), color = YY.c.t1, modifier = Modifier.weight(1f))
        Switch(
            model?.immersive ?: always,
            { on -> if (model != null) { model.chromeShown = false; model.immersive = on } else always = on },
            modifier = Modifier.semantics { contentDescription = t("沉浸阅读") },
            colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
        )
    }
    Text(
        if (model == null) t("打开书籍时自动进入沉浸阅读，以下项目将默认隐藏或关闭，也可逐项保留。") else t("开启后全屏隐藏界面要素，以下项目将默认隐藏或关闭，也可逐项保留。"),
        style = YY.font(10f), color = YY.c.t3, modifier = Modifier.padding(top = 6.dp, bottom = 12.dp),
    )
    YYGroup {
        row { SwitchRow(t("隐藏状态栏、时间和电量"), status) { status = it } }
        row { SwitchRow(t("隐藏进度、页码和剩余时间"), progress) { progress = it } }
        row { SwitchRow(t("专注模式联动"), quiet) { quiet = it } }
        row { SwitchRow(t("关闭翻页动画"), still) { still = it; model?.applyImmersive() } }
        row { SwitchRow(t("隐藏划线与想法"), marks) { marks = it; model?.applyImmersive() } }
        row { SwitchRow(t("隐藏书伴提示与难词注释"), hints) { hints = it; model?.applyImmersive() } }
    }
    if (quiet && !DoNotDisturb.allowed(context)) {
        YYNote(t("请在系统设置中允许语阅管理勿扰权限，以便阅读时自动开启勿扰。"))
        Spacer(Modifier.height(8.dp))
        YYButton(t("打开设置"), ButtonKind.Raised, ButtonSize.Small) { DoNotDisturb.ask(context) }
    } else if (quiet) {
        YYNote(t("沉浸阅读时自动开启系统勿扰，退出后恢复；不影响手动开启的勿扰。"))
    }
}

/** 我的 › 墨水屏: local display mode toggle, optional grayscale for colour e-ink, and live preview. */
@Composable
fun EInkPage(core: YuyueCore) {
    var on by rememberPref(EInk.KEY, false)
    var grayscale by rememberPref(EInk.GRAYSCALE_KEY, true)
    val shared = runCatching { core.readerSettings() }.getOrNull()
    val paper = if (on) EInk.paper else (shared?.paperTokens ?: YYPapers.Plain)
    YYPage {
        Spacer(Modifier.height(YY.s(10)))
        YYGroup {
            row {
                YYRow(t("墨水屏模式"), Icons.eink, trailing = {
                    Switch(on, { EInk.set(it); on = it }, modifier = Modifier.semantics { contentDescription = t("墨水屏模式") }, colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line))
                })
            }
            row {
                Row(
                    Modifier.fillMaxWidth().padding(vertical = YY.s(8)).alpha(if (on) 1f else 0.45f),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                ) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                        Text(t("封面与插图转为灰度"), style = YY.font(11.5f), color = YY.c.t1)
                        Text(t("彩墨屏可关闭以保留原色"), style = YY.font(10f), color = YY.c.t3)
                    }
                    Switch(
                        grayscale,
                        { EInk.setGrayscale(it); grayscale = it },
                        enabled = on,
                        modifier = Modifier.semantics { contentDescription = t("封面与插图转为灰度") },
                        colors = SwitchDefaults.colors(checkedTrackColor = YY.tint.fill, checkedThumbColor = YY.c.card, uncheckedTrackColor = YY.c.inset, uncheckedBorderColor = YY.c.line),
                    )
                }
            }
        }
        Column(
            Modifier.padding(top = YY.s(14)).fillMaxWidth().background(paper.bg, RoundedCornerShape(YY.s(12))).border(if (on) 1.dp else 0.75.dp, if (on) paper.ink else paper.line, RoundedCornerShape(YY.s(12))).padding(YY.s(18)),
            verticalArrangement = Arrangement.spacedBy(YY.s(8)),
        ) {
            Text(t("背影"), style = YY.font(18f, FontWeight.SemiBold, YY.serif), color = paper.ink)
            Text(
                buildAnnotatedString {
                    append(t("父亲说：“事已如此，不必难过，"))
                    withStyle(if (on) SpanStyle(textDecoration = TextDecoration.Underline) else SpanStyle(background = paper.hl)) {
                        append(t("好在天无绝人之路！”"))
                    }
                    append("\n")
                    withStyle(SpanStyle(textDecoration = TextDecoration.Underline)) {
                        append(t("回家变卖典质，父亲还了亏空；"))
                    }
                    append(t("又借钱办了丧事。"))
                },
                style = YY.font(13.5f, family = YY.serif).copy(lineHeight = YY.sp(28f)), color = paper.ink,
            )
        }
        YYNote(t("仅在本机生效：阅读页切换为纯白底与纯黑字（无颗粒噪点），划线转为黑线，并关闭翻页动画、阴影与毛玻璃效果；不改动跨设备同步的纸色，关闭后恢复原纸色。"))
    }
}
