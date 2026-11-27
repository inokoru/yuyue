@preconcurrency import AVFoundation
import Foundation
import MediaPlayer
import Observation
import os
import Speech
import YuyueCore
#if os(iOS)
import UIKit
#endif

/// Search hit from Apple's public iTunes Podcast Search API (zero self-hosted server).
struct PodcastSearchHit: Identifiable, Hashable, Sendable {
    let id: String
    let title: String
    let author: String
    let feedURL: String
    let artworkURL: String?
    let genre: String?
}

/// Unified Audio-Magazine & Private Studio store + player for Yuyue Podcast.
///
/// Owns:
/// - Desk (`书桌`) / Newsstand (`报刊栏`, rolling 14 days, zero unread badges) / Archive (`已留档`)
/// - External RSS subscriptions, OPML import, single-episode URL import, local audio import
/// - Real `AVPlayer` playback for external audio + `AVSpeechSynthesizer` spoken playback for
///   Private Studio episodes without pre-rendered audio files
/// - Blind clip-marking (`记这句 H` -> sentence-snapped `[t0, t1]` + optional local `.m4a` slice)
/// - Cut-in voice/text Q&A (`插话`) with `-24 dB` ducking & 3-second rewind resume
/// - Private Studio generation (`书房周刊` / `难书导读` / `跨书圆桌` / `生词外语`) with
///   physical non-spoiler truncation in Rust core and optional recurring schedule via `ShowSchedule`
@Observable
final class PodcastHub {
    private static let logger = Logger(subsystem: "app.yuyue", category: "PodcastHub")

    let core: YuyueCore
    let dataDir: URL

    // MARK: Stored data
    private(set) var channels: [PodcastChannel] = []
    private(set) var deskIssues: [PodcastIssue] = []
    private(set) var newsstandIssues: [PodcastIssue] = []
    private(set) var archiveIssues: [PodcastIssue] = []
    private(set) var clips: [PodcastClip] = []
    /// Live top-charts recommendations from Apple's public iTunes directory (cached in memory).
    private(set) var discoveredShows: [PodcastSearchHit] = []
    private(set) var isLoadingDiscovery = false
    private(set) var subscribingFeedURLs: Set<String> = []
    private(set) var loadingMoreChannelIDs: Set<String> = []
    private(set) var exhaustedChannelIDs: Set<String> = []

    // MARK: Active playback state
    private(set) var activeIssueID: String?
    private(set) var isPlaying = false
    private(set) var currentTime: Double = 0
    private(set) var duration: Double = 0
    var rate: Float = 1.0 {
        didSet { applyRate() }
    }
    /// Ducking during cut-in Q&A ("插话"): drops playback volume to -24 dB (~0.063).
    private(set) var isDucked = false
    /// Live on-device microphone voice capture state during cut-in Q&A ("语音插话").
    private(set) var isListeningInterrupt = false
    /// Normalized live microphone voice level (`0...1`) for subtle acoustic bars.
    private(set) var interruptVoiceLevel: CGFloat = 0
    /// Inline hint if microphone or speech recognition permission is unavailable.
    private(set) var interruptVoiceHint: String?
    /// Whether the companion is currently reading its cut-in answer aloud.
    private(set) var isSpeakingInterruptReply = false
    /// Currently highlighted sentence id in the Audio Magazine reader.
    private(set) var activeSentenceID: String?
    /// Currently replaying micro-clip id (for `Today's Review` & sidebar `原声摘`).
    private(set) var playingClipID: String?

    // MARK: Transcript & side notes cache for active/opened issue
    private(set) var transcripts: [String: [MagazineSentence]] = [:]
    private(set) var highlightIDs: [String: Set<String>] = [:]
    private(set) var interrupts: [String: [PodcastInterrupt]] = [:]

    // MARK: Async task states
    private(set) var isGeneratingStudio = false
    private(set) var studioStatus: String?
    private(set) var isAddingFeed = false
    private(set) var addError: String?
    private(set) var lastNotice: String?

    /// Selected Private Studio host voice preset (`studio-duo-zh`, `studio-duo-en`, `studio-solo-zh`, or `cloned:<id>`).
    var studioVoiceStyle: String {
        didSet { UserDefaults.standard.set(studioVoiceStyle, forKey: "podcast.studioVoiceStyle") }
    }
    /// Persistent per-voice-card toggle in the Voices drawer (`voiceID -> Bool`).
    private(set) var rememberedVoices: [String: Bool]
    /// External presentation request on iOS when tapping the floating Podcast mini-player bar.
    var requestedOpenIssueID: String?

    // MARK: Private audio engines
    @ObservationIgnored private var avPlayer: AVPlayer?
    @ObservationIgnored private var timeObserver: Any?
    @ObservationIgnored private var itemEndObserver: NSObjectProtocol?
    @ObservationIgnored private var clipPlayer: AVAudioPlayer?
    @ObservationIgnored private var _pcmPlayer: PodcastPCMPlayer?
    private var pcmPlayer: PodcastPCMPlayer {
        if let existing = _pcmPlayer { return existing }
        let created = PodcastPCMPlayer()
        _pcmPlayer = created
        return created
    }
    @ObservationIgnored private var studioSpeechTask: Task<Void, Never>?
    @ObservationIgnored private var studioSynth: AVSpeechSynthesizer?
    @ObservationIgnored private var studioDelegate: StudioSpeechDelegate?
    @ObservationIgnored private var voiceCapture: PodcastVoiceCapture?
    @ObservationIgnored private var replySynth: AVSpeechSynthesizer?
    @ObservationIgnored private var lastSavedPositionAt: Date = .distantPast
    @ObservationIgnored private var observers: [NSObjectProtocol] = []
    @ObservationIgnored private var remoteTargets: [(MPRemoteCommand, Any)] = []
    @ObservationIgnored private var ownsRemote = false

    init(core: YuyueCore, dataDir: URL) {
        self.core = core
        self.dataDir = dataDir
        self.studioVoiceStyle = UserDefaults.standard.string(forKey: "podcast.studioVoiceStyle") ?? "studio-duo-zh"
        self.rememberedVoices = (UserDefaults.standard.dictionary(forKey: "podcast.rememberedVoices") as? [String: Bool]) ?? [:]
        reload(clearCache: false, eagerTranscript: false)
        setupAudioObservers()
    }

    // MARK: Queries

    var activeIssue: PodcastIssue? {
        if let id = activeIssueID {
            return issue(id)
        }
        return deskIssues.first(where: { $0.positionSec > 0 }) ?? deskIssues.first
    }

    var weeklyDigestIssue: PodcastIssue? {
        let all = deskIssues + archiveIssues
        return all.first(where: { $0.studioKind == .weeklyDigest })
    }

    /// Deduplicated union of Desk, Newsstand, and Archive issues in priority order.
    var allIssues: [PodcastIssue] {
        var seen = Set<String>()
        return (deskIssues + newsstandIssues + archiveIssues).filter { seen.insert($0.id).inserted }
    }

    /// Issues currently in progress (active playback or resumed position on Desk).
    var inProgressIssues: [PodcastIssue] {
        var result: [PodcastIssue] = []
        if let current = activeIssue {
            result.append(current)
        }
        for item in deskIssues where item.id != activeIssue?.id && item.positionSec > 0 {
            result.append(item)
        }
        return result
    }

    /// All Private Studio ("书房电台") issues across Desk and Archive.
    var studioIssues: [PodcastIssue] {
        allIssues.filter { $0.studioKind != nil }
    }

    /// Current live transcript sentence for tabletop preview, falling back to the first spoken line.
    func activeSentence(for issueID: String) -> MagazineSentence? {
        let sents = sentences(for: issueID)
        guard !sents.isEmpty else { return nil }
        if activeIssueID == issueID, let aid = activeSentenceID,
           let match = sents.first(where: { $0.id == aid }) {
            return match
        }
        let pos = activeIssueID == issueID ? currentTime : (issue(issueID)?.positionSec ?? 0)
        return sents.first(where: { pos >= $0.t0 && pos <= $0.t1 }) ?? sents.first
    }

    func cycleRate() {
        let steps: [Float] = [0.8, 1.0, 1.2, 1.5]
        if let idx = steps.firstIndex(where: { abs($0 - rate) < 0.05 }) {
            rate = steps[(idx + 1) % steps.count]
        } else {
            rate = 1.0
        }
    }

    func isVoiceRemembered(_ id: String) -> Bool {
        rememberedVoices[id] ?? true
    }

    func setVoiceRemembered(_ id: String, _ on: Bool) {
        rememberedVoices[id] = on
        UserDefaults.standard.set(rememberedVoices, forKey: "podcast.rememberedVoices")
    }

    /// True only when an issue genuinely carries bilingual / cross-lingual sentence tracks.
    func supportsTrackSwitch(_ issue: PodcastIssue) -> Bool {
        if issue.studioKind == .vocabRadio || issue.fidelity == .l3CrossLingual {
            return true
        }
        return sentences(for: issue.id).contains {
            ($0.textTrans?.isEmpty == false) || ($0.coachNote?.isEmpty == false)
        }
    }

    private func shouldPlayTranslatedScript(for issue: PodcastIssue) -> Bool {
        guard issue.track == .dub else { return false }
        return sentences(for: issue.id).contains { ($0.textTrans?.isEmpty == false) }
    }

    func setTrack(_ track: PodcastTrack, for issue: PodcastIssue) {
        setViewMode(issueID: issue.id, edition: issue.edition, track: track)
        guard activeIssueID == issue.id, isPlaying, let refreshed = self.issue(issue.id) else { return }
        start(refreshed, from: currentTime)
    }

    func toggleSavedLater(_ issue: PodcastIssue) {
        if issue.placement == .archive {
            move(issue, to: .desk)
            lastNotice = String(localized: "已移回在听书桌")
        } else {
            move(issue, to: .archive)
            lastNotice = String(localized: "已存入稍后听")
        }
    }

