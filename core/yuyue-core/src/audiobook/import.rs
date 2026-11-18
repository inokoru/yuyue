//! 导入: chapters from every container that carries them, the order files play in, and chapters
//! read off the transcript when the files have none.
//!
//! M4B's are read in `m4b`. Here: ID3v2 `CHAP` frames (mp3), Vorbis comments `CHAPTERnnn` /
//! `CHAPTERnnnNAME` (ogg, opus, flac), cue sheets. Files sort by disc, then track, then name —
//! numbers compared as numbers, Chinese (第十二回) and Roman (Part IV) ones too.

use std::cmp::Ordering;
use std::io::{Read, Seek, SeekFrom};
use std::path::Path;

use crate::audio::AudioMark;

/// Sound files a recording is made of.
pub const AUDIO_EXTENSIONS: &[&str] = &["m4b", "m4a", "mp3", "aac", "wav", "aiff", "flac", "ogg", "opus"];
/// Formats that are always locked: Audible's, and the iTunes Store's old protected files.
pub const LOCKED_EXTENSIONS: &[&str] = &["aa", "aax", "aaxc", "m4p"];

fn extension(name: &str) -> String {
    name.rsplit_once('.').map(|(_, e)| e.to_ascii_lowercase()).unwrap_or_default()
}

/// Whether a recording is locked (DRM) and can't be played or transcribed here: a locked
/// format by its name, or an MP4 whose sound is encrypted (a protection box in its `moov`).
pub fn is_locked(path: &Path) -> bool {
    let name = path.file_name().and_then(|n| n.to_str()).unwrap_or_default();
    if LOCKED_EXTENSIONS.contains(&extension(name).as_str()) {
        return true;
    }
    if !matches!(extension(name).as_str(), "m4b" | "m4a" | "mp4" | "aac") {
        return false;
    }
    mp4_locked(path).unwrap_or(false)
}

fn mp4_locked(path: &Path) -> std::io::Result<bool> {
    let mut f = std::fs::File::open(path)?;
    let len = f.metadata()?.len();
    let mut at = 0u64;
    while at + 8 <= len {
        f.seek(SeekFrom::Start(at))?;
        let mut head = [0u8; 16];
        f.read_exact(&mut head[..8])?;
        let mut size = u32::from_be_bytes(head[..4].try_into().unwrap()) as u64;
        let mut header = 8;
        if size == 1 {
            f.read_exact(&mut head[8..16])?;
            size = u64::from_be_bytes(head[8..16].try_into().unwrap());
            header = 16;
        } else if size == 0 {
            size = len - at;
        }
        if size < header {
            return Ok(false);
        }
        if &head[4..8] == b"moov" {
            let n = (size - header).min(64 << 20) as usize;
            let mut moov = vec![0u8; n];
            f.read_exact(&mut moov)?;
            // The sample entry of protected sound, or the protection scheme box inside it.
            return Ok(moov.windows(4).any(|w| matches!(w, b"sinf" | b"drms" | b"aavd" | b"adrm" | b"enca")));
        }
        at += size;
    }
    Ok(false)
}

/// The sound files in a zip, taken out into `into` (folders flattened into the name, so
/// `Disc 1/01.mp3` stays before `Disc 2/01.mp3`); none when it holds no sound — then it's
/// something else (an EPUB is a zip too).
pub fn unzip_sound(zip_path: &Path, into: &Path) -> crate::error::Result<Vec<String>> {
    let mut z = zip::ZipArchive::new(std::io::BufReader::new(std::fs::File::open(zip_path)?))?;
    let names: Vec<(usize, String)> = (0..z.len())
        .filter_map(|i| {
            let e = z.by_index(i).ok()?;
            let name = e.name().ok()?.to_string();
            let leaf = name.rsplit('/').next().unwrap_or_default();
            (!e.is_dir() && !name.starts_with("__MACOSX/") && !leaf.starts_with("._") && AUDIO_EXTENSIONS.contains(&extension(leaf).as_str())).then_some((i, name))
        })
        .collect();
    if names.is_empty() {
        return Ok(Vec::new());
    }
    std::fs::create_dir_all(into)?;
    let mut out = Vec::new();
    for (i, name) in names {
        let flat: String = name.trim_matches('/').split('/').filter(|p| !p.is_empty() && *p != "." && *p != "..").collect::<Vec<_>>().join(" - ");
        let target = into.join(flat.replace(['\\', ':', '\0'], "-"));
        let mut e = z.by_index(i)?;
        let mut w = std::fs::File::create(&target)?;
        std::io::copy(&mut e, &mut w)?;
        out.push(target.to_string_lossy().into_owned());
    }
    Ok(out)
}

/// Chapters from any supported audio container (MP4/M4B, MP3 ID3v2 `CHAP`, FLAC Vorbis/cuesheet,
/// Ogg Vorbis/Opus `CHAPTERnnn`) or a sibling `.cue` sheet; empty when none are found.
pub fn file_chapter_marks(path: &Path) -> Vec<AudioMark> {
    let ext = extension(path.file_name().and_then(|n| n.to_str()).unwrap_or_default());
    let mut marks = match ext.as_str() {
        "m4b" | "m4a" | "mp4" | "aac" => crate::m4b::chapter_marks(path).unwrap_or_default(),
        "mp3" => read_id3_head(path).map(|b| id3_chapters(&b)).unwrap_or_default(),
        "flac" => flac_chapters(path).unwrap_or_default(),
        "ogg" | "opus" => ogg_comments(path).map(|c| vorbis_chapters(&c)).unwrap_or_default(),
        _ => Vec::new(),
    };
    if marks.len() < 2
        && let Some(cue) = cue_marks_for(path)
        && cue.len() >= 2
    {
        marks = cue;
    }
    marks
}

