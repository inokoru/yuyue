//! MOBI and AZW3 (KF8), the Kindle's own formats, turned into an EPUB that the importer already
//! reads. After foliate-js's `mobi.js` (MIT, John Factotum).
//!
//! A MOBI is a Palm database: record 0 holds the headers, the next records the compressed text,
//! then pictures. An old MOBI's text is one HTML stream cut by `<mbp:pagebreak/>`, with links
//! as byte offsets (`filepos`). A KF8 book keeps each XHTML file as a skeleton with fragments to
//! put back in, links as `kindle:pos:fid:…:off:…`, and stylesheets as separate flows. A joint
//! file carries both; the KF8 half is read.

use std::collections::{BTreeSet, HashMap};
use std::io::{Cursor, Write};
use std::sync::LazyLock;

use regex::bytes::{Captures, Regex};
use zip::write::SimpleFileOptions;
use zip::{CompressionMethod, ZipWriter};

use crate::error::{CoreError, Result};

pub fn is_mobi(bytes: &[u8]) -> bool {
    bytes.len() > 78 && matches!(&bytes[60..68], b"BOOKMOBI" | b"TEXtREAd")
}

fn bad(what: &str) -> CoreError {
    CoreError::bad_book(format!("the Kindle book is damaged ({what})"))
}

fn u16_at(b: &[u8], i: usize) -> u32 {
    b.get(i..i + 2).map(|s| u16::from_be_bytes([s[0], s[1]]) as u32).unwrap_or(0)
}

fn u32_at(b: &[u8], i: usize) -> u32 {
    b.get(i..i + 4).map(|s| u32::from_be_bytes([s[0], s[1], s[2], s[3]])).unwrap_or(0)
}

const NONE: u32 = 0xffff_ffff;
/// No book's text is near this; a damaged one could claim more.
const MAX_TEXT: usize = 512 << 20;

struct Pdb<'a> {
    bytes: &'a [u8],
    offsets: Vec<usize>,
}

impl<'a> Pdb<'a> {
    fn open(bytes: &'a [u8]) -> Result<Self> {
        let n = u16_at(bytes, 76) as usize;
        if n == 0 || 78 + n * 8 > bytes.len() {
            return Err(bad("record table"));
        }
        let raws: Vec<usize> = (0..n).map(|i| u32_at(bytes, 78 + i * 8) as usize).collect();
        let mut offsets = Vec::with_capacity(n + 1);
        for (i, &raw) in raws.iter().enumerate() {
            if i == 0 && (raw < 78 + n * 8 || raw >= bytes.len()) {
                return Err(bad("record table"));
            }
            let last = offsets.last().copied().unwrap_or(0);
            let o = if raw < last || raw > bytes.len() {
                if i <= 1 {
                    return Err(bad("record table"));
                }
                // Trailing unused or EOF sentinel entries in third-party PRC/MOBI tools:
                // anchor at the next valid offset (or EOF) so the preceding record stays intact.
                raws[i + 1..]
                    .iter()
                    .copied()
                    .find(|&next| next >= last && next <= bytes.len())
                    .unwrap_or(bytes.len())
            } else {
                raw
            };
            offsets.push(o);
        }
        offsets.push(bytes.len());
        Ok(Pdb { bytes, offsets })
    }

    fn name(&self) -> &'a [u8] {
        let head = &self.bytes[..32.min(self.bytes.len())];
        let end = head.iter().position(|&b| b == 0).unwrap_or(head.len());
        &head[..end]
    }

    fn record(&self, i: usize) -> Option<&'a [u8]> {
        let (&a, &b) = (self.offsets.get(i)?, self.offsets.get(i + 1)?);
        self.bytes.get(a..b)
    }
}

#[derive(Default)]
struct Exth {
    authors: Vec<String>,
    publisher: Option<String>,
    description: Option<String>,
    subjects: Vec<String>,
    isbn: Option<String>,
    title: Option<String>,
    language: Option<String>,
    cover: Option<u32>,
    thumb: Option<u32>,
    boundary: Option<u32>,
}

struct Header {
    compression: u32,
    record_count: u32,
    encryption: u32,
    codepage: u32,
    utf8: bool,
    palmdoc_only: bool,
    version: u32,
    title: String,
    locale: u32,
    resource_start: u32,
    huff: u32,
    huff_count: u32,
    trailing: u32,
    ncx: u32,
    fdst: u32,
    frag: u32,
    skel: u32,
    exth: Exth,
}

fn clean_xml(s: &str) -> String {
    s.chars()
        .filter(|&c| (c >= '\x20' || matches!(c, '\t' | '\n' | '\r')) && !matches!(c, '\u{FFFE}' | '\u{FFFF}'))
        .collect()
}

fn encoding_for(codepage: u32, locale: u32, sample: &[u8]) -> &'static encoding_rs::Encoding {
    match codepage {
        65001 => encoding_rs::UTF_8,
        936 | 54936 => encoding_rs::GB18030,
        950 => encoding_rs::BIG5,
        932 => encoding_rs::SHIFT_JIS,
        949 => encoding_rs::EUC_KR,
        1251 => encoding_rs::WINDOWS_1251,
        _ => {
            if !sample.is_empty() && sample.is_ascii() {
                return if locale & 0xff == 4 {
                    let sublang = (locale >> 10) & 0x3f;
                    if matches!(sublang, 1 | 3 | 5) { encoding_rs::BIG5 } else { encoding_rs::GB18030 }
                } else {
                    encoding_rs::WINDOWS_1252
                };
            }
            if !sample.is_empty() && std::str::from_utf8(sample).is_ok() {
                return encoding_rs::UTF_8;
            }
            let tld: Option<&[u8]> = match locale & 0xff {
                4 => {
                    let sublang = (locale >> 10) & 0x3f;
                    if matches!(sublang, 1 | 3 | 5) { Some(b"tw") } else { Some(b"cn") }
                }
                17 => Some(b"jp"),
                18 => Some(b"kr"),
                25 => Some(b"ru"),
                _ => None,
            };
            let mut detector = chardetng::EncodingDetector::new(chardetng::Iso2022JpDetection::Deny);
            detector.feed(&sample[..sample.len().min(65536)], true);
            detector.guess(tld, chardetng::Utf8Detection::Allow)
        }
    }
}

fn decode_bytes(enc: &'static encoding_rs::Encoding, b: &[u8]) -> String {
    let raw = if enc == encoding_rs::UTF_8 || (!b.is_ascii() && std::str::from_utf8(b).is_ok()) {
        String::from_utf8_lossy(b).into_owned()
    } else {
        enc.decode_without_bom_handling(b).0.into_owned()
    };
    clean_xml(&raw)
}

fn fill_header_meta(r0: &[u8], pdb_name: &[u8], h: &mut Header, enc: &'static encoding_rs::Encoding) {
    let decode = |b: &[u8]| decode_bytes(enc, b);
    if h.palmdoc_only {
        h.title = decode(pdb_name).trim().to_string();
        return;
    }
    let length = u32_at(r0, 20) as usize;
    let (to, tl) = (u32_at(r0, 84) as usize, u32_at(r0, 88) as usize);
    h.title = r0
        .get(to..to.saturating_add(tl))
        .map(&decode)
        .filter(|s| !s.trim().is_empty())
        .unwrap_or_else(|| decode(pdb_name))
        .trim()
        .to_string();
    let mut exth = Exth::default();
    if u32_at(r0, 128) & 0x40 != 0 {
        let e = 16 + length;
        if r0.get(e..e + 4) == Some(b"EXTH") {
            let count = u32_at(r0, e + 8) as usize;
            let mut at = e + 12;
            for _ in 0..count.min(4096) {
                let (kind, len) = (u32_at(r0, at), u32_at(r0, at + 4) as usize);
                let Some(data) = r0.get(at + 8..at + len.max(8)) else { break };
                let text = || Some(decode(data).trim().to_string()).filter(|s| !s.is_empty());
                match kind {
                    100 => exth.authors.extend(text()),
                    101 => exth.publisher = text(),
                    103 => exth.description = text(),
                    104 => exth.isbn = text().and_then(|t| crate::epub::isbn13(&t)),
                    105 => exth.subjects.extend(text()),
                    121 => exth.boundary = Some(u32_at(data, 0)),
                    201 => exth.cover = Some(u32_at(data, 0)),
                    202 => exth.thumb = Some(u32_at(data, 0)),
                    503 => exth.title = text(),
                    524 => exth.language = text(),
                    _ => {}
                }
                at += len.max(8);
            }
        }
    }
    h.exth = exth;
}

