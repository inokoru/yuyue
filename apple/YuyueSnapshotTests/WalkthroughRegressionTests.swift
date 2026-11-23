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
}
