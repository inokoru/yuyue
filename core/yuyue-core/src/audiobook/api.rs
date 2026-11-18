//! What the apps call for 有声书: the work queue (find speech, transcribe, align), the map, and
//! the player's questions (where is this moment, where does this sentence start, where to go
//! back to after a pause).
//!
//! Kept on this device beside the recording (`<book>/align/`): transcript, pauses, coarse and
//! refined times — megabytes, not synced. Synced: the map (packed), the user's pins, the pairing,
//! skip settings. Another device with the same recording picks the map up and does no work.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::Mutex;

use base64::Engine;
use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use super::anchor::{BookToks, Class, Coarse, HeardToks, HeardWord, SentCoarse, Units, coarse_in};
use super::grade::{SyncBy, grade};
use super::map::{ExtraKind, ExtraPlace, ExtraSpan, SyncHit, SyncChapter, SyncMap};
use super::matchbook::{KIndex, MatchKind, best};
use super::progress::resume_at;
use super::refine::{SpeechPause, Refined, AlignWindow, snap, windows};
use crate::api::{BookFormat, YuyueCore, lock};
use crate::error::{CoreError, Result};

/// The aligner's version: part of every map, so a better one redoes the work.
pub const ALIGNER: &str = "2";
/// How far before a word's first sound a jump to it starts.
pub const WORD_LEAD: f64 = 0.1;
/// Transcribed a block at a time.
pub const BLOCK: f64 = 600.0;

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct AudiobookCaps {
    pub vad: bool,
    pub transcribe: bool,
    /// The transcript comes as the alignment model's letters, one a word (no transcriber here).
    pub letters: bool,
    pub align: bool,
    /// Plugged in, or the user said “现在就做”: the whole book may be done now (phones).
    pub whole_book_now: bool,
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct TimeSpan {
    pub t0: f64,
    pub t1: f64,
}

#[derive(Clone, Debug, PartialEq, uniffi::Enum)]
pub enum AudiobookTask {
    /// Find speech and music in [from, to) of the whole recording (seconds).
    Vad { id: String, file: u32, from: f64, to: f64, file_offset: f64 },
    /// Transcribe these stretches, word times in the whole recording's seconds.
    Transcribe { id: String, file: u32, spans: Vec<TimeSpan>, lang: String, careful: bool, file_offset: f64 },
    /// Give each of these sentences its start and end inside [from, to).
    Align { id: String, file: u32, from: f64, to: f64, sentences: Vec<String>, lang: String, file_offset: f64 },
}

#[derive(Clone, Debug, PartialEq, uniffi::Enum)]
pub enum AudiobookResult {
    Vad { speech: Vec<TimeSpan>, music: Vec<TimeSpan> },
    Transcribe { words: Vec<HeardWord> },
    Align { sentences: Vec<Refined> },
    Failed { reason: String },
    /// Not done, and not the task's fault (paused, stopped): handed back without counting.
    Returned,
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct AudiobookProgress {
    pub seconds: f64,
    pub transcribed: f64,
    pub paired_with: Option<String>,
    pub match_kind: Option<MatchKind>,
    pub sentences: u32,
    pub exact: u32,
    pub spread: u32,
    pub unread: u32,
    pub done: bool,
}

/// 这本录音: each recording's own settings, synced.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
#[serde(default)]
pub struct SkipSettings {
    /// 片头片尾 跳过 (or 照放).
    pub head_tail: bool,
    /// Talk in the middle not in the book: skipped (default: played).
    pub middle: bool,
    /// 章间音乐 压到 1.5 秒.
    pub shorten_chapter_gaps: bool,
    /// 人声清楚一点: low hum down, the voice up a little.
    pub clearer_voice: bool,
    /// 缩短停顿: pauses over 0.6 s cut to 0.35 s.
    pub shorten_pauses: bool,
    /// 没念的用朗读补上.
    pub fill_unread: bool,
    /// 逐词跟读: the line goes under the word being said, not across the sentence.
    pub word_follow: bool,
}

impl Default for SkipSettings {
    fn default() -> Self {
        SkipSettings { head_tail: true, middle: false, shorten_chapter_gaps: true, clearer_voice: false, shorten_pauses: false, fill_unread: false, word_follow: false }
    }
}

/// What the map says about one moment, for the player: where it is, and whether it should go
/// somewhere else.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct AudiobookMoment {
    pub hit: SyncHit,
    /// Jump here (past 片头 / 片尾 / music / a long pause); None to play on.
    pub skip_to: Option<f64>,
    /// What's being passed, when skipping or in an extra.
    pub extra: Option<ExtraSpan>,
}

/// A sentence of the e-book the recording is laid against.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct BookSentence {
    pub book: String,
    pub section: u32,
    pub sentence: u32,
}

#[derive(Default)]
pub struct AudiobookRuntime {
    pub caps: AudiobookCaps,
    pub inflight: HashSet<String>,
    pub failures: HashMap<String, u32>,
    /// Maps from today's lay-outs, by recording, with the stamp they were made at.
    pub derived: HashMap<String, (String, std::sync::Arc<SyncMap>)>,
}

pub type AudiobookState = Mutex<AudiobookRuntime>;

/// What's kept on this device for one recording.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct Work {
    /// Blocks done (start seconds).
    vad_done: Vec<u64>,
    speech: Vec<(f64, f64)>,
    music: Vec<(f64, f64)>,
    transcribed: Vec<u64>,
    words: Vec<HeardWord>,
    book: Option<String>,
    kind: Option<MatchKind>,
    /// Coarse sentences (when paired) and the windows asked for.
    coarse: Vec<SentCoarse>,
    sec_per_tok: f64,
    windows: Vec<AlignWindow>,
    windows_done: Vec<u32>,
    refined: HashMap<u32, Refined>,
    extras: Vec<(f64, f64, String)>,
    /// What the transcript is made of.
    #[serde(default)]
    units: Units,
}

fn entity(id: &str) -> String {
    format!("ab/{id}")
}

impl YuyueCore {
    fn align_dir(&self, id: &str) -> Result<PathBuf> {
        let d = self.book_dir(id)?.join("align");
        std::fs::create_dir_all(&d)?;
        Ok(d)
    }

    fn work(&self, id: &str) -> Result<Work> {
        let p = self.align_dir(id)?.join("work.json");
        Ok(std::fs::read(&p).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default())
    }

    fn save_work(&self, id: &str, w: &Work) -> Result<()> {
        let p = self.align_dir(id)?.join("work.json");
        std::fs::write(p, serde_json::to_vec(w).map_err(|e| CoreError::Invalid { detail: e.to_string() })?)?;
        Ok(())
    }

    fn file_at(&self, id: &str, t: f64) -> Result<(u32, f64)> {
        let r = self.audio_recording(id.to_string())?;
        let mut at = 0.0;
        for (i, f) in r.files.iter().enumerate() {
            if t < at + f.seconds || i + 1 == r.files.len() {
                return Ok((i as u32, at));
            }
            at += f.seconds;
        }
        Ok((0, 0.0))
    }

    fn pauses_of(w: &Work) -> Vec<SpeechPause> {
        let mut sp = w.speech.clone();
        sp.sort_by(|a, b| a.0.total_cmp(&b.0));
        sp.windows(2).filter(|p| p[1].0 - p[0].1 >= 0.15).map(|p| SpeechPause { t0: p[0].1, t1: p[1].0 }).collect()
    }

    fn lang_of_book(&self, book: &str) -> bool {
        self.manifest(book).map(|m| m.language.starts_with("zh")).unwrap_or(true)
    }

    fn book_sections(&self, book: &str) -> Vec<Vec<String>> {
        let n = self.manifest(book).map(|m| m.sections.len()).unwrap_or(0) as u32;
        (0..n).map(|i| self.sentences(book.to_string(), i).unwrap_or_default()).collect()
    }

