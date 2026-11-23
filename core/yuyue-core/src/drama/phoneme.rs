//! Shared compact flat-arena lexicon and text-normalization helpers for the on-device neural
//! TTS frontends (`kokoro` and `zipvoice`).

use std::collections::HashMap;
use std::path::{Path, PathBuf};

#[derive(Clone, Copy)]
struct LexEntry {
    key_off: u32,
    key_len: u16,
    tok_off: u32,
    tok_len: u8,
}

#[derive(Default)]
pub struct FlatLexicon {
    pub dir: PathBuf,
    pub tokens: HashMap<String, u16>,
    keys_arena: String,
    toks_arena: Vec<u16>,
    entries: Vec<LexEntry>,
    pub max_cjk_phrase: usize,
}

impl FlatLexicon {
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    fn key_of(&self, e: &LexEntry) -> &str {
        let s = e.key_off as usize;
        &self.keys_arena[s..s + e.key_len as usize]
    }

    pub fn lookup(&self, key: &str) -> Option<&[u16]> {
        let idx = self
            .entries
            .binary_search_by(|e| self.key_of(e).cmp(key))
            .ok()?;
        let e = &self.entries[idx];
        let s = e.tok_off as usize;
        Some(&self.toks_arena[s..s + e.tok_len as usize])
    }

    /// Loads `tokens.txt`, the first matching Chinese lexicon file, and English lexicon files
    /// (stopping after the first English file when `first_en_only` is true).
    pub fn load(dir: &Path, zh_files: &[&str], en_files: &[&str], first_en_only: bool) -> Option<Self> {
        let tok_txt = std::fs::read_to_string(dir.join("tokens.txt")).ok()?;
        let mut tokens: HashMap<String, u16> = HashMap::with_capacity(400);
        for line in tok_txt.lines() {
            let line = line.trim_end_matches(['\r', '\n']);
            let Some((sym, id_s)) = line.rsplit_once('\t').or_else(|| line.rsplit_once(' ')) else {
                continue;
            };
            if let Ok(id) = id_s.trim().parse::<u16>() {
                tokens.insert(sym.to_string(), id);
            }
        }
        if tokens.is_empty() {
            return None;
        }

        let mut raw_pairs: Vec<(String, Vec<u16>)> = Vec::with_capacity(240_000);
        let mut max_cjk_phrase = 1usize;

        for name in zh_files {
            let Ok(txt) = std::fs::read_to_string(dir.join(name)) else { continue };
            for line in txt.lines() {
                let line = line.trim();
                if line.is_empty() || line.starts_with('#') {
                    continue;
                }
                let mut it = line.split_whitespace();
                let Some(word) = it.next() else { continue };
                let ids: Vec<u16> = it.filter_map(|ph| tokens.get(ph).copied()).collect();
                if !ids.is_empty() && ids.len() <= u8::MAX as usize {
                    let cjk_len = word.chars().count();
                    if word.chars().any(is_cjk) && cjk_len > max_cjk_phrase && cjk_len <= 12 {
                        max_cjk_phrase = cjk_len;
                    }
                    raw_pairs.push((word.to_lowercase(), ids));
                }
            }
            break;
        }

        for name in en_files {
            let Ok(txt) = std::fs::read_to_string(dir.join(name)) else { continue };
            for line in txt.lines() {
                let line = line.trim();
                if line.is_empty() || line.starts_with('#') {
                    continue;
                }
                let (word, phones_part) = if let Some((w, r)) = line.split_once("||") {
                    (w.trim(), r.trim())
                } else if let Some((w, r)) = line.split_once(' ') {
                    (w.trim(), r.trim())
                } else {
                    continue;
                };
                if word.is_empty() || word.len() > u16::MAX as usize {
                    continue;
                }
                let mut ids = Vec::new();
                for ph in phones_part.split_whitespace() {
                    if let Some(&id) = tokens.get(ph) {
                        ids.push(id);
                    } else {
                        // Compound stress markers like `ˌeɪ` attached without spaces: scan longest token.
                        let mut rest = ph;
                        while !rest.is_empty() {
                            let matched = (1..=rest.len())
                                .rev()
                                .filter(|&k| rest.is_char_boundary(k))
                                .find_map(|k| tokens.get(&rest[..k]).map(|&id| (k, id)));
                            if let Some((k, id)) = matched {
                                ids.push(id);
                                rest = &rest[k..];
                            } else {
                                let step = rest.chars().next().map(char::len_utf8).unwrap_or(1);
                                rest = &rest[step..];
                            }
                        }
                    }
                }
                if !ids.is_empty() && ids.len() <= u8::MAX as usize {
                    raw_pairs.push((word.to_lowercase(), ids));
                }
            }
            if first_en_only {
                break;
            }
        }

        raw_pairs.sort_by(|a, b| a.0.cmp(&b.0));
        raw_pairs.dedup_by(|a, b| a.0 == b.0);

        let key_bytes: usize = raw_pairs.iter().map(|(k, _)| k.len()).sum();
        let tok_count: usize = raw_pairs.iter().map(|(_, v)| v.len()).sum();
        let mut keys_arena = String::with_capacity(key_bytes);
        let mut toks_arena = Vec::with_capacity(tok_count);
        let mut entries = Vec::with_capacity(raw_pairs.len());

        for (k, v) in raw_pairs {
            if k.len() > u16::MAX as usize {
                continue;
            }
            let key_off = keys_arena.len() as u32;
            let key_len = k.len() as u16;
            keys_arena.push_str(&k);
            let tok_off = toks_arena.len() as u32;
            let tok_len = v.len() as u8;
            toks_arena.extend_from_slice(&v);
            entries.push(LexEntry { key_off, key_len, tok_off, tok_len });
        }

        Some(Self { dir: dir.to_path_buf(), tokens, keys_arena, toks_arena, entries, max_cjk_phrase })
    }
}

