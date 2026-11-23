//! Persistent Audio-Magazine & Private Studio store on `YuyueCore`.
//!
//! Uses the existing CRDT operation-log `Store` (`pod_channel/`, `pod_issue/`, `pod_clip/`,
//! `pod_interrupt/`) so subscriptions, Desk/Newsstand/Archive placement, playback position,
//! highlights/clips, and cut-in Q&A side notes survive restarts and sync cleanly across devices.
//!
//! Large transcripts are stored in `<data>/podcast_hub/issues/<id>.json` alongside the SQLite
//! registers so sync batches stay lean.

use std::collections::BTreeMap;
use std::path::PathBuf;

use serde_json::{Value, json};

use crate::api::{YuyueCore, lock, now_ms};
use crate::error::{CoreError, Result};
use crate::podcast_hub::parse::{MagazineSentence, ParsedPodcastFeed, PodcastChapter, PodcastPerson};
use crate::podcast_hub::studio::{ParsedStudioDraft, StudioKind};

/// Three-state desk placement: never piles up unread counts.
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum PodcastPlacement {
    /// 书桌待听 / 正在听
    Desk,
    /// 报刊栏 (rolling 14-day window, no unread badges)
    Newsstand,
    /// 已留档 (kept in the study permanently)
    Archive,
}

impl PodcastPlacement {
    fn key(self) -> &'static str {
        match self {
            PodcastPlacement::Desk => "desk",
            PodcastPlacement::Newsstand => "newsstand",
            PodcastPlacement::Archive => "archive",
        }
    }
    fn from_key(s: &str) -> PodcastPlacement {
        match s {
            "desk" => PodcastPlacement::Desk,
            "archive" => PodcastPlacement::Archive,
            _ => PodcastPlacement::Newsstand,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum PodcastSourceKind {
    Rss,
    SingleImport,
    LocalAudio,
    Studio,
}

impl PodcastSourceKind {
    fn key(self) -> &'static str {
        match self {
            PodcastSourceKind::Rss => "rss",
            PodcastSourceKind::SingleImport => "single",
            PodcastSourceKind::LocalAudio => "local",
            PodcastSourceKind::Studio => "studio",
        }
    }
    fn from_key(s: &str) -> PodcastSourceKind {
        match s {
            "single" => PodcastSourceKind::SingleImport,
            "local" => PodcastSourceKind::LocalAudio,
            "studio" => PodcastSourceKind::Studio,
            _ => PodcastSourceKind::Rss,
        }
    }
}

/// Progressive fidelity levels (L0 audio-only -> L1 live transcript -> L2 structured magazine -> L3 cross-lingual).
#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum PodcastFidelity {
    L0AudioOnly,
    L1LiveTranscript,
    L2Magazine,
    L3CrossLingual,
}

impl PodcastFidelity {
    fn key(self) -> &'static str {
        match self {
            PodcastFidelity::L0AudioOnly => "L0",
            PodcastFidelity::L1LiveTranscript => "L1",
            PodcastFidelity::L2Magazine => "L2",
            PodcastFidelity::L3CrossLingual => "L3",
        }
    }
    fn from_key(s: &str) -> PodcastFidelity {
        match s {
            "L1" => PodcastFidelity::L1LiveTranscript,
            "L2" => PodcastFidelity::L2Magazine,
            "L3" => PodcastFidelity::L3CrossLingual,
            _ => PodcastFidelity::L0AudioOnly,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum PodcastEdition {
    Full,
    Highlights,
}

impl PodcastEdition {
    fn key(self) -> &'static str {
        match self {
            PodcastEdition::Full => "full",
            PodcastEdition::Highlights => "highlights",
        }
    }
    fn from_key(s: &str) -> PodcastEdition {
        match s {
            "highlights" => PodcastEdition::Highlights,
            _ => PodcastEdition::Full,
        }
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum PodcastTrack {
    Original,
    Dub,
    Coach,
}

impl PodcastTrack {
    fn key(self) -> &'static str {
        match self {
            PodcastTrack::Original => "orig",
            PodcastTrack::Dub => "dub",
            PodcastTrack::Coach => "coach",
        }
    }
    fn from_key(s: &str) -> PodcastTrack {
        match s {
            "dub" => PodcastTrack::Dub,
            "coach" => PodcastTrack::Coach,
            _ => PodcastTrack::Original,
        }
    }
}

/// A subscribed external podcast channel (RSS feed).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PodcastChannel {
    pub id: String,
    pub feed_url: String,
    pub title: String,
    pub author: String,
    pub summary: String,
    pub language: String,
    pub artwork_url: Option<String>,
    pub added_at: i64,
    pub refreshed_at: i64,
}

/// One Audio-Magazine issue / episode (external RSS, single import, local audio, or Private Studio).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PodcastIssue {
    pub id: String,
    pub channel_id: Option<String>,
    pub show_title: String,
    pub episode_number: Option<String>,
    pub title: String,
    pub summary: String,
    pub shownotes: String,
    pub source: PodcastSourceKind,
    pub studio_kind: Option<StudioKind>,
    pub placement: PodcastPlacement,
    pub fidelity: PodcastFidelity,
    pub edition: PodcastEdition,
    pub track: PodcastTrack,
    pub language: String,
    pub audio_url: Option<String>,
    pub local_audio_path: Option<String>,
    pub artwork_url: Option<String>,
    pub duration_sec: u32,
    pub highlight_sec: u32,
    pub position_sec: f64,
    pub published_at: i64,
    pub added_at: i64,
    pub last_played_at: Option<i64>,
    pub anchor_count: u32,
    pub chapters: Vec<PodcastChapter>,
    pub people: Vec<PodcastPerson>,
    pub book_ids: Vec<String>,
}