    /// Which book on the shelf the transcript reads, by a sample of it.
    fn pair(&self, id: &str, w: &mut Work) -> Result<()> {
        if w.book.is_some() || w.words.len() < 200 {
            return Ok(());
        }
        if let Some(b) = lock(&self.store).get(&entity(id), "book").ok().flatten().and_then(|v| v.as_str().map(str::to_string)) {
            w.book = Some(b);
            w.kind = Some(MatchKind::Same);
            return Ok(());
        }
        let books: Vec<String> = self.books()?.into_iter().filter(|b| matches!(b.format, BookFormat::Epub | BookFormat::Txt | BookFormat::Mobi)).map(|b| b.id).collect();
        let mut best_of: Option<(String, f32, MatchKind)> = None;
        for b in books {
            let zh = self.lang_of_book(&b);
            // The first three minutes and a few minutes from the middle.
            let sample: Vec<HeardWord> = w.words.iter().filter(|x| x.t0 < 180.0).cloned().chain(w.words.iter().skip(w.words.len() / 2).take(400).cloned()).collect();
            let heard = HeardToks::of(&sample, zh, w.units);
            let bt = BookToks::of(&self.book_sections(&b), zh, w.units);
            let ix = KIndex::build(&bt.toks, w.units.k(zh));
            if let Some((_, r, kind)) = best([&ix], &heard.toks)
                && best_of.as_ref().is_none_or(|x| r > x.1) {
                    best_of = Some((b.clone(), r, kind));
                }
        }
        if let Some((b, _, kind)) = best_of {
            lock(&self.store).set(&entity(id), "book", json!(b))?;
            w.book = Some(b);
            w.kind = Some(kind);
        }
        Ok(())
    }

    /// The coarse pass and the windows for the fine one.
    fn lay_coarse(&self, id: &str, w: &mut Work) -> Result<()> {
        let Some(book) = w.book.clone() else { return Ok(()) };
        if !matches!(w.kind, Some(MatchKind::Same | MatchKind::Revised)) {
            return Ok(());
        }
        let zh = self.lang_of_book(&book);
        let bt = BookToks::of(&self.book_sections(&book), zh, w.units);
        let ht = HeardToks::of(&w.words, zh, w.units);
        let r = self.audio_recording(id.to_string())?;
        // The recording's chapters as transcript ranges.
        let mut chunks = Vec::new();
        for (i, c) in r.chapters.iter().enumerate() {
            let a = ht.at(c.start);
            let b = r.chapters.get(i + 1).map(|n| ht.at(n.start)).unwrap_or(ht.toks.len());
            if b > a {
                chunks.push((a, b));
            }
        }
        if chunks.is_empty() {
            chunks.push((0, ht.toks.len()));
        }
        let c: Coarse = coarse_in(&bt, &ht, &chunks, zh, w.units);
        w.extras = c.extras.iter().map(|e| {
            let text = e.text.split('-').filter_map(|x| x.parse::<usize>().ok()).collect::<Vec<_>>();
            let words = match text.as_slice() {
                [a, b] => w.words[*a..=*b].iter().map(|x| x.text.as_str()).collect::<Vec<_>>().join(if zh || w.units == Units::Letters { "" } else { " " }),
                _ => String::new(),
            };
            (e.t0, e.t1, words)
        }).collect();
        w.sec_per_tok = c.sec_per_tok;
        w.coarse = c.sentences;
        w.windows = windows(&w.coarse, &Self::pauses_of(w));
        w.windows_done.clear();
        w.refined.clear();
        Ok(())
    }

