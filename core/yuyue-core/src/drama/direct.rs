//! 导演稿: how each stretch is read. What the book writes comes first (冷笑道, 哭道, 低声道); a
//! model fills in what it doesn't, one step softer than it asks for — flat beats overacted.

use serde::Deserialize;
use serde_json::json;

use super::{DramaAct, ActDistance, ActEmotion, ActNonverbal, ActPace, ActVolume};

pub const DIRECT_PROMPT: &str = include_str!("prompts/direct.v1.txt");

/// Speech-tag words and what they ask for: (word, emotion, level, volume, pace, nonverbal).
/// Longest first where one holds another.
const TAGS: &[(&str, ActEmotion, u8, ActVolume, ActPace, Option<ActNonverbal>)] = &[
    ("冷笑道", ActEmotion::Mocking, 2, ActVolume::Normal, ActPace::Normal, None),
    ("冷笑", ActEmotion::Mocking, 2, ActVolume::Normal, ActPace::Normal, None),
    ("哭道", ActEmotion::Sadness, 3, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sob)),
    ("哭着", ActEmotion::Sadness, 3, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sob)),
    ("泣道", ActEmotion::Sadness, 2, ActVolume::Low, ActPace::Slow, Some(ActNonverbal::Sob)),
    ("喝道", ActEmotion::Anger, 3, ActVolume::Shout, ActPace::Normal, None),
    ("骂道", ActEmotion::Anger, 3, ActVolume::Loud, ActPace::Fast, None),
    ("怒道", ActEmotion::Anger, 2, ActVolume::Loud, ActPace::Normal, None),
    ("嚷道", ActEmotion::Anger, 1, ActVolume::Loud, ActPace::Fast, None),
    ("喊道", ActEmotion::Neutral, 1, ActVolume::Shout, ActPace::Normal, None),
    ("叫道", ActEmotion::Neutral, 1, ActVolume::Loud, ActPace::Normal, None),
    ("悄声道", ActEmotion::Neutral, 1, ActVolume::Whisper, ActPace::Normal, None),
    ("悄悄的道", ActEmotion::Neutral, 1, ActVolume::Whisper, ActPace::Normal, None),
    ("低声道", ActEmotion::Neutral, 1, ActVolume::Low, ActPace::Normal, None),
    ("忙道", ActEmotion::Anxious, 1, ActVolume::Normal, ActPace::Fast, None),
    ("急道", ActEmotion::Anxious, 2, ActVolume::Normal, ActPace::Fast, None),
    ("叹道", ActEmotion::Weary, 1, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sigh)),
    ("嘆道", ActEmotion::Weary, 1, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sigh)),
    ("歎道", ActEmotion::Weary, 1, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sigh)),
    ("笑道", ActEmotion::Joy, 1, ActVolume::Normal, ActPace::Normal, None),
    ("笑着", ActEmotion::Joy, 1, ActVolume::Normal, ActPace::Normal, None),
    ("惊道", ActEmotion::Surprise, 2, ActVolume::Normal, ActPace::Fast, Some(ActNonverbal::Gasp)),
    ("whispered", ActEmotion::Neutral, 1, ActVolume::Whisper, ActPace::Normal, None),
    ("shouted", ActEmotion::Neutral, 1, ActVolume::Shout, ActPace::Normal, None),
    ("cried", ActEmotion::Neutral, 1, ActVolume::Loud, ActPace::Normal, None),
    ("sobbed", ActEmotion::Sadness, 3, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sob)),
    ("laughed", ActEmotion::Joy, 2, ActVolume::Normal, ActPace::Normal, Some(ActNonverbal::Laugh)),
    ("sighed", ActEmotion::Weary, 1, ActVolume::Normal, ActPace::Slow, Some(ActNonverbal::Sigh)),
    ("snapped", ActEmotion::Anger, 2, ActVolume::Loud, ActPace::Fast, None),
    ("muttered", ActEmotion::Neutral, 1, ActVolume::Low, ActPace::Normal, None),
];

/// The acting the tag around a line asks for; flat when it asks for nothing.
pub fn from_tag(tag: &str) -> DramaAct {
    let t = tag.to_lowercase();
    for (w, e, level, v, p, nv) in TAGS {
        if t.contains(w) {
            return DramaAct { emotion: *e, level: *level, volume: *v, pace: *p, nonverbal: nv.iter().copied().collect(), distance: ActDistance::Normal, from_text: true };
        }
    }
    DramaAct::flat()
}

impl DramaAct {
    pub fn flat() -> DramaAct {
        DramaAct { emotion: ActEmotion::Neutral, level: 0, volume: ActVolume::Normal, pace: ActPace::Normal, nonverbal: vec![], distance: ActDistance::Normal, from_text: false }
    }

