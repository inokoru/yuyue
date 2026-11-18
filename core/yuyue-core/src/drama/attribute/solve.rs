//! 按场整体求解: the lines of a scene decided together, not one by one. In a back-and-forth one
//! line settled settles the ones around it.
//!
//! Each line scores each candidate (the model's choice, the check's, names inside the line);
//! neighbouring lines add what follows from talk (the same person rarely speaks twice running
//! without narration between; two people take turns). The state is the pair of speakers of
//! two lines in a row, so turn-taking — this line's speaker is the one before last — is seen.
//! Forward–backward gives, for each line, how likely each candidate is.

use super::ask::{Cand, Checked, Vote};
use super::constraints::{LineFacts, allowed};
use crate::drama::LineBasis;
use crate::drama::cast::CastTable;

/// The soft scores. Tuned on the calibration set (第十节); kept together here.
pub mod w {
    pub const EXPLICIT: f32 = 6.0;
    pub const INFERRED: f32 = 4.0;
    pub const CONTENT: f32 = 2.0;
    pub const VERIFY_AGREES: f32 = 3.0;
    pub const VERIFY_EXCLUDES: f32 = -4.0;
    pub const NAMED_INSIDE: f32 = -2.0;
    pub const SAME_TWICE: f32 = -3.0;
    pub const TURNS: f32 = 2.0;
    pub const ADDRESSED: f32 = 2.0;
    /// What a candidate nobody chose scores: below any vote.
    pub const PRIOR: f32 = 0.0;
    /// The crowd says a line only when something says so.
    pub const GROUP_PRIOR: f32 = -4.0;
}

/// One line of the scene to decide.
#[derive(Clone, Debug, Default)]
pub struct LineIn {
    pub utterance: u32,
    /// Settled by the user or the rules.
    pub fixed: Option<Cand>,
    pub first: Option<Checked>,
    pub verify: Option<Checked>,
    pub facts: LineFacts,
}

/// One line decided.
#[derive(Clone, Debug, PartialEq)]
pub struct LineOut {
    pub utterance: u32,
    /// Candidates with their posterior, best first.
    pub post: Vec<(Cand, f32)>,
}

const NEG: f32 = -1e9;

fn lse(xs: impl IntoIterator<Item = f32>) -> f32 {
    let v: Vec<f32> = xs.into_iter().collect();
    let m = v.iter().cloned().fold(NEG, f32::max);
    if m <= NEG / 2.0 {
        return NEG;
    }
    m + v.iter().map(|x| (x - m).exp()).sum::<f32>().ln()
}

/// Whether the scene's talk is between two people: two named speakers at most among settled
/// lines, or two present.
fn two_people(lines: &[LineIn], present: &[u32]) -> bool {
    if present.len() == 2 {
        return true;
    }
    let mut seen: Vec<Cand> = lines.iter().filter_map(|l| l.fixed).collect();
    seen.sort();
    seen.dedup();
    seen.len() == 2 && seen.iter().all(|c| matches!(c, Cand::Char(_)))
}

