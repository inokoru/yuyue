//! Pure-computation frontend, compact lexicon arena, speaker catalog, and 24 kHz loudness DSP
//! for the cross-platform lightweight offline neural TTS model (`Kokoro-82M-v1.1-zh Int8`, Apache-2.0).
//!
//! Shares identical tokenization (`tokens.txt`, `lexicon-zh.txt`, `lexicon-us-en.txt`), `{词|读音}`
//! polyphone overrides, Traditional-to-Simplified conversion, Chinese/English number expansion,
//! Misaki-zh Zhuyin syllable fallback, clause splitting (`≤ 400` inner tokens for Kokoro's 510
//! style-vector length buckets), and −20 LUFS loudness normalization across macOS, iOS, Android,
//! and Windows.

use std::collections::HashMap;
use std::path::Path;
use std::sync::{Mutex, OnceLock};

use pinyin::ToPinyin;
use serde::{Deserialize, Serialize};

use super::CharacterGender;
use super::phoneme::{
    self, FlatLexicon, append_english_word, expand_numbers, is_cjk, normalize_pinyin_tone,
    split_spoken_parts,
};

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

fn kokoro_state() -> &'static Mutex<FlatLexicon> {
    static K: OnceLock<Mutex<FlatLexicon>> = OnceLock::new();
    K.get_or_init(|| Mutex::new(FlatLexicon::default()))
}

/// Curated 16 standout narrators from `Kokoro-82M-v1.1-zh` (12 Mandarin + 4 English),
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
        return st.len();
    }
    let Some(loaded) = FlatLexicon::load(
        dir,
        &["lexicon-zh.txt", "lexicon.txt"],
        &["lexicon-us-en.txt", "lexicon-en.txt", "lexicon-gb-en.txt"],
        false,
    ) else {
        return 0;
    };
    let count = loaded.len();
    if let Ok(mut st) = kokoro_state().lock() {
        *st = loaded;
    }
    count
}

/// Splits long sentences into natural breath clauses (`≤ 68` Chinese chars or `≤ 52` English words)
/// so each Kokoro pass stays comfortably inside the 510-token style embedding window and maintains
/// crisp prosody without end-of-utterance rush.
pub fn split_clauses(text: &str, lang: &str) -> Vec<String> {
    phoneme::split_clauses(text, lang, 68, 52, 0)
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

fn tokenize_plain(raw: &str, chinese_ctx: bool, st: &FlatLexicon, out: &mut Vec<i64>) {
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
