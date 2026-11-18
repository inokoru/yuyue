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
use super::casting::{CastingPlan, Together, VoiceInfo, cast as cast_voices, together_key};
use super::direct::{DIRECT_PROMPT, DirectAnswer, direct_schema, from_model as act_from_model, from_tag};
use super::queue::{ClipAsk, ClipCheck, ClipVerdict, VoiceNorm, Voices, clips, judge};
use super::scenes::{ModelScene, checked as checked_scenes, rule_scenes};
use super::sound::{SoundAsset, SoundEvent, checked_events};
use super::spans::{RawSeg, locate_lines, segments, unquoted};
use super::timeline::{MadeClip, DramaTimeline, TimelineItem, lay_out};
use super::{DramaAct, LineBasis, DramaCharacter, DecidedBy, CharacterGender, DramaScene, SegKind, DramaSegment, DramaSpeaker};
use crate::api::{YuyueCore, lock};
use crate::error::{CoreError, Result};

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

fn entity(book: &str) -> String {
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
fn lang_of(sentences: &[Vec<String>]) -> &'static str {
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
struct ClipStore {
    made: HashMap<String, MadeClip>,
    /// (section:sentence:start) → how it's being made now after failures.
    fallback: HashMap<String, ClipVerdict>,
    attempts: HashMap<String, u8>,
    /// Per voice: clips kept, mean and M2 of similarity (Welford), seconds and characters.
    stats: HashMap<String, VoiceStats>,
    /// Clip key → voice, for the clips asked for.
    voice_of: HashMap<String, String>,
    /// Kept after every try without passing 回听.
    #[serde(default)]
    failed_check: std::collections::HashSet<String>,
    /// 局域网传片段: the clips came from another device (a Mac), made with its voices; they're
    /// asked for by its plan and engine until this device casts the book itself.
    #[serde(default)]
    borrowed: Option<Borrowed>,
    /// 生成音效: the sounds made for this book (their samples beside it, `sounds/<id>.f32`).
    #[serde(default)]
    sounds: Vec<SoundAsset>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
struct Borrowed {
    device: String,
    plan: CastingPlan,
    acting: Vec<String>,
    engine: String,
}

/// 局域网传片段: what a device has made of a book, for another to take.
#[derive(Clone, Debug, Serialize, Deserialize)]
struct ClipManifest {
    plan: CastingPlan,
    acting: Vec<String>,
    engine: String,
    made: Vec<MadeClip>,
    fallback: HashMap<String, ClipVerdict>,
}

#[derive(Clone, Debug, Default, Serialize, Deserialize)]
struct VoiceStats {
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
    fn prints_path(&self) -> PathBuf {
        self.books_dir.parent().map(|p| p.join("voice-prints.json")).unwrap_or_else(|| self.books_dir.join("voice-prints.json"))
    }

    fn clones_dir(&self) -> PathBuf {
        let d = self.books_dir.parent().map(|p| p.join("clones")).unwrap_or_else(|| self.books_dir.join("clones"));
        let _ = std::fs::create_dir_all(&d);
        d
    }

    fn book_cards_dir(&self, book: &str) -> Result<PathBuf> {
        let d = self.drama_dir(book)?.join("cards");
        std::fs::create_dir_all(&d)?;
        Ok(d)
    }

    fn materialize_book_cards(&self, book: &str) -> Result<Vec<super::card::VoiceCard>> {
        let dir = self.book_cards_dir(book)?;
        let fields = lock(&self.store).fields(&format!("drama/{book}/cards")).unwrap_or_default();
        let mut by_id: HashMap<String, super::card::VoiceCard> = HashMap::new();
        for (id, val) in fields {
            if let Ok(packed) = serde_json::from_value::<super::card::PackedCard>(val)
                && let Some(c) = packed.materialize(&dir.join(&id))
            {
                by_id.insert(c.id.clone(), c);
            }
        }
        if let Ok(entries) = std::fs::read_dir(&dir) {
            for e in entries.flatten() {
                let p = e.path();
                if !p.is_dir() {
                    continue;
                }
                if let Some(raw) = std::fs::read(p.join("card.json")).ok().and_then(|b| serde_json::from_slice::<super::card::VoiceCard>(&b).ok())
                    && !by_id.contains_key(&raw.id)
                    && let Some(c) = super::card::validate_in_dir(raw, &p)
                {
                    by_id.insert(c.id.clone(), c);
                }
            }
        }
        let mut out: Vec<_> = by_id.into_values().collect();
        out.sort_by(|a, b| a.id.cmp(&b.id));
        Ok(out)
    }

    fn materialize_cloned_cards(&self) -> Vec<super::card::VoiceCard> {
        let dir = self.clones_dir();
        let fields = lock(&self.store).fields("clones").unwrap_or_default();
        let mut by_id: HashMap<String, super::card::VoiceCard> = HashMap::new();
        for (id, val) in fields {
            if let Ok(packed) = serde_json::from_value::<super::card::PackedCard>(val)
                && let Some(c) = packed.materialize(&dir.join(&id))
            {
                by_id.insert(c.id.clone(), c);
            }
        }
        if let Ok(entries) = std::fs::read_dir(&dir) {
            for e in entries.flatten() {
                let p = e.path();
                if !p.is_dir() {
                    continue;
                }
                if let Some(raw) = std::fs::read(p.join("card.json")).ok().and_then(|b| serde_json::from_slice::<super::card::VoiceCard>(&b).ok())
                    && !by_id.contains_key(&raw.id)
                    && let Some(c) = super::card::validate_in_dir(raw, &p)
                {
                    by_id.insert(c.id.clone(), c);
                }
            }
        }
        let mut out: Vec<_> = by_id.into_values().collect();
        out.sort_by(|a, b| a.id.cmp(&b.id));
        out
    }

    fn all_known_cards(&self, book: Option<&str>) -> HashMap<String, super::card::VoiceCard> {
        let mut map: HashMap<String, super::card::VoiceCard> = super::card::builtin_cards().into_iter().map(|c| (c.id.clone(), c)).collect();
        for c in self.materialize_cloned_cards() {
            map.insert(c.id.clone(), c);
        }
        if let Some(b) = book
            && let Ok(designed) = self.materialize_book_cards(b)
        {
            for c in designed {
                map.insert(c.id.clone(), c);
            }
        }
        map
    }

    /// The sounds made for a book, told to the mixer (which reads them from beside the book).
    fn register_sounds(&self, book: &str, store: &ClipStore) -> Vec<SoundAsset> {
        if let Ok(dir) = self.drama_dir(book) {
            let mut g = lock(&super::mixer::MADE_SOUNDS);
            for a in &store.sounds {
                g.insert(a.id.clone(), dir.join("sounds").join(format!("{}.f32", a.id)));
            }
        }
        store.sounds.clone()
    }

    fn drama_dir(&self, book: &str) -> Result<PathBuf> {
        let d = self.book_dir(book)?.join("drama");
        std::fs::create_dir_all(&d)?;
        Ok(d)
    }

    fn clip_store(&self, book: &str) -> Result<ClipStore> {
        let p = self.drama_dir(book)?.join("clips.json");
        Ok(std::fs::read(&p).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default())
    }

    fn save_clip_store(&self, book: &str, s: &ClipStore) -> Result<()> {
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

    /// Casts the book with the voices the device has; the user's own choices stay.
    pub fn drama_cast_voices(&self, book_id: String, voices: Vec<VoiceInfo>, narrator: String) -> Result<CastingPlan> {
        let b = self.book_drama(&book_id)?;
        // 声纹: the prints this device has made, for voices the app didn't measure itself.
        let prints = super::speaker::VoicePrints::read(&self.prints_path());
        let voices: Vec<VoiceInfo> = voices
            .into_iter()
            .map(|mut v| {
                if v.embedding.is_empty()
                    && let Some(e) = prints.voices.get(&v.id) {
                        v.embedding = e.clone();
                    }
                v
            })
            .collect();
        let mut together = Together::new();
        for section in 0..b.sentences.len() as u32 {
            let (d, scenes, _) = self.decided(&book_id, &b, section);
            let all = lines_of(&b.sentences[section as usize], &b.segs[section as usize]);
            for sc in &scenes {
                let mut count: HashMap<u32, u32> = HashMap::new();
                for x in &d {
                    if let DramaSpeaker::Char { id } = x.speaker
                        && all.iter().any(|l| l.utterance == x.utterance && l.first_sentence >= sc.first && l.first_sentence <= sc.last) {
                            *count.entry(id).or_default() += 1;
                        }
                }
                let ids: Vec<u32> = count.keys().copied().collect();
                for i in 0..ids.len() {
                    for j in i + 1..ids.len() {
                        *together.entry(together_key(ids[i], ids[j])).or_default() += count[&ids[i]] + count[&ids[j]];
                    }
                }
            }
        }
        let stored: HashMap<String, String> = lock(&self.store).fields(&format!("drama/{book_id}/voices")).unwrap_or_default().into_iter().filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string()))).collect();
        let fixed: HashMap<u32, String> = stored.iter().filter(|(k, _)| !k.starts_with('_')).filter_map(|(k, v)| b.cast.resolve(k).map(|id| (id, v.clone()))).collect();
        let lang = lang_of(&b.sentences);
        // Sex where the model hasn't said: from titles and pronouns, when it's clear.
        let chars: Vec<DramaCharacter> = b.cast.chars.iter().map(|c| DramaCharacter { gender: super::casting::guess_gender(c, &b.sentences), ..c.clone() }).collect();
        let mut plan = cast_voices(&chars, &voices, &together, &fixed, &narrator, lang);
        // This device makes the play itself now: clips taken from another are asked for no more.
        let mut cs = self.clip_store(&book_id)?;
        if cs.borrowed.take().is_some() {
            self.save_clip_store(&book_id, &cs)?;
        }
        if let Some(d) = stored.get("_dialogue") {
            plan.dialogue = d.clone();
        }
        let acting: Vec<&str> = voices.iter().filter(|v| v.acts).map(|v| v.id.as_str()).collect();
        let list: Vec<(String, String, Value)> = vec![
            (format!("drama/{book_id}/plan"), "plan".to_string(), serde_json::to_value(&plan).unwrap_or(Value::Null)),
            (format!("drama/{book_id}/plan"), "acting".to_string(), json!(acting)),
        ];
        let refs: Vec<(&str, &str, Value)> = list.iter().map(|(a, b, c)| (a.as_str(), b.as_str(), c.clone())).collect();
        lock(&self.store).set_many(&refs)?;
        Ok(plan)
    }

    /// The user's voice for a character (by name), or for 对话 with `_dialogue`.
    pub fn drama_set_voice(&self, book_id: String, name: String, voice: String) -> Result<()> {
        lock(&self.store).set(&format!("drama/{book_id}/voices"), &name, json!(voice))
    }

    /// 剧化: whether speech tags that only say who speaks are left out.
    pub fn drama_mode(&self, book_id: String) -> bool {
        lock(&self.store).get(&entity(&book_id), "drama_mode").ok().flatten().and_then(|v| v.as_bool()).unwrap_or(false)
    }

    /// 剧化: speech tags that only say who speaks are left out.
    pub fn drama_set_mode(&self, book_id: String, drama_mode: bool) -> Result<()> {
        lock(&self.store).set(&entity(&book_id), "drama_mode", json!(drama_mode))
    }

    /// The scenes of a section laid out with the clips made so far.
    pub fn drama_timelines(&self, book_id: String, section: u32, library: Vec<SoundAsset>) -> Result<Vec<DramaTimeline>> {
        Ok(self.scene_layouts(&book_id, section, library)?.into_iter().map(|(tl, _)| tl).collect())
    }

    /// 环境音 / 音效 / 带语气: this book's settings.
    pub fn drama_sound(&self, book_id: String) -> Result<DramaSound> {
        Ok(lock(&self.store).get(&entity(&book_id), "sound")?.and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default())
    }

    pub fn drama_set_sound(&self, book_id: String, sound: DramaSound) -> Result<()> {
        lock(&self.store).set(&entity(&book_id), "sound", serde_json::to_value(sound).unwrap_or(Value::Null))
    }

    /// Whether any voice cast in this book can act (带语气 has something to switch).
    pub fn drama_can_act(&self, book_id: String) -> bool {
        !self.acting_voices(&book_id).is_empty()
    }

    /// A clip's text in pieces, the words the book gave a reading for (`{word|reading}` in the
    /// clip's text) apart, for a voice that can be told how to say a word (Azure's phonemes).
    pub fn drama_spoken_parts(&self, text: String) -> Vec<SpokenPart> {
        spoken_parts(&text)
    }

    /// A clip's text as it's read, the reading marks taken out: for voices that can't be told.
    pub fn drama_plain_text(&self, text: String) -> String {
        spoken_parts(&text).into_iter().map(|p| p.text).collect()
    }

    /// The acting a clip asks for as one plain instruction, for voices that take one
    /// (“压低声音，带着冷笑，语速稍快”). Empty for none.
    pub fn drama_act_instruction(&self, act: DramaAct, chinese: bool) -> String {
        act.instruction(chinese)
    }

    /// The acting as inline tags around the text, for voices that read them (`[whispers]`).
    pub fn drama_act_tags(&self, act: DramaAct) -> ActTags {
        let (head, tail) = act.tags();
        ActTags { head, tail }
    }

    /// 听成一部剧, a sentence at a time: the stretch of its scene's timeline from this sentence's
    /// start to the next one's (so the room tone and the pause after it come along), and the clips
    /// it needs. The app hands those to `DramaMixer` and plays `render_span(from, to)`. None while
    /// any of the sentence's clips isn't made (the voice reads it as usual).
    pub fn drama_sentence_window(&self, book_id: String, section: u32, sentence: u32) -> Result<Option<DramaWindow>> {
        let Some((tl, complete)) = self.scene_layouts(&book_id, section, Vec::new())?.into_iter().find(|(t, _)| t.first <= sentence && sentence <= t.last) else { return Ok(None) };
        Ok(window_of(tl, complete, sentence))
    }

    /// A voice's samples brought to the mixer's rate (48 kHz), for a device whose voices speak at
    /// another (Android's 16 or 24 kHz). Linear in between: speech has nothing up there to lose.
    pub fn drama_resample(&self, samples: Vec<f32>, from_rate: u32) -> Vec<f32> {
        let to = yuyue_mix::RATE as f64;
        let from = from_rate.max(1) as f64;
        if samples.is_empty() || (from - to).abs() < 1.0 {
            return samples;
        }
        let n = ((samples.len() as f64) * to / from).round() as usize;
        (0..n)
            .map(|i| {
                let x = i as f64 * from / to;
                let k = x.floor() as usize;
                let f = (x - k as f64) as f32;
                let a = samples[k.min(samples.len() - 1)];
                let b = samples[(k + 1).min(samples.len() - 1)];
                a + (b - a) * f
            })
            .collect()
    }

    /// 听成一部剧: a sentence's clips, in order, when every one of them is made (empty otherwise,
    /// and the sentence is read by the voice as usual).
    pub fn drama_sentence_clips(&self, book_id: String, section: u32, sentence: u32) -> Result<Vec<String>> {
        let ds = self.drama_section(book_id.clone(), section)?;
        let asks = self.section_clips(&book_id, &ds)?;
        let store = self.clip_store(&book_id)?;
        let mine: Vec<&ClipAsk> = asks.iter().filter(|c| c.sentence == sentence).collect();
        if mine.is_empty() || mine.iter().any(|c| !store.made.contains_key(&c.key)) {
            return Ok(Vec::new());
        }
        Ok(mine.iter().map(|c| c.key.clone()).collect())
    }

    /// 导出成有声书: chapter marks written into the M4A the app made of the play.
    pub fn drama_add_chapters(&self, path: String, marks: Vec<crate::audio::AudioMark>) -> Result<()> {
        crate::m4b::add_chapters(std::path::Path::new(&path), &marks)
    }

    /// 导出成有声书: chapter marks, book title, author and cover written into the M4B.
    pub fn drama_finish_m4b(&self, book_id: String, path: String, marks: Vec<crate::audio::AudioMark>) -> Result<()> {
        let meta = self.summary(&book_id).ok().map(|s| crate::m4b::Meta {
            title: Some(s.title),
            author: (!s.authors.is_empty()).then(|| s.authors.join(&crate::i18n::tr("、", ", "))),
            cover: s.cover_path.and_then(|p| std::fs::read(p).ok()),
        });
        crate::m4b::add_chapters_and_meta(std::path::Path::new(&path), &marks, meta.as_ref())
    }

    /// 声纹: a model's input (80-band filter banks at 16 kHz) for `samples` at `rate`.
    pub fn speaker_features(&self, samples: Vec<f32>, rate: u32) -> super::speaker::SpeakerFeatures {
        super::speaker::features(&samples, rate)
    }

    /// 声音素材库: the downloaded sounds' folder (its `library.json` and Ogg files). Scenes are
    /// laid out with them before the built-in ones. How many sounds it has (0: none, as before).
    pub fn drama_set_sound_library(&self, dir: String) -> u32 {
        super::library::set(std::path::Path::new(&dir)) as u32
    }

    /// The sentence each voice reads once for its reference print.
    pub fn speaker_sample_text(&self, language: String) -> String {
        super::speaker::sample_text(&language).to_string()
    }

    /// The speaker model in use: prints made by another model don't compare, so they go.
    pub fn speaker_model(&self, model: String) {
        let p = self.prints_path();
        let mut v = super::speaker::VoicePrints::read(&p);
        if v.model != model {
            v = super::speaker::VoicePrints { model, voices: HashMap::new(), genders: v.genders };
            v.write(&p);
        }
    }

    /// Voices that have no reference print yet, of `voices`.
    pub fn speaker_unprinted(&self, voices: Vec<String>) -> Vec<String> {
        let v = super::speaker::VoicePrints::read(&self.prints_path());
        voices.into_iter().filter(|id| !v.voices.contains_key(id)).collect()
    }

    /// A voice's reference print, from its sample read once.
    pub fn speaker_set_print(&self, voice: String, embedding: Vec<f32>) {
        let p = self.prints_path();
        let mut v = super::speaker::VoicePrints::read(&p);
        v.voices.insert(voice, embedding);
        v.write(&p);
    }

    /// Estimates a voice's gender from its sample's voiced pitch when the engine doesn't report it.
    pub fn speaker_voice_gender(&self, samples: Vec<f32>, rate: u32) -> crate::drama::CharacterGender {
        super::speaker::voice_gender(&samples, rate)
    }

    /// Kept gender of `voice`, or `Unknown` before it's measured.
    pub fn speaker_known_gender(&self, voice: String) -> crate::drama::CharacterGender {
        super::speaker::VoicePrints::read(&self.prints_path())
            .genders
            .get(&voice)
            .copied()
            .unwrap_or(crate::drama::CharacterGender::Unknown)
    }

    /// Remembers `voice`'s measured gender on this device.
    pub fn speaker_set_gender(&self, voice: String, gender: crate::drama::CharacterGender) {
        let p = self.prints_path();
        let mut v = super::speaker::VoicePrints::read(&p);
        v.genders.insert(voice, gender);
        v.write(&p);
    }

    // ── 声音卡 (`VoiceCard`, §2.3 & §3.1 & §6.2) ─────────────────────────────

    /// Maps fine-grained director notes onto the 7 reference clip styles (§2.2).
    pub fn card_style_for(&self, act: DramaAct) -> super::card::CardStyle {
        super::card::style_for(&act)
    }

    /// Speaking instruction for generating a reference clip of `style` (§2.1).
    pub fn card_style_instruction(&self, style: super::card::CardStyle, chinese: bool) -> String {
        style.instruction(chinese).to_string()
    }

    /// Canonical sentence read when recording or generating a reference clip (§4.2 & §6.1).
    pub fn card_script(&self, lang: String, style: super::card::CardStyle) -> String {
        super::card::script_for(&lang, style).to_string()
    }

    /// Built-in cards from the registered `voices-1/` pack (§2.3).
    pub fn cards_builtin(&self) -> Vec<super::card::VoiceCard> {
        super::card::builtin_cards()
    }

    /// Registers the `voices-1/` pack folder (`voices.json` + Ogg clips); returns valid card count.
    pub fn cards_set_pack(&self, dir: String) -> u32 {
        super::card::set_pack(std::path::Path::new(&dir)) as u32
    }

    /// Cards designed specifically for `book_id` (materializing any synced from other devices).
    pub fn cards_of_book(&self, book_id: String) -> Result<Vec<super::card::VoiceCard>> {
        self.materialize_book_cards(&book_id)
    }

    /// Packs all designed cards of `book_id` (with base64 clip audio) as JSON for LAN `/card` (§2.4).
    pub fn cards_pack_book_json(&self, book_id: String) -> Result<String> {
        let dir = self.book_cards_dir(&book_id)?;
        let packed: Vec<super::card::PackedCard> = self
            .materialize_book_cards(&book_id)?
            .into_iter()
            .filter_map(|c| super::card::PackedCard::pack(&c, &dir.join(&c.id)))
            .collect();
        serde_json::to_string(&packed).map_err(|e| CoreError::Invalid { detail: e.to_string() })
    }

    /// Adopts designed cards fetched from LAN `/card` into `book_id`, returning how many cards exist.
    pub fn cards_adopt_book_json(&self, book_id: String, json: String) -> Result<u32> {
        let list: Vec<super::card::PackedCard> = serde_json::from_str(&json).map_err(|e| CoreError::Invalid { detail: e.to_string() })?;
        let dir = self.book_cards_dir(&book_id)?;
        let mut adopted = 0u32;
        for p in list {
            if let Some(c) = p.materialize(&dir.join(&p.card.id)) {
                lock(&self.store).set(&format!("drama/{book_id}/cards"), &c.id, serde_json::to_value(&p).unwrap_or(Value::Null))?;
                adopted += 1;
            }
        }
        Ok(adopted)
    }

    /// User-cloned cards on this device (plus any synced from other devices).
    pub fn cards_cloned(&self) -> Vec<super::card::VoiceCard> {
        self.materialize_cloned_cards()
    }

    /// Validates `card` against Ogg files in `source_dir` (which may already be the destination
    /// folder), copies them into the canonical card folder (`drama/cards/<id>/` for a book, or
    /// `clones/<id>/` when `book_id` is `None`), and writes to the sync store when appropriate.
    pub fn card_save(&self, book_id: Option<String>, card: super::card::VoiceCard, source_dir: String) -> Result<super::card::VoiceCard> {
        if !super::card::safe_card_id(&card.id) {
            return Err(CoreError::Invalid { detail: "bad card id".into() });
        }
        let src = PathBuf::from(source_dir);
        let validated = super::card::validate_in_dir(card, &src)
            .ok_or_else(|| CoreError::Invalid { detail: "card needs a valid neutral ogg inside its folder".into() })?;
        let dst = match &book_id {
            Some(b) => self.book_cards_dir(b)?.join(&validated.id),
            None => self.clones_dir().join(&validated.id),
        };
        std::fs::create_dir_all(&dst)?;
        if src != dst {
            for c in &validated.clips {
                let to = dst.join(&c.file);
                if let Some(p) = to.parent() {
                    std::fs::create_dir_all(p)?;
                }
                std::fs::copy(src.join(&c.file), to)?;
            }
        }
        std::fs::write(
            dst.join("card.json"),
            serde_json::to_vec_pretty(&validated).map_err(|e| CoreError::Invalid { detail: e.to_string() })?,
        )?;
        match &book_id {
            Some(b) => {
                if let Some(packed) = super::card::PackedCard::pack(&validated, &dst) {
                    lock(&self.store).set(&format!("drama/{b}/cards"), &validated.id, serde_json::to_value(packed).unwrap_or(Value::Null))?;
                }
            }
            None => {
                let val = if validated.sync {
                    super::card::PackedCard::pack(&validated, &dst).and_then(|p| serde_json::to_value(p).ok()).unwrap_or(Value::Null)
                } else {
                    Value::Null
                };
                lock(&self.store).set("clones", &validated.id, val)?;
            }
        }
        Ok(validated)
    }

    /// Deletes a designed or cloned card from disk and sync store.
    pub fn card_delete(&self, book_id: Option<String>, card_id: String) -> Result<()> {
        if !super::card::safe_card_id(&card_id) {
            return Ok(());
        }
        match book_id {
            Some(b) => {
                let _ = std::fs::remove_dir_all(self.book_cards_dir(&b)?.join(&card_id));
                lock(&self.store).set(&format!("drama/{b}/cards"), &card_id, Value::Null)?;
            }
            None => {
                let _ = std::fs::remove_dir_all(self.clones_dir().join(&card_id));
                lock(&self.store).set("clones", &card_id, Value::Null)?;
            }
        }
        Ok(())
    }

    /// Finds a card by `id` among built-in, cloned, and (if `book_id` is given) book-designed cards.
    pub fn card_find(&self, book_id: Option<String>, id: String) -> Option<super::card::VoiceCard> {
        self.all_known_cards(book_id.as_deref()).remove(&id)
    }

    /// Absolute path to `id`'s reference Ogg for `style`, falling back to `Neutral` when `style`
    /// wasn't recorded (§2.1 & §2.3).
    pub fn card_clip_path(&self, book_id: Option<String>, id: String, style: super::card::CardStyle) -> Option<String> {
        self.card_clip_resolved(book_id, id, style).map(|c| c.file)
    }

    /// Resolved `CardClip` (with `file` expanded to the absolute Ogg path and `text` matching the
    /// resolved clip, including when `style` falls back to `Neutral`).
    pub fn card_clip_resolved(&self, book_id: Option<String>, id: String, style: super::card::CardStyle) -> Option<super::card::CardClip> {
        if let Some((p, mut clip)) = super::card::builtin_clip(&id, style) {
            clip.file = p.to_string_lossy().into_owned();
            return Some(clip);
        }
        let card = self.card_find(book_id.clone(), id.clone())?;
        let mut clip = card
            .clips
            .iter()
            .find(|c| c.style == style)
            .or_else(|| card.clips.iter().find(|c| c.style == super::card::CardStyle::Neutral))?
            .clone();
        let base = match (&card.origin, book_id) {
            (super::card::CardOrigin::Designed { book, .. }, _) => self.book_cards_dir(book).ok()?.join(&card.id),
            (_, Some(b)) if self.book_cards_dir(&b).ok().is_some_and(|d| d.join(&card.id).is_dir()) => self.book_cards_dir(&b).ok()?.join(&card.id),
            _ => self.clones_dir().join(&card.id),
        };
        let full = base.join(&clip.file);
        if !full.is_file() {
            return None;
        }
        clip.file = full.to_string_lossy().into_owned();
        Some(clip)
    }

    /// Checks a user's recorded cloning sample against §6.2's duration, level, clipping, noise & CER gates.
    pub fn card_check(&self, samples: Vec<f32>, rate: u32, text: String, heard: Option<String>) -> super::card::CardCheck {
        super::card::check_sample(&samples, rate, &text, heard.as_deref())
    }

    /// Builds a `VoiceInfo` from a `VoiceCard` for `drama_cast_voices` (§2.3).
    pub fn card_voice_info(&self, card: super::card::VoiceCard, embedding: Vec<f32>, acts: bool) -> VoiceInfo {
        super::card::to_voice_info(&card, embedding, acts)
    }

    /// Decodes a reference clip (`.ogg` or `.wav`) at `path` into mono `f32` samples at `target_rate`
    /// (pass `0` to keep the file's sample rate).
    pub fn card_decode_clip(&self, path: String, target_rate: u32) -> Result<Vec<f32>> {
        let bytes = std::fs::read(&path)?;
        super::card::decode_clip_mono(&bytes, target_rate)
            .map(|(s, _)| s)
            .ok_or_else(|| CoreError::Invalid { detail: "unreadable reference audio".into() })
    }

    /// Trims head/tail silence (leaving 0.1 s), resamples to `target_rate`, and normalizes speech
    /// loudness to −20 LUFS (§2.3 & §3.2).
    pub fn card_prepare_pcm(&self, samples: Vec<f32>, rate: u32, target_rate: u32) -> Vec<f32> {
        super::card::prepare_reference_pcm(&samples, rate, target_rate)
    }

    /// Prepares `samples` (trim silence + resample to 24 kHz + −20 LUFS) and writes a 16-bit mono
    /// WAV file to `path`, returning the resulting duration in seconds.
    pub fn card_write_wav(&self, path: String, samples: Vec<f32>, rate: u32) -> Result<f32> {
        let pcm = super::card::prepare_reference_pcm(&samples, rate, super::zipvoice::LIVE_RATE);
        if pcm.is_empty() {
            return Err(CoreError::Invalid { detail: "empty audio".into() });
        }
        let dst = PathBuf::from(&path);
        if let Some(p) = dst.parent() {
            std::fs::create_dir_all(p)?;
        }
        std::fs::write(&dst, super::card::encode_wav_16bit(&pcm, super::zipvoice::LIVE_RATE))?;
        Ok(pcm.len() as f32 / super::zipvoice::LIVE_RATE as f32)
    }

    // ── 即时层纯计算前端与声码器 DSP (`ZipVoice` + `Vocos 24 kHz`, §3.2 & §3.3) ──

    /// Registers the `live-zipvoice-1/` directory (`tokens.txt` + `lexicon-*.txt`).
    pub fn live_voice_set_dir(&self, dir: String) -> u32 {
        super::zipvoice::set_model_dir(std::path::Path::new(&dir)) as u32
    }

    /// Splits a sentence into clauses that fit cleanly inside the ≤ 9 s mobile attention window.
    pub fn live_voice_clauses(&self, text: String, lang: String) -> Vec<String> {
        super::zipvoice::split_clauses(&text, &lang)
    }

    /// Tokenizes a clause (with `{词|读音}` and Traditional Chinese support) into ZipVoice token IDs.
    pub fn live_voice_tokens(&self, text: String, lang: String) -> Vec<i64> {
        super::zipvoice::tokenize(&text, &lang)
    }

    /// Builds a cached `ZipVoicePrompt` from a card's reference clip (`path` + exact `text`).
    pub fn live_voice_prompt(&self, path: String, text: String, lang: String) -> Result<super::zipvoice::ZipVoicePrompt> {
        let bytes = std::fs::read(&path)?;
        let (samples, _) = super::card::decode_clip_mono(&bytes, super::zipvoice::LIVE_RATE)
            .ok_or_else(|| CoreError::Invalid { detail: "unreadable reference clip".into() })?;
        super::zipvoice::build_prompt(&samples, super::zipvoice::LIVE_RATE, &text, &lang)
            .ok_or_else(|| CoreError::Invalid { detail: "empty prompt tokens or features".into() })
    }

    /// Shifted Euler timesteps (`num_steps + 1` floats) for flow-matching decoding.
    pub fn live_voice_timesteps(&self, num_steps: u32, t_shift: f32) -> Vec<f32> {
        super::zipvoice::shifted_timesteps(num_steps, t_shift)
    }

    /// Deterministic standard-normal initial noise `x` of length `len` for `seed`.
    pub fn live_voice_noise(&self, len: u32, seed: u64) -> Vec<f32> {
        super::zipvoice::standard_normal(len as usize, seed)
    }

    /// Reconstructs 24 kHz mono −20 LUFS speech from Vocos outputs `mag`, `x_cos`, `y_sin` (`[1, 513, frames]`).
    pub fn live_voice_istft(&self, mag: Vec<f32>, x_cos: Vec<f32>, y_sin: Vec<f32>, frames: u32) -> Vec<f32> {
        super::zipvoice::vocos_istft(&mag, &x_cos, &y_sin, frames as usize)
    }

    /// §3.3 lightweight quality gate (duration ±50%, head/tail/internal silence, < 0.1% clipping).
    pub fn live_voice_check(&self, samples: Vec<f32>, rate: u32, text: String, lang: String, expected_rate: f32) -> bool {
        super::zipvoice::light_check(&samples, rate, &text, &lang, expected_rate)
    }

    /// Which layer (`Studio` vs `Live`) `section` plays from: `Studio` only when every clip of the
    /// section is already made under `studio_engine`, so voices never switch mid-chapter (§3.1).
    pub fn drama_section_layer(&self, book_id: String, section: u32, studio_engine: String) -> Result<super::card::DramaLayer> {
        if studio_engine.is_empty() {
            return Ok(super::card::DramaLayer::Live);
        }
        let ds = self.drama_section(book_id.clone(), section)?;
        let asks = self.section_clips_with_engine(&book_id, &ds, Some(&studio_engine))?;
        if asks.is_empty() {
            return Ok(super::card::DramaLayer::Live);
        }
        let store = self.clip_store(&book_id)?;
        let all_made = asks.iter().all(|c| store.made.contains_key(&c.key));
        Ok(if all_made { super::card::DramaLayer::Studio } else { super::card::DramaLayer::Live })
    }

    /// 回听: how much a clip sounds like its voice (cosine to the voice's print); 1 when there's
    /// no print or no clip print to go by.
    pub fn speaker_similarity(&self, voice: String, embedding: Vec<f32>) -> f32 {
        let v = super::speaker::VoicePrints::read(&self.prints_path());
        match v.voices.get(&voice) {
            Some(r) if !embedding.is_empty() && r.len() == embedding.len() => super::casting::alike(r, &embedding),
            _ => 1.0,
        }
    }

    /// 局域网传片段, the giving side: what this device has made of the book (its plan, engine,
    /// made clips), as JSON for another device's `drama_adopt_clips`. None before it's cast.
    pub fn drama_clip_manifest(&self, book_id: String) -> Result<Option<String>> {
        let store = self.clip_store(&book_id)?;
        let (plan, acting, engine) = match &store.borrowed {
            Some(b) => (Some(b.plan.clone()), b.acting.clone(), b.engine.clone()),
            None => (
                lock(&self.store).get(&format!("drama/{book_id}/plan"), "plan").ok().flatten().and_then(|v| serde_json::from_value(v).ok()),
                self.acting_voices(&book_id).into_iter().collect(),
                lock(&self.drama).caps.engine.clone(),
            ),
        };
        let Some(plan) = plan else { return Ok(None) };
        let m = ClipManifest { plan, acting, engine, made: store.made.values().cloned().collect(), fallback: store.fallback.clone() };
        Ok(Some(serde_json::to_string(&m).map_err(|e| CoreError::Invalid { detail: e.to_string() })?))
    }

    /// The clip keys a manifest lists.
    pub fn drama_manifest_keys(&self, manifest: String) -> Vec<String> {
        serde_json::from_str::<ClipManifest>(&manifest).map(|m| m.made.into_iter().map(|c| c.key).collect()).unwrap_or_default()
    }

    /// 局域网传片段, the taking side: the clips in `have` (their files now beside the book) are
    /// made, by the giving device's plan and engine, until this device casts the book itself.
    pub fn drama_adopt_clips(&self, book_id: String, device: String, manifest: String, have: Vec<String>) -> Result<()> {
        let m: ClipManifest = serde_json::from_str(&manifest).map_err(|e| CoreError::Invalid { detail: e.to_string() })?;
        let mut store = self.clip_store(&book_id)?;
        // Taken from a device whose plan isn't this one's: the clips of the old one don't fit.
        let same = store.borrowed.as_ref().is_some_and(|b| b.plan == m.plan && b.engine == m.engine);
        if !same {
            store.made.clear();
            store.fallback.clear();
            store.attempts.clear();
        }
        let have: HashSet<String> = have.into_iter().collect();
        for c in m.made.into_iter().filter(|c| have.contains(&c.key)) {
            store.made.insert(c.key.clone(), c);
        }
        store.fallback.extend(m.fallback);
        store.borrowed = Some(Borrowed { device, plan: m.plan, acting: m.acting, engine: m.engine });
        self.save_clip_store(&book_id, &store)?;
        Ok(())
    }

    /// The device this book's clips were taken from, while they are.
    pub fn drama_borrowed_from(&self, book_id: String) -> Option<String> {
        self.clip_store(&book_id).ok().and_then(|s| s.borrowed).map(|b| b.device)
    }

    /// 整本提前做好: how many sections have all their clips made, of how many.
    pub fn drama_made(&self, book_id: String) -> Result<DramaMade> {
        let b = self.book_drama(&book_id)?;
        let store = self.clip_store(&book_id)?;
        let sections = b.sentences.len() as u32;
        let mut made_sections = Vec::new();
        for s in 0..sections {
            let ds = self.drama_section(book_id.clone(), s)?;
            let asks = self.section_clips(&book_id, &ds)?;
            if !asks.is_empty() && asks.iter().all(|c| store.made.contains_key(&c.key)) {
                made_sections.push(s);
            }
        }
        // Sections with nothing to say (a picture, a blank page) count as made.
        let empty = (0..sections).filter(|&s| b.sentences[s as usize].is_empty()).count() as u32;
        Ok(DramaMade { sections: sections - empty, made: made_sections.len() as u32, made_sections })
    }

    /// 回听: measures a clip the app made (see `queue::measure`) for the Tts result.
    pub fn drama_measure_clip(&self, text: String, heard: String, samples: Vec<f32>, rate: u32, voice_sim: f32, chinese: bool) -> ClipCheck {
        let text: String = spoken_parts(&text).into_iter().map(|p| p.text).collect();
        super::queue::measure(&text, &heard, &samples, rate, voice_sim, chinese)
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
                        // §7.3: Voice cards keep a separate similarity baseline per style so
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
                    self.drama_set_voice(book_id.clone(), c.key().to_string(), voice.id.clone())?;
                }
            }
            _ => {}
        }
        self.drama_progress(book_id)
    }

    /// The pronunciations the model's cast found, checked against the book.
    pub fn drama_readings(&self, book_id: String) -> Result<Vec<DramaReading>> {
        let b = self.book_drama(&book_id)?;
        Ok(b.readings.iter().map(|r| DramaReading { word: r.word.clone(), reading: r.reading.clone() }).collect())
    }
}

