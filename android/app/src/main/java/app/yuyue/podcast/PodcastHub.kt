package app.yuyue.podcast

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.media.AudioAttributes as PlatformAudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMetadataRetriever
import android.media.MediaMuxer
import android.media.PlaybackParams
import android.net.Uri
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.speech.RecognitionListener
import android.speech.RecognizerIntent
import android.speech.SpeechRecognizer
import android.util.LruCache
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableDoubleStateOf
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.SimpleBasePlayer
import androidx.media3.common.util.UnstableApi
import androidx.media3.exoplayer.ExoPlayer
import app.yuyue.L10n
import app.yuyue.MainActivity
import app.yuyue.companion.Companion
import app.yuyue.companion.CompanionConfig
import app.yuyue.design.Icons
import app.yuyue.design.Prefs
import app.yuyue.design.YY
import app.yuyue.library.Library
import app.yuyue.speech.BookVoice
import app.yuyue.speech.CardVoice
import app.yuyue.speech.ClonedVoices
import app.yuyue.speech.Listener
import app.yuyue.speech.LiveVoiceModel
import app.yuyue.speech.PlaybackService
import app.yuyue.speech.Session
import app.yuyue.speech.Speech
import app.yuyue.speech.SystemVoice
import app.yuyue.speech.Voices
import app.yuyue.t
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import org.json.JSONArray
import org.json.JSONObject
import uniffi.yuyue_core.MagazineSentence
import uniffi.yuyue_core.PodcastChannel
import uniffi.yuyue_core.PodcastClip
import uniffi.yuyue_core.PodcastEdition
import uniffi.yuyue_core.PodcastFidelity
import uniffi.yuyue_core.PodcastInterrupt
import uniffi.yuyue_core.PodcastIssue
import uniffi.yuyue_core.PodcastPlacement
import uniffi.yuyue_core.PodcastSourceKind
import uniffi.yuyue_core.PodcastTrack
import uniffi.yuyue_core.StudioKind
import uniffi.yuyue_core.parseEpisodeWebpage
import uniffi.yuyue_core.parsePodcastOpml
import uniffi.yuyue_core.parsePodcastRss
import uniffi.yuyue_core.parsePodcastTranscript
import uniffi.yuyue_core.resolveClipWindow
import java.io.File
import java.net.URLEncoder
import java.nio.ByteBuffer
import java.util.Locale
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlin.coroutines.coroutineContext
import kotlin.coroutines.resume
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.roundToLong
import androidx.media3.common.Player as Media3Player

data class PodcastSearchHit(
    val id: String,
    val title: String,
    val author: String,
    val feedURL: String,
    val artworkURL: String? = null,
    val genre: String? = null,
    val episodeCount: Int? = null,
)

data class PodcastVoiceOption(
    val id: String,
    val title: String,
    val subtitle: String,
    val isCloned: Boolean = false,
)

val StudioKind.title: String
    get() = when (this) {
        StudioKind.WEEKLY_DIGEST -> t("书房周刊")
        StudioKind.BOOK_GUIDE -> t("读前导读")
        StudioKind.ROUNDTABLE -> t("跨书圆桌")
        StudioKind.VOCAB_RADIO -> t("生词伴听")
    }

val StudioKind.subtitle: String
    get() = when (this) {
        StudioKind.WEEKLY_DIGEST -> t("串联本周阅读与划线")
        StudioKind.BOOK_GUIDE -> t("读前热身精讲 · 拆解核心脉络")
        StudioKind.ROUNDTABLE -> t("多本书同桌交锋对谈")
        StudioKind.VOCAB_RADIO -> t("把生词自然融进通勤对话")
    }

object PodcastFormat {
    fun clock(sec: Double): String {
        val total = max(0, sec.roundToInt())
        val h = total / 3600
        val m = (total % 3600) / 60
        val s = total % 60
        return if (h > 0) String.format(Locale.US, "%d:%02d:%02d", h, m, s)
        else String.format(Locale.US, "%02d:%02d", m, s)
    }

    fun compactDuration(durationSec: UInt): String {
        val sec = durationSec.toInt()
        if (sec <= 0) return t("待播")
        val mins = max(1, (sec + 30) / 60)
        return if (mins < 60) t("%lld 分", mins)
        else {
            val h = mins / 60
            val m = mins % 60
            if (m == 0) t("%lld 小时", h) else t("%lld 小时 %lld 分", h, m)
        }
    }

    fun remaining(position: Double, duration: Double): String {
        if (duration <= 0) return ""
        val left = max(0.0, duration - position)
        if (left < 45) return t("即将听完")
        val mins = max(1, (left / 60.0).roundToInt())
        return if (position <= 3.0) t("约 %lld 分钟", mins) else t("还剩 %lld 分钟", mins)
    }

    fun trackLabel(track: PodcastTrack, language: String = "en"): String = when (track) {
        PodcastTrack.ORIGINAL -> if (language.lowercase().startsWith("en")) t("英文原声") else t("原声")
        PodcastTrack.DUB -> t("原声线中文")
        PodcastTrack.COACH -> t("双语助教")
    }

    fun sentenceText(sentence: MagazineSentence, track: PodcastTrack): String = when (track) {
        PodcastTrack.ORIGINAL -> sentence.textOrig
        PodcastTrack.DUB -> sentence.textTrans?.takeIf { it.isNotBlank() } ?: sentence.textOrig
        PodcastTrack.COACH -> {
            val trans = sentence.textTrans?.takeIf { it.isNotBlank() }
            if (trans != null && trans != sentence.textOrig) "${sentence.textOrig}\n$trans" else sentence.textOrig
        }
    }

    fun skipReasonLabel(reason: String?): String = when (reason?.lowercase()) {
        "ad", "sponsor" -> t("赞助口播")
        "intro" -> t("片头寒暄")
        "outro" -> t("片尾致谢")
        else -> t("闲聊段落")
    }
}

/** Lightweight remote artwork loader with an in-memory LRU cache and warm stationery fallback. */
object PodcastArtwork {
    private val cache = object : LruCache<String, ImageBitmap>(64) {}
    private val failed = mutableSetOf<String>()
    private val http = OkHttpClient.Builder()
        .connectTimeout(12, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    fun cached(url: String?): ImageBitmap? {
        val key = url?.trim()?.takeIf { it.startsWith("http") } ?: return null
        synchronized(cache) { return cache.get(key) }
    }

    suspend fun load(url: String?, targetPx: Int = 256): ImageBitmap? = withContext(Dispatchers.IO) {
        val key = url?.trim()?.takeIf { it.startsWith("http") } ?: return@withContext null
        synchronized(cache) {
            cache.get(key)?.let { return@withContext it }
            if (key in failed) return@withContext null
        }
        runCatching {
            http.newCall(Request.Builder().url(key).header("User-Agent", "YuyuePodcast/1.0").build()).execute().use { r ->
                if (!r.isSuccessful) return@runCatching null
                val bytes = r.body?.bytes() ?: return@runCatching null
                val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
                var sample = 1
                val longest = max(bounds.outWidth, bounds.outHeight)
                while (longest / (sample * 2) >= targetPx) sample *= 2
                val opts = BitmapFactory.Options().apply { inSampleSize = sample }
                BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)?.asImageBitmap()
            }
        }.getOrNull().also { bmp ->
            synchronized(cache) {
                if (bmp != null) cache.put(key, bmp) else failed.add(key)
            }
        }
    }
}

@Composable
fun PodcastShowMark(
    title: String,
    artworkURL: String? = null,
    isStudio: Boolean = false,
    size: Dp = 32.dp,
    modifier: Modifier = Modifier,
) {
    val radius = (size.value * 0.27f).coerceIn(5f, 12f).dp
    val shape = RoundedCornerShape(radius)
    var bitmap by remember(artworkURL) { mutableStateOf(PodcastArtwork.cached(artworkURL)) }
    LaunchedEffect(artworkURL) {
        if (bitmap == null && !artworkURL.isNullOrBlank()) {
            bitmap = PodcastArtwork.load(artworkURL)
        }
    }
    if (bitmap != null && !isStudio) {
        Image(
            bitmap = bitmap!!,
            contentDescription = title,
            contentScale = ContentScale.Crop,
            modifier = modifier
                .size(size)
                .clip(shape)
                .border(0.5.dp, YY.c.line, shape),
        )
        return
    }
    val palettes = if (YY.dark) {
        listOf(
            Color(0xFF2D2722) to Color(0xFFD6B48E),
            Color(0xFF232D26) to Color(0xFF8EC29E),
            Color(0xFF242B34) to Color(0xFF9AB6D3),
            Color(0xFF322628) to Color(0xFFD49AA2),
        )
    } else {
        listOf(
            Color(0xFFF2E7DB) to Color(0xFF7B5734),
            Color(0xFFE3ECE5) to Color(0xFF3E6848),
            Color(0xFFE2EAF2) to Color(0xFF426280),
            Color(0xFFF2E3E5) to Color(0xFF8F525C),
        )
    }
    val (bg, fg) = if (isStudio) {
        YY.tint.accentSoft to YY.tint.accent
    } else {
        val idx = (title.hashCode() and 0x7FFFFFFF) % palettes.size
        palettes[idx]
    }
    Box(
        modifier = modifier
            .size(size)
            .clip(shape)
            .background(bg)
            .border(0.5.dp, if (isStudio) YY.tint.accent.copy(alpha = 0.25f) else YY.c.line, shape),
        contentAlignment = Alignment.Center,
    ) {
        if (isStudio) {
            Icon(Icons.book, contentDescription = null, tint = fg, modifier = Modifier.size(size * 0.52f))
        } else {
            val glyph = title.trim().firstOrNull { !it.isWhitespace() }?.uppercaseChar()?.toString() ?: "播"
            Text(
                glyph,
                fontFamily = YY.serif,
                fontWeight = FontWeight.SemiBold,
                fontSize = (size.value * 0.44f).sp,
                color = fg,
            )
        }
    }
}

