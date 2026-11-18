//! 更新日志 and 检查更新. `CHANGELOG.md` is the one place the notes are written: a section per
//! version, newest first, `## 0.2.0 · 2026-11-20`, then `### zh` and `### en`, a `- ` line each.
//! The release script makes the GitHub release's text, the Mac's appcast and `update.json` from
//! it; the apps carry it to show every version's notes offline, and read `update.json` (fetched
//! by them — the core never goes online) to know of a newer one.

use serde::{Deserialize, Serialize};

use crate::error::{CoreError, Result};

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct Release {
    pub version: String,
    /// `2026-11-20`, as written.
    pub date: String,
    pub notes_zh: Vec<String>,
    pub notes_en: Vec<String>,
}

impl Release {
    /// The notes in the reader's language, the other one's when that's missing.
    pub fn notes(&self, chinese: bool) -> &[String] {
        let (a, b) = if chinese { (&self.notes_zh, &self.notes_en) } else { (&self.notes_en, &self.notes_zh) };
        if a.is_empty() { b } else { a }
    }
}

/// Every version in a `CHANGELOG.md`, as written (newest first). Lines before the first version,
/// and notes under no language, are left out; a note may run on to indented lines.
#[uniffi::export]
pub fn parse_changelog(text: String) -> Vec<Release> {
    let mut out: Vec<Release> = Vec::new();
    let mut lang: Option<bool> = None; // Some(true) = zh
    for raw in text.lines() {
        let line = raw.trim_end();
        if let Some(head) = line.strip_prefix("## ") {
            // `0.2.0 · 2026-11-20` (or ` - `, ` — `); a version may carry its own `-beta`.
            let cut = [" · ", " - ", " — "].iter().filter_map(|s| head.find(s).map(|i| (i, s.len()))).min();
            let (v, date) = match cut {
                Some((i, n)) => (&head[..i], head[i + n..].trim()),
                None => (head, ""),
            };
            let version = v.trim().trim_start_matches('v').to_string();
            let date = date.to_string();
            out.push(Release { version, date, ..Default::default() });
            lang = None;
        } else if let Some(l) = line.strip_prefix("### ") {
            lang = match l.trim().to_lowercase().as_str() {
                "zh" | "中文" => Some(true),
                "en" | "english" => Some(false),
                _ => None,
            };
        } else if let (Some(r), Some(zh)) = (out.last_mut(), lang) {
            let notes = if zh { &mut r.notes_zh } else { &mut r.notes_en };
            if let Some(n) = line.trim_start().strip_prefix("- ").filter(|_| !line.starts_with("  ")) {
                notes.push(n.trim().to_string());
            } else if !line.trim().is_empty()
                && let Some(last) = notes.last_mut() {
                    // A note run on to the next line.
                    if !zh {
                        last.push(' ');
                    }
                    last.push_str(line.trim());
                }
        }
    }
    out
}

/// Versions compared part by part as numbers (`0.10.0` after `0.9.2`); a missing part is 0, a
/// part with letters (`1.0.0-beta`) counts by its leading number. −1, 0 or 1.
#[uniffi::export]
pub fn compare_versions(a: String, b: String) -> i32 {
    let parts = |v: &str| -> Vec<u64> { v.trim().trim_start_matches('v').split('.').map(|p| p.chars().take_while(|c| c.is_ascii_digit()).collect::<String>().parse().unwrap_or(0)).collect() };
    let (x, y) = (parts(&a), parts(&b));
    for i in 0..x.len().max(y.len()) {
        let (p, q) = (x.get(i).copied().unwrap_or(0), y.get(i).copied().unwrap_or(0));
        if p != q {
            return if p < q { -1 } else { 1 };
        }
    }
    0
}

/// The versions in `text` newer than `current`, newest first: what an update brings, every
/// version skipped included.
#[uniffi::export]
pub fn releases_after(text: String, current: String) -> Vec<Release> {
    parse_changelog(text).into_iter().filter(|r| compare_versions(r.version.clone(), current.clone()) > 0).collect()
}

/// `update.json`: the newest version, published with each release.
#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize, uniffi::Record)]
#[serde(default)]
pub struct UpdateInfo {
    pub version: String,
    /// Android's `versionCode`.
    pub code: u64,
    pub date: String,
    /// The release's page, for a download by hand (iPhone).
    pub page: String,
    /// Android: the APK, its size and SHA-256 (hex).
    pub apk: String,
    pub size: u64,
    pub sha256: String,
    /// The whole `CHANGELOG.md` of that release: the notes of the versions in between.
    pub changelog: String,
    pub notes: Notes,
}

