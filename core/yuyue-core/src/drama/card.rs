//! 声音卡 (`VoiceCard`): one representation for built-in voices, book-designed character voices,
//! and user-cloned voices — up to seven 6–10 s Ogg Vorbis reference clips (`neutral` required,
//! six acting styles optional) with their exact transcripts, metadata, and versioned cache keys.

use std::collections::BTreeMap;
use std::path::{Component, Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use base64::Engine;
use serde::{Deserialize, Serialize};

use super::casting::VoiceInfo;
use super::{ActEmotion, ActVolume, CharacterAge, CharacterGender, DramaAct};

/// Seven reference acting styles on a voice card (§2.1).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize, uniffi::Enum)]
#[serde(rename_all = "lowercase")]
pub enum CardStyle {
    Neutral,
    Joy,
    Anger,
    Sad,
    Tense,
    Soft,
    Shout,
}

impl CardStyle {
    pub const ALL: [CardStyle; 7] = [
        CardStyle::Neutral,
        CardStyle::Joy,
        CardStyle::Anger,
        CardStyle::Sad,
        CardStyle::Tense,
        CardStyle::Soft,
        CardStyle::Shout,
    ];

    pub fn as_str(self) -> &'static str {
        match self {
            CardStyle::Neutral => "neutral",
            CardStyle::Joy => "joy",
            CardStyle::Anger => "anger",
            CardStyle::Sad => "sad",
            CardStyle::Tense => "tense",
            CardStyle::Soft => "soft",
            CardStyle::Shout => "shout",
        }
    }

    /// Speaking instruction given to a studio voice-design / acting model (§2.1).
    pub fn instruction(self, chinese: bool) -> &'static str {
        if chinese {
            match self {
                CardStyle::Neutral => "平稳自然地说",
                CardStyle::Joy => "高兴地、带着笑意说",
                CardStyle::Anger => "生气地、语气很重地说",
                CardStyle::Sad => "难过地、声音发沉地说",
                CardStyle::Tense => "紧张地、急促地说",
                CardStyle::Soft => "压低声音、轻轻地说",
                CardStyle::Shout => "大声喊出来",
            }
        } else {
            match self {
                CardStyle::Neutral => "Speak calmly and naturally",
                CardStyle::Joy => "Speak happily, with a warm smile in your voice",
                CardStyle::Anger => "Speak angrily, with a heavy, sharp tone",
                CardStyle::Sad => "Speak sadly, with a low and heavy voice",
                CardStyle::Tense => "Speak tensely and hurriedly",
                CardStyle::Soft => "Lower your voice and speak very gently",
                CardStyle::Shout => "Call out loudly",
            }
        }
    }
}

/// Maps fine-grained director notes (`DramaAct`) onto the 7 card styles (§2.2, top-down).
pub fn style_for(act: &DramaAct) -> CardStyle {
    if act.volume == ActVolume::Shout {
        return CardStyle::Shout;
    }
    if act.volume == ActVolume::Whisper {
        return CardStyle::Soft;
    }
    if act.level <= 1 {
        return if act.volume == ActVolume::Low { CardStyle::Soft } else { CardStyle::Neutral };
    }
    match act.emotion {
        ActEmotion::Neutral => CardStyle::Neutral,
        ActEmotion::Joy => CardStyle::Joy,
        ActEmotion::Anger | ActEmotion::Disgust => CardStyle::Anger,
        ActEmotion::Sadness | ActEmotion::Weary => CardStyle::Sad,
        ActEmotion::Fear | ActEmotion::Anxious | ActEmotion::Surprise => CardStyle::Tense,
        ActEmotion::Tender => CardStyle::Soft,
        // Small zero-shot models can't imitate a sneer cleanly; forcing one breaks immersion.
        ActEmotion::Mocking => CardStyle::Neutral,
    }
}

/// Prompt sentence (~35 Chinese chars / ~25 English words) read when recording or generating a
/// reference clip for `style` (§4.2 & §6.1).
pub fn script_for(lang: &str, style: CardStyle) -> &'static str {
    if lang.to_ascii_lowercase().starts_with("en") {
        match style {
            CardStyle::Neutral => "The morning rain had just stopped, so we walked slowly along the quiet river bank all the way home.",
            CardStyle::Joy => "Look who finally made it back today! Sit down by the window and tell me everything that happened!",
            CardStyle::Anger => "You knew from the very beginning what this meant, and yet you hid the truth from every one of us!",
            CardStyle::Sad => "The house stood empty when I returned at dusk, and nobody answered when I called out your name.",
            CardStyle::Tense => "Listen closely—someone is coming up the stairs right now, so turn out the lamp and stay behind me!",
            CardStyle::Soft => "Keep your voice down, everyone else is already asleep; rest a little while and I will watch the door.",
            CardStyle::Shout => "Stop right there! Drop the lantern and get away from the bridge before it gives way!",
        }
    } else {
        match style {
            CardStyle::Neutral => "今天早上下了一场小雨，我们沿着河边慢慢走回家，一路上聊了许多往事。",
            CardStyle::Joy => "真是没想到能在这儿遇上你！快进屋坐下喝杯热茶，好好跟我说说最近的新鲜事！",
            CardStyle::Anger => "这件事你明明从头到尾都清楚，为什么偏偏瞒着所有人，到现在还不肯说实话！",
            CardStyle::Sad => "院子里的灯早就灭了，我站在门口叫了好几声，屋里却再也没有人答应。",
            CardStyle::Tense => "别出声，外头好像有人踩着碎石走过来了，快把灯吹灭，先躲到屏风后面去！",
            CardStyle::Soft => "夜深了，屋里的人都睡熟了，你把衣裳披好，咱们轻手轻脚地从侧门出去吧。",
            CardStyle::Shout => "快停下！前头的木桥已经断了，再往前走一步就要掉下去了，快退回来！",
        }
    }
}

