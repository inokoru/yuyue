//! An M4B's (or M4A's) chapter marks, read from the file itself, for platforms whose player
//! doesn't give them (Android). Two places hold them:
//!
//! - `moov/udta/chpl`, Nero's list: each mark's start (100 ns units) and title;
//! - a text track the audio track points to through `tref/chap` (QuickTime, iTunes, Audible):
//!   each sample is a chapter, its title in the sample's text, its start from the track's
//!   sample times.
//!
//! Only the boxes on the way are read; the audio isn't.

use std::fs::{File, OpenOptions};
use std::io::{Read, Seek, SeekFrom, Write};
use std::path::Path;

use crate::audio::AudioMark;
use crate::error::Result;

/// The chapter marks in the file, in seconds from its start; empty when it has none.
pub fn chapter_marks(path: &Path) -> Result<Vec<AudioMark>> {
    let mut f = File::open(path)?;
    let len = f.metadata()?.len();
    let Some(moov) = find(&mut f, 0, len, b"moov")? else { return Ok(Vec::new()) };
    let moov = read_body(&mut f, moov)?;
    let mut marks = quicktime(&mut f, &moov)?;
    if marks.is_empty() {
        marks = nero(&moov);
    }
    marks.sort_by(|a, b| a.start.total_cmp(&b.start));
    Ok(marks)
}

#[derive(Clone, Copy)]
struct Span {
    /// Where the body starts and how long it is.
    at: u64,
    len: u64,
}

/// The first box called `name` among the boxes in `[at, at + len)` of the file.
fn find(f: &mut File, at: u64, len: u64, name: &[u8; 4]) -> Result<Option<Span>> {
    let Some(end) = at.checked_add(len) else { return Ok(None) };
    let mut pos = at;
    while pos.checked_add(8).is_some_and(|e| e <= end) {
        f.seek(SeekFrom::Start(pos))?;
        let mut head = [0u8; 8];
        f.read_exact(&mut head)?;
        let mut size = u32::from_be_bytes(head[..4].try_into().unwrap()) as u64;
        let mut header = 8u64;
        if size == 1 {
            if pos.checked_add(16).is_none_or(|e| e > end) {
                break;
            }
            let mut big = [0u8; 8];
            f.read_exact(&mut big)?;
            size = u64::from_be_bytes(big);
            header = 16;
        } else if size == 0 {
            size = end - pos;
        }
        let Some(next) = pos.checked_add(size) else { break };
        if size < header || next > end {
            break;
        }
        if &head[4..] == name {
            return Ok(Some(Span { at: pos + header, len: size - header }));
        }
        pos = next;
    }
    Ok(None)
}

/// `moov` is small (the index, not the audio): read it whole. Capped so a broken size can't
/// ask for gigabytes.
fn read_body(f: &mut File, s: Span) -> Result<Vec<u8>> {
    let mut buf = vec![0; s.len.min(64 << 20) as usize];
    f.seek(SeekFrom::Start(s.at))?;
    f.read_exact(&mut buf)?;
    Ok(buf)
}

/// The boxes directly inside `data`, as (name, body).
fn boxes(data: &[u8]) -> Vec<([u8; 4], &[u8])> {
    let mut out = Vec::new();
    let mut pos = 0usize;
    while pos.checked_add(8).is_some_and(|e| e <= data.len()) {
        let mut size = u32::from_be_bytes(data[pos..pos + 4].try_into().unwrap()) as usize;
        let name: [u8; 4] = data[pos + 4..pos + 8].try_into().unwrap();
        let mut header = 8usize;
        if size == 1 {
            if pos.checked_add(16).is_none_or(|e| e > data.len()) {
                break;
            }
            let Ok(big) = usize::try_from(u64::from_be_bytes(data[pos + 8..pos + 16].try_into().unwrap())) else { break };
            size = big;
            header = 16;
        } else if size == 0 {
            size = data.len() - pos;
        }
        let Some(next) = pos.checked_add(size) else { break };
        if size < header || next > data.len() {
            break;
        }
        out.push((name, &data[pos + header..next]));
        pos = next;
    }
    out
}

fn child<'a>(data: &'a [u8], name: &[u8; 4]) -> Option<&'a [u8]> {
    boxes(data).into_iter().find(|(n, _)| n == name).map(|(_, b)| b)
}

fn path<'a>(data: &'a [u8], names: &[&[u8; 4]]) -> Option<&'a [u8]> {
    names.iter().try_fold(data, |d, n| child(d, n))
}

fn be32(d: &[u8], at: usize) -> Option<u32> {
    d.get(at..at + 4).map(|b| u32::from_be_bytes(b.try_into().unwrap()))
}