/// A saved highlight / audio micro-clip ("原声摘" / "记这句 H").
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PodcastClip {
    pub id: String,
    pub issue_id: String,
    pub issue_title: String,
    pub show_title: String,
    pub t0: f64,
    pub t1: f64,
    pub speaker: Option<String>,
    pub quote_text: String,
    pub thought: Option<String>,
    pub audio_rel_path: Option<String>,
    pub created_at: i64,
}

/// A cut-in voice/text Q&A side note ("插话") recorded while listening.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PodcastInterrupt {
    pub id: String,
    pub issue_id: String,
    pub at_sec: f64,
    pub question: String,
    pub answer: String,
    pub created_at: i64,
}

/// Cross-reference from a book on the shelf to podcast episodes that discussed or anchored it.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct BookPodcastMention {
    pub issue_id: String,
    pub issue_title: String,
    pub show_title: String,
    pub snippet: String,
    pub at_sec: f64,
    pub studio_kind: Option<StudioKind>,
}

/// Payload persisted on disk per issue for full transcript sentences.
#[derive(Clone, Debug, Default, serde::Serialize, serde::Deserialize)]
struct IssueTranscriptFile {
    sentences: Vec<MagazineSentence>,
    highlight_ids: Vec<String>,
}

fn stable_id(prefix: &str, seed: &str) -> String {
    let hash = blake3::hash(format!("{prefix}:{seed}").as_bytes());
    let hex = hash.to_hex();
    format!("{prefix}_{}", &hex[..16])
}

fn fresh_id(prefix: &str) -> String {
    let n = getrandom::u64().unwrap_or_else(|_| now_ms() as u64 ^ (std::process::id() as u64) << 32);
    format!("{prefix}_{n:016x}")
}

fn str_field(f: &BTreeMap<String, Value>, k: &str) -> String {
    f.get(k).and_then(Value::as_str).unwrap_or("").to_string()
}

fn opt_str(f: &BTreeMap<String, Value>, k: &str) -> Option<String> {
    f.get(k).and_then(Value::as_str).filter(|s| !s.is_empty()).map(str::to_string)
}

fn i64_field(f: &BTreeMap<String, Value>, k: &str) -> i64 {
    f.get(k).and_then(Value::as_i64).unwrap_or(0)
}

fn f64_field(f: &BTreeMap<String, Value>, k: &str) -> f64 {
    f.get(k).and_then(Value::as_f64).unwrap_or(0.0)
}

fn u32_field(f: &BTreeMap<String, Value>, k: &str) -> u32 {
    f.get(k).and_then(Value::as_u64).unwrap_or(0) as u32
}

impl YuyueCore {
    pub(crate) fn podcast_hub_dir(&self) -> PathBuf {
        self.books_dir.parent().unwrap_or(&self.books_dir).join("podcast_hub")
    }

    fn issue_transcript_path(&self, issue_id: &str) -> PathBuf {
        self.podcast_hub_dir().join("issues").join(format!("{issue_id}.json"))
    }

    fn save_transcript_file(&self, issue_id: &str, file: &IssueTranscriptFile) -> Result<()> {
        let path = self.issue_transcript_path(issue_id);
        if let Some(parent) = path.parent() {
            std::fs::create_dir_all(parent)?;
        }
        let tmp = path.with_extension(format!("json.{}", std::process::id()));
        std::fs::write(&tmp, serde_json::to_string(file)?)?;
        std::fs::rename(&tmp, &path)?;
        Ok(())
    }