/// Disc and track numbers read from a file's container tags (ID3v2, MP4 `ilst`, FLAC, Ogg),
/// plus its filename for natural-order fallback.
pub fn file_tags(path: &Path) -> FileTags {
    let name = path.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| path.to_string_lossy().into_owned());
    if !path.is_file() {
        return FileTags { name, disc: None, track: None };
    }
    let ext = extension(&name);
    let (disc, track) = match ext.as_str() {
        "mp3" => read_id3_head(path).map(|b| id3_disc_track(&b)).unwrap_or((None, None)),
        "m4b" | "m4a" | "mp4" => mp4_disc_track(path).unwrap_or((None, None)),
        "flac" => flac_comments(path).map(|c| vorbis_disc_track(&c)).unwrap_or((None, None)),
        "ogg" | "opus" => ogg_comments(path).map(|c| vorbis_disc_track(&c)).unwrap_or((None, None)),
        _ => (None, None),
    };
    FileTags { name, disc, track }
}

fn read_id3_head(path: &Path) -> Option<Vec<u8>> {
    let mut f = std::fs::File::open(path).ok()?;
    let mut head = [0u8; 10];
    f.read_exact(&mut head).ok()?;
    if &head[..3] != b"ID3" {
        return None;
    }
    let size = (syncsafe(&head[6..10]) as usize).min(16 << 20);
    let mut buf = vec![0u8; 10 + size];
    buf[..10].copy_from_slice(&head);
    let n = f.read(&mut buf[10..]).ok()?;
    buf.truncate(10 + n);
    Some(buf)
}

fn parse_tag_index(s: &str) -> Option<u32> {
    s.trim().split('/').next()?.trim().parse::<u32>().ok().filter(|&n| n > 0)
}

fn id3_disc_track(data: &[u8]) -> (Option<u32>, Option<u32>) {
    if data.len() < 10 || &data[..3] != b"ID3" {
        return (None, None);
    }
    let major = data[3];
    let end = (10 + syncsafe(&data[6..10]) as usize).min(data.len());
    let (mut disc, mut track) = (None, None);
    if major == 2 {
        let mut p = 10;
        while p + 6 <= end {
            let id = &data[p..p + 3];
            if id == [0, 0, 0] {
                break;
            }
            let len = ((data[p + 3] as usize) << 16) | ((data[p + 4] as usize) << 8) | (data[p + 5] as usize);
            let body = &data[(p + 6).min(end)..(p + 6 + len).min(end)];
            if id == b"TPA" {
                disc = text_frame(body).and_then(|s| parse_tag_index(&s));
            } else if id == b"TRK" {
                track = text_frame(body).and_then(|s| parse_tag_index(&s));
            }
            p += 6 + len;
        }
        return (disc, track);
    }
    let mut p = 10;
    if data[5] & 0x40 != 0 && p + 4 <= end {
        let ext = if major == 4 { syncsafe(&data[p..p + 4]) as usize } else { u32::from_be_bytes(data[p..p + 4].try_into().unwrap()) as usize + 4 };
        p += ext;
    }
    while p + 10 <= end {
        let id = &data[p..p + 4];
        if id == [0, 0, 0, 0] {
            break;
        }
        let len = if major == 4 { syncsafe(&data[p + 4..p + 8]) } else { u32::from_be_bytes(data[p + 4..p + 8].try_into().unwrap()) } as usize;
        let body = &data[(p + 10).min(end)..(p + 10 + len).min(end)];
        if id == b"TPOS" {
            disc = text_frame(body).and_then(|s| parse_tag_index(&s));
        } else if id == b"TRCK" {
            track = text_frame(body).and_then(|s| parse_tag_index(&s));
        }
        p += 10 + len;
    }
    (disc, track)
}

