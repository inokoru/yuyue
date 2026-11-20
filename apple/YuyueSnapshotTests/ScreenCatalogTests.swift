import SnapshotTesting
import SwiftUI
import XCTest
#if canImport(AppKit)
import AppKit
#endif
@testable import Yuyue

/// 界面快照. These render SwiftUI on its own, with fixture words, and compare PNGs under
/// `__Snapshots__/`. They are not the data-backup snapshots (`Snapshots.swift`, `PhoneSnapshot.swift`,
/// `snapshot.rs`), and they are not `scripts/window-catalog.sh` — that one still launches the app
/// for system permission dialogs, the Sparkle installer and the WebView reader.
///
/// First run on a machine records any missing PNG and fails; run again to compare. Re-record
/// everything with `YY_SNAPSHOT_RECORD=all`. See docs/dev/testing.md.
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
        hosting.frame = NSRect(x: 0, y: 0, width: width, height: height)
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
                TopButton(title: "反馈", symbol: "text.bubble") {}
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
        snap(
            YYDialogCard(
                tone: .error, icon: .bang,
                title: String(localized: "删除《红楼梦》？"),
                message: String(localized: "书会移到已删除，30 天内可以在存储里撤销。"),
                secondaryTitle: String(localized: "取消"),
                primaryTitle: String(localized: "删除"),
                onPrimary: {},
                destructive: true
            )
            .padding(24)
            .background(YYColor.bg),
            width: 420, height: 280
        )
    }

    @MainActor
    func testMacBookWontOpen() {
        snap(bookWontOpen, width: 480, height: 360)
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
