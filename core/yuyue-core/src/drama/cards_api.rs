//! Voice cards (`VoiceCard`), speaker prints, and ZipVoice live-layer DSP APIs on `YuyueCore`.

use std::collections::HashMap;
use std::path::PathBuf;

use serde_json::Value;

use super::casting::VoiceInfo;
use super::DramaAct;
use crate::api::{YuyueCore, lock};
use crate::error::{CoreError, Result};

impl YuyueCore {
    pub(super) fn prints_path(&self) -> PathBuf {
        self.books_dir.parent().map(|p| p.join("voice-prints.json")).unwrap_or_else(|| self.books_dir.join("voice-prints.json"))
    }

    pub(super) fn clones_dir(&self) -> PathBuf {
        let d = self.books_dir.parent().map(|p| p.join("clones")).unwrap_or_else(|| self.books_dir.join("clones"));
        let _ = std::fs::create_dir_all(&d);
        d
    }

    pub(super) fn book_cards_dir(&self, book: &str) -> Result<PathBuf> {
        let d = self.drama_dir(book)?.join("cards");
        std::fs::create_dir_all(&d)?;
        Ok(d)
    }

    pub(super) fn materialize_book_cards(&self, book: &str) -> Result<Vec<super::card::VoiceCard>> {
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

    pub(super) fn materialize_cloned_cards(&self) -> Vec<super::card::VoiceCard> {
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

    pub(super) fn all_known_cards(&self, book: Option<&str>) -> HashMap<String, super::card::VoiceCard> {
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
}

#[uniffi::export]
impl YuyueCore {
    // ── 声纹 (`VoicePrints`) ────────────────────────────────────────────────

    /// 声纹: a model's input (80-band filter banks at 16 kHz) for `samples` at `rate`.
    pub fn speaker_features(&self, samples: Vec<f32>, rate: u32) -> super::speaker::SpeakerFeatures {
        super::speaker::features(&samples, rate)
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

    /// 回听: how much a clip sounds like its voice (cosine to the voice's print); 1 when there's
    /// no print or no clip print to go by.
    pub fn speaker_similarity(&self, voice: String, embedding: Vec<f32>) -> f32 {
        let v = super::speaker::VoicePrints::read(&self.prints_path());
        match v.voices.get(&voice) {
            Some(r) if !embedding.is_empty() && r.len() == embedding.len() => super::casting::alike(r, &embedding),
            _ => 1.0,
        }
    }

    // ── 声音卡 (`VoiceCard`) ─────────────────────────────

    /// Maps fine-grained director notes onto the 7 reference clip styles.
    pub fn card_style_for(&self, act: DramaAct) -> super::card::CardStyle {
        super::card::style_for(&act)
    }

    /// Speaking instruction for generating a reference clip of `style`.
    pub fn card_style_instruction(&self, style: super::card::CardStyle, chinese: bool) -> String {
        style.instruction(chinese).to_string()
    }

    /// Canonical sentence read when recording or generating a reference clip.
    pub fn card_script(&self, lang: String, style: super::card::CardStyle) -> String {
        super::card::script_for(&lang, style).to_string()
    }

    /// Built-in cards from the registered `voices-1/` pack.
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

    /// Packs all designed cards of `book_id` (with base64 clip audio) as JSON for LAN `/card`.
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
    /// wasn't recorded.
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

    /// Checks a user's recorded cloning sample against the duration, level, clipping, noise & CER gates.
    pub fn card_check(&self, samples: Vec<f32>, rate: u32, text: String, heard: Option<String>) -> super::card::CardCheck {
        super::card::check_sample(&samples, rate, &text, heard.as_deref())
    }

    /// Builds a `VoiceInfo` from a `VoiceCard` for `drama_cast_voices`.
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
    /// loudness to −20 LUFS.
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

    // ── 即时层纯计算前端与声码器 DSP (`ZipVoice` + `Vocos 24 kHz`) ──

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

    /// lightweight quality gate (duration ±50%, head/tail/internal silence, < 0.1% clipping).
    pub fn live_voice_check(&self, samples: Vec<f32>, rate: u32, text: String, lang: String, expected_rate: f32) -> bool {
        super::zipvoice::light_check(&samples, rate, &text, &lang, expected_rate)
    }

    // ── 跨端轻量离线神经旁白 (`Kokoro-82M-v1.1-zh Int8`, Apache-2.0) ──

    /// Registers the `kokoro-v1_1-1/` directory (`tokens.txt` + `lexicon-*.txt`).
    pub fn kokoro_set_dir(&self, dir: String) -> u32 {
        super::kokoro::set_model_dir(std::path::Path::new(&dir)) as u32
    }

    /// Curated Mandarin & English narrator catalog for `Kokoro-82M-v1.1-zh`.
    pub fn kokoro_voices(&self) -> Vec<super::kokoro::KokoroSpeaker> {
        super::kokoro::builtin_speakers()
    }

    /// Splits a sentence into natural breath clauses within Kokoro's 510-token style window.
    pub fn kokoro_clauses(&self, text: String, lang: String) -> Vec<String> {
        super::kokoro::split_clauses(&text, &lang)
    }

    /// Tokenizes a clause into Kokoro inner phoneme IDs (caller wraps `[0, .., 0]`).
    pub fn kokoro_tokens(&self, text: String, lang: String) -> Vec<i64> {
        super::kokoro::tokenize(&text, &lang)
    }

    /// Trims leading/trailing silence and normalizes Kokoro PCM to −20 LUFS.
    pub fn kokoro_postprocess(&self, samples: Vec<f32>, rate: u32) -> Vec<f32> {
        super::kokoro::postprocess_pcm(&samples, rate)
    }
}
