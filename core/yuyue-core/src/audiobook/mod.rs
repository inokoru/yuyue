//! 有声书: a recording someone read, laid against the book it reads, so eyes and ears share one
//! place. The core does what's a rule — containers, order, sounds, matching, the dynamic
//! programming, the map between sentences and seconds, progress; the apps decode, find speech,
//! transcribe and run the acoustic model, and hand back what those give.
//!
//!   ① import   containers, order, chapters, identity
//!   ② listen   speech found and transcribed (apps)
//!   ③ match    which book, which edition
//!   ④ anchor   sounds → anchors → sentence by sentence (coarse, ±1 s)
//!   ⑤ refine   acoustic forced alignment (apps) → snapped into pauses (fine, ±50 ms)
//!   ⑥ grade    how sure each sentence is: exact / spread / chapter only; extras and unread
//!
//! Wrong is worse than rough: where it can't be sure, a sentence gets a rough time between two
//! it is sure of, never a precise wrong one.

pub mod anchor;
pub mod api;
pub mod eval;
pub mod export;
pub mod fromlisten;
pub mod grade;
pub mod import;
pub mod map;
pub mod matchbook;
pub mod overlay;
pub mod phon;
pub mod progress;
pub mod refine;
pub mod textbook;
pub mod vad;
