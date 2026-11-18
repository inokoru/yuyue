//! 声音素材库: room tones and spot sounds the app downloads once and keeps beside the library
//! (`sounds-1/`), richer than the built-in ones made here from noise. A folder of Ogg Vorbis
//! files and `library.json` listing each with its key, length, whether it loops, and where it
//! came from and under what licence. Looked at before the built-in sounds; without it,
//! everything works as before.
//!
//! The app says where the folder is once (`drama_set_sound_library`); the core reads the list,
//! lays scenes out with it, and decodes a sound the first time the mixer needs it.

use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, OnceLock};

use serde::Deserialize;

use super::sound::SoundAsset;

pub const PREFIX: &str = "lib:";
const RATE: u32 = yuyue_mix::RATE;
/// Decoded sounds kept at once (a 30-second stereo bed is about 11 MB).
const KEEP: usize = 24;

#[derive(Clone, Debug, Deserialize)]
struct Entry {
    id: String,
    key: String,
    file: String,
    seconds: f32,
    #[serde(default)]
    loops: bool,
    license: String,
}

#[derive(Default)]
struct Library {
    dir: PathBuf,
    entries: Vec<Entry>,
    decoded: Vec<(String, Arc<Vec<f32>>, bool)>,
}

fn library() -> &'static Mutex<Library> {
    static L: OnceLock<Mutex<Library>> = OnceLock::new();
    L.get_or_init(|| Mutex::new(Library::default()))
}

/// Takes the library in `dir` (its `library.json`); a folder without one clears it. How many
/// sounds it has.
pub fn set(dir: &Path) -> usize {
    if let Ok(l) = library().lock()
        && l.dir == dir
        && !l.entries.is_empty()
    {
        return l.entries.len();
    }
    let entries: Vec<Entry> = std::fs::read(dir.join("library.json"))
        .ok()
        .and_then(|b| serde_json::from_slice(&b).ok())
        .unwrap_or_default();
    let entries: Vec<Entry> = entries.into_iter().filter(|e| !e.file.contains("..") && !e.file.starts_with('/')).collect();
    let n = entries.len();
    if let Ok(mut l) = library().lock() {
        *l = Library { dir: dir.to_path_buf(), entries, decoded: Vec::new() };
    }
    n
}

/// The library's sounds, as entries to lay scenes out with.
pub fn assets() -> Vec<SoundAsset> {
    let Ok(l) = library().lock() else { return Vec::new() };
    l.entries.iter().map(|e| SoundAsset { id: format!("{PREFIX}{}", e.id), key: e.key.clone(), seconds: e.seconds, loops: e.loops, license: e.license.clone() }).collect()
}

/// A library sound's samples at the mixer's rate (stereo interleaved when the file is stereo),
/// decoded the first time and kept while it's in use.
pub fn samples(id: &str) -> Option<(Arc<Vec<f32>>, bool)> {
    let name = id.strip_prefix(PREFIX)?;
    let path = {
        let mut l = library().lock().ok()?;
        if let Some(i) = l.decoded.iter().position(|d| d.0 == name) {
            // Most recently used last.
            let d = l.decoded.remove(i);
            let out = (d.1.clone(), d.2);
            l.decoded.push(d);
            return Some(out);
        }
        let e = l.entries.iter().find(|e| e.id == name)?;
        l.dir.join(&e.file)
    };
    let (samples, stereo) = decode(&std::fs::read(path).ok()?)?;
    let samples = Arc::new(samples);
    let mut l = library().lock().ok()?;
    if l.decoded.len() >= KEEP {
        l.decoded.remove(0);
    }
    l.decoded.push((name.to_string(), samples.clone(), stereo));
    Some((samples, stereo))
}

/// An Ogg Vorbis file's samples at the mixer's rate: mono, or stereo interleaved (more
/// channels are folded to two).
pub fn decode(bytes: &[u8]) -> Option<(Vec<f32>, bool)> {
    use lewton::inside_ogg::OggStreamReader;
    let mut r = OggStreamReader::new(std::io::Cursor::new(bytes)).ok()?;
    let channels = r.ident_hdr.audio_channels as usize;
    let rate = r.ident_hdr.audio_sample_rate;
    if channels == 0 {
        return None;
    }
    let stereo = channels > 1;
    let mut out: Vec<f32> = Vec::new();
    while let Ok(Some(packet)) = r.read_dec_packet_generic::<Vec<Vec<f32>>>() {
        match packet.as_slice() {
            [l, r, ..] => out.extend(l.iter().zip(r).flat_map(|(a, b)| [*a, *b])),
            [m] => out.extend_from_slice(m),
            [] => {}
        }
    }
    // The last packet runs past the end; the stream says where the sound stops.
    if let Some(end) = r.get_last_absgp() {
        out.truncate(end as usize * channels.min(2));
    }
    if rate != RATE {
        out = resample(&out, if stereo { 2 } else { 1 }, rate);
    }
    Some((out, stereo))
}

