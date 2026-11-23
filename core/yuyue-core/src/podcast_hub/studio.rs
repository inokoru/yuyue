//! Private Studio ("书房电台") engine:
//! - Deterministic material extraction from the user's own books, highlights, and vocabulary
//!   with **physical non-spoiler truncation** (`section/sentence <= current reading progress`).
//! - Prompt builder for the 4 studio formats (`书房周刊`, `难书导读`, `跨书圆桌`, `生词外语`)
//!   honoring the user's chosen duration (`5 / 10 / 20` min) and optional topic.
//! - Anti-slop validator & anchor verifier + deterministic offline fallback script generator so
//!   studio creation still produces grounded episodes even without a cloud LLM configured.

use serde_json::Value;

use crate::api::{Position, YuyueCore};
use crate::error::Result;
use crate::podcast_hub::parse::{MagazineSentence, PodcastChapter};

/// The four Private Studio formats ("私人书房电台").
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash, serde::Serialize, serde::Deserialize, uniffi::Enum)]
pub enum StudioKind {
    /// 本周书房周刊
    WeeklyDigest,
    /// 难书与论文导读
    BookGuide,
    /// 跨书圆桌
    Roundtable,
    /// 生词伴听外语台
    VocabRadio,
}

impl StudioKind {
    pub fn key(self) -> &'static str {
        match self {
            StudioKind::WeeklyDigest => "weekly_digest",
            StudioKind::BookGuide => "book_guide",
            StudioKind::Roundtable => "roundtable",
            StudioKind::VocabRadio => "vocab_radio",
        }
    }

    pub fn from_key(s: &str) -> StudioKind {
        match s {
            "book_guide" => StudioKind::BookGuide,
            "roundtable" => StudioKind::Roundtable,
            "vocab_radio" => StudioKind::VocabRadio,
            _ => StudioKind::WeeklyDigest,
        }
    }

    pub fn label(self) -> &'static str {
        match self {
            StudioKind::WeeklyDigest => "本周书房周刊",
            StudioKind::BookGuide => "难书与论文导读",
            StudioKind::Roundtable => "跨书圆桌",
            StudioKind::VocabRadio => "生词伴听外语台",
        }
    }
}

/// One grounded excerpt pulled from a book strictly within the user's read progress.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct StudioBookSnippet {
    pub book_id: String,
    pub book_title: String,
    pub author: String,
    pub progress_pct: u8,
    pub section: u32,
    pub sentence: u32,
    pub quote: String,
    pub user_note: Option<String>,
}

/// One vocabulary item pulled from the user's 生词本.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct StudioWordItem {
    pub word: String,
    pub meaning: String,
    pub context: Option<String>,
    pub book_id: Option<String>,
    pub book_title: Option<String>,
}

/// Complete material packet handed to the LLM (or offline fallback), physically truncated so
/// unread chapters are never passed in.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct StudioBrief {
    pub kind: StudioKind,
    pub minutes: u32,
    pub topic: Option<String>,
    pub snippets: Vec<StudioBookSnippet>,
    pub words: Vec<StudioWordItem>,
    pub system_prompt: String,
    pub user_prompt: String,
}

/// Parsed & verified studio episode output ready to be saved to the Desk.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ParsedStudioDraft {
    pub title: String,
    pub subtitle: String,
    pub summary: String,
    pub chapters: Vec<PodcastChapter>,
    pub sentences: Vec<MagazineSentence>,
    pub anchor_count: u32,
}

const SLOP_PHRASES: &[&str] = &[
    "大家好，欢迎来到今天的节目",
    "各位听众朋友们大家好",
    "今天我们要聊一个非常有意思的话题",
    "让我们拭目以待",
    "希望本期节目能给大家带来启发",
    "欢迎在评论区告诉我们你的想法",
    "感谢您的收听，我们下期再见",
    "welcome to today's deep dive",
    "let's unpack this fascinating topic",
    "without further ado, let's dive right in",
];

