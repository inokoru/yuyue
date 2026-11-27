package app.yuyue.shelf

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.pressScale
import app.yuyue.design.pressable
import app.yuyue.t
import uniffi.yuyue_core.BookCollection

/** Where 书架 is: 书 · 进度 · 播客 · 有声 · 笔记, or one of your 合集. The title names it; a tap on the title
 *  (or, scrolled, on the chip at the top left) opens the panel to go to another. */
sealed interface ShelfPlace {
    data object Books : ShelfPlace
    data object Progress : ShelfPlace
    data object Podcasts : ShelfPlace
    data object Audio : ShelfPlace
    data object Notes : ShelfPlace
    data class Collection(val id: String) : ShelfPlace

    val key: String
        get() = when (this) {
            Books -> "books"
            Progress -> "progress"
            Podcasts -> "podcasts"
            Audio -> "audio"
            Notes -> "notes"
            is Collection -> "collection:$id"
        }

    companion object {
        const val PREF_KEY = "shelf.lastPlace"

        /** The shelf's own, in the panel's order. */
        val kinds = listOf(Books, Progress, Podcasts, Audio)

        fun parse(raw: String?): ShelfPlace = when {
            raw.isNullOrBlank() || raw == "books" -> Books
            raw == "progress" -> Progress
            raw == "podcasts" -> Podcasts
            raw == "audio" -> Audio
            raw == "notes" -> Notes
            raw.startsWith("collection:") && raw.length > "collection:".length -> Collection(raw.removePrefix("collection:"))
            else -> Books
        }

        fun resolveValid(
            place: ShelfPlace,
            foldedPlaces: List<ShelfPlace>,
            collectionIds: Set<String>,
        ): ShelfPlace = when (place) {
            is Collection -> if (place.id in collectionIds) place else Books
            else -> if (place in foldedPlaces) place else Books
        }
    }
}

/** What the title, the chip and the panel need to know of one place. */
data class PlaceInfo(
    val place: ShelfPlace,
    val name: String,
    val icon: ImageVector,
    /** The panel's row: 38 本 / 在读 3. */
    val short: String,
    /** Two lines at the panel's head. */
    val meta: List<String>,
    val collection: BookCollection? = null,
    val fresh: Boolean = false,
)

/** The marks' line drawings: thin, as the title's. */
object PlaceIcons {
    val books = Icons.book
    val progress = Icons.bookmarkOutline
    val podcasts = Icons.radio
    val audio = Icons.headphones
    val notes = app.yuyue.design.TabIcons.notes
    val switch = Icons.chevronUpDown

    fun filled(icon: ImageVector): ImageVector = when (icon) {
        Icons.book -> Icons.bookFill
        Icons.bookmarkOutline -> Icons.bookmark
        Icons.radio -> Icons.radioFill
        Icons.microphone -> Icons.microphoneFill
        app.yuyue.design.TabIcons.notes -> app.yuyue.design.TabIcons.notesOn
        Icons.checkCircle -> Icons.checkCircleFill
        else -> icon
    }
}

/** Between the root and the shelf: 书架 tapped again while you're on it, and where the shelf is,
 *  for its tab to wear. */
@Stable
class ShelfChrome(initialPlace: ShelfPlace = ShelfPlace.Books) {
    var switching by mutableStateOf(false)
    var place by mutableStateOf<ShelfPlace>(initialPlace)
    var name by mutableStateOf<String?>(null)
    var icon by mutableStateOf<ImageVector?>(null)
    /** Horizontal alignment of the pop-up PlaceStack above the 书架 tab wherever the user placed it. */
    var stackAlignment by mutableStateOf(Alignment.BottomStart)
    /** Whether 书架 currently has any folded sub-places or collections to switch between. */
    var hasSwitcher by mutableStateOf(true)
}

private val paper @Composable get() = if (YY.dark) Color(0xFF2B2925) else Color(0xFFF3F0E8)
private val ink @Composable get() = if (YY.dark) Color(0xFFE9E5DC) else Color(0xFF2B2B2B)

/** A place's mark: a square of warm paper with one thin line drawing on a horizon — round in the
 *  chip, smaller in the panel's rows; `bare`, the title's own: the drawing alone on one line. */
