//! Podcast RSS 2.0, Podcasting 2.0, OPML, SRT/WebVTT/JSON transcripts, and webpage metadata
//! parsers. Pure Rust, zero network I/O.

use roxmltree::Node;
use serde_json::Value;

use crate::error::{CoreError, Result};

/// A transcript file referenced by `<podcast:transcript>`.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct PodcastTranscriptLink {
    pub url: String,
    pub mime_type: String,
    pub language: Option<String>,
}

/// A chapter from `<podcast:chapters>` JSON or Podlove Simple Chapters.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize, uniffi::Record)]
pub struct PodcastChapter {
    pub title: String,
    pub start_sec: f64,
    pub end_sec: Option<f64>,
}

/// A person from `<podcast:person>`.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize, uniffi::Record)]
pub struct PodcastPerson {
    pub name: String,
    pub role: Option<String>,
}

/// One episode parsed from an external podcast RSS feed.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ParsedPodcastEpisode {
    pub guid: String,
    pub title: String,
    pub summary: String,
    pub shownotes: String,
    pub audio_url: String,
    pub audio_mime: String,
    pub duration_sec: u32,
    pub published_at: i64,
    pub artwork_url: Option<String>,
    pub episode_number: Option<String>,
    pub transcripts: Vec<PodcastTranscriptLink>,
    pub chapters_url: Option<String>,
    pub chapters: Vec<PodcastChapter>,
    pub people: Vec<PodcastPerson>,
}

/// A parsed external podcast RSS feed.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ParsedPodcastFeed {
    pub title: String,
    pub author: String,
    pub summary: String,
    pub language: String,
    pub link: Option<String>,
    pub artwork_url: Option<String>,
    pub episodes: Vec<ParsedPodcastEpisode>,
}

/// An OPML subscription outline.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct OpmlFeed {
    pub title: String,
    pub xml_url: String,
    pub html_url: Option<String>,
}

/// A single sentence parsed from SRT / WebVTT / Podcasting 2.0 JSON transcript.
#[derive(Clone, Debug, PartialEq, serde::Serialize, serde::Deserialize, uniffi::Record)]
pub struct MagazineSentence {
    pub id: String,
    pub speaker: Option<String>,
    pub text_orig: String,
    pub text_trans: Option<String>,
    pub coach_note: Option<String>,
    pub t0: f64,
    pub t1: f64,
    /// When set ("ad", "intro", "outro", "banter"), the reader folds consecutive skipped lines.
    pub skip_reason: Option<String>,
    /// Optional clickable anchor into a book on the user's shelf.
    pub book_id: Option<String>,
    pub book_title: Option<String>,
    pub book_section: Option<u32>,
    pub book_sentence: Option<u32>,
    pub book_quote: Option<String>,
}

/// Metadata extracted from a single-episode webpage HTML (og:*, audio tags, JSON-LD).
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ParsedWebEpisode {
    pub title: String,
    pub show_title: String,
    pub summary: String,
    pub audio_url: Option<String>,
    pub rss_url: Option<String>,
    pub artwork_url: Option<String>,
    pub duration_sec: u32,
}

fn child_text(node: Node<'_, '_>, name: &str) -> Option<String> {
    node.children()
        .find(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case(name))
        .and_then(|n| collect_text(n))
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
}

fn child_text_ns(node: Node<'_, '_>, local: &str, ns_hint: &str) -> Option<String> {
    node.children()
        .find(|n| {
            n.is_element()
                && n.tag_name().name().eq_ignore_ascii_case(local)
                && n.tag_name().namespace().is_some_and(|ns| ns.to_ascii_lowercase().contains(ns_hint))
        })
        .and_then(|n| collect_text(n))
        .map(|s| s.trim().to_string())
        .filter(|s| !s.is_empty())
}

fn collect_text(node: Node<'_, '_>) -> Option<String> {
    let mut out = String::new();
    for desc in node.descendants() {
        if desc.is_text() {
            if let Some(t) = desc.text() {
                out.push_str(t);
            }
        }
    }
    if out.trim().is_empty() { None } else { Some(out) }
}

