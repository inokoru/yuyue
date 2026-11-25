package app.yuyue.shelf

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.pressScale
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.library.percent
import app.yuyue.t
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.BookSummary
import kotlin.math.abs

/** Main bookshelf presentation style, mirrored on Apple (`shelf.viewStyle`). */
enum class ShelfViewStyle(val key: String) {
    GRID("grid"),
    SPINE("spine");

    companion object {
        fun of(raw: String?): ShelfViewStyle = entries.firstOrNull { it.key == raw } ?: GRID
    }
}

object ShelfStylePrefs {
    const val VIEW_KEY = "shelf.viewStyle"
    const val WOOD_KEY = "shelf.woodLedge"
}

/** Deterministic physical spine width & height tiers per book (mirrors Apple's formula). */
internal data class SpineMetrics(val width: Dp, val height: Dp)

internal fun spineMetrics(book: BookSummary, scale: Float): SpineMetrics {
    val k = scale.coerceIn(0.85f, 1.25f)
    var h = 2166136261u
    for (ch in book.id) {
        h = (h xor ch.code.toUInt()) * 16777619u
    }
    val wTier = ((h and 3u).toInt() + if ((book.readMinutes + book.listenMinutes) > 180u) 1 else 0).coerceAtMost(3)
    val hTier = ((h shr 4) and 3u).toInt()
    val baseW = when (wTier) {
        0 -> 34f
        1 -> 40f
        2 -> 46f
        else -> 52f
    }
    val baseH = when (hTier) {
        0 -> 174f
        1 -> 182f
        2 -> 190f
        else -> 196f
    }
    return SpineMetrics((baseW * k).dp, (baseH * k).dp)
}

/** Packs books into bottom-aligned shelf rows that fit inside [maxWidth]. */
internal fun packSpineRows(books: List<BookSummary>, maxWidth: Dp, scale: Float, gap: Dp = 5.dp): List<List<BookSummary>> {
    if (books.isEmpty()) return emptyList()
    val limit = maxWidth.value.coerceAtLeast(160f)
    val rows = mutableListOf<List<BookSummary>>()
    var current = mutableListOf<BookSummary>()
    var used = 0f
    for (b in books) {
        val w = spineMetrics(b, scale).width.value
        val add = if (current.isEmpty()) w else w + gap.value
        if (current.isNotEmpty() && used + add > limit) {
            rows += current
            current = mutableListOf(b)
            used = w
        } else {
            current += b
            used += add
        }
    }
    if (current.isNotEmpty()) rows += current
    return rows
}

/** Light oak (or muted walnut in dark mode) shelf ledge drawn purely with Canvas; hidden in E-Ink. */
@Composable
internal fun ShelfWoodLedge(modifier: Modifier = Modifier) {
    val eink by rememberPref("eink.on", false)
    if (eink) return
    val dark = YY.dark
    Canvas(modifier.fillMaxWidth().height(18.dp)) {
        val boardH = 8.dp.toPx()
        val radius = CornerRadius(3.dp.toPx(), 3.dp.toPx())
        // Soft downward ambient shadow below the ledge.
        drawRect(
            brush = Brush.verticalGradient(
                0f to Color(if (dark) 0x55000000 else 0x264A3720),
                0.55f to Color(if (dark) 0x1E000000 else 0x0E4A3720),
                1f to Color.Transparent,
                startY = boardH - 1.dp.toPx(),
                endY = size.height,
            ),
            topLeft = Offset(4.dp.toPx(), boardH - 1.dp.toPx()),
            size = Size((size.width - 8.dp.toPx()).coerceAtLeast(0f), size.height - boardH + 1.dp.toPx()),
        )
        // Main wooden shelf bar with subtle top bevel and front lip.
        drawRoundRect(
            brush = Brush.verticalGradient(
                0f to Color(if (dark) 0xFF4B3F34 else 0xFFE8D8BE),
                0.32f to Color(if (dark) 0xFF3C3229 else 0xFFD9C3A1),
                0.78f to Color(if (dark) 0xFF2F2720 else 0xFFC8AC84),
                1f to Color(if (dark) 0xFF231D18 else 0xFFB5966C),
                startY = 0f,
                endY = boardH,
            ),
            topLeft = Offset.Zero,
            size = Size(size.width, boardH),
            cornerRadius = radius,
        )
        // Crisp 0.75dp top edge highlight.
        drawLine(
            color = Color.White.copy(alpha = if (dark) 0.08f else 0.42f),
            start = Offset(3.dp.toPx(), 0.75.dp.toPx()),
            end = Offset(size.width - 3.dp.toPx(), 0.75.dp.toPx()),
            strokeWidth = 0.75.dp.toPx(),
        )
    }
}