pub fn is_cjk(c: char) -> bool {
    ('\u{4e00}'..='\u{9fff}').contains(&c) || ('\u{3400}'..='\u{4dbf}').contains(&c)
}

pub fn count_units(s: &str, chinese: bool) -> usize {
    if chinese {
        s.chars().filter(|&c| is_cjk(c) || c.is_ascii_alphanumeric()).count()
    } else {
        s.split_whitespace().filter(|w| w.chars().any(char::is_alphanumeric)).count()
    }
}

/// Splits long sentences on punctuation boundaries while respecting `{word|reading}` braces.
pub fn split_clauses(text: &str, lang: &str, max_zh: usize, max_en: usize, merge_tail_under: usize) -> Vec<String> {
    let clean = text.trim();
    if clean.is_empty() {
        return Vec::new();
    }
    let chinese = !lang.to_ascii_lowercase().starts_with("en") || clean.chars().any(is_cjk);
    let max_units = if chinese { max_zh } else { max_en };
    if count_units(clean, chinese) <= max_units {
        return vec![clean.to_string()];
    }

    let mut pieces = Vec::new();
    let mut cur = String::new();
    let mut depth = 0i32;
    for ch in clean.chars() {
        if ch == '{' {
            depth += 1;
        } else if ch == '}' {
            depth = (depth - 1).max(0);
        }
        cur.push(ch);
        if depth == 0 && matches!(ch, '，' | '。' | '；' | '：' | '！' | '？' | '…' | '—' | ',' | '.' | ';' | ':' | '!' | '?') {
            if !cur.trim().is_empty() {
                pieces.push(std::mem::take(&mut cur));
            }
        }
    }
    if !cur.trim().is_empty() {
        pieces.push(cur);
    }

    let mut out: Vec<String> = Vec::new();
    let mut buf = String::new();
    for p in pieces {
        let p = p.trim();
        if p.is_empty() {
            continue;
        }
        let cand = if buf.is_empty() {
            p.to_string()
        } else if chinese {
            format!("{buf}{p}")
        } else {
            format!("{buf} {p}")
        };
        if !buf.is_empty() && count_units(&cand, chinese) > max_units {
            out.push(std::mem::take(&mut buf));
            buf = p.to_string();
        } else {
            buf = cand;
        }
    }
    if !buf.is_empty() {
        if merge_tail_under > 0 && out.last().is_some() && count_units(&buf, chinese) < merge_tail_under {
            let last = out.last_mut().unwrap();
            if !chinese {
                last.push(' ');
            }
            last.push_str(&buf);
        } else {
            out.push(buf);
        }
    }
    out
}