    /// Grades and keeps the map.
    fn finish(&self, id: &str, w: &Work) -> Result<SyncMap> {
        let book = w.book.clone().unwrap_or_default();
        let pauses = Self::pauses_of(w);
        let zh = self.lang_of_book(&book);
        let bt = BookToks::of(&self.book_sections(&book), zh, w.units);
        let sounds: Vec<u32> = bt.sentences.iter().map(|s| s.3 - s.2).collect();
        // Snap each refined start into its pause.
        let mut refined: Vec<Option<(Refined, bool)>> = vec![None; w.coarse.len()];
        // Without the model's answer, a placed sentence's own coarse times, marked by a NaN
        // score: they snap into the pauses the same way and are graded more strictly.
        let mut answers: HashMap<u32, Refined> = w.refined.clone();
        for (i, c) in w.coarse.iter().enumerate() {
            if c.class == Class::Aligned && !answers.contains_key(&(i as u32)) {
                answers.insert(i as u32, Refined { index: i as u32, t0: c.t0, t1: c.t1, score: f32::NAN, units: Vec::new() });
            }
        }
        let mut keys: Vec<&u32> = answers.keys().collect();
        keys.sort();
        let mut prev_end: Option<f64> = None;
        for k in keys {
            let mut r = answers[k].clone();
            let (e, s, found) = snap(prev_end.unwrap_or(r.t0), r.t0, &pauses);
            if let (Some(p), true) = (prev_end, found) {
                let _ = p;
                if let Some(Some(last)) = refined.iter_mut().rev().find(|x| x.is_some()) {
                    last.0.t1 = last.0.t1.min(e.max(last.0.t0));
                }
            }
            r.t0 = s;
            prev_end = Some(r.t1);
            refined[*k as usize] = Some((r, found));
        }
        let pins: Vec<(u32, f64)> = self.pins(id).into_iter().filter_map(|(sec, sen, t)| w.coarse.iter().position(|c| c.section == sec && c.sentence == sen).map(|i| (i as u32, t))).collect();
        let g = grade(&w.coarse, &refined, &sounds, w.sec_per_tok.max(0.05), &pins);
        let rec = self.audio_recording(id.to_string())?;
        let first = g.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread)).map(|x| x.t0).fold(f64::MAX, f64::min);
        let last = g.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User | SyncBy::Spread)).map(|x| x.t1).fold(0.0, f64::max);
        // Before the book's first sentence (by its middle: the edges are a breath either way) is
        // 片头, from the very start; after the last is 片尾, to the very end.
        let mut extras: Vec<ExtraSpan> = w
            .extras
            .iter()
            .map(|(a, b, t)| {
                let mid = (a + b) / 2.0;
                let place = if mid <= first { ExtraPlace::Head } else if mid >= last { ExtraPlace::Tail } else { ExtraPlace::Middle };
                let (t0, t1) = match place {
                    ExtraPlace::Head => (0.0, b.min(first - 0.05).max(0.0)),
                    ExtraPlace::Tail => (a.max(last + 0.05), rec.seconds.max(*b)),
                    _ => (*a, *b),
                };
                ExtraSpan { t0, t1, kind: ExtraKind::Speech, place, text: Some(t.clone()) }
            })
            .collect();
        // Loud stretches the transcriber heard no words in: music.
        let mut music = w.music.clone();
        let mut speech = w.speech.clone();
        speech.sort_by(|a, b| a.0.total_cmp(&b.0));
        let starts: Vec<f64> = w.words.iter().map(|x| x.t0).collect();
        music.extend(super::vad::music_of(&speech, &starts));
        for (a, b) in &music {
            let place = if *b <= first + 1.0 { ExtraPlace::Head } else if *a >= last - 1.0 { ExtraPlace::Tail } else if b - a >= 4.0 && rec.chapters.iter().any(|c| (c.start - a).abs() < 10.0 || (c.start - b).abs() < 10.0) { ExtraPlace::ChapterGap } else { continue };
            extras.push(ExtraSpan { t0: *a, t1: *b, kind: ExtraKind::Music, place, text: None });
        }
        if first > 1.0 && first < f64::MAX && !extras.iter().any(|e| e.place == ExtraPlace::Head) {
            extras.push(ExtraSpan { t0: 0.0, t1: first - 0.2, kind: ExtraKind::Speech, place: ExtraPlace::Head, text: None });
        }
        // What the model found of the book isn't talk outside it: an extra is cut back off any
        // sentence it heard (the transcript can't match a sentence of two characters).
        let heard: Vec<(f64, f64)> = g.iter().zip(&refined).filter(|(x, r)| matches!(x.by, SyncBy::Auto | SyncBy::User) && r.as_ref().is_some_and(|r| !r.0.score.is_nan())).map(|(x, _)| (x.t0, x.t1)).collect();
        for e in extras.iter_mut().filter(|e| e.kind == ExtraKind::Speech) {
            for &(a, b) in &heard {
                if a < e.t1 && b > e.t0 {
                    if a - e.t0 >= e.t1 - b { e.t1 = a - 0.05 } else { e.t0 = b + 0.05 }
                }
            }
        }
        extras.retain(|e| e.t1 - e.t0 > 0.3);
        extras.sort_by(|a, b| a.t0.total_cmp(&b.t0));
        let chapters = rec.chapters.iter().map(|c| SyncChapter { title: c.title.clone().unwrap_or_default(), t0: c.start, book_section: g.iter().filter(|x| matches!(x.by, SyncBy::Auto | SyncBy::User) && x.t0 >= c.start).min_by(|a, b| a.t0.total_cmp(&b.t0)).map(|x| x.section) }).collect();
        let m = SyncMap::from_graded(id, &book, w.kind.unwrap_or(MatchKind::Same), rec.files.iter().map(|f| f.seconds).collect(), &g, extras, chapters, &format!("{ALIGNER}:{}", self.text_digest(&book)));
        self.save_map(id, &m)?;
        Ok(m)
    }

    fn text_digest(&self, book: &str) -> String {
        let mut h = blake3::Hasher::new();
        for s in self.book_sections(book) {
            for t in s {
                h.update(t.as_bytes());
            }
        }
        h.finalize().to_hex()[..12].to_string()
    }

    fn save_map(&self, id: &str, m: &SyncMap) -> Result<()> {
        let packed = base64::engine::general_purpose::STANDARD.encode(m.pack());
        lock(&self.store).set(&entity(id), "map", json!(packed))
    }

    /// Several pins at once (微调 saves a chapter's moved sentences together); the map is made
    /// again once, and only when one of them is new or moved.
    pub(crate) fn audiobook_pin_many(&self, book_id: &str, new: &[(u32, u32, f64)]) -> Result<()> {
        let mut pins = self.pins(book_id);
        let mut changed = false;
        for &(section, sentence, seconds) in new {
            if pins.iter().any(|p| p.0 == section && p.1 == sentence && (p.2 - seconds).abs() < 0.001) {
                continue;
            }
            pins.retain(|p| !(p.0 == section && p.1 == sentence));
            pins.push((section, sentence, seconds));
            changed = true;
        }
        if !changed {
            return Ok(());
        }
        lock(&self.store).set(&entity(book_id), "pins", serde_json::to_value(&pins).unwrap_or(Value::Null))?;
        // Made again where the recording is; elsewhere the pins wait for it.
        if let Ok(w) = self.work(book_id)
            && !w.coarse.is_empty() {
                self.finish(book_id, &w)?;
            }
        Ok(())
    }

    fn pins(&self, id: &str) -> Vec<(u32, u32, f64)> {
        lock(&self.store).get(&entity(id), "pins").ok().flatten().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
    }

    /// The new pipeline's map, when it has made one.
    fn stored_map(&self, id: &str) -> Result<Option<SyncMap>> {
        let v = lock(&self.store).get(&entity(id), "map")?;
        Ok(v.and_then(|v| v.as_str().map(str::to_string)).and_then(|s| base64::engine::general_purpose::STANDARD.decode(s).ok()).and_then(|b| SyncMap::unpack(&b)))
    }

    /// The map the player and the page go by: the new pipeline's, else today's lay-out graded.
    /// Kept between calls (the player asks several times a second) until either changes.
    fn map_of(&self, id: &str) -> Result<Option<std::sync::Arc<SyncMap>>> {
        let stamp = {
            let store = lock(&self.store);
            format!("{}|{}", store.last_stamp(&entity(id))?.unwrap_or_default(), store.last_stamp(&format!("listen/{id}"))?.unwrap_or_default())
        };
        if let Some((s, m)) = lock(&self.audiobook).derived.get(id)
            && *s == stamp {
                return Ok(Some(m.clone()));
            }
        let map = match self.stored_map(id)? {
            Some(m) => Some(m),
            None => self.listening(id.to_string())?.filter(|l| !l.sentences.is_empty()).map(|l| {
                let rec = self.audio_recording(id.to_string()).ok();
                let total = rec.as_ref().map(|r| r.seconds).filter(|t| *t > 0.0).unwrap_or(l.through);
                let chapters: Vec<(String, f64)> = rec.map(|r| r.chapters.iter().map(|c| (c.title.clone().unwrap_or_default(), c.start)).collect()).unwrap_or_default();
                // Lengths in letters, a chapter read at a time.
                let mut lens: HashMap<u32, Vec<u32>> = HashMap::new();
                for x in &l.sentences {
                    lens.entry(x.section).or_insert_with(|| {
                        self.sentences(l.book.clone(), x.section).unwrap_or_default().iter().map(|t| t.chars().filter(|c| c.is_alphanumeric()).count().max(1) as u32).collect()
                    });
                }
                super::fromlisten::map_of(&l, total, &chapters, &|sec, n| lens.get(&sec).and_then(|v| v.get(n as usize)).copied().unwrap_or(10))
            }),
        };
        let Some(map) = map else { return Ok(None) };
        let m = std::sync::Arc::new(map);
        lock(&self.audiobook).derived.insert(id.to_string(), (stamp, m.clone()));
        Ok(Some(m))
    }

    /// Windows the fine pass has still to do: the model is here, and they're neither done nor
    /// given up on (failed three times).
    fn fine_left(&self, book_id: &str, w: &Work) -> bool {
        let rt = lock(&self.audiobook);
        rt.caps.align && (0..w.windows.len()).any(|i| !w.windows_done.contains(&(i as u32)) && rt.failures.get(&format!("align:{book_id}:{i}")).copied().unwrap_or(0) < 3)
    }

    fn claim(&self, id: String) -> bool {
        let mut rt = lock(&self.audiobook);
        if rt.inflight.contains(&id) || rt.failures.get(&id).copied().unwrap_or(0) >= 3 {
            return false;
        }
        rt.inflight.insert(id);
        true
    }
}

#[uniffi::export]
impl YuyueCore {
    pub fn audiobook_set_caps(&self, caps: AudiobookCaps) {
        lock(&self.audiobook).caps = caps;
    }

