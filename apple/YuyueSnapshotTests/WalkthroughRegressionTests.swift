import Foundation
import Testing
import YuyueCore
@testable import Yuyue

struct WalkthroughRegressionTests {
    @Test func `iPad portrait stays single column while landscape uses two columns`() {
        // iPad Pro 13" portrait: 1032 x 1376 (>= 880 wide, but taller than wide) -> 1 column
        #expect(
            ReaderPager.columnCount(
                width: 1032, height: 1376, spread: false,
                prefersTwoColumns: true, chat: false, mode: .paginated, requireLandscape: true
            ) == 1
        )
        // iPad Pro 13" landscape: 1376 x 1032 -> 2 columns
        #expect(
            ReaderPager.columnCount(
                width: 1376, height: 1032, spread: false,
                prefersTwoColumns: true, chat: false, mode: .paginated, requireLandscape: true
            ) == 2
        )
        // iPhone Duo spread still uses 2 columns regardless of aspect ratio
        #expect(
            ReaderPager.columnCount(
                width: 780, height: 820, spread: true,
                prefersTwoColumns: false, chat: false, mode: .paginated, requireLandscape: true
            ) == 2
        )
        // macOS wide window keeps 2 columns
        #expect(
            ReaderPager.columnCount(
                width: 1040, height: 720, spread: false,
                prefersTwoColumns: true, chat: false, mode: .paginated, requireLandscape: false
            ) == 2
        )
    }

    @Test func `TOC chapter number column widens for five and six character chapter numbers`() {
        let shortWidth = ChapterTitle.columnWidth(for: ["第一回 甄士隐", "第二回 贾夫人"], minWidth: 46)
        #expect(shortWidth == 46)

        let fiveCharWidth = ChapterTitle.columnWidth(for: ["第一回 甄士隐", "第一二零回 甄士隐详说太虚情"], minWidth: 46)
        #expect(fiveCharWidth >= 60)

        let sixCharWidth = ChapterTitle.columnWidth(for: ["第一百二十回 甄士隐详说太虚情"], minWidth: 46)
        #expect(sixCharWidth >= 72)
    }

    @Test func `storage usage separates voice models from aligner and removes them independently`() async throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "yuyue-storage-test-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        func writeBytes(_ rel: String, count: Int) throws {
            let url = root.appending(path: rel)
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data(repeating: 0x5A, count: count).write(to: url)
        }

        try writeBytes("books/active1/original.epub", count: 8_192)
        try writeBytes("books/deleted1/original.epub", count: 4_096)
        try writeBytes("audiobooks/rec1/part1.m4a", count: 16_384)
        try writeBytes("models/\(Aligner.file)/weights.bin", count: 32_768)
        try writeBytes("models/\(LiveVoiceModel.modelFolderName)/encoder.int8.onnx", count: 65_536)
        try writeBytes("models/\(LiveVoiceModel.packFolderName)/voices.json", count: 4_096)
        try writeBytes("fonts/LXGWWenKai.ttf", count: 12_288)
        try writeBytes("library.db", count: 4_096)

        let usage = await StorageUsage.measure(dir: root, active: ["active1": "红楼梦"])
        #expect(usage.bookBytes > 0)
        #expect(usage.deletedBytes > 0)
        #expect(usage.audioBytes > 0)
        #expect(usage.alignerBytes > 0)
        #expect(usage.voiceModelBytes > usage.alignerBytes)
        #expect(usage.fontBytes > 0)
        // Voice models must NOT be lumped into "other"
        #expect(usage.otherBytes < usage.voiceModelBytes)

        // Removing the aligner must preserve live voice models and packs
        StorageUsage.removeAligner(in: root)
        let afterAligner = await StorageUsage.measure(dir: root, active: ["active1": "红楼梦"])
        #expect(afterAligner.alignerBytes == 0)
        #expect(afterAligner.voiceModelBytes == usage.voiceModelBytes)

        // Removing voice models leaves books and audiobooks intact
        StorageUsage.removeVoiceModels(in: root)
        let afterVoice = await StorageUsage.measure(dir: root, active: ["active1": "红楼梦"])
        #expect(afterVoice.voiceModelBytes == 0)
        #expect(afterVoice.bookBytes == usage.bookBytes)
        #expect(afterVoice.audioBytes == usage.audioBytes)
    }

    @Test func `OPDS author splitting and fallback metadata apply when ebook lacks embedded metadata`() async throws {
        #expect(Library.splitAuthors("曹雪芹、高鹗") == ["曹雪芹", "高鹗"])
        #expect(Library.splitAuthors("  Jane Austen ; Charlotte Bronte  ") == ["Jane Austen", "Charlotte Bronte"])
        #expect(Library.splitAuthors("   ").isEmpty)

        let root = FileManager.default.temporaryDirectory
            .appending(path: "yuyue-import-test-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let txt = root.appending(path: "test.txt")
        try Data("第一章 开始\n这是一段测试文字。\n".utf8).write(to: txt)

        let core = try YuyueCore.open(dataDir: root.path(percentEncoded: false), deviceName: "Test", platform: "macos")
        let imported = try await Library.importBook(
            core,
            path: txt.path(percentEncoded: false),
            fallbackTitle: "OPDS Catalog Title",
            fallbackAuthors: ["测试作者"]
        )
        #expect(imported.title == "OPDS Catalog Title")
        #expect(imported.authors == ["测试作者"])
    }

    @Test func `capsule progress text uses chapter percentage matching the progress bar`() {
        let chapterNote = ReaderModel.capsuleProgressText(chapterProgress: 160.0 / 282.0, minutesLeft: 6, isPodcast: false)
        #expect(chapterNote.contains("57%"))
        #expect(chapterNote.contains("6"))
        #expect(!chapterNote.contains("0%"))

        let episodeNote = ReaderModel.capsuleProgressText(chapterProgress: 0.25, minutesLeft: 12, isPodcast: true)
        #expect(episodeNote.contains("25%"))
        #expect(episodeNote.contains("12"))
        #expect(episodeNote != chapterNote)

        let clamped = ReaderModel.capsuleProgressText(chapterProgress: 1.5, minutesLeft: 1, isPodcast: false)
        #expect(clamped.contains("100%"))
    }

    @Test func `TOC boilerplate detection separates license entries from main chapters`() {
        let kinds: [SectionKind] = [.boilerplate, .text, .text, .boilerplate]
        #expect(TocChapter.isBoilerplateRange(0..<1, in: kinds) == true)
        #expect(TocChapter.isBoilerplateRange(1..<2, in: kinds) == false)
        #expect(TocChapter.isBoilerplateRange(2..<4, in: kinds) == false)
        #expect(TocChapter.isBoilerplateRange(3..<4, in: kinds) == true)
        #expect(TocChapter.isBoilerplateRange(4..<5, in: kinds) == false)
    }

    @Test func `companion slash query detects leading slash and stops on whitespace or newline`() {
        #expect(CompanionCommand.slashQuery(in: "") == nil)
        #expect(CompanionCommand.slashQuery(in: "普通问题") == nil)
        #expect(CompanionCommand.slashQuery(in: "什么是 /recap") == nil)
        #expect(CompanionCommand.slashQuery(in: "/") == "")
        #expect(CompanionCommand.slashQuery(in: "／") == "")
        #expect(CompanionCommand.slashQuery(in: "/re") == "re")
        #expect(CompanionCommand.slashQuery(in: "/人物") == "人物")
        #expect(CompanionCommand.slashQuery(in: "/characters 闰土") == nil)
        #expect(CompanionCommand.slashQuery(in: "/recap\n第二行") == nil)
    }

    @Test func `companion slash matching supports English names and Chinese keywords`() {
        #expect(CompanionCommand.matching("").count == CompanionCommand.all.count)

        let byPrefix = CompanionCommand.matching("re")
        #expect(byPrefix.map(\.name).prefix(2) == ["recap", "read"])

        let byChineseTitle = CompanionCommand.matching("人物")
        #expect(byChineseTitle.first?.name == "characters")

        let byChineseKeyword = CompanionCommand.matching("剧透")
        #expect(byChineseKeyword.contains(where: { $0.name == "whole" }))

        #expect(CompanionCommand.matching("zzzz-no-match").isEmpty)
    }

    @Test func `companion slash parsing extracts command and optional multiline argument`() throws {
        let clear = try #require(CompanionCommand.parse("/clear"))
        #expect(clear.command.action == .clear)
        #expect(clear.argument.isEmpty)

        let fullWidth = try #require(CompanionCommand.parse("／人物关系 闰土"))
        #expect(fullWidth.command.action == .characters)
        #expect(fullWidth.argument == "闰土")

        let multiline = try #require(CompanionCommand.parse("/whole 结局是什么\n还有伏笔吗"))
        #expect(multiline.command.action == .scope(.whole))
        #expect(multiline.argument == "结局是什么\n还有伏笔吗")

        #expect(CompanionCommand.parse("/unknown something") == nil)
        #expect(CompanionCommand.parse("第一行\n第二行") == nil)

        let promptWithArg = try #require(fullWidth.command.question(argument: fullWidth.argument, hasQuote: false, chapterTitle: "故乡"))
        #expect(promptWithArg.contains("闰土"))

        let chapterCmd = try #require(CompanionCommand.parse("/chapter")?.command)
        let chapterPrompt = try #require(chapterCmd.question(argument: "", hasQuote: false, chapterTitle: "孔乙己"))
        #expect(chapterPrompt.contains("孔乙己"))
    }

    @Test func `paper appearance pairs day and night presets keeping typography`() {
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .plain, custom: nil), dark: true) == .dark)
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .kami, custom: nil), dark: true) == .kamiNight)
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .sage, custom: nil), dark: true) == .kamiNight)
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .eink, custom: nil), dark: true) == .kamiNight)
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .dark, custom: nil), dark: false) == .plain)
        #expect(PaperAppearance.pairedPreset(for: .init(preset: .kamiNight, custom: nil), dark: false) == .kami)
    }

    @Test func `paper appearance rewrites initial section HTML without flashing`() {
        let raw = """
        <!DOCTYPE html>
        <html lang="zh-CN" data-yy-paper="kami" data-yy-mode="paginated" data-yy-texture="xuan" style="--yy-fs:18px;--yy-p-bg:#F5F4ED;color-scheme:light">
        <body><p>故乡</p></body></html>
        """

        let nightPreset = PaperAppearance.applyToHTML(raw, presetID: "kaminight", customStyle: nil, customTexture: nil)
        #expect(nightPreset.contains(#"data-yy-paper="kaminight""#))
        #expect(!nightPreset.contains("data-yy-texture="))
        #expect(nightPreset.contains(#"style="--yy-fs:18px""#))
        #expect(!nightPreset.contains("--yy-p-bg:"))

        let nightCustom = PaperAppearance.applyToHTML(
            raw,
            presetID: "dark",
            customStyle: "--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark",
            customTexture: "linen"
        )
        #expect(nightCustom.contains(#"data-yy-paper="dark""#))
        #expect(nightCustom.contains(#"data-yy-texture="linen""#))
        #expect(nightCustom.contains(#"style="--yy-fs:18px;--yy-p-bg:#18202B;--yy-p-ink:#C9D3DE;color-scheme:dark""#))
    }

    @MainActor @Test func `font settings forward fontFace and system sans to reader script`() {
        var s = ReaderSettings.standard
        s.paper = .kami
        // With font == nil on serif-default kami (when follow is off), default id reflects serif,
        // and explicitly choosing "system" applies .sans so the reading page switches to sans.
        FontShelf.shared.apply("system", to: &s)
        #expect(s.font == .sans)
        #expect(s.script()["font"] as? String == "sans")

        FontShelf.shared.apply("notoserif", to: &s)
        #expect(s.fontFace == "notoserif")
        #expect(s.script()["font"] as? String == "\"yy-notoserif\", var(--yy-serif)")

        FontShelf.shared.apply("file:我的字.ttf", to: &s)
        #expect(s.script()["font"] as? String == "\"yy-file:我的字.ttf\", var(--yy-serif)")

        FontShelf.shared.apply("local:Yuanti SC", to: &s)
        #expect(s.script()["font"] as? String == "\"Yuanti SC\", var(--yy-sans)")
    }

    @Test func `remote library hasEntry clears after deletion and restores on undo`() {
        func summary(id: String, title: String, available: Bool = true, record: Bool = false) -> BookSummary {
            BookSummary(
                id: id,
                title: title,
                authors: ["F. Scott Fitzgerald"],
                language: "en",
                format: .epub,
                coverPath: nil,
                addedAt: 1,
                openedAt: nil,
                finishedAt: nil,
                progress: 0,
                status: .want,
                collections: [],
                readMinutes: 0,
                listenMinutes: 0,
                available: available,
                page: 0,
                record: record,
                rating: nil,
                comment: nil,
                tags: [],
                startedAt: nil,
                manual: nil
            )
        }

        let got = ["src1/entry-gatsby": "book-123"]
        let book = summary(id: "book-123", title: "The Great Gatsby")
        #expect(RemoteSources.hasEntry(entryID: "entry-gatsby", entryTitle: "1925_gatsby", sourceID: "src1", got: got, books: [book]) == true)
        #expect(RemoteSources.hasEntry(entryID: "entry-gatsby", entryTitle: "The Great Gatsby", sourceID: "src1", got: got, books: []) == false)
        #expect(RemoteSources.hasEntry(entryID: "entry-gatsby", entryTitle: "The Great Gatsby", sourceID: "src1", got: got, books: [summary(id: "book-123", title: "The Great Gatsby", record: true)]) == false)
        #expect(RemoteSources.hasEntry(entryID: "entry-gatsby", entryTitle: "The Great Gatsby", sourceID: "src1", got: got, books: [summary(id: "book-123", title: "The Great Gatsby", available: false)]) == false)
    }

    @Test func `kokoro installation checks English lexicon, gates unknown network path, and aligns default narrators`() throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "yuyue-kokoro-test-\(UUID().uuidString)", directoryHint: .isDirectory)
        let dir = KokoroModel.folder(root)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        for name in ["model.int8.onnx", "voices.bin", "tokens.txt", "lexicon-zh.txt"] {
            try Data([1]).write(to: dir.appending(path: name))
        }
        #expect(KokoroModel.installed(root) == false)

        try Data([1]).write(to: dir.appending(path: "lexicon-us-en.txt"))
        if !KokoroModel.deviceUnsupported {
            #expect(KokoroModel.installed(root) == true)
        }

        // Unknown network path or cellular path must never auto-start large model downloads (#46).
        #expect(KokoroDownload.canAutoStartOnWifi(pathKnown: false, isSatisfied: false, isExpensive: false) == false)
        #expect(KokoroDownload.canAutoStartOnWifi(pathKnown: false, isSatisfied: true, isExpensive: false) == false)
        #expect(KokoroDownload.canAutoStartOnWifi(pathKnown: true, isSatisfied: false, isExpensive: false) == false)
        #expect(KokoroDownload.canAutoStartOnWifi(pathKnown: true, isSatisfied: true, isExpensive: true) == false)
        #expect(KokoroDownload.canAutoStartOnWifi(pathKnown: true, isSatisfied: true, isExpensive: false) == true)

        let zhDefault = try #require(KokoroModel.fallbackSpeakers.first(where: { $0.id == "kokoro.zf_001" }))
        let enDefault = try #require(KokoroModel.fallbackSpeakers.first(where: { $0.id == "kokoro.af_maple" }))
        #expect(KokoroVoice(speaker: zhDefault).isDefaultNarrator == true)
        #expect(KokoroVoice(speaker: enDefault).isDefaultNarrator == true)
    }

    // MARK: - #58 EdgeTTS truncated stream rejection

    @Test func `EdgeTTS rejects truncated stream without Path turn.end and accepts complete stream`() async throws {
        func makeAudioFrame(_ payload: Data) -> URLSessionWebSocketTask.Message {
            let header = Data("X-RequestId:test\r\nContent-Type:audio/mpeg\r\nPath:audio\r\n".utf8)
            var frame = Data([UInt8((header.count >> 8) & 0xFF), UInt8(header.count & 0xFF)])
            frame.append(header)
            frame.append(payload)
            return .data(frame)
        }

        // Partial audio followed by disconnect (no Path:turn.end) must throw, never return partial bytes.
        let partialFrames: [Result<URLSessionWebSocketTask.Message, Error>] = [
            .success(makeAudioFrame(Data([0xFF, 0xFB, 0x10, 0x20]))),
            .failure(URLError(.networkConnectionLost)),
        ]
        var partialIdx = 0
        await #expect(throws: URLError.self) {
            _ = try await EdgeTTS.collectAudioFrames {
                defer { partialIdx += 1 }
                return try partialFrames[partialIdx].get()
            }
        }

        // Complete stream ending with Path:turn.end returns concatenated audio bytes.
        let partA = Data([0xFF, 0xFB, 0x01, 0x02])
        let partB = Data([0x03, 0x04, 0x05])
        let completeFrames: [URLSessionWebSocketTask.Message] = [
            makeAudioFrame(partA),
            makeAudioFrame(partB),
            .string("X-RequestId:test\r\nPath:turn.end\r\n"),
        ]
        var completeIdx = 0
        let audio = try await EdgeTTS.collectAudioFrames {
            defer { completeIdx += 1 }
            return completeFrames[completeIdx]
        }
        #expect(audio == partA + partB)

        // Path:turn.end with zero audio bytes throws emptyAudio.
        var emptySent = false
        await #expect(throws: EdgeTTS.EdgeError.self) {
            _ = try await EdgeTTS.collectAudioFrames {
                if !emptySent {
                    emptySent = true
                    return .string("X-RequestId:test\r\nPath:turn.end\r\n")
                }
                throw URLError(.networkConnectionLost)
            }
        }

        // Cancelled task throws CancellationError even if receive fails.
        let cancelTask = Task {
            try await EdgeTTS.collectAudioFrames {
                try await Task.sleep(for: .seconds(5))
                return .string("Path:turn.end")
            }
        }
        cancelTask.cancel()
        await #expect(throws: CancellationError.self) {
            _ = try await cancelTask.value
        }
    }

    // MARK: - #59 Sleep timer precedence over single and range loop

    @Test func `Player sleep timer takes precedence over single and range loops`() {
        let now = Date(timeIntervalSince1970: 1_700_000_000)
        let expired = now.addingTimeInterval(-1)
        let future = now.addingTimeInterval(60)
        let done = Player.Spot(section: 1, sentence: 5)
        let nextSameSection = Player.Spot(section: 1, sentence: 6)
        let nextChapter = Player.Spot(section: 2, sentence: 0)
        let range = Player.LoopRange(section: 1, first: 2, last: 5)
        let rangeStart = Player.Spot(section: 1, sentence: 2)

        // Single loop + sleep
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .sentence, loop: true, loopRange: nil, now: now) == .sleepAt(done))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .until(expired, minutes: 10), loop: true, loopRange: nil, now: now) == .sleepAt(done))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .until(future, minutes: 10), loop: true, loopRange: nil, now: now) == .loopTo(done))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextChapter, sleep: .chapter, loop: true, loopRange: nil, now: now) == .sleepAt(done))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .chapter, loop: true, loopRange: nil, now: now) == .loopTo(done))

        // Range loop at boundary + sleep
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .sentence, loop: true, loopRange: range, now: now) == .sleepAt(rangeStart))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .until(expired, minutes: 10), loop: true, loopRange: range, now: now) == .sleepAt(rangeStart))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextChapter, sleep: .chapter, loop: true, loopRange: range, now: now) == .sleepAt(rangeStart))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .off, loop: true, loopRange: range, now: now) == .loopTo(rangeStart))

        // Range loop mid-range
        let mid = Player.Spot(section: 1, sentence: 3)
        let midNext = Player.Spot(section: 1, sentence: 4)
        #expect(Player.nextActionAfterPlayed(done: mid, next: midNext, sleep: .off, loop: true, loopRange: range, now: now) == .advance)
        #expect(Player.nextActionAfterPlayed(done: mid, next: midNext, sleep: .sentence, loop: true, loopRange: range, now: now) == .sleepAt(midNext))

        // No loop + chapter sleep at book end
        #expect(Player.nextActionAfterPlayed(done: done, next: nil, sleep: .chapter, loop: false, loopRange: nil, now: now) == .sleepAt(done))
        #expect(Player.nextActionAfterPlayed(done: done, next: nextSameSection, sleep: .off, loop: false, loopRange: nil, now: now) == .advance)
    }

    // MARK: - #60 Audio claim notification on voice preview

    @MainActor @Test func `voice preview posts yyAudioClaimed and stops when another player claims audio`() async throws {
        let previewClaims = Mutex(0)
        let observer = NotificationCenter.default.addObserver(forName: .yyAudioClaimed, object: nil, queue: nil) { note in
            if (note.object as AnyObject?) === Voices.previewAudioOwner {
                previewClaims.withLock { $0 += 1 }
            }
        }
        defer {
            Voices.stopPreview()
            NotificationCenter.default.removeObserver(observer)
        }

        let stub = StubPreviewVoice()
        Voices.preview(stub, text: "试听测试")
        #expect(previewClaims.withLock { $0 } == 1)
        #expect(PreviewState.shared.voiceID == stub.id)

        // Another player claiming audio must stop the active preview.
        let otherPlayer = NSObject()
        NotificationCenter.default.post(name: .yyAudioClaimed, object: otherPlayer)
        for _ in 0 ..< 50 where PreviewState.shared.voiceID != nil {
            try await Task.sleep(for: .milliseconds(10))
        }
        #expect(PreviewState.shared.voiceID == nil)

        // Direct SystemVoice.preview also posts .yyAudioClaimed when a system voice exists.
        if let sys = SystemVoice.best(for: "zh") ?? SystemVoice.all().first {
            let before = previewClaims.withLock { $0 }
            SystemVoice.preview(sys, text: "测试")
            #expect(previewClaims.withLock { $0 } == before + 1)
            Voices.stopPreview()
        }
    }

    // MARK: - #61 CloudVoice in-flight deduplication and cancellation safety

    @Test func `CloudVoice deduplicates concurrent fetches and isolates per-waiter cancellation`() async throws {
        let voiceA = CloudVoice(provider: .edge, voiceID: "zh-CN-XiaoxiaoNeural", name: "晓晓", language: "zh-CN")
        let voiceB = CloudVoice(provider: .edge, voiceID: "zh-CN-XiaoxiaoNeural", name: "晓晓", language: "zh-CN")
        let sampleWAV = CloudVoice.wav(pcm: Data(repeating: 0, count: 4_800), rate: 24_000)

        // 1. Concurrent render() + prefetch() across separate instances share one fetch & disk write.
        let text1 = "dedup-share-\(UUID().uuidString)"
        defer { try? FileManager.default.removeItem(at: voiceA.cacheFile(text1, ext: "wav")) }

        let fetchCount1 = Mutex(0)
        let entered1 = TestAsyncGate()
        let release1 = TestAsyncGate()

        try await CloudVoice.$testFetchOverride.withValue({ _, _, _ in
            fetchCount1.withLock { $0 += 1 }
            entered1.open()
            await release1.wait()
            return (sampleWAV, "wav")
        }) {
            let renderTask = Task { try await voiceA.render(text1, language: "zh-CN") }
            await entered1.wait()
            let voiceID = await voiceA.id
            let prefetchTask = Task { await voiceB.prefetch([text1], language: "zh-CN") }
            while CloudVoice.inFlightWaiterCount(voiceID: voiceID, cacheKey: text1) < 2 {
                await Task.yield()
            }
            release1.open()

            let speech = try await renderTask.value
            await prefetchTask.value
            #expect(speech != nil)
            #expect(fetchCount1.withLock { $0 } == 1)
            #expect(FileManager.default.fileExists(atPath: voiceA.cacheFile(text1, ext: "wav").path))
        }

        // 2. Cancelling one waiter does NOT cancel the shared fetch for remaining waiters.
        let text2 = "dedup-cancel-one-\(UUID().uuidString)"
        defer { try? FileManager.default.removeItem(at: voiceA.cacheFile(text2, ext: "wav")) }

        let fetchCount2 = Mutex(0)
        let entered2 = TestAsyncGate()
        let release2 = TestAsyncGate()

        try await CloudVoice.$testFetchOverride.withValue({ _, _, _ in
            fetchCount2.withLock { $0 += 1 }
            entered2.open()
            await release2.wait()
            try Task.checkCancellation()
            return (sampleWAV, "wav")
        }) {
            let waiter1 = Task { try await voiceA.render(text2, language: "zh-CN") }
            await entered2.wait()
            let voiceID = await voiceA.id
            let waiter2 = Task { try await voiceB.render(text2, language: "zh-CN") }
            while CloudVoice.inFlightWaiterCount(voiceID: voiceID, cacheKey: text2) < 2 {
                await Task.yield()
            }

            waiter1.cancel()
            await #expect(throws: CancellationError.self) {
                _ = try await waiter1.value
            }

            release2.open()
            let speech = try await waiter2.value
            #expect(speech != nil)
            #expect(fetchCount2.withLock { $0 } == 1)
        }

        // 3. Cancelling the last remaining waiter cancels the underlying fetch.
        let text3 = "dedup-cancel-all-\(UUID().uuidString)"
        defer { try? FileManager.default.removeItem(at: voiceA.cacheFile(text3, ext: "wav")) }

        let entered3 = TestAsyncGate()
        let cancelled3 = TestAsyncGate()
        let sawCancel = Mutex(false)

        try await CloudVoice.$testFetchOverride.withValue({ _, _, _ in
            entered3.open()
            do {
                try await Task.sleep(for: .seconds(30))
                cancelled3.open()
                return (sampleWAV, "wav")
            } catch is CancellationError {
                sawCancel.withLock { $0 = true }
                cancelled3.open()
                throw CancellationError()
            }
        }) {
            let solo = Task { try await voiceA.render(text3, language: "zh-CN") }
            await entered3.wait()
            solo.cancel()
            await #expect(throws: CancellationError.self) {
                _ = try await solo.value
            }
            await cancelled3.wait()
            #expect(sawCancel.withLock { $0 } == true)
        }
    }

    // MARK: - #66 ModelDownload corrupt .part retry from byte 0 on last healthy fallback

    @Test func `ModelDownload retries healthy fallback source from byte 0 after corrupt part digest mismatch`() async throws {
        let root = FileManager.default.temporaryDirectory
            .appending(path: "yuyue-download-retry-test-\(UUID().uuidString)", directoryHint: .isDirectory)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: root) }

        // Build deterministic valid payload and compute its real BLAKE3 digest.
        let payload = Data((0 ..< 1024).map { UInt8($0 & 0xFF) })
        let refFile = root.appending(path: "reference.bin")
        try payload.write(to: refFile)
        let expectedDigest = try fileDigest(path: refFile.path(percentEncoded: false))

        // Pre-seed .part with a 128-byte corrupt prefix (simulating corruption from an earlier failed mirror).
        let partFile = root.appending(path: "model.bin.part")
        try Data(repeating: 0xEE, count: 128).write(to: partFile)

        let url = URL(string: "https://yuyue-test-66.local/model.bin")!
        CorruptPartFallbackURLProtocol.mock.withLock { st in
            st.payload = payload
            st.observedRanges = []
        }

        try await ModelDownload.downloadWithResume(
            urls: [url],
            to: partFile,
            expectedBytes: Int64(payload.count),
            expectedBlake3: expectedDigest,
            sessionConfig: {
                let cfg = URLSessionConfiguration.ephemeral
                cfg.protocolClasses = [CorruptPartFallbackURLProtocol.self]
                return cfg
            }
        ) { _, _ in }

        let downloaded = try Data(contentsOf: partFile)
        #expect(downloaded == payload)
        let ranges = CorruptPartFallbackURLProtocol.mock.withLock { $0.observedRanges }
        #expect(ranges == ["bytes=128-", nil])
    }

    // MARK: - #72 DailyQuote.current must stay read-only during SwiftUI body evaluation

    @Test @MainActor func `DailyQuote current does not mutate Observable state or trigger self-invalidation`() {
        let ud = UserDefaults.standard
        let keys = ["voice.daily.zh.date", "voice.daily.zh.text", "voice.daily.zh.source", "voice.daily.zh.online",
                    "voice.daily.en.date", "voice.daily.en.text", "voice.daily.en.source", "voice.daily.en.online"]
        let saved = Dictionary(uniqueKeysWithValues: keys.map { ($0, ud.object(forKey: $0)) })
        defer {
            for (k, v) in saved {
                if let v { ud.set(v, forKey: k) } else { ud.removeObject(forKey: k) }
            }
        }
        for k in keys { ud.removeObject(forKey: k) }

        let quote = DailyQuote()
        let invalidated = Mutex(false)
        withObservationTracking {
            let zh = quote.current("zh")
            let en = quote.current("en")
            #expect(!zh.text.isEmpty)
            #expect(!en.text.isEmpty)
        } onChange: {
            invalidated.withLock { $0 = true }
        }
        // Calling current() again from subsequent body passes must not trigger observation invalidation.
        _ = quote.current("zh")
        _ = quote.current("en")
        #expect(!invalidated.withLock { $0 })
    }
}

