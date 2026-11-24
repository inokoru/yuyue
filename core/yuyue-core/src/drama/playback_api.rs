//! Drama voice casting, playback windows, clip manifests, M4B export, and playback helpers.

use std::collections::{HashMap, HashSet};

use serde_json::{Value, json};

use super::api::{ActTags, Borrowed, ClipManifest, DramaMade, DramaSection, DramaSound, DramaWindow, WINDOW_PREROLL, entity, lang_of};
use super::attribute::lines_of;
use super::casting::{CastingPlan, Together, VoiceInfo, cast as cast_voices, together_key};
use super::queue::{ClipAsk, ClipCheck, ClipVerdict, Voices, clips};
use super::sound::{SoundAsset, SoundEvent};
use super::timeline::{DramaTimeline, MadeClip, TimelineItem, lay_out};
use super::{DramaAct, DramaCharacter, DramaSegment, DramaSpeaker, SegKind};
use crate::api::{YuyueCore, lock};
use crate::error::{CoreError, Result};

/// One stretch inside a sentence for manual role playback (narration vs character speech).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct RoleSegment {
    /// Cast `shown` name when this stretch belongs to a named character, else `None` (narrator).
    pub role: Option<String>,
    pub text: String,
}

/// Auto-cast voice assignment for a character (keyed by `CastMember.name`).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct CastRoleVoice {
    pub name: String,
    pub voice: String,
    /// True when casting fell back to the narrator voice (e.g. gender unknown without LLM).
    pub is_dialogue_fallback: bool,
}

/// A piece of a clip's text; `reading` (pinyin with tones) when the book said how to say it.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct SpokenPart {
    pub text: String,
    pub reading: Option<String>,
}

