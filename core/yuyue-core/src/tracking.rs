//! 书的记录: what the reader keeps about a book besides reading it — a score, a line of their
//! own, tags — and books kept only as a record (a paper book, one read elsewhere), whose
//! progress is noted by hand.
//!
//! Registers: `book/<id>` · `rating` (1–5), `comment`, `record` (no files: only the record is
//! kept), `manual` (the progress noted by hand); `tag/<book>/<tag>` · `on`, one register per
//! pair as collection membership is, so tags added on two devices both survive a sync.

use std::collections::BTreeMap;

use serde_json::{Value, json};

use crate::api::{BookSummary, YuyueCore, lock, now_ms};
use crate::error::{CoreError, Result};
use crate::shelf::ReadingStatus;
use crate::store::Store;

/// What a hand-noted position counts in.
#[derive(Clone, Copy, Debug, PartialEq, Eq, uniffi::Enum)]
pub enum ProgressUnit {
    Page,
    Percent,
    Chapter,
}

impl ProgressUnit {
    fn key(self) -> &'static str {
        match self {
            ProgressUnit::Page => "page",
            ProgressUnit::Percent => "percent",
            ProgressUnit::Chapter => "chapter",
        }
    }

    fn from_key(key: &str) -> Option<Self> {
        Some(match key {
            "page" => ProgressUnit::Page,
            "percent" => ProgressUnit::Percent,
            "chapter" => ProgressUnit::Chapter,
            _ => return None,
        })
    }

    /// How far one tap of “+” goes before the reader has shown otherwise.
    fn first_step(self) -> u32 {
        match self {
            ProgressUnit::Page => 10,
            ProgressUnit::Percent => 5,
            ProgressUnit::Chapter => 1,
        }
    }

    /// The longest step “+” learns. A bigger jump is catching up, not a sitting's reading.
    fn longest_step(self) -> u32 {
        match self {
            ProgressUnit::Page => 50,
            ProgressUnit::Percent => 20,
            ProgressUnit::Chapter => 3,
        }
    }
}

/// 读到 148 / 320 页: progress noted by hand, for a book read somewhere the app can't see.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct ManualProgress {
    pub unit: ProgressUnit,
    pub value: u32,
    /// Pages or chapters in all; 100 for a percentage. 0 while unknown.
    pub total: u32,
    /// What “+” adds: the last step the reader took forward.
    pub step: u32,
    pub at: i64,
}

impl ManualProgress {
    pub(crate) fn fraction(&self) -> f64 {
        if self.total == 0 { 0.0 } else { (self.value as f64 / self.total as f64).min(1.0) }
    }
}

pub(crate) fn manual_of(fields: &BTreeMap<String, Value>) -> Option<ManualProgress> {
    let m = fields.get("manual")?;
    Some(ManualProgress {
        unit: ProgressUnit::from_key(m["unit"].as_str()?)?,
        value: m["value"].as_u64().unwrap_or(0) as u32,
        total: m["total"].as_u64().unwrap_or(0) as u32,
        step: m["step"].as_u64().unwrap_or(0) as u32,
        at: m["at"].as_i64().unwrap_or(0),
    })
}

pub(crate) fn rating_of(fields: &BTreeMap<String, Value>) -> Option<u8> {
    fields.get("rating").and_then(Value::as_u64).filter(|r| (1..=5).contains(r)).map(|r| r as u8)
}

pub(crate) fn is_record(fields: &BTreeMap<String, Value>) -> bool {
    fields.get("record") == Some(&json!(true))
}

/// A book's tags, in the order they were first put on.
pub(crate) fn tags_of(store: &Store, book: &str) -> Result<Vec<String>> {
    let prefix = format!("tag/{book}/");
    let mut tags: Vec<(String, String)> = store
        .entities(&prefix)?
        .into_iter()
        .filter(|(_, f)| f.get("on") == Some(&json!(true)))
        .filter_map(|(e, _)| e.strip_prefix(&prefix).map(|t| (e.clone(), t.to_string())))
        .map(|(e, tag)| {
            // The stamp of when it was first put on: in order even within one millisecond.
            let since = store.stamp_of(&e, "since").ok().flatten().unwrap_or_default();
            (since, tag)
        })
        .collect();
    tags.sort();
    Ok(tags.into_iter().map(|(_, t)| t).collect())
}

