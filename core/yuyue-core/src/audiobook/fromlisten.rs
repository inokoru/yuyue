//! The map from the lay-out the apps make today (强制对齐 against the whole book, kept and synced
//! as `listen/<recording>`), graded the same way as the new pipeline's: sure sentences keep their
//! times, unsure ones share the stretch between sure ones, a chapter mostly unsure is placed only
//! as a whole, and what the recording has that the book doesn't (片头, 片尾, music between
//! chapters, talk in the middle) is marked so playing can pass it.
//!
//! Used until the new pipeline has made a map of its own for the recording.

use super::grade::{Graded, SyncBy, by_chapter, spread};
use super::map::{ExtraKind, ExtraPlace, ExtraSpan, SyncChapter, SyncMap};
use super::matchbook::MatchKind;
use crate::listening::Listening;

/// Share of a sentence's letters heard for its time to be trusted as exact.
pub const HEARD_SURE: f32 = 0.6;
/// Seconds of recording before the first sentence (or after the last) that count as 片头 / 片尾.
pub const HEAD_MIN: f64 = 1.0;
/// Silence between two chapters long enough to be music, not a breath.
pub const CHAPTER_GAP_MIN: f64 = 4.0;
/// Seconds between two sure sentences of a chapter, nothing of the book between them, that are
/// taken as talk not in the book.
pub const MIDDLE_MIN: f64 = 8.0;

/// The map for a lay-out. `total` is the recording's length (the lay-out's `through` when not
/// known); `chapters` the recording's own (title, start).
pub fn map_of(l: &Listening, total: f64, chapters: &[(String, f64)], sounds: &dyn Fn(u32, u32) -> u32) -> SyncMap {
    let mut g: Vec<Graded> = l
        .sentences
        .iter()
        .map(|s| {
            let by = if s.start < 0.0 || s.end <= s.start {
                SyncBy::Unread
            } else if s.adjusted {
                SyncBy::User
            } else if s.heard >= HEARD_SURE {
                SyncBy::Auto
            } else {
                SyncBy::Spread
            };
            Graded { section: s.section, sentence: s.sentence, t0: s.start, t1: s.end, by, conf: if by == SyncBy::User { 1.0 } else { s.heard.clamp(0.0, 1.0) }, no_gap: false }
        })
        .collect();
    let lens: Vec<u32> = g.iter().map(|x| sounds(x.section, x.sentence)).collect();
    spread(&mut g, &lens);
    by_chapter(&mut g);
    let extras = extras_of(&g, l.intro, total);
    let durations = vec![total];
    let sync_chapters = chapters
        .iter()
        .map(|(title, t0)| SyncChapter {
            title: title.clone(),
            t0: *t0,
            book_section: g.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User) && x.t0 >= *t0).min_by(|a, b| a.t0.total_cmp(&b.t0)).map(|x| x.section),
        })
        .collect();
    SyncMap::from_graded(&l.recording, &l.book, MatchKind::Same, durations, &g, extras, sync_chapters, "listen")
}

fn timed(x: &Graded) -> bool {
    matches!(x.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread | SyncBy::Chapter) && x.t0 >= 0.0 && x.t1 > x.t0
}

fn sure(x: &Graded) -> bool {
    matches!(x.by, SyncBy::Auto | SyncBy::User)
}

fn extras_of(g: &[Graded], intro: f64, total: f64) -> Vec<ExtraSpan> {
    let mut out = Vec::new();
    let mut order: Vec<&Graded> = g.iter().filter(|x| timed(x)).collect();
    order.sort_by(|a, b| a.t0.total_cmp(&b.t0));
    let (Some(first), Some(last)) = (order.first(), order.iter().max_by(|a, b| a.t1.total_cmp(&b.t1))) else { return out };
    let head = if intro > 0.0 { intro.min(first.t0) } else { first.t0 };
    if head >= HEAD_MIN {
        out.push(ExtraSpan { t0: 0.0, t1: head, kind: ExtraKind::Speech, place: ExtraPlace::Head, text: None });
    }
    // Between chapters: a long quiet stretch is music (or the next chapter's announcement, which
    // is not in the book either).
    for w in order.windows(2) {
        let (a, b) = (w[0], w[1]);
        let gap = b.t0 - a.t1;
        if a.section != b.section {
            if gap >= CHAPTER_GAP_MIN {
                out.push(ExtraSpan { t0: a.t1, t1: b.t0, kind: ExtraKind::Music, place: ExtraPlace::ChapterGap, text: None });
            }
        } else if gap >= MIDDLE_MIN && sure(a) && sure(b) && b.sentence == a.sentence + 1 {
            out.push(ExtraSpan { t0: a.t1, t1: b.t0, kind: ExtraKind::Speech, place: ExtraPlace::Middle, text: None });
        }
    }
    if total - last.t1 >= HEAD_MIN {
        out.push(ExtraSpan { t0: last.t1, t1: total, kind: ExtraKind::Speech, place: ExtraPlace::Tail, text: None });
    }
    out
}