    fn load_transcript_file(&self, issue_id: &str) -> IssueTranscriptFile {
        let path = self.issue_transcript_path(issue_id);
        std::fs::read_to_string(path)
            .ok()
            .and_then(|s| serde_json::from_str(&s).ok())
            .unwrap_or_default()
    }

    fn issue_from(id: &str, f: &BTreeMap<String, Value>) -> Option<PodcastIssue> {
        if f.get("deleted") == Some(&json!(true)) {
            return None;
        }
        let title = opt_str(f, "title")?;
        let chapters: Vec<PodcastChapter> = f
            .get("chapters")
            .cloned()
            .and_then(|v| serde_json::from_value(v).ok())
            .unwrap_or_default();
        let people: Vec<PodcastPerson> = f
            .get("people")
            .cloned()
            .and_then(|v| serde_json::from_value(v).ok())
            .unwrap_or_default();
        let book_ids: Vec<String> = f
            .get("bookIds")
            .cloned()
            .and_then(|v| serde_json::from_value(v).ok())
            .unwrap_or_default();
        let studio_kind = opt_str(f, "studioKind").map(|k| StudioKind::from_key(&k));
        Some(PodcastIssue {
            id: id.to_string(),
            channel_id: opt_str(f, "channelId"),
            show_title: str_field(f, "showTitle"),
            episode_number: opt_str(f, "episodeNumber"),
            title,
            summary: str_field(f, "summary"),
            shownotes: str_field(f, "shownotes"),
            source: PodcastSourceKind::from_key(&str_field(f, "source")),
            studio_kind,
            placement: PodcastPlacement::from_key(&str_field(f, "placement")),
            fidelity: PodcastFidelity::from_key(&str_field(f, "fidelity")),
            edition: PodcastEdition::from_key(&str_field(f, "edition")),
            track: PodcastTrack::from_key(&str_field(f, "track")),
            language: opt_str(f, "language").unwrap_or_else(|| "zh-Hans".to_string()),
            audio_url: opt_str(f, "audioUrl"),
            local_audio_path: opt_str(f, "localAudioPath"),
            artwork_url: opt_str(f, "artworkUrl"),
            duration_sec: u32_field(f, "durationSec"),
            highlight_sec: u32_field(f, "highlightSec"),
            position_sec: f64_field(f, "positionSec"),
            published_at: i64_field(f, "publishedAt"),
            added_at: i64_field(f, "addedAt"),
            last_played_at: f.get("lastPlayedAt").and_then(Value::as_i64),
            anchor_count: u32_field(f, "anchorCount"),
            chapters,
            people,
            book_ids,
        })
    }
}

#[uniffi::export]
impl YuyueCore {
    /// Directory where extracted audio micro-clips (`clips/`) and cached audio live.
    pub fn podcast_clips_dir(&self) -> String {
        let dir = self.podcast_hub_dir().join("clips");
        let _ = std::fs::create_dir_all(&dir);
        dir.to_string_lossy().into_owned()
    }

    /// Lists all subscribed external podcast channels.
    pub fn podcast_channels(&self) -> Result<Vec<PodcastChannel>> {
        let mut out = Vec::new();
        for (entity, f) in lock(&self.store).entities("pod_channel/")? {
            if f.get("deleted") == Some(&json!(true)) {
                continue;
            }
            let Some(id) = entity.strip_prefix("pod_channel/") else { continue };
            out.push(PodcastChannel {
                id: id.to_string(),
                feed_url: str_field(&f, "feedUrl"),
                title: str_field(&f, "title"),
                author: str_field(&f, "author"),
                summary: str_field(&f, "summary"),
                language: opt_str(&f, "language").unwrap_or_else(|| "zh-Hans".to_string()),
                artwork_url: opt_str(&f, "artworkUrl"),
                added_at: i64_field(&f, "addedAt"),
                refreshed_at: i64_field(&f, "refreshedAt"),
            });
        }
        out.sort_by_key(|c| std::cmp::Reverse(c.added_at));
        Ok(out)
    }