/// Strips HTML tags and collapses excessive blank lines into clean readable plain text.
pub fn strip_html(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len());
    let mut in_tag = false;
    let mut tag_buf = String::new();
    for ch in raw.chars() {
        match ch {
            '<' => {
                in_tag = true;
                tag_buf.clear();
            }
            '>' if in_tag => {
                in_tag = false;
                let t = tag_buf.trim().to_ascii_lowercase();
                if t.starts_with("br") || t.starts_with("/p") || t.starts_with("/div") || t.starts_with("/li") || t.starts_with("p ") || t == "p" {
                    if !out.ends_with('\n') {
                        out.push('\n');
                    }
                }
            }
            _ if in_tag => tag_buf.push(ch),
            _ => out.push(ch),
        }
    }
    let decoded = decode_entities(&out);
    let mut lines = Vec::new();
    for line in decoded.lines() {
        let trimmed = line.split_whitespace().collect::<Vec<_>>().join(" ");
        if !trimmed.is_empty() {
            lines.push(trimmed);
        }
    }
    lines.join("\n")
}

fn decode_entities(s: &str) -> String {
    s.replace("&amp;", "&")
        .replace("&lt;", "<")
        .replace("&gt;", ">")
        .replace("&quot;", "\"")
        .replace("&#39;", "'")
        .replace("&apos;", "'")
        .replace("&nbsp;", " ")
        .replace("&#x27;", "'")
        .replace("&#8217;", "’")
        .replace("&#8220;", "“")
        .replace("&#8221;", "”")
        .replace("&#8212;", "—")
}

/// Parses `<itunes:duration>` ("1:42:06", "45:10", "3620") into seconds.
pub fn parse_duration_sec(raw: &str) -> u32 {
    let s = raw.trim();
    if s.is_empty() {
        return 0;
    }
    if let Ok(secs) = s.parse::<f64>() {
        return secs.max(0.0).round() as u32;
    }
    let parts: Vec<f64> = s.split(':').filter_map(|p| p.trim().parse::<f64>().ok()).collect();
    match parts.as_slice() {
        [m, sec] => (m * 60.0 + sec).max(0.0).round() as u32,
        [h, m, sec] => (h * 3600.0 + m * 60.0 + sec).max(0.0).round() as u32,
        _ => 0,
    }
}

/// Parses RFC 2822 / ISO 8601 date strings to Unix milliseconds (reusing `feed.rs` logic style).
pub fn parse_pubdate_ms(raw: &str) -> i64 {
    let s = raw.trim();
    if s.is_empty() {
        return 0;
    }
    // ISO 8601: YYYY-MM-DD(THH:MM:SS)?
    if s.len() >= 10 && s.as_bytes()[4] == b'-' && s.as_bytes()[7] == b'-' {
        if let (Some(Ok(y)), Some(Ok(m)), Some(Ok(d))) = (
            s.get(0..4).map(str::parse::<i64>),
            s.get(5..7).map(str::parse::<i64>),
            s.get(8..10).map(str::parse::<i64>),
        ) {
            let (hh, mm, ss) = if s.get(11..19).is_some_and(|t| t.is_ascii() && t.as_bytes()[2] == b':' && t.as_bytes()[5] == b':') {
                (
                    s.get(11..13).and_then(|x| x.parse::<i64>().ok()).unwrap_or(0),
                    s.get(14..16).and_then(|x| x.parse::<i64>().ok()).unwrap_or(0),
                    s.get(17..19).and_then(|x| x.parse::<i64>().ok()).unwrap_or(0),
                )
            } else {
                (0, 0, 0)
            };
            return ymd_hms_to_ms(y, m, d, hh, mm, ss);
        }
    }
    // RFC 2822: "Thu, 14 Nov 2026 08:30:00 +0000"
    let after_comma = s.split_once(',').map(|x| x.1).unwrap_or(s).trim();
    let toks: Vec<&str> = after_comma.split_whitespace().collect();
    if toks.len() >= 3 {
        let day = toks[0].parse::<i64>().ok();
        let mon = month_num(toks[1]);
        let year = toks[2].parse::<i64>().ok().map(|y| if y < 100 { 2000 + y } else { y });
        if let (Some(d), Some(m), Some(y)) = (day, mon, year) {
            let (hh, mm, ss) = toks
                .get(3)
                .map(|t| {
                    let p: Vec<i64> = t.split(':').filter_map(|x| x.parse().ok()).collect();
                    (p.first().copied().unwrap_or(0), p.get(1).copied().unwrap_or(0), p.get(2).copied().unwrap_or(0))
                })
                .unwrap_or((0, 0, 0));
            return ymd_hms_to_ms(y, m, d, hh, mm, ss);
        }
    }
    0
}

