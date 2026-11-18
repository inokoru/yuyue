package app.yuyue.companion

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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYField
import app.yuyue.design.YYGroup
import app.yuyue.design.YYLabel
import app.yuyue.design.YYNote
import app.yuyue.design.YYPage
import app.yuyue.design.rememberPref
import app.yuyue.design.YYRow
import app.yuyue.design.YYSheetHeader
import app.yuyue.me.Secret
import app.yuyue.shelf.Field
import app.yuyue.t
import kotlinx.coroutines.launch

/** 书伴, as the Mac's: 书伴用哪家 — each service added, the one answering marked 书伴在用, its
 *  model and key under its name — then 添加服务商. One opens its page. */
@Composable
fun CompanionSettings(open: (String) -> Unit) {
    // Bumped when something changes, to read it again.
    var version by remember { mutableIntStateOf(0) }
    var adding by remember { mutableStateOf(false) }
    val provider by rememberPref("companion.provider", "")
    val added = remember(version, provider) { CompanionProvider.added }
    val current = remember(version, provider) { CompanionConfig.current }
    YYPage {
        app.yuyue.me.SettingsLead(t("阅读问答与播客撰稿所用的大模型"))
        YYLabel(t("模型服务"))
        YYGroup {
            for (p in added) row {
                val config = CompanionConfig.saved(p)
                ServiceRow(p, {
                    Text(p.title, style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                    if (current?.provider == p) InUse()
                }, (listOf(if (config.model.isEmpty()) t("未选择模型") else config.model) + if (config.key.isEmpty()) emptyList() else listOf("Key " + config.maskedKey)).joinToString(" · ")) { open(p.id) }
            }
            row {
                Row(Modifier.fillMaxWidth().clickable { adding = true }.padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                    app.yuyue.me.ServiceTile(26f) { Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(15))) }
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                        Text(t("添加服务商"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t2)
                        Text(t("Claude、OpenAI、DeepSeek、Gemini、Kimi、通义、本机 Ollama…"), style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                    }
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
                }
            }
        }
        YYNote(t("API Key 仅在本机加密保存，不同步、不上传；调用费用由服务商直接结算。播客文稿生成同样使用此处所选模型。"))
    }
    if (adding) ProviderSheet { adding = false; version++ }
}

/** 当前使用, in green. */
@Composable
private fun InUse() {
    Text(t("当前使用"), style = YY.font(9.5f, FontWeight.Medium), color = YY.c.ok,
        modifier = Modifier.background(YY.c.ok.copy(alpha = 0.12f), RoundedCornerShape(YY.s(4))).padding(horizontal = YY.s(5), vertical = YY.s(1)))
}

/** A service: its logo on a tile, its name, a grey line; the chevron at the end. */
@Composable
private fun ServiceRow(p: CompanionProvider, title: @Composable () -> Unit, detail: String, onClick: () -> Unit) {
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(vertical = YY.s(8)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        app.yuyue.me.ServiceTile(26f) { ProviderLogo(p, YY.s(16)) }
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) { title() }
            Text(detail, style = YY.font(10f), color = YY.c.t3, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
        }
        Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
    }
}

/** One service, as the Mac's right column: its model and 书伴用它; its key and address, tried as
 *  they are typed; 检查连接; 移除 at the foot. */
@Composable
fun ServicePage(id: String, openModels: () -> Unit, back: () -> Unit) {
    val provider = remember(id) { CompanionProvider.find(id) } ?: return
    var version by remember { mutableIntStateOf(0) }
    val chosen by rememberPref("companion.provider", "")
    val config = remember(version, id) { CompanionConfig.saved(provider) }
    val inUse = remember(version, chosen) { CompanionConfig.current?.provider == provider }
    var removing by remember { mutableStateOf(false) }
    // Back from 选一个模型: read the model again.
    LaunchedEffect(Unit) { version++ }
    YYPage {
        app.yuyue.me.PanelHeader(provider.title, if (inUse) t("当前使用") else provider.account.ifEmpty { provider.group.title }, if (inUse) YY.c.ok else YY.c.t3) {
            app.yuyue.me.ServiceTile(34f) { ProviderLogo(provider, YY.s(20)) }
        }
        YYGroup {
            row { YYRow(t("模型"), value = config.model.ifEmpty { t("选择…") }, chevron = true, onClick = openModels) }
            row {
                YYRow(t("设为当前服务"), trailing = {
                    app.yuyue.me.SettingSwitch(inUse, { on -> if (on && !inUse) { CompanionConfig.saved(provider).choose(); version++ } })
                })
            }
        }
        Spacer(Modifier.height(YY.s(12)))
        KeyCard(provider) { version++ }
        app.yuyue.me.DestructiveCard(t("移除 %@", provider.title)) { removing = true }
    }
    if (removing) AlertDialog(
        onDismissRequest = { removing = false },
        containerColor = YY.c.card,
        title = { Text(t("移除 %@？", provider.title), color = YY.c.t1) },
        text = { Text(t("它的 Key 也一起删掉。"), color = YY.c.t2) },
        confirmButton = { TextButton({ CompanionConfig.remove(provider); removing = false; back() }) { Text(t("移除"), color = YY.c.danger) } },
        dismissButton = { TextButton({ removing = false }) { Text(t("取消"), color = YY.c.t2) } },
    )
}

