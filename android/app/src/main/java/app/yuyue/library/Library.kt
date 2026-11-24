package app.yuyue.library

import android.content.Context
import android.net.Uri
import android.os.Build
import android.provider.OpenableColumns
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.yuyue.t
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.MainScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.BookCollection
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.BookSummary
import uniffi.yuyue_core.CoreException
import uniffi.yuyue_core.Mark
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.YuyueCore
import java.io.File
import java.util.UUID

/** The app's handle on the core (one per process) and the shelf it shows, as on iPhone. */
class Library(private val context: Context) {
    val dataDir: File = File(context.filesDir, "Yuyue").apply { mkdirs() }
    val core: YuyueCore = YuyueCore.open(dataDir.path, deviceName(context), "android")
    val scope: CoroutineScope = MainScope()
    /** 导出's sending, which outlives the menu that starts it. */
    val noteSender by lazy { app.yuyue.notes.NoteSender(context, this) }

    var books by mutableStateOf<List<BookSummary>>(emptyList())
        private set
    var collections by mutableStateOf<List<BookCollection>>(emptyList())
        private set
    var importing by mutableIntStateOf(0)
        private set
    /** 出了点问题: shown once, over every tab. */
    var alert by mutableStateOf<String?>(null)
    /** The note at the bottom after deleting or importing, with its undo. */
    var toast by mutableStateOf<Toast?>(null)
        private set
    /** The book open over the tabs; opening one puts it here. */
    var presented by mutableStateOf<String?>(null)
    /** The book being read aloud, in whichever screen. */
    var listening by mutableStateOf<String?>(null)
    /** A book still being read aloud after its pages were closed (a ReaderModel): the 播放条
     *  over the tabs plays and pauses it, and opening the book again picks it up as it is. */
    var nowPlaying by mutableStateOf<Any?>(null)
    private val intents = mutableMapOf<String, OpenIntent>()
    val sync = app.yuyue.sync.Sync(context, core, this)
    /** 远程书库 filled in under 书库来源. */
    val sources = RemoteSources()
    /** 听读书: 有声书 laid against their e-books. */
    val recordings by lazy { app.yuyue.speech.Recordings(context, this) }
    /** 边下边听: recordings coming from a share, going on where they were. */
    val shareStream by lazy { app.yuyue.speech.ShareStream(context, this).also { it.resume() } }
    /** 局域网传录音: Macs nearby offering recordings. */
    val recordingFetch by lazy { app.yuyue.speech.RecordingFetch(context, this).also { it.browse() } }
    /** 广播剧's clips, made in the background. */
    val dramaSynth by lazy { app.yuyue.speech.DramaSynth(context, this) }
    /** 播客与书房电台: initialized lazily when first visited or synced so cold start stays instant. */
    private var _podcastHub: app.yuyue.podcast.PodcastHub? = null
    val hasPodcastHub: Boolean get() = _podcastHub != null
    val podcastHub: app.yuyue.podcast.PodcastHub
        get() = _podcastHub ?: app.yuyue.podcast.PodcastHub(context, this).also { _podcastHub = it }
    /** 监听一个文件夹. */
    val watched = WatchedFolder(context)
    /** A 数据快照 opened from another app, waiting to be looked at before it's brought back. */
    var openedSnapshot by mutableStateOf<Pair<File, uniffi.yuyue_core.SnapshotInfo>?>(null)
    /** Where a book stands chapter by chapter, worked out once per position. */
    private val chapterCache = mutableMapOf<String, Pair<Double, List<ChapterRow>>>()
    /** Bumped when highlights change outside the book, so 笔记 loads again. */
    var notesVersion by mutableIntStateOf(0)
        private set
    private var toastJob: Job? = null
    var pendingRecording by mutableStateOf<PendingRecording?>(null)

