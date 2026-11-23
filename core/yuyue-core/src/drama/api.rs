//! What the apps call: the play of a section, the cast, the list of unsure lines, the user's
//! corrections, and the work queue (`drama_next_task` / `drama_submit`).
//!
//! Synced (in the store, small): the model's cast, its answers per scene, its acting and sounds,
//! the user's corrections and cast edits, the voices. Not synced: what the rules work out (the
//! same on every device), made clips and their checks (files; sent over the local network).
//! A device that finds a scene answered elsewhere doesn't ask again — the money is spent once.

use std::collections::{HashMap, HashSet};
use std::path::PathBuf;
use std::sync::{Arc, Mutex};

use serde::{Deserialize, Serialize};
use serde_json::{Value, json};

use super::attribute::ask::{self, Cand, Checked, SceneAsk, Slot, Vote};
use super::attribute::calibrate;
use super::attribute::rules::{RuleAnswer, attribute as rule_attribute};
use super::attribute::{Decided, ReviewLine, SectionIn, UserChoice, decide_section, lines_of, review};
use super::cast::{CastTable, ModelCast, ModelReading, bare, from_model, from_rules};
use super::casting::{CastingPlan, VoiceInfo};
use super::direct::{DIRECT_PROMPT, DirectAnswer, direct_schema, from_model as act_from_model, from_tag};
use super::queue::{ClipAsk, ClipCheck, ClipVerdict, VoiceNorm, judge};
use super::scenes::{ModelScene, checked as checked_scenes, rule_scenes};
use super::sound::{SoundAsset, SoundEvent, checked_events};
use super::spans::{RawSeg, locate_lines, segments, unquoted};
use super::timeline::{DramaTimeline, MadeClip};
use super::{CharacterGender, DecidedBy, DramaAct, DramaCharacter, DramaScene, DramaSegment, DramaSpeaker, LineBasis, SegKind};
use crate::api::{YuyueCore, lock};
use crate::error::{CoreError, Result};

pub use super::playback_api::{CastRoleVoice, RoleSegment, SpokenPart, window_of};

pub const CAST_PROMPT: &str = include_str!("prompts/cast.v1.txt");
/// 无引号体: the lines of a section, as their words.
pub const UNQUOTED_PROMPT: &str = include_str!("prompts/unquoted.v1.txt");

/// What this device can do. Without a language model only the rules attribute; without a
/// voice that acts, no acting; without a sound model, only the library's sounds.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaCaps {
    pub llm: bool,
    /// The verify tier is another model (another provider or tier), not the same one again.
    pub verify_distinct: bool,
    /// The strong model's context, in tokens.
    pub context_tokens: u32,
    pub tts: bool,
    pub design_voice: bool,
    pub sfx: bool,
    /// The model combination's name, for the calibration fit (`provider/strong+fast+verify`).
    pub combo: String,
    /// The voice engine's name and version, part of each clip's key.
    pub engine: String,
    /// The voices say a text the same way every time (the system's): making a clip again
    /// gives the same clip, so only a gross fault (words left out, clipping, long quiet, most
    /// of it wrong) fails 回听 — a few wrong characters are the transcriber's as often as not.
    #[serde(default)]
    pub tts_same_each_time: bool,
}

/// 听成一部剧's switches, per book, synced.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
#[serde(default)]
pub struct DramaSound {
    /// 带语气: voices that can act do (off: they read flat).
    pub acting: bool,
    /// 环境音: the room tone under each scene.
    pub beds: bool,
    /// 音效: the sounds the book writes (a knock, the rain starting).
    pub spots: bool,
}

impl Default for DramaSound {
    fn default() -> Self {
        DramaSound { acting: true, beds: true, spots: true }
    }
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ActTags {
    pub head: String,
    pub tail: String,
}

/// Mixed from this far before a window's start (and thrown away), so the reverb and the room
/// tone's fade are where they'd be had it played through.
pub const WINDOW_PREROLL: f64 = 1.0;

/// One sentence of a scene, to mix and play: see `drama_sentence_window`.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct DramaWindow {
    /// The scene's timeline with only what sounds in this stretch.
    pub timeline: DramaTimeline,
    pub from: f64,
    pub to: f64,
    /// The clips to decode and hand to the mixer (the built-in sounds it finds itself).
    pub clips: Vec<String>,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum LlmTier {
    Strong,
    Fast,
    Verify,
}

#[derive(Clone, Debug, PartialEq, uniffi::Enum)]
pub enum DramaTask {
    Llm { id: String, tier: LlmTier, system: String, user: String, schema: String, cache_prefix_len: u32, max_output_tokens: u32 },
    Tts { id: String, clip: ClipAsk },
    DesignVoice { id: String, character: u32, description: String, sample_text: String },
    /// 生成音效: a sound the book writes that no library has — `description` in English for a
    /// sound-making model, about `seconds` long. Answered with `Sound`.
    Sfx { id: String, kind: String, description: String, seconds: f32 },
}

#[derive(Clone, Debug, PartialEq, uniffi::Enum)]
pub enum DramaResult {
    Llm { text: String },
    Clip { clip: MadeClip, check: ClipCheck },
    Voice { voice: VoiceInfo },
    /// A made sound: mono, 48 kHz.
    Sound { samples: Vec<f32> },
    Failed { reason: String },
    /// Not done, and not the task's fault (paused, the key wrong, no credit, no model): handed
    /// back to be given out again, without counting as a failure.
    Returned,
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct DramaProgress {
    pub sections: u32,
    /// Sections whose scenes are all answered (or need no model).
    pub sections_done: u32,
    pub lines: u32,
    pub unsure: u32,
    pub cast_ready: bool,
}

/// 整本提前做好's progress.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct DramaMade {
    pub sections: u32,
    pub made: u32,
    /// Which sections, in order.
    pub made_sections: Vec<u32>,
}

/// A section as a play.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct DramaSection {
    pub section: u32,
    pub segments: Vec<DramaSegment>,
    pub scenes: Vec<DramaScene>,
    pub events: Vec<SoundEvent>,
    /// A book without quote marks the rules can't split (人物页写“这本书分不出台词”).
    pub unquoted: bool,
}

/// A line on the 没把握 list.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct UnsureLine {
    pub section: u32,
    pub sentence: u32,
    pub start: u32,
    pub text: String,
    /// The sentences around it, for context.
    pub context: Vec<String>,
    pub top: Vec<super::LineCandidate>,
    pub frees: u32,
    /// The shown names of `top`, in its order.
    #[uniffi(default = [])]
    pub names: Vec<String>,
    /// The sentence `context` starts at.
    #[uniffi(default = 0)]
    pub context_first: u32,
    /// The scene's place, and who's in it (shown names).
    #[uniffi(default = "")]
    pub place: String,
    #[uniffi(default = [])]
    pub present: Vec<String>,
}

/// An edit to the cast, kept by name and replayed after every rebuild.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum CastEdit {
    Add { name: String },
    Merge { into: String, from: String },
    Split { name: String },
    SetGender { name: String, gender: CharacterGender },
    /// Another name the book uses for someone (＋ on their page).
    AddName { to: String, name: String },
    /// The name shown for them.
    Show { name: String },
    /// 不是一个人物: the name isn't anyone; their lines go back to the unsure list.
    Remove { name: String },
    /// “可能是同一个人” answered 不是.
    NotSame { a: String, b: String },
}

