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
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
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
        Spacer(Modifier.height(YY.s(12)))
        CompanionQuickPromptsSection()
        Spacer(Modifier.height(YY.s(12)))
        CompanionCostSection(current)
    }
    if (adding) ProviderSheet { adding = false; version++ }
}

/** 常用提问: user-saved quick prompts with editable `/command` tokens, shown in 书伴 suggestion bar and `/` menu. */
@Composable
private fun CompanionQuickPromptsSection() {
    val promptVersion = CompanionPrompts.version
    val items = remember(promptVersion) { CompanionPrompts.items }
    val isDefault = remember(items) { items == CompanionPrompts.defaults }
    var editingID by remember { mutableStateOf<String?>(null) }

    Row(
        Modifier.fillMaxWidth().padding(horizontal = YY.s(4)).padding(top = YY.s(14), bottom = YY.s(6)),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(t("常用提问"), style = YY.font(10.5f, FontWeight.SemiBold), color = YY.c.t2, modifier = Modifier.weight(1f))
        if (!isDefault) {
            Text(
                t("恢复默认"),
                style = YY.font(10.5f),
                color = YY.c.t3,
                modifier = Modifier.clickable { CompanionPrompts.resetDefaults() }.padding(vertical = YY.s(2), horizontal = YY.s(4)),
            )
        }
    }
    YYGroup {
        for (item in items) {
            row {
                Row(
                    Modifier.fillMaxWidth().clickable { editingID = item.id }.padding(vertical = YY.s(10)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                ) {
                    Box(
                        Modifier.background(YY.tint.accentSoft, RoundedCornerShape(YY.s(6)))
                            .padding(horizontal = YY.s(7), vertical = YY.s(3)),
                    ) {
                        Text(
                            "/${item.command}",
                            style = YY.font(10.5f, FontWeight.SemiBold).copy(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace),
                            color = YY.tint.accent,
                        )
                    }
                    Text(
                        item.prompt,
                        style = YY.font(11.5f, FontWeight.Medium),
                        color = YY.c.t1,
                        maxLines = 1,
                        overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                        modifier = Modifier.weight(1f),
                    )
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(12)))
                }
            }
        }
        if (items.size < CompanionPrompts.MAX_COUNT) {
            row {
                Row(
                    Modifier.fillMaxWidth().clickable { editingID = "new" }.padding(vertical = YY.s(10)),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                ) {
                    Box(
                        Modifier.size(YY.s(24)).background(YY.c.inset, RoundedCornerShape(YY.s(6)))
                            .border(0.5.dp, YY.c.line, RoundedCornerShape(YY.s(6))),
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(Icons.add, null, tint = YY.c.t2, modifier = Modifier.size(YY.s(12)))
                    }
                    Text(t("添加常用提问"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t2, modifier = Modifier.weight(1f))
                    Text(t("最多 %lld 条", CompanionPrompts.MAX_COUNT), style = YY.font(10f, tnum = true), color = YY.c.t3)
                    Icon(Icons.chevronRight, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(12)))
                }
            }
        }
    }
    YYNote(t("点进去可修改 / 指令名与提问内容；同时出现在书伴底部建议栏与 / 快捷指令菜单中。"))

    val openID = editingID
    if (openID != null) {
        PromptEditorSheet(promptID = openID, close = { editingID = null })
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun PromptEditorSheet(promptID: String, close: () -> Unit) {
    val items = remember(CompanionPrompts.version) { CompanionPrompts.items }
    val existing = remember(promptID, items) { if (promptID == "new") null else items.firstOrNull { it.id == promptID } }
    val defaultCmd = remember(existing, items) { existing?.command ?: CompanionPrompts.nextDefaultCommand(items) }
    var commandText by remember(promptID) { mutableStateOf(existing?.command ?: CompanionPrompts.nextDefaultCommand(items)) }
    var promptText by remember(promptID) { mutableStateOf(existing?.prompt ?: "") }

    val resolvedCmd = CompanionPrompts.sanitizeCommand(
        raw = commandText,
        fallback = defaultCmd,
        existing = items,
        excludingID = existing?.id,
    )
    val trimmedPrompt = promptText.trim().take(CompanionPrompts.MAX_LENGTH)
    val isDuplicate = trimmedPrompt.isNotEmpty() && items.any { it.id != existing?.id && it.prompt == trimmedPrompt }
    val canSave = trimmedPrompt.isNotEmpty() && !isDuplicate && (existing != null || items.size < CompanionPrompts.MAX_COUNT)

    ModalBottomSheet(
        close,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
        dragHandle = { YYSheetDragHandle(bottom = 2.dp) },
    ) {
        Column(
            Modifier.fillMaxWidth().imePadding().verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp).padding(bottom = 28.dp),
            verticalArrangement = Arrangement.spacedBy(YY.s(10)),
        ) {
            YYSheetHeader(if (existing == null) t("添加常用提问") else t("编辑常用提问"), "/$resolvedCmd", close)

            YYGroup {
                row {
                    Row(
                        Modifier.fillMaxWidth().heightIn(min = YY.s(42)),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
                    ) {
                        Text(t("快捷指令"), style = YY.font(11.5f), color = YY.c.t1)
                        Spacer(Modifier.weight(1f))
                        Row(
                            Modifier.background(YY.c.inset, RoundedCornerShape(YY.s(7)))
                                .padding(horizontal = YY.s(9), vertical = YY.s(5)),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(YY.s(2)),
                        ) {
                            Text(
                                "/",
                                style = YY.font(11.5f, FontWeight.SemiBold).copy(fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace),
                                color = YY.tint.accent,
                            )
                            Box(Modifier.width(YY.s(110))) {
                                Field(commandText, defaultCmd) { commandText = it }
                            }
                        }
                    }
                }
                row {
                    Column(
                        Modifier.fillMaxWidth().padding(vertical = YY.s(10)),
                        verticalArrangement = Arrangement.spacedBy(YY.s(6)),
                    ) {
                        Text(t("提问内容"), style = YY.font(11.5f), color = if (isDuplicate) YY.c.danger else YY.c.t1)
                        Field(promptText, t("例如：拆解这句语法与长难句")) { promptText = it }
                    }
                }
            }

            if (isDuplicate) {
                YYNote(t("这条提问已经存在了。"), YY.c.danger)
            } else {
                YYNote(t("在书伴输入框输入 /%@ 回车即可直接提问；留空时自动使用默认指令 /%@。", resolvedCmd, defaultCmd))
            }

            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                if (existing != null) {
                    YYButton(t("删除这条提问"), ButtonKind.Danger, ButtonSize.Regular, modifier = Modifier.weight(1f)) {
                        CompanionPrompts.remove(existing.id)
                        close()
                    }
                }
                YYButton(
                    if (existing == null) t("添加到常用提问") else t("保存修改"),
                    ButtonKind.Main,
                    ButtonSize.Regular,
                    enabled = canSave,
                    modifier = Modifier.weight(1f),
                ) {
                    val targetID = existing?.id ?: java.util.UUID.randomUUID().toString()
                    CompanionPrompts.upsert(CompanionPrompts.Item(id = targetID, command = resolvedCmd, prompt = trimmedPrompt))
                    close()
                }
            }
        }
    }
}

