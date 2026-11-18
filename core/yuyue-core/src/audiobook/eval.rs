//! 考卷: recordings made up from a book's own sentences, where it's known to the millisecond
//! when each sentence is read — so every change to the aligner is scored without anyone
//! marking anything. The transcript is what a transcriber would give: homophones, a few wrong
//! characters, numbers read another way. Variants leave sentences and a whole stretch out, put
//! talk that isn't in the book at the head, the tail and the middle.
//!
//! The acoustic model is stood in for by the truth with a little noise, so what's scored is the
//! core's part: matching, anchors, the alignment between them, snapping and grading.

use std::collections::HashMap;

use pinyin::ToPinyin;

use super::anchor::{BookToks, HeardToks, HeardWord, coarse};
use super::grade::{SyncBy, grade};
use super::refine::{SpeechPause, Refined, snap, windows};

/// A small deterministic random source (tests must repeat).
pub struct Rng(u64);

impl Rng {
    pub fn new(seed: u64) -> Rng {
        Rng(seed.wrapping_mul(6364136223846793005).wrapping_add(1442695040888963407))
    }
    pub fn next_u64(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }
    pub fn f(&mut self) -> f64 {
        (self.next_u64() % 1_000_000) as f64 / 1_000_000.0
    }
}

#[derive(Clone, Debug, Default)]
pub struct Variant {
    /// Share of sentences not read.
    pub drop: f64,
    /// A stretch of this many sentences not read, in the middle.
    pub drop_run: usize,
    /// Talk not in the book: seconds at the head, the middle, the tail.
    pub extra: f64,
    /// Share of characters heard as a homophone, and as something else.
    pub homophone: f64,
    pub wrong: f64,
}

pub struct Made {
    pub words: Vec<HeardWord>,
    pub pauses: Vec<SpeechPause>,
    /// For each sentence (in book order): its true start and end, or None when not read.
    pub truth: Vec<Option<(f64, f64)>>,
    /// Seconds of talk not in the book.
    pub extra: Vec<(f64, f64)>,
}

/// A made-up reading of `sections`: Chinese a character at a time, other languages a word.
pub fn make(sections: &[Vec<String>], v: &Variant, seed: u64) -> Made {
    make_in(sections, v, seed, true)
}