/// Where a voice card came from (§2.3).
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
#[serde(tag = "kind", rename_all = "lowercase")]
pub enum CardOrigin {
    Builtin,
    Designed { book: String, character: u32 },
    Cloned,
}

/// One reference audio clip inside a `VoiceCard`.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct CardClip {
    pub style: CardStyle,
    /// Relative path inside the card's folder, e.g. `"neutral.ogg"`.
    pub file: String,
    /// Exact words spoken in `file`.
    pub text: String,
    /// Decoded length in seconds (set and verified by the core).
    pub seconds: f32,
}

/// A voice card (§2.3 & §4.2).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct VoiceCard {
    pub id: String,
    pub name: String,
    pub gender: CharacterGender,
    pub age: CharacterAge,
    #[serde(default)]
    pub tags: Vec<String>,
    pub lang: String,
    pub origin: CardOrigin,
    pub clips: Vec<CardClip>,
    #[serde(default)]
    pub description: String,
    #[serde(default = "default_version")]
    pub version: u32,
    /// Built-in default narrator candidate (`voices.json`, §4.1).
    #[serde(default)]
    pub narrator: bool,
    /// Average speaking rate in characters (or words) per second (§3.3).
    #[serde(default)]
    pub rate: f32,
    /// Reference `neutral` speaker embedding (§4.2).
    #[serde(default)]
    pub embedding: Vec<f32>,
    /// For `Cloned` cards: whether the user opted into cross-device sync (§2.4).
    #[serde(default)]
    pub sync: bool,
}

fn default_version() -> u32 {
    1
}

/// Which synthesis layer a chapter plays from (§3.1).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum DramaLayer {
    Studio,
    Live,
}

/// Why a recorded reference sample failed `card_check` (§6.2).
#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum CardFault {
    TooShort,
    TooLong,
    TooQuiet,
    TooLoud,
    Clipped,
    Noisy,
    WrongWords,
}

impl CardFault {
    pub fn message(self) -> String {
        match self {
            CardFault::TooShort => crate::i18n::tr("太短了，再多念一点", "Too short — read a little more"),
            CardFault::TooLong => crate::i18n::tr("太长了，念到提示的话就停", "Too long — stop at the end of the prompt"),
            CardFault::TooQuiet => crate::i18n::tr("声音太小，离近一点", "Too quiet — move closer"),
            CardFault::TooLoud => crate::i18n::tr("声音太大，离远一点", "Too loud — move back a little"),
            CardFault::Clipped => crate::i18n::tr("声音破了，离远一点", "Audio clipped — move back a little"),
            CardFault::Noisy => crate::i18n::tr("周围有点吵，换个安静的地方", "A bit noisy here — try a quieter spot"),
            CardFault::WrongWords => crate::i18n::tr("好像没照着念，再试一次", "Didn't match the prompt — try again"),
        }
    }
}

/// Result of checking a recorded cloning sample (§6.2).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct CardCheck {
    pub ok: bool,
    pub seconds: f32,
    pub speech_db: f32,
    pub snr_db: f32,
    pub clipped_ratio: f32,
    pub cer: Option<f32>,
    pub faults: Vec<CardFault>,
    pub messages: Vec<String>,
}

/// Checks a recorded reference sample against §6.2's five gates.
pub fn check_sample(samples: &[f32], rate: u32, text: &str, heard: Option<&str>) -> CardCheck {
    if samples.is_empty() || rate == 0 {
        return CardCheck {
            ok: false,
            seconds: 0.0,
            speech_db: -96.0,
            snr_db: 0.0,
            clipped_ratio: 0.0,
            cer: None,
            faults: vec![CardFault::TooShort],
            messages: vec![CardFault::TooShort.message()],
        };
    }
    let seconds = samples.len() as f32 / rate as f32;
    let clipped = samples.iter().filter(|&&x| x.abs() >= 0.999).count() as f32 / samples.len() as f32;
    let hop = ((rate as usize) / 20).max(1); // 50 ms frames
    let mut rms: Vec<f32> = samples
        .chunks(hop)
        .filter(|w| w.len() * 2 >= hop)
        .map(|w| (w.iter().map(|&x| x * x).sum::<f32>() / w.len() as f32).sqrt())
        .collect();
    if rms.is_empty() {
        let r = (samples.iter().map(|&x| x * x).sum::<f32>() / samples.len() as f32).sqrt();
        rms.push(r);
    }
    rms.sort_by(|a, b| a.total_cmp(b));
    let at = |q: f32| rms[((rms.len() as f32 - 1.0) * q).round().clamp(0.0, (rms.len() - 1) as f32) as usize];
    let room = at(0.10);
    let speech = at(0.90);
    let speech_db = 20.0 * (speech.max(1e-5)).log10();
    let room_db = 20.0 * (room.max(1e-5)).log10();
    let snr_db = (speech_db - room_db).max(0.0);

    let mut faults = Vec::new();
    if seconds < 6.0 {
        faults.push(CardFault::TooShort);
    } else if seconds > 15.0 {
        faults.push(CardFault::TooLong);
    }
    if clipped >= 0.001 {
        faults.push(CardFault::Clipped);
    }
    if speech_db < -35.0 {
        faults.push(CardFault::TooQuiet);
    } else if speech_db > -8.0 {
        faults.push(CardFault::TooLoud);
    }
    if speech_db >= -35.0 && snr_db < 20.0 {
        faults.push(CardFault::Noisy);
    }
    let cer = heard.map(str::trim).filter(|h| !h.is_empty()).map(|h| {
        let chinese = text.chars().any(|c| ('\u{4e00}'..='\u{9fff}').contains(&c));
        phonetic_cer(text, h, chinese)
    });
    if let Some(c) = cer
        && c > 0.15
    {
        faults.push(CardFault::WrongWords);
    }
    let messages = faults.iter().map(|f| f.message()).collect();
    CardCheck { ok: faults.is_empty(), seconds, speech_db, snr_db, clipped_ratio: clipped, cer, faults, messages }
}

