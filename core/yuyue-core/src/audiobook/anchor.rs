//! 粗对齐: the book's sounds laid along the transcript's, to a second or two.
//!
//! 1. Each chapter of the recording votes for the book's sections its sounds are found in, so
//!    chapters read out of order (an afterword first) are each placed on their own.
//! 2. Within a chapter, runs of K sounds found exactly once on each side are anchors; those out
//!    of order are dropped (the longest increasing run is kept).
//! 3. Between two anchors, the stretch of transcript and the stretch of book are aligned with
//!    gaps (affine: opening a gap costs more than going on with one), in a band around the
//!    diagonal.
//!
//! Then each sentence knows how much of it was heard and roughly when. A sentence hardly heard
//! in hardly any time is unread (a note, a cut); transcript nothing in the book matched, three
//! seconds or more, is extra (an ad, a reader's word).

use serde::{Deserialize, Serialize};

use super::matchbook::KIndex;
use super::phon::{Tok, same};

/// The book as sounds: each with the sentence it's in.
#[derive(Clone, Debug, Default)]
pub struct BookToks {
    pub toks: Vec<Tok>,
    /// Index into `sentences` for each tok.
    pub sent_of: Vec<u32>,
    /// (section, sentence, first tok, end tok).
    pub sentences: Vec<(u32, u32, u32, u32)>,
}

impl BookToks {
    pub fn build(sections: &[Vec<String>], chinese: bool) -> BookToks {
        let mut b = BookToks::default();
        for (si, ss) in sections.iter().enumerate() {
            for (i, s) in ss.iter().enumerate() {
                let first = b.toks.len() as u32;
                let t = super::phon::phon(s, chinese);
                let idx = b.sentences.len() as u32;
                b.sent_of.extend(std::iter::repeat_n(idx, t.len()));
                b.toks.extend(t);
                b.sentences.push((si as u32, i as u32, first, b.toks.len() as u32));
            }
        }
        b
    }

    /// The tok range of sections [from, to).
    pub fn range_of(&self, from: u32, to: u32) -> (usize, usize) {
        let a = self.sentences.iter().find(|s| s.0 >= from).map(|s| s.2 as usize).unwrap_or(self.toks.len());
        let b = self.sentences.iter().find(|s| s.0 >= to).map(|s| s.2 as usize).unwrap_or(self.toks.len());
        (a, b)
    }
}

/// What the transcript is made of: the transcriber's words (compared by their sounds), or the
/// alignment model's letters, one a time (a device with no transcriber hears the recording
/// through the model it aligns with: pinyin or English letters, no word breaks).
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq, Serialize, Deserialize)]
pub enum Units {
    #[default]
    Sounds,
    Letters,
}

/// Runs of this many letters are anchors (about six syllables of pinyin, four English words).
pub const LETTER_K: usize = 16;

impl Units {
    pub fn k(self, chinese: bool) -> usize {
        match self {
            Units::Sounds => super::matchbook::k_for(chinese),
            Units::Letters => LETTER_K,
        }
    }

    /// Transcript nothing in the book took, this many units or more, is talk outside it.
    pub fn extra_min(self) -> usize {
        match self {
            Units::Sounds => 8,
            Units::Letters => 24,
        }
    }
}

fn letter_tok(l: usize) -> Tok {
    Tok { keys: vec![l as u32], key: l as u32, src: 0, light: false, word: None }
}

impl BookToks {
    /// The book as the alignment model's letters.
    pub fn of(sections: &[Vec<String>], chinese: bool, units: Units) -> BookToks {
        if units == Units::Sounds {
            return BookToks::build(sections, chinese);
        }
        let mut b = BookToks::default();
        for (si, ss) in sections.iter().enumerate() {
            for (i, s) in ss.iter().enumerate() {
                let first = b.toks.len() as u32;
                let t: Vec<Tok> = crate::forced::letters(s, chinese).into_iter().map(letter_tok).collect();
                let idx = b.sentences.len() as u32;
                b.sent_of.extend(std::iter::repeat_n(idx, t.len()));
                b.toks.extend(t);
                b.sentences.push((si as u32, i as u32, first, b.toks.len() as u32));
            }
        }
        b
    }
}