    func issue(_ id: String) -> PodcastIssue? {
        deskIssues.first(where: { $0.id == id })
            ?? newsstandIssues.first(where: { $0.id == id })
            ?? archiveIssues.first(where: { $0.id == id })
            ?? (try? core.podcastIssue(issueId: id))
    }

    func sentences(for issueID: String) -> [MagazineSentence] {
        if let cached = transcripts[issueID] { return cached }
        let loaded = core.podcastTranscript(issueId: issueID)
        transcripts[issueID] = loaded
        return loaded
    }

    func highlightSet(for issueID: String) -> Set<String> {
        if let cached = highlightIDs[issueID] { return cached }
        let set = Set(core.podcastHighlightSentenceIds(issueId: issueID))
        highlightIDs[issueID] = set
        return set
    }

    func interrupts(for issueID: String) -> [PodcastInterrupt] {
        if let cached = interrupts[issueID] { return cached }
        let loaded = (try? core.podcastInterrupts(issueId: issueID)) ?? []
        interrupts[issueID] = loaded
        return loaded
    }

    func clips(for issueID: String) -> [PodcastClip] {
        clips.filter { $0.issueId == issueID }
    }

    func mentions(for book: BookSummary) -> [BookPodcastMention] {
        (try? core.podcastMentionsForBook(bookId: book.id, bookTitle: book.title)) ?? []
    }

    func reload(clearCache: Bool = false, eagerTranscript: Bool = true) {
        if clearCache {
            transcripts.removeAll()
            highlightIDs.removeAll()
            interrupts.removeAll()
        } else {
            transcripts = transcripts.filter { !$0.value.isEmpty }
        }
        channels = (try? core.podcastChannels()) ?? []
        deskIssues = (try? core.podcastIssues(placement: .desk)) ?? []
        newsstandIssues = (try? core.podcastIssues(placement: .newsstand)) ?? []
        archiveIssues = (try? core.podcastIssues(placement: .archive)) ?? []
        clips = (try? core.podcastClips(issueId: nil)) ?? []

        if activeIssueID == nil,
           let preferred = deskIssues.first(where: { $0.positionSec > 0 }) ?? deskIssues.first {
            activeIssueID = preferred.id
            currentTime = preferred.positionSec
            duration = Double(preferred.durationSec)
            if eagerTranscript { updateActiveSentence() }
        } else if !isPlaying, let id = activeIssueID, let cur = issue(id) {
            currentTime = cur.positionSec
            duration = Double(cur.durationSec)
            if eagerTranscript { updateActiveSentence() }
        }
    }

    // MARK: Placement & Management

    func move(_ issue: PodcastIssue, to placement: PodcastPlacement) {
        try? core.podcastSetPlacement(issueId: issue.id, placement: placement)
        reload()
    }

    func delete(_ issue: PodcastIssue) {
        if activeIssueID == issue.id { stop() }
        try? core.podcastDeleteIssue(issueId: issue.id)
        transcripts.removeValue(forKey: issue.id)
        interrupts.removeValue(forKey: issue.id)
        reload()
    }

    func setViewMode(issueID: String, edition: PodcastEdition, track: PodcastTrack) {
        try? core.podcastSetViewMode(issueId: issueID, edition: edition, track: track)
        reload()
    }

    // MARK: Search, Subscribe, Single-Import, OPML & Local Audio

    static func searchITunes(query: String) async -> [PodcastSearchHit] {
        let q = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty,
              let encoded = q.addingPercentEncoding(withAllowedCharacters: .urlQueryAllowed),
              let url = URL(string: "https://itunes.apple.com/search?media=podcast&limit=12&term=\(encoded)")
        else { return [] }
        do {
            let (data, _) = try await URLSession.shared.data(from: url)
            struct Resp: Decodable {
                struct Item: Decodable {
                    let collectionId: Int?
                    let collectionName: String?
                    let artistName: String?
                    let feedUrl: String?
                    let artworkUrl600: String?
                    let artworkUrl100: String?
                    let primaryGenreName: String?
                }
                let results: [Item]
            }
            let decoded = try JSONDecoder().decode(Resp.self, from: data)
            return decoded.results.compactMap { item in
                guard let feed = item.feedUrl, !feed.isEmpty, let title = item.collectionName else { return nil }
                return PodcastSearchHit(
                    id: "\(item.collectionId ?? feed.hashValue)",
                    title: title,
                    author: item.artistName ?? "",
                    feedURL: feed,
                    artworkURL: item.artworkUrl600 ?? item.artworkUrl100,
                    genre: item.primaryGenreName
                )
            }
        } catch {
            logger.error("iTunes search failed: \(error.localizedDescription)")
            return []
        }
    }

    /// Fetches live featured podcasts from Apple's public top-charts RSS + iTunes Lookup API
    /// (zero self-hosted backend), so the Podcast Home can show real, one-click subscribable shows.
    func loadDiscoveryIfNeeded() async {
        guard discoveredShows.isEmpty, !isLoadingDiscovery else { return }
        isLoadingDiscovery = true
        defer { isLoadingDiscovery = false }

        let preferCN = Locale.preferredLanguages.first?.lowercased().hasPrefix("zh") ?? true
        let country = preferCN ? "cn" : "us"
        guard let chartURL = URL(string: "https://itunes.apple.com/\(country)/rss/toppodcasts/limit=10/json") else {
            return
        }
        do {
            let (chartData, _) = try await URLSession.shared.data(from: chartURL)
            struct ChartResp: Decodable {
                struct Feed: Decodable {
                    struct Entry: Decodable {
                        struct IDBox: Decodable {
                            struct Attrs: Decodable {
                                let imId: String?
                                enum CodingKeys: String, CodingKey { case imId = "im:id" }
                            }
                            let attributes: Attrs?
                        }
                        let id: IDBox?
                    }
                    let entry: [Entry]?
                }
                let feed: Feed?
            }
            let chart = try JSONDecoder().decode(ChartResp.self, from: chartData)
            let ids = (chart.feed?.entry ?? []).compactMap { $0.id?.attributes?.imId }.prefix(8)
            guard !ids.isEmpty,
                  let lookupURL = URL(string: "https://itunes.apple.com/lookup?id=\(ids.joined(separator: ","))&country=\(country)")
            else { return }

            let (lookupData, _) = try await URLSession.shared.data(from: lookupURL)
            struct LookupResp: Decodable {
                struct Item: Decodable {
                    let collectionId: Int?
                    let collectionName: String?
                    let artistName: String?
                    let feedUrl: String?
                    let artworkUrl600: String?
                    let artworkUrl100: String?
                    let primaryGenreName: String?
                }
                let results: [Item]
            }
            let decoded = try JSONDecoder().decode(LookupResp.self, from: lookupData)
            var seen = Set<String>()
            let resolved = decoded.results.compactMap { item -> PodcastSearchHit? in
                guard let feed = item.feedUrl, !feed.isEmpty,
                      let title = item.collectionName, !title.isEmpty,
                      seen.insert(feed).inserted
                else { return nil }
                return PodcastSearchHit(
                    id: "\(item.collectionId ?? feed.hashValue)",
                    title: title,
                    author: item.artistName ?? "",
                    feedURL: feed,
                    artworkURL: item.artworkUrl600 ?? item.artworkUrl100,
                    genre: item.primaryGenreName
                )
            }
            if !resolved.isEmpty {
                discoveredShows = Array(resolved.prefix(6))
            }
        } catch {
            Self.logger.error("discovery load failed: \(error.localizedDescription)")
        }
    }

    @discardableResult
    func subscribeFeaturedShow(_ hit: PodcastSearchHit) async -> Bool {
        guard !subscribingFeedURLs.contains(hit.feedURL) else { return false }
        subscribingFeedURLs.insert(hit.feedURL)
        defer { subscribingFeedURLs.remove(hit.feedURL) }
        return await subscribeRSS(urlString: hit.feedURL, putLatestOnDesk: true)
    }

    @discardableResult
    func subscribeRSS(urlString: String, putLatestOnDesk: Bool = true, trackAdd: Bool = true) async -> Bool {
        let trimmed = urlString.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let url = URL(string: trimmed) else {
            addError = String(localized: "链接格式不正确")
            return false
        }
        isAddingFeed = true
        addError = nil
        defer { isAddingFeed = false }
        do {
            var req = URLRequest(url: url)
            req.timeoutInterval = 20
            req.setValue("YuyuePodcast/1.0", forHTTPHeaderField: "User-Agent")
            let (data, resp) = try await URLSession.shared.data(for: req)
            let body = String(decoding: data, as: UTF8.self)
            let ct = (resp as? HTTPURLResponse)?.value(forHTTPHeaderField: "Content-Type")?.lowercased() ?? ""

            // Direct audio file link -> import straight onto Desk.
            let lowerURL = trimmed.lowercased()
            if ct.contains("audio/") || lowerURL.hasSuffix(".mp3") || lowerURL.hasSuffix(".m4a") || lowerURL.hasSuffix(".aac") {
                let name = url.deletingPathExtension().lastPathComponent.removingPercentEncoding ?? String(localized: "单集音频")
                _ = try core.podcastImportSingleIssue(
                    title: name,
                    showTitle: url.host() ?? String(localized: "单集导入"),
                    summary: "",
                    shownotes: "",
                    audioUrl: trimmed,
                    localAudioPath: nil,
                    artworkUrl: nil,
                    durationSec: 0,
                    language: "zh-Hans",
                    source: .singleImport
                )
                reload()
                if trackAdd { AirTraffic.trackAudioFeatureUsed(kind: "podcast_added") }
                return true
            }

            // Try RSS first; if HTML webpage, extract single episode or discover embedded RSS.
            if let feed = try? parsePodcastRss(xml: body), !feed.episodes.isEmpty {
                let channel = try core.podcastUpsertChannelFromFeed(feedUrl: trimmed, feed: feed, putLatestOnDesk: putLatestOnDesk)
                exhaustedChannelIDs.remove(channel.id)
                reload()
                if trackAdd { AirTraffic.trackAudioFeatureUsed(kind: "podcast_added") }
                // Prefetch Podcasting 2.0 transcript if the top episode provides one.
                if let firstEp = feed.episodes.first, let tx = firstEp.transcripts.first,
                   let issue = deskIssues.first(where: { $0.audioUrl == firstEp.audioUrl }) ?? newsstandIssues.first(where: { $0.audioUrl == firstEp.audioUrl }) {
                    await fetchRemoteTranscript(for: issue.id, urlString: tx.url, mime: tx.mimeType)
                }
                return true
            }

            let web = parseEpisodeWebpage(html: body, pageUrl: trimmed)
            if let audio = web.audioUrl, !audio.isEmpty {
                _ = try core.podcastImportSingleIssue(
                    title: web.title,
                    showTitle: web.showTitle,
                    summary: web.summary,
                    shownotes: web.summary,
                    audioUrl: audio,
                    localAudioPath: nil,
                    artworkUrl: web.artworkUrl,
                    durationSec: web.durationSec,
                    language: "zh-Hans",
                    source: .singleImport
                )
                reload()
                if trackAdd { AirTraffic.trackAudioFeatureUsed(kind: "podcast_added") }
                return true
            }
            if let rss = web.rssUrl, let resolved = URL(string: rss, relativeTo: url)?.absoluteString {
                return await subscribeRSS(urlString: resolved, putLatestOnDesk: putLatestOnDesk, trackAdd: trackAdd)
            }
            addError = String(localized: "没在这个链接里找到音频或 RSS 订阅源")
            return false
        } catch {
            addError = error.localizedDescription
            return false
        }
    }

