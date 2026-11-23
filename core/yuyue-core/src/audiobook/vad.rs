//! 人声检测 from loudness: the app measures each short frame's level (dB) and the core says
//! where speech is. A reader's voice sits well above the room; the floor and the voice level are
//! taken from this stretch itself, so a quiet recording and a loud one come out the same.
//! Music isn't told apart here: a long stretch the transcriber heard no words in is music
//! (`music_of`).

use super::api::TimeSpan;

/// Speech shorter than this is a click or a breath.
pub const MIN_SPEECH: f64 = 0.25;
/// Quiet shorter than this inside speech is between two sounds of one word.
pub const MIN_GAP: f64 = 0.15;
/// Speech with no words heard in it, this long or more, is music.
pub const MIN_MUSIC: f64 = 5.0;

/// Speech in a stretch from `from` seconds, given each frame's level in dB (`frame` seconds a
/// frame).
pub fn speech(levels: &[f32], frame: f64, from: f64) -> Vec<TimeSpan> {
    if levels.is_empty() || frame <= 0.0 {
        return Vec::new();
    }
    let mut sorted: Vec<f32> = levels.iter().copied().filter(|x| x.is_finite()).collect();
    if sorted.is_empty() {
        return Vec::new();
    }
    sorted.sort_by(f32::total_cmp);
    let at = |q: f32| sorted[((sorted.len() - 1) as f32 * q) as usize];
    let floor = at(0.1);
    let voice = at(0.9);
    // All one level (silence, or a tone): nothing told apart.
    if voice - floor < 6.0 {
        return Vec::new();
    }
    // A third of the way up from the floor, and never within 6 dB of it.
    let on = (floor + (voice - floor) * 0.35).max(floor + 6.0);
    let off = on - 3.0;
    // With a little stickiness: once in speech, it takes a drop below `off` to leave.
    let mut runs: Vec<(usize, usize)> = Vec::new();
    let mut start: Option<usize> = None;
    for (i, &l) in levels.iter().enumerate() {
        let l = if l.is_finite() { l } else { floor };
        match start {
            None if l >= on => start = Some(i),
            Some(s) if l < off => {
                runs.push((s, i));
                start = None;
            }
            _ => {}
        }
    }
    if let Some(s) = start {
        runs.push((s, levels.len()));
    }
    // Gaps too short to be pauses closed, then runs too short to be speech dropped.
    let gap = (MIN_GAP / frame).ceil() as usize;
    let mut merged: Vec<(usize, usize)> = Vec::new();
    for r in runs {
        match merged.last_mut() {
            Some(m) if r.0 - m.1 < gap => m.1 = r.1,
            _ => merged.push(r),
        }
    }
    let least = (MIN_SPEECH / frame).ceil() as usize;
    merged
        .into_iter()
        .filter(|r| r.1 - r.0 >= least)
        .map(|(a, b)| TimeSpan { t0: from + a as f64 * frame, t1: from + b as f64 * frame })
        .collect()
}

/// Stretches of “speech” the transcriber heard nothing in, long enough to be music: `speech`
/// sorted by start, `words` the start of each word heard.
pub fn music_of(speech: &[(f64, f64)], words: &[f64]) -> Vec<(f64, f64)> {
    let mut w: Vec<f64> = words.to_vec();
    w.sort_by(f64::total_cmp);
    speech
        .iter()
        .filter(|(a, b)| b - a >= MIN_MUSIC)
        .filter(|(a, b)| {
            let i = w.partition_point(|t| *t < *a);
            w.get(i).is_none_or(|t| *t >= *b)
        })
        .copied()
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    /// Levels: quiet at −60, speech at −20, in the given (seconds, speaking) pieces.
    fn levels(pieces: &[(f64, bool)], frame: f64) -> Vec<f32> {
        pieces.iter().flat_map(|&(s, on)| std::iter::repeat_n(if on { -20.0 } else { -60.0 }, (s / frame).round() as usize)).collect()
    }

    #[test]
    fn finds_speech_and_keeps_pauses() {
        let f = 0.03;
        let l = levels(&[(1.0, false), (2.0, true), (0.6, false), (1.5, true), (0.06, false), (1.0, true), (1.0, false)], f);
        let s = speech(&l, f, 100.0);
        assert_eq!(s.len(), 2, "{s:?}");
        assert!((s[0].t0 - 101.0).abs() < 0.05 && (s[0].t1 - 103.0).abs() < 0.05, "{s:?}");
        // The 60 ms dip inside the second run is closed.
        assert!((s[1].t0 - 103.6).abs() < 0.05 && (s[1].t1 - 106.16).abs() < 0.05, "{s:?}");
    }

    #[test]
    fn drops_clicks_and_flat_sound() {
        let f = 0.03;
        let l = levels(&[(2.0, false), (0.09, true), (2.0, false), (1.0, true), (2.0, false)], f);
        assert_eq!(speech(&l, f, 0.0).len(), 1);
        assert!(speech(&vec![-30.0; 500], f, 0.0).is_empty());
        assert!(speech(&[], f, 0.0).is_empty());
    }

    #[test]
    fn music_is_long_speech_with_no_words() {
        let sp = [(0.0, 20.0), (21.0, 30.0), (31.0, 33.0)];
        let m = music_of(&sp, &[22.0, 25.0]);
        assert_eq!(m, vec![(0.0, 20.0)]);
    }
}
