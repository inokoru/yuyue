package app.yuyue.design

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.PathFillType
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/**
 * SF-Symbol-aligned 24×24 vector icons matching the Apple app's exact metaphors and stroke weight:
 * regular outlined line drawings by default, and `.fill` silhouettes only where Apple uses `.fill`
 * (playback transport, active bookmarks, multi-select ticks, clear-text buttons, pins, ratings).
 */
object Icons {
    // MARK: Navigation & Arrows

    /** plus */
    val add = sf("plus") {
        line("M12 4.75V19.25M4.75 12H19.25")
    }

    /** chevron.left (Apple's standard back & previous chevron) */
    val chevronLeft = sf("chevron.left") {
        line("M14.75 5.25L8 12L14.75 18.75", width = 1.85f)
    }

    /** chevron.right */
    val chevronRight = sf("chevron.right") {
        line("M9.25 5.25L16 12L9.25 18.75", width = 1.85f)
    }

    /** chevron.up */
    val chevronUp = sf("chevron.up") {
        line("M5.25 14.75L12 8L18.75 14.75", width = 1.85f)
    }

    /** chevron.down */
    val chevronDown = sf("chevron.down") {
        line("M5.25 9.25L12 16L18.75 9.25", width = 1.85f)
    }

    /** chevron.up.chevron.down */
    val chevronUpDown = sf("chevron.up.chevron.down") {
        line("M8 9.25L12 5.25L16 9.25M8 14.75L12 18.75L16 14.75", width = 1.8f)
    }

    /** Navigation back on iOS is chevron.left (`‹`). */
    val back = chevronLeft

    /** arrow.right */
    val arrowRight = sf("arrow.right") {
        line("M4.75 12H19.25M13.5 6.25L19.25 12L13.5 17.75")
    }
    val forward = arrowRight

    /** arrow.up */
    val arrowUp = sf("arrow.up") {
        line("M12 19.25V4.75M6.25 10.5L12 4.75L17.75 10.5", width = 1.85f)
    }
    val send = arrowUp

    /** arrow.down */
    val arrowDown = sf("arrow.down") {
        line("M12 4.75V19.25M6.25 13.5L12 19.25L17.75 13.5", width = 1.8f)
    }

    /** arrow.down.to.line */
    val download = sf("arrow.down.to.line") {
        line("M12 3.75V15.5M7.25 10.75L12 15.5L16.75 10.75M5.25 19.5H18.75")
    }