/** Subtle floor grounding shadow beneath a spine row when wood ledge is off. */
@Composable
internal fun SpineGroundShadow(modifier: Modifier = Modifier) {
    val eink by rememberPref("eink.on", false)
    if (eink) {
        Box(modifier.fillMaxWidth().height(1.dp).background(YY.c.line))
        return
    }
    val dark = YY.dark
    Canvas(modifier.fillMaxWidth().height(8.dp)) {
        drawRect(
            brush = Brush.verticalGradient(
                0f to Color(if (dark) 0x3A000000 else 0x1A2C241B),
                1f to Color.Transparent,
            ),
            topLeft = Offset(4.dp.toPx(), 0f),
            size = Size((size.width - 8.dp.toPx()).coerceAtLeast(0f), size.height),
        )
    }
}

/** One shelf row of vertical book spines, aligned along their bottom edge. */
@OptIn(ExperimentalFoundationApi::class)
@Composable
internal fun SpineShelfRow(
    library: Library,
    books: List<BookSummary>,
    scale: Float,
    showProgress: Boolean,
    woodLedge: Boolean,
    selecting: Boolean,
    selected: Set<String>,
    onTap: (BookSummary) -> Unit,
    onEdit: (BookSummary) -> Unit,
    onPick: (BookSummary) -> Unit,
    menuHost: @Composable (BookSummary, Boolean, () -> Unit) -> Unit,
) {
    val haptics = LocalHapticFeedback.current
    val tint = YY.tint
    val eink by rememberPref("eink.on", false)
    Column(Modifier.fillMaxWidth()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 2.dp),
            horizontalArrangement = Arrangement.spacedBy(5.dp),
            verticalAlignment = Alignment.Bottom,
        ) {
            for (book in books) {
                val m = spineMetrics(book, scale)
                val chosen = book.id in selected
                var menu by remember { mutableStateOf(false) }
                val press = remember { MutableInteractionSource() }
                val shape = RoundedCornerShape(topStart = 5.dp, topEnd = 5.dp, bottomStart = 2.dp, bottomEnd = 2.dp)
                Box {
                    Box(
                        Modifier
                            .pressScale(press, 0.97f)
                            .width(m.width)
                            .height(m.height)
                            .clip(shape)
                            .border(
                                if (chosen) 1.5.dp else 0.5.dp,
                                if (chosen) tint.accent else if (YY.dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.08f),
                                shape,
                            )
                            .alpha(if (book.available) 1f else 0.5f)
                            .combinedClickable(
                                interactionSource = press,
                                indication = androidx.compose.material3.ripple(),
                                onClick = { onTap(book) },
                                onLongClick = if (selecting) null else ({ haptics.performHapticFeedback(HapticFeedbackType.LongPress); menu = true }),
                            ),
                    ) {
                        val note = if (showProgress && book.progress > 0.0) book.percent else null
                        CoverStrip(book, note, m.width, m.height, Modifier.fillMaxSize())
                        // Subtle cylindrical book-spine shading (left highlight + right groove).
                        if (!eink) {
                            Box(
                                Modifier.fillMaxSize().background(
                                    Brush.horizontalGradient(
                                        0f to Color.White.copy(alpha = 0.16f),
                                        0.14f to Color.Transparent,
                                        0.84f to Color.Transparent,
                                        1f to Color.Black.copy(alpha = 0.18f),
                                    ),
                                ),
                            )
                        }
                        if (selecting) {
                            Box(Modifier.align(Alignment.BottomCenter).padding(bottom = 6.dp)) {
                                Icon(
                                    if (chosen) Icons.checkCircleFill else Icons.circle,
                                    if (chosen) t("已选") else t("未选"),
                                    tint = if (chosen) tint.accent else Color.White,
                                    modifier = Modifier.size(18.dp).clip(CircleShape).background(if (chosen) Color.White else Color.Black.copy(alpha = 0.3f)),
                                )
                            }
                        } else if (book.format == BookFormat.AUDIO) {
                            Box(
                                Modifier.align(Alignment.BottomCenter).padding(bottom = 6.dp).size(16.dp).clip(CircleShape).background(Color.Black.copy(alpha = 0.32f)),
                                contentAlignment = Alignment.Center,
                            ) {
                                Icon(Icons.headphones, null, tint = Color.White, modifier = Modifier.size(10.dp))
                            }
                        }
                    }
                    menuHost(book, menu) { menu = false }
                }
            }
        }
        if (woodLedge) {
            ShelfWoodLedge()
        } else {
            SpineGroundShadow()
        }
    }
}