private struct StubPreviewVoice: Voice {
    let id = "stub.preview.voice"
    let name = "Stub"
    let qualityName: String? = nil
    let isOffline = true
    func speaks(_ language: String) -> Bool { true }
    func render(_ text: String, language: String) async throws -> Speech? {
        try await Task.sleep(for: .seconds(5))
        return nil
    }
}

private final class TestAsyncGate: @unchecked Sendable {
    private let state = Mutex<(opened: Bool, waiters: [CheckedContinuation<Void, Never>])>((false, []))

    func open() {
        let waiters = state.withLock { st -> [CheckedContinuation<Void, Never>] in
            st.opened = true
            let list = st.waiters
            st.waiters.removeAll()
            return list
        }
        for waiter in waiters { waiter.resume() }
    }

    func wait() async {
        await withCheckedContinuation { continuation in
            let resumeNow = state.withLock { st -> Bool in
                if st.opened { return true }
                st.waiters.append(continuation)
                return false
            }
            if resumeNow { continuation.resume() }
        }
    }
}

private final class CorruptPartFallbackURLProtocol: URLProtocol, @unchecked Sendable {
    struct MockState {
        var payload = Data()
        var observedRanges: [String?] = []
    }

    static let mock = Mutex(MockState())

    override class func canInit(with request: URLRequest) -> Bool {
        request.url?.host == "yuyue-test-66.local"
    }