#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct DramaReading {
    pub word: String,
    pub reading: String,
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

    /// The clips a section needs with the current plan.
    /// Each scene of a section laid out with the clips made so far, and whether all its clips
    /// are made. The library is the app's (downloaded) sounds and the built-in ones, as the
    /// book's 环境音 / 音效 allow.
    fn scene_layouts(&self, book_id: &str, section: u32, library: Vec<SoundAsset>) -> Result<Vec<(DramaTimeline, bool)>> {
        let ds = self.drama_section(book_id.to_string(), section)?;
        let asks = self.section_clips(book_id, &ds)?;
        let store = self.clip_store(book_id)?;
        let made = |k: &str| store.made.get(k).cloned();
        let sound = self.drama_sound(book_id.to_string())?;
        let made_sounds = self.register_sounds(book_id, &store);
        let library: Vec<SoundAsset> = library.into_iter().chain(super::library::assets()).chain(super::builtin::assets()).chain(made_sounds).filter(|a| if a.loops || a.key == super::builtin::TRANSITION { sound.beds } else { sound.spots }).collect();
        let paragraphs = self.paragraph_starts(book_id, section);
        // 过门 (with 环境音): between two scenes of the chapter that are somewhere else, or at
        // another time of day; the library's before the built-in ones, taken in turn.
        let transitions: Vec<&SoundAsset> = library.iter().filter(|a| a.key == super::builtin::TRANSITION).collect();
        let mut changes = 0usize;
        Ok(ds
            .scenes
            .iter()
            .enumerate()
            .map(|(i, sc)| {
                let moved = i > 0 && {
                    let p = &ds.scenes[i - 1];
                    p.place != sc.place || p.place_kind != sc.place_kind || p.time != sc.time
                };
                let transition = if moved && !transitions.is_empty() {
                    changes += 1;
                    Some(transitions[(changes - 1) % transitions.len()])
                } else {
                    None
                };
                let segs: Vec<DramaSegment> = ds.segments.iter().filter(|s| s.sentence >= sc.first && s.sentence <= sc.last).cloned().collect();
                let a: Vec<ClipAsk> = asks.iter().filter(|c| c.sentence >= sc.first && c.sentence <= sc.last).cloned().collect();
                let ev: Vec<SoundEvent> = ds.events.iter().filter(|e| e.sentence >= sc.first && e.sentence <= sc.last).cloned().collect();
                let complete = a.iter().all(|c| store.made.contains_key(&c.key));
                (lay_out(sc, &segs, &a, &made, &paragraphs, &ev, &library, transition), complete)
            })
            .collect())
    }

    /// The sentences that begin a paragraph (a longer pause before them), from the section's
    /// page: the first sentence after each block's start.
    fn paragraph_starts(&self, book: &str, section: u32) -> Vec<u32> {
        let Ok(html) = self.book_dir(book).and_then(|d| crate::book::section_html(&d, section)) else { return Vec::new() };
        paragraph_starts_in(&html)
    }

    /// The voices cast in this book that act.
    fn acting_voices(&self, book: &str) -> HashSet<String> {
        if let Some(b) = self.clip_store(book).ok().and_then(|s| s.borrowed) {
            return b.acting.into_iter().collect();
        }
        lock(&self.store).get(&format!("drama/{book}/plan"), "acting").ok().flatten().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
    }

    fn section_clips(&self, book: &str, ds: &DramaSection) -> Result<Vec<ClipAsk>> {
        self.section_clips_with_engine(book, ds, None)
    }

    fn section_clips_with_engine(&self, book: &str, ds: &DramaSection, engine_override: Option<&str>) -> Result<Vec<ClipAsk>> {
        let b = self.book_drama(book)?;
        let borrowed = self.clip_store(book)?.borrowed;
        let plan: Option<CastingPlan> = match &borrowed {
            Some(x) => Some(x.plan.clone()),
            None => lock(&self.store).get(&format!("drama/{book}/plan"), "plan").ok().flatten().and_then(|v| serde_json::from_value(v).ok()),
        };
        let Some(plan) = plan else { return Ok(vec![]) };
        let user_voices: HashMap<String, String> = lock(&self.store).fields(&format!("drama/{book}/voices")).unwrap_or_default().into_iter().filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string()))).collect();
        let narrator = user_voices.get("_narrator").cloned().unwrap_or(plan.narrator.clone());
        let dialogue = user_voices.get("_dialogue").cloned().unwrap_or(plan.dialogue.clone());
        let of = |id: u32| -> Option<String> {
            let c = b.cast.get(id)?;
            c.names.iter().find_map(|n| user_voices.get(n).cloned()).or_else(|| plan.voices.iter().find(|v| v.character == id).map(|v| v.voice.clone()))
        };
        let v = Voices { narrator: &narrator, dialogue: &dialogue, of: &of };
        let mode = lock(&self.store).get(&entity(book), "drama_mode").ok().flatten().and_then(|v| v.as_bool()).unwrap_or(false);
        let engine = match engine_override {
            Some(e) => e.to_string(),
            None => borrowed.as_ref().map(|x| x.engine.clone()).unwrap_or_else(|| lock(&self.drama).caps.engine.clone()),
        };
        let store = self.clip_store(book)?;
        let cards = self.all_known_cards(Some(book));
        let key_of = |voice: &str, text: &str, act: &DramaAct| -> String {
            match cards.get(voice) {
                Some(card) => super::queue::clip_key(&super::card::voice_key_of(card, act, &engine), text, act, &engine),
                None => super::queue::clip_key(voice, text, act, &engine),
            }
        };
        let readings = b.readings.clone();
        let reading = |t: &str| {
            let mut s = t.to_string();
            for r in &readings {
                if s.contains(&r.word) && !s.contains(&format!("{{{}|", r.word)) {
                    s = s.replace(&r.word, &format!("{{{}|{}}}", r.word, r.reading));
                }
            }
            s
        };
        let mut out = clips(ds.section, &ds.segments, &v, mode, &engine, &reading);
        for c in &mut out {
            if cards.contains_key(&c.voice) {
                c.key = key_of(&c.voice, &c.text, &c.act);
            }
        }
        // 带语气 off: the voices that act read flat (a voice that can't act reads flat anyway,
        // and keeps its clips).
        if !self.drama_sound(book.to_string()).map(|s| s.acting).unwrap_or(true) {
            let acting = self.acting_voices(book);
            for c in out.iter_mut().filter(|c| acting.contains(&c.voice) && c.act != DramaAct::flat()) {
                c.act = DramaAct::flat();
                c.key = key_of(&c.voice, &c.text, &c.act);
            }
        }
        // Clips that kept failing: made plain, or by the narrator.
        for c in &mut out {
            match store.fallback.get(&c.key) {
                Some(ClipVerdict::Plain) => {
                    c.act = DramaAct::flat();
                    c.key = key_of(&c.voice, &c.text, &c.act);
                }
                Some(ClipVerdict::Narrator) => {
                    c.voice = narrator.clone();
                    c.act = DramaAct::flat();
                    c.key = key_of(&c.voice, &c.text, &c.act);
                }
                _ => {}
            }
            c.attempt = store.attempts.get(&c.key).copied().unwrap_or(0);
        }
        Ok(out)
    }
}

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

