package app.yuyue.shelf

import android.graphics.Bitmap
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.requiredWidth
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentWidth
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.derivedStateOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.drawWithContent
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.RoundRect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.FilterQuality
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shadow
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.clipPath
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.YY
import app.yuyue.design.YYMotion
import app.yuyue.design.rememberPref
import app.yuyue.library.Library
import app.yuyue.library.author
import app.yuyue.library.percent
import app.yuyue.library.remaining
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.BookSummary

private val CoverW = 136.dp
private val StripW = 42.dp
private val RowH = 204.dp
private val Gap = 6.dp

/**
 * 在读: the books in a row of narrow strips, the one picked opened out to its cover; what's left
 * of the row stands empty, fainter to the right. Under them, as the shelf's title, a thin drawing
 * beside the name in 宋体 over a line — the line running on is how far it's read.
 * One book: its cover, the words beside it. None: the empty row, and where one comes from.
 */
@Composable
fun ReadingShelf(library: Library, books: List<BookSummary>, open: (BookSummary, Library.OpenIntent?) -> Unit) {
    var picked by rememberSaveable { mutableStateOf<String?>(null) }
    val current = books.firstOrNull { it.id == picked } ?: books.firstOrNull()
    fun playing(b: BookSummary) = library.listening == b.id
    fun read(b: BookSummary) = open(b, if (b.format == BookFormat.AUDIO && !playing(b)) Library.OpenIntent.Listen else null)
    fun listen(b: BookSummary) = open(b, if (playing(b)) null else Library.OpenIntent.Listen)

    when {
        books.isEmpty() -> Column(verticalArrangement = Arrangement.spacedBy(20.dp)) {
            StripRow(books, null) {}
            ReadingLine(LineIcons.book, t("还没有在读的书"), t("在下面点一本，它就会立在这里"), 0.0)
        }
        books.size == 1 -> One(books[0], playing(books[0]), read = { read(books[0]) }, listen = { listen(books[0]) })
        current != null -> Column(verticalArrangement = Arrangement.spacedBy(20.dp)) {
            StripRow(books, current.id) { b -> if (b.id == current.id) open(b, null) else picked = b.id }
            AnimatedContent(current, transitionSpec = { fadeIn(YYMotion.standard()) togetherWith fadeOut(YYMotion.exit()) }, contentKey = { it.id }, label = "reading") { b ->
                ReadingLine(
                    if (playing(b)) Icons.waveform else LineIcons.headset, b.title, meta(b), b.progress,
                    percent = b.percent, listen = { listen(b) }, read = { read(b) },
                )
            }
        }
    }
}

/** The strips, then as many empty ones as the row has room for. */
@Composable
private fun StripRow(books: List<BookSummary>, on: String?, tap: (BookSummary) -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val used = if (books.isEmpty()) 0.dp else CoverW + (StripW + Gap) * (books.size - 1)
        val room = ((maxWidth - used + if (books.isEmpty()) Gap else 0.dp) / (StripW + Gap)).toInt().coerceAtLeast(0)
        val onIndex = books.indexOfFirst { it.id == on }
        val opens = books.map { b -> key(b.id) { animateFloatAsState(if (b.id == on) 1f else 0f, YYMotion.standard(), label = "strip") } }
        Row(Modifier.horizontalScroll(rememberScrollState(), enabled = used > maxWidth), horizontalArrangement = Arrangement.spacedBy(Gap)) {
            books.forEachIndexed { idx, b ->
                key(b.id) {
                    Strip(b, b.id == on, idx, onIndex, opens) { tap(b) }
                }
            }
            for (i in 0 until room) {
                val fade = (if (books.isEmpty()) 1f else 0.75f) * (1f - i / (room + 0.6f))
                Box(Modifier.size(StripW, RowH).alpha(fade).clip(RoundedCornerShape(9.dp)).background(if (YY.dark) Color(0xFF2A2622) else Color(0xFFECE6DC)))
            }
        }
    }
}

