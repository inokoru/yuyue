//! 合成队列: which clips a section needs, under which key they're kept, and what to do when the
//! app's check of a clip (转写、声纹、电平) fails — make it again, then without acting, then in
//! the narrator's voice, noted.

use serde::{Deserialize, Serialize};

use super::{DramaAct, DramaSegment, SegKind, DramaSpeaker};

/// Bumped when what goes into a clip changes, so old clips aren't reused.
pub const CLIP_VERSION: u32 = 1;

/// One clip to make: one or more neighbouring stretches of a sentence in the same voice and
/// acting.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct ClipAsk {
    pub key: String,
    pub voice: String,
    pub text: String,
    pub act: DramaAct,
    /// The two stretches before, for voices that carry tone over: (who, text).
    pub context: Vec<ContextLine>,
    pub section: u32,
    pub sentence: u32,
    pub start: u32,
    pub end: u32,
    pub attempt: u8,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct ContextLine {
    pub voice: String,
    pub text: String,
}

/// Which voice reads a stretch.
pub struct Voices<'a> {
    pub narrator: &'a str,
    pub dialogue: &'a str,
    pub of: &'a dyn Fn(u32) -> Option<String>,
}

pub fn voice_for(s: &DramaSegment, v: &Voices) -> String {
    let speaker_voice = |sp: &DramaSpeaker| match sp {
        DramaSpeaker::Char { id } => (v.of)(*id).unwrap_or_else(|| v.dialogue.to_string()),
        DramaSpeaker::Narrator => v.narrator.to_string(),
        _ => v.dialogue.to_string(),
    };
    match s.kind {
        SegKind::Narration => v.narrator.to_string(),
        SegKind::Speech | SegKind::Thought => speaker_voice(&s.speaker),
        SegKind::Quoted => match s.speaker {
            DramaSpeaker::Char { .. } => speaker_voice(&s.speaker),
            _ => v.narrator.to_string(),
        },
    }
}

pub fn clip_key(voice: &str, text: &str, act: &DramaAct, engine: &str) -> String {
    let mut h = blake3::Hasher::new();
    h.update(format!("{CLIP_VERSION}\u{1}{engine}\u{1}{voice}\u{1}{text}\u{1}").as_bytes());
    h.update(serde_json::to_string(act).unwrap_or_default().as_bytes());
    h.finalize().to_hex()[..32].to_string()
}

/// Whether there's anything to say: punctuation alone isn't.
fn speakable(t: &str) -> bool {
    t.chars().any(|c| c.is_alphanumeric())
}

/// The clips a section needs, in reading order. Stretches marked `tag_only` are left out when
/// 剧化 is on. `reading` rewrites the text for the voice (读音表).
pub fn clips(section: u32, segs: &[DramaSegment], v: &Voices, drama_mode: bool, engine: &str, reading: &dyn Fn(&str) -> String) -> Vec<ClipAsk> {
    let mut out: Vec<ClipAsk> = Vec::new();
    for s in segs {
        if drama_mode && s.tag_only {
            continue;
        }
        let voice = voice_for(s, v);
        let act = if s.kind == SegKind::Narration { DramaAct::flat() } else { s.act.clone() };
        if let Some(last) = out.last_mut()
            && last.sentence == s.sentence && last.voice == voice && last.act == act && last.end == s.start {
                last.text.push_str(&s.text);
                last.end = s.end;
                continue;
            }
        out.push(ClipAsk { key: String::new(), voice, text: s.text.clone(), act, context: vec![], section, sentence: s.sentence, start: s.start, end: s.end, attempt: 0 });
    }
    out.retain(|c| speakable(&c.text));
    for i in 0..out.len() {
        let ctx: Vec<ContextLine> = out[i.saturating_sub(2)..i].iter().map(|c| ContextLine { voice: c.voice.clone(), text: c.text.clone() }).collect();
        out[i].text = reading(&out[i].text);
        out[i].key = clip_key(&out[i].voice, &out[i].text, &out[i].act, engine);
        out[i].context = ctx;
    }
    out
}