fn phonetic_cer(text: &str, heard: &str, chinese: bool) -> f32 {
    use crate::audiobook::phon::{phon, same};
    let want = phon(text, chinese);
    let got = phon(heard, chinese);
    let (n, m) = (want.len(), got.len());
    if n == 0 {
        return 0.0;
    }
    let mut prev: Vec<usize> = (0..=m).collect();
    for i in 1..=n {
        let mut cur = vec![i; m + 1];
        for j in 1..=m {
            let sub = prev[j - 1] + usize::from(!same(&want[i - 1], &got[j - 1]));
            cur[j] = sub.min(prev[j] + 1).min(cur[j - 1] + 1);
        }
        prev = cur;
    }
    prev[m] as f32 / n as f32
}

/// Versioned voice token folded into `clip_key` so re-recording or redesigning a card (`version`
/// bumped) invalidates every cached clip in both layers (§7.2).
pub fn voice_key_of(card: &VoiceCard, act: &DramaAct, engine: &str) -> String {
    if engine.starts_with("live-") {
        format!("{}@{}/{}", card.id, card.version, style_for(act).as_str())
    } else {
        format!("{}@{}", card.id, card.version)
    }
}

/// Converts a `VoiceCard` into a `VoiceInfo` for `drama_cast_voices` (§2.3).
pub fn to_voice_info(card: &VoiceCard, embedding: Vec<f32>, acts: bool) -> VoiceInfo {
    let emb = if !embedding.is_empty() { embedding } else { card.embedding.clone() };
    let narrator_ok = match card.origin {
        CardOrigin::Builtin => card.narrator || card.clips.iter().any(|c| c.style == CardStyle::Neutral),
        _ => true,
    };
    VoiceInfo {
        id: card.id.clone(),
        langs: vec![card.lang.clone()],
        gender: card.gender,
        age: card.age,
        tags: card.tags.clone(),
        embedding: emb,
        acts,
        narrator_ok,
        loudness_lufs: -20.0,
    }
}

/// Rejects any path that is empty, absolute, has Windows drive letters, or climbs out via `..`.
pub fn safe_rel_path(file: &str) -> bool {
    if file.is_empty() || file.contains('\\') || file.contains(':') || file.contains('\0') {
        return false;
    }
    let p = Path::new(file);
    if p.is_absolute() {
        return false;
    }
    let mut any = false;
    for c in p.components() {
        match c {
            Component::Normal(_) => any = true,
            _ => return false,
        }
    }
    any
}

pub fn safe_card_id(id: &str) -> bool {
    !id.is_empty()
        && id.len() <= 96
        && !id.contains("..")
        && id.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '.' | '-' | '_'))
}

/// Decodes an Ogg Vorbis (or 16-bit/float RIFF WAV) stream to mono `f32` samples at `target_rate`
/// (pass `0` to keep the file's native sample rate), along with `(samples, native_seconds)`.
pub fn decode_clip_mono(bytes: &[u8], target_rate: u32) -> Option<(Vec<f32>, f32)> {
    let (mono, rate) = decode_ogg_raw(bytes).or_else(|| decode_wav_raw(bytes))?;
    if mono.is_empty() || rate == 0 {
        return None;
    }
    let seconds = mono.len() as f32 / rate as f32;
    let out = if target_rate > 0 && target_rate != rate {
        resample_mono(&mono, rate, target_rate)
    } else {
        mono
    };
    Some((out, seconds))
}

fn decode_ogg_raw(bytes: &[u8]) -> Option<(Vec<f32>, u32)> {
    use lewton::inside_ogg::OggStreamReader;
    let mut r = OggStreamReader::new(std::io::Cursor::new(bytes)).ok()?;
    let ch = r.ident_hdr.audio_channels as usize;
    let rate = r.ident_hdr.audio_sample_rate;
    if ch == 0 || rate == 0 {
        return None;
    }
    let mut mono = Vec::new();
    while let Ok(Some(pkt)) = r.read_dec_packet_generic::<Vec<Vec<f32>>>() {
        match pkt.as_slice() {
            [m] => mono.extend_from_slice(m),
            [a, b, ..] => mono.extend(a.iter().zip(b).map(|(x, y)| 0.5 * (x + y))),
            [] => {}
        }
    }
    if let Some(end) = r.get_last_absgp()
        && end > 0
    {
        mono.truncate(end as usize);
    }
    (!mono.is_empty()).then_some((mono, rate))
}

