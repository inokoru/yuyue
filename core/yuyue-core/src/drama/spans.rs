//! 拆段: a section's sentences cut into narration, speech, thought and quoted writing.
//!
//! Only the outermost quote is a line; one inside it (“他说‘你来’”) stays part of it. A quote
//! that doesn't close in its sentence runs on into the next ones — the same line — up to
//! `MAX_RUN` sentences; past that the opener is taken for a typo and read as narration. An
//! English writer who leaves a long speech open at a paragraph's end and opens it again on the
//! next keeps the same line.
//!
//! What's quoted but not said: a word or two set off for stress (“所谓”的), what's written on
//! something (写着“…”), and a thought (心想：“…”) are told apart by the words around them.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

use super::SegKind;

/// The most sentences one line may run over before its opener is taken for a typo.
pub const MAX_RUN: usize = 40;

/// A stretch of one sentence. `start`/`end` are characters (not bytes) of the sentence; a line's
/// quote marks are inside its stretch.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct RawSeg {
    pub sentence: u32,
    pub start: u32,
    pub end: u32,
    pub kind: SegKind,
    /// Stretches of the same quote share it: a line run over several sentences. Narration has none.
    pub utterance: Option<u32>,
}

/// A quote found in the section: from its opener to its closer, as places in all the section's
/// characters laid end to end.
#[derive(Clone, Debug)]
struct Pair {
    open: usize,
    close: usize,
    kind: SegKind,
}

#[derive(Clone, Copy)]
struct At {
    c: char,
    sentence: usize,
    offset: usize,
}

fn closer(open: char) -> Option<char> {
    Some(match open {
        '“' => '”',
        '「' => '」',
        '『' => '』',
        '‘' => '’',
        '«' => '»',
        '"' => '"',
        _ => return None,
    })
}

fn is_word(c: Option<char>) -> bool {
    c.is_some_and(|c| c.is_alphanumeric())
}

/// Whether the text from `from` to `to` is mostly Latin letters.
fn latin_since(chars: &[At], from: usize, to: usize) -> bool {
    let (mut l, mut h) = (0, 0);
    for a in &chars[from..to] {
        if a.c.is_ascii_alphabetic() {
            l += 1;
        } else if a.c.is_alphabetic() {
            h += 1;
        }
    }
    l > h * 2
}

/// The quotes of a section, outermost only.
fn pairs(chars: &[At]) -> Vec<(usize, usize)> {
    let mut out = Vec::new();
    let mut skip = std::collections::HashSet::new();
    let mut i = 0;
    'scan: while i < chars.len() {
        let c = chars[i].c;
        let prev = i.checked_sub(1).map(|p| chars[p].c);
        let next = chars.get(i + 1).map(|a| a.c);
        let opens = match c {
            '“' | '「' | '『' | '«' | '"' => true,
            // ‘ opens a quote unless it's in a word (rare: ‘tis).
            '‘' => !is_word(prev),
            _ => false,
        };
        if !opens || skip.contains(&i) {
            i += 1;
            continue;
        }
        let first = chars[i].sentence;
        let mut stack = vec![c];
        let mut j = i + 1;
        while j < chars.len() {
            if chars[j].sentence - first > MAX_RUN {
                break;
            }
            let d = chars[j].c;
            let top = *stack.last().unwrap();
            let p = chars[j - 1].c;
            let n = chars.get(j + 1).map(|a| a.c);
            // A curly opener closed by a straight quote (and the other way) is a typo, not a
            // new quote: “在那里看見了？"
            let closes = Some(d) == closer(top) || (top == '“' && d == '"') || (top == '"' && d == '”');
            if closes {
                // ’ between letters is an apostrophe (don’t), not a closer.
                let apostrophe = d == '’' && p.is_alphanumeric() && is_word(n);
                if !apostrophe {
                    stack.pop();
                    if stack.is_empty() {
                        out.push((i, j));
                        i = j + 1;
                        continue 'scan;
                    }
                }
            } else if d == c && stack.len() == 1 && d != '"' && chars[j].offset == 0 && latin_since(chars, i, j) {
                // The same opener again at a sentence's start in English: a long speech opened
                // again at a new paragraph. Still the same line.
            } else if matches!(d, '“' | '「' | '『' | '«') || (d == '‘' && !p.is_alphanumeric()) {
                stack.push(d);
            }
            j += 1;
        }
        // The quote never closes: at the section's end within reach, it runs to the end;
        // otherwise its opener was a typo.
        if j >= chars.len() && chars.last().is_some_and(|l| l.sentence - first <= MAX_RUN) && c != '"' && c != '‘' {
            out.push((i, chars.len() - 1));
            break;
        }
        skip.insert(i);
        i += 1;
        let _ = next;
    }
    out
}

