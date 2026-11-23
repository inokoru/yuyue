//! 读音归一: the book and what the transcriber heard, both turned into the same string of
//! sounds, so they can be laid side by side whatever the characters.
//!
//! Chinese: through 简体, each character as its toneless pinyin — every reading a 多音字 has, so
//! two match when their readings meet; front and back nasals, flat and curled tongues, n and l
//! counted as one (readers and transcribers both mix them). Numbers, written in digits or in
//! characters, positional (一千九百七十八) or digit by digit (一九七八), become the digits they
//! mean — on both sides, so “1978年” and “一九七八年” are the same four sounds. 啊呀呢吧, 儿 after a
//! syllable: light, missing or extra costs half.
//!
//! English and other Latin-script text: lower case, punctuation off, number words to digits,
//! a few abbreviations spelt out. Two words match when equal, or when long and one letter apart
//! (a transcriber's slip).

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};

use pinyin::ToPinyinMulti;

/// One sound.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Tok {
    /// The readings it may have (interned); two toks match when these meet.
    pub keys: Vec<u32>,
    /// For hashing: the first reading.
    pub key: u32,
    /// Where in the text it starts (characters).
    pub src: u32,
    /// 啊呀呢吧, 儿: costs half to miss.
    pub light: bool,
    /// The word as written, for English near-matches.
    pub word: Option<String>,
}

fn intern(s: &str) -> u32 {
    static T: OnceLock<Mutex<HashMap<String, u32>>> = OnceLock::new();
    let mut t = T.get_or_init(|| Mutex::new(HashMap::new())).lock().unwrap_or_else(|e| e.into_inner());
    let n = t.len() as u32;
    *t.entry(s.to_string()).or_insert(n)
}

/// Front/back nasals, flat/curled tongues, n/l: one sound each.
fn fuzzy(p: &str) -> String {
    let mut s = p.replace('ü', "v");
    for (a, b) in [("zh", "z"), ("ch", "c"), ("sh", "s")] {
        if s.starts_with(a) {
            s = format!("{b}{}", &s[a.len()..]);
        }
    }
    if s.starts_with('l') {
        s = format!("n{}", &s[1..]);
    }
    for (a, b) in [("ing", "in"), ("eng", "en"), ("ang", "an")] {
        if s.ends_with(a) && !s.ends_with("iang") && !s.ends_with("uang") {
            s = format!("{}{b}", &s[..s.len() - a.len()]);
        }
    }
    s
}

fn is_han(c: char) -> bool {
    ('\u{4e00}'..='\u{9fff}').contains(&c) || ('\u{3400}'..='\u{4dbf}').contains(&c)
}

const LIGHT: &str = "啊呀呢吧嘛哦哇啦呐儿";
const ZH_DIGITS: &str = "零〇一二两三四五六七八九";
const ZH_UNITS: &str = "十百千万亿";

fn zh_digit(c: char) -> Option<u64> {
    Some(match c {
        '零' | '〇' => 0,
        '一' => 1,
        '二' | '两' => 2,
        '三' => 3,
        '四' => 4,
        '五' => 5,
        '六' => 6,
        '七' => 7,
        '八' => 8,
        '九' => 9,
        _ => return None,
    })
}

/// A run of Chinese numerals as the digits it means: positional when it has units (一千九百七十八
/// → 1978), digit by digit otherwise (一九七八 → 1978).
pub fn zh_number(run: &str) -> String {
    if !run.chars().any(|c| ZH_UNITS.contains(c)) {
        return run.chars().filter_map(zh_digit).map(|d| char::from(b'0' + d as u8)).collect();
    }
    let mut total: u64 = 0;
    let mut section: u64 = 0;
    let mut num: u64 = 0;
    for c in run.chars() {
        if let Some(d) = zh_digit(c) {
            num = d;
            continue;
        }
        let unit = match c {
            '十' => 10,
            '百' => 100,
            '千' => 1000,
            '万' => 10_000,
            '亿' => 100_000_000,
            _ => continue,
        };
        if unit >= 10_000 {
            section = (section + num) * unit;
            total += section;
            section = 0;
        } else {
            section += if num == 0 && unit == 10 { 10 } else { num * unit };
        }
        num = 0;
    }
    (total + section + num).to_string()
}

