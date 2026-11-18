//! 第一层：书上写明的. The rules in `script` find who a line names (宝玉道, said Darcy) and the
//! one carried on from just before (说着又道); this lays their answers on the segments. A name
//! the cast can't place for sure (two people share it) isn't used. A first-person teller (我说,
//! I said) is the narrator.

use crate::drama::cast::CastTable;
use crate::drama::spans::RawSeg;
use crate::drama::{LineBasis, DecidedBy, SegKind, DramaSpeaker};
use crate::script::{Cast as RuleCast, How, Line, lines};

/// What the rules say about one line (an utterance).
#[derive(Clone, Debug, PartialEq)]
pub struct RuleAnswer {
    pub utterance: u32,
    pub speaker: DramaSpeaker,
    pub basis: LineBasis,
    pub decided_by: DecidedBy,
    /// The speech-verb words around the line (宝玉笑道): what's left out when 剧化 is on, and
    /// what the acting is read from. (sentence, start, end) in characters.
    pub tag: Option<(u32, u32, u32)>,
}

fn first_person_before(before: &str) -> bool {
    let b = before.trim_end_matches(['：', ':', '，', ',', ' ']);
    let tail: String = b.chars().rev().take(6).collect::<Vec<_>>().into_iter().rev().collect();
    let Some(i) = tail.rfind('我') else { return false };
    let rest = &tail[i + '我'.len_utf8()..];
    rest.chars().all(|c| "笑忙又便也就因冷低悄高大声說说道问問答回叫喊".contains(c)) && ["说", "說", "道", "问", "問", "答", "叫", "喊"].iter().any(|v| rest.ends_with(v))
}

fn first_person_after(after: &str) -> bool {
    let a = after.trim_start_matches([' ', ',', '，']);
    ["I said", "I asked", "I replied", "I answered", "I cried", "said I", "I whispered", "我说", "我說", "我道", "我问", "我問"].iter().any(|p| a.starts_with(p))
}

/// Who says each line, as far as the rules are sure.
pub fn attribute(rule_cast: &RuleCast, cast: &mut CastTable, sentences: &[String], segs: &[RawSeg]) -> Vec<RuleAnswer> {
    let found: Vec<Line> = lines(rule_cast, sentences);
    let mut out = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for (k, seg) in segs.iter().enumerate() {
        let Some(u) = seg.utterance else { continue };
        if !seen.insert(u) || !matches!(seg.kind, SegKind::Speech | SegKind::Thought | SegKind::Quoted) {
            continue;
        }
        let s = &sentences[seg.sentence as usize];
        let chars: Vec<char> = s.chars().collect();
        let before: String = chars[..seg.start as usize].iter().collect();
        // The words after the whole line (the last stretch of this utterance).
        let last = segs[k..].iter().take_while(|x| x.utterance == Some(u) || x.utterance.is_none()).filter(|x| x.utterance == Some(u)).last().unwrap_or(seg);
        let lchars: Vec<char> = sentences[last.sentence as usize].chars().collect();
        let after: String = lchars[(last.end as usize).min(lchars.len())..].iter().collect();
        // First person.
        if first_person_before(&before) || first_person_after(&after) {
            let id = cast.narrator();
            out.push(RuleAnswer { utterance: u, speaker: DramaSpeaker::Char { id }, basis: LineBasis::Explicit, decided_by: DecidedBy::Rule, tag: tag_of(seg, &before, last, &after) });
            continue;
        }
        let body_start = seg.start + 1;
        let line = found.iter().find(|l| l.first == seg.sentence && (l.start == body_start || l.start == seg.start));
        let Some(line) = line else {
            out.push(unsure(u));
            continue;
        };
        let Some(who) = line.who.as_deref() else {
            // 说话词在后: “……”黛玉冷笑道。
            if let Some(id) = named_after(cast, &after) {
                out.push(RuleAnswer { utterance: u, speaker: DramaSpeaker::Char { id }, basis: LineBasis::Explicit, decided_by: DecidedBy::Rule, tag: tag_of(seg, &before, last, &after) });
            } else {
                out.push(unsure(u));
            }
            continue;
        };
        // Not a name of anyone (忽有人說道: who it is comes later), or the one spoken to, not the
        // speaker (便向襲人道, 問寶玉道): left for the model, never guessed.
        if SOMEONE.contains(&who) || GENERIC.contains(&who) || spoken_to(&before, who) || who.starts_with(TO) {
            out.push(unsure(u));
            continue;
        }
        // 襲人等道: she and others — a group.
        let group_after = who.chars().count() >= 3 && who.chars().last().is_some_and(|c| crate::drama::cast::GROUP_AFTER.contains(c));
        let (speaker, basis) = match line.how {
            How::Group => (DramaSpeaker::Group { name: who.to_string() }, LineBasis::Explicit),
            _ if group_after => (DramaSpeaker::Group { name: who.to_string() }, LineBasis::Explicit),
            How::Named | How::Before => {
                let id = match cast.resolve(who) {
                    Some(id) => id,
                    // Someone known with 儿 after (鳳姐儿 for 鳳姐): most likely the same person, but
                    // the rules don't join names; the model will.
                    None if who.strip_suffix('儿').or_else(|| who.strip_suffix('兒')).is_some_and(|w| cast.resolve(w).is_some()) => {
                        out.push(unsure(u));
                        continue;
                    }
                    // A name the rules found that the cast doesn't have yet (茜雪道 once): a new row.
                    None if !cast.ambiguous.iter().any(|a| a == who) => cast.add_name(who, false),
                    None => {
                        out.push(unsure(u));
                        continue;
                    }
                };
                (DramaSpeaker::Char { id }, if line.how == How::Named { LineBasis::Explicit } else { LineBasis::Inferred })
            }
            How::Unsure => {
                out.push(unsure(u));
                continue;
            }
        };
        out.push(RuleAnswer { utterance: u, speaker, basis, decided_by: DecidedBy::Rule, tag: tag_of(seg, &before, last, &after) });
    }
    out
}

