//! 人物表: who speaks in a book, under all the names they go by.
//!
//! Without a model, the names come from the rules (`script::Cast`: the few characters before a
//! speech verb) and only sure merges are made: a full name and its short one (贾宝玉 = 宝玉) when
//! the short one ends no other name in the book (甄宝玉 keeps 宝玉 apart). Two people made one is
//! far worse than one person in two rows, so nothing else is merged without the user.
//!
//! With a model, it reads the whole book once and answers with every speaker, their names, and
//! a quote from the book for each claim. Here nothing is taken on its word: a quote has to be
//! found in the book, a merge has to have both names in its quote, a name two people claim
//! proves neither.

use std::collections::{HashMap, HashSet};

use aho_corasick::{AhoCorasick, MatchKind};
use serde::{Deserialize, Serialize};

use super::{CharacterAge, DramaCharacter, CharacterGender};

/// Titles and kin words: who they mean depends on who says them, so they're never a name.
const KIN: &[&str] = &[
    "母亲", "母親", "父亲", "父親", "老爷", "老爺", "太太", "奶奶", "爷爷", "爺爺", "师父", "師父", "师傅", "師傅", "姐姐", "哥哥", "妹妹", "弟弟", "嫂子", "姑娘", "公子", "夫人",
    "娘", "爹", "妈", "媽", "老祖宗", "二爷", "二爺", "大爷", "大爺", "小姐", "相公", "先生", "Mother", "Father", "Sir", "Madam", "Mama", "Papa",
];

/// The cast of a book with the index that finds its names in text.
#[derive(Clone, Debug, Default, Serialize, Deserialize)]
pub struct CastTable {
    pub chars: Vec<DramaCharacter>,
    /// Names more than one character claims: they prove nobody.
    pub ambiguous: Vec<String>,
    /// Who calls whom what; kin words live here, never among names.
    #[serde(default)]
    pub called_by: Vec<CalledBy>,
    #[serde(skip)]
    index: Option<NameIndex>,
}

#[derive(Clone, Debug)]
struct NameIndex {
    ac: AhoCorasick,
    /// For each pattern, the character it names, or None when ambiguous.
    who: Vec<Option<u32>>,
}

/// A name found in a text.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Hit {
    pub id: u32,
    /// Characters, not bytes.
    pub start: usize,
    pub end: usize,
}

impl CastTable {
    pub fn new(chars: Vec<DramaCharacter>) -> CastTable {
        let mut t = CastTable { chars, ambiguous: Vec::new(), called_by: Vec::new(), index: None };
        t.reindex();
        t
    }

    /// Rebuilds the name index after the characters changed.
    pub fn reindex(&mut self) {
        let mut owners: HashMap<String, HashSet<u32>> = HashMap::new();
        for c in &self.chars {
            for n in &c.names {
                owners.entry(n.clone()).or_default().insert(c.id);
            }
        }
        let mut names: Vec<(String, Option<u32>)> = owners.into_iter().map(|(n, ids)| (n, if ids.len() == 1 { ids.into_iter().next() } else { None })).collect();
        names.sort_by(|a, b| b.0.chars().count().cmp(&a.0.chars().count()).then(a.0.cmp(&b.0)));
        self.ambiguous = names.iter().filter(|n| n.1.is_none()).map(|n| n.0.clone()).collect();
        let pats: Vec<&str> = names.iter().map(|n| n.0.as_str()).collect();
        let ac = AhoCorasick::builder().match_kind(MatchKind::LeftmostLongest).build(&pats).expect("names build");
        self.index = Some(NameIndex { ac, who: names.into_iter().map(|n| n.1).collect() });
    }

    fn index(&self) -> &NameIndex {
        self.index.as_ref().expect("cast index")
    }

    /// Every unambiguous name in `text`, leftmost-longest.
    pub fn find(&self, text: &str) -> Vec<Hit> {
        let ix = self.index();
        if self.chars.is_empty() {
            return Vec::new();
        }
        ix.ac
            .find_iter(text)
            .filter_map(|m| {
                let id = ix.who[m.pattern().as_usize()]?;
                Some(Hit { id, start: text[..m.start()].chars().count(), end: text[..m.end()].chars().count() })
            })
            .collect()
    }