    init {
        app.yuyue.speech.ClonedVoices.init(context)
        app.yuyue.speech.KokoroModel.init(context, core, dataDir)
        app.yuyue.speech.LiveVoiceModel.init(context, core, dataDir)
        reload()
        // Deleted books can be put back for 30 days; after that their files go off the critical startup path.
        scope.launch(Dispatchers.IO) {
            delay(3000)
            runCatching { core.purgeDeleted(30L * 24 * 3600 * 1000) }
        }
    }
    private val initCompleted = true

    fun reload() {
        try {
            books = core.books()
            collections = core.collections()
            app.yuyue.reader.Papers.load(core)
            _podcastHub?.reload(eagerTranscript = false)
        } catch (e: Exception) {
            alert = message(e)
        }
    }

    // MARK: Opening

    sealed interface OpenIntent {
        data object Listen : OpenIntent
        data object ListenFromStart : OpenIntent
        /** From 笔记 or 搜索: open at this place, optionally highlighting a search hit. */
        data class At(val position: Position, val query: String? = null, val hit: uniffi.yuyue_core.SearchHit? = null) : OpenIntent
        /** From 搜索 · 问: open with 书伴 and this question. */
        data class Ask(val question: String) : OpenIntent
    }

    fun open(book: BookSummary, intent: OpenIntent? = null) {
        if (!book.available) {
            note(Toast.Icon.Download, t("《%@》在别的设备上", book.title), t("把同一个文件导入这台手机，进度和笔记就接上了"))
            return
        }
        if (intent != null) intents[book.id] = intent else intents.remove(book.id)
        presented = book.id
    }

    fun open(bookID: String, at: Position, query: String? = null, hit: uniffi.yuyue_core.SearchHit? = null) {
        intents[bookID] = OpenIntent.At(at, query, hit)
        presented = bookID
    }

    fun ask(question: String, bookID: String) {
        intents[bookID] = OpenIntent.Ask(question)
        presented = bookID
    }

    fun takeIntent(bookID: String): OpenIntent? = intents.remove(bookID)

    // MARK: Organising

    fun setFinished(book: BookSummary, finished: Boolean) = run { core.setFinished(book.id, finished) }

    // 书的记录: 在读 · 想读 · 读过 · 搁置 · 弃读, a score, a line, tags, and how far a paper book has got.

    /** The books on the shelf: files here, not ones kept only as a record. */
    val shelfBooks: List<BookSummary> get() = books.filter { it.format != uniffi.yuyue_core.BookFormat.PODCAST && !it.record }
    /** 进度's: every book kept, files or record. */
    val keptBooks: List<BookSummary> get() = books.filter { it.format != uniffi.yuyue_core.BookFormat.PODCAST }

    fun book(id: String): BookSummary? = books.firstOrNull { it.id == id }
    fun setStatus(book: BookSummary, status: uniffi.yuyue_core.ReadingStatus) = run { core.setStatus(book.id, status) }
    fun setRating(book: BookSummary, rating: Int?) = run { core.setRating(book.id, rating?.toUByte()) }
    fun setComment(book: BookSummary, comment: String) = run { core.setComment(book.id, comment.ifEmpty { null }) }
    fun setTag(book: BookSummary, tag: String, on: Boolean) = run { core.setTag(book.id, tag, on) }
    /** 加一本书: only the title is needed. */
    fun addRecordBook(title: String, status: uniffi.yuyue_core.ReadingStatus) = run { core.addRecordBook(title, emptyList(), status) }
    fun setManualProgress(book: BookSummary, unit: uniffi.yuyue_core.ProgressUnit, value: Int, total: Int) = run { core.setManualProgress(book.id, unit, value.toUInt(), total.toUInt()) }
    /** 只删文件，留下记录. */
    fun keepRecordOnly(book: BookSummary) = run { core.keepRecordOnly(book.id) }