/// One stored answer about a line, by where it is and who by name (ids change with the cast).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
struct StoredVote {
    sentence: u32,
    head: String,
    /// `name:王熙凤`, `GROUP`, `UNKNOWN`, `NOT`, `OTHER:名字`.
    speaker: String,
    basis: String,
    to: Option<String>,
    anchor_ok: bool,
    excluded: Vec<String>,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub(super) struct StoredSection {
    /// Scenes answered (by first sentence), and the model's cut.
    attributed: Vec<u32>,
    verified: Vec<u32>,
    directed: Vec<u32>,
    scenes: Vec<ModelScene>,
    first: Vec<StoredVote>,
    verify: Vec<StoredVote>,
    /// (sentence, start) → acting.
    acts: Vec<(u32, u32, DramaAct)>,
    events: Vec<SoundEvent>,
}

/// A book's rules work, done once and kept while the core is open.
pub struct BookDrama {
    pub cast: CastTable,
    pub sentences: Vec<Vec<String>>,
    pub segs: Vec<Vec<RawSeg>>,
    pub rules: Vec<Vec<RuleAnswer>>,
    pub readings: Vec<ModelReading>,
    pub cast_from_model: bool,
    /// Rows the model thought one person without proof: asked, never merged by itself.
    pub maybe_same: Vec<(u32, u32)>,
}

#[derive(Default)]
pub struct DramaRuntime {
    pub caps: DramaCaps,
    pub books: HashMap<String, Arc<BookDrama>>,
    pub inflight: HashSet<String>,
    /// Failed tries per task: after three it's left alone.
    pub failures: HashMap<String, u32>,
    /// Per book and section: (lines, unsure, done). Dropped for a section when it changes.
    pub counts: HashMap<(String, u32), (u32, u32, bool)>,
    /// Per book and section: lines per character, for the cast page.
    pub who: HashMap<(String, u32), HashMap<u32, u32>>,
}

/// How many lines before and after go into `scene_before`.
const BEFORE_LINES: u32 = 15;
/// The examples of the user's own choices given with each request.
const EXAMPLES: usize = 20;

pub(super) fn entity(book: &str) -> String {
    format!("drama/{book}")
}
fn sec_entity(book: &str, section: u32) -> String {
    format!("drama/{book}/s{section}")
}
fn user_entity(book: &str) -> String {
    format!("drama/{book}/user")
}

/// The key a line is kept under: its first sentence and its first twelve characters.
fn head_of(sentences: &[String], seg: &RawSeg) -> String {
    sentences[seg.sentence as usize].chars().skip(seg.start as usize).take(12).collect()
}

fn user_key(section: u32, sentence: u32, head: &str) -> String {
    format!("{section}:{sentence}:{head}")
}

/// Finds a line by its kept key: the same sentence first, then up to 20 sentences around by its
/// first words. None when the book changed too much.
fn find_line(sentences: &[String], segs: &[RawSeg], sentence: u32, head: &str) -> Option<u32> {
    let firsts: Vec<&RawSeg> = {
        let mut seen = HashSet::new();
        segs.iter().filter(|s| s.utterance.is_some_and(|u| seen.insert(u))).collect()
    };
    if let Some(s) = firsts.iter().find(|s| s.sentence == sentence && head_of(sentences, s) == head) {
        return s.utterance;
    }
    firsts.iter().filter(|s| (s.sentence as i64 - sentence as i64).abs() <= 20 && head_of(sentences, s) == head).min_by_key(|s| (s.sentence as i64 - sentence as i64).abs()).and_then(|s| s.utterance)
}

fn vote_label(v: &Vote, cast: &CastTable) -> String {
    match v {
        Vote::Is(Cand::Char(id)) => format!("name:{}", cast.get(*id).map(|c| c.key()).unwrap_or("")),
        Vote::Is(Cand::Group) => "GROUP".into(),
        Vote::NotSpeech => "NOT".into(),
        Vote::Unknown => "UNKNOWN".into(),
        Vote::Other(n) => format!("OTHER:{n}"),
    }
}

fn vote_of(label: &str, cast: &CastTable) -> Vote {
    if let Some(n) = label.strip_prefix("name:") {
        return cast.resolve(n).map(|id| Vote::Is(Cand::Char(id))).unwrap_or(Vote::Unknown);
    }
    if let Some(n) = label.strip_prefix("OTHER:") {
        return cast.resolve(n).map(|id| Vote::Is(Cand::Char(id))).unwrap_or(Vote::Other(n.into()));
    }
    match label {
        "GROUP" => Vote::Is(Cand::Group),
        "NOT" => Vote::NotSpeech,
        _ => Vote::Unknown,
    }
}

fn basis_label(b: LineBasis) -> &'static str {
    match b {
        LineBasis::Explicit => "explicit",
        LineBasis::Inferred => "inferred",
        LineBasis::Content => "content",
        LineBasis::None => "none",
    }
}

fn basis_of(s: &str) -> LineBasis {
    match s {
        "explicit" => LineBasis::Explicit,
        "inferred" => LineBasis::Inferred,
        "content" => LineBasis::Content,
        _ => LineBasis::None,
    }
}

fn store_votes(c: &[Checked], sentences: &[String], segs: &[RawSeg], cast: &CastTable) -> Vec<StoredVote> {
    c.iter()
        .filter_map(|v| {
            let seg = segs.iter().find(|s| s.utterance == Some(v.utterance))?;
            let name = |id: u32| cast.get(id).map(|c| c.key().to_string());
            Some(StoredVote {
                sentence: seg.sentence,
                head: head_of(sentences, seg),
                speaker: vote_label(&v.vote, cast),
                basis: basis_label(v.basis).into(),
                to: v.to.and_then(name),
                anchor_ok: v.anchor_ok,
                excluded: v.excluded.iter().filter_map(|e| if let Cand::Char(id) = e { name(*id) } else { Some("GROUP".into()) }).collect(),
            })
        })
        .collect()
}

fn load_votes(v: &[StoredVote], sentences: &[String], segs: &[RawSeg], cast: &CastTable) -> HashMap<u32, Checked> {
    v.iter()
        .filter_map(|s| {
            let u = find_line(sentences, segs, s.sentence, &s.head)?;
            Some((
                u,
                Checked {
                    utterance: u,
                    vote: vote_of(&s.speaker, cast),
                    basis: basis_of(&s.basis),
                    to: s.to.as_deref().and_then(|n| cast.resolve(n)),
                    anchor_ok: s.anchor_ok,
                    excluded: s.excluded.iter().filter_map(|n| if n == "GROUP" { Some(Cand::Group) } else { cast.resolve(n).map(Cand::Char) }).collect(),
                },
            ))
        })
        .collect()
}

/// The book's language, by its first sentences: Chinese, or anything else as Latin-script.
pub(super) fn lang_of(sentences: &[Vec<String>]) -> &'static str {
    let mut han = 0usize;
    let mut latin = 0usize;
    for s in sentences.iter().flatten().take(400) {
        for c in s.chars() {
            if ('\u{4e00}'..='\u{9fff}').contains(&c) {
                han += 1;
            } else if c.is_ascii_alphabetic() {
                latin += 1;
            }
        }
    }
    if han * 3 >= latin { "zh" } else { "en" }
}

/// Clip state on this device: made clips and tries, kept as a file beside the book.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub(super) struct ClipStore {
    pub(super) made: HashMap<String, MadeClip>,
    /// (section:sentence:start) → how it's being made now after failures.
    pub(super) fallback: HashMap<String, ClipVerdict>,
    pub(super) attempts: HashMap<String, u8>,
    /// Per voice: clips kept, mean and M2 of similarity (Welford), seconds and characters.
    pub(super) stats: HashMap<String, VoiceStats>,
    /// Clip key → voice, for the clips asked for.
    pub(super) voice_of: HashMap<String, String>,
    /// Kept after every try without passing 回听.
    #[serde(default)]
    pub(super) failed_check: std::collections::HashSet<String>,
    /// 局域网传片段: the clips came from another device (a Mac), made with its voices; they're
    /// asked for by its plan and engine until this device casts the book itself.
    #[serde(default)]
    pub(super) borrowed: Option<Borrowed>,
    /// 生成音效: the sounds made for this book (their samples beside it, `sounds/<id>.f32`).
    #[serde(default)]
    pub(super) sounds: Vec<SoundAsset>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct Borrowed {
    pub(super) device: String,
    pub(super) plan: CastingPlan,
    pub(super) acting: Vec<String>,
    pub(super) engine: String,
}

/// 局域网传片段: what a device has made of a book, for another to take.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub(super) struct ClipManifest {
    pub(super) plan: CastingPlan,
    pub(super) acting: Vec<String>,
    pub(super) engine: String,
    pub(super) made: Vec<MadeClip>,
    pub(super) fallback: HashMap<String, ClipVerdict>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub(super) struct VoiceStats {
    n: u32,
    mean: f32,
    m2: f32,
    seconds: f32,
    chars: u32,
}

impl VoiceStats {
    fn add(&mut self, sim: f32, seconds: f32, chars: u32) {
        self.n += 1;
        let d = sim - self.mean;
        self.mean += d / self.n as f32;
        self.m2 += d * (sim - self.mean);
        self.seconds += seconds;
        self.chars += chars;
    }

    /// Once there are five clips to go by.
    fn norm(&self) -> Option<VoiceNorm> {
        (self.n >= 5).then(|| VoiceNorm { sim_mean: self.mean, sim_sd: (self.m2 / (self.n - 1) as f32).sqrt(), sec_per_char: if self.chars > 0 { self.seconds / self.chars as f32 } else { 0.0 } })
    }
}

impl YuyueCore {
    /// The sounds made for a book, told to the mixer (which reads them from beside the book).
    pub(super) fn register_sounds(&self, book: &str, store: &ClipStore) -> Vec<SoundAsset> {
        if let Ok(dir) = self.drama_dir(book) {
            let mut g = lock(&super::mixer::MADE_SOUNDS);
            for a in &store.sounds {
                g.insert(a.id.clone(), dir.join("sounds").join(format!("{}.f32", a.id)));
            }
        }
        store.sounds.clone()
    }

    pub(super) fn drama_dir(&self, book: &str) -> Result<PathBuf> {
        let d = self.book_dir(book)?.join("drama");
        std::fs::create_dir_all(&d)?;
        Ok(d)
    }

    pub(super) fn clip_store(&self, book: &str) -> Result<ClipStore> {
        let p = self.drama_dir(book)?.join("clips.json");
        Ok(std::fs::read(&p).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default())
    }

    pub(super) fn save_clip_store(&self, book: &str, s: &ClipStore) -> Result<()> {
        let p = self.drama_dir(book)?.join("clips.json");
        std::fs::write(p, serde_json::to_vec(s).map_err(|e| CoreError::Invalid { detail: e.to_string() })?)?;
        Ok(())
    }

    fn stored_section(&self, book: &str, section: u32) -> StoredSection {
        lock(&self.store).get(&sec_entity(book, section), "data").ok().flatten().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
    }

    fn save_section(&self, book: &str, section: u32, s: &StoredSection) -> Result<()> {
        self.forget_counts(book, section);
        lock(&self.store).set(&sec_entity(book, section), "data", serde_json::to_value(s).unwrap_or(Value::Null))
    }

    fn user_choices(&self, book: &str) -> HashMap<String, UserChoice> {
        lock(&self.store).fields(&user_entity(book)).unwrap_or_default().into_iter().filter_map(|(k, v)| serde_json::from_value(v).ok().map(|c| (k, c))).collect()
    }

    fn cast_edits(&self, book: &str) -> Vec<CastEdit> {
        lock(&self.store).get(&entity(book), "edits").ok().flatten().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
    }

