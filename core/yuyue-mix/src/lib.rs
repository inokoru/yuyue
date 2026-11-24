//! 混音: a radio play's timeline played as sound — voices, room tone, spot sounds and music on
//! four buses, mixed live so turning the room tone off or a new voice for one person changes
//! nothing else. The same code renders offline for export.
//!
//! Per item: gain, constant-power pan, a filter (low / high / band pass), a send to the reverb.
//! Per bus: gain and mute, set from any thread without locks. The room tone and music duck under
//! the voices (an envelope follower on the voice bus). One algorithmic reverb (comb + all-pass,
//! Schroeder–Moorer style) with presets per place. A peak limiter at the end holds −1 dBFS.
//!
//! The render callback doesn't allocate or lock: sources are decoded before they're scheduled.

use std::sync::Arc;
use std::sync::atomic::{AtomicU32, Ordering};

pub const RATE: u32 = 48_000;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Bus {
    Voice = 0,
    Bed = 1,
    Spot = 2,
    Music = 3,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum Filter {
    None,
    LowPass(f32),
    HighPass(f32),
    BandPass(f32, f32),
}

/// One sound on the timeline.
#[derive(Clone, Debug)]
pub struct Item {
    pub bus: Bus,
    /// Mono or stereo interleaved samples at `RATE`.
    pub samples: Arc<Vec<f32>>,
    pub stereo: bool,
    /// Seconds.
    pub at: f64,
    /// Play this long (loops when the source is shorter and `looped`); None: the source's length.
    pub len: Option<f64>,
    pub looped: bool,
    pub gain_db: f32,
    pub pan: f32,
    pub reverb_send: f32,
    pub filter: Filter,
    pub fade_in: f32,
    pub fade_out: f32,
}

/// Reverb settings for a place.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Room {
    pub size: f32,
    pub damp: f32,
    pub wet: f32,
}

pub fn room(preset: &str) -> Room {
    match preset {
        "hall" => Room { size: 0.85, damp: 0.3, wet: 0.35 },
        "room" => Room { size: 0.55, damp: 0.5, wet: 0.2 },
        "dream" => Room { size: 0.92, damp: 0.2, wet: 0.45 },
        _ => Room { size: 0.3, damp: 0.7, wet: 0.05 },
    }
}

fn db(x: f32) -> f32 {
    10f32.powf(x / 20.0)
}

/// A float shared without locks.
#[derive(Debug)]
pub struct Shared(AtomicU32);

impl Shared {
    pub fn new(v: f32) -> Shared {
        Shared(AtomicU32::new(v.to_bits()))
    }
    pub fn set(&self, v: f32) {
        self.0.store(v.to_bits(), Ordering::Relaxed)
    }
    pub fn get(&self) -> f32 {
        f32::from_bits(self.0.load(Ordering::Relaxed))
    }
}

/// Bus gains (linear; 0 mutes), shared with the UI thread.
#[derive(Debug)]
pub struct Controls {
    pub bus: [Shared; 4],
    pub master: Shared,
}

impl Default for Controls {
    fn default() -> Self {
        Controls { bus: [Shared::new(1.0), Shared::new(1.0), Shared::new(1.0), Shared::new(1.0)], master: Shared::new(1.0) }
    }
}

#[derive(Clone, Copy, Debug, Default)]
struct Biquad {
    b0: f32,
    b1: f32,
    b2: f32,
    a1: f32,
    a2: f32,
    z1: f32,
    z2: f32,
}

impl Biquad {
    fn new(kind: &str, f: f32, q: f32) -> Biquad {
        let w = 2.0 * std::f32::consts::PI * f / RATE as f32;
        let (s, c) = w.sin_cos();
        let alpha = s / (2.0 * q);
        let (b0, b1, b2) = match kind {
            "lp" => ((1.0 - c) / 2.0, 1.0 - c, (1.0 - c) / 2.0),
            "hp" => ((1.0 + c) / 2.0, -(1.0 + c), (1.0 + c) / 2.0),
            _ => (alpha, 0.0, -alpha),
        };
        let a0 = 1.0 + alpha;
        Biquad { b0: b0 / a0, b1: b1 / a0, b2: b2 / a0, a1: -2.0 * c / a0, a2: (1.0 - alpha) / a0, z1: 0.0, z2: 0.0 }
    }