fn month_num(s: &str) -> Option<i64> {
    match s.get(0..3)?.to_ascii_lowercase().as_str() {
        "jan" => Some(1),
        "feb" => Some(2),
        "mar" => Some(3),
        "apr" => Some(4),
        "may" => Some(5),
        "jun" => Some(6),
        "jul" => Some(7),
        "aug" => Some(8),
        "sep" => Some(9),
        "oct" => Some(10),
        "nov" => Some(11),
        "dec" => Some(12),
        _ => None,
    }
}

fn ymd_hms_to_ms(y: i64, m: i64, d: i64, hh: i64, mm: i64, ss: i64) -> i64 {
    // Howard Hinnant's civil-from-days inverse.
    let y = y - if m <= 2 { 1 } else { 0 };
    let era = if y >= 0 { y } else { y - 399 } / 400;
    let yoe = y - era * 400;
    let mp = m + if m > 2 { -3 } else { 9 };
    let doy = (153 * mp + 2) / 5 + d - 1;
    let doe = yoe * 365 + yoe / 4 - yoe / 100 + doy;
    let days = era * 146097 + doe - 719468;
    let secs = days * 86400 + hh * 3600 + mm * 60 + ss;
    secs * 1000
}

fn artwork_of(node: Node<'_, '_>) -> Option<String> {
    for child in node.children().filter(|n| n.is_element()) {
        let name = child.tag_name().name();
        if name.eq_ignore_ascii_case("image") {
            if let Some(href) = child.attribute("href").filter(|s| !s.trim().is_empty()) {
                return Some(href.trim().to_string());
            }
            if let Some(url) = child_text(child, "url") {
                return Some(url);
            }
        }
        if name.eq_ignore_ascii_case("thumbnail") {
            if let Some(url) = child.attribute("url").filter(|s| !s.trim().is_empty()) {
                return Some(url.trim().to_string());
            }
        }
    }
    None
}

