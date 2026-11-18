//! 听写 where the system has none that takes a file (Android): SenseVoice-Small (FunAudioLLM,
//! Chinese, English, Japanese, Korean, Cantonese), as sherpa-onnx exports it. The app runs the
//! model; the core makes its input (FunASR's front end: filter banks, frames stacked seven at a
//! time every six, normalised by the model's own statistics) and reads its output (greedy CTC
//! over the model's tokens, each with the time it was heard).
//!
//! 回听 asks it what a clip said; 转成文字 asks it for a whole recording, punctuated.

use std::collections::HashMap;
use std::sync::Arc;

use crate::audiobook::anchor::HeardWord;
use crate::drama::speaker::{BANDS, fbank, to_16k};
use crate::error::{CoreError, Result};

/// One model output frame: 10 ms frames, six to a step.
const STEP_SECONDS: f64 = 0.06;
/// Outputs before the speech: language, emotion, event, text normalisation.
const QUERIES: usize = 4;

/// The model's input for a stretch of sound: `frames` rows of `dim`.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct AsrInput {
    pub frames: u32,
    pub dim: u32,
    pub data: Vec<f32>,
}

#[derive(uniffi::Object)]
pub struct SenseVoice {
    lfr_m: usize,
    lfr_n: usize,
    neg_mean: Vec<f32>,
    inv_stddev: Vec<f32>,
    tokens: Vec<String>,
    meta: HashMap<String, String>,
}

fn floats(s: &str) -> Vec<f32> {
    s.split(',').filter_map(|v| v.trim().parse().ok()).collect()
}

fn is_punct(s: &str) -> bool {
    !s.is_empty() && s.chars().all(|c| !c.is_alphanumeric())
}

fn is_cjk(c: char) -> bool {
    matches!(c, '\u{3040}'..='\u{30ff}' | '\u{3400}'..='\u{4dbf}' | '\u{4e00}'..='\u{9fff}' | '\u{ac00}'..='\u{d7af}' | '\u{f900}'..='\u{faff}')
}

#[uniffi::export]
impl SenseVoice {
    /// From the model's metadata (as ONNX Runtime lists it) and its `tokens.txt`.
    #[uniffi::constructor]
    pub fn new(meta: HashMap<String, String>, tokens: String) -> Result<Arc<SenseVoice>> {
        let num = |k: &str| meta.get(k).and_then(|v| v.trim().parse::<usize>().ok());
        let (Some(lfr_m), Some(lfr_n)) = (num("lfr_window_size"), num("lfr_window_shift")) else {
            return Err(CoreError::Invalid { detail: "not a SenseVoice model".into() });
        };
        let neg_mean = meta.get("neg_mean").map(|s| floats(s)).unwrap_or_default();
        let inv_stddev = meta.get("inv_stddev").map(|s| floats(s)).unwrap_or_default();
        if neg_mean.len() != BANDS * lfr_m || inv_stddev.len() != BANDS * lfr_m {
            return Err(CoreError::Invalid { detail: "SenseVoice statistics".into() });
        }
        let mut list: Vec<(usize, String)> = tokens
            .lines()
            .filter_map(|l| {
                let (t, id) = l.rsplit_once(' ')?;
                Some((id.trim().parse().ok()?, t.to_string()))
            })
            .collect();
        list.sort_by_key(|x| x.0);
        let mut toks = vec![String::new(); list.last().map_or(0, |x| x.0 + 1)];
        for (i, t) in list {
            toks[i] = t;
        }
        Ok(Arc::new(SenseVoice { lfr_m, lfr_n, neg_mean, inv_stddev, tokens: toks, meta }))
    }

    /// The model's `language` input for a book's language (`zh`, `en`, …); `auto` otherwise.
    pub fn language_id(&self, language: String) -> i32 {
        let l = language.to_lowercase();
        let key = ["zh", "en", "ja", "ko", "yue"].into_iter().find(|k| l.starts_with(k)).unwrap_or("auto");
        self.meta.get(&format!("lang_{key}")).or_else(|| self.meta.get("lang_auto")).and_then(|v| v.parse().ok()).unwrap_or(0)
    }

    /// The model's `text_norm` input: with punctuation and numbers written out, or plain.
    pub fn text_norm_id(&self, punctuated: bool) -> i32 {
        self.meta.get(if punctuated { "with_itn" } else { "without_itn" }).and_then(|v| v.parse().ok()).unwrap_or(14)
    }

    /// The model's input for `samples` at `rate` (mono, −1…1).
    pub fn input(&self, samples: Vec<f32>, rate: u32) -> AsrInput {
        let x = to_16k(&samples, rate);
        let (n, fb) = fbank(&x, true);
        let dim = BANDS * self.lfr_m;
        if n == 0 {
            return AsrInput { frames: 0, dim: dim as u32, data: Vec::new() };
        }
        let row = |i: usize| &fb[i * BANDS..(i + 1) * BANDS];
        // The first frame repeated before, so each stack is centred on its frame.
        let left = (self.lfr_m - 1) / 2;
        let padded = n + left;
        let at = |k: usize| row(k.saturating_sub(left).min(n - 1));
        let out_frames = n.div_ceil(self.lfr_n);
        let mut data = Vec::with_capacity(out_frames * dim);
        for i in 0..out_frames {
            for j in 0..self.lfr_m {
                // Past the end: the last frame again.
                let k = (i * self.lfr_n + j).min(padded - 1);
                data.extend_from_slice(at(k));
            }
        }
        for (i, v) in data.iter_mut().enumerate() {
            let d = i % dim;
            *v = (*v + self.neg_mean[d]) * self.inv_stddev[d];
        }
        AsrInput { frames: out_frames as u32, dim: dim as u32, data }
    }

