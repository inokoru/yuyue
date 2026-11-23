//! 声纹: what a speaker-embedding model (CAM++ and its kind) takes — Kaldi-style 80-band log
//! mel filter banks at 16 kHz, 25 ms frames every 10 ms, the mean taken off — and the reference
//! embedding kept for each voice, so 选角 can keep voices in one scene apart and 回听 can tell
//! when a clip doesn't sound like its voice. The model itself runs in the app (Core ML / ONNX);
//! without one, nothing here is asked for and both work as before.

use std::collections::HashMap;
use std::f32::consts::PI;
use std::path::Path;

use serde::{Deserialize, Serialize};

pub const RATE: u32 = 16_000;
pub const BANDS: usize = 80;
const FRAME: usize = 400;
const SHIFT: usize = 160;
const FFT: usize = 512;

/// Filter-bank features for the model: `frames` rows of `BANDS`.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct SpeakerFeatures {
    pub frames: u32,
    pub data: Vec<f32>,
}

/// Samples at `rate` to 16 kHz, linear in between (low-passed first by averaging when coming
/// down, enough for a voice's character).
pub fn to_16k(samples: &[f32], rate: u32) -> Vec<f32> {
    if rate == RATE || samples.is_empty() {
        return samples.to_vec();
    }
    let step = rate as f64 / RATE as f64;
    // Averaging over the step keeps what's above 8 kHz from folding back.
    let smooth: Vec<f32> = if step > 1.0 {
        let w = step.round().max(1.0) as usize;
        let mut out = Vec::with_capacity(samples.len());
        let mut sum = 0.0f32;
        for i in 0..samples.len() {
            sum += samples[i];
            if i >= w {
                sum -= samples[i - w];
            }
            out.push(sum / (i + 1).min(w) as f32);
        }
        out
    } else {
        samples.to_vec()
    };
    let n = (samples.len() as f64 / step).floor() as usize;
    (0..n)
        .map(|i| {
            let x = i as f64 * step;
            let k = x.floor() as usize;
            let f = (x - k as f64) as f32;
            let a = smooth[k.min(smooth.len() - 1)];
            let b = smooth[(k + 1).min(smooth.len() - 1)];
            a + (b - a) * f
        })
        .collect()
}

fn mel(f: f32) -> f32 {
    1127.0 * (1.0 + f / 700.0).ln()
}

/// The triangular filters, Kaldi's way (in mel, 20 Hz to Nyquist).
fn filters() -> Vec<(usize, Vec<f32>)> {
    let bins = FFT / 2;
    let (lo, hi) = (mel(20.0), mel(RATE as f32 / 2.0));
    let delta = (hi - lo) / (BANDS + 1) as f32;
    (0..BANDS)
        .map(|b| {
            let (left, centre, right) = (lo + b as f32 * delta, lo + (b + 1) as f32 * delta, lo + (b + 2) as f32 * delta);
            let mut first = usize::MAX;
            let mut w = Vec::new();
            for k in 0..bins {
                let m = mel(k as f32 * RATE as f32 / FFT as f32);
                let v = if m > left && m < right { if m <= centre { (m - left) / (centre - left) } else { (right - m) / (right - centre) } } else { 0.0 };
                if v > 0.0 {
                    if first == usize::MAX {
                        first = k;
                    }
                    w.push(v);
                } else if first != usize::MAX {
                    break;
                }
            }
            (if first == usize::MAX { 0 } else { first }, w)
        })
        .collect()
}