    /// The character a name means: an exact name, or the one whose name it ends, when only one.
    /// A verb stuck to its end (寶玉勸) is let go.
    pub fn resolve(&self, name: &str) -> Option<u32> {
        if self.ambiguous.iter().any(|a| a == name) {
            return None;
        }
        if let Some(last) = name.chars().last()
            && STUCK_AFTER.contains(last) && !GROUP_AFTER.contains(last) && name.chars().count() >= 3 {
                let head: String = name.chars().take(name.chars().count() - 1).collect();
                if let Some(id) = self.chars.iter().find(|c| c.names.contains(&head)).map(|c| c.id) {
                    return Some(id);
                }
            }
        if let Some(c) = self.chars.iter().find(|c| c.names.iter().any(|n| n == name)) {
            return Some(c.id);
        }
        let n = name.chars().count();
        if n < 2 {
            return None;
        }
        let ends: Vec<u32> = self.chars.iter().filter(|c| c.names.iter().any(|x| x.ends_with(name) || (name.ends_with(x.as_str()) && x.chars().count() >= 2))).map(|c| c.id).collect();
        (ends.len() == 1).then(|| ends[0])
    }

    pub fn get(&self, id: u32) -> Option<&DramaCharacter> {
        self.chars.iter().find(|c| c.id == id)
    }

    pub fn get_mut(&mut self, id: u32) -> Option<&mut DramaCharacter> {
        self.chars.iter_mut().find(|c| c.id == id)
    }

    fn next_id(&self) -> u32 {
        self.chars.iter().map(|c| c.id + 1).max().unwrap_or(1)
    }

    /// Adds a character for a name nobody has yet; returns who has it.
    pub fn add_name(&mut self, name: &str, provisional: bool) -> u32 {
        if let Some(id) = self.chars.iter().find(|c| c.names.iter().any(|n| n == name)).map(|c| c.id) {
            return id;
        }
        let id = self.next_id();
        self.chars.push(DramaCharacter::named(id, name, provisional));
        self.reindex();
        id
    }

    /// The narrator of a first-person book (“我说”), made the first time it's needed.
    pub fn narrator(&mut self) -> u32 {
        if let Some(c) = self.chars.iter().find(|c| c.narrator) {
            return c.id;
        }
        let id = self.next_id();
        let mut c = DramaCharacter::named(id, "我", false);
        c.narrator = true;
        self.chars.push(c);
        self.reindex();
        id
    }
}

impl DramaCharacter {
    pub fn named(id: u32, name: &str, provisional: bool) -> DramaCharacter {
        DramaCharacter {
            id,
            names: vec![name.to_string()],
            gender: guess_gender(name),
            age: CharacterAge::Unknown,
            traits: String::new(),
            lines: 0,
            first_section: None,
            provisional,
            narrator: false,
            revealed_in: None,
            same_as: None,
            shown: name.to_string(),
        }
    }

    /// The name to show when the user hasn't picked one.
    pub fn short_name(&self) -> &str {
        if let Some(jia) = self.names.iter().filter(|n| n.ends_with("家的")).max_by_key(|n| (n.chars().count(), std::cmp::Reverse(n.as_str()))) {
            return jia.as_str();
        }
        let chars = |n: &&String| n.chars().count();
        self.names.iter().filter(|n| chars(n) >= 2).min_by_key(|n| (chars(n), n.as_str())).or(self.names.first()).map(String::as_str).unwrap_or("")
    }

    /// The name shown first: the longest, which is the fullest.
    pub fn key(&self) -> &str {
        self.names.iter().max_by_key(|n| (n.chars().count(), std::cmp::Reverse(n.as_str()))).map(String::as_str).unwrap_or("")
    }
}

/// Mr. / Mrs. / Miss and the like; 太太, 姑娘 when they end a name. Unknown otherwise.
fn guess_gender(name: &str) -> CharacterGender {
    let n = name.trim();
    if ["Mr.", "Mr ", "Sir ", "Lord ", "Colonel ", "Captain ", "Father "].iter().any(|p| n.starts_with(p)) {
        return CharacterGender::M;
    }
    if ["Mrs.", "Mrs ", "Miss ", "Lady ", "Madam ", "Mother "].iter().any(|p| n.starts_with(p)) {
        return CharacterGender::F;
    }
    if ["太太", "姑娘", "奶奶", "夫人", "小姐", "姐姐", "妹妹", "丫头", "丫頭", "嬷嬷", "嬤嬤", "婆子", "姨妈", "姨媽", "嫂子"].iter().any(|s| n.ends_with(s)) {
        return CharacterGender::F;
    }
    if ["老爷", "老爺", "公子", "先生", "大爷", "大爺", "二爷", "二爺", "哥哥", "相公"].iter().any(|s| n.ends_with(s)) {
        return CharacterGender::M;
    }
    CharacterGender::Unknown
}

