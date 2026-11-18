//! 考卷: the play's lines against answers written by hand (`fixtures/speakers/*.jsonl`).
//!
//! Each answer file starts with a head line `{book, section, …}` and then one line per quote:
//! `{s, e, q, who, kind}` — the sentence it starts in, the sentence it ends in, its first words,
//! who says it (several joined by 、 for a group), and how the book tells it (`named`, `zero`,
//! `pronoun`, `group`, `unnamed`, `not_speech`). `ambiguous: true` marks a line the person
//! marking it wasn't sure of either: never counted wrong, only expected unsure.

use serde::Deserialize;

use super::api::DramaSection;
use super::{DecidedBy, SegKind, DramaSpeaker};
use super::cast::CastTable;

#[derive(Clone, Debug, Deserialize)]
pub struct Head {
    pub book: String,
    pub section: u32,
}

#[derive(Clone, Debug, Deserialize)]
pub struct Gold {
    pub s: u32,
    pub q: String,
    pub who: String,
    pub kind: String,
    #[serde(default)]
    pub ambiguous: bool,
}

pub fn read(text: &str) -> Option<(Head, Vec<Gold>)> {
    let mut lines = text.lines().filter(|l| !l.trim().is_empty());
    let head: Head = serde_json::from_str(lines.next()?).ok()?;
    let rows = lines.filter_map(|l| serde_json::from_str(l).ok()).collect();
    Some((head, rows))
}

/// One wrong answer, for the report.
#[derive(Clone, Debug, PartialEq)]
pub struct Miss {
    pub sentence: u32,
    pub head: String,
    pub want: String,
    pub got: String,
    pub by: DecidedBy,
}

#[derive(Clone, Debug, Default, PartialEq)]
pub struct Score {
    pub lines: u32,
    pub right: u32,
    pub wrong: Vec<Miss>,
    pub unsure: u32,
    /// Lines the gold says aren't speech that got a speaker.
    pub not_speech_voiced: u32,
    /// Quotes in the gold the play doesn't have as a line.
    pub missed: u32,
    pub rule_wrong: u32,
    pub by_kind: Vec<(String, u32, u32, u32)>,
}

impl Score {
    pub fn add(&mut self, o: &Score) {
        self.lines += o.lines;
        self.right += o.right;
        self.wrong.extend(o.wrong.iter().cloned());
        self.unsure += o.unsure;
        self.not_speech_voiced += o.not_speech_voiced;
        self.missed += o.missed;
        self.rule_wrong += o.rule_wrong;
        for (k, r, w, u) in &o.by_kind {
            match self.by_kind.iter_mut().find(|x| &x.0 == k) {
                Some(x) => {
                    x.1 += r;
                    x.2 += w;
                    x.3 += u;
                }
                None => self.by_kind.push((k.clone(), *r, *w, *u)),
            }
        }
    }

    /// Of the lines given a speaker, the share wrong.
    pub fn error_rate(&self) -> f64 {
        let given = self.right + self.wrong.len() as u32;
        if given == 0 { 0.0 } else { self.wrong.len() as f64 / given as f64 }
    }

    /// Of all lines, the share given a speaker.
    pub fn coverage(&self) -> f64 {
        if self.lines == 0 { 0.0 } else { (self.right + self.wrong.len() as u32) as f64 / self.lines as f64 }
    }
}

fn same(a: &str, b: &str) -> bool {
    crate::script::same_person(a, b)
}

