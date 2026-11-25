package app.yuyue.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Assume.assumeTrue
import org.junit.Test
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.ConflictChoice
import uniffi.yuyue_core.CustomPaper
import uniffi.yuyue_core.Font
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.MarkStyle
import uniffi.yuyue_core.PageGeometry
import uniffi.yuyue_core.Paging
import uniffi.yuyue_core.Paper
import uniffi.yuyue_core.PaperTexture
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.ReadingStatus
import uniffi.yuyue_core.ReplaceRule
import uniffi.yuyue_core.SnapshotParts
import uniffi.yuyue_core.SyncPart
import uniffi.yuyue_core.TextPoint
import uniffi.yuyue_core.YuyueCore
import uniffi.yuyue_core.compareVersions
import uniffi.yuyue_core.pageSentences
import java.io.File
import java.nio.file.Files
import kotlin.math.abs
import kotlin.math.sign

/**
 * Layer 2: Shared UniFFI conformance runner (Android / Kotlin side).
 *
 * Runs the exact same JSON scenarios under `fixtures/conformance/` as Rust, C#, and Swift.
 * Requires host `libyuyue_core.{so,dylib,dll}` on `jna.library.path` (built via
 * `cargo build -p yuyue-core --lib` in CI); skips gracefully if absent during plain local unit runs.
 */
class ConformanceTest {

    private fun repoRoot(): File {
        var dir: File? = File(System.getProperty("user.dir")).canonicalFile
        while (dir != null) {
            if (File(dir, "fixtures/conformance").isDirectory && File(dir, "android").isDirectory) {
                return dir
            }
            dir = dir.parentFile
        }
        error("Could not locate repo root from ${System.getProperty("user.dir")}")
    }

    private fun nativeCoreAvailable(): Boolean = try {
        compareVersions("0.1.0", "0.1.0") == 0
    } catch (_: UnsatisfiedLinkError) {
        false
    } catch (_: NoClassDefFoundError) {
        false
    }

