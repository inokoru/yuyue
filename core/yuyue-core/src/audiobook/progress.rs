//! 进度 and 聪明地倒回. Progress is kept as a sentence and how far through it, so another
//! recording of the same book (or the synthetic voice) picks it up; seconds only until there's
//! a map. After a pause, playing goes back a little — by sentences and paragraphs when known,
//! by pauses in the speech otherwise.

use serde::{Deserialize, Serialize};

use super::map::{SyncHit, SyncMap};
use super::refine::SpeechPause;

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum ListenPlace {
    Sentence { section: u32, sentence: u32, through: f32 },
    Seconds { t: f64 },
}

/// Where to resume after a pause of `paused` seconds at `t`. `paragraph_start` gives the first
/// sentence of the paragraph a sentence is in.
pub fn resume_at(t: f64, paused: f64, map: Option<&SyncMap>, pauses: &[SpeechPause], paragraph_start: &dyn Fn(u32, u32) -> (u32, u32)) -> f64 {
    if paused < 10.0 {
        return t;
    }
    if let Some(m) = map
        && let SyncHit::Sentence { section, sentence, .. } | SyncHit::Gap { section, sentence } = m.time_to_loc(t) {
            let this = m.loc_to_time(section, sentence).unwrap_or(t);
            if paused < 120.0 {
                return this;
            }
            if paused < 3600.0 {
                return m.step(this + 0.001, false).filter(|p| *p < this).unwrap_or(this);
            }
            let (ps, pn) = paragraph_start(section, sentence);
            let para = m.loc_to_time(ps, pn).unwrap_or(this);
            let limit = if paused < 86_400.0 { 30.0 } else { 60.0 };
            return para.max(t - limit);
        }
    // No map: back to a pause in the speech.
    let back = if paused < 120.0 {
        0.0
    } else if paused < 3600.0 {
        5.0
    } else if paused < 86_400.0 {
        30.0
    } else {
        60.0
    };
    let target = t - back;
    pauses.iter().filter(|p| p.t1 <= target + 0.01 && p.t1 - p.t0 >= 0.4).map(|p| p.t1).fold(None, |a: Option<f64>, x| Some(a.map_or(x, |a| a.max(x)))).unwrap_or(target.max(0.0))
}

/// Progress as a place: a sentence when the map knows the moment, seconds otherwise.
pub fn place_of(t: f64, map: Option<&SyncMap>) -> ListenPlace {
    match map.map(|m| m.time_to_loc(t)) {
        Some(SyncHit::Sentence { section, sentence, through }) => ListenPlace::Sentence { section, sentence, through },
        Some(SyncHit::Gap { section, sentence }) => ListenPlace::Sentence { section, sentence, through: 1.0 },
        _ => ListenPlace::Seconds { t },
    }
}

/// Seconds of a place, with the map.
pub fn seconds_of(p: &ListenPlace, map: Option<&SyncMap>) -> Option<f64> {
    match p {
        ListenPlace::Seconds { t } => Some(*t),
        ListenPlace::Sentence { section, sentence, through } => {
            let m = map?;
            let r = m.rows.iter().find(|r| r.section == *section && r.sentence == *sentence)?;
            let t0 = m.loc_to_time(*section, *sentence)?;
            Some(t0 + (r.t1_ms.saturating_sub(r.t0_ms)) as f64 / 1000.0 * *through as f64)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::audiobook::grade::{SyncBy, Graded};
    use crate::audiobook::matchbook::MatchKind;

    fn map() -> SyncMap {
        let g: Vec<Graded> = (0..10).map(|i| Graded { section: 0, sentence: i, t0: i as f64 * 5.0, t1: i as f64 * 5.0 + 4.5, by: SyncBy::Auto, conf: 1.0, no_gap: false }).collect();
        SyncMap::from_graded("a", "b", MatchKind::Same, vec![50.0], &g, vec![], vec![], "1")
    }

    #[test]
    fn going_back_grows_with_the_pause() {
        let m = map();
        let para = |_s: u32, n: u32| (0, n - n % 4);
        assert_eq!(resume_at(32.0, 5.0, Some(&m), &[], &para), 32.0);
        assert_eq!(resume_at(32.0, 60.0, Some(&m), &[], &para), 30.0);
        assert_eq!(resume_at(32.0, 600.0, Some(&m), &[], &para), 25.0);
        // Paragraph start (sentence 4, 20 s), but no more than 30 s back.
        assert_eq!(resume_at(32.0, 7200.0, Some(&m), &[], &para), 20.0);
        // No map: to a pause.
        assert_eq!(resume_at(32.0, 600.0, None, &[SpeechPause { t0: 25.0, t1: 25.6 }, SpeechPause { t0: 28.0, t1: 28.1 }], &para), 25.6);
    }

    #[test]
    fn a_place_round_trips() {
        let m = map();
        let p = place_of(31.0, Some(&m));
        assert!(matches!(p, ListenPlace::Sentence { sentence: 6, .. }));
        assert!((seconds_of(&p, Some(&m)).unwrap() - 31.0).abs() < 0.01);
    }
}
