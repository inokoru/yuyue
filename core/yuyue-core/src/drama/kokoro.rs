//! Pure-computation frontend, compact lexicon arena, speaker catalog, and 24 kHz loudness DSP
//! for the cross-platform lightweight offline neural TTS model (`Kokoro-82M-v1.1-zh Int8`, Apache-2.0).
//!
//! Shares identical tokenization (`tokens.txt`, `lexicon-zh.txt`, `lexicon-us-en.txt`), `{词|读音}`
//! polyphone overrides, Traditional-to-Simplified conversion, Chinese/English number expansion,
//! Misaki-zh Zhuyin syllable fallback, clause splitting (`≤ 400` inner tokens for Kokoro's 510
//! style-vector length buckets), and −20 LUFS loudness normalization across macOS, iOS, Android,
//! and Windows.

use std::collections::HashMap;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use pinyin::ToPinyin;
use serde::{Deserialize, Serialize};

use super::CharacterGender;

pub const KOKORO_RATE: u32 = 24_000;
pub const KOKORO_MAX_BUCKET: usize = 510;

/// Curated speaker entry from `kokoro-int8-multi-lang-v1_1` (`103` total speakers in `voices.bin`).
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct KokoroSpeaker {
    /// `"kokoro.zf_001"`, `"kokoro.zm_010"`, `"kokoro.af_maple"`, etc.
    pub id: String,
    /// Raw speaker key in Kokoro (`zf_001`, `zm_010`, `af_maple`, `bf_vale`, ...).
    pub voice_id: String,
    /// Zero-based index `[0..103)` into `voices.bin` (`[103, 510, 256]` float32).
    pub sid: u32,
    /// Display name shown in the voice list.
    pub name: String,
    /// Primary language (`"zh"` or `"en"`).
    pub lang: String,
    pub gender: CharacterGender,
    /// Recommended default narrator for this language.
    pub narrator: bool,
    /// Short timbre summary (`"温润自然 · 女声"`, `"沉稳舒缓 · 男声"`, etc.).
    pub summary: String,
}

#[derive(Clone, Copy)]
struct LexEntry {
    key_off: u32,
    key_len: u16,
    tok_off: u32,
    tok_len: u8,
}

#[derive(Default)]
struct KokoroLexicon {
    dir: PathBuf,
    tokens: HashMap<String, u16>,
    keys_arena: String,
    toks_arena: Vec<u16>,
    entries: Vec<LexEntry>,
    max_cjk_phrase: usize,
}

impl KokoroLexicon {
    fn key_of(&self, e: &LexEntry) -> &str {
        let s = e.key_off as usize;
        &self.keys_arena[s..s + e.key_len as usize]
    }

    fn lookup(&self, key: &str) -> Option<&[u16]> {
        let idx = self
            .entries
            .binary_search_by(|e| self.key_of(e).cmp(key))
            .ok()?;
        let e = &self.entries[idx];
        let s = e.tok_off as usize;
        Some(&self.toks_arena[s..s + e.tok_len as usize])
    }
}

fn kokoro_state() -> &'static Mutex<KokoroLexicon> {
    static K: OnceLock<Mutex<KokoroLexicon>> = OnceLock::new();
    K.get_or_init(|| Mutex::new(KokoroLexicon::default()))
}