/// Scores a section's play against its answers.
pub fn score(play: &DramaSection, cast: &CastTable, sentences: &[String], gold: &[Gold]) -> Score {
    let mut sc = Score::default();
    for g in gold {
        // The line: the first speech stretch in sentence `s` whose text (quote marks off) starts
        // with the answer's first words.
        let seg = play.segments.iter().find(|x| {
            x.sentence == g.s && x.utterance.is_some() && x.text.trim_start_matches(|c: char| "“「『‘\"".contains(c)).starts_with(g.q.as_str())
        });
        let seg = seg.or_else(|| play.segments.iter().find(|x| x.sentence == g.s && x.utterance.is_some() && sentences[g.s as usize].contains(g.q.as_str()) && x.text.contains(g.q.chars().take(4).collect::<String>().as_str())));
        if g.kind == "not_speech" {
            if seg.is_some_and(|x| x.kind == SegKind::Speech && !matches!(x.speaker, DramaSpeaker::Unsure { .. } | DramaSpeaker::Narrator)) {
                sc.not_speech_voiced += 1;
            }
            continue;
        }
        sc.lines += 1;
        let entry = |sc: &mut Score, k: &str, r: u32, w: u32, u: u32| match sc.by_kind.iter_mut().find(|x| x.0 == k) {
            Some(x) => {
                x.1 += r;
                x.2 += w;
                x.3 += u;
            }
            None => sc.by_kind.push((k.to_string(), r, w, u)),
        };
        let Some(seg) = seg else {
            sc.missed += 1;
            sc.unsure += 1;
            entry(&mut sc, &g.kind, 0, 0, 1);
            continue;
        };
        let got = match &seg.speaker {
            DramaSpeaker::Char { id } => cast.get(*id).map(|c| c.names.clone()),
            DramaSpeaker::Group { name } => Some(vec![name.clone()]),
            _ => None,
        };
        let Some(names) = got else {
            sc.unsure += 1;
            entry(&mut sc, &g.kind, 0, 0, 1);
            continue;
        };
        let wants: Vec<&str> = g.who.split('、').collect();
        let is_group = matches!(seg.speaker, DramaSpeaker::Group { .. });
        let ok = names.iter().any(|n| same(n, &g.who) || wants.iter().any(|w| same(n, w))) || (is_group && (wants.len() > 1 || g.kind == "group"));
        if ok {
            sc.right += 1;
            entry(&mut sc, &g.kind, 1, 0, 0);
        } else if g.ambiguous {
            sc.unsure += 1;
            entry(&mut sc, &g.kind, 0, 0, 1);
        } else {
            if seg.decided_by == DecidedBy::Rule {
                sc.rule_wrong += 1;
            }
            sc.wrong.push(Miss { sentence: g.s, head: g.q.clone(), want: g.who.clone(), got: names.join("/"), by: seg.decided_by });
            entry(&mut sc, &g.kind, 0, 1, 0);
        }
    }
    sc
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::api::YuyueCore;

    /// 红楼梦 第八回 through the whole play, rules only: the rules never name the wrong one.
    #[test]
    fn hongloumeng_eight_rules_only() {
        let dir = std::env::temp_dir().join(format!("yuyue-drama-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        let (head, gold) = read(&std::fs::read_to_string(format!("{root}/../../fixtures/speakers/hongloumeng-08.jsonl")).unwrap()).unwrap();
        let t0 = std::time::Instant::now();
        let b = core.book_drama(&book.id).unwrap();
        let whole = t0.elapsed();
        let play = core.drama_section(book.id.clone(), head.section).unwrap();
        let s = score(&play, &b.cast, &b.sentences[head.section as usize], &gold);
        std::fs::remove_dir_all(dir).ok();
        println!("第八回（只用规则）：台词 {}，认对 {}，认错 {}，没把握 {}，漏了 {}；整本规则 {:?}", s.lines, s.right, s.wrong.len(), s.unsure, s.missed, whole);
        for (k, r, w, u) in &s.by_kind {
            println!("  {k}: 对 {r} 错 {w} 没把握 {u}");
        }
        assert_eq!(s.rule_wrong, 0, "规则认错：{:?}", s.wrong);
        assert!(s.wrong.is_empty(), "{:?}", s.wrong);
        assert_eq!(s.not_speech_voiced, 0);
    }
}

#[cfg(test)]
mod model_loop {
    use super::*;
    use crate::api::YuyueCore;
    use crate::drama::api::{DramaCaps, DramaResult, DramaTask, LlmTier};
    use std::collections::HashMap;

    /// A stand-in model that answers from the answer sheet: who it knows from the gold, UNKNOWN
    /// otherwise. `lie_every` makes every n-th answer wrong, to see the checks hold.
    fn answer(user: &str, gold: &[Gold], sentences: &[String], lie_every: usize, verify: bool) -> String {
        let mut ids: HashMap<String, String> = HashMap::new();
        let mut in_cast = false;
        for l in user.lines() {
            if l == "<cast>" { in_cast = true; continue; }
            if l == "</cast>" { in_cast = false; continue; }
            if in_cast && l.starts_with('c') {
                let mut it = l.splitn(2, ' ');
                let id = it.next().unwrap().to_string();
                let rest = it.next().unwrap_or("");
                let names = rest.split(' ').next().unwrap_or("");
                for n in names.replace('（', "、").replace('）', "").split('、') {
                    ids.entry(n.to_string()).or_insert(id.clone());
                }
            }
        }
        let cands: Vec<&str> = user.split("<candidates>").nth(1).and_then(|x| x.split("</candidates>").next()).unwrap_or("").split(' ').collect();
        let scene = user.split("<scene>\n").nth(1).and_then(|x| x.split("</scene>").next()).unwrap_or("");
        let mut quotes = Vec::new();
        let mut k = 0;
        for line in scene.lines() {
            let Some(rest) = line.strip_prefix('[') else { continue };
            let Some((num, text)) = rest.split_once("] ") else { continue };
            let s: u32 = num.parse().unwrap();
            let mut t = text;
            while let Some(a) = t.find("⟦q") {
                let after = &t[a + "⟦q".len()..];
                let close = after.find('⟧').unwrap();
                let q: u32 = after[..close].parse().unwrap();
                let body = &after[close + '⟧'.len_utf8()..];
                let body_text: String = body.split("⟦/q").next().unwrap().trim_start_matches(|c: char| "“「『\"".contains(c)).to_string();
                if !body.contains("⟦/q") {
                    // Closed in a later sentence.
                    let g = gold.iter().find(|g| g.s == s && body_text.starts_with(g.q.as_str()));
                    push(&mut quotes, &mut k, q, s, g, &ids, &cands, sentences, lie_every);
                    break;
                }
                let g = gold.iter().find(|g| g.s == s && body_text.starts_with(g.q.as_str()));
                push(&mut quotes, &mut k, q, s, g, &ids, &cands, sentences, lie_every);
                t = &body[body.find("⟦/q").unwrap() + 3..];
            }
        }
        if verify {
            serde_json::json!({"quotes": quotes}).to_string()
        } else {
            serde_json::json!({"quotes": quotes, "scenes": []}).to_string()
        }
    }

    #[allow(clippy::too_many_arguments)]
    fn push(quotes: &mut Vec<serde_json::Value>, k: &mut usize, q: u32, s: u32, g: Option<&Gold>, ids: &HashMap<String, String>, cands: &[&str], sentences: &[String], lie_every: usize) {
        let who = g.and_then(|g| {
            g.who.split('、').find_map(|w| {
                if let Some(id) = ids.get(w) {
                    return Some(id.clone());
                }
                let near: Vec<&String> = ids.iter().filter(|(n, _)| crate::script::same_person(n, w)).map(|(_, id)| id).collect();
                (near.len() == 1).then(|| near[0].clone())
            })
        });
        let mut speaker = match (g, who) {
            (Some(g), _) if g.kind == "group" => "GROUP".to_string(),
            (_, Some(id)) if cands.contains(&id.as_str()) => id,
            _ => "UNKNOWN".to_string(),
        };
        *k += 1;
        if lie_every > 0 && *k % lie_every == 0 && speaker != "UNKNOWN" {
            speaker = cands.iter().find(|c| c.starts_with('c') && **c != speaker).map(|c| c.to_string()).unwrap_or(speaker);
        }
        let ev: String = sentences[s as usize].chars().take(4).collect();
        let anchor = if q > 1 { q - 1 } else { 1 };
        quotes.push(serde_json::json!({"q": q, "speaker": speaker, "basis": "inferred", "anchor": anchor, "evidence": {"sentence": s, "quote": ev}, "to": null, "excluded": []}));
    }

    fn run(lie_every: usize) -> Score {
        let dir = std::env::temp_dir().join(format!("yuyue-drama-loop-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        let (head, gold) = read(&std::fs::read_to_string(format!("{root}/../../fixtures/speakers/hongloumeng-08.jsonl")).unwrap()).unwrap();
        core.drama_set_caps(DramaCaps { llm: true, verify_distinct: true, context_tokens: 2_000_000, ..Default::default() });
        let sentences = core.book_drama(&book.id).unwrap().sentences[head.section as usize].clone();
        // Only the focus section's scenes: stop once the work moves past it.
        for _ in 0..400 {
            let Some(t) = core.drama_next_task(book.id.clone(), head.section).unwrap() else { break };
            let DramaTask::Llm { id, tier, user, .. } = t else { break };
            let text = if id.starts_with("cast:") {
                r#"{"characters": []}"#.to_string()
            } else if id.starts_with("direct:") {
                r#"{"parts": []}"#.to_string()
            } else if id.contains(&format!(":{}:", head.section)) {
                answer(&user, &gold, &sentences, lie_every, tier == LlmTier::Verify)
            } else {
                // Past the section being read: done here.
                break;
            };
            core.drama_submit(book.id.clone(), id, DramaResult::Llm { text }).unwrap();
        }
        let b = core.book_drama(&book.id).unwrap();
        let play = core.drama_section(book.id.clone(), head.section).unwrap();
        if std::env::var("DRAMA_DEBUG").is_ok() {
            core.debug_section(&book.id, head.section);
        }
        let s = score(&play, &b.cast, &sentences, &gold);
        std::fs::remove_dir_all(dir).ok();
        s
    }

    #[test]
    fn a_right_model_fills_in_without_errors() {
        let s = run(0);
        println!("第八回（规则 + 答对的模型）：台词 {}，认对 {}，认错 {}，没把握 {}", s.lines, s.right, s.wrong.len(), s.unsure);
        assert!(s.wrong.is_empty(), "{:?}", s.wrong);
        assert!(s.right >= 92);
    }

    #[test]
    fn a_model_that_lies_now_and_then_is_caught_by_the_check() {
        // The same stand-in answers both layers, so a lie agrees with itself: what holds the line
        // is the rules' concord (a model wrong on written lines isn't trusted) — and it must.
        let s = run(3);
        println!("第八回（规则 + 三句错一句的模型）：认对 {}，认错 {}，没把握 {}", s.right, s.wrong.len(), s.unsure);
        assert!(s.wrong.is_empty(), "{:?}", s.wrong);
    }

    /// A task handed back (paused, the key wrong) is given out again however often; one that
    /// fails three times is left alone.
    #[test]
    fn handed_back_tasks_come_again_failed_ones_stop() {
        let dir = std::env::temp_dir().join(format!("yuyue-drama-back-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        core.drama_set_caps(DramaCaps { llm: true, verify_distinct: true, context_tokens: 2_000_000, ..Default::default() });
        let id_of = |t: DramaTask| match t { DramaTask::Llm { id, .. } => id, _ => panic!() };
        let first = id_of(core.drama_next_task(book.id.clone(), 9).unwrap().unwrap());
        for _ in 0..5 {
            core.drama_submit(book.id.clone(), first.clone(), DramaResult::Returned).unwrap();
            assert_eq!(id_of(core.drama_next_task(book.id.clone(), 9).unwrap().unwrap()), first);
        }
        for _ in 0..3 {
            core.drama_submit(book.id.clone(), first.clone(), DramaResult::Failed { reason: "bad answer".into() }).unwrap();
            let next = core.drama_next_task(book.id.clone(), 9).unwrap().map(id_of);
            if next.as_deref() != Some(first.as_str()) {
                // Given up: something else comes instead.
                assert!(next.is_none() || next.as_deref() != Some(first.as_str()));
                std::fs::remove_dir_all(dir).ok();
                return;
            }
        }
        panic!("a task failing three times is still given out");
    }

    /// A voice that gives nothing for a clip, twice: the narrator reads that clip instead.
    #[test]
    fn a_clip_its_voice_cannot_say_goes_to_the_narrator() {
        use super::super::casting::VoiceInfo;
        use super::super::{CharacterAge, CharacterGender};
        use super::super::queue::ClipCheck;
        use super::super::timeline::MadeClip;
        let dir = std::env::temp_dir().join(format!("yuyue-drama-tts-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        core.drama_set_caps(DramaCaps { tts: true, ..Default::default() });
        let v = |id: &str, g: CharacterGender| VoiceInfo { id: id.into(), langs: vec!["zh".into()], gender: g, age: CharacterAge::Unknown, tags: vec![], embedding: vec![], acts: false, narrator_ok: true, loudness_lufs: -20.0 };
        let voices = vec![v("n", CharacterGender::Unknown), v("m1", CharacterGender::M), v("f1", CharacterGender::F), v("m2", CharacterGender::M), v("f2", CharacterGender::F)];
        let narrator = core.drama_cast_voices(book.id.clone(), voices, "n".into()).unwrap().narrator;
        let tts = |t: Option<DramaTask>| match t { Some(DramaTask::Tts { id, clip }) => Some((id, clip)), _ => None };
        let ok = ClipCheck { cer: 0.0, longest_missing: 0, voice_sim: 1.0, clipped: false, silence_head: 0.1, silence_tail: 0.1, seconds: 2.0, chars: 10 };
        // The narrator's clips are made as asked, until one in someone else's voice comes.
        let mut picked = None;
        for _ in 0..500 {
            let Some((id, clip)) = tts(core.drama_next_task(book.id.clone(), 8).unwrap()) else { break };
            if clip.voice != narrator {
                picked = Some((id, clip));
                break;
            }
            let made = MadeClip { key: clip.key.clone(), seconds: 2.0, char_times: vec![] };
            core.drama_submit(book.id.clone(), id, DramaResult::Clip { clip: made, check: ok.clone() }).unwrap();
        }
        let (id, clip) = picked.expect("no clip in a character's voice");
        for _ in 0..2 {
            core.drama_submit(book.id.clone(), id.clone(), DramaResult::Failed { reason: "the voice gave nothing".into() }).unwrap();
        }
        let (_, again) = tts(core.drama_next_task(book.id.clone(), 8).unwrap()).expect("no clip given out");
        assert_eq!((again.section, again.sentence, again.start), (clip.section, clip.sentence, clip.start));
        assert_eq!(again.voice, narrator);
        std::fs::remove_dir_all(dir).ok();
    }
}