    fun chapters(book: BookSummary): List<ChapterRow> {
        if (!book.available || book.record || book.format == uniffi.yuyue_core.BookFormat.AUDIO || book.format == uniffi.yuyue_core.BookFormat.PODCAST) return emptyList()
        chapterCache[book.id]?.let { (at, rows) -> if (at == book.progress) return rows }
        val detail = runCatching { core.book(book.id) }.getOrNull() ?: return emptyList()
        val rows = Chapters.rows(detail, runCatching { core.latestProgress(book.id) }.getOrNull()?.position)
        chapterCache[book.id] = book.progress to rows
        return rows
    }

    /** How long it has been read and listened to, in minutes. */
    fun timeSpent(book: BookSummary): Pair<Int, Int> {
        val times = runCatching { core.sectionTimes(book.id) }.getOrDefault(emptyList())
        return times.sumOf { it.readSeconds.toInt() } / 60 to times.sumOf { it.listenSeconds.toInt() } / 60
    }

    fun setBook(book: String, collection: String, inside: Boolean) = run { core.setInCollection(collection, book, inside) }

    fun createCollection(name: String, book: String? = null): String? {
        var id: String? = null
        run { id = core.createCollection(name, CollectionIcon.guess(name), book).id }
        return id
    }

    fun renameCollection(id: String, name: String) = run { core.renameCollection(id, name) }
    /** 换图标: null goes back to the icon its name brings. */
    fun setCollectionIcon(id: String, icon: String?) = run { core.setCollectionIcon(id, icon) }

    fun deleteCollection(c: BookCollection) {
        run { core.deleteCollection(c.id) }
        show(Toast(Toast.Icon.Delete, t("已删除合集「%@」", c.name), t("里面的书都还在"), danger = true) { run { core.restoreCollection(c.id) } })
    }

    fun reorderCollections(ids: List<String>) = run { core.reorderCollections(ids) }

    fun editInfo(book: BookSummary, title: String, authors: List<String>) = run { core.editBookInfo(book.id, title, authors) }

    fun setCover(book: BookSummary, jpeg: ByteArray) = run { core.setCover(book.id, jpeg) }

    /** Deletes straight away; the note at the bottom can put it back. */
    fun delete(book: BookSummary) {
        run { core.deleteBook(book.id) }
        show(Toast(Toast.Icon.Delete, t("已删除《%@》", book.title), t("笔记和进度都留着"), danger = true) { run { core.restoreBook(book.id) } })
    }

    /** Deletes a selection in one batch; Undo restores every deleted book. */
    fun deleteAll(books: List<BookSummary>) {
        if (books.isEmpty()) return
        if (books.size == 1) { delete(books[0]); return }
        val ids = books.map { it.id }
        run { for (id in ids) core.deleteBook(id) }
        show(Toast(Toast.Icon.Delete, t("已删除 %lld 本书", books.size), t("笔记和进度都留着"), danger = true) {
            run { for (id in ids) core.restoreBook(id) }
        })
    }

    /** Deletes highlights outside the book (笔记), with 撤销. Each is (book, mark). */
    fun deleteMarks(marks: List<Pair<String, Mark>>) {
        if (marks.isEmpty()) return
        try {
            for ((book, mark) in marks) core.deleteMark(book, mark.id)
        } catch (e: Exception) {
            alert = message(e)
        }
        notesChanged()
        val one = marks.singleOrNull()?.second
        val detail = one?.let { if (it.note == null) "“${it.text.take(16)}”" else t("想法也一起删了") } ?: t("%lld 条划线和它们的想法", marks.size)
        show(Toast(Toast.Icon.Highlight, if (one == null) t("已删除 %lld 条", marks.size) else t("已删除划线"), detail, danger = true) {
            for ((book, mark) in marks) runCatching { core.restoreMark(book, mark.id) }
            notesChanged()
        })
    }

    fun setNote(note: String?, book: String, mark: Mark) {
        try {
            core.setMarkNote(book, mark.id, note)
        } catch (e: Exception) {
            alert = message(e)
        }
        notesChanged()
    }

