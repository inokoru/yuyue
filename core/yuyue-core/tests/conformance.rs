//! Layer 2: Shared UniFFI conformance runner (Rust side).
//!
//! Executes every scenario JSON in `fixtures/conformance/` (except `reader-messages.json`)
//! against the public `YuyueCore` API. Identical runners exist in C# (Windows), Swift (Apple),
//! and Kotlin (Android).

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use serde_json::Value;
use yuyue_core::changelog::compare_versions;
use yuyue_core::conflicts::ConflictChoice;
use yuyue_core::document::{Font, LayoutMode, Paging, Paper};
use yuyue_core::notes::{MarkStyle, TextPoint};
use yuyue_core::segment::page_sentences;
use yuyue_core::snapshot::SnapshotParts;
use yuyue_core::{BookFormat, PageGeometry, Position, SyncPart, YuyueCore};

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn parse_paper(s: &str) -> Paper {
    match s {
        "plain" => Paper::Plain,
        "dark" => Paper::Dark,
        "kami" => Paper::Kami,
        "kaminight" => Paper::KamiNight,
        "sage" => Paper::Sage,
        "eink" => Paper::Eink,
        other => panic!("unknown paper: {other}"),
    }
}

fn parse_mode(s: &str) -> LayoutMode {
    match s {
        "paginated" => LayoutMode::Paginated,
        "scrolled" => LayoutMode::Scrolled,
        other => panic!("unknown mode: {other}"),
    }
}

fn parse_font(s: &str) -> Font {
    match s {
        "sans" => Font::Sans,
        "serif" => Font::Serif,
        "kai" => Font::Kai,
        "wenkai" => Font::WenKai,
        other => panic!("unknown font: {other}"),
    }
}

fn parse_style(s: &str) -> MarkStyle {
    match s {
        "yellow" => MarkStyle::Yellow,
        "blue" => MarkStyle::Blue,
        "pink" => MarkStyle::Pink,
        "wave" => MarkStyle::Wave,
        "underline" => MarkStyle::Underline,
        "dotted" => MarkStyle::Dotted,
        other => panic!("unknown mark style: {other}"),
    }
}

fn parse_format(s: &str) -> BookFormat {
    match s {
        "epub" => BookFormat::Epub,
        "txt" => BookFormat::Txt,
        "pdf" => BookFormat::Pdf,
        "mobi" => BookFormat::Mobi,
        "audio" => BookFormat::Audio,
        "podcast" => BookFormat::Podcast,
        other => panic!("unknown book format: {other}"),
    }
}

fn parse_sync_part(s: &str) -> SyncPart {
    match s {
        "reading" => SyncPart::Reading,
        "notes" => SyncPart::Notes,
        "settings" => SyncPart::Settings,
        other => panic!("unknown sync part: {other}"),
    }
}

fn parse_choice(s: &str) -> ConflictChoice {
    match s {
        "older" => ConflictChoice::Older,
        "newer" => ConflictChoice::Newer,
        "both" => ConflictChoice::Both,
        other => panic!("unknown conflict choice: {other}"),
    }
}