fn header(r0: &[u8], pdb_name: &[u8]) -> Result<Header> {
    if r0.get(16..20) != Some(b"MOBI") {
        // Plain 16-byte PalmDOC (`TEXtREAd` or `BOOKMOBI` without a MOBI subheader).
        if r0.len() < 16 || !matches!(u16_at(r0, 0), 1 | 2) {
            return Err(CoreError::Unsupported);
        }
        let mut h = Header {
            compression: u16_at(r0, 0),
            record_count: u16_at(r0, 8),
            encryption: u16_at(r0, 12),
            codepage: 0,
            utf8: false,
            palmdoc_only: true,
            version: 1,
            title: String::new(),
            locale: 0,
            resource_start: NONE,
            huff: NONE,
            huff_count: 0,
            trailing: 0,
            ncx: NONE,
            fdst: NONE,
            frag: NONE,
            skel: NONE,
            exth: Exth::default(),
        };
        let enc = encoding_for(0, 0, pdb_name);
        fill_header_meta(r0, pdb_name, &mut h, enc);
        return Ok(h);
    }
    let length = u32_at(r0, 20) as usize;
    let codepage = u32_at(r0, 28);
    let utf8 = codepage == 65001;
    let version = u32_at(r0, 36);
    let locale = u32_at(r0, 92);
    let mut h = Header {
        compression: u16_at(r0, 0),
        record_count: u16_at(r0, 8),
        encryption: u16_at(r0, 12),
        codepage,
        utf8,
        palmdoc_only: false,
        version,
        title: String::new(),
        locale,
        resource_start: u32_at(r0, 108),
        huff: u32_at(r0, 112),
        huff_count: u32_at(r0, 116),
        // Extra record data flags are a uint16 at r0 offset 242 (MOBI header + 226).
        trailing: if length >= 0xe4 { u16_at(r0, 242) } else { 0 },
        ncx: if length >= 0xe8 { u32_at(r0, 244) } else { NONE },
        fdst: if length >= 0xb4 && version >= 8 { u32_at(r0, 192) } else { NONE },
        frag: if length >= 0xf0 { u32_at(r0, 248) } else { NONE },
        skel: if length >= 0xf4 { u32_at(r0, 252) } else { NONE },
        exth: Exth::default(),
    };
    let enc = encoding_for(codepage, locale, r0);
    fill_header_meta(r0, pdb_name, &mut h, enc);
    Ok(h)
}

// MARK: Text

/// Each text record may end with extra entries (for the Kindle's own use) to cut off first.
fn strip_trailing(mut r: &[u8], flags: u32) -> &[u8] {
    for _ in 0..(flags >> 1).count_ones() {
        let mut size = 0usize;
        for &b in &r[r.len().saturating_sub(4)..] {
            if b & 0x80 != 0 {
                size = 0;
            }
            size = (size << 7) | (b & 0x7f) as usize;
        }
        r = &r[..r.len().saturating_sub(size)];
    }
    if flags & 1 != 0
        && let Some(&last) = r.last() {
            r = &r[..r.len().saturating_sub((last & 3) as usize + 1)];
        }
    r
}

/// PalmDOC's LZ77.
fn palmdoc(data: &[u8], out: &mut Vec<u8>) {
    let start = out.len();
    let mut i = 0;
    while i < data.len() {
        let c = data[i];
        i += 1;
        match c {
            0 | 9..=0x7f => out.push(c),
            1..=8 => {
                let end = (i + c as usize).min(data.len());
                out.extend_from_slice(&data[i..end]);
                i = end;
            }
            0x80..=0xbf => {
                let Some(&next) = data.get(i) else { break };
                i += 1;
                let pair = ((c as usize) << 8) | next as usize;
                let distance = (pair >> 3) & 0x7ff;
                let length = (pair & 7) + 3;
                if distance == 0 || distance > out.len() - start {
                    continue;
                }
                for _ in 0..length {
                    out.push(out[out.len() - distance]);
                }
            }
            _ => {
                out.push(b' ');
                out.push(c ^ 0x80);
            }
        }
    }
}

/// HUFF/CDIC, the Huffman coding of books made by Amazon's own tools.
struct Huff {
    table1: Vec<(bool, u32, u32)>,
    table2: Vec<(u64, u64)>,
    dictionary: Vec<(Vec<u8>, bool)>,
}

impl Huff {
    fn open(pdb: &Pdb, first: usize, count: usize) -> Result<Huff> {
        let h = pdb.record(first).filter(|r| r.starts_with(b"HUFF")).ok_or_else(|| bad("HUFF"))?;
        let (o1, o2) = (u32_at(h, 8) as usize, u32_at(h, 12) as usize);
        let table1 = (0..256).map(|i| u32_at(h, o1 + i * 4)).map(|x| (x & 0x80 != 0, x & 0x1f, x >> 8)).collect();
        let mut table2 = vec![(0, 0)];
        table2.extend((0..32).map(|i| (u32_at(h, o2 + i * 8) as u64, u32_at(h, o2 + i * 8 + 4) as u64)));
        let mut dictionary = Vec::new();
        for i in 1..count {
            let r = pdb.record(first + i).filter(|r| r.starts_with(b"CDIC")).ok_or_else(|| bad("CDIC"))?;
            let (len, entries, bits) = (u32_at(r, 4) as usize, u32_at(r, 8) as usize, u32_at(r, 12).min(31));
            let n = (1usize << bits).min(entries.saturating_sub(dictionary.len()));
            let buf = r.get(len..).unwrap_or_default();
            for k in 0..n {
                let o = u16_at(buf, k * 2) as usize;
                let x = u16_at(buf, o);
                let value = buf.get(o + 2..o + 2 + (x & 0x7fff) as usize).unwrap_or_default().to_vec();
                dictionary.push((value, x & 0x8000 != 0));
            }
        }
        Ok(Huff { table1, table2, dictionary })
    }

    fn decompress(&mut self, data: &[u8], out: &mut Vec<u8>, depth: u32) -> Result<()> {
        if depth > 32 {
            return Err(bad("HUFF depth"));
        }
        let bit_len = data.len() * 8;
        let mut i = 0;
        while i < bit_len {
            let bits = {
                let (byte, shift) = (i / 8, i % 8);
                let mut v = 0u64;
                for k in 0..5 {
                    v = (v << 8) | *data.get(byte + k).unwrap_or(&0) as u64;
                }
                (v >> (8 - shift)) & 0xffff_ffff
            };
            let (found, mut code_len, value) = self.table1[(bits >> 24) as usize];
            let mut value = value as u64;
            if !found {
                while code_len < 32 && (bits >> (32 - code_len)) < self.table2[code_len as usize].0 {
                    code_len += 1;
                }
                value = self.table2[code_len as usize].1;
            }
            if code_len == 0 {
                return Err(bad("HUFF code"));
            }
            i += code_len as usize;
            if i > bit_len {
                break;
            }
            let code = value.wrapping_sub(bits >> (32 - code_len)) as usize;
            let Some((entry, done)) = self.dictionary.get(code).cloned() else { return Err(bad("HUFF dictionary")) };
            if done {
                out.extend_from_slice(&entry);
            } else {
                let mut expanded = Vec::new();
                self.decompress(&entry, &mut expanded, depth + 1)?;
                out.extend_from_slice(&expanded);
                self.dictionary[code] = (expanded, true);
            }
            if out.len() > MAX_TEXT {
                return Err(bad("too long"));
            }
        }
        Ok(())
    }
}