    fun notesChanged() {
        notesVersion++
    }

    /** Takes a word out of the 生词本, with 撤销. */
    fun deleteWord(word: String) {
        try {
            core.deleteWord(word)
        } catch (e: Exception) {
            alert = message(e)
            return
        }
        notesChanged()
        show(Toast(Toast.Icon.Delete, t("已删除“%@”", word), t("从生词本里拿掉了"), danger = true) {
            runCatching { core.restoreWord(word) }
            notesChanged()
        })
    }

    private inline fun run(change: () -> Unit) {
        try {
            change()
        } catch (e: Exception) {
            alert = message(e)
        }
        reload()
    }

    // MARK: The note at the bottom

    data class Toast(
        val icon: Icon,
        val title: String,
        val detail: String,
        val danger: Boolean = false,
        val id: String = UUID.randomUUID().toString(),
        val undo: (() -> Unit)? = null,
    ) {
        enum class Icon { Delete, Highlight, Add, Folder, Download, Check, Share, Error }

        /** Until it goes: a banner counts its seconds, shows the tick, then rises away. */
        val lifetime: Long
            get() = when {
                undo != null -> UNDO_MS + UNDO_SETTLE_MS + 400
                detail.isNotBlank() -> TOAST_DETAIL_MS
                else -> TOAST_MS
            }
    }

    fun note(icon: Toast.Icon, title: String, detail: String) = show(Toast(icon, title, detail))

    fun show(t: Toast) {
        toast = t
        toastJob?.cancel()
        toastJob = scope.launch {
            delay(t.lifetime)
            if (toast?.id == t.id) toast = null
        }
    }

    fun undo() {
        val t = toast ?: return
        toast = null
        t.undo?.invoke()
    }

    // MARK: Importing

    /** Imports the files the system picker or another app handed over, into the collection when given. */
    fun importUris(uris: List<Uri>, collection: String? = null, folder: String? = null) {
        if (uris.isEmpty()) return
        importUrisJob(uris, collection, folder)
    }

