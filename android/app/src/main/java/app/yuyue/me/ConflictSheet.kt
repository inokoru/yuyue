package app.yuyue.me

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
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
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import app.yuyue.design.ButtonKind
import app.yuyue.design.ButtonSize
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYButton
import app.yuyue.design.YYNote
import app.yuyue.design.YYSheetDragHandle
import app.yuyue.design.YYTag
import app.yuyue.design.yyCard
import app.yuyue.shelf.BookCover
import app.yuyue.sync.Sync
import app.yuyue.t
import uniffi.yuyue_core.ConflictChoice
import uniffi.yuyue_core.ConflictSide
import uniffi.yuyue_core.NoteConflict
import java.util.Calendar

data class DiffSpan(val prefix: String, val middle: String, val suffix: String)

/** Splits two strings into shared prefix, differing middle, and shared suffix (matching Apple's ConflictSheet). */
object ConflictDiff {
    fun compute(a: String, b: String): Pair<DiffSpan, DiffSpan> {
        val minLen = minOf(a.length, b.length)
        var start = 0
        while (start < minLen && a[start] == b[start]) start++
        if (start > 0 && start < minLen && a[start].isLowSurrogate()) start--
        var end = 0
        while (end < minLen - start && a[a.length - 1 - end] == b[b.length - 1 - end]) end++
        if (end > 0 && end < minLen - start && a[a.length - end].isLowSurrogate()) end--
        fun split(s: String) = DiffSpan(
            prefix = s.substring(0, start),
            middle = s.substring(start, s.length - end),
            suffix = s.substring(s.length - end),
        )
        return split(a) to split(b)
    }
}

/** Banner shown in SyncPage whenever concurrent edits on two devices produced note conflicts. */
@Composable
fun ConflictsBanner(sync: Sync, modifier: Modifier = Modifier) {
    var shown by remember { mutableStateOf(false) }
    val count = sync.conflicts.size
    if (count > 0) {
        Row(
            modifier
                .padding(top = YY.s(10))
                .fillMaxWidth()
                .yyCard(YY.s(14))
                .clickable { shown = true }
                .padding(YY.s(12)),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(YY.s(10)),
        ) {
            Box(
                Modifier.size(YY.s(28)).background(YY.c.warn.copy(alpha = 0.13f), CircleShape),
                contentAlignment = Alignment.Center,
            ) {
                Icon(Icons.copy, null, tint = YY.c.warn, modifier = Modifier.size(YY.s(13)))
            }
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                Text(t("%lld 处笔记两边不一样", count), style = YY.font(11.5f, FontWeight.SemiBold), color = YY.c.t1)
                Text(t("两份都留着，等你来选"), style = YY.font(10f), color = YY.c.t3)
            }
            Box(
                Modifier.clip(CircleShape).background(YY.c.inset).padding(horizontal = YY.s(10), vertical = YY.s(5)),
                contentAlignment = Alignment.Center,
            ) {
                Text(t("去选"), style = YY.font(10.5f, FontWeight.SemiBold), color = YY.c.t1)
            }
        }
    }
    if (shown) {
        NoteConflictsSheet(sync) {
            shown = false
            sync.loadConflicts()
        }
    }
}

