import SnapshotTesting
import SwiftUI
import XCTest
#if canImport(AppKit)
import AppKit
#elseif canImport(UIKit)
import UIKit
#endif
@testable import Yuyue

/// 界面快照. These render SwiftUI on its own, with fixture words, and compare PNGs under
/// `__Snapshots__/`. They are not the data-backup snapshots (`Snapshots.swift`, `PhoneSnapshot.swift`,
/// `snapshot.rs`), and they are not `scripts/window-catalog.sh` — that one still launches the app
/// for system permission dialogs, the Sparkle installer and the WebView reader.
///
/// First run on a machine records any missing PNG and fails; run again to compare. Re-record
/// everything with `TEST_RUNNER_YY_SNAPSHOT_RECORD=all` (via `xcodebuild test`). See docs/dev/testing.md.
final class ScreenCatalogTests: XCTestCase {
    override func invokeTest() {
        if ProcessInfo.processInfo.environment["YY_SNAPSHOT_RECORD"] == "all" {
            withSnapshotTesting(record: .all) { super.invokeTest() }
        } else {
            super.invokeTest()
        }
    }

    @MainActor
    private func snap<V: View>(
        _ view: V, width: CGFloat, height: CGFloat,
        file: StaticString = #filePath, testName: String = #function, line: UInt = #line
    ) {
        let framed = view
            .frame(width: width, height: height)
            .environment(\.colorScheme, .light)
            .transaction { $0.disablesAnimations = true }
        // Swift 6 forbids `#if` inside a call's argument list, so the platform
        // branch wraps each whole `assertSnapshot` call.
        #if os(macOS)
        let hosting = NSHostingView(rootView: framed)
        hosting.appearance = NSAppearance(named: .aqua)
        hosting.frame = NSRect(x: 0, y: 0, width: width, height: height)
        hosting.layoutSubtreeIfNeeded()
        assertSnapshot(
            of: hosting,
            as: .image,
            file: file, testName: testName, line: line
        )
        #else
        assertSnapshot(
            of: framed,
            as: .image(layout: .fixed(width: width, height: height)),
            file: file, testName: testName, line: line
        )
        #endif
    }

    private struct ShelfBook {
        var title: String
        var author: String?
        var note: String
        var pdf = false
    }

    /// The same four titles the Mac window catalog seeds, without their files.
    private var shelfBooks: [ShelfBook] {
        [
            ShelfBook(title: "红楼梦", author: "曹雪芹", note: "读到 12%"),
            ShelfBook(title: "了不起的盖茨比", author: "菲茨杰拉德", note: "想读"),
            ShelfBook(title: "背影", author: "朱自清", note: "读完"),
            ShelfBook(title: "样张", author: nil, note: "PDF", pdf: true),
        ]
    }
}

#if os(iOS)
extension ScreenCatalogTests {
    @MainActor
    func testIphoneShelfEmpty() {
        snap(shelfChrome {
            PhoneShelfWelcome(narrow: true, files: .constant(false), wifi: .constant(false), remote: .constant(false))
        }, width: 390, height: 844)
    }

    @MainActor
    func testIphoneShelfBooks() {
        snap(shelfChrome {
            VStack(alignment: .leading, spacing: 0) {
                HStack {
                    Text("全部").font(.subheadline.weight(.semibold)).foregroundStyle(YYColor.t1)
                    Text("\(shelfBooks.count) 本").font(.footnote).foregroundStyle(YYColor.t3)
                    Spacer()
                    ShelfSortLabel(name: ShelfSort.recent.name)
                }
                .padding(.bottom, 12)
                LazyVGrid(columns: [GridItem(.flexible(), spacing: 18), GridItem(.flexible(), spacing: 18)], spacing: 22) {
                    ForEach(Array(shelfBooks.enumerated()), id: \.offset) { _, book in
                        PhoneShelfBookCell(title: book.title, author: book.author, note: book.note, pdf: book.pdf)
                    }
                }
            }
            .padding(.horizontal, 20)
            .padding(.top, 16)
        }, width: 390, height: 844)
    }

