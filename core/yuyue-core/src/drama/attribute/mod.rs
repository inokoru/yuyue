//! 认人: who says each line. Four layers, each stricter than the one before — the rules (what's
//! written), a model reading the whole scene, a second model checking without seeing the first's
//! answers, and a threshold on how often lines like this one turn out wrong. Whatever doesn't
//! clear it is `Unsure`: read in the 对话 voice, and on the list for the user.
//!
//! Who decides, highest first: the user, the rules, the model, nobody.

pub mod ask;
pub mod calibrate;
pub mod constraints;
pub mod rules;
pub mod solve;

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use self::ask::{Cand, Checked, Vote};
use self::calibrate::Fit;
use self::constraints::{LineFacts, facts};
use self::rules::RuleAnswer;
use self::solve::{LineIn, LineOut, solve};
use super::cast::CastTable;
use super::spans::RawSeg;
use super::{LineBasis, LineCandidate, DecidedBy, DramaScene, SegKind, DramaSpeaker};

/// What the user said about a line. Kept by the line's place and first words, so it survives a
/// rules or model upgrade; by name, so it survives the cast being merged or split.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum UserChoice {
    /// Someone in the cast, by any of their names.
    Person { name: String },
    Group { name: String },
    NotSpeech,
}

/// One line decided.
#[derive(Clone, Debug, PartialEq)]
pub struct Decided {
    pub utterance: u32,
    pub speaker: DramaSpeaker,
    pub basis: LineBasis,
    pub decided_by: DecidedBy,
    pub p_correct: f32,
    pub addressee: Option<u32>,
    pub not_speech: bool,
}

/// Everything known about one section's lines.
pub struct SectionIn<'a> {
    pub sentences: &'a [String],
    pub segs: &'a [RawSeg],
    pub rules: &'a [RuleAnswer],
    pub scenes: &'a [DramaScene],
    pub first: &'a HashMap<u32, Checked>,
    pub verify: &'a HashMap<u32, Checked>,
    pub user: &'a HashMap<u32, UserChoice>,
    pub cast: &'a CastTable,
    pub fit: Option<&'a Fit>,
}

/// A line with where it is.
#[derive(Clone, Debug)]
pub struct LineAt {
    pub utterance: u32,
    pub first_sentence: u32,
    pub last_sentence: u32,
    /// The line's own words, quote marks off.
    pub inner: String,
    /// The narration right before and after it in its sentences.
    pub before: String,
    pub after: String,
}

/// The lines of a section, in order.
pub fn lines_of(sentences: &[String], segs: &[RawSeg]) -> Vec<LineAt> {
    let mut out: Vec<LineAt> = Vec::new();
    for (i, s) in segs.iter().enumerate() {
        let Some(u) = s.utterance else { continue };
        if !matches!(s.kind, SegKind::Speech | SegKind::Thought) {
            continue;
        }
        let t = super::spans::seg_text(sentences, s);
        if let Some(l) = out.iter_mut().find(|l| l.utterance == u) {
            l.inner.push_str(&t);
            l.last_sentence = s.sentence;
            l.after = segs.get(i + 1).filter(|n| n.sentence == s.sentence && n.utterance.is_none()).map(|n| super::spans::seg_text(sentences, n)).unwrap_or_default();
            continue;
        }
        let before = i.checked_sub(1).and_then(|p| segs.get(p)).filter(|p| p.sentence == s.sentence && p.utterance.is_none()).map(|p| super::spans::seg_text(sentences, p)).unwrap_or_default();
        let after = segs.get(i + 1).filter(|n| n.sentence == s.sentence && n.utterance.is_none()).map(|n| super::spans::seg_text(sentences, n)).unwrap_or_default();
        out.push(LineAt { utterance: u, first_sentence: s.sentence, last_sentence: s.sentence, inner: t, before, after });
    }
    for l in &mut out {
        l.inner = l.inner.trim_matches(|c: char| "“”「」『』‘’\"«»".contains(c)).to_string();
    }
    out
}

fn narration_between(prev: &LineAt, this: &LineAt) -> u32 {
    let between = this.first_sentence.saturating_sub(prev.last_sentence + 1);
    let here = (this.before.chars().filter(|c| c.is_alphanumeric()).count() >= 2) as u32;
    let there = (this.first_sentence != prev.last_sentence && prev.after.chars().filter(|c| c.is_alphanumeric()).count() >= 2) as u32;
    between + here.max(there)
}

fn rule_cand(r: &RuleAnswer) -> Option<Cand> {
    if r.decided_by != DecidedBy::Rule {
        return None;
    }
    match &r.speaker {
        DramaSpeaker::Char { id } => Some(Cand::Char(*id)),
        DramaSpeaker::Group { .. } => Some(Cand::Group),
        _ => None,
    }
}

fn user_cand(u: &UserChoice, cast: &CastTable) -> Option<Cand> {
    match u {
        UserChoice::Person { name } => cast.resolve(name).map(Cand::Char),
        UserChoice::Group { .. } => Some(Cand::Group),
        UserChoice::NotSpeech => None,
    }
}