    /// Subscribes to (or refreshes) a parsed RSS feed, placing recent episodes (within 14 days)
    /// into the Newsstand (`报刊栏`) without ever creating unread badges.
    pub fn podcast_upsert_channel_from_feed(
        &self,
        feed_url: String,
        feed: ParsedPodcastFeed,
        put_latest_on_desk: bool,
    ) -> Result<PodcastChannel> {
        let now = now_ms();
        let ch_id = stable_id("ch", feed_url.trim());
        let entity = format!("pod_channel/{ch_id}");
        let existing_added = lock(&self.store)
            .get(&entity, "addedAt")?
            .and_then(|v| v.as_i64())
            .unwrap_or(now);

        lock(&self.store).set_fields(
            &entity,
            &[
                ("feedUrl", json!(feed_url.trim())),
                ("title", json!(feed.title)),
                ("author", json!(feed.author)),
                ("summary", json!(feed.summary)),
                ("language", json!(feed.language)),
                ("artworkUrl", json!(feed.artwork_url)),
                ("addedAt", json!(existing_added)),
                ("refreshedAt", json!(now)),
                ("deleted", json!(false)),
            ],
        )?;

        let fourteen_days_ms = 14 * 24 * 3600 * 1000i64;
        for (idx, ep) in feed.episodes.iter().take(20).enumerate() {
            let issue_id = stable_id("iss", &format!("{ch_id}:{}", ep.guid));
            let iss_entity = format!("pod_issue/{issue_id}");
            let already_exists = lock(&self.store).get(&iss_entity, "title")?.is_some();
            if already_exists {
                continue;
            }
            let pub_ms = if ep.published_at > 0 { ep.published_at } else { now };
            // Newsstand only keeps recent 14 days (unless user explicitly puts latest on Desk).
            let recent = now - pub_ms <= fourteen_days_ms;
            if idx > 0 && !recent {
                continue;
            }
            let placement = if idx == 0 && put_latest_on_desk {
                PodcastPlacement::Desk
            } else {
                PodcastPlacement::Newsstand
            };
            let highlight_sec = if ep.duration_sec >= 1800 { 900 } else { ep.duration_sec / 3 };
            lock(&self.store).set_fields(
                &iss_entity,
                &[
                    ("channelId", json!(ch_id)),
                    ("showTitle", json!(feed.title)),
                    ("episodeNumber", json!(ep.episode_number)),
                    ("title", json!(ep.title)),
                    ("summary", json!(ep.summary)),
                    ("shownotes", json!(ep.shownotes)),
                    ("source", json!(PodcastSourceKind::Rss.key())),
                    ("placement", json!(placement.key())),
                    ("fidelity", json!(PodcastFidelity::L0AudioOnly.key())),
                    ("edition", json!(PodcastEdition::Full.key())),
                    ("track", json!(PodcastTrack::Original.key())),
                    ("language", json!(feed.language)),
                    ("audioUrl", json!(ep.audio_url)),
                    ("artworkUrl", json!(ep.artwork_url)),
                    ("durationSec", json!(ep.duration_sec)),
                    ("highlightSec", json!(highlight_sec)),
                    ("positionSec", json!(0.0)),
                    ("publishedAt", json!(pub_ms)),
                    ("addedAt", json!(now)),
                    ("chapters", json!(ep.chapters)),
                    ("people", json!(ep.people)),
                    ("deleted", json!(false)),
                ],
            )?;
        }

        Ok(PodcastChannel {
            id: ch_id,
            feed_url: feed_url.trim().to_string(),
            title: feed.title,
            author: feed.author,
            summary: feed.summary,
            language: feed.language,
            artwork_url: feed.artwork_url,
            added_at: existing_added,
            refreshed_at: now,
        })
    }

    /// Imports a single external episode or local audio file straight onto the Desk ("单集直达书桌").
    #[allow(clippy::too_many_arguments)]
    pub fn podcast_import_single_issue(
        &self,
        title: String,
        show_title: String,
        summary: String,
        shownotes: String,
        audio_url: Option<String>,
        local_audio_path: Option<String>,
        artwork_url: Option<String>,
        duration_sec: u32,
        language: String,
        source: PodcastSourceKind,
    ) -> Result<PodcastIssue> {
        let now = now_ms();
        let seed = audio_url
            .as_deref()
            .or(local_audio_path.as_deref())
            .unwrap_or(&title);
        let id = stable_id("iss", &format!("{seed}:{title}"));
        let entity = format!("pod_issue/{id}");
        let chapters = crate::podcast_hub::parse::extract_chapters_from_shownotes(&shownotes);
        let highlight_sec = if duration_sec >= 1800 { 900 } else { duration_sec / 3 };
        lock(&self.store).set_fields(
            &entity,
            &[
                ("showTitle", json!(show_title)),
                ("title", json!(title)),
                ("summary", json!(summary)),
                ("shownotes", json!(shownotes)),
                ("source", json!(source.key())),
                ("placement", json!(PodcastPlacement::Desk.key())),
                ("fidelity", json!(PodcastFidelity::L0AudioOnly.key())),
                ("edition", json!(PodcastEdition::Full.key())),
                ("track", json!(PodcastTrack::Original.key())),
                ("language", json!(if language.is_empty() { "zh-Hans".to_string() } else { language })),
                ("audioUrl", json!(audio_url)),
                ("localAudioPath", json!(local_audio_path)),
                ("artworkUrl", json!(artwork_url)),
                ("durationSec", json!(duration_sec)),
                ("highlightSec", json!(highlight_sec)),
                ("positionSec", json!(0.0)),
                ("publishedAt", json!(now)),
                ("addedAt", json!(now)),
                ("chapters", json!(chapters)),
                ("deleted", json!(false)),
            ],
        )?;
        self.podcast_issue(id)
    }