    @MainActor
    func testIphoneReaderChrome() {
        snap(PhoneReaderChromeFixture(), width: 390, height: 844)
    }

    @MainActor
    func testIphoneSettingsAppearance() {
        snap(NavigationStack { PhoneAppearance() }, width: 390, height: 844)
    }

    @MainActor
    func testIphoneImportSheet() {
        snap(
            PhoneImportSheet(watching: "下载", fromFiles: {}, overWifi: {}, remote: {}, watch: {})
                .background(YYColor.bg),
            width: 390, height: 480
        )
    }

    @MainActor
    func testIphoneBookWontOpen() {
        snap(bookWontOpen, width: 390, height: 520)
    }

    /// Title and the top-right buttons, then whatever the shelf is showing. Matches the phone shelf's insets.
    @MainActor
    private func shelfChrome<Content: View>(@ViewBuilder content: () -> Content) -> some View {
        ZStack(alignment: .topTrailing) {
            YYColor.bg
            VStack(alignment: .leading, spacing: 0) {
                ShelfPlaceTitle(symbol: "book", name: String(localized: "书"))
                    .padding(.trailing, 150)
                    .padding(.top, 14)
                    .padding(.horizontal, 20)
                    .frame(maxWidth: .infinity, alignment: .leading)
                content()
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
            TopButtons {
                TopButton(title: "反馈", asset: "bug") {}
                TopButton(title: "导入", symbol: "plus") {}
                TopButton(title: "选择", symbol: "checkmark.circle") {}
                    .disabled(true)
                    .opacity(0.35)
            }
            .padding(.top, 14)
            .padding(.trailing, 16)
        }
    }
}
#endif

#if os(macOS)
extension ScreenCatalogTests {
    @MainActor
    func testMacShelfEmpty() {
        snap(
            MacShelfEmpty(
                symbol: "books.vertical",
                text: String(localized: "书架是空的，把 EPUB、PDF、MOBI、TXT 拖进窗口就能开始读")
            )
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(YYColor.bg),
            width: 640, height: 360
        )
    }