/// Strips generic AI podcast opener/closer clichés ("反 AI 味").
pub fn strip_slop(text: &str) -> String {
    let mut out = text.trim().to_string();
    let lower = out.to_ascii_lowercase();
    for bad in SLOP_PHRASES {
        let bad_l = bad.to_ascii_lowercase();
        if lower.contains(&bad_l) {
            // Remove case-insensitively when ASCII, or exact match for CJK.
            if bad.is_ascii() {
                if let Some(pos) = out.to_ascii_lowercase().find(&bad_l) {
                    out.replace_range(pos..pos + bad.len(), "");
                }
            } else {
                out = out.replace(bad, "");
            }
        }
    }
    out.trim_start_matches(|c: char| c.is_whitespace() || c == '，' || c == ',' || c == '。' || c == '！' || c == '!')
        .trim()
        .to_string()
}

/// Checks whether a `(section, sentence)` lies strictly within the user's current read progress.
pub fn is_within_progress(section: u32, sentence: u32, limit: Option<Position>, book_progress: f64, total_sections: u32) -> bool {
    if book_progress >= 0.98 {
        return true;
    }
    if let Some(pos) = limit {
        return (section, sentence) <= (pos.section, pos.sentence);
    }
    if total_sections == 0 {
        return true;
    }
    let max_sec = ((total_sections as f64) * book_progress.max(0.05)).floor() as u32;
    section <= max_sec
}