/// Parses a Podcast RSS 2.0 / Podcasting 2.0 XML document.
#[uniffi::export]
pub fn parse_podcast_rss(xml: String) -> Result<ParsedPodcastFeed> {
    let cleaned = xml.trim_start_matches('\u{feff}').trim();
    let doc = roxmltree::Document::parse(cleaned).map_err(|e| CoreError::bad_book(format!("RSS 解析失败：{e}")))?;
    let root = doc.root_element();
    let channel = root
        .children()
        .find(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("channel"))
        .unwrap_or(root);

    let title = child_text(channel, "title").unwrap_or_else(|| "未命名播客".to_string());
    let author = child_text_ns(channel, "author", "itunes")
        .or_else(|| child_text(channel, "author"))
        .or_else(|| child_text(channel, "managingEditor"))
        .unwrap_or_default();
    let summary = child_text_ns(channel, "summary", "itunes")
        .or_else(|| child_text(channel, "description"))
        .map(|s| strip_html(&s))
        .unwrap_or_default();
    let language = child_text(channel, "language").unwrap_or_else(|| "zh-Hans".to_string());
    let link = child_text(channel, "link");
    let artwork_url = artwork_of(channel);

    let mut episodes = Vec::new();
    for item in channel.children().filter(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("item")) {
        let enclosure = item.children().find(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("enclosure"));
        let Some(enc) = enclosure else { continue };
        let Some(audio_url) = enc.attribute("url").map(str::trim).filter(|s| !s.is_empty()) else { continue };
        let audio_mime = enc.attribute("type").unwrap_or("audio/mpeg").trim().to_string();

        let ep_title = child_text(item, "title").unwrap_or_else(|| "未命名单集".to_string());
        let guid = child_text(item, "guid").unwrap_or_else(|| audio_url.to_string());
        let raw_notes = child_text_ns(item, "encoded", "content")
            .or_else(|| child_text(item, "description"))
            .or_else(|| child_text_ns(item, "summary", "itunes"))
            .unwrap_or_default();
        let shownotes = strip_html(&raw_notes);
        let ep_summary = child_text_ns(item, "subtitle", "itunes")
            .or_else(|| child_text_ns(item, "summary", "itunes"))
            .map(|s| strip_html(&s))
            .filter(|s| !s.is_empty())
            .unwrap_or_else(|| shownotes.lines().next().unwrap_or("").chars().take(120).collect());

        let duration_sec = child_text_ns(item, "duration", "itunes")
            .or_else(|| child_text(item, "duration"))
            .map(|d| parse_duration_sec(&d))
            .unwrap_or(0);
        let published_at = child_text(item, "pubDate").map(|d| parse_pubdate_ms(&d)).unwrap_or(0);
        let ep_artwork = artwork_of(item).or_else(|| artwork_url.clone());
        let episode_number = child_text_ns(item, "episode", "itunes");

        let mut transcripts = Vec::new();
        let mut chapters_url = None;
        let mut chapters = Vec::new();
        let mut people = Vec::new();

        for child in item.children().filter(|n| n.is_element()) {
            let local = child.tag_name().name();
            if local.eq_ignore_ascii_case("transcript") {
                if let Some(url) = child.attribute("url").map(str::trim).filter(|s| !s.is_empty()) {
                    transcripts.push(PodcastTranscriptLink {
                        url: url.to_string(),
                        mime_type: child.attribute("type").unwrap_or("text/vtt").trim().to_string(),
                        language: child.attribute("language").map(|s| s.trim().to_string()),
                    });
                }
            } else if local.eq_ignore_ascii_case("chapters") {
                if let Some(url) = child.attribute("url").map(str::trim).filter(|s| !s.is_empty()) {
                    chapters_url = Some(url.to_string());
                } else {
                    // Podlove Simple Chapters: <psc:chapters><psc:chapter start="..." title="..."/>
                    for ch in child.children().filter(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("chapter")) {
                        if let (Some(start), Some(t)) = (ch.attribute("start"), ch.attribute("title")) {
                            chapters.push(PodcastChapter {
                                title: t.trim().to_string(),
                                start_sec: parse_timestamp_sec(start),
                                end_sec: None,
                            });
                        }
                    }
                }
            } else if local.eq_ignore_ascii_case("person") {
                if let Some(name) = collect_text(child).map(|s| s.trim().to_string()).filter(|s| !s.is_empty()) {
                    people.push(PodcastPerson {
                        name,
                        role: child.attribute("role").map(|s| s.trim().to_string()),
                    });
                }
            }
        }

        // Fallback: parse timestamps from shownotes when no explicit chapters exist.
        if chapters.is_empty() {
            chapters = extract_chapters_from_shownotes(&shownotes);
        }

        episodes.push(ParsedPodcastEpisode {
            guid,
            title: ep_title,
            summary: ep_summary,
            shownotes,
            audio_url: audio_url.to_string(),
            audio_mime,
            duration_sec,
            published_at,
            artwork_url: ep_artwork,
            episode_number,
            transcripts,
            chapters_url,
            chapters,
            people,
        });
    }

    Ok(ParsedPodcastFeed { title, author, summary, language, link, artwork_url, episodes })
}

