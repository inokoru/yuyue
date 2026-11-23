//! A book that carries its own reading (EPUB 3 Media Overlays) brought in as it comes: the
//! recording out of the package onto the shelf, each `<par>` laid on the sentence its fragment
//! points at, and the map made from them — nothing to align.
//!
//! A fragment is found by its element id in the sections as stored (ids survive the import);
//! the sentence is the first one that starts at or after the element. A `<par>` that covers more
//! than one sentence shares its time out among them by length (spread, not exact).

use std::collections::HashMap;
use std::io::Read;
use std::path::{Path, PathBuf};

use regex::Regex;

use super::export::{Par, read_smil};

/// What a package says about its reading: each `<par>` with its document and fragment resolved
/// to archive paths, and the audio files in the order they first come.
pub struct Overlays {
    pub pars: Vec<(String, String, String, f64, f64)>,
    pub audio: Vec<String>,
}

fn dir_of(p: &str) -> &str {
    p.rfind('/').map(|i| &p[..i]).unwrap_or("")
}

/// `href` from a document in `base_dir`, as an archive path (`../audio/a.mp3` from
/// `OEBPS/smil` → `OEBPS/audio/a.mp3`).
fn resolve(base_dir: &str, href: &str) -> String {
    let href = percent_encoding::percent_decode_str(href).decode_utf8_lossy().to_string();
    let mut parts: Vec<&str> = if base_dir.is_empty() { Vec::new() } else { base_dir.split('/').collect() };
    for seg in href.split('/') {
        match seg {
            "" | "." => {}
            ".." => {
                parts.pop();
            }
            s => parts.push(s),
        }
    }
    parts.join("/")
}

fn read_entry(z: &mut zip::ZipArchive<std::fs::File>, name: &str) -> Option<Vec<u8>> {
    let mut f = z.by_name(name).ok()?;
    let mut v = Vec::new();
    f.read_to_end(&mut v).ok()?;
    Some(v)
}

