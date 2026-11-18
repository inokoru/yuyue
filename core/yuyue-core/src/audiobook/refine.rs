//! 精修: the rough times made exact by an acoustic model that, given the words, says where
//! each falls. The core cuts the recording into windows of 15–25 s around sentences it has
//! placed, the app runs the model on each, and here the answers are snapped into the pauses
//! the speech finder heard, so a jump to a sentence never clips its first sound or carries the
//! last one's tail.

use serde::{Deserialize, Serialize};

use super::anchor::{Class, SentCoarse};

pub const WINDOW_MIN: f64 = 15.0;
pub const WINDOW_MAX: f64 = 25.0;
/// Extra recording on each side of a window.
pub const PAD: f64 = 1.5;
/// Breath left before a sentence's first sound.
pub const LEAD: f64 = 0.12;
/// How much of a pause the sentence before may keep.
pub const TAIL: f64 = 0.25;
/// How far from the model's start a pause may be and still be the one.
pub const SNAP: f64 = 0.15;

/// A window to align: these sentences, this stretch of recording.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct AlignWindow {
    pub from: f64,
    pub to: f64,
    /// Indexes into the coarse sentence list.
    pub sentences: Vec<u32>,
}

/// A pause between speech (from the speech finder), seconds.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SpeechPause {
    pub t0: f64,
    pub t1: f64,
}

/// Windows over runs of aligned sentences, cut at the longest pause near the size wanted.
pub fn windows(coarse: &[SentCoarse], pauses: &[SpeechPause]) -> Vec<AlignWindow> {
    let mut out = Vec::new();
    let mut cur: Vec<u32> = Vec::new();
    let mut start = 0.0;
    let flush = |cur: &mut Vec<u32>, start: f64, end: f64, out: &mut Vec<AlignWindow>| {
        if !cur.is_empty() {
            out.push(AlignWindow { from: (start - PAD).max(0.0), to: end + PAD, sentences: std::mem::take(cur) });
        }
    };
    for (i, s) in coarse.iter().enumerate() {
        // Partly matched sentences go to the model too, between their neighbours: it hears
        // the book's words, not the transcriber's guesses. Unread ones break the window.
        if s.class == Class::Unread {
            let end = cur.last().map(|&j| coarse[j as usize].t1).unwrap_or(start);
            flush(&mut cur, start, end, &mut out);
            continue;
        }
        if cur.is_empty() {
            start = s.t0;
        }
        let len = s.t1 - start;
        if len > WINDOW_MAX && !cur.is_empty() {
            // Cut at the longest pause between the window's sentences past the minimum.
            let mut best = cur.len();
            let mut best_gap = -1.0;
            for (k, w) in cur.windows(2).enumerate() {
                let a = coarse[w[0] as usize].t1;
                let b = coarse[w[1] as usize].t0;
                if b - start < WINDOW_MIN {
                    continue;
                }
                let gap = pauses.iter().filter(|p| p.t1 >= a - 0.2 && p.t0 <= b + 0.2).map(|p| p.t1 - p.t0).fold(b - a, f64::max);
                if gap > best_gap {
                    best_gap = gap;
                    best = k + 1;
                }
            }
            let rest: Vec<u32> = cur.split_off(best.min(cur.len()));
            let end = coarse[*cur.last().unwrap() as usize].t1;
            flush(&mut cur, start, end, &mut out);
            cur = rest;
            start = cur.first().map(|&j| coarse[j as usize].t0).unwrap_or(s.t0);
        }
        cur.push(i as u32);
    }
    let end = cur.last().map(|&j| coarse[j as usize].t1).unwrap_or(start);
    flush(&mut cur, start, end, &mut out);
    out
}

/// What the app's aligner gave for one sentence of a window.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct Refined {
    pub index: u32,
    pub t0: f64,
    pub t1: f64,
    /// The model's mean log probability over the sentence's sounds (any scale; it's normalised
    /// over the book).
    pub score: f32,
    /// Start of each character or word, when given.
    pub units: Vec<f64>,
}