    fn forget_drama(&self, book: &str) {
        let mut rt = lock(&self.drama);
        rt.books.remove(book);
        rt.counts.retain(|k, _| k.0 != book);
        rt.who.retain(|k, _| k.0 != book);
    }

    /// Everything worked out, for every book: the store changed under it (a sync came in).
    pub(crate) fn forget_all_drama(&self) {
        let mut rt = lock(&self.drama);
        rt.books.clear();
        rt.counts.clear();
        rt.who.clear();
    }

    fn forget_counts(&self, book: &str, section: u32) {
        let mut rt = lock(&self.drama);
        rt.counts.remove(&(book.to_string(), section));
        rt.who.remove(&(book.to_string(), section));
    }

    /// The rules work for a book, done once.
    pub(crate) fn book_drama(&self, book: &str) -> Result<Arc<BookDrama>> {
        if let Some(b) = lock(&self.drama).books.get(book) {
            return Ok(b.clone());
        }
        let rc = self.cast(book)?;
        let n = self.manifest(book)?.sections.len() as u32;
        let sentences: Vec<Vec<String>> = (0..n).map(|i| self.sentences(book.to_string(), i).unwrap_or_default()).collect();
        let segs: Vec<Vec<RawSeg>> = sentences.iter().enumerate().map(|(i, s)| segments(s, self.unquoted_lines_of(book, i as u32).as_deref())).collect();
        // One pass of the rules over the book, on a cast of the rules' names; the names it adds
        // (茜雪道 once) join them, the final cast is built from all, and the answers are moved
        // over by name — so ids don't depend on which section was read first.
        let mut scratch = from_rules(rc.names());
        let first: Vec<Vec<RuleAnswer>> = sentences.iter().zip(&segs).map(|(s, sg)| rule_attribute(&rc, &mut scratch, s, sg)).collect();
        let mut rule_names: Vec<String> = scratch.chars.iter().filter(|c| !c.narrator).flat_map(|c| c.names.clone()).collect();
        rule_names.sort();
        rule_names.dedup();
        let model: Option<ModelCast> = lock(&self.store).get(&entity(book), "cast").ok().flatten().and_then(|v| serde_json::from_value(v).ok());
        let (mut cast, readings, from_m, maybe) = match &model {
            Some(m) => {
                let bare_sections: Vec<String> = sentences.iter().map(|s| bare(&s.concat())).collect();
                let (t, r) = from_model(m, &bare_sections, &rule_names);
                // By name: ids move when the user edits the cast.
                let pairs: Vec<(String, String)> = r.maybe_same.iter().filter_map(|(a, b)| Some((t.get(*a)?.key().to_string(), t.get(*b)?.key().to_string()))).collect();
                (t, r.readings, true, pairs)
            }
            None => (from_rules(&rule_names), vec![], false, vec![]),
        };
        let edits = self.cast_edits(book);
        for c in &mut cast.chars {
            c.shown = c.short_name().to_string();
        }
        apply_edits(&mut cast, &edits);
        let removed: HashSet<String> = edits.iter().filter_map(|e| if let CastEdit::Remove { name } = e { Some(name.clone()) } else { None }).collect();
        let rules = remap(first, &scratch, &mut cast, &removed);
        // Names the rules brought back (a row of their own) show as themselves.
        for c in &mut cast.chars {
            if c.shown.is_empty() || !c.names.contains(&c.shown) {
                c.shown = c.short_name().to_string();
            }
        }
        for (i, r) in rules.iter().enumerate() {
            for a in r {
                if let DramaSpeaker::Char { id } = a.speaker
                    && let Some(c) = cast.get_mut(id) {
                        c.lines += 1;
                        c.first_section = Some(c.first_section.map_or(i as u32, |f| f.min(i as u32)));
                    }
            }
        }
        // 鳳姐 and 鳳姐儿: likely one person, but only the user joins them.
        let mut maybe = maybe;
        for c in &cast.chars {
            for n in &c.names {
                if let Some(base) = n.strip_suffix(['儿', '兒'])
                    && base.chars().count() >= 2
                    && let Some(other) = cast.chars.iter().find(|o| o.id != c.id && o.names.iter().any(|x| x == base)) {
                        maybe.push((other.key().to_string(), c.key().to_string()));
                    }
            }
        }
        let not_same: Vec<(String, String)> = edits.iter().filter_map(|e| if let CastEdit::NotSame { a, b } = e { Some((a.clone(), b.clone())) } else { None }).collect();
        let maybe_same: Vec<(u32, u32)> = maybe
            .iter()
            .filter_map(|(a, b)| Some((cast.resolve(a)?, cast.resolve(b)?)))
            .filter(|(a, b)| a != b)
            .filter(|(a, b)| {
                let names = |id: u32| cast.get(id).map(|c| c.names.clone()).unwrap_or_default();
                let (na, nb) = (names(*a), names(*b));
                !not_same.iter().any(|(x, y)| (na.contains(x) && nb.contains(y)) || (na.contains(y) && nb.contains(x)))
            })
            .collect();
        let b = Arc::new(BookDrama { cast, sentences, segs, rules, readings, cast_from_model: from_m, maybe_same });
        lock(&self.drama).books.insert(book.to_string(), b.clone());
        Ok(b)
    }

    fn section_input_parts(&self, book: &str, b: &BookDrama, section: u32) -> (Vec<DramaScene>, HashMap<u32, Checked>, HashMap<u32, Checked>, HashMap<u32, UserChoice>, StoredSection) {
        let sentences = &b.sentences[section as usize];
        let segs = &b.segs[section as usize];
        let st = self.stored_section(book, section);
        let rules_scenes = rule_scenes(section, sentences, &b.cast);
        let scenes = if st.scenes.is_empty() {
            rules_scenes
        } else {
            let last = sentences.len().saturating_sub(1) as u32;
            checked_scenes(section, 0, last, &st.scenes, &rules_scenes, &b.cast).unwrap_or(rules_scenes)
        };
        let first = load_votes(&st.first, sentences, segs, &b.cast);
        let verify = load_votes(&st.verify, sentences, segs, &b.cast);
        let mut user = HashMap::new();
        for (k, c) in self.user_choices(book) {
            let mut it = k.splitn(3, ':');
            let (Some(sec), Some(sen), Some(head)) = (it.next(), it.next(), it.next()) else { continue };
            if sec.parse::<u32>().ok() != Some(section) {
                continue;
            }
            if let Some(u) = sen.parse().ok().and_then(|sen| find_line(sentences, segs, sen, head)) {
                user.insert(u, c);
            }
        }
        (scenes, first, verify, user, st)
    }

    pub(super) fn decided(&self, book: &str, b: &BookDrama, section: u32) -> (Vec<Decided>, Vec<DramaScene>, StoredSection) {
        let (scenes, first, verify, user, st) = self.section_input_parts(book, b, section);
        let fits = calibrate::fits();
        let combo = lock(&self.drama).caps.combo.clone();
        let fit = fits.iter().find(|f| f.combo == combo);
        let input = SectionIn { sentences: &b.sentences[section as usize], segs: &b.segs[section as usize], rules: &b.rules[section as usize], scenes: &scenes, first: &first, verify: &verify, user: &user, cast: &b.cast, fit };
        (decide_section(&input), scenes, st)
    }

    fn ask_for_scene(&self, book: &str, b: &BookDrama, section: u32, scene: &DramaScene, verify: bool) -> Option<(SceneAsk, String, usize)> {
        let sentences = &b.sentences[section as usize];
        let segs = &b.segs[section as usize];
        let (_, _, _, user, _) = self.section_input_parts(book, b, section);
        let all = lines_of(sentences, segs);
        let here: Vec<_> = all.iter().filter(|l| l.first_sentence >= scene.first && l.first_sentence <= scene.last).collect();
        if here.is_empty() {
            return None;
        }
        let rules: HashMap<u32, &RuleAnswer> = b.rules[section as usize].iter().map(|r| (r.utterance, r)).collect();
        let slots: Vec<Slot> = here
            .iter()
            .enumerate()
            .map(|(i, l)| {
                let known = match user.get(&l.utterance) {
                    Some(UserChoice::Person { name }) => b.cast.resolve(name).map(|id| DramaSpeaker::Char { id }),
                    Some(UserChoice::Group { name }) => Some(DramaSpeaker::Group { name: name.clone() }),
                    _ => rules.get(&l.utterance).filter(|r| r.decided_by == DecidedBy::Rule).map(|r| r.speaker.clone()),
                };
                Slot { q: i as u32 + 1, utterance: l.utterance, sentence: l.first_sentence, known }
            })
            .collect();
        // Nothing to ask: every line settled.
        let open: Vec<u32> = slots.iter().filter(|s| s.known.is_none()).map(|s| s.q).collect();
        if open.is_empty() {
            return None;
        }
        // Candidates: those present, and who spoke in the scene before.
        let mut candidates = scene.present.clone();
        let prev_scene_first = scene.first.saturating_sub(BEFORE_LINES);
        for r in &b.rules[section as usize] {
            if let DramaSpeaker::Char { id } = r.speaker
                && let Some(l) = all.iter().find(|l| l.utterance == r.utterance)
                    && l.first_sentence >= prev_scene_first && l.first_sentence <= scene.last && !candidates.contains(&id) {
                        candidates.push(id);
                    }
        }
        candidates.retain(|id| b.cast.get(*id).is_some_and(|c| !c.narrator));
        let narrator = b.cast.chars.iter().find(|c| c.narrator).map(|c| c.id);
        let ask = SceneAsk { scene: scene.clone(), slots, candidates, narrator };
        let q_of: HashMap<u32, u32> = ask.slots.iter().map(|s| (s.utterance, s.q)).collect();
        let lines: Vec<(u32, u32, u32, Option<u32>)> = segs.iter().filter(|s| s.sentence >= scene.first && s.sentence <= scene.last).map(|s| (s.sentence, s.start, s.end, s.utterance.filter(|u| q_of.contains_key(u)))).collect();
        let text = ask::scene_text(sentences, scene.first, scene.last, &lines, &q_of);
        let before: String = (scene.first.saturating_sub(BEFORE_LINES)..scene.first).map(|i| format!("[{i}] {}\n", sentences[i as usize])).collect();
        let examples: Vec<String> = self
            .user_choices(book)
            .into_iter()
            .filter_map(|(k, c)| match c {
                UserChoice::Person { name } => Some(format!("{} → {name}", k.splitn(3, ':').nth(2).unwrap_or(""))),
                _ => None,
            })
            .take(EXAMPLES)
            .collect();
        let (user_text, prefix) = ask::user_text(&b.cast, &ask, &before, &text, &examples, verify.then_some(open.as_slice()));
        Some((ask, user_text, prefix))
    }