    func refreshAllChannels() async {
        for ch in channels {
            _ = await subscribeRSS(urlString: ch.feedUrl, putLatestOnDesk: false, trackAdd: false)
        }
    }

    func channel(forShowKey key: String) -> PodcastChannel? {
        channels.first { ($0.title.isEmpty ? $0.id : $0.title) == key }
    }

    @discardableResult
    func loadMoreEpisodes(for channel: PodcastChannel) async -> Int {
        guard !loadingMoreChannelIDs.contains(channel.id),
              let url = URL(string: channel.feedUrl)
        else { return 0 }
        loadingMoreChannelIDs.insert(channel.id)
        defer { loadingMoreChannelIDs.remove(channel.id) }
        do {
            var req = URLRequest(url: url)
            req.timeoutInterval = 25
            req.setValue("YuyuePodcast/1.0", forHTTPHeaderField: "User-Agent")
            let (data, _) = try await URLSession.shared.data(for: req)
            let body = String(decoding: data, as: UTF8.self)
            let feed = try parsePodcastRss(xml: body)
            let existing = allIssues.filter { $0.channelId == channel.id }
            let existingAudio = Set(existing.compactMap(\.audioUrl))
            let existingTitles = Set(existing.map(\.title))
            let lastMatchedIdx = feed.episodes.lastIndex {
                existingAudio.contains($0.audioUrl) || existingTitles.contains($0.title)
            }
            let startIdx = (lastMatchedIdx ?? -1) + 1
            let remaining = Array(feed.episodes.dropFirst(startIdx))
            let batch = Array(remaining.prefix(20))
            if batch.isEmpty {
                exhaustedChannelIDs.insert(channel.id)
                lastNotice = String(localized: "已加载该节目全部往期")
                return 0
            }
            let beforeCount = allIssues.count
            let batchFeed = ParsedPodcastFeed(
                title: feed.title,
                author: feed.author,
                summary: feed.summary,
                language: feed.language,
                link: feed.link,
                artworkUrl: feed.artworkUrl,
                episodes: batch
            )
            _ = try core.podcastUpsertChannelFromFeed(feedUrl: channel.feedUrl, feed: batchFeed, putLatestOnDesk: false)
            reload()
            let added = max(0, allIssues.count - beforeCount)
            if remaining.count <= batch.count {
                exhaustedChannelIDs.insert(channel.id)
            }
            if added > 0 {
                lastNotice = String(localized: "已加载 \(added) 期往期节目")
            } else {
                exhaustedChannelIDs.insert(channel.id)
                lastNotice = String(localized: "已加载该节目全部往期")
            }
            return added
        } catch {
            Self.logger.error("loadMoreEpisodes failed: \(error.localizedDescription)")
            lastNotice = error.localizedDescription
            return 0
        }
    }

    func importOPML(from fileURL: URL) async -> Int {
        let accessed = fileURL.startAccessingSecurityScopedResource()
        defer { if accessed { fileURL.stopAccessingSecurityScopedResource() } }
        guard let data = try? Data(contentsOf: fileURL),
              let xml = String(data: data, encoding: .utf8),
              let feeds = try? parsePodcastOpml(xml: xml)
        else {
            addError = String(localized: "无法读取这个 OPML 文件")
            return 0
        }
        var okCount = 0
        for item in feeds.prefix(25) {
            if await subscribeRSS(urlString: item.xmlUrl, putLatestOnDesk: okCount < 2, trackAdd: false) {
                okCount += 1
            }
        }
        if okCount > 0 { AirTraffic.trackAudioFeatureUsed(kind: "podcast_added") }
        return okCount
    }

    func importLocalAudio(from fileURL: URL) async -> PodcastIssue? {
        let accessed = fileURL.startAccessingSecurityScopedResource()
        defer { if accessed { fileURL.stopAccessingSecurityScopedResource() } }
        do {
            let destDir = dataDir.appending(path: "podcast_hub/local", directoryHint: .isDirectory)
            try FileManager.default.createDirectory(at: destDir, withIntermediateDirectories: true)
            let dest = destDir.appending(path: "\(UUID().uuidString.prefix(8))-\(fileURL.lastPathComponent)")
            try FileManager.default.copyItem(at: fileURL, to: dest)
            let asset = AVURLAsset(url: dest)
            let dur = (try? await asset.load(.duration))?.seconds ?? 0
            let title = fileURL.deletingPathExtension().lastPathComponent
            let issue = try core.podcastImportSingleIssue(
                title: title,
                showTitle: String(localized: "本地音频"),
                summary: String(localized: "已放入书桌 · 可随时听读与摘录"),
                shownotes: "",
                audioUrl: nil,
                localAudioPath: dest.path(percentEncoded: false),
                artworkUrl: nil,
                durationSec: UInt32(max(0, dur.rounded())),
                language: "zh-Hans",
                source: .localAudio
            )
            reload()
            AirTraffic.trackAudioFeatureUsed(kind: "podcast_added")
            return issue
        } catch {
            addError = error.localizedDescription
            return nil
        }
    }

    func fetchRemoteTranscript(for issueID: String, urlString: String, mime: String) async {
        guard let url = URL(string: urlString),
              let (data, _) = try? await URLSession.shared.data(from: url)
        else { return }
        let text = String(decoding: data, as: UTF8.self)
        let parsed = parsePodcastTranscript(content: text, mimeType: mime)
        guard !parsed.isEmpty else { return }
        try? core.podcastSetTranscript(issueId: issueID, sentences: parsed, fidelity: .l2Magazine)
        transcripts[issueID] = parsed
        highlightIDs.removeValue(forKey: issueID)
        reload()
    }

    /// On-device L1/L2 transcription via Apple Speech framework for local audio files when no
    /// `<podcast:transcript>` was provided.
    func transcribeOnDevice(issueID: String) async {
        guard let iss = issue(issueID),
              let path = iss.localAudioPath
        else { return }
        let locale = Locale(identifier: iss.language.hasPrefix("en") ? "en-US" : "zh-CN")
        let auth = await Self.requestSpeechAuthorization()
        guard auth == .authorized else { return }
        AirTraffic.trackAudioFeatureUsed(kind: "podcast_transcribe")
        let segments = await Self.recognizeLocalFile(url: URL(filePath: path), locale: locale)
        guard !segments.isEmpty else { return }
        var sentences: [MagazineSentence] = []
        var buf = ""
        var start = 0.0
        var end = 0.0
        for seg in segments {
            if buf.isEmpty { start = seg.timestamp }
            if !buf.isEmpty && !iss.language.hasPrefix("zh") { buf += " " }
            buf += seg.text
            end = seg.timestamp + seg.duration
            let endsSentence = seg.text.hasSuffix("。") || seg.text.hasSuffix(".") || seg.text.hasSuffix("?") || seg.text.hasSuffix("？")
            if endsSentence || (end - start) >= 9.0 {
                sentences.append(MagazineSentence(
                    id: "asr\(sentences.count)",
                    speaker: nil,
                    textOrig: buf.trimmingCharacters(in: .whitespaces),
                    textTrans: nil,
                    coachNote: nil,
                    t0: start,
                    t1: max(end, start + 0.5),
                    skipReason: nil,
                    bookId: nil,
                    bookTitle: nil,
                    bookSection: nil,
                    bookSentence: nil,
                    bookQuote: nil
                ))
                buf = ""
            }
        }
        if !buf.isEmpty {
            sentences.append(MagazineSentence(
                id: "asr\(sentences.count)",
                speaker: nil,
                textOrig: buf.trimmingCharacters(in: .whitespaces),
                textTrans: nil,
                coachNote: nil,
                t0: start,
                t1: max(end, start + 0.5),
                skipReason: nil,
                bookId: nil,
                bookTitle: nil,
                bookSection: nil,
                bookSentence: nil,
                bookQuote: nil
            ))
        }
        guard !sentences.isEmpty else { return }
        try? core.podcastSetTranscript(issueId: issueID, sentences: sentences, fidelity: .l1LiveTranscript)
        transcripts[issueID] = sentences
        reload()
    }

    // MARK: Private Studio ("书房电台") Generation

