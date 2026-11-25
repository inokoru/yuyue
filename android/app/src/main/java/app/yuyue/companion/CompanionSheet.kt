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
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextRange
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYMotion
import app.yuyue.design.YYSegments
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.pressable
import app.yuyue.design.rememberPref
import app.yuyue.reader.ReaderModel
import app.yuyue.t
import kotlinx.coroutines.launch

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
    var linesOpen by remember { mutableStateOf(false) }
    var modelPickerOpen by remember { mutableStateOf(false) }
    var modelTick by remember { mutableIntStateOf(0) }
    var pickingThought by remember { mutableStateOf(false) }
    var pickedMessages by remember { mutableStateOf<Set<BookCompanion.MessagePick>>(emptySet()) }
    var previewMerged by remember { mutableStateOf(false) }
    val focusRequester = remember { FocusRequester() }
    val keyboard = LocalSoftwareKeyboardController.current
    val activeProviderPref by rememberPref("companion.provider", "")
    val addedProvidersPref by rememberPref("companion.added", "")
    val currentConfig = remember(activeProviderPref, addedProvidersPref, modelTick) { CompanionConfig.current }
    val readUpTo = model.bookPage?.let { t("截至第 %lld 页", it) } ?: t("截至当前位置")

    fun allSelectablePicks(): Set<BookCompanion.MessagePick> {
        val out = LinkedHashSet<BookCompanion.MessagePick>()
        for (turn in companion.turns) {
            if (turn.question.trim().isNotEmpty()) out += BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Question)
            if (turn.answer?.notYet == false) out += BookCompanion.MessagePick(turn.id, BookCompanion.MessagePick.Role.Answer)
        }
        return out
    }

    fun dismissSlashMenu() {
        slashManualOpen = false
        if (CompanionCommand.slashQuery(draft.text) != null) slashDismissedFor = draft.text
    }

    fun enterPicking(initial: BookCompanion.MessagePick? = null) {
        slashManualOpen = false
        slashDismissedFor = null
        linesOpen = false
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
    val readyQuestions = remember(model.companionQuote, companion.turns.lastOrNull()?.answer?.next, promptVersion) {
        CompanionPrompts.suggestions(
            hasQuote = model.companionQuote != null,
            followUps = companion.turns.lastOrNull()?.answer?.next,
            quoteDefaults = listOf(t("这句是什么意思？"), t("为什么这么写？")),
            startDefaults = listOf(t("前面发生了什么？"), t("这一章讲了什么？")),
        )
    }

    fun send(text: String, action: CompanionCommand.Action? = null) {
        val q = text.trim()
        if (q.isEmpty() || companion.busy) return
        companion.ask(q, model.companionQuote, anchor = model.companionAnchor, action = action)
        draft = TextFieldValue("")
        linesOpen = false
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
                    linesOpen = false
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
            CompanionCommand.Action.Model -> {
                slashManualOpen = false
                slashDismissedFor = null
                linesOpen = false
                draft = TextFieldValue("")
                keyboard?.hide()
                modelPickerOpen = true
            }
            CompanionCommand.Action.Settings -> {
                slashManualOpen = false
                slashDismissedFor = null
                linesOpen = false
                draft = TextFieldValue("")
                keyboard?.hide()
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
            } else Column(Modifier.padding(horizontal = 14.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (slashMatches.isNotEmpty()) {
                    SlashCommandCard(
                        commands = slashMatches,
                        currentScope = companion.scope,
                        busy = companion.busy,
                        onDismiss = {
                            if (draft.text == "/" || draft.text == "／") draft = TextFieldValue("")
                            dismissSlashMenu()
                        },
                        onSelect = { executeCommand(it, "") },
                    )
                } else if (linesOpen && readyQuestions.isNotEmpty()) {
                    ReadyLinesCard(
                        questions = readyQuestions,
                        busy = companion.busy,
                        onDismiss = { linesOpen = false },
                        onSelect = { send(it) },
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
                }
                Column(
                    Modifier.fillMaxWidth()
                        .background(YY.c.card, RoundedCornerShape(18.dp))
                        .border(0.5.dp, YY.c.line, RoundedCornerShape(18.dp))
                        .padding(start = 12.dp, end = 10.dp, top = 10.dp, bottom = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    BasicTextField(
                        draft,
                        onValueChange = { next ->
                            if (slashDismissedFor != next.text) slashDismissedFor = null
                            if (CompanionCommand.slashQuery(next.text) != null || next.text.isNotEmpty()) {
                                slashManualOpen = false
                                linesOpen = false
                            }
                            draft = next
                        },
                        maxLines = 5,
                        textStyle = TextStyle(fontSize = 15.5.sp, color = YY.c.t1),
                        cursorBrush = SolidColor(YY.tint.accent),
                        modifier = Modifier.fillMaxWidth().padding(horizontal = 2.dp).focusRequester(focusRequester),
                        decorationBox = { inner ->
                            if (draft.text.isEmpty()) {
                                Text(
                                    if (companion.turns.isEmpty()) t("问这本书，或输入 / …") else t("继续问，或输入 / …"),
                                    fontSize = 15.5.sp,
                                    color = YY.c.t3,
                                    maxLines = 1,
                                    overflow = TextOverflow.Ellipsis,
                                )
                            }
                            inner()
                        },
                    )
                    Row(
                        Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                    ) {
                        if (readyQuestions.isNotEmpty()) {
                            Box(
                                Modifier.size(32.dp).clip(CircleShape)
                                    .background(if (linesOpen) YY.c.inset else Color.Transparent, CircleShape)
                                    .clickable(enabled = !companion.busy, onClickLabel = t("挑一句现成的")) {
                                        val next = !linesOpen
                                        if (next) {
                                            dismissSlashMenu()
                                            keyboard?.hide()
                                        }
                                        linesOpen = next
                                    },
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.quote, t("挑一句现成的"), tint = if (linesOpen) YY.c.t1 else YY.c.t2, modifier = Modifier.size(16.dp))
                            }
                        }

                        val slashActive = slashMatches.isNotEmpty()
                        Box(
                            Modifier.size(32.dp).clip(CircleShape)
                                .background(if (slashActive) YY.c.inset else Color.Transparent, CircleShape)
                                .clickable(onClickLabel = t("快捷指令")) {
                                    linesOpen = false
                                    if (slashActive) {
                                        if (draft.text == "/" || draft.text == "／") draft = TextFieldValue("")
                                        dismissSlashMenu()
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

                        val modelLabel = when {
                            currentConfig == null -> t("选择模型…")
                            currentConfig.model.isEmpty() -> currentConfig.provider.title
                            else -> CompanionModelCatalog.prettyName(currentConfig.model)
                        }
                        Row(
                            Modifier.height(32.dp)
                                .weight(1f, fill = false)
                                .background(YY.c.bg, CircleShape)
                                .border(0.5.dp, YY.c.line, CircleShape)
                                .clip(CircleShape)
                                .pressable(scale = 0.97f) {
                                    linesOpen = false
                                    dismissSlashMenu()
                                    keyboard?.hide()
                                    modelPickerOpen = true
                                }
                                .padding(start = 7.dp, end = 9.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(5.dp),
                        ) {
                            if (currentConfig != null) {
                                ProviderLogo(currentConfig.provider, 16.dp)
                            } else {
                                Icon(Icons.sparkle, null, tint = YY.c.t3, modifier = Modifier.size(14.dp))
                            }
                            Text(
                                modelLabel,
                                fontSize = 12.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = if (currentConfig?.isReady == true) YY.c.t1 else YY.c.t2,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                                modifier = Modifier.weight(1f, fill = false),
                            )
                            Icon(Icons.chevronUpDown, null, tint = YY.c.t3, modifier = Modifier.size(11.dp))
                        }

                        Spacer(Modifier.weight(1f))

                        val ground by androidx.compose.animation.animateColorAsState(if (canSubmit) YY.tint.black else YY.c.inset, YYMotion.quick(), label = "send")
                        val ink by androidx.compose.animation.animateColorAsState(if (canSubmit) YY.tint.onBlack else YY.c.t3, YYMotion.quick(), label = "sendInk")
                        Box(
                            Modifier.size(32.dp)
                                .clip(CircleShape)
                                .background(ground, CircleShape)
                                .clickable(enabled = canSubmit, onClickLabel = t("发送")) { submitDraft() },
                            contentAlignment = Alignment.Center,
                        ) {
                            Icon(Icons.send, t("发送"), tint = ink, modifier = Modifier.size(15.dp))
                        }
                    }
                }
            }
        }
    }

    if (modelPickerOpen) {
        CompanionModelPickerSheet(
            openSettings = {
                modelPickerOpen = false
                settings()
            },
            onPicked = {
                modelTick++
                modelPickerOpen = false
            },
            dismiss = { modelPickerOpen = false },
        )
    }
}

@Composable
private fun ReadyLinesCard(
    questions: List<String>,
    busy: Boolean,
    onDismiss: () -> Unit,
    onSelect: (String) -> Unit,
) {
    Column(
        Modifier.fillMaxWidth()
            .background(YY.c.card, RoundedCornerShape(14.dp))
            .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(start = 12.dp, end = 6.dp, top = 6.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(t("挑一句现成的"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t3, modifier = Modifier.weight(1f))
            Box(Modifier.size(28.dp).clip(CircleShape).clickable(onClickLabel = t("关闭"), onClick = onDismiss), contentAlignment = Alignment.Center) {
                Icon(Icons.close, t("关闭"), tint = YY.c.t3, modifier = Modifier.size(14.dp))
            }
        }
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Column(
            Modifier.fillMaxWidth().heightIn(max = 220.dp).verticalScroll(rememberScrollState()).padding(6.dp),
            verticalArrangement = Arrangement.spacedBy(2.dp),
        ) {
            for (q in questions) {
                Row(
                    Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp))
                        .clickable(enabled = !busy) { onSelect(q) }
                        .alpha(if (busy) 0.42f else 1f)
                        .padding(horizontal = 10.dp, vertical = 9.dp),
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(q, fontFamily = YY.serif, fontSize = 14.sp, lineHeight = 20.sp, color = YY.c.t1, maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                    Icon(Icons.external, null, tint = YY.c.t3, modifier = Modifier.size(13.dp))
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
    onDismiss: () -> Unit,
    onSelect: (CompanionCommand) -> Unit,
) {
    Column(
        Modifier.fillMaxWidth()
            .background(YY.c.card, RoundedCornerShape(14.dp))
            .border(0.5.dp, YY.c.line, RoundedCornerShape(14.dp)),
    ) {
        Row(
            Modifier.fillMaxWidth().padding(start = 12.dp, end = 6.dp, top = 6.dp, bottom = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(t("指令"), fontSize = 11.5.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t3, modifier = Modifier.weight(1f))
            Box(Modifier.size(28.dp).clip(CircleShape).clickable(onClickLabel = t("关闭"), onClick = onDismiss), contentAlignment = Alignment.Center) {
                Icon(Icons.close, t("关闭"), tint = YY.c.t3, modifier = Modifier.size(14.dp))
            }
        }
        Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Column(
            Modifier.fillMaxWidth().heightIn(max = 220.dp)
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
}

private enum class ModelPickerNav { Starred, Recent, Provider }

private data class ModelEntry(val provider: CompanionProvider, val model: String)

private object CompanionModelCatalog {
    private val starredKey = "companion.models.starred"
    private val recentKey = "companion.models.recent"
    private fun cacheKey(providerId: String) = "companion.models.cache.$providerId"

    fun prettyName(id: String): String {
        val trimmed = id.trim()
        if (trimmed.isEmpty()) return id
        val core = trimmed.substringAfterLast('/')
        return core.replace(Regex("-20\\d{6}$"), "")
    }

    fun routeBadge(id: String): String? {
        val idx = id.indexOf('/')
        if (idx <= 0) return null
        val prefix = id.substring(0, idx).trim()
        return prefix.takeIf { it.isNotEmpty() && it.length <= 14 }
    }

    fun family(id: String): String {
        val lower = id.lowercase()
        return when {
            "claude" in lower -> "Claude"
            "gpt" in lower || lower.startsWith("o1") || lower.startsWith("o3") || lower.startsWith("o4") -> "OpenAI"
            "gemini" in lower -> "Gemini"
            "deepseek" in lower -> "DeepSeek"
            "qwen" in lower || "qwq" in lower -> "Qwen"
            "kimi" in lower || "moonshot" in lower -> "Kimi"
            "glm" in lower -> "GLM"
            "doubao" in lower -> "豆包"
            "grok" in lower -> "Grok"
            "llama" in lower -> "Llama"
            "mistral" in lower -> "Mistral"
            "minimax" in lower -> "MiniMax"
            else -> routeBadge(id)?.replaceFirstChar { it.uppercase() } ?: t("其他")
        }
    }

    fun matches(query: String, id: String, providerTitle: String): Boolean {
        val q = query.lowercase().filterNot { it in " -_./:" }
        if (q.isEmpty()) return true
        val hay = "$id ${prettyName(id)} $providerTitle".lowercase().filterNot { it in " -_./:" }
        if (q in hay) return true
        var qi = 0
        for (ch in hay) {
            if (ch == q[qi]) {
                qi++
                if (qi == q.length) return true
            }
        }
        return false
    }

    private fun readPairs(key: String): List<Pair<String, String>> =
        Prefs.string(key).split('\n').mapNotNull { line ->
            val sep = line.indexOf('|')
            if (sep <= 0 || sep >= line.lastIndex) null
            else line.substring(0, sep) to line.substring(sep + 1)
        }

    private fun writePairs(key: String, pairs: List<Pair<String, String>>) {
        Prefs.set(key, pairs.joinToString("\n") { "${it.first}|${it.second}" })
    }

    fun isStarred(providerId: String, model: String): Boolean =
        readPairs(starredKey).any { it.first == providerId && it.second == model }

    fun toggleStar(providerId: String, model: String) {
        val cur = readPairs(starredKey).toMutableList()
        val idx = cur.indexOfFirst { it.first == providerId && it.second == model }
        if (idx >= 0) cur.removeAt(idx) else cur.add(0, providerId to model)
        writePairs(starredKey, cur.take(40))
    }

    fun starredEntries(providers: List<CompanionProvider>): List<ModelEntry> {
        val byId = providers.associateBy { it.id }
        return readPairs(starredKey).mapNotNull { (pid, m) -> byId[pid]?.let { ModelEntry(it, m) } }
    }

    fun recordRecent(providerId: String, model: String) {
        if (model.isBlank()) return
        val cur = readPairs(recentKey).filterNot { it.first == providerId && it.second == model }
        writePairs(recentKey, (listOf(providerId to model) + cur).take(20))
    }

    fun recentEntries(providers: List<CompanionProvider>): List<ModelEntry> {
        val byId = providers.associateBy { it.id }
        return readPairs(recentKey).mapNotNull { (pid, m) -> byId[pid]?.let { ModelEntry(it, m) } }
    }

    fun cached(providerId: String): List<String> =
        Prefs.string(cacheKey(providerId)).split('\n').filter { it.isNotBlank() }

    fun saveCache(providerId: String, models: List<String>) {
        Prefs.set(cacheKey(providerId), models.distinct().joinToString("\n"))
    }

    fun pool(provider: CompanionProvider, live: List<String>?): List<String> {
        val saved = CompanionConfig.saved(provider).model
        val base = when {
            !live.isNullOrEmpty() -> live
            cached(provider.id).isNotEmpty() -> cached(provider.id)
            else -> listOfNotNull(saved.ifBlank { null }, provider.defaultModel.ifBlank { null }, provider.defaultFastModel.ifBlank { null })
        }
        val out = LinkedHashSet<String>()
        if (saved.isNotBlank()) out += saved
        out.addAll(base)
        return out.toList()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CompanionModelPickerSheet(
    openSettings: () -> Unit,
    onPicked: () -> Unit,
    dismiss: () -> Unit,
) {
    val allProviders = remember { CompanionProvider.added }
    val readyProviders = remember(allProviders) { allProviders.filter { CompanionConfig.saved(it).isConnected } }
    val current = remember { CompanionConfig.current }
    var starTick by remember { mutableIntStateOf(0) }
    val starred = remember(starTick, readyProviders) { CompanionModelCatalog.starredEntries(readyProviders) }
    val recents = remember(starTick, readyProviders) { CompanionModelCatalog.recentEntries(readyProviders) }

    var navKind by remember {
        mutableStateOf(
            when {
                starred.isNotEmpty() && current?.isReady == true -> ModelPickerNav.Starred
                current != null -> ModelPickerNav.Provider
                readyProviders.isNotEmpty() -> ModelPickerNav.Provider
                else -> ModelPickerNav.Starred
            }
        )
    }
    var selectedProviderId by remember { mutableStateOf(current?.provider?.id ?: allProviders.firstOrNull()?.id) }
    var query by remember { mutableStateOf("") }
    var selectedFamily by remember { mutableStateOf<String?>(null) }
    var livePools by remember { mutableStateOf<Map<String, List<String>>>(emptyMap()) }
    var loadingProvider by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()

    fun fetchModels(p: CompanionProvider, force: Boolean) {
        if (!force && livePools.containsKey(p.id)) return
        val cfg = CompanionConfig.saved(p)
        if (!cfg.isConnected) return
        scope.launch {
            loadingProvider = p.id
            val fetched = runCatching { Companion.models(cfg).sorted() }.getOrNull()
            if (!fetched.isNullOrEmpty()) {
                CompanionModelCatalog.saveCache(p.id, fetched)
                livePools = livePools + (p.id to fetched)
            }
            if (loadingProvider == p.id) loadingProvider = null
        }
    }

    LaunchedEffect(readyProviders) {
        for (p in readyProviders) {
            if (CompanionModelCatalog.cached(p.id).isEmpty()) fetchModels(p, force = false)
        }
    }

    LaunchedEffect(navKind, selectedProviderId) {
        selectedFamily = null
        if (navKind == ModelPickerNav.Provider) {
            allProviders.firstOrNull { it.id == selectedProviderId }?.let { p ->
                if (CompanionConfig.saved(p).isConnected) fetchModels(p, force = false)
            }
        }
    }

    fun pick(entry: ModelEntry) {
        current?.takeIf { it.isReady }?.let { CompanionModelCatalog.recordRecent(it.provider.id, it.model) }
        val next = CompanionConfig.saved(entry.provider).copy(model = entry.model)
        next.choose()
        CompanionModelCatalog.recordRecent(entry.provider.id, entry.model)
        onPicked()
    }

    ModalBottomSheet(
        dismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
        dragHandle = { YYSheetDragHandle(bottom = 2.dp) },
    ) {
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.86f).imePadding()) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(t("书伴设置"), fontSize = 13.5.sp, color = YY.tint.accent, modifier = Modifier.clickable(onClick = openSettings).padding(vertical = 8.dp))
                Text(t("选择模型"), fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, textAlign = TextAlign.Center, modifier = Modifier.weight(1f))
                Text(t("完成"), fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, color = YY.tint.accent, modifier = Modifier.clickable(onClick = dismiss).padding(vertical = 8.dp))
            }

            if (allProviders.isEmpty()) {
                Column(
                    Modifier.fillMaxWidth().weight(1f).padding(32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    Icon(Icons.sparkle, null, tint = YY.c.t3, modifier = Modifier.size(32.dp))
                    Spacer(Modifier.height(10.dp))
                    Text(t("还没添加模型服务商"), fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1)
                    Spacer(Modifier.height(4.dp))
                    Text(t("添加 OpenAI、Claude、DeepSeek、Kimi、通义或本机 Ollama 后即可在这里一键切换模型。"), fontSize = 13.sp, color = YY.c.t3, textAlign = TextAlign.Center)
                    Spacer(Modifier.height(14.dp))
                    YYButton(t("去设置 · 书伴"), ButtonKind.Main, ButtonSize.Small, onClick = openSettings)
                }
                return@ModalBottomSheet
            }

            Row(
                Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 6.dp)
                    .height(38.dp)
                    .background(YY.c.card, RoundedCornerShape(12.dp))
                    .border(0.5.dp, YY.c.line, RoundedCornerShape(12.dp))
                    .padding(horizontal = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Icon(Icons.search, null, tint = YY.c.t3, modifier = Modifier.size(15.dp))
                BasicTextField(
                    query,
                    onValueChange = { query = it },
                    singleLine = true,
                    textStyle = TextStyle(fontSize = 14.sp, color = YY.c.t1),
                    cursorBrush = SolidColor(YY.tint.accent),
                    modifier = Modifier.weight(1f),
                    decorationBox = { inner ->
                        if (query.isEmpty()) Text(t("搜模型名、ID 或缩写（如 co46 / dsr1）…"), fontSize = 14.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        inner()
                    },
                )
                if (query.isNotEmpty()) {
                    Icon(Icons.close, t("清除"), tint = YY.c.t3, modifier = Modifier.size(15.dp).clickable { query = "" })
                }
            }

            val trimmed = query.trim()
            if (trimmed.isEmpty()) {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 6.dp),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    ModelNavChip(
                        selected = navKind == ModelPickerNav.Starred,
                        title = t("收藏"),
                        count = starred.size.takeIf { it > 0 }?.toString(),
                        icon = { Icon(Icons.starFill, null, tint = Color(0xFFD99126), modifier = Modifier.size(13.dp)) },
                    ) { navKind = ModelPickerNav.Starred }

                    ModelNavChip(
                        selected = navKind == ModelPickerNav.Recent,
                        title = t("最近"),
                        count = recents.size.takeIf { it > 0 }?.toString(),
                        icon = { Icon(Icons.clock, null, tint = YY.c.t2, modifier = Modifier.size(13.dp)) },
                    ) { navKind = ModelPickerNav.Recent }

                    for (p in allProviders) {
                        val cfg = CompanionConfig.saved(p)
                        val cnt = if (cfg.isConnected) CompanionModelCatalog.pool(p, livePools[p.id]).size.toString() else t("未配")
                        ModelNavChip(
                            selected = navKind == ModelPickerNav.Provider && selectedProviderId == p.id,
                            title = p.title,
                            count = cnt,
                            dimmed = !cfg.isConnected,
                            icon = { ProviderLogo(p, 16.dp) },
                        ) {
                            navKind = ModelPickerNav.Provider
                            selectedProviderId = p.id
                        }
                    }
                }
            }

            Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))

            val activeProvider = if (trimmed.isEmpty() && navKind == ModelPickerNav.Provider) allProviders.firstOrNull { it.id == selectedProviderId } else null
            if (activeProvider != null && !CompanionConfig.saved(activeProvider).isConnected) {
                Column(
                    Modifier.fillMaxWidth().weight(1f).padding(32.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    ProviderLogo(activeProvider, 32.dp)
                    Spacer(Modifier.height(10.dp))
                    Text(t("还没填好 %@ 的 Key", activeProvider.title), fontSize = 14.sp, color = YY.c.t2)
                    Spacer(Modifier.height(12.dp))
                    YYButton(t("去设置 · 书伴"), ButtonKind.Main, ButtonSize.Small, onClick = openSettings)
                }
            } else {
                val (entries, families, showProvider) = remember(trimmed, navKind, selectedProviderId, selectedFamily, livePools, starred, recents) {
                    if (trimmed.isNotEmpty()) {
                        val hits = readyProviders.flatMap { p ->
                            CompanionModelCatalog.pool(p, livePools[p.id])
                                .filter { CompanionModelCatalog.matches(trimmed, it, p.title) }
                                .map { ModelEntry(p, it) }
                        }
                        Triple(hits, emptyList<String>(), true)
                    } else when (navKind) {
                        ModelPickerNav.Starred -> Triple(starred, emptyList(), true)
                        ModelPickerNav.Recent -> Triple(recents, emptyList(), true)
                        ModelPickerNav.Provider -> {
                            val p = activeProvider
                            if (p == null) Triple(emptyList(), emptyList(), false)
                            else {
                                val models = CompanionModelCatalog.pool(p, livePools[p.id])
                                val fams = models.map { CompanionModelCatalog.family(it) }.distinct()
                                val filtered = models.filter { selectedFamily == null || CompanionModelCatalog.family(it) == selectedFamily }
                                Triple(filtered.map { ModelEntry(p, it) }, if (fams.size > 1) fams else emptyList(), false)
                            }
                        }
                    }
                }

                if (families.isNotEmpty() || activeProvider != null) {
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                    ) {
                        if (families.isNotEmpty()) {
                            Row(Modifier.weight(1f).horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                FamilyChip(t("全部"), selectedFamily == null) { selectedFamily = null }
                                for (f in families) FamilyChip(f, selectedFamily == f) { selectedFamily = f }
                            }
                        } else {
                            Text(activeProvider?.title ?: "", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t2, modifier = Modifier.weight(1f))
                        }
                        if (activeProvider != null) {
                            Box(
                                Modifier.size(28.dp).clip(CircleShape)
                                    .clickable(enabled = loadingProvider != activeProvider.id, onClickLabel = t("刷新模型列表")) { fetchModels(activeProvider, force = true) },
                                contentAlignment = Alignment.Center,
                            ) {
                                if (loadingProvider == activeProvider.id) CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 1.5.dp, color = YY.c.t3)
                                else Icon(Icons.clockwise, t("刷新模型列表"), tint = YY.c.t2, modifier = Modifier.size(15.dp))
                            }
                        }
                    }
                    Box(Modifier.fillMaxWidth().height(0.5.dp).background(YY.c.line))
                }

                val customTarget = activeProvider?.takeIf { CompanionConfig.saved(it).isConnected }
                    ?: current?.provider?.takeIf { CompanionConfig.saved(it).isConnected }
                    ?: readyProviders.firstOrNull()

                LazyColumn(
                    Modifier.fillMaxWidth().weight(1f),
                    contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 14.dp, vertical = 10.dp),
                    verticalArrangement = Arrangement.spacedBy(6.dp),
                ) {
                    items(entries, key = { "${it.provider.id}:${it.model}" }) { entry ->
                        val isActive = current?.provider?.id == entry.provider.id && current.model == entry.model
                        val isStarred = CompanionModelCatalog.isStarred(entry.provider.id, entry.model)
                        val displayName = CompanionModelCatalog.prettyName(entry.model)
                        val route = CompanionModelCatalog.routeBadge(entry.model)
                        val sub = listOfNotNull(
                            entry.provider.title.takeIf { showProvider },
                            entry.model.takeIf { it != displayName },
                        ).joinToString(" · ")
                        Row(
                            Modifier.fillMaxWidth()
                                .clip(RoundedCornerShape(12.dp))
                                .background(if (isActive) YY.tint.accentSoft.copy(alpha = 0.55f) else YY.c.card)
                                .border(0.5.dp, if (isActive) YY.tint.accent.copy(alpha = 0.35f) else YY.c.line, RoundedCornerShape(12.dp))
                                .clickable { pick(entry) }
                                .padding(horizontal = 12.dp, vertical = 10.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.spacedBy(8.dp),
                        ) {
                            if (showProvider) ProviderLogo(entry.provider, 20.dp)
                            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                    Text(displayName, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                                    if (route != null) {
                                        Text(
                                            route,
                                            fontFamily = FontFamily.Monospace,
                                            fontSize = 10.5.sp,
                                            color = YY.c.t2,
                                            modifier = Modifier.background(YY.c.inset, RoundedCornerShape(4.dp)).padding(horizontal = 5.dp, vertical = 1.dp),
                                        )
                                    }
                                }
                                if (sub.isNotEmpty()) Text(sub, fontSize = 11.5.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            }
                            Box(
                                Modifier.size(32.dp).clip(CircleShape).clickable {
                                    CompanionModelCatalog.toggleStar(entry.provider.id, entry.model)
                                    starTick++
                                },
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(
                                    Icons.starFill,
                                    if (isStarred) t("移出常用收藏") else t("加入常用收藏"),
                                    tint = if (isStarred) Color(0xFFD99126) else YY.c.t3.copy(alpha = 0.35f),
                                    modifier = Modifier.size(15.dp),
                                )
                            }
                            if (isActive) {
                                Icon(Icons.check, t("在用"), tint = YY.tint.accent, modifier = Modifier.size(15.dp))
                            }
                        }
                    }

                    if (customTarget != null && trimmed.isNotEmpty() && entries.none { it.provider.id == customTarget.id && it.model == trimmed }) {
                        item(key = "custom:$trimmed") {
                            Row(
                                Modifier.fillMaxWidth()
                                    .clip(RoundedCornerShape(12.dp))
                                    .background(YY.c.card)
                                    .border(0.8.dp, YY.c.line, RoundedCornerShape(12.dp))
                                    .clickable { pick(ModelEntry(customTarget, trimmed)) }
                                    .padding(horizontal = 12.dp, vertical = 12.dp),
                                verticalAlignment = Alignment.CenterVertically,
                                horizontalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                ProviderLogo(customTarget, 18.dp)
                                Text(t("在 %@ 使用 ID：%@", customTarget.title, trimmed), fontSize = 13.sp, color = YY.tint.accent, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                            }
                        }
                    }

                    if (entries.isEmpty() && trimmed.isEmpty()) {
                        item(key = "empty") {
                            Column(
                                Modifier.fillMaxWidth().padding(vertical = 56.dp),
                                horizontalAlignment = Alignment.CenterHorizontally,
                                verticalArrangement = Arrangement.spacedBy(8.dp),
                            ) {
                                Icon(if (navKind == ModelPickerNav.Starred) Icons.starFill else Icons.clock, null, tint = YY.c.t3.copy(alpha = 0.45f), modifier = Modifier.size(24.dp))
                                Text(
                                    if (navKind == ModelPickerNav.Starred) t("点模型右侧的 ★ 把常用模型收进这里") else t("切换过的模型会出现在这里"),
                                    fontSize = 13.sp,
                                    color = YY.c.t3,
                                    textAlign = TextAlign.Center,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ModelNavChip(
    selected: Boolean,
    title: String,
    count: String?,
    dimmed: Boolean = false,
    icon: @Composable () -> Unit,
    onClick: () -> Unit,
) {
    Row(
        Modifier.height(32.dp)
            .alpha(if (dimmed) 0.55f else 1f)
            .background(if (selected) YY.c.card else YY.c.inset, CircleShape)
            .border(if (selected) 1.dp else 0.5.dp, if (selected) YY.tint.accent.copy(alpha = 0.45f) else YY.c.line, CircleShape)
            .clip(CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(5.dp),
    ) {
        icon()
        Text(title, fontSize = 12.5.sp, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Medium, color = if (selected) YY.c.t1 else YY.c.t2, maxLines = 1)
        if (count != null) Text(count, fontSize = 10.5.sp, color = YY.c.t3, style = YY.tnum)
    }
}

@Composable
private fun FamilyChip(label: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        Modifier.height(26.dp)
            .background(if (selected) YY.tint.accentSoft else YY.c.inset, CircleShape)
            .clip(CircleShape)
            .clickable(onClick = onClick)
            .padding(horizontal = 10.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, fontSize = 12.sp, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal, color = if (selected) YY.tint.accent else YY.c.t2)
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