    /** arrow.down.circle */
    val arrowDownCircle = sf("arrow.down.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM12 8v8M8.75 12.75L12 16L15.25 12.75")
    }

    /** arrow.up.forward */
    val external = sf("arrow.up.forward") {
        line("M7 17L17 7M8.75 7H17V15.25")
    }

    /** arrow.left.arrow.right (替换规则) */
    val swapHoriz = sf("arrow.left.arrow.right") {
        line("M4.75 8.75H19.25M15.5 5L19.25 8.75L15.5 12.5M19.25 15.75H4.75M8.5 12L4.75 15.75L8.5 19.5")
    }

    /** Sort menus on Apple use chevron.up.chevron.down. */
    val sort = chevronUpDown

    /** arrow.uturn.backward */
    val undo = sf("arrow.uturn.backward") {
        line("M8.5 5.5L4.5 9.5L8.5 13.5M5 9.5H14a5 5 0 0 1 0 10H10.5")
    }

    /** arrow.uturn.forward */
    val redo = sf("arrow.uturn.forward") {
        line("M15.5 5.5L19.5 9.5L15.5 13.5M19 9.5H10a5 5 0 0 0 0 10H13.5")
    }

    /** arrow.clockwise / arrow.trianglehead.2.clockwise (回顾 · 刷新) */
    val clockwise = sf("arrow.clockwise") {
        line("M19.25 6.25V10.75H14.75M18.7 10.2A7.5 7.5 0 1 0 19.35 14.5")
    }

    /** arrow.triangle.2.circlepath (同步刷新) */
    val sync = sf("arrow.triangle.2.circlepath") {
        line(
            "M19 5.75V9.75H15M5 18.25V14.25H9" +
                "M18.4 9.4A7.25 7.25 0 0 0 6.2 7.65" +
                "M5.6 14.6A7.25 7.25 0 0 0 17.8 16.35"
        )
    }

    // MARK: Marks, Checks & Close

    /** checkmark */
    val check = sf("checkmark") {
        line("M5 12.5L9.75 17.25L19.25 6.75", width = 1.9f)
    }
    val done = check

    /** circle */
    val circle = sf("circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18z")
    }

    /** checkmark.circle (outlined, as on TopButton("选择"), 读完, 快照) */
    val checkCircle = sf("checkmark.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM8.35 12.3L10.85 14.8L15.85 9.5")
    }

    /** checkmark.circle.fill (selected state on cards & rows) */
    val checkCircleFill = sf("checkmark.circle.fill") {
        fill(
            "M12 2.25a9.75 9.75 0 1 1 0 19.5a9.75 9.75 0 1 1 0-19.5z" +
                "M15.4 8.7L10.85 13.45L8.65 11.25A0.85 0.85 0 0 0 7.45 12.45L10.25 15.25A0.85 0.85 0 0 0 11.47 15.23L16.62 9.88A0.85 0.85 0 0 0 15.4 8.7Z"
        )
    }

    /** xmark */
    val close = sf("xmark") {
        line("M6.25 6.25L17.75 17.75M17.75 6.25L6.25 17.75", width = 1.85f)
    }

    /** xmark.circle.fill (clear-text button in search bars) */
    val xmarkCircleFill = sf("xmark.circle.fill") {
        fill(
            "M12 2.5a9.5 9.5 0 1 1 0 19a9.5 9.5 0 1 1 0-19z" +
                "M9.3 8.1A0.85 0.85 0 0 0 8.1 9.3L10.8 12L8.1 14.7A0.85 0.85 0 0 0 9.3 15.9L12 13.2L14.7 15.9A0.85 0.85 0 0 0 15.9 14.7L13.2 12L15.9 9.3A0.85 0.85 0 0 0 14.7 8.1L12 10.8Z"
        )
    }

    /** ellipsis */
    val more = sf("ellipsis") {
        fill(
            "M5.5 10.4a1.6 1.6 0 1 1 0 3.2a1.6 1.6 0 1 1 0-3.2z" +
                "M12 10.4a1.6 1.6 0 1 1 0 3.2a1.6 1.6 0 1 1 0-3.2z" +
                "M18.5 10.4a1.6 1.6 0 1 1 0 3.2a1.6 1.6 0 1 1 0-3.2z"
        )
    }

    /** magnifyingglass */
    val search = sf("magnifyingglass") {
        line("M10.75 4.25a6.5 6.5 0 1 1 0 13a6.5 6.5 0 1 1 0-13zM15.5 15.5L20 20", width = 1.75f)
    }

    // MARK: Share, Copy, Edit & Trash

    /** square.and.arrow.up (Apple's share & export icon) */
    val share = sf("square.and.arrow.up") {
        line("M12 3.25V14.25M8.25 7L12 3.25L15.75 7M8 10.25H6.5A2 2 0 0 0 4.5 12.25V18.5A2 2 0 0 0 6.5 20.5H17.5A2 2 0 0 0 19.5 18.5V12.25A2 2 0 0 0 17.5 10.25H16")
    }
    val upload = share

    /** square.and.arrow.down (导入这份 · 存图) */
    val squareArrowDown = sf("square.and.arrow.down") {
        line("M12 3.25V14.25M8.25 10.5L12 14.25L15.75 10.5M8 9.25H6.5A2 2 0 0 0 4.5 11.25V18.5A2 2 0 0 0 6.5 20.5H17.5A2 2 0 0 0 19.5 18.5V11.25A2 2 0 0 0 17.5 9.25H16")
    }

    /** doc.on.doc (复制) */
    val copy = sf("doc.on.doc") {
        line(
            "M9.25 6.75H16.75A2 2 0 0 1 18.75 8.75V18.25A2 2 0 0 1 16.75 20.25H9.25A2 2 0 0 1 7.25 18.25V8.75A2 2 0 0 1 9.25 6.75Z" +
                "M15.25 6.75V5.5A1.75 1.75 0 0 0 13.5 3.75H6.75A2 2 0 0 0 4.75 5.75V15.25A1.75 1.75 0 0 0 6.5 17H7.25"
        )
    }

    /** pencil (outlined) */
    val edit = sf("pencil") {
        line("M15.6 4.6a2.1 2.1 0 0 1 2.97 2.97L8.25 17.9L4.25 19L5.35 15ZM13.85 6.35L16.9 9.4")
    }

    /** pencil.line (还没有想法) */
    val pencilLine = sf("pencil.line") {
        line("M15.8 4.2a2 2 0 0 1 2.83 2.83L9.2 16.45L5.5 17.4L6.45 13.7ZM4.5 20.25H19.5")
    }

    /** square.and.pencil (阅读器 · 想法) */
    val noteEdit = sf("square.and.pencil") {
        line(
            "M11 4.75H6.75A2.25 2.25 0 0 0 4.5 7V17.25A2.25 2.25 0 0 0 6.75 19.5H17A2.25 2.25 0 0 0 19.25 17.25V13" +
                "M16.5 3.85a1.85 1.85 0 0 1 2.62 2.62L12.25 13.35L9.25 14.1L10 11.1Z"
        )
    }

    /** trash (outlined) */
    val delete = sf("trash") {
        line(
            "M4.5 6.75H19.5M9.25 6.75V5A1.5 1.5 0 0 1 10.75 3.5H13.25A1.5 1.5 0 0 1 14.75 5V6.75" +
                "M6.5 6.75L7.35 18.6A2 2 0 0 0 9.35 20.45H14.65A2 2 0 0 0 16.65 18.6L17.5 6.75" +
                "M10.25 10.5V16.75M13.75 10.5V16.75"
        )
    }

    // MARK: Books, Bookmarks, Notes & Shelf

    /** book (outlined open book) */
    val book = sf("book") {
        line(
            "M12 6.25C10.4 4.95 7.9 4.5 4.75 4.75A1 1 0 0 0 3.75 5.75V17.75A1 1 0 0 0 4.75 18.75C7.9 18.5 10.4 18.95 12 20.25" +
                "C13.6 18.95 16.1 18.5 19.25 18.75A1 1 0 0 0 20.25 17.75V5.75A1 1 0 0 0 19.25 4.75C16.1 4.5 13.6 4.95 12 6.25Z" +
                "M12 6.25V20.25"
        )
    }
    val read = book

    /** book.closed (EPUB 导出) */
    val bookClosed = sf("book.closed") {
        line(
            "M6.75 3.75H17.5A1.75 1.75 0 0 1 19.25 5.5V18.5A1.75 1.75 0 0 1 17.5 20.25H6.75A2 2 0 0 1 4.75 18.25V5.75A2 2 0 0 1 6.75 3.75Z" +
                "M4.75 17.25A2 2 0 0 1 6.75 15.75H19.25M8.25 3.75V15.75"
        )
    }

    /** character.book.closed (生词本) */
    val vocabBook = sf("character.book.closed") {
        line(
            "M6.5 3.75H17.5A1.75 1.75 0 0 1 19.25 5.5V18.5A1.75 1.75 0 0 1 17.5 20.25H6.5A2 2 0 0 1 4.5 18.25V5.75A2 2 0 0 1 6.5 3.75Z" +
                "M4.5 17.25A2 2 0 0 1 6.5 15.75H19.25" +
                "M9.75 12.5L12 6.75L14.25 12.5M10.45 11H13.55"
        )
    }

    /** books.vertical */
    val books = TabIcons.shelf

    /** rectangle.portrait.on.rectangle.portrait (墨水屏) */
    val eink = sf("rectangle.portrait.on.rectangle.portrait") {
        line(
            "M8.75 6.25H16.75A1.75 1.75 0 0 1 18.5 8V18.5A1.75 1.75 0 0 1 16.75 20.25H8.75A1.75 1.75 0 0 1 7 18.5V8A1.75 1.75 0 0 1 8.75 6.25Z" +
                "M7 17.25H6.25A1.75 1.75 0 0 1 4.5 15.5V5.5A1.75 1.75 0 0 1 6.25 3.75H14A1.75 1.75 0 0 1 15.75 5.5V6.25"
        )
    }

    /** bookmark (outlined) */
    val bookmarkOutline = sf("bookmark") {
        line("M6.5 4.75A1.5 1.5 0 0 1 8 3.25H16A1.5 1.5 0 0 1 17.5 4.75V20.25L12 16.35L6.5 20.25Z")
    }

    /** bookmark.fill */
    val bookmark = sf("bookmark.fill") {
        fill("M6 4.75A2 2 0 0 1 8 2.75H16A2 2 0 0 1 18 4.75V20.4A0.65 0.65 0 0 1 16.95 20.92L12 17.4L7.05 20.92A0.65 0.65 0 0 1 6 20.4Z")
    }

    /** highlighter */
    val highlighter = sf("highlighter") {
        line("M14.75 4.25L19.25 8.75L10.5 17.5H6L5.25 16.75L6 13ZM12.25 6.75L16.75 11.25M4.5 20.25H14.5")
    }

    /** doc.text / doc.plaintext */
    val docText = sf("doc.text") {
        line(
            "M7.25 3.5H13.75L18.5 8.25V18.75A1.75 1.75 0 0 1 16.75 20.5H7.25A1.75 1.75 0 0 1 5.5 18.75V5.25A1.75 1.75 0 0 1 7.25 3.5Z" +
                "M13.5 3.75V8.5H18.25M8.5 12H15.5M8.5 15.5H13.5"
        )
    }

    /** note.text (flomo) */
    val noteText = sf("note.text") {
        line(
            "M6.5 4.25H17.5A2 2 0 0 1 19.5 6.25V14.75L14.75 19.5H6.5A2 2 0 0 1 4.5 17.5V6.25A2 2 0 0 1 6.5 4.25Z" +
                "M14.5 19.25V15.25A1 1 0 0 1 15.5 14.25H19.25M8 9.25H16M8 12.75H12.5"
        )
    }

    /** doc.text.magnifyingglass (编码和目录) */
    val docSearch = sf("doc.text.magnifyingglass") {
        line(
            "M18 11V7.75L14 3.75H6.75A1.75 1.75 0 0 0 5 5.5V18.5A1.75 1.75 0 0 0 6.75 20.25H11" +
                "M13.75 4V8H17.75M8 11.5H13M8 14.75H10.75" +
                "M16 13.25a3.25 3.25 0 1 1 0 6.5a3.25 3.25 0 1 1 0-6.5zM18.4 18.9L20.5 21"
        )
    }

    /** folder (outlined) */
    val folder = sf("folder") {
        line("M3.5 7A2 2 0 0 1 5.5 5H9.6a1.5 1.5 0 0 1 1.15.54L12.1 7.2H18.5A2 2 0 0 1 20.5 9.2V17.5A2 2 0 0 1 18.5 19.5H5.5A2 2 0 0 1 3.5 17.5Z")
    }

    /** tray / tray.full (加入合集 · 空合集) */
    val inbox = sf("tray") {
        line(
            "M3.75 13.25L5.65 6.25A1.75 1.75 0 0 1 7.35 5H16.65A1.75 1.75 0 0 1 18.35 6.25L20.25 13.25V17.75A1.75 1.75 0 0 1 18.5 19.5H5.5A1.75 1.75 0 0 1 3.75 17.75Z" +
                "M3.9 13.5H8.5L9.75 15.5H14.25L15.5 13.5H20.1"
        )
    }

    /** square.stack / rectangle.stack (合集 · Anki 卡片) */
    val collection = sf("rectangle.stack") {
        line(
            "M5 9.25H19A1.75 1.75 0 0 1 20.75 11V18.25A1.75 1.75 0 0 1 19 20H5A1.75 1.75 0 0 1 3.25 18.25V11A1.75 1.75 0 0 1 5 9.25Z" +
                "M6 6.25H18M8 3.5H16"
        )
    }

    /** square.grid.2x2 */
    val grid = sf("square.grid.2x2") {
        line(
            "M4.5 4.5H10.25V10.25H4.5ZM13.75 4.5H19.5V10.25H13.75Z" +
                "M4.5 13.75H10.25V19.5H4.5ZM13.75 13.75H19.5V19.5H13.75Z"
        )
    }

    /** tablecells (CSV 表格) */
    val table = sf("tablecells") {
        line("M3.75 5.25H20.25V18.75H3.75ZM3.75 9.75H20.25M3.75 14.25H20.25M9.5 5.25V18.75")
    }

    /** list.bullet */
    val list = sf("list.bullet") {
        fill("M4.75 5.6a1.15 1.15 0 1 1 0 2.3a1.15 1.15 0 1 1 0-2.3zM4.75 10.85a1.15 1.15 0 1 1 0 2.3a1.15 1.15 0 1 1 0-2.3zM4.75 16.1a1.15 1.15 0 1 1 0 2.3a1.15 1.15 0 1 1 0-2.3z")
        line("M8.5 6.75H19.5M8.5 12H19.5M8.5 17.25H19.5")
    }

    /** list.bullet.rectangle */
    val listBulletRect = sf("list.bullet.rectangle") {
        line("M4.75 5H19.25A1.5 1.5 0 0 1 20.75 6.5V17.5A1.5 1.5 0 0 1 19.25 19H4.75A1.5 1.5 0 0 1 3.25 17.5V6.5A1.5 1.5 0 0 1 4.75 5ZM9.75 9.25H17.25M9.75 12.5H17.25M9.75 15.75H15")
        fill("M6.9 8.4a0.85 0.85 0 1 1 0 1.7a0.85 0.85 0 1 1 0-1.7zM6.9 11.65a0.85 0.85 0 1 1 0 1.7a0.85 0.85 0 1 1 0-1.7zM6.9 14.9a0.85 0.85 0 1 1 0 1.7a0.85 0.85 0 1 1 0-1.7z")
    }

    /** viewfinder (沉浸阅读) */
    val viewfinder = sf("viewfinder") {
        line(
            "M4 8.25V5.75A1.75 1.75 0 0 1 5.75 4H8.25M15.75 4H18.25A1.75 1.75 0 0 1 20 5.75V8.25" +
                "M20 15.75V18.25A1.75 1.75 0 0 1 18.25 20H15.75M8.25 20H5.75A1.75 1.75 0 0 1 4 18.25V15.75"
        )
    }

    /** textformat.size (aA) */
    val textSize = sf("textformat.size") {
        line("M10.5 18L15 6L19.5 18M11.9 14.25H18.1M3.5 18L6.5 10.5L9.5 18M4.45 15.75H8.55")
    }

    // MARK: Audio, Playback & Speech

    /** play.fill */
    val play = sf("play.fill") {
        fill("M6.75 4.65A1.1 1.1 0 0 1 8.45 3.73L19.15 10.98A1.2 1.2 0 0 1 19.15 13.02L8.45 20.27A1.1 1.1 0 0 1 6.75 19.35Z")
    }

    /** pause.fill */
    val pause = sf("pause.fill") {
        fill(
            "M6.5 4.25H9.5A1.25 1.25 0 0 1 10.75 5.5V18.5A1.25 1.25 0 0 1 9.5 19.75H6.5A1.25 1.25 0 0 1 5.25 18.5V5.5A1.25 1.25 0 0 1 6.5 4.25Z" +
                "M14.5 4.25H17.5A1.25 1.25 0 0 1 18.75 5.5V18.5A1.25 1.25 0 0 1 17.5 19.75H14.5A1.25 1.25 0 0 1 13.25 18.5V5.5A1.25 1.25 0 0 1 14.5 4.25Z"
        )
    }

    /** pause.circle (念完停一下) */
    val pauseCircle = sf("pause.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM10 9.25V14.75M14 9.25V14.75")
    }

    /** stop.fill */
    val stop = sf("stop.fill") {
        fill("M6.75 5.25H17.25A1.5 1.5 0 0 1 18.75 6.75V17.25A1.5 1.5 0 0 1 17.25 18.75H6.75A1.5 1.5 0 0 1 5.25 17.25V6.75A1.5 1.5 0 0 1 6.75 5.25Z")
    }

    /** backward.end.fill (播放条 · 上一句) */
    val previous = sf("backward.end.fill") {
        fill(
            "M5.25 5.5A0.9 0.9 0 0 1 7.05 5.5V18.5A0.9 0.9 0 0 1 5.25 18.5Z" +
                "M18.65 5.55A0.95 0.95 0 0 1 20.1 6.37V17.63A0.95 0.95 0 0 1 18.65 18.45L9.15 12.82A0.95 0.95 0 0 1 9.15 11.18Z"
        )
    }

    /** forward.end.fill (播放条 · 下一句) */
    val next = sf("forward.end.fill") {
        fill(
            "M16.95 5.5A0.9 0.9 0 0 1 18.75 5.5V18.5A0.9 0.9 0 0 1 16.95 18.5Z" +
                "M5.35 5.55A0.95 0.95 0 0 0 3.9 6.37V17.63A0.95 0.95 0 0 0 5.35 18.45L14.85 12.82A0.95 0.95 0 0 0 14.85 11.18Z"
        )
    }

    /** gobackward.15 (有声书 · 后退 15 秒, with "15" inside) */
    val back15 = sf("gobackward.15") {
        line("M12 5.25A7.75 7.75 0 1 1 5.15 9.35M12 2.5L9.15 5.25L12 8")
        line(
            "M9.4 10.6V15.4M8.5 11.5L9.4 10.6" +
                "M15.4 10.6H12.4V12.8H14.3A1.3 1.3 0 0 1 14.3 15.4H12.3",
            width = 1.4f,
        )
    }

    /** goforward.30 (有声书 · 前进 30 秒, matched with gobackward.15) */
    val ahead30 = sf("goforward.30") {
        line("M12 5.25A7.75 7.75 0 1 0 18.85 9.35M12 2.5L14.85 5.25L12 8")
        line(
            "M8.3 10.8H10.5A1.15 1.15 0 0 1 10.5 13H9.2M10.5 13A1.2 1.2 0 0 1 10.5 15.4H8.2" +
                "M14.4 10.6A1.55 1.55 0 0 1 15.95 12.15V13.85A1.55 1.55 0 0 1 12.85 13.85V12.15A1.55 1.55 0 0 1 14.4 10.6Z",
            width = 1.4f,
        )
    }

    /** moon.zzz (定时 off) */
    val sleep = sf("moon.zzz") {
        line(
            "M13.5 4.25A7.75 7.75 0 1 0 19.75 15.2A6.25 6.25 0 0 1 13.5 4.25Z" +
                "M15.25 4.5H18L15.25 7.5H18M18.25 8.75H20.25L18.25 11H20.25",
            width = 1.55f,
        )
    }

    /** moon.zzz.fill (定时 on) */
    val sleepFill = sf("moon.zzz.fill") {
        fill("M13.5 4.25A7.75 7.75 0 1 0 19.75 15.2A6.25 6.25 0 0 1 13.5 4.25Z")
        line("M15.25 4.5H18L15.25 7.5H18M18.25 8.75H20.25L18.25 11H20.25", width = 1.55f)
    }

    /** headphones */
    val headphones = sf("headphones") {
        line(
            "M4.25 14V12a7.75 7.75 0 0 1 15.5 0V14" +
                "M4.25 14A1.75 1.75 0 0 1 6 12.25H6.75A1.5 1.5 0 0 1 8.25 13.75V17.75A1.5 1.5 0 0 1 6.75 19.25H6A1.75 1.75 0 0 1 4.25 17.5Z" +
                "M19.75 14A1.75 1.75 0 0 0 18 12.25H17.25A1.5 1.5 0 0 0 15.75 13.75V17.75A1.5 1.5 0 0 0 17.25 19.25H18A1.75 1.75 0 0 0 19.75 17.5Z"
        )
    }

    /** radio (播客, matching macOS asset.radio) */
    val radio = sf("radio") {
        line(
            "M6 9H18A3 3 0 0 1 21 12V17.25A3 3 0 0 1 18 20.25H6A3 3 0 0 1 3 17.25V12A3 3 0 0 1 6 9Z" +
                "M7.5 9L17.25 3.75" +
                "M15.75 12.15A2.4 2.4 0 1 1 15.75 16.95A2.4 2.4 0 1 1 15.75 12.15Z" +
                "M6.75 14.55H9.75",
            width = 1.75f,
        )
    }

    /** radio.fill (播客 active tab, matching iOS radio-fill) */
    val radioFill = sf("radio.fill") {
        line("M7.5 9L17.25 3.75", width = 1.75f)
        fill(
            "M6 8.2H18A3.8 3.8 0 0 1 21.8 12V17.25A3.8 3.8 0 0 1 18 21.05H6A3.8 3.8 0 0 1 2.2 17.25V12A3.8 3.8 0 0 1 6 8.2Z" +
                "M15.75 12.15A2.4 2.4 0 1 0 15.75 16.95A2.4 2.4 0 1 0 15.75 12.15Z" +
                "M6.75 13.7H9.75A0.85 0.85 0 0 1 9.75 15.4H6.75A0.85 0.85 0 0 1 6.75 13.7Z"
        )
    }

    /** microphone (录声音) */
    val microphone = sf("microphone") {
        line(
            "M12 3.25A2.75 2.75 0 0 1 14.75 6V11.25A2.75 2.75 0 0 1 9.25 11.25V6A2.75 2.75 0 0 1 12 3.25Z" +
                "M6.5 10.75V11.25a5.5 5.5 0 0 0 11 0V10.75M12 16.75V20.25M8.75 20.25H15.25"
        )
    }

    /** mic.fill */
    val microphoneFill = sf("mic.fill") {
        fill("M12 3A3 3 0 0 1 15 6V11.25A3 3 0 0 1 9 11.25V6A3 3 0 0 1 12 3Z")
        line("M6.5 10.75V11.25a5.5 5.5 0 0 0 11 0V10.75M12 16.75V20.25M8.75 20.25H15.25")
    }

    /** waveform */
    val waveform = sf("waveform") {
        line("M4.5 10V14M8.25 6.75V17.25M12 4V20M15.75 7.5V16.5M19.5 9.75V14.25", width = 1.8f)
    }

    /** waveform.path.ecg (测连接) */
    val waveformEcg = sf("waveform.path.ecg") {
        line("M3 12H7.25L9.5 6.5L12.75 17.5L15.25 10L16.75 12H21")
    }

    /** speaker.wave.2 (生词本 · 读一遍 / 翻页音效) */
    val speakerWave = sf("speaker.wave.2") {
        line("M4.25 9.75H7.5L12 6V18L7.5 14.25H4.25ZM15.25 9.25a4 4 0 0 1 0 5.5M17.75 6.75a7.5 7.5 0 0 1 0 10.5")
    }

    /** speaker.slash (静音) */
    val speakerSlash = sf("speaker.slash") {
        line("M4.25 9.75H7.5L12 6V18L7.5 14.25H4.25ZM15.5 9.5L20.5 14.5M20.5 9.5L15.5 14.5")
    }

    /** bubble.left.and.bubble.right (对话气泡) */
    val bubbles = sf("bubble.left.and.bubble.right") {
        line("M4.5 6.5h9a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H9l-3 2.5V14.5H4.5a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2zM15.5 10.5h4a2 2 0 0 1 2 2v3.5a2 2 0 0 1-2 2h-1.25V20.5l-2.75-2.5H12a2 2 0 0 1-1.9-1.4")
    }

    /** ear (替换规则 · 只改朗读) */
    val ear = sf("ear") {
        line("M7.5 10a4.75 4.75 0 1 1 9.1 1.9C15.9 13.7 14.4 14.7 13.8 16.4A2.6 2.6 0 0 1 9 15.75M10.25 10.25a2 2 0 1 1 3.6 1.2c-.5.75-1.1 1.25-1.35 2.05")
    }

    /** repeat */
    val repeat = sf("repeat") {
        line("M17 4.75L19.75 7.5L17 10.25M4.5 11V9.5A2 2 0 0 1 6.5 7.5H19.5M7 19.25L4.25 16.5L7 13.75M19.5 13V14.5A2 2 0 0 1 17.5 16.5H4.5")
    }

    /** repeat.1 (单句循环) */
    val repeatOne = sf("repeat.1") {
        line("M17 4.75L19.75 7.5L17 10.25M4.5 11V9.5A2 2 0 0 1 6.5 7.5H19.5M7 19.25L4.25 16.5L7 13.75M19.5 13V14.5A2 2 0 0 1 17.5 16.5H4.5M12 10.25V13.75", width = 1.6f)
    }

    /** scope (有声书 · 对准) */
    val target = sf("scope") {
        line("M12 5a7 7 0 1 1 0 14a7 7 0 1 1 0-14zM12 2.5V6.5M12 17.5V21.5M2.5 12H6.5M17.5 12H21.5")
    }

    // MARK: Settings, Cloud, Companion & About

    /** slider.horizontal.3 (通用 · 这本录音 · 常用规则) */
    val general = sf("slider.horizontal.3") {
        line(
            "M4 7H8.5M12.5 7H20M10.5 5a2 2 0 1 1 0 4a2 2 0 1 1 0-4z" +
                "M4 12H13.5M17.5 12H20M15.5 10a2 2 0 1 1 0 4a2 2 0 1 1 0-4z" +
                "M4 17H6.5M10.5 17H20M8.5 15a2 2 0 1 1 0 4a2 2 0 1 1 0-4z"
        )
    }

    /** gearshape (书伴设置 · 声音服务商设置) */
    val settings = sf("gearshape") {
        line(
            "M12 9a3 3 0 1 1 0 6a3 3 0 1 1 0-6z" +
                "M10.35 3.75H13.65L14.2 5.95L16.15 7.08L18.3 6.35L19.95 9.2L18.35 10.85V13.15L19.95 14.8L18.3 17.65L16.15 16.92L14.2 18.05L13.65 20.25H10.35L9.8 18.05L7.85 16.92L5.7 17.65L4.05 14.8L5.65 13.15V10.85L4.05 9.2L5.7 6.35L7.85 7.08L9.8 5.95Z"
        )
    }

    /** sun.max (外观) */
    val appearance = sf("sun.max") {
        line(
            "M12 7.75a4.25 4.25 0 1 1 0 8.5a4.25 4.25 0 1 1 0-8.5z" +
                "M12 2.75V4.75M12 19.25V21.25M2.75 12H4.75M19.25 12H21.25" +
                "M5.45 5.45L6.85 6.85M17.15 17.15L18.55 18.55M18.55 5.45L17.15 6.85M6.85 17.15L5.45 18.55"
        )
    }

    /** globe (外文 · Google Drive · 网页链接) */
    val globe = sf("globe") {
        line(
            "M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18z" +
                "M3.35 12H20.65M12 3c2.4 2.4 3.75 5.55 3.75 9S14.4 18.6 12 21c-2.4-2.4-3.75-5.55-3.75-9S9.6 5.4 12 3z"
        )
    }

    /** character.bubble / translate (阅读器 · 翻译) */
    val translate = sf("translate") {
        line("M4 6.5H12.5M8.25 4.25V6.5M6 6.5c.35 3.3 2.35 6.1 5.6 7.85M10.75 6.5c-.45 3.1-2.5 5.95-5.75 7.85M12.5 19.5L15.75 11L19 19.5M13.6 16.75H17.9")
    }

    /** sparkle (4-point SF star: 问 · 问书伴 · 科幻合集) */
    val sparkle = sf("sparkle") {
        line("M12 3.25C12.75 8.1 15.9 11.25 20.75 12C15.9 12.75 12.75 15.9 12 20.75C11.25 15.9 8.1 12.75 3.25 12C8.1 11.25 11.25 8.1 12 3.25Z")
    }
    val companion = sparkle

    /** sparkles (酿一期书房电台) */
    val sparkles = sf("sparkles") {
        line(
            "M10.5 4.25C11.1 8.1 13.9 10.9 17.75 11.5C13.9 12.1 11.1 14.9 10.5 18.75C9.9 14.9 7.1 12.1 3.25 11.5C7.1 10.9 9.9 8.1 10.5 4.25Z" +
                "M18.25 3.5V7.5M16.25 5.5H20.25M17.75 16.25V19.75M16 18H19.5"
        )
    }

    /** text.bubble (我的 › 书伴 · 阅读器 › 书伴) */
    val textBubble = sf("text.bubble") {
        line(
            "M6.5 4.5H17.5A2.75 2.75 0 0 1 20.25 7.25V14.25A2.75 2.75 0 0 1 17.5 17H10.5L6.25 20.25V17H6.5A2.75 2.75 0 0 1 3.75 14.25V7.25A2.75 2.75 0 0 1 6.5 4.5Z" +
                "M8 9.25H16M8 12.5H13.25"
        )
    }

    /** exclamationmark.bubble (反馈 · 问题与建议) */
    val feedback = sf("exclamationmark.bubble") {
        line("M6.5 4.25H17.5A2.75 2.75 0 0 1 20.25 7V14A2.75 2.75 0 0 1 17.5 16.75H10.5L6.25 20V16.75H6.5A2.75 2.75 0 0 1 3.75 14V7A2.75 2.75 0 0 1 6.5 4.25ZM12 7.5V11.25")
        fill("M12 13.1a0.95 0.95 0 1 1 0 1.9a0.95 0.95 0 1 1 0-1.9z")
    }

    /** icloud (我的 › 同步) */
    val icloud = sf("icloud") {
        line("M6.75 18.25H17.25a4 4 0 0 0 .55-7.96A5.5 5.5 0 0 0 7.15 8.85a4.5 4.5 0 0 0-.4 9.4Z")
    }

    /** icloud.and.arrow.down (在别的设备上) */
    val icloudDown = sf("icloud.and.arrow.down") {
        line("M7 16.75H5.75a3.75 3.75 0 0 1-.35-7.48A5.25 5.25 0 0 1 15.65 8a3.75 3.75 0 0 1 1.6 7.15M12 11.75V20.25M9 17.25L12 20.25L15 17.25")
    }
    val cloudDownload = icloudDown

    /** icloud.slash */
    val cloudOff = sf("icloud.slash") {
        line("M4.25 4.25L19.75 19.75M9.4 6.1A5.25 5.25 0 0 1 17.75 10.3a3.9 3.9 0 0 1 2 6.4M15.75 18.25H6.75a4.25 4.25 0 0 1-.85-8.42")
    }

    /** server.rack (书库来源 · WebDAV) */
    val server = sf("server.rack") {
        line(
            "M5.5 4.75H18.5A1.75 1.75 0 0 1 20.25 6.5V9.5A1.75 1.75 0 0 1 18.5 11.25H5.5A1.75 1.75 0 0 1 3.75 9.5V6.5A1.75 1.75 0 0 1 5.5 4.75Z" +
                "M5.5 12.75H18.5A1.75 1.75 0 0 1 20.25 14.5V17.5A1.75 1.75 0 0 1 18.5 19.25H5.5A1.75 1.75 0 0 1 3.75 17.5V14.5A1.75 1.75 0 0 1 5.5 12.75Z" +
                "M7.25 8H11.25M7.25 16H11.25"
        )
        fill("M16.5 7.1a0.9 0.9 0 1 1 0 1.8a0.9 0.9 0 1 1 0-1.8zM16.5 15.1a0.9 0.9 0 1 1 0 1.8a0.9 0.9 0 1 1 0-1.8z")
    }

    /** internaldrive (存储) */
    val storage = sf("internaldrive") {
        line(
            "M5.75 5.25H18.25A1.75 1.75 0 0 1 20 7V17A1.75 1.75 0 0 1 18.25 18.75H5.75A1.75 1.75 0 0 1 4 17V7A1.75 1.75 0 0 1 5.75 5.25Z" +
                "M4 14.25H20M7.25 16.5H11"
        )
        fill("M16.5 15.6a0.9 0.9 0 1 1 0 1.8a0.9 0.9 0 1 1 0-1.8z")
    }

    /** shippingbox (数据快照) */
    val snapshot = sf("shippingbox") {
        line("M12 3.25L20 7.6V16.4L12 20.75L4 16.4V7.6ZM4.25 7.75L12 12L19.75 7.75M12 12V20.5M8 5.45L15.85 9.85")
    }

    /** clock.arrow.circlepath (最近改过) */
    val history = sf("clock.arrow.circlepath") {
        line("M4.25 5.25V9.5H8.5M4.8 9.2A8.25 8.25 0 1 1 4.1 13.75M12 7.75V12.25L15.25 14.25")
    }

    /** wifi */
    val wifi = sf("wifi") {
        line("M2.75 8.75a13.25 13.25 0 0 1 18.5 0M6 12.25a8.75 8.75 0 0 1 12 0M9.25 15.75a4.25 4.25 0 0 1 5.5 0")
        fill("M12 17.85a1.25 1.25 0 1 1 0 2.5a1.25 1.25 0 1 1 0-2.5z")
    }

    /** info.circle */
    val info = sf("info.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM12 11.25V16.25")
        fill("M12 7.35a1 1 0 1 1 0 2a1 1 0 1 1 0-2z")
    }

    /** exclamationmark.circle */
    val error = sf("exclamationmark.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM12 7.5V12.75")
        fill("M12 15.1a1 1 0 1 1 0 2a1 1 0 1 1 0-2z")
    }

    /** exclamationmark.triangle */
    val warning = sf("exclamationmark.triangle") {
        line("M10.45 4.65a1.8 1.8 0 0 1 3.1 0L20.35 16.6A1.8 1.8 0 0 1 18.8 19.25H5.2A1.8 1.8 0 0 1 3.65 16.6ZM12 9.25V13.5")
        fill("M12 15.55a0.95 0.95 0 1 1 0 1.9a0.95 0.95 0 1 1 0-1.9z")
    }

    /** exclamationmark (bare, for small status circles) */
    val bang = sf("exclamationmark") {
        line("M12 5.25V13.75", width = 2.1f)
        fill("M12 16.6a1.25 1.25 0 1 1 0 2.5a1.25 1.25 0 1 1 0-2.5z")
    }

    /** chevron.left.forwardslash.chevron.right (开源许可) */
    val code = sf("chevron.left.forwardslash.chevron.right") {
        line("M7.75 8.25L3.75 12L7.75 15.75M16.25 8.25L20.25 12L16.25 15.75M13.75 5.25L10.25 18.75")
    }

    /** checkmark.shield (隐私说明) */
    val shieldCheck = sf("checkmark.shield") {
        line("M12 3.25L19 6.25V11.75C19 16.15 16.05 19.45 12 20.75C7.95 19.45 5 16.15 5 11.75V6.25ZM9.1 12.15L11.15 14.2L15.15 9.95")
    }

    /** link (源代码 · 链接) */
    val link = sf("link") {
        line(
            "M10 14a4.25 4.25 0 0 1 0-6L12.75 5.25a4.25 4.25 0 0 1 6 6L17.1 12.9" +
                "M14 10a4.25 4.25 0 0 1 0 6L11.25 18.75a4.25 4.25 0 0 1-6-6L6.9 11.1"
        )
    }

    // MARK: People, Devices, Pins & Collection Symbols

    /** person (outlined) */
    val person = TabIcons.me

    /** person.crop.circle (谁说的 · 人物页) */
    val personCircle = sf("person.crop.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM12 7.25a2.75 2.75 0 1 1 0 5.5a2.75 2.75 0 1 1 0-5.5zM6.65 18.55c1.1-2.3 3.05-3.55 5.35-3.55s4.25 1.25 5.35 3.55")
    }

    /** person.2 (人物声音) */
    val personTwo = sf("person.2") {
        line(
            "M9 5.5a3 3 0 1 1 0 6a3 3 0 1 1 0-6zM3.25 19c.55-3 2.75-4.75 5.75-4.75S14.2 16 14.75 19" +
                "M15.75 6.5a2.5 2.5 0 1 1 0 5M16.25 14.5c2.25.25 3.95 1.75 4.5 4.5"
        )
    }

    /** cast.svg (人物：谁说了话，各用什么声音) */
    val cast = sf("cast") {
        line("M10 5.6a6.4 6.4 0 1 1 0 12.8a6.4 6.4 0 1 1 0-12.8zM7.8 13.8c.9 1.1 3.5 1.1 4.4 0M19.2 8.8a4.9 4.9 0 0 1 0 6.4", width = 1.7f)
        fill("M8 9.75a0.85 0.85 0 1 1 0 1.7a0.85 0.85 0 1 1 0-1.7zM12 9.75a0.85 0.85 0 1 1 0 1.7a0.85 0.85 0 1 1 0-1.7z")
    }

    /** lock (outlined) */
    val lock = sf("lock") {
        line("M6.5 10.5H17.5A1.75 1.75 0 0 1 19.25 12.25V18.5A1.75 1.75 0 0 1 17.5 20.25H6.5A1.75 1.75 0 0 1 4.75 18.5V12.25A1.75 1.75 0 0 1 6.5 10.5ZM8.5 10.5V7.5a3.5 3.5 0 0 1 7 0V10.5")
    }

    /** lock.fill */
    val lockFill = sf("lock.fill") {
        line("M8.5 10.5V7.5a3.5 3.5 0 0 1 7 0V10.5", width = 1.9f)
        fill("M6.25 10H17.75A2 2 0 0 1 19.75 12V18.75A2 2 0 0 1 17.75 20.75H6.25A2 2 0 0 1 4.25 18.75V12A2 2 0 0 1 6.25 10Z")
    }

    /** pin.fill */
    val pin = sf("pin.fill") {
        fill("M9 3.5H15A0.85 0.85 0 0 1 15.65 4.9L14.4 6.6V10.5L16.75 12.85A0.75 0.75 0 0 1 16.22 14.15H7.78A0.75 0.75 0 0 1 7.25 12.85L9.6 10.5V6.6L8.35 4.9A0.85 0.85 0 0 1 9 3.5Z")
        line("M12 14.15V20.5", width = 1.85f)
    }

    /** quote.opening (“) */
    val quote = sf("quote.opening") {
        line(
            "M9.75 7.25C7.15 8.55 5.75 10.8 5.75 13.75a2.5 2.5 0 1 0 2.65-2.5c-.45 0-.85.1-1.15.25" +
                "M18 7.25c-2.6 1.3-4 3.55-4 6.5a2.5 2.5 0 1 0 2.65-2.5c-.45 0-.85.1-1.15.25",
            width = 1.75f,
        )
    }

    /** star.fill */
    val starFill = sf("star.fill") {
        fill("M12 3.1L14.65 8.65L20.75 9.45L16.25 13.7L17.4 19.75L12 16.8L6.6 19.75L7.75 13.7L3.25 9.45L9.35 8.65Z")
    }

    /** iphone */
    val iphone = sf("iphone") {
        line("M8 3.25H16A2 2 0 0 1 18 5.25V18.75A2 2 0 0 1 16 20.75H8A2 2 0 0 1 6 18.75V5.25A2 2 0 0 1 8 3.25ZM10.5 5.5H13.5")
    }
    val phone = iphone

    /** laptopcomputer */
    val laptop = sf("laptopcomputer") {
        line("M5.25 5.5H18.75A1.5 1.5 0 0 1 20.25 7V16.25H3.75V7A1.5 1.5 0 0 1 5.25 5.5ZM2.25 18.5H21.75")
    }

    /** moon (睡前合集 · PDF 夜间) */
    val moon = sf("moon") {
        line("M14.25 4A8 8 0 1 0 20 15.65A6.5 6.5 0 0 1 14.25 4Z")
    }

    /** cup.and.saucer (闲读合集) */
    val cup = sf("cup.and.saucer") {
        line("M5 7.5H15.5V13A4.5 4.5 0 0 1 11 17.5H9.5A4.5 4.5 0 0 1 5 13ZM15.5 8.75H17.5a2.25 2.25 0 0 1 0 4.5H15.5M4 19.75H17")
    }

    /** key (推理合集) */
    val key = sf("key") {
        line("M9 8.25a4.25 4.25 0 1 1 0 8.5a4.25 4.25 0 1 1 0-8.5zM13.15 11.5H20.5V14.75M17.5 11.5V14.25")
    }

    /** heart (最爱合集) */
    val heart = sf("heart") {
        line("M12 19.75C12 19.75 4 14.6 4 9.15A4.4 4.4 0 0 1 12 6.65A4.4 4.4 0 0 1 20 9.15C20 14.6 12 19.75 12 19.75Z")
    }

    /** clock (历史合集 · 记录) */
    val clock = sf("clock") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM12 7.25V12.25L15.5 14.25")
    }

    /** eye (科普合集 · 密码可见) */
    val eye = sf("eye") {
        line("M2.75 12C4.65 7.75 8.05 5.5 12 5.5S19.35 7.75 21.25 12C19.35 16.25 15.95 18.5 12 18.5S4.65 16.25 2.75 12ZM12 9.25a2.75 2.75 0 1 1 0 5.5a2.75 2.75 0 1 1 0-5.5z")
    }

    /** eye.slash (只听 · 隐藏密码) */
    val eyeSlash = sf("eye.slash") {
        line("M3.75 3.75L20.25 20.25M9.9 5.8A8.8 8.8 0 0 1 12 5.5c3.95 0 7.35 2.25 9.25 6.5a11.3 11.3 0 0 1-3.1 4.15M6.35 8.15A11.4 11.4 0 0 0 2.75 12c1.9 4.25 5.3 6.5 9.25 6.5c1.3 0 2.55-.26 3.7-.75")
    }

    /** leaf (新枝) */
    val leaf = sf("leaf") {
        line("M19.25 4.75C12.5 4.75 6.5 8.5 5.5 15.25C5.25 17 6.25 18.5 8 18.5C14.75 18.5 19.25 13 19.25 4.75ZM5.25 19.25C8.25 15.75 11.5 12.75 15.25 10.25")
    }

    /** xmark.circle (outlined, 弃读) */
    val xmarkCircle = sf("xmark.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM9.25 9.25L14.75 14.75M14.75 9.25L9.25 14.75")
    }

    /** minus.circle (从格子里拿掉) */
    val minusCircle = sf("minus.circle") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM8.25 12H15.75")
    }

    /** nosign (并非人物) */
    val nosign = sf("nosign") {
        line("M12 3a9 9 0 1 1 0 18a9 9 0 1 1 0-18zM5.65 5.65L18.35 18.35")
    }

    /** arrow.triangle.merge (合并到别人) */
    val merge = sf("arrow.triangle.merge") {
        line("M12 4.25V12.5M8.75 7.5L12 4.25L15.25 7.5M6.25 19.5L12 12.5L17.75 19.5")
    }

    /** hand.tap (第一次进阅读器 · 菜单提示) */
    val handTap = sf("hand.tap") {
        line(
            "M8.5 7.5a3.5 3.5 0 0 1 7 0" +
                "M10.5 9V14.5L8.35 12.85a1.35 1.35 0 0 0-1.85 2L10.4 19.2A2.5 2.5 0 0 0 12.25 20H16.25A2.5 2.5 0 0 0 18.75 17.5V13.25a1.5 1.5 0 0 0-2.5-.95V12a1.5 1.5 0 0 0-2.75-.75V9a1.5 1.5 0 0 0-3 0Z"
        )
    }

    /** folder.badge.gearshape (监听一个文件夹) */
    val folderGear = sf("folder.badge.gearshape") {
        line(
            "M11.5 19.25H5.5A2 2 0 0 1 3.5 17.25V7A2 2 0 0 1 5.5 5H9.6a1.5 1.5 0 0 1 1.15.54L12.1 7.2H18.5A2 2 0 0 1 20.5 9.2V11.25" +
                "M17 14.5a2.25 2.25 0 1 1 0 4.5a2.25 2.25 0 1 1 0-4.5z" +
                "M17 12.75V14M17 19.5V20.75M13 16.75H14.25M19.75 16.75H21"
        )
    }

    /** music.note (Wi‑Fi 传书里的音频) */
    val musicNote = sf("music.note") {
        line("M10 17.25V5.75L18.5 4V15.5M10 9.5L18.5 7.75")
        fill("M7.5 15a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5zM16 13.25a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5z")
    }

    /** rectangle.split.2x1 (气泡放在) */
    val splitColumns = sf("rectangle.split.2x1") {
        line("M4.25 6.25H19.75A1.5 1.5 0 0 1 21.25 7.75V16.25A1.5 1.5 0 0 1 19.75 17.75H4.25A1.5 1.5 0 0 1 2.75 16.25V7.75A1.5 1.5 0 0 1 4.25 6.25ZM12 6.25V17.75")
    }

    /** paintpalette (调整颜色) */
    val palette = sf("paintpalette") {
        line("M12 3.25a8.75 8.75 0 0 0 0 17.5h1.5a2.25 2.25 0 0 0 2.25-2.25c0-.6-.24-1.15-.62-1.55a2.1 2.1 0 0 1 1.52-3.55H18a2.75 2.75 0 0 0 2.75-2.75A7.4 7.4 0 0 0 12 3.25Z")
        fill("M8 9.25a1.1 1.1 0 1 1 0 2.2a1.1 1.1 0 1 1 0-2.2zM11.25 6.75a1.1 1.1 0 1 1 0 2.2a1.1 1.1 0 1 1 0-2.2zM15.25 7.5a1.1 1.1 0 1 1 0 2.2a1.1 1.1 0 1 1 0-2.2zM7.5 13.25a1.1 1.1 0 1 1 0 2.2a1.1 1.1 0 1 1 0-2.2z")
    }

    /** plus.square.on.square (复制一张纸色) */
    val duplicateSquare = sf("plus.square.on.square") {
        line(
            "M8.5 8.25H18.25A1.75 1.75 0 0 1 20 10V18.25A1.75 1.75 0 0 1 18.25 20H8.5A1.75 1.75 0 0 1 6.75 18.25V10A1.75 1.75 0 0 1 8.5 8.25Z" +
                "M6.75 16.5H5.5A1.75 1.75 0 0 1 3.75 14.75V5.75A1.75 1.75 0 0 1 5.5 4H14.5A1.75 1.75 0 0 1 16.25 5.75V8.25" +
                "M13.38 11.25V17M10.5 14.12H16.25"
        )
    }

    /** book.fill (active shelf place tab) */
    val bookFill = sf("book.fill") {
        fill(
            "M11.25 5.8C9.65 4.65 7.3 4.25 4.5 4.5A1.25 1.25 0 0 0 3.25 5.75V17.9A1.15 1.15 0 0 0 4.5 19.05C7.3 18.8 9.65 19.2 11.25 20.35Z" +
                "M12.75 5.8V20.35C14.35 19.2 16.7 18.8 19.5 19.05A1.15 1.15 0 0 0 20.75 17.9V5.75A1.25 1.25 0 0 0 19.5 4.5C16.7 4.25 14.35 4.65 12.75 5.8Z"
        )
    }

    /** doc (反馈附件文件) */
    val doc = sf("doc") {
        line(
            "M7.25 3.5H13.75L18.5 8.25V18.75A1.75 1.75 0 0 1 16.75 20.5H7.25A1.75 1.75 0 0 1 5.5 18.75V5.25A1.75 1.75 0 0 1 7.25 3.5Z" +
                "M13.5 3.75V8.5H18.25"
        )
    }

    /** paperclip (加附件) */
    val paperclip = sf("paperclip") {
        line("M16.5 7.5L8.9 15.1a2.25 2.25 0 0 0 3.18 3.18L19.75 10.6a4.25 4.25 0 0 0-6.01-6.01L6.1 12.25a6.25 6.25 0 0 0 8.84 8.84L20 16")
    }

    /** photo (照片) */
    val photo = sf("photo") {
        line("M4.75 5.25H19.25A1.5 1.5 0 0 1 20.75 6.75V17.25A1.5 1.5 0 0 1 19.25 18.75H4.75A1.5 1.5 0 0 1 3.25 17.25V6.75A1.5 1.5 0 0 1 4.75 5.25ZM3.75 16L8.75 11.25L12.75 15L15.75 12.25L20.25 16.5")
        fill("M8.5 8a1.25 1.25 0 1 1 0 2.5a1.25 1.25 0 1 1 0-2.5z")
    }

    /** doc.on.clipboard (粘贴) */
    val clipboard = sf("doc.on.clipboard") {
        line(
            "M9 4.75H15A1 1 0 0 1 16 5.75V6.75H8V5.75A1 1 0 0 1 9 4.75ZM10.5 3.25H13.5" +
                "M8 5.5H6.5A1.75 1.75 0 0 0 4.75 7.25V18.75A1.75 1.75 0 0 0 6.5 20.5H17.5A1.75 1.75 0 0 0 19.25 18.75V7.25A1.75 1.75 0 0 0 17.5 5.5H16"
        )
    }

    /** line.3.horizontal (拖拽排序手柄) */
    val dragHandle = sf("line.3.horizontal") {
        line("M4.75 7.25H19.25M4.75 12H19.25M4.75 16.75H19.25", width = 1.75f)
    }

    /** plus.circle.fill (添加底部入口) */
    val plusCircleFill = sf("plus.circle.fill") {
        fill(
            "M12 2.75A9.25 9.25 0 1 0 12 21.25A9.25 9.25 0 1 0 12 2.75Z" +
                "M11.2 7.5H12.8V11.2H16.5V12.8H12.8V16.5H11.2V12.8H7.5V11.2H11.2V7.5Z"
        )
    }

    /** minus.circle.fill (隐藏底部入口) */
    val minusCircleFill = sf("minus.circle.fill") {
        fill(
            "M12 2.75A9.25 9.25 0 1 0 12 21.25A9.25 9.25 0 1 0 12 2.75Z" +
                "M7.5 11.2H16.5V12.8H7.5V11.2Z"
        )
    }

    /** dock.rectangle (底部导航设置) */
    val bottomNav = sf("dock.rectangle") {
        line(
            "M4.75 4.75H19.25A1.75 1.75 0 0 1 21 6.5V17.5A1.75 1.75 0 0 1 19.25 19.25H4.75A1.75 1.75 0 0 1 3 17.5V6.5A1.75 1.75 0 0 1 4.75 4.75Z" +
                "M3.5 14.75H20.5"
        )
    }

    // MARK: Vector Builder

    private fun sf(name: String, build: ImageVector.Builder.() -> Unit): ImageVector =
        ImageVector.Builder(name, 24.dp, 24.dp, 24f, 24f).apply(build).build()

    private fun ImageVector.Builder.line(d: String, width: Float = 1.65f) {
        addPath(
            addPathNodes(d),
            fill = null,
            stroke = SolidColor(Color.Black),
            strokeLineWidth = width,
            strokeLineCap = StrokeCap.Round,
            strokeLineJoin = StrokeJoin.Round,
        )
    }

    private fun ImageVector.Builder.fill(d: String) {
        addPath(addPathNodes(d), pathFillType = PathFillType.EvenOdd, fill = SolidColor(Color.Black))
    }
}