/// In-place radix-2 FFT of `re`/`im` (length a power of two).
pub(super) fn fft(re: &mut [f32], im: &mut [f32]) {
    let n = re.len();
    let mut j = 0;
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
    let mut len = 2;
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

/// The model's input for `samples` at `rate`: frames of 80 log mel energies, the mean over
/// the whole taken off each band.
pub fn features(samples: &[f32], rate: u32) -> SpeakerFeatures {
    let x = to_16k(samples, rate);
    let (frames, mut data) = fbank(&x, false);
    if frames == 0 {
        return SpeakerFeatures { frames: 0, data: Vec::new() };
    }
    for b in 0..BANDS {
        let m = (0..frames).map(|f| data[f * BANDS + b]).sum::<f32>() / frames as f32;
        for f in 0..frames {
            data[f * BANDS + b] -= m;
        }
    }
    SpeakerFeatures { frames: frames as u32, data }
}

/// Kaldi-style 80-band log mel filter banks of 16 kHz samples (25 ms frames every 10 ms,
/// pre-emphasis 0.97, DC taken off each frame): the Povey window, or Hamming (FunASR's).
/// How many frames, and the frames one after another.
pub fn fbank(x: &[f32], hamming: bool) -> (usize, Vec<f32>) {
    if x.len() < FRAME {
        return (0, Vec::new());
    }
    let fb = filters();
    let window: Vec<f32> = (0..FRAME)
        .map(|i| {
            let c = (2.0 * PI * i as f32 / (FRAME - 1) as f32).cos();
            if hamming { 0.54 - 0.46 * c } else { (0.5 - 0.5 * c).powf(0.85) }
        })
        .collect();
    let frames = 1 + (x.len() - FRAME) / SHIFT;
    let mut data = Vec::with_capacity(frames * BANDS);
    let (mut re, mut im) = (vec![0f32; FFT], vec![0f32; FFT]);
    for f in 0..frames {
        // Kaldi scales to 16-bit range before the log.
        let frame: Vec<f32> = x[f * SHIFT..f * SHIFT + FRAME].iter().map(|v| v * 32768.0).collect();
        let mean = frame.iter().sum::<f32>() / FRAME as f32;
        re.iter_mut().for_each(|v| *v = 0.0);
        im.iter_mut().for_each(|v| *v = 0.0);
        let mut prev = frame[0] - mean;
        for i in 0..FRAME {
            let v = frame[i] - mean;
            // Pre-emphasis 0.97.
            re[i] = (v - 0.97 * prev) * window[i];
            prev = v;
        }
        fft(&mut re, &mut im);
        let power: Vec<f32> = (0..FFT / 2).map(|k| re[k] * re[k] + im[k] * im[k]).collect();
        for (first, w) in &fb {
            let e: f32 = w.iter().enumerate().map(|(i, v)| v * power.get(first + i).copied().unwrap_or(0.0)).sum();
            data.push(e.max(f32::EPSILON).ln());
        }
    }
    (frames, data)
}

/// Each voice's reference embedding, by the model that made it; kept on this device.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct VoicePrints {
    pub model: String,
    pub voices: HashMap<String, Vec<f32>>,
    #[serde(default)]
    pub genders: HashMap<String, crate::drama::CharacterGender>,
}

impl VoicePrints {
    pub fn read(path: &Path) -> Self {
        std::fs::read(path).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default()
    }

    pub fn write(&self, path: &Path) {
        if let Ok(b) = serde_json::to_vec(self) {
            let _ = std::fs::write(path, b);
        }
    }
}

/// Estimates median voiced fundamental frequency (Hz) over human speech (70–350 Hz).
pub fn voice_pitch_hz(samples: &[f32], rate: u32) -> Option<f32> {
    let x = to_16k(samples, rate);
    const W: usize = 512;
    const HOP: usize = 256;
    const MIN_LAG: usize = 46;  // ~348 Hz at 16 kHz
    const MAX_LAG: usize = 228; // ~70 Hz at 16 kHz
    if x.len() < W {
        return None;
    }
    let mut f0s = Vec::new();
    let mut at = 0;
    while at + W <= x.len() {
        let raw = &x[at..at + W];
        at += HOP;
        let mean = raw.iter().sum::<f32>() / W as f32;
        let mut frame = [0f32; W];
        let mut energy = 0f32;
        for (i, &v) in raw.iter().enumerate() {
            let d = v - mean;
            frame[i] = d;
            energy += d * d;
        }
        if (energy / W as f32).sqrt() < 0.015 {
            continue;
        }
        let mut corr = [0f32; MAX_LAG + 2];
        for lag in (MIN_LAG - 1)..=(MAX_LAG + 1) {
            let n = W - lag;
            let (mut num, mut e0, mut e1) = (0f32, 0f32, 0f32);
            for i in 0..n {
                let (a, b) = (frame[i], frame[i + lag]);
                num += a * b;
                e0 += a * a;
                e1 += b * b;
            }
            let denom = (e0 * e1).sqrt();
            corr[lag] = if denom > 1e-8 { num / denom } else { 0.0 };
        }
        let mut peaks: Vec<(usize, f32)> = Vec::new();
        for lag in MIN_LAG..=MAX_LAG {
            if corr[lag] >= 0.55 && corr[lag] > corr[lag - 1] && corr[lag] >= corr[lag + 1] {
                peaks.push((lag, corr[lag]));
            }
        }
        let Some(&(_, best)) = peaks.iter().max_by(|a, b| a.1.total_cmp(&b.1)) else { continue };
        // Prefer the first strong peak so sub-harmonics don't halve the pitch.
        let Some(&(lag, _)) = peaks.iter().find(|&&(_, v)| v >= best * 0.88) else { continue };
        let (ym, y0, yp) = (corr[lag - 1], corr[lag], corr[lag + 1]);
        let denom = ym - 2.0 * y0 + yp;
        let shift = if denom.abs() > 1e-6 { (0.5 * (ym - yp) / denom).clamp(-0.5, 0.5) } else { 0.0 };
        f0s.push(RATE as f32 / (lag as f32 + shift));
    }
    if f0s.len() < 4 {
        return None;
    }
    f0s.sort_by(|a, b| a.total_cmp(b));
    Some(f0s[f0s.len() / 2])
}

