package app.yuyue.reader

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.toArgb
import app.yuyue.design.Prefs
import app.yuyue.library.Library
import app.yuyue.t
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.BookDetail
import uniffi.yuyue_core.BookFormat
import uniffi.yuyue_core.Bookmark
import uniffi.yuyue_core.Font
import uniffi.yuyue_core.LayoutMode
import uniffi.yuyue_core.Mark
import uniffi.yuyue_core.MarkStyle
import uniffi.yuyue_core.Paper
import uniffi.yuyue_core.Position
import uniffi.yuyue_core.ReaderSettings
import uniffi.yuyue_core.SearchHit
import uniffi.yuyue_core.SectionKind
import uniffi.yuyue_core.TocItem
import java.time.LocalDate
import kotlin.math.roundToInt
import uniffi.yuyue_core.TextPoint as CoreTextPoint

/** Text selected on the page, for the card over it. */
data class TextSelection(val section: Int, val start: TextPoint, val end: TextPoint, val text: String, val rect: TextRect?)

/** A highlight tapped on the page, for the card over it. */
data class OpenMark(val mark: Mark, val x: Double, val y: Double)

/** One open book: where the reader is, how the pages look, the notes in it (as `ReaderModel` on iPhone). */
class ReaderModel(val library: Library, val bookID: String, val pager: ReaderPager, val context: Context) : ReaderPager.Listener {
    val core = library.core
    private val opening = runCatching { core.book(bookID) }
    var detail: BookDetail? by mutableStateOf(opening.getOrNull())
        private set
    var failure by mutableStateOf<String?>(null)
    var settings by mutableStateOf(ReaderSettings(Paper.PLAIN, LayoutMode.PAGINATED, null, 18.0, null, null, null, null, true))
        private set
    /** Whether this book keeps settings of its own (只用于这本书). */
    var ownSettings by mutableStateOf(false)
        private set
    var chromeShown by mutableStateOf(false)
    /** User tucked the slim listening pill into a corner orb while reading along. */
    var playerTucked by mutableStateOf(false)
    /** Scrolling down the page sent the controls away, and the top bar if it was up. */
    var barsAway by mutableStateOf(false)
        private set
    fun expandListenBar() {
        playerTucked = false
        barsAway = false
    }
    private var chromeComesBack = false
    var tocShown by mutableStateOf(false)
    var settingsShown by mutableStateOf(false)
    var section by mutableIntStateOf(0)
        private set
    var page by mutableIntStateOf(0)
        private set
    var pageCount by mutableIntStateOf(1)
        private set
    /** The sentence the reader is at: the one read aloud, or the first on the page. */
    var sentence by mutableIntStateOf(0)
    var marks by mutableStateOf<List<Mark>>(emptyList())
        private set
    var bookmarks by mutableStateOf<List<Bookmark>>(emptyList())
        private set
    var selection by mutableStateOf<TextSelection?>(null)
    var openMark by mutableStateOf<OpenMark?>(null)
    /** 气泡: a bubble's avatar tapped, the card for who says it over the page. */
    var speakerPick by mutableStateOf<SpeakerPick?>(null)
    /** 别的人物… from that card: the whole cast to search. */
    var speakerOthers by mutableStateOf<SpeakerPick?>(null)
    /** The model answered a scene: the bubbles and who reads what follow. */
    private val dramaWatch = library.scope.launch {
        DramaWork.updated.collect { if (it == bookID) { pager.bubblesChanged(); player?.speakersChanged() } }
    }
    var lastStyle by mutableStateOf(MarkStyle.YELLOW)
    /** 书内搜索. */
    var searchShown by mutableStateOf(false)
    var query by mutableStateOf("")
    var hits by mutableStateOf<List<SearchHit>>(emptyList())
        private set
    var hitIndex by mutableStateOf<Int?>(null)
        private set
    /** The undo note at the bottom of the page. */
    var toast by mutableStateOf<Library.Toast?>(null)
        private set
    /** 回到原处: where the reader was before jumping off, kept until a few pages are read. */
    var returnTo by mutableStateOf<Pair<Position, Int?>?>(null)
        private set
    /** Reads the book aloud; null for a book without words to read. */
    val player: app.yuyue.speech.Player? = detail?.let { app.yuyue.speech.Player(context, core, it) }?.also {
        // 听读书: the narrator reads where the recording has the sentence.
        it.narrator = library.recordings.voice(bookID)
        it.guide = library.recordings.guide(bookID)
    }
    /** Bumped when the recording's guide is loaded again. */
    var guideVersion by mutableStateOf(0)
        private set
    private var unreadShown = false
    /** 回到昨晚: the timer stopped the voice last time; where it was set. */
    var sleepReturn by mutableStateOf<SleepReturn?>(null)
    private val guideWatch = library.scope.launch {
        app.yuyue.speech.Recordings.changed.collect { if (it == library.recordings.audioFor(bookID)) loadGuide() }
    }
    /** 整理错字 done: the 文字版's words changed, the page loads them again. */
    private val polishWatch = library.scope.launch {
        app.yuyue.speech.TextPolishRuns.finished.collect { if (it == bookID) pager.reload() }
    }
    /** While listening to this book as a play, keep nearby paired Macs updated every 60 s. */
    private val wantWatch = library.scope.launch {
        while (true) {
            delay(60_000)
            if (isDramaActive()) {
                val sec = (player?.spot?.section ?: section).coerceAtLeast(0).toUInt()
                library.recordingFetch.want(bookID, sec)
                library.recordingFetch.dramaOffer(bookID)?.let { library.recordingFetch.takeClips(bookID, it) }
            }
        }
    }

    private fun isDramaActive(): Boolean = library.dramaSynth.isDramaActive(bookID)
    var listenShown by mutableStateOf(false)
    /** 人物: the cast page. */
    var castShown by mutableStateOf(false)
    private var bubblesState = mutableStateOf(runCatching { core.bookBubbles(bookID) }.getOrDefault(false))
    /** 气泡模式: the dialogue as bubbles, kept per book (synced). */
    var bubbles: Boolean
        get() = bubblesState.value
        set(v) {
            if (v == bubblesState.value) return
            bubblesState.value = v
            pager.bubbles = v
            runCatching { core.setBookBubbles(bookID, v) }
        }
    /** Enough dialogue for 气泡 and 人物: books without (essays, papers) don't offer them. */
    var hasDialogue by mutableStateOf(false)
        private set

    /** Someone's voice or side changed: the bubbles are drawn again. */
    fun castChanged() = pager.bubblesChanged()
    var immersiveShown by mutableStateOf(false)
    var infoShown by mutableStateOf(false)
    var rulesShown by mutableStateOf(false)
    var txtShown by mutableStateOf(false)
    /** Why a TXT may have been decoded or split wrong, for the hint pill leading to 编码和目录. */
    var txtDoubt by mutableStateOf<String?>(null)
        private set
    /** 外文原著: the word tapped, and its card. */
    var lookup by mutableStateOf<WordLookup?>(null)
    /** 译: 原文 / 译文对照 / 仅译文. */
    var translateMode by mutableStateOf(TranslateMode.OFF)
    var lastActiveTranslateMode = TranslateMode.BILINGUAL
    val translateShown: Boolean get() = translateMode != TranslateMode.OFF
    var translateFailure: String? = null
    /** 双击一句只翻这一句. */
    var translatingSentence by mutableStateOf<String?>(null)
    private var immersiveState = mutableStateOf(false)
    /** 沉浸阅读: only the words on the page. */
    var immersive: Boolean
        get() = immersiveState.value
        set(v) { immersiveState.value = v; barsAway = false; applyImmersive() }
    /** 与书对话. */
    val companion = app.yuyue.companion.BookCompanion(this)
    var companionShown by mutableStateOf(false)
    /** Words handed to the companion (问), and where they sit on the page. */
    var companionQuote by mutableStateOf<String?>(null)
    var companionAnchor: app.yuyue.companion.CompanionAnchor? = null
    /** After a page is turned by hand the pages stop following the voice for a while. */
    private var followFrom = 0L