/** 同步冲突: both versions of a conflicted highlight note laid side by side until the user chooses. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun NoteConflictsSheet(sync: Sync, dismiss: () -> Unit) {
    var index by remember { mutableIntStateOf(0) }
    var problem by remember { mutableStateOf<String?>(null) }
    val conflicts = sync.conflicts
    LaunchedEffect(conflicts.size) {
        if (conflicts.isEmpty()) dismiss()
        else if (index >= conflicts.size) index = conflicts.lastIndex
    }
    val current = conflicts.getOrNull(index) ?: return

    fun choose(choice: ConflictChoice) {
        problem = sync.resolveConflict(current.book, current.mark, choice)
    }

    ModalBottomSheet(
        dismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = YY.c.bg,
        dragHandle = { YYSheetDragHandle() },
    ) {
        Column(
            Modifier
                .verticalScroll(rememberScrollState())
                .padding(horizontal = YY.s(16))
                .padding(bottom = YY.s(28)),
        ) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(t("同步冲突"), style = YY.font(14f, FontWeight.Bold), color = YY.c.t1)
                    if (conflicts.size > 1) {
                        Text(t("第 %lld 处，共 %lld 处", index + 1, conflicts.size), style = YY.font(10f, tnum = true), color = YY.c.t3)
                    }
                }
                Box(
                    Modifier.size(YY.s(30)).clip(CircleShape).background(YY.c.card).clickable(onClick = dismiss),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(Icons.close, t("关上"), tint = YY.c.t1, modifier = Modifier.size(YY.s(13)))
                }
            }

            Spacer(Modifier.height(YY.s(14)))
            Text(t("两台设备上的这条笔记不一样"), style = YY.font(16f, FontWeight.Bold), color = YY.c.t1)
            Row(
                Modifier.padding(top = YY.s(6)),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(YY.s(6)),
            ) {
                sync.bookSummary(current.book)?.let { book ->
                    BookCover(book, Modifier.size(YY.s(12), YY.s(17)))
                }
                Text(
                    t("%@ · 划线的想法", current.bookTitle),
                    style = YY.font(10f),
                    color = YY.c.t3,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
            }

            Text(
                buildAnnotatedString {
                    withStyle(SpanStyle(background = YY.c.mkY)) { append(current.text) }
                },
                style = YY.font(13.5f, family = YY.serif).copy(lineHeight = YY.sp(22f)),
                color = YY.c.t1,
                modifier = Modifier.padding(top = YY.s(14)),
            )

            val (olderSpan, newerSpan) = remember(current.older.note, current.newer.note) {
                ConflictDiff.compute(current.older.note ?: "", current.newer.note ?: "")
            }
            val diffTint = YY.tint.accentSoft
            val olderText = remember(olderSpan, diffTint) { formatDiff(olderSpan, diffTint) }
            val newerText = remember(newerSpan, diffTint) { formatDiff(newerSpan, diffTint) }

            val firstIsNewer = current.newer.thisDevice
            Column(Modifier.padding(top = YY.s(16)), verticalArrangement = Arrangement.spacedBy(YY.s(10))) {
                ConflictSideCard(
                    side = if (firstIsNewer) current.newer else current.older,
                    text = if (firstIsNewer) newerText else olderText,
                    newer = firstIsNewer,
                    onKeep = { choose(if (firstIsNewer) ConflictChoice.NEWER else ConflictChoice.OLDER) },
                )
                ConflictSideCard(
                    side = if (firstIsNewer) current.older else current.newer,
                    text = if (firstIsNewer) olderText else newerText,
                    newer = !firstIsNewer,
                    onKeep = { choose(if (firstIsNewer) ConflictChoice.OLDER else ConflictChoice.NEWER) },
                )
            }

            YYNote(t("标了底色的是两份不一样的字。"))
            problem?.let { YYNote(it, YY.c.danger) }

            Spacer(Modifier.height(YY.s(16)))
            Text(
                t("你选之前，两份都留着，不会偷偷覆盖"),
                style = YY.font(10f),
                color = YY.c.t3,
                modifier = Modifier.align(Alignment.CenterHorizontally),
            )
            Spacer(Modifier.height(YY.s(10)))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(YY.s(10))) {
                YYButton(t("两份都留"), ButtonKind.Raised, ButtonSize.Large, wide = true, modifier = Modifier.weight(1f)) {
                    choose(ConflictChoice.BOTH)
                }
                YYButton(t("用较新的"), ButtonKind.Main, ButtonSize.Large, wide = true, modifier = Modifier.weight(1f)) {
                    choose(ConflictChoice.NEWER)
                }
            }
        }
    }
}

@Composable
private fun ConflictSideCard(side: ConflictSide, text: AnnotatedString, newer: Boolean, onKeep: () -> Unit) {
    val tint = YY.tint
    Column(
        Modifier.fillMaxWidth().yyCard(YY.s(14)).padding(YY.s(13)),
        verticalArrangement = Arrangement.spacedBy(YY.s(10)),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(8))) {
            Icon(platformIcon(side.platform), null, tint = YY.c.t2, modifier = Modifier.size(YY.s(13)))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(YY.s(1))) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(YY.s(5))) {
                    Text(
                        if (side.thisDevice) t("这台手机") else side.deviceName.ifEmpty { t("设备") },
                        style = YY.font(11.5f, FontWeight.SemiBold),
                        color = YY.c.t1,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    if (newer) YYTag(t("较新"))
                }
                Text(t("%@ 改的", conflictWhen(side.at)), style = YY.font(9.5f, tnum = true), color = YY.c.t3)
            }
            YYButton(t("留这份"), ButtonKind.Plain, ButtonSize.Small, onClick = onKeep)
        }
        Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min)) {
            Box(Modifier.width(1.5.dp).fillMaxHeight().background(tint.accent, RoundedCornerShape(1.dp)))
            Spacer(Modifier.width(YY.s(10)))
            if (side.note == null) {
                Text(t("（没写想法）"), style = YY.font(12f, family = YY.serif), color = YY.c.t3)
            } else {
                Text(
                    text,
                    style = YY.font(12f, family = YY.serif).copy(lineHeight = YY.sp(20f)),
                    color = tint.accent,
                )
            }
        }
    }
}

private fun formatDiff(span: DiffSpan, highlight: Color): AnnotatedString = buildAnnotatedString {
    append(span.prefix)
    if (span.middle.isNotEmpty()) {
        withStyle(SpanStyle(background = highlight)) { append(span.middle) }
    }
    append(span.suffix)
}

private fun platformIcon(platform: String) = when (platform.lowercase()) {
    "mac", "macos", "windows" -> Icons.laptop
    "ios", "iphone", "ipad", "ipados" -> Icons.iphone
    else -> Icons.phone
}

private fun conflictWhen(ms: Long): String {
    val cal = Calendar.getInstance().apply { timeInMillis = ms }
    val now = Calendar.getInstance()
    val time = "%d:%02d".format(cal.get(Calendar.HOUR_OF_DAY), cal.get(Calendar.MINUTE))
    val sameYear = cal.get(Calendar.YEAR) == now.get(Calendar.YEAR)
    val dayDiff = now.get(Calendar.DAY_OF_YEAR) - cal.get(Calendar.DAY_OF_YEAR)
    if (sameYear && dayDiff == 0) return t("今天 %@", time)
    if (sameYear && dayDiff == 1) return t("昨天 %@", time)
    return t("%lld 月 %lld 日 %@", cal.get(Calendar.MONTH) + 1, cal.get(Calendar.DAY_OF_MONTH), time)
}