/// See `paragraph_starts`.
fn paragraph_starts_in(html: &str) -> Vec<u32> {
    let mut out = Vec::new();
    let mut fresh = true;
    let mut rest = html;
    while let Some(k) = rest.find('<') {
        rest = &rest[k + 1..];
        let name: String = rest.chars().take_while(|c| c.is_ascii_alphanumeric()).collect::<String>().to_ascii_lowercase();
        if matches!(name.as_str(), "p" | "div" | "li" | "blockquote" | "h1" | "h2" | "h3" | "h4" | "h5" | "h6" | "br" | "tr" | "dd" | "dt") {
            fresh = true;
        }
        if name == "span"
            && let Some(end) = rest.find('>')
            && let Some(at) = rest[..end].find("data-s=\"") {
                let n: String = rest[at + 8..].chars().take_while(char::is_ascii_digit).collect();
                if let Ok(n) = n.parse::<u32>() {
                    if fresh && n > 0 {
                        out.push(n);
                    }
                    fresh = false;
                }
            }
    }
    out
}

#[cfg(test)]
mod paragraph_tests {
    #[test]
    fn each_block_starts_one() {
        let html = r#"<h2><span class="s" data-s="0">一</span></h2><p><span class="s" data-s="1">甲。</span><span class="s" data-s="2">乙。</span></p><p><span class="s" data-s="3">丙。</span></p>"#;
        assert_eq!(super::paragraph_starts_in(html), vec![1, 3]);
    }
}

