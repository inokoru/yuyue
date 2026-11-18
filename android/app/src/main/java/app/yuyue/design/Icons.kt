package app.yuyue.design

import androidx.compose.material.icons.Icons.AutoMirrored
import androidx.compose.material.icons.Icons.Rounded
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.ArrowForward
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.rounded.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.rounded.List
import androidx.compose.material.icons.automirrored.rounded.MenuBook
import androidx.compose.material.icons.automirrored.rounded.OpenInNew
import androidx.compose.material.icons.automirrored.rounded.Redo
import androidx.compose.material.icons.automirrored.rounded.Undo
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Repeat
import androidx.compose.material.icons.rounded.GpsFixed
import androidx.compose.material.icons.rounded.Chat
import androidx.compose.material.icons.rounded.AutoStories
import androidx.compose.material.icons.rounded.Bookmark
import androidx.compose.material.icons.rounded.BookmarkBorder
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.CloudDownload
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.Folder
import androidx.compose.material.icons.rounded.GraphicEq
import androidx.compose.material.icons.rounded.GridView
import androidx.compose.material.icons.rounded.Headphones
import androidx.compose.material.icons.rounded.Inbox
import androidx.compose.material.icons.rounded.Info
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.ArrowDownward
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.KeyboardArrowUp
import androidx.compose.material.icons.rounded.LibraryBooks
import androidx.compose.material.icons.rounded.LightMode
import androidx.compose.material.icons.rounded.MoreHoriz
import androidx.compose.material.icons.rounded.Pause
import androidx.compose.material.icons.rounded.Person
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.RadioButtonUnchecked
import androidx.compose.material.icons.rounded.Search
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.Share
import androidx.compose.material.icons.rounded.SkipNext
import androidx.compose.material.icons.rounded.SkipPrevious
import androidx.compose.material.icons.rounded.Storage
import androidx.compose.material.icons.rounded.SwapVert
import androidx.compose.material.icons.rounded.TextFields
import androidx.compose.material.icons.rounded.Translate
import androidx.compose.material.icons.rounded.Wifi
import androidx.compose.material.icons.rounded.History
import androidx.compose.material.icons.rounded.PhoneAndroid
import androidx.compose.material.icons.rounded.PushPin
import androidx.compose.material.icons.rounded.Language
import androidx.compose.material.icons.rounded.Lock
import androidx.compose.material.icons.rounded.CloudOff
import androidx.compose.material.icons.rounded.PhoneIphone
import androidx.compose.material.icons.rounded.LaptopMac
import androidx.compose.material.icons.rounded.Done
import androidx.compose.material.icons.rounded.PriorityHigh
import androidx.compose.material.icons.rounded.FormatQuote
import androidx.compose.material.icons.rounded.ArrowUpward
import androidx.compose.material.icons.rounded.Tune
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.BorderColor
import androidx.compose.material.icons.rounded.CollectionsBookmark
import androidx.compose.material.icons.rounded.Dns
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.Inventory2
import androidx.compose.material.icons.rounded.Sync
import androidx.compose.material.icons.rounded.Upload
import androidx.compose.material.icons.rounded.Bedtime
import androidx.compose.material.icons.rounded.Forward30
import androidx.compose.material.icons.rounded.Replay

/** The iPhone's SF Symbols, by what they stand for, as Material's rounded icons. */
object Icons {
    val add = Rounded.Add
    /** 反馈: a speech bubble with lines, as text.bubble on iPhone and Mac. */
    val feedback = Rounded.Chat
    /** 外观: the sun, as on Mac and iPhone (sun.max). */
    val appearance = Rounded.LightMode
    val back = AutoMirrored.Rounded.ArrowBack
    val forward = AutoMirrored.Rounded.ArrowForward
    val book = AutoMirrored.Rounded.MenuBook
    val books = Rounded.LibraryBooks
    val bookmark = Rounded.Bookmark
    val bookmarkOutline = Rounded.BookmarkBorder
    val check = Rounded.Check
    val checkCircle = Rounded.CheckCircle
    val circle = Rounded.RadioButtonUnchecked
    val chevronRight = AutoMirrored.Rounded.KeyboardArrowRight
    val chevronLeft = AutoMirrored.Rounded.KeyboardArrowLeft
    val chevronDown = Rounded.KeyboardArrowDown
    val arrowDown = Rounded.ArrowDownward
    val chevronUp = Rounded.KeyboardArrowUp
    val close = Rounded.Close
    val cloudDownload = Rounded.CloudDownload
    val collection = Rounded.CollectionsBookmark
    val companion = Rounded.AutoAwesome
    val delete = Rounded.Delete
    val edit = Rounded.Edit
    val error = Rounded.ErrorOutline
    val folder = Rounded.Folder
    val grid = Rounded.GridView
    val headphones = Rounded.Headphones
    val repeat = Rounded.Repeat
    val target = Rounded.GpsFixed
    val highlighter = Rounded.BorderColor
    val inbox = Rounded.Inbox
    val info = Rounded.Info
    val list = AutoMirrored.Rounded.List
    val more = Rounded.MoreHoriz
    val pause = Rounded.Pause
    val play = Rounded.PlayArrow
    val previous = Rounded.SkipPrevious
    val next = Rounded.SkipNext
    val read = Rounded.AutoStories
    val search = Rounded.Search
    val server = Rounded.Dns
    val settings = Rounded.Settings
    val person = Rounded.Person
    val share = Rounded.Share
    val snapshot = Rounded.Inventory2
    val sort = Rounded.SwapVert
    val storage = Rounded.Storage
    val sync = Rounded.Sync
    val textSize = Rounded.TextFields
    val translate = Rounded.Translate
    val undo = AutoMirrored.Rounded.Undo
    val redo = AutoMirrored.Rounded.Redo
    val upload = Rounded.Upload
    val waveform = Rounded.GraphicEq
    val wifi = Rounded.Wifi
    /** Still to download (arrow.down.to.line). */
    val download = Rounded.Download
    /** 有声书: −15 s and +30 s (gobackward.15, goforward.30); Material has no 15, so the plain turn back. */
    val back15 = Rounded.Replay
    val ahead30 = Rounded.Forward30
    /** 定时 (moon.zzz). */
    val sleep = Rounded.Bedtime
    /** 通用 (slider.horizontal.3) and 最近改过 (clock.arrow.circlepath). */
    val general = Rounded.Tune
    val history = Rounded.History
    /** 这台手机, as a voice source. */
    val phone = Rounded.PhoneAndroid
    /** Opens a page outside 语阅 (arrow.up.forward). */
    val external = AutoMirrored.Rounded.OpenInNew
    /** 念你的中文书 (pin.fill), the ready lines (“), and send (↑). */
    val pin = Rounded.PushPin
    val quote = Rounded.FormatQuote
    val send = Rounded.ArrowUpward
    /** 同步: Google Drive (globe), the lock under it, off (icloud.slash), the devices, the state. */
    val globe = Rounded.Language
    val lock = Rounded.Lock
    val cloudOff = Rounded.CloudOff
    val iphone = Rounded.PhoneIphone
    val laptop = Rounded.LaptopMac
    val done = Rounded.Done
    val bang = Rounded.PriorityHigh
}
