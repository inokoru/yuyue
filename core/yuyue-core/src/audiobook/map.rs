//! 对照表: sentence ↔ seconds for a whole book, small enough to sync (a few hundred KB packed
//! for a long book). The player knows only seconds, the reader only sentences; they meet here.
//!
//! Rows are in the book's order. A recording that reads chapters in another order (the
//! afterword first) is still found by time through a second index sorted by start.

use std::io::{Read, Write};

use serde::{Deserialize, Serialize};

use super::grade::{SyncBy, Graded};
use super::matchbook::MatchKind;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SyncRow {
    pub section: u32,
    pub sentence: u32,
    pub t0_ms: u32,
    pub t1_ms: u32,
    pub by: SyncBy,
    /// 0–255.
    pub conf: u8,
    pub no_gap: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct ExtraSpan {
    pub t0: f64,
    pub t1: f64,
    pub kind: ExtraKind,
    pub place: ExtraPlace,
    pub text: Option<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ExtraKind {
    Music,
    Speech,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ExtraPlace {
    Head,
    Tail,
    Middle,
    ChapterGap,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SyncChapter {
    pub title: String,
    pub t0: f64,
    pub book_section: Option<u32>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SyncMap {
    pub audio_id: String,
    pub book_id: String,
    pub match_kind: MatchKind,
    pub durations: Vec<f64>,
    pub chapters: Vec<SyncChapter>,
    pub rows: Vec<SyncRow>,
    pub extras: Vec<ExtraSpan>,
    /// Aligner version, book text digest, model names.
    pub version: String,
}

/// Where a moment of the recording falls.
#[derive(Clone, Debug, PartialEq, uniffi::Enum)]
pub enum SyncHit {
    /// In a sentence, this far through it (0–1).
    Sentence { section: u32, sentence: u32, through: f32 },
    /// In something not in the book.
    Extra { index: u32 },
    /// Between sentences: after this one.
    Gap { section: u32, sentence: u32 },
    Nothing,
}

impl SyncMap {
    #[allow(clippy::too_many_arguments)]
    pub fn from_graded(audio_id: &str, book_id: &str, kind: MatchKind, durations: Vec<f64>, g: &[Graded], extras: Vec<ExtraSpan>, chapters: Vec<SyncChapter>, version: &str) -> SyncMap {
        let rows = g
            .iter()
            .map(|x| SyncRow {
                section: x.section,
                sentence: x.sentence,
                t0_ms: if x.t0 >= 0.0 { (x.t0 * 1000.0).round() as u32 } else { 0 },
                t1_ms: if x.t1 >= 0.0 { (x.t1 * 1000.0).round() as u32 } else { 0 },
                by: x.by,
                conf: (x.conf.clamp(0.0, 1.0) * 255.0).round() as u8,
                no_gap: x.no_gap,
            })
            .collect();
        SyncMap { audio_id: audio_id.into(), book_id: book_id.into(), match_kind: kind, durations, chapters, rows, extras, version: version.into() }
    }

    fn timed(&self) -> Vec<usize> {
        let mut v: Vec<usize> = (0..self.rows.len()).filter(|&i| matches!(self.rows[i].by, SyncBy::Auto | SyncBy::Spread | SyncBy::User)).collect();
        v.sort_by_key(|&i| self.rows[i].t0_ms);
        v
    }

    pub fn time_to_loc(&self, t: f64) -> SyncHit {
        let ms = (t * 1000.0).max(0.0) as u32;
        if let Some(i) = self.extras.iter().position(|e| t >= e.t0 && t < e.t1) {
            return SyncHit::Extra { index: i as u32 };
        }
        let idx = self.timed();
        let k = idx.partition_point(|&i| self.rows[i].t0_ms <= ms);
        if k == 0 {
            return SyncHit::Nothing;
        }
        let r = &self.rows[idx[k - 1]];
        if ms < r.t1_ms {
            let through = (ms - r.t0_ms) as f32 / (r.t1_ms - r.t0_ms).max(1) as f32;
            SyncHit::Sentence { section: r.section, sentence: r.sentence, through }
        } else {
            SyncHit::Gap { section: r.section, sentence: r.sentence }
        }
    }

    /// Where a sentence starts; one not read gives the next read one's start.
    pub fn loc_to_time(&self, section: u32, sentence: u32) -> Option<f64> {
        let i = self.rows.iter().position(|r| r.section == section && r.sentence == sentence)?;
        self.rows[i..].iter().find(|r| matches!(r.by, SyncBy::Auto | SyncBy::Spread | SyncBy::User)).map(|r| r.t0_ms as f64 / 1000.0)
    }

    /// The sentence around a moment, for 按句跳 and 念完这句.
    pub fn sentence_bounds(&self, t: f64) -> Option<(f64, f64)> {
        match self.time_to_loc(t) {
            SyncHit::Sentence { section, sentence, .. } | SyncHit::Gap { section, sentence } => {
                let r = self.rows.iter().find(|r| r.section == section && r.sentence == sentence)?;
                Some((r.t0_ms as f64 / 1000.0, r.t1_ms as f64 / 1000.0))
            }
            _ => None,
        }
    }

    /// The sentence before or after the one at `t`, by time.
    pub fn step(&self, t: f64, forward: bool) -> Option<f64> {
        let ms = (t * 1000.0) as u32;
        let idx = self.timed();
        let k = idx.partition_point(|&i| self.rows[i].t0_ms <= ms);
        if forward {
            idx.get(k).map(|&i| self.rows[i].t0_ms as f64 / 1000.0)
        } else {
            // Within the first second of a sentence, go to the one before; else to this one's start.
            let cur = k.checked_sub(1)?;
            let r = &self.rows[idx[cur]];
            if ms.saturating_sub(r.t0_ms) > 1000 {
                Some(r.t0_ms as f64 / 1000.0)
            } else {
                cur.checked_sub(1).map(|p| self.rows[idx[p]].t0_ms as f64 / 1000.0).or(Some(r.t0_ms as f64 / 1000.0))
            }
        }
    }

    /// Packed for the store: the rows as varint deltas, the rest as JSON, all deflated.
    pub fn pack(&self) -> Vec<u8> {
        let mut rows = Vec::new();
        let mut last = 0i64;
        let mut sec = 0u32;
        for r in &self.rows {
            varint(&mut rows, r.section.wrapping_sub(sec) as u64);
            sec = r.section;
            varint(&mut rows, r.sentence as u64);
            let d = r.t0_ms as i64 - last;
            varint(&mut rows, zig(d));
            last = r.t0_ms as i64;
            varint(&mut rows, r.t1_ms.saturating_sub(r.t0_ms) as u64);
            rows.push(match r.by {
                SyncBy::Auto => 0,
                SyncBy::Spread => 1,
                SyncBy::User => 2,
                SyncBy::Unread => 3,
                SyncBy::Chapter => 4,
            } | if r.no_gap { 8 } else { 0 });
            rows.push(r.conf);
        }
        let head = serde_json::to_vec(&SyncMap { rows: vec![], ..self.clone() }).unwrap_or_default();
        let mut raw = Vec::new();
        varint(&mut raw, head.len() as u64);
        raw.extend(head);
        raw.extend(rows);
        let mut z = flate2::write::DeflateEncoder::new(Vec::new(), flate2::Compression::best());
        z.write_all(&raw).ok();
        z.finish().unwrap_or_default()
    }

    pub fn unpack(b: &[u8]) -> Option<SyncMap> {
        let mut raw = Vec::new();
        flate2::read::DeflateDecoder::new(b).read_to_end(&mut raw).ok()?;
        let mut p = 0usize;
        let hl = read_varint(&raw, &mut p)? as usize;
        let mut m: SyncMap = serde_json::from_slice(raw.get(p..p + hl)?).ok()?;
        p += hl;
        let mut last = 0i64;
        let mut sec = 0u32;
        while p < raw.len() {
            sec = sec.wrapping_add(read_varint(&raw, &mut p)? as u32);
            let sentence = read_varint(&raw, &mut p)? as u32;
            last += unzig(read_varint(&raw, &mut p)?);
            let len = read_varint(&raw, &mut p)? as u32;
            let flags = *raw.get(p)?;
            let conf = *raw.get(p + 1)?;
            p += 2;
            let by = match flags & 7 {
                0 => SyncBy::Auto,
                1 => SyncBy::Spread,
                2 => SyncBy::User,
                3 => SyncBy::Unread,
                _ => SyncBy::Chapter,
            };
            m.rows.push(SyncRow { section: sec, sentence, t0_ms: last as u32, t1_ms: last as u32 + len, by, conf, no_gap: flags & 8 != 0 });
        }
        Some(m)
    }
}

fn zig(v: i64) -> u64 {
    ((v << 1) ^ (v >> 63)) as u64
}

fn unzig(v: u64) -> i64 {
    ((v >> 1) as i64) ^ -((v & 1) as i64)
}

fn varint(out: &mut Vec<u8>, mut v: u64) {
    loop {
        let b = (v & 0x7f) as u8;
        v >>= 7;
        if v == 0 {
            out.push(b);
            return;
        }
        out.push(b | 0x80);
    }
}

fn read_varint(b: &[u8], p: &mut usize) -> Option<u64> {
    let mut v = 0u64;
    let mut shift = 0;
    loop {
        let x = *b.get(*p)?;
        *p += 1;
        v |= ((x & 0x7f) as u64) << shift;
        if x & 0x80 == 0 {
            return Some(v);
        }
        shift += 7;
        if shift > 63 {
            return None;
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn map() -> SyncMap {
        let g: Vec<Graded> = (0..1000)
            .map(|i| Graded { section: i / 100, sentence: i % 100, t0: i as f64 * 3.0, t1: i as f64 * 3.0 + 2.6, by: if i % 50 == 7 { SyncBy::Unread } else { SyncBy::Auto }, conf: 0.95, no_gap: false })
            .collect();
        SyncMap::from_graded("a", "b", MatchKind::Same, vec![3000.0], &g, vec![ExtraSpan { t0: 2.7, t1: 2.9, kind: ExtraKind::Speech, place: ExtraPlace::Middle, text: None }], vec![], "1")
    }

    #[test]
    fn packs_small_and_unpacks_the_same() {
        let m = map();
        let p = m.pack();
        assert!(p.len() < 8_000, "{}", p.len());
        assert_eq!(SyncMap::unpack(&p).unwrap(), m);
    }

    #[test]
    fn time_and_place_meet() {
        let m = map();
        assert!(matches!(m.time_to_loc(3.5), SyncHit::Sentence { section: 0, sentence: 1, through } if (through - 0.5 / 2.6).abs() < 1e-4));
        assert_eq!(m.time_to_loc(2.8), SyncHit::Extra { index: 0 });
        assert!(matches!(m.time_to_loc(5.8), SyncHit::Gap { section: 0, sentence: 1 }));
        // An unread sentence gives the next one's start.
        assert_eq!(m.loc_to_time(0, 7), Some(24.0));
        assert_eq!(m.step(3.5, true), Some(6.0));
        assert_eq!(m.step(3.5, false), Some(0.0));
        assert_eq!(m.step(4.5, false), Some(3.0));
        assert_eq!(m.sentence_bounds(3.5), Some((3.0, 5.6)));
    }
}
