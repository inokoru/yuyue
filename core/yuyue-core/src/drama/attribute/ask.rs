//! 第二、三层：整场精读和复核. Builds what a model is asked about one scene, and checks what it
//! answers. Nothing it says is taken unchecked: a speaker must be one of the candidates given,
//! a quote must be in the sentence it names, an anchor must be a line already settled, and an
//! answer that doesn't list every line is thrown away whole.

use std::collections::HashMap;

use serde::Deserialize;
use serde_json::json;

use crate::drama::cast::{CastTable, bare};
use crate::drama::scenes::ModelScene;
use crate::drama::{LineBasis, CharacterGender, DramaScene, DramaSpeaker};

pub const ATTRIBUTE_PROMPT: &str = include_str!("../prompts/attribute.v1.txt");
pub const VERIFY_PROMPT: &str = include_str!("../prompts/verify.v1.txt");

/// One line of the scene as the model sees it.
#[derive(Clone, Debug, PartialEq)]
pub struct Slot {
    /// 1-based, in the scene.
    pub q: u32,
    pub utterance: u32,
    /// The sentence the line starts in.
    pub sentence: u32,
    /// Settled already (by the user or the rules): given as known.
    pub known: Option<DramaSpeaker>,
}

/// What's asked about one scene.
#[derive(Clone, Debug)]
pub struct SceneAsk {
    pub scene: DramaScene,
    pub slots: Vec<Slot>,
    /// DramaCharacter ids the speaker may be.
    pub candidates: Vec<u32>,
    pub narrator: Option<u32>,
}

/// The cast as the fixed opening of every request (cached by the provider).
pub fn cast_block(cast: &CastTable) -> String {
    let mut s = String::from("<cast>\n");
    for c in &cast.chars {
        let other: Vec<&str> = c.names.iter().map(String::as_str).filter(|n| *n != c.key()).collect();
        let g = match c.gender {
            CharacterGender::M => "男",
            CharacterGender::F => "女",
            CharacterGender::Unknown => "?",
        };
        s.push_str(&format!("c{} {}", c.id, c.key()));
        if !other.is_empty() {
            s.push_str(&format!("（{}）", other.join("、")));
        }
        s.push_str(&format!(" {g}"));
        if !c.traits.is_empty() {
            s.push_str(&format!(" · {}", c.traits));
        }
        s.push('\n');
    }
    for cb in &cast.called_by {
        s.push_str(&format!("c{} 叫 c{} “{}”\n", cb.by, cb.who, cb.as_));
    }
    s.push_str("</cast>\n");
    s
}

fn speaker_label(s: &DramaSpeaker) -> String {
    match s {
        DramaSpeaker::Char { id } => format!("c{id}"),
        DramaSpeaker::Group { .. } => "GROUP".into(),
        _ => "UNKNOWN".into(),
    }
}

/// The scene's text with each line wrapped and numbered. `segs` are the section's segments as
/// (sentence, start, end, utterance) in characters.
pub fn scene_text(sentences: &[String], first: u32, last: u32, lines: &[(u32, u32, u32, Option<u32>)], q_of: &HashMap<u32, u32>) -> String {
    // A line over several sentences is marked once: opened at its first stretch, closed at its last.
    let mut order: Vec<&(u32, u32, u32, Option<u32>)> = lines.iter().collect();
    order.sort_by_key(|l| (l.0, l.1));
    let first_of = |u: u32| order.iter().position(|l| l.3 == Some(u));
    let last_of = |u: u32| order.iter().rposition(|l| l.3 == Some(u));
    let mut s = String::new();
    for i in first..=last {
        let chars: Vec<char> = sentences[i as usize].chars().collect();
        s.push_str(&format!("[{i}] "));
        for (k, l) in order.iter().enumerate().filter(|(_, l)| l.0 == i) {
            let t: String = chars[(l.1 as usize).min(chars.len())..(l.2 as usize).min(chars.len())].iter().collect();
            match l.3.and_then(|u| q_of.get(&u).map(|q| (u, q))) {
                Some((u, q)) => {
                    if first_of(u) == Some(k) {
                        s.push_str(&format!("⟦q{q}⟧"));
                    }
                    s.push_str(&t);
                    if last_of(u) == Some(k) {
                        s.push_str(&format!("⟦/q{q}⟧"));
                    }
                }
                None => s.push_str(&t),
            }
        }
        s.push('\n');
    }
    s
}

