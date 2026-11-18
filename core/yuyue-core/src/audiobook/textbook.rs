//! 文字版: a recording without its e-book becomes one. The careful transcript, punctuated where
//! the transcriber didn't (by pause length), cut into paragraphs at long pauses and into
//! chapters by the recording's marks, is a book like any other — and its map comes straight
//! from the words' times.
//!
//! A model may tidy it (同音错字, a name spelt two ways), but only by changing characters for
//! ones that sound the same: anything else is refused.

use serde::{Deserialize, Serialize};

use super::anchor::HeardWord;
use crate::audio::AudioMark;

pub const COMMA: f64 = 0.35;
pub const STOP: f64 = 0.6;
pub const PARAGRAPH: f64 = 0.9;
pub const LONGEST_PARAGRAPH: usize = 400;

/// One sentence of the made book, with its time.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct MadeSentence {
    pub text: String,
    pub t0: f64,
    pub t1: f64,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct MadeChapter {
    pub title: Option<String>,
    /// Paragraphs of sentences.
    pub paragraphs: Vec<Vec<MadeSentence>>,
}

fn has_end(s: &str) -> bool {
    s.ends_with(['。', '！', '？', '…', '.', '!', '?', '”', '"'])
}

/// The words as chapters, paragraphs and sentences. `chinese` decides spacing and punctuation.
pub fn make(words: &[HeardWord], marks: &[AudioMark], chinese: bool) -> Vec<MadeChapter> {
    let starts: Vec<f64> = if marks.is_empty() { vec![0.0] } else { marks.iter().map(|m| m.start).collect() };
    let mut chapters: Vec<MadeChapter> = starts.iter().enumerate().map(|(i, _)| MadeChapter { title: marks.get(i).and_then(|m| m.title.clone()), paragraphs: vec![] }).collect();
    let chapter_of = |t: f64| starts.partition_point(|s| *s <= t).saturating_sub(1);
    let mut cur = MadeSentence { text: String::new(), t0: 0.0, t1: 0.0 };
    let mut para: Vec<MadeSentence> = Vec::new();
    let mut ch = 0;
    let push_sentence = |cur: &mut MadeSentence, para: &mut Vec<MadeSentence>| {
        if !cur.text.trim().is_empty() {
            if !has_end(&cur.text) {
                cur.text.push(if chinese { '。' } else { '.' });
            }
            para.push(std::mem::replace(cur, MadeSentence { text: String::new(), t0: 0.0, t1: 0.0 }));
        }
    };
    for (i, w) in words.iter().enumerate() {
        let gap = if i == 0 { 0.0 } else { w.t0 - words[i - 1].t1 };
        let c = chapter_of(w.t0);
        if c != ch || gap >= PARAGRAPH || para.iter().map(|s| s.text.chars().count()).sum::<usize>() > LONGEST_PARAGRAPH {
            push_sentence(&mut cur, &mut para);
            if !para.is_empty() {
                chapters[ch].paragraphs.push(std::mem::take(&mut para));
            }
            ch = c;
        } else if gap >= STOP {
            push_sentence(&mut cur, &mut para);
        } else if gap >= COMMA && !cur.text.is_empty() && !cur.text.ends_with(['，', ',', '、', '：', ':', '；', ';']) {
            cur.text.push(if chinese { '，' } else { ',' });
        }
        if cur.text.is_empty() {
            cur.t0 = w.t0;
        } else if !chinese {
            cur.text.push(' ');
        }
        cur.text.push_str(w.text.trim());
        cur.t1 = w.t1;
        if has_end(w.text.trim()) {
            push_sentence(&mut cur, &mut para);
        }
    }
    push_sentence(&mut cur, &mut para);
    if !para.is_empty() {
        chapters[ch].paragraphs.push(para);
    }
    chapters.retain(|c| !c.paragraphs.is_empty());
    chapters
}

/// A model's tidying of one sentence: taken only when every change keeps the sounds.
pub fn tidy_ok(before: &str, after: &str, chinese: bool) -> bool {
    use super::phon::{phon, same};
    let (a, b) = (phon(before, chinese), phon(after, chinese));
    a.len() == b.len() && a.iter().zip(&b).all(|(x, y)| same(x, y))
}

/// The made book as plain text: a title line per chapter (第 N 章 where the recording names
/// none; none at all for a single one), a blank line between paragraphs.
pub fn as_text(chapters: &[MadeChapter]) -> String {
    let mut s = String::new();
    let chinese = chapters.iter().flat_map(|c| c.paragraphs.iter().flatten()).take(20).any(|x| x.text.chars().any(|c| ('\u{4e00}'..='\u{9fff}').contains(&c)));
    for (i, c) in chapters.iter().enumerate() {
        let title = c.title.clone().or_else(|| (chapters.len() > 1).then(|| if chinese { format!("第{}章", i + 1) } else { format!("Chapter {}", i + 1) }));
        if let Some(t) = title {
            s.push_str(&t);
            s.push_str("\n\n");
        }
        for p in &c.paragraphs {
            for x in p {
                s.push_str(&x.text);
            }
            s.push_str("\n\n");
        }
    }
    s
}