/** A service's key and address. A key saved before shows as "sk-…3f9a" with 更换; a new one is
 *  tried as soon as it looks whole, and kept only once it answers. 检查连接 asks it one short
 *  question; the answer is how long it took. */
@Composable
private fun KeyCard(provider: CompanionProvider, changed: () -> Unit) {
    var config by remember(provider.id) { mutableStateOf(CompanionConfig.saved(provider)) }
    var name by remember(provider.id) { mutableStateOf(provider.title) }
    var newKey by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf(config.key.isEmpty() && provider.needsKey) }
    var checking by remember { mutableStateOf(false) }
    var outcome by remember { mutableStateOf<Pair<Boolean, String>?>(null) }
    val scope = rememberCoroutineScope()
    fun draft() = config.copy(provider = if (provider.isCustom) CompanionProvider.custom(name.trim(), provider.id) else provider, key = newKey.trim().ifEmpty { config.key })
    fun check() {
        val d = draft()
        if (!d.isConnected) { outcome = false to t("还没填好"); return }
        checking = true
        outcome = null
        val started = System.currentTimeMillis()
        scope.launch {
            outcome = try {
                if (d.model.isNotEmpty()) Companion.answer(d, "用一句话回答。", "你好") else Companion.models(d)
                // It answered: what was typed is kept.
                d.save()
                config = d
                newKey = ""
                editing = false
                changed()
                true to t("正常 · %@ 秒", "%.1f".format((System.currentTimeMillis() - started) / 1000.0))
            } catch (e: Exception) {
                false to Companion.message(e)
            }
            checking = false
        }
    }
    LaunchedEffect(newKey) {
        if (newKey.trim().length < 20) return@LaunchedEffect
        kotlinx.coroutines.delay(600)
        check()
    }
    YYGroup {
        if (provider.isCustom) row { YYField(t("名称"), 64f) { Field(name, t("比如 公司的模型")) { name = it; draft().takeIf { d -> d.isConnected }?.save() } } }
        row {
            YYField("API Key", 64f) {
                if (!editing) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(if (config.key.isEmpty()) t("没填") else config.maskedKey, style = YY.font(11.5f), color = YY.c.t2, modifier = Modifier.weight(1f))
                    Text(t("更换"), style = YY.font(10.5f), color = YY.c.t2, modifier = Modifier.clickable { editing = true }.padding(start = YY.s(8), top = YY.s(6), bottom = YY.s(6)))
                } else app.yuyue.me.Secret(newKey, if (config.key.isNotEmpty()) t("贴上新的 Key") else if (provider.needsKey) provider.keyHint else t("可不填")) { newKey = it }
            }
        }
        row {
            YYField(t("接口地址"), 64f) {
                Field(config.url, provider.urlHint) { u -> config = config.copy(url = u.trim()); if (config.isConnected) config.save() }
            }
        }
        row {
            Row(
                Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !checking) { check() },
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
            ) {
                Icon(Icons.waveform, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
                Text(t("检查连接"), style = YY.font(11.5f), color = YY.c.ok, modifier = Modifier.weight(1f))
                when {
                    checking -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                    outcome != null -> Text(outcome!!.second, style = YY.font(10.5f, tnum = true), color = if (outcome!!.first) YY.c.ok else YY.c.danger, maxLines = 2, textAlign = TextAlign.End)
                    !config.isReady -> Text(if (config.isConnected) t("先选模型") else t("还没填好"), style = YY.font(10.5f), color = YY.c.t3)
                }
            }
        }
    }
    YYNote(when {
        provider.local -> t("本机或局域网里跑的，提问和书里相关的几段原文只发到这个地址。")
        provider.isCustom -> t("提问和书里相关的几段原文直接发到这个地址，语阅不经手。")
        else -> t("粘贴后自动验证，有效才会替换。API Key 仅在本机加密保存，调用费用由你自己的 %@ 账号结算。", provider.account)
    })
}