    @MainActor
    func testMacSettingsAppearance() {
        snap(
            HStack(alignment: .top, spacing: 0) {
                SettingsSidebar(page: .constant(.appearance), tint: .byDefault)
                VStack(spacing: 0) {
                    SettingsPageHeader(page: .appearance)
                    AppearanceSettings()
                        .padding(.horizontal, 24)
                        .padding(.top, 8)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                .background(YYColor.bg)
            },
            width: 760, height: 640
        )
    }

    @MainActor
    func testMacDeleteBook() {
        let title = "红楼梦"
        snap(
            YYDialogCard(
                tone: .error, icon: .bang,
                title: String(localized: "删除《\(title)》？"),
                message: String(localized: "书会移到已删除，30 天内可以在存储里撤销。"),
                secondaryTitle: String(localized: "取消"),
                primaryTitle: String(localized: "删除"),
                onPrimary: {},
                destructive: true
            )
            .padding(24)
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(YYColor.bg),
            width: 420, height: 280
        )
    }

    @MainActor
    func testMacBookWontOpen() {
        snap(bookWontOpen, width: 480, height: 360)
    }

    @MainActor
    func testMacCollectionsWallPopulated() throws {
        let scratch = FileManager.default.temporaryDirectory.appending(path: "yuyue-col-pop-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer {
            UserDefaults.standard.removeObject(forKey: "debugLibrary")
            try? FileManager.default.removeItem(at: scratch)
        }
        UserDefaults.standard.set(scratch.path(percentEncoded: false), forKey: "debugLibrary")
        let library = try Library()
        let b1 = try XCTUnwrap(library.addRecordBook("The Great Gatsby", status: .reading))
        let b2 = try XCTUnwrap(library.addRecordBook("Pride and Prejudice", status: .want))
        let b3 = try XCTUnwrap(library.addRecordBook("Walden", status: .reading))
        let b4 = try XCTUnwrap(library.addRecordBook("百年孤独", status: .finished))
        let b5 = try XCTUnwrap(library.addRecordBook("红楼梦", status: .reading))
        let b6 = try XCTUnwrap(library.addRecordBook("万历十五年", status: .finished))

        let c1 = try XCTUnwrap(library.createCollection("英文原著", with: b1.id))
        library.setBook(b2.id, in: c1, true)
        library.setBook(b3.id, in: c1, true)

        let c2 = try XCTUnwrap(library.createCollection("文学与思想", with: b4.id))
        library.setBook(b5.id, in: c2, true)
        library.setBook(b6.id, in: c2, true)

        let c3 = try XCTUnwrap(library.createCollection("通勤听", with: b5.id))
        library.setBook(b3.id, in: c3, true)

        let c4 = try XCTUnwrap(library.createCollection("科幻", with: b1.id))
        _ = library.createCollection("睡前")
        let c6 = try XCTUnwrap(library.createCollection("历史与社会"))
        _ = library.createCollection("周末闲读")
        _ = library.createCollection("诗词选")
        _ = c4

        let pinned = PinnedCollections.raw([c1, c2, c6])
        let wall = CollectionWall(library: library, pinnedRaw: .constant(pinned), leading: 30) { _ in }
        snap(wall, width: 960, height: 640)
    }
}
#endif

extension ScreenCatalogTests {
    /// The paper card the app draws when a book cannot be opened. iPhone's system alerts are not this.
    @MainActor
    fileprivate var bookWontOpen: some View {
        ZStack {
            YYColor.bg
            Color.black.opacity(0.18)
            YYDialogCard(
                tone: .error, icon: .docX,
                title: String(localized: "这本书打不开"),
                message: String(localized: "这本书读不出来，文件可能坏了，或者不是它看起来的格式"),
                primaryTitle: String(localized: "回到书架"),
                onPrimary: {},
                wide: true
            )
            .padding(.horizontal, 24)
        }
    }
}

final class CoreFlowRegressionTests: XCTestCase {
    @MainActor
    func testTextVersionCardRendersWithoutEnvironmentLibrary() throws {
        let scratch = FileManager.default.temporaryDirectory.appending(path: "yuyue-test-\(UUID().uuidString)", directoryHint: .isDirectory)
        defer {
            UserDefaults.standard.removeObject(forKey: "debugLibrary")
            try? FileManager.default.removeItem(at: scratch)
        }
        UserDefaults.standard.set(scratch.path(percentEncoded: false), forKey: "debugLibrary")
        let library = try Library()
        let book = try XCTUnwrap(library.addRecordBook("背影", status: .reading))

        // Mount TextVersionCard without `.environment(library)` to guard against the P1 SIGTRAP.
        let card = TextVersionCard(library: library, summary: book)
            .frame(width: 340, height: 140)
        #if os(macOS)
        let host = NSHostingView(rootView: card)
        host.frame = NSRect(x: 0, y: 0, width: 340, height: 140)
        host.layoutSubtreeIfNeeded()
        XCTAssertGreaterThan(host.fittingSize.height, 0)
        #else
        let host = UIHostingController(rootView: card)
        host.view.frame = CGRect(x: 0, y: 0, width: 340, height: 140)
        host.view.setNeedsLayout()
        host.view.layoutIfNeeded()
        XCTAssertGreaterThan(host.view.bounds.height, 0)
        #endif
    }