fn clean_line(text: &str, max: usize) -> String {
    text.split_whitespace().collect::<Vec<_>>().join(" ").chars().take(max).collect()
}

/// A tag is a few words; `/` would read as a new part of the register's name.
fn clean_tag(tag: &str) -> Result<String> {
    let tag = clean_line(&tag.replace('/', " "), 20);
    if tag.is_empty() {
        return Err(CoreError::Invalid { detail: crate::i18n::tr("标签要有字", "A tag needs a word") });
    }
    Ok(tag)
}

/// A fresh book id, shaped like the hash ids of imported books (32 hex digits).
fn record_id() -> String {
    let part = || getrandom::u64().unwrap_or_else(|_| now_ms() as u64 ^ ((std::process::id() as u64) << 40));
    format!("{:016x}{:016x}", part(), part())
}

#[uniffi::export]
impl YuyueCore {
    /// 加一本书: a book kept only as a record, by its title. The rest can come later.
    pub fn add_record_book(&self, title: String, authors: Vec<String>, status: ReadingStatus) -> Result<BookSummary> {
        let title = clean_line(&title, 200);
        if title.is_empty() {
            return Err(CoreError::Invalid { detail: crate::i18n::tr("书要有名字", "A book needs a title") });
        }
        let authors: Vec<String> = authors.iter().map(|a| clean_line(a, 100)).filter(|a| !a.is_empty()).collect();
        let id = record_id();
        lock(&self.store).set_fields(
            &format!("book/{id}"),
            &[
                ("title", json!(title)),
                ("authors", json!(authors)),
                ("record", json!(true)),
                ("addedAt", json!(now_ms())),
                ("deleted", json!(false)),
            ],
        )?;
        if status != ReadingStatus::Want {
            self.set_status(id.clone(), status)?;
        }
        self.summary(&id)
    }

    /// 只删文件，留下记录: the book's files go (on every device, at the next purge); its status,
    /// score, notes and dates stay, as a book kept only as a record. Importing the file again
    /// brings it back as it was.
    pub fn keep_record_only(&self, book: String) -> Result<BookSummary> {
        self.book_dir(&book)?;
        lock(&self.store).set_fields(&format!("book/{book}"), &[("record", json!(true)), ("recordAt", json!(now_ms()))])?;
        lock(&self.manifests).remove(&book);
        self.summary(&book)
    }

    /// 简介: what the book says about itself, when it says something (EPUB only).
    pub fn book_description(&self, book: String) -> Result<Option<String>> {
        let path = self.book_dir(&book)?.join("original.epub");
        if !path.is_file() {
            return Ok(None);
        }
        Ok(crate::epub::description(&path).unwrap_or(None))
    }

    /// 你的评分: 1–5 stars, or none.
    pub fn set_rating(&self, book: String, rating: Option<u8>) -> Result<()> {
        self.book_dir(&book)?;
        let value = match rating {
            Some(r) if (1..=5).contains(&r) => json!(r),
            Some(_) => return Err(CoreError::Invalid { detail: "rating".into() }),
            None => Value::Null,
        };
        lock(&self.store).set(&format!("book/{book}"), "rating", value)
    }

    /// 短评: a few lines of the reader's own. Empty takes it away.
    pub fn set_comment(&self, book: String, comment: Option<String>) -> Result<()> {
        self.book_dir(&book)?;
        let comment = comment.map(|c| c.trim().chars().take(2000).collect::<String>()).filter(|c| !c.is_empty());
        lock(&self.store).set(&format!("book/{book}"), "comment", comment.map_or(Value::Null, |c| json!(c)))
    }