/// The package's overlays, or None when it has none.
pub fn read(epub: &Path) -> Option<Overlays> {
    let mut z = zip::ZipArchive::new(std::fs::File::open(epub).ok()?).ok()?;
    let container = String::from_utf8(read_entry(&mut z, "META-INF/container.xml")?).ok()?;
    let opf_path = Regex::new(r#"full-path="([^"]+)""#).ok()?.captures(&container)?.get(1)?.as_str().to_string();
    let opf = String::from_utf8(read_entry(&mut z, &opf_path)?).ok()?;
    let opf_dir = dir_of(&opf_path).to_string();
    let item = Regex::new(r#"<item\b[^>]*>"#).ok()?;
    let attr = |tag: &str, name: &str| Regex::new(&format!(r#"\b{name}="([^"]*)""#)).ok().and_then(|r| r.captures(tag)).and_then(|c| c.get(1)).map(|m| m.as_str().to_string());
    let mut by_id: HashMap<String, String> = HashMap::new();
    let mut smil_ids: Vec<String> = Vec::new();
    for m in item.find_iter(&opf) {
        let tag = m.as_str();
        let (Some(id), Some(href)) = (attr(tag, "id"), attr(tag, "href")) else { continue };
        by_id.insert(id.clone(), resolve(&opf_dir, &href));
        if let Some(mo) = attr(tag, "media-overlay")
            && !smil_ids.contains(&mo) {
                smil_ids.push(mo);
            }
    }
    if smil_ids.is_empty() {
        return None;
    }
    let mut pars = Vec::new();
    let mut audio: Vec<String> = Vec::new();
    for sid in smil_ids {
        let Some(path) = by_id.get(&sid) else { continue };
        let Some(text) = read_entry(&mut z, path).and_then(|b| String::from_utf8(b).ok()) else { continue };
        let base = dir_of(path).to_string();
        for Par { text_src, audio_src, begin, end } in read_smil(&text) {
            let (doc, frag) = text_src.split_once('#').unwrap_or((text_src.as_str(), ""));
            let a = resolve(&base, &audio_src);
            if !audio.contains(&a) {
                audio.push(a.clone());
            }
            pars.push((resolve(&base, doc), frag.to_string(), a, begin, end));
        }
    }
    (!pars.is_empty()).then_some(Overlays { pars, audio })
}

/// The audio files written out of the package into `dir`, each as (path, seconds — the last
/// time any `<par>` reaches in it).
pub fn extract_audio(epub: &Path, o: &Overlays, dir: &Path) -> Option<Vec<(PathBuf, f64)>> {
    let mut z = zip::ZipArchive::new(std::fs::File::open(epub).ok()?).ok()?;
    std::fs::create_dir_all(dir).ok()?;
    let mut out = Vec::new();
    for (i, a) in o.audio.iter().enumerate() {
        let bytes = read_entry(&mut z, a)?;
        let ext = Path::new(a).extension().and_then(|e| e.to_str()).unwrap_or("mp3");
        let p = dir.join(format!("{i:03}.{ext}"));
        std::fs::write(&p, bytes).ok()?;
        let seconds = o.pars.iter().filter(|x| &x.2 == a).map(|x| x.4).fold(0.0, f64::max);
        out.push((p, seconds));
    }
    Some(out)
}

/// Where an element id is in the stored sections: (section, the first sentence at or after it).
pub fn locate(sections: &[String], id: &str) -> Option<(u32, u32)> {
    let needle = format!(" id=\"{id}\"");
    for (n, html) in sections.iter().enumerate() {
        let Some(at) = html.find(&needle) else { continue };
        // The element's own tag starts before `id=`; a sentence span may be it or come after.
        let tag_start = html[..at].rfind('<').unwrap_or(at);
        let rest = &html[tag_start..];
        let i = rest.find("data-s=\"")?;
        let k: String = rest[i + 8..].chars().take_while(|c| c.is_ascii_digit()).collect();
        return Some((n as u32, k.parse().ok()?));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hrefs_resolve_against_their_document() {
        assert_eq!(resolve("OEBPS/smil", "../audio/a%20b.mp3"), "OEBPS/audio/a b.mp3");
        assert_eq!(resolve("", "text/c1.xhtml"), "text/c1.xhtml");
    }

    #[test]
    fn a_fragment_lands_on_its_sentence() {
        let s = vec![
            "<p><span class=\"s\" data-s=\"0\">甲。</span></p>".to_string(),
            "<h2 id=\"c2\">第二章</h2><p><span class=\"s\" data-s=\"0\">乙。</span><span class=\"s\" id=\"s1\" data-s=\"1\">丙。</span></p>".to_string(),
        ];
        assert_eq!(locate(&s, "s1"), Some((1, 1)));
        assert_eq!(locate(&s, "c2"), Some((1, 0)));
        assert_eq!(locate(&s, "nope"), None);
    }

    #[test]
    fn exported_and_imported_again_the_times_come_back() {
        use crate::api::YuyueCore;
        use crate::audiobook::grade::{Graded, SyncBy};
        use crate::audiobook::map::SyncMap;
        use crate::audiobook::matchbook::MatchKind;
        let dir = std::env::temp_dir().join(format!("yuyue-overlay-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.join("lib").to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/beiying.epub")).unwrap().id;
        let m = core.manifest(&book).unwrap();
        let sentences = core.sentences(book.clone(), 0).unwrap();
        // Two seconds a sentence, the third and fourth sharing a stretch.
        let g: Vec<Graded> = (0..sentences.len() as u32).map(|k| Graded { section: 0, sentence: k, t0: k as f64 * 2.0, t1: k as f64 * 2.0 + 1.8, by: if k == 2 || k == 3 { SyncBy::Spread } else { SyncBy::Auto }, conf: 1.0, no_gap: false }).collect();
        let total = sentences.len() as f64 * 2.0;
        let map = SyncMap::from_graded("rec", &book, MatchKind::Same, vec![total], &g, vec![], vec![], "1");
        let audio = dir.join("a.mp3");
        std::fs::write(&audio, [0u8; 128]).unwrap();
        let html = std::fs::read_to_string(core.book_dir(&book).unwrap().join("s").join("0.html")).unwrap();
        let p = crate::audiobook::export::Package { id: &book, title: "背影（朗读）", author: "朱自清", lang: "zh", sections: vec![(0, "背影".into(), html)], audio: vec![("000.mp3".into(), audio, total)], resources: vec![], map: &map };
        let out = dir.join("out.epub");
        crate::audiobook::export::write_epub(&p, &out).unwrap();
        // In again: a new book (the package differs), its recording laid out from the package.
        let back = core.import_book(out.to_string_lossy().into_owned()).unwrap().id;
        let rec = core.audiobook_import_overlays(back.clone()).unwrap().expect("a recording");
        let rows = core.audiobook_map(rec).unwrap().expect("a map").rows;
        let first = rows.iter().find(|r| r.section == 0 && r.sentence == 0).unwrap();
        let fifth = rows.iter().find(|r| r.sentence == 4).unwrap();
        assert!(first.t0_ms < 20, "{rows:?}");
        assert!((fifth.t0_ms as i64 - 8000).abs() < 20, "{rows:?}");
        assert_eq!(rows.iter().filter(|r| r.by == SyncBy::Auto).count(), sentences.len() - 2, "{rows:?}");
        let _ = m;
        let _ = std::fs::remove_dir_all(&dir);
    }
}
