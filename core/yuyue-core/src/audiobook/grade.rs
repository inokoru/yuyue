//! 把握: how sure each sentence's time is, and what it gets for it.
//!
//! * `Auto` — sure: exact times; a tap starts it from its first sound.
//! * `Spread` — not sure: no time of its own, the stretch between the nearest sure sentences
//!   shared out by length; a tap goes to the stretch's start. The highlight is a little loose,
//!   never on the wrong sentence.
//! * a chapter with fewer than half its sentences sure is only placed as a whole.
//!
//! The user's pins (“从这里对准”) are sure by definition and every recompute keeps them.

use serde::{Deserialize, Serialize};

use super::anchor::{Class, SentCoarse};
use super::refine::Refined;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum SyncBy {
    Auto,
    Spread,
    User,
    Unread,
    /// The chapter is placed only as a whole.
    Chapter,
}

/// One sentence's time, graded.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct Graded {
    pub section: u32,
    pub sentence: u32,
    pub t0: f64,
    pub t1: f64,
    pub by: SyncBy,
    pub conf: f32,
    pub no_gap: bool,
}

/// What `conf` is computed from.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct Features {
    pub coverage: f32,
    /// The model's score, standardised over the book.
    pub z: f32,
    /// Seconds between the model's start and the coarse one.
    pub moved: f64,
    /// Actual length ÷ (sounds × the book's seconds a sound).
    pub rate: f64,
    pub neighbours_sure: bool,
    pub snapped: bool,
}

/// Weights of the logistic over the features. Fitted on the calibration set (第十一节); these
/// are the starting values.
pub mod w {
    pub const BIAS: f32 = -1.0;
    pub const COVERAGE: f32 = 3.0;
    pub const Z: f32 = 0.8;
    pub const MOVED: f32 = -1.5;
    pub const RATE_OFF: f32 = -2.0;
    pub const NEIGHBOURS: f32 = 1.0;
    pub const SNAPPED: f32 = 1.0;
}

/// The threshold for `Auto`.
pub const SURE: f32 = 0.9;
/// The threshold for `Auto` on the transcriber's times alone (no alignment model ran): stricter,
/// and only for a start that fell into a pause.
pub const SURE_COARSE: f32 = 0.95;

pub fn conf(f: &Features) -> f32 {
    let rate_off = if (0.4..=2.5).contains(&f.rate) { 0.0 } else { 1.0 };
    let z = w::BIAS + w::COVERAGE * f.coverage + w::Z * f.z.clamp(-3.0, 3.0) + w::MOVED * (f.moved.min(3.0) as f32) + w::RATE_OFF * rate_off + w::NEIGHBOURS * f.neighbours_sure as u8 as f32 + w::SNAPPED * f.snapped as u8 as f32;
    1.0 / (1.0 + (-z).exp())
}

/// Grades a book's sentences. `refined` are the aligner's answers by coarse index (missing: the
/// coarse times stand, less sure; a score that's NaN: the coarse times snapped into a pause, no
/// model run); `sounds` each sentence's sound count; `pins` the user's
/// (coarse index, start).
pub fn grade(coarse: &[SentCoarse], refined: &[Option<(Refined, bool)>], sounds: &[u32], sec_per_tok: f64, pins: &[(u32, f64)]) -> Vec<Graded> {
    let scores: Vec<f32> = refined.iter().flatten().map(|r| r.0.score).filter(|x| !x.is_nan()).collect();
    let (mean, sd) = {
        let n = scores.len().max(1) as f32;
        let m = scores.iter().sum::<f32>() / n;
        let v = scores.iter().map(|s| (s - m) * (s - m)).sum::<f32>() / n;
        (m, v.sqrt().max(1e-3))
    };
    let mut out: Vec<Graded> = coarse
        .iter()
        .enumerate()
        .map(|(i, c)| {
            let (t0, t1, z, moved, snapped, fine) = match refined.get(i).and_then(|r| r.as_ref()) {
                Some((r, snapped)) if r.score.is_nan() => (r.t0, r.t1, -1.0, 0.0, *snapped, false),
                // Heard well (the model's own odds at least even) isn't held against a sentence
                // for being below the book's average: a steady reader makes the spread narrow.
                Some((r, snapped)) => (r.t0, r.t1, if r.score.exp() >= crate::align::UNSURE_BELOW { ((r.score - mean) / sd).max(0.0) } else { (r.score - mean) / sd }, if c.class == Class::Weak { 0.0 } else { (r.t0 - c.t0).abs() }, *snapped, true),
                None => (c.t0, c.t1, -1.0, 0.0, false, false),
            };
            let want = sounds.get(i).copied().unwrap_or(1) as f64 * sec_per_tok;
            let rate = if want > 0.0 { (t1 - t0) / want } else { 1.0 };
            let ok = |j: usize| coarse.get(j).is_some_and(|x| x.class == Class::Aligned);
            let f = Features { coverage: c.coverage, z, moved, rate, neighbours_sure: (i == 0 || ok(i - 1)) && ok(i + 1), snapped };
            let p = conf(&f);
            let by = match c.class {
                Class::Unread => SyncBy::Unread,
                // Partly matched by the transcript, then found by the model: as sure as it says.
                Class::Aligned | Class::Weak if fine && p >= SURE => SyncBy::Auto,
                Class::Aligned if !fine && snapped && p >= SURE_COARSE => SyncBy::Auto,
                _ => SyncBy::Spread,
            };
            Graded { section: c.section, sentence: c.sentence, t0, t1, by, conf: p, no_gap: !snapped }
        })
        .collect();
    for &(i, t) in pins {
        if let Some(g) = out.get_mut(i as usize) {
            g.t0 = t;
            g.by = SyncBy::User;
            g.conf = 1.0;
        }
    }
    spread(&mut out, sounds);
    by_chapter(&mut out);
    out
}