/// Words before writing on something: what follows is read, not said.
const WRITTEN: &[&str] = &["写着", "寫著", "题着", "題著", "刻着", "刻著", "上书", "上書", "题曰", "題曰", "书曰", "書曰", "印着", "印著", "信上说", "信上說", "信中写道", "信中寫道"];
const THINK: &[&str] = &["心想", "暗想", "暗道", "寻思", "尋思", "自忖", "暗忖", "想道", "心道", "心里想", "心裏想", "心中想", "心內想", "心内想", "思忖", "忖道", "心下想", "暗暗想"];
const THINK_EN: &[&str] = &["thought", "wondered", "mused", "reflected"];
/// The words after a quote that say it was spoken: a short quote with these is a line, not stress.
const SAY_AFTER: &[&str] = &["道", "说", "說", "问", "問", "笑", "叫", "喊", "答", "said", "asked", "cried", "replied", "says", "whispered", "answered"];
const ENDS: &[char] = &['。', '！', '？', '…', '，', '!', '?', '.', ',', '；', ';', '—', '～', '~'];

fn text(chars: &[At], from: usize, to: usize) -> String {
    chars[from.min(chars.len())..to.min(chars.len())].iter().map(|a| a.c).collect()
}

/// What a quote is, from the words around it.
fn kind_of(chars: &[At], open: usize, close: usize) -> SegKind {
    let inner = text(chars, open + 1, close);
    let before: String = text(chars, open.saturating_sub(8), open);
    let before = before.trim_end_matches([' ', '：', ':', '，', ',']);
    let after: String = text(chars, close + 1, close + 10);
    let near6: String = before.chars().rev().take(6).collect::<Vec<_>>().into_iter().rev().collect();
    if WRITTEN.iter().any(|w| near6.contains(w)) {
        return SegKind::Quoted;
    }
    if THINK.iter().any(|w| before.ends_with(w)) || THINK_EN.iter().any(|w| before.to_lowercase().ends_with(w)) {
        return SegKind::Thought;
    }
    let after_lower = after.to_lowercase();
    let short = inner.chars().filter(|c| !c.is_whitespace()).count() <= 4;
    let has_end = inner.chars().any(|c| ENDS.contains(&c));
    let colon_before = text(chars, open.saturating_sub(2), open).trim_end().ends_with(['：', ':']);
    // 借“通灵”之说, “无题”的说法: the word after names a saying, nobody saying it.
    let names_a_saying = after.starts_with(['之', '的']);
    let say_after = !names_a_saying && SAY_AFTER.iter().any(|w| after_lower.trim_start_matches([' ', '，', ',']).chars().take(6).collect::<String>().contains(w));
    if short && !has_end && !colon_before && !say_after {
        return SegKind::Narration;
    }
    SegKind::Speech
}