/// Curated 20 standout narrators from `Kokoro-82M-v1.1-zh` (12 Mandarin + 8 English),
/// ordered with default narrators first.
pub fn builtin_speakers() -> Vec<KokoroSpeaker> {
    let raw: &[(&str, u32, &str, &str, CharacterGender, bool, &str)] = &[
        // Mandarin Chinese — Female
        ("zf_001", 3, "清禾", "zh", CharacterGender::F, true, "温润清朗 · 默认中文女声"),
        ("zf_002", 4, "知夏", "zh", CharacterGender::F, false, "柔和舒缓 · 适合散文与小说"),
        ("zf_004", 6, "云舒", "zh", CharacterGender::F, false, "沉静端庄 · 适合历史与社科"),
        ("zf_006", 8, "茗烟", "zh", CharacterGender::F, false, "轻快自然 · 适合随笔与故事"),
        ("zf_018", 12, "晚晴", "zh", CharacterGender::F, false, "柔和细腻 · 适合夜读"),
        ("zf_028", 20, "素心", "zh", CharacterGender::F, false, "干净利落 · 适合非虚构长文"),
        // Mandarin Chinese — Male
        ("zm_010", 59, "沉舟", "zh", CharacterGender::M, true, "醇厚稳重 · 推荐中文男声"),
        ("zm_009", 58, "松风", "zh", CharacterGender::M, false, "清朗平和 · 适合纪实与传记"),
        ("zm_012", 61, "远山", "zh", CharacterGender::M, false, "低沉内敛 · 适合经典文学"),
        ("zm_025", 67, "怀瑾", "zh", CharacterGender::M, false, "青年明快 · 适合科幻与通识"),
        ("zm_035", 73, "竹轩", "zh", CharacterGender::M, false, "温厚从容 · 适合长篇慢听"),
        ("zm_052", 78, "景行", "zh", CharacterGender::M, false, "清晰有力 · 适合论著与技术"),
        // English — Female & Male (`kokoro-multi-lang-v1_1` ships `af_maple`, `af_sol`, `bf_vale`,
        // plus bilingual voices capable of fluent English reading)
        ("af_maple", 0, "Maple", "en", CharacterGender::F, true, "Warm American narrator · Default"),
        ("bf_vale", 2, "Vale", "en", CharacterGender::F, true, "Crisp British RP narrator"),
        ("af_sol", 1, "Sol", "en", CharacterGender::F, false, "Bright conversational American"),
        ("zm_010", 59, "Arthur (Bilingual)", "en", CharacterGender::M, false, "Calm bilingual male narrator"),
    ];
    let mut out = Vec::with_capacity(raw.len());
    let mut seen = std::collections::HashSet::new();
    for &(voice_id, sid, name, lang, gender, narrator, summary) in raw {
        let id = if seen.insert(voice_id) {
            format!("kokoro.{voice_id}")
        } else {
            format!("kokoro.{voice_id}.{lang}")
        };
        out.push(KokoroSpeaker {
            id,
            voice_id: voice_id.to_string(),
            sid,
            name: name.to_string(),
            lang: lang.to_string(),
            gender,
            narrator,
            summary: summary.to_string(),
        });
    }
    out
}