fn mp4_disc_track(path: &Path) -> Option<(Option<u32>, Option<u32>)> {
    let mut f = std::fs::File::open(path).ok()?;
    let len = f.metadata().ok()?.len();
    let mut at = 0u64;
    let mut moov: Option<Vec<u8>> = None;
    while at + 8 <= len {
        f.seek(SeekFrom::Start(at)).ok()?;
        let mut head = [0u8; 16];
        f.read_exact(&mut head[..8]).ok()?;
        let mut size = u32::from_be_bytes(head[..4].try_into().unwrap()) as u64;
        let mut header = 8u64;
        if size == 1 {
            f.read_exact(&mut head[8..16]).ok()?;
            size = u64::from_be_bytes(head[8..16].try_into().unwrap());
            header = 16;
        } else if size == 0 {
            size = len - at;
        }
        if size < header || at + size > len {
            break;
        }
        if &head[4..8] == b"moov" {
            let mut buf = vec![0u8; (size - header).min(32 << 20) as usize];
            f.read_exact(&mut buf).ok()?;
            moov = Some(buf);
            break;
        }
        at += size;
    }
    let moov = moov?;
    fn sub<'a>(data: &'a [u8], want: &[u8; 4]) -> Option<&'a [u8]> {
        let mut p = 0;
        while p + 8 <= data.len() {
            let s = u32::from_be_bytes(data[p..p + 4].try_into().unwrap()) as usize;
            let s = if s == 0 { data.len() - p } else { s };
            if s < 8 || p + s > data.len() {
                break;
            }
            if &data[p + 4..p + 8] == want {
                return Some(&data[p + 8..p + s]);
            }
            p += s;
        }
        None
    }
    let udta = sub(&moov, b"udta")?;
    let meta = sub(udta, b"meta").filter(|m| m.len() >= 4)?;
    let ilst = sub(&meta[4..], b"ilst")?;
    let num = |tag: &[u8; 4]| -> Option<u32> {
        let item = sub(ilst, tag)?;
        let data = sub(item, b"data")?;
        // 8 bytes (type + locale), 2 bytes padding, then u16 number.
        let n = u16::from_be_bytes(data.get(10..12)?.try_into().ok()?) as u32;
        (n > 0).then_some(n)
    };
    Some((num(b"disk"), num(b"trkn")))
}

fn vorbis_disc_track(comments: &[String]) -> (Option<u32>, Option<u32>) {
    let (mut disc, mut track) = (None, None);
    for c in comments {
        let Some((k, v)) = c.split_once('=') else { continue };
        match k.trim().to_ascii_uppercase().as_str() {
            "DISCNUMBER" | "DISC" => disc = parse_tag_index(v),
            "TRACKNUMBER" | "TRACK" => track = parse_tag_index(v),
            _ => {}
        }
    }
    (disc, track)
}

fn parse_vorbis_comment_list(b: &[u8]) -> Option<Vec<String>> {
    let vendor_len = u32::from_le_bytes(b.get(..4)?.try_into().ok()?) as usize;
    let mut p = 4usize.checked_add(vendor_len)?;
    let count = u32::from_le_bytes(b.get(p..p + 4)?.try_into().ok()?) as usize;
    p += 4;
    let mut out = Vec::with_capacity(count.min(1024));
    for _ in 0..count.min(4096) {
        let n = u32::from_le_bytes(b.get(p..p + 4)?.try_into().ok()?) as usize;
        p += 4;
        let s = b.get(p..p + n)?;
        p += n;
        out.push(String::from_utf8_lossy(s).into_owned());
    }
    Some(out)
}

fn flac_comments(path: &Path) -> Option<Vec<String>> {
    let mut f = std::fs::File::open(path).ok()?;
    let mut magic = [0u8; 4];
    f.read_exact(&mut magic).ok()?;
    if &magic != b"fLaC" {
        return None;
    }
    loop {
        let mut h = [0u8; 4];
        f.read_exact(&mut h).ok()?;
        let last = h[0] & 0x80 != 0;
        let kind = h[0] & 0x7f;
        let len = ((h[1] as usize) << 16) | ((h[2] as usize) << 8) | (h[3] as usize);
        if kind == 4 {
            let mut buf = vec![0u8; len.min(4 << 20)];
            f.read_exact(&mut buf).ok()?;
            return parse_vorbis_comment_list(&buf);
        }
        f.seek(SeekFrom::Current(len as i64)).ok()?;
        if last {
            return None;
        }
    }
}

fn flac_chapters(path: &Path) -> Option<Vec<AudioMark>> {
    let mut f = std::fs::File::open(path).ok()?;
    let mut magic = [0u8; 4];
    f.read_exact(&mut magic).ok()?;
    if &magic != b"fLaC" {
        return None;
    }
    let mut rate = 44_100u32;
    let mut vorbis = Vec::new();
    let mut cuesheet = Vec::new();
    loop {
        let mut h = [0u8; 4];
        if f.read_exact(&mut h).is_err() {
            break;
        }
        let last = h[0] & 0x80 != 0;
        let kind = h[0] & 0x7f;
        let len = ((h[1] as usize) << 16) | ((h[2] as usize) << 8) | (h[3] as usize);
        match kind {
            0 if len >= 18 => {
                let mut b = vec![0u8; len];
                f.read_exact(&mut b).ok()?;
                let r = ((b[10] as u32) << 12) | ((b[11] as u32) << 4) | ((b[12] as u32) >> 4);
                if r > 0 {
                    rate = r;
                }
            }
            4 => {
                let mut b = vec![0u8; len.min(4 << 20)];
                f.read_exact(&mut b).ok()?;
                if let Some(c) = parse_vorbis_comment_list(&b) {
                    vorbis = vorbis_chapters(&c);
                }
            }
            5 if len >= 396 => {
                let mut b = vec![0u8; len.min(1 << 20)];
                f.read_exact(&mut b).ok()?;
                let tracks = b[395] as usize;
                let mut p = 396usize;
                for _ in 0..tracks {
                    if p + 36 > b.len() {
                        break;
                    }
                    let offset = u64::from_be_bytes(b[p..p + 8].try_into().unwrap());
                    let num = b[p + 8];
                    let idx_n = b[p + 35] as usize;
                    p += 36 + idx_n * 12;
                    // 170 (0xAA) or 255 is the lead-out track.
                    if num != 170 && num != 255 && num > 0 {
                        cuesheet.push(AudioMark { title: None, start: offset as f64 / rate as f64 });
                    }
                }
            }
            _ => {
                f.seek(SeekFrom::Current(len as i64)).ok()?;
            }
        }
        if last {
            break;
        }
    }
    Some(if !vorbis.is_empty() { vorbis } else { cuesheet })
}

