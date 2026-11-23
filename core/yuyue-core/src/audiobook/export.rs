//! EPUB 3 媒体叠加 (Media Overlays): the standard way an e-book carries its own reading's times.
//! Read in, a book that has them needs no aligning; written out, the map goes to other readers.
//!
//! A `<par>` pairs a text fragment (`chapter.xhtml#s12`) with a clip of an audio file
//! (`clipBegin`/`clipEnd`). Spread sentences are written as one `<par>` for their stretch, not
//! as precise times they don't have.

use super::grade::SyncBy;
use super::map::SyncMap;

/// One `<par>` read from a SMIL file.
#[derive(Clone, Debug, PartialEq)]
pub struct Par {
    pub text_src: String,
    pub audio_src: String,
    pub begin: f64,
    pub end: f64,
}

/// SMIL clock values: `12.5s`, `1500ms`, `0:01:02.5`, `01:02`, `3.2`.
pub fn clock(v: &str) -> Option<f64> {
    let v = v.trim();
    if let Some(ms) = v.strip_suffix("ms") {
        return ms.parse::<f64>().ok().map(|x| x / 1000.0);
    }
    if let Some(s) = v.strip_suffix('s') {
        return s.parse().ok();
    }
    if let Some(m) = v.strip_suffix("min") {
        return m.parse::<f64>().ok().map(|x| x * 60.0);
    }
    if let Some(h) = v.strip_suffix('h') {
        return h.parse::<f64>().ok().map(|x| x * 3600.0);
    }
    let parts: Vec<f64> = v.split(':').map(|p| p.parse::<f64>()).collect::<Result<_, _>>().ok()?;
    Some(parts.iter().fold(0.0, |a, x| a * 60.0 + x))
}

/// The `<par>`s of a SMIL document, in order.
pub fn read_smil(xml: &str) -> Vec<Par> {
    let Ok(doc) = roxmltree::Document::parse(xml) else { return Vec::new() };
    let mut out = Vec::new();
    for par in doc.descendants().filter(|n| n.tag_name().name() == "par") {
        let text = par.children().find(|c| c.tag_name().name() == "text").and_then(|t| t.attribute("src"));
        let audio = par.children().find(|c| c.tag_name().name() == "audio");
        let (Some(text), Some(audio)) = (text, audio) else { continue };
        let Some(src) = audio.attribute("src") else { continue };
        let begin = audio.attribute("clipBegin").and_then(clock).unwrap_or(0.0);
        let end = audio.attribute("clipEnd").and_then(clock).unwrap_or(begin);
        out.push(Par { text_src: text.to_string(), audio_src: src.to_string(), begin, end });
    }
    out
}

fn esc(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('"', "&quot;")
}

fn fmt(t: f64) -> String {
    let ms = (t * 1000.0).round() as u64;
    format!("{}:{:02}:{:02}.{:03}", ms / 3_600_000, ms / 60_000 % 60, ms / 1000 % 60, ms % 1000)
}

/// One SMIL document for a section. `href` names a sentence's fragment (`text/c3.xhtml#s12`);
/// `audio` names the file a time falls in and the time within it.
pub fn write_smil(map: &SyncMap, section: u32, href: &dyn Fn(u32, u32) -> String, audio: &dyn Fn(f64) -> (String, f64)) -> String {
    let mut s = String::from("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<smil xmlns=\"http://www.w3.org/ns/SMIL\" xmlns:epub=\"http://www.idpf.org/2007/ops\" version=\"3.0\">\n<body>\n<seq>\n");
    let rows: Vec<_> = map.rows.iter().filter(|r| r.section == section && matches!(r.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread)).collect();
    let mut i = 0;
    let mut n = 0;
    while i < rows.len() {
        let r = rows[i];
        // A run of spread sentences: one par for the stretch, pointing at its first sentence.
        let mut j = i + 1;
        if r.by == SyncBy::Spread {
            while j < rows.len() && rows[j].by == SyncBy::Spread {
                j += 1;
            }
        }
        let t0 = r.t0_ms as f64 / 1000.0;
        let t1 = rows[j - 1].t1_ms as f64 / 1000.0;
        let (file, a) = audio(t0);
        let (_, b) = audio(t1);
        n += 1;
        s.push_str(&format!("<par id=\"p{n}\"><text src=\"{}\"/><audio src=\"{}\" clipBegin=\"{}\" clipEnd=\"{}\"/></par>\n", esc(&href(r.section, r.sentence)), esc(&file), fmt(a), fmt(b)));
        i = j;
    }
    s.push_str("</seq>\n</body>\n</smil>\n");
    s
}