pub fn make_in(sections: &[Vec<String>], v: &Variant, seed: u64, chinese: bool) -> Made {
    let mut rng = Rng::new(seed);
    // Homophones from the book's own characters.
    let mut by_sound: HashMap<String, Vec<char>> = HashMap::new();
    for s in sections.iter().flatten() {
        for c in s.chars() {
            if let Some(p) = c.to_pinyin() {
                let e = by_sound.entry(p.plain().to_string()).or_default();
                if !e.contains(&c) && e.len() < 8 {
                    e.push(c);
                }
            }
        }
    }
    let all: Vec<&String> = sections.iter().flatten().collect();
    let n = all.len();
    let run_at = if v.drop_run > 0 { n / 2 } else { usize::MAX };
    let mut words = Vec::new();
    let mut truth = Vec::new();
    let mut extra = Vec::new();
    let mut t = 0.5;
    let talk = |t: &mut f64, secs: f64, words: &mut Vec<HeardWord>, extra: &mut Vec<(f64, f64)>| {
        let filler = "欢迎收听本节目由某某出版社出品播讲人某某某感谢您的收听请关注我们";
        let start = *t;
        let count = (secs / 0.22) as usize;
        for (k, c) in filler.chars().cycle().take(count).enumerate() {
            let _ = k;
            words.push(HeardWord { text: c.to_string(), t0: *t, t1: *t + 0.2 });
            *t += 0.22;
        }
        extra.push((start, *t));
        *t += 0.8;
    };
    if v.extra > 0.0 {
        talk(&mut t, v.extra, &mut words, &mut extra);
    }
    for (i, s) in all.iter().enumerate() {
        if v.extra > 0.0 && i == n / 3 {
            talk(&mut t, v.extra, &mut words, &mut extra);
        }
        let skip = (i >= run_at && i < run_at + v.drop_run) || rng.f() < v.drop;
        let readable: Vec<char> = s.chars().filter(|c| c.is_alphanumeric()).collect();
        if skip || readable.is_empty() {
            truth.push(None);
            continue;
        }
        let start = t;
        let mut last_end = t;
        if !chinese {
            let vocab: Vec<&str> = all.iter().flat_map(|x| x.split_whitespace()).take(5000).collect();
            for w in s.split_whitespace() {
                let pace = 0.28 + rng.f() * 0.12;
                let heard = if rng.f() < v.wrong + v.homophone { vocab[(rng.next_u64() as usize) % vocab.len()].to_string() } else { w.to_string() };
                words.push(HeardWord { text: heard, t0: t, t1: t + pace - 0.02 });
                t += pace;
                last_end = t - 0.02;
                if w.ends_with(',') || w.ends_with(';') {
                    t += 0.18;
                }
            }
            truth.push(Some((start, last_end)));
            t += 0.35 + rng.f() * 0.5;
            continue;
        }
        for c in s.chars() {
            if !c.is_alphanumeric() {
                if "，、；：,;".contains(c) {
                    t += 0.18;
                }
                continue;
            }
            let pace = 0.19 + rng.f() * 0.06;
            let r = rng.f();
            let heard = if r < v.homophone {
                c.to_pinyin().and_then(|p| by_sound.get(p.plain())).and_then(|cs| cs.iter().find(|x| **x != c).copied()).unwrap_or(c)
            } else if r < v.homophone + v.wrong {
                char::from_u32(0x4e00 + (rng.next_u64() % 20000) as u32).unwrap_or(c)
            } else {
                c
            };
            words.push(HeardWord { text: heard.to_string(), t0: t, t1: t + pace - 0.01 });
            t += pace;
            last_end = t - 0.01;
        }
        truth.push(Some((start, last_end)));
        t += 0.35 + rng.f() * 0.5;
    }
    if v.extra > 0.0 {
        talk(&mut t, v.extra, &mut words, &mut extra);
    }
    // The speech finder's pauses: silences of 150 ms or more between words.
    let mut pauses = Vec::new();
    for w in words.windows(2) {
        if w[1].t0 - w[0].t1 >= 0.15 {
            pauses.push(SpeechPause { t0: w[0].t1, t1: w[1].t0 });
        }
    }
    Made { words, pauses, truth, extra }
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Score {
    pub sentences: u32,
    pub read: u32,
    pub auto: u32,
    /// Auto sentences whose start is off by more than 0.3 s.
    pub off: u32,
    /// Sentences whose time lies in another sentence (a tap plays the wrong one).
    pub wrong: u32,
    pub unread_given_time: u32,
    pub read_called_unread: u32,
    /// Seconds of extra talk heard as part of a sentence.
    pub extra_leaked: f64,
    pub extra_total: f64,
}

impl Score {
    pub fn auto_share(&self) -> f64 {
        self.auto as f64 / self.read.max(1) as f64
    }
    pub fn off_rate(&self) -> f64 {
        self.off as f64 / self.auto.max(1) as f64
    }
    pub fn wrong_rate(&self) -> f64 {
        self.wrong as f64 / self.read.max(1) as f64
    }
}

/// Aligns a made recording and scores it. `jitter` is the stand-in acoustic model's error (s).
pub fn run(sections: &[Vec<String>], made: &Made, jitter: f64, seed: u64) -> Score {
    run_in(sections, made, jitter, seed, true)
}

pub fn run_in(sections: &[Vec<String>], made: &Made, jitter: f64, seed: u64, chinese: bool) -> Score {
    let mut rng = Rng::new(seed ^ 0xabcdef);
    let bt = BookToks::build(sections, chinese);
    let ht = HeardToks::build(&made.words, chinese);
    let c = coarse(&bt, &ht, &[(0, ht.toks.len())], chinese);
    let wins = windows(&c.sentences, &made.pauses);
    // The stand-in acoustic model: the truth, a little off, for the sentences of each window.
    let mut refined: Vec<Option<(Refined, bool)>> = vec![None; c.sentences.len()];
    let mut prev_end: Option<f64> = None;
    for w in &wins {
        for &k in &w.sentences {
            let Some((t0, t1)) = made.truth[k as usize] else { continue };
            let d0 = (rng.f() - 0.5) * 2.0 * jitter;
            let d1 = (rng.f() - 0.5) * 2.0 * jitter;
            let r = Refined { index: k, t0: t0 + d0, t1: t1 + d1, score: -1.0 - rng.f() as f32 * 0.2, units: vec![] };
            let (_e, s, found) = snap(prev_end.unwrap_or(r.t0), r.t0, &made.pauses);
            prev_end = Some(r.t1);
            refined[k as usize] = Some((Refined { t0: s, ..r }, found));
        }
    }
    let sounds: Vec<u32> = bt.sentences.iter().map(|s| s.3 - s.2).collect();
    let g = grade(&c.sentences, &refined, &sounds, c.sec_per_tok, &[]);
    let mut sc = Score { sentences: g.len() as u32, ..Default::default() };
    let truth_at = |t: f64| made.truth.iter().position(|x| x.is_some_and(|(a, b)| t >= a - 0.05 && t <= b));
    for (i, x) in g.iter().enumerate() {
        match made.truth[i] {
            Some((t0, _t1)) => {
                sc.read += 1;
                if x.by == SyncBy::Unread {
                    sc.read_called_unread += 1;
                    continue;
                }
                if x.by == SyncBy::Auto || x.by == SyncBy::User {
                    sc.auto += 1;
                    if (x.t0 - t0).abs() > 0.3 {
                        sc.off += 1;
                    }
                }
                if matches!(x.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread) {
                    // Wrong: where a tap plays is inside another sentence.
                    let probe = x.t0 + 0.15;
                    if let Some(j) = truth_at(probe)
                        && j != i && x.by != SyncBy::Spread {
                            sc.wrong += 1;
                        }
                }
            }
            None => {
                if matches!(x.by, SyncBy::Auto) {
                    sc.unread_given_time += 1;
                }
            }
        }
    }
    for &(a, b) in &made.extra {
        sc.extra_total += b - a;
        for x in g.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User)) {
            let lo = x.t0.max(a);
            let hi = x.t1.min(b);
            if hi > lo {
                sc.extra_leaked += hi - lo;
            }
        }
    }
    sc
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api::YuyueCore;

    fn book(sections: std::ops::Range<u32>) -> Vec<Vec<String>> {
        let dir = std::env::temp_dir().join(format!("yuyue-ab-eval-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let b = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        let out = sections.map(|i| core.sentences(b.id.clone(), i).unwrap()).collect();
        std::fs::remove_dir_all(dir).ok();
        out
    }

    fn report(name: &str, s: &Score) {
        println!(
            "{name}: 句 {} 念了 {} · 精确 {:.1}% · 偏出 0.3 s {:.2}% · 点错句 {:.2}% · 没念却给了时间 {} · 念了却判没念 {} · 插话漏进 {:.1}/{:.1} s",
            s.sentences,
            s.read,
            s.auto_share() * 100.0,
            s.off_rate() * 100.0,
            s.wrong_rate() * 100.0,
            s.unread_given_time,
            s.read_called_unread,
            s.extra_leaked,
            s.extra_total
        );
    }

    #[test]
    fn clean_reading_of_three_chapters() {
        let b = book(9..12);
        let made = make(&b, &Variant { homophone: 0.05, wrong: 0.03, ..Default::default() }, 1);
        let s = run(&b, &made, 0.05, 1);
        report("照念", &s);
        assert!(s.auto_share() >= 0.97, "{s:?}");
        assert!(s.off_rate() <= 0.01, "{s:?}");
        assert!(s.wrong_rate() <= 0.001, "{s:?}");
    }

    #[test]
    fn skipped_sentences_a_cut_and_talk_not_in_the_book() {
        let b = book(9..12);
        let made = make(&b, &Variant { drop: 0.06, drop_run: 25, extra: 40.0, homophone: 0.05, wrong: 0.03 }, 2);
        let s = run(&b, &made, 0.05, 2);
        report("删节 + 插话", &s);
        assert!(s.wrong_rate() <= 0.001, "{s:?}");
        assert!(s.off_rate() <= 0.01, "{s:?}");
        let unread = made.truth.iter().filter(|x| x.is_none()).count() as f64;
        assert!(s.unread_given_time as f64 <= (unread * 0.005).ceil(), "{s:?}");
        assert!(s.read_called_unread as f64 <= s.read as f64 * 0.01, "{s:?}");
    }

    #[test]
    fn english_with_cuts_and_talk() {
        let dir = std::env::temp_dir().join(format!("yuyue-ab-eval-en-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let b = core.import_book(format!("{root}/../../fixtures/books/pride-and-prejudice.epub")).unwrap();
        let sections: Vec<Vec<String>> = (5..9).map(|i| core.sentences(b.id.clone(), i).unwrap()).collect();
        std::fs::remove_dir_all(dir).ok();
        let made = make_in(&sections, &Variant { drop: 0.06, drop_run: 10, extra: 30.0, wrong: 0.04, ..Default::default() }, 5, false);
        let s = run_in(&sections, &made, 0.05, 5, false);
        report("英文 删节 + 插话", &s);
        assert!(s.wrong_rate() <= 0.001, "{s:?}");
        assert!(s.off_rate() <= 0.01, "{s:?}");
    }

    #[test]
    fn another_book_is_not_this_one() {
        use crate::audiobook::matchbook::{KIndex, MatchKind, classify, hit_rate};
        let b = book(9..10);
        let other = book(40..41);
        let made = make(&other, &Variant { homophone: 0.05, ..Default::default() }, 3);
        let bt = BookToks::build(&b, true);
        let ht = HeardToks::build(&made.words, true);
        let ix = KIndex::build(&bt.toks, 6);
        let r = hit_rate(&ix, &ht.toks);
        assert!(matches!(classify(r), MatchKind::NotThis | MatchKind::ChapterOnly), "{r}");
        let same = make(&b, &Variant { homophone: 0.05, wrong: 0.03, ..Default::default() }, 4);
        assert_eq!(classify(hit_rate(&ix, &HeardToks::build(&same.words, true).toks)), MatchKind::Same);
    }
}