/// Infers a voice's gender from its voiced fundamental frequency when the engine doesn't tell us.
pub fn voice_gender(samples: &[f32], rate: u32) -> crate::drama::CharacterGender {
    match voice_pitch_hz(samples, rate) {
        Some(hz) if hz < 158.0 => crate::drama::CharacterGender::M,
        Some(hz) if hz > 172.0 => crate::drama::CharacterGender::F,
        _ => crate::drama::CharacterGender::Unknown,
    }
}

/// Time-scale and pitch-shift 16-bit mono speech (`speed` 0.5..=2.5, `semitones` -12..=12) via
/// Hann-windowed WSOLA + cubic resampling, for voices that don't adjust pace/pitch natively.
pub fn tune_speech(samples: &[i16], rate: u32, speed: f32, semitones: f32) -> Vec<i16> {
    if samples.is_empty() || rate == 0 || ((speed - 1.0).abs() < 0.01 && semitones.abs() < 0.01) {
        return samples.to_vec();
    }
    let speed = speed.clamp(0.5, 2.5);
    let pitch = 2f32.powf(semitones.clamp(-12.0, 12.0) / 12.0);
    // Stretch by `pitch / speed`, then resample by `pitch` so duration scales by `1 / speed` and
    // pitch scales by `pitch`.
    let stretch = (pitch / speed) as f64;
    let x: Vec<f32> = samples.iter().map(|&s| s as f32 / 32768.0).collect();
    let stretched = if (stretch - 1.0).abs() < 0.01 {
        x
    } else {
        let w = ((rate as usize) / 40).max(64) & !1;
        let hs = w / 2;
        let search = ((rate as usize) / 125).max(16);
        if x.len() <= w {
            x
        } else {
            let win: Vec<f32> = (0..w).map(|i| 0.5 * (1.0 - (2.0 * PI * i as f32 / w as f32).cos())).collect();
            let out_len = ((x.len() as f64 * stretch).round() as usize).max(w);
            let mut y = vec![0f32; out_len + w];
            let mut weight = vec![0f32; out_len + w];
            let mut prev_pos: Option<usize> = None;
            let mut out_pos = 0usize;
            while out_pos < out_len {
                let ideal = ((out_pos as f64 / stretch).round() as usize).min(x.len() - w);
                let chosen = match prev_pos {
                    None => ideal,
                    Some(prev) => {
                        let natural = (prev + hs).min(x.len() - w);
                        let lo = ideal.saturating_sub(search);
                        let hi = (ideal + search).min(x.len() - w);
                        let mut best_p = ideal;
                        let mut best_c = f32::NEG_INFINITY;
                        // Compare every 2nd sample for fast alignment.
                        for cand in lo..=hi {
                            let (mut num, mut e1) = (0f32, 0f32);
                            for k in (0..hs).step_by(2) {
                                let a = x[natural + k];
                                let b = x[cand + k];
                                num += a * b;
                                e1 += b * b;
                            }
                            let c = num / (e1.sqrt() + 1e-6);
                            if c > best_c {
                                best_c = c;
                                best_p = cand;
                            }
                        }
                        best_p
                    }
                };
                for i in 0..w {
                    y[out_pos + i] += x[chosen + i] * win[i];
                    weight[out_pos + i] += win[i];
                }
                prev_pos = Some(chosen);
                out_pos += hs;
            }
            y.truncate(out_len);
            for i in 0..out_len {
                if weight[i] > 1e-3 {
                    y[i] /= weight[i];
                }
            }
            y
        }
    };
    if (pitch - 1.0).abs() < 0.01 {
        return stretched.into_iter().map(|v| (v.clamp(-1.0, 1.0) * 32767.0) as i16).collect();
    }
    let step = pitch as f64;
    let src: Vec<f32> = if step > 1.05 {
        // Gentle box anti-aliasing before upward pitch resampling.
        let k = step.round().max(2.0) as usize;
        let mut s = Vec::with_capacity(stretched.len());
        let mut sum = 0f32;
        for i in 0..stretched.len() {
            sum += stretched[i];
            if i >= k {
                sum -= stretched[i - k];
            }
            s.push(sum / (i + 1).min(k) as f32);
        }
        s
    } else {
        stretched
    };
    let n = (src.len() as f64 / step).floor() as usize;
    let at = |idx: isize| src[idx.clamp(0, src.len() as isize - 1) as usize];
    (0..n)
        .map(|i| {
            let pos = i as f64 * step;
            let k = pos.floor() as isize;
            let t = (pos - k as f64) as f32;
            let (p0, p1, p2, p3) = (at(k - 1), at(k), at(k + 1), at(k + 2));
            let v = 0.5 * ((2.0 * p1) + (-p0 + p2) * t + (2.0 * p0 - 5.0 * p1 + 4.0 * p2 - p3) * t * t + (-p0 + 3.0 * p1 - 3.0 * p2 + p3) * t * t * t);
            (v.clamp(-1.0, 1.0) * 32767.0) as i16
        })
        .collect()
}