/// What the app measured on a clip it made.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct ClipCheck {
    /// Error rate of the transcript against the text, by reading (0–1).
    pub cer: f32,
    /// The longest run of characters missing from the transcript.
    pub longest_missing: u32,
    /// DramaSpeaker-embedding similarity to the voice's reference.
    pub voice_sim: f32,
    pub clipped: bool,
    pub silence_head: f32,
    pub silence_tail: f32,
    pub seconds: f32,
    pub chars: u32,
}

/// What this voice usually measures: similarity mean and spread over its clips, and seconds a
/// character.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct VoiceNorm {
    pub sim_mean: f32,
    pub sim_sd: f32,
    pub sec_per_char: f32,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ClipVerdict {
    Keep,
    Again,
    /// Again, without acting.
    Plain,
    /// In the narrator's voice; noted on the cast page.
    Narrator,
}

/// Silence is quieter than this (dB below full scale).
pub const SILENCE_DB: f32 = -45.0;

/// 回听: measures a made clip — `heard` is what the transcriber made of it, `samples` the clip
/// (mono, `rate` a second). Errors are counted by reading (homophones and 平翘舌 are no error),
/// on the clip's sounds; `voice_sim` is passed through (1 where the device can't measure it).
pub fn measure(text: &str, heard: &str, samples: &[f32], rate: u32, voice_sim: f32, chinese: bool) -> ClipCheck {
    use crate::audiobook::phon::{phon, same};
    let want = phon(text, chinese);
    let got = phon(heard, chinese);
    // Edit distance on sounds.
    let (n, m) = (want.len(), got.len());
    let mut prev: Vec<usize> = (0..=m).collect();
    for i in 1..=n {
        let mut cur = vec![i; m + 1];
        for j in 1..=m {
            let sub = prev[j - 1] + usize::from(!same(&want[i - 1], &got[j - 1]));
            cur[j] = sub.min(prev[j] + 1).min(cur[j - 1] + 1);
        }
        prev = cur;
    }
    let cer = if n == 0 { 0.0 } else { prev[m] as f32 / n as f32 };
    // The longest run of the text's sounds nothing heard matched.
    let matched = crate::audiobook::anchor::align(&got, &want);
    let mut longest = 0u32;
    let mut run = 0u32;
    for x in &matched {
        if x.is_none() {
            run += 1;
            longest = longest.max(run);
        } else {
            run = 0;
        }
    }
    // Quiet at each end, in 10 ms frames.
    let frame = (rate as usize / 100).max(1);
    let loud = |w: &[f32]| {
        let e = w.iter().map(|x| x * x).sum::<f32>() / w.len().max(1) as f32;
        10.0 * (e + 1e-12).log10() > SILENCE_DB
    };
    let frames: Vec<bool> = samples.chunks(frame).map(loud).collect();
    let head = frames.iter().take_while(|l| !**l).count();
    let tail = if head == frames.len() { 0 } else { frames.iter().rev().take_while(|l| !**l).count() };
    // Clipped: three samples in a row at full scale.
    let clipped = samples.windows(3).any(|w| w.iter().all(|x| x.abs() >= 0.999));
    let chars = text.chars().filter(|c| c.is_alphanumeric()).count() as u32;
    ClipCheck {
        cer,
        longest_missing: longest,
        voice_sim,
        clipped,
        silence_head: (head * frame) as f32 / rate.max(1) as f32,
        silence_tail: (tail * frame) as f32 / rate.max(1) as f32,
        seconds: samples.len() as f32 / rate.max(1) as f32,
        chars,
    }
}

/// A fault no transcriber's slip explains: words left out, clipping, long quiet at an end, or
/// most of it heard wrong.
pub fn gross(c: &ClipCheck) -> bool {
    c.longest_missing >= 3 || c.clipped || c.silence_head > 0.4 || c.silence_tail > 0.4 || c.cer > 0.3
}