#[uniffi::export]
impl YuyueCore {
    /// Collects strictly non-spoiler reading materials (snippets + marks + vocabulary) and builds
    /// the structured prompt for a Private Studio episode.
    pub fn build_studio_brief(
        &self,
        kind: StudioKind,
        book_ids: Vec<String>,
        minutes: u32,
        topic: Option<String>,
    ) -> Result<StudioBrief> {
        let minutes = minutes.clamp(3, 30);
        let all_books = self.books().unwrap_or_default();
        let chosen_books: Vec<_> = if book_ids.is_empty() {
            all_books
                .into_iter()
                .filter(|b| b.format != crate::api::BookFormat::Podcast)
                .take(3)
                .collect()
        } else {
            all_books.into_iter().filter(|b| book_ids.contains(&b.id)).collect()
        };

        let mut snippets = Vec::new();
        for b in &chosen_books {
            let author = b.authors.first().cloned().unwrap_or_default();
            let progress_pct = (b.progress * 100.0).round().clamp(0.0, 100.0) as u8;
            let limit = self.latest_progress(b.id.clone()).ok().flatten().map(|p| p.position);
            let total_sections = self.book(b.id.clone()).ok().map(|d| d.sections.len() as u32).unwrap_or(0);

            // 1. User's highlights strictly within read progress (physical anti-spoiler guard).
            let mut book_hits = 0usize;
            if let Ok(marks) = self.marks(b.id.clone()) {
                for m in marks.into_iter().rev() {
                    if !is_within_progress(m.section, m.start.sentence, limit, b.progress, total_sections) {
                        continue;
                    }
                    let q = m.text.trim();
                    if q.is_empty() {
                        continue;
                    }
                    snippets.push(StudioBookSnippet {
                        book_id: b.id.clone(),
                        book_title: b.title.clone(),
                        author: author.clone(),
                        progress_pct,
                        section: m.section,
                        sentence: m.start.sentence,
                        quote: q.chars().take(240).collect(),
                        user_note: m.note.clone(),
                    });
                    book_hits += 1;
                    if book_hits >= 4 {
                        break;
                    }
                }
            }

            // 2. If the book has no highlights yet, sample opening / read-so-far sentences (never past progress).
            if book_hits == 0 && b.available {
                if let Ok(detail) = self.book(b.id.clone()) {
                    let target_sec = limit.map(|p| p.section.min(detail.start + 1)).unwrap_or(detail.start);
                    if let Ok(sents) = self.sentences(b.id.clone(), target_sec) {
                        let cap_sent = limit
                            .filter(|p| p.section == target_sec)
                            .map(|p| (p.sentence as usize + 1).min(sents.len()))
                            .unwrap_or_else(|| sents.len().min(6));
                        for (idx, text) in sents.into_iter().take(cap_sent).enumerate() {
                            let clean = text.trim();
                            if clean.chars().count() < 16 {
                                continue;
                            }
                            snippets.push(StudioBookSnippet {
                                book_id: b.id.clone(),
                                book_title: b.title.clone(),
                                author: author.clone(),
                                progress_pct,
                                section: target_sec,
                                sentence: idx as u32,
                                quote: clean.chars().take(220).collect(),
                                user_note: None,
                            });
                            book_hits += 1;
                            if book_hits >= 2 {
                                break;
                            }
                        }
                    }
                }
            }
        }

        // Vocabulary items for VocabRadio or WeeklyDigest.
        let mut words = Vec::new();
        if matches!(kind, StudioKind::VocabRadio | StudioKind::WeeklyDigest) {
            if let Ok(all_words) = self.words() {
                let mut picked: Vec<_> = all_words.into_iter().filter(|w| !w.known).collect();
                picked.sort_by_key(|w| std::cmp::Reverse(w.at));
                for w in picked.into_iter().take(15) {
                    let book_id = if w.book.is_empty() { None } else { Some(w.book.clone()) };
                    let book_title = book_id
                        .as_ref()
                        .and_then(|id| self.book(id.clone()).ok().map(|d| d.summary.title));
                    let context = if w.context.trim().is_empty() { None } else { Some(w.context) };
                    words.push(StudioWordItem {
                        word: w.word,
                        meaning: w.meaning.unwrap_or_default(),
                        context,
                        book_id,
                        book_title,
                    });
                }
            }
        }

        let clean_topic = topic.map(|t| t.trim().to_string()).filter(|t| !t.is_empty());
        let target_turns = match minutes {
            0..=6 => 10,
            7..=12 => 16,
            _ => 24,
        };

        let system_prompt = format!(
            "你是语阅书房电台的专栏编辑与双人对谈编剧。输出克制、耐听、像高端音频杂志一样的播客对谈脚本。\n\
            硬性规则：\n\
            1. 严禁寒暄套话（禁止“大家好欢迎来到”“让我们拭目以待”“希望带来启发”等 AI 味开场白与结束语），第一句直接切入问题。\n\
            2. 严禁剧透：所有书目素材已按用户当前阅读进度物理截断，绝对不要提及素材以外的后续章节情节。\n\
            3. 每次引用原文或笔记时，必须附带给定的 book_id / section / sentence 锚点。\n\
            4. 仅输出合法 JSON，不要 markdown 代码块围栏，结构如下：\n\
            {{\"title\":\"精炼标题\",\"subtitle\":\"一句话提要\",\"chapters\":[{{\"title\":\"章节名\",\"turn_index\":0}}],\"turns\":[{{\"speaker\":\"主持名\",\"text\":\"台词\",\"trans\":\"可选中文翻译\",\"coach\":\"可选外语表达或概念点拨\",\"book_id\":null,\"book_title\":null,\"section\":null,\"sentence\":null,\"quote\":null}}]}}"
        );

        let mut user_prompt = format!(
            "节目类型：{}\n目标时长：约 {} 分钟（约 {} 轮自然对谈）\n",
            kind.label(),
            minutes,
            target_turns
        );
        if let Some(t) = &clean_topic {
            user_prompt.push_str(&format!("用户指定想聊的题目：{t}\n"));
        }
        match kind {
            StudioKind::WeeklyDigest => {
                user_prompt.push_str("任务：围绕用户本周在多本书里的划线、想法与关注线索，串联成一期私人《本周书房周刊》。\n");
            }
            StudioKind::BookGuide => {
                user_prompt.push_str("任务：为所选书目制作一期“读前热身 / 难书导读”，只讲时代背景、核心问题框架与阅读抓手，绝不剧透未读部分。\n");
            }
            StudioKind::Roundtable => {
                user_prompt.push_str("任务：让所选多本书的观点同桌交锋，找出彼此的共识与分歧。\n");
            }
            StudioKind::VocabRadio => {
                user_prompt.push_str("任务：用自然英语双人日常对话把下面生词自然串进真实语境（text 写英文，trans 写自然中文翻译，对重点词在 coach 里给一句简短地道点拨）。\n");
            }
        }

        if !snippets.is_empty() {
            user_prompt.push_str("\n【已读进度内书目素材】\n");
            for (i, s) in snippets.iter().enumerate() {
                user_prompt.push_str(&format!(
                    "{}. 《{}》（{}，进度 {}%，anchor: book_id={}, section={}, sentence={}）\n原文：{}\n",
                    i + 1,
                    s.book_title,
                    s.author,
                    s.progress_pct,
                    s.book_id,
                    s.section,
                    s.sentence,
                    s.quote
                ));
                if let Some(note) = &s.user_note {
                    user_prompt.push_str(&format!("读者想法：{note}\n"));
                }
            }
        }
        if !words.is_empty() {
            user_prompt.push_str("\n【待复习生词】\n");
            for w in &words {
                user_prompt.push_str(&format!("- {}：{}", w.word, w.meaning));
                if let Some(ctx) = &w.context {
                    user_prompt.push_str(&format!("（例句：{ctx}）"));
                }
                user_prompt.push('\n');
            }
        }

        Ok(StudioBrief { kind, minutes, topic: clean_topic, snippets, words, system_prompt, user_prompt })
    }