/// 整理文字版: what the model is told. It may only put right a character heard as another that
/// sounds the same, and a name written two ways; the core takes a fix only when it reads the same.
pub const POLISH_PROMPT: &str = "下面是一本有声书用语音识别转出来的文字，每行前面是句子的编号。识别会把同音字写错（比如“梨香院”写成“离乡院”），同一个人名前后写法不一。\n只改这两种错：同音或近音的错字、前后不一致的人名地名。不改标点、不增删字、不改说法、不润色。拿不准的不改。\n只回 JSON：{\"fixes\":[{\"section\":章号,\"sentence\":句号,\"from\":\"原文里错的那几个字\",\"to\":\"改成的字\"}]}，from 和 to 字数相同。没有要改的回 {\"fixes\":[]}。";

/// About this many characters of the 文字版 to a request.
pub const POLISH_CHUNK: usize = 2000;

/// One request: the sentences it covers and the text sent.
#[derive(Clone, Debug, PartialEq)]
pub struct PolishChunk {
    pub section: u32,
    pub first: u32,
    pub last: u32,
    pub text: String,
}

/// A book's sentences (by section) cut into requests, each line `[section.sentence] text`.
pub fn polish_chunks(sections: &[Vec<String>]) -> Vec<PolishChunk> {
    let mut out = Vec::new();
    for (sec, sentences) in sections.iter().enumerate() {
        let mut cur: Option<PolishChunk> = None;
        for (k, t) in sentences.iter().enumerate() {
            let line = format!("[{sec}.{k}] {t}\n");
            if let Some(c) = &cur
                && c.text.chars().count() + line.chars().count() > POLISH_CHUNK {
                    out.push(cur.take().unwrap());
                }
            let c = cur.get_or_insert(PolishChunk { section: sec as u32, first: k as u32, last: k as u32, text: String::new() });
            c.last = k as u32;
            c.text.push_str(&line);
        }
        out.extend(cur);
    }
    out
}

/// A fix the model proposed.
#[derive(Clone, Debug, PartialEq, serde::Deserialize)]
pub struct PolishFix {
    pub section: u32,
    pub sentence: u32,
    pub from: String,
    pub to: String,
}

/// The fixes of an answer that may be made: inside the request's sentences, `from` found in its
/// sentence, as many characters each way, and read the same (so the recording still matches).
pub fn checked_fixes(answer: &str, chunk: &PolishChunk, sentences: &[String], chinese: bool) -> Vec<PolishFix> {
    use super::phon::{phon, same};
    let body = answer.trim().trim_start_matches("```json").trim_start_matches("```").trim_end_matches("```").trim();
    let start = body.find('{').unwrap_or(0);
    let end = body.rfind('}').map(|e| e + 1).unwrap_or(body.len());
    #[derive(serde::Deserialize)]
    struct Answer {
        #[serde(default)]
        fixes: Vec<PolishFix>,
    }
    let Ok(a) = serde_json::from_str::<Answer>(&body[start..end]) else { return Vec::new() };
    a.fixes
        .into_iter()
        .filter(|f| {
            if f.section != chunk.section || f.sentence < chunk.first || f.sentence > chunk.last || f.from.is_empty() || f.from == f.to {
                return false;
            }
            let Some(text) = sentences.get(f.sentence as usize) else { return false };
            if !text.contains(&f.from) || f.from.chars().count() != f.to.chars().count() {
                return false;
            }
            let (a, b) = (phon(&f.from, chinese), phon(&f.to, chinese));
            a.len() == b.len() && !a.is_empty() && a.iter().zip(&b).all(|(x, y)| same(x, y))
        })
        .collect()
}

/// A fix made in a section's stored page: inside sentence `k`'s span, the first `from`.
pub fn fix_html(html: &str, k: u32, from: &str, to: &str) -> Option<String> {
    let esc = |t: &str| t.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;");
    let open = format!("data-s=\"{k}\"");
    let at = html.find(&open)?;
    let end = html[at + open.len()..].find("data-s=\"").map(|e| at + open.len() + e).unwrap_or(html.len());
    let (from, to) = (esc(from), esc(to));
    let i = html[at..end].find(&from)? + at;
    Some(format!("{}{}{}", &html[..i], to, &html[i + from.len()..]))
}

