import Foundation
import Testing
@testable import Yuyue

/// Layer 3: Validates Apple's real `ReaderMessage(body:)` decoder against `reader/schema/bridge-messages.schema.json`
/// and every canonical payload in `fixtures/conformance/reader-messages.json`.
struct ReaderBridgeContractTests {
    private static func repoRoot() -> URL {
        URL(filePath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
    }

    @Test func `Apple ReaderMessage decodes all canonical bridge messages from schema`() throws {
        let root = Self.repoRoot()
        let schemaData = try Data(contentsOf: root.appending(path: "reader/schema/bridge-messages.schema.json"))
        let schema = try #require(JSONSerialization.jsonObject(with: schemaData) as? [String: Any])
        let schemaTypes = try #require(schema["x-message-types"] as? [String])
        #expect(schemaTypes.count >= 11)

        let fixtureData = try Data(contentsOf: root.appending(path: "fixtures/conformance/reader-messages.json"))
        let fixture = try #require(JSONSerialization.jsonObject(with: fixtureData) as? [String: Any])
        let valid = try #require(fixture["valid"] as? [[String: Any]])

        var seen: Set<String> = []
        for raw in valid {
            let decoded = try #require(ReaderMessage(body: raw))
            seen.insert(decoded.type.rawValue)
            switch decoded.type {
            case .ready, .layout:
                #expect(decoded.section != nil)
                #expect((decoded.pageCount ?? 0) > 0)
                #expect(decoded.sentencePages != nil)
                #expect(decoded.pageFirstSentence != nil)
            case .tap:
                #expect(decoded.x != nil && decoded.y != nil)
                #expect(decoded.width != nil && decoded.height != nil)
                if let word = decoded.word {
                    #expect(!word.text.isEmpty)
                }
            case .doubleTap, .context:
                #expect(decoded.x != nil && decoded.y != nil)
            case .selection:
                if decoded.text != nil {
                    #expect(decoded.start != nil && decoded.end != nil && decoded.rect != nil)
                }
            case .activePage:
                #expect(decoded.sentence != nil && decoded.page != nil)
            case .bubble:
                #expect(decoded.line != nil)
            case .page:
                #expect(decoded.page != nil)
            case .scroll:
                #expect(decoded.progress != nil)
            case .log:
                #expect(decoded.level != nil && decoded.message != nil)
            }
        }

        for expected in schemaTypes {
            #expect(seen.contains(expected))
        }
    }
}