/// Sentences without a sure time share out the stretch between the nearest sure ones by length.
pub(crate) fn spread(g: &mut [Graded], sounds: &[u32]) {
    let sure = |x: &Graded| matches!(x.by, SyncBy::Auto | SyncBy::User);
    // The reader's pace, seconds a sound, from the sure sentences (their middle one).
    let pace = {
        let mut p: Vec<f64> = g.iter().enumerate().filter(|(_, x)| x.by == SyncBy::Auto && x.t1 > x.t0).map(|(k, x)| (x.t1 - x.t0) / sounds.get(k).copied().unwrap_or(1).max(1) as f64).collect();
        p.sort_by(f64::total_cmp);
        p.get(p.len() / 2).copied().unwrap_or(0.25)
    };
    let mut i = 0;
    while i < g.len() {
        if sure(&g[i]) || g[i].by == SyncBy::Unread {
            i += 1;
            continue;
        }
        let a = i;
        while i < g.len() && !sure(&g[i]) {
            i += 1;
        }
        // At the very start or end there's no sure sentence on one side: the stretch is laid
        // back from the first sure one (or on from the last) by the group's own length, so
        // talk before the book (片头) or after it isn't taken in.
        let own: f64 = (a..i).filter(|&k| g[k].by != SyncBy::Unread).map(|k| sounds.get(k).copied().unwrap_or(1).max(1) as f64 * pace).sum();
        let earliest = g[a..i].iter().filter(|x| x.t0 >= 0.0).map(|x| x.t0).fold(f64::MAX, f64::min);
        let latest = g[a..i].iter().map(|x| x.t1).fold(f64::MIN, f64::max);
        let (from, to) = match (a > 0, i < g.len()) {
            (true, true) => (g[a - 1].t1, g[i].t0),
            (false, true) => (earliest.max(g[i].t0 - own), g[i].t0),
            (true, false) => (g[a - 1].t1, latest.min(g[a - 1].t1 + own)),
            (false, false) => (earliest, latest),
        };
        if from >= to || from == f64::MAX {
            continue;
        }
        let idx: Vec<usize> = (a..i).filter(|&k| g[k].by != SyncBy::Unread).collect();
        let total: f64 = idx.iter().map(|&k| sounds.get(k).copied().unwrap_or(1).max(1) as f64).sum();
        let mut t = from;
        for k in idx {
            let d = (to - from) * sounds.get(k).copied().unwrap_or(1).max(1) as f64 / total;
            g[k].t0 = t;
            g[k].t1 = t + d;
            g[k].by = SyncBy::Spread;
            t += d;
        }
    }
}