pub fn solve(lines: &[LineIn], present: &[u32], cast: &CastTable) -> Vec<LineOut> {
    let n = lines.len();
    if n == 0 {
        return Vec::new();
    }
    let mut cands: Vec<Cand> = present.iter().map(|&id| Cand::Char(id)).collect();
    cands.push(Cand::Group);
    // Settled lines may name someone the scene's list missed.
    for l in lines {
        if let Some(c) = l.fixed
            && !cands.contains(&c) {
                cands.push(c);
            }
    }
    let k = cands.len();
    let mut present_all: Vec<u32> = present.to_vec();
    for c in &cands {
        if let Cand::Char(id) = c
            && !present_all.contains(id) {
                present_all.push(*id);
            }
    }
    // Emissions.
    let e: Vec<Vec<f32>> = lines
        .iter()
        .map(|l| {
            cands
                .iter()
                .map(|&c| {
                    if let Some(f) = l.fixed {
                        return if f == c { 0.0 } else { NEG };
                    }
                    if !allowed(c, &l.facts, &present_all, cast) {
                        return NEG;
                    }
                    let mut s = if c == Cand::Group { w::GROUP_PRIOR } else { w::PRIOR };
                    if let Some(a) = &l.first
                        && a.vote == Vote::Is(c) {
                            s += match a.basis {
                                LineBasis::Explicit => w::EXPLICIT,
                                LineBasis::Inferred => w::INFERRED,
                                LineBasis::Content => w::CONTENT,
                                LineBasis::None => 0.0,
                            };
                        }
                    if let Some(v) = &l.verify {
                        if v.vote == Vote::Is(c) {
                            s += w::VERIFY_AGREES;
                        }
                        if v.excluded.contains(&c) {
                            s += w::VERIFY_EXCLUDES;
                        }
                    }
                    if let Cand::Char(id) = c
                        && l.facts.named_inside.contains(&id) {
                            s += w::NAMED_INSIDE;
                        }
                    s
                })
                .collect()
        })
        .collect();
    let two = two_people(lines, present);
    // Between line i-1 (speaker a) and line i (speaker b).
    let t1 = |i: usize, a: usize, b: usize| -> f32 {
        let mut s = 0.0;
        if a == b && lines[i].facts.narration_before == 0 && cands[a] != Cand::Group && !lines[i].facts.interrupts {
            s += w::SAME_TWICE;
        }
        if let (Some(f), Cand::Char(id)) = (&lines[i - 1].first, cands[b])
            && f.to == Some(id) && a != b {
                s += w::ADDRESSED;
            }
        s
    };
    // Turn-taking: line i's speaker c is the one before last (a), not the last (b).
    let t2 = |i: usize, a: usize, b: usize, c: usize| -> f32 {
        if two && a == c && b != c && lines[i].facts.narration_before <= 1 && lines[i - 1].facts.narration_before <= 1 { w::TURNS } else { 0.0 }
    };
    if n == 1 {
        let z = lse(e[0].iter().cloned());
        return vec![out_of(lines[0].utterance, &cands, &e[0].iter().map(|x| (x - z).exp()).collect::<Vec<_>>())];
    }
    // alpha[i][a*k+b]: lines i-1, i say a, b.
    let idx = |a: usize, b: usize| a * k + b;
    let mut alpha = vec![vec![NEG; k * k]; n];
    for a in 0..k {
        for b in 0..k {
            alpha[1][idx(a, b)] = e[0][a] + e[1][b] + t1(1, a, b);
        }
    }
    for i in 2..n {
        for b in 0..k {
            for c in 0..k {
                if e[i][c] <= NEG / 2.0 {
                    continue;
                }
                let v = lse((0..k).map(|a| alpha[i - 1][idx(a, b)] + t2(i, a, b, c)));
                alpha[i][idx(b, c)] = v + e[i][c] + t1(i, b, c);
            }
        }
    }
    let mut beta = vec![vec![0.0f32; k * k]; n];
    for i in (1..n - 1).rev() {
        for a in 0..k {
            for b in 0..k {
                beta[i][idx(a, b)] = lse((0..k).map(|c| e[i + 1][c] + t1(i + 1, b, c) + t2(i + 1, a, b, c) + beta[i + 1][idx(b, c)]));
            }
        }
    }
    let z = lse(alpha[n - 1].iter().cloned());
    let mut out = Vec::with_capacity(n);
    for i in 0..n {
        let p: Vec<f32> = if i == 0 {
            (0..k).map(|a| lse((0..k).map(|b| alpha[1][idx(a, b)] + beta[1][idx(a, b)])) - z).map(f32::exp).collect()
        } else {
            (0..k).map(|b| lse((0..k).map(|a| alpha[i][idx(a, b)] + beta[i][idx(a, b)])) - z).map(f32::exp).collect()
        };
        out.push(out_of(lines[i].utterance, &cands, &p));
    }
    out
}

fn out_of(utterance: u32, cands: &[Cand], p: &[f32]) -> LineOut {
    let mut post: Vec<(Cand, f32)> = cands.iter().cloned().zip(p.iter().cloned()).filter(|(_, x)| *x > 1e-6).collect();
    post.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
    LineOut { utterance, post }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::drama::cast::from_rules;

    fn voted(c: Cand, basis: LineBasis) -> Option<Checked> {
        Some(Checked { utterance: 0, vote: Vote::Is(c), basis, to: None, anchor_ok: true, excluded: vec![] })
    }

    #[test]
    fn a_back_and_forth_follows_from_one_settled_line() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into()]);
        let (b, d) = (cast.resolve("宝玉").unwrap(), cast.resolve("黛玉").unwrap());
        let mut lines: Vec<LineIn> = (0..5).map(|u| LineIn { utterance: u, ..Default::default() }).collect();
        lines[0].fixed = Some(Cand::Char(b));
        let out = solve(&lines, &[b, d], &cast);
        let best: Vec<Cand> = out.iter().map(|o| o.post[0].0).collect();
        assert_eq!(best, vec![Cand::Char(b), Cand::Char(d), Cand::Char(b), Cand::Char(d), Cand::Char(b)]);
        assert!(out[1].post[0].1 > 0.9, "{:?}", out[1]);
        // Further from the settled line, less sure.
        assert!(out[4].post[0].1 <= out[1].post[0].1 + 1e-6);
    }

    #[test]
    fn a_settled_line_is_never_moved_and_votes_count() {
        let cast = from_rules(&["宝玉".into(), "黛玉".into(), "凤姐".into()]);
        let ids: Vec<u32> = cast.chars.iter().map(|c| c.id).collect();
        let feng = cast.resolve("凤姐").unwrap();
        let mut lines: Vec<LineIn> = (0..2).map(|u| LineIn { utterance: u, ..Default::default() }).collect();
        lines[0].fixed = Some(Cand::Char(feng));
        lines[1].first = voted(Cand::Char(feng), LineBasis::Explicit);
        lines[1].facts.narration_before = 1;
        let out = solve(&lines, &ids, &cast);
        assert_eq!(out[0].post, vec![(Cand::Char(feng), 1.0)]);
        assert_eq!(out[1].post[0].0, Cand::Char(feng));
    }

    #[test]
    fn probabilities_add_up() {
        let cast = from_rules(&["甲乙".into(), "丙丁".into(), "戊己".into()]);
        let ids: Vec<u32> = cast.chars.iter().map(|c| c.id).collect();
        let lines: Vec<LineIn> = (0..4).map(|u| LineIn { utterance: u, ..Default::default() }).collect();
        for o in solve(&lines, &ids, &cast) {
            let s: f32 = o.post.iter().map(|p| p.1).sum();
            assert!((s - 1.0).abs() < 1e-3, "{s}");
        }
    }
}