/// A piece of a clip's text; `reading` (pinyin with tones) when the book said how to say it.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct SpokenPart {
    pub text: String,
    pub reading: Option<String>,
}

fn spoken_parts(text: &str) -> Vec<SpokenPart> {
    let mut out: Vec<SpokenPart> = Vec::new();
    let mut plain = String::new();
    let mut rest = text;
    while let Some(a) = rest.find('{') {
        let Some(b) = rest[a..].find('}').map(|b| a + b) else { break };
        let inner = &rest[a + 1..b];
        let Some((word, reading)) = inner.split_once('|') else {
            plain.push_str(&rest[..=b]);
            rest = &rest[b + 1..];
            continue;
        };
        plain.push_str(&rest[..a]);
        if !plain.is_empty() {
            out.push(SpokenPart { text: std::mem::take(&mut plain), reading: None });
        }
        out.push(SpokenPart { text: word.to_string(), reading: Some(reading.to_string()) });
        rest = &rest[b + 1..];
    }
    plain.push_str(rest);
    if !plain.is_empty() {
        out.push(SpokenPart { text: plain, reading: None });
    }
    out
}

#[cfg(test)]
mod spoken_tests {
    #[test]
    fn readings_come_apart() {
        let p = super::spoken_parts("我是{贾珍|jiǎ zhēn}，你{好");
        assert_eq!(p.len(), 3);
        assert_eq!(p[1].text, "贾珍");
        assert_eq!(p[1].reading.as_deref(), Some("jiǎ zhēn"));
        assert_eq!(p.iter().map(|x| x.text.as_str()).collect::<String>(), "我是贾珍，你{好");
    }
}