/// A chapter where fewer than half the sentences are sure is placed only as a whole.
pub(crate) fn by_chapter(g: &mut [Graded]) {
    let mut i = 0;
    while i < g.len() {
        let s = g[i].section;
        let a = i;
        while i < g.len() && g[i].section == s {
            i += 1;
        }
        let read: Vec<&Graded> = g[a..i].iter().filter(|x| x.by != SyncBy::Unread).collect();
        let sure = read.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User)).count();
        if !read.is_empty() && sure * 2 < read.len() {
            for x in &mut g[a..i] {
                if x.by != SyncBy::User {
                    x.by = SyncBy::Chapter;
                }
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn c(t0: f64, t1: f64, class: Class) -> SentCoarse {
        SentCoarse { section: 0, sentence: 0, t0, t1, coverage: if class == Class::Aligned { 1.0 } else { 0.4 }, class }
    }

    fn r(i: u32, t0: f64, t1: f64) -> Option<(Refined, bool)> {
        Some((Refined { index: i, t0, t1, score: -1.0, units: vec![] }, true))
    }

    #[test]
    fn unsure_sentences_share_the_time_between_sure_ones() {
        let co = vec![c(0.0, 2.0, Class::Aligned), c(2.0, 4.0, Class::Weak), c(4.0, 6.0, Class::Weak), c(6.0, 8.0, Class::Aligned), c(8.0, 10.0, Class::Aligned)];
        let re = vec![r(0, 0.1, 2.0), None, None, r(3, 6.1, 8.0), r(4, 8.1, 10.0)];
        let g = grade(&co, &re, &[8, 8, 8, 8, 8], 0.25, &[]);
        assert_eq!(g[0].by, SyncBy::Auto);
        assert_eq!(g[1].by, SyncBy::Spread);
        assert!((g[1].t0 - 2.0).abs() < 1e-9 && (g[2].t1 - 6.1).abs() < 1e-9, "{g:?}");
        assert!((g[1].t1 - g[2].t0).abs() < 1e-9);
    }

    #[test]
    fn coarse_times_are_sure_only_snapped_into_a_pause() {
        let co = vec![c(0.0, 2.0, Class::Aligned), c(2.0, 4.0, Class::Aligned), c(4.0, 6.0, Class::Aligned)];
        let coarse_only = |i: u32, t0: f64, snapped: bool| Some((Refined { index: i, t0, t1: t0 + 1.9, score: f32::NAN, units: vec![] }, snapped));
        let g = grade(&co, &[coarse_only(0, 0.1, true), coarse_only(1, 2.1, true), coarse_only(2, 4.1, true)], &[8, 8, 8], 0.25, &[]);
        assert!(g[..2].iter().all(|x| x.by == SyncBy::Auto), "{g:?}");
        // Not in a pause, and no model: never exact.
        let g = grade(&co, &[None, None, None], &[8, 8, 8], 0.25, &[]);
        assert!(g.iter().all(|x| x.by != SyncBy::Auto), "{g:?}");
        let g = grade(&co, &[coarse_only(0, 0.1, true), coarse_only(1, 2.1, false), coarse_only(2, 4.1, true)], &[8, 8, 8], 0.25, &[]);
        assert_ne!(g[1].by, SyncBy::Auto, "{g:?}");
    }

    #[test]
    fn the_first_unsure_sentences_keep_out_of_talk_before_the_book() {
        // Two short sentences coarse-placed at 0.2 (inside an announcer's talk), the first sure
        // one at 6.0: they're laid just before it, not stretched back to 0.2.
        let co = vec![c(0.2, 0.7, Class::Weak), c(3.0, 3.6, Class::Weak), c(6.0, 8.0, Class::Aligned), c(8.0, 10.0, Class::Aligned), c(10.0, 12.0, Class::Aligned)];
        let re = vec![None, None, r(2, 6.0, 8.0), r(3, 8.0, 10.0), r(4, 10.0, 12.0)];
        let g = grade(&co, &re, &[2, 3, 8, 8, 8], 0.25, &[]);
        assert_eq!(g[0].by, SyncBy::Spread);
        assert!((g[0].t0 - 4.75).abs() < 1e-9 && (g[1].t1 - 6.0).abs() < 1e-9, "{g:?}");
    }

    #[test]
    fn a_chapter_mostly_unsure_is_placed_as_a_whole_and_pins_stay() {
        let co = vec![c(0.0, 2.0, Class::Weak), c(2.0, 4.0, Class::Weak), c(4.0, 6.0, Class::Weak), c(6.0, 8.0, Class::Weak), c(8.0, 10.0, Class::Aligned)];
        let re = vec![None, None, None, None, r(4, 8.0, 10.0)];
        let g = grade(&co, &re, &[8, 8, 8, 8, 8], 0.25, &[(1, 2.5)]);
        assert_eq!(g[0].by, SyncBy::Chapter);
        assert_eq!(g[1].by, SyncBy::User);
        assert_eq!(g[1].t0, 2.5);
    }
}