    fn whole_book_text(b: &BookDrama, from: usize, to: usize) -> String {
        let mut s = String::new();
        for i in from..to {
            s.push_str(&format!("[[{i}]]\n"));
            for t in &b.sentences[i] {
                s.push_str(t);
            }
            s.push('\n');
        }
        s
    }

    /// Volumes of the book that fit the strong model's context (60%), by section.
    fn volumes(b: &BookDrama, context_tokens: u32) -> Vec<(usize, usize)> {
        let zh = lang_of(&b.sentences) == "zh";
        let per_token = if zh { 1.4 } else { 4.0 };
        let budget = (context_tokens as f64 * 0.6 * per_token) as usize;
        let mut out = Vec::new();
        let mut start = 0;
        let mut size = 0;
        for (i, s) in b.sentences.iter().enumerate() {
            let n: usize = s.iter().map(|t| t.chars().count()).sum();
            if size + n > budget && i > start {
                out.push((start, i));
                start = i;
                size = 0;
            }
            size += n;
        }
        out.push((start, b.sentences.len()));
        out
    }
}

fn apply_edits(cast: &mut CastTable, edits: &[CastEdit]) {
    for e in edits {
        match e {
            CastEdit::Add { name } => {
                cast.add_name(name, false);
            }
            CastEdit::Merge { into, from } => {
                let (Some(a), Some(b)) = (cast.resolve(into), cast.resolve(from)) else { continue };
                if a == b {
                    continue;
                }
                let moved = cast.get(b).map(|c| c.names.clone()).unwrap_or_default();
                if let Some(c) = cast.get_mut(a) {
                    for n in moved {
                        if !c.names.contains(&n) {
                            c.names.push(n);
                        }
                    }
                }
                cast.chars.retain(|c| c.id != b);
            }
            CastEdit::Split { name } => {
                let Some(id) = cast.chars.iter().find(|c| c.names.len() > 1 && c.names.contains(name)).map(|c| c.id) else { continue };
                if let Some(c) = cast.get_mut(id) {
                    c.names.retain(|n| n != name);
                }
                let nid = cast.chars.iter().map(|c| c.id + 1).max().unwrap_or(1);
                cast.chars.push(DramaCharacter::named(nid, name, false));
            }
            CastEdit::SetGender { name, gender } => {
                if let Some(id) = cast.resolve(name)
                    && let Some(c) = cast.get_mut(id) {
                        c.gender = *gender;
                    }
            }
            CastEdit::AddName { to, name } => {
                let Some(id) = cast.resolve(to) else { continue };
                // Taken from whoever had it; a row left with no names goes.
                for c in cast.chars.iter_mut().filter(|c| c.id != id) {
                    c.names.retain(|n| n != name);
                }
                cast.chars.retain(|c| !c.names.is_empty());
                if let Some(c) = cast.get_mut(id)
                    && !c.names.contains(name) {
                        c.names.push(name.clone());
                    }
            }
            CastEdit::Show { name } => {
                if let Some(id) = cast.resolve(name)
                    && let Some(c) = cast.get_mut(id) {
                        c.shown = name.clone();
                    }
            }
            CastEdit::Remove { name } => {
                if let Some(id) = cast.resolve(name) {
                    cast.chars.retain(|c| c.id != id);
                }
            }
            CastEdit::NotSame { .. } => {}
        }
        cast.reindex();
    }
}

/// A merged model cast from volumes read one by one: the same written name in two volumes is the
/// same person (a full name repeated is as sure as the text gets); nothing else is joined.
fn merge_volumes(parts: Vec<ModelCast>) -> ModelCast {
    let mut out = ModelCast { characters: vec![], merges: vec![], groups: vec![], narrator: None, readings: vec![] };
    for (vi, mut p) in parts.into_iter().enumerate() {
        let rekey = |k: &str| format!("v{vi}_{k}");
        for c in &mut p.characters {
            c.key = rekey(&c.key);
            c.same_as = c.same_as.as_deref().map(rekey);
            for cb in &mut c.called_by {
                cb.who = rekey(&cb.who);
            }
        }
        for m in &mut p.merges {
            m.a = rekey(&m.a);
            m.b = rekey(&m.b);
        }
        for c in p.characters {
            let same = out.characters.iter_mut().find(|o| o.names.iter().any(|n| c.names.iter().any(|m| m.text == n.text && m.text.chars().count() >= 2)));
            match same {
                Some(o) => {
                    for n in c.names {
                        if !o.names.iter().any(|x| x.text == n.text) {
                            o.names.push(n);
                        }
                    }
                    o.evidence.extend(c.evidence);
                }
                None => out.characters.push(c),
            }
        }
        out.merges.extend(p.merges);
        out.groups.extend(p.groups);
        out.readings.extend(p.readings);
        if out.narrator.is_none() {
            out.narrator = p.narrator;
        }
    }
    out
}

fn unquoted_schema() -> String {
    json!({
        "type": "object",
        "required": ["lines"],
        "properties": {
            "lines": {
                "type": "array",
                "items": {
                    "type": "object",
                    "required": ["s", "text"],
                    "properties": { "s": { "type": "integer" }, "text": { "type": "string" } }
                }
            }
        }
    })
    .to_string()
}

#[derive(Deserialize)]
struct UnquotedAnswer {
    lines: Vec<UnquotedLine>,
}

#[derive(Deserialize)]
struct UnquotedLine {
    s: u32,
    text: String,
}

fn cast_schema() -> String {
    json!({
        "type": "object",
        "required": ["characters"],
        "properties": {
            "characters": { "type": "array", "items": { "type": "object", "required": ["key", "names", "evidence"], "properties": {
                "key": { "type": "string" },
                "names": { "type": "array", "items": { "type": "object", "required": ["text"], "properties": { "text": { "type": "string" }, "kind": { "type": "string" } } } },
                "called_by": { "type": "array", "items": { "type": "object", "properties": { "who": { "type": "string" }, "as": { "type": "string" } } } },
                "gender": { "enum": ["m", "f", "unknown"] },
                "age": { "enum": ["child", "teen", "young", "middle", "old", "unknown"] },
                "traits": { "type": "string" }, "speaks": { "type": "boolean" },
                "evidence": { "type": "array", "items": { "type": "object", "properties": { "section": { "type": "integer" }, "quote": { "type": "string" } } } },
                "same_as": { "type": ["string", "null"] }, "revealed": { "type": ["integer", "null"] }
            }}},
            "merges": { "type": "array", "items": { "type": "object", "properties": { "a": { "type": "string" }, "b": { "type": "string" }, "evidence": { "type": "object" } } } },
            "groups": { "type": "array" },
            "narrator": { "type": "object", "properties": { "person": { "enum": ["first", "third"] }, "who": { "type": ["string", "null"] } } },
            "readings": { "type": "array", "items": { "type": "object", "properties": { "word": { "type": "string" }, "reading": { "type": "string" }, "evidence": { "type": "object" } } } }
        }
    })
    .to_string()
}

#[uniffi::export]
impl YuyueCore {
    /// What this device can do; tasks follow from it.
    pub fn drama_set_caps(&self, caps: DramaCaps) {
        lock(&self.drama).caps = caps;
    }