    override class func canonicalRequest(for request: URLRequest) -> URLRequest {
        request
    }

    override func startLoading() {
        guard let url = request.url else {
            client?.urlProtocol(self, didFailWithError: URLError(.badURL))
            return
        }
        let rangeHeader = request.value(forHTTPHeaderField: "Range")
        let payload = Self.mock.withLock { st -> Data in
            st.observedRanges.append(rangeHeader)
            return st.payload
        }
        if let rangeHeader, rangeHeader.hasPrefix("bytes="),
           let dash = rangeHeader.firstIndex(of: "-"),
           let start = Int(rangeHeader[rangeHeader.index(rangeHeader.startIndex, offsetBy: 6) ..< dash]),
           start >= 0, start < payload.count {
            let slice = payload[start...]
            let end = payload.count - 1
            let headers = [
                "Content-Range": "bytes \(start)-\(end)/\(payload.count)",
                "Content-Length": "\(slice.count)",
            ]
            let response = HTTPURLResponse(url: url, statusCode: 206, httpVersion: "HTTP/1.1", headerFields: headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: Data(slice))
            client?.urlProtocolDidFinishLoading(self)
        } else {
            let headers = ["Content-Length": "\(payload.count)"]
            let response = HTTPURLResponse(url: url, statusCode: 200, httpVersion: "HTTP/1.1", headerFields: headers)!
            client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
            client?.urlProtocol(self, didLoad: payload)
            client?.urlProtocolDidFinishLoading(self)
        }
    }

    override func stopLoading() {}
}