    internal fun importUrisJob(
        uris: List<Uri>,
        collection: String? = null,
        folder: String? = null,
        onHandled: ((Uri) -> Unit)? = null,
    ): Job = scope.launch {
        importing++
        val failures = mutableListOf<String>()
        val imported = mutableListOf<BookSummary>()
        var skippedExisting = false
        // Sound files picked together are one 有声书; an M4B is one on its own.
        val named = uris.map { it to displayName(it) }
        val (sounds, files) = named.partition { AudioImporter.isAudio(it.second) }
        val (m4b, loose) = sounds.partition { it.second.endsWith(".m4b", ignoreCase = true) }
        val zippedRecordings = mutableListOf<PendingRecording>()
        try {
            for ((uri, name) in files) {
                // A zip of sound files is a recording, brought in as a folder would be.
                if (name.endsWith(".zip", ignoreCase = true)) {
                    try {
                        val unzipped = withContext(Dispatchers.IO) { unzipRecording(uri, name) }
                        if (unzipped != null) {
                            zippedRecordings += unzipped
                            onHandled?.invoke(uri)
                            continue
                        }
                    } catch (e: kotlinx.coroutines.CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        failures += "$name：${message(e)}"
                        continue
                    }
                }
                if (name.endsWith(".yuyue", ignoreCase = true)) {
                    try {
                        val snapshot = withContext(Dispatchers.IO) { app.yuyue.me.copyIn(uri)?.let { it to core.readSnapshot(it.path) } }
                        openedSnapshot = snapshot
                        if (snapshot != null) onHandled?.invoke(uri)
                    } catch (e: kotlinx.coroutines.CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        failures += "$name：${message(e)}"
                    }
                    continue
                }
                val had = books.filter { it.available }.map { it.id }.toSet()
                try {
                    val book = withContext(Dispatchers.IO) { importOne(uri, name) }
                    if (book.id !in had) {
                        imported += book
                        if (collection != null) runCatching { core.setInCollection(collection, book.id, true) }
                    } else {
                        skippedExisting = true
                    }
                    onHandled?.invoke(uri)
                } catch (e: kotlinx.coroutines.CancellationException) {
                    throw e
                } catch (e: Exception) {
                    failures += "$name：${message(e)}"
                }
                reload()
            }
        } finally {
            importing--
        }
        when {
            failures.isNotEmpty() -> alert = failures.joinToString("\n")
            imported.isNotEmpty() -> {
                val first = imported[0]
                val title = when {
                    folder != null -> t("导入了 %lld 本", imported.size)
                    imported.size == 1 -> t("已导入《%@》", first.title)
                    else -> t("已导入 %lld 本书", imported.size)
                }
                show(Toast(if (folder != null) Toast.Icon.Folder else Toast.Icon.Add, title, folder?.let { t("来自“%@”文件夹", it) } ?: t("在书架最前面")) {
                    for (b in imported) runCatching { core.deleteBook(b.id) }
                    reload()
                })
            }
            skippedExisting -> note(Toast.Icon.Check, t("书库中已存在此书，已自动跳过"), "")
        }
        // Recordings after the books: their sheet waits on the reader, and the books
        // shouldn't wait on it.
        val recordingFailures = mutableListOf<String>()
        for (recording in m4b.map { listOf(it) } + listOfNotNull(loose.takeIf { it.isNotEmpty() })) {
            importing++
            val pending = try {
                withContext(Dispatchers.IO) { readRecording(recording) }
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                recordingFailures += "${recording.first().second}：${message(e)}"
                null
            } finally {
                importing--
            }
            if (pending != null) {
                // Several brought in at once: one sheet after another.
                while (pendingRecording != null) delay(200)
                pendingRecording = pending
            }
        }
        for (pending in zippedRecordings) {
            while (pendingRecording != null) delay(200)
            pendingRecording = pending
        }
        if (recordingFailures.isNotEmpty()) alert = (listOfNotNull(alert) + recordingFailures).joinToString("\n")
    }

    /** A recording read and waiting on 导入有声书: its name, author, cover
     *  and the order of its files can be changed before it goes onto the shelf. Its files sit in
     *  `scratch` till then. */
    data class PendingRecording(val audio: uniffi.yuyue_core.AudioImport, val scratch: File, val names: List<String>)

    /** A recording's files copied next to the library and read. */
    private fun readRecording(files: List<Pair<Uri, String>>): PendingRecording {
        val tmp = File(context.cacheDir, "import-${UUID.randomUUID()}").apply { mkdirs() }
        try {
            val copied = files.map { (uri, name) ->
                File(tmp, name.replace('/', '-')).also { f ->
                    context.contentResolver.openInputStream(uri)?.use { input -> f.outputStream().use { input.copyTo(it) } } ?: throw CoreException.NotFound(name)
                }
            }
            val audio = AudioImporter.read(core, copied, null)
            return PendingRecording(audio, tmp, audio.files.map { File(it.path).name })
        } catch (e: Exception) {
            tmp.deleteRecursively()
            throw e
        }
    }

    /** A zip's sound files taken out and read as one recording named for the zip; null when it
     *  holds none (it's a book, or something else). */
    private fun unzipRecording(uri: Uri, name: String): PendingRecording? {
        val tmp = File(context.cacheDir, "import-${UUID.randomUUID()}").apply { mkdirs() }
        try {
            val zip = File(tmp, "in.zip")
            context.contentResolver.openInputStream(uri)?.use { input -> zip.outputStream().use { input.copyTo(it) } } ?: return null.also { tmp.deleteRecursively() }
            val folder = name.substringBeforeLast('.')
            val files = core.audiobookUnzip(zip.path, File(tmp, folder.replace('/', '-')).path).map(::File)
            zip.delete()
            if (files.isEmpty()) {
                tmp.deleteRecursively()
                return null
            }
            val audio = AudioImporter.read(core, files, folder)
            return PendingRecording(audio, tmp, audio.files.map { File(it.path).name })
        } catch (e: Exception) {
            tmp.deleteRecursively()
            throw e
        }
    }

