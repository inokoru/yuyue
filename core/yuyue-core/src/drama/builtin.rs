//! 内置声音: room tones and a few spot sounds made here, from noise and sine waves, so a play
//! has its rooms and rain with nothing to download and no one else's recordings to license.
//! Plain on purpose: a quiet room, rain, wind, water, insects at night; a knock, steps, a bell.
//! A downloaded library, when there is one, is looked at first (its keys are more specific).
//!
//! Everything is made the same way every time (seeded), at the mixer's rate, and kept once made.

use std::collections::HashMap;
use std::f32::consts::PI;
use std::sync::{Arc, Mutex, OnceLock};

use super::sound::SoundAsset;

const RATE: f32 = yuyue_mix::RATE as f32;
/// A room tone's length; it loops, the seam crossfaded.
const BED_SECONDS: f32 = 30.0;
const SEAM: f32 = 2.0;
pub const PREFIX: &str = "builtin:";

/// (id, key, loops, seconds)
const LIST: &[(&str, &str, bool, f32)] = &[
    ("room", "indoor", true, BED_SECONDS),
    ("room_night", "indoor/night", true, BED_SECONDS),
    ("rain_in", "indoor/rain", true, BED_SECONDS),
    ("air", "outdoor", true, BED_SECONDS),
    ("night_out", "outdoor/night", true, BED_SECONDS),
    ("rain_out", "outdoor/rain", true, BED_SECONDS),
    ("wind", "outdoor/wind", true, BED_SECONDS),
    ("snow", "outdoor/snow", true, BED_SECONDS),
    ("river", "河边", true, BED_SECONDS),
    ("sea", "海边", true, BED_SECONDS),
    ("sea_dock", "码头", true, BED_SECONDS),
    ("knock", "door_wood_knock", false, 1.2),
    ("knock_table", "knock_table", false, 0.8),
    ("steps", "steps_wood", false, 2.4),
    ("steps_stone", "steps_stone", false, 2.4),
    ("steps_run", "steps_running", false, 1.6),
    ("thunder", "thunder", false, 6.0),
    ("bell_temple", "bell_temple", false, 7.0),
    ("bell_small", "bell_small", false, 2.5),
    ("gong", "gong", false, 6.0),
    ("chime", "wind_chime", false, 4.0),
    ("clink", "jade_clink", false, 1.2),
    ("cup", "cup_set", false, 0.6),
    ("tick", "clock_tick", false, 4.0),
    ("strike", "clock_strike", false, 6.0),
    ("drum", "drum", false, 1.5),
    ("drip", "drip", false, 2.0),
    ("cricket", "cricket", false, 4.0),
    // 过门: a short phrase between scenes.
    ("pluck_phrase", TRANSITION, false, 4.5),
    ("bell_phrase", TRANSITION, false, 4.0),
];

/// The key of a 过门 (music between scenes), in the built-in sounds and a library alike.
pub const TRANSITION: &str = "transition";

/// The built-in sounds, as library entries.
pub fn assets() -> Vec<SoundAsset> {
    LIST.iter().map(|(id, key, loops, s)| SoundAsset { id: format!("{PREFIX}{id}"), key: (*key).into(), seconds: *s, loops: *loops, license: crate::i18n::tr("语阅自制", "Built in").into() }).collect()
}

/// A built-in sound's samples (48 kHz; stereo interleaved for room tones, mono for the rest),
/// made the first time it's asked for and kept.
pub fn samples(id: &str) -> Option<(Arc<Vec<f32>>, bool)> {
    static MADE: OnceLock<Mutex<HashMap<String, (Arc<Vec<f32>>, bool)>>> = OnceLock::new();
    let name = id.strip_prefix(PREFIX)?;
    let made = MADE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Some(s) = made.lock().ok()?.get(name) {
        return Some(s.clone());
    }
    let (_, _, loops, seconds) = LIST.iter().find(|x| x.0 == name)?;
    let out = if *loops { (Arc::new(bed(name)), true) } else { (Arc::new(spot(name, *seconds)), false) };
    made.lock().ok()?.insert(name.to_string(), out.clone());
    Some(out)
}

// MARK: Pieces

/// A small seeded noise source (xorshift).
struct Rng(u64);