/// A section's stored HTML as XHTML for the package: each sentence's span gets `id="s<n>"` (the
/// fragment its `<par>` points at), empty elements are closed, named entities become numbers.
pub fn section_xhtml(html: &str, title: &str, lang: &str) -> String {
    let mut body = html.replace("class=\"s\" data-s=\"", "class=\"s\" id=\"s\" data-s=\"");
    // id="s" data-s="12" → id="s12" data-s="12"
    let mut out = String::with_capacity(body.len() + 64);
    let mut rest = body.as_str();
    while let Some(i) = rest.find("id=\"s\" data-s=\"") {
        out.push_str(&rest[..i]);
        let after = &rest[i + "id=\"s\" data-s=\"".len()..];
        let n: String = after.chars().take_while(|c| c.is_ascii_digit()).collect();
        out.push_str(&format!("id=\"s{n}\" data-s=\""));
        rest = after;
    }
    out.push_str(rest);
    body = out;
    for tag in ["br", "hr", "img", "wbr"] {
        let mut fixed = String::with_capacity(body.len());
        let mut rest = body.as_str();
        let open = format!("<{tag}");
        while let Some(i) = rest.find(&open) {
            fixed.push_str(&rest[..i]);
            let tail = &rest[i..];
            let end = tail.find('>').map(|e| e + 1).unwrap_or(tail.len());
            let el = &tail[..end];
            if el.ends_with("/>") || !el.ends_with('>') {
                fixed.push_str(el);
            } else {
                fixed.push_str(&el[..el.len() - 1]);
                fixed.push_str("/>");
            }
            rest = &tail[end..];
        }
        fixed.push_str(rest);
        body = fixed;
    }
    for (name, num) in [("&nbsp;", "&#160;"), ("&mdash;", "&#8212;"), ("&ndash;", "&#8211;"), ("&hellip;", "&#8230;"), ("&lsquo;", "&#8216;"), ("&rsquo;", "&#8217;"), ("&ldquo;", "&#8220;"), ("&rdquo;", "&#8221;"), ("&copy;", "&#169;")] {
        body = body.replace(name, num);
    }
    format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE html>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\" xml:lang=\"{l}\" lang=\"{l}\">\n<head><meta charset=\"UTF-8\"/><title>{t}</title><style>.-epub-media-overlay-active{{background:rgba(192,83,58,.18)}}</style></head>\n<body>\n{body}\n</body>\n</html>\n",
        l = esc(lang),
        t = esc(title)
    )
}

/// What goes into an EPUB 3 with the reading's times: the book's sections (title, stored
/// HTML), the recording's files (name in the package, path on disk, seconds), the map.
pub struct Package<'a> {
    pub id: &'a str,
    pub title: &'a str,
    pub author: &'a str,
    pub lang: &'a str,
    pub sections: Vec<(u32, String, String)>,
    pub audio: Vec<(String, std::path::PathBuf, f64)>,
    pub resources: Vec<(String, std::path::PathBuf)>,
    pub map: &'a SyncMap,
}