    /// Saves a generated Private Studio episode straight onto the Desk with its full structured
    /// magazine transcript and verified book anchors.
    pub fn podcast_save_studio_issue(
        &self,
        kind: StudioKind,
        draft: ParsedStudioDraft,
        book_ids: Vec<String>,
        language: String,
    ) -> Result<PodcastIssue> {
        let now = now_ms();
        let id = fresh_id("st");
        let duration_sec = draft.sentences.last().map(|s| s.t1.ceil() as u32).unwrap_or(300).max(60);
        let entity = format!("pod_issue/{id}");
        let fidelity = if kind == StudioKind::VocabRadio {
            PodcastFidelity::L3CrossLingual
        } else {
            PodcastFidelity::L2Magazine
        };
        let track = if kind == StudioKind::VocabRadio {
            PodcastTrack::Coach
        } else {
            PodcastTrack::Original
        };
        lock(&self.store).set_fields(
            &entity,
            &[
                ("showTitle", json!(kind.label())),
                ("title", json!(draft.title)),
                ("summary", json!(draft.summary)),
                ("shownotes", json!(draft.subtitle)),
                ("source", json!(PodcastSourceKind::Studio.key())),
                ("studioKind", json!(kind.key())),
                ("placement", json!(PodcastPlacement::Desk.key())),
                ("fidelity", json!(fidelity.key())),
                ("edition", json!(PodcastEdition::Full.key())),
                ("track", json!(track.key())),
                ("language", json!(language)),
                ("durationSec", json!(duration_sec)),
                ("highlightSec", json!((duration_sec / 2).max(60))),
                ("positionSec", json!(0.0)),
                ("publishedAt", json!(now)),
                ("addedAt", json!(now)),
                ("anchorCount", json!(draft.anchor_count)),
                ("chapters", json!(draft.chapters)),
                ("bookIds", json!(book_ids)),
                ("deleted", json!(false)),
            ],
        )?;
        self.save_transcript_file(
            &id,
            &IssueTranscriptFile { sentences: draft.sentences, highlight_ids: Vec::new() },
        )?;
        self.podcast_issue(id)
    }

    /// Lists issues in a given placement (`Desk` / `Newsstand` / `Archive`), or all issues when
    /// `placement` is `None`. Newsstand automatically filters to the last 14 days.
    pub fn podcast_issues(&self, placement: Option<PodcastPlacement>) -> Result<Vec<PodcastIssue>> {
        let now = now_ms();
        let fourteen_days_ms = 14 * 24 * 3600 * 1000i64;
        let mut out = Vec::new();
        for (entity, f) in lock(&self.store).entities("pod_issue/")? {
            let Some(id) = entity.strip_prefix("pod_issue/") else { continue };
            let Some(issue) = Self::issue_from(id, &f) else { continue };
            if let Some(want) = placement {
                if issue.placement != want {
                    continue;
                }
                if want == PodcastPlacement::Newsstand && issue.published_at > 0 && now - issue.published_at > fourteen_days_ms {
                    continue;
                }
            }
            out.push(issue);
        }
        out.sort_by_key(|i| std::cmp::Reverse(i.last_played_at.unwrap_or(0).max(i.published_at).max(i.added_at)));
        Ok(out)
    }

    pub fn podcast_issue(&self, issue_id: String) -> Result<PodcastIssue> {
        let f = lock(&self.store).fields(&format!("pod_issue/{issue_id}"))?;
        Self::issue_from(&issue_id, &f).ok_or_else(|| CoreError::not_found("podcast issue"))
    }

