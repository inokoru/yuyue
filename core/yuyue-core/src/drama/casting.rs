//! 选角: a voice for each character, solved at once.
//!
//!   total = Σ fit(character, voice) × lines − λ · Σ together(a, b) × alike(voice a, voice b)
//!
//! The leads (the K with most lines) each get a voice of their own — greedy by lines, then
//! swaps while any helps. Those with a handful of lines take turns from a few “bit-part”
//! voices per gender and age, never the same as someone in the same scene. A voice must speak
//! the book's language; a man's voice for a woman costs a lot, an unknown gender nothing.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use super::{CharacterAge, DramaCharacter, CharacterGender};

/// A voice the device can use, measured once (`embedding`, loudness) by the app.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct VoiceInfo {
    /// Engine and voice, as the app knows it.
    pub id: String,
    pub langs: Vec<String>,
    pub gender: CharacterGender,
    pub age: CharacterAge,
    pub tags: Vec<String>,
    pub embedding: Vec<f32>,
    /// Takes instructions (语气).
    pub acts: bool,
    pub narrator_ok: bool,
    pub loudness_lufs: f32,
}

/// How strongly sounding alike is avoided between people in the same scenes.
pub const LAMBDA: f32 = 2.0;
/// The most leads with a voice of their own.
pub const MAX_LEADS: usize = 24;
/// Bit-part voices kept per gender and age band.
pub const BIT_VOICES: usize = 3;

pub fn alike(a: &[f32], b: &[f32]) -> f32 {
    if a.len() != b.len() || a.is_empty() {
        return 0.0;
    }
    let dot: f32 = a.iter().zip(b).map(|(x, y)| x * y).sum();
    let na: f32 = a.iter().map(|x| x * x).sum::<f32>().sqrt();
    let nb: f32 = b.iter().map(|x| x * x).sum::<f32>().sqrt();
    if na == 0.0 || nb == 0.0 { 0.0 } else { (dot / (na * nb)).max(0.0) }
}

fn age_rank(a: CharacterAge) -> Option<i32> {
    Some(match a {
        CharacterAge::Child => 0,
        CharacterAge::Teen => 1,
        CharacterAge::Young => 2,
        CharacterAge::Middle => 3,
        CharacterAge::Old => 4,
        CharacterAge::Unknown => return None,
    })
}

pub fn fit(c: &DramaCharacter, v: &VoiceInfo) -> f32 {
    let mut s = 0.0;
    match (c.gender, v.gender) {
        (CharacterGender::Unknown, _) | (_, CharacterGender::Unknown) => {}
        (a, b) if a == b => s += 3.0,
        _ => s -= 10.0,
    }
    if let (Some(a), Some(b)) = (age_rank(c.age), age_rank(v.age)) {
        s += match (a - b).abs() {
            0 => 2.0,
            1 => 1.0,
            _ => -1.0,
        };
    } else if c.age == CharacterAge::Unknown && matches!(v.age, CharacterAge::Old | CharacterAge::Child) {
        // An old man's or a child's voice only for someone known to be one.
        s -= 1.0;
    }
    if v.acts {
        s += 1.0;
    }
    s
}

/// Who's in scenes together, as lines said by both in the same scenes.
pub type Together = HashMap<(u32, u32), u32>;

pub fn together_key(a: u32, b: u32) -> (u32, u32) {
    if a < b { (a, b) } else { (b, a) }
}