fn text(pdb: &Pdb, h: &Header, start: usize) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    let mut huff = if h.compression == 17480 { Some(Huff::open(pdb, start + h.huff as usize, h.huff_count as usize)?) } else { None };
    for i in 1..=h.record_count as usize {
        let Some(r) = pdb.record(start + i) else { break };
        let r = strip_trailing(r, h.trailing);
        match (h.compression, &mut huff) {
            (1, _) => out.extend_from_slice(r),
            (2, _) => palmdoc(r, &mut out),
            (_, Some(huff)) => huff.decompress(r, &mut out, 0)?,
            _ => return Err(CoreError::Unsupported),
        }
        if out.len() > MAX_TEXT {
            return Err(bad("too long"));
        }
    }
    Ok(out)
}

// MARK: Indexes

/// Var-width integers, high bit marks the last byte.
fn varlen(b: &[u8], at: usize) -> (u32, usize) {
    let mut value = 0u32;
    let mut n = 0;
    for &byte in b.get(at..(at + 4).min(b.len())).unwrap_or_default() {
        value = (value << 7) | (byte & 0x7f) as u32;
        n += 1;
        if byte & 0x80 != 0 {
            break;
        }
    }
    (value, n.max(1))
}

struct Entry {
    name: String,
    tags: HashMap<u8, Vec<u32>>,
}

/// An INDX table (the skeleton and fragment tables, the NCX), and its strings (CNCX).
fn index(
    pdb: &Pdb,
    start: usize,
    first: u32,
    decode: &dyn Fn(&[u8]) -> String,
) -> Result<(Vec<Entry>, HashMap<u32, String>)> {
    let at = start + first as usize;
    let r = pdb.record(at).filter(|r| r.starts_with(b"INDX")).ok_or_else(|| bad("INDX"))?;
    let (len, records, cncx_count) = (u32_at(r, 4) as usize, u32_at(r, 24) as usize, u32_at(r, 52) as usize);
    let tagx = r.get(len..).filter(|t| t.starts_with(b"TAGX")).ok_or_else(|| bad("TAGX"))?;
    let tagx_len = u32_at(tagx, 4) as usize;
    let control_bytes = u32_at(tagx, 8) as usize;
    let table: Vec<[u8; 4]> = (0..tagx_len.saturating_sub(12) / 4)
        .filter_map(|i| tagx.get(12 + i * 4..16 + i * 4).map(|s| [s[0], s[1], s[2], s[3]]))
        .collect();

    let mut cncx = HashMap::new();
    for i in 0..cncx_count.min(64) {
        let Some(c) = pdb.record(at + records + 1 + i) else { break };
        let mut pos = 0;
        while pos < c.len() {
            let here = pos;
            let (size, n) = varlen(c, pos);
            pos += n;
            let end = (pos + size as usize).min(c.len());
            cncx.insert((i as u32) << 16 | here as u32, decode(&c[pos..end]));
            pos = end;
        }
    }

    let mut entries = Vec::new();
    for i in 0..records.min(4096) {
        let Some(rec) = pdb.record(at + 1 + i).filter(|r| r.starts_with(b"INDX")) else { break };
        let (idxt, count) = (u32_at(rec, 20) as usize, u32_at(rec, 24) as usize);
        for j in 0..count.min(65536) {
            let offset = u16_at(rec, idxt + 4 + 2 * j) as usize;
            let Some(&name_len) = rec.get(offset) else { continue };
            let name = rec.get(offset + 1..offset + 1 + name_len as usize).map(String::from_utf8_lossy).unwrap_or_default().into_owned();
            let begin = offset + 1 + name_len as usize;
            let mut pos = begin + control_bytes;
            let mut control = 0;
            let mut found: Vec<(u8, Option<u32>, Option<u32>, u8)> = Vec::new();
            for &[tag, values, mask, end] in &table {
                if end & 1 != 0 {
                    control += 1;
                    continue;
                }
                let v = *rec.get(begin + control).unwrap_or(&0) & mask;
                if v == mask {
                    if mask.count_ones() > 1 {
                        let (size, n) = varlen(rec, pos);
                        pos += n;
                        found.push((tag, None, Some(size), values));
                    } else {
                        found.push((tag, Some(1), None, values));
                    }
                } else if v != 0 {
                    found.push((tag, Some((v >> mask.trailing_zeros()) as u32), None, values));
                }
            }
            let mut tags = HashMap::new();
            for (tag, count, bytes, values) in found {
                let mut list = Vec::new();
                if let Some(count) = count {
                    for _ in 0..(count * values as u32).min(1024) {
                        let (v, n) = varlen(rec, pos);
                        list.push(v);
                        pos += n;
                    }
                } else if let Some(bytes) = bytes {
                    let mut read = 0;
                    while read < bytes as usize && pos < rec.len() {
                        let (v, n) = varlen(rec, pos);
                        list.push(v);
                        pos += n;
                        read += n;
                    }
                }
                tags.insert(tag, list);
            }
            entries.push(Entry { name, tags });
        }
    }
    Ok((entries, cncx))
}

// MARK: Resources

fn image_type(b: &[u8]) -> Option<(&'static str, &'static str)> {
    if b.starts_with(&[0xff, 0xd8, 0xff]) {
        Some(("jpg", "image/jpeg"))
    } else if b.starts_with(b"\x89PNG") {
        Some(("png", "image/png"))
    } else if b.starts_with(b"GIF8") {
        Some(("gif", "image/gif"))
    } else if b.starts_with(b"BM") {
        Some(("bmp", "image/bmp"))
    } else {
        None
    }
}

/// Where resource `n` (from 1) goes in the EPUB.
fn picture(pdb: &Pdb, resource_start: usize, n: u32) -> String {
    let data = n.checked_sub(1).and_then(|i| pdb.record(resource_start + i as usize)).unwrap_or_default();
    let ext = image_type(data).map(|(e, _)| e).unwrap_or("jpg");
    format!("images/{n}.{ext}")
}