    /** Opening a book only shows where it was; the position is written once the reader moves. */
    private var opened = false
    private var saving: Job? = null
    private var saved: Position? = null
    private var searching: Job? = null
    private var toastJob: Job? = null
    private var turnsSinceJump = 0
    private var jumpHits = false
    private var jumpHitTurns = 0
    /** 计时: when time was last added, and when a page was last turned. */
    private var since = System.currentTimeMillis()
    private var lastTurn = System.currentTimeMillis()
    /** 文字版: made from a recording's words; the page says so. */
    val textVersion: Boolean by lazy { runCatching { core.audiobookIsTextVersion(bookID) }.getOrDefault(false) }
    /** 逐词跟读's word times, by section and sentence. */
    private val wordCache = HashMap<Pair<Int, Int>, List<Double>?>()
    /** 简繁: this book shown in the other script, or (null) as it is. Synced with the book. */
    var script by mutableStateOf(runCatching { core.bookScript(bookID) }.getOrNull())
        private set
    /** The paper 调纸 shows, over the one in the settings. */
    var previewing by mutableStateOf<app.yuyue.design.YYPaper?>(null)
        private set
    /** 微调 / 校准一句: `(section, sentence)` open in `TimingAdjustSheet`. */
    var adjustingSentence by mutableStateOf<Pair<Int, Int>?>(null)
    var narrationFlag by mutableStateOf<Flag?>(null)
        private set
    private val flagsLetBe = HashSet<String>()
    /** 局域网传录音: this book's recording, laid out on another device but not here, and a Mac
     *  nearby that has it. */
    var missingRecording by mutableStateOf<String?>(null)
    var recordingOffer by mutableStateOf<app.yuyue.speech.RecordingFetch.Offer?>(null)
    /** 正文模式: the quote the chosen words are in, for 谁说的. */
    var contextLine: SpeakerPick? = null
        private set

