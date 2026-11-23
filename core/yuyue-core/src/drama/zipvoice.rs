//! Pure-computation frontend & vocoder DSP for the on-device zero-shot Live Voice model
//! (`ZipVoice-Distill-Int8` + `Vocos 24 kHz`).
//!
//! Keeps `yuyue-core` free of model execution and networking while sharing identical
//! compact G2P tokenization (< 8 MB flat arena vs. 300 MB hash maps), `{词|读音}` pinyin override
//! support, 24 kHz 100-band Mel extraction, Euler timestep schedule, 1024-pt Vocos ISTFT, −20 LUFS
//! loudness normalization, and lightweight quality gating across macOS, iOS, and Android.

use std::collections::HashMap;
use std::f32::consts::PI;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use pinyin::ToPinyin;

pub const LIVE_RATE: u32 = 24_000;
pub const N_FFT: usize = 1024;
pub const HOP: usize = 256;
pub const BINS: usize = N_FFT / 2 + 1; // 513
pub const NUM_MELS: usize = 100;
pub const FEAT_SCALE: f32 = 0.1;
pub const TARGET_RMS: f32 = 0.1;

/// Cached reference prompt features & token IDs for one `(card_id, version, style)`.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ZipVoicePrompt {
    pub prompt_tokens: Vec<i64>,
    /// Flattened `[prompt_frames, 100]` Mel features scaled by `FEAT_SCALE = 0.1`.
    pub prompt_features: Vec<f32>,
    pub prompt_frames: u32,
}

#[derive(Clone, Copy)]
struct LexEntry {
    key_off: u32,
    key_len: u16,
    tok_off: u32,
    tok_len: u8,
}

#[derive(Default)]
struct LexiconState {
    dir: PathBuf,
    tokens: HashMap<String, u16>,
    keys_arena: String,
    toks_arena: Vec<u16>,
    entries: Vec<LexEntry>,
    max_cjk_phrase: usize,
}

impl LexiconState {
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

fn lex_state() -> &'static Mutex<LexiconState> {
    static L: OnceLock<Mutex<LexiconState>> = OnceLock::new();
    L.get_or_init(|| Mutex::new(LexiconState::default()))
}