    /// A section as a play: every stretch with its speaker, acting and how sure.
    pub fn drama_section(&self, book_id: String, section: u32) -> Result<DramaSection> {
        let b = self.book_drama(&book_id)?;
        if section as usize >= b.sentences.len() {
            return Err(CoreError::Invalid { detail: format!("no section {section}") });
        }
        let sentences = &b.sentences[section as usize];
        let segs = &b.segs[section as usize];
        let (decided, scenes, st) = self.decided(&book_id, &b, section);
        let by_u: HashMap<u32, &Decided> = decided.iter().map(|d| (d.utterance, d)).collect();
        let rules: HashMap<u32, &RuleAnswer> = b.rules[section as usize].iter().map(|r| (r.utterance, r)).collect();
        let tags: Vec<(u32, u32, u32)> = b.rules[section as usize].iter().filter_map(|r| r.tag).collect();
        let mut out = Vec::new();
        for s in segs {
            let text = super::spans::seg_text(sentences, s);
            let mut kind = s.kind;
            let (speaker, basis, by, p, to) = match s.utterance.and_then(|u| by_u.get(&u)) {
                Some(d) if d.not_speech => {
                    kind = SegKind::Narration;
                    (DramaSpeaker::Narrator, LineBasis::Explicit, DecidedBy::User, 1.0, None)
                }
                Some(d) => (d.speaker.clone(), d.basis, d.decided_by, d.p_correct, d.addressee),
                None if s.kind == SegKind::Quoted => match s.utterance.and_then(|u| rules.get(&u)).map(|r| r.speaker.clone()) {
                    Some(sp @ DramaSpeaker::Char { .. }) => (sp, LineBasis::Explicit, DecidedBy::Rule, 1.0, None),
                    _ => (DramaSpeaker::Narrator, LineBasis::None, DecidedBy::Rule, 1.0, None),
                },
                None => (DramaSpeaker::Narrator, LineBasis::Explicit, DecidedBy::Rule, 1.0, None),
            };
            // Acting: the model's (softened) when there is one, else the tag's.
            let tag_text: String = s.utterance.and_then(|u| rules.get(&u)).and_then(|r| r.tag).map(|(sen, a, z)| b.sentences[section as usize][sen as usize].chars().skip(a as usize).take((z - a) as usize).collect()).unwrap_or_default();
            let written = if kind == SegKind::Narration { DramaAct::flat() } else { from_tag(&tag_text) };
            let act = st.acts.iter().find(|(sen, st0, _)| *sen == s.sentence && *st0 == s.start).map(|a| if written.from_text { written.clone() } else { a.2.clone() }).unwrap_or(written);
            let tag_only = kind == SegKind::Narration && tags.iter().any(|(sen, a, z)| *sen == s.sentence && *a == s.start && *z == s.end) && !text.chars().any(|c| "一面着著".contains(c));
            out.push(DramaSegment { sentence: s.sentence, start: s.start, end: s.end, kind, utterance: s.utterance, speaker, addressee: to, basis, decided_by: by, p_correct: p, act, tag_only, text });
        }
        Ok(DramaSection { section, segments: out, scenes, events: st.events.clone(), unquoted: unquoted(sentences) })
    }

    /// The cast met by `upto_section` (no spoilers), most lines first.
    pub fn drama_cast(&self, book_id: String, upto_section: u32) -> Result<Vec<DramaCharacter>> {
        let b = self.book_drama(&book_id)?;
        let mut v: Vec<DramaCharacter> = b.cast.chars.iter().filter(|c| c.lines > 0 && c.first_section.is_some_and(|f| f <= upto_section)).cloned().collect();
        for c in &mut v {
            // Revealed later: not the same person yet.
            if c.revealed_in.is_some_and(|r| r > upto_section) {
                c.same_as = None;
            }
        }
        v.sort_by(|a, b| b.lines.cmp(&a.lines).then(a.id.cmp(&b.id)));
        Ok(v)
    }

    /// Who says a line, from the user. `start` is the line's first character in `sentence`.
    pub fn drama_set_speaker(&self, book_id: String, section: u32, sentence: u32, start: u32, choice: UserChoice) -> Result<()> {
        let b = self.book_drama(&book_id)?;
        let seg = b.segs.get(section as usize).and_then(|s| s.iter().find(|x| x.sentence == sentence && x.start == start && x.utterance.is_some())).ok_or_else(|| CoreError::Invalid { detail: "no such line".into() })?.clone();
        let key = user_key(section, sentence, &head_of(&b.sentences[section as usize], &seg));
        if let UserChoice::Person { name } = &choice
            && b.cast.resolve(name).is_none() {
                // Someone new (“新的人物…”): a cast row of their own from now on.
                let mut edits = self.cast_edits(&book_id);
                edits.push(CastEdit::Add { name: name.clone() });
                lock(&self.store).set(&entity(&book_id), "edits", serde_json::to_value(&edits).unwrap_or(Value::Null))?;
                self.forget_drama(&book_id);
            }
        lock(&self.store).set(&user_entity(&book_id), &key, serde_json::to_value(&choice).unwrap_or(Value::Null))?;
        self.forget_counts(&book_id, section);
        Ok(())
    }

    /// Takes back the user's word on a line.
    pub fn drama_clear_speaker(&self, book_id: String, section: u32, sentence: u32, start: u32) -> Result<()> {
        let b = self.book_drama(&book_id)?;
        let Some(seg) = b.segs.get(section as usize).and_then(|s| s.iter().find(|x| x.sentence == sentence && x.start == start)) else { return Ok(()) };
        let key = user_key(section, sentence, &head_of(&b.sentences[section as usize], seg));
        lock(&self.store).set(&user_entity(&book_id), &key, Value::Null)?;
        self.forget_counts(&book_id, section);
        Ok(())
    }

    pub fn drama_edit_cast(&self, book_id: String, edit: CastEdit) -> Result<()> {
        let mut edits = self.cast_edits(&book_id);
        edits.push(edit);
        lock(&self.store).set(&entity(&book_id), "edits", serde_json::to_value(&edits).unwrap_or(Value::Null))?;
        self.forget_drama(&book_id);
        Ok(())
    }

    /// The 没把握 list up to a section, the lines that settle most first.
    pub fn drama_review(&self, book_id: String, upto_section: u32) -> Result<Vec<UnsureLine>> {
        let b = self.book_drama(&book_id)?;
        let fits = calibrate::fits();
        let combo = lock(&self.drama).caps.combo.clone();
        let fit = fits.iter().find(|f| f.combo == combo);
        let mut out = Vec::new();
        for section in 0..=upto_section.min(b.sentences.len().saturating_sub(1) as u32) {
            let (scenes, first, verify, user, _) = self.section_input_parts(&book_id, &b, section);
            let sentences = &b.sentences[section as usize];
            let segs = &b.segs[section as usize];
            let input = SectionIn { sentences, segs, rules: &b.rules[section as usize], scenes: &scenes, first: &first, verify: &verify, user: &user, cast: &b.cast, fit };
            for r in review(&input) {
                let ReviewLine { utterance, top, frees } = r;
                let Some(seg) = segs.iter().find(|s| s.utterance == Some(utterance)) else { continue };
                let lo = seg.sentence.saturating_sub(3) as usize;
                let hi = (seg.sentence as usize + 4).min(sentences.len());
                let shown = |id: u32| b.cast.get(id).map(|c| c.shown.clone());
                let names: Vec<String> = top.iter().filter_map(|c| shown(c.id)).collect();
                let scene = scenes.iter().find(|s| s.first <= seg.sentence && seg.sentence <= s.last);
                let place = scene.map(|s| s.place.clone()).unwrap_or_default();
                let present: Vec<String> = scene.map(|s| s.present.iter().filter_map(|id| shown(*id)).collect()).unwrap_or_default();
                out.push(UnsureLine { section, sentence: seg.sentence, start: seg.start, text: super::spans::seg_text(sentences, seg), context: sentences[lo..hi].to_vec(), top, frees, names, context_first: lo as u32, place, present });
            }
        }
        out.sort_by(|a, b| b.frees.cmp(&a.frees).then((a.section, a.sentence).cmp(&(b.section, b.sentence))));
        Ok(out)
    }

    pub fn drama_progress(&self, book_id: String) -> Result<DramaProgress> {
        let b = self.book_drama(&book_id)?;
        let (mut lines, mut unsure, mut done) = (0u32, 0u32, 0u32);
        for section in 0..b.sentences.len() as u32 {
            let key = (book_id.clone(), section);
            let cached = lock(&self.drama).counts.get(&key).copied();
            let (l, u, d) = match cached {
                Some(c) => c,
                None => {
                    let (d, scenes, st) = self.decided(&book_id, &b, section);
                    let c = (
                        d.len() as u32,
                        d.iter().filter(|x| x.decided_by == DecidedBy::Unsure).count() as u32,
                        scenes.iter().all(|s| st.attributed.contains(&s.first)) || d.iter().all(|x| x.decided_by != DecidedBy::Unsure),
                    );
                    lock(&self.drama).counts.insert(key, c);
                    c
                }
            };
            lines += l;
            unsure += u;
            done += d as u32;
        }
        Ok(DramaProgress { sections: b.sentences.len() as u32, sections_done: done, lines, unsure, cast_ready: b.cast_from_model })
    }