/// The user part of an attribution or verification request. `before` is the previous scene's
/// last sentences with their settled speakers already marked by the caller.
pub fn user_text(cast: &CastTable, ask: &SceneAsk, before: &str, scene: &str, examples: &[String], verify: Option<&[u32]>) -> (String, usize) {
    let head = cast_block(cast);
    let mut s = head.clone();
    if !examples.is_empty() {
        s.push_str("<examples>\n");
        for e in examples {
            s.push_str(e);
            s.push('\n');
        }
        s.push_str("</examples>\n");
    }
    s.push_str(&format!("<scene_before>\n{before}</scene_before>\n<scene>\n{scene}</scene>\n<known>\n"));
    for sl in &ask.slots {
        if let Some(k) = &sl.known {
            s.push_str(&format!("q{}={}\n", sl.q, speaker_label(k)));
        }
    }
    s.push_str("</known>\n<candidates>");
    let mut c: Vec<String> = ask.candidates.iter().map(|id| format!("c{id}")).collect();
    c.push("GROUP".into());
    if ask.narrator.is_some() {
        c.push("NARRATOR".into());
    }
    s.push_str(&c.join(" "));
    s.push_str("</candidates>\n");
    if let Some(v) = verify {
        s.push_str(&format!("<ask>{}</ask>\n", v.iter().map(|q| format!("q{q}")).collect::<Vec<_>>().join(" ")));
    }
    (s, head.len())
}

/// JSON Schema of the attribution answer.
pub fn attribute_schema() -> String {
    json!({
        "type": "object",
        "required": ["quotes", "scenes"],
        "properties": {
            "quotes": { "type": "array", "items": {
                "type": "object",
                "required": ["q", "speaker", "basis"],
                "properties": {
                    "q": { "type": "integer" },
                    "speaker": { "type": "string" },
                    "other_name": { "type": ["string", "null"] },
                    "to": { "type": ["string", "null"] },
                    "basis": { "enum": ["explicit", "inferred", "content"] },
                    "anchor": { "type": ["integer", "null"] },
                    "evidence": { "type": ["object", "null"], "properties": { "sentence": { "type": "integer" }, "quote": { "type": "string" } } }
                }
            }},
            "scenes": { "type": "array", "items": {
                "type": "object",
                "required": ["first", "last"],
                "properties": {
                    "first": { "type": "integer" }, "last": { "type": "integer" },
                    "place": { "type": "string" }, "place_kind": { "type": "string" },
                    "indoor": { "type": ["boolean", "null"] },
                    "time": { "enum": ["day", "night", "dawn", "dusk", "unknown"] },
                    "weather": { "enum": ["clear", "rain", "snow", "wind", "storm", "unknown"] },
                    "crowd": { "enum": ["alone", "few", "many", "unknown"] },
                    "present": { "type": "array", "items": { "type": "object", "properties": { "who": { "type": "string" }, "enter": { "type": ["integer", "null"] }, "leave": { "type": ["integer", "null"] } } } }
                }
            }}
        }
    })
    .to_string()
}

pub fn verify_schema() -> String {
    json!({
        "type": "object",
        "required": ["quotes"],
        "properties": { "quotes": { "type": "array", "items": {
            "type": "object",
            "required": ["q", "speaker"],
            "properties": {
                "q": { "type": "integer" },
                "speaker": { "type": "string" },
                "excluded": { "type": "array", "items": { "type": "string" } }
            }
        }}}
    })
    .to_string()
}