    init {
        PaperAppearance.syncFromContext(context)
        val own = runCatching { core.bookReaderSettings(bookID) }.getOrNull()
        val s = own ?: runCatching { core.readerSettings() }.getOrNull() ?: settings
        settings = s
        ownSettings = own != null
        pager.listener = this
        pager.fontSize = s.fontSize
        pager.mode = s.mode
        pager.calm = EInk.on
        pager.pageTurn = PageTurnPrefs.current
        pager.soundEnabled = PageTurnPrefs.sound
        pager.bubbles = bubbles
        pager.background = paperTokens.bg.toArgb()
        pager.ink = paperTokens.ink.toArgb()
        pager.accent = paperTokens.accent.toArgb()
        pager.chapterTitle = { sec -> detail?.let { chapterTitle(it, sec) } }
        // Whether the book has dialogue: cached in core across all chapters.
        kotlinx.coroutines.MainScope().launch {
            hasDialogue = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                runCatching { core.bookHasDialogue(bookID) }.getOrDefault(false)
            }
        }
        Prefs.string(STYLE_KEY).let { saved -> MarkStyle.entries.firstOrNull { it.name == saved }?.let { lastStyle = it } }
        reloadNotes()
        // 我的 › 沉浸阅读 on: every book opens with only its words.
        immersive = ImmersivePrefs.always
        setUpForeign()
        if (detail?.summary?.format == BookFormat.TXT) {
            val doubt = runCatching { core.txtDoubt(bookID) }.getOrNull()
            if (doubt != null && Prefs.string("txtDoubtDismissed.$bookID") != doubt) {
                txtDoubt = doubt
            }
        }
        player?.let { p ->
            p.onSentence = { spot, duration, elapsed, playing -> reading(spot, duration, elapsed, playing); setListening(playing) }
            p.onStop = { flush(); pager.clearInk(); setListening(false) }
            p.onSleep = { spot ->
                rememberSleep(p.sleepFrom ?: spot)
                try {
                    core.addBookmark(bookID, spot.section.toUInt(), spot.sentence.toUInt())
                } catch (e: Exception) {
                    failure = Library.message(e)
                }
                reloadNotes()
                show(Library.Toast(Library.Toast.Icon.Check, t("定时到了，停在这里"), t("加了一个书签")))
            }
        }
        val d = detail
        if (d == null) {
            failure = opening.exceptionOrNull()?.let { Library.message(it) } ?: t("找不到这本书的文件，可能被移走或删掉了。从书架上删掉，再导入一次就好")
        } else {
            val intent = library.takeIntent(bookID)
            val start = Position(d.start, 0u)
            val mine = runCatching { core.progress(bookID) }.getOrNull()
            val latest = runCatching { core.latestProgress(bookID) }.getOrNull()
            var position = if (intent == Library.OpenIntent.ListenFromStart) start else mine?.position ?: latest?.position ?: start
            if (intent is Library.OpenIntent.At) {
                position = intent.position
                if (!intent.query.isNullOrBlank()) {
                    pager.setHits(intent.query, intent.hit)
                    jumpHits = true
                    jumpHitTurns = 0
                }
            }
            pager.open(position.section.toInt(), if (position.sentence == 0u) Target.Start else Target.Sentence(position.sentence.toInt()))
            if (intent == Library.OpenIntent.Listen || intent == Library.OpenIntent.ListenFromStart) player?.play(position)
            if (intent is Library.OpenIntent.Ask) ask(intent.question)
            if (intent == null) offerSleepReturn()
            lookForRecording()
            val formatRaw = if (d.summary.format == uniffi.yuyue_core.BookFormat.MOBI) {
                runCatching { library.originalFile(d.summary)?.extension }.getOrNull() ?: d.summary.format.name
            } else {
                d.summary.format.name
            }
            val paperThemeRaw = if (EInk.on) {
                "eink"
            } else {
                val resolved = PaperAppearance.resolve(settings)
                if (resolved.custom != null) "other" else resolved.preset.name
            }
            app.yuyue.analytics.AirTraffic.trackReadingOpened(formatRaw, paperThemeRaw)
        }
    }

    /** Guarded in unit tests: no backing field or Compose delegate may be declared below `init`. */
    @Suppress("unused")
    private val initCompleted = true

    /** 不用了 on the TXT layout hint: won't come back for the same doubt message. */
    fun dismissDoubt() {
        txtDoubt?.let { Prefs.set("txtDoubtDismissed.$bookID", it) }
        txtDoubt = null
    }

    /** Opens 书伴 with a question. */

    /** What 沉浸阅读 changes on the page. 墨水屏模式 turns whole pages, never slides them. */
    fun applyImmersive() {
        pager.calm = (immersive && ImmersivePrefs.stillPages) || EInk.on
        pager.marksHidden = immersive && ImmersivePrefs.hidesMarks
        pager.glossesHidden = immersive && ImmersivePrefs.hidesHints
    }

    /** Consumes a pending open intent when an already-playing model is re-entered from Search/Notes. */
    fun consumeIntent() {
        val intent = library.takeIntent(bookID) ?: return
        when (intent) {
            is Library.OpenIntent.At -> {
                jump(intent.position.section.toInt(), intent.position.sentence.toInt())
                if (!intent.query.isNullOrBlank()) {
                    pager.setHits(intent.query, intent.hit)
                    jumpHits = true
                    jumpHitTurns = 0
                } else {
                    jumpHits = false
                    pager.setHits(null, null)
                }
            }
            Library.OpenIntent.Listen -> pager.here()?.let { player?.play(it) }
            Library.OpenIntent.ListenFromStart -> detail?.let { player?.play(Position(it.start, 0u)) }
            is Library.OpenIntent.Ask -> ask(intent.question)
        }
    }
    /** Captures the active selection or open highlight so 书伴 can save an answer back as its thought. */
    fun captureCompanionAnchor() {
        val open = openMark
        val sel = selection
        companionAnchor = when {
            open != null -> app.yuyue.companion.CompanionAnchor(
                markID = open.mark.id,
                section = open.mark.section,
                start = open.mark.start,
                end = open.mark.end,
                text = open.mark.text,
            )
            sel != null -> app.yuyue.companion.CompanionAnchor(
                markID = null,
                section = sel.section.toUInt(),
                start = CoreTextPoint(sel.start.s.coerceAtLeast(0).toUInt(), sel.start.o.coerceAtLeast(0).toUInt()),
                end = CoreTextPoint(sel.end.s.coerceAtLeast(0).toUInt(), sel.end.o.coerceAtLeast(0).toUInt()),
                text = sel.text.trim(),
            )
            else -> null
        }
    }

    fun clearCompanionQuote() {
        companionQuote = null
        companionAnchor = null
    }

    fun ask(question: String) {
        companionShown = true
        if (question.isNotBlank()) {
            companion.ask(question, companionQuote, anchor = companionAnchor)
            clearCompanionQuote()
        }
    }

    // MARK: Reading aloud

    val isPlaying: Boolean get() = player?.state == app.yuyue.speech.Player.State.Playing || player?.state == app.yuyue.speech.Player.State.Loading

    private val following: Boolean get() = System.currentTimeMillis() >= followFrom

    /** Pauses; or reads on from what's on screen. Paused on a sentence still on the page, it carries on from there. */
    fun togglePlay() {
        val p = player ?: return
        when (p.state) {
            app.yuyue.speech.Player.State.Playing, app.yuyue.speech.Player.State.Loading -> p.pause()
            app.yuyue.speech.Player.State.Paused -> {
                followFrom = 0
                if (p.spot?.let(::isOnScreen) == true) p.resume() else pager.here()?.let { p.play(narratedStart(it)) }
            }
            app.yuyue.speech.Player.State.Stopped -> {
                followFrom = 0
                pager.here()?.let { p.play(narratedStart(it)) }
            }
        }
    }

    /** ‹ › in the capsule: a sentence while reading aloud, a page otherwise. */
    fun step(forward: Boolean) {
        val p = player
        if (p != null && p.spot != null) p.skip(forward) else turn(forward)
    }

    private fun isOnScreen(spot: app.yuyue.speech.Player.Spot): Boolean {
        val cur = pager.current ?: return false
        if (cur.section != spot.section) return false
        val layout = cur.layout ?: return false
        return settings.mode == LayoutMode.SCROLLED || layout.pageOf(spot.sentence) == cur.page
    }

    private fun setListening(playing: Boolean) {
        if (!playing && playerTucked) playerTucked = false
        if (playing) { if (library.listening != bookID) library.listening = bookID }
        else if (library.listening == bookID) library.listening = null
    }

    /** When each word of the sentence is said, from its start, as [character, ms] pairs — in the
     *  narrator's voice, with the setting on, where the recording's lay-out knows. */
    private fun wordTimes(spot: app.yuyue.speech.Player.Spot): List<Double>? {
        val p = player ?: return null
        if (p.narrator == null || p.guide?.settings?.wordFollow != true) return null
        val audio = library.recordings.audioFor(bookID) ?: return null
        val start = library.recordings.timings(bookID)?.get(spot.section to spot.sentence)?.start ?: return null
        return wordCache.getOrPut(spot.section to spot.sentence) {
            val units = runCatching { core.audiobookUnits(audio, spot.section.toUInt(), spot.sentence.toUInt()) }.getOrDefault(emptyList())
            if (units.isEmpty()) null else units.flatMap { listOf(it.at.toDouble(), maxOf(0.0, (it.seconds - start) * 1000)) }
        }
    }

    private fun reading(spot: app.yuyue.speech.Player.Spot, durationMs: Double, elapsedMs: Double, playing: Boolean) {
        checkFlag(spot)
        if (playing) sleepHeard()
        if (spot.section == section && sentence != spot.sentence) sentence = spot.sentence
        val spoken = Position(spot.section.toUInt(), maxOf(0, spot.sentence).toUInt())
        if (playing || opened) {
            opened = true
            save(spoken)
            if (!playing) flush()
        }
        showUnread()
        // A chapter the recording only follows as a whole: no ink line; the page keeps up.
        if (player?.narrator != null && player.guide?.chapterOnly(spot.section) == true) {
            pager.clearInk()
            if (following && !isOnScreen(spot)) pager.show(spot.section, Target.Sentence(spot.sentence, "third"))
            return
        }
        pager.setInk(spot.section, spot.sentence, durationMs, elapsedMs, playing, wordTimes(spot))
        // Into the next chapter with the voice; within a section the reader says when the ink reaches another page.
        if (following && spot.section != pager.current?.section) pager.show(spot.section, Target.Sentence(spot.sentence, "third"))
    }

    val paper: Paper get() = settings.paper

    // MARK: Moving around

    fun turn(forward: Boolean) {
        turnedByHand(pager)
        pager.turn(forward)
    }

    fun go(item: TocItem) {
        leaving()
        turnedByHand(pager)
        pager.show(item.section.toInt(), item.anchor?.let { Target.Anchor(it) } ?: Target.Start)
    }

    fun jump(section: Int, sentence: Int) {
        leaving()
        lastTurn = System.currentTimeMillis()
        opened = true
        pager.show(section, Target.Sentence(sentence, "third"))
    }

    /** About to jump: the place to come back to, unless one is kept already. */
    fun leaving() {
        turnsSinceJump = 0
        if (returnTo != null || !opened) return
        val here = pager.here() ?: return
        returnTo = here to bookPage
    }

    /** 回到第 N 页. */
    fun goBack() {
        val back = returnTo?.first ?: return
        returnTo = null
        pager.show(back.section.toInt(), Target.Sentence(back.sentence.toInt(), "third"))
    }

    val chapterTitle: String? get() = detail?.let { chapterTitle(it, section) }

    // MARK: Notes

    fun reloadNotes() {
        marks = runCatching { core.marks(bookID) }.getOrDefault(emptyList())
        bookmarks = runCatching { core.bookmarks(bookID) }.getOrDefault(emptyList())
        pager.setMarks(marks)
        library.notesChanged()
    }

    /** Highlights the selection in `style` (the last one used when null). */
    fun mark(style: MarkStyle? = null, note: String? = null): Mark? {
        val sel = selection ?: return null
        val st = style ?: lastStyle
        remember(st)
        return try {
            val m = core.addMark(bookID, sel.section.toUInt(), CoreTextPoint(sel.start.s.toUInt(), sel.start.o.toUInt()), CoreTextPoint(sel.end.s.toUInt(), sel.end.o.toUInt()), sel.text.trim(), st, note)
            selection = null
            pager.call("yy.clearSelection()")
            pager.keepShown(m.id)
            reloadNotes()
            m
        } catch (e: Exception) {
            failure = Library.message(e)
            null
        }
    }

    fun setStyle(style: MarkStyle, mark: Mark) {
        remember(style)
        run { core.setMarkStyle(bookID, mark.id, style) }
        if (openMark?.mark?.id == mark.id) marks.firstOrNull { it.id == mark.id }?.let { openMark = openMark?.copy(mark = it) }
    }

    fun setNote(note: String?, mark: Mark) = run { core.setMarkNote(bookID, mark.id, note?.trim()?.ifEmpty { null }) }

    fun deleteMark(mark: Mark) {
        openMark = null
        run { core.deleteMark(bookID, mark.id) }
        show(Library.Toast(Library.Toast.Icon.Highlight, t("已删除划线"), if (mark.note == null) "“${mark.text.take(16)}”" else t("想法也一起删了"), danger = true) {
            run { core.restoreMark(bookID, mark.id) }
        })
    }

    private fun remember(style: MarkStyle) {
        lastStyle = style
        Prefs.set(STYLE_KEY, style.name)
    }

    fun copySelection() {
        val text = selection?.text ?: openMark?.mark?.text ?: return
        (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText("", text))
        dismissCards()
        show(Library.Toast(Library.Toast.Icon.Check, t("已复制"), "“${text.take(16)}”"))
    }

    /** 从这读: reads aloud from the first selected sentence, or the highlight's. */
    fun readFrom(mark: Mark?) {
        val p = player ?: return
        val start = mark?.let { Position(it.section, it.start.sentence) } ?: selection?.let { Position(it.section.toUInt(), it.start.s.toUInt()) } ?: return
        dismissCards()
        followFrom = 0
        p.play(narratedStart(start))
    }

    fun dismissCards() {
        if (selection != null) pager.call("yy.clearSelection()")
        selection = null
        openMark = null
        speakerPick = null
    }

    override fun bubble(line: uniffi.yuyue_core.BubbleLine, section: Int, choice: String?, x: Double, y: Double) {
        selection = null
        openMark = null
        if (!choice.isNullOrEmpty()) return setSpeaker(uniffi.yuyue_core.UserChoice.Person(choice), section, line)
        val pick = SpeakerPick(section.toUInt(), line, x, y)
        if (choice != null) { speakerOthers = pick; return }
        speakerPick = pick
        library.scope.launch {
            val people = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                runCatching { core.dramaLinePeople(bookID, section.toUInt(), line.first) }.getOrDefault(emptyList())
            }
            if (speakerPick?.line == line) speakerPick = speakerPick?.copy(people = people)
        }
    }

    /** Who says a line, from the reader: kept (and synced); the bubbles and the voices follow. */
    fun setSpeaker(choice: uniffi.yuyue_core.UserChoice, section: Int, line: uniffi.yuyue_core.BubbleLine) {
        speakerPick = null
        speakerOthers = null
        library.scope.launch {
            kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                runCatching { core.dramaSetSpeaker(bookID, section.toUInt(), line.first, line.lineStart, choice) }
            }
            player?.speakersChanged()
            castChanged()
        }
    }

    // MARK: Bookmarks

    /** The bookmark on the page showing, if any. */
    val bookmarkHere: Bookmark?
        get() {
            val layout = pager.current?.layout ?: return null
            return bookmarks.firstOrNull { it.section.toInt() == section && layout.pageOf(it.sentence.toInt()) == page }
        }

    fun toggleBookmark() {
        val here = bookmarkHere
        if (here != null) {
            run { core.deleteBookmark(bookID, here.id) }
            show(Library.Toast(Library.Toast.Icon.Delete, t("书签拿掉了"), if (here.text.isEmpty()) t("这一页") else "“${here.text.take(16)}”") {
                run { core.restoreBookmark(bookID, here.id) }
            })
        } else {
            val p = pager.here() ?: return
            run { core.addBookmark(bookID, p.section, p.sentence) }
            show(Library.Toast(Library.Toast.Icon.Check, t("加了书签"), estimatedPage(p.section.toInt(), p.sentence.toInt())?.let { t("第 %lld 页", it) } ?: ""))
        }
    }

    fun deleteBookmark(b: Bookmark) = run { core.deleteBookmark(bookID, b.id) }

    // MARK: Search

    /** Searches the book for `query`, a moment after the typing stops. */
    fun search() {
        searching?.cancel()
        jumpHits = false
        val q = query.trim()
        if (q.isEmpty()) {
            hits = emptyList()
            hitIndex = null
            pager.setHits(null, null)
            return
        }
        searching = library.scope.launch {
            delay(180)
            val found = withContext(Dispatchers.IO) { runCatching { core.searchBook(bookID, q) }.getOrDefault(emptyList()) }
            hits = found
            val first = found.indexOfFirst { it.section.toInt() > section || (it.section.toInt() == section && it.sentence.toInt() >= sentence) }
            showHit(if (first >= 0) first else if (found.isEmpty()) null else 0)
        }
    }

    fun showHit(index: Int?) {
        hitIndex = index
        val hit = index?.let { hits.getOrNull(it) }
        pager.setHits(query.trim(), hit)
        if (hit != null) {
            jump(hit.section.toInt(), hit.sentence.toInt())
            listenAt(hit)
        }
    }

    /** Listening: the voice goes to the hit too — in the narrator's voice from the word itself,
     *  where the recording's lay-out knows when it's said; else from the sentence's start. */
    private fun listenAt(hit: uniffi.yuyue_core.SearchHit) {
        val player = player ?: return
        if (!isPlaying) return
        var word: Double? = null
        val audio = library.recordings.audioFor(bookID)
        if (player.narrator != null && audio != null) {
            val text = runCatching { core.sentences(bookID, hit.section) }.getOrNull()?.getOrNull(hit.sentence.toInt())
            if (text != null) {
                // The nth time the match comes in the sentence, in characters.
                var from = 0
                var at = -1
                for (k in 0..hit.nth.toInt()) {
                    at = text.indexOf(hit.matched, from, ignoreCase = true)
                    if (at < 0) break
                    from = at + hit.matched.length
                }
                if (at >= 0) word = runCatching { core.audiobookWordTime(audio, hit.section, hit.sentence, text.codePointCount(0, at).toUInt()) }.getOrNull()
            }
        }
        player.play(uniffi.yuyue_core.Position(hit.section, hit.sentence), word)
    }

    fun nextHit(forward: Boolean) {
        if (hits.isEmpty()) return
        val i = hitIndex ?: if (forward) -1 else hits.size
        showHit((i + (if (forward) 1 else -1) + hits.size) % hits.size)
    }

    fun endSearch() {
        searching?.cancel()
        jumpHits = false
        searchShown = false
        query = ""
        hits = emptyList()
        hitIndex = null
        pager.setHits(null, null)
    }

    // MARK: Settings

    fun update(change: (ReaderSettings) -> ReaderSettings) {
        val new = change(settings)
        if (new == settings) return
        settings = new
        try {
            if (ownSettings) core.setBookReaderSettings(bookID, new) else core.setReaderSettings(new)
        } catch (e: Exception) {
            failure = Library.message(e)
        }
        apply(new)
    }

    /** A Chinese book, which 简繁 can be shown for. */
    val isChinese: Boolean get() = detail?.summary?.language?.startsWith("zh") == true

    fun changeScript(to: uniffi.yuyue_core.Script?) {
        if (to == script) return
        script = to
        runCatching { core.setBookScript(bookID, to) }
        // The pages, the voice, the chapter titles and the search all go through it.
        detail = runCatching { core.book(bookID) }.getOrNull() ?: detail
        player?.textsChanged()
        pager.reload()
    }

    /** 只用于这本书: the book keeps its own settings from now on, or goes back to the shared ones. */
    fun setOwn(on: Boolean) {
        if (on == ownSettings) return
        ownSettings = on
        runCatching { core.setBookReaderSettings(bookID, if (on) settings else null) }
        if (!on) runCatching { core.readerSettings() }.getOrNull()?.takeIf { it != settings }?.let { settings = it; apply(it) }
    }

    fun newPaper(): uniffi.yuyue_core.CustomPaper = Papers.newDraft(settings)

    /** 复制一张: straight onto the shelf of papers, named 豆沙绿 2 and so on, and on the page. */
    fun duplicatePaper(mine: uniffi.yuyue_core.CustomPaper) {
        Papers.duplicate(mine)?.let { choose(it) }
    }

    /** 删除, with 撤销 for a few seconds. */
    fun deletePaper(mine: uniffi.yuyue_core.CustomPaper) {
        val wasOn = PaperAppearance.resolve(settings).custom?.id == mine.id || settings.customPaper == mine.id
        if (settings.customPaper == mine.id) update { it.copy(customPaper = null) }
        PaperAppearance.forget(mine.id)
        Papers.delete(mine.id)
        show(Library.Toast(Library.Toast.Icon.Delete, app.yuyue.t("已删除“%@”", mine.name), "", danger = true) {
            Papers.restore(mine.id)
            if (wasOn) choose(mine)
        })
    }

    fun choose(mine: uniffi.yuyue_core.CustomPaper) {
        PaperAppearance.pick(mine, settings) { update(it) }
    }

    /** The paper the page and the controls over it are drawn on. */
    val paperTokens: app.yuyue.design.YYPaper get() = if (EInk.on) EInk.paper else (previewing ?: PaperAppearance.resolve(settings).tokens)

    /** 调纸 being set: the page and the controls on `draft` until it is done or cancelled (null). */
    fun previewPaper(draft: uniffi.yuyue_core.CustomPaper?) {
        if (draft == null) {
            if (previewing == null) return
            previewing = null
            apply(settings)
            return
        }
        previewing = customTokens(draft)
        pager.apply(settings.script(pager.geometry()).apply {
            put("paper", uniffi.yuyue_core.paperColors(draft).preset.tokens.id)
            put("paperStyle", uniffi.yuyue_core.paperStyle(draft))
            put("texture", draft.texture.css ?: JSONObject.NULL)
        })
    }

    /** 我的纸 changed: the page on one takes its new colours. */
    fun papersChanged() {
        if (previewing == null) apply(settings)
    }

    private fun apply(s: ReaderSettings) {
        pager.fontSize = s.fontSize
        val modeChanged = pager.mode != s.mode
        pager.mode = s.mode
        if (modeChanged) barsAway(false)
        pager.background = paperTokens.bg.toArgb()
        pager.ink = paperTokens.ink.toArgb()
        pager.accent = paperTokens.accent.toArgb()
        applyImmersive()
        pager.apply(s.script(pager.geometry()))
        if (modeChanged) pager.reload()
    }

    // MARK: Where the reader is

    private val counted: List<Pair<Int, uniffi.yuyue_core.SectionSummary>>
        get() = detail?.sections?.withIndex()?.filter { it.value.linear && it.value.kind == SectionKind.TEXT }?.map { it.index to it.value } ?: emptyList()

    /** 0–1 through the main text at a sentence, as the core counts it. */
    fun fraction(section: Int, sentence: Int): Double {
        val all = counted
        val total = all.sumOf { it.second.units.toDouble() }
        if (total <= 0) return 0.0
        var before = 0.0
        for ((i, s) in all) {
            if (i < section) before += s.units.toDouble()
            else if (i == section) before += s.units.toDouble() * sentence / maxOf(1u, s.sentences).toDouble()
        }
        return minOf(1.0, before / total)
    }

    val bookFraction: Double get() = fraction(section, sentence)

    /** Sentences in the current section. */
    val sentenceCount: Int get() = detail?.sections?.getOrNull(section)?.sentences?.toInt() ?: 0

    /** Minutes to read a section at the core's pace (300 characters a minute). */
    fun minutes(section: Int): Int {
        val s = detail?.sections?.getOrNull(section) ?: return 0
        return maxOf(1, (s.units.toDouble() / 300).roundToInt())
    }

    val chapterMinutesLeft: Int
        get() {
            val n = maxOf(1, sentenceCount)
            return maxOf(1, (minutes(section).toDouble() * (n - sentence) / n).roundToInt())
        }

    /** 0–1 through the current chapter, matching the progress bar on the reading capsule. */
    val chapterProgress: Double
        get() = if (sentenceCount > 0) minOf(1.0, (sentence + 1).toDouble() / sentenceCount) else 0.0

    /** The capsule's second line: `本章 57% · 还剩 3 分钟`, matching the chapter progress bar. */
    val capsuleNote: String
        get() {
            val pct = (chapterProgress * 100).roundToInt()
            return if (detail?.summary?.format == BookFormat.PODCAST) {
                t("本集 %lld%% · 还剩 %lld 分钟", pct, chapterMinutesLeft)
            } else {
                t("本章 %lld%% · 还剩 %lld 分钟", pct, chapterMinutesLeft)
            }
        }

    /** The page in the whole book, guessed from how much text the current section puts on a page. */
    fun estimatedPage(section: Int, sentence: Int): Int? {
        val s = detail?.sections?.getOrNull(this.section) ?: return null
        if (s.units == 0u) return null
        val perPage = s.units.toDouble() / maxOf(1, pageCount)
        if (perPage <= 0) return null
        var before = counted.filter { it.first < section }.sumOf { it.second.units.toDouble() }
        detail?.sections?.getOrNull(section)?.let { before += it.units.toDouble() * sentence / maxOf(1u, it.sentences).toDouble() }
        return (before / perPage).toInt() + 1
    }

    val bookPage: Int?
        get() {
            val layout = pager.current?.layout
            if (layout != null && pager.current?.section == section) {
                val first = estimatedPage(section, 0) ?: 1
                return first + minOf(page, layout.pageCount - 1)
            }
            return estimatedPage(section, sentence)
        }

    /** "第三回 · 第 3 页". */
    fun place(section: Int, sentence: Int): String =
        listOfNotNull(detail?.let { chapterTitle(it, section) }, estimatedPage(section, sentence)?.let { t("第 %lld 页", it) }).joinToString(" · ")

    // MARK: Time

    /** Adds the time since the last tick to the section showing: reading while pages have been
     *  turned in the last five minutes. */
    fun tick() {
        val now = System.currentTimeMillis()
        val seconds = (now - since) / 1000
        since = now
        if (seconds <= 0 || seconds >= 3600) return
        if (now - lastTurn >= 300_000) return
        runCatching { core.addTime(bookID, section.toUInt(), seconds.toUInt(), 0u, LocalDate.now().toString()) }
    }

    // MARK: Progress

    private fun save(p: Position) {
        if (p == saved) return
        saved = p
        if (saving != null) return
        saving = library.scope.launch {
            delay(300)
            saving = null
            saved?.let(::write)
        }
    }

    fun flush() {
        val job = saving ?: return
        job.cancel()
        saving = null
        saved?.let(::write)
    }

    private fun write(p: Position) {
        try {
            core.saveProgress(bookID, p)
        } catch (e: Exception) {
            failure = Library.message(e)
        }
        library.reload()
    }

    // MARK: 有声书

    private fun loadGuide() {
        player?.guide = library.recordings.guide(bookID)
        guideVersion++
        unreadShown = false
        showUnread()
    }

    /** The sentences the recording doesn't read, a shade lighter, while it reads. */
    private fun showUnread() {
        val g = player?.guide?.takeIf { player.narrator != null }
        if ((g != null) == unreadShown && g == null) return
        unreadShown = g != null
        pager.setUnread(g?.sections?.associateWith { g.unread(it) }?.filterValues { it.isNotEmpty() } ?: emptyMap())
    }

    /** The chapter on the page is only followed as a whole while the recording reads it. */
    val chapterOnlyHere: Boolean
        get() {
            guideVersion
            val p = player ?: return false
            if (p.narrator == null || p.state == app.yuyue.speech.Player.State.Stopped) return false
            return p.guide?.chapterOnly(section) == true
        }

    /** 这一段对得不太准: `(section, firstSentence, count)` while the narrator reads an unsure stretch. */
    data class Flag(val section: Int, val first: Int, val last: Int, val number: Int, val total: Int) {
        val count: Int get() = last - first + 1
        val key: String get() = "$section:$first"
    }

    private fun checkFlag(spot: app.yuyue.speech.Player.Spot) {
        val p = player
        val g = p?.guide
        if (p?.narrator == null || g == null || g.chapterOnly(spot.section) || g.grade(spot.section, spot.sentence) != uniffi.yuyue_core.SyncBy.SPREAD) {
            pager.clearFlag()
            if (narrationFlag != null) narrationFlag = null
            return
        }
        val first = g.stretchStart(spot.section, spot.sentence)
        var last = spot.sentence
        while (g.grade(spot.section, last + 1) == uniffi.yuyue_core.SyncBy.SPREAD) last++
        pager.setFlag(spot.section, first, last)
        // Count SPREAD stretches across the book for "第 m / n 处".
        var total = 0
        var num = 1
        for (sec in g.sections.sorted()) {
            if (g.chapterOnly(sec)) continue
            var prevSpread = false
            var s = 0
            val maxS = (library.recordings.timings(bookID)?.keys?.filter { it.first == sec }?.maxOfOrNull { it.second } ?: -1)
            while (s <= maxS) {
                val sp = g.grade(sec, s) == uniffi.yuyue_core.SyncBy.SPREAD
                if (sp && !prevSpread) {
                    total++
                    if (sec == spot.section && s == first) num = total
                }
                prevSpread = sp
                s++
            }
        }
        val f = Flag(spot.section, first, last, num, maxOf(1, total))
        narrationFlag = if (f.key in flagsLetBe) null else f
    }

    fun letFlagBe() {
        val f = narrationFlag ?: return
        flagsLetBe += f.key
        narrationFlag = null
    }

    fun adjustFlag() {
        val f = narrationFlag ?: return
        player?.pause()
        adjustingSentence = f.section to f.first
    }

    /** 校准一句: opens `TimingAdjustSheet` at the current or first timed sentence of this chapter. */
    fun calibrateChapter(): Boolean {
        val times = library.recordings.timings(bookID) ?: return false
        val inChapter = times.keys.filter { it.first == section }.map { it.second }.sorted()
        val first = inChapter.firstOrNull { it >= sentence } ?: inChapter.firstOrNull() ?: return false
        player?.pause()
        adjustingSentence = section to first
        return true
    }

    /** A sentence in a stretch not sure sentence by sentence starts from the stretch's first. */
    private fun narratedStart(p: Position): Position {
        val g = player?.guide?.takeIf { player.narrator != null } ?: return p
        return Position(p.section, g.stretchStart(p.section.toInt(), p.sentence.toInt()).toUInt())
    }

    /** On opening the book: whether its recording is missing here and a Mac nearby has it. */
    fun lookForRecording() {
        val pair = runCatching { core.listeningPairs() }.getOrNull()?.firstOrNull { it.book == bookID } ?: return
        val here = runCatching { core.audioRecording(pair.recording) }.getOrNull()?.files?.all { java.io.File(it.path).isFile } ?: false
        if (here) return
        missingRecording = pair.recording
        library.scope.launch {
            repeat(6) {
                if (recordingOffer != null) return@launch
                recordingOffer = library.recordingFetch.offer(pair.recording)
                if (recordingOffer == null) kotlinx.coroutines.delay(5000)
            }
        }
    }

    val capsuleAlert: CapsuleAlert?
        get() {
            val id = missingRecording
            val offer = recordingOffer
            if (id != null && offer != null) {
                val fetch = library.recordingFetch
                fetch.taking[id]?.let { return CapsuleAlert(null, t("正在从 %@ 传 · %lld%%", offer.device, (it * 100).toLong())) {} }
                return CapsuleAlert(null, fetch.problem[id] ?: t("%@ 上有这本的录音 · 传过来", offer.device)) { fetch.take(id, offer) }
            }
            val p = player ?: return null
            if (p.narrator == null) return null
            p.extraPlaying?.let { e ->
                val what = when (e.place) { uniffi.yuyue_core.ExtraPlace.HEAD -> t("片头"); uniffi.yuyue_core.ExtraPlace.TAIL -> t("片尾"); else -> t("不在书里的话") }
                return CapsuleAlert("$what · ${app.yuyue.speech.AudiobookPlayer.clock(e.t1 - e.t0)}", t("跳过")) { p.skipExtra() }
            }
            val h = p.skippedHead ?: return null
            if (p.state != app.yuyue.speech.Player.State.Playing && p.state != app.yuyue.speech.Player.State.Loading) return null
            return CapsuleAlert(null, t("跳过了片头 %@ · 点这里听", app.yuyue.speech.AudiobookPlayer.clock(h.t1 - h.t0))) { p.playHead() }
        }

    private fun rememberSleep(from: app.yuyue.speech.Player.Spot) {
        Prefs.set(sleepKey, "${from.section},${from.sentence},${System.currentTimeMillis()}")
    }

    /** Listened on since (the offer showing or not): nothing to ask, now or next time. */
    private fun sleepHeard() {
        if (sleepReturn != null) sleepReturn = null
        if (Prefs.string(sleepKey).isNotEmpty()) Prefs.set(sleepKey, "")
    }

    private fun offerSleepReturn() {
        val v = Prefs.string(sleepKey).split(",").mapNotNull { it.toLongOrNull() }
        if (v.size != 3 || System.currentTimeMillis() - v[2] > 7 * 86_400_000L) return
        val spot = app.yuyue.speech.Player.Spot(v[0].toInt(), v[1].toInt())
        val title = runCatching { detail?.sections?.getOrNull(spot.section)?.title }.getOrNull() ?: chapterTitle ?: ""
        sleepReturn = SleepReturn(spot, SleepReturn.short(title), t("第 %lld 句", spot.sentence + 1), v[2])
    }

    fun answerSleepReturn(go: Boolean) {
        val r = sleepReturn
        sleepReturn = null
        if (go && r != null) {
            pager.show(r.spot.section, Target.Sentence(r.spot.sentence, "third"))
            player?.play(r.spot.position)
            player?.pause()
        }
        sleepReturn = null
        Prefs.set(sleepKey, "")
    }

    private val sleepKey get() = "sleepFrom.$bookID"

    fun close() {
        guideWatch.cancel()
        polishWatch.cancel()
        dramaWatch.cancel()
        wantWatch.cancel()
        if (ImmersivePrefs.quiet) DoNotDisturb.set(context, false)
        tick()
        flush()
        player?.close()
        setListening(false)
        pager.close()
    }

    // MARK: Helpers

    fun show(t: Library.Toast) {
        toast = t
        toastJob?.cancel()
        toastJob = library.scope.launch {
            delay(t.lifetime)
            if (toast?.id == t.id) toast = null
        }
    }

    fun undoToast() {
        val t = toast ?: return
        toast = null
        t.undo?.invoke()
    }

    private inline fun run(work: () -> Unit) {
        try {
            work()
        } catch (e: Exception) {
            failure = Library.message(e)
        }
        reloadNotes()
    }

    // MARK: ReaderPager.Listener

    override fun moved(pager: ReaderPager) {
        val cur = pager.current ?: return
        val layout = cur.layout ?: return
        if (section != cur.section) {
            tick()
            // A chapter too short to scroll could not bring the capsule back.
            barsAway(false)
            if (isDramaActive()) {
                val sec = (cur.section ?: 0).coerceAtLeast(0).toUInt()
                library.scope.launch {
                    library.recordingFetch.want(bookID, sec)
                    library.recordingFetch.dramaOffer(bookID)?.let { library.recordingFetch.takeClips(bookID, it) }
                }
            }
        }
        section = cur.section ?: 0
        page = cur.page
        pageCount = layout.pageCount
        val activeSpot = player?.spot?.takeIf {
            it.section == section && ((isPlaying && following) || (player.state == app.yuyue.speech.Player.State.Paused && isOnScreen(it)))
        }
        val p = activeSpot?.position ?: pager.here() ?: return
        if (opened) save(p) else { opened = true; saved = p }
        sentence = p.sentence.toInt()
    }

    override fun turnedByHand(pager: ReaderPager) {
        lastTurn = System.currentTimeMillis()
        followFrom = System.currentTimeMillis() + 8000
        chromeShown = false
        dismissCards()
        player?.let { p ->
            val spot = p.spot
            if (p.state == app.yuyue.speech.Player.State.Paused && spot != null && !isOnScreen(spot)) {
                p.stop()
            }
        }
        if (returnTo != null && ++turnsSinceJump >= 3) returnTo = null
        if (jumpHits && !searchShown && ++jumpHitTurns >= 3) {
            jumpHits = false
            pager.setHits(null, null)
        }
    }

    override fun tapped(m: ReaderMessage) {
        if (selection != null || openMark != null) return dismissCards()
        if (lookup != null) return closeLookup()
        if (chromeShown) {
            chromeShown = false
            return
        }
        m.mark?.let { id -> marks.firstOrNull { it.id == id }?.let { openMark = OpenMark(it, m.x ?: 0.0, m.y ?: 0.0); return } }
        m.word?.takeIf { isForeign && m.link == null }?.let { w -> library.scope.launch { look(w) }; return }
        m.link?.let { link ->
            link.optString("target").takeIf { it.isNotEmpty() }?.let { return follow(it) }
            link.optString("external").takeIf { it.isNotEmpty() }?.let { url ->
                if (url.startsWith("http") || url.startsWith("mailto")) {
                    runCatching { context.startActivity(android.content.Intent(android.content.Intent.ACTION_VIEW, android.net.Uri.parse(url)).addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)) }
                }
                return
            }
        }
        val w = m.width ?: return
        val x = m.x ?: return
        // 点哪里翻页: the outer thirds turn, the middle shows the controls.
        when {
            settings.mode == LayoutMode.SCROLLED -> toggleChrome()
            x < w * 0.3 -> turn(false)
            x > w * 0.7 -> turn(true)
            else -> toggleChrome()
        }
    }

    private fun toggleChrome() {
        chromeShown = !chromeShown
        barsAway = false
    }

    override fun barsAway(away: Boolean) {
        if (searchShown || away == barsAway) return
        barsAway = away
        if (away) {
            chromeComesBack = chromeShown && !immersive
            chromeShown = false
        } else {
            if (chromeComesBack) chromeShown = true
            chromeComesBack = false
        }
    }

    private fun follow(target: String) {
        val parts = target.split('#', limit = 2)
        val s = parts[0].toIntOrNull() ?: return
        leaving()
        turnedByHand(pager)
        pager.show(s, if (parts.size > 1) Target.Anchor(parts[1], "third") else Target.Start)
    }

    override fun inkReached(page: Int) {
        if (following && isPlaying) pager.showPage(page, animated = true)
    }

    override fun selected(m: ReaderMessage) {
        val start = m.start
        val end = m.end
        val text = m.text
        // 正文模式: words chosen inside a quote offer 谁说的 on the card.
        contextLine = null
        if (start != null && !bubbles) {
            val lines = runCatching { core.sectionBubbles(bookID, section.toUInt()) }.getOrDefault(emptyList())
            val s = start.s.toUInt()
            val o = start.o.toUInt()
            lines.firstOrNull { l ->
                (s > l.first || (s == l.first && o >= l.lineStart)) && (s < l.last || (s == l.last && o < l.end))
            }?.let { contextLine = SpeakerPick(section.toUInt(), it, (m.rect?.x ?: 0.0) + (m.rect?.width ?: 0.0) / 2, (m.rect?.y ?: 0.0) + (m.rect?.height ?: 0.0)) }
        }
        selection = if (start != null && end != null && !text.isNullOrBlank()) TextSelection(section, start, end, text, m.rect) else null
        if (selection != null) openMark = null
    }

    /** 循环到这: from the sentence being read to this one, over and over. */
    fun loopTo(section: Int, sentence: Int) {
        player?.loopTo(section, sentence)
        dismissCards()
    }

    /** 对准 shows: the recording is being heard now. */
    val canPin: Boolean get() = player?.recordingNow() != null && library.recordings.audioFor(bookID) != null

    /** 从这里对准: the sentence chosen starts at the moment of the recording heard now; the
     *  lay-out is made again around it, and the pin syncs. */
    fun pinHere(section: Int, sentence: Int) {
        val at = player?.recordingNow() ?: return
        val audio = library.recordings.audioFor(bookID) ?: return
        dismissCards()
        library.scope.launch(kotlinx.coroutines.Dispatchers.IO) {
            runCatching { core.audiobookPin(audio, section.toUInt(), sentence.toUInt(), at) }
            library.recordings.mapChanged(bookID)
        }
        show(app.yuyue.library.Library.Toast(app.yuyue.library.Library.Toast.Icon.Check, t("对准了"), t("这一句从这里开始，前后几句跟着重排")))
    }

    /** 谁说的: the card for that line, where the words were. */
    fun pickSpeakerFromSelection() {
        val c = contextLine ?: return
        dismissCards()
        bubble(c.line, c.section.toInt(), null, c.x, c.y)
    }

    override fun doubleTapped(m: ReaderMessage) {
        // 双击屏幕边缘进出沉浸, as on iPhone: the outer eighth on either side, where no words are.
        val view = pager.current?.webView ?: return
        val width = view.width / view.resources.displayMetrics.density
        val x = m.x
        if (x != null && width > 0 && (x < width * 0.12 || x > width * 0.88)) {
            chromeShown = false
            immersive = !immersive
            return
        }
        val sentence = m.sentence ?: return
        val section = pager.current?.section ?: return
        if (!isForeign) return
        dismissCards()
        closeLookup()
        val sentences = runCatching { core.sentences(bookID, section.toUInt()) }.getOrDefault(emptyList())
        val text = sentences.getOrNull(sentence)?.trim().orEmpty()
        if (text.isNotEmpty()) translatingSentence = text
    }

    override fun sectionReady(page: SectionPage) {
        player?.spot?.takeIf { it.section == page.section && player.state != app.yuyue.speech.Player.State.Stopped }?.let {
            page.call("yy.setActive(${it.sentence}, {durationMs: 0, elapsedMs: 0, playing: false})")
        }
    }

    companion object {
        private const val STYLE_KEY = "reader.markStyle"

        fun chapterTitle(d: BookDetail, section: Int): String? =
            d.toc.lastOrNull { it.section.toInt() <= section }?.title ?: d.sections.getOrNull(section)?.title
    }
}