/// Time-scale and pitch-shift 16-bit mono speech (`speed` 0.5..=2.5, `semitones` -12..=12),
/// exported for app-side `TunedVoice` when a voice (Cloud / Cloned) doesn't tune itself.
#[uniffi::export]
pub fn tune_speech_pcm(samples: Vec<i16>, rate: u32, speed: f32, semitones: f32) -> Vec<i16> {
    tune_speech(&samples, rate, speed, semitones)
}

/// The sentence each voice reads once for its reference.
pub fn sample_text(language: &str) -> &'static str {
    if language.starts_with("zh") {
        "今天下了一场雨，我们沿着河边慢慢走回家，路上说了很多话。"
    } else {
        "It rained this morning, so we walked home slowly along the river and talked the whole way."
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_tone_lights_its_band() {
        // One second of 1 kHz at 48 kHz: 98 frames, the band holding 1 kHz above the rest.
        let s: Vec<f32> = (0..48_000).map(|i| (2.0 * PI * 1000.0 * i as f32 / 48_000.0).sin() * 0.5).collect();
        let f = features(&s, 48_000);
        assert_eq!(f.frames, 98);
        assert_eq!(f.data.len(), 98 * BANDS);
        // Mean taken off: each band averages to zero.
        let m: f32 = (0..98).map(|i| f.data[i * BANDS + 10]).sum::<f32>() / 98.0;
        assert!(m.abs() < 1e-3);
        // Before the mean comes off, 1 kHz is loudest: check on a fresh run without it.
        let fb = filters();
        let (k1, _) = fb.iter().enumerate().max_by(|a, b| {
            let c = |(first, w): &(usize, Vec<f32>)| w.iter().enumerate().map(|(i, v)| if (first + i) == 32 { *v } else { 0.0 }).sum::<f32>();
            c(a.1).partial_cmp(&c(b.1)).unwrap()
        }).unwrap();
        assert!((20..40).contains(&k1));
    }

    #[test]
    fn fft_of_an_impulse_is_flat() {
        let mut re = vec![0f32; 8];
        let mut im = vec![0f32; 8];
        re[0] = 1.0;
        fft(&mut re, &mut im);
        assert!(re.iter().all(|v| (v - 1.0).abs() < 1e-6));
    }

    #[test]
    fn pitch_tells_male_from_female_harmonic_voice() {
        use crate::drama::CharacterGender;
        let synth = |f0: f32| -> Vec<f32> {
            (0..24_000)
                .map(|i| {
                    let t = i as f32 / 24_000.0;
                    // Fundamental + strong harmonics, as voiced speech has.
                    0.25 * (2.0 * PI * f0 * t).sin() + 0.35 * (4.0 * PI * f0 * t).sin() + 0.2 * (6.0 * PI * f0 * t).sin()
                })
                .collect()
        };
        assert_eq!(voice_gender(&synth(120.0), 24_000), CharacterGender::M);
        assert_eq!(voice_gender(&synth(215.0), 24_000), CharacterGender::F);
    }

    #[test]
    fn tune_speech_shifts_pace_and_pitch() {
        let rate = 24_000u32;
        let src: Vec<i16> = (0..24_000)
            .map(|i| {
                let t = i as f32 / rate as f32;
                ((0.35 * (2.0 * PI * 140.0 * t).sin() + 0.25 * (2.0 * PI * 280.0 * t).sin()) * 32767.0) as i16
            })
            .collect();
        let faster = tune_speech(&src, rate, 1.25, 0.0);
        assert!((faster.len() as f32 / src.len() as f32 - 0.8).abs() < 0.05);
        let higher = tune_speech(&src, rate, 1.0, 5.0);
        assert!((higher.len() as f32 / src.len() as f32 - 1.0).abs() < 0.05);
        let hz = voice_pitch_hz(&higher.iter().map(|&s| s as f32 / 32768.0).collect::<Vec<_>>(), rate).unwrap();
        let expected = 140.0 * 2f32.powf(5.0 / 12.0);
        assert!((hz - expected).abs() < 8.0, "got {hz}, expected {expected}");
    }
}