/// One scene ready to solve.
pub struct SceneLines {
    pub lines: Vec<LineIn>,
    pub at: Vec<LineAt>,
    pub present: Vec<u32>,
    pub concord: f32,
}

pub fn scene_lines(input: &SectionIn, scene: &DramaScene, all: &[LineAt]) -> SceneLines {
    let at: Vec<LineAt> = all.iter().filter(|l| l.first_sentence >= scene.first && l.first_sentence <= scene.last).cloned().collect();
    let rules: HashMap<u32, &RuleAnswer> = input.rules.iter().map(|r| (r.utterance, r)).collect();
    let mut lines = Vec::new();
    let (mut agree, mut asked) = (0u32, 0u32);
    for (k, l) in at.iter().enumerate() {
        let rule = rules.get(&l.utterance).and_then(|r| rule_cand(r));
        let user = input.user.get(&l.utterance).and_then(|u| user_cand(u, input.cast));
        let fixed = user.or(rule);
        let first = input.first.get(&l.utterance).cloned();
        // The model's word on lines the rules settled: abstaining is no disagreement.
        if let (Some(r), Some(f)) = (rule, &first)
            && matches!(f.vote, Vote::Is(_)) {
                asked += 1;
                if f.vote == Vote::Is(r) {
                    agree += 1;
                }
            }
        let tag = format!("{}{}", l.before, l.after);
        let mut f: LineFacts = facts(input.cast, &l.inner, &tag);
        if k > 0 {
            f.narration_before = narration_between(&at[k - 1], l);
            f.interrupts = at[k - 1].inner.trim_end().ends_with(['—', '…']);
        } else {
            f.narration_before = 9;
        }
        lines.push(LineIn { utterance: l.utterance, fixed, first, verify: input.verify.get(&l.utterance).cloned(), facts: f });
    }
    let concord = if asked == 0 { 1.0 } else { agree as f32 / asked as f32 };
    SceneLines { lines, at, present: scene.present.clone(), concord }
}

fn speaker_of(c: Cand, name_hint: Option<&str>) -> DramaSpeaker {
    match c {
        Cand::Char(id) => DramaSpeaker::Char { id },
        Cand::Group => DramaSpeaker::Group { name: name_hint.map(str::to_string).unwrap_or_else(|| crate::i18n::tr("众人", "Crowd")) },
    }
}

/// How far each line is from the nearest settled one, in lines.
fn from_anchor(lines: &[LineIn]) -> Vec<u32> {
    let fixed: Vec<usize> = lines.iter().enumerate().filter(|(_, l)| l.fixed.is_some()).map(|(i, _)| i).collect();
    (0..lines.len()).map(|i| fixed.iter().map(|&f| (f as i64 - i as i64).unsigned_abs() as u32).min().unwrap_or(99)).collect()
}

fn decide_lines(input: &SectionIn, sl: &SceneLines, outs: &[LineOut]) -> Vec<Decided> {
    let rules: HashMap<u32, &RuleAnswer> = input.rules.iter().map(|r| (r.utterance, r)).collect();
    let dist = from_anchor(&sl.lines);
    let mut out = Vec::new();
    for (k, (l, o)) in sl.lines.iter().zip(outs).enumerate() {
        let u = l.utterance;
        if let Some(choice) = input.user.get(&u) {
            if *choice == UserChoice::NotSpeech {
                out.push(Decided { utterance: u, speaker: DramaSpeaker::Narrator, basis: LineBasis::Explicit, decided_by: DecidedBy::User, p_correct: 1.0, addressee: None, not_speech: true });
                continue;
            }
            if let Some(c) = user_cand(choice, input.cast) {
                let hint = match choice {
                    UserChoice::Group { name } => Some(name.as_str()),
                    _ => None,
                };
                out.push(Decided { utterance: u, speaker: speaker_of(c, hint), basis: LineBasis::Explicit, decided_by: DecidedBy::User, p_correct: 1.0, addressee: None, not_speech: false });
                continue;
            }
        }
        if let Some(r) = rules.get(&u).filter(|r| r.decided_by == DecidedBy::Rule) {
            out.push(Decided { utterance: u, speaker: r.speaker.clone(), basis: r.basis, decided_by: DecidedBy::Rule, p_correct: 1.0, addressee: l.first.as_ref().and_then(|f| f.to), not_speech: false });
            continue;
        }
        let f = calibrate::features(o, l.first.as_ref(), l.verify.as_ref(), sl.concord, sl.present.len() as u32, dist[k]);
        match calibrate::decide(o, l.first.as_ref(), l.verify.as_ref(), &f, input.fit) {
            Some((c, p)) => out.push(Decided {
                utterance: u,
                speaker: speaker_of(c, None),
                basis: l.first.as_ref().map(|a| a.basis).unwrap_or(LineBasis::None),
                decided_by: DecidedBy::Model,
                p_correct: p,
                addressee: l.first.as_ref().and_then(|a| a.to),
                not_speech: false,
            }),
            None => {
                let top = o.post.iter().filter_map(|(c, p)| if let Cand::Char(id) = c { Some(LineCandidate { id: *id, p: *p }) } else { None }).take(2).collect();
                out.push(Decided { utterance: u, speaker: DramaSpeaker::Unsure { top }, basis: LineBasis::None, decided_by: DecidedBy::Unsure, p_correct: o.post.first().map(|p| p.1).unwrap_or(0.0), addressee: None, not_speech: false });
            }
        }
    }
    out
}