struct Book {
    title: String,
    authors: Vec<String>,
    language: String,
    publisher: Option<String>,
    description: Option<String>,
    subjects: Vec<String>,
    isbn: Option<String>,
    parts: Vec<Vec<u8>>,
    /// Stylesheets and SVG pictures, by file name.
    files: Vec<(String, &'static str, Vec<u8>)>,
    /// Pictures used, by resource number (from 1).
    images: BTreeSet<u32>,
    cover: Option<u32>,
    toc: Vec<(String, u16, String)>,
}

/// Turns a MOBI or AZW3 into an EPUB, in memory.
pub fn to_epub(bytes: &[u8]) -> Result<Vec<u8>> {
    let pdb = Pdb::open(bytes)?;
    let r0 = pdb.record(0).ok_or_else(|| bad("header"))?;
    let pdb_name = pdb.name();
    let mut first = header(r0, pdb_name)?;
    if first.encryption != 0 {
        return Err(CoreError::Protected);
    }
    // The pictures are numbered from the first header's resources, KF8 half or not.
    let resource_start = first.resource_start as usize;
    let boundary = first
        .exth
        .boundary
        .filter(|&b| b != NONE && first.version < 8)
        .map(|b| b as usize)
        .or_else(|| {
            if first.version >= 8 {
                return None;
            }
            let from = (first.record_count as usize).saturating_add(1);
            let total = pdb.offsets.len().saturating_sub(1);
            (from..total).find(|&i| pdb.record(i).is_some_and(|r| r.starts_with(b"BOUNDARY")))
        });
    let kf8 = match boundary {
        Some(b) => {
            let candidate = if pdb.record(b).is_some_and(|r| r.starts_with(b"BOUNDARY")) { b + 1 } else { b };
            pdb.record(candidate)
                .and_then(|r| header(r, pdb_name).ok())
                .filter(|h| !h.palmdoc_only)
                .map(|h| (candidate, h))
        }
        None if first.version >= 8 => Some((0, header(r0, pdb_name)?)),
        None => None,
    };
    let mut book = match kf8 {
        Some((start, mut h)) if h.skel != NONE && h.frag != NONE => {
            kf8_book(&pdb, r0, pdb_name, &mut first, &mut h, start, resource_start)?
        }
        _ => mobi6_book(&pdb, r0, pdb_name, &mut first, resource_start)?,
    };
    if resource_start != NONE as usize {
        book.cover = first.exth.cover.or(first.exth.thumb).filter(|&c| c != NONE).map(|c| c + 1);
    }
    write_epub(&pdb, resource_start, &book)
}

struct Meta {
    title: String,
    authors: Vec<String>,
    language: String,
    publisher: Option<String>,
    description: Option<String>,
    subjects: Vec<String>,
    isbn: Option<String>,
}

impl Meta {
    fn of(first: &Header, h: &Header) -> Meta {
        let e = if h.exth.authors.is_empty() && h.exth.title.is_none() { &first.exth } else { &h.exth };
        let title = e.title.clone().unwrap_or_else(|| if h.title.is_empty() { first.title.clone() } else { h.title.clone() });
        let language = e.language.clone().unwrap_or_else(|| locale_language(h.locale).to_string());
        Meta {
            title: title.trim().to_string(),
            authors: e.authors.clone(),
            language,
            publisher: e.publisher.clone(),
            description: e.description.clone(),
            subjects: e.subjects.clone(),
            isbn: e.isbn.clone().or_else(|| first.exth.isbn.clone()),
        }
    }
}

/// MOBI's locale: Windows language ids, in the low byte.
fn locale_language(locale: u32) -> &'static str {
    match locale & 0xff {
        4 => "zh",
        7 => "de",
        9 => "en",
        10 => "es",
        12 => "fr",
        16 => "it",
        17 => "ja",
        18 => "ko",
        19 => "nl",
        22 => "pt",
        25 => "ru",
        _ => "und",
    }
}

fn book(meta: Meta) -> Book {
    Book {
        title: meta.title,
        authors: meta.authors,
        language: meta.language,
        publisher: meta.publisher,
        description: meta.description,
        subjects: meta.subjects,
        isbn: meta.isbn,
        parts: Vec::new(),
        files: Vec::new(),
        images: BTreeSet::new(),
        cover: None,
        toc: Vec::new(),
    }
}

/// Puts an empty anchor at each offset, outside any tag; the ids keep the offsets as given.
fn insert_anchors(text: &mut Vec<u8>, at: &BTreeSet<usize>, utf8: bool, id: impl Fn(usize) -> String) {
    let mut places: Vec<(usize, usize)> = at
        .iter()
        .map(|&offset| {
            let mut p = offset.min(text.len());
            // Inside a tag: before it instead.
            if let Some(lt) = text[..p].iter().rposition(|&b| b == b'<')
                && text[lt..p].iter().all(|&b| b != b'>') {
                    p = lt;
                }
            // Never inside a UTF-8 character.
            if utf8 {
                while p > 0 && p < text.len() && text[p] & 0xc0 == 0x80 {
                    p -= 1;
                }
            }
            (p, offset)
        })
        .collect();
    places.sort();
    // In one pass: a book can have thousands of links.
    let mut out = Vec::with_capacity(text.len() + places.len() * 24);
    let mut from = 0;
    for (p, offset) in places {
        out.extend_from_slice(&text[from..p]);
        out.extend_from_slice(format!("<a id=\"{}\"></a>", id(offset)).as_bytes());
        from = p;
    }
    out.extend_from_slice(&text[from..]);
    *text = out;
}

/// Depths from the NCX's parent links.
fn ncx_depths(entries: &[Entry]) -> Vec<u16> {
    let mut depths = vec![0u16; entries.len()];
    for i in 0..entries.len() {
        if let Some(&p) = entries[i].tags.get(&21).and_then(|v| v.first())
            && (p as usize) < i {
                depths[i] = (depths[p as usize] + 1).min(6);
            }
    }
    depths
}

// MARK: Old MOBI

static FILEPOS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"(?i)\bfilepos=['"]?0*(\d+)['"]?"#).unwrap());
static RECINDEX: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"(?i)\b(?:hi|lo)?recindex=['"]?0*(\d+)['"]?"#).unwrap());
static PAGEBREAK: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)<mbp:pagebreak[^>]*>").unwrap());
static BODY: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)<body[^>]*>").unwrap());
static HTML_TAG: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"(?i)<(?:html|body|p|div|br|h[1-6]|span|table|ul|ol|blockquote)\b").unwrap());