/// Common surnames, simplified and traditional: what a full name's extra first character must be.
/// Ones that are mostly little words in a novel (向, 和, 与) are left out on purpose.
const SURNAMES: &str = "王李张張刘劉陈陳杨楊黄黃赵趙吴吳周徐孙孫马馬朱胡郭何林罗羅高郑鄭梁谢謝宋唐许許韩韓冯馮邓鄧曹彭曾肖萧蕭田董袁潘于蒋蔣蔡余杜叶葉程苏蘇魏吕呂丁任沈姚卢盧姜崔钟鍾谭譚陆陸汪范金石廖贾賈夏韦韋付方白邹鄒孟熊秦邱江尹薛闫閆段雷侯龙龍史陶黎贺賀顾顧毛郝龚龔邵万萬钱錢严嚴覃武戴莫孔汤湯甄邢尤史薛柳岳";

/// Characters that stick to the end of a name in a speech tag without being part of it:
/// 寶玉勸道, 襲人等道.
const STUCK_AFTER: &str = "劝勸问問说說笑叹嘆歎骂罵哭喝叫答回道忙又便也等们們众眾";
/// The ones that make it a group: 襲人等, 丫头们.
pub const GROUP_AFTER: &str = "等们們众眾";

/// A rules name that's a word stuck to a real name: one character more than another name, that
/// character before it and no surname (指寶玉, 向黛玉), or after it and a verb or plural mark
/// (寶玉勸, 襲人等).
fn stuck_to_a_name(long: &str, names: &[&String]) -> bool {
    let n = long.chars().count();
    let han = |c: char| ('\u{4e00}'..='\u{9fff}').contains(&c);
    names.iter().any(|s| {
        let k = s.chars().count();
        if k < 2 || n != k + 1 {
            return false;
        }
        let first = long.chars().next().unwrap();
        let last = long.chars().last().unwrap();
        (long.ends_with(s.as_str()) && han(first) && !SURNAMES.contains(first)) || (long.starts_with(s.as_str()) && STUCK_AFTER.contains(last))
    })
}

/// The cast from the rules' names alone: a short name joins the one full name it ends, when no
/// other full name ends with it too.
/// 他女儿, 她母亲, 他乃: a pronoun and what follows it isn't anyone's name.
pub fn pronoun_led(n: &str) -> bool {
    n.chars().count() >= 2 && n.starts_with(['他', '她', '我', '你', '它'])
}

pub fn from_rules(names: &[String]) -> CastTable {
    let all: Vec<&String> = names.iter().collect();
    let mut sorted: Vec<&String> = names.iter().filter(|n| !KIN.contains(&n.as_str()) && !stuck_to_a_name(n, &all) && !pronoun_led(n)).collect();
    sorted.sort_by(|a, b| b.chars().count().cmp(&a.chars().count()).then(a.cmp(b)));
    sorted.dedup();
    let mut chars: Vec<DramaCharacter> = Vec::new();
    for n in sorted {
        let len = n.chars().count();
        let owners: Vec<usize> = chars.iter().enumerate().filter(|(_, c)| len >= 2 && c.names.iter().any(|x| x.chars().count() > len && x.ends_with(n.as_str()))).map(|(i, _)| i).collect();
        if owners.len() == 1 {
            chars[owners[0]].names.push(n.clone());
            continue;
        }
        let id = chars.len() as u32 + 1;
        chars.push(DramaCharacter::named(id, n, false));
    }
    CastTable::new(chars)
}

// ---------------------------------------------------------------------------------------------
// The model's reading of the whole book.

