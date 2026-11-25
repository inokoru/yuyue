//! The reader's own views — the bubbles, the cast page, who reads each sentence — drawn from the
//! play, so the user's corrections and the model's answers show everywhere at once. The names
//! are the ones the cast shows (`DramaCharacter::shown`); a voice set under a name keeps working
//! as long as the cast still has that name.

use std::collections::HashMap;

use super::{CharacterGender, DramaSpeaker, SegKind};
use crate::api::{YuyueCore, lock};
use crate::error::Result;
use crate::roles::{BubbleLine, CastMember, avatar, color, role_for};

/// Two rows the model thought might be one person.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct SameSuggestion {
    pub a: String,
    pub b: String,
    /// Where the second of them first speaks.
    pub section: Option<u32>,
}

impl YuyueCore {
    /// Lines each character says in a section (by id), kept until the section changes.
    pub(crate) fn drama_who(&self, book: &str, section: u32) -> Result<HashMap<u32, u32>> {
        let key = (book.to_string(), section);
        if let Some(w) = lock(&self.drama).who.get(&key) {
            return Ok(w.clone());
        }
        let b = self.book_drama(book)?;
        let (decided, _, _) = self.decided(book, &b, section);
        let mut w: HashMap<u32, u32> = HashMap::new();
        for d in &decided {
            if let DramaSpeaker::Char { id } = d.speaker
                && !d.not_speech {
                    *w.entry(id).or_default() += 1;
                }
        }
        lock(&self.drama).who.insert(key, w.clone());
        Ok(w)
    }

    /// The cast page from the play: who speaks in `section` first, then those before; nobody
    /// after it.
    pub(crate) fn drama_cast_members(&self, book: &str, section: u32) -> Result<Vec<CastMember>> {
        let b = self.book_drama(book)?;
        let n = b.sentences.len() as u32;
        if n == 0 {
            return Ok(vec![]);
        }
        let upto = section.min(n - 1);
        let here = self.drama_who(book, upto)?;
        let mut so_far: HashMap<u32, u32> = HashMap::new();
        for s in 0..=upto {
            for (id, k) in self.drama_who(book, s)? {
                *so_far.entry(id).or_default() += k;
            }
        }
        let among: Vec<String> = b.cast.chars.iter().map(|c| c.shown.clone()).collect();
        let shown_of = |id: u32| b.cast.get(id).map(|c| c.shown.clone());
        let mut out: Vec<CastMember> = b
            .cast
            .chars
            .iter()
            .filter(|c| !c.narrator && so_far.get(&c.id).copied().unwrap_or(0) > 0)
            .map(|c| {
                let mut aliases: Vec<String> = c.names.iter().filter(|x| **x != c.shown).cloned().collect();
                aliases.sort_by_key(|a| std::cmp::Reverse(a.chars().count()));
                let maybe = b.maybe_same.iter().find_map(|(x, y)| if *x == c.id { Some(*y) } else if *y == c.id { Some(*x) } else { None });
                CastMember {
                    name: c.shown.clone(),
                    aliases,
                    lines_here: here.get(&c.id).copied().unwrap_or(0),
                    lines_so_far: so_far[&c.id],
                    avatar: avatar(&c.shown, &among),
                    color: color(&c.shown).to_string(),
                    gender: Some(c.gender),
                    maybe_same: maybe.filter(|o| so_far.get(o).copied().unwrap_or(0) > 0).and_then(shown_of),
                }
            })
            .collect();
        out.sort_by(|a, b| (b.lines_here > 0).cmp(&(a.lines_here > 0)).then(b.lines_here.cmp(&a.lines_here)).then(b.lines_so_far.cmp(&a.lines_so_far)).then(a.name.cmp(&b.name)));
        Ok(out)
    }