    /// As one plain instruction, for voices that take one.
    pub fn instruction(&self, chinese: bool) -> String {
        let mut parts: Vec<&str> = Vec::new();
        let e = match (self.emotion, chinese) {
            (ActEmotion::Neutral, _) => "",
            (ActEmotion::Joy, true) => "带着笑意",
            (ActEmotion::Anger, true) => "生气",
            (ActEmotion::Sadness, true) => "难过",
            (ActEmotion::Fear, true) => "害怕",
            (ActEmotion::Surprise, true) => "吃惊",
            (ActEmotion::Disgust, true) => "嫌恶",
            (ActEmotion::Tender, true) => "温柔",
            (ActEmotion::Mocking, true) => "冷笑、讥讽",
            (ActEmotion::Anxious, true) => "着急",
            (ActEmotion::Weary, true) => "疲惫、叹气",
            (ActEmotion::Joy, false) => "amused, smiling",
            (ActEmotion::Anger, false) => "angry",
            (ActEmotion::Sadness, false) => "sad",
            (ActEmotion::Fear, false) => "afraid",
            (ActEmotion::Surprise, false) => "surprised",
            (ActEmotion::Disgust, false) => "disgusted",
            (ActEmotion::Tender, false) => "tender",
            (ActEmotion::Mocking, false) => "mocking",
            (ActEmotion::Anxious, false) => "anxious",
            (ActEmotion::Weary, false) => "weary",
        };
        let strength = match (self.level, chinese) {
            (0, _) => "",
            (1, true) => "稍微",
            (3, true) => "非常",
            (1, false) => "slightly",
            (3, false) => "very",
            _ => "",
        };
        let mut emo = String::new();
        if !e.is_empty() {
            emo = if chinese { format!("{strength}{e}") } else { format!("{strength} {e}").trim().to_string() };
        }
        let vol = match (self.volume, chinese) {
            (ActVolume::Whisper, true) => "压低声音耳语",
            (ActVolume::Low, true) => "小声",
            (ActVolume::Loud, true) => "大声",
            (ActVolume::Shout, true) => "喊出来",
            (ActVolume::Whisper, false) => "whispering",
            (ActVolume::Low, false) => "quietly",
            (ActVolume::Loud, false) => "loudly",
            (ActVolume::Shout, false) => "shouting",
            _ => "",
        };
        let pace = match (self.pace, chinese) {
            (ActPace::Slow, true) => "语速慢",
            (ActPace::Fast, true) => "语速快",
            (ActPace::Slow, false) => "slowly",
            (ActPace::Fast, false) => "quickly",
            _ => "",
        };
        if !emo.is_empty() {
            parts.push(&emo);
        }
        if !vol.is_empty() {
            parts.push(vol);
        }
        if !pace.is_empty() {
            parts.push(pace);
        }
        parts.join(if chinese { "，" } else { ", " })
    }

    /// As inline tags, for voices that read them: `[whispers]`, `[laughs]`.
    pub fn tags(&self) -> (String, String) {
        let mut head = String::new();
        let mut tail = String::new();
        match self.volume {
            ActVolume::Whisper => head.push_str("[whispers] "),
            ActVolume::Shout => head.push_str("[shouting] "),
            _ => {}
        }
        if self.level >= 2 {
            let w = match self.emotion {
                ActEmotion::Anger => "[angry] ",
                ActEmotion::Sadness => "[sad] ",
                ActEmotion::Joy => "[happy] ",
                ActEmotion::Mocking => "[sarcastic] ",
                ActEmotion::Fear => "[scared] ",
                ActEmotion::Surprise => "[surprised] ",
                _ => "",
            };
            head.push_str(w);
        }
        for n in &self.nonverbal {
            let w = match n {
                ActNonverbal::Laugh => "[laughs]",
                ActNonverbal::Sigh => "[sighs]",
                ActNonverbal::Sob => "[crying]",
                ActNonverbal::Gasp => "[gasps]",
                ActNonverbal::Cough => "[coughs]",
            };
            if matches!(n, ActNonverbal::Sigh) {
                tail.push(' ');
                tail.push_str(w);
            } else {
                head.push_str(w);
                head.push(' ');
            }
        }
        (head, tail)
    }
}