    /// The next piece of work for a recording, nearest `focus` (seconds) first.
    pub fn audiobook_next_task(&self, book_id: String, focus: Option<f64>) -> Result<Option<AudiobookTask>> {
        let caps = lock(&self.audiobook).caps.clone();
        let rec = self.audio_recording(book_id.clone())?;
        let w = self.work(&book_id)?;
        // A map made already is done with, unless it was made without the fine pass and the
        // model is here now (or the work for it was never kept on this device: another one's).
        let fine_left = self.fine_left(&book_id, &w);
        if self.stored_map(&book_id)?.is_some() && !fine_left {
            return Ok(None);
        }
        let total = rec.seconds;
        let nblocks = (total / BLOCK).ceil() as u64;
        let focus_block = (focus.unwrap_or(0.0) / BLOCK) as u64;
        // The block being listened to and the two after it, then the rest in order — all of it
        // only when the device may (plugged in, or asked).
        let mut order: Vec<u64> = (focus_block..(focus_block + 3).min(nblocks)).collect();
        if caps.whole_book_now {
            let rest: Vec<u64> = (0..nblocks).filter(|b| !order.contains(b)).collect();
            order.extend(rest);
        }
        let lang = w.book.as_deref().map(|b| if self.lang_of_book(b) { "zh" } else { "en" }).unwrap_or("auto").to_string();
        for b in &order {
            let from = *b as f64 * BLOCK;
            let to = (from + BLOCK).min(total);
            let (file, offset) = self.file_at(&book_id, from)?;
            if caps.vad && !w.vad_done.contains(b) {
                let id = format!("vad:{book_id}:{b}");
                if self.claim(id.clone()) {
                    return Ok(Some(AudiobookTask::Vad { id, file, from, to, file_offset: offset }));
                }
            }
            if caps.transcribe && !w.transcribed.contains(b) && (w.vad_done.contains(b) || !caps.vad) {
                let spans: Vec<TimeSpan> = if caps.vad { speech_units(&w.speech, from, to) } else { vec![TimeSpan { t0: from, t1: to }] };
                let id = format!("asr:{book_id}:{b}");
                if self.claim(id.clone()) {
                    return Ok(Some(AudiobookTask::Transcribe { id, file, spans, lang: lang.clone(), careful: false, file_offset: offset }));
                }
            }
        }
        if caps.align && !w.windows.is_empty() {
            let book = w.book.clone().unwrap_or_default();
            let sections = self.book_sections(&book);
            for (i, win) in w.windows.iter().enumerate() {
                if w.windows_done.contains(&(i as u32)) {
                    continue;
                }
                // Near what's being listened to first, unless the whole book may be done.
                if !caps.whole_book_now && focus.is_some_and(|f| (win.from - f).abs() > BLOCK * 3.0) {
                    continue;
                }
                let sentences: Vec<String> = win.sentences.iter().map(|&k| {
                    let c = &w.coarse[k as usize];
                    sections[c.section as usize][c.sentence as usize].clone()
                }).collect();
                let (file, offset) = self.file_at(&book_id, win.from)?;
                let id = format!("align:{book_id}:{i}");
                if self.claim(id.clone()) {
                    return Ok(Some(AudiobookTask::Align { id, file, from: win.from, to: win.to, sentences, lang: lang.clone(), file_offset: offset }));
                }
            }
        }
        Ok(None)
    }

    pub fn audiobook_submit(&self, book_id: String, task_id: String, result: AudiobookResult) -> Result<AudiobookProgress> {
        lock(&self.audiobook).inflight.remove(&task_id);
        let mut w = self.work(&book_id)?;
        let parts: Vec<&str> = task_id.split(':').collect();
        let n: u64 = parts.get(2).and_then(|x| x.parse().ok()).unwrap_or(0);
        match (parts.first().copied(), result) {
            (_, AudiobookResult::Returned) => {}
            (_, AudiobookResult::Failed { .. }) => {
                *lock(&self.audiobook).failures.entry(task_id).or_default() += 1;
            }
            (Some("vad"), AudiobookResult::Vad { speech, music }) => {
                w.speech.extend(speech.iter().map(|s| (s.t0, s.t1)));
                w.music.extend(music.iter().map(|s| (s.t0, s.t1)));
                if !w.vad_done.contains(&n) {
                    w.vad_done.push(n);
                }
            }
            (Some("asr"), AudiobookResult::Transcribe { words }) => {
                let units = if lock(&self.audiobook).caps.letters { Units::Letters } else { Units::Sounds };
                // A transcript of the other kind (another transcriber now) starts over.
                if units != w.units {
                    w.units = units;
                    w.words.clear();
                    w.transcribed.clear();
                    w.book = None;
                    w.kind = None;
                }
                let from = n as f64 * BLOCK;
                w.words.retain(|x| x.t0 < from || x.t0 >= from + BLOCK);
                w.words.extend(words);
                w.words.sort_by(|a, b| a.t0.total_cmp(&b.t0));
                if !w.transcribed.contains(&n) {
                    w.transcribed.push(n);
                }
                self.pair(&book_id, &mut w)?;
                self.lay_coarse(&book_id, &mut w)?;
            }
            (Some("align"), AudiobookResult::Align { sentences }) => {
                let i = n as u32;
                if let Some(win) = w.windows.get(i as usize).cloned() {
                    for r in sentences {
                        // The app answers by the window's own order.
                        if let Some(&k) = win.sentences.get(r.index as usize) {
                            w.refined.insert(k, Refined { index: k, ..r });
                        }
                    }
                    if !w.windows_done.contains(&i) {
                        w.windows_done.push(i);
                    }
                }
            }
            _ => {}
        }
        let rec = self.audio_recording(book_id.clone())?;
        let all_heard = w.transcribed.len() as u64 >= (rec.seconds / BLOCK).ceil() as u64;
        // A window that failed three times stays as the coarse pass placed it.
        let given_up = {
            let rt = lock(&self.audiobook);
            (0..w.windows.len()).filter(|i| !w.windows_done.contains(&(*i as u32)) && rt.failures.get(&format!("align:{book_id}:{i}")).copied().unwrap_or(0) >= 3).count()
        };
        let all_fine = !w.windows.is_empty() && w.windows_done.len() + given_up >= w.windows.len();
        let caps = lock(&self.audiobook).caps.clone();
        // Without the fine pass, only when there's nothing better: a lay-out from the alignment
        // model already here isn't replaced by coarse times.
        let coarse_will_do = !caps.align && self.listening(book_id.clone())?.is_none_or(|l| l.sentences.is_empty());
        if all_heard && rec.chapters.len() <= 1 && w.units == Units::Sounds {
            let spoken = super::import::spoken_chapters(&w.words);
            if spoken.len() >= 2 {
                let _ = self.set_inferred_chapters(&book_id, &spoken);
            }
        }
        if all_heard && w.book.is_some() && (all_fine || coarse_will_do) {
            self.finish(&book_id, &w)?;
        }
        self.save_work(&book_id, &w)?;
        self.audiobook_progress(book_id)
    }

    pub fn audiobook_progress(&self, book_id: String) -> Result<AudiobookProgress> {
        let rec = self.audio_recording(book_id.clone())?;
        let w = self.work(&book_id)?;
        let map = self.map_of(&book_id)?;
        let count = |b: SyncBy| map.as_ref().map(|m| m.rows.iter().filter(|r| r.by == b).count() as u32).unwrap_or(0);
        Ok(AudiobookProgress {
            seconds: rec.seconds,
            transcribed: (w.transcribed.len() as f64 * BLOCK).min(rec.seconds),
            paired_with: w.book.clone(),
            match_kind: w.kind,
            sentences: w.coarse.len() as u32,
            exact: count(SyncBy::Auto) + count(SyncBy::User),
            spread: count(SyncBy::Spread),
            unread: count(SyncBy::Unread) + w.coarse.iter().filter(|c| c.class == Class::Unread).count() as u32 * map.is_none() as u32,
            // This pipeline's own map, with the fine pass through when the model is here: a
            // lay-out from the alignment model alone doesn't count (the work goes on past it).
            done: self.stored_map(&book_id)?.is_some() && !self.fine_left(&book_id, &w),
        })
    }

    /// The map, from this device or another: the new pipeline's when it has made one, else
    /// today's lay-out graded the same way.
    pub fn audiobook_map(&self, book_id: String) -> Result<Option<SyncMap>> {
        Ok(self.map_of(&book_id)?.map(|m| (*m).clone()))
    }

    /// One chapter of the e-book as the recording has it: each sentence's grade and time, for
    /// the page (淡 for the unread, the banner for a chapter only placed as a whole). `book_id`
    /// is the recording.
    pub fn audiobook_section_rows(&self, book_id: String, section: u32) -> Result<Vec<super::map::SyncRow>> {
        Ok(self.map_of(&book_id)?.map(|m| m.rows.iter().filter(|r| r.section == section).cloned().collect()).unwrap_or_default())
    }