/// One sentence's stretch of a scene's timeline (see `drama_sentence_window`): from its start
/// to the next sentence's, the last one to the scene's end and the pause after it once the scene
/// is all made; only the items that sound in it (and in the second before, mixed and dropped).
/// A sound's key as a short id (a description can be anything).
fn sound_id(key: &str) -> String {
    blake3::hash(key.as_bytes()).to_hex()[..16].to_string()
}

pub fn window_of(tl: DramaTimeline, complete: bool, sentence: u32) -> Option<DramaWindow> {
    let times = super::timeline::sentence_times(&tl);
    let i = times.iter().position(|t| t.0 == sentence)?;
    let from = if i == 0 { 0.0 } else { times[i].1 };
    let to = match times.get(i + 1) {
        Some(next) => next.1,
        None if complete => tl.duration + super::timeline::gap::SCENE,
        None => times[i].2,
    };
    let lo = from - WINDOW_PREROLL;
    let items: Vec<TimelineItem> = tl
        .items
        .iter()
        .filter(|it| match it {
            TimelineItem::Voice { at, seconds, .. } => at + seconds > lo && *at < to,
            TimelineItem::Spot { at, .. } | TimelineItem::Music { at, .. } => *at < to && *at > lo - 10.0,
            TimelineItem::Bed { from: a, to: b, .. } => *a < to && *b > lo,
        })
        .cloned()
        .collect();
    let clips = items.iter().filter_map(|it| if let TimelineItem::Voice { clip, .. } = it { Some(clip.clone()) } else { None }).collect();
    Some(DramaWindow { timeline: DramaTimeline { items, ..tl }, from, to, clips })
}