/// English number words as digits: twenty-one → 21, nineteen seventy-eight → 1978 (as pairs).
fn en_number(words: &[&str]) -> Option<(String, usize)> {
    let small = |w: &str| -> Option<u64> {
        Some(match w {
            "zero" | "oh" => 0,
            "one" => 1,
            "two" => 2,
            "three" => 3,
            "four" => 4,
            "five" => 5,
            "six" => 6,
            "seven" => 7,
            "eight" => 8,
            "nine" => 9,
            "ten" => 10,
            "eleven" => 11,
            "twelve" => 12,
            "thirteen" => 13,
            "fourteen" => 14,
            "fifteen" => 15,
            "sixteen" => 16,
            "seventeen" => 17,
            "eighteen" => 18,
            "nineteen" => 19,
            "twenty" => 20,
            "thirty" => 30,
            "forty" => 40,
            "fifty" => 50,
            "sixty" => 60,
            "seventy" => 70,
            "eighty" => 80,
            "ninety" => 90,
            _ => return None,
        })
    };
    // A year read in pairs: nineteen seventy-eight → 1978, twenty twenty → 2020.
    let pair = |w: &str| -> Option<u64> {
        let v: Option<u64> = w.split('-').map(small).sum();
        v.filter(|x| (10..100).contains(x))
    };
    if words.len() >= 2
        && let (Some(a), Some(b)) = (pair(words[0]), words.get(1).and_then(|w| pair(w).or_else(|| (*w == "hundred").then_some(0))))
            && (11..=20).contains(&a) {
                // "nineteen seventy" + "eight" as the next word.
                let mut b = b;
                let mut n = 2;
                if b % 10 == 0 && b > 0 && let Some(c) = words.get(2).and_then(|w| small(w)).filter(|c| *c < 10 && *c > 0) {
                    b += c;
                    n = 3;
                }
                return Some((format!("{a}{b:02}"), n));
            }
    let mut total = 0u64;
    let mut cur = 0u64;
    let mut used = 0;
    for w in words {
        let parts: Vec<&str> = w.split('-').collect();
        let mut v = 0u64;
        let mut ok = true;
        for p in &parts {
            match small(p) {
                Some(x) => v += x,
                None => ok = false,
            }
        }
        if ok {
            cur += v;
            used += 1;
            continue;
        }
        match *w {
            "hundred" if used > 0 => {
                cur *= 100;
                used += 1;
            }
            "thousand" if used > 0 => {
                total += cur * 1000;
                cur = 0;
                used += 1;
            }
            "million" if used > 0 => {
                total += cur * 1_000_000;
                cur = 0;
                used += 1;
            }
            "and" if used > 0 => used += 1,
            _ => break,
        }
    }
    if used == 0 {
        return None;
    }
    // A trailing "and" isn't part of it.
    let mut n = used;
    while n > 0 && words[n - 1] == "and" {
        n -= 1;
    }
    Some(((total + cur).to_string(), n))
}

const ABBR: &[(&str, &str)] = &[("mr", "mister"), ("mrs", "missus"), ("dr", "doctor"), ("st", "saint"), ("no", "number"), ("vs", "versus"), ("etc", "etcetera")];

fn push_digits(out: &mut Vec<Tok>, digits: &str, src: u32) {
    for d in digits.chars() {
        let k = intern(&format!("#{d}"));
        out.push(Tok { keys: vec![k], key: k, src, light: false, word: None });
    }
}

/// The sounds of `text`.
pub fn phon(text: &str, chinese: bool) -> Vec<Tok> {
    let simple: Vec<char> = zhconv::zhconv(text, zhconv::Variant::ZhHans).chars().collect();
    let original: Vec<char> = text.chars().collect();
    let chars = if simple.len() == original.len() { simple } else { original };
    let mut out = Vec::new();
    let mut i = 0;
    while i < chars.len() {
        let c = chars[i];
        // Digits (and full-width ones), with a decimal point inside.
        if c.is_ascii_digit() || ('０'..='９').contains(&c) {
            let start = i;
            let mut s = String::new();
            while i < chars.len() && (chars[i].is_ascii_digit() || ('０'..='９').contains(&chars[i]) || (chars[i] == ',' && i + 1 < chars.len() && chars[i + 1].is_ascii_digit())) {
                let d = chars[i];
                if d != ',' {
                    s.push(if d.is_ascii_digit() { d } else { char::from_u32(d as u32 - '０' as u32 + '0' as u32).unwrap() });
                }
                i += 1;
            }
            push_digits(&mut out, &s, start as u32);
            continue;
        }
        // A run of Chinese numerals: 两 and 一 alone are words as often as numbers, but as long as
        // both sides go the same way it doesn't matter.
        if chinese && (ZH_DIGITS.contains(c) || (ZH_UNITS.contains(c) && c != '万' && c != '亿')) {
            let start = i;
            while i < chars.len() && (ZH_DIGITS.contains(chars[i]) || ZH_UNITS.contains(chars[i])) {
                i += 1;
            }
            let run: String = chars[start..i].iter().collect();
            push_digits(&mut out, &zh_number(&run), start as u32);
            continue;
        }
        if is_han(c) {
            let mut keys: Vec<u32> = Vec::new();
            if let Some(m) = c.to_pinyin_multi() {
                for p in m {
                    let k = intern(&fuzzy(p.plain()));
                    if !keys.contains(&k) {
                        keys.push(k);
                    }
                }
            }
            if keys.is_empty() {
                let k = intern(&c.to_string());
                keys.push(k);
            }
            out.push(Tok { key: keys[0], keys, src: i as u32, light: LIGHT.contains(c), word: None });
            i += 1;
            continue;
        }
        if c.is_alphabetic() {
            let start = i;
            while i < chars.len() && (chars[i].is_alphabetic() || chars[i] == '\'' || chars[i] == '’' || (chars[i] == '-' && i + 1 < chars.len() && chars[i + 1].is_alphabetic())) {
                i += 1;
            }
            let w: String = chars[start..i].iter().collect::<String>().to_lowercase().replace('’', "'");
            // Number words: take the whole run.
            let mut ahead: Vec<String> = vec![w.clone()];
            let mut j = i;
            loop {
                let mut k = j;
                while k < chars.len() && chars[k] == ' ' {
                    k += 1;
                }
                let s0 = k;
                while k < chars.len() && (chars[k].is_alphabetic() || chars[k] == '-') {
                    k += 1;
                }
                if s0 == k || ahead.len() > 8 {
                    break;
                }
                ahead.push(chars[s0..k].iter().collect::<String>().to_lowercase());
                j = k;
            }
            let refs: Vec<&str> = ahead.iter().map(String::as_str).collect();
            if let Some((digits, n)) = en_number(&refs) {
                // Advance past the n words used.
                let mut k = start;
                let mut seen = 0;
                while k < chars.len() && seen < n {
                    while k < chars.len() && !chars[k].is_alphabetic() {
                        k += 1;
                    }
                    while k < chars.len() && (chars[k].is_alphabetic() || chars[k] == '-') {
                        k += 1;
                    }
                    seen += 1;
                }
                push_digits(&mut out, &digits, start as u32);
                i = k;
                continue;
            }
            let w = ABBR.iter().find(|(a, _)| *a == w && chars.get(i) == Some(&'.')).map(|(_, b)| b.to_string()).unwrap_or(w);
            let k = intern(&w);
            out.push(Tok { keys: vec![k], key: k, src: start as u32, light: ["a", "the", "uh", "um"].contains(&w.as_str()), word: Some(w) });
            continue;
        }
        i += 1;
    }
    out
}