    pub fn podcast_set_placement(&self, issue_id: String, placement: PodcastPlacement) -> Result<()> {
        lock(&self.store).set(&format!("pod_issue/{issue_id}"), "placement", json!(placement.key()))
    }

    pub fn podcast_delete_issue(&self, issue_id: String) -> Result<()> {
        lock(&self.store).set(&format!("pod_issue/{issue_id}"), "deleted", json!(true))
    }

    pub fn podcast_save_playback(
        &self,
        issue_id: String,
        position_sec: f64,
        duration_sec: Option<u32>,
    ) -> Result<()> {
        let now = now_ms();
        let mut fields = vec![
            ("positionSec", json!(position_sec.max(0.0))),
            ("lastPlayedAt", json!(now)),
        ];
        if let Some(d) = duration_sec.filter(|&d| d > 0) {
            fields.push(("durationSec", json!(d)));
        }
        lock(&self.store).set_fields(&format!("pod_issue/{issue_id}"), &fields)
    }

    pub fn podcast_set_view_mode(
        &self,
        issue_id: String,
        edition: PodcastEdition,
        track: PodcastTrack,
    ) -> Result<()> {
        lock(&self.store).set_fields(
            &format!("pod_issue/{issue_id}"),
            &[("edition", json!(edition.key())), ("track", json!(track.key()))],
        )
    }

    /// Replaces/updates an issue's magazine transcript sentences and fidelity level.
    pub fn podcast_set_transcript(
        &self,
        issue_id: String,
        mut sentences: Vec<MagazineSentence>,
        fidelity: PodcastFidelity,
    ) -> Result<()> {
        crate::podcast_hub::adfold::mark_skippable_sentences(&mut sentences);
        let has_trans = sentences.iter().any(|s| s.text_trans.is_some());
        let effective_fidelity = if has_trans { PodcastFidelity::L3CrossLingual } else { fidelity };
        let anchor_count = sentences.iter().filter(|s| s.book_id.is_some()).count() as u32;

        // Default highlight edition picks the most substantial non-skipped sentences (~30%).
        let highlight_ids: Vec<String> = sentences
            .iter()
            .filter(|s| s.skip_reason.is_none() && s.text_orig.chars().count() >= 28)
            .step_by(3)
            .map(|s| s.id.clone())
            .collect();

        let highlight_sec: u32 = sentences
            .iter()
            .filter(|s| highlight_ids.contains(&s.id))
            .map(|s| (s.t1 - s.t0).max(0.0))
            .sum::<f64>()
            .round() as u32;

        self.save_transcript_file(&issue_id, &IssueTranscriptFile { sentences, highlight_ids })?;
        lock(&self.store).set_fields(
            &format!("pod_issue/{issue_id}"),
            &[
                ("fidelity", json!(effective_fidelity.key())),
                ("anchorCount", json!(anchor_count)),
                ("highlightSec", json!(highlight_sec)),
            ],
        )
    }

    pub fn podcast_transcript(&self, issue_id: String) -> Vec<MagazineSentence> {
        self.load_transcript_file(&issue_id).sentences
    }

    pub fn podcast_highlight_sentence_ids(&self, issue_id: String) -> Vec<String> {
        self.load_transcript_file(&issue_id).highlight_ids
    }

    /// Saves a highlight / audio micro-clip ("记这句 H" / "原声摘").
    #[allow(clippy::too_many_arguments)]
    pub fn podcast_add_clip(
        &self,
        issue_id: String,
        t0: f64,
        t1: f64,
        speaker: Option<String>,
        quote_text: String,
        thought: Option<String>,
        audio_rel_path: Option<String>,
    ) -> Result<PodcastClip> {
        let issue = self.podcast_issue(issue_id.clone())?;
        let id = fresh_id("clip");
        let now = now_ms();
        lock(&self.store).set_fields(
            &format!("pod_clip/{id}"),
            &[
                ("issueId", json!(issue_id)),
                ("issueTitle", json!(issue.title)),
                ("showTitle", json!(issue.show_title)),
                ("t0", json!(t0.max(0.0))),
                ("t1", json!(t1.max(t0))),
                ("speaker", json!(speaker)),
                ("quoteText", json!(quote_text)),
                ("thought", json!(thought)),
                ("audioRelPath", json!(audio_rel_path)),
                ("createdAt", json!(now)),
                ("deleted", json!(false)),
            ],
        )?;
        Ok(PodcastClip {
            id,
            issue_id,
            issue_title: issue.title,
            show_title: issue.show_title,
            t0: t0.max(0.0),
            t1: t1.max(t0),
            speaker,
            quote_text,
            thought,
            audio_rel_path,
            created_at: now,
        })
    }

