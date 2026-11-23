//! 定线: how sure a line is, as how often lines like it turn out wrong. Only what clears the line
//! gets a voice; the rest is read in the 对话 voice and goes on the list.
//!
//! For a model combination measured on the test set (第十节), a small logistic regression over
//! the features below gives `p_correct`, and the threshold is where the held-out set's error
//! stays under 0.1%. For one never measured (the user's own model), a conservative rule: a
//! written line both layers agree on; an inferred one both agree on, with a settled anchor and a
//! clear winner; never a guess from content alone.

use serde::{Deserialize, Serialize};

use super::ask::{Cand, Checked, Vote};
use super::solve::LineOut;
use crate::drama::LineBasis;

/// What `p_correct` is computed from.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize)]
pub struct Features {
    pub basis: u8,
    pub agree: bool,
    pub post_top: f32,
    pub margin: f32,
    /// The share of rule-settled lines in the scene the model also got right.
    pub concord: f32,
    pub present: u32,
    pub from_anchor: u32,
}

/// Coefficients for one model combination.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct Fit {
    pub combo: String,
    pub bias: f32,
    pub w: [f32; 8],
    pub threshold: f32,
}

/// Fits shipped with the app, one per measured combination.
pub fn fits() -> Vec<Fit> {
    serde_json::from_str(include_str!("../../../data/drama-calibration.json")).unwrap_or_default()
}

fn x(f: &Features) -> [f32; 8] {
    [
        (f.basis == 0) as u8 as f32,
        (f.basis == 1) as u8 as f32,
        (f.basis == 2) as u8 as f32,
        f.agree as u8 as f32,
        f.post_top,
        f.margin,
        f.concord,
        (f.from_anchor as f32).min(10.0) / 10.0 - (f.present as f32).min(12.0) / 12.0,
    ]
}

pub fn p_correct(fit: &Fit, f: &Features) -> f32 {
    let v = x(f);
    let z = fit.bias + v.iter().zip(fit.w.iter()).map(|(a, b)| a * b).sum::<f32>();
    1.0 / (1.0 + (-z).exp())
}

fn basis_code(b: LineBasis) -> u8 {
    match b {
        LineBasis::Explicit => 0,
        LineBasis::Inferred => 1,
        LineBasis::Content => 2,
        LineBasis::None => 3,
    }
}

pub fn features(out: &LineOut, first: Option<&Checked>, verify: Option<&Checked>, concord: f32, present: u32, from_anchor: u32) -> Features {
    let top = out.post.first().map(|p| p.1).unwrap_or(0.0);
    let second = out.post.get(1).map(|p| p.1).unwrap_or(0.0);
    let best = out.post.first().map(|p| p.0);
    let agree = match (first, verify, best) {
        (Some(a), Some(v), Some(b)) => a.vote == Vote::Is(b) && v.vote == Vote::Is(b),
        (Some(a), None, Some(b)) => a.vote == Vote::Is(b) && a.basis == LineBasis::Explicit,
        _ => false,
    };
    Features { basis: first.map(|a| basis_code(a.basis)).unwrap_or(3), agree, post_top: top, margin: top - second, concord, present, from_anchor }
}

/// The decision for a line: who, and how sure; None when it doesn't clear the line.
pub fn decide(out: &LineOut, first: Option<&Checked>, verify: Option<&Checked>, f: &Features, fit: Option<&Fit>) -> Option<(Cand, f32)> {
    let (best, top) = *out.post.first()?;
    if let Some(fit) = fit {
        let p = p_correct(fit, f);
        return (p >= fit.threshold).then_some((best, p));
    }
    // A scene where the model got lines written plainly wrong isn't to be trusted.
    if f.concord < 0.9 {
        return None;
    }
    let a = first?;
    if a.vote != Vote::Is(best) {
        return None;
    }
    match a.basis {
        LineBasis::Explicit if f.agree || verify.is_none_or(|v| v.vote == Vote::Is(best)) => Some((best, 0.995_f32.min(top.max(0.99)))),
        LineBasis::Inferred if f.agree && a.anchor_ok && top >= 0.9 => Some((best, top.min(0.99))),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn line(c: Cand, p: f32) -> LineOut {
        LineOut { utterance: 0, post: vec![(c, p), (Cand::Group, 1.0 - p)] }
    }

    fn vote(c: Cand, basis: LineBasis) -> Checked {
        Checked { utterance: 0, vote: Vote::Is(c), basis, to: None, anchor_ok: true, excluded: vec![] }
    }

    #[test]
    fn content_alone_never_clears_the_line() {
        let o = line(Cand::Char(1), 0.99);
        let a = vote(Cand::Char(1), LineBasis::Content);
        let v = vote(Cand::Char(1), LineBasis::None);
        let f = features(&o, Some(&a), Some(&v), 1.0, 2, 1);
        assert_eq!(decide(&o, Some(&a), Some(&v), &f, None), None);
    }

    #[test]
    fn inferred_needs_both_layers_and_a_clear_winner() {
        let o = line(Cand::Char(1), 0.95);
        let a = vote(Cand::Char(1), LineBasis::Inferred);
        let v = vote(Cand::Char(1), LineBasis::None);
        let f = features(&o, Some(&a), Some(&v), 1.0, 2, 1);
        assert!(decide(&o, Some(&a), Some(&v), &f, None).is_some());
        let v2 = vote(Cand::Char(2), LineBasis::None);
        let f2 = features(&o, Some(&a), Some(&v2), 1.0, 2, 1);
        assert_eq!(decide(&o, Some(&a), Some(&v2), &f2, None), None);
        let weak = line(Cand::Char(1), 0.8);
        let f3 = features(&weak, Some(&a), Some(&v), 1.0, 2, 1);
        assert_eq!(decide(&weak, Some(&a), Some(&v), &f3, None), None);
    }

    #[test]
    fn a_model_wrong_on_written_lines_isnt_trusted() {
        let o = line(Cand::Char(1), 0.99);
        let a = vote(Cand::Char(1), LineBasis::Explicit);
        let f = features(&o, Some(&a), None, 0.5, 2, 0);
        assert_eq!(decide(&o, Some(&a), None, &f, None), None);
    }
}