/** One book in the row: a strip of its cover's colour with the title down it, or opened to the cover. */
@Composable
private fun Strip(book: BookSummary, on: Boolean, index: Int, onIndex: Int, opens: List<State<Float>>, tap: () -> Unit) {
    val open = opens[index]
    val showCover by remember(open) { derivedStateOf { open.value > 0.001f } }
    val borderColor = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.06f)
    Box(
        Modifier.size(if (on) CoverW else StripW, RowH)
            .graphicsLayer {
                clip = false
                val deltaPx = (CoverW - StripW).toPx()
                var before = 0f
                for (j in 0 until index) before += opens[j].value
                val layoutBefore = if (onIndex in 0 until index) 1f else 0f
                translationX = deltaPx * (before - layoutBefore)
            }
            .drawWithContent {
                val deltaPx = (CoverW - StripW).toPx()
                val visualW = StripW.toPx() + deltaPx * open.value
                val radius = CornerRadius(9.dp.toPx())
                val path = Path().apply { addRoundRect(RoundRect(0f, 0f, visualW, size.height, radius)) }
                clipPath(path) { this@drawWithContent.drawContent() }
                drawRoundRect(borderColor, size = Size(visualW, size.height), cornerRadius = radius, style = Stroke(0.5.dp.toPx()))
            }
            .clickable(
                interactionSource = remember { MutableInteractionSource() },
                indication = null,
                onClickLabel = if (on) t("接着读《%@》", book.title) else book.title,
                onClick = tap,
            ),
    ) {
        CoverStrip(
            book, book.percent, StripW, RowH,
            Modifier.wrapContentWidth(Alignment.Start, unbounded = true).width(StripW).fillMaxHeight()
                .graphicsLayer { alpha = 1f - open.value }.align(Alignment.TopStart),
        )
        if (on || showCover) {
            Box(
                Modifier.wrapContentWidth(Alignment.Start, unbounded = true).width(CoverW).fillMaxHeight()
                    .graphicsLayer { alpha = open.value }.align(Alignment.TopStart),
            ) { CoverFill(book) }
        }
    }
}