    pub fn podcast_clips(&self, issue_id: Option<String>) -> Result<Vec<PodcastClip>> {
        let mut out = Vec::new();
        for (entity, f) in lock(&self.store).entities("pod_clip/")? {
            if f.get("deleted") == Some(&json!(true)) {
                continue;
            }
            let Some(id) = entity.strip_prefix("pod_clip/") else { continue };
            let cid = str_field(&f, "issueId");
            if let Some(want) = &issue_id {
                if &cid != want {
                    continue;
                }
            }
            out.push(PodcastClip {
                id: id.to_string(),
                issue_id: cid,
                issue_title: str_field(&f, "issueTitle"),
                show_title: str_field(&f, "showTitle"),
                t0: f64_field(&f, "t0"),
                t1: f64_field(&f, "t1"),
                speaker: opt_str(&f, "speaker"),
                quote_text: str_field(&f, "quoteText"),
                thought: opt_str(&f, "thought"),
                audio_rel_path: opt_str(&f, "audioRelPath"),
                created_at: i64_field(&f, "createdAt"),
            });
        }
        out.sort_by_key(|c| std::cmp::Reverse(c.created_at));
        Ok(out)
    }

    pub fn podcast_delete_clip(&self, clip_id: String) -> Result<()> {
        lock(&self.store).set(&format!("pod_clip/{clip_id}"), "deleted", json!(true))
    }

    /// Saves a cut-in Q&A side note ("插话") anchored at `at_sec`.
    pub fn podcast_add_interrupt(
        &self,
        issue_id: String,
        at_sec: f64,
        question: String,
        answer: String,
    ) -> Result<PodcastInterrupt> {
        let id = fresh_id("int");
        let now = now_ms();
        lock(&self.store).set_fields(
            &format!("pod_interrupt/{id}"),
            &[
                ("issueId", json!(issue_id)),
                ("atSec", json!(at_sec.max(0.0))),
                ("question", json!(question)),
                ("answer", json!(answer)),
                ("createdAt", json!(now)),
                ("deleted", json!(false)),
            ],
        )?;
        Ok(PodcastInterrupt { id, issue_id, at_sec: at_sec.max(0.0), question, answer, created_at: now })
    }

    pub fn podcast_interrupts(&self, issue_id: String) -> Result<Vec<PodcastInterrupt>> {
        let mut out = Vec::new();
        for (entity, f) in lock(&self.store).entities("pod_interrupt/")? {
            if f.get("deleted") == Some(&json!(true)) {
                continue;
            }
            let Some(id) = entity.strip_prefix("pod_interrupt/") else { continue };
            if str_field(&f, "issueId") != issue_id {
                continue;
            }
            out.push(PodcastInterrupt {
                id: id.to_string(),
                issue_id: issue_id.clone(),
                at_sec: f64_field(&f, "atSec"),
                question: str_field(&f, "question"),
                answer: str_field(&f, "answer"),
                created_at: i64_field(&f, "createdAt"),
            });
        }
        out.sort_by(|a, b| a.at_sec.partial_cmp(&b.at_sec).unwrap_or(std::cmp::Ordering::Equal));
        Ok(out)
    }

    /// Finds all podcast episodes that explicitly anchor or mention a book on the user's shelf,
    /// powering "你听过的节目里聊到过它" on `BookRecordPage`.
    pub fn podcast_mentions_for_book(&self, book_id: String, book_title: String) -> Result<Vec<BookPodcastMention>> {
        let issues = self.podcast_issues(None)?;
        let mut out = Vec::new();
        let needle = book_title.trim();
        for issue in issues {
            let direct = issue.book_ids.contains(&book_id);
            let transcript = self.load_transcript_file(&issue.id).sentences;
            if let Some(hit) = transcript.iter().find(|s| {
                s.book_id.as_deref() == Some(book_id.as_str())
                    || (!needle.is_empty() && s.text_orig.contains(needle))
            }) {
                out.push(BookPodcastMention {
                    issue_id: issue.id.clone(),
                    issue_title: issue.title.clone(),
                    show_title: issue.show_title.clone(),
                    snippet: hit.text_orig.chars().take(110).collect(),
                    at_sec: hit.t0,
                    studio_kind: issue.studio_kind,
                });
            } else if direct || (!needle.is_empty() && (issue.summary.contains(needle) || issue.shownotes.contains(needle))) {
                out.push(BookPodcastMention {
                    issue_id: issue.id.clone(),
                    issue_title: issue.title.clone(),
                    show_title: issue.show_title.clone(),
                    snippet: issue.summary.chars().take(110).collect(),
                    at_sec: 0.0,
                    studio_kind: issue.studio_kind,
                });
            }
        }
        Ok(out)
    }
}