    /// The next piece of work for the app, nearest `focus_section` first; None when there's
    /// nothing to do (or nothing this device can do).
    pub fn drama_next_task(&self, book_id: String, focus_section: u32) -> Result<Option<DramaTask>> {
        let caps = lock(&self.drama).caps.clone();
        let b = self.book_drama(&book_id)?;
        let n = b.sentences.len() as u32;
        let taken = |id: &str, rt: &DramaRuntime| rt.inflight.contains(id) || rt.failures.get(id).copied().unwrap_or(0) >= 3;
        let claim = |id: String, task: DramaTask| -> Option<DramaTask> {
            let mut rt = lock(&self.drama);
            if taken(&id, &rt) {
                return None;
            }
            rt.inflight.insert(id);
            Some(task)
        };
        let order: Vec<u32> = std::iter::once(focus_section.min(n.saturating_sub(1))).chain(focus_section + 1..n).chain(0..focus_section.min(n)).collect();
        if caps.llm {
            // 1. The section being read, all the way: its lines (无引号体), who, a second look, how it's played.
            if let Some(t) = self.unquoted_task(&book_id, &b, order[0], &claim) {
                return Ok(Some(t));
            }
            if let Some(t) = self.attr_task(&book_id, &b, order[0], &claim)? {
                return Ok(Some(t));
            }
            if let Some(t) = self.verify_task(&book_id, &b, order[0], &caps, &claim)? {
                return Ok(Some(t));
            }
            if let Some(t) = self.direct_task(&book_id, &b, order[0], &claim)? {
                return Ok(Some(t));
            }
            // 2. The whole book's cast.
            if !b.cast_from_model {
                let vols = Self::volumes(&b, caps.context_tokens.max(32_000));
                let parts: Vec<Option<Value>> = (0..vols.len()).map(|i| lock(&self.store).get(&entity(&book_id), &format!("cast_part{i}")).ok().flatten()).collect();
                for (i, (from, to)) in vols.iter().enumerate() {
                    if parts[i].is_none() {
                        let id = format!("cast:{book_id}:{i}:{}", vols.len());
                        let user = Self::whole_book_text(&b, *from, *to);
                        if let Some(t) = claim(id.clone(), DramaTask::Llm { id, tier: LlmTier::Strong, system: CAST_PROMPT.into(), user, schema: cast_schema(), cache_prefix_len: 0, max_output_tokens: 16_000 }) {
                            return Ok(Some(t));
                        }
                    }
                }
            }
            // 3. The rest in reading order, each section whole; acting only near where one is.
            for (k, &s) in order.iter().enumerate().skip(1) {
                if let Some(t) = self.unquoted_task(&book_id, &b, s, &claim) {
                    return Ok(Some(t));
                }
                if let Some(t) = self.attr_task(&book_id, &b, s, &claim)? {
                    return Ok(Some(t));
                }
                if let Some(t) = self.verify_task(&book_id, &b, s, &caps, &claim)? {
                    return Ok(Some(t));
                }
                if k < 3
                    && let Some(t) = self.direct_task(&book_id, &b, s, &claim)? {
                        return Ok(Some(t));
                    }
            }
        }
        // 生成音效: the sounds the book writes near where it's being read that nothing has.
        if caps.sfx && self.drama_sound(book_id.clone()).map(|s| s.spots).unwrap_or(false) {
            let store = self.clip_store(&book_id)?;
            let have: Vec<SoundAsset> = super::builtin::assets().into_iter().chain(store.sounds.iter().cloned()).collect();
            for &s in order.iter().take(2) {
                let ds = self.drama_section(book_id.clone(), s)?;
                for e in &ds.events {
                    let key = super::sound::event_key(e);
                    if super::sound::pick_spot(&key, 0, &have).is_some() {
                        continue;
                    }
                    let id = format!("sfx:{book_id}:{}", sound_id(&key));
                    let description = super::sound::describe(e);
                    if let Some(t) = claim(id.clone(), DramaTask::Sfx { id, kind: key, description, seconds: super::sound::seconds_for(&e.kind) }) {
                        return Ok(Some(t));
                    }
                }
            }
        }
        // Clips taken from another device are made with its voices: nothing for this one to make.
        if caps.tts && self.clip_store(&book_id)?.borrowed.is_none() {
            let plan: Option<CastingPlan> = lock(&self.store).get(&format!("drama/{book_id}/plan"), "plan").ok().flatten().and_then(|v| serde_json::from_value(v).ok());
            if let Some(plan) = plan {
                if caps.design_voice {
                    for d in &plan.to_design {
                        let id = format!("design:{book_id}:{}", d.character);
                        let sample = b.rules.iter().flatten().find(|r| r.speaker == DramaSpeaker::Char { id: d.character }).and_then(|_| b.sentences.iter().flatten().next().cloned()).unwrap_or_default();
                        if let Some(t) = claim(id.clone(), DramaTask::DesignVoice { id, character: d.character, description: d.description.clone(), sample_text: sample }) {
                            return Ok(Some(t));
                        }
                    }
                }
                let mut store = self.clip_store(&book_id)?;
                for &s in order.iter().take(2) {
                    let ds = self.drama_section(book_id.clone(), s)?;
                    for c in self.section_clips(&book_id, &ds)? {
                        if store.made.contains_key(&c.key) {
                            continue;
                        }
                        let id = format!("tts:{book_id}:{}", c.key);
                        // Voice cards keep a separate similarity baseline per style so
                        // strong emotions (anger, shout) compare against their own style's mean.
                        let stats_key = if c.voice.starts_with("card.") || c.voice.starts_with("cloned.") {
                            format!("{}#{}", c.voice, super::card::style_for(&c.act).as_str())
                        } else {
                            c.voice.clone()
                        };
                        store.voice_of.insert(c.key.clone(), stats_key);
                        if let Some(t) = claim(id.clone(), DramaTask::Tts { id, clip: c }) {
                            self.save_clip_store(&book_id, &store)?;
                            return Ok(Some(t));
                        }
                    }
                }
            }
        }
        Ok(None)
    }