/** 选一个模型: what the service lists, the one chosen ticked; or typed in, for one it doesn't list. */
@Composable
fun ModelsPage(id: String, back: () -> Unit) {
    val provider = remember(id) { CompanionProvider.find(id) } ?: return
    var config by remember { mutableStateOf(CompanionConfig.saved(provider)) }
    var models by remember { mutableStateOf<List<String>?>(null) }
    var failed by remember { mutableStateOf<String?>(null) }
    var typed by remember { mutableStateOf("") }
    var query by remember { mutableStateOf("") }
    LaunchedEffect(id) {
        models = try { Companion.models(config).sorted() } catch (e: Exception) { failed = Companion.message(e); emptyList() }
    }
    fun pick(m: String) {
        config = config.copy(model = m.trim())
        config.save()
        back()
    }
    YYPage {
        Spacer(Modifier.height(YY.s(12)))
        val list = models
        if (list == null) {
            Box(Modifier.fillMaxWidth().padding(vertical = YY.s(30)), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(YY.s(18)), color = YY.c.t3, strokeWidth = 2.dp) }
        } else {
            if (list.size > 12) {
                Row(Modifier.fillMaxWidth().height(YY.s(34)).background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(horizontal = YY.s(10)),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                    Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
                    Box(Modifier.weight(1f)) { Field(query, t("搜索模型")) { query = it } }
                }
                Spacer(Modifier.height(YY.s(10)))
            }
            val shown = list.filter { query.isBlank() || it.contains(query.trim(), ignoreCase = true) }
            if (shown.isNotEmpty()) YYGroup {
                for (m in shown) row {
                    YYRow(m, onClick = { pick(m) }, trailing = {
                        if (m == config.model) Icon(Icons.check, t("在用"), tint = YY.tint.accent, modifier = Modifier.size(YY.s(13)))
                    })
                }
            }
            failed?.let { YYNote(t("未能获取模型列表：%@", it), YY.c.danger) }
            YYLabel(t("自己填"))
            YYGroup {
                row {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.weight(1f)) { YYField(t("模型"), 44f) { Field(typed, config.model.ifEmpty { provider.modelHint }) { typed = it } } }
                        Text(t("用这个"), style = YY.font(11f, FontWeight.Medium), color = if (typed.isBlank()) YY.c.t3 else YY.c.t1,
                            modifier = Modifier.clickable(enabled = typed.isNotBlank()) { pick(typed) }.padding(start = YY.s(8), top = YY.s(8), bottom = YY.s(8)))
                    }
                }
            }
        }
    }
}

/** The settings in a sheet, for a book's 书伴 before any service is added: the list, a service,
 *  its models, each over the last. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CompanionSettingsSheet(dismiss: () -> Unit) {
    var service by remember { mutableStateOf<String?>(null) }
    var models by remember { mutableStateOf(false) }
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg) {
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.9f)) {
            val s = service
            if (s != null) Row(Modifier.heightIn(min = 44.dp).clickable { if (models) models = false else service = null }.padding(horizontal = 16.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.chevronLeft, t("返回"), tint = YY.c.t2, modifier = Modifier.size(YY.s(18)))
                Text(if (models) CompanionProvider.find(s)?.title ?: t("书伴") else t("书伴"), style = YY.font(11.5f), color = YY.c.t2)
            }
            when {
                s != null && models -> ModelsPage(s) { models = false }
                s != null -> ServicePage(s, { models = true }) { service = null }
                else -> CompanionSettings { service = it }
            }
        }
    }
}

/** A service's logo, bare; the reader's own get their first letter on a small grey disc. */
@Composable
fun ProviderLogo(p: CompanionProvider, size: Dp = 18.dp) {
    val logo = p.logo
    if (logo != null) Icon(painterResource(logo), null, tint = if (p.logoTinted) YY.c.t1 else Color.Unspecified, modifier = Modifier.size(size))
    else Box(Modifier.size(size).background(YY.c.inset, CircleShape), contentAlignment = Alignment.Center) {
        Text(p.title.take(1).uppercase(), style = YY.font(size.value * 0.55f, FontWeight.SemiBold), color = YY.c.t2)
    }
}