/// A name right after a line, then only manner words and a speech verb, then 。or ，— not a
/// colon, which would make it the next line's tag: “……”黛玉冷笑道。
fn named_after(cast: &CastTable, after: &str) -> Option<u32> {
    let a = after.trim_start();
    let stop = a.find(['。', '，', '；', '！', '？', '：', ':'])?;
    let mark = a[stop..].chars().next()?;
    if mark == '：' || mark == ':' {
        return None;
    }
    let clause = &a[..stop];
    let hits = cast.find(clause);
    let h = hits.first().filter(|h| h.start == 0)?;
    if hits.len() > 1 {
        return None;
    }
    let rest: String = clause.chars().skip(h.end).collect();
    const MANNER: &str = "笑忙又便也就因冷低悄高大声聲含着著也只方才乃遂连連拍手点點头頭叹嘆歎";
    let verb = ["说道", "說道", "笑道", "问道", "問道", "道", "说", "說", "问", "問", "答", "叫", "喊", "嚷"].iter().find(|v| rest.ends_with(*v))?;
    rest[..rest.len() - verb.len()].chars().all(|c| MANNER.contains(c)).then_some(h.id)
}

/// Words for someone not yet named.
const SOMEONE: &[&str] = &["有人", "有一人", "一人", "一個人", "一个人", "那人", "這人", "这人", "某人", "人"];
/// What someone is, not who: a maid who says a line here may be named a page later (丫頭 →
/// 小紅); a voice of its own for “丫頭” would be the wrong one.
const GENERIC: &[&str] = &["丫頭", "丫头", "小丫頭", "小丫头", "丫鬟", "婆子", "老婆子", "婆子們", "小廝", "小厮", "媳婦", "媳妇", "醫生", "医生", "大夫", "太醫", "太医", "家人", "下人", "那丫頭", "那丫头", "那婆子", "那小廝", "那小厮"];
/// A word before a name that makes it whom the line is said to: 向襲人道, 對他說, 問寶玉道.
const TO: &[char] = &['向', '對', '对', '與', '与', '和', '跟', '同', '問', '问', '朝', '沖', '冲', '給', '给', '望'];

/// Whether `who`, where it last comes before the line, is the one spoken to.
fn spoken_to(before: &str, who: &str) -> bool {
    before.rfind(who).is_some_and(|i| before[..i].chars().last().is_some_and(|c| TO.contains(&c)))
}