impl Rng {
    fn new(seed: u64) -> Rng {
        Rng(seed.wrapping_mul(0x9E37_79B9_7F4A_7C15) | 1)
    }
    fn next(&mut self) -> f32 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        (self.0 >> 40) as f32 / (1u64 << 24) as f32 * 2.0 - 1.0
    }
    /// 0..1
    fn unit(&mut self) -> f32 {
        (self.next() + 1.0) * 0.5
    }
}

/// Pink-ish noise (Paul Kellet's economy filter).
struct Pink {
    b: [f32; 3],
}

impl Pink {
    fn new() -> Pink {
        Pink { b: [0.0; 3] }
    }
    fn next(&mut self, w: f32) -> f32 {
        self.b[0] = 0.99765 * self.b[0] + w * 0.0990460;
        self.b[1] = 0.96300 * self.b[1] + w * 0.2965164;
        self.b[2] = 0.57000 * self.b[2] + w * 1.0526913;
        (self.b[0] + self.b[1] + self.b[2] + w * 0.1848) * 0.25
    }
}

#[derive(Clone, Copy, Default)]
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
    fn new(kind: char, f: f32, q: f32) -> Biquad {
        let mut b = Biquad::default();
        b.set(kind, f, q);
        b
    }
    fn set(&mut self, kind: char, f: f32, q: f32) {
        let w = 2.0 * PI * f.clamp(10.0, RATE * 0.45) / RATE;
        let (s, c) = w.sin_cos();
        let alpha = s / (2.0 * q);
        let (b0, b1, b2) = match kind {
            'l' => ((1.0 - c) / 2.0, 1.0 - c, (1.0 - c) / 2.0),
            'h' => ((1.0 + c) / 2.0, -(1.0 + c), (1.0 + c) / 2.0),
            _ => (alpha, 0.0, -alpha),
        };
        let a0 = 1.0 + alpha;
        self.b0 = b0 / a0;
        self.b1 = b1 / a0;
        self.b2 = b2 / a0;
        self.a1 = -2.0 * c / a0;
        self.a2 = (1.0 - alpha) / a0;
    }
    fn run(&mut self, x: f32) -> f32 {
        let y = self.b0 * x + self.z1;
        self.z1 = self.b1 * x - self.a1 * y + self.z2;
        self.z2 = self.b2 * x - self.a2 * y;
        y
    }
}

/// A slowly wandering value in 0..1, for gusts and swells.
struct Drift {
    v: f32,
    target: f32,
    left: usize,
    every: (f32, f32),
}

impl Drift {
    fn new(every: (f32, f32)) -> Drift {
        Drift { v: 0.5, target: 0.5, left: 0, every }
    }
    fn next(&mut self, r: &mut Rng) -> f32 {
        if self.left == 0 {
            self.target = r.unit();
            self.left = ((self.every.0 + (self.every.1 - self.every.0) * r.unit()) * RATE) as usize;
        }
        self.left -= 1;
        self.v += (self.target - self.v) / (RATE * 0.8);
        self.v
    }
}

fn rms_to(x: &mut [f32], db: f32) {
    let rms = (x.iter().map(|v| v * v).sum::<f32>() / x.len().max(1) as f32).sqrt();
    if rms > 0.0 {
        let k = 10f32.powf(db / 20.0) / rms;
        x.iter_mut().for_each(|v| *v *= k);
    }
}

fn peak_to(x: &mut [f32], db: f32) {
    let p = x.iter().fold(0f32, |a, v| a.max(v.abs()));
    if p > 0.0 {
        let k = 10f32.powf(db / 20.0) / p;
        x.iter_mut().for_each(|v| *v *= k);
    }
}

/// A short struck sound: inharmonic partials (freq ratio, level, decay seconds) from `f`.
fn strike(out: &mut [f32], at: usize, f: f32, partials: &[(f32, f32, f32)], level: f32) {
    for (ratio, amp, decay) in partials {
        let w = 2.0 * PI * f * ratio / RATE;
        let n = ((decay * 6.0) * RATE) as usize;
        for i in 0..n.min(out.len().saturating_sub(at)) {
            let t = i as f32 / RATE;
            // 2 ms attack, so it isn't a click.
            let a = (t / 0.002).min(1.0) * (-t / decay).exp();
            out[at + i] += level * amp * a * (w * i as f32).sin();
        }
    }
}