impl HeardToks {
    pub fn of(words: &[HeardWord], chinese: bool, units: Units) -> HeardToks {
        if units == Units::Sounds {
            return HeardToks::build(words, chinese);
        }
        let mut h = HeardToks::default();
        for (wi, w) in words.iter().enumerate() {
            for c in w.text.chars() {
                if let Some(l) = crate::forced::LETTERS.chars().position(|x| x == c).filter(|&i| i != 0) {
                    h.toks.push(letter_tok(l));
                    h.t0.push(w.t0);
                    h.t1.push(w.t1);
                    h.word.push(wi as u32);
                }
            }
        }
        h
    }
}

/// The model's letters heard over a stretch, as a transcript: each frame's likeliest letter, a
/// run of the same one counted once, blanks dropped. `em` is `frames` rows of the model's
/// letters covering `seconds` from `from`.
pub fn heard_letters(em: &[f32], frames: usize, seconds: f64, from: f64) -> Vec<HeardWord> {
    let classes = crate::forced::LETTERS.chars().count();
    if frames == 0 || em.len() < frames * classes {
        return Vec::new();
    }
    let letters: Vec<char> = crate::forced::LETTERS.chars().collect();
    let dt = seconds / frames as f64;
    let mut out: Vec<HeardWord> = Vec::new();
    let mut prev = 0usize;
    for f in 0..frames {
        let row = &em[f * classes..(f + 1) * classes];
        let best = (0..classes).max_by(|&a, &b| row[a].total_cmp(&row[b])).unwrap_or(0);
        if best != 0 {
            let t = from + f as f64 * dt;
            if best == prev && let Some(last) = out.last_mut() {
                last.t1 = t + dt;
            } else {
                out.push(HeardWord { text: letters[best].to_string(), t0: t, t1: t + dt });
            }
        }
        prev = best;
    }
    out
}

/// One word the transcriber heard.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct HeardWord {
    pub text: String,
    pub t0: f64,
    pub t1: f64,
}

/// The transcript as sounds, each with its time.
#[derive(Clone, Debug, Default)]
pub struct HeardToks {
    pub toks: Vec<Tok>,
    pub t0: Vec<f64>,
    pub t1: Vec<f64>,
    /// Which word each came from (for extras' text).
    pub word: Vec<u32>,
}

impl HeardToks {
    pub fn build(words: &[HeardWord], chinese: bool) -> HeardToks {
        let mut h = HeardToks::default();
        for (wi, w) in words.iter().enumerate() {
            let t = super::phon::phon(&w.text, chinese);
            let n = t.len().max(1) as f64;
            for (k, tok) in t.into_iter().enumerate() {
                let a = w.t0 + (w.t1 - w.t0) * k as f64 / n;
                let b = w.t0 + (w.t1 - w.t0) * (k + 1) as f64 / n;
                h.toks.push(tok);
                h.t0.push(a);
                h.t1.push(b);
                h.word.push(wi as u32);
            }
        }
        h
    }