    /// The section's quotes for 气泡模式. A line nobody is sure of comes with the two people it
    /// most likely is, for the bubble to offer.
    pub(crate) fn drama_bubbles(&self, book: &str, section: u32) -> Result<Vec<BubbleLine>> {
        let ds = self.drama_section(book.to_string(), section)?;
        let b = self.book_drama(book)?;
        let roles = self.book_roles(book.to_string())?;
        let among: Vec<String> = b.cast.chars.iter().map(|c| c.shown.clone()).collect();
        let shown = |id: u32| b.cast.get(id).filter(|c| !c.narrator).map(|c| c.shown.clone());
        let mut out: Vec<BubbleLine> = Vec::new();
        let mut i = 0;
        let segs = &ds.segments;
        while i < segs.len() {
            let f = &segs[i];
            let Some(u) = f.utterance else {
                i += 1;
                continue;
            };
            let mut j = i;
            while j + 1 < segs.len() && segs[j + 1].utterance == Some(u) {
                j += 1;
            }
            let l = &segs[j];
            i = j + 1;
            let speaking = matches!(f.kind, SegKind::Speech | SegKind::Thought) || (f.kind == SegKind::Quoted && matches!(f.speaker, DramaSpeaker::Char { .. }));
            if !speaking {
                continue;
            }
            let (who, choices) = match &f.speaker {
                DramaSpeaker::Char { id } => match shown(*id) {
                    Some(n) => (Some(n), vec![]),
                    // The teller of a first-person book: their lines read as narration.
                    None => continue,
                },
                DramaSpeaker::Group { name } => (Some(name.clone()), vec![]),
                DramaSpeaker::Narrator => continue,
                DramaSpeaker::Unsure { top } => (None, top.iter().filter_map(|c| shown(c.id)).take(2).collect()),
            };
            let (avatar_s, color_s, right) = match &who {
                Some(n) => (avatar(n, &among), color(n).to_string(), role_for(&roles, n).is_some_and(|r| r.right)),
                None => ("?".into(), "#888888".into(), false),
            };
            let same = f.sentence == l.sentence;
            let first_s = b.sentences.get(section as usize).and_then(|ss| ss.get(f.sentence as usize));
            let open_quote = first_s.and_then(|s| s.chars().nth(f.start as usize)).is_some_and(|c| "“\"「『".contains(c));
            let close_quote = same
                && l.end > 0
                && first_s.and_then(|s| s.chars().nth((l.end - 1) as usize)).is_some_and(|c| "”\"」』".contains(c));
            let start = if open_quote { f.start + 1 } else { f.start };
            let end = if same {
                let e = if close_quote { l.end.saturating_sub(1) } else { l.end };
                e.max(start)
            } else {
                f.end
            };
            out.push(BubbleLine {
                first: f.sentence,
                last: l.sentence,
                start,
                end,
                who,
                avatar: avatar_s,
                color: color_s,
                right,
                choices,
                line_start: f.start,
            });
        }
        Ok(out)
    }

    /// Who reads each sentence: the cast name of the line in it, None for narration and lines
    /// nobody is sure of (the narrator's voice reads those).
    pub(crate) fn drama_sentence_roles(&self, book: &str, section: u32) -> Result<Vec<Option<String>>> {
        let ds = self.drama_section(book.to_string(), section)?;
        let b = self.book_drama(book)?;
        let n = b.sentences.get(section as usize).map(Vec::len).unwrap_or(0);
        let mut out = vec![None; n];
        for s in &ds.segments {
            if s.kind == SegKind::Narration {
                continue;
            }
            if let DramaSpeaker::Char { id } = s.speaker
                && let Some(c) = b.cast.get(id).filter(|c| !c.narrator)
                && let Some(slot) = out.get_mut(s.sentence as usize) {
                    *slot = Some(c.shown.clone());
                }
        }
        Ok(out)
    }
}

/// Someone to pick in “这句是谁说的”.
#[derive(Clone, Debug, PartialEq, uniffi::Record)]
pub struct CastPick {
    pub name: String,
    pub avatar: String,
    pub color: String,
}

#[uniffi::export]
impl YuyueCore {
    /// Fast check on book open: whether the book has enough dialogue (quoted, dash `——`, or
    /// script `人物：`) to offer dialogue features. Scans at most the first 12 sections without
    /// building a full-book cast or drama state, stopping as soon as 6 distinct utterances are found.
    pub fn book_has_dialogue(&self, book_id: String) -> Result<bool> {
        let n = self.manifest(&book_id)?.sections.len() as u32;
        let mut count = 0usize;
        for s in 0..n.min(12) {
            let sentences = match self.sentences(book_id.clone(), s) {
                Ok(v) => v,
                Err(crate::error::CoreError::NotFound { .. }) => continue,
                Err(e) => return Err(e),
            };
            let segs = super::spans::segments(&sentences, self.unquoted_lines_of(&book_id, s).as_deref());
            let mut seen = std::collections::HashSet::new();
            for seg in &segs {
                if matches!(seg.kind, SegKind::Speech | SegKind::Thought)
                    && let Some(u) = seg.utterance
                    && seen.insert(u)
                {
                    count += 1;
                    if count >= 6 {
                        return Ok(true);
                    }
                }
            }
        }
        Ok(false)
    }