fn decode_wav_raw(bytes: &[u8]) -> Option<(Vec<f32>, u32)> {
    if bytes.len() < 44 || &bytes[0..4] != b"RIFF" || &bytes[8..12] != b"WAVE" {
        return None;
    }
    let mut pos = 12usize;
    let mut fmt: Option<(u16, u16, u32, u16)> = None;
    let mut data_slice: Option<&[u8]> = None;
    while pos + 8 <= bytes.len() {
        let id = &bytes[pos..pos + 4];
        let sz = u32::from_le_bytes(bytes[pos + 4..pos + 8].try_into().ok()?) as usize;
        pos += 8;
        let end = (pos + sz).min(bytes.len());
        if id == b"fmt " && sz >= 16 {
            let tag = u16::from_le_bytes(bytes[pos..pos + 2].try_into().ok()?);
            let ch = u16::from_le_bytes(bytes[pos + 2..pos + 4].try_into().ok()?);
            let rate = u32::from_le_bytes(bytes[pos + 4..pos + 8].try_into().ok()?);
            let bits = u16::from_le_bytes(bytes[pos + 14..pos + 16].try_into().ok()?);
            fmt = Some((tag, ch, rate, bits));
        } else if id == b"data" {
            data_slice = Some(&bytes[pos..end]);
            break;
        }
        pos = end + (sz & 1);
    }
    let (tag, ch, rate, bits) = fmt?;
    let raw = data_slice?;
    let ch = ch as usize;
    if ch == 0 || rate == 0 {
        return None;
    }
    let mono: Vec<f32> = match (tag, bits) {
        (1, 16) => raw
            .chunks_exact(2 * ch)
            .map(|f| {
                let sum: f32 = (0..ch)
                    .map(|c| i16::from_le_bytes([f[2 * c], f[2 * c + 1]]) as f32 / 32768.0)
                    .sum();
                sum / ch as f32
            })
            .collect(),
        (3, 32) => raw
            .chunks_exact(4 * ch)
            .map(|f| {
                let sum: f32 = (0..ch)
                    .map(|c| f32::from_le_bytes([f[4 * c], f[4 * c + 1], f[4 * c + 2], f[4 * c + 3]]))
                    .sum();
                sum / ch as f32
            })
            .collect(),
        _ => return None,
    };
    (!mono.is_empty()).then_some((mono, rate))
}

pub fn resample_mono(x: &[f32], from: u32, to: u32) -> Vec<f32> {
    if x.is_empty() || from == 0 || to == 0 || from == to {
        return x.to_vec();
    }
    let step = from as f64 / to as f64;
    let n = (x.len() as f64 / step).floor() as usize;
    let mut out = Vec::with_capacity(n);
    let last = x.len() - 1;
    for i in 0..n {
        let p = i as f64 * step;
        let k = p.floor() as usize;
        let f = (p - k as f64) as f32;
        let a = x[k.min(last)];
        let b = x[(k + 1).min(last)];
        out.push(a + (b - a) * f);
    }
    out
}

/// Trims leading/trailing silence (leaving 0.10 s margin), resamples to `target_rate`, and
/// normalizes integrated speech loudness to −20 LUFS with a −1 dBFS safety ceiling (§2.3 & §3.2).
pub fn prepare_reference_pcm(samples: &[f32], rate: u32, target_rate: u32) -> Vec<f32> {
    let mut s = if target_rate > 0 && target_rate != rate {
        resample_mono(samples, rate, target_rate)
    } else {
        samples.to_vec()
    };
    let r = if target_rate > 0 { target_rate } else { rate };
    if s.is_empty() || r == 0 {
        return s;
    }
    // Trim head/tail below -45 dBFS, keeping 0.10 s pad on each end.
    let win = (r as usize / 50).max(1); // 20 ms
    let thresh = 10f32.powf(-45.0 / 20.0);
    let active: Vec<usize> = s
        .chunks(win)
        .enumerate()
        .filter_map(|(i, w)| {
            let rms = (w.iter().map(|&x| x * x).sum::<f32>() / w.len() as f32).sqrt();
            (rms >= thresh).then_some(i)
        })
        .collect();
    if let (Some(&first), Some(&last)) = (active.first(), active.last()) {
        let pad = (r as usize) / 10; // 0.10 s
        let a = (first * win).saturating_sub(pad);
        let b = ((last + 1) * win + pad).min(s.len());
        s = s[a..b].to_vec();
    }
    normalize_lufs_20(&mut s);
    s
}

/// Normalizes mono speech in-place to approximately −20 LUFS (gated RMS at −20 dBFS, ceiling −1 dBFS).
pub fn normalize_lufs_20(samples: &mut [f32]) {
    if samples.is_empty() {
        return;
    }
    let gate = 10f32.powf(-42.0 / 20.0);
    let mut sum = 0.0f64;
    let mut cnt = 0usize;
    for &x in samples.iter() {
        if x.abs() >= gate {
            sum += (x as f64) * (x as f64);
            cnt += 1;
        }
    }
    if cnt == 0 {
        return;
    }
    let rms = (sum / cnt as f64).sqrt() as f32;
    if rms < 1e-5 {
        return;
    }
    let target = 10f32.powf(-20.0 / 20.0); // 0.10
    let peak = samples.iter().map(|x| x.abs()).fold(0.0f32, f32::max);
    let ceiling = 10f32.powf(-1.0 / 20.0); // ~0.891
    let mut gain = (target / rms).clamp(0.1, 16.0);
    if peak * gain > ceiling {
        gain = ceiling / peak;
    }
    for x in samples.iter_mut() {
        *x *= gain;
    }
}