    /// Puts a tag on a book, or takes it off.
    pub fn set_tag(&self, book: String, tag: String, on: bool) -> Result<()> {
        self.book_dir(&book)?;
        let tag = clean_tag(&tag)?;
        let entity = format!("tag/{book}/{tag}");
        let mut store = lock(&self.store);
        let mut fields = vec![("on", json!(on))];
        if on && store.get(&entity, "since")?.is_none() {
            fields.push(("since", json!(now_ms())));
        }
        store.set_fields(&entity, &fields)
    }

    /// Every tag in use, the most used first: what to offer when tagging another book.
    pub fn all_tags(&self) -> Result<Vec<String>> {
        let store = lock(&self.store);
        let live: std::collections::HashSet<String> = store
            .entities("book/")?
            .into_iter()
            .filter(|(_, f)| f.get("deleted") != Some(&json!(true)))
            .filter_map(|(e, _)| e.strip_prefix("book/").map(str::to_string))
            .collect();
        let mut counts: BTreeMap<String, u32> = BTreeMap::new();
        for (entity, f) in store.entities("tag/")? {
            let Some((book, tag)) = entity.strip_prefix("tag/").and_then(|r| r.split_once('/')) else { continue };
            if f.get("on") == Some(&json!(true)) && live.contains(book) {
                *counts.entry(tag.to_string()).or_default() += 1;
            }
        }
        let mut out: Vec<(u32, String)> = counts.into_iter().map(|(t, n)| (n, t)).collect();
        out.sort_by(|a, b| b.0.cmp(&a.0).then(a.1.cmp(&b.1)));
        Ok(out.into_iter().map(|(_, t)| t).collect())
    }

    /// 读到 148 / 320 页: notes how far a book kept as a record has got. Going forward
    /// remembers the step for “+”; a book still 想读 moves to 在读. Reaching the end doesn't
    /// make it 读过: the app asks.
    pub fn set_manual_progress(&self, book: String, unit: ProgressUnit, value: u32, total: u32) -> Result<ManualProgress> {
        self.book_dir(&book)?;
        let entity = format!("book/{book}");
        let (before, status) = {
            let store = lock(&self.store);
            let fields = store.fields(&entity)?;
            if fields.is_empty() {
                return Err(CoreError::not_found("book"));
            }
            (manual_of(&fields), crate::shelf::status(&fields, 0.0))
        };
        let total = if unit == ProgressUnit::Percent { 100 } else { total };
        let value = if total > 0 { value.min(total) } else { value };
        let step = match &before {
            Some(b) if b.unit == unit && value > b.value && value - b.value <= unit.longest_step() => value - b.value,
            Some(b) if b.unit == unit && b.step > 0 => b.step,
            _ => unit.first_step(),
        };
        let m = ManualProgress { unit, value, total, step, at: now_ms() };
        lock(&self.store).set(
            &entity,
            "manual",
            json!({ "unit": unit.key(), "value": m.value, "total": m.total, "step": m.step, "at": m.at }),
        )?;
        if status == ReadingStatus::Want && value > 0 {
            self.set_status(book, ReadingStatus::Reading)?;
        }
        Ok(m)
    }

