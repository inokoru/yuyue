//! The play's timeline through `yuyue-mix`, for the apps: they decode the clips and sounds,
//! hand the samples over once, then pull mixed audio a block at a time from a worker thread
//! into the ring buffer their audio callback reads (the callback itself never crosses the FFI).
//! The same object renders a whole scene for export.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use yuyue_mix::{Bus, Controls, Filter as MixFilter, Item as MixItem, Mixer};

use super::timeline::{TimelineFilter, TimelineItem, DramaTimeline};
use crate::api::lock;

/// Decoded audio the app hands over: 48 kHz float, mono or stereo interleaved.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct MixSource {
    /// A clip key or a sound asset id, as the timeline names it.
    pub id: String,
    pub samples: Vec<f32>,
    pub stereo: bool,
}

#[derive(uniffi::Object)]
pub struct DramaMixer {
    mixer: Mutex<Mixer>,
}

fn filter(f: &TimelineFilter) -> MixFilter {
    match *f {
        TimelineFilter::None => MixFilter::None,
        TimelineFilter::LowPass { hz } => MixFilter::LowPass(hz),
        TimelineFilter::HighPass { hz } => MixFilter::HighPass(hz),
        TimelineFilter::BandPass { low, high } => MixFilter::BandPass(low, high),
    }
}

/// The timeline's items with their samples; an item whose source wasn't given is left out.
pub fn items(tl: &DramaTimeline, sources: &HashMap<String, (Arc<Vec<f32>>, bool)>) -> Vec<MixItem> {
    let mut out = Vec::new();
    for it in &tl.items {
        let (bus, id, at, len, looped, gain, pan, send, f, fi, fo) = match it {
            TimelineItem::Voice { clip, at, seconds, gain_db, pan, reverb_send, filter: fl, .. } => (Bus::Voice, clip, *at, Some(*seconds), false, *gain_db, *pan, *reverb_send, filter(fl), 0.0, 0.0),
            TimelineItem::Bed { asset, from, to, gain_db, fade_in, fade_out } => (Bus::Bed, asset, *from, Some(to - from), true, *gain_db, 0.0, 0.0, MixFilter::None, *fade_in, *fade_out),
            TimelineItem::Spot { asset, at, gain_db, pan } => (Bus::Spot, asset, *at, None, false, *gain_db, *pan, 0.2, MixFilter::None, 0.0, 0.0),
            TimelineItem::Music { asset, at, gain_db } => (Bus::Music, asset, *at, None, false, *gain_db, 0.0, 0.0, MixFilter::None, 0.3, 0.5),
        };
        let Some((samples, stereo)) = sources.get(id) else { continue };
        let gain = if bus == Bus::Voice { gain + level_to_target(samples) } else { gain };
        out.push(MixItem { bus, samples: samples.clone(), stereo: *stereo, at, len, looped, gain_db: gain, pan, reverb_send: send, filter: f, fade_in: fi, fade_out: fo });
    }
    out
}

/// Every voice clip is levelled to this before its own gain (耳语 −4, 喊 +2 on top), so voices
/// from different engines sit at the same loudness.
pub const CLIP_LOUDNESS: f32 = -20.0;

/// dB that brings a clip to `CLIP_LOUDNESS`; at most 12 either way, none for silence.
pub fn level_to_target(samples: &[f32]) -> f32 {
    let l = yuyue_mix::loudness(samples);
    if l <= -60.0 { 0.0 } else { (CLIP_LOUDNESS - l).clamp(-12.0, 12.0) }
}

/// 生成音效: the sounds made for books, by id → their samples' file (`f32` little-endian, mono,
/// 48 kHz). Filled as books' scenes are laid out.
pub static MADE_SOUNDS: std::sync::LazyLock<Mutex<HashMap<String, std::path::PathBuf>>> = std::sync::LazyLock::new(Default::default);

fn made_sound(id: &str) -> Option<(Arc<Vec<f32>>, bool)> {
    let path = MADE_SOUNDS.lock().ok()?.get(id).cloned()?;
    let bytes = std::fs::read(path).ok()?;
    let v: Vec<f32> = bytes.chunks_exact(4).map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect();
    Some((Arc::new(v), false))
}

/// Cap single offline/span renders at 30 minutes of 48 kHz audio to prevent OOM on bad spans.
const MAX_RENDER_FRAMES: usize = yuyue_mix::RATE as usize * 60 * 30;