    /// Parses an LLM JSON response (or falls back deterministically to the brief's verified
    /// snippets/words when `raw_json` is empty or malformed), runs the anti-slop filter, verifies
    /// anchors, and computes estimated timestamps.
    pub fn finalize_studio_script(&self, brief: StudioBrief, raw_json: String) -> ParsedStudioDraft {
        if let Some(draft) = parse_llm_studio_json(&brief, &raw_json) {
            return draft;
        }
        offline_fallback_draft(&brief)
    }
}

fn parse_llm_studio_json(brief: &StudioBrief, raw: &str) -> Option<ParsedStudioDraft> {
    let trimmed = raw.trim();
    if trimmed.is_empty() {
        return None;
    }
    let start = trimmed.find('{')?;
    let end = trimmed.rfind('}')?;
    let val: Value = serde_json::from_str(&trimmed[start..=end]).ok()?;
    let turns = val.get("turns").and_then(Value::as_array)?;
    if turns.is_empty() {
        return None;
    }

    let title = val
        .get("title")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .unwrap_or_else(|| default_title(brief));
    let subtitle = val
        .get("subtitle")
        .and_then(Value::as_str)
        .map(str::trim)
        .filter(|s| !s.is_empty())
        .map(str::to_string)
        .unwrap_or_else(|| format!("{} · 约 {} 分钟", brief.kind.label(), brief.minutes));

    let valid_book_ids: std::collections::HashSet<&str> = brief.snippets.iter().map(|s| s.book_id.as_str()).collect();
    let mut sentences = Vec::new();
    let mut t = 0.0f64;
    let mut anchor_count = 0u32;

    for (idx, item) in turns.iter().enumerate() {
        let raw_text = item.get("text").and_then(Value::as_str).unwrap_or("");
        let text = strip_slop(raw_text);
        if text.is_empty() {
            continue;
        }
        let dur = estimate_speech_seconds(&text);
        let speaker = item.get("speaker").and_then(Value::as_str).map(|s| s.trim().to_string()).filter(|s| !s.is_empty());
        let text_trans = item.get("trans").and_then(Value::as_str).map(|s| s.trim().to_string()).filter(|s| !s.is_empty());
        let coach_note = item.get("coach").and_then(Value::as_str).map(|s| s.trim().to_string()).filter(|s| !s.is_empty());

        // Verify book anchor against the physical non-spoiler packet.
        let raw_bid = item.get("book_id").and_then(Value::as_str).filter(|id| valid_book_ids.contains(*id));
        let (book_id, book_title, book_section, book_sentence, book_quote) = if let Some(bid) = raw_bid {
            anchor_count += 1;
            let matched = brief.snippets.iter().find(|s| s.book_id == bid);
            (
                Some(bid.to_string()),
                item.get("book_title")
                    .and_then(Value::as_str)
                    .map(str::to_string)
                    .or_else(|| matched.map(|m| m.book_title.clone())),
                item.get("section").and_then(Value::as_u64).map(|n| n as u32).or_else(|| matched.map(|m| m.section)),
                item.get("sentence").and_then(Value::as_u64).map(|n| n as u32).or_else(|| matched.map(|m| m.sentence)),
                item.get("quote").and_then(Value::as_str).map(str::to_string).or_else(|| matched.map(|m| m.quote.clone())),
            )
        } else {
            (None, None, None, None, None)
        };

        sentences.push(MagazineSentence {
            id: format!("st{idx}"),
            speaker,
            text_orig: text,
            text_trans,
            coach_note,
            t0: t,
            t1: t + dur,
            skip_reason: None,
            book_id,
            book_title,
            book_section,
            book_sentence,
            book_quote,
        });
        t += dur + 0.35;
    }

    if sentences.is_empty() {
        return None;
    }

    let mut chapters = Vec::new();
    if let Some(chs) = val.get("chapters").and_then(Value::as_array) {
        for ch in chs {
            let ct = ch.get("title").and_then(Value::as_str).unwrap_or("").trim();
            let ti = ch.get("turn_index").and_then(Value::as_u64).unwrap_or(0) as usize;
            if !ct.is_empty() {
                let start_sec = sentences.get(ti).map(|s| s.t0).unwrap_or(0.0);
                chapters.push(PodcastChapter { title: ct.to_string(), start_sec, end_sec: None });
            }
        }
    }
    if chapters.is_empty() {
        chapters.push(PodcastChapter { title: "开篇切入".to_string(), start_sec: 0.0, end_sec: None });
    }

    let summary = subtitle.clone();
    Some(ParsedStudioDraft { title, subtitle, summary, chapters, sentences, anchor_count })
}