    /// The people to offer for a line in `sentence`: those in its scene, most lines there first;
    /// a scene that doesn't say, everyone who speaks in the section.
    pub fn drama_line_people(&self, book_id: String, section: u32, sentence: u32) -> Result<Vec<CastPick>> {
        let b = self.book_drama(&book_id)?;
        let ds = self.drama_section(book_id.clone(), section)?;
        let scene = ds.scenes.iter().find(|s| s.first <= sentence && sentence <= s.last);
        let inside = |x: u32| scene.is_none_or(|s| s.first <= x && x <= s.last);
        let mut count: HashMap<u32, u32> = HashMap::new();
        for seg in &ds.segments {
            if let DramaSpeaker::Char { id } = seg.speaker
                && inside(seg.sentence) && seg.start == ds.segments.iter().find(|x| x.utterance == seg.utterance).map_or(seg.start, |x| x.start) {
                    *count.entry(id).or_default() += 1;
                }
        }
        let mut ids: Vec<u32> = scene.map(|s| s.present.clone()).unwrap_or_default();
        for id in count.keys() {
            if !ids.contains(id) {
                ids.push(*id);
            }
        }
        if ids.is_empty() {
            ids = self.drama_who(&book_id, section)?.into_keys().collect();
        }
        ids.sort_by_key(|id| (std::cmp::Reverse(count.get(id).copied().unwrap_or(0)), *id));
        let among: Vec<String> = b.cast.chars.iter().map(|c| c.shown.clone()).collect();
        Ok(ids
            .into_iter()
            .filter_map(|id| b.cast.get(id).filter(|c| !c.narrator))
            .map(|c| CastPick { name: c.shown.clone(), avatar: avatar(&c.shown, &among), color: color(&c.shown).to_string() })
            .collect())
    }

    /// Everyone in the cast met by `upto_section`, for 别的人物… (searched in the app).
    pub fn drama_everyone(&self, book_id: String, upto_section: u32) -> Result<Vec<CastPick>> {
        let b = self.book_drama(&book_id)?;
        let among: Vec<String> = b.cast.chars.iter().map(|c| c.shown.clone()).collect();
        let mut v: Vec<&super::DramaCharacter> = b.cast.chars.iter().filter(|c| !c.narrator && c.first_section.is_some_and(|f| f <= upto_section)).collect();
        v.sort_by(|a, b| b.lines.cmp(&a.lines).then(a.shown.cmp(&b.shown)));
        Ok(v.into_iter().map(|c| CastPick { name: c.shown.clone(), avatar: avatar(&c.shown, &among), color: color(&c.shown).to_string() }).collect())
    }

    /// “可能是同一个人”: pairs met by `upto_section`, the user not having said no.
    pub fn drama_same_suggestions(&self, book_id: String, upto_section: u32) -> Result<Vec<SameSuggestion>> {
        let b = self.book_drama(&book_id)?;
        Ok(b.maybe_same
            .iter()
            .filter_map(|(x, y)| {
                let (a, c) = (b.cast.get(*x)?, b.cast.get(*y)?);
                let met = |f: Option<u32>| f.is_some_and(|f| f <= upto_section);
                (met(a.first_section) && met(c.first_section)).then(|| SameSuggestion { a: a.shown.clone(), b: c.shown.clone(), section: c.first_section.max(a.first_section) })
            })
            .collect())
    }

    /// Someone's gender, as the cast holds it (the user's word wins).
    pub fn drama_gender(&self, book_id: String, name: String) -> Result<CharacterGender> {
        let b = self.book_drama(&book_id)?;
        Ok(b.cast.resolve(&name).and_then(|id| b.cast.get(id)).map(|c| c.gender).unwrap_or(CharacterGender::Unknown))
    }

    /// All of someone's names, the shown one first.
    pub fn drama_names(&self, book_id: String, name: String) -> Result<Vec<String>> {
        let b = self.book_drama(&book_id)?;
        let Some(c) = b.cast.resolve(&name).and_then(|id| b.cast.get(id)) else { return Ok(vec![]) };
        let mut v = vec![c.shown.clone()];
        let mut rest: Vec<String> = c.names.iter().filter(|n| **n != c.shown).cloned().collect();
        rest.sort_by_key(|n| n.chars().count());
        v.extend(rest);
        Ok(v)
    }
}

#[cfg(test)]
mod tests {
    use super::super::attribute::UserChoice;
    use super::super::api::CastEdit;
    use super::*;

