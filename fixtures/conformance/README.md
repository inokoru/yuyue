# Shared Core Conformance Fixtures (Layer 2)

Every `*.json` file here (except `reader-messages.json`, which is for Layer 3 bridge validation) is a language-agnostic scenario executed identically against `YuyueCore` through UniFFI across all four platforms:

- **Rust** (`core/yuyue-core/tests/conformance.rs`) — runs in `cargo test --workspace`
- **Windows / C#** (`windows/Yuyue.Tests/ConformanceTests.cs`) — runs in `dotnet test`
- **Apple / Swift** (`apple/YuyueSnapshotTests/ConformanceTests.swift`) — runs in `xcodebuild test`
- **Android / Kotlin** (`android/app/src/test/java/app/yuyue/core/ConformanceTest.kt`) — runs in `./gradlew testDebugUnitTest` when host `libyuyue_core` is built

## Scenario Schema

Each scenario JSON has `{ "id", "description", "steps": [ ... ] }`.

Supported `op` values:
- `open`: `{ device, name, platform, fresh?: true }` — opens/reopens `YuyueCore` for `device`
- `close`: `{ device }` — drops/disposes the `YuyueCore` handle (keeps its temp dir for reopen tests)
- `import_book`: `{ device, fixture, save_as?, expect_error?, expect?: { format?, available?, same_id_as?, title_contains? } }`
- `assert_books`: `{ device, count?, first_id_ref? }`
- `assert_sentences`: `{ device, book, section, min_count?, any_contains? }`
- `save_progress`: `{ device, book, section, sentence }`
- `assert_progress`: `{ device, book, kind: "own" | "latest", present, section?, sentence?, this_device?, min_fraction? }`
- `set_reader_settings`: `{ device, paper?, mode?, font?, font_size?, compress? }`
- `assert_reader_settings`: `{ device, paper?, mode?, font?, font_size?, compress? }`
- `set_book_reader_settings`: `{ device, book, clear?: bool, paper?, font_size? }`
- `assert_section_document`: `{ device, book, section, contains?: string[] }`
- `add_mark`: `{ device, book, section, start_sentence, start_offset, end_sentence, end_offset, text, style, note?, save_as? }`
- `set_mark_note`: `{ device, book, mark, note? }`
- `set_mark_style`: `{ device, book, mark, style }`
- `assert_marks`: `{ device, book, count, first_style?, first_note?, first_note_contains?: string[] }`
- `search_book`: `{ device, book, query, min_hits }`
- `delete_book`: `{ device, book }`
- `restore_book`: `{ device, book }`
- `sleep_ms`: `{ ms }`
- `sync`: `{ from, to, left_out?: ("reading"|"notes"|"settings")[], full?: bool, min_changed?, exact_changed? }`
- `assert_conflicts`: `{ device, count, first_older_note?, first_newer_note? }`
- `resolve_conflict`: `{ device, book, mark, choice: "older"|"newer"|"both" }`
- `make_snapshot`: `{ device, snapshot_name, progress, notes, words, settings, files }`
- `restore_snapshot`: `{ device, snapshot_name, replace }`
- `assert_snapshot_counts`: `{ device, min_books?, min_notes? }`
- `assert_page_sentences`: `{ text, expected_texts: string[] }`
- `assert_compare_versions`: `{ cases: [{ a, b, sign: -1 | 0 | 1 }] }`
- `assert_review_today`: `{ device, day_start, count, exact_items?, exact_done_today? }`
- `set_mark_review`: `{ device, book, mark, keep }`
- `add_time`: `{ device, book, section, read_seconds, listen_seconds, day }`
- `assert_day_times`: `{ device, from, to, min_entries?, min_read_seconds?, min_listen_seconds? }`
- `bookmark_undo_roundtrip`: `{ device, book, section, sentence }`
- `collection_undo_roundtrip`: `{ device, name, book }`
- `custom_paper_undo_roundtrip`: `{ device, name, bg }`
- `word_undo_and_count`: `{ device, book, word, min_book_count }`
- `replace_rule_reorder`: `{ device, book }`
- `set_finished`: `{ device, book, finished }`
- `keep_record_only`: `{ device, book }`
- `purge_deleted`: `{ device, older_than_ms, min_purged }`