fn mobi6_book(
    pdb: &Pdb,
    r0: &[u8],
    pdb_name: &[u8],
    h: &mut Header,
    resource_start: usize,
) -> Result<Book> {
    let mut raw = text(pdb, h, 0)?;
    let enc = encoding_for(h.codepage, h.locale, &raw);
    if !h.utf8 && matches!(h.codepage, 0 | 1252) {
        fill_header_meta(r0, pdb_name, h, enc);
    }
    let decode = |b: &[u8]| decode_bytes(enc, b);
    let meta = Meta::of(h, h);
    let mut out = book(meta);

    if h.palmdoc_only || !HTML_TAG.is_match(&raw[..raw.len().min(32768)]) {
        let plain = decode(&raw);
        let headings = crate::txt::Headings::default();
        let analysis = crate::txt::analyze(plain.as_bytes(), Some(encoding_rs::UTF_8), &headings);
        if out.language == "und" {
            out.language = analysis.book.language.to_string();
        }
        if out.title.is_empty()
            && let Some(t) = analysis.book.title {
                out.title = clean_xml(&t);
            }
        if out.authors.is_empty()
            && let Some(a) = analysis.book.author {
                out.authors.push(clean_xml(&a));
            }
        for (i, ch) in analysis.book.chapters.iter().enumerate() {
            let mut body = String::new();
            if let Some(title) = &ch.title {
                body.push_str(&format!("<h2>{}</h2>", escape(title)));
                if !ch.continued {
                    out.toc.push((clean_xml(title), 0, format!("part{i}.html")));
                }
            }
            for p in &ch.paragraphs {
                body.push_str(&format!("<p>{}</p>", escape(p)));
            }
            if !body.is_empty() {
                out.parts.push(wrap(&body, &out.language, &[]));
            }
        }
        if !out.parts.is_empty() {
            return Ok(out);
        }
    }

    let ncx = if h.ncx != NONE { index(pdb, 0, h.ncx, &decode).ok() } else { None };

    let mut targets: BTreeSet<usize> = FILEPOS.captures_iter(&raw).filter_map(|c| num(&c, 1)).map(|n| n as usize).collect();
    if let Some((entries, _)) = &ncx {
        targets.extend(entries.iter().filter_map(|e| e.tags.get(&1).and_then(|v| v.first())).map(|&o| o as usize));
    }
    insert_anchors(&mut raw, &targets, enc == encoding_rs::UTF_8, |o| format!("filepos{o}"));

    // The body, cut at each page break.
    let body_start = BODY.find(&raw).map(|m| m.end()).unwrap_or(0);
    let body_end = find_last(&raw, b"</body>").filter(|&e| e >= body_start).unwrap_or(raw.len());
    let body = &raw[body_start..body_end];
    let mut parts: Vec<Vec<u8>> = Vec::new();
    let mut last = 0;
    for m in PAGEBREAK.find_iter(body) {
        parts.push(body[last..m.start()].to_vec());
        last = m.end();
    }
    parts.push(body[last..].to_vec());
    parts.retain(|p| p.iter().any(|b| !b.is_ascii_whitespace()));

    let mut where_is: HashMap<usize, usize> = HashMap::new();
    for (i, p) in parts.iter().enumerate() {
        for c in ANCHOR_ID.captures_iter(p) {
            if let Some(n) = num(&c, 1) {
                where_is.entry(n as usize).or_insert(i);
            }
        }
    }
    for p in parts.iter_mut() {
        let linked = FILEPOS.replace_all(p, |c: &Captures| {
            let n = num(c, 1).unwrap_or(0) as usize;
            let part = where_is.get(&n).copied().unwrap_or(0);
            format!("href=\"part{part}.html#filepos{n}\"").into_bytes()
        });
        let pictured = RECINDEX.replace_all(&linked, |c: &Captures| {
            let n = num(c, 1).unwrap_or(0);
            if resource_start != NONE as usize {
                out.images.insert(n);
            }
            format!("src=\"{}\"", picture(pdb, resource_start, n)).into_bytes()
        });
        let text = decode(&pictured);
        out.parts.push(wrap(&text, &out.language, &[]));
    }

    if let Some((entries, cncx)) = &ncx {
        let depths = ncx_depths(entries);
        for (e, depth) in entries.iter().zip(depths) {
            let (Some(&offset), Some(label)) = (e.tags.get(&1).and_then(|v| v.first()), e.tags.get(&3).and_then(|v| v.first()).and_then(|l| cncx.get(l))) else { continue };
            let part = where_is.get(&(offset as usize)).copied().unwrap_or(0);
            out.toc.push((label.clone(), depth, format!("part{part}.html#filepos{offset}")));
        }
    }
    Ok(out)
}

static ANCHOR_ID: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"<a id="filepos(\d+)"></a>"#).unwrap());

fn num(c: &Captures, i: usize) -> Option<u32> {
    std::str::from_utf8(c.get(i)?.as_bytes()).ok()?.parse().ok()
}

fn find_last(hay: &[u8], needle: &[u8]) -> Option<usize> {
    let lower: Vec<u8> = hay.iter().map(u8::to_ascii_lowercase).collect();
    lower.windows(needle.len()).rposition(|w| w == needle)
}

fn wrap(body: &str, lang: &str, styles: &[String]) -> Vec<u8> {
    let links: String = styles.iter().map(|s| format!("<link rel=\"stylesheet\" type=\"text/css\" href=\"{s}\"/>")).collect();
    format!("<!DOCTYPE html><html lang=\"{}\"><head><meta charset=\"utf-8\"/>{links}</head><body>{}</body></html>", escape(lang), clean_xml(body)).into_bytes()
}

// MARK: KF8