fn be64(d: &[u8], at: usize) -> Option<u64> {
    d.get(at..at + 8).map(|b| u64::from_be_bytes(b.try_into().unwrap()))
}

/// Book metadata written into an exported M4B's `udta/meta/ilst`.
#[derive(Clone, Debug, Default)]
pub struct Meta {
    pub title: Option<String>,
    pub author: Option<String>,
    pub cover: Option<Vec<u8>>,
}

/// Writes chapter marks into an M4A/M4B as Nero's `chpl` (what `chapter_marks` reads, and most
/// players). The file's `moov` must be its last box, as a writer that finishes the file leaves
/// it: growing it then moves no audio. Marks already there are replaced.
pub fn add_chapters(path: &Path, marks: &[AudioMark]) -> Result<()> {
    add_chapters_and_meta(path, marks, None)
}

/// Writes chapter marks (`chpl` + QuickTime `chap`) and optional iTunes metadata (`©nam`, `©alb`,
/// `©ART`, `covr`) into an M4A/M4B. Rewrites only from `moov` onward so large audio `mdat` boxes
/// are never loaded into memory.
pub fn add_chapters_and_meta(path: &Path, marks: &[AudioMark], meta: Option<&Meta>) -> Result<()> {
    let mut f = OpenOptions::new().read(true).write(true).open(path)?;
    let len = f.metadata()?.len();
    // Scan top-level boxes via seek to locate moov and verify it's last (handling 64-bit size == 1).
    let mut at = 0u64;
    let mut moov: Option<(u64, u64, u64)> = None;
    let mut after_moov: Vec<[u8; 4]> = Vec::new();
    while at.checked_add(8).is_some_and(|e| e <= len) {
        f.seek(SeekFrom::Start(at))?;
        let mut head = [0u8; 8];
        f.read_exact(&mut head)?;
        let mut size = u32::from_be_bytes(head[..4].try_into().unwrap()) as u64;
        let mut header = 8u64;
        if size == 1 {
            if at.checked_add(16).is_none_or(|e| e > len) {
                break;
            }
            let mut big = [0u8; 8];
            f.read_exact(&mut big)?;
            size = u64::from_be_bytes(big);
            header = 16;
        } else if size == 0 {
            size = len - at;
        }
        let Some(next) = at.checked_add(size) else { break };
        if size < header || next > len {
            break;
        }
        if &head[4..8] == b"moov" {
            moov = Some((at, header, size));
            after_moov.clear();
        } else if moov.is_some() {
            after_moov.push([head[4], head[5], head[6], head[7]]);
        }
        at = next;
    }
    let Some((m, m_header, size)) = moov else { return Err(crate::error::CoreError::Invalid { detail: "no moov".into() }) };
    // Only the chapter titles a previous run put after moov may follow it: they're written anew.
    let ours = after_moov == [*b"mdat"];
    if m.checked_add(size) != Some(len) && !ours {
        return Err(crate::error::CoreError::Invalid { detail: "moov isn't last".into() });
    }
    if size - m_header > 64 << 20 {
        return Err(crate::error::CoreError::Invalid { detail: "moov too large".into() });
    }
    let wrap = |name: &[u8; 4], body: &[u8]| -> Vec<u8> {
        let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        v.extend_from_slice(name);
        v.extend_from_slice(body);
        v
    };
    let mut chpl = vec![1u8, 0, 0, 0, 0, marks.len().min(255) as u8];
    for mk in marks.iter().take(255) {
        chpl.extend_from_slice(&((mk.start.max(0.0) * 1e7) as u64).to_be_bytes());
        let title = mk.title.clone().unwrap_or_default();
        let mut t = title.as_bytes();
        while t.len() > 255 {
            // Cut on a character boundary.
            let mut n = 255;
            while !title.is_char_boundary(n) {
                n -= 1;
            }
            t = &t[..n];
        }
        chpl.push(t.len() as u8);
        chpl.extend_from_slice(t);
    }
    let chpl = wrap(b"chpl", &chpl);
    let meta_box = meta.and_then(itunes_meta);
    // moov's children, udta rebuilt with the new chpl (and meta, if given) in place of any old one.
    let body = read_body(&mut f, Span { at: m + m_header, len: size - m_header })?;
    let mut out_body = Vec::new();
    let mut had_udta = false;
    for (name, child_body) in boxes(&body) {
        if &name == b"udta" {
            had_udta = true;
            let mut u = Vec::new();
            for (n2, b2) in boxes(child_body) {
                if &n2 != b"chpl" && !(&n2 == b"meta" && meta_box.is_some()) {
                    u.extend(wrap(&n2, b2));
                }
            }
            u.extend_from_slice(&chpl);
            if let Some(mb) = &meta_box {
                u.extend_from_slice(mb);
            }
            out_body.extend(wrap(b"udta", &u));
        } else {
            out_body.extend(wrap(&name, child_body));
        }
    }
    if !had_udta {
        let mut u = chpl;
        if let Some(mb) = &meta_box {
            u.extend_from_slice(mb);
        }
        out_body.extend(wrap(b"udta", &u));
    }
    // And a QuickTime chapter track, which Apple's players read (they don't read chpl): a text
    // track of one sample a chapter, the audio track pointing at it through tref/chap, the titles
    // in a small mdat after moov.
    let use_co64 = m.saturating_add(out_body.len() as u64).saturating_add(1 << 20) > u32::MAX as u64;
    let (moov_body, texts) = with_chapter_track(&out_body, marks, use_co64);
    let mut moov = wrap(b"moov", &moov_body);
    let text_at = m.checked_add(moov.len() as u64).and_then(|x| x.checked_add(8)).ok_or_else(|| crate::error::CoreError::Invalid { detail: "m4b offset overflow".into() })?;
    // The chunk offset was written as a placeholder at the end of text_trak's stco/co64; patch the last match.
    if use_co64 {
        if let Some(i) = find_bytes_last(&moov, b"YYCH64!!") {
            moov[i..i + 8].copy_from_slice(&text_at.to_be_bytes());
        }
    } else if let Some(i) = find_bytes_last(&moov, b"YYCH") {
        moov[i..i + 4].copy_from_slice(&(text_at as u32).to_be_bytes());
    }
    let mdat = wrap(b"mdat", &texts);
    f.seek(SeekFrom::Start(m))?;
    f.write_all(&moov)?;
    f.write_all(&mdat)?;
    f.set_len(m + moov.len() as u64 + mdat.len() as u64)?;
    f.flush()?;
    Ok(())
}