/// Pauses between sentences longer than `PAUSE_LONG`, after `from`: (start, end).
pub fn long_pauses(m: &SyncMap, from: f64) -> Vec<(f64, f64)> {
    let mut rows: Vec<_> = m.rows.iter().filter(|r| matches!(r.by, SyncBy::Auto | SyncBy::User) && r.t1_ms > r.t0_ms).collect();
    rows.sort_by_key(|r| r.t0_ms);
    let from_ms = (from.max(0.0) * 1000.0) as u32;
    rows.windows(2)
        .filter(|w| w[0].section == w[1].section && w[1].sentence == w[0].sentence + 1 && w[1].t0_ms >= from_ms)
        .map(|w| (w[0].t1_ms as f64 / 1000.0, w[1].t0_ms as f64 / 1000.0))
        .filter(|(a, b)| b - a > PAUSE_LONG && b - a < MIDDLE_MIN)
        .collect()
}

/// 缩短停顿: pauses longer than this are cut down to `PAUSE_KEPT`.
pub const PAUSE_LONG: f64 = 0.6;
pub const PAUSE_KEPT: f64 = 0.35;

#[cfg(test)]
mod tests {
    use super::*;
    use crate::listening::ListenSentence;

    fn s(section: u32, sentence: u32, start: f64, end: f64, heard: f32) -> ListenSentence {
        ListenSentence { section, sentence, start, end, heard, adjusted: false }
    }

    fn listening(sentences: Vec<ListenSentence>, intro: f64) -> Listening {
        Listening { recording: "r".into(), book: "b".into(), sentences, sections: vec![], intro, through: 100.0, intro_skipped: false, made_ms: None, took: None }
    }

    #[test]
    fn grades_extras_and_unread() {
        let l = listening(
            vec![
                s(0, 0, 5.0, 8.0, 0.9),
                s(0, 1, 8.2, 11.0, 0.3),
                s(0, 2, 11.2, 14.0, 0.9),
                s(0, 3, -1.0, -1.0, 0.0),
                s(0, 4, 14.5, 17.0, 0.95),
                // Talk not in the book between two sure ones.
                s(0, 5, 30.0, 33.0, 0.95),
                // Music between chapters.
                s(1, 0, 40.0, 43.0, 0.9),
                s(1, 1, 43.3, 46.0, 0.9),
            ],
            5.0,
        );
        let m = map_of(&l, 60.0, &[], &|_, _| 10);
        let by: Vec<SyncBy> = m.rows.iter().map(|r| r.by).collect();
        assert_eq!(by, vec![SyncBy::Auto, SyncBy::Spread, SyncBy::Auto, SyncBy::Unread, SyncBy::Auto, SyncBy::Auto, SyncBy::Auto, SyncBy::Auto]);
        let places: Vec<ExtraPlace> = m.extras.iter().map(|e| e.place).collect();
        assert_eq!(places, vec![ExtraPlace::Head, ExtraPlace::Middle, ExtraPlace::ChapterGap, ExtraPlace::Tail]);
        assert_eq!(m.extras[0].t1, 5.0);
        assert_eq!((m.extras[1].t0, m.extras[1].t1), (17.0, 30.0));
        assert_eq!(m.extras[3].t0, 46.0);
        // The unread sentence plays from the next one.
        assert_eq!(m.loc_to_time(0, 3), Some(14.5));
        let p = long_pauses(&m, 0.0);
        assert!(p.contains(&(43.0, 43.3)) == false, "0.3 s is not long");
    }

    #[test]
    fn a_chapter_mostly_unsure_is_by_chapter() {
        let l = listening((0..10).map(|i| s(0, i, i as f64 * 3.0, i as f64 * 3.0 + 2.9, if i < 3 { 0.9 } else { 0.2 })).collect(), 0.0);
        let m = map_of(&l, 30.0, &[], &|_, _| 10);
        assert!(m.rows.iter().all(|r| r.by == SyncBy::Chapter), "{:?}", m.rows);
    }
}
