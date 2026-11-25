//! Layer 2: `insta` snapshot tests locking deterministic core outputs across releases.
//!
//! Covers:
//!  - Sentence segmentation (`page_sentences`) regression lock (ROADMAP 0.5 位置考卷)
//!  - Section document HTML contract attributes across paper presets
//!  - Changelog parser output stability

use std::path::Path;

use serde_json::json;
use yuyue_core::changelog::parse_changelog;
use yuyue_core::document::{Font, LayoutMode, PageSettings, Paging, Paper, section_document};
use yuyue_core::segment::page_sentences;
use yuyue_core::{PageGeometry, YuyueCore};

#[test]
fn snapshot_page_sentences_mixed_cjk_and_latin() {
    let sample = "我与父亲不相见已二年余了，我最不能忘记的是他的背影。“不要紧，他们去不好！”他往车外看了看说。\n\nMr. Bennet was so odd a mixture of quick parts, sarcastic humour, reserve, and caprice. Indeed! Really?";
    let pieces: Vec<_> = page_sentences(sample.to_string())
        .into_iter()
        .map(|s| json!({ "start": s.start, "end": s.end, "text": s.text }))
        .collect();
    insta::assert_json_snapshot!("page_sentences_mixed", pieces);
}

#[test]
fn snapshot_section_document_html_contract_headers() {
    let dir = std::env::temp_dir().join(format!("yuyue-snap-doc-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&dir);
    let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "Snap".into(), "test".into()).unwrap();
    let fixture = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../fixtures/books/beiying.epub");
    let book = core.import_book(fixture.to_string_lossy().into_owned()).unwrap();
    let start_section = core.book(book.id.clone()).unwrap().start;

    let mut headers = Vec::new();
    for (label, paper, mode, font, fs, paging) in [
        ("plain-paginated-native", Paper::Plain, LayoutMode::Paginated, None, 18.0, Paging::Native),
        ("kami-paginated-snap", Paper::Kami, LayoutMode::Paginated, Some(Font::Serif), 20.0, Paging::Snap),
        ("dark-scrolled-snap", Paper::Dark, LayoutMode::Scrolled, Some(Font::Sans), 16.0, Paging::Snap),
        ("eink-paginated-native", Paper::Eink, LayoutMode::Paginated, Some(Font::WenKai), 22.0, Paging::Native),
    ] {
        let mut s = core.reader_settings().unwrap();
        s.paper = paper;
        s.mode = mode;
        s.font = font;
        s.font_size = fs;
        core.set_reader_settings(s).unwrap();
        let geom = PageGeometry {
            paging,
            margin: 24.0,
            top: 56.0,
            bottom: 88.0,
            columns: 1,
            reduced_motion: label.starts_with("eink"),
        };
        let html = core.section_document(book.id.clone(), start_section, geom).unwrap();
        // Snapshot the normalized <html ...> tag and <main ...> opening tag to lock the contract §2.
        let html_tag = html.lines().find(|l| l.starts_with("<html ")).unwrap_or("").to_string();
        let main_tag = html.lines().find(|l| l.starts_with("<main ")).unwrap_or("").to_string();
        headers.push(json!({ "case": label, "html": html_tag, "main": main_tag }));
    }
    let _ = std::fs::remove_dir_all(&dir);
    let _ = (PageSettings::default(), section_document);
    insta::assert_json_snapshot!("section_document_headers", headers);
}

#[test]
fn snapshot_changelog_parser_contract() {
    let md = "\
## 0.2.0 (2026-12-01)

### 中文
- 新增多端共享用例。
- 修复同步冲突展示。

### English
- Add cross-platform conformance suite.
- Fix sync conflict display.

## 0.1.3 (2026-11-20)

### 中文
- 优化本地朗读。

### English
- Improve local voices.
";
    let releases: Vec<_> = parse_changelog(md.to_string())
        .into_iter()
        .map(|r| {
            json!({
                "version": r.version,
                "date": r.date,
                "notes_zh": r.notes_zh,
                "notes_en": r.notes_en,
            })
        })
        .collect();
    insta::assert_json_snapshot!("changelog_parser", releases);
}