fn itunes_meta(meta: &Meta) -> Option<Vec<u8>> {
    let wrap = |name: &[u8; 4], body: &[u8]| -> Vec<u8> {
        let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        v.extend_from_slice(name);
        v.extend_from_slice(body);
        v
    };
    let item = |tag: &[u8; 4], kind: u32, payload: &[u8]| -> Vec<u8> {
        let mut d = kind.to_be_bytes().to_vec();
        d.extend_from_slice(&[0, 0, 0, 0]);
        d.extend_from_slice(payload);
        wrap(tag, &wrap(b"data", &d))
    };
    let mut ilst = Vec::new();
    if let Some(t) = meta.title.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
        ilst.extend(item(b"\xa9nam", 1, t.as_bytes()));
        ilst.extend(item(b"\xa9alb", 1, t.as_bytes()));
    }
    if let Some(a) = meta.author.as_deref().map(str::trim).filter(|s| !s.is_empty()) {
        ilst.extend(item(b"\xa9ART", 1, a.as_bytes()));
    }
    if let Some(c) = meta.cover.as_deref().filter(|b| !b.is_empty()) {
        let kind = if c.starts_with(&[0x89, b'P', b'N', b'G']) { 14 } else { 13 };
        ilst.extend(item(b"covr", kind, c));
    }
    if ilst.is_empty() {
        return None;
    }
    let mut hdlr = vec![0u8; 8];
    hdlr.extend_from_slice(b"mdirappl");
    hdlr.extend_from_slice(&[0u8; 9]);
    let mut body = vec![0u8; 4]; // FullBox version + flags
    body.extend(wrap(b"hdlr", &hdlr));
    body.extend(wrap(b"ilst", &ilst));
    Some(wrap(b"meta", &body))
}

fn find_bytes_last(h: &[u8], n: &[u8]) -> Option<usize> {
    h.windows(n.len()).rposition(|w| w == n)
}