pub fn split_spoken_parts(text: &str) -> Vec<(String, Option<String>)> {
    let mut out = Vec::new();
    let mut plain = String::new();
    let mut rest = text;
    while let Some(a) = rest.find('{') {
        let Some(b) = rest[a..].find('}').map(|b| a + b) else { break };
        let inner = &rest[a + 1..b];
        let Some((word, reading)) = inner.split_once('|') else {
            plain.push_str(&rest[..=b]);
            rest = &rest[b + 1..];
            continue;
        };
        plain.push_str(&rest[..a]);
        if !plain.is_empty() {
            out.push((std::mem::take(&mut plain), None));
        }
        out.push((word.to_string(), Some(reading.to_string())));
        rest = &rest[b + 1..];
    }
    plain.push_str(rest);
    if !plain.is_empty() {
        out.push((plain, None));
    }
    out
}

pub fn append_english_word(word: &str, st: &FlatLexicon, out: &mut Vec<i64>) {
    if let Some(ids) = st.lookup(word) {
        out.extend(ids.iter().map(|&x| x as i64));
        return;
    }
    if word.contains('-') {
        for (k, sub) in word.split('-').filter(|s| !s.is_empty()).enumerate() {
            if k > 0
                && let Some(&sp) = st.tokens.get(" ")
            {
                out.push(sp as i64);
            }
            append_english_word(sub, st, out);
        }
        return;
    }
    if let Some(stem) = word.strip_suffix("'s") {
        append_english_word(stem, st, out);
        if let Some(&z) = st.tokens.get("z") {
            out.push(z as i64);
        }
        return;
    }
    if let Some(stem) = word.strip_suffix('s')
        && stem.len() >= 3
        && let Some(ids) = st.lookup(stem)
    {
        out.extend(ids.iter().map(|&x| x as i64));
        if let Some(&z) = st.tokens.get("z") {
            out.push(z as i64);
        }
        return;
    }
    if let Some(stem) = word.strip_suffix("ed")
        && stem.len() >= 3
        && let Some(ids) = st.lookup(stem)
    {
        out.extend(ids.iter().map(|&x| x as i64));
        if let Some(&d) = st.tokens.get("d") {
            out.push(d as i64);
        }
        return;
    }
    if let Some(stem) = word.strip_suffix("ing")
        && stem.len() >= 3
        && let Some(ids) = st.lookup(stem)
    {
        out.extend(ids.iter().map(|&x| x as i64));
        for ph in ["ɪ", "ŋ"] {
            if let Some(&id) = st.tokens.get(ph) {
                out.push(id as i64);
            }
        }
        return;
    }
    for ch in word.chars() {
        let s = ch.to_string();
        if let Some(ids) = st.lookup(&s) {
            out.extend(ids.iter().map(|&x| x as i64));
        } else if let Some(&id) = st.tokens.get(&s) {
            out.push(id as i64);
        }
    }
}

pub fn normalize_pinyin_tone(raw: &str) -> (String, u8) {
    let mut letters = String::new();
    let mut tone = 5u8;
    for ch in raw.trim().to_lowercase().chars() {
        let mapped = match ch {
            'ā' => Some(('a', 1)), 'á' => Some(('a', 2)), 'ǎ' => Some(('a', 3)), 'à' => Some(('a', 4)),
            'ē' => Some(('e', 1)), 'é' => Some(('e', 2)), 'ě' => Some(('e', 3)), 'è' => Some(('e', 4)),
            'ī' => Some(('i', 1)), 'í' => Some(('i', 2)), 'ǐ' => Some(('i', 3)), 'ì' => Some(('i', 4)),
            'ō' => Some(('o', 1)), 'ó' => Some(('o', 2)), 'ǒ' => Some(('o', 3)), 'ò' => Some(('o', 4)),
            'ū' => Some(('u', 1)), 'ú' => Some(('u', 2)), 'ǔ' => Some(('u', 3)), 'ù' => Some(('u', 4)),
            'ǖ' => Some(('v', 1)), 'ǘ' => Some(('v', 2)), 'ǚ' => Some(('v', 3)), 'ǜ' => Some(('v', 4)),
            'ü' => Some(('v', 0)),
            '1'..='5' => {
                tone = ch as u8 - b'0';
                None
            }
            'a'..='z' => Some((ch, 0)),
            _ => None,
        };
        if let Some((c, t)) = mapped {
            letters.push(c);
            if t > 0 {
                tone = t;
            }
        }
    }
    (letters, tone)
}