/** The CSS name a style has in the reader (`::highlight(yy-yellow)`). */
val MarkStyle.css: String
    get() = when (this) {
        MarkStyle.YELLOW -> "yellow"
        MarkStyle.BLUE -> "blue"
        MarkStyle.PINK -> "pink"
        MarkStyle.WAVE -> "wave"
        MarkStyle.UNDERLINE -> "underline"
        MarkStyle.DOTTED -> "dotted"
    }

/** The reader's `applySettings` for these settings (contract §4). */
fun ReaderSettings.script(g: uniffi.yuyue_core.PageGeometry): JSONObject = JSONObject().apply {
    val resolved = PaperAppearance.resolve(this@script)
    put("paper", when (resolved.preset) { Paper.PLAIN -> "plain"; Paper.DARK -> "dark"; Paper.KAMI -> "kami"; Paper.KAMI_NIGHT -> "kaminight"; Paper.SAGE -> "sage"; Paper.EINK -> "eink" })
    val mine = resolved.custom
    put("paperStyle", mine?.let { uniffi.yuyue_core.paperStyle(it) } ?: JSONObject.NULL)
    put("texture", mine?.texture?.css ?: JSONObject.NULL)
    put("mode", if (mode == LayoutMode.SCROLLED) "scrolled" else "paginated")
    put("fontSize", fontSize)
    put("lineHeight", lineHeight ?: JSONObject.NULL)
    put("paragraphSpacing", paragraphSpacing ?: JSONObject.NULL)
    put("justify", justify ?: JSONObject.NULL)
    put("indent", indent ?: JSONObject.NULL)
    put("compress", compress)
    put("font", cssFont() ?: JSONObject.NULL)
    put("margin", g.margin)
    put("top", g.top)
    put("bottom", g.bottom)
    put("motion", if (g.reducedMotion) "reduced" else "full")
    put("eink", EInk.on)
    put("einkGray", EInk.grayscaleImages)
}