/// Parses an OPML file (exported from Overcast, Apple Podcasts, Pocket Casts, Xiaoyuzhou, etc.).
#[uniffi::export]
pub fn parse_podcast_opml(xml: String) -> Result<Vec<OpmlFeed>> {
    let cleaned = xml.trim_start_matches('\u{feff}').trim();
    let doc = roxmltree::Document::parse(cleaned).map_err(|e| CoreError::bad_book(format!("OPML 解析失败：{e}")))?;
    let mut out = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for node in doc.descendants().filter(|n| n.is_element() && n.tag_name().name().eq_ignore_ascii_case("outline")) {
        let xml_url = node
            .attribute("xmlUrl")
            .or_else(|| node.attribute("xmlurl"))
            .map(str::trim)
            .filter(|s| !s.is_empty());
        let Some(url) = xml_url else { continue };
        if !seen.insert(url.to_string()) {
            continue;
        }
        let title = node
            .attribute("title")
            .or_else(|| node.attribute("text"))
            .map(str::trim)
            .filter(|s| !s.is_empty())
            .unwrap_or(url)
            .to_string();
        let html_url = node.attribute("htmlUrl").or_else(|| node.attribute("htmlurl")).map(|s| s.trim().to_string());
        out.push(OpmlFeed { title, xml_url: url.to_string(), html_url });
    }
    Ok(out)
}

/// Parses SRT, WebVTT, or Podcasting 2.0 JSON transcripts into `MagazineSentence`s,
/// automatically running the ad-folding rule engine over the result.
#[uniffi::export]
pub fn parse_podcast_transcript(content: String, mime_type: String) -> Vec<MagazineSentence> {
    let trimmed = content.trim_start_matches('\u{feff}').trim();
    let mut sentences = if mime_type.contains("json") || trimmed.starts_with('{') || trimmed.starts_with('[') {
        parse_json_transcript(trimmed)
    } else {
        parse_vtt_or_srt(trimmed)
    };
    crate::podcast_hub::adfold::mark_skippable_sentences(&mut sentences);
    sentences
}

fn parse_json_transcript(raw: &str) -> Vec<MagazineSentence> {
    let Ok(val) = serde_json::from_str::<Value>(raw) else { return Vec::new() };
    let segments = val
        .get("segments")
        .and_then(Value::as_array)
        .or_else(|| val.as_array())
        .cloned()
        .unwrap_or_default();
    let mut out = Vec::new();
    for (idx, seg) in segments.into_iter().enumerate() {
        let text = seg.get("body").or_else(|| seg.get("text")).and_then(Value::as_str).unwrap_or("").trim();
        if text.is_empty() {
            continue;
        }
        let t0 = seg.get("startTime").or_else(|| seg.get("start")).and_then(Value::as_f64).unwrap_or(0.0);
        let t1 = seg.get("endTime").or_else(|| seg.get("end")).and_then(Value::as_f64).unwrap_or(t0 + 3.0);
        let speaker = seg.get("speaker").and_then(Value::as_str).map(|s| s.trim().to_string()).filter(|s| !s.is_empty());
        out.push(MagazineSentence {
            id: format!("s{idx}"),
            speaker,
            text_orig: text.to_string(),
            text_trans: None,
            coach_note: None,
            t0,
            t1,
            skip_reason: None,
            book_id: None,
            book_title: None,
            book_section: None,
            book_sentence: None,
            book_quote: None,
        });
    }
    out
}

