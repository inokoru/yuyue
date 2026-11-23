//! Audio-Magazine Podcast & Private Study Radio ("有声杂志" + "私人书房电台").
//!
//! - `parse`: Podcast RSS 2.0 / Podcasting 2.0 (`<enclosure>`, `<itunes:*>`,
//!   `<podcast:transcript>`, `<podcast:chapters>`, `<podcast:person>`), OPML, SRT/WebVTT/JSON
//!   transcripts, and single-episode webpage metadata.
//! - `adfold`: deterministic rule engine folding sponsor reads, intros, outros and pre-roll banter.
//! - `clip`: blind-mark backtrack (`记这句 H` / double-tap headphone) snapping to clean sentence
//!   boundaries.
//! - `studio`: Private Studio (`书房周刊`, `难书导读`, `跨书圆桌`, `生词外语`) with physical
//!   non-spoiler truncation (`loc <= reading progress`), anti-slop filter, and anchor verification.
//! - `store`: SQLite register persistence (`pod_channel/`, `pod_issue/`, `pod_clip/`,
//!   `pod_interrupt/`) exported on `YuyueCore` through UniFFI.

pub mod adfold;
pub mod clip;
pub mod parse;
pub mod store;
pub mod studio;

pub use parse::*;
pub use store::*;
pub use studio::*;