    /// Generates a Private Studio episode using strictly non-spoiler materials from Rust core,
    /// calling the configured Companion LLM when available and falling back gracefully offline.
    @discardableResult
    func generateStudioEpisode(
        kind: StudioKind,
        bookIDs: [String],
        minutes: UInt32,
        topic: String?,
        noSpoiler: Bool = true,
        voiceStyle: String? = nil
    ) async -> PodcastIssue? {
        guard !isGeneratingStudio else { return nil }
        if let voiceStyle, !voiceStyle.isEmpty {
            studioVoiceStyle = voiceStyle
        }
        isGeneratingStudio = true
        studioStatus = noSpoiler
            ? String(localized: "正在按当前阅读进度整理素材（防剧透锁已开启）…")
            : String(localized: "正在整理全书素材与划线…")
        defer {
            isGeneratingStudio = false
            studioStatus = nil
        }

        do {
            let brief = try core.buildStudioBrief(kind: kind, bookIds: bookIDs, minutes: minutes, topic: topic)
            var rawAnswer = ""
            let cfg = CompanionConfig.current
            if cfg.isReady {
                studioStatus = String(localized: "正在编排双人对谈与原文锚点…")
                var sysPrompt = noSpoiler
                    ? brief.systemPrompt
                    : brief.systemPrompt + "\n用户已手动关闭防剧透锁，可自由延伸探讨全书主题与后文脉络。"
                if studioVoiceStyle == "studio-solo-zh" {
                    sysPrompt += "\n本期采用林深单人沉静导读形式，请统一由「林深」主讲。"
                } else if studioVoiceStyle == "studio-duo-en" {
                    sysPrompt += "\nHost names: Clara and David, speaking natural conversational English."
                }
                if let reply = try? await Companion.answer(cfg, system: sysPrompt, prompt: brief.userPrompt, maxTokens: 2048, source: .podcast) {
                    rawAnswer = reply
                }
            }
            let draft = core.finalizeStudioScript(brief: brief, rawJson: rawAnswer)
            let lang = (kind == .vocabRadio || studioVoiceStyle == "studio-duo-en") ? "en" : "zh-Hans"
            let issue = try core.podcastSaveStudioIssue(kind: kind, draft: draft, bookIds: bookIDs, language: lang)
            transcripts[issue.id] = draft.sentences
            reload()
            AirTraffic.trackAudioFeatureUsed(kind: "podcast_added")
            start(issue, from: 0)
            return issue
        } catch {
            Self.logger.error("studio generation failed: \(error.localizedDescription)")
            lastNotice = error.localizedDescription
            return nil
        }
    }

    // MARK: Playback Control

    func selectAndPlay(_ issue: PodcastIssue) {
        if activeIssueID == issue.id {
            togglePlay()
            return
        }
        start(issue, from: issue.positionSec)
    }

    func start(_ issue: PodcastIssue, from seconds: Double? = nil) {
        stopAudioOnly()
        claimRemote()
        activeIssueID = issue.id
        AirTraffic.trackAudioFeatureUsed(kind: "podcast_play")
        let startAt = seconds ?? issue.positionSec
        currentTime = max(0, startAt)
        duration = Double(issue.durationSec)
        updateActiveSentence()

        if !shouldPlayTranslatedScript(for: issue), let url = resolvedAudioURL(for: issue) {
            playAVPlayer(url: url, from: startAt, issue: issue)
        } else {
            playStudioScript(issueID: issue.id, from: startAt)
        }
    }

    func togglePlay() {
        guard let issue = activeIssue else { return }
        if isPlaying {
            pause()
        } else {
            if avPlayer != nil, !shouldPlayTranslatedScript(for: issue) {
                claimRemote()
                avPlayer?.playImmediately(atRate: rate)
                isPlaying = true
                updateNowPlaying(issue)
            } else {
                start(issue, from: currentTime)
            }
        }
    }

    func pause() {
        isPlaying = false
        avPlayer?.pause()
        studioSpeechTask?.cancel()
        studioSpeechTask = nil
        _pcmPlayer?.stop()
        studioDelegate?.cancelActive()
        studioSynth?.stopSpeaking(at: .immediate)
        savePlaybackProgress(force: true)
        if let issue = activeIssue { updateNowPlaying(issue) }
    }

    func stop() {
        pause()
        stopAudioOnly()
        releaseRemote()
        activeIssueID = nil
    }

    func skip(by delta: Double) {
        let target = max(0, min(duration > 0 ? duration : currentTime + 3600, currentTime + delta))
        seek(to: target)
    }

    func seek(to seconds: Double) {
        let clamped = max(0, duration > 0 ? min(duration, seconds) : seconds)
        currentTime = clamped
        updateActiveSentence()
        if let avPlayer {
            let cm = CMTime(seconds: clamped, preferredTimescale: 600)
            avPlayer.seek(to: cm, toleranceBefore: .zero, toleranceAfter: .zero)
        } else if isPlaying, let id = activeIssueID {
            playStudioScript(issueID: id, from: clamped)
        }
        savePlaybackProgress(force: true)
        if let issue = activeIssue { updateNowPlaying(issue) }
    }

    func jump(to sentence: MagazineSentence) {
        seek(to: sentence.t0)
        activeSentenceID = sentence.id
        if !isPlaying, let issue = activeIssue {
            start(issue, from: sentence.t0)
        }
    }

    func setDucking(_ duck: Bool) {
        isDucked = duck
        // -24 dB ≈ 0.063 linear gain.
        avPlayer?.volume = duck ? 0.065 : 1.0
        _pcmPlayer?.setDucked(duck)
        if duck && studioSynth?.isSpeaking == true {
            studioSynth?.pauseSpeaking(at: .word)
        }
    }

    /// Ends a cut-in Q&A session and rewinds 3 seconds so the listener lands cleanly back in context.
    func resumeAfterInterrupt() {
        stopVoiceInterrupt()
        stopSpeakingInterruptAnswer()
        setDucking(false)
        let target = max(0, currentTime - 3.0)
        seek(to: target)
        if !isPlaying, let issue = activeIssue {
            if avPlayer != nil, !shouldPlayTranslatedScript(for: issue) {
                claimRemote()
                avPlayer?.playImmediately(atRate: rate)
                isPlaying = true
                updateNowPlaying(issue)
            } else {
                start(issue, from: target)
            }
        }
    }

    // MARK: Live On-Device Voice Interject ("语音插话")

    /// Starts live on-device microphone capture + real-time speech-to-text for hands-free cut-in Q&A.
    @discardableResult
    func startVoiceInterrupt(
        language: String = "zh-CN",
        onTranscript: @escaping @MainActor (String) -> Void
    ) async -> Bool {
        stopVoiceInterrupt()
        stopSpeakingInterruptAnswer()
        interruptVoiceHint = nil

        let micAllowed = await AVAudioApplication.requestRecordPermission()
        guard micAllowed else {
            interruptVoiceHint = String(localized: "未开启麦克风权限，可在系统设置里开启，或直接打字提问")
            return false
        }

        let speechAuth = await Self.requestSpeechAuthorization()
        guard speechAuth == .authorized else {
            interruptVoiceHint = String(localized: "未开启语音识别权限，可直接打字提问")
            return false
        }

        let capture = PodcastVoiceCapture()
        let ok = capture.start(
            localeID: language,
            onPartial: { [weak self] text in
                Task { @MainActor in
                    guard let self, self.isListeningInterrupt else { return }
                    onTranscript(text)
                }
            },
            onLevel: { [weak self] level in
                Task { @MainActor in
                    guard let self, self.isListeningInterrupt else { return }
                    self.interruptVoiceLevel = level
                }
            },
            onError: { [weak self] message in
                Task { @MainActor in
                    guard let self else { return }
                    self.stopVoiceInterrupt()
                    if !message.isEmpty {
                        self.interruptVoiceHint = message
                    }
                }
            }
        )

        if ok {
            voiceCapture = capture
            isListeningInterrupt = true
            interruptVoiceLevel = 0.14
            return true
        } else {
            interruptVoiceHint = String(localized: "当前设备语音听写暂不可用，可直接打字提问")
            return false
        }
    }

    func stopVoiceInterrupt() {
        voiceCapture?.stop()
        voiceCapture = nil
        isListeningInterrupt = false
        interruptVoiceLevel = 0
    }

    func speakInterruptAnswer(_ text: String, language: String = "zh-CN") {
        stopSpeakingInterruptAnswer()
        isSpeakingInterruptReply = true
        Task { [weak self] in
            guard let self else { return }
            if let voice = self.resolveStudioVoices(for: language).primary,
               let speech = try? await voice.render(text, language: language),
               !Task.isCancelled, self.isSpeakingInterruptReply {
                AirTraffic.trackTtsStarted(engine: AirTraffic.ttsEngine(for: voice), surface: "podcast")
                await self.pcmPlayer.play(speech, rate: 1.0, ducked: false)
                self.isSpeakingInterruptReply = false
                return
            }
            guard !Task.isCancelled, self.isSpeakingInterruptReply else { return }
            AirTraffic.trackTtsStarted(engine: "system", surface: "podcast")
            let synth = self.replySynth ?? AVSpeechSynthesizer()
            self.replySynth = synth
            let utterance = AVSpeechUtterance(string: text)
            utterance.voice = Self.distinctSystemVoices(for: language).primary
            utterance.rate = AVSpeechUtteranceDefaultSpeechRate * 1.04
            synth.speak(utterance)
            while self.isSpeakingInterruptReply {
                try? await Task.sleep(for: .milliseconds(250))
                if self.replySynth?.isSpeaking != true {
                    self.isSpeakingInterruptReply = false
                    break
                }
            }
        }
    }

    func stopSpeakingInterruptAnswer() {
        _pcmPlayer?.stop()
        replySynth?.stopSpeaking(at: .immediate)
        isSpeakingInterruptReply = false
    }