pub(super) fn spoken_parts(text: &str) -> Vec<SpokenPart> {
    super::phoneme::split_spoken_parts(text)
        .into_iter()
        .map(|(text, reading)| SpokenPart { text, reading })
        .collect()
}

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
            && let Some(at) = rest[..end].find("data-s=\"")
        {
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

/// One sentence's stretch of a scene's timeline (see `drama_sentence_window`): from its start
/// to the next sentence's, the last one to the scene's end and the pause after it once the scene
/// is all made; only the items that sound in it (and in the second before, mixed and dropped).
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

impl YuyueCore {
    /// The latest saved casting plan for `book_id` (borrowed or local), if cast.
    pub(crate) fn drama_plan(&self, book_id: &str) -> Option<CastingPlan> {
        if let Some(b) = self.clip_store(book_id).ok().and_then(|s| s.borrowed) {
            return Some(b.plan);
        }
        lock(&self.store).get(&format!("drama/{book_id}/plan"), "plan").ok().flatten().and_then(|v| serde_json::from_value(v).ok())
    }

    /// Each scene of a section laid out with the clips made so far, and whether all its clips
    /// are made. The library is the app's (downloaded) sounds and the built-in ones, as the
    /// book's 环境音 / 音效 allow.
    pub(super) fn scene_layouts(&self, book_id: &str, section: u32, library: Vec<SoundAsset>) -> Result<Vec<(DramaTimeline, bool)>> {
        let ds = self.drama_section(book_id.to_string(), section)?;
        let asks = self.section_clips(book_id, &ds)?;
        let store = self.clip_store(book_id)?;
        let made = |k: &str| store.made.get(k).cloned();
        let sound = self.drama_sound(book_id.to_string())?;
        let made_sounds = self.register_sounds(book_id, &store);
        let library: Vec<SoundAsset> = library
            .into_iter()
            .chain(super::library::assets())
            .chain(super::builtin::assets())
            .chain(made_sounds)
            .filter(|a| if a.loops || a.key == super::builtin::TRANSITION { sound.beds } else { sound.spots })
            .collect();
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
    pub(super) fn paragraph_starts(&self, book: &str, section: u32) -> Vec<u32> {
        let Ok(html) = self.book_dir(book).and_then(|d| crate::book::section_html(&d, section)) else { return Vec::new() };
        paragraph_starts_in(&html)
    }

    /// The voices cast in this book that act.
    pub(super) fn acting_voices(&self, book: &str) -> HashSet<String> {
        if let Some(b) = self.clip_store(book).ok().and_then(|s| s.borrowed) {
            return b.acting.into_iter().collect();
        }
        lock(&self.store).get(&format!("drama/{book}/plan"), "acting").ok().flatten().and_then(|v| serde_json::from_value(v).ok()).unwrap_or_default()
    }

    pub(super) fn section_clips(&self, book: &str, ds: &DramaSection) -> Result<Vec<ClipAsk>> {
        self.section_clips_with_engine(book, ds, None)
    }

    pub(super) fn section_clips_with_engine(&self, book: &str, ds: &DramaSection, engine_override: Option<&str>) -> Result<Vec<ClipAsk>> {
        let b = self.book_drama(book)?;
        let borrowed = self.clip_store(book)?.borrowed;
        let plan: Option<CastingPlan> = match &borrowed {
            Some(x) => Some(x.plan.clone()),
            None => lock(&self.store).get(&format!("drama/{book}/plan"), "plan").ok().flatten().and_then(|v| serde_json::from_value(v).ok()),
        };
        let Some(plan) = plan else { return Ok(vec![]) };
        let user_voices: HashMap<String, String> = self
            .book_roles(book.to_string())
            .unwrap_or_default()
            .into_iter()
            .filter_map(|r| r.voice.voice.map(|v| (r.name, v)))
            .collect();
        let designed_voices: HashMap<String, String> = lock(&self.store)
            .fields(&format!("drama/{book}/designed"))
            .unwrap_or_default()
            .into_iter()
            .filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string())))
            .collect();
        let narrator = user_voices.get("_narrator").cloned().unwrap_or(plan.narrator.clone());
        let dialogue = user_voices.get("_dialogue").cloned().unwrap_or(plan.dialogue.clone());
        let of = |id: u32| -> Option<String> {
            let c = b.cast.get(id)?;
            c.names
                .iter()
                .find_map(|n| user_voices.get(n).cloned())
                .or_else(|| c.names.iter().find_map(|n| designed_voices.get(n).cloned()))
                .or_else(|| plan.voices.iter().find(|v| v.character == id).map(|v| v.voice.clone()))
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

#[uniffi::export]
impl YuyueCore {
    pub fn drama_cast_voices(&self, book_id: String, voices: Vec<VoiceInfo>, narrator: String) -> Result<CastingPlan> {
        let b = self.book_drama(&book_id)?;
        // 声纹: the prints this device has made, for voices the app didn't measure itself.
        let prints = super::speaker::VoicePrints::read(&self.prints_path());
        let voices: Vec<VoiceInfo> = voices
            .into_iter()
            .map(|mut v| {
                if v.embedding.is_empty()
                    && let Some(e) = prints.voices.get(&v.id)
                {
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
                        && all.iter().any(|l| l.utterance == x.utterance && l.first_sentence >= sc.first && l.first_sentence <= sc.last)
                    {
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
        let designed: HashMap<String, String> = lock(&self.store)
            .fields(&format!("drama/{book_id}/designed"))
            .unwrap_or_default()
            .into_iter()
            .filter_map(|(k, v)| v.as_str().map(|s| (k, s.to_string())))
            .collect();
        let stored: HashMap<String, String> = self
            .book_roles(book_id.clone())
            .unwrap_or_default()
            .into_iter()
            .filter_map(|r| r.voice.voice.map(|v| (r.name, v)))
            .collect();
        let mut fixed: HashMap<u32, String> = designed
            .iter()
            .filter(|(k, _)| !k.starts_with('_'))
            .filter_map(|(k, v)| b.cast.resolve(k).map(|id| (id, v.clone())))
            .collect();
        for (k, v) in stored.iter().filter(|(k, _)| !k.starts_with('_')) {
            if let Some(id) = b.cast.resolve(k) {
                fixed.insert(id, v.clone());
            }
        }
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

    /// 广播剧开关 (per book, synced): plays the book with multi-voice drama pipeline on demand.
    pub fn drama_enabled(&self, book_id: String) -> bool {
        lock(&self.store).get(&entity(&book_id), "drama_enabled").ok().flatten().and_then(|v| v.as_bool()).unwrap_or(false)
    }

    pub fn drama_set_enabled(&self, book_id: String, enabled: bool) -> Result<()> {
        lock(&self.store).set(&entity(&book_id), "drama_enabled", json!(enabled))
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

    /// Live-layer sentence clips: the clips needed to render `sentence` right now (respecting
    /// `drama_mode`, `drama_sound.acting`, book readings, user voice overrides and casting plan).
    pub fn drama_live_sentence_asks(&self, book_id: String, section: u32, sentence: u32) -> Result<Vec<ClipAsk>> {
        let ds = self.drama_section(book_id.clone(), section)?;
        let asks = self.section_clips(&book_id, &ds)?;
        Ok(asks.into_iter().filter(|c| c.sentence == sentence).collect())
    }

    /// Look up the character's role voice settings (rate/pitch) for a clip ask, if any.
    pub fn drama_clip_tuning(&self, book_id: String, section: u32, sentence: u32, start: u32) -> Option<crate::roles::RoleVoice> {
        let ds = self.drama_section(book_id.clone(), section).ok()?;
        let b = self.book_drama(&book_id).ok()?;
        let seg = ds.segments.iter().find(|s| s.sentence == sentence && s.start == start)?;
        if seg.kind == SegKind::Narration {
            return None;
        }
        let DramaSpeaker::Char { id } = seg.speaker else { return None };
        let c = b.cast.get(id).filter(|c| !c.narrator)?;
        let roles = self.book_roles(book_id).ok()?;
        c.names.iter().find_map(|n| crate::roles::role_for(&roles, n).cloned())
    }

    /// Live-layer sentence window: builds a single-sentence `DramaWindow` from the just-rendered
    /// `clips` (with their measured durations) so live playback runs through `DramaMixer` and gets
    /// room tone, spot SFX, transitions, reverb, pan and distance filters just like Studio playback.
    pub fn drama_live_sentence_window(&self, book_id: String, section: u32, sentence: u32, clips: Vec<MadeClip>) -> Result<Option<DramaWindow>> {
        if clips.is_empty() {
            return Ok(None);
        }
        let ds = self.drama_section(book_id.clone(), section)?;
        let asks = self.section_clips(&book_id, &ds)?;
        let Some((scene_idx, sc)) = ds.scenes.iter().enumerate().find(|(_, s)| s.first <= sentence && sentence <= s.last) else {
            return Ok(None);
        };
        let mine_asks: Vec<ClipAsk> = asks.into_iter().filter(|c| c.sentence == sentence).collect();
        if mine_asks.is_empty() {
            return Ok(None);
        }
        let by_key: HashMap<String, MadeClip> = clips.into_iter().map(|c| (c.key.clone(), c)).collect();
        if mine_asks.iter().any(|a| !by_key.contains_key(&a.key)) {
            return Ok(None);
        }
        let store = self.clip_store(&book_id)?;
        let sound = self.drama_sound(book_id.clone())?;
        let made_sounds = self.register_sounds(&book_id, &store);
        let library: Vec<SoundAsset> = super::library::assets()
            .into_iter()
            .chain(super::builtin::assets())
            .chain(made_sounds)
            .filter(|a| if a.loops || a.key == super::builtin::TRANSITION { sound.beds } else { sound.spots })
            .collect();
        let transitions: Vec<&SoundAsset> = library.iter().filter(|a| a.key == super::builtin::TRANSITION).collect();
        let transition = if sentence == sc.first && scene_idx > 0 && !transitions.is_empty() {
            let p = &ds.scenes[scene_idx - 1];
            let moved = p.place != sc.place || p.place_kind != sc.place_kind || p.time != sc.time;
            moved.then(|| transitions[(scene_idx - 1) % transitions.len()])
        } else {
            None
        };
        let segs: Vec<DramaSegment> = ds.segments.iter().filter(|s| s.sentence == sentence).cloned().collect();
        let ev: Vec<SoundEvent> = ds.events.iter().filter(|e| e.sentence == sentence).cloned().collect();
        let paragraphs = self.paragraph_starts(&book_id, section);
        let made = |k: &str| by_key.get(k).cloned();
        let mut tl = lay_out(sc, &segs, &mine_asks, &made, &paragraphs, &ev, &library, transition);
        // Only apply the 1.2 s bed fade-in on the first sentence of a scene, and fade-out on the last,
        // so consecutive live-mixed sentences keep a continuous ambient bed without pumping.
        let is_first = sentence == sc.first;
        let is_last = sentence == sc.last;
        let tail_gap = if is_last {
            super::timeline::gap::SCENE
        } else if paragraphs.contains(&(sentence + 1)) {
            super::timeline::gap::PARAGRAPH
        } else {
            super::timeline::gap::SENTENCE
        };
        let to = tl.duration + tail_gap;
        for it in &mut tl.items {
            if let TimelineItem::Bed { to: b_to, fade_in, fade_out, .. } = it {
                *b_to = to + 0.5;
                if !is_first {
                    *fade_in = 0.0;
                }
                if !is_last {
                    *fade_out = 0.0;
                }
            }
        }
        let clip_keys: Vec<String> = tl
            .items
            .iter()
            .filter_map(|it| if let TimelineItem::Voice { clip, .. } = it { Some(clip.clone()) } else { None })
            .collect();
        Ok(Some(DramaWindow { timeline: tl, from: 0.0, to, clips: clip_keys }))
    }

    /// Manual-role sentence segments (used when radio drama is off, but the user assigned voices,
    /// pace or pitch to individual roles): splits a sentence cleanly so narration tags keep the
    /// narrator's voice and multiple speakers in one sentence never overwrite each other.
    pub fn role_sentence_segments(&self, book_id: String, section: u32, sentence: u32) -> Result<Vec<RoleSegment>> {
        let ds = self.drama_section(book_id.clone(), section)?;
        let b = self.book_drama(&book_id)?;
        let mut out: Vec<RoleSegment> = Vec::new();
        for s in ds.segments.iter().filter(|s| s.sentence == sentence) {
            let role = if s.kind == SegKind::Narration {
                None
            } else if let DramaSpeaker::Char { id } = s.speaker {
                b.cast.get(id).filter(|c| !c.narrator).map(|c| c.shown.clone())
            } else {
                None
            };
            if let Some(last) = out.last_mut()
                && last.role == role
            {
                last.text.push_str(&s.text);
                continue;
            }
            out.push(RoleSegment { role, text: s.text.clone() });
        }
        out.retain(|s| s.text.chars().any(char::is_alphanumeric));
        Ok(out)
    }

    /// Returns the role's effective cast voice id from the saved `CastingPlan` (matching by shown
    /// name or any alias), or `None` if no plan exists or the character maps back to narrator.
    pub fn drama_role_voices(&self, book_id: String) -> Result<Vec<CastRoleVoice>> {
        let Some(plan) = self.drama_plan(&book_id) else {
            return Ok(Vec::new());
        };
        let b = self.book_drama(&book_id)?;
        let mut out = Vec::new();
        for cv in &plan.voices {
            if let Some(c) = b.cast.get(cv.character).filter(|c| !c.narrator) {
                out.push(CastRoleVoice { name: c.shown.clone(), voice: cv.voice.clone(), is_dialogue_fallback: cv.voice == plan.dialogue && cv.voice == plan.narrator });
            }
        }
        Ok(out)
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

    /// 导出成有声书: chapter marks, book title, author and cover written into the M4B.
    pub fn drama_finish_m4b(&self, book_id: String, path: String, marks: Vec<crate::audio::AudioMark>) -> Result<()> {
        let meta = self.summary(&book_id).ok().map(|s| crate::m4b::Meta {
            title: Some(s.title),
            author: (!s.authors.is_empty()).then(|| s.authors.join(&crate::i18n::tr("、", ", "))),
            cover: s.cover_path.and_then(|p| std::fs::read(p).ok()),
        });
        crate::m4b::add_chapters_and_meta(std::path::Path::new(&path), &marks, meta.as_ref())
    }

    /// 声音素材库: the downloaded sounds' folder (its `library.json` and Ogg files). Scenes are
    /// laid out with them before the built-in ones. How many sounds it has (0: none, as before).
    pub fn drama_set_sound_library(&self, dir: String) -> u32 {
        super::library::set(std::path::Path::new(&dir)) as u32
    }

    /// Which layer (`Studio` vs `Live`) `section` plays from: `Studio` only when every clip of the
    /// section is already made under `studio_engine`, so voices never switch mid-chapter.
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
        for c in m.made.into_iter().filter(|c| have.contains(&c.key) && c.seconds.is_finite() && (0.0..=3600.0).contains(&c.seconds)) {
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
}

#[cfg(test)]
mod paragraph_tests {
    #[test]
    fn each_block_starts_one() {
        let html = r#"<h2><span class="s" data-s="0">一</span></h2><p><span class="s" data-s="1">甲。</span><span class="s" data-s="2">乙。</span></p><p><span class="s" data-s="3">丙。</span></p>"#;
        assert_eq!(super::paragraph_starts_in(html), vec![1, 3]);
    }
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

#[cfg(test)]
mod window_tests {
    use super::*;
    use crate::drama::CharacterGender;
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

    #[test]
    fn drama_enabled_clear_voice_role_segments_and_live_window() {
        let dir = std::env::temp_dir().join(format!("yuyue-drama-fix-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();

        // 1. drama_enabled toggle defaults to false and persists.
        assert!(!core.drama_enabled(book.id.clone()));
        core.drama_set_enabled(book.id.clone(), true).unwrap();
        assert!(core.drama_enabled(book.id.clone()));
        core.drama_set_enabled(book.id.clone(), false).unwrap();
        assert!(!core.drama_enabled(book.id.clone()));

        // Find a dialogue section with 寶玉 and 鳳姐.
        let n = core.manifest(&book.id).unwrap().sections.len() as u32;
        let sec = (0..n)
            .find(|&s| core.sentences(book.id.clone(), s).unwrap_or_default().iter().any(|x| x.contains("莫失莫忘")))
            .unwrap();

        // 2. role_sentence_segments splits narration tag from character speech inside one sentence.
        let ds = core.drama_section(book.id.clone(), sec).unwrap();
        let mixed_sentence = ds
            .segments
            .iter()
            .filter(|s| s.kind == SegKind::Speech && matches!(s.speaker, DramaSpeaker::Char { .. }))
            .map(|s| s.sentence)
            .find(|&sent| {
                ds.segments.iter().any(|x| x.sentence == sent && x.kind == SegKind::Narration && x.text.chars().any(char::is_alphanumeric))
            })
            .expect("expected a sentence with both narration tag and character speech");
        let rsegs = core.role_sentence_segments(book.id.clone(), sec, mixed_sentence).unwrap();
        assert!(rsegs.len() >= 2, "{rsegs:?}");
        assert!(rsegs.iter().any(|s| s.role.is_none()), "narration tag should keep role=None: {rsegs:?}");
        assert!(rsegs.iter().any(|s| s.role.is_some()), "speech should have character role: {rsegs:?}");

        // 3. set_book_role overrides casting and clearing it restores auto-casting.
        let voices = vec![
            VoiceInfo { id: "narr".into(), langs: vec!["zh".into()], gender: CharacterGender::M, age: crate::drama::CharacterAge::Middle, tags: vec![], embedding: vec![1.0, 0.0, 0.0], acts: true, narrator_ok: true, loudness_lufs: -20.0 },
            VoiceInfo { id: "f1".into(), langs: vec!["zh".into()], gender: CharacterGender::F, age: crate::drama::CharacterAge::Young, tags: vec![], embedding: vec![0.0, 1.0, 0.0], acts: true, narrator_ok: true, loudness_lufs: -20.0 },
            VoiceInfo { id: "m1".into(), langs: vec!["zh".into()], gender: CharacterGender::M, age: crate::drama::CharacterAge::Young, tags: vec![], embedding: vec![0.0, 0.0, 1.0], acts: true, narrator_ok: true, loudness_lufs: -20.0 },
        ];
        core.set_book_role(book.id.clone(), "寶玉".into(), crate::roles::RoleVoice { voice: Some("f1".into()), rate: 1.0, pitch: 0.0, right: false }).unwrap();
        let plan1 = core.drama_cast_voices(book.id.clone(), voices.clone(), "narr".into()).unwrap();
        let rv1 = core.drama_role_voices(book.id.clone()).unwrap();
        assert_eq!(rv1.iter().find(|r| r.name == "寶玉").map(|r| r.voice.as_str()), Some("f1"));
        assert_eq!(core.drama_plan(&book.id), Some(plan1));

        // Clearing via set_book_role(voice: None) restores auto-casting.
        core.set_book_role(book.id.clone(), "寶玉".into(), crate::roles::RoleVoice { voice: None, rate: 1.1, pitch: 0.0, right: false }).unwrap();
        let _plan2 = core.drama_cast_voices(book.id.clone(), voices.clone(), "narr".into()).unwrap();
        let rv2 = core.drama_role_voices(book.id.clone()).unwrap();
        assert_ne!(rv2.iter().find(|r| r.name == "寶玉").map(|r| r.voice.as_str()), Some("f1"));

        // 3b. Auto-designed drama voice stays in drama-only storage and never pollutes book_roles.
        let baoyu_id = core.book_drama(&book.id).unwrap().cast.resolve("寶玉").expect("baoyu character id");
        core.drama_submit(
            book.id.clone(),
            format!("design:{}:{baoyu_id}", book.id),
            crate::drama::api::DramaResult::Voice {
                voice: VoiceInfo {
                    id: "designed-baoyu".into(),
                    langs: vec!["zh".into()],
                    gender: CharacterGender::M,
                    age: crate::drama::CharacterAge::Teen,
                    tags: vec![],
                    embedding: vec![0.2, 0.3, 0.5],
                    acts: true,
                    narrator_ok: false,
                    loudness_lufs: -20.0,
                },
            },
        ).unwrap();
        assert_eq!(
            core.book_roles(book.id.clone()).unwrap().iter().find(|r| r.name == "寶玉").and_then(|r| r.voice.voice.as_deref()),
            None,
            "auto-designed drama voice must not write into manual book_roles"
        );
        let rv_designed = core.drama_role_voices(book.id.clone()).unwrap();
        assert_eq!(rv_designed.iter().find(|r| r.name == "寶玉").map(|r| r.voice.as_str()), Some("designed-baoyu"));
        let _plan3 = core.drama_cast_voices(book.id.clone(), voices.clone(), "narr".into()).unwrap();
        let rv_recast = core.drama_role_voices(book.id.clone()).unwrap();
        assert_eq!(rv_recast.iter().find(|r| r.name == "寶玉").map(|r| r.voice.as_str()), Some("designed-baoyu"));

        // 4. drama_live_sentence_asks + drama_live_sentence_window mix through DramaMixer.
        let asks = core.drama_live_sentence_asks(book.id.clone(), sec, mixed_sentence).unwrap();
        assert!(!asks.is_empty());
        let made: Vec<MadeClip> = asks.iter().map(|a| MadeClip { key: a.key.clone(), seconds: 0.8, char_times: vec![] }).collect();
        let win = core.drama_live_sentence_window(book.id.clone(), sec, mixed_sentence, made).unwrap().expect("live window");
        assert_eq!(win.from, 0.0);
        assert!(win.to > 0.8);
        assert!(!win.clips.is_empty());
        let sources: Vec<MixSource> = win.clips.iter().map(|k| MixSource { id: k.clone(), samples: tone(240.0)[..38_400].to_vec(), stereo: false }).collect();
        let rendered = DramaMixer::new(win.timeline, sources).render_span(win.from, win.to);
        assert!(!rendered.is_empty());

        // 5. drama_clip_tuning finds rate/pitch set on the character's RoleVoice.
        let speech_ask = asks.iter().find(|a| {
            ds.segments.iter().any(|s| s.sentence == a.sentence && s.start == a.start && s.kind == SegKind::Speech && matches!(s.speaker, DramaSpeaker::Char { .. }))
        });
        if let Some(a) = speech_ask {
            let sp = ds.segments.iter().find(|s| s.sentence == a.sentence && s.start == a.start).unwrap();
            if let DramaSpeaker::Char { id } = sp.speaker {
                let b = core.book_drama(&book.id).unwrap();
                if let Some(ch) = b.cast.get(id) {
                    core.set_book_role(book.id.clone(), ch.shown.clone(), crate::roles::RoleVoice { voice: None, rate: 1.2, pitch: -2.0, right: false }).unwrap();
                    let tuned = core.drama_clip_tuning(book.id.clone(), sec, a.sentence, a.start);
                    assert!(tuned.is_some());
                    assert!((tuned.as_ref().unwrap().rate - 1.2).abs() < 1e-6);
                    assert!((tuned.as_ref().unwrap().pitch + 2.0).abs() < 1e-6);
                }
            }
        }
        std::fs::remove_dir_all(dir).ok();
    }
}
