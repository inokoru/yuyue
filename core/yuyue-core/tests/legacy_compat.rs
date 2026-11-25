//! Layer 5 & ROADMAP 0.5: Legacy library & `.yuyue` snapshot compatibility regression test.
//!
//! Opens every frozen sample directory under `fixtures/legacy/*/` with the current `YuyueCore`
//! and verifies zero data loss across books, progress, highlights/thoughts, vocabulary words,
//! settings, and `.yuyue` snapshot restore.

use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

use serde_json::Value;
use yuyue_core::YuyueCore;
use yuyue_core::document::Paper;

fn repo_root() -> PathBuf {
    Path::new(env!("CARGO_MANIFEST_DIR")).join("../..").canonicalize().unwrap()
}

fn copy_dir(from: &Path, to: &Path) {
    fs::create_dir_all(to).unwrap();
    for entry in fs::read_dir(from).unwrap() {
        let entry = entry.unwrap();
        let src = entry.path();
        let dst = to.join(entry.file_name());
        if entry.file_type().unwrap().is_dir() {
            copy_dir(&src, &dst);
        } else {
            fs::copy(&src, &dst).unwrap();
        }
    }
}

fn paper_name(p: Paper) -> &'static str {
    match p {
        Paper::Plain => "plain",
        Paper::Dark => "dark",
        Paper::Kami => "kami",
        Paper::KamiNight => "kaminight",
        Paper::Sage => "sage",
        Paper::Eink => "eink",
    }
}

fn verify_library(core: &YuyueCore, expected: &Value, label: &str) {
    let books = core.books().unwrap();
    let exp_books = expected["books"].as_array().unwrap();
    assert_eq!(books.len(), exp_books.len(), "{label}: book count");

    for eb in exp_books {
        let title_sub = eb["title_contains"].as_str().unwrap();
        let book = books
            .iter()
            .find(|b| b.title.contains(title_sub))
            .unwrap_or_else(|| panic!("{label}: missing book containing '{title_sub}'"));
        assert!(book.available, "{label}: book '{}' should be available", book.title);

        let marks = core.marks(book.id.clone()).unwrap();
        let exp_marks = eb["marks"].as_array().unwrap();
        assert_eq!(marks.len(), exp_marks.len(), "{label}: marks count");
        for em in exp_marks {
            let note = em["note"].as_str();
            assert!(
                marks.iter().any(|m| m.note.as_deref() == note),
                "{label}: missing mark with note {note:?}"
            );
        }

        if let Some(ep) = eb.get("latest_progress") {
            let p = core.latest_progress(book.id.clone()).unwrap().expect("missing latest_progress");
            assert_eq!(p.position.section, ep["section"].as_u64().unwrap() as u32, "{label}: progress section");
            assert_eq!(p.position.sentence, ep["sentence"].as_u64().unwrap() as u32, "{label}: progress sentence");
        }
    }

    let words = core.words().unwrap();
    let exp_words = expected["words"].as_array().unwrap();
    assert_eq!(words.len(), exp_words.len(), "{label}: words count");
    for ew in exp_words {
        let w = ew.as_str().unwrap();
        assert!(words.iter().any(|x| x.word == w), "{label}: missing word '{w}'");
    }

    let settings = core.reader_settings().unwrap();
    assert_eq!(
        paper_name(settings.paper),
        expected["settings"]["paper"].as_str().unwrap(),
        "{label}: settings.paper"
    );
    assert!(
        (settings.font_size - expected["settings"]["font_size"].as_f64().unwrap()).abs() < 1e-6,
        "{label}: settings.font_size"
    );
}

#[test]
fn opens_and_restores_all_frozen_legacy_libraries_without_data_loss() {
    let legacy_dir = repo_root().join("fixtures/legacy");
    let mut samples: Vec<PathBuf> = fs::read_dir(&legacy_dir)
        .unwrap()
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.is_dir() && p.join("expected.json").is_file())
        .collect();
    samples.sort();
    assert!(!samples.is_empty(), "no legacy samples found under {}", legacy_dir.display());

    let nonce = SystemTime::now().duration_since(UNIX_EPOCH).unwrap().as_nanos();
    let work = std::env::temp_dir().join(format!("yuyue-legacy-{}-{nonce}", std::process::id()));
    let _ = fs::remove_dir_all(&work);

    for sample in samples {
        let name = sample.file_name().unwrap().to_string_lossy().into_owned();
        let expected: Value = serde_json::from_str(&fs::read_to_string(sample.join("expected.json")).unwrap()).unwrap();

        // 1. Direct upgrade: open frozen SQLite library.db + books/ directory in place
        let direct_dir = work.join(format!("direct-{name}"));
        copy_dir(&sample, &direct_dir);
        let core = YuyueCore::open(direct_dir.to_string_lossy().into_owned(), "UpgradeCheck".into(), "test".into()).unwrap();
        verify_library(&core, &expected, &format!("{name}/direct"));
        drop(core);

        // 2. Snapshot restore: restore frozen snapshot.yuyue into a brand-new empty library
        let snap = sample.join("snapshot.yuyue");
        if snap.is_file() {
            let snap_dir = work.join(format!("snap-{name}"));
            let fresh = YuyueCore::open(snap_dir.to_string_lossy().into_owned(), "SnapCheck".into(), "test".into()).unwrap();
            let info = fresh.read_snapshot(snap.to_string_lossy().into_owned()).unwrap();
            assert!(info.counts.books >= 1, "{name}/snapshot: counts.books");
            fresh.restore_snapshot(snap.to_string_lossy().into_owned(), false).unwrap();
            verify_library(&fresh, &expected, &format!("{name}/snapshot"));
            drop(fresh);
        }
    }

    let _ = fs::remove_dir_all(&work);
}