    /// What's in the recording and not in the book.
    pub fn audiobook_extras(&self, book_id: String) -> Result<Vec<ExtraSpan>> {
        Ok(self.map_of(&book_id)?.map(|m| m.extras.clone()).unwrap_or_default())
    }

    /// Everything the player asks each tick: where `seconds` is, and where to go instead under
    /// this recording's settings.
    pub fn audiobook_moment(&self, book_id: String, seconds: f64) -> Result<AudiobookMoment> {
        let Some(m) = self.map_of(&book_id)? else { return Ok(AudiobookMoment { hit: SyncHit::Nothing, skip_to: None, extra: None }) };
        let s = self.audiobook_skip_settings(book_id)?;
        let hit = m.time_to_loc(seconds);
        let extra = match &hit {
            SyncHit::Extra { index } => m.extras.get(*index as usize).cloned(),
            _ => None,
        };
        let mut skip_to = extra.as_ref().and_then(|e| skip_past(e, seconds, &s));
        if skip_to.is_none() && s.shorten_pauses {
            // Into a long pause by half of what's kept: on to half before its end.
            let half = super::fromlisten::PAUSE_KEPT / 2.0;
            skip_to = super::fromlisten::long_pauses(&m, seconds - 10.0)
                .into_iter()
                .find(|(a, b)| seconds >= a + half && seconds < *b)
                .map(|(_, b)| b - half)
                .filter(|to| *to > seconds + 0.05);
        }
        Ok(AudiobookMoment { hit, skip_to, extra })
    }

    /// Seconds 缩短停顿 would save from `seconds` to the end.
    pub fn audiobook_pause_savings(&self, book_id: String, seconds: f64) -> Result<f64> {
        Ok(self.map_of(&book_id)?.map(|m| super::fromlisten::long_pauses(&m, seconds).iter().map(|(a, b)| b - a - super::fromlisten::PAUSE_KEPT).sum()).unwrap_or(0.0))
    }

    /// 这本已经省了…: seconds 缩短停顿 has saved on this recording so far.
    pub fn audiobook_saved(&self, book_id: String) -> Result<f64> {
        Ok(lock(&self.store).get(&entity(&book_id), "saved")?.and_then(|v| v.as_f64()).unwrap_or(0.0))
    }

    pub fn audiobook_add_saved(&self, book_id: String, seconds: f64) -> Result<()> {
        let have = self.audiobook_saved(book_id.clone())?;
        lock(&self.store).set(&entity(&book_id), "saved", json!(((have + seconds.max(0.0)) * 10.0).round() / 10.0))
    }

    /// 书签键 划线: the sentence just heard at `seconds` (0.8 s back, so a press as a sentence
    /// ends takes that one), in the e-book.
    pub fn audiobook_heard_sentence(&self, book_id: String, seconds: f64) -> Result<Option<BookSentence>> {
        let Some(m) = self.map_of(&book_id)? else { return Ok(None) };
        Ok(match m.time_to_loc((seconds - 0.8).max(0.0)) {
            SyncHit::Sentence { section, sentence, .. } | SyncHit::Gap { section, sentence } => Some(BookSentence { book: m.book_id.clone(), section, sentence }),
            _ => None,
        })
    }

    pub fn audiobook_time_to_loc(&self, book_id: String, seconds: f64) -> Result<SyncHit> {
        Ok(self.map_of(&book_id)?.map(|m| m.time_to_loc(seconds)).unwrap_or(SyncHit::Nothing))
    }

    pub fn audiobook_loc_to_time(&self, book_id: String, section: u32, sentence: u32) -> Result<Option<f64>> {
        Ok(self.map_of(&book_id)?.and_then(|m| m.loc_to_time(section, sentence)))
    }

    /// The previous or next sentence from `seconds`; None without a map (the app steps 15 s).
    pub fn audiobook_step(&self, book_id: String, seconds: f64, forward: bool) -> Result<Option<f64>> {
        Ok(self.map_of(&book_id)?.and_then(|m| m.step(seconds, forward)))
    }

    pub fn audiobook_sentence_bounds(&self, book_id: String, seconds: f64) -> Result<Option<TimeSpan>> {
        Ok(self.map_of(&book_id)?.and_then(|m| m.sentence_bounds(seconds)).map(|(t0, t1)| TimeSpan { t0, t1 }))
    }

    /// Where to play from after a pause of `paused` seconds at `seconds`.
    pub fn audiobook_resume_at(&self, book_id: String, seconds: f64, paused: f64) -> Result<f64> {
        let map = self.map_of(&book_id)?;
        let w = self.work(&book_id)?;
        let pauses = Self::pauses_of(&w);
        // Paragraphs: the first sentence after a long pause in the recording stands for one.
        let para = |s: u32, n: u32| -> (u32, u32) {
            let Some(m) = &map else { return (s, n) };
            let rows: Vec<_> = m.rows.iter().filter(|r| r.section == s && r.sentence <= n).collect();
            for pair in rows.windows(2).rev() {
                if pair[1].t0_ms.saturating_sub(pair[0].t1_ms) >= 900 {
                    return (s, pair[1].sentence);
                }
            }
            (s, rows.first().map(|r| r.sentence).unwrap_or(n))
        };
        Ok(resume_at(seconds, paused, map.as_deref(), &pauses, &para))
    }

    /// “从这里对准”: this sentence starts at this moment. Kept through every recompute.
    pub fn audiobook_pin(&self, book_id: String, section: u32, sentence: u32, seconds: f64) -> Result<()> {
        self.audiobook_pin_many(&book_id, &[(section, sentence, seconds)])
    }

    /// 划线搬家: the highlights (and their thoughts) on `from` (a 文字版) moved to the same words in
    /// `to` (the real e-book), found by reading; the ones whose place isn't sure stay on the 文字版.
    pub fn audiobook_move_marks(&self, from: String, to: String) -> Result<MarksMoved> {
        let to_n = self.manifest(&to)?.sections.len() as u32;
        let to_dir = self.book_dir(&to)?;
        let sections: Vec<Vec<String>> = (0..to_n).map(|i| crate::book::sentences(&to_dir, i).unwrap_or_default()).collect();
        let from_n = self.manifest(&from)?.sections.len().max(1) as f64;
        let chinese = self.lang_of_book(&to);
        let mut out = MarksMoved { moved: 0, left: 0 };
        for m in self.marks(from.clone())? {
            let near = (m.section as f64 + 0.5) / from_n;
            let Some((sec, (s0, o0), (s1, o1))) = super::textbook::find_words(&m.text, &sections, chinese, Some(near)) else {
                out.left += 1;
                continue;
            };
            // The words as the real book has them.
            let ss = &sections[sec as usize];
            let text: String = (s0..=s1)
                .map(|k| {
                    let t: Vec<char> = ss[k as usize].chars().collect();
                    let a = if k == s0 { o0 as usize } else { 0 };
                    let b = if k == s1 { (o1 as usize).min(t.len()) } else { t.len() };
                    t[a.min(b)..b].iter().collect::<String>()
                })
                .collect();
            let p = |s, o| crate::notes::TextPoint { sentence: s, offset: o };
            if self.add_mark(to.clone(), sec, p(s0, o0), p(s1, o1), text, m.style, m.note.clone()).is_ok() {
                self.delete_mark(from.clone(), m.id)?;
                out.moved += 1;
            } else {
                out.left += 1;
            }
        }
        lock(&self.store).set(&format!("textversion/{from}"), "marks_left", json!(out.left))?;
        Ok(out)
    }