/// The plan: character → voice id; the narrator's voice; who's sharing bit-part voices.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct CastingPlan {
    pub narrator: String,
    /// The voice for lines nobody's sure of (对话).
    pub dialogue: String,
    pub voices: Vec<CastVoice>,
    /// Leads no voice suits: a voice to design from this description.
    pub to_design: Vec<DesignAsk>,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct CastVoice {
    pub character: u32,
    pub voice: String,
    pub lead: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DesignAsk {
    pub character: u32,
    pub description: String,
}

fn score(chars: &[&DramaCharacter], pick: &[usize], voices: &[VoiceInfo], together: &Together) -> f32 {
    let mut s = 0.0;
    for (i, c) in chars.iter().enumerate() {
        s += fit(c, &voices[pick[i]]) * c.lines as f32;
    }
    for i in 0..chars.len() {
        for j in i + 1..chars.len() {
            let t = *together.get(&together_key(chars[i].id, chars[j].id)).unwrap_or(&0) as f32;
            if t > 0.0 {
                s -= LAMBDA * t * alike(&voices[pick[i]].embedding, &voices[pick[j]].embedding);
            }
        }
    }
    s
}

/// Casts the book. `fixed` are the user's own choices, never moved; `narrator` the voice the
/// user reads with; `lang` the book's language.
pub fn cast(chars: &[DramaCharacter], voices: &[VoiceInfo], together: &Together, fixed: &HashMap<u32, String>, narrator: &str, lang: &str) -> CastingPlan {
    // A voice speaks the book's language when their main language is the same (zh-CN, zh-TW and
    // zh-Hans are all zh).
    let main = |t: &str| t.split(['-', '_']).next().unwrap_or("").to_lowercase();
    let usable: Vec<VoiceInfo> = voices.iter().filter(|v| v.langs.iter().any(|l| l == "*" || main(l) == main(lang))).cloned().collect();
    let narr = usable.iter().find(|v| v.id == narrator).or_else(|| usable.iter().find(|v| v.narrator_ok)).or(usable.first()).map(|v| v.id.clone()).unwrap_or_else(|| narrator.to_string());
    // 对话: an unmarked voice, not the narrator's, not anyone's.
    let mut speaking: Vec<&DramaCharacter> = chars.iter().filter(|c| c.lines > 0 && !c.narrator).collect();
    speaking.sort_by(|a, b| b.lines.cmp(&a.lines).then(a.id.cmp(&b.id)));
    // The narrator character (我) speaks in the narrator's voice.
    let mut out: Vec<CastVoice> = chars.iter().filter(|c| c.narrator).map(|c| CastVoice { character: c.id, voice: narr.clone(), lead: true }).collect();
    let pool: Vec<usize> = (0..usable.len()).filter(|&i| usable[i].id != narr).collect();
    let k = MAX_LEADS.min(pool.len().saturating_sub(BIT_VOICES.min(pool.len() / 3))).min(speaking.len());
    let leads: Vec<&DramaCharacter> = speaking[..k].to_vec();
    // Greedy: most lines first, the best voice not yet taken.
    let mut pick: Vec<usize> = Vec::new();
    let mut taken: Vec<usize> = Vec::new();
    for c in &leads {
        if let Some(v) = fixed.get(&c.id).and_then(|id| usable.iter().position(|u| &u.id == id)) {
            pick.push(v);
            taken.push(v);
            continue;
        }
        let best = pool
            .iter()
            .copied()
            .filter(|i| !taken.contains(i))
            .max_by(|&a, &b| {
                let pen = |v: usize| -> f32 {
                    leads.iter().zip(&pick).map(|(o, &pv)| *together.get(&together_key(o.id, c.id)).unwrap_or(&0) as f32 * alike(&usable[v].embedding, &usable[pv].embedding)).sum::<f32>() * LAMBDA
                };
                (fit(c, &usable[a]) * c.lines as f32 - pen(a)).partial_cmp(&(fit(c, &usable[b]) * c.lines as f32 - pen(b))).unwrap_or(std::cmp::Ordering::Equal)
            });
        match best {
            Some(v) => {
                pick.push(v);
                taken.push(v);
            }
            None => {
                pick.push(pool.first().copied().unwrap_or(0));
            }
        }
    }
    // Swaps (and swaps with unused voices) while the total rises.
    if !usable.is_empty() && !leads.is_empty() {
        let mut best = score(&leads, &pick, &usable, together);
        let mut improved = true;
        let mut rounds = 0;
        while improved && rounds < 50 {
            improved = false;
            rounds += 1;
            for i in 0..leads.len() {
                if fixed.contains_key(&leads[i].id) {
                    continue;
                }
                for j in i + 1..leads.len() {
                    if fixed.contains_key(&leads[j].id) {
                        continue;
                    }
                    pick.swap(i, j);
                    let s = score(&leads, &pick, &usable, together);
                    if s > best + 1e-4 {
                        best = s;
                        improved = true;
                    } else {
                        pick.swap(i, j);
                    }
                }
                for &v in &pool {
                    if pick.contains(&v) {
                        continue;
                    }
                    let old = pick[i];
                    pick[i] = v;
                    let s = score(&leads, &pick, &usable, together);
                    if s > best + 1e-4 {
                        best = s;
                        improved = true;
                    } else {
                        pick[i] = old;
                    }
                }
            }
        }
    }
    let mut to_design = Vec::new();
    for (c, &v) in leads.iter().zip(&pick) {
        if let Some(id) = fixed.get(&c.id) {
            out.push(CastVoice { character: c.id, voice: id.clone(), lead: true });
        } else if let Some(u) = usable.get(v) {
            if fit(c, u) < 0.0 {
                to_design.push(DesignAsk { character: c.id, description: describe(c) });
            }
            out.push(CastVoice { character: c.id, voice: u.id.clone(), lead: true });
        }
    }
    // Bit parts: from the voices nobody leads with, by fit, avoiding scene-mates' voices.
    let spare: Vec<usize> = pool.iter().copied().filter(|v| !pick.contains(v)).collect();
    let spare = if spare.is_empty() { pool.clone() } else { spare };
    // 对话 reads lines nobody's sure of, men's and women's alike: a voice of neither sex, else the
    // narrator's own (one voice rather than the wrong one).
    let neither = |v: &usize| usable[*v].gender == CharacterGender::Unknown;
    let dialogue = spare.iter().find(|v| neither(v)).or_else(|| pool.iter().find(|v| neither(v))).map(|&v| usable[v].id.clone()).unwrap_or_else(|| narr.clone());
    for c in &speaking[k..] {
        if let Some(id) = fixed.get(&c.id) {
            out.push(CastVoice { character: c.id, voice: id.clone(), lead: false });
            continue;
        }
        let mates: Vec<&str> = out.iter().filter(|o| together.contains_key(&together_key(o.character, c.id))).map(|o| o.voice.as_str()).collect();
        let mut cands: Vec<usize> = spare.clone();
        cands.sort_by(|&a, &b| fit(c, &usable[b]).partial_cmp(&fit(c, &usable[a])).unwrap_or(std::cmp::Ordering::Equal));
        let v = cands.iter().find(|&&v| !mates.contains(&usable[v].id.as_str())).or(cands.first()).map(|&v| usable[v].id.clone()).unwrap_or_else(|| dialogue.clone());
        out.push(CastVoice { character: c.id, voice: v, lead: false });
    }
    // 宁可不分，不要分错: a voice of one's own only where the person's sex and the voice's are both
    // known and the same; anyone else is read in the 对话 voice.
    for o in out.iter_mut() {
        let Some(c) = chars.iter().find(|c| c.id == o.character) else { continue };
        if c.narrator || fixed.contains_key(&c.id) {
            continue;
        }
        let sure = usable.iter().find(|u| u.id == o.voice).is_some_and(|u| c.gender != CharacterGender::Unknown && u.gender == c.gender);
        if !sure {
            o.voice = dialogue.clone();
        }
    }
    CastingPlan { narrator: narr, dialogue, voices: out, to_design }
}

/// Words in a name or title that say a person is a woman, or a man.
const WOMAN: &[&str] = &["姐", "妹", "嫂", "娘", "母", "姨", "婆", "媽", "妈", "嬤", "嬷", "奶奶", "夫人", "姑娘", "小姐", "太太", "丫頭", "丫头", "丫鬟", "妃", "皇后", "女"];
const MAN: &[&str] = &["爺", "爷", "公子", "老公", "父", "爹", "兄", "哥", "弟", "叔", "伯", "先生", "少爺", "少爷", "老爺", "老爷", "和尚", "道士", "皇帝", "王爺", "王爷", "相公"];

/// A person's sex when the model hasn't said: by their names and titles (凤姐, 李嬷嬷, 贾老爷),
/// else, in a book that writes 她, by the pronoun that follows their name — and only when it's
/// clear. Unknown otherwise.
pub fn guess_gender(c: &DramaCharacter, sentences: &[Vec<String>]) -> CharacterGender {
    if c.gender != CharacterGender::Unknown {
        return c.gender;
    }
    let w = c.names.iter().any(|n| WOMAN.iter().any(|x| n.ends_with(x)));
    let m = c.names.iter().any(|n| MAN.iter().any(|x| n.ends_with(x)));
    match (w, m) {
        (true, false) => return CharacterGender::F,
        (false, true) => return CharacterGender::M,
        _ => {}
    }
    // 他 / 她 soon after the name, in the same sentence.
    let (mut he, mut she) = (0u32, 0u32);
    for s in sentences.iter().flatten() {
        for n in c.names.iter().filter(|n| n.chars().count() >= 2) {
            let mut from = 0;
            while let Some(i) = s[from..].find(n.as_str()) {
                let after: String = s[from + i + n.len()..].chars().take(12).collect();
                match (after.find('他'), after.find('她')) {
                    (Some(a), Some(b)) if a < b => he += 1,
                    (Some(_), None) => he += 1,
                    (_, Some(_)) => she += 1,
                    _ => {}
                }
                from += i + n.len();
            }
        }
    }
    // A book that never writes 她 says nothing this way.
    let total = he + she;
    if total >= 3 && she * 5 >= total * 4 {
        CharacterGender::F
    } else if total >= 3 && she > 0 && he * 5 >= total * 4 {
        CharacterGender::M
    } else {
        CharacterGender::Unknown
    }
}

/// A description to design a voice from.
pub fn describe(c: &DramaCharacter) -> String {
    let g = match c.gender {
        CharacterGender::M => "男声",
        CharacterGender::F => "女声",
        CharacterGender::Unknown => "",
    };
    let a = match c.age {
        CharacterAge::Child => "孩童",
        CharacterAge::Teen => "少年",
        CharacterAge::Young => "青年",
        CharacterAge::Middle => "中年",
        CharacterAge::Old => "老年",
        CharacterAge::Unknown => "",
    };
    format!("{a}{g}。{}", c.traits).trim_matches('。').to_string()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::DramaCharacter;

    fn v(id: &str, g: CharacterGender, emb: Vec<f32>) -> VoiceInfo {
        VoiceInfo { id: id.into(), langs: vec!["zh".into()], gender: g, age: CharacterAge::Unknown, tags: vec![], embedding: emb, acts: false, narrator_ok: id == "n", loudness_lufs: -20.0 }
    }

    fn ch(id: u32, g: CharacterGender, lines: u32) -> DramaCharacter {
        let mut c = DramaCharacter::named(id, &format!("人{id}"), false);
        c.gender = g;
        c.lines = lines;
        c
    }

    #[test]
    fn genders_match_and_scene_mates_sound_apart() {
        let voices = vec![
            v("n", CharacterGender::M, vec![1.0, 0.0, 0.0]),
            v("m1", CharacterGender::M, vec![1.0, 0.0, 0.0]),
            v("m2", CharacterGender::M, vec![0.0, 1.0, 0.0]),
            v("f1", CharacterGender::F, vec![0.0, 0.0, 1.0]),
            v("f2", CharacterGender::F, vec![0.0, 0.1, 1.0]),
            v("x", CharacterGender::Unknown, vec![0.5, 0.5, 0.5]),
        ];
        let chars = vec![ch(1, CharacterGender::M, 100), ch(2, CharacterGender::M, 90), ch(3, CharacterGender::F, 80)];
        let mut together = Together::new();
        together.insert(together_key(1, 2), 50);
        let plan = cast(&chars, &voices, &together, &HashMap::new(), "n", "zh");
        let of = |id: u32| plan.voices.iter().find(|x| x.character == id).unwrap().voice.clone();
        assert!(of(1).starts_with('m') && of(2).starts_with('m'));
        assert_ne!(of(1), of(2));
        assert!(of(3).starts_with('f'));
        assert_eq!(plan.narrator, "n");
        assert!(plan.voices.iter().all(|x| x.voice != "n"));
    }

    #[test]
    fn the_users_choice_stays() {
        let voices = vec![v("n", CharacterGender::M, vec![1.0]), v("m1", CharacterGender::M, vec![1.0]), v("f1", CharacterGender::F, vec![0.5])];
        let chars = vec![ch(1, CharacterGender::M, 100)];
        let fixed: HashMap<u32, String> = [(1, "f1".to_string())].into();
        let plan = cast(&chars, &voices, &Together::new(), &fixed, "n", "zh");
        assert_eq!(plan.voices[0].voice, "f1");
        assert!(plan.to_design.is_empty());
    }

    #[test]
    fn sex_from_titles_and_pronouns_only_when_clear() {
        let named = |n: &str| DramaCharacter::named(9, n, false);
        assert_eq!(guess_gender(&named("鳳姐"), &[]), CharacterGender::F);
        assert_eq!(guess_gender(&named("李嬤嬤"), &[]), CharacterGender::F);
        assert_eq!(guess_gender(&named("賈老爺"), &[]), CharacterGender::M);
        assert_eq!(guess_gender(&named("寶玉"), &[vec!["寶玉笑道：他來了。".into()]]), CharacterGender::Unknown);
        let modern = vec![vec!["小林说她累了。".into(), "小林看着窗外，她想。".into(), "小林把书给她。".into()]];
        assert_eq!(guess_gender(&named("小林"), &modern), CharacterGender::F);
    }

    #[test]
    fn a_voice_of_ones_own_only_when_the_sex_is_sure() {
        let chars = vec![ch(1, CharacterGender::Unknown, 30), ch(2, CharacterGender::F, 20)];
        let v = |id: &str, g: CharacterGender| VoiceInfo { id: id.into(), langs: vec!["zh-CN".into()], gender: g, age: CharacterAge::Unknown, tags: vec![], embedding: vec![], acts: false, narrator_ok: true, loudness_lufs: -20.0 };
        let voices = vec![v("n", CharacterGender::F), v("a", CharacterGender::M), v("b", CharacterGender::F), v("u", CharacterGender::Unknown)];
        let plan = cast(&chars, &voices, &Together::new(), &HashMap::new(), "n", "zh");
        let of = |c: u32| plan.voices.iter().find(|x| x.character == c).map(|x| x.voice.clone()).unwrap();
        assert_eq!(of(1), plan.dialogue, "{plan:?}");
        // Neither-sex voice available: that's 对话.
        assert_eq!(plan.dialogue, "u", "{plan:?}");
        assert_eq!(of(2), "b", "{plan:?}");
    }

    #[test]
    fn regional_voices_speak_the_books_language() {
        let v = |id: &str, lang: &str, g: CharacterGender| VoiceInfo { id: id.into(), langs: vec![lang.into()], gender: g, age: CharacterAge::Unknown, tags: vec![], embedding: vec![], acts: false, narrator_ok: true, loudness_lufs: -20.0 };
        let chars = vec![ch(1, CharacterGender::M, 30), ch(2, CharacterGender::F, 20)];
        let voices = vec![v("n", "zh-CN", CharacterGender::F), v("a", "zh-CN", CharacterGender::M), v("b", "zh-TW", CharacterGender::F), v("e", "en-US", CharacterGender::M)];
        let plan = cast(&chars, &voices, &Together::new(), &HashMap::new(), "n", "zh");
        let of = |c: u32| plan.voices.iter().find(|x| x.character == c).map(|x| x.voice.clone());
        assert_eq!(of(1).as_deref(), Some("a"), "{plan:?}");
        assert_eq!(of(2).as_deref(), Some("b"), "{plan:?}");
    }
}