fn ogg_comments(path: &Path) -> Option<Vec<String>> {
    let mut f = std::fs::File::open(path).ok()?;
    let mut data = vec![0u8; 256 << 10];
    let n = f.read(&mut data).ok()?;
    data.truncate(n);
    if data.len() < 27 || &data[..4] != b"OggS" {
        return None;
    }
    let mut p = 0usize;
    let mut packet = Vec::new();
    while p + 27 <= data.len() && &data[p..p + 4] == b"OggS" {
        let segs = data[p + 26] as usize;
        let table = data.get(p + 27..p + 27 + segs)?;
        let mut body = p + 27 + segs;
        for &l in table {
            let chunk = data.get(body..body + l as usize)?;
            packet.extend_from_slice(chunk);
            body += l as usize;
            if l < 255 {
                if let Some(rest) = packet.strip_prefix(b"\x03vorbis").or_else(|| packet.strip_prefix(b"OpusTags")) {
                    return parse_vorbis_comment_list(rest);
                }
                packet.clear();
            }
        }
        p = body;
    }
    None
}

fn decode_cue_bytes(bytes: &[u8]) -> String {
    if let Some(rest) = bytes.strip_prefix(&[0xEF, 0xBB, 0xBF]) {
        return String::from_utf8_lossy(rest).into_owned();
    }
    if let Ok(s) = std::str::from_utf8(bytes) {
        return s.to_string();
    }
    let mut det = chardetng::EncodingDetector::new(chardetng::Iso2022JpDetection::Allow);
    det.feed(bytes, true);
    let enc = det.guess(None, chardetng::Utf8Detection::Allow);
    enc.decode(bytes).0.into_owned()
}

fn cue_marks_for(path: &Path) -> Option<Vec<AudioMark>> {
    let dir = path.parent()?;
    let file_name = path.file_name()?.to_str()?;
    let stem = path.file_stem()?.to_str()?;
    let entries: Vec<_> = std::fs::read_dir(dir)
        .ok()?
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.extension().and_then(|x| x.to_str()).is_some_and(|x| x.eq_ignore_ascii_case("cue")))
        .collect();
    if entries.is_empty() {
        return None;
    }
    let exact = entries.iter().find(|p| p.file_stem().and_then(|s| s.to_str()).is_some_and(|s| s.eq_ignore_ascii_case(stem)));
    for cue in exact.into_iter().chain(entries.iter().filter(|p| Some(*p) != exact)) {
        let Ok(raw) = std::fs::read(cue) else { continue };
        let tracks = cue_tracks(&decode_cue_bytes(&raw));
        if tracks.is_empty() {
            continue;
        }
        let files: std::collections::HashSet<String> = tracks.iter().map(|t| t.0.to_lowercase()).collect();
        let matched: Vec<AudioMark> = tracks
            .into_iter()
            .filter(|(f, _, _)| {
                let leaf = f.rsplit(['/', '\\']).next().unwrap_or(f.as_str());
                leaf.eq_ignore_ascii_case(file_name) || (Some(cue) == exact && files.len() <= 1)
            })
            .map(|(_, title, start)| AudioMark { title, start })
            .collect();
        if matched.len() >= 2 {
            return Some(matched);
        }
    }
    None
}

/// Chapters from an ID3v2 tag at the start of an mp3: `CHAP` frames, titled by their `TIT2`.
pub fn id3_chapters(data: &[u8]) -> Vec<AudioMark> {
    let mut out = Vec::new();
    if data.len() < 10 || &data[..3] != b"ID3" {
        return out;
    }
    let major = data[3];
    let size = syncsafe(&data[6..10]) as usize;
    let end = (10 + size).min(data.len());
    let mut p = 10;
    if data[5] & 0x40 != 0 && p + 4 <= end {
        // Extended header.
        let ext = if major == 4 { syncsafe(&data[p..p + 4]) as usize } else { u32::from_be_bytes(data[p..p + 4].try_into().unwrap()) as usize + 4 };
        p += ext;
    }
    while p + 10 <= end {
        let id = &data[p..p + 4];
        if id == [0, 0, 0, 0] {
            break;
        }
        let len = if major == 4 { syncsafe(&data[p + 4..p + 8]) } else { u32::from_be_bytes(data[p + 4..p + 8].try_into().unwrap()) } as usize;
        let body = &data[(p + 10).min(end)..(p + 10 + len).min(end)];
        if id == b"CHAP"
            && let Some(z) = body.iter().position(|&b| b == 0)
                && body.len() >= z + 1 + 16 {
                    let t = &body[z + 1..];
                    let start_ms = u32::from_be_bytes(t[..4].try_into().unwrap());
                    let title = frames_title(&t[16..], major);
                    out.push(AudioMark { title, start: start_ms as f64 / 1000.0 });
                }
        p += 10 + len;
    }
    out.sort_by(|a, b| a.start.total_cmp(&b.start));
    out
}