    @Test
    fun sharedConformanceScenariosPass() {
        assumeTrue(
            "Host libyuyue_core not on jna.library.path; pass -Djna.library.path=<core/target/debug>",
            nativeCoreAvailable(),
        )
        val root = repoRoot()
        val dir = File(root, "fixtures/conformance")
        val files = dir.listFiles { f -> f.isFile && f.extension == "json" && f.name != "reader-messages.json" }
            ?.sortedBy { it.name }
            .orEmpty()
        assertTrue("No conformance scenarios found in ${dir.path}", files.isNotEmpty())
        for (file in files) {
            runScenario(root, file)
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun runScenario(root: File, file: File) {
        val scenario = parseJson(file.readText(Charsets.UTF_8)) as Map<String, Any?>
        val id = scenario["id"] as String
        val steps = scenario["steps"] as List<Map<String, Any?>>

        val work = Files.createTempDirectory("yuyue-kt-conformance-$id-").toFile()
        val fixtures = File(root, "fixtures")
        val cores = LinkedHashMap<String, YuyueCore>()
        val vars = LinkedHashMap<String, String>()
        val geom = PageGeometry(Paging.NATIVE, 20.0, 50.0, 80.0, 1u, false)

        try {
            steps.forEachIndexed { idx, step ->
                val op = step["op"] as String
                val ctx = "$id[$idx] ($op)"
                when (op) {
                    "open" -> {
                        val dev = step["device"] as String
                        val dir = File(work, "dev-$dev").apply { mkdirs() }
                        cores.remove(dev)?.close()
                        cores[dev] = YuyueCore.open(dir.absolutePath, step["name"] as String, step["platform"] as String)
                    }
                    "close" -> cores.remove(step["device"] as String)?.close()
                    "import_book" -> {
                        val core = cores.getValue(step["device"] as String)
                        val rel = step["fixture"] as String
                        val full = File(fixtures, rel).absolutePath
                        if (step["expect_error"] == true) {
                            try {
                                core.importBook(full)
                                fail("$ctx: expected error importing $rel")
                            } catch (_: Exception) {
                                // expected
                            }
                            return@forEachIndexed
                        }
                        val book = core.importBook(full)
                        (step["save_as"] as? String)?.let { vars[it] = book.id }
                        val exp = step["expect"] as? Map<String, Any?>
                        if (exp != null) {
                            (exp["format"] as? String)?.let { assertEquals(ctx, parseFormat(it), book.format) }
                            (exp["available"] as? Boolean)?.let { assertEquals(ctx, it, book.available) }
                            (exp["same_id_as"] as? String)?.let { assertEquals(ctx, vars.getValue(it), book.id) }
                            (exp["title_contains"] as? String)?.let { assertTrue(ctx, book.title.contains(it)) }
                        }
                    }
                    "assert_books" -> {
                        val books = cores.getValue(step["device"] as String).books()
                        (step["count"] as? Number)?.let { assertEquals(ctx, it.toInt(), books.size) }
                        (step["first_id_ref"] as? String)?.let { assertEquals(ctx, vars.getValue(it), books[0].id) }
                    }
                    "assert_sentences" -> {
                        val core = cores.getValue(step["device"] as String)
                        val sents = core.sentences(vars.getValue(step["book"] as String), (step["section"] as Number).toInt().toUInt())
                        (step["min_count"] as? Number)?.let { assertTrue(ctx, sents.size >= it.toInt()) }
                        (step["any_contains"] as? String)?.let { sub -> assertTrue(ctx, sents.any { it.contains(sub) }) }
                    }
                    "save_progress" -> {
                        val core = cores.getValue(step["device"] as String)
                        core.saveProgress(
                            vars.getValue(step["book"] as String),
                            Position((step["section"] as Number).toInt().toUInt(), (step["sentence"] as Number).toInt().toUInt()),
                        )
                    }
                    "assert_progress" -> {
                        val core = cores.getValue(step["device"] as String)
                        val bookId = vars.getValue(step["book"] as String)
                        val got = if (step["kind"] == "own") core.progress(bookId) else core.latestProgress(bookId)
                        if (step["present"] as Boolean) assertNotNull(ctx, got) else assertNull(ctx, got)
                        if (got != null) {
                            (step["section"] as? Number)?.let { assertEquals(ctx, it.toInt().toUInt(), got.position.section) }
                            (step["sentence"] as? Number)?.let { assertEquals(ctx, it.toInt().toUInt(), got.position.sentence) }
                            (step["this_device"] as? Boolean)?.let { assertEquals(ctx, it, got.thisDevice) }
                            (step["min_fraction"] as? Number)?.let { assertTrue(ctx, got.fraction >= it.toDouble()) }
                        }
                    }
                    "set_reader_settings" -> {
                        val core = cores.getValue(step["device"] as String)
                        var s = core.readerSettings()
                        (step["paper"] as? String)?.let { s = s.copy(paper = parsePaper(it)) }
                        (step["mode"] as? String)?.let { s = s.copy(mode = parseMode(it)) }
                        (step["font"] as? String)?.let { s = s.copy(font = parseFont(it)) }
                        (step["font_size"] as? Number)?.let { s = s.copy(fontSize = it.toDouble()) }
                        (step["compress"] as? Boolean)?.let { s = s.copy(compress = it) }
                        core.setReaderSettings(s)
                    }
                    "assert_reader_settings" -> {
                        val s = cores.getValue(step["device"] as String).readerSettings()
                        (step["paper"] as? String)?.let { assertEquals(ctx, parsePaper(it), s.paper) }
                        (step["mode"] as? String)?.let { assertEquals(ctx, parseMode(it), s.mode) }
                        (step["font"] as? String)?.let { assertEquals(ctx, parseFont(it), s.font) }
                        (step["font_size"] as? Number)?.let { assertTrue(ctx, abs(s.fontSize - it.toDouble()) < 1e-6) }
                        (step["compress"] as? Boolean)?.let { assertEquals(ctx, it, s.compress) }
                    }
                    "set_book_reader_settings" -> {
                        val core = cores.getValue(step["device"] as String)
                        val bookId = vars.getValue(step["book"] as String)
                        if (step["clear"] == true) {
                            core.setBookReaderSettings(bookId, null)
                        } else {
                            var s = core.readerSettings()
                            (step["paper"] as? String)?.let { s = s.copy(paper = parsePaper(it)) }
                            (step["font_size"] as? Number)?.let { s = s.copy(fontSize = it.toDouble()) }
                            core.setBookReaderSettings(bookId, s)
                        }
                    }
                    "assert_section_document" -> {
                        val core = cores.getValue(step["device"] as String)
                        val html = core.sectionDocument(vars.getValue(step["book"] as String), (step["section"] as Number).toInt().toUInt(), geom)
                        for (needle in step["contains"] as List<String>) {
                            assertTrue("$ctx: missing $needle", html.contains(needle))
                        }
                    }
                    "add_mark" -> {
                        val core = cores.getValue(step["device"] as String)
                        val mark = core.addMark(
                            vars.getValue(step["book"] as String),
                            (step["section"] as Number).toInt().toUInt(),
                            TextPoint((step["start_sentence"] as Number).toInt().toUInt(), (step["start_offset"] as Number).toInt().toUInt()),
                            TextPoint((step["end_sentence"] as Number).toInt().toUInt(), (step["end_offset"] as Number).toInt().toUInt()),
                            step["text"] as String,
                            parseStyle(step["style"] as String),
                            step["note"] as? String,
                        )
                        (step["save_as"] as? String)?.let { vars[it] = mark.id }
                    }
                    "set_mark_note" -> {
                        val core = cores.getValue(step["device"] as String)
                        core.setMarkNote(vars.getValue(step["book"] as String), vars.getValue(step["mark"] as String), step["note"] as? String)
                    }
                    "set_mark_style" -> {
                        val core = cores.getValue(step["device"] as String)
                        core.setMarkStyle(vars.getValue(step["book"] as String), vars.getValue(step["mark"] as String), parseStyle(step["style"] as String))
                    }
                    "assert_marks" -> {
                        val marks = cores.getValue(step["device"] as String).marks(vars.getValue(step["book"] as String))
                        val count = (step["count"] as Number).toInt()
                        assertEquals(ctx, count, marks.size)
                        if (count > 0) {
                            (step["first_style"] as? String)?.let { assertEquals(ctx, parseStyle(it), marks[0].style) }
                            (step["first_note"] as? String)?.let { assertEquals(ctx, it, marks[0].note) }
                        }
                    }
                    "search_book" -> {
                        val hits = cores.getValue(step["device"] as String).searchBook(vars.getValue(step["book"] as String), step["query"] as String)
                        assertTrue(ctx, hits.size >= (step["min_hits"] as Number).toInt())
                    }
                    "delete_book" -> cores.getValue(step["device"] as String).deleteBook(vars.getValue(step["book"] as String))
                    "restore_book" -> cores.getValue(step["device"] as String).restoreBook(vars.getValue(step["book"] as String))
                    "sleep_ms" -> Thread.sleep((step["ms"] as Number).toLong())
                    "sync" -> {
                        val from = cores.getValue(step["from"] as String)
                        val to = cores.getValue(step["to"] as String)
                        val ver = if (step["full"] == true) "{}" else to.syncVersion()
                        val leftOut = (step["left_out"] as? List<String>).orEmpty().map(::parseSyncPart)
                        val batch = if (leftOut.isEmpty()) from.exportChanges(ver) else from.exportChangesExcept(ver, leftOut)
                        val changed = if (leftOut.isEmpty()) to.importChanges(batch) else to.importChangesExcept(batch, leftOut)
                        (step["min_changed"] as? Number)?.let { assertTrue(ctx, changed >= it.toInt().toUInt()) }
                        (step["exact_changed"] as? Number)?.let { assertEquals(ctx, it.toInt().toUInt(), changed) }
                    }
                    "assert_conflicts" -> {
                        val conflicts = cores.getValue(step["device"] as String).noteConflicts()
                        val count = (step["count"] as Number).toInt()
                        assertEquals(ctx, count, conflicts.size)
                        if (count > 0) {
                            (step["first_older_note"] as? String)?.let { assertEquals(ctx, it, conflicts[0].older.note) }
                            (step["first_newer_note"] as? String)?.let { assertEquals(ctx, it, conflicts[0].newer.note) }
                        }
                    }
                    "resolve_conflict" -> {
                        cores.getValue(step["device"] as String).resolveNoteConflict(
                            vars.getValue(step["book"] as String),
                            vars.getValue(step["mark"] as String),
                            parseChoice(step["choice"] as String),
                        )
                    }
                    "make_snapshot" -> {
                        val snap = File(work, step["snapshot_name"] as String).absolutePath
                        val bytes = cores.getValue(step["device"] as String).makeSnapshot(
                            snap,
                            SnapshotParts(
                                step["progress"] as Boolean,
                                step["notes"] as Boolean,
                                step["words"] as Boolean,
                                step["settings"] as Boolean,
                                step["files"] as Boolean,
                            ),
                        )
                        assertTrue(ctx, bytes > 0uL)
                    }
                    "restore_snapshot" -> {
                        val snap = File(work, step["snapshot_name"] as String).absolutePath
                        cores.getValue(step["device"] as String).restoreSnapshot(snap, step["replace"] as Boolean)
                    }
                    "assert_snapshot_counts" -> {
                        val counts = cores.getValue(step["device"] as String).snapshotCounts()
                        (step["min_books"] as? Number)?.let { assertTrue(ctx, counts.books >= it.toInt().toUInt()) }
                        (step["min_notes"] as? Number)?.let { assertTrue(ctx, counts.notes >= it.toInt().toUInt()) }
                    }
                    "assert_page_sentences" -> {
                        val got = pageSentences(step["text"] as String).map { it.text }
                        assertEquals(ctx, step["expected_texts"] as List<String>, got)
                    }
                    "assert_compare_versions" -> {
                        for (c in step["cases"] as List<Map<String, Any?>>) {
                            val got = compareVersions(c["a"] as String, c["b"] as String).sign
                            assertEquals(ctx, (c["sign"] as Number).toInt(), got)
                        }
                    }
                    "assert_review_today" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = (step["book"] as? String)?.let { vars.getValue(it) }
                        val deck = core.reviewToday(book, (step["day_start"] as Number).toLong(), (step["count"] as Number).toInt().toUInt())
                        (step["exact_items"] as? Number)?.let { assertEquals(ctx, it.toInt(), deck.items.size) }
                        (step["exact_done_today"] as? Number)?.let { assertEquals(ctx, it.toInt().toUInt(), deck.doneToday) }
                    }
                    "set_mark_review" -> {
                        cores.getValue(step["device"] as String).setMarkReview(
                            vars.getValue(step["book"] as String),
                            vars.getValue(step["mark"] as String),
                            step["keep"] as Boolean,
                        )
                    }
                    "add_time" -> {
                        cores.getValue(step["device"] as String).addTime(
                            vars.getValue(step["book"] as String),
                            (step["section"] as Number).toInt().toUInt(),
                            (step["read_seconds"] as Number).toInt().toUInt(),
                            (step["listen_seconds"] as Number).toInt().toUInt(),
                            step["day"] as String,
                        )
                    }
                    "assert_day_times" -> {
                        val rows = cores.getValue(step["device"] as String).dayTimes(step["from"] as String, step["to"] as String)
                        val totalR = rows.sumOf { it.readSeconds.toLong() }
                        val totalL = rows.sumOf { it.listenSeconds.toLong() }
                        (step["min_entries"] as? Number)?.let { assertTrue(ctx, rows.size >= it.toInt()) }
                        (step["min_read_seconds"] as? Number)?.let { assertTrue(ctx, totalR >= it.toLong()) }
                        (step["min_listen_seconds"] as? Number)?.let { assertTrue(ctx, totalL >= it.toLong()) }
                    }
                    "bookmark_undo_roundtrip" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val bm = core.addBookmark(book, (step["section"] as Number).toInt().toUInt(), (step["sentence"] as Number).toInt().toUInt())
                        core.deleteBookmark(book, bm.id)
                        assertEquals(ctx, 0, core.bookmarks(book).size)
                        core.restoreBookmark(book, bm.id)
                        assertEquals(ctx, 1, core.bookmarks(book).size)
                    }
                    "collection_undo_roundtrip" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val col = core.createCollection(step["name"] as String, null, book)
                        core.deleteCollection(col.id)
                        assertTrue(ctx, core.collections().none { it.id == col.id })
                        core.restoreCollection(col.id)
                        assertTrue(ctx, core.collections().any { it.id == col.id })
                    }
                    "custom_paper_undo_roundtrip" -> {
                        val core = cores.getValue(step["device"] as String)
                        val paper = core.saveCustomPaper(CustomPaper("", step["name"] as String, step["bg"] as String, null, Paper.KAMI, PaperTexture.NONE))
                        core.deleteCustomPaper(paper.id)
                        assertTrue(ctx, core.customPapers().none { it.id == paper.id })
                        core.restoreCustomPaper(paper.id)
                        assertTrue(ctx, core.customPapers().any { it.id == paper.id })
                    }
                    "word_undo_and_count" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val word = step["word"] as String
                        core.addWord(word, null, "family name", book, 0u, 0u, "Mr. Bennet")
                        val cnt = core.bookWordCount(book, word)
                        assertTrue(ctx, cnt.toInt() >= (step["min_book_count"] as Number).toInt())
                        core.deleteWord(word)
                        assertTrue(ctx, core.words().none { it.word.equals(word, ignoreCase = true) })
                        core.restoreWord(word)
                        assertTrue(ctx, core.words().any { it.word.equals(word, ignoreCase = true) })
                    }
                    "replace_rule_reorder" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val r1 = core.saveReplaceRule(ReplaceRule("", book, "甲", "乙", false, false, true, 1.0))
                        val r2 = core.saveReplaceRule(ReplaceRule("", book, "丙", "丁", false, false, true, 2.0))
                        core.moveReplaceRule(r2.id, 0.5)
                        val ordered = core.replaceRules(book).sortedBy { it.order }
                        val p2 = ordered.indexOfFirst { it.id == r2.id }
                        val p1 = ordered.indexOfFirst { it.id == r1.id }
                        assertTrue(ctx, p2 in 0 until p1)
                    }
                    "set_finished" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val finished = step["finished"] as Boolean
                        core.setFinished(book, finished)
                        val b = core.books().first { it.id == book }
                        assertEquals(ctx, if (finished) ReadingStatus.FINISHED else ReadingStatus.READING, b.status)
                    }
                    "keep_record_only" -> {
                        val core = cores.getValue(step["device"] as String)
                        val book = vars.getValue(step["book"] as String)
                        val updated = core.keepRecordOnly(book)
                        assertTrue(ctx, updated.record)
                        assertTrue(ctx, !updated.available)
                    }
                    "purge_deleted" -> {
                        val purged = cores.getValue(step["device"] as String).purgeDeleted((step["older_than_ms"] as Number).toLong())
                        (step["min_purged"] as? Number)?.let { assertTrue(ctx, purged.toInt() >= it.toInt()) }
                    }
                    else -> fail("$ctx: unsupported op $op")
                }
            }
        } finally {
            cores.values.forEach { runCatching { it.close() } }
            work.deleteRecursively()
        }
    }

    private fun parsePaper(s: String) = when (s) {
        "plain" -> Paper.PLAIN
        "dark" -> Paper.DARK
        "kami" -> Paper.KAMI
        "kaminight" -> Paper.KAMI_NIGHT
        "sage" -> Paper.SAGE
        "eink" -> Paper.EINK
        else -> error("unknown paper $s")
    }

    private fun parseMode(s: String) = if (s == "scrolled") LayoutMode.SCROLLED else LayoutMode.PAGINATED

    private fun parseFont(s: String) = when (s) {
        "sans" -> Font.SANS
        "serif" -> Font.SERIF
        "kai" -> Font.KAI
        "wenkai" -> Font.WEN_KAI
        else -> error("unknown font $s")
    }

    private fun parseStyle(s: String) = when (s) {
        "yellow" -> MarkStyle.YELLOW
        "blue" -> MarkStyle.BLUE
        "pink" -> MarkStyle.PINK
        "wave" -> MarkStyle.WAVE
        "underline" -> MarkStyle.UNDERLINE
        "dotted" -> MarkStyle.DOTTED
        else -> error("unknown style $s")
    }

    private fun parseFormat(s: String) = when (s) {
        "epub" -> BookFormat.EPUB
        "txt" -> BookFormat.TXT
        "pdf" -> BookFormat.PDF
        "mobi" -> BookFormat.MOBI
        "audio" -> BookFormat.AUDIO
        "podcast" -> BookFormat.PODCAST
        else -> error("unknown format $s")
    }

    private fun parseSyncPart(s: String) = when (s) {
        "reading" -> SyncPart.READING
        "notes" -> SyncPart.NOTES
        "settings" -> SyncPart.SETTINGS
        else -> error("unknown sync part $s")
    }

    private fun parseChoice(s: String) = when (s) {
        "older" -> ConflictChoice.OLDER
        "newer" -> ConflictChoice.NEWER
        "both" -> ConflictChoice.BOTH
        else -> error("unknown choice $s")
    }

    // Minimal pure-JVM JSON parser (Android JVM unit tests stub org.json).
    private fun parseJson(src: String): Any? = JsonParser(src).parseRoot()

    private class JsonParser(private val src: String) {
        private var i = 0
        fun parseRoot(): Any? {
            val v = parseValue()
            skipWs()
            assertEquals(src.length, i)
            return v
        }
        private fun skipWs() {
            while (i < src.length && src[i].isWhitespace()) i++
        }
        private fun parseValue(): Any? {
            skipWs()
            return when (src[i]) {
                '{' -> parseObj()
                '[' -> parseArr()
                '"' -> parseStr()
                't' -> { i += 4; true }
                'f' -> { i += 5; false }
                'n' -> { i += 4; null }
                else -> parseNum()
            }
        }
        private fun parseObj(): Map<String, Any?> {
            i++
            val map = LinkedHashMap<String, Any?>()
            skipWs()
            if (src[i] == '}') { i++; return map }
            while (true) {
                skipWs()
                val k = parseStr()
                skipWs()
                require(src[i++] == ':')
                map[k] = parseValue()
                skipWs()
                when (src[i++]) {
                    ',' -> continue
                    '}' -> return map
                    else -> error("malformed JSON object at $i")
                }
            }
        }
        private fun parseArr(): List<Any?> {
            i++
            val list = ArrayList<Any?>()
            skipWs()
            if (src[i] == ']') { i++; return list }
            while (true) {
                list.add(parseValue())
                skipWs()
                when (src[i++]) {
                    ',' -> continue
                    ']' -> return list
                    else -> error("malformed JSON array at $i")
                }
            }
        }
        private fun parseStr(): String {
            i++
            val sb = StringBuilder()
            while (i < src.length) {
                val c = src[i++]
                if (c == '"') return sb.toString()
                if (c == '\\') {
                    when (val e = src[i++]) {
                        '"', '\\', '/' -> sb.append(e)
                        'n' -> sb.append('\n')
                        'r' -> sb.append('\r')
                        't' -> sb.append('\t')
                        'b' -> sb.append('\b')
                        'f' -> sb.append('\u000C')
                        'u' -> {
                            sb.append(src.substring(i, i + 4).toInt(16).toChar())
                            i += 4
                        }
                        else -> sb.append(e)
                    }
                } else {
                    sb.append(c)
                }
            }
            error("unterminated string")
        }
        private fun parseNum(): Number {
            val start = i
            if (src[i] == '-') i++
            while (i < src.length && src[i] in '0'..'9') i++
            var isFloat = false
            if (i < src.length && src[i] == '.') { isFloat = true; i++; while (i < src.length && src[i] in '0'..'9') i++ }
            if (i < src.length && (src[i] == 'e' || src[i] == 'E')) {
                isFloat = true; i++
                if (i < src.length && (src[i] == '+' || src[i] == '-')) i++
                while (i < src.length && src[i] in '0'..'9') i++
            }
            val token = src.substring(start, i)
            return if (isFloat) token.toDouble() else token.toLong()
        }
    }
}