/// Linear resampling to the mixer's rate, per channel (the library is made at 48 kHz; this is
/// for a file that isn't).
fn resample(x: &[f32], ch: usize, from: u32) -> Vec<f32> {
    let frames = x.len() / ch;
    if frames == 0 {
        return Vec::new();
    }
    let step = from as f64 / RATE as f64;
    let n = (frames as f64 / step).floor() as usize;
    let mut out = Vec::with_capacity(n * ch);
    for i in 0..n {
        let p = i as f64 * step;
        let k = p.floor() as usize;
        let f = (p - k as f64) as f32;
        for c in 0..ch {
            let a = x[k.min(frames - 1) * ch + c];
            let b = x[(k + 1).min(frames - 1) * ch + c];
            out.push(a + (b - a) * f);
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    /// One test, as the library is the process's: none, then one with two sounds.
    #[test]
    fn a_library_is_listed_and_read() {
        let dir = std::env::temp_dir().join(format!("yuyue-lib-{}", getrandom::u64().unwrap()));
        std::fs::create_dir_all(&dir).unwrap();
        assert_eq!(set(&dir), 0);
        assert!(assets().is_empty());
        assert!(samples("lib:room").is_none());

        let root = env!("CARGO_MANIFEST_DIR");
        std::fs::copy(format!("{root}/../../fixtures/sounds/tone-44k-stereo.ogg"), dir.join("room.ogg")).unwrap();
        std::fs::copy(format!("{root}/../../fixtures/sounds/tone-48k-mono.ogg"), dir.join("knock.ogg")).unwrap();
        let list = r#"[
            {"id": "room", "key": "居室/night", "file": "room.ogg", "seconds": 1.0, "loops": true, "license": "CC0"},
            {"id": "knock", "key": "door_wood_knock", "file": "knock.ogg", "seconds": 1.0, "license": "CC0"},
            {"id": "sneaky", "key": "gong", "file": "../../etc/passwd", "seconds": 1.0, "license": "CC0"}
        ]"#;
        std::fs::write(dir.join("library.json"), list).unwrap();
        assert_eq!(set(&dir), 2, "a file outside the folder isn't taken");
        let a = assets();
        assert!(a.iter().any(|x| x.id == "lib:room" && x.loops && x.key == "居室/night"));
        let (room, stereo) = samples("lib:room").unwrap();
        assert!(stereo && room.len() > 90_000);
        let (knock, stereo) = samples("lib:knock").unwrap();
        assert!(!stereo && knock.len() > 47_000);
        // Kept: the second time is the same samples.
        assert!(Arc::ptr_eq(&samples("lib:knock").unwrap().0, &knock));
        set(&dir.join("gone"));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn ogg_files_come_out_at_the_mixers_rate() {
        let root = env!("CARGO_MANIFEST_DIR");
        let (m, st) = decode(&std::fs::read(format!("{root}/../../fixtures/sounds/tone-48k-mono.ogg")).unwrap()).unwrap();
        assert!(!st);
        assert!((m.len() as i64 - 48_000).abs() < 100, "{}", m.len());
        // 440 Hz: about 880 zero crossings a second.
        let crossings = m.windows(2).filter(|w| (w[0] < 0.0) != (w[1] < 0.0)).count();
        assert!((860..=900).contains(&crossings), "{crossings}");
        let (s, st) = decode(&std::fs::read(format!("{root}/../../fixtures/sounds/tone-44k-stereo.ogg")).unwrap()).unwrap();
        assert!(st);
        assert!((s.len() as i64 / 2 - 48_000).abs() < 150, "{}", s.len());
        // The right channel was silent.
        let right: f32 = s.iter().skip(1).step_by(2).map(|v| v.abs()).fold(0.0, f32::max);
        assert!(right < 0.01);
    }

    #[test]
    fn resampling_keeps_length_in_time() {
        let x: Vec<f32> = (0..44_100).map(|i| (i as f32 / 100.0).sin()).collect();
        let y = resample(&x, 1, 44_100);
        assert!((y.len() as i64 - 48_000).abs() <= 1);
    }
}