fn run_scenario(path: &Path) {
    let raw = fs::read_to_string(path).unwrap_or_else(|e| panic!("read {}: {e}", path.display()));
    let scenario: Value = serde_json::from_str(&raw).unwrap_or_else(|e| panic!("parse {}: {e}", path.display()));
    let id = scenario["id"].as_str().expect("scenario id");
    let steps = scenario["steps"].as_array().expect("scenario steps");

    let nonce = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let work = std::env::temp_dir().join(format!("yuyue-conformance-{id}-{}-{nonce}", std::process::id()));
    let _ = fs::remove_dir_all(&work);
    fs::create_dir_all(&work).unwrap();

    let fixtures = repo_root().join("fixtures");
    let mut cores: HashMap<String, Arc<YuyueCore>> = HashMap::new();
    let mut vars: HashMap<String, String> = HashMap::new();

    let geom = PageGeometry {
        paging: Paging::Native,
        margin: 20.0,
        top: 50.0,
        bottom: 80.0,
        columns: 1,
        reduced_motion: false,
    };

    for (idx, step) in steps.iter().enumerate() {
        let op = step["op"].as_str().unwrap_or_else(|| panic!("{id}[{idx}]: missing op"));
        let ctx = format!("{id}[{idx}] ({op})");
        match op {
            "open" => {
                let dev = step["device"].as_str().unwrap();
                let name = step["name"].as_str().unwrap();
                let platform = step["platform"].as_str().unwrap();
                let dir = work.join(format!("dev-{dev}"));
                let core = YuyueCore::open(dir.to_string_lossy().into_owned(), name.into(), platform.into())
                    .unwrap_or_else(|e| panic!("{ctx}: open failed: {e}"));
                cores.insert(dev.to_string(), core);
            }
            "close" => {
                let dev = step["device"].as_str().unwrap();
                cores.remove(dev);
            }
            "import_book" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let rel = step["fixture"].as_str().unwrap();
                let full = fixtures.join(rel);
                let res = core.import_book(full.to_string_lossy().into_owned());
                if step["expect_error"].as_bool().unwrap_or(false) {
                    assert!(res.is_err(), "{ctx}: expected error importing {rel}, got {res:?}");
                    continue;
                }
                let book = res.unwrap_or_else(|e| panic!("{ctx}: import_book failed: {e}"));
                if let Some(save) = step["save_as"].as_str() {
                    vars.insert(save.to_string(), book.id.clone());
                }
                if let Some(exp) = step.get("expect") {
                    if let Some(fmt) = exp["format"].as_str() {
                        assert_eq!(book.format, parse_format(fmt), "{ctx}: format mismatch");
                    }
                    if let Some(avail) = exp["available"].as_bool() {
                        assert_eq!(book.available, avail, "{ctx}: available mismatch");
                    }
                    if let Some(ref_k) = exp["same_id_as"].as_str() {
                        assert_eq!(book.id, vars[ref_k], "{ctx}: dedup id mismatch");
                    }
                    if let Some(sub) = exp["title_contains"].as_str() {
                        assert!(book.title.contains(sub), "{ctx}: title '{}' missing '{}'", book.title, sub);
                    }
                }
            }
            "assert_books" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let books = core.books().unwrap();
                if let Some(count) = step["count"].as_u64() {
                    assert_eq!(books.len() as u64, count, "{ctx}: books count");
                }
                if let Some(first_ref) = step["first_id_ref"].as_str() {
                    assert_eq!(books[0].id, vars[first_ref], "{ctx}: first book id");
                }
            }
            "assert_sentences" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let section = step["section"].as_u64().unwrap() as u32;
                let sents = core.sentences(book.clone(), section).unwrap();
                if let Some(min) = step["min_count"].as_u64() {
                    assert!(sents.len() as u64 >= min, "{ctx}: sentences len {} < {min}", sents.len());
                }
                if let Some(sub) = step["any_contains"].as_str() {
                    assert!(sents.iter().any(|s| s.contains(sub)), "{ctx}: no sentence contains '{sub}'");
                }
            }
            "save_progress" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let pos = Position {
                    section: step["section"].as_u64().unwrap() as u32,
                    sentence: step["sentence"].as_u64().unwrap() as u32,
                };
                core.save_progress(book.clone(), pos).unwrap();
            }
            "assert_progress" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let kind = step["kind"].as_str().unwrap();
                let got = match kind {
                    "own" => core.progress(book.clone()).unwrap(),
                    "latest" => core.latest_progress(book.clone()).unwrap(),
                    other => panic!("{ctx}: unknown progress kind {other}"),
                };
                let present = step["present"].as_bool().unwrap();
                assert_eq!(got.is_some(), present, "{ctx}: progress presence");
                if let Some(p) = got {
                    if let Some(sec) = step["section"].as_u64() {
                        assert_eq!(p.position.section, sec as u32, "{ctx}: section");
                    }
                    if let Some(sen) = step["sentence"].as_u64() {
                        assert_eq!(p.position.sentence, sen as u32, "{ctx}: sentence");
                    }
                    if let Some(td) = step["this_device"].as_bool() {
                        assert_eq!(p.this_device, td, "{ctx}: this_device");
                    }
                    if let Some(min_f) = step["min_fraction"].as_f64() {
                        assert!(p.fraction >= min_f, "{ctx}: fraction {} < {min_f}", p.fraction);
                    }
                }
            }
            "set_reader_settings" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let mut s = core.reader_settings().unwrap();
                if let Some(p) = step["paper"].as_str() {
                    s.paper = parse_paper(p);
                }
                if let Some(m) = step["mode"].as_str() {
                    s.mode = parse_mode(m);
                }
                if let Some(f) = step["font"].as_str() {
                    s.font = Some(parse_font(f));
                }
                if let Some(fs) = step["font_size"].as_f64() {
                    s.font_size = fs;
                }
                if let Some(c) = step["compress"].as_bool() {
                    s.compress = c;
                }
                core.set_reader_settings(s).unwrap();
            }
            "assert_reader_settings" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let s = core.reader_settings().unwrap();
                if let Some(p) = step["paper"].as_str() {
                    assert_eq!(s.paper, parse_paper(p), "{ctx}: paper");
                }
                if let Some(m) = step["mode"].as_str() {
                    assert_eq!(s.mode, parse_mode(m), "{ctx}: mode");
                }
                if let Some(f) = step["font"].as_str() {
                    assert_eq!(s.font, Some(parse_font(f)), "{ctx}: font");
                }
                if let Some(fs) = step["font_size"].as_f64() {
                    assert!((s.font_size - fs).abs() < 1e-6, "{ctx}: font_size {} != {fs}", s.font_size);
                }
                if let Some(c) = step["compress"].as_bool() {
                    assert_eq!(s.compress, c, "{ctx}: compress");
                }
            }
            "set_book_reader_settings" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                if step["clear"].as_bool().unwrap_or(false) {
                    core.set_book_reader_settings(book.clone(), None).unwrap();
                } else {
                    let mut s = core.reader_settings().unwrap();
                    if let Some(p) = step["paper"].as_str() {
                        s.paper = parse_paper(p);
                    }
                    if let Some(fs) = step["font_size"].as_f64() {
                        s.font_size = fs;
                    }
                    core.set_book_reader_settings(book.clone(), Some(s)).unwrap();
                }
            }
            "assert_section_document" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let section = step["section"].as_u64().unwrap() as u32;
                let html = core.section_document(book.clone(), section, geom.clone()).unwrap();
                for sub in step["contains"].as_array().unwrap() {
                    let needle = sub.as_str().unwrap();
                    assert!(html.contains(needle), "{ctx}: section_document missing '{needle}'");
                }
            }
            "add_mark" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let mark = core
                    .add_mark(
                        book.clone(),
                        step["section"].as_u64().unwrap() as u32,
                        TextPoint {
                            sentence: step["start_sentence"].as_u64().unwrap() as u32,
                            offset: step["start_offset"].as_u64().unwrap() as u32,
                        },
                        TextPoint {
                            sentence: step["end_sentence"].as_u64().unwrap() as u32,
                            offset: step["end_offset"].as_u64().unwrap() as u32,
                        },
                        step["text"].as_str().unwrap().to_string(),
                        parse_style(step["style"].as_str().unwrap()),
                        step["note"].as_str().map(str::to_string),
                    )
                    .unwrap();
                if let Some(save) = step["save_as"].as_str() {
                    vars.insert(save.to_string(), mark.id);
                }
            }
            "set_mark_note" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let mark = &vars[step["mark"].as_str().unwrap()];
                core.set_mark_note(book.clone(), mark.clone(), step["note"].as_str().map(str::to_string)).unwrap();
            }
            "set_mark_style" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let mark = &vars[step["mark"].as_str().unwrap()];
                core.set_mark_style(book.clone(), mark.clone(), parse_style(step["style"].as_str().unwrap())).unwrap();
            }
            "assert_marks" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let marks = core.marks(book.clone()).unwrap();
                let count = step["count"].as_u64().unwrap();
                assert_eq!(marks.len() as u64, count, "{ctx}: marks count");
                if count > 0 {
                    if let Some(st) = step["first_style"].as_str() {
                        assert_eq!(marks[0].style, parse_style(st), "{ctx}: mark style");
                    }
                    if let Some(note) = step["first_note"].as_str() {
                        assert_eq!(marks[0].note.as_deref(), Some(note), "{ctx}: mark note");
                    }
                }
            }
            "search_book" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let hits = core.search_book(book.clone(), step["query"].as_str().unwrap().into()).unwrap();
                let min_hits = step["min_hits"].as_u64().unwrap();
                assert!(hits.len() as u64 >= min_hits, "{ctx}: hits {} < {min_hits}", hits.len());
            }
            "delete_book" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                core.delete_book(book.clone()).unwrap();
            }
            "restore_book" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                core.restore_book(book.clone()).unwrap();
            }
            "sleep_ms" => {
                std::thread::sleep(Duration::from_millis(step["ms"].as_u64().unwrap()));
            }
            "sync" => {
                let from = &cores[step["from"].as_str().unwrap()];
                let to = &cores[step["to"].as_str().unwrap()];
                let full = step["full"].as_bool().unwrap_or(false);
                let version = if full { "{}".to_string() } else { to.sync_version().unwrap() };
                let left_out: Vec<SyncPart> = step
                    .get("left_out")
                    .and_then(Value::as_array)
                    .map(|arr| arr.iter().map(|v| parse_sync_part(v.as_str().unwrap())).collect())
                    .unwrap_or_default();
                let batch = if left_out.is_empty() {
                    from.export_changes(version).unwrap()
                } else {
                    from.export_changes_except(version, left_out.clone()).unwrap()
                };
                let changed = if left_out.is_empty() {
                    to.import_changes(batch).unwrap()
                } else {
                    to.import_changes_except(batch, left_out).unwrap()
                };
                if let Some(min_c) = step["min_changed"].as_u64() {
                    assert!(changed as u64 >= min_c, "{ctx}: changed {changed} < {min_c}");
                }
                if let Some(exact_c) = step["exact_changed"].as_u64() {
                    assert_eq!(changed as u64, exact_c, "{ctx}: changed {changed} != {exact_c}");
                }
            }
            "assert_conflicts" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let conflicts = core.note_conflicts().unwrap();
                let count = step["count"].as_u64().unwrap();
                assert_eq!(conflicts.len() as u64, count, "{ctx}: conflicts count");
                if count > 0 {
                    if let Some(o) = step["first_older_note"].as_str() {
                        assert_eq!(conflicts[0].older.note.as_deref(), Some(o), "{ctx}: older note");
                    }
                    if let Some(n) = step["first_newer_note"].as_str() {
                        assert_eq!(conflicts[0].newer.note.as_deref(), Some(n), "{ctx}: newer note");
                    }
                }
            }
            "resolve_conflict" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let book = &vars[step["book"].as_str().unwrap()];
                let mark = &vars[step["mark"].as_str().unwrap()];
                let choice = parse_choice(step["choice"].as_str().unwrap());
                core.resolve_note_conflict(book.clone(), mark.clone(), choice).unwrap();
            }
            "make_snapshot" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let snap = work.join(step["snapshot_name"].as_str().unwrap());
                let parts = SnapshotParts {
                    progress: step["progress"].as_bool().unwrap(),
                    notes: step["notes"].as_bool().unwrap(),
                    words: step["words"].as_bool().unwrap(),
                    settings: step["settings"].as_bool().unwrap(),
                    files: step["files"].as_bool().unwrap(),
                };
                let bytes = core.make_snapshot(snap.to_string_lossy().into_owned(), parts).unwrap();
                assert!(bytes > 0, "{ctx}: snapshot empty");
            }
            "restore_snapshot" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let snap = work.join(step["snapshot_name"].as_str().unwrap());
                let replace = step["replace"].as_bool().unwrap();
                core.restore_snapshot(snap.to_string_lossy().into_owned(), replace).unwrap();
            }
            "assert_snapshot_counts" => {
                let core = &cores[step["device"].as_str().unwrap()];
                let counts = core.snapshot_counts().unwrap();
                if let Some(b) = step["min_books"].as_u64() {
                    assert!(counts.books as u64 >= b, "{ctx}: books {} < {b}", counts.books);
                }
                if let Some(n) = step["min_notes"].as_u64() {
                    assert!(counts.notes as u64 >= n, "{ctx}: notes {} < {n}", counts.notes);
                }
            }
            "assert_page_sentences" => {
                let text = step["text"].as_str().unwrap().to_string();
                let got: Vec<String> = page_sentences(text).into_iter().map(|s| s.text).collect();
                let exp: Vec<String> = step["expected_texts"]
                    .as_array()
                    .unwrap()
                    .iter()
                    .map(|v| v.as_str().unwrap().to_string())
                    .collect();
                assert_eq!(got, exp, "{ctx}: page_sentences");
            }
            "assert_compare_versions" => {
                for c in step["cases"].as_array().unwrap() {
                    let a = c["a"].as_str().unwrap().to_string();
                    let b = c["b"].as_str().unwrap().to_string();
                    let exp = c["sign"].as_i64().unwrap() as i32;
                    let got = compare_versions(a.clone(), b.clone()).signum();
                    assert_eq!(got, exp, "{ctx}: compare_versions({a}, {b})");
                }
            }
            other => panic!("{ctx}: unsupported op '{other}'"),
        }
    }

    drop(cores);
    let _ = fs::remove_dir_all(&work);
}

#[test]
fn shared_conformance_suite_passes() {
    let dir = repo_root().join("fixtures/conformance");
    let mut files: Vec<PathBuf> = fs::read_dir(&dir)
        .unwrap()
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| {
            p.extension().is_some_and(|e| e == "json")
                && p.file_name().is_some_and(|n| n != "reader-messages.json")
        })
        .collect();
    files.sort();
    assert!(!files.is_empty(), "no conformance fixtures found in {}", dir.display());
    for path in files {
        run_scenario(&path);
    }
}