    /// Pairs a recording with a book by hand (or None to unpair); the work starts over. Paired
    /// away from its 文字版 to a real e-book, the 文字版's highlights go along (划线搬家).
    pub fn audiobook_pair(&self, book_id: String, with: Option<String>) -> Result<()> {
        let was = lock(&self.store).get(&entity(&book_id), "book").ok().flatten().and_then(|v| v.as_str().map(str::to_string));
        if let (Some(old), Some(new)) = (&was, &with)
            && old != new && self.audiobook_is_text_version(old.clone()) && !self.audiobook_is_text_version(new.clone()) {
                let _ = self.audiobook_move_marks(old.clone(), new.clone());
            }
        lock(&self.store).set(&entity(&book_id), "book", with.clone().map(Value::from).unwrap_or(Value::Null))?;
        lock(&self.store).set(&entity(&book_id), "map", Value::Null)?;
        let mut w = self.work(&book_id)?;
        w.book = with;
        w.kind = w.book.as_ref().map(|_| MatchKind::Same);
        self.lay_coarse(&book_id, &mut w)?;
        self.save_work(&book_id, &w)
    }

    pub fn audiobook_skip_settings(&self, book_id: String) -> Result<SkipSettings> {
        Ok(lock(&self.store).get(&entity(&book_id), "skip")?.and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default())
    }

    pub fn audiobook_set_skip_settings(&self, book_id: String, s: SkipSettings) -> Result<()> {
        lock(&self.store).set(&entity(&book_id), "skip", serde_json::to_value(s).unwrap_or(Value::Null))
    }

    /// Where playing should jump past what's not in the book, at `seconds`; None to play on.
    pub fn audiobook_skip_to(&self, book_id: String, seconds: f64) -> Result<Option<f64>> {
        let Some(m) = self.map_of(&book_id)? else { return Ok(None) };
        let s = self.audiobook_skip_settings(book_id)?;
        Ok(m.extras.iter().find(|e| seconds >= e.t0 && seconds < e.t1).and_then(|e| skip_past(e, seconds, &s)))
    }

    /// The book's sentences the recording doesn't read (for the 用朗读补上 option and the dim
    /// text): (section, sentence).
    pub fn audiobook_unread(&self, book_id: String) -> Result<Vec<UnreadSentence>> {
        Ok(self.map_of(&book_id)?.map(|m| m.rows.iter().filter(|r| r.by == SyncBy::Unread).map(|r| UnreadSentence { section: r.section, sentence: r.sentence }).collect()).unwrap_or_default())
    }

    /// 人声检测 on the app's measure of loudness: `levels` in dB, `frame_seconds` apart, from
    /// `from` seconds into the recording. The app hands the answer back as the Vad result.
    pub fn audiobook_speech(&self, levels: Vec<f32>, frame_seconds: f64, from: f64) -> Vec<TimeSpan> {
        super::vad::speech(&levels, frame_seconds, from)
    }

    /// 精修 one window from the alignment model's output over it: `emissions` is `frames` rows
    /// of the model's letters, covering `seconds` from `from`. The app hands the answer back as
    /// the Align result.
    pub fn audiobook_align_window(&self, sentences: Vec<String>, lang: String, emissions: Vec<f32>, frames: u32, seconds: f64, from: f64) -> Vec<Refined> {
        let classes = crate::forced::LETTERS.chars().count();
        if emissions.len() != frames as usize * classes {
            return Vec::new();
        }
        super::refine::align_window(&sentences, !lang.starts_with("en"), &emissions, frames as usize, seconds, from)
    }

    /// 听写 by the alignment model, for a device with no transcriber: its letters over a
    /// stretch (`emissions`: `frames` rows covering `seconds` from `from`), handed back as the
    /// Transcribe result with `letters` set in the caps.
    pub fn audiobook_heard_letters(&self, emissions: Vec<f32>, frames: u32, seconds: f64, from: f64) -> Vec<HeardWord> {
        super::anchor::heard_letters(&emissions, frames as usize, seconds, from)
    }

    /// `audiobook_heard_letters` over [from, to) of the whole recording, from the model's output
    /// the app saved for one file (`path`: little-endian f32, `file_seconds` long, the file
    /// starting `offset` into the recording). Empty when there's no such output.
    pub fn audiobook_heard_letters_saved(&self, path: String, file_seconds: f64, offset: f64, from: f64, to: f64) -> Vec<HeardWord> {
        match saved_slice(&path, file_seconds, from - offset, to - offset) {
            Some((em, frames, a, b)) => super::anchor::heard_letters(&em, frames, b - a, a + offset),
            None => Vec::new(),
        }
    }

    /// `audiobook_align_window` from the model's output the app saved for one file, as above.
    /// None when there's no such output (the app runs the model on the window instead).
    pub fn audiobook_align_window_saved(&self, sentences: Vec<String>, lang: String, path: String, file_seconds: f64, offset: f64, from: f64, to: f64) -> Option<Vec<Refined>> {
        let (em, frames, a, b) = saved_slice(&path, file_seconds, from - offset, to - offset)?;
        Some(super::refine::align_window(&sentences, !lang.starts_with("en"), &em, frames, b - a, a + offset))
    }

    /// 字级时间: when each character (Chinese) or word (English) of a sentence is read, where the
    /// fine pass placed it; kept on this device only. For 搜索 to jump to a word, 点一个词从这里念,
    /// and the word-by-word highlight.
    pub fn audiobook_units(&self, book_id: String, section: u32, sentence: u32) -> Result<Vec<UnitTime>> {
        let w = self.work(&book_id)?;
        let Some(k) = w.coarse.iter().position(|c| c.section == section && c.sentence == sentence) else { return Ok(Vec::new()) };
        let Some(r) = w.refined.get(&(k as u32)) else { return Ok(Vec::new()) };
        let book = w.book.clone().unwrap_or_default();
        let zh = self.lang_of_book(&book);
        let text = self.sentences(book, section)?.into_iter().nth(sentence as usize).unwrap_or_default();
        Ok(crate::forced::units(&text, zh).into_iter().zip(&r.units).filter(|(_, t)| **t >= 0.0).map(|((at, _), t)| UnitTime { at, seconds: *t }).collect())
    }

    /// Where the word at character `at` of a sentence is read: its own time when the fine pass
    /// placed it, else the sentence's start.
    pub fn audiobook_word_time(&self, book_id: String, section: u32, sentence: u32, at: u32) -> Result<Option<f64>> {
        let units = self.audiobook_units(book_id.clone(), section, sentence)?;
        // The sentence's first word: the sentence's own start (its breath already left in).
        if let Some(i) = units.iter().rposition(|u| u.at <= at)
            && i > 0 {
                // A breath before the word's first sound, so its consonant isn't cut; never
                // into the word before.
                return Ok(Some((units[i].seconds - WORD_LEAD).max(units[i - 1].seconds)));
            }
        self.audiobook_loc_to_time(book_id, section, sentence)
    }