#[cfg(test)]
mod window_tests {
    use super::*;
    use crate::drama::mixer::{DramaMixer, MixSource};
    use crate::drama::timeline::{TimelineFilter, TimelineItem};

    /// A scene of four sentences in three voices over the built-in room tone.
    fn scene() -> DramaTimeline {
        let mut items = Vec::new();
        let mut t = 0.0;
        for s in 0..4u32 {
            items.push(TimelineItem::Voice { clip: format!("c{s}"), at: t, seconds: 1.0, gain_db: 0.0, pan: [-0.25, 0.0, 0.25, 0.0][s as usize], reverb_send: 0.25, filter: TimelineFilter::None, sentence: s, start: 0, end: 5 });
            t += 1.0 + 0.3;
        }
        let duration = t - 0.3;
        items.push(TimelineItem::Bed { asset: "builtin:room".into(), from: 0.0, to: duration + 1.5, gain_db: -12.0, fade_in: 1.2, fade_out: 1.2 });
        DramaTimeline { section: 0, first: 0, last: 3, reverb: "room".into(), duration, items }
    }

    fn tone(f: f32) -> Vec<f32> {
        (0..48_000).map(|i| 0.3 * (2.0 * std::f32::consts::PI * f * i as f32 / 48_000.0).sin()).collect()
    }

