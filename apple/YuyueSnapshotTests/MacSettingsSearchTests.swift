#if os(macOS)
import AppKit
import Foundation
import SwiftUI
import Testing
@testable import Yuyue

@MainActor
struct MacSettingsSearchTests {
    @Test func `every settings anchor is indexed exactly once and spans all pages`() {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let anchors = items.map(\.anchor)
        #expect(anchors.count == Set(anchors).count)
        #expect(Set(anchors) == Set(SettingsAnchor.allCases))

        let coveredPages = Set(items.map(\.page))
        #expect(coveredPages == Set(SettingsPage.allCases))
    }

    @Test func `liquid glass entry respects runtime platform support flag`() {
        let supported = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let unsupported = SettingsSearchIndex.allItems(liquidGlassSupported: false)
        #expect(supported.contains { $0.anchor == .appearanceLiquidGlass })
        #expect(!unsupported.contains { $0.anchor == .appearanceLiquidGlass })
    }

    @Test func `empty or whitespace query returns no search matches`() {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        #expect(SettingsSearchIndex.search("", in: items).isEmpty)
        #expect(SettingsSearchIndex.search("   \n\t  ", in: items).isEmpty)
    }

    @Test func `exact Chinese title match ranks first`() throws {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let hits = SettingsSearchIndex.search("纸色", in: items)
        let top = try #require(hits.first)
        #expect(top.item.anchor == .readingPaper)
        #expect(top.item.page == .reading)
    }

    @Test(arguments: [
        ("zs", SettingsAnchor.readingPaper),
        ("zhise", SettingsAnchor.readingPaper),
        ("fyfs", SettingsAnchor.readingLayoutMode),
        ("ziti", SettingsAnchor.readingFont),
        ("jgy", SettingsAnchor.syncWebDAV),
    ])
    func `pinyin and initials find expected settings`(query: String, expected: SettingsAnchor) {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let hits = SettingsSearchIndex.search(query, in: items)
        #expect(hits.contains { $0.item.anchor == expected })
    }

    @Test(arguments: [
        ("webdav", SettingsAnchor.syncWebDAV),
        ("obsidian", SettingsAnchor.notesObsidian),
        ("dark", SettingsAnchor.appearanceMode),
        ("snapshot", SettingsAnchor.dataSnapshots),
        ("mdx", SettingsAnchor.foreignMDict),
        ("deepseek", SettingsAnchor.companionProviders),
        ("privacy", SettingsAnchor.aboutPrivacy),
    ])
    func `english keywords find corresponding settings`(query: String, expected: SettingsAnchor) {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let hits = SettingsSearchIndex.search(query, in: items)
        #expect(hits.contains { $0.item.anchor == expected })
    }

    @Test func `multi-term query narrows across page and row terms`() throws {
        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let hits = SettingsSearchIndex.search("外文 发音", in: items)
        #expect(!hits.isEmpty)
        #expect(hits.allSatisfy { $0.item.page == .foreign })
        let anchors = Set(hits.map(\.item.anchor))
        #expect(anchors.contains(.foreignAutoSpeak) || anchors.contains(.foreignAccent))
    }