fn parse_vtt_or_srt(raw: &str) -> Vec<MagazineSentence> {
    let mut out = Vec::new();
    let lines: Vec<&str> = raw.lines().collect();
    let mut i = 0;
    let mut idx = 0usize;
    while i < lines.len() {
        let line = lines[i].trim();
        if line.contains("-->") {
            let parts: Vec<&str> = line.split("-->").collect();
            let t0 = parse_timestamp_sec(parts.first().copied().unwrap_or("0"));
            let t1 = parse_timestamp_sec(parts.get(1).copied().unwrap_or("0").split_whitespace().next().unwrap_or("0"));
            i += 1;
            let mut body_lines = Vec::new();
            while i < lines.len() && !lines[i].trim().is_empty() {
                body_lines.push(lines[i].trim());
                i += 1;
            }
            let joined = body_lines.join(" ");
            let (speaker, text) = split_speaker(&joined);
            if !text.is_empty() {
                out.push(MagazineSentence {
                    id: format!("s{idx}"),
                    speaker,
                    text_orig: text,
                    text_trans: None,
                    coach_note: None,
                    t0,
                    t1: t1.max(t0),
                    skip_reason: None,
                    book_id: None,
                    book_title: None,
                    book_section: None,
                    book_sentence: None,
                    book_quote: None,
                });
                idx += 1;
            }
        } else {
            i += 1;
        }
    }
    out
}

fn split_speaker(raw: &str) -> (Option<String>, String) {
    // WebVTT `<v Speaker Name>Text`
    if let Some(rest) = raw.strip_prefix("<v ") {
        if let Some((sp, text)) = rest.split_once('>') {
            let clean = strip_html(text).trim().to_string();
            return (Some(sp.trim().to_string()), clean);
        }
    }
    let clean = strip_html(raw);
    if let Some((left, right)) = clean.split_once(": ") {
        if !left.is_empty() && left.chars().count() <= 24 && !left.contains('.') {
            return (Some(left.trim().to_string()), right.trim().to_string());
        }
    }
    if let Some((left, right)) = clean.split_once("：") {
        if !left.is_empty() && left.chars().count() <= 16 && !left.contains('。') {
            return (Some(left.trim().to_string()), right.trim().to_string());
        }
    }
    (None, clean.trim().to_string())
}

pub fn parse_timestamp_sec(raw: &str) -> f64 {
    let s = raw.trim().replace(',', ".");
    let parts: Vec<f64> = s.split(':').filter_map(|p| p.trim().parse::<f64>().ok()).collect();
    match parts.as_slice() {
        [sec] => sec.max(0.0),
        [m, sec] => (m * 60.0 + sec).max(0.0),
        [h, m, sec] => (h * 3600.0 + m * 60.0 + sec).max(0.0),
        _ => 0.0,
    }
}

/// Extracts chapters from shownote lines like `00:12:30 为什么地方政府依赖土地财政`.
pub fn extract_chapters_from_shownotes(notes: &str) -> Vec<PodcastChapter> {
    let mut out = Vec::new();
    for line in notes.lines() {
        let trimmed = line.trim();
        let Some(first) = trimmed.split_whitespace().next() else { continue };
        let clean_ts = first.trim_matches(|c| c == '[' || c == ']' || c == '(' || c == ')');
        if !clean_ts.contains(':') || !clean_ts.chars().all(|c| c.is_ascii_digit() || c == ':') {
            continue;
        }
        let sec = parse_timestamp_sec(clean_ts);
        let rest = trimmed[first.len()..].trim().trim_start_matches(|c| c == '-' || c == '—' || c == ':' || c == '·' || c == ' ');
        if rest.chars().count() >= 2 {
            out.push(PodcastChapter { title: rest.to_string(), start_sec: sec, end_sec: None });
        }
    }
    out
}

/// Extracts metadata from a single-episode webpage (Xiaoyuzhou, Apple Podcasts, Substack, etc.).
#[uniffi::export]
pub fn parse_episode_webpage(html: String, page_url: String) -> ParsedWebEpisode {
    let title = meta_content(&html, "og:title")
        .or_else(|| meta_content(&html, "twitter:title"))
        .or_else(|| tag_inner(&html, "title"))
        .unwrap_or_else(|| page_url.clone());
    let show_title = meta_content(&html, "og:site_name").unwrap_or_else(|| "单集导入".to_string());
    let summary = meta_content(&html, "og:description")
        .or_else(|| meta_content(&html, "description"))
        .unwrap_or_default();
    let artwork_url = meta_content(&html, "og:image");
    let audio_url = meta_content(&html, "og:audio")
        .or_else(|| meta_content(&html, "og:audio:secure_url"))
        .or_else(|| find_audio_url_in_html(&html));
    let rss_url = find_rss_link_in_html(&html);
    ParsedWebEpisode { title, show_title, summary, audio_url, rss_url, artwork_url, duration_sec: 0 }
}

