//! 声景: the room tone of a scene and the few sounds the book writes. Little and right: the
//! listener should feel they're there, not that someone's playing effects. Only what the text
//! says happens makes a sound; no more than four a minute, at least three seconds apart, and
//! the ones far from talk kept first.

use serde::{Deserialize, Serialize};

use super::cast::bare;
use super::{DramaScene, SceneTime, SceneWeather};

/// Kinds of sound a scene can hold. Assets are found by these; anything else is `other` with a
/// description for a sound-making model.
pub const EVENT_KINDS: &[&str] = &[
    // doors and rooms
    "door_wood_open", "door_wood_close", "door_wood_knock", "door_slam", "door_creak", "curtain_draw", "window_open", "window_close", "gate_iron", "lock_key", "drawer", "chair_move", "bed_creak", "floor_creak",
    // steps
    "steps_wood", "steps_stone", "steps_grass", "steps_snow", "steps_running", "steps_many", "steps_stairs", "steps_heels", "steps_mud",
    // horses and carriages
    "horse_trot", "horse_gallop", "horse_neigh", "carriage_wheels", "cart", "sedan_chair", "boat_oars", "ship_creak", "bell_horse",
    // water and weather
    "rain_light", "rain_heavy", "thunder", "wind_light", "wind_strong", "stream", "waves", "fountain", "drip", "splash", "ice_crack",
    // fire and light
    "fire_crackle", "candle_blow", "lantern", "fireworks", "firecrackers",
    // objects
    "cup_set", "cup_break", "porcelain_break", "glass_break", "pour_tea", "pour_wine", "chopsticks", "bowl_set", "plate_set", "paper_rustle", "book_page", "brush_write", "scissors", "needle_cloth",
    "fan_open", "fan_flutter", "jade_clink", "coins", "bag_drop", "box_open", "box_close", "knock_table", "slap", "cloth_rustle", "silk_rustle", "mirror_set", "comb", "broom",
    // weapons and fights
    "sword_draw", "sword_clash", "sword_sheath", "arrow", "bow_release", "punch", "fall_body", "armor_move", "drum_war", "gong", "horn",
    // bells and music
    "bell_temple", "bell_small", "wind_chime", "clock_tick", "clock_strike", "guqin", "flute", "pipa", "drum", "applause", "music_far",
    // animals
    "dog_bark", "cat_meow", "rooster", "bird_song", "crow", "cicada", "cricket", "frog", "wolf_howl", "chicken", "cow", "goose", "insects_night",
    // people
    "crowd_murmur", "crowd_laugh", "crowd_gasp", "baby_cry", "cough", "sneeze", "laugh_far", "cry_far", "shout_far", "kiss",
    // modern
    "phone_ring", "phone_vibrate", "car_pass", "car_door", "car_horn", "train", "subway", "elevator", "keyboard", "doorbell", "tv", "camera", "typewriter", "engine_start", "plane",
    "other",
];

/// A sound the text writes, placed at its words.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SoundEvent {
    pub sentence: u32,
    /// After which character of the sentence it sounds.
    pub at_char: u32,
    pub kind: String,
    /// For `other`: what to make.
    pub what: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelEvent {
    pub sentence: u32,
    pub after_text: String,
    pub kind: String,
    #[serde(default)]
    pub what: Option<String>,
    pub evidence: String,
}

/// A model's sounds for a scene, checked: its quote must be in the sentence, its place in the
/// sentence found, its kind one we know.
pub fn checked_events(events: &[ModelEvent], sentences: &[String], first: u32, last: u32) -> Vec<SoundEvent> {
    let mut out = Vec::new();
    for e in events {
        if e.sentence < first || e.sentence > last {
            continue;
        }
        let Some(s) = sentences.get(e.sentence as usize) else { continue };
        if !bare(s).contains(&bare(&e.evidence)) || bare(&e.evidence).is_empty() {
            continue;
        }
        let Some(i) = s.find(e.after_text.as_str()).filter(|_| !e.after_text.is_empty()) else { continue };
        let at_char = s[..i + e.after_text.len()].chars().count() as u32;
        let kind = if EVENT_KINDS.contains(&e.kind.as_str()) { e.kind.clone() } else { "other".into() };
        let what = e.what.clone().unwrap_or_default();
        if kind == "other" && what.trim().is_empty() {
            continue;
        }
        out.push(SoundEvent { sentence: e.sentence, at_char, kind, what });
    }
    out
}

/// Keeps at most four sounds a minute and three seconds apart. `when` estimates each sound's
/// time (seconds) and `talk_near` how close it is to speech; the ones far from talk win.
pub fn thin(mut events: Vec<SoundEvent>, when: impl Fn(&SoundEvent) -> f64, talk_near: impl Fn(&SoundEvent) -> f64) -> Vec<SoundEvent> {
    events.sort_by(|a, b| talk_near(b).partial_cmp(&talk_near(a)).unwrap_or(std::cmp::Ordering::Equal));
    let mut kept: Vec<(f64, SoundEvent)> = Vec::new();
    for e in events {
        let t = when(&e);
        if kept.iter().any(|(k, _)| (k - t).abs() < 3.0) {
            continue;
        }
        if kept.iter().filter(|(k, _)| (k - t).abs() < 60.0).count() >= 4 {
            continue;
        }
        kept.push((t, e));
    }
    kept.sort_by(|a, b| a.0.partial_cmp(&b.0).unwrap_or(std::cmp::Ordering::Equal));
    kept.into_iter().map(|(_, e)| e).collect()
}