@Composable
fun PlaceMark(icon: ImageVector, size: Dp = 56.dp, round: Boolean = false, bare: Boolean = false) {
    if (bare) {
        Column(Modifier.size(size), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(size * 0.06f, Alignment.CenterVertically)) {
            Icon(icon, null, tint = ink, modifier = Modifier.size(size * 0.62f))
            Box(Modifier.width(size * 0.78f).height(1.dp).background(ink, CircleShape))
        }
        return
    }
    val shape = if (round) CircleShape else RoundedCornerShape(size * 0.25f)
    Box(Modifier.size(size).clip(shape).background(paper).border(0.5.dp, ink.copy(alpha = 0.07f), shape), contentAlignment = Alignment.Center) {
        Column(Modifier.padding(top = size * 0.06f), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(size * 0.05f)) {
            Icon(icon, null, tint = ink, modifier = Modifier.size(size * 0.42f))
            Box(Modifier.width(size * 0.6f).height(maxOf(0.7.dp, size * 0.015f)).background(ink, CircleShape))
            Box(Modifier.width(size * 0.38f).height(maxOf(0.7.dp, size * 0.015f)).alpha(0.75f).background(ink, CircleShape))
        }
    }
}

/** The buttons at the top right of a tab that draws its own title: icons in one white capsule. */
@Composable
fun TopButtons(content: @Composable () -> Unit) {
    val eink = if (app.yuyue.design.Prefs.isReady) app.yuyue.design.rememberPref("eink.on", false).value else false
    Row(
        Modifier.height(40.dp).shadow(if (eink) 0.dp else 3.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.05f), spotColor = Color.Black.copy(alpha = 0.08f))
            .clip(CircleShape).background(YY.c.card).border(0.5.dp, YY.c.line, CircleShape).padding(horizontal = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(2.dp),
    ) { content() }
}

/** One of `TopButtons`: a 40 × 40 place to press. */
@Composable
fun TopButton(icon: ImageVector, label: String, enabled: Boolean = true, action: () -> Unit) {
    Box(
        Modifier.size(40.dp).clip(CircleShape).pressable(label = label, enabled = enabled, scale = 0.9f, onClick = action).alpha(if (enabled) 1f else 0.35f),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, label, tint = YY.c.t1, modifier = Modifier.size(21.dp)) }
}

/** A tab's own title, as the shelf has it: the mark and the name in 宋体 (笔记, 我的). */
@Composable
fun PageTitle(icon: ImageVector, title: String, modifier: Modifier = Modifier) {
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(11.dp)) {
        PlaceMark(icon, 40.dp, bare = true)
        Text(title, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 30.sp, letterSpacing = 1.sp, color = YY.c.t1, maxLines = 1)
    }
}

