//! 时间线: where each clip, the room tone and each sound go in a scene, and how loud, how far,
//! which side. Kept as a list, not baked into a file: turning the room tone off, a new voice
//! for one person or one line given to someone else changes only what it touches.

use serde::{Deserialize, Serialize};

use super::queue::ClipAsk;
use super::sound::{SoundAsset, SoundEvent, pick_bed, pick_spot};
use super::{ActDistance, DramaScene, SegKind, DramaSegment, DramaSpeaker, ActVolume};

/// Seconds of silence between clips.
pub mod gap {
    /// Narration ↔ a line within one sentence.
    pub const IN_SENTENCE: f64 = 0.15;
    /// Someone else speaks.
    pub const NEW_SPEAKER: f64 = 0.30;
    /// Breaking in on a line cut off (—— …).
    pub const BREAK_IN: f64 = 0.05;
    /// Sentence to sentence in one voice (on top of the voice's own ending, which is trimmed).
    pub const SENTENCE: f64 = 0.25;
    /// Paragraph to paragraph.
    pub const PARAGRAPH: f64 = 0.60;
    /// DramaScene to scene; the room tone crossfades here.
    pub const SCENE: f64 = 1.50;
    pub const BED_CROSSFADE: f32 = 1.2;
    /// After a 过门, before the first voice.
    pub const AFTER_TRANSITION: f64 = 0.5;
}

