//! 硬约束: what a speaker can't be, checked by the program and not left to a model's care.
//!
//! 1. Someone not in the scene doesn't speak.
//! 2. A line the user or the rules settled stays settled.
//! 3. Whoever a line calls by name at its start (“妹妹，你来”) isn't the one saying it.
//! 4. 她道 isn't a man; he said isn't a woman (他 is left alone: older Chinese writes it for both).
//! 5. One line is one speaker — the segments of an utterance are one unit already.
//! 6. Whoever a line names as “I” (我王熙凤…, I am Darcy) is the one saying it.

use crate::drama::CharacterGender;
use crate::drama::cast::CastTable;

use super::ask::Cand;

/// What the text around one line says, for the constraints and the soft scores.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct LineFacts {
    /// Called at the line's start: 宝玉，你来.
    pub vocative: Vec<u32>,
    /// Named elsewhere inside the line (not as “I”).
    pub named_inside: Vec<u32>,
    /// Named as “I” inside the line.
    pub self_named: Option<u32>,
    /// The pronoun of the speech tag: 她 / she → F, he → M.
    pub tag_gender: Option<CharacterGender>,
    /// Sentences of narration between the line before and this one (0: right after it).
    pub narration_before: u32,
    /// The line before ends cut off (—— or …): this one breaks in.
    pub interrupts: bool,
}

const SELF: &[&str] = &["我", "俺", "老夫", "本官", "在下", "小的", "奴家", "妾身", "I am ", "I'm ", "My name is "];

/// The facts of a line from its own text (`inner`, without the quote marks) and its tag.
pub fn facts(cast: &CastTable, inner: &str, tag: &str) -> LineFacts {
    let mut f = LineFacts::default();
    let hits = cast.find(inner);
    let chars: Vec<char> = inner.chars().collect();
    for h in &hits {
        let before: String = chars[..h.start].iter().collect();
        let after = chars.get(h.end).copied();
        let is_self = SELF.iter().any(|s| before.trim_end().ends_with(s.trim_end()));
        if is_self && f.self_named.is_none() {
            f.self_named = Some(h.id);
            continue;
        }
        let at_start = before.chars().all(|c| !c.is_alphanumeric());
        if at_start && matches!(after, Some('，' | '！' | ',' | '!' | '：' | '、')) {
            f.vocative.push(h.id);
        } else if !f.named_inside.contains(&h.id) {
            f.named_inside.push(h.id);
        }
    }
    let t = tag.to_lowercase();
    if tag.contains('她') || t.split(|c: char| !c.is_alphabetic()).any(|w| w == "she") {
        f.tag_gender = Some(CharacterGender::F);
    } else if t.split(|c: char| !c.is_alphabetic()).any(|w| w == "he") {
        f.tag_gender = Some(CharacterGender::M);
    }
    f
}

/// Whether `c` may say a line with these facts in a scene with `present`.
pub fn allowed(c: Cand, f: &LineFacts, present: &[u32], cast: &CastTable) -> bool {
    match c {
        Cand::Group => f.self_named.is_none(),
        Cand::Char(id) => {
            if !present.contains(&id) {
                return false;
            }
            if f.vocative.contains(&id) {
                return false;
            }
            if let Some(s) = f.self_named
                && s != id {
                    return false;
                }
            if let (Some(g), Some(ch)) = (f.tag_gender, cast.get(id))
                && ch.gender != CharacterGender::Unknown && ch.gender != g {
                    return false;
                }
            true
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;

    #[test]
    fn who_a_line_calls_isnt_who_says_it() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let f = facts(&cast, "宝玉，你来。", "");
        let bao = cast.resolve("宝玉").unwrap();
        let dai = cast.resolve("黛玉").unwrap();
        assert!(!allowed(Cand::Char(bao), &f, &[bao, dai], &cast));
        assert!(allowed(Cand::Char(dai), &f, &[bao, dai], &cast));
    }

    #[test]
    fn i_names_the_speaker() {
        let cast = from_rules(&["王熙凤".into(), "宝玉".into()]);
        let f = facts(&cast, "我王熙凤什么没见过", "");
        let feng = cast.resolve("王熙凤").unwrap();
        assert_eq!(f.self_named, Some(feng));
        assert!(!allowed(Cand::Char(cast.resolve("宝玉").unwrap()), &f, &[1, 2], &cast));
    }

    #[test]
    fn she_isnt_a_man_and_absent_people_dont_speak() {
        let mut cast = from_rules(&["Mr. Darcy".into(), "Elizabeth".into()]);
        let liz = cast.resolve("Elizabeth").unwrap();
        cast.get_mut(liz).unwrap().gender = CharacterGender::F;
        let darcy = cast.resolve("Mr. Darcy").unwrap();
        let f = facts(&cast, "Indeed.", "she said");
        assert!(!allowed(Cand::Char(darcy), &f, &[darcy, liz], &cast));
        assert!(allowed(Cand::Char(liz), &f, &[darcy, liz], &cast));
        assert!(!allowed(Cand::Char(liz), &f, &[darcy], &cast));
    }
}