fn unsure(u: u32) -> RuleAnswer {
    RuleAnswer { utterance: u, speaker: DramaSpeaker::Unsure { top: vec![] }, basis: LineBasis::None, decided_by: DecidedBy::Unsure, tag: None }
}

/// The speech tag next to a line: the clause just before it ending in a speech verb and colon
/// (宝玉笑道：), or the clause just after it (宝玉道。/ said Darcy.).
fn tag_of(first: &RawSeg, before: &str, last: &RawSeg, after: &str) -> Option<(u32, u32, u32)> {
    const VERB_END: &[&str] = &["道", "说", "說", "问", "問", "答", "叫", "喊", "嚷"];
    let b = before.trim_end_matches(['：', ':']);
    if before.ends_with(['：', ':']) && VERB_END.iter().any(|v| b.ends_with(v)) {
        let start = b.rfind(['，', '。', '；', '！', '？', ',', '.']).map(|i| b[..i].chars().count() + 1).unwrap_or(0);
        return Some((first.sentence, start as u32, first.start));
    }
    let a = after;
    let stop = a.find(['。', '，', '；', '.', ';', '!', '?', '！', '？', ',']).unwrap_or(a.len());
    let clause = &a[..stop];
    let lower = clause.to_lowercase();
    let said = VERB_END.iter().any(|v| clause.ends_with(v)) || ["said", "asked", "replied", "cried", "answered", "whispered"].iter().any(|w| lower.contains(w));
    if said && clause.chars().count() <= 20 && !clause.trim().is_empty() {
        let n = a[..stop + a[stop..].chars().next().map(char::len_utf8).unwrap_or(0)].chars().count() as u32;
        return Some((last.sentence, last.end, last.end + n));
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;
    use crate::drama::spans::split;

    #[test]
    fn named_lines_get_their_speaker_and_tag() {
        let ss: Vec<String> = vec!["宝玉笑道：“你来了。”".into(), "“来了。”黛玉道。".into()];
        let rc = RuleCast::read(&[ss.clone(), ss.clone(), ss.clone()]);
        let mut cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let segs = split(&ss);
        let a = attribute(&rc, &mut cast, &ss, &segs);
        assert_eq!(a.len(), 2);
        assert_eq!(a[0].speaker, DramaSpeaker::Char { id: cast.resolve("宝玉").unwrap() });
        assert_eq!(a[0].tag, Some((0, 0, 5)));
        assert_eq!(a[1].speaker, DramaSpeaker::Char { id: cast.resolve("黛玉").unwrap() });
        assert_eq!(a[1].tag, Some((1, 5, 9)));
    }

    #[test]
    fn first_person_is_the_narrator() {
        let ss: Vec<String> = vec!["我笑道：“好。”".into()];
        let rc = RuleCast::read(&[ss.clone()]);
        let mut cast = from_rules(&[]);
        let a = attribute(&rc, &mut cast, &ss, &split(&ss));
        let DramaSpeaker::Char { id } = a[0].speaker else { panic!() };
        assert!(cast.get(id).unwrap().narrator);
    }

    #[test]
    fn the_one_spoken_to_and_someone_unnamed_are_not_speakers() {
        assert!(spoken_to("黛玉看了，便向襲人道：", "襲人"));
        assert!(spoken_to("因問寶玉道：", "寶玉"));
        assert!(!spoken_to("寶玉笑道：", "寶玉"));
        assert!(!spoken_to("說著，黛玉又道：", "黛玉"));
        assert!(SOMEONE.contains(&"有人"));
    }

    #[test]
    fn said_to_xiren_is_daiyu_s_line_not_xiren_s() {
        let ss: Vec<String> = vec!["黛玉看了，知是寶玉一時感忿而作，不覺可笑可歎，便向襲人道：“作的是玩意儿，無甚關系。”".into()];
        let rc = RuleCast::read(&[ss.clone(), ss.clone(), ss.clone()]);
        let mut cast = from_rules(&["黛玉".into(), "襲人".into(), "寶玉".into()]);
        let a = attribute(&rc, &mut cast, &ss, &split(&ss));
        assert_ne!(a[0].speaker, DramaSpeaker::Char { id: cast.resolve("襲人").unwrap() }, "{a:?}");
    }
}