/// The section's sentences as stretches, each sentence covered end to end.
pub fn split(sentences: &[String]) -> Vec<RawSeg> {
    let mut chars = Vec::new();
    for (si, s) in sentences.iter().enumerate() {
        for (o, c) in s.chars().enumerate() {
            chars.push(At { c, sentence: si, offset: o });
        }
    }
    let found: Vec<Pair> = pairs(&chars).into_iter().map(|(open, close)| Pair { open, close, kind: kind_of(&chars, open, close) }).filter(|p| p.kind != SegKind::Narration).collect();
    let mut out = Vec::new();
    let mut utterance = 0u32;
    let mut cursor = 0usize; // in `chars`
    let push = |from: usize, to_incl: usize, kind: SegKind, utt: Option<u32>, out: &mut Vec<RawSeg>| {
        // Cut [from, to_incl] at sentence ends.
        let mut a = from;
        while a <= to_incl {
            let s = chars[a].sentence;
            let mut b = a;
            while b < to_incl && chars[b + 1].sentence == s {
                b += 1;
            }
            out.push(RawSeg { sentence: s as u32, start: chars[a].offset as u32, end: chars[b].offset as u32 + 1, kind, utterance: utt });
            a = b + 1;
        }
    };
    for p in &found {
        if p.open > cursor {
            push(cursor, p.open - 1, SegKind::Narration, None, &mut out);
        }
        push(p.open, p.close, p.kind, Some(utterance), &mut out);
        utterance += 1;
        cursor = p.close + 1;
    }
    if cursor < chars.len() {
        push(cursor, chars.len() - 1, SegKind::Narration, None, &mut out);
    }
    // Empty sentences still get a stretch, so every sentence is there.
    for (si, s) in sentences.iter().enumerate() {
        if s.is_empty() {
            out.push(RawSeg { sentence: si as u32, start: 0, end: 0, kind: SegKind::Narration, utterance: None });
        }
    }
    out.sort_by_key(|r| (r.sentence, r.start));
    out
}

/// A segment's text.
pub fn seg_text(sentences: &[String], r: &RawSeg) -> String {
    sentences.get(r.sentence as usize).map(|s| s.chars().skip(r.start as usize).take((r.end - r.start) as usize).collect()).unwrap_or_default()
}

/// A section with no quotes worth the name but with lines written as `——` or `张三：`: a book
/// without quote marks (无引号体).
pub fn unquoted(sentences: &[String]) -> bool {
    let total: usize = sentences.iter().map(|s| s.chars().count()).sum();
    if total == 0 {
        return false;
    }
    let quoted: usize = split(sentences).iter().filter(|r| r.kind != SegKind::Narration).map(|r| (r.end - r.start) as usize).sum();
    let marks = sentences.iter().filter(|s| s.starts_with("——") || s.contains("道：") || s.contains("说：") || s.contains("說：")).count();
    quoted * 50 < total && marks >= 5
}

/// A section's segments: by its quotes, or — a book without quote marks — by the model's lines
/// (`model`, checked) when there are any, the sure lines otherwise.
pub fn segments(sentences: &[String], model: Option<&[(u32, u32, u32)]>) -> Vec<RawSeg> {
    if unquoted(sentences) { split_unquoted(sentences, model) } else { split(sentences) }
}

/// 无引号体 cut into narration and lines. `model`'s (sentence, start, end) char ranges are taken
/// sentence by sentence: one that falls outside its sentence or overlaps another puts that whole
/// sentence back to narration. Without `model`, the sure lines (`——`, `名字：`) to the sentence's end.
pub fn split_unquoted(sentences: &[String], model: Option<&[(u32, u32, u32)]>) -> Vec<RawSeg> {
    let mut by: HashMap<u32, Vec<(u32, u32)>> = HashMap::new();
    match model {
        Some(m) => {
            for &(s, a, b) in m {
                by.entry(s).or_default().push((a, b));
            }
            for (s, rs) in by.iter_mut() {
                let len = sentences.get(*s as usize).map(|x| x.chars().count() as u32).unwrap_or(0);
                rs.sort();
                let ok = rs.iter().all(|(a, b)| a < b && *b <= len) && rs.windows(2).all(|w| w[0].1 <= w[1].0);
                if !ok {
                    rs.clear();
                }
            }
        }
        None => {
            for (s, start, _) in unquoted_lines(sentences) {
                let len = sentences[s as usize].chars().count() as u32;
                if start < len {
                    by.entry(s).or_default().push((start, len));
                }
            }
        }
    }
    let mut out = Vec::new();
    let mut utterance = 0u32;
    for (i, sentence) in sentences.iter().enumerate() {
        let len = sentence.chars().count() as u32;
        let mut at = 0u32;
        for &(a, b) in by.get(&(i as u32)).map(|v| v.as_slice()).unwrap_or(&[]) {
            if a > at {
                out.push(RawSeg { sentence: i as u32, start: at, end: a, kind: SegKind::Narration, utterance: None });
            }
            out.push(RawSeg { sentence: i as u32, start: a, end: b, kind: SegKind::Speech, utterance: Some(utterance) });
            utterance += 1;
            at = b;
        }
        if at < len {
            out.push(RawSeg { sentence: i as u32, start: at, end: len, kind: SegKind::Narration, utterance: None });
        }
    }
    out
}