    /// Plays an authentic preview for a voice card in the Podcast Voices drawer (`试听`),
    /// using Yuyue's real `CardVoice` / `ClonedVoice` when installed and distinct system voices otherwise.
    func auditionVoiceCard(id: String, text: String, language: String = "zh-CN") async {
        Voices.stopPreview()
        replySynth?.stopSpeaking(at: .immediate)
        _pcmPlayer?.stop()
        if isPlaying { pause() }

        let targetVoice: (any Voice)? = {
            if id.hasPrefix("clone-") {
                let rawID = String(id.dropFirst("clone-".count))
                return ClonedVoices.resolveVoice(rawID, bookID: nil)
            }
            if id == "self-clone" {
                if let first = ClonedVoices.entries().first(where: \.isPlayable) {
                    return ClonedVoices.resolveVoice(first.id, bookID: nil)
                }
                return nil
            }
            let pair = resolveStudioVoices(for: language)
            return id == "studio-su" ? (pair.secondary ?? pair.primary) : pair.primary
        }()

        if let targetVoice,
           let speech = try? await targetVoice.render(text, language: language),
           !Task.isCancelled {
            await pcmPlayer.play(speech, rate: 1.0, ducked: false)
            return
        }

        guard !Task.isCancelled else { return }
        let sysVoices = Self.distinctSystemVoices(for: language)
        let delegate = StudioSpeechDelegate()
        studioDelegate = delegate
        let synth = replySynth ?? AVSpeechSynthesizer()
        replySynth = synth
        synth.delegate = delegate
        let picked = id == "studio-su" ? sysVoices.secondary : sysVoices.primary
        await delegate.speak(synthesizer: synth, text: text, voice: picked, rate: 1.0, pitch: id == "studio-su" ? 1.05 : 0.98)
    }

    func stopAudition() {
        _pcmPlayer?.stop()
        studioDelegate?.cancelActive()
        replySynth?.stopSpeaking(at: .immediate)
    }

    // MARK: Blind Clip-Marking ("记这句 H") & Audio Micro-Clip Replay

    @discardableResult
    func markCurrentMoment(thought: String? = nil) async -> PodcastClip? {
        guard let issue = activeIssue else { return nil }
        let sents = sentences(for: issue.id)
        let win = resolveClipWindow(sentences: sents, tPress: currentTime, durationSec: duration)
        let quote = win.quoteText.isEmpty
            ? String(localized: "原声片段 \(PodcastFormat.clock(win.t0)) – \(PodcastFormat.clock(win.t1))")
            : win.quoteText

        var relPath: String?
        if let url = resolvedAudioURL(for: issue), url.isFileURL {
            relPath = await sliceLocalClip(from: url, t0: win.t0, t1: win.t1)
        }

        guard let saved = try? core.podcastAddClip(
            issueId: issue.id,
            t0: win.t0,
            t1: win.t1,
            speaker: win.speaker,
            quoteText: quote,
            thought: thought,
            audioRelPath: relPath
        ) else { return nil }
        reload()
        lastNotice = String(localized: "已记下这句原声")
        return saved
    }

    func deleteClip(_ clip: PodcastClip) {
        try? core.podcastDeleteClip(clipId: clip.id)
        reload()
    }

    /// Replays a saved audio micro-clip (from `Today's Review` or sidebar `原声摘`).
    func replayClip(_ clip: PodcastClip) {
        if playingClipID == clip.id {
            clipPlayer?.stop()
            playingClipID = nil
            return
        }
        clipPlayer?.stop()
        if let rel = clip.audioRelPath {
            let full = URL(filePath: core.podcastClipsDir()).appending(path: rel)
            if FileManager.default.fileExists(atPath: full.path()),
               let p = try? AVAudioPlayer(contentsOf: full) {
                clipPlayer = p
                playingClipID = clip.id
                p.play()
                Task { [weak self] in
                    try? await Task.sleep(for: .seconds(max(1.0, clip.t1 - clip.t0 + 0.3)))
                    if self?.playingClipID == clip.id { self?.playingClipID = nil }
                }
                return
            }
        }
        // Fallback: jump the source issue to clip.t0 and play.
        if let iss = issue(clip.issueId) {
            start(iss, from: clip.t0)
        }
    }

    // MARK: Cut-in Q&A ("插话")

    @discardableResult
    func submitInterrupt(issueID: String, question: String) async -> PodcastInterrupt? {
        let q = question.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !q.isEmpty, let iss = issue(issueID) else { return nil }
        let atSec = currentTime
        let sents = sentences(for: issueID)
        let contextLines = sents
            .filter { $0.t0 <= atSec + 2 && $0.t1 >= max(0, atSec - 180) }
            .suffix(10)
            .map { ($0.speaker.map { "\($0): " } ?? "") + $0.textOrig }
            .joined(separator: "\n")

        let answer: String
        let cfg = CompanionConfig.current
        if cfg.isReady {
            let sys = "你是正在陪用户听播客的书伴。回答简短克制（2–4 句话），严格基于已听到的片段内容，不要剧透后面未听到的部分。"
            let usr = "节目：《\(iss.title)》（\(iss.showTitle)）\n当前位置：\(PodcastFormat.clock(atSec))\n刚才听到的内容：\n\(contextLines)\n\n用户插话提问：\(q)"
            if let reply = try? await Companion.answer(cfg, system: sys, prompt: usr, maxTokens: 512, fast: true, source: .podcast), !reply.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                answer = reply.trimmingCharacters(in: .whitespacesAndNewlines)
            } else {
                answer = offlineInterruptReply(question: q, context: contextLines, issue: iss)
            }
        } else {
            answer = offlineInterruptReply(question: q, context: contextLines, issue: iss)
        }