    fn run(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
}

#[derive(Clone, Debug)]
struct Comb {
    buf: Vec<f32>,
    i: usize,
    store: f32,
}

#[derive(Clone, Debug)]
struct AllPass {
    buf: Vec<f32>,
    i: usize,
}

/// Schroeder–Moorer reverb: eight combs into four all-passes per side.
#[derive(Clone, Debug)]
struct Reverb {
    combs: [Vec<Comb>; 2],
    passes: [Vec<AllPass>; 2],
    room: Room,
}

impl Reverb {
    fn new(room: Room) -> Reverb {
        const COMBS: [usize; 8] = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
        const PASSES: [usize; 4] = [556, 441, 341, 225];
        let scale = RATE as f32 / 44_100.0;
        let side = |spread: usize| -> (Vec<Comb>, Vec<AllPass>) {
            (
                COMBS.iter().map(|&n| Comb { buf: vec![0.0; ((n + spread) as f32 * scale) as usize], i: 0, store: 0.0 }).collect(),
                PASSES.iter().map(|&n| AllPass { buf: vec![0.0; ((n + spread) as f32 * scale) as usize], i: 0 }).collect(),
            )
        };
        let (cl, pl) = side(0);
        let (cr, pr) = side(23);
        Reverb { combs: [cl, cr], passes: [pl, pr], room }
    }

    fn run(&mut self, x: f32) -> (f32, f32) {
        let feedback = 0.7 + 0.28 * self.room.size;
        let damp = self.room.damp * 0.4;
        let mut out = [0.0f32; 2];
        for (s, o) in out.iter_mut().enumerate() {
            let mut acc = 0.0;
            for c in &mut self.combs[s] {
                let y = c.buf[c.i];
                c.store = y * (1.0 - damp) + c.store * damp;
                c.buf[c.i] = x * 0.015 + c.store * feedback;
                c.i = (c.i + 1) % c.buf.len();
                acc += y;
            }
            for p in &mut self.passes[s] {
                let b = p.buf[p.i];
                p.buf[p.i] = acc + b * 0.5;
                p.i = (p.i + 1) % p.buf.len();
                acc = b - acc;
            }
            *o = acc * self.room.wet;
        }
        (out[0], out[1])
    }
}

struct Voice {
    item: Item,
    start: usize,
    frames: usize,
    filters: Vec<Biquad>,
    l: f32,
    r: f32,
}

/// The mixer for one timeline.
pub struct Mixer {
    voices: Vec<Voice>,
    reverb: Reverb,
    pub controls: Arc<Controls>,
    pos: usize,
    env: f32,
    limiter_gain: f32,
    /// How far the bed and music duck under speech (dB, negative).
    pub duck_db: f32,
    pub ceiling: f32,
}

/// Upper bounds for timeline coordinates (4 hours) and offline `render_all` (30 minutes)
/// so non-finite or corrupt timestamps cannot overflow `usize` or trigger OOM.
const MAX_TIMELINE_FRAMES: usize = RATE as usize * 60 * 60 * 4;
const MAX_RENDER_ALL_FRAMES: usize = RATE as usize * 60 * 30;

fn secs_to_frames(secs: f64, max: usize) -> usize {
    if !secs.is_finite() || secs <= 0.0 {
        return 0;
    }
    let frames = secs * RATE as f64;
    if !frames.is_finite() || frames >= max as f64 {
        max
    } else {
        frames as usize
    }
}

fn fade_frames(secs: f32) -> usize {
    if !secs.is_finite() || secs <= 0.0 {
        return 0;
    }
    let frames = secs as f64 * RATE as f64;
    if !frames.is_finite() || frames >= MAX_TIMELINE_FRAMES as f64 {
        MAX_TIMELINE_FRAMES
    } else {
        frames as usize
    }
}

impl Mixer {
    pub fn new(items: Vec<Item>, preset: &str, controls: Arc<Controls>) -> Mixer {
        let voices = items
            .into_iter()
            .map(|mut item| {
                let ch = if item.stereo { 2 } else { 1 };
                let src = item.samples.len() / ch;
                let frames = item.len.map(|l| secs_to_frames(l, MAX_TIMELINE_FRAMES)).unwrap_or(src);
                let filters = match item.filter {
                    Filter::None => vec![],
                    Filter::LowPass(f) => vec![Biquad::new("lp", f, 0.707), Biquad::new("lp", f, 0.707)],
                    Filter::HighPass(f) => vec![Biquad::new("hp", f, 0.707), Biquad::new("hp", f, 0.707)],
                    Filter::BandPass(a, b) => vec![Biquad::new("hp", a, 0.707), Biquad::new("hp", a, 0.707), Biquad::new("lp", b, 0.707), Biquad::new("lp", b, 0.707)],
                };
                let pan = if item.pan.is_finite() { item.pan.clamp(-1.0, 1.0) } else { 0.0 };
                let p = (pan + 1.0) * std::f32::consts::FRAC_PI_4;
                let gain_db = if item.gain_db.is_finite() { item.gain_db } else { 0.0 };
                let g = db(gain_db);
                if !item.reverb_send.is_finite() {
                    item.reverb_send = 0.0;
                }
                let start = secs_to_frames(item.at, MAX_TIMELINE_FRAMES);
                Voice { start, frames, filters, l: p.cos() * g, r: p.sin() * g, item }
            })
            .collect();
        Mixer { voices, reverb: Reverb::new(room(preset)), controls, pos: 0, env: 0.0, limiter_gain: 1.0, duck_db: -3.0, ceiling: db(-1.0) }
    }