fn media_type(name: &str) -> &'static str {
    match name.rsplit('.').next().map(|x| x.to_ascii_lowercase()).as_deref() {
        Some("mp3") => "audio/mpeg",
        Some("m4a" | "m4b" | "mp4" | "aac") => "audio/mp4",
        Some("ogg" | "opus") => "audio/ogg",
        Some("wav") => "audio/wav",
        Some("flac") => "audio/flac",
        Some("jpg" | "jpeg") => "image/jpeg",
        Some("png") => "image/png",
        Some("gif") => "image/gif",
        Some("svg") => "image/svg+xml",
        Some("webp") => "image/webp",
        Some("css") => "text/css",
        _ => "application/octet-stream",
    }
}

/// Writes the package to `out` (zip: mimetype first and stored, as EPUB wants).
pub fn write_epub(p: &Package, out: &std::path::Path) -> std::io::Result<()> {
    use std::io::Write;
    use zip::write::SimpleFileOptions;
    let file = std::fs::File::create(out)?;
    let mut z = zip::ZipWriter::new(file);
    let stored = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Stored);
    let deflated = SimpleFileOptions::default().compression_method(zip::CompressionMethod::Deflated);
    let io = |e: zip::result::ZipError| std::io::Error::other(e.to_string());
    z.start_file("mimetype", stored).map_err(io)?;
    z.write_all(b"application/epub+zip")?;
    z.start_file("META-INF/container.xml", deflated).map_err(io)?;
    z.write_all(b"<?xml version=\"1.0\"?>\n<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>\n")?;
    // Whole-recording time → (file in the package, seconds into it).
    let starts: Vec<f64> = p.audio.iter().scan(0.0, |at, a| { let s = *at; *at += a.2; Some(s) }).collect();
    let audio_at = |t: f64| -> (String, f64) {
        let i = starts.iter().rposition(|s| *s <= t).unwrap_or(0);
        (format!("../audio/{}", p.audio.get(i).map(|a| a.0.as_str()).unwrap_or("")), t - starts.get(i).copied().unwrap_or(0.0))
    };
    let mut manifest = String::new();
    let mut spine = String::new();
    let mut nav = String::new();
    let mut durations = String::new();
    let mut total = 0.0;
    for (sec, title, html) in &p.sections {
        let has = p.map.rows.iter().any(|r| r.section == *sec && matches!(r.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread));
        z.start_file(format!("OEBPS/text/c{sec}.xhtml"), deflated).map_err(io)?;
        z.write_all(section_xhtml(html, title, p.lang).as_bytes())?;
        if has {
            let smil = write_smil(p.map, *sec, &|s, n| format!("../text/c{s}.xhtml#s{n}"), &audio_at);
            let d: f64 = read_smil(&smil).iter().map(|x| (x.end - x.begin).max(0.0)).sum();
            total += d;
            z.start_file(format!("OEBPS/smil/c{sec}.smil"), deflated).map_err(io)?;
            z.write_all(smil.as_bytes())?;
            manifest.push_str(&format!("<item id=\"c{sec}\" href=\"text/c{sec}.xhtml\" media-type=\"application/xhtml+xml\" media-overlay=\"m{sec}\"/>\n<item id=\"m{sec}\" href=\"smil/c{sec}.smil\" media-type=\"application/smil+xml\"/>\n"));
            durations.push_str(&format!("<meta property=\"media:duration\" refines=\"#m{sec}\">{}</meta>\n", fmt(d)));
        } else {
            manifest.push_str(&format!("<item id=\"c{sec}\" href=\"text/c{sec}.xhtml\" media-type=\"application/xhtml+xml\"/>\n"));
        }
        spine.push_str(&format!("<itemref idref=\"c{sec}\"/>\n"));
        if !title.is_empty() {
            nav.push_str(&format!("<li><a href=\"text/c{sec}.xhtml\">{}</a></li>\n", esc(title)));
        }
    }
    for (k, (name, path, _)) in p.audio.iter().enumerate() {
        z.start_file(format!("OEBPS/audio/{name}"), stored).map_err(io)?;
        std::io::copy(&mut std::fs::File::open(path)?, &mut z)?;
        manifest.push_str(&format!("<item id=\"a{k}\" href=\"audio/{}\" media-type=\"{}\"/>\n", esc(name), media_type(name)));
    }
    for (k, (name, path)) in p.resources.iter().enumerate() {
        let Ok(mut f) = std::fs::File::open(path) else { continue };
        z.start_file(format!("OEBPS/r/{name}"), deflated).map_err(io)?;
        std::io::copy(&mut f, &mut z)?;
        manifest.push_str(&format!("<item id=\"r{k}\" href=\"r/{}\" media-type=\"{}\"/>\n", esc(name), media_type(name)));
    }
    z.start_file("OEBPS/nav.xhtml", deflated).map_err(io)?;
    z.write_all(format!("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>{t}</title></head><body><nav epub:type=\"toc\"><ol>\n{nav}</ol></nav></body></html>\n", t = esc(p.title)).as_bytes())?;
    z.start_file("OEBPS/content.opf", deflated).map_err(io)?;
    z.write_all(format!(
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\" xml:lang=\"{l}\">\n<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">\n<dc:identifier id=\"uid\">urn:yuyue:{id}</dc:identifier>\n<dc:title>{t}</dc:title>\n<dc:creator>{a}</dc:creator>\n<dc:language>{l}</dc:language>\n<meta property=\"dcterms:modified\">2000-01-01T00:00:00Z</meta>\n<meta property=\"media:active-class\">-epub-media-overlay-active</meta>\n<meta property=\"media:duration\">{d}</meta>\n{durations}</metadata>\n<manifest>\n<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>\n{manifest}</manifest>\n<spine>\n{spine}</spine>\n</package>\n",
        id = esc(p.id), t = esc(p.title), a = esc(p.author), l = esc(p.lang), d = fmt(total)
    ).as_bytes())?;
    z.finish().map_err(io)?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audiobook::grade::Graded;
    use crate::audiobook::matchbook::MatchKind;

    #[test]
    fn clocks() {
        assert_eq!(clock("12.5s"), Some(12.5));
        assert_eq!(clock("1500ms"), Some(1.5));
        assert_eq!(clock("0:01:02.5"), Some(62.5));
        assert_eq!(clock("01:02"), Some(62.0));
    }

    #[test]
    fn written_and_read_back() {
        let g = vec![
            Graded { section: 0, sentence: 0, t0: 0.0, t1: 2.0, by: SyncBy::Auto, conf: 1.0, no_gap: false },
            Graded { section: 0, sentence: 1, t0: 2.0, t1: 3.0, by: SyncBy::Spread, conf: 0.5, no_gap: false },
            Graded { section: 0, sentence: 2, t0: 3.0, t1: 4.5, by: SyncBy::Spread, conf: 0.5, no_gap: false },
            Graded { section: 0, sentence: 3, t0: 4.5, t1: 6.0, by: SyncBy::Auto, conf: 1.0, no_gap: false },
        ];
        let m = SyncMap::from_graded("a", "b", MatchKind::Same, vec![6.0], &g, vec![], vec![], "1");
        let x = write_smil(&m, 0, &|_, s| format!("c0.xhtml#s{s}"), &|t| ("a.mp3".into(), t));
        let p = read_smil(&x);
        assert_eq!(p.len(), 3);
        assert_eq!(p[1].text_src, "c0.xhtml#s1");
        assert_eq!((p[1].begin, p[1].end), (2.0, 4.5));
    }

    #[test]
    fn sections_become_xhtml_with_fragments() {
        let x = section_xhtml("<p><span class=\"s\" data-s=\"0\">甲&nbsp;乙</span><br><img src=\"../r/a.png\" alt=\"\"><span class=\"s\" data-s=\"12\">丙</span></p>", "第一章", "zh");
        assert!(x.contains("id=\"s0\" data-s=\"0\"") && x.contains("id=\"s12\" data-s=\"12\""), "{x}");
        assert!(x.contains("<br/>") && x.contains("alt=\"\"/>") && x.contains("&#160;"), "{x}");
    }
}