/// Levels (dB) relative to the voice bus.
pub mod level {
    pub const BED: f32 = -12.0;
    pub const SPOT: f32 = -6.0;
    pub const TRANSITION: f32 = -8.0;
    pub const WHISPER: f32 = -4.0;
    pub const SHOUT: f32 = 2.0;
    pub const FAR: f32 = -6.0;
    pub const OFFSTAGE: f32 = -9.0;
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum TimelineFilter {
    None,
    LowPass { hz: f32 },
    HighPass { hz: f32 },
    BandPass { low: f32, high: f32 },
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum TimelineItem {
    Voice { clip: String, at: f64, seconds: f64, gain_db: f32, pan: f32, reverb_send: f32, filter: TimelineFilter, sentence: u32, start: u32, end: u32 },
    Bed { asset: String, from: f64, to: f64, gain_db: f32, fade_in: f32, fade_out: f32 },
    Spot { asset: String, at: f64, gain_db: f32, pan: f32 },
    Music { asset: String, at: f64, gain_db: f32 },
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaTimeline {
    pub section: u32,
    pub first: u32,
    pub last: u32,
    pub reverb: String,
    pub duration: f64,
    pub items: Vec<TimelineItem>,
}

/// A made clip: its key, length, and (when the engine gave them) when each character starts.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct MadeClip {
    pub key: String,
    pub seconds: f64,
    pub char_times: Vec<f32>,
}

/// The reverb preset for a place.
pub fn reverb_for(place_kind: &str, indoor: Option<bool>) -> &'static str {
    match place_kind {
        "大殿" | "厅堂" | "山洞" | "地下室" | "地铁车站" | "机场" | "工厂" => "hall",
        "居室" | "书房" | "厨房" | "牢房" | "船舱" | "车厢" | "车内" | "教室" | "办公室" | "医院" | "咖啡馆" | "餐厅" | "酒吧" | "客栈酒馆" | "茶楼" | "店铺" => "room",
        "梦境" | "回忆" => "dream",
        "未知" => {
            if indoor == Some(false) {
                "outdoor"
            } else {
                "room"
            }
        }
        _ => "outdoor",
    }
}

/// Where a stretch's speaker stands, −1 left … 1 right: the leads spread over ±0.25 in the
/// order they first speak in the scene; narration centre.
fn pan_for(seg: &DramaSegment, order: &[u32]) -> f32 {
    let DramaSpeaker::Char { id } = seg.speaker else { return 0.0 };
    if seg.kind == SegKind::Narration || seg.kind == SegKind::Thought {
        return 0.0;
    }
    let Some(i) = order.iter().position(|o| *o == id) else { return 0.0 };
    let n = order.len().max(2) as f32;
    -0.25 + 0.5 * (i as f32) / (n - 1.0)
}

/// Lays out one scene. `segs` are the scene's stretches, `asks` the clips they make (in order,
/// as `queue::clips` gave), `made` what came back. A clip not made yet stops the scene there:
/// the timeline is only ever as long as what can play.
pub fn lay_out(scene: &DramaScene, segs: &[DramaSegment], asks: &[ClipAsk], made: &dyn Fn(&str) -> Option<MadeClip>, paragraph_starts: &[u32], events: &[SoundEvent], library: &[SoundAsset], transition: Option<&SoundAsset>) -> DramaTimeline {
    let mut items = Vec::new();
    let mut t = 0.0f64;
    // 过门: the scene opens on it; the first voice comes in after it has died away.
    if let Some(m) = transition {
        items.push(TimelineItem::Music { asset: m.id.clone(), at: 0.0, gain_db: level::TRANSITION });
        t = m.seconds as f64 + gap::AFTER_TRANSITION;
    }
    let mut order: Vec<u32> = Vec::new();
    let mut prev: Option<(&ClipAsk, &DramaSegment)> = None;
    let mut spot_n = 0usize;
    let mut placed: Vec<(u32, u32, f64, f64, Option<Vec<f32>>)> = Vec::new(); // sentence, start, at, seconds, char_times
    for a in asks {
        let Some(seg) = segs.iter().find(|s| s.sentence == a.sentence && s.start == a.start) else { continue };
        let Some(m) = made(&a.key) else { break };
        if let DramaSpeaker::Char { id } = seg.speaker
            && !order.contains(&id) && seg.kind == SegKind::Speech {
                order.push(id);
            }
        if let Some((pa, ps)) = prev {
            t += if a.sentence == pa.sentence {
                if pa.voice != a.voice {
                    if ps.text.trim_end().trim_end_matches(['”', '」', '"']).ends_with(['—', '…']) { gap::BREAK_IN } else { gap::IN_SENTENCE }
                } else {
                    0.0
                }
            } else if paragraph_starts.contains(&a.sentence) {
                gap::PARAGRAPH
            } else if pa.voice != a.voice && seg.kind != SegKind::Narration && ps.kind != SegKind::Narration {
                gap::NEW_SPEAKER
            } else {
                gap::SENTENCE
            };
        }
        let mut gain = 0.0;
        let mut filter = TimelineFilter::None;
        let mut send = 0.25;
        match seg.act.volume {
            ActVolume::Whisper => gain += level::WHISPER,
            ActVolume::Shout => gain += level::SHOUT,
            _ => {}
        }
        match seg.act.distance {
            ActDistance::Far => {
                gain += level::FAR;
                filter = TimelineFilter::LowPass { hz: 3000.0 };
                send = 0.5;
            }
            ActDistance::Offstage => {
                gain += level::OFFSTAGE;
                filter = TimelineFilter::LowPass { hz: 1500.0 };
                send = 0.45;
            }
            ActDistance::Close => send = 0.1,
            ActDistance::Normal => {}
        }
        if seg.kind == SegKind::Thought {
            send = 0.0;
            filter = TimelineFilter::HighPass { hz: 120.0 };
        }
        if seg.kind == SegKind::Narration {
            send = 0.0;
        }
        items.push(TimelineItem::Voice { clip: a.key.clone(), at: t, seconds: m.seconds, gain_db: gain, pan: pan_for(seg, &order), reverb_send: send, filter, sentence: a.sentence, start: a.start, end: a.end });
        placed.push((a.sentence, a.start, t, m.seconds, (!m.char_times.is_empty()).then(|| m.char_times.clone())));
        t += m.seconds;
        prev = Some((a, seg));
    }
    let duration = t;
    // Sounds at their words: by character time when the engine gave it, by share of the clip
    // otherwise; never over the first 0.2 s of the next line (pushed back is left to the mixer).
    for e in events {
        let Some(p) = placed.iter().filter(|p| p.0 == e.sentence && p.1 <= e.at_char).max_by_key(|p| p.1) else { continue };
        let a = asks.iter().find(|a| a.sentence == p.0 && a.start == p.1).unwrap();
        let len = (a.end - a.start).max(1) as f64;
        let k = (e.at_char - a.start) as f64;
        let at = match &p.4 {
            Some(ct) if (k as usize) < ct.len() => p.2 + ct[k as usize] as f64,
            _ => p.2 + p.3 * (k / len).min(1.0),
        };
        if let Some(asset) = pick_spot(&super::sound::event_key(e), spot_n, library) {
            spot_n += 1;
            items.push(TimelineItem::Spot { asset: asset.id.clone(), at, gain_db: level::SPOT, pan: 0.0 });
        }
    }
    if let Some(bed) = pick_bed(scene, library) {
        items.push(TimelineItem::Bed { asset: bed.id.clone(), from: 0.0, to: duration + gap::SCENE, gain_db: level::BED, fade_in: gap::BED_CROSSFADE, fade_out: gap::BED_CROSSFADE });
    }
    DramaTimeline { section: scene.section, first: scene.first, last: scene.last, reverb: reverb_for(&scene.place_kind, scene.indoor).to_string(), duration, items }
}

/// When each sentence starts and ends in a scene's timeline: for highlight, jumps, timers.
pub fn sentence_times(tl: &DramaTimeline) -> Vec<(u32, f64, f64)> {
    let mut out: Vec<(u32, f64, f64)> = Vec::new();
    for it in &tl.items {
        if let TimelineItem::Voice { at, seconds, sentence, .. } = it {
            match out.iter_mut().find(|o| o.0 == *sentence) {
                Some(o) => {
                    o.1 = o.1.min(*at);
                    o.2 = o.2.max(at + seconds);
                }
                None => out.push((*sentence, *at, at + seconds)),
            }
        }
    }
    out.sort_by(|a, b| a.1.partial_cmp(&b.1).unwrap_or(std::cmp::Ordering::Equal));
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::queue::{Voices, clips};
    use crate::drama::{DramaAct, LineBasis, SceneCrowd, DecidedBy, SceneTime, SceneWeather};

    fn seg(sentence: u32, start: u32, end: u32, kind: SegKind, speaker: DramaSpeaker, text: &str) -> DramaSegment {
        DramaSegment { sentence, start, end, kind, utterance: None, speaker, addressee: None, basis: LineBasis::None, decided_by: DecidedBy::Rule, p_correct: 1.0, act: DramaAct::flat(), tag_only: false, text: text.into() }
    }

    #[test]
    fn gaps_follow_who_speaks() {
        let scene = DramaScene { section: 0, first: 0, last: 1, place: String::new(), place_kind: "居室".into(), indoor: Some(true), time: SceneTime::Day, weather: SceneWeather::Clear, crowd: SceneCrowd::Few, present: vec![1, 2] };
        let segs = vec![
            seg(0, 0, 5, SegKind::Narration, DramaSpeaker::Narrator, "宝玉笑道："),
            seg(0, 5, 10, SegKind::Speech, DramaSpeaker::Char { id: 1 }, "“你来了。”"),
            seg(1, 0, 5, SegKind::Speech, DramaSpeaker::Char { id: 2 }, "“来了。”"),
        ];
        let of = |id: u32| Some(format!("v{id}"));
        let v = Voices { narrator: "n", dialogue: "d", of: &of };
        let asks = clips(0, &segs, &v, false, "e", &|t| t.to_string());
        let lib = vec![SoundAsset { id: "room1".into(), key: "居室/day/clear".into(), seconds: 60.0, loops: true, license: "CC0".into() }];
        let tl = lay_out(&scene, &segs, &asks, &|_| Some(MadeClip { key: String::new(), seconds: 1.0, char_times: vec![] }), &[], &[], &lib, None);
        let at: Vec<f64> = tl.items.iter().filter_map(|i| if let TimelineItem::Voice { at, .. } = i { Some(*at) } else { None }).collect();
        assert_eq!(at.len(), 3);
        assert!((at[1] - (1.0 + gap::IN_SENTENCE)).abs() < 1e-9);
        assert!((at[2] - (at[1] + 1.0 + gap::NEW_SPEAKER)).abs() < 1e-9);
        assert!(tl.items.iter().any(|i| matches!(i, TimelineItem::Bed { .. })));
        assert_eq!(tl.reverb, "room");
        // A clip not made yet: the timeline stops there.
        let tl2 = lay_out(&scene, &segs, &asks, &|k| (k != asks[2].key).then(|| MadeClip { key: k.into(), seconds: 1.0, char_times: vec![] }), &[], &[], &[], None);
        assert_eq!(tl2.items.len(), 2);
        assert_eq!(sentence_times(&tl).len(), 2);
    }

    /// A scene that opens on a 过门: the first voice comes in half a second after it, and the
    /// first sentence played on its own still has it.
    #[test]
    fn a_transition_opens_the_scene() {
        let scene = DramaScene { section: 0, first: 0, last: 0, place: String::new(), place_kind: "居室".into(), indoor: Some(true), time: SceneTime::Day, weather: SceneWeather::Clear, crowd: SceneCrowd::Few, present: vec![] };
        let segs = vec![seg(0, 0, 5, SegKind::Narration, DramaSpeaker::Narrator, "天色晚了。")];
        let of = |id: u32| Some(format!("v{id}"));
        let v = Voices { narrator: "n", dialogue: "d", of: &of };
        let asks = clips(0, &segs, &v, false, "e", &|t| t.to_string());
        let music = SoundAsset { id: "builtin:pluck_phrase".into(), key: crate::drama::builtin::TRANSITION.into(), seconds: 4.5, loops: false, license: "语阅自制".into() };
        let tl = lay_out(&scene, &segs, &asks, &|_| Some(MadeClip { key: String::new(), seconds: 1.0, char_times: vec![] }), &[], &[], &[], Some(&music));
        assert!(matches!(&tl.items[0], TimelineItem::Music { at, .. } if *at == 0.0));
        let first = sentence_times(&tl)[0].1;
        assert!((first - (4.5 + gap::AFTER_TRANSITION)).abs() < 1e-9);
        let w = crate::drama::api::window_of(tl, true, 0).unwrap();
        assert_eq!(w.from, 0.0);
        assert!(w.timeline.items.iter().any(|i| matches!(i, TimelineItem::Music { .. })));
    }
}