/// Loads `tokens.txt`, `lexicon-zh.txt` (or `lexicon.txt`), and `lexicon-en.txt` (or
/// `lexicon-en-us.txt`) from `dir` into a single flat sorted arena. Returns loaded entry count.
pub fn set_model_dir(dir: &Path) -> usize {
    if let Ok(st) = lex_state().lock()
        && st.dir == dir
        && !st.tokens.is_empty()
    {
        return st.entries.len();
    }
    let Ok(tok_txt) = std::fs::read_to_string(dir.join("tokens.txt")) else {
        return 0;
    };
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
        return 0;
    }

    let mut raw_pairs: Vec<(String, Vec<u16>)> = Vec::with_capacity(240_000);
    let mut max_cjk_phrase = 1usize;

    // 1. Chinese lexicon (`word ph1 ph2 ...`)
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

    // 2. English lexicon (`word || ph1 ph2 ...` or `word ph1 ph2 ...`)
    for name in ["lexicon-en.txt", "lexicon-en-us.txt"] {
        let p = dir.join(name);
        if let Ok(txt) = std::fs::read_to_string(&p) {
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

    let count = entries.len();
    if let Ok(mut st) = lex_state().lock() {
        *st = LexiconState { dir: dir.to_path_buf(), tokens, keys_arena, toks_arena, entries, max_cjk_phrase };
    }
    count
}

fn is_cjk(c: char) -> bool {
    ('\u{4e00}'..='\u{9fff}').contains(&c) || ('\u{3400}'..='\u{4dbf}').contains(&c)
}

/// Splits a sentence into clauses short enough to keep ZipVoice's total attention window ≤ 9 s
/// (`≤ 32` Chinese characters or `≤ 22` English words per chunk).
pub fn split_clauses(text: &str, lang: &str) -> Vec<String> {
    let clean = strip_reading_tags_keep_braces(text.trim());
    if clean.is_empty() {
        return Vec::new();
    }
    let chinese = !lang.to_ascii_lowercase().starts_with("en") || clean.chars().any(is_cjk);
    let max_units = if chinese { 32usize } else { 22usize };
    if count_units(&clean, chinese) <= max_units {
        return vec![clean];
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
        if out.last().is_some() && count_units(&buf, chinese) < 3 {
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

fn strip_reading_tags_keep_braces(s: &str) -> String {
    s.to_string()
}

fn count_units(s: &str, chinese: bool) -> usize {
    if chinese {
        s.chars().filter(|&c| is_cjk(c) || c.is_ascii_alphanumeric()).count()
    } else {
        s.split_whitespace().filter(|w| w.chars().any(char::is_alphanumeric)).count()
    }
}

/// Converts `text` (supporting `{词|jiǎ zhēn}` reading annotations, Traditional Chinese, numbers,
/// and mixed Chinese/English) into ZipVoice token IDs.
pub fn tokenize(text: &str, lang: &str) -> Vec<i64> {
    let Ok(st) = lex_state().lock() else { return Vec::new() };
    if st.tokens.is_empty() {
        return Vec::new();
    }
    let chinese_ctx = !lang.to_ascii_lowercase().starts_with("en") || text.chars().any(is_cjk);
    let mut out: Vec<i64> = Vec::new();

    for part in split_spoken_parts(text) {
        if let Some(reading) = part.1 {
            for syl in reading.split_whitespace() {
                append_pinyin_syllable(&syl, &st.tokens, &mut out);
            }
        } else {
            tokenize_plain(&part.0, chinese_ctx, &st, &mut out);
        }
    }
    // Ensure a trailing punctuation token so flow-matching knows the utterance ends cleanly.
    let punct_ids: [i64; 6] = [
        st.tokens.get(".").copied().unwrap_or(10) as i64,
        st.tokens.get(",").copied().unwrap_or(8) as i64,
        st.tokens.get("!").copied().unwrap_or(4) as i64,
        st.tokens.get("?").copied().unwrap_or(13) as i64,
        st.tokens.get(";").copied().unwrap_or(12) as i64,
        st.tokens.get(":").copied().unwrap_or(11) as i64,
    ];
    if let Some(&last) = out.last()
        && !punct_ids.contains(&last)
    {
        out.push(punct_ids[0]);
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
        out.push((word.to_string(), Some(reading.to_string())));
        rest = &rest[b + 1..];
    }
    plain.push_str(rest);
    if !plain.is_empty() {
        out.push((plain, None));
    }
    out
}

fn tokenize_plain(raw: &str, chinese_ctx: bool, st: &LexiconState, out: &mut Vec<i64>) {
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
        // Curly quotes / brackets: drop silently or turn internal dash into comma pause.
        if matches!(c, '“' | '”' | '‘' | '’' | '"' | '「' | '」' | '『' | '』' | '《' | '》' | '（' | '）' | '(' | ')' | '[' | ']') {
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
            // Keep inter-word space before Latin words; omit spaces adjacent to CJK.
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
                    append_pinyin_syllable(py.with_tone_num_end(), &st.tokens, out);
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
            let word: String = chars[start..i].iter().collect::<String>().trim_matches('\'').to_ascii_lowercase();
            if word.is_empty() {
                continue;
            }
            // Space between consecutive English words if not already added.
            if let Some(&sp) = st.tokens.get(" ")
                && let Some(&last) = out.last()
                && last != sp as i64
                && !matches_punct_id(last, &st.tokens)
                && start > 0
                && !chars[..start].iter().rev().find(|x| !x.is_whitespace()).is_none_or(|&x| is_cjk(x))
            {
                // Already handled by whitespace branch above when separated by space.
            }
            append_english_word(&word, st, out);
            continue;
        }
        i += 1;
    }
}

fn matches_punct_id(id: i64, tokens: &HashMap<String, u16>) -> bool {
    [",", ".", "!", "?", ";", ":"].iter().any(|p| tokens.get(*p).copied().map(i64::from) == Some(id))
}

fn map_punct(c: char) -> Option<&'static str> {
    match c {
        '，' | '、' | '：' | ',' | ':' | '—' | '…' => Some(","),
        '；' | ';' => Some(";"),
        '。' | '.' => Some("."),
        '？' | '?' => Some("?"),
        '！' | '!' => Some("!"),
        _ => None,
    }
}

fn append_english_word(word: &str, st: &LexiconState, out: &mut Vec<i64>) {
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
    // Spell/letter fallback using single-letter entries in lexicon (`a`, `b`, ...).
    for ch in word.chars() {
        let s = ch.to_string();
        if let Some(ids) = st.lookup(&s) {
            out.extend(ids.iter().map(|&x| x as i64));
        } else if let Some(&id) = st.tokens.get(&s) {
            out.push(id as i64);
        }
    }
}

/// Converts a pinyin syllable (either tone-marked `jiǎ` or numbered `jia3` / `shuang1`) into
/// ZipVoice initial (`j0`, `sh0`, `y0`, `w0`) + toned final (`ia3`, `uang1`) token IDs.
fn append_pinyin_syllable(raw: &str, tokens: &HashMap<String, u16>, out: &mut Vec<i64>) {
    let (letters, tone) = normalize_pinyin_tone(raw);
    if letters.is_empty() {
        return;
    }
    const INITIALS: &[&str] = &[
        "zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x", "r", "z", "c", "s", "y", "w",
    ];
    let (init, mut rest) = INITIALS
        .iter()
        .find_map(|&ini| letters.strip_prefix(ini).map(|r| (Some(ini), r.to_string())))
        .unwrap_or((None, letters));

    if let Some(ini) = init {
        if matches!(ini, "j" | "q" | "x" | "y") && rest.starts_with('v') {
            rest.replace_range(0..1, "u");
        }
        let ini_tok = format!("{ini}0");
        if let Some(&id) = tokens.get(&ini_tok) {
            out.push(id as i64);
        }
    }
    if !rest.is_empty() {
        let fin_tok = format!("{rest}{tone}");
        if let Some(&id) = tokens.get(&fin_tok) {
            out.push(id as i64);
        } else {
            let fin1 = format!("{rest}1");
            if let Some(&id) = tokens.get(&fin1) {
                out.push(id as i64);
            }
        }
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
    const ZH_DIGITS: [char; 10] = ['零', '一', '二', '三', '四', '五', '六', '七', '八', '九'];
    const EN_DIGITS: [&str; 10] = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine"];
    let mut out = String::with_capacity(s.len());
    let mut chars = s.chars().peekable();
    while let Some(c) = chars.next() {
        if c.is_ascii_digit() {
            let mut run = String::from(c);
            while let Some(&nx) = chars.peek() {
                if nx.is_ascii_digit() {
                    run.push(nx);
                    chars.next();
                } else {
                    break;
                }
            }
            if chinese {
                if let Ok(n) = run.parse::<u32>()
                    && n < 100
                {
                    out.push_str(&zh_small_int(n));
                } else {
                    for d in run.bytes() {
                        out.push(ZH_DIGITS[(d - b'0') as usize]);
                    }
                }
            } else {
                if !out.ends_with(' ') && !out.is_empty() {
                    out.push(' ');
                }
                if let Ok(n) = run.parse::<u32>()
                    && n < 100
                {
                    out.push_str(&en_small_int(n));
                } else {
                    for (k, d) in run.bytes().enumerate() {
                        if k > 0 {
                            out.push(' ');
                        }
                        out.push_str(EN_DIGITS[(d - b'0') as usize]);
                    }
                }
                if chars.peek().is_some_and(|x| x.is_ascii_alphabetic()) {
                    out.push(' ');
                }
            }
        } else {
            out.push(c);
        }
    }
    out
}

fn zh_small_int(n: u32) -> String {
    const D: [&str; 10] = ["零", "一", "二", "三", "四", "五", "六", "七", "八", "九"];
    match n {
        0..=9 => D[n as usize].into(),
        10 => "十".into(),
        11..=19 => format!("十{}", D[(n % 10) as usize]),
        _ if n.is_multiple_of(10) => format!("{}十", D[(n / 10) as usize]),
        _ => format!("{}十{}", D[(n / 10) as usize], D[(n % 10) as usize]),
    }
}

fn en_small_int(n: u32) -> String {
    const ONES: [&str; 20] = [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];
    const TENS: [&str; 10] = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    if n < 20 {
        ONES[n as usize].into()
    } else if n.is_multiple_of(10) {
        TENS[(n / 10) as usize].into()
    } else {
        format!("{} {}", TENS[(n / 10) as usize], ONES[(n % 10) as usize])
    }
}

// ── 24 kHz Mel Filterbank, FFT / IRFFT & Vocos ISTFT ─────────────────────────

fn mel_scale(f: f32) -> f32 {
    1127.0 * (1.0 + f / 700.0).ln()
}

fn inv_mel_scale(m: f32) -> f32 {
    700.0 * ((m / 1127.0).exp() - 1.0)
}

fn hann_1024() -> &'static [f32; N_FFT] {
    static W: OnceLock<[f32; N_FFT]> = OnceLock::new();
    W.get_or_init(|| {
        let mut w = [0.0f32; N_FFT];
        for (i, v) in w.iter_mut().enumerate() {
            *v = 0.5 - 0.5 * (2.0 * PI * i as f32 / N_FFT as f32).cos();
        }
        w
    })
}

fn mel_filters_24k() -> &'static Vec<(usize, Vec<f32>)> {
    static F: OnceLock<Vec<(usize, Vec<f32>)>> = OnceLock::new();
    F.get_or_init(|| {
        let lo = mel_scale(0.0);
        let hi = mel_scale(LIVE_RATE as f32 / 2.0);
        let pts: Vec<f32> = (0..NUM_MELS + 2)
            .map(|i| inv_mel_scale(lo + (hi - lo) * i as f32 / (NUM_MELS + 1) as f32))
            .collect();
        (0..NUM_MELS)
            .map(|m| {
                let (l, c, r) = (pts[m], pts[m + 1], pts[m + 2]);
                let mut first = usize::MAX;
                let mut weights = Vec::new();
                for k in 0..BINS {
                    let freq = k as f32 * LIVE_RATE as f32 / N_FFT as f32;
                    let up = (freq - l) / (c - l).max(1e-9);
                    let dn = (r - freq) / (r - c).max(1e-9);
                    let v = up.min(dn).max(0.0);
                    if v > 0.0 {
                        if first == usize::MAX {
                            first = k;
                        }
                        weights.push(v);
                    } else if first != usize::MAX {
                        break;
                    }
                }
                (if first == usize::MAX { 0 } else { first }, weights)
            })
            .collect()
    })
}

fn fft_in_place(re: &mut [f32; N_FFT], im: &mut [f32; N_FFT]) {
    let n = N_FFT;
    let mut j = 0usize;
    for i in 1..n {
        let mut bit = n >> 1;
        while j & bit != 0 {
            j ^= bit;
            bit >>= 1;
        }
        j |= bit;
        if i < j {
            re.swap(i, j);
            im.swap(i, j);
        }
    }
    let mut len = 2usize;
    while len <= n {
        let ang = -2.0 * PI / len as f32;
        for start in (0..n).step_by(len) {
            for k in 0..len / 2 {
                let (s, c) = (ang * k as f32).sin_cos();
                let (a, b) = (start + k, start + k + len / 2);
                let (xr, xi) = (re[b] * c - im[b] * s, re[b] * s + im[b] * c);
                re[b] = re[a] - xr;
                im[b] = im[a] - xi;
                re[a] += xr;
                im[a] += xi;
            }
        }
        len <<= 1;
    }
}

/// Extracts `ZipVoicePrompt` (tokens + 24 kHz 100-band Mel features scaled by `0.1`) for a
/// reference clip.
pub fn build_prompt(samples: &[f32], rate: u32, text: &str, lang: &str) -> Option<ZipVoicePrompt> {
    if samples.is_empty() || rate == 0 {
        return None;
    }
    let prompt_tokens = tokenize(text, lang);
    if prompt_tokens.is_empty() {
        return None;
    }
    let mut wav = super::card::resample_mono(samples, rate, LIVE_RATE);
    if wav.len() < N_FFT {
        return None;
    }
    let rms = (wav.iter().map(|&x| (x as f64) * (x as f64)).sum::<f64>() / wav.len() as f64).sqrt() as f32;
    if rms > 0.0 && rms < TARGET_RMS {
        let scale = TARGET_RMS / rms;
        for s in &mut wav {
            *s *= scale;
        }
    }

    let pad = N_FFT / 2;
    let mut padded = Vec::with_capacity(wav.len() + 2 * pad);
    for k in (1..=pad).rev() {
        padded.push(wav[k.min(wav.len() - 1)]);
    }
    padded.extend_from_slice(&wav);
    for k in 1..=pad {
        padded.push(wav[wav.len().saturating_sub(1 + k)]);
    }

    let win = hann_1024();
    let filters = mel_filters_24k();
    let num_frames = 1 + (padded.len() - N_FFT) / HOP;
    let mut features = Vec::with_capacity(num_frames * NUM_MELS);
    let mut re = [0.0f32; N_FFT];
    let mut im = [0.0f32; N_FFT];
    let mut mag = [0.0f32; BINS];

    for f in 0..num_frames {
        let off = f * HOP;
        for i in 0..N_FFT {
            re[i] = padded[off + i] * win[i];
            im[i] = 0.0;
        }
        fft_in_place(&mut re, &mut im);
        for k in 0..BINS {
            mag[k] = (re[k] * re[k] + im[k] * im[k]).sqrt();
        }
        for (start, w) in filters {
            let mut sum = 0.0f32;
            for (j, &wk) in w.iter().enumerate() {
                sum += mag[start + j] * wk;
            }
            features.push((sum + 1e-10).ln() * FEAT_SCALE);
        }
    }

    Some(ZipVoicePrompt {
        prompt_tokens,
        prompt_features: features,
        prompt_frames: num_frames as u32,
    })
}

/// Shifted Euler timesteps (`num_steps + 1` points from `0.0` to `1.0`) with `t_shift = 0.5`.
pub fn shifted_timesteps(num_steps: u32, t_shift: f32) -> Vec<f32> {
    let n = num_steps.max(1);
    (0..=n)
        .map(|i| {
            let t = i as f32 / n as f32;
            t_shift * t / (1.0 + (t_shift - 1.0) * t)
        })
        .collect()
}

/// Deterministic $\mathcal{N}(0, 1)$ vector of length `len` from `seed` (Box-Muller over SplitMix64).
pub fn standard_normal(len: usize, seed: u64) -> Vec<f32> {
    let mut state = seed ^ 0x9E37_79B9_7F4A_7C15;
    let mut next_u64 = || {
        state = state.wrapping_add(0x9E37_79B9_7F4A_7C15);
        let mut z = state;
        z = (z ^ (z >> 30)).wrapping_mul(0xBF58_476D_1CE4_E5B9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94D0_49BB_1331_11EB);
        z ^ (z >> 31)
    };
    let mut next_u01 = || ((next_u64() >> 11) as f64 + 0.5) * (1.0 / ((1u64 << 53) as f64));

    let mut out = Vec::with_capacity(len);
    while out.len() < len {
        let u1 = next_u01().max(1e-12);
        let u2 = next_u01();
        let r = (-2.0 * u1.ln()).sqrt();
        let theta = 2.0 * std::f64::consts::PI * u2;
        out.push((r * theta.cos()) as f32);
        if out.len() < len {
            out.push((r * theta.sin()) as f32);
        }
    }
    out
}

/// Reconstructs 24 kHz mono PCM from Vocos outputs `mag`, `x_cos`, `y_sin` (each shaped
/// `[1, 513, frames]` row-major) via 1024-pt inverse RFFT + Hann overlap-add, normalized to −20 LUFS.
pub fn vocos_istft(mag: &[f32], x_cos: &[f32], y_sin: &[f32], frames: usize) -> Vec<f32> {
    if frames == 0 || mag.len() < BINS * frames || x_cos.len() < BINS * frames || y_sin.len() < BINS * frames {
        return Vec::new();
    }
    let win = hann_1024();
    let out_len = (frames - 1) * HOP + N_FFT;
    let mut out = vec![0.0f32; out_len];
    let mut env = vec![0.0f32; out_len];
    let mut re = [0.0f32; N_FFT];
    let mut im = [0.0f32; N_FFT];
    let inv_n = 1.0 / N_FFT as f32;

    for t in 0..frames {
        for k in 0..BINS {
            let idx = k * frames + t;
            let m = mag[idx];
            re[k] = m * x_cos[idx];
            im[k] = m * y_sin[idx];
        }
        for k in 1..(N_FFT / 2) {
            re[N_FFT - k] = re[k];
            im[N_FFT - k] = -im[k];
        }
        // IFFT(X) = conj(FFT(conj(X))) / N
        for v in &mut im {
            *v = -*v;
        }
        fft_in_place(&mut re, &mut im);
        let base = t * HOP;
        for i in 0..N_FFT {
            let sample = re[i] * inv_n;
            let w = win[i];
            out[base + i] += sample * w;
            env[base + i] += w * w;
        }
    }

    let pad = N_FFT / 2;
    if out_len <= 2 * pad {
        return Vec::new();
    }
    let mut trimmed = Vec::with_capacity(out_len - 2 * pad);
    for i in pad..(out_len - pad) {
        trimmed.push(out[i] / env[i].max(1e-8));
    }
    super::card::normalize_lufs_20(&mut trimmed);
    trimmed
}

/// Lightweight ASR-free check for Live-layer synthesized clips:
/// 1. Duration within ±50% of `units / expected_rate` (checked when `units >= 5`).
/// 2. Leading or trailing silence ≤ 0.6 s, and internal continuous silence ≤ 1.5 s.
/// 3. Clipped samples (`|x| >= 0.999`) ≤ 0.1%.
pub fn light_check(samples: &[f32], rate: u32, text: &str, lang: &str, expected_rate: f32) -> bool {
    if samples.is_empty() || rate == 0 {
        return false;
    }
    let secs = samples.len() as f32 / rate as f32;
    let clipped = samples.iter().filter(|&&x| x.abs() >= 0.999).count() as f32 / samples.len() as f32;
    if clipped > 0.001 {
        return false;
    }

    let chinese = !lang.to_ascii_lowercase().starts_with("en") || text.chars().any(is_cjk);
    let units = count_units(text, chinese);
    let rate_ref = if expected_rate > 0.5 {
        expected_rate
    } else if chinese {
        4.3
    } else {
        2.8
    };
    if units >= 5 {
        let expected = units as f32 / rate_ref;
        if secs < expected * 0.50 || secs > expected * 1.50 + 0.6 {
            return false;
        }
    } else if secs < 0.20 || secs > 4.5 {
        return false;
    }

    let hop = (rate as usize / 50).max(1); // 20 ms frames
    let thresh = 10f32.powf(-42.0 / 20.0);
    let voiced: Vec<bool> = samples
        .chunks(hop)
        .map(|w| (w.iter().map(|&x| x * x).sum::<f32>() / w.len() as f32).sqrt() >= thresh)
        .collect();
    let Some(first) = voiced.iter().position(|&v| v) else {
        return false;
    };
    let last = voiced.iter().rposition(|&v| v).unwrap_or(first);
    let frame_sec = hop as f32 / rate as f32;
    let head_sil = first as f32 * frame_sec;
    let tail_sil = (voiced.len().saturating_sub(1 + last)) as f32 * frame_sec;
    if head_sil > 0.60 || tail_sil > 0.60 {
        return false;
    }
    let mut max_gap = 0usize;
    let mut cur_gap = 0usize;
    for &v in &voiced[first..=last] {
        if v {
            cur_gap = 0;
        } else {
            cur_gap += 1;
            max_gap = max_gap.max(cur_gap);
        }
    }
    if max_gap as f32 * frame_sec > 1.50 {
        return false;
    }
    true
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn stft_and_istft_round_trip_a_24khz_sine_wave() {
        let secs = 1.0f32;
        let n = (LIVE_RATE as f32 * secs) as usize;
        let orig: Vec<f32> = (0..n).map(|i| 0.1 * (2.0 * PI * 440.0 * i as f32 / LIVE_RATE as f32).sin()).collect();
        let pad = N_FFT / 2;
        let mut padded = vec![0.0f32; n + 2 * pad];
        padded[pad..pad + n].copy_from_slice(&orig);
        let win = hann_1024();
        let frames = 1 + (padded.len() - N_FFT) / HOP;
        let mut mag = vec![0.0f32; BINS * frames];
        let mut xc = vec![0.0f32; BINS * frames];
        let mut ys = vec![0.0f32; BINS * frames];
        let mut re = [0.0f32; N_FFT];
        let mut im = [0.0f32; N_FFT];
        for t in 0..frames {
            for i in 0..N_FFT {
                re[i] = padded[t * HOP + i] * win[i];
                im[i] = 0.0;
            }
            fft_in_place(&mut re, &mut im);
            for k in 0..BINS {
                let m = (re[k] * re[k] + im[k] * im[k]).sqrt();
                mag[k * frames + t] = m;
                if m > 1e-12 {
                    xc[k * frames + t] = re[k] / m;
                    ys[k * frames + t] = im[k] / m;
                } else {
                    xc[k * frames + t] = 1.0;
                }
            }
        }
        let rec = vocos_istft(&mag, &xc, &ys, frames);
        assert!((rec.len() as isize - n as isize).abs() <= HOP as isize);
        let crossings = rec.windows(2).filter(|w| (w[0] < 0.0) != (w[1] < 0.0)).count();
        assert!((870..=890).contains(&crossings), "got {crossings}");
    }

    #[test]
    fn clause_splitter_and_light_check_behave_per_spec() {
        let long_zh = "我与父亲不相见已二年余了，我最不能忘记的是他的背影，那年冬天祖母死了，父亲的差使也交卸了，正是祸不单行的日子。";
        let chunks = split_clauses(long_zh, "zh");
        assert!(chunks.len() >= 2);
        for c in &chunks {
            assert!(count_units(c, true) <= 32, "too long: {c}");
        }

        // Good 2.0 s clip for 9 Chinese chars (~4.5 chars/s).
        let good: Vec<f32> = (0..48_000).map(|i| 0.1 * (2.0 * PI * 220.0 * i as f32 / 24_000.0).sin()).collect();
        assert!(light_check(&good, 24_000, "今天早上下了小雨。", "zh", 4.5));

        // 1.8 s silent gap in the middle -> rejected.
        let mut gappy = good.clone();
        for s in &mut gappy[2_400..46_000] {
            *s = 0.0;
        }
        assert!(!light_check(&gappy, 24_000, "今天早上下了小雨。", "zh", 4.5));
    }

    #[test]
    fn real_lexicon_matches_sherpa_tokens_and_supports_reading_overrides() {
        let root = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../models/live-zipvoice-1");
        if !root.join("tokens.txt").is_file() {
            return;
        }
        let count = set_model_dir(&root);
        assert!(count > 200_000, "expected >200k zh+en lexicon entries, got {count}");
        let ids = tokenize("你好，很高兴认识你。", "zh");
        assert_eq!(ids, vec![276, 220, 218, 181, 8, 218, 200, 213, 179, 352, 257, 299, 201, 301, 220, 276, 221, 10]);

        let override_ids = tokenize("我是{贾珍|jiǎ zhēn}。", "zh");
        assert!(override_ids.len() >= 9);

        let en_ids = tokenize("It is a truth universally acknowledged.", "en");
        assert!(en_ids.len() >= 20);
    }
}
