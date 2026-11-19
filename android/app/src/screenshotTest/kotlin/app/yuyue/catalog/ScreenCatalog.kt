package app.yuyue.catalog

import android.content.res.Configuration
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.design.YYPapers
import app.yuyue.design.YuyueTheme
import app.yuyue.library.ShelfSort
import app.yuyue.me.AppearancePage
import app.yuyue.me.ConfirmDialog
import app.yuyue.reader.ReaderCapsuleBar
import app.yuyue.reader.ReaderTopBar
import app.yuyue.reader.ThoughtDialog
import app.yuyue.shelf.AllRow
import app.yuyue.shelf.BookFailure
import app.yuyue.shelf.CoverPlate
import app.yuyue.shelf.ImportChoices
import app.yuyue.shelf.PageTitle
import app.yuyue.shelf.PlaceIcons
import app.yuyue.shelf.TopButton
import app.yuyue.shelf.TopButtons
import app.yuyue.shelf.UndoToastPlate
import app.yuyue.shelf.Welcome
import app.yuyue.t
import com.android.tools.screenshot.PreviewTest
import dev.chrisbanes.haze.rememberHazeState

/**
 * 界面快照: in-app screens drawn from fixtures, not a full library. The WebView page, system
 * permission dialogs and the update installer stay in scripts/window-catalog.sh.
 * Record with `./gradlew :app:updateDebugScreenshotTest`.
 */

private data class Book(val title: String, val author: String?, val note: String, val pdf: Boolean = false)

/** The same four books the Mac window catalog seeds, without their files. */
private val books = listOf(
    Book("红楼梦", "曹雪芹", "读到 12%"),
    Book("了不起的盖茨比", "菲茨杰拉德", "想读"),
    Book("背影", "朱自清", "读完"),
    Book("样张", null, "PDF", pdf = true),
)

@Composable
private fun Frame(content: @Composable () -> Unit) {
    val context = LocalContext.current
    remember(context) {
        val host = runCatching { context.applicationContext }.getOrNull() ?: context
        runCatching { Prefs.init(host) }
        true
    }
    YuyueTheme(content)
}

private val phone = Modifier.fillMaxSize()

@PreviewTest
@Preview(name = "书架 · 空", widthDp = 390, heightDp = 844)
@Composable
fun ShelfEmpty() {
    Frame { ShelfEmptyScreen() }
}

@PreviewTest
@Preview(name = "书架 · 空 · 深色", widthDp = 390, heightDp = 844, uiMode = Configuration.UI_MODE_NIGHT_YES)
@Composable
fun ShelfEmptyDark() {
    Frame { ShelfEmptyScreen() }
}

@PreviewTest
@Preview(name = "书架 · 四本", widthDp = 390, heightDp = 844)
@Composable
fun ShelfBooks() {
    Frame { ShelfBooksScreen() }
}

@PreviewTest
@Preview(name = "阅读条", widthDp = 390, heightDp = 844)
@Composable
fun ReaderChrome() {
    Frame { ReaderChromeScreen() }
}

@PreviewTest
@Preview(name = "设置 · 外观", widthDp = 390, heightDp = 844)
@Composable
fun SettingsAppearance() {
    Frame { AppearancePage() }
}

@PreviewTest
@Preview(name = "导入", widthDp = 390, heightDp = 844)
@Composable
fun ImportSheet() {
    Frame { ImportSheetScreen() }
}

@PreviewTest
@Preview(name = "删除这本书", widthDp = 390, heightDp = 844)
@Composable
fun DeleteBook() {
    Frame {
        Box(phone.background(YY.c.bg)) {
            ConfirmDialog(
                t("删除《%@》？", "红楼梦"),
                t("书会移到已删除，30 天内可以在存储里撤销。"),
                t("删除"),
                confirm = {},
                dismiss = {},
            )
        }
    }
}

@PreviewTest
@Preview(name = "这本书打不开", widthDp = 390, heightDp = 844)
@Composable
fun BookWontOpen() {
    Frame { BookFailure(t("这本书读不出来，文件可能坏了，或者不是它看起来的格式")) {} }
}

@PreviewTest
@Preview(name = "写想法", widthDp = 390, heightDp = 844)
@Composable
fun Thought() {
    Frame {
        Box(phone.background(YY.c.bg)) {
            ThoughtDialog(initial = "", quote = "我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。") {}
        }
    }
}