/// Encodes mono `f32` PCM (`[-1, 1]`) as a compact 16-bit RIFF WAV byte buffer at `rate` Hz.
pub fn encode_wav_16bit(samples: &[f32], rate: u32) -> Vec<u8> {
    let data_bytes = (samples.len() * 2) as u32;
    let mut buf = Vec::with_capacity(44 + data_bytes as usize);
    buf.extend_from_slice(b"RIFF");
    buf.extend_from_slice(&(36 + data_bytes).to_le_bytes());
    buf.extend_from_slice(b"WAVEfmt ");
    buf.extend_from_slice(&16u32.to_le_bytes());
    buf.extend_from_slice(&1u16.to_le_bytes()); // PCM
    buf.extend_from_slice(&1u16.to_le_bytes()); // mono
    buf.extend_from_slice(&rate.to_le_bytes());
    buf.extend_from_slice(&(rate * 2).to_le_bytes());
    buf.extend_from_slice(&2u16.to_le_bytes());
    buf.extend_from_slice(&16u16.to_le_bytes());
    buf.extend_from_slice(b"data");
    buf.extend_from_slice(&data_bytes.to_le_bytes());
    for &s in samples {
        let q = (s.clamp(-1.0, 1.0) * 32767.0).round() as i16;
        buf.extend_from_slice(&q.to_le_bytes());
    }
    buf
}

/// Decodes an Ogg Vorbis (or RIFF WAV) stream's true duration in seconds (`None` if invalid or empty).
pub fn ogg_seconds(bytes: &[u8]) -> Option<f32> {
    decode_clip_mono(bytes, 0).map(|(_, secs)| secs)
}

/// Validates a `VoiceCard` against the Ogg files in `base_dir`: rejects unsafe paths, overwrites
/// each clip's `seconds` with the decoded Ogg length, and rejects any card lacking `Neutral`.
pub fn validate_in_dir(mut card: VoiceCard, base_dir: &Path) -> Option<VoiceCard> {
    if !safe_card_id(&card.id) {
        return None;
    }
    let mut valid_clips = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for mut clip in card.clips {
        if !safe_rel_path(&clip.file) || !seen.insert(clip.style) {
            continue;
        }
        let Ok(bytes) = std::fs::read(base_dir.join(&clip.file)) else { continue };
        let Some(secs) = ogg_seconds(&bytes) else { continue };
        clip.seconds = secs;
        valid_clips.push(clip);
    }
    if !valid_clips.iter().any(|c| c.style == CardStyle::Neutral) {
        return None;
    }
    if card.rate <= 0.0
        && let Some(n) = valid_clips.iter().find(|c| c.style == CardStyle::Neutral)
    {
        let units = count_units(&n.text, &card.lang);
        if units > 0 && n.seconds > 0.1 {
            card.rate = units as f32 / n.seconds;
        }
    }
    if card.version == 0 {
        card.version = 1;
    }
    card.clips = valid_clips;
    Some(card)
}

fn count_units(text: &str, lang: &str) -> usize {
    if lang.to_ascii_lowercase().starts_with("en") {
        text.split_whitespace().filter(|w| w.chars().any(char::is_alphanumeric)).count()
    } else {
        text.chars().filter(|c| c.is_alphanumeric()).count()
    }
}

/// Serialized form stored in the CRDT KV log (`drama/{book}/cards` and `clones`) for sync (§2.4).
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PackedCard {
    pub card: VoiceCard,
    /// Relative clip filename → base64 Ogg bytes.
    pub files: BTreeMap<String, String>,
}

impl PackedCard {
    pub fn pack(card: &VoiceCard, dir: &Path) -> Option<Self> {
        let mut files = BTreeMap::new();
        for c in &card.clips {
            if !safe_rel_path(&c.file) {
                return None;
            }
            let bytes = std::fs::read(dir.join(&c.file)).ok()?;
            files.insert(c.file.clone(), base64::engine::general_purpose::STANDARD.encode(bytes));
        }
        Some(PackedCard { card: card.clone(), files })
    }

    /// Materializes `card.json` + Ogg files into `target_dir` if missing or older, then validates.
    pub fn materialize(&self, target_dir: &Path) -> Option<VoiceCard> {
        if !safe_card_id(&self.card.id) {
            return None;
        }
        let existing: Option<VoiceCard> = std::fs::read(target_dir.join("card.json"))
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok());
        let up_to_date = existing.as_ref().is_some_and(|e| {
            e.version >= self.card.version
                && self.card.clips.iter().all(|c| safe_rel_path(&c.file) && target_dir.join(&c.file).is_file())
        });
        if !up_to_date {
            std::fs::create_dir_all(target_dir).ok()?;
            for (rel, b64) in &self.files {
                if !safe_rel_path(rel) {
                    continue;
                }
                let bytes = base64::engine::general_purpose::STANDARD.decode(b64).ok()?;
                let dst = target_dir.join(rel);
                if let Some(parent) = dst.parent() {
                    std::fs::create_dir_all(parent).ok()?;
                }
                std::fs::write(dst, bytes).ok()?;
            }
            if let Ok(json) = serde_json::to_vec_pretty(&self.card) {
                let _ = std::fs::write(target_dir.join("card.json"), json);
            }
        }
        validate_in_dir(self.card.clone(), target_dir)
    }
}

/// Built-in voice pack (`models/voices-1/voices.json`), registered once per process like `library.rs`.
#[derive(Default)]
struct BuiltinPack {
    dir: PathBuf,
    cards: Vec<VoiceCard>,
}

#[derive(Deserialize)]
#[serde(untagged)]
enum PackFile {
    List(Vec<VoiceCard>),
    Wrapped { cards: Vec<VoiceCard> },
}

fn pack() -> &'static Mutex<BuiltinPack> {
    static P: OnceLock<Mutex<BuiltinPack>> = OnceLock::new();
    P.get_or_init(|| Mutex::new(BuiltinPack::default()))
}