    #[test]
    fn sentences_one_after_another_sound_as_the_scene_does() {
        let tl = scene();
        let sources = || (0..4).map(|s| MixSource { id: format!("c{s}"), samples: tone(220.0 + 110.0 * s as f32), stereo: false }).collect::<Vec<_>>();
        // The windows tile the scene with its closing pause, no gaps, no overlaps.
        let windows: Vec<DramaWindow> = (0..4).map(|s| window_of(tl.clone(), true, s).unwrap()).collect();
        assert_eq!(windows[0].from, 0.0);
        for w in windows.windows(2) {
            assert!((w[0].to - w[1].from).abs() < 1e-9);
        }
        assert!((windows[3].to - (tl.duration + 1.5)).abs() < 1e-9);
        // Played window by window, as the player does, against the scene mixed in one go.
        let mut pieces: Vec<f32> = Vec::new();
        for w in &windows {
            let mine: Vec<MixSource> = sources().into_iter().filter(|s| w.clips.contains(&s.id)).collect();
            pieces.extend(DramaMixer::new(w.timeline.clone(), mine).render_span(w.from, w.to));
        }
        let whole = DramaMixer::new(tl.clone(), sources()).render_span(0.0, tl.duration + 1.5);
        let n = pieces.len().min(whole.len());
        assert!((pieces.len() as i64 - whole.len() as i64).abs() <= 4, "{} vs {}", pieces.len(), whole.len());
        let diff = (0..n).map(|i| (pieces[i] - whole[i]).powi(2)).sum::<f32>() / n as f32;
        let power = whole.iter().map(|x| x * x).sum::<f32>() / n as f32;
        assert!(diff < power * 0.01, "window by window differs from the whole: {diff} against {power}");
        // The room tone is there between the lines.
        let gap = &whole[(1.1 * 48_000.0) as usize..(1.25 * 48_000.0) as usize];
        assert!(gap.iter().any(|x| x.abs() > 1e-4), "no room tone in the pause");
    }

    #[test]
    fn a_scene_not_all_made_stops_where_it_does() {
        let w = window_of(scene(), false, 3).unwrap();
        assert!((w.to - (3.0 * 1.3 + 1.0)).abs() < 1e-9);
        assert!(window_of(scene(), true, 9).is_none());
    }
}