#[cfg(test)]
mod tests {
    use crate::podcast_hub::clip::resolve_clip_window;
    use crate::podcast_hub::parse::{parse_podcast_opml, parse_podcast_rss, parse_podcast_transcript};
    use crate::podcast_hub::studio::{is_within_progress, strip_slop};

    #[test]
    fn parses_podcasting_2_rss_and_shownote_chapters() {
        let xml = r#"<?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd" xmlns:podcast="https://podcastindex.org/namespace/1.0">
          <channel>
            <title>The Long Table</title>
            <itunes:author>Dana Whitlock</itunes:author>
            <language>en</language>
            <itunes:image href="https://example.com/art.jpg"/>
            <item>
              <guid>ep-118</guid>
              <title>Who Pays for the City?</title>
              <pubDate>Thu, 14 Nov 2026 08:00:00 +0000</pubDate>
              <itunes:duration>1:42:06</itunes:duration>
              <itunes:episode>118</itunes:episode>
              <enclosure url="https://example.com/ep118.mp3" type="audio/mpeg" length="12345"/>
              <podcast:transcript url="https://example.com/ep118.vtt" type="text/vtt" language="en"/>
              <podcast:person role="guest">Lan Shen</podcast:person>
              <description>00:00 开场&#10;12:30 核心交锋：土地财政为什么会自我强化</description>
            </item>
          </channel>
        </rss>"#;
        let feed = parse_podcast_rss(xml.to_string()).expect("rss");
        assert_eq!(feed.title, "The Long Table");
        assert_eq!(feed.episodes.len(), 1);
        let ep = &feed.episodes[0];
        assert_eq!(ep.duration_sec, 6126);
        assert_eq!(ep.episode_number.as_deref(), Some("118"));
        assert_eq!(ep.transcripts.len(), 1);
        assert_eq!(ep.people.len(), 1);
        assert_eq!(ep.chapters.len(), 2);
        assert_eq!(ep.chapters[1].start_sec, 750.0);
    }

    #[test]
    fn parses_opml_and_deduplicates() {
        let opml = r#"<opml version="2.0"><body>
          <outline text="The Long Table" xmlUrl="https://a.test/rss.xml"/>
          <outline text="Dup" xmlUrl="https://a.test/rss.xml"/>
          <outline text="Evening Study" xmlUrl="https://b.test/feed"/>
        </body></opml>"#;
        let list = parse_podcast_opml(opml.to_string()).expect("opml");
        assert_eq!(list.len(), 2);
        assert_eq!(list[0].title, "The Long Table");
    }

    #[test]
    fn folds_ads_and_snaps_blind_clip_backtrack() {
        let vtt = "WEBVTT\n\n00:00:02.000 --> 00:00:08.000\n<v Host>This episode is brought to you by Acme VPN, use promo code TABLE.\n\n00:00:10.000 --> 00:00:18.000\n<v Whitlock>Nobody was being irrational; they were rewarded for the exact rule they got.\n";
        let sents = parse_podcast_transcript(vtt.to_string(), "text/vtt".to_string());
        assert_eq!(sents.len(), 2);
        assert_eq!(sents[0].skip_reason.as_deref(), Some("ad"));
        assert_eq!(sents[1].skip_reason, None);
        let clip = resolve_clip_window(sents, 19.5, 600.0);
        assert_eq!(clip.t0, 10.0);
        assert_eq!(clip.t1, 18.0);
        assert!(clip.quote_text.contains("Nobody was being irrational"));
    }

    #[test]
    fn non_spoiler_guard_and_slop_filter_work() {
        let limit = Some(crate::api::Position { section: 3, sentence: 12 });
        assert!(is_within_progress(2, 99, limit, 0.3, 10));
        assert!(is_within_progress(3, 12, limit, 0.3, 10));
        assert!(!is_within_progress(3, 13, limit, 0.3, 10));
        assert!(!is_within_progress(4, 0, limit, 0.3, 10));
        assert_eq!(strip_slop("大家好，欢迎来到今天的节目！今天直接看土地财政。"), "今天直接看土地财政。");
    }
}