/** 用量与省费: 30-day Token 用量 chart + 2-row 省费 card. */
@Composable
private fun CompanionCostSection(current: CompanionConfig?) {
    val snapshot = remember { CompanionUsage.snapshot30Days() }
    var inspectedIndex by remember { mutableStateOf<Int?>(null) }
    var tierRouting by remember { mutableStateOf(CompanionUsage.tierRoutingEnabled) }
    var dailyTokenLimit by remember { mutableIntStateOf(CompanionUsage.dailyTokenLimit) }
    val showCloudTts = snapshot.ttsChars > 0 || snapshot.ttsSavedChars > 0 || app.yuyue.speech.CloudVoice.all().isNotEmpty()
    val tokenPresets = listOf(t("不限") to 0, t("20 万") to 200_000, t("50 万") to 500_000, t("100 万") to 1_000_000)

    val dark = YY.dark
    val cacheBg = if (dark) Color(0x38B4865A) else Color(0xFFF5E6D3)
    val cacheStripe = if (dark) Color(0xFFD49B63) else Color(0xFFB5773B)
    val missColor = if (dark) Color(0xFF6B6762) else Color(0xFFC8C3BC)
    val outColor = if (dark) Color(0xFFDDDAD5) else Color(0xFF3B3936)
    val gridColor = if (dark) Color.White.copy(alpha = 0.08f) else Color(0x142A2B2E)

    YYLabel(t("用量"))
    YYGroup {
        row {
            Column(Modifier.fillMaxWidth().padding(vertical = YY.s(14)), verticalArrangement = Arrangement.spacedBy(YY.s(12))) {
                Column(verticalArrangement = Arrangement.spacedBy(YY.s(4))) {
                    Row(verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
                        Text(t("Token 用量"), style = YY.font(12f, FontWeight.Medium), color = YY.c.t1)
                        Text(t("近 30 天"), style = YY.font(10f), color = YY.c.t3)
                    }
                    Row(verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
                        Text(
                            if (snapshot.totalTokens > 0) CompanionUsage.formatCount(snapshot.totalTokens) else "0",
                            style = YY.font(22f, FontWeight.SemiBold),
                            color = YY.c.t1,
                        )
                        val hit = snapshot.cacheHitRate
                        if (hit != null && snapshot.cachedInputTokens > 0) {
                            Text(t("缓存命中 %lld%%", kotlin.math.round(hit * 100).toInt()), style = YY.font(10.5f), color = YY.c.ok)
                        }
                    }
                }

                if (snapshot.totalTokens == 0) {
                    Text(t("近 30 天暂无调用记录"), style = YY.font(11f), color = YY.c.t3)
                } else {
                    val peak = maxOf(1, snapshot.peakTokens)
                    val meanFrac = (1.0 - snapshot.averageTokens / peak).coerceIn(0.0, 1.0).toFloat()
                    val showMean = meanFrac >= 0.16f && snapshot.averageTokens >= 1.0

                    Column(verticalArrangement = Arrangement.spacedBy(YY.s(6))) {
                        Row(Modifier.fillMaxWidth().height(92.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                            Box(Modifier.size(width = 34.dp, height = 92.dp)) {
                                Text(CompanionUsage.formatCount(snapshot.peakTokens), style = YY.font(9f), color = YY.c.t3, modifier = Modifier.align(Alignment.TopEnd))
                                if (showMean) {
                                    Text(
                                        CompanionUsage.formatCount(kotlin.math.round(snapshot.averageTokens).toInt()),
                                        style = YY.font(9f),
                                        color = YY.c.t3,
                                        modifier = Modifier.align(Alignment.TopEnd).padding(top = (92f * meanFrac - 6f).coerceAtLeast(10f).dp),
                                    )
                                }
                            }
                            Box(Modifier.weight(1f).fillMaxHeight()) {
                                androidx.compose.foundation.Canvas(Modifier.fillMaxWidth().fillMaxHeight()) {
                                    drawLine(gridColor, androidx.compose.ui.geometry.Offset(0f, 0.5f), androidx.compose.ui.geometry.Offset(size.width, 0.5f), strokeWidth = 1f)
                                    if (showMean) {
                                        val y = size.height * meanFrac
                                        drawLine(gridColor, androidx.compose.ui.geometry.Offset(0f, y), androidx.compose.ui.geometry.Offset(size.width, y), strokeWidth = 1f)
                                    }
                                    drawLine(gridColor, androidx.compose.ui.geometry.Offset(0f, size.height - 0.5f), androidx.compose.ui.geometry.Offset(size.width, size.height - 0.5f), strokeWidth = 1f)
                                }
                                Row(Modifier.fillMaxWidth().fillMaxHeight(), horizontalArrangement = Arrangement.spacedBy(2.dp), verticalAlignment = Alignment.Bottom) {
                                    snapshot.days.forEachIndexed { idx, day ->
                                        val tot = day.totalTokens
                                        val frac = if (tot > 0) (tot.toFloat() / peak).coerceIn(0.03f, 1f) else 0f
                                        val active = inspectedIndex == null || inspectedIndex == idx
                                        Box(
                                            Modifier.weight(1f).fillMaxHeight().clickable(enabled = tot > 0) {
                                                inspectedIndex = if (inspectedIndex == idx) null else idx
                                            },
                                            contentAlignment = Alignment.BottomCenter,
                                        ) {
                                            if (tot > 0) {
                                                androidx.compose.foundation.Canvas(
                                                    Modifier.fillMaxWidth().fillMaxHeight(frac).clip(RoundedCornerShape(topStart = 2.dp, topEnd = 2.dp))
                                                ) {
                                                    val alpha = if (active) 1f else 0.45f
                                                    val outH = size.height * day.output / tot
                                                    val missH = size.height * day.missInput / tot
                                                    val cacheH = (size.height - outH - missH).coerceAtLeast(0f)
                                                    if (outH > 0f) drawRect(outColor.copy(alpha = alpha), size = androidx.compose.ui.geometry.Size(size.width, outH))
                                                    if (missH > 0f) drawRect(missColor.copy(alpha = alpha), topLeft = androidx.compose.ui.geometry.Offset(0f, outH), size = androidx.compose.ui.geometry.Size(size.width, missH))
                                                    if (cacheH > 0f) {
                                                        val topY = outH + missH
                                                        drawRect(cacheBg.copy(alpha = alpha), topLeft = androidx.compose.ui.geometry.Offset(0f, topY), size = androidx.compose.ui.geometry.Size(size.width, cacheH))
                                                        var x = -cacheH
                                                        while (x < size.width + cacheH) {
                                                            drawLine(
                                                                cacheStripe.copy(alpha = alpha),
                                                                androidx.compose.ui.geometry.Offset(x, topY + cacheH),
                                                                androidx.compose.ui.geometry.Offset(x + cacheH, topY),
                                                                strokeWidth = 1.5f,
                                                            )
                                                            x += 6f
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        // Date axis
                        Row(Modifier.fillMaxWidth().padding(start = 40.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                            Text(snapshot.days.firstOrNull()?.shortLabel ?: "", style = YY.font(9f), color = YY.c.t3)
                            if (snapshot.days.indices.contains(snapshot.peakIndex)) {
                                Text(snapshot.days[snapshot.peakIndex].shortLabel, style = YY.font(9f, FontWeight.Medium), color = YY.tint.fill)
                            }
                            Text(snapshot.days.lastOrNull()?.shortLabel ?: "", style = YY.font(9f), color = YY.c.t3)
                        }

                        // Tapped day detail strip
                        val sel = inspectedIndex?.let { snapshot.days.getOrNull(it) }?.takeIf { it.totalTokens > 0 }
                        if (sel != null) {
                            Row(
                                Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(8.dp)).padding(horizontal = 10.dp, vertical = 6.dp),
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                                verticalAlignment = Alignment.CenterVertically,
                            ) {
                                Text(sel.date, style = YY.font(10f, FontWeight.Medium), color = YY.c.t1)
                                Text(t("总 %@", CompanionUsage.formatCount(sel.totalTokens)), style = YY.font(10f), color = YY.c.t2)
                                if (sel.cachedInput > 0) Text(t("缓存 %@", CompanionUsage.formatCount(sel.cachedInput)), style = YY.font(10f), color = YY.c.ok)
                                Spacer(Modifier.weight(1f))
                                Text(t("%lld 次", sel.requests), style = YY.font(9.5f), color = YY.c.t3)
                            }
                        }

                        // Legend
                        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                            if (snapshot.cachedInputTokens > 0) {
                                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Box(Modifier.size(8.dp).clip(RoundedCornerShape(2.dp)).background(cacheStripe.copy(alpha = 0.7f)))
                                    Text(t("缓存命中输入"), style = YY.font(9.5f), color = YY.c.t3)
                                }
                            }
                            if (snapshot.missInputTokens > 0) {
                                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Box(Modifier.size(8.dp).clip(RoundedCornerShape(2.dp)).background(missColor))
                                    Text(t("常规输入"), style = YY.font(9.5f), color = YY.c.t3)
                                }
                            }
                            if (snapshot.outputTokens > 0) {
                                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                                    Box(Modifier.size(8.dp).clip(RoundedCornerShape(2.dp)).background(outColor))
                                    Text(t("输出"), style = YY.font(9.5f), color = YY.c.t3)
                                }
                            }
                        }
                    }

                    if (snapshot.sources.isNotEmpty()) {
                        Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                            for (item in snapshot.sources.take(4)) {
                                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.Bottom) {
                                    Text(item.source.title, style = YY.font(10.5f), color = YY.c.t3)
                                    Text("${maxOf(1, kotlin.math.round(item.share * 100).toInt())}%", style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t1)
                                }
                            }
                        }
                    }
                }
            }
        }
        if (showCloudTts) row {
            Row(Modifier.fillMaxWidth().padding(vertical = YY.s(10)), verticalAlignment = Alignment.CenterVertically) {
                Text(t("云端朗读 · 近 30 天"), style = YY.font(11f), color = YY.c.t2, modifier = Modifier.weight(1f))
                Text(
                    if (snapshot.ttsSavedChars > 0)
                        t("%@ 字 · 本地缓存省 %@ 字", CompanionUsage.formatCount(snapshot.ttsChars), CompanionUsage.formatCount(snapshot.ttsSavedChars))
                    else t("%@ 字", CompanionUsage.formatCount(snapshot.ttsChars)),
                    style = YY.font(11f),
                    color = YY.c.t1,
                )
            }
        }
    }

    Spacer(Modifier.height(YY.s(12)))
    YYLabel(t("省费"))
    YYGroup {
        row {
            val hint = current?.resolvedFastModel?.let { t("认人、错字整理、提要优先用 %@，并自动启用提示词缓存", it) }
                ?: t("认人、错字整理、提要优先用同厂轻量模型，并自动启用提示词缓存")
            Row(Modifier.fillMaxWidth().padding(vertical = YY.s(10)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                    Text(t("批量任务自动用轻量模型"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                    Text(hint, style = YY.font(10f), color = YY.c.t3)
                }
                app.yuyue.me.SettingSwitch(tierRouting, { tierRouting = it; CompanionUsage.tierRoutingEnabled = it })
            }
        }
        row {
            Column(Modifier.fillMaxWidth().padding(vertical = YY.s(10)), verticalArrangement = Arrangement.spacedBy(YY.s(8))) {
                Column(verticalArrangement = Arrangement.spacedBy(YY.s(2))) {
                    Text(t("每日用量上限"), style = YY.font(11.5f, FontWeight.Medium), color = YY.c.t1)
                    Text(t("达到后自动暂停后台批量任务"), style = YY.font(10f), color = YY.c.t3)
                }
                Row(
                    Modifier.background(YY.c.inset, CircleShape).padding(2.dp),
                    horizontalArrangement = Arrangement.spacedBy(2.dp),
                ) {
                    for ((title, value) in tokenPresets) {
                        val active = dailyTokenLimit == value
                        Text(
                            title,
                            style = YY.font(10.5f, if (active) FontWeight.Medium else FontWeight.Normal),
                            color = if (active) YY.c.t1 else YY.c.t2,
                            modifier = Modifier
                                .clip(CircleShape)
                                .background(if (active) YY.c.card else Color.Transparent)
                                .clickable { dailyTokenLimit = value; CompanionUsage.dailyTokenLimit = value }
                                .padding(horizontal = 10.dp, vertical = 4.dp),
                        )
                    }
                }
            }
        }
    }
    YYNote(t("统计为本机估算值，以服务商实际账单为准。书伴问答与前情提要的纯文本缓存会随同步在你的设备间复用。"))
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
                Icon(Icons.waveformEcg, null, tint = YY.c.ok, modifier = Modifier.size(YY.s(14)))
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
    var loading by remember { mutableStateOf(true) }
    var failed by remember { mutableStateOf<String?>(null) }
    var typed by remember { mutableStateOf("") }
    var query by remember { mutableStateOf("") }
    val scope = rememberCoroutineScope()
    fun refresh() {
        loading = true
        failed = null
        scope.launch {
            models = try {
                Companion.models(config).sorted()
            } catch (e: Exception) {
                failed = Companion.message(e)
                models ?: emptyList()
            }
            loading = false
        }
    }
    LaunchedEffect(id) { refresh() }
    fun pick(m: String) {
        config = config.copy(model = m.trim())
        config.save()
        back()
    }
    YYPage {
        Spacer(Modifier.height(YY.s(12)))
        YYGroup {
            row {
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !loading) { refresh() },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.clockwise, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(14)))
                    Text(t("获取模型列表"), style = YY.font(11.5f), color = YY.c.t1, modifier = Modifier.weight(1f))
                    when {
                        loading -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                        !models.isNullOrEmpty() -> Text(t("共 %lld 个模型", models!!.size), style = YY.font(10.5f, tnum = true), color = YY.c.t3)
                    }
                }
            }
        }
        Spacer(Modifier.height(YY.s(10)))
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
            if (failed == null && list.isEmpty() && !loading) {
                YYNote(t("服务商返回了空的模型列表，可以在下方手动输入模型 ID。"))
            }
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
    ModalBottomSheet(dismiss, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle() }) {
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
    ModalBottomSheet(close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle(bottom = 2.dp) }) {
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
    Row(
        modifier.height(YY.s(36)).clip(RoundedCornerShape(YY.s(8))).clickable(onClick = onClick).padding(horizontal = YY.s(8)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
    ) {
        ProviderLogo(p)
        Text(p.title, style = YY.font(11.5f), color = YY.c.t1, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
    }
}

/** One service's key, address and live model list.
 *  A key saved before shows as "sk-…3f9a" with 更换, so opening the form never wipes it.
 *  OpenRouter signs in instead; a key can still be pasted. */
@Composable
private fun ProviderForm(provider: CompanionProvider, adding: Boolean, back: (() -> Unit)?, close: () -> Unit, done: () -> Unit) {
    var config by remember(provider.id) { mutableStateOf(CompanionConfig.saved(provider)) }
    var name by remember(provider.id) { mutableStateOf(provider.title) }
    var keyLocked by remember(provider.id) { mutableStateOf(config.key.isNotEmpty()) }
    var showKey by remember { mutableStateOf(false) }
    var signingIn by remember { mutableStateOf(false) }
    var fetchingModels by remember { mutableStateOf(false) }
    var fetchedModels by remember(provider.id) { mutableStateOf<List<String>>(emptyList()) }
    var choosingModel by remember(provider.id) { mutableStateOf(false) }
    var modelQuery by remember(provider.id) { mutableStateOf("") }
    var failed by remember { mutableStateOf<String?>(null) }
    var removing by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()
    val context = LocalContext.current
    val draft = config.copy(provider = if (provider.isCustom) CompanionProvider.custom(name.trim(), provider.id) else provider)
    fun fetchModels(openPicker: Boolean = true) {
        if (fetchingModels) return
        if (!draft.isConnected) {
            failed = if (provider.needsKey && draft.key.isEmpty()) t("请先填好 API Key") else t("请先填好接口地址")
            return
        }
        fetchingModels = true
        failed = null
        scope.launch {
            try {
                val found = Companion.models(draft).sorted()
                fetchedModels = found
                if (found.isEmpty()) {
                    failed = t("服务商返回了空的模型列表，可手动输入模型 ID")
                } else {
                    if (config.model.isEmpty() || (config.model == provider.defaultModel && config.model !in found)) {
                        config = config.copy(model = found.first())
                    }
                    if (openPicker) choosingModel = true
                }
            } catch (e: Exception) {
                failed = Companion.message(e)
            }
            fetchingModels = false
        }
    }
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

    if (choosingModel) {
        Title(t("选择模型"), { choosingModel = false }, close)
        Spacer(Modifier.height(YY.s(10)))
        YYGroup {
            row {
                Row(
                    Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !fetchingModels) { fetchModels(openPicker = false) },
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
                ) {
                    Icon(Icons.clockwise, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(14)))
                    Text(t("获取模型列表"), style = YY.font(11.5f), color = YY.c.t1, modifier = Modifier.weight(1f))
                    when {
                        fetchingModels -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                        fetchedModels.isNotEmpty() -> Text(t("共 %lld 个模型", fetchedModels.size), style = YY.font(10.5f, tnum = true), color = YY.c.t3)
                    }
                }
            }
        }
        Spacer(Modifier.height(YY.s(10)))
        Row(Modifier.fillMaxWidth().height(YY.s(34)).background(YY.c.inset, RoundedCornerShape(YY.s(10))).padding(horizontal = YY.s(10)),
            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(6))) {
            Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(YY.s(14)))
            Box(Modifier.weight(1f)) { Field(modelQuery, t("搜索或输入模型 ID")) { modelQuery = it } }
        }
        Spacer(Modifier.height(YY.s(10)))
        val q = modelQuery.trim()
        val shown = fetchedModels.filter { q.isEmpty() || it.contains(q, ignoreCase = true) }
        if (shown.isNotEmpty()) YYGroup {
            for (m in shown) row {
                YYRow(m, onClick = { config = config.copy(model = m); choosingModel = false }, trailing = {
                    if (m == config.model) Icon(Icons.check, t("在用"), tint = YY.tint.accent, modifier = Modifier.size(YY.s(13)))
                })
            }
        }
        if (q.isNotEmpty() && q !in fetchedModels) {
            Spacer(Modifier.height(YY.s(8)))
            YYGroup {
                row {
                    YYRow(t("使用 “%@”", q), onClick = { config = config.copy(model = q); choosingModel = false })
                }
            }
        }
        failed?.let { YYNote(it, YY.c.danger) }
        return
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
        row {
            YYField(t("模型"), 64f) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.weight(1f)) { Field(config.model, provider.modelHint) { config = config.copy(model = it.trim()) } }
                    if (fetchedModels.isNotEmpty()) {
                        Text(t("选择"), style = YY.font(10.5f, FontWeight.Medium), color = YY.c.t1,
                            modifier = Modifier.clickable { choosingModel = true }.padding(start = YY.s(8), top = YY.s(6), bottom = YY.s(6)))
                    }
                }
            }
        }
        row {
            Row(
                Modifier.fillMaxWidth().heightIn(min = YY.s(38)).clickable(enabled = !fetchingModels) { fetchModels(openPicker = true) },
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8)),
            ) {
                Icon(Icons.sync, null, tint = YY.c.t1, modifier = Modifier.size(YY.s(14)))
                Text(t("获取模型列表"), style = YY.font(11.5f), color = YY.c.t1, modifier = Modifier.weight(1f))
                when {
                    fetchingModels -> CircularProgressIndicator(Modifier.size(YY.s(12)), color = YY.c.t3, strokeWidth = 1.5.dp)
                    fetchedModels.isNotEmpty() -> Text(t("已获取 %lld 个", fetchedModels.size), style = YY.font(10.5f, tnum = true), color = YY.c.t3)
                    !draft.isConnected -> Text(if (provider.needsKey && draft.key.isEmpty()) t("先填 Key") else t("先填接口地址"), style = YY.font(10.5f), color = YY.c.t3)
                }
            }
        }
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