    pub fn seek(&mut self, seconds: f64) {
        self.pos = secs_to_frames(seconds, MAX_TIMELINE_FRAMES);
        self.env = 0.0;
    }

    pub fn position(&self) -> f64 {
        self.pos as f64 / RATE as f64
    }

    pub fn duration(&self) -> f64 {
        self.voices.iter().map(|v| v.start.saturating_add(v.frames)).max().unwrap_or(0) as f64 / RATE as f64
    }

    fn sample(v: &Voice, f: usize) -> (f32, f32) {
        let s = &v.item.samples;
        let ch = if v.item.stereo { 2 } else { 1 };
        let n = s.len() / ch;
        if n == 0 {
            return (0.0, 0.0);
        }
        let i = if v.item.looped { f % n } else if f < n { f } else { return (0.0, 0.0) };
        if v.item.stereo { (s[2 * i], s[2 * i + 1]) } else { (s[i], s[i]) }
    }

    /// Fills `out` (stereo interleaved) and moves on.
    pub fn render(&mut self, out: &mut [f32]) {
        let frames = out.len() / 2;
        let gains: [f32; 4] = [self.controls.bus[0].get(), self.controls.bus[1].get(), self.controls.bus[2].get(), self.controls.bus[3].get()];
        let master = self.controls.master.get();
        let duck = db(self.duck_db);
        let attack = (-1.0 / (0.01 * RATE as f32)).exp();
        let release = (-1.0 / (0.3 * RATE as f32)).exp();
        let lim_release = (-1.0 / (0.08 * RATE as f32)).exp();
        for k in 0..frames {
            let t = self.pos.saturating_add(k);
            let mut bus = [[0.0f32; 2]; 4];
            let mut send = 0.0f32;
            for v in &mut self.voices {
                let end = v.start.saturating_add(v.frames);
                if t < v.start || t >= end {
                    continue;
                }
                let f = t - v.start;
                let (mut a, mut b) = Self::sample(v, f);
                // Fades.
                let fi = fade_frames(v.item.fade_in);
                let fo = fade_frames(v.item.fade_out);
                let mut g = 1.0;
                if fi > 0 && f < fi {
                    g *= f as f32 / fi as f32;
                }
                if fo > 0 && v.frames - f < fo {
                    g *= (v.frames - f) as f32 / fo as f32;
                }
                if !v.filters.is_empty() {
                    for (i, fl) in v.filters.iter_mut().enumerate() {
                        if i % 2 == 0 {
                            a = fl.run(a);
                        } else {
                            b = fl.run(b);
                        }
                    }
                }
                let (l, r) = if v.item.stereo { (a * v.l.max(v.r) * g, b * v.l.max(v.r) * g) } else { (a * v.l * g, b * v.r * g) };
                let bi = v.item.bus as usize;
                bus[bi][0] += l;
                bus[bi][1] += r;
                send += (l + r) * 0.5 * v.item.reverb_send * gains[bi];
            }
            // Follow the voices' level; duck the bed and music under it.
            let lvl = (bus[0][0].abs() + bus[0][1].abs()) * 0.5 * gains[0];
            let c = if lvl > self.env { attack } else { release };
            self.env = lvl + c * (self.env - lvl);
            let under = if self.env > 0.01 { duck } else { 1.0 };
            let (rl, rr) = self.reverb.run(send);
            let mut l = bus[0][0] * gains[0] + bus[2][0] * gains[2] + (bus[1][0] * gains[1] + bus[3][0] * gains[3]) * under + rl;
            let mut r = bus[0][1] * gains[0] + bus[2][1] * gains[2] + (bus[1][1] * gains[1] + bus[3][1] * gains[3]) * under + rr;
            l *= master;
            r *= master;
            // Peak limiter.
            let peak = l.abs().max(r.abs());
            let want = if peak * self.limiter_gain > self.ceiling { self.ceiling / peak } else { 1.0 };
            self.limiter_gain = if want < self.limiter_gain { want } else { want + lim_release * (self.limiter_gain - want) };
            l *= self.limiter_gain;
            r *= self.limiter_gain;
            out[2 * k] = l.clamp(-self.ceiling, self.ceiling);
            out[2 * k + 1] = r.clamp(-self.ceiling, self.ceiling);
        }
        self.pos = self.pos.saturating_add(frames);
    }