    /// What was said, from the likeliest token of each output frame (the app takes the
    /// argmax; the first four outputs are the model's tags), `offset` seconds added to each
    /// time. A word each Chinese character, a word each English word; punctuation goes with the
    /// word before it.
    pub fn words(&self, best: Vec<i32>, offset: f64) -> Vec<HeardWord> {
        let mut out: Vec<HeardWord> = Vec::new();
        let mut prev = -1i32;
        for (t, &id) in best.iter().enumerate().skip(QUERIES) {
            if id == prev {
                continue;
            }
            prev = id;
            if id <= 0 {
                continue;
            }
            let Some(tok) = self.tokens.get(id as usize) else { continue };
            // Tags and markers: <|zh|>, <s>, <unk>.
            if tok.is_empty() || (tok.starts_with('<') && tok.ends_with('>')) {
                continue;
            }
            let at = offset + (t - QUERIES) as f64 * STEP_SECONDS;
            let starts_word = tok.starts_with('▁');
            let piece = tok.trim_start_matches('▁');
            if piece.is_empty() {
                continue;
            }
            if is_punct(piece) {
                if let Some(w) = out.last_mut() {
                    w.text.push_str(piece);
                }
                continue;
            }
            // A piece of an English word carries on the word before it.
            let carries = !starts_word
                && !piece.chars().any(is_cjk)
                && out.last().is_some_and(|w| w.text.chars().last().is_some_and(|c| c.is_alphanumeric() && !is_cjk(c)));
            if carries {
                let w = out.last_mut().unwrap();
                w.text.push_str(piece);
                w.t1 = at + STEP_SECONDS;
                continue;
            }
            if piece.chars().all(is_cjk) && piece.chars().count() > 1 {
                for c in piece.chars() {
                    out.push(HeardWord { text: c.to_string(), t0: at, t1: at + STEP_SECONDS });
                }
            } else {
                out.push(HeardWord { text: piece.to_string(), t0: at, t1: at + STEP_SECONDS });
            }
        }
        // Each word lasts until the next begins (half a second at most).
        for i in 0..out.len().saturating_sub(1) {
            let next = out[i + 1].t0;
            out[i].t1 = out[i].t1.max(next.min(out[i].t0 + 0.5));
        }
        out
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn model() -> Arc<SenseVoice> {
        let mut meta = HashMap::new();
        meta.insert("lfr_window_size".into(), "7".into());
        meta.insert("lfr_window_shift".into(), "6".into());
        meta.insert("neg_mean".into(), vec!["0"; 560].join(","));
        meta.insert("inv_stddev".into(), vec!["1"; 560].join(","));
        meta.insert("lang_zh".into(), "3".into());
        meta.insert("lang_auto".into(), "0".into());
        meta.insert("with_itn".into(), "14".into());
        meta.insert("without_itn".into(), "15".into());
        let tokens = "<blk> 0\n<|zh|> 1\n<|NEUTRAL|> 2\n<|Speech|> 3\n<|withitn|> 4\n你 5\n好 6\n， 7\n▁hel 8\nlo 9\n▁world 10\n。 11\n世界 12";
        SenseVoice::new(meta, tokens.into()).unwrap()
    }

    #[test]
    fn tokens_become_words_with_times() {
        let m = model();
        // Tags, then 你 你 _ 好 ， _ ▁hel lo ▁world 。
        let best = vec![1, 2, 3, 4, 5, 5, 0, 6, 7, 0, 8, 9, 10, 11, 12];
        let w = m.words(best, 10.0);
        let texts: Vec<&str> = w.iter().map(|w| w.text.as_str()).collect();
        assert_eq!(texts, ["你", "好，", "hello", "world。", "世", "界"]);
        assert!((w[0].t0 - 10.0).abs() < 1e-9);
        assert!((w[1].t0 - (10.0 + 3.0 * STEP_SECONDS)).abs() < 1e-9);
        assert_eq!(m.language_id("zh-Hans".into()), 3);
        assert_eq!(m.language_id("fr".into()), 0);
        assert_eq!(m.text_norm_id(false), 15);
    }

    #[test]
    fn frames_are_stacked_seven_every_six() {
        let m = model();
        // One second at 16 kHz: 98 filter-bank frames → ceil(98 / 6) = 17 stacks of 560.
        let x: Vec<f32> = (0..16_000).map(|i| (i as f32 * 0.05).sin() * 0.3).collect();
        let a = m.input(x.clone(), 16_000);
        assert_eq!((a.frames, a.dim), (17, 560));
        assert_eq!(a.data.len(), 17 * 560);
        // With zero mean and unit spread the first stack starts with the first frame, repeated.
        let (_, fb) = fbank(&x, true);
        assert_eq!(&a.data[..BANDS], &fb[..BANDS]);
        assert_eq!(&a.data[BANDS..2 * BANDS], &fb[..BANDS]);
        assert_eq!(&a.data[3 * BANDS..4 * BANDS], &fb[..BANDS]);
        assert_eq!(&a.data[4 * BANDS..5 * BANDS], &fb[BANDS..2 * BANDS]);
    }
}