#[derive(Clone, Debug, Deserialize)]
pub struct RawAnswer {
    pub quotes: Vec<RawQuote>,
    #[serde(default)]
    pub scenes: Vec<ModelScene>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct RawQuote {
    pub q: u32,
    pub speaker: String,
    #[serde(default)]
    pub other_name: Option<String>,
    #[serde(default)]
    pub to: Option<String>,
    #[serde(default)]
    pub basis: Option<String>,
    #[serde(default)]
    pub anchor: Option<u32>,
    #[serde(default)]
    pub evidence: Option<RawEvidence>,
    #[serde(default)]
    pub excluded: Vec<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct RawEvidence {
    pub sentence: u32,
    pub quote: String,
}

/// A candidate: one character, or the crowd.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, PartialOrd, Ord)]
pub enum Cand {
    Char(u32),
    Group,
}

/// One line's checked answer.
#[derive(Clone, Debug, PartialEq)]
pub enum Vote {
    Is(Cand),
    NotSpeech,
    Unknown,
    /// Someone the text names who isn't in the cast yet.
    Other(String),
}

#[derive(Clone, Debug, PartialEq)]
pub struct Checked {
    pub utterance: u32,
    pub vote: Vote,
    pub basis: LineBasis,
    pub to: Option<u32>,
    /// For `inferred`: the anchor was a settled line.
    pub anchor_ok: bool,
    pub excluded: Vec<Cand>,
}

/// Why a whole answer was thrown away.
#[derive(Clone, Debug, PartialEq)]
pub enum Rejected {
    Json(String),
    Count { want: usize, got: usize },
}

fn parse_cand(s: &str, ask: &SceneAsk) -> Option<Cand> {
    match s {
        "GROUP" => Some(Cand::Group),
        "NARRATOR" => ask.narrator.map(Cand::Char),
        _ => {
            let id: u32 = s.trim_start_matches('c').parse().ok()?;
            ask.candidates.contains(&id).then_some(Cand::Char(id))
        }
    }
}

/// Checks a model's answer about a scene. Lines whose answer fails a check come back as
/// `Unknown`; an answer that's not JSON or doesn't list each line once is rejected whole.
pub fn check(answer: &str, ask: &SceneAsk, sentences: &[String], cast: &CastTable, verify: bool) -> Result<(Vec<Checked>, Vec<ModelScene>), Rejected> {
    let raw: RawAnswer = serde_json::from_str(strip_fence(answer)).map_err(|e| Rejected::Json(e.to_string()))?;
    let want: Vec<u32> = if verify { raw.quotes.iter().map(|q| q.q).collect() } else { ask.slots.iter().map(|s| s.q).collect() };
    let mut got: Vec<u32> = raw.quotes.iter().map(|q| q.q).collect();
    got.sort();
    got.dedup();
    if got.len() != raw.quotes.len() || (!verify && got.len() != want.len()) || got.iter().any(|q| !ask.slots.iter().any(|s| s.q == *q)) {
        return Err(Rejected::Count { want: want.len(), got: raw.quotes.len() });
    }
    let settled: HashMap<u32, bool> = ask.slots.iter().map(|s| (s.q, s.known.is_some())).collect();
    let mut out = Vec::new();
    for rq in &raw.quotes {
        let slot = ask.slots.iter().find(|s| s.q == rq.q).unwrap();
        let basis = match rq.basis.as_deref() {
            Some("explicit") => LineBasis::Explicit,
            Some("inferred") => LineBasis::Inferred,
            Some("content") => LineBasis::Content,
            _ => LineBasis::None,
        };
        let mut vote = match rq.speaker.as_str() {
            "UNKNOWN" => Vote::Unknown,
            "NOT_SPEECH" => Vote::NotSpeech,
            "OTHER" => match rq.other_name.as_deref().map(str::trim).filter(|n| !n.is_empty()) {
                Some(n) => match cast.resolve(n) {
                    Some(id) => Vote::Is(Cand::Char(id)),
                    None => Vote::Other(n.to_string()),
                },
                None => Vote::Unknown,
            },
            s => parse_cand(s, ask).map(Vote::Is).unwrap_or(Vote::Unknown),
        };
        // Evidence must be in the sentence it names (punctuation aside).
        if !verify && matches!(basis, LineBasis::Explicit | LineBasis::Inferred) {
            let ok = rq.evidence.as_ref().is_some_and(|e| {
                let q = bare(&e.quote);
                !q.is_empty() && sentences.get(e.sentence as usize).is_some_and(|s| bare(s).contains(&q))
            });
            if !ok {
                vote = Vote::Unknown;
            }
        }
        // An Other must be written in the scene's text.
        if let Vote::Other(n) = &vote {
            let inscene = (ask.scene.first..=ask.scene.last).any(|i| sentences[i as usize].contains(n.as_str()));
            if !inscene || basis != LineBasis::Explicit {
                vote = Vote::Unknown;
            }
        }
        let anchor_ok = basis == LineBasis::Inferred && rq.anchor.is_some_and(|a| settled.get(&a).copied().unwrap_or(false) || a < slot.q);
        if basis == LineBasis::Inferred && rq.anchor.is_none() {
            vote = Vote::Unknown;
        }
        let to = rq.to.as_deref().and_then(|t| t.trim_start_matches('c').parse().ok()).filter(|id| ask.candidates.contains(id));
        let excluded = rq.excluded.iter().filter_map(|e| parse_cand(e, ask)).collect();
        out.push(Checked { utterance: slot.utterance, vote, basis, to, anchor_ok, excluded });
    }
    Ok((out, raw.scenes))
}

/// Models wrap JSON in ``` fences now and then.
pub fn strip_fence(s: &str) -> &str {
    let t = s.trim();
    let t = t.strip_prefix("```json").or_else(|| t.strip_prefix("```")).unwrap_or(t);
    t.strip_suffix("```").unwrap_or(t).trim()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;
    use crate::drama::{SceneCrowd, SceneTime, SceneWeather};

    fn ask(cast: &CastTable) -> SceneAsk {
        let scene = DramaScene { section: 0, first: 0, last: 1, place: String::new(), place_kind: "未知".into(), indoor: None, time: SceneTime::Unknown, weather: SceneWeather::Unknown, crowd: SceneCrowd::Unknown, present: cast.chars.iter().map(|c| c.id).collect() };
        SceneAsk {
            scene,
            slots: vec![Slot { q: 1, utterance: 0, sentence: 0, known: Some(DramaSpeaker::Char { id: 1 }) }, Slot { q: 2, utterance: 1, sentence: 1, known: None }],
            candidates: cast.chars.iter().map(|c| c.id).collect(),
            narrator: None,
        }
    }

    #[test]
    fn answers_are_checked_line_by_line() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let a = ask(&cast);
        let ss: Vec<String> = vec!["宝玉道：“你来了。”".into(), "“来了。”她笑着说。".into()];
        let good = r#"{"quotes":[{"q":1,"speaker":"c1","basis":"explicit","evidence":{"sentence":0,"quote":"宝玉道"}},{"q":2,"speaker":"c2","basis":"inferred","anchor":1,"evidence":{"sentence":1,"quote":"她笑着说"}}],"scenes":[]}"#;
        let (c, _) = check(good, &a, &ss, &cast, false).unwrap();
        assert_eq!(c[1].vote, Vote::Is(Cand::Char(2)));
        assert!(c[1].anchor_ok);
        // A quote that isn't in the sentence: that line becomes unknown.
        let lie = good.replace("她笑着说", "黛玉笑道");
        let (c, _) = check(&lie, &a, &ss, &cast, false).unwrap();
        assert_eq!(c[1].vote, Vote::Unknown);
        // Someone not among the candidates.
        let stranger = good.replace(r#""speaker":"c2""#, r#""speaker":"c9""#);
        let (c, _) = check(&stranger, &a, &ss, &cast, false).unwrap();
        assert_eq!(c[1].vote, Vote::Unknown);
        // A line missing: the whole answer goes.
        let short = r#"{"quotes":[{"q":1,"speaker":"c1","basis":"explicit"}],"scenes":[]}"#;
        assert!(matches!(check(short, &a, &ss, &cast, false), Err(Rejected::Count { .. })));
        assert!(matches!(check("not json", &a, &ss, &cast, false), Err(Rejected::Json(_))));
    }

    #[test]
    fn the_request_numbers_lines_and_lists_candidates() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let a = ask(&cast);
        let ss: Vec<String> = vec!["宝玉道：“你来了。”".into(), "“来了。”".into()];
        let lines = vec![(0, 0, 4, None), (0, 4, 10, Some(0)), (1, 0, 5, Some(1))];
        let q: HashMap<u32, u32> = [(0, 1), (1, 2)].into();
        let text = scene_text(&ss, 0, 1, &lines, &q);
        assert!(text.contains("⟦q1⟧“你来了。”⟦/q1⟧"), "{text}");
        // Over two sentences: marked once.
        let ss2: Vec<String> = vec!["他道：“你去。".into(), "我不去。”".into()];
        let l2 = vec![(0, 0, 3, None), (0, 3, 7, Some(0)), (1, 0, 5, Some(0))];
        let q2: HashMap<u32, u32> = [(0, 1)].into();
        let t2 = scene_text(&ss2, 0, 1, &l2, &q2);
        assert_eq!(t2.matches("⟦q1⟧").count(), 1, "{t2}");
        assert_eq!(t2.matches("⟦/q1⟧").count(), 1, "{t2}");
        let (u, head) = user_text(&cast, &a, "", &text, &[], None);
        assert!(u[..head].starts_with("<cast>"));
        assert!(u.contains("q1=c1"));
        assert!(u.contains("<candidates>c1 c2 GROUP</candidates>"));
    }
}