/** 添加服务商: what's on offer, searchable, grouped by how it's reached, with one of your own at
 *  the bottom; picked, its form. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ProviderSheet(close: () -> Unit) {
    var picked by remember { mutableStateOf<CompanionProvider?>(null) }
    ModalBottomSheet(close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg) {
        Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 28.dp)) {
            val p = picked
            if (p != null) ProviderForm(p, adding = true, back = { picked = null }, close = close, done = close)
            else Picker(back = null, close = close) { picked = it }
        }
    }
}

@Composable
private fun Title(title: String, back: (() -> Unit)?, close: () -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        if (back != null) Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("返回"), onClick = back), contentAlignment = Alignment.CenterStart) {
            Icon(Icons.chevronLeft, t("返回"), tint = YY.c.t2, modifier = Modifier.size(YY.s(18)))
        }
        Box(Modifier.weight(1f)) { YYSheetHeader(title, t("书伴"), close) }
    }
}

@Composable
private fun Picker(back: (() -> Unit)?, close: () -> Unit, pick: (CompanionProvider) -> Unit) {
    var query by remember { mutableStateOf("") }
    val matches = CompanionProvider.available(query)
    Title(t("添加服务商"), back, close)
    Spacer(Modifier.height(YY.s(12)))
    Row(Modifier.fillMaxWidth().height(YY.s(34)).background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(horizontal = YY.s(10)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
        Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
        Box(Modifier.weight(1f)) { Field(query, t("搜索服务商，比如 千问、Kimi、magpie")) { query = it } }
    }
    for (group in CompanionProvider.Group.entries) {
        val list = matches.filter { it.group == group }
        if (list.isEmpty()) continue
        Text(group.title, style = YY.font(10f, FontWeight.SemiBold), color = YY.c.t3, modifier = Modifier.padding(start = YY.s(8), top = YY.s(12), bottom = YY.s(2)))
        Column(verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
            for (pair in list.chunked(2)) Row(horizontalArrangement = Arrangement.spacedBy(YY.s(2))) {
                for (p in pair) Tile(p, Modifier.weight(1f)) { pick(p) }
                if (pair.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
    if (matches.isEmpty()) Text(t("没找到。在下面自己添加，填上接口地址就行"), style = YY.font(11f), color = YY.c.t3,
        modifier = Modifier.fillMaxWidth().padding(vertical = YY.s(36)), textAlign = TextAlign.Center)
    Spacer(Modifier.height(YY.s(14)))
    YYGroup {
        row {
            Row(Modifier.fillMaxWidth().clickable { pick(CompanionProvider.custom("")) }.padding(vertical = YY.s(8)),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                Box(Modifier.size(24.dp), contentAlignment = Alignment.Center) { Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(16))) }
                Column(Modifier.weight(1f)) {
                    Text(t("自己添加"), style = YY.font(11.5f), color = YY.c.t1)
                    Text(t("任何 OpenAI 兼容接口，比如公司里部署的模型"), style = YY.font(10f), color = YY.c.t3)
                }
                Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(11)))
            }
        }
    }
}

@Composable
private fun Tile(p: CompanionProvider, modifier: Modifier, onClick: () -> Unit) {
    Row(modifier.height(YY.s(36)).clip(RoundedCornerShape(YY.s(8))).clickable(onClick = onClick).padding(horizontal = YY.s(8)),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
        ProviderLogo(p)
        Text(p.title, style = YY.font(11.5f), color = YY.c.t1, maxLines = 1)
    }
}

/** One service's key and address (the model is chosen in 设置 · 书伴).
 *  A key saved before shows as "sk-…3f9a" with 更换, so opening the form never wipes it.
 *  OpenRouter signs in instead; a key can still be pasted. */