/** Matches `document.rs` + `fonts.rs`: `fontFace` CSS family first (when set), followed by the
 *  built-in fallback stack, or the built-in keyword alone. */
fun ReaderSettings.cssFont(): String? {
    val face = fontFace?.takeIf { it.isNotEmpty() }
    val family = face?.let { raw ->
        fun quote(s: String) = "\"${s.filterNot { it in "\"\\<>;{}" }}\""
        when {
            raw.startsWith("local:") -> raw.removePrefix("local:").trim().takeIf { it.isNotEmpty() }?.let(::quote)
            raw.startsWith("file:") -> raw.removePrefix("file:").takeIf {
                it.isNotEmpty() && !it.startsWith(".") && it.none { c -> c in "/\\\"'" } &&
                    it.substringAfterLast('.', "").lowercase() in setOf("ttf", "otf", "ttc")
            }?.let { quote("yy-$raw") }
            raw == "notoserif" || raw == "notosans" ||
                runCatching { FontShelf.catalog }.getOrDefault(emptyList()).any { it.id == raw && it.id != "wenkai" } -> quote("yy-$raw")
            else -> null
        }
    }
    if (family != null) {
        val fallback = when (font) {
            Font.SANS -> "sans"
            Font.SERIF -> "serif"
            Font.KAI -> "kaiti"
            Font.WEN_KAI -> "wenkai"
            null -> "p-font"
        }
        return "$family, var(--yy-$fallback)"
    }
    return when (font) {
        Font.SANS -> "sans"
        Font.SERIF -> "serif"
        Font.KAI -> "kai"
        Font.WEN_KAI -> "wenkai"
        null -> null
    }
}

/** A message on the reading capsule with something to do. */
class CapsuleAlert(val title: String?, val note: String, val action: () -> Unit)

/** 回到昨晚: the timer stopped the voice; where it had been set. */
data class SleepReturn(val spot: app.yuyue.speech.Player.Spot, val chapter: String, val at: String, val ms: Long) {
    /** `昨晚`, `刚才`, or a date. */
    val whenText: String
        get() {
            val then = java.time.Instant.ofEpochMilli(ms).atZone(java.time.ZoneId.systemDefault())
            val today = java.time.LocalDate.now()
            return when (then.toLocalDate()) {
                today -> t("刚才")
                today.minusDays(1) -> if (then.hour >= 18) t("昨晚") else t("昨天")
                else -> t("%lld 月 %lld 日", then.monthValue, then.dayOfMonth)
            }
        }

    companion object {
        /** `第八回`, from `第八回 比通靈金鶯微露意 …`. */
        fun short(title: String): String {
            val first = title.split(' ', '\u3000').firstOrNull() ?: title
            return if (first.length < title.length && first.length <= 8) first else title.take(12)
        }
    }
}