fn offline_fallback_draft(brief: &StudioBrief) -> ParsedStudioDraft {
    let title = default_title(brief);
    let mut sentences = Vec::new();
    let mut chapters = Vec::new();
    let mut t = 0.0f64;
    let mut anchor_count = 0u32;

    fn add_turn(
        sentences: &mut Vec<MagazineSentence>,
        t: &mut f64,
        anchor_count: &mut u32,
        speaker: &str,
        text: String,
        trans: Option<String>,
        coach: Option<String>,
        snippet: Option<&StudioBookSnippet>,
    ) {
        let dur = estimate_speech_seconds(&text);
        let id = format!("st{}", sentences.len());
        if snippet.is_some() {
            *anchor_count += 1;
        }
        sentences.push(MagazineSentence {
            id,
            speaker: Some(speaker.to_string()),
            text_orig: text,
            text_trans: trans,
            coach_note: coach,
            t0: *t,
            t1: *t + dur,
            skip_reason: None,
            book_id: snippet.map(|s| s.book_id.clone()),
            book_title: snippet.map(|s| s.book_title.clone()),
            book_section: snippet.map(|s| s.section),
            book_sentence: snippet.map(|s| s.sentence),
            book_quote: snippet.map(|s| s.quote.clone()),
        });
        *t += dur + 0.4;
    }

    chapters.push(PodcastChapter { title: "直接切入".to_string(), start_sec: 0.0, end_sec: None });

    match brief.kind {
        StudioKind::VocabRadio if !brief.words.is_empty() => {
            add_turn(
                &mut sentences,
                &mut t,
                &mut anchor_count,
                "CLARA",
                format!(
                    "We collected {} words from your reading desk this week. Let's weave them into a real conversation.",
                    brief.words.len()
                ),
                Some(format!("这周你从书和播客里捡到了 {} 个词，我们把它们放进自然对谈里复听一遍。", brief.words.len())),
                None,
                None,
            );
            for (i, w) in brief.words.iter().take(8).enumerate() {
                let sp = if i % 2 == 0 { "DAVID" } else { "CLARA" };
                let line = if let Some(ctx) = w.context.as_deref().filter(|s| !s.trim().is_empty()) {
                    ctx.trim().to_string()
                } else {
                    format!("Notice how \"{}\" changes the tone of the sentence when you read it aloud.", w.word)
                };
                let coach = if w.meaning.is_empty() {
                    Some(format!("重点词：{}", w.word))
                } else {
                    Some(format!("{} — {}", w.word, w.meaning))
                };
                add_turn(
                    &mut sentences,
                    &mut t,
                    &mut anchor_count,
                    sp,
                    line,
                    Some(format!("原词释义：{}（{}）。", w.word, w.meaning)),
                    coach,
                    None,
                );
            }
        }
        _ => {
            let lead = if let Some(topic) = &brief.topic {
                format!("今天我们围绕「{topic}」把你书桌上的线索摊开来对照一遍。")
            } else if let Some(first) = brief.snippets.first() {
                format!("先从你正在读的《{}》里这段停下来的地方说起。", first.book_title)
            } else {
                format!("这是一期按你书桌现状生成的{}。", brief.kind.label())
            };
            add_turn(&mut sentences, &mut t, &mut anchor_count, "林致", lead, None, None, None);

            if brief.snippets.is_empty() {
                add_turn(
                    &mut sentences,
                    &mut t,
                    &mut anchor_count,
                    "沈岚",
                    "书架里还没有足够划线素材时，只要先导入一本书或在书里划几句，下次就能直接带你串联原文锚点。".to_string(),
                    None,
                    None,
                    None,
                );
            } else {
                chapters.push(PodcastChapter { title: "书页对照".to_string(), start_sec: t, end_sec: None });
                for (i, sn) in brief.snippets.iter().take(6).enumerate() {
                    let sp = if i % 2 == 0 { "沈岚" } else { "林致" };
                    let quote_short: String = sn.quote.chars().take(90).collect();
                    let body = if let Some(note) = &sn.user_note {
                        format!("在《{}》（当前进度 {}%）里你划下这句：「{}」，并且记下了想法：「{}」。", sn.book_title, sn.progress_pct, quote_short, note)
                    } else {
                        format!("看《{}》（已读 {}%）这一处：「{}」。把这句话放回你最近在关注的问题里，逻辑马上就立起来了。", sn.book_title, sn.progress_pct, quote_short)
                    };
                    add_turn(&mut sentences, &mut t, &mut anchor_count, sp, body, None, None, Some(sn));
                }
            }
        }
    }

    let subtitle = if anchor_count > 0 {
        format!("{} · {} 处原文锚点 · 约 {} 分钟", brief.kind.label(), anchor_count, brief.minutes)
    } else {
        format!("{} · 约 {} 分钟", brief.kind.label(), brief.minutes)
    };
    let summary = brief
        .snippets
        .first()
        .map(|s| format!("围绕《{}》等已读进度内线索展开，绝不剧透未读章节。", s.book_title))
        .unwrap_or_else(|| subtitle.clone());

    ParsedStudioDraft { title, subtitle, summary, chapters, sentences, anchor_count }
}