fn syncsafe(b: &[u8]) -> u32 {
    b.iter().fold(0u32, |a, &x| (a << 7) | (x & 0x7f) as u32)
}

/// The TIT2 among a chapter's embedded frames.
fn frames_title(mut b: &[u8], major: u8) -> Option<String> {
    while b.len() >= 10 {
        let len = if major == 4 { syncsafe(&b[4..8]) } else { u32::from_be_bytes(b[4..8].try_into().unwrap()) } as usize;
        let body = b.get(10..10 + len)?;
        if &b[..4] == b"TIT2" {
            return text_frame(body);
        }
        b = &b[(10 + len).min(b.len())..];
    }
    None
}

fn text_frame(body: &[u8]) -> Option<String> {
    let (enc, t) = body.split_first()?;
    let s = match enc {
        0 => t.iter().map(|&c| c as char).collect(),
        1 | 2 => {
            let (be, t) = match t {
                [0xfe, 0xff, r @ ..] => (true, r),
                [0xff, 0xfe, r @ ..] => (false, r),
                r => (*enc == 2, r),
            };
            let u: Vec<u16> = t.as_chunks::<2>().0.iter().map(|c| if be { u16::from_be_bytes(*c) } else { u16::from_le_bytes(*c) }).collect();
            String::from_utf16_lossy(&u)
        }
        _ => String::from_utf8_lossy(t).into_owned(),
    };
    let s = s.trim_matches('\0').trim().to_string();
    (!s.is_empty()).then_some(s)
}

/// Chapters from Vorbis comments (`CHAPTER001=00:00:00.000`, `CHAPTER001NAME=…`), given as
/// `KEY=value` strings.
pub fn vorbis_chapters(comments: &[String]) -> Vec<AudioMark> {
    let mut by: std::collections::BTreeMap<u32, (Option<f64>, Option<String>)> = Default::default();
    for c in comments {
        let Some((k, v)) = c.split_once('=') else { continue };
        let k = k.to_ascii_uppercase();
        let Some(rest) = k.strip_prefix("CHAPTER") else { continue };
        let digits: String = rest.chars().take_while(char::is_ascii_digit).collect();
        let Ok(n) = digits.parse::<u32>() else { continue };
        let tail = &rest[digits.len()..];
        let e = by.entry(n).or_default();
        if tail.is_empty() {
            e.0 = clock(v);
        } else if tail == "NAME" {
            e.1 = Some(v.trim().to_string());
        }
    }
    by.into_values().filter_map(|(t, n)| t.map(|start| AudioMark { title: n, start })).collect()
}

/// 01:02:03.500 or 02:03.5 as seconds.
fn clock(s: &str) -> Option<f64> {
    let parts: Vec<f64> = s.trim().split(':').map(|p| p.parse::<f64>()).collect::<Result<_, _>>().ok()?;
    Some(parts.iter().fold(0.0, |a, x| a * 60.0 + x))
}

/// A cue sheet's tracks: (file, title, start in that file). `INDEX 01 mm:ss:ff`, 75 frames a second.
pub fn cue_tracks(text: &str) -> Vec<(String, Option<String>, f64)> {
    let mut out = Vec::new();
    let mut file = String::new();
    let mut title: Option<String> = None;
    let unq = |s: &str| s.trim().trim_matches('"').to_string();
    for line in text.lines() {
        let l = line.trim();
        if let Some(r) = l.strip_prefix("FILE ") {
            let r = r.trim();
            file = match r.rfind(' ') {
                Some(i) if r.starts_with('"') => unq(&r[..i]),
                _ => unq(r),
            };
        } else if let Some(r) = l.strip_prefix("TRACK ") {
            let _ = r;
            title = None;
        } else if let Some(r) = l.strip_prefix("TITLE ") {
            title = Some(unq(r));
        } else if let Some(r) = l.strip_prefix("INDEX 01 ") {
            let p: Vec<f64> = r.trim().split(':').filter_map(|x| x.parse().ok()).collect();
            if p.len() == 3 {
                out.push((file.clone(), title.clone(), p[0] * 60.0 + p[1] + p[2] / 75.0));
            }
        }
    }
    out
}

fn zh_value(s: &str) -> Option<u64> {
    if s.is_empty() || !s.chars().all(|c| "零〇一二两三四五六七八九十百千万".contains(c)) {
        return None;
    }
    super::phon::zh_number(s).parse().ok()
}

fn roman_value(s: &str) -> Option<u64> {
    let v = |c: char| match c {
        'I' => Some(1),
        'V' => Some(5),
        'X' => Some(10),
        'L' => Some(50),
        'C' => Some(100),
        'D' => Some(500),
        'M' => Some(1000),
        _ => None,
    };
    let vals: Vec<u64> = s.chars().map(v).collect::<Option<_>>()?;
    if vals.is_empty() {
        return None;
    }
    Some(vals.iter().enumerate().fold(0i64, |a, (i, &x)| if vals.get(i + 1).is_some_and(|&n| n > x) { a - x as i64 } else { a + x as i64 }) as u64)
}

#[derive(Debug, PartialEq, Eq, PartialOrd, Ord)]
enum Piece {
    Num(u64),
    Text(String),
}