    /// 导出带朗读时间的电子书: the e-book this recording is laid against, with the recording and
    /// each sentence's time, as an EPUB 3 (Media Overlays) at `out`. Spread sentences go as one
    /// stretch, not as times they don't have.
    pub fn audiobook_export_epub(&self, book_id: String, out: String) -> Result<()> {
        let Some(map) = self.map_of(&book_id)? else { return Err(CoreError::not_found("map")) };
        let book = map.book_id.clone();
        let m = self.manifest(&book)?;
        let dir = self.book_dir(&book)?;
        let mut sections = Vec::new();
        let mut resources: Vec<(String, std::path::PathBuf)> = Vec::new();
        for (i, info) in m.sections.iter().enumerate() {
            let Ok(html) = std::fs::read_to_string(dir.join("s").join(format!("{i}.html"))) else { continue };
            // Pictures and the like the section points at, by their path under r/.
            let mut rest = html.as_str();
            while let Some(k) = rest.find("\"../r/") {
                let tail = &rest[k + 6..];
                let end = tail.find('"').unwrap_or(0);
                let name = tail[..end].to_string();
                if !name.is_empty() && !resources.iter().any(|r| r.0 == name)
                    && let Some(p) = crate::book::resource_path(&dir, &name) {
                        resources.push((name, p));
                    }
                rest = &tail[end..];
            }
            sections.push((i as u32, info.title.clone().unwrap_or_default(), html));
        }
        let rec = self.audio_recording(book_id.clone())?;
        let audio: Vec<(String, std::path::PathBuf, f64)> = rec.files.iter().enumerate().map(|(i, f)| {
            let ext = std::path::Path::new(&f.path).extension().and_then(|e| e.to_str()).unwrap_or("m4a").to_lowercase();
            (format!("{i:03}.{ext}"), std::path::PathBuf::from(&f.path), f.seconds)
        }).collect();
        let author = m.authors.join(&crate::i18n::tr("、", ", "));
        let p = super::export::Package { id: &book, title: &m.title, author: &author, lang: &m.language, sections, audio, resources, map: &map };
        super::export::write_epub(&p, std::path::Path::new(&out))?;
        Ok(())
    }

    /// 文字版: a recording with no e-book made into one from what was heard (`words`, the whole
    /// recording transcribed with care): punctuated and cut into paragraphs by its pauses, into
    /// chapters by its marks, brought onto the shelf as a book of its own and laid against the
    /// recording. The page says where its words came from (`audiobook_is_text_version`).
    pub fn audiobook_make_text_version(&self, book_id: String, words: Vec<HeardWord>, title: String) -> Result<crate::BookSummary> {
        if words.is_empty() {
            return Err(CoreError::Invalid { detail: "nothing heard".into() });
        }
        let rec = self.audio_recording(book_id.clone())?;
        let mut marks: Vec<crate::audio::AudioMark> = rec.chapters.iter().map(|c| crate::audio::AudioMark { title: c.title.clone(), start: c.start }).collect();
        if marks.len() <= 1 {
            let spoken = super::import::spoken_chapters(&words);
            if spoken.len() >= 2 {
                let _ = self.set_inferred_chapters(&book_id, &spoken);
                marks = spoken;
            }
        }
        let cjk = words.iter().flat_map(|w| w.text.chars()).filter(|c| ('\u{4e00}'..='\u{9fff}').contains(c)).count();
        let latin = words.iter().flat_map(|w| w.text.chars()).filter(char::is_ascii_alphabetic).count();
        let chapters = super::textbook::make(&words, &marks, cjk * 3 >= latin);
        let dir = std::env::temp_dir().join(format!("yuyue-text-{book_id}"));
        std::fs::create_dir_all(&dir)?;
        let name: String = title.chars().map(|c| if c == '/' || c == '\\' || c == ':' { '-' } else { c }).collect();
        let path = dir.join(format!("{name}.txt"));
        // The book's name as its first line: the plain-text import takes its title from there.
        std::fs::write(&path, format!("{title}\n\n{}", super::textbook::as_text(&chapters)))?;
        let made = self.import_book(path.to_string_lossy().into_owned())?;
        let _ = std::fs::remove_dir_all(&dir);
        lock(&self.store).set(&format!("textversion/{}", made.id), "from", json!(book_id))?;
        self.audiobook_pair(book_id, Some(made.id.clone()))?;
        Ok(made)
    }

    /// A book that carries its own reading (EPUB 3 Media Overlays): the recording out of it onto
    /// the shelf as an 有声书, laid against the book by the package's own times. Returns the
    /// recording's id, or None when the book has no overlays. Called when an EPUB comes in.
    pub fn audiobook_import_overlays(&self, book_id: String) -> Result<Option<String>> {
        let epub = std::path::PathBuf::from(self.original_file(book_id.clone())?);
        let Some(o) = super::overlay::read(&epub) else { return Ok(None) };
        let dir = std::env::temp_dir().join(format!("yuyue-overlay-{book_id}"));
        let Some(files) = super::overlay::extract_audio(&epub, &o, &dir) else { return Ok(None) };
        let m = self.manifest(&book_id)?;
        let rec = self.import_audiobook(crate::audio::AudioImport {
            files: files.iter().map(|(p, s)| crate::audio::AudioFile { path: p.to_string_lossy().into_owned(), seconds: *s }).collect(),
            title: m.title.clone(),
            authors: m.authors.clone(),
            marks: Vec::new(),
            cover: None,
        })?;
        let _ = std::fs::remove_dir_all(&dir);
        // Each file's start in the whole recording.
        let mut starts: HashMap<String, f64> = HashMap::new();
        let mut at = 0.0;
        for (a, (_, s)) in o.audio.iter().zip(&files) {
            starts.insert(a.clone(), at);
            at += s;
        }
        // Each par on its sentence.
        let dir = self.book_dir(&book_id)?;
        let sections: Vec<String> = (0..m.sections.len()).map(|i| std::fs::read_to_string(dir.join("s").join(format!("{i}.html"))).unwrap_or_default()).collect();
        let mut placed: Vec<(u32, u32, f64, f64)> = o
            .pars
            .iter()
            .filter_map(|(_, frag, a, b, e)| {
                let (sec, sen) = super::overlay::locate(&sections, frag)?;
                let off = starts.get(a).copied().unwrap_or(0.0);
                Some((sec, sen, off + b, off + e))
            })
            .collect();
        placed.sort_by(|x, y| (x.0, x.1).cmp(&(y.0, y.1)).then(x.2.total_cmp(&y.2)));
        placed.dedup_by(|a, b| a.0 == b.0 && a.1 == b.1);
        // A par running over several sentences: its time shared out by length among them.
        let mut graded = Vec::new();
        for (i, &(sec, sen, t0, t1)) in placed.iter().enumerate() {
            let texts = self.sentences(book_id.clone(), sec).unwrap_or_default();
            let upto = placed.get(i + 1).filter(|n| n.0 == sec).map(|n| n.1).unwrap_or(sen + 1).max(sen + 1).min(texts.len() as u32);
            let lens: Vec<f64> = (sen..upto).map(|k| texts.get(k as usize).map(|t| t.chars().count().max(1)).unwrap_or(1) as f64).collect();
            let total: f64 = lens.iter().sum();
            let mut t = t0;
            for (j, l) in lens.iter().enumerate() {
                let d = (t1 - t0) * l / total;
                graded.push(super::grade::Graded { section: sec, sentence: sen + j as u32, t0: t, t1: t + d, by: if lens.len() == 1 { SyncBy::Auto } else { SyncBy::Spread }, conf: 1.0, no_gap: false });
                t += d;
            }
        }
        if graded.is_empty() {
            return Ok(Some(rec.id));
        }
        let map = SyncMap::from_graded(&rec.id, &book_id, MatchKind::Imported, files.iter().map(|f| f.1).collect(), &graded, Vec::new(), Vec::new(), &format!("{ALIGNER}:imported"));
        self.save_map(&rec.id, &map)?;
        lock(&self.store).set(&entity(&rec.id), "book", json!(book_id))?;
        // The apps' own record of where each sentence is, as another device would have left it.
        let through = files.iter().map(|f| f.1).sum();
        self.save_listening(crate::listening::Listening {
            recording: rec.id.clone(),
            book: book_id,
            sentences: graded.iter().map(|g| crate::listening::ListenSentence { section: g.section, sentence: g.sentence, start: g.t0, end: g.t1, heard: 1.0, adjusted: false }).collect(),
            sections: Vec::new(),
            intro: 0.0,
            through,
            intro_skipped: false,
            made_ms: Some(0),
            took: Some(0.0),
        })?;
        Ok(Some(rec.id))
    }