    pub fn at(&self, t: f64) -> usize {
        self.t0.partition_point(|x| *x < t)
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub enum Class {
    Aligned,
    Weak,
    Unread,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct SentCoarse {
    pub section: u32,
    pub sentence: u32,
    pub t0: f64,
    pub t1: f64,
    pub coverage: f32,
    pub class: Class,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Extra {
    pub t0: f64,
    pub t1: f64,
    pub text: String,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Coarse {
    pub sentences: Vec<SentCoarse>,
    pub extras: Vec<Extra>,
    /// Seconds a sound takes, read off the anchors.
    pub sec_per_tok: f64,
}

/// Scores of the alignment between anchors.
pub mod score {
    pub const SAME: f32 = 2.0;
    pub const DIFF: f32 = -1.0;
    pub const GAP_OPEN: f32 = -2.0;
    pub const GAP_EXTEND: f32 = -0.5;
}

/// The longest increasing run of book places (anchors kept in order); `pairs` sorted by heard.
fn lis(pairs: &[(u32, u32)]) -> Vec<(u32, u32)> {
    let mut tails: Vec<usize> = Vec::new();
    let mut prev: Vec<Option<usize>> = vec![None; pairs.len()];
    for (i, p) in pairs.iter().enumerate() {
        let pos = tails.partition_point(|&t| pairs[t].1 < p.1);
        if pos > 0 {
            prev[i] = Some(tails[pos - 1]);
        }
        if pos == tails.len() {
            tails.push(i);
        } else {
            tails[pos] = i;
        }
    }
    let mut out = Vec::new();
    let mut cur = tails.last().copied();
    while let Some(i) = cur {
        out.push(pairs[i]);
        cur = prev[i];
    }
    out.reverse();
    out
}

/// Anchors between a stretch of transcript [ha, hb) and of book [ba, bb): (heard, book) places of
/// the first sound of each run found once on each side, in order on both.
pub fn anchors(heard: &[Tok], ha: usize, hb: usize, book: &[Tok], ba: usize, bb: usize, k: usize) -> Vec<(u32, u32)> {
    if hb - ha < k || bb - ba < k {
        return Vec::new();
    }
    let hix = KIndex::build(&heard[ha..hb], k);
    let bix = KIndex::build(&book[ba..bb], k);
    let mut pairs: Vec<(u32, u32)> = Vec::new();
    for i in 0..=(hb - ha - k) {
        let run = &heard[ha + i..ha + i + k];
        if let (Some(_), Some(b)) = (hix.unique(run), bix.unique(run)) {
            // Hash equal isn't sound equal: check.
            if run.iter().zip(&book[ba + b as usize..]).all(|(x, y)| same(x, y)) {
                pairs.push(((ha + i) as u32, ba as u32 + b));
            }
        }
    }
    lis(&pairs)
}

/// Global alignment with affine gaps of `a` (heard) and `b` (book), in a band. For each sound of
/// `b`, the sound of `a` it matched.
pub fn align(a: &[Tok], b: &[Tok]) -> Vec<Option<u32>> {
    let (n, m) = (a.len(), b.len());
    if n == 0 || m == 0 {
        return vec![None; m];
    }
    let band = (n.abs_diff(m) + 20).max(50);
    let neg = f32::NEG_INFINITY;
    let w = m + 1;
    // M: match/mismatch at (i, j); X: gap in b (a's sound left out); Y: gap in a (b's left out).
    let mut mm = vec![neg; (n + 1) * w];
    let mut xx = vec![neg; (n + 1) * w];
    let mut yy = vec![neg; (n + 1) * w];
    mm[0] = 0.0;
    let half = |t: &Tok, v: f32| if t.light { v * 0.5 } else { v };
    let inside = |i: usize, j: usize| -> bool {
        let jd = (i as f64 * m as f64 / n as f64) as isize;
        (j as isize - jd).unsigned_abs() <= band
    };
    for i in 0..=n {
        for j in 0..=m {
            if (i, j) == (0, 0) || !inside(i, j) {
                continue;
            }
            let k = i * w + j;
            if i > 0 && j > 0 {
                let s = if same(&a[i - 1], &b[j - 1]) { score::SAME } else { half(&b[j - 1], score::DIFF) };
                let p = (i - 1) * w + j - 1;
                mm[k] = mm[p].max(xx[p]).max(yy[p]) + s;
            }
            if i > 0 {
                let p = (i - 1) * w + j;
                let o = half(&a[i - 1], score::GAP_OPEN);
                let e = half(&a[i - 1], score::GAP_EXTEND);
                xx[k] = (mm[p] + o).max(yy[p] + o).max(xx[p] + e);
            }
            if j > 0 {
                let p = i * w + j - 1;
                let o = half(&b[j - 1], score::GAP_OPEN);
                let e = half(&b[j - 1], score::GAP_EXTEND);
                yy[k] = (mm[p] + o).max(xx[p] + o).max(yy[p] + e);
            }
        }
    }
    // Trace back.
    let mut out = vec![None; m];
    let (mut i, mut j) = (n, m);
    let k = n * w + m;
    let mut state = if mm[k] >= xx[k] && mm[k] >= yy[k] { 0 } else if xx[k] >= yy[k] { 1 } else { 2 };
    while i > 0 || j > 0 {
        let k = i * w + j;
        match state {
            0 if i > 0 && j > 0 => {
                if same(&a[i - 1], &b[j - 1]) {
                    out[j - 1] = Some((i - 1) as u32);
                }
                let p = (i - 1) * w + j - 1;
                state = if mm[p] >= xx[p] && mm[p] >= yy[p] { 0 } else if xx[p] >= yy[p] { 1 } else { 2 };
                i -= 1;
                j -= 1;
            }
            1 if i > 0 => {
                let p = (i - 1) * w + j;
                let o = half(&a[i - 1], score::GAP_OPEN);
                let e = half(&a[i - 1], score::GAP_EXTEND);
                state = if (xx[k] - (xx[p] + e)).abs() < 1e-4 { 1 } else if (xx[k] - (mm[p] + o)).abs() < 1e-4 { 0 } else { 2 };
                i -= 1;
            }
            2 if j > 0 => {
                let p = i * w + j - 1;
                let o = half(&b[j - 1], score::GAP_OPEN);
                let e = half(&b[j - 1], score::GAP_EXTEND);
                state = if (yy[k] - (yy[p] + e)).abs() < 1e-4 { 2 } else if (yy[k] - (mm[p] + o)).abs() < 1e-4 { 0 } else { 1 };
                j -= 1;
            }
            _ => {
                if i > 0 {
                    i -= 1;
                    state = 1;
                } else {
                    j -= 1;
                    state = 2;
                }
            }
        }
    }
    out
}

/// The largest stretch aligned with DP at once (heard × book sounds); larger ones are left to
/// the anchors around them.
const MAX_CELLS: usize = 12_000_000;

/// Which book sections a stretch of transcript reads: the run from the first to the last
/// section whose share of hits, for its length, is at least a fifth of the densest one's — so a
/// short chapter counts as much as a long one.
pub fn sections_for(heard: &[Tok], book: &BookToks, index: &KIndex, sections: u32) -> Option<(u32, u32)> {
    let k = index.k;
    if heard.len() < k {
        return None;
    }
    let mut votes = vec![0u32; sections as usize];
    let mut size = vec![0u32; sections as usize];
    for s in &book.sentences {
        size[s.0 as usize] += s.3 - s.2;
    }
    let step = (heard.len() / 4000).max(1);
    let mut i = 0;
    while i + k <= heard.len() {
        let hits = index.get(&heard[i..i + k]);
        if hits.len() == 1 {
            let s = book.sentences[book.sent_of[hits[0] as usize] as usize].0;
            votes[s as usize] += 1;
        }
        i += step;
    }
    let density: Vec<f64> = votes.iter().zip(&size).map(|(&v, &n)| if n == 0 { 0.0 } else { v as f64 / n as f64 }).collect();
    let best = density.iter().cloned().fold(0.0, f64::max);
    if best == 0.0 {
        return None;
    }
    // A section needs a few hits too, so a stray one in a tiny section doesn't count.
    let ok = |i: usize| density[i] * 5.0 >= best && votes[i] >= 2;
    let first = (0..density.len()).find(|&i| ok(i))? as u32;
    let last = (0..density.len()).rfind(|&i| ok(i))? as u32;
    Some((first, last + 1))
}

/// Lays the transcript along the book. `chunks` are the recording's chapters as transcript
/// sound ranges (one chunk for the whole when there are none).
pub fn coarse(book: &BookToks, heard: &HeardToks, chunks: &[(usize, usize)], chinese: bool) -> Coarse {
    coarse_in(book, heard, chunks, chinese, Units::Sounds)
}

/// `coarse`, on a transcript of these units.
pub fn coarse_in(book: &BookToks, heard: &HeardToks, chunks: &[(usize, usize)], chinese: bool, units: Units) -> Coarse {
    let k = units.k(chinese);
    let index = KIndex::build(&book.toks, k);
    let nsec = book.sentences.last().map(|s| s.0 + 1).unwrap_or(0);
    let mut matched: Vec<Option<u32>> = vec![None; book.toks.len()];
    let mut used = vec![false; heard.toks.len()];
    let mut anchor_pairs: Vec<(u32, u32)> = Vec::new();
    for &(ha, hb) in chunks {
        let Some((s0, s1)) = sections_for(&heard.toks[ha..hb], book, &index, nsec) else { continue };
        let (ba, bb) = book.range_of(s0, s1);
        let mut an = anchors(&heard.toks, ha, hb, &book.toks, ba, bb, k);
        // Thin stretches: look again with shorter runs.
        let mut extra: Vec<(u32, u32)> = Vec::new();
        let bounds: Vec<(u32, u32)> = std::iter::once((ha as u32, ba as u32)).chain(an.iter().copied()).chain(std::iter::once((hb as u32, bb as u32))).collect();
        for w in bounds.windows(2) {
            let (h0, b0) = w[0];
            let (h1, b1) = w[1];
            let secs = heard.t0.get(h1 as usize).copied().unwrap_or(f64::MAX) - heard.t0.get(h0 as usize).copied().unwrap_or(0.0);
            if (secs > 90.0 || b1.saturating_sub(b0) > 600) && k > 2 {
                extra.extend(anchors(&heard.toks, h0 as usize, h1 as usize, &book.toks, b0 as usize, b1 as usize, k / 2));
            }
        }
        if !extra.is_empty() {
            an.extend(extra);
            an.sort();
            an = lis(&an);
        }
        anchor_pairs.extend(an.iter().copied());
        // Anchors themselves match their K sounds.
        for &(h, b) in &an {
            for d in 0..k {
                let (hi, bi) = (h as usize + d, b as usize + d);
                if hi < hb && bi < bb && matched[bi].is_none() && !used[hi] {
                    matched[bi] = Some(hi as u32);
                    used[hi] = true;
                }
            }
        }
        // Between anchors (and from the chunk's edges to the first and last).
        let bounds: Vec<(usize, usize)> = std::iter::once((ha, ba)).chain(an.iter().map(|&(h, b)| (h as usize + k, b as usize + k))).collect();
        let ends: Vec<(usize, usize)> = an.iter().map(|&(h, b)| (h as usize, b as usize)).chain(std::iter::once((hb, bb))).collect();
        for ((h0, b0), (h1, b1)) in bounds.into_iter().zip(ends) {
            if h1 <= h0 || b1 <= b0 {
                continue;
            }
            if (h1 - h0) * (b1 - b0) > MAX_CELLS {
                continue;
            }
            let got = align(&heard.toks[h0..h1], &book.toks[b0..b1]);
            for (j, g) in got.into_iter().enumerate() {
                if let Some(i) = g {
                    let hi = h0 + i as usize;
                    if !used[hi] && matched[b0 + j].is_none() {
                        matched[b0 + j] = Some(hi as u32);
                        used[hi] = true;
                    }
                }
            }
        }
    }
    // Seconds a sound takes, from the anchors.
    let sec_per_tok = {
        let mut num = 0.0;
        let mut den = 0.0;
        for w in anchor_pairs.windows(2) {
            let dh = heard.t0[w[1].0 as usize] - heard.t0[w[0].0 as usize];
            let db = (w[1].1 - w[0].1) as f64;
            if db > 0.0 && dh > 0.0 && dh / db < 2.0 {
                num += dh;
                den += db;
            }
        }
        if den > 0.0 { num / den } else { 0.25 }
    };
    let mut sentences = Vec::with_capacity(book.sentences.len());
    for &(section, sentence, a, b) in &book.sentences {
        let toks = &book.toks[a as usize..b as usize];
        let weight: f32 = toks.iter().map(|t| if t.light { 0.5 } else { 1.0 }).sum();
        let hit: f32 = toks.iter().zip(&matched[a as usize..b as usize]).filter(|(_, m)| m.is_some()).map(|(t, _)| if t.light { 0.5 } else { 1.0 }).sum();
        let coverage = if weight == 0.0 { 1.0 } else { hit / weight };
        let hs: Vec<u32> = matched[a as usize..b as usize].iter().flatten().copied().collect();
        let (t0, t1) = match (hs.first(), hs.last()) {
            (Some(&f), Some(&l)) => (heard.t0[f as usize], heard.t1[l as usize]),
            _ => (-1.0, -1.0),
        };
        let want = (b - a) as f64 * sec_per_tok;
        let class = if weight == 0.0 {
            Class::Weak
        } else if coverage >= 0.6 {
            Class::Aligned
        } else if coverage < 0.2 && (t0 < 0.0 || t1 - t0 < want * 0.3) {
            Class::Unread
        } else {
            Class::Weak
        };
        sentences.push(SentCoarse { section, sentence, t0, t1, coverage, class });
    }
    // Extras: runs of the transcript no book sound took, three seconds or eight sounds long.
    let mut extras = Vec::new();
    let mut i = 0;
    while i < used.len() {
        if used[i] {
            i += 1;
            continue;
        }
        let s = i;
        while i < used.len() && !used[i] {
            i += 1;
        }
        let secs = heard.t1[i - 1] - heard.t0[s];
        // A stray letter or two in a long pause isn't talk: by letters, three seconds takes eight.
        let enough = match units {
            Units::Sounds => i - s >= units.extra_min() || secs >= 3.0,
            Units::Letters => i - s >= units.extra_min() || (secs >= 3.0 && i - s >= 8),
        };
        if enough {
            let w0 = heard.word[s];
            let w1 = heard.word[i - 1];
            extras.push(Extra { t0: heard.t0[s], t1: heard.t1[i - 1], text: format!("{w0}-{w1}") });
        }
    }
    Coarse { sentences, extras, sec_per_tok }
}

#[cfg(test)]
pub mod tests {
    use super::*;

    /// A fake transcript of sentences read one after another: `read[i]` is the text heard for
    /// sentence i (None: not read), each sound 0.25 s, 0.4 s between sentences.
    pub fn fake(read: &[Option<String>], extra_at: Option<(usize, &str, f64)>) -> (Vec<HeardWord>, Vec<(f64, f64)>) {
        let mut words = Vec::new();
        let mut truth = Vec::new();
        let mut t = 1.0;
        for (i, r) in read.iter().enumerate() {
            if let Some((at, text, secs)) = extra_at
                && at == i {
                    words.push(HeardWord { text: text.into(), t0: t, t1: t + secs });
                    t += secs + 0.5;
                }
            match r {
                Some(s) => {
                    let start = t;
                    for c in s.chars().filter(|c| c.is_alphanumeric()) {
                        words.push(HeardWord { text: c.to_string(), t0: t, t1: t + 0.25 });
                        t += 0.25;
                    }
                    truth.push((start, t));
                    t += 0.4;
                }
                None => truth.push((-1.0, -1.0)),
            }
        }
        (words, truth)
    }

    fn book() -> Vec<Vec<String>> {
        vec![
            vec!["第一回".into(), "甄士隐梦幻识通灵".into(), "贾雨村风尘怀闺秀".into(), "此开卷第一回也。".into(), "作者自云曾历过一番梦幻之后，故将真事隐去。".into()],
            vec!["第二回".into(), "贾夫人仙逝扬州城".into(), "冷子兴演说荣国府".into(), "却说封肃因听见公差传唤，忙出来陪笑启问。".into(), "那些人只嚷快请出甄爷来。".into(), "封肃忙陪笑道：小人姓封，并不姓甄。".into()],
        ]
    }

    #[test]
    fn sentences_found_with_homophones_skips_and_an_ad() {
        let b = book();
        let flat: Vec<String> = b.iter().flatten().cloned().collect();
        let mut read: Vec<Option<String>> = flat.iter().map(|s| Some(s.clone())).collect();
        read[3] = None; // not read
        read[9] = Some("那些人只嚷快请出真爷来".into()); // 甄 → 真
        let (words, truth) = fake(&read, Some((6, "本节目由某某赞助播出欢迎收听", 4.0)));
        let bt = BookToks::build(&b, true);
        let ht = HeardToks::build(&words, true);
        let c = coarse(&bt, &ht, &[(0, ht.toks.len())], true);
        for (i, s) in c.sentences.iter().enumerate() {
            match truth[i] {
                (-1.0, _) => assert_eq!(s.class, Class::Unread, "{i} {s:?}"),
                (t0, t1) => {
                    assert_eq!(s.class, Class::Aligned, "{i} {s:?}");
                    assert!((s.t0 - t0).abs() < 0.3 && (s.t1 - t1).abs() < 0.3, "{i} {s:?} vs {t0} {t1}");
                }
            }
        }
        assert_eq!(c.extras.len(), 1, "{:?}", c.extras);
        assert!((c.extras[0].t1 - c.extras[0].t0 - 4.0).abs() < 0.5);
    }

    #[test]
    fn the_band_alignment_takes_the_obvious_path() {
        let a = super::super::phon::phon("甲乙丙丁戊己庚辛", true);
        let b = super::super::phon::phon("甲乙丁戊己庚辛壬", true);
        let m = align(&a, &b);
        assert_eq!(m[0], Some(0));
        assert_eq!(m[2], Some(3));
        assert_eq!(m[7], None);
    }

    #[test]
    fn the_models_letters_place_sentences_and_find_talk_outside_the_book() {
        // Frames at 50 a second: each letter of the reading three frames with a blank after,
        // 0.4 s of blank between sentences, an ad's letters before the second sentence, and
        // the third sentence not read.
        let book = vec![vec!["the cat sat on the mat today".to_string(), "a dog ran far away from home".to_string(), "birds sang in the tall trees".to_string(), "then night fell over the town".to_string()]];
        let row = |hot: usize| (0..28).map(|c| if c == hot { -0.01 } else { -8.0 }).collect::<Vec<f32>>();
        let mut em: Vec<f32> = Vec::new();
        let mut starts = Vec::new();
        let say = |em: &mut Vec<f32>, text: &str| {
            for l in crate::forced::letters(text, false) {
                for _ in 0..3 {
                    em.extend(row(l));
                }
                em.extend(row(0));
            }
            for _ in 0..20 {
                em.extend(row(0));
            }
        };
        for (i, s) in book[0].iter().enumerate() {
            if i == 1 {
                say(&mut em, "buy our soap now it is the best soap you can buy anywhere");
            }
            if i == 2 {
                continue;
            }
            starts.push((i, em.len() / 28));
            say(&mut em, s);
        }
        let frames = em.len() / 28;
        let words = heard_letters(&em, frames, frames as f64 / 50.0, 0.0);
        let heard = HeardToks::of(&words, false, Units::Letters);
        let bt = BookToks::of(&book, false, Units::Letters);
        let c = coarse_in(&bt, &heard, &[(0, heard.toks.len())], false, Units::Letters);
        assert_eq!(c.sentences[2].class, Class::Unread, "{c:?}");
        for (i, f) in starts {
            assert_eq!(c.sentences[i].class, Class::Aligned, "{i}: {c:?}");
            assert!((c.sentences[i].t0 - f as f64 / 50.0).abs() < 0.05, "{i}: {c:?}");
        }
        assert_eq!(c.extras.len(), 1, "{c:?}");
    }
}
