import SwiftUI
import YuyueCore

extension ReaderModel {
    /// Loads the recording's map (`RecordingGuide`) and updates unread-sentence dimming.
    func loadFlags() {
        player?.guide = Audiobooks.shared?.guide(for: bookID)
        guideVersion += 1
        wordCache.removeAll()
        unreadShown = false
        showUnread()
    }

    /// The timings or map were updated (for example after 对准).
    func timingsChanged() {
        loadFlags()
    }

    /// The sentences the recording doesn't read, a shade lighter, while it's what's reading.
    func showUnread() {
        let want = player?.narrated == true && player?.guide != nil
        guard want != unreadShown || want else { return }
        unreadShown = want
        guard want, let guide = player?.guide else { return pager.setUnread([:]) }
        var all: [UInt32: [Int]] = [:]
        for section in guide.grades.keys {
            let u = guide.unread(section)
            if !u.isEmpty { all[section] = u }
        }
        pager.setUnread(all)
    }

    /// The chapter on the page is only followed as a whole while the recording reads it.
    var chapterOnlyHere: Bool {
        _ = guideVersion
        guard let player, player.narrated, player.state != .stopped, let s = section else { return false }
        return player.guide?.chapterOnly(s) == true
    }

    /// 对准 shows: the recording is being heard now.
    var canPin: Bool {
        player?.recordingNow() != nil && Audiobooks.shared?.audiobook(for: bookID) != nil
    }

    /// 从这里对准: the sentence chosen starts at the moment of the recording heard now; the
    /// lay-out is made again around it, and the pin syncs.
    func pinHere(section: UInt32, sentence: Int) {
        guard let at = player?.recordingNow(), let books = Audiobooks.shared, let a = books.audiobook(for: bookID) else { return }
        let wasPlaying = player?.state == .playing
        let targetSentence = UInt32(max(0, sentence))
        dismissCards()
        let core = core, id = a.id
        Task {
            await Self.savePin(core, bookID: id, section: section, sentence: targetSentence, seconds: at)
            books.adoptMap(id)
            timingsChanged()
            if wasPlaying {
                player?.play(from: Position(section: section, sentence: targetSentence))
            }
        }
        show(Library.Toast(icon: "checkmark.circle.fill", title: String(localized: "对准了"), detail: String(localized: "这一句从这里开始，前后几句跟着重排")))
    }

    @concurrent nonisolated private static func savePin(_ core: YuyueCore, bookID: String, section: UInt32, sentence: UInt32, seconds: Double) async {
        try? core.audiobookPin(bookId: bookID, section: section, sentence: sentence, seconds: seconds)
    }

    /// Where reading from `position` begins with the recording: a sentence in a stretch not
    /// sure sentence by sentence starts from the stretch's first.
    func narratedStart(_ position: Position) -> Position {
        guard let player, player.narrated, let guide = player.guide else { return position }
        return Position(section: position.section, sentence: UInt32(guide.stretchStart(position.section, Int(position.sentence))))
    }

    /// The capsule's message about the recording's own talk: 片头 just passed (点这里听), or talk
    /// not in the book being played (跳过).
    var capsuleAlert: CapsuleAlert? {
        // 局域网传录音: the recording is on a Mac nearby, not here.
        if let id = missingRecording, let offer = recordingOffer {
            if let p = RecordingFetch.shared.taking[id] {
                return CapsuleAlert(title: nil, note: String(localized: "正在从 \(offer.device) 传 · \(Int(p * 100))%")) {}
            }
            let note = RecordingFetch.shared.problem[id] ?? String(localized: "\(offer.device) 上有这本的录音 · 传过来")
            return CapsuleAlert(title: nil, note: note) { [weak self] in
                guard let self, let library else { return }
                RecordingFetch.shared.take(id, from: offer, core: core, library: library)
            }
        }
        guard let player, player.narrated else { return nil }
        if let e = player.extraPlaying {
            let what = e.place == .head ? String(localized: "片头") : e.place == .tail ? String(localized: "片尾") : String(localized: "不在书里的话")
            return CapsuleAlert(title: "\(what) · \(AudiobookPlayer.clock(e.t1 - e.t0))", note: String(localized: "跳过")) { [weak player] in player?.skipExtra() }
        }
        if let h = player.skippedHead, player.state == .playing || player.state == .loading {
            return CapsuleAlert(title: nil, note: String(localized: "跳过了片头 \(AudiobookPlayer.clock(h.t1 - h.t0)) · 点这里听")) { [weak player] in player?.playHead() }
        }
        return nil
    }