    /// Hands back what a task gave. Checked, kept, and the work moves on.
    pub fn drama_submit(&self, book_id: String, task_id: String, result: DramaResult) -> Result<DramaProgress> {
        lock(&self.drama).inflight.remove(&task_id);
        if result == DramaResult::Returned {
            return self.drama_progress(book_id);
        }
        if let DramaResult::Failed { .. } = result {
            let n = {
                let mut rt = lock(&self.drama);
                let n = rt.failures.entry(task_id.clone()).or_default();
                *n += 1;
                *n
            };
            // A voice that twice gave nothing for a clip: the narrator reads it, as for a clip
            // that kept failing its check (a new clip, tried afresh).
            if n == 2
                && let Some(key) = task_id.strip_prefix(&format!("tts:{book_id}:"))
            {
                let mut store = self.clip_store(&book_id)?;
                if store.fallback.get(key) != Some(&ClipVerdict::Narrator) {
                    store.fallback.insert(key.to_string(), ClipVerdict::Narrator);
                    self.save_clip_store(&book_id, &store)?;
                }
            }
            return self.drama_progress(book_id);
        }
        let parts: Vec<&str> = task_id.split(':').collect();
        let b = self.book_drama(&book_id)?;
        match (parts.first().copied(), &result) {
            (Some("cast"), DramaResult::Llm { text }) => {
                let i: usize = parts.get(2).and_then(|x| x.parse().ok()).unwrap_or(0);
                let total: usize = parts.get(3).and_then(|x| x.parse().ok()).unwrap_or(1);
                let v: ModelCast = match serde_json::from_str(ask::strip_fence(text)) {
                    Ok(v) => v,
                    Err(_) => {
                        *lock(&self.drama).failures.entry(task_id.clone()).or_default() += 1;
                        return self.drama_progress(book_id);
                    }
                };
                let raw: Value = serde_json::from_str(ask::strip_fence(text)).unwrap_or(Value::Null);
                lock(&self.store).set(&entity(&book_id), &format!("cast_part{i}"), raw)?;
                let got: Vec<ModelCast> = (0..total).filter_map(|k| if k == i { Some(v.clone()) } else { lock(&self.store).get(&entity(&book_id), &format!("cast_part{k}")).ok().flatten().and_then(|x| serde_json::from_value(x).ok()) }).collect();
                if got.len() == total {
                    let merged = if total == 1 { got.into_iter().next().unwrap() } else { merge_volumes(got) };
                    let value = model_cast_value(&merged);
                    lock(&self.store).set(&entity(&book_id), "cast", value)?;
                    self.forget_drama(&book_id);
                }
            }
            (Some("unquoted"), DramaResult::Llm { text }) => {
                let section: u32 = parts.last().and_then(|x| x.parse().ok()).unwrap_or(0);
                let Ok(ans) = serde_json::from_str::<UnquotedAnswer>(ask::strip_fence(text)) else {
                    *lock(&self.drama).failures.entry(task_id.clone()).or_default() += 1;
                    return self.drama_progress(book_id);
                };
                let Some(sentences) = b.sentences.get(section as usize) else { return self.drama_progress(book_id) };
                let asked: Vec<(u32, String)> = ans.lines.into_iter().map(|l| (l.s, l.text)).collect();
                let found = locate_lines(sentences, &asked);
                lock(&self.store).set(&entity(&book_id), &format!("unquoted{section}"), json!(found))?;
                self.forget_drama(&book_id);
            }
            (Some(kind @ ("attr" | "verify")), DramaResult::Llm { text }) => {
                let section: u32 = parts.get(2).and_then(|x| x.parse().ok()).unwrap_or(0);
                let first: u32 = parts.get(3).and_then(|x| x.parse().ok()).unwrap_or(0);
                let (scenes, ..) = self.section_input_parts(&book_id, &b, section);
                let Some(scene) = scenes.iter().find(|s| s.first == first).cloned() else { return self.drama_progress(book_id) };
                let verify = kind == "verify";
                let Some((sask, ..)) = self.ask_for_scene(&book_id, &b, section, &scene, verify) else { return self.drama_progress(book_id) };
                let sentences = &b.sentences[section as usize];
                let segs = &b.segs[section as usize];
                match ask::check(text, &sask, sentences, &b.cast, verify) {
                    Ok((checked, mscenes)) => {
                        let mut st = self.stored_section(&book_id, section);
                        let votes = store_votes(&checked, sentences, segs, &b.cast);
                        if verify {
                            st.verify.retain(|v| !votes.iter().any(|n| n.sentence == v.sentence && n.head == v.head));
                            st.verify.extend(votes);
                            st.verified.push(first);
                        } else {
                            st.first.retain(|v| !votes.iter().any(|n| n.sentence == v.sentence && n.head == v.head));
                            st.first.extend(votes);
                            st.attributed.push(first);
                            // The model's cut for this scene's stretch, if it checks out.
                            let rs = rule_scenes(section, sentences, &b.cast);
                            if let Some(ok) = checked_scenes(section, scene.first, scene.last, &mscenes, &rs, &b.cast) {
                                let _ = ok;
                                st.scenes.retain(|m| m.last < scene.first || m.first > scene.last);
                                st.scenes.extend(mscenes);
                                st.scenes.sort_by_key(|m| m.first);
                                // Keep the stored cut whole: fill gaps with the rules' scenes.
                                st.scenes = fill_gaps(&st.scenes, &rs, sentences.len() as u32);
                            }
                        }
                        self.save_section(&book_id, section, &st)?;
                    }
                    Err(_) => {
                        *lock(&self.drama).failures.entry(task_id.clone()).or_default() += 1;
                    }
                }
            }
            (Some("direct"), DramaResult::Llm { text }) => {
                let section: u32 = parts.get(2).and_then(|x| x.parse().ok()).unwrap_or(0);
                let first: u32 = parts.get(3).and_then(|x| x.parse().ok()).unwrap_or(0);
                let ds = self.drama_section(book_id.clone(), section)?;
                let Some(scene) = ds.scenes.iter().find(|s| s.first == first).cloned() else { return self.drama_progress(book_id) };
                let ans: DirectAnswer = match serde_json::from_str(ask::strip_fence(text)) {
                    Ok(a) => a,
                    Err(_) => {
                        *lock(&self.drama).failures.entry(task_id.clone()).or_default() += 1;
                        return self.drama_progress(book_id);
                    }
                };
                let parts_of: Vec<&DramaSegment> = ds.segments.iter().filter(|s| s.sentence >= scene.first && s.sentence <= scene.last).collect();
                let mut st = self.stored_section(&book_id, section);
                for p in &ans.parts {
                    let Some(seg) = parts_of.get(p.p.saturating_sub(1) as usize) else { continue };
                    if seg.kind == SegKind::Narration {
                        continue;
                    }
                    let act = act_from_model(p, &seg.act);
                    st.acts.retain(|a| !(a.0 == seg.sentence && a.1 == seg.start));
                    st.acts.push((seg.sentence, seg.start, act));
                }
                let ev = checked_events(&ans.events, &b.sentences[section as usize], scene.first, scene.last);
                st.events.retain(|e| e.sentence < scene.first || e.sentence > scene.last);
                st.events.extend(ev);
                st.directed.push(first);
                self.save_section(&book_id, section, &st)?;
            }
            (Some("tts"), DramaResult::Clip { clip, check }) => {
                let mut store = self.clip_store(&book_id)?;
                let attempt = store.attempts.get(&clip.key).copied().unwrap_or(0);
                let voice = store.voice_of.get(&clip.key).cloned().unwrap_or_default();
                let norm = store.stats.get(&voice).and_then(VoiceStats::norm);
                let same_each_time = lock(&self.drama).caps.tts_same_each_time;
                let verdict = match judge(check, norm.as_ref(), attempt) {
                    ClipVerdict::Again | ClipVerdict::Plain | ClipVerdict::Narrator if same_each_time && !super::queue::gross(check) => ClipVerdict::Keep,
                    v => v,
                };
                match verdict {
                    ClipVerdict::Keep => {
                        store.made.insert(clip.key.clone(), clip.clone());
                        store.stats.entry(voice).or_default().add(check.voice_sim, check.seconds, check.chars);
                    }
                    ClipVerdict::Again => {
                        store.attempts.insert(clip.key.clone(), attempt + 1);
                    }
                    // Already made by the narrator (or the narrator is its voice) and still not
                    // passing: kept as it is, the last try; noted on the cast page.
                    ClipVerdict::Narrator if attempt >= 3 || store.fallback.get(&clip.key) == Some(&ClipVerdict::Narrator) => {
                        store.made.insert(clip.key.clone(), clip.clone());
                        store.failed_check.insert(clip.key.clone());
                    }
                    v @ (ClipVerdict::Plain | ClipVerdict::Narrator) => {
                        store.attempts.insert(clip.key.clone(), attempt + 1);
                        store.fallback.insert(clip.key.clone(), v);
                    }
                }
                self.save_clip_store(&book_id, &store)?;
            }
            (Some("sfx"), DramaResult::Sound { samples }) => {
                // The key back from the task id isn't kept whole (it's hashed): find it again.
                let ds_keys: Vec<String> = (0..b.sentences.len() as u32)
                    .filter_map(|s| self.drama_section(book_id.clone(), s).ok())
                    .flat_map(|ds| ds.events.iter().map(super::sound::event_key).collect::<Vec<_>>())
                    .collect();
                let want = parts.get(2).copied().unwrap_or_default();
                if let Some(key) = ds_keys.into_iter().find(|k| sound_id(k) == want) {
                    let id = format!("gen-{want}");
                    let dir = self.drama_dir(&book_id)?.join("sounds");
                    std::fs::create_dir_all(&dir)?;
                    let bytes: Vec<u8> = samples.iter().flat_map(|v| v.to_le_bytes()).collect();
                    std::fs::write(dir.join(format!("{id}.f32")), bytes)?;
                    let mut store = self.clip_store(&book_id)?;
                    store.sounds.retain(|a| a.id != id);
                    store.sounds.push(SoundAsset { id, key, seconds: samples.len() as f32 / yuyue_mix::RATE as f32, loops: false, license: "generated".into() });
                    self.save_clip_store(&book_id, &store)?;
                }
            }
            (Some("design"), DramaResult::Voice { voice }) => {
                let ch: u32 = parts.get(2).and_then(|x| x.parse().ok()).unwrap_or(0);
                if let Some(c) = b.cast.get(ch) {
                    self.set_book_role(
                        book_id.clone(),
                        c.key().to_string(),
                        crate::roles::RoleVoice { voice: Some(voice.id.clone()), rate: 1.0, pitch: 0.0, right: false },
                    )?;
                }
            }
            _ => {}
        }
        self.drama_progress(book_id)
    }
}

fn model_cast_value(m: &ModelCast) -> Value {
    json!({
        "characters": m.characters.iter().map(|c| json!({
            "key": c.key, "names": c.names.iter().map(|n| json!({"text": n.text, "kind": n.kind})).collect::<Vec<_>>(),
            "called_by": c.called_by.iter().map(|x| json!({"who": x.who, "as": x.as_})).collect::<Vec<_>>(),
            "gender": c.gender, "age": c.age, "traits": c.traits, "speaks": c.speaks,
            "evidence": c.evidence.iter().map(|e| json!({"section": e.section, "quote": e.quote})).collect::<Vec<_>>(),
            "same_as": c.same_as, "revealed": c.revealed,
        })).collect::<Vec<_>>(),
        "merges": m.merges.iter().map(|x| json!({"a": x.a, "b": x.b, "evidence": {"section": x.evidence.section, "quote": x.evidence.quote}})).collect::<Vec<_>>(),
        "groups": m.groups.iter().map(|g| json!({"key": g.key, "names": g.names})).collect::<Vec<_>>(),
        "narrator": m.narrator.as_ref().map(|n| json!({"person": n.person, "who": n.who})),
        "readings": m.readings.iter().map(|r| json!({"word": r.word, "reading": r.reading, "evidence": r.evidence.as_ref().map(|e| json!({"section": e.section, "quote": e.quote}))})).collect::<Vec<_>>(),
    })
}

/// A stored cut kept whole: where the model's scenes leave a gap, the rules' fill it.
fn fill_gaps(model: &[ModelScene], rules: &[DramaScene], n: u32) -> Vec<ModelScene> {
    let mut out: Vec<ModelScene> = Vec::new();
    let mut at = 0u32;
    let mut m = model.to_vec();
    m.sort_by_key(|x| x.first);
    for x in m {
        if x.first < at {
            continue;
        }
        if x.first > at {
            for r in rules.iter().filter(|r| r.last >= at && r.first < x.first) {
                out.push(ModelScene { first: r.first.max(at), last: r.last.min(x.first - 1), place: String::new(), place_kind: None, indoor: None, time: None, weather: None, crowd: None, present: vec![] });
            }
        }
        at = x.last + 1;
        out.push(x);
    }
    if at < n {
        for r in rules.iter().filter(|r| r.last >= at) {
            out.push(ModelScene { first: r.first.max(at), last: r.last, place: String::new(), place_kind: None, indoor: None, time: None, weather: None, crowd: None, present: vec![] });
        }
    }
    out
}

impl YuyueCore {
    /// 无引号体: the model's lines of a section, checked and stored (sentence, start, end).
    fn unquoted_lines_of(&self, book: &str, section: u32) -> Option<Vec<(u32, u32, u32)>> {
        lock(&self.store).get(&entity(book), &format!("unquoted{section}")).ok().flatten().and_then(|v| serde_json::from_value(v).ok())
    }

    /// A book without quote marks: the section's lines asked of the model first, since who said
    /// them is asked of what they are.
    fn unquoted_task(&self, book: &str, b: &BookDrama, section: u32, claim: &dyn Fn(String, DramaTask) -> Option<DramaTask>) -> Option<DramaTask> {
        let sentences = b.sentences.get(section as usize)?;
        if !unquoted(sentences) || self.unquoted_lines_of(book, section).is_some() {
            return None;
        }
        let user: String = sentences.iter().enumerate().map(|(i, s)| format!("[{i}] {s}\n")).collect();
        let id = format!("unquoted:{book}:{section}");
        claim(id.clone(), DramaTask::Llm { id, tier: LlmTier::Fast, system: UNQUOTED_PROMPT.into(), user, schema: unquoted_schema(), cache_prefix_len: 0, max_output_tokens: 8_000 })
    }

