//! 广播剧: a novel read as a radio play — who says each line, a voice for each of them, how each
//! line is played, and the room it's played in.
//!
//! The core never goes online and never runs a model. It cuts the text, asks (as tasks the app
//! carries out), checks the answers, solves, keeps the results; the app runs the language
//! model, the voices, the transcriber and the speaker embeddings and hands back what they give.
//! So all three apps decide the same way and every rule is tested here.
//!
//!   ① spans     sentences → stretches (narration / speech / thought / quoted)
//!   ② cast      the whole book → who speaks, under all their names
//!   ③ scenes    a section → scenes (where, when, who's there)
//!   ④ attribute who says each line, how sure, why
//!   ⑤ direct    how each stretch is played; what's heard in each scene
//!   ⑥ casting   a voice for each character
//!   ⑦ queue     the clips to make, and what to do when one comes out wrong
//!   ⑧ sound     room tone and the sounds the book writes
//!   ⑨ timeline  where it all goes; `yuyue-mix` plays it
//!
//! Who decides, highest first: the user, the rules, the model, nobody (read in the 对话 voice).

pub mod api;
pub mod cards_api;
pub mod playback_api;
pub mod attribute;
pub mod bridge;
pub mod builtin;
pub mod card;
pub mod cast;
pub mod casting;
pub mod direct;
#[cfg(any(test, feature = "eval"))]
pub mod eval;
pub mod library;
pub mod mixer;
pub mod queue;
pub mod speaker;
pub mod scenes;
pub mod sound;
pub mod kokoro;
pub mod phoneme;
pub mod zipvoice;
pub mod spans;
pub mod timeline;

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, Serialize, Deserialize, uniffi::Enum)]
pub enum SegKind {
    Narration,
    Speech,
    Thought,
    /// A letter, a poem, a notice: read in its writer's voice when known, the narrator's otherwise.
    Quoted,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum LineBasis {
    /// The book writes who: 宝玉道.
    Explicit,
    /// Follows from what's written: turn-taking, a dropped subject, a name called.
    Inferred,
    /// Only from what's said: tone, stance, what they know. Never taken without the user.
    Content,
    None,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum DecidedBy {
    User,
    Rule,
    Model,
    Unsure,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct LineCandidate {
    pub id: u32,
    pub p: f32,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Enum)]
pub enum DramaSpeaker {
    Char { id: u32 },
    Group { name: String },
    /// Narration, and quoted writing nobody's named for.
    Narrator,
    /// Not sure: read in the 对话 voice; the two likeliest for the list.
    Unsure { top: Vec<LineCandidate> },
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum CharacterGender {
    M,
    F,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum CharacterAge {
    Child,
    Teen,
    Young,
    Middle,
    Old,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ActEmotion {
    Neutral,
    Joy,
    Anger,
    Sadness,
    Fear,
    Surprise,
    Disgust,
    Tender,
    Mocking,
    Anxious,
    Weary,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ActVolume {
    Whisper,
    Low,
    Normal,
    Loud,
    Shout,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ActPace {
    Slow,
    Normal,
    Fast,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ActNonverbal {
    Laugh,
    Sigh,
    Sob,
    Gasp,
    Cough,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum ActDistance {
    Close,
    Normal,
    Far,
    /// Behind a door, through a wall.
    Offstage,
}

/// How a stretch is played.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaAct {
    pub emotion: ActEmotion,
    /// 0 flat … 3 strong.
    pub level: u8,
    pub volume: ActVolume,
    pub pace: ActPace,
    pub nonverbal: Vec<ActNonverbal>,
    pub distance: ActDistance,
    /// The book wrote it (冷笑道), rather than a model reading it in.
    pub from_text: bool,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaCharacter {
    pub id: u32,
    /// Every name, the fullest first when shown (`key`).
    pub names: Vec<String>,
    pub gender: CharacterGender,
    pub age: CharacterAge,
    /// One line: who they are, how they talk.
    pub traits: String,
    pub lines: u32,
    /// The section they first speak in: the cast page lists only those met so far.
    pub first_section: Option<u32>,
    /// From the chapter being read before the whole book's cast came.
    pub provisional: bool,
    /// The first-person teller (我): reads in the narrator's voice.
    pub narrator: bool,
    /// Another row that's the same person, and from which section the reader knows it.
    pub revealed_in: Option<u32>,
    pub same_as: Option<u32>,
    /// The name the cast page and the bubbles use: the user's pick, else the shortest of two
    /// characters or more (宝玉, not 贾宝玉 or 玉).
    #[serde(default)]
    pub shown: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum SceneTime {
    Day,
    Night,
    Dawn,
    Dusk,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum SceneWeather {
    Clear,
    Rain,
    Snow,
    Wind,
    Storm,
    Unknown,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize, uniffi::Enum)]
pub enum SceneCrowd {
    Alone,
    Few,
    Many,
    Unknown,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaScene {
    pub section: u32,
    /// Sentences, both ends in.
    pub first: u32,
    pub last: u32,
    pub place: String,
    /// One of `scenes::PLACE_KINDS`: what the room tone is chosen by.
    pub place_kind: String,
    pub indoor: Option<bool>,
    pub time: SceneTime,
    pub weather: SceneWeather,
    pub crowd: SceneCrowd,
    pub present: Vec<u32>,
}

/// A stretch of a sentence and how it's read.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize, uniffi::Record)]
pub struct DramaSegment {
    pub sentence: u32,
    /// Characters of the sentence.
    pub start: u32,
    pub end: u32,
    pub kind: SegKind,
    pub utterance: Option<u32>,
    pub speaker: DramaSpeaker,
    pub addressee: Option<u32>,
    pub basis: LineBasis,
    pub decided_by: DecidedBy,
    /// How likely it's right: 1 for the user's and the rules'.
    pub p_correct: f32,
    pub act: DramaAct,
    /// Only says who speaks (宝玉道): left out when 剧化 is on.
    pub tag_only: bool,
    pub text: String,
}