    // MARK: 回到昨晚

    /// The timer stopped the voice: remembered where it was set, to offer next time.
    func rememberSleep(from spot: Player.Spot) {
        UserDefaults.standard.set([Int(spot.section), spot.sentence, Int(Date().timeIntervalSince1970)], forKey: Self.sleepKey(bookID))
    }

    /// Listened on since (the offer showing or not): nothing to ask, now or next time.
    func sleepHeard() {
        if sleepReturn != nil { sleepReturn = nil }
        guard UserDefaults.standard.object(forKey: Self.sleepKey(bookID)) != nil else { return }
        UserDefaults.standard.removeObject(forKey: Self.sleepKey(bookID))
    }

    /// On opening the book: when its recording (laid out on another device) isn't here, whether a
    /// Mac nearby has it.
    func lookForRecording() async {
        guard let pair = (try? core.listeningPairs())?.first(where: { $0.book == bookID }) else { return }
        let here = (try? core.audioRecording(bookId: pair.recording)).map { $0.files.allSatisfy { FileManager.default.fileExists(atPath: $0.path) } } ?? false
        guard !here else { return }
        missingRecording = pair.recording
        for _ in 0..<6 where recordingOffer == nil {
            recordingOffer = await RecordingFetch.shared.offer(for: pair.recording, core: core)
            if recordingOffer == nil { try? await Task.sleep(for: .seconds(5)) }
        }
    }

    /// On opening the book: asked once, if the timer stopped it and it hasn't been listened to since.
    func offerSleepReturn() {
        guard let v = UserDefaults.standard.array(forKey: Self.sleepKey(bookID)) as? [Int], v.count == 3,
              Date().timeIntervalSince1970 - Double(v[2]) < 7 * 86_400 else { return }
        let spot = Player.Spot(section: UInt32(v[0]), sentence: v[1])
        var at = String(localized: "第 \(spot.sentence + 1) 句")
        if let g = Audiobooks.shared?.guide(for: bookID), let t = g.times[RecordingGuide.key(spot.section, UInt32(spot.sentence))]?.0,
           let start = g.times.filter({ $0.key.hasPrefix("\(spot.section).") }).map(\.value.0).min() {
            at = AudiobookPlayer.clock(t - start)
        }
        sleepReturn = SleepReturn(spot: spot, chapter: Self.shortTitle(detail?.chapterTitle(for: spot.section) ?? ""), at: at, date: Date(timeIntervalSince1970: Double(v[2])))
    }

    func answerSleepReturn(go: Bool) {
        let r = sleepReturn
        sleepReturn = nil
        if go, let r {
            pager.show(section: r.spot.section, target: .sentence(r.spot.sentence, align: .third), animated: !pager.reduceMotion)
            player?.play(from: r.spot.position)
            player?.pause()
        }
        UserDefaults.standard.removeObject(forKey: Self.sleepKey(bookID))
    }

    static func sleepKey(_ book: String) -> String { "sleepFrom." + book }

    /// `第八回`, from `第八回 比通靈金鶯微露意 …`; a short title as it is.
    static func shortTitle(_ t: String) -> String {
        let first = t.split(whereSeparator: { $0 == " " || $0 == "\u{3000}" }).first.map(String.init) ?? t
        return first.count < t.count && first.count <= 8 ? first : String(t.prefix(12))
    }
}

/// A message on the reading capsule with something to do.
struct CapsuleAlert {
    /// In place of `第 n / m 句`; nil keeps it.
    let title: String?
    let note: String
    let action: () -> Void
}

/// 回到昨晚: the timer stopped the voice; where it had been set.
struct SleepReturn: Equatable {
    let spot: Player.Spot
    let chapter: String
    /// `12:03` into the chapter in the recording, or `第 12 句`.
    let at: String
    let date: Date