/// Registers the built-in voice pack folder (`voices.json` + Ogg clips) and returns how many valid
/// cards it holds. Passing a folder without `voices.json` clears the pack.
pub fn set_pack(dir: &Path) -> usize {
    if let Ok(p) = pack().lock()
        && p.dir == dir
        && !p.cards.is_empty()
    {
        return p.cards.len();
    }
    let raw: Vec<VoiceCard> = std::fs::read(dir.join("voices.json"))
        .ok()
        .and_then(|b| serde_json::from_slice::<PackFile>(&b).ok())
        .map(|pf| match pf {
            PackFile::List(v) => v,
            PackFile::Wrapped { cards } => cards,
        })
        .unwrap_or_default();
    let valid: Vec<VoiceCard> = raw.into_iter().filter_map(|c| validate_in_dir(c, dir)).collect();
    let n = valid.len();
    if let Ok(mut p) = pack().lock() {
        *p = BuiltinPack { dir: dir.to_path_buf(), cards: valid };
    }
    n
}

pub fn builtin_cards() -> Vec<VoiceCard> {
    pack().lock().map(|p| p.cards.clone()).unwrap_or_default()
}

pub fn builtin_clip(id: &str, style: CardStyle) -> Option<(PathBuf, CardClip)> {
    let p = pack().lock().ok()?;
    let card = p.cards.iter().find(|c| c.id == id)?;
    let clip = card.clips.iter().find(|c| c.style == style).or_else(|| card.clips.iter().find(|c| c.style == CardStyle::Neutral))?;
    Some((p.dir.join(&clip.file), clip.clone()))
}