/** The shelf's title: the place's mark, and beside it its name in 宋体 with ⌄ (when switchable). */
@Composable
fun PlaceTitle(info: PlaceInfo, modifier: Modifier = Modifier, interactive: Boolean = true, open: () -> Unit) {
    val press = remember { MutableInteractionSource() }
    val tapMod = if (interactive) {
        Modifier.pressScale(press, 0.98f).clickable(press, null, onClickLabel = t("%@，换一处看", info.name), onClick = open)
    } else {
        Modifier
    }
    Row(
        modifier.height(40.dp).then(tapMod),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(11.dp),
    ) {
        PlaceMark(info.icon, 40.dp, bare = true)
        val titleSize = when {
            info.name.length >= 8 -> 20.sp
            info.name.length >= 6 -> 23.sp
            info.name.length >= 5 -> 26.sp
            else -> 30.sp
        }
        Text(info.name, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = titleSize, letterSpacing = if (info.name.any { it.code < 128 } || info.name.length > 4) 0.sp else 1.sp, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (interactive) {
            Icon(Icons.chevronDown, null, tint = YY.c.t3, modifier = Modifier.padding(top = 3.dp).size(20.dp))
        }
    }
}

/** The panel that grows out of the title (or the chip): the place's mark and name where the title
 *  was, then a row for each place and your 合集, and 新建合集 at the end. */
@Composable
fun PlacePanel(
    current: PlaceInfo, kinds: List<PlaceInfo>, collections: List<PlaceInfo>,
    go: (ShelfPlace) -> Unit, newCollection: () -> Unit, rename: (BookCollection) -> Unit, icon: (BookCollection) -> Unit, delete: (BookCollection) -> Unit,
) {
    val eink = if (app.yuyue.design.Prefs.isReady) app.yuyue.design.rememberPref("eink.on", false).value else false
    val shape = RoundedCornerShape(28.dp)
    Column(
        Modifier.widthIn(max = 520.dp).fillMaxWidth()
            .shadow(if (eink) 0.dp else 24.dp, shape, ambientColor = Color.Black.copy(alpha = 0.16f), spotColor = Color.Black.copy(alpha = 0.2f))
            .clip(shape).background(YY.c.card).border(0.5.dp, YY.c.line, shape)
            .padding(start = 10.dp, end = 10.dp, top = 15.dp, bottom = 10.dp),
    ) {
        Row(Modifier.padding(horizontal = 6.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(13.dp)) {
            PlaceMark(current.icon)
            val panelTitleSize = when {
                current.name.length >= 8 -> 21.sp
                current.name.length >= 6 -> 24.sp
                current.name.length >= 5 -> 27.sp
                else -> 31.sp
            }
            Text(current.name, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = panelTitleSize, letterSpacing = if (current.name.any { it.code < 128 } || current.name.length > 4) 0.sp else 1.sp, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            Column(Modifier.padding(end = 4.dp), horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(2.dp)) {
                for (m in current.meta) Text(m, fontSize = 13.5.sp, color = YY.c.t3, style = YY.tnum)
            }
        }
        Box(Modifier.padding(top = 14.dp, bottom = 8.dp).fillMaxWidth().height(0.5.dp).background(YY.c.line))
        Column(Modifier.heightIn(max = 470.dp).verticalScroll(rememberScrollState())) {
            for (k in kinds) PlaceRow(k, k.place == current.place, 36.dp, 52.dp, check = true) { go(k.place) }
            Text(t("合集"), fontSize = 12.sp, color = YY.c.t3, modifier = Modifier.padding(start = 8.dp, top = 12.dp, bottom = 4.dp))
            for (c in collections) {
                val col = c.collection
                if (col != null) {
                    var menu by remember { mutableStateOf(false) }
                    val manageLabel = t("管理合集“%@”", col.name)
                    PlaceRow(
                        info = c,
                        on = c.place == current.place,
                        mark = 36.dp,
                        height = 52.dp,
                        check = true,
                        onLong = { menu = true },
                        trailing = {
                            Box {
                                Box(
                                    Modifier.size(36.dp).clip(CircleShape).clickable(onClickLabel = manageLabel) { menu = true },
                                    contentAlignment = Alignment.Center,
                                ) {
                                    Icon(Icons.more, manageLabel, tint = YY.c.t3, modifier = Modifier.size(18.dp))
                                }
                                DropdownMenu(menu, { menu = false }, containerColor = YY.c.card) {
                                    PanelItem(t("重命名"), Icons.edit) { menu = false; rename(col) }
                                    PanelItem(t("换图标"), c.icon) { menu = false; icon(col) }
                                    PanelItem(t("删除"), Icons.delete, danger = true) { menu = false; delete(col) }
                                }
                            }
                        },
                    ) { go(c.place) }
                } else {
                    PlaceRow(c, c.place == current.place, 36.dp, 52.dp, check = true) { go(c.place) }
                }
            }
            val press = remember { MutableInteractionSource() }
            val pressed by press.collectIsPressedAsState()
            Row(
                Modifier.fillMaxWidth().height(52.dp).clip(RoundedCornerShape(14.dp)).background(if (pressed) YY.c.inset else Color.Transparent)
                    .clickable(press, null, onClick = newCollection).padding(start = 6.dp),
                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                Box(Modifier.size(36.dp).border(1.dp, YY.c.line, RoundedCornerShape(10.dp)), contentAlignment = Alignment.Center) {
                    Icon(Icons.add, null, tint = YY.c.t3, modifier = Modifier.size(18.dp))
                }
                Text(t("新建合集"), fontSize = 16.sp, color = YY.c.t3)
            }
        }
    }
}

/** A row in the panel or the stack: a light grey ground on the place you're in, and while pressed. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun PlaceRow(
    info: PlaceInfo,
    on: Boolean,
    mark: Dp,
    height: Dp,
    check: Boolean,
    onLong: (() -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
    go: () -> Unit,
) {
    val press = remember { MutableInteractionSource() }
    val pressed by press.collectIsPressedAsState()
    val ground = when {
        pressed -> YY.c.inset
        on -> YY.c.inset.copy(alpha = 0.75f)
        else -> Color.Transparent
    }
    Row(
        Modifier.fillMaxWidth().heightIn(min = height).clip(RoundedCornerShape(14.dp)).background(ground)
            .combinedClickable(press, null, onLongClick = onLong, onClick = go)
            .padding(start = 6.dp, end = if (trailing != null) 4.dp else if (check) 12.dp else 10.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        PlaceMark(info.icon, mark)
        Text(info.name, fontSize = 16.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        if (info.fresh) Box(Modifier.size(5.dp).clip(CircleShape).background(YY.tint.accent))
        Text(info.short, fontSize = 13.sp, color = YY.c.t3, style = YY.tnum)
        if (check) Icon(Icons.check, null, tint = YY.c.t1, modifier = Modifier.size(16.dp).alpha(if (on) 1f else 0f))
        trailing?.invoke()
    }
}

/** 书架 tapped again: the places stand up in a column over the tab, nearest it the one you're on,
 *  and your 合集 above them. A tap goes there; anywhere else, or 书架 again, puts it away. */
@Composable
fun PlaceStack(current: ShelfPlace, kinds: List<PlaceInfo>, collections: List<PlaceInfo>, go: (ShelfPlace) -> Unit) {
    val eink = if (app.yuyue.design.Prefs.isReady) app.yuyue.design.rememberPref("eink.on", false).value else false
    val shape = RoundedCornerShape(24.dp)
    Column(
        Modifier.widthIn(min = 220.dp, max = 264.dp).shadow(if (eink) 0.dp else 20.dp, shape, ambientColor = Color.Black.copy(alpha = 0.16f), spotColor = Color.Black.copy(alpha = 0.2f))
            .clip(shape).background(YY.c.card).border(0.5.dp, YY.c.line, shape).padding(6.dp),
    ) {
        if (collections.isNotEmpty()) {
            Column(Modifier.heightIn(max = 52.dp * 3).verticalScroll(rememberScrollState())) {
                for (c in collections) PlaceRow(c, c.place == current, 34.dp, 50.dp, check = false) { go(c.place) }
            }
            Box(Modifier.padding(horizontal = 10.dp, vertical = 4.dp).fillMaxWidth().height(0.5.dp).background(YY.c.line))
        }
        for (k in kinds.reversed()) PlaceRow(k, k.place == current, 34.dp, 50.dp, check = false) { go(k.place) }
    }
}

/** Scrolled down, the title's place in a white capsule at the top left: the mark, the name and a
 *  line under it, and ⌃⌄ in a small grey circle. A tap opens the panel from here. */
@Composable
fun PlaceChip(info: PlaceInfo, open: () -> Unit) {
    val eink = if (app.yuyue.design.Prefs.isReady) app.yuyue.design.rememberPref("eink.on", false).value else false
    val press = remember { MutableInteractionSource() }
    Row(
        Modifier.pressScale(press, 0.97f).heightIn(min = 45.dp)
            .shadow(if (eink) 0.dp else 9.dp, CircleShape, ambientColor = Color.Black.copy(alpha = 0.1f), spotColor = Color.Black.copy(alpha = 0.12f))
            .clip(CircleShape).background(YY.c.card).border(0.5.dp, YY.c.line, CircleShape)
            .clickable(press, null, onClickLabel = t("%@，换一处看", info.name), onClick = open)
            .padding(start = 6.dp, end = 11.dp, top = 5.dp, bottom = 5.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        PlaceMark(info.icon, 33.dp, round = true)
        Column(verticalArrangement = Arrangement.spacedBy(1.dp)) {
            Text(info.name, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 16.sp, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(info.short, fontSize = 11.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1)
        }
        Box(Modifier.padding(start = 2.dp).size(20.dp).clip(CircleShape).background(YY.c.inset), contentAlignment = Alignment.Center) {
            Icon(PlaceIcons.switch, null, tint = YY.c.t2, modifier = Modifier.size(14.dp))
        }
    }
}

@Composable
private fun PanelItem(title: String, icon: ImageVector, danger: Boolean = false, action: () -> Unit) {
    androidx.compose.material3.DropdownMenuItem(
        text = { Text(title, color = if (danger) YY.c.danger else YY.c.t1) },
        leadingIcon = { Icon(icon, null, tint = if (danger) YY.c.danger else YY.c.t2) },
        onClick = action,
    )
}