    /** 完成 on 导入有声书: onto the shelf as a book of its own. */
    fun finishRecording(p: PendingRecording) {
        pendingRecording = null
        scope.launch {
            val had = books.filter { it.available }.map { it.id }.toSet()
            try {
                val book = withContext(Dispatchers.IO) { core.importAudiobook(p.audio) }
                reload()
                if (book.id !in had) {
                    show(Toast(Toast.Icon.Add, t("已导入《%@》", book.title), t("在书架最前面")) {
                        runCatching { core.deleteBook(book.id) }
                        reload()
                    })
                }
            } catch (e: Exception) {
                alert = message(e)
            } finally {
                withContext(Dispatchers.IO) { p.scratch.deleteRecursively() }
            }
        }
    }

    fun cancelRecording() {
        pendingRecording?.scratch?.deleteRecursively()
        pendingRecording = null
    }

    /** Copies the picked file next to the library (the core reads paths) and imports it. */
    private fun importOne(uri: Uri, name: String): BookSummary {
        val tmp = File(context.cacheDir, "import-${UUID.randomUUID()}").apply { mkdirs() }
        val file = File(tmp, name.replace('/', '-'))
        try {
            context.contentResolver.openInputStream(uri)?.use { input -> file.outputStream().use { input.copyTo(it) } }
                ?: throw CoreException.NotFound(name)
            return importPath(file.path)
        } finally {
            tmp.deleteRecursively()
        }
    }

    /** A file already on disk (Wi‑Fi 传书, 远程书库, a watched folder): into the library, or why not. */
    fun importPath(path: String, title: String? = null, author: String? = null, cover: ByteArray? = null): BookSummary {
        val file = File(path)
        val stem = file.nameWithoutExtension
        val cleanTitle = title?.trim()?.takeIf { it.isNotEmpty() }
        val cleanAuthors = author?.split('、', ',', '，', ';', '；', '/')
            ?.map { it.trim() }
            ?.filter { it.isNotEmpty() }
            .orEmpty()
        if (AudioImporter.isAudio(file.name)) {
            var audio = AudioImporter.read(core, listOf(file), null)
            if (cleanTitle != null && (audio.title.isBlank() || audio.title == stem)) {
                audio = audio.copy(title = cleanTitle)
            }
            if (audio.authors.isEmpty() && cleanAuthors.isNotEmpty()) {
                audio = audio.copy(authors = cleanAuthors)
            }
            if (audio.cover == null && cover != null) {
                audio = audio.copy(cover = cover)
            }
            return core.importAudiobook(audio)
        }
        var book = core.importBook(path)
        if (book.format == BookFormat.PDF) {
            if (book.readMinutes != 0u) return book
            // The core keeps a PDF as it is; the system's renderer says what is in it.
            return try {
                var info = PdfInfoReader.read(file)
                if (info.title.isNullOrBlank() && cleanTitle != null) info = info.copy(title = cleanTitle)
                if (info.authors.isEmpty() && cleanAuthors.isNotEmpty()) info = info.copy(authors = cleanAuthors)
                if (info.cover == null && cover != null) info = info.copy(cover = cover)
                core.setPdfInfo(book.id, info)
            } catch (e: Exception) {
                runCatching { core.deleteBook(book.id) }
                throw e
            }
        }
        val needsTitle = cleanTitle != null && (book.title.isBlank() || book.title == stem)
        val needsAuthors = cleanAuthors.isNotEmpty() && book.authors.isEmpty()
        if (needsTitle || needsAuthors) {
            val nextTitle = if (needsTitle) cleanTitle else book.title
            val nextAuthors = if (needsAuthors) cleanAuthors else book.authors
            runCatching { core.editBookInfo(book.id, nextTitle, nextAuthors) }.getOrNull()?.let { book = it }
        }
        return book
    }