#[derive(Clone, Debug, Deserialize)]
pub struct DirectAnswer {
    #[serde(default)]
    pub parts: Vec<DirectPart>,
    #[serde(default)]
    pub events: Vec<super::sound::ModelEvent>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct DirectPart {
    pub p: u32,
    #[serde(default)]
    pub emotion: Option<String>,
    #[serde(default)]
    pub level: Option<u8>,
    #[serde(default)]
    pub volume: Option<String>,
    #[serde(default)]
    pub pace: Option<String>,
    #[serde(default)]
    pub nonverbal: Vec<String>,
    #[serde(default)]
    pub distance: Option<String>,
}

/// A model's acting for a line, one step softer; what the book wrote wins.
pub fn from_model(p: &DirectPart, written: &DramaAct) -> DramaAct {
    if written.from_text {
        return written.clone();
    }
    let emotion = match p.emotion.as_deref() {
        Some("joy") => ActEmotion::Joy,
        Some("anger") => ActEmotion::Anger,
        Some("sadness") => ActEmotion::Sadness,
        Some("fear") => ActEmotion::Fear,
        Some("surprise") => ActEmotion::Surprise,
        Some("disgust") => ActEmotion::Disgust,
        Some("tender") => ActEmotion::Tender,
        Some("mocking") => ActEmotion::Mocking,
        Some("anxious") => ActEmotion::Anxious,
        Some("weary") => ActEmotion::Weary,
        _ => ActEmotion::Neutral,
    };
    let level = p.level.unwrap_or(0).min(3).saturating_sub(1);
    DramaAct {
        emotion: if level == 0 { ActEmotion::Neutral } else { emotion },
        level,
        volume: match p.volume.as_deref() {
            Some("whisper") => ActVolume::Whisper,
            Some("low") => ActVolume::Low,
            Some("loud") => ActVolume::Loud,
            Some("shout") => ActVolume::Loud, // one step softer
            _ => ActVolume::Normal,
        },
        pace: match p.pace.as_deref() {
            Some("slow") => ActPace::Slow,
            Some("fast") => ActPace::Fast,
            _ => ActPace::Normal,
        },
        nonverbal: p
            .nonverbal
            .iter()
            .filter_map(|n| match n.split(':').next().unwrap_or("") {
                "laugh" => Some(ActNonverbal::Laugh),
                "sigh" => Some(ActNonverbal::Sigh),
                "sob" => Some(ActNonverbal::Sob),
                "gasp" => Some(ActNonverbal::Gasp),
                "cough" => Some(ActNonverbal::Cough),
                _ => None,
            })
            .take(1)
            .collect(),
        distance: match p.distance.as_deref() {
            Some("close") => ActDistance::Close,
            Some("far") => ActDistance::Far,
            Some("offstage") => ActDistance::Offstage,
            _ => ActDistance::Normal,
        },
        from_text: false,
    }
}

pub fn direct_schema() -> String {
    json!({
        "type": "object",
        "required": ["parts"],
        "properties": {
            "parts": { "type": "array", "items": { "type": "object", "required": ["p"], "properties": {
                "p": { "type": "integer" },
                "emotion": { "type": "string" }, "level": { "type": "integer" },
                "volume": { "type": "string" }, "pace": { "type": "string" },
                "nonverbal": { "type": "array", "items": { "type": "string" } },
                "distance": { "type": "string" }, "style": { "type": "string" }
            }}},
            "events": { "type": "array", "items": { "type": "object", "required": ["sentence", "after_text", "kind", "evidence"], "properties": {
                "sentence": { "type": "integer" }, "after_text": { "type": "string" },
                "kind": { "type": "string" }, "what": { "type": "string" }, "evidence": { "type": "string" }
            }}}
        }
    })
    .to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn the_book_says_how() {
        let a = from_tag("黛玉冷笑道：");
        assert_eq!((a.emotion, a.level, a.from_text), (ActEmotion::Mocking, 2, true));
        assert_eq!(from_tag("宝玉道：").emotion, ActEmotion::Neutral);
        assert_eq!(from_tag("she whispered").volume, ActVolume::Whisper);
    }

    #[test]
    fn a_models_acting_is_one_step_softer_and_never_overrides_the_book() {
        let p = DirectPart { p: 1, emotion: Some("anger".into()), level: Some(3), volume: Some("shout".into()), pace: None, nonverbal: vec![], distance: None };
        let a = from_model(&p, &DramaAct::flat());
        assert_eq!((a.emotion, a.level, a.volume), (ActEmotion::Anger, 2, ActVolume::Loud));
        let p1 = DirectPart { level: Some(1), ..p.clone() };
        assert_eq!(from_model(&p1, &DramaAct::flat()).emotion, ActEmotion::Neutral);
        let written = from_tag("哭道");
        assert_eq!(from_model(&p, &written), written);
    }

    #[test]
    fn instructions_read_plainly() {
        let a = from_tag("冷笑道");
        assert_eq!(a.instruction(true), "冷笑、讥讽");
        assert_eq!(DramaAct::flat().instruction(true), "");
    }
}