        guard let saved = try? core.podcastAddInterrupt(issueId: issueID, atSec: atSec, question: q, answer: answer) else {
            return nil
        }
        interrupts[issueID] = (try? core.podcastInterrupts(issueId: issueID)) ?? []
        return saved
    }

    private func offlineInterruptReply(question: String, context: String, issue: PodcastIssue) -> String {
        if context.isEmpty {
            return String(localized: "已记下你在《\(issue.title)》此处的问题「\(question)」。在设置里连上书伴模型后，插话可以结合上下文与书架笔记实时作答。")
        }
        return String(localized: "已锚定到刚才听到的片段并存入边注。配置书伴模型后，这里会直接结合你的在读书籍与上下文回答「\(question)」。")
    }

    // MARK: Private Helpers

    private func setupAudioObservers() {
        let center = NotificationCenter.default
        observers.append(center.addObserver(forName: .yyAudioClaimed, object: nil, queue: .main) { [weak self] note in
            let isOther = (note.object as AnyObject?) !== self
            MainActor.assumeIsolated {
                guard let self, isOther else { return }
                if self.isPlaying { self.pause() }
                self.releaseRemote()
            }
        })
        #if os(iOS)
        observers.append(center.addObserver(forName: AVAudioSession.interruptionNotification, object: nil, queue: .main) { [weak self] note in
            let type = note.userInfo?[AVAudioSessionInterruptionTypeKey] as? UInt
            let options = note.userInfo?[AVAudioSessionInterruptionOptionKey] as? UInt ?? 0
            let began = type == AVAudioSession.InterruptionType.began.rawValue
            let resume = AVAudioSession.InterruptionOptions(rawValue: options).contains(.shouldResume)
            MainActor.assumeIsolated {
                guard let self, self.ownsRemote else { return }
                if began, self.isPlaying {
                    self.pause()
                } else if resume, !self.isPlaying, self.activeIssue != nil {
                    self.togglePlay()
                }
            }
        })
        observers.append(center.addObserver(forName: AVAudioSession.routeChangeNotification, object: nil, queue: .main) { [weak self] note in
            let reason = note.userInfo?[AVAudioSessionRouteChangeReasonKey] as? UInt
            guard reason == AVAudioSession.RouteChangeReason.oldDeviceUnavailable.rawValue else { return }
            MainActor.assumeIsolated {
                guard let self, self.isPlaying else { return }
                self.pause()
            }
        })
        #endif
    }

    private func claimRemote() {
        NotificationCenter.default.post(name: .yyAudioClaimed, object: self)
        guard !ownsRemote else { return }
        ownsRemote = true
        let center = MPRemoteCommandCenter.shared()
        let handlers: [(MPRemoteCommand, @MainActor @Sendable (PodcastHub) -> Void)] = [
            (center.playCommand, { if !$0.isPlaying { $0.togglePlay() } }),
            (center.pauseCommand, { if $0.isPlaying { $0.pause() } }),
            (center.togglePlayPauseCommand, { $0.togglePlay() }),
            (center.skipBackwardCommand, { $0.skip(by: -15) }),
            (center.skipForwardCommand, { $0.skip(by: 30) }),
            (center.previousTrackCommand, { $0.skip(by: -15) }),
            (center.nextTrackCommand, { $0.skip(by: 30) }),
            (center.bookmarkCommand, { hub in Task { _ = await hub.markCurrentMoment() } }),
        ]
        center.skipBackwardCommand.preferredIntervals = [15]
        center.skipForwardCommand.preferredIntervals = [30]
        for (command, action) in handlers {
            command.isEnabled = true
            let target = command.addTarget(handler: Self.remoteCommand { [weak self] in
                guard let self else { return }
                action(self)
            })
            remoteTargets.append((command, target))
        }
        let pos = center.changePlaybackPositionCommand
        pos.isEnabled = true
        remoteTargets.append((pos, pos.addTarget(handler: Self.remotePositionCommand { [weak self] sec in
            self?.seek(to: sec)
        })))
        let speeds = center.changePlaybackRateCommand
        speeds.isEnabled = true
        speeds.supportedPlaybackRates = [0.8, 1.0, 1.2, 1.5, 1.8, 2.0]
        remoteTargets.append((speeds, speeds.addTarget(handler: Self.remoteRateCommand { [weak self] r in
            self?.rate = r
        })))
    }

    private func releaseRemote() {
        for (command, target) in remoteTargets {
            command.removeTarget(target)
        }
        remoteTargets = []
        ownsRemote = false
    }

    nonisolated private static func remoteCommand(_ work: @escaping @MainActor @Sendable () -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { _ in
            Task { @MainActor in work() }
            return .success
        }
    }

    nonisolated private static func remotePositionCommand(_ work: @escaping @MainActor @Sendable (Double) -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { event in
            guard let sec = (event as? MPChangePlaybackPositionCommandEvent)?.positionTime else { return .commandFailed }
            Task { @MainActor in work(sec) }
            return .success
        }
    }

    nonisolated private static func remoteRateCommand(_ work: @escaping @MainActor @Sendable (Float) -> Void) -> @Sendable (MPRemoteCommandEvent) -> MPRemoteCommandHandlerStatus {
        { event in
            guard let rate = (event as? MPChangePlaybackRateCommandEvent)?.playbackRate else { return .commandFailed }
            Task { @MainActor in work(rate) }
            return .success
        }
    }

    private func resolveStudioVoices(for language: String) -> (primary: (any Voice)?, secondary: (any Voice)?) {
        if studioVoiceStyle.hasPrefix("cloned:") {
            let rawID = String(studioVoiceStyle.dropFirst("cloned:".count))
            if isVoiceRemembered("clone-\(rawID)"),
               let cloned = ClonedVoices.resolveVoice(rawID, bookID: nil) {
                let fallbackCards = LiveVoiceModel.isInstalled ? VoicePack.builtinCards(for: language) : []
                let partner = fallbackCards.first { $0.gender == .f } ?? fallbackCards.first
                return (cloned, partner.map { CardVoice(card: $0, bookID: nil) } ?? cloned)
            }
        }
        guard LiveVoiceModel.isInstalled else { return (nil, nil) }
        let cards = VoicePack.builtinCards(for: language)
        guard !cards.isEmpty else { return (nil, nil) }
        let male = cards.first { $0.narrator && $0.gender == .m }
            ?? cards.first { $0.gender == .m }
            ?? cards.first
        let female = cards.first { $0.narrator && $0.gender == .f && $0.id != male?.id }
            ?? cards.first { $0.gender == .f && $0.id != male?.id }
            ?? cards.first { $0.id != male?.id }
            ?? male
        let primaryCard = isVoiceRemembered("studio-lin") ? male : female
        if studioVoiceStyle == "studio-solo-zh" {
            let v = primaryCard.map { CardVoice(card: $0, bookID: nil) }
            return (v, v)
        }
        let secondaryCard = isVoiceRemembered("studio-su") ? female : male
        return (
            primaryCard.map { CardVoice(card: $0, bookID: nil) },
            secondaryCard.map { CardVoice(card: $0, bookID: nil) }
        )
    }

    nonisolated fileprivate static func distinctSystemVoices(for language: String) -> (primary: AVSpeechSynthesisVoice?, secondary: AVSpeechSynthesisVoice?) {
        let prefix = language.lowercased().hasPrefix("en") ? "en" : "zh"
        let matching = AVSpeechSynthesisVoice.speechVoices()
            .filter { $0.language.lowercased().hasPrefix(prefix) }
            .sorted { $0.quality.rawValue > $1.quality.rawValue }
        let first = matching.first ?? AVSpeechSynthesisVoice(language: prefix == "en" ? "en-US" : "zh-CN")
        let second = matching.first { $0.identifier != first?.identifier } ?? first
        return (first, second)
    }

    nonisolated private static func requestSpeechAuthorization() async -> SFSpeechRecognizerAuthorizationStatus {
        let current = SFSpeechRecognizer.authorizationStatus()
        guard current == .notDetermined else { return current }
        return await withCheckedContinuation { cont in
            let resumed = Mutex(false)
            SFSpeechRecognizer.requestAuthorization { status in
                let shouldResume = resumed.withLock { done -> Bool in
                    if done { return false }
                    done = true
                    return true
                }
                if shouldResume {
                    cont.resume(returning: status)
                }
            }
        }
    }

    nonisolated private static func recognizeLocalFile(
        url: URL,
        locale: Locale
    ) async -> [(text: String, timestamp: Double, duration: Double)] {
        guard let recognizer = SFSpeechRecognizer(locale: locale), recognizer.isAvailable else { return [] }
        let req = SFSpeechURLRecognitionRequest(url: url)
        req.requiresOnDeviceRecognition = recognizer.supportsOnDeviceRecognition
        req.addsPunctuation = true
        return await withCheckedContinuation { cont in
            let resumed = Mutex(false)
            _ = recognizer.recognitionTask(with: req) { res, err in
                if let res, res.isFinal {
                    let shouldResume = resumed.withLock { done -> Bool in
                        if done { return false }
                        done = true
                        return true
                    }
                    if shouldResume {
                        let mapped = res.bestTranscription.segments.map { ($0.substring, $0.timestamp, $0.duration) }
                        cont.resume(returning: mapped)
                    }
                } else if err != nil {
                    let shouldResume = resumed.withLock { done -> Bool in
                        if done { return false }
                        done = true
                        return true
                    }
                    if shouldResume {
                        cont.resume(returning: [])
                    }
                }
            }
        }
    }

    private func resolvedAudioURL(for issue: PodcastIssue) -> URL? {
        if let local = issue.localAudioPath, !local.isEmpty, FileManager.default.fileExists(atPath: local) {
            return URL(filePath: local)
        }
        if let remote = issue.audioUrl, !remote.isEmpty, let url = URL(string: remote) {
            return url
        }
        return nil
    }

    private func playAVPlayer(url: URL, from startAt: Double, issue: PodcastIssue) {
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, options: [.allowAirPlay, .allowBluetoothA2DP])
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif
        let item = AVPlayerItem(url: url)
        let player = AVPlayer(playerItem: item)
        player.volume = isDucked ? 0.065 : 1.0
        avPlayer = player

        if startAt > 0.5 {
            player.seek(to: CMTime(seconds: startAt, preferredTimescale: 600))
        }

        let interval = CMTime(seconds: 0.25, preferredTimescale: 600)
        timeObserver = player.addPeriodicTimeObserver(forInterval: interval, queue: .main) { [weak self] time in
            MainActor.assumeIsolated {
                guard let self else { return }
                let sec = time.seconds
                if sec.isFinite && sec >= 0 {
                    self.currentTime = sec
                    if let d = self.avPlayer?.currentItem?.duration.seconds, d.isFinite && d > 0, abs(self.duration - d) > 0.05 {
                        self.duration = d
                    }
                    self.updateActiveSentence()
                    self.savePlaybackProgress(force: false)
                }
            }
        }
        itemEndObserver = NotificationCenter.default.addObserver(forName: .AVPlayerItemDidPlayToEndTime, object: item, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.handleIssueFinished()
            }
        }
        player.playImmediately(atRate: rate)
        isPlaying = true
        updateNowPlaying(issue)
    }

    private func playStudioScript(issueID: String, from startAt: Double) {
        studioSpeechTask?.cancel()
        _pcmPlayer?.stop()
        studioDelegate?.cancelActive()
        studioSynth?.stopSpeaking(at: .immediate)
        let sents = sentences(for: issueID)
        guard !sents.isEmpty else {
            isPlaying = false
            return
        }
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, options: [.allowAirPlay, .allowBluetoothA2DP])
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif
        let synth = studioSynth ?? AVSpeechSynthesizer()
        studioSynth = synth
        let delegate = StudioSpeechDelegate()
        studioDelegate = delegate
        synth.delegate = delegate
        isPlaying = true
        if let last = sents.last { duration = max(duration, last.t1) }

        let startIndex = sents.firstIndex(where: { $0.t1 > startAt }) ?? 0
        let iss = issue(issueID)
        let track = iss?.track ?? .original
        let lang = iss?.language ?? "zh-Hans"
        let baseSpokenLang = track == .dub ? "zh-CN" : (lang.hasPrefix("en") ? "en-US" : "zh-CN")
        LiveVoiceDownload.shared.startIfWifi(for: baseSpokenLang)
        if let iss { updateNowPlaying(iss) }

        func sentenceSpeechInput(_ s: MagazineSentence, index: Int) -> (text: String, lang: String, secondSpeaker: Bool) {
            let spokenText: String
            let spokenLang: String
            switch track {
            case .dub:
                spokenText = s.textTrans ?? s.textOrig
                spokenLang = "zh-CN"
            case .coach:
                spokenText = s.coachNote.map { "\(s.textOrig) \($0)" } ?? s.textOrig
                spokenLang = lang.hasPrefix("en") ? "en-US" : "zh-CN"
            case .original:
                spokenText = s.textOrig
                spokenLang = lang.hasPrefix("en") ? "en-US" : "zh-CN"
            }
            let spk = (s.speaker ?? "").lowercased()
            let isSecond = spk.contains("苏") || spk.contains("clara") || spk.contains("b") || (s.speaker == nil && index % 2 == 1)
            return (spokenText, spokenLang, isSecond)
        }

        studioSpeechTask = Task { [weak self] in
            guard let self else { return }
            let sysVoices = Self.distinctSystemVoices(for: baseSpokenLang)
            var prefetched: (index: Int, engine: String, task: Task<Speech?, Never>)?
            var trackedTts = false

            @MainActor func startRender(at i: Int) -> (engine: String, task: Task<Speech?, Never>)? {
                guard i < sents.count, sents[i].skipReason == nil else { return nil }
                let info = sentenceSpeechInput(sents[i], index: i)
                let pair = self.resolveStudioVoices(for: info.lang)
                guard let voice = info.secondSpeaker ? (pair.secondary ?? pair.primary) : pair.primary else {
                    return nil
                }
                let engine = AirTraffic.ttsEngine(for: voice)
                return (engine, Task { try? await voice.render(info.text, language: info.lang) })
            }

            for idx in startIndex ..< sents.count {
                if Task.isCancelled { break }
                let s = sents[idx]
                if s.skipReason != nil { continue }
                self.currentTime = s.t0
                self.activeSentenceID = s.id
                if let iss = self.issue(issueID) { self.updateNowPlaying(iss) }

                let info = sentenceSpeechInput(s, index: idx)
                let currentRender: (engine: String, task: Task<Speech?, Never>)? = if let pre = prefetched, pre.index == idx {
                    (pre.engine, pre.task)
                } else {
                    startRender(at: idx)
                }
                if let nextIdx = ((idx + 1) ..< sents.count).first(where: { sents[$0].skipReason == nil }),
                   let nextRender = startRender(at: nextIdx) {
                    prefetched = (nextIdx, nextRender.engine, nextRender.task)
                } else {
                    prefetched = nil
                }

                if let currentRender, let rendered = await currentRender.task.value, !Task.isCancelled {
                    if !trackedTts {
                        trackedTts = true
                        AirTraffic.trackTtsStarted(engine: currentRender.engine, surface: "podcast")
                    }
                    await self.pcmPlayer.play(rendered, rate: self.rate, ducked: self.isDucked)
                } else if !Task.isCancelled {
                    if !trackedTts {
                        trackedTts = true
                        AirTraffic.trackTtsStarted(engine: "system", surface: "podcast")
                    }
                    let chosenSys = (info.secondSpeaker && self.studioVoiceStyle != "studio-solo-zh") ? sysVoices.secondary : sysVoices.primary
                    let fallbackPitch: Float = (info.secondSpeaker && chosenSys?.identifier == sysVoices.primary?.identifier && self.studioVoiceStyle != "studio-solo-zh") ? 1.06 : 1.0
                    await delegate.speak(synthesizer: synth, text: info.text, voice: chosenSys, rate: self.rate, pitch: fallbackPitch)
                }
                if Task.isCancelled { break }
                self.currentTime = s.t1
                self.savePlaybackProgress(force: false)
            }
            prefetched?.task.cancel()
            if !Task.isCancelled {
                self.handleIssueFinished()
            }
        }
    }

    private func stopAudioOnly() {
        if let obs = timeObserver {
            avPlayer?.removeTimeObserver(obs)
            timeObserver = nil
        }
        if let end = itemEndObserver {
            NotificationCenter.default.removeObserver(end)
            itemEndObserver = nil
        }
        avPlayer?.pause()
        avPlayer = nil
        studioSpeechTask?.cancel()
        studioSpeechTask = nil
        _pcmPlayer?.stop()
        studioDelegate?.cancelActive()
        studioSynth?.stopSpeaking(at: .immediate)
        isPlaying = false
        isDucked = false
    }

    private func applyRate() {
        _pcmPlayer?.setRate(rate)
        if isPlaying, let avPlayer {
            avPlayer.rate = rate
        }
        if let issue = activeIssue { updateNowPlaying(issue) }
    }

    private func updateActiveSentence() {
        guard let id = activeIssueID else { return }
        let sents = sentences(for: id)
        guard !sents.isEmpty else { return }
        var firstSpoken: MagazineSentence?
        var hit: MagazineSentence?
        var lastPassed: MagazineSentence?
        for s in sents where s.skipReason == nil {
            if firstSpoken == nil { firstSpoken = s }
            if currentTime >= s.t0 && currentTime <= s.t1 {
                hit = s
                break
            }
            if currentTime >= s.t0 {
                lastPassed = s
            }
        }
        if let chosen = hit ?? lastPassed ?? firstSpoken, activeSentenceID != chosen.id {
            activeSentenceID = chosen.id
        }
    }

    private func savePlaybackProgress(force: Bool) {
        guard let id = activeIssueID else { return }
        if !force && Date().timeIntervalSince(lastSavedPositionAt) < 4 { return }
        lastSavedPositionAt = .now
        let dur = duration > 0 ? UInt32(duration.rounded()) : nil
        try? core.podcastSavePlayback(issueId: id, positionSec: currentTime, durationSec: dur)
    }

    private func handleIssueFinished() {
        isPlaying = false
        savePlaybackProgress(force: true)
        if let issue = activeIssue { updateNowPlaying(issue) }
        reload()
    }

    private func updateNowPlaying(_ issue: PodcastIssue) {
        guard ownsRemote else { return }
        MPNowPlayingInfoCenter.default().nowPlayingInfo = [
            MPMediaItemPropertyTitle: issue.title,
            MPMediaItemPropertyArtist: issue.showTitle,
            MPMediaItemPropertyPlaybackDuration: duration,
            MPNowPlayingInfoPropertyElapsedPlaybackTime: currentTime,
            MPNowPlayingInfoPropertyPlaybackRate: isPlaying ? Double(rate) : 0.0,
        ]
    }

    private func sliceLocalClip(from sourceURL: URL, t0: Double, t1: Double) async -> String? {
        let asset = AVURLAsset(url: sourceURL)
        guard let session = AVAssetExportSession(asset: asset, presetName: AVAssetExportPresetAppleM4A) else { return nil }
        let fileName = "clip-\(UUID().uuidString.prefix(10)).m4a"
        let outURL = URL(filePath: core.podcastClipsDir()).appending(path: fileName)
        session.timeRange = CMTimeRange(
            start: CMTime(seconds: max(0, t0), preferredTimescale: 600),
            duration: CMTime(seconds: max(1.0, t1 - t0), preferredTimescale: 600)
        )
        do {
            try await session.export(to: outURL, as: .m4a)
            return fileName
        } catch {
            return nil
        }
    }
}