    /// “+20 页”: one step further.
    pub fn step_manual_progress(&self, book: String) -> Result<ManualProgress> {
        let fields = lock(&self.store).fields(&format!("book/{book}"))?;
        let m = manual_of(&fields).ok_or_else(|| CoreError::not_found("progress"))?;
        let step = if m.step > 0 { m.step } else { m.unit.first_step() };
        let next = self.set_manual_progress(book, m.unit, m.value.saturating_add(step), m.total)?;
        // The step stays what it was, not what this tap made it.
        Ok(next)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn fixture(name: &str) -> String {
        format!("{}/../../fixtures/books/{name}", env!("CARGO_MANIFEST_DIR"))
    }

    fn temp(name: &str) -> String {
        let dir = std::env::temp_dir().join(format!("yuyue-tracking-{name}-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        dir.to_string_lossy().into_owned()
    }

    fn find(core: &YuyueCore, id: &str) -> BookSummary {
        core.books().unwrap().into_iter().find(|b| b.id == id).unwrap()
    }

    #[test]
    fn five_statuses_hand_chosen_wins() {
        let dir = temp("status");
        let core = YuyueCore::open(dir.clone(), "Mac".into(), "macos".into()).unwrap();
        let b = core.import_book(fixture("beiying.txt")).unwrap();
        assert_eq!(b.status, ReadingStatus::Want);
        assert_eq!(b.started_at, None);

        core.save_progress(b.id.clone(), crate::api::Position { section: 0, sentence: 0 }).unwrap();
        let read = find(&core, &b.id);
        assert_eq!(read.status, ReadingStatus::Reading);
        assert!(read.started_at.is_some(), "opening it records the day it was begun");

        core.set_status(b.id.clone(), ReadingStatus::Paused).unwrap();
        // Reading on doesn't take it out of 搁置: only the reader does.
        core.save_progress(b.id.clone(), crate::api::Position { section: 0, sentence: 3 }).unwrap();
        assert_eq!(find(&core, &b.id).status, ReadingStatus::Paused);

        core.set_status(b.id.clone(), ReadingStatus::Finished).unwrap();
        let done = find(&core, &b.id);
        assert_eq!(done.status, ReadingStatus::Finished);
        assert!(done.finished_at.is_some());
        core.set_status(b.id.clone(), ReadingStatus::Dropped).unwrap();
        let dropped = find(&core, &b.id);
        assert_eq!((dropped.status, dropped.finished_at), (ReadingStatus::Dropped, None));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn rating_comment_tags() {
        let dir = temp("notes");
        let core = YuyueCore::open(dir.clone(), "Mac".into(), "macos".into()).unwrap();
        let b = core.import_book(fixture("beiying.txt")).unwrap();
        core.set_rating(b.id.clone(), Some(4)).unwrap();
        assert!(core.set_rating(b.id.clone(), Some(6)).is_err());
        core.set_comment(b.id.clone(), Some("  父亲的背影  ".into())).unwrap();
        core.set_tag(b.id.clone(), "散文".into(), true).unwrap();
        core.set_tag(b.id.clone(), "二刷".into(), true).unwrap();
        core.set_tag(b.id.clone(), "a/b".into(), true).unwrap();
        assert!(core.set_tag(b.id.clone(), "  ".into(), true).is_err());
        let s = find(&core, &b.id);
        assert_eq!((s.rating, s.comment.as_deref()), (Some(4), Some("父亲的背影")));
        assert_eq!(s.tags, ["散文", "二刷", "a b"]);
        core.set_tag(b.id.clone(), "二刷".into(), false).unwrap();
        core.set_rating(b.id.clone(), None).unwrap();
        core.set_comment(b.id.clone(), Some(" ".into())).unwrap();
        let s = find(&core, &b.id);
        assert_eq!((s.rating, s.comment, s.tags.len()), (None, None, 2));
        assert_eq!(core.all_tags().unwrap().len(), 2);
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn paper_book_and_hand_noted_progress() {
        let dir = temp("paper");
        let core = YuyueCore::open(dir.clone(), "iPhone".into(), "ios".into()).unwrap();
        assert!(core.add_record_book("  ".into(), vec![], ReadingStatus::Want).is_err());
        let b = core.add_record_book(" 我与地坛 ".into(), vec!["史铁生".into()], ReadingStatus::Want).unwrap();
        assert_eq!((b.title.as_str(), b.record, b.available, b.status), ("我与地坛", true, false, ReadingStatus::Want));

        let m = core.set_manual_progress(b.id.clone(), ProgressUnit::Page, 20, 320).unwrap();
        assert_eq!((m.value, m.step), (20, 10));
        assert_eq!(find(&core, &b.id).status, ReadingStatus::Reading, "noting progress begins it");
        let m = core.set_manual_progress(b.id.clone(), ProgressUnit::Page, 40, 320).unwrap();
        assert_eq!(m.step, 20, "a sitting's reading becomes the step");
        let m = core.step_manual_progress(b.id.clone()).unwrap();
        assert_eq!((m.value, m.step), (60, 20));
        // Catching up a long way, or going back, keeps the step.
        let m = core.set_manual_progress(b.id.clone(), ProgressUnit::Page, 148, 320).unwrap();
        assert_eq!(m.step, 20);
        let m = core.set_manual_progress(b.id.clone(), ProgressUnit::Page, 140, 320).unwrap();
        assert_eq!(m.step, 20);
        let s = find(&core, &b.id);
        assert!((s.progress - 140.0 / 320.0).abs() < 1e-9);
        assert_eq!(s.manual.unwrap().total, 320);
        // Past the end stops at the end, and doesn't finish it.
        core.set_manual_progress(b.id.clone(), ProgressUnit::Page, 999, 320).unwrap();
        assert_eq!(find(&core, &b.id).status, ReadingStatus::Reading);

        let p = core.set_manual_progress(b.id.clone(), ProgressUnit::Percent, 30, 0).unwrap();
        assert_eq!((p.total, p.step), (100, 5));

        let later = core.add_record_book("边城".into(), vec![], ReadingStatus::Finished).unwrap();
        assert_eq!(later.status, ReadingStatus::Finished);
        assert!(later.finished_at.is_some());
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn keep_only_the_record() {
        let dir = temp("keep");
        let core = YuyueCore::open(dir.clone(), "Mac".into(), "macos".into()).unwrap();
        let b = core.import_book(fixture("beiying.txt")).unwrap();
        core.set_status(b.id.clone(), ReadingStatus::Finished).unwrap();
        core.set_rating(b.id.clone(), Some(5)).unwrap();
        let kept = core.keep_record_only(b.id.clone()).unwrap();
        assert_eq!((kept.record, kept.available, kept.rating), (true, false, Some(5)));
        assert_eq!(kept.title, b.title);
        assert_eq!(core.purge_deleted(0).unwrap(), 1, "the files go");
        let still = find(&core, &b.id);
        assert_eq!((still.status, still.rating), (ReadingStatus::Finished, Some(5)));
        // The same file again: the book comes back, its record with it.
        let again = core.import_book(fixture("beiying.txt")).unwrap();
        assert_eq!((again.record, again.available, again.rating), (false, true, Some(5)));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn records_and_tags_merge_across_devices() {
        let (da, db) = (temp("a"), temp("b"));
        let a = YuyueCore::open(da.clone(), "iPhone".into(), "ios".into()).unwrap();
        let b = YuyueCore::open(db.clone(), "Mac".into(), "macos".into()).unwrap();
        let x = a.add_record_book("活着".into(), vec!["余华".into()], ReadingStatus::Want).unwrap();
        b.import_changes(a.export_changes(b.sync_version().unwrap()).unwrap()).unwrap();
        a.set_tag(x.id.clone(), "小说".into(), true).unwrap();
        b.set_tag(x.id.clone(), "送人".into(), true).unwrap();
        b.set_rating(x.id.clone(), Some(5)).unwrap();
        b.import_changes(a.export_changes(b.sync_version().unwrap()).unwrap()).unwrap();
        a.import_changes(b.export_changes(a.sync_version().unwrap()).unwrap()).unwrap();
        for core in [&a, &b] {
            let s = find(core, &x.id);
            assert_eq!((s.record, s.rating, s.tags.len()), (true, Some(5), 2));
        }
        std::fs::remove_dir_all(&da).unwrap();
        std::fs::remove_dir_all(&db).unwrap();
    }
}

#[cfg(test)]
mod description_tests {
    use super::*;

    #[test]
    fn description_or_none() {
        let dir = std::env::temp_dir().join(format!("yuyue-desc-{}", std::process::id()));
        let _ = std::fs::remove_dir_all(&dir);
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "Mac".into(), "macos".into()).unwrap();
        let epub = core.import_book(format!("{}/../../fixtures/books/edge-cases.epub", env!("CARGO_MANIFEST_DIR"))).unwrap();
        let txt = core.import_book(format!("{}/../../fixtures/books/beiying.txt", env!("CARGO_MANIFEST_DIR"))).unwrap();
        assert!(core.book_description(epub.id).is_ok());
        assert_eq!(core.book_description(txt.id).unwrap(), None);
        std::fs::remove_dir_all(&dir).unwrap();
    }
}
