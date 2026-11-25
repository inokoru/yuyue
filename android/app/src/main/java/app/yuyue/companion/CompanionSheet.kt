package app.yuyue.companion

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.ClickableText
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYMotion
import app.yuyue.design.YYSegments
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.pressable
import app.yuyue.reader.ReaderModel
import app.yuyue.t

/** 书伴 on the phone, as on iPhone: the talk in a sheet over the page; a 出处
 *  tag turns the page underneath, `/` opens quick reading commands, and answers can be saved
 *  straight into 想法. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CompanionSheet(model: ReaderModel, settings: () -> Unit, dismiss: () -> Unit) {
    val companion = model.companion
    var draft by remember { mutableStateOf(TextFieldValue("")) }
    var slashDismissedFor by remember { mutableStateOf<String?>(null) }
    var slashManualOpen by remember { mutableStateOf(false) }
    var pickingThought by remember { mutableStateOf(false) }
    var pickedMessages by remember { mutableStateOf<Set<BookCompanion.MessagePick>>(emptySet()) }
    var previewMerged by remember { mutableStateOf(false) }
    val focusRequester = remember { FocusRequester() }
    val readUpTo = model.bookPage?.let { t("截至第 %lld 页", it) } ?: t("截至当前位置")

    fun allSelectablePicks(): Set<BookCompanion.MessagePick> {
        val out = LinkedHashSet<BookCompanion.MessagePick>()
        for (turn in companion.turns) {
            if (turn.question.trim().isNotEmpty()) out += BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Question)
            if (turn.answer?.notYet == false) out += BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Answer)
        }
        return out
    }

    fun enterPicking(initial: BookCompanion.MessagePick? = null) {
        slashManualOpen = false
        slashDismissedFor = null
        pickedMessages = initial?.let { setOf(it) } ?: emptySet()
        previewMerged = false
        pickingThought = true
    }

    fun exitPicking() {
        pickingThought = false
        previewMerged = false
        pickedMessages = emptySet()
    }

    fun togglePick(pick: BookCompanion.MessagePick) {
        pickedMessages = if (pick in pickedMessages) pickedMessages - pick else pickedMessages + pick
        if (pickedMessages.isEmpty()) previewMerged = false
    }

    val promptVersion = CompanionPrompts.version
    val slashQuery = CompanionCommand.slashQuery(draft.text)?.takeIf { slashDismissedFor != draft.text }
        ?: if (slashManualOpen) "" else null
    val slashMatches = remember(slashQuery, promptVersion) {
        if (slashQuery != null) CompanionCommand.matching(slashQuery) else emptyList()
    }

    fun send(text: String, action: CompanionCommand.Action? = null) {
        val q = text.trim()
        if (q.isEmpty() || companion.busy) return
        companion.ask(q, model.companionQuote, anchor = model.companionAnchor, action = action)
        draft = TextFieldValue("")
        slashManualOpen = false
        slashDismissedFor = null
        model.clearCompanionQuote()
    }

    fun executeCommand(command: CompanionCommand, argument: String) {
        val arg = argument.trim()
        when (val act = command.action) {
            CompanionCommand.Action.Recap,
            CompanionCommand.Action.Chapter,
            CompanionCommand.Action.Characters,
            CompanionCommand.Action.Explain,
            CompanionCommand.Action.Quote,
            CompanionCommand.Action.Quiz,
            is CompanionCommand.Action.Custom -> {
                if (companion.busy) return
                command.question(arg, model.companionQuote != null, model.chapterTitle)?.let { q ->
                    slashManualOpen = false
                    slashDismissedFor = null
                    send(q, act)
                }
            }
            is CompanionCommand.Action.Scope -> {
                slashManualOpen = false
                slashDismissedFor = null
                companion.scope = act.scope
                draft = TextFieldValue("")
                if (arg.isNotEmpty() && !companion.busy) {
                    companion.ask(arg, model.companionQuote, scope = act.scope, anchor = model.companionAnchor)
                    model.clearCompanionQuote()
                }
            }
            CompanionCommand.Action.Model, CompanionCommand.Action.Settings -> {
                slashManualOpen = false
                slashDismissedFor = null
                draft = TextFieldValue("")
                settings()
            }
            CompanionCommand.Action.Clear -> {
                slashManualOpen = false
                slashDismissedFor = null
                draft = TextFieldValue("")
                companion.clear()
            }
        }
    }

    fun submitDraft() {
        val parsed = CompanionCommand.parse(draft.text)
        if (parsed != null) {
            executeCommand(parsed.command, parsed.argument)
        } else {
            send(draft.text)
        }
    }

    val canSubmit = remember(draft.text, companion.busy) {
        val trimmed = draft.text.trim()
        if (trimmed.isEmpty()) false
        else {
            val parsed = CompanionCommand.parse(draft.text)
            if (parsed != null && parsed.command.category == CompanionCommand.Category.Control && parsed.argument.isEmpty()) true
            else !companion.busy
        }
    }

    ModalBottomSheet({ model.pager.setSource(null); dismiss() }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = YY.c.bg, dragHandle = { YYSheetDragHandle(bottom = 2.dp) }) {
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.92f).imePadding()) {
            Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                if (pickingThought) {
                    val all = allSelectablePicks()
                    val allChosen = all.isNotEmpty() && pickedMessages == all
                    Text(
                        if (pickedMessages.isEmpty()) t("勾选要存的消息") else t("已选 %lld 条", pickedMessages.size),
                        fontSize = 16.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = YY.c.t1,
                        modifier = Modifier.weight(1f),
                    )
                    Text(
                        if (allChosen) t("取消全选") else t("全选"),
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Medium,
                        color = YY.tint.accent,
                        modifier = Modifier.clickable {
                            pickedMessages = if (allChosen) emptySet() else all
                            if (allChosen) previewMerged = false
                        }.padding(horizontal = 8.dp, vertical = 10.dp),
                    )
                    Text(
                        t("取消"),
                        fontSize = 13.sp,
                        color = YY.c.t2,
                        modifier = Modifier.clickable { exitPicking() }.padding(horizontal = 8.dp, vertical = 10.dp),
                    )
                } else {
                    Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Box(Modifier.size(26.dp).background(YY.tint.accentSoft, RoundedCornerShape(8.dp)), contentAlignment = Alignment.Center) {
                            Icon(Icons.sparkle, null, tint = YY.tint.accent, modifier = Modifier.size(14.dp))
                        }
                        Text(t("书伴"), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                    }
                    if (companion.turns.isNotEmpty()) {
                        Text(t("多选存"), fontSize = 13.sp, color = YY.c.t2, modifier = Modifier.clickable { enterPicking() }.padding(horizontal = 8.dp, vertical = 10.dp))
                        Text(t("清空对话"), fontSize = 13.sp, color = YY.c.t2, modifier = Modifier.clickable { companion.clear() }.padding(horizontal = 8.dp, vertical = 10.dp))
                    }
                    Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClickLabel = t("书伴设置"), onClick = settings), contentAlignment = Alignment.Center) {
                        Icon(Icons.settings, t("书伴设置"), tint = YY.c.t2, modifier = Modifier.size(20.dp))
                    }
                }
            }
            Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                YYSegments(listOf(BookCompanion.Scope.Read to t("仅限已读"), BookCompanion.Scope.Whole to t("全书范围")), companion.scope, { companion.scope = it }, modifier = Modifier.fillMaxWidth())
                Text(if (companion.scope == BookCompanion.Scope.Read) readUpTo + t("，不涉及后续情节") else t("检索整本书，可能包含后续剧透"), fontSize = 12.sp, color = YY.c.t3, style = YY.tnum)
            }
            val list = rememberLazyListState()
            val reduce = YYMotion.isReduceMotion()
            val lastStreamingLen = companion.turns.lastOrNull()?.streaming?.length ?: 0
            LaunchedEffect(companion.turns.size, companion.busy) {
                if (companion.turns.isNotEmpty()) list.animateScrollToItem(companion.turns.size)
            }
            LaunchedEffect(lastStreamingLen) {
                if (lastStreamingLen > 0 && companion.turns.isNotEmpty()) list.scrollToItem(companion.turns.size)
            }
            LazyColumn(Modifier.weight(1f).fillMaxWidth(), state = list, contentPadding = androidx.compose.foundation.layout.PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
                if (companion.turns.isEmpty()) item(key = "welcome") { Box(Modifier.animateItem(fadeInSpec = YYMotion.standard(), placementSpec = YYMotion.place(), fadeOutSpec = YYMotion.exit())) { Welcome(settings) } }
                items(companion.turns, key = { it.id }) { turn ->
                    Column(Modifier.animateItem(fadeInSpec = YYMotion.standard(), placementSpec = YYMotion.place(), fadeOutSpec = YYMotion.exit()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                        val qPick = BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Question)
                        SelectableMessage(enabled = pickingThought, selected = qPick in pickedMessages, onToggle = { togglePick(qPick) }) {
                            Row(Modifier.fillMaxWidth()) { Spacer(Modifier.weight(1f).width(48.dp)); Bubble(turn.question) }
                        }
                        val aPick = BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Answer)
                        SelectableMessage(enabled = pickingThought && turn.answer?.notYet == false, selected = aPick in pickedMessages, onToggle = { togglePick(aPick) }) {
                            androidx.compose.animation.AnimatedContent(
                                targetState = when {
                                    turn.answer != null -> 2
                                    turn.failed != null -> 3
                                    !turn.streaming.isNullOrEmpty() -> 1
                                    else -> 0
                                },
                                transitionSpec = { YYMotion.swap(reduce) },
                                label = "reply",
                            ) { _ ->
                                Reply(
                                    model = model,
                                    turn = turn,
                                    readUpTo = readUpTo,
                                    pickingThought = pickingThought,
                                    onStartPicking = { enterPicking(aPick) },
                                )
                            }
                        }
                    }
                }
            }
            if (pickingThought) {
                val previewText = if (previewMerged && pickedMessages.isNotEmpty()) {
                    BookCompanion.composedThought(companion.turns, pickedMessages)
                } else ""
                Column(
                    Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 10.dp)
                        .background(YY.c.card, RoundedCornerShape(16.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(16.dp))
                        .padding(12.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(
                            t("可同时勾选你的提问与多条回答，合并存为 1 条想法"),
                            fontSize = 12.sp,
                            color = YY.c.t3,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f),
                        )
                        if (pickedMessages.isNotEmpty()) {
                            Text(
                                if (previewMerged) t("收起预览") else t("预览合并"),
                                fontSize = 12.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = YY.tint.accent,
                                modifier = Modifier.clickable { previewMerged = !previewMerged }.padding(horizontal = 2.dp, vertical = 2.dp),
                            )
                        }
                    }
                    if (previewText.isNotEmpty()) {
                        Box(
                            Modifier.fillMaxWidth().heightIn(max = 124.dp)
                                .background(YY.c.inset, RoundedCornerShape(12.dp))
                                .verticalScroll(rememberScrollState())
                                .padding(horizontal = 12.dp, vertical = 9.dp),
                        ) {
                            Text(previewText, fontSize = 13.sp, lineHeight = 20.sp, color = YY.c.t2)
                        }
                    }
                    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        YYButton(t("取消"), ButtonKind.Raised, ButtonSize.Small, onClick = { exitPicking() })
                        Spacer(Modifier.weight(1f))
                        YYButton(
                            if (pickedMessages.size > 1) t("合并存为想法 (%lld)", pickedMessages.size) else t("存为想法 (%lld)", pickedMessages.size),
                            ButtonKind.Main,
                            ButtonSize.Small,
                            enabled = pickedMessages.isNotEmpty(),
                        ) {
                            if (companion.saveThought(pickedMessages)) exitPicking()
                        }
                    }
                }
            } else Column(Modifier.padding(horizontal = 16.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (slashMatches.isNotEmpty()) {
                    SlashCommandCard(
                        commands = slashMatches,
                        currentScope = companion.scope,
                        busy = companion.busy,
                        onSelect = { executeCommand(it, "") },
                    )
                } else {
                    val quote = model.companionQuote
                    var lastQuote by remember { mutableStateOf(quote) }
                    if (quote != null) lastQuote = quote
                    androidx.compose.animation.AnimatedVisibility(
                        quote != null,
                        enter = androidx.compose.animation.fadeIn(YYMotion.standard()) + androidx.compose.animation.expandVertically(YYMotion.layout()),
                        exit = androidx.compose.animation.fadeOut(YYMotion.exit()) + androidx.compose.animation.shrinkVertically(YYMotion.layout()),
                    ) {
                        lastQuote?.let { q ->
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                Box(Modifier.width(1.5.dp).height(36.dp).background(YY.tint.accent))
                                Text(q, fontFamily = YY.serif, fontSize = 13.sp, color = YY.c.t2, maxLines = 2, modifier = Modifier.weight(1f))
                                Box(Modifier.size(36.dp).clip(CircleShape).clickable(onClickLabel = t("不带这句")) { model.clearCompanionQuote() }, contentAlignment = Alignment.CenterEnd) {
                                    Icon(Icons.close, t("不带这句"), tint = YY.c.t3, modifier = Modifier.size(20.dp))
                                }
                            }
                        }
                    }
                    val suggestions = remember(model.companionQuote, companion.turns.lastOrNull()?.answer?.next, promptVersion) {
                        CompanionPrompts.suggestions(
                            hasQuote = model.companionQuote != null,
                            followUps = companion.turns.lastOrNull()?.answer?.next,
                            quoteDefaults = listOf(t("这句是什么意思？"), t("为什么这么写？")),
                            startDefaults = listOf(t("前面发生了什么？"), t("这一章讲了什么？")),
                        )
                    }
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        for (s in suggestions) {
                            Box(
                                Modifier.heightIn(min = 32.dp).background(YY.c.card, CircleShape).border(0.5.dp, YY.c.line, CircleShape)
                                    .clip(CircleShape).pressable(enabled = !companion.busy, scale = 0.97f) { send(s) }.padding(horizontal = 13.dp, vertical = 6.dp),
                                contentAlignment = Alignment.Center,
                            ) {
                                Text(s, fontSize = 13.sp, color = YY.c.t2)
                            }
                        }
                    }
                }
                Row(
                    Modifier.fillMaxWidth().background(YY.c.card, RoundedCornerShape(22.dp)).border(0.5.dp, YY.c.line, RoundedCornerShape(22.dp)).padding(start = 8.dp, end = 4.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    val slashActive = slashMatches.isNotEmpty()
                    Box(
                        Modifier.size(34.dp).clip(CircleShape)
                            .background(if (slashActive) YY.c.inset else androidx.compose.ui.graphics.Color.Transparent, CircleShape)
                            .clickable(onClickLabel = t("快捷指令")) {
                                if (slashActive) {
                                    if (draft.text == "/" || draft.text == "／") draft = TextFieldValue("")
                                    slashManualOpen = false
                                    if (CompanionCommand.slashQuery(draft.text) != null) slashDismissedFor = draft.text
                                } else {
                                    slashDismissedFor = null
                                    if (draft.text.isEmpty()) {
                                        draft = TextFieldValue("/", TextRange(1))
                                    } else {
                                        slashManualOpen = true
                                    }
                                    runCatching { focusRequester.requestFocus() }
                                }
                            },
                        contentAlignment = Alignment.Center,
                    ) {
                        Text("/", fontFamily = FontFamily.Monospace, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = if (slashActive) YY.c.t1 else YY.c.t2)
                    }
                    BasicTextField(
                        draft,
                        onValueChange = { next ->
                            if (slashDismissedFor != next.text) slashDismissedFor = null
                            if (CompanionCommand.slashQuery(next.text) != null || next.text.isNotEmpty()) slashManualOpen = false
                            draft = next
                        },
                        maxLines = 4,
                        textStyle = TextStyle(fontSize = 16.sp, color = YY.c.t1),
                        cursorBrush = SolidColor(YY.tint.accent),
                        modifier = Modifier.weight(1f).heightIn(min = 44.dp).padding(horizontal = 6.dp, vertical = 11.dp).focusRequester(focusRequester),
                        decorationBox = { inner ->
                            if (draft.text.isEmpty()) {
                                Text(
                                    if (companion.turns.isEmpty()) t("问这本书，或输入 / …") else t("继续问，或输入 / …"),
                                    fontSize = 16.sp,
                                    color = YY.c.t3,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                            inner()
                        },
                    )
                    Box(Modifier.size(40.dp).clip(CircleShape).clickable(enabled = canSubmit, onClickLabel = t("发送")) { submitDraft() }, contentAlignment = Alignment.Center) {
                        val ground by androidx.compose.animation.animateColorAsState(if (canSubmit) YY.tint.black else YY.c.inset, YYMotion.quick(), label = "send")
                        val ink by androidx.compose.animation.animateColorAsState(if (canSubmit) YY.tint.onBlack else YY.c.t3, YYMotion.quick(), label = "sendInk")
                        Box(Modifier.size(34.dp).background(ground, CircleShape), contentAlignment = Alignment.Center) {
                            Icon(Icons.send, t("发送"), tint = ink, modifier = Modifier.size(16.dp))
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun SlashCommandCard(
    commands: List<CompanionCommand>,
    currentScope: BookCompanion.Scope,
    busy: Boolean,
    onSelect: (CompanionCommand) -> Unit,
) {
    Column(
        Modifier.fillMaxWidth().heightIn(max = 220.dp)
            .background(YY.c.card, RoundedCornerShape(14.dp))
            .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp))
            .verticalScroll(rememberScrollState())
            .padding(6.dp),
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        for (cmd in commands) {
            val disabled = busy && cmd.category == CompanionCommand.Category.Ask
            val isCurrentScope = (cmd.action as? CompanionCommand.Action.Scope)?.scope == currentScope
            Row(
                Modifier.fillMaxWidth().heightIn(min = 44.dp).clip(RoundedCornerShape(10.dp))
                    .clickable(enabled = !disabled) { onSelect(cmd) }
                    .alpha(if (disabled) 0.42f else 1f)
                    .padding(horizontal = 10.dp, vertical = 7.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(10.dp),
            ) {
                Box(Modifier.size(28.dp).background(YY.c.inset, RoundedCornerShape(8.dp)), contentAlignment = Alignment.Center) {
                    Icon(cmd.icon, null, tint = YY.c.t2, modifier = Modifier.size(15.dp))
                }
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(1.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(cmd.slashName, fontFamily = FontFamily.Monospace, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                        Text(cmd.title, fontSize = 13.5.sp, fontWeight = FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    Text(cmd.subtitle, fontSize = 12.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (isCurrentScope) {
                    Box(Modifier.background(YY.tint.accentSoft, CircleShape).padding(horizontal = 6.dp, vertical = 2.dp)) {
                        Text(t("当前"), fontSize = 11.sp, fontWeight = FontWeight.Medium, color = YY.tint.accent)
                    }
                }
            }
        }
    }
}

@Composable
private fun Welcome(settings: () -> Unit) {
    val config = CompanionConfig.current
    Column(Modifier.fillMaxWidth().background(YY.c.inset, RoundedCornerShape(16.dp)).padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Text(t("读到哪儿问到哪儿"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
        Text(t("可以问人物关系、这句是什么意思、前面发生了什么。回答标着出处，点一下就翻到那句；也可点回答下方的「存为想法」收进笔记。"), fontSize = 14.sp, lineHeight = 21.sp, color = YY.c.t2)
        if (config?.isReady != true) {
            Text(if (config == null) t("还没添加服务商") else t("还没填好 %@ 的 Key", config.provider.title), fontSize = 13.sp, color = YY.c.t3)
            YYButton(t("去设置 · 书伴"), ButtonKind.Raised, ButtonSize.Small, onClick = settings)
        }
    }
}

@Composable
private fun Bubble(text: String) {
    Text(
        text, fontSize = 15.sp, lineHeight = 21.sp, color = YY.c.t1,
        modifier = Modifier
            .background(YY.c.pill, RoundedCornerShape(topStart = 16.dp, bottomStart = 16.dp, bottomEnd = 4.dp, topEnd = 16.dp))
            .border(0.5.dp, YY.c.pillLine, RoundedCornerShape(topStart = 16.dp, bottomStart = 16.dp, bottomEnd = 4.dp, topEnd = 16.dp))
            .padding(horizontal = 14.dp, vertical = 9.dp),
    )
}

@Composable
private fun SelectableMessage(
    enabled: Boolean,
    selected: Boolean,
    onToggle: () -> Unit,
    content: @Composable () -> Unit,
) {
    if (!enabled) {
        content()
        return
    }
    Row(
        Modifier.fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .background(if (selected) YY.tint.accentSoft.copy(alpha = 0.45f) else androidx.compose.ui.graphics.Color.Transparent)
            .border(1.dp, if (selected) YY.tint.accent.copy(alpha = 0.34f) else androidx.compose.ui.graphics.Color.Transparent, RoundedCornerShape(12.dp))
            .clickable(onClick = onToggle)
            .padding(horizontal = 8.dp, vertical = 6.dp),
        verticalAlignment = Alignment.Top,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(
            if (selected) Icons.checkCircleFill else Icons.circle,
            null,
            tint = if (selected) YY.tint.accent else YY.c.t3,
            modifier = Modifier.padding(top = 4.dp).size(18.dp),
        )
        Box(Modifier.weight(1f)) { content() }
    }
}

@Composable
private fun Reply(
    model: ReaderModel,
    turn: BookCompanion.Turn,
    readUpTo: String,
    pickingThought: Boolean,
    onStartPicking: () -> Unit,
) {
    val companion = model.companion
    val answer = turn.answer
    when {
        answer != null && answer.notYet -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(t("%@，这件事可能要到后面才写。切换至「全书范围」即可作答，但可能包含后续剧透。", readUpTo), fontSize = 15.sp, lineHeight = 23.sp, color = YY.c.t1)
            Row(
                Modifier.heightIn(min = 34.dp).background(YY.c.card, CircleShape).border(0.5.dp, YY.c.line, CircleShape).clip(CircleShape).clickable(enabled = !companion.busy) { companion.ask(turn.question, turn.quote, BookCompanion.Scope.Whole, anchor = turn.anchor) }.padding(horizontal = 14.dp, vertical = 6.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(t("用全书回答"), fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                Text(t("可能剧透"), fontSize = 11.sp, fontWeight = FontWeight.SemiBold, color = YY.c.warn)
            }
        }
        answer != null -> Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            val text = cited(answer.text, answer.sources, companion.shown)
            if (pickingThought) {
                Text(text, style = TextStyle(fontSize = 15.sp, lineHeight = 23.sp, color = YY.c.t1))
            } else {
                @Suppress("DEPRECATION")
                ClickableText(text, style = TextStyle(fontSize = 15.sp, lineHeight = 23.sp, color = YY.c.t1)) { offset ->
                    text.getStringAnnotations("cite", offset, offset).firstOrNull()?.item?.toIntOrNull()?.let { n -> answer.sources[n]?.let(companion::show) }
                }
            }
            if (!pickingThought) {
                val clipboard = androidx.compose.ui.platform.LocalClipboardManager.current
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    SaveThoughtChip(saved = turn.savedMarkID != null) { companion.saveThought(turn.id) }
                    CopyAnswerChip {
                        clipboard.setText(AnnotatedString(BookCompanion.noteText(answer.text)))
                        model.show(app.yuyue.library.Library.Toast(app.yuyue.library.Library.Toast.Icon.Check, t("复制好了"), ""))
                    }
                    MultiPickChip(onClick = onStartPicking)
                }
            }
            Text(
                if (turn.scope == BookCompanion.Scope.Whole) answer.label + t(" · 用了全书") else answer.label,
                fontSize = 11.sp,
                color = YY.c.t3,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
            )
        }
        turn.failed != null -> Text(turn.failed, fontSize = 13.sp, color = YY.c.danger)
        !turn.streaming.isNullOrEmpty() -> {
            val preview = cited(turn.streaming, emptyMap(), null, streaming = true)
            Text(preview, style = TextStyle(fontSize = 15.sp, lineHeight = 23.sp, color = YY.c.t1))
        }
        else -> Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp, color = YY.c.t3)
            Text(if (turn.thinking) t("正在思考…") else t("正在翻阅原文…"), fontSize = 13.sp, color = YY.c.t3)
        }
    }
}

@Composable
private fun SaveThoughtChip(saved: Boolean, onClick: () -> Unit) {
    Row(
        Modifier.height(28.dp)
            .background(if (saved) YY.c.ok.copy(alpha = 0.12f) else YY.c.card, CircleShape)
            .border(0.5.dp, if (saved) YY.c.ok.copy(alpha = 0.32f) else YY.c.line, CircleShape)
            .clip(CircleShape)
            .pressable(scale = 0.96f, onClick = onClick)
            .padding(horizontal = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Icon(if (saved) Icons.check else Icons.noteEdit, null, tint = if (saved) YY.c.ok else YY.c.t1, modifier = Modifier.size(12.dp))
        Text(
            if (saved) t("已存想法") else t("存为想法"),
            fontSize = 12.sp,
            fontWeight = if (saved) FontWeight.SemiBold else FontWeight.Medium,
            color = if (saved) YY.c.ok else YY.c.t1,
        )
    }
}

@Composable
private fun CopyAnswerChip(onClick: () -> Unit) {
    Row(
        Modifier.height(28.dp)
            .background(YY.c.card, CircleShape)
            .border(0.5.dp, YY.c.line, CircleShape)
            .clip(CircleShape)
            .pressable(scale = 0.96f, onClick = onClick)
            .padding(horizontal = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Icon(Icons.copy, null, tint = YY.c.t2, modifier = Modifier.size(12.dp))
        Text(t("复制"), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = YY.c.t2)
    }
}

@Composable
private fun MultiPickChip(onClick: () -> Unit) {
    Row(
        Modifier.height(28.dp)
            .background(YY.c.card, CircleShape)
            .border(0.5.dp, YY.c.line, CircleShape)
            .clip(CircleShape)
            .pressable(scale = 0.96f, onClick = onClick)
            .padding(horizontal = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Icon(Icons.list, null, tint = YY.c.t2, modifier = Modifier.size(12.dp))
        Text(t("多选"), fontSize = 12.sp, fontWeight = FontWeight.Medium, color = YY.c.t2)
    }
}

/** Renders Markdown (bold, lists, headings, inline code) and turns each `[n]` into a clickable `第 N 页` tag. */
@Composable
private fun cited(rawText: String, sources: Map<Int, BookCompanion.Source>, shown: BookCompanion.Source?, streaming: Boolean = false): AnnotatedString {
    val accent = YY.tint.accent
    val soft = YY.tint.accentSoft
    val card = YY.c.card
    val inset = YY.c.inset
    val t3 = YY.c.t3
    return buildAnnotatedString {
        for (segment in BookCompanion.richSegments(rawText, streaming = streaming)) {
            when (segment) {
                is BookCompanion.RichSegment.Text -> append(segment.value)
                is BookCompanion.RichSegment.Bold -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(segment.value) }
                is BookCompanion.RichSegment.Italic -> withStyle(SpanStyle(fontStyle = androidx.compose.ui.text.font.FontStyle.Italic)) { append(segment.value) }
                is BookCompanion.RichSegment.Code -> withStyle(SpanStyle(fontFamily = FontFamily.Monospace, fontSize = 13.5.sp, background = inset)) { append(" ${segment.value} ") }
                is BookCompanion.RichSegment.Strike -> withStyle(SpanStyle(color = t3, textDecoration = androidx.compose.ui.text.style.TextDecoration.LineThrough)) { append(segment.value) }
                BookCompanion.RichSegment.Bullet -> withStyle(SpanStyle(fontWeight = FontWeight.Bold, color = accent)) { append("•\u00A0") }
                BookCompanion.RichSegment.QuoteBar -> withStyle(SpanStyle(color = t3)) { append("▎\u00A0") }
                is BookCompanion.RichSegment.Heading -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold, fontSize = 15.5.sp)) { append(segment.value) }
                is BookCompanion.RichSegment.Citation -> {
                    val source = sources[segment.number]
                    if (source != null) {
                        val place = source.page?.let { t("第 %lld 页", it) } ?: t("出处")
                        val label = "\u00A0" + place + (if (source.later) t(" · 未读") else "") + "\u00A0"
                        val on = shown == source
                        append(" ")
                        pushStringAnnotation("cite", "${segment.number}")
                        withStyle(SpanStyle(fontSize = 11.sp, fontWeight = FontWeight.SemiBold, fontFeatureSettings = "tnum", color = if (on) card else accent, background = if (on) accent else soft)) { append(label) }
                        pop()
                    }
                }
                BookCompanion.RichSegment.Newline -> append("\n")
            }
        }
    }
}