    pub(super) fn open_book() -> (std::path::PathBuf, std::sync::Arc<YuyueCore>, String) {
        open()
    }

    fn open() -> (std::path::PathBuf, std::sync::Arc<YuyueCore>, String) {
        let dir = std::env::temp_dir().join(format!("yuyue-bridge-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/hongloumeng.epub")).unwrap();
        (dir, core, book.id)
    }

    fn section_of(core: &YuyueCore, book: &str, needle: &str) -> u32 {
        let n = core.manifest(book).unwrap().sections.len() as u32;
        (0..n).find(|&s| core.sentences(book.into(), s).unwrap_or_default().iter().any(|x| x.contains(needle))).unwrap()
    }

    #[test]
    fn bubbles_cast_and_corrections_come_from_the_play() {
        let (dir, core, book) = open();
        let s = section_of(&core, &book, "莫失莫忘");
        let bubbles = core.section_bubbles(book.clone(), s).unwrap();
        assert!(bubbles.len() > 50, "{}", bubbles.len());
        let sentences = core.sentences(book.clone(), s).unwrap();
        // Every bubble sits inside its quote marks.
        for b in &bubbles {
            let first: Vec<char> = sentences[b.first as usize].chars().collect();
            assert!("“\"「『".contains(first[b.line_start as usize]), "{:?}", b);
            assert_eq!(b.start, b.line_start + 1);
        }
        let cast = core.book_cast(book.clone(), s).unwrap();
        assert!(cast.iter().any(|m| m.name == "寶玉" && m.lines_here > 10), "{:?}", cast.iter().map(|m| &m.name).collect::<Vec<_>>());
        assert!(cast.iter().all(|m| m.name.chars().count() >= 2 || m.aliases.is_empty()));
        assert!(!cast.iter().any(|m| m.name.starts_with('他')), "a pronoun isn't a name");
        let fengjie = cast.iter().find(|m| m.name == "鳳姐儿").unwrap();
        assert_eq!(fengjie.maybe_same.as_deref(), Some("鳳姐"), "鳳姐儿 may be 鳳姐: asked, not joined");

        // The user names the speaker of a line: the bubble and the player both follow.
        let line = bubbles.iter().find(|b| b.who.as_deref() == Some("寶玉")).unwrap().clone();
        core.drama_set_speaker(book.clone(), s, line.first, line.line_start, UserChoice::Person { name: "寶釵".into() }).unwrap();
        let after = core.section_bubbles(book.clone(), s).unwrap();
        let moved = after.iter().find(|b| b.first == line.first && b.line_start == line.line_start).unwrap();
        assert_eq!(moved.who.as_deref(), Some("寶釵"));
        assert_eq!(core.section_roles(book.clone(), s).unwrap()[line.first as usize].as_deref(), Some("寶釵"));

        // 不是一个人物: their lines go back to nobody; a shown name the user picks sticks.
        core.drama_edit_cast(book.clone(), CastEdit::AddName { to: "寶玉".into(), name: "賈寶玉".into() }).unwrap();
        assert_eq!(core.drama_names(book.clone(), "寶玉".into()).unwrap(), vec!["寶玉".to_string(), "賈寶玉".to_string()]);
        core.drama_edit_cast(book.clone(), CastEdit::Show { name: "賈寶玉".into() }).unwrap();
        assert!(core.book_cast(book.clone(), s).unwrap().iter().any(|m| m.name == "賈寶玉"));
        core.drama_edit_cast(book.clone(), CastEdit::Remove { name: "鶯儿".into() }).unwrap();
        let cast = core.book_cast(book.clone(), s).unwrap();
        assert!(!cast.iter().any(|m| m.name == "鶯儿"));
        assert!(!core.section_bubbles(book.clone(), s).unwrap().iter().any(|b| b.who.as_deref() == Some("鶯儿")));
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn dash_dialogue_becomes_bubbles_with_names() {
        let dir = std::env::temp_dir().join(format!("yuyue-dash-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");
        let book = core.import_book(format!("{root}/../../fixtures/books/dash-dialogue.txt")).unwrap();
        let b0 = core.section_bubbles(book.id.clone(), 0).unwrap();
        assert!(b0.len() >= 4, "{b0:?}");
        let s0 = core.sentences(book.id.clone(), 0).unwrap();
        let texts: Vec<String> = b0
            .iter()
            .map(|b| s0[b.first as usize].chars().skip(b.start as usize).take((b.end - b.start) as usize).collect())
            .collect();
        assert!(texts.iter().any(|t| t.starts_with("——你又来晚了")), "{texts:?}");
        assert!(b0.iter().any(|b| b.who.as_deref() == Some("老周")), "{b0:?}");
        assert!(b0.iter().any(|b| b.who.as_deref() == Some("阿青")), "{b0:?}");
        let b1 = core.section_bubbles(book.id.clone(), 1).unwrap();
        assert!(b1.len() >= 2, "{b1:?}");
        assert!(core.book_has_dialogue(book.id.clone()).unwrap());
        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn book_has_dialogue_fixtures_and_non_dialogue_guards() {
        let dir = std::env::temp_dir().join(format!("yuyue-has-dlg-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");

        // Dialogue fixtures: quoted & dash/script books must all return true.
        for fixture in ["dash-dialogue.txt", "hongloumeng.epub", "great-gatsby.epub", "pride-and-prejudice.epub"] {
            let book = core.import_book(format!("{root}/../../fixtures/books/{fixture}")).unwrap();
            assert!(core.book_has_dialogue(book.id.clone()).unwrap(), "{fixture} should have dialogue");
        }

        // Non-dialogue EPUB & PDF fixtures must return Ok(false) cleanly (never NotFound on PDFs).
        for fixture in ["edge-cases.epub", "sample.pdf"] {
            let book = core.import_book(format!("{root}/../../fixtures/books/{fixture}")).unwrap();
            assert!(!core.book_has_dialogue(book.id.clone()).unwrap(), "{fixture} should not have dialogue");
        }
        let check_txt = |name: &str, body: &str| -> bool {
            let p = dir.join(name);
            std::fs::write(&p, body).unwrap();
            let book = core.import_book(p.to_string_lossy().into_owned()).unwrap();
            core.book_has_dialogue(book.id).unwrap()
        };

        // 1. Prose with mid-sentence `——` dashes in every paragraph -> false.
        let mut mid_dash = String::new();
        for ch in 1..=12 {
            mid_dash.push_str(&format!("第{ch}章 散文随笔\n\n"));
            for p in 0..8 {
                mid_dash.push_str(&format!(
                    "清晨的微风拂过湖面——水面泛起细碎的涟漪，远处传来钟声。行人走在青石板路上——这是第{ch}章第{p}段的记录，一切如常。\n\n"
                ));
            }
        }
        assert!(!check_txt("mid-dash.txt", &mid_dash), "mid-sentence dashes must not trigger book_has_dialogue");

        // 2. Prose with 1-3 leading `——` epigraphs/attributions per chapter across 12 chapters -> false.
        let mut epigraph_book = String::new();
        for ch in 1..=12 {
            epigraph_book.push_str(&format!("第{ch}章 随想\n\n"));
            epigraph_book.push_str("世上本没有路，走的人多了，也便成了路。\n——鲁迅\n\n");
            if ch % 2 == 0 {
                epigraph_book.push_str("学而不思则罔，思而不学则殆。\n——《论语》\n\n");
            }
            if ch % 3 == 0 {
                epigraph_book.push_str("人固有一死，或重于泰山，或轻于鸿毛。\n——摘自《史记》。\n\n");
            }
            for p in 0..8 {
                epigraph_book.push_str(&format!(
                    "山间的气候变化很快，午后常常飘来一阵细雨。第{ch}章第{p}段记载着四季更替与草木枯荣，并无人物交谈。\n\n"
                ));
            }
        }
        assert!(!check_txt("epigraphs.txt", &epigraph_book), "leading —— epigraphs must not trigger book_has_dialogue");

        // 3. Prose with occasional non-dialogue colons (注意：/例如：/总结：/第一：) -> false.
        let mut colon_prose = String::new();
        for ch in 1..=12 {
            colon_prose.push_str(&format!("第{ch}章 技术笔记\n\n"));
            colon_prose.push_str("注意：操作前请先备份原始数据。\n例如：在低温环境下电池容量会下降。\n总结：以上步骤适用于大多数场景。\n第一：检查电源连接是否稳固。\n第二：确认系统日志没有异常。\n也就是说：稳定运行取决于前期准备。\n\n");
            for p in 0..6 {
                colon_prose.push_str(&format!("本节继续说明第{ch}章第{p}项工程规范的细节与背景。\n\n"));
            }
        }
        assert!(!check_txt("colon-prose.txt", &colon_prose), "non-dialogue prose colons must not trigger book_has_dialogue");

        // 4. Plain prose without dialogue -> false.
        let mut plain = String::new();
        for ch in 1..=12 {
            plain.push_str(&format!("第{ch}章 风景\n\n"));
            for p in 0..6 {
                plain.push_str(&format!("秋日的傍晚格外安静，夕阳斜照在古老的城墙上，第{ch}章第{p}段只描写静谧的秋色。\n\n"));
            }
        }
        assert!(!check_txt("plain.txt", &plain), "plain prose must not trigger book_has_dialogue");

        std::fs::remove_dir_all(dir).ok();
    }

    #[test]
    fn book_has_dialogue_hongloumeng_speed_and_all_fixtures() {
        let dir = std::env::temp_dir().join(format!("yuyue-bench-dlg-{}", getrandom::u64().unwrap()));
        let core = YuyueCore::open(dir.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let root = env!("CARGO_MANIFEST_DIR");

        // Benchmark cold book_has_dialogue vs cold section_script vs cold section_bubbles on hongloumeng.epub.
        let hlm_path = format!("{root}/../../fixtures/books/hongloumeng.epub");
        let hlm = core.import_book(hlm_path.clone()).unwrap();
        let t0 = std::time::Instant::now();
        let has = core.book_has_dialogue(hlm.id.clone()).unwrap();
        let d_has = t0.elapsed();
        assert!(has);

        // Fresh core for uncached section_script timing.
        let dir2 = std::env::temp_dir().join(format!("yuyue-bench-sc-{}", getrandom::u64().unwrap()));
        let core2 = YuyueCore::open(dir2.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let hlm2 = core2.import_book(hlm_path.clone()).unwrap();
        let t1 = std::time::Instant::now();
        let sc = core2.section_script(hlm2.id.clone(), 2).unwrap();
        let d_script = t1.elapsed();
        assert!(!sc.lines.is_empty());

        // Fresh core for uncached section_bubbles timing.
        let dir3 = std::env::temp_dir().join(format!("yuyue-bench-bub-{}", getrandom::u64().unwrap()));
        let core3 = YuyueCore::open(dir3.to_string_lossy().into_owned(), "test".into(), "test".into()).unwrap();
        let hlm3 = core3.import_book(hlm_path).unwrap();
        let t2 = std::time::Instant::now();
        let bub = core3.section_bubbles(hlm3.id.clone(), 2).unwrap();
        let d_bubbles = t2.elapsed();
        assert!(!bub.is_empty());

        println!(
            "[BENCH hongloumeng.epub] book_has_dialogue={:?} | section_script={:?} | section_bubbles={:?}",
            d_has, d_script, d_bubbles
        );

        // Inspect all fixtures in fixtures/books/.
        let mut entries: Vec<_> = std::fs::read_dir(format!("{root}/../../fixtures/books"))
            .unwrap()
            .filter_map(|e| e.ok())
            .collect();
        entries.sort_by_key(|e| e.file_name());
        for entry in entries {
            let name = entry.file_name().to_string_lossy().into_owned();
            let path = entry.path().to_string_lossy().into_owned();
            match core.import_book(path) {
                Ok(b) => {
                    let t = std::time::Instant::now();
                    let res = core.book_has_dialogue(b.id);
                    println!("[FIXTURE {name}] book_has_dialogue={res:?} ({:?})", t.elapsed());
                }
                Err(e) => {
                    println!("[FIXTURE {name}] import_book error (expected for broken/empty): {e}");
                }
            }
        }

        std::fs::remove_dir_all(dir).ok();
        std::fs::remove_dir_all(dir2).ok();
        std::fs::remove_dir_all(dir3).ok();
    }
}

#[cfg(test)]
mod timing {
    #[test]
    #[ignore]
    fn how_long_the_cast_page_waits() {
        let (dir, core, book) = super::tests::open_book();
        for (what, f) in [
            ("section", Box::new(|| { core.drama_section(book.clone(), 9).unwrap(); }) as Box<dyn Fn()>),
            ("review", Box::new(|| { core.drama_review(book.clone(), 9).unwrap(); })),
            ("progress", Box::new(|| { core.drama_progress(book.clone()).unwrap(); })),
            ("cast", Box::new(|| { core.book_cast(book.clone(), 9).unwrap(); })),
        ] {
            let t = std::time::Instant::now();
            f();
            println!("{what}: {:?}", t.elapsed());
        }
        std::fs::remove_dir_all(dir).ok();
    }
}