/// moov's children with a chapter text track added (any earlier one, and the audio track's old
/// tref, taken out), and the samples' bytes for the mdat after moov. The chunk offset is the
/// placeholder `YYCH`.
fn with_chapter_track(moov: &[u8], marks: &[AudioMark], use_co64: bool) -> (Vec<u8>, Vec<u8>) {
    let wrap = |name: &[u8; 4], body: &[u8]| -> Vec<u8> {
        let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        v.extend_from_slice(name);
        v.extend_from_slice(body);
        v
    };
    let handler = |trak: &[u8]| path(trak, &[b"mdia", b"hdlr"]).and_then(|h| h.get(8..12)).map(|x| x.to_vec());
    // The movie's clock and the next free track id.
    let mvhd = child(moov, b"mvhd").unwrap_or(&[]);
    let v1 = mvhd.first() == Some(&1);
    let (scale, duration) = if v1 { (be32(mvhd, 20).unwrap_or(1000), be64(mvhd, 24).unwrap_or(0)) } else { (be32(mvhd, 12).unwrap_or(1000), be32(mvhd, 16).unwrap_or(0) as u64) };
    let next_at = if v1 { 108 } else { 96 };
    let id = be32(mvhd, next_at).unwrap_or(2).max(2);
    let total_ms = duration * 1000 / scale.max(1) as u64;
    // The samples: a length, the title, and encd saying it's UTF-8.
    let mut texts = Vec::new();
    let mut sizes = Vec::new();
    let mut deltas = Vec::new();
    for (i, mk) in marks.iter().enumerate() {
        let t = mk.title.clone().unwrap_or_default();
        let b = t.as_bytes();
        let mut sample = (b.len() as u16).to_be_bytes().to_vec();
        sample.extend_from_slice(b);
        sample.extend_from_slice(&[0, 0, 0, 12, b'e', b'n', b'c', b'd', 0, 0, 1, 0]);
        sizes.push(sample.len() as u32);
        texts.extend(sample);
        let start = (mk.start.max(0.0) * 1000.0) as u64;
        let end = marks.get(i + 1).map(|n| (n.start.max(0.0) * 1000.0) as u64).unwrap_or(total_ms.max(start + 1));
        deltas.push(end.saturating_sub(start).max(1) as u32);
    }
    let ms: u64 = deltas.iter().map(|d| *d as u64).sum();
    let u32s = |v: &[u32]| v.iter().flat_map(|x| x.to_be_bytes()).collect::<Vec<u8>>();
    // stbl
    let mut tx3g = vec![0u8; 6];
    tx3g.extend_from_slice(&1u16.to_be_bytes()); // data reference
    tx3g.extend_from_slice(&[0, 0, 0, 0, 0, 0, 0, 0, 0, 0]); // flags, justification, background
    tx3g.extend_from_slice(&[0; 8]); // text box
    tx3g.extend_from_slice(&[0, 0, 0, 0, 0, 1, 0, 12, 0, 0, 0, 255]); // style: font 1, size 12, black
    tx3g.extend(wrap(b"ftab", &[0, 1, 0, 1, 4, b'S', b'a', b'n', b's']));
    let mut stsd = vec![0, 0, 0, 0, 0, 0, 0, 1];
    stsd.extend(wrap(b"tx3g", &tx3g));
    let mut stts = vec![0, 0, 0, 0];
    stts.extend_from_slice(&(deltas.len() as u32).to_be_bytes());
    for d in &deltas {
        stts.extend_from_slice(&1u32.to_be_bytes());
        stts.extend_from_slice(&d.to_be_bytes());
    }
    let mut stsz = vec![0, 0, 0, 0, 0, 0, 0, 0];
    stsz.extend_from_slice(&(sizes.len() as u32).to_be_bytes());
    stsz.extend(u32s(&sizes));
    let mut stsc = vec![0, 0, 0, 0, 0, 0, 0, 1];
    stsc.extend(u32s(&[1, sizes.len() as u32, 1]));
    let chunk_offsets = if use_co64 {
        let mut co64 = vec![0, 0, 0, 0, 0, 0, 0, 1];
        co64.extend_from_slice(b"YYCH64!!");
        wrap(b"co64", &co64)
    } else {
        let mut stco = vec![0, 0, 0, 0, 0, 0, 0, 1];
        stco.extend_from_slice(b"YYCH");
        wrap(b"stco", &stco)
    };
    let stbl = [wrap(b"stsd", &stsd), wrap(b"stts", &stts), wrap(b"stsz", &stsz), wrap(b"stsc", &stsc), chunk_offsets].concat();
    let mut dref = vec![0, 0, 0, 0, 0, 0, 0, 1];
    dref.extend(wrap(b"url ", &[0, 0, 0, 1]));
    let minf = [wrap(b"nmhd", &[0; 4]), wrap(b"dinf", &wrap(b"dref", &dref)), wrap(b"stbl", &stbl)].concat();
    let mut mdhd = vec![0u8; 12];
    mdhd.extend(u32s(&[1000, ms as u32]));
    mdhd.extend_from_slice(&[0x55, 0xC4, 0, 0]);
    let mut hdlr = vec![0u8; 8];
    hdlr.extend_from_slice(b"text");
    hdlr.extend_from_slice(&[0; 12]);
    hdlr.extend_from_slice(b"Chapters\0");
    let mdia = [wrap(b"mdhd", &mdhd), wrap(b"hdlr", &hdlr), wrap(b"minf", &minf)].concat();
    let mut tkhd = vec![0u8; 12]; // version 0, flags 0 (not played), times
    tkhd.extend(u32s(&[id, 0, (ms * scale as u64 / 1000) as u32]));
    tkhd.extend_from_slice(&[0; 16]); // reserved, layer, group, volume, reserved
    tkhd.extend(u32s(&[0x10000, 0, 0, 0, 0x10000, 0, 0, 0, 0x4000_0000])); // matrix
    tkhd.extend(u32s(&[0, 0]));
    let text_trak = wrap(b"trak", &[wrap(b"tkhd", &tkhd), wrap(b"mdia", &mdia)].concat());
    // moov again: mvhd's next id moved on, old chapter tracks out, the sound pointing at the new.
    let mut body = Vec::new();
    for (name, b) in boxes(moov) {
        match &name {
            b"mvhd" => {
                let mut mv = b.to_vec();
                if mv.len() >= next_at + 4 {
                    mv[next_at..next_at + 4].copy_from_slice(&(id + 1).to_be_bytes());
                }
                body.extend(wrap(b"mvhd", &mv));
            }
            b"trak" if handler(b).as_deref() == Some(b"text") => {}
            b"trak" if handler(b).as_deref() == Some(b"soun") => {
                let mut t = Vec::new();
                for (n2, b2) in boxes(b) {
                    if &n2 == b"tref" {
                        continue;
                    }
                    t.extend(wrap(&n2, b2));
                    if &n2 == b"tkhd" {
                        t.extend(wrap(b"tref", &wrap(b"chap", &id.to_be_bytes())));
                    }
                }
                body.extend(wrap(b"trak", &t));
            }
            _ => body.extend(wrap(&name, b)),
        }
    }
    body.extend(text_trak);
    (body, texts)
}