/// What to do with a clip. Two more tries as it is, then one without acting, then the narrator.
pub fn judge(c: &ClipCheck, norm: Option<&VoiceNorm>, attempt: u8) -> ClipVerdict {
    let mut bad = c.cer > 0.08 || c.longest_missing >= 3 || c.clipped || c.silence_head > 0.4 || c.silence_tail > 0.4;
    if let Some(n) = norm {
        if n.sim_sd > 0.0 && c.voice_sim < n.sim_mean - 3.0 * n.sim_sd {
            bad = true;
        }
        let want = n.sec_per_char * c.chars as f32;
        if want > 0.5 && (c.seconds < want * 0.5 || c.seconds > want * 1.5) {
            bad = true;
        }
    }
    if !bad {
        return ClipVerdict::Keep;
    }
    match attempt {
        0 | 1 => ClipVerdict::Again,
        2 => ClipVerdict::Plain,
        _ => ClipVerdict::Narrator,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::{LineBasis, DecidedBy};

    fn seg(sentence: u32, start: u32, end: u32, kind: SegKind, speaker: DramaSpeaker, text: &str) -> DramaSegment {
        DramaSegment { sentence, start, end, kind, utterance: None, speaker, addressee: None, basis: LineBasis::None, decided_by: DecidedBy::Unsure, p_correct: 0.0, act: DramaAct::flat(), tag_only: false, text: text.into() }
    }

    #[test]
    fn neighbours_in_one_voice_are_one_clip() {
        let segs = vec![
            seg(0, 0, 5, SegKind::Narration, DramaSpeaker::Narrator, "宝玉笑道："),
            seg(0, 5, 10, SegKind::Speech, DramaSpeaker::Char { id: 1 }, "“你来了。”"),
            seg(1, 0, 4, SegKind::Narration, DramaSpeaker::Narrator, "说着，"),
            seg(1, 4, 8, SegKind::Narration, DramaSpeaker::Narrator, "走了。"),
        ];
        let of = |id: u32| (id == 1).then(|| "bao".to_string());
        let v = Voices { narrator: "n", dialogue: "d", of: &of };
        let c = clips(0, &segs, &v, false, "e", &|t| t.to_string());
        assert_eq!(c.len(), 3);
        assert_eq!(c[1].voice, "bao");
        assert_eq!(c[2].text, "说着，走了。");
        assert_eq!(c[1].context.len(), 1);
        // 剧化 leaves the tag out.
        let mut segs2 = segs.clone();
        segs2[0].tag_only = true;
        assert_eq!(clips(0, &segs2, &v, true, "e", &|t| t.to_string())[0].voice, "bao");
    }

    #[test]
    fn a_bad_clip_is_made_again_then_plain_then_by_the_narrator() {
        let bad = ClipCheck { cer: 0.2, longest_missing: 0, voice_sim: 0.9, clipped: false, silence_head: 0.0, silence_tail: 0.0, seconds: 2.0, chars: 10 };
        assert_eq!(judge(&bad, None, 0), ClipVerdict::Again);
        assert_eq!(judge(&bad, None, 2), ClipVerdict::Plain);
        assert_eq!(judge(&bad, None, 3), ClipVerdict::Narrator);
        let good = ClipCheck { cer: 0.0, ..bad.clone() };
        assert_eq!(judge(&good, Some(&VoiceNorm { sim_mean: 0.9, sim_sd: 0.02, sec_per_char: 0.2 }), 0), ClipVerdict::Keep);
        let drifted = ClipCheck { voice_sim: 0.7, ..good };
        assert_eq!(judge(&drifted, Some(&VoiceNorm { sim_mean: 0.9, sim_sd: 0.02, sec_per_char: 0.2 }), 0), ClipVerdict::Again);
    }

    #[test]
    fn a_clip_is_measured_by_reading() {
        let rate = 16_000;
        let mut samples = vec![0.0f32; rate as usize / 2];
        samples.extend((0..rate).map(|i| (i as f32 * 0.05).sin() * 0.3));
        samples.extend(vec![0.0f32; rate as usize / 10]);
        let ok = measure("宝玉笑道，你来了。", "宝玉笑到你来了", &samples, rate, 1.0, true);
        assert!(ok.cer < 0.01, "{ok:?}");
        assert!((ok.silence_head - 0.5).abs() < 0.02 && (ok.silence_tail - 0.1).abs() < 0.02, "{ok:?}");
        assert!(!ok.clipped);
        let missing = measure("宝玉笑道，你来了，快坐下吧。", "宝玉笑道", &samples, rate, 1.0, true);
        assert!(missing.cer > 0.3 && missing.longest_missing >= 3, "{missing:?}");
        assert_eq!(judge(&missing, None, 0), ClipVerdict::Again);
    }
}