/// Every line of the section decided.
pub fn decide_section(input: &SectionIn) -> Vec<Decided> {
    let all = lines_of(input.sentences, input.segs);
    let mut out = Vec::new();
    for scene in input.scenes {
        let sl = scene_lines(input, scene, &all);
        if sl.lines.is_empty() {
            continue;
        }
        let outs = solve(&sl.lines, &sl.present, input.cast);
        out.extend(decide_lines(input, &sl, &outs));
    }
    // Lines outside every scene (shouldn't happen): unsure.
    for l in &all {
        if !out.iter().any(|d| d.utterance == l.utterance) {
            out.push(Decided { utterance: l.utterance, speaker: DramaSpeaker::Unsure { top: vec![] }, basis: LineBasis::None, decided_by: DecidedBy::Unsure, p_correct: 0.0, addressee: None, not_speech: false });
        }
    }
    out
}

/// A line on the 没把握 list, with how many lines settling it would settle.
#[derive(Clone, Debug, PartialEq)]
pub struct ReviewLine {
    pub utterance: u32,
    pub top: Vec<LineCandidate>,
    /// Lines that would clear the threshold if this one were settled as its top candidate.
    pub frees: u32,
}

/// The unsure lines of a section, the most useful to settle first.
pub fn review(input: &SectionIn) -> Vec<ReviewLine> {
    let all = lines_of(input.sentences, input.segs);
    let mut out = Vec::new();
    for scene in input.scenes {
        let sl = scene_lines(input, scene, &all);
        if sl.lines.is_empty() {
            continue;
        }
        let outs = solve(&sl.lines, &sl.present, input.cast);
        let base = decide_lines(input, &sl, &outs);
        let unsure_before = base.iter().filter(|d| d.decided_by == DecidedBy::Unsure).count() as u32;
        for (k, d) in base.iter().enumerate() {
            let DramaSpeaker::Unsure { top } = &d.speaker else { continue };
            let frees = match top.first() {
                Some(c) => {
                    let mut lines = sl.lines.clone();
                    lines[k].fixed = Some(Cand::Char(c.id));
                    let trial = SceneLines { lines, at: sl.at.clone(), present: sl.present.clone(), concord: sl.concord };
                    let o2 = solve(&trial.lines, &trial.present, input.cast);
                    let mut user = input.user.clone();
                    if let Some(name) = input.cast.get(c.id).map(|c| c.key().to_string()) {
                        user.insert(d.utterance, UserChoice::Person { name });
                    }
                    let inp = SectionIn { user: &user, ..*input };
                    let after = decide_lines(&inp, &trial, &o2).iter().filter(|x| x.decided_by == DecidedBy::Unsure).count() as u32;
                    unsure_before.saturating_sub(after + 1)
                }
                None => 0,
            };
            out.push(ReviewLine { utterance: d.utterance, top: top.clone(), frees });
        }
    }
    out.sort_by(|a, b| b.frees.cmp(&a.frees).then(a.utterance.cmp(&b.utterance)));
    out
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;
    use crate::drama::scenes::rule_scenes;
    use crate::drama::spans::split;
    use crate::script::Cast as RuleCast;

    #[test]
    fn a_whole_section_decided_and_the_users_word_wins() {
        let ss: Vec<String> = vec!["宝玉笑道：“妹妹好。”".into(), "黛玉道：“你也好。”".into(), "“今日去哪？”".into(), "“园子里。”".into()];
        let rc = RuleCast::read(&[ss.clone(), ss.clone(), ss.clone()]);
        let mut cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let segs = split(&ss);
        let rules = rules::attribute(&rc, &mut cast, &ss, &segs);
        let scenes = rule_scenes(0, &ss, &cast);
        let empty = HashMap::new();
        let mut user = HashMap::new();
        let third = {
            let input = SectionIn { sentences: &ss, segs: &segs, rules: &rules, scenes: &scenes, first: &empty, verify: &empty, user: &user, cast: &cast, fit: None };
            let d = decide_section(&input);
            assert_eq!(d.len(), 4);
            assert_eq!(d[0].decided_by, DecidedBy::Rule);
            // No model: the two unwritten lines stay unsure, however likely the turn-taking makes them.
            assert_eq!(d[2].decided_by, DecidedBy::Unsure);
            assert_eq!(review(&input).len(), 2);
            d[2].utterance
        };
        user.insert(third, UserChoice::Person { name: "宝玉".into() });
        let input = SectionIn { sentences: &ss, segs: &segs, rules: &rules, scenes: &scenes, first: &empty, verify: &empty, user: &user, cast: &cast, fit: None };
        let d = decide_section(&input);
        assert_eq!(d[2].decided_by, DecidedBy::User);
        assert_eq!(d[2].speaker, DramaSpeaker::Char { id: cast.resolve("宝玉").unwrap() });
    }
}