/**
 * Android Podcast & Private Studio coordinator ("收听台 + 书房电台"):
 * - External RSS / webpage / OPML / local audio playback via Media3 ExoPlayer.
 * - Private Studio ("书房电台") & cross-lingual `.DUB` playback via Yuyue's on-device `CardVoice` / `ClonedVoices` / `SystemVoice` with parallel next-sentence prefetching.
 * - Live microphone cut-in interjection ("语音插话") via Android `SpeechRecognizer` with live RMS waveform levels, audio ducking, and 3-second rewind on resume.
 * - Blind clip marking ("原声划线") & on-device SenseVoice transcription (`Listener`) for local audio.
 */
@androidx.annotation.OptIn(UnstableApi::class)
@Stable
class PodcastHub(
    private val context: Context,
    private val library: Library,
) {
    private val core get() = library.core
    private val scope get() = library.scope
    private val mainHandler = Handler(Looper.getMainLooper())

    // MARK: - Observable State

    var channels by mutableStateOf<List<PodcastChannel>>(emptyList())
        private set
    var deskIssues by mutableStateOf<List<PodcastIssue>>(emptyList())
        private set
    var newsstandIssues by mutableStateOf<List<PodcastIssue>>(emptyList())
        private set
    var archiveIssues by mutableStateOf<List<PodcastIssue>>(emptyList())
        private set
    var clips by mutableStateOf<List<PodcastClip>>(emptyList())
        private set

    var discoveredShows by mutableStateOf<List<PodcastSearchHit>>(emptyList())
        private set
    var isLoadingDiscovery by mutableStateOf(false)
        private set
    var subscribingFeedURLs by mutableStateOf<Set<String>>(emptySet())
        private set

    var activeIssueID by mutableStateOf<String?>(null)
        private set
    var isPlaying by mutableStateOf(false)
        private set
    var currentTime by mutableDoubleStateOf(0.0)
        private set
    var duration by mutableDoubleStateOf(0.0)
        private set
    var rate by mutableFloatStateOf(Prefs.float(PREF_RATE, 1f).coerceIn(0.6f, 2.2f))
        private set

    var isDucked by mutableStateOf(false)
        private set
    var isListeningInterrupt by mutableStateOf(false)
        private set
    var interruptVoiceLevel by mutableFloatStateOf(0f)
        private set
    var interruptVoiceHint by mutableStateOf<String?>(null)
        private set
    var isSpeakingInterruptReply by mutableStateOf(false)
        private set

    var activeSentenceID by mutableStateOf<String?>(null)
        private set
    var playingClipID by mutableStateOf<String?>(null)
        private set

    var isGeneratingStudio by mutableStateOf(false)
        private set
    var studioStatus by mutableStateOf<String?>(null)
        private set
    var isAddingFeed by mutableStateOf(false)
        private set
    var addError by mutableStateOf<String?>(null)
    var isTranscribingIssueID by mutableStateOf<String?>(null)
        private set
    var lastNotice by mutableStateOf<String?>(null)

    var studioVoiceStyle by mutableStateOf(Prefs.string(PREF_STUDIO_VOICE, "warm_duo"))
        private set
    var voiceOptions by mutableStateOf<List<PodcastVoiceOption>>(emptyList())
        private set

    /** Cross-screen request to open the full-screen `PodcastReaderScreen` for an episode. */
    var requestedOpenIssueID by mutableStateOf<String?>(null)

    // MARK: - Internal Caches & Playback Engines

    private val transcriptCache = mutableMapOf<String, List<MagazineSentence>>()
    private val highlightCache = mutableMapOf<String, Set<String>>()
    private val interruptCache = mutableMapOf<String, List<PodcastInterrupt>>()
    private var cacheVersion by mutableStateOf(0)

    private val http = OkHttpClient.Builder()
        .connectTimeout(18, TimeUnit.SECONDS)
        .readTimeout(45, TimeUnit.SECONDS)
        .followRedirects(true)
        .build()

    private var exo: ExoPlayer? = null
    private var pollJob: Job? = null
    private var spokenJob: Job? = null
    private var spokenTrack: AudioTrack? = null
    private var replyJob: Job? = null
    private var replyTrack: AudioTrack? = null
    private var clipJob: Job? = null
    private var clipPlayer: ExoPlayer? = null
    private var speechRecognizer: SpeechRecognizer? = null
    private var wasPlayingBeforeInterrupt = false
    private var lastSavedSec = -999.0

    val allIssues: List<PodcastIssue>
        get() = deskIssues + newsstandIssues + archiveIssues

    val studioIssues: List<PodcastIssue>
        get() = allIssues.filter { it.studioKind != null || it.source == PodcastSourceKind.STUDIO }

    val activeIssue: PodcastIssue?
        get() {
            val id = activeIssueID
            if (id != null) issue(id)?.let { return it }
            return deskIssues.firstOrNull() ?: newsstandIssues.firstOrNull()
        }

    val sessionPlayer: PodcastSessionPlayer by lazy { PodcastSessionPlayer() }

    init {
        reload(eagerTranscript = false)
        refreshVoiceOptions()
    }

    fun startService() {
        runCatching { context.startService(Intent(context, PlaybackService::class.java)) }
    }

    // MARK: - Reload & Queries

    fun reload(eagerTranscript: Boolean = false) {
        channels = runCatching { core.podcastChannels() }.getOrDefault(emptyList())
        deskIssues = runCatching { core.podcastIssues(PodcastPlacement.DESK) }.getOrDefault(emptyList())
        newsstandIssues = runCatching { core.podcastIssues(PodcastPlacement.NEWSSTAND) }.getOrDefault(emptyList())
        archiveIssues = runCatching { core.podcastIssues(PodcastPlacement.ARCHIVE) }.getOrDefault(emptyList())
        clips = runCatching { core.podcastClips(null) }.getOrDefault(emptyList())

        if (activeIssueID == null) {
            val first = deskIssues.firstOrNull()
            if (first != null) {
                activeIssueID = first.id
                currentTime = first.positionSec
                duration = first.durationSec.toDouble()
                if (eagerTranscript) {
                    ensureTranscriptLoaded(first.id)
                    updateActiveSentence()
                }
            }
        } else {
            val cur = issue(activeIssueID!!)
            if (cur == null) {
                stopPlayback()
                activeIssueID = deskIssues.firstOrNull()?.id
            } else if (!isPlaying) {
                duration = max(duration, cur.durationSec.toDouble())
            }
        }
        cacheVersion++
        sessionPlayer.refresh()
    }

    fun refreshVoiceOptions() {
        val options = mutableListOf(
            PodcastVoiceOption("warm_duo", t("书房双人 · 温润对谈"), t("男女双声道自然交替，适合导读与圆桌")),
            PodcastVoiceOption("calm_solo", t("夜读单人 · 沉静主理"), t("舒缓叙述，适合睡前书房周刊")),
        )
        if (LiveVoiceModel.canRun(library.dataDir)) {
            val cards = Voices.recommendedCards(core, library.dataDir, if (L10n.chinese) "zh-Hans" else "en")
            cards.take(4).forEach { card ->
                options += PodcastVoiceOption(
                    id = "card:${card.id}",
                    title = card.name,
                    subtitle = t("本机声音卡 · 离线自然合成"),
                )
            }
            val clonedCards = runCatching { core.cardsCloned() }.getOrDefault(emptyList())
            clonedCards.forEach { card ->
                options += PodcastVoiceOption(
                    id = "card:${card.id}",
                    title = card.name,
                    subtitle = t("我的克隆声音 · 本机离线"),
                    isCloned = true,
                )
            }
        }
        ClonedVoices.all().filter { it.isReady }.forEach { cv ->
            if (options.none { it.id == "cloned:${cv.id}" || it.title == cv.name }) {
                options += PodcastVoiceOption(
                    id = "cloned:${cv.id}",
                    title = cv.name,
                    subtitle = t("克隆声音 · ElevenLabs"),
                    isCloned = true,
                )
            }
        }
        voiceOptions = options
    }

    fun selectStudioVoiceStyle(styleId: String) {
        studioVoiceStyle = styleId
        Prefs.set(PREF_STUDIO_VOICE, styleId)
    }

    fun issue(id: String): PodcastIssue? =
        allIssues.firstOrNull { it.id == id } ?: runCatching { core.podcastIssue(id) }.getOrNull()

    fun sentences(issueID: String): List<MagazineSentence> {
        @Suppress("UNUSED_EXPRESSION") cacheVersion
        return transcriptCache[issueID] ?: ensureTranscriptLoaded(issueID)
    }

    private fun ensureTranscriptLoaded(issueID: String): List<MagazineSentence> {
        transcriptCache[issueID]?.let { return it }
        val loaded = runCatching { core.podcastTranscript(issueID) }.getOrDefault(emptyList())
        transcriptCache[issueID] = loaded
        highlightCache[issueID] = runCatching { core.podcastHighlightSentenceIds(issueID).toSet() }.getOrDefault(emptySet())
        return loaded
    }

    fun highlightSet(issueID: String): Set<String> {
        @Suppress("UNUSED_EXPRESSION") cacheVersion
        if (issueID !in highlightCache) ensureTranscriptLoaded(issueID)
        return highlightCache[issueID].orEmpty()
    }

    fun interrupts(issueID: String): List<PodcastInterrupt> {
        @Suppress("UNUSED_EXPRESSION") cacheVersion
        return interruptCache.getOrPut(issueID) {
            runCatching { core.podcastInterrupts(issueID) }.getOrDefault(emptyList())
        }
    }

    fun clipsForIssue(issueID: String): List<PodcastClip> =
        clips.filter { it.issueId == issueID }

    fun activeSentence(issueID: String): MagazineSentence? {
        val list = sentences(issueID).filter { it.skipReason == null }
        if (list.isEmpty()) return null
        if (activeIssueID == issueID && activeSentenceID != null) {
            list.firstOrNull { it.id == activeSentenceID }?.let { return it }
        }
        val pos = if (activeIssueID == issueID) currentTime else (issue(issueID)?.positionSec ?: 0.0)
        return list.firstOrNull { pos >= it.t0 && pos <= it.t1 }
            ?: list.lastOrNull { pos >= it.t0 }
            ?: list.firstOrNull()
    }

    /** Only expose 3-way track switch when the episode actually carries cross-lingual translations! */
    fun supportsTrackSwitch(issue: PodcastIssue): Boolean {
        if (issue.studioKind != null || issue.source == PodcastSourceKind.STUDIO) return false
        if (issue.fidelity == PodcastFidelity.L3_CROSS_LINGUAL) return true
        val list = transcriptCache[issue.id] ?: return false
        return list.any { !it.textTrans.isNullOrBlank() && it.textTrans != it.textOrig }
    }

    fun setPlacement(issueID: String, placement: PodcastPlacement) {
        runCatching { core.podcastSetPlacement(issueID, placement) }
        reload()
    }

    fun deleteIssue(issueID: String) {
        if (activeIssueID == issueID) stopPlayback()
        transcriptCache.remove(issueID)
        highlightCache.remove(issueID)
        interruptCache.remove(issueID)
        runCatching { core.podcastDeleteIssue(issueID) }
        reload()
    }

    fun deleteClip(clipID: String) {
        if (playingClipID == clipID) stopClip()
        runCatching { core.podcastDeleteClip(clipID) }
        clips = runCatching { core.podcastClips(null) }.getOrDefault(emptyList())
    }

    fun setViewMode(issueID: String, edition: PodcastEdition, track: PodcastTrack) {
        runCatching { core.podcastSetViewMode(issueID, edition, track) }
        reload()
    }

    fun setTrack(newTrack: PodcastTrack, issue: PodcastIssue) {
        val wasPlayingHere = activeIssueID == issue.id && isPlaying
        val at = if (activeIssueID == issue.id) currentTime else issue.positionSec
        runCatching { core.podcastSetViewMode(issue.id, issue.edition, newTrack) }
        reload()
        val updated = issue(issue.id) ?: return
        if (wasPlayingHere) {
            start(updated, from = at)
        }
    }

    // MARK: - Playback Engine (ExoPlayer + On-Device Studio/Dub Voice Engine)

    private fun usesSpokenEngine(issue: PodcastIssue): Boolean {
        if (issue.studioKind != null || issue.source == PodcastSourceKind.STUDIO) return true
        if (issue.audioUrl.isNullOrBlank() && issue.localAudioPath.isNullOrBlank()) return true
        if (issue.track == PodcastTrack.DUB && supportsTrackSwitch(issue)) return true
        return false
    }

    fun selectAndPlay(issue: PodcastIssue) {
        if (activeIssueID == issue.id) {
            togglePlay()
        } else {
            start(issue, from = issue.positionSec)
        }
    }

    fun togglePlay() {
        val cur = activeIssue ?: return
        if (isPlaying) {
            pause()
        } else {
            start(cur, from = if (activeIssueID == cur.id) currentTime else cur.positionSec)
        }
    }

    fun start(issue: PodcastIssue, from: Double = issue.positionSec) {
        stopClip()
        stopSpeakingInterruptAnswer()
        // Yield any open book / audiobook playback cleanly via global Session
        (library.nowPlaying as? app.yuyue.reader.ReaderModel)?.player?.pause()
        (library.nowPlaying as? app.yuyue.speech.AudiobookPlayer)?.pause()
        Session.claimPodcast(this)

        activeIssueID = issue.id
        isDucked = false
        val list = ensureTranscriptLoaded(issue.id)
        val estDur = max(
            issue.durationSec.toDouble(),
            list.maxOfOrNull { it.t1 } ?: 0.0
        )
        duration = estDur
        val safeStart = if (estDur > 4.0 && from >= estDur - 1.5) 0.0 else max(0.0, from)
        currentTime = safeStart
        updateActiveSentence()

        if (issue.placement == PodcastPlacement.NEWSSTAND) {
            runCatching { core.podcastSetPlacement(issue.id, PodcastPlacement.DESK) }
            reload()
        }

        if (usesSpokenEngine(issue)) {
            stopExo()
            startSpokenEngine(issue, safeStart)
        } else {
            stopSpokenEngine()
            startExo(issue, safeStart)
        }
        sessionPlayer.refresh()
    }

    private fun ensureExo(): ExoPlayer {
        exo?.let { return it }
        return ExoPlayer.Builder(context)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_SPEECH)
                    .build(),
                true
            )
            .setHandleAudioBecomingNoisy(true)
            .setSeekBackIncrementMs(15_000L)
            .setSeekForwardIncrementMs(15_000L)
            .build()
            .also { player ->
                player.addListener(object : Media3Player.Listener {
                    override fun onIsPlayingChanged(playing: Boolean) {
                        if (activeIssue?.let { !usesSpokenEngine(it) } == true) {
                            isPlaying = playing
                            if (playing) startPolling() else {
                                pollJob?.cancel()
                                saveCurrentProgress(force = true)
                            }
                            sessionPlayer.refresh()
                        }
                    }

                    override fun onPlaybackStateChanged(playbackState: Int) {
                        if (playbackState == Media3Player.STATE_READY && player.duration > 0) {
                            duration = player.duration / 1000.0
                        } else if (playbackState == Media3Player.STATE_ENDED) {
                            isPlaying = false
                            pollJob?.cancel()
                            currentTime = duration
                            saveCurrentProgress(force = true)
                            sessionPlayer.refresh()
                        }
                    }
                })
                exo = player
            }
    }

    private fun startExo(issue: PodcastIssue, fromSec: Double) {
        val uri = issue.localAudioPath?.takeIf { File(it).isFile }?.let { Uri.fromFile(File(it)) }
            ?: issue.audioUrl?.takeIf { it.isNotBlank() }?.let { Uri.parse(it) }
        if (uri == null) {
            lastNotice = t("这期节目没有可播放的音频源")
            isPlaying = false
            return
        }
        val player = ensureExo()
        val currentUri = player.currentMediaItem?.localConfiguration?.uri
        if (currentUri != uri) {
            val meta = MediaMetadata.Builder()
                .setTitle(issue.title)
                .setArtist(issue.showTitle)
                .setAlbumTitle(issue.showTitle)
                .apply { issue.artworkUrl?.let { setArtworkUri(Uri.parse(it)) } }
                .build()
            val item = MediaItem.Builder().setMediaId(issue.id).setUri(uri).setMediaMetadata(meta).build()
            player.setMediaItem(item)
            player.prepare()
        }
        player.volume = if (isDucked) 0.065f else 1f
        player.playbackParameters = PlaybackParameters(rate)
        player.seekTo((fromSec * 1000.0).roundToLong())
        player.playWhenReady = true
        isPlaying = true
        startPolling()
    }

    private fun startPolling() {
        pollJob?.cancel()
        pollJob = scope.launch {
            while (isActive) {
                val p = exo
                if (p != null && p.isPlaying) {
                    currentTime = p.currentPosition / 1000.0
                    if (p.duration > 0) duration = p.duration / 1000.0
                    updateActiveSentence()
                    saveCurrentProgress(force = false)
                }
                delay(250L)
            }
        }
    }

    private fun stopExo() {
        pollJob?.cancel()
        pollJob = null
        exo?.playWhenReady = false
        exo?.stop()
    }

    private fun startSpokenEngine(issue: PodcastIssue, fromSec: Double) {
        stopSpokenEngine()
        val all = sentences(issue.id).filter { it.skipReason == null }
        if (all.isEmpty()) {
            lastNotice = t("这期节目还没有可朗读的文稿")
            isPlaying = false
            return
        }
        val startIndex = all.indexOfFirst { it.t1 > fromSec + 0.15 }.let { if (it < 0) 0 else it }
        isPlaying = true
        spokenJob = scope.launch {
            SystemVoice.Engine.init(context)
            if (LiveVoiceModel.state is LiveVoiceModel.State.Idle) {
                LiveVoiceModel.onFirstListen(context, core, library.dataDir)
            }
            val lang = if (issue.track == PodcastTrack.DUB) "zh-Hans" else issue.language.ifBlank { "zh-Hans" }
            val (hostA, hostB) = resolveStudioVoicePair(lang)
            val speakers = LinkedHashSet<String>()

            fun voiceFor(s: MagazineSentence): BookVoice? {
                val sp = s.speaker?.trim()?.takeIf { it.isNotEmpty() } ?: "A"
                speakers.add(sp)
                val idx = speakers.indexOf(sp)
                return if (idx % 2 == 0) hostA else (hostB ?: hostA)
            }

            fun spokenLine(s: MagazineSentence): String =
                if (issue.track == PodcastTrack.DUB) s.textTrans?.takeIf { it.isNotBlank() } ?: s.textOrig
                else s.textOrig

            var prefetched: Deferred<Speech?>? = async {
                voiceFor(all[startIndex])?.render(spokenLine(all[startIndex]), lang)
            }

            try {
                for (i in startIndex until all.size) {
                    ensureActive()
                    val sentence = all[i]
                    currentTime = sentence.t0
                    activeSentenceID = sentence.id
                    saveCurrentProgress(force = false)
                    sessionPlayer.refresh()

                    val nextDeferred = if (i + 1 < all.size) {
                        val nextS = all[i + 1]
                        async { voiceFor(nextS)?.render(spokenLine(nextS), lang) }
                    } else null

                    val speech = prefetched?.await()
                    prefetched = nextDeferred

                    ensureActive()
                    if (speech != null && speech.samples.isNotEmpty()) {
                        playPcmSentence(speech, sentence.t0, sentence.t1)
                    } else {
                        delay(450L)
                    }
                    // Brief natural breath between turns
                    delay(90L)
                }
                if (isActive) {
                    currentTime = duration
                    isPlaying = false
                    saveCurrentProgress(force = true)
                    sessionPlayer.refresh()
                }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                isPlaying = false
                sessionPlayer.refresh()
            }
        }
    }

    private suspend fun resolveStudioVoicePair(language: String): Pair<BookVoice?, BookVoice?> {
        val style = studioVoiceStyle
        if (style.startsWith("card:")) {
            val id = style.removePrefix("card:")
            val chosen = Voices.find(id)
            val fallback = Voices.recommendedCards(core, library.dataDir, language)
                .firstOrNull { it.id != id }
                ?.let { CardVoice(it) }
            if (chosen != null) return chosen to (fallback ?: chosen)
        } else if (style.startsWith("cloned:")) {
            val id = style.removePrefix("cloned:")
            val chosen = ClonedVoices.find(id)?.takeIf { it.isReady } ?: Voices.find(id)
            val partner = Voices.best(language)
            if (chosen != null) return chosen to (partner ?: chosen)
        }
        if (LiveVoiceModel.canRun(library.dataDir)) {
            val cards = Voices.recommendedCards(core, library.dataDir, language)
            if (cards.isNotEmpty()) {
                val primary = CardVoice(cards[0])
                if (style == "calm_solo" || cards.size == 1) return primary to primary
                val secondary = cards.firstOrNull { it.id != cards[0].id }?.let { CardVoice(it) } ?: primary
                return primary to secondary
            }
        }
        val sys = SystemVoice.Engine.voices().filter { it.speaks(language) }
        if (sys.isNotEmpty()) {
            val a = sys[0]
            val b = if (style == "calm_solo" || sys.size == 1) a else sys[1]
            return a to b
        }
        val best = Voices.best(language)
        return best to best
    }

    private suspend fun playPcmSentence(speech: Speech, t0: Double, t1: Double) {
        val track = AudioTrack.Builder()
            .setAudioAttributes(
                PlatformAudioAttributes.Builder()
                    .setUsage(PlatformAudioAttributes.USAGE_MEDIA)
                    .setContentType(PlatformAudioAttributes.CONTENT_TYPE_SPEECH)
                    .build()
            )
            .setAudioFormat(
                AudioFormat.Builder()
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setSampleRate(speech.rate)
                    .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                    .build()
            )
            .setTransferMode(AudioTrack.MODE_STATIC)
            .setBufferSizeInBytes(max(2, speech.samples.size * 2))
            .build()

        track.write(speech.samples, 0, speech.samples.size)
        val vol = if (isDucked) 0.065f else 1f
        track.setVolume(vol)
        if (kotlin.math.abs(rate - 1f) > 0.02f) {
            runCatching {
                track.playbackParams = PlaybackParams().setSpeed(rate).setPitch(1f)
            }
        }
        spokenTrack = track
        try {
            track.play()
            val totalFrames = speech.samples.size
            val span = max(0.2, t1 - t0)
            while (coroutineContext.isActive) {
                val head = track.playbackHeadPosition
                if (head >= totalFrames) break
                val frac = (head.toDouble() / max(1, totalFrames)).coerceIn(0.0, 1.0)
                currentTime = t0 + span * frac
                delay(120L)
            }
        } finally {
            runCatching { track.stop() }
            runCatching { track.release() }
            if (spokenTrack === track) spokenTrack = null
        }
    }

    private fun stopSpokenEngine() {
        spokenJob?.cancel()
        spokenJob = null
        spokenTrack?.let {
            runCatching { it.pause() }
            runCatching { it.flush() }
            runCatching { it.release() }
        }
        spokenTrack = null
    }

    fun pause() {
        isPlaying = false
        pollJob?.cancel()
        exo?.playWhenReady = false
        stopSpokenEngine()
        saveCurrentProgress(force = true)
        sessionPlayer.refresh()
    }

    fun stopPlayback() {
        pause()
        Session.releasePodcast(this)
    }

    fun seek(toSec: Double) {
        val cur = activeIssue ?: return
        val clamped = toSec.coerceIn(0.0, max(duration, toSec))
        currentTime = clamped
        updateActiveSentence()
        saveCurrentProgress(force = true)
        if (usesSpokenEngine(cur)) {
            if (isPlaying) startSpokenEngine(cur, clamped)
        } else {
            exo?.seekTo((clamped * 1000.0).roundToLong())
        }
        sessionPlayer.refresh()
    }

    fun skip(deltaSec: Double) {
        val base = currentTime
        val upper = if (duration > 0) duration else Double.MAX_VALUE
        seek((base + deltaSec).coerceIn(0.0, upper))
    }

    fun setPlaybackRate(newRate: Float) {
        val r = newRate.coerceIn(0.6f, 2.2f)
        rate = r
        Prefs.set(PREF_RATE, r)
        exo?.playbackParameters = PlaybackParameters(r)
        spokenTrack?.let { tr ->
            runCatching { tr.playbackParams = PlaybackParams().setSpeed(r).setPitch(1f) }
        }
        sessionPlayer.refresh()
    }

    private fun updateActiveSentence() {
        val id = activeIssueID ?: return
        val list = transcriptCache[id]?.filter { it.skipReason == null } ?: return
        if (list.isEmpty()) {
            activeSentenceID = null
            return
        }
        val t = currentTime
        val match = list.firstOrNull { t >= it.t0 && t <= it.t1 }
            ?: list.lastOrNull { t >= it.t0 }
            ?: list.first()
        if (activeSentenceID != match.id) {
            activeSentenceID = match.id
        }
    }

    private fun saveCurrentProgress(force: Boolean) {
        val id = activeIssueID ?: return
        if (!force && kotlin.math.abs(currentTime - lastSavedSec) < 3.0) return
        lastSavedSec = currentTime
        val durArg = duration.takeIf { it > 1.0 }?.roundToInt()?.toUInt()
        runCatching { core.podcastSavePlayback(id, currentTime, durArg) }
    }

    // MARK: - Audio Ducking & Live Voice Cut-In ("语音插话")

    fun beginInterruptSession() {
        wasPlayingBeforeInterrupt = isPlaying
        isDucked = true
        exo?.volume = 0.065f
        spokenTrack?.setVolume(0.065f)
        // Pause spoken TTS during voice capture so microphone doesn't hear our own synthesis
        if (activeIssue?.let { usesSpokenEngine(it) } == true && isPlaying) {
            pause()
        }
    }

    fun endInterruptSession(rewindOnResume: Boolean = true) {
        stopVoiceInterrupt()
        stopSpeakingInterruptAnswer()
        isDucked = false
        exo?.volume = 1f
        spokenTrack?.setVolume(1f)
        if (wasPlayingBeforeInterrupt) {
            wasPlayingBeforeInterrupt = false
            val cur = activeIssue ?: return
            val target = if (rewindOnResume) max(0.0, currentTime - 3.0) else currentTime
            start(cur, from = target)
        }
    }

    fun hasMicPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED

    fun startVoiceInterrupt(onPartial: (String) -> Unit) {
        mainHandler.post {
            stopVoiceInterruptInternal()
            interruptVoiceHint = null
            if (!hasMicPermission()) {
                interruptVoiceHint = t("请先允许麦克风权限后再语音插话")
                return@post
            }
            if (!SpeechRecognizer.isRecognitionAvailable(context)) {
                interruptVoiceHint = t("这台手机暂不支持系统语音听写，可直接打字提问")
                return@post
            }
            beginInterruptSession()
            // Pause ExoPlayer cleanly while the mic listens so Android audio route doesn't echo
            exo?.playWhenReady = false
            isPlaying = false

            val recognizer = runCatching { SpeechRecognizer.createSpeechRecognizer(context) }.getOrNull()
            if (recognizer == null) {
                interruptVoiceHint = t("无法启动语音识别，请直接打字提问")
                return@post
            }
            speechRecognizer = recognizer
            isListeningInterrupt = true
            interruptVoiceLevel = 0.15f

            recognizer.setRecognitionListener(object : RecognitionListener {
                override fun onReadyForSpeech(params: Bundle?) {
                    interruptVoiceHint = t("正在听你说…")
                }
                override fun onBeginningOfSpeech() {}
                override fun onRmsChanged(rmsdB: Float) {
                    val norm = ((rmsdB + 2f) / 12f).coerceIn(0.08f, 1f)
                    interruptVoiceLevel = interruptVoiceLevel * 0.45f + norm * 0.55f
                }
                override fun onBufferReceived(buffer: ByteArray?) {}
                override fun onEndOfSpeech() {
                    isListeningInterrupt = false
                    interruptVoiceLevel = 0f
                }
                override fun onError(error: Int) {
                    isListeningInterrupt = false
                    interruptVoiceLevel = 0f
                    if (error != SpeechRecognizer.ERROR_CLIENT && error != SpeechRecognizer.ERROR_NO_MATCH) {
                        interruptVoiceHint = t("没听清，可再点麦克风或直接输入问题")
                    } else if (error == SpeechRecognizer.ERROR_NO_MATCH) {
                        interruptVoiceHint = t("没听清刚才那句，再试一次或打字提问")
                    }
                }
                override fun onResults(results: Bundle?) {
                    isListeningInterrupt = false
                    interruptVoiceLevel = 0f
                    val text = results?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.trim()
                    if (!text.isNullOrEmpty()) {
                        interruptVoiceHint = null
                        onPartial(text)
                    }
                }
                override fun onPartialResults(partialResults: Bundle?) {
                    val text = partialResults?.getStringArrayList(SpeechRecognizer.RESULTS_RECOGNITION)?.firstOrNull()?.trim()
                    if (!text.isNullOrEmpty()) {
                        onPartial(text)
                    }
                }
                override fun onEvent(eventType: Int, params: Bundle?) {}
            })

            val langTag = activeIssue?.language?.takeIf { it.isNotBlank() }
                ?: if (L10n.chinese) "zh-CN" else "en-US"
            val intent = Intent(RecognizerIntent.ACTION_RECOGNIZE_SPEECH).apply {
                putExtra(RecognizerIntent.EXTRA_LANGUAGE_MODEL, RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                putExtra(RecognizerIntent.EXTRA_PARTIAL_RESULTS, true)
                putExtra(RecognizerIntent.EXTRA_LANGUAGE, if (langTag.startsWith("zh")) "zh-CN" else langTag)
                putExtra(RecognizerIntent.EXTRA_MAX_RESULTS, 1)
            }
            runCatching { recognizer.startListening(intent) }.onFailure {
                isListeningInterrupt = false
                interruptVoiceLevel = 0f
                interruptVoiceHint = t("启动语音识别失败，请直接输入问题")
            }
        }
    }

    fun stopVoiceInterrupt() {
        mainHandler.post { stopVoiceInterruptInternal() }
    }

    private fun stopVoiceInterruptInternal() {
        isListeningInterrupt = false
        interruptVoiceLevel = 0f
        speechRecognizer?.let { rec ->
            runCatching { rec.stopListening() }
            runCatching { rec.cancel() }
            runCatching { rec.destroy() }
        }
        speechRecognizer = null
    }

    suspend fun submitInterrupt(issue: PodcastIssue, question: String, speakAnswer: Boolean = true): PodcastInterrupt? {
        val q = question.trim()
        if (q.isEmpty()) return null
        stopVoiceInterrupt()
        val atSec = if (activeIssueID == issue.id) currentTime else issue.positionSec
        val list = sentences(issue.id).filter { it.skipReason == null }
        val contextWindow = list.filter { it.t1 >= atSec - 90.0 && it.t0 <= atSec + 25.0 }
            .takeLast(8)
            .joinToString("\n") { s ->
                val who = s.speaker?.takeIf { it.isNotBlank() }?.let { "$it: " } ?: ""
                "$who${s.textOrig}" + (s.textTrans?.takeIf { it.isNotBlank() }?.let { " ($it)" } ?: "")
            }

        val cfg = CompanionConfig.current
        val answer = if (cfg != null && cfg.isReady) {
            val system = """
                你是语阅（Yuyue）播客收听台的随身书伴。听众正在听《${issue.showTitle}》的单集《${issue.title}》，在 ${PodcastFormat.clock(atSec)} 处插话提问。
                请结合当前上下文简洁、自然、像温和的对谈搭档一样回答（控制在 2～4 句以内，适合口播朗读，不要列机械大纲，严禁剧透听众尚未读到的书籍后文）。
            """.trimIndent()
            val prompt = buildString {
                if (contextWindow.isNotBlank()) {
                    append("【刚才听到的段落】\n").append(contextWindow).append("\n\n")
                } else if (issue.summary.isNotBlank()) {
                    append("【单集简介】\n").append(issue.summary).append("\n\n")
                }
                append("【听众插话提问】\n").append(q)
            }
            runCatching { app.yuyue.companion.Companion.answer(cfg, system, prompt, maxTokens = 512, fast = true, source = app.yuyue.companion.CompanionUsage.Source.Podcast) }.getOrElse { e ->
                fallbackInterruptAnswer(issue, atSec, contextWindow, app.yuyue.companion.Companion.message(e))
            }
        } else {
            fallbackInterruptAnswer(issue, atSec, contextWindow, null)
        }

        val saved = runCatching { core.podcastAddInterrupt(issue.id, atSec, q, answer) }.getOrNull()
        interruptCache[issue.id] = runCatching { core.podcastInterrupts(issue.id) }.getOrDefault(emptyList())
        cacheVersion++
        if (saved != null && speakAnswer) {
            speakInterruptAnswer(saved.answer)
        }
        return saved
    }

    private fun fallbackInterruptAnswer(issue: PodcastIssue, atSec: Double, contextLines: String, errNote: String?): String {
        val anchor = activeSentence(issue.id)
        val quote = anchor?.let { PodcastFormat.sentenceText(it, issue.track) }
            ?: contextLines.lineSequence().lastOrNull().orEmpty()
        val head = if (quote.isNotBlank()) {
            t("已为你记在 %@ 处。刚才提到：「%@」。", PodcastFormat.clock(atSec), quote.take(64))
        } else {
            t("已把你的问题记在 %@ 处。", PodcastFormat.clock(atSec))
        }
        val tail = errNote ?: t("在「我的 · 书伴」接上任意模型后，插话会实时结合上下文为你解答。")
        return "$head $tail"
    }

    fun speakInterruptAnswer(answer: String, onDone: (() -> Unit)? = null) {
        stopSpeakingInterruptAnswer()
        if (answer.isBlank()) {
            onDone?.invoke()
            return
        }
        isSpeakingInterruptReply = true
        replyJob = scope.launch {
            try {
                SystemVoice.Engine.init(context)
                val lang = if (L10n.chinese || answer.any { it.code > 0x3000 }) "zh-Hans" else "en"
                val voice = Voices.best(lang)
                val speech = voice?.render(answer, lang)
                if (speech != null && speech.samples.isNotEmpty()) {
                    val track = AudioTrack.Builder()
                        .setAudioAttributes(
                            PlatformAudioAttributes.Builder()
                                .setUsage(PlatformAudioAttributes.USAGE_MEDIA)
                                .setContentType(PlatformAudioAttributes.CONTENT_TYPE_SPEECH)
                                .build()
                        )
                        .setAudioFormat(
                            AudioFormat.Builder()
                                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                                .setSampleRate(speech.rate)
                                .setChannelMask(AudioFormat.CHANNEL_OUT_MONO)
                                .build()
                        )
                        .setTransferMode(AudioTrack.MODE_STATIC)
                        .setBufferSizeInBytes(max(2, speech.samples.size * 2))
                        .build()
                    track.write(speech.samples, 0, speech.samples.size)
                    replyTrack = track
                    try {
                        track.play()
                        while (isActive && track.playbackHeadPosition < speech.samples.size) {
                            delay(100L)
                        }
                    } finally {
                        runCatching { track.stop() }
                        runCatching { track.release() }
                        if (replyTrack === track) replyTrack = null
                    }
                }
            } finally {
                isSpeakingInterruptReply = false
                onDone?.invoke()
            }
        }
    }

    fun stopSpeakingInterruptAnswer() {
        replyJob?.cancel()
        replyJob = null
        replyTrack?.let {
            runCatching { it.pause() }
            runCatching { it.release() }
        }
        replyTrack = null
        isSpeakingInterruptReply = false
    }

    // MARK: - Blind Clip Marking ("原声划线") & Micro-Clip Replay

    suspend fun markCurrentMoment(thought: String? = null): PodcastClip? {
        val cur = activeIssue ?: return null
        val list = sentences(cur.id)
        val pos = if (activeIssueID == cur.id) currentTime else cur.positionSec
        val dur = max(duration, cur.durationSec.toDouble()).let { if (it > 0) it else max(60.0, pos + 30.0) }
        val window = resolveClipWindow(list, pos, dur)
        val quote = window.quoteText.ifBlank {
            cur.summary.takeIf { it.isNotBlank() }?.take(90) ?: cur.title
        }

        val relPath = cur.localAudioPath?.takeIf { File(it).isFile }?.let { localPath ->
            withContext(Dispatchers.IO) {
                exportLocalAudioSlice(File(localPath), window.t0, window.t1)
            }
        }

        val clip = runCatching {
            core.podcastAddClip(
                cur.id,
                window.t0,
                window.t1,
                window.speaker,
                quote,
                thought?.ifBlank { null },
                relPath,
            )
        }.getOrNull()

        if (clip != null) {
            clips = runCatching { core.podcastClips(null) }.getOrDefault(emptyList())
            library.notesChanged()
            lastNotice = t("已记下原声书摘（%@）", "${PodcastFormat.clock(window.t0)}–${PodcastFormat.clock(window.t1)}")
        }
        return clip
    }

    private fun exportLocalAudioSlice(sourceFile: File, t0: Double, t1: Double): String? {
        val clipsDir = File(core.podcastClipsDir()).apply { mkdirs() }
        val fileName = "clip-${UUID.randomUUID().toString().take(12)}.m4a"
        val outFile = File(clipsDir, fileName)
        return runCatching {
            val extractor = MediaExtractor()
            try {
                extractor.setDataSource(sourceFile.path)
                val trackIndex = (0 until extractor.trackCount).firstOrNull {
                    val mime = extractor.getTrackFormat(it).getString(MediaFormat.KEY_MIME).orEmpty()
                    mime.startsWith("audio/mp4a") || mime == MediaFormat.MIMETYPE_AUDIO_AAC
                } ?: return@runCatching null
                extractor.selectTrack(trackIndex)
                val format = extractor.getTrackFormat(trackIndex)
                val muxer = MediaMuxer(outFile.path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
                val outTrack = muxer.addTrack(format)
                muxer.start()
                val startUs = (max(0.0, t0) * 1_000_000.0).roundToLong()
                val endUs = (max(t0 + 1.0, t1) * 1_000_000.0).roundToLong()
                extractor.seekTo(startUs, MediaExtractor.SEEK_TO_PREVIOUS_SYNC)
                val buf = ByteBuffer.allocate(256 * 1024)
                val info = MediaCodec.BufferInfo()
                var wrote = false
                while (true) {
                    val size = extractor.readSampleData(buf, 0)
                    if (size < 0) break
                    val pts = extractor.sampleTime
                    if (pts > endUs) break
                    if (pts >= startUs - 250_000L) {
                        info.offset = 0
                        info.size = size
                        info.presentationTimeUs = max(0L, pts - startUs)
                        info.flags = extractor.sampleFlags
                        muxer.writeSampleData(outTrack, buf, info)
                        wrote = true
                    }
                    extractor.advance()
                }
                runCatching { muxer.stop() }
                muxer.release()
                if (wrote && outFile.length() > 0) fileName else {
                    outFile.delete()
                    null
                }
            } finally {
                extractor.release()
            }
        }.getOrNull()
    }

    fun playClip(clip: PodcastClip) {
        if (playingClipID == clip.id) {
            stopClip()
            return
        }
        stopClip()
        pause()
        val localSlice = clip.audioRelPath?.let { File(core.podcastClipsDir(), it) }?.takeIf { it.isFile }
        val targetIssue = issue(clip.issueId)
        val uri = when {
            localSlice != null -> Uri.fromFile(localSlice)
            targetIssue?.localAudioPath?.let { File(it).isFile } == true -> Uri.fromFile(File(targetIssue.localAudioPath!!))
            !targetIssue?.audioUrl.isNullOrBlank() -> Uri.parse(targetIssue.audioUrl!!)
            else -> null
        }
        if (uri == null && targetIssue != null && usesSpokenEngine(targetIssue)) {
            start(targetIssue, from = clip.t0)
            return
        }
        if (uri == null) return

        playingClipID = clip.id
        val player = ExoPlayer.Builder(context)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_SPEECH)
                    .build(),
                true
            )
            .build()
        clipPlayer = player
        player.setMediaItem(MediaItem.fromUri(uri))
        player.prepare()
        val startMs = if (localSlice != null) 0L else (clip.t0 * 1000.0).roundToLong()
        val spanMs = ((clip.t1 - clip.t0).coerceIn(2.0, 90.0) * 1000.0).roundToLong()
        player.seekTo(startMs)
        player.playWhenReady = true
        clipJob = scope.launch {
            delay(spanMs + 400L)
            if (playingClipID == clip.id) stopClip()
        }
    }

    fun stopClip() {
        clipJob?.cancel()
        clipJob = null
        clipPlayer?.let {
            runCatching { it.stop() }
            runCatching { it.release() }
        }
        clipPlayer = null
        playingClipID = null
    }

    // MARK: - Discovery, RSS, Single-Episode URL, OPML & Local Audio

    suspend fun loadDiscoveryIfNeeded() {
        if (discoveredShows.isNotEmpty() || isLoadingDiscovery) return
        isLoadingDiscovery = true
        try {
            val query = if (L10n.chinese) "读书 文化 历史" else "books literature history"
            val country = if (L10n.chinese) "CN" else "US"
            val found = fetchItunesSearch(query, country, limit = 8)
            if (found.isNotEmpty()) {
                discoveredShows = found
            }
        } finally {
            isLoadingDiscovery = false
        }
    }

    suspend fun searchDirectory(query: String): List<PodcastSearchHit> {
        val q = query.trim()
        if (q.isEmpty()) return emptyList()
        val hasCjk = q.any { it.code > 0x2E80 }
        val primaryCountry = if (hasCjk || L10n.chinese) "CN" else "US"
        val first = fetchItunesSearch(q, primaryCountry, limit = 18)
        if (first.isNotEmpty()) return first
        val fallbackCountry = if (primaryCountry == "CN") "US" else "CN"
        return fetchItunesSearch(q, fallbackCountry, limit = 18)
    }

    private suspend fun fetchItunesSearch(term: String, country: String, limit: Int): List<PodcastSearchHit> =
        withContext(Dispatchers.IO) {
            val encoded = URLEncoder.encode(term, "UTF-8")
            val url = "https://itunes.apple.com/search?media=podcast&entity=podcast&limit=$limit&country=$country&term=$encoded"
            runCatching {
                http.newCall(Request.Builder().url(url).header("User-Agent", "YuyuePodcast/1.0").build()).execute().use { r ->
                    if (!r.isSuccessful) return@runCatching emptyList()
                    val root = JSONObject(r.body?.string().orEmpty())
                    val arr = root.optJSONArray("results") ?: JSONArray()
                    val out = ArrayList<PodcastSearchHit>(arr.length())
                    val seen = HashSet<String>()
                    for (i in 0 until arr.length()) {
                        val o = arr.optJSONObject(i) ?: continue
                        val feed = o.optString("feedUrl").trim()
                        val title = o.optString("collectionName").trim()
                        if (feed.isEmpty() || title.isEmpty() || !seen.add(feed)) continue
                        out += PodcastSearchHit(
                            id = o.optLong("collectionId", i.toLong()).toString(),
                            title = title,
                            author = o.optString("artistName").trim(),
                            feedURL = feed,
                            artworkURL = o.optString("artworkUrl600").ifEmpty { o.optString("artworkUrl100") }.ifEmpty { null },
                            genre = o.optString("primaryGenreName").ifEmpty { null },
                            episodeCount = o.optInt("trackCount", 0).takeIf { it > 0 },
                        )
                    }
                    out
                }
            }.getOrDefault(emptyList())
        }

    suspend fun subscribeFeaturedShow(hit: PodcastSearchHit): Boolean {
        if (hit.feedURL in subscribingFeedURLs) return false
        subscribingFeedURLs = subscribingFeedURLs + hit.feedURL
        return try {
            subscribeRSS(hit.feedURL, putLatestOnDesk = true)
        } finally {
            subscribingFeedURLs = subscribingFeedURLs - hit.feedURL
        }
    }

    suspend fun subscribeRSS(urlString: String, putLatestOnDesk: Boolean = true): Boolean {
        val clean = urlString.trim()
        if (clean.isEmpty()) return false
        val normalized = if (clean.startsWith("http://") || clean.startsWith("https://")) clean else "https://$clean"
        isAddingFeed = true
        addError = null
        return try {
            val lower = normalized.substringBefore('?').lowercase()
            if (lower.endsWith(".mp3") || lower.endsWith(".m4a") || lower.endsWith(".aac") || lower.endsWith(".ogg") || lower.endsWith(".wav")) {
                val title = normalized.substringBefore('?').substringAfterLast('/').substringBeforeLast('.').ifBlank { t("单集音频") }
                val issue = withContext(Dispatchers.IO) {
                    core.podcastImportSingleIssue(
                        title = Uri.decode(title),
                        showTitle = t("单集直链"),
                        summary = "",
                        shownotes = "",
                        audioUrl = normalized,
                        localAudioPath = null,
                        artworkUrl = null,
                        durationSec = 0u,
                        language = if (L10n.chinese) "zh-Hans" else "en",
                        source = PodcastSourceKind.SINGLE_IMPORT,
                    )
                }
                reload()
                lastNotice = t("已将《%@》放上收听台", issue.title)
                true
            } else {
                val (body, contentType) = withContext(Dispatchers.IO) {
                    http.newCall(
                        Request.Builder()
                            .url(normalized)
                            .header("User-Agent", "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 YuyuePodcast/1.0")
                            .header("Accept", "application/rss+xml, application/xml, text/xml, text/html, */*")
                            .build()
                    ).execute().use { r ->
                        if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                        (r.body?.string().orEmpty()) to (r.header("Content-Type").orEmpty().lowercase())
                    }
                }
                val trimmed = body.trimStart()
                val looksXml = contentType.contains("xml") || trimmed.startsWith("<?xml") || trimmed.startsWith("<rss") || trimmed.startsWith("<feed")
                if (looksXml) {
                    ingestRssXml(normalized, body, putLatestOnDesk)
                } else {
                    val web = parseEpisodeWebpage(body, normalized)
                    when {
                        !web.audioUrl.isNullOrBlank() -> {
                            val issue = core.podcastImportSingleIssue(
                                title = web.title.ifBlank { t("单集节目") },
                                showTitle = web.showTitle.ifBlank { t("单集导入") },
                                summary = web.summary,
                                shownotes = web.summary,
                                audioUrl = web.audioUrl,
                                localAudioPath = null,
                                artworkUrl = web.artworkUrl,
                                durationSec = web.durationSec,
                                language = if (web.title.any { it.code > 0x2E80 }) "zh-Hans" else "en",
                                source = PodcastSourceKind.SINGLE_IMPORT,
                            )
                            reload()
                            lastNotice = t("已将单集《%@》放入收听台", issue.title)
                            true
                        }
                        !web.rssUrl.isNullOrBlank() -> {
                            val rssBody = withContext(Dispatchers.IO) {
                                http.newCall(Request.Builder().url(web.rssUrl!!).header("User-Agent", "YuyuePodcast/1.0").build()).execute().use { r ->
                                    if (!r.isSuccessful) throw java.io.IOException("HTTP ${r.code}")
                                    r.body?.string().orEmpty()
                                }
                            }
                            ingestRssXml(web.rssUrl!!, rssBody, putLatestOnDesk)
                        }
                        else -> {
                            // Try RSS parser anyway in case server sent text/plain
                            val rssAttempt = runCatching { parsePodcastRss(body) }.getOrNull()
                            if (rssAttempt != null && rssAttempt.episodes.isNotEmpty()) {
                                val ch = core.podcastUpsertChannelFromFeed(normalized, rssAttempt, putLatestOnDesk)
                                reload()
                                prefetchFeedTranscripts(ch.id, rssAttempt)
                                true
                            } else {
                                addError = t("没能从这个链接解析出播客音频或 RSS 源")
                                false
                            }
                        }
                    }
                }
            }
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            addError = t("解析失败：%@", e.localizedMessage ?: e.javaClass.simpleName)
            false
        } finally {
            isAddingFeed = false
        }
    }

    private fun ingestRssXml(feedUrl: String, xml: String, putLatestOnDesk: Boolean): Boolean {
        val parsed = parsePodcastRss(xml)
        if (parsed.title.isBlank() && parsed.episodes.isEmpty()) {
            addError = t("这不是有效的播客 RSS 订阅源")
            return false
        }
        val ch = core.podcastUpsertChannelFromFeed(feedUrl, parsed, putLatestOnDesk)
        reload()
        prefetchFeedTranscripts(ch.id, parsed)
        lastNotice = t("已订阅《%@》", ch.title)
        return true
    }

    private fun prefetchFeedTranscripts(channelId: String, feed: uniffi.yuyue_core.ParsedPodcastFeed) {
        val withLinks = feed.episodes.filter { it.transcripts.isNotEmpty() }.take(3)
        if (withLinks.isEmpty()) return
        scope.launch(Dispatchers.IO) {
            val channelIssues = allIssues.filter { it.channelId == channelId }
            for (ep in withLinks) {
                val target = channelIssues.firstOrNull { it.title == ep.title || it.audioUrl == ep.audioUrl } ?: continue
                val link = ep.transcripts.firstOrNull() ?: continue
                runCatching {
                    http.newCall(Request.Builder().url(link.url).header("User-Agent", "YuyuePodcast/1.0").build()).execute().use { r ->
                        if (!r.isSuccessful) return@use
                        val raw = r.body?.string().orEmpty()
                        val sentences = parsePodcastTranscript(raw, link.mimeType)
                        if (sentences.isNotEmpty()) {
                            core.podcastSetTranscript(target.id, sentences, PodcastFidelity.L2_MAGAZINE)
                            withContext(Dispatchers.Main) {
                                transcriptCache[target.id] = sentences
                                highlightCache[target.id] = runCatching { core.podcastHighlightSentenceIds(target.id).toSet() }.getOrDefault(emptySet())
                                reload()
                            }
                        }
                    }
                }
            }
        }
    }

    suspend fun refreshAllChannels() {
        val list = channels
        if (list.isEmpty()) return
        withContext(Dispatchers.IO) {
            for (ch in list) {
                runCatching {
                    http.newCall(Request.Builder().url(ch.feedUrl).header("User-Agent", "YuyuePodcast/1.0").build()).execute().use { r ->
                        if (r.isSuccessful) {
                            val parsed = parsePodcastRss(r.body?.string().orEmpty())
                            core.podcastUpsertChannelFromFeed(ch.feedUrl, parsed, false)
                        }
                    }
                }
            }
        }
        reload()
    }

    suspend fun importOPML(uri: Uri): Int {
        val xml = withContext(Dispatchers.IO) {
            context.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }.orEmpty()
        }
        if (xml.isBlank()) return 0
        val feeds = runCatching { parsePodcastOpml(xml) }.getOrDefault(emptyList())
        var count = 0
        for (f in feeds.take(24)) {
            if (subscribeRSS(f.xmlUrl, putLatestOnDesk = count == 0)) count++
        }
        reload()
        return count
    }

    suspend fun importLocalAudio(uri: Uri): PodcastIssue? = withContext(Dispatchers.IO) {
        val displayName = queryDisplayName(uri) ?: "audio-${System.currentTimeMillis()}.m4a"
        val ext = displayName.substringAfterLast('.', "m4a").lowercase().take(5)
        val audioDir = File(File(core.podcastClipsDir()).parentFile, "audio").apply { mkdirs() }
        val dst = File(audioDir, "local-${UUID.randomUUID().toString().take(10)}.$ext")
        context.contentResolver.openInputStream(uri)?.use { input ->
            dst.outputStream().use { out -> input.copyTo(out) }
        } ?: return@withContext null

        val retriever = MediaMetadataRetriever()
        var durSec = 0u
        var metaTitle: String? = null
        var metaArtist: String? = null
        try {
            retriever.setDataSource(dst.path)
            durSec = ((retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_DURATION)?.toLongOrNull() ?: 0L) / 1000L).toUInt()
            metaTitle = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_TITLE)?.trim()?.takeIf { it.isNotEmpty() }
            metaArtist = retriever.extractMetadata(MediaMetadataRetriever.METADATA_KEY_ARTIST)?.trim()?.takeIf { it.isNotEmpty() }
        } catch (_: Exception) {
        } finally {
            runCatching { retriever.release() }
        }

        val cleanTitle = metaTitle ?: displayName.substringBeforeLast('.').ifBlank { t("本地录音") }
        val issue = core.podcastImportSingleIssue(
            title = cleanTitle,
            showTitle = metaArtist ?: t("本机音频"),
            summary = "",
            shownotes = "",
            audioUrl = null,
            localAudioPath = dst.path,
            artworkUrl = null,
            durationSec = durSec,
            language = if (cleanTitle.any { it.code > 0x2E80 } || L10n.chinese) "zh-Hans" else "en",
            source = PodcastSourceKind.LOCAL_AUDIO,
        )
        withContext(Dispatchers.Main) {
            reload()
            lastNotice = t("已将《%@》放上书桌", issue.title)
        }
        issue
    }

    private fun queryDisplayName(uri: Uri): String? = runCatching {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        }
    }.getOrNull()

    // MARK: - On-Device SenseVoice Transcription (`Listener`)

    suspend fun transcribeOnDevice(issueID: String): Boolean {
        val target = issue(issueID) ?: return false
        val path = target.localAudioPath?.let { File(it) }?.takeIf { it.isFile }
        if (path == null) {
            lastNotice = t("先导入本机音频文件即可离线转出逐字稿")
            return false
        }
        if (isTranscribingIssueID != null) return false
        isTranscribingIssueID = issueID
        return try {
            if (!Listener.installed(library.dataDir)) {
                lastNotice = t("正在准备本机语音识别模型（约 230 MB）…")
                Listener.ensure(library.dataDir) { p ->
                    lastNotice = t("正在下载语音识别模型 · %lld%%", (p * 100).roundToInt())
                }
            }
            lastNotice = t("正在本机识别逐字稿…")
            val words = Listener.file(library.dataDir, path, target.language.ifBlank { "zh" }, 0.0) { sec ->
                val total = max(1.0, target.durationSec.toDouble())
                val pct = ((sec / total) * 100.0).roundToInt().coerceIn(1, 99)
                lastNotice = t("正在本机转写 · %lld%%", pct)
            }
            if (words.isNullOrEmpty()) {
                lastNotice = t("没能从这段音频里识别出清晰语音")
                return false
            }
            val built = groupWordsIntoSentences(words, target.language)
            if (built.isEmpty()) return false
            core.podcastSetTranscript(issueID, built, PodcastFidelity.L1_LIVE_TRANSCRIPT)
            transcriptCache[issueID] = built
            highlightCache[issueID] = runCatching { core.podcastHighlightSentenceIds(issueID).toSet() }.getOrDefault(emptySet())
            reload()
            lastNotice = t("本机逐字稿已就绪（共 %lld 句）", built.size)
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            lastNotice = t("本机转写未完成：%@", e.localizedMessage ?: "error")
            false
        } finally {
            isTranscribingIssueID = null
        }
    }

    private fun groupWordsIntoSentences(words: List<uniffi.yuyue_core.HeardWord>, language: String): List<MagazineSentence> {
        val isZh = language.lowercase().startsWith("zh")
        val out = mutableListOf<MagazineSentence>()
        val buf = StringBuilder()
        var segStart = words.first().t0
        var segEnd = words.first().t1
        val terminals = setOf('。', '！', '？', '.', '!', '?', '；', ';')

        fun flush() {
            val text = buf.toString().trim()
            if (text.isNotEmpty()) {
                out += MagazineSentence(
                    id = "asr-${out.size}",
                    speaker = null,
                    textOrig = text,
                    textTrans = null,
                    coachNote = null,
                    t0 = segStart,
                    t1 = max(segStart + 0.4, segEnd),
                    skipReason = null,
                    bookId = null,
                    bookTitle = null,
                    bookSection = null,
                    bookSentence = null,
                    bookQuote = null,
                )
            }
            buf.clear()
        }

        for (w in words) {
            if (buf.isEmpty()) segStart = w.t0
            if (!isZh && buf.isNotEmpty() && !w.text.firstOrNull().let { it != null && it in terminals }) {
                buf.append(' ')
            }
            buf.append(w.text)
            segEnd = w.t1
            val ended = w.text.lastOrNull()?.let { it in terminals } == true
            val longEnough = (segEnd - segStart) >= 14.0 || buf.length >= (if (isZh) 54 else 160)
            if (ended || longEnough) flush()
        }
        flush()
        return out
    }

    // MARK: - Private Studio Generation ("酿一期书房电台")

    suspend fun generateStudioEpisode(
        kind: StudioKind,
        bookIDs: List<String>,
        minutes: UInt,
        topic: String?,
        noSpoiler: Boolean = true,
        voiceStyle: String = studioVoiceStyle,
    ): PodcastIssue? {
        if (isGeneratingStudio) return null
        isGeneratingStudio = true
        selectStudioVoiceStyle(voiceStyle)
        studioStatus = t("正在整理书摘与阅读进度…")
        return try {
            val brief = withContext(Dispatchers.IO) {
                core.buildStudioBrief(kind, bookIDs, minutes, topic?.trim()?.ifEmpty { null })
            }
            val cfg = CompanionConfig.current
            val rawJson = if (cfg != null && cfg.isReady) {
                studioStatus = t("正在用 %@ 编排双人对谈台本…", cfg.provider.title)
                val sys = if (noSpoiler) brief.systemPrompt
                else brief.systemPrompt + "\n（听众已允许结合全书脉络深入展开讨论。）"
                runCatching {
                    app.yuyue.companion.Companion.answer(cfg, sys, brief.userPrompt, maxTokens = 2800)
                }.getOrDefault("")
            } else {
                studioStatus = t("正在本机编排精读电台…")
                ""
            }
            studioStatus = t("正在封存逐字稿与章节锚点…")
            val lang = if (kind == StudioKind.VOCAB_RADIO) "en" else if (L10n.chinese) "zh-Hans" else "en"
            val issue = withContext(Dispatchers.IO) {
                val draft = core.finalizeStudioScript(brief, rawJson)
                core.podcastSaveStudioIssue(kind, draft, bookIDs, lang)
            }
            transcriptCache.remove(issue.id)
            highlightCache.remove(issue.id)
            reload()
            ensureTranscriptLoaded(issue.id)
            lastNotice = t("《%@》已酿好", issue.title)
            issue
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            addError = app.yuyue.companion.Companion.message(e)
            null
        } finally {
            isGeneratingStudio = false
            studioStatus = null
        }
    }

    // MARK: - Media3 Notification & Lock-Screen Bridge

    inner class PodcastSessionPlayer : SimpleBasePlayer(Looper.getMainLooper()) {
        fun refresh() = invalidateState()

        override fun getState(): State {
            val commands = Media3Player.Commands.Builder().addAll(
                COMMAND_PLAY_PAUSE,
                COMMAND_STOP,
                COMMAND_SEEK_IN_CURRENT_MEDIA_ITEM,
                COMMAND_SEEK_BACK,
                COMMAND_SEEK_FORWARD,
                COMMAND_SEEK_TO_NEXT,
                COMMAND_SEEK_TO_PREVIOUS,
                COMMAND_SEEK_TO_NEXT_MEDIA_ITEM,
                COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM,
                COMMAND_GET_CURRENT_MEDIA_ITEM,
                COMMAND_GET_METADATA,
                COMMAND_GET_TIMELINE,
            ).build()
            val builder = State.Builder()
                .setAvailableCommands(commands)
                .setSeekBackIncrementMs(15_000L)
                .setSeekForwardIncrementMs(15_000L)
            val cur = activeIssue
            if (cur == null) {
                return builder.setPlaybackState(STATE_IDLE)
                    .setPlayWhenReady(false, PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
                    .build()
            }
            val sentenceLine = activeSentence(cur.id)?.let { PodcastFormat.sentenceText(it, cur.track) }
            val meta = MediaMetadata.Builder()
                .setTitle(cur.title)
                .setArtist(cur.showTitle)
                .setAlbumTitle(cur.showTitle)
                .setSubtitle(sentenceLine ?: cur.showTitle)
                .apply { cur.artworkUrl?.takeIf { it.isNotBlank() }?.let { setArtworkUri(Uri.parse(it)) } }
                .build()
            val durUs = if (duration > 0) (duration * 1_000_000.0).roundToLong() else C.TIME_UNSET
            val item = MediaItemData.Builder(cur.id)
                .setMediaItem(MediaItem.Builder().setMediaId(cur.id).setMediaMetadata(meta).build())
                .setMediaMetadata(meta)
                .setDurationUs(durUs)
                .setIsSeekable(true)
                .build()
            val posMs = (currentTime * 1000.0).roundToLong()
            return builder
                .setPlaylist(listOf(item))
                .setCurrentMediaItemIndex(0)
                .setContentPositionMs(posMs)
                .setPlaybackParameters(PlaybackParameters(rate))
                .setPlaybackState(STATE_READY)
                .setPlayWhenReady(isPlaying, PLAY_WHEN_READY_CHANGE_REASON_USER_REQUEST)
                .build()
        }

        override fun handleSetPlayWhenReady(playWhenReady: Boolean): ListenableFuture<*> {
            if (playWhenReady) {
                activeIssue?.let { start(it, from = currentTime) }
            } else {
                pause()
            }
            return Futures.immediateVoidFuture()
        }

        override fun handleSeek(mediaItemIndex: Int, positionMs: Long, seekCommand: Int): ListenableFuture<*> {
            when (seekCommand) {
                COMMAND_SEEK_BACK, COMMAND_SEEK_TO_PREVIOUS, COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM -> skip(-15.0)
                COMMAND_SEEK_FORWARD, COMMAND_SEEK_TO_NEXT, COMMAND_SEEK_TO_NEXT_MEDIA_ITEM -> skip(15.0)
                else -> if (positionMs >= 0) seek(positionMs / 1000.0)
            }
            return Futures.immediateVoidFuture()
        }

        override fun handleStop(): ListenableFuture<*> {
            stopPlayback()
            return Futures.immediateVoidFuture()
        }
    }

    companion object {
        private const val PREF_RATE = "podcast.rate"
        private const val PREF_STUDIO_VOICE = "podcast.studio.voice"
    }
}