/** The cover edge to edge; its title on paper when it has none. */
@Composable
internal fun CoverFill(book: BookSummary) {
    var bitmap by remember(book.coverPath) { mutableStateOf(book.coverPath?.let(Thumbnails::cached)) }
    LaunchedEffect(book.coverPath) { if (bitmap == null) bitmap = book.coverPath?.let { Thumbnails.load(it, 600) } }
    val b = bitmap
    when {
        b != null -> Image(b.asImageBitmap(), null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        // A recording without a cover: its square, filling the place.
        book.format == BookFormat.AUDIO -> SquareCover(book, Modifier.fillMaxSize(), radius = 0.dp, shadow = 0.dp)
        else -> BookCover(book, Modifier.fillMaxSize())
    }
}

/** A strip: the cover shrunk to a few pixels and grown back, so it reads as its colours alone;
 *  `note` on top (how far on 在读, the day finished on 读过), the title down it. */
@Composable
internal fun CoverStrip(book: BookSummary, note: String?, width: androidx.compose.ui.unit.Dp, height: androidx.compose.ui.unit.Dp, modifier: Modifier) {
    var field by remember(book.coverPath) { mutableStateOf<Pair<Bitmap, Float>?>(null) }
    LaunchedEffect(book.coverPath) {
        val full = book.coverPath?.let { Thumbnails.cached(it) ?: Thumbnails.load(it, 300) } ?: return@LaunchedEffect
        field = withContext(Dispatchers.Default) {
            // The middle of the cover, the strip's shape, made a few pixels and grown back smooth.
            val w = minOf(full.width, (full.height * width.value / height.value).toInt().coerceAtLeast(1))
            val middle = Bitmap.createBitmap(full, (full.width - w) / 2, 0, w, full.height)
            val small = Bitmap.createScaledBitmap(Bitmap.createScaledBitmap(middle, 2, 5, true), 42, 204, true)
            // Light or dark words by the part the words are written over: the top three fifths.
            val one = Bitmap.createScaledBitmap(Bitmap.createBitmap(middle, 0, 0, middle.width, (middle.height * 0.6f).toInt().coerceAtLeast(1)), 1, 1, true).getPixel(0, 0)
            val lum = (0.2126f * android.graphics.Color.red(one) + 0.7152f * android.graphics.Color.green(one) + 0.0722f * android.graphics.Color.blue(one)) / 255f
            // Its one colour, for 按颜色.
            val hsl = FloatArray(3).also { androidx.core.graphics.ColorUtils.colorToHSL(Bitmap.createScaledBitmap(full, 1, 1, true).getPixel(0, 0), it) }
            app.yuyue.library.CoverColors.set(book.id, hsl)
            small to lum
        }
    }
    val eink by rememberPref("eink.on", false)
    val einkGray by rememberPref("eink.grayscale", true)
    val f = field
    val light = f != null && f.second < 0.62f
    val ink = when {
        f == null -> YY.c.t1
        light -> Color.White.copy(alpha = 0.92f)
        else -> Color(0xD1282018)
    }
    val glow = Shadow(if (light) Color.Black.copy(alpha = 0.25f) else Color.White.copy(alpha = if (f == null) 0f else 0.3f), blurRadius = 8f)
    Box(modifier) {
        if (f != null) {
            Image(
                f.first.asImageBitmap(), null, contentScale = ContentScale.FillBounds, filterQuality = FilterQuality.Low,
                modifier = Modifier.fillMaxSize(),
                colorFilter = if (eink && einkGray) androidx.compose.ui.graphics.ColorFilter.colorMatrix(androidx.compose.ui.graphics.ColorMatrix().apply { setToSaturation(0f) }) else null,
            )
        } else {
            Box(Modifier.fillMaxSize().background(Brush.verticalGradient(if (YY.dark) listOf(Color(0xFF3A352F), Color(0xFF282420)) else listOf(Color(0xFFEBE6DD), Color(0xFFD6CEC1)))))
        }
        Column(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (note != null) Text(note, fontFamily = YY.serif, fontSize = 10.sp, color = ink.copy(alpha = ink.alpha * 0.75f), style = TextStyle(shadow = glow, fontFeatureSettings = "tnum"))
            SpineTitle(book.title, ink, glow, width, height - 52.dp)
        }
    }
}

/** Down the strip: Chinese and Japanese a character under the next; other titles on their side. */
@Composable
private fun SpineTitle(title: String, ink: Color, glow: Shadow, width: androidx.compose.ui.unit.Dp, room: androidx.compose.ui.unit.Dp) {
    val t = title.trim()
    if (t.any { it.code in 0x3000..0x9FFF || it.code in 0xF900..0xFAFF }) {
        val chars = t.filter { it != ' ' }.map { it.toString() }
        val fit = (room.value / 17.5f).toInt()
        val shown = if (chars.size > fit) chars.take(fit - 1) + "︙" else chars
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
            for (c in shown) Text(c, fontFamily = YY.serif, fontSize = 13.5.sp, lineHeight = 13.5.sp, color = ink, style = TextStyle(shadow = glow))
        }
    } else {
        Box(Modifier.size(width, room), contentAlignment = Alignment.Center) {
            Text(
                t, fontFamily = YY.serif, fontSize = 13.sp, letterSpacing = 0.5.sp, color = ink, maxLines = 1, overflow = TextOverflow.Ellipsis,
                style = TextStyle(shadow = glow), modifier = Modifier.requiredWidth(room).rotate(90f),
            )
        }
    }
}

/** One book: the cover, its name and author at the top beside it, how far at the bottom. */
@Composable
private fun One(book: BookSummary, playing: Boolean, read: () -> Unit, listen: () -> Unit) {
    Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
        val shape = RoundedCornerShape(9.dp)
        val borderColor = if (YY.dark) Color.White.copy(alpha = 0.08f) else Color.Black.copy(alpha = 0.06f)
        Box(Modifier.size(CoverW, RowH).clip(shape).border(0.5.dp, borderColor, shape).clickable(onClick = read)) { CoverFill(book) }
        Column(Modifier.weight(1f).height(RowH).clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClick = read).padding(top = 4.dp)) {
            Text(book.title, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 21.sp, lineHeight = 27.sp, letterSpacing = 0.5.sp, color = YY.c.t1, maxLines = 3, overflow = TextOverflow.Ellipsis)
            book.author?.let { Text(it, fontSize = 13.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 5.dp)) }
            Spacer(Modifier.weight(1f))
            Text(meta(book), fontSize = 12.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(bottom = 14.dp))
            Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Mark(if (playing) Icons.waveform else LineIcons.headset, listen)
                Spacer(Modifier.weight(1f))
                Text(book.percent, fontSize = 12.5.sp, color = YY.c.t3, style = YY.tnum)
            }
            Rule(book.progress)
        }
    }
}