    /// Its lines still being asked for (无引号体): who said them waits, or the answers would be
    /// for the sure lines only. Given up on after three failures.
    fn waiting_for_lines(&self, book: &str, b: &BookDrama, section: u32) -> bool {
        let Some(sentences) = b.sentences.get(section as usize) else { return false };
        unquoted(sentences) && self.unquoted_lines_of(book, section).is_none() && lock(&self.drama).failures.get(&format!("unquoted:{book}:{section}")).copied().unwrap_or(0) < 3
    }

    fn attr_task(&self, book: &str, b: &BookDrama, section: u32, claim: &dyn Fn(String, DramaTask) -> Option<DramaTask>) -> Result<Option<DramaTask>> {
        if section as usize >= b.sentences.len() || self.waiting_for_lines(book, b, section) {
            return Ok(None);
        }
        let (scenes, .., st) = self.section_input_parts(book, b, section);
        for sc in &scenes {
            if st.attributed.contains(&sc.first) {
                continue;
            }
            let Some((_, user, prefix)) = self.ask_for_scene(book, b, section, sc, false) else { continue };
            let id = format!("attr:{book}:{section}:{}", sc.first);
            let t = DramaTask::Llm { id: id.clone(), tier: LlmTier::Fast, system: ask::ATTRIBUTE_PROMPT.into(), user, schema: ask::attribute_schema(), cache_prefix_len: prefix as u32, max_output_tokens: 8_000 };
            if let Some(t) = claim(id, t) {
                return Ok(Some(t));
            }
        }
        Ok(None)
    }

    fn verify_task(&self, book: &str, b: &BookDrama, section: u32, caps: &DramaCaps, claim: &dyn Fn(String, DramaTask) -> Option<DramaTask>) -> Result<Option<DramaTask>> {
        if section as usize >= b.sentences.len() {
            return Ok(None);
        }
        let (scenes, first, ..) = self.section_input_parts(book, b, section);
        let st = self.stored_section(book, section);
        for sc in &scenes {
            if !st.attributed.contains(&sc.first) || st.verified.contains(&sc.first) {
                continue;
            }
            // Only lines the first model didn't find written need a second look.
            let all = lines_of(&b.sentences[section as usize], &b.segs[section as usize]);
            let needs = all.iter().filter(|l| l.first_sentence >= sc.first && l.first_sentence <= sc.last).any(|l| first.get(&l.utterance).is_some_and(|c| c.basis != LineBasis::Explicit && matches!(c.vote, Vote::Is(_))));
            if !needs {
                continue;
            }
            let Some((_, user, prefix)) = self.ask_for_scene(book, b, section, sc, true) else { continue };
            let id = format!("verify:{book}:{section}:{}", sc.first);
            let _ = caps;
            let t = DramaTask::Llm { id: id.clone(), tier: LlmTier::Verify, system: ask::VERIFY_PROMPT.into(), user, schema: ask::verify_schema(), cache_prefix_len: prefix as u32, max_output_tokens: 6_000 };
            if let Some(t) = claim(id, t) {
                return Ok(Some(t));
            }
        }
        Ok(None)
    }

    fn direct_task(&self, book: &str, b: &BookDrama, section: u32, claim: &dyn Fn(String, DramaTask) -> Option<DramaTask>) -> Result<Option<DramaTask>> {
        if section as usize >= b.sentences.len() {
            return Ok(None);
        }
        let st = self.stored_section(book, section);
        let ds = self.drama_section(book.to_string(), section)?;
        for sc in &ds.scenes {
            if st.directed.contains(&sc.first) || !st.attributed.contains(&sc.first) {
                continue;
            }
            let mut text = ask::cast_block(&b.cast);
            let prefix = text.len();
            text.push_str("<scene>\n");
            for (p, s) in ds.segments.iter().filter(|s| s.sentence >= sc.first && s.sentence <= sc.last).enumerate() {
                let who = match &s.speaker {
                    DramaSpeaker::Char { id } => b.cast.get(*id).map(|c| c.key().to_string()).unwrap_or_default(),
                    DramaSpeaker::Group { name } => name.clone(),
                    DramaSpeaker::Narrator => "旁白".into(),
                    DramaSpeaker::Unsure { .. } => "?".into(),
                };
                text.push_str(&format!("[p{} s{} {who}] {}\n", p + 1, s.sentence, s.text));
            }
            text.push_str("</scene>\n<event_kinds>");
            text.push_str(&super::sound::EVENT_KINDS.join(" "));
            text.push_str("</event_kinds>\n");
            let id = format!("direct:{book}:{section}:{}", sc.first);
            let t = DramaTask::Llm { id: id.clone(), tier: LlmTier::Fast, system: DIRECT_PROMPT.into(), user: text, schema: direct_schema(), cache_prefix_len: prefix as u32, max_output_tokens: 6_000 };
            if let Some(t) = claim(id, t) {
                return Ok(Some(t));
            }
        }
        Ok(None)
    }
}

#[cfg(test)]
impl YuyueCore {
    /// Prints, for each unsure line of a section, what each layer said.
    pub fn debug_section(&self, book: &str, section: u32) {
        let Ok(b) = self.book_drama(book) else { return };
        let (scenes, first, verify, user, st) = self.section_input_parts(book, &b, section);
        println!("scenes {:?}", scenes.iter().map(|s| (s.first, s.last, s.present.len())).collect::<Vec<_>>());
        println!("attributed {:?} verified {:?} first {} verify {}", st.attributed, st.verified, st.first.len(), st.verify.len());
        let input = SectionIn { sentences: &b.sentences[section as usize], segs: &b.segs[section as usize], rules: &b.rules[section as usize], scenes: &scenes, first: &first, verify: &verify, user: &user, cast: &b.cast, fit: None };
        let all = lines_of(input.sentences, input.segs);
        for sc in &scenes {
            let sl = super::attribute::scene_lines(&input, sc, &all);
            let outs = super::attribute::solve::solve(&sl.lines, &sl.present, &b.cast);
            for (l, o) in sl.lines.iter().zip(&outs) {
                if let (Some(f), Some(a)) = (l.fixed, &l.first)
                    && a.vote != Vote::Is(f) {
                        let name = |c: Cand| match c { Cand::Char(id) => b.cast.get(id).map(|c| c.names.join("/")).unwrap_or_default(), Cand::Group => "GROUP".into() };
                        let got = match &a.vote { Vote::Is(c) => name(*c), v => format!("{v:?}") };
                        let at = all.iter().find(|x| x.utterance == l.utterance).map(|x| (x.first_sentence, x.inner.chars().take(8).collect::<String>())).unwrap_or_default();
                        println!("  不一致 u{} {:?} 规则 {} 模型 {}", l.utterance, at, name(f), got);
                    }
                if l.fixed.is_some() {
                    continue;
                }
                println!("u{} concord {:.2} first {:?} verify {:?} post {:?}", l.utterance, sl.concord, l.first.as_ref().map(|f| (&f.vote, f.basis, f.anchor_ok)), l.verify.as_ref().map(|f| &f.vote), &o.post[..o.post.len().min(3)]);
            }
        }
    }
}

/// Rule answers made on one cast, moved to another by name: the narrator to its narrator, a
/// character to whoever has its fullest name (or any of its names), a new row if nobody does.
fn remap(answers: Vec<Vec<RuleAnswer>>, from: &CastTable, to: &mut CastTable, removed: &HashSet<String>) -> Vec<Vec<RuleAnswer>> {
    let mut map: HashMap<u32, Option<u32>> = HashMap::new();
    answers
        .into_iter()
        .map(|sec| {
            sec.into_iter()
                .map(|mut a| {
                    if let DramaSpeaker::Char { id } = a.speaker {
                        let new = *map.entry(id).or_insert_with(|| {
                            let c = from.get(id)?;
                            if c.narrator {
                                return Some(to.narrator());
                            }
                            if c.names.iter().any(|n| removed.contains(n)) || super::cast::pronoun_led(c.key()) {
                                return None;
                            }
                            to.resolve(c.key()).or_else(|| c.names.iter().find_map(|n| to.resolve(n))).or_else(|| Some(to.add_name(c.key(), false)))
                        });
                        match new {
                            Some(n) => a.speaker = DramaSpeaker::Char { id: n },
                            None => {
                                a.speaker = DramaSpeaker::Unsure { top: vec![] };
                                a.decided_by = DecidedBy::Unsure;
                                a.basis = LineBasis::None;
                            }
                        }
                    }
                    a
                })
                .collect()
        })
        .collect()
}

/// For tests and the command line: a core-less run over sentences.
pub fn rules_only(sections: &[Vec<String>]) -> (CastTable, Vec<Vec<RawSeg>>, Vec<Vec<RuleAnswer>>) {
    let rc = crate::script::Cast::read(sections);
    let segs: Vec<Vec<RawSeg>> = sections.iter().map(|s| segments(s, None)).collect();
    let mut scratch = from_rules(rc.names());
    for (s, sg) in sections.iter().zip(&segs) {
        rule_attribute(&rc, &mut scratch, s, sg);
    }
    let mut names: Vec<String> = scratch.chars.iter().filter(|c| !c.narrator).flat_map(|c| c.names.clone()).collect();
    names.sort();
    names.dedup();
    let first: Vec<Vec<RuleAnswer>> = sections.iter().zip(&segs).map(|(s, sg)| rule_attribute(&rc, &mut scratch.clone(), s, sg)).collect();
    let mut cast = from_rules(&names);
    let rules = remap(first, &scratch, &mut cast, &HashSet::new());
    (cast, segs, rules)
}

pub type DramaState = Mutex<DramaRuntime>;

/// A sound's key as a short id (a description can be anything).
fn sound_id(key: &str) -> String {
    blake3::hash(key.as_bytes()).to_hex()[..16].to_string()
}