/// A name cut into text and numbers: Arabic, full-width, Chinese (after 第 or before 回章集卷部),
/// Roman (a whole word of capitals after Part / Book / Chapter / Vol).
fn pieces(name: &str) -> Vec<Piece> {
    let chars: Vec<char> = name.chars().collect();
    let mut out = Vec::new();
    let mut text = String::new();
    let mut i = 0;
    let flush = |text: &mut String, out: &mut Vec<Piece>| {
        if !text.is_empty() {
            out.push(Piece::Text(std::mem::take(text).to_lowercase()));
        }
    };
    while i < chars.len() {
        let c = chars[i];
        if c.is_ascii_digit() || ('０'..='９').contains(&c) {
            let mut n = 0u64;
            while i < chars.len() && (chars[i].is_ascii_digit() || ('０'..='９').contains(&chars[i])) {
                let d = if chars[i].is_ascii_digit() { chars[i] as u64 - '0' as u64 } else { chars[i] as u64 - '０' as u64 };
                n = n.saturating_mul(10).saturating_add(d);
                i += 1;
            }
            flush(&mut text, &mut out);
            out.push(Piece::Num(n));
            continue;
        }
        if "零〇一二两三四五六七八九十百千".contains(c) {
            let s = i;
            while i < chars.len() && "零〇一二两三四五六七八九十百千".contains(chars[i]) {
                i += 1;
            }
            let run: String = chars[s..i].iter().collect();
            let before_di = s > 0 && chars[s - 1] == '第';
            let after_unit = chars.get(i).is_some_and(|c| "回章集卷部节節篇册冊".contains(*c));
            if (before_di || after_unit)
                && let Some(v) = zh_value(&run) {
                    flush(&mut text, &mut out);
                    out.push(Piece::Num(v));
                    continue;
                }
            text.push_str(&run);
            continue;
        }
        if c.is_ascii_uppercase() {
            let s = i;
            while i < chars.len() && chars[i].is_ascii_alphabetic() {
                i += 1;
            }
            let word: String = chars[s..i].iter().collect();
            let prev: String = text.trim_end().to_lowercase();
            if ["part", "book", "chapter", "vol", "volume", "disc", "cd"].iter().any(|p| prev.ends_with(p))
                && let Some(v) = roman_value(&word) {
                    flush(&mut text, &mut out);
                    out.push(Piece::Num(v));
                    continue;
                }
            text.push_str(&word);
            continue;
        }
        text.push(c);
        i += 1;
    }
    flush(&mut text, &mut out);
    out
}

/// The order files play in by name.
pub fn name_order(a: &str, b: &str) -> Ordering {
    pieces(a).cmp(&pieces(b)).then_with(|| a.cmp(b))
}

/// What the tags say about a file's place.
#[derive(Clone, Debug, Default, PartialEq, uniffi::Record)]
pub struct FileTags {
    pub name: String,
    pub disc: Option<u32>,
    pub track: Option<u32>,
}

/// Files in play order: disc and track from the tags when every file has them and none repeat,
/// names otherwise.
pub fn play_order(files: &[FileTags]) -> Vec<usize> {
    let mut idx: Vec<usize> = (0..files.len()).collect();
    let keys: Vec<(u32, u32)> = files.iter().filter_map(|f| f.track.map(|t| (f.disc.unwrap_or(1), t))).collect();
    let mut unique = keys.clone();
    unique.sort();
    unique.dedup();
    if keys.len() == files.len() && unique.len() == keys.len() {
        idx.sort_by_key(|&i| (files[i].disc.unwrap_or(1), files[i].track.unwrap_or(0)));
    } else {
        idx.sort_by(|&a, &b| name_order(&files[a].name, &files[b].name));
    }
    idx
}