/// Places that are outside, whatever the model said of `indoor`.
const OUTSIDE: &[&str] = &["庭院", "街市", "村庄", "田野", "山林", "竹林", "河边", "海边", "荒漠", "雪地", "城墙", "战场", "码头", "桥上", "墓地", "夜路", "城市街道"];

/// The room tone a scene asks for: asset keys, most specific first — `place_kind/time/weather`,
/// then the place, then inside or outside with the weather or the time. A dream or a memory has
/// none: it isn't anywhere.
pub fn bed_keys(scene: &DramaScene) -> Vec<String> {
    let p = scene.place_kind.as_str();
    if p == "梦境" || p == "回忆" {
        return Vec::new();
    }
    let t = match scene.time {
        SceneTime::Night | SceneTime::Dusk => "night",
        _ => "day",
    };
    let w = match scene.weather {
        SceneWeather::Rain | SceneWeather::Storm => "rain",
        SceneWeather::Snow => "snow",
        SceneWeather::Wind => "wind",
        _ => "clear",
    };
    let side = if OUTSIDE.contains(&p) || scene.indoor == Some(false) { "outdoor" } else { "indoor" };
    let mut keys = vec![format!("{p}/{t}/{w}"), format!("{p}/{t}"), p.to_string()];
    if w != "clear" {
        keys.push(format!("{side}/{w}"));
    }
    keys.push(format!("{side}/{t}"));
    keys.push(side.to_string());
    keys
}

/// One sound in the app's library.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct SoundAsset {
    pub id: String,
    /// `居室/night/rain` for a bed; an event kind for a spot.
    pub key: String,
    pub seconds: f32,
    pub loops: bool,
    pub license: String,
}

/// The asset for a bed, by the most specific key the library has.
pub fn pick_bed<'a>(scene: &DramaScene, library: &'a [SoundAsset]) -> Option<&'a SoundAsset> {
    bed_keys(scene).iter().find_map(|k| library.iter().find(|a| a.loops && &a.key == k))
}

/// What a sound is looked for by: its kind, or for `other` what it is.
pub fn event_key(e: &SoundEvent) -> String {
    if e.kind == "other" { format!("other:{}", e.what.trim()) } else { e.kind.clone() }
}

/// 生成音效: what to ask a sound-making model for.
pub fn describe(e: &SoundEvent) -> String {
    if e.kind == "other" {
        return e.what.trim().to_string();
    }
    let words = e.kind.replace('_', " ");
    format!("{words}, a single clear sound effect, close and dry, no music, no voices")
}

/// About how long a kind of sound lasts.
pub fn seconds_for(kind: &str) -> f32 {
    match kind {
        k if k.starts_with("steps") || k.starts_with("horse") || k.starts_with("rain") || k.starts_with("wind") || k.contains("crowd") || k.contains("music") => 4.0,
        "thunder" | "bell_temple" | "gong" | "fireworks" | "firecrackers" | "train" | "plane" | "clock_strike" => 5.0,
        _ => 2.0,
    }
}

/// The asset for a spot sound; variants are taken in turn by `n` so the same door doesn't
/// sound the same twice in a row.
pub fn pick_spot<'a>(kind: &str, n: usize, library: &'a [SoundAsset]) -> Option<&'a SoundAsset> {
    let v: Vec<&SoundAsset> = library.iter().filter(|a| !a.loops && a.key == kind).collect();
    (!v.is_empty()).then(|| v[n % v.len()])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_sounds_the_book_writes() {
        let ss: Vec<String> = vec!["只见帘子一掀，推门进来一个人。".into()];
        let ev = vec![
            ModelEvent { sentence: 0, after_text: "推门进来".into(), kind: "door_wood_open".into(), what: None, evidence: "帘子一掀，推门进来".into() },
            ModelEvent { sentence: 0, after_text: "一个人".into(), kind: "steps_wood".into(), what: None, evidence: "他快步走来".into() },
            ModelEvent { sentence: 0, after_text: "一掀".into(), kind: "made_up".into(), what: None, evidence: "帘子一掀".into() },
        ];
        let got = checked_events(&ev, &ss, 0, 0);
        assert_eq!(got.len(), 1, "made-up quote and unknown kind without description are dropped");
        assert_eq!(got[0].at_char, 11);
    }

    #[test]
    fn four_a_minute_three_seconds_apart() {
        let ev: Vec<SoundEvent> = (0..10).map(|i| SoundEvent { sentence: i, at_char: 0, kind: "dog_bark".into(), what: String::new() }).collect();
        let kept = thin(ev, |e| e.sentence as f64 * 2.0, |_| 1.0);
        assert!(kept.len() <= 4);
        for w in kept.windows(2) {
            assert!((w[1].sentence as f64 - w[0].sentence as f64) * 2.0 >= 3.0);
        }
    }
}