    @Test func `recent searches deduplicate, preserve order, and cap at four`() throws {
        let suite = "app.yuyue.tests.settings-search-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }

        let items = SettingsSearchIndex.allItems(liquidGlassSupported: true)
        let sequence: [SettingsAnchor] = [
            .generalLanguage,
            .readingPaper,
            .syncWebDAV,
            .notesObsidian,
            .dataSnapshots,
            .readingPaper,
        ]
        for anchor in sequence {
            SettingsSearchIndex.recordRecent(anchor, defaults: defaults)
        }

        let recents = SettingsSearchIndex.recentItems(from: items, defaults: defaults)
        #expect(recents.map(\.anchor) == [
            .readingPaper,
            .dataSnapshots,
            .notesObsidian,
            .syncWebDAV,
        ])

        SettingsSearchIndex.clearRecent(defaults: defaults)
        #expect(SettingsSearchIndex.recentItems(from: items, defaults: defaults).isEmpty)
    }

    @Test func `highlighting marks matched characters for both direct and pinyin queries`() {
        let direct = SettingsSearchIndex.highlighted("书架封面大小", query: "封面", accent: .orange)
        let directRuns = direct.runs.filter { $0.foregroundColor != nil }
        #expect(!directRuns.isEmpty)

        let pinyin = SettingsSearchIndex.highlighted("纸色", query: "zs", accent: .orange)
        let pinyinRuns = pinyin.runs.filter { $0.foregroundColor != nil }
        #expect(!pinyinRuns.isEmpty)
    }

    @Test func `settings search corner and overlay render cleanly in light and dark modes`() throws {
        let previewDir = ProcessInfo.processInfo.environment["YY_SETTINGS_SEARCH_PREVIEW_DIR"]
            .map { URL(filePath: $0, directoryHint: .isDirectory) }
        if let previewDir {
            try FileManager.default.createDirectory(at: previewDir, withIntermediateDirectories: true)
        }

        let cases: [(name: String, dark: Bool, view: AnyView)] = [
            ("corner-and-highlight-light", false, AnyView(
                HStack(alignment: .top, spacing: 0) {
                    SettingsSidebar(page: .constant(.appearance), tint: .byDefault)
                    VStack(spacing: 0) {
                        SettingsPageHeader(page: .appearance)
                        ScrollView {
                            AppearanceSettings()
                                .padding(.leading, 24)
                                .padding(.trailing, 18)
                                .padding(.top, 10)
                        }
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .background(YYColor.bg)
                }
                .environment(\.settingsHighlightedAnchor, .appearanceTint)
                .overlay(alignment: .topTrailing) {
                    SettingsCorner(compact: true, searchOpen: .constant(false))
                        .padding(.trailing, 10)
                        .padding(.top, 13)
                }
            )),
            ("search-empty-light", false, AnyView(
                HStack(alignment: .top, spacing: 0) {
                    SettingsSidebar(page: .constant(.appearance), tint: .byDefault)
                    VStack(spacing: 0) {
                        SettingsPageHeader(page: .appearance)
                        AppearanceSettings().padding(.horizontal, 24).padding(.top, 10)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
                    .background(YYColor.bg)
                }
                .overlay(alignment: .topTrailing) {
                    SettingsCorner(compact: true, searchOpen: .constant(true))
                        .padding(.trailing, 10)
                        .padding(.top, 13)
                }
                .overlay {
                    SettingsSearchOverlay(initialQuery: "", close: {}, jump: { _ in })
                }
            )),
            ("search-query-light", false, AnyView(
                ZStack {
                    YYColor.bg
                    SettingsSearchOverlay(initialQuery: "翻页", close: {}, jump: { _ in })
                }
            )),
            ("search-query-dark", true, AnyView(
                ZStack {
                    YYColor.bg
                    SettingsSearchOverlay(initialQuery: "webdav", close: {}, jump: { _ in })
                }
            )),
        ]

        for entry in cases {
            let size = NSSize(width: 960, height: 620)
            let framed = entry.view
                .frame(width: size.width, height: size.height)
                .environment(\.colorScheme, entry.dark ? .dark : .light)
                .transaction { $0.disablesAnimations = true }
            let hosting = NSHostingView(rootView: framed)
            hosting.appearance = NSAppearance(named: entry.dark ? .darkAqua : .aqua)
            hosting.frame = NSRect(origin: .zero, size: size)
            hosting.layoutSubtreeIfNeeded()
            let rep = try #require(hosting.bitmapImageRepForCachingDisplay(in: hosting.bounds))
            hosting.cacheDisplay(in: hosting.bounds, to: rep)
            let png = try #require(rep.representation(using: .png, properties: [:]))
            #expect(png.count > 1000)
            if let previewDir {
                try png.write(to: previewDir.appending(path: "\(entry.name).png"))
            }
        }
    }
}
#endif