#[uniffi::export]
impl DramaMixer {
    #[uniffi::constructor]
    pub fn new(timeline: DramaTimeline, sources: Vec<MixSource>) -> Arc<DramaMixer> {
        let mut map: HashMap<String, (Arc<Vec<f32>>, bool)> = sources.into_iter().map(|s| (s.id, (Arc::new(s.samples), s.stereo))).collect();
        // The library's and built-in sounds the timeline names: read or made here, not handed over.
        for it in &timeline.items {
            let id = match it {
                TimelineItem::Bed { asset, .. } | TimelineItem::Spot { asset, .. } | TimelineItem::Music { asset, .. } => asset,
                TimelineItem::Voice { .. } => continue,
            };
            if !map.contains_key(id)
                && let Some(s) = super::library::samples(id).or_else(|| super::builtin::samples(id)).or_else(|| made_sound(id)) {
                    map.insert(id.clone(), s);
                }
        }
        let controls = Arc::new(Controls::default());
        let mixer = Mixer::new(items(&timeline, &map), &timeline.reverb, controls);
        Arc::new(DramaMixer { mixer: Mutex::new(mixer) })
    }

    /// The next `frames` frames, stereo interleaved.
    pub fn render(&self, frames: u32) -> Vec<f32> {
        let count = (frames as usize).min(MAX_RENDER_FRAMES);
        let Some(samples) = count.checked_mul(2) else { return Vec::new() };
        let mut out = vec![0.0; samples];
        lock(&self.mixer).render(&mut out);
        out
    }

    /// [from, to) of the timeline, mono (the two sides summed at equal power), for a player
    /// that plays one sentence at a time. Mixed from a second before and that part thrown away,
    /// so the reverb's tail and the room tone are as they'd be had it played through.
    pub fn render_span(&self, from: f64, to: f64) -> Vec<f32> {
        if !from.is_finite() || !to.is_finite() || to <= from {
            return Vec::new();
        }
        let from = from.max(0.0);
        if to <= from {
            return Vec::new();
        }
        let rate = yuyue_mix::RATE as f64;
        let span_secs = to - from;
        if !span_secs.is_finite() || span_secs <= 0.0 {
            return Vec::new();
        }
        let out_frames = (span_secs * rate).min(MAX_RENDER_FRAMES as f64) as usize;
        let Some(out_samples) = out_frames.checked_mul(2) else { return Vec::new() };
        let pre = from.clamp(0.0, super::api::WINDOW_PREROLL);
        let pre_frames = (pre * rate).min(MAX_RENDER_FRAMES as f64) as usize;
        let mut m = lock(&self.mixer);
        m.seek(from - pre);
        let mut scratch = vec![0.0; pre_frames * 2];
        m.render(&mut scratch);
        let mut out = vec![0.0; out_samples];
        m.render(&mut out);
        out.chunks_exact(2).map(|c| ((c[0] + c[1]) * std::f32::consts::FRAC_1_SQRT_2).clamp(-1.0, 1.0)).collect()
    }

    pub fn seek(&self, seconds: f64) {
        lock(&self.mixer).seek(seconds);
    }

    pub fn position(&self) -> f64 {
        lock(&self.mixer).position()
    }

    pub fn duration(&self) -> f64 {
        lock(&self.mixer).duration()
    }

    /// The whole timeline as a 16-bit stereo WAV, for export.
    pub fn render_wav(&self) -> Vec<u8> {
        yuyue_mix::wav(&lock(&self.mixer).render_all())
    }
}

/// 16-bit stereo WAV of interleaved samples at the mixer's rate.
pub fn wav(stereo: &[f32]) -> Vec<u8> {
    yuyue_mix::wav(stereo)
}

#[cfg(test)]
mod level_tests {
    use super::*;

    fn tone(amp: f32) -> Vec<f32> {
        (0..48_000).map(|i| (i as f32 * 220.0 * 2.0 * std::f32::consts::PI / 48_000.0).sin() * amp).collect()
    }

    #[test]
    fn quiet_and_loud_clips_come_out_alike() {
        for amp in [0.05, 0.1, 0.5] {
            let t = tone(amp);
            let g = level_to_target(&t);
            let after = yuyue_mix::loudness(&t.iter().map(|x| x * 10f32.powf(g / 20.0)).collect::<Vec<_>>());
            assert!((after - CLIP_LOUDNESS).abs() < 0.5, "{amp}: {after}");
        }
        assert_eq!(level_to_target(&vec![0.0; 4800]), 0.0);
        assert_eq!(level_to_target(&tone(0.005)), 12.0);
    }

    #[test]
    fn render_span_handles_non_finite_and_invalid_bounds() {
        let tl = DramaTimeline { section: 0, first: 0, last: 0, items: vec![], reverb: "room".into(), duration: 0.0 };
        let mixer = DramaMixer::new(tl, vec![]);
        assert!(mixer.render_span(0.0, f64::INFINITY).is_empty());
        assert!(mixer.render_span(f64::NEG_INFINITY, 0.0).is_empty());
        assert!(mixer.render_span(f64::NAN, 1.0).is_empty());
        assert!(mixer.render_span(f64::INFINITY, f64::INFINITY).is_empty());
        assert!(mixer.render_span(2.0, 1.0).is_empty());
        assert_eq!(mixer.render(0).len(), 0);
    }
}