pub fn expand_numbers(s: &str, chinese: bool) -> String {
    let mut out = String::with_capacity(s.len());
    let chars: Vec<char> = s.chars().collect();
    let n = chars.len();
    let mut i = 0usize;
    while i < n {
        if chars[i].is_ascii_digit() {
            let start = i;
            while i < n && chars[i].is_ascii_digit() {
                i += 1;
            }
            let int_part: String = chars[start..i].iter().collect();
            let frac_part = if i + 1 < n && chars[i] == '.' && chars[i + 1].is_ascii_digit() {
                i += 1;
                let fs = i;
                while i < n && chars[i].is_ascii_digit() {
                    i += 1;
                }
                Some(chars[fs..i].iter().collect::<String>())
            } else {
                None
            };
            let next_non_ws = chars[i..].iter().copied().find(|x| !x.is_whitespace());
            let is_year = chinese && frac_part.is_none() && int_part.len() == 4 && next_non_ws == Some('年');
            if chinese {
                if is_year || int_part.len() > 8 || int_part.starts_with('0') {
                    out.push_str(&digits_zh(&int_part));
                } else if let Ok(val) = int_part.parse::<u64>() {
                    out.push_str(&cardinal_zh(val));
                } else {
                    out.push_str(&digits_zh(&int_part));
                }
                if let Some(fp) = frac_part {
                    out.push('点');
                    out.push_str(&digits_zh(&fp));
                }
            } else {
                if !out.is_empty() && !out.ends_with(' ') {
                    out.push(' ');
                }
                if let Ok(val) = int_part.parse::<u64>()
                    && val <= 9999
                {
                    out.push_str(&cardinal_en(val));
                } else {
                    out.push_str(&digits_en(&int_part));
                }
                if let Some(fp) = frac_part {
                    out.push_str(" point ");
                    out.push_str(&digits_en(&fp));
                }
                if i < n && !chars[i].is_whitespace() && !chars[i].is_ascii_punctuation() {
                    out.push(' ');
                }
            }
        } else {
            out.push(chars[i]);
            i += 1;
        }
    }
    out
}

fn digits_zh(d: &str) -> String {
    const MAP: [char; 10] = ['零', '一', '二', '三', '四', '五', '六', '七', '八', '九'];
    d.chars().filter_map(|c| c.to_digit(10).map(|k| MAP[k as usize])).collect()
}

fn cardinal_zh(n: u64) -> String {
    if n == 0 {
        return "零".to_string();
    }
    if n >= 10_000 {
        let hi = n / 10_000;
        let lo = n % 10_000;
        if lo == 0 {
            return format!("{}万", cardinal_zh(hi));
        }
        if lo < 1000 {
            return format!("{}万零{}", cardinal_zh(hi), cardinal_zh(lo));
        }
        return format!("{}万{}", cardinal_zh(hi), cardinal_zh(lo));
    }
    const DIG: [&str; 10] = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    const UNIT: [&str; 4] = ["", "十", "百", "千"];
    let mut s = String::new();
    let mut zero = false;
    for (p, div) in [(3, 1000u64), (2, 100), (1, 10), (0, 1)] {
        let d = ((n / div) % 10) as usize;
        if d == 0 {
            if !s.is_empty() {
                zero = true;
            }
        } else {
            if zero {
                s.push_str("零");
                zero = false;
            }
            if !(p == 1 && d == 1 && s.is_empty()) {
                s.push_str(DIG[d]);
            }
            s.push_str(UNIT[p]);
        }
    }
    s
}

fn digits_en(d: &str) -> String {
    const MAP: [&str; 10] = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    d.chars()
        .filter_map(|c| c.to_digit(10).map(|k| MAP[k as usize]))
        .collect::<Vec<_>>()
        .join(" ")
}

fn cardinal_en(n: u64) -> String {
    const ONES: [&str; 20] = [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];
    const TENS: [&str; 10] = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    if n < 20 {
        return ONES[n as usize].to_string();
    }
    if n < 100 {
        let t = TENS[(n / 10) as usize];
        let r = n % 10;
        return if r == 0 { t.to_string() } else { format!("{t} {}", ONES[r as usize]) };
    }
    if n < 1000 {
        let h = ONES[(n / 100) as usize];
        let r = n % 100;
        return if r == 0 { format!("{h} hundred") } else { format!("{h} hundred {}", cardinal_en(r)) };
    }
    let th = cardinal_en(n / 1000);
    let r = n % 1000;
    if r == 0 { format!("{th} thousand") } else { format!("{th} thousand {}", cardinal_en(r)) }
}