/// Nero's `chpl`: version, flags, (a reserved byte in version 1), a count, then each mark's
/// start in 100 ns and a length-prefixed title.
fn nero(moov: &[u8]) -> Vec<AudioMark> {
    let Some(c) = boxes(moov).into_iter().filter(|(n, _)| n == b"udta").find_map(|(_, u)| child(u, b"chpl")) else { return Vec::new() };
    let mut at = if c.first() == Some(&1) { 5 } else { 4 };
    // Some writers put the count in four bytes, some in one.
    let count = if c.first() == Some(&1) { c.get(at).copied().unwrap_or(0) as usize } else { be32(c, at).unwrap_or(0) as usize };
    at += if c.first() == Some(&1) { 1 } else { 4 };
    let mut out = Vec::new();
    for _ in 0..count {
        let (Some(start), Some(&n)) = (be64(c, at), c.get(at + 8)) else { break };
        let Some(title) = c.get(at + 9..at + 9 + n as usize) else { break };
        out.push(AudioMark { title: text(title), start: start as f64 / 1e7 });
        at += 9 + n as usize;
    }
    out
}

/// The chapter track the audio points to: its sample times say where each chapter starts, its
/// samples (in the file, outside `moov`) carry the titles.
fn quicktime(f: &mut File, moov: &[u8]) -> Result<Vec<AudioMark>> {
    let traks: Vec<&[u8]> = boxes(moov).into_iter().filter(|(n, _)| n == b"trak").map(|(_, b)| b).collect();
    let id = |t: &[u8]| {
        let h = child(t, b"tkhd")?;
        be32(h, if h.first() == Some(&1) { 20 } else { 12 })
    };
    let Some(chap) = traks.iter().find_map(|t| path(t, &[b"tref", b"chap"]).and_then(|c| be32(c, 0))) else { return Ok(Vec::new()) };
    let Some(track) = traks.iter().find(|t| id(t) == Some(chap)) else { return Ok(Vec::new()) };
    let Some(mdia) = child(track, b"mdia") else { return Ok(Vec::new()) };
    let Some(mdhd) = child(mdia, b"mdhd") else { return Ok(Vec::new()) };
    let scale = be32(mdhd, if mdhd.first() == Some(&1) { 20 } else { 12 }).unwrap_or(0);
    let Some(stbl) = path(mdia, &[b"minf", b"stbl"]) else { return Ok(Vec::new()) };
    if scale == 0 {
        return Ok(Vec::new());
    }
    // Each sample's start, from the run lengths of durations.
    let mut starts = Vec::new();
    if let Some(stts) = child(stbl, b"stts") {
        let mut t = 0u64;
        for i in 0..be32(stts, 4).unwrap_or(0) as usize {
            let (Some(n), Some(d)) = (be32(stts, 8 + i * 8), be32(stts, 12 + i * 8)) else { break };
            for _ in 0..n.min(10_000) {
                starts.push(t);
                t += d as u64;
            }
        }
    }
    // Each sample's place in the file: chunks from stco/co64, samples per chunk from stsc,
    // sizes from stsz.
    let chunks: Vec<u64> = if let Some(c) = child(stbl, b"stco") {
        (0..be32(c, 4).unwrap_or(0) as usize).filter_map(|i| be32(c, 8 + i * 4).map(u64::from)).collect()
    } else if let Some(c) = child(stbl, b"co64") {
        (0..be32(c, 4).unwrap_or(0) as usize).filter_map(|i| be64(c, 8 + i * 8)).collect()
    } else {
        Vec::new()
    };
    let sizes: Vec<u32> = match child(stbl, b"stsz") {
        Some(s) => {
            let fixed = be32(s, 4).unwrap_or(0);
            let n = be32(s, 8).unwrap_or(0) as usize;
            if fixed > 0 { vec![fixed; n] } else { (0..n).filter_map(|i| be32(s, 12 + i * 4)).collect() }
        }
        None => Vec::new(),
    };
    let runs: Vec<(u32, u32)> = child(stbl, b"stsc")
        .map(|s| (0..be32(s, 4).unwrap_or(0) as usize).filter_map(|i| Some((be32(s, 8 + i * 12)?, be32(s, 12 + i * 12)?))).collect())
        .unwrap_or_default();
    let mut offsets = Vec::new();
    for (ci, &chunk) in chunks.iter().enumerate() {
        let per = runs.iter().rev().find(|(first, _)| *first as usize <= ci + 1).map(|r| r.1).unwrap_or(1);
        let mut at = chunk;
        for _ in 0..per {
            let Some(&size) = sizes.get(offsets.len()) else { break };
            offsets.push((at, size));
            at += size as u64;
        }
    }
    let mut out = Vec::new();
    for (i, &t) in starts.iter().enumerate() {
        // A text sample: two bytes of length, then the title.
        let title = offsets.get(i).and_then(|&(at, size)| {
            let mut buf = vec![0; (size as usize).min(1024)];
            f.seek(SeekFrom::Start(at)).ok()?;
            f.read_exact(&mut buf).ok()?;
            let n = u16::from_be_bytes(buf.get(..2)?.try_into().ok()?) as usize;
            text(buf.get(2..2 + n)?)
        });
        out.push(AudioMark { title, start: t as f64 / scale as f64 });
    }
    Ok(out)
}