#[derive(Clone, Debug, Default, PartialEq, Serialize, Deserialize, uniffi::Record)]
#[serde(default)]
pub struct Notes {
    pub zh: Vec<String>,
    pub en: Vec<String>,
}

#[uniffi::export]
pub fn parse_update(json: String) -> Result<UpdateInfo> {
    let u: UpdateInfo = serde_json::from_str(&json).map_err(|e| CoreError::Invalid { detail: e.to_string() })?;
    if u.version.is_empty() {
        return Err(CoreError::Invalid { detail: "update.json has no version".into() });
    }
    Ok(u)
}

/// The newest version's notes as a `Release`, for a list that also holds the changelog's.
#[uniffi::export]
pub fn update_release(u: UpdateInfo) -> Release {
    Release { version: u.version, date: u.date, notes_zh: u.notes.zh, notes_en: u.notes.en }
}

fn escape(s: &str) -> String {
    s.replace('&', "&amp;").replace('<', "&lt;").replace('>', "&gt;").replace('"', "&quot;")
}

/// A version's notes as a list in HTML, for the Mac's update window (the appcast).
pub fn release_html(r: &Release, chinese: bool) -> String {
    let items: String = r.notes(chinese).iter().map(|n| format!("<li>{}</li>", escape(n))).collect();
    format!("<ul>{items}</ul>")
}

/// A version's notes for the GitHub release's text: the Chinese, then the English.
pub fn release_markdown(r: &Release) -> String {
    let list = |v: &[String]| v.iter().map(|n| format!("- {n}\n")).collect::<String>();
    match (r.notes_zh.is_empty(), r.notes_en.is_empty()) {
        (false, false) => format!("{}\n{}", list(&r.notes_zh), list(&r.notes_en)),
        (false, true) => list(&r.notes_zh),
        _ => list(&r.notes_en),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LOG: &str = "# 更新日志\n\n说明文字。\n\n## 0.10.0 · 2026-12-01\n\n### zh\n- 第一条。\n- 第二条，\n  接着写。\n\n### en\n- First.\n- Second,\n  run on.\n\n## 0.9.2 · 2026-11-20\n\n### zh\n- 只有中文。\n\n## v0.1.0 - 2026-11-14\n\n### en\n- Only English.\n";

    #[test]
    fn reads_every_version() {
        let r = parse_changelog(LOG.into());
        assert_eq!(r.len(), 3);
        assert_eq!((r[0].version.as_str(), r[0].date.as_str()), ("0.10.0", "2026-12-01"));
        assert_eq!(r[0].notes_zh, ["第一条。", "第二条，接着写。"]);
        assert_eq!(r[0].notes_en, ["First.", "Second, run on."]);
        assert_eq!(r[2].version, "0.1.0");
        assert_eq!(r[1].notes(false), ["只有中文。"]);
        assert_eq!(r[2].notes(true), ["Only English."]);
    }

    #[test]
    fn versions_compare_as_numbers() {
        assert_eq!(compare_versions("0.10.0".into(), "0.9.2".into()), 1);
        assert_eq!(compare_versions("1.0".into(), "1.0.0".into()), 0);
        assert_eq!(compare_versions("v1.0.0-beta".into(), "1.0.1".into()), -1);
    }

    #[test]
    fn an_update_brings_the_skipped_versions_too() {
        let v: Vec<String> = releases_after(LOG.into(), "0.1.0".into()).into_iter().map(|r| r.version).collect();
        assert_eq!(v, ["0.10.0", "0.9.2"]);
        assert!(releases_after(LOG.into(), "0.10.0".into()).is_empty());
    }

    #[test]
    fn reads_update_json() {
        let u = parse_update(r#"{"version":"0.2.0","code":3,"apk":"https://x/a.apk","notes":{"zh":["好"],"en":["Good"]}}"#.into()).unwrap();
        assert_eq!((u.version.as_str(), u.code, u.notes.zh[0].as_str()), ("0.2.0", 3, "好"));
        assert!(parse_update("{}".into()).is_err());
        assert_eq!(update_release(u).notes_en, ["Good"]);
    }

    #[test]
    fn html_and_markdown() {
        let r = &parse_changelog(LOG.into())[0];
        assert_eq!(release_html(r, true), "<ul><li>第一条。</li><li>第二条，接着写。</li></ul>");
        assert!(release_markdown(r).starts_with("- 第一条。\n"));
        assert!(release_html(&Release { notes_en: vec!["a < b".into()], ..Default::default() }, false).contains("a &lt; b"));
    }
}