    func testOpdsAudioMp4AndMultiFormatParsing() throws {
        let xml = """
        <?xml version="1.0" encoding="utf-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <title>测试书源</title>
          <entry>
            <id>urn:book:beiying-audio</id>
            <title>背影（朗读）</title>
            <author><name>朱自清</name></author>
            <link rel="http://opds-spec.org/acquisition" href="/media/beiying-short.m4a" type="audio/mp4" length="1468006"/>
          </entry>
          <entry>
            <id>urn:book:hongloumeng-m4b</id>
            <title>红楼梦有声</title>
            <link rel="http://opds-spec.org/acquisition/open-access" href="/media/hlm.m4b?v=1" type="audio/mp4"/>
          </entry>
          <entry>
            <id>urn:book:dual</id>
            <title>背影</title>
            <link rel="http://opds-spec.org/acquisition" href="/media/beiying.m4a" type="audio/mp4"/>
            <link rel="http://opds-spec.org/acquisition" href="/books/beiying.epub" type="application/epub+zip"/>
          </entry>
        </feed>
        """
        let base = URL(string: "https://books.example.com/opds/feed.xml")!
        let (feed, _) = try OPDSParser.parse(Data(xml.utf8), base: base)
        XCTAssertEqual(feed.entries.count, 3)

        let audio = feed.entries[0]
        XCTAssertEqual(audio.title, "背影（朗读）")
        XCTAssertEqual(audio.author, "朱自清")
        XCTAssertEqual(audio.format, "M4A")
        XCTAssertEqual(audio.size, 1_468_006)
        if case .book(let url, let type) = audio.kind {
            XCTAssertEqual(url.absoluteString, "https://books.example.com/media/beiying-short.m4a")
            XCTAssertEqual(type, "audio/mp4")
            XCTAssertEqual(RemoteClient.name(audio, url: url, type: type, suggested: nil), "beiying-short.m4a")
        } else {
            XCTFail("Expected book entry for audio/mp4 acquisition link")
        }

        let noExtURL = URL(string: "https://books.example.com/download/99")!
        XCTAssertEqual(RemoteClient.name(audio, url: noExtURL, type: "audio/mp4", suggested: nil), "背影（朗读）.m4a")

        XCTAssertEqual(feed.entries[1].format, "M4B")
        XCTAssertEqual(feed.entries[2].format, "EPUB")
    }

    func testScrolledVerticalOverscrollAndArming() {
        // Inside scroll bounds: no overscroll.
        XCTAssertNil(ReaderPager.verticalOverscroll(y: 0, maxY: 400))
        XCTAssertNil(ReaderPager.verticalOverscroll(y: 200, maxY: 400))
        XCTAssertNil(ReaderPager.verticalOverscroll(y: 400, maxY: 400))

        // Past bottom edge -> .next.
        let bottom = ReaderPager.verticalOverscroll(y: 472, maxY: 400)
        XCTAssertEqual(bottom?.edge, .next)
        XCTAssertEqual(bottom?.amount ?? 0, 72, accuracy: 0.01)

        // Past top edge -> .previous.
        let top = ReaderPager.verticalOverscroll(y: -68, maxY: 400)
        XCTAssertEqual(top?.edge, .previous)
        XCTAssertEqual(top?.amount ?? 0, 68, accuracy: 0.01)

        // Short section (maxY == 0): direction alone selects edge.
        XCTAssertEqual(ReaderPager.verticalOverscroll(y: 40, maxY: 0)?.edge, .next)
        XCTAssertEqual(ReaderPager.verticalOverscroll(y: -40, maxY: 0)?.edge, .previous)

        // Hysteresis around pullThreshold (64pt, hysteresis 8pt).
        XCTAssertFalse(ReaderPager.isPullArmed(amount: 63, wasArmed: false))
        XCTAssertTrue(ReaderPager.isPullArmed(amount: 64, wasArmed: false))
        XCTAssertTrue(ReaderPager.isPullArmed(amount: 57, wasArmed: true))
        XCTAssertFalse(ReaderPager.isPullArmed(amount: 55, wasArmed: true))
    }
}