#[derive(Clone, Debug, Deserialize)]
pub struct ModelCast {
    pub characters: Vec<ModelChar>,
    #[serde(default)]
    pub merges: Vec<ModelMerge>,
    #[serde(default)]
    pub groups: Vec<ModelGroup>,
    #[serde(default)]
    pub narrator: Option<ModelNarrator>,
    #[serde(default)]
    pub readings: Vec<ModelReading>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelChar {
    pub key: String,
    pub names: Vec<ModelName>,
    #[serde(default)]
    pub called_by: Vec<ModelCalled>,
    #[serde(default)]
    pub gender: Option<String>,
    #[serde(default)]
    pub age: Option<String>,
    #[serde(default)]
    pub traits: Option<String>,
    #[serde(default = "yes")]
    pub speaks: bool,
    #[serde(default)]
    pub evidence: Vec<Evidence>,
    #[serde(default)]
    pub same_as: Option<String>,
    #[serde(default)]
    pub revealed: Option<u32>,
}

fn yes() -> bool {
    true
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelName {
    pub text: String,
    #[serde(default)]
    pub kind: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelCalled {
    pub who: String,
    #[serde(rename = "as")]
    pub as_: String,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct Evidence {
    pub section: u32,
    pub quote: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelMerge {
    pub a: String,
    pub b: String,
    pub evidence: Evidence,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelGroup {
    pub key: String,
    pub names: Vec<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct ModelNarrator {
    pub person: String,
    #[serde(default)]
    pub who: Option<String>,
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq)]
pub struct ModelReading {
    pub word: String,
    pub reading: String,
    #[serde(default)]
    pub evidence: Option<Evidence>,
}

/// Text with whitespace and punctuation taken out, so a quote matches whatever its punctuation.
pub fn bare(s: &str) -> String {
    s.chars().filter(|c| c.is_alphanumeric()).flat_map(|c| c.to_lowercase()).collect()
}

/// Where a model's quote really is in the book: its section if found there, any section
/// otherwise; None when it's nowhere.
pub fn find_quote(sections: &[String], ev: &Evidence) -> Option<u32> {
    let q = bare(&ev.quote);
    if q.chars().count() < 2 {
        return None;
    }
    if sections.get(ev.section as usize).is_some_and(|s| s.contains(&q)) {
        return Some(ev.section);
    }
    sections.iter().position(|s| s.contains(&q)).map(|i| i as u32)
}

/// What came of checking a model's cast.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct CastReport {
    pub kept: usize,
    pub dropped_chars: usize,
    pub dropped_names: usize,
    pub merges_kept: usize,
    /// Merges without proof: shown as “可能是同一个人”, made only by the user.
    pub maybe_same: Vec<(u32, u32)>,
    pub readings: Vec<ModelReading>,
}

/// The cast from the model's reading, checked against the book. `bare_sections` is each
/// section's text through `bare`. The rules' names the model left out stay, each its own row.
pub fn from_model(m: &ModelCast, bare_sections: &[String], rule_names: &[String]) -> (CastTable, CastReport) {
    let mut report = CastReport::default();
    let in_book = |name: &str| {
        let b = bare(name);
        !b.is_empty() && bare_sections.iter().any(|s| s.contains(&b))
    };
    let mut chars: Vec<DramaCharacter> = Vec::new();
    let mut key_to_id: HashMap<String, u32> = HashMap::new();
    for mc in &m.characters {
        if !mc.speaks {
            continue;
        }
        let proven = mc.evidence.iter().any(|e| find_quote(bare_sections, e).is_some());
        let names: Vec<String> = mc
            .names
            .iter()
            .map(|n| n.text.trim().to_string())
            .filter(|n| !n.is_empty() && !KIN.contains(&n.as_str()))
            .filter(|n| {
                let ok = in_book(n);
                if !ok {
                    report.dropped_names += 1;
                }
                ok
            })
            .collect();
        if !proven || names.is_empty() {
            report.dropped_chars += 1;
            continue;
        }
        let id = chars.len() as u32 + 1;
        let mut c = DramaCharacter::named(id, &names[0], false);
        for n in &names[1..] {
            if !c.names.contains(n) {
                c.names.push(n.clone());
            }
        }
        c.gender = match mc.gender.as_deref() {
            Some("m" | "M" | "male") => CharacterGender::M,
            Some("f" | "F" | "female") => CharacterGender::F,
            _ => guess_gender(&names[0]),
        };
        c.age = match mc.age.as_deref() {
            Some("child") => CharacterAge::Child,
            Some("teen") => CharacterAge::Teen,
            Some("young") => CharacterAge::Young,
            Some("middle") => CharacterAge::Middle,
            Some("old") => CharacterAge::Old,
            _ => CharacterAge::Unknown,
        };
        c.traits = mc.traits.clone().unwrap_or_default();
        c.revealed_in = mc.revealed;
        key_to_id.insert(mc.key.clone(), id);
        chars.push(c);
    }
    // Same person, revealed later: kept apart, the link noted for voices and the cast page.
    for mc in &m.characters {
        if let (Some(id), Some(other)) = (key_to_id.get(&mc.key), mc.same_as.as_ref().and_then(|k| key_to_id.get(k)))
            && let Some(c) = chars.iter_mut().find(|c| c.id == *id) {
                c.same_as = Some(*other);
            }
    }
    // Merges: only with a quote that's in the book and names both.
    let mut merged_into: HashMap<u32, u32> = HashMap::new();
    for mg in &m.merges {
        let (Some(&a), Some(&b)) = (key_to_id.get(&mg.a), key_to_id.get(&mg.b)) else { continue };
        let (a, b) = (root(&merged_into, a), root(&merged_into, b));
        if a == b {
            continue;
        }
        let proof = find_quote(bare_sections, &mg.evidence).is_some() && {
            let q = &mg.evidence.quote;
            let has = |id: u32| chars.iter().find(|c| c.id == id).is_some_and(|c| c.names.iter().any(|n| q.contains(n.as_str())));
            has(a) && has(b)
        };
        if proof {
            let moved: Vec<String> = chars.iter().find(|c| c.id == b).map(|c| c.names.clone()).unwrap_or_default();
            if let Some(ca) = chars.iter_mut().find(|c| c.id == a) {
                for n in moved {
                    if !ca.names.contains(&n) {
                        ca.names.push(n);
                    }
                }
            }
            merged_into.insert(b, a);
            report.merges_kept += 1;
        } else {
            report.maybe_same.push((a, b));
        }
    }
    chars.retain(|c| !merged_into.contains_key(&c.id));
    // The rules' names nobody claimed: through the same sure merges as without a model — a short
    // name joins the one full name it ends — else a row of their own.
    let all: Vec<&String> = rule_names.iter().collect();
    let mut left: Vec<&String> = rule_names.iter().filter(|n| !KIN.contains(&n.as_str()) && !stuck_to_a_name(n, &all) && !chars.iter().any(|c| c.names.iter().any(|x| x == *n))).collect();
    left.sort_by(|a, b| b.chars().count().cmp(&a.chars().count()).then(a.cmp(b)));
    left.dedup();
    for n in left {
        let len = n.chars().count();
        let owners: Vec<usize> = chars.iter().enumerate().filter(|(_, c)| len >= 2 && c.names.iter().any(|x| x.chars().count() > len && x.ends_with(n.as_str()))).map(|(i, _)| i).collect();
        if owners.len() == 1 {
            chars[owners[0]].names.push(n.clone());
            continue;
        }
        let id = chars.iter().map(|c| c.id + 1).max().unwrap_or(1);
        chars.push(DramaCharacter::named(id, n, false));
    }
    report.kept = chars.len();
    for r in &m.readings {
        if in_book(&r.word) {
            report.readings.push(r.clone());
        }
    }
    let mut t = CastTable::new(chars);
    for mc in &m.characters {
        let Some(&id) = key_to_id.get(&mc.key) else { continue };
        let id = root(&merged_into, id);
        for cb in &mc.called_by {
            if let Some(&by) = key_to_id.get(&cb.who) {
                t.called_by.push(CalledBy { by: root(&merged_into, by), as_: cb.as_.clone(), who: id });
            }
        }
    }
    (t, report)
}

fn root(m: &HashMap<u32, u32>, mut id: u32) -> u32 {
    while let Some(&p) = m.get(&id) {
        id = p;
    }
    id
}

/// Who calls whom what: (贾宝玉, 老祖宗) → 贾母.
#[derive(Clone, Debug, Serialize, Deserialize, PartialEq)]
pub struct CalledBy {
    pub by: u32,
    pub as_: String,
    pub who: u32,
}

/// A temporary cast (before the whole book's) joined into the real one: a temporary character
/// whose names meet a real one's is that one. Returns old id → new id.
pub fn reconcile(provisional: &CastTable, real: &CastTable) -> HashMap<u32, u32> {
    let mut out = HashMap::new();
    for c in &provisional.chars {
        let ids: HashSet<u32> = c.names.iter().filter_map(|n| real.resolve(n)).collect();
        if ids.len() == 1 {
            out.insert(c.id, ids.into_iter().next().unwrap());
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn names(v: &[&str]) -> Vec<String> {
        v.iter().map(|s| s.to_string()).collect()
    }

    #[test]
    fn a_short_name_joins_its_one_full_name() {
        let t = from_rules(&names(&["贾宝玉", "宝玉", "林黛玉", "黛玉", "凤姐"]));
        assert_eq!(t.chars.len(), 3);
        assert_eq!(t.resolve("宝玉"), t.resolve("贾宝玉"));
        assert!(t.resolve("凤姐").is_some());
    }

    #[test]
    fn two_full_names_keep_the_short_one_apart() {
        let t = from_rules(&names(&["贾宝玉", "甄宝玉", "宝玉"]));
        assert_eq!(t.chars.len(), 3);
        assert_ne!(t.resolve("贾宝玉"), t.resolve("甄宝玉"));
    }

    #[test]
    fn a_word_stuck_to_a_name_isnt_a_full_name() {
        let t = from_rules(&names(&["寶玉勸", "襲人等", "寶玉", "襲人"]));
        assert_eq!(t.chars.len(), 2, "{:?}", t.chars);
        assert_eq!(t.resolve("寶玉勸"), t.resolve("寶玉"));
        let t = from_rules(&names(&["向黛玉", "黛玉", "林黛玉"]));
        assert_eq!(t.chars.len(), 1, "{:?}", t.chars);
        let t = from_rules(&names(&["指寶玉", "寶玉", "賈寶玉"]));
        assert_eq!(t.chars.len(), 1);
        assert_eq!(t.chars[0].key(), "賈寶玉");
        assert!(t.resolve("指寶玉").is_none() || t.resolve("指寶玉") == t.resolve("寶玉"));
    }

    #[test]
    fn kin_words_are_never_names() {
        let t = from_rules(&names(&["母亲", "宝玉"]));
        assert_eq!(t.chars.len(), 1);
    }

    #[test]
    fn the_index_finds_names_longest_first() {
        let t = from_rules(&names(&["林黛玉", "黛玉", "宝玉"]));
        let h = t.find("林黛玉看宝玉");
        assert_eq!(h.len(), 2);
        assert_eq!((h[0].start, h[0].end), (0, 3));
        assert_eq!((h[1].start, h[1].end), (4, 6));
    }

    #[test]
    fn the_models_claims_need_quotes_from_the_book() {
        let book = vec![bare("却说凤姐笑道：我来迟了。"), bare("这熙凤听了，忙转悲为喜道：正是呢。王熙凤便是凤姐。")];
        let m: ModelCast = serde_json::from_value(serde_json::json!({
            "characters": [
                { "key": "c1", "names": [{"text": "王熙凤"}], "gender": "f", "evidence": [{"section": 1, "quote": "这熙凤听了，忙转悲为喜道"}] },
                { "key": "c2", "names": [{"text": "凤姐"}], "evidence": [{"section": 0, "quote": "凤姐笑道"}] },
                { "key": "c3", "names": [{"text": "张飞"}], "evidence": [{"section": 0, "quote": "燕人张飞在此"}] },
                { "key": "c4", "names": [{"text": "熙凤"}], "evidence": [{"section": 1, "quote": "这熙凤听了"}] }
            ],
            "merges": [
                { "a": "c1", "b": "c2", "evidence": { "section": 1, "quote": "王熙凤便是凤姐" } },
                { "a": "c1", "b": "c4", "evidence": { "section": 1, "quote": "忙转悲为喜道" } }
            ]
        }))
        .unwrap();
        let (t, r) = from_model(&m, &book, &[]);
        assert_eq!(r.dropped_chars, 1, "张飞 has no quote in the book");
        assert_eq!(r.merges_kept, 1);
        assert_eq!(r.maybe_same.len(), 1, "a quote without both names proves nothing");
        assert_eq!(t.resolve("凤姐"), t.resolve("王熙凤"));
        assert_eq!(t.get(t.resolve("凤姐").unwrap()).unwrap().gender, CharacterGender::F);
    }

    #[test]
    fn jia_de_is_shown_with_its_surname() {
        let t = from_rules(&names(&["周瑞家的", "瑞家的", "贾宝玉", "宝玉"]));
        let zhou = t.get(t.resolve("周瑞家的").unwrap()).unwrap();
        assert_eq!(zhou.short_name(), "周瑞家的");
        let baoyu = t.get(t.resolve("宝玉").unwrap()).unwrap();
        assert_eq!(baoyu.short_name(), "宝玉");
    }
}