    var when: String { Self.when(date) }

    /// `昨晚`, `刚才`, or a date.
    static func when(_ date: Date) -> String {
        let cal = Calendar.current
        if cal.isDateInToday(date) { return String(localized: "刚才") }
        if cal.isDateInYesterday(date) { return cal.component(.hour, from: date) >= 18 ? String(localized: "昨晚") : String(localized: "昨天") }
        return date.formatted(.dateTime.month().day())
    }
}

/// The capsule's offer: 回到那里 / 不用.
struct SleepReturnCard: View {
    /// `昨晚`.
    let when: String
    /// `第八回 12:03`.
    let place: String
    let answer: (Bool) -> Void
    @Environment(\.colorScheme) private var scheme

    var body: some View {
        // One line in a pill at the foot of the page.
        HStack(spacing: 6) {
            (Text("\(when)定时从") + Text(" \(place) ").fontWeight(.semibold) + Text("开始，之后睡着了？"))
                .font(.yy(size: 11.5))
                .foregroundStyle(YYColor.t1)
                #if os(macOS)
                .lineLimit(1)
                .fixedSize()
                #else
                .lineLimit(2)
                .frame(maxWidth: .infinity, alignment: .leading)
                #endif
                .padding(.trailing, 4)
            Button("回到那里") { answer(true) }.buttonStyle(YYButtonStyle(kind: .main, size: .small))
            Button("不用") { answer(false) }.buttonStyle(YYButtonStyle(kind: .plain, size: .small))
        }
        .padding(.leading, 14)
        .padding(.trailing, 6)
        .padding(.vertical, 5)
        #if os(macOS)
        .yyFloatCard(Capsule(), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
        #else
        .yyFloatCard(.rect(cornerRadius: 18), shadow: scheme == .dark ? YYShadow.floatDark : YYShadow.floatLight)
        #endif
    }
}

/// 文字版: a book made from a recording's words says so on every page.
struct TextVersionBanner: View {
    let paper: YYPaper
    let core: YuyueCore
    let book: String
    @State private var polish = TextPolish.Run.shared

    var body: some View {
        HStack(spacing: 8) {
            label
                .font(.yy(size: 11.5))
                .foregroundStyle(paper.ink2)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
            // 整理错字: only with a model connected (书伴).
            if case .running = polish.state(book) {
                Spacer(minLength: 4)
                Button("停") { polish.stop(book) }.buttonStyle(YYButtonStyle(kind: .plain, size: .small))
            } else if DramaWork.config != nil, polish.fixed(book) == nil {
                Spacer(minLength: 4)
                Button("整理错字") { polish.start(core: core, book: book) }.buttonStyle(YYButtonStyle(kind: .plain, size: .small))
                    .help("用书伴的模型改同音错字和前后不一的人名；念起来不一样的不改")
            }
        }
        .padding(.leading, 12)
        .padding(.trailing, 4)
        .padding(.vertical, 4)
        .frame(minHeight: 30)
        .background(paper.line.opacity(0.55), in: .rect(cornerRadius: 10))
    }

    private var label: Text {
        switch polish.state(book) {
        case let .running(done, of, fixed):
            Text("正在整理 · \(done) / \(of) 段 · 改了 \(fixed) 处")
        case let .failed(why):
            Text(why)
        default:
            if let n = polish.fixed(book) {
                Text("整理过了，改了 \(n) 处同音字和人名，") + Text("可能还有错字").fontWeight(.semibold)
            } else {
                Text("这是录音转出来的文字，") + Text("可能有错字").fontWeight(.semibold)
            }
        }
    }
}

/// 只按章跟着走: over a chapter the recording doesn't line up with sentence by sentence.
struct ChapterOnlyBanner: View {
    let paper: YYPaper

    var body: some View {
        HStack(spacing: 8) {
            (Text("这一章只按章跟着走 · ") + Text("选中正在念的句子点「对准」可校准").fontWeight(.semibold))
                .font(.yy(size: 11.5))
                .foregroundStyle(paper.ink2)
                .lineLimit(1)
                .minimumScaleFactor(0.85)
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 6)
        .background(paper.line.opacity(0.55), in: .rect(cornerRadius: 10))
    }
}