/// UTF-8, or UTF-16 with a byte-order mark.
fn text(b: &[u8]) -> Option<String> {
    let s = if b.starts_with(&[0xFE, 0xFF]) || b.starts_with(&[0xFF, 0xFE]) {
        let be = b[0] == 0xFE;
        let units: Vec<u16> = b[2..].as_chunks::<2>().0.iter().map(|c| if be { u16::from_be_bytes([c[0], c[1]]) } else { u16::from_le_bytes([c[0], c[1]]) }).collect();
        String::from_utf16_lossy(&units)
    } else {
        String::from_utf8_lossy(b).into_owned()
    };
    let s = s.trim_matches(|c: char| c == '\0' || c.is_whitespace()).to_string();
    (!s.is_empty()).then_some(s)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bx(name: &[u8; 4], body: &[u8]) -> Vec<u8> {
        let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        v.extend_from_slice(name);
        v.extend_from_slice(body);
        v
    }

    fn temp(bytes: &[u8]) -> std::path::PathBuf {
        let p = std::env::temp_dir().join(format!("yuyue-m4b-{}.m4b", getrandom::u64().unwrap()));
        std::fs::write(&p, bytes).unwrap();
        p
    }

    #[test]
    fn nero_marks() {
        let mut chpl = vec![1, 0, 0, 0, 0, 2];
        for (t, name) in [(0u64, "序"), (125_000_000u64, "第一章")] {
            chpl.extend_from_slice(&t.to_be_bytes());
            chpl.push(name.len() as u8);
            chpl.extend_from_slice(name.as_bytes());
        }
        let file = [bx(b"ftyp", b"M4B \0\0\0\0"), bx(b"moov", &bx(b"udta", &bx(b"chpl", &chpl)))].concat();
        let p = temp(&file);
        let marks = chapter_marks(&p).unwrap();
        std::fs::remove_file(p).ok();
        assert_eq!(marks.iter().map(|m| (m.title.clone().unwrap(), m.start)).collect::<Vec<_>>(), [("序".into(), 0.0), ("第一章".into(), 12.5)]);
    }

    #[test]
    fn quicktime_chapter_track() {
        // Two titles in an mdat, then a moov whose audio track points to a text track holding them.
        let titles = ["开头", "Chapter 2"];
        let samples: Vec<Vec<u8>> = titles.iter().map(|t| [(t.len() as u16).to_be_bytes().to_vec(), t.as_bytes().to_vec()].concat()).collect();
        let ftyp = bx(b"ftyp", b"M4B \0\0\0\0");
        let mdat = bx(b"mdat", &samples.concat());
        let first = (ftyp.len() + 8) as u32;
        let tkhd = |id: u32| bx(b"tkhd", &[vec![0; 12], id.to_be_bytes().to_vec(), vec![0; 4]].concat());
        let audio = bx(b"trak", &[tkhd(1), bx(b"tref", &bx(b"chap", &2u32.to_be_bytes()))].concat());
        let mdhd = bx(b"mdhd", &[vec![0; 12], 1000u32.to_be_bytes().to_vec(), vec![0; 8]].concat());
        let stts = bx(b"stts", &[vec![0; 4], 2u32.to_be_bytes().to_vec(), 1u32.to_be_bytes().to_vec(), 3000u32.to_be_bytes().to_vec(), 1u32.to_be_bytes().to_vec(), 5000u32.to_be_bytes().to_vec()].concat());
        let stsc = bx(b"stsc", &[vec![0; 4], 1u32.to_be_bytes().to_vec(), 1u32.to_be_bytes().to_vec(), 2u32.to_be_bytes().to_vec(), 1u32.to_be_bytes().to_vec()].concat());
        let stsz = bx(b"stsz", &[vec![0; 4], 0u32.to_be_bytes().to_vec(), 2u32.to_be_bytes().to_vec(), (samples[0].len() as u32).to_be_bytes().to_vec(), (samples[1].len() as u32).to_be_bytes().to_vec()].concat());
        let stco = bx(b"stco", &[vec![0; 4], 1u32.to_be_bytes().to_vec(), first.to_be_bytes().to_vec()].concat());
        let stbl = bx(b"stbl", &[stts, stsc, stsz, stco].concat());
        let text = bx(b"trak", &[tkhd(2), bx(b"mdia", &[mdhd, bx(b"minf", &stbl)].concat())].concat());
        let file = [ftyp, mdat, bx(b"moov", &[audio, text].concat())].concat();
        let p = temp(&file);
        let marks = chapter_marks(&p).unwrap();
        std::fs::remove_file(p).ok();
        assert_eq!(marks.iter().map(|m| (m.title.clone().unwrap(), m.start)).collect::<Vec<_>>(), [("开头".into(), 0.0), ("Chapter 2".into(), 3.0)]);
    }

    #[test]
    fn no_marks_in_a_plain_file() {
        let p = temp(&bx(b"ftyp", b"M4A \0\0\0\0"));
        assert!(chapter_marks(&p).unwrap().is_empty());
        std::fs::remove_file(p).ok();
    }

    #[test]
    fn chapters_written_are_read_back() {
        let wrap = |name: &[u8], body: &[u8]| { let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec(); v.extend_from_slice(name); v.extend_from_slice(body); v };
        let mut f = wrap(b"ftyp", b"M4A \0\0\0\0");
        f.extend(wrap(b"mdat", &[0u8; 64]));
        // mvhd: timescale 1000, 3000 s long, next track 2; one sound track.
        let mut mvhd = vec![0u8; 100];
        mvhd[12..16].copy_from_slice(&1000u32.to_be_bytes());
        mvhd[16..20].copy_from_slice(&3_000_000u32.to_be_bytes());
        mvhd[96..100].copy_from_slice(&2u32.to_be_bytes());
        let mut hdlr = vec![0u8; 8];
        hdlr.extend_from_slice(b"soun");
        hdlr.extend_from_slice(&[0u8; 13]);
        let trak = wrap(b"trak", &[wrap(b"tkhd", &[0u8; 84]), wrap(b"mdia", &wrap(b"hdlr", &hdlr))].concat());
        f.extend(wrap(b"moov", &[wrap(b"mvhd", &mvhd), trak].concat()));
        let p = std::env::temp_dir().join(format!("yy-chpl-{}.m4a", std::process::id()));
        std::fs::write(&p, &f).unwrap();
        let marks = vec![AudioMark { title: Some("第一回".into()), start: 0.0 }, AudioMark { title: Some("第二回".into()), start: 1520.5 }];
        add_chapters(&p, &marks).unwrap();
        let back = chapter_marks(&p).unwrap();
        assert_eq!(back.len(), 2);
        assert_eq!(back[1].title.as_deref(), Some("第二回"));
        assert!((back[1].start - 1520.5).abs() < 1e-3);
        // Apple's way, on its own: the chapter track.
        let mut fh = File::open(&p).unwrap();
        let len = fh.metadata().unwrap().len();
        let moov = find(&mut fh, 0, len, b"moov").unwrap().unwrap();
        let moov = read_body(&mut fh, moov).unwrap();
        let qt = quicktime(&mut fh, &moov).unwrap();
        assert_eq!(qt.iter().map(|m| m.title.clone().unwrap_or_default()).collect::<Vec<_>>(), vec!["第一回", "第二回"], "{qt:?}");
        assert!((qt[1].start - 1520.5).abs() < 1e-2, "{qt:?}");
        // Again: replaced, not added.
        add_chapters(&p, &marks[..1]).unwrap();
        assert_eq!(chapter_marks(&p).unwrap().len(), 1);
        // With metadata + cover art (even containing the placeholder bytes "YYCH").
        let meta = Meta {
            title: Some("红楼梦".into()),
            author: Some("曹雪芹".into()),
            cover: Some(b"\xff\xd8\xff\xe0YYCHfakejpeg".to_vec()),
        };
        add_chapters_and_meta(&p, &marks, Some(&meta)).unwrap();
        let mut fh = File::open(&p).unwrap();
        let len = fh.metadata().unwrap().len();
        let span = find(&mut fh, 0, len, b"moov").unwrap().unwrap();
        let moov = read_body(&mut fh, span).unwrap();
        let qt = quicktime(&mut fh, &moov).unwrap();
        assert_eq!(qt.len(), 2);
        let ilst = path(&moov, &[b"udta"]).and_then(|u| child(u, b"meta")).and_then(|m| child(&m[4..], b"ilst")).unwrap();
        assert!(child(ilst, b"covr").is_some());
        assert!(child(ilst, b"\xa9nam").is_some());
        let _ = std::fs::remove_file(&p);
    }

    #[test]
    fn corrupt_64bit_box_sizes_do_not_panic_or_hang() {
        // 1) Wrap-around in boxes(): size=1, big_size=u64::MAX after an initial 16-byte box.
        let mut inner = vec![0, 0, 0, 16, b'f', b'r', b'e', b'e', 0, 0, 0, 0, 0, 0, 0, 0];
        inner.extend_from_slice(&1u32.to_be_bytes());
        inner.extend_from_slice(b"udta");
        inner.extend_from_slice(&u64::MAX.to_be_bytes());
        let file1 = bx(b"moov", &inner);
        let p1 = temp(&file1);
        assert!(chapter_marks(&p1).unwrap().is_empty());
        let _ = std::fs::remove_file(&p1);

        // 2) Wrap-around in find(): pos=16 + size=(u64::MAX - 15) wraps to 0.
        let mut file2 = vec![0, 0, 0, 16, b'f', b't', b'y', b'p', b'M', b'4', b'B', b' ', 0, 0, 0, 0];
        file2.extend_from_slice(&1u32.to_be_bytes());
        file2.extend_from_slice(b"free");
        file2.extend_from_slice(&(u64::MAX - 15).to_be_bytes());
        let p2 = temp(&file2);
        assert!(chapter_marks(&p2).unwrap().is_empty());
        let _ = std::fs::remove_file(&p2);
    }

    #[test]
    fn add_chapters_handles_64bit_mdat_box() {
        let mut f = bx(b"ftyp", b"M4A \0\0\0\0");
        // mdat using 64-bit extended size (size == 1).
        let payload = [0u8; 32];
        f.extend_from_slice(&1u32.to_be_bytes());
        f.extend_from_slice(b"mdat");
        f.extend_from_slice(&((16 + payload.len()) as u64).to_be_bytes());
        f.extend_from_slice(&payload);
        let mut mvhd = vec![0u8; 100];
        mvhd[12..16].copy_from_slice(&1000u32.to_be_bytes());
        mvhd[16..20].copy_from_slice(&60_000u32.to_be_bytes());
        mvhd[96..100].copy_from_slice(&2u32.to_be_bytes());
        let mut hdlr = vec![0u8; 8];
        hdlr.extend_from_slice(b"soun");
        hdlr.extend_from_slice(&[0u8; 13]);
        let trak = bx(b"trak", &[bx(b"tkhd", &[0u8; 84]), bx(b"mdia", &bx(b"hdlr", &hdlr))].concat());
        f.extend(bx(b"moov", &[bx(b"mvhd", &mvhd), trak].concat()));
        let p = temp(&f);
        let marks = vec![AudioMark { title: Some("第一章".into()), start: 0.0 }];
        add_chapters(&p, &marks).unwrap();
        let back = chapter_marks(&p).unwrap();
        let _ = std::fs::remove_file(&p);
        assert_eq!(back.len(), 1);
        assert_eq!(back[0].title.as_deref(), Some("第一章"));
    }
}