/** The mark and the name in one line, the rule under them that is how far, the grey line under it
 *  in line with the name. */
@Composable
private fun ReadingLine(
    icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, meta: String, progress: Double,
    percent: String? = null, listen: (() -> Unit)? = null, read: (() -> Unit)? = null,
) {
    Column(Modifier.fillMaxWidth().then(if (read != null) Modifier.clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClick = read) else Modifier)) {
        Row(Modifier.fillMaxWidth().padding(bottom = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Mark(icon, listen)
            Text(title, fontFamily = YY.serif, fontWeight = FontWeight.Medium, fontSize = 22.sp, letterSpacing = 0.5.sp, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
            if (percent != null) Text(percent, fontSize = 12.5.sp, color = YY.c.t3, style = YY.tnum)
        }
        Rule(progress)
        Text(meta, fontSize = 12.5.sp, color = YY.c.t3, style = YY.tnum, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 9.dp, start = 46.dp))
    }
}

/** The thin drawing at the head of the line; with an action, 听. */
@Composable
private fun Mark(icon: androidx.compose.ui.graphics.vector.ImageVector, action: (() -> Unit)?) {
    Box(
        Modifier.size(34.dp).clip(CircleShape).then(if (action != null) Modifier.clickable(onClickLabel = t("听"), onClick = action) else Modifier),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, null, tint = YY.c.t1, modifier = Modifier.size(if (icon === LineIcons.headset || icon === LineIcons.book) 34.dp else 22.dp)) }
}

/** A hairline the width of the row; the part read drawn over it in ink, never shorter than the mark. */
@Composable
private fun Rule(value: Double) {
    val v = animateFloatAsState(value.toFloat().coerceIn(0f, 1f), YYMotion.standard(), label = "rule")
    val trackColor = if (YY.dark) Color(0xFF3A352F) else Color(0xFFE2DCD2)
    val inkColor = YY.c.t1
    Box(Modifier.fillMaxWidth().height(2.dp).drawBehind {
        val trackH = 1.5.dp.toPx()
        val trackY = (size.height - trackH) / 2f
        drawRoundRect(trackColor, topLeft = Offset(0f, trackY), size = Size(size.width, trackH), cornerRadius = CornerRadius(trackH / 2f))
        val fillW = maxOf(34.dp.toPx(), size.width * v.value).coerceAtMost(size.width)
        drawRoundRect(inkColor, size = Size(fillW, size.height), cornerRadius = CornerRadius(size.height / 2f))
    })
}

private fun meta(book: BookSummary): String =
    if (book.format == BookFormat.PDF && book.page > 0u) listOfNotNull(book.author, t("第 %lld 页", book.page.toInt())).joinToString(" · ")
    else listOfNotNull(book.author, book.remaining).joinToString(" · ")

/** 在读's thin drawings: the headphones (a band, two rounded cups) and
 *  the open book, on a 34 × 34 sheet, 1.5 thick. */
object LineIcons {
    private fun draw(name: String, path: String) = androidx.compose.ui.graphics.vector.ImageVector.Builder(name, 34.dp, 34.dp, 34f, 34f).apply {
        addPath(
            androidx.compose.ui.graphics.vector.PathParser().parsePathString(path).toNodes(),
            stroke = androidx.compose.ui.graphics.SolidColor(Color.Black), strokeLineWidth = 1.5f,
            strokeLineCap = androidx.compose.ui.graphics.StrokeCap.Round, strokeLineJoin = androidx.compose.ui.graphics.StrokeJoin.Round,
        )
    }.build()

    val headset = draw("headset", "M9 22v-6a8 8 0 0 1 16 0v6M6.5 18.5h4v7h-4zM23.5 18.5h4v7h-4z")
    val book = draw("book", "M5 9c4-1.6 8-1.6 12 1 4-2.6 8-2.6 12-1v15c-4-1.4-8-1.4-12 1-4-2.4-8-2.4-12-1zM17 10v16")
}