    private fun displayName(uri: Uri): String {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0)?.takeIf { it.isNotBlank() }?.let { return it }
        }
        return uri.lastPathSegment?.substringAfterLast('/')?.takeIf { it.isNotBlank() } ?: "book"
    }

    fun originalFile(book: BookSummary): File? =
        File(dataDir, "books/${book.id}").listFiles()?.firstOrNull { it.name.startsWith("original.") }

    companion object {
        /** How long a plain note stays. */
        const val TOAST_MS = 3000L
        /** How long a two-line note with guidance stays so it can be read. */
        const val TOAST_DETAIL_MS = 4200L
        /** The seconds a deletion can be undone, counted down on its banner. */
        const val UNDO_MS = 5000L
        /** How long the red tick shows after the count before the banner rises away. */
        const val UNDO_SETTLE_MS = 900L

        fun message(e: Throwable): String = when (e) {
            is PdfInfoReader.Locked -> t("这个 PDF 设了密码，打不开")
            is PdfInfoReader.Unreadable -> t("这个 PDF 读不出来")
            is CoreException.Unsupported -> t("不支持这种文件，目前能打开 EPUB、PDF、MOBI、AZW3 和 TXT")
            is CoreException.Protected -> t("这本书有版权保护（DRM），打不开")
            is CoreException.BadBook -> when {
                e.detail == "empty" -> t("这个文件是空的，里面没有内容")
                e.detail.contains("password", ignoreCase = true) -> t("这个压缩包设了密码，打不开")
                e.detail.contains("too large") -> t("这本书太大了，读不进来")
                else -> t("这本书读不出来，文件可能坏了，或者不是它看起来的格式")
            }
            is CoreException.NotFound -> t("找不到这本书的文件，可能被移走或删掉了。从书架上删掉，再导入一次就好")
            is CoreException.Io -> when {
                e.detail.contains("os error 28)") -> t("磁盘满了，腾点地方再导入")
                else -> t("出错了（%@）", e.detail)
            }
            is CoreException.Db -> t("出错了（%@）", e.detail)
            is CoreException.Invalid -> t("出错了（%@）", e.detail)
            is java.net.UnknownHostException -> t("没有联网，连上网再试")
            is java.net.SocketTimeoutException, is java.net.SocketException -> t("网断了，下载停在半路。连好网再点一次，会重新下")
            is java.io.IOException -> when {
                outOfSpace(e) -> t("存储空间不够了，腾出一些地方再试")
                else -> t("读写文件时出了问题，再试一次")
            }
            else -> e.localizedMessage ?: e.toString()
        }

        /** The disk is full, whichever layer said so. */
        fun outOfSpace(e: Throwable): Boolean =
            generateSequence(e) { it.cause }.any { it.message?.contains("ENOSPC") == true || it.message?.contains("No space") == true }

        fun deviceName(context: Context): String {
            fun isGeneric(s: String) = s.isEmpty() ||
                s.startsWith("sdk_", ignoreCase = true) ||
                s.startsWith("generic", ignoreCase = true) ||
                s.contains("emulator", ignoreCase = true) ||
                s.startsWith("Android SDK", ignoreCase = true)
            val named = Settings.Global.getString(context.contentResolver, Settings.Global.DEVICE_NAME)?.trim()
            if (named != null && !isGeneric(named)) return named
            val model = Build.MODEL.orEmpty().trim()
            val mfr = Build.MANUFACTURER.orEmpty().trim()
            val combined = when {
                model.isEmpty() -> mfr
                mfr.isEmpty() || model.startsWith(mfr, ignoreCase = true) -> model
                else -> "$mfr $model"
            }
            return if (!isGeneric(combined)) combined else "Android"
        }
    }
}
