//! 配书: which book a recording reads, and whether it's the same edition. Not by title — titles
//! differ and one title has many translations — but by how many runs of K sounds from a few
//! minutes of the recording are found in the book.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use super::phon::Tok;

/// Sounds per run: six syllables of Chinese, four English words.
pub fn k_for(chinese: bool) -> usize {
    if chinese { 6 } else { 4 }
}

fn hash(keys: &[Tok]) -> u64 {
    let mut h: u64 = 0xcbf29ce484222325;
    for t in keys {
        h ^= t.key as u64;
        h = h.wrapping_mul(0x100000001b3);
        h ^= 0xff;
        h = h.wrapping_mul(0x100000001b3);
    }
    h
}

/// Where each run of K sounds is in a text.
#[derive(Clone, Debug, Default)]
pub struct KIndex {
    pub k: usize,
    pub at: HashMap<u64, Vec<u32>>,
}

impl KIndex {
    pub fn build(toks: &[Tok], k: usize) -> KIndex {
        let mut at: HashMap<u64, Vec<u32>> = HashMap::new();
        if toks.len() >= k {
            for i in 0..=toks.len() - k {
                at.entry(hash(&toks[i..i + k])).or_default().push(i as u32);
            }
        }
        KIndex { k, at }
    }

    pub fn get(&self, run: &[Tok]) -> &[u32] {
        self.at.get(&hash(run)).map(Vec::as_slice).unwrap_or(&[])
    }

    /// Runs found only once.
    pub fn unique(&self, run: &[Tok]) -> Option<u32> {
        match self.get(run) {
            [one] => Some(*one),
            _ => None,
        }
    }
}

/// The share of a sample's runs that are found in the book.
pub fn hit_rate(book: &KIndex, sample: &[Tok]) -> f32 {
    let k = book.k;
    if sample.len() < k {
        return 0.0;
    }
    let n = sample.len() - k + 1;
    let hits = (0..n).filter(|&i| !book.get(&sample[i..i + k]).is_empty()).count();
    hits as f32 / n as f32
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum MatchKind {
    /// The same edition: sentence by sentence.
    Same,
    /// Revised or abridged: sentence by sentence, gaps marked.
    Revised,
    /// Another translation or a retelling: by chapter only.
    ChapterOnly,
    /// The recording's own text (no e-book).
    Transcript,
    /// Read from a book that carries its own timings (EPUB 3 media overlays).
    Imported,
    NotThis,
}

pub fn classify(rate: f32) -> MatchKind {
    if rate >= 0.5 {
        MatchKind::Same
    } else if rate >= 0.2 {
        MatchKind::Revised
    } else if rate >= 0.03 {
        MatchKind::ChapterOnly
    } else {
        MatchKind::NotThis
    }
}

/// The best book for a sample among several: (index, rate, kind). Two editions within 0.1 of
/// each other: the higher wins, and the app lets the user switch.
pub fn best<'a>(books: impl IntoIterator<Item = &'a KIndex>, sample: &[Tok]) -> Option<(usize, f32, MatchKind)> {
    let mut rates: Vec<(usize, f32)> = books.into_iter().enumerate().map(|(i, b)| (i, hit_rate(b, sample))).collect();
    rates.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
    let (i, r) = *rates.first()?;
    let kind = classify(r);
    (kind != MatchKind::NotThis).then_some((i, r, kind))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audiobook::phon::phon;

    #[test]
    fn the_same_text_matches_and_another_doesnt() {
        let book = phon("却说黛玉自那日弃舟登岸时，便有荣国府打发了轿子并拉行李的车辆久候了。这林黛玉常听得母亲说过，他外祖母家与别家不同。", true);
        let ix = KIndex::build(&book, 6);
        // Heard with homophones.
        let heard = phon("却说代玉自那日弃舟登岸时便有荣国府打发了轿子并拉行李的车辆久候了", true);
        assert_eq!(classify(hit_rate(&ix, &heard)), MatchKind::Same);
        let other = phon("话说天下大势，分久必合，合久必分。周末七国分争，并入于秦。", true);
        assert_eq!(classify(hit_rate(&ix, &other)), MatchKind::NotThis);
    }
}