/// Loads `tokens.txt`, `lexicon-zh.txt`, and `lexicon-us-en.txt` / `lexicon-gb-en.txt` from `dir`
/// into a compact sorted arena. Idempotent if `dir` is already loaded.
pub fn set_model_dir(dir: &Path) -> usize {
    if let Ok(st) = kokoro_state().lock()
        && st.dir == dir
        && !st.tokens.is_empty()
    {
        return st.entries.len();
    }
    let Ok(tok_txt) = std::fs::read_to_string(dir.join("tokens.txt")) else {
        return 0;
    };
    let mut tokens: HashMap<String, u16> = HashMap::with_capacity(256);
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
        return 0;
    }

    let mut raw_pairs: Vec<(String, Vec<u16>)> = Vec::with_capacity(250_000);
    let mut max_cjk_phrase = 1usize;

    // 1. Mandarin lexicon (`word ph1 ph2 ...` in Misaki-zh Zhuyin notation)
    for name in ["lexicon-zh.txt", "lexicon.txt"] {
        let p = dir.join(name);
        if let Ok(txt) = std::fs::read_to_string(&p) {
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
    }

    // 2. English lexicon (`lexicon-us-en.txt` primary, `lexicon-gb-en.txt` / `lexicon-en.txt` supplement)
    for name in ["lexicon-us-en.txt", "lexicon-en.txt", "lexicon-gb-en.txt"] {
        let p = dir.join(name);
        let Ok(txt) = std::fs::read_to_string(&p) else { continue };
        for line in txt.lines() {
            let line = line.trim();
            if line.is_empty() || line.starts_with('#') {
                continue;
            }
            let Some((word, phones_part)) = line.split_once(' ') else { continue };
            let word = word.trim();
            if word.is_empty() || word.len() > u16::MAX as usize {
                continue;
            }
            let mut ids = Vec::new();
            for ph in phones_part.split_whitespace() {
                if let Some(&id) = tokens.get(ph) {
                    ids.push(id);
                } else {
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

    let count = entries.len();
    if let Ok(mut st) = kokoro_state().lock() {
        *st = KokoroLexicon { dir: dir.to_path_buf(), tokens, keys_arena, toks_arena, entries, max_cjk_phrase };
    }
    count
}

fn is_cjk(c: char) -> bool {
    ('\u{4e00}'..='\u{9fff}').contains(&c) || ('\u{3400}'..='\u{4dbf}').contains(&c)
}

/// Splits long sentences into natural breath clauses (`≤ 72` Chinese chars or `≤ 55` English words)
/// so each Kokoro pass stays comfortably inside the 510-token style embedding window and maintains
/// crisp prosody without end-of-utterance rush.
pub fn split_clauses(text: &str, lang: &str) -> Vec<String> {
    let clean = text.trim();
    if clean.is_empty() {
        return Vec::new();
    }
    let chinese = !lang.to_ascii_lowercase().starts_with("en") || clean.chars().any(is_cjk);
    let max_units = if chinese { 68usize } else { 52usize };
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
        out.push(buf);
    }
    out
}

fn count_units(s: &str, chinese: bool) -> usize {
    if chinese {
        s.chars().filter(|&c| is_cjk(c) || c.is_ascii_alphanumeric()).count()
    } else {
        s.split_whitespace().filter(|w| w.chars().any(char::is_alphanumeric)).count()
    }
}

/// Tokenizes `text` into Kokoro inner phoneme token IDs (caller wraps with BOS/EOS `0` and selects
/// style bucket `min(tokens.len(), 509)`). Caps output at `509` tokens so ONNX never exceeds `512`.
pub fn tokenize(text: &str, lang: &str) -> Vec<i64> {
    let Ok(st) = kokoro_state().lock() else { return Vec::new() };
    if st.tokens.is_empty() {
        return Vec::new();
    }
    let chinese_ctx = !lang.to_ascii_lowercase().starts_with("en") || text.chars().any(is_cjk);
    let mut out: Vec<i64> = Vec::new();

    for (seg, reading) in split_spoken_parts(text) {
        if let Some(py_str) = reading {
            for syl in py_str.split_whitespace() {
                append_pinyin_kokoro(syl, &st.tokens, &mut out);
            }
        } else {
            tokenize_plain(&seg, chinese_ctx, &st, &mut out);
        }
    }

    // Ensure clean terminal punctuation so Kokoro's duration predictor resolves the final cadence.
    let punct_ids: [i64; 6] = [
        st.tokens.get(".").copied().unwrap_or(4) as i64,
        st.tokens.get(",").copied().unwrap_or(3) as i64,
        st.tokens.get("!").copied().unwrap_or(5) as i64,
        st.tokens.get("?").copied().unwrap_or(6) as i64,
        st.tokens.get(";").copied().unwrap_or(1) as i64,
        st.tokens.get(":").copied().unwrap_or(2) as i64,
    ];
    if let Some(&last) = out.last()
        && !punct_ids.contains(&last)
    {
        out.push(punct_ids[0]);
    }
    if out.len() >= KOKORO_MAX_BUCKET {
        out.truncate(KOKORO_MAX_BUCKET - 1);
    }
    out
}

fn split_spoken_parts(text: &str) -> Vec<(String, Option<String>)> {
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
        let _ = word;
        out.push((word.to_string(), Some(reading.to_string())));
        rest = &rest[b + 1..];
    }
    plain.push_str(rest);
    if !plain.is_empty() {
        out.push((plain, None));
    }
    out
}

fn tokenize_plain(raw: &str, chinese_ctx: bool, st: &KokoroLexicon, out: &mut Vec<i64>) {
    let simp = if raw.chars().any(is_cjk) {
        zhconv::zhconv(raw, zhconv::Variant::ZhHans)
    } else {
        raw.to_string()
    };
    let expanded = expand_numbers(&simp, chinese_ctx);
    let chars: Vec<char> = expanded.chars().collect();
    let n = chars.len();
    let mut i = 0usize;

    while i < n {
        let c = chars[i];
        if matches!(c, '「' | '」' | '『' | '』' | '《' | '》' | '（' | '）' | '[' | ']' | '‘' | '’') {
            i += 1;
            continue;
        }
        if let Some(p) = map_punct(c) {
            if let Some(&id) = st.tokens.get(p) {
                if out.last().copied() != Some(id as i64) {
                    out.push(id as i64);
                }
            }
            i += 1;
            continue;
        }
        if c.is_whitespace() {
            let prev_latin = i > 0 && chars[i - 1].is_ascii_alphabetic();
            let next_latin = chars.get(i + 1).is_some_and(|x| x.is_ascii_alphabetic());
            if prev_latin && next_latin
                && let Some(&sp) = st.tokens.get(" ")
            {
                out.push(sp as i64);
            }
            i += 1;
            continue;
        }
        if is_cjk(c) {
            let max_len = st.max_cjk_phrase.min(n - i);
            let mut matched = false;
            for len in (1..=max_len).rev() {
                if !chars[i..i + len].iter().all(|&x| is_cjk(x)) {
                    continue;
                }
                let phrase: String = chars[i..i + len].iter().collect();
                if let Some(ids) = st.lookup(&phrase) {
                    out.extend(ids.iter().map(|&x| x as i64));
                    i += len;
                    matched = true;
                    break;
                }
            }
            if !matched {
                if let Some(py) = c.to_pinyin() {
                    append_pinyin_kokoro(py.with_tone_num_end(), &st.tokens, out);
                }
                i += 1;
            }
            continue;
        }
        if c.is_ascii_alphabetic() || c == '\'' {
            let start = i;
            while i < n && (chars[i].is_ascii_alphabetic() || chars[i] == '\'' || chars[i] == '-') {
                i += 1;
            }
            let word = chars[start..i].iter().collect::<String>().trim_matches('\'').to_ascii_lowercase();
            if word.is_empty() {
                continue;
            }
            // Add space when transitioning from CJK directly to an English word without whitespace.
            if start > 0
                && is_cjk(chars[start - 1])
                && let Some(&sp) = st.tokens.get(" ")
            {
                out.push(sp as i64);
            }
            append_english_word(&word, st, out);
            if i < n
                && is_cjk(chars[i])
                && let Some(&sp) = st.tokens.get(" ")
            {
                out.push(sp as i64);
            }
            continue;
        }
        i += 1;
    }
}

fn map_punct(c: char) -> Option<&'static str> {
    match c {
        '，' | '、' | ',' => Some(","),
        '：' | ':' => Some(":"),
        '；' | ';' => Some(";"),
        '。' | '.' => Some("."),
        '？' | '?' => Some("?"),
        '！' | '!' => Some("!"),
        '—' => Some("—"),
        '…' => Some("…"),
        '“' => Some("“"),
        '”' => Some("”"),
        '"' => Some("\""),
        '(' => Some("("),
        ')' => Some(")"),
        _ => None,
    }
}

fn append_english_word(word: &str, st: &KokoroLexicon, out: &mut Vec<i64>) {
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

/// Maps any Mandarin pinyin syllable (`jiǎ`, `jia3`, `shuang1`, `dianr3`, `lü4`) into exact
/// Misaki-zh Zhuyin + compact final symbols + tone (`1..5`) token IDs.
fn append_pinyin_kokoro(raw: &str, tokens: &HashMap<String, u16>, out: &mut Vec<i64>) {
    let (mut letters, tone) = normalize_pinyin_tone(raw);
    if letters.is_empty() {
        return;
    }
    let erhua = letters != "er" && letters.ends_with('r') && letters.len() > 2;
    if erhua {
        letters.pop();
    }

    // Zero-initial `y*` and `w*` canonicalization to underlying medial + nucleus.
    if let Some(rest) = letters.strip_prefix("yu") {
        letters = match rest {
            "" => "v".to_string(),
            "e" => "ve".to_string(),
            "an" => "van".to_string(),
            "n" => "vn".to_string(),
            _ => format!("v{rest}"),
        };
    } else if let Some(rest) = letters.strip_prefix('y') {
        letters = match rest {
            "i" | "" => "i".to_string(),
            "ou" => "iou".to_string(),
            "in" => "in".to_string(),
            "ing" => "ing".to_string(),
            "ong" => "iong".to_string(),
            _ => format!("i{rest}"),
        };
    } else if let Some(rest) = letters.strip_prefix('w') {
        letters = match rest {
            "u" | "" => "u".to_string(),
            "ei" => "uei".to_string(),
            "en" => "uen".to_string(),
            "eng" => "weng".to_string(),
            _ => format!("u{rest}"),
        };
    }

    const INITIALS: &[(&str, &str)] = &[
        ("zh", "ㄓ"), ("ch", "ㄔ"), ("sh", "ㄕ"),
        ("b", "ㄅ"), ("p", "ㄆ"), ("m", "ㄇ"), ("f", "ㄈ"),
        ("d", "ㄉ"), ("t", "ㄊ"), ("n", "ㄋ"), ("l", "ㄌ"),
        ("g", "ㄍ"), ("k", "ㄎ"), ("h", "ㄏ"),
        ("j", "ㄐ"), ("q", "ㄑ"), ("x", "ㄒ"),
        ("r", "ㄖ"), ("z", "ㄗ"), ("c", "ㄘ"), ("s", "ㄙ"),
    ];

    let (init_raw, init_sym, mut fin) = INITIALS
        .iter()
        .find_map(|&(p, sym)| letters.strip_prefix(p).map(|r| (Some(p), Some(sym), r.to_string())))
        .unwrap_or((None, None, letters));

    if matches!(init_raw, Some("j" | "q" | "x")) {
        fin = match fin.as_str() {
            "u" => "v".to_string(),
            "ue" => "ve".to_string(),
            "uan" => "van".to_string(),
            "un" => "vn".to_string(),
            _ => fin,
        };
    }

    if let Some(sym) = init_sym
        && let Some(&id) = tokens.get(sym)
    {
        out.push(id as i64);
    }

    let fin_sym: Option<&str> = match fin.as_str() {
        "a" => Some("ㄚ"),
        "o" => Some("ㄛ"),
        "e" => Some("ㄜ"),
        "ai" => Some("ㄞ"),
        "ei" => Some("ㄟ"),
        "ao" => Some("ㄠ"),
        "ou" => Some("ㄡ"),
        "an" => Some("ㄢ"),
        "en" => Some("ㄣ"),
        "ang" => Some("ㄤ"),
        "eng" => Some("ㄥ"),
        "ong" => Some("中"),
        "weng" => Some("瓮"),
        "er" => Some("ㄦ"),
        "i" => match init_raw {
            Some("zh" | "ch" | "sh" | "r") => Some("十"),
            Some("z" | "c" | "s") => Some("ㄭ"),
            _ => Some("ㄧ"),
        },
        "ia" => Some("压"),
        "ie" => Some("ㄝ"),
        "iao" => Some("要"),
        "iu" | "iou" => Some("又"),
        "ian" => Some("言"),
        "in" => Some("阴"),
        "iang" => Some("阳"),
        "ing" => Some("应"),
        "iong" => Some("用"),
        "u" => Some("ㄨ"),
        "ua" => Some("穵"),
        "uo" => match init_raw {
            Some("b" | "p" | "m" | "f") => Some("ㄛ"),
            _ => Some("我"),
        },
        "uai" => Some("外"),
        "ui" | "uei" => Some("为"),
        "uan" => Some("万"),
        "un" | "uen" => Some("文"),
        "uang" => Some("王"),
        "v" => Some("ㄩ"),
        "ve" | "ue" => Some("月"),
        "van" => Some("元"),
        "vn" => Some("云"),
        _ => None,
    };

    if let Some(fs) = fin_sym
        && let Some(&id) = tokens.get(fs)
    {
        out.push(id as i64);
    }
    if erhua
        && let Some(&r_id) = tokens.get("R")
    {
        out.push(r_id as i64);
    }
    let tone_s = tone.clamp(1, 5).to_string();
    if let Some(&t_id) = tokens.get(&tone_s) {
        out.push(t_id as i64);
    }
}

fn normalize_pinyin_tone(raw: &str) -> (String, u8) {
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

fn expand_numbers(s: &str, chinese: bool) -> String {
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

/// Trims leading/trailing near-silence (`−42 dB` relative to peak, keeping `50 ms` breath pad)
/// and normalizes active RMS to `−20 LUFS` (`rms ≈ 0.1`, peak ≤ `0.891`) so Kokoro matches
/// ZipVoice and CloudVoice volume seamlessly.
pub fn postprocess_pcm(samples: &[f32], rate: u32) -> Vec<f32> {
    if samples.is_empty() || rate == 0 {
        return Vec::new();
    }
    let mut peak = 0.0f32;
    for &x in samples {
        let a = x.abs();
        if a.is_finite() && a > peak {
            peak = a;
        }
    }
    if peak < 1e-6 {
        return Vec::new();
    }
    let thresh = peak * 0.008; // ~-42 dB
    let first = samples.iter().position(|x| x.abs() > thresh).unwrap_or(0);
    let last = samples.iter().rposition(|x| x.abs() > thresh).unwrap_or(samples.len() - 1);
    let pad = (rate as usize) / 20; // 50 ms
    let s = first.saturating_sub(pad);
    let e = (last + 1 + pad).min(samples.len());
    let slice = &samples[s..e];

    let mut sum_sq = 0.0f64;
    let mut active = 0usize;
    for &x in slice {
        if x.abs() > 1e-3 {
            sum_sq += (x as f64) * (x as f64);
            active += 1;
        }
    }
    let rms = if active >= 64 {
        (sum_sq / active as f64).sqrt() as f32
    } else {
        (slice.iter().map(|&x| (x as f64) * (x as f64)).sum::<f64>() / slice.len() as f64).sqrt() as f32
    };
    if rms < 1e-6 {
        return slice.to_vec();
    }
    let mut gain = 0.10f32 / rms;
    let max_after = peak * gain;
    if max_after > 0.891f32 {
        gain = 0.891f32 / peak;
    }
    slice.iter().map(|&x| (x * gain).clamp(-0.98, 0.98)).collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn curated_speakers_cover_zh_and_en_with_valid_sids() {
        let list = builtin_speakers();
        assert!(list.iter().any(|s| s.lang == "zh" && s.narrator && s.gender == CharacterGender::F));
        assert!(list.iter().any(|s| s.lang == "zh" && s.narrator && s.gender == CharacterGender::M));
        assert!(list.iter().any(|s| s.lang == "en" && s.narrator));
        assert!(list.iter().all(|s| s.sid < 103 && s.id.starts_with("kokoro.")));
    }

    #[test]
    fn numbers_and_pinyin_map_cleanly() {
        assert_eq!(expand_numbers("第 12 章，1986 年 3.5 倍", true), "第 十二 章，一九八六 年 三点五 倍");
        assert_eq!(expand_numbers("1986年", true), "一九八六年");
        assert_eq!(expand_numbers("Chapter 12 in 1859", false), "Chapter twelve in one thousand eight hundred fifty nine");

        let mut fake_tokens = HashMap::new();
        for (i, sym) in [
            ";", ":", ",", ".", "!", "?", " ", "ㄓ", "ㄔ", "ㄕ", "ㄖ", "ㄗ", "ㄘ", "ㄙ",
            "ㄅ", "ㄆ", "ㄇ", "ㄈ", "ㄉ", "ㄊ", "ㄋ", "ㄌ", "ㄍ", "ㄎ", "ㄏ", "ㄐ", "ㄑ", "ㄒ",
            "ㄚ", "ㄛ", "ㄜ", "ㄝ", "ㄞ", "ㄟ", "ㄠ", "ㄡ", "ㄢ", "ㄣ", "ㄤ", "ㄥ", "ㄦ",
            "ㄧ", "ㄨ", "ㄩ", "ㄭ", "十", "压", "言", "阳", "要", "阴", "应", "用", "又", "中",
            "穵", "外", "万", "王", "为", "文", "瓮", "我", "月", "元", "云", "R",
            "1", "2", "3", "4", "5",
        ]
        .into_iter()
        .enumerate()
        {
            fake_tokens.insert(sym.to_string(), (i + 1) as u16);
        }
        for syl in ["zhōng", "guó", "shuang1", "dianr3", "lü4", "yuan2", "zhi1", "si1", "ying3"] {
            let mut ids = Vec::new();
            append_pinyin_kokoro(syl, &fake_tokens, &mut ids);
            assert!(ids.len() >= 2, "failed mapping syllable {syl}");
        }
    }

    #[test]
    fn clause_splitting_respects_punctuation_and_limits() {
        let long_zh = "时候既然是深冬；渐近故乡时，天气又阴晦了，冷风吹进船舱中，呜呜的响，从篷隙向外一望，苍黄的天底下，远近横着几个萧索的荒村，没有一些活气。我的心禁不住悲凉起来了。";
        let parts = split_clauses(long_zh, "zh");
        assert!(parts.len() >= 2);
        assert!(parts.iter().all(|p| !p.is_empty()));
    }
}