fn meta_content(html: &str, key: &str) -> Option<String> {
    let lower = html.to_ascii_lowercase();
    let needle = key.to_ascii_lowercase();
    let mut start = 0;
    while let Some(pos) = lower[start..].find("<meta") {
        let abs = start + pos;
        let Some(end) = html[abs..].find('>') else { break };
        let tag = &html[abs..abs + end + 1];
        let tag_l = tag.to_ascii_lowercase();
        if tag_l.contains(&format!("\"{needle}\"")) || tag_l.contains(&format!("'{needle}'")) {
            if let Some(val) = attr_value(tag, "content") {
                return Some(decode_entities(&val).trim().to_string());
            }
        }
        start = abs + end + 1;
    }
    None
}

fn tag_inner(html: &str, tag: &str) -> Option<String> {
    let lower = html.to_ascii_lowercase();
    let open = format!("<{tag}");
    let close = format!("</{tag}>");
    let start = lower.find(&open)?;
    let body_start = start + html[start..].find('>')? + 1;
    let body_end = body_start + lower[body_start..].find(&close)?;
    Some(decode_entities(&html[body_start..body_end]).trim().to_string())
}

fn attr_value(tag: &str, attr: &str) -> Option<String> {
    let lower = tag.to_ascii_lowercase();
    let idx = lower.find(&format!("{attr}="))?;
    let rest = tag[idx + attr.len() + 1..].trim_start();
    let quote = rest.chars().next()?;
    if quote == '"' || quote == '\'' {
        let inner = &rest[1..];
        let end = inner.find(quote)?;
        Some(inner[..end].to_string())
    } else {
        let end = rest.find(|c: char| c.is_whitespace() || c == '>').unwrap_or(rest.len());
        Some(rest[..end].to_string())
    }
}

fn find_audio_url_in_html(html: &str) -> Option<String> {
    for ext in [".mp3", ".m4a", ".aac", ".ogg"] {
        if let Some(idx) = html.find(ext) {
            let prefix = &html[..idx + ext.len()];
            if let Some(http_idx) = prefix.rfind("http") {
                let tail = &html[http_idx..];
                let end = tail.find(|c: char| c == '"' || c == '\'' || c == '<' || c.is_whitespace()).unwrap_or(tail.len());
                let candidate = &tail[..end];
                if candidate.starts_with("http://") || candidate.starts_with("https://") {
                    return Some(candidate.to_string());
                }
            }
        }
    }
    None
}

fn find_rss_link_in_html(html: &str) -> Option<String> {
    let lower = html.to_ascii_lowercase();
    let mut start = 0;
    while let Some(pos) = lower[start..].find("<link") {
        let abs = start + pos;
        let Some(end) = html[abs..].find('>') else { break };
        let tag = &html[abs..abs + end + 1];
        let tag_l = tag.to_ascii_lowercase();
        if tag_l.contains("application/rss+xml") || tag_l.contains("application/atom+xml") {
            if let Some(href) = attr_value(tag, "href") {
                return Some(href);
            }
        }
        start = abs + end + 1;
    }
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parse_pubdate_handles_non_ascii_without_panicking() {
        // Previously panicked slicing across multibyte UTF-8 characters.
        assert!(parse_pubdate_ms("2026-11-24 下午3点") > 0);
        assert!(parse_pubdate_ms("2026-11-24（周二）") > 0);
        assert_eq!(parse_pubdate_ms("2026-11-一"), 0);
        assert_eq!(parse_pubdate_ms("2026-11-24T12:34:56Z"), ymd_hms_to_ms(2026, 11, 24, 12, 34, 56));
    }
}