@PreviewTest
@Preview(name = "撤销", widthDp = 390, heightDp = 160)
@Composable
fun Undo() {
    Frame {
        Box(phone.background(YY.c.bg), contentAlignment = Alignment.Center) {
            UndoToastPlate(
                title = t("已删除《%@》", "背影"),
                danger = true,
                error = false,
                button = 1f,
                undoShown = true,
                undo = {},
            )
        }
    }
}

@Composable
private fun ShelfEmptyScreen() {
    Box(phone.background(YY.c.bg)) {
        Column(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            PageTitle(PlaceIcons.books, t("书"), Modifier.padding(top = 14.dp, end = 150.dp))
            Welcome({}, {}, {})
        }
        Box(Modifier.align(Alignment.TopEnd).padding(top = 14.dp, end = 16.dp)) {
            TopButtons {
                TopButton(Icons.feedback, t("反馈")) {}
                TopButton(Icons.add, t("导入")) {}
                TopButton(Icons.checkCircle, t("选择"), enabled = false) {}
            }
        }
    }
}

@Composable
private fun ShelfBooksScreen() {
    Column(phone.background(YY.c.bg).padding(horizontal = 16.dp)) {
        PageTitle(PlaceIcons.books, t("书"), Modifier.padding(top = 14.dp, end = 150.dp))
        AllRow(books.size, ShelfSort.Recent) {}
        Column(Modifier.padding(top = 18.dp), verticalArrangement = Arrangement.spacedBy(22.dp)) {
            books.chunked(2).forEach { row ->
                Row(horizontalArrangement = Arrangement.spacedBy(18.dp)) {
                    row.forEach { book -> ShelfCell(book, Modifier.weight(1f)) }
                    if (row.size == 1) Spacer(Modifier.weight(1f))
                }
            }
        }
    }
}

@Composable
private fun ShelfCell(book: Book, modifier: Modifier) {
    Column(modifier) {
        CoverPlate(book.title, book.author, book.pdf, Modifier.fillMaxWidth())
        Text(book.title, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = YY.c.t1, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(top = 8.dp, bottom = 2.dp))
        Text(book.note, fontSize = 11.sp, color = YY.c.t3, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun ReaderChromeScreen() {
    val paper = YYPapers.Kami
    val haze = rememberHazeState()
    Box(phone.background(paper.bg)) {
        Column(Modifier.padding(start = 28.dp, end = 28.dp, top = 96.dp)) {
            Text(t("故乡"), fontFamily = FontFamily.Serif, fontWeight = FontWeight.Bold, fontSize = 22.sp, color = paper.ink)
            Text(
                t("我冒了严寒，回到相隔二千余里，别了二十余年的故乡去。"),
                fontFamily = FontFamily.Serif, fontSize = 17.sp, lineHeight = 32.sp, color = paper.ink,
                modifier = Modifier.padding(top = 18.dp),
            )
        }
        Column(Modifier.fillMaxSize().padding(12.dp), verticalArrangement = Arrangement.SpaceBetween) {
            ReaderTopBar(
                paper, haze,
                title = t("故乡"),
                subtitle = t("第 %lld 页", 1),
                bookmarked = true,
                onClose = {}, onSearch = {}, onBookmark = {},
                more = {
                    Box(Modifier.size(36.dp), contentAlignment = Alignment.Center) {
                        Icon(Icons.more, t("更多"), tint = paper.ink, modifier = Modifier.size(19.dp))
                    }
                },
            )
            ReaderCapsuleBar(
                paper, haze,
                title = t("故乡"),
                note = t("第 %lld 页", 1),
                noteAccent = false,
                playing = false,
                progress = 0.08f,
                playEnabled = true,
                sentenceStep = false,
                onToc = {}, onCenter = {}, onPrevious = {}, onNext = {}, onPlay = {}, onTypeset = {},
            )
        }
    }
}

@Composable
private fun ImportSheetScreen() {
    Box(phone.background(Color.Black.copy(alpha = 0.32f)), contentAlignment = Alignment.BottomCenter) {
        Column(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(topStart = 28.dp, topEnd = 28.dp)).background(YY.c.bg).padding(top = 12.dp),
        ) {
            ImportChoices(watching = "下载", files = {}, wifi = {}, remote = {}, watch = {})
        }
    }
}
