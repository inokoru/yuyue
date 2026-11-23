//! Blind-mark backtrack ("记这句 H" / headphone double-tap):
//! When the listener taps "记这句" at time `t_press` (always 2–4 seconds after the memorable line
//! finished), snap to the natural sentence boundaries `[t0, t1]` covering the thought just heard.

use crate::podcast_hub::parse::MagazineSentence;

/// Result of snapping a blind mark press to a clean audio + quote span.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ClipWindow {
    pub t0: f64,
    pub t1: f64,
    pub quote_text: String,
    pub speaker: Option<String>,
    pub sentence_id: Option<String>,
}

/// Computes the natural `[t0, t1]` clip window around `t_press` using transcript sentences when
/// available, or a clean `[t_press - 12s, t_press + 1.5s]` fallback for L0/L1 episodes.
#[uniffi::export]
pub fn resolve_clip_window(sentences: Vec<MagazineSentence>, t_press: f64, duration_sec: f64) -> ClipWindow {
    let clamped_press = t_press.max(0.0);
    if sentences.is_empty() {
        let t0 = (clamped_press - 12.0).max(0.0);
        let t1 = if duration_sec > 0.0 {
            (clamped_press + 1.5).min(duration_sec)
        } else {
            clamped_press + 1.5
        };
        return ClipWindow { t0, t1: t1.max(t0 + 1.0), quote_text: String::new(), speaker: None, sentence_id: None };
    }

    // Human reaction lag is ~2.5s; aim slightly before the press to land inside the sentence that
    // actually triggered the reaction.
    let target = (clamped_press - 2.5).max(0.0);
    let mut best_idx = 0usize;
    let mut best_dist = f64::MAX;
    for (i, s) in sentences.iter().enumerate() {
        if target >= s.t0 && target <= s.t1 {
            best_idx = i;
            break;
        }
        let dist = if target < s.t0 { s.t0 - target } else { target - s.t1 };
        if dist < best_dist {
            best_dist = dist;
            best_idx = i;
        }
    }

    // If the matched sentence is very short (< 3.5s), include the preceding sentence from the same
    // speaker so the quote carries full meaning without cutting mid-thought.
    let mut first = best_idx;
    let last = best_idx;
    if best_idx > 0 {
        let cur = &sentences[best_idx];
        let prev = &sentences[best_idx - 1];
        if (cur.t1 - cur.t0) < 3.5 && prev.speaker == cur.speaker && (cur.t0 - prev.t1) < 2.0 {
            first = best_idx - 1;
        }
    }

    let t0 = sentences[first].t0.max(0.0);
    let t1 = sentences[last].t1.max(t0 + 0.5);
    let quote_text = sentences[first..=last]
        .iter()
        .map(|s| s.text_orig.trim())
        .filter(|s| !s.is_empty())
        .collect::<Vec<_>>()
        .join(" ");
    let speaker = sentences[best_idx].speaker.clone();
    let sentence_id = Some(sentences[best_idx].id.clone());

    ClipWindow { t0, t1, quote_text, speaker, sentence_id }
}