/// One window laid out from the alignment model's output over it (`frames` rows of log
/// probabilities, `seconds` long, starting `from` seconds into the recording): each sentence it
/// found, numbered by its place in `sentences`, in the recording's seconds.
pub fn align_window(sentences: &[String], chinese: bool, em: &[f32], frames: usize, seconds: f64, from: f64) -> Vec<Refined> {
    if frames == 0 || sentences.is_empty() {
        return Vec::new();
    }
    let (a, units) = crate::forced::align_with_units(&[(0, sentences.to_vec())], chinese, em, frames, seconds);
    a.sentences
        .iter()
        .filter(|t| t.start >= 0.0 && t.end > t.start)
        .map(|t| Refined {
            index: t.sentence,
            t0: from + t.start,
            t1: from + t.end,
            score: t.heard.max(1e-4).ln(),
            units: units.get(t.sentence as usize).map(|v| v.iter().map(|&x| if x >= 0.0 { from + x } else { -1.0 }).collect()).unwrap_or_default(),
        })
        .collect()
}

/// Snaps a start into the pause before it: the sentence starts `LEAD` before the pause ends, the
/// one before ends `TAIL` into the pause. Two sentences read without a pause meet halfway.
/// Returns (new end of the one before, new start, whether a pause was found).
pub fn snap(prev_end: f64, start: f64, pauses: &[SpeechPause]) -> (f64, f64, bool) {
    let lo = prev_end.min(start) - SNAP;
    let hi = prev_end.max(start) + SNAP;
    let best = pauses.iter().filter(|p| p.t1 >= lo && p.t0 <= hi).max_by(|a, b| (a.t1 - a.t0).partial_cmp(&(b.t1 - b.t0)).unwrap_or(std::cmp::Ordering::Equal));
    match best {
        Some(p) => {
            let s = (p.t1 - LEAD).max(p.t0);
            let e = (p.t0 + TAIL).min(s);
            (e, s, true)
        }
        None => {
            let mid = (prev_end + start) / 2.0;
            (mid, mid, false)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn sc(t0: f64, t1: f64) -> SentCoarse {
        SentCoarse { section: 0, sentence: 0, t0, t1, coverage: 1.0, class: Class::Aligned }
    }

    #[test]
    fn windows_stay_within_size_and_stop_at_gaps() {
        let mut v: Vec<SentCoarse> = (0..20).map(|i| sc(i as f64 * 4.0, i as f64 * 4.0 + 3.5)).collect();
        v[10].class = Class::Unread;
        let w = windows(&v, &[]);
        assert!(w.iter().all(|w| w.to - w.from <= WINDOW_MAX + 2.0 * PAD + 4.0), "{w:?}");
        assert!(w.iter().all(|w| !w.sentences.contains(&10)));
        let all: usize = w.iter().map(|w| w.sentences.len()).sum();
        assert_eq!(all, 19);
    }

    #[test]
    fn a_start_snaps_into_the_pause() {
        let (e, s, found) = snap(10.0, 10.2, &[SpeechPause { t0: 9.9, t1: 10.6 }]);
        assert!(found);
        assert!((s - 10.48).abs() < 1e-9);
        assert!((e - 10.15).abs() < 1e-9);
        let (e, s, found) = snap(10.0, 10.2, &[]);
        assert!(!found && e == s);
    }

    #[test]
    fn a_window_lays_its_sentences_in_recording_time() {
        // Two English sentences, each letter sure for three frames with a blank after, a second
        // of quiet between them, at 50 frames a second.
        let row = |hot: usize| (0..28).map(|c| if c == hot { -0.01 } else { -8.0 }).collect::<Vec<f32>>();
        let mut em = Vec::new();
        for _ in 0..25 {
            em.extend(row(0));
        }
        let texts = ["the cat sat", "on a mat"];
        for (k, t) in texts.iter().enumerate() {
            for l in crate::forced::letters(t, false) {
                for _ in 0..3 {
                    em.extend(row(l));
                }
                em.extend(row(0));
            }
            if k == 0 {
                for _ in 0..50 {
                    em.extend(row(0));
                }
            }
        }
        let frames = em.len() / 28;
        let seconds = frames as f64 / 50.0;
        let r = align_window(&texts.map(String::from), false, &em, frames, seconds, 100.0);
        assert_eq!(r.len(), 2, "{r:?}");
        assert_eq!((r[0].index, r[1].index), (0, 1));
        assert!(r[0].t0 >= 100.0 && r[0].t0 < 100.6, "{r:?}");
        assert!(r[1].t0 > r[0].t1 - 0.01 && r[1].t1 <= 100.0 + seconds + 0.01, "{r:?}");
        // Each word's start, in order, inside its sentence.
        assert_eq!(r[0].units.len(), 3);
        assert!(r[0].units.windows(2).all(|w| w[0] < w[1]) && r[0].units[0] >= r[0].t0 - 0.2, "{r:?}");
        assert!(r[1].units[0] >= r[0].units[2], "{r:?}");
    }
}