/// Chapters read off the transcript of a recording without marks: a chapter heading said right
/// after a pause of two seconds or more (第三章, Chapter Three).
pub fn spoken_chapters(words: &[super::anchor::HeardWord]) -> Vec<AudioMark> {
    const ZH_NUM: &str = "0123456789０１２３４５６７８９零〇一二两三四五六七八九十百千万";
    const EN_NUM: &[&str] = &[
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen", "twenty",
        "thirty", "forty", "fifty", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
    ];
    let mut out = Vec::new();
    for (i, w) in words.iter().enumerate() {
        let gap = if i == 0 { f64::MAX } else { w.t0 - words[i - 1].t1 };
        if gap < 2.0 {
            continue;
        }
        let head: String = words[i..(i + 10).min(words.len())].iter().map(|x| x.text.as_str()).collect::<Vec<_>>().join(" ");
        let compact: String = head.chars().filter(|c| !c.is_whitespace()).collect();
        let zh = compact.strip_prefix('第').is_some_and(|rest| {
            let num: String = rest.chars().take_while(|c| ZH_NUM.contains(*c)).collect();
            !num.is_empty() && num.chars().count() <= 8 && rest[num.len()..].chars().next().is_some_and(|u| "章回卷节節部集篇幕".contains(u))
        });
        let lower = head.to_lowercase();
        let en = ["chapter ", "part ", "book "].iter().any(|p| {
            lower.strip_prefix(p).is_some_and(|rest| {
                let tok = rest.split(|c: char| !c.is_ascii_alphanumeric()).find(|s| !s.is_empty()).unwrap_or("");
                !tok.is_empty()
                    && (tok.chars().all(|c| c.is_ascii_digit())
                        || EN_NUM.contains(&tok)
                        || roman_value(&tok.to_ascii_uppercase()).is_some())
            })
        });
        if zh || en {
            let title: String = if zh { compact.chars().take_while(|c| !"，。,.".contains(*c)).take(20).collect() } else { head.split(['.', ',']).next().unwrap_or("").trim().to_string() };
            out.push(AudioMark { title: Some(title), start: w.t0 });
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn names_sort_by_their_numbers() {
        let mut v = vec!["第十二回.mp3", "第二回.mp3", "第一百回.mp3", "第3回.mp3"];
        v.sort_by(|a, b| name_order(a, b));
        assert_eq!(v, vec!["第二回.mp3", "第3回.mp3", "第十二回.mp3", "第一百回.mp3"]);
        let mut e = vec!["Part X.mp3", "Part IV.mp3", "Part IX.mp3"];
        e.sort_by(|a, b| name_order(a, b));
        assert_eq!(e, vec!["Part IV.mp3", "Part IX.mp3", "Part X.mp3"]);
        let mut d = vec!["track10.mp3", "track2.mp3", "Track1.mp3"];
        d.sort_by(|a, b| name_order(a, b));
        assert_eq!(d, vec!["Track1.mp3", "track2.mp3", "track10.mp3"]);
    }

    #[test]
    fn tags_win_when_complete() {
        let f = vec![
            FileTags { name: "b".into(), disc: Some(1), track: Some(2) },
            FileTags { name: "a".into(), disc: Some(2), track: Some(1) },
            FileTags { name: "c".into(), disc: Some(1), track: Some(1) },
        ];
        assert_eq!(play_order(&f), vec![2, 0, 1]);
        let partial = vec![FileTags { name: "2".into(), disc: None, track: Some(1) }, FileTags { name: "1".into(), disc: None, track: None }];
        assert_eq!(play_order(&partial), vec![1, 0]);
    }

    #[test]
    fn id3_chapters_are_read() {
        // ID3v2.3 with one CHAP holding a TIT2.
        let title = b"\x00Intro";
        let mut tit2 = b"TIT2".to_vec();
        tit2.extend((title.len() as u32).to_be_bytes());
        tit2.extend([0, 0]);
        tit2.extend(title);
        let mut chap_body = b"ch0\x00".to_vec();
        chap_body.extend(1500u32.to_be_bytes());
        chap_body.extend(9000u32.to_be_bytes());
        chap_body.extend([0xff; 8]);
        chap_body.extend(&tit2);
        let mut chap = b"CHAP".to_vec();
        chap.extend((chap_body.len() as u32).to_be_bytes());
        chap.extend([0, 0]);
        chap.extend(&chap_body);
        let mut tag = b"ID3\x03\x00\x00".to_vec();
        let n = chap.len() as u32;
        tag.extend([(n >> 21) as u8 & 0x7f, (n >> 14) as u8 & 0x7f, (n >> 7) as u8 & 0x7f, n as u8 & 0x7f]);
        tag.extend(&chap);
        let m = id3_chapters(&tag);
        assert_eq!(m, vec![AudioMark { title: Some("Intro".into()), start: 1.5 }]);
    }

    #[test]
    fn vorbis_and_cue() {
        let v = vorbis_chapters(&["CHAPTER001=00:00:00.000".into(), "CHAPTER001NAME=开篇".into(), "CHAPTER002=00:12:30.500".into()]);
        assert_eq!(v.len(), 2);
        assert_eq!(v[1].start, 750.5);
        let cue = "FILE \"book.flac\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"One\"\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    TITLE \"Two\"\n    INDEX 01 05:10:37\n";
        let t = cue_tracks(cue);
        assert_eq!(t.len(), 2);
        assert_eq!(t[1].0, "book.flac");
        assert!((t[1].2 - 310.4933).abs() < 1e-3);
    }

    #[test]
    fn chapters_heard_after_a_pause() {
        use crate::audiobook::anchor::HeardWord;
        let w = |t: &str, a: f64| HeardWord { text: t.into(), t0: a, t1: a + 0.3 };
        let words = vec![
            w("正文", 0.0), w("结束", 1.0),
            w("第一天", 4.0), w("晚上", 4.4), w("他回到家", 4.8), // NOT a chapter
            w("Part", 8.0), w("of", 8.4), w("it", 8.8),           // NOT a chapter
            w("第三章", 12.0), w("归来", 12.4), w("他", 13.0),
            w("Chapter", 18.0), w("IV", 18.4), w("Dawn", 18.8),
        ];
        let c = spoken_chapters(&words);
        assert_eq!(c.len(), 2);
        assert_eq!(c[0].start, 12.0);
        assert_eq!(c[1].start, 18.0);
    }

    #[test]
    fn file_chapters_and_tags_from_disk() {
        let dir = std::env::temp_dir().join(format!("yy-import-{}", getrandom::u64().unwrap()));
        std::fs::create_dir_all(&dir).unwrap();
        // MP3 with TPOS=2, TRCK=3/10, and two CHAP frames.
        let frame = |id: &[u8; 4], body: &[u8]| {
            let mut v = id.to_vec();
            v.extend((body.len() as u32).to_be_bytes());
            v.extend([0, 0]);
            v.extend_from_slice(body);
            v
        };
        let chap = |cid: &[u8], ms: u32, name: &str| {
            let mut tit2_body = vec![3]; // UTF-8
            tit2_body.extend(name.as_bytes());
            let mut b = cid.to_vec();
            b.push(0);
            b.extend(ms.to_be_bytes());
            b.extend((ms + 5000).to_be_bytes());
            b.extend([0xff; 8]);
            b.extend(frame(b"TIT2", &tit2_body));
            frame(b"CHAP", &b)
        };
        let mut frames = Vec::new();
        frames.extend(frame(b"TPOS", b"\x032/2"));
        frames.extend(frame(b"TRCK", b"\x033/10"));
        frames.extend(chap(b"c1", 0, "序幕"));
        frames.extend(chap(b"c2", 12_500, "第一章"));
        let n = frames.len() as u32;
        let mut mp3 = b"ID3\x03\x00\x00".to_vec();
        mp3.extend([(n >> 21) as u8 & 0x7f, (n >> 14) as u8 & 0x7f, (n >> 7) as u8 & 0x7f, n as u8 & 0x7f]);
        mp3.extend(frames);
        mp3.extend([0u8; 64]);
        let mp3_path = dir.join("z.mp3");
        std::fs::write(&mp3_path, &mp3).unwrap();
        let tags = file_tags(&mp3_path);
        assert_eq!((tags.disc, tags.track), (Some(2), Some(3)));
        let marks = file_chapter_marks(&mp3_path);
        assert_eq!(marks.len(), 2);
        assert_eq!(marks[1].title.as_deref(), Some("第一章"));
        assert!((marks[1].start - 12.5).abs() < 1e-3);

        // WAV paired with a sibling .cue sheet.
        let wav_path = dir.join("book.wav");
        std::fs::write(&wav_path, b"RIFF\x00\x00\x00\x00WAVE").unwrap();
        std::fs::write(
            dir.join("book.cue"),
            "FILE \"book.wav\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"上篇\"\n    INDEX 01 00:00:00\n  TRACK 02 AUDIO\n    TITLE \"下篇\"\n    INDEX 01 02:30:00\n",
        )
        .unwrap();
        let cue_m = file_chapter_marks(&wav_path);
        assert_eq!(cue_m.len(), 2);
        assert_eq!(cue_m[1].title.as_deref(), Some("下篇"));
        assert!((cue_m[1].start - 150.0).abs() < 1e-3);
        let _ = std::fs::remove_dir_all(&dir);
    }
}

#[cfg(test)]
mod lock_tests {
    use super::*;

    fn boxed(kind: &[u8], body: &[u8]) -> Vec<u8> {
        let mut v = ((body.len() + 8) as u32).to_be_bytes().to_vec();
        v.extend(kind);
        v.extend(body);
        v
    }

    #[test]
    fn protected_sound_is_found_in_moov() {
        let dir = std::env::temp_dir().join(format!("yy-lock-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let plain = [boxed(b"ftyp", b"M4B "), boxed(b"mdat", &[0; 64]), boxed(b"moov", &boxed(b"trak", &boxed(b"mp4a", &[0; 20])))].concat();
        let locked = [boxed(b"ftyp", b"M4B "), boxed(b"moov", &boxed(b"trak", &boxed(b"drms", &boxed(b"sinf", &[0; 20])))), boxed(b"mdat", &[0; 64])].concat();
        std::fs::write(dir.join("a.m4b"), plain).unwrap();
        std::fs::write(dir.join("b.m4b"), locked).unwrap();
        std::fs::write(dir.join("c.aax"), b"anything").unwrap();
        std::fs::write(dir.join("d.mp3"), b"ID3").unwrap();
        assert!(!is_locked(&dir.join("a.m4b")));
        assert!(is_locked(&dir.join("b.m4b")));
        assert!(is_locked(&dir.join("c.aax")));
        assert!(!is_locked(&dir.join("d.mp3")));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn a_zip_of_sound_comes_out_in_order() {
        use std::io::Write;
        let dir = std::env::temp_dir().join(format!("yy-zip-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let path = dir.join("book.zip");
        {
            let mut z = zip::ZipWriter::new(std::fs::File::create(&path).unwrap());
            let o = zip::write::SimpleFileOptions::default();
            for n in ["Disc 2/01.mp3", "Disc 1/01.mp3", "cover.jpg", "__MACOSX/._x.mp3", "Disc 1/02.mp3"] {
                z.start_file(n, o).unwrap();
                z.write_all(b"x").unwrap();
            }
            z.finish().unwrap();
        }
        let got = unzip_sound(&path, &dir.join("out")).unwrap();
        let names: Vec<String> = got.iter().map(|p| Path::new(p).file_name().unwrap().to_string_lossy().into_owned()).collect();
        assert_eq!(names, vec!["Disc 2 - 01.mp3", "Disc 1 - 01.mp3", "Disc 1 - 02.mp3"]);
        let epubish = dir.join("e.zip");
        {
            let mut z = zip::ZipWriter::new(std::fs::File::create(&epubish).unwrap());
            z.start_file("mimetype", zip::write::SimpleFileOptions::default()).unwrap();
            z.write_all(b"application/epub+zip").unwrap();
            z.finish().unwrap();
        }
        assert!(unzip_sound(&epubish, &dir.join("out2")).unwrap().is_empty());
        let _ = std::fs::remove_dir_all(&dir);
    }
}