    /// 局域网传录音: the secret this person's devices share (made once, then synced), asked for
    /// before a recording is handed to another device on the network.
    pub fn lan_token(&self) -> Result<String> {
        if let Some(t) = lock(&self.store).get("lan", "token")?.and_then(|v| v.as_str().map(str::to_string)) {
            return Ok(t);
        }
        let t = format!("{:016x}{:016x}", getrandom::u64().unwrap_or(0), getrandom::u64().unwrap_or(1));
        lock(&self.store).set("lan", "token", json!(t))?;
        Ok(t)
    }

    /// 整理文字版 (asked for, with a model connected): the requests, each about 2000 characters
    /// of the 文字版. The app sends each to the reader's model and hands the answer to
    /// `audiobook_polish_apply`.
    pub fn audiobook_polish_asks(&self, book: String) -> Result<Vec<PolishAsk>> {
        let n = self.manifest(&book)?.sections.len() as u32;
        let dir = self.book_dir(&book)?;
        let sections: Vec<Vec<String>> = (0..n).map(|i| crate::book::sentences(&dir, i).unwrap_or_default()).collect();
        Ok(super::textbook::polish_chunks(&sections)
            .into_iter()
            .map(|c| PolishAsk { id: format!("polish:{}:{}:{}", c.section, c.first, c.last), system: super::textbook::POLISH_PROMPT.into(), user: c.text })
            .collect())
    }

    /// A model's answer to one 整理 request: the fixes that read the same as what they replace
    /// are made in the 文字版 (its sentences and its page), the rest dropped. How many were made.
    pub fn audiobook_polish_apply(&self, book: String, ask_id: String, answer: String) -> Result<u32> {
        let parts: Vec<u32> = ask_id.split(':').skip(1).filter_map(|x| x.parse().ok()).collect();
        let [section, first, last] = parts[..] else { return Err(CoreError::Invalid { detail: "ask id".into() }) };
        let dir = self.book_dir(&book)?;
        let mut sentences = crate::book::sentences(&dir, section)?;
        let mut html = crate::book::section_html(&dir, section)?;
        let chinese = self.lang_of_book(&book);
        let chunk = super::textbook::PolishChunk { section, first, last, text: String::new() };
        let mut made = 0;
        for f in super::textbook::checked_fixes(&answer, &chunk, &sentences, chinese) {
            let Some(page) = super::textbook::fix_html(&html, f.sentence, &f.from, &f.to) else { continue };
            html = page;
            let s = &mut sentences[f.sentence as usize];
            *s = s.replacen(&f.from, &f.to, 1);
            made += 1;
        }
        if made > 0 {
            std::fs::write(dir.join("s").join(format!("{section}.json")), serde_json::to_string(&sentences).map_err(|e| CoreError::Invalid { detail: e.to_string() })?)?;
            std::fs::write(dir.join("s").join(format!("{section}.html")), html)?;
        }
        Ok(made)
    }

    /// Whether `book` is a 文字版: made from a recording's words, so its page says so.
    pub fn audiobook_is_text_version(&self, book: String) -> bool {
        lock(&self.store).get(&format!("textversion/{book}"), "from").ok().flatten().is_some()
    }

    /// Whether a sound file is locked (DRM): Audible's, or an MP4 with encrypted sound. 导入
    /// says so instead of bringing in what can't be played.
    pub fn audiobook_is_locked(&self, path: String) -> bool {
        super::import::is_locked(std::path::Path::new(&path))
    }

    /// The sound files of a zip, taken out into `into`, in the order they were packed; empty
    /// when it holds none (it's something else).
    pub fn audiobook_unzip(&self, path: String, into: String) -> Result<Vec<String>> {
        super::import::unzip_sound(std::path::Path::new(&path), std::path::Path::new(&into))
    }

    /// Chapters from an mp3's ID3 tag (the first bytes of the file are enough).
    pub fn audiobook_id3_chapters(&self, head: Vec<u8>) -> Vec<crate::audio::AudioMark> {
        super::import::id3_chapters(&head)
    }

    pub fn audiobook_vorbis_chapters(&self, comments: Vec<String>) -> Vec<crate::audio::AudioMark> {
        super::import::vorbis_chapters(&comments)
    }

    /// The order files play in: tags when complete, names otherwise (indexes into `files`).
    pub fn audiobook_play_order(&self, files: Vec<super::import::FileTags>) -> Vec<u32> {
        super::import::play_order(&files).into_iter().map(|i| i as u32).collect()
    }
}

/// Where to go from `seconds`, inside extra `e`, under settings `s`.
fn skip_past(e: &ExtraSpan, seconds: f64, s: &SkipSettings) -> Option<f64> {
    match e.place {
        ExtraPlace::Head | ExtraPlace::Tail if s.head_tail => Some(e.t1),
        ExtraPlace::Middle if s.middle => Some(e.t1),
        ExtraPlace::ChapterGap if s.shorten_chapter_gaps && e.t1 - seconds > 1.5 => Some(e.t1 - 1.5),
        _ => None,
    }
}

/// 划线搬家's count: moved to the real book, and left on the 文字版 (not found for sure).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct MarksMoved {
    pub moved: u32,
    pub left: u32,
}

/// One 整理文字版 request for the reader's model.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PolishAsk {
    pub id: String,
    pub system: String,
    pub user: String,
}

/// One character or word and when it's read.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct UnitTime {
    /// Where it starts in the sentence (characters).
    pub at: u32,
    pub seconds: f64,
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct UnreadSentence {
    pub section: u32,
    pub sentence: u32,
}

/// The frames of a saved model output (f32 rows of the model's letters over a whole file) from
/// `a` to `b` seconds into the file: (values, frames, start, end) with the edges as found.
fn saved_slice(path: &str, file_seconds: f64, a: f64, b: f64) -> Option<(Vec<f32>, usize, f64, f64)> {
    use std::io::{Read, Seek, SeekFrom};
    let classes = crate::forced::LETTERS.chars().count();
    let mut f = std::fs::File::open(path).ok()?;
    let bytes = f.metadata().ok()?.len() as usize;
    if bytes == 0 || bytes % (4 * classes) != 0 || file_seconds <= 0.0 {
        return None;
    }
    let total = bytes / (4 * classes);
    let fps = total as f64 / file_seconds;
    let lo = ((a.max(0.0) * fps) as usize).min(total);
    let hi = ((b.min(file_seconds) * fps).ceil() as usize).min(total);
    if hi <= lo {
        return None;
    }
    f.seek(SeekFrom::Start((lo * classes * 4) as u64)).ok()?;
    let mut buf = vec![0u8; (hi - lo) * classes * 4];
    f.read_exact(&mut buf).ok()?;
    let em: Vec<f32> = buf.chunks_exact(4).map(|x| f32::from_le_bytes([x[0], x[1], x[2], x[3]])).collect();
    Some((em, hi - lo, lo as f64 / fps, hi as f64 / fps))
}

/// Speech stretches of a block cut into units of at most 30 s, at the longest pauses.
fn speech_units(speech: &[(f64, f64)], from: f64, to: f64) -> Vec<TimeSpan> {
    let mut sp: Vec<(f64, f64)> = speech.iter().filter(|s| s.1 > from && s.0 < to).map(|s| (s.0.max(from), s.1.min(to))).collect();
    sp.sort_by(|a, b| a.0.total_cmp(&b.0));
    let mut out: Vec<TimeSpan> = Vec::new();
    for (a, b) in sp {
        match out.last_mut() {
            Some(l) if b - l.t0 <= 30.0 && a - l.t1 < 1.0 => l.t1 = b,
            _ => out.push(TimeSpan { t0: a, t1: b }),
        }
    }
    out
}