/// A thump of filtered noise: a knock, a step.
fn thump(out: &mut [f32], at: usize, r: &mut Rng, low: f32, body: f32, decay: f32, level: f32) {
    let mut bp = Biquad::new('b', low, 1.2);
    let mut lp = Biquad::new('l', low * 3.0, 0.7);
    let n = (decay * 6.0 * RATE) as usize;
    for i in 0..n.min(out.len().saturating_sub(at)) {
        let t = i as f32 / RATE;
        let e = (t / 0.001).min(1.0) * (-t / decay).exp();
        let s = bp.run(r.next()) * body + lp.run(r.next()) * (1.0 - body);
        out[at + i] += level * e * s;
    }
}

/// A plucked string (Karplus–Strong): a burst of noise through a delay line that loses a
/// little each time round, so it rings and fades like a zither.
fn pluck(out: &mut [f32], at: usize, r: &mut Rng, f: f32, ring: f32, level: f32) {
    let period = (RATE / f).round().max(2.0) as usize;
    let mut line: Vec<f32> = (0..period).map(|_| r.next()).collect();
    // How much is kept each time round, for the sound to fall 60 dB in `ring` seconds.
    let keep = 10f32.powf(-3.0 / (ring * f));
    let n = ((ring * 1.2) * RATE) as usize;
    let mut prev = 0.0f32;
    for i in 0..n.min(out.len().saturating_sub(at)) {
        let k = i % period;
        let y = line[k];
        line[k] = keep * 0.5 * (y + prev);
        prev = y;
        // 3 ms attack, so it isn't a click.
        out[at + i] += level * y * (i as f32 / (0.003 * RATE)).min(1.0);
    }
}

/// A chirp: a short sine sweep, for birds and insects.
fn chirp(out: &mut [f32], at: usize, f0: f32, f1: f32, len: f32, level: f32) {
    let n = (len * RATE) as usize;
    let mut ph = 0f32;
    for i in 0..n.min(out.len().saturating_sub(at)) {
        let x = i as f32 / n as f32;
        let f = f0 + (f1 - f0) * x;
        ph += 2.0 * PI * f / RATE;
        let e = (PI * x).sin().powi(2);
        out[at + i] += level * e * ph.sin();
    }
}

// MARK: Room tones

/// A room tone, stereo interleaved, its end crossfaded into its start so it loops unheard.
fn bed(name: &str) -> Vec<f32> {
    let n = ((BED_SECONDS + SEAM) * RATE) as usize;
    let mut l = vec![0f32; n];
    let mut r = vec![0f32; n];
    let seed = name.bytes().fold(7u64, |a, b| a.wrapping_mul(131).wrapping_add(b as u64));
    for (ch, buf) in [&mut l, &mut r].into_iter().enumerate() {
        let mut rng = Rng::new(seed + ch as u64 * 977);
        make_bed(name, buf, &mut rng, ch);
    }
    let db = match name {
        "room" | "room_night" => -32.0,
        "snow" => -30.0,
        "air" | "night_out" => -24.0,
        _ => -20.0,
    };
    // One level for both sides.
    let mut both: Vec<f32> = l.iter().zip(&r).flat_map(|(a, b)| [*a, *b]).collect();
    rms_to(&mut both, db);
    // The seam: the last SEAM seconds fade into the first.
    let body = (BED_SECONDS * RATE) as usize;
    let seam = (SEAM * RATE) as usize;
    for i in 0..seam {
        let k = i as f32 / seam as f32;
        for c in 0..2 {
            let a = both[2 * i + c];
            let b = both[2 * (body + i) + c];
            both[2 * i + c] = a * k.sqrt() + b * (1.0 - k).sqrt();
        }
    }
    both.truncate(body * 2);
    both
}