/// Plays rendered `Speech` (`AVAudioPCMBuffer` in `Speech.format`) with live rate and ducking control.
private final class PodcastPCMPlayer {
    private let engine = AVAudioEngine()
    private let node = AVAudioPlayerNode()
    private let speed = AVAudioUnitTimePitch()
    private var continuation: CheckedContinuation<Void, Never>?
    private var generation = 0

    init() {
        engine.attach(node)
        engine.attach(speed)
        engine.connect(node, to: speed, format: Speech.format)
        engine.connect(speed, to: engine.mainMixerNode, format: Speech.format)
    }

    func setRate(_ rate: Float) {
        speed.rate = max(0.5, min(2.5, rate))
    }

    func setDucked(_ ducked: Bool) {
        node.volume = ducked ? 0.065 : 1.0
    }

    func play(_ speech: Speech, rate: Float, ducked: Bool) async {
        stop()
        speed.rate = max(0.5, min(2.5, rate))
        node.volume = ducked ? 0.065 : 1.0
        if !engine.isRunning {
            engine.prepare()
            do {
                try engine.start()
            } catch {
                return
            }
        }
        generation &+= 1
        let mine = generation
        await withTaskCancellationHandler {
            await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
                if Task.isCancelled {
                    cont.resume()
                    return
                }
                self.continuation = cont
                self.node.scheduleBuffer(speech.buffer, at: nil, options: [], completionCallbackType: .dataPlayedBack, completionHandler: Self.onMain { [weak self] in
                    guard let self, self.generation == mine else { return }
                    self.finishContinuation()
                })
                self.node.play()
            }
        } onCancel: {
            Task { @MainActor [weak self] in
                self?.stop()
            }
        }
    }

    func stop() {
        generation &+= 1
        if engine.isRunning {
            node.stop()
        }
        finishContinuation()
    }

    private func finishContinuation() {
        let cont = continuation
        continuation = nil
        cont?.resume()
    }

    nonisolated private static func onMain(_ work: @escaping @MainActor @Sendable () -> Void) -> @Sendable (AVAudioPlayerNodeCompletionCallbackType) -> Void {
        { _ in Task { @MainActor in work() } }
    }
}

/// Bridges `AVSpeechSynthesizerDelegate` to `async/await` for fallback Studio script playback.
private final class StudioSpeechDelegate: NSObject, AVSpeechSynthesizerDelegate, @unchecked Sendable {
    private var continuation: CheckedContinuation<Void, Never>?
    private weak var activeSynthesizer: AVSpeechSynthesizer?

    deinit {
        continuation?.resume()
    }

    func speak(synthesizer: AVSpeechSynthesizer, text: String, voice: AVSpeechSynthesisVoice?, rate: Float, pitch: Float) async {
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        resumeIfNeeded()
        activeSynthesizer = synthesizer
        let utterance = AVSpeechUtterance(string: trimmed)
        utterance.voice = voice
        utterance.rate = min(AVSpeechUtteranceMaximumSpeechRate, max(AVSpeechUtteranceMinimumSpeechRate, AVSpeechUtteranceDefaultSpeechRate * rate))
        utterance.pitchMultiplier = pitch
        await withTaskCancellationHandler {
            await self.awaitUtterance(utterance)
        } onCancel: {
            Task { @MainActor in
                self.cancelActive()
            }
        }
    }

    private func awaitUtterance(_ utterance: AVSpeechUtterance) async {
        await withCheckedContinuation { (cont: CheckedContinuation<Void, Never>) in
            if Task.isCancelled {
                cont.resume()
                return
            }
            self.continuation = cont
            self.activeSynthesizer?.speak(utterance)
        }
    }

    func cancelActive() {
        activeSynthesizer?.stopSpeaking(at: .immediate)
        resumeIfNeeded()
    }

    private func resumeIfNeeded() {
        let cont = continuation
        continuation = nil
        cont?.resume()
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        Task { @MainActor in self.resumeIfNeeded() }
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        Task { @MainActor in self.resumeIfNeeded() }
    }
}