@Composable
private fun ProviderForm(provider: CompanionProvider, adding: Boolean, back: (() -> Unit)?, close: () -> Unit, done: () -> Unit) {
    var config by remember(provider.id) { mutableStateOf(CompanionConfig.saved(provider)) }
    var name by remember(provider.id) { mutableStateOf(provider.title) }
    var keyLocked by remember(provider.id) { mutableStateOf(config.key.isNotEmpty()) }
    var showKey by remember { mutableStateOf(false) }
    var signingIn by remember { mutableStateOf(false) }
    var failed by remember { mutableStateOf<String?>(null) }
    var removing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val draft = config.copy(provider = if (provider.isCustom) CompanionProvider.custom(name.trim(), provider.id) else provider)
    fun save(c: CompanionConfig = draft) {
        if (!c.isConnected) return
        // A service just added answers from now on; editing one leaves the choice alone.
        if (adding) c.choose() else c.save()
        done()
    }
    val note = when {
        provider.local -> t("本机或局域网里跑的，提问和书里相关的几段原文只发到这个地址。")
        provider.isCustom -> t("提问和书里相关的几段原文直接发到这个地址，语阅不经手。")
        else -> t("Key 只存在这台设备上，加密保存。费用走你自己的 %@ 账号，语阅不经手，也看不到。", provider.account)
    }

    Title(if (provider.isCustom && adding) t("自己添加") else provider.title, back, close)
    Spacer(Modifier.height(YY.s(12)))
    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
        ProviderLogo(draft.provider, 22.dp)
        Column(Modifier.weight(1f)) {
            Text(if (provider.isCustom) t("OpenAI 兼容接口") else provider.group.title, style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t2)
            Text(provider.blurb ?: note, style = YY.font(10f), color = YY.c.t3)
        }
    }
    Spacer(Modifier.height(YY.s(12)))
    if (provider.group == CompanionProvider.Group.SignIn && !keyLocked) {
        YYButton(if (signingIn) "…" else t("用 %@ 登录", provider.title), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = !signingIn) {
            signingIn = true
            failed = null
            scope.launch {
                try {
                    val key = OpenRouterSignIn.run(context)
                    config = config.copy(key = key)
                    keyLocked = true
                    save(draft.copy(key = key))
                } catch (e: Exception) {
                    failed = Companion.message(e)
                }
                signingIn = false
            }
        }
        Text(t("或者粘贴一个已有的 Key"), style = YY.font(10f), color = YY.c.t3, modifier = Modifier.padding(start = YY.s(4), top = YY.s(10), bottom = YY.s(6)))
    }
    YYGroup {
        if (provider.isCustom) row { YYField(t("名称"), 64f) { Field(name, t("比如 公司的模型")) { name = it } } }
        row {
            YYField("API Key", 64f) {
                if (keyLocked) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(config.maskedKey, style = YY.font(11.5f), color = YY.c.t2, modifier = Modifier.weight(1f))
                    YYButton(t("更换"), ButtonKind.Plain, ButtonSize.Small) { config = config.copy(key = ""); keyLocked = false }
                } else Row(verticalAlignment = Alignment.CenterVertically) {
                    val hint = if (provider.needsKey || provider.keyHint != "sk-…") provider.keyHint else t("可不填")
                    Box(Modifier.weight(1f)) {
                        if (showKey) Field(config.key, hint) { config = config.copy(key = it.trim()) }
                        else Secret(config.key, hint) { config = config.copy(key = it.trim()) }
                    }
                    Text(if (showKey) t("藏起 Key") else t("显示 Key"), style = YY.font(10.5f), color = YY.c.t3,
                        modifier = Modifier.clickable { showKey = !showKey }.padding(start = YY.s(8), top = YY.s(6), bottom = YY.s(6)))
                }
            }
        }
        row { YYField(t("接口地址"), 64f) { Field(config.url, provider.urlHint) { config = config.copy(url = it.trim()) } } }
    }
    failed?.let { YYNote(it, YY.c.danger) }
    if (provider.blurb != null) YYNote(note)
    Spacer(Modifier.height(16.dp))
    YYButton(if (adding) t("添加") else t("保存"), ButtonKind.Main, ButtonSize.Large, wide = true, enabled = draft.isConnected) { save() }
    if (!adding) {
        Spacer(Modifier.height(10.dp))
        YYButton(t("移除"), ButtonKind.Danger, ButtonSize.Large, wide = true) { removing = true }
    }
    if (removing) AlertDialog(
        onDismissRequest = { removing = false },
        title = { Text(t("移除 %@？", provider.title)) },
        text = { Text(t("它的 Key 也会从这台设备上删掉。")) },
        confirmButton = { TextButton({ CompanionConfig.remove(provider); removing = false; done() }) { Text(t("移除"), color = YY.c.danger) } },
        dismissButton = { TextButton({ removing = false }) { Text(t("取消")) } },
    )
}