/// Two sounds the same, as near as the comparison goes.
pub fn same(a: &Tok, b: &Tok) -> bool {
    if a.keys.iter().any(|k| b.keys.contains(k)) {
        return true;
    }
    match (&a.word, &b.word) {
        (Some(x), Some(y)) if x.len() >= 5 && y.len() >= 5 => near(x, y),
        _ => false,
    }
}

fn near(a: &str, b: &str) -> bool {
    let (a, b): (Vec<char>, Vec<char>) = (a.chars().collect(), b.chars().collect());
    if a.len().abs_diff(b.len()) > 1 {
        return false;
    }
    let mut prev: Vec<usize> = (0..=b.len()).collect();
    for i in 1..=a.len() {
        let mut cur = vec![i; b.len() + 1];
        for j in 1..=b.len() {
            cur[j] = (prev[j] + 1).min(cur[j - 1] + 1).min(prev[j - 1] + (a[i - 1] != b[j - 1]) as usize);
        }
        prev = cur;
    }
    prev[b.len()] <= 1
}

#[cfg(test)]
mod tests {
    use super::*;

    fn keys(t: &str) -> Vec<Vec<u32>> {
        phon(t, true).into_iter().map(|t| t.keys).collect()
    }

    fn eq(a: &str, b: &str, zh: bool) -> bool {
        let (x, y) = (phon(a, zh), phon(b, zh));
        x.len() == y.len() && x.iter().zip(&y).all(|(p, q)| same(p, q))
    }

    #[test]
    fn numbers_however_written() {
        assert!(eq("1978年", "一九七八年", true));
        assert!(eq("一千九百七十八", "1978", true));
        assert!(eq("十二", "12", true));
        assert!(eq("两个", "2个", true));
        assert_eq!(zh_number("三万五千"), "35000");
        assert_eq!(zh_number("一百零五"), "105");
    }

    #[test]
    fn homophones_and_traditional_meet() {
        assert!(eq("寶玉", "宝玉", true));
        // A transcriber writes the sound, not always the word.
        assert!(eq("黛玉", "代玉", true));
        // 平翘舌, 前后鼻音.
        assert!(eq("知识", "资私", true));
        // A 多音字 in either reading.
        assert!(eq("银行", "银航", true));
        assert!(!eq("宝玉", "宝钗", true));
        assert_eq!(keys("，。！").len(), 0);
    }

    #[test]
    fn english_words_numbers_and_slips() {
        assert!(eq("In 1978, Mr. Darcy said", "in nineteen seventy eight mister darcy said", false));
        assert!(eq("1978", "one thousand nine hundred seventy eight", false));
        assert!(eq("1900", "nineteen hundred", false));
        assert!(eq("twenty-one", "21", false));
        assert!(eq("Elizabeth", "Elisabeth", false));
        assert!(!eq("cat", "cut", false));
    }
}