fn default_title(brief: &StudioBrief) -> String {
    if let Some(t) = &brief.topic {
        return t.clone();
    }
    match brief.kind {
        StudioKind::WeeklyDigest => "本周书房周刊".to_string(),
        StudioKind::BookGuide => brief
            .snippets
            .first()
            .map(|s| format!("《{}》读前导读", s.book_title))
            .unwrap_or_else(|| "难书与论文导读".to_string()),
        StudioKind::Roundtable => {
            let titles: Vec<&str> = brief
                .snippets
                .iter()
                .map(|s| s.book_title.as_str())
                .collect::<std::collections::BTreeSet<_>>()
                .into_iter()
                .take(2)
                .collect();
            if titles.is_empty() {
                "跨书圆桌".to_string()
            } else {
                format!("{} 同桌对照", titles.iter().map(|t| format!("《{t}》")).collect::<String>())
            }
        }
        StudioKind::VocabRadio => format!("This week's {} words", brief.words.len().max(5)),
    }
}

fn estimate_speech_seconds(text: &str) -> f64 {
    let chars = text.chars().count();
    let is_mostly_ascii = text.bytes().filter(|b| b.is_ascii_alphabetic()).count() * 2 > text.len().max(1);
    if is_mostly_ascii {
        let words = text.split_whitespace().count().max(1);
        (words as f64 / 2.5).clamp(2.5, 22.0)
    } else {
        (chars as f64 / 4.2).clamp(2.5, 22.0)
    }
}