fn make_bed(name: &str, buf: &mut [f32], rng: &mut Rng, ch: usize) {
    let n = buf.len();
    let mut pink = Pink::new();
    match name {
        "room" | "room_night" => {
            // Air in a room: low, dark, steady.
            let mut lp = Biquad::new('l', 260.0, 0.7);
            let mut hp = Biquad::new('h', 40.0, 0.7);
            for x in buf.iter_mut() {
                *x = hp.run(lp.run(pink.next(rng.next())));
            }
            if name == "room_night" {
                // A clock somewhere in the house.
                let mut sub = vec![0f32; n];
                let mut i = (0.3 * RATE) as usize;
                let mut tock = false;
                while i < n {
                    strike(&mut sub, i, if tock { 2100.0 } else { 2500.0 }, &[(1.0, 1.0, 0.004), (2.7, 0.4, 0.002)], 0.05);
                    tock = !tock;
                    i += RATE as usize;
                }
                rms_to(&mut sub, -38.0 + (ch as f32) * -3.0);
                for (x, s) in buf.iter_mut().zip(sub) {
                    *x += s * 0.02;
                }
            }
        }
        "rain_in" | "rain_out" => {
            // A wash of rain and the drops in it; through a roof, only the low part.
            let mut bp = Biquad::new('b', 2500.0, 0.5);
            let mut drops = vec![0f32; n];
            let per_second = if name == "rain_out" { 260.0 } else { 140.0 };
            let count = (per_second * n as f32 / RATE) as usize;
            for _ in 0..count {
                let at = (rng.unit() * n as f32) as usize;
                let f = 1800.0 + rng.unit() * 4500.0;
                strike(&mut drops, at, f, &[(1.0, 1.0, 0.006)], 0.05 + rng.unit() * 0.1);
            }
            let mut lp = Biquad::new('l', if name == "rain_in" { 1400.0 } else { 9000.0 }, 0.7);
            for (x, d) in buf.iter_mut().zip(drops) {
                *x = lp.run(bp.run(pink.next(rng.next())) * 0.9 + d);
            }
        }
        "air" => {
            // Open air: a breath of wind that comes and goes, birds now and then.
            let mut lp = Biquad::new('l', 700.0, 0.7);
            let mut gust = Drift::new((2.0, 6.0));
            for x in buf.iter_mut() {
                *x = lp.run(pink.next(rng.next())) * (0.4 + 0.6 * gust.next(rng));
            }
            let mut birds = vec![0f32; n];
            let mut t = rng.unit() * 3.0;
            while t < n as f32 / RATE - 1.0 {
                let at = (t * RATE) as usize;
                let base = 2800.0 + rng.unit() * 2200.0;
                let notes = 2 + (rng.unit() * 4.0) as usize;
                for k in 0..notes {
                    chirp(&mut birds, at + k * (0.11 * RATE) as usize, base, base * (1.0 + rng.next() * 0.25), 0.07, 0.4);
                }
                t += 2.5 + rng.unit() * 6.0;
            }
            rms_to(&mut birds, -30.0);
            let mut sum: Vec<f32> = buf.to_vec();
            rms_to(&mut sum, -24.0);
            for (x, (s, b)) in buf.iter_mut().zip(sum.into_iter().zip(birds)) {
                *x = s + b * 0.6;
            }
        }
        "night_out" => {
            // Crickets: a few, each its own pitch and pulse, on a very quiet air.
            let mut lp = Biquad::new('l', 400.0, 0.7);
            for x in buf.iter_mut() {
                *x = lp.run(pink.next(rng.next())) * 0.3;
            }
            for c in 0..3 {
                let f = 4200.0 + rng.unit() * 900.0 + c as f32 * 150.0;
                let every = 0.6 + rng.unit() * 0.5;
                let mut t = rng.unit() * every;
                let level = 0.25 + rng.unit() * 0.2;
                while t < n as f32 / RATE - 0.3 {
                    for p in 0..3 {
                        chirp(buf, ((t + p as f32 * 0.035) * RATE) as usize, f, f, 0.022, level);
                    }
                    t += every * (0.9 + rng.unit() * 0.2);
                }
            }
        }
        "wind" | "snow" => {
            // Wind: noise through a band that moves with the gusts. Snow: the same, hushed.
            let mut bp = Biquad::new('b', 500.0, 0.8);
            let mut gust = Drift::new((1.5, 5.0));
            for (i, x) in buf.iter_mut().enumerate() {
                let g = gust.next(rng);
                if i % 64 == 0 {
                    bp.set('b', 250.0 + 700.0 * g, 0.8 + g);
                }
                *x = bp.run(pink.next(rng.next())) * (0.2 + 0.8 * g * g) * if name == "snow" { 0.4 } else { 1.0 };
            }
        }
        "river" => {
            // Running water: several bands of noise each rising and falling fast.
            let bands = [(450.0, 2.0), (900.0, 2.5), (1700.0, 3.0), (3200.0, 3.0)];
            let mut filters: Vec<Biquad> = bands.iter().map(|(f, q)| Biquad::new('b', *f, *q)).collect();
            let mut drifts: Vec<Drift> = bands.iter().map(|_| Drift::new((0.05, 0.25))).collect();
            for x in buf.iter_mut() {
                let w = rng.next();
                *x = filters.iter_mut().zip(drifts.iter_mut()).map(|(f, d)| f.run(w) * d.next(rng).powi(2)).sum::<f32>();
            }
        }
        _ => {
            // The sea: swells every several seconds; a dock has them smaller and nearer.
            let mut lp = Biquad::new('l', if name == "sea_dock" { 1800.0 } else { 1100.0 }, 0.7);
            let period = if name == "sea_dock" { 5.5 } else { 8.0 };
            let offset = ch as f32 * 0.7;
            for (i, x) in buf.iter_mut().enumerate() {
                let t = i as f32 / RATE + offset;
                let swell = ((2.0 * PI * t / period).sin() * 0.5 + 0.5).powf(1.8);
                *x = lp.run(pink.next(rng.next())) * (0.15 + 0.85 * swell);
            }
        }
    }
}