/// Nonisolated live microphone + `SFSpeechRecognizer` bridge for hands-free cut-in Q&A ("语音插话").
///
/// Explicitly `nonisolated` so `AVAudioEngine.inputNode.installTap` and `SFSpeechRecognizer`
/// Objective-C blocks never inherit `@MainActor` executor assertions on CoreAudio's realtime IO thread.
nonisolated private final class PodcastVoiceCapture: @unchecked Sendable {
    private struct State: @unchecked Sendable {
        var engine: AVAudioEngine?
        var request: SFSpeechAudioBufferRecognitionRequest?
        var task: SFSpeechRecognitionTask?
        var running = false
        var sampleTick: UInt32 = 0
    }

    private let state = Mutex(State())

    func start(
        localeID: String,
        onPartial: @escaping @Sendable (String) -> Void,
        onLevel: @escaping @Sendable (CGFloat) -> Void,
        onError: @escaping @Sendable (String) -> Void
    ) -> Bool {
        stop()

        let candidates = [localeID, Locale.current.identifier, "zh-CN", "en-US"]
        guard let recognizer = candidates.lazy
            .compactMap({ SFSpeechRecognizer(locale: Locale(identifier: $0)) })
            .first(where: { $0.isAvailable }) else {
            return false
        }

        #if os(iOS)
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playAndRecord, mode: .measurement, options: [.duckOthers, .defaultToSpeaker, .allowBluetoothA2DP])
            try session.setActive(true)
        } catch {
            return false
        }
        #endif

        let engine = AVAudioEngine()
        let input = engine.inputNode
        let hwFormat = input.inputFormat(forBus: 0)
        let outFormat = input.outputFormat(forBus: 0)
        guard hwFormat.sampleRate > 0, hwFormat.channelCount > 0,
              outFormat.sampleRate > 0, outFormat.channelCount > 0 else {
            restorePlaybackSessionIfNeeded()
            return false
        }

        let req = SFSpeechAudioBufferRecognitionRequest()
        req.shouldReportPartialResults = true
        if recognizer.supportsOnDeviceRecognition {
            req.requiresOnDeviceRecognition = true
        }

        // Pass `format: nil` so AVAudioEngine uses the input node's native bus format without
        // throwing Objective-C format-mismatch NSExceptions across Bluetooth/USB route changes.
        input.installTap(onBus: 0, bufferSize: 1024, format: nil) { [weak self, weak req] buffer, _ in
            req?.append(buffer)
            guard let self else { return }
            let shouldEmit = self.state.withLock { s -> Bool in
                guard s.running else { return false }
                s.sampleTick &+= 1
                return s.sampleTick % 3 == 0
            }
            guard shouldEmit, let rms = Self.rmsLevel(of: buffer) else { return }
            let db = 20 * log10(max(rms, 1e-5))
            let normalized = CGFloat(max(0.08, min(1.0, (db + 52) / 44)))
            onLevel(normalized)
        }

        engine.prepare()
        do {
            try engine.start()
        } catch {
            input.removeTap(onBus: 0)
            restorePlaybackSessionIfNeeded()
            return false
        }

        let recognitionTask = recognizer.recognitionTask(with: req) { [weak self] result, error in
            guard let self else { return }
            let isRunning = self.state.withLock { $0.running }
            guard isRunning else { return }
            if let result {
                let text = result.bestTranscription.formattedString
                if !text.isEmpty {
                    onPartial(text)
                }
            }
            if let error, result?.isFinal != true {
                let nsErr = error as NSError
                // Ignore benign cancellation / silence codes when the user stops dictation.
                if nsErr.code != 216 && nsErr.code != 1110 && nsErr.code != 301 {
                    onError("")
                }
            }
        }

        state.withLock { s in
            s.engine = engine
            s.request = req
            s.task = recognitionTask
            s.running = true
            s.sampleTick = 0
        }
        return true
    }

    func stop() {
        let old = state.withLock { s -> State in
            var copy = s
            s = State()
            copy.running = false
            return copy
        }
        old.task?.cancel()
        old.request?.endAudio()
        if let engine = old.engine {
            if engine.isRunning {
                engine.stop()
            }
            engine.inputNode.removeTap(onBus: 0)
        }
        if old.engine != nil {
            restorePlaybackSessionIfNeeded()
        }
    }

    private func restorePlaybackSessionIfNeeded() {
        #if os(iOS)
        try? AVAudioSession.sharedInstance().setCategory(.playback, mode: .spokenAudio, options: [.allowAirPlay, .allowBluetoothA2DP])
        try? AVAudioSession.sharedInstance().setActive(true)
        #endif
    }

    private static func rmsLevel(of buffer: AVAudioPCMBuffer) -> Float? {
        let count = Int(buffer.frameLength)
        guard count > 0 else { return nil }
        if let floatData = buffer.floatChannelData?[0] {
            var sum: Float = 0
            for i in 0..<count {
                let s = floatData[i]
                sum += s * s
            }
            return sqrt(sum / Float(count))
        }
        if let int16Data = buffer.int16ChannelData?[0] {
            var sum: Float = 0
            let scale: Float = 1.0 / 32768.0
            for i in 0..<count {
                let s = Float(int16Data[i]) * scale
                sum += s * s
            }
            return sqrt(sum / Float(count))
        }
        return nil
    }
}

// MARK: Display helpers & StudioKind UI metadata

extension StudioKind {
    var shortBadge: String {
        switch self {
        case .weeklyDigest: "刊"
        case .bookGuide: "导"
        case .roundtable: "桌"
        case .vocabRadio: "词"
        }
    }

    var title: String {
        switch self {
        case .weeklyDigest: String(localized: "书房周刊")
        case .bookGuide: String(localized: "难书导读")
        case .roundtable: String(localized: "跨书圆桌")
        case .vocabRadio: String(localized: "生词外语")
        }
    }

    var longTitle: String {
        switch self {
        case .weeklyDigest: String(localized: "本周书房周刊")
        case .bookGuide: String(localized: "难书与论文导读")
        case .roundtable: String(localized: "跨书圆桌")
        case .vocabRadio: String(localized: "生词伴听外语台")
        }
    }

    var subtitle: String {
        switch self {
        case .weeklyDigest: String(localized: "串联本周划线、想法与听过的播客线索")
        case .bookGuide: String(localized: "读前 8 分钟热身 · 不剧透")
        case .roundtable: String(localized: "挑 2–3 本在读书同桌对谈")
        case .vocabRadio: String(localized: "本周生词编进自然对谈")
        }
    }
}

enum PodcastFormat {
    static func clock(_ sec: Double) -> String {
        let s = max(0, Int(sec.rounded()))
        let h = s / 3600
        let m = (s % 3600) / 60
        let r = s % 60
        if h > 0 { return String(format: "%d:%02d:%02d", h, m, r) }
        return String(format: "%02d:%02d", m, r)
    }

    static func compactDuration(_ sec: UInt32) -> String {
        let s = Int(sec)
        if s <= 0 { return "—" }
        let h = s / 3600
        let m = max(1, (s % 3600) / 60)
        if h > 0 { return String(format: "%d:%02d", h, m) }
        return String(localized: "\(m) 分")
    }

    private static let monthDayFormatter: DateFormatter = {
        let fmt = DateFormatter()
        fmt.dateFormat = "M月d日"
        return fmt
    }()

    static func shortDate(_ ms: Int64) -> String {
        guard ms > 0 else { return "" }
        let date = Date(timeIntervalSince1970: Double(ms) / 1000)
        let days = Calendar.current.dateComponents([.day], from: date, to: .now).day ?? 0
        if days == 0 { return String(localized: "今天") }
        if days == 1 { return String(localized: "昨天") }
        if days > 1 && days <= 6 { return String(localized: "\(days) 天前") }
        return monthDayFormatter.string(from: date)
    }

    static func remaining(position: Double, duration: Double) -> String {
        let remSec = max(0, duration - position)
        guard duration > 0 else { return "" }
        let remMin = max(1, Int((remSec / 60).rounded()))
        if remMin >= 60 {
            let h = remMin / 60
            let m = remMin % 60
            return m > 0 ? String(localized: "剩 \(h) 小时 \(m) 分") : String(localized: "剩 \(h) 小时")
        }
        return String(localized: "剩 \(remMin) 分")
    }

    static func greeting(for date: Date = .now) -> String {
        let hour = Calendar.current.component(.hour, from: date)
        return switch hour {
        case 5..<11: String(localized: "早上好")
        case 11..<13: String(localized: "中午好")
        case 13..<18: String(localized: "下午好")
        default: String(localized: "晚上好")
        }
    }

    static func editorialDate(for date: Date = .now, compact: Bool = false) -> String {
        let cal = Calendar.current
        let comps = cal.dateComponents([.month, .day, .weekday], from: date)
        let months = ["", "一月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "十一月", "十二月"]
        let weekdaysFull = ["", "星期日", "星期一", "星期二", "星期三", "星期四", "星期五", "星期六"]
        let weekdaysShort = ["", "周日", "周一", "周二", "周三", "周四", "周五", "周六"]
        let m = comps.month ?? 1
        let d = comps.day ?? 1
        let w = comps.weekday ?? 1
        let monthStr = (1...12).contains(m) ? months[m] : "\(m)月"
        let dayStr = chineseDay(d)
        let weekStr = (1...7).contains(w) ? (compact ? weekdaysShort[w] : weekdaysFull[w]) : ""
        return weekStr.isEmpty ? "\(monthStr)\(dayStr)" : "\(monthStr)\(dayStr) · \(weekStr)"
    }

    private static func chineseDay(_ day: Int) -> String {
        let digits = ["", "一", "二", "三", "四", "五", "六", "七", "八", "九"]
        switch day {
        case 1...9: return "\(digits[day])日"
        case 10: return "十日"
        case 11...19: return "十\(digits[day - 10])日"
        case 20: return "二十日"
        case 21...29: return "廿\(digits[day - 20])日"
        case 30: return "三十日"
        case 31: return "卅一日"
        default: return "\(day)日"
        }
    }

    static func trackLabel(_ track: PodcastTrack) -> String {
        switch track {
        case .original: String(localized: "原声")
        case .dub: String(localized: "原声线中文")
        case .coach: String(localized: "双语")
        }
    }

    static func sentenceText(_ sentence: MagazineSentence, track: PodcastTrack) -> String {
        switch track {
        case .dub: sentence.textTrans ?? sentence.textOrig
        case .original, .coach: sentence.textOrig
        }
    }
}