pub fn builtin_clip_path(id: &str, style: CardStyle) -> Option<PathBuf> {
    builtin_clip(id, style).map(|(p, _)| p)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::{ActDistance, ActPace};

    fn act(emotion: ActEmotion, level: u8, volume: ActVolume) -> DramaAct {
        DramaAct { emotion, level, volume, pace: ActPace::Normal, nonverbal: vec![], distance: ActDistance::Normal, from_text: false }
    }

    #[test]
    fn every_row_of_the_style_mapping_table() {
        // 1. Shout wins regardless of level or emotion.
        assert_eq!(style_for(&act(ActEmotion::Neutral, 0, ActVolume::Shout)), CardStyle::Shout);
        assert_eq!(style_for(&act(ActEmotion::Sadness, 3, ActVolume::Shout)), CardStyle::Shout);
        // 2. Whisper -> Soft regardless of level or emotion.
        assert_eq!(style_for(&act(ActEmotion::Anger, 3, ActVolume::Whisper)), CardStyle::Soft);
        assert_eq!(style_for(&act(ActEmotion::Neutral, 0, ActVolume::Whisper)), CardStyle::Soft);
        // 3. level <= 1 -> Soft when volume == Low, else Neutral.
        assert_eq!(style_for(&act(ActEmotion::Anger, 1, ActVolume::Low)), CardStyle::Soft);
        assert_eq!(style_for(&act(ActEmotion::Anger, 1, ActVolume::Normal)), CardStyle::Neutral);
        assert_eq!(style_for(&act(ActEmotion::Joy, 0, ActVolume::Loud)), CardStyle::Neutral);
        // 4. level >= 2 -> mapped by emotion.
        assert_eq!(style_for(&act(ActEmotion::Neutral, 2, ActVolume::Normal)), CardStyle::Neutral);
        assert_eq!(style_for(&act(ActEmotion::Joy, 2, ActVolume::Normal)), CardStyle::Joy);
        assert_eq!(style_for(&act(ActEmotion::Anger, 2, ActVolume::Normal)), CardStyle::Anger);
        assert_eq!(style_for(&act(ActEmotion::Disgust, 3, ActVolume::Normal)), CardStyle::Anger);
        assert_eq!(style_for(&act(ActEmotion::Sadness, 2, ActVolume::Normal)), CardStyle::Sad);
        assert_eq!(style_for(&act(ActEmotion::Weary, 2, ActVolume::Normal)), CardStyle::Sad);
        assert_eq!(style_for(&act(ActEmotion::Fear, 2, ActVolume::Normal)), CardStyle::Tense);
        assert_eq!(style_for(&act(ActEmotion::Anxious, 3, ActVolume::Normal)), CardStyle::Tense);
        assert_eq!(style_for(&act(ActEmotion::Surprise, 2, ActVolume::Normal)), CardStyle::Tense);
        assert_eq!(style_for(&act(ActEmotion::Tender, 2, ActVolume::Normal)), CardStyle::Soft);
        assert_eq!(style_for(&act(ActEmotion::Mocking, 3, ActVolume::Normal)), CardStyle::Neutral);
    }

    #[test]
    fn outside_paths_and_missing_neutral_are_rejected_and_ogg_seconds_override() {
        let dir = std::env::temp_dir().join(format!("yy-card-{}", getrandom::u64().unwrap()));
        std::fs::create_dir_all(dir.join("b01")).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        std::fs::copy(format!("{root}/../../fixtures/sounds/tone-48k-mono.ogg"), dir.join("b01/neutral.ogg")).unwrap();

        assert!(!safe_rel_path("../secret.ogg"));
        assert!(!safe_rel_path("b01/../../etc/passwd"));
        assert!(!safe_rel_path("/abs/file.ogg"));
        assert!(!safe_rel_path("C:\\file.ogg"));
        assert!(safe_rel_path("b01/neutral.ogg"));

        // Sneaky path in clip is stripped; without a valid Neutral, card is rejected.
        let sneaky = VoiceCard {
            id: "card.b.01".into(),
            name: "测试".into(),
            gender: CharacterGender::M,
            age: CharacterAge::Young,
            tags: vec!["稳".into()],
            lang: "zh".into(),
            origin: CardOrigin::Builtin,
            clips: vec![CardClip { style: CardStyle::Neutral, file: "../outside.ogg".into(), text: "你好".into(), seconds: 99.0 }],
            description: String::new(),
            version: 1,
            narrator: true,
            rate: 0.0,
            embedding: vec![],
            sync: false,
        };
        assert!(validate_in_dir(sneaky.clone(), &dir).is_none());

        // Joy-only card (no Neutral) is rejected.
        let mut joy_only = sneaky.clone();
        joy_only.clips = vec![CardClip { style: CardStyle::Joy, file: "b01/neutral.ogg".into(), text: "高兴".into(), seconds: 99.0 }];
        assert!(validate_in_dir(joy_only, &dir).is_none());

        // Valid Neutral + sneaky Anger: Anger is dropped, Neutral stays with true decoded Ogg length (~1.0 s, not 99.0 s).
        let mut good = sneaky;
        good.clips = vec![
            CardClip { style: CardStyle::Neutral, file: "b01/neutral.ogg".into(), text: "一二三四".into(), seconds: 99.0 },
            CardClip { style: CardStyle::Anger, file: "../../etc/passwd".into(), text: "坏".into(), seconds: 5.0 },
        ];
        let v = validate_in_dir(good, &dir).expect("valid card");
        assert_eq!(v.clips.len(), 1);
        assert!((v.clips[0].seconds - 1.0).abs() < 0.15, "got {}", v.clips[0].seconds);
        assert!(v.rate > 2.0);
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn sample_check_catches_length_level_clipping_noise_and_wrong_words() {
        let rate = 16_000u32;
        // Clean 8 s recording: 7 s of speech (-18 dBFS) + 1 s of quiet room (-55 dBFS).
        let mut clean = vec![0f32; 8 * rate as usize];
        for (i, s) in clean.iter_mut().enumerate() {
            let t = i as f32 / rate as f32;
            *s = if i < 7 * rate as usize { 0.12 * (2.0 * std::f32::consts::PI * 180.0 * t).sin() } else { 0.001 };
        }
        let text = "今天早上下了一场小雨我们沿着河边慢慢走回家";
        let ok = check_sample(&clean, rate, text, Some(text));
        assert!(ok.ok, "expected ok, got {:?}", ok.faults);

        // Too short (3 s).
        assert!(check_sample(&clean[..3 * rate as usize], rate, text, None).faults.contains(&CardFault::TooShort));
        // Clipped.
        let mut loud = clean.clone();
        for s in loud.iter_mut().take(500) {
            *s = 1.0;
        }
        assert!(check_sample(&loud, rate, text, None).faults.contains(&CardFault::Clipped));
        // Noisy room (room nearly as loud as speech).
        let noisy: Vec<f32> = (0..8 * rate as usize).map(|i| 0.08 * (2.0 * std::f32::consts::PI * 200.0 * i as f32 / rate as f32).sin()).collect();
        assert!(check_sample(&noisy, rate, text, None).faults.contains(&CardFault::Noisy));
        // Wrong words spoken.
        let wrong = check_sample(&clean, rate, text, Some("完全不一样的一段话根本没有照着念"));
        assert!(wrong.faults.contains(&CardFault::WrongWords));
    }

    #[test]
    fn version_and_style_go_into_live_and_studio_voice_keys() {
        let card = VoiceCard {
            id: "card.b.07".into(),
            name: "林清".into(),
            gender: CharacterGender::F,
            age: CharacterAge::Young,
            tags: vec![],
            lang: "zh".into(),
            origin: CardOrigin::Builtin,
            clips: vec![],
            description: String::new(),
            version: 2,
            narrator: false,
            rate: 4.2,
            embedding: vec![],
            sync: false,
        };
        let angry = act(ActEmotion::Anger, 3, ActVolume::Normal);
        assert_eq!(voice_key_of(&card, &angry, "live-zipvoice-1"), "card.b.07@2/anger");
        assert_eq!(voice_key_of(&card, &angry, "studio-qwen3tts-1.7b-1"), "card.b.07@2");
    }

    #[test]
    fn core_saves_syncs_falls_back_to_neutral_and_switches_layer_by_whole_chapter() {
        use crate::api::YuyueCore;
        use crate::drama::api::{DramaCaps, DramaResult, DramaTask};
        use crate::drama::queue::ClipCheck;
        use crate::drama::timeline::MadeClip;

        let tmp = std::env::temp_dir().join(format!("yy-card-e2e-{}", getrandom::u64().unwrap()));
        std::fs::create_dir_all(&tmp).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let ogg = format!("{root}/../../fixtures/sounds/tone-48k-mono.ogg");

        // 1. Register a built-in pack with 1 valid card + 1 path-traversal card.
        let pack_dir = tmp.join("voices-1");
        std::fs::create_dir_all(pack_dir.join("b01")).unwrap();
        std::fs::copy(&ogg, pack_dir.join("b01/neutral.ogg")).unwrap();
        let pack_json = r#"{
            "license": "CC0 1.0",
            "cards": [
                {"id": "card.b.01", "name": "沉舟", "gender": "M", "age": "Middle", "tags": ["厚", "稳"], "lang": "zh", "origin": {"kind": "builtin"}, "clips": [{"style": "neutral", "file": "b01/neutral.ogg", "text": "山雨欲来风满楼。", "seconds": 50.0}], "narrator": true},
                {"id": "card.b.02", "name": "越界", "gender": "F", "age": "Young", "lang": "zh", "origin": {"kind": "builtin"}, "clips": [{"style": "neutral", "file": "../outside.ogg", "text": "坏", "seconds": 1.0}]}
            ]
        }"#;
        std::fs::write(pack_dir.join("voices.json"), pack_json).unwrap();

        let dev1 = YuyueCore::open(tmp.join("d1").to_string_lossy().into_owned(), "Mac".into(), "macos".into()).unwrap();
        assert_eq!(dev1.cards_set_pack(pack_dir.to_string_lossy().into_owned()), 1);
        // Anger isn't on card.b.01 -> falls back to neutral.ogg.
        let clip_p = dev1.card_clip_path(None, "card.b.01".into(), CardStyle::Anger).unwrap();
        assert!(clip_p.ends_with("b01/neutral.ogg"));

        // 2. Import a short book, design a card for it, and verify sync to dev2 + layer switching.
        let book_txt = tmp.join("book.txt");
        std::fs::write(&book_txt, "试音记\n\n第一章\n\n宝玉笑道：“妹妹来了。”黛玉道：“早来了。”宝玉道：“快请坐。”黛玉笑道：“好。”\n").unwrap();
        let book = dev1.import_book(book_txt.to_string_lossy().into_owned()).unwrap();

        let stage = tmp.join("stage");
        std::fs::create_dir_all(&stage).unwrap();
        std::fs::copy(&ogg, stage.join("neutral.ogg")).unwrap();
        let designed = VoiceCard {
            id: format!("card.d.{}.1", &book.id[..8]),
            name: "宝玉".into(),
            gender: CharacterGender::M,
            age: CharacterAge::Teen,
            tags: vec!["亮".into()],
            lang: "zh".into(),
            origin: CardOrigin::Designed { book: book.id.clone(), character: 1 },
            clips: vec![CardClip { style: CardStyle::Neutral, file: "neutral.ogg".into(), text: "今天天气很好。".into(), seconds: 8.0 }],
            description: "少年男声".into(),
            version: 1,
            narrator: false,
            rate: 0.0,
            embedding: vec![1.0, 0.0],
            sync: true,
        };
        let saved = dev1.card_save(Some(book.id.clone()), designed.clone(), stage.to_string_lossy().into_owned()).unwrap();
        assert!((saved.clips[0].seconds - 1.0).abs() < 0.15);

        // Sync changes to a second device and confirm the designed card materializes with its Ogg file.
        let dev2 = YuyueCore::open(tmp.join("d2").to_string_lossy().into_owned(), "Phone".into(), "ios".into()).unwrap();
        let _ = dev2.import_book(book_txt.to_string_lossy().into_owned()).unwrap();
        let batch = dev1.export_changes(dev2.sync_version().unwrap()).unwrap();
        dev2.import_changes(batch).unwrap();
        let synced_cards = dev2.cards_of_book(book.id.clone()).unwrap();
        assert_eq!(synced_cards.len(), 1);
        assert!(dev2.card_clip_path(Some(book.id.clone()), saved.id.clone(), CardStyle::Joy).unwrap().ends_with("neutral.ogg"));

        // 3. Section layer stays Live until EVERY clip of the chapter is made under studio_engine.
        dev1.drama_set_voice(book.id.clone(), "宝玉".into(), saved.id.clone()).unwrap();
        let vi_narr = dev1.card_voice_info(dev1.cards_builtin()[0].clone(), vec![0.0, 1.0], true);
        let vi_char = dev1.card_voice_info(saved.clone(), vec![1.0, 0.0], true);
        dev1.drama_cast_voices(book.id.clone(), vec![vi_narr.clone(), vi_char], vi_narr.id.clone()).unwrap();
        let studio = "studio-qwen3tts-1.7b-1";
        dev1.drama_set_caps(DramaCaps { tts: true, engine: studio.into(), tts_same_each_time: true, ..Default::default() });
        let n_secs = dev1.book(book.id.clone()).unwrap().sections.len() as u32;
        let dialogue_sec = (0..n_secs)
            .find(|&s| {
                let ds = dev1.drama_section(book.id.clone(), s).unwrap();
                ds.segments.iter().any(|seg| matches!(seg.speaker, crate::drama::DramaSpeaker::Char { .. }))
            })
            .expect("section with character dialogue");
        assert_eq!(dev1.drama_section_layer(book.id.clone(), dialogue_sec, studio.into()).unwrap(), DramaLayer::Live);

        // Complete all clips of `dialogue_sec` under studio_engine.
        let mut made_in_sec = 0;
        while let Some(task) = dev1.drama_next_task(book.id.clone(), dialogue_sec).unwrap() {
            let DramaTask::Tts { id, clip } = task else { break };
            if clip.section == dialogue_sec && made_in_sec == 1 {
                assert_eq!(dev1.drama_section_layer(book.id.clone(), dialogue_sec, studio.into()).unwrap(), DramaLayer::Live);
            }
            if clip.section == dialogue_sec {
                made_in_sec += 1;
            }
            let check = ClipCheck { cer: 0.0, longest_missing: 0, voice_sim: 0.95, clipped: false, silence_head: 0.05, silence_tail: 0.05, seconds: 1.2, chars: 5 };
            dev1.drama_submit(book.id.clone(), id, DramaResult::Clip { clip: MadeClip { key: clip.key, seconds: 1.2, char_times: vec![] }, check }).unwrap();
        }
        assert!(made_in_sec >= 2);
        assert_eq!(dev1.drama_section_layer(book.id.clone(), dialogue_sec, studio.into()).unwrap(), DramaLayer::Studio);

        // Bumping the card's version invalidates its studio clips -> flips back to Live!
        let mut v2 = saved;
        v2.version = 2;
        dev1.card_save(Some(book.id.clone()), v2, stage.to_string_lossy().into_owned()).unwrap();
        assert_eq!(dev1.drama_section_layer(book.id.clone(), dialogue_sec, studio.into()).unwrap(), DramaLayer::Live);

        let _ = std::fs::remove_dir_all(&tmp);
    }
}