// MARK: Spot sounds

fn spot(name: &str, seconds: f32) -> Vec<f32> {
    let mut out = vec![0f32; (seconds * RATE) as usize];
    let mut rng = Rng::new(name.bytes().fold(11u64, |a, b| a.wrapping_mul(131).wrapping_add(b as u64)));
    let at = |s: f32| (s * RATE) as usize;
    match name {
        "knock" => {
            for (k, t) in [0.02, 0.22, 0.42].iter().enumerate() {
                thump(&mut out, at(*t), &mut rng, 180.0 + k as f32 * 10.0, 0.7, 0.025, 1.0);
                strike(&mut out, at(*t), 420.0, &[(1.0, 0.3, 0.02), (2.3, 0.15, 0.01)], 1.0);
            }
        }
        "knock_table" => {
            for t in [0.02, 0.2] {
                thump(&mut out, at(t), &mut rng, 260.0, 0.6, 0.02, 1.0);
            }
        }
        "steps" | "steps_stone" | "steps_run" => {
            let (gap, low, decay) = match name {
                "steps_run" => (0.28, 140.0, 0.02),
                "steps_stone" => (0.55, 320.0, 0.012),
                _ => (0.55, 160.0, 0.03),
            };
            let mut t = 0.05;
            while t < seconds - 0.2 {
                let (f, level) = (low * (0.9 + rng.unit() * 0.2), 0.6 + rng.unit() * 0.4);
                thump(&mut out, at(t), &mut rng, f, 0.5, decay, level);
                t += gap * (0.92 + rng.unit() * 0.16);
            }
        }
        "thunder" => {
            // A crack, then the roll: low noise in waves that die away.
            let mut lp = Biquad::new('l', 160.0, 0.8);
            let mut crack = Biquad::new('b', 1200.0, 0.7);
            let mut roll = Drift::new((0.15, 0.5));
            for (i, x) in out.iter_mut().enumerate() {
                let t = i as f32 / RATE;
                let w = rng.next();
                let body = lp.run(w) * 6.0 * (t / 0.3).min(1.0) * (-(t - 0.3).max(0.0) / 1.6).exp() * (0.4 + roll.next(&mut rng));
                let c = crack.run(w) * (-t / 0.08).exp() * 0.5;
                *x = body + c;
            }
        }
        "bell_temple" => strike(&mut out, 0, 110.0, &[(1.0, 1.0, 2.2), (2.0, 0.6, 1.6), (2.76, 0.5, 1.2), (5.4, 0.25, 0.6), (8.9, 0.1, 0.3)], 0.4),
        "bell_small" => strike(&mut out, 0, 880.0, &[(1.0, 1.0, 0.5), (2.76, 0.5, 0.3), (5.4, 0.25, 0.15)], 0.4),
        "gong" => {
            strike(&mut out, 0, 140.0, &[(1.0, 1.0, 1.8), (1.48, 0.7, 1.4), (2.03, 0.6, 1.1), (2.6, 0.4, 0.9), (3.4, 0.3, 0.6)], 0.4);
            thump(&mut out, 0, &mut rng, 200.0, 0.4, 0.05, 0.4);
        }
        "chime" => {
            let mut t = 0.0;
            while t < seconds - 1.2 {
                let f = [1320.0, 1480.0, 1760.0, 1980.0][(rng.unit() * 4.0) as usize % 4];
                strike(&mut out, at(t), f, &[(1.0, 1.0, 0.5), (2.76, 0.3, 0.2)], 0.2);
                t += 0.15 + rng.unit() * 0.4;
            }
        }
        "clink" => {
            strike(&mut out, 0, 2400.0, &[(1.0, 1.0, 0.15), (2.4, 0.5, 0.08), (3.9, 0.3, 0.05)], 0.5);
            strike(&mut out, at(0.12), 2650.0, &[(1.0, 0.6, 0.12), (2.4, 0.3, 0.06)], 0.5);
        }
        "cup" => {
            thump(&mut out, 0, &mut rng, 600.0, 0.5, 0.01, 0.6);
            strike(&mut out, 0, 1900.0, &[(1.0, 0.6, 0.06), (2.6, 0.3, 0.03)], 0.5);
        }
        "tick" => {
            for k in 0..4 {
                strike(&mut out, at(0.05 + k as f32), if k % 2 == 0 { 2500.0 } else { 2100.0 }, &[(1.0, 1.0, 0.004), (2.7, 0.4, 0.002)], 0.8);
            }
        }
        "strike" => {
            for k in 0..3 {
                strike(&mut out, at(0.05 + k as f32 * 1.6), 523.0, &[(1.0, 1.0, 1.0), (2.0, 0.4, 0.6), (3.0, 0.2, 0.3)], 0.35);
            }
        }
        "drum" => {
            // A skin: a low tone falling in pitch, and the hit's noise.
            let n = out.len();
            let mut ph = 0f32;
            for i in 0..n {
                let t = i as f32 / RATE;
                let f = 70.0 + 60.0 * (-t / 0.05).exp();
                ph += 2.0 * PI * f / RATE;
                out[i] += ph.sin() * (-t / 0.35).exp();
            }
            thump(&mut out, 0, &mut rng, 300.0, 0.5, 0.02, 0.5);
        }
        "pluck_phrase" => {
            // A falling line on the pentatonic scale (A, F♯, E, D), the last note left to ring.
            for (t, f, ring) in [(0.0, 440.0, 1.6), (0.45, 369.99, 1.6), (0.9, 329.63, 1.8), (1.5, 293.66, 2.8)] {
                pluck(&mut out, at(t), &mut rng, f, ring, 0.6);
                // The octave below, softly, for body.
                pluck(&mut out, at(t), &mut rng, f / 2.0, ring * 0.8, 0.25);
            }
        }
        "bell_phrase" => {
            for (t, f) in [(0.0, 880.0), (0.35, 659.25), (0.7, 587.33), (1.2, 440.0)] {
                strike(&mut out, at(t), f, &[(1.0, 1.0, 0.9), (2.76, 0.35, 0.35), (5.4, 0.12, 0.15)], 0.35);
            }
        }
        "drip" => {
            for t in [0.1, 0.9, 1.6] {
                chirp(&mut out, at(t), 900.0, 1700.0, 0.03, 0.8);
            }
        }
        _ => {
            // A cricket close by.
            let mut t = 0.05;
            while t < seconds - 0.2 {
                for p in 0..3 {
                    chirp(&mut out, at(t + p as f32 * 0.035), 4600.0, 4600.0, 0.022, 0.8);
                }
                t += 0.5;
            }
        }
    }
    peak_to(&mut out, -3.0);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn every_sound_is_made_and_in_level() {
        for a in assets() {
            let (s, stereo) = samples(&a.id).unwrap();
            assert_eq!(stereo, a.loops, "{}", a.id);
            let frames = s.len() / if stereo { 2 } else { 1 };
            assert!((frames as f32 / RATE - a.seconds).abs() < 0.01, "{} is {} s", a.id, frames as f32 / RATE);
            assert!(s.iter().all(|x| x.is_finite() && x.abs() <= 1.0), "{} out of range", a.id);
            let rms = (s.iter().map(|x| x * x).sum::<f32>() / s.len() as f32).sqrt();
            assert!(rms > 1e-4, "{} is silent", a.id);
        }
    }

    #[test]
    fn the_same_every_time() {
        let a = bed("rain_out");
        let b = bed("rain_out");
        assert_eq!(a, b);
    }

    #[test]
    fn the_loop_has_no_jump() {
        // The last frame and the first are close: the seam was crossfaded.
        let s = bed("wind");
        let n = s.len();
        let jump = (s[0] - s[n - 2]).abs();
        let typical = s.windows(4).take(48_000).map(|w| (w[2] - w[0]).abs()).sum::<f32>() / 48_000.0;
        assert!(jump < typical * 8.0 + 0.01, "jump {jump}, typical step {typical}");
    }
}