/// The model's lines, given as the words themselves, as char ranges: each found in its sentence
/// (after the one before in the same sentence). Words not found are left out.
pub fn locate_lines(sentences: &[String], lines: &[(u32, String)]) -> Vec<(u32, u32, u32)> {
    let mut out = Vec::new();
    let mut from: HashMap<u32, usize> = HashMap::new();
    for (s, text) in lines {
        let Some(sentence) = sentences.get(*s as usize) else { continue };
        let text = text.trim();
        if text.is_empty() {
            continue;
        }
        let f = from.get(s).copied().unwrap_or(0);
        let Some(k) = sentence[f..].find(text).map(|k| k + f) else { continue };
        let a = sentence[..k].chars().count() as u32;
        let b = a + text.chars().count() as u32;
        from.insert(*s, k + text.len());
        out.push((*s, a, b));
    }
    out
}

/// The sure lines of a book without quote marks: a sentence from `——` to its end, and
/// `名字：` at a sentence's start (two to four characters) to its end. Returns (sentence, start
/// char of the line, name if written).
pub fn unquoted_lines(sentences: &[String]) -> Vec<(u32, u32, Option<String>)> {
    let mut out = Vec::new();
    for (i, s) in sentences.iter().enumerate() {
        if s.starts_with("——") {
            out.push((i as u32, 0, None));
        } else if let Some(k) = s.find('：') {
            let head = &s[..k];
            let n = head.chars().count();
            if (2..=4).contains(&n) && head.chars().all(|c| ('\u{4e00}'..='\u{9fff}').contains(&c)) {
                out.push((i as u32, n as u32 + 1, Some(head.to_string())));
            }
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn s(v: &[&str]) -> Vec<String> {
        v.iter().map(|x| x.to_string()).collect()
    }

    fn kinds(v: &[&str]) -> Vec<(u32, String, SegKind)> {
        let ss = s(v);
        split(&ss).iter().map(|r| (r.sentence, seg_text(&ss, r), r.kind)).collect()
    }

    #[test]
    fn a_line_and_its_narration() {
        assert_eq!(
            kinds(&["宝玉笑道：“你来了。”"]),
            vec![(0, "宝玉笑道：".into(), SegKind::Narration), (0, "“你来了。”".into(), SegKind::Speech)]
        );
    }

    #[test]
    fn a_line_runs_over_sentences_as_one() {
        let ss = s(&["黛玉道：“你去罢。", "我不去了。”说着走了。"]);
        let r = split(&ss);
        let sp: Vec<_> = r.iter().filter(|x| x.kind == SegKind::Speech).collect();
        assert_eq!(sp.len(), 2);
        assert_eq!(sp[0].utterance, sp[1].utterance);
        assert_eq!(seg_text(&ss, sp[1]), "我不去了。”");
    }

    #[test]
    fn a_quote_inside_belongs_to_the_outer_line() {
        let k = kinds(&["他道：“他说‘你来’，我就来了。”"]);
        assert_eq!(k.iter().filter(|x| x.2 == SegKind::Speech).count(), 1);
    }

    #[test]
    fn stress_writing_and_thought() {
        let k = kinds(&["这就是所谓的“规矩”罢了。"]);
        assert!(k.iter().all(|x| x.2 == SegKind::Narration), "{k:?}");
        let k = kinds(&["匾上写着“大观园”三个字。"]);
        assert!(k.iter().any(|x| x.2 == SegKind::Quoted), "{k:?}");
        let k = kinds(&["宝玉心想：“这可怎么好。”"]);
        assert!(k.iter().any(|x| x.2 == SegKind::Thought), "{k:?}");
    }

    #[test]
    fn english_apostrophes_and_long_speeches() {
        let k = kinds(&["\u{201c}I don\u{2019}t know,\u{201d} said Jane."]);
        assert_eq!(k.iter().filter(|x| x.2 == SegKind::Speech).count(), 1, "{k:?}");
        // A paragraph left open and opened again: one line.
        let ss = s(&["\u{201c}It was late.", "\u{201c}And then we left.\u{201d}"]);
        let r = split(&ss);
        let u: Vec<_> = r.iter().filter(|x| x.kind == SegKind::Speech).map(|x| x.utterance).collect();
        assert_eq!(u.len(), 2);
        assert_eq!(u[0], u[1]);
    }

    #[test]
    fn an_opener_that_never_closes_is_a_typo() {
        let mut v = vec!["他说“你好。".to_string()];
        for _ in 0..50 {
            v.push("天色晚了。".into());
        }
        v.push("“好。”她道。".into());
        let r = split(&v);
        // The first opener runs past MAX_RUN: narration. The last line is still found.
        assert_eq!(r.iter().filter(|x| x.kind == SegKind::Speech).count(), 1);
        assert_eq!(r.iter().find(|x| x.kind == SegKind::Speech).unwrap().sentence, 51);
    }

    #[test]
    fn every_character_is_covered_once() {
        let ss = s(&["甲道：“乙", "丙。”丁说“戊”。", "", "己"]);
        let r = split(&ss);
        for (i, t) in ss.iter().enumerate() {
            let mut covered: Vec<_> = r.iter().filter(|x| x.sentence == i as u32).map(|x| (x.start, x.end)).collect();
            covered.sort();
            let mut at = 0;
            for (a, b) in covered {
                assert_eq!(a, at);
                at = b;
            }
            assert_eq!(at as usize, t.chars().count());
        }
    }

    #[test]
    fn a_word_in_quotes_named_as_a_saying_is_not_a_line() {
        let segs = split(&["故將真事隱去，而借“通靈”之說，撰此《石頭記》一書也．".to_string()]);
        assert!(segs.iter().all(|s| s.kind == SegKind::Narration), "{segs:?}");
    }

    fn unquoted_book() -> Vec<String> {
        let mut v: Vec<String> = ["他推门进来。", "——你来了。", "张三：坐吧，外面冷。", "李四点点头，说你也坐。", "——好。", "王五：嗯。", "——走吧。"].iter().map(|s| s.to_string()).collect();
        v.extend(["窗外下着雨。", "——还下着。", "——明天再说。"].map(String::from));
        v
    }

    #[test]
    fn a_book_without_quotes_takes_its_sure_lines() {
        let ss = unquoted_book();
        assert!(unquoted(&ss));
        let segs = segments(&ss, None);
        let said: Vec<String> = segs.iter().filter(|r| r.kind == SegKind::Speech).map(|r| seg_text(&ss, r)).collect();
        assert_eq!(said, ["——你来了。", "坐吧，外面冷。", "——好。", "嗯。", "——走吧。", "——还下着。", "——明天再说。"]);
        // Every character is in one segment, in order.
        for (i, s) in ss.iter().enumerate() {
            let joined: String = segs.iter().filter(|r| r.sentence == i as u32).map(|r| seg_text(&ss, r)).collect();
            assert_eq!(&joined, s);
        }
    }

    #[test]
    fn the_models_lines_are_found_and_checked() {
        let ss = unquoted_book();
        let found = locate_lines(&ss, &[(3, "你也坐".into()), (2, "坐吧，外面冷。".into()), (0, "不在这句里".into())]);
        assert_eq!(found, vec![(3, 7, 10), (2, 3, 10)]);
        let segs = segments(&ss, Some(&found));
        let said: Vec<String> = segs.iter().filter(|r| r.kind == SegKind::Speech).map(|r| seg_text(&ss, r)).collect();
        assert_eq!(said, ["坐吧，外面冷。", "你也坐"]);
        // Overlapping or out of the sentence: that sentence is narration.
        let segs = segments(&ss, Some(&[(2, 0, 5), (2, 3, 8), (3, 2, 99)]));
        assert!(segs.iter().all(|r| r.kind == SegKind::Narration));
    }
}