/// 划线搬家: where a highlight's words are in another edition of the book — (section, start
/// (sentence, character), end (sentence, character, exclusive)) — found by reading, so a 文字版's
/// homophones still find the real words. Only when the place is sure: the words read the same in
/// exactly one place, or in exactly one place near where they were (`near`, 0–1 through the book).
/// Too short a mark (under four sounds) could be anywhere and isn't moved.
pub fn find_words(words: &str, sections: &[Vec<String>], chinese: bool, near: Option<f64>) -> Option<(u32, (u32, u32), (u32, u32))> {
    use super::phon::{phon, same};
    let needle = phon(words, chinese);
    if needle.len() < 4 {
        return None;
    }
    let mut found: Vec<(u32, (u32, u32), (u32, u32))> = Vec::new();
    for (sec, sentences) in sections.iter().enumerate() {
        // The section as one stream of sounds, each with its sentence and place in it.
        let mut stream: Vec<(u32, super::phon::Tok)> = Vec::new();
        for (k, t) in sentences.iter().enumerate() {
            stream.extend(phon(t, chinese).into_iter().map(|tok| (k as u32, tok)));
        }
        if stream.len() < needle.len() {
            continue;
        }
        for at in 0..=stream.len() - needle.len() {
            if !needle.iter().zip(&stream[at..]).all(|(a, (_, b))| same(a, b)) {
                continue;
            }
            let (s0, first) = &stream[at];
            let (s1, last) = &stream[at + needle.len() - 1];
            let len = last.word.as_ref().map(|w| w.chars().count() as u32).unwrap_or(1);
            found.push((sec as u32, (*s0, first.src), (*s1, last.src + len)));
            if found.len() > 50 {
                return None;
            }
        }
    }
    if found.len() == 1 {
        return found.pop();
    }
    let near = near?;
    let n = sections.len().max(1) as f64;
    let close: Vec<_> = found.into_iter().filter(|f| ((f.0 as f64 + 0.5) / n - near).abs() <= 0.1).collect();
    (close.len() == 1).then(|| close[0])
}

#[cfg(test)]
mod tests {
    use super::*;

    fn w(t: &str, a: f64, b: f64) -> HeardWord {
        HeardWord { text: t.into(), t0: a, t1: b }
    }

    #[test]
    fn pauses_make_punctuation_and_paragraphs() {
        let words = vec![w("天", 0.0, 0.2), w("黑", 0.2, 0.4), w("了", 0.8, 1.0), w("他", 1.7, 1.9), w("走", 1.9, 2.1), w("了", 4.0, 4.2)];
        let c = make(&words, &[], true);
        assert_eq!(c.len(), 1);
        assert_eq!(c[0].paragraphs.len(), 2);
        assert_eq!(c[0].paragraphs[0][0].text, "天黑，了。");
        assert_eq!(c[0].paragraphs[0][1].text, "他走。");
    }

    #[test]
    fn tidying_may_only_fix_sounds() {
        assert!(tidy_ok("代玉笑了", "黛玉笑了", true));
        assert!(!tidy_ok("代玉笑了", "黛玉哭了", true));
        assert!(!tidy_ok("代玉笑了", "黛玉笑了起来", true));
    }
}

#[cfg(test)]
mod polish_tests {
    use super::*;

    #[test]
    fn only_fixes_that_read_the_same() {
        let sentences = vec!["宝玉往离乡院去了。".to_string(), "他笑了。".to_string()];
        let chunks = polish_chunks(&[sentences.clone()]);
        assert_eq!(chunks.len(), 1);
        assert!(chunks[0].text.starts_with("[0.0] 宝玉往离乡院去了。"));
        let answer = r#"```json
{"fixes":[{"section":0,"sentence":0,"from":"离乡","to":"梨香"},
 {"section":0,"sentence":1,"from":"笑","to":"哭"},
 {"section":0,"sentence":1,"from":"他","to":"她们"},
 {"section":0,"sentence":5,"from":"他","to":"她"}]}
```"#;
        let got = checked_fixes(answer, &chunks[0], &sentences, true);
        assert_eq!(got.len(), 1, "{got:?}");
        assert_eq!(got[0].to, "梨香");
        let html = r#"<p><span class="s" data-s="0">宝玉往离乡院去了。</span><span class="s" data-s="1">离乡</span></p>"#;
        let fixed = fix_html(html, 0, "离乡", "梨香").unwrap();
        assert!(fixed.contains("宝玉往梨香院去了。") && fixed.contains(">离乡<"), "{fixed}");
    }

    #[test]
    fn long_sections_are_cut() {
        let sentences: Vec<String> = (0..400).map(|i| format!("这是第{i}句话，长一点。")).collect();
        let chunks = polish_chunks(&[sentences]);
        assert!(chunks.len() > 1);
        assert!(chunks.iter().all(|c| c.text.chars().count() <= POLISH_CHUNK + 40));
        assert_eq!(chunks.last().unwrap().last, 399);
    }
}

#[cfg(test)]
mod move_tests {
    use super::*;

    #[test]
    fn a_mark_finds_its_words_by_reading() {
        let real = vec![
            vec!["宝玉往梨香院去了。".to_string(), "见宝钗在炕上做针线。".to_string()],
            vec!["他又往梨香院去。".to_string()],
        ];
        // The 文字版 heard 离乡院 for 梨香院: same sounds, found once with what follows.
        let got = find_words("离乡院去了。见宝钗", &real, true, None).unwrap();
        assert_eq!(got, (0, (0, 3), (1, 3)));
        // 梨香院去 is in both chapters: only near where it was.
        assert_eq!(find_words("离乡院去", &real, true, None), None);
        assert_eq!(find_words("离乡院去", &real, true, Some(0.8)).map(|f| f.0), Some(1));
        // Too short to be sure.
        assert_eq!(find_words("宝钗", &real, true, None), None);
    }
}