static KINDLE_POS: LazyLock<Regex> = LazyLock::new(|| Regex::new(r"kindle:pos:fid:([0-9A-Va-v]{4}):off:([0-9A-Va-v]{10})").unwrap());
static KINDLE_EMBED: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"kindle:embed:([0-9A-Va-v]{4})(?:\?mime=[^"')\s]*)?"#).unwrap());
static KINDLE_FLOW: LazyLock<Regex> = LazyLock::new(|| Regex::new(r#"kindle:flow:([0-9A-Va-v]{4})(?:\?mime=([^"')\s]*))?"#).unwrap());

fn base32(c: &Captures, i: usize) -> Option<u32> {
    u32::from_str_radix(std::str::from_utf8(c.get(i)?.as_bytes()).ok()?, 32).ok()
}

fn kf8_book(
    pdb: &Pdb,
    r0: &[u8],
    pdb_name: &[u8],
    first: &mut Header,
    h: &mut Header,
    start: usize,
    resource_start: usize,
) -> Result<Book> {
    let raw = text(pdb, h, start)?;
    let enc = encoding_for(h.codepage, h.locale, &raw);
    if !first.utf8 && matches!(first.codepage, 0 | 1252) {
        fill_header_meta(r0, pdb_name, first, enc);
    }
    if start != 0
        && !h.utf8
        && matches!(h.codepage, 0 | 1252)
        && let Some(kh) = pdb.record(start)
    {
        fill_header_meta(kh, pdb_name, h, enc);
    }
    let decode = |b: &[u8]| decode_bytes(enc, b);
    let meta = Meta::of(first, h);
    let mut out = book(meta);

    // Flows: 0 is the text; the rest stylesheets and SVG.
    let mut flows = vec![(0usize, raw.len())];
    if h.fdst != NONE
        && let Some(f) = pdb.record(start + h.fdst as usize).filter(|r| r.starts_with(b"FDST")) {
            let n = u32_at(f, 8) as usize;
            let list: Vec<(usize, usize)> = (0..n.min(4096))
                .map(|i| (u32_at(f, 12 + i * 8) as usize, u32_at(f, 16 + i * 8) as usize))
                .filter(|&(a, b)| a <= b && b <= raw.len())
                .collect();
            if !list.is_empty() {
                flows = list;
            }
        }

    let (frags, _) = index(pdb, start, h.frag, &decode)?;
    let (skels, _) = index(pdb, start, h.skel, &decode)?;
    struct Frag {
        insert: usize,
        length: usize,
    }
    let frags: Vec<Frag> = frags
        .iter()
        .map(|e| Frag {
            insert: e.name.trim().parse().unwrap_or(0),
            length: e.tags.get(&6).and_then(|v| v.get(1)).copied().unwrap_or(0) as usize,
        })
        .collect();

    // Each file: its skeleton with its fragments put in; where each fragment starts in it.
    let text = &raw[..flows[0].1.min(raw.len())];
    let mut parts: Vec<Vec<u8>> = Vec::new();
    let mut frag_at: Vec<(usize, usize)> = vec![(0, 0); frags.len()];
    let mut next_frag = 0usize;
    for (i, s) in skels.iter().enumerate() {
        let count = s.tags.get(&1).and_then(|v| v.first()).copied().unwrap_or(0) as usize;
        let (offset, length) = s.tags.get(&6).map(|v| (v.first().copied().unwrap_or(0) as usize, v.get(1).copied().unwrap_or(0) as usize)).unwrap_or((0, 0));
        let end = (offset + length).min(text.len());
        let mut part = text.get(offset..end).unwrap_or_default().to_vec();
        let mut from = end;
        for f in next_frag..(next_frag + count).min(frags.len()) {
            let frag = &frags[f];
            let insert = frag.insert.saturating_sub(offset).min(part.len());
            let to = (from + frag.length).min(text.len());
            let piece = text.get(from..to).unwrap_or_default().to_vec();
            frag_at[f] = (i, insert);
            part.splice(insert..insert, piece);
            from = to;
        }
        next_frag += count;
        parts.push(part);
    }
    if parts.is_empty() {
        return Err(bad("no files"));
    }

    let ncx = if h.ncx != NONE { index(pdb, start, h.ncx, &decode).ok() } else { None };
    // Every place linked to, by part.
    let resolve = |fid: u32, off: u32| -> Option<(usize, usize)> {
        let &(part, at) = frag_at.get(fid as usize)?;
        Some((part, at + off as usize))
    };
    let mut targets: Vec<BTreeSet<usize>> = vec![BTreeSet::new(); parts.len()];
    for p in &parts {
        for c in KINDLE_POS.captures_iter(p) {
            if let Some((part, at)) = base32(&c, 1).zip(base32(&c, 2)).and_then(|(f, o)| resolve(f, o)) {
                targets[part].insert(at);
            }
        }
    }
    let ncx_pos = |e: &Entry| -> Option<(usize, usize)> {
        let v = e.tags.get(&6)?;
        resolve(*v.first()?, *v.get(1)?)
    };
    if let Some((entries, _)) = &ncx {
        for (part, at) in entries.iter().filter_map(ncx_pos) {
            targets[part].insert(at);
        }
    }
    for (p, t) in parts.iter_mut().zip(&targets) {
        insert_anchors(p, t, enc == encoding_rs::UTF_8, |o| format!("pos{o}"));
    }
    let href = |(part, at): (usize, usize)| format!("part{part}.html#pos{at}");

    let mut styles_used: BTreeSet<u32> = BTreeSet::new();
    for p in parts.iter_mut() {
        let linked = KINDLE_POS.replace_all(p, |c: &Captures| {
            base32(c, 1).zip(base32(c, 2)).and_then(|(f, o)| resolve(f, o)).map(href).unwrap_or_default().into_bytes()
        });
        let flowed = KINDLE_FLOW.replace_all(&linked, |c: &Captures| {
            let n = base32(c, 1).unwrap_or(0);
            styles_used.insert(n);
            flow_name(n, c.get(2).map(|m| m.as_bytes()).unwrap_or_default()).into_bytes()
        });
        let embedded = KINDLE_EMBED.replace_all(&flowed, |c: &Captures| {
            let n = base32(c, 1).unwrap_or(0);
            if resource_start != NONE as usize {
                out.images.insert(n);
            }
            picture(pdb, resource_start, n).into_bytes()
        });
        out.parts.push(decode(&embedded).into_bytes());
    }
    for n in styles_used {
        let Some(&(a, b)) = flows.get(n as usize) else { continue };
        let data = &raw[a..b];
        let svg = data.windows(4).take(400).any(|w| w == b"<svg");
        let fixed = KINDLE_EMBED.replace_all(data, |c: &Captures| {
            let n = base32(c, 1).unwrap_or(0);
            if resource_start != NONE as usize {
                out.images.insert(n);
            }
            // Stylesheets sit beside the parts, so the same path holds.
            picture(pdb, resource_start, n).into_bytes()
        });
        let (name, mime) = if svg { (format!("flow{n}.svg"), "image/svg+xml") } else { (format!("flow{n}.css"), "text/css") };
        out.files.push((name, mime, fixed.into_owned()));
    }

    if let Some((entries, cncx)) = &ncx {
        let depths = ncx_depths(entries);
        for (e, depth) in entries.iter().zip(depths) {
            let (Some(pos), Some(label)) = (ncx_pos(e), e.tags.get(&3).and_then(|v| v.first()).and_then(|l| cncx.get(l))) else { continue };
            out.toc.push((label.clone(), depth, href(pos)));
        }
    }
    Ok(out)
}

fn flow_name(n: u32, mime: &[u8]) -> String {
    if mime.windows(3).any(|w| w == b"svg") { format!("flow{n}.svg") } else { format!("flow{n}.css") }
}

// MARK: The EPUB

fn escape(s: &str) -> String {
    clean_xml(s).replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

fn write_epub(pdb: &Pdb, resource_start: usize, book: &Book) -> Result<Vec<u8>> {
    let err = |e: zip::result::ZipError| CoreError::Io { detail: e.to_string() };
    let mut zip = ZipWriter::new(Cursor::new(Vec::new()));
    let stored = SimpleFileOptions::default().compression_method(CompressionMethod::Stored);
    let deflated = SimpleFileOptions::default().compression_method(CompressionMethod::Deflated);
    zip.start_file("mimetype", stored).map_err(err)?;
    zip.write_all(b"application/epub+zip")?;
    zip.start_file("META-INF/container.xml", deflated).map_err(err)?;
    zip.write_all(br#"<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>"#)?;

    let mut manifest = String::new();
    let mut spine = String::new();
    for (i, p) in book.parts.iter().enumerate() {
        zip.start_file(format!("OEBPS/part{i}.html"), deflated).map_err(err)?;
        zip.write_all(p)?;
        manifest.push_str(&format!("<item id=\"p{i}\" href=\"part{i}.html\" media-type=\"application/xhtml+xml\"/>"));
        spine.push_str(&format!("<itemref idref=\"p{i}\"/>"));
    }
    for (i, (name, mime, data)) in book.files.iter().enumerate() {
        zip.start_file(format!("OEBPS/{name}"), deflated).map_err(err)?;
        zip.write_all(data)?;
        manifest.push_str(&format!("<item id=\"f{i}\" href=\"{name}\" media-type=\"{mime}\"/>"));
    }
    // Pictures keep their numbers as names, the type found from their first bytes.
    let mut images: BTreeSet<u32> = book.images.clone();
    images.extend(book.cover);
    let mut cover_item = None;
    for n in images {
        let Some(data) = n.checked_sub(1).and_then(|i| pdb.record(resource_start + i as usize)) else { continue };
        let Some((_, mime)) = image_type(data) else { continue };
        let href = picture(pdb, resource_start, n);
        zip.start_file(format!("OEBPS/{href}"), stored).map_err(err)?;
        zip.write_all(data)?;
        let cover = Some(n) == book.cover;
        if cover {
            cover_item = Some(format!("i{n}"));
        }
        manifest.push_str(&format!(
            "<item id=\"i{n}\" href=\"{href}\" media-type=\"{mime}\"{}/>",
            if cover { " properties=\"cover-image\"" } else { "" }
        ));
    }

    // 目录 as an EPUB 3 nav.
    if !book.toc.is_empty() {
        let mut nav = String::from("<!DOCTYPE html><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><meta charset=\"utf-8\"/></head><body><nav epub:type=\"toc\"><ol>");
        let mut depth = 0u16;
        for (i, (label, d, href)) in book.toc.iter().enumerate() {
            let d = (*d).min(depth + 1);
            if i > 0 {
                if d > depth {
                    nav.push_str("<ol>");
                } else {
                    nav.push_str("</li>");
                    for _ in d..depth {
                        nav.push_str("</ol></li>");
                    }
                }
            }
            nav.push_str(&format!("<li><a href=\"{}\">{}</a>", escape(href), escape(label)));
            depth = d;
        }
        nav.push_str("</li>");
        for _ in 0..depth {
            nav.push_str("</ol></li>");
        }
        nav.push_str("</ol></nav></body></html>");
        zip.start_file("OEBPS/nav.xhtml", deflated).map_err(err)?;
        zip.write_all(nav.as_bytes())?;
        manifest.push_str("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
    }

    let mut meta = format!("<dc:title>{}</dc:title><dc:language>{}</dc:language>", escape(&book.title), escape(&book.language));
    for a in &book.authors {
        meta.push_str(&format!("<dc:creator>{}</dc:creator>", escape(a)));
    }
    if let Some(p) = &book.publisher {
        meta.push_str(&format!("<dc:publisher>{}</dc:publisher>", escape(p)));
    }
    if let Some(d) = &book.description {
        meta.push_str(&format!("<dc:description>{}</dc:description>", escape(d)));
    }
    for s in &book.subjects {
        meta.push_str(&format!("<dc:subject>{}</dc:subject>", escape(s)));
    }
    if let Some(i) = &book.isbn {
        meta.push_str(&format!("<dc:identifier>urn:isbn:{i}</dc:identifier>"));
    }
    if let Some(c) = &cover_item {
        meta.push_str(&format!("<meta name=\"cover\" content=\"{c}\"/>"));
    }
    let opf = format!(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">mobi</dc:identifier>{meta}</metadata><manifest>{manifest}</manifest><spine>{spine}</spine></package>"
    );
    zip.start_file("OEBPS/content.opf", deflated).map_err(err)?;
    zip.write_all(opf.as_bytes())?;
    Ok(zip.finish().map_err(err)?.into_inner())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn palmdoc_decompresses() {
        // "abcabcabc" as a literal "abc", then copy distance 3 length 6; then " x".
        let pair: u16 = (3 << 3) | (6 - 3);
        let data = [b'a', b'b', b'c', 0x80 | (pair >> 8) as u8, pair as u8, 0x80 ^ b'x'];
        let mut out = Vec::new();
        palmdoc(&data, &mut out);
        assert_eq!(out, b"abcabcabc x");
    }

    #[test]
    fn trailing_entries_are_cut() {
        // One extra entry of 3 bytes (its size in its last byte), then a multibyte tail of 2.
        let r = [b'h', b'i', 0xaa, 0x01, b'z', b'z', 0x83];
        assert_eq!(strip_trailing(&r, 0b11), b"hi");
    }

    #[test]
    fn anchors_go_outside_tags() {
        let mut t = b"<p class=\"x\">hello</p>".to_vec();
        insert_anchors(&mut t, &BTreeSet::from([3, 15]), true, |o| format!("a{o}"));
        assert_eq!(String::from_utf8(t).unwrap(), "<a id=\"a3\"></a><p class=\"x\">he<a id=\"a15\"></a>llo</p>");
    }

    /// HUFF/CDIC as Amazon's tools write it, on a made-up table: every byte an 8-bit code, code
    /// `c` naming dictionary entry `255 - c`; entry 0 ("ab") stored plain, entry 1 compressed
    /// itself (codes for "ab" and "c"), the others single letters.
    #[test]
    fn huffman_decompresses() {
        let mut huff = b"HUFF".to_vec();
        huff.extend(24u32.to_be_bytes());
        huff.extend(16u32.to_be_bytes());
        huff.extend(24u32.to_be_bytes());
        huff.extend((24 + 1024u32).to_be_bytes());
        huff.extend([0; 4]);
        for _ in 0..256 {
            huff.extend(((255u32 << 8) | 0x80 | 8).to_be_bytes());
        }
        huff.extend([0; 256]);

        let entry = |i: usize| -> (Vec<u8>, bool) {
            match i {
                0 => (b"ab".to_vec(), true),
                1 => (vec![255, 255 - b'c'], false),
                _ => (vec![i as u8], true),
            }
        };
        let mut offsets = Vec::new();
        let mut entries = Vec::new();
        for i in 0..256 {
            let (value, done) = entry(i);
            offsets.extend(((512 + entries.len()) as u16).to_be_bytes());
            entries.extend(((value.len() as u16) | if done { 0x8000 } else { 0 }).to_be_bytes());
            entries.extend(value);
        }
        let mut cdic = b"CDIC".to_vec();
        cdic.extend(16u32.to_be_bytes());
        cdic.extend(256u32.to_be_bytes());
        cdic.extend(8u32.to_be_bytes());
        cdic.extend(offsets);
        cdic.extend(entries);

        let bytes = [huff.clone(), cdic.clone()].concat();
        let pdb = Pdb { bytes: &bytes, offsets: vec![0, huff.len(), bytes.len()] };
        let mut h = Huff::open(&pdb, 0, 2).unwrap();
        let mut out = Vec::new();
        // "ab", then entry 1 ("abc"), then "x", then "ab" again.
        h.decompress(&[255, 254, 255 - b'x', 255], &mut out, 0).unwrap();
        assert_eq!(out, b"ababcxab");
        // Entry 1 now kept expanded.
        out.clear();
        h.decompress(&[254], &mut out, 0).unwrap();
        assert_eq!(out, b"abc");
    }

    fn build_pdb(name: &[u8], kind: &[u8; 8], records: &[&[u8]], trailing_zero_record: bool) -> Vec<u8> {
        let mut out = vec![0u8; 78];
        let copy_len = name.len().min(31);
        out[..copy_len].copy_from_slice(&name[..copy_len]);
        out[60..68].copy_from_slice(kind);
        let total_records = records.len() + usize::from(trailing_zero_record);
        out[76..78].copy_from_slice(&(total_records as u16).to_be_bytes());
        let table_end = 78 + total_records * 8;
        let mut offset = table_end as u32;
        for (idx, rec) in records.iter().enumerate() {
            out.extend_from_slice(&offset.to_be_bytes());
            out.extend_from_slice(&(idx as u32).to_be_bytes());
            offset += rec.len() as u32;
        }
        if trailing_zero_record {
            out.extend_from_slice(&0u32.to_be_bytes());
            out.extend_from_slice(&(records.len() as u32).to_be_bytes());
        }
        for rec in records {
            out.extend_from_slice(rec);
        }
        out
    }

    #[test]
    fn imports_palmdoc_textread_mobi_with_gbk_chapters() {
        let (gbk_title, _, _) = encoding_rs::GB18030.encode("星海航行");
        let (gbk_body, _, _) = encoding_rs::GB18030.encode("第一章 起航\n\n风帆在清晨拉起。\n\n第二章 远海\n\n灯塔渐渐远去。\n");
        let mut r0 = vec![0u8; 16];
        r0[0..2].copy_from_slice(&1u16.to_be_bytes()); // uncompressed PalmDOC
        r0[4..8].copy_from_slice(&(gbk_body.len() as u32).to_be_bytes());
        r0[8..10].copy_from_slice(&1u16.to_be_bytes()); // 1 text record
        r0[10..12].copy_from_slice(&4096u16.to_be_bytes());
        let mobi_bytes = build_pdb(&gbk_title, b"TEXtREAd", &[&r0, &gbk_body], false);

        let dir = std::env::temp_dir().join(format!("yuyue-mobi-palmdoc-{:016x}", getrandom::u64().unwrap_or(1)));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("voyage.mobi");
        std::fs::write(&src, &mobi_bytes).unwrap();
        let manifest = crate::book::import(&dir, &src).unwrap();
        assert_eq!(manifest.title, "星海航行");
        assert!(manifest.language.starts_with("zh"));
        assert!(manifest.toc.iter().any(|t| t.title.contains("第一章")));
        let first_sentences = crate::book::sentences(&dir.join(&manifest.id), manifest.start).unwrap();
        assert!(first_sentences.iter().any(|s| s.contains("风帆在清晨拉起")));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn imports_gbk_mobi6_with_nul_metadata_and_nonzero_offset_240() {
        let (gbk_title, _, _) = encoding_rs::GB18030.encode("背影\0");
        let (gbk_author, _, _) = encoding_rs::GB18030.encode("朱自清\0");
        let (gbk_html, _, _) = encoding_rs::GB18030.encode("<html><body><p>我与父亲不相见已二年余了。</p></body></html>");

        let mobi_len: u32 = 232; // standard MOBI6 header length (16 + 232 = 248)
        let mut r0 = vec![0u8; (16 + mobi_len) as usize];
        r0[0..2].copy_from_slice(&1u16.to_be_bytes()); // uncompressed
        r0[4..8].copy_from_slice(&(gbk_html.len() as u32).to_be_bytes());
        r0[8..10].copy_from_slice(&1u16.to_be_bytes()); // 1 text record
        r0[10..12].copy_from_slice(&4096u16.to_be_bytes());
        r0[16..20].copy_from_slice(b"MOBI");
        r0[20..24].copy_from_slice(&mobi_len.to_be_bytes());
        r0[28..32].copy_from_slice(&1252u32.to_be_bytes()); // mislabeled codepage common in CN MOBIs
        r0[36..40].copy_from_slice(&6u32.to_be_bytes()); // MOBI 6
        r0[92..96].copy_from_slice(&4u32.to_be_bytes()); // zh locale
        r0[108..112].copy_from_slice(&NONE.to_be_bytes()); // no image records
        r0[128..132].copy_from_slice(&0x40u32.to_be_bytes()); // EXTH flag
        // Non-zero uint16 at 240..242 before uint16 extra_data_flags = 0 at 242..244.
        r0[240..242].copy_from_slice(&0xffffu16.to_be_bytes());
        r0[242..244].copy_from_slice(&0u16.to_be_bytes());
        r0[244..248].copy_from_slice(&NONE.to_be_bytes());

        // EXTH block with author (100) and updated title (503) containing trailing NULs.
        let mut exth = b"EXTH".to_vec();
        let rec_author_len = 8 + gbk_author.len();
        let rec_title_len = 8 + gbk_title.len();
        let exth_len = 12 + rec_author_len + rec_title_len;
        exth.extend_from_slice(&(exth_len as u32).to_be_bytes());
        exth.extend_from_slice(&2u32.to_be_bytes());
        exth.extend_from_slice(&100u32.to_be_bytes());
        exth.extend_from_slice(&(rec_author_len as u32).to_be_bytes());
        exth.extend_from_slice(&gbk_author);
        exth.extend_from_slice(&503u32.to_be_bytes());
        exth.extend_from_slice(&(rec_title_len as u32).to_be_bytes());
        exth.extend_from_slice(&gbk_title);
        while exth.len() % 4 != 0 {
            exth.push(0);
        }
        r0.extend_from_slice(&exth);
        let full_name_offset = r0.len() as u32;
        r0.extend_from_slice(&gbk_title);
        r0[84..88].copy_from_slice(&full_name_offset.to_be_bytes());
        r0[88..92].copy_from_slice(&(gbk_title.len() as u32).to_be_bytes());

        let mobi_bytes = build_pdb(b"beiying", b"BOOKMOBI", &[&r0, &gbk_html], true);
        let dir = std::env::temp_dir().join(format!("yuyue-mobi-gbk-{:016x}", getrandom::u64().unwrap_or(2)));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("beiying.mobi");
        std::fs::write(&src, &mobi_bytes).unwrap();
        let manifest = crate::book::import(&dir, &src).unwrap();
        assert_eq!(manifest.title, "背影");
        assert_eq!(manifest.authors, vec!["朱自清"]);
        let sentences = crate::book::sentences(&dir.join(&manifest.id), manifest.start).unwrap();
        assert!(sentences.iter().any(|s| s.contains("我与父亲不相见已二年余了")));
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn imports_mobi6_with_oversized_section_exceeding_max_section_chars() {
        // Regression for #53: an unstyled MOBI6 chapter over 60,000 characters is split by
        // `epub::split_large`; `Assembled::joinable` must grow alongside `sections` so `final_toc`
        // never panics with `index out of bounds: the len is N but the index is N` and remaps
        // trailing chapters cleanly.
        let mut html = String::from("<html><body><h2>清晨</h2><p>河水静静流淌。</p><mbp:pagebreak/><h2>黄昏</h2>");
        let para = "<p>森林里的篝火映照着驯鹿的影子，风穿过白桦林，带来远方山峦与河流的气息，夜色缓缓降临在右岸的营地。</p>";
        for _ in 0..1350 {
            html.push_str(para);
        }
        html.push_str("<mbp:pagebreak/><h2>跋</h2><p>山峦与海洋都在远方。</p></body></html>");
        let html_bytes = html.into_bytes();

        let mobi_len: u32 = 232;
        let mut r0 = vec![0u8; (16 + mobi_len) as usize];
        r0[0..2].copy_from_slice(&1u16.to_be_bytes());
        r0[4..8].copy_from_slice(&(html_bytes.len() as u32).to_be_bytes());
        let chunks: Vec<&[u8]> = html_bytes.chunks(4096).collect();
        r0[8..10].copy_from_slice(&(chunks.len() as u16).to_be_bytes());
        r0[10..12].copy_from_slice(&4096u16.to_be_bytes());
        r0[16..20].copy_from_slice(b"MOBI");
        r0[20..24].copy_from_slice(&mobi_len.to_be_bytes());
        r0[28..32].copy_from_slice(&65001u32.to_be_bytes());
        r0[36..40].copy_from_slice(&6u32.to_be_bytes());
        r0[92..96].copy_from_slice(&4u32.to_be_bytes());
        r0[108..112].copy_from_slice(&NONE.to_be_bytes());
        r0[244..248].copy_from_slice(&NONE.to_be_bytes());
        let title_bytes = "右岸测试".as_bytes();
        let full_name_offset = r0.len() as u32;
        r0.extend_from_slice(title_bytes);
        r0[84..88].copy_from_slice(&full_name_offset.to_be_bytes());
        r0[88..92].copy_from_slice(&(title_bytes.len() as u32).to_be_bytes());

        let mut records: Vec<&[u8]> = Vec::with_capacity(1 + chunks.len());
        records.push(&r0);
        records.extend(chunks);
        let mobi_bytes = build_pdb(b"youan", b"BOOKMOBI", &records, false);

        let dir = std::env::temp_dir().join(format!("yuyue-mobi-oversized-{:016x}", getrandom::u64().unwrap_or(3)));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let src = dir.join("youan.mobi");
        std::fs::write(&src, &mobi_bytes).unwrap();
        let manifest = crate::book::import(&dir, &src).unwrap();
        assert_eq!(manifest.title, "右岸测试");
        assert!(manifest.sections.len() >= 4, "expected oversized chapter to split: {:?}", manifest.sections.len());
        assert!(manifest.sections.iter().any(|s| s.continued));
        assert_eq!(manifest.sections.last().and_then(|s| s.title.as_deref()), Some("跋"));
        let _ = std::fs::remove_dir_all(&dir);
    }

    /// The Gutenberg Kindle files of Pride and Prejudice, when they've been downloaded:
    /// `YUYUE_MOBI_DIR=/path/to/dir cargo test -- --ignored mobi`.
    #[test]
    #[ignore]
    fn reads_real_kindle_books() {
        let dir = std::env::var("YUYUE_MOBI_DIR").unwrap();
        for name in ["pp.azw3", "pp.mobi"] {
            let bytes = std::fs::read(format!("{dir}/{name}")).unwrap();
            let epub = to_epub(&bytes).unwrap();
            let path = std::env::temp_dir().join(format!("yuyue-{name}.epub"));
            std::fs::write(&path, &epub).unwrap();
            let book = crate::epub::read(&path).unwrap();
            let chapters = book.toc.iter().filter(|t| t.title.contains("CHAPTER") || t.title.contains("Chapter")).count();
            eprintln!("{name}: {:?} by {:?}, {} sections, {} toc ({chapters} chapters), cover {:?}", book.title, book.authors, book.sections.len(), book.toc.len(), book.cover);
            assert!(book.title.as_deref().is_some_and(|t| t.contains("Pride")));
            assert!(chapters >= 50, "{name}");
        }
    }
}