    /// The whole timeline, offline.
    pub fn render_all(&mut self) -> Vec<f32> {
        self.seek(0.0);
        let dur_frames = self.voices.iter().map(|v| v.start.saturating_add(v.frames)).max().unwrap_or(0);
        let n = dur_frames.saturating_add(RATE as usize).min(MAX_RENDER_ALL_FRAMES);
        let mut out = vec![0.0; n * 2];
        for chunk in out.chunks_mut(2048) {
            self.render(chunk);
        }
        out
    }
}

/// 16-bit PCM WAV of stereo interleaved samples.
pub fn wav(samples: &[f32]) -> Vec<u8> {
    let data: Vec<u8> = samples.iter().flat_map(|s| ((s.clamp(-1.0, 1.0) * 32767.0) as i16).to_le_bytes()).collect();
    let mut w = Vec::with_capacity(44 + data.len());
    w.extend(b"RIFF");
    w.extend((36 + data.len() as u32).to_le_bytes());
    w.extend(b"WAVEfmt ");
    w.extend(16u32.to_le_bytes());
    w.extend(1u16.to_le_bytes());
    w.extend(2u16.to_le_bytes());
    w.extend(RATE.to_le_bytes());
    w.extend((RATE * 4).to_le_bytes());
    w.extend(4u16.to_le_bytes());
    w.extend(16u16.to_le_bytes());
    w.extend(b"data");
    w.extend((data.len() as u32).to_le_bytes());
    w.extend(data);
    w
}

/// Integrated loudness, roughly (RMS in dBFS over 400 ms blocks above −70): for levelling clips.
pub fn loudness(mono: &[f32]) -> f32 {
    let block = (RATE as f32 * 0.4) as usize;
    let mut sum = 0.0f64;
    let mut n = 0;
    for b in mono.chunks(block) {
        let ms = b.iter().map(|x| (*x as f64) * (*x as f64)).sum::<f64>() / b.len().max(1) as f64;
        if ms > 1e-7 {
            sum += ms;
            n += 1;
        }
    }
    if n == 0 { -70.0 } else { (10.0 * (sum / n as f64).log10()) as f32 }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn tone(secs: f64, freq: f32, amp: f32) -> Arc<Vec<f32>> {
        Arc::new((0..(secs * RATE as f64) as usize).map(|i| (i as f32 * freq * 2.0 * std::f32::consts::PI / RATE as f32).sin() * amp).collect())
    }

    fn item(bus: Bus, s: Arc<Vec<f32>>, at: f64) -> Item {
        Item { bus, samples: s, stereo: false, at, len: None, looped: false, gain_db: 0.0, pan: 0.0, reverb_send: 0.0, filter: Filter::None, fade_in: 0.0, fade_out: 0.0 }
    }

    fn rms(x: &[f32]) -> f32 {
        (x.iter().map(|v| v * v).sum::<f32>() / x.len().max(1) as f32).sqrt()
    }

    #[test]
    fn the_same_timeline_renders_the_same() {
        let items = vec![item(Bus::Voice, tone(0.5, 220.0, 0.3), 0.1)];
        let a = Mixer::new(items.clone(), "room", Arc::new(Controls::default())).render_all();
        let b = Mixer::new(items, "room", Arc::new(Controls::default())).render_all();
        assert_eq!(a, b);
    }

    #[test]
    fn the_bed_ducks_under_speech_and_a_muted_bus_is_silent() {
        let mut bed = item(Bus::Bed, tone(4.0, 100.0, 0.2), 0.0);
        bed.looped = true;
        let items = vec![bed, item(Bus::Voice, tone(1.0, 440.0, 0.3), 2.0)];
        let c = Arc::new(Controls::default());
        let mut m = Mixer::new(items.clone(), "outdoor", c.clone());
        let out = m.render_all();
        let at = |t: f64| (t * RATE as f64) as usize * 2;
        // Bed alone (0.5–1.0 s) vs bed with speech, speech removed: compare the bed's level by
        // muting the voice bus in a second run.
        c.bus[Bus::Voice as usize].set(0.0);
        let _ = out;
        let c2 = Arc::new(Controls::default());
        let mut m2 = Mixer::new(items, "outdoor", c2.clone());
        let full = m2.render_all();
        let voice_only = {
            let c3 = Arc::new(Controls::default());
            c3.bus[Bus::Bed as usize].set(0.0);
            Mixer::new(vec![item(Bus::Voice, tone(1.0, 440.0, 0.3), 2.0)], "outdoor", c3).render_all()
        };
        let bed_alone = rms(&full[at(0.5)..at(1.0)]);
        let bed_under: Vec<f32> = full[at(2.4)..at(2.9)].iter().zip(&voice_only[at(2.4)..at(2.9)]).map(|(a, b)| a - b).collect();
        let ratio = 20.0 * (rms(&bed_under) / bed_alone).log10();
        assert!((-4.0..=-2.0).contains(&ratio), "{ratio}");
        let muted = Mixer::new(vec![item(Bus::Voice, tone(1.0, 440.0, 0.3), 0.0)], "outdoor", c).render_all();
        assert!(rms(&muted) < 1e-6);
    }

    #[test]
    fn nothing_goes_over_the_ceiling() {
        let items = vec![item(Bus::Voice, tone(1.0, 220.0, 0.9), 0.0), item(Bus::Voice, tone(1.0, 220.0, 0.9), 0.0)];
        let out = Mixer::new(items, "outdoor", Arc::new(Controls::default())).render_all();
        let peak = out.iter().fold(0.0f32, |a, x| a.max(x.abs()));
        assert!(peak <= db(-1.0) + 1e-6, "{peak}");
    }

    #[test]
    fn far_voices_lose_their_highs() {
        let mut far = item(Bus::Voice, tone(0.5, 6000.0, 0.3), 0.0);
        far.filter = Filter::LowPass(1500.0);
        let near = item(Bus::Voice, tone(0.5, 6000.0, 0.3), 0.0);
        let a = Mixer::new(vec![far], "outdoor", Arc::new(Controls::default())).render_all();
        let b = Mixer::new(vec![near], "outdoor", Arc::new(Controls::default())).render_all();
        assert!(rms(&a) < rms(&b) * 0.2);
    }

    #[test]
    fn a_wav_has_its_header() {
        let w = wav(&[0.0, 0.5, -0.5, 1.0]);
        assert_eq!(&w[..4], b"RIFF");
        assert_eq!(w.len(), 44 + 8);
    }

    #[test]
    fn non_finite_times_do_not_overflow_or_panic() {
        let mut inf = item(Bus::Voice, tone(0.1, 440.0, 0.2), f64::INFINITY);
        inf.len = Some(1.0);
        inf.fade_in = f32::INFINITY;
        inf.fade_out = f32::NAN;
        let mut nan = item(Bus::Voice, tone(0.1, 440.0, 0.2), f64::NAN);
        nan.len = Some(f64::INFINITY);
        let mut m = Mixer::new(vec![inf, nan], "room", Arc::new(Controls::default()));
        assert!(m.duration().is_finite());
        m.seek(f64::INFINITY);
        let mut out = vec![0.0; 128];
        m.render(&mut out);
        assert!(out.iter().all(|x| x.is_finite()));
    }
}
